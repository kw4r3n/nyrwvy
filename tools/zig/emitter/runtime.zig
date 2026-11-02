const std = @import("std");

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

// Memory (docs/decisions/zig-backend-memory-ownership.md). Program values (String bytes, boxed
// Products and Unions, List, Set and Map storage, Bytes) are reference counted: an 8-byte header
// before the payload holds the count and the allocated length, so a prefix of a fresh allocation
// frees exactly. A call consumes its arguments and returns an owned result; runtime helpers borrow
// their arguments and return owned results. Values come from `ny_values` (a checked or freeing
// allocator in the runner; the arena when null, as library mode resets it per call).
// Task and hand-off cell handles are counted too (`ny_handle`): the record keeps its value until
// the last handle (and, for a task, its own run) drops it, so an escaped handle still reads it.
// shortcut: runtime records (scheduler, task and cell records, cancellation, fake scripts,
// failures) still live in `ny_arena` for the life of the process; move them to counted storage
// when a long-running program starts many tasks.
var ny_arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
var ny_values: ?std.mem.Allocator = null;
/// While set, values are allocated in the arena with a static count (never retained or freed):
/// scripted fake responses, which the script keeps for the whole run.
var ny_static_values = false;

fn nyAllocator() std.mem.Allocator {
    return ny_arena.allocator();
}

/// The allocator of value storage and of temporary buffers freed in the same call.
fn nyRaw() std.mem.Allocator {
    return ny_values orelse ny_arena.allocator();
}

const NyHeader = extern struct { count: u32, cap: u32 };
const ny_static: u32 = std.math.maxInt(u32);

fn nyHeader(ptr: anytype) *NyHeader {
    return @ptrFromInt(@intFromPtr(ptr) - @sizeOf(NyHeader));
}

/// Counted storage for `count` items with a count of one; no allocation for zero items.
fn nyAlloc(comptime T: type, count: usize) []T {
    comptime std.debug.assert(@alignOf(T) <= 8);
    if (count == 0) return &.{};
    if (count > std.math.maxInt(u32)) nyOom();
    const bytes = (if (ny_static_values) nyAllocator() else nyRaw()).alignedAlloc(u8, .@"8", @sizeOf(NyHeader) + count * @sizeOf(T)) catch nyOom();
    const header: *NyHeader = @ptrCast(bytes.ptr);
    header.* = .{ .count = if (ny_static_values) ny_static else 1, .cap = @intCast(count) };
    const items: [*]T = @ptrCast(@alignCast(bytes.ptr + @sizeOf(NyHeader)));
    return items[0..count];
}

fn nyFree(comptime T: type, ptr: anytype) void {
    const header = nyHeader(ptr);
    const bytes: [*]align(8) u8 = @ptrCast(@alignCast(header));
    nyRaw().free(bytes[0 .. @sizeOf(NyHeader) + @as(usize, header.cap) * @sizeOf(T)]);
}

fn nyBox(comptime T: type, value: T) *const T {
    const slot = &nyAlloc(T, 1)[0];
    slot.* = value;
    return slot;
}

/// A string literal as a static counted value.
fn nyStatic(comptime text: []const u8) []const u8 {
    if (text.len == 0) return "";
    const Holder = struct {
        const data: extern struct { header: NyHeader, bytes: [text.len]u8 } = .{ .header = .{ .count = ny_static, .cap = text.len }, .bytes = text[0..text.len].* };
    };
    return &Holder.data.bytes;
}

/// Whether a value of type T holds counted storage. Other mutable pointers are runtime records
/// (scopes, cancellation) and function pointers carry no count; task and cell handles count.
fn nyCounted(comptime T: type) bool {
    return switch (@typeInfo(T)) {
        .pointer => |info| info.size == .slice or (info.size == .one and info.attrs.@"const" and @typeInfo(info.child) != .@"fn") or nyHandle(T),
        .optional => |info| nyCounted(info.child),
        .@"struct" => @hasDecl(T, "ny_set") or @hasDecl(T, "ny_map") or @hasDecl(T, "ny_bytes"),
        .@"union" => |info| !@hasDecl(T, "ny_ids") and (nyCounted(info.field_types[0]) or nyCounted(info.field_types[1])),
        else => false,
    };
}

/// A task or hand-off cell handle: a record in the arena with its own count of handles.
fn nyHandle(comptime T: type) bool {
    return switch (@typeInfo(T)) {
        .pointer => |info| info.size == .one and !info.attrs.@"const" and @typeInfo(info.child) == .@"struct" and @hasDecl(info.child, "ny_handle"),
        else => false,
    };
}

fn nyIncrement(ptr: anytype) void {
    const header = nyHeader(ptr);
    if (header.count != ny_static) header.count += 1;
}

/// Drops one reference; true when it was the last.
fn nyDrop(ptr: anytype) bool {
    const header = nyHeader(ptr);
    if (header.count == ny_static) return false;
    header.count -= 1;
    return header.count == 0;
}

fn nyRetain(value: anytype) void {
    const T = @TypeOf(value);
    if (comptime !nyCounted(T)) return;
    switch (@typeInfo(T)) {
        .pointer => |info| if (comptime nyHandle(T)) {
            value.refs += 1;
        } else if (info.size != .slice) nyIncrement(value) else if (value.len > 0) nyIncrement(value.ptr),
        .optional => if (value) |item| nyRetain(item),
        .@"struct" => if (comptime @hasDecl(T, "ny_set")) nyRetain(value.items) else if (comptime @hasDecl(T, "ny_map")) {
            nyRetain(value.keys);
            nyRetain(value.values);
        } else nyRetain(value.data),
        .@"union" => switch (value) {
            inline else => |item| nyRetain(item),
        },
        else => unreachable,
    }
}

/// The value with one more reference: a borrowed value passed on as owned.
fn nyShare(value: anytype) @TypeOf(value) {
    nyRetain(value);
    return value;
}

fn nyRelease(value: anytype) void {
    const T = @TypeOf(value);
    if (comptime !nyCounted(T)) return;
    switch (@typeInfo(T)) {
        .pointer => |info| {
            if (comptime nyHandle(T)) return value.drop();
            if (info.size != .slice) return nyReleaseBox(value);
            if (value.len == 0 or !nyDrop(value.ptr)) return;
            if (comptime nyCounted(info.child)) for (value) |item| nyRelease(item);
            nyFree(info.child, value.ptr);
        },
        .optional => if (value) |item| nyRelease(item),
        .@"struct" => if (comptime @hasDecl(T, "ny_set")) nyRelease(value.items) else if (comptime @hasDecl(T, "ny_map")) {
            nyRelease(value.keys);
            nyRelease(value.values);
        } else nyRelease(value.data),
        .@"union" => switch (value) {
            inline else => |item| nyRelease(item),
        },
        else => unreachable,
    }
}

/// Releases a box; a field of the box's own type (a list-like spine) continues the loop instead of
/// recursing, so a long chain never overflows the stack.
fn nyReleaseBox(first: anytype) void {
    const P = @TypeOf(first);
    const T = @typeInfo(P).pointer.child;
    var next: ?P = first;
    while (next) |box| {
        next = null;
        if (!nyDrop(box)) return;
        const value = box.*;
        nyFree(T, box);
        switch (@typeInfo(T)) {
            .@"struct" => nyReleaseFields(P, value, &next),
            .@"union" => switch (value) {
                inline else => |payload| nyReleaseFields(P, payload, &next),
            },
            else => unreachable,
        }
    }
}

fn nyReleaseFields(comptime P: type, fields: anytype, next: *?P) void {
    inline for (@typeInfo(@TypeOf(fields)).@"struct".field_names) |name| {
        const field = @field(fields, name);
        if (@TypeOf(field) == P or @TypeOf(field) == ?P) {
            if (next.*) |earlier| nyRelease(earlier);
            next.* = field;
        } else nyRelease(field);
    }
}

/// Releases every field of a struct or tuple (a never-started task's captured arguments).
fn nyReleaseAll(values: anytype) void {
    inline for (@typeInfo(@TypeOf(values)).@"struct".field_names) |name| nyRelease(@field(values, name));
}

/// Retains every field of a copied Product, so the copy owns its own references.
fn nyRetainFields(value: anytype) void {
    inline for (@typeInfo(@TypeOf(value)).@"struct".field_names) |name| nyRetain(@field(value, name));
}

/// Counted copies: `nyListOf` retains the items (borrowed), `nyListMove` takes them over.
fn nyListOf(comptime T: type, items: []const T) []const T {
    const copy = nyAlloc(T, items.len);
    @memcpy(copy, items);
    for (copy) |item| nyRetain(item);
    return copy;
}

fn nyListMove(comptime T: type, items: []const T) []const T {
    const copy = nyAlloc(T, items.len);
    @memcpy(copy, items);
    return copy;
}

/// A fresh allocation cut to its first `count` items (which it owns); freed when none remain.
fn nyShrink(comptime T: type, items: []T, count: usize) []const T {
    if (count > 0) return items[0..count];
    if (items.len > 0) nyFree(T, items.ptr);
    return &.{};
}

/// Takes over a temporary buffer from `nyRaw()`: a counted copy, then the buffer is freed.
fn nyAdopt(comptime T: type, raw: []const T) []const T {
    const copy = nyListMove(T, raw);
    nyRaw().free(raw);
    return copy;
}

fn nyAdoptList(comptime T: type, list: *std.ArrayList(T)) []const T {
    const copy = nyListMove(T, list.items);
    list.deinit(nyRaw());
    return copy;
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
    var buffer: [24]u8 = undefined;
    return nyListMove(u8, std.fmt.bufPrint(&buffer, "{d}", .{value}) catch unreachable);
}

/// Strings are UTF-8 sequences of Unicode scalar values, so this never fails.
fn nyStringLength(text: []const u8) i64 {
    return @intCast(std.unicode.utf8CountCodepoints(text) catch unreachable);
}

fn nyConcatOwned(left: []const u8, right: []const u8) []const u8 {
    return nyListConcatOwned(u8, left, right);
}

fn nyConcat(left: []const u8, right: []const u8) []const u8 {
    if (left.len == 0) return nyShare(right);
    if (right.len == 0) return nyShare(left);
    const joined = nyAlloc(u8, left.len + right.len);
    @memcpy(joined[0..left.len], left);
    @memcpy(joined[left.len..], right);
    return joined;
}

// Concurrency follows the C# runtime under its deterministic virtual clock: one thread runs at a time;
// when it blocks or finishes, the next resumes: an elapsed delay by (deadline, start order), else a
// thread that can continue (new, ready, or timed out) by start order, else time jumps to the earliest
// deadline. With nothing able to resume, every waiter fails as the C# clock's waiters do, one at a time.
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

fn nyFirstFailed() ?*NyWaiter {
    var first: ?*NyWaiter = null;
    for (ny_waiters.items) |waiter| {
        if (waiter.failed and (first == null or waiter.order < first.?.order)) first = waiter;
    }
    return first;
}

fn nySchedule() void {
    if (ny_running > 0 or ny_waiters.items.len == 0) return;
    var next = nyFirstFailed() orelse nyEarliest(nyElapsedDelay);
    if (next == null) {
        for (ny_waiters.items) |waiter| {
            if ((waiter.ready(waiter.context) or (waiter.due != null and waiter.due.? <= ny_now)) and (next == null or waiter.order < next.?.order)) next = waiter;
        }
    }
    if (next == null) {
        next = nyEarliest(nyHasDue);
        if (next == null) {
            // Nothing can ever resume: every waiter fails, released one at a time by start order so
            // their unwinding releases never run on two threads at once (decision A's plain counts).
            for (ny_waiters.items) |waiter| waiter.failed = true;
            next = nyFirstFailed();
        } else ny_now = next.?.due.?;
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

/// A task record; `refs` counts its handles plus one for the run until it is done. The value (set
/// when the task succeeds) is released with the last reference.
fn NyTask(comptime W: type) type {
    return struct {
        base: NyTaskBase,
        value: W = undefined,
        refs: u32 = 2,
        pub const ny_handle = {};

        pub fn drop(task: *@This()) void {
            task.refs -= 1;
            if (task.refs == 0 and !task.base.failed) nyRelease(task.value);
        }
    };
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
    const tasks = nyAllocator().dupe(*NyTaskBase, scope.tasks.items) catch nyOom();
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
    task.drop();
}

fn nyTaskThread(comptime W: type, comptime Job: type) fn (*NyTask(W), *Job, *NyWaiter) void {
    return struct {
        fn run(task: *NyTask(W), job: *Job, ticket: *NyWaiter) void {
            if (!nyStart(ticket)) {
                // The clock failed every waiter: the task fails before it starts and drops its captures.
                job.discard();
                task.base.failed = true;
                task.base.failure = ny_deadlock;
                task.base.done = true;
                task.drop();
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
    const bases = nyAllocator().alloc(*NyTaskBase, tasks.len) catch nyOom();
    for (tasks, 0..) |task, index| bases[index] = &task.base;
    const held = nyAllocator().create([]const *NyTaskBase) catch @panic("out of memory");
    held.* = bases;
    _ = try nyBlock(if (all) nyAllDone else nyAnyDone, @ptrCast(held), if (limit) |value| @max(value, 0) else null, false);
    for (tasks) |task| {
        if (task.base.done and task.base.failed) {
            ny_failure = task.base.failure;
            return error.NyFailure;
        }
    }
    const out = nyAlloc(?W, tasks.len);
    for (tasks, 0..) |task, index| out[index] = if (task.base.done) nyShare(task.value) else null;
    return out;
}

/// A hand-off cell; `refs` counts its handles, and the last one releases a completed value.
fn NyOneShot(comptime T: type) type {
    return struct {
        state: NyOneShotState,
        value: T = undefined,
        scope: *NyScope,
        refs: u32 = 1,
        pub const ny_handle = {};

        pub fn drop(cell: *@This()) void {
            cell.refs -= 1;
            if (cell.refs == 0 and cell.state == .completed) nyRelease(cell.value);
        }
    };
}

fn nyOneShotCreate(comptime T: type, scope: *NyScope) *NyOneShot(T) {
    const cell = nyAllocator().create(NyOneShot(T)) catch @panic("out of memory");
    cell.* = .{ .state = if (scope.handoffs_closed) .closed else .pending, .scope = scope };
    scope.handoffs.append(nyAllocator(), &cell.state) catch @panic("out of memory");
    return cell;
}

fn nyOneShotComplete(comptime T: type, cell: *NyOneShot(T), value: T) bool {
    if (cell.state != .pending or cell.scope.handoffs_closed) {
        nyRelease(value);
        return false;
    }
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
    return if (cell.state == .completed) nyShare(cell.value) else null;
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
/// A task's work: `Work.call` applied to the values the starting instruction captured (moved in);
/// `Work.drop` releases them when the task never starts.
fn NyJob(comptime C: type, comptime R: type, comptime Work: type) type {
    return struct {
        c: C,

        fn run(job: *const @This()) R {
            return Work.call(job.c);
        }

        fn discard(job: *const @This()) void {
            Work.drop(job.c);
        }
    };
}

fn nyParallelAll(comptime T: type, comptime U: type, comptime E: type, items: []const T, context: anytype, comptime worker: anytype) NyError!NyResult([]const U, E) {
    const Outcome = NyResult(U, E);
    const shared = nyLinked(ny_token);
    const tasks = nyAllocator().alloc(*NyTask(Outcome), items.len) catch nyOom();
    const bases = nyAllocator().alloc(*NyTaskBase, items.len) catch nyOom();
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

        // The item and context are borrowed from the caller, which outlives every worker.
        fn discard(_: *const @This()) void {}
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
    // The outcomes move out of the task records: into the result, or released when it is a failure or an Err.
    const failed = for (tasks) |task| {
        if (task.base.failed) break &task.base;
    } else null;
    const first_err = if (failed != null) null else for (tasks, 0..) |task, index| {
        if (task.value == .err) break index;
    } else null;
    if (failed != null or first_err != null) {
        for (tasks, 0..) |task, index| if (!task.base.failed and index != first_err) nyRelease(task.value);
        if (failed) |base| {
            ny_failure = base.failure;
            return error.NyFailure;
        }
        return .{ .err = tasks[first_err.?].value.err };
    }
    const values = nyAlloc(U, items.len);
    for (tasks, 0..) |task, index| values[index] = task.value.ok;
    return .{ .ok = values };
}

// Lists are immutable slices. shortcut: every update copies (O(n)); a persistent vector, as the C#
// backend uses, replaces this when a consumer needs the documented O(log n) updates.

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

/// Copies borrowed items into fresh storage, retaining each.
fn nyCopyItems(comptime T: type, into: []T, items: []const T) void {
    @memcpy(into, items);
    for (items) |item| nyRetain(item);
}

/// Storage for an owned `list` plus `extra` trailing items the caller fills. A unique list (count
/// one) is reused, grown by doubling when its spare capacity is short; a shared or static one is
/// copied (its items retained) and the caller's reference dropped. So reuse is never observable.
fn nyExtend(comptime T: type, list: []const T, extra: usize) []T {
    const len = list.len + extra;
    if (list.len > 0 and nyHeader(list.ptr).count == 1) {
        const header = nyHeader(list.ptr);
        if (header.cap >= len) return @constCast(list.ptr)[0..len];
        const cap = @min(@max(len, @as(usize, header.cap) * 2), std.math.maxInt(u32));
        if (len > cap) nyOom();
        const old: [*]align(8) u8 = @ptrCast(@alignCast(header));
        const bytes = nyRaw().realloc(old[0 .. @sizeOf(NyHeader) + @as(usize, header.cap) * @sizeOf(T)], @sizeOf(NyHeader) + cap * @sizeOf(T)) catch nyOom();
        const moved: *NyHeader = @ptrCast(bytes.ptr);
        moved.cap = @intCast(cap);
        const items: [*]T = @ptrCast(@alignCast(bytes.ptr + @sizeOf(NyHeader)));
        return items[0..len];
    }
    const items = nyAlloc(T, len);
    nyCopyItems(T, items[0..list.len], list);
    nyRelease(list);
    return items;
}

/// Concatenation that consumes `left` (reused in place when unique) and borrows `right`.
fn nyListConcatOwned(comptime T: type, left: []const T, right: []const T) []const T {
    if (right.len == 0) return left;
    if (left.len == 0) return nyShare(right);
    const items = nyExtend(T, left, right.len);
    nyCopyItems(T, items[items.len - right.len ..], right);
    return items;
}

fn nyListAppendOwned(comptime T: type, list: []const T, item: T) []const T {
    const items = nyExtend(T, list, 1);
    items[list.len] = nyShare(item);
    return items;
}

fn nyListSetAtOwned(comptime T: type, list: []const T, index: i64, item: T) []const T {
    const at = nyIndex(index, list.len) orelse return list;
    const items = nyExtend(T, list, 0);
    nyRelease(items[at]);
    items[at] = nyShare(item);
    return items;
}

/// A slice that consumes `list`: a unique list drops the items outside the range and keeps its
/// storage, shrunk when it is more than twice the kept length.
fn nyListSliceOwned(comptime T: type, list: []const T, start: i64, end: i64) []const T {
    const from = nyClamp(start, list.len);
    const to = @max(from, nyClamp(end, list.len));
    if (from == 0 and to == list.len) return list;
    if (list.len == 0 or nyHeader(list.ptr).count != 1) {
        defer nyRelease(list);
        return nyListSlice(T, list, start, end);
    }
    const items = @constCast(list);
    for (items[0..from]) |item| nyRelease(item);
    for (items[to..]) |item| nyRelease(item);
    if (to == from) {
        nyFree(T, list.ptr);
        return &.{};
    }
    const kept = to - from;
    std.mem.copyForwards(T, items[0..kept], items[from..to]);
    const header = nyHeader(list.ptr);
    if (kept * 2 >= header.cap) return items[0..kept];
    // Storage more than twice the kept length shrinks to it, so a small slice never pins a large list.
    const old: [*]align(8) u8 = @ptrCast(@alignCast(header));
    const bytes = nyRaw().realloc(old[0 .. @sizeOf(NyHeader) + @as(usize, header.cap) * @sizeOf(T)], @sizeOf(NyHeader) + kept * @sizeOf(T)) catch nyOom();
    const moved: *NyHeader = @ptrCast(bytes.ptr);
    moved.cap = @intCast(kept);
    const shrunk: [*]T = @ptrCast(@alignCast(bytes.ptr + @sizeOf(NyHeader)));
    return shrunk[0..kept];
}

/// Whether an owned box is its only reference, so an update may write it in place.
fn nyUnique(box: anytype) bool {
    return nyHeader(box).count == 1;
}

fn nyListAppend(comptime T: type, list: []const T, item: T) []const T {
    const items = nyAlloc(T, list.len + 1);
    nyCopyItems(T, items[0..list.len], list);
    items[list.len] = nyShare(item);
    return items;
}

fn nyListConcat(comptime T: type, left: []const T, right: []const T) []const T {
    if (left.len == 0) return nyShare(right);
    if (right.len == 0) return nyShare(left);
    const items = nyAlloc(T, left.len + right.len);
    nyCopyItems(T, items[0..left.len], left);
    nyCopyItems(T, items[left.len..], right);
    return items;
}

fn nyListReverse(comptime T: type, list: []const T) []const T {
    const items = nyAlloc(T, list.len);
    for (list, 0..) |item, position| items[list.len - 1 - position] = nyShare(item);
    return items;
}

fn nyListItemAt(comptime T: type, list: []const T, index: i64) ?T {
    return if (nyIndex(index, list.len)) |at| nyShare(list[at]) else null;
}

fn nyListLast(comptime T: type, list: []const T) ?T {
    return if (list.len == 0) null else nyShare(list[list.len - 1]);
}

fn nyListDropLast(comptime T: type, list: []const T) []const T {
    return if (list.len == 0) list else nyListOf(T, list[0 .. list.len - 1]);
}

fn nyListSlice(comptime T: type, list: []const T, start: i64, end: i64) []const T {
    const from = nyClamp(start, list.len);
    const to = nyClamp(end, list.len);
    return if (to <= from) &.{} else nyListOf(T, list[from..to]);
}

fn nyListSetAt(comptime T: type, list: []const T, index: i64, item: T) []const T {
    const at = nyIndex(index, list.len) orelse return nyShare(list);
    const items = nyAlloc(T, list.len);
    nyCopyItems(T, items[0..at], list[0..at]);
    items[at] = nyShare(item);
    nyCopyItems(T, items[at + 1 ..], list[at + 1 ..]);
    return items;
}

fn nyListInsertAt(comptime T: type, list: []const T, index: i64, item: T) []const T {
    const at = nyClamp(index, list.len);
    const items = nyAlloc(T, list.len + 1);
    nyCopyItems(T, items[0..at], list[0..at]);
    items[at] = nyShare(item);
    nyCopyItems(T, items[at + 1 ..], list[at..]);
    return items;
}

fn nyListRemoveAt(comptime T: type, list: []const T, index: i64) []const T {
    const at = nyIndex(index, list.len) orelse return nyShare(list);
    const items = nyAlloc(T, list.len - 1);
    nyCopyItems(T, items[0..at], list[0..at]);
    nyCopyItems(T, items[at..], list[at + 1 ..]);
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
    if (nyFind(T, set.items, item) != null) return nyShare(set);
    return .{ .items = nyListAppend(T, set.items, item) };
}

/// Inserts into a set this call owns (reused when unique), borrowing the item.
fn nySetInsertInto(comptime T: type, set: NySet(T), item: T) NySet(T) {
    if (nyFind(T, set.items, item) != null) return set;
    return .{ .items = nyListAppendOwned(T, set.items, item) };
}

/// Inserts into a set this call owns, consuming both the set and the item.
fn nySetInsertOwned(comptime T: type, set: NySet(T), item: T) NySet(T) {
    defer nyRelease(item);
    return nySetInsertInto(T, set, item);
}

fn nySetRemove(comptime T: type, set: NySet(T), item: T) NySet(T) {
    const at = nyFind(T, set.items, item) orelse return nyShare(set);
    return .{ .items = nyListRemoveAt(T, set.items, @intCast(at)) };
}

/// The distinct items in first-seen order.
fn nyDistinct(comptime T: type, first: []const T, second: []const T) []const T {
    var kept: std.ArrayList(T) = .empty;
    for ([_][]const T{ first, second }) |items| for (items) |item| {
        if (nyFind(T, kept.items, item) == null) kept.append(nyRaw(), item) catch nyOom();
    };
    for (kept.items) |item| nyRetain(item);
    return nyAdoptList(T, &kept);
}

fn nySetFromList(comptime T: type, items: []const T) NySet(T) {
    return .{ .items = nyDistinct(T, items, &.{}) };
}

fn nySetUnion(comptime T: type, left: NySet(T), right: NySet(T)) NySet(T) {
    if (right.items.len == 0) return nyShare(left);
    return .{ .items = nyDistinct(T, left.items, right.items) };
}

fn nySetKeep(comptime T: type, left: NySet(T), right: NySet(T), present: bool) NySet(T) {
    const out = nyAlloc(T, left.items.len);
    var count: usize = 0;
    for (left.items) |item| {
        if ((nyFind(T, right.items, item) != null) == present) {
            out[count] = nyShare(item);
            count += 1;
        }
    }
    return .{ .items = nyShrink(T, out, count) };
}

fn nySetIsSubset(comptime T: type, left: NySet(T), right: NySet(T)) bool {
    if (left.items.len > right.items.len) return false;
    for (left.items) |item| if (nyFind(T, right.items, item) == null) return false;
    return true;
}

fn nyMapInsert(comptime K: type, comptime V: type, map: NyMap(K, V), key: K, value: V) NyMap(K, V) {
    if (nyFind(K, map.keys, key)) |at| return .{ .keys = nyShare(map.keys), .values = nyListSetAt(V, map.values, @intCast(at), value) };
    return .{ .keys = nyListAppend(K, map.keys, key), .values = nyListAppend(V, map.values, value) };
}

/// Inserts into a map this call owns (keys and values each reused when unique), borrowing the key and value.
fn nyMapInsertInto(comptime K: type, comptime V: type, map: NyMap(K, V), key: K, value: V) NyMap(K, V) {
    if (nyFind(K, map.keys, key)) |at| return .{ .keys = map.keys, .values = nyListSetAtOwned(V, map.values, @intCast(at), value) };
    return .{ .keys = nyListAppendOwned(K, map.keys, key), .values = nyListAppendOwned(V, map.values, value) };
}

/// Inserts into a map this call owns, consuming the map, the key and the value.
fn nyMapInsertOwned(comptime K: type, comptime V: type, map: NyMap(K, V), key: K, value: V) NyMap(K, V) {
    defer nyRelease(key);
    defer nyRelease(value);
    return nyMapInsertInto(K, V, map, key, value);
}

fn nyMapRemove(comptime K: type, comptime V: type, map: NyMap(K, V), key: K) NyMap(K, V) {
    const at: i64 = @intCast(nyFind(K, map.keys, key) orelse return nyShare(map));
    return .{ .keys = nyListRemoveAt(K, map.keys, at), .values = nyListRemoveAt(V, map.values, at) };
}

fn nyMapGet(comptime K: type, comptime V: type, map: NyMap(K, V), key: K) ?V {
    return if (nyFind(K, map.keys, key)) |at| nyShare(map.values[at]) else null;
}

fn nyMapUnion(comptime K: type, comptime V: type, left: NyMap(K, V), right: NyMap(K, V)) NyMap(K, V) {
    var map = nyShare(left);
    for (right.keys, right.values) |key, value| map = nyMapInsertOwned(K, V, map, nyShare(key), nyShare(value));
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
        out.appendSlice(nyRaw(), buffer[0..size]) catch @panic("out of memory");
    }
    return nyAdoptList(u8, &out);
}

/// The language's sort: a top-down merge sort; a list of 0 or 1 items is returned as is, the
/// first length / 2 items and the rest are sorted, then merged taking the right head only when
/// less(right head, left head) holds.
fn nySortBy(comptime T: type, list: []const T, context: anytype, comptime less: anytype) []const T {
    if (list.len <= 1) return nyShare(list);
    const out = nyAlloc(T, list.len);
    nyCopyItems(T, out, list);
    const scratch = nyRaw().alloc(T, list.len) catch nyOom();
    defer nyRaw().free(scratch);
    nySortRun(T, out, scratch, context, less);
    return out;
}

fn nySortRun(comptime T: type, items: []T, scratch: []T, context: anytype, comptime less: anytype) void {
    if (items.len <= 1) return;
    const half = items.len / 2;
    nySortRun(T, items[0..half], scratch[0..half], context, less);
    nySortRun(T, items[half..], scratch[half..], context, less);
    @memcpy(scratch, items);
    const left = scratch[0..half];
    const right = scratch[half..];
    var l: usize = 0;
    var r: usize = 0;
    for (items) |*slot| {
        if (l < left.len and (r >= right.len or !less(context, right[r], left[l]))) {
            slot.* = left[l];
            l += 1;
        } else {
            slot.* = right[r];
            r += 1;
        }
    }
}

/// The first best item: a later item replaces it only when better(context, item, best) holds.
fn nyBestBy(comptime T: type, list: []const T, context: anytype, comptime better: anytype) ?T {
    if (list.len == 0) return null;
    var best = list[0];
    for (list[1..]) |item| {
        if (better(context, item, best)) best = item;
    }
    return nyShare(best);
}

// Text positions count code points. A String is valid UTF-8, so a byte-level match of a String
// always starts and ends on code-point boundaries and byte order is code-point order.
fn nyOom() noreturn {
    @panic("out of memory");
}

fn nyWidth(text: []const u8, offset: usize) usize {
    return std.unicode.utf8ByteSequenceLength(text[offset]) catch unreachable;
}

/// The byte offset of a code-point position clamped to [0, length].
fn nyOffsetOf(text: []const u8, position: i64) usize {
    var offset: usize = 0;
    var reached: i64 = 0;
    while (reached < position and offset < text.len) : (reached += 1) offset += nyWidth(text, offset);
    return offset;
}

fn nyPositionOf(text: []const u8, offset: usize) i64 {
    return nyStringLength(text[0..offset]);
}

/// Whether the code point at the start of `point` (one whole code point) is in `set`.
fn nyInSet(set: []const u8, point: []const u8) bool {
    return std.mem.indexOf(u8, set, point) != null;
}

fn nyStringSlice(text: []const u8, start: i64, end: i64) []const u8 {
    const from = nyOffsetOf(text, start);
    const to = if (end > start) nyOffsetOf(text, end) else from;
    return nyListMove(u8, text[from..to]);
}

fn nyStringIndexOf(text: []const u8, value: []const u8, from: i64) ?i64 {
    const offset = nyOffsetOf(text, from);
    if (value.len == 0) return nyPositionOf(text, offset);
    return if (std.mem.indexOfPos(u8, text, offset, value)) |found| nyPositionOf(text, found) else null;
}

/// The last match that ends at or before code-point position `end`.
fn nyStringLastIndexOfBefore(text: []const u8, value: []const u8, end: i64) ?i64 {
    const offset = nyOffsetOf(text, end);
    if (value.len == 0) return nyPositionOf(text, offset);
    return if (std.mem.lastIndexOf(u8, text[0..offset], value)) |found| nyPositionOf(text, found) else null;
}

fn nyStringSpan(text: []const u8, set: []const u8, from: i64) i64 {
    var offset = nyOffsetOf(text, from);
    var position = nyPositionOf(text, offset);
    if (set.len == 0) return position;
    while (offset < text.len) : (position += 1) {
        const next = offset + nyWidth(text, offset);
        if (!nyInSet(set, text[offset..next])) break;
        offset = next;
    }
    return position;
}

fn nyStringSpanBack(text: []const u8, set: []const u8, end: i64) i64 {
    var offset = nyOffsetOf(text, end);
    var position = nyPositionOf(text, offset);
    if (set.len == 0) return position;
    while (offset > 0) : (position -= 1) {
        var previous = offset - 1;
        while (text[previous] & 0xC0 == 0x80) previous -= 1;
        if (!nyInSet(set, text[previous..offset])) break;
        offset = previous;
    }
    return position;
}

fn nyStringSplit(text: []const u8, separator: []const u8) []const []const u8 {
    if (separator.len == 0) return nyListOf([]const u8, &.{text});
    var pieces: std.ArrayList([]const u8) = .empty;
    var parts = std.mem.splitSequence(u8, text, separator);
    while (parts.next()) |piece| pieces.append(nyRaw(), nyListMove(u8, piece)) catch nyOom();
    return nyAdoptList([]const u8, &pieces);
}

/// Lines split at code points of `breaks`; "\r\n" is one break when both are members, and a final
/// break adds no trailing empty line (Python splitlines).
fn nyStringSplitLines(text: []const u8, breaks: []const u8) []const []const u8 {
    if (text.len == 0) return &.{};
    if (breaks.len == 0) return nyListOf([]const u8, &.{text});
    const crlf = nyInSet(breaks, "\r") and nyInSet(breaks, "\n");
    var lines: std.ArrayList([]const u8) = .empty;
    var start: usize = 0;
    var offset: usize = 0;
    while (offset < text.len) {
        var next = offset + nyWidth(text, offset);
        if (nyInSet(breaks, text[offset..next])) {
            lines.append(nyRaw(), nyListMove(u8, text[start..offset])) catch nyOom();
            if (crlf and text[offset] == '\r' and next < text.len and text[next] == '\n') next += 1;
            start = next;
        }
        offset = next;
    }
    if (start < text.len) lines.append(nyRaw(), nyListMove(u8, text[start..])) catch nyOom();
    return nyAdoptList([]const u8, &lines);
}

fn nyStringReplace(text: []const u8, pattern: []const u8, replacement: []const u8) []const u8 {
    if (pattern.len == 0) return nyShare(text);
    return nyAdopt(u8, std.mem.replaceOwned(u8, nyRaw(), text, pattern, replacement) catch nyOom());
}

/// The Unicode White_Space property (.NET char.IsWhiteSpace); every member is in the BMP.
fn nyIsWhiteSpace(point: []const u8) bool {
    const code = std.unicode.utf8Decode(point) catch unreachable;
    return switch (code) {
        0x09...0x0D, 0x20, 0x85, 0xA0, 0x1680, 0x2000...0x200A, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000 => true,
        else => false,
    };
}

fn nyStringTrimStart(text: []const u8) []const u8 {
    var start: usize = 0;
    while (start < text.len) {
        const next = start + nyWidth(text, start);
        if (!nyIsWhiteSpace(text[start..next])) break;
        start = next;
    }
    return if (start == 0) nyShare(text) else nyListMove(u8, text[start..]);
}

fn nyStringTrimEnd(text: []const u8) []const u8 {
    var end = text.len;
    while (end > 0) {
        var previous = end - 1;
        while (text[previous] & 0xC0 == 0x80) previous -= 1;
        if (!nyIsWhiteSpace(text[previous..end])) break;
        end = previous;
    }
    return if (end == text.len) nyShare(text) else nyListMove(u8, text[0..end]);
}

fn nyStringTrim(text: []const u8) []const u8 {
    const start = nyStringTrimStart(text);
    defer nyRelease(start);
    return nyStringTrimEnd(start);
}

fn nyStringJoin(list: []const []const u8, separator: []const u8) []const u8 {
    return nyAdopt(u8, std.mem.join(nyRaw(), separator, list) catch nyOom());
}

fn nyStringCodePoints(text: []const u8) []const []const u8 {
    const points = nyAlloc([]const u8, @intCast(nyStringLength(text)));
    var offset: usize = 0;
    for (points) |*point| {
        const next = offset + nyWidth(text, offset);
        point.* = nyListMove(u8, text[offset..next]);
        offset = next;
    }
    return points;
}

/// Python chr limited to Unicode scalar values.
fn nyStringFromCodePoint(code: i64) ?[]const u8 {
    if (code < 0 or code > 0x10FFFF or (code >= 0xD800 and code <= 0xDFFF)) return null;
    var out: [4]u8 = undefined;
    return nyListMove(u8, out[0 .. std.unicode.utf8Encode(@intCast(code), &out) catch unreachable]);
}

/// Python ord: exactly one code point.
fn nyStringToCodePoint(text: []const u8) ?i64 {
    if (text.len == 0 or nyWidth(text, 0) != text.len) return null;
    return std.unicode.utf8Decode(text) catch unreachable;
}

/// UTF-8 bytes as one code point U+0000..U+00FF each.
fn nyStringEncodeUtf8(text: []const u8) []const u8 {
    return nyBytesToLatin1(.{ .data = text });
}

/// The inverse: code points as bytes (above U+00FF reads as 0xFF), decoded with one U+FFFD per
/// maximal ill-formed subpart, as .NET and Python's decode("utf-8", "replace") do.
fn nyStringDecodeUtf8Lossy(text: []const u8) []const u8 {
    const bytes = nyBytesFromLatin1(text);
    defer nyRelease(bytes);
    return nyAdopt(u8, std.fmt.allocPrint(nyRaw(), "{f}", .{std.unicode.fmtUtf8(bytes.data)}) catch nyOom());
}

/// Python text * count; a result longer than a .NET string (2^31 - 1 UTF-16 units) fails as the
/// C# backend's allocation does.
fn nyStringRepeat(text: []const u8, count: i64) []const u8 {
    if (count <= 0 or text.len == 0) return &.{};
    const units = std.unicode.calcUtf16LeLen(text) catch unreachable;
    if (count > @divTrunc(std.math.maxInt(i32), @as(i64, @intCast(units)))) nyOom();
    const out = nyAlloc(u8, text.len * @as(usize, @intCast(count)));
    var at: usize = 0;
    while (at < out.len) : (at += text.len) @memcpy(out[at..][0..text.len], text);
    return out;
}

/// Python format(value, "x").
fn nyInt64ToHexString(value: i64) []const u8 {
    var buffer: [24]u8 = undefined;
    return nyListMove(u8, std.fmt.bufPrint(&buffer, "{s}{x}", .{ if (value < 0) "-" else "", @abs(value) }) catch unreachable);
}

/// Only '-'? ASCII digits; the Int64 range is then enforced by parseInt.
fn nyStringParseInt64(text: []const u8) ?i64 {
    const start: usize = if (text.len > 0 and text[0] == '-') 1 else 0;
    if (text.len == start) return null;
    for (text[start..]) |c| if (c < '0' or c > '9') return null;
    return std.fmt.parseInt(i64, text, 10) catch null;
}

/// Only '-'? ASCII hex digits of either case; the magnitude must be at most 2^63 - 1, or 2^63 when
/// negative.
fn nyStringParseInt64Hex(text: []const u8) ?i64 {
    const negative = text.len > 0 and text[0] == '-';
    const start: usize = if (negative) 1 else 0;
    if (text.len == start) return null;
    var magnitude: u64 = 0;
    for (text[start..]) |c| {
        const digit = std.fmt.charToDigit(c, 16) catch return null;
        if (magnitude > std.math.maxInt(u64) >> 4) return null;
        magnitude = (magnitude << 4) + digit;
    }
    if (magnitude > @as(u64, if (negative) 1 << 63 else (1 << 63) - 1)) return null;
    return if (negative) 0 -% @as(i64, @bitCast(magnitude)) else @intCast(magnitude);
}

fn nyChecked(result: anytype) ?i64 {
    return if (result[1] == 0) result[0] else null;
}

/// The shortest round-trip decimal digits of a positive finite value, without leading or trailing
/// zeros, and the point position: value = 0.d1d2... * 10^point.
const NyDecimal = struct { digits: []const u8, point: i64 };

fn nyShortest(magnitude: f64, digits: *[24]u8) NyDecimal {
    var buffer: [32]u8 = undefined;
    // "{e}" is "d.ddde±x" (or "de±x") with the shortest digits.
    const text = std.fmt.bufPrint(&buffer, "{e}", .{magnitude}) catch unreachable;
    const mark = std.mem.indexOfScalar(u8, text, 'e').?;
    digits[0] = text[0];
    const count = if (mark > 1) mark - 1 else 1;
    if (mark > 1) @memcpy(digits[1..count], text[2..mark]);
    const exponent = std.fmt.parseInt(i64, text[mark + 1 ..], 10) catch unreachable;
    return .{ .digits = digits[0..count], .point = exponent + 1 };
}

/// Python repr(float): exponent notation when point <= -4 or > 16, with a signed exponent of at
/// least two digits, otherwise fixed notation with at least one fractional digit.
fn nyFloat64ToString(value: f64) []const u8 {
    if (std.math.isNan(value)) return nyStatic("nan");
    if (std.math.isInf(value)) return if (value > 0) nyStatic("inf") else nyStatic("-inf");
    if (value == 0) return if (std.math.signbit(value)) nyStatic("-0.0") else nyStatic("0.0");
    var buffer: [24]u8 = undefined;
    const decimal = nyShortest(@abs(value), &buffer);
    const digits = decimal.digits;
    const sign = if (value < 0) "-" else "";
    const zeros = "0000000000000000";
    const point = decimal.point;
    const text = if (point <= -4 or point > 16)
        std.fmt.allocPrint(nyRaw(), "{s}{c}{s}{s}e{s}{d:0>2}", .{ sign, digits[0], if (digits.len > 1) "." else "", digits[1..], if (point - 1 < 0) "-" else "+", @abs(point - 1) })
    else if (point <= 0)
        std.fmt.allocPrint(nyRaw(), "{s}0.{s}{s}", .{ sign, zeros[0..@intCast(-point)], digits })
    else if (point >= digits.len)
        std.fmt.allocPrint(nyRaw(), "{s}{s}{s}.0", .{ sign, digits, zeros[0 .. @as(usize, @intCast(point)) - digits.len] })
    else
        std.fmt.allocPrint(nyRaw(), "{s}{s}.{s}", .{ sign, digits[0..@intCast(point)], digits[@intCast(point)..] });
    return nyAdopt(u8, text catch nyOom());
}

/// Python float(text) on the JSON number grammar only; parseFloat is correctly rounded and gives a
/// signed infinity on overflow and a signed zero on underflow.
fn nyStringParseFloat64(text: []const u8) ?f64 {
    var index: usize = if (text.len > 0 and text[0] == '-') 1 else 0;
    if (!nySkipDigits(text, &index)) return null;
    if (index < text.len and text[index] == '.') {
        index += 1;
        if (!nySkipDigits(text, &index)) return null;
    }
    if (index < text.len and (text[index] == 'e' or text[index] == 'E')) {
        index += 1;
        if (index < text.len and (text[index] == '+' or text[index] == '-')) index += 1;
        if (!nySkipDigits(text, &index)) return null;
    }
    if (index != text.len) return null;
    return std.fmt.parseFloat(f64, text) catch unreachable;
}

fn nySkipDigits(text: []const u8, index: *usize) bool {
    const start = index.*;
    while (index.* < text.len and text[index.*] >= '0' and text[index.*] <= '9') index.* += 1;
    return index.* > start;
}

/// Round(value, digits) rounds the shortest round-trip decimal text half away from zero at `digits`
/// places and reads the result back: 2.675 to 2 places is 2.68 although the binary64 is below it.
fn nyFloat64Round(value: f64, places: i64) f64 {
    if (std.math.isNan(value) or std.math.isInf(value) or value == 0) return value;
    var buffer: [24]u8 = undefined;
    const decimal = nyShortest(@abs(value), &buffer);
    const keep = decimal.point + std.math.clamp(places, -1000, 1000);
    if (keep >= decimal.digits.len) return value;
    if (keep < 0) return if (value < 0) -0.0 else 0.0;
    var kept: i64 = if (keep == 0) 0 else std.fmt.parseInt(i64, decimal.digits[0..@intCast(keep)], 10) catch unreachable;
    if (decimal.digits[@intCast(keep)] >= '5') kept += 1;
    var text: [48]u8 = undefined;
    const rounded = std.fmt.parseFloat(f64, std.fmt.bufPrint(&text, "{d}e{d}", .{ kept, decimal.point - keep }) catch unreachable) catch unreachable;
    return if (value < 0) -rounded else rounded;
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
