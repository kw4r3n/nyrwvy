const std = @import("std");
const b = @import("root.zig");
const Value = b.Value;
const Context = b.Context;
const Writer = b.Writer;
pub fn valueType(fn_: Value, id: []const u8) Value {
    for (b.arr(fn_, "parameters")) |p| {
        const v = b.get(p, "value");
        if (b.eq(b.str(v, "id"), id)) return b.get(v, "type");
    }
    for (b.arr(fn_, "blocks")) |block| {
        for (b.arr(block, "parameters")) |v| if (b.eq(b.str(v, "id"), id)) return b.get(v, "type");
        for (b.arr(block, "instructions")) |i| {
            const v = b.get(i, "result");
            if (b.eq(b.str(v, "id"), id)) return b.get(v, "type");
        }
    }
    return .null;
}
fn parameter(fn_: Value, id: []const u8) !Value {
    return b.find(b.arr(fn_, "parameters"), "semanticParameterId", id) orelse error.MalformedExecutionIr;
}
pub fn callbackName(a: b.Allocator, id: []const u8) ![]const u8 {
    return b.cat(a, &.{ "NyFunctions.", try b.name(a, "NyF_", id) });
}
pub fn namedArguments(a: b.Allocator, args: []const Value, key: []const u8, prefix: []const u8, assign: []const u8) anyerror![]const u8 {
    var parts: std.ArrayList([]const u8) = .empty;
    for (try b.sorted(a, args, key)) |arg| try parts.append(a, try b.cat(a, &.{ try b.name(a, prefix, b.str(arg, key)), assign, try b.valueName(a, b.str(arg, "valueId")) }));
    return std.mem.join(a, ", ", parts.items);
}
pub fn valueList(a: b.Allocator, args: []const Value) anyerror![]const u8 {
    var parts: std.ArrayList([]const u8) = .empty;
    for (args) |arg| try parts.append(a, try b.valueName(a, arg.string));
    return std.mem.join(a, ", ", parts.items);
}
pub fn invocation(c: Context, id: []const u8, context: []const Value, iterations: []const []const u8) anyerror![]const u8 {
    const a = c.a;
    var parts: std.ArrayList([]const u8) = .empty;
    for (context) |arg| {
        const v = try b.valueName(a, b.str(arg, "valueId"));
        try parts.append(a, try b.cat(a, &.{ try b.name(a, "p_", b.str(arg, "targetParameterId")), ": ", v }));
    }
    var at: usize = 0;
    for (b.arr(c.function(id), "parameters")) |p| {
        const pid = b.str(p, "semanticParameterId");
        if (b.find(context, "targetParameterId", pid) != null) continue;
        if (at >= iterations.len) return error.InvalidCallbackArity;
        try parts.append(a, try b.cat(a, &.{ try b.name(a, "p_", pid), ": ", iterations[at] }));
        at += 1;
    }
    if (at != iterations.len) return error.InvalidCallbackArity;
    return b.cat(a, &.{ try callbackName(a, id), "(", try std.mem.join(a, ", ", parts.items), ")" });
}
// TaskStart supplies the captured slots explicitly instead of sharing emitter state.
pub fn capturedInvocation(c: Context, id: []const u8, context: []const Value, target: []const u8, item: []const u8) anyerror![]const u8 {
    const a = c.a;
    var parts: std.ArrayList([]const u8) = .empty;
    for (context, 0..) |arg, i| try parts.append(a, try b.fmt(a, "{s}: ref {s}_c{d}", .{ try b.name(a, "p_", b.str(arg, "targetParameterId")), target, i }));
    for (b.arr(c.function(id), "parameters")) |p| {
        const pid = b.str(p, "semanticParameterId");
        if (b.find(context, "targetParameterId", pid) == null) try parts.append(a, try b.fmt(a, "{s}: ref {s}", .{ try b.name(a, "p_", pid), item }));
    }
    return b.cat(a, &.{ try callbackName(a, id), "(", try std.mem.join(a, ", ", parts.items), ")" });
}
fn mentionsParameter(t: Value) bool {
    if (b.eq(b.kind(t), "typeParameter")) return true;
    if (t == .object) {
        var it = t.object.iterator();
        while (it.next()) |entry| {
            const v = entry.value_ptr.*;
            if (v == .object and mentionsParameter(v)) return true;
            if (v == .array) for (v.array.items) |child| if (mentionsParameter(child)) return true;
        }
    }
    return false;
}
pub fn callback(c: Context, target: []const u8, id: []const u8, context: []const Value) anyerror![]const u8 {
    const params = b.arr(c.function(id), "parameters");
    var is_generic = false;
    for (params) |p| {
        if (mentionsParameter(b.get(b.get(p, "value"), "type"))) is_generic = true;
    }
    if (context.len == 0 and !is_generic) return callbackName(c.a, id);
    if (context.len > params.len) return error.InvalidCallbackArity;
    const names = try c.a.alloc([]const u8, params.len - context.len);
    for (names, 0..) |*n, i| n.* = try b.fmt(c.a, "{s}_it{d}", .{ target, i });
    return b.cat(c.a, &.{ "(", try std.mem.join(c.a, ", ", names), ") => ", try invocation(c, id, context, names) });
}
pub fn instructions(a: b.Allocator, program: Value) ![]const Value {
    var out: std.ArrayList(Value) = .empty;
    for (b.arr(program, "functions")) |fn_| for (b.arr(fn_, "blocks")) |block| try out.appendSlice(a, b.arr(block, "instructions"));
    return out.items;
}
pub fn targets(a: b.Allocator, k: Value) ![]const []const u8 {
    var out: std.ArrayList([]const u8) = .empty;
    const op = b.kind(k);
    if (b.eq(op, "directCall") or b.eq(op, "functionReference")) try out.append(a, b.str(k, "functionId"));
    for ([_][]const u8{ "folderFunctionId", "mapperFunctionId", "combinerFunctionId", "keyOfFunctionId", "predicateFunctionId", "keepFunctionId", "binderFunctionId", "lessThanFunctionId", "workerFunctionId", "bodyFunctionId", "handlerFunctionId" }) |key| {
        const id = b.str(k, key);
        if (id.len > 0) try out.append(a, id);
    }
    for (b.arr(k, "implementations")) |impl| try out.append(a, b.str(impl, "functionId"));
    return out.items;
}
pub fn operations(a: b.Allocator, k: Value) ![]const []const u8 {
    var out: std.ArrayList([]const u8) = .empty;
    const op = b.kind(k);
    if (b.eq(op, "capabilityInvoke")) try out.append(a, b.str(k, "operationId"));
    if (b.eq(op, "useResource")) try out.append(a, b.str(k, "releaseOperationId"));
    return out.items;
}
fn generic(a: b.Allocator, fn_: Value) ![]const u8 {
    var parts: std.ArrayList([]const u8) = .empty;
    for (b.arr(fn_, "typeParameters")) |t| try parts.append(a, try b.name(a, "T_", b.str(t, "typeParameterId")));
    return if (parts.items.len == 0) "" else b.cat(a, &.{ "<", try std.mem.join(a, ", ", parts.items), ">" });
}
fn temp(a: b.Allocator, block: Value, index: usize) ![]const u8 {
    return b.fmt(a, "{s}_{d}", .{ try b.name(a, "jt_", b.str(block, "id")), index });
}
fn assignments(a: b.Allocator, fn_: Value, term: Value) ![]const Value {
    const args = b.arr(term, "arguments");
    if (!b.eq(b.kind(term), "tailCall")) return args;
    var result: std.ArrayList(Value) = .empty;
    for (try b.sorted(a, args, "targetParameterId")) |arg| try result.append(a, try b.object(a, &.{ "targetValueId", "valueId" }, &.{ b.get(b.get(try parameter(fn_,b.str(arg, "targetParameterId")), "value"), "id"), b.get(arg, "valueId") }));
    return result.items;
}
fn parallelAssignment(w: *Writer, block: Value, args: []const Value) anyerror!void {
    const a = w.a;
    for (args, 0..) |arg, i| try w.line(try b.cat(a, &.{ try temp(a, block, i), " = ", try b.valueName(a, b.str(arg, "valueId")), ";" }));
    for (args, 0..) |arg, i| try w.line(try b.cat(a, &.{ try b.valueName(a, b.str(arg, "targetValueId")), " = ", try temp(a, block, i), ";" }));
}
fn terminator(w: *Writer, fn_: Value, block: Value) anyerror!void {
    const a = w.a;
    const t = b.get(block, "terminator");
    const op = b.kind(t);
    if (b.eq(op, "tailCall") or b.eq(op, "jump")) {
        try parallelAssignment(w, block, try assignments(a, fn_, t));
        try w.line(try b.cat(a, &.{ "goto ", try b.name(a, "L_", if (b.eq(op, "tailCall")) b.str(fn_, "entryBlockId") else b.str(t, "blockId")), ";" }));
        return;
    }
    if (b.eq(op, "return")) {
        try w.line(try b.cat(a, &.{ "return ", try b.valueName(a, b.str(t, "valueId")), ";" }));
        return;
    }
    if (b.eq(op, "abort")) {
        try w.line(try b.cat(a, &.{ "return ", try b.formatType(a, b.get(fn_, "resultType")), ".Err(", try b.valueName(a, b.str(t, "errorValueId")), ");" }));
        return;
    }
    if (b.eq(op, "matchUnion")) {
        const ts = try b.sorted(a, b.arr(t, "targets"), "variantId");
        if (ts.len == 0) {
            try w.line("throw new InvalidOperationException(\"Execution IR union match has no targets.\");");
            return;
        }
        for (ts, 0..) |target, i| try w.line(try b.cat(a, &.{ if (i == 0) "if" else "else if", " (", try b.valueName(a, b.str(t, "inputValueId")), " is ", try b.name(a, "NyV_", b.str(target, "variantId")), try b.typeArgs(a, b.arr(valueType(fn_, b.str(t, "inputValueId")), "arguments")), ") goto ", try b.name(a, "L_", b.str(target, "blockId")), ";" }));
        try w.line("else throw new InvalidOperationException(\"Execution IR union value did not match a declared variant.\");");
        return;
    }
    var condition: []const u8 = undefined;
    var yes: []const u8 = undefined;
    var no: []const u8 = undefined;
    if (b.eq(op, "branch")) {
        condition = try b.valueName(a, b.str(t, "conditionValueId"));
        yes = b.str(t, "whenTrue");
        no = b.str(t, "whenFalse");
    } else if (b.eq(op, "matchOption")) {
        condition = try b.cat(a, &.{ try b.valueName(a, b.str(t, "inputValueId")), ".HasValue" });
        yes = b.str(t, "someBlock");
        no = b.str(t, "noneBlock");
    } else if (b.eq(op, "matchResult")) {
        condition = try b.cat(a, &.{ try b.valueName(a, b.str(t, "inputValueId")), ".IsOk" });
        yes = b.str(t, "okBlock");
        no = b.str(t, "errBlock");
    } else return error.InvalidTerminator;
    try w.line(try b.cat(a, &.{ "if (", condition, ") goto ", try b.name(a, "L_", yes), ";" }));
    try w.line(try b.cat(a, &.{ "goto ", try b.name(a, "L_", no), ";" }));
}
fn function(w: *Writer, c: Context, fn_: Value, consume: bool) anyerror!void {
    const a = w.a;
    var params: std.ArrayList([]const u8) = .empty;
    for (b.arr(fn_, "parameters")) |p| try params.append(a, try b.cat(a, &.{ if (consume) "ref " else "", try b.formatType(a, b.get(b.get(p, "value"), "type")), " ", try b.name(a, "p_", b.str(p, "semanticParameterId")) }));
    const blocks = b.arr(fn_, "blocks");
    var forwards = false;
    if (blocks.len == 1 and b.arr(blocks[0], "instructions").len <= 4) for (b.arr(blocks[0], "instructions")) |i| {
        const k = i;
        if (b.eq(b.kind(k), "capabilityInvoke")) {
            const typ = valueType(fn_, b.str(k, "capabilityValueId"));
            if (b.eq(b.kind(typ), "capability") and c.hasAdapter(c.capability(b.str(typ, "capabilityId")))) forwards = true;
        }
    };
    if (forwards) try w.line(b.aggressive_inlining);
    try w.line(try b.cat(a, &.{ if (consume) "private" else "public", " static ", try b.formatType(a, b.get(fn_, "resultType")), " ", try b.name(a, "NyF_", b.str(fn_, "semanticFunctionId")), try generic(a, fn_), "(", try std.mem.join(a, ", ", params.items), ")" }));
    try w.line("{");
    w.push();
    var vals: std.ArrayList(Value) = .empty;
    for (b.arr(fn_, "parameters")) |p| try vals.append(a, b.get(p, "value"));
    for (blocks) |block| {
        for (b.arr(block, "parameters")) |v| if (b.find(vals.items, "id", b.str(v, "id")) == null) try vals.append(a, v);
        for (b.arr(block, "instructions")) |i| {
            const v = b.get(i, "result");
            if (b.find(vals.items, "id", b.str(v, "id")) == null) try vals.append(a, v);
        }
    }
    for (try b.sorted(a, vals.items, "id")) |v| try w.line(try b.cat(a, &.{ try b.formatType(a, b.get(v, "type")), " ", try b.valueName(a, b.str(v, "id")), " = default!;" }));
    for (try b.sorted(a, blocks, "id")) |block| {
        const t = b.get(block, "terminator");
        if (b.eq(b.kind(t), "jump") or b.eq(b.kind(t), "tailCall")) for (try assignments(a, fn_, t), 0..) |arg, i| try w.line(try b.cat(a, &.{ try b.formatType(a, valueType(fn_, b.str(arg, "targetValueId"))), " ", try temp(a, block, i), " = default!;" }));
    }
    for (b.arr(fn_, "parameters")) |p| try w.line(try b.cat(a, &.{ try b.valueName(a, b.str(b.get(p, "value"), "id")), " = ", try b.name(a, "p_", b.str(p, "semanticParameterId")), ";" }));
    const ownership = try @import("ownership.zig").plan(a, fn_);
    var updates = false;
    for (blocks) |block| for (b.arr(block, "instructions")) |i| {
        const k = b.kind(i);
        if (b.eq(k, "bytesSet") or b.eq(k, "bytesAppend")) updates = true;
    };
    if (updates) try w.line("object nyBytesOwner = new();");
    if (consume) for (b.arr(fn_, "parameters")) |p| try w.line(try b.cat(a, &.{ try b.name(a, "p_", b.str(p, "semanticParameterId")), " = default!;" }));
    try w.line(try b.cat(a, &.{ "goto ", try b.name(a, "L_", b.str(fn_, "entryBlockId")), ";" }));
    try w.line("");
    for (try b.sorted(a, blocks, "id")) |block| {
        w.pop();
        try w.line(try b.cat(a, &.{ try b.name(a, "L_", b.str(block, "id")), ":" }));
        w.push();
        for (b.arr(block, "instructions")) |i| {
            const id = b.str(b.get(i, "result"), "id");
            try release(w, ownership.escapes.get(id) orelse &.{});
            const k = i;
            const op = b.kind(k);
            if (b.eq(op, "bytesSet") or b.eq(op, "bytesAppend")) {
                try w.line(try b.cat(a, &.{ try b.valueName(a, id), " = ", try b.valueName(a, b.str(k, "bytesId")), if (b.eq(op, "bytesSet")) ".Set(" else ".Append(", if (b.eq(op, "bytesSet")) try b.cat(a, &.{ try b.valueName(a, b.str(k, "indexId")), ", " }) else "", try b.valueName(a, b.str(k, "valueId")), ", nyBytesOwner, ", if (ownership.in_place.contains(id)) "true" else "false", ");" }));
            } else if (!try @import("instructions.zig").simple(w, c, i)) try @import("complex.zig").emit(w, c, fn_, i);
        }
        try release(w, ownership.tail_releases.get(b.str(block, "id")) orelse &.{});
        try terminator(w, fn_, block);
        try w.line("");
    }
    w.pop();
    try w.line("}");
    try w.line("");
}
fn release(w: *Writer, ids: []const []const u8) !void {
    for (ids) |id| try w.line(try b.cat(w.a, &.{ try b.valueName(w.a, id), ".ReleaseOwner();" }));
}
pub fn emit(w: *Writer, c: Context) anyerror!void {
    try w.line("public static class NyFunctions");
    try w.line("{");
    w.push();
    var workers: std.StringHashMapUnmanaged(void) = .empty;
    for (try instructions(c.a, c.program)) |i| {
        const k = i;
        if (b.eq(b.kind(k), "taskStart")) try workers.put(c.a, b.str(k, "workerFunctionId"), {});
    }
    for (try b.sorted(c.a, c.functions(), "semanticFunctionId")) |fn_| {
        try function(w, c, fn_, false);
        if (workers.contains(b.str(fn_, "semanticFunctionId"))) try function(w, c, fn_, true);
    }
    w.pop();
    try w.line("}");
}
