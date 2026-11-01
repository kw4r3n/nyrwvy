//! The session host's typed model (docs/decisions/zig-session-host.md, "In-memory layout"): stable
//! IDs interned per domain into `u32`s, display names and literals interned as UTF-16 units,
//! declaration records, and one struct-of-arrays graph per function body. A snapshot is an
//! immutable `Generation`. `u32` numbers never leave the process: every output writes ID texts.
const std = @import("std");
const reader = @import("main.zig");

const Value = reader.Value;
const Str = reader.Str;
const Allocator = std.mem.Allocator;

pub const Domain = enum(u8) { snapshot, transaction, function, type, field, variant, parameter, type_parameter, node, binding, effect, capability, operation, capability_parameter, obligation };
pub const Id = u32;
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
        return self.ids[@intFromEnum(domain)].intern(self.gpa, text);
    }

    pub fn idText(self: *const Interner, domain: Domain, value: Id) Str {
        return self.ids[@intFromEnum(domain)].texts.items[value];
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
};

// ---------------------------------------------------------------- node kinds

const Shape = enum { id, ids, type, targs, graph, varm, ctx, args, capargs, fields, lit, bool, int, bits, mode, optid, impls, arms, errarm };
const Read = struct { name: []const u8, shape: Shape, domain: ?Domain };
const Kind = struct { name: []const u8, reads: []const Read };

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

fn kindTable() []const Kind {
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
        out.append(gpa, .{ .name = name, .reads = reads.items }) catch @panic("out of memory");
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
            var builder: BodyBuilder = .{ .loader = self };
            _ = try builder.graph(get(item, "body"));
            break :blk builder.done();
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
            .lit => try self.push(payload, try l.interner.string(reader.decoded(value.?))),
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

    pub fn generation(self: *Compactor, old: *const Generation, root: Value) !Generation {
        var result: Generation = undefined;
        result.snapshot = try self.interner.id(.snapshot, self.old_interner.idText(.snapshot, old.snapshot));
        result.parent = if (old.parent == none) none else try self.interner.id(.snapshot, self.old_interner.idText(.snapshot, old.parent));
        result.marker = if (old.marker) |marker| .{ .snapshot = try self.arena.dupe(u16, marker.snapshot), .digest = try self.arena.dupe(u16, marker.digest) } else null;
        var loader: Loader = .{ .arena = self.arena, .interner = self.interner };
        inline for (.{ .{ "functions", Function, Loader.loadFunction, Domain.function }, .{ "types", Type, Loader.loadType, Domain.type }, .{ "effects", Effect, Loader.loadEffect, Domain.effect }, .{ "capabilities", Capability, Loader.loadCapability, Domain.capability }, .{ "obligations", Obligation, Loader.loadObligation, Domain.obligation } }) |entry| {
            const old_records = @field(old, entry[0]);
            const records = try self.arena.alloc(*const entry[1], old_records.len);
            var items: std.HashMapUnmanaged(Str, Value, Utf16Context, 80) = .empty;
            for (reader.arrayItems(root, entry[0])) |value| try items.put(self.scratch, reader.decoded(reader.property(value, "id").?), value);
            for (old_records, records) |original, *slot| {
                if (self.records.get(@intFromPtr(original))) |existing| {
                    slot.* = @ptrFromInt(existing);
                    continue;
                }
                const id = self.old_interner.idText(entry[3], original.id);
                const item = items.get(id) orelse unreachable;
                const record = try self.arena.create(entry[1]);
                if (entry[1] == Function) {
                    const body_key = @intFromPtr(original.body.graphs.ptr);
                    record.* = try loader.loadFunctionWithBody(item, self.bodies.get(body_key));
                    try self.bodies.put(self.scratch, body_key, record.body);
                } else record.* = try entry[2](&loader, item);
                try self.records.put(self.scratch, @intFromPtr(original), @intFromPtr(record));
                slot.* = record;
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
        for (functions) |*f| if (f.*.id == created.id) { f.* = created; break; };
    } else functions[base.functions.len] = created;
    const Order = struct {
        fn less(i: *const Interner, x: *const Function, y: *const Function) bool { return i.less(.function, x.id, y.id); }
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
                fn less(i: *const Interner, x: *const entry[1], y: *const entry[1]) bool { return i.less(entry[3], x.id, y.id); }
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
            var builder: BodyBuilder = .{ .loader = l };
            _ = try builder.graph(new.?);
            result.body = builder.done();
        }
        }
    }
    return result;
}

// ---------------------------------------------------------------- writing

const Writer = std.Io.Writer;

const Emitter = struct {
    out: *Writer,
    interner: *const Interner,

    fn id(self: Emitter, domain: Domain, value: Id) !void {
        try reader.writeString(self.out, self.interner.idText(domain, value));
    }

    fn string(self: Emitter, handle: u32) !void {
        try reader.writeString(self.out, self.interner.stringText(handle));
    }

    fn key(self: Emitter, name: []const u8) !void {
        try self.out.print(",\"{s}\":", .{name});
    }

    fn typeRef(self: Emitter, t: TypeRef) anyerror!void {
        switch (t) {
            .primitive => |name| {
                try self.out.writeAll("{\"kind\":\"primitive\",\"primitive\":");
                try self.string(name);
            },
            .named => |named| {
                try self.out.writeAll("{\"kind\":\"named\",\"typeId\":");
                try self.id(.type, named.id);
                if (named.arguments) |arguments| {
                    try self.key("arguments");
                    try self.typeRefs(arguments);
                }
            },
            .function => |function| {
                try self.out.writeAll("{\"kind\":\"function\",\"parameters\":");
                try self.typeRefs(function.parameters);
                try self.key("resultType");
                try self.typeRef(function.result.*);
            },
            .type_parameter => |value| {
                try self.out.writeAll("{\"kind\":\"typeParameter\",\"typeParameterId\":");
                try self.id(.type_parameter, value);
            },
            .capability => |value| {
                try self.out.writeAll("{\"kind\":\"capability\",\"capabilityId\":");
                try self.id(.capability, value);
            },
            .generic => |generic| {
                try self.out.writeAll("{\"kind\":\"builtinGeneric\",\"generic\":");
                try self.string(generic.name);
                try self.key("arguments");
                try self.typeRefs(generic.arguments);
            },
        }
        try self.out.writeByte('}');
    }

    fn typeRefs(self: Emitter, items: []const TypeRef) !void {
        try self.out.writeByte('[');
        for (items, 0..) |item, index| {
            if (index > 0) try self.out.writeByte(',');
            try self.typeRef(item);
        }
        try self.out.writeByte(']');
    }

    fn typed(self: Emitter, domain: Domain, items: []const Typed) !void {
        try self.out.writeByte('[');
        for (items, 0..) |item, index| {
            if (index > 0) try self.out.writeByte(',');
            try self.out.writeAll("{\"id\":");
            try self.id(domain, item.id);
            try self.key("displayName");
            try self.string(item.name);
            try self.key("type");
            try self.typeRef(item.type);
            try self.out.writeByte('}');
        }
        try self.out.writeByte(']');
    }

    fn head(self: Emitter, domain: Domain, value: Id, name: u32, exported: bool) !void {
        try self.out.writeAll("{\"id\":");
        try self.id(domain, value);
        try self.key("displayName");
        try self.string(name);
        if (exported) try self.out.writeAll(",\"exported\":true");
    }

    fn typeParameters(self: Emitter, parameters: ?[]const TypeParameter) !void {
        const items = parameters orelse return;
        try self.key("typeParameters");
        try self.out.writeByte('[');
        for (items, 0..) |item, index| {
            if (index > 0) try self.out.writeByte(',');
            try self.out.writeAll("{\"typeParameterId\":");
            try self.id(.type_parameter, item.id);
            try self.key("displayName");
            try self.string(item.name);
            try self.out.writeByte('}');
        }
        try self.out.writeByte(']');
    }

    fn binding(self: Emitter, value: Binding) !void {
        try self.out.writeAll("{\"id\":");
        try self.id(.binding, value.id);
        if (value.name) |name| {
            try self.key("displayName");
            try self.string(name);
        }
        try self.key("type");
        try self.typeRef(value.type);
        try self.out.writeByte('}');
    }

    fn graph(self: Emitter, body: *const Body, index: u32) anyerror!void {
        const g = body.graphs[index];
        try self.out.writeAll("{\"resultNodeId\":");
        try self.id(.node, g.result);
        try self.key("nodes");
        try self.out.writeByte('[');
        for (g.first..g.first + g.count, 0..) |at, position| {
            if (position > 0) try self.out.writeByte(',');
            try self.node(body, at);
        }
        try self.out.writeAll("]}");
    }

    fn targets(self: Emitter, payload: []const u32, at: *usize, target: []const u8, domain: Domain) !void {
        const count = payload[at.*];
        at.* += 1;
        try self.out.writeByte('[');
        for (0..count) |index| {
            if (index > 0) try self.out.writeByte(',');
            try self.out.print("{{\"{s}\":", .{target});
            try self.id(domain, payload[at.*]);
            try self.key("valueNodeId");
            try self.id(.node, payload[at.* + 1]);
            try self.out.writeByte('}');
            at.* += 2;
        }
        try self.out.writeByte(']');
    }

    fn node(self: Emitter, body: *const Body, at: usize) !void {
        const kind = kindTable()[body.kinds[at]];
        const payload = body.extra[body.starts[at] .. body.starts[at] + body.lens[at]];
        try self.out.writeAll("{\"id\":");
        try self.id(.node, body.ids[at]);
        try self.out.print(",\"kind\":\"{s}\"", .{kind.name});
        var i: usize = 0;
        for (kind.reads) |read| {
            switch (read.shape) {
                .optid => if (payload[i] == none) {
                    i += 1;
                    continue;
                },
                .targs, .ctx => if (payload[i] == 0) {
                    i += 1;
                    continue;
                },
                else => {},
            }
            try self.key(read.name);
            switch (read.shape) {
                .id, .optid => {
                    try self.id(read.domain.?, payload[i]);
                    i += 1;
                },
                .ids => {
                    const count = payload[i];
                    i += 1;
                    try self.out.writeByte('[');
                    for (0..count) |index| {
                        if (index > 0) try self.out.writeByte(',');
                        try self.id(read.domain.?, payload[i]);
                        i += 1;
                    }
                    try self.out.writeByte(']');
                },
                .type => {
                    try self.typeRef(body.types[payload[i]]);
                    i += 1;
                },
                .targs => {
                    const count = payload[i];
                    i += 1;
                    try self.out.writeByte('[');
                    for (0..count) |index| {
                        if (index > 0) try self.out.writeByte(',');
                        try self.typeRef(body.types[payload[i]]);
                        i += 1;
                    }
                    try self.out.writeByte(']');
                },
                .graph => {
                    try self.graph(body, payload[i]);
                    i += 1;
                },
                .varm, .errarm => {
                    try self.out.writeAll(if (read.shape == .varm) "{\"valueBinding\":" else "{\"errorBinding\":");
                    try self.binding(body.bindings[payload[i]]);
                    try self.key("body");
                    try self.graph(body, payload[i + 1]);
                    try self.out.writeByte('}');
                    i += 2;
                },
                .ctx, .args => try self.targets(payload, &i, "targetParameterId", .parameter),
                .capargs => try self.targets(payload, &i, "targetCapabilityParameterId", .capability_parameter),
                .fields => try self.targets(payload, &i, "targetFieldId", .field),
                .lit => {
                    try self.string(payload[i]);
                    i += 1;
                },
                .bool => {
                    try self.out.writeAll(if (payload[i] == 1) "true" else "false");
                    i += 1;
                },
                .int => {
                    const bits = @as(u64, payload[i]) | (@as(u64, payload[i + 1]) << 32);
                    try self.out.print("{d}", .{@as(i64, @bitCast(bits))});
                    i += 2;
                },
                .bits => {
                    const bits = @as(u64, payload[i]) | (@as(u64, payload[i + 1]) << 32);
                    try self.out.print("\"{x:0>16}\"", .{bits});
                    i += 2;
                },
                .mode => {
                    try self.out.writeAll(if (payload[i] == 1) "\"all\"" else "\"any\"");
                    i += 1;
                },
                .impls => {
                    const count = payload[i];
                    i += 1;
                    try self.out.writeByte('[');
                    for (0..count) |index| {
                        if (index > 0) try self.out.writeByte(',');
                        try self.out.writeAll("{\"operationId\":");
                        try self.id(.operation, payload[i]);
                        try self.key("functionId");
                        try self.id(.function, payload[i + 1]);
                        try self.out.writeByte('}');
                        i += 2;
                    }
                    try self.out.writeByte(']');
                },
                .arms => {
                    const count = payload[i];
                    i += 1;
                    try self.out.writeByte('[');
                    for (0..count) |index| {
                        if (index > 0) try self.out.writeByte(',');
                        const variants = payload[i];
                        i += 1;
                        if (variants == 1) {
                            try self.out.writeAll("{\"variantId\":");
                            try self.id(.variant, payload[i]);
                            i += 1;
                        } else {
                            try self.out.writeAll("{\"variantIds\":[");
                            for (0..variants) |v| {
                                if (v > 0) try self.out.writeByte(',');
                                try self.id(.variant, payload[i]);
                                i += 1;
                            }
                            try self.out.writeByte(']');
                        }
                        const bindings = payload[i];
                        i += 1;
                        try self.key("fieldBindings");
                        try self.out.writeByte('[');
                        for (0..bindings) |b| {
                            if (b > 0) try self.out.writeByte(',');
                            try self.out.writeAll("{\"sourceFieldId\":");
                            try self.id(.field, payload[i]);
                            try self.key("binding");
                            try self.binding(body.bindings[payload[i + 1]]);
                            try self.out.writeByte('}');
                            i += 2;
                        }
                        try self.out.writeByte(']');
                        try self.key("body");
                        try self.graph(body, payload[i]);
                        i += 1;
                        try self.out.writeByte('}');
                    }
                    try self.out.writeByte(']');
                },
            }
        }
        try self.out.writeByte('}');
    }
};

/// A snapshot document of the generation at schema `version`: valid input to the model reader,
/// which writes the canonical text from it (`writtenText`).
pub fn writeDocument(out: *Writer, interner: *const Interner, generation: *const Generation, version: i32) !void {
    const e: Emitter = .{ .out = out, .interner = interner };
    try out.print("{{\"schemaVersion\":{d},\"snapshotId\":", .{version});
    try e.id(.snapshot, generation.snapshot);
    if (generation.marker) |marker| {
        try out.writeAll(",\"dependency\":{\"snapshotId\":");
        try reader.writeString(out, marker.snapshot);
        try out.writeAll(",\"digest\":");
        try reader.writeString(out, marker.digest);
        try out.writeByte('}');
    }
    if (generation.parent != none) {
        try e.key("parentSnapshotId");
        try e.id(.snapshot, generation.parent);
    }
    try out.writeAll(",\"types\":[");
    for (generation.types, 0..) |t, index| {
        if (index > 0) try out.writeByte(',');
        try e.head(.type, t.id, t.name, t.exported);
        try e.typeParameters(t.type_parameters);
        if (t.product) {
            try out.writeAll(",\"kind\":\"product\",\"fields\":");
            try e.typed(.field, t.fields);
        } else {
            try out.writeAll(",\"kind\":\"union\",\"variants\":[");
            for (t.variants, 0..) |variant, v| {
                if (v > 0) try out.writeByte(',');
                try e.head(.variant, variant.id, variant.name, false);
                try e.key("fields");
                try e.typed(.field, variant.fields);
                try out.writeByte('}');
            }
            try out.writeByte(']');
        }
        try out.writeByte('}');
    }
    try out.writeAll("],\"effects\":[");
    for (generation.effects, 0..) |effect, index| {
        if (index > 0) try out.writeByte(',');
        try e.head(.effect, effect.id, effect.name, effect.exported);
        try out.writeByte('}');
    }
    try out.writeAll("],\"capabilities\":[");
    for (generation.capabilities, 0..) |capability, index| {
        if (index > 0) try out.writeByte(',');
        try e.head(.capability, capability.id, capability.name, capability.exported);
        if (capability.task_bound) try out.writeAll(",\"taskBound\":true");
        try out.writeAll(",\"operations\":[");
        for (capability.operations, 0..) |operation, o| {
            if (o > 0) try out.writeByte(',');
            try e.head(.operation, operation.id, operation.name, false);
            try e.key("parameters");
            try e.typed(.capability_parameter, operation.parameters);
            try e.key("resultType");
            try e.typeRef(operation.result);
            if (operation.failure) |failure| {
                try e.key("failureType");
                try e.typeRef(failure);
            }
            try e.key("effectId");
            try e.id(.effect, operation.effect);
            if (operation.may_wait) try out.writeAll(",\"mayWait\":true");
            try out.writeByte('}');
        }
        try out.writeAll("]}");
    }
    try out.writeAll("],\"functions\":[");
    for (generation.functions, 0..) |function, index| {
        if (index > 0) try out.writeByte(',');
        try e.head(.function, function.id, function.name, function.exported);
        if (function.must_not_wait) try out.writeAll(",\"mustNotWait\":true");
        try e.typeParameters(function.type_parameters);
        try e.key("parameters");
        try e.typed(.parameter, function.parameters);
        try e.key("resultType");
        try e.typeRef(function.result);
        try out.writeAll(",\"requiredEffects\":[");
        for (function.effects, 0..) |effect, x| {
            if (x > 0) try out.writeByte(',');
            try e.id(.effect, effect);
        }
        try out.writeAll("],\"body\":");
        try e.graph(&function.body, 0);
        try out.writeByte('}');
    }
    try out.writeAll("],\"obligations\":[");
    for (generation.obligations, 0..) |obligation, index| {
        if (index > 0) try out.writeByte(',');
        try out.writeAll("{\"id\":");
        try e.id(.obligation, obligation.id);
        try out.writeAll(",\"kind\":\"missingArgument\",\"originTransactionId\":");
        try e.id(.transaction, obligation.origin);
        try e.key("callNodeId");
        try e.id(.node, obligation.call);
        try e.key("parameterId");
        try e.id(.parameter, obligation.parameter);
        if (obligation.resolution) |resolution| {
            try out.writeAll(",\"status\":\"resolved\",\"resolutionTransactionId\":");
            try e.id(.transaction, resolution.transaction);
            try e.key("resolutionValueNodeId");
            try e.id(.node, resolution.value);
        } else try out.writeAll(",\"status\":\"open\"");
        try out.writeByte('}');
    }
    try out.writeAll("]}");
}

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
    const live_before = try compactor.generation(&before, root);
    const live_changed = try compactor.generation(&changed, candidate);
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
        try std.testing.expectEqual(@as(usize, 0), interner.ids[@intFromEnum(Domain.node)].texts.items.len);
        try std.testing.expectEqual(@as(usize, 0), interner.strings.texts.items.len);
        try std.testing.expectEqualSlices(u16, &.{'f'}, interner.idText(.function, kept));
    }
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
        fn prepare(gpa: Allocator, previous_interner: *const Interner, previous: *const Generation, previous_root: Value) !void {
            var arena = std.heap.ArenaAllocator.init(gpa);
            defer arena.deinit();
            var next: Interner = .{ .gpa = gpa };
            defer next.deinit();
            var compactor: Compactor = .{ .arena = arena.allocator(), .scratch = arena.allocator(), .old_interner = previous_interner, .interner = &next };
            const rebuilt = try compactor.generation(previous, previous_root);
            try std.testing.expectEqualSlices(u16, &.{'F'}, next.stringText(rebuilt.functions[0].name));
        }
    };
    try std.testing.checkAllAllocationFailures(std.testing.allocator, Check.prepare, .{ &old, &original, root });
    var source: Writer.Allocating = .init(a);
    try writeDocument(&source.writer, &old, &original, reader.currentVersion());
    try std.testing.expect(std.mem.indexOf(u8, source.written(), "\"snapshotId\":\"base\"") != null);
    try std.testing.expectEqualSlices(u16, &.{'F'}, old.stringText(original.functions[0].name));
}
