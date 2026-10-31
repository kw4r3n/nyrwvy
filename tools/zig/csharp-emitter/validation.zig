const std = @import("std");
const b = @import("root.zig");
const f = @import("functions.zig");
const Value = b.Value;
const Context = b.Context;
const Failure = b.Failure;
const Set = std.StringHashMapUnmanaged(void);
fn fail(message: []const u8) Failure {
    return .{ .message = message };
}
fn strings(a: b.Allocator, set: Set) ![]const []const u8 {
    const xs = try a.alloc([]const u8, set.count());
    var it = set.keyIterator();
    var i: usize = 0;
    while (it.next()) |k| {
        xs[i] = k.*;
        i += 1;
    }
    std.mem.sort([]const u8, xs, {}, b.ordinalLess);
    return xs;
}
fn concurrent(c: Context, id: []const u8) bool {
    for (b.arr(c.profile, "concurrentCapabilityIds")) |s| if (b.eq(s.string, id)) return true;
    return false;
}
fn handle(v: Value) bool {
    return b.eq(b.kind(v), "handle");
}
fn touches(native: Value) bool {
    if (b.get(native, "handle") == .bool and b.get(native, "handle").bool) return true;
    const call = b.get(native, "call");
    if (call == .null) return false;
    if (handle(b.get(call, "returns"))) return true;
    for (b.arr(call, "arguments")) |arg| {
        const k = b.kind(arg);
        if (b.eq(k, "self")) return true;
        if ((b.eq(k, "parameter") or b.eq(k, "reference") or b.eq(k, "out")) and handle(b.get(arg, "value"))) return true;
    }
    return false;
}
pub fn prepare(c: Context) anyerror!?Failure {
    const a = c.a;
    const bindings = try b.sorted(a, b.arr(c.profile, "capabilityBindings"), "operationId");
    var bound: Set = .empty;
    for (bindings, 0..) |item, i| if (i > 0 and b.eq(b.str(item, "operationId"), b.str(bindings[i - 1], "operationId"))) return fail("DuplicateCapabilityBinding");
    for (bindings) |item| {
        const cap = c.capabilityOf(b.str(item, "operationId")) orelse return fail("UnknownCapabilityBindingOperation");
        try bound.put(a, b.str(cap, "id"), {});
    }
    const ins = try f.instructions(a, c.program);
    var targets: Set = .empty;
    var operations: Set = .empty;
    for (ins) |i| {
        for (try f.targets(a, i)) |id| try targets.put(a, id, {});
        for (try f.operations(a, i)) |id| try operations.put(a, id, {});
    }
    for (try strings(a, targets)) |id| if (b.find(c.functions(), "semanticFunctionId", id) == null) return fail("UnknownCallTarget");
    for (try strings(a, operations)) |id| if (c.operation(id) == null) return fail("UnknownCapabilityOperation");
    for (try strings(a, operations)) |id| if (c.binding(id) == null) return fail("MissingCapabilityBinding");
    const caps = try b.sorted(a, c.capabilities(), "id");
    for (caps) |cap| if (bound.contains(b.str(cap, "id"))) for (try b.sorted(a, b.arr(cap, "operations"), "id")) |op| if (c.binding(b.str(op, "id")) == null) return fail("MissingCapabilityBinding");
    for (caps) |cap| {
        var first: ?[]const u8 = null;
        for (b.arr(cap, "operations")) |op| if (c.binding(b.str(op, "id"))) |binding| {
            if (first) |kind| {
                if (!b.eq(kind, b.kind(binding))) return fail("MixedCapabilityBindingKinds");
            } else first = b.kind(binding);
        };
    }
    for (bindings) |item| {
        const op = c.operation(b.str(item, "operationId")).?;
        if (b.eq(b.kind(item), "systemConsoleWriteLine")) {
            const ps = b.arr(op, "parameters");
            if (ps.len != 1 or !b.eq(b.kind(b.get(ps[0], "type")), "primitiveString") or !b.eq(b.kind(b.get(op, "resultType")), "primitiveUnit") or b.get(op, "failureType") != .null) return fail("IncompatibleSystemConsoleWriteLineBinding");
        } else if (b.eq(b.kind(item), "native")) {
            const cap = c.capabilityOf(b.str(item, "operationId")).?;
            if (try @import("native.zig").problem(c, b.str(cap, "id"), op, b.get(item, "native"))) |reason| return .{ .message = try b.cat(a, &.{ "IncompatibleNativeBinding: ", reason }), .details = try b.object(a, &.{"operationId"}, &.{b.get(item, "operationId")}) };
        }
    }
    for (caps) |cap| for (b.arr(cap, "operations")) |op| if (b.get(op, "failureType") != .null) return fail("UnsupportedCapabilityFailure");
    var concurrent_ids: Set = .empty;
    for (b.arr(c.profile, "concurrentCapabilityIds")) |id| try concurrent_ids.put(a, id.string, {});
    for (try strings(a, concurrent_ids)) |id| {
        if (b.find(caps, "id", id)) |cap| for (b.arr(cap, "operations")) |op| {
            if (c.binding(b.str(op, "id"))) |binding| if (b.eq(b.kind(binding), "native") and touches(b.get(binding, "native"))) return fail("ConcurrentNativeHandle");
        };
    }
    for (try strings(a, concurrent_ids)) |id| if (!bound.contains(id)) return fail("InvalidConcurrentCapabilityBinding");
    var workers: Set = .empty;
    var todo: std.ArrayList([]const u8) = .empty;
    for (ins) |i| {
        const op = b.kind(i);
        if (b.eq(op, "parallelAll") or b.eq(op, "taskStart")) try todo.append(a, b.str(i, "workerFunctionId"));
    }
    while (todo.pop()) |id| {
        if (workers.contains(id)) continue;
        try workers.put(a, id, {});
        const fn_ = c.function(id);
        for (b.arr(fn_, "blocks")) |block| for (b.arr(block, "instructions")) |i| try todo.appendSlice(a, try f.targets(a, i));
    }
    for (try b.sorted(a, c.functions(), "semanticFunctionId")) |fn_| {
        const fid = b.str(fn_, "semanticFunctionId");
        if (!workers.contains(fid)) continue;
        var all: std.ArrayList(Value) = .empty;
        for (b.arr(fn_, "blocks")) |block| try all.appendSlice(a, b.arr(block, "instructions"));
        for (try b.sorted(a, all.items, "originNodeId")) |i| for (try f.operations(a, i)) |id| {
            const cap = c.capabilityOf(id).?;
            if (!concurrent(c, b.str(cap, "id"))) return .{ .code = "ConcurrentCapabilityBindingRequired", .message = "An operation reachable from a ParallelAll or TaskStart worker requires a capability binding that supports concurrent calls.", .details = try b.object(a, &.{ "functionId", "nodeId", "capabilityId", "operationId" }, &.{ b.string(fid), b.get(i, "originNodeId"), b.get(cap, "id"), b.string(id) }) };
        };
    }
    return null;
}
