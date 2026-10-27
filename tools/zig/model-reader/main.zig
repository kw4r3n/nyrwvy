//! The Nyrwvy snapshot reader, second stage: snapshot JSON to the canonical JSON the F# writer
//! produces for the shape-decoded model, with every shape check of `SnapshotCodec`'s reader, the
//! schema version and construct minimums. Its reference is `SnapshotCodec.toCanonicalJson`.
//! Semantic validation (`SnapshotValidator`) is ported for the codes in `validation_codes.txt`:
//! model-only rules, type references, the typed graph rules on a port of the node type resolver,
//! effect order, waiting, OneShot payloads and task crossing. See docs/decisions/zig-tools.md.
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
const references_text = @embedFile("node_references.txt");
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
    message = oneLine(reason);
    return error.Json;
}

fn invalidShape(reason: []const u8) Failure {
    message = oneLine(reason);
    return error.Shape;
}

/// One line per input: a backslash in a message is written `\\`, a line break `\n` (and `\r`), so
/// a reader can undo it.
fn oneLine(reason: []const u8) []const u8 {
    var out: List(u8) = .empty;
    for (reason) |byte| switch (byte) {
        '\\' => out.appendSlice(a, "\\\\") catch oom(),
        '\n' => out.appendSlice(a, "\\n") catch oom(),
        '\r' => out.appendSlice(a, "\\r") catch oom(),
        else => out.append(a, byte) catch oom(),
    };
    return out.items;
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
    /// The properties `directValueReferences` reads, in operand order (node_references.txt).
    references: []const []const u8 = &.{},
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

    lines = std.mem.tokenizeScalar(u8, references_text, '\n');
    while (lines.next()) |line| {
        var parts = std.mem.splitScalar(u8, line, '|');
        const kind = kinds.getPtr(parts.next().?) orelse @panic("node_references.txt: unknown kind");
        var names: List([]const u8) = .empty;
        var items = std.mem.tokenizeScalar(u8, parts.next().?, ' ');
        while (items.next()) |item| names.append(a, item) catch oom();
        kind.references = names.items;
    }
    checkTypingTable();
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

/// Reports an issue of a listed code. A code stays unlisted until every site that emits it is
/// ported; the sites already ported run but report nothing (and a misspelt code shows up as an
/// issue the comparison misses).
fn add(code: []const u8, message_units: Str) void {
    var lines = std.mem.tokenizeAny(u8, codes_text, "\r\n");
    while (lines.next()) |line| {
        if (std.mem.eql(u8, line, code)) return issues.append(a, .{ .code = code, .message = message_units }) catch oom();
    }
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

/// The ID back from an `idKey`.
fn keyId(key: []const u8) Str {
    return @as([*]const u16, @ptrCast(@alignCast(key.ptr)))[0 .. key.len / 2];
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

// ---------------------------------------------------------------- type references (slice 3b)

/// A decoded `TypeRef`. `name` is the primitive or generic wire name, or the type, capability or
/// type parameter ID; a function type keeps its parameters in `args` and its result in `result`.
const TypeKind = enum { primitive, named, function, type_parameter, capability, generic };
const TypeRef = struct {
    kind: TypeKind,
    name: Str = &.{},
    args: []const TypeRef = &.{},
    result: ?*const TypeRef = null,

    fn isGeneric(self: TypeRef, comptime name: []const u8) bool {
        return self.kind == .generic and isName(self.name, name);
    }
    fn isPrimitive(self: TypeRef, comptime name: []const u8) bool {
        return self.kind == .primitive and isName(self.name, name);
    }
};

fn typeRefOf(element: Value) TypeRef {
    const kind = str(element, "kind");
    if (isName(kind, "primitive")) return .{ .kind = .primitive, .name = str(element, "primitive") };
    if (isName(kind, "named")) return .{ .kind = .named, .name = str(element, "typeId"), .args = typeRefsOf(arrayOf(element, "arguments")) };
    if (isName(kind, "typeParameter")) return .{ .kind = .type_parameter, .name = str(element, "typeParameterId") };
    if (isName(kind, "capability")) return .{ .kind = .capability, .name = str(element, "capabilityId") };
    if (isName(kind, "builtinGeneric")) return .{ .kind = .generic, .name = str(element, "generic"), .args = typeRefsOf(arrayOf(element, "arguments")) };
    return .{ .kind = .function, .args = typeRefsOf(arrayOf(element, "parameters")), .result = boxed(typeRefOf(get(element, "resultType"))) };
}

fn typeRefsOf(elements: []const Value) []const TypeRef {
    const out = a.alloc(TypeRef, elements.len) catch oom();
    for (elements, out) |element, *t| t.* = typeRefOf(element);
    return out;
}

fn boxed(t: TypeRef) *const TypeRef {
    const p = a.create(TypeRef) catch oom();
    p.* = t;
    return p;
}

fn builtin(comptime name: []const u8, args: []const TypeRef) TypeRef {
    return .{ .kind = .generic, .name = L(name), .args = a.dupe(TypeRef, args) catch oom() };
}

/// Structural equality, as F# compares `TypeRef` values.
fn typeEql(left: TypeRef, right: TypeRef) bool {
    if (left.kind != right.kind or !same(left.name, right.name) or left.args.len != right.args.len) return false;
    for (left.args, right.args) |l, r| if (!typeEql(l, r)) return false;
    if (left.result) |l| return typeEql(l.*, right.result.?.*);
    return true;
}

/// A text that identifies a type reference structurally, for sets keyed by types.
fn typeKey(t: TypeRef, out: *List(u8)) void {
    out.append(a, "pnftcg"[@intFromEnum(t.kind)]) catch oom();
    out.appendSlice(a, idKey(t.name)) catch oom();
    out.append(a, '(') catch oom();
    for (t.args) |arg| typeKey(arg, out);
    if (t.result) |r| typeKey(r.*, out);
    out.append(a, ')') catch oom();
}

/// The type references directly below one (`Function`: parameters, then the result).
fn typeArgs(t: TypeRef) []const TypeRef {
    if (t.result) |r| {
        var out: List(TypeRef) = .empty;
        out.appendSlice(a, t.args) catch oom();
        out.append(a, r.*) catch oom();
        return out.items;
    }
    return t.args;
}

/// `TypeParameters.occurring`: distinct, in first-occurrence order.
fn occurringIds(t: TypeRef, out: *Set) void {
    if (t.kind == .type_parameter) out.put(a, idKey(t.name), {}) catch oom();
    for (typeArgs(t)) |arg| occurringIds(arg, out);
}

const Substitution = std.StringArrayHashMapUnmanaged(TypeRef);

/// `TypeParameters.substitute`.
fn substitute(substitution: *const Substitution, t: TypeRef) TypeRef {
    if (t.kind == .type_parameter) return substitution.get(idKey(t.name)) orelse t;
    var out = t;
    const args = a.alloc(TypeRef, t.args.len) catch oom();
    for (t.args, args) |arg, *d| d.* = substitute(substitution, arg);
    out.args = args;
    if (t.result) |r| out.result = boxed(substitute(substitution, r.*));
    return out;
}

/// `TypeParameters.ofArguments`: the declared parameters zipped with the arguments; the last of a duplicated ID wins.
fn ofArguments(type_entity: Value, arguments: []const TypeRef) Substitution {
    const declared = arrayOf(type_entity, "typeParameters");
    var substitution: Substitution = .empty;
    const n = @min(declared.len, arguments.len);
    for (declared[0..n], arguments[0..n]) |declaration, argument| substitution.put(a, idKey(str(declaration, "typeParameterId")), argument) catch oom();
    return substitution;
}

/// `TypeParameters.instantiate`: a field type seen through a reference with these arguments.
fn instantiate(type_entity: Value, arguments: []const TypeRef, field_type: TypeRef) TypeRef {
    if (arrayOf(type_entity, "typeParameters").len == 0) return field_type;
    const substitution = ofArguments(type_entity, arguments);
    return substitute(&substitution, field_type);
}

/// `TaskTypes.occurs` for builtin generics with these wire names.
fn occursGeneric(t: TypeRef, comptime names: []const []const u8) bool {
    if (t.kind == .generic and inNames(t.name, names)) return true;
    for (typeArgs(t)) |arg| if (occursGeneric(arg, names)) return true;
    return false;
}

fn mentionsScope(t: TypeRef) bool {
    return occursGeneric(t, &.{"taskScope"});
}

fn mentionsTaskTypes(t: TypeRef) bool {
    return occursGeneric(t, &.{ "exception", "oneShot", "task", "taskScope" });
}

/// The declared types a type reference names, with their arguments, outermost first
/// (`Function`: the result before the parameters).
fn namedIn(t: TypeRef, out: *List(TypeRef)) void {
    if (t.kind == .named) out.append(a, t) catch oom();
    if (t.result) |r| namedIn(r.*, out);
    for (t.args) |arg| namedIn(arg, out);
}

var types_by_id: std.StringArrayHashMapUnmanaged(Value) = .empty;
var all_types: []const Value = &.{};
var capability_ids: Set = .empty;
/// `TypeParameters.names`: types before functions, the last declaration of an ID wins.
var type_parameter_names: std.StringArrayHashMapUnmanaged(Str) = .empty;

/// `TaskTypes.bearingWith`: the type mentions what `mentions` looks for, or reaches a declared
/// type whose instantiated fields do; each (type, arguments) pair is inspected once.
fn bearingWith(mentions: *const fn (TypeRef) bool, t: TypeRef, visited: *Set) bool {
    if (mentions(t)) return true;
    var named_list: List(TypeRef) = .empty;
    namedIn(t, &named_list);
    for (named_list.items) |n| {
        var key: List(u8) = .empty;
        typeKey(n, &key);
        if ((visited.getOrPut(a, key.items) catch oom()).found_existing) continue;
        const type_entity = types_by_id.get(idKey(n.name)) orelse continue;
        for (fieldsOf(type_entity)) |field| {
            if (bearingWith(mentions, instantiate(type_entity, n.args, typeRefOf(get(field, "type"))), visited)) return true;
        }
    }
    return false;
}

/// `TaskTypes.bearing`.
fn bearing(t: TypeRef) bool {
    var visited: Set = .empty;
    return bearingWith(&mentionsTaskTypes, t, &visited);
}

fn asciiUnits(bytes: []const u8) Str {
    const out = a.alloc(u16, bytes.len) catch oom();
    for (bytes, out) |b, *u| u.* = b;
    return out;
}

fn number(n: anytype) Str {
    return asciiUnits(fmt("{d}", .{n}));
}

fn joinTypes(types: []const TypeRef, render: *const fn (TypeRef) Str) Str {
    var out: List(u16) = .empty;
    for (types, 0..) |t, i| {
        if (i > 0) out.appendSlice(a, L(", ")) catch oom();
        out.appendSlice(a, render(t)) catch oom();
    }
    return out.items;
}

/// The first declared type with this ID (`List.tryFind`).
fn firstType(type_id: Str) ?Value {
    for (all_types) |type_entity| if (same(str(type_entity, "id"), type_id)) return type_entity;
    return null;
}

const primitive_names = [_][2][]const u8{ .{ "string", "String" }, .{ "bool", "Bool" }, .{ "unit", "Unit" }, .{ "int64", "Int64" }, .{ "float64", "Float64" }, .{ "bits32", "Bits32" }, .{ "bytes", "Bytes" } };
const generic_names = [_][2][]const u8{ .{ "option", "Option" }, .{ "result", "Result" }, .{ "map", "Map" }, .{ "list", "List" }, .{ "set", "Set" }, .{ "oneShot", "OneShot" }, .{ "task", "Task" }, .{ "exception", "Exception" }, .{ "taskScope", "TaskScope" } };

/// The F# case name of a primitive or builtin generic (`string primitive`, `{kind}`).
fn caseName(t: TypeRef) Str {
    for (if (t.kind == .primitive) &primitive_names else &generic_names) |pair| if (isName(t.name, pair[0])) return asciiUnits(pair[1]);
    unreachable;
}

fn typeParameterName(id: Str) Str {
    return type_parameter_names.get(idKey(id)) orelse id;
}

/// `EquatableTypes.describeIn` with the snapshot's type parameter names.
fn describeType(t: TypeRef) Str {
    return switch (t.kind) {
        .primitive => caseName(t),
        .named => blk: {
            const name = if (firstType(t.name)) |type_entity| str(type_entity, "displayName") else t.name;
            break :blk if (t.args.len == 0) name else cat(&.{ name, L("<"), joinTypes(t.args, &describeType), L(">") });
        },
        .capability => cat(&.{ L("capability '"), t.name, L("'") }),
        .function => cat(&.{ L("("), joinTypes(t.args, &describeType), L(") -> "), describeType(t.result.?.*) }),
        .generic => if (t.isGeneric("taskScope")) L("TaskScope") else cat(&.{ caseName(t), L("<"), joinTypes(t.args, &describeType), L(">") }),
        .type_parameter => typeParameterName(t.name),
    };
}

const NotEquatable = struct { path: []const Str, offending: TypeRef };

fn notEquatableAt(path: *List(Str), step: Str, t: TypeRef, visited: *Set) ?NotEquatable {
    path.append(a, step) catch oom();
    defer _ = path.pop();
    return firstNotEquatableAt(path, t, visited);
}

/// `EquatableTypes.firstNotEquatable`: the path to the first part that is not equatable.
fn firstNotEquatableAt(path: *List(Str), t: TypeRef, visited: *Set) ?NotEquatable {
    switch (t.kind) {
        .primitive => if (!t.isPrimitive("float64")) return null,
        .function, .capability, .type_parameter => {},
        .generic => if (!inNames(t.name, &.{ "exception", "oneShot", "task", "taskScope" })) {
            for (t.args, 0..) |arg, i| {
                const step = if (t.isGeneric("list")) L("List item") else if (t.isGeneric("set")) L("Set item") else if (t.isGeneric("option")) L("Option value") else if (t.isGeneric("map"))
                    (if (i == 0) L("Map key") else L("Map value"))
                else if (i == 0) L("Result ok") else L("Result err");
                if (notEquatableAt(path, step, arg, visited)) |found| return found;
            }
            return null;
        },
        .named => {
            var key: List(u8) = .empty;
            typeKey(t, &key);
            if ((visited.getOrPut(a, key.items) catch oom()).found_existing) return null;
            const type_entity = types_by_id.get(idKey(t.name)) orelse return null;
            const type_name = str(type_entity, "displayName");
            if (isName(str(type_entity, "kind"), "product")) {
                for (arrayOf(type_entity, "fields")) |field| {
                    if (notEquatableAt(path, cat(&.{ type_name, L("."), str(field, "displayName") }), instantiate(type_entity, t.args, typeRefOf(get(field, "type"))), visited)) |found| return found;
                }
            } else for (arrayOf(type_entity, "variants")) |variant| {
                for (arrayOf(variant, "fields")) |field| {
                    if (notEquatableAt(path, cat(&.{ type_name, L("."), str(variant, "displayName"), L("."), str(field, "displayName") }), instantiate(type_entity, t.args, typeRefOf(get(field, "type"))), visited)) |found| return found;
                }
            }
            return null;
        },
    }
    return .{ .path = a.dupe(Str, path.items) catch oom(), .offending = t };
}

fn firstNotEquatable(t: TypeRef) ?NotEquatable {
    var path: List(Str) = .empty;
    var visited: Set = .empty;
    return firstNotEquatableAt(&path, t, &visited);
}

fn isEquatable(t: TypeRef) bool {
    return firstNotEquatable(t) == null;
}

/// `EquatableTypes.explainIn`.
fn explainEquatable(t: TypeRef) Str {
    const found = firstNotEquatable(t) orelse return cat(&.{ describeType(t), L(" is equatable.") });
    const reason = switch (found.offending.kind) {
        .primitive => L("Float64 is not equatable because of NaN; compare with Float64Equal"),
        .function => L("function values are not equatable"),
        .generic => L("runtime handles and scopes are not equatable"),
        .capability => L("capability values are not equatable"),
        .type_parameter => L("type parameters are never equatable, because nothing constrains their type arguments, so a Set item or Map key type must be concrete"),
        .named => unreachable,
    };
    var location: List(u16) = .empty;
    for (found.path, 0..) |step, i| {
        location.appendSlice(a, if (i == 0) L(" at ") else L(" -> ")) catch oom();
        location.appendSlice(a, step) catch oom();
    }
    return cat(&.{ describeType(t), L(" is not equatable: "), describeType(found.offending), location.items, L("; "), reason, L(".") });
}

/// `validateTypeRef`. It uses the outer `add`, so no function prefix even inside a graph.
fn validateTypeRef(context: Str, t: TypeRef) void {
    switch (t.kind) {
        .primitive, .type_parameter => {},
        .function => for (typeArgs(t)) |arg| validateTypeRef(context, arg),
        .named => {
            if (types_by_id.get(idKey(t.name))) |type_entity| {
                const declared = arrayOf(type_entity, "typeParameters").len;
                if (declared != t.args.len)
                    add("NamedTypeArityMismatch", cat(&.{ context, L(" uses "), str(type_entity, "displayName"), L(" with "), number(t.args.len), L(" type arguments; exactly "), number(declared), L(" are required.") }));
            } else add("UnknownNamedType", cat(&.{ context, L(" references unknown type '"), t.name, L("'.") }));
            for (t.args) |arg| if (mentionsScope(arg)) {
                add("TaskTypeInDeclaredType", cat(&.{ context, L(" passes TaskScope as a type argument; a task scope cannot be stored in a declared type.") }));
                break;
            };
            for (t.args) |arg| validateTypeRef(context, arg);
        },
        .capability => if (!capability_ids.contains(idKey(t.name)))
            add("UnknownCapabilityType", cat(&.{ context, L(" references unknown capability '"), t.name, L("'.") })),
        .generic => {
            const n = t.args.len;
            if (t.isGeneric("exception")) {
                if (n != 0) add("BuiltinGenericArityMismatch", cat(&.{ context, L(" uses Exception with type arguments; none are allowed.") }));
                return;
            }
            if (t.isGeneric("taskScope")) {
                if (n != 0) add("BuiltinGenericArityMismatch", cat(&.{ context, L(" uses TaskScope with "), number(n), L(" type arguments; none are allowed.") }));
                return;
            }
            const required: usize = if (t.isGeneric("result") or t.isGeneric("map")) 2 else 1;
            if (n != required) {
                if (t.isGeneric("oneShot"))
                    add("BuiltinGenericArityMismatch", cat(&.{ context, L(" uses OneShot with "), number(n), L(" arguments; exactly 1 is required.") }))
                else
                    add("BuiltinGenericArityMismatch", cat(&.{ context, L(" uses "), caseName(t), L(" with "), number(n), L(" type arguments; exactly "), number(required), if (required == 1) L(" is required.") else L(" are required.") }));
            } else if (t.isGeneric("set") and !isEquatable(t.args[0])) {
                add("SetItemTypeNotEquatable", cat(&.{ context, L(" uses a Set whose item type is not equatable: "), explainEquatable(t.args[0]) }));
            } else if (t.isGeneric("map") and !isEquatable(t.args[0])) {
                add("MapKeyTypeNotEquatable", cat(&.{ context, L(" uses a Map whose key type is not equatable: "), explainEquatable(t.args[0]) }));
            }
            if (t.isGeneric("oneShot")) {
                for (t.args) |arg| if (mentionsScope(arg)) {
                    add("OneShotScopePayloadUnsupported", cat(&.{ context, L(" cannot hide a TaskScope inside a OneShot payload.") }));
                    break;
                };
            }
            for (t.args) |arg| validateTypeRef(context, arg);
        },
    }
}

fn validateTypeOf(context: Str, element: Value) void {
    validateTypeRef(context, typeRefOf(element));
}

/// A node's own type arguments; none when omitted.
fn typeArgumentsOf(node: Value) []const TypeRef {
    return typeRefsOf(arrayOf(node, "typeArguments"));
}

/// `variantsById` (the last of a duplicated ID wins): the owning type of each variant.
var variant_owners: std.StringArrayHashMapUnmanaged(Str) = .empty;

/// The 11 `validateTypeRef` sites of `validateGraph`, each on its node alone.
fn validateNodeTypeRefs(node: Value) void {
    const kind = str(node, "kind");
    const id = str(node, "id");
    if (isName(kind, "listEmpty")) {
        validateTypeOf(cat(&.{ L("ListEmpty node '"), id, L("' item type") }), get(node, "itemType"));
    } else if (isName(kind, "setEmpty")) {
        validateTypeRef(cat(&.{ L("SetEmpty node '"), id, L("' type") }), builtin("set", &.{typeRefOf(get(node, "itemType"))}));
    } else if (isName(kind, "mapEmpty")) {
        validateTypeRef(cat(&.{ L("MapEmpty node '"), id, L("' type") }), builtin("map", &.{ typeRefOf(get(node, "keyType")), typeRefOf(get(node, "valueType")) }));
    } else if (isName(kind, "listConstruct")) {
        validateTypeOf(cat(&.{ L("ListConstruct node '"), id, L("' item type") }), get(node, "itemType"));
    } else if (isName(kind, "exceptionRethrow")) {
        validateTypeOf(L("ExceptionRethrow result"), get(node, "resultType"));
    } else if (isName(kind, "oneShotCreate")) {
        validateTypeRef(L("OneShotCreate payload"), builtin("oneShot", &.{typeRefOf(get(node, "valueType"))}));
    } else if (isName(kind, "productConstruct")) {
        // Only for a declared Product (`Some({ Kind = Product _ })`).
        const type_id = str(node, "typeId");
        const type_entity = types_by_id.get(idKey(type_id)) orelse return;
        if (isName(str(type_entity, "kind"), "product"))
            validateTypeRef(cat(&.{ L("Product node '"), id, L("'") }), .{ .kind = .named, .name = type_id, .args = typeArgumentsOf(node) });
    } else if (isName(kind, "unionConstruct")) {
        const owner = variant_owners.get(idKey(str(node, "variantId"))) orelse return;
        validateTypeRef(cat(&.{ L("Union node '"), id, L("'") }), .{ .kind = .named, .name = owner, .args = typeArgumentsOf(node) });
    } else if (isName(kind, "optionNone")) {
        validateTypeOf(cat(&.{ L("Option.None node '"), id, L("'") }), get(node, "itemType"));
    } else if (isName(kind, "resultOk")) {
        validateTypeOf(cat(&.{ L("Result.Ok node '"), id, L("' error type") }), get(node, "errorType"));
    } else if (isName(kind, "resultErr")) {
        validateTypeOf(cat(&.{ L("Result.Err node '"), id, L("' ok type") }), get(node, "okType"));
    }
}

/// `SemanticNodeShape.payloadTypeRefs`: the types a node states in its own payload (the `type`
/// and `targs` properties of model_kinds.txt).
fn payloadTypeRefs(node: Value, out: *List(TypeRef)) void {
    for (node.object) |member| {
        if (isName(member.name, "typeArguments")) {
            out.appendSlice(a, typeRefsOf(member.value.array)) catch oom();
        } else if (inNames(member.name, &.{ "itemType", "keyType", "valueType", "resultType", "okType", "errorType" })) {
            out.append(a, typeRefOf(member.value)) catch oom();
        }
    }
}

fn kindOf(node: Value) *const Kind {
    return kinds.getPtr(text(str(node, "kind"))).?;
}

/// `SemanticNodeShape.directValueReferences`, in operand order: the properties node_references.txt
/// lists for the kind; a node list stands for its items, a binding list for their values.
fn directValueReferences(node: Value, out: *List(Str)) void {
    for (kindOf(node).references) |name| {
        const value = tryProperty(name, node) orelse continue;
        switch (value) {
            .string => |s| out.append(a, s) catch oom(),
            .array => |items| for (items) |item| switch (item) {
                .string => |s| out.append(a, s) catch oom(),
                else => out.append(a, str(item, "valueNodeId")) catch oom(),
            },
            else => unreachable,
        }
    }
}

/// `SemanticNodeShape.dependencies`: direct references and what nested graphs reference from outside themselves.
fn dependencies(node: Value, out: *List(Str)) void {
    directValueReferences(node, out);
    for (nestedScopes(node)) |scope| {
        const nodes = arrayOf(scope.graph, "nodes");
        const local = idSet(nodes);
        for (nodes) |inner| {
            var deps: List(Str) = .empty;
            dependencies(inner, &deps);
            for (deps.items) |dep| if (!local.contains(idKey(dep))) out.append(a, dep) catch oom();
        }
    }
}

/// `hasDependencyCycle` over `nodesById` (the last node of a duplicated ID wins); a reference to
/// an unknown node is no edge.
fn hasDependencyCycle(nodes: []const Value) bool {
    const nodes_by_id = byId(nodes);
    // 0 unvisited, 1 on the path, 2 finished.
    const state = a.alloc(u8, nodes_by_id.count()) catch oom();
    @memset(state, 0);
    const Frame = struct { index: usize, deps: []const Str, next: usize = 0 };
    var frames: List(Frame) = .empty;
    for (0..nodes_by_id.count()) |root| {
        if (state[root] != 0) continue;
        var index = root;
        while (true) {
            var deps: List(Str) = .empty;
            dependencies(nodes_by_id.values()[index], &deps);
            state[index] = 1;
            frames.append(a, .{ .index = index, .deps = deps.items }) catch oom();
            const next: ?usize = descend: while (frames.items.len > 0) {
                const top = &frames.items[frames.items.len - 1];
                if (top.next == top.deps.len) {
                    state[top.index] = 2;
                    _ = frames.pop();
                    continue;
                }
                const dep = top.deps[top.next];
                top.next += 1;
                const at = nodes_by_id.getIndex(idKey(dep)) orelse continue;
                if (state[at] == 1) return true;
                if (state[at] == 0) break :descend at;
            } else null;
            index = next orelse break;
        }
    }
    return false;
}

/// The prefix the per-function loop's `add` gives a message.
fn inFunction(function: Value) Str {
    return cat(&.{ L("In function "), describe(str(function, "displayName"), str(function, "id")), L(": ") });
}

/// The rules of slice 3b: type references of declarations and nodes, undeclared type parameters,
/// task types in declared types and at capability boundaries, and graph dependency cycles.
fn validateTypeReferences(types: []const Value, operations: []const Value, functions: []const Value) void {
    for (types) |type_entity| {
        for (fieldsOf(type_entity)) |field| validateTypeOf(cat(&.{ L("Field "), describe(str(field, "displayName"), str(field, "id")) }), get(field, "type"));
    }
    for (operations) |operation| {
        const label = describe(str(operation, "displayName"), str(operation, "id"));
        for (arrayOf(operation, "parameters")) |parameter| validateTypeOf(cat(&.{ L("Capability parameter "), describe(str(parameter, "displayName"), str(parameter, "id")) }), get(parameter, "type"));
        validateTypeOf(cat(&.{ L("Capability operation "), label, L(" result") }), get(operation, "resultType"));
        if (tryProperty("failureType", operation)) |failure| validateTypeOf(cat(&.{ L("Capability operation "), label, L(" failure") }), failure);
    }
    for (types) |type_entity| {
        const declared = idSetOf(arrayOf(type_entity, "typeParameters"));
        for (fieldsOf(type_entity)) |field| {
            const field_label = describe(str(field, "displayName"), str(field, "id"));
            const field_type = typeRefOf(get(field, "type"));
            var ids: Set = .empty;
            occurringIds(field_type, &ids);
            for (ids.keys()) |key| if (!declared.contains(key))
                add("TypeParameterNotDeclared", cat(&.{ L("Field "), field_label, L(" uses type parameter '"), typeParameterName(keyId(key)), L("', which its type "), describe(str(type_entity, "displayName"), str(type_entity, "id")), L(" does not declare.") }));
            if (mentionsScope(field_type))
                add("TaskTypeInDeclaredType", cat(&.{ L("Field "), field_label, L(" uses TaskScope; a task scope cannot be stored in a declared type.") }));
        }
    }
    for (operations) |operation| {
        var bears = bearing(typeRefOf(get(operation, "resultType")));
        for (arrayOf(operation, "parameters")) |parameter| bears = bears or bearing(typeRefOf(get(parameter, "type")));
        if (tryProperty("failureType", operation)) |failure| bears = bears or bearing(typeRefOf(failure));
        if (bears) add("TaskTypeAtCapabilityBoundary", cat(&.{ L("Capability operation "), describe(str(operation, "displayName"), str(operation, "id")), L(" uses Task, OneShot or TaskScope, directly or through a declared type, which cannot cross a capability boundary.") }));
    }

    for (functions) |function| {
        const label = describe(str(function, "displayName"), str(function, "id"));
        var nodes: List(Value) = .empty;
        collectNodes(get(function, "body"), &nodes);
        var bindings: List(Value) = .empty;
        collectBindings(get(function, "body"), &bindings);

        const declared = idSetOf(arrayOf(function, "typeParameters"));
        var mentioned: List(TypeRef) = .empty;
        mentioned.append(a, typeRefOf(get(function, "resultType"))) catch oom();
        for (arrayOf(function, "parameters")) |parameter| mentioned.append(a, typeRefOf(get(parameter, "type"))) catch oom();
        for (bindings.items) |binding| mentioned.append(a, typeRefOf(get(binding, "type"))) catch oom();
        for (nodes.items) |node| payloadTypeRefs(node, &mentioned);
        for (mentioned.items) |t| {
            var ids: Set = .empty;
            occurringIds(t, &ids);
            for (ids.keys()) |key| if (!declared.contains(key))
                add("TypeParameterNotDeclared", cat(&.{ L("Function "), label, L(" uses type parameter '"), typeParameterName(keyId(key)), L("' that it does not declare.") }));
        }

        validateTypeOf(cat(&.{ L("Function "), label, L(" result") }), get(function, "resultType"));
        for (arrayOf(function, "parameters")) |parameter| validateTypeOf(cat(&.{ L("Parameter "), describe(str(parameter, "displayName"), str(parameter, "id")) }), get(parameter, "type"));
        for (bindings.items) |binding| validateTypeOf(cat(&.{ L("Pattern binding '"), str(binding, "id"), L("'") }), get(binding, "type"));

        // `validateGraph` visits each node of each nested graph once, as `collectNodes` does.
        for (nodes.items) |node| validateNodeTypeRefs(node);
        if (hasDependencyCycle(nodes.items))
            add("GraphNodeDependencyCycle", cat(&.{ inFunction(function), L("Function "), label, L(" contains a cycle between value-producing graph nodes.") }));
    }
}

/// The declared type parameter IDs.
fn idSetOf(declarations: []const Value) Set {
    var set: Set = .empty;
    for (declarations) |declaration| set.put(a, idKey(str(declaration, "typeParameterId")), {}) catch oom();
    return set;
}

// ---------------------------------------------------------------- node typing (slice 3c)

// `SemanticFacts.createCompletionResolver`: the completion (normal type or abrupt) of each node of
// one function. The F# resolver resolves every node eagerly when it is created, in a post-order
// search over `nodesById` (ordinal ID order), and memoises every result, including the `None` of a
// node met again on the current path; a later query only reads the memo. So the order in which the
// validator asks does not matter, but the order of the search does, and it is ported exactly:
// `directValueReferences` in operand order (node_references.txt), then the nested graphs' nodes.

const Completion = union(enum) { normal: TypeRef, aborts };

fn normalType(completion: ?Completion) ?TypeRef {
    return if (completion) |c| switch (c) {
        .normal => |t| t,
        .aborts => null,
    } else null;
}

/// `GraphCompletion.merge`: unknown or inconsistent arms stay unknown; abrupt arms are ignored.
fn mergeCompletions(arms: []const ?Completion) ?Completion {
    if (arms.len == 0) return null;
    for (arms) |arm| if (arm == null) return null;
    var first: ?TypeRef = null;
    for (arms) |arm| switch (arm.?) {
        .normal => |t| if (first) |f| {
            if (!typeEql(f, t)) return null;
        } else {
            first = t;
        },
        .aborts => {},
    };
    return if (first) |f| .{ .normal = f } else .aborts;
}

const ValueMap = std.StringArrayHashMapUnmanaged(Value);
var functions_by_id: ValueMap = .empty;
var operations_by_id: ValueMap = .empty;
var product_fields_by_id: ValueMap = .empty;
/// `ProductFieldOwners`: the Product that declares each Product field (the last wins).
var product_field_owners: ValueMap = .empty;

/// `TypeParameters.matchType`, extending `substitution` in place; false on a conflict (the caller
/// then discards the substitution).
fn matchType(substitution: *Substitution, declared: TypeRef, actual: TypeRef) bool {
    if (declared.kind == .type_parameter) {
        if (substitution.get(idKey(declared.name))) |bound| return typeEql(bound, actual);
        substitution.put(a, idKey(declared.name), actual) catch oom();
        return true;
    }
    const structural = declared.kind == actual.kind and declared.args.len == actual.args.len and switch (declared.kind) {
        .function => true,
        .generic, .named => same(declared.name, actual.name),
        else => false,
    };
    if (structural) {
        const declared_args = typeArgs(declared);
        for (declared_args, typeArgs(actual)) |d, x| if (!matchType(substitution, d, x)) return false;
        return true;
    }
    return typeEql(declared, actual);
}

const Pair = struct { declared: TypeRef, actual: TypeRef };

/// `TypeParameters.matchFrom`.
fn matchFrom(initial: *const Substitution, pairs: []const Pair) ?Substitution {
    var substitution = initial.clone(a) catch oom();
    for (pairs) |pair| if (!matchType(&substitution, pair.declared, pair.actual)) return null;
    return substitution;
}

fn matchAll(pairs: []const Pair) ?Substitution {
    return matchFrom(&.empty, pairs);
}

/// The first parameter of a function with this ID (`List.tryFind`).
fn parameterOf(function: Value, parameter_id: Str) ?Value {
    for (arrayOf(function, "parameters")) |parameter| if (same(str(parameter, "id"), parameter_id)) return parameter;
    return null;
}

fn bindsParameter(context: []const Value, parameter_id: Str) bool {
    for (context) |binding| if (same(str(binding, "targetParameterId"), parameter_id)) return true;
    return false;
}

/// The parameter types a callback's context leaves unbound, in order.
fn unboundTypes(callback: Value, context: []const Value) []const TypeRef {
    var out: List(TypeRef) = .empty;
    for (arrayOf(callback, "parameters")) |parameter| {
        if (!bindsParameter(context, str(parameter, "id"))) out.append(a, typeRefOf(get(parameter, "type"))) catch oom();
    }
    return out.items;
}

const Resolver = struct {
    nodes_by_id: ValueMap = .empty,
    parameters_by_id: ValueMap = .empty,
    bindings_by_id: ValueMap = .empty,
    resolved: std.StringHashMapUnmanaged(?Completion) = .empty,
    /// The F# `visiting` set: the nodes on the current resolution path.
    visiting: Set = .empty,

    fn init(function: Value) Resolver {
        var self: Resolver = .{};
        var nodes: List(Value) = .empty;
        collectNodes(get(function, "body"), &nodes);
        self.nodes_by_id = byId(nodes.items);
        self.parameters_by_id = byId(arrayOf(function, "parameters"));
        var bindings: List(Value) = .empty;
        collectBindings(get(function, "body"), &bindings);
        self.bindings_by_id = byId(bindings.items);
        self.resolveAll();
        return self;
    }

    /// The post-order search of the F# constructor.
    fn resolveAll(self: *Resolver) void {
        const ids = a.dupe([]const u8, self.nodes_by_id.keys()) catch oom();
        std.mem.sort([]const u8, ids, {}, lessKey);
        var finished: Set = .empty;
        var on_path: Set = .empty;
        const Frame = struct { id: []const u8, reads: []const Str, next: usize = 0 };
        var frames: List(Frame) = .empty;
        for (ids) |root| {
            if (finished.contains(root)) continue;
            self.enter(root, &on_path, &frames);
            while (frames.items.len > 0) {
                const top = &frames.items[frames.items.len - 1];
                if (top.next == top.reads.len) {
                    const id = top.id;
                    _ = frames.pop();
                    _ = on_path.swapRemove(id);
                    finished.put(a, id, {}) catch oom();
                    _ = self.completion(keyId(id));
                    continue;
                }
                const next = top.reads[top.next];
                top.next += 1;
                if (on_path.contains(idKey(next))) {
                    self.resolved.put(a, idKey(next), null) catch oom();
                } else if (!finished.contains(idKey(next))) self.enter(idKey(next), &on_path, &frames);
            }
        }
    }

    fn enter(self: *Resolver, id: []const u8, on_path: *Set, frames: anytype) void {
        const node = self.nodes_by_id.get(id) orelse return;
        on_path.put(a, id, {}) catch oom();
        var reads: List(Str) = .empty;
        directValueReferences(node, &reads);
        for (nestedScopes(node)) |scope| for (arrayOf(scope.graph, "nodes")) |nested| reads.append(a, str(nested, "id")) catch oom();
        frames.append(a, .{ .id = id, .reads = reads.items }) catch oom();
    }

    fn nodeType(self: *Resolver, id: Str) ?TypeRef {
        return normalType(self.completion(id));
    }

    /// `SemanticFacts.graphCompletion`: abrupt when any stored node is, else the result node's.
    fn graph(self: *Resolver, graph_value: Value) ?Completion {
        for (arrayOf(graph_value, "nodes")) |node| {
            if (self.completion(str(node, "id"))) |c| if (c == .aborts) return .aborts;
        }
        return self.completion(str(graph_value, "resultNodeId"));
    }

    /// `tryCompletion`.
    fn completion(self: *Resolver, id: Str) ?Completion {
        if (self.resolved.get(idKey(id))) |cached| return cached;
        const result: ?Completion = if (self.visiting.contains(idKey(id))) null else blk: {
            self.visiting.put(a, idKey(id), {}) catch oom();
            defer _ = self.visiting.swapRemove(idKey(id));
            break :blk self.structured(id);
        };
        self.resolved.put(a, idKey(id), result) catch oom();
        return result;
    }

    fn structured(self: *Resolver, id: Str) ?Completion {
        const node = self.nodes_by_id.get(idKey(id)) orelse return null;
        const kind = str(node, "kind");
        if (isName(kind, "abort")) return .aborts;
        if (isName(kind, "conditional")) {
            const when_true = self.graph(get(node, "whenTrue"));
            return mergeCompletions(&.{ when_true, self.graph(get(node, "whenFalse")) });
        }
        if (isName(kind, "matchUnion")) {
            var arms: List(?Completion) = .empty;
            for (arrayOf(node, "arms")) |arm| arms.append(a, self.graph(get(arm, "body"))) catch oom();
            return mergeCompletions(arms.items);
        }
        if (isName(kind, "bind")) return self.graph(get(get(node, "boundBody"), "body"));
        if (isName(kind, "matchOption")) {
            const some = self.graph(get(get(node, "someArm"), "body"));
            return mergeCompletions(&.{ some, self.graph(get(node, "noneBody")) });
        }
        if (isName(kind, "matchResult")) {
            const ok = self.graph(get(get(node, "okArm"), "body"));
            return mergeCompletions(&.{ ok, self.graph(get(get(node, "errArm"), "body")) });
        }
        return if (self.infer(node)) |t| .{ .normal = t } else null;
    }

    /// `callbackResult`: the callback's result type at the type arguments its context and
    /// iteration values determine.
    fn callbackResult(self: *Resolver, function: Value, context: []const Value, iteration: []const TypeRef) TypeRef {
        var pairs: List(Pair) = .empty;
        for (context) |binding| {
            const parameter = parameterOf(function, str(binding, "targetParameterId"));
            const actual = self.nodeType(str(binding, "valueNodeId"));
            if (parameter != null and actual != null) pairs.append(a, .{ .declared = typeRefOf(get(parameter.?, "type")), .actual = actual.? }) catch oom();
        }
        const unbound = unboundTypes(function, context);
        const n = @min(unbound.len, iteration.len);
        for (unbound[0..n], iteration[0..n]) |declared, actual| pairs.append(a, .{ .declared = declared, .actual = actual }) catch oom();
        const result_type = typeRefOf(get(function, "resultType"));
        const substitution = matchAll(pairs.items) orelse return result_type;
        return substitute(&substitution, result_type);
    }

    fn operand(self: *Resolver, node: Value, name: []const u8) ?TypeRef {
        return self.nodeType(str(node, name));
    }

    fn functionAt(node: Value, name: []const u8) ?Value {
        return functions_by_id.get(idKey(str(node, name)));
    }

    /// `inferNodeType` for a node of the function (not a structured one).
    fn infer(self: *Resolver, node: Value) ?TypeRef {
        const kind = text(str(node, "kind"));
        if (constantType(kind)) |t| return t;
        const k = std.meta.stringToEnum(Typed, kind) orelse return null;
        const context = arrayOf(node, "context");
        switch (k) {
            .parameterReference => return if (self.parameters_by_id.get(idKey(str(node, "parameterId")))) |p| typeRefOf(get(p, "type")) else null,
            .patternReference => return if (self.bindings_by_id.get(idKey(str(node, "patternBindingId")))) |b| typeRefOf(get(b, "type")) else null,
            .mapEmpty => return builtin("map", &.{ typeRefOf(get(node, "keyType")), typeRefOf(get(node, "valueType")) }),
            .mapInsert, .mapRemove, .mapGetOrDefault, .mapGet, .mapKeys, .mapValues => {
                const t = self.operand(node, "mapNodeId") orelse return null;
                if (!t.isGeneric("map") or t.args.len != 2) return null;
                return switch (k) {
                    .mapGetOrDefault => t.args[1],
                    .mapGet => builtin("option", &.{t.args[1]}),
                    .mapKeys => builtin("list", &.{t.args[0]}),
                    .mapValues => builtin("list", &.{t.args[1]}),
                    else => t,
                };
            },
            .mapUnion => {
                const t = self.operand(node, "leftMapNodeId") orelse return null;
                return if (t.isGeneric("map") and t.args.len == 2) t else null;
            },
            .mapFilter => {
                const t = self.operand(node, "sourceMapNodeId") orelse return null;
                return if (t.isGeneric("map") and t.args.len == 2) t else null;
            },
            .mapMapValues => {
                const t = self.operand(node, "sourceMapNodeId");
                const mapper = functionAt(node, "mapperFunctionId");
                if (t == null or mapper == null or !t.?.isGeneric("map") or t.?.args.len != 2) return null;
                return builtin("map", &.{ t.?.args[0], self.callbackResult(mapper.?, context, t.?.args) });
            },
            .optionGetOrDefault => {
                const t = self.operand(node, "optionNodeId") orelse return null;
                return if (t.isGeneric("option") and t.args.len == 1) t.args[0] else null;
            },
            .resultGetOrDefault => {
                const t = self.operand(node, "resultNodeId") orelse return null;
                return if (t.isGeneric("result") and t.args.len == 2) t.args[0] else null;
            },
            .optionOkOr => {
                const option = self.operand(node, "optionNodeId");
                const error_type = self.operand(node, "errorNodeId");
                if (option == null or error_type == null or !option.?.isGeneric("option") or option.?.args.len != 1) return null;
                return builtin("result", &.{ option.?.args[0], error_type.? });
            },
            .resultOkValue, .resultErrValue => {
                const t = self.operand(node, "valueNodeId") orelse return null;
                if (!t.isGeneric("result") or t.args.len != 2) return null;
                return builtin("option", &.{t.args[if (k == .resultOkValue) 0 else 1]});
            },
            .optionMap, .optionAndThen => {
                const t = self.operand(node, "sourceOptionNodeId");
                const f = functionAt(node, if (k == .optionMap) "mapperFunctionId" else "binderFunctionId");
                if (t == null or f == null or !t.?.isGeneric("option") or t.?.args.len != 1) return null;
                const result = self.callbackResult(f.?, context, t.?.args);
                return if (k == .optionMap) builtin("option", &.{result}) else result;
            },
            .resultMap, .resultMapErr, .resultAndThen => {
                const t = self.operand(node, "sourceResultNodeId");
                const f = functionAt(node, if (k == .resultAndThen) "binderFunctionId" else "mapperFunctionId");
                if (t == null or f == null or !t.?.isGeneric("result") or t.?.args.len != 2) return null;
                const ok_type = t.?.args[0];
                const error_type = t.?.args[1];
                return switch (k) {
                    .resultMap => builtin("result", &.{ self.callbackResult(f.?, context, &.{ok_type}), error_type }),
                    .resultMapErr => builtin("result", &.{ ok_type, self.callbackResult(f.?, context, &.{error_type}) }),
                    else => self.callbackResult(f.?, context, &.{ok_type}),
                };
            },
            .listMinBy, .listMaxBy => {
                const t = self.operand(node, "sourceListNodeId") orelse return null;
                return if (t.isGeneric("list") and t.args.len == 1) builtin("option", t.args) else null;
            },
            .setFilter, .setMap, .setToList, .setInsert, .setRemove, .setUnion, .setIntersection, .setDifference => {
                const t = self.nodeType(str(node, switch (k) {
                    .setFilter, .setMap => "sourceSetNodeId",
                    .setInsert, .setRemove, .setToList => "setNodeId",
                    else => "leftSetNodeId",
                })) orelse return null;
                if (k == .setMap) {
                    const mapper = functionAt(node, "mapperFunctionId");
                    if (mapper == null or !t.isGeneric("set") or t.args.len != 1) return null;
                    return builtin("set", &.{self.callbackResult(mapper.?, context, t.args)});
                }
                if (!t.isGeneric("set") or t.args.len != 1) return null;
                return if (k == .setToList) builtin("list", t.args) else t;
            },
            .setEmpty => return builtin("set", &.{typeRefOf(get(node, "itemType"))}),
            .setFromList => {
                const t = self.operand(node, "listNodeId") orelse return null;
                return if (t.isGeneric("list") and t.args.len == 1) builtin("set", t.args) else null;
            },
            .listEmpty, .listConstruct => return builtin("list", &.{typeRefOf(get(node, "itemType"))}),
            .listItemAt, .listLast => {
                const t = self.operand(node, "listNodeId") orelse return null;
                return if (t.isGeneric("list") and t.args.len == 1) builtin("option", t.args) else null;
            },
            .listAppendItem, .listSlice, .listSetAt, .listInsertAt, .listRemoveAt, .listReverse, .listDropLast, .listFilter, .listSortBy => {
                const t = self.nodeType(str(node, if (k == .listFilter or k == .listSortBy) "sourceListNodeId" else "listNodeId")) orelse return null;
                return if (t.isGeneric("list") and t.args.len == 1) builtin("list", t.args) else null;
            },
            .listConcat => {
                const left = self.operand(node, "leftListNodeId");
                const right = self.operand(node, "rightListNodeId");
                if (left == null or right == null or !left.?.isGeneric("list") or left.?.args.len != 1 or !right.?.isGeneric("list") or right.?.args.len != 1) return null;
                return if (typeEql(left.?.args[0], right.?.args[0])) builtin("list", left.?.args) else null;
            },
            .listFold, .mapFold, .setFold => return self.operand(node, "seedNodeId"),
            .listScan => return if (self.operand(node, "seedNodeId")) |seed| builtin("list", &.{seed}) else null,
            .listEffectFold => {
                const seed = self.operand(node, "seedNodeId");
                const folder = functionAt(node, "folderFunctionId");
                if (seed == null or folder == null) return seed;
                const source = self.operand(node, "sourceListNodeId");
                var iteration: [2]TypeRef = .{ seed.?, undefined };
                var n: usize = 1;
                if (source != null and source.?.isGeneric("list") and source.?.args.len == 1) {
                    iteration[1] = source.?.args[0];
                    n = 2;
                }
                const result = self.callbackResult(folder.?, context, iteration[0..n]);
                return if (result.isGeneric("result") and result.args.len == 2 and typeEql(result.args[0], seed.?)) result else seed;
            },
            .useResource => {
                const body = functionAt(node, "bodyFunctionId") orelse return null;
                const acquired = self.operand(node, "acquireNodeId");
                const resource: []const TypeRef = if (acquired != null and acquired.?.isGeneric("result") and acquired.?.args.len == 2) acquired.?.args[0..1] else &.{};
                return self.callbackResult(body, context, resource);
            },
            .exceptionCatch => {
                const body = functionAt(node, "bodyFunctionId") orelse return null;
                return self.callbackResult(body, arrayOf(node, "bodyContext"), &.{.{ .kind = .primitive, .name = L("unit") }});
            },
            .cancellationShield => {
                const body = functionAt(node, "bodyFunctionId") orelse return null;
                return self.callbackResult(body, context, &.{.{ .kind = .primitive, .name = L("unit") }});
            },
            .taskScope => {
                const body = functionAt(node, "bodyFunctionId") orelse return null;
                return self.callbackResult(body, context, &.{builtin("taskScope", &.{})});
            },
            .exceptionRethrow => return typeRefOf(get(node, "resultType")),
            .taskStart => {
                const item = self.operand(node, "itemNodeId");
                const worker = functionAt(node, "workerFunctionId");
                if (item == null or worker == null) return null;
                return builtin("task", &.{self.callbackResult(worker.?, context, &.{item.?})});
            },
            .taskWait => {
                const t = self.operand(node, "tasksNodeId") orelse return null;
                if (!t.isGeneric("list") or t.args.len != 1 or !t.args[0].isGeneric("task") or t.args[0].args.len != 1) return null;
                return builtin("list", &.{builtin("option", t.args[0].args)});
            },
            .oneShotCreate => return builtin("oneShot", &.{typeRefOf(get(node, "valueType"))}),
            .oneShotWait => {
                const t = self.operand(node, "cellNodeId") orelse return null;
                return if (t.isGeneric("oneShot") and t.args.len == 1) builtin("option", t.args) else null;
            },
            .parallelAll => {
                const t = self.operand(node, "sourceListNodeId");
                const worker = functionAt(node, "workerFunctionId");
                if (t == null or worker == null or !t.?.isGeneric("list") or t.?.args.len != 1) return null;
                const result = self.callbackResult(worker.?, context, t.?.args);
                if (!result.isGeneric("result") or result.args.len != 2) return null;
                return builtin("result", &.{ builtin("list", result.args[0..1]), result.args[1] });
            },
            .listMap, .listFlatMap, .mapFromList => {
                const t = self.operand(node, "sourceListNodeId");
                const f = functionAt(node, if (k == .mapFromList) "keyOfFunctionId" else "mapperFunctionId");
                if (t == null or f == null or !t.?.isGeneric("list") or t.?.args.len != 1) return null;
                const result = self.callbackResult(f.?, context, t.?.args);
                return switch (k) {
                    .listMap => builtin("list", &.{result}),
                    .listFlatMap => if (result.isGeneric("list") and result.args.len == 1) result else null,
                    else => builtin("map", &.{ result, t.?.args[0] }),
                };
            },
            .listZipWith => {
                const left = self.operand(node, "leftListNodeId");
                const right = self.operand(node, "rightListNodeId");
                const combiner = functionAt(node, "combinerFunctionId");
                if (left == null or right == null or combiner == null or !left.?.isGeneric("list") or left.?.args.len != 1 or !right.?.isGeneric("list") or right.?.args.len != 1) return null;
                return builtin("list", &.{self.callbackResult(combiner.?, context, &.{ left.?.args[0], right.?.args[0] })});
            },
            .capabilityConstruct => return .{ .kind = .capability, .name = str(node, "capabilityId") },
            .functionReference => {
                const target = functionAt(node, "targetFunctionId") orelse return null;
                return functionType(target);
            },
            .functionInvoke => {
                const t = self.operand(node, "functionValueNodeId") orelse return null;
                return if (t.kind == .function) t.result.?.* else null;
            },
            .directCall => {
                const target = functionAt(node, "targetFunctionId") orelse return null;
                return self.callbackResult(target, arrayOf(node, "arguments"), &.{});
            },
            .capabilityInvoke => return if (operations_by_id.get(idKey(str(node, "operationId")))) |operation| typeRefOf(get(operation, "resultType")) else null,
            .productConstruct => return .{ .kind = .named, .name = str(node, "typeId"), .args = typeArgumentsOf(node) },
            .productUpdate => {
                const type_id = str(node, "typeId");
                if (self.operand(node, "sourceNodeId")) |source| if (source.kind == .named and same(source.name, type_id)) return source;
                return .{ .kind = .named, .name = type_id };
            },
            .fieldAccess => {
                const field_id = idKey(str(node, "fieldId"));
                const field = product_fields_by_id.get(field_id) orelse return null;
                const owner = product_field_owners.get(field_id) orelse return null;
                const field_type = typeRefOf(get(field, "type"));
                if (arrayOf(owner, "typeParameters").len == 0) return field_type;
                const source = self.operand(node, "sourceNodeId") orelse return null;
                if (source.kind != .named or !same(source.name, str(owner, "id"))) return null;
                return instantiate(owner, source.args, field_type);
            },
            .unionConstruct => return if (variant_owners.get(idKey(str(node, "variantId")))) |owner| .{ .kind = .named, .name = owner, .args = typeArgumentsOf(node) } else null,
            .optionNone => return builtin("option", &.{typeRefOf(get(node, "itemType"))}),
            .optionSome => return if (self.operand(node, "valueNodeId")) |t| builtin("option", &.{t}) else null,
            .resultOk => return if (self.operand(node, "valueNodeId")) |t| builtin("result", &.{ t, typeRefOf(get(node, "errorType")) }) else null,
            .resultErr => return if (self.operand(node, "errorNodeId")) |t| builtin("result", &.{ typeRefOf(get(node, "okType")), t }) else null,
        }
    }
};

/// `TypeRef.Function` of a function's declared signature.
fn functionType(function: Value) TypeRef {
    var parameters: List(TypeRef) = .empty;
    for (arrayOf(function, "parameters")) |parameter| parameters.append(a, typeRefOf(get(parameter, "type"))) catch oom();
    return .{ .kind = .function, .args = parameters.items, .result = boxed(typeRefOf(get(function, "resultType"))) };
}

fn lessKey(_: void, left: []const u8, right: []const u8) bool {
    return lessUnits(keyId(left), keyId(right));
}

/// The kinds `inferNodeType` types from operands or payload. Every other kind is constant, or
/// has no type of its own (`abort` and the structured kinds, which `tryCompletion` handles).
const Typed = enum {
    parameterReference, patternReference, mapEmpty, mapInsert, mapRemove, mapUnion, mapGetOrDefault, mapMapValues,
    optionGetOrDefault, resultGetOrDefault, optionOkOr, resultOkValue, resultErrValue, optionMap, optionAndThen,
    resultMap, resultMapErr, resultAndThen, listMinBy, listMaxBy, mapGet, mapFilter, setFilter, setMap, mapKeys,
    mapValues, setEmpty, setInsert, setRemove, setUnion, setIntersection, setDifference, setToList, setFromList,
    listEmpty, listConstruct, listItemAt, listLast, listAppendItem, listSlice, listSetAt, listInsertAt, listRemoveAt,
    listReverse, listDropLast, listFilter, listSortBy, listConcat, listFold, listScan, mapFold, setFold,
    listEffectFold, useResource, exceptionCatch, exceptionRethrow, cancellationShield, taskScope, taskStart,
    taskWait, oneShotCreate, oneShotWait, parallelAll, listMap, listZipWith, listFlatMap, mapFromList,
    capabilityConstruct, functionReference, functionInvoke, directCall, capabilityInvoke, productConstruct,
    productUpdate, fieldAccess, unionConstruct, optionNone, optionSome, resultOk, resultErr,
};
const untyped_kinds = [_][]const u8{ "abort", "conditional", "matchUnion", "bind", "matchOption", "matchResult" };

/// The kinds with one fixed type, by that type.
const constant_kinds = [_]struct { []const u8, []const []const u8 }{
    .{ "string", &.{ "stringLiteral", "stringConcat", "stringToLower", "stringToUpper", "stringReplace", "stringTrim", "stringTrimStart", "stringTrimEnd", "stringDecodeUtf8Lossy", "stringEncodeUtf8", "stringJoin", "stringRepeat", "stringSlice", "bytesToLatin1", "int64ToString", "int64ToHexString", "float64ToString", "exceptionMessage" } },
    .{ "listInt64", &.{"listRange"} },
    .{ "bool", &.{ "boolLiteral", "boolNot", "boolAnd", "boolOr", "stringIsEmpty", "stringContains", "stringStartsWith", "stringEndsWith", "stringEqual", "stringLessThan", "valueEqual", "int64Equal", "int64LessThan", "int64LessThanOrEqual", "int64GreaterThan", "int64GreaterThanOrEqual", "float64Equal", "float64LessThan", "float64IsNaN", "float64IsFinite", "float64IsInfinite", "listAny", "listAll", "mapContainsKey", "setContains", "listContains", "setIsSubset", "optionIsSome", "resultIsOk", "cancellationRequested", "exceptionIsCancellation", "oneShotComplete" } },
    .{ "unit", &.{ "unitLiteral", "taskCancel" } },
    .{ "int64", &.{ "int64Literal", "int64WrappingAdd", "int64WrappingSubtract", "int64WrappingMultiply", "int64FloorDivide", "int64FloorModulo", "int64Min", "int64Max", "int64BitAnd", "int64BitOr", "int64BitXor", "int64ShiftLeft", "int64ShiftRight", "int64WrappingAbs", "int64BitNot", "stringLength", "stringUtf8ByteCount", "stringSpan", "stringSpanBack", "listLength", "mapSize", "setSize", "bits32ToInt64", "bytesLength" } },
    .{ "float64", &.{ "float64Literal", "float64Round", "float64Add", "float64Min", "float64Max", "float64Subtract", "float64Multiply", "float64Divide", "float64FloorModulo", "int64ToFloat64", "float64Sqrt", "float64Abs", "float64Negate", "float64Floor", "float64Ceiling", "float64Truncate" } },
    .{ "bits32", &.{ "bits32FromInt64", "bits32Xor", "bits32And", "bits32Or", "bits32Not", "bits32Add", "bits32RotateRight", "bits32ShiftRight", "bits32ShiftLeft" } },
    .{ "bytes", &.{ "bytesEmpty", "bytesFill", "bytesSet", "bytesAppend", "bytesConcat", "bytesSlice", "bytesFromLatin1" } },
    .{ "optionInt64", &.{ "bytesGet", "stringParseInt64", "stringParseInt64Hex", "stringIndexOf", "stringLastIndexOf", "stringLastIndexOfBefore", "stringToCodePoint", "int64CheckedAdd", "int64CheckedSubtract", "int64CheckedMultiply", "float64RoundToInt64", "float64FloorToInt64", "listFindIndex", "listIndexOf", "listLastIndexOf" } },
    .{ "optionFloat64", &.{"stringParseFloat64"} },
    .{ "optionString", &.{"stringFromCodePoint"} },
    .{ "listString", &.{ "stringCodePoints", "stringSplitLines", "stringSplit" } },
};

fn primitiveType(comptime name: []const u8) TypeRef {
    return .{ .kind = .primitive, .name = L(name) };
}

fn constantType(kind: []const u8) ?TypeRef {
    for (constant_kinds) |group| {
        for (group[1]) |listed| if (std.mem.eql(u8, listed, kind)) {
            const t = group[0];
            if (std.mem.eql(u8, t, "listInt64")) return builtin("list", &.{primitiveType("int64")});
            if (std.mem.eql(u8, t, "listString")) return builtin("list", &.{primitiveType("string")});
            if (std.mem.eql(u8, t, "optionInt64")) return builtin("option", &.{primitiveType("int64")});
            if (std.mem.eql(u8, t, "optionFloat64")) return builtin("option", &.{primitiveType("float64")});
            if (std.mem.eql(u8, t, "optionString")) return builtin("option", &.{primitiveType("string")});
            return .{ .kind = .primitive, .name = asciiUnits(t) };
        };
    }
    return null;
}

/// Each model kind is typed exactly once: constant, typed from operands, or untyped.
fn checkTypingTable() void {
    var it = kinds.iterator();
    while (it.next()) |entry| {
        const kind = entry.key_ptr.*;
        var count: usize = @intFromBool(std.meta.stringToEnum(Typed, kind) != null);
        for (untyped_kinds) |listed| count += @intFromBool(std.mem.eql(u8, listed, kind));
        for (constant_kinds) |group| for (group[1]) |listed| {
            count += @intFromBool(std.mem.eql(u8, listed, kind));
        };
        if (count != 1) std.debug.panic("node kind '{s}' is typed {d} times", .{ kind, count });
    }
    inline for (@typeInfo(Typed).@"enum".field_names) |name| if (!kinds.contains(name)) @panic("unknown typed kind " ++ name);
}

// ---------------------------------------------------------------- typed graph rules (slice 3c)

/// `DiagnosticText.typeRef`: display names in wire notation; an unknown declaration keeps its ID.
/// (F# throws on `Exception` with type arguments; the comparison skips such inputs.)
fn typeText(t: TypeRef) Str {
    return switch (t.kind) {
        .primitive => caseName(t),
        .named => blk: {
            const name = if (firstType(t.name)) |type_entity| str(type_entity, "displayName") else cat(&.{ L("type '"), t.name, L("'") });
            break :blk if (t.args.len == 0) name else cat(&.{ name, L("<"), joinTypes(t.args, &typeText), L(">") });
        },
        .capability => blk: {
            for (all_capabilities) |capability| if (same(str(capability, "id"), t.name)) break :blk str(capability, "displayName");
            break :blk cat(&.{ L("capability '"), t.name, L("'") });
        },
        .function => cat(&.{ L("("), joinTypes(t.args, &typeText), L(") -> "), typeText(t.result.?.*) }),
        .type_parameter => typeParameterName(t.name),
        .generic => if (t.args.len == 0 and (t.isGeneric("exception") or t.isGeneric("taskScope"))) caseName(t) else cat(&.{ caseName(t), L("<"), joinTypes(t.args, &typeText), L(">") }),
    };
}

var all_capabilities: []const Value = &.{};

/// The function the per-function loop is in, its resolver and its parameters.
const Scope3c = struct {
    function: Value,
    prefix: Str,
    resolver: *Resolver,
    parameters_by_id: ValueMap,
    all_nodes: []const Value,
    required_effects: Set,
};
var in_function: Scope3c = undefined;

/// The per-function loop's `add`: "In function …: " before the message.
fn addIn(code: []const u8, message_units: Str) void {
    add(code, cat(&.{ in_function.prefix, message_units }));
}

fn nodeType(id: Str) ?TypeRef {
    return in_function.resolver.nodeType(id);
}

fn nodeTypeAt(node: Value, name: []const u8) ?TypeRef {
    return nodeType(str(node, name));
}

/// `requireNodeType`: reports when the node has a known type other than `expected`.
fn requireNodeType(code: []const u8, message_units: Str, expected: TypeRef, id: Str) void {
    if (nodeType(id)) |actual| if (!typeEql(actual, expected)) addIn(code, message_units);
}

/// `compatibleArms`: the merged completion of these graphs is known.
fn compatibleArms(graphs: []const Value) bool {
    var arms: List(?Completion) = .empty;
    for (graphs) |g| arms.append(a, in_function.resolver.graph(g)) catch oom();
    return mergeCompletions(arms.items) != null;
}

fn nodeLabel(comptime what: []const u8, node: Value, comptime rest: []const u8) Str {
    return cat(&.{ L(what ++ " node '"), str(node, "id"), L("' " ++ rest) });
}

/// One `requireNodeType` of `validateGraph` on a fixed operand: the kinds (space-separated), the
/// operand's index in `directValueReferences`, its required type, the code, the name the message
/// starts with (empty: the F# case name) and the rest of the message after "node '<id>' ".
const Requirement = struct { kinds: []const u8, operand: u8, type: []const u8, code: []const u8, name: []const u8 = "", tail: []const u8 };

const string_unary = "stringIsEmpty stringParseInt64 stringParseInt64Hex stringParseFloat64 stringCodePoints stringToLower stringToUpper stringDecodeUtf8Lossy stringEncodeUtf8 stringUtf8ByteCount stringToCodePoint stringLength stringSlice stringContains stringIndexOf stringLastIndexOfBefore stringSpan stringSpanBack stringLastIndexOf stringStartsWith stringEndsWith stringSplit stringSplitLines stringReplace stringTrim stringTrimStart stringTrimEnd";
const int64_binary = "int64WrappingAdd int64WrappingSubtract int64WrappingMultiply int64FloorDivide int64FloorModulo int64Min int64Max int64BitAnd int64BitOr int64BitXor int64ShiftLeft int64ShiftRight int64CheckedAdd int64CheckedSubtract int64CheckedMultiply listRange int64Equal int64LessThan int64LessThanOrEqual int64GreaterThan int64GreaterThanOrEqual";
const float64_binary = "float64Add float64Min float64Max float64Subtract float64Multiply float64Divide float64FloorModulo float64Equal float64LessThan";
const float64_unary = "float64ToString float64RoundToInt64 float64FloorToInt64 float64Sqrt float64Abs float64Negate float64Floor float64Ceiling float64Truncate float64Round";
const string_code = "StringIntrinsicTypeMismatch";
const bytes_code = "BytesOperandTypeMismatch";
const bits_code = "Bits32OperandTypeMismatch";

/// Literals, arithmetic, strings, Bits32 and Bytes (Validation.fs, the scalar node kinds).
const requirements = [_]Requirement{
    .{ .kinds = string_unary, .operand = 0, .type = "string", .code = string_code, .tail = "requires a String value." },
    .{ .kinds = "stringFromCodePoint", .operand = 0, .type = "int64", .code = string_code, .tail = "requires an Int64 value." },
    .{ .kinds = "stringSlice", .operand = 1, .type = "int64", .code = string_code, .tail = "requires an Int64 start." },
    .{ .kinds = "stringSlice stringLastIndexOfBefore stringSpanBack", .operand = 2, .type = "int64", .code = string_code, .tail = "requires an Int64 end." },
    .{ .kinds = "stringRepeat", .operand = 0, .type = "string", .code = string_code, .tail = "requires a String text." },
    .{ .kinds = "stringRepeat", .operand = 1, .type = "int64", .code = string_code, .tail = "requires an Int64 count." },
    .{ .kinds = "stringContains stringIndexOf stringLastIndexOfBefore stringLastIndexOf", .operand = 1, .type = "string", .code = string_code, .tail = "requires a String substring." },
    .{ .kinds = "stringIndexOf stringSpan", .operand = 2, .type = "int64", .code = string_code, .tail = "requires an Int64 from." },
    .{ .kinds = "stringSpan stringSpanBack", .operand = 1, .type = "string", .code = string_code, .tail = "requires a String set." },
    .{ .kinds = "stringStartsWith", .operand = 1, .type = "string", .code = string_code, .tail = "requires a String prefix." },
    .{ .kinds = "stringEndsWith", .operand = 1, .type = "string", .code = string_code, .tail = "requires a String suffix." },
    .{ .kinds = "stringSplitLines", .operand = 1, .type = "string", .code = string_code, .tail = "requires a String breaks." },
    .{ .kinds = "stringSplit", .operand = 1, .type = "string", .code = string_code, .tail = "requires a String separator." },
    .{ .kinds = "stringReplace", .operand = 1, .type = "string", .code = string_code, .tail = "requires a String pattern." },
    .{ .kinds = "stringReplace", .operand = 2, .type = "string", .code = string_code, .tail = "requires a String replacement." },
    .{ .kinds = "stringJoin", .operand = 0, .type = "listString", .code = string_code, .tail = "requires a List<String> list." },
    .{ .kinds = "stringJoin", .operand = 1, .type = "string", .code = string_code, .tail = "requires a String separator." },
    .{ .kinds = "stringEqual", .operand = 0, .type = "string", .code = string_code, .tail = "requires a StringEqual left operand." },
    .{ .kinds = "stringEqual", .operand = 1, .type = "string", .code = string_code, .tail = "requires a StringEqual right operand." },
    .{ .kinds = "stringLessThan", .operand = 0, .type = "string", .code = string_code, .tail = "requires a StringLessThan left operand." },
    .{ .kinds = "stringLessThan", .operand = 1, .type = "string", .code = string_code, .tail = "requires a StringLessThan right operand." },
    .{ .kinds = "stringConcat", .operand = 0, .type = "string", .code = string_code, .tail = "requires a StringConcat left operand." },
    .{ .kinds = "stringConcat", .operand = 1, .type = "string", .code = string_code, .tail = "requires a StringConcat right operand." },
    .{ .kinds = "int64ToString int64ToHexString int64ToFloat64 int64WrappingAbs int64BitNot", .operand = 0, .type = "int64", .code = "Int64IntrinsicTypeMismatch", .tail = "requires an Int64 value." },
    .{ .kinds = int64_binary, .operand = 0, .type = "int64", .code = "Int64OperandTypeMismatch", .name = "Int64", .tail = "requires a Int64 left operand." },
    .{ .kinds = int64_binary, .operand = 1, .type = "int64", .code = "Int64OperandTypeMismatch", .name = "Int64", .tail = "requires a Int64 right operand." },
    .{ .kinds = float64_unary, .operand = 0, .type = "float64", .code = "Float64OperandTypeMismatch", .tail = "requires a Float64 value." },
    .{ .kinds = "float64Round", .operand = 1, .type = "int64", .code = "Float64OperandTypeMismatch", .tail = "requires an Int64 digit count." },
    .{ .kinds = float64_binary, .operand = 0, .type = "float64", .code = "Float64OperandTypeMismatch", .name = "Float64", .tail = "requires a Float64 left operand." },
    .{ .kinds = float64_binary, .operand = 1, .type = "float64", .code = "Float64OperandTypeMismatch", .name = "Float64", .tail = "requires a Float64 right operand." },
    .{ .kinds = "float64IsNaN float64IsFinite float64IsInfinite", .operand = 0, .type = "float64", .code = "Float64OperandTypeMismatch", .name = "Float64", .tail = "requires a Float64 operand." },
    .{ .kinds = "bits32FromInt64", .operand = 0, .type = "int64", .code = bits_code, .tail = "requires an Int64 value." },
    .{ .kinds = "bits32ToInt64 bits32Not", .operand = 0, .type = "bits32", .code = bits_code, .name = "Bits32", .tail = "requires a Bits32 operand." },
    .{ .kinds = "bits32Xor bits32And bits32Or bits32Add", .operand = 0, .type = "bits32", .code = bits_code, .name = "Bits32", .tail = "requires a Bits32 left operand." },
    .{ .kinds = "bits32Xor bits32And bits32Or bits32Add", .operand = 1, .type = "bits32", .code = bits_code, .name = "Bits32", .tail = "requires a Bits32 right operand." },
    .{ .kinds = "bits32RotateRight bits32ShiftRight bits32ShiftLeft", .operand = 0, .type = "bits32", .code = bits_code, .name = "Bits32", .tail = "requires a Bits32 value." },
    .{ .kinds = "bits32RotateRight bits32ShiftRight bits32ShiftLeft", .operand = 1, .type = "int64", .code = bits_code, .name = "Bits32", .tail = "requires an Int64 amount." },
    .{ .kinds = "bytesFill", .operand = 0, .type = "int64", .code = bytes_code, .tail = "requires an Int64 length." },
    .{ .kinds = "bytesFill", .operand = 1, .type = "int64", .code = bytes_code, .tail = "requires an Int64 value." },
    .{ .kinds = "bytesLength bytesToLatin1", .operand = 0, .type = "bytes", .code = bytes_code, .name = "Bytes", .tail = "requires a Bytes operand." },
    .{ .kinds = "bytesFromLatin1", .operand = 0, .type = "string", .code = bytes_code, .tail = "requires a String value." },
    .{ .kinds = "bytesConcat", .operand = 0, .type = "bytes", .code = bytes_code, .name = "Bytes", .tail = "requires a Bytes left operand." },
    .{ .kinds = "bytesConcat", .operand = 1, .type = "bytes", .code = bytes_code, .name = "Bytes", .tail = "requires a Bytes right operand." },
    .{ .kinds = "bytesGet bytesAppend bytesSet bytesSlice", .operand = 0, .type = "bytes", .code = bytes_code, .name = "Bytes", .tail = "requires a Bytes value." },
    .{ .kinds = "bytesGet bytesSet", .operand = 1, .type = "int64", .code = bytes_code, .name = "Bytes", .tail = "requires an Int64 index." },
    .{ .kinds = "bytesAppend", .operand = 1, .type = "int64", .code = bytes_code, .name = "Bytes", .tail = "requires an Int64 value." },
    .{ .kinds = "bytesSet", .operand = 2, .type = "int64", .code = bytes_code, .name = "Bytes", .tail = "requires an Int64 value." },
    .{ .kinds = "bytesSlice", .operand = 1, .type = "int64", .code = bytes_code, .name = "Bytes", .tail = "requires an Int64 start." },
    .{ .kinds = "bytesSlice", .operand = 2, .type = "int64", .code = bytes_code, .name = "Bytes", .tail = "requires an Int64 end." },
};

/// A type of the requirement table.
fn requiredType(spec: []const u8) TypeRef {
    if (std.mem.eql(u8, spec, "listString")) return builtin("list", &.{primitiveType("string")});
    return .{ .kind = .primitive, .name = asciiUnits(spec) };
}

/// The F# case name of a node kind: its wire name with an upper-case first letter.
fn caseNameOf(kind: Str) Str {
    const out = a.dupe(u16, kind) catch oom();
    if (out.len > 0 and out[0] >= 'a' and out[0] <= 'z') out[0] -= 'a' - 'A';
    return out;
}

/// The table's requirements on one node, then `ValueEqual`.
fn scalarRules(node: Value) void {
    const kind = text(str(node, "kind"));
    var references: List(Str) = .empty;
    directValueReferences(node, &references);
    for (requirements) |r| {
        var kinds_listed = std.mem.tokenizeScalar(u8, r.kinds, ' ');
        while (kinds_listed.next()) |listed| {
            if (!std.mem.eql(u8, listed, kind)) continue;
            const name = if (r.name.len == 0) caseNameOf(str(node, "kind")) else asciiUnits(r.name);
            requireNodeType(r.code, cat(&.{ name, L(" node '"), str(node, "id"), L("' "), asciiUnits(r.tail) }), requiredType(r.type), references.items[r.operand]);
        }
    }
    if (std.mem.eql(u8, kind, "valueEqual")) {
        const left = nodeType(references.items[0]);
        const right = nodeType(references.items[1]);
        const id = str(node, "id");
        if (left != null and right != null and !typeEql(left.?, right.?)) {
            addIn("ValueEqualOperandTypeMismatch", cat(&.{ L("ValueEqual node '"), id, L("' requires operands of one type, but its left operand is "), describeType(left.?), L(" and its right operand is "), describeType(right.?), L(".") }));
        } else if (left orelse right) |operand_type| {
            if (!isEquatable(operand_type))
                addIn("ValueEqualTypeNotEquatable", cat(&.{ L("ValueEqual node '"), id, L("' compares values of a type that is not equatable: "), explainEquatable(operand_type) }));
        }
    }
}

/// A node's type seen as one builtin generic of a fixed arity.
const TypeShape = union(enum) { unknown, other, args: []const TypeRef };

fn shapeOf(id: Str, comptime name: []const u8, arity: usize) TypeShape {
    const t = nodeType(id) orelse return .unknown;
    return if (t.isGeneric(name) and t.args.len == arity) .{ .args = t.args } else .other;
}

fn operandsOf(node: Value) []const Str {
    var references: List(Str) = .empty;
    directValueReferences(node, &references);
    return references.items;
}

fn nodeMessage(name: Str, node: Value, comptime tail: []const u8) Str {
    return cat(&.{ name, L(" node '"), str(node, "id"), L("' " ++ tail) });
}

/// `requireMapInput`: the key and value types of a Map input.
fn requireMapInput(node: Value, id: Str) ?[]const TypeRef {
    return switch (shapeOf(id, "map", 2)) {
        .args => |args| args,
        .other => blk: {
            addIn("MapInputTypeMismatch", cat(&.{ L("Node '"), str(node, "id"), L("' requires a Map<K, V> input.") }));
            break :blk null;
        },
        .unknown => null,
    };
}

/// `requireSetInput`: the item type of a Set input.
fn requireSetInput(node: Value, id: Str) ?TypeRef {
    return switch (shapeOf(id, "set", 1)) {
        .args => |args| args[0],
        .other => blk: {
            addIn("SetInputTypeMismatch", cat(&.{ L("Node '"), str(node, "id"), L("' requires a Set<T> input.") }));
            break :blk null;
        },
        .unknown => null,
    };
}

fn requireMapKey(node: Value, key_type: TypeRef, id: Str) void {
    requireNodeType("MapKeyTypeMismatch", cat(&.{ L("Node '"), str(node, "id"), L("' requires a key of the map's key type.") }), key_type, id);
}

fn kindIs(kind: []const u8, comptime names: []const u8) bool {
    var it = std.mem.tokenizeScalar(u8, names, ' ');
    while (it.next()) |name| if (std.mem.eql(u8, name, kind)) return true;
    return false;
}

/// Lists, Sets, Maps, Option and Result without callbacks (Validation.fs `ListConstruct` … `ListConcat`).
fn collectionRules(node: Value) void {
    const kind = text(str(node, "kind"));
    const name = caseNameOf(str(node, "kind"));
    const ops = operandsOf(node);
    const int64 = primitiveType("int64");
    if (std.mem.eql(u8, kind, "listConstruct")) {
        const item_type = typeRefOf(get(node, "itemType"));
        for (ops) |item| requireNodeType("ListConstructItemTypeMismatch", nodeMessage(name, node, "contains an item whose type does not match its declared item type."), item_type, item);
    } else if (kindIs(kind, "listLength listReverse listLast listDropLast")) {
        if (shapeOf(ops[0], "list", 1) == .other) addIn(fmt("{s}InputTypeMismatch", .{text(name)}), nodeMessage(name, node, "requires List<T> input."));
    } else if (kindIs(kind, "mapSize mapKeys mapValues")) {
        _ = requireMapInput(node, ops[0]);
    } else if (kindIs(kind, "mapGet mapContainsKey mapRemove")) {
        if (requireMapInput(node, ops[0])) |args| requireMapKey(node, args[0], ops[1]);
    } else if (kindIs(kind, "setSize setToList")) {
        _ = requireSetInput(node, ops[0]);
    } else if (kindIs(kind, "listIndexOf listLastIndexOf listContains")) {
        switch (shapeOf(ops[0], "list", 1)) {
            .args => |args| {
                if (!isEquatable(args[0]))
                    addIn(fmt("{s}ItemTypeNotEquatable", .{text(name)}), cat(&.{ nodeMessage(name, node, "compares items of a type that is not equatable: "), explainEquatable(args[0]) }));
                requireNodeType(fmt("{s}ItemTypeMismatch", .{text(name)}), nodeMessage(name, node, "requires an item of the list's item type."), args[0], ops[1]);
            },
            .other => addIn(fmt("{s}ListTypeMismatch", .{text(name)}), nodeMessage(name, node, "requires List<T> as its list input.")),
            .unknown => {},
        }
    } else if (kindIs(kind, "setContains setInsert setRemove")) {
        if (requireSetInput(node, ops[0])) |item| requireNodeType("SetItemTypeMismatch", cat(&.{ L("Node '"), str(node, "id"), L("' requires an item of the set's item type.") }), item, ops[1]);
    } else if (kindIs(kind, "setUnion setIntersection setDifference setIsSubset")) {
        if (requireSetInput(node, ops[0])) |item| requireNodeType("SetInputTypeMismatch", cat(&.{ L("Node '"), str(node, "id"), L("' requires two sets of the same item type.") }), builtin("set", &.{item}), ops[1]);
    } else if (std.mem.eql(u8, kind, "setFromList")) {
        switch (shapeOf(ops[0], "list", 1)) {
            .args => |args| if (!isEquatable(args[0]))
                addIn("SetItemTypeNotEquatable", cat(&.{ nodeMessage(name, node, "makes a Set of a type that is not equatable: "), explainEquatable(args[0]) })),
            .other => addIn("HigherOrderSourceTypeMismatch", nodeMessage(name, node, "requires List<T> input.")),
            .unknown => {},
        }
    } else if (std.mem.eql(u8, kind, "mapInsert") or std.mem.eql(u8, kind, "mapGetOrDefault")) {
        if (requireMapInput(node, ops[0])) |args| {
            requireMapKey(node, args[0], ops[1]);
            requireNodeType("MapValueTypeMismatch", if (std.mem.eql(u8, kind, "mapInsert")) nodeMessage(name, node, "requires a value of the map's value type.") else nodeMessage(name, node, "requires a default of the map's value type."), args[1], ops[2]);
        }
    } else if (std.mem.eql(u8, kind, "mapUnion")) {
        if (requireMapInput(node, ops[0])) |args| requireNodeType("MapInputTypeMismatch", nodeMessage(name, node, "requires two maps of the same key and value types."), builtin("map", args), ops[1]);
    } else if (std.mem.eql(u8, kind, "optionGetOrDefault")) {
        switch (shapeOf(ops[0], "option", 1)) {
            .args => |args| requireNodeType("OptionDefaultTypeMismatch", nodeMessage(name, node, "requires a default of the option's item type."), args[0], ops[1]),
            .other => addIn("OptionInputTypeMismatch", nodeMessage(name, node, "requires an Option<T> input.")),
            .unknown => {},
        }
    } else if (std.mem.eql(u8, kind, "resultGetOrDefault")) {
        switch (shapeOf(ops[0], "result", 2)) {
            .args => |args| requireNodeType("ResultDefaultTypeMismatch", nodeMessage(name, node, "requires a default of the result's ok type."), args[0], ops[1]),
            .other => addIn("ResultInputTypeMismatch", nodeMessage(name, node, "requires a Result<T, E> input.")),
            .unknown => {},
        }
    } else if (kindIs(kind, "optionOkOr optionIsSome")) {
        if (shapeOf(ops[0], "option", 1) == .other) addIn("OptionInputTypeMismatch", cat(&.{ L("Node '"), str(node, "id"), L("' requires an Option<T> input.") }));
    } else if (kindIs(kind, "resultOkValue resultErrValue resultIsOk")) {
        if (shapeOf(ops[0], "result", 2) == .other) addIn("ResultInputTypeMismatch", cat(&.{ L("Node '"), str(node, "id"), L("' requires a Result<T, E> input.") }));
    } else if (std.mem.eql(u8, kind, "listItemAt")) {
        if (shapeOf(ops[0], "list", 1) == .other) addIn("ListItemAtListTypeMismatch", nodeMessage(name, node, "requires List<T> as its list input."));
        requireNodeType("ListItemAtIndexTypeMismatch", nodeMessage(name, node, "requires an Int64 index."), int64, ops[1]);
    } else if (kindIs(kind, "listSetAt listInsertAt")) {
        switch (shapeOf(ops[0], "list", 1)) {
            .args => |args| requireNodeType(fmt("{s}ItemTypeMismatch", .{text(name)}), nodeMessage(name, node, "requires an item of the list's item type."), args[0], ops[2]),
            .other => addIn(fmt("{s}ListTypeMismatch", .{text(name)}), nodeMessage(name, node, "requires List<T> as its list input.")),
            .unknown => {},
        }
        requireNodeType(fmt("{s}IndexTypeMismatch", .{text(name)}), nodeMessage(name, node, "requires an Int64 index."), int64, ops[1]);
    } else if (std.mem.eql(u8, kind, "listRemoveAt")) {
        if (shapeOf(ops[0], "list", 1) == .other) addIn("ListRemoveAtListTypeMismatch", nodeMessage(name, node, "requires List<T> as its list input."));
        requireNodeType("ListRemoveAtIndexTypeMismatch", nodeMessage(name, node, "requires an Int64 index."), int64, ops[1]);
    } else if (std.mem.eql(u8, kind, "listSlice")) {
        if (shapeOf(ops[0], "list", 1) == .other) addIn("ListSliceListTypeMismatch", nodeMessage(name, node, "requires List<T> as its list input."));
        requireNodeType("ListSliceBoundTypeMismatch", nodeMessage(name, node, "requires an Int64 start."), int64, ops[1]);
        requireNodeType("ListSliceBoundTypeMismatch", nodeMessage(name, node, "requires an Int64 end."), int64, ops[2]);
    } else if (std.mem.eql(u8, kind, "listAppendItem")) {
        switch (shapeOf(ops[0], "list", 1)) {
            .args => |args| if (nodeType(ops[1])) |actual| if (!typeEql(actual, args[0]))
                addIn("ListAppendItemTypeMismatch", nodeMessage(name, node, "item does not match the list item type.")),
            .other => addIn("ListAppendListTypeMismatch", nodeMessage(name, node, "requires List<T> as its list input.")),
            .unknown => {},
        }
    } else if (std.mem.eql(u8, kind, "listConcat")) {
        const left = shapeOf(ops[0], "list", 1);
        const right = shapeOf(ops[1], "list", 1);
        if (left == .unknown or right == .unknown) return;
        if (left == .args and right == .args) {
            if (!typeEql(left.args[0], right.args[0])) addIn("ListConcatItemTypeMismatch", nodeMessage(name, node, "requires both lists to have the same item type."));
            return;
        }
        if (left == .other) addIn("ListConcatLeftTypeMismatch", nodeMessage(name, node, "requires List<T> as its left input."));
        if (right == .other) addIn("ListConcatRightTypeMismatch", nodeMessage(name, node, "requires List<T> as its right input."));
    }
}

/// A union match arm's variant IDs: `variantId` alone, or `variantIds`.
fn armVariantIds(arm: Value) []const Str {
    if (tryProperty("variantId", arm)) |single| return a.dupe(Str, &.{single.string}) catch oom();
    const items = arrayOf(arm, "variantIds");
    const ids = a.alloc(Str, items.len) catch oom();
    for (items, ids) |item, *id| id.* = item.string;
    return ids;
}

var variants_by_id: ValueMap = .empty;

/// A declared field with its type seen through the owner's type arguments (`instantiateFields`).
const Field = struct { id: Str, display: Str, type: TypeRef };

fn instantiateFields(owner: ?Value, arguments: []const TypeRef, fields: []const Value) []const Field {
    const out = a.alloc(Field, fields.len) catch oom();
    for (fields, out) |field, *f| {
        const declared = typeRefOf(get(field, "type"));
        f.* = .{ .id = str(field, "id"), .display = str(field, "displayName"), .type = if (owner) |o| instantiate(o, arguments, declared) else declared };
    }
    return out;
}

fn findField(fields: []const Field, id: Str) ?Field {
    for (fields) |f| if (same(f.id, id)) return f;
    return null;
}

fn duplicateTargets(context: Str, bindings: []const Value, comptime target: []const u8) void {
    const ids = a.alloc(Str, bindings.len) catch oom();
    for (bindings, ids) |binding, *id| id.* = str(binding, target);
    for (countBy(ids).values()) |entry| {
        if (entry.count > 1) addIn("DuplicateFieldBinding", cat(&.{ context, L(" binds field '"), entry.id, L("' more than once.") }));
    }
}

fn fieldValueType(context: Str, field: Field, binding: Value) void {
    if (nodeType(str(binding, "valueNodeId"))) |value_type| if (!typeEql(field.type, value_type))
        addIn("FieldValueTypeMismatch", cat(&.{ context, L(" binds a value with the wrong type to field "), describe(field.display, field.id), L(".") }));
}

/// `validateFieldBindings`.
fn validateFieldBindings(context: Str, expected: []const Field, bindings: []const Value) void {
    duplicateTargets(context, bindings, "targetFieldId");
    var expected_ids: Set = .empty;
    for (expected) |f| expected_ids.put(a, idKey(f.id), {}) catch oom();
    var actual_ids: Set = .empty;
    for (bindings) |binding| actual_ids.put(a, idKey(str(binding, "targetFieldId")), {}) catch oom();
    if (!sameSet(&expected_ids, &actual_ids)) addIn("FieldBindingMismatch", cat(&.{ context, L(" must bind exactly the fields declared by its target.") }));
    for (bindings) |binding| {
        if (findField(expected, str(binding, "targetFieldId"))) |field| fieldValueType(context, field, binding);
    }
}

fn sameSet(left: *const Set, right: *const Set) bool {
    if (left.count() != right.count()) return false;
    for (left.keys()) |key| if (!right.contains(key)) return false;
    return true;
}

fn bindingType(binding: Value) TypeRef {
    return typeRefOf(get(binding, "type"));
}

/// Products, unions, abort and the match nodes (Validation.fs `ProductConstruct` … `MatchResult`).
fn structureRules(node: Value, graph_result: Str) void {
    const kind = text(str(node, "kind"));
    const id = str(node, "id");
    const ops = operandsOf(node);
    const function = in_function.function;
    const function_label = describe(str(function, "displayName"), str(function, "id"));
    if (std.mem.eql(u8, kind, "productConstruct")) {
        const type_id = str(node, "typeId");
        const context = cat(&.{ L("Product node '"), id, L("'") });
        if (types_by_id.get(idKey(type_id))) |type_entity| {
            if (isName(str(type_entity, "kind"), "product"))
                validateFieldBindings(context, instantiateFields(type_entity, typeArgumentsOf(node), arrayOf(type_entity, "fields")), arrayOf(node, "fields"))
            else
                addIn("InvalidProductConstructTarget", cat(&.{ context, L(" targets a non-product type.") }));
        } else addIn("UnknownProductConstructType", cat(&.{ context, L(" targets unknown type '"), type_id, L("'.") }));
    } else if (std.mem.eql(u8, kind, "fieldAccess")) {
        const owner = product_field_owners.get(idKey(str(node, "fieldId"))) orelse
            return addIn("UnknownProductField", cat(&.{ L("Field-access node '"), id, L("' references a field that is not owned by a product type.") }));
        if (nodeType(ops[0])) |source| if (source.kind != .named or !same(source.name, str(owner, "id")))
            addIn("FieldAccessTypeMismatch", cat(&.{ L("Field-access node '"), id, L("' reads a field from the wrong source type.") }));
    } else if (std.mem.eql(u8, kind, "productUpdate")) {
        const type_id = str(node, "typeId");
        const context = cat(&.{ L("Product-update node '"), id, L("'") });
        const type_entity = types_by_id.get(idKey(type_id)) orelse
            return addIn("UnknownProductUpdateType", cat(&.{ context, L(" targets unknown type '"), type_id, L("'.") }));
        if (!isName(str(type_entity, "kind"), "product")) return addIn("InvalidProductUpdateTarget", cat(&.{ context, L(" targets a non-product type.") }));
        const declared = arrayOf(type_entity, "fields");
        // The bound values must match the fields at the source's type arguments.
        const expected = if (nodeType(ops[0])) |source| blk: {
            if (source.kind == .named and same(source.name, type_id)) break :blk instantiateFields(type_entity, source.args, declared);
            addIn("ProductUpdateSourceTypeMismatch", cat(&.{ context, L(" updates a source that is not of its product type.") }));
            break :blk instantiateFields(null, &.{}, declared);
        } else instantiateFields(null, &.{}, declared);
        const fields = arrayOf(node, "fields");
        if (fields.len == 0) addIn("EmptyProductUpdate", cat(&.{ context, L(" must replace at least one field.") }));
        duplicateTargets(context, fields, "targetFieldId");
        for (fields) |binding| {
            const field = findField(expected, str(binding, "targetFieldId")) orelse {
                addIn("FieldBindingMismatch", cat(&.{ context, L(" binds field '"), str(binding, "targetFieldId"), L("', which its product type does not declare.") }));
                continue;
            };
            fieldValueType(context, field, binding);
        }
    } else if (std.mem.eql(u8, kind, "unionConstruct")) {
        const variant_id = str(node, "variantId");
        const context = cat(&.{ L("Union node '"), id, L("'") });
        const variant = variants_by_id.get(idKey(variant_id)) orelse
            return addIn("UnknownUnionVariant", cat(&.{ context, L(" references unknown variant '"), variant_id, L("'.") }));
        const owner = types_by_id.get(idKey(variant_owners.get(idKey(variant_id)).?));
        validateFieldBindings(context, instantiateFields(owner, typeArgumentsOf(node), arrayOf(variant, "fields")), arrayOf(node, "fields"));
    } else if (std.mem.eql(u8, kind, "abort")) {
        if (!same(id, graph_result))
            addIn("AbortNotGraphResult", cat(&.{ L("Abort node '"), id, L("' in function "), function_label, L(" must be its graph result.") }));
        const result_type = typeRefOf(get(function, "resultType"));
        if (result_type.isGeneric("result") and result_type.args.len == 2) {
            requireNodeType("AbortPayloadTypeMismatch", cat(&.{ L("Abort node '"), id, L("' payload '"), ops[0], L("' must match error type "), typeText(result_type.args[1]), L(" of function "), function_label, L(".") }), result_type.args[1], ops[0]);
        } else addIn("AbortOwnerResultTypeMismatch", cat(&.{ L("Abort node '"), id, L("' requires function "), function_label, L(" to return Result<T, E>.") }));
    } else if (std.mem.eql(u8, kind, "matchUnion")) {
        matchUnionRules(node, ops[0]);
    } else if (std.mem.eql(u8, kind, "bind")) {
        const binding = get(get(node, "boundBody"), "valueBinding");
        requireNodeType("PatternBindingTypeMismatch", cat(&.{ L("Bind binding '"), str(binding, "id"), L("' does not match its input type.") }), bindingType(binding), ops[0]);
    } else if (std.mem.eql(u8, kind, "matchOption")) {
        const some = get(node, "someArm");
        switch (shapeOf(ops[0], "option", 1)) {
            .args => |args| if (!typeEql(bindingType(get(some, "valueBinding")), args[0]))
                addIn("PatternBindingTypeMismatch", cat(&.{ L("Option match binding '"), str(get(some, "valueBinding"), "id"), L("' does not match the Option item type.") })),
            .other => addIn("MatchOptionInputTypeMismatch", cat(&.{ L("Option match node '"), id, L("' requires Option<T> input.") })),
            .unknown => {},
        }
        if (!compatibleArms(&.{ get(some, "body"), get(node, "noneBody") }))
            addIn("MatchArmTypeMismatch", cat(&.{ L("Option match node '"), id, L("' arms do not produce one common type.") }));
    } else if (std.mem.eql(u8, kind, "matchResult")) {
        const ok_arm = get(node, "okArm");
        const err_arm = get(node, "errArm");
        switch (shapeOf(ops[0], "result", 2)) {
            .args => |args| {
                if (!typeEql(bindingType(get(ok_arm, "valueBinding")), args[0]))
                    addIn("PatternBindingTypeMismatch", cat(&.{ L("Result.Ok binding '"), str(get(ok_arm, "valueBinding"), "id"), L("' does not match the Result ok type.") }));
                if (!typeEql(bindingType(get(err_arm, "errorBinding")), args[1]))
                    addIn("PatternBindingTypeMismatch", cat(&.{ L("Result.Err binding '"), str(get(err_arm, "errorBinding"), "id"), L("' does not match the Result error type.") }));
            },
            .other => addIn("MatchResultInputTypeMismatch", cat(&.{ L("Result match node '"), id, L("' requires Result<T,E> input.") })),
            .unknown => {},
        }
        if (!compatibleArms(&.{ get(ok_arm, "body"), get(err_arm, "body") }))
            addIn("MatchArmTypeMismatch", cat(&.{ L("Result match node '"), id, L("' arms do not produce one common type.") }));
    }
}

fn matchUnionRules(node: Value, input: Str) void {
    const id = str(node, "id");
    const arms = arrayOf(node, "arms");
    if (nodeType(input)) |input_type| {
        const input_entity = if (input_type.kind == .named) types_by_id.get(idKey(input_type.name)) else null;
        if (input_entity != null and isName(str(input_entity.?, "kind"), "union")) {
            const declared = arrayOf(input_entity.?, "variants");
            // `EmptyUnionMatchArm` cannot occur: the reader requires a variant per arm.
            var all_ids: List(Str) = .empty;
            for (arms) |arm| all_ids.appendSlice(a, armVariantIds(arm)) catch oom();
            for (countBy(all_ids.items).values()) |entry| {
                if (entry.count > 1) addIn("DuplicateUnionMatchArm", cat(&.{ L("Union match node '"), id, L("' has duplicate arm for variant '"), entry.id, L("'.") }));
            }
            const expected = idSet(declared);
            var actual: Set = .empty;
            for (all_ids.items) |v| actual.put(a, idKey(v), {}) catch oom();
            if (!sameSet(&expected, &actual)) addIn("NonExhaustiveUnionMatch", cat(&.{ L("Union match node '"), id, L("' must contain exactly one arm for every variant.") }));
            for (arms) |arm| {
                const ids = armVariantIds(arm);
                const bindings = arrayOf(arm, "fieldBindings");
                if (ids.len >= 2 and bindings.len > 0)
                    addIn("PatternFieldBindingMismatch", cat(&.{ L("Union match arm '"), describeArm(arm), L("' names several variants and must bind no fields.") }));
                if (ids.len != 1) continue;
                const variant = for (declared) |v| {
                    if (same(str(v, "id"), ids[0])) break v;
                } else continue;
                const fields = instantiateFields(input_entity.?, input_type.args, arrayOf(variant, "fields"));
                var expected_fields: Set = .empty;
                for (fields) |f| expected_fields.put(a, idKey(f.id), {}) catch oom();
                var actual_fields: Set = .empty;
                for (bindings) |b| actual_fields.put(a, idKey(str(b, "sourceFieldId")), {}) catch oom();
                if (!sameSet(&expected_fields, &actual_fields) or bindings.len != actual_fields.count())
                    addIn("PatternFieldBindingMismatch", cat(&.{ L("Union match arm '"), describeArm(arm), L("' must bind each variant field exactly once.") }));
                for (bindings) |b| {
                    const field = findField(fields, str(b, "sourceFieldId")) orelse continue;
                    if (!typeEql(field.type, bindingType(get(b, "binding"))))
                        addIn("PatternBindingTypeMismatch", cat(&.{ L("Pattern binding '"), str(get(b, "binding"), "id"), L("' does not match its variant field type.") }));
                }
            }
        } else addIn("MatchUnionInputTypeMismatch", cat(&.{ L("Union match node '"), id, L("' requires a declared union input.") }));
    }
    var bodies: List(Value) = .empty;
    for (arms) |arm| bodies.append(a, get(arm, "body")) catch oom();
    if (!compatibleArms(bodies.items)) addIn("MatchArmTypeMismatch", cat(&.{ L("Union match node '"), id, L("' arms do not produce one common type.") }));
}

/// `TypeParameters.matchType` without changing `substitution`.
fn matchOne(substitution: *const Substitution, declared: TypeRef, actual: TypeRef) ?Substitution {
    return matchFrom(substitution, &.{.{ .declared = declared, .actual = actual }});
}

fn resultOf(callback: Value, substitution: *const Substitution) TypeRef {
    return substitute(substitution, typeRefOf(get(callback, "resultType")));
}

const CallbackContext = struct { unbound: []const Value, substitution: Substitution };

/// `validateCallbackContext`: the context's own rules; the parameters it leaves unbound and the
/// substitution its values determine (empty when they conflict).
fn validateCallbackContext(node: Value, callback: Value, context: []const Value) CallbackContext {
    const id = str(node, "id");
    const targets = a.alloc(Str, context.len) catch oom();
    for (context, targets) |binding, *t| t.* = str(binding, "targetParameterId");
    for (countBy(targets).values()) |entry| {
        if (entry.count > 1) addIn("DuplicateArgumentTarget", cat(&.{ L("Higher-order node '"), id, L("' binds callback parameter '"), entry.id, L("' more than once.") }));
    }
    for (targets) |target| {
        if (parameterOf(callback, target) == null)
            addIn("UnknownCallbackContextParameter", cat(&.{ L("Higher-order node '"), id, L("' binds '"), target, L("', which is not a parameter of callback '"), str(callback, "id"), L("'.") }));
    }
    var pairs: List(Pair) = .empty;
    for (context) |binding| {
        const parameter = parameterOf(callback, str(binding, "targetParameterId"));
        const actual = nodeType(str(binding, "valueNodeId"));
        if (parameter != null and actual != null) pairs.append(a, .{ .declared = typeRefOf(get(parameter.?, "type")), .actual = actual.? }) catch oom();
    }
    const substitution = matchAll(pairs.items) orelse blk: {
        addIn("CallbackContextTypeMismatch", cat(&.{ L("Higher-order node '"), id, L("' binds a callback parameter to a value of another type.") }));
        break :blk Substitution.empty;
    };
    var unbound: List(Value) = .empty;
    for (arrayOf(callback, "parameters")) |parameter| {
        if (!bindsParameter(context, str(parameter, "id"))) unbound.append(a, parameter) catch oom();
    }
    return .{ .unbound = unbound.items, .substitution = substitution };
}

fn parameterType(parameter: Value) TypeRef {
    return typeRefOf(get(parameter, "type"));
}

/// The kinds `SemanticNodeShape.isIntrinsicallyEffectful` names.
const effectful_kinds = "parallelAll cancellationRequested useResource exceptionCatch exceptionRethrow cancellationShield taskScope taskStart taskWait oneShotCreate oneShotComplete oneShotWait taskCancel capabilityInvoke";

/// `PureFunctions.isPure`: the function and every function its nodes name, transitively, are
/// known, require no effect and hold no intrinsically effectful node.
fn isPure(function_id: Str) bool {
    var seen: Set = .empty;
    var pending: List(Str) = .empty;
    pending.append(a, function_id) catch oom();
    while (pending.pop()) |id| {
        if ((seen.getOrPut(a, idKey(id)) catch oom()).found_existing) continue;
        const function = functions_by_id.get(idKey(id)) orelse return false;
        if (arrayOf(function, "requiredEffects").len > 0) return false;
        var nodes: List(Value) = .empty;
        collectNodes(get(function, "body"), &nodes);
        for (nodes.items) |node| {
            if (kindIs(text(str(node, "kind")), effectful_kinds)) return false;
            for (node.object) |member| {
                const name = member.name;
                if (name.len > 10 and isName(name[name.len - 10 ..], "FunctionId")) pending.append(a, member.value.string) catch oom();
            }
            for (arrayOf(node, "implementations")) |implementation| pending.append(a, str(implementation, "functionId")) catch oom();
        }
    }
    return true;
}

/// `callbackEffectsHint`.
fn effectsHint(comptime collection: []const u8, callback: Value) Str {
    var out: List(u16) = .empty;
    const effects = arrayOf(callback, "requiredEffects");
    if (effects.len > 0) {
        out.appendSlice(a, L(" The callback requires effect ")) catch oom();
        for (effects, 0..) |effect, i| {
            if (i > 0) out.appendSlice(a, L(", ")) catch oom();
            out.appendSlice(a, effectLabel(effect.string)) catch oom();
        }
        out.appendSlice(a, L(".")) catch oom();
    }
    out.appendSlice(a, if (comptime std.mem.eql(u8, collection, "List"))
        L(" To run effects once per item, in order, use ListEffectFold instead (its folder takes the accumulator and the item).")
    else
        L(" Do the effectful work outside the callback and pass only pure values into it.")) catch oom();
    return out.items;
}

/// `MissingEffectRequirement` for each effect the callback requires and the function does not declare.
fn missingEffects(callback: Value, before: Str, after: Str) void {
    for (arrayOf(callback, "requiredEffects")) |effect| {
        if (!in_function.required_effects.contains(idKey(effect.string)))
            addIn("MissingEffectRequirement", cat(&.{ before, effectLabel(effect.string), after }));
    }
}

fn functionLabel(function: Value) Str {
    return describe(str(function, "displayName"), str(function, "id"));
}

/// `validateCallbackOver`: a pure callback taking each List or Set item.
fn validateCallbackOver(comptime collection: []const u8, node: Value, source: Str, callback_id: Str, context: []const Value, predicate: bool) void {
    const id = str(node, "id");
    const head = cat(&.{ L("Higher-order " ++ collection ++ " node '"), id, L("'") });
    const item: ?TypeRef = if (nodeType(source)) |t| blk: {
        if (t.kind == .generic and t.args.len == 1 and isName(t.name, if (comptime std.mem.eql(u8, collection, "List")) "list" else "set")) break :blk t.args[0];
        addIn("HigherOrderSourceTypeMismatch", cat(&.{ head, L(" requires " ++ collection ++ "<T> input.") }));
        break :blk null;
    } else null;
    const callback = functions_by_id.get(idKey(callback_id)) orelse
        return addIn("UnknownCallbackFunction", cat(&.{ head, L(" references unknown callback function '"), callback_id, L("'.") }));
    var checked = validateCallbackContext(node, callback, context);
    if (checked.unbound.len != 1) {
        addIn("CallbackArityMismatch", cat(&.{ head, L(" requires callback '"), str(callback, "id"), L("' to have exactly one parameter not bound by context.") }));
    } else if (item) |item_type| {
        if (matchOne(&checked.substitution, parameterType(checked.unbound[0]), item_type)) |extended| {
            checked.substitution = extended;
        } else addIn("CallbackParameterTypeMismatch", cat(&.{ head, L(" callback parameter type does not match the source "), asciiUnits(comptime lowerAscii(collection)), L(" item type.") }));
    }
    if (!isPure(str(callback, "id")))
        addIn("CallbackEffectsNotAllowed", cat(&.{ head, L(" requires a pure callback: no required effects, capability invocations, resources, tasks or cancellation checks, including transitive calls."), effectsHint(collection, callback) }));
    if (predicate and !resultOf(callback, &checked.substitution).isPrimitive("bool"))
        addIn("PredicateResultTypeMismatch", cat(&.{ head, L(" predicate callback must return Bool.") }));
}

fn lowerAscii(comptime s: []const u8) []const u8 {
    comptime var out: [s.len]u8 = undefined;
    inline for (s, 0..) |c, i| out[i] = comptime std.ascii.toLower(c);
    const final = out;
    return &final;
}

/// `validateLessThanCallback`.
fn validateLessThanCallback(name: Str, node: Value, source: Str, callback_id: Str, context: []const Value) void {
    const head = cat(&.{ name, L(" node '"), str(node, "id"), L("'") });
    const item: ?TypeRef = switch (shapeOf(source, "list", 1)) {
        .args => |args| args[0],
        .other => blk: {
            addIn("HigherOrderSourceTypeMismatch", cat(&.{ head, L(" requires List<T> input.") }));
            break :blk null;
        },
        .unknown => null,
    };
    const callback = functions_by_id.get(idKey(callback_id)) orelse
        return addIn("UnknownCallbackFunction", cat(&.{ head, L(" references unknown callback function '"), callback_id, L("'.") }));
    var checked = validateCallbackContext(node, callback, context);
    if (checked.unbound.len != 2) {
        addIn("CallbackArityMismatch", cat(&.{ head, L(" requires callback '"), str(callback, "id"), L("' to have exactly two parameters not bound by context (left, right).") }));
    } else if (item) |item_type| {
        if (matchFrom(&checked.substitution, &.{ .{ .declared = parameterType(checked.unbound[0]), .actual = item_type }, .{ .declared = parameterType(checked.unbound[1]), .actual = item_type } })) |extended| {
            checked.substitution = extended;
        } else addIn("CallbackParameterTypeMismatch", cat(&.{ head, L(" callback parameter types must both match the source list item type.") }));
    }
    if (!isPure(str(callback, "id")))
        addIn("CallbackEffectsNotAllowed", cat(&.{ head, L(" requires a pure callback: no required effects, capability invocations, resources, tasks or cancellation checks, including transitive calls.") }));
    if (!resultOf(callback, &checked.substitution).isPrimitive("bool"))
        addIn("PredicateResultTypeMismatch", cat(&.{ head, L(" less-than callback must return Bool.") }));
}

/// `validateMapEntryCallback`: MapFilter and MapMapValues.
fn validateMapEntryCallback(name: Str, require_bool: bool, node: Value, source: Str, callback_id: Str, context: []const Value) void {
    const head = cat(&.{ name, L(" node '"), str(node, "id"), L("'") });
    switch (shapeOf(source, "map", 2)) {
        .args => |args| {
            const keep = functions_by_id.get(idKey(callback_id)) orelse return addIn("UnknownCallbackFunction", cat(&.{ head, L(" references an unknown callback.") }));
            var checked = validateCallbackContext(node, keep, context);
            if (checked.unbound.len == 2) {
                if (matchFrom(&checked.substitution, &.{ .{ .declared = parameterType(checked.unbound[0]), .actual = args[0] }, .{ .declared = parameterType(checked.unbound[1]), .actual = args[1] } })) |extended| {
                    checked.substitution = extended;
                } else addIn("CallbackParameterTypeMismatch", cat(&.{ head, L(" callback input types must match the key and value types.") }));
            } else addIn("CallbackArityMismatch", cat(&.{ head, L(" callback must have exactly two parameters not bound by context (key, value).") }));
            if (require_bool and !resultOf(keep, &checked.substitution).isPrimitive("bool"))
                addIn("PredicateResultTypeMismatch", cat(&.{ head, L(" callback must return Bool.") }));
            if (!isPure(str(keep, "id")))
                addIn("CallbackEffectsNotAllowed", cat(&.{ head, L(" callback must be pure, including its transitive calls and ambient task or cancellation behavior.") }));
        },
        .other => addIn("MapInputTypeMismatch", cat(&.{ head, L(" requires a Map<K, V> input.") })),
        .unknown => {},
    }
}

const CasePayload = enum { option_item, result_ok, result_error };
const BinderResult = enum { none, option, result_with_source_error };

fn casePayload(t: TypeRef, payload: CasePayload) ?TypeRef {
    return switch (payload) {
        .option_item => if (t.isGeneric("option") and t.args.len == 1) t.args[0] else null,
        .result_ok => if (t.isGeneric("result") and t.args.len == 2) t.args[0] else null,
        .result_error => if (t.isGeneric("result") and t.args.len == 2) t.args[1] else null,
    };
}

/// `validateCaseCallback`: OptionMap, OptionAndThen, ResultMap, ResultMapErr and ResultAndThen.
fn validateCaseCallback(name: Str, comptime source_shape: []const u8, node: Value, source: Str, callback_id: Str, context: []const Value, payload: CasePayload, binder: BinderResult) void {
    const head = cat(&.{ name, L(" node '"), str(node, "id"), L("'") });
    const source_type = nodeType(source) orelse return;
    const payload_type = casePayload(source_type, payload) orelse return addIn("HigherOrderSourceTypeMismatch", cat(&.{ head, L(" requires " ++ source_shape ++ " input.") }));
    const callback = functions_by_id.get(idKey(callback_id)) orelse
        return addIn("UnknownCallbackFunction", cat(&.{ head, L(" references unknown callback function '"), callback_id, L("'.") }));
    var checked = validateCallbackContext(node, callback, context);
    if (checked.unbound.len == 1) {
        if (matchOne(&checked.substitution, parameterType(checked.unbound[0]), payload_type)) |extended| {
            checked.substitution = extended;
        } else addIn("CallbackParameterTypeMismatch", cat(&.{ head, L(" callback parameter type does not match the " ++ source_shape ++ " payload type.") }));
    } else addIn("CallbackArityMismatch", cat(&.{ head, L(" requires callback '"), str(callback, "id"), L("' to have exactly one parameter not bound by context.") }));
    const result = resultOf(callback, &checked.substitution);
    switch (binder) {
        .none => {},
        .option => if (casePayload(result, .option_item) == null) addIn("CallbackResultTypeMismatch", cat(&.{ head, L(" callback must return Option<R>.") })),
        .result_with_source_error => {
            const source_error = casePayload(source_type, .result_error);
            const result_error = casePayload(result, .result_error);
            if (source_error == null or result_error == null or !typeEql(source_error.?, result_error.?))
                addIn("CallbackResultTypeMismatch", cat(&.{ head, L(" callback must return Result<R, E> with the source's error type E.") }));
        },
    }
    if (!isPure(str(callback, "id")))
        addIn("CallbackEffectsNotAllowed", cat(&.{ head, L(" requires a pure callback: no required effects, capability invocations, resources, tasks or cancellation checks, including transitive calls.") }));
}

/// ListFold, ListScan, SetFold, MapFold and ListEffectFold.
fn validateFold(name: Str, kind: []const u8, node: Value, ops: []const Str, context: []const Value) void {
    const head = cat(&.{ name, L(" node '"), str(node, "id"), L("'") });
    const is_map = std.mem.eql(u8, kind, "mapFold");
    const is_set = std.mem.eql(u8, kind, "setFold");
    const effect = std.mem.eql(u8, kind, "listEffectFold");
    const source_shape = if (is_map) shapeOf(ops[0], "map", 2) else if (is_set) shapeOf(ops[0], "set", 1) else shapeOf(ops[0], "list", 1);
    const seed_type = nodeType(ops[1]);
    if (source_shape == .unknown or seed_type == null) return;
    const args = switch (source_shape) {
        .args => |args| args,
        else => return if (is_map)
            addIn("MapInputTypeMismatch", cat(&.{ head, L(" requires a Map<K, V> input.") }))
        else if (is_set)
            addIn("SetInputTypeMismatch", cat(&.{ head, L(" requires a Set<T> input.") }))
        else
            addIn("HigherOrderSourceTypeMismatch", cat(&.{ head, L(" requires List<T> input.") })),
    };
    const seed = seed_type.?;
    const folder = functions_by_id.get(idKey(str(node, "folderFunctionId"))) orelse
        return addIn("UnknownCallbackFunction", cat(&.{ head, L(" references an unknown folder.") }));
    var checked = validateCallbackContext(node, folder, context);
    const arity: usize = if (is_map) 3 else 2;
    if (checked.unbound.len != arity) {
        addIn("CallbackArityMismatch", if (is_map)
            cat(&.{ head, L(" folder must have exactly three parameters not bound by context (accumulator, key, value).") })
        else
            cat(&.{ head, L(" folder must have exactly two parameters not bound by context (accumulator, item).") }));
    } else {
        var pairs: List(Pair) = .empty;
        pairs.append(a, .{ .declared = parameterType(checked.unbound[0]), .actual = seed }) catch oom();
        for (checked.unbound[1..], args) |parameter, t| pairs.append(a, .{ .declared = parameterType(parameter), .actual = t }) catch oom();
        if (matchFrom(&checked.substitution, pairs.items)) |extended| {
            checked.substitution = extended;
        } else addIn("CallbackParameterTypeMismatch", cat(&.{ head, if (is_map) L(" folder input types must match the seed, key and value types.") else if (is_set) L(" folder input types must match seed and set item types.") else L(" folder input types must match seed and list item types.") }));
    }
    const result = resultOf(folder, &checked.substitution);
    if (effect) {
        const accepted = typeEql(result, seed) or (result.isGeneric("result") and result.args.len == 2 and typeEql(result.args[0], seed));
        if (!accepted) addIn("CallbackResultTypeMismatch", cat(&.{ head, L(" folder must return the seed type S or Result<S, E>.") }));
        missingEffects(folder, cat(&.{ L("Function "), functionLabel(in_function.function), L(" folds with "), functionLabel(folder), L(" but does not declare required effect ") }), L("."));
        return;
    }
    if (!typeEql(result, seed)) addIn("CallbackResultTypeMismatch", cat(&.{ head, L(" folder must return the seed type.") }));
    if (!isPure(str(folder, "id")))
        addIn("CallbackEffectsNotAllowed", cat(&.{ head, L(" folder must be pure, including its transitive calls and ambient task or cancellation behavior.") }));
}

/// A body callback with one unbound parameter of a fixed type (CancellationShield, TaskScope,
/// UseResource): the substitution that makes it fit, or none.
fn bodySubstitution(checked: *const CallbackContext, input: TypeRef) ?Substitution {
    if (checked.unbound.len != 1) return null;
    return matchOne(&checked.substitution, parameterType(checked.unbound[0]), input);
}

/// The higher-order, effect and task nodes (Validation.fs `ListFold` … `ListAll`).
fn callbackRules(node: Value, local: *const Set) void {
    const kind = text(str(node, "kind"));
    const name = caseNameOf(str(node, "kind"));
    const id = str(node, "id");
    const ops = operandsOf(node);
    const context = arrayOf(node, "context");
    const function_label = functionLabel(in_function.function);
    if (kindIs(kind, "listFold listScan mapFold setFold listEffectFold")) {
        validateFold(name, kind, node, ops, context);
    } else if (kindIs(kind, "mapFilter mapMapValues")) {
        validateMapEntryCallback(name, std.mem.eql(u8, kind, "mapFilter"), node, ops[0], str(node, if (std.mem.eql(u8, kind, "mapFilter")) "keepFunctionId" else "mapperFunctionId"), context);
    } else if (kindIs(kind, "setFilter setMap")) {
        const filter = std.mem.eql(u8, kind, "setFilter");
        validateCallbackOver("Set", node, ops[0], str(node, if (filter) "predicateFunctionId" else "mapperFunctionId"), context, filter);
        if (!filter) if (nodeType(id)) |t| if (t.isGeneric("set") and t.args.len == 1 and !isEquatable(t.args[0]))
            addIn("SetItemTypeNotEquatable", cat(&.{ L("SetMap node '"), id, L("' makes a Set of a type that is not equatable: "), explainEquatable(t.args[0]) }));
    } else if (std.mem.eql(u8, kind, "optionMap")) {
        validateCaseCallback(name, "Option<T>", node, ops[0], str(node, "mapperFunctionId"), context, .option_item, .none);
    } else if (std.mem.eql(u8, kind, "optionAndThen")) {
        validateCaseCallback(name, "Option<T>", node, ops[0], str(node, "binderFunctionId"), context, .option_item, .option);
    } else if (std.mem.eql(u8, kind, "resultMap")) {
        validateCaseCallback(name, "Result<T, E>", node, ops[0], str(node, "mapperFunctionId"), context, .result_ok, .none);
    } else if (std.mem.eql(u8, kind, "resultMapErr")) {
        validateCaseCallback(name, "Result<T, E>", node, ops[0], str(node, "mapperFunctionId"), context, .result_error, .none);
    } else if (std.mem.eql(u8, kind, "resultAndThen")) {
        validateCaseCallback(name, "Result<T, E>", node, ops[0], str(node, "binderFunctionId"), context, .result_ok, .result_with_source_error);
    } else if (std.mem.eql(u8, kind, "listZipWith")) {
        const left = shapeOf(ops[0], "list", 1);
        const right = shapeOf(ops[1], "list", 1);
        if (left == .unknown or right == .unknown) return;
        if (left == .other or right == .other) return addIn("HigherOrderSourceTypeMismatch", nodeMessage(name, node, "requires List<T> inputs."));
        const combiner = functions_by_id.get(idKey(str(node, "combinerFunctionId"))) orelse
            return addIn("UnknownCallbackFunction", nodeMessage(name, node, "references an unknown combiner."));
        const checked = validateCallbackContext(node, combiner, context);
        if (checked.unbound.len != 2) {
            addIn("CallbackArityMismatch", nodeMessage(name, node, "combiner must have exactly two parameters not bound by context (left item, right item)."));
        } else if (matchFrom(&checked.substitution, &.{ .{ .declared = parameterType(checked.unbound[0]), .actual = left.args[0] }, .{ .declared = parameterType(checked.unbound[1]), .actual = right.args[0] } }) == null)
            addIn("CallbackParameterTypeMismatch", nodeMessage(name, node, "combiner input types must match the left and right list item types."));
        if (!isPure(str(combiner, "id")))
            addIn("CallbackEffectsNotAllowed", nodeMessage(name, node, "combiner must be pure, including its transitive calls and ambient task or cancellation behavior."));
    } else if (kindIs(kind, "listMap listFlatMap mapFromList listFilter listAny listFindIndex listAll")) {
        const callback_id = str(node, if (std.mem.eql(u8, kind, "mapFromList")) "keyOfFunctionId" else if (kindIs(kind, "listMap listFlatMap")) "mapperFunctionId" else "predicateFunctionId");
        validateCallbackOver("List", node, ops[0], callback_id, context, kindIs(kind, "listFilter listAny listFindIndex listAll"));
        if (std.mem.eql(u8, kind, "listFlatMap")) {
            if (shapeOf(ops[0], "list", 1) == .args and functions_by_id.contains(idKey(callback_id)) and nodeType(id) == null)
                addIn("CallbackResultTypeMismatch", nodeMessage(name, node, "mapper must return List<R>."));
        } else if (std.mem.eql(u8, kind, "mapFromList")) {
            if (nodeType(id)) |t| if (t.isGeneric("map") and t.args.len == 2 and !isEquatable(t.args[0]))
                addIn("MapKeyTypeNotEquatable", cat(&.{ L("MapFromList node '"), id, L("' makes keys of a type that is not equatable: "), explainEquatable(t.args[0]) }));
        }
    } else if (kindIs(kind, "listSortBy listMinBy listMaxBy")) {
        validateLessThanCallback(name, node, ops[0], str(node, "lessThanFunctionId"), context);
    } else if (std.mem.eql(u8, kind, "useResource")) {
        useResourceRules(node, local);
    } else if (std.mem.eql(u8, kind, "exceptionCatch")) {
        const body = catchCallback(node, "body", str(node, "bodyFunctionId"), arrayOf(node, "bodyContext"), primitiveType("unit"), L("Primitive Unit"));
        const handler = catchCallback(node, "handler", str(node, "handlerFunctionId"), arrayOf(node, "handlerContext"), builtin("exception", &.{}), L("BuiltinGeneric (Exception, [])"));
        if (body != null and handler != null) {
            const same_result = body.?.isGeneric("result") and body.?.args.len == 2 and typeEql(body.?, handler.?);
            if (!same_result) addIn("ExceptionCatchResultMismatch", L("ExceptionCatch callbacks must return the same Result<T, E>."));
        }
    } else if (kindIs(kind, "exceptionIsCancellation exceptionMessage exceptionRethrow")) {
        const value_type = nodeType(ops[0]);
        if (value_type == null or !typeEql(value_type.?, builtin("exception", &.{})))
            addIn("ExceptionValueTypeMismatch", cat(&.{ name, L(" requires an Exception value.") }));
    } else if (kindIs(kind, "cancellationShield taskScope")) {
        const shield = std.mem.eql(u8, kind, "cancellationShield");
        const body = functions_by_id.get(idKey(str(node, "bodyFunctionId"))) orelse
            return addIn("UnknownCallTarget", nodeMessage(name, node, "references an unknown body function."));
        const checked = validateCallbackContext(node, body, context);
        const fitted = bodySubstitution(&checked, if (shield) primitiveType("unit") else builtin("taskScope", &.{}));
        if (fitted == null) addIn(if (shield) "CancellationShieldBodyMismatch" else "TaskScopeBodyMismatch", if (shield) nodeMessage(name, node, "body must have exactly one unbound parameter of type Unit.") else nodeMessage(name, node, "body must have exactly one unbound parameter of type TaskScope."));
        const result = resultOf(body, if (fitted) |*f| f else &checked.substitution);
        if (!(result.isGeneric("result") and result.args.len == 2))
            addIn(if (shield) "CancellationShieldResultMismatch" else "TaskScopeResultMismatch", nodeMessage(name, node, "body must return Result<T, E>."));
        missingEffects(body, cat(&.{ L("Function "), function_label, if (shield) L(" runs cancellation shield body ") else L(" runs task scope body "), functionLabel(body), L(" but does not declare required effect ") }), L("."));
    } else if (std.mem.eql(u8, kind, "taskStart")) {
        rejectTaskBoundInputs(name, node, ops[1], "item");
        if (nodeType(ops[0])) |scope| if (!typeEql(scope, builtin("taskScope", &.{})))
            addIn("TaskStartScopeTypeMismatch", nodeMessage(name, node, "requires a TaskScope value as its scope."));
        const item = nodeType(ops[1]);
        const worker = functions_by_id.get(idKey(str(node, "workerFunctionId"))) orelse
            return addIn("UnknownCallbackFunction", nodeMessage(name, node, "references an unknown worker."));
        const item_type = item orelse return;
        workerRules(name, node, worker, context, item_type, " starts ", " as a task but does not declare required effect ");
    } else if (std.mem.eql(u8, kind, "parallelAll")) {
        rejectTaskBoundInputs(name, node, ops[0], "source");
        const source = shapeOf(ops[0], "list", 1);
        const worker = functions_by_id.get(idKey(str(node, "workerFunctionId")));
        if (source == .args and worker != null) {
            workerRules(name, node, worker.?, context, source.args[0], " runs ", " in parallel but does not declare required effect ");
        } else if (source == .other and worker != null) {
            addIn("HigherOrderSourceTypeMismatch", nodeMessage(name, node, "requires List<T> input."));
        } else if (worker == null) addIn("UnknownCallbackFunction", nodeMessage(name, node, "references an unknown worker."));
    } else if (std.mem.eql(u8, kind, "taskWait")) {
        if (nodeType(str(node, "tasksNodeId"))) |t| if (!(t.isGeneric("list") and t.args.len == 1 and t.args[0].isGeneric("task") and t.args[0].args.len == 1))
            addIn("TaskWaitInputTypeMismatch", nodeMessage(name, node, "requires List<Task<T>> input."));
        if (limitType(node)) |t| if (!isOptionInt64(t)) addIn("TaskWaitLimitTypeMismatch", nodeMessage(name, node, "requires an Option<Int64> limit."));
    } else if (std.mem.eql(u8, kind, "oneShotCreate")) {
        if (nodeType(ops[0])) |t| if (!typeEql(t, builtin("taskScope", &.{}))) addIn("OneShotScopeTypeMismatch", L("OneShotCreate requires a TaskScope."));
    } else if (std.mem.eql(u8, kind, "oneShotComplete")) {
        const cell = nodeType(ops[0]) orelse return;
        if (!(cell.isGeneric("oneShot") and cell.args.len == 1)) return addIn("OneShotCellTypeMismatch", L("OneShotComplete requires OneShot<T>."));
        if (nodeType(ops[1])) |actual| if (!typeEql(cell.args[0], actual)) addIn("OneShotValueTypeMismatch", L("OneShotComplete payload must match the cell's exact type."));
    } else if (std.mem.eql(u8, kind, "oneShotWait")) {
        if (nodeType(ops[0])) |cell| if (!(cell.isGeneric("oneShot") and cell.args.len == 1)) addIn("OneShotCellTypeMismatch", L("OneShotWait requires OneShot<T>."));
        if (limitType(node)) |t| if (!isOptionInt64(t)) addIn("OneShotLimitTypeMismatch", L("OneShotWait requires Option<Int64> limit."));
    } else if (std.mem.eql(u8, kind, "taskCancel")) {
        if (nodeType(ops[0])) |t| if (!(t.isGeneric("task") and t.args.len == 1)) addIn("TaskCancelInputTypeMismatch", nodeMessage(name, node, "requires a Task<T> input."));
    }
}

fn limitType(node: Value) ?TypeRef {
    const limit = tryProperty("limitNodeId", node) orelse return null;
    return nodeType(limit.string);
}

fn isOptionInt64(t: TypeRef) bool {
    return t.isGeneric("option") and t.args.len == 1 and t.args[0].isPrimitive("int64");
}

/// The worker of TaskStart and ParallelAll, given the item it receives.
fn workerRules(name: Str, node: Value, worker: Value, context: []const Value, item_type: TypeRef, comptime runs: []const u8, comptime missing: []const u8) void {
    const checked = validateCallbackContext(node, worker, context);
    if (bodySubstitution(&checked, item_type)) |fitted| {
        const result = resultOf(worker, &fitted);
        rejectTaskBound(nodeMessage(name, node, "worker result"), result);
        if (!(result.isGeneric("result") and result.args.len == 2)) addIn("CallbackResultTypeMismatch", nodeMessage(name, node, "worker must return Result<U, E>."));
    } else addIn("CallbackParameterTypeMismatch", nodeMessage(name, node, "worker must take exactly one unbound parameter of the item type."));
    missingEffects(worker, cat(&.{ L("Function "), functionLabel(in_function.function), L(runs), functionLabel(worker), L(missing) }), L("."));
}

/// One callback of ExceptionCatch: its result type, or none when the function is unknown.
fn catchCallback(node: Value, comptime role: []const u8, function_id: Str, context: []const Value, input: TypeRef, input_text: Str) ?TypeRef {
    const callback = functions_by_id.get(idKey(function_id)) orelse {
        addIn("UnknownCallTarget", L("ExceptionCatch " ++ role ++ " references an unknown function."));
        return null;
    };
    const checked = validateCallbackContext(node, callback, context);
    const fitted = bodySubstitution(&checked, input);
    if (fitted == null) addIn("ExceptionCatchCallbackMismatch", cat(&.{ L("ExceptionCatch " ++ role ++ " must have one unbound parameter of type "), input_text, L(".") }));
    missingEffects(callback, L("ExceptionCatch " ++ role ++ " requires effect "), L("."));
    return resultOf(callback, if (fitted) |*f| f else &checked.substitution);
}

/// `capabilityOperationsById`: each operation with its capability (the last of a duplicated ID wins).
var operation_owners: std.StringArrayHashMapUnmanaged(Str) = .empty;

fn useResourceRules(node: Value, local: *const Set) void {
    const id = str(node, "id");
    const acquire = str(node, "acquireNodeId");
    const head = cat(&.{ L("UseResource node '"), id, L("'") });
    if (in_function.resolver.nodes_by_id.contains(idKey(acquire))) {
        if (!local.contains(idKey(acquire)))
            addIn("UseResourceAcquireOutOfGraph", cat(&.{ head, L(" acquires resource node '"), acquire, L("' from an enclosing graph; the acquisition must be owned by the same graph as the node that releases it.") }));
        for (in_function.all_nodes) |other| {
            if (same(str(other, "id"), id)) continue;
            for (operandsOf(other)) |reference| if (same(reference, acquire)) {
                addIn("UseResourceAcquireShared", cat(&.{ head, L(" must be the only consumer of resource node '"), acquire, L("', which node '"), str(other, "id"), L("' also uses.") }));
                break;
            };
        }
    }
    const acquired = nodeType(acquire) orelse return;
    if (!(acquired.isGeneric("result") and acquired.args.len == 2)) return addIn("UseResourceAcquireTypeMismatch", cat(&.{ head, L(" acquisition must produce Result<R, E>.") }));
    const body = functions_by_id.get(idKey(str(node, "bodyFunctionId"))) orelse return addIn("UnknownCallTarget", cat(&.{ head, L(" references an unknown body function.") }));
    const release_id = idKey(str(node, "releaseOperationId"));
    const operation = operations_by_id.get(release_id) orelse return addIn("UnknownCapabilityOperation", cat(&.{ head, L(" references an unknown release operation.") }));
    const resource_type = acquired.args[0];
    const error_type = acquired.args[1];
    const checked = validateCallbackContext(node, body, arrayOf(node, "context"));
    const fitted = bodySubstitution(&checked, resource_type);
    if (fitted == null) addIn("UseResourceBodyMismatch", cat(&.{ head, L(" body must have exactly one unbound parameter of the resource type.") }));
    const result = resultOf(body, if (fitted) |*f| f else &checked.substitution);
    if (!(result.isGeneric("result") and result.args.len == 2 and typeEql(result.args[1], error_type)))
        addIn("UseResourceResultMismatch", cat(&.{ head, L(" body must return Result<T, E> with the acquisition error type.") }));
    const receiver = nodeType(str(node, "releaseCapabilityNodeId"));
    if (receiver == null or receiver.?.kind != .capability or !same(receiver.?.name, operation_owners.get(release_id).?))
        addIn("UseResourceReleaseReceiverMismatch", cat(&.{ head, L(" release capability does not own the release operation.") }));
    const parameters = arrayOf(operation, "parameters");
    const release_result = typeRefOf(get(operation, "resultType"));
    const signature = parameters.len == 1 and typeEql(parameterType(parameters[0]), resource_type) and
        (release_result.isPrimitive("unit") or typeEql(release_result, builtin("result", &.{ primitiveType("unit"), error_type })));
    if (!signature) addIn("UseResourceReleaseSignatureMismatch", cat(&.{ head, L(" release must take the resource and return Unit or Result<Unit, E>.") }));
    const before = cat(&.{ L("Function "), functionLabel(in_function.function), L(" uses a resource requiring effect ") });
    missingEffects(body, before, L(" that it does not declare."));
    const effect_id = str(operation, "effectId");
    if (!in_function.required_effects.contains(idKey(effect_id))) addIn("MissingEffectRequirement", cat(&.{ before, effectLabel(effect_id), L(" that it does not declare.") }));
}

/// Open obligations as (call, parameter) pairs, for `MissingArgument`.
var open_obligation_pairs: Set = .empty;

/// `CapabilityImplementations.validate`, prefixed "Capability construction '<id>': ".
fn capabilityImplementationRules(node: Value) void {
    const head = cat(&.{ L("Capability construction '"), str(node, "id"), L("': ") });
    const capability_id = str(node, "capabilityId");
    const capability = for (all_capabilities) |c| {
        if (same(str(c, "id"), capability_id)) break c;
    } else return addIn("UnknownCapabilityConstructTarget", cat(&.{ head, L("A capability construction must name an existing capability.") }));
    const operations = byId(arrayOf(capability, "operations"));
    const implementations = arrayOf(node, "implementations");
    const operation_ids = a.alloc(Str, implementations.len) catch oom();
    for (implementations, operation_ids) |implementation, *id| id.* = str(implementation, "operationId");
    var duplicated = false;
    for (countBy(operation_ids).values()) |entry| duplicated = duplicated or entry.count > 1;
    if (duplicated) addIn("DuplicateCapabilityImplementation", cat(&.{ head, L("Each capability operation must have exactly one implementation.") }));
    var implemented: Set = .empty;
    for (operation_ids) |id| implemented.put(a, idKey(id), {}) catch oom();
    var declared: Set = .empty;
    for (operations.keys()) |key| declared.put(a, key, {}) catch oom();
    if (!sameSet(&implemented, &declared)) addIn("CapabilityImplementationSetMismatch", cat(&.{ head, L("Implementations must cover exactly the operations of the constructed capability.") }));
    for (implementations) |implementation| {
        const operation_id = str(implementation, "operationId");
        const function_id = str(implementation, "functionId");
        const function = functions_by_id.get(idKey(function_id)) orelse {
            addIn("UnknownCapabilityImplementationFunction", cat(&.{ head, L("Implementation function '"), function_id, L("' does not exist.") }));
            continue;
        };
        const operation = operations.get(idKey(operation_id)) orelse continue;
        if (arrayOf(function, "typeParameters").len > 0) addIn("GenericCapabilityImplementationUnsupported", cat(&.{ head, L("Use a monomorphic wrapper for a generic implementation function.") }));
        if (!isPure(function_id)) addIn("ImpureCapabilityImplementation", cat(&.{ head, L("Implementation '"), function_id, L("' must be transitively pure, including ambient cancellation behavior.") }));
        const function_parameters = arrayOf(function, "parameters");
        const operation_parameters = arrayOf(operation, "parameters");
        var signature = function_parameters.len == operation_parameters.len and typeEql(typeRefOf(get(function, "resultType")), typeRefOf(get(operation, "resultType")));
        if (signature) for (function_parameters, operation_parameters) |f, o| {
            signature = signature and typeEql(parameterType(f), parameterType(o));
        };
        if (!signature) addIn("CapabilityImplementationSignatureMismatch", cat(&.{ head, L("Implementation '"), function_id, L("' must match operation '"), operation_id, L("' parameter order, parameter types and result type exactly.") }));
    }
}

/// Function references and invocations, direct calls and capabilities (Validation.fs
/// `CapabilityConstruct` … `CapabilityInvoke`).
fn callRules(node: Value) void {
    const kind = text(str(node, "kind"));
    const id = str(node, "id");
    const function_label = functionLabel(in_function.function);
    if (std.mem.eql(u8, kind, "capabilityConstruct")) {
        capabilityImplementationRules(node);
    } else if (std.mem.eql(u8, kind, "functionReference")) {
        const target_id = str(node, "targetFunctionId");
        const head = cat(&.{ L("Function reference '"), id, L("'") });
        const target = functions_by_id.get(idKey(target_id)) orelse return addIn("UnknownFunctionReference", cat(&.{ head, L(" names unknown function '"), target_id, L("'.") }));
        if (arrayOf(target, "typeParameters").len > 0) {
            addIn("GenericFunctionReferenceUnsupported", cat(&.{ head, L(" must reference a monomorphic function; use a concrete wrapper.") }));
        } else if (!isPure(target_id)) addIn("ImpureFunctionReference", cat(&.{ head, L(" must reference a pure function, including its transitive calls and ambient cancellation behavior.") }));
    } else if (std.mem.eql(u8, kind, "functionInvoke")) {
        const head = cat(&.{ L("Function invocation '"), id, L("'") });
        const value_type = nodeType(str(node, "functionValueNodeId")) orelse return;
        if (value_type.kind != .function) return addIn("FunctionInvokeTargetTypeMismatch", cat(&.{ head, L(" requires a function value.") }));
        const arguments = arrayOf(node, "arguments");
        if (value_type.args.len != arguments.len) return addIn("FunctionInvokeArityMismatch", cat(&.{ head, L(" must supply exactly one value for each positional parameter.") }));
        for (value_type.args, arguments) |expected, argument| {
            if (nodeType(argument.string)) |actual| if (!typeEql(actual, expected))
                addIn("FunctionInvokeArgumentTypeMismatch", cat(&.{ head, L(" argument has the wrong type.") }));
        }
    } else if (std.mem.eql(u8, kind, "directCall")) {
        directCallRules(node);
    } else if (std.mem.eql(u8, kind, "capabilityInvoke")) {
        const operation_id = str(node, "operationId");
        const head = cat(&.{ L("Capability-invoke node '"), id, L("'") });
        const operation = operations_by_id.get(idKey(operation_id)) orelse return addIn("UnknownCapabilityOperation", cat(&.{ head, L(" references unknown operation '"), operation_id, L("'.") }));
        if (nodeType(str(node, "capabilityValueNodeId"))) |t| if (t.kind != .capability or !same(t.name, operation_owners.get(idKey(operation_id)).?))
            addIn("CapabilityValueTypeMismatch", cat(&.{ head, L(" does not receive the capability that owns operation '"), operation_id, L("'.") }));
        const arguments = arrayOf(node, "arguments");
        const targets = a.alloc(Str, arguments.len) catch oom();
        for (arguments, targets) |argument, *t| t.* = str(argument, "targetCapabilityParameterId");
        for (countBy(targets).values()) |entry| {
            if (entry.count > 1) addIn("DuplicateCapabilityArgumentTarget", cat(&.{ head, L(" binds capability parameter '"), entry.id, L("' more than once.") }));
        }
        const parameters = arrayOf(operation, "parameters");
        var actual: Set = .empty;
        for (targets) |t| actual.put(a, idKey(t), {}) catch oom();
        if (!sameSet(&idSet(parameters), &actual))
            addIn("CapabilityArgumentSetMismatch", cat(&.{ head, L(" must bind exactly the parameters declared by operation '"), operation_id, L("'.") }));
        for (arguments, targets) |argument, target| {
            const parameter = for (parameters) |p| {
                if (same(str(p, "id"), target)) break p;
            } else continue;
            if (nodeType(str(argument, "valueNodeId"))) |value_type| if (!typeEql(parameterType(parameter), value_type))
                addIn("CapabilityArgumentTypeMismatch", cat(&.{ head, L(" binds a value with the wrong type to capability parameter "), describe(str(parameter, "displayName"), str(parameter, "id")), L(".") }));
        }
        const effect_id = str(operation, "effectId");
        if (!in_function.required_effects.contains(idKey(effect_id)))
            addIn("MissingEffectRequirement", cat(&.{ L("Function "), function_label, L(" invokes capability operation "), describe(str(operation, "displayName"), operation_id), L(" but does not declare required effect "), effectLabel(effect_id), L(".") }));
    }
}

fn directCallRules(node: Value) void {
    const id = str(node, "id");
    const target_id = str(node, "targetFunctionId");
    const head = cat(&.{ L("Call node '"), id, L("'") });
    const function = in_function.function;
    const target = functions_by_id.get(idKey(target_id)) orelse return addIn("UnknownCallTarget", cat(&.{ head, L(" references unknown function '"), target_id, L("'.") }));
    const arguments = arrayOf(node, "arguments");
    const targets = a.alloc(Str, arguments.len) catch oom();
    for (arguments, targets) |argument, *t| t.* = str(argument, "targetParameterId");
    for (countBy(targets).values()) |entry| {
        if (entry.count > 1) addIn("DuplicateArgumentTarget", cat(&.{ head, L(" binds parameter '"), entry.id, L("' more than once.") }));
    }
    for (targets) |t| {
        if (parameterOf(target, t) == null) addIn("UnknownArgumentTargetParameter", cat(&.{ head, L(" targets parameter '"), t, L("' not owned by its callee.") }));
    }
    // A generic callee's type arguments follow from matching every bound argument.
    const Bound = struct { parameter: Value, value_type: TypeRef };
    var pairs: List(Bound) = .empty;
    for (arguments, targets) |argument, t| {
        const parameter = parameterOf(target, t);
        const value_type = nodeType(str(argument, "valueNodeId"));
        if (parameter != null and value_type != null) pairs.append(a, .{ .parameter = parameter.?, .value_type = value_type.? }) catch oom();
    }
    if (same(target_id, str(function, "id"))) {
        // A self call takes the function's exact parameter types.
        for (pairs.items) |pair| {
            const declared = parameterType(pair.parameter);
            if (!typeEql(pair.value_type, declared))
                addIn("PolymorphicSelfCallUnsupported", cat(&.{ head, L(" calls its own function "), functionLabel(function), L(" with a value of type "), describeType(pair.value_type), L(" for parameter "), describe(str(pair.parameter, "displayName"), str(pair.parameter, "id")), L(" of type "), describeType(declared), L("; a function calls itself at its own parameter types.") }));
        }
    } else {
        var matched: List(Pair) = .empty;
        for (pairs.items) |pair| matched.append(a, .{ .declared = parameterType(pair.parameter), .actual = pair.value_type }) catch oom();
        if (matchAll(matched.items)) |substitution| {
            if (violates(target_id, &substitution))
                addIn("OneShotScopePayloadUnsupported", cat(&.{ head, L(" instantiates a OneShot payload with a type containing TaskScope, directly or through another generic call.") }));
        } else addIn("ArgumentTypeMismatch", cat(&.{ head, L(" binds a value whose type does not match its callee parameter.") }));
    }
    for (arrayOf(target, "parameters")) |parameter| {
        const parameter_id = str(parameter, "id");
        const bound = bindsParameter(arguments, parameter_id);
        const open = open_obligation_pairs.contains(idKey(cat(&.{ id, L("\n"), parameter_id })));
        if (!bound and !open) addIn("MissingArgument", cat(&.{ head, L(" has no binding or open obligation for parameter "), describe(str(parameter, "displayName"), parameter_id), L(".") }));
    }
    for (arrayOf(target, "requiredEffects")) |effect| {
        if (!in_function.required_effects.contains(idKey(effect.string)))
            addIn("MissingEffectRequirement", cat(&.{ L("Function "), functionLabel(function), L(" calls "), functionLabel(target), L(" but does not declare required effect "), effectLabel(effect.string), L(".") }));
    }
}

/// `validateGraph`: the graph's own rules, each node's rules, then its nested graphs.
fn validateGraph(outer_visible: *const Set, visible_patterns: *const Set, context: Str, graph: Value) void {
    const nodes = arrayOf(graph, "nodes");
    const local = idSet(nodes);
    var visible = outer_visible.clone(a) catch oom();
    for (local.keys()) |key| visible.put(a, key, {}) catch oom();

    const result_id = str(graph, "resultNodeId");
    if (!local.contains(idKey(result_id)))
        addIn("UnknownGraphResultNode", cat(&.{ context, L(" result node '"), result_id, L("' is not owned directly by that graph.") }));

    for (nodes) |node| {
        const id = str(node, "id");
        var references: List(Str) = .empty;
        directValueReferences(node, &references);
        for (references.items) |reference| {
            if (!visible.contains(idKey(reference)))
                addIn("ValueNodeOutOfScope", cat(&.{ L("Graph node '"), id, L("' references value node '"), reference, L("' outside its lexical value-graph scope.") }));
        }
        scalarRules(node);
        collectionRules(node);
        structureRules(node, result_id);
        callbackRules(node, &local);
        callRules(node);
        const kind = str(node, "kind");
        if (isName(kind, "parameterReference")) {
            const parameter_id = str(node, "parameterId");
            if (!in_function.parameters_by_id.contains(idKey(parameter_id)))
                addIn("UnknownParameterReference", cat(&.{ L("Graph node '"), id, L("' references parameter '"), parameter_id, L("' outside its owning function.") }));
        } else if (isName(kind, "patternReference")) {
            const binding_id = str(node, "patternBindingId");
            if (!visible_patterns.contains(idKey(binding_id)))
                addIn("UnknownPatternReference", cat(&.{ L("Graph node '"), id, L("' references pattern binding '"), binding_id, L("' outside its lexical binding scope.") }));
        } else if (isName(kind, "stringLiteral")) {
            // `NullStringLiteral` cannot occur: the reader requires a string.
            if (hasLoneSurrogate(str(node, "value")))
                addIn("StringLiteralUnpairedSurrogate", nodeLabel("String literal", node, "contains an unpaired surrogate; a String is a sequence of Unicode scalar values."));
        } else if (isName(kind, "boolNot")) {
            requireNodeType("BoolOperandTypeMismatch", nodeLabel("BoolNot", node, "requires a Bool operand."), primitiveType("bool"), str(node, "operandNodeId"));
        } else if (isName(kind, "boolAnd") or isName(kind, "boolOr")) {
            const role: Str = if (isName(kind, "boolAnd")) L("BoolAnd") else L("BoolOr");
            requireNodeType("BoolOperandTypeMismatch", cat(&.{ role, L(" node '"), id, L("' requires a Bool left operand.") }), primitiveType("bool"), str(node, "leftNodeId"));
            const right = get(node, "right");
            // The right graph is a Conditional branch in all but name: abrupt is allowed, normal must be Bool.
            if (normalType(in_function.resolver.graph(right))) |t| if (!t.isPrimitive("bool"))
                addIn("BoolOperandTypeMismatch", cat(&.{ role, L(" node '"), id, L("' requires a Bool right operand.") }));
            validateGraph(&visible, visible_patterns, cat(&.{ role, L(" right operand of '"), id, L("'") }), right);
        } else if (isName(kind, "conditional")) {
            if (nodeTypeAt(node, "conditionNodeId")) |t| if (!t.isPrimitive("bool"))
                addIn("ConditionalConditionTypeMismatch", nodeLabel("Conditional", node, "requires a Bool condition."));
            const when_true = get(node, "whenTrue");
            const when_false = get(node, "whenFalse");
            if (!compatibleArms(&.{ when_true, when_false }))
                addIn("ConditionalBranchTypeMismatch", nodeLabel("Conditional", node, "branches do not produce one common type."));
            validateGraph(&visible, visible_patterns, cat(&.{ L("Conditional true branch of '"), id, L("'") }), when_true);
            validateGraph(&visible, visible_patterns, cat(&.{ L("Conditional false branch of '"), id, L("'") }), when_false);
        } else if (isName(kind, "matchUnion")) {
            for (arrayOf(node, "arms")) |arm| {
                var patterns = visible_patterns.clone(a) catch oom();
                for (arrayOf(arm, "fieldBindings")) |field| patterns.put(a, idKey(str(get(field, "binding"), "id")), {}) catch oom();
                validateGraph(&visible, &patterns, cat(&.{ L("Union match arm '"), describeArm(arm), L("'") }), get(arm, "body"));
            }
        } else if (isName(kind, "bind")) {
            const body = get(node, "boundBody");
            validateGraph(&visible, &withPattern(visible_patterns, get(body, "valueBinding")), cat(&.{ L("Bind body of '"), id, L("'") }), get(body, "body"));
        } else if (isName(kind, "matchOption")) {
            const some = get(node, "someArm");
            validateGraph(&visible, &withPattern(visible_patterns, get(some, "valueBinding")), cat(&.{ L("Option.Some arm of '"), id, L("'") }), get(some, "body"));
            validateGraph(&visible, visible_patterns, cat(&.{ L("Option.None arm of '"), id, L("'") }), get(node, "noneBody"));
        } else if (isName(kind, "matchResult")) {
            const ok_arm = get(node, "okArm");
            const err_arm = get(node, "errArm");
            validateGraph(&visible, &withPattern(visible_patterns, get(ok_arm, "valueBinding")), cat(&.{ L("Result.Ok arm of '"), id, L("'") }), get(ok_arm, "body"));
            validateGraph(&visible, &withPattern(visible_patterns, get(err_arm, "errorBinding")), cat(&.{ L("Result.Err arm of '"), id, L("'") }), get(err_arm, "body"));
        }
    }
}

fn withPattern(patterns: *const Set, binding: Value) Set {
    var out = patterns.clone(a) catch oom();
    out.put(a, idKey(str(binding, "id")), {}) catch oom();
    return out;
}

/// `UnionMatchArm.canonicalVariantIds` joined with `|`: the arm's variant IDs, sorted ordinally.
fn describeArm(arm: Value) Str {
    const ids = a.dupe(Str, armVariantIds(arm)) catch oom();
    std.mem.sort(Str, ids, {}, lessStr);
    var out: List(u16) = .empty;
    for (ids, 0..) |v, i| {
        if (i > 0) out.append(a, '|') catch oom();
        out.appendSlice(a, v) catch oom();
    }
    return out.items;
}

/// The per-function loop of `validateUncached` (the rules ported so far).
// Slice 3d: effect order, waiting, OneShot payloads and task crossing.

const Role = enum { direct_call, pure_callback, effect_callback, worker, body, function_value, implementation };
const FunctionRef = struct { function_id: Str, role: Role, arguments: []const Value };

/// `SemanticNodeShape.functionReferences`, in payload order.
fn functionReferences(node: Value) []const FunctionRef {
    var out: List(FunctionRef) = .empty;
    const kind = text(str(node, "kind"));
    const context = arrayOf(node, "context");
    if (std.mem.eql(u8, kind, "directCall")) {
        out.append(a, .{ .function_id = str(node, "targetFunctionId"), .role = .direct_call, .arguments = arrayOf(node, "arguments") }) catch oom();
    } else if (std.mem.eql(u8, kind, "functionReference")) {
        out.append(a, .{ .function_id = str(node, "targetFunctionId"), .role = .function_value, .arguments = &.{} }) catch oom();
    } else if (std.mem.eql(u8, kind, "capabilityConstruct")) {
        for (arrayOf(node, "implementations")) |implementation|
            out.append(a, .{ .function_id = str(implementation, "functionId"), .role = .implementation, .arguments = &.{} }) catch oom();
    } else if (std.mem.eql(u8, kind, "exceptionCatch")) {
        out.append(a, .{ .function_id = str(node, "bodyFunctionId"), .role = .body, .arguments = arrayOf(node, "bodyContext") }) catch oom();
        out.append(a, .{ .function_id = str(node, "handlerFunctionId"), .role = .body, .arguments = arrayOf(node, "handlerContext") }) catch oom();
    } else if (kindIs(kind, "cancellationShield taskScope useResource")) {
        out.append(a, .{ .function_id = str(node, "bodyFunctionId"), .role = .body, .arguments = context }) catch oom();
    } else if (kindIs(kind, "parallelAll taskStart")) {
        out.append(a, .{ .function_id = str(node, "workerFunctionId"), .role = .worker, .arguments = context }) catch oom();
    } else for (node.object) |member| {
        // The pure callbacks: the one `…FunctionId` property of a higher-order node.
        const name = member.name;
        if (name.len > 10 and isName(name[name.len - 10 ..], "FunctionId"))
            out.append(a, .{ .function_id = member.value.string, .role = if (std.mem.eql(u8, kind, "listEffectFold")) .effect_callback else .pure_callback, .arguments = context }) catch oom();
    }
    return out.items;
}

/// `FunctionReferenceRole.isInvoked`.
fn isInvoked(role: Role) bool {
    return role != .function_value and role != .implementation;
}

/// `SemanticNodeShape.invokedOperations`: CapabilityInvoke's operation and UseResource's release.
fn invokedOperation(node: Value) ?Str {
    const kind = str(node, "kind");
    if (isName(kind, "capabilityInvoke")) return str(node, "operationId");
    if (isName(kind, "useResource")) return str(node, "releaseOperationId");
    return null;
}

/// `GraphOrder.nodeObservable` with `semanticView`: Abort and the intrinsically effectful kinds
/// except ParallelAll, a call, effect callback, worker or body of an observable function, or an
/// observable node in a nested graph.
fn nodeObservable(node: Value, observable: *const Set) bool {
    const kind = text(str(node, "kind"));
    if (std.mem.eql(u8, kind, "abort")) return true;
    if (!std.mem.eql(u8, kind, "parallelAll") and kindIs(kind, effectful_kinds)) return true;
    for (functionReferences(node)) |reference| switch (reference.role) {
        .direct_call, .effect_callback, .worker, .body => if (observable.contains(idKey(reference.function_id))) return true,
        else => {},
    };
    for (nestedScopes(node)) |scope| for (arrayOf(scope.graph, "nodes")) |inner| if (nodeObservable(inner, observable)) return true;
    return false;
}

const Edges = std.StringArrayHashMapUnmanaged([]const Str);

/// `dependsOn` of `tryUnorderedPair`: `goal` is reachable from `start` (or is it).
fn dependsOn(edges: *const Edges, start: Str, goal: Str) bool {
    var visited: Set = .empty;
    var pending: List(Str) = .empty;
    pending.append(a, start) catch oom();
    while (pending.pop()) |id| {
        if (same(id, goal)) return true;
        if ((visited.getOrPut(a, idKey(id)) catch oom()).found_existing) continue;
        if (edges.get(idKey(id))) |deps| pending.appendSlice(a, deps) catch oom();
    }
    return false;
}

/// `GraphOrder.tryUnorderedEffects`: the first unordered observable pair of the graph's own nodes,
/// else of its nested graphs in order.
fn unorderedEffects(nodes: []const Value, observable: *const Set) ?[2]Str {
    var edges: Edges = .empty;
    var effectful: List(Str) = .empty;
    for (nodes) |node| {
        var deps: List(Str) = .empty;
        dependencies(node, &deps);
        edges.put(a, idKey(str(node, "id")), deps.items) catch oom();
        if (nodeObservable(node, observable)) effectful.append(a, str(node, "id")) catch oom();
    }
    // `tryUnorderedPair` without its fast path (a topological walk), which only skips this search
    // when every pair is ordered and so cannot change the pair it names.
    // shortcut: O(k²) reachability walks kept in the process arena, unlike F#'s ~k; port
    // `totallyOrdered` or cache one reach set per node if graphs with thousands of effects appear.
    std.mem.sort(Str, effectful.items, {}, lessStr);
    for (effectful.items, 0..) |first, i| for (effectful.items[i + 1 ..]) |second| {
        if (!dependsOn(&edges, first, second) and !dependsOn(&edges, second, first)) return .{ first, second };
    };
    for (nodes) |node| for (nestedScopes(node)) |scope| if (unorderedEffects(arrayOf(scope.graph, "nodes"), observable)) |pair| return pair;
    return null;
}

/// Functions that declare effects (`tryAbortOrderConflict`'s observable set, over the last function of an ID).
var declaring_functions: Set = .empty;

/// `GraphOrder.tryAbortOrderConflict` and its message, for a function with an Abort node.
fn effectOrderRules() void {
    const function = in_function.function;
    for (in_function.all_nodes) |node| {
        if (isName(str(node, "kind"), "abort")) break;
    } else return;
    const pair = unorderedEffects(arrayOf(get(function, "body"), "nodes"), &declaring_functions) orelse return;
    const nodes_by_id = byId(in_function.all_nodes);
    var described: [2]Str = undefined;
    for (pair, &described) |id, *d| {
        const node = nodes_by_id.get(idKey(id)).?;
        const kind = str(node, "kind");
        const callee = if (isName(kind, "directCall")) functions_by_id.get(idKey(str(node, "targetFunctionId"))) else null;
        d.* = if (callee) |f| cat(&.{ quote(id), L(" (DirectCall to '"), str(f, "displayName"), L("')") }) else cat(&.{ quote(id), L(" ("), caseNameOf(kind), L(")") });
    }
    addIn("AmbiguousEffectOrder", cat(&.{ L("Function "), functionLabel(function), L(" has unordered observable nodes "), quote(pair[0]), L(" and "), quote(pair[1]), L(", including local abort control flow. "), described[0], L(" and "), described[1], L(" each observe effects or abort, so their order is undefined; sequence them with bind so one runs first.") }));
}

/// `WaitPath`: the (node, function) steps, then the waiting node and its builtin kind or operation.
const WaitPath = struct { via: []const [2]Str, node_id: Str, builtin: ?Str, operation: Str = &.{} };

/// `Waiting.derive`: one path per function that may wait.
fn deriveWaiting(capabilities: []const Value, functions: []const Value) std.StringArrayHashMapUnmanaged(WaitPath) {
    var waiting_operations: Set = .empty;
    for (capabilities) |capability| for (arrayOf(capability, "operations")) |operation| {
        if (tryProperty("mayWait", operation) != null) waiting_operations.put(a, idKey(str(operation, "id")), {}) catch oom();
    };
    var found: std.StringArrayHashMapUnmanaged(WaitPath) = .empty;
    const Call = struct { node_id: Str, callee: Str };
    const invoked = a.alloc([]const Call, functions.len) catch oom();
    for (functions, invoked) |function, *calls| {
        var nodes: List(Value) = .empty;
        collectNodes(get(function, "body"), &nodes);
        for (nodes.items) |node| {
            const kind = str(node, "kind");
            const builtin_wait = isName(kind, "taskWait") or isName(kind, "oneShotWait") or isName(kind, "parallelAll") or isName(kind, "taskScope");
            const operation = invokedOperation(node);
            if (builtin_wait or (operation != null and waiting_operations.contains(idKey(operation.?)))) {
                const path: WaitPath = if (builtin_wait) .{ .via = &.{}, .node_id = str(node, "id"), .builtin = caseNameOf(kind) } else .{ .via = &.{}, .node_id = str(node, "id"), .builtin = null, .operation = operation.? };
                found.put(a, idKey(str(function, "id")), path) catch oom();
                break;
            }
        }
        var list: List(Call) = .empty;
        for (nodes.items) |node| for (functionReferences(node)) |reference| {
            if (isInvoked(reference.role)) list.append(a, .{ .node_id = str(node, "id"), .callee = reference.function_id }) catch oom();
        };
        calls.* = list.items;
    }
    // Each round adds the functions that run one found earlier, in this round too.
    var changed = true;
    while (changed) {
        changed = false;
        for (functions, invoked) |function, calls| {
            const id = idKey(str(function, "id"));
            if (found.contains(id)) continue;
            for (calls) |call| {
                const inner = found.get(idKey(call.callee)) orelse continue;
                var path = inner;
                path.via = std.mem.concat(a, [2]Str, &.{ &.{.{ call.node_id, call.callee }}, inner.via }) catch oom();
                found.put(a, id, path) catch oom();
                changed = true;
                break;
            }
        }
    }
    return found;
}

/// `Waiting.describe`: `helper (node 'n1') -> TaskWait (node 'n2')`, names by first match.
fn describeWait(capabilities: []const Value, functions: []const Value, path: WaitPath) Str {
    var out: List(u16) = .empty;
    for (path.via) |step| {
        const name = for (functions) |f| {
            if (same(str(f, "id"), step[1])) break str(f, "displayName");
        } else quote(step[1]);
        out.appendSlice(a, cat(&.{ name, L(" (node '"), step[0], L("') -> ") })) catch oom();
    }
    const source = path.builtin orelse for (capabilities) |capability| {
        const operation = for (arrayOf(capability, "operations")) |o| {
            if (same(str(o, "id"), path.operation)) break o;
        } else continue;
        break cat(&.{ L("waiting operation "), str(capability, "displayName"), L("."), str(operation, "displayName") });
    } else cat(&.{ L("waiting operation "), quote(path.operation) });
    out.appendSlice(a, cat(&.{ source, L(" (node '"), path.node_id, L("')") })) catch oom();
    return out.items;
}

/// The `MustNotWaitFunctionWaits` loop after the per-function one.
fn waitingRules(capabilities: []const Value, functions: []const Value) void {
    for (functions) |function| {
        if (tryProperty("mustNotWait", function) != null) break;
    } else return;
    const waiting = deriveWaiting(capabilities, functions);
    for (functions) |function| {
        if (tryProperty("mustNotWait", function) == null) continue;
        const path = waiting.get(idKey(str(function, "id"))) orelse continue;
        add("MustNotWaitFunctionWaits", cat(&.{ L("Function "), functionLabel(function), L(" must not wait but reaches "), describeWait(capabilities, functions, path), L(".") }));
    }
}

/// One function reference whose callee's type parameters follow from its arguments and iteration
/// values (`OneShotPayloadRequirements.instantiations`).
const Instantiation = struct { owner: Str, node_id: Str, reference: FunctionRef, substitution: Substitution };

/// The arguments of a node's operand of this builtin generic and arity, else none.
fn genericArgs(r: *Resolver, node: Value, name: []const u8, comptime generic: []const u8, arity: usize) []const TypeRef {
    const t = r.nodeType(str(node, name)) orelse return &.{};
    return if (t.isGeneric(generic) and t.args.len == arity) t.args else &.{};
}

/// The values a node passes its callee besides the context, by kind (the `iteration` of `instantiations`).
fn iterationTypes(r: *Resolver, node: Value) []const TypeRef {
    const kind = text(str(node, "kind"));
    var out: List(TypeRef) = .empty;
    if (std.mem.eql(u8, kind, "cancellationShield")) {
        out.append(a, primitiveType("unit")) catch oom();
    } else if (std.mem.eql(u8, kind, "taskScope")) {
        out.append(a, builtin("taskScope", &.{})) catch oom();
    } else if (std.mem.eql(u8, kind, "taskStart")) {
        if (r.nodeType(str(node, "itemNodeId"))) |t| out.append(a, t) catch oom();
    } else if (std.mem.eql(u8, kind, "useResource")) {
        const args = genericArgs(r, node, "acquireNodeId", "result", 2);
        if (args.len == 2) out.append(a, args[0]) catch oom();
    } else if (kindIs(kind, "listFold listScan listEffectFold mapFold setFold")) {
        if (r.nodeType(str(node, "seedNodeId"))) |t| out.append(a, t) catch oom();
        out.appendSlice(a, if (std.mem.eql(u8, kind, "mapFold")) genericArgs(r, node, "sourceMapNodeId", "map", 2) else if (std.mem.eql(u8, kind, "setFold")) genericArgs(r, node, "sourceSetNodeId", "set", 1) else genericArgs(r, node, "sourceListNodeId", "list", 1)) catch oom();
    } else if (kindIs(kind, "setFilter setMap")) {
        out.appendSlice(a, genericArgs(r, node, "sourceSetNodeId", "set", 1)) catch oom();
    } else if (kindIs(kind, "listSortBy listMinBy listMaxBy")) {
        for (0..2) |_| out.appendSlice(a, genericArgs(r, node, "sourceListNodeId", "list", 1)) catch oom();
    } else if (std.mem.eql(u8, kind, "listZipWith")) {
        out.appendSlice(a, genericArgs(r, node, "leftListNodeId", "list", 1)) catch oom();
        out.appendSlice(a, genericArgs(r, node, "rightListNodeId", "list", 1)) catch oom();
    } else if (kindIs(kind, "listFlatMap listMap mapFromList listFilter listAny listAll listFindIndex parallelAll")) {
        out.appendSlice(a, genericArgs(r, node, "sourceListNodeId", "list", 1)) catch oom();
    } else if (kindIs(kind, "mapFilter mapMapValues")) {
        out.appendSlice(a, genericArgs(r, node, "sourceMapNodeId", "map", 2)) catch oom();
    } else if (kindIs(kind, "optionMap optionAndThen")) {
        out.appendSlice(a, genericArgs(r, node, "sourceOptionNodeId", "option", 1)) catch oom();
    } else if (kindIs(kind, "resultMap resultAndThen resultMapErr")) {
        const args = genericArgs(r, node, "sourceResultNodeId", "result", 2);
        if (args.len == 2) out.append(a, if (std.mem.eql(u8, kind, "resultMapErr")) args[1] else args[0]) catch oom();
    }
    return out.items;
}

fn instantiations(functions: []const Value, resolvers: []Resolver) []const Instantiation {
    var out: List(Instantiation) = .empty;
    for (functions, resolvers) |function, *r| {
        var nodes: List(Value) = .empty;
        collectNodes(get(function, "body"), &nodes);
        for (nodes.items) |node| {
            const iteration = iterationTypes(r, node);
            for (functionReferences(node)) |reference| {
                const callee = functions_by_id.get(idKey(reference.function_id)) orelse continue;
                var pairs: List(Pair) = .empty;
                for (reference.arguments) |argument| {
                    const parameter = parameterOf(callee, str(argument, "targetParameterId"));
                    const actual = r.nodeType(str(argument, "valueNodeId"));
                    if (parameter != null and actual != null) pairs.append(a, .{ .declared = parameterType(parameter.?), .actual = actual.? }) catch oom();
                }
                const unbound = unboundTypes(callee, reference.arguments);
                const n = @min(unbound.len, iteration.len);
                for (unbound[0..n], iteration[0..n]) |declared, actual| pairs.append(a, .{ .declared = declared, .actual = actual }) catch oom();
                const substitution = matchAll(pairs.items) orelse continue;
                out.append(a, .{ .owner = str(function, "id"), .node_id = str(node, "id"), .reference = reference, .substitution = substitution }) catch oom();
            }
        }
    }
    return out.items;
}

/// Type parameters per function (the last function of an ID wins).
const Requirements = std.StringArrayHashMapUnmanaged(Set);

/// The monotone fixed point of both requirement analyses: a caller inherits the type parameters
/// its instantiations put where the callee's required ones were.
fn propagate(sets: *Requirements, calls: []const Instantiation) void {
    var changed = true;
    while (changed) {
        changed = false;
        for (calls) |call| {
            const callee = sets.get(idKey(call.reference.function_id)) orelse continue;
            const keys = a.dupe([]const u8, callee.keys()) catch oom();
            const owner = sets.getPtr(idKey(call.owner)).?;
            for (keys) |key| if (call.substitution.get(key)) |t| {
                var ids: Set = .empty;
                occurringIds(t, &ids);
                for (ids.keys()) |id| if (!(owner.getOrPut(a, id) catch oom()).found_existing) {
                    changed = true;
                };
            };
        }
    }
}

/// `requiredParameters`: the type parameters inside a OneShot payload.
fn oneShotParameters(t: TypeRef, out: *Set) void {
    if (t.isGeneric("oneShot")) {
        for (t.args) |arg| occurringIds(arg, out);
    } else if (t.kind == .generic or t.kind == .named or t.kind == .function) {
        for (typeArgs(t)) |arg| oneShotParameters(arg, out);
    }
}

/// `OneShotPayloadRequirements.derive`.
var one_shot_requirements: Requirements = .empty;

/// A required type parameter of `target` is instantiated with a type containing TaskScope (`violates`).
fn violates(target: Str, substitution: *const Substitution) bool {
    const required = one_shot_requirements.get(idKey(target)) orelse return false;
    for (required.keys()) |key| if (substitution.get(key)) |t| if (mentionsScope(t)) return true;
    return false;
}

var task_bound_capabilities: Set = .empty;

fn mentionsTaskBound(t: TypeRef) bool {
    if (t.kind == .capability and task_bound_capabilities.contains(idKey(t.name))) return true;
    for (typeArgs(t)) |arg| if (mentionsTaskBound(arg)) return true;
    return false;
}

/// `TaskTypes.bearingTaskBound`.
fn bearingTaskBound(t: TypeRef) bool {
    if (task_bound_capabilities.count() == 0) return false;
    var visited: Set = .empty;
    return bearingWith(&mentionsTaskBound, t, &visited);
}

/// `rejectTaskBound`.
fn rejectTaskBound(what: Str, t: ?TypeRef) void {
    if (t) |actual| if (bearingTaskBound(actual))
        addIn("TaskBoundValueCrossesTask", cat(&.{ what, L(" has a type that can hold a task-bound capability, whose values cannot cross into another task.") }));
}

/// `rejectTaskBound` on the item or source and each context binding of TaskStart and ParallelAll.
fn rejectTaskBoundInputs(name: Str, node: Value, input: Str, comptime input_label: []const u8) void {
    rejectTaskBound(nodeMessage(name, node, input_label), nodeType(input));
    for (arrayOf(node, "context")) |binding|
        rejectTaskBound(cat(&.{ nodeMessage(name, node, "context binding '"), str(binding, "targetParameterId"), L("'") }), nodeType(str(binding, "valueNodeId")));
}

/// The analyses `validate` runs before its per-function loop: task-crossing instantiations
/// (`TaskCrossingRequirements.invalidInstantiations`) and OneShot payloads in callbacks
/// (`OneShotPayloadRequirements.invalidCallbackUses`); they also set `one_shot_requirements`.
fn instantiationRules(functions: []const Value, resolvers: []Resolver) void {
    const calls = instantiations(functions, resolvers);
    one_shot_requirements = .empty;
    var crossing: Requirements = .empty;
    for (functions, resolvers) |function, *r| {
        var nodes: List(Value) = .empty;
        collectNodes(get(function, "body"), &nodes);
        var payload: Set = .empty;
        oneShotParameters(typeRefOf(get(function, "resultType")), &payload);
        for (arrayOf(function, "parameters")) |parameter| oneShotParameters(parameterType(parameter), &payload);
        var crosses: Set = .empty;
        for (nodes.items) |node| {
            const kind = str(node, "kind");
            if (isName(kind, "oneShotCreate")) {
                oneShotParameters(builtin("oneShot", &.{typeRefOf(get(node, "valueType"))}), &payload);
            } else {
                var types: List(TypeRef) = .empty;
                payloadTypeRefs(node, &types);
                for (types.items) |t| oneShotParameters(t, &payload);
            }
            // The node's own type carries the worker result.
            if (isName(kind, "taskStart") or isName(kind, "parallelAll")) {
                var ids: List(Str) = .empty;
                ids.append(a, str(node, "id")) catch oom();
                ids.append(a, str(node, if (isName(kind, "taskStart")) "itemNodeId" else "sourceListNodeId")) catch oom();
                for (arrayOf(node, "context")) |binding| ids.append(a, str(binding, "valueNodeId")) catch oom();
                for (ids.items) |id| if (r.nodeType(id)) |t| occurringIds(t, &crosses);
            }
        }
        one_shot_requirements.put(a, idKey(str(function, "id")), payload) catch oom();
        crossing.put(a, idKey(str(function, "id")), crosses) catch oom();
    }
    propagate(&one_shot_requirements, calls);
    propagate(&crossing, calls);

    var reported: Set = .empty;
    for (calls) |call| {
        const required = crossing.get(idKey(call.reference.function_id)) orelse continue;
        for (required.keys()) |key| if (call.substitution.get(key)) |t| if (bearingTaskBound(t)) {
            if (!(reported.getOrPut(a, idKey(cat(&.{ call.node_id, L("\n"), call.reference.function_id }))) catch oom()).found_existing)
                add("TaskBoundValueCrossesTask", cat(&.{ L("Node '"), call.node_id, L("' instantiates function '"), call.reference.function_id, L("' with a type that can hold a task-bound capability, and the function passes values of that type into another task, directly or through another generic call.") }));
            break;
        };
    }
    for (calls) |call| {
        if (call.reference.role != .direct_call and violates(call.reference.function_id, &call.substitution))
            add("OneShotScopePayloadUnsupported", cat(&.{ L("Callback node '"), call.node_id, L("' instantiates a OneShot payload with a type containing TaskScope.") }));
    }
}

fn validateFunctionGraphs(functions: []const Value) void {
    const resolvers = a.alloc(Resolver, functions.len) catch oom();
    for (functions, resolvers) |function, *r| r.* = Resolver.init(function);
    instantiationRules(functions, resolvers);
    declaring_functions = .empty;
    for (functions_by_id.values()) |function| {
        if (arrayOf(function, "requiredEffects").len > 0) declaring_functions.put(a, idKey(str(function, "id")), {}) catch oom();
    }
    for (functions, resolvers) |function, *resolver| {
        const label = describe(str(function, "displayName"), str(function, "id"));
        const body = get(function, "body");
        var nodes: List(Value) = .empty;
        collectNodes(body, &nodes);
        var required_effects: Set = .empty;
        for (arrayOf(function, "requiredEffects")) |effect| required_effects.put(a, idKey(effect.string), {}) catch oom();
        in_function = .{ .function = function, .prefix = inFunction(function), .resolver = resolver, .parameters_by_id = byId(arrayOf(function, "parameters")), .all_nodes = nodes.items, .required_effects = required_effects };
        for (nodes.items) |node| {
            var references: List(Str) = .empty;
            directValueReferences(node, &references);
            for (references.items) |reference| {
                if (resolver.completion(reference)) |c| if (c == .aborts)
                    addIn("AbortValueUse", cat(&.{ L("Node '"), str(node, "id"), L("' uses abrupt node '"), reference, L("' as a value in function "), label, L(".") }));
            }
        }
        validateGraph(&.empty, &.empty, cat(&.{ L("Function "), label, L(" body") }), body);
        effectOrderRules();
        if (resolver.nodeType(str(body, "resultNodeId"))) |t| if (!typeEql(t, typeRefOf(get(function, "resultType"))))
            addIn("FunctionResultTypeMismatch", cat(&.{ L("Function "), label, L(" result node type does not match its declared result type.") }));
    }
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
    functions_by_id = byId(functions);

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

    types_by_id = byId(types);
    all_types = types;
    capability_ids = idSet(capabilities);
    type_parameter_names = .empty;
    for ([_][]const Value{ types, functions }) |declared| for (declared) |owner| for (arrayOf(owner, "typeParameters")) |declaration|
        type_parameter_names.put(a, idKey(str(declaration, "typeParameterId")), str(declaration, "displayName")) catch oom();
    variant_owners = .empty;
    variants_by_id = .empty;
    for (types) |type_entity| for (arrayOf(type_entity, "variants")) |variant| {
        variant_owners.put(a, idKey(str(variant, "id")), str(type_entity, "id")) catch oom();
        variants_by_id.put(a, idKey(str(variant, "id")), variant) catch oom();
    };
    validateTypeReferences(types, operations.items, functions);

    all_capabilities = capabilities;
    operations_by_id = byId(operations.items);
    operation_owners = .empty;
    for (capabilities) |capability| for (arrayOf(capability, "operations")) |operation|
        operation_owners.put(a, idKey(str(operation, "id")), str(capability, "id")) catch oom();
    var product_fields: List(Value) = .empty;
    product_field_owners = .empty;
    for (types) |type_entity| if (isName(str(type_entity, "kind"), "product")) for (arrayOf(type_entity, "fields")) |field| {
        product_fields.append(a, field) catch oom();
        product_field_owners.put(a, idKey(str(field, "id")), type_entity) catch oom();
    };
    product_fields_by_id = byId(product_fields.items);
    open_obligation_pairs = .empty;
    for (open_pairs.items) |pair| open_obligation_pairs.put(a, idKey(pair), {}) catch oom();
    task_bound_capabilities = .empty;
    for (capabilities) |capability| {
        if (tryProperty("taskBound", capability) != null) task_bound_capabilities.put(a, idKey(str(capability, "id")), {}) catch oom();
    }
    validateFunctionGraphs(functions);
    waitingRules(capabilities, functions);

    std.mem.sort(Issue, issues.items, {}, issueLess);
}

/// One line per issue, then `end`; a message is escaped as the reader's errors are (`oneLine`).
fn printIssues(out: *std.Io.Writer) !void {
    for (issues.items) |issue| try out.print("issue {s}: {s}\n", .{ issue.code, oneLine(text(issue.message)) });
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
