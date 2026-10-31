//! Process-wide stable IDs, matching `StableIdAllocator` (including the test-only restart guard).
const std = @import("std");

pub const warning = "Warning: NYRWVY_TEST_ID_SEED is set, so new IDs are deterministic test IDs. Use it only on test copies.";
const epoch = 1_700_000_000_000;
var host_io: std.Io = undefined;
var test_seed: ?[]const u8 = null;
var allocated: u64 = 0;

/// Called once at startup; the environment and its strings live for the process.
pub fn init(io: std.Io, seed: ?[]const u8) void {
    host_io = io;
    test_seed = if (seed) |s| (if (s.len == 0) null else s) else null;
    if (test_seed != null) std.debug.print("{s}\n", .{warning});
}

fn payload(milliseconds: u64, random: [10]u8) [36]u8 {
    var bytes: [16]u8 = undefined;
    for (0..6) |i| bytes[i] = @truncate(milliseconds >> @as(u6, @intCast(8 * (5 - i))));
    bytes[6] = 0x70 | (random[0] & 0x0f);
    bytes[7] = random[1];
    bytes[8] = 0x80 | (random[2] & 0x3f);
    @memcpy(bytes[9..16], random[3..10]);
    const hex = "0123456789abcdef";
    var out: [36]u8 = undefined;
    var at: usize = 0;
    for (bytes, 0..) |byte, i| {
        if (i == 4 or i == 6 or i == 8 or i == 10) {
            out[at] = '-';
            at += 1;
        }
        out[at] = hex[byte >> 4];
        out[at + 1] = hex[byte & 15];
        at += 2;
    }
    return out;
}

pub fn seeded(seed: []const u8, n: u64) [36]u8 {
    var number: [20]u8 = undefined;
    const decimal = std.fmt.bufPrint(&number, "{d}", .{n}) catch unreachable;
    var hash = std.crypto.hash.sha2.Sha256.init(.{});
    hash.update(seed);
    hash.update(":");
    hash.update(decimal);
    var digest: [32]u8 = undefined;
    hash.final(&digest);
    return payload(epoch + n, digest[0..10].*);
}

/// Scan precisely the lowercase UUIDv7 regex used by F#, without requiring token boundaries.
pub fn observe(text: []const u8) void {
    const seed = test_seed orelse return;
    var at: usize = 0;
    while (at + 36 <= text.len) {
        const candidate = text[at..][0..36];
        var valid = true;
        var milliseconds: u64 = 0;
        for (candidate, 0..) |c, i| {
            if (i == 8 or i == 13 or i == 18 or i == 23) {
                if (c != '-') valid = false;
            } else {
                if (!(c >= '0' and c <= '9') and !(c >= 'a' and c <= 'f')) valid = false;
                if (i < 13) milliseconds = milliseconds * 16 + (std.fmt.charToDigit(c, 16) catch 0);
            }
        }
        valid = valid and candidate[14] == '7' and std.mem.indexOfScalar(u8, "89ab", candidate[19]) != null;
        if (valid) {
            if (milliseconds > epoch) {
                const n = milliseconds - epoch;
                const expected = seeded(seed, n);
                if (std.mem.eql(u8, candidate, &expected)) allocated = @max(allocated, n);
            }
            at += 36; // Regex.Matches is non-overlapping, even for another seed's payload.
        } else at += 1;
    }
}

pub fn next() ![36]u8 {
    if (test_seed) |seed| {
        allocated += 1;
        return seeded(seed, allocated);
    }
    var random: [10]u8 = undefined;
    host_io.randomSecure(&random) catch return error.RandomUnavailable;
    const milliseconds: u64 = @intCast(std.Io.Clock.real.now(host_io).toMilliseconds());
    return payload(milliseconds, random);
}

test "seeded UUIDv7 payloads and restart guard" {
    const first = seeded("parity", 1);
    const second = seeded("parity", 2);
    try std.testing.expectEqualStrings("018bcfe5-6801-748a-9758-205026a0e050", &first);
    try std.testing.expectEqualStrings("018bcfe5-6802-74fc-ad04-627bc4483f77", &second);
    test_seed = "parity";
    allocated = 0;
    defer {
        test_seed = null;
        allocated = 0;
    }
    const thirty = seeded("parity", 30);
    const older = seeded("parity", 5);
    const other = seeded("other", 80);
    const zero = seeded("parity", 0);
    var upper = seeded("parity", 90);
    for (&upper) |*c| c.* = std.ascii.toUpper(c.*);
    const text = try std.fmt.allocPrint(std.testing.allocator, "x{s}{s} {s} {s} {s}", .{ thirty, older, other, zero, upper });
    defer std.testing.allocator.free(text);
    observe(text);
    observe(&older); // Observations never lower the high-water mark.
    const actual = try next();
    const expected = seeded("parity", 31);
    try std.testing.expectEqualStrings(&expected, &actual);
    test_seed = null;
    observe(&other);
    try std.testing.expectEqual(@as(u64, 31), allocated);
}

test "unseeded IDs use the host clock and secure entropy" {
    init(std.testing.io, null);
    const before = std.Io.Clock.real.now(std.testing.io).toMilliseconds();
    const first = try next();
    const after = std.Io.Clock.real.now(std.testing.io).toMilliseconds();
    var timestamp: u64 = 0;
    for (first[0..13]) |c| if (c != '-') { timestamp = timestamp * 16 + try std.fmt.charToDigit(c, 16); };
    try std.testing.expect(timestamp >= @as(u64, @intCast(before)) and timestamp <= @as(u64, @intCast(after)));
    const second = try next();
    try std.testing.expect(first[14] == '7' and std.mem.indexOfScalar(u8, "89ab", first[19]) != null);
    try std.testing.expect(!std.mem.eql(u8, &first, &second));
}
