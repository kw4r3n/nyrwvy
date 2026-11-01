//! The session host's typed model (docs/decisions/zig-session-host.md, "In-memory layout"): stable
//! IDs interned per domain into `u32`s, display names and literals interned as UTF-16 units,
//! declaration records, and one struct-of-arrays graph per function body. A snapshot is an
//! immutable `Generation`. `u32` numbers never leave the process: every output writes ID texts.
const std = @import("std");
const reader = @import("main.zig");

const Value = reader.Value;
const Str = reader.Str;
const Allocator = std.mem.Allocator;
const Writer = std.Io.Writer;

pub const Domain = enum(u8) { snapshot, transaction, function, type, field, variant, parameter, type_parameter, node, binding, effect, capability, operation, capability_parameter, obligation };
pub const Id = u32;
pub const builder = @import("typed_builder.zig");
pub const dependencies = @import("typed_dependencies.zig");
/// An absent optional ID.
pub const none: Id = std.math.maxInt(u32);

const Utf16Context = struct {
    pub fn hash(_: Utf16Context, key: Str) u64 {
        return std.hash.Wyhash.hash(0, std.mem.sliceAsBytes(key));
    }
    pub fn eql(_: Utf16Context, x: Str, y: Str) bool {
        return std.mem.eql(u16, x, y);
    }
};

const Table = struct {
    map: std.HashMapUnmanaged(Str, u32, Utf16Context, 80) = .empty,
    texts: std.ArrayList(Str) = .empty,
    /// Removals since the last rehash; std leaves each as a tombstone (`size + available` stays fixed).
    tombstones: usize = 0,

    fn intern(self: *Table, gpa: Allocator, text: Str) !u32 {
        if (self.map.get(text)) |existing| return existing;
        try self.map.ensureUnusedCapacity(gpa, 1);
        try self.texts.ensureUnusedCapacity(gpa, 1);
        const owned = try gpa.dupe(u16, text);
        const handle: u32 = @intCast(self.texts.items.len);
        self.map.putAssumeCapacity(owned, handle);
        self.texts.appendAssumeCapacity(owned);
        return handle;
    }
};

/// The interned texts of one session.
// The host rebuilds this table from the live generations before commit/reject.
pub const Interner = struct {
    gpa: Allocator,
    ids: [std.enums.values(Domain).len]Table = @splat(.{}),
    strings: Table = .{},

    pub const Checkpoint = struct { ids: [std.enums.values(Domain).len]usize, strings: usize };

    pub fn checkpoint(self: *const Interner) Checkpoint {
        var result: Checkpoint = .{ .ids = undefined, .strings = self.strings.texts.items.len };
        for (self.ids, &result.ids) |table, *count| count.* = table.texts.items.len;
        return result;
    }

    fn truncate(self: *Interner, table: *Table, count: usize) void {
        while (table.texts.items.len > count) {
            const text = table.texts.pop().?;
            const removed = table.map.remove(text);
            std.debug.assert(removed);
            self.gpa.free(text);
            table.tombstones += 1;
        }
        // Tombstones are never reused as free slots, so lookups slow as they pile up; clear them past a quarter.
        if (table.tombstones > table.map.capacity() / 4) {
            table.map.rehash(Utf16Context{});
            table.tombstones = 0;
        }
    }

    pub fn restore(self: *Interner, previous: Checkpoint) void {
        for (&self.ids, previous.ids) |*table, count| self.truncate(table, count);
        self.truncate(&self.strings, previous.strings);
    }

    pub fn deinit(self: *Interner) void {
        for (&self.ids) |*table| {
            self.truncate(table, 0);
            table.texts.deinit(self.gpa);
            table.map.deinit(self.gpa);
        }
        self.truncate(&self.strings, 0);
        self.strings.texts.deinit(self.gpa);
        self.strings.map.deinit(self.gpa);
    }

    pub fn id(self: *Interner, domain: Domain, text: Str) !Id {
        return self.ids[@backingInt(domain)].intern(self.gpa, text);
    }

    pub fn findId(self: *const Interner, domain: Domain, text: Str) ?Id {
        return self.ids[@backingInt(domain)].map.get(text);
    }

    pub fn idText(self: *const Interner, domain: Domain, value: Id) Str {
        return self.ids[@backingInt(domain)].texts.items[value];
    }

    pub fn string(self: *Interner, text: Str) !u32 {
        return self.strings.intern(self.gpa, text);
    }

    pub fn stringText(self: *const Interner, handle: u32) Str {
        return self.strings.texts.items[handle];
    }

    /// F#'s order of IDs: their texts, ordinal UTF-16. Compared at sort time, so an ID created later sorts correctly.
    pub fn less(self: *const Interner, domain: Domain, x: Id, y: Id) bool {
        return std.mem.order(u16, self.idText(domain, x), self.idText(domain, y)) == .lt;
    }
};

pub const TypeRef = union(enum) {
    primitive: u32,
    named: struct { id: Id, arguments: ?[]const TypeRef },
    function: struct { parameters: []const TypeRef, result: *const TypeRef },
    type_parameter: Id,
    capability: Id,
    generic: struct { name: u32, arguments: []const TypeRef },
};

/// A field, parameter or capability parameter.
pub const Typed = struct { id: Id, name: u32, type: TypeRef };
pub const TypeParameter = struct { id: Id, name: u32 };
pub const Variant = struct { id: Id, name: u32, fields: []const Typed };
pub const Type = struct { id: Id, name: u32, exported: bool, type_parameters: ?[]const TypeParameter, product: bool, fields: []const Typed, variants: []const Variant };
pub const Effect = struct { id: Id, name: u32, exported: bool };
pub const Operation = struct { id: Id, name: u32, parameters: []const Typed, result: TypeRef, failure: ?TypeRef, effect: Id, may_wait: bool };
pub const Capability = struct { id: Id, name: u32, exported: bool, task_bound: bool, operations: []const Operation };
pub const Obligation = struct { id: Id, origin: Id, call: Id, parameter: Id, resolution: ?struct { transaction: Id, value: Id } };
pub const Function = struct { id: Id, name: u32, exported: bool, must_not_wait: bool, type_parameters: ?[]const TypeParameter, parameters: []const Typed, result: TypeRef, effects: []const Id, body: Body };

/// A graph's nodes are `first .. first + count` of the body's node arrays.
pub const Graph = struct { first: u32, count: u32, result: Id };
pub const Binding = struct { id: Id, name: ?u32, type: TypeRef };

/// A function body as struct-of-arrays: per node its kind (the line of model_kinds.txt), ID and
/// payload (`extra[start .. start + len]`, laid out by its kind's reads). Graph 0 is the body.
pub const Body = struct {
    kinds: []const u8,
    ids: []const Id,
    starts: []const u32,
    lens: []const u32,
    extra: []const u32,
    graphs: []const Graph,
    types: []const TypeRef,
    bindings: []const Binding,

    pub fn node(self: *const Body, index: usize) Node {
        return .{ .body = self, .index = index };
    }

    pub fn findNode(self: *const Body, id: Id) ?Node {
        for (self.ids, 0..) |candidate, index| if (candidate == id) return self.node(index);
        return null;
    }

    pub fn graph(self: *const Body, index: u32) GraphView {
        return .{ .body = self, .index = index };
    }

    pub fn depthFirst(self: *const Body, arena: Allocator) ![]const Node {
        var result: std.ArrayList(Node) = .empty;
        try self.graph(0).collect(arena, &result);
        return result.items;
    }
};

/// The dependency a resolved snapshot names.
pub const Marker = struct { snapshot: Str, digest: Str };

/// One immutable snapshot.
// Arena ownership is held by the session; a proposal retains its base and shares its unchanged records.
pub const Generation = struct {
    snapshot: Id,
    parent: Id,
    marker: ?Marker,
    types: []const *const Type,
    effects: []const *const Effect,
    capabilities: []const *const Capability,
    functions: []const *const Function,
    obligations: []const *const Obligation,

    pub fn findFunction(self: *const Generation, id: Id) ?*const Function {
        for (self.functions) |item| if (item.id == id) return item;
        return null;
    }
    pub fn findType(self: *const Generation, id: Id) ?*const Type {
        for (self.types) |item| if (item.id == id) return item;
        return null;
    }
    pub fn findEffect(self: *const Generation, id: Id) ?*const Effect {
        for (self.effects) |item| if (item.id == id) return item;
        return null;
    }
    pub fn findCapability(self: *const Generation, id: Id) ?*const Capability {
        for (self.capabilities) |item| if (item.id == id) return item;
        return null;
    }
    pub fn findObligation(self: *const Generation, id: Id) ?*const Obligation {
        for (self.obligations) |item| if (item.id == id) return item;
        return null;
    }
};

// ---------------------------------------------------------------- node kinds

pub const Shape = enum { id, ids, type, targs, graph, varm, ctx, args, capargs, fields, lit, bool, int, bits, mode, optid, impls, arms, errarm };
pub const Read = struct { name: []const u8, shape: Shape, domain: ?Domain };
pub const Kind = struct { name: []const u8, reads: []const Read, writes: []const u8 };

var kinds: ?[]const Kind = null;

/// The domain of an ID property, by its name; every ID property of model_kinds.txt has one.
fn domainOf(name: []const u8) ?Domain {
    const ends = std.mem.endsWith;
    // `arguments` is a node list only where it is ID-shaped (FunctionInvoke).
    if (ends(u8, name, "NodeId") or ends(u8, name, "NodeIds") or std.mem.eql(u8, name, "arguments")) return .node;
    if (ends(u8, name, "FunctionId")) return .function;
    if (std.mem.indexOf(u8, name, "CapabilityParameter") != null or std.mem.indexOf(u8, name, "capabilityParameter") != null) return .capability_parameter;
    if (ends(u8, name, "OperationId") or std.mem.eql(u8, name, "operationId")) return .operation;
    if (std.mem.eql(u8, name, "capabilityId")) return .capability;
    if (std.mem.eql(u8, name, "typeId")) return .type;
    if (std.mem.eql(u8, name, "fieldId")) return .field;
    if (std.mem.eql(u8, name, "variantId") or std.mem.eql(u8, name, "variantIds")) return .variant;
    if (ends(u8, name, "arameterId")) return .parameter;
    if (std.mem.eql(u8, name, "patternBindingId")) return .binding;
    return null;
}

pub fn kindTable() []const Kind {
    if (kinds) |table| return table;
    const gpa = std.heap.page_allocator;
    var out: std.ArrayList(Kind) = .empty;
    var lines = std.mem.tokenizeScalar(u8, @embedFile("model_kinds.txt"), '\n');
    while (lines.next()) |line| {
        var parts = std.mem.splitScalar(u8, line, '|');
        const name = parts.next().?;
        _ = parts.next();
        var reads: std.ArrayList(Read) = .empty;
        var items = std.mem.tokenizeScalar(u8, parts.next() orelse "", ' ');
        while (items.next()) |item| {
            const colon = std.mem.indexOfScalar(u8, item, ':').?;
            const shape = std.meta.stringToEnum(Shape, item[colon + 1 ..]).?;
            const property = item[0..colon];
            const domain = switch (shape) {
                .id, .ids, .optid => domainOf(property) orelse std.debug.panic("model_kinds.txt: no ID domain for '{s}'", .{property}),
                else => null,
            };
            reads.append(gpa, .{ .name = property, .shape = shape, .domain = domain }) catch @panic("out of memory");
        }
        var writes: std.ArrayList(u8) = .empty;
        if (parts.next()) |write_order| {
            var names = std.mem.tokenizeScalar(u8, write_order, ' ');
            while (names.next()) |property| {
                for (reads.items, 0..) |read, index| if (std.mem.eql(u8, read.name, property)) {
                    writes.append(gpa, @intCast(index)) catch @panic("out of memory");
                    break;
                } else {};
            }
        } else for (reads.items, 0..) |_, index| writes.append(gpa, @intCast(index)) catch @panic("out of memory");
        std.debug.assert(writes.items.len == reads.items.len);
        out.append(gpa, .{ .name = name, .reads = reads.items, .writes = writes.items }) catch @panic("out of memory");
    }
    kinds = out.items;
    return out.items;
}

fn kindIndex(name: Str) u8 {
    for (kindTable(), 0..) |kind, index| {
        if (kind.name.len == name.len and for (kind.name, name) |c, unit| {
            if (c != unit) break false;
        } else true) return @intCast(index);
    }
    std.debug.panic("unknown node kind", .{});
}

/// Borrowed graph/node views refer directly to the immutable body's arrays.
pub const GraphView = struct {
    body: *const Body,
    index: u32,

    pub fn result(self: GraphView) Id {
        return self.body.graphs[self.index].result;
    }
    fn collect(self: GraphView, arena: Allocator, result_nodes: *std.ArrayList(Node)) anyerror!void {
        var iterator = self.nodes();
        while (iterator.next()) |node| {
            try result_nodes.append(arena, node);
            var fields = node.payload();
            // MatchResult's stored payload has err before ok; lexical traversal is ok then err.
            var error_body: ?GraphView = null;
            while (fields.next()) |field| switch (field.value) {
                .graph => |graph| try graph.collect(arena, result_nodes),
                .varm => |arm| try arm.body.collect(arena, result_nodes),
                .errarm => |arm| error_body = arm.body,
                .arms => |arms| {
                    var union_arms = arms.iterator();
                    while (union_arms.next()) |arm| try arm.body.collect(arena, result_nodes);
                },
                else => {},
            };
            if (error_body) |graph| try graph.collect(arena, result_nodes);
        }
    }
    pub fn nodes(self: GraphView) NodeIterator {
        const g = self.body.graphs[self.index];
        return .{ .body = self.body, .at = g.first, .end = g.first + g.count };
    }
};

pub const NodeIterator = struct {
    body: *const Body,
    at: usize,
    end: usize,
    pub fn next(self: *NodeIterator) ?Node {
        if (self.at == self.end) return null;
        defer self.at += 1;
        return self.body.node(self.at);
    }
};

pub const Node = struct {
    body: *const Body,
    index: usize,
    pub fn id(self: Node) Id {
        return self.body.ids[self.index];
    }
    pub fn kind(self: Node) []const u8 {
        return kindTable()[self.body.kinds[self.index]].name;
    }
    pub fn payload(self: Node) PayloadIterator {
        const start = self.body.starts[self.index];
        return .{ .body = self.body, .reads = kindTable()[self.body.kinds[self.index]].reads, .words = self.body.extra[start .. start + self.body.lens[self.index]] };
    }
    pub fn stringLiteral(self: Node) ?u32 {
        if (!std.mem.eql(u8, self.kind(), "stringLiteral")) return null;
        return self.body.extra[self.body.starts[self.index]];
    }
};

pub const Target = struct { target: Id, value: Id };
pub const Targets = struct {
    words: []const u32,
    pub fn len(self: Targets) usize {
        return self.words.len / 2;
    }
    pub fn at(self: Targets, index: usize) Target {
        return .{ .target = self.words[index * 2], .value = self.words[index * 2 + 1] };
    }
};
pub const TypeArguments = struct {
    body: *const Body,
    indices: []const u32,
    pub fn len(self: TypeArguments) usize {
        return self.indices.len;
    }
    pub fn at(self: TypeArguments, index: usize) TypeRef {
        return self.body.types[self.indices[index]];
    }
};
pub const Implementation = struct { operation: Id, function: Id };
pub const Implementations = struct {
    words: []const u32,
    pub fn len(self: Implementations) usize {
        return self.words.len / 2;
    }
    pub fn at(self: Implementations, index: usize) Implementation {
        return .{ .operation = self.words[index * 2], .function = self.words[index * 2 + 1] };
    }
};
pub const FieldBinding = struct { source: Id, binding: *const Binding };
pub const FieldBindings = struct {
    body: *const Body,
    words: []const u32,
    pub fn len(self: FieldBindings) usize {
        return self.words.len / 2;
    }
    pub fn at(self: FieldBindings, index: usize) FieldBinding {
        return .{ .source = self.words[index * 2], .binding = &self.body.bindings[self.words[index * 2 + 1]] };
    }
};
pub const BindingArm = struct { binding: *const Binding, body: GraphView };
pub const UnionArm = struct { variants: []const Id, fields: FieldBindings, body: GraphView };
pub const UnionArms = struct {
    body: *const Body,
    words: []const u32,
    count: usize,
    pub fn len(self: UnionArms) usize {
        return self.count;
    }
    pub fn iterator(self: UnionArms) UnionArmIterator {
        return .{ .body = self.body, .words = self.words, .remaining = self.count };
    }
};
pub const UnionArmIterator = struct {
    body: *const Body,
    words: []const u32,
    remaining: usize,
    pub fn next(self: *UnionArmIterator) ?UnionArm {
        if (self.remaining == 0) return null;
        const variants = self.words[1 .. 1 + self.words[0]];
        const fields_start = 1 + variants.len;
        const fields_end = fields_start + 1 + self.words[fields_start] * 2;
        const result: UnionArm = .{ .variants = variants, .fields = .{ .body = self.body, .words = self.words[fields_start + 1 .. fields_end] }, .body = self.body.graph(self.words[fields_end]) };
        self.words = self.words[fields_end + 1 ..];
        self.remaining -= 1;
        return result;
    }
};
pub const WaitMode = enum { any, all };
pub const Payload = union(Shape) {
    id: Id,
    ids: []const Id,
    type: TypeRef,
    targs: TypeArguments,
    graph: GraphView,
    varm: BindingArm,
    ctx: Targets,
    args: Targets,
    capargs: Targets,
    fields: Targets,
    lit: u32,
    bool: bool,
    int: i64,
    bits: u64,
    mode: WaitMode,
    optid: ?Id,
    impls: Implementations,
    arms: UnionArms,
    errarm: BindingArm,
};
pub const PayloadField = struct { read: Read, value: Payload, offset: usize };
pub const PayloadIterator = struct {
    body: *const Body,
    reads: []const Read,
    words: []const u32,
    at: usize = 0,
    field: usize = 0,

    fn take(self: *PayloadIterator, count: usize) []const u32 {
        defer self.at += count;
        return self.words[self.at .. self.at + count];
    }
    fn counted(self: *PayloadIterator, width: usize) []const u32 {
        const count = self.take(1)[0];
        return self.take(count * width);
    }
    pub fn next(self: *PayloadIterator) ?PayloadField {
        if (self.field == self.reads.len) {
            std.debug.assert(self.at == self.words.len);
            return null;
        }
        const read = self.reads[self.field];
        self.field += 1;
        const offset = self.at;
        const value: Payload = switch (read.shape) {
            .id => .{ .id = self.take(1)[0] },
            .optid => blk: {
                const id = self.take(1)[0];
                break :blk .{ .optid = if (id == none) null else id };
            },
            .ids => .{ .ids = self.counted(1) },
            .type => .{ .type = self.body.types[self.take(1)[0]] },
            .targs => .{ .targs = .{ .body = self.body, .indices = self.counted(1) } },
            .graph => .{ .graph = self.body.graph(self.take(1)[0]) },
            .varm, .errarm => blk: {
                const pair = self.take(2);
                const arm: BindingArm = .{ .binding = &self.body.bindings[pair[0]], .body = self.body.graph(pair[1]) };
                break :blk if (read.shape == .varm) .{ .varm = arm } else .{ .errarm = arm };
            },
            .ctx => .{ .ctx = .{ .words = self.counted(2) } },
            .args => .{ .args = .{ .words = self.counted(2) } },
            .capargs => .{ .capargs = .{ .words = self.counted(2) } },
            .fields => .{ .fields = .{ .words = self.counted(2) } },
            .lit => .{ .lit = self.take(1)[0] },
            .bool => .{ .bool = self.take(1)[0] == 1 },
            .int => blk: {
                const pair = self.take(2);
                break :blk .{ .int = @bitCast(@as(u64, pair[0]) | (@as(u64, pair[1]) << 32)) };
            },
            .bits => blk: {
                const pair = self.take(2);
                break :blk .{ .bits = @as(u64, pair[0]) | (@as(u64, pair[1]) << 32) };
            },
            .mode => .{ .mode = if (self.take(1)[0] == 1) .all else .any },
            .impls => .{ .impls = .{ .words = self.counted(2) } },
            .arms => blk: {
                const count = self.take(1)[0];
                const start = self.at;
                for (0..count) |_| {
                    _ = self.counted(1);
                    _ = self.counted(2);
                    _ = self.take(1);
                }
                break :blk .{ .arms = .{ .body = self.body, .words = self.words[start..self.at], .count = count } };
            },
        };
        return .{ .read = read, .value = value, .offset = offset };
    }
};

// ---------------------------------------------------------------- loading

const Loader = struct {
    arena: Allocator,
    interner: *Interner,

    fn get(element: Value, name: []const u8) Value {
        return reader.property(element, name).?;
    }

    fn text(element: Value, name: []const u8) Str {
        return reader.decoded(get(element, name));
    }

    fn id(self: *Loader, domain: Domain, element: Value, name: []const u8) !Id {
        return self.interner.id(domain, text(element, name));
    }

    fn displayName(self: *Loader, element: Value) !u32 {
        return self.interner.string(text(element, "displayName"));
    }

    fn flag(element: Value, property: []const u8) bool {
        return reader.property(element, property) != null;
    }

    fn typeRef(self: *Loader, element: Value) anyerror!TypeRef {
        const kind = reader.utf8(text(element, "kind"));
        if (std.mem.eql(u8, kind, "primitive")) return .{ .primitive = try self.interner.string(text(element, "primitive")) };
        if (std.mem.eql(u8, kind, "named")) return .{ .named = .{ .id = try self.id(.type, element, "typeId"), .arguments = if (reader.property(element, "arguments") != null) try self.typeRefs(reader.arrayItems(element, "arguments")) else null } };
        if (std.mem.eql(u8, kind, "function")) {
            const result = try self.arena.create(TypeRef);
            result.* = try self.typeRef(get(element, "resultType"));
            return .{ .function = .{ .parameters = try self.typeRefs(reader.arrayItems(element, "parameters")), .result = result } };
        }
        if (std.mem.eql(u8, kind, "typeParameter")) return .{ .type_parameter = try self.id(.type_parameter, element, "typeParameterId") };
        if (std.mem.eql(u8, kind, "capability")) return .{ .capability = try self.id(.capability, element, "capabilityId") };
        return .{ .generic = .{ .name = try self.interner.string(text(element, "generic")), .arguments = try self.typeRefs(reader.arrayItems(element, "arguments")) } };
    }

    fn typeRefs(self: *Loader, items: []const Value) ![]const TypeRef {
        const out = try self.arena.alloc(TypeRef, items.len);
        for (items, out) |item, *t| t.* = try self.typeRef(item);
        return out;
    }

    fn typed(self: *Loader, domain: Domain, items: []const Value) ![]const Typed {
        const out = try self.arena.alloc(Typed, items.len);
        for (items, out) |item, *t| t.* = .{ .id = try self.id(domain, item, "id"), .name = try self.displayName(item), .type = try self.typeRef(get(item, "type")) };
        return out;
    }

    fn typeParameters(self: *Loader, element: Value) !?[]const TypeParameter {
        if (reader.property(element, "typeParameters") == null) return null;
        const items = reader.arrayItems(element, "typeParameters");
        const out = try self.arena.alloc(TypeParameter, items.len);
        for (items, out) |item, *t| t.* = .{ .id = try self.id(.type_parameter, item, "typeParameterId"), .name = try self.displayName(item) };
        return out;
    }

    fn records(self: *Loader, comptime T: type, items: []const Value, comptime loadOne: fn (*Loader, Value) anyerror!T) ![]const *const T {
        const out = try self.arena.alloc(*const T, items.len);
        for (items, out) |item, *slot| {
            const record = try self.arena.create(T);
            record.* = try loadOne(self, item);
            slot.* = record;
        }
        return out;
    }

    fn loadType(self: *Loader, item: Value) !Type {
        const product = std.mem.eql(u8, reader.utf8(text(item, "kind")), "product");
        var variants: []Variant = &.{};
        if (!product) {
            const items = reader.arrayItems(item, "variants");
            variants = try self.arena.alloc(Variant, items.len);
            for (items, variants) |variant, *v| v.* = .{ .id = try self.id(.variant, variant, "id"), .name = try self.displayName(variant), .fields = try self.typed(.field, reader.arrayItems(variant, "fields")) };
        }
        return .{ .id = try self.id(.type, item, "id"), .name = try self.displayName(item), .exported = flag(item, "exported"), .type_parameters = try self.typeParameters(item), .product = product, .fields = if (product) try self.typed(.field, reader.arrayItems(item, "fields")) else &.{}, .variants = variants };
    }

    fn loadEffect(self: *Loader, item: Value) !Effect {
        return .{ .id = try self.id(.effect, item, "id"), .name = try self.displayName(item), .exported = flag(item, "exported") };
    }

    fn loadCapability(self: *Loader, item: Value) !Capability {
        const items = reader.arrayItems(item, "operations");
        const operations = try self.arena.alloc(Operation, items.len);
        for (items, operations) |operation, *o| o.* = .{
            .id = try self.id(.operation, operation, "id"),
            .name = try self.displayName(operation),
            .parameters = try self.typed(.capability_parameter, reader.arrayItems(operation, "parameters")),
            .result = try self.typeRef(get(operation, "resultType")),
            .failure = if (reader.property(operation, "failureType")) |failure| try self.typeRef(failure) else null,
            .effect = try self.id(.effect, operation, "effectId"),
            .may_wait = flag(operation, "mayWait"),
        };
        return .{ .id = try self.id(.capability, item, "id"), .name = try self.displayName(item), .exported = flag(item, "exported"), .task_bound = flag(item, "taskBound"), .operations = operations };
    }

    fn loadObligation(self: *Loader, item: Value) !Obligation {
        const resolved = std.mem.eql(u8, reader.utf8(text(item, "status")), "resolved");
        return .{
            .id = try self.id(.obligation, item, "id"),
            .origin = try self.id(.transaction, item, "originTransactionId"),
            .call = try self.id(.node, item, "callNodeId"),
            .parameter = try self.id(.parameter, item, "parameterId"),
            .resolution = if (resolved) .{ .transaction = try self.id(.transaction, item, "resolutionTransactionId"), .value = try self.id(.node, item, "resolutionValueNodeId") } else null,
        };
    }

    fn loadFunction(self: *Loader, item: Value) !Function {
        return self.loadFunctionWithBody(item, null);
    }

    fn loadFunctionWithBody(self: *Loader, item: Value, shared_body: ?Body) !Function {
        const effects_values = reader.arrayItems(item, "requiredEffects");
        const effects = try self.arena.alloc(Id, effects_values.len);
        for (effects_values, effects) |effect, *e| e.* = try self.interner.id(.effect, reader.decoded(effect));
        const body = shared_body orelse blk: {
            var body_builder: BodyBuilder = .{ .loader = self };
            _ = try body_builder.graph(get(item, "body"));
            break :blk body_builder.done();
        };
        return .{
            .id = try self.id(.function, item, "id"),
            .name = try self.displayName(item),
            .exported = flag(item, "exported"),
            .must_not_wait = flag(item, "mustNotWait"),
            .type_parameters = try self.typeParameters(item),
            .parameters = try self.typed(.parameter, reader.arrayItems(item, "parameters")),
            .result = try self.typeRef(get(item, "resultType")),
            .effects = effects,
            .body = body,
        };
    }
};

const BodyBuilder = struct {
    loader: *Loader,
    kinds: std.ArrayList(u8) = .empty,
    ids: std.ArrayList(Id) = .empty,
    starts: std.ArrayList(u32) = .empty,
    lens: std.ArrayList(u32) = .empty,
    extra: std.ArrayList(u32) = .empty,
    graphs: std.ArrayList(Graph) = .empty,
    types: std.ArrayList(TypeRef) = .empty,
    bindings: std.ArrayList(Binding) = .empty,

    fn done(self: *BodyBuilder) Body {
        return .{ .kinds = self.kinds.items, .ids = self.ids.items, .starts = self.starts.items, .lens = self.lens.items, .extra = self.extra.items, .graphs = self.graphs.items, .types = self.types.items, .bindings = self.bindings.items };
    }

    fn push(self: *BodyBuilder, payload: *std.ArrayList(u32), value: u32) !void {
        try payload.append(self.loader.arena, value);
    }

    fn typeIndex(self: *BodyBuilder, element: Value) !u32 {
        try self.types.append(self.loader.arena, try self.loader.typeRef(element));
        return @intCast(self.types.items.len - 1);
    }

    fn binding(self: *BodyBuilder, element: Value) !u32 {
        const l = self.loader;
        try self.bindings.append(l.arena, .{
            .id = try l.id(.binding, element, "id"),
            .name = if (reader.property(element, "displayName") != null) try l.displayName(element) else null,
            .type = try l.typeRef(Loader.get(element, "type")),
        });
        return @intCast(self.bindings.items.len - 1);
    }

    fn targets(self: *BodyBuilder, payload: *std.ArrayList(u32), items: []const Value, target: []const u8, domain: Domain) !void {
        try self.push(payload, @intCast(items.len));
        for (items) |item| {
            try self.push(payload, try self.loader.id(domain, item, target));
            try self.push(payload, try self.loader.id(.node, item, "valueNodeId"));
        }
    }

    /// Appends a graph's nodes, then each node's payload (which appends nested graphs after them).
    fn graph(self: *BodyBuilder, element: Value) anyerror!u32 {
        const l = self.loader;
        const index: u32 = @intCast(self.graphs.items.len);
        const nodes = reader.arrayItems(element, "nodes");
        const first: u32 = @intCast(self.kinds.items.len);
        try self.graphs.append(l.arena, .{ .first = first, .count = @intCast(nodes.len), .result = try l.id(.node, element, "resultNodeId") });
        for (nodes) |node| {
            try self.kinds.append(l.arena, kindIndex(Loader.text(node, "kind")));
            try self.ids.append(l.arena, try l.id(.node, node, "id"));
            try self.starts.append(l.arena, 0);
            try self.lens.append(l.arena, 0);
        }
        for (nodes, first..) |node, at| {
            var payload: std.ArrayList(u32) = .empty;
            for (kindTable()[self.kinds.items[at]].reads) |read| try self.field(&payload, node, read);
            self.starts.items[at] = @intCast(self.extra.items.len);
            self.lens.items[at] = @intCast(payload.items.len);
            try self.extra.appendSlice(l.arena, payload.items);
        }
        return index;
    }

    fn field(self: *BodyBuilder, payload: *std.ArrayList(u32), node: Value, read: Read) !void {
        const l = self.loader;
        const value = reader.property(node, read.name);
        switch (read.shape) {
            .id => try self.push(payload, try l.id(read.domain.?, node, read.name)),
            .optid => try self.push(payload, if (value != null) try l.id(read.domain.?, node, read.name) else none),
            .ids => {
                const items = value.?.array;
                try self.push(payload, @intCast(items.len));
                for (items) |item| try self.push(payload, try l.interner.id(read.domain.?, reader.decoded(item)));
            },
            .type => try self.push(payload, try self.typeIndex(value.?)),
            .targs => {
                const items: []const Value = if (value) |v| v.array else &.{};
                try self.push(payload, @intCast(items.len));
                for (items) |item| try self.push(payload, try self.typeIndex(item));
            },
            .graph => try self.push(payload, try self.graph(value.?)),
            .varm, .errarm => {
                try self.push(payload, try self.binding(Loader.get(value.?, if (read.shape == .varm) "valueBinding" else "errorBinding")));
                try self.push(payload, try self.graph(Loader.get(value.?, "body")));
            },
            .ctx => try self.targets(payload, if (value) |v| v.array else &.{}, "targetParameterId", .parameter),
            .args => try self.targets(payload, value.?.array, "targetParameterId", .parameter),
            .capargs => try self.targets(payload, value.?.array, "targetCapabilityParameterId", .capability_parameter),
            .fields => try self.targets(payload, value.?.array, "targetFieldId", .field),
            .lit => try self.push(payload, try l.interner.string(value.?.string)),
            .bool => try self.push(payload, @intFromBool(value.? == .json_true)),
            .int => {
                const bits: u64 = @bitCast(try std.fmt.parseInt(i64, value.?.number, 10));
                try self.push(payload, @truncate(bits));
                try self.push(payload, @truncate(bits >> 32));
            },
            .bits => {
                const bits = try std.fmt.parseInt(u64, reader.utf8(reader.decoded(value.?)), 16);
                try self.push(payload, @truncate(bits));
                try self.push(payload, @truncate(bits >> 32));
            },
            .mode => try self.push(payload, @intFromBool(std.mem.eql(u8, reader.utf8(reader.decoded(value.?)), "all"))),
            .impls => {
                const items = value.?.array;
                try self.push(payload, @intCast(items.len));
                for (items) |item| {
                    try self.push(payload, try l.id(.operation, item, "operationId"));
                    try self.push(payload, try l.id(.function, item, "functionId"));
                }
            },
            .arms => {
                const items = value.?.array;
                try self.push(payload, @intCast(items.len));
                for (items) |arm| {
                    if (reader.property(arm, "variantId") != null) {
                        try self.push(payload, 1);
                        try self.push(payload, try l.id(.variant, arm, "variantId"));
                    } else {
                        const variants = reader.arrayItems(arm, "variantIds");
                        try self.push(payload, @intCast(variants.len));
                        for (variants) |variant| try self.push(payload, try l.interner.id(.variant, reader.decoded(variant)));
                    }
                    const bindings = reader.arrayItems(arm, "fieldBindings");
                    try self.push(payload, @intCast(bindings.len));
                    for (bindings) |item| {
                        try self.push(payload, try l.id(.field, item, "sourceFieldId"));
                        try self.push(payload, try self.binding(Loader.get(item, "binding")));
                    }
                    try self.push(payload, try self.graph(Loader.get(arm, "body")));
                }
            },
        }
    }
};

/// The generation of `root`, a snapshot in the model reader's canonical form, with `marker` when
/// it is a resolved snapshot's local part.
pub fn load(arena: Allocator, interner: *Interner, root: Value, marker: ?Marker) !Generation {
    var l: Loader = .{ .arena = arena, .interner = interner };
    return .{
        .snapshot = try l.id(.snapshot, root, "snapshotId"),
        .parent = if (reader.property(root, "parentSnapshotId") != null) try l.id(.snapshot, root, "parentSnapshotId") else none,
        .marker = marker,
        .types = try l.records(Type, reader.arrayItems(root, "types"), Loader.loadType),
        .effects = try l.records(Effect, reader.arrayItems(root, "effects"), Loader.loadEffect),
        .capabilities = try l.records(Capability, reader.arrayItems(root, "capabilities"), Loader.loadCapability),
        .functions = try l.records(Function, reader.arrayItems(root, "functions"), Loader.loadFunction),
        .obligations = try l.records(Obligation, reader.arrayItems(root, "obligations"), Loader.loadObligation),
    };
}

/// Rebuild live generations into a fresh arena/interner, preserving shared declarations/bodies.
/// The old generations remain untouched until the caller installs the completed live set.
pub const Compactor = struct {
    arena: Allocator,
    scratch: Allocator,
    old_interner: *const Interner,
    interner: *Interner,
    records: std.AutoHashMapUnmanaged(usize, usize) = .empty,
    bodies: std.AutoHashMapUnmanaged(usize, Body) = .empty,

    fn id(self: *Compactor, domain: Domain, value: Id) !Id {
        return if (value == none) none else self.interner.id(domain, self.old_interner.idText(domain, value));
    }

    fn string(self: *Compactor, value: u32) !u32 {
        return self.interner.string(self.old_interner.stringText(value));
    }

    fn typeRef(self: *Compactor, old: TypeRef) anyerror!TypeRef {
        return switch (old) {
            .primitive => |value| .{ .primitive = try self.string(value) },
            .named => |value| .{ .named = .{ .id = try self.id(.type, value.id), .arguments = if (value.arguments) |arguments| try self.typeRefs(arguments) else null } },
            .function => |value| blk: {
                const result = try self.arena.create(TypeRef);
                result.* = try self.typeRef(value.result.*);
                break :blk .{ .function = .{ .parameters = try self.typeRefs(value.parameters), .result = result } };
            },
            .type_parameter => |value| .{ .type_parameter = try self.id(.type_parameter, value) },
            .capability => |value| .{ .capability = try self.id(.capability, value) },
            .generic => |value| .{ .generic = .{ .name = try self.string(value.name), .arguments = try self.typeRefs(value.arguments) } },
        };
    }

    fn typeRefs(self: *Compactor, old: []const TypeRef) ![]const TypeRef {
        const result = try self.arena.alloc(TypeRef, old.len);
        for (old, result) |value, *slot| slot.* = try self.typeRef(value);
        return result;
    }

    fn typed(self: *Compactor, domain: Domain, old: []const Typed) ![]const Typed {
        const result = try self.arena.alloc(Typed, old.len);
        for (old, result) |value, *slot| slot.* = .{ .id = try self.id(domain, value.id), .name = try self.string(value.name), .type = try self.typeRef(value.type) };
        return result;
    }

    fn typeParameters(self: *Compactor, old: ?[]const TypeParameter) !?[]const TypeParameter {
        const values = old orelse return null;
        const result = try self.arena.alloc(TypeParameter, values.len);
        for (values, result) |value, *slot| slot.* = .{ .id = try self.id(.type_parameter, value.id), .name = try self.string(value.name) };
        return result;
    }

    fn ids(self: *Compactor, domain: Domain, old: []const Id) ![]const Id {
        const result = try self.arena.alloc(Id, old.len);
        for (old, result) |value, *slot| slot.* = try self.id(domain, value);
        return result;
    }

    fn binding(self: *Compactor, old: Binding) !Binding {
        return .{ .id = try self.id(.binding, old.id), .name = if (old.name) |value| try self.string(value) else null, .type = try self.typeRef(old.type) };
    }

    // All slices passed here are borrowed from the old body's extra array.
    fn remapWords(self: *Compactor, extra: []u32, old_extra: []const u32, words: []const u32, domain: Domain, stride: usize, member: usize) !void {
        if (words.len == 0) return;
        const offset = (@intFromPtr(words.ptr) - @intFromPtr(old_extra.ptr)) / @sizeOf(u32);
        var index = member;
        while (index < words.len) : (index += stride) extra[offset + index] = try self.id(domain, words[index]);
    }

    fn body(self: *Compactor, old: *const Body) !Body {
        const key = @intFromPtr(old.graphs.ptr);
        if (self.bodies.get(key)) |existing| return existing;
        const extra = try self.arena.dupe(u32, old.extra);
        const graphs = try self.arena.dupe(Graph, old.graphs);
        for (graphs) |*graph| graph.result = try self.id(.node, graph.result);
        const bindings = try self.arena.alloc(Binding, old.bindings.len);
        for (old.bindings, bindings) |value, *slot| slot.* = try self.binding(value);
        for (old.ids, 0..) |_, index| {
            var payload = old.node(index).payload();
            while (payload.next()) |field| {
                const at = old.starts[index] + field.offset;
                switch (field.value) {
                    .id, .optid => extra[at] = try self.id(field.read.domain.?, old.extra[at]),
                    .ids => |values| try self.remapWords(extra, old.extra, values, field.read.domain.?, 1, 0),
                    .lit => |value| extra[at] = try self.string(value),
                    .ctx, .args, .capargs, .fields => |values| {
                        const domain: Domain = switch (field.read.shape) {
                            .capargs => .capability_parameter,
                            .fields => .field,
                            else => .parameter,
                        };
                        try self.remapWords(extra, old.extra, values.words, domain, 2, 0);
                        try self.remapWords(extra, old.extra, values.words, .node, 2, 1);
                    },
                    .impls => |values| {
                        try self.remapWords(extra, old.extra, values.words, .operation, 2, 0);
                        try self.remapWords(extra, old.extra, values.words, .function, 2, 1);
                    },
                    .arms => |arms| {
                        var iterator = arms.iterator();
                        while (iterator.next()) |arm| {
                            try self.remapWords(extra, old.extra, arm.variants, .variant, 1, 0);
                            try self.remapWords(extra, old.extra, arm.fields.words, .field, 2, 0);
                        }
                    },
                    else => {},
                }
            }
        }
        const result: Body = .{
            .kinds = try self.arena.dupe(u8, old.kinds),
            .ids = try self.ids(.node, old.ids),
            .starts = try self.arena.dupe(u32, old.starts),
            .lens = try self.arena.dupe(u32, old.lens),
            .extra = extra,
            .graphs = graphs,
            .types = try self.typeRefs(old.types),
            .bindings = bindings,
        };
        try self.bodies.put(self.scratch, key, result);
        return result;
    }

    fn record(self: *Compactor, comptime T: type, old: *const T) !T {
        if (T == Function) return .{
            .id = try self.id(.function, old.id),
            .name = try self.string(old.name),
            .exported = old.exported,
            .must_not_wait = old.must_not_wait,
            .type_parameters = try self.typeParameters(old.type_parameters),
            .parameters = try self.typed(.parameter, old.parameters),
            .result = try self.typeRef(old.result),
            .effects = try self.ids(.effect, old.effects),
            .body = try self.body(&old.body),
        };
        if (T == Type) {
            const variants = try self.arena.alloc(Variant, old.variants.len);
            for (old.variants, variants) |value, *slot| slot.* = .{ .id = try self.id(.variant, value.id), .name = try self.string(value.name), .fields = try self.typed(.field, value.fields) };
            return .{ .id = try self.id(.type, old.id), .name = try self.string(old.name), .exported = old.exported, .type_parameters = try self.typeParameters(old.type_parameters), .product = old.product, .fields = try self.typed(.field, old.fields), .variants = variants };
        }
        if (T == Effect) return .{ .id = try self.id(.effect, old.id), .name = try self.string(old.name), .exported = old.exported };
        if (T == Capability) {
            const operations = try self.arena.alloc(Operation, old.operations.len);
            for (old.operations, operations) |value, *slot| slot.* = .{ .id = try self.id(.operation, value.id), .name = try self.string(value.name), .parameters = try self.typed(.capability_parameter, value.parameters), .result = try self.typeRef(value.result), .failure = if (value.failure) |failure| try self.typeRef(failure) else null, .effect = try self.id(.effect, value.effect), .may_wait = value.may_wait };
            return .{ .id = try self.id(.capability, old.id), .name = try self.string(old.name), .exported = old.exported, .task_bound = old.task_bound, .operations = operations };
        }
        if (T == Obligation) return .{ .id = try self.id(.obligation, old.id), .origin = try self.id(.transaction, old.origin), .call = try self.id(.node, old.call), .parameter = try self.id(.parameter, old.parameter), .resolution = if (old.resolution) |value| .{ .transaction = try self.id(.transaction, value.transaction), .value = try self.id(.node, value.value) } else null };
        unreachable;
    }

    pub fn cloneRecord(self: *Compactor, comptime T: type, original: *const T) !*const T {
        if (self.records.get(@intFromPtr(original))) |existing| return @ptrFromInt(existing);
        const remapped = try self.arena.create(T);
        remapped.* = try self.record(T, original);
        try self.records.put(self.scratch, @intFromPtr(original), @intFromPtr(remapped));
        return remapped;
    }

    pub fn generation(self: *Compactor, old: *const Generation) !Generation {
        var result: Generation = undefined;
        result.snapshot = try self.id(.snapshot, old.snapshot);
        result.parent = try self.id(.snapshot, old.parent);
        result.marker = if (old.marker) |marker| .{ .snapshot = try self.arena.dupe(u16, marker.snapshot), .digest = try self.arena.dupe(u16, marker.digest) } else null;
        inline for (.{ .{ "functions", Function }, .{ "types", Type }, .{ "effects", Effect }, .{ "capabilities", Capability }, .{ "obligations", Obligation } }) |entry| {
            const old_records = @field(old, entry[0]);
            const records = try self.arena.alloc(*const entry[1], old_records.len);
            for (old_records, records) |original, *slot| {
                slot.* = try self.cloneRecord(entry[1], original);
            }
            @field(result, entry[0]) = records;
        }
        return result;
    }
};

/// Path-copy the function table; all unchanged declarations and bodies remain immutable/shared.
/// The caller keeps the base generation's arena alive for this generation's lifetime.
pub fn withFunction(arena: Allocator, interner: *Interner, base: *const Generation, snapshot: Str, item: Value) !Generation {
    var l: Loader = .{ .arena = arena, .interner = interner };
    const created = try arena.create(Function);
    created.* = try l.loadFunction(item);
    const replacing = for (base.functions) |f| {
        if (f.id == created.id) break true;
    } else false;
    const functions = try arena.alloc(*const Function, base.functions.len + @as(usize, if (replacing) 0 else 1));
    @memcpy(functions[0..base.functions.len], base.functions);
    if (replacing) {
        for (functions) |*f| if (f.*.id == created.id) {
            f.* = created;
            break;
        };
    } else functions[base.functions.len] = created;
    const Order = struct {
        fn less(i: *const Interner, x: *const Function, y: *const Function) bool {
            return i.less(.function, x.id, y.id);
        }
    };
    std.mem.sort(*const Function, functions, @as(*const Interner, interner), Order.less);
    var result = base.*;
    result.snapshot = try interner.id(.snapshot, snapshot);
    result.parent = base.snapshot;
    result.functions = functions;
    return result;
}

/// Append declarations while sharing every unchanged record with the base generation.
pub fn withDeclarations(arena: Allocator, interner: *Interner, base: *const Generation, snapshot: Str, group: []const u8, items: []const Value) !Generation {
    var l: Loader = .{ .arena = arena, .interner = interner };
    var result = base.*;
    result.snapshot = try interner.id(.snapshot, snapshot);
    result.parent = base.snapshot;
    inline for (.{ .{ "functions", Function, Loader.loadFunction, Domain.function }, .{ "types", Type, Loader.loadType, Domain.type }, .{ "effects", Effect, Loader.loadEffect, Domain.effect }, .{ "capabilities", Capability, Loader.loadCapability, Domain.capability } }) |entry| {
        if (std.mem.eql(u8, group, entry[0])) {
            const old = @field(base, entry[0]);
            const records = try arena.alloc(*const entry[1], old.len + items.len);
            @memcpy(records[0..old.len], old);
            const added = try l.records(entry[1], items, entry[2]);
            @memcpy(records[old.len..], added);
            const Order = struct {
                fn less(i: *const Interner, x: *const entry[1], y: *const entry[1]) bool {
                    return i.less(entry[3], x.id, y.id);
                }
            };
            std.mem.sort(*const entry[1], records, @as(*const Interner, interner), Order.less);
            @field(result, entry[0]) = records;
            return result;
        }
    }
    unreachable;
}

/// Apply a path-copied candidate, preserving unchanged records and function bodies. `before`
/// and `after` share the original Value slices; the caller retains the base generation's arena.
/// This supports additions, replacements and removals in every declaration/obligation table.
pub fn withChanges(arena: Allocator, interner: *Interner, base: *const Generation, before: Value, after: Value) !Generation {
    var l: Loader = .{ .arena = arena, .interner = interner };
    var result = base.*;
    result.snapshot = try l.id(.snapshot, after, "snapshotId");
    result.parent = base.snapshot;
    inline for (.{ .{ "functions", Function, Loader.loadFunction, Domain.function }, .{ "types", Type, Loader.loadType, Domain.type }, .{ "effects", Effect, Loader.loadEffect, Domain.effect }, .{ "capabilities", Capability, Loader.loadCapability, Domain.capability }, .{ "obligations", Obligation, Loader.loadObligation, Domain.obligation } }) |entry| {
        table: {
            const old_items = reader.arrayItems(before, entry[0]);
            const new_items = reader.arrayItems(after, entry[0]);
            if (sameSlice(Value, old_items, new_items)) break :table;
            const old_records = @field(base, entry[0]);
            const records = try arena.alloc(*const entry[1], new_items.len);
            for (new_items, records) |item, *slot| {
                const id = try l.id(entry[3], item, "id");
                const old_record = for (old_records) |record| {
                    if (record.id == id) break record;
                } else null;
                const old_item = for (old_items) |old| {
                    if (std.mem.eql(u16, reader.decoded(reader.property(old, "id").?), reader.decoded(reader.property(item, "id").?))) break old;
                } else null;
                if (old_record != null and old_item != null and sameValue(old_item.?, item)) {
                    slot.* = old_record.?;
                    continue;
                }
                const record = try arena.create(entry[1]);
                if (entry[1] == Function and old_record != null and old_item != null) {
                    record.* = try changedFunction(&l, old_record.?, old_item.?, item);
                } else record.* = try entry[2](&l, item);
                slot.* = record;
            }
            const Order = struct {
                fn less(i: *const Interner, x: *const entry[1], y: *const entry[1]) bool {
                    return i.less(entry[3], x.id, y.id);
                }
            };
            std.mem.sort(*const entry[1], records, @as(*const Interner, interner), Order.less);
            @field(result, entry[0]) = records;
        }
    }
    return result;
}

fn sameSlice(comptime T: type, x: []const T, y: []const T) bool {
    return x.ptr == y.ptr and x.len == y.len;
}

fn sameValue(x: Value, y: Value) bool {
    return switch (x) {
        .object => |fields| y == .object and sameSlice(reader.Member, fields, y.object),
        .array => |items| y == .array and sameSlice(Value, items, y.array),
        else => false,
    };
}

fn changedFunction(l: *Loader, original: *const Function, before: Value, after: Value) !Function {
    var result = original.*;
    result.name = try l.displayName(after);
    result.exported = Loader.flag(after, "exported");
    result.must_not_wait = Loader.flag(after, "mustNotWait");
    inline for (.{ "typeParameters", "parameters", "resultType", "requiredEffects", "body" }) |key| {
        property: {
            const old = reader.property(before, key);
            const new = reader.property(after, key);
            if (old != null and new != null and sameValue(old.?, new.?)) break :property;
            if (comptime std.mem.eql(u8, key, "typeParameters")) {
                result.type_parameters = try l.typeParameters(after);
            } else if (comptime std.mem.eql(u8, key, "parameters")) {
                result.parameters = try l.typed(.parameter, reader.arrayItems(after, key));
            } else if (comptime std.mem.eql(u8, key, "resultType")) {
                result.result = try l.typeRef(new.?);
            } else if (comptime std.mem.eql(u8, key, "requiredEffects")) {
                const values = reader.arrayItems(after, key);
                const effects = try l.arena.alloc(Id, values.len);
                for (values, effects) |effect, *e| e.* = try l.interner.id(.effect, reader.decoded(effect));
                result.effects = effects;
            } else {
                var body_builder: BodyBuilder = .{ .loader = l };
                _ = try body_builder.graph(new.?);
                result.body = body_builder.done();
            }
        }
    }
    return result;
}

// ---------------------------------------------------------------- writing

pub const writeDocument = @import("typed_writer.zig").writeDocument;

test "operation generations path-copy changed tables and share unchanged function bodies" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const before = try reader.parseValue(a,
        \\{"snapshotId":"base","types":[{"id":"t","displayName":"T","kind":"product","fields":[]}],"effects":[{"id":"e","displayName":"E"}],"capabilities":[],"functions":[{"id":"f","displayName":"F","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"nodes":[{"id":"n","kind":"unitLiteral"}],"resultNodeId":"n"}},{"id":"g","displayName":"G","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"nodes":[{"id":"m","kind":"unitLiteral"}],"resultNodeId":"m"}}],"obligations":[]}
    );
    var interner: Interner = .{ .gpa = a };
    const original = try load(a, &interner, before, null);
    const functions = reader.arrayItems(before, "functions");
    const renamed_fields = try a.dupe(reader.Member, functions[0].object);
    for (renamed_fields) |*field| if (std.mem.eql(u8, reader.utf8(field.name), "displayName")) {
        field.value = .{ .string = try std.unicode.utf8ToUtf16LeAlloc(a, "Renamed") };
        break;
    };
    const root_fields = try a.dupe(reader.Member, before.object);
    for (root_fields) |*field| {
        const key = reader.utf8(field.name);
        if (std.mem.eql(u8, key, "snapshotId")) field.value = .{ .string = try std.unicode.utf8ToUtf16LeAlloc(a, "next") };
        if (std.mem.eql(u8, key, "functions")) field.value = .{ .array = try a.dupe(Value, &.{ .{ .object = renamed_fields }, functions[1] }) };
        if (std.mem.eql(u8, key, "effects")) field.value = .{ .array = &.{} };
    }
    const next = try withChanges(a, &interner, &original, before, .{ .object = root_fields });
    try std.testing.expect(next.types.ptr == original.types.ptr);
    try std.testing.expect(next.functions.ptr != original.functions.ptr);
    try std.testing.expect(next.functions[0] != original.functions[0]);
    try std.testing.expect(next.functions[1] == original.functions[1]);
    try std.testing.expect(next.functions[0].body.ids.ptr == original.functions[0].body.ids.ptr);
    try std.testing.expect(next.functions[0].parameters.ptr == original.functions[0].parameters.ptr);
    try std.testing.expectEqual(@as(usize, 0), next.effects.len);
    try std.testing.expectEqual(@as(usize, 1), original.effects.len);
    try std.testing.expect(std.mem.eql(u16, interner.stringText(original.functions[0].name), &.{'F'}));
    try std.testing.expectEqual(original.snapshot, next.parent);
    // A body replacement owns its own node arrays while the base body remains unchanged.
    const body_fields = try a.dupe(reader.Member, functions[0].object);
    for (body_fields) |*field| if (std.mem.eql(u8, reader.utf8(field.name), "body")) {
        field.value = try reader.parseValue(a, "{\"nodes\":[{\"id\":\"replacement\",\"kind\":\"unitLiteral\"}],\"resultNodeId\":\"replacement\"}");
        break;
    };
    const changed_root = try a.dupe(reader.Member, before.object);
    for (changed_root) |*field| if (std.mem.eql(u8, reader.utf8(field.name), "functions")) {
        field.value = .{ .array = try a.dupe(Value, &.{.{ .object = body_fields }}) };
        break;
    };
    const replacement = try withChanges(a, &interner, &original, before, .{ .object = changed_root });
    try std.testing.expectEqual(@as(usize, 1), replacement.functions.len);
    try std.testing.expect(replacement.functions[0].body.ids.ptr != original.functions[0].body.ids.ptr);
    try std.testing.expect(std.mem.eql(u16, interner.idText(.node, original.functions[0].body.ids[0]), &.{'n'}));
}

test "live-set compaction remaps handles and preserves shared declarations and bodies" {
    var original_arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer original_arena.deinit();
    const a = original_arena.allocator();
    const root = try reader.parseValue(a,
        \\{"snapshotId":"base","types":[],"effects":[],"capabilities":[],"functions":[{"id":"f","displayName":"F","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"nodes":[{"id":"n","kind":"unitLiteral"}],"resultNodeId":"n"}},{"id":"g","displayName":"G","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"nodes":[{"id":"m","kind":"unitLiteral"}],"resultNodeId":"m"}}],"obligations":[]}
    );
    var old: Interner = .{ .gpa = a };
    _ = try old.string(&.{ 'd', 'e', 'a', 'd' });
    const before = try load(a, &old, root, null);
    const functions = reader.arrayItems(root, "functions");
    const renamed = try a.dupe(reader.Member, functions[0].object);
    for (renamed) |*field| if (std.mem.eql(u8, reader.utf8(field.name), "displayName")) {
        field.value = .{ .string = &.{ 'N', 'e', 'w' } };
    };
    const fields = try a.dupe(reader.Member, root.object);
    for (fields) |*field| if (std.mem.eql(u8, reader.utf8(field.name), "functions")) {
        field.value = .{ .array = try a.dupe(Value, &.{ .{ .object = renamed }, functions[1] }) };
    };
    const candidate: Value = .{ .object = fields };
    const changed = try withChanges(a, &old, &before, root, candidate);
    var compact_arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer compact_arena.deinit();
    const compact = compact_arena.allocator();
    var fresh: Interner = .{ .gpa = std.testing.allocator };
    defer fresh.deinit();
    var compactor: Compactor = .{ .arena = compact, .scratch = compact, .old_interner = &old, .interner = &fresh };
    const live_before = try compactor.generation(&before);
    const live_changed = try compactor.generation(&changed);
    try std.testing.expect(live_before.functions[1] == live_changed.functions[1]);
    try std.testing.expect(live_before.functions[0] != live_changed.functions[0]);
    try std.testing.expect(live_before.functions[0].body.ids.ptr == live_changed.functions[0].body.ids.ptr);
    try std.testing.expectEqual(@as(?u32, null), fresh.strings.map.get(&.{ 'd', 'e', 'a', 'd' }));
    for ([_]Generation{ before, changed }, [_]Generation{ live_before, live_changed }) |old_generation, new_generation| {
        var expected: Writer.Allocating = .init(a);
        var actual: Writer.Allocating = .init(a);
        try writeDocument(&expected.writer, &old, &old_generation, reader.currentVersion());
        try writeDocument(&actual.writer, &fresh, &new_generation, reader.currentVersion());
        try std.testing.expectEqualStrings(expected.written(), actual.written());
    }
}

test "interner checkpoint releases failed proposal entries and retains prior handles" {
    var interner: Interner = .{ .gpa = std.testing.allocator };
    defer interner.deinit();
    const kept = try interner.id(.function, &.{'f'});
    const checkpoint = interner.checkpoint();
    for (0..100) |_| {
        _ = try interner.id(.node, &.{'n'});
        _ = try interner.string(&.{'s'});
        interner.restore(checkpoint);
        try std.testing.expectEqual(@as(usize, 0), interner.ids[@backingInt(Domain.node)].texts.items.len);
        try std.testing.expectEqual(@as(usize, 0), interner.strings.texts.items.len);
        try std.testing.expectEqualSlices(u16, &.{'f'}, interner.idText(.function, kept));
    }
}

test "interner restore cycles keep lookups correct and the map bounded" {
    var interner: Interner = .{ .gpa = std.testing.allocator };
    defer interner.deinit();
    const kept = try interner.id(.function, &.{'f'});
    const checkpoint = interner.checkpoint();
    var capacity: u32 = 0;
    for (0..2000) |round| {
        for (0..8) |i| _ = try interner.id(.node, &.{ @intCast(round % 60000), @intCast(i) });
        interner.restore(checkpoint);
        try std.testing.expectEqual(kept, interner.findId(.function, &.{'f'}).?);
        try std.testing.expect(interner.findId(.node, &.{ 0, 0 }) == null);
        const table = &interner.ids[@backingInt(Domain.node)];
        capacity = @max(capacity, table.map.capacity());
        try std.testing.expect(table.tombstones <= table.map.capacity() / 4);
    }
    try std.testing.expect(capacity <= 64);
}

test "failed live-set preparation leaves the old generation usable" {
    var fixture_arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer fixture_arena.deinit();
    const a = fixture_arena.allocator();
    const root = try reader.parseValue(a,
        \\{"snapshotId":"base","types":[],"effects":[],"capabilities":[],"functions":[{"id":"f","displayName":"F","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"nodes":[{"id":"n","kind":"unitLiteral"}],"resultNodeId":"n"}}],"obligations":[]}
    );
    var old: Interner = .{ .gpa = a };
    const original = try load(a, &old, root, null);
    const Check = struct {
        fn prepare(gpa: Allocator, previous_interner: *const Interner, previous: *const Generation) !void {
            var arena = std.heap.ArenaAllocator.init(gpa);
            defer arena.deinit();
            var next: Interner = .{ .gpa = gpa };
            defer next.deinit();
            var compactor: Compactor = .{ .arena = arena.allocator(), .scratch = arena.allocator(), .old_interner = previous_interner, .interner = &next };
            const rebuilt = try compactor.generation(previous);
            try std.testing.expectEqualSlices(u16, &.{'F'}, next.stringText(rebuilt.functions[0].name));
        }
    };
    try std.testing.checkAllAllocationFailures(std.testing.allocator, Check.prepare, .{ &old, &original });
    var source: Writer.Allocating = .init(a);
    try writeDocument(&source.writer, &old, &original, reader.currentVersion());
    try std.testing.expect(std.mem.indexOf(u8, source.written(), "\"snapshotId\":\"base\"") != null);
    try std.testing.expectEqualSlices(u16, &.{'F'}, old.stringText(original.functions[0].name));
}

test "payload decoder covers every kind and compaction preserves all shapes and UTF16 units" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    var document: Writer.Allocating = .init(a);
    const out = &document.writer;
    try out.writeAll("{\"snapshotId\":\"snapshot\",\"types\":[],\"effects\":[],\"capabilities\":[],\"obligations\":[],\"functions\":[{\"id\":\"function\",\"displayName\":\"\\ud800\\udc00Z\",\"parameters\":[],\"resultType\":{\"kind\":\"primitive\",\"primitive\":\"unit\"},\"requiredEffects\":[],\"body\":{\"resultNodeId\":\"node\",\"nodes\":[");
    const graph_json = "{\"resultNodeId\":\"nested\",\"nodes\":[{\"id\":\"nested\",\"kind\":\"unitLiteral\"}]}";
    const type_json = "{\"kind\":\"function\",\"parameters\":[{\"kind\":\"named\",\"typeId\":\"type\",\"arguments\":[{\"kind\":\"typeParameter\",\"typeParameterId\":\"tp\"}]},{\"kind\":\"capability\",\"capabilityId\":\"cap\"}],\"resultType\":{\"kind\":\"builtinGeneric\",\"generic\":\"list\",\"arguments\":[{\"kind\":\"primitive\",\"primitive\":\"unit\"}]}}";
    const binding_prefix = "{\"id\":\"binding\",\"displayName\":\"\\udbff\\udfff\",\"type\":";
    for (kindTable(), 0..) |kind, index| {
        for (0..2) |empty| {
            if (index != 0 or empty != 0) try out.writeByte(',');
            try out.print("{{\"id\":\"n{d}-{d}\",\"kind\":\"{s}\"", .{ index, empty, kind.name });
            for (kind.reads) |read| {
                if (empty == 1 and (read.shape == .optid or read.shape == .ctx or read.shape == .targs)) continue;
                try out.print(",\"{s}\":", .{read.name});
                switch (read.shape) {
                    .id, .optid => try out.writeAll("\"reference\""),
                    .ids => try out.writeAll(if (empty == 1) "[]" else "[\"reference\",\"other\"]"),
                    .type => try out.writeAll(type_json),
                    .targs => try out.print("[{s}]", .{type_json}),
                    .graph => try out.writeAll(graph_json),
                    .varm, .errarm => {
                        try out.print("{{\"{s}\":{s}{s}}},\"body\":{s}}}", .{ if (read.shape == .varm) "valueBinding" else "errorBinding", binding_prefix, type_json, graph_json });
                    },
                    .ctx, .args, .capargs, .fields => {
                        if (empty == 1) {
                            try out.writeAll("[]");
                            continue;
                        }
                        const target = switch (read.shape) {
                            .capargs => "targetCapabilityParameterId",
                            .fields => "targetFieldId",
                            else => "targetParameterId",
                        };
                        try out.print("[{{\"{s}\":\"target\",\"valueNodeId\":\"reference\"}}]", .{target});
                    },
                    .lit => try out.writeAll("\"\\ud800\\udc00X\""),
                    .bool => try out.writeAll("true"),
                    .int => try out.writeAll("-9223372036854775808"),
                    .bits => try out.writeAll("\"fff8000000000001\""),
                    .mode => try out.writeAll("\"all\""),
                    .impls => try out.writeAll(if (empty == 1) "[]" else "[{\"operationId\":\"operation\",\"functionId\":\"function\"}]"),
                    .arms => {
                        if (empty == 1) {
                            try out.writeAll("[]");
                            continue;
                        }
                        try out.print("[{{\"variantIds\":[\"variant1\",\"variant2\"],\"fieldBindings\":[{{\"sourceFieldId\":\"field\",\"binding\":{s}{s}}}}}],\"body\":{s}}}]", .{ binding_prefix, type_json, graph_json });
                    },
                }
            }
            try out.writeByte('}');
        }
    }
    try out.writeAll("]}}]}");
    const root = try reader.parseValue(a, document.written());
    var old: Interner = .{ .gpa = a };
    // Force every used domain to receive different handles in the compacted table.
    for (std.enums.values(Domain)) |domain| _ = try old.id(domain, &.{ 'd', 'e', 'a', 'd' });
    _ = try old.string(&.{ 'd', 'e', 'a', 'd' });
    var generation = try load(a, &old, root, null);
    var function = generation.functions[0].*;
    function.name = try old.string(&.{ 0xd800, 'Z', 0xdfff });
    generation.functions = &.{&function};
    const body = &generation.functions[0].body;
    var nodes = body.graph(0).nodes();
    var coverage: [std.enums.values(Shape).len]bool = @splat(false);
    while (nodes.next()) |node| {
        var payload = node.payload();
        var fields: usize = 0;
        while (payload.next()) |field| {
            fields += 1;
            coverage[@backingInt(field.read.shape)] = true;
            switch (field.value) {
                .lit => |handle| try std.testing.expectEqualSlices(u16, &.{ 0xd800, 0xdc00, 'X' }, old.stringText(handle)),
                .int => |value| try std.testing.expectEqual(std.math.minInt(i64), value),
                .bits => |value| try std.testing.expectEqual(@as(u64, 0xfff8000000000001), value),
                .bool => |value| try std.testing.expect(value),
                .mode => |value| try std.testing.expectEqual(WaitMode.all, value),
                .type => |value| try std.testing.expect(value == .function),
                .arms => |arms| {
                    var iterator = arms.iterator();
                    if (iterator.next()) |arm| {
                        try std.testing.expectEqual(@as(usize, 2), arm.variants.len);
                        try std.testing.expectEqual(@as(usize, 1), arm.fields.len());
                        try std.testing.expectEqualSlices(u16, &.{ 0xdbff, 0xdfff }, old.stringText(arm.fields.at(0).binding.name.?));
                        try std.testing.expect(iterator.next() == null);
                    }
                },
                else => {},
            }
        }
        try std.testing.expectEqual(kindTable()[body.kinds[node.index]].reads.len, fields);
    }
    for (coverage) |seen| try std.testing.expect(seen);
    var fresh: Interner = .{ .gpa = a };
    var compactor: Compactor = .{ .arena = a, .scratch = a, .old_interner = &old, .interner = &fresh };
    const remapped = try compactor.generation(&generation);
    try std.testing.expect(fresh.findId(.node, &.{ 'd', 'e', 'a', 'd' }) == null);
    const before_lookup = fresh.checkpoint();
    try std.testing.expect(fresh.findId(.function, &.{ 'm', 'i', 's', 's' }) == null);
    try std.testing.expectEqualDeep(before_lookup, fresh.checkpoint());
    var expected: Writer.Allocating = .init(a);
    var actual: Writer.Allocating = .init(a);
    try writeDocument(&expected.writer, &old, &generation, reader.currentVersion());
    try writeDocument(&actual.writer, &fresh, &remapped, reader.currentVersion());
    try std.testing.expectEqualStrings(expected.written(), actual.written());
    var rebuilt_function = generation.functions[0].*;
    rebuilt_function.body = try builder.rebuild(a, rebuilt_function.body.graph(0), .{});
    var rebuilt_generation = generation;
    rebuilt_generation.functions = &.{&rebuilt_function};
    var rebuilt_document: Writer.Allocating = .init(a);
    try writeDocument(&rebuilt_document.writer, &old, &rebuilt_generation, reader.currentVersion());
    try std.testing.expectEqualStrings(expected.written(), rebuilt_document.written());
    try std.testing.expectEqualSlices(u16, &.{ 0xd800, 'Z', 0xdfff }, fresh.stringText(remapped.functions[0].name));
    // UTF-16 ordinal order compares code units, including unpaired surrogates.
    const high = try fresh.id(.function, &.{0xd800});
    const later = try fresh.id(.function, &.{0xe000});
    try std.testing.expect(fresh.less(.function, high, later));
}

test "typed compaction owns every declaration and nested type after the source arena is freed" {
    var source_arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    const source = source_arena.allocator();
    var target_arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer target_arena.deinit();
    const target = target_arena.allocator();
    const root = try reader.parseValue(source,
        \\{"snapshotId":"snapshot","parentSnapshotId":"parent","types":[{"id":"product","displayName":"Product","exported":true,"kind":"product","typeParameters":[{"typeParameterId":"tp","displayName":"TP"}],"fields":[{"id":"field","displayName":"Field","type":{"kind":"named","typeId":"union","arguments":[]}}]},{"id":"union","displayName":"Union","kind":"union","variants":[{"id":"variant","displayName":"Variant","fields":[{"id":"variant-field","displayName":"VF","type":{"kind":"typeParameter","typeParameterId":"tp"}}]}]}],"effects":[{"id":"effect","displayName":"Effect","exported":true}],"capabilities":[{"id":"cap","displayName":"Capability","exported":true,"taskBound":true,"operations":[{"id":"op","displayName":"Operation","parameters":[{"id":"cp","displayName":"CP","type":{"kind":"capability","capabilityId":"cap"}}],"resultType":{"kind":"function","parameters":[{"kind":"named","typeId":"product"}],"resultType":{"kind":"primitive","primitive":"unit"}},"failureType":{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"primitive","primitive":"string"}]},"effectId":"effect","mayWait":true}]}],"functions":[],"obligations":[{"id":"obligation1","originTransactionId":"origin","callNodeId":"call","parameterId":"parameter","status":"open"},{"id":"obligation2","originTransactionId":"origin","callNodeId":"call","parameterId":"parameter","status":"resolved","resolutionTransactionId":"resolution","resolutionValueNodeId":"value"}]}
    );
    var old: Interner = .{ .gpa = source };
    const original = try load(source, &old, root, .{ .snapshot = &.{ 'm', 'a', 'r', 'k', 'e', 'r' }, .digest = &.{'d'} });
    var expected: Writer.Allocating = .init(target);
    try writeDocument(&expected.writer, &old, &original, reader.currentVersion());
    var fresh: Interner = .{ .gpa = target };
    var compactor: Compactor = .{ .arena = target, .scratch = target, .old_interner = &old, .interner = &fresh };
    const compacted = try compactor.generation(&original);
    source_arena.deinit();
    var actual: Writer.Allocating = .init(target);
    try writeDocument(&actual.writer, &fresh, &compacted, reader.currentVersion());
    try std.testing.expectEqualStrings(expected.written(), actual.written());
    const product = compacted.findType(fresh.findId(.type, &.{ 'p', 'r', 'o', 'd', 'u', 'c', 't' }).?).?;
    try std.testing.expect(product.fields[0].type.named.arguments != null);
    const operation = compacted.capabilities[0].operations[0];
    try std.testing.expect(operation.result.function.parameters[0].named.arguments == null);
}

test "body depth first traversal visits match result ok before err and children before siblings" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const root = try reader.parseValue(a,
        \\{"snapshotId":"s","types":[],"effects":[],"capabilities":[],"obligations":[],"functions":[{"id":"f","displayName":"F","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"resultNodeId":"last","nodes":[{"id":"match","kind":"matchResult","inputNodeId":"last","okArm":{"valueBinding":{"id":"ok-binding","type":{"kind":"primitive","primitive":"unit"}},"body":{"resultNodeId":"ok","nodes":[{"id":"ok","kind":"unitLiteral"}]}},"errArm":{"errorBinding":{"id":"err-binding","type":{"kind":"primitive","primitive":"unit"}},"body":{"resultNodeId":"err","nodes":[{"id":"err","kind":"unitLiteral"}]}}},{"id":"last","kind":"unitLiteral"}]}}]}
    );
    var interner: Interner = .{ .gpa = a };
    const generation = try load(a, &interner, root, null);
    const nodes = try generation.functions[0].body.depthFirst(a);
    try std.testing.expectEqual(@as(usize, 4), nodes.len);
    const expected = [_][]const u8{ "match", "ok", "err", "last" };
    for (nodes, expected) |node, text| try std.testing.expectEqualStrings(text, reader.utf8(interner.idText(.node, node.id())));
}

test "loading string literals retains lone UTF16 units for semantic validation" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const root = try reader.parseValue(a,
        \\{"snapshotId":"s","types":[],"effects":[],"capabilities":[],"obligations":[],"functions":[{"id":"f","displayName":"F","parameters":[],"resultType":{"kind":"primitive","primitive":"string"},"requiredEffects":[],"body":{"resultNodeId":"n","nodes":[{"id":"n","kind":"stringLiteral","value":"\ud800X\udfff"}]}}]}
    );
    var interner: Interner = .{ .gpa = a };
    const generation = try load(a, &interner, root, null);
    const handle = generation.functions[0].body.node(0).stringLiteral().?;
    try std.testing.expectEqualSlices(u16, &.{ 0xd800, 'X', 0xdfff }, interner.stringText(handle));
}

pub const order = @import("typed_order.zig");
