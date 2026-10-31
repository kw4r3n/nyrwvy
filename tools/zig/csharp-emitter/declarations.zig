const std = @import("std");
const b = @import("root.zig");
const Value = b.Value;
const Context = b.Context;
const Writer = b.Writer;
fn taskType(t: Value) bool {
    const k = b.kind(t);
    if (b.eq(k, "task") or b.eq(k, "taskScope") or b.eq(k, "oneShot") or b.eq(k, "exception")) return true;
    if (t == .object) {
        var it = t.object.iterator();
        while (it.next()) |e| {
            const v = e.value_ptr.*;
            if (v == .object and taskType(v)) return true;
            if (v == .array) for (v.array.items) |child| if (taskType(child)) return true;
        }
    }
    return false;
}
fn hasTasks(c: Context) bool {
    for (b.arr(c.program, "types")) |t| {
        for (fields(t)) |f| if (taskType(b.get(f, "type"))) return true;
        for (b.arr(t, "variants")) |v| for (fields(v)) |f| if (taskType(b.get(f, "type"))) return true;
    }
    for (c.functions()) |fn_| {
        if (taskType(b.get(fn_, "resultType"))) return true;
        for (b.arr(fn_, "parameters")) |p| if (taskType(b.get(b.get(p, "value"), "type"))) return true;
        for (b.arr(fn_, "blocks")) |block| for (b.arr(block, "instructions")) |i| {
            if (taskType(b.get(b.get(i, "result"), "type"))) return true;
            const op = b.kind(i);
            if (std.mem.startsWith(u8, op, "task") or std.mem.startsWith(u8, op, "oneShot")) return true;
        };
    }
    return false;
}
fn concurrency(c: Context) bool {
    if (hasTasks(c)) return true;
    for (c.functions()) |fn_| for (b.arr(fn_, "blocks")) |block| for (b.arr(block, "instructions")) |i| {
        const k = b.kind(i);
        for ([_][]const u8{ "parallelAll", "cancellationRequested", "cancellationShield" }) |name| if (b.eq(k, name)) return true;
    };
    return false;
}
fn arities(a: b.Allocator, t: Value, set: *std.AutoHashMapUnmanaged(usize, void)) anyerror!void {
    if (b.eq(b.kind(t), "function")) try set.put(a, b.arr(t, "parameters").len, {});
    if (t == .object) {
        var it = t.object.iterator();
        while (it.next()) |e| {
            const v = e.value_ptr.*;
            if (v == .object) try arities(a, v, set);
            if (v == .array) for (v.array.items) |child| try arities(a, child, set);
        }
    }
}
fn functionTypes(w: *Writer, c: Context) anyerror!void {
    var set: std.AutoHashMapUnmanaged(usize, void) = .empty;
    try arities(c.a, c.program, &set);
    const counts = try c.a.alloc(usize, set.count());
    var it = set.keyIterator();
    var at: usize = 0;
    while (it.next()) |n| {
        counts[at] = n.*;
        at += 1;
    }
    std.mem.sort(usize, counts, {}, std.sort.asc(usize));
    for (counts) |count| {
        var ts: std.ArrayList([]const u8) = .empty;
        var ps: std.ArrayList([]const u8) = .empty;
        for (0..count) |i| {
            try ts.append(c.a, try b.fmt(c.a, "T{d}", .{i}));
            try ps.append(c.a, try b.fmt(c.a, "T{d} arg{d}", .{ i, i }));
        }
        try ts.append(c.a, "TResult");
        try w.line(try b.fmt(c.a, "public delegate TResult NyFunction{d}<{s}>({s});", .{ count, try std.mem.join(c.a, ", ", ts.items), try std.mem.join(c.a, ", ", ps.items) }));
    }
    if (counts.len > 0) try w.line("");
}
fn fields(t: Value) []const Value {
    return b.arr(t, "fields");
}
fn embedded(c: Context, t: Value, pending: *std.ArrayList([]const u8)) anyerror!void {
    const k = b.kind(t);
    if (b.eq(k, "named")) {
        const decl = b.find(b.arr(c.program, "types"), "id", b.str(t, "typeId")) orelse return;
        if (b.eq(b.kind(decl), "product")) {
            try pending.append(c.a, b.str(t, "typeId"));
            for (b.arr(t, "arguments")) |arg| try embedded(c, arg, pending);
        }
    } else if (b.eq(k, "option")) try embedded(c, b.get(t, "itemType"), pending) else if (b.eq(k, "result")) {
        try embedded(c, b.get(t, "okType"), pending);
        try embedded(c, b.get(t, "errorType"), pending);
    }
}
fn structProduct(c: Context, t: Value) anyerror!bool {
    const start = b.str(t, "id");
    var todo: std.ArrayList([]const u8) = .empty;
    var seen: std.StringHashMapUnmanaged(void) = .empty;
    for (fields(t)) |f| try embedded(c, b.get(f, "type"), &todo);
    while (todo.pop()) |id| {
        if (b.eq(id, start)) return false;
        if (seen.contains(id)) continue;
        try seen.put(c.a, id, {});
        const child = b.find(b.arr(c.program, "types"), "id", id).?;
        for (fields(child)) |f| try embedded(c, b.get(f, "type"), &todo);
    }
    return true;
}
fn typeParameters(a: b.Allocator, t: Value) ![]const u8 {
    var parts: std.ArrayList([]const u8) = .empty;
    for (b.arr(t, "typeParameters")) |p| try parts.append(a, try b.name(a, "T_", b.str(p, "typeParameterId")));
    if (parts.items.len == 0) return "";
    return b.cat(a, &.{ "<", try std.mem.join(a, ", ", parts.items), ">" });
}
fn fieldParameters(a: b.Allocator, t: Value) ![]const u8 {
    var parts: std.ArrayList([]const u8) = .empty;
    for (try b.sorted(a, fields(t), "id")) |f| try parts.append(a, try b.cat(a, &.{ try b.formatType(a, b.get(f, "type")), " ", try b.name(a, "f_", b.str(f, "id")) }));
    return std.mem.join(a, ", ", parts.items);
}
pub fn operationParameters(a: b.Allocator, op: Value) ![]const u8 {
    var parts: std.ArrayList([]const u8) = .empty;
    for (b.arr(op, "parameters")) |p| try parts.append(a, try b.cat(a, &.{ try b.formatType(a, b.get(p, "type")), " ", try b.name(a, "cp_", b.str(p, "id")) }));
    return std.mem.join(a, ", ", parts.items);
}
fn types(w: *Writer, c: Context) anyerror!void {
    for (try b.sorted(c.a, b.arr(c.program, "types"), "id")) |t| {
        const suffix = try typeParameters(c.a, t);
        const tn = try b.cat(c.a, &.{ try b.name(c.a, "NyT_", b.str(t, "id")), suffix });
        if (b.eq(b.kind(t), "product")) {
            try w.line(try b.cat(c.a, &.{ if (try structProduct(c, t)) "public readonly record struct " else "public sealed record ", tn, "(", try fieldParameters(c.a, t), ");" }));
        } else {
            try w.line(try b.cat(c.a, &.{ "public abstract record ", tn, ";" }));
            for (try b.sorted(c.a, b.arr(t, "variants"), "id")) |v| try w.line(try b.cat(c.a, &.{ "public sealed record ", try b.name(c.a, "NyV_", b.str(v, "id")), suffix, "(", try fieldParameters(c.a, v), ") : ", tn, ";" }));
        }
        try w.line("");
    }
}
fn capabilities(w: *Writer, c: Context) anyerror!void {
    for (try b.sorted(c.a, c.capabilities(), "id")) |cap| {
        try w.line(try b.cat(c.a, &.{ "public interface ", try b.name(c.a, "NyC_", b.str(cap, "id")) }));
        try w.line("{");
        w.push();
        for (try b.sorted(c.a, b.arr(cap, "operations"), "id")) |op| try w.line(try b.cat(c.a, &.{ try b.formatType(c.a, b.get(op, "resultType")), " ", try b.name(c.a, "Op_", b.str(op, "id")), "(", try operationParameters(c.a, op), ");" }));
        w.pop();
        try w.line("}");
        try w.line("");
    }
}
fn implementations(w: *Writer, c: Context) anyerror!void {
    for (try b.sorted(c.a, try @import("functions.zig").instructions(c.a, c.program), "originNodeId")) |i| {
        const k = i;
        if (!b.eq(b.kind(k), "capabilityConstruct")) continue;
        const cap = c.capability(b.str(k, "capabilityId"));
        try w.line(try b.cat(c.a, &.{ "internal sealed class ", try b.name(c.a, "NyI_", b.str(i, "originNodeId")), " : ", try b.name(c.a, "NyC_", b.str(cap, "id")) }));
        try w.line("{");
        w.push();
        for (try b.sorted(c.a, b.arr(cap, "operations"), "id")) |op| {
            const impl = b.find(b.arr(k, "implementations"), "operationId", b.str(op, "id")).?;
            const fn_ = c.function(b.str(impl, "functionId"));
            var args: std.ArrayList([]const u8) = .empty;
            for (b.arr(fn_, "parameters"), b.arr(op, "parameters")) |p, supplied| try args.append(c.a, try b.cat(c.a, &.{ try b.name(c.a, "p_", b.str(p, "semanticParameterId")), ": ", try b.name(c.a, "cp_", b.str(supplied, "id")) }));
            try w.line(try b.cat(c.a, &.{ "public ", try b.formatType(c.a, b.get(op, "resultType")), " ", try b.name(c.a, "Op_", b.str(op, "id")), "(", try operationParameters(c.a, op), ") => NyFunctions.", try b.name(c.a, "NyF_", b.str(impl, "functionId")), "(", try std.mem.join(c.a, ", ", args.items), ");" }));
        }
        w.pop();
        try w.line("}");
        try w.line("");
    }
}
pub fn emit(w: *Writer, c: Context) anyerror!void {
    if (concurrency(c)) try w.lines(@embedFile("runtime/Concurrency.cs"));
    if (hasTasks(c)) {
        try w.lines(@embedFile("runtime/OneShot.cs"));
        try w.lines(@embedFile("runtime/Task.cs"));
    }
    try functionTypes(w, c);
    try types(w, c);
    try capabilities(w, c);
    try @import("native.zig").emitAdapters(w, c);
    try implementations(w, c);
}
fn symbols(a: b.Allocator, items: []const Value, key: []const u8, prefix: []const u8) !Value {
    var out: std.ArrayList(Value) = .empty;
    for (try b.sorted(a, items, key)) |item| {
        const id = b.str(item, key);
        try out.append(a, try b.object(a, &.{ "id", "name" }, &.{ b.string(id), b.string(try b.name(a, prefix, id)) }));
    }
    return b.array(a, out.items);
}
pub fn manifest(c: Context) anyerror!Value {
    const a = c.a;
    var fs: std.ArrayList(Value) = .empty;
    var vs: std.ArrayList(Value) = .empty;
    var ps: std.ArrayList(Value) = .empty;
    var ops: std.ArrayList(Value) = .empty;
    var cps: std.ArrayList(Value) = .empty;
    var adapters: std.ArrayList(Value) = .empty;
    for (b.arr(c.program, "types")) |t| {
        try vs.appendSlice(a, b.arr(t, "variants"));
        for (fields(t)) |f| if (b.find(fs.items, "id", b.str(f, "id")) == null) try fs.append(a, f);
        for (b.arr(t, "variants")) |v| for (fields(v)) |f| {
            if (b.find(fs.items, "id", b.str(f, "id")) == null) try fs.append(a, f);
        };
    }
    for (c.functions()) |fn_| try ps.appendSlice(a, b.arr(fn_, "parameters"));
    for (c.capabilities()) |cap| {
        try ops.appendSlice(a, b.arr(cap, "operations"));
        if (c.hasAdapter(cap)) try adapters.append(a, cap);
        for (b.arr(cap, "operations")) |op| try cps.appendSlice(a, b.arr(op, "parameters"));
    }
    return b.object(a, &.{ "namespace", "functionContainer", "types", "fields", "variants", "functions", "parameters", "capabilities", "capabilityOperations", "capabilityParameters", "capabilityAdapters" }, &.{ b.string(b.generated_namespace), b.string(b.function_container_name), try symbols(a, b.arr(c.program, "types"), "id", "NyT_"), try symbols(a, fs.items, "id", "f_"), try symbols(a, vs.items, "id", "NyV_"), try symbols(a, c.functions(), "semanticFunctionId", "NyF_"), try symbols(a, ps.items, "semanticParameterId", "p_"), try symbols(a, c.capabilities(), "id", "NyC_"), try symbols(a, ops.items, "id", "Op_"), try symbols(a, cps.items, "id", "cp_"), try symbols(a, adapters.items, "id", "NyA_") });
}
