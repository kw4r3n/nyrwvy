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
// frees exactly; storage with spare room also records how many items it owns (`ny_spare`). A call consumes its arguments and returns an owned result; runtime helpers borrow
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
/// The top bit of `cap` marks storage with spare room whose last 4 bytes hold `used`, the number of
/// items the storage owns; without it every allocated item is owned. A list sharing the storage may
/// be shorter than `used`, and only one whose length is `used` may claim the next slot (`nyExtend`).
const ny_spare: u32 = 1 << 31;
const ny_static: u32 = std.math.maxInt(u32);

fn nyHeader(ptr: anytype) *NyHeader {
    return @ptrFromInt(@intFromPtr(ptr) - @sizeOf(NyHeader));
}

fn nyCap(header: *const NyHeader) usize {
    return header.cap & ~ny_spare;
}

fn nyUsedBytes(comptime T: type, ptr: [*]const T) *align(1) u32 {
    return @ptrFromInt(@intFromPtr(ptr) + nyCap(nyHeader(ptr)) * @sizeOf(T) - 4);
}

fn nyUsed(comptime T: type, ptr: [*]const T) usize {
    return if (nyHeader(ptr).cap & ny_spare == 0) nyCap(nyHeader(ptr)) else nyUsedBytes(T, ptr).*;
}

/// Records that the storage owns `used` items. Spare room under 4 bytes (only Strings and other
/// uncounted items smaller than 4 bytes) counts as owned, so it is never claimed.
fn nySetUsed(comptime T: type, ptr: [*]const T, used: usize) void {
    comptime std.debug.assert(!nyCounted(T) or @sizeOf(T) >= 4);
    const header = nyHeader(ptr);
    header.cap &= ~ny_spare;
    if ((header.cap - used) * @sizeOf(T) < 4) return;
    header.cap |= ny_spare;
    nyUsedBytes(T, ptr).* = @intCast(used);
}

/// Counted storage for `count` items with a count of one; no allocation for zero items.
fn nyAlloc(comptime T: type, count: usize) []T {
    comptime std.debug.assert(@alignOf(T) <= 8);
    if (count == 0) return &.{};
    if (count > std.math.maxInt(u31)) nyOom();
    const bytes = (if (ny_static_values) nyAllocator() else nyRaw()).alignedAlloc(u8, .@"8", @sizeOf(NyHeader) + count * @sizeOf(T)) catch nyOom();
    const header: *NyHeader = @ptrCast(bytes.ptr);
    header.* = .{ .count = if (ny_static_values) ny_static else 1, .cap = @intCast(count) };
    const items: [*]T = @ptrCast(@alignCast(bytes.ptr + @sizeOf(NyHeader)));
    return items[0..count];
}

/// Counted storage for `count` items with room for `cap`, so a shared copy can grow in place.
fn nyAllocCap(comptime T: type, count: usize, cap: usize) []T {
    const items = nyAlloc(T, cap);
    if (cap > count) nySetUsed(T, items.ptr, count);
    return items[0..count];
}

fn nyFree(comptime T: type, ptr: anytype) void {
    const header = nyHeader(ptr);
    const bytes: [*]align(8) u8 = @ptrCast(@alignCast(header));
    nyCursorForget(T, ptr);
    nyRaw().free(bytes[0 .. @sizeOf(NyHeader) + nyCap(header) * @sizeOf(T)]);
}

/// Drops the String cursor when it points into the storage of `ptr`, which is freed or rewritten.
fn nyCursorForget(comptime T: type, ptr: anytype) void {
    const start = @intFromPtr(ptr);
    const end = start + nyCap(nyHeader(ptr)) * @sizeOf(T);
    if (ny_cursor.ptr >= start and ny_cursor.ptr < end) ny_cursor = .{};
    if (ny_length.ptr >= start and ny_length.ptr < end) ny_length = .{};
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
        .@"struct" => if (comptime @hasDecl(T, "ny_set")) {
            nyRetain(value.items);
            nyRetain(value.index);
        } else if (comptime @hasDecl(T, "ny_map")) {
            nyRetain(value.keys);
            nyRetain(value.values);
            nyRetain(value.index);
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
            if (comptime nyCounted(info.child)) for (value.ptr[0..nyUsed(info.child, value.ptr)]) |item| nyRelease(item);
            nyFree(info.child, value.ptr);
        },
        .optional => if (value) |item| nyRelease(item),
        .@"struct" => if (comptime @hasDecl(T, "ny_set")) {
            nyRelease(value.items);
            nyRelease(value.index);
        } else if (comptime @hasDecl(T, "ny_map")) {
            nyRelease(value.keys);
            nyRelease(value.values);
            nyRelease(value.index);
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
    if (count > 0) {
        nySetUsed(T, items.ptr, count);
        return items[0..count];
    }
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

/// The low 32 bits of the two's complement form, that is `value` mod 2^32.
fn nyBits32FromInt64(value: i64) u32 {
    return @truncate(@as(u64, @bitCast(value)));
}

/// The amount is taken floor-modulo 32 (its low five bits), so a negative amount rotates left.
fn nyBits32RotateRight(value: u32, amount: i64) u32 {
    return std.math.rotr(u32, value, @as(u5, @truncate(@as(u64, @bitCast(amount)))));
}

/// Unlike Int64 shifts the amount is not masked: <= 0 keeps the value and >= 32 gives zero.
fn nyBits32ShiftRight(value: u32, amount: i64) u32 {
    if (amount <= 0) return value;
    if (amount >= 32) return 0;
    return value >> @as(u5, @intCast(amount));
}

fn nyBits32ShiftLeft(value: u32, amount: i64) u32 {
    if (amount <= 0) return value;
    if (amount >= 32) return 0;
    return value << @as(u5, @intCast(amount));
}

fn nyInt64ToString(value: i64) []const u8 {
    var buffer: [24]u8 = undefined;
    return nyListMove(u8, std.fmt.bufPrint(&buffer, "{d}", .{value}) catch unreachable);
}

/// The code-point count of the last long String measured, so a loop that tests a long String's
/// length each step counts it once. Dropped with the cursor when the storage is freed or rewritten.
var ny_length: struct { ptr: usize = 0, len: usize = 0, count: i64 = 0 } = .{};

/// Strings are UTF-8 sequences of Unicode scalar values, so this never fails.
fn nyStringLength(text: []const u8) i64 {
    if (text.len < ny_cursor_min) return @intCast(std.unicode.utf8CountCodepoints(text) catch unreachable);
    if (ny_length.ptr != @intFromPtr(text.ptr) or ny_length.len != text.len)
        ny_length = .{ .ptr = @intFromPtr(text.ptr), .len = text.len, .count = @intCast(std.unicode.utf8CountCodepoints(text) catch unreachable) };
    return ny_length.count;
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

// Lists are immutable slices. Unique lists update in place and a shared list appends in place
// (`nyExtend`). shortcut: other updates of a shared list copy (O(n)); a persistent vector, as the C#
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
/// one) is reused, grown by doubling when its spare capacity is short. A shared list whose length is
/// the storage's `used` claims its spare slots in place: every other list sharing the storage is no
/// longer and never reads past its own length. Otherwise the list is copied (its items retained,
/// with doubled room when extended) and the caller's reference dropped. So reuse is never observable.
fn nyExtend(comptime T: type, list: []const T, extra: usize) []T {
    const len = list.len + extra;
    if (list.len > 0 and nyHeader(list.ptr).count == 1) {
        const header = nyHeader(list.ptr);
        nyCursorForget(T, list.ptr);
        // Items a longer, now dead, list left past this one are owned by the storage alone.
        if (comptime nyCounted(T)) for (list.ptr[list.len..nyUsed(T, list.ptr)]) |item| nyRelease(item);
        if (nyCap(header) >= len) {
            nySetUsed(T, list.ptr, len);
            return @constCast(list.ptr)[0..len];
        }
        const cap = @min(@max(len, nyCap(header) * 2), std.math.maxInt(u31));
        if (len > cap) nyOom();
        const old: [*]align(8) u8 = @ptrCast(@alignCast(header));
        const bytes = nyRaw().realloc(old[0 .. @sizeOf(NyHeader) + nyCap(header) * @sizeOf(T)], @sizeOf(NyHeader) + cap * @sizeOf(T)) catch nyOom();
        const moved: *NyHeader = @ptrCast(bytes.ptr);
        moved.cap = @intCast(cap);
        const items: [*]T = @ptrCast(@alignCast(bytes.ptr + @sizeOf(NyHeader)));
        nySetUsed(T, items, len);
        return items[0..len];
    }
    if (list.len > 0 and extra > 0) {
        const header = nyHeader(list.ptr);
        if (header.count != ny_static and nyUsed(T, list.ptr) == list.len and nyCap(header) >= len) {
            nySetUsed(T, list.ptr, len);
            return @constCast(list.ptr)[0..len];
        }
    }
    const items = nyAllocCap(T, len, if (extra > 0 and list.len > 0) @max(len, list.len * 2) else len);
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
        // A shared prefix keeping at least half the storage's items shares it; the reference moves.
        if (from == 0 and to > 0 and to * 2 >= nyUsed(T, list.ptr)) return list[0..to];
        defer nyRelease(list);
        return nyListSlice(T, list, start, end);
    }
    const header = nyHeader(list.ptr);
    nyCursorForget(T, list.ptr);
    const items = @constCast(list.ptr)[0..nyUsed(T, list.ptr)];
    for (items[0..from]) |item| nyRelease(item);
    for (items[to..]) |item| nyRelease(item);
    if (to == from) {
        nyFree(T, list.ptr);
        return &.{};
    }
    const kept = to - from;
    std.mem.copyForwards(T, items[0..kept], items[from..to]);
    if (kept * 2 >= nyCap(header)) {
        nySetUsed(T, list.ptr, kept);
        return items[0..kept];
    }
    // Storage more than twice the kept length shrinks to it, so a small slice never pins a large list.
    const old: [*]align(8) u8 = @ptrCast(@alignCast(header));
    const bytes = nyRaw().realloc(old[0 .. @sizeOf(NyHeader) + nyCap(header) * @sizeOf(T)], @sizeOf(NyHeader) + kept * @sizeOf(T)) catch nyOom();
    const moved: *NyHeader = @ptrCast(bytes.ptr);
    moved.cap = @intCast(kept);
    const shrunk: [*]T = @ptrCast(@alignCast(bytes.ptr + @sizeOf(NyHeader)));
    return shrunk[0..kept];
}

/// Moves a field out of a unique box whose next update overwrites it (main.zig
/// `moveOutUpdatedFields`): the box keeps an empty value, which that update releases (a no-op).
fn nyTakeField(slot: anytype) @TypeOf(slot.*) {
    const value = slot.*;
    slot.* = switch (@typeInfo(@TypeOf(value))) {
        .pointer => &.{},
        .@"struct" => |info| blk: {
            var empty: @TypeOf(value) = undefined;
            inline for (info.field_names) |name| @field(empty, name) = &.{};
            break :blk empty;
        },
        else => @compileError("a moved field is a List, String, Bytes, Set or Map"),
    };
    return value;
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

/// Drops the last item of an owned `list`; a unique list keeps its storage.
fn nyListDropLastOwned(comptime T: type, list: []const T) []const T {
    return nyListSliceOwned(T, list, 0, @as(i64, @intCast(list.len)) - 1);
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

/// Insertion that consumes `list` (reused when unique) and borrows `item`.
fn nyListInsertAtOwned(comptime T: type, list: []const T, index: i64, item: T) []const T {
    const at = nyClamp(index, list.len);
    // A shared list may claim spare slots in `nyExtend`, but its holders must not see items shift.
    if (list.len == 0 or nyHeader(list.ptr).count != 1) {
        defer nyRelease(list);
        const copy = nyAlloc(T, list.len + 1);
        nyCopyItems(T, copy[0..at], list[0..at]);
        copy[at] = nyShare(item);
        nyCopyItems(T, copy[at + 1 ..], list[at..]);
        return copy;
    }
    const items = nyExtend(T, list, 1);
    std.mem.copyBackwards(T, items[at + 1 ..], items[at .. items.len - 1]);
    items[at] = nyShare(item);
    return items;
}

/// Removal that consumes `list`: a unique list closes the gap in place, a shared one is copied.
fn nyListRemoveAtOwned(comptime T: type, list: []const T, index: i64) []const T {
    const at = nyIndex(index, list.len) orelse return list;
    if (list.len == 1) {
        nyRelease(list);
        return &.{};
    }
    if (nyHeader(list.ptr).count != 1) {
        defer nyRelease(list);
        const items = nyAlloc(T, list.len - 1);
        nyCopyItems(T, items[0..at], list[0..at]);
        nyCopyItems(T, items[at..], list[at + 1 ..]);
        return items;
    }
    nyCursorForget(T, list.ptr);
    const items = @constCast(list.ptr)[0..nyUsed(T, list.ptr)];
    nyRelease(items[at]);
    // Items a longer, now dead, list left past this one are owned by the storage alone.
    for (items[list.len..]) |item| nyRelease(item);
    std.mem.copyForwards(T, items[at .. list.len - 1], items[at + 1 .. list.len]);
    nySetUsed(T, list.ptr, list.len - 1);
    return items[0 .. list.len - 1];
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

// Sets and Maps are insertion-ordered immutable values (decision D): the items (a Map's keys) in a
// dense array plus, past `nyIndexMin` items, a hash index of their positions. The index is counted
// storage of power-of-two slots, each 0 (empty) or a position + 1, at most half full (linear
// probing), and covers exactly the items of the value holding it. It is updated in place only when
// its count is one, and otherwise rebuilt, so a shared update is O(n). Empty means no index: lookups
// scan, which is faster for a few items. Removing the last item is O(1) when unique. Removing an
// earlier one compacts the items, so it is O(n): a unique index drops the slot and renumbers in place
// without hashing, a shared one is rebuilt. shortcut: dense storage keeps front and middle removal
// O(n); use tombstones if a queue-like drain of a large Set or Map is slow.
fn NySet(comptime T: type) type {
    return struct {
        items: []const T,
        index: []const u32 = &.{},
        pub const ny_set = true;
    };
}

fn NyMap(comptime K: type, comptime V: type) type {
    return struct {
        keys: []const K,
        values: []const V,
        index: []const u32 = &.{},
        pub const ny_map = true;
    };
}

/// Sets and Maps of up to this many items have no index, as a scan is faster (measured in
/// docs/verification/zig-reference-counting-e-20261102.md): 32 scalars, 8 of anything compared through memory.
fn nyIndexMin(comptime T: type) usize {
    return switch (@typeInfo(T)) {
        .int, .bool, .float => 32,
        else => 8,
    };
}

fn nyFind(comptime T: type, items: []const T, item: T) ?usize {
    for (items, 0..) |candidate, position| if (nyEqual(candidate, item)) return position;
    return null;
}

/// The position of `item` in `items`, through `index` when there is one.
fn nyLookup(comptime T: type, items: []const T, index: []const u32, item: T) ?usize {
    return if (index.len == 0) nyFind(T, items, item) else nyProbe(T, items, index, item);
}

// Out of line, as is all index upkeep, so the scan of a small Set or Map stays inlined.
noinline fn nyProbe(comptime T: type, items: []const T, index: []const u32, item: T) ?usize {
    var at: usize = @truncate(nyHash(item));
    while (true) : (at +%= 1) {
        const slot = index[at & (index.len - 1)];
        if (slot == 0) return null;
        if (nyEqual(items[slot - 1], item)) return slot - 1;
    }
}

fn nySlotPut(slots: []u32, hash: u64, position: usize) void {
    var at: usize = @truncate(hash);
    while (slots[at & (slots.len - 1)] != 0) at +%= 1;
    slots[at & (slots.len - 1)] = @intCast(position + 1);
}

/// A fresh index of distinct `items`, none for `nyIndexMin(T)` items or fewer.
noinline fn nyIndexBuild(comptime T: type, items: []const T) []const u32 {
    if (items.len <= nyIndexMin(T)) return &.{};
    const slots = nyAlloc(u32, std.math.ceilPowerOfTwoAssert(usize, items.len * 2 + 1));
    @memset(slots, 0);
    for (items, 0..) |item, position| nySlotPut(slots, nyHash(item), position);
    return slots;
}

/// Consumes the index of `items` without its last item and returns the index of `items`.
fn nyIndexAdd(comptime T: type, index: []const u32, items: []const T) []const u32 {
    if (index.len == 0 and items.len <= nyIndexMin(T)) return index;
    return nyIndexGrow(T, index, items);
}

noinline fn nyIndexGrow(comptime T: type, index: []const u32, items: []const T) []const u32 {
    if (index.len == 0 or nyHeader(index.ptr).count != 1 or items.len * 2 > index.len) {
        nyRelease(index);
        return nyIndexBuild(T, items);
    }
    nySlotPut(@constCast(index), nyHash(items[items.len - 1]), items.len - 1);
    return index;
}

/// Consumes the index of `kept` plus the item that was at position `at` (whose hash is `hash`), and
/// returns the index of `kept`. A unique index frees that item's slot, renumbers the positions past
/// `at` (none when it was the last), and closes the slot by backward shift (linear probing),
/// rehashing only the cluster after it.
noinline fn nyIndexRemove(comptime T: type, index: []const u32, kept: []const T, at: usize, hash: u64) []const u32 {
    if (index.len == 0) return index;
    // A drained set shrinks its index: a built one is at most 4x its items, so this rebuilds once
    // per 2x shrink and later removals renumber a small index.
    if (kept.len <= nyIndexMin(T) or nyHeader(index.ptr).count != 1 or index.len > kept.len * 8) {
        nyRelease(index);
        return nyIndexBuild(T, kept);
    }
    const slots = @constCast(index);
    const mask = slots.len - 1;
    var hole: usize = @truncate(hash);
    while (slots[hole & mask] != at + 1) hole +%= 1;
    hole &= mask;
    slots[hole] = 0;
    if (at < kept.len) for (slots) |*slot| {
        if (slot.* > at + 1) slot.* -= 1;
    };
    var next = hole;
    while (true) {
        next = (next + 1) & mask;
        const slot = slots[next];
        if (slot == 0) break;
        const home = @as(usize, @truncate(nyHash(kept[slot - 1]))) & mask;
        // An entry stays when its home lies cyclically in (hole, next]; otherwise it fills the hole.
        const stays = if (hole <= next) home > hole and home <= next else home > hole or home <= next;
        if (stays) continue;
        slots[hole] = slot;
        slots[next] = 0;
        hole = next;
    }
    return index;
}

fn nySetFind(comptime T: type, set: NySet(T), item: T) ?usize {
    return nyLookup(T, set.items, set.index, item);
}

fn nyMapFind(comptime K: type, comptime V: type, map: NyMap(K, V), key: K) ?usize {
    return nyLookup(K, map.keys, map.index, key);
}

/// A set of owned, distinct items.
fn nySetOf(comptime T: type, items: []const T) NySet(T) {
    return .{ .items = items, .index = nyIndexBuild(T, items) };
}

/// Inserts into a set this call owns (reused when unique), borrowing the item.
inline fn nySetInsertInto(comptime T: type, set: NySet(T), item: T) NySet(T) {
    if (nySetFind(T, set, item) != null) return set;
    const items = nyListAppendOwned(T, set.items, item);
    return .{ .items = items, .index = nyIndexAdd(T, set.index, items) };
}

/// Inserts into a set this call owns, consuming both the set and the item.
inline fn nySetInsertOwned(comptime T: type, set: NySet(T), item: T) NySet(T) {
    defer nyRelease(item);
    return nySetInsertInto(T, set, item);
}

/// Removal from a set this call owns (reused when unique), borrowing the item.
fn nySetRemoveOwned(comptime T: type, set: NySet(T), item: T) NySet(T) {
    const at = nySetFind(T, set, item) orelse return set;
    const hash = nyHash(set.items[at]);
    const items = nyListRemoveAtOwned(T, set.items, @intCast(at));
    return .{ .items = items, .index = nyIndexRemove(T, set.index, items, at, hash) };
}

/// The distinct items in first-seen order.
fn nySetFromList(comptime T: type, items: []const T) NySet(T) {
    var set: NySet(T) = .{ .items = &.{} };
    for (items) |item| set = nySetInsertInto(T, set, item);
    return set;
}

fn nySetUnion(comptime T: type, left: NySet(T), right: NySet(T)) NySet(T) {
    var set = nyShare(left);
    for (right.items) |item| set = nySetInsertInto(T, set, item);
    return set;
}

fn nySetKeep(comptime T: type, left: NySet(T), right: NySet(T), present: bool) NySet(T) {
    const out = nyAlloc(T, left.items.len);
    var count: usize = 0;
    for (left.items) |item| {
        if ((nySetFind(T, right, item) != null) == present) {
            out[count] = nyShare(item);
            count += 1;
        }
    }
    return nySetOf(T, nyShrink(T, out, count));
}

fn nySetIsSubset(comptime T: type, left: NySet(T), right: NySet(T)) bool {
    if (left.items.len > right.items.len) return false;
    for (left.items) |item| if (nySetFind(T, right, item) == null) return false;
    return true;
}

/// Inserts into a map this call owns (keys and values each reused when unique), borrowing the key and value.
inline fn nyMapInsertInto(comptime K: type, comptime V: type, map: NyMap(K, V), key: K, value: V) NyMap(K, V) {
    if (nyMapFind(K, V, map, key)) |at| return .{ .keys = map.keys, .index = map.index, .values = nyListSetAtOwned(V, map.values, @intCast(at), value) };
    const keys = nyListAppendOwned(K, map.keys, key);
    return .{ .keys = keys, .index = nyIndexAdd(K, map.index, keys), .values = nyListAppendOwned(V, map.values, value) };
}

/// Inserts into a map this call owns, consuming the map, the key and the value.
inline fn nyMapInsertOwned(comptime K: type, comptime V: type, map: NyMap(K, V), key: K, value: V) NyMap(K, V) {
    defer nyRelease(key);
    defer nyRelease(value);
    return nyMapInsertInto(K, V, map, key, value);
}

/// Removal from a map this call owns (keys, values and index each reused when unique), borrowing the key.
fn nyMapRemoveOwned(comptime K: type, comptime V: type, map: NyMap(K, V), key: K) NyMap(K, V) {
    const at = nyMapFind(K, V, map, key) orelse return map;
    const hash = nyHash(map.keys[at]);
    const keys = nyListRemoveAtOwned(K, map.keys, @intCast(at));
    return .{ .keys = keys, .index = nyIndexRemove(K, map.index, keys, at, hash), .values = nyListRemoveAtOwned(V, map.values, @intCast(at)) };
}

fn nyMapGet(comptime K: type, comptime V: type, map: NyMap(K, V), key: K) ?V {
    return if (nyMapFind(K, V, map, key)) |at| nyShare(map.values[at]) else null;
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

/// Consumes `bytes`, so a unique buffer is written in place.
fn nyBytesSet(bytes: NyBytes, index: i64, value: i64) NyBytes {
    return .{ .data = nyListSetAtOwned(u8, bytes.data, index, @truncate(@as(u64, @bitCast(value)))) };
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

/// The last (offset, position) pair reached in one String, so a loop that steps a position over that
/// String walks only the step (spec: positional String operations are near-constant time after the
/// first call). One thread runs at a time; storage that is freed or rewritten in place drops it
/// (`nyCursorForget`). shortcut: one global cursor, so alternating between distant positions or several
/// large Strings walks again; upgrade to per-String checkpoints (C# `MapOf`) when such a pattern shows up.
var ny_cursor: struct { ptr: usize = 0, len: usize = 0, offset: usize = 0, position: i64 = 0 } = .{};

/// Strings shorter than this are walked from byte 0 and leave the cursor alone, so a lookup table read
/// inside a loop over a long String does not take the cursor from it.
const ny_cursor_min = 256;

fn nyCursorStart(text: []const u8) void {
    if (ny_cursor.ptr == @intFromPtr(text.ptr) and ny_cursor.len == text.len) return;
    ny_cursor = .{ .ptr = @intFromPtr(text.ptr), .len = text.len };
}

fn nyCursorBack(text: []const u8) void {
    var previous = ny_cursor.offset - 1;
    while (text[previous] & 0xC0 == 0x80) previous -= 1;
    ny_cursor.offset = previous;
    ny_cursor.position -= 1;
}

/// The byte offset of a code-point position clamped to [0, length].
fn nyOffsetOf(text: []const u8, position: i64) usize {
    if (position <= 0 or text.len == 0) return 0;
    if (text.len < ny_cursor_min) {
        var offset: usize = 0;
        var reached: i64 = 0;
        while (reached < position and offset < text.len) : (reached += 1) offset += nyWidth(text, offset);
        return offset;
    }
    nyCursorStart(text);
    if (position < @divTrunc(ny_cursor.position, 2)) ny_cursor = .{ .ptr = ny_cursor.ptr, .len = text.len };
    while (ny_cursor.position < position and ny_cursor.offset < text.len) {
        ny_cursor.offset += nyWidth(text, ny_cursor.offset);
        ny_cursor.position += 1;
    }
    while (ny_cursor.position > position) nyCursorBack(text);
    return ny_cursor.offset;
}

/// The code-point position of a code-point boundary `offset`.
fn nyPositionOf(text: []const u8, offset: usize) i64 {
    if (offset == 0) return 0;
    if (text.len < ny_cursor_min) return nyStringLength(text[0..offset]);
    nyCursorStart(text);
    if (offset < ny_cursor.offset / 2) ny_cursor = .{ .ptr = ny_cursor.ptr, .len = text.len };
    while (ny_cursor.offset < offset) {
        ny_cursor.offset += nyWidth(text, ny_cursor.offset);
        ny_cursor.position += 1;
    }
    while (ny_cursor.offset > offset) nyCursorBack(text);
    return ny_cursor.position;
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
                for (left.items) |item| if (nySetFind(@TypeOf(item), right, item) == null) return false;
                return true;
            }
            if (comptime @hasDecl(T, "ny_map")) {
                if (left.keys.len != right.keys.len) return false;
                for (left.keys, left.values) |key, value| {
                    const at = nyMapFind(@TypeOf(key), @TypeOf(value), right, key) orelse return false;
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

/// A structural hash consistent with `nyEqual`: equal values hash equally. Floats hash as equality
/// compares them (every NaN alike, 0.0 as -0.0), and Sets and Maps independently of insertion order.
fn nyHash(value: anytype) u64 {
    const T = @TypeOf(value);
    switch (@typeInfo(T)) {
        .int => return nyMix(@truncate(@as(u128, @bitCast(@as(i128, value))))),
        .bool => return if (value) 0x2545f4914f6cdd1d else 0x9e3779b97f4a7c15,
        .float => return nyMix(if (std.math.isNan(value)) 0x7ff8000000000000 else if (value == 0) 0 else @as(u64, @bitCast(@as(f64, value)))),
        .optional => return if (value) |item| nyCombine(1, nyHash(item)) else 0x6a09e667f3bcc909,
        .pointer => |pointer| {
            if (pointer.size == .slice and pointer.child == u8) return std.hash.Wyhash.hash(0, value);
            if (pointer.size == .slice) {
                var hash = nyMix(value.len);
                for (value) |item| hash = nyCombine(hash, nyHash(item));
                return hash;
            }
            return nyHash(value.*);
        },
        .@"struct" => |info| {
            if (comptime @hasDecl(T, "ny_bytes")) return std.hash.Wyhash.hash(0, value.data);
            if (comptime @hasDecl(T, "ny_set")) {
                var hash = nyMix(value.items.len);
                for (value.items) |item| hash +%= nyHash(item);
                return hash;
            }
            if (comptime @hasDecl(T, "ny_map")) {
                var hash = nyMix(value.keys.len);
                for (value.keys, value.values) |key, item| hash +%= nyCombine(nyHash(key), nyHash(item));
                return hash;
            }
            var hash: u64 = 0xbb67ae8584caa73b;
            inline for (info.field_names) |name| hash = nyCombine(hash, nyHash(@field(value, name)));
            return hash;
        },
        .@"union" => return switch (value) {
            inline else => |item, tag| nyCombine(@intFromEnum(tag), nyHash(item)),
        },
        else => @compileError("nyHash does not support " ++ @typeName(T)),
    }
}

/// MurmurHash3's 64-bit finaliser: every input bit reaches the low bits the index masks.
fn nyMix(input: u64) u64 {
    var hash = input;
    hash ^= hash >> 33;
    hash *%= 0xff51afd7ed558ccd;
    hash ^= hash >> 33;
    hash *%= 0xc4ceb9fe1a85ec53;
    return hash ^ (hash >> 33);
}

fn nyCombine(seed: u64, hash: u64) u64 {
    return nyMix(seed *% 0x9e3779b97f4a7c15 +% hash);
}
