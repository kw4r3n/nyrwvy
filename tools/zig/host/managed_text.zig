//! .NET StreamReader/File.ReadAllText text boundary: BOM detection and replacement decoding.
const std = @import("std");

pub fn decode(a: std.mem.Allocator, bytes: []const u8) ![]const u8 {
    var width: usize = 0;
    var endian: std.builtin.Endian = .little;
    var i: usize = 0;
    if (std.mem.startsWith(u8, bytes, "\xff\xfe\x00\x00")) {
        width = 4;
        i = 4;
    } else if (std.mem.startsWith(u8, bytes, "\x00\x00\xfe\xff")) {
        width = 4;
        endian = .big;
        i = 4;
    } else if (std.mem.startsWith(u8, bytes, "\xff\xfe")) {
        width = 2;
        i = 2;
    } else if (std.mem.startsWith(u8, bytes, "\xfe\xff")) {
        width = 2;
        endian = .big;
        i = 2;
    }
    if (width == 0) {
        const raw = if (std.mem.startsWith(u8, bytes, "\xef\xbb\xbf")) bytes[3..] else bytes;
        return if (std.unicode.utf8ValidateSlice(raw)) raw else try std.fmt.allocPrint(a, "{f}", .{std.unicode.fmtUtf8(raw)});
    }
    var out: std.ArrayList(u8) = .empty;
    while (i < bytes.len) {
        var point: u21 = 0xfffd;
        if (i + width > bytes.len) {
            i = bytes.len;
        } else if (width == 4) {
            const n = std.mem.readInt(u32, bytes[i..][0..4], endian);
            i += 4;
            if (n <= 0x10ffff and (n < 0xd800 or n > 0xdfff)) point = @intCast(n);
        } else {
            const n = std.mem.readInt(u16, bytes[i..][0..2], endian);
            i += 2;
            if (n >= 0xd800 and n <= 0xdbff) {
                if (i + 2 <= bytes.len) {
                    const lo = std.mem.readInt(u16, bytes[i..][0..2], endian);
                    if (lo >= 0xdc00 and lo <= 0xdfff) {
                        point = 0x10000 + (@as(u21, n) - 0xd800) * 1024 + lo - 0xdc00;
                        i += 2;
                    }
                }
            } else if (n < 0xdc00 or n > 0xdfff) point = n;
        }
        var buf: [4]u8 = undefined;
        const len = try std.unicode.utf8Encode(point, &buf);
        try out.appendSlice(a, buf[0..len]);
    }
    return out.items;
}

/// Text is valid UTF-8 after decode; .NET output limits and Substring count UTF-16 units.
pub fn units(text: []const u8) usize {
    var count: usize = 0;
    var it = std.unicode.Utf8View.initUnchecked(text).iterator();
    while (it.nextCodepoint()) |point| count += if (point > 0xffff) @as(usize, 2) else 1;
    return count;
}
pub fn prefix(a: std.mem.Allocator, text: []const u8, limit: usize) ![]const u8 {
    var count: usize = 0;
    var it = std.unicode.Utf8View.initUnchecked(text).iterator();
    while (it.i < text.len) {
        const start = it.i;
        const point = it.nextCodepoint().?;
        const width: usize = if (point > 0xffff) 2 else 1;
        if (count == limit) return text[0..start];
        if (count + width > limit) return std.mem.concat(a, u8, &.{ text[0..start], "\xef\xbf\xbd" });
        count += width;
    }
    return text;
}

test "managed text detects BOMs and replaces malformed maximal subparts" {
    var arena: std.heap.ArenaAllocator = .init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const text = "A😀";
    for ([_][]const u8{
        "\xef\xbb\xbfA😀",
        "\xff\xfeA\x00\x3d\xd8\x00\xde",
        "\xfe\xff\x00A\xd8\x3d\xde\x00",
        "\xff\xfe\x00\x00A\x00\x00\x00\x00\xf6\x01\x00",
        "\x00\x00\xfe\xff\x00\x00\x00A\x00\x01\xf6\x00",
    }) |bytes| try std.testing.expectEqualStrings(text, try decode(a, bytes));
    try std.testing.expectEqualStrings("�A�", try decode(a, "\xe1\x80A\xf0\x91\x92"));
    try std.testing.expectEqualStrings("���", try decode(a, "\xed\xa0\x80"));
    try std.testing.expectEqualStrings("�A�", try decode(a, "\xff\xfe\x00\xd8A\x00\x00"));
    try std.testing.expectEqualStrings("�", try decode(a, "\x00\x00\xfe\xff\x00\x11\x00\x00"));
    try std.testing.expectEqual(@as(usize, 3), units(text));
    try std.testing.expectEqualStrings("A�", try prefix(a, text, 2));
    try std.testing.expectEqualStrings("A", try prefix(a, text, 1));
}
