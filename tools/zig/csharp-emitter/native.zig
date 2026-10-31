//! Native mapping-table validation and ABI adapters, matching the F# backend oracle.
const std = @import("std");
const root = @import("root.zig");
const Value = root.Value;
const Context = root.Context;
const Writer = root.Writer;
const Allocator = root.Allocator;
const get = root.get;
const str = root.str;
const arr = root.arr;
const kind = root.kind;
const eq = root.eq;
const fmt = root.fmt;
const cat = root.cat;
const interop = "System.Runtime.InteropServices.";
fn boolean(v: Value, key: []const u8) bool {
    const x = get(v, key);
    return x == .bool and x.bool;
}
fn number(v: Value, key: []const u8) i64 {
    const x = get(v, key);
    return if (x == .integer) x.integer else 0;
}
fn nullValue(v: Value, key: []const u8) bool {
    return get(v, key) == .null;
}
fn nk(v: Value, k: []const u8) bool {
    return eq(kind(v), k);
}
fn scalar(v: Value) bool {
    return nk(v, "integer") or nk(v, "bits64") or nk(v, "bool") or nk(v, "intBool") or nk(v, "float");
}
fn validBits(v: Value) bool {
    const n = number(v, "bits");
    return if (nk(v, "integer")) n == 8 or n == 16 or n == 32 or n == 64 else if (nk(v, "float")) n == 32 or n == 64 else true;
}
fn borrowedHandle(v: Value) bool {
    return nk(v, "handle") and !boolean(v, "release");
}
fn nativeHandle(c: Context, id: []const u8) bool {
    const cap = root.find(c.capabilities(), "id", id) orelse return false;
    const ops = arr(cap, "operations");
    if (ops.len == 0) return false;
    for (ops) |op| {
        const b = c.binding(str(op, "id")) orelse return false;
        if (!nk(b, "native") or !boolean(get(b, "native"), "handle")) return false;
    }
    return true;
}
fn handleType(c: Context, t: Value) bool {
    return nk(t, "capability") and nativeHandle(c, str(t, "capabilityId"));
}
fn valueText(a: Allocator, v: Value) ![]const u8 {
    const k = kind(v);
    if (eq(k, "integer")) return fmt(a, "Integer ({d}, {s})", .{ number(v, "bits"), if (boolean(v, "signed")) "true" else "false" });
    if (eq(k, "float")) return fmt(a, "Float {d}", .{number(v, "bits")});
    if (eq(k, "handle")) return fmt(a, "Handle {s}", .{if (boolean(v, "release")) "true" else "false"});
    if (eq(k, "utf8")) {
        if (get(v, "free") == .string) return fmt(a, "Utf8 (Some \"{s}\")", .{str(v, "free")});
        return "Utf8 None";
    }
    const names = .{ .{ "bool", "Bool" }, .{ "intBool", "IntBool" }, .{ "void", "Void" }, .{ "bytes", "Bytes" }, .{ "bits64", "Bits64" }, .{ "pointer", "Pointer" } };
    inline for (names) |row| if (eq(k, row[0])) return row[1];
    return error.InvalidNativeValue;
}
fn parameterFits(c: Context, op: Value, id: []const u8, v: Value) !?[]const u8 {
    const p = root.find(arr(op, "parameters"), "id", id) orelse return "an argument names no parameter of the operation";
    const t = get(p, "type");
    const fits = if (nk(v, "integer") or nk(v, "bits64")) nk(t, "primitiveInt64") and validBits(v) else if (nk(v, "float")) nk(t, "primitiveFloat64") and validBits(v) else if (nk(v, "bool") or nk(v, "intBool")) nk(t, "primitiveBool") else if (nk(v, "utf8") and nullValue(v, "free")) nk(t, "primitiveString") else if (nk(v, "bytes")) nk(t, "primitiveBytes") else if (nk(v, "handle")) handleType(c, t) else false;
    return if (fits) null else try fmt(c.a, "argument {s}: {s} does not convert its parameter type", .{ id, try valueText(c.a, v) });
}
fn constantFits(n: i64, v: Value) bool {
    if (nk(v, "pointer")) return n == 0;
    if (nk(v, "integer") and validBits(v)) {
        const bits = number(v, "bits");
        if (bits == 64) return boolean(v, "signed") or n >= 0;
        const shift: u6 = @intCast(if (boolean(v, "signed")) bits - 1 else bits);
        const max = @as(i64, 1) << shift;
        return if (boolean(v, "signed")) n >= -max and n < max else n >= 0 and n < max;
    }
    return (nk(v, "bool") or nk(v, "intBool")) and (n == 0 or n == 1);
}
fn argumentProblem(c: Context, op: Value, handle: bool, in_struct: bool, arg: Value) anyerror!?[]const u8 {
    const v = get(arg, "value");
    const k = kind(arg);
    if (eq(k, "self")) return if (in_struct) "self cannot be a struct field" else if (handle) null else "self is only an argument of a handle capability's operations";
    if (eq(k, "parameter")) {
        if (in_struct and !scalar(v)) return try fmt(c.a, "a struct field cannot hold {s}", .{try valueText(c.a, v)});
        return parameterFits(c, op, str(arg, "parameterId"), v);
    }
    if (eq(k, "reference")) {
        if (in_struct) return "a struct field cannot be a reference";
        if (scalar(v) or borrowedHandle(v)) return parameterFits(c, op, str(arg, "parameterId"), v);
        return try fmt(c.a, "{s} cannot be passed by address", .{try valueText(c.a, v)});
    }
    if (eq(k, "retained")) {
        if (in_struct) return "a struct field cannot be retained";
        if (!handle) return "only a handle capability's operation retains an argument (the handle owns the copy)";
        if (nk(v, "bytes") or (nk(v, "utf8") and nullValue(v, "free"))) return parameterFits(c, op, str(arg, "parameterId"), v);
        return try fmt(c.a, "{s} cannot be retained (Bytes or String only)", .{try valueText(c.a, v)});
    }
    if (eq(k, "constant")) return if (constantFits(number(arg, "constant"), v)) null else try fmt(c.a, "constant {d} does not fit {s}", .{ number(arg, "constant"), try valueText(c.a, v) });
    if (eq(k, "out") or eq(k, "outBytes")) {
        if (in_struct) return "a struct field cannot be an out-parameter";
        if (eq(k, "outBytes")) return if (number(arg, "capacity") >= 1) null else "an out buffer needs a capacity of at least 1";
        return if ((scalar(v) and validBits(v)) or borrowedHandle(v)) null else try fmt(c.a, "{s} cannot be an out-parameter", .{try valueText(c.a, v)});
    }
    if (eq(k, "struct")) {
        if (in_struct) return "structs do not nest";
        const fields = arr(arg, "fields");
        if (fields.len == 0) return "a struct has at least one field";
        var offset: i64 = 0;
        var largest: i64 = 1;
        for (fields) |field| {
            if (try argumentProblem(c, op, handle, true, field)) |p| return p;
            const fv = get(field, "value");
            const width = if (nk(fv, "integer") or nk(fv, "float")) @divTrunc(number(fv, "bits"), 8) else if (nk(fv, "bool")) @as(i64, 1) else if (nk(fv, "intBool")) @as(i64, 4) else @as(i64, 8);
            offset = @divTrunc(offset + width - 1, width) * width + width;
            largest = @max(largest, width);
        }
        const natural = @divTrunc(offset + largest - 1, largest) * largest;
        if (!nullValue(arg, "size") and number(arg, "size") < natural) return try fmt(c.a, "a struct size is at least its natural size {d}", .{natural});
        return null;
    }
    if (eq(k, "sizeOf")) {
        if (!in_struct) return "sizeof is only a struct field";
        return if (nk(v, "integer") and validBits(v)) null else "sizeof is an integer field";
    }
    return error.InvalidNativeArgument;
}
fn valueFits(c: Context, v: Value, t: Value) bool {
    if (nk(v, "void")) return nk(t, "primitiveUnit");
    if (nk(v, "integer") or nk(v, "bits64")) return nk(t, "primitiveInt64") and validBits(v);
    if (nk(v, "float")) return nk(t, "primitiveFloat64") and validBits(v);
    if (nk(v, "bool") or nk(v, "intBool")) return nk(t, "primitiveBool");
    if (nk(v, "utf8")) return nk(t, "primitiveString");
    if (nk(v, "bytes")) return nk(t, "primitiveBytes");
    return borrowedHandle(v) and handleType(c, t);
}
fn failureKind(call: Value) []const u8 {
    const f = get(call, "failure");
    return if (f == .string) f.string else kind(f);
}
pub fn problem(c: Context, capability_id: []const u8, op: Value, binding: Value) !?[]const u8 {
    const handle = boolean(binding, "handle");
    for (arr(c.capability(capability_id), "operations")) |other| {
        if (c.binding(str(other, "id"))) |b| {
            if (nk(b, "native") and boolean(get(b, "native"), "handle") != handle) return "every operation of a handle capability must be a handle operation";
        }
    }
    const call = get(binding, "call");
    if (call == .null) return null;
    const args = arr(call, "arguments");
    const ret = get(call, "returns");
    const failure = failureKind(call);
    const has_failure = failure.len > 0;
    var parameter_ids: std.ArrayList([]const u8) = .empty;
    var outs: usize = 0;
    var out: Value = .null;
    var selfs: usize = 0;
    var releases: usize = 0;
    var retries = false;
    var retains = false;
    for (args) |arg| {
        const fields = if (nk(arg, "struct")) arr(arg, "fields") else &[_]Value{arg};
        for (fields) |f| {
            if (nk(f, "parameter") or nk(f, "reference") or nk(f, "retained")) try parameter_ids.append(c.a, str(f, "parameterId"));
        }
        if (nk(arg, "out")) {
            outs += 1;
            out = get(arg, "value");
        } else if (nk(arg, "outBytes")) {
            outs += 1;
            out = try root.object(c.a, &.{"kind"}, &.{root.string("bytes")});
            if (!nullValue(arg, "retry")) retries = true;
        }
        if (nk(arg, "self")) selfs += 1;
        if ((nk(arg, "self") and boolean(arg, "release")) or (nk(arg, "parameter") and nk(get(arg, "value"), "handle") and boolean(get(arg, "value"), "release"))) releases += 1;
        if (nk(arg, "retained")) retains = true;
    }
    const result = get(op, "resultType");
    const is_result = nk(result, "result") and nk(get(result, "errorType"), "primitiveString");
    const ok = if (is_result) get(result, "okType") else result;
    const result_fits = if (outs == 1) ((nk(ret, "void") or (nk(ret, "integer") and has_failure)) and valueFits(c, out, ok)) else if (nk(ret, "integer") and nk(ok, "primitiveUnit")) has_failure else if (nk(ret, "bytes") or nk(ret, "pointer")) false else valueFits(c, ret, ok);
    const failure_fits = if (!has_failure) true else if (eq(failure, "nullPointer")) nk(ret, "utf8") or nk(ret, "handle") else if (eq(failure, "negative")) nk(ret, "integer") and boolean(ret, "signed") else if (eq(failure, "nonzero")) nk(ret, "integer") and (nk(ok, "primitiveUnit") or outs > 0) else false;
    for (args) |arg| if (try argumentProblem(c, op, handle, false, arg)) |p| return p;
    if (outs > 1) return "an operation has at most one out-parameter";
    if (retries and (!(nk(ret, "integer") and boolean(ret, "signed")) or !has_failure)) return "retry compares a signed integer result that failure also checks";
    if (retries and releases > 0) return "an operation that retries cannot release a handle (the retry would pass it again)";
    if (releases > 0 and retains) return "an operation that releases a handle cannot retain an argument";
    const parameters = arr(op, "parameters");
    if (parameter_ids.items.len != parameters.len) return "every parameter must be passed exactly once";
    for (parameters) |p| {
        var count: usize = 0;
        for (parameter_ids.items) |id| {
            if (eq(id, str(p, "id"))) count += 1;
        }
        if (count != 1) return "every parameter must be passed exactly once";
    }
    if (handle and selfs != 1) return "a handle operation passes self exactly once";
    if (releases > 1) return "an operation releases at most one handle";
    if (nk(result, "result") and !nk(get(result, "errorType"), "primitiveString")) return "a Result's error type must be String";
    if (is_result and !has_failure) return "a Result<_, String> operation needs failure detection";
    if (outs > 0 and !nk(ret, "void") and !has_failure) return "beside an out-parameter the C result is a status: name its failure detection (a bool status is not supported)";
    if (!result_fits) return try fmt(c.a, "result {s} does not convert to the operation's result type", .{try valueText(c.a, ret)});
    if (!failure_fits) {
        const f = if (eq(failure, "nullPointer")) "NullPointer" else if (eq(failure, "negative")) "Negative" else "Nonzero";
        return try fmt(c.a, "failure Some({s}) does not apply to result {s}", .{ f, try valueText(c.a, ret) });
    }
    return null;
}
fn nativeType(v: Value) []const u8 {
    const k = kind(v);
    if (eq(k, "integer")) {
        const signed = boolean(v, "signed");
        return switch (number(v, "bits")) {
            8 => if (signed) "sbyte" else "byte",
            16 => if (signed) "short" else "ushort",
            32 => if (signed) "int" else "uint",
            else => if (signed) "long" else "ulong",
        };
    }
    if (eq(k, "bool")) return "byte";
    if (eq(k, "intBool")) return "int";
    if (eq(k, "bytes")) return "System.IntPtr pointer, nuint length";
    if (eq(k, "void")) return "void";
    if (eq(k, "bits64")) return "ulong";
    if (eq(k, "float")) return if (number(v, "bits") == 32) "float" else "double";
    return "System.IntPtr";
}
fn parameterName(a: Allocator, p: Value) ![]const u8 {
    return root.name(a, "cp_", str(p, "id"));
}
fn operationParameters(c: Context, op: Value) ![]const u8 {
    var out: std.ArrayList([]const u8) = .empty;
    for (arr(op, "parameters")) |p| try out.append(c.a, try fmt(c.a, "{s} {s}", .{ try root.formatType(c.a, get(p, "type")), try parameterName(c.a, p) }));
    return std.mem.join(c.a, ", ", out.items);
}
fn jsonString(a: Allocator, s: []const u8) ![]const u8 {
    const escaped = try root.escaped(a, s);
    var out: std.ArrayList(u8) = .empty;
    var i: usize = 0;
    while (i < escaped.len) : (i += 1) {
        const b = escaped[i];
        if (b == '\\' and i + 1 < escaped.len and escaped[i + 1] == '\\') {
            try out.appendSlice(a, "\\\\");
            i += 1;
        } else if (i + 6 <= escaped.len and (std.mem.eql(u8, escaped[i..][0..6], "\\u0008") or std.mem.eql(u8, escaped[i..][0..6], "\\u000C"))) {
            try out.appendSlice(a, if (escaped[i + 5] == '8') "\\b" else "\\f");
            i += 5;
        } else if (i > 0 and i + 1 < escaped.len and b == '\\' and escaped[i + 1] == '"') {
            try out.appendSlice(a, "\\u0022");
            i += 1;
        } else if (b == '\'' or b == '&' or b == '+' or b == '<' or b == '>' or b == '`') {
            try out.appendSlice(a, try fmt(a, "\\u{X:0>4}", .{@as(u16, b)}));
        } else try out.append(a, b);
    }
    return out.items;
}
fn dllImport(a: Allocator, library: []const u8, symbol: []const u8) ![]const u8 {
    return fmt(a, "[System.Runtime.InteropServices.DllImport({s}, EntryPoint = {s}, ExactSpelling = true, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]", .{ try jsonString(a, library), try jsonString(a, symbol) });
}
fn emitSupport(w: *Writer) !void {
    try w.line("internal static class NyNative");
    try w.line("{");
    w.push();
    try w.line("public static System.IntPtr Utf8(string text) => text.Contains('\\0') ? throw new System.ArgumentException(\"A String passed to native code contains U+0000.\") : System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(text);");
    try w.line("public static void Free(System.IntPtr pointer) => System.Runtime.InteropServices.Marshal.FreeCoTaskMem(pointer);");
    try w.line("public static System.IntPtr Copy(byte[] bytes) { var pointer = System.Runtime.InteropServices.Marshal.AllocCoTaskMem(System.Math.Max(1, bytes.Length)); System.Runtime.InteropServices.Marshal.Copy(bytes, 0, pointer, bytes.Length); return pointer; }");
    try w.line("public static float Float32(double value) { var single = (float)value; return float.IsInfinity(single) && !double.IsInfinity(value) ? throw new System.OverflowException(\"A Float64 passed to native code is outside float's range.\") : single; }");
    try w.line("public static System.IntPtr NotNull(System.IntPtr pointer, string symbol) => pointer != System.IntPtr.Zero ? pointer : throw new System.InvalidOperationException(symbol + \" returned null\");");
    try w.line("public static NyStr ReadUtf8(System.IntPtr pointer, string symbol) => System.Runtime.InteropServices.Marshal.PtrToStringUTF8(NotNull(pointer, symbol))!;");
    try w.line("public static System.IntPtr Address<T>(ref T local) where T : unmanaged => System.Runtime.CompilerServices.Unsafe.ByteOffset(ref System.Runtime.CompilerServices.Unsafe.NullRef<T>(), ref local);");
    try w.line("[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] public static string Failed<T>(string symbol, T code) => symbol + \" failed with \" + code;");
    w.pop();
    try w.line("}");
    try w.line("");
}
fn convert(a: Allocator, v: Value, n: []const u8) ![]const u8 {
    if (nk(v, "integer")) return fmt(a, "checked(({s}){s})", .{ nativeType(v), n });
    if (nk(v, "bits64")) return fmt(a, "unchecked((ulong){s})", .{n});
    if (nk(v, "float")) return if (number(v, "bits") == 32) try fmt(a, "NyNative.Float32({s})", .{n}) else n;
    if (nk(v, "bool")) return fmt(a, "(byte)({s} ? 1 : 0)", .{n});
    if (nk(v, "intBool")) return fmt(a, "({s} ? 1 : 0)", .{n});
    return error.InvalidNativeValue;
}
fn constant(a: Allocator, arg: Value) ![]const u8 {
    const v = get(arg, "value");
    return if (nk(v, "pointer")) "System.IntPtr.Zero" else try fmt(a, "unchecked(({s})({d}L))", .{ nativeType(v), number(arg, "constant") });
}
fn handleClass(a: Allocator, t: Value) ![]const u8 {
    return root.name(a, "NyA_", str(t, "capabilityId"));
}
fn scalarValue(c: Context, v: Value, expression: []const u8, ok: Value, symbol: []const u8) ![]const u8 {
    if (nk(v, "integer")) return fmt(c.a, "checked((long){s})", .{expression});
    if (nk(v, "bits64")) return fmt(c.a, "unchecked((long){s})", .{expression});
    if (nk(v, "float")) return fmt(c.a, "(double){s}", .{expression});
    if (nk(v, "bool") or nk(v, "intBool")) return fmt(c.a, "{s} != 0", .{expression});
    if (nk(v, "handle")) return fmt(c.a, "new {s}(NyNative.NotNull({s}, {s}))", .{ try handleClass(c.a, ok), expression, symbol });
    return error.InvalidNativeValue;
}
fn failText(c: Context, result: Value, is_result: bool, message: []const u8) ![]const u8 {
    return if (is_result) try fmt(c.a, "return {s}.Err({s});", .{ try root.formatType(c.a, result), message }) else try fmt(c.a, "throw new System.InvalidOperationException({s});", .{message});
}
fn local(w: *Writer, index: usize, expression: []const u8) ![]const u8 {
    try w.line(try fmt(w.a, "var a{d} = {s};", .{ index, expression }));
    return fmt(w.a, "a{d}", .{index});
}
fn handleArgument(w: *Writer, released: *std.ArrayList([]const u8), index: usize, owner: []const u8, value: []const u8, release: bool) ![]const u8 {
    if (release) {
        try w.line(try fmt(w.a, "{s}.Open({s});", .{ owner, value }));
        try released.append(w.a, try fmt(w.a, "var a{d} = {s}.Release({s});", .{ index, owner, value }));
        return fmt(w.a, "a{d}", .{index});
    }
    return local(w, index, try fmt(w.a, "{s}.Open({s})", .{ owner, value }));
}
fn emitOperation(w: *Writer, c: Context, adapter_name: []const u8, op: Value, binding: Value) !void {
    const a = c.a;
    const method = try root.name(a, "Op_", str(op, "id"));
    const result = get(op, "resultType");
    const result_type = try root.formatType(a, result);
    const signature = try fmt(a, "public {s} {s}({s})", .{ result_type, method, try operationParameters(c, op) });
    const call = get(binding, "call");
    if (call == .null) {
        try w.line(try cat(a, &.{ signature, " => throw new System.NotSupportedException(\"This operation is unavailable on the C# backend.\");" }));
        return;
    }
    const args = arr(call, "arguments");
    const returns = get(call, "returns");
    const extern_name = try cat(a, &.{ "N_", method });
    const free_name = try cat(a, &.{ "F_", method });
    var extern_parameters: std.ArrayList([]const u8) = .empty;
    var cleanup = false;
    for (args, 0..) |arg, i| {
        const v = get(arg, "value");
        const k = kind(arg);
        const sn = try fmt(a, "S_{s}_{d}", .{ method, i });
        if (eq(k, "struct")) {
            var fields: std.ArrayList([]const u8) = .empty;
            for (arr(arg, "fields"), 0..) |field, j| try fields.append(a, try fmt(a, "public {s} f{d};", .{ nativeType(get(field, "value")), j }));
            const size = if (nullValue(arg, "size")) "" else try fmt(a, ", Size = {d}", .{number(arg, "size")});
            try w.line(try fmt(a, "[{s}StructLayout({s}LayoutKind.Sequential{s})] private struct {s} {{ {s} }}", .{ interop, interop, size, sn, try std.mem.join(a, " ", fields.items) }));
        }
        const p = if (eq(k, "self") or eq(k, "out")) try fmt(a, "System.IntPtr a{d}", .{i}) else if ((eq(k, "parameter") or eq(k, "retained")) and nk(v, "bytes")) try fmt(a, "System.IntPtr a{d}, nuint n{d}", .{ i, i }) else if (eq(k, "parameter") or eq(k, "constant")) try fmt(a, "{s} a{d}", .{ nativeType(v), i }) else if (eq(k, "reference")) try fmt(a, "ref {s} a{d}", .{ nativeType(v), i }) else if (eq(k, "retained")) try fmt(a, "System.IntPtr a{d}", .{i}) else if (eq(k, "outBytes")) try fmt(a, "System.IntPtr a{d}, nuint n{d}, out nuint w{d}", .{ i, i, i }) else if (eq(k, "struct")) try fmt(a, "{s}{s} a{d}", .{ if (boolean(arg, "byPointer")) "ref " else "", sn, i }) else return error.InvalidNativeArgument;
        try extern_parameters.append(a, p);
        if ((eq(k, "parameter") and (nk(v, "utf8") or nk(v, "bytes"))) or eq(k, "outBytes") or eq(k, "retained")) cleanup = true;
    }
    try w.line(try dllImport(a, str(call, "library"), str(call, "symbol")));
    try w.line(try fmt(a, "private static extern {s} {s}({s});", .{ nativeType(returns), extern_name, try std.mem.join(a, ", ", extern_parameters.items) }));
    if (nk(returns, "utf8") and !nullValue(returns, "free")) {
        try w.line(try dllImport(a, str(call, "library"), str(returns, "free")));
        try w.line(try fmt(a, "private static extern void {s}(System.IntPtr pointer);", .{free_name}));
    }
    if (!cleanup) try w.line(root.aggressive_inlining);
    try w.line(signature);
    try w.line("{");
    w.push();
    for (args, 0..) |arg, i| {
        if (nk(arg, "parameter") and nk(get(arg, "value"), "utf8")) try w.line(try fmt(a, "var s{d} = System.IntPtr.Zero;", .{i})) else if ((nk(arg, "parameter") and nk(get(arg, "value"), "bytes")) or nk(arg, "outBytes")) try w.line(try fmt(a, "var g{d} = default({s}GCHandle);", .{ i, interop })) else if (nk(arg, "retained")) try w.line(try fmt(a, "var k{d} = System.IntPtr.Zero;", .{i}));
    }
    if (cleanup) {
        try w.line("try");
        try w.line("{");
        w.push();
    }
    var released: std.ArrayList([]const u8) = .empty;
    var arguments: std.ArrayList([]const u8) = .empty;
    for (args, 0..) |arg, i| {
        const v = get(arg, "value");
        const k = kind(arg);
        var expression: []const u8 = undefined;
        if (eq(k, "self")) {
            if (boolean(arg, "release")) expression = try handleArgument(w, &released, i, adapter_name, "this", true) else {
                try w.line(try fmt(a, "var a{d} = _handle;", .{i}));
                try w.line(try fmt(a, "if (a{d} == System.IntPtr.Zero) throw new System.InvalidOperationException(\"Handle is not open\");", .{i}));
                expression = try fmt(a, "a{d}", .{i});
            }
        } else if (eq(k, "constant")) expression = try constant(a, arg) else if (eq(k, "out")) {
            try w.line(try fmt(a, "{s} a{d} = default;", .{ nativeType(v), i }));
            expression = try fmt(a, "NyNative.Address(ref a{d})", .{i});
        } else if (eq(k, "outBytes")) {
            try w.line(try fmt(a, "var b{d} = new byte[{d}];", .{ i, number(arg, "capacity") }));
            try w.line(try fmt(a, "g{d} = {s}GCHandle.Alloc(b{d}, {s}GCHandleType.Pinned);", .{ i, interop, i, interop }));
            try w.line(try fmt(a, "nuint w{d};", .{i}));
            expression = try fmt(a, "g{d}.AddrOfPinnedObject(), (nuint)b{d}.Length, out w{d}", .{ i, i, i });
        } else if (eq(k, "struct")) {
            const sn = try fmt(a, "S_{s}_{d}", .{ method, i });
            var assignments: std.ArrayList([]const u8) = .empty;
            for (arr(arg, "fields"), 0..) |field, j| {
                const fv = get(field, "value");
                const expr = if (nk(field, "parameter")) try convert(a, fv, try root.name(a, "cp_", str(field, "parameterId"))) else if (nk(field, "constant")) try constant(a, field) else if (nk(field, "sizeOf")) try fmt(a, "checked(({s}){s}Marshal.SizeOf<{s}>())", .{ nativeType(fv), interop, sn }) else return error.InvalidNativeArgument;
                try assignments.append(a, try fmt(a, "f{d} = {s}", .{ j, expr }));
            }
            expression = try cat(a, &.{ if (boolean(arg, "byPointer")) "ref " else "", try local(w, i, try fmt(a, "new {s} {{ {s} }}", .{ sn, try std.mem.join(a, ", ", assignments.items) })) });
        } else if (eq(k, "retained")) {
            const pn = try root.name(a, "cp_", str(arg, "parameterId"));
            if (nk(v, "bytes")) {
                try w.line(try fmt(a, "var d{d} = {s}.Span.ToArray();", .{ i, pn }));
                try w.line(try fmt(a, "k{d} = NyNative.Copy(d{d});", .{ i, i }));
                expression = try fmt(a, "k{d}, (nuint)d{d}.Length", .{ i, i });
            } else {
                try w.line(try fmt(a, "k{d} = NyNative.Utf8({s});", .{ i, pn }));
                expression = try fmt(a, "k{d}", .{i});
            }
        } else if (eq(k, "reference") or eq(k, "parameter")) {
            const pn = try root.name(a, "cp_", str(arg, "parameterId"));
            const p = root.find(arr(op, "parameters"), "id", str(arg, "parameterId")).?;
            if (eq(k, "reference")) {
                const expr = if (nk(v, "handle")) try fmt(a, "{s}.Open({s})", .{ try handleClass(a, get(p, "type")), pn }) else try convert(a, v, pn);
                expression = try cat(a, &.{ "ref ", try local(w, i, expr) });
            } else if (nk(v, "utf8")) {
                try w.line(try fmt(a, "s{d} = NyNative.Utf8({s});", .{ i, pn }));
                expression = try fmt(a, "s{d}", .{i});
            } else if (nk(v, "bytes")) {
                try w.line(try fmt(a, "g{d} = {s}GCHandle.Alloc({s}.Span.ToArray(), {s}GCHandleType.Pinned);", .{ i, interop, pn, interop }));
                expression = try fmt(a, "g{d}.AddrOfPinnedObject(), (nuint){s}.Span.Length", .{ i, pn });
            } else if (nk(v, "handle")) expression = try handleArgument(w, &released, i, try handleClass(a, get(p, "type")), pn, boolean(v, "release")) else expression = try local(w, i, try convert(a, v, pn));
        } else return error.InvalidNativeArgument;
        try arguments.append(a, expression);
    }
    for (released.items) |line| try w.line(line);
    const invocation = try fmt(a, "{s}({s})", .{ extern_name, try std.mem.join(a, ", ", arguments.items) });
    const is_result = nk(result, "result");
    const ok = if (is_result) get(result, "okType") else result;
    if (nk(returns, "void")) try w.line(try cat(a, &.{ invocation, ";" })) else try w.line(try fmt(a, "var r = {s};", .{invocation}));
    const symbol = try jsonString(a, str(call, "symbol"));
    for (args, 0..) |arg, i| {
        if (nk(arg, "outBytes") and !nullValue(arg, "retry")) {
            try w.line(try fmt(a, "if (r == {d}L)", .{number(arg, "retry")}));
            try w.line("{");
            w.push();
            const message = try fmt(a, "{s} + \" asked for a buffer of \" + w{d} + \" bytes, above the limit\"", .{ symbol, i });
            try w.line(try fmt(a, "if (w{d} > (nuint)16777216) {s}", .{ i, try failText(c, result, is_result, message) }));
            try w.line(try fmt(a, "g{d}.Free();", .{i}));
            try w.line(try fmt(a, "b{d} = new byte[checked((int)w{d})];", .{ i, i }));
            try w.line(try fmt(a, "g{d} = {s}GCHandle.Alloc(b{d}, {s}GCHandleType.Pinned);", .{ i, interop, i, interop }));
            try w.line(try fmt(a, "r = {s};", .{invocation}));
            w.pop();
            try w.line("}");
        }
    }
    for (args, 0..) |arg, i| if (nk(arg, "retained")) try w.line(try fmt(a, "{{ var previous = _kept_{s}_{d}; _kept_{s}_{d} = k{d}; k{d} = System.IntPtr.Zero; if (previous != System.IntPtr.Zero) NyNative.Free(previous); }}", .{ method, i, method, i, i, i }));
    const failure = failureKind(call);
    if (failure.len > 0) {
        const condition = if (eq(failure, "nullPointer")) "r == System.IntPtr.Zero" else if (eq(failure, "negative")) "r < 0" else "r != 0";
        const message = if (eq(failure, "nullPointer")) try cat(a, &.{ symbol, " + \" returned null\"" }) else try fmt(a, "NyNative.Failed({s}, r)", .{symbol});
        try w.line(try fmt(a, "if ({s}) {s}", .{ condition, try failText(c, result, is_result, message) }));
    }
    for (args, 0..) |arg, i| if (nk(arg, "outBytes")) {
        const message = try fmt(a, "{s} + \" reported \" + w{d} + \" bytes written into a buffer of \" + b{d}.Length", .{ symbol, i, i });
        try w.line(try fmt(a, "if (w{d} > (nuint)b{d}.Length) {s}", .{ i, i, try failText(c, result, is_result, message) }));
    };
    var value: ?[]const u8 = null;
    for (args, 0..) |arg, i| {
        if (nk(arg, "out")) {
            value = try scalarValue(c, get(arg, "value"), try fmt(a, "a{d}", .{i}), ok, symbol);
            break;
        }
        if (nk(arg, "outBytes")) {
            value = try fmt(a, "NyBytes.FromOwnedArray(new System.ReadOnlySpan<byte>(b{d}, 0, checked((int)w{d})).ToArray())", .{ i, i });
            break;
        }
    }
    if (value == null) {
        if (nk(ok, "primitiveUnit")) value = "NyUnit.Value" else if (nk(returns, "utf8")) {
            if (!nullValue(returns, "free")) {
                try w.line("NyStr text;");
                try w.line(try fmt(a, "try {{ text = NyNative.ReadUtf8(r, {s}); }}", .{symbol}));
                try w.line(try fmt(a, "finally {{ if (r != System.IntPtr.Zero) {s}(r); }}", .{free_name}));
                value = "text";
            } else value = try fmt(a, "NyNative.ReadUtf8(r, {s})", .{symbol});
        } else value = try scalarValue(c, returns, "r", ok, symbol);
    }
    const returned = if (is_result) try fmt(a, "{s}.Ok({s})", .{ result_type, value.? }) else value.?;
    try w.line(try fmt(a, "return {s};", .{returned}));
    if (cleanup) {
        w.pop();
        try w.line("}");
        try w.line("finally");
        try w.line("{");
        w.push();
        for (args, 0..) |arg, i| {
            if (nk(arg, "parameter") and nk(get(arg, "value"), "utf8")) try w.line(try fmt(a, "NyNative.Free(s{d});", .{i})) else if ((nk(arg, "parameter") and nk(get(arg, "value"), "bytes")) or nk(arg, "outBytes")) try w.line(try fmt(a, "if (g{d}.IsAllocated) g{d}.Free();", .{ i, i })) else if (nk(arg, "retained")) try w.line(try fmt(a, "if (k{d} != System.IntPtr.Zero) NyNative.Free(k{d});", .{ i, i }));
        }
        w.pop();
        try w.line("}");
    }
    w.pop();
    try w.line("}");
}
pub fn emitAdapters(w: *Writer, c: Context) !void {
    for (arr(c.profile, "capabilityBindings")) |b| {
        if (nk(b, "native")) {
            try emitSupport(w);
            break;
        }
    }
    for (try root.sorted(c.a, c.capabilities(), "id")) |cap| {
        if (!c.hasAdapter(cap)) continue;
        const a = c.a;
        const cap_name = try root.name(a, "NyC_", str(cap, "id"));
        const adapter = try root.name(a, "NyA_", str(cap, "id"));
        try w.line(try fmt(a, "public sealed class {s} : {s}", .{ adapter, cap_name }));
        try w.line("{");
        w.push();
        var handle = false;
        for (arr(cap, "operations")) |op| {
            const b = c.binding(str(op, "id")).?;
            if (nk(b, "native") and boolean(get(b, "native"), "handle")) handle = true;
        }
        if (handle) {
            try w.line("private System.IntPtr _handle;");
            try w.line(try fmt(a, "internal {s}(System.IntPtr handle) {{ _handle = handle; }}", .{adapter}));
            try w.line(root.aggressive_inlining);
            try w.line(try fmt(a, "internal static System.IntPtr Open({s} value) => value is {s} native && native._handle != System.IntPtr.Zero ? native._handle : throw new System.InvalidOperationException(\"Handle is not open\");", .{ cap_name, adapter }));
            var kept: std.ArrayList([]const u8) = .empty;
            for (arr(cap, "operations")) |op| {
                const b = c.binding(str(op, "id")).?;
                if (nk(b, "native")) {
                    for (arr(get(get(b, "native"), "call"), "arguments"), 0..) |arg, i| if (nk(arg, "retained")) try kept.append(a, try fmt(a, "_kept_{s}_{d}", .{ try root.name(a, "Op_", str(op, "id")), i }));
                }
            }
            var free: std.ArrayList([]const u8) = .empty;
            for (kept.items) |field| {
                try w.line(try fmt(a, "private System.IntPtr {s};", .{field}));
                try free.append(a, try fmt(a, "NyNative.Free(System.Threading.Interlocked.Exchange(ref owner.{s}, System.IntPtr.Zero)); ", .{field}));
            }
            const frees = if (kept.items.len == 0) "" else try cat(a, &.{ try fmt(a, "var owner = ({s})value; ", .{adapter}), try std.mem.join(a, "", free.items) });
            try w.line(try fmt(a, "internal static System.IntPtr Release({s} value) {{ var handle = value is {s} native ? System.Threading.Interlocked.Exchange(ref native._handle, System.IntPtr.Zero) : System.IntPtr.Zero; if (handle == System.IntPtr.Zero) throw new System.InvalidOperationException(\"Handle is not open\"); {s}return handle; }}", .{ cap_name, adapter, frees }));
        }
        for (try root.sorted(a, arr(cap, "operations"), "id")) |op| {
            const b = c.binding(str(op, "id")).?;
            if (nk(b, "native")) try emitOperation(w, c, adapter, op, get(b, "native")) else {
                try w.line(try fmt(a, "public {s} {s}({s})", .{ try root.formatType(a, get(op, "resultType")), try root.name(a, "Op_", str(op, "id")), try operationParameters(c, op) }));
                try w.line("{");
                w.push();
                try w.line(try fmt(a, "System.Console.WriteLine({s});", .{try parameterName(a, arr(op, "parameters")[0])}));
                try w.line("return NyUnit.Value;");
                w.pop();
                try w.line("}");
            }
        }
        w.pop();
        try w.line("}");
        try w.line("");
    }
}

test "native scalar validation and FSharp descriptions" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const parsed = try std.json.parseFromSlice(Value, a, "{\"kind\":\"integer\",\"bits\":8,\"signed\":false}", .{});
    const v = parsed.value;
    try std.testing.expect(constantFits(255, v));
    try std.testing.expect(!constantFits(256, v));
    try std.testing.expect(!constantFits(-1, v));
    try std.testing.expectEqualStrings("Integer (8, false)", try valueText(a, v));
    try std.testing.expectEqualStrings("\"a\\u0022\\u002B\\u003C\\n\"", try jsonString(a, "a\"+<\n"));
}

test "native adapters cover buffers ownership references structs and unavailable calls" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const data = try std.json.parseFromSlice(Value, a,
        \\{"program":{"capabilities":[{"id":"h","operations":[
        \\{"id":"release","parameters":[],"resultType":{"kind":"primitiveUnit"}},
        \\{"id":"retain","parameters":[{"id":"text","type":{"kind":"primitiveString"}}],"resultType":{"kind":"primitiveUnit"}},
        \\{"id":"read","parameters":[],"resultType":{"kind":"result","okType":{"kind":"primitiveBytes"},"errorType":{"kind":"primitiveString"}}},
        \\{"id":"unavailable","parameters":[],"resultType":{"kind":"primitiveUnit"}}
        \\]},{"id":"api","operations":[
        \\{"id":"create","parameters":[],"resultType":{"kind":"capability","capabilityId":"h"}},
        \\{"id":"text","parameters":[{"id":"bytes","type":{"kind":"primitiveBytes"}}],"resultType":{"kind":"primitiveString"}},
        \\{"id":"struct","parameters":[{"id":"x","type":{"kind":"primitiveInt64"}}],"resultType":{"kind":"primitiveUnit"}},
        \\{"id":"reference","parameters":[{"id":"x","type":{"kind":"primitiveFloat64"}}],"resultType":{"kind":"primitiveInt64"}}
        \\]}]},"profile":{"capabilityBindings":[
        \\{"operationId":"release","kind":"native","native":{"handle":true,"call":{"library":"lib","symbol":"release","arguments":[{"kind":"self","release":true}],"returns":{"kind":"void"}}}},
        \\{"operationId":"retain","kind":"native","native":{"handle":true,"call":{"library":"lib","symbol":"retain","arguments":[{"kind":"self","release":false},{"kind":"retained","parameterId":"text","value":{"kind":"utf8"}}],"returns":{"kind":"void"}}}},
        \\{"operationId":"read","kind":"native","native":{"handle":true,"call":{"library":"lib","symbol":"read","arguments":[{"kind":"self","release":false},{"kind":"outBytes","capacity":8,"retry":-1}],"returns":{"kind":"integer","bits":32,"signed":true},"failure":"negative"}}},
        \\{"operationId":"unavailable","kind":"native","native":{"handle":true,"call":null}},
        \\{"operationId":"create","kind":"native","native":{"handle":false,"call":{"library":"lib","symbol":"create","arguments":[],"returns":{"kind":"handle","release":false},"failure":"nullPointer"}}},
        \\{"operationId":"text","kind":"native","native":{"handle":false,"call":{"library":"lib","symbol":"text","arguments":[{"kind":"parameter","parameterId":"bytes","value":{"kind":"bytes"}}],"returns":{"kind":"utf8","free":"free"}}}},
        \\{"operationId":"struct","kind":"native","native":{"handle":false,"call":{"library":"lib","symbol":"struct","arguments":[{"kind":"struct","byPointer":true,"size":24,"fields":[{"kind":"sizeOf","value":{"kind":"integer","bits":64,"signed":false}},{"kind":"parameter","parameterId":"x","value":{"kind":"integer","bits":32,"signed":true}},{"kind":"constant","constant":0,"value":{"kind":"pointer"}}]}],"returns":{"kind":"void"}}}},
        \\{"operationId":"reference","kind":"native","native":{"handle":false,"call":{"library":"lib","symbol":"reference","arguments":[{"kind":"reference","parameterId":"x","value":{"kind":"float","bits":32}},{"kind":"out","value":{"kind":"bits64"}}],"returns":{"kind":"void"}}}}
        \\]}}
    , .{});
    const c: Context = .{ .a = a, .program = get(data.value, "program"), .profile = get(data.value, "profile") };
    for (c.capabilities()) |cap| for (arr(cap, "operations")) |op| try std.testing.expectEqual(@as(?[]const u8, null), try problem(c, str(cap, "id"), op, get(c.binding(str(op, "id")).?, "native")));
    var w: Writer = .{ .a = a };
    try emitAdapters(&w, c);
    const required = [_][]const u8{
        "if (w1 > (nuint)16777216)",                                                     "b1 = new byte[checked((int)w1)];",
        "System.Threading.Interlocked.Exchange(ref native._handle, System.IntPtr.Zero)", "NyNative.Free(System.Threading.Interlocked.Exchange(ref owner._kept_Op_72657461696E_1, System.IntPtr.Zero));",
        "public static System.IntPtr Copy(byte[] bytes)",                                "private static extern void F_Op_74657874(System.IntPtr pointer);",
        "Marshal.SizeOf<S_Op_737472756374_0>()",                                         "NyNative.Float32(cp_78)",
        "NyNative.Address(ref a1)",                                                      "This operation is unavailable on the C# backend.",
    };
    // Copy is part of shared support even when this fixture retains UTF-8.
    for (required) |text| try std.testing.expect(std.mem.indexOf(u8, w.text.items, text) != null);
    const bad = try std.json.parseFromSlice(Value, a, "{\"kind\":\"constant\",\"constant\":256,\"value\":{\"kind\":\"integer\",\"bits\":8,\"signed\":false}}", .{});
    try std.testing.expectEqualStrings("constant 256 does not fit Integer (8, false)", (try argumentProblem(c, .null, false, false, bad.value)).?);
    try std.testing.expectEqualStrings("\"\\b\\f\"", try jsonString(a, "\x08\x0c"));
    try std.testing.expectEqualStrings("\"\\\\u0008\\\\u000C\"", try jsonString(a, "\\u0008\\u000C"));
    try std.testing.expectEqualStrings("\"lib\\\\\"", try jsonString(a, "lib\\"));
}
