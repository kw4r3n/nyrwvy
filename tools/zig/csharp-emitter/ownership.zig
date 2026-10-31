//! The backend-neutral Bytes ownership plan, using value references from Execution IR JSON.
const std = @import("std");
const b = @import("root.zig");
const Value = b.Value;
const Set = std.StringHashMapUnmanaged(void);
pub const Plan = struct { in_place: Set = .empty, escapes: std.StringHashMapUnmanaged([]const []const u8) = .empty, tail_releases: std.StringHashMapUnmanaged([]const []const u8) = .empty };
const Alias = std.StringHashMapUnmanaged([]const u8);
fn root(parent: Alias, id: []const u8) []const u8 {
    var curr = id;
    while (parent.get(curr)) |next| {
        if (b.eq(curr, next)) break;
        curr = next;
    }
    return curr;
}
fn unite(a: b.Allocator, p: *Alias, left: []const u8, right: []const u8) !void {
    const l = root(p.*, left);
    const r = root(p.*, right);
    if (!b.eq(l, r)) try p.put(a, l, r);
}
fn moved(i: Value) bool {
    return b.eq(b.kind(i), "readParameter") or b.eq(b.kind(i), "readPatternBinding");
}
fn updated(i: Value) bool {
    return b.eq(b.kind(i), "bytesSet") or b.eq(b.kind(i), "bytesAppend");
}
fn read(i: Value) bool {
    for ([_][]const u8{ "bytesLength", "bytesGet", "bytesConcat", "bytesSlice", "bytesToLatin1", "valueEqual" }) |k| if (b.eq(b.kind(i), k)) return true;
    return false;
}
fn appendUnique(a: b.Allocator, out: *std.ArrayList([]const u8), id: []const u8) !void {
    for (out.items) |v| if (b.eq(v, id)) return;
    try out.append(a, id);
}
fn valueKey(key: []const u8) bool {
    const keys = [_][]const u8{ "valueId", "sourceValueId", "leftValueId", "rightValueId", "startId", "endId", "countValueId", "digitsValueId", "amountValueId", "lengthId", "bytesId", "indexId", "listValueId", "mapValueId", "setValueId", "indexValueId", "keyValueId", "itemValueId", "leftSetValueId", "rightSetValueId", "leftMapValueId", "rightMapValueId", "optionValueId", "defaultValueId", "resultValueId", "errorValueId", "keyId", "leftListValueId", "rightListValueId", "sourceListValueId", "seedValueId", "sourceMapValueId", "sourceSetValueId", "acquireValueId", "releaseCapabilityValueId", "scopeValueId", "tasksValueId", "limitValueId", "cellValueId", "valueValueId", "taskValueId", "sourceOptionValueId", "sourceResultValueId", "functionValueId", "capabilityValueId", "inputValueId", "conditionValueId", "targetValueId", "substringId", "setId", "fromId", "leftId", "endValueId", "separatorId", "patternId", "breaksId", "suffixId", "replacementId", "rightId", "startValueId", "prefixId", "textValueId" };
    for (keys) |k| if (b.eq(k, key)) return true;
    return false;
}
fn collect(a: b.Allocator, v: Value, out: *std.ArrayList([]const u8)) anyerror!void {
    switch (v) {
        .object => |obj| {
            var it = obj.iterator();
            while (it.next()) |entry| {
                const key = entry.key_ptr.*;
                const child = entry.value_ptr.*;
                if (valueKey(key) and child == .string) try out.append(a, child.string) else if (b.eq(key, "itemValueIds") or (b.eq(key, "arguments") and b.eq(b.kind(v), "functionInvoke"))) {
                    for (child.array.items) |id| try out.append(a, id.string);
                } else if (!b.eq(key, "result") and !b.eq(key, "type")) try collect(a, child, out);
            }
        },
        .array => |list| for (list.items) |child| try collect(a, child, out),
        else => {},
    }
}
fn ids(a: b.Allocator, v: Value) ![]const []const u8 {
    var out: std.ArrayList([]const u8) = .empty;
    try collect(a, v, &out);
    return out.items;
}
fn successors(a: b.Allocator, t: Value, out: *std.ArrayList([]const u8)) !void {
    const k = b.kind(t);
    if (b.eq(k, "jump")) try out.append(a, b.str(t, "blockId")) else if (b.eq(k, "branch")) {
        try out.append(a, b.str(t, "whenTrue"));
        try out.append(a, b.str(t, "whenFalse"));
    } else if (b.eq(k, "matchOption")) {
        try out.append(a, b.str(t, "someBlock"));
        try out.append(a, b.str(t, "noneBlock"));
    } else if (b.eq(k, "matchResult")) {
        try out.append(a, b.str(t, "okBlock"));
        try out.append(a, b.str(t, "errBlock"));
    } else if (b.eq(k, "matchUnion")) for (b.arr(t, "targets")) |target| try out.append(a, b.str(target, "blockId"));
}
fn reachable(a: b.Allocator, blocks: []const Value, start: Value) !Set {
    var seen: Set = .empty;
    var todo: std.ArrayList([]const u8) = .empty;
    try successors(a, b.get(start, "terminator"), &todo);
    while (todo.pop()) |id| {
        if (seen.contains(id)) continue;
        try seen.put(a, id, {});
        try successors(a, b.get(b.find(blocks, "id", id).?, "terminator"), &todo);
    }
    return seen;
}
const Use = struct { block: []const u8, index: usize, instruction: ?[]const u8, id: []const u8 };
pub fn plan(a: b.Allocator, fn_: Value) anyerror!Plan {
    var result: Plan = .{};
    var has_bytes = false;
    for (b.arr(fn_, "parameters")) |p| {
        if (b.eq(b.kind(b.get(b.get(p, "value"), "type")), "primitiveBytes")) has_bytes = true;
    }
    for (b.arr(fn_, "blocks")) |block| {
        for (b.arr(block, "parameters")) |p| {
            if (b.eq(b.kind(b.get(p, "type")), "primitiveBytes")) has_bytes = true;
        }
        for (b.arr(block, "instructions")) |i| {
            if (b.eq(b.kind(b.get(b.get(i, "result"), "type")), "primitiveBytes")) has_bytes = true;
        }
    }
    if (!has_bytes) return result;
    const blocks = b.arr(fn_, "blocks");
    var aliases: Alias = .empty;
    var uses: std.ArrayList(Use) = .empty;
    for (blocks) |block| {
        const t = b.get(block, "terminator");
        if (b.eq(b.kind(t), "jump")) for (b.arr(t, "arguments")) |arg| try unite(a, &aliases, b.str(arg, "valueId"), b.str(arg, "targetValueId"));
        for (b.arr(block, "instructions")) |i| if (moved(i)) try unite(a, &aliases, b.str(b.get(i, "result"), "id"), b.str(i, "sourceValueId"));
    }
    for (blocks) |block| {
        const ins = b.arr(block, "instructions");
        for (ins, 0..) |i, index| {
            if (moved(i)) continue;
            for (try ids(a, i)) |id| try uses.append(a, .{ .block = b.str(block, "id"), .index = index, .instruction = b.str(b.get(i, "result"), "id"), .id = id });
        }
        const t = b.get(block, "terminator");
        if (!b.eq(b.kind(t), "jump")) for (try ids(a, t)) |id| try uses.append(a, .{ .block = b.str(block, "id"), .index = ins.len, .instruction = null, .id = id });
    }
    for (blocks) |block| {
        const reached = try reachable(a, blocks, block);
        for (b.arr(block, "instructions"), 0..) |i, index| {
            const iid = b.str(b.get(i, "result"), "id");
            if (updated(i)) {
                const alias = root(aliases, b.str(i, "bytesId"));
                var later = false;
                for (uses.items) |use| {
                    if (!b.eq(root(aliases, use.id), alias)) continue;
                    if (use.instruction) |other| if (b.eq(other, iid)) continue;
                    const same = b.eq(use.block, b.str(block, "id"));
                    if ((same and use.index > index) or (!same and reached.contains(use.block))) {
                        later = true;
                        break;
                    }
                }
                if (!later) try result.in_place.put(a, iid, {});
            } else if (!read(i) and !moved(i)) {
                var escaping: std.ArrayList([]const u8) = .empty;
                for (try ids(a, i)) |id| if (b.eq(b.kind(@import("functions.zig").valueType(fn_, id)), "primitiveBytes")) try appendUnique(a, &escaping, id);
                if (escaping.items.len > 0) try result.escapes.put(a, iid, escaping.items);
            }
        }
        const t = b.get(block, "terminator");
        if (b.eq(b.kind(t), "tailCall")) {
            var passed: std.ArrayList([]const u8) = .empty;
            for (try ids(a, t)) |id| if (b.eq(b.kind(@import("functions.zig").valueType(fn_, id)), "primitiveBytes")) try passed.append(a, id);
            var released: std.ArrayList([]const u8) = .empty;
            for (passed.items) |id| {
                var count: usize = 0;
                for (passed.items) |other| if (b.eq(root(aliases, id), root(aliases, other))) {
                    count += 1;
                };
                if (count > 1) try appendUnique(a, &released, id);
            }
            if (released.items.len > 0) try result.tail_releases.put(a, b.str(block, "id"), released.items);
        }
    }
    return result;
}
