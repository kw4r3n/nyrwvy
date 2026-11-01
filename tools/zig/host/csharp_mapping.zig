//! Public C# mapping tables: display names are resolved once into the backend's stable-ID profile.
const std = @import("std");
const q = @import("query.zig");
const recipe = @import("recipe.zig");
const model = @import("model_reader");
const e = @import("csharp_emitter");
const host = @import("root");
const V = std.json.Value;
const A = std.mem.Allocator;
const D = recipe.Decoder;
const Entry = struct { value: V, duplicates: std.StringHashMapUnmanaged(void) };
const Cap = struct { handle: ?bool = null, operations: std.StringHashMapUnmanaged(Entry) = .empty };
const Table = struct { library: ?[]const u8 = null, capabilities: std.StringHashMapUnmanaged(Cap) = .empty };
fn f(a: A, comptime format: []const u8, args: anytype) ![]const u8 {
    return std.fmt.allocPrint(a, format, args);
}
fn obj(a: A, field_names: []const []const u8, values: []const V) !V {
    return e.object(a, field_names, values);
}
fn has(v: V, key: []const u8) bool {
    return v == .object and v.object.contains(key);
}
fn boolValue(d: *D, v: V, key: []const u8, fallback: bool) !bool {
    if (!has(v, key)) return fallback;
    try d.optionalBool(v, key);
    return e.get(v, key).bool;
}
fn integer(d: *D, v: V, key: []const u8) !i32 {
    const n = try d.property(v, key);
    if (n != .integer or n.integer < std.math.minInt(i32) or n.integer > std.math.maxInt(i32)) return d.r.fail("Property '{s}' must be a 32-bit integer.", .{key});
    return @intCast(n.integer);
}
fn int64(d: *D, context: []const u8, v: V, key: []const u8) !i64 {
    const n = try d.property(v, key);
    if (n != .integer and n != .float and n != .number_string) {
        const value_kind: []const u8 = switch (n) {
            .null => "Null",
            .bool => |b| if (b) "True" else "False",
            .string => "String",
            .object => "Object",
            .array => "Array",
            else => unreachable,
        };
        d.r.rejection = .{ .code = "SessionError", .message = try f(d.r.a, "The requested operation requires an element of type 'Number', but the target element has type '{s}'.", .{value_kind}) };
        return error.Rejected;
    }
    if (n != .integer) return d.r.fail("{s}: {s} must be an integer.", .{ context, key });
    return n.integer;
}
fn ensure(d: *D, v: V, path: []const u8, context: []const u8, allowed: []const []const u8, required: []const []const u8) !void {
    return d.ensureFields(path, v, context, allowed, required);
}
fn at(a: A, path: []const u8, key: []const u8) ![]const u8 {
    return f(a, "{s}/{s}", .{ path, key });
}
fn objectMap(d: *D, v: V, context: []const u8) !std.json.ObjectMap {
    if (v != .object) return d.r.fail("{s} must be an object.", .{context});
    return v.object;
}
fn keys(comptime T: type, a: A, map: std.StringHashMapUnmanaged(T)) ![]const []const u8 {
    var out: std.ArrayList([]const u8) = .empty;
    var it = map.keyIterator();
    while (it.next()) |key| try out.append(a, key.*);
    std.mem.sort([]const u8, out.items, {}, q.ordinal);
    return out.items;
}
fn read(d: *D, path: []const u8, value: V) !Table {
    const a = d.r.a;
    const context = try f(a, "mapping table {s}", .{path});
    try ensure(d, value, "", context, &.{ "format", "version", "library", "capabilities" }, &.{ "format", "version", "capabilities" });
    if (!e.eq(try d.requiredString(value, "format"), "nyrwvy.csharp-mapping")) return d.r.fail("mapping table {s}: format must be 'nyrwvy.csharp-mapping'.", .{path});
    if (try integer(d, value, "version") != 1) return d.r.fail("mapping table {s}: only version 1 is supported.", .{path});
    var t: Table = .{};
    if (has(value, "library")) t.library = try d.requiredString(value, "library");
    const caps = try objectMap(d, try d.property(value, "capabilities"), try f(a, "{s}: capabilities", .{path}));
    const names = try a.dupe([]const u8, caps.keys());
    std.mem.sort([]const u8, names, {}, q.ordinal);
    for (names) |name| {
        const item = caps.get(name).?;
        const p = try at(a, "/capabilities", name);
        try ensure(d, item, p, try f(a, "{s}: capability {s}", .{ path, name }), &.{ "handle", "operations" }, &.{});
        var cap: Cap = .{};
        if (has(item, "handle")) cap.handle = try boolValue(d, item, "handle", false);
        if (has(item, "operations")) {
            const ops = try objectMap(d, e.get(item, "operations"), try f(a, "{s}: {s}.operations", .{ path, name }));
            for (ops.keys(), ops.values()) |key, v| try cap.operations.put(a, key, .{ .value = v, .duplicates = d.duplicates });
        }
        try t.capabilities.put(a, name, cap);
    }
    return t;
}
fn merge(a: A, base: *Table, over: Table) !void {
    if (over.library) |library| base.library = library;
    for (try keys(Cap, a, over.capabilities)) |name| {
        const item = over.capabilities.get(name).?;
        if (base.capabilities.getPtr(name)) |cap| {
            if (item.handle) |flag| cap.handle = flag;
            var it = item.operations.iterator();
            while (it.next()) |pair| try cap.operations.put(a, pair.key_ptr.*, pair.value_ptr.*);
        } else try base.capabilities.put(a, name, item);
    }
}
fn nativeValue(d: *D, context: []const u8, c: []const u8, v: V, is_result: bool) !V {
    const a = d.r.a;
    const ints = .{ .{ "int8", 8, true }, .{ "int16", 16, true }, .{ "int32", 32, true }, .{ "int64", 64, true }, .{ "uint8", 8, false }, .{ "uint16", 16, false }, .{ "uint32", 32, false }, .{ "uint64", 64, false } };
    inline for (ints) |row| if (e.eq(c, row[0])) return obj(a, &.{ "kind", "bits", "signed" }, &.{ e.string("integer"), .{ .integer = row[1] }, .{ .bool = row[2] } });
    if (e.eq(c, "utf8")) {
        var free: V = .null;
        if (has(v, "free")) {
            free = e.string(try d.requiredString(v, "free"));
            if (!is_result) return d.r.fail("{s}: free applies to a result only.", .{context});
        }
        return obj(a, &.{ "kind", "free" }, &.{ e.string("utf8"), free });
    }
    if (e.eq(c, "handle")) {
        const release = try boolValue(d, v, "release", false);
        if (release and is_result) return d.r.fail("{s}: release applies to an argument only.", .{context});
        return obj(a, &.{ "kind", "release" }, &.{ e.string("handle"), .{ .bool = release } });
    }
    if (e.eq(c, "float32") or e.eq(c, "float64")) return obj(a, &.{ "kind", "bits" }, &.{ e.string("float"), .{ .integer = if (e.eq(c, "float32")) 32 else 64 } });
    inline for (.{ "bool", "intBool", "bytes", "void", "bits64", "pointer" }) |k| if (e.eq(c, k)) return obj(a, &.{"kind"}, &.{e.string(k)});
    return d.r.fail("{s}: unknown C type '{s}'.", .{ context, c });
}
fn native(d: *D, context: []const u8, v: V, is_result: bool) !V {
    return nativeValue(d, context, try d.requiredString(v, "c"), v, is_result);
}
fn byPointer(d: *D, context: []const u8, v: V) !bool {
    if (!has(v, "by")) return false;
    const by = try d.requiredString(v, "by");
    if (e.eq(by, "value")) return false;
    if (e.eq(by, "pointer")) return true;
    return d.r.fail("{s}: by must be 'value' or 'pointer', not '{s}'.", .{ context, by });
}
fn parameterId(d: *D, context: []const u8, op: model.Model, v: V) ![]const u8 {
    const name = try d.requiredString(v, "from");
    var found: ?[]const u8 = null;
    var count: usize = 0;
    for (op.parameters()) |p| if (e.eq(q.nameOf(p), name)) {
        count += 1;
        found = q.idOf(p);
    };
    if (count != 1) return d.r.fail("{s}: '{s}' names no single parameter of the operation.", .{ context, name });
    return found.?;
}
fn argument(d: *D, path: []const u8, context: []const u8, op: model.Model, is_field: bool, v: V) anyerror!V {
    const a = d.r.a;
    if (v != .object) return d.r.fail("{s} must be an object.", .{context});
    if (has(v, "self") and !is_field) {
        try ensure(d, v, path, context, &.{ "self", "release" }, &.{"self"});
        if (!try boolValue(d, v, "self", false)) return d.r.fail("{s}: self must be true.", .{context});
        return obj(a, &.{ "kind", "release" }, &.{ e.string("self"), .{ .bool = try boolValue(d, v, "release", false) } });
    }
    if (has(v, "out") and !is_field) {
        try ensure(d, v, path, context, &.{ "out", "capacity", "retry" }, &.{"out"});
        const c = try d.requiredString(v, "out");
        if (e.eq(c, "bytes")) {
            try ensure(d, v, path, context, &.{ "out", "capacity", "retry" }, &.{ "out", "capacity" });
            return obj(a, &.{ "kind", "capacity", "retry" }, &.{ e.string("outBytes"), .{ .integer = try integer(d, v, "capacity") }, if (has(v, "retry")) .{ .integer = try int64(d, context, v, "retry") } else .null });
        }
        try ensure(d, v, path, context, &.{"out"}, &.{"out"});
        return obj(a, &.{ "kind", "value" }, &.{ e.string("out"), try nativeValue(d, context, c, v, true) });
    }
    if (has(v, "struct") and !is_field) {
        try ensure(d, v, path, context, &.{ "struct", "size", "by" }, &.{"struct"});
        const fields = try d.property(v, "struct");
        if (fields != .array) return d.r.fail("{s}: struct must be an array of fields.", .{context});
        var items: std.ArrayList(V) = .empty;
        for (fields.array.items, 0..) |field, i| try items.append(a, try argument(d, try f(a, "{s}/struct/{d}", .{ path, i }), try f(a, "{s} field {d}", .{ context, i }), op, true, field));
        return obj(a, &.{ "kind", "fields", "size", "byPointer" }, &.{ e.string("struct"), try e.array(a, items.items), if (has(v, "size")) .{ .integer = try integer(d, v, "size") } else .null, .{ .bool = try byPointer(d, context, v) } });
    }
    if (has(v, "sizeof") and is_field) {
        try ensure(d, v, path, context, &.{ "sizeof", "c" }, &.{ "sizeof", "c" });
        return obj(a, &.{ "kind", "value" }, &.{ e.string("sizeOf"), try native(d, context, v, false) });
    }
    if (has(v, "value")) {
        try ensure(d, v, path, context, &.{ "value", "c", "name" }, &.{ "value", "c" });
        const n = try d.property(v, "value");
        if ((n == .null) != e.eq(try d.requiredString(v, "c"), "pointer")) return d.r.fail("{s}: null is the only pointer constant.", .{context});
        return obj(a, &.{ "kind", "constant", "value" }, &.{ e.string("constant"), .{ .integer = if (n == .null) 0 else try int64(d, context, v, "value") }, try native(d, context, v, false) });
    }
    try ensure(d, v, path, context, &.{ "from", "c", "release", "by", "retain" }, &.{ "from", "c" });
    if (is_field and (has(v, "release") or has(v, "by") or has(v, "retain"))) return d.r.fail("{s}: a struct field takes only from and c.", .{context});
    const retained = try boolValue(d, v, "retain", false);
    if (retained and (has(v, "by") or has(v, "release"))) return d.r.fail("{s}: a retained argument takes no by or release.", .{context});
    const k = if (retained) "retained" else if (try byPointer(d, context, v)) "reference" else "parameter";
    const id = try parameterId(d, context, op, v);
    return obj(a, &.{ "kind", "parameterId", "value" }, &.{ e.string(k), e.string(id), try native(d, context, v, false) });
}
fn entry(d: *D, path: []const u8, context: []const u8, library: ?[]const u8, op: model.Model, v: V) !V {
    const a = d.r.a;
    try ensure(d, v, path, context, &.{ "symbol", "arguments", "result", "failure", "unavailable", "library" }, &.{});
    if (try boolValue(d, v, "unavailable", false)) {
        if (has(v, "symbol")) return d.r.fail("{s}: an unavailable operation names no symbol.", .{context});
        return .null;
    }
    const lib = if (has(v, "library")) try d.requiredString(v, "library") else library orelse return d.r.fail("{s}: no library is named.", .{context});
    var args: std.ArrayList(V) = .empty;
    if (has(v, "arguments")) {
        const xs = try d.property(v, "arguments");
        if (xs != .array) return d.r.fail("{s}: arguments must be an array.", .{context});
        for (xs.array.items, 0..) |arg, i| try args.append(a, try argument(d, try f(a, "{s}/arguments/{d}", .{ path, i }), try f(a, "{s} argument {d}", .{ context, i }), op, false, arg));
    }
    if (has(v, "result")) try ensure(d, e.get(v, "result"), try at(a, path, "result"), try f(a, "{s} result", .{context}), &.{ "c", "free" }, &.{"c"});
    var failure: V = .null;
    if (has(v, "failure")) {
        const k = try d.requiredString(v, "failure");
        if (!e.eq(k, "null") and !e.eq(k, "negative") and !e.eq(k, "nonzero")) return d.r.fail("{s}: unknown failure '{s}' (null, negative or nonzero).", .{ context, k });
        failure = e.string(if (e.eq(k, "null")) "nullPointer" else k);
    }
    const symbol = try d.requiredString(v, "symbol");
    const result = if (has(v, "result")) try native(d, try f(a, "{s} result", .{context}), e.get(v, "result"), true) else try obj(a, &.{"kind"}, &.{e.string("void")});
    return obj(a, &.{ "library", "symbol", "arguments", "returns", "failure" }, &.{ e.string(lib), e.string(symbol), try e.array(a, args.items), result, failure });
}
pub fn overridePath(a: A, path: []const u8) ![]const u8 {
    return f(a, "{s}.override.json", .{if (std.mem.endsWith(u8, path, ".json")) path[0 .. path.len - 5] else path});
}
pub fn bindings(r: *q.Request, paths: []const []const u8) ![]const V {
    const a = r.a;
    var out: std.ArrayList(V) = .empty;
    const caps = try q.typedVisible(r, "capability");
    for (paths) |path| {
        var decoder: D = .{ .r = r, .duplicates = .empty };
        const loaded = try @import("recipe_file.zig").load(&decoder, path, "tables", "");
        var table = try read(&decoder, path, loaded.value);
        const override = try overridePath(a, path);
        const full = try std.fs.path.resolveAlloc(a, &.{ std.fs.path.dirname(r.snapshot_path) orelse "/", override });
        const info = std.Io.Dir.cwd().statFile(host.host_io, full, .{}) catch null;
        if (info != null and info.?.kind == .file) {
            var over_decoder: D = .{ .r = r, .duplicates = .empty };
            const over = try @import("recipe_file.zig").load(&over_decoder, override, "tables", "");
            try merge(a, &table, try read(&over_decoder, override, over.value));
        }
        for (try keys(Cap, a, table.capabilities)) |name| {
            const table_cap = table.capabilities.get(name).?;
            var matches: usize = 0;
            var cap: model.Model = undefined;
            for (caps) |candidate| if (e.eq(q.nameOf(candidate), name)) {
                matches += 1;
                cap = candidate;
            };
            if (matches == 0) return r.fail("mapping table {s}: no capability is named '{s}'.", .{ path, name });
            if (matches != 1) return r.fail("mapping table {s}: several capabilities are named '{s}'.", .{ path, name });
            const ops = cap.operations();
            for (try keys(Entry, a, table_cap.operations)) |key| {
                for (ops) |op| {
                    if (e.eq(q.nameOf(op), key)) break;
                } else return r.fail("mapping table {s}: capability {s} has no operation '{s}'.", .{ path, name, key });
            }
            for (ops) |op| {
                const context = try f(a, "mapping table {s}: {s}.{s}", .{ path, name, q.nameOf(op) });
                const item = table_cap.operations.get(q.nameOf(op)) orelse return r.fail("{s} is neither bound nor marked unavailable.", .{context});
                const p = try f(a, "/capabilities/{s}/operations/{s}", .{ name, q.nameOf(op) });
                var entry_decoder: D = .{ .r = r, .duplicates = item.duplicates };
                try out.append(a, try obj(a, &.{ "operationId", "kind", "native" }, &.{ e.string(q.idOf(op)), e.string("native"), try obj(a, &.{ "handle", "call" }, &.{ .{ .bool = table_cap.handle orelse false }, try entry(&entry_decoder, p, context, table.library, op, item.value) }) }));
            }
        }
    }
    return out.items;
}

test "mapping override names preserve extension policy" {
    const a = std.testing.allocator;
    const p = try overridePath(a, "x.csharp.json");
    defer a.free(p);
    try std.testing.expectEqualStrings("x.csharp.override.json", p);
}

test "mapping rejects pointer constants and preserves failure diagnostic precedence" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    var r: q.Request = .{ .a = a, .line = "", .root = .empty, .provenance = null };
    var d: D = .{ .r = &r, .duplicates = .empty };
    const pointer = try std.json.parseFromSliceLeaky(V, a, "{\"value\":1,\"c\":\"pointer\"}", .{});
    try std.testing.expectError(error.Rejected, argument(&d, "", "test", undefined, false, pointer));
    try std.testing.expectEqualStrings("test: null is the only pointer constant.", r.rejection.message);
    const invalid = try std.json.parseFromSliceLeaky(V, a, "{\"symbol\":\"x\",\"result\":{\"c\":\"bad\"},\"failure\":\"wrong\"}", .{});
    try std.testing.expectError(error.Rejected, entry(&d, "", "test", "lib", undefined, invalid));
    try std.testing.expectEqualStrings("test: unknown failure 'wrong' (null, negative or nonzero).", r.rejection.message);
}
