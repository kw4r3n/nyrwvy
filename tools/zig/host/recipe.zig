//! Decoding a request's types and exact-ID expression recipes as the F# host does (`WireInput`,
//! `WireRecipe`, `WireRecipeCommon`, `WireRecipeNames`), with its rejections. A recipe that decodes
//! becomes a `Node` tree in the exact-ID form (`ExpressionRecipe`), whichever surface spelled it.
const std = @import("std");
const model = @import("model_reader");
const query = @import("query.zig");

const Json = std.json.Value;
const Allocator = std.mem.Allocator;
const Fail = anyerror;

const Shape = enum { recipe, recipes, id, type, types, int, int64, bool, text, bits, optrecipe, context, args, capargs, fields, mode, slot, selfargs, impls, arms, pname, fnref, ctx, capsel, capop, typesel, variant, field, pfields, ufields, scapargs, simpls, sarms };
/// `callback`: for a callback context, the property whose function it binds.
const Read = struct { name: []const u8, shape: Shape, callback: []const u8 = "" };
const Table = std.StringHashMapUnmanaged(Kind);
/// `order`: the F# constructor's order of the slots, when it is not the read order.
const Kind = struct { context: []const u8, allowed: []const []const u8, required: []const []const u8, reads: []const Read, order: ?[]const []const u8 };

/// `TypeRef`; a type parameter is its ID (a request-local one is spelled as its name).
pub const TypeRef = union(enum) {
    primitive: []const u8,
    type_parameter: []const u8,
    capability: []const u8,
    named: struct { id: []const u8, arguments: []const TypeRef },
    function: struct { parameters: []const TypeRef, result: *const TypeRef },
    builtin: struct { generic: []const u8, arguments: []const TypeRef },
};

/// An `ExpressionRecipe` case: its F# name and its fields, named as the exact-ID wire spells them, in
/// the constructor's order (the order `recipeChildren` and `ExpressionRecipeTypes.collect` follow).
pub const Node = struct { case: []const u8, slots: []const Slot };
pub const Slot = struct { name: []const u8, data: Data };
pub const Data = union(enum) {
    recipe: *const Node,
    recipes: []const *const Node,
    pairs: []const Pair,
    arms: []const Arm,
    implementations: []const Implementation,
    type: TypeRef,
    types: []const TypeRef,
    id: []const u8,
    int: i64,
    text: []const u8,
    bits: u64,
    bool: bool,
    pattern: struct { slot: []const u8, field_id: ?[]const u8 },
};
/// A bound recipe: `id` is the parameter, field or capability parameter; a self call binds `slot`.
pub const Pair = struct { id: []const u8 = "", slot: i64 = 0, value: *const Node };
/// `variants` is empty for the `otherwise` arm.
pub const Arm = struct { variants: []const []const u8, body: *const Node };
pub const Implementation = struct { operation: []const u8, function: []const u8 };

/// `recipeChildren`: the single recipes in slot order, then those of the lists in slot order.
pub fn children(a: Allocator, node: *const Node) ![]const *const Node {
    var out: std.ArrayList(*const Node) = .empty;
    for (node.slots) |slot| if (slot.data == .recipe) try out.append(a, slot.data.recipe);
    for (node.slots) |slot| switch (slot.data) {
        .recipes => |items| try out.appendSlice(a, items),
        .pairs => |pairs| for (pairs) |pair| try out.append(a, pair.value),
        .arms => |arms| for (arms) |arm| try out.append(a, arm.body),
        else => {},
    };
    return out.items;
}

/// `ExpressionRecipeTypes.collect`: every type the tree holds, in constructor order, nested recipes included.
pub fn collectTypes(a: Allocator, node: *const Node, out: *std.ArrayList(TypeRef)) !void {
    for (node.slots) |slot| switch (slot.data) {
        .type => |t| try out.append(a, t),
        .types => |items| try out.appendSlice(a, items),
        .recipe => |child| try collectTypes(a, child, out),
        .recipes => |items| for (items) |child| try collectTypes(a, child, out),
        .pairs => |pairs| for (pairs) |pair| try collectTypes(a, pair.value, out),
        .arms => |arms| for (arms) |arm| try collectTypes(a, arm.body, out),
        else => {},
    };
}

var kinds: ?Table = null;
var byname_kinds: ?Table = null;

fn words(gpa: Allocator, text: []const u8) []const []const u8 {
    var out: std.ArrayList([]const u8) = .empty;
    var it = std.mem.tokenizeScalar(u8, text, ' ');
    while (it.next()) |word| out.append(gpa, word) catch @panic("out of memory");
    return out.items;
}

fn kindTable() Table {
    if (kinds == null) kinds = parseTable("recipe_kinds.txt");
    return kinds.?;
}

fn byNameTable() Table {
    if (byname_kinds == null) byname_kinds = parseTable("recipe_kinds_byname.txt");
    return byname_kinds.?;
}

fn parseTable(comptime file: []const u8) Table {
    const gpa = std.heap.page_allocator;
    var table: Table = .empty;
    var lines = std.mem.tokenizeScalar(u8, @embedFile(file), '\n');
    while (lines.next()) |line| {
        if (line[0] == '#') continue;
        var parts = std.mem.splitScalar(u8, line, '|');
        const name = parts.next().?;
        const context = parts.next().?;
        const allowed = words(gpa, parts.next().?);
        const required = words(gpa, parts.next().?);
        var reads: std.ArrayList(Read) = .empty;
        for (words(gpa, parts.next().?)) |item| {
            var bits = std.mem.splitScalar(u8, item, ':');
            const read_name = bits.next().?;
            const shape = std.meta.stringToEnum(Shape, bits.next().?) orelse std.debug.panic(file ++ ": unknown shape '{s}'", .{item});
            reads.append(gpa, .{ .name = read_name, .shape = shape, .callback = bits.next() orelse "" }) catch @panic("out of memory");
        }
        const order = if (parts.next()) |text| words(gpa, text) else null;
        table.put(gpa, name, .{ .context = context, .allowed = allowed, .required = required, .reads = reads.items, .order = order }) catch @panic("out of memory");
    }
    return table;
}

/// The paths (`/key/index/…` from the request root) of the objects that name a property twice, as
/// the last occurrence of each object shows them; `ensureFields` rejects those when it reaches them.
pub fn duplicatePaths(a: Allocator, line: []const u8) !std.StringHashMapUnmanaged(void) {
    const Frame = struct { object: bool, path: []const u8, names: std.StringHashMapUnmanaged(void) = .empty, key: []const u8 = "", index: usize = 0, duplicate: bool = false, expecting_name: bool = false };
    var out: std.StringHashMapUnmanaged(void) = .empty;
    var frames: std.ArrayList(Frame) = .empty;
    var scanner = std.json.Scanner.initCompleteInput(a, line);
    while (true) {
        const token = try scanner.nextAlloc(a, .alloc_always);
        switch (token) {
            .end_of_document => return out,
            .object_begin, .array_begin => {
                const path = if (frames.items.len == 0) "" else blk: {
                    const parent = &frames.items[frames.items.len - 1];
                    break :blk if (parent.object) try std.fmt.allocPrint(a, "{s}/{s}", .{ parent.path, parent.key }) else try std.fmt.allocPrint(a, "{s}/{d}", .{ parent.path, parent.index });
                };
                try frames.append(a, .{ .object = token == .object_begin, .path = path, .expecting_name = token == .object_begin });
                continue;
            },
            .object_end, .array_end => {
                const frame = frames.pop().?;
                if (frame.object) {
                    if (frame.duplicate) try out.put(a, frame.path, {}) else _ = out.remove(frame.path);
                }
            },
            .allocated_string => |text| {
                const frame = &frames.items[frames.items.len - 1];
                if (frame.object and frame.expecting_name) {
                    if ((try frame.names.fetchPut(a, text, {})) != null) frame.duplicate = true;
                    frame.key = text;
                    frame.expecting_name = false;
                    continue;
                }
            },
            else => {},
        }
        // A value ended: the next string of an object is a name, the next item of an array has the next index.
        if (frames.items.len > 0) {
            const frame = &frames.items[frames.items.len - 1];
            if (frame.object) frame.expecting_name = true else frame.index += 1;
        }
    }
}

/// The function a by-name recipe is written for (`WireSymbolic.ParameterContext.NewFunction`).
pub const Owner = struct { name: []const u8, parameters: []const []const u8 = &.{} };

pub const Decoder = struct {
    r: *query.Request,
    duplicates: std.StringHashMapUnmanaged(void),
    /// Set for a by-name request: recipes and types are then `WireSymbolic`'s.
    owner: ?Owner = null,

    fn fail(self: *Decoder, comptime format: []const u8, args: anytype) Fail {
        return self.r.fail(format, args);
    }

    fn at(self: *Decoder, path: []const u8, key: anytype) ![]const u8 {
        return switch (@TypeOf(key)) {
            usize => std.fmt.allocPrint(self.r.a, "{s}/{d}", .{ path, key }),
            else => std.fmt.allocPrint(self.r.a, "{s}/{s}", .{ path, key }),
        };
    }

    fn object(self: *Decoder, value: Json) Fail!std.json.ObjectMap {
        if (value != .object) return self.fail("Expected a JSON object.", .{});
        try query.lookable(self.r, value.object);
        return value.object;
    }

    /// `WireInput.ensureFields`.
    pub fn ensureFields(self: *Decoder, path: []const u8, value: Json, context: []const u8, allowed: []const []const u8, required: []const []const u8) Fail!void {
        const map = try self.object(value);
        if (self.duplicates.contains(path)) return self.fail("JSON object contains duplicate property names.", .{});
        const a = self.r.a;
        var unknown: std.ArrayList([]const u8) = .empty;
        for (map.keys()) |key| {
            for (allowed) |field| {
                if (std.mem.eql(u8, key, field)) break;
            } else try unknown.append(a, key);
        }
        var missing: std.ArrayList([]const u8) = .empty;
        for (required) |field| if (map.get(field) == null) try missing.append(a, field);
        const ordinal = struct {
            fn less(_: void, x: []const u8, y: []const u8) bool {
                return std.mem.order(u8, x, y) == .lt;
            }
        }.less;
        std.mem.sort([]const u8, unknown.items, {}, ordinal);
        std.mem.sort([]const u8, missing.items, {}, ordinal);
        if (unknown.items.len > 0) return self.fail("{s} contains unsupported properties: {s}. Allowed: {s}.", .{ context, try std.mem.join(a, ", ", unknown.items), try std.mem.join(a, ", ", allowed) });
        if (missing.items.len > 0) return self.fail("{s} is missing required properties: {s}.", .{ context, try std.mem.join(a, ", ", missing.items) });
    }

    /// `WireInput.property`.
    pub fn property(self: *Decoder, value: Json, name: []const u8) Fail!Json {
        const map = try self.object(value);
        return map.get(name) orelse self.fail("Missing required property '{s}'.", .{name});
    }

    fn tryProperty(self: *Decoder, value: Json, name: []const u8) Fail!?Json {
        const map = try self.object(value);
        return map.get(name);
    }

    pub fn requiredString(self: *Decoder, value: Json, name: []const u8) Fail![]const u8 {
        const v = try self.property(value, name);
        if (v != .string) return self.fail("Property '{s}' must be a JSON string.", .{name});
        return query.readable(self.r, v.string);
    }

    /// `WireInput.stableId`: `StableId.tryCreate`'s two errors.
    pub fn stableId(self: *Decoder, label: []const u8, text: []const u8) Fail![]const u8 {
        var it = std.unicode.Utf8View.initUnchecked(text).iterator();
        var blank = true;
        while (it.nextCodepoint()) |c| switch (c) {
            0x09...0x0D, 0x20, 0x85, 0xA0, 0x1680, 0x2000...0x200A, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000 => {},
            else => blank = false,
        };
        if (blank) return self.fail("{s} is not a valid stable ID: BlankValue.", .{label});
        if (std.mem.indexOfAny(u8, text, "\r\n") != null) return self.fail("{s} is not a valid stable ID: ContainsLineBreak.", .{label});
        return text;
    }

    fn id(self: *Decoder, value: Json, name: []const u8) Fail![]const u8 {
        return self.stableId(name, try self.requiredString(value, name));
    }

    pub fn array(self: *Decoder, value: Json, name: []const u8) Fail![]const Json {
        const v = try self.property(value, name);
        if (v != .array) return self.fail("Property '{s}' must be a JSON array.", .{name});
        return v.array.items;
    }

    pub fn optionalBool(self: *Decoder, value: Json, name: []const u8) Fail!void {
        const v = (try self.tryProperty(value, name)) orelse return;
        if (v != .bool) return self.fail("Property '{s}' must be a JSON boolean.", .{name});
    }

    fn requiredInt(self: *Decoder, value: Json, name: []const u8) Fail!i64 {
        const v = try self.property(value, name);
        const ok = v == .integer and v.integer >= std.math.minInt(i32) and v.integer <= std.math.maxInt(i32);
        if (!ok) return self.fail("Property '{s}' must be a 32-bit integer.", .{name});
        return v.integer;
    }

    /// `WireInput.requiredInt64`: a JSON integer, or a string `Int64.TryParse` takes with a leading sign only.
    fn requiredInt64(self: *Decoder, value: Json, name: []const u8) Fail!i64 {
        const v = try self.property(value, name);
        const parsed: ?i64 = switch (v) {
            .integer => |n| n,
            .string => |given| blk: {
                // `Int64.TryParse` also ignores trailing NUL characters.
                const text = std.mem.trimEnd(u8, try query.readable(self.r, given), "\x00");
                const digits = if (text.len > 0 and (text[0] == '-' or text[0] == '+')) text[1..] else text;
                if (digits.len == 0) break :blk null;
                for (digits) |c| if (!std.ascii.isDigit(c)) break :blk null;
                break :blk std.fmt.parseInt(i64, text, 10) catch null;
            },
            else => null,
        };
        return parsed orelse self.fail("Property '{s}' must be a 64-bit integer or exact decimal integer string.", .{name});
    }

    fn typeList(self: *Decoder, path: []const u8, items: []const Json) Fail![]const TypeRef {
        const out = try self.r.a.alloc(TypeRef, items.len);
        for (items, 0..) |item, i| out[i] = try self.anyType(try self.at(path, i), item);
        return out;
    }

    /// `WireInput.typeRef`.
    pub fn typeRef(self: *Decoder, path: []const u8, value: Json) Fail!TypeRef {
        const kind = try self.requiredString(value, "kind");
        const eql = std.mem.eql;
        if (eql(u8, kind, "function")) {
            try self.ensureFields(path, value, "Function type", &.{ "kind", "parameters", "resultType" }, &.{ "kind", "parameters", "resultType" });
            const parameters = try self.typeList(try self.at(path, "parameters"), try self.array(value, "parameters"));
            const result = try self.r.a.create(TypeRef);
            result.* = try self.anyType(try self.at(path, "resultType"), try self.property(value, "resultType"));
            return .{ .function = .{ .parameters = parameters, .result = result } };
        } else if (eql(u8, kind, "primitive")) {
            try self.ensureFields(path, value, "primitive type", &.{ "kind", "primitive" }, &.{ "kind", "primitive" });
            const primitive = try self.requiredString(value, "primitive");
            for ([_][]const u8{ "string", "bool", "unit", "int64", "float64", "bits32", "bytes" }) |known| {
                if (eql(u8, known, primitive)) break;
            } else return self.fail("Unsupported primitive type '{s}'.", .{primitive});
            return .{ .primitive = primitive };
        } else if (eql(u8, kind, "named")) {
            try self.ensureFields(path, value, "named type", &.{ "kind", "typeId", "arguments" }, &.{ "kind", "typeId" });
            const type_id = try self.id(value, "typeId");
            return .{ .named = .{ .id = type_id, .arguments = try self.typeArguments(path, value, "arguments") } };
        } else if (eql(u8, kind, "capability")) {
            try self.ensureFields(path, value, "capability type", &.{ "kind", "capabilityId" }, &.{ "kind", "capabilityId" });
            return .{ .capability = try self.id(value, "capabilityId") };
        } else if (eql(u8, kind, "typeParameter") and (try self.tryProperty(value, "name")) != null) {
            try self.ensureFields(path, value, "type parameter", &.{ "kind", "name" }, &.{ "kind", "name" });
            const name = try self.requiredString(value, "name");
            if (try isBlank(self.r.a, name)) return self.fail("Type parameter names must not be blank.", .{});
            return .{ .type_parameter = try self.id(value, "name") };
        } else if (eql(u8, kind, "typeParameter")) {
            try self.ensureFields(path, value, "type parameter", &.{ "kind", "typeParameterId", "displayName" }, &.{ "kind", "typeParameterId" });
            return .{ .type_parameter = try self.id(value, "typeParameterId") };
        } else if (eql(u8, kind, "builtinGeneric")) {
            try self.ensureFields(path, value, "builtin generic type", &.{ "kind", "generic", "arguments" }, &.{ "kind", "generic", "arguments" });
            const generic = try self.requiredString(value, "generic");
            for ([_][]const u8{ "option", "result", "map", "list", "set", "oneShot", "task", "exception", "taskScope" }) |known| {
                if (eql(u8, known, generic)) break;
            } else return self.fail("Unsupported builtin generic '{s}'.", .{generic});
            return .{ .builtin = .{ .generic = generic, .arguments = try self.typeList(try self.at(path, "arguments"), try self.array(value, "arguments")) } };
        } else return self.fail("Unsupported type kind '{s}'.", .{kind});
    }

    fn typeArguments(self: *Decoder, path: []const u8, value: Json, name: []const u8) Fail![]const TypeRef {
        if ((try self.tryProperty(value, name)) == null) return &.{};
        return self.typeList(try self.at(path, name), try self.array(value, name));
    }

    /// A type as this request spells it: `WireInput.typeRef`, or `WireSymbolic.typeRef` by name.
    pub fn anyType(self: *Decoder, path: []const u8, value: Json) Fail!TypeRef {
        if (self.owner == null) return self.typeRef(path, value);
        const kind = try self.requiredString(value, "kind");
        const eql = std.mem.eql;
        if (eql(u8, kind, "function") or eql(u8, kind, "primitive") or eql(u8, kind, "builtinGeneric")) {
            return self.typeRef(path, value);
        } else if (eql(u8, kind, "named")) {
            try self.ensureFields(path, value, "named type", &.{ "kind", "name", "arguments" }, &.{ "kind", "name" });
            // A string name is read before the arguments (to look it up among types created together).
            const name = try self.property(value, "name");
            if (name == .string) _ = try query.readable(self.r, name.string);
            const arguments = try self.typeArguments(path, value, "arguments");
            return .{ .named = .{ .id = query.idOf(try self.selector(try self.at(path, "name"), name, "type")), .arguments = arguments } };
        } else if (eql(u8, kind, "capability")) {
            try self.ensureFields(path, value, "capability type", &.{ "kind", "name" }, &.{ "kind", "name" });
            return .{ .capability = query.idOf(try self.selector(try self.at(path, "name"), try self.property(value, "name"), "capability")) };
        } else if (eql(u8, kind, "typeParameter")) {
            try self.ensureFields(path, value, "type parameter", &.{ "kind", "name" }, &.{ "kind", "name" });
            const name = try self.requiredString(value, "name");
            if (try isBlank(self.r.a, name)) return self.fail("Type parameter names must not be blank.", .{});
            return .{ .type_parameter = try self.id(value, "name") };
        } else return self.fail("Unsupported symbolic type kind '{s}'.", .{kind});
    }

    /// `WireSymbolic.resolveSelector` of `selector` (at `path`) among the visible `kind` declarations.
    pub fn selector(self: *Decoder, path: []const u8, selector_value: Json, kind: []const u8) Fail!query.Value {
        const items = try query.visible(self.r, kind);
        if (selector_value == .string) return query.exactOne(self.r, kind, try query.readable(self.r, selector_value.string), items);
        try self.ensureFields(path, selector_value, try std.fmt.allocPrint(self.r.a, "{s} selector", .{kind}), &.{ "id", "name", "dependencySnapshotId", "ownership" }, &.{});
        return query.guarded(self.r, kind, items, selector_value.object);
    }

    /// `exactOne` among the `group` members of a declaration, labelled `label` (with `args`).
    fn member(self: *Decoder, comptime label: []const u8, args: anytype, parent: query.Value, group: []const u8, wanted: []const u8) Fail!query.Value {
        return query.exactOne(self.r, try std.fmt.allocPrint(self.r.a, label, args), wanted, model.arrayItems(parent, group));
    }

    /// `exactOne` among names, as an index.
    fn exactName(self: *Decoder, label: []const u8, wanted: []const u8, names: []const []const u8) Fail!usize {
        var found: ?usize = null;
        var count: usize = 0;
        for (names, 0..) |name, i| if (std.mem.eql(u8, name, wanted)) {
            found = i;
            count += 1;
        };
        if (count == 1) return found.?;
        if (count == 0) return self.fail("Unknown {s} '{s}'.{s}", .{ label, wanted, try query.candidateHint(self.r.a, wanted, names) });
        return self.fail("Ambiguous {s} '{s}'.", .{ label, wanted });
    }

    fn productField(self: *Decoder, owner: query.Value, name: []const u8) Fail!query.Value {
        if (model.property(owner, "fields") == null) return self.fail("Type '{s}' is not a product type.", .{query.nameOf(owner)});
        return self.member("field on {s}", .{query.nameOf(owner)}, owner, "fields", name);
    }

    fn unionVariant(self: *Decoder, owner: query.Value, name: []const u8) Fail!query.Value {
        if (model.property(owner, "variants") == null) return self.fail("Type '{s}' is not a union type.", .{query.nameOf(owner)});
        return self.member("variant on {s}", .{query.nameOf(owner)}, owner, "variants", name);
    }

    fn variantField(self: *Decoder, variant: query.Value, name: []const u8) Fail!query.Value {
        return self.member("field on variant {s}", .{query.nameOf(variant)}, variant, "fields", name);
    }

    fn functionNamed(self: *Decoder, name: []const u8) Fail!bool {
        for (try query.visible(self.r, "function")) |item| if (std.mem.eql(u8, query.nameOf(item), name)) return true;
        return false;
    }

    /// `resolveReferencedFunction`: a callback, worker or function value.
    fn referencedFunction(self: *Decoder, path: []const u8, value: Json, key: []const u8) Fail!query.Value {
        const selector_value = try self.property(value, key);
        if (selector_value == .string) {
            const name = try query.readable(self.r, selector_value.string);
            if (std.mem.eql(u8, name, self.owner.?.name) and !try self.functionNamed(name))
                return self.fail("Function '{s}' is the function being created: only a directCall to it is a self call. A callback, worker or functionReference naming it (and mutual recursion through another new function) needs it to exist, so create it with a placeholder body, create the functions it calls back, then give it the real body with simulateReplaceBodyByName.", .{name});
        }
        return self.selector(try self.at(path, key), selector_value, "function");
    }

    /// `namedCallbackContext`: `[{ parameter, value }]` naming the callback's parameters.
    fn callbackContext(self: *Decoder, path: []const u8, value: Json, name: []const u8, callback: query.Value) Fail![]const Pair {
        if ((try self.tryProperty(value, name)) == null) return &.{};
        var out: std.ArrayList(Pair) = .empty;
        for (try self.array(value, name), 0..) |item, i| {
            const item_path = try self.at(try self.at(path, name), i);
            try self.ensureFields(item_path, item, "callback context", &.{ "parameter", "value" }, &.{ "parameter", "value" });
            const parameter = try self.member("parameter on {s}", .{query.nameOf(callback)}, callback, "parameters", try self.requiredString(item, "parameter"));
            try out.append(self.r.a, .{ .id = query.idOf(parameter), .value = try self.recipe(try self.at(item_path, "value"), try self.property(item, "value")) });
        }
        return out.items;
    }

    /// `WireInput.unionArms`; `owner` resolves variant names (by name), or they are exact IDs. The
    /// `otherwise` arm comes last, naming no variant.
    fn arms(self: *Decoder, path: []const u8, value: Json, single: []const u8, plural: []const u8, owner: ?query.Value) Fail![]const Arm {
        const a = self.r.a;
        var out: std.ArrayList(Arm) = .empty;
        for (try self.array(value, "arms"), 0..) |arm, i| {
            const arm_path = try self.at(try self.at(path, "arms"), i);
            try self.ensureFields(arm_path, arm, "union arm", &.{ single, plural, "body" }, &.{"body"});
            const one, const many = .{ (try self.tryProperty(arm, single)) != null, (try self.tryProperty(arm, plural)) != null };
            var variants: std.ArrayList([]const u8) = .empty;
            if (one and !many) {
                try variants.append(a, try self.variantOf(owner, try self.requiredString(arm, single)));
            } else if (many and !one) {
                const items = try self.array(arm, plural);
                if (items.len == 0) return self.fail("Property '{s}' must name at least one variant.", .{plural});
                for (items) |item| {
                    if (item != .string) return self.fail("Property '{s}' must contain only strings.", .{plural});
                    try variants.append(a, try self.variantOf(owner, try query.readable(self.r, item.string)));
                }
            } else return self.fail("A union arm requires exactly one of '{s}' and '{s}'.", .{ single, plural });
            try out.append(a, .{ .variants = variants.items, .body = try self.recipe(try self.at(arm_path, "body"), try self.property(arm, "body")) });
        }
        if (try self.tryProperty(value, "otherwise")) |otherwise| try out.append(a, .{ .variants = &.{}, .body = try self.recipe(try self.at(path, "otherwise"), otherwise) });
        return out.items;
    }

    fn variantOf(self: *Decoder, owner: ?query.Value, name: []const u8) Fail![]const u8 {
        return if (owner) |type_| query.idOf(try self.unionVariant(type_, name)) else self.stableId("variantId", name);
    }

    fn node(self: *Decoder, kind_name: []const u8, slots: []const Slot) Fail!*const Node {
        const a = self.r.a;
        const case = try a.dupe(u8, kind_name);
        case[0] = std.ascii.toUpper(case[0]);
        const made = try a.create(Node);
        made.* = .{ .case = case, .slots = slots };
        return made;
    }

    /// The by-name `patternReference`: the slot is read before the fields are checked.
    fn namedSlot(self: *Decoder, path: []const u8, value: Json) Fail!*const Node {
        const slot = try self.requiredString(value, "slot");
        var field_id: ?[]const u8 = null;
        if (std.mem.eql(u8, slot, "value") or std.mem.eql(u8, slot, "error")) {
            try self.ensureFields(path, value, "pattern-reference recipe", &.{ "kind", "scopeDepth", "slot" }, &.{ "kind", "scopeDepth", "slot" });
        } else if (std.mem.eql(u8, slot, "field")) {
            const all = &.{ "kind", "scopeDepth", "slot", "type", "variant", "field" };
            try self.ensureFields(path, value, "pattern-reference recipe", all, all);
            const owner = try self.selector(try self.at(path, "type"), try self.property(value, "type"), "type");
            const variant = try self.unionVariant(owner, try self.requiredString(value, "variant"));
            field_id = query.idOf(try self.variantField(variant, try self.requiredString(value, "field")));
        } else return self.fail("Unsupported pattern slot '{s}'.", .{slot});
        const depth = try self.requiredInt(value, "scopeDepth");
        return self.node("patternReference", try self.r.a.dupe(Slot, &.{ .{ .name = "slot", .data = .{ .pattern = .{ .slot = slot, .field_id = field_id } } }, .{ .name = "scopeDepth", .data = .{ .int = depth } } }));
    }

    fn parameterSlot(self: *Decoder, index: usize) Fail!*const Node {
        return self.node("parameterSlot", try self.r.a.dupe(Slot, &.{.{ .name = "index", .data = .{ .int = @intCast(index) } }}));
    }

    /// The by-name `directCall`: a call naming the function being created is a self call.
    fn directCall(self: *Decoder, path: []const u8, value: Json) Fail!*const Node {
        try self.ensureFields(path, value, "direct-call recipe", &.{ "kind", "function", "arguments", "keepUnlisted" }, &.{ "kind", "function", "arguments" });
        const callee_selector = try self.property(value, "function");
        const callee_name: ?[]const u8 = if (callee_selector == .string) try query.readable(self.r, callee_selector.string) else null;
        // Object guards resolve before recursion is classified.
        const selected: ?query.Value = if (callee_selector == .string) null else try self.selector(try self.at(path, "function"), callee_selector, "function");
        const keep = try self.flag(value, "keepUnlisted");
        const owner = self.owner.?;
        const a = self.r.a;
        var arguments: std.ArrayList(Pair) = .empty;
        if (callee_name != null and std.mem.eql(u8, callee_name.?, owner.name)) {
            if (try self.functionNamed(owner.name)) return self.fail("Ambiguous function '{s}'.", .{owner.name});
            const label = try std.fmt.allocPrint(a, "parameter on {s}", .{owner.name});
            var named = try a.alloc(bool, owner.parameters.len);
            @memset(named, false);
            for (try self.array(value, "arguments"), 0..) |item, i| {
                const item_path = try self.at(try self.at(path, "arguments"), i);
                try self.ensureFields(item_path, item, "call argument", &.{ "parameter", "value" }, &.{ "parameter", "value" });
                const slot = try self.exactName(label, try self.requiredString(item, "parameter"), owner.parameters);
                named[slot] = true;
                try arguments.append(a, .{ .slot = @intCast(slot), .value = try self.recipe(try self.at(item_path, "value"), try self.property(item, "value")) });
            }
            if (keep) for (owner.parameters, named, 0..) |name, done, slot| if (!done) {
                try arguments.append(a, .{ .slot = @intCast(slot), .value = try self.parameterSlot(try self.exactName("new function parameter", name, owner.parameters)) });
            };
            return self.node("selfCall", try a.dupe(Slot, &.{.{ .name = "arguments", .data = .{ .pairs = arguments.items } }}));
        } else if (keep) {
            return self.fail("keepUnlisted applies only to a call of the function being created or replaced; '{s}' is another function.", .{callee_name orelse ""});
        } else {
            const callee = selected orelse try self.selector(try self.at(path, "function"), callee_selector, "function");
            for (try self.array(value, "arguments"), 0..) |item, i| {
                const item_path = try self.at(try self.at(path, "arguments"), i);
                try self.ensureFields(item_path, item, "call argument", &.{ "parameter", "value" }, &.{ "parameter", "value" });
                const parameter = try self.member("parameter on {s}", .{query.nameOf(callee)}, callee, "parameters", try self.requiredString(item, "parameter"));
                try arguments.append(a, .{ .id = query.idOf(parameter), .value = try self.recipe(try self.at(item_path, "value"), try self.property(item, "value")) });
            }
            return self.node("directCall", try a.dupe(Slot, &.{ .{ .name = "functionId", .data = .{ .id = query.idOf(callee) } }, .{ .name = "arguments", .data = .{ .pairs = arguments.items } } }));
        }
    }

    fn flag(self: *Decoder, value: Json, name: []const u8) Fail!bool {
        const v = (try self.tryProperty(value, name)) orelse return false;
        if (v != .bool) return self.fail("Property '{s}' must be a JSON boolean.", .{name});
        return v.bool;
    }

    /// Bindings of one target each: call arguments, capability arguments, field values.
    fn targets(self: *Decoder, path: []const u8, value: Json, name: []const u8, context: []const u8, target: []const u8) Fail![]const Pair {
        var out: std.ArrayList(Pair) = .empty;
        for (try self.array(value, name), 0..) |item, i| {
            const item_path = try self.at(try self.at(path, name), i);
            try self.ensureFields(item_path, item, context, &.{ target, "value" }, &.{ target, "value" });
            const target_id = try self.id(item, target);
            try out.append(self.r.a, .{ .id = target_id, .value = try self.recipe(try self.at(item_path, "value"), try self.property(item, "value")) });
        }
        return out.items;
    }

    /// The exact-ID slot a by-name read stands for, or null when the exact form has none (a selector
    /// another read implies).
    fn exactSlot(self: *Decoder, kind_name: []const u8, read: Read) Fail!?[]const u8 {
        const name = switch (read.shape) {
            .fnref => if (std.mem.eql(u8, read.name, "function")) "functionId" else try std.fmt.allocPrint(self.r.a, "{s}FunctionId", .{read.name}),
            .capsel, .capop, .typesel, .variant, .field => try std.fmt.allocPrint(self.r.a, "{s}Id", .{read.name}),
            else => read.name,
        };
        for (kindTable().get(kind_name).?.reads) |exact| if (std.mem.eql(u8, exact.name, name)) return name;
        return null;
    }

    /// The slots in the F# constructor's order (the exact table's read order unless it says otherwise).
    fn ordered(self: *Decoder, kind_name: []const u8, slots: []const Slot) Fail!*const Node {
        const kind = kindTable().get(kind_name).?;
        var out: std.ArrayList(Slot) = .empty;
        if (kind.order) |order| {
            for (order) |name| for (slots) |slot| if (std.mem.eql(u8, slot.name, name)) try out.append(self.r.a, slot);
        } else for (kind.reads) |read| for (slots) |slot| if (std.mem.eql(u8, slot.name, read.name)) try out.append(self.r.a, slot);
        return self.node(kind_name, out.items);
    }

    /// `WireRecipe.recipeIn` (after `WireRecipeNames.resolve`) of an exact-ID recipe, or
    /// `WireSymbolic.recipe` of a by-name one.
    pub fn recipe(self: *Decoder, path: []const u8, value: Json) Fail!*const Node {
        const kind_name = try self.requiredString(value, "kind");
        const eql = std.mem.eql;
        const kind = if (self.owner == null)
            kindTable().get(kind_name) orelse return self.fail("Unsupported expression recipe kind '{s}'.", .{kind_name})
        else blk: {
            if (eql(u8, kind_name, "patternReference")) return self.namedSlot(path, value);
            if (eql(u8, kind_name, "directCall")) return self.directCall(path, value);
            if (byNameTable().get(kind_name)) |kind| break :blk kind;
            // `WireRecipeCommon.tryReferenceFree`: the exact kinds but the two the by-name surface spells as `parameter` and `directCall`.
            if (!eql(u8, kind_name, "parameterSlot") and !eql(u8, kind_name, "selfCall")) if (kindTable().get(kind_name)) |kind| break :blk kind;
            return self.fail("Unsupported symbolic expression recipe kind '{s}'.", .{kind_name});
        };
        try self.ensureFields(path, value, kind.context, kind.allowed, kind.required);
        const a = self.r.a;
        // What earlier reads of this recipe resolved, for the later ones.
        var callbacks: [2]struct { name: []const u8, function: query.Value } = undefined;
        var callback_count: usize = 0;
        var capability: ?query.Value = null;
        var operation: ?query.Value = null;
        var owner: ?query.Value = null;
        var variant: ?query.Value = null;
        var slots: std.ArrayList(Slot) = .empty;
        for (kind.reads) |read| {
            const name = read.name;
            const here = try self.at(path, name);
            const data: ?Data = switch (read.shape) {
                .pname => return self.parameterSlot(try self.exactName("new function parameter", try self.requiredString(value, name), self.owner.?.parameters)),
                .fnref => blk: {
                    const function = try self.referencedFunction(path, value, name);
                    callbacks[callback_count] = .{ .name = name, .function = function };
                    callback_count += 1;
                    break :blk .{ .id = query.idOf(function) };
                },
                .ctx => blk: {
                    for (callbacks[0..callback_count]) |callback| {
                        if (eql(u8, callback.name, read.callback)) break :blk .{ .pairs = try self.callbackContext(path, value, name, callback.function) };
                    }
                    break :blk null;
                },
                .capsel => blk: {
                    capability = try self.selector(here, try self.property(value, name), "capability");
                    break :blk .{ .id = query.idOf(capability.?) };
                },
                .capop => blk: {
                    operation = try self.member("operation on {s}", .{query.nameOf(capability.?)}, capability.?, "operations", try self.requiredString(value, name));
                    break :blk .{ .id = query.idOf(operation.?) };
                },
                .typesel => blk: {
                    owner = try self.selector(here, try self.property(value, name), "type");
                    break :blk .{ .id = query.idOf(owner.?) };
                },
                .variant => blk: {
                    variant = try self.unionVariant(owner.?, try self.requiredString(value, name));
                    break :blk .{ .id = query.idOf(variant.?) };
                },
                .field => .{ .id = query.idOf(try self.productField(owner.?, try self.requiredString(value, name))) },
                .pfields, .ufields, .scapargs => blk: {
                    var pairs: std.ArrayList(Pair) = .empty;
                    for (try self.array(value, name), 0..) |item, i| {
                        const item_path = try self.at(here, i);
                        const target = if (read.shape == .scapargs) "parameter" else "field";
                        const context = switch (read.shape) {
                            .pfields => "product field recipe",
                            .ufields => "union field recipe",
                            else => "capability argument",
                        };
                        try self.ensureFields(item_path, item, context, &.{ target, "value" }, &.{ target, "value" });
                        const wanted = try self.requiredString(item, target);
                        const bound = switch (read.shape) {
                            .pfields => try self.productField(owner.?, wanted),
                            .ufields => try self.variantField(variant.?, wanted),
                            else => try self.member("parameter on operation {s}", .{query.nameOf(operation.?)}, operation.?, "parameters", wanted),
                        };
                        try pairs.append(a, .{ .id = query.idOf(bound), .value = try self.recipe(try self.at(item_path, "value"), try self.property(item, "value")) });
                    }
                    break :blk .{ .pairs = pairs.items };
                },
                .simpls => blk: {
                    var impls: std.ArrayList(Implementation) = .empty;
                    for (try self.array(value, name), 0..) |item, i| {
                        const item_path = try self.at(here, i);
                        try self.ensureFields(item_path, item, "Capability implementation", &.{ "operation", "function" }, &.{ "operation", "function" });
                        const op = try self.member("operation on {s}", .{query.nameOf(capability.?)}, capability.?, "operations", try self.requiredString(item, "operation"));
                        try impls.append(a, .{ .operation = query.idOf(op), .function = query.idOf(try self.referencedFunction(item_path, item, "function")) });
                    }
                    break :blk .{ .implementations = impls.items };
                },
                .sarms => .{ .arms = try self.arms(path, value, "variant", "variants", owner.?) },
                .recipe => .{ .recipe = try self.recipe(here, try self.property(value, name)) },
                .recipes => blk: {
                    const items = try self.array(value, name);
                    const out = try a.alloc(*const Node, items.len);
                    for (items, 0..) |item, i| out[i] = try self.recipe(try self.at(here, i), item);
                    break :blk .{ .recipes = out };
                },
                .optrecipe => if (try self.tryProperty(value, name)) |item| .{ .recipe = try self.recipe(here, item) } else null,
                .id => .{ .id = try self.id(value, name) },
                .type => .{ .type = try self.anyType(here, try self.property(value, name)) },
                .types => .{ .types = try self.typeArguments(path, value, name) },
                .int => .{ .int = try self.requiredInt(value, name) },
                .int64 => .{ .int = try self.requiredInt64(value, name) },
                .bool => blk: {
                    const v = try self.property(value, name);
                    if (v != .bool) return self.fail("Property '{s}' must be a JSON boolean.", .{name});
                    break :blk .{ .bool = v.bool };
                },
                .text => blk: {
                    const v = try self.property(value, name);
                    if (v != .string) return self.fail("A String run value must be a JSON string.", .{});
                    if (query.masked and std.mem.indexOf(u8, v.string, query.lone_mark) != null)
                        return self.fail("A String must not contain an unpaired surrogate escape; a String is a sequence of Unicode scalar values.", .{});
                    break :blk .{ .text = v.string };
                },
                .bits => blk: {
                    const bits = try self.requiredString(value, name);
                    // `String.Length`: UTF-16 units.
                    if (try std.unicode.calcUtf16LeLen(bits) != 16) return self.fail("Invalid Float64 bits: Float64 bits must contain exactly 16 hexadecimal digits..", .{});
                    // `UInt64.TryParse` ignores trailing NUL characters, which the length above counts.
                    const digits = std.mem.trimEnd(u8, bits, "\x00");
                    const parsed = std.fmt.parseInt(u64, digits, 16) catch return self.fail("Invalid Float64 bits: Float64 bits contain invalid hexadecimal digits..", .{});
                    for (digits) |c| if (!std.ascii.isHex(c)) return self.fail("Invalid Float64 bits: Float64 bits contain invalid hexadecimal digits..", .{});
                    break :blk .{ .bits = parsed };
                },
                .context => if ((try self.tryProperty(value, name)) != null) .{ .pairs = try self.targets(path, value, name, "call argument", "parameterId") } else .{ .pairs = &.{} },
                .args => .{ .pairs = try self.targets(path, value, name, "call argument", "parameterId") },
                .capargs => .{ .pairs = try self.targets(path, value, name, "capability argument", "capabilityParameterId") },
                .fields => .{ .pairs = try self.targets(path, value, name, "field recipe", "fieldId") },
                .mode => blk: {
                    const mode = try self.requiredString(value, "mode");
                    if (!std.mem.eql(u8, mode, "any") and !std.mem.eql(u8, mode, "all")) return self.fail("Unsupported task wait mode '{s}'; expected 'any' or 'all'.", .{mode});
                    break :blk .{ .text = mode };
                },
                .slot => blk: {
                    const slot = try self.requiredString(value, "slot");
                    var field_id: ?[]const u8 = null;
                    if (std.mem.eql(u8, slot, "field")) {
                        const by_id, const by_name = .{ (try self.tryProperty(value, "fieldId")) != null, (try self.tryProperty(value, "field")) != null };
                        if (by_id and !by_name) {
                            field_id = try self.id(value, "fieldId");
                        } else if (by_name and !by_id) {
                            const field = try self.requiredString(value, "field");
                            return self.fail("Pattern reference by field name '{s}' is available only in an addUnionVariant arm body; use fieldId.", .{field});
                        } else return self.fail("A field pattern reference takes exactly one of fieldId and field.", .{});
                    } else if (!std.mem.eql(u8, slot, "value") and !std.mem.eql(u8, slot, "error")) return self.fail("Unsupported pattern slot '{s}'.", .{slot});
                    break :blk .{ .pattern = .{ .slot = slot, .field_id = field_id } };
                },
                .selfargs => blk: {
                    var pairs: std.ArrayList(Pair) = .empty;
                    for (try self.array(value, "arguments"), 0..) |item, i| {
                        const item_path = try self.at(try self.at(path, "arguments"), i);
                        try self.ensureFields(item_path, item, "self-call argument", &.{ "slot", "value" }, &.{ "slot", "value" });
                        const slot = try self.requiredInt(item, "slot");
                        try pairs.append(a, .{ .slot = slot, .value = try self.recipe(try self.at(item_path, "value"), try self.property(item, "value")) });
                    }
                    break :blk .{ .pairs = pairs.items };
                },
                .impls => blk: {
                    var impls: std.ArrayList(Implementation) = .empty;
                    for (try self.array(value, "implementations"), 0..) |item, i| {
                        try self.ensureFields(try self.at(try self.at(path, "implementations"), i), item, "Capability implementation", &.{ "operationId", "functionId" }, &.{ "operationId", "functionId" });
                        const op = try self.id(item, "operationId");
                        try impls.append(a, .{ .operation = op, .function = try self.id(item, "functionId") });
                    }
                    break :blk .{ .implementations = impls.items };
                },
                .arms => .{ .arms = try self.arms(path, value, "variantId", "variantIds", null) },
            };
            if (data) |d| if (try self.exactSlot(kind_name, read)) |slot_name| try slots.append(a, .{ .name = slot_name, .data = d });
        }
        return self.ordered(kind_name, slots.items);
    }
};

pub fn isBlank(a: Allocator, text: []const u8) !bool {
    _ = a;
    var it = std.unicode.Utf8View.initUnchecked(text).iterator();
    while (it.nextCodepoint()) |c| switch (c) {
        0x09...0x0D, 0x20, 0x85, 0xA0, 0x1680, 0x2000...0x200A, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000 => {},
        else => return false,
    };
    return true;
}

// ---------------------------------------------------------------- named pattern references

/// `WireRecipeNames.resolve`: before decoding, a `patternReference` by `name` becomes the
/// `scopeDepth` and `slot` of the innermost binder in the same recipe that declares the name, and
/// the binder names are dropped. Frames are pushed as inference pushes them.
pub fn resolveNames(d: *Decoder, value: *Json, raw: []const u8) Fail!void {
    // Only a recipe whose text names a binder or a reference is rewritten (`resolve`'s guard).
    if (std.mem.indexOf(u8, raw, "Name\"") == null and std.mem.indexOf(u8, raw, "\"name\"") == null) return;
    if (try hasDuplicates(d, "/body", value.*)) return d.fail("JSON object contains duplicate property names.", .{});
    var frames: std.ArrayList(?Binder) = .empty;
    try visit(d, &frames, value);
    // `ToJsonString` then reads every string of the copy again.
    if (query.masked and std.mem.indexOf(u8, raw, "\\ufdd0") != null) _ = try query.readable(d.r, query.lone_mark);
}

/// `WireRecipeNames.hasDuplicates`: in document order, each object's names are read (which throws on a
/// lone surrogate) and checked before its values are.
fn hasDuplicates(d: *Decoder, path: []const u8, value: Json) Fail!bool {
    switch (value) {
        .object => |map| {
            try query.lookable(d.r, map);
            if (d.duplicates.contains(path)) return true;
            for (map.keys(), map.values()) |key, item|
                if (try hasDuplicates(d, try std.fmt.allocPrint(d.r.a, "{s}/{s}", .{ path, key }), item)) return true;
        },
        .array => |items| for (items.items, 0..) |item, index|
            if (try hasDuplicates(d, try std.fmt.allocPrint(d.r.a, "{s}/{d}", .{ path, index }), item)) return true,
        else => {},
    }
    return false;
}

const Binder = struct { name: []const u8, slot: []const u8 };

fn takeName(d: *Decoder, map: *std.json.ObjectMap, key: []const u8) Fail!?[]const u8 {
    const entry = map.fetchOrderedRemove(key) orelse return null;
    if (entry.value != .string) return d.fail("Property '{s}' must be a binding name string.", .{key});
    return try query.readable(d.r, entry.value.string);
}

fn kindOf(map: std.json.ObjectMap) []const u8 {
    const kind = map.get("kind") orelse return "";
    return if (kind == .string) kind.string else "";
}

fn visitChild(d: *Decoder, frames: *std.ArrayList(?Binder), map: *std.json.ObjectMap, key: []const u8, frame: ?Binder) Fail!void {
    const child = map.getPtr(key) orelse return;
    if (child.* == .null) return;
    try frames.insert(d.r.a, 0, frame);
    defer _ = frames.orderedRemove(0);
    try visit(d, frames, child);
}

fn visitRest(d: *Decoder, frames: *std.ArrayList(?Binder), map: *std.json.ObjectMap, skip: []const []const u8) Fail!void {
    for (map.keys(), map.values()) |key, *child| {
        for (skip) |s| {
            if (std.mem.eql(u8, s, key)) break;
        } else if (child.* != .null) try visit(d, frames, child);
    }
}

fn visit(d: *Decoder, frames: *std.ArrayList(?Binder), value: *Json) Fail!void {
    const a = d.r.a;
    switch (value.*) {
        .array => |*items| for (items.items) |*item| try visit(d, frames, item),
        .object => |*map| {
            const kind = kindOf(map.*);
            const eql = std.mem.eql;
            if (eql(u8, kind, "patternReference")) {
                const name = (try takeName(d, map, "name")) orelse return;
                if (map.get("scopeDepth") != null or map.get("slot") != null) return d.fail("Pattern reference '{s}' gives both a name and scopeDepth or slot; use one form.", .{name});
                for (frames.items, 0..) |frame, depth| if (frame) |f| if (eql(u8, f.name, name)) {
                    try map.put(a, "scopeDepth", .{ .integer = @intCast(depth) });
                    try map.put(a, "slot", .{ .string = f.slot });
                    return;
                };
                var listed: std.ArrayList([]const u8) = .empty;
                for (frames.items) |frame| if (frame) |f| {
                    for (listed.items) |seen| {
                        if (eql(u8, seen, f.name)) break;
                    } else try listed.append(a, f.name);
                };
                return d.fail("No binder in this recipe names '{s}' (names in scope: {s}). A binding outside the recipe still needs scopeDepth; nyrwvy_inspect view scopes (op bindingScopes) lists the bindings visible at a node with their scopeDepth.", .{ name, if (listed.items.len == 0) "none" else try std.mem.join(a, ", ", listed.items) });
            } else if (eql(u8, kind, "bind")) {
                const name = try takeName(d, map, "name");
                try visitRest(d, frames, map, &.{"body"});
                try visitChild(d, frames, map, "body", if (name) |n| .{ .name = n, .slot = "value" } else null);
            } else if (eql(u8, kind, "matchOption")) {
                const name = try takeName(d, map, "someName");
                try visitRest(d, frames, map, &.{ "someBody", "noneBody" });
                try visitChild(d, frames, map, "someBody", if (name) |n| .{ .name = n, .slot = "value" } else null);
                try visitChild(d, frames, map, "noneBody", null);
            } else if (eql(u8, kind, "matchResult")) {
                const ok_name = try takeName(d, map, "okName");
                const err_name = try takeName(d, map, "errName");
                try visitRest(d, frames, map, &.{ "okBody", "errBody" });
                try visitChild(d, frames, map, "okBody", if (ok_name) |n| .{ .name = n, .slot = "value" } else null);
                try visitChild(d, frames, map, "errBody", if (err_name) |n| .{ .name = n, .slot = "error" } else null);
            } else if (eql(u8, kind, "matchUnion")) {
                try visitRest(d, frames, map, &.{ "arms", "otherwise" });
                if (map.getPtr("arms")) |arms| {
                    if (arms.* == .array) {
                        for (arms.array.items) |*arm| {
                            if (arm.* != .object) {
                                try visit(d, frames, arm);
                                continue;
                            }
                            // In key order; only the body is a new frame.
                            for (arm.object.keys(), arm.object.values()) |key, *child| {
                                if (child.* == .null) continue;
                                if (std.mem.eql(u8, key, "body")) {
                                    try frames.insert(a, 0, null);
                                    defer _ = frames.orderedRemove(0);
                                    try visit(d, frames, child);
                                } else try visit(d, frames, child);
                            }
                        }
                    } else if (arms.* != .null) try visit(d, frames, arms);
                }
                try visitChild(d, frames, map, "otherwise", null);
            } else try visitRest(d, frames, map, &.{});
        },
        else => {},
    }
}

/// The raw text of the request root's `key` value, as the line spells it.
pub fn rawProperty(a: Allocator, line: []const u8, key: []const u8) ![]const u8 {
    var scanner = std.json.Scanner.initCompleteInput(a, line);
    var depth: usize = 0;
    // At the root object: whether the next string is a name, and where the wanted value starts.
    var name_next = false;
    var start: ?usize = null;
    while (true) {
        const token = try scanner.nextAlloc(a, .alloc_always);
        var ended = false;
        switch (token) {
            .end_of_document => return "",
            .object_begin, .array_begin => {
                depth += 1;
                name_next = depth == 1;
            },
            .object_end, .array_end => {
                depth -= 1;
                ended = depth == 1;
            },
            .allocated_string => |text| {
                if (depth == 1 and name_next) {
                    name_next = false;
                    if (start == null and std.mem.eql(u8, text, key)) start = scanner.cursor;
                } else ended = depth == 1;
            },
            else => ended = depth == 1,
        }
        if (ended) {
            if (start) |s| return std.mem.trim(u8, line[s..scanner.cursor], " \t\r\n:");
            name_next = true;
        }
    }
}
