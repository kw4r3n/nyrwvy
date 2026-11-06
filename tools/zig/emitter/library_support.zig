// Library mode's C ABI (docs/decisions/zig-backend-memory-ownership.md, section 3 and the first
// milestone). Arguments are borrowed for the call (copied into counted values the call consumes;
// values live in the arena, `ny_values` stays null); a result is copied into caller buffers and the
// arena is then reset, so memory per call stays bounded. One mutex serialises every export. Status:
// 0 ok, 1 fault and 2 cancelled (message through `ny_last_failure`), 3 a result buffer too small
// (the lengths written say what is needed). OOM, stack overflow and safety panics still abort.

var ny_export_lock: std.Io.Mutex = .init;
threadlocal var ny_export_failure: []u8 = &.{};

fn nyExportEnter() void {
    // Real OS futexes, so the lock blocks across threads; this Io offers no concurrency of its own.
    const io = std.Io.Threaded.global_single_threaded.io();
    ny_export_lock.lockUncancelable(io);
    ny_io = io;
}

fn nyExportLeave() void {
    _ = ny_arena.reset(.retain_capacity);
    ny_export_lock.unlock(ny_io);
}

fn nyExportFail() c_int {
    std.heap.page_allocator.free(ny_export_failure);
    ny_export_failure = std.heap.page_allocator.dupe(u8, ny_failure.message) catch &.{};
    return if (ny_failure.cancelled) 2 else 1;
}

/// The message of this thread's last failed call; valid until its next failed call.
export fn ny_last_failure(len: *usize) [*]const u8 {
    len.* = ny_export_failure.len;
    return ny_export_failure.ptr;
}

/// Bytes the runtime arena keeps between calls; flat across repeated calls of the same size.
export fn ny_arena_capacity() usize {
    nyExportEnter();
    defer ny_export_lock.unlock(ny_io);
    return ny_arena.queryCapacity();
}

// Host declarations (`<graph>.zig.json`) take the arena and plain Zig values and know no runtime type.
// Their results are copied to counted storage; the arena is reset when the export returns.
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
            const out = nyAlloc(info.child, value.len);
            for (out, value) |*slot, item| slot.* = try nyHostValue(info.child, item);
            return out;
        },
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

// The runtime assumes valid UTF-8 (`catch unreachable`, `initUnchecked`), so text from C is checked here.
fn nyText(ptr: ?[*]const u8, len: usize) ?[]const u8 {
    const text = nySlice(u8, ptr, len);
    if (std.unicode.utf8ValidateSlice(text)) return text;
    nyRelease(text);
    return null;
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

