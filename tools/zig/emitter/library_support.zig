// Library mode's C ABI (docs/decisions/zig-backend-memory-ownership.md, section 3 and the first
// milestone; docs/decisions/zig-stateful-exports.md, slice 1). Arguments are borrowed for the call
// (copied into counted values the call consumes); a result is copied into caller buffers and then
// released. Values are counted in `ny_values` (`smp_allocator` unless a harness set a checking one);
// `ny_arena` keeps only runtime records. One mutex serialises every export, released around an
// operation that may wait (slice 2). Status: 0 ok, 1 fault and 2 cancelled (message through
// `ny_last_failure`), 3 a result buffer too small (the lengths written say what is needed), 4 `Err` of
// a `Result<_, String>` result (its text through `ny_last_failure`). OOM, stack overflow and safety
// panics still abort.

var ny_export_lock: std.Io.Mutex = .init;
threadlocal var ny_export_failure: []u8 = &.{};

fn nyExportEnter() void {
    // Real OS futexes, so the lock blocks across threads; this Io offers no concurrency of its own.
    const io = std.Io.Threaded.global_single_threaded.io();
    ny_export_lock.lockUncancelable(io);
    ny_io = io;
    if (ny_values == null) ny_values = std.heap.smp_allocator;
}

fn nyExportLeave() void {
    ny_export_lock.unlock(ny_io);
}

// A declaration bound to an operation that may wait runs with the lock released (memory ownership,
// section 4). Counts change only under the lock and the frame's references pin the arguments.
fn nyExportUnlock() void {
    ny_export_lock.unlock(ny_io);
}

fn nyExportRelock() void {
    ny_export_lock.lockUncancelable(std.Io.Threaded.global_single_threaded.io());
}

fn nyExportFail() c_int {
    std.heap.page_allocator.free(ny_export_failure);
    ny_export_failure = std.heap.page_allocator.dupe(u8, ny_failure.message) catch &.{};
    return if (ny_failure.cancelled) 2 else 1;
}

fn nyExportErr(result: anytype) c_int {
    _ = nyExportInvalid(result.err);
    nyRelease(result);
    return 4;
}

// A handle capability's record: the host holds a pointer to it, and Nyr values copy it freely. It is
// never freed, so use after close is the defined failure below, never a dangling pointer.
// shortcut: one small record leaks per handle ever opened; free it, with an ABI saying when, if a
// program opens enough handles to matter.
const NyHandle = struct { open: bool, tag: usize, state: *anyopaque };
var ny_open_count: usize = 0;

fn nyHandleNew(tag: usize, state: *anyopaque) *NyHandle {
    const record = std.heap.smp_allocator.create(NyHandle) catch nyOom();
    record.* = .{ .open = true, .tag = tag, .state = state };
    ny_open_count += 1;
    return record;
}

fn nyHandleIs(handle: ?*NyHandle, tag: usize) bool {
    return if (handle) |record| record.tag == tag else false;
}

fn nyHandleUse(handle: ?*NyHandle) NyError!*anyopaque {
    const record = handle orelse return nyFail("HostFailureException", "Handle is not open", false);
    if (!record.open) return nyFail("HostFailureException", "Handle is not open", false);
    return record.state;
}

/// Closing clears the record before the declaration runs, so a second close is the same failure.
fn nyHandleClose(handle: ?*NyHandle) NyError!*anyopaque {
    const state = try nyHandleUse(handle);
    handle.?.open = false;
    ny_open_count -= 1;
    return state;
}

/// Handles opened and not yet closed.
export fn ny_open_handles() usize {
    nyExportEnter();
    defer nyExportLeave();
    return ny_open_count;
}

/// The message of this thread's last failed call or `Err`; valid until its next one.
export fn ny_last_failure(len: *usize) [*]const u8 {
    len.* = ny_export_failure.len;
    return ny_export_failure.ptr;
}

/// Bytes the runtime arena holds: runtime records only, values are not in it.
export fn ny_arena_capacity() usize {
    nyExportEnter();
    defer ny_export_lock.unlock(ny_io);
    return ny_arena.queryCapacity();
}

// Host declarations (`<graph>.zig.json`) take a per-call scratch arena and plain Zig values and know no
// runtime type. Their results are copied to counted storage before the scratch is freed.
fn nyHostValue(comptime T: type, value: anytype) NyError!T {
    if (T == NyUnit) return .{};
    if (T == NyBytes) return .{ .data = nyListMove(u8, value) };
    if (T == []const u8) {
        if (!std.unicode.utf8ValidateSlice(value)) return nyFail("HostFailureException", "A host declaration returned text that is not UTF-8.", false);
        return nyListMove(u8, value);
    }
    switch (@typeInfo(T)) {
        .optional => |info| return if (value) |item| try nyHostValue(info.child, item) else null,
        .pointer => |info| {
            if (info.size == .one) return nyHostProduct(info.child, value);
            const out = nyAlloc(info.child, value.len);
            var filled: usize = 0;
            errdefer {
                for (out[0..filled]) |item| nyRelease(item);
                if (out.len > 0) nyFree(info.child, out.ptr);
            }
            for (out, value) |*slot, item| {
                slot.* = try nyHostValue(info.child, item);
                filled += 1;
            }
            return out;
        },
        // A handle capability: the declaration returned a pointer to its state.
        .@"struct" => return if (@hasDecl(T, "ny_tag")) .{ .fake = 0, .site = 0, .implementation = T.ny_tag, .handle = nyHandleNew(T.ny_tag, value) } else value,
        else => return value,
    }
}

// An error is `Err` with its name for a `Result<_, String>` operation, otherwise a host failure.
fn nyHost(comptime R: type, outcome: anytype) NyError!R {
    if (@typeInfo(R) == .@"union") {
        const value = outcome catch |failure| return .{ .err = nyListMove(u8, @errorName(failure)) };
        return .{ .ok = try nyHostValue(@FieldType(R, "ok"), value) };
    }
    if (@typeInfo(@TypeOf(outcome)) != .error_union) return nyHostValue(R, outcome);
    const value = outcome catch |failure| return nyFail("HostFailureException", @errorName(failure), false);
    return nyHostValue(R, value);
}

fn nySlice(comptime T: type, ptr: ?[*]const T, len: usize) []const T {
    return if (len == 0) &.{} else nyListMove(T, ptr.?[0..len]);
}

// The runtime assumes valid UTF-8 (`catch unreachable`, `initUnchecked`), so text from C is checked
// before any argument is copied: an early return would leak the copies made so far.
fn nyValidText(ptr: ?[*]const u8, len: usize) bool {
    return len == 0 or std.unicode.utf8ValidateSlice(ptr.?[0..len]);
}

/// Every row of a `List<String>` argument (concatenated bytes plus one length per row) is checked
/// the same way, before `nyRows` copies any of them.
fn nyValidRows(ptr: ?[*]const u8, row_lens: ?[*]const usize, rows: usize) bool {
    var at: usize = 0;
    for (0..rows) |index| {
        const len = row_lens.?[index];
        if (len > 0 and !std.unicode.utf8ValidateSlice(ptr.?[at..][0..len])) return false;
        at += len;
    }
    return true;
}

fn nyExportInvalid(message: []const u8) c_int {
    std.heap.page_allocator.free(ny_export_failure);
    ny_export_failure = std.heap.page_allocator.dupe(u8, message) catch &.{};
    return 1;
}

fn nyRows(comptime T: type, ptr: ?[*]const T, row_lens: ?[*]const usize, rows: usize) []const []const T {
    const out = nyAlloc([]const T, rows);
    var at: usize = 0;
    for (out, 0..) |*row, index| {
        const len = row_lens.?[index];
        row.* = if (len == 0) &.{} else nyListMove(T, ptr.?[at..][0..len]);
        at += len;
    }
    return out;
}

fn nyCopyOut(comptime T: type, items: []const T, out: ?[*]T, cap: usize, out_len: *usize) bool {
    out_len.* = items.len;
    if (items.len > cap) return false;
    if (items.len > 0) @memcpy(out.?[0..items.len], items);
    return true;
}

fn nyCopyRows(comptime T: type, rows: []const []const T, out: ?[*]T, cap: usize, out_len: *usize, out_row_lens: ?[*]usize, row_cap: usize, out_rows: *usize) bool {
    var total: usize = 0;
    for (rows) |row| total += row.len;
    out_len.* = total;
    out_rows.* = rows.len;
    if (total > cap or rows.len > row_cap) return false;
    var at: usize = 0;
    for (rows, 0..) |row, index| {
        out_row_lens.?[index] = row.len;
        if (row.len > 0) @memcpy(out.?[at..][0..row.len], row);
        at += row.len;
    }
    return true;
}

