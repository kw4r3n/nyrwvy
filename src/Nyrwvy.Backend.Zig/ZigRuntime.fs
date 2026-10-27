namespace Nyrwvy.Core

/// Zig source the backend emits with every program, and the support a runPure runner adds.
[<RequireQualifiedAccess>]
module ZigRuntime =
    let runtime = """const std = @import("std");

const NyUnit = struct {};

/// A host failure: a per-task abort with a kind (`cancelled` or a fault) and a message. A function
/// that may fail returns `NyError!T` and leaves the failure in `ny_failure`; allocation failure and
/// stack overflow are fatal panics instead.
const NyFailure = struct { type_name: []const u8, message: []const u8, exhausted: bool = false, cancelled: bool = false };
const NyError = error{NyFailure};
threadlocal var ny_failure: NyFailure = undefined;

fn nyRethrow(comptime T: type, failure: NyFailure) NyError!T {
    ny_failure = failure;
    return error.NyFailure;
}

fn nyFail(exception_type: []const u8, message: []const u8, exhausted: bool) NyError {
    ny_failure = .{ .type_name = exception_type, .message = message, .exhausted = exhausted };
    return error.NyFailure;
}

fn NyPayload(comptime T: type) type {
    return switch (@typeInfo(T)) {
        .error_union => |info| info.payload,
        else => T,
    };
}

fn NyResult(comptime Ok: type, comptime Err: type) type {
    return union(enum) { ok: Ok, err: Err };
}

// shortcut: every String and boxed value lives in one arena for the life of the process; replace
// with reference counting (plan: Zig backend notes) before long-running programs use this backend.
var ny_arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);

fn nyAllocator() std.mem.Allocator {
    return ny_arena.allocator();
}

fn nyBox(comptime T: type, value: T) *const T {
    const slot = nyAllocator().create(T) catch @panic("out of memory");
    slot.* = value;
    return slot;
}

fn nyFloorDivide(dividend: i64, divisor: i64) i64 {
    if (divisor == 0) return 0;
    if (divisor == -1) return 0 -% dividend;
    return @divFloor(dividend, divisor);
}

fn nyFloorModulo(dividend: i64, divisor: i64) i64 {
    if (divisor == 0) return dividend;
    if (divisor == -1) return 0;
    const remainder = @rem(dividend, divisor);
    return if (remainder != 0 and (remainder < 0) != (divisor < 0)) remainder + divisor else remainder;
}

fn nyShiftLeft(value: i64, count: i64) i64 {
    if (count <= 0) return value;
    if (count >= 64) return 0;
    return value << @as(u6, @intCast(count));
}

fn nyShiftRight(value: i64, count: i64) i64 {
    if (count <= 0) return value;
    if (count >= 64) return if (value < 0) -1 else 0;
    return value >> @as(u6, @intCast(count));
}

fn nyInt64ToString(value: i64) []const u8 {
    return std.fmt.allocPrint(nyAllocator(), "{d}", .{value}) catch @panic("out of memory");
}

/// Strings are UTF-8 sequences of Unicode scalar values, so this never fails.
fn nyStringLength(text: []const u8) i64 {
    return @intCast(std.unicode.utf8CountCodepoints(text) catch unreachable);
}

fn nyConcat(left: []const u8, right: []const u8) []const u8 {
    if (left.len == 0) return right;
    if (right.len == 0) return left;
    const joined = nyAllocator().alloc(u8, left.len + right.len) catch @panic("out of memory");
    @memcpy(joined[0..left.len], left);
    @memcpy(joined[left.len..], right);
    return joined;
}

// Concurrency follows the C# runtime under its deterministic virtual clock: one thread runs at a time;
// when it blocks or finishes, the next resumes: an elapsed delay by (deadline, start order), else a
// thread that can continue (new, ready, or timed out) by start order, else time jumps to the earliest
// deadline. With nothing able to resume, every waiter fails as the C# clock's waiters do.
var ny_io: std.Io = undefined;
var ny_gate: std.Io.Mutex = .init;
var ny_turn: std.Io.Condition = .init;
var ny_running: usize = 1;
var ny_next_order: u64 = 1;
var ny_now: i64 = 0;
var ny_waiters: std.ArrayList(*NyWaiter) = .empty;
threadlocal var ny_order: u64 = 0;

const NyWaiter = struct {
    ready: *const fn (*const anyopaque) bool,
    context: *const anyopaque,
    due: ?i64,
    order: u64,
    is_delay: bool,
    released: bool = false,
    failed: bool = false,
};

fn nyAlways(_: *const anyopaque) bool {
    return true;
}

fn nyNever(_: *const anyopaque) bool {
    return false;
}

fn nyDeadline(millis: i64) i64 {
    const span = @max(millis, 0);
    return if (span >= std.math.maxInt(i64) - ny_now) std.math.maxInt(i64) else ny_now + span;
}

/// The waiter with the earliest deadline among those `matches` accepts; delays first, then start order.
fn nyEarliest(comptime matches: fn (*NyWaiter) bool) ?*NyWaiter {
    var earliest: ?*NyWaiter = null;
    for (ny_waiters.items) |waiter| {
        if (!matches(waiter)) continue;
        if (earliest) |best| {
            const key = .{ waiter.due.?, @as(u8, if (waiter.is_delay) 0 else 1), waiter.order };
            const other = .{ best.due.?, @as(u8, if (best.is_delay) 0 else 1), best.order };
            if (key[0] < other[0] or (key[0] == other[0] and (key[1] < other[1] or (key[1] == other[1] and key[2] < other[2])))) earliest = waiter;
        } else earliest = waiter;
    }
    return earliest;
}

fn nyElapsedDelay(waiter: *NyWaiter) bool {
    return waiter.is_delay and waiter.due.? <= ny_now;
}

fn nyHasDue(waiter: *NyWaiter) bool {
    return waiter.due != null;
}

fn nySchedule() void {
    if (ny_running > 0 or ny_waiters.items.len == 0) return;
    var next = nyEarliest(nyElapsedDelay);
    if (next == null) {
        for (ny_waiters.items) |waiter| {
            if ((waiter.ready(waiter.context) or (waiter.due != null and waiter.due.? <= ny_now)) and (next == null or waiter.order < next.?.order)) next = waiter;
        }
    }
    if (next == null) {
        next = nyEarliest(nyHasDue);
        if (next == null) {
            // Nothing can ever resume: fail every waiter instead of hanging.
            for (ny_waiters.items) |waiter| {
                waiter.failed = true;
                waiter.released = true;
                ny_running += 1;
            }
            ny_waiters.clearRetainingCapacity();
            ny_turn.broadcast(ny_io);
            return;
        }
        ny_now = next.?.due.?;
    }
    for (ny_waiters.items, 0..) |waiter, index| {
        if (waiter == next.?) {
            _ = ny_waiters.orderedRemove(index);
            break;
        }
    }
    next.?.released = true;
    ny_running += 1;
    ny_turn.broadcast(ny_io);
}

const ny_deadlock: NyFailure = .{ .type_name = "System.InvalidOperationException", .message = "Every task is blocked and no delay is pending, so the program cannot continue." };

fn nyDeadlock() NyError {
    ny_failure = ny_deadlock;
    return error.NyFailure;
}

/// Blocks until `ready` holds or `millis` of virtual time have passed; true when ready.
fn nyBlock(ready: *const fn (*const anyopaque) bool, context: *const anyopaque, millis: ?i64, is_delay: bool) NyError!bool {
    ny_gate.lockUncancelable(ny_io);
    const waiter = nyAllocator().create(NyWaiter) catch @panic("out of memory");
    waiter.* = .{ .ready = ready, .context = context, .due = if (millis) |value| nyDeadline(value) else null, .order = ny_order, .is_delay = is_delay };
    ny_waiters.append(nyAllocator(), waiter) catch @panic("out of memory");
    ny_running -= 1;
    nySchedule();
    while (!waiter.released) ny_turn.waitUncancelable(ny_io, &ny_gate);
    ny_gate.unlock(ny_io);
    if (waiter.failed) return nyDeadlock();
    return ready(context);
}

/// Registers a task that has not started; it runs once scheduled.
fn nyEnter() *NyWaiter {
    ny_gate.lockUncancelable(ny_io);
    defer ny_gate.unlock(ny_io);
    const waiter = nyAllocator().create(NyWaiter) catch @panic("out of memory");
    waiter.* = .{ .ready = nyAlways, .context = undefined, .due = null, .order = ny_next_order, .is_delay = false };
    ny_next_order += 1;
    ny_waiters.append(nyAllocator(), waiter) catch @panic("out of memory");
    return waiter;
}

/// Called first on a task's own thread: waits for its turn and takes on its start order.
fn nyStart(ticket: *NyWaiter) bool {
    ny_order = ticket.order;
    ny_gate.lockUncancelable(ny_io);
    while (!ticket.released) ny_turn.waitUncancelable(ny_io, &ny_gate);
    ny_gate.unlock(ny_io);
    return !ticket.failed;
}

fn nyExit() void {
    ny_gate.lockUncancelable(ny_io);
    ny_running -= 1;
    nySchedule();
    ny_gate.unlock(ny_io);
}

/// A cancellation source. Cancelling flags it, closes the hand-offs of a scope it belongs to, and
/// cancels the sources linked to it, synchronously. `null` is the token that is never cancelled.
const NyCancel = struct {
    cancelled: bool = false,
    children: std.ArrayList(*NyCancel) = .empty,
    scope: ?*NyScope = null,

    fn cancel(self: *NyCancel) void {
        if (self.cancelled) return;
        self.cancelled = true;
        if (self.scope) |scope| scope.closeHandoffs();
        for (self.children.items) |child| child.cancel();
    }
};

threadlocal var ny_token: ?*NyCancel = null;

fn nyLinked(parent: ?*NyCancel) *NyCancel {
    const source = nyAllocator().create(NyCancel) catch @panic("out of memory");
    source.* = .{};
    if (parent) |owner| {
        owner.children.append(nyAllocator(), source) catch @panic("out of memory");
        if (owner.cancelled) source.cancel();
    }
    return source;
}

fn nyCancellationRequested() bool {
    return if (ny_token) |token| token.cancelled else false;
}

const NyTaskBase = struct {
    done: bool = false,
    failed: bool = false,
    failure: NyFailure = undefined,
    cancel: *NyCancel,
};

fn NyTask(comptime W: type) type {
    return struct { base: NyTaskBase, value: W = undefined };
}

const NyOneShotState = enum { pending, completed, closed };

const NyScope = struct {
    cancel: *NyCancel,
    tasks: std.ArrayList(*NyTaskBase) = .empty,
    handoffs: std.ArrayList(*NyOneShotState) = .empty,
    closed: bool = false,
    handoffs_closed: bool = false,

    fn closeHandoffs(self: *NyScope) void {
        if (self.handoffs_closed) return;
        self.handoffs_closed = true;
        for (self.handoffs.items) |state| {
            if (state.* == .pending) state.* = .closed;
        }
    }
};

fn nyScopeOpen() *NyScope {
    const scope = nyAllocator().create(NyScope) catch @panic("out of memory");
    scope.* = .{ .cancel = nyLinked(ny_token) };
    scope.cancel.scope = scope;
    if (scope.cancel.cancelled) scope.closeHandoffs();
    return scope;
}

fn nyAllDone(context: *const anyopaque) bool {
    const tasks: *const []const *NyTaskBase = @ptrCast(@alignCast(context));
    for (tasks.*) |task| if (!task.done) return false;
    return true;
}

fn nyAnyDone(context: *const anyopaque) bool {
    const tasks: *const []const *NyTaskBase = @ptrCast(@alignCast(context));
    for (tasks.*) |task| if (task.done) return true;
    return false;
}

fn nyWaitAll(tasks: []const *NyTaskBase) NyError!void {
    if (tasks.len == 0) return;
    const held = nyAllocator().create([]const *NyTaskBase) catch @panic("out of memory");
    held.* = tasks;
    _ = try nyBlock(nyAllDone, @ptrCast(held), null, false);
}

/// Closes a scope on its body's exit: closes hand-offs, cancels and joins its tasks, and, when the
/// body itself did not fail, fails with the earliest-started failed task's failure.
fn nyScopeClose(scope: *NyScope, body_failed: bool) NyError!void {
    scope.closeHandoffs();
    scope.closed = true;
    const tasks = nyListOf(*NyTaskBase, scope.tasks.items);
    scope.cancel.cancel();
    try nyWaitAll(tasks);
    if (body_failed) return;
    for (tasks) |task| {
        if (task.failed) {
            ny_failure = task.failure;
            return error.NyFailure;
        }
    }
}

/// Starts `job.run()` as a task of `scope` (or runs it at once, cancelled, when the scope is closed).
fn nyStartTask(comptime W: type, scope: *NyScope, job: anytype) *NyTask(W) {
    const task = nyAllocator().create(NyTask(W)) catch @panic("out of memory");
    task.* = .{ .base = .{ .cancel = nyLinked(scope.cancel) } };
    const Job = @TypeOf(job);
    const held = nyAllocator().create(Job) catch @panic("out of memory");
    held.* = job;
    if (scope.closed) {
        task.base.cancel.cancel();
        const saved = ny_token;
        ny_token = task.base.cancel;
        nyRunJob(W, task, held);
        ny_token = saved;
        return task;
    }
    scope.tasks.append(nyAllocator(), &task.base) catch @panic("out of memory");
    const ticket = nyEnter();
    const thread = std.Thread.spawn(.{}, nyTaskThread(W, Job), .{ task, held, ticket }) catch @panic("cannot start a task");
    thread.detach();
    return task;
}

fn nyRunJob(comptime W: type, task: *NyTask(W), job: anytype) void {
    const outcome = job.run();
    if (@typeInfo(@TypeOf(outcome)) == .error_union) {
        if (outcome) |value| task.value = value else |_| {
            task.base.failed = true;
            task.base.failure = ny_failure;
        }
    } else task.value = outcome;
    task.base.done = true;
}

fn nyTaskThread(comptime W: type, comptime Job: type) fn (*NyTask(W), *Job, *NyWaiter) void {
    return struct {
        fn run(task: *NyTask(W), job: *Job, ticket: *NyWaiter) void {
            if (!nyStart(ticket)) {
                // The clock failed every waiter: the task fails before it starts.
                task.base.failed = true;
                task.base.failure = ny_deadlock;
                task.base.done = true;
            } else {
                ny_token = task.base.cancel;
                nyRunJob(W, task, job);
            }
            nyExit();
        }
    }.run;
}

const NyTaskWaitContext = struct { tasks: []const *NyTaskBase };

/// The listed tasks' results in input order, `null` for one still running, after waiting for any or
/// all of them (or `limit` of virtual time). A failed finished task fails the wait.
fn nyTaskWait(comptime W: type, tasks: []const *NyTask(W), all: bool, limit: ?i64) NyError![]const ?W {
    if (tasks.len == 0) return &.{};
    const bases = nyAlloc(*NyTaskBase, tasks.len);
    for (tasks, 0..) |task, index| bases[index] = &task.base;
    const held = nyAllocator().create([]const *NyTaskBase) catch @panic("out of memory");
    held.* = bases;
    _ = try nyBlock(if (all) nyAllDone else nyAnyDone, @ptrCast(held), if (limit) |value| @max(value, 0) else null, false);
    const out = nyAlloc(?W, tasks.len);
    for (tasks, 0..) |task, index| {
        if (task.base.done and task.base.failed) {
            ny_failure = task.base.failure;
            return error.NyFailure;
        }
        out[index] = if (task.base.done) task.value else null;
    }
    return out;
}

fn NyOneShot(comptime T: type) type {
    return struct { state: NyOneShotState, value: T = undefined, scope: *NyScope };
}

fn nyOneShotCreate(comptime T: type, scope: *NyScope) *NyOneShot(T) {
    const cell = nyAllocator().create(NyOneShot(T)) catch @panic("out of memory");
    cell.* = .{ .state = if (scope.handoffs_closed) .closed else .pending, .scope = scope };
    scope.handoffs.append(nyAllocator(), &cell.state) catch @panic("out of memory");
    return cell;
}

fn nyOneShotComplete(comptime T: type, cell: *NyOneShot(T), value: T) bool {
    if (cell.state != .pending or cell.scope.handoffs_closed) return false;
    cell.value = value;
    cell.state = .completed;
    return true;
}

fn NyOneShotWaitContext(comptime T: type) type {
    return struct {
        cell: *NyOneShot(T),
        token: ?*NyCancel,

        fn ready(context: *const anyopaque) bool {
            const self: *const @This() = @ptrCast(@alignCast(context));
            return self.cell.state != .pending or (if (self.token) |token| token.cancelled else false);
        }
    };
}

fn nyOneShotWait(comptime T: type, cell: *NyOneShot(T), limit: ?i64) NyError!?T {
    const context = nyAllocator().create(NyOneShotWaitContext(T)) catch @panic("out of memory");
    context.* = .{ .cell = cell, .token = ny_token };
    _ = try nyBlock(NyOneShotWaitContext(T).ready, @ptrCast(context), limit, false);
    return if (cell.state == .completed) cell.value else null;
}

fn nyCancelledContext(context: *const anyopaque) bool {
    const token: *const ?*NyCancel = @ptrCast(@alignCast(context));
    return if (token.*) |source| source.cancelled else false;
}

/// A fake's await-cancellation response: blocks until the caller's token is cancelled.
fn nyAwaitCancellation() NyError!void {
    const held = nyAllocator().create(?*NyCancel) catch @panic("out of memory");
    held.* = ny_token;
    _ = try nyBlock(nyCancelledContext, @ptrCast(held), null, false);
}

/// ParallelAll: runs `worker(item)` for every item as tasks sharing one cancellation source linked to
/// the caller's; a worker's Err or failure cancels the others. The first failure by index fails the
/// call, else the lowest-index Err is the result, else the Ok values in index order.
/// A task's work: `body` applied to the values the starting instruction captured.
fn NyJob(comptime C: type, comptime R: type, comptime body: anytype) type {
    return struct {
        c: C,

        fn run(job: *const @This()) R {
            return body(job.c);
        }
    };
}

fn nyParallelAll(comptime T: type, comptime U: type, comptime E: type, items: []const T, context: anytype, comptime worker: anytype) NyError!NyResult([]const U, E) {
    const Outcome = NyResult(U, E);
    const shared = nyLinked(ny_token);
    const tasks = nyAlloc(*NyTask(Outcome), items.len);
    const bases = nyAlloc(*NyTaskBase, items.len);
    const Job = struct {
        item: T,
        context: @TypeOf(context),
        shared: *NyCancel,

        fn run(job: *const @This()) NyError!Outcome {
            const outcome = worker(job.context, job.item) catch |failure| {
                job.shared.cancel();
                return failure;
            };
            if (outcome == .err) job.shared.cancel();
            return outcome;
        }
    };
    for (items, 0..) |item, index| {
        const task = nyAllocator().create(NyTask(Outcome)) catch @panic("out of memory");
        task.* = .{ .base = .{ .cancel = shared } };
        const job = nyAllocator().create(Job) catch @panic("out of memory");
        job.* = .{ .item = item, .context = context, .shared = shared };
        tasks[index] = task;
        bases[index] = &task.base;
        const ticket = nyEnter();
        const thread = std.Thread.spawn(.{}, nyTaskThread(Outcome, Job), .{ task, job, ticket }) catch @panic("cannot start a task");
        thread.detach();
    }
    try nyWaitAll(bases);
    for (tasks) |task| {
        if (task.base.failed) {
            ny_failure = task.base.failure;
            return error.NyFailure;
        }
    }
    const values = nyAlloc(U, items.len);
    for (tasks, 0..) |task, index| {
        switch (task.value) {
            .ok => |value| values[index] = value,
            .err => |failure| return .{ .err = failure },
        }
    }
    return .{ .ok = values };
}

// Lists are immutable slices. shortcut: every update copies (O(n)); a persistent vector, as the C#
// backend uses, replaces this when a consumer needs the documented O(log n) updates.
fn nyAlloc(comptime T: type, count: usize) []T {
    return nyAllocator().alloc(T, count) catch @panic("out of memory");
}

fn nyListOf(comptime T: type, items: []const T) []const T {
    const copy = nyAlloc(T, items.len);
    @memcpy(copy, items);
    return copy;
}

fn nyIndex(index: i64, length: usize) ?usize {
    return if (index >= 0 and index < @as(i64, @intCast(length))) @intCast(index) else null;
}

fn nyClamp(bound: i64, length: usize) usize {
    if (bound <= 0) return 0;
    return @min(@as(usize, @intCast(bound)), length);
}

fn nyListRange(start: i64, end: i64) []const i64 {
    if (end <= start) return &.{};
    const count: u64 = @bitCast(end -% start);
    if (count > std.math.maxInt(u32)) @panic("out of memory");
    const items = nyAlloc(i64, @intCast(count));
    for (items, 0..) |*item, offset| item.* = start +% @as(i64, @intCast(offset));
    return items;
}

fn nyListAppend(comptime T: type, list: []const T, item: T) []const T {
    const items = nyAlloc(T, list.len + 1);
    @memcpy(items[0..list.len], list);
    items[list.len] = item;
    return items;
}

fn nyListConcat(comptime T: type, left: []const T, right: []const T) []const T {
    if (left.len == 0) return right;
    if (right.len == 0) return left;
    const items = nyAlloc(T, left.len + right.len);
    @memcpy(items[0..left.len], left);
    @memcpy(items[left.len..], right);
    return items;
}

fn nyListReverse(comptime T: type, list: []const T) []const T {
    const items = nyAlloc(T, list.len);
    for (list, 0..) |item, position| items[list.len - 1 - position] = item;
    return items;
}

fn nyListItemAt(comptime T: type, list: []const T, index: i64) ?T {
    return if (nyIndex(index, list.len)) |at| list[at] else null;
}

fn nyListLast(comptime T: type, list: []const T) ?T {
    return if (list.len == 0) null else list[list.len - 1];
}

fn nyListDropLast(comptime T: type, list: []const T) []const T {
    return if (list.len == 0) list else list[0 .. list.len - 1];
}

fn nyListSlice(comptime T: type, list: []const T, start: i64, end: i64) []const T {
    const from = nyClamp(start, list.len);
    const to = nyClamp(end, list.len);
    return if (to <= from) &.{} else list[from..to];
}

fn nyListSetAt(comptime T: type, list: []const T, index: i64, item: T) []const T {
    const at = nyIndex(index, list.len) orelse return list;
    const items = nyAlloc(T, list.len);
    @memcpy(items, list);
    items[at] = item;
    return items;
}

fn nyListInsertAt(comptime T: type, list: []const T, index: i64, item: T) []const T {
    const at = nyClamp(index, list.len);
    const items = nyAlloc(T, list.len + 1);
    @memcpy(items[0..at], list[0..at]);
    items[at] = item;
    @memcpy(items[at + 1 ..], list[at..]);
    return items;
}

fn nyListRemoveAt(comptime T: type, list: []const T, index: i64) []const T {
    const at = nyIndex(index, list.len) orelse return list;
    const items = nyAlloc(T, list.len - 1);
    @memcpy(items[0..at], list[0..at]);
    @memcpy(items[at..], list[at + 1 ..]);
    return items;
}

fn nyListIndexOf(comptime T: type, list: []const T, item: T) ?i64 {
    for (list, 0..) |candidate, position| if (nyEqual(candidate, item)) return @intCast(position);
    return null;
}

fn nyListLastIndexOf(comptime T: type, list: []const T, item: T) ?i64 {
    var position = list.len;
    while (position > 0) {
        position -= 1;
        if (nyEqual(list[position], item)) return @intCast(position);
    }
    return null;
}

// Float64 follows IEEE 754 binary64; Min and Max are IEEE 754-2019 minimum and maximum.
fn nyFloat64Min(left: f64, right: f64) f64 {
    if (std.math.isNan(left) or std.math.isNan(right)) return std.math.nan(f64);
    if (left == right) return if (std.math.signbit(left)) left else right;
    return if (left < right) left else right;
}

fn nyFloat64Max(left: f64, right: f64) f64 {
    if (std.math.isNan(left) or std.math.isNan(right)) return std.math.nan(f64);
    if (left == right) return if (std.math.signbit(left)) right else left;
    return if (left > right) left else right;
}

/// Python's float %: the truncated remainder moved by one divisor when it is nonzero and signed
/// unlike the divisor, and a zero remainder signed like the divisor.
fn nyFloat64FloorModulo(dividend: f64, divisor: f64) f64 {
    var remainder = @rem(dividend, divisor);
    if (remainder != 0) {
        if ((divisor < 0) != (remainder < 0)) remainder += divisor;
    } else remainder = std.math.copysign(@as(f64, 0), divisor);
    return remainder;
}

fn nyFloat64ToInt64(integral: f64) ?i64 {
    return if (integral >= -9223372036854775808.0 and integral < 9223372036854775808.0) @intFromFloat(integral) else null;
}

// Sets and Maps are insertion-ordered immutable values. shortcut: lookups are linear and updates
// copy; a hashed persistent structure replaces this when a consumer needs the documented O(log n).
fn NySet(comptime T: type) type {
    return struct {
        items: []const T,
        pub const ny_set = true;
    };
}

fn NyMap(comptime K: type, comptime V: type) type {
    return struct {
        keys: []const K,
        values: []const V,
        pub const ny_map = true;
    };
}

fn nyFind(comptime T: type, items: []const T, item: T) ?usize {
    for (items, 0..) |candidate, position| if (nyEqual(candidate, item)) return position;
    return null;
}

fn nySetInsert(comptime T: type, set: NySet(T), item: T) NySet(T) {
    if (nyFind(T, set.items, item) != null) return set;
    return .{ .items = nyListAppend(T, set.items, item) };
}

fn nySetRemove(comptime T: type, set: NySet(T), item: T) NySet(T) {
    const at = nyFind(T, set.items, item) orelse return set;
    return .{ .items = nyListRemoveAt(T, set.items, @intCast(at)) };
}

fn nySetFromList(comptime T: type, items: []const T) NySet(T) {
    var set = NySet(T){ .items = &.{} };
    for (items) |item| set = nySetInsert(T, set, item);
    return set;
}

fn nySetUnion(comptime T: type, left: NySet(T), right: NySet(T)) NySet(T) {
    var set = left;
    for (right.items) |item| set = nySetInsert(T, set, item);
    return set;
}

fn nySetKeep(comptime T: type, left: NySet(T), right: NySet(T), present: bool) NySet(T) {
    const out = nyAlloc(T, left.items.len);
    var count: usize = 0;
    for (left.items) |item| {
        if ((nyFind(T, right.items, item) != null) == present) {
            out[count] = item;
            count += 1;
        }
    }
    return .{ .items = out[0..count] };
}

fn nySetIsSubset(comptime T: type, left: NySet(T), right: NySet(T)) bool {
    if (left.items.len > right.items.len) return false;
    for (left.items) |item| if (nyFind(T, right.items, item) == null) return false;
    return true;
}

fn nyMapInsert(comptime K: type, comptime V: type, map: NyMap(K, V), key: K, value: V) NyMap(K, V) {
    if (nyFind(K, map.keys, key)) |at| return .{ .keys = map.keys, .values = nyListSetAt(V, map.values, @intCast(at), value) };
    return .{ .keys = nyListAppend(K, map.keys, key), .values = nyListAppend(V, map.values, value) };
}

fn nyMapRemove(comptime K: type, comptime V: type, map: NyMap(K, V), key: K) NyMap(K, V) {
    const at: i64 = @intCast(nyFind(K, map.keys, key) orelse return map);
    return .{ .keys = nyListRemoveAt(K, map.keys, at), .values = nyListRemoveAt(V, map.values, at) };
}

fn nyMapGet(comptime K: type, comptime V: type, map: NyMap(K, V), key: K) ?V {
    return if (nyFind(K, map.keys, key)) |at| map.values[at] else null;
}

fn nyMapUnion(comptime K: type, comptime V: type, left: NyMap(K, V), right: NyMap(K, V)) NyMap(K, V) {
    var map = left;
    for (right.keys, right.values) |key, value| map = nyMapInsert(K, V, map, key, value);
    return map;
}

/// Bytes wrap a byte slice so they stay distinct from a UTF-8 String.
const NyBytes = struct {
    data: []const u8,
    pub const ny_bytes = true;
};

fn nyBytesFill(length: i64, value: i64) NyBytes {
    if (length <= 0) return .{ .data = &.{} };
    if (length > std.math.maxInt(u32)) @panic("out of memory");
    const data = nyAlloc(u8, @intCast(length));
    @memset(data, @truncate(@as(u64, @bitCast(value))));
    return .{ .data = data };
}

fn nyBytesSet(bytes: NyBytes, index: i64, value: i64) NyBytes {
    return .{ .data = nyListSetAt(u8, bytes.data, index, @truncate(@as(u64, @bitCast(value)))) };
}

/// One byte per code point; a code point above U+00FF becomes 0xFF.
fn nyBytesFromLatin1(text: []const u8) NyBytes {
    const data = nyAlloc(u8, std.unicode.utf8CountCodepoints(text) catch unreachable);
    var view = std.unicode.Utf8View.initUnchecked(text);
    var points = view.iterator();
    var at: usize = 0;
    while (points.nextCodepoint()) |point| : (at += 1) data[at] = if (point > 0xFF) 0xFF else @intCast(point);
    return .{ .data = data };
}

/// One code point U+0000..U+00FF per byte, as UTF-8.
fn nyBytesToLatin1(bytes: NyBytes) []const u8 {
    var out: std.ArrayList(u8) = .empty;
    for (bytes.data) |byte| {
        var buffer: [2]u8 = undefined;
        const size = std.unicode.utf8Encode(byte, &buffer) catch unreachable;
        out.appendSlice(nyAllocator(), buffer[0..size]) catch @panic("out of memory");
    }
    return out.items;
}

/// The language's sort: a top-down merge sort; a list of 0 or 1 items is returned as is, the
/// first length / 2 items and the rest are sorted, then merged taking the right head only when
/// less(right head, left head) holds.
fn nySortBy(comptime T: type, list: []const T, context: anytype, comptime less: anytype) []const T {
    if (list.len <= 1) return list;
    const half = list.len / 2;
    const left = nySortBy(T, list[0..half], context, less);
    const right = nySortBy(T, list[half..], context, less);
    const out = nyAlloc(T, list.len);
    var l: usize = 0;
    var r: usize = 0;
    for (out) |*slot| {
        if (l < left.len and (r >= right.len or !less(context, right[r], left[l]))) {
            slot.* = left[l];
            l += 1;
        } else {
            slot.* = right[r];
            r += 1;
        }
    }
    return out;
}

/// The first best item: a later item replaces it only when better(context, item, best) holds.
fn nyBestBy(comptime T: type, list: []const T, context: anytype, comptime better: anytype) ?T {
    if (list.len == 0) return null;
    var best = list[0];
    for (list[1..]) |item| {
        if (better(context, item, best)) best = item;
    }
    return best;
}

/// Structural equality over every supported value type.
fn nyEqual(left: anytype, right: @TypeOf(left)) bool {
    const T = @TypeOf(left);
    switch (@typeInfo(T)) {
        .int, .bool => return left == right,
        // As .NET's Double.Equals: NaN equals NaN, and 0.0 equals -0.0.
        .float => return left == right or (std.math.isNan(left) and std.math.isNan(right)),
        .optional => {
            if (left) |l| {
                if (right) |r| return nyEqual(l, r);
                return false;
            }
            return right == null;
        },
        .pointer => |pointer| {
            // A u8 slice is a String; any other slice is a List.
            if (pointer.size == .slice and pointer.child == u8) return std.mem.eql(u8, left, right);
            if (pointer.size == .slice) {
                if (left.len != right.len) return false;
                for (left, right) |l, r| if (!nyEqual(l, r)) return false;
                return true;
            }
            return nyEqual(left.*, right.*);
        },
        .@"struct" => |info| {
            if (comptime @hasDecl(T, "ny_bytes")) return std.mem.eql(u8, left.data, right.data);
            // Sets and Maps compare by content regardless of insertion order.
            if (comptime @hasDecl(T, "ny_set")) {
                if (left.items.len != right.items.len) return false;
                for (left.items) |item| if (nyFind(@TypeOf(item), right.items, item) == null) return false;
                return true;
            }
            if (comptime @hasDecl(T, "ny_map")) {
                if (left.keys.len != right.keys.len) return false;
                for (left.keys, left.values) |key, value| {
                    const at = nyFind(@TypeOf(key), right.keys, key) orelse return false;
                    if (!nyEqual(value, right.values[at])) return false;
                }
                return true;
            }
            inline for (info.field_names) |name| {
                if (!nyEqual(@field(left, name), @field(right, name))) return false;
            }
            return true;
        },
        .@"union" => |info| {
            const tag = std.meta.activeTag(left);
            if (tag != std.meta.activeTag(right)) return false;
            inline for (info.field_names) |name| {
                if (tag == @field(info.tag_type.?, name)) return nyEqual(@field(left, name), @field(right, name));
            }
            unreachable;
        },
        else => @compileError("nyEqual does not support " ++ @typeName(T)),
    }
}
"""

    /// Zig support for a runPure runner: reads argument tokens in the order the authoring session
    /// writes them and writes a result in the session's envelope form.
    let runnerSupport = """
const NyTokens = struct {
    items: []const [:0]const u8,
    index: usize = 1,

    fn next(self: *NyTokens) []const u8 {
        const token = self.items[self.index];
        self.index += 1;
        return token;
    }
};

/// A String token is "s" and the Base64 of its UTF-16LE code units.
fn nyDecodeText(token: []const u8) []const u8 {
    const encoded = token[1..];
    const size = std.base64.standard.Decoder.calcSizeForSlice(encoded) catch @panic("bad String token");
    const units = nyAllocator().alloc(u16, size / 2) catch @panic("out of memory");
    std.base64.standard.Decoder.decode(std.mem.sliceAsBytes(units), encoded) catch @panic("bad String token");
    for (units) |*unit| unit.* = std.mem.littleToNative(u16, unit.*);
    return std.unicode.utf16LeToUtf8Alloc(nyAllocator(), units) catch @panic("bad String token");
}

fn nyEncodeText(out: *std.Io.Writer, text: []const u8) !void {
    const units = std.unicode.utf8ToUtf16LeAlloc(nyAllocator(), text) catch @panic("bad String");
    const bytes = std.mem.sliceAsBytes(units);
    const encoded = nyAllocator().alloc(u8, std.base64.standard.Encoder.calcSize(bytes.len)) catch @panic("out of memory");
    try out.print("\"s{s}\"", .{std.base64.standard.Encoder.encode(encoded, bytes)});
}

fn nyDecode(comptime T: type, tokens: *NyTokens) T {
    switch (@typeInfo(T)) {
        .int => return std.fmt.parseInt(i64, tokens.next(), 10) catch @panic("bad Int64 token"),
        // A Float64 token is its 16 hexadecimal bit digits.
        .float => return @bitCast(std.fmt.parseInt(u64, tokens.next(), 16) catch @panic("bad Float64 token")),
        .bool => return std.mem.eql(u8, tokens.next(), "1"),
        .optional => |info| {
            if (std.mem.eql(u8, tokens.next(), "1")) return nyDecode(info.child, tokens);
            return null;
        },
        .pointer => |info| {
            if (info.size == .slice and info.child == u8) return nyDecodeText(tokens.next());
            if (info.size == .slice) {
                const count = std.fmt.parseInt(usize, tokens.next(), 10) catch @panic("bad List token");
                const items = nyAllocator().alloc(info.child, count) catch @panic("out of memory");
                for (items) |*item| item.* = nyDecode(info.child, tokens);
                return items;
            }
            return nyBox(info.child, nyDecode(info.child, tokens));
        },
        .@"struct" => |info| {
            if (T == NyUnit) {
                _ = tokens.next();
                return NyUnit{};
            }
            if (comptime @hasDecl(T, "ny_set")) {
                const Item = @typeInfo(@FieldType(T, "items")).pointer.child;
                return nySetFromList(Item, nyDecode([]const Item, tokens));
            }
            // Bytes travel as the String token of their latin-1 form.
            if (comptime @hasDecl(T, "ny_bytes")) return nyBytesFromLatin1(nyDecodeText(tokens.next()));
            if (comptime @hasDecl(T, "ny_map")) {
                const K = @typeInfo(@FieldType(T, "keys")).pointer.child;
                const V = @typeInfo(@FieldType(T, "values")).pointer.child;
                const count = std.fmt.parseInt(usize, tokens.next(), 10) catch @panic("bad Map token");
                var map = T{ .keys = &.{}, .values = &.{} };
                for (0..count) |_| {
                    const key = nyDecode(K, tokens);
                    map = nyMapInsert(K, V, map, key, nyDecode(V, tokens));
                }
                return map;
            }
            var value: T = undefined;
            inline for (info.field_names, info.field_types) |name, FieldType| @field(value, name) = nyDecode(FieldType, tokens);
            return value;
        },
        .@"union" => |info| {
            if (@hasDecl(T, "ny_ids")) {
                const index = std.fmt.parseInt(usize, tokens.next(), 10) catch @panic("bad variant token");
                inline for (info.field_names, info.field_types, 0..) |name, FieldType, position| {
                    if (position == index) return @unionInit(T, name, nyDecode(FieldType, tokens));
                }
                @panic("bad variant token");
            }
            if (std.mem.eql(u8, tokens.next(), "1")) return .{ .ok = nyDecode(@FieldType(T, "ok"), tokens) };
            return .{ .err = nyDecode(@FieldType(T, "err"), tokens) };
        },
        else => @compileError("nyDecode does not support " ++ @typeName(T)),
    }
}

// Scripted fakes, as the C# runner's: a script is one ordered queue, or one queue per argument key.
fn NyResponse(comptime R: type) type {
    // Kind: 0 return, 1 throw, 2 await cancellation, 3 delay.
    return struct { kind: u8, value: R = undefined, message: []const u8 = "", delay: i64 = 0, inner: ?*const NyResponse(R) = null };
}

fn NyScript(comptime R: type) type {
    return struct { keyed: bool, keys: []const []const u8, queues: []const []const NyResponse(R), next: []usize };
}

fn nyReadResponse(comptime R: type, tokens: *NyTokens) NyResponse(R) {
    const kind = std.fmt.parseInt(u8, tokens.next(), 10) catch @panic("bad response token");
    return switch (kind) {
        0, 2 => .{ .kind = kind, .value = nyDecode(R, tokens) },
        1 => .{ .kind = 1, .message = nyDecodeText(tokens.next()) },
        3 => blk: {
            const delay = std.fmt.parseInt(i64, tokens.next(), 10) catch @panic("bad delay token");
            const inner = nyAllocator().create(NyResponse(R)) catch @panic("out of memory");
            inner.* = nyReadResponse(R, tokens);
            break :blk .{ .kind = 3, .delay = delay, .inner = inner };
        },
        else => @panic("bad response token"),
    };
}

fn nyReadScript(comptime R: type, tokens: *NyTokens) NyScript(R) {
    const keyed = std.mem.eql(u8, tokens.next(), "1");
    const count = std.fmt.parseInt(usize, tokens.next(), 10) catch @panic("bad script token");
    const keys = nyAlloc([]const u8, count);
    const queues = nyAlloc([]const NyResponse(R), count);
    const next = nyAlloc(usize, count);
    for (0..count) |index| {
        next[index] = 0;
        // A key token is "k" and the argument tokens joined by commas.
        if (keyed) keys[index] = tokens.next()[1..];
        const responses = nyAlloc(NyResponse(R), std.fmt.parseInt(usize, tokens.next(), 10) catch @panic("bad script token"));
        for (responses) |*response| response.* = nyReadResponse(R, tokens);
        queues[index] = responses;
    }
    return .{ .keyed = keyed, .keys = keys, .queues = queues, .next = next };
}

var ny_out: *std.Io.Writer = undefined;
var ny_calls: std.Io.Writer.Allocating = undefined;
var ny_call_count: usize = 0;

fn nyRecordCall(site: usize, arguments: anytype) void {
    nyRecordCallWrite(site, arguments) catch @panic("out of memory");
}

fn nyRecordCallWrite(site: usize, arguments: anytype) !void {
    if (ny_call_count >= 1024) {
        try ny_out.writeAll("[\"limit\",\"The program made more than 1024 capability calls.\"]\n");
        try ny_out.flush();
        std.process.exit(0);
    }
    const w = &ny_calls.writer;
    if (ny_call_count > 0) try w.writeAll(",");
    ny_call_count += 1;
    try w.print("[{d},[", .{site});
    inline for (@typeInfo(@TypeOf(arguments)).@"struct".field_names, 0..) |name, position| {
        if (position > 0) try w.writeAll(",");
        try nyEncode(w, @field(arguments, name));
    }
    try w.writeAll("]]");
}

fn nyToken(w: *std.Io.Writer, first: *bool, comptime format: []const u8, arguments: anytype) !void {
    if (!first.*) try w.writeAll(",");
    first.* = false;
    try w.print(format, arguments);
}

fn nyTextToken(text: []const u8) []const u8 {
    var buffer: std.Io.Writer.Allocating = .init(nyAllocator());
    nyEncodeText(&buffer.writer, text) catch @panic("out of memory");
    const quoted = buffer.written();
    return quoted[1 .. quoted.len - 1];
}

/// Writes a value's input tokens, comma separated, exactly as the authoring session writes an
/// argument, so a call's key compares equal to a keyed case's.
fn nyTokens(w: *std.Io.Writer, first: *bool, value: anytype) !void {
    const T = @TypeOf(value);
    switch (@typeInfo(T)) {
        .int => try nyToken(w, first, "{d}", .{value}),
        .bool => try nyToken(w, first, "{s}", .{if (value) "1" else "0"}),
        .float => try nyToken(w, first, "{x:0>16}", .{@as(u64, @bitCast(value))}),
        .optional => {
            if (value) |item| {
                try nyToken(w, first, "1", .{});
                try nyTokens(w, first, item);
            } else try nyToken(w, first, "0", .{});
        },
        .pointer => |info| {
            if (info.size == .slice and info.child == u8) return nyToken(w, first, "{s}", .{nyTextToken(value)});
            if (info.size == .slice) {
                try nyToken(w, first, "{d}", .{value.len});
                for (value) |item| try nyTokens(w, first, item);
                return;
            }
            try nyTokens(w, first, value.*);
        },
        .@"struct" => |info| {
            if (T == NyUnit) return nyToken(w, first, "u", .{});
            if (comptime @hasDecl(T, "ny_bytes")) return nyToken(w, first, "{s}", .{nyTextToken(nyBytesToLatin1(value))});
            if (comptime @hasDecl(T, "ny_set")) {
                try nyToken(w, first, "{d}", .{value.items.len});
                for (value.items) |item| try nyTokens(w, first, item);
                return;
            }
            if (comptime @hasDecl(T, "ny_map")) {
                try nyToken(w, first, "{d}", .{value.keys.len});
                for (value.keys, value.values) |key, item| {
                    try nyTokens(w, first, key);
                    try nyTokens(w, first, item);
                }
                return;
            }
            // A Product, or a Union variant's fields.
            inline for (info.field_names) |name| try nyTokens(w, first, @field(value, name));
        },
        .@"union" => |info| {
            if (comptime @hasDecl(T, "ny_ids")) {
                const tag = std.meta.activeTag(value);
                inline for (info.field_names, 0..) |name, position| {
                    if (tag == @field(info.tag_type.?, name)) {
                        try nyToken(w, first, "{d}", .{position});
                        try nyTokens(w, first, @field(value, name));
                    }
                }
                return;
            }
            switch (value) {
                .ok => |item| {
                    try nyToken(w, first, "1", .{});
                    try nyTokens(w, first, item);
                },
                .err => |item| {
                    try nyToken(w, first, "0", .{});
                    try nyTokens(w, first, item);
                },
            }
        },
        else => @compileError("nyTokens does not support " ++ @typeName(T)),
    }
}


/// Ends the run with the envelope of a failure no ExceptionCatch handled.
fn nyUncaught() noreturn {
    nyUncaughtWrite() catch {};
    std.process.exit(0);
}

fn nyUncaughtWrite() !void {
    try ny_out.print("[\"exception\",\"{s}\",", .{ny_failure.type_name});
    try nyEncodeText(ny_out, ny_failure.message);
    try ny_out.print(",[],[{s}],{},\"{s}\"]\n", .{ ny_calls.written(), ny_failure.exhausted, if (ny_failure.cancelled) "cancelled" else "fault" });
    try ny_out.flush();
}

/// An entry's result, or the end of the run when the entry failed.
fn nyValue(value: anytype) NyPayload(@TypeOf(value)) {
    return switch (@typeInfo(@TypeOf(value))) {
        .error_union => value catch nyUncaught(),
        else => value,
    };
}

fn nyRespond(comptime R: type, script: *NyScript(R), arguments: anytype) NyError!R {
    var selected: usize = 0;
    if (script.keyed) {
        var key: std.Io.Writer.Allocating = .init(nyAllocator());
        var first = true;
        inline for (@typeInfo(@TypeOf(arguments)).@"struct".field_names) |name| nyTokens(&key.writer, &first, @field(arguments, name)) catch @panic("out of memory");
        selected = for (script.keys, 0..) |candidate, index| {
            if (std.mem.eql(u8, candidate, key.written())) break index;
        } else return nyFail("FakeArgumentsUnmatchedException", "No fake case matches the complete call arguments.", false);
    }
    const position = script.next[selected];
    if (position >= script.queues[selected].len) return nyFail("FakeResponsesExhaustedException", "The fake operation has no scripted response left for this call.", true);
    script.next[selected] += 1;
    var response: *const NyResponse(R) = &script.queues[selected][position];
    while (true) {
        switch (response.kind) {
            0 => return response.value,
            1 => return nyFail("FakeCapabilityException", response.message, false),
            2 => {
                try nyAwaitCancellation();
                return response.value;
            },
            else => {
                // A delay is not interrupted by cancellation; then the inner response applies.
                _ = try nyBlock(nyNever, undefined, response.delay, true);
                response = response.inner.?;
            },
        }
    }
}

fn nyEncode(out: *std.Io.Writer, value: anytype) !void {
    const T = @TypeOf(value);
    switch (@typeInfo(T)) {
        .int => try out.print("{d}", .{value}),
        .float => try out.print("\"{x:0>16}\"", .{@as(u64, @bitCast(value))}),
        .bool => try out.writeAll(if (value) "true" else "false"),
        .optional => {
            if (value) |item| {
                try out.writeAll("[");
                try nyEncode(out, item);
                try out.writeAll("]");
            } else try out.writeAll("[]");
        },
        .pointer => |info| {
            if (info.size == .slice and info.child == u8) return nyEncodeText(out, value);
            if (info.size == .slice) {
                try out.writeAll("[");
                for (value, 0..) |item, position| {
                    if (position > 0) try out.writeAll(",");
                    try nyEncode(out, item);
                }
                return out.writeAll("]");
            }
            return nyEncode(out, value.*);
        },
        .@"struct" => |info| {
            if (T == NyUnit) return out.writeAll("\"unit\"");
            if (comptime @hasDecl(T, "ny_set")) return nyEncode(out, value.items);
            if (comptime @hasDecl(T, "ny_bytes")) return nyEncodeText(out, nyBytesToLatin1(value));
            if (comptime @hasDecl(T, "ny_map")) {
                try out.writeAll("[");
                for (value.keys, value.values, 0..) |key, item, position| {
                    if (position > 0) try out.writeAll(",");
                    try out.writeAll("[");
                    try nyEncode(out, key);
                    try out.writeAll(",");
                    try nyEncode(out, item);
                    try out.writeAll("]");
                }
                return out.writeAll("]");
            }
            try out.writeAll("[");
            inline for (info.field_names, 0..) |name, position| {
                if (position > 0) try out.writeAll(",");
                try nyEncode(out, @field(value, name));
            }
            try out.writeAll("]");
        },
        .@"union" => |info| {
            if (@hasDecl(T, "ny_ids")) {
                const tag = std.meta.activeTag(value);
                inline for (info.field_names, 0..) |name, position| {
                    if (tag == @field(info.tag_type.?, name)) {
                        try out.print("[\"{s}\",", .{T.ny_ids[position]});
                        try nyEncode(out, @field(value, name));
                        try out.writeAll("]");
                    }
                }
                return;
            }
            switch (value) {
                .ok => |item| {
                    try out.writeAll("[true,");
                    try nyEncode(out, item);
                    try out.writeAll("]");
                },
                .err => |item| {
                    try out.writeAll("[false,");
                    try nyEncode(out, item);
                    try out.writeAll("]");
                },
            }
        },
        else => @compileError("nyEncode does not support " ++ @typeName(T)),
    }
}
"""
