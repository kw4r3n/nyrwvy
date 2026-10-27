namespace Nyrwvy.Core

/// Zig source the backend emits with every program, and the support a runPure runner adds.
[<RequireQualifiedAccess>]
module ZigRuntime =
    let runtime = """const std = @import("std");

const NyUnit = struct {};

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
    return text[from..to];
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
    while (parts.next()) |piece| pieces.append(nyAllocator(), piece) catch nyOom();
    return pieces.items;
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
            lines.append(nyAllocator(), text[start..offset]) catch nyOom();
            if (crlf and text[offset] == '\r' and next < text.len and text[next] == '\n') next += 1;
            start = next;
        }
        offset = next;
    }
    if (start < text.len) lines.append(nyAllocator(), text[start..]) catch nyOom();
    return lines.items;
}

fn nyStringReplace(text: []const u8, pattern: []const u8, replacement: []const u8) []const u8 {
    if (pattern.len == 0) return text;
    return std.mem.replaceOwned(u8, nyAllocator(), text, pattern, replacement) catch nyOom();
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
    return text[start..];
}

fn nyStringTrimEnd(text: []const u8) []const u8 {
    var end = text.len;
    while (end > 0) {
        var previous = end - 1;
        while (text[previous] & 0xC0 == 0x80) previous -= 1;
        if (!nyIsWhiteSpace(text[previous..end])) break;
        end = previous;
    }
    return text[0..end];
}

fn nyStringJoin(list: []const []const u8, separator: []const u8) []const u8 {
    return std.mem.join(nyAllocator(), separator, list) catch nyOom();
}

fn nyStringCodePoints(text: []const u8) []const []const u8 {
    const points = nyAlloc([]const u8, @intCast(nyStringLength(text)));
    var offset: usize = 0;
    for (points) |*point| {
        const next = offset + nyWidth(text, offset);
        point.* = text[offset..next];
        offset = next;
    }
    return points;
}

/// Python chr limited to Unicode scalar values.
fn nyStringFromCodePoint(code: i64) ?[]const u8 {
    if (code < 0 or code > 0x10FFFF or (code >= 0xD800 and code <= 0xDFFF)) return null;
    const out = nyAlloc(u8, 4);
    return out[0 .. std.unicode.utf8Encode(@intCast(code), out) catch unreachable];
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
    return std.fmt.allocPrint(nyAllocator(), "{f}", .{std.unicode.fmtUtf8(nyBytesFromLatin1(text).data)}) catch nyOom();
}

/// Python text * count; a result longer than a .NET string (2^31 - 1 UTF-16 units) fails as the
/// C# backend's allocation does.
fn nyStringRepeat(text: []const u8, count: i64) []const u8 {
    if (count <= 0 or text.len == 0) return "";
    const units = std.unicode.calcUtf16LeLen(text) catch unreachable;
    if (count > @divTrunc(std.math.maxInt(i32), @as(i64, @intCast(units)))) nyOom();
    const out = nyAlloc(u8, text.len * @as(usize, @intCast(count)));
    var at: usize = 0;
    while (at < out.len) : (at += text.len) @memcpy(out[at..][0..text.len], text);
    return out;
}

/// Python format(value, "x").
fn nyInt64ToHexString(value: i64) []const u8 {
    return std.fmt.allocPrint(nyAllocator(), "{s}{x}", .{ if (value < 0) "-" else "", @abs(value) }) catch nyOom();
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
    if (std.math.isNan(value)) return "nan";
    if (std.math.isInf(value)) return if (value > 0) "inf" else "-inf";
    if (value == 0) return if (std.math.signbit(value)) "-0.0" else "0.0";
    var buffer: [24]u8 = undefined;
    const decimal = nyShortest(@abs(value), &buffer);
    const digits = decimal.digits;
    const sign = if (value < 0) "-" else "";
    const zeros = "0000000000000000";
    const point = decimal.point;
    const text = if (point <= -4 or point > 16)
        std.fmt.allocPrint(nyAllocator(), "{s}{c}{s}{s}e{s}{d:0>2}", .{ sign, digits[0], if (digits.len > 1) "." else "", digits[1..], if (point - 1 < 0) "-" else "+", @abs(point - 1) })
    else if (point <= 0)
        std.fmt.allocPrint(nyAllocator(), "{s}0.{s}{s}", .{ sign, zeros[0..@intCast(-point)], digits })
    else if (point >= digits.len)
        std.fmt.allocPrint(nyAllocator(), "{s}{s}{s}.0", .{ sign, digits, zeros[0 .. @as(usize, @intCast(point)) - digits.len] })
    else
        std.fmt.allocPrint(nyAllocator(), "{s}{s}.{s}", .{ sign, digits[0..@intCast(point)], digits[@intCast(point)..] });
    return text catch nyOom();
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
    return struct { kind: u8, value: R = undefined, message: []const u8 = "" };
}

fn NyScript(comptime R: type) type {
    return struct { keyed: bool, keys: []const []const u8, queues: []const []const NyResponse(R), next: []usize };
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
        for (responses) |*response| {
            const kind = std.fmt.parseInt(u8, tokens.next(), 10) catch @panic("bad response token");
            response.* = switch (kind) {
                0 => .{ .kind = 0, .value = nyDecode(R, tokens) },
                1 => .{ .kind = 1, .message = nyDecodeText(tokens.next()) },
                // Waiting responses belong to task programs, which this backend does not run yet.
                else => @panic("unsupported fake response"),
            };
        }
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

fn nyFail(exception_type: []const u8, message: []const u8, exhausted: bool) noreturn {
    nyFailWrite(exception_type, message, exhausted) catch {};
    std.process.exit(0);
}

fn nyFailWrite(exception_type: []const u8, message: []const u8, exhausted: bool) !void {
    try ny_out.print("[\"exception\",\"{s}\",", .{exception_type});
    try nyEncodeText(ny_out, message);
    try ny_out.print(",[],[{s}],{},\"fault\"]\n", .{ ny_calls.written(), exhausted });
    try ny_out.flush();
}

fn nyRespond(comptime R: type, script: *NyScript(R), arguments: anytype) R {
    var selected: usize = 0;
    if (script.keyed) {
        var key: std.Io.Writer.Allocating = .init(nyAllocator());
        var first = true;
        inline for (@typeInfo(@TypeOf(arguments)).@"struct".field_names) |name| nyTokens(&key.writer, &first, @field(arguments, name)) catch @panic("out of memory");
        selected = for (script.keys, 0..) |candidate, index| {
            if (std.mem.eql(u8, candidate, key.written())) break index;
        } else nyFail("FakeArgumentsUnmatchedException", "No fake case matches the complete call arguments.", false);
    }
    const position = script.next[selected];
    if (position >= script.queues[selected].len) nyFail("FakeResponsesExhaustedException", "The fake operation has no scripted response left for this call.", true);
    script.next[selected] += 1;
    const response = script.queues[selected][position];
    if (response.kind == 1) nyFail("FakeCapabilityException", response.message, false);
    return response.value;
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

    /// The simple (one-to-one) case mapping the C# backend's NyText.ToLower/ToUpper apply: .NET's
    /// invariant mapping per code point, except U+0130 -> i, U+0131 -> I and U+00DF -> U+1E9E.
    let private mapCase (lower: bool) (code: int) =
        if code < 0x10000 then
            let unit = char code
            if lower then (if code = 0x130 then 0x69 else int (System.Char.ToLowerInvariant unit))
            elif code = 0x131 then 0x49
            elif code = 0xDF then 0x1E9E
            else int (System.Char.ToUpperInvariant unit)
        else
            let rune = System.Text.Rune code
            (if lower then System.Text.Rune.ToLowerInvariant rune else System.Text.Rune.ToUpperInvariant rune).Value

    /// Runs of mapped code points { first, last, step, delta }: every step-th code point from first
    /// to last maps to itself plus delta. Runs take consecutive mapped code points, so a step-2 run
    /// skips only unmapped ones, runs never overlap and a binary search over `first` finds the only
    /// candidate.
    let private caseRuns (lower: bool) =
        let mapped =
            [| for code in 0 .. 0x10FFFF do
                 if code < 0xD800 || code > 0xDFFF then
                     let target = mapCase lower code
                     if target <> code then code, target - code |]
        let runs = ResizeArray<string>()
        let mutable index = 0
        while index < mapped.Length do
            let first, delta = mapped[index]
            let mutable last = first
            let mutable step = 1
            if index + 1 < mapped.Length then
                let next, nextDelta = mapped[index + 1]
                if nextDelta = delta && next - first = 2 then step <- 2
            while index + 1 < mapped.Length
                  && fst mapped[index + 1] = last + step
                  && snd mapped[index + 1] = delta do
                index <- index + 1
                last <- last + step
            index <- index + 1
            runs.Add $".{{ {first}, {last}, {step}, {delta} }}"
        System.String.Join(", ", runs)

    /// Zig case mapping, emitted only for a program that maps case.
    let caseMapping =
        lazy
            ("const ny_lower_runs = [_][4]i32{ " + caseRuns true + " };\n"
             + "const ny_upper_runs = [_][4]i32{ " + caseRuns false + " };\n"
             + """
fn nyMapCase(text: []const u8, runs: []const [4]i32) []const u8 {
    var out: std.ArrayList(u8) = .empty;
    var view = std.unicode.Utf8View.initUnchecked(text);
    var points = view.iterator();
    while (points.nextCodepoint()) |point| {
        const code: i32 = point;
        var low: usize = 0;
        var high: usize = runs.len;
        while (low < high) {
            const middle = (low + high) / 2;
            if (runs[middle][0] <= code) low = middle + 1 else high = middle;
        }
        var mapped = code;
        if (low > 0) {
            const run = runs[low - 1];
            if (code <= run[1] and @mod(code - run[0], run[2]) == 0) mapped = code + run[3];
        }
        var buffer: [4]u8 = undefined;
        const size = std.unicode.utf8Encode(@intCast(mapped), &buffer) catch unreachable;
        out.appendSlice(nyAllocator(), buffer[0..size]) catch nyOom();
    }
    return out.items;
}
""")
