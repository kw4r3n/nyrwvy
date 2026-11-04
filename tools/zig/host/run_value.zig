//! runPure wire values, validation and generated-runner transport. All storage belongs to the request.
const std = @import("std");
const model = @import("model_reader");
const q = @import("query.zig");
const recipe = @import("recipe.zig");
const infer = @import("infer.zig");
const mat = @import("materialize.zig");
const preflight = @import("preflight.zig");
const Json = std.json.Value;
const Type = recipe.TypeRef;
const Model = model.Model;
const tm = model.session_model;
const Allocator = std.mem.Allocator;
const Writer = std.Io.Writer;
const Fail = anyerror;
const same = mat.same;
pub const Field = struct { id: []const u8, value: RunValue };
pub const Entry = struct { key: RunValue, value: RunValue };
pub const Response = struct { kind: enum { ret, throw, cancellation }, value: ?*RunValue = null, message: []const u8 = "", delay: i64 = 0 };
pub const Case = struct { arguments: []Field, responses: []Response };
pub const Operation = struct { id: []const u8, responses: []Response = &.{}, cases: ?[]Case = null };
pub const Kind = enum { string, boolean, int64, float64, bits32, bytes, unit, list, set, map, option, result, product, union_value, fake, host };
pub const RunValue = struct {
    kind: Kind,
    type: Type,
    legacy: bool = false,
    text: []const u8 = "",
    integer: i64 = 0,
    bits: u64 = 0,
    boolean: bool = false,
    items: []RunValue = &.{},
    fields: []Field = &.{},
    entries: []Entry = &.{},
    payload: ?*RunValue = null,
    variant: []const u8 = "",
    operations: []Operation = &.{},
};
pub const CapturedCall = struct { parameter_id: []const u8, operation_id: []const u8, arguments: []const Field };
pub const Capture = struct { actual: ?RunValue = null, calls: []const CapturedCall = &.{} };
pub const Input = struct {
    function_id: []const u8,
    declarations: Model,
    fn_value: ?Model = null,
    arguments: []RunValue,
    expected: ?RunValue,
    host_sources: []const []const u8,
    host_source_texts: []const []const u8 = &.{},
    has_host_binding: bool = false,
    waiting_responses: bool = false,
    csharp_symbols: Json = .null,
    capture: ?*Capture = null,
};
fn primitive(name: []const u8) Type {
    return .{ .primitive = name };
}
fn generic(a: Allocator, name: []const u8, arguments: []const Type) !Type {
    return .{ .builtin = .{ .generic = name, .arguments = try a.dupe(Type, arguments) } };
}
fn product(v: Model) bool {
    return v.data.type_declaration.product;
}
pub fn find(items: []const Model, id: []const u8) ?Model {
    for (items) |item| if (same(q.idOf(item), id)) return item;
    return null;
}
pub fn sorted(a: Allocator, items: []const Model) ![]Model {
    const out = try a.dupe(Model, items);
    std.mem.sort(Model, out, {}, struct {
        fn less(_: void, x: Model, y: Model) bool {
            return q.ordinal({}, q.idOf(x), q.idOf(y));
        }
    }.less);
    return out;
}
fn sortFields(fields: []Field) void {
    std.mem.sort(Field, fields, {}, struct {
        fn less(_: void, x: Field, y: Field) bool {
            return q.ordinal({}, x.id, y.id);
        }
    }.less);
}
fn box(a: Allocator, value: RunValue) !*RunValue {
    const out = try a.create(RunValue);
    out.* = value;
    return out;
}
const dummy_node: recipe.Node = .{ .case = "", .slots = &.{} };
fn inference(r: *q.Request) infer.Infer {
    return .{ .r = r, .a = r.a, .local = &.{}, .fresh = &.{}, .c = .{ .expected_snapshot_id = "", .display_name = "", .type_parameters = null, .parameters = &.{}, .result_type = primitive("unit"), .required_effects = &.{}, .body = &dummy_node } };
}
pub fn instantiated(r: *q.Request, owner: Model, args: []const Type, field: Model) !Type {
    var context = inference(r);
    return context.instantiate(owner, args, try infer.typeOf(r.a, field.declaredType()));
}
fn typeValue(r: *q.Request, t: Type) !model.Value {
    var context = inference(r);
    var m: mat.Materializer = .{ .a = r.a, .r = r, .types = &context, .fresh = &.{}, .function_id = tm.none, .parameters = &.{} };
    var wire_out: Writer.Allocating = .init(r.a);
    try @import("wire.zig").typedTypeRef(&wire_out.writer, r.interner.?, try m.typeRef(t), &.{});
    return model.parseValue(r.a, wire_out.written());
}
fn boolJson(v: bool) model.Value {
    return if (v) .json_true else .json_false;
}
fn object(r: *q.Request, pairs: anytype) !model.Value {
    var out: std.ArrayList(model.Member) = .empty;
    inline for (pairs) |pair| try out.append(r.a, try mat.member(r.a, pair[0], pair[1]));
    return .{ .object = out.items };
}
fn named(r: *q.Request, out: *std.ArrayList(model.Member), key: []const u8, id: []const u8) !void {
    const root = r.typedModel();
    for (root.types()) |owner| {
        if (same(q.idOf(owner), id)) {
            try out.append(r.a, try mat.member(r.a, key, try mat.string(r.a, q.nameOf(owner))));
            return;
        }
        for (owner.fields()) |field| if (same(q.idOf(field), id)) {
            try out.append(r.a, try mat.member(r.a, key, try mat.string(r.a, q.nameOf(field))));
            return;
        };
        for (owner.variants()) |variant| {
            if (same(q.idOf(variant), id)) {
                try out.append(r.a, try mat.member(r.a, key, try mat.string(r.a, q.nameOf(variant))));
                return;
            }
            for (variant.fields()) |field| if (same(q.idOf(field), id)) {
                try out.append(r.a, try mat.member(r.a, key, try mat.string(r.a, q.nameOf(field))));
                return;
            };
        }
    }
    for (root.functions()) |owner| {
        if (same(q.idOf(owner), id)) {
            try out.append(r.a, try mat.member(r.a, key, try mat.string(r.a, q.nameOf(owner))));
            return;
        }
        for (owner.parameters()) |p| if (same(q.idOf(p), id)) {
            try out.append(r.a, try mat.member(r.a, key, try mat.string(r.a, q.nameOf(p))));
            return;
        };
    }
    for (root.capabilities()) |owner| {
        if (same(q.idOf(owner), id)) {
            try out.append(r.a, try mat.member(r.a, key, try mat.string(r.a, q.nameOf(owner))));
            return;
        }
        for (owner.operations()) |op| {
            if (same(q.idOf(op), id)) {
                try out.append(r.a, try mat.member(r.a, key, try mat.string(r.a, q.nameOf(op))));
                return;
            }
            for (op.parameters()) |p| if (same(q.idOf(p), id)) {
                try out.append(r.a, try mat.member(r.a, key, try mat.string(r.a, q.nameOf(p))));
                return;
            };
        }
    }
}
pub fn declarationView(r: *q.Request) !Model {
    const g = try r.a.create(tm.Generation);
    g.* = r.generation.?.*;
    inline for (.{ .{ "function", "functions", tm.Function }, .{ "type", "types", tm.Type }, .{ "effect", "effects", tm.Effect }, .{ "capability", "capabilities", tm.Capability } }) |entry| {
        const visible = try q.typedVisible(r, entry[0]);
        const records = try r.a.alloc(*const entry[2], visible.len);
        for (visible, records) |item, *record| record.* = @field(item.data, if (entry[2] == tm.Type) "type_declaration" else entry[0]);
        @field(g, entry[1]) = records;
    }
    return model.typedRoot(r.a, r.interner.?, g);
}

pub fn diagnostic(r: *q.Request, input: *const Input, code: []const u8, message: []const u8) Fail!void {
    const original_model = r.generation;
    r.generation = input.declarations.data.snapshot;
    defer r.generation = original_model;
    var fields: std.ArrayList(model.Member) = .empty;
    try fields.append(r.a, try mat.member(r.a, "functionId", try mat.string(r.a, input.function_id)));
    try named(r, &fields, "function", input.function_id);
    var out: Writer.Allocating = .init(r.a);
    for (fields.items) |field| {
        try out.writer.writeByte(',');
        try model.writeString(&out.writer, field.name);
        try out.writer.writeByte(':');
        try mat.write(&out.writer, field.value);
    }
    r.rejection = .{ .code = code, .message = message, .fields = out.written() };
    return error.Rejected;
}
const unpaired = "A String must not contain an unpaired surrogate escape; a String is a sequence of Unicode scalar values.";
const float_shape = "A Float64 value must be a JSON number or one of \"NaN\", \"Infinity\", \"-Infinity\" and \"-0\".";
fn integer(v: Json) ?i64 {
    const s = switch (v) {
        .integer => |n| return n,
        .number_string, .string => |text| std.mem.trimEnd(u8, text, "\x00"),
        else => return null,
    };
    const digits = if (s.len > 0 and (s[0] == '-' or s[0] == '+')) s[1..] else s;
    if (digits.len == 0) return null;
    for (digits) |c| if (!std.ascii.isDigit(c)) return null;
    return std.fmt.parseInt(i64, s, 10) catch null;
}
const Decoder = struct {
    d: recipe.Decoder,
    names: bool,
    remaining: i64 = 8192,
    top: []const u8 = "",
    order: []const u8 = "",
    op: []const u8 = "",
    selector_failure: bool = false,
    fn r(self: *Decoder) *q.Request {
        return self.d.r;
    }
    fn fmt(self: *Decoder, comptime format: []const u8, args: anytype) ![]const u8 {
        return std.fmt.allocPrint(self.r().a, format, args);
    }
    fn at(self: *Decoder, path: []const u8, key: anytype) ![]const u8 {
        return switch (@TypeOf(key)) {
            usize => self.fmt("{s}/{d}", .{ path, key }),
            else => self.fmt("{s}/{s}", .{ path, key }),
        };
    }
    fn fail(self: *Decoder, comptime format: []const u8, args: anytype) Fail {
        return self.r().fail(format, args);
    }
    fn charge(self: *Decoder, n: usize) !void {
        self.remaining -= @intCast(n);
        if (self.remaining < 0) return self.fail("Run values may contain at most 8192 value nodes per argument or expected value. At {s}: at least {d} nodes were charged, including containers and their values. Split the input into smaller batches or build it inside the graph and return an aggregate.", .{ self.top, 8192 - self.remaining });
    }
    fn boolean(self: *Decoder, item: Json, key: []const u8) !bool {
        const value = try self.d.property(item, key);
        if (value != .bool) return self.fail("Property '{s}' must be a JSON boolean.", .{key});
        return value.bool;
    }
    fn int(self: *Decoder, item: Json, key: []const u8) !i64 {
        return integer(try self.d.property(item, key)) orelse self.fail("Property '{s}' must be a 64-bit integer or exact decimal integer string.", .{key});
    }
    fn text(self: *Decoder, value: Json) ![]const u8 {
        if (value != .string) return self.fail("A String run value must be a JSON string.", .{});
        if (q.masked and std.mem.indexOf(u8, value.string, q.lone_mark) != null) return self.fail("{s}", .{unpaired});
        return value.string;
    }
    fn stableId(self: *Decoder, item: Json, key: []const u8) ![]const u8 {
        return self.d.stableId(key, try self.d.requiredString(item, key));
    }
    fn selector(self: *Decoder, path: []const u8, value: Json, kind: []const u8) !Model {
        return self.d.selector(path, value, kind);
    }
    fn member(self: *Decoder, comptime label: []const u8, args: anytype, parent: Model, group: []const u8, name: []const u8) !Model {
        return self.d.member(label, args, parent, group, name);
    }
    fn protectedMember(self: *Decoder, comptime label: []const u8, args: anytype, parent: Model, group: []const u8, name: []const u8) !Model {
        return self.member(label, args, parent, group, name) catch |e| {
            self.selector_failure = true;
            return e;
        };
    }
    fn typeRef(self: *Decoder, path: []const u8, item: Json) !Type {
        return self.d.anyType(path, item);
    }
    fn types(self: *Decoder, path: []const u8, item: Json, key: []const u8) ![]const Type {
        if (item.object.get(key) == null) return &.{};
        const values = try self.d.array(item, key);
        const out = try self.r().a.alloc(Type, values.len);
        for (values, out, 0..) |value, *slot, i| slot.* = try self.typeRef(try self.at(try self.at(path, key), i), value);
        return out;
    }
    fn leaf(self: *Decoder, kind: []const u8, value: Json, location: []const u8, is_plain: bool) !RunValue {
        if (same(kind, "string")) {
            if (is_plain and value != .string) return self.fail("Expected a JSON string at {s}.", .{location});
            return .{ .kind = .string, .type = primitive(kind), .text = try self.text(value) };
        }
        if (same(kind, "bool")) {
            if (value != .bool) return if (is_plain) self.fail("Expected a JSON boolean at {s}.", .{location}) else self.fail("Property 'value' must be a JSON boolean.", .{});
            return .{ .kind = .boolean, .type = primitive(kind), .boolean = value.bool };
        }
        if (same(kind, "int64")) return .{ .kind = .int64, .type = primitive(kind), .integer = integer(value) orelse return if (is_plain) self.fail("Expected an exact integer or decimal integer string at {s}.", .{location}) else self.fail("Property 'value' must be a 64-bit integer or exact decimal integer string.", .{}) };
        if (same(kind, "float64")) {
            const number: f64 = switch (value) {
                .integer => |n| @floatFromInt(n),
                .float => |n| n,
                .number_string => |s| std.fmt.parseFloat(f64, s) catch return self.fail("{s}", .{float_shape}),
                .string => |s| if (same(s, "NaN")) std.math.nan(f64) else if (same(s, "Infinity")) std.math.inf(f64) else if (same(s, "-Infinity")) -std.math.inf(f64) else if (same(s, "-0")) -0.0 else return self.fail("{s}", .{float_shape}),
                else => return self.fail("{s}", .{float_shape}),
            };
            if (value != .string and !std.math.isFinite(number)) return self.fail("A Float64 number must be within the finite binary64 range; write \"Infinity\" or \"-Infinity\" as a string.", .{});
            return .{ .kind = .float64, .type = primitive(kind), .bits = if (std.math.isNan(number)) 0x7ff8000000000000 else @bitCast(number) };
        }
        if (same(kind, "bits32")) {
            const n = integer(value) orelse return self.fail("A Bits32 value must be an integer from 0 to 4294967295, as a JSON number or an exact decimal string.", .{});
            if (n < 0 or n > std.math.maxInt(u32) or (value == .number_string and value.number_string.len > 0 and value.number_string[0] == '-') or (value == .string and (value.string.len == 0 or value.string[0] == '+' or value.string[0] == '-'))) return self.fail("A Bits32 value must be an integer from 0 to 4294967295, as a JSON number or an exact decimal string.", .{});
            return .{ .kind = .bits32, .type = primitive(kind), .integer = n };
        }
        if (same(kind, "bytes")) {
            if (value != .string or value.string.len % 2 != 0) return self.fail("A Bytes value must be a string of hexadecimal digit pairs, such as \"00ff7f\".", .{});
            const bytes = try self.r().a.alloc(u8, value.string.len / 2);
            _ = std.fmt.hexToBytes(bytes, value.string) catch return self.fail("A Bytes value must be a string of hexadecimal digit pairs, such as \"00ff7f\".", .{});
            return .{ .kind = .bytes, .type = primitive(kind), .text = bytes };
        }
        if (value != .null) return self.fail("Expected null for Unit at {s}.", .{location});
        return .{ .kind = .unit, .type = primitive("unit") };
    }
    fn shape(item: Json) []const u8 {
        if (item != .object) return "a non-null typed run-value object";
        const k = item.object.get("kind") orelse return "a typed run-value object with a string kind";
        if (k != .string) return "a typed run-value object with a string kind";
        if (same(k.string, "result")) return "Result {kind, okType, errorType, isOk, value} with a typed run-value payload, or the legacy scalar {kind, okKind, errorKind, isOk, value} with a raw string/bool/int64 payload";
        if (same(k.string, "string")) return "String {kind: string, value: JSON string}";
        if (same(k.string, "bool")) return "Bool {kind: bool, value: JSON boolean}";
        if (same(k.string, "int64")) return "Int64 {kind: int64, value: integer or exact decimal integer string}";
        if (same(k.string, "unit")) return "Unit {kind: unit} without a value property";
        return "a typed run value with the required fields for its kind";
    }
    fn wrap(self: *Decoder, location: []const u8, expected: []const u8, e: anyerror) anyerror {
        const rejection = &self.r().rejection;
        if (e != error.Rejected or self.selector_failure or !same(rejection.code, "ProtocolError") or std.mem.indexOf(u8, location, ".operations[") == null or std.mem.startsWith(u8, rejection.message, "Invalid run value at ") or std.mem.startsWith(u8, rejection.message, "Run values may contain at most 8192")) return e;
        const reasons = [_][]const u8{ "Run values may have at most 32 nested composite layers.", "Fake delayMillis must not be negative.", "Lists may contain at most 4096 items.", "List run values may contain at most 4096 items.", "Sets may contain at most 4096 items.", "Maps may contain at most 4096 entries.", "A Map run value names a key more than once.", "Named run values may contain at most 256 fields.", "A fake capability may script at most 256 operations.", "A fake operation may script at most 256 responses.", "A fake operation may script at most 256 cases.", "A fake case may contain at most 256 arguments.", "A fake operation may script at most 256 total responses.", unpaired };
        var reason: []const u8 = "";
        for (reasons) |candidate| if (same(candidate, rejection.message)) {
            reason = std.fmt.allocPrint(self.r().a, "{s} ", .{candidate}) catch return error.OutOfMemory;
            break;
        };
        rejection.message = std.fmt.allocPrint(self.r().a, "Invalid run value at {s}. {s}Expected {s}.", .{ location, reason, expected }) catch return error.OutOfMemory;
        return e;
    }
    fn fakeMetadata(self: *Decoder, loc: []const u8, item: Json) !usize {
        const operations = try self.d.array(item, "operations");
        var count: usize = 0;
        for (operations, 0..) |operation_json, i| {
            const path = try self.fmt("{s}.operations[{d}]", .{ loc, i });
            count += self.operationMetadata(path, operation_json) catch |e| return self.wrap(path, "a fake operation with a responses array or a cases array", e);
        }
        return count;
    }
    fn operationMetadata(self: *Decoder, loc: []const u8, item: Json) !usize {
        if (item != .object) return self.fail("Expected a JSON object.", .{});
        var count: usize = 0;
        if (item.object.get("cases") != null) {
            for (try self.d.array(item, "cases"), 0..) |case, i| {
                const path = try self.fmt("{s}.cases[{d}]", .{ loc, i });
                count += self.caseMetadata(case) catch |e| return self.wrap(path, "a fake case with arguments and responses arrays", e);
            }
        } else {
            for (try self.d.array(item, "responses"), 0..) |response_json, i| {
                const path = try self.fmt("{s}.responses[{d}]", .{ loc, i });
                const kind = self.d.requiredString(response_json, "kind") catch |e| return self.wrap(path, "a fake response object with a string kind", e);
                if (same(kind, "throw")) count += 1;
            }
        }
        return count;
    }
    fn caseMetadata(self: *Decoder, item: Json) !usize {
        return 1 + (try self.d.array(item, "arguments")).len + (try self.d.array(item, "responses")).len;
    }
    fn tagged(self: *Decoder, path: []const u8, location: []const u8, item: Json, depth: usize) Fail!RunValue {
        return self.taggedBody(path, location, item, depth) catch |e| return self.wrap(location, shape(item), e);
    }
    fn taggedBody(self: *Decoder, path: []const u8, location: []const u8, item: Json, depth: usize) Fail!RunValue {
        if (item != .object) return self.fail("Run values must be non-null typed objects.", .{});
        if (item.object.get("kind") == null and item.object.get("parameter") != null and self.order.len > 0 and depth == 0) return self.fail("{s} arguments are positional plain JSON values, one per parameter in the order ({s}), not {{parameter, value}} pairs: write [{{\"field\":1}}, 10], not [{{\"parameter\":\"x\",\"value\":1}}]. The pair form is only for fake capability case arguments.", .{ self.op, self.order });
        if (item.object.get("kind") == null and item.object.get("parameter") != null) return self.fail("Run values are typed objects with a kind. runPure arguments are positional, one typed value per parameter in declaration order (for example [{{\"kind\":\"string\",\"value\":\"a\"}}]); {{parameter, value}} pairs are the form of fake capability case arguments.", .{});
        const kind = try self.d.requiredString(item, "kind");
        const leaf_kind = same(kind, "string") or same(kind, "bool") or same(kind, "int64") or same(kind, "float64") or same(kind, "bits32") or same(kind, "bytes") or same(kind, "unit");
        if (depth >= 32 and !leaf_kind) return self.fail("Run values may have at most 32 nested composite layers.", .{});
        var precharged: usize = 0;
        if (same(kind, "list") and item.object.get("itemKind") != null) precharged = (try self.d.array(item, "items")).len;
        if (same(kind, "result") and item.object.get("okType") == null) precharged = 1;
        if (same(kind, "option") and item.object.get("itemKind") != null) precharged = @intFromBool(try self.boolean(item, "hasValue"));
        if (same(kind, "fakeCapability")) precharged = try self.fakeMetadata(location, item);
        try self.charge(1 + precharged);
        if (leaf_kind) {
            if (same(kind, "unit")) {
                try self.d.ensureFields(path, item, "Unit run value", &.{"kind"}, &.{"kind"});
                return .{ .kind = .unit, .type = primitive("unit") };
            }
            const context = if (same(kind, "float64")) "Float64 run value" else if (same(kind, "bits32")) "Bits32 run value" else if (same(kind, "bytes")) "Bytes run value" else "Scalar run value";
            const key: []const u8 = if (same(kind, "bytes")) "hex" else "value";
            try self.d.ensureFields(path, item, context, &.{ "kind", key }, &.{ "kind", key });
            return self.leaf(kind, try self.d.property(item, key), location, false);
        }
        if (same(kind, "list") or same(kind, "set")) {
            const flat = same(kind, "list") and item.object.get("itemType") == null;
            const key: []const u8 = if (flat) "itemKind" else "itemType";
            try self.d.ensureFields(path, item, if (flat) "List run value" else if (same(kind, "set")) "Set run value" else "Nested List run value", &.{ "kind", key, "items" }, &.{ "kind", key, "items" });
            const values = try self.d.array(item, "items");
            if (values.len > 4096) return self.fail("{s}", .{if (flat) "List run values may contain at most 4096 items." else if (same(kind, "set")) "Sets may contain at most 4096 items." else "Lists may contain at most 4096 items."});
            const t = if (flat) primitive(try self.scalarKind(item, key, "List itemKind must be string, bool or int64.")) else try self.typeRef(try self.at(path, key), try self.d.property(item, key));
            const out = try self.r().a.alloc(RunValue, values.len);
            for (values, out, 0..) |value, *slot, i| {
                if (flat) {
                    if (same(t.primitive, "string") and value != .string) return self.fail("String List items must be strings.", .{});
                    if (same(t.primitive, "bool") and value != .bool) return self.fail("Bool List items must be booleans.", .{});
                    if (same(t.primitive, "int64") and integer(value) == null) return self.fail("Int64 List items must be exact integers or decimal strings.", .{});
                    slot.* = try self.leaf(t.primitive, value, location, false);
                } else slot.* = try self.tagged(try self.at(try self.at(path, "items"), i), try self.fmt("{s}.items[{d}]", .{ location, i }), value, depth + 1);
            }
            return .{ .kind = if (same(kind, "set")) .set else .list, .legacy = flat, .type = try generic(self.r().a, kind, &.{t}), .items = out };
        }
        if (same(kind, "option")) {
            const has = try self.boolean(item, "hasValue");
            const flat = item.object.get("itemType") == null;
            const key: []const u8 = if (flat) "itemKind" else "itemType";
            const fields: []const []const u8 = if (has) &.{ "kind", key, "hasValue", "value" } else &.{ "kind", key, "hasValue" };
            try self.d.ensureFields(path, item, if (flat) "Option run value" else "Nested Option run value", fields, fields);
            const t = if (flat) primitive(try self.scalarKind(item, key, "Option itemKind must be string, bool or int64.")) else try self.typeRef(try self.at(path, key), try self.d.property(item, key));
            var payload: ?*RunValue = null;
            if (has) {
                payload = try box(self.r().a, if (flat) try self.leaf(t.primitive, try self.d.property(item, "value"), location, false) else try self.tagged(try self.at(path, "value"), try self.fmt("{s}.value", .{location}), try self.d.property(item, "value"), depth + 1));
            }
            return .{ .kind = .option, .legacy = flat, .type = try generic(self.r().a, "option", &.{t}), .payload = payload };
        }
        if (same(kind, "result")) {
            if (item.object.get("case") != null) return self.fail("Result run values select Ok or Err with isOk, not case: nested {{kind:'result', okType, errorType, isOk, value:<typed value>}} or flat {{kind:'result', okKind, errorKind, isOk, value:<raw scalar>}}.", .{});
            const flat = item.object.get("okType") == null;
            const ok_key: []const u8 = if (flat) "okKind" else "okType";
            const err_key: []const u8 = if (flat) "errorKind" else "errorType";
            try self.d.ensureFields(path, item, if (flat) "Result run value" else "Nested Result run value", &.{ "kind", ok_key, err_key, "isOk", "value" }, &.{ "kind", ok_key, err_key, "isOk", "value" });
            const ok = if (flat) primitive(try self.scalarKind(item, ok_key, "Result okKind and errorKind must be string, bool or int64.")) else try self.typeRef(try self.at(path, ok_key), try self.d.property(item, ok_key));
            const err = if (flat) primitive(try self.scalarKind(item, err_key, "Result okKind and errorKind must be string, bool or int64.")) else try self.typeRef(try self.at(path, err_key), try self.d.property(item, err_key));
            const is_ok = try self.boolean(item, "isOk");
            const selected = if (is_ok) ok else err;
            const loc = try self.fmt("{s}.value", .{location});
            const payload = if (flat) self.leaf(selected.primitive, try self.d.property(item, "value"), loc, false) catch |e| return self.wrap(loc, if (same(selected.primitive, "string")) "a raw JSON string (flat Result uses okKind/errorKind; nested Result uses okType/errorType and a typed value)" else if (same(selected.primitive, "bool")) "a raw JSON boolean" else "a raw integer or exact decimal integer string", e) else try self.tagged(try self.at(path, "value"), loc, try self.d.property(item, "value"), depth + 1);
            return .{ .kind = .result, .legacy = flat, .type = try generic(self.r().a, "result", &.{ ok, err }), .boolean = is_ok, .payload = try box(self.r().a, payload) };
        }
        if (same(kind, "map")) {
            try self.d.ensureFields(path, item, "Map run value", &.{ "kind", "keyType", "valueType", "entries" }, &.{ "kind", "keyType", "valueType", "entries" });
            const values = try self.d.array(item, "entries");
            if (values.len > 4096) return self.fail("Maps may contain at most 4096 entries.", .{});
            const out = try self.r().a.alloc(Entry, values.len);
            for (values, out, 0..) |value, *slot, i| slot.* = try self.mapEntry(try self.at(try self.at(path, "entries"), i), try self.fmt("{s}.entries[{d}]", .{ location, i }), value, depth);
            for (out, 0..) |entry, i| for (out[0..i]) |prior| if (equalRaw(entry.key, prior.key)) return self.fail("A Map run value names a key more than once.", .{});
            return .{ .kind = .map, .type = try generic(self.r().a, "map", &.{ try self.typeRef(try self.at(path, "keyType"), try self.d.property(item, "keyType")), try self.typeRef(try self.at(path, "valueType"), try self.d.property(item, "valueType")) }), .entries = out };
        }
        if (same(kind, "product") or same(kind, "union")) return self.namedValue(path, location, item, depth, same(kind, "union"));
        if (same(kind, "hostCapability") or same(kind, "fakeCapability")) return self.binding(path, location, item, depth, same(kind, "hostCapability"));
        return self.fail("Unsupported pure run value kind '{s}'.", .{kind});
    }
    fn scalarKind(self: *Decoder, item: Json, key: []const u8, message: []const u8) ![]const u8 {
        const k = try self.d.requiredString(item, key);
        if (!same(k, "string") and !same(k, "bool") and !same(k, "int64")) return self.fail("{s}", .{message});
        return k;
    }
    fn mapEntry(self: *Decoder, path: []const u8, loc: []const u8, item: Json, depth: usize) !Entry {
        return self.mapEntryBody(path, loc, item, depth) catch |e| return self.wrap(loc, "a Map entry with typed key and value", e);
    }
    fn mapEntryBody(self: *Decoder, path: []const u8, loc: []const u8, item: Json, depth: usize) !Entry {
        try self.d.ensureFields(path, item, "Map run value entry", &.{ "key", "value" }, &.{ "key", "value" });
        return .{ .key = try self.tagged(try self.at(path, "key"), try self.fmt("{s}.key", .{loc}), try self.d.property(item, "key"), depth + 1), .value = try self.tagged(try self.at(path, "value"), try self.fmt("{s}.value", .{loc}), try self.d.property(item, "value"), depth + 1) };
    }
    fn namedValue(self: *Decoder, path: []const u8, loc: []const u8, item: Json, depth: usize, is_union: bool) !RunValue {
        const tk: []const u8 = if (self.names) "type" else "typeId";
        const vk: []const u8 = if (self.names) "variant" else "variantId";
        const required: []const []const u8 = if (is_union) &.{ "kind", tk, "fields", vk } else &.{ "kind", tk, "fields" };
        const allowed: []const []const u8 = if (self.names) (if (is_union) &.{ "kind", tk, "fields", vk, "typeArguments" } else &.{ "kind", tk, "fields", "typeArguments" }) else (if (is_union) &.{ "kind", tk, "fields", vk, "type", "variant", "typeArguments" } else &.{ "kind", tk, "fields", "type", "typeArguments" });
        try self.d.ensureFields(path, item, "Named run value", allowed, required);
        const args = try self.types(path, item, "typeArguments");
        const values = try self.d.array(item, "fields");
        if (values.len > 256) return self.fail("Named run values may contain at most 256 fields.", .{});
        var owner: Model = undefined;
        var variant: Model = undefined;
        const tid = if (self.names) blk: {
            owner = try self.selector(try self.at(path, tk), try self.d.property(item, tk), "type");
            if (is_union and !!product(owner)) return self.fail("Type '{s}' is not a union type.", .{q.nameOf(owner)});
            break :blk q.idOf(owner);
        } else try self.stableId(item, tk);
        const vid = if (is_union) (if (self.names) blk: {
            variant = try self.member("variant on {s}", .{q.nameOf(owner)}, owner, "variants", try self.d.requiredString(item, vk));
            break :blk q.idOf(variant);
        } else try self.stableId(item, vk)) else "";
        const fields = try self.r().a.alloc(Field, values.len);
        for (values, fields, 0..) |value, *slot, i| slot.* = try self.namedField(try self.at(try self.at(path, "fields"), i), try self.fmt("{s}.fields[{d}]", .{ loc, i }), value, depth, if (is_union) variant else owner, is_union);
        return .{ .kind = if (is_union) .union_value else .product, .type = .{ .named = .{ .id = tid, .arguments = args } }, .variant = vid, .fields = fields };
    }
    fn namedField(self: *Decoder, path: []const u8, loc: []const u8, item: Json, depth: usize, owner: Model, is_union: bool) !Field {
        return self.namedFieldBody(path, loc, item, depth, owner, is_union) catch |e| return self.wrap(loc, "a declared field with a typed value", e);
    }
    fn namedFieldBody(self: *Decoder, path: []const u8, loc: []const u8, item: Json, depth: usize, owner: Model, is_union: bool) !Field {
        const key: []const u8 = if (self.names) "field" else "fieldId";
        try self.d.ensureFields(path, item, "Named run field", if (self.names) &.{ key, "value" } else &.{ key, "value", "field" }, &.{ key, "value" });
        if (self.names and !is_union and !product(owner)) return self.fail("Type '{s}' is not a product type.", .{q.nameOf(owner)});
        const id = if (self.names) q.idOf(if (is_union) try self.member("field on variant {s}", .{q.nameOf(owner)}, owner, "fields", try self.d.requiredString(item, key)) else try self.member("field on {s}", .{q.nameOf(owner)}, owner, "fields", try self.d.requiredString(item, key))) else try self.stableId(item, key);
        return .{ .id = id, .value = try self.tagged(try self.at(path, "value"), try self.fmt("{s}.value", .{loc}), try self.d.property(item, "value"), depth + 1) };
    }
    fn binding(self: *Decoder, path: []const u8, loc: []const u8, item: Json, depth: usize, host: bool) !RunValue {
        const key: []const u8 = if (self.names) "capability" else "capabilityId";
        try self.d.ensureFields(path, item, if (host) "Host capability" else "Fake capability", if (host) &.{ "kind", key, "implementation", "supportsConcurrentCalls" } else &.{ "kind", key, "operations" }, if (host) &.{ "kind", key, "implementation" } else &.{ "kind", key, "operations" });
        if (host) {
            const id = if (self.names) q.idOf(try self.selector(try self.at(path, key), try self.d.property(item, key), "capability")) else try self.stableId(item, key);
            const implementation = try self.d.requiredString(item, "implementation");
            const concurrent = if (item.object.get("supportsConcurrentCalls") != null) try self.boolean(item, "supportsConcurrentCalls") else false;
            return .{ .kind = .host, .type = .{ .capability = id }, .text = implementation, .boolean = concurrent };
        }
        const values = try self.d.array(item, "operations");
        if (values.len > 256) return self.fail("A fake capability may script at most 256 operations.", .{});
        var owner: Model = undefined;
        const id = if (self.names) blk: {
            owner = try self.selector(try self.at(path, key), try self.d.property(item, key), "capability");
            break :blk q.idOf(owner);
        } else try self.stableId(item, key);
        const operations = try self.r().a.alloc(Operation, values.len);
        for (values, operations, 0..) |value, *slot, i| slot.* = try self.operation(try self.at(try self.at(path, "operations"), i), try self.fmt("{s}.operations[{d}]", .{ loc, i }), value, depth, owner);
        return .{ .kind = .fake, .type = .{ .capability = id }, .operations = operations };
    }
    fn operation(self: *Decoder, path: []const u8, loc: []const u8, item: Json, depth: usize, owner: Model) !Operation {
        return self.operationBody(path, loc, item, depth, owner) catch |e| return self.wrap(loc, "a fake operation with exactly one of responses or cases", e);
    }
    fn operationBody(self: *Decoder, path: []const u8, loc: []const u8, item: Json, depth: usize, owner: Model) !Operation {
        const key: []const u8 = if (self.names) "operation" else "operationId";
        try self.d.ensureFields(path, item, "Fake operation", &.{ key, "responses", "cases" }, &.{key});
        var declaration: Model = undefined;
        const id = if (self.names) blk: {
            declaration = try self.protectedMember("operation on {s}", .{q.nameOf(owner)}, owner, "operations", try self.d.requiredString(item, key));
            break :blk q.idOf(declaration);
        } else try self.stableId(item, key);
        const responses = item.object.get("responses");
        const cases = item.object.get("cases");
        if (responses != null and cases == null) return .{ .id = id, .responses = try self.parseResponses(path, loc, item, depth) };
        if (cases != null and responses == null) {
            const values = try self.d.array(item, "cases");
            if (values.len > 256) return self.fail("A fake operation may script at most 256 cases.", .{});
            const out = try self.r().a.alloc(Case, values.len);
            var total: usize = 0;
            for (values, out, 0..) |value, *slot, i| {
                slot.* = try self.fakeCase(try self.at(try self.at(path, "cases"), i), try self.fmt("{s}.cases[{d}]", .{ loc, i }), value, depth, declaration);
                total += slot.responses.len;
            }
            if (total > 256) return self.fail("A fake operation may script at most 256 total responses.", .{});
            return .{ .id = id, .cases = out };
        }
        return self.fail("A fake operation requires exactly one of responses or cases.", .{});
    }
    fn parseResponses(self: *Decoder, path: []const u8, loc: []const u8, item: Json, depth: usize) ![]Response {
        const values = try self.d.array(item, "responses");
        if (values.len > 256) return self.fail("A fake operation may script at most 256 responses.", .{});
        const out = try self.r().a.alloc(Response, values.len);
        for (values, out, 0..) |value, *slot, i| slot.* = try self.response(try self.at(try self.at(path, "responses"), i), try self.fmt("{s}.responses[{d}]", .{ loc, i }), value, depth);
        return out;
    }
    fn response(self: *Decoder, path: []const u8, loc: []const u8, item: Json, depth: usize) !Response {
        return self.responseBody(path, loc, item, depth) catch |e| return self.wrap(loc, "return/awaitCancellation with a typed value, or throw with a string message; only return/throw accept delayMillis", e);
    }
    fn responseBody(self: *Decoder, path: []const u8, loc: []const u8, item: Json, depth: usize) !Response {
        const kind = try self.d.requiredString(item, "kind");
        var out: Response = undefined;
        if (same(kind, "return") or same(kind, "awaitCancellation")) {
            const cancellation = same(kind, "awaitCancellation");
            try self.d.ensureFields(path, item, if (cancellation) "Fake awaitCancellation" else "Fake return", if (cancellation) &.{ "kind", "value" } else &.{ "kind", "value", "delayMillis" }, &.{ "kind", "value" });
            out = .{ .kind = if (cancellation) .cancellation else .ret, .value = try box(self.r().a, try self.tagged(try self.at(path, "value"), try self.fmt("{s}.value", .{loc}), try self.d.property(item, "value"), depth + 1)) };
        } else if (same(kind, "throw")) {
            try self.d.ensureFields(path, item, "Fake throw", &.{ "kind", "message", "delayMillis" }, &.{ "kind", "message" });
            out = .{ .kind = .throw, .message = try self.text(try self.d.property(item, "message")) };
        } else return self.fail("Unsupported fake response kind '{s}'.", .{kind});
        if (item.object.get("delayMillis") != null) {
            out.delay = try self.int(item, "delayMillis");
            if (out.delay < 0) return self.fail("Fake delayMillis must not be negative.", .{});
        }
        return out;
    }
    fn fakeCase(self: *Decoder, path: []const u8, loc: []const u8, item: Json, depth: usize, operation_decl: Model) !Case {
        return self.fakeCaseBody(path, loc, item, depth, operation_decl) catch |e| return self.wrap(loc, "a fake case with arguments and responses arrays", e);
    }
    fn fakeCaseBody(self: *Decoder, path: []const u8, loc: []const u8, item: Json, depth: usize, operation_decl: Model) !Case {
        try self.d.ensureFields(path, item, "Fake case", &.{ "arguments", "responses" }, &.{ "arguments", "responses" });
        const values = try self.d.array(item, "arguments");
        if (values.len > 256) return self.fail("A fake case may contain at most 256 arguments.", .{});
        const args = try self.r().a.alloc(Field, values.len);
        for (values, args, 0..) |value, *slot, i| slot.* = try self.fakeArgument(try self.at(try self.at(path, "arguments"), i), try self.fmt("{s}.arguments[{d}]", .{ loc, i }), value, depth, operation_decl);
        return .{ .arguments = args, .responses = try self.parseResponses(path, loc, item, depth) };
    }
    fn fakeArgument(self: *Decoder, path: []const u8, loc: []const u8, item: Json, depth: usize, operation_decl: Model) !Field {
        return self.fakeArgumentBody(path, loc, item, depth, operation_decl) catch |e| return self.wrap(loc, "a fake argument with a declared parameter and typed value", e);
    }
    fn fakeArgumentBody(self: *Decoder, path: []const u8, loc: []const u8, item: Json, depth: usize, operation_decl: Model) !Field {
        const key: []const u8 = if (self.names) "parameter" else "capabilityParameterId";
        try self.d.ensureFields(path, item, "Fake case argument", &.{ key, "value" }, &.{ key, "value" });
        const id = if (self.names) q.idOf(try self.protectedMember("parameter on operation {s}", .{q.nameOf(operation_decl)}, operation_decl, "parameters", try self.d.requiredString(item, key))) else try self.stableId(item, key);
        return .{ .id = id, .value = try self.tagged(try self.at(path, "value"), try self.fmt("{s}.value", .{loc}), try self.d.property(item, "value"), depth + 1) };
    }
    fn plain(self: *Decoder, path: []const u8, loc: []const u8, t: Type, item: Json, depth: usize) Fail!RunValue {
        var is_tagged = false;
        if (item == .object) {
            if (item.object.get("kind")) |kind| {
                is_tagged = kind == .string;
                if (is_tagged and t == .named) if (find(self.r().typedModel().types(), t.named.id)) |owner| {
                    if (product(owner)) for (owner.fields()) |field| {
                        if (same(q.nameOf(field), "kind")) {
                            is_tagged = false;
                            break;
                        }
                    };
                };
            } else is_tagged = item.object.count() == 2 and item.object.get("parameter") != null and item.object.get("value") != null;
        }
        if (is_tagged) return self.tagged(path, loc, item, depth);
        try self.charge(1);
        if (t == .primitive) return self.leaf(t.primitive, item, loc, true);
        if (depth >= 32) return self.fail("Run values may have at most 32 nested composite layers.", .{});
        if (t == .builtin) {
            const b = t.builtin;
            if ((same(b.generic, "list") or same(b.generic, "set") or same(b.generic, "map")) and b.arguments.len == (if (same(b.generic, "map")) @as(usize, 2) else 1)) {
                const label: []const u8 = if (same(b.generic, "list")) "List" else if (same(b.generic, "set")) "Set" else "Map";
                if (item != .array) return self.fail("Expected a JSON array for the {s} at {s}.", .{ label, loc });
                const values = item.array.items;
                if (values.len > 4096) return self.fail("{s}s may contain at most 4096 items.", .{label});
                if (same(b.generic, "map")) {
                    const out = try self.r().a.alloc(Entry, values.len);
                    for (values, out, 0..) |value, *slot, i| {
                        if (value != .array or value.array.items.len != 2) return self.fail("Expected a [key, value] pair at {s}[{d}].", .{ loc, i });
                        const p = try self.at(path, i);
                        slot.* = .{ .key = try self.plain(try self.at(p, @as(usize, 0)), try self.fmt("{s}[{d}][0]", .{ loc, i }), b.arguments[0], value.array.items[0], depth + 1), .value = try self.plain(try self.at(p, @as(usize, 1)), try self.fmt("{s}[{d}][1]", .{ loc, i }), b.arguments[1], value.array.items[1], depth + 1) };
                    }
                    for (out, 0..) |entry, i| for (out[0..i]) |prior| if (equalRaw(entry.key, prior.key)) return self.fail("A Map run value names a key more than once.", .{});
                    return .{ .kind = .map, .type = t, .entries = out };
                }
                const out = try self.r().a.alloc(RunValue, values.len);
                for (values, out, 0..) |value, *slot, i| slot.* = try self.plain(try self.at(path, i), try self.fmt("{s}[{d}]", .{ loc, i }), b.arguments[0], value, depth + 1);
                return .{ .kind = if (same(b.generic, "list")) .list else .set, .type = t, .items = out };
            }
            if (same(b.generic, "option") and b.arguments.len == 1) {
                const child = b.arguments[0];
                if ((child == .builtin and same(child.builtin.generic, "option")) or (child == .primitive and same(child.primitive, "unit"))) return self.fail("An Option of Option or Unit is ambiguous as plain JSON at {s}; write it as a tagged run value.", .{loc});
                return .{ .kind = .option, .type = t, .payload = if (item == .null) null else try box(self.r().a, try self.plain(path, loc, child, item, depth + 1)) };
            }
            if (same(b.generic, "result") and b.arguments.len == 2) {
                if (item != .object) return self.fail("Expected a JSON object for the Result at {s}.", .{loc});
                if (item.object.count() != 1 or (!item.object.contains("ok") and !item.object.contains("error")) or self.d.duplicates.contains(path)) return self.fail("Expected {{\"ok\": value}} or {{\"error\": value}} for the Result at {s}.", .{loc});
                const ok = item.object.contains("ok");
                const key: []const u8 = if (ok) "ok" else "error";
                return .{ .kind = .result, .type = t, .boolean = ok, .payload = try box(self.r().a, try self.plain(try self.at(path, key), try self.fmt("{s}.{s}", .{ loc, key }), b.arguments[if (ok) @as(usize, 0) else 1], item.object.get(key).?, depth + 1)) };
            }
        }
        if (t == .named) {
            const owner = find(self.r().typedModel().types(), t.named.id) orelse return self.fail("Unknown type at {s}; write it as a tagged run value.", .{loc});
            if (item != .object) return self.fail("Expected a JSON object for the {s} at {s}.", .{ q.nameOf(owner), loc });
            if (product(owner)) return .{ .kind = .product, .type = t, .fields = try self.plainFields(path, loc, owner, owner, t.named.arguments, item, depth) };
            if (item.object.count() != 1 or self.d.duplicates.contains(path)) return self.fail("Expected {{\"Variant\": {{fields}}}} for the {s} Union at {s}.", .{ q.nameOf(owner), loc });
            const variant_name = item.object.keys()[0];
            const variant = try self.member("variant on {s}", .{q.nameOf(owner)}, owner, "variants", variant_name);
            const value = item.object.values()[0];
            if (value != .object) return self.fail("Expected an object of fields for variant '{s}' at {s}.", .{ variant_name, loc });
            return .{ .kind = .union_value, .type = t, .variant = q.idOf(variant), .fields = try self.plainFields(try self.at(path, variant_name), loc, owner, variant, t.named.arguments, value, depth) };
        }
        return self.fail("This parameter type has no plain JSON form at {s}; write it as a tagged run value.", .{loc});
    }
    fn plainFields(self: *Decoder, path: []const u8, loc: []const u8, owner: Model, constructor: Model, args: []const Type, item: Json, depth: usize) ![]Field {
        const fields = constructor.fields();
        if (self.d.duplicates.contains(path)) return self.fail("{s} names a field more than once at {s}.", .{ q.nameOf(constructor), loc });
        for (item.object.keys()) |key| {
            for (fields) |field| {
                if (same(q.nameOf(field), key)) break;
            } else return self.fail("{s} has no field '{s}' at {s}.", .{ q.nameOf(constructor), key, loc });
        }
        const out = try self.r().a.alloc(Field, fields.len);
        for (fields, out) |field, *slot| {
            const name = q.nameOf(field);
            const value = item.object.get(name) orelse return self.fail("Missing field '{s}' of {s} at {s}.", .{ name, q.nameOf(constructor), loc });
            slot.* = .{ .id = q.idOf(field), .value = try self.plain(try self.at(path, name), try self.fmt("{s}.{s}", .{ loc, name }), try instantiated(self.r(), owner, args, field), value, depth + 1) };
        }
        return out;
    }
};

pub fn decode(r: *q.Request) Fail!Input {
    const original_model = r.generation;
    const declarations = try declarationView(r);
    r.generation = declarations.data.snapshot;
    defer r.generation = original_model;
    const op = r.root.get("op").?.string;
    const names = same(op, "runPureByName");
    const key: []const u8 = if (names) "function" else "functionId";
    var d: Decoder = .{ .names = names, .d = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line), .owner = if (names) .{ .name = "" } else null } };
    const root = try std.json.parseFromSliceLeaky(Json, r.a, r.line, .{ .duplicate_field_behavior = .use_last, .parse_numbers = false });
    try d.d.ensureFields("", root, try d.fmt("{s} request", .{op}), &.{ "id", "op", key, "arguments", "expected", "hostSources", "backend" }, &.{ "id", "op", key, "arguments" });
    const id = if (names) q.idOf(try d.selector(try d.at("", key), try d.d.property(root, key), "function")) else try d.stableId(root, key);
    const fn_value = find(try q.typedVisible(r, "function"), id);
    const params = if (fn_value) |f| f.parameters() else &.{};
    d.op = op;
    if (params.len > 0) {
        const order = try r.a.alloc([]const u8, params.len);
        for (params, order) |p, *name| name.* = q.nameOf(p);
        d.order = try std.mem.join(r.a, ", ", order);
    }
    const args_json = try d.d.array(root, "arguments");
    const args = try r.a.alloc(RunValue, args_json.len);
    for (args_json, args, 0..) |value, *slot, i| {
        d.remaining = 8192;
        d.top = try d.fmt("arguments[{d}]", .{i});
        slot.* = if (i < params.len) try d.plain(try d.at("/arguments", i), d.top, try infer.typeOf(r.a, params[i].declaredType()), value, 0) else try d.tagged(try d.at("/arguments", i), d.top, value, 0);
    }
    d.order = "";
    var expected: ?RunValue = null;
    if (root.object.get("expected")) |value| {
        d.remaining = 8192;
        d.top = "expected";
        expected = if (fn_value) |f| try d.plain("/expected", "expected", try infer.typeOf(r.a, f.resultType()), value, 0) else try d.tagged("/expected", "expected", value, 0);
    }
    var sources: []const []const u8 = &.{};
    if (root.object.get("hostSources") != null) {
        const values = try d.d.array(root, "hostSources");
        const out = try r.a.alloc([]const u8, values.len);
        for (values, out) |value, *slot| {
            if (value != .string) return r.fail("hostSources must contain file paths.", .{});
            slot.* = try q.readable(r, value.string);
        }
        sources = out;
    }
    var input: Input = .{ .function_id = id, .declarations = r.typedModel(), .fn_value = fn_value, .arguments = args, .expected = expected, .host_sources = sources };
    for (args) |arg| {
        if (arg.kind == .host) input.has_host_binding = true;
        if (arg.kind == .fake) {
            for (arg.operations) |operation| {
                if (operation.cases) |cases| {
                    for (cases) |case| {
                        for (case.responses) |response| {
                            if (response.kind == .cancellation or response.delay > 0) input.waiting_responses = true;
                        }
                    }
                } else {
                    for (operation.responses) |response| {
                        if (response.kind == .cancellation or response.delay > 0) input.waiting_responses = true;
                    }
                }
            }
        }
    }
    return input;
}
/// Verification grammar is tagged-only; its names belong to two immutable declaration views.
pub const VerificationCase = struct { label: []const u8, function_id: []const u8, arguments: []RunValue, expected: ?RunValue };
pub fn verificationCases(r: *q.Request, candidate: *q.Request, names: bool) Fail![]VerificationCase {
    const original = r.generation;
    r.generation = (try declarationView(r)).data.snapshot;
    defer r.generation = original;
    const candidate_original = candidate.generation;
    candidate.generation = (try declarationView(candidate)).data.snapshot;
    defer candidate.generation = candidate_original;
    const duplicates = try recipe.duplicatePaths(r.a, r.line);
    var d: Decoder = .{ .names = names, .d = .{ .r = r, .duplicates = duplicates, .owner = if (names) .{ .name = "" } else null } };
    var cd: Decoder = .{ .names = names, .d = .{ .r = candidate, .duplicates = duplicates, .owner = if (names) .{ .name = "" } else null } };
    const root = try std.json.parseFromSliceLeaky(Json, r.a, r.line, .{ .duplicate_field_behavior = .use_last, .parse_numbers = false });
    const items = try d.d.array(root, "cases");
    const out = try r.a.alloc(VerificationCase, items.len);
    const key: []const u8 = if (names) "function" else "functionId";
    for (items, out, 0..) |item, *slot, i| {
        const path = try std.fmt.allocPrint(r.a, "/cases/{d}", .{i});
        try d.d.ensureFields(path, item, "verification case", &.{ "label", key, "arguments", "expectedCandidate" }, &.{ "label", key, "arguments" });
        const label = try d.d.requiredString(item, "label");
        const id = if (names) q.idOf(try d.selector(try d.at(path, key), try d.d.property(item, key), "function")) else try d.stableId(item, key);
        const args = try d.d.array(item, "arguments");
        const values = try r.a.alloc(RunValue, args.len);
        for (args, values, 0..) |arg, *value, index| {
            d.remaining = 8192;
            d.top = try std.fmt.allocPrint(r.a, "cases[{d}].arguments[{d}]", .{ i, index });
            value.* = try d.tagged(try d.at(try d.at(path, "arguments"), index), d.top, arg, 0);
        }
        var expected: ?RunValue = null;
        if (item.object.get("expectedCandidate")) |value| {
            cd.remaining = 8192;
            cd.top = try std.fmt.allocPrint(r.a, "cases[{d}].expectedCandidate", .{i});
            expected = cd.tagged(try cd.at(path, "expectedCandidate"), cd.top, value, 0) catch |e| {
                r.rejection = candidate.rejection;
                return e;
            };
        }
        slot.* = .{ .label = label, .function_id = id, .arguments = values, .expected = expected };
    }
    return out;
}
pub fn inputForCase(r: *q.Request, case: VerificationCase, expected: ?RunValue) Fail!Input {
    const declarations = try declarationView(r);
    var input: Input = .{ .function_id = case.function_id, .declarations = declarations, .fn_value = find(declarations.functions(), case.function_id), .arguments = case.arguments, .expected = expected, .host_sources = &.{} };
    for (input.arguments) |arg| {
        if (arg.kind == .host) input.has_host_binding = true;
        for (arg.operations) |op| {
            if (op.cases) |cases| {
                for (cases) |c| {
                    for (c.responses) |response| {
                        if (response.kind == .cancellation or response.delay > 0) input.waiting_responses = true;
                    }
                }
            } else {
                for (op.responses) |response| {
                    if (response.kind == .cancellation or response.delay > 0) input.waiting_responses = true;
                }
            }
        }
    }
    return input;
}

fn children(a: Allocator, v: RunValue) ![]const RunValue {
    var out: std.ArrayList(RunValue) = .empty;
    for (v.items) |x| try out.append(a, x);
    for (v.fields) |x| try out.append(a, x.value);
    for (v.entries) |x| {
        try out.append(a, x.key);
        try out.append(a, x.value);
    }
    if (v.payload) |x| try out.append(a, x.*);
    for (v.operations) |op| {
        if (op.cases) |cases| {
            for (cases) |case| {
                for (case.arguments) |arg| try out.append(a, arg.value);
                for (case.responses) |response| if (response.value) |x| try out.append(a, x.*);
            }
        } else for (op.responses) |response| if (response.value) |x| try out.append(a, x.*);
    }
    return out.items;
}
fn canonicalize(v: *RunValue) void {
    sortFields(v.fields);
    for (v.fields) |*field| canonicalize(&field.value);
    for (v.items) |*item| canonicalize(item);
    for (v.entries) |*entry| {
        canonicalize(&entry.key);
        canonicalize(&entry.value);
    }
    if (v.payload) |payload| canonicalize(payload);
    std.mem.sort(Operation, v.operations, {}, struct {
        fn less(_: void, x: Operation, y: Operation) bool {
            return q.ordinal({}, x.id, y.id);
        }
    }.less);
    for (v.operations) |*operation| {
        if (operation.cases) |cases| {
            for (cases) |*case| {
                sortFields(case.arguments);
                for (case.arguments) |*arg| canonicalize(&arg.value);
                for (case.responses) |*response| {
                    if (response.value) |value| canonicalize(value);
                }
            }
        } else {
            for (operation.responses) |*response| {
                if (response.value) |value| canonicalize(value);
            }
        }
    }
}
fn equalRaw(x: RunValue, y: RunValue) bool {
    if (x.legacy != y.legacy or !equal(x, y)) return false;
    for (x.items, y.items) |p, z| if (!equalRaw(p, z)) return false;
    for (x.fields, y.fields) |p, z| if (!equalRaw(p.value, z.value)) return false;
    for (x.entries, y.entries) |p, z| if (!equalRaw(p.key, z.key) or !equalRaw(p.value, z.value)) return false;
    if (x.payload) |p| if (!equalRaw(p.*, y.payload.?.*)) return false;
    return true;
}
pub fn equal(x: RunValue, y: RunValue) bool {
    if (x.kind != y.kind or !infer.eqlType(x.type, y.type) or !same(x.text, y.text) or x.integer != y.integer or x.bits != y.bits or x.boolean != y.boolean or !same(x.variant, y.variant) or x.items.len != y.items.len or x.fields.len != y.fields.len or x.entries.len != y.entries.len or (x.payload == null) != (y.payload == null)) return false;
    for (x.items, y.items) |p, z| if (!equal(p, z)) return false;
    for (x.fields, y.fields) |p, z| if (!same(p.id, z.id) or !equal(p.value, z.value)) return false;
    for (x.entries, y.entries) |p, z| if (!equal(p.key, z.key) or !equal(p.value, z.value)) return false;
    if (x.payload) |p| if (!equal(p.*, y.payload.?.*)) return false;
    return true;
}
fn countValue(a: Allocator, v: RunValue, depth: usize, remaining: *i64) Fail!bool {
    remaining.* -= 1;
    const composite = switch (v.kind) {
        .string, .boolean, .int64, .float64, .bits32, .bytes, .unit => false,
        else => true,
    };
    if (remaining.* < 0 or (composite and depth >= 32)) return false;
    if (v.kind == .fake) for (v.operations) |op| {
        if (op.cases) |cases| {
            for (cases) |case| remaining.* -= @intCast(1 + case.arguments.len + case.responses.len);
        } else {
            for (op.responses) |response| {
                if (response.kind == .throw) remaining.* -= 1;
            }
        }
    };
    if (remaining.* < 0) return false;
    for (try children(a, v)) |child| if (!try countValue(a, child, depth + 1, remaining)) return false;
    return true;
}
pub fn bounded(r: *q.Request, input: *Input) Fail!void {
    for (input.arguments) |arg| {
        var remaining: i64 = 8192;
        if (!try countValue(r.a, arg, 0, &remaining)) return diagnostic(r, input, "RunArgumentTooLarge", "Each argument or expected value is limited to 32 nested composite layers and 8192 value nodes. Split the input into smaller batches or return an aggregate.");
    }
    if (input.expected) |v| {
        var remaining: i64 = 8192;
        if (!try countValue(r.a, v, 0, &remaining)) return diagnostic(r, input, "RunArgumentTooLarge", "Each argument or expected value is limited to 32 nested composite layers and 8192 value nodes. Split the input into smaller batches or return an aggregate.");
    }
}
fn fieldsOf(r: *q.Request, v: RunValue) !?[]const Model {
    if (v.type != .named) return null;
    const owner = find(r.typedModel().types(), v.type.named.id) orelse return null;
    if (v.kind == .product and product(owner)) return try sorted(r.a, owner.fields());
    if (v.kind == .union_value and !product(owner)) if (find(owner.variants(), v.variant)) |variant| return try sorted(r.a, variant.fields());
    return null;
}
fn supportedCost(r: *q.Request, t: Type, depth: usize, path: []const Type, budget: *usize) Fail!bool {
    budget.* += 1;
    if (budget.* > 131072) return false;
    if (t == .primitive) return same(t.primitive, "string") or same(t.primitive, "bool") or same(t.primitive, "int64") or same(t.primitive, "float64") or same(t.primitive, "bits32") or same(t.primitive, "bytes") or same(t.primitive, "unit");
    if (t == .named) for (path) |p| if (infer.eqlType(p, t)) return true;
    if (depth > 7) return false;
    if (t == .builtin) {
        const b = t.builtin;
        const arity: usize = if (same(b.generic, "list") or same(b.generic, "option") or same(b.generic, "set")) 1 else if (same(b.generic, "result") or same(b.generic, "map")) 2 else return false;
        if (b.arguments.len != arity) return false;
        for (b.arguments) |arg| if (!try supportedCost(r, arg, depth + 1, path, budget)) return false;
        return true;
    }
    if (t == .named) {
        const owner = find(r.typedModel().types(), t.named.id) orelse return false;
        const next = try r.a.alloc(Type, path.len + 1);
        @memcpy(next[0..path.len], path);
        next[path.len] = t;
        if (product(owner)) {
            const fields = owner.fields();
            if (fields.len > 256) return false;
            for (fields) |field| if (!try supportedCost(r, try instantiated(r, owner, t.named.arguments, field), depth + 1, next, budget)) return false;
            return true;
        }
        const variants = owner.variants();
        if (variants.len == 0 or variants.len > 256) return false;
        for (variants) |variant| {
            const fields = variant.fields();
            if (fields.len > 256) return false;
            for (fields) |field| if (!try supportedCost(r, try instantiated(r, owner, t.named.arguments, field), depth + 1, next, budget)) return false;
        }
        return true;
    }
    return false;
}
pub fn supported(r: *q.Request, t: Type, depth: usize) !bool {
    var budget: usize = 0;
    return supportedCost(r, t, depth, &.{}, &budget);
}
fn bearsCapability(t: Type) bool {
    switch (t) {
        .capability => return true,
        .named => |n| {
            for (n.arguments) |arg| if (bearsCapability(arg)) return true;
        },
        .builtin => |b| {
            for (b.arguments) |arg| if (bearsCapability(arg)) return true;
        },
        else => {},
    }
    return false;
}
fn taskBearing(r: *q.Request, t: Type, path: []const []const u8) Fail!bool {
    if (t == .builtin) {
        if (same(t.builtin.generic, "task") or same(t.builtin.generic, "oneShot") or same(t.builtin.generic, "taskScope")) return true;
        for (t.builtin.arguments) |arg| if (try taskBearing(r, arg, path)) return true;
    }
    if (t == .named) {
        for (t.named.arguments) |arg| if (try taskBearing(r, arg, path)) return true;
        for (path) |id| if (same(id, t.named.id)) return false;
        const owner = find(r.typedModel().types(), t.named.id) orelse return false;
        const next = try r.a.alloc([]const u8, path.len + 1);
        @memcpy(next[0..path.len], path);
        next[path.len] = t.named.id;
        if (product(owner)) {
            for (owner.fields()) |field| if (try taskBearing(r, try instantiated(r, owner, t.named.arguments, field), next)) return true;
        } else for (owner.variants()) |variant| for (variant.fields()) |field| {
            if (try taskBearing(r, try instantiated(r, owner, t.named.arguments, field), next)) return true;
        };
    }
    return false;
}
fn containsBinding(r: *q.Request, v: RunValue) Fail!bool {
    for (try children(r.a, v)) |child| {
        if (child.kind == .fake or child.kind == .host or try containsBinding(r, child)) return true;
    }
    return false;
}
fn validNamed(r: *q.Request, v: RunValue, kind: Kind) Fail!bool {
    if (v.kind == kind) {
        const fields = (try fieldsOf(r, v)) orelse return false;
        if (fields.len != v.fields.len) return false;
        const owner = find(r.typedModel().types(), v.type.named.id).?;
        for (fields, v.fields) |field, actual| if (!same(q.idOf(field), actual.id) or !infer.eqlType(try instantiated(r, owner, v.type.named.arguments, field), actual.value.type)) return false;
    }
    for (try children(r.a, v)) |child| if (!try validNamed(r, child, kind)) return false;
    return true;
}
fn validContainers(r: *q.Request, v: RunValue) Fail!bool {
    if (v.kind == .list or v.kind == .set) {
        const t = v.type.builtin.arguments[0];
        if (!try supported(r, t, 1)) return false;
        for (v.items, 0..) |item, i| {
            if (!infer.eqlType(t, item.type)) return false;
            if (v.kind == .set) for (v.items[0..i]) |prior| {
                if (equal(item, prior)) return false;
            };
        }
    }
    if (v.kind == .option) {
        const t = v.type.builtin.arguments[0];
        if (!try supported(r, t, 1)) return false;
        if (v.payload) |payload| if (!infer.eqlType(t, payload.type)) return false;
    }
    if (v.kind == .result) {
        const args = v.type.builtin.arguments;
        if (!try supported(r, args[0], 1) or !try supported(r, args[1], 1) or !infer.eqlType(args[if (v.boolean) @as(usize, 0) else 1], v.payload.?.type)) return false;
    }
    if (v.kind == .map) {
        const args = v.type.builtin.arguments;
        if (!try supported(r, args[0], 1) or !try supported(r, args[1], 1)) return false;
        for (v.entries, 0..) |entry, i| {
            if (!infer.eqlType(args[0], entry.key.type) or !infer.eqlType(args[1], entry.value.type)) return false;
            for (v.entries[0..i]) |prior| if (equal(entry.key, prior.key)) return false;
        }
    }
    for (try children(r.a, v)) |child| if (!try validContainers(r, child)) return false;
    return true;
}
const Problem = struct { code: []const u8, message: []const u8 };
fn fakeProblem(r: *q.Request, v: RunValue) !?Problem {
    if (v.kind != .fake and v.kind != .host) return null;
    const capability = find(r.typedModel().capabilities(), v.type.capability) orelse return .{ .code = "RunBindingShapeMismatch", .message = "The binding names an unknown capability." };
    const declarations = capability.operations();
    for (declarations) |op| {
        const rt = try infer.typeOf(r.a, op.resultType());
        if (!try supported(r, rt, 0) and !bearsCapability(rt)) return .{ .code = "RunBindingTypeUnsupported", .message = "Every operation of a bound capability needs parameter and result types that runPure values support, or that mention a capability." };
        for (op.parameters()) |parameter| {
            const t = try infer.typeOf(r.a, parameter.declaredType());
            if (!try supported(r, t, 0) and !bearsCapability(t)) return .{ .code = "RunBindingTypeUnsupported", .message = "Every operation of a bound capability needs parameter and result types that runPure values support, or that mention a capability." };
        }
    }
    if (v.kind == .host) {
        if (v.text.len == 0 or v.text.len > 512) return .{ .code = "RunBindingShapeMismatch", .message = "A host implementation must be a dotted C# type name." };
        var start = true;
        for (v.text) |c| {
            if (c == '.') {
                if (start) return .{ .code = "RunBindingShapeMismatch", .message = "A host implementation must be a dotted C# type name." };
                start = true;
            } else {
                if (!(std.ascii.isAlphabetic(c) or c == '_' or (!start and std.ascii.isDigit(c)))) return .{ .code = "RunBindingShapeMismatch", .message = "A host implementation must be a dotted C# type name." };
                start = false;
            }
        }
        if (start) return .{ .code = "RunBindingShapeMismatch", .message = "A host implementation must be a dotted C# type name." };
        return null;
    }
    for (v.operations, 0..) |op, i| {
        if (find(declarations, op.id) == null) return .{ .code = "RunBindingShapeMismatch", .message = "Fake operations must be distinct operations of the faked capability." };
        for (v.operations[0..i]) |prior| if (same(op.id, prior.id)) return .{ .code = "RunBindingShapeMismatch", .message = "Fake operations must be distinct operations of the faked capability." };
    }
    for (v.operations) |op| if (op.cases) |cases| {
        const declaration = find(declarations, op.id).?;
        const parameters = try sorted(r.a, declaration.parameters());
        for (cases, 0..) |case, i| {
            var valid = case.arguments.len == parameters.len;
            if (valid) for (case.arguments, parameters) |arg, p| {
                if (!same(arg.id, q.idOf(p)) or !infer.eqlType(arg.value.type, try infer.typeOf(r.a, p.declaredType()))) {
                    valid = false;
                    break;
                }
            };
            if (valid) for (cases[0..i]) |prior| {
                if (prior.arguments.len != case.arguments.len) continue;
                var eq = true;
                for (prior.arguments, case.arguments) |p, arg| {
                    if (!same(p.id, arg.id) or !equal(p.value, arg.value)) {
                        eq = false;
                        break;
                    }
                }
                if (eq) {
                    valid = false;
                    break;
                }
            };
            if (!valid) return .{ .code = "RunBindingShapeMismatch", .message = "Keyed fakes require unique complete typed argument sets and cannot mix cases with ordered responses." };
        }
    };
    for (v.operations) |op| {
        const rt = try infer.typeOf(r.a, find(declarations, op.id).?.resultType());
        if (op.cases) |cases| {
            for (cases) |case| {
                for (case.responses) |response| {
                    if (response.value) |value| {
                        if (!infer.eqlType(value.type, rt)) return .{ .code = "RunBindingShapeMismatch", .message = "Each fake return value must have the operation's declared result type." };
                    }
                }
            }
        } else {
            for (op.responses) |response| {
                if (response.value) |value| {
                    if (!infer.eqlType(value.type, rt)) return .{ .code = "RunBindingShapeMismatch", .message = "Each fake return value must have the operation's declared result type." };
                }
            }
        }
    }
    return null;
}
// Plain constructors reach this guard after signature and shape validation.
fn oversizedConstructors(a: Allocator, value: RunValue) Fail!bool {
    if (value.fields.len > 256) return true;
    for (try children(a, value)) |child| if (try oversizedConstructors(a, child)) return true;
    return false;
}
const StringSize = struct { total: usize = 0, max: usize = 0, flat_list: bool = false };
fn stringSize(a: Allocator, v: RunValue) Fail!StringSize {
    var result: StringSize = .{};
    if (v.kind == .string) {
        const n = (try std.unicode.utf8ToUtf16LeAlloc(a, v.text)).len;
        result.total = n;
        result.max = n;
    }
    if (v.kind == .fake) for (v.operations) |op| {
        if (op.cases) |cases| {
            for (cases) |case| for (case.responses) |response| {
                if (response.kind == .throw) {
                    const n = (try std.unicode.utf8ToUtf16LeAlloc(a, response.message)).len;
                    result.total += n;
                    result.max = @max(result.max, n);
                }
            };
        } else {
            for (op.responses) |response| {
                if (response.kind == .throw) {
                    const n = (try std.unicode.utf8ToUtf16LeAlloc(a, response.message)).len;
                    result.total += n;
                    result.max = @max(result.max, n);
                }
            }
        }
    };
    for (try children(a, v)) |child| {
        const stats = try stringSize(a, child);
        result.total += stats.total;
        result.max = @max(result.max, stats.max);
        result.flat_list = result.flat_list or stats.flat_list;
    }
    if (v.kind == .list and scalarType(v.type.builtin.arguments[0]) and same(v.type.builtin.arguments[0].primitive, "string") and result.total > 65536) result.flat_list = true;
    return result;
}
fn scalarType(t: Type) bool {
    return t == .primitive and (same(t.primitive, "string") or same(t.primitive, "bool") or same(t.primitive, "int64"));
}
const Issue = struct { path: []const []const u8, expected: ?Type, actual: ?Type, reason: []const u8 };
fn pathAdd(a: Allocator, path: []const []const u8, step: []const u8) ![]const []const u8 {
    const out = try a.alloc([]const u8, path.len + 1);
    @memcpy(out[0..path.len], path);
    out[path.len] = step;
    return out;
}
fn issueFor(category: []const u8, code: []const u8, path: []const []const u8, expected: ?Type, actual: ?Type, reason: []const u8) ?Issue {
    if (!same(category, code)) return null;
    return .{ .path = path, .expected = expected, .actual = actual, .reason = reason };
}
fn inputIssue(r: *q.Request, category: []const u8, code: []const u8, path: []const []const u8, declared: Type, v: RunValue) Fail!?Issue {
    if (!infer.eqlType(declared, v.type)) if (issueFor(category, code, path, declared, v.type, "The value type differs from the declared type.")) |found| return found;
    if (v.kind == .fake) {
        if (find(r.typedModel().capabilities(), v.type.capability)) |capability| {
            for (v.operations, 0..) |operation, i| {
                const declaration = find(capability.operations(), operation.id) orelse continue;
                const op_path = try pathAdd(r.a, path, try std.fmt.allocPrint(r.a, "operations[{d}]", .{i}));
                if (operation.cases) |cases| {
                    for (cases, 0..) |case, j| {
                        const case_path = try pathAdd(r.a, op_path, try std.fmt.allocPrint(r.a, "cases[{d}]", .{j}));
                        for (case.arguments, 0..) |argument, k| {
                            const parameter = find(declaration.parameters(), argument.id) orelse continue;
                            const arg_path = try pathAdd(r.a, try pathAdd(r.a, case_path, try std.fmt.allocPrint(r.a, "arguments[{d}]", .{k})), "value");
                            if (try inputIssue(r, category, "RunBindingShapeMismatch", arg_path, try infer.typeOf(r.a, parameter.declaredType()), argument.value)) |found| return found;
                        }
                        if (try responseIssue(r, category, case_path, declaration, case.responses)) |found| return found;
                    }
                } else if (try responseIssue(r, category, op_path, declaration, operation.responses)) |found| return found;
            }
        }
    }
    if (v.kind == .product or v.kind == .union_value) {
        const shape_code: []const u8 = if (v.kind == .product) "RunProductShapeMismatch" else "RunUnionShapeMismatch";
        const owner = find(r.typedModel().types(), v.type.named.id);
        var p = try pathAdd(r.a, path, if (owner) |o| q.nameOf(o) else v.type.named.id);
        if (v.kind == .union_value) {
            const variant = if (owner) |o| find(o.variants(), v.variant) else null;
            p = try pathAdd(r.a, p, if (variant) |x| q.nameOf(x) else try std.fmt.allocPrint(r.a, "variant:{s}", .{v.variant}));
        }
        const fields = (try fieldsOf(r, v)) orelse return issueFor(category, shape_code, p, v.type, v.type, "The Product type or Union variant is not declared.");
        for (v.fields) |field| if (find(fields, field.id) == null) return issueFor(category, shape_code, try pathAdd(r.a, p, try std.fmt.allocPrint(r.a, "field:{s}", .{field.id})), null, field.value.type, "This field does not belong to the selected constructor.");
        for (fields) |field| {
            const field_path = try pathAdd(r.a, p, q.nameOf(field));
            const ft = try instantiated(r, owner.?, v.type.named.arguments, field);
            var n: usize = 0;
            var value: ?RunValue = null;
            for (v.fields) |actual| if (same(actual.id, q.idOf(field))) {
                n += 1;
                value = actual.value;
            };
            if (n == 0) {
                if (issueFor(category, shape_code, field_path, ft, null, "A required field is missing.")) |found| return found;
            } else if (n > 1) {
                if (issueFor(category, shape_code, field_path, ft, null, "A field is supplied more than once.")) |found| return found;
            } else if (try inputIssue(r, category, shape_code, field_path, ft, value.?)) |found| return found;
        }
    }
    if (v.kind == .list or v.kind == .set) {
        const t = v.type.builtin.arguments[0];
        for (v.items, 0..) |item, i| if (try inputIssue(r, category, "RunContainerTypeMismatch", try pathAdd(r.a, path, try std.fmt.allocPrint(r.a, "items[{d}]", .{i})), t, item)) |found| return found;
        if (v.kind == .set) for (v.items, 0..) |item, i| {
            for (v.items[0..i]) |prior| if (equal(item, prior)) return issueFor(category, "RunContainerTypeMismatch", try pathAdd(r.a, path, try std.fmt.allocPrint(r.a, "items[{d}]", .{i})), t, t, "Set items must be distinct.");
        };
    }
    if (v.kind == .map) for (v.entries, 0..) |entry, i| {
        const kp = try pathAdd(r.a, path, try std.fmt.allocPrint(r.a, "entries[{d}].key", .{i}));
        const ts = v.type.builtin.arguments;
        if (try inputIssue(r, category, "RunContainerTypeMismatch", kp, ts[0], entry.key)) |found| return found;
        if (try inputIssue(r, category, "RunContainerTypeMismatch", try pathAdd(r.a, path, try std.fmt.allocPrint(r.a, "entries[{d}].value", .{i})), ts[1], entry.value)) |found| return found;
        for (v.entries[0..i]) |prior| if (equal(entry.key, prior.key)) return issueFor(category, "RunContainerTypeMismatch", kp, ts[0], ts[0], "Map keys must be distinct.");
    };
    if (v.kind == .option) {
        if (v.payload) |payload| return inputIssue(r, category, "RunContainerTypeMismatch", try pathAdd(r.a, path, "some"), v.type.builtin.arguments[0], payload.*);
    }
    if (v.kind == .result) return inputIssue(r, category, "RunContainerTypeMismatch", try pathAdd(r.a, path, if (v.boolean) "ok" else "err"), v.type.builtin.arguments[if (v.boolean) @as(usize, 0) else 1], v.payload.?.*);
    return null;
}
fn responseIssue(r: *q.Request, category: []const u8, path: []const []const u8, declaration: Model, responses: []const Response) Fail!?Issue {
    const rt = try infer.typeOf(r.a, declaration.resultType());
    for (responses, 0..) |response, i| {
        if (response.value) |value| {
            const value_path = try pathAdd(r.a, try pathAdd(r.a, path, try std.fmt.allocPrint(r.a, "responses[{d}]", .{i})), "value");
            if (try inputIssue(r, category, "RunBindingShapeMismatch", value_path, rt, value.*)) |found| return found;
        }
    }
    return null;
}
fn validationError(r: *q.Request, input: *Input, code: []const u8, message: []const u8) Fail!void {
    var found: ?Issue = null;
    if (same(code, "RunArgumentTypeMismatch") or same(code, "RunProductShapeMismatch") or same(code, "RunUnionShapeMismatch") or same(code, "RunContainerTypeMismatch") or same(code, "RunExpectedTypeMismatch")) {
        const parameters = input.fn_value.?.parameters();
        if (parameters.len == input.arguments.len) {
            for (parameters, input.arguments) |parameter, value| {
                found = try inputIssue(r, code, "RunArgumentTypeMismatch", &.{ "arguments", q.nameOf(parameter) }, try infer.typeOf(r.a, parameter.declaredType()), value);
                if (found != null) break;
            }
            if (found == null) if (input.expected) |value| {
                found = try inputIssue(r, code, "RunExpectedTypeMismatch", &.{"expected"}, try infer.typeOf(r.a, input.fn_value.?.resultType()), value);
            };
        }
    }
    var msg = message;
    if (found) |issue| msg = try std.fmt.allocPrint(r.a, "{s} At {s}: {s}", .{ message, try std.mem.join(r.a, ".", issue.path), issue.reason });
    diagnostic(r, input, code, msg) catch |e| {
        if (found) |issue| {
            const paths = try r.a.alloc(model.Value, issue.path.len);
            for (issue.path, paths) |p, *slot| slot.* = try mat.string(r.a, p);
            const details = try object(r, .{ .{ "path", model.Value{ .array = paths } }, .{ "expectedType", if (issue.expected) |t| try typeValue(r, t) else model.Value.json_null }, .{ "actualType", if (issue.actual) |t| try typeValue(r, t) else model.Value.json_null }, .{ "reason", try mat.string(r.a, issue.reason) } });
            var out: Writer.Allocating = .init(r.a);
            try mat.write(&out.writer, details);
            r.rejection.fields = try std.fmt.allocPrint(r.a, "{s},\"details\":{s}", .{ r.rejection.fields, out.written() });
        }
        return e;
    };
}
pub fn validate(r: *q.Request, input: *Input) Fail!void {
    const original_model = r.generation;
    r.generation = input.declarations.data.snapshot;
    defer r.generation = original_model;
    for (input.arguments) |*value| canonicalize(value);
    if (input.expected) |*value| canonicalize(value);
    if (input.fn_value == null) return diagnostic(r, input, "RunFunctionNotFound", "The entry function does not exist.");
    const fn_value = input.fn_value.?;
    const parameters = fn_value.parameters();
    const rt = try infer.typeOf(r.a, fn_value.resultType());
    var occurring: std.ArrayList([]const u8) = .empty;
    for (parameters) |parameter| try preflight.occurring(r.a, try infer.typeOf(r.a, parameter.declaredType()), &occurring);
    try preflight.occurring(r.a, rt, &occurring);
    if (fn_value.typeParameters().len > 0 or occurring.items.len > 0) return diagnostic(r, input, "RunGenericEntryUnsupported", "A generic function cannot be an entry point; run a concrete function that calls it.");
    if (try taskBearing(r, rt, &.{})) return diagnostic(r, input, "RunTaskTypeUnsupported", "Task, OneShot and TaskScope values, directly or inside a declared type, are not runPure values; run a function that waits for its tasks and returns their results.");
    for (parameters) |parameter| if (try taskBearing(r, try infer.typeOf(r.a, parameter.declaredType()), &.{})) return diagnostic(r, input, "RunTaskTypeUnsupported", "Task, OneShot and TaskScope values, directly or inside a declared type, are not runPure values; run a function that waits for its tasks and returns their results.");
    if (parameters.len != input.arguments.len) return diagnostic(r, input, "RunArgumentCountMismatch", "The number of arguments differs from the function signature.");
    for (parameters, input.arguments) |parameter, value| if (!infer.eqlType(try infer.typeOf(r.a, parameter.declaredType()), value.type)) return validationError(r, input, "RunArgumentTypeMismatch", "An argument has the wrong type; capability parameters take a fakeCapability binding.");
    if (input.has_host_binding != (input.host_sources.len > 0)) return diagnostic(r, input, "RunHostSourceInvalid", "hostSources are required exactly when an argument is a hostCapability binding.");
    for (input.arguments) |value| if (try containsBinding(r, value)) return diagnostic(r, input, "RunBindingPlacement", "Fake capabilities are accepted only as top-level arguments for capability parameters.");
    if (input.expected) |value| if (try containsBinding(r, value)) return diagnostic(r, input, "RunBindingPlacement", "Fake capabilities are accepted only as top-level arguments for capability parameters.");
    for (input.arguments) |value| if (try fakeProblem(r, value)) |problem| return diagnostic(r, input, problem.code, problem.message);
    for (input.arguments) |value| if (!try validNamed(r, value, .product)) return validationError(r, input, "RunProductShapeMismatch", "Product arguments must supply each declared field exactly once with its exact type (at most 256 fields), including nested Products.");
    for (input.arguments) |value| if (!try validNamed(r, value, .union_value)) return validationError(r, input, "RunUnionShapeMismatch", "Union arguments must name a variant of the declared Union and supply each field exactly once with its exact type, including nested Unions.");
    for (input.arguments) |value| if (!try validContainers(r, value)) return validationError(r, input, "RunContainerTypeMismatch", "List/Option item, Set item, Map key and value, and Result payload types must be supported and match every payload, and Set items and Map keys must be distinct. Scalar containers accept typed values or their legacy flat form.");
    if (!try supported(r, rt, 0)) return diagnostic(r, input, "RunResultTypeUnsupported", "Supported results are String, Bool, Int64, Float64, Bits32, Bytes and Unit values and List/Option/Result/Product/Union compositions of them with at most eight composite layers along any non-recursive path; function, capability and type-parameter types are not run values.");
    if (input.expected) |value| {
        if (!infer.eqlType(value.type, rt)) return validationError(r, input, "RunExpectedTypeMismatch", "The expected result type differs from the declared result type.");
        if (!try validNamed(r, value, .product)) return validationError(r, input, "RunProductShapeMismatch", "A Product expectation must supply each declared field exactly once with its exact type.");
        if (!try validNamed(r, value, .union_value)) return validationError(r, input, "RunUnionShapeMismatch", "A Union expectation must name a variant of the declared Union and supply each field exactly once with its exact type.");
        if (!try validContainers(r, value)) return validationError(r, input, "RunContainerTypeMismatch", "A nested expectation has an unsupported or mismatched item type.");
    }
    for (input.arguments) |value| if (try oversizedConstructors(r.a, value)) return diagnostic(r, input, "RunArgumentTooLarge", "Lists are limited to 4096 items; Product/Union constructors to 256 fields; each value to 65536 total String UTF-16 code units.");
    if (input.expected) |value| if (try oversizedConstructors(r.a, value)) return diagnostic(r, input, "RunArgumentTooLarge", "Lists are limited to 4096 items; Product/Union constructors to 256 fields; each value to 65536 total String UTF-16 code units.");
    var stats: std.ArrayList(StringSize) = .empty;
    for (input.arguments) |value| try stats.append(r.a, try stringSize(r.a, value));
    if (input.expected) |value| try stats.append(r.a, try stringSize(r.a, value));
    for (stats.items) |s| if (s.flat_list) return diagnostic(r, input, "RunArgumentTooLarge", "Lists are limited to 4096 items; Product/Union constructors to 256 fields; each value to 65536 total String UTF-16 code units.");
    for (stats.items) |s| if (s.max > 65536) return diagnostic(r, input, "RunArgumentTooLarge", "A String argument or expectation is null or exceeds 65536 UTF-16 code units.");
    for (stats.items) |s| if (s.total > 65536) return diagnostic(r, input, "RunArgumentTooLarge", "Each value is limited to 65536 aggregate String UTF-16 code units.");
}
fn textToken(a: Allocator, text: []const u8) ![]const u8 {
    const units = try std.unicode.utf8ToUtf16LeAlloc(a, text);
    defer a.free(units);
    const bytes = std.mem.sliceAsBytes(units);
    const out = try a.alloc(u8, 1 + std.base64.standard.Encoder.calcSize(bytes.len));
    out[0] = 's';
    _ = std.base64.standard.Encoder.encode(out[1..], bytes);
    return out;
}
fn latinToken(a: Allocator, bytes: []const u8) ![]const u8 {
    const units = try a.alloc(u16, bytes.len);
    defer a.free(units);
    for (bytes, units) |byte, *unit| unit.* = byte;
    const raw = std.mem.sliceAsBytes(units);
    const out = try a.alloc(u8, 1 + std.base64.standard.Encoder.calcSize(raw.len));
    out[0] = 's';
    _ = std.base64.standard.Encoder.encode(out[1..], raw);
    return out;
}
fn tokenAdd(r: *q.Request, out: *std.ArrayList([]const u8), n: anytype) !void {
    try out.append(r.a, try std.fmt.allocPrint(r.a, "{d}", .{n}));
}
fn valueTokens(r: *q.Request, v: RunValue, out: *std.ArrayList([]const u8)) Fail!void {
    switch (v.kind) {
        .unit => try out.append(r.a, "u"),
        .string => try out.append(r.a, try textToken(r.a, v.text)),
        .boolean => try out.append(r.a, if (v.boolean) "1" else "0"),
        .int64, .bits32 => try tokenAdd(r, out, v.integer),
        .float64 => try out.append(r.a, try std.fmt.allocPrint(r.a, "{x:0>16}", .{v.bits})),
        .bytes => try out.append(r.a, try latinToken(r.a, v.text)),
        .list, .set => {
            try tokenAdd(r, out, v.items.len);
            for (v.items) |item| try valueTokens(r, item, out);
        },
        .map => {
            try tokenAdd(r, out, v.entries.len);
            for (v.entries) |entry| {
                try valueTokens(r, entry.key, out);
                try valueTokens(r, entry.value, out);
            }
        },
        .option => {
            try out.append(r.a, if (v.payload != null) "1" else "0");
            if (v.payload) |payload| try valueTokens(r, payload.*, out);
        },
        .result => {
            try out.append(r.a, if (v.boolean) "1" else "0");
            try valueTokens(r, v.payload.?.*, out);
        },
        .product, .union_value => {
            if (v.kind == .union_value) {
                const owner = find(r.typedModel().types(), v.type.named.id).?;
                for (try sorted(r.a, owner.variants()), 0..) |variant, i| if (same(q.idOf(variant), v.variant)) {
                    try tokenAdd(r, out, i);
                    break;
                };
            }
            for (v.fields) |field| try valueTokens(r, field.value, out);
        },
        .fake, .host => unreachable,
    }
}
fn responseTokens(r: *q.Request, responses: []const Response, out: *std.ArrayList([]const u8)) !void {
    try tokenAdd(r, out, responses.len);
    for (responses) |response| {
        if (response.delay > 0) {
            try out.append(r.a, "3");
            try tokenAdd(r, out, response.delay);
        }
        try out.append(r.a, switch (response.kind) {
            .ret => "0",
            .throw => "1",
            .cancellation => "2",
        });
        if (response.value) |value| try valueTokens(r, value.*, out) else try out.append(r.a, try textToken(r.a, response.message));
    }
}
pub fn tokens(r: *q.Request, input: *Input) Fail![]const []const u8 {
    const original_model = r.generation;
    r.generation = input.declarations.data.snapshot;
    defer r.generation = original_model;
    var out: std.ArrayList([]const u8) = .empty;
    for (input.arguments) |value| {
        if (value.kind == .host) continue;
        if (value.kind != .fake) {
            try valueTokens(r, value, &out);
            continue;
        }
        const owner = find(r.typedModel().capabilities(), value.type.capability).?;
        for (try sorted(r.a, owner.operations())) |op| {
            var script: ?Operation = null;
            for (value.operations) |candidate| if (same(candidate.id, q.idOf(op))) {
                script = candidate;
                break;
            };
            if (script) |operation| {
                if (operation.cases) |cases| {
                    try out.append(r.a, "1");
                    try tokenAdd(r, &out, cases.len);
                    for (cases) |case| {
                        var key: std.ArrayList([]const u8) = .empty;
                        for (case.arguments) |arg| try valueTokens(r, arg.value, &key);
                        try out.append(r.a, try std.fmt.allocPrint(r.a, "k{s}", .{try std.mem.join(r.a, ",", key.items)}));
                        try responseTokens(r, case.responses, &out);
                    }
                } else {
                    try out.appendSlice(r.a, &.{ "0", "1" });
                    try responseTokens(r, operation.responses, &out);
                }
            } else try out.appendSlice(r.a, &.{ "0", "1", "0" });
        }
    }
    return out.items;
}
fn parseText(a: Allocator, v: Json) !?[]const u8 {
    if (v != .string or v.string.len == 0 or v.string[0] != 's') return null;
    const base64 = v.string[1..];
    const n = std.base64.standard.Decoder.calcSizeForSlice(base64) catch return null;
    if (n % 2 != 0) return null;
    const units = try a.alloc(u16, n / 2);
    std.base64.standard.Decoder.decode(std.mem.sliceAsBytes(units), base64) catch return null;
    return std.unicode.utf16LeToUtf8Alloc(a, units) catch null;
}
fn invalid(r: *q.Request, input: *Input, message: []const u8) Fail {
    diagnostic(r, input, "RunInvalidResult", message) catch |e| return e;
    unreachable;
}
fn resultValue(r: *q.Request, input: *Input, t: Type, item: Json) Fail!RunValue {
    if (t == .primitive) {
        const p = t.primitive;
        if (same(p, "unit") and item == .string and same(item.string, "unit")) return .{ .kind = .unit, .type = t };
        if (same(p, "string")) return .{ .kind = .string, .type = t, .text = (try parseText(r.a, item)) orelse return invalid(r, input, "The result is not a String.") };
        if (same(p, "bool") and item == .bool) return .{ .kind = .boolean, .type = t, .boolean = item.bool };
        if (same(p, "int64") and item != .string) return .{ .kind = .int64, .type = t, .integer = integer(item) orelse return invalid(r, input, "The result is not an Int64.") };
        if (same(p, "bits32") and item != .string) {
            const n = integer(item) orelse return invalid(r, input, "The result is not a Bits32.");
            if (n < 0 or n > std.math.maxInt(u32)) return invalid(r, input, "The result is not a Bits32.");
            return .{ .kind = .bits32, .type = t, .integer = n };
        }
        if (same(p, "float64") and item == .string) {
            if (item.string.len != 16) return invalid(r, input, "The result is not Float64 bits.");
            const bits = std.fmt.parseInt(u64, item.string, 16) catch return invalid(r, input, "The result is not Float64 bits.");
            const number: f64 = @bitCast(bits);
            return .{ .kind = .float64, .type = t, .bits = if (std.math.isNan(number)) 0x7ff8000000000000 else bits };
        }
        if (same(p, "bytes")) {
            const text = (try parseText(r.a, item)) orelse return invalid(r, input, "The result is not Bytes.");
            var it = std.unicode.Utf8View.initUnchecked(text).iterator();
            var bytes: std.ArrayList(u8) = .empty;
            while (it.nextCodepoint()) |cp| {
                if (cp > 255) return invalid(r, input, "The result is not Bytes.");
                try bytes.append(r.a, @intCast(cp));
            }
            return .{ .kind = .bytes, .type = t, .text = bytes.items };
        }
    }
    if (t == .builtin and item == .array) {
        const b = t.builtin;
        const values = item.array.items;
        if (same(b.generic, "list") or same(b.generic, "set")) {
            if (values.len > 4096) {
                if (same(b.generic, "list") and scalarType(b.arguments[0])) {
                    diagnostic(r, input, "RunOutputTooLarge", "List results may contain at most 4096 elements.") catch |e| return e;
                    unreachable;
                }
                return invalid(r, input, "Invalid container result size.");
            }
            const out = try r.a.alloc(RunValue, values.len);
            for (values, out) |value, *slot| slot.* = try resultValue(r, input, b.arguments[0], value);
            return .{ .kind = if (same(b.generic, "list")) .list else .set, .type = t, .items = out };
        }
        if (same(b.generic, "option") and values.len <= 1) return .{ .kind = .option, .type = t, .payload = if (values.len == 0) null else try box(r.a, try resultValue(r, input, b.arguments[0], values[0])) };
        if (same(b.generic, "result") and values.len == 2 and values[0] == .bool) return .{ .kind = .result, .type = t, .boolean = values[0].bool, .payload = try box(r.a, try resultValue(r, input, b.arguments[if (values[0].bool) @as(usize, 0) else 1], values[1])) };
        if (same(b.generic, "map")) {
            if (values.len > 4096) return invalid(r, input, "Invalid container result size.");
            const out = try r.a.alloc(Entry, values.len);
            for (values, out) |value, *slot| {
                if (value != .array or value.array.items.len != 2) return invalid(r, input, "A Map result entry is not a key and value pair.");
                slot.* = .{ .key = try resultValue(r, input, b.arguments[0], value.array.items[0]), .value = try resultValue(r, input, b.arguments[1], value.array.items[1]) };
            }
            return .{ .kind = .map, .type = t, .entries = out };
        }
    }
    if (t == .named and item == .array) {
        const owner = find(r.typedModel().types(), t.named.id) orelse return invalid(r, input, "The result does not have the declared type.");
        const is_product = product(owner);
        var constructor = owner;
        var items = item;
        var variant: []const u8 = "";
        if (!is_product) {
            if (item.array.items.len != 2 or item.array.items[0] != .string) return invalid(r, input, "The Union result does not match its declaration.");
            variant = item.array.items[0].string;
            constructor = find(owner.variants(), variant) orelse return invalid(r, input, "The Union result does not match its declaration.");
            items = item.array.items[1];
        }
        const fields = try sorted(r.a, constructor.fields());
        if (items != .array or items.array.items.len != fields.len) return invalid(r, input, "The Product result fields do not match their declaration.");
        const out = try r.a.alloc(Field, fields.len);
        for (fields, items.array.items, out) |field, value, *slot| slot.* = .{ .id = q.idOf(field), .value = try resultValue(r, input, try instantiated(r, owner, t.named.arguments, field), value) };
        return .{ .kind = if (is_product) .product else .union_value, .type = t, .variant = variant, .fields = out };
    }
    return invalid(r, input, "The result does not have the declared type.");
}
fn floatText(a: Allocator, value: f64) ![]const u8 {
    var buffer: [std.fmt.float.bufferSize(.decimal, f64)]u8 = undefined;
    const scientific = try std.fmt.float.render(&buffer, value, .{ .mode = .scientific });
    const e = std.mem.indexOfScalar(u8, scientific, 'e').?;
    const exponent = try std.fmt.parseInt(i32, scientific[e + 1 ..], 10);
    if (exponent >= -4 and exponent < 17) return a.dupe(u8, try std.fmt.float.render(&buffer, value, .{ .mode = .decimal }));
    return std.fmt.allocPrint(a, "{s}E{s}{d:0>2}", .{ scientific[0..e], if (exponent < 0) "-" else "+", @abs(exponent) });
}
pub fn encoded(r: *q.Request, v: RunValue) Fail!model.Value {
    return encodeValue(r, v, true);
}
pub fn encodedInput(r: *q.Request, value: RunValue) Fail!model.Value {
    return encodeValue(r, value, false);
}
fn encodeValue(r: *q.Request, v: RunValue, canonical: bool) Fail!model.Value {
    var fields: std.ArrayList(model.Member) = .empty;
    const kind: []const u8 = switch (v.kind) {
        .string => "string",
        .boolean => "bool",
        .int64 => "int64",
        .float64 => "float64",
        .bits32 => "bits32",
        .bytes => "bytes",
        .unit => "unit",
        .list => "list",
        .set => "set",
        .map => "map",
        .option => "option",
        .result => "result",
        .product => "product",
        .union_value => "union",
        .fake => "fakeCapability",
        .host => "hostCapability",
    };
    try fields.append(r.a, try mat.member(r.a, "kind", try mat.string(r.a, kind)));
    switch (v.kind) {
        .unit => {},
        .string => try fields.append(r.a, try mat.member(r.a, "value", try mat.string(r.a, v.text))),
        .boolean => try fields.append(r.a, try mat.member(r.a, "value", boolJson(v.boolean))),
        .int64, .bits32 => try fields.append(r.a, try mat.member(r.a, "value", try mat.string(r.a, try std.fmt.allocPrint(r.a, "{d}", .{v.integer})))),
        .float64 => {
            const number: f64 = @bitCast(v.bits);
            const value: model.Value = if (std.math.isNan(number)) try mat.string(r.a, "NaN") else if (std.math.isInf(number)) try mat.string(r.a, if (number > 0) "Infinity" else "-Infinity") else if (v.bits == 0x8000000000000000) try mat.string(r.a, "-0") else .{ .number = try floatText(r.a, number) };
            try fields.append(r.a, try mat.member(r.a, "value", value));
        },
        .bytes => {
            const hex = try r.a.alloc(u8, v.text.len * 2);
            for (v.text, 0..) |byte, i| {
                hex[i * 2] = std.fmt.digitToChar(@intCast(byte >> 4), .lower);
                hex[i * 2 + 1] = std.fmt.digitToChar(@intCast(byte & 15), .lower);
            }
            try fields.append(r.a, try mat.member(r.a, "hex", try mat.string(r.a, hex)));
        },
        .list, .set => {
            const t = v.type.builtin.arguments[0];
            const flat = v.kind == .list and scalarType(t) and (canonical or v.legacy);
            try fields.append(r.a, try mat.member(r.a, if (flat) "itemKind" else "itemType", if (flat) try mat.string(r.a, t.primitive) else try typeValue(r, t)));
            const items = try r.a.alloc(model.Value, v.items.len);
            for (v.items, items) |item, *slot| {
                const value = try encodeValue(r, item, canonical);
                slot.* = if (flat) model.property(value, "value").? else value;
            }
            try fields.append(r.a, try mat.member(r.a, "items", .{ .array = items }));
        },
        .option => {
            const t = v.type.builtin.arguments[0];
            const flat = scalarType(t) and (canonical or v.legacy);
            try fields.append(r.a, try mat.member(r.a, if (flat) "itemKind" else "itemType", if (flat) try mat.string(r.a, t.primitive) else try typeValue(r, t)));
            try fields.append(r.a, try mat.member(r.a, "hasValue", boolJson(v.payload != null)));
            if (v.payload) |payload| {
                const value = try encodeValue(r, payload.*, canonical);
                try fields.append(r.a, try mat.member(r.a, "value", if (flat) model.property(value, "value").? else value));
            }
        },
        .result => {
            const ts = v.type.builtin.arguments;
            const flat = scalarType(ts[0]) and scalarType(ts[1]) and (canonical or v.legacy);
            try fields.append(r.a, try mat.member(r.a, if (flat) "okKind" else "okType", if (flat) try mat.string(r.a, ts[0].primitive) else try typeValue(r, ts[0])));
            try fields.append(r.a, try mat.member(r.a, if (flat) "errorKind" else "errorType", if (flat) try mat.string(r.a, ts[1].primitive) else try typeValue(r, ts[1])));
            try fields.append(r.a, try mat.member(r.a, "isOk", boolJson(v.boolean)));
            const value = try encodeValue(r, v.payload.?.*, canonical);
            try fields.append(r.a, try mat.member(r.a, "value", if (flat) model.property(value, "value").? else value));
        },
        .map => {
            try fields.append(r.a, try mat.member(r.a, "keyType", try typeValue(r, v.type.builtin.arguments[0])));
            try fields.append(r.a, try mat.member(r.a, "valueType", try typeValue(r, v.type.builtin.arguments[1])));
            const entries = try r.a.alloc(model.Value, v.entries.len);
            for (v.entries, entries) |entry, *slot| slot.* = try object(r, .{ .{ "key", try encodeValue(r, entry.key, canonical) }, .{ "value", try encodeValue(r, entry.value, canonical) } });
            try fields.append(r.a, try mat.member(r.a, "entries", .{ .array = entries }));
        },
        .product, .union_value => {
            try fields.append(r.a, try mat.member(r.a, "typeId", try mat.string(r.a, v.type.named.id)));
            try named(r, &fields, "type", v.type.named.id);
            if (v.type.named.arguments.len > 0) {
                const args = try r.a.alloc(model.Value, v.type.named.arguments.len);
                for (v.type.named.arguments, args) |t, *slot| slot.* = try typeValue(r, t);
                try fields.append(r.a, try mat.member(r.a, "typeArguments", .{ .array = args }));
            }
            if (v.kind == .union_value) {
                try fields.append(r.a, try mat.member(r.a, "variantId", try mat.string(r.a, v.variant)));
                try named(r, &fields, "variant", v.variant);
            }
            const fs = try r.a.alloc(model.Value, v.fields.len);
            for (v.fields, fs) |field, *slot| {
                var f: std.ArrayList(model.Member) = .empty;
                try f.append(r.a, try mat.member(r.a, "fieldId", try mat.string(r.a, field.id)));
                try named(r, &f, "field", field.id);
                try f.append(r.a, try mat.member(r.a, "value", try encodeValue(r, field.value, canonical)));
                slot.* = .{ .object = f.items };
            }
            try fields.append(r.a, try mat.member(r.a, "fields", .{ .array = fs }));
        },
        .host, .fake => {
            try fields.append(r.a, try mat.member(r.a, "capabilityId", try mat.string(r.a, v.type.capability)));
            try named(r, &fields, "capability", v.type.capability);
            if (v.kind == .host) {
                try fields.append(r.a, try mat.member(r.a, "implementation", try mat.string(r.a, v.text)));
                try fields.append(r.a, try mat.member(r.a, "supportsConcurrentCalls", boolJson(v.boolean)));
            } else {
                const operations = try r.a.alloc(model.Value, v.operations.len);
                for (v.operations, operations) |op, *value| {
                    var members: std.ArrayList(model.Member) = .empty;
                    try members.append(r.a, try mat.member(r.a, "operationId", try mat.string(r.a, op.id)));
                    try named(r, &members, "operation", op.id);
                    if (op.cases) |cases_| {
                        const cases = try r.a.alloc(model.Value, cases_.len);
                        for (cases_, cases) |case, *cs| {
                            const arguments = try r.a.alloc(model.Value, case.arguments.len);
                            for (case.arguments, arguments) |arg, *out| {
                                var members_: std.ArrayList(model.Member) = .empty;
                                try members_.append(r.a, try mat.member(r.a, "capabilityParameterId", try mat.string(r.a, arg.id)));
                                try named(r, &members_, "parameter", arg.id);
                                try members_.append(r.a, try mat.member(r.a, "value", try encodeValue(r, arg.value, canonical)));
                                out.* = .{ .object = members_.items };
                            }
                            cs.* = try object(r, .{ .{ "arguments", model.Value{ .array = arguments } }, .{ "responses", try encodeResponses(r, case.responses, canonical) } });
                        }
                        try members.append(r.a, try mat.member(r.a, "cases", .{ .array = cases }));
                    } else try members.append(r.a, try mat.member(r.a, "responses", try encodeResponses(r, op.responses, canonical)));
                    value.* = .{ .object = members.items };
                }
                try fields.append(r.a, try mat.member(r.a, "operations", .{ .array = operations }));
            }
        },
    }
    return .{ .object = fields.items };
}
fn encodeResponses(r: *q.Request, responses: []const Response, canonical: bool) Fail!model.Value {
    const out = try r.a.alloc(model.Value, responses.len);
    for (responses, out) |response, *slot| {
        var fields: std.ArrayList(model.Member) = .empty;
        const kind = switch (response.kind) {
            .ret => "return",
            .throw => "throw",
            .cancellation => "awaitCancellation",
        };
        try fields.append(r.a, try mat.member(r.a, "kind", try mat.string(r.a, kind)));
        if (response.value) |value| try fields.append(r.a, try mat.member(r.a, "value", try encodeValue(r, value.*, canonical))) else try fields.append(r.a, try mat.member(r.a, "message", try mat.string(r.a, response.message)));
        if (response.delay > 0 and response.kind != .cancellation) try fields.append(r.a, try mat.member(r.a, "delayMillis", try mat.string(r.a, try std.fmt.allocPrint(r.a, "{d}", .{response.delay}))));
        slot.* = .{ .object = fields.items };
    }
    return .{ .array = out };
}

const Difference = struct { path: []const model.Value, expected: RunValue, actual: RunValue };
fn difference(r: *q.Request, x: RunValue, y: RunValue, path: []const model.Value) Fail!?Difference {
    if (equal(x, y)) return null;
    const here: Difference = .{ .path = path, .expected = x, .actual = y };
    if (!infer.eqlType(x.type, y.type)) return here;
    if ((x.kind == .product and y.kind == .product) or (x.kind == .union_value and y.kind == .union_value and same(x.variant, y.variant))) {
        if (x.fields.len != y.fields.len) return here;
        for (x.fields, y.fields) |p, z| if (!same(p.id, z.id)) return here;
        for (x.fields, y.fields) |p, z| {
            var step: std.ArrayList(model.Member) = .empty;
            try step.append(r.a, try mat.member(r.a, "kind", try mat.string(r.a, "field")));
            try step.append(r.a, try mat.member(r.a, "typeId", try mat.string(r.a, x.type.named.id)));
            try named(r, &step, "type", x.type.named.id);
            try step.append(r.a, try mat.member(r.a, "fieldId", try mat.string(r.a, p.id)));
            try named(r, &step, "field", p.id);
            if (try difference(r, p.value, z.value, try valuePath(r.a, path, .{ .object = step.items }))) |found| return found;
        }
    } else if (x.kind == .list and y.kind == .list) {
        const n = @min(x.items.len, y.items.len);
        for (x.items[0..n], y.items[0..n], 0..) |p, z, i| if (try difference(r, p, z, try valuePath(r.a, path, try object(r, .{ .{ "kind", try mat.string(r.a, "item") }, .{ "index", try mat.number(r.a, i) } })))) |found| return found;
    } else if ((x.kind == .option and y.kind == .option and x.payload != null and y.payload != null) or (x.kind == .result and y.kind == .result and x.boolean == y.boolean)) {
        const step = try object(r, .{.{ "kind", try mat.string(r.a, if (x.kind == .option) "some" else if (x.boolean) "ok" else "err") }});
        return difference(r, x.payload.?.*, y.payload.?.*, try valuePath(r.a, path, step));
    }
    return here;
}
fn valuePath(a: Allocator, path: []const model.Value, step: model.Value) ![]const model.Value {
    const out = try a.alloc(model.Value, path.len + 1);
    @memcpy(out[0..path.len], path);
    out[path.len] = step;
    return out;
}
const Site = struct { parameter: Model, operation: Model };
fn sites(r: *q.Request, input: *Input) ![]const Site {
    var out: std.ArrayList(Site) = .empty;
    for (input.fn_value.?.parameters(), input.arguments) |parameter, arg| if (arg.kind == .fake or arg.kind == .host) {
        const capability = find(r.typedModel().capabilities(), arg.type.capability).?;
        for (try sorted(r.a, capability.operations())) |operation| try out.append(r.a, .{ .parameter = parameter, .operation = operation });
    };
    return out.items;
}
fn calls(r: *q.Request, input: *Input, json: Json) Fail!model.Value {
    if (json != .array) return invalid(r, input, "The process did not return one JSON result.");
    const all_sites = try sites(r, input);
    const out = try r.a.alloc(model.Value, json.array.items.len);
    var captured: std.ArrayList(CapturedCall) = .empty;
    for (json.array.items, out) |call, *slot| {
        if (call != .array or call.array.items.len != 2) return invalid(r, input, "The process did not return one JSON result.");
        const data = call.array.items;
        const n = integer(data[0]) orelse return invalid(r, input, "The process did not return one JSON result.");
        if (n < 0 or n >= all_sites.len or data[1] != .array) return invalid(r, input, "The process did not return one JSON result.");
        const site = all_sites[@intCast(n)];
        var ps: std.ArrayList(Model) = .empty;
        for (site.operation.parameters()) |parameter| if (try supported(r, try infer.typeOf(r.a, parameter.declaredType()), 0)) try ps.append(r.a, parameter);
        if (data[1].array.items.len != ps.items.len) return invalid(r, input, "The process did not return one JSON result.");
        const args = try r.a.alloc(Field, ps.items.len);
        for (ps.items, data[1].array.items, args) |parameter, value, *arg| arg.* = .{ .id = q.idOf(parameter), .value = try resultValue(r, input, try infer.typeOf(r.a, parameter.declaredType()), value) };
        sortFields(args);
        if (input.capture != null) try captured.append(r.a, .{ .parameter_id = q.idOf(site.parameter), .operation_id = q.idOf(site.operation), .arguments = args });
        const arguments = try r.a.alloc(model.Value, args.len);
        for (args, arguments) |arg, *value| {
            var f: std.ArrayList(model.Member) = .empty;
            try f.append(r.a, try mat.member(r.a, "capabilityParameterId", try mat.string(r.a, arg.id)));
            try named(r, &f, "parameter", arg.id);
            try f.append(r.a, try mat.member(r.a, "value", try encoded(r, arg.value)));
            value.* = .{ .object = f.items };
        }
        var f: std.ArrayList(model.Member) = .empty;
        try f.append(r.a, try mat.member(r.a, "parameterId", try mat.string(r.a, q.idOf(site.parameter))));
        try named(r, &f, "parameter", q.idOf(site.parameter));
        try f.append(r.a, try mat.member(r.a, "operationId", try mat.string(r.a, q.idOf(site.operation))));
        try operationNames(r, &f, q.idOf(site.operation));
        try f.append(r.a, try mat.member(r.a, "arguments", .{ .array = arguments }));
        slot.* = .{ .object = f.items };
    }
    if (input.capture) |capture| capture.calls = captured.items;
    return .{ .array = out };
}
fn operationNames(r: *q.Request, out: *std.ArrayList(model.Member), id: []const u8) !void {
    for (r.typedModel().capabilities()) |cap| {
        if (find(cap.operations(), id) != null) {
            try out.append(r.a, try mat.member(r.a, "capability", try mat.string(r.a, q.nameOf(cap))));
            break;
        }
    }
    try named(r, out, "operation", id);
}
fn csharpFrames(r: *q.Request, input: *Input, names: Json) Fail!model.Value {
    var frames: std.ArrayList(model.Value) = .empty;
    if (input.csharp_symbols != .object or names != .array) return .{ .array = frames.items };
    for (names.array.items) |frame| {
        if (frames.items.len >= 256) break;
        if (frame != .string) continue;
        for ([_][]const u8{ "functions", "capabilityOperations" }) |group| {
            const symbols = input.csharp_symbols.object.get(group) orelse continue;
            if (symbols != .array) continue;
            for (symbols.array.items) |symbol| {
                if (symbol != .object) continue;
                const name = symbol.object.get("name") orelse continue;
                const id = symbol.object.get("id") orelse continue;
                if (name != .string or id != .string or !same(name.string, frame.string)) continue;
                var fields: std.ArrayList(model.Member) = .empty;
                const function = same(group, "functions");
                try fields.append(r.a, try mat.member(r.a, "kind", try mat.string(r.a, if (function) "function" else "capabilityOperation")));
                try fields.append(r.a, try mat.member(r.a, if (function) "functionId" else "operationId", try mat.string(r.a, id.string)));
                if (function) try named(r, &fields, "function", id.string) else try operationNames(r, &fields, id.string);
                try frames.append(r.a, .{ .object = fields.items });
                break;
            }
        }
    }
    return .{ .array = frames.items };
}
fn runtimeFailure(r: *q.Request, input: *Input, kind: []const u8, exception_type: []const u8, message: []const u8, recorded: model.Value, exhausted: bool) Fail!void {
    return runtimeFailureFrames(r, input, kind, exception_type, message, recorded, exhausted, .null);
}
fn runtimeFailureFrames(r: *q.Request, input: *Input, kind: []const u8, exception_type: []const u8, message: []const u8, recorded: model.Value, exhausted: bool, frames: Json) Fail!void {
    const code: []const u8 = if (exhausted) "RunBindingExhausted" else if (same(exception_type, "FakeArgumentsUnmatchedException")) "RunBindingNoMatch" else "RunRuntimeFailure";
    diagnostic(r, input, code, message) catch |e| {
        var out: Writer.Allocating = .init(r.a);
        try out.writer.writeAll(r.rejection.fields);
        const extra = try object(r, .{ .{ "sourceSnapshotId", try mat.string(r.a, r.snapshot_id) }, .{ "failureKind", try mat.string(r.a, kind) }, .{ "exceptionType", try mat.string(r.a, exception_type) }, .{ "frames", try csharpFrames(r, input, frames) }, .{ "calls", recorded } });
        for (extra.object) |field| {
            try out.writer.writeByte(',');
            try model.writeString(&out.writer, field.name);
            try out.writer.writeByte(':');
            try mat.write(&out.writer, field.value);
        }
        r.rejection.fields = out.written();
        return e;
    };
}
pub fn crash(r: *q.Request, input: *Input, exit_code: i32, stderr: []const u8) Fail!void {
    var lines = std.mem.splitScalar(u8, stderr, '\n');
    var panic: ?[]const u8 = null;
    while (lines.next()) |line| if (std.mem.startsWith(u8, line, "panic: ")) {
        panic = line[7..];
        break;
    } else if (exit_code == 86 and std.mem.startsWith(u8, line, "leak: ")) {
        // The conformance runner's leak check (emitter runner_support.zig, `nyLeakCheck`).
        return runtimeFailure(r, input, "fatal", "ZigLeak", line[6..], .{ .array = &.{} }, false);
    };
    return runtimeFailure(r, input, "fatal", if (panic != null) "ZigPanic" else "ZigCrash", panic orelse try std.fmt.allocPrint(r.a, "The Zig program stopped with exit code {d} (for example a stack overflow).", .{exit_code}), .{ .array = &.{} }, false);
}
pub fn csharpCrash(r: *q.Request, input: *Input, stdout: []const u8, stderr: []const u8) Fail!void {
    if (std.mem.indexOf(u8, stderr, "Stack overflow") != null) {
        var frames: std.array_list.Managed(Json) = .init(r.a);
        var i: usize = 0;
        while (i < stderr.len) : (i += 1) {
            if (!std.mem.startsWith(u8, stderr[i..], "NyF_") and !std.mem.startsWith(u8, stderr[i..], "Op_")) continue;
            const start = i;
            i += if (stderr[i] == 'N') @as(usize, 4) else 3;
            while (i < stderr.len and (std.ascii.isDigit(stderr[i]) or (stderr[i] >= 'A' and stderr[i] <= 'F'))) : (i += 1) {}
            try frames.append(.{ .string = stderr[start..i] });
        }
        const original = r.generation;
        r.generation = input.declarations.data.snapshot;
        defer r.generation = original;
        return runtimeFailureFrames(r, input, "fatal", "System.StackOverflowException", "Stack overflow: recursion was too deep. A self DirectCall in tail position runs as a loop (the body's result, a Bind body, a Conditional branch, a match arm or otherwise, or the right operand of BoolAnd/BoolOr); any other self or mutual DirectCall recursion runs on the runner's thread stack, which holds only several thousand frames of a typical function. Make the self call the last step of each continuing branch, or iterate a List with ListFold (for text, over StringCodePoints).", .{ .array = &.{} }, false, .{ .array = frames });
    }
    const details = try std.mem.concat(r.a, u8, &.{ stdout, "\n", stderr });
    return diagnostic(r, input, "RunProcessFailure", try @import("managed_text.zig").prefix(r.a, details, 4096));
}
fn envelopeWithinDepth(a: Allocator, text: []const u8) bool {
    var scanner = std.json.Scanner.initCompleteInput(a, text);
    defer scanner.deinit();
    while (true) {
        const token = scanner.next() catch return false;
        if (scanner.stackHeight() > 256) return false;
        if (token == .end_of_document) return true;
    }
}

pub fn envelope(r: *q.Request, input: *Input, stdout: []const u8, out: *Writer) Fail!void {
    const original_model = r.generation;
    r.generation = input.declarations.data.snapshot;
    defer r.generation = original_model;
    if (!envelopeWithinDepth(r.a, stdout)) return invalid(r, input, "The process did not return one JSON result.");
    const parsed = std.json.parseFromSlice(Json, r.a, stdout, .{ .allocate = .alloc_always, .parse_numbers = false }) catch return invalid(r, input, "The process did not return one JSON result.");
    const root = parsed.value;
    if (root != .array or root.array.items.len < 2 or root.array.items[0] != .string) return invalid(r, input, "The process did not return one JSON result.");
    const values = root.array.items;
    const tag = values[0].string;
    if (same(tag, "limit") and values.len == 2 and values[1] == .string) return diagnostic(r, input, "RunProcessFailure", values[1].string);
    if (same(tag, "exception") and values.len == 7 and values[1] == .string and values[3] == .array and values[5] == .bool and values[6] == .string) {
        const text = (try parseText(r.a, values[2])) orelse return invalid(r, input, "The process did not return one JSON result.");
        return runtimeFailureFrames(r, input, values[6].string, values[1].string, text, try calls(r, input, values[4]), values[5].bool, values[3]);
    }
    if (!same(tag, "value") or values.len != 3) return invalid(r, input, "The process did not return one JSON result.");
    const actual = try resultValue(r, input, try infer.typeOf(r.a, input.fn_value.?.resultType()), values[1]);
    const recorded = try calls(r, input, values[2]);
    if (input.capture) |capture| capture.actual = actual;
    var fields: std.ArrayList(model.Member) = .empty;
    try fields.append(r.a, try mat.member(r.a, "sourceSnapshotId", try mat.string(r.a, r.snapshot_id)));
    try fields.append(r.a, try mat.member(r.a, "functionId", try mat.string(r.a, input.function_id)));
    try named(r, &fields, "function", input.function_id);
    try fields.append(r.a, try mat.member(r.a, "actual", try encoded(r, actual)));
    try fields.append(r.a, try mat.member(r.a, "expected", if (input.expected) |expected| try encoded(r, expected) else .json_null));
    try fields.append(r.a, try mat.member(r.a, "matches", if (input.expected) |expected| boolJson(equal(expected, actual)) else .json_null));
    var mismatch: model.Value = .json_null;
    if (input.expected) |expected| if (try difference(r, expected, actual, &.{})) |diff| {
        mismatch = try object(r, .{ .{ "path", model.Value{ .array = diff.path } }, .{ "expected", try encoded(r, diff.expected) }, .{ "actual", try encoded(r, diff.actual) } });
    };
    try fields.append(r.a, try mat.member(r.a, "mismatch", mismatch));
    try fields.append(r.a, try mat.member(r.a, "calls", recorded));
    try mat.write(out, .{ .object = fields.items });
}

test "binary64 output follows the wire's round-trip decimal convention" {
    var arena: std.heap.ArenaAllocator = .init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    try std.testing.expectEqualStrings("0.0001", try floatText(a, 1e-4));
    try std.testing.expectEqualStrings("1E-05", try floatText(a, 1e-5));
    try std.testing.expectEqualStrings("10000000000000000", try floatText(a, 1e16));
    try std.testing.expectEqualStrings("1.234E+17", try floatText(a, 1.234e17));
    try std.testing.expectEqualStrings("5E-324", try floatText(a, @as(f64, @bitCast(@as(u64, 1)))));
}
test "named values preserve wire field order until semantic canonicalization" {
    const t: Type = .{ .named = .{ .id = "Pair", .arguments = &.{} } };
    const scalar: RunValue = .{ .kind = .int64, .type = primitive("int64"), .integer = 1 };
    var left_fields = [_]Field{ .{ .id = "b", .value = scalar }, .{ .id = "a", .value = scalar } };
    var right_fields = [_]Field{ .{ .id = "a", .value = scalar }, .{ .id = "b", .value = scalar } };
    var left: RunValue = .{ .kind = .product, .type = t, .fields = &left_fields };
    var right: RunValue = .{ .kind = .product, .type = t, .fields = &right_fields };
    try std.testing.expect(!equalRaw(left, right));
    canonicalize(&left);
    canonicalize(&right);
    try std.testing.expect(equal(left, right));
}

test "run values keep exact integers, binary64 signs, and text tokens" {
    try std.testing.expectEqual(@as(?i64, std.math.maxInt(i64)), integer(.{ .number_string = "9223372036854775807" }));
    try std.testing.expectEqual(@as(?i64, null), integer(.{ .number_string = "9223372036854775808" }));
    const x: RunValue = .{ .kind = .float64, .type = primitive("float64"), .bits = 0 };
    var y = x;
    y.bits = 0x8000000000000000;
    try std.testing.expect(!equal(x, y));
    const a = std.testing.allocator;
    const token = try textToken(a, "é😀");
    defer a.free(token);
    var arena: std.heap.ArenaAllocator = .init(a);
    defer arena.deinit();
    try std.testing.expectEqualStrings("é😀", (try parseText(arena.allocator(), .{ .string = token })).?);
}

test "scalar decoding preserves negative zero and rejects unsigned negative zero" {
    var arena: std.heap.ArenaAllocator = .init(std.testing.allocator);
    defer arena.deinit();
    var request: q.Request = .{ .a = arena.allocator(), .line = "", .root = .empty, .provenance = null };
    var d: Decoder = .{ .names = false, .d = .{ .r = &request, .duplicates = .empty } };
    try std.testing.expectEqual(@as(u64, 0x8000000000000000), (try d.leaf("float64", .{ .number_string = "-0" }, "arguments[0]", true)).bits);
    try std.testing.expectError(error.Rejected, d.leaf("bits32", .{ .number_string = "-0" }, "arguments[0]", true));
    try std.testing.expectEqualStrings("ProtocolError", request.rejection.code);
}

test "run declarations hide imported private members without changing the model" {
    var arena: std.heap.ArenaAllocator = .init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const root = try model.parseValue(a,
        \\{"snapshotId":"snapshot","functions":[],"effects":[],"capabilities":[],"types":[{"id":"hidden","displayName":"Hidden","kind":"product","fields":[]},{"id":"visible","displayName":"Visible","kind":"product","fields":[]}]}
    );
    var interner: tm.Interner = .{ .gpa = a };
    defer interner.deinit();
    const generation = try tm.load(a, &interner, root, null);
    var request: q.Request = .{ .a = a, .line = "", .root = .empty, .generation = &generation, .interner = &interner, .provenance = .{ .members = &.{}, .hidden = &.{"type:hidden"} } };
    const view = try declarationView(&request);
    try std.testing.expectEqual(@as(usize, 1), view.types().len);
    try std.testing.expectEqualStrings("visible", q.idOf(view.types()[0]));
    try std.testing.expectEqual(@as(usize, 2), request.typedModel().types().len);
    try std.testing.expectEqualStrings("snapshot", q.idOf(view));
}

test "nested fake response diagnostics include the operation response and field path" {
    var arena: std.heap.ArenaAllocator = .init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const root = try model.parseValue(a,
        \\{"snapshotId":"snapshot","types":[{"id":"Answer","displayName":"Answer","kind":"union","variants":[{"id":"Found","displayName":"Found","fields":[{"id":"text","displayName":"text","type":{"kind":"primitive","primitive":"string"}}]}]}],"capabilities":[{"id":"Provider","displayName":"Provider","operations":[{"id":"lookup","displayName":"lookup","parameters":[],"resultType":{"kind":"named","typeId":"Answer"},"effectId":"effect"}]}]}
    );
    var interner: tm.Interner = .{ .gpa = a };
    defer interner.deinit();
    const generation = try tm.load(a, &interner, root, null);
    var request: q.Request = .{ .a = a, .line = "", .root = .empty, .generation = &generation, .interner = &interner, .provenance = null };
    var fields = [_]Field{.{ .id = "text", .value = .{ .kind = .int64, .type = primitive("int64"), .integer = 1 } }};
    var payload: RunValue = .{ .kind = .union_value, .type = .{ .named = .{ .id = "Answer", .arguments = &.{} } }, .variant = "Found", .fields = &fields };
    var responses = [_]Response{.{ .kind = .ret, .value = &payload }};
    var operations = [_]Operation{.{ .id = "lookup", .responses = &responses }};
    const value: RunValue = .{ .kind = .fake, .type = .{ .capability = "Provider" }, .operations = &operations };
    const issue = (try inputIssue(&request, "RunUnionShapeMismatch", "RunArgumentTypeMismatch", &.{ "arguments", "provider" }, value.type, value)).?;
    try std.testing.expectEqualStrings("arguments.provider.operations[0].responses[0].value.Answer.Found.text", try std.mem.join(a, ".", issue.path));
    try std.testing.expectEqualStrings("The value type differs from the declared type.", issue.reason);
}

test "plain constructor size checks include nested 257-field values" {
    var arena: std.heap.ArenaAllocator = .init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    var fields: [257]Field = undefined;
    for (&fields) |*field| field.* = .{ .id = "field", .value = .{ .kind = .unit, .type = primitive("unit") } };
    const t: Type = .{ .named = .{ .id = "Large", .arguments = &.{} } };
    var value: RunValue = .{ .kind = .product, .type = t, .fields = fields[0..256] };
    try std.testing.expect(!try oversizedConstructors(a, value));
    value.fields = &fields;
    var child = value;
    const option: RunValue = .{ .kind = .option, .type = try generic(a, "option", &.{t}), .payload = &child };
    try std.testing.expect(try oversizedConstructors(a, option));
}

test "produced recursive values follow the whole envelope JSON depth limit" {
    var arena: std.heap.ArenaAllocator = .init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const root = try model.parseValue(a,
        \\{"snapshotId":"snapshot","types":[{"id":"Node","displayName":"Node","kind":"product","fields":[{"id":"next","displayName":"next","type":{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"named","typeId":"Node"}]},"requiredEffects":[],"body":{"nodes":[{"id":"m","kind":"unitLiteral"}],"resultNodeId":"m"}}]}],"functions":[{"id":"node","displayName":"node","parameters":[],"resultType":{"kind":"named","typeId":"Node"},"requiredEffects":[],"body":{"nodes":[{"id":"n","kind":"unitLiteral"}],"resultNodeId":"n"}},{"id":"option","displayName":"option","parameters":[],"resultType":{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"named","typeId":"Node"}]},"requiredEffects":[],"body":{"nodes":[{"id":"m","kind":"unitLiteral"}],"resultNodeId":"m"}}]}
    );
    const nested = struct {
        fn json(allocator: Allocator, prefix: []const u8, layers: usize, suffix: []const u8) ![]const u8 {
            const open = try allocator.alloc(u8, layers);
            const close = try allocator.alloc(u8, layers);
            @memset(open, '[');
            @memset(close, ']');
            return std.mem.concat(allocator, u8, &.{ prefix, open, close, suffix });
        }
    }.json;
    var interner: tm.Interner = .{ .gpa = a };
    defer interner.deinit();
    const generation = try tm.load(a, &interner, root, null);
    const declarations = model.typedRoot(a, &interner, &generation);
    var request: q.Request = .{ .a = a, .line = "", .root = .empty, .generation = &generation, .interner = &interner, .provenance = null };
    for ([_]usize{ 66, 127, 128 }) |count| {
        const option = count != 66;
        const fn_value = declarations.functions()[@intFromBool(option)];
        var input: Input = .{ .function_id = q.idOf(fn_value), .declarations = declarations, .fn_value = fn_value, .arguments = &.{}, .expected = null, .host_sources = &.{} };
        const text = try nested(a, "[\"value\",", 2 * count + @intFromBool(option), ",[]]");
        var output: Writer.Allocating = .init(a);
        if (count < 128) {
            try envelope(&request, &input, text, &output.writer);
            try std.testing.expect(output.written().len > 0);
        } else {
            try std.testing.expectError(error.Rejected, envelope(&request, &input, text, &output.writer));
            try std.testing.expectEqualStrings("RunInvalidResult", request.rejection.code);
            try std.testing.expectEqualStrings("The process did not return one JSON result.", request.rejection.message);
        }
    }
    // The limit includes call metadata and ignores bracket characters inside JSON strings.
    try std.testing.expect(envelopeWithinDepth(a, try nested(a, "[\"value\",\"[\\\"{\",[[0,[", 252, "]]]]")));
    try std.testing.expect(!envelopeWithinDepth(a, try nested(a, "[\"value\",\"unit\",[[0,[", 253, "]]]]")));
    try std.testing.expect(!envelopeWithinDepth(a, try nested(a, "[\"value\",", 256, ",[]]")));
}
