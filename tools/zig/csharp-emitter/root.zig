//! C# backend over the same Execution IR consumed by the Zig backend.
const std = @import("std");
pub const Value = std.json.Value;
pub const Allocator = std.mem.Allocator;
pub const worker_source = @embedFile("runtime/Worker.cs");
pub const generated_namespace = "Nyrwvy.Generated";
pub const function_container_name = "NyFunctions";
pub const Failure = struct { message: []const u8, details: Value = .null, code: []const u8 = "BackendFailure" };
pub const Artifact = struct { source: []const u8, symbols: Value, generated_namespace: []const u8 = generated_namespace, function_container_name: []const u8 = function_container_name };
pub const Result = union(enum) { artifact: Artifact, failure: Failure };
pub const aggressive_inlining = "[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]";

pub fn get(v: Value, key: []const u8) Value {
    return if (v == .object) v.object.get(key) orelse .null else .null;
}
pub fn str(v: Value, key: []const u8) []const u8 {
    const x = get(v, key);
    return if (x == .string) x.string else "";
}
pub fn arr(v: Value, key: []const u8) []const Value {
    const x = get(v, key);
    return if (x == .array) x.array.items else &.{};
}
pub fn eq(x: []const u8, y: []const u8) bool {
    return std.mem.eql(u8, x, y);
}
pub fn kind(v: Value) []const u8 {
    return str(v, "kind");
}
pub fn cat(a: Allocator, parts: []const []const u8) ![]const u8 {
    return std.mem.concat(a, u8, parts);
}
pub fn fmt(a: Allocator, comptime f: []const u8, args: anytype) ![]const u8 {
    return std.fmt.allocPrint(a, f, args);
}
pub fn name(a: Allocator, prefix: []const u8, id: []const u8) ![]const u8 {
    const out = try a.alloc(u8, prefix.len + id.len * 2);
    @memcpy(out[0..prefix.len], prefix);
    for (id, 0..) |b, i| {
        out[prefix.len + i * 2] = "0123456789ABCDEF"[b >> 4];
        out[prefix.len + i * 2 + 1] = "0123456789ABCDEF"[b & 15];
    }
    return out;
}
pub fn valueName(a: Allocator, id: []const u8) ![]const u8 {
    return name(a, "v_", id);
}
pub fn object(a: Allocator, keys: []const []const u8, values: []const Value) !Value {
    var o: std.json.ObjectMap = .empty;
    for (keys, values) |k, v| try o.put(a, k, v);
    return .{ .object = o };
}
pub fn string(s: []const u8) Value {
    return .{ .string = s };
}
pub fn array(a: Allocator, items: []const Value) !Value {
    var list: std.array_list.Managed(Value) = .init(a);
    try list.appendSlice(items);
    return .{ .array = list };
}
// F# StableId ordering compares UTF-16 code units, including supplementary characters.
pub fn ordinalLess(_: void, x: []const u8, y: []const u8) bool {
    var xi: usize = 0;
    var yi: usize = 0;
    var xp: ?u16 = null;
    var yp: ?u16 = null;
    while (xi < x.len or xp != null) {
        if (yi >= y.len and yp == null) return false;
        const u = nextUnit(x, &xi, &xp);
        const v = nextUnit(y, &yi, &yp);
        if (u != v) return u < v;
    }
    return yi < y.len or yp != null;
}
fn nextUnit(s: []const u8, i: *usize, p: *?u16) u16 {
    if (p.*) |u| {
        p.* = null;
        return u;
    }
    const n = std.unicode.utf8ByteSequenceLength(s[i.*]) catch 1;
    const c = std.unicode.utf8Decode(s[i.*..][0..n]) catch 0xfffd;
    i.* += n;
    if (c < 0x10000) return @intCast(c);
    const k = c - 0x10000;
    p.* = @intCast(0xdc00 + (k & 1023));
    return @intCast(0xd800 + (k >> 10));
}
pub fn sorted(a: Allocator, items: []const Value, key: []const u8) ![]const Value {
    const out = try a.dupe(Value, items);
    std.mem.sort(Value, out, key, struct {
        fn less(k: []const u8, x: Value, y: Value) bool {
            return ordinalLess({}, str(x, k), str(y, k));
        }
    }.less);
    return out;
}
pub fn find(items: []const Value, key: []const u8, id: []const u8) ?Value {
    for (items) |x| if (eq(str(x, key), id)) return x;
    return null;
}
pub fn escaped(a: Allocator, s: []const u8) ![]const u8 {
    var out: std.ArrayList(u8) = .empty;
    try out.append(a, '"');
    var i: usize = 0;
    var pending: ?u16 = null;
    while (i < s.len or pending != null) {
        const u = nextUnit(s, &i, &pending);
        switch (u) {
            '"' => try out.appendSlice(a, "\\\""),
            '\\' => try out.appendSlice(a, "\\\\"),
            '\r' => try out.appendSlice(a, "\\r"),
            '\n' => try out.appendSlice(a, "\\n"),
            '\t' => try out.appendSlice(a, "\\t"),
            0x20...0x21, 0x23...0x5b, 0x5d...0x7e => try out.append(a, @intCast(u)),
            else => {
                var buf: [6]u8 = undefined;
                try out.appendSlice(a, try std.fmt.bufPrint(&buf, "\\u{X:0>4}", .{u}));
            },
        }
    }
    try out.append(a, '"');
    return out.toOwnedSlice(a);
}
pub fn typeArgs(a: Allocator, args: []const Value) anyerror![]const u8 {
    if (args.len == 0) return "";
    const items = try a.alloc([]const u8, args.len);
    for (args, items) |t, *dest| dest.* = try formatType(a, t);
    return cat(a, &.{ "<", try std.mem.join(a, ", ", items), ">" });
}
pub fn formatType(a: Allocator, t: Value) anyerror![]const u8 {
    const k = kind(t);
    const table = .{ .{ "primitiveString", "NyStr" }, .{ "primitiveBool", "bool" }, .{ "primitiveUnit", "NyUnit" }, .{ "primitiveInt64", "long" }, .{ "primitiveFloat64", "double" }, .{ "primitiveBits32", "uint" }, .{ "primitiveBytes", "NyBytes" }, .{ "exception", "System.Runtime.ExceptionServices.ExceptionDispatchInfo" }, .{ "taskScope", "NyTaskScope" } };
    inline for (table) |row| if (eq(k, row[0])) return row[1];
    if (eq(k, "named")) return cat(a, &.{ try name(a, "NyT_", str(t, "typeId")), try typeArgs(a, arr(t, "arguments")) });
    if (eq(k, "capability")) return name(a, "NyC_", str(t, "capabilityId"));
    if (eq(k, "typeParameter")) return name(a, "T_", str(t, "typeParameterId"));
    if (eq(k, "function")) {
        var args: std.ArrayList(Value) = .empty;
        try args.appendSlice(a, arr(t, "parameters"));
        try args.append(a, get(t, "resultType"));
        return fmt(a, "NyFunction{d}{s}", .{ args.items.len - 1, try typeArgs(a, args.items) });
    }
    const builtin = .{ .{ "option", "NyOption", &[_][]const u8{"itemType"} }, .{ "result", "NyResult", &[_][]const u8{ "okType", "errorType" } }, .{ "map", "NyMap", &[_][]const u8{ "keyType", "valueType" } }, .{ "set", "NySet", &[_][]const u8{"itemType"} }, .{ "list", "NyList", &[_][]const u8{"itemType"} }, .{ "oneShot", "NyOneShot", &[_][]const u8{"valueType"} }, .{ "task", "NyTask", &[_][]const u8{"resultType"} } };
    inline for (builtin) |row| if (eq(k, row[0])) {
        var args: std.ArrayList(Value) = .empty;
        for (row[2]) |key| try args.append(a, get(t, key));
        return cat(a, &.{ row[1], try typeArgs(a, args.items) });
    };
    return error.InvalidExecutionType;
}
pub const Writer = struct {
    a: Allocator,
    text: std.ArrayList(u8) = .empty,
    indent: usize = 0,
    pub fn line(w: *Writer, s: []const u8) !void {
        if (s.len > 0) try w.text.appendNTimes(w.a, ' ', w.indent * 4);
        try w.text.appendSlice(w.a, s);
        try w.text.append(w.a, '\n');
    }
    pub fn push(w: *Writer) void {
        w.indent += 1;
    }
    pub fn pop(w: *Writer) void {
        w.indent -= 1;
    }
    pub fn lines(w: *Writer, s: []const u8) !void {
        var it = std.mem.splitScalar(u8, s, '\n');
        while (it.next()) |text_line| try w.line(text_line);
    }
};
pub const Context = struct {
    a: Allocator,
    program: Value,
    profile: Value,
    pub fn functions(c: Context) []const Value {
        return arr(c.program, "functions");
    }
    pub fn capabilities(c: Context) []const Value {
        return arr(c.program, "capabilities");
    }
    pub fn function(c: Context, id: []const u8) Value {
        return find(c.functions(), "semanticFunctionId", id).?;
    }
    pub fn capability(c: Context, id: []const u8) Value {
        return find(c.capabilities(), "id", id).?;
    }
    pub fn operation(c: Context, id: []const u8) ?Value {
        for (c.capabilities()) |cap| if (find(arr(cap, "operations"), "id", id)) |op| return op;
        return null;
    }
    pub fn capabilityOf(c: Context, id: []const u8) ?Value {
        for (c.capabilities()) |cap| if (find(arr(cap, "operations"), "id", id) != null) return cap;
        return null;
    }
    pub fn binding(c: Context, id: []const u8) ?Value {
        return find(arr(c.profile, "capabilityBindings"), "operationId", id);
    }
    pub fn hasAdapter(c: Context, cap: Value) bool {
        const ops = arr(cap, "operations");
        if (ops.len == 0) return false;
        for (ops) |op| {
            const b = c.binding(str(op, "id")) orelse return false;
            if (!eq(kind(b), "native") and !eq(kind(b), "systemConsoleWriteLine")) return false;
        }
        return true;
    }
};

pub fn emit(a: Allocator, ir_json: []const u8, profile: Value) anyerror!Result {
    const parsed = try std.json.parseFromSlice(Value, a, ir_json, .{ .allocate = .alloc_always });
    const c: Context = .{ .a = a, .program = parsed.value, .profile = profile };
    if (try @import("validation.zig").prepare(c)) |failure| return .{ .failure = failure };
    var w: Writer = .{ .a = a };
    try w.line("// Generated by Nyrwvy. Do not edit as authoritative source.");
    try w.line("#nullable enable");
    try w.line("#pragma warning disable CS0219 // Preserve Execution IR assignments even when their values are unused.");
    try w.line("using System;");
    try w.line("");
    try w.line("namespace Nyrwvy.Generated;");
    try w.line("");
    try w.text.appendSlice(a, @embedFile("runtime/Core.cs"));
    try w.lines(@embedFile("runtime/List.cs"));
    try w.line("");
    try w.lines(@embedFile("runtime/Bytes.cs"));
    try w.line("");
    body(&w, c) catch |e| switch (e) {
        error.MalformedExecutionIr, error.InvalidCallbackArity, error.InvalidTerminator => return .{ .failure = .{ .message = @errorName(e) } },
        else => return e,
    };
    try w.line("#pragma warning restore CS0219");
    return .{ .artifact = .{ .source = w.text.items, .symbols = try @import("declarations.zig").manifest(c) } };
}

fn body(w: *Writer, c: Context) anyerror!void {
    try @import("declarations.zig").emit(w, c);
    try @import("functions.zig").emit(w, c);
}

test "malformed Execution IR references are a backend failure" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const cases = [_][2][]const u8{
        .{ "{\"functions\":[{\"blocks\":[{\"instructions\":[{\"kind\":\"capabilityConstruct\",\"capabilityId\":\"missing\",\"originNodeId\":\"n\"}]}]}]}", "MalformedExecutionIr" },
        .{ "{\"functions\":[{\"semanticFunctionId\":\"f\",\"parameters\":[],\"resultType\":{\"kind\":\"primitiveUnit\"},\"blocks\":[{\"id\":\"b\",\"instructions\":[],\"terminator\":{\"kind\":\"tailCall\",\"arguments\":[{\"targetParameterId\":\"missing\",\"valueId\":\"v\"}]}}]}]}", "MalformedExecutionIr" },
    };
    for (cases) |case| {
        const result = try emit(arena.allocator(), case[0], .null);
        try std.testing.expectEqualStrings(case[1], result.failure.message);
    }
}

test "malformed literal values are a backend failure" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    for ([_][]const u8{ "{\"kind\":\"int64Literal\",\"value\":1.0}", "{\"kind\":\"int64Literal\",\"value\":99999999999999999999}", "{\"kind\":\"boolLiteral\",\"value\":1}", "{\"kind\":\"float64Literal\"}", "{\"kind\":\"float64Literal\",\"bits\":\"3ff0000000000000);x\"}" }) |instruction| {
        const ir = try std.mem.concat(arena.allocator(), u8, &.{ "{\"functions\":[{\"blocks\":[{\"instructions\":[", instruction, "]}]}]}" });
        const result = try emit(arena.allocator(), ir, .null);
        try std.testing.expectEqualStrings("MalformedLiteral", result.failure.message);
    }
}

test "stable names and UTF16 ordinal sorting" {
    const a = std.testing.allocator;
    const n = try name(a, "NyF_", "é");
    defer a.free(n);
    try std.testing.expectEqualStrings("NyF_C3A9", n);
    try std.testing.expect(ordinalLess({}, "\u{10000}", "\u{e000}"));
    const s = try escaped(a, "a\n\u{10000}");
    defer a.free(s);
    try std.testing.expectEqualStrings("\"a\\n\\uD800\\uDC00\"", s);
}
