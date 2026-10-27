//! The Nyrwvy snapshot reader, second stage: snapshot JSON to the canonical JSON the F# writer
//! produces for the shape-decoded model, with every shape check of `SnapshotCodec`'s reader, the
//! schema version and construct minimums. Its reference is `SnapshotCodec.toCanonicalJson`.
//! The model-only rules of semantic validation (`SnapshotValidator`, the codes in
//! `validation_codes.txt`) are ported; the rest is not. See docs/decisions/zig-tools.md.
//!
//! Usage: `model-reader <in.json> <out.json> [<in> <out> ...]`. One line per pair on standard
//! output: `ok`, `skip legacy schema <n>` (versions before 21 are not ported yet), or
//! `error <Case>: <message>` naming the `SnapshotCodecError` case. Exit 0 unless the arguments or
//! files cannot be used.
//!
//! `model-reader --validate <in.json> [<in.json> ...]`: per input the same `error`/`skip` line when
//! the shape read fails, otherwise one `issue <Code>: <message>` line per ported issue, in the F#
//! validator's (Code, Message) order, then `end`.
//!
//! `model_kinds.txt` beside this file is the node reader table, one kind per line:
//! `kind|context|name:shape ...|written names` (properties in reading order; the last field only
//! when the writer's order differs). `schema_minimums.txt` holds the version rows above the first
//! record schema, generated from `SnapshotSchema`; a fast test compares it.
const std = @import("std");

const kind_table_text = @embedFile("model_kinds.txt");
const minimums_text = @embedFile("schema_minimums.txt");
const max_json_depth = 1000;

var a: std.mem.Allocator = undefined;

fn oom() noreturn {
    @panic("out of memory");
}

fn List(comptime T: type) type {
    return std.ArrayList(T);
}

fn fmt(comptime format: []const u8, args: anytype) []const u8 {
    return std.fmt.allocPrint(a, format, args) catch oom();
}

// ---------------------------------------------------------------- failures

const Failure = error{ Json, Shape, Version, Legacy };
var message: []const u8 = "";
var declared_version: i32 = 0;

fn invalidJson(reason: []const u8) Failure {
    message = reason;
    return error.Json;
}

fn invalidShape(reason: []const u8) Failure {
    // One line per input: a line break inside a message (a quoted value) is written as `\n`.
    const once = std.mem.replaceOwned(u8, a, reason, "\r", "\\r") catch oom();
    message = std.mem.replaceOwned(u8, a, once, "\n", "\\n") catch oom();
    return error.Shape;
}

// ---------------------------------------------------------------- JSON text

/// Strings stay UTF-16 code units, as .NET holds them: ordering, lengths and the writer's escapes
/// are per unit, and a lone surrogate escape survives where the F# reader keeps one.
const Str = []const u16;
const Member = struct { name: Str, value: Value };
const Value = union(enum) {
    json_null,
    json_true,
    json_false,
    number: []const u8,
    string: Str,
    array: []const Value,
    object: []const Member,
};

/// JsonDocument's grammar: no comments or trailing commas, at most `max_json_depth` open containers.
const Parser = struct {
    s: []const u8,
    i: usize = 0,
    depth: usize = 0,

    fn skipSpace(p: *Parser) void {
        while (p.i < p.s.len and (p.s[p.i] == ' ' or p.s[p.i] == '\t' or p.s[p.i] == '\n' or p.s[p.i] == '\r')) p.i += 1;
    }

    fn at(p: *Parser, c: u8) bool {
        return p.i < p.s.len and p.s[p.i] == c;
    }

    fn digits(p: *Parser) Failure!void {
        if (!(p.i < p.s.len and std.ascii.isDigit(p.s[p.i]))) return invalidJson("invalid number");
        while (p.i < p.s.len and std.ascii.isDigit(p.s[p.i])) p.i += 1;
    }

    fn value(p: *Parser) Failure!Value {
        p.skipSpace();
        if (p.i >= p.s.len) return invalidJson("unexpected end of data");
        switch (p.s[p.i]) {
            '{', '[' => |open| {
                if (p.depth >= max_json_depth) return invalidJson("maximum depth exceeded");
                p.depth += 1;
                p.i += 1;
                const close: u8 = if (open == '{') '}' else ']';
                var members: List(Member) = .empty;
                var items: List(Value) = .empty;
                p.skipSpace();
                if (p.at(close)) {
                    p.i += 1;
                } else while (true) {
                    if (open == '{') {
                        p.skipSpace();
                        if (!p.at('"')) return invalidJson("expected a property name");
                        const name = try p.string();
                        p.skipSpace();
                        if (!p.at(':')) return invalidJson("expected ':'");
                        p.i += 1;
                        members.append(a, .{ .name = name, .value = try p.value() }) catch oom();
                    } else items.append(a, try p.value()) catch oom();
                    p.skipSpace();
                    if (p.at(',')) {
                        p.i += 1;
                        continue;
                    }
                    if (!p.at(close)) return invalidJson("expected ',' or the container's end");
                    p.i += 1;
                    break;
                }
                p.depth -= 1;
                return if (open == '{') .{ .object = members.items } else .{ .array = items.items };
            },
            '"' => return .{ .string = try p.string() },
            't' => return p.literal("true", .json_true),
            'f' => return p.literal("false", .json_false),
            'n' => return p.literal("null", .json_null),
            '-', '0'...'9' => {
                const start = p.i;
                if (p.at('-')) p.i += 1;
                if (p.at('0')) p.i += 1 else try p.digits();
                if (p.at('.')) {
                    p.i += 1;
                    try p.digits();
                }
                if (p.at('e') or p.at('E')) {
                    p.i += 1;
                    if (p.at('+') or p.at('-')) p.i += 1;
                    try p.digits();
                }
                return .{ .number = p.s[start..p.i] };
            },
            else => return invalidJson("invalid start of a value"),
        }
    }

    fn literal(p: *Parser, word: []const u8, result: Value) Failure!Value {
        if (!std.mem.startsWith(u8, p.s[p.i..], word)) return invalidJson("invalid literal");
        p.i += word.len;
        return result;
    }

    fn hex4(p: *Parser) Failure!u16 {
        if (p.i + 4 > p.s.len) return invalidJson("invalid escape");
        const unit = std.fmt.parseInt(u16, p.s[p.i .. p.i + 4], 16) catch return invalidJson("invalid escape");
        if (p.s[p.i] == '+' or p.s[p.i] == '-') return invalidJson("invalid escape");
        p.i += 4;
        return unit;
    }

    fn string(p: *Parser) Failure!Str {
        p.i += 1;
        var units: List(u16) = .empty;
        while (true) {
            if (p.i >= p.s.len) return invalidJson("unterminated string");
            const c = p.s[p.i];
            if (c == '"') {
                p.i += 1;
                return units.items;
            }
            if (c < 0x20) return invalidJson("control character in a string");
            if (c == '\\') {
                p.i += 1;
                if (p.i >= p.s.len) return invalidJson("invalid escape");
                const e = p.s[p.i];
                p.i += 1;
                const unit: u16 = switch (e) {
                    '"' => '"',
                    '\\' => '\\',
                    '/' => '/',
                    'b' => 8,
                    'f' => 12,
                    'n' => 10,
                    'r' => 13,
                    't' => 9,
                    'u' => try p.hex4(),
                    else => return invalidJson("invalid escape"),
                };
                units.append(a, unit) catch oom();
                continue;
            }
            const length = std.unicode.utf8ByteSequenceLength(c) catch unreachable;
            const point = std.unicode.utf8Decode(p.s[p.i .. p.i + length]) catch unreachable;
            p.i += length;
            if (point >= 0x10000) {
                units.append(a, @intCast(0xD800 + ((point - 0x10000) >> 10))) catch oom();
                units.append(a, @intCast(0xDC00 + ((point - 0x10000) & 0x3FF))) catch oom();
            } else units.append(a, @intCast(point)) catch oom();
        }
    }
};

fn parseDocument(source: []const u8) Failure!Value {
    // shortcut: assumes valid UTF-8 (the test writes .NET strings as UTF-8); reject until needed.
    if (!std.unicode.utf8ValidateSlice(source)) return invalidJson("invalid UTF-8");
    var parser: Parser = .{ .s = source };
    const root = try parser.value();
    parser.skipSpace();
    if (parser.i != source.len) return invalidJson("data after the JSON value");
    return root;
}

fn hasLoneSurrogate(units: Str) bool {
    var i: usize = 0;
    while (i < units.len) : (i += 1) {
        const u = units[i];
        if (u >= 0xD800 and u <= 0xDBFF and i + 1 < units.len and units[i + 1] >= 0xDC00 and units[i + 1] <= 0xDFFF) {
            i += 1;
        } else if (u >= 0xD800 and u <= 0xDFFF) return true;
    }
    return false;
}

/// UTF-8 for messages; a lone surrogate becomes U+FFFD, as .NET's UTF-8 output writes it.
fn text(units: Str) []const u8 {
    var out: List(u8) = .empty;
    var i: usize = 0;
    while (i < units.len) : (i += 1) {
        var point: u21 = units[i];
        if (point >= 0xD800 and point <= 0xDBFF and i + 1 < units.len and units[i + 1] >= 0xDC00 and units[i + 1] <= 0xDFFF) {
            point = 0x10000 + ((point - 0xD800) << 10) + (units[i + 1] - 0xDC00);
            i += 1;
        } else if (point >= 0xD800 and point <= 0xDFFF) point = 0xFFFD;
        var bytes: [4]u8 = undefined;
        const n = std.unicode.utf8Encode(point, &bytes) catch unreachable;
        out.appendSlice(a, bytes[0..n]) catch oom();
    }
    return out.items;
}

fn isName(units: Str, name: []const u8) bool {
    if (units.len != name.len) return false;
    for (units, name) |u, c| if (u != c) return false;
    return true;
}

fn lessUnits(left: Str, right: Str) bool {
    return std.mem.order(u16, left, right) == .lt;
}

// ---------------------------------------------------------------- the writer's text

/// Utf8JsonWriter's default encoder, per UTF-16 unit.
fn escapeUnit(out: *List(u8), unit: u16) void {
    switch (unit) {
        8 => out.appendSlice(a, "\\b") catch oom(),
        9 => out.appendSlice(a, "\\t") catch oom(),
        10 => out.appendSlice(a, "\\n") catch oom(),
        12 => out.appendSlice(a, "\\f") catch oom(),
        13 => out.appendSlice(a, "\\r") catch oom(),
        '\\' => out.appendSlice(a, "\\\\") catch oom(),
        0x20...0x21, 0x23...0x25, 0x28...0x2A, 0x2C...0x3B, 0x3D, 0x3F...0x5B, 0x5D...0x5F, 0x61...0x7E => out.append(a, @intCast(unit)) catch oom(),
        else => out.print(a, "\\u{X:0>4}", .{unit}) catch oom(),
    }
}

fn quoted(units: Str) []const u8 {
    var out: List(u8) = .empty;
    out.append(a, '"') catch oom();
    for (units) |unit| escapeUnit(&out, unit);
    out.append(a, '"') catch oom();
    return out.items;
}

fn quotedAscii(name: []const u8) []const u8 {
    return fmt("\"{s}\"", .{name});
}

/// A compact object; keys are ASCII names that need no escape.
const Obj = struct {
    buf: List(u8) = .empty,

    fn put(self: *Obj, key: []const u8, value: []const u8) void {
        self.buf.append(a, if (self.buf.items.len == 0) '{' else ',') catch oom();
        self.buf.print(a, "\"{s}\":{s}", .{ key, value }) catch oom();
    }

    fn putOptional(self: *Obj, key: []const u8, value: ?[]const u8) void {
        if (value) |v| self.put(key, v);
    }

    fn done(self: *Obj) []const u8 {
        if (self.buf.items.len == 0) return "{}";
        self.buf.append(a, '}') catch oom();
        return self.buf.items;
    }
};

fn array(items: []const []const u8) []const u8 {
    var out: List(u8) = .empty;
    out.append(a, '[') catch oom();
    for (items, 0..) |item, index| {
        if (index > 0) out.append(a, ',') catch oom();
        out.appendSlice(a, item) catch oom();
    }
    out.append(a, ']') catch oom();
    return out.items;
}

/// One written item with the stable IDs the writer sorts it by (`List.sortBy`, stable, ordinal).
const Keyed = struct { key: Str, key2: Str = &.{}, text: []const u8 };

fn keyedLess(_: void, left: Keyed, right: Keyed) bool {
    return switch (std.mem.order(u16, left.key, right.key)) {
        .lt => true,
        .gt => false,
        .eq => lessUnits(left.key2, right.key2),
    };
}

fn sortedArray(items: []Keyed) []const u8 {
    std.mem.sort(Keyed, items, {}, keyedLess);
    const texts = a.alloc([]const u8, items.len) catch oom();
    for (items, texts) |item, *t| t.* = item.text;
    return array(texts);
}

fn plainArray(items: []const Keyed) []const u8 {
    const texts = a.alloc([]const u8, items.len) catch oom();
    for (items, texts) |item, *t| t.* = item.text;
    return array(texts);
}

// ---------------------------------------------------------------- the reader's helpers

fn ensureObject(context: []const u8, element: Value) Failure!void {
    if (element != .object) return invalidShape(fmt("{s} must be a JSON object.", .{context}));
}

fn inNames(name: Str, names: []const []const u8) bool {
    for (names) |n| if (isName(name, n)) return true;
    return false;
}

fn lessAscii(_: void, left: []const u8, right: []const u8) bool {
    return std.mem.order(u8, left, right) == .lt;
}

fn lessStr(_: void, left: Str, right: Str) bool {
    return lessUnits(left, right);
}

fn ensureProperties(context: []const u8, required: []const []const u8, allowed: []const []const u8, element: Value) Failure!void {
    try ensureObject(context, element);
    const members = element.object;
    var duplicate = false;
    var unsupported = false;
    for (members, 0..) |member, index| {
        if (hasLoneSurrogate(member.name)) return invalidShape("Invalid JSON property name encoding: lone surrogate");
        for (members[0..index]) |earlier| {
            if (std.mem.eql(u16, earlier.name, member.name)) duplicate = true;
        }
        if (!inNames(member.name, allowed)) unsupported = true;
    }
    if (duplicate) return invalidShape(fmt("{s} contains duplicate property names.", .{context}));
    var missing: List([]const u8) = .empty;
    for (required) |name| {
        var present = false;
        for (members) |member| present = present or isName(member.name, name);
        if (!present) missing.append(a, name) catch oom();
    }
    if (missing.items.len > 0) {
        std.mem.sort([]const u8, missing.items, {}, lessAscii);
        const joined = std.mem.join(a, ", ", missing.items) catch oom();
        return invalidShape(fmt("{s} is missing required properties: {s}.", .{ context, joined }));
    }
    if (unsupported) {
        var names: List(Str) = .empty;
        for (members) |member| {
            if (!inNames(member.name, allowed)) names.append(a, member.name) catch oom();
        }
        std.mem.sort(Str, names.items, {}, lessStr);
        var texts: List([]const u8) = .empty;
        for (names.items, 0..) |name, index| {
            if (index == 0 or !std.mem.eql(u16, names.items[index - 1], name)) texts.append(a, text(name)) catch oom();
        }
        const joined = std.mem.join(a, ", ", texts.items) catch oom();
        return invalidShape(fmt("{s} contains unsupported properties: {s}.", .{ context, joined }));
    }
}

/// `TryGetProperty`: the last of duplicate names wins.
fn tryProperty(name: []const u8, element: Value) ?Value {
    var found: ?Value = null;
    for (element.object) |member| {
        if (isName(member.name, name)) found = member.value;
    }
    return found;
}

fn requireProperty(name: []const u8, element: Value) Failure!Value {
    try ensureObject(fmt("Container for property '{s}'", .{name}), element);
    return tryProperty(name, element) orelse invalidShape(fmt("Missing required property '{s}'.", .{name}));
}

fn decodeString(units: Str) Failure!Str {
    // The .NET message after the colon differs by surrogate; the test compares this case by kind only.
    if (hasLoneSurrogate(units)) return invalidShape("Invalid JSON string encoding: lone surrogate");
    return units;
}

fn stringValue(name: []const u8, element: Value) Failure!Str {
    const value = try requireProperty(name, element);
    if (value != .string) return invalidShape(fmt("Property '{s}' must be a string.", .{name}));
    return value.string;
}

fn readString(name: []const u8, element: Value) Failure!Str {
    return decodeString(try stringValue(name, element));
}

/// `readLiteralString`: a lone surrogate is kept (validation reports it, not the reader).
fn readLiteralString(name: []const u8, element: Value) Failure!Str {
    return stringValue(name, element);
}

/// `Char.IsWhiteSpace`.
fn isWhiteSpace(unit: u16) bool {
    return switch (unit) {
        0x09...0x0D, 0x20, 0x85, 0xA0, 0x1680, 0x2000...0x200A, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000 => true,
        else => false,
    };
}

/// `StableId.tryCreate`'s error, if any.
fn stableIdError(units: Str) ?[]const u8 {
    var blank = true;
    for (units) |unit| blank = blank and isWhiteSpace(unit);
    if (blank) return "BlankValue";
    for (units) |unit| {
        if (unit == '\r' or unit == '\n') return "ContainsLineBreak";
    }
    return null;
}

fn readId(name: []const u8, element: Value) Failure!Str {
    const units = try readString(name, element);
    if (stableIdError(units)) |e| return invalidShape(fmt("Property '{s}' contains an invalid stable ID: {s}.", .{ name, e }));
    return units;
}

fn readBool(name: []const u8, element: Value) Failure![]const u8 {
    return switch (try requireProperty(name, element)) {
        .json_true => "true",
        .json_false => "false",
        else => invalidShape(fmt("Property '{s}' must be a boolean.", .{name})),
    };
}

/// `TryGetInt32`/`TryGetInt64`: an integer token (no fraction or exponent) in range.
fn integer(comptime T: type, value: Value) ?T {
    if (value != .number) return null;
    for (value.number, 0..) |c, i| {
        if (!(std.ascii.isDigit(c) or (c == '-' and i == 0))) return null;
    }
    return std.fmt.parseInt(T, value.number, 10) catch null;
}

fn readInt32(name: []const u8, element: Value) Failure!i32 {
    return integer(i32, try requireProperty(name, element)) orelse invalidShape(fmt("Property '{s}' must be a 32-bit integer.", .{name}));
}

fn readInt64(name: []const u8, element: Value) Failure!i64 {
    return integer(i64, try requireProperty(name, element)) orelse invalidShape(fmt("Property '{s}' must be a signed 64-bit integer.", .{name}));
}

/// `Float64Bits.tryParseHex`, written back as the writer's lower-case `x16`.
fn readFloat64Bits(name: []const u8, element: Value) Failure![]const u8 {
    const units = try readString(name, element);
    if (units.len != 16) return invalidShape(fmt("Property '{s}' is not canonical binary64 bits: Float64 bits must contain exactly 16 hexadecimal digits.", .{name}));
    var bits: u64 = 0;
    for (units) |unit| {
        const digit: u64 = if (unit < 128) std.fmt.charToDigit(@intCast(unit), 16) catch 16 else 16;
        if (digit == 16) return invalidShape(fmt("Property '{s}' is not canonical binary64 bits: Float64 bits contain invalid hexadecimal digits.", .{name}));
        bits = bits * 16 + digit;
    }
    return fmt("\"{x:0>16}\"", .{bits});
}

fn readArray(name: []const u8, element: Value) Failure![]const Value {
    const value = try requireProperty(name, element);
    if (value != .array) return invalidShape(fmt("Property '{s}' must be an array.", .{name}));
    return value.array;
}

fn readIdArray(name: []const u8, element: Value) Failure![]Keyed {
    const items = try readArray(name, element);
    const ids = a.alloc(Keyed, items.len) catch oom();
    for (items, ids) |item, *id| {
        if (item != .string) return invalidShape(fmt("Property '{s}' must contain only stable-ID strings.", .{name}));
        const units = try decodeString(item.string);
        if (stableIdError(units)) |e| return invalidShape(fmt("Property '{s}' contains an invalid stable ID: {s}.", .{ name, e }));
        id.* = .{ .key = units, .text = quoted(units) };
    }
    return ids;
}

/// A `true`-or-omitted marker (`readVisibility` at an explicit-visibility schema, `readMarker`).
fn readMarker(name: []const u8, element: Value, wrong: []const u8) Failure!bool {
    const value = tryProperty(name, element) orelse return false;
    if (value != .json_true) return invalidShape(wrong);
    return true;
}

fn readVisibility(element: Value) Failure!?[]const u8 {
    const exported = try readMarker("exported", element, "Property 'exported' must be true or omitted (a private declaration omits it).");
    return if (exported) "true" else null;
}

/// A wait or task-binding marker (`WaitAttributes`).
fn readWaitMarker(name: []const u8, element: Value) Failure!?[]const u8 {
    if (!try readMarker(name, element, fmt("Property '{s}' must be true or omitted.", .{name}))) return null;
    use(wait_row);
    return "true";
}

// ---------------------------------------------------------------- construct minimums

const Row = struct { minimum: i32, feature: []const u8, name: []const u8 };
var rows: List(Row) = .empty;
var used: []bool = &.{};
var first_supported: i32 = 0;
var first_record: i32 = 0;
var current: i32 = 0;
var wait_row: ?usize = null;
var bytes_row: ?usize = null;

fn use(row: ?usize) void {
    if (row) |r| used[r] = true;
}

// ---------------------------------------------------------------- types

const primitives = [_][]const u8{ "string", "bool", "unit", "int64", "float64", "bits32", "bytes" };
const generics = [_][]const u8{ "option", "result", "map", "list", "set", "oneShot", "task", "exception", "taskScope" };

fn oneOf(units: Str, names: []const []const u8) bool {
    return inNames(units, names);
}

fn readTypeRefs(name: []const u8, element: Value) Failure![]const u8 {
    const items = try readArray(name, element);
    const texts = a.alloc([]const u8, items.len) catch oom();
    for (items, texts) |item, *t| t.* = try readTypeRef(item);
    return array(texts);
}

fn readTypeRef(element: Value) Failure![]const u8 {
    const kind = try readString("kind", element);
    var o: Obj = .{};
    if (isName(kind, "primitive")) {
        try ensureProperties("Primitive type reference", &.{ "kind", "primitive" }, &.{ "kind", "primitive" }, element);
        const primitive = try readString("primitive", element);
        if (!oneOf(primitive, &primitives)) return invalidShape(fmt("Unsupported primitive type '{s}'.", .{text(primitive)}));
        if (isName(primitive, "bytes")) use(bytes_row);
        o.put("kind", "\"primitive\"");
        o.put("primitive", quoted(primitive));
    } else if (isName(kind, "named")) {
        try ensureProperties("Named type reference", &.{ "kind", "typeId" }, &.{ "kind", "typeId", "arguments" }, element);
        const type_id = try readId("typeId", element);
        const arguments = try readTypeArguments("arguments", element);
        o.put("kind", "\"named\"");
        o.put("typeId", quoted(type_id));
        o.putOptional("arguments", arguments);
    } else if (isName(kind, "function")) {
        try ensureProperties("Function type", &.{ "kind", "parameters", "resultType" }, &.{ "kind", "parameters", "resultType" }, element);
        const parameters = try readTypeRefs("parameters", element);
        const result = try readTypeRef(try requireProperty("resultType", element));
        o.put("kind", "\"function\"");
        o.put("parameters", parameters);
        o.put("resultType", result);
    } else if (isName(kind, "typeParameter")) {
        try ensureProperties("Type parameter reference", &.{ "kind", "typeParameterId" }, &.{ "kind", "typeParameterId" }, element);
        o.put("kind", "\"typeParameter\"");
        o.put("typeParameterId", quoted(try readId("typeParameterId", element)));
    } else if (isName(kind, "capability")) {
        try ensureProperties("Capability type reference", &.{ "kind", "capabilityId" }, &.{ "kind", "capabilityId" }, element);
        o.put("kind", "\"capability\"");
        o.put("capabilityId", quoted(try readId("capabilityId", element)));
    } else if (isName(kind, "builtinGeneric")) {
        try ensureProperties("Builtin generic type reference", &.{ "kind", "generic", "arguments" }, &.{ "kind", "generic", "arguments" }, element);
        const generic = try readString("generic", element);
        if (!oneOf(generic, &generics)) return invalidShape(fmt("Unsupported builtin generic type '{s}'.", .{text(generic)}));
        const arguments = try readTypeRefs("arguments", element);
        o.put("kind", "\"builtinGeneric\"");
        o.put("generic", quoted(generic));
        o.put("arguments", arguments);
    } else return invalidShape(fmt("Unsupported type reference kind '{s}'.", .{text(kind)}));
    return o.done();
}

/// Optional type arguments; an empty array is not canonical, since the writer omits it.
fn readTypeArguments(name: []const u8, element: Value) Failure!?[]const u8 {
    if (tryProperty(name, element) == null) return null;
    const items = try readArray(name, element);
    const texts = a.alloc([]const u8, items.len) catch oom();
    for (items, texts) |item, *t| t.* = try readTypeRef(item);
    if (items.len == 0) return invalidShape(fmt("Property '{s}' must be omitted rather than empty.", .{name}));
    return array(texts);
}

const typed_declaration = [_][]const u8{ "id", "displayName", "type" };

/// A field, parameter or capability parameter: `{ id, displayName, type }`.
fn readTyped(context: []const u8, element: Value) Failure!Keyed {
    try ensureProperties(context, &typed_declaration, &typed_declaration, element);
    const id = try readId("id", element);
    const display = try readString("displayName", element);
    const type_ref = try readTypeRef(try requireProperty("type", element));
    var o: Obj = .{};
    o.put("id", quoted(id));
    o.put("displayName", quoted(display));
    o.put("type", type_ref);
    return .{ .key = id, .text = o.done() };
}

fn readTypedArray(name: []const u8, context: []const u8, element: Value) Failure![]Keyed {
    const items = try readArray(name, element);
    const out = a.alloc(Keyed, items.len) catch oom();
    for (items, out) |item, *o| o.* = try readTyped(context, item);
    return out;
}

fn readTypeParameters(element: Value) Failure!?[]const u8 {
    if (tryProperty("typeParameters", element) == null) return null;
    const items = try readArray("typeParameters", element);
    const texts = a.alloc([]const u8, items.len) catch oom();
    for (items, texts) |item, *t| {
        try ensureProperties("Type parameter declaration", &.{ "typeParameterId", "displayName" }, &.{ "typeParameterId", "displayName" }, item);
        var o: Obj = .{};
        o.put("typeParameterId", quoted(try readId("typeParameterId", item)));
        o.put("displayName", quoted(try readString("displayName", item)));
        t.* = o.done();
    }
    if (items.len == 0) return invalidShape("Property 'typeParameters' must be omitted rather than empty.");
    return array(texts);
}

// ---------------------------------------------------------------- nodes

const Shape = enum { id, ids, type, targs, graph, varm, ctx, args, capargs, fields, lit, bool, int, bits, mode, optid, impls, arms, errarm };
const Read = struct { name: []const u8, shape: Shape };
const Kind = struct {
    context: []const u8,
    reads: []const Read,
    /// Indexes into `reads` in the writer's order.
    writes: []const usize,
    required: []const []const u8,
    allowed: []const []const u8,
    row: ?usize = null,
};
var kinds: std.StringHashMapUnmanaged(Kind) = .empty;

fn loadTables() void {
    var lines = std.mem.tokenizeScalar(u8, minimums_text, '\n');
    var versions = std.mem.tokenizeScalar(u8, lines.next().?, ' ');
    _ = versions.next();
    first_supported = std.fmt.parseInt(i32, versions.next().?, 10) catch unreachable;
    first_record = std.fmt.parseInt(i32, versions.next().?, 10) catch unreachable;
    current = std.fmt.parseInt(i32, versions.next().?, 10) catch unreachable;
    var row_kinds: List([]const u8) = .empty;
    while (lines.next()) |line| {
        var fields = std.mem.splitScalar(u8, line, '|');
        const minimum = std.fmt.parseInt(i32, fields.next().?, 10) catch unreachable;
        const feature = fields.next().?;
        rows.append(a, .{ .minimum = minimum, .feature = feature, .name = fields.next().? }) catch oom();
        row_kinds.append(a, fields.next().?) catch oom();
        if (std.mem.eql(u8, feature, "WaitAttributes")) wait_row = rows.items.len - 1;
        if (std.mem.eql(u8, feature, "Bytes")) bytes_row = rows.items.len - 1;
    }
    used = a.alloc(bool, rows.items.len) catch oom();

    lines = std.mem.tokenizeScalar(u8, kind_table_text, '\n');
    while (lines.next()) |line| {
        var parts = std.mem.splitScalar(u8, line, '|');
        const name = parts.next().?;
        const context = parts.next().?;
        var reads: List(Read) = .empty;
        var required: List([]const u8) = .empty;
        var allowed: List([]const u8) = .empty;
        required.appendSlice(a, &.{ "id", "kind" }) catch oom();
        allowed.appendSlice(a, &.{ "id", "kind" }) catch oom();
        var items = std.mem.tokenizeScalar(u8, parts.next().?, ' ');
        while (items.next()) |item| {
            const colon = std.mem.indexOfScalar(u8, item, ':').?;
            const shape = std.meta.stringToEnum(Shape, item[colon + 1 ..]) orelse std.debug.panic("model_kinds.txt: unknown shape in '{s}'", .{item});
            reads.append(a, .{ .name = item[0..colon], .shape = shape }) catch oom();
            allowed.append(a, item[0..colon]) catch oom();
            if (shape != .optid and shape != .targs and shape != .ctx) required.append(a, item[0..colon]) catch oom();
        }
        var writes: List(usize) = .empty;
        if (parts.next()) |order| {
            var names = std.mem.tokenizeScalar(u8, order, ' ');
            while (names.next()) |written| {
                for (reads.items, 0..) |r, index| {
                    if (std.mem.eql(u8, r.name, written)) writes.append(a, index) catch oom();
                }
            }
            std.debug.assert(writes.items.len == reads.items.len);
        } else for (reads.items, 0..) |_, index| writes.append(a, index) catch oom();
        var row: ?usize = null;
        for (row_kinds.items, 0..) |listed, index| {
            var listed_kinds = std.mem.tokenizeScalar(u8, listed, ' ');
            while (listed_kinds.next()) |k| {
                if (std.mem.eql(u8, k, name)) row = index;
            }
        }
        kinds.put(a, name, .{ .context = context, .reads = reads.items, .writes = writes.items, .required = required.items, .allowed = allowed.items, .row = row }) catch oom();
    }
}

fn readArgumentLike(context: []const u8, target: []const u8, element: Value) Failure!Keyed {
    try ensureProperties(context, &.{ target, "valueNodeId" }, &.{ target, "valueNodeId" }, element);
    const target_id = try readId(target, element);
    const value_id = try readId("valueNodeId", element);
    var o: Obj = .{};
    o.put(target, quoted(target_id));
    o.put("valueNodeId", quoted(value_id));
    return .{ .key = target_id, .key2 = value_id, .text = o.done() };
}

/// Arguments, capability arguments or field values, sorted as the writer sorts them.
fn readArguments(name: []const u8, context: []const u8, target: []const u8, by_target_only: bool, element: Value) Failure![]const u8 {
    const items = try readArray(name, element);
    const out = a.alloc(Keyed, items.len) catch oom();
    for (items, out) |item, *o| {
        o.* = try readArgumentLike(context, target, item);
        if (by_target_only) o.key2 = &.{};
    }
    return sortedArray(out);
}

fn readNamedCallbackContext(name: []const u8, element: Value) Failure!?[]const u8 {
    if (tryProperty(name, element) == null) return null;
    const items = try readArray(name, element);
    const out = a.alloc(Keyed, items.len) catch oom();
    for (items, out) |item, *o| o.* = try readArgumentLike("Argument", "targetParameterId", item);
    if (items.len == 0) return invalidShape("Callback context must be omitted rather than empty.");
    return sortedArray(out);
}

fn readPatternBinding(element: Value) Failure![]const u8 {
    try ensureProperties("Pattern binding", &.{ "id", "type" }, &typed_declaration, element);
    const display: ?Str = if (tryProperty("displayName", element) != null) try readString("displayName", element) else null;
    const id = try readId("id", element);
    const type_ref = try readTypeRef(try requireProperty("type", element));
    var o: Obj = .{};
    o.put("id", quoted(id));
    if (display) |d| o.put("displayName", quoted(d));
    o.put("type", type_ref);
    return o.done();
}

fn readValueArm(element: Value) Failure![]const u8 {
    try ensureProperties("Value arm", &.{ "valueBinding", "body" }, &.{ "valueBinding", "body" }, element);
    const binding = try readPatternBinding(try requireProperty("valueBinding", element));
    const body = try readGraph(try requireProperty("body", element));
    var o: Obj = .{};
    o.put("valueBinding", binding);
    o.put("body", body);
    return o.done();
}

fn readUnionArm(element: Value) Failure!Keyed {
    try ensureProperties("Union match arm", &.{ "fieldBindings", "body" }, &.{ "variantId", "variantIds", "fieldBindings", "body" }, element);
    const single = tryProperty("variantId", element) != null;
    const several = tryProperty("variantIds", element) != null;
    var variants: []Keyed = undefined;
    if (single and !several) {
        const id = try readId("variantId", element);
        variants = a.alloc(Keyed, 1) catch oom();
        variants[0] = .{ .key = id, .text = quoted(id) };
    } else if (several and !single) {
        variants = try readIdArray("variantIds", element);
        if (variants.len < 2) return invalidShape("Union match arm 'variantIds' must name at least two variants; one variant uses 'variantId'.");
    } else return invalidShape("Union match arm requires exactly one of 'variantId' and 'variantIds'.");
    const items = try readArray("fieldBindings", element);
    const bindings = a.alloc(Keyed, items.len) catch oom();
    for (items, bindings) |item, *b| {
        try ensureProperties("Pattern field binding", &.{ "sourceFieldId", "binding" }, &.{ "sourceFieldId", "binding" }, item);
        const source = try readId("sourceFieldId", item);
        const binding = try readPatternBinding(try requireProperty("binding", item));
        var o: Obj = .{};
        o.put("sourceFieldId", quoted(source));
        o.put("binding", binding);
        b.* = .{ .key = source, .text = o.done() };
    }
    const body = try readGraph(try requireProperty("body", element));
    std.mem.sort(Keyed, variants, {}, keyedLess);
    var o: Obj = .{};
    if (variants.len == 1) o.put("variantId", variants[0].text) else o.put("variantIds", plainArray(variants));
    o.put("fieldBindings", sortedArray(bindings));
    o.put("body", body);
    return .{ .key = variants[0].key, .text = o.done() };
}

fn readShape(read: Read, element: Value) Failure!?[]const u8 {
    const name = read.name;
    return switch (read.shape) {
        .id => quoted(try readId(name, element)),
        .ids => plainArray(try readIdArray(name, element)),
        .type => try readTypeRef(try requireProperty(name, element)),
        .targs => try readTypeArguments(name, element),
        .graph => try readGraph(try requireProperty(name, element)),
        .varm => try readValueArm(try requireProperty(name, element)),
        .ctx => try readNamedCallbackContext(name, element),
        .args => try readArguments(name, "Argument", "targetParameterId", false, element),
        .capargs => try readArguments(name, "Capability argument", "targetCapabilityParameterId", false, element),
        .fields => try readArguments(name, "Field value", "targetFieldId", true, element),
        .lit => quoted(try readLiteralString(name, element)),
        .bool => try readBool(name, element),
        .int => fmt("{d}", .{try readInt64(name, element)}),
        .bits => try readFloat64Bits(name, element),
        .mode => blk: {
            const mode = try readString(name, element);
            if (!isName(mode, "any") and !isName(mode, "all")) return invalidShape(fmt("Unsupported task wait mode '{s}'.", .{text(mode)}));
            break :blk quoted(mode);
        },
        .optid => if (tryProperty(name, element) != null) quoted(try readId(name, element)) else null,
        .impls => blk: {
            const items = try readArray(name, element);
            const out = a.alloc(Keyed, items.len) catch oom();
            for (items, out) |item, *o| {
                try ensureProperties("Capability implementation", &.{ "operationId", "functionId" }, &.{ "operationId", "functionId" }, item);
                const operation = try readId("operationId", item);
                const function = try readId("functionId", item);
                var obj: Obj = .{};
                obj.put("operationId", quoted(operation));
                obj.put("functionId", quoted(function));
                o.* = .{ .key = operation, .text = obj.done() };
            }
            break :blk sortedArray(out);
        },
        .arms => blk: {
            const items = try readArray(name, element);
            const out = a.alloc(Keyed, items.len) catch oom();
            for (items, out) |item, *o| o.* = try readUnionArm(item);
            break :blk sortedArray(out);
        },
        .errarm => unreachable,
    };
}

fn readNode(element: Value) Failure!Keyed {
    const id = try readId("id", element);
    const kind_units = try readString("kind", element);
    const kind_name = text(kind_units);
    const kind = kinds.get(kind_name) orelse return invalidShape(fmt("Unsupported graph node kind '{s}'.", .{kind_name}));
    try ensureProperties(kind.context, kind.required, kind.allowed, element);
    use(kind.row);
    var o: Obj = .{};
    o.put("id", quoted(id));
    o.put("kind", quoted(kind_units));
    if (std.mem.eql(u8, kind_name, "matchResult")) {
        // The Err arm's shape is checked before the Ok arm is read, its content after; the input last.
        const err = try requireProperty("errArm", element);
        try ensureProperties("Result.Err match arm", &.{ "errorBinding", "body" }, &.{ "errorBinding", "body" }, err);
        const ok_arm = try readValueArm(try requireProperty("okArm", element));
        const binding = try readPatternBinding(try requireProperty("errorBinding", err));
        const body = try readGraph(try requireProperty("body", err));
        o.put("inputNodeId", quoted(try readId("inputNodeId", element)));
        o.put("okArm", ok_arm);
        var err_arm: Obj = .{};
        err_arm.put("errorBinding", binding);
        err_arm.put("body", body);
        o.put("errArm", err_arm.done());
        return .{ .key = id, .text = o.done() };
    }
    const pieces = a.alloc(?[]const u8, kind.reads.len) catch oom();
    for (kind.reads, pieces) |read, *piece| piece.* = try readShape(read, element);
    for (kind.writes) |index| o.putOptional(kind.reads[index].name, pieces[index]);
    return .{ .key = id, .text = o.done() };
}

fn readGraph(element: Value) Failure![]const u8 {
    try ensureProperties("Value graph", &.{ "nodes", "resultNodeId" }, &.{ "nodes", "resultNodeId" }, element);
    const items = try readArray("nodes", element);
    const nodes = a.alloc(Keyed, items.len) catch oom();
    for (items, nodes) |item, *node| node.* = try readNode(item);
    const result = try readId("resultNodeId", element);
    var o: Obj = .{};
    o.put("nodes", sortedArray(nodes));
    o.put("resultNodeId", quoted(result));
    return o.done();
}

// ---------------------------------------------------------------- declarations

fn readType(element: Value) Failure!Keyed {
    const kind = try readString("kind", element);
    const product = isName(kind, "product");
    if (!product and !isName(kind, "union")) return invalidShape(fmt("Unsupported semantic type kind '{s}'.", .{text(kind)}));
    const members = if (product) "fields" else "variants";
    try ensureProperties(
        if (product) "Product type" else "Union type",
        &.{ "id", "displayName", "kind", members },
        &.{ "id", "displayName", "kind", members, "typeParameters", "exported" },
        element,
    );
    const id = try readId("id", element);
    const display = try readString("displayName", element);
    const exported = try readVisibility(element);
    const type_parameters = try readTypeParameters(element);
    var listed: []Keyed = undefined;
    if (product) {
        listed = try readTypedArray("fields", "Field", element);
    } else {
        const items = try readArray("variants", element);
        listed = a.alloc(Keyed, items.len) catch oom();
        for (items, listed) |item, *variant| {
            try ensureProperties("Variant", &.{ "id", "displayName", "fields" }, &.{ "id", "displayName", "fields" }, item);
            const variant_id = try readId("id", item);
            const variant_name = try readString("displayName", item);
            const fields = try readTypedArray("fields", "Field", item);
            var v: Obj = .{};
            v.put("id", quoted(variant_id));
            v.put("displayName", quoted(variant_name));
            v.put("fields", sortedArray(fields));
            variant.* = .{ .key = variant_id, .text = v.done() };
        }
    }
    var o: Obj = .{};
    o.put("id", quoted(id));
    o.put("displayName", quoted(display));
    o.putOptional("exported", exported);
    o.putOptional("typeParameters", type_parameters);
    o.put("kind", quoted(kind));
    o.put(members, sortedArray(listed));
    return .{ .key = id, .text = o.done() };
}

fn readEffect(element: Value) Failure!Keyed {
    try ensureProperties("Effect", &.{ "id", "displayName" }, &.{ "id", "displayName", "exported" }, element);
    const id = try readId("id", element);
    var o: Obj = .{};
    o.put("id", quoted(id));
    o.put("displayName", quoted(try readString("displayName", element)));
    o.putOptional("exported", try readVisibility(element));
    return .{ .key = id, .text = o.done() };
}

fn readOperation(element: Value) Failure!Keyed {
    try ensureProperties(
        "Capability operation",
        &.{ "id", "displayName", "parameters", "resultType", "effectId" },
        &.{ "id", "displayName", "parameters", "resultType", "failureType", "effectId", "mayWait" },
        element,
    );
    const failure_type: ?[]const u8 = if (tryProperty("failureType", element)) |value| try readTypeRef(value) else null;
    const id = try readId("id", element);
    const display = try readString("displayName", element);
    const parameters = try readTypedArray("parameters", "Capability parameter", element);
    const result = try readTypeRef(try requireProperty("resultType", element));
    const effect = try readId("effectId", element);
    const may_wait = try readWaitMarker("mayWait", element);
    var o: Obj = .{};
    o.put("id", quoted(id));
    o.put("displayName", quoted(display));
    o.put("parameters", plainArray(parameters));
    o.put("resultType", result);
    o.putOptional("failureType", failure_type);
    o.put("effectId", quoted(effect));
    o.putOptional("mayWait", may_wait);
    return .{ .key = id, .text = o.done() };
}

fn readCapability(element: Value) Failure!Keyed {
    try ensureProperties("Capability", &.{ "id", "displayName", "operations" }, &.{ "id", "displayName", "operations", "taskBound", "exported" }, element);
    const id = try readId("id", element);
    const display = try readString("displayName", element);
    const exported = try readVisibility(element);
    const task_bound = try readWaitMarker("taskBound", element);
    const items = try readArray("operations", element);
    const operations = a.alloc(Keyed, items.len) catch oom();
    for (items, operations) |item, *operation| operation.* = try readOperation(item);
    var o: Obj = .{};
    o.put("id", quoted(id));
    o.put("displayName", quoted(display));
    o.putOptional("exported", exported);
    o.putOptional("taskBound", task_bound);
    o.put("operations", sortedArray(operations));
    return .{ .key = id, .text = o.done() };
}

fn readFunction(element: Value) Failure!Keyed {
    try ensureProperties(
        "Function",
        &.{ "id", "displayName", "parameters", "resultType", "requiredEffects", "body" },
        &.{ "id", "displayName", "parameters", "resultType", "requiredEffects", "body", "typeParameters", "mustNotWait", "exported" },
        element,
    );
    const parameters = try readTypedArray("parameters", "Parameter", element);
    const type_parameters = try readTypeParameters(element);
    const id = try readId("id", element);
    const display = try readString("displayName", element);
    const exported = try readVisibility(element);
    const must_not_wait = try readWaitMarker("mustNotWait", element);
    const result = try readTypeRef(try requireProperty("resultType", element));
    const effects = try readIdArray("requiredEffects", element);
    const body = try readGraph(try requireProperty("body", element));
    var o: Obj = .{};
    o.put("id", quoted(id));
    o.put("displayName", quoted(display));
    o.putOptional("exported", exported);
    o.putOptional("mustNotWait", must_not_wait);
    o.putOptional("typeParameters", type_parameters);
    o.put("parameters", plainArray(parameters));
    o.put("resultType", result);
    o.put("requiredEffects", sortedArray(effects));
    o.put("body", body);
    return .{ .key = id, .text = o.done() };
}

fn readObligation(element: Value) Failure!Keyed {
    const status = try readString("status", element);
    const kind = try readString("kind", element);
    if (!isName(kind, "missingArgument")) return invalidShape(fmt("Unsupported obligation kind '{s}'.", .{text(kind)}));
    const open = isName(status, "open");
    if (open) {
        const names = [_][]const u8{ "id", "kind", "originTransactionId", "callNodeId", "parameterId", "status" };
        try ensureProperties("Open missing-argument obligation", &names, &names, element);
    } else if (isName(status, "resolved")) {
        const names = [_][]const u8{ "id", "kind", "originTransactionId", "callNodeId", "parameterId", "status", "resolutionTransactionId", "resolutionValueNodeId" };
        try ensureProperties("Resolved missing-argument obligation", &names, &names, element);
    } else return invalidShape(fmt("Unsupported obligation status '{s}'.", .{text(status)}));
    const id = try readId("id", element);
    var o: Obj = .{};
    o.put("id", quoted(id));
    o.put("kind", "\"missingArgument\"");
    o.put("originTransactionId", quoted(try readId("originTransactionId", element)));
    o.put("callNodeId", quoted(try readId("callNodeId", element)));
    o.put("parameterId", quoted(try readId("parameterId", element)));
    o.put("status", quoted(status));
    if (!open) {
        o.put("resolutionTransactionId", quoted(try readId("resolutionTransactionId", element)));
        o.put("resolutionValueNodeId", quoted(try readId("resolutionValueNodeId", element)));
    }
    return .{ .key = id, .text = o.done() };
}

fn readDeclarations(name: []const u8, root: Value, comptime reader: fn (Value) Failure!Keyed) Failure![]const u8 {
    const items = try readArray(name, root);
    const out = a.alloc(Keyed, items.len) catch oom();
    for (items, out) |item, *o| o.* = try reader(item);
    return sortedArray(out);
}

/// `SnapshotCodec.toCanonicalJson` for a schema from the first record version on.
fn canonicalJson(source: []const u8) Failure![]const u8 {
    @memset(used, false);
    const root = try parseDocument(source);
    parsed_root = root;
    const top = [_][]const u8{ "schemaVersion", "snapshotId", "types", "effects", "capabilities", "functions", "obligations" };
    try ensureProperties("Snapshot", &top, &(top ++ [_][]const u8{"parentSnapshotId"}), root);
    declared_version = try readInt32("schemaVersion", root);
    if (declared_version < first_supported or declared_version > current) return error.Version;
    if (declared_version < first_record) return error.Legacy;
    const parent: ?[]const u8 = if (tryProperty("parentSnapshotId", root) != null) quoted(try readId("parentSnapshotId", root)) else null;
    const snapshot_id = try readId("snapshotId", root);
    const types = try readDeclarations("types", root, readType);
    const effects = try readDeclarations("effects", root, readEffect);
    const capabilities = try readDeclarations("capabilities", root, readCapability);
    const functions = try readDeclarations("functions", root, readFunction);
    const obligations = try readDeclarations("obligations", root, readObligation);
    for (rows.items, used) |row, is_used| {
        if (is_used and declared_version < row.minimum) return invalidShape(fmt("{s} requires snapshot schemaVersion {d}.", .{ row.name, row.minimum }));
    }
    var o: Obj = .{};
    o.put("schemaVersion", fmt("{d}", .{current}));
    o.put("snapshotId", quoted(snapshot_id));
    o.putOptional("parentSnapshotId", parent);
    o.put("types", types);
    o.put("effects", effects);
    o.put("capabilities", capabilities);
    o.put("functions", functions);
    o.put("obligations", obligations);
    return o.done();
}

// ---------------------------------------------------------------- semantic validation (slice 3a)

// The rules of `SnapshotValidator.validateUncached` that read only the decoded model, listed in
// `validation_codes.txt`. They walk the parsed document after a successful shape read, so every
// object has passed `ensureProperties` and lists keep the document order the F# model keeps (the
// `Map.ofList` lookups below are last-wins in that order). See docs/decisions/zig-tools.md.

const codes_text = @embedFile("validation_codes.txt");
/// An ASCII literal as UTF-16 units.
fn L(comptime ascii: []const u8) Str {
    const S = struct {
        const units = blk: {
            @setEvalBranchQuota(10 * ascii.len + 100);
            var u: [ascii.len]u16 = undefined;
            for (ascii, &u) |c, *d| d.* = c;
            break :blk u;
        };
    };
    return &S.units;
}
const Issue = struct { code: []const u8, message: Str };
var issues: List(Issue) = .empty;
var parsed_root: Value = .json_null;

fn add(code: []const u8, message_units: Str) void {
    var lines = std.mem.tokenizeAny(u8, codes_text, "\r\n");
    while (lines.next()) |line| {
        if (std.mem.eql(u8, line, code)) return issues.append(a, .{ .code = code, .message = message_units }) catch oom();
    }
    @panic("a ported code is missing from validation_codes.txt");
}

/// The F# validator's final `sortBy (Code, Message)`: ordinal, that is per UTF-16 unit.
fn issueLess(_: void, left: Issue, right: Issue) bool {
    return switch (std.mem.order(u8, left.code, right.code)) {
        .lt => true,
        .gt => false,
        .eq => lessUnits(left.message, right.message),
    };
}

fn get(element: Value, name: []const u8) Value {
    return tryProperty(name, element).?;
}

fn str(element: Value, name: []const u8) Str {
    return get(element, name).string;
}

/// An array property, or none when the property is optional and omitted.
fn arrayOf(element: Value, name: []const u8) []const Value {
    return if (tryProperty(name, element)) |value| value.array else &.{};
}

fn cat(parts: []const Str) Str {
    var out: List(u16) = .empty;
    for (parts) |part| out.appendSlice(a, part) catch oom();
    return out.items;
}

fn same(left: Str, right: Str) bool {
    return std.mem.eql(u16, left, right);
}

/// `String.IsNullOrWhiteSpace`.
fn isBlank(units: Str) bool {
    for (units) |unit| if (!isWhiteSpace(unit)) return false;
    return true;
}

/// `StableId.describe`.
fn describe(display: Str, id: Str) Str {
    return cat(&.{ L("'"), display, L("' ("), id, L(")") });
}

fn quote(id: Str) Str {
    return cat(&.{ L("'"), id, L("'") });
}

fn idKey(id: Str) []const u8 {
    return std.mem.sliceAsBytes(id);
}

const Set = std.StringArrayHashMapUnmanaged(void);
const Count = struct { id: Str, count: usize = 0 };
const Counts = std.StringArrayHashMapUnmanaged(Count);

fn countBy(ids: []const Str) Counts {
    var counts: Counts = .empty;
    for (ids) |id| {
        const entry = counts.getOrPut(a, idKey(id)) catch oom();
        if (!entry.found_existing) entry.value_ptr.* = .{ .id = id };
        entry.value_ptr.count += 1;
    }
    return counts;
}

/// `duplicateBy`: once per ID that occurs more than once.
fn duplicates(code: []const u8, comptime what: []const u8, values: []const Value) void {
    const ids = a.alloc(Str, values.len) catch oom();
    for (values, ids) |value, *id| id.* = str(value, "id");
    for (countBy(ids).values()) |entry| {
        if (entry.count > 1) add(code, cat(&.{ L(what ++ " '"), entry.id, L("' occurs more than once.") }));
    }
}

fn blankNames(code: []const u8, comptime what: []const u8, values: []const Value) void {
    for (values) |value| {
        if (isBlank(str(value, "displayName"))) add(code, cat(&.{ L(what ++ " '"), str(value, "id"), L("' has a blank display name.") }));
    }
}

/// `SemanticNodeShape.nestedScopes`: the pattern bindings and graph of each nested scope.
const Scope = struct { bindings: []const Value, graph: Value };

fn one(value: Value) []const Value {
    return a.dupe(Value, &.{value}) catch oom();
}

fn nestedScopes(node: Value) []const Scope {
    var out: List(Scope) = .empty;
    const kind = str(node, "kind");
    if (isName(kind, "conditional")) {
        out.append(a, .{ .bindings = &.{}, .graph = get(node, "whenTrue") }) catch oom();
        out.append(a, .{ .bindings = &.{}, .graph = get(node, "whenFalse") }) catch oom();
    } else if (isName(kind, "boolAnd") or isName(kind, "boolOr")) {
        out.append(a, .{ .bindings = &.{}, .graph = get(node, "right") }) catch oom();
    } else if (isName(kind, "matchUnion")) {
        for (arrayOf(node, "arms")) |arm| {
            const fields = arrayOf(arm, "fieldBindings");
            const bindings = a.alloc(Value, fields.len) catch oom();
            for (fields, bindings) |field, *binding| binding.* = get(field, "binding");
            out.append(a, .{ .bindings = bindings, .graph = get(arm, "body") }) catch oom();
        }
    } else if (isName(kind, "bind")) {
        const body = get(node, "boundBody");
        out.append(a, .{ .bindings = one(get(body, "valueBinding")), .graph = get(body, "body") }) catch oom();
    } else if (isName(kind, "matchOption")) {
        const some = get(node, "someArm");
        out.append(a, .{ .bindings = one(get(some, "valueBinding")), .graph = get(some, "body") }) catch oom();
        out.append(a, .{ .bindings = &.{}, .graph = get(node, "noneBody") }) catch oom();
    } else if (isName(kind, "matchResult")) {
        const ok_arm = get(node, "okArm");
        const err_arm = get(node, "errArm");
        out.append(a, .{ .bindings = one(get(ok_arm, "valueBinding")), .graph = get(ok_arm, "body") }) catch oom();
        out.append(a, .{ .bindings = one(get(err_arm, "errorBinding")), .graph = get(err_arm, "body") }) catch oom();
    }
    return out.items;
}

/// `ValueGraph.collectNodes`: each node, then the nodes of its nested graphs.
fn collectNodes(graph: Value, out: *List(Value)) void {
    for (arrayOf(graph, "nodes")) |node| {
        out.append(a, node) catch oom();
        for (nestedScopes(node)) |scope| collectNodes(scope.graph, out);
    }
}

/// `SemanticFacts.collectPatternBindings`.
fn collectBindings(graph: Value, out: *List(Value)) void {
    for (arrayOf(graph, "nodes")) |node| {
        for (nestedScopes(node)) |scope| {
            out.appendSlice(a, scope.bindings) catch oom();
            collectBindings(scope.graph, out);
        }
    }
}

/// The type references directly below a type reference.
fn typeChildren(type_ref: Value) []const Value {
    const kind = str(type_ref, "kind");
    if (isName(kind, "function")) {
        var out: List(Value) = .empty;
        out.appendSlice(a, arrayOf(type_ref, "parameters")) catch oom();
        out.append(a, get(type_ref, "resultType")) catch oom();
        return out.items;
    }
    if (isName(kind, "named") or isName(kind, "builtinGeneric")) return arrayOf(type_ref, "arguments");
    return &.{};
}

/// `TypeParameters.occurring`, as a set.
fn occurring(type_ref: Value, out: *Set) void {
    if (isName(str(type_ref, "kind"), "typeParameter")) out.put(a, idKey(str(type_ref, "typeParameterId")), {}) catch oom();
    for (typeChildren(type_ref)) |child| occurring(child, out);
}

/// The declared types a type reference names (`directlyReferenced`).
fn named(type_ref: Value, out: *Set) void {
    if (isName(str(type_ref, "kind"), "named")) out.put(a, idKey(str(type_ref, "typeId")), {}) catch oom();
    for (typeChildren(type_ref)) |child| named(child, out);
}

fn idSet(values: []const Value) Set {
    var set: Set = .empty;
    for (values) |value| set.put(a, idKey(str(value, "id")), {}) catch oom();
    return set;
}

/// `Map.ofList` by ID: the last declaration of an ID wins.
fn byId(values: []const Value) std.StringArrayHashMapUnmanaged(Value) {
    var map: std.StringArrayHashMapUnmanaged(Value) = .empty;
    for (values) |value| map.put(a, idKey(str(value, "id")), value) catch oom();
    return map;
}

var effect_names: std.StringArrayHashMapUnmanaged(Value) = .empty;

fn effectLabel(effect_id: Str) Str {
    return if (effect_names.get(idKey(effect_id))) |effect| describe(str(effect, "displayName"), effect_id) else quote(effect_id);
}

const Direct = std.StringArrayHashMapUnmanaged(Set);

/// `reaches[start]` contains `goal`: `goal` is reachable in one or more steps from the declared type `start`.
fn reaches(direct: *const Direct, start: Str, goal: Str) bool {
    if (direct.get(idKey(start)) == null) return false;
    var visited: Set = .empty;
    var stack: List([]const u8) = .empty;
    stack.append(a, idKey(start)) catch oom();
    while (stack.pop()) |at| {
        const next = direct.get(at) orelse continue;
        for (next.keys()) |id| {
            if ((visited.getOrPut(a, id) catch oom()).found_existing) continue;
            if (std.mem.eql(u8, id, idKey(goal))) return true;
            stack.append(a, id) catch oom();
        }
    }
    return false;
}

fn checkRecursion(direct: *const Direct, owner: Str, field: Value, type_ref: Value) void {
    if (isName(str(type_ref, "kind"), "named")) {
        const arguments = arrayOf(type_ref, "arguments");
        var plain = true;
        for (arguments) |argument| plain = plain and isName(str(argument, "kind"), "typeParameter");
        if (arguments.len > 0 and !plain and reaches(direct, str(type_ref, "typeId"), owner))
            add("IrregularRecursiveType", cat(&.{ L("Field "), describe(str(field, "displayName"), str(field, "id")), L(" refers back to its recursive type group with type arguments other than plain type parameters.") }));
    }
    for (typeChildren(type_ref)) |child| checkRecursion(direct, owner, field, child);
}

fn fieldsOf(type_entity: Value) []const Value {
    if (isName(str(type_entity, "kind"), "product")) return arrayOf(type_entity, "fields");
    var out: List(Value) = .empty;
    for (arrayOf(type_entity, "variants")) |variant| out.appendSlice(a, arrayOf(variant, "fields")) catch oom();
    return out.items;
}

/// The type parameters a type or function declares: blank names, duplicate names and IDs.
fn typeParameterDeclarations(comptime what: []const u8, label: Str, declarations: []const Value) void {
    for (declarations) |declaration| {
        if (isBlank(str(declaration, "displayName"))) add("TypeParameterDisplayNameBlank", cat(&.{ L(what ++ " "), label, L(" declares a type parameter with a blank display name.") }));
    }
    const names = a.alloc(Str, declarations.len) catch oom();
    const ids = a.alloc(Str, declarations.len) catch oom();
    for (declarations, names, ids) |declaration, *name, *id| {
        name.* = str(declaration, "displayName");
        id.* = str(declaration, "typeParameterId");
    }
    for (countBy(names).values()) |entry| {
        if (entry.count > 1) add("DuplicateTypeParameterName", cat(&.{ L(what ++ " "), label, L(" declares type parameter '"), entry.id, L("' more than once.") }));
    }
    if (countBy(ids).count() != ids.len) add("DuplicateTypeParameterId", cat(&.{ L(what ++ " "), label, L(" declares one type parameter more than once.") }));
}

const NodeOwner = struct { owner: Str, node: Value };
const TypeParameterOwners = struct { id: Str, first: Str, shared: bool };

fn validate(root: Value) void {
    issues.clearRetainingCapacity();
    const types = arrayOf(root, "types");
    const effects = arrayOf(root, "effects");
    const capabilities = arrayOf(root, "capabilities");
    const functions = arrayOf(root, "functions");
    const obligations = arrayOf(root, "obligations");

    if (tryProperty("parentSnapshotId", root)) |parent| {
        if (same(parent.string, str(root, "snapshotId"))) add("SnapshotParentSelfReference", L("A snapshot cannot name itself as its parent."));
    }

    var fields: List(Value) = .empty;
    var variants: List(Value) = .empty;
    for (types) |type_entity| {
        if (isName(str(type_entity, "kind"), "product")) {
            fields.appendSlice(a, arrayOf(type_entity, "fields")) catch oom();
        } else for (arrayOf(type_entity, "variants")) |variant| {
            variants.append(a, variant) catch oom();
            fields.appendSlice(a, arrayOf(variant, "fields")) catch oom();
        }
    }
    var operations: List(Value) = .empty;
    for (capabilities) |capability| operations.appendSlice(a, arrayOf(capability, "operations")) catch oom();
    var capability_parameters: List(Value) = .empty;
    for (operations.items) |operation| capability_parameters.appendSlice(a, arrayOf(operation, "parameters")) catch oom();
    var parameters: List(Value) = .empty;
    var nodes: List(Value) = .empty;
    var node_owners: std.StringArrayHashMapUnmanaged(NodeOwner) = .empty;
    var bindings: List(Value) = .empty;
    for (functions) |function| {
        parameters.appendSlice(a, arrayOf(function, "parameters")) catch oom();
        const first = nodes.items.len;
        collectNodes(get(function, "body"), &nodes);
        // `nodeOwners`: the last owner of a duplicated node ID wins.
        for (nodes.items[first..]) |node| node_owners.put(a, idKey(str(node, "id")), .{ .owner = str(function, "id"), .node = node }) catch oom();
        collectBindings(get(function, "body"), &bindings);
    }

    duplicates("DuplicateTypeId", "Type ID", types);
    duplicates("DuplicateEffectId", "Effect ID", effects);
    duplicates("DuplicateCapabilityId", "Capability ID", capabilities);
    duplicates("DuplicateFunctionId", "Function ID", functions);
    duplicates("DuplicateFieldId", "Field ID", fields.items);
    duplicates("DuplicateVariantId", "Variant ID", variants.items);
    duplicates("DuplicateCapabilityOperationId", "Capability operation ID", operations.items);
    duplicates("DuplicateCapabilityParameterId", "Capability parameter ID", capability_parameters.items);
    duplicates("DuplicateParameterId", "Parameter ID", parameters.items);
    duplicates("DuplicateGraphNodeId", "Graph node ID", nodes.items);
    duplicates("DuplicatePatternBindingId", "Pattern binding ID", bindings.items);
    duplicates("DuplicateObligationId", "Obligation ID", obligations);

    var open_pairs: List(Str) = .empty;
    var open_calls: std.StringArrayHashMapUnmanaged([2]Str) = .empty;
    for (obligations) |obligation| {
        if (!isName(str(obligation, "status"), "open")) continue;
        // A stable ID holds no line break, so one separates the pair.
        const pair = cat(&.{ str(obligation, "callNodeId"), L("\n"), str(obligation, "parameterId") });
        open_pairs.append(a, pair) catch oom();
        open_calls.put(a, idKey(pair), .{ str(obligation, "callNodeId"), str(obligation, "parameterId") }) catch oom();
    }
    for (countBy(open_pairs.items).values()) |entry| {
        if (entry.count > 1) {
            const pair = open_calls.get(idKey(entry.id)).?;
            add("DuplicateOpenMissingArgumentObligation", cat(&.{ L("Call '"), pair[0], L("' has more than one open obligation for parameter '"), pair[1], L("'.") }));
        }
    }

    blankNames("BlankTypeDisplayName", "Type", types);
    blankNames("BlankEffectDisplayName", "Effect", effects);
    blankNames("BlankCapabilityDisplayName", "Capability", capabilities);
    blankNames("BlankCapabilityOperationDisplayName", "Capability operation", operations.items);
    blankNames("BlankCapabilityParameterDisplayName", "Capability parameter", capability_parameters.items);
    blankNames("BlankFieldDisplayName", "Field", fields.items);
    blankNames("BlankVariantDisplayName", "Variant", variants.items);
    blankNames("BlankFunctionDisplayName", "Function", functions);
    blankNames("BlankParameterDisplayName", "Parameter", parameters.items);
    for (bindings.items) |binding| {
        const display = tryProperty("displayName", binding) orelse continue;
        if (isBlank(display.string)) add("BlankPatternBindingDisplayName", cat(&.{ L("Pattern binding '"), str(binding, "id"), L("' has a blank display name.") }));
    }

    const effect_ids = idSet(effects);
    effect_names = byId(effects);
    const functions_by_id = byId(functions);

    for (operations.items) |operation| {
        const effect_id = str(operation, "effectId");
        if (!effect_ids.contains(idKey(effect_id)))
            add("UnknownCapabilityEffect", cat(&.{ L("Capability operation "), describe(str(operation, "displayName"), str(operation, "id")), L(" references unknown effect "), effectLabel(effect_id), L(".") }));
    }

    var direct: Direct = .empty;
    for (types) |type_entity| {
        var set: Set = .empty;
        for (fieldsOf(type_entity)) |field| named(get(field, "type"), &set);
        direct.put(a, idKey(str(type_entity, "id")), set) catch oom();
    }
    for (types) |type_entity| {
        typeParameterDeclarations("Type", describe(str(type_entity, "displayName"), str(type_entity, "id")), arrayOf(type_entity, "typeParameters"));
        for (fieldsOf(type_entity)) |field| checkRecursion(&direct, str(type_entity, "id"), field, get(field, "type"));
    }
    for (operations.items) |operation| {
        var set: Set = .empty;
        occurring(get(operation, "resultType"), &set);
        for (arrayOf(operation, "parameters")) |parameter| occurring(get(parameter, "type"), &set);
        if (tryProperty("failureType", operation)) |failure| occurring(failure, &set);
        if (set.count() > 0) add("TypeParameterOutsideFunction", cat(&.{ L("Capability operation "), describe(str(operation, "displayName"), str(operation, "id")), L(" uses a function type parameter.") }));
    }

    var owners: std.StringArrayHashMapUnmanaged(TypeParameterOwners) = .empty;
    for ([_][]const Value{ functions, types }, [_]Str{ L("function:"), L("type:") }) |declared, prefix| {
        for (declared) |declaration_owner| {
            const owner = cat(&.{ prefix, str(declaration_owner, "id") });
            for (arrayOf(declaration_owner, "typeParameters")) |declaration| {
                const id = str(declaration, "typeParameterId");
                const entry = owners.getOrPut(a, idKey(id)) catch oom();
                if (!entry.found_existing) entry.value_ptr.* = .{ .id = id, .first = owner, .shared = false };
                if (!same(entry.value_ptr.first, owner)) entry.value_ptr.shared = true;
            }
        }
    }
    for (owners.values()) |entry| {
        if (entry.shared) add("TypeParameterSharedAcrossFunctions", cat(&.{ L("Type parameter '"), entry.id, L("' is declared by more than one function or type.") }));
    }

    for (functions) |function| {
        const label = describe(str(function, "displayName"), str(function, "id"));
        const declarations = arrayOf(function, "typeParameters");
        typeParameterDeclarations("Function", label, declarations);
        var introduced: Set = .empty;
        for (arrayOf(function, "parameters")) |parameter| occurring(get(parameter, "type"), &introduced);
        for (declarations) |declaration| {
            if (!introduced.contains(idKey(str(declaration, "typeParameterId"))))
                add("TypeParameterNotInferable", cat(&.{ L("Function "), label, L(" declares type parameter '"), str(declaration, "displayName"), L("' that none of its parameter types mentions, so a call could not determine it.") }));
        }
    }

    for (functions) |function| {
        const label = describe(str(function, "displayName"), str(function, "id"));
        const required = arrayOf(function, "requiredEffects");
        const ids = a.alloc(Str, required.len) catch oom();
        for (required, ids) |effect, *id| id.* = effect.string;
        for (countBy(ids).values()) |entry| {
            if (entry.count > 1) add("DuplicateFunctionEffectRequirement", cat(&.{ L("Function "), label, L(" lists effect "), effectLabel(entry.id), L(" more than once.") }));
        }
        for (ids) |id| {
            if (!effect_ids.contains(idKey(id))) add("UnknownFunctionEffectRequirement", cat(&.{ L("Function "), label, L(" requires unknown effect "), effectLabel(id), L(".") }));
        }
    }

    for (obligations) |obligation| {
        const id = str(obligation, "id");
        const call_id = str(obligation, "callNodeId");
        const parameter_id = str(obligation, "parameterId");
        const call = node_owners.get(idKey(call_id)) orelse {
            add("InvalidObligationTarget", cat(&.{ L("Obligation '"), id, L("' targets unknown graph node '"), call_id, L("'.") }));
            continue;
        };
        if (!isName(str(call.node, "kind"), "directCall")) {
            add("InvalidObligationTarget", cat(&.{ L("Obligation '"), id, L("' does not target a direct-call node.") }));
            continue;
        }
        var owned = false;
        if (functions_by_id.get(idKey(str(call.node, "targetFunctionId")))) |target| {
            for (arrayOf(target, "parameters")) |parameter| owned = owned or same(str(parameter, "id"), parameter_id);
        }
        if (!owned) add("InvalidObligationParameter", cat(&.{ L("Obligation '"), id, L("' targets a parameter not owned by the call's callee.") }));
        var matching: List(Value) = .empty;
        for (arrayOf(call.node, "arguments")) |argument| {
            if (same(str(argument, "targetParameterId"), parameter_id)) matching.append(a, argument) catch oom();
        }
        if (isName(str(obligation, "status"), "open")) {
            if (matching.items.len > 0) add("SatisfiedOpenObligation", cat(&.{ L("Open obligation '"), id, L("' already has a matching argument binding.") }));
        } else {
            const value_id = str(obligation, "resolutionValueNodeId");
            var exact = false;
            for (matching.items) |argument| exact = exact or same(str(argument, "valueNodeId"), value_id);
            if (!exact) add("InvalidObligationResolution", cat(&.{ L("Resolved obligation '"), id, L("' does not match the call's argument binding.") }));
            const value_owner = node_owners.get(idKey(value_id));
            if (value_owner == null or !same(value_owner.?.owner, call.owner))
                add("InvalidObligationResolutionValue", cat(&.{ L("Resolved obligation '"), id, L("' references a value node outside the call's owning function.") }));
        }
    }

    std.mem.sort(Issue, issues.items, {}, issueLess);
}

/// One line per issue, then `end`; a line break in a message is written `\n` (and `\r`), as in the reader's errors.
fn printIssues(out: *std.Io.Writer) !void {
    for (issues.items) |issue| {
        try out.print("issue {s}: ", .{issue.code});
        for (text(issue.message)) |byte| switch (byte) {
            '\n' => try out.writeAll("\\n"),
            '\r' => try out.writeAll("\\r"),
            else => try out.writeByte(byte),
        };
        try out.writeByte('\n');
    }
    try out.writeAll("end\n");
}

pub fn main(init: std.process.Init) !void {
    a = init.arena.allocator();
    const args = try init.minimal.args.toSlice(a);
    const validating = args.len >= 2 and std.mem.eql(u8, args[1], "--validate");
    if (if (validating) args.len < 3 else args.len < 3 or args.len % 2 == 0) {
        std.debug.print("usage: model-reader <in.json> <out.json> [<in> <out> ...] | model-reader --validate <in.json> [<in.json> ...]\n", .{});
        std.process.exit(64);
    }
    loadTables();
    var buffer: [4096]u8 = undefined;
    var writer = std.Io.File.stdout().writer(init.io, &buffer);
    const out = &writer.interface;
    var index: usize = if (validating) 2 else 1;
    while (index < args.len) : (index += if (validating) 1 else 2) {
        const input = try std.Io.Dir.cwd().readFileAlloc(init.io, args[index], a, .unlimited);
        if (canonicalJson(input)) |json| {
            if (validating) {
                validate(parsed_root);
                try printIssues(out);
            } else {
                try std.Io.Dir.cwd().writeFile(init.io, .{ .sub_path = args[index + 1], .data = json });
                try out.print("ok\n", .{});
            }
        } else |e| switch (e) {
            error.Json => try out.print("error InvalidJson: {s}\n", .{message}),
            error.Shape => try out.print("error InvalidShape: {s}\n", .{message}),
            error.Version => try out.print("error UnsupportedSchemaVersion: {d}\n", .{declared_version}),
            error.Legacy => try out.print("skip legacy schema {d}\n", .{declared_version}),
        }
    }
    try out.flush();
}
