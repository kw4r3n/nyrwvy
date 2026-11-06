// Hand-written replacement for the body of layoutFrameFlat(rows, narrow, wide) = layoutFrame(rows, CellGrid{narrow, wide}).
// Agrees with the Nyr body: cell code 3 is a table separator, 2 a wide cell, 0 a zero-width cell, anything else narrow.
// A row without a separator gets running positions (n + 1 entries). Consecutive rows with a separator form a block:
// every row's segment widths (the cell widths between separators) are merged by a column-wise max over the rows that
// match the first row's segment count, and each matching row is then aligned so a separator starts at the widest
// segment's end; a row with another segment count gets running positions. Additions wrap like int64WrappingAdd.
const std = @import("std");

fn units(code: i64, narrow: i64, wide: i64) i64 {
    return if (code == 2) wide else if (code == 0) 0 else narrow;
}

fn isTable(row: []const i64) bool {
    for (row) |code| if (code == 3) return true;
    return false;
}

fn plain(allocator: std.mem.Allocator, row: []const i64, narrow: i64, wide: i64) ![]const i64 {
    const xs = try allocator.alloc(i64, row.len + 1);
    var x: i64 = 0;
    for (row, 0..) |code, i| {
        xs[i] = x;
        x +%= units(code, narrow, wide);
    }
    xs[row.len] = x;
    return xs;
}

fn segments(row: []const i64) usize {
    var n: usize = 1;
    for (row) |code| n += @intFromBool(code == 3);
    return n;
}

fn widths(out: []i64, row: []const i64, narrow: i64, wide: i64) void {
    var at: usize = 0;
    var cur: i64 = 0;
    for (row) |code| {
        if (code == 3) {
            out[at] = cur;
            at += 1;
            cur = 0;
        } else cur +%= units(code, narrow, wide);
    }
    out[at] = cur;
}

fn align_(allocator: std.mem.Allocator, row: []const i64, targets: []const i64, narrow: i64, wide: i64) ![]const i64 {
    const xs = try allocator.alloc(i64, row.len + 1);
    var seg: usize = 0;
    var x: i64 = 0;
    var start: i64 = 0;
    for (row, 0..) |code, i| {
        if (code == 3) {
            const p = @max(x, start +% (if (seg < targets.len) targets[seg] else 0));
            xs[i] = p;
            seg += 1;
            x = p +% narrow;
            start = x;
        } else {
            xs[i] = x;
            x +%= units(code, narrow, wide);
        }
    }
    xs[row.len] = x;
    return xs;
}

pub fn layoutFrameFlat(allocator: std.mem.Allocator, rows: []const []const i64, narrow: i64, wide: i64) error{OutOfMemory}![]const []const i64 {
    const out = try allocator.alloc([]const i64, rows.len);
    var i: usize = 0;
    while (i < rows.len) {
        if (!isTable(rows[i])) {
            out[i] = try plain(allocator, rows[i], narrow, wide);
            i += 1;
            continue;
        }
        var end = i;
        while (end < rows.len and isTable(rows[end])) end += 1;
        const count = segments(rows[i]);
        const targets = try allocator.alloc(i64, count);
        widths(targets, rows[i], narrow, wide);
        const scratch = try allocator.alloc(i64, count);
        for (rows[i + 1 .. end]) |row| {
            if (segments(row) != count) continue;
            widths(scratch, row, narrow, wide);
            for (targets, scratch) |*t, w| t.* = @max(t.*, w);
        }
        for (rows[i..end], i..) |row, at| {
            out[at] = if (segments(row) == count) try align_(allocator, row, targets, narrow, wide) else try plain(allocator, row, narrow, wide);
        }
        i = end;
    }
    return out;
}
