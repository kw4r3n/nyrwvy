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
