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
