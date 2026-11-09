// Appended to runtime.zig and case_mapping.zig by scripts/test-typed-host.mjs (it is not a file of its own):
// every value the runtime allocates is released, checked by DebugAllocator.

const Node = union(enum) {
    end: struct {},
    link: struct { value: i64, next: *const Node },
    pub const ny_ids = [_][]const u8{ "a", "b" };
};
const Pair = struct { text: []const u8, items: []const []const u8 };

test "counted runtime frees everything" {
    var gpa: std.heap.DebugAllocator(.{}) = .init;
    ny_values = gpa.allocator();
    {
        const a1 = nyInt64ToString(12345);
        const b1 = nyConcat(a1, nyStatic(" x "));
        const c1 = nyConcat(b1, a1);
        nyRelease(b1);
        const parts = nyStringSplit(c1, nyStatic(" "));
        const joined = nyStringJoin(parts, nyStatic("-"));
        try std.testing.expectEqualStrings("12345-x-12345", joined);
        const pair = nyBox(Pair, .{ .text = nyShare(joined), .items = nyShare(parts) });
        nyRelease(parts);
        nyRelease(joined);
        const sorted = nySortBy([]const u8, pair.items, {}, struct {
            fn less(_: void, l: []const u8, r: []const u8) bool {
                return std.mem.lessThan(u8, l, r);
            }
        }.less);
        nyRelease(sorted);
        nyRelease(pair);
        const trimmed = nyStringTrim(nyStatic("  hi  "));
        nyRelease(trimmed);
        var set = NySet([]const u8){ .items = &.{} };
        set = nySetInsertOwned([]const u8, set, nyInt64ToString(1));
        set = nySetInsertOwned([]const u8, set, nyInt64ToString(1));
        set = nySetInsertOwned([]const u8, set, nyInt64ToString(2));
        try std.testing.expectEqual(@as(usize, 2), set.items.len);
        const kept = nySetKeep([]const u8, set, .{ .items = &.{} }, true);
        nyRelease(kept);
        var map = NyMap([]const u8, i64){ .keys = &.{}, .values = &.{} };
        map = nyMapInsertOwned([]const u8, i64, map, nyInt64ToString(1), 1);
        map = nyMapInsertOwned([]const u8, i64, map, nyInt64ToString(1), 2);
        const u = nyMapUnion([]const u8, i64, map, map);
        nyRelease(u);
        nyRelease(map);
        nyRelease(set);
        const lines = nyStringSplitLines(nyStatic("a\nb\r\nc"), nyStatic("\r\n"));
        nyRelease(lines);
        nyRelease(nyFloat64ToString(1.5));
        nyRelease(nyFloat64ToString(0));
        nyRelease(nyMapCase(c1, &ny_upper_runs));
        nyRelease(nyStringDecodeUtf8Lossy(c1));
        nyRelease(nyStringCodePoints(c1));
        nyRelease(c1);
        nyRelease(a1);
        // A long chain releases without recursion.
        var chain: *const Node = nyBox(Node, .{ .end = .{} });
        for (0..1_000_000) |i| chain = nyBox(Node, .{ .link = .{ .value = @intCast(i), .next = chain } });
        nyRelease(chain);
    }
    try std.testing.expect(gpa.deinit() == .ok);
}

/// A worker in the shape the emitter writes (main.zig `taskStart`): moved captures, `drop` for a
/// task that never starts.
const Echo = struct {
    fn call(c: anytype) NyError![]const u8 {
        if (c.text.len == 0) return nyFail("Test", "empty", false);
        return c.text;
    }
    fn drop(c: anytype) void {
        nyReleaseAll(.{c.text});
    }
};
const EchoJob = NyJob(struct { text: []const u8 }, NyError![]const u8, Echo);

/// A failing call in the shape the emitter writes: what the frame holds is released by `errdefer`
/// when the failure propagates (main.zig `blockLines`).
fn failAfterHolding(held: []const u8) NyError![]const u8 {
    var result: []const u8 = undefined;
    {
        errdefer nyRelease(held);
        result = try nyRethrow([]const u8, .{ .type_name = "Test", .message = "failed" });
    }
    nyRelease(held);
    return result;
}

test "failures, tasks and hand-off cells release their values" {
    var gpa: std.heap.DebugAllocator(.{}) = .init;
    ny_values = gpa.allocator();
    ny_io = std.testing.io;
    {
        // A caught failure leaves nothing behind.
        if (failAfterHolding(nyInt64ToString(7))) |_| return error.TestUnexpectedResult else |_| {}
        const scope = nyScopeOpen();
        // A handle dropped before its task runs: the run's own reference releases the value.
        nyRelease(nyStartTask([]const u8, scope, EchoJob{ .c = .{ .text = nyInt64ToString(1) } }));
        // A waited task: the waiter owns its copy, the record's goes with the last handle.
        const waited = nyStartTask([]const u8, scope, EchoJob{ .c = .{ .text = nyInt64ToString(2) } });
        const results = try nyTaskWait([]const u8, &.{waited}, true, null);
        try std.testing.expectEqualStrings("2", results[0].?);
        nyRelease(results);
        // A failed task holds no value.
        const failed = nyStartTask([]const u8, scope, EchoJob{ .c = .{ .text = "" } });
        try std.testing.expectError(error.NyFailure, nyScopeClose(scope, false));
        nyRelease(failed);
        // A handle that escaped its scope still reads its value; a task started after the close runs at once.
        const late = nyStartTask([]const u8, scope, EchoJob{ .c = .{ .text = nyInt64ToString(3) } });
        const again = try nyTaskWait([]const u8, &.{ waited, late }, true, null);
        nyRelease(again);
        nyRelease(waited);
        nyRelease(late);
        // A completed cell releases its value with its last handle; a closed one refuses (and releases) it.
        const open_scope = nyScopeOpen();
        const cell = nyOneShotCreate([]const u8, open_scope);
        nyRetain(cell);
        try std.testing.expect(nyOneShotComplete([]const u8, cell, nyInt64ToString(4)));
        try std.testing.expect(!nyOneShotComplete([]const u8, cell, nyInt64ToString(5)));
        nyRelease(cell);
        nyRelease(cell);
        try nyScopeClose(open_scope, false);
        // A task that never starts (the clock failed every waiter) releases its captured arguments.
        const ticket = nyAllocator().create(NyWaiter) catch unreachable;
        ticket.* = .{ .ready = nyAlways, .context = undefined, .due = null, .order = 0, .is_delay = false, .released = true, .failed = true };
        const task = nyAllocator().create(NyTask([]const u8)) catch unreachable;
        task.* = .{ .base = .{ .cancel = nyLinked(null) } };
        var job = EchoJob{ .c = .{ .text = nyInt64ToString(6) } };
        ny_running += 1; // the task thread's exit gives it back
        nyTaskThread([]const u8, EchoJob)(task, &job, ticket);
        try std.testing.expect(task.base.failed);
        nyRelease(task);
    }
    try std.testing.expect(gpa.deinit() == .ok);
}

/// A task that holds a value while it waits on a cell nobody completes; its unwinding records
/// whether another thread was unwinding at the same time.
var stuck_unwinding = std.atomic.Value(u32).init(0);
var stuck_overlapped = std.atomic.Value(bool).init(false);
const Stuck = struct {
    fn call(c: anytype) NyError![]const u8 {
        const held = nyInt64ToString(9);
        errdefer {
            if (stuck_unwinding.fetchAdd(1, .seq_cst) != 0) stuck_overlapped.store(true, .seq_cst);
            for (0..2_000_000) |_| std.atomic.spinLoopHint();
            _ = stuck_unwinding.fetchSub(1, .seq_cst);
            nyRelease(held);
        }
        _ = try nyOneShotWait([]const u8, c.cell, null);
        return held;
    }
    fn drop(_: anytype) void {}
};
const StuckJob = NyJob(struct { cell: *NyOneShot([]const u8) }, NyError![]const u8, Stuck);

test "a deadlock fails every waiter and each unwinds alone" {
    var gpa: std.heap.DebugAllocator(.{}) = .init;
    ny_values = gpa.allocator();
    ny_io = std.testing.io;
    {
        const scope = nyScopeOpen();
        const cell = nyOneShotCreate([]const u8, scope);
        const first = nyStartTask([]const u8, scope, StuckJob{ .c = .{ .cell = cell } });
        const second = nyStartTask([]const u8, scope, StuckJob{ .c = .{ .cell = cell } });
        try std.testing.expectError(error.NyFailure, nyTaskWait([]const u8, &.{ first, second }, true, null));
        // The close waits for both tasks, which the clock releases one at a time.
        nyScopeClose(scope, false) catch {};
        try std.testing.expect(first.base.failed and second.base.failed);
        try std.testing.expect(!stuck_overlapped.load(.seq_cst));
        nyRelease(first);
        nyRelease(second);
        nyRelease(cell);
    }
    try std.testing.expect(gpa.deinit() == .ok);
}

/// A list of `count` owned strings ("0", "1", ...) in storage with room for `room` items.
fn numbered(count: usize, room: usize) []const []const u8 {
    const items = nyAlloc([]const u8, room);
    for (items[0..count], 0..) |*item, i| item.* = nyInt64ToString(@intCast(i));
    return nyShrink([]const u8, items, count);
}

fn expectNumbered(list: []const []const u8, count: usize) !void {
    try std.testing.expectEqual(count, list.len);
    for (list, 0..) |item, i| try std.testing.expectEqual(@as(i64, @intCast(i)), nyStringParseInt64(item).?);
}

test "count-of-one updates reuse unique storage and copy shared storage" {
    var gpa: std.heap.DebugAllocator(.{}) = .init;
    ny_values = gpa.allocator();
    {
        const x = nyStatic("x");
        // Each update consumes its input: a unique input keeps its storage, a shared one is copied
        // and left as it was.
        inline for (.{ "append", "setAt", "slice", "concat" }) |op| {
            const unique = numbered(3, 4);
            // Full storage, so a shared append cannot claim a spare slot (stage (d)) and copies.
            const shared = numbered(3, 3);
            const other = nyShare(shared);
            const update = struct {
                fn on(list: []const []const u8, item: []const u8) []const []const u8 {
                    if (comptime std.mem.eql(u8, op, "append")) return nyListAppendOwned([]const u8, list, item);
                    if (comptime std.mem.eql(u8, op, "setAt")) return nyListSetAtOwned([]const u8, list, 1, item);
                    if (comptime std.mem.eql(u8, op, "slice")) return nyListSliceOwned([]const u8, list, 1, 3);
                    return nyListConcatOwned([]const u8, list, &.{item});
                }
            }.on;
            const reused = update(unique, x);
            try std.testing.expectEqual(unique.ptr, reused.ptr);
            const copied = update(shared, x);
            try std.testing.expect(copied.ptr != shared.ptr);
            try expectNumbered(other, 3);
            try std.testing.expectEqualDeep(reused, copied);
            if (comptime std.mem.eql(u8, op, "slice")) try std.testing.expectEqualStrings("2", reused[1]);
            nyRelease(reused);
            nyRelease(copied);
            nyRelease(other);
        }
        // A small slice of a unique list does not keep its large storage.
        const cut = nyListSliceOwned([]const u8, numbered(8, 64), 6, 7);
        try std.testing.expectEqual(@as(u32, 1), nyHeader(cut.ptr).cap);
        try std.testing.expectEqualStrings("6", cut[0]);
        nyRelease(cut);
        // Growing a unique list past its storage moves it and keeps every item.
        var grown = numbered(0, 0);
        for (0..100) |i| {
            const item = nyInt64ToString(@intCast(i));
            grown = nyListAppendOwned([]const u8, grown, item);
            nyRelease(item);
        }
        try expectNumbered(grown, 100);
        nyRelease(grown);
        // A static string is never written in place.
        const literal = nyStatic("ab");
        const joined = nyConcatOwned(literal, x);
        try std.testing.expectEqualStrings("ab", literal);
        try std.testing.expectEqualStrings("abx", joined);
        nyRelease(joined);
        const room = nyAlloc(u8, 8);
        @memcpy(room[0..2], "ab");
        const text = nyShrink(u8, room, 2);
        const longer = nyConcatOwned(text, x);
        try std.testing.expectEqual(text.ptr, longer.ptr);
        try std.testing.expectEqualStrings("abx", longer);
        nyRelease(longer);
        // Sets and Maps: a shared container is copied, a unique one extended in place.
        var set = NySet([]const u8){ .items = numbered(2, 3) };
        const set_before = set.items.ptr;
        set = nySetInsertInto([]const u8, set, x);
        try std.testing.expectEqual(set_before, set.items.ptr);
        const set_other = nyShare(set);
        set = nySetInsertInto([]const u8, set, nyStatic("y"));
        try std.testing.expect(set.items.ptr != set_other.items.ptr);
        try std.testing.expectEqual(@as(usize, 3), set_other.items.len);
        try std.testing.expectEqual(@as(usize, 4), set.items.len);
        nyRelease(set_other);
        nyRelease(set);
        var map = NyMap(i64, []const u8){ .keys = nyListOf(i64, &.{ 1, 2 }), .values = numbered(2, 2) };
        const map_other = nyShare(map);
        map = nyMapInsertInto(i64, []const u8, map, 1, x);
        try std.testing.expect(map.values.ptr != map_other.values.ptr);
        try expectNumbered(map_other.values, 2);
        nyRelease(map_other);
        const values_before = map.values.ptr;
        map = nyMapInsertInto(i64, []const u8, map, 2, x);
        try std.testing.expectEqual(values_before, map.values.ptr);
        try std.testing.expectEqualStrings("x", map.values[1]);
        nyRelease(map);
        // A Product update writes a unique box in place and only then (main.zig `productUpdate`).
        const box = nyBox(Pair, .{ .text = nyInt64ToString(1), .items = &.{} });
        try std.testing.expect(nyUnique(box));
        const box_other = nyShare(box);
        try std.testing.expect(!nyUnique(box));
        nyRelease(box_other);
        nyRelease(box);
    }
    try std.testing.expect(gpa.deinit() == .ok);
}

test "a shared list grows in place past its own length and copies otherwise" {
    var gpa: std.heap.DebugAllocator(.{}) = .init;
    ny_values = gpa.allocator();
    {
        const x = nyInt64ToString(100);
        const y = nyInt64ToString(101);
        // Appending to the longest list on the storage claims the next slot; the old list is unchanged.
        const old = numbered(3, 4);
        const first = nyListAppendOwned([]const u8, nyShare(old), x);
        try std.testing.expectEqual(old.ptr, first.ptr);
        try expectNumbered(old, 3);
        try std.testing.expectEqualStrings("100", first[3]);
        // The slot is taken, so a second append to the old list copies and leaves `first` as it was.
        const second = nyListAppendOwned([]const u8, nyShare(old), y);
        try std.testing.expect(second.ptr != old.ptr);
        try std.testing.expectEqualStrings("100", first[3]);
        try std.testing.expectEqualStrings("101", second[3]);
        nyRelease(second);
        // Once `first` dies the old list is unique again: it drops the item `first` left and reuses.
        nyRelease(first);
        const third = nyListAppendOwned([]const u8, old, y);
        try std.testing.expectEqual(old.ptr, third.ptr);
        try std.testing.expectEqualStrings("101", third[3]);
        nyRelease(third);
        // The storage's last reference releases every item it owns, past the releasing list's length.
        const short = numbered(3, 4);
        const long = nyListAppendOwned([]const u8, nyShare(short), x);
        nyRelease(long);
        nyRelease(short);
        // A shared prefix shares the storage; appending to it copies, since a longer list owns the slot.
        const whole = numbered(3, 4);
        const prefix = nyListSliceOwned([]const u8, nyShare(whole), 0, 2);
        try std.testing.expectEqual(whole.ptr, prefix.ptr);
        const extended = nyListAppendOwned([]const u8, prefix, x);
        try std.testing.expect(extended.ptr != whole.ptr);
        try expectNumbered(whole, 3);
        try std.testing.expectEqualStrings("100", extended[2]);
        nyRelease(extended);
        nyRelease(whole);
        // A shared Set or Map gains a new key in place; the old one keeps its size.
        const set = NySet([]const u8){ .items = numbered(2, 4) };
        const grown = nySetInsertInto([]const u8, nyShare(set), x);
        try std.testing.expectEqual(set.items.ptr, grown.items.ptr);
        try std.testing.expectEqual(@as(usize, 2), set.items.len);
        try std.testing.expectEqual(@as(usize, 3), grown.items.len);
        nyRelease(grown);
        nyRelease(set);
        // Strings too; spare room under 4 bytes cannot record its count and is never claimed.
        const wide = nyAlloc(u8, 16);
        @memcpy(wide[0..2], "ab");
        const text = nyShrink(u8, wide, 2);
        const text_more = nyConcatOwned(nyShare(text), nyStatic("cd"));
        try std.testing.expectEqual(text.ptr, text_more.ptr);
        try std.testing.expectEqualStrings("ab", text);
        try std.testing.expectEqualStrings("abcd", text_more);
        nyRelease(text_more);
        nyRelease(text);
        const narrow = nyAlloc(u8, 4);
        @memcpy(narrow[0..2], "ab");
        const tight = nyShrink(u8, narrow, 2);
        const tight_more = nyConcatOwned(nyShare(tight), nyStatic("c"));
        try std.testing.expect(tight_more.ptr != tight.ptr);
        try std.testing.expectEqualStrings("ab", tight);
        try std.testing.expectEqualStrings("abc", tight_more);
        nyRelease(tight_more);
        nyRelease(tight);
        // Keeping every version alive while appending: storage doubles, so 10,000 versions share
        // a handful of allocations instead of one copy each.
        const versions = try std.heap.page_allocator.alloc([]const []const u8, 10_001);
        defer std.heap.page_allocator.free(versions);
        versions[0] = numbered(1, 1);
        var storages: usize = 1;
        for (1..versions.len) |i| {
            const item = nyInt64ToString(@intCast(i));
            versions[i] = nyListAppendOwned([]const u8, nyShare(versions[i - 1]), item);
            nyRelease(item);
            if (versions[i].ptr != versions[i - 1].ptr) storages += 1;
        }
        try std.testing.expect(storages <= 16);
        for (versions, 0..) |version, i| try expectNumbered(version, i + 1);
        for (versions) |version| nyRelease(version);
        nyRelease(x);
        nyRelease(y);
    }
    try std.testing.expect(gpa.deinit() == .ok);
}

const Weighed = struct { name: []const u8, weight: f64, tags: []const i64, extra: ?i64 };

fn weighed(i: usize, weight: f64) *const Weighed {
    return nyBox(Weighed, .{ .name = nyInt64ToString(@intCast(i)), .weight = weight, .tags = nyListOf(i64, &.{ @intCast(i), 7 }), .extra = if (i % 2 == 0) null else @intCast(i) });
}

fn expectSetOf(set: NySet(i64), expected: []const i64) !void {
    try std.testing.expectEqualSlices(i64, expected, set.items);
    for (expected) |item| try std.testing.expectEqual(item, set.items[nySetFind(i64, set, item).?]);
}

test "sets and maps look up through a hash index that agrees with equality" {
    var gpa: std.heap.DebugAllocator(.{}) = .init;
    ny_values = gpa.allocator();
    {
        // Values equal under nyEqual hash equally: -0.0 and 0.0, every NaN, separately built
        // Strings, and Sets and Maps whatever their insertion order.
        try std.testing.expectEqual(nyHash(@as(f64, 0.0)), nyHash(@as(f64, -0.0)));
        try std.testing.expectEqual(nyHash(std.math.nan(f64)), nyHash(@as(f64, @bitCast(@as(u64, 0xfff0000000000001)))));
        const text = nyInt64ToString(42);
        const built = nyConcat(nyStatic("4"), nyStatic("2"));
        try std.testing.expectEqual(nyHash(text), nyHash(built));
        nyRelease(text);
        nyRelease(built);
        var up = NySet(i64){ .items = &.{} };
        var down = NySet(i64){ .items = &.{} };
        var up_map = NyMap(i64, i64){ .keys = &.{}, .values = &.{} };
        var down_map = NyMap(i64, i64){ .keys = &.{}, .values = &.{} };
        for (0..20) |i| {
            up = nySetInsertInto(i64, up, @intCast(i));
            down = nySetInsertInto(i64, down, @intCast(19 - i));
            up_map = nyMapInsertInto(i64, i64, up_map, @intCast(i), @intCast(i * 3));
            down_map = nyMapInsertInto(i64, i64, down_map, @intCast(19 - i), @intCast((19 - i) * 3));
        }
        try std.testing.expect(nyEqual(up, down) and nyHash(up) == nyHash(down));
        try std.testing.expect(nyEqual(up_map, down_map) and nyHash(up_map) == nyHash(down_map));
        try std.testing.expect(nyHash(NyResult(i64, i64){ .ok = 1 }) != nyHash(NyResult(i64, i64){ .err = 1 }));
        // Insertion order is kept, so Sets equal by content still iterate differently.
        try std.testing.expectEqual(@as(i64, 19), down.items[0]);
        nyRelease(down);
        nyRelease(down_map);
        // Up to nyIndexMin items scan; one more builds the index, at most half full as it grows.
        var set = NySet(i64){ .items = &.{} };
        for (0..nyIndexMin(i64)) |i| set = nySetInsertInto(i64, set, @intCast(i));
        try std.testing.expectEqual(@as(usize, 0), set.index.len);
        for (nyIndexMin(i64)..1000) |i| {
            set = nySetInsertInto(i64, set, @intCast(i));
            try std.testing.expect(set.index.len >= 2 * set.items.len);
        }
        // A Set of records found through the index by equal, separately built records.
        var records = NySet(*const Weighed){ .items = &.{} };
        for (0..40) |i| records = nySetInsertOwned(*const Weighed, records, weighed(i, if (i % 3 == 0) std.math.nan(f64) else 0.0));
        try std.testing.expect(records.index.len > 0);
        for (0..40) |i| {
            const probe = weighed(i, if (i % 3 == 0) -std.math.nan(f64) else -0.0);
            try std.testing.expectEqual(@as(?usize, i), nySetFind(*const Weighed, records, probe));
            nyRelease(probe);
        }
        const absent = weighed(40, 0.0);
        try std.testing.expectEqual(@as(?usize, null), nySetFind(*const Weighed, records, absent));
        nyRelease(absent);
        nyRelease(records);
        // A unique set updates its index in place; a shared one keeps its own and the update copies.
        const index_before = set.index.ptr;
        set = nySetInsertInto(i64, set, 999);
        set = nySetInsertInto(i64, set, 1000);
        try std.testing.expectEqual(index_before, set.index.ptr);
        const old = nyShare(set);
        set = nySetInsertInto(i64, set, 1001);
        try std.testing.expect(set.index.ptr != old.index.ptr);
        try std.testing.expectEqual(@as(?usize, null), nySetFind(i64, old, 1001));
        try std.testing.expectEqual(@as(?usize, 1001), nySetFind(i64, set, 1001));
        for (0..1001) |i| try std.testing.expectEqual(@as(?usize, i), nySetFind(i64, old, @intCast(i)));
        nyRelease(old);
        // Removing from a shared set compacts a copy of the items and rebuilds the index.
        const removed = nySetRemoveOwned(i64, nyShare(set), 3);
        try std.testing.expectEqual(@as(?usize, null), nySetFind(i64, removed, 3));
        for (4..1002) |i| try std.testing.expectEqual(@as(?usize, i - 1), nySetFind(i64, removed, @intCast(i)));
        try std.testing.expectEqual(@as(?usize, 3), nySetFind(i64, set, 3));
        // Union, intersection, difference, subset and equality use the index of their right set.
        const evens = nySetFromList(i64, &.{ 10, 2, 2, 4, 6, 8, 12, 14, 16, 18, 20, 2000 });
        try expectSetOf(evens, &.{ 10, 2, 4, 6, 8, 12, 14, 16, 18, 20, 2000 });
        const both = nySetKeep(i64, evens, removed, true);
        try expectSetOf(both, &.{ 10, 2, 4, 6, 8, 12, 14, 16, 18, 20 });
        const rest = nySetKeep(i64, removed, evens, false);
        try std.testing.expect(rest.index.len > 0 and nySetFind(i64, rest, 10) == null);
        try std.testing.expectEqual(@as(?usize, rest.items.len - 1), nySetFind(i64, rest, 1001));
        nyRelease(rest);
        const only = nySetKeep(i64, evens, removed, false);
        try expectSetOf(only, &.{2000});
        try std.testing.expect(nySetIsSubset(i64, both, removed) and !nySetIsSubset(i64, evens, removed));
        const joined = nySetUnion(i64, removed, evens);
        try std.testing.expectEqual(@as(i64, 2000), joined.items[joined.items.len - 1]);
        try std.testing.expectEqual(removed.items.len + 1, joined.items.len);
        const unjoined = nySetRemoveOwned(i64, nyShare(joined), 2000);
        try std.testing.expect(!nyEqual(joined, removed) and nyEqual(unjoined, removed));
        nyRelease(unjoined);
        for ([_]NySet(i64){ evens, both, only, joined, removed, set, up }) |each| nyRelease(each);
        // 20,000 String keys: every get and the insertion order, a replaced value keeps its key's
        // place and a removed key inserted again goes last.
        var map = NyMap([]const u8, i64){ .keys = &.{}, .values = &.{} };
        for (0..20_000) |i| map = nyMapInsertOwned([]const u8, i64, map, nyInt64ToString(@intCast(i)), @intCast(i));
        const key = nyInt64ToString(5);
        map = nyMapInsertInto([]const u8, i64, map, key, -5);
        try std.testing.expectEqualStrings("5", map.keys[5]);
        // A unique map removes in place: the index renumbers and closes the slot without a rebuild.
        const index_before_remove = map.index.ptr;
        const without = nyMapRemoveOwned([]const u8, i64, map, key);
        try std.testing.expectEqual(index_before_remove, without.index.ptr);
        map = nyMapInsertInto([]const u8, i64, without, key, 5);
        nyRelease(key);
        try std.testing.expectEqualStrings("5", map.keys[map.keys.len - 1]);
        for (0..20_000) |i| {
            const probe = nyInt64ToString(@intCast(i));
            try std.testing.expectEqual(@as(?i64, @intCast(i)), nyMapGet([]const u8, i64, map, probe));
            nyRelease(probe);
        }
        // MapMapValues shares the keys and their index (main.zig `mapMapValues`).
        const values = nyAlloc(i64, map.keys.len);
        for (values, map.values) |*out, value| out.* = value * 2;
        const doubled = NyMap([]const u8, i64){ .keys = nyShare(map.keys), .index = nyShare(map.index), .values = values };
        nyRelease(map);
        try std.testing.expectEqual(@as(?i64, 14), nyMapGet([]const u8, i64, doubled, nyStatic("7")));
        try std.testing.expect(nyMapFind([]const u8, i64, doubled, nyStatic("20000")) == null);
        nyRelease(doubled);
        nyRelease(up_map);
    }
    try std.testing.expect(gpa.deinit() == .ok);
}

test "Bits32 operations match the C# uint semantics" {
    var gpa: std.heap.DebugAllocator(.{}) = .init;
    ny_values = gpa.allocator();
    const max: u32 = 0xffffffff;
    try std.testing.expectEqual(max, nyBits32FromInt64(-1));
    try std.testing.expectEqual(@as(u32, 0), nyBits32FromInt64(1 << 32));
    try std.testing.expectEqual(@as(u32, 5), nyBits32FromInt64((1 << 32) + 5));
    try std.testing.expectEqual(@as(u32, 0), nyBits32FromInt64(std.math.minInt(i64)));
    try std.testing.expectEqual(max, nyBits32FromInt64(std.math.maxInt(i64)));
    try std.testing.expectEqual(@as(u32, 0x80000000), nyBits32FromInt64(0x80000000));
    try std.testing.expectEqual(@as(u32, 0x7fffffff), nyBits32FromInt64(0x7fffffff));
    try std.testing.expectEqual(@as(u32, 0x80000000), nyBits32RotateRight(1, 1));
    try std.testing.expectEqual(@as(u32, 2), nyBits32RotateRight(1, -1));
    for ([_]i64{ 0, 32, 64, std.math.minInt(i64) }) |amount| try std.testing.expectEqual(@as(u32, 0x80000001), nyBits32RotateRight(0x80000001, amount));
    try std.testing.expectEqual(@as(u32, 4), nyBits32RotateRight(1, std.math.maxInt(i64) - 1));
    for ([_]i64{ std.math.minInt(i64), -1, 0 }) |amount| {
        try std.testing.expectEqual(max, nyBits32ShiftLeft(max, amount));
        try std.testing.expectEqual(max, nyBits32ShiftRight(max, amount));
    }
    for ([_]i64{ 32, 33, 64, std.math.maxInt(i64) }) |amount| {
        try std.testing.expectEqual(@as(u32, 0), nyBits32ShiftLeft(max, amount));
        try std.testing.expectEqual(@as(u32, 0), nyBits32ShiftRight(max, amount));
    }
    try std.testing.expectEqual(@as(u32, 0x80000000), nyBits32ShiftLeft(1, 31));
    try std.testing.expectEqual(@as(u32, 1), nyBits32ShiftRight(0x80000000, 31));
    try std.testing.expectEqual(@as(u32, 1), max +% 2);
    // More items than nyIndexMin, so lookups go through nyHash, which must agree with nyEqual.
    var items: [80]u32 = undefined;
    for (&items, 0..) |*item, i| item.* = nyBits32FromInt64(@as(i64, @intCast(i % 40)) * 0x7fffffff);
    const set = nySetFromList(u32, &items);
    try std.testing.expectEqual(@as(usize, 40), set.items.len);
    for (items) |item| try std.testing.expect(nySetFind(u32, set, item) != null);
    try std.testing.expect(nySetFind(u32, set, 3) == null);
    nyRelease(set);
    try std.testing.expect(gpa.deinit() == .ok);
}

test "three waiters deadlocked at once, waited out of start order, each unwind alone" {
    var gpa: std.heap.DebugAllocator(.{}) = .init;
    ny_values = gpa.allocator();
    ny_io = std.testing.io;
    stuck_overlapped.store(false, .seq_cst);
    {
        const scope = nyScopeOpen();
        const cell = nyOneShotCreate([]const u8, scope);
        var stuck: [3]*NyTask([]const u8) = undefined;
        for (&stuck) |*task| task.* = nyStartTask([]const u8, scope, StuckJob{ .c = .{ .cell = cell } });
        try std.testing.expectError(error.NyFailure, nyTaskWait([]const u8, &.{ stuck[2], stuck[0], stuck[1] }, true, null));
        nyScopeClose(scope, false) catch {};
        for (stuck) |task| try std.testing.expect(task.base.failed);
        try std.testing.expect(!stuck_overlapped.load(.seq_cst));
        for (stuck) |task| nyRelease(task);
        nyRelease(cell);
    }
    try std.testing.expect(gpa.deinit() == .ok);
}

test "stepping a position over one String is linear, keeps its cursor past short Strings, and survives freeing it" {
    var gpa: std.heap.DebugAllocator(.{}) = .init;
    ny_values = gpa.allocator();
    {
        // 300k code points of widths 1, 2 and 4; walking from byte 0 per call would take minutes.
        const unit = "a\u{e9}\u{1F600}";
        const repeats = 100_000;
        const text = nyAlloc(u8, unit.len * repeats);
        for (0..repeats) |i| @memcpy(text[i * unit.len ..][0..unit.len], unit);
        const length: i64 = 3 * repeats;
        var position: i64 = 0;
        while (position < length) : (position += 1) {
            const point = nyStringSlice(text, position, position + 1);
            try std.testing.expectEqual(@as(usize, switch (@mod(position, 3)) {
                0 => 1,
                1 => 2,
                else => 4,
            }), point.len);
            nyRelease(point);
            // A short table read between steps must not take the cursor from `text`.
            nyRelease(nyStringSlice(nyStatic("\"\\/bfnrt"), 3, 4));
            try std.testing.expectEqual(@intFromPtr(text.ptr), ny_cursor.ptr);
            try std.testing.expectEqual(position + 1, nyStringSpan(text, nyStatic("a"), position) + @intFromBool(@mod(position, 3) != 0));
        }
        while (position > 0) : (position -= 3) try std.testing.expectEqual(position - 1, nyStringIndexOf(text, nyStatic("\u{1F600}"), position - 1).?);
        try std.testing.expectEqual(length - 1, nyStringLastIndexOfBefore(text, nyStatic("\u{1F600}"), length).?);
        nyRelease(@as([]const u8, text));
        try std.testing.expectEqual(@as(usize, 0), ny_cursor.ptr);
        // Same size, other widths: a cursor left from the freed String must not be reused.
        const other = nyAlloc(u8, unit.len * repeats);
        @memset(other, 'b');
        try std.testing.expectEqual(@as(i64, 7), nyStringIndexOf(other, nyStatic("b"), 7).?);
        nyRelease(@as([]const u8, other));
    }
    try std.testing.expect(gpa.deinit() == .ok);
}

test "consumed dropLast and Bytes updates reuse unique storage; a long String's length is recounted after a rewrite" {
    var gpa: std.heap.DebugAllocator(.{}) = .init;
    ny_values = gpa.allocator();
    {
        const list = numbered(3, 3);
        const dropped = nyListDropLastOwned([]const u8, list);
        try std.testing.expectEqual(list.ptr, dropped.ptr);
        try expectNumbered(dropped, 2);
        const kept = nyShare(dropped);
        const copied = nyListDropLastOwned([]const u8, dropped);
        try expectNumbered(kept, 2);
        try expectNumbered(copied, 1);
        nyRelease(kept);
        nyRelease(copied);
        try std.testing.expectEqual(@as(usize, 0), nyListDropLastOwned(u8, &.{}).len);

        const bytes = nyBytesFill(4, 1);
        const set = nyBytesSet(bytes, 1, 7);
        try std.testing.expectEqual(bytes.data.ptr, set.data.ptr);
        try std.testing.expectEqualSlices(u8, &.{ 1, 7, 1, 1 }, set.data);
        nyRelease(set);

        const text = nyAlloc(u8, 300);
        @memset(text, 'a');
        try std.testing.expectEqual(@as(i64, 300), nyStringLength(text));
        // Rewritten in place to the same address and byte length with one code point fewer.
        const rewritten = nyConcatOwned(nyListSliceOwned(u8, text, 2, 300), nyStatic("\u{e9}"));
        try std.testing.expectEqual(@intFromPtr(text.ptr), @intFromPtr(rewritten.ptr));
        try std.testing.expectEqual(@as(usize, 300), rewritten.len);
        try std.testing.expectEqual(@as(i64, 299), nyStringLength(rewritten));
        nyRelease(rewritten);
        try std.testing.expectEqual(@as(usize, 0), ny_length.ptr);
    }
    try std.testing.expect(gpa.deinit() == .ok);
}

test "a long String measured after each append counts only the new tail, never bytes rewritten under it" {
    var gpa: std.heap.DebugAllocator(.{}) = .init;
    ny_values = gpa.allocator();
    {
        const start = nyAlloc(u8, 300);
        @memset(start, 'x');
        var text: []const u8 = start;
        var expected: i64 = 300;
        for (0..40) |_| {
            text = nyConcatOwned(text, nyStatic("\u{e9}\u{1F600}ab"));
            expected += 4;
            try std.testing.expectEqual(expected, nyStringLength(text));
        }
        try std.testing.expectEqual(@intFromPtr(text.ptr), ny_length.ptr);

        // A longer String claimed the spare bytes, was measured and died; the survivor overwrites them
        // with fewer code points at the same address and byte length.
        const longer = nyConcatOwned(nyShare(text), nyStatic("abc"));
        try std.testing.expectEqual(text.ptr, longer.ptr);
        try std.testing.expectEqual(expected + 3, nyStringLength(longer));
        nyRelease(longer);
        text = nyConcatOwned(text, nyStatic("\u{e9}a"));
        try std.testing.expectEqual(text.ptr, longer.ptr);
        try std.testing.expectEqual(longer.len, text.len);
        try std.testing.expectEqual(expected + 2, nyStringLength(text));

        // Bytes rewritten in place by setAt and insertAt: one code point more each time.
        text = nyListSetAtOwned(u8, nyListSetAtOwned(u8, text, 300, 'a'), 301, 'b');
        try std.testing.expectEqual(expected + 3, nyStringLength(text));
        // Ending in a multi-byte code point, so counting only the shifted last byte would be wrong.
        text = nyConcatOwned(text, nyStatic("\u{e9}"));
        try std.testing.expectEqual(expected + 4, nyStringLength(text));
        text = nyListInsertAtOwned(u8, text, 0, 'c');
        try std.testing.expectEqual(expected + 5, nyStringLength(text));
        nyRelease(text);
    }
    try std.testing.expect(gpa.deinit() == .ok);
}

test "removing from a unique set or list closes the gap in place and keeps every lookup" {
    var gpa: std.heap.DebugAllocator(.{}) = .init;
    ny_values = gpa.allocator();
    {
        // 2000 values in a scattered insertion order (7 is coprime to 2000), so removals hit
        // probe clusters in every position.
        var set: NySet(i64) = .{ .items = &.{} };
        for (0..2000) |i| set = nySetInsertInto(i64, set, @intCast(i * 7 % 2000));
        const items_ptr = set.items.ptr;
        const index_ptr = set.index.ptr;
        var value: i64 = 0;
        while (value < 2000) : (value += 3) set = nySetRemoveOwned(i64, set, value);
        try std.testing.expectEqual(items_ptr, set.items.ptr);
        try std.testing.expectEqual(index_ptr, set.index.ptr);
        var position: usize = 0;
        for (0..2000) |i| {
            const item: i64 = @intCast(i * 7 % 2000);
            if (@mod(item, 3) == 0) {
                try std.testing.expectEqual(@as(?usize, null), nySetFind(i64, set, item));
                continue;
            }
            try std.testing.expectEqual(item, set.items[position]);
            try std.testing.expectEqual(@as(?usize, position), nySetFind(i64, set, item));
            position += 1;
        }
        try std.testing.expectEqual(position, set.items.len);
        // Removing the last item needs no renumbering.
        const last = set.items[set.items.len - 1];
        set = nySetRemoveOwned(i64, set, last);
        try std.testing.expectEqual(@as(?usize, null), nySetFind(i64, set, last));
        for (set.items, 0..) |item, at| try std.testing.expectEqual(@as(?usize, at), nySetFind(i64, set, item));
        nyRelease(set);

        // A Set, Map or Bytes field moved out leaves empty slices that release as a no-op.
        const Fields = struct { set: NySet(i64), map: NyMap(i64, i64), bytes: NyBytes };
        const box = nyBox(Fields, .{ .set = nySetFromList(i64, &.{ 1, 2 }), .map = nyMapInsertOwned(i64, i64, .{ .keys = &.{}, .values = &.{} }, 1, 2), .bytes = nyBytesFill(3, 9) });
        const moved_set = nyTakeField(&@constCast(box).set);
        const moved_map = nyTakeField(&@constCast(box).map);
        const moved_bytes = nyTakeField(&@constCast(box).bytes);
        try std.testing.expectEqual(@as(usize, 0), box.set.items.len + box.map.keys.len + box.map.values.len + box.bytes.data.len);
        try std.testing.expectEqual(@as(usize, 2), moved_set.items.len);
        try std.testing.expectEqual(@as(?i64, 2), nyMapGet(i64, i64, moved_map, 1));
        try std.testing.expectEqual(@as(usize, 3), moved_bytes.data.len);
        nyRelease(box);
        nyRelease(moved_set);
        nyRelease(moved_map);
        nyRelease(moved_bytes);

        var list = numbered(3, 4);
        const list_ptr = list.ptr;
        list = nyListInsertAtOwned([]const u8, list, 1, nyStatic("x"));
        try std.testing.expectEqual(list_ptr, list.ptr);
        try std.testing.expectEqualStrings("x", list[1]);
        list = nyListRemoveAtOwned([]const u8, list, 1);
        try std.testing.expectEqual(list_ptr, list.ptr);
        try expectNumbered(list, 3);
        // A shared list with a spare slot (3 items in room for 4) is copied, not shifted under its holders.
        const held = nyShare(list);
        const inserted = nyListInsertAtOwned([]const u8, nyShare(list), 0, nyStatic("y"));
        try std.testing.expect(inserted.ptr != held.ptr);
        try expectNumbered(held, 3);
        try std.testing.expectEqualStrings("y", inserted[0]);
        nyRelease(inserted);
        nyRelease(held);
        const shared = nyShare(list);
        const removed = nyListRemoveAtOwned([]const u8, list, 0);
        try std.testing.expect(removed.ptr != shared.ptr);
        try expectNumbered(shared, 3);
        try std.testing.expectEqualStrings("1", removed[0]);
        nyRelease(removed);
        nyRelease(shared);
    }
    try std.testing.expect(gpa.deinit() == .ok);
}
