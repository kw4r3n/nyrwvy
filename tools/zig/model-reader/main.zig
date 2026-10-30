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
//! `model-reader --typed-ir <in.json> <out.json> <entry|-> [...]`: per triple the same `error`/`skip`
//! line when the shape read fails, otherwise the typed semantic IR of the model (or of the
//! functions the entry reaches) written to `<out.json>` and `ok`, or `error InvalidSnapshot: <n>
//! issues`, `error IncompleteSnapshot: <ids>` or `error MissingNodeType: <function> <node>`.
//! `--execution-ir`, same arguments, writes the Execution IR instead (`ExecutionIr.lower`), or prints
//! `error <ExecutionIrError case>: <function> <ids>` when the lowering fails.
//!
//! `model_kinds.txt` beside this file is the node reader table, one kind per line:
//! `kind|context|name:shape ...|written names` (properties in reading order; the last field only
//! when the writer's order differs). `schema_minimums.txt` holds every construct row of
//! `SnapshotSchema.minimumVersions`, generated from it; a fast test compares it. The rows above the
//! first record version fail a document that declares less; all of them decide the version
//! `--fingerprints` writes.
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
pub const Str = []const u16;
pub const Member = struct { name: Str, value: Value };
pub const Value = union(enum) {
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
            if (c < 0x80 and c != '\\') {
                // A run of plain ASCII, appended at once.
                var end = p.i + 1;
                while (end < p.s.len and p.s[end] >= 0x20 and p.s[end] < 0x80 and p.s[end] != '"' and p.s[end] != '\\') end += 1;
                const run = units.addManyAsSlice(a, end - p.i) catch oom();
                for (run, p.s[p.i..end]) |*unit, byte| unit.* = byte;
                p.i = end;
                continue;
            }
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

/// False while only the shape of a document is read (`--validate`, `--execution-ir`): the reader's
/// checks run, but the canonical text it would write, and the sorting for it, are skipped.
var emit_text = true;

fn quoted(units: Str) []const u8 {
    if (!emit_text) return "";
    var out: List(u8) = .empty;
    out.append(a, '"') catch oom();
    for (units) |unit| escapeUnit(&out, unit);
    out.append(a, '"') catch oom();
    return out.items;
}

fn quotedAscii(name: []const u8) []const u8 {
    if (!emit_text) return "";
    return fmt("\"{s}\"", .{name});
}

/// A compact object; keys are ASCII names that need no escape.
const Obj = struct {
    buf: List(u8) = .empty,

    fn put(self: *Obj, key: []const u8, value: []const u8) void {
        if (!emit_text) return;
        self.buf.append(a, if (self.buf.items.len == 0) '{' else ',') catch oom();
        self.buf.print(a, "\"{s}\":{s}", .{ key, value }) catch oom();
    }

    fn putOptional(self: *Obj, key: []const u8, value: ?[]const u8) void {
        if (value) |v| self.put(key, v);
    }

    fn done(self: *Obj) []const u8 {
        if (!emit_text) return "";
        if (self.buf.items.len == 0) return "{}";
        self.buf.append(a, '}') catch oom();
        return self.buf.items;
    }
};

fn array(items: []const []const u8) []const u8 {
    if (!emit_text) return "";
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
    if (!emit_text) return "";
    std.mem.sort(Keyed, items, {}, keyedLess);
    const texts = a.alloc([]const u8, items.len) catch oom();
    for (items, texts) |item, *t| t.* = item.text;
    return array(texts);
}

fn plainArray(items: []const Keyed) []const u8 {
    if (!emit_text) return "";
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
// The rows `SnapshotSchema.usedFeatures` marks from something other than a node's kind alone.
var generic_row: ?usize = null;
var visibility_row: ?usize = null;
var declared_type_parameters_row: ?usize = null;
var timed_wait_row: ?usize = null;
var shared_arm_row: ?usize = null;
var exception_row: ?usize = null;
var one_shot_row: ?usize = null;
var map_row: ?usize = null;
var set_row: ?usize = null;

// The forms of `SnapshotCodec.serializeCanonical`, which the fingerprints hash (see `fingerprints`):
// the schema version written (null: the current one), the semantic fingerprint's normalization,
// and below version 21 the type parameter names written at each occurrence, by ID.
var write_version: ?i32 = null;
var neutral = false;
var legacy_names: std.StringHashMapUnmanaged(Str) = .empty;

/// Below the first record version (21, also the first with declared visibility and function type
/// parameter lists), the writer has no `exported` marker, omits a function's type parameter list
/// and names each type parameter occurrence.
fn legacyForm() bool {
    return if (write_version) |version| version < first_record else false;
}

fn visibility(exported: ?[]const u8) ?[]const u8 {
    if (exported == null) use(visibility_row);
    return if (legacyForm()) null else exported;
}

/// A display name, or `SemanticFingerprint`'s placeholder.
fn displayed(units: Str) Str {
    return if (neutral) L("_") else units;
}

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
        const id = try readId("typeParameterId", element);
        o.put("kind", "\"typeParameter\"");
        o.put("typeParameterId", quoted(id));
        // `TypeParameters.nameIn`: the ID text when no declaration names it.
        if (legacyForm()) o.put("displayName", quoted(legacy_names.get(text(id)) orelse id));
    } else if (isName(kind, "capability")) {
        try ensureProperties("Capability type reference", &.{ "kind", "capabilityId" }, &.{ "kind", "capabilityId" }, element);
        o.put("kind", "\"capability\"");
        o.put("capabilityId", quoted(try readId("capabilityId", element)));
    } else if (isName(kind, "builtinGeneric")) {
        try ensureProperties("Builtin generic type reference", &.{ "kind", "generic", "arguments" }, &.{ "kind", "generic", "arguments" }, element);
        const generic = try readString("generic", element);
        if (!oneOf(generic, &generics)) return invalidShape(fmt("Unsupported builtin generic type '{s}'.", .{text(generic)}));
        if (isName(generic, "exception")) use(exception_row);
        if (isName(generic, "oneShot")) use(one_shot_row);
        if (isName(generic, "map")) use(map_row);
        if (isName(generic, "set")) use(set_row);
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
    use(generic_row);
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
    o.put("displayName", quoted(displayed(display)));
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
    for (items, texts, 0..) |item, *t, index| {
        try ensureProperties("Type parameter declaration", &.{ "typeParameterId", "displayName" }, &.{ "typeParameterId", "displayName" }, item);
        var o: Obj = .{};
        o.put("typeParameterId", quoted(try readId("typeParameterId", item)));
        const display = try readString("displayName", item);
        o.put("displayName", quoted(if (neutral) positionalName(index) else display));
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
        const special = [_]struct { []const u8, *?usize }{
            .{ "GenericTypes", &generic_row },                .{ "DeclarationVisibility", &visibility_row },
            .{ "DeclaredFunctionTypeParameters", &declared_type_parameters_row }, .{ "TimedTaskWait", &timed_wait_row },
            .{ "SharedUnionMatchArm", &shared_arm_row },      .{ "ExceptionBoundary", &exception_row },
            .{ "OneShot", &one_shot_row },                    .{ "Map", &map_row },
            .{ "Set", &set_row },
        };
        for (special) |entry| if (std.mem.eql(u8, feature, entry[0])) {
            entry[1].* = rows.items.len - 1;
        };
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
    if (display) |d| if (!neutral) o.put("displayName", quoted(d));
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
        use(shared_arm_row);
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
    if (std.mem.eql(u8, kind_name, "taskWait") and tryProperty("limitNodeId", element) != null) use(timed_wait_row);
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
            v.put("displayName", quoted(displayed(variant_name)));
            v.put("fields", sortedArray(fields));
            variant.* = .{ .key = variant_id, .text = v.done() };
        }
    }
    if (type_parameters != null) use(generic_row);
    var o: Obj = .{};
    o.put("id", quoted(id));
    o.put("displayName", quoted(displayed(display)));
    o.putOptional("exported", visibility(exported));
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
    o.put("displayName", quoted(displayed(try readString("displayName", element))));
    o.putOptional("exported", visibility(try readVisibility(element)));
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
    o.put("displayName", quoted(displayed(display)));
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
    o.put("displayName", quoted(displayed(display)));
    o.putOptional("exported", visibility(exported));
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
    if (!try derivedTypeParameters(element)) use(declared_type_parameters_row);
    var o: Obj = .{};
    o.put("id", quoted(id));
    o.put("displayName", quoted(displayed(display)));
    o.putOptional("exported", visibility(exported));
    o.putOptional("mustNotWait", must_not_wait);
    if (!legacyForm()) o.putOptional("typeParameters", type_parameters);
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

/// `SemanticFingerprint.normalizeTypeParameters`: `_`, then `_1`, `_2`, ... by position.
fn positionalName(index: usize) Str {
    if (index == 0) return L("_");
    const ascii = fmt("_{d}", .{index});
    const units = a.alloc(u16, ascii.len) catch oom();
    for (ascii, units) |c, *u| u.* = c;
    return units;
}

/// `TypeParameters.occurring`: the type parameters of a type reference in first-occurrence order.
fn occurringInOrder(type_ref: Value, out: *List(Str)) Failure!void {
    const kind = str(type_ref, "kind");
    if (isName(kind, "typeParameter")) {
        const id = try decodeString(str(type_ref, "typeParameterId"));
        for (out.items) |seen| if (std.mem.eql(u16, seen, id)) return;
        out.append(a, id) catch oom();
    } else if (isName(kind, "function")) {
        for (arrayOf(type_ref, "parameters")) |item| try occurringInOrder(item, out);
        try occurringInOrder(get(type_ref, "resultType"), out);
    } else for (arrayOf(type_ref, "arguments")) |item| try occurringInOrder(item, out);
}

/// `TypeParameters.isDerived`: the declared list is the one the parameter types imply.
fn derivedTypeParameters(function: Value) Failure!bool {
    var implied: List(Str) = .empty;
    for (arrayOf(function, "parameters")) |parameter| try occurringInOrder(get(parameter, "type"), &implied);
    const declared = arrayOf(function, "typeParameters");
    if (declared.len != implied.items.len) return false;
    for (declared, implied.items) |item, id| if (!std.mem.eql(u16, try decodeString(str(item, "typeParameterId")), id)) return false;
    return true;
}

/// `TypeParameters.names` (the declared names by ID, the last declaration winning), or under the
/// normalization each declaration's positional names.
fn collectTypeParameterNames(root: Value) Failure!void {
    legacy_names = .empty;
    for ([_][]const u8{ "types", "functions" }) |group| for (arrayOf(root, group)) |declaration| {
        for (arrayOf(declaration, "typeParameters"), 0..) |item, index| {
            const name = if (neutral) positionalName(index) else try decodeString(str(item, "displayName"));
            legacy_names.put(a, text(try decodeString(str(item, "typeParameterId"))), name) catch oom();
        }
    };
}

/// `SemanticFingerprint.normalizeObligations`: sorted by call node, parameter and status, then
/// numbered, with the transaction IDs neutral.
fn neutralObligations(root: Value) Failure![]const u8 {
    const Entry = struct { call: Str, parameter: Str, status: Str, value: ?Str };
    const items = arrayOf(root, "obligations");
    const entries = a.alloc(Entry, items.len) catch oom();
    for (items, entries) |item, *entry| {
        const resolved = isName(str(item, "status"), "resolved");
        entry.* = .{
            .call = try decodeString(str(item, "callNodeId")),
            .parameter = try decodeString(str(item, "parameterId")),
            .status = if (resolved) cat(&.{ L("resolved:"), try decodeString(str(item, "resolutionValueNodeId")) }) else L("open"),
            .value = if (resolved) try decodeString(str(item, "resolutionValueNodeId")) else null,
        };
    }
    std.mem.sort(Entry, entries, {}, struct {
        fn less(_: void, x: Entry, y: Entry) bool {
            for ([_][2]Str{ .{ x.call, y.call }, .{ x.parameter, y.parameter }, .{ x.status, y.status } }) |pair| switch (std.mem.order(u16, pair[0], pair[1])) {
                .lt => return true,
                .gt => return false,
                .eq => {},
            };
            return false;
        }
    }.less);
    const transaction = quoted(L("semantic-fingerprint-transaction"));
    const out = a.alloc(Keyed, entries.len) catch oom();
    for (entries, out, 0..) |entry, *o, index| {
        const id = quotedAscii(fmt("semantic-fingerprint-obligation-{d:0>8}", .{index}));
        var obj: Obj = .{};
        obj.put("id", id);
        obj.put("kind", "\"missingArgument\"");
        obj.put("originTransactionId", transaction);
        obj.put("callNodeId", quoted(entry.call));
        obj.put("parameterId", quoted(entry.parameter));
        if (entry.value) |value| {
            obj.put("status", "\"resolved\"");
            obj.put("resolutionTransactionId", transaction);
            obj.put("resolutionValueNodeId", quoted(value));
        } else obj.put("status", "\"open\"");
        o.* = .{ .key = L("-"), .text = obj.done() };
    }
    return plainArray(out);
}

pub const Fingerprints = struct { snapshot: []const u8, semantic: []const u8, canonical: []const u8, normalized: []const u8 };

/// `SnapshotFingerprint` and `SemanticFingerprint` of a valid snapshot document: SHA-256 over the
/// domain, a NUL and `SnapshotCodec.serializeCanonical`'s text, which is written at the smallest
/// version holding every construct the snapshot uses (`SnapshotSchema.required`).
// shortcut: neither the snapshot nor its normalization is validated, as `serializeCanonical` does;
// the caller passes a validated snapshot (the host fingerprints only what it opened), and no
// normalization of a valid snapshot has been seen to fail. Revisit if the parity test ever differs.
fn fingerprints(source: []const u8) Failure!Fingerprints {
    return fingerprintsOf(try parseDocument(source));
}

fn fingerprintsOf(root: Value) Failure!Fingerprints {
    defer {
        write_version = null;
        neutral = false;
    }
    _ = try canonicalOf(root);
    var required = first_supported;
    for (rows.items, used) |row, is_used| if (is_used and row.minimum > required) {
        required = row.minimum;
    };
    write_version = required;
    try collectTypeParameterNames(root);
    const canonical = try canonicalOf(root);
    neutral = true;
    try collectTypeParameterNames(root);
    const normalized = try canonicalOf(root);
    return .{ .snapshot = hashIn("nayamnyam.snapshot-fingerprint.v6", canonical), .semantic = hashIn("nayamnyam.semantic-fingerprint.v6", normalized), .canonical = canonical, .normalized = normalized };
}

/// One snapshot document as the session host reads it: what `--validate` prints, line by line,
/// and on acceptance its two fingerprints. `allocator` holds the result.
pub const DocumentRead = struct {
    lines: []const u8,
    validation_issues: []const Issue = &.{},
    fingerprints: ?Fingerprints = null,
    /// The model read (for a resolved document, the merged one), in document order.
    root: Value = .json_null,
    /// The current-version canonical text and `SnapshotCodec.dependencyDigest`, for a document
    /// accepted as a dependency.
    canonical: []const u8 = "",
    digest: []const u8 = "",
    /// Its snapshot ID, whether an obligation is open (`ensureCompleteDependency`), and the subjects a
    /// resolved local's records may name (`RecordCodec.dependencySubjects`, `domain:uuid`).
    id: []const u8 = "",
    open_obligations: bool = false,
    subjects: []const []const u8 = &.{},
};

/// `RecordCodec.dependencySubjects`: the declarations and their members.
fn subjectsOf(root: Value) []const []const u8 {
    var out: List([]const u8) = .empty;
    const subject = struct {
        fn subject(list: *List([]const u8), domain: []const u8, item: Value, key: []const u8) void {
            list.append(a, fmt("{s}:{s}", .{ domain, text(str(item, key)) })) catch oom();
        }
    }.subject;
    for (arrayOf(root, "types")) |t| {
        subject(&out, "type", t, "id");
        for (arrayOf(t, "typeParameters")) |p| subject(&out, "type-parameter", p, "typeParameterId");
        for (arrayOf(t, "fields")) |f| subject(&out, "field", f, "id");
        for (arrayOf(t, "variants")) |v| {
            subject(&out, "variant", v, "id");
            for (arrayOf(v, "fields")) |f| subject(&out, "field", f, "id");
        }
    }
    for (arrayOf(root, "effects")) |e| subject(&out, "effect", e, "id");
    for (arrayOf(root, "capabilities")) |c| {
        subject(&out, "capability", c, "id");
        for (arrayOf(c, "operations")) |o| {
            subject(&out, "operation", o, "id");
            for (arrayOf(o, "parameters")) |p| subject(&out, "capability-parameter", p, "id");
        }
    }
    for (arrayOf(root, "functions")) |f| {
        subject(&out, "function", f, "id");
        for (arrayOf(f, "typeParameters")) |p| subject(&out, "type-parameter", p, "typeParameterId");
        for (arrayOf(f, "parameters")) |p| subject(&out, "parameter", p, "id");
    }
    return out.items;
}

/// `DependencyGraph.combine` over current-version canonical texts: every declaration once, in
/// first-occurrence order, when its content (its canonical text) is the same wherever it occurs.
pub const Combined = union(enum) {
    text: []const u8,
    /// The first identity with different content, and the display name each member gives it.
    conflict: struct { kind: []const u8, id: []const u8, names: []const ?[]const u8 },
};

pub fn combine(allocator: std.mem.Allocator, members: []const []const u8, combined_id: []const u8) Combined {
    a = allocator;
    resetState();
    const roots = a.alloc(Value, members.len) catch oom();
    for (members, roots) |member, *root| root.* = parseDocument(member) catch unreachable;
    var out: List(u8) = .empty;
    out.print(a, "{{\"schemaVersion\":{d},\"snapshotId\":{s}", .{ current, quotedAscii(combined_id) }) catch oom();
    const groups = [_]struct { []const u8, []const u8, *const fn (Value) Failure!Keyed }{
        .{ "types", "type", readType },             .{ "effects", "effect", readEffect },
        .{ "capabilities", "capability", readCapability }, .{ "functions", "function", readFunction },
        .{ "obligations", "obligation", readObligation },
    };
    for (groups) |group| {
        var seen: std.StringArrayHashMapUnmanaged([]const u8) = .empty;
        for (roots) |root| for (arrayOf(root, group[0])) |item| {
            const keyed = group[2](item) catch unreachable;
            const id = text(keyed.key);
            if (seen.get(id)) |existing| {
                if (std.mem.eql(u8, existing, keyed.text)) continue;
                const names = a.alloc(?[]const u8, roots.len) catch oom();
                for (roots, names) |other, *name| {
                    name.* = null;
                    for (arrayOf(other, group[0])) |candidate| if (std.mem.eql(u8, text(str(candidate, "id")), id)) {
                        name.* = if (tryProperty("displayName", candidate) != null) text(str(candidate, "displayName")) else null;
                    };
                }
                return .{ .conflict = .{ .kind = group[1], .id = id, .names = names } };
            }
            seen.put(a, id, keyed.text) catch oom();
        };
        out.print(a, ",\"{s}\":[", .{group[0]}) catch oom();
        for (seen.values(), 0..) |item, index| {
            if (index > 0) out.append(a, ',') catch oom();
            out.appendSlice(a, item) catch oom();
        }
        out.append(a, ']') catch oom();
    }
    out.append(a, '}') catch oom();
    return .{ .text = out.items };
}
pub fn readDocument(allocator: std.mem.Allocator, source: []const u8) DocumentRead {
    a = allocator;
    resetState();
    var out: std.Io.Writer.Allocating = .init(a);
    emit_text = false;
    const read = canonicalJson(source);
    emit_text = true;
    if (read) |_| {
        const root = parsed_root;
        validate(root);
        printIssues(&out.writer) catch oom();
        if (issues.items.len > 0) return .{ .lines = out.written(), .validation_issues = issues.items };
        const found = fingerprintsOf(root) catch return .{ .lines = out.written() };
        var open = false;
        for (arrayOf(root, "obligations")) |obligation| open = open or isName(str(obligation, "status"), "open");
        return .{
            .lines = out.written(),
            .fingerprints = found,
            .root = root,
            .canonical = canonicalOf(root) catch "",
            .digest = fmt("{s}", .{std.fmt.bytesToHex(sha256(found.canonical), .lower)}),
            .id = text(decodeString(str(root, "snapshotId")) catch unreachable),
            .open_obligations = open,
            .subjects = subjectsOf(root),
        };
    } else |e| printFailure(&out.writer, e);
    return .{ .lines = out.written() };
}

fn sha256(bytes: []const u8) [32]u8 {
    var digest: [32]u8 = undefined;
    std.crypto.hash.sha2.Sha256.hash(bytes, &digest, .{});
    return digest;
}

fn printFailure(out: *std.Io.Writer, e: Failure) void {
    switch (e) {
        error.Json => out.print("error InvalidJson: {s}\n", .{message}) catch oom(),
        error.Shape => out.print("error InvalidShape: {s}\n", .{message}) catch oom(),
        error.Version => out.print("error UnsupportedSchemaVersion: {d}\n", .{declared_version}) catch oom(),
        error.Legacy => out.print("skip legacy schema {d}\n", .{declared_version}) catch oom(),
    }
}

/// A resolved document as the session host reads it (`SnapshotCodec.deserializeResolved`): the
/// local document with its marker checked against the dependency's ID and digest, then the merged
/// model, the dependency's declarations first, validated and fingerprinted. `dependency` is the
/// dependency's current-version canonical text (`DocumentRead.canonical`).
pub fn readResolvedDocument(allocator: std.mem.Allocator, dependency: []const u8, dependency_id: []const u8, dependency_digest: []const u8, source: []const u8) DocumentRead {
    a = allocator;
    resetState();
    var out: std.Io.Writer.Allocating = .init(a);
    const dependency_root = parseDocument(dependency) catch unreachable;
    resolved_input = true;
    expected_marker = .{ .id = utf16Of(dependency_id), .digest = utf16Of(dependency_digest) };
    emit_text = false;
    const read = canonicalJson(source);
    emit_text = true;
    resolved_input = false;
    expected_marker = null;
    _ = read catch |e| {
        printFailure(&out.writer, e);
        return .{ .lines = out.written() };
    };
    var members: List(Member) = .empty;
    for (parsed_root.object) |member| {
        if (isName(member.name, "dependency")) continue;
        var value = member.value;
        inline for (.{ "types", "effects", "capabilities", "functions", "obligations" }) |name| {
            if (isName(member.name, name)) value = .{ .array = std.mem.concat(a, Value, &.{ arrayOf(dependency_root, name), member.value.array }) catch oom() };
        }
        // The merged model is not a document: only the local one's version bounds its constructs,
        // and the dependency's text is at the current version.
        if (isName(member.name, "schemaVersion")) value = .{ .number = fmt("{d}", .{current}) };
        members.append(a, .{ .name = member.name, .value = value }) catch oom();
    }
    const merged: Value = .{ .object = members.items };
    const id = text(decodeString(str(parsed_root, "snapshotId")) catch unreachable);
    validate(merged);
    printIssues(&out.writer) catch oom();
    if (issues.items.len > 0) return .{ .lines = out.written() };
    return .{ .lines = out.written(), .fingerprints = fingerprintsOf(merged) catch null, .id = id, .root = merged };
}

/// ASCII or UTF-8 text as UTF-16 units.
fn utf16Of(bytes: []const u8) Str {
    return std.unicode.utf8ToUtf16LeAlloc(a, bytes) catch oom();
}

// ---------------------------------------------------------------- for the session host

/// The typed session generation and its writer.
pub const session_model = @import("typed.zig");

/// Parse a wire/model value without resetting the snapshot reader's tables.
pub fn parseValue(allocator: std.mem.Allocator, source: []const u8) Failure!Value {
    a = allocator;
    return parseDocument(source);
}

/// A model in the writer's canonical order (`SnapshotCodec.serialize`): declarations and their
/// members sorted by ID, as the host's encoders list them. `allocator` holds the result.
pub fn canonicalRoot(allocator: std.mem.Allocator, root: Value) Value {
    a = allocator;
    emit_text = true;
    return parseDocument(canonicalOf(root) catch unreachable) catch unreachable;
}

/// A string property's value (the parse keeps escapes).
pub fn decoded(value: Value) Str {
    return decodeString(value.string) catch unreachable;
}

pub fn property(element: Value, name: []const u8) ?Value {
    return tryProperty(name, element);
}

pub fn arrayItems(element: Value, name: []const u8) []const Value {
    return arrayOf(element, name);
}

/// Makes `allocator` (a request's arena) hold what the reader's helpers allocate from now on.
/// Derived reader state is request-local. Discard it before changing arenas; in particular no
/// table from a failed proposal may be consulted by a later query of the committed snapshot.
pub fn beginRequest(allocator: std.mem.Allocator) void {
    a = allocator;
    resetState();
}

pub fn useAllocator(allocator: std.mem.Allocator) void {
    a = allocator;
}

pub fn utf8(units: Str) []const u8 {
    return text(units);
}

/// `ExecutionIr.lower` of a model already validated (the open model), as the serializer's JSON;
/// null when it does not lower (`ProjectExecution` fails).
pub fn executionIrOf(allocator: std.mem.Allocator, root: Value) ?[]const u8 {
    a = allocator;
    const was = validated_input;
    validated_input = true;
    defer validated_input = was;
    return executionIr(root) catch null;
}

/// The functions a node names (`SemanticNodeShape.functionReferences`), decoded, in payload order,
/// each with whether it is invoked (`FunctionReferenceRole.isInvoked`).
pub const FunctionRelation = struct { id: Str, invoked: bool };
pub fn functionRelationsOf(node: Value) []const FunctionRelation {
    const references = functionReferences(node);
    const out = a.alloc(FunctionRelation, references.len) catch oom();
    for (references, out) |reference, *relation| relation.* = .{ .id = dec(reference.function_id), .invoked = isInvoked(reference.role) };
    return out;
}

/// `ProjectExecution` of a model already validated (the open model): the number of functions it
/// lowers, or the case name of the projection's or the lowering's error.
pub const Readiness = union(enum) { ready: usize, projection: []const u8, lowering: []const u8 };
pub fn executionReadinessOf(allocator: std.mem.Allocator, root: Value) Readiness {
    a = allocator;
    const was = validated_input;
    validated_input = true;
    defer validated_input = was;
    const case = struct {
        fn of(failure: []const u8) []const u8 {
            return failure[0 .. std.mem.indexOfScalar(u8, failure, ':') orelse failure.len];
        }
    }.of;
    _ = executionIr(root) catch |e| return switch (e) {
        error.Lowering => .{ .lowering = case(lowering_failure) },
        else => .{ .projection = case(lowering_failure) },
    };
    return .{ .ready = arrayOf(root, "functions").len };
}

/// `SemanticNodeShape.nestedScopes` of a node.
pub fn scopesOf(node: Value) []const Scope {
    return nestedScopes(node);
}

/// `SemanticNodeShape.directValueReferences` of a node, decoded, in operand order (`operandsOf` keeps them raw).
pub fn operandIdsOf(node: Value) []const Str {
    var raw: List(Str) = .empty;
    directValueReferences(node, &raw);
    for (raw.items) |*id| id.* = dec(id.*);
    return raw.items;
}

/// The IDs `SemanticNodeShape.functionReferences` names, decoded, in payload order.
pub fn functionIdsOf(node: Value) []const Str {
    const references = functionReferences(node);
    const ids = a.alloc(Str, references.len) catch oom();
    for (references, ids) |reference, *id| id.* = dec(reference.function_id);
    return ids;
}

/// A JSON string as the F# writers escape it (`escapeUnit`), straight to `out`.
pub fn writeString(out: *std.Io.Writer, units: Str) !void {
    try out.writeByte('"');
    for (units) |unit| switch (unit) {
        8 => try out.writeAll("\\b"),
        9 => try out.writeAll("\\t"),
        10 => try out.writeAll("\\n"),
        12 => try out.writeAll("\\f"),
        13 => try out.writeAll("\\r"),
        '\\' => try out.writeAll("\\\\"),
        0x20...0x21, 0x23...0x25, 0x28...0x2A, 0x2C...0x3B, 0x3D, 0x3F...0x5B, 0x5D...0x5F, 0x61...0x7E => try out.writeByte(@intCast(unit)),
        else => try out.print("\\u{X:0>4}", .{unit}),
    };
    try out.writeByte('"');
}

/// The `kind:id` keys of a model's functions, types, effects and capabilities, and those of the
/// ones it does not export (`PinnedDependency.visible`).
pub fn declarationKeys(allocator: std.mem.Allocator, root: Value) struct { all: []const []const u8, hidden: []const []const u8 } {
    a = allocator;
    var all: List([]const u8) = .empty;
    var hidden: List([]const u8) = .empty;
    for ([_][2][]const u8{ .{ "functions", "function" }, .{ "types", "type" }, .{ "effects", "effect" }, .{ "capabilities", "capability" } }) |group| {
        for (arrayOf(root, group[0])) |item| {
            const key = fmt("{s}:{s}", .{ group[1], text(decoded(get(item, "id"))) });
            all.append(a, key) catch oom();
            if (tryProperty("exported", item) == null) hidden.append(a, key) catch oom();
        }
    }
    return .{ .all = all.items, .hidden = hidden.items };
}

/// One site of `SemanticQuery.references`. `a`/`b` are the case's IDs in field order (FieldType's
/// optional variant is `a`), `role` a node's role; `ReferenceSite`'s structural order is
/// tag, then `a` (none first), `b` and `role`, each ordinal.
pub const Site = struct { tag: SiteTag, a: ?Str = null, b: Str = &.{}, role: []const u8 = "" };
pub const SiteTag = enum(u8) { parameter_type, result_type, required_effect, field_type, operation_parameter_type, operation_result_type, operation_failure_type, operation_effect, node };
pub const OwnerKind = enum(u8) { function, type, capability };
pub const Reference = struct { owner: OwnerKind, owner_id: Str, site: Site };

fn sameId(raw: Str, id: Str) bool {
    return std.mem.eql(u16, decodeString(raw) catch unreachable, id);
}

fn memberIds(items: []const Value, comptime name: []const u8) []const Str {
    var out: List(Str) = .empty;
    for (items) |item| for (arrayOf(item, name)) |member| out.append(a, decoded(get(member, "id"))) catch oom();
    return out.items;
}

fn containsId(ids: []const Str, raw: Str) bool {
    for (ids) |id| if (sameId(raw, id)) return true;
    return false;
}

fn typeMentions(t: TypeRef, kind: TypeKind, id: Str) bool {
    if (t.kind == kind and sameId(t.name, id)) return true;
    for (t.args) |arg| if (typeMentions(arg, kind, id)) return true;
    if (t.result) |result| return typeMentions(result.*, kind, id);
    return false;
}

fn orderOptional(x: ?Str, y: ?Str) std.math.Order {
    if (x == null) return if (y == null) .eq else .lt;
    if (y == null) return .gt;
    return std.mem.order(u16, x.?, y.?);
}

fn referenceLess(_: void, x: Reference, y: Reference) bool {
    if (x.owner != y.owner) return @intFromEnum(x.owner) < @intFromEnum(y.owner);
    var o = std.mem.order(u16, x.owner_id, y.owner_id);
    if (o == .eq) o = std.math.order(@intFromEnum(x.site.tag), @intFromEnum(y.site.tag));
    if (o == .eq) o = orderOptional(x.site.a, y.site.a);
    if (o == .eq) o = std.mem.order(u16, x.site.b, y.site.b);
    if (o == .eq) o = std.mem.order(u8, x.site.role, y.site.role);
    return o == .lt;
}

/// `SemanticQuery.references` over the model's tree: every site referring to the `kind` subject
/// with ID `id`, or null when no such subject is declared (`SubjectNotFound`).
pub fn referencesTo(allocator: std.mem.Allocator, root: Value, kind: []const u8, id: Str) ?[]const Reference {
    a = allocator;
    const types = arrayOf(root, "types");
    const capabilities = arrayOf(root, "capabilities");
    const is = struct {
        fn f(k: []const u8, comptime name: []const u8) bool {
            return std.mem.eql(u8, k, name);
        }
    }.f;
    // The variants, fields and operations whose use refers to the subject.
    var variants: []const Str = &.{};
    var fields: []const Str = &.{};
    var operations: []const Str = &.{};
    var declared = false;
    if (is(kind, "function") or is(kind, "type") or is(kind, "effect") or is(kind, "capability")) {
        const group = if (is(kind, "function")) "functions" else if (is(kind, "type")) "types" else if (is(kind, "effect")) "effects" else "capabilities";
        for (arrayOf(root, group)) |item| if (sameId(str(item, "id"), id)) {
            declared = true;
            if (is(kind, "type")) {
                variants = memberIds(&.{item}, "variants");
                var all: List(Str) = .empty;
                for (arrayOf(item, "fields")) |field| all.append(a, decoded(get(field, "id"))) catch oom();
                all.appendSlice(a, memberIds(arrayOf(item, "variants"), "fields")) catch oom();
                fields = all.items;
            } else if (is(kind, "capability")) operations = memberIds(&.{item}, "operations");
        };
    } else if (is(kind, "variant")) {
        for (types) |item| for (arrayOf(item, "variants")) |variant| if (sameId(str(variant, "id"), id)) {
            declared = true;
        };
        variants = a.dupe(Str, &.{id}) catch oom();
    } else if (is(kind, "field")) {
        for (types) |item| {
            for (arrayOf(item, "fields")) |field| if (sameId(str(field, "id"), id)) {
                declared = true;
            };
            for (arrayOf(item, "variants")) |variant| for (arrayOf(variant, "fields")) |field| if (sameId(str(field, "id"), id)) {
                declared = true;
            };
        }
        fields = a.dupe(Str, &.{id}) catch oom();
    } else if (is(kind, "capabilityOperation")) {
        for (capabilities) |item| for (arrayOf(item, "operations")) |operation| if (sameId(str(operation, "id"), id)) {
            declared = true;
        };
        operations = a.dupe(Str, &.{id}) catch oom();
    }
    if (!declared) return null;
    const mention_kind: ?TypeKind = if (is(kind, "type")) .named else if (is(kind, "capability")) .capability else null;
    const mentions = struct {
        fn f(mk: ?TypeKind, subject: Str, t: Value) bool {
            return if (mk) |k| typeMentions(typeRefOf(t), k, subject) else false;
        }
    }.f;
    const effect = is(kind, "effect");
    var out: List(Reference) = .empty;
    for (arrayOf(root, "functions")) |function| {
        const owner = decoded(get(function, "id"));
        const addSite = struct {
            fn f(list: *List(Reference), o: Str, site: Site) void {
                list.append(a, .{ .owner = .function, .owner_id = o, .site = site }) catch oom();
            }
        }.f;
        for (arrayOf(function, "parameters")) |parameter|
            if (mentions(mention_kind, id, get(parameter, "type"))) addSite(&out, owner, .{ .tag = .parameter_type, .a = decoded(get(parameter, "id")) });
        if (mentions(mention_kind, id, get(function, "resultType"))) addSite(&out, owner, .{ .tag = .result_type });
        if (effect) for (arrayOf(function, "requiredEffects")) |required| if (sameId(required.string, id)) {
            addSite(&out, owner, .{ .tag = .required_effect });
            break;
        };
        var nodes: List(Value) = .empty;
        collectNodes(get(function, "body"), &nodes);
        for (nodes.items) |node| {
            var roles: List([]const u8) = .empty;
            if (is(kind, "function")) for (functionReferences(node)) |reference| if (sameId(reference.function_id, id)) roles.append(a, switch (reference.role) {
                .direct_call => "directCall",
                .pure_callback, .effect_callback => "callback",
                .worker => "worker",
                .body => "body",
                .function_value => "functionValue",
                .implementation => "implementation",
            }) catch oom();
            var payload: List(TypeRef) = .empty;
            payloadTypeRefs(node, &payload);
            if (mention_kind) |k| for (payload.items) |t| if (typeMentions(t, k, id)) {
                roles.append(a, "typeArgument") catch oom();
                break;
            };
            const node_kind = str(node, "kind");
            const binds = struct {
                fn f(n: Value, ids: []const Str) bool {
                    for (arrayOf(n, "fields")) |binding| if (containsId(ids, str(binding, "targetFieldId"))) return true;
                    return false;
                }
            }.f;
            const constructs = is(kind, "type") and tryProperty("typeId", node) != null and sameId(str(node, "typeId"), id);
            if (isName(node_kind, "productConstruct")) {
                if (constructs or binds(node, fields)) roles.append(a, "construct") catch oom();
            } else if (isName(node_kind, "unionConstruct")) {
                if (containsId(variants, str(node, "variantId")) or binds(node, fields)) roles.append(a, "construct") catch oom();
            } else if (isName(node_kind, "productUpdate")) {
                if (constructs or binds(node, fields)) roles.append(a, "productUpdate") catch oom();
            } else if (isName(node_kind, "fieldAccess")) {
                if (containsId(fields, str(node, "fieldId"))) roles.append(a, "fieldAccess") catch oom();
            } else if (isName(node_kind, "matchUnion")) {
                var matched = false;
                var bound = false;
                for (arrayOf(node, "arms")) |arm| {
                    if (tryProperty("variantId", arm)) |single| matched = matched or containsId(variants, single.string);
                    for (arrayOf(arm, "variantIds")) |variant| matched = matched or containsId(variants, variant.string);
                    for (arrayOf(arm, "fieldBindings")) |binding| bound = bound or containsId(fields, str(binding, "sourceFieldId"));
                }
                if (matched) roles.append(a, "match") catch oom();
                if (bound) roles.append(a, "patternBinding") catch oom();
            } else if (isName(node_kind, "capabilityConstruct")) {
                if (is(kind, "capability") and sameId(str(node, "capabilityId"), id)) roles.append(a, "capabilityConstruct") catch oom();
                for (arrayOf(node, "implementations")) |implementation| if (containsId(operations, str(implementation, "operationId"))) {
                    roles.append(a, "implementation") catch oom();
                    break;
                };
            } else if (isName(node_kind, "capabilityInvoke")) {
                if (containsId(operations, str(node, "operationId"))) roles.append(a, "invoke") catch oom();
            } else if (isName(node_kind, "useResource")) {
                if (containsId(operations, str(node, "releaseOperationId"))) roles.append(a, "release") catch oom();
            }
            if (mention_kind != null) {
                var bound = false;
                for (nestedScopes(node)) |scope| for (scope.bindings) |binding| {
                    bound = bound or mentions(mention_kind, id, get(binding, "type"));
                };
                if (bound) roles.append(a, "patternBinding") catch oom();
            }
            const node_id = decoded(get(node, "id"));
            for (roles.items, 0..) |role, index| {
                const seen = for (roles.items[0..index]) |earlier| {
                    if (std.mem.eql(u8, earlier, role)) break true;
                } else false;
                if (!seen) addSite(&out, owner, .{ .tag = .node, .a = node_id, .role = role });
            }
        }
    }
    for (types) |item| {
        const owner = decoded(get(item, "id"));
        for (arrayOf(item, "fields")) |field| if (mentions(mention_kind, id, get(field, "type")))
            out.append(a, .{ .owner = .type, .owner_id = owner, .site = .{ .tag = .field_type, .b = decoded(get(field, "id")) } }) catch oom();
        for (arrayOf(item, "variants")) |variant| for (arrayOf(variant, "fields")) |field| if (mentions(mention_kind, id, get(field, "type")))
            out.append(a, .{ .owner = .type, .owner_id = owner, .site = .{ .tag = .field_type, .a = decoded(get(variant, "id")), .b = decoded(get(field, "id")) } }) catch oom();
    }
    for (capabilities) |item| {
        const owner = decoded(get(item, "id"));
        for (arrayOf(item, "operations")) |operation| {
            const op = decoded(get(operation, "id"));
            const at = struct {
                fn f(list: *List(Reference), o: Str, site: Site) void {
                    list.append(a, .{ .owner = .capability, .owner_id = o, .site = site }) catch oom();
                }
            }.f;
            for (arrayOf(operation, "parameters")) |parameter| if (mentions(mention_kind, id, get(parameter, "type")))
                at(&out, owner, .{ .tag = .operation_parameter_type, .a = op, .b = decoded(get(parameter, "id")) });
            if (mentions(mention_kind, id, get(operation, "resultType"))) at(&out, owner, .{ .tag = .operation_result_type, .a = op });
            if (tryProperty("failureType", operation)) |failure| if (failure != .json_null and mentions(mention_kind, id, failure))
                at(&out, owner, .{ .tag = .operation_failure_type, .a = op });
            if (effect and sameId(str(operation, "effectId"), id)) at(&out, owner, .{ .tag = .operation_effect, .a = op });
        }
    }
    std.mem.sort(Reference, out.items, {}, referenceLess);
    return out.items;
}

/// `ValueGraph.collectNodes` of a function body.
pub fn bodyNodes(allocator: std.mem.Allocator, function: Value) []const Value {
    a = allocator;
    var nodes: List(Value) = .empty;
    collectNodes(get(function, "body"), &nodes);
    return nodes.items;
}

/// The facts `SemanticQuery.focusFunction` derives for one function; IDs decoded.
pub const NodeTypeOf = struct { id: Str, type: ?Value };
pub const Evidence = struct { effect: Str, node: Str, operation: bool, source: Str };
pub const BodyRequirements = struct { effects: []const Str, evidence: []const Evidence, unresolved_functions: []const Str, unresolved_operations: []const Str };
pub const CallSite = struct { caller: Str, node: Str, callee: Str };
pub const Abort = struct { node: Str, error_node: Str, error_type: ?Value, enclosing: []const Str, dependents: []const Str };
pub const Focus = struct { node_types: []const NodeTypeOf, requirements: BodyRequirements, wait_path: ?Str, aborts: []const Abort, callers: []const CallSite, callees: []const CallSite };

fn dec(raw: Str) Str {
    return decodeString(raw) catch unreachable;
}

fn orderStr(x: Str, y: Str) std.math.Order {
    return std.mem.order(u16, x, y);
}

fn lessDecoded(_: void, x: Str, y: Str) bool {
    return orderStr(x, y) == .lt;
}

/// Sorted and distinct.
fn sortedSet(items: []Str) []const Str {
    std.mem.sort(Str, items, {}, lessDecoded);
    var out: List(Str) = .empty;
    for (items) |item| if (out.items.len == 0 or !std.mem.eql(u16, out.items[out.items.len - 1], item)) out.append(a, item) catch oom();
    return out.items;
}

/// A type in the model's JSON form, for the wire's type encoder.
pub fn typeValue(t: TypeRef) Value {
    var members: List(Member) = .empty;
    const put = struct {
        fn f(list: *List(Member), comptime name: []const u8, value: Value) void {
            list.append(a, .{ .name = L(name), .value = value }) catch oom();
        }
    }.f;
    const values = struct {
        fn f(ts: []const TypeRef) Value {
            const out = a.alloc(Value, ts.len) catch oom();
            for (ts, out) |item, *v| v.* = typeValue(item);
            return .{ .array = out };
        }
    }.f;
    switch (t.kind) {
        .primitive => {
            put(&members, "kind", .{ .string = L("primitive") });
            put(&members, "primitive", .{ .string = t.name });
        },
        .named => {
            put(&members, "kind", .{ .string = L("named") });
            put(&members, "typeId", .{ .string = t.name });
            if (t.args.len > 0) put(&members, "arguments", values(t.args));
        },
        .type_parameter => {
            put(&members, "kind", .{ .string = L("typeParameter") });
            put(&members, "typeParameterId", .{ .string = t.name });
        },
        .capability => {
            put(&members, "kind", .{ .string = L("capability") });
            put(&members, "capabilityId", .{ .string = t.name });
        },
        .generic => {
            put(&members, "kind", .{ .string = L("builtinGeneric") });
            put(&members, "generic", .{ .string = t.name });
            put(&members, "arguments", values(t.args));
        },
        .function => {
            put(&members, "kind", .{ .string = L("function") });
            put(&members, "parameters", values(t.args));
            put(&members, "resultType", typeValue(t.result.?.*));
        },
    }
    return .{ .object = members.items };
}

/// `OneShotPayloadRequirements.derive`, for recipe inference; all IDs are decoded UTF-8.
pub const OneShotRequirement = struct { function: []const u8, parameters: []const []const u8 };
pub fn oneShotPayloadRequirementsOf(allocator: std.mem.Allocator, root: Value) []const OneShotRequirement {
    a = allocator;
    prepareContext(root);
    const functions = arrayOf(root, "functions");
    const resolvers = a.alloc(Resolver, functions.len) catch oom();
    for (functions, resolvers) |function, *r| r.* = Resolver.init(function);
    const derived = deriveOneShotRequirements(functions, instantiations(functions, resolvers));
    const out = a.alloc(OneShotRequirement, derived.count()) catch oom();
    for (derived.keys(), derived.values(), out) |key, parameters, *entry| {
        const ids = a.alloc([]const u8, parameters.count()) catch oom();
        for (parameters.keys(), ids) |parameter, *id| id.* = text(dec(keyId(parameter)));
        entry.* = .{ .function = text(dec(keyId(key))), .parameters = ids };
    }
    return out;
}

/// Targeted replacement needs to distinguish an abrupt node from an unavailable completion.
/// Like Resolver/byId, id is the model's raw string (not decoded UTF-16).
pub const NodeCompletion = union(enum) { normal: Value, aborts };
pub fn completionOf(allocator: std.mem.Allocator, root: Value, function: Value, id: Str) ?NodeCompletion {
    a = allocator;
    prepareContext(root);
    var resolver = Resolver.init(function);
    const completion = resolver.completion(id) orelse return null;
    return switch (completion) { .normal => |t| .{ .normal = typeValue(t) }, .aborts => .aborts };
}

/// `SemanticQuery.focusFunction`'s derived facts for `function` of `root` (the session's model).
pub fn focusOf(allocator: std.mem.Allocator, root: Value, function: Value) Focus {
    a = allocator;
    prepareContext(root);
    var resolver = Resolver.init(function);
    var nodes: List(Value) = .empty;
    collectNodes(get(function, "body"), &nodes);
    var node_types: List(NodeTypeOf) = .empty;
    for (nodes.items) |node| node_types.append(a, .{ .id = dec(str(node, "id")), .type = if (resolver.nodeType(str(node, "id"))) |t| typeValue(t) else null }) catch oom();
    std.mem.sort(NodeTypeOf, node_types.items, {}, struct {
        fn less(_: void, x: NodeTypeOf, y: NodeTypeOf) bool {
            return orderStr(x.id, y.id) == .lt;
        }
    }.less);
    // `EffectRequirements.derive`.
    var evidence: List(Evidence) = .empty;
    var unresolved_functions: List(Str) = .empty;
    var unresolved_operations: List(Str) = .empty;
    for (nodes.items) |node| {
        const node_id = dec(str(node, "id"));
        for (functionReferences(node)) |reference| if (isInvoked(reference.role)) {
            if (functions_by_id.get(idKey(reference.function_id))) |target| {
                for (arrayOf(target, "requiredEffects")) |effect| evidence.append(a, .{ .effect = dec(effect.string), .node = node_id, .operation = false, .source = dec(reference.function_id) }) catch oom();
            } else unresolved_functions.append(a, dec(reference.function_id)) catch oom();
        };
        if (invokedOperation(node)) |operation_id| {
            if (operations_by_id.get(idKey(operation_id))) |operation| {
                evidence.append(a, .{ .effect = dec(str(operation, "effectId")), .node = node_id, .operation = true, .source = dec(operation_id) }) catch oom();
            } else unresolved_operations.append(a, dec(operation_id)) catch oom();
        }
    }
    std.mem.sort(Evidence, evidence.items, {}, struct {
        fn less(_: void, x: Evidence, y: Evidence) bool {
            var o = orderStr(x.effect, y.effect);
            if (o == .eq) o = orderStr(x.node, y.node);
            if (o == .eq) o = std.math.order(@intFromBool(x.operation), @intFromBool(y.operation));
            if (o == .eq) o = orderStr(x.source, y.source);
            return o == .lt;
        }
    }.less);
    var distinct: List(Evidence) = .empty;
    var effects: List(Str) = .empty;
    for (evidence.items) |item| {
        if (distinct.items.len > 0) {
            const last = distinct.items[distinct.items.len - 1];
            if (std.mem.eql(u16, last.effect, item.effect) and std.mem.eql(u16, last.node, item.node) and last.operation == item.operation and std.mem.eql(u16, last.source, item.source)) continue;
        }
        distinct.append(a, item) catch oom();
        effects.append(a, item.effect) catch oom();
    }
    // `Waiting.derive` and `Waiting.describe`.
    const capabilities = arrayOf(root, "capabilities");
    const functions = arrayOf(root, "functions");
    const waits = deriveWaiting(capabilities, functions);
    const wait_path: ?Str = if (waits.get(idKey(str(function, "id")))) |path| dec(describeWait(capabilities, functions, path)) else null;
    // `abortBoundaries`.
    var dependents: std.StringArrayHashMapUnmanaged(List(Str)) = .empty;
    for (nodes.items) |node| {
        var reads: List(Str) = .empty;
        directValueReferences(node, &reads);
        for (reads.items) |read| {
            const entry = dependents.getOrPut(a, idKey(read)) catch oom();
            if (!entry.found_existing) entry.value_ptr.* = .empty;
            entry.value_ptr.append(a, str(node, "id")) catch oom();
        }
    }
    var aborts: List(Abort) = .empty;
    const Walk = struct {
        fn visit(graph: Value, parents: []const Str, deps: *const std.StringArrayHashMapUnmanaged(List(Str)), types: []const NodeTypeOf, out: *List(Abort)) void {
            for (arrayOf(graph, "nodes")) |node| {
                if (isName(str(node, "kind"), "abort")) {
                    var found: Set = .empty;
                    var pending: List(Str) = .empty;
                    found.put(a, idKey(str(node, "id")), {}) catch oom();
                    pending.append(a, str(node, "id")) catch oom();
                    for (parents) |parent| {
                        found.put(a, idKey(parent), {}) catch oom();
                        pending.append(a, parent) catch oom();
                    }
                    const seeds = found.count();
                    var continuation: List(Str) = .empty;
                    while (pending.pop()) |id| if (deps.get(idKey(id))) |list| for (list.items) |dependent| {
                        if ((found.getOrPut(a, idKey(dependent)) catch oom()).found_existing) continue;
                        continuation.append(a, dec(dependent)) catch oom();
                        pending.append(a, dependent) catch oom();
                    };
                    _ = seeds;
                    const error_node = dec(str(node, "errorNodeId"));
                    const error_type = for (types) |item| {
                        if (std.mem.eql(u16, item.id, error_node)) break item.type;
                    } else null;
                    var enclosing: List(Str) = .empty;
                    for (parents) |parent| enclosing.append(a, dec(parent)) catch oom();
                    out.append(a, .{ .node = dec(str(node, "id")), .error_node = error_node, .error_type = error_type, .enclosing = enclosing.items, .dependents = sortedSet(continuation.items) }) catch oom();
                } else {
                    const inner = std.mem.concat(a, Str, &.{ &.{str(node, "id")}, parents }) catch oom();
                    for (nestedScopes(node)) |scope| visit(scope.graph, inner, deps, types, out);
                }
            }
        }
    };
    Walk.visit(get(function, "body"), &.{}, &dependents, node_types.items, &aborts);
    std.mem.sort(Abort, aborts.items, {}, struct {
        fn less(_: void, x: Abort, y: Abort) bool {
            return orderStr(x.node, y.node) == .lt;
        }
    }.less);
    // `callSites`, then the ones into and out of this function.
    const self_id = dec(str(function, "id"));
    var callers: List(CallSite) = .empty;
    var callees: List(CallSite) = .empty;
    for (functions) |owner| {
        var owned: List(Value) = .empty;
        collectNodes(get(owner, "body"), &owned);
        const owner_id = dec(str(owner, "id"));
        for (owned.items) |node| {
            var targets: List(Str) = .empty;
            for (functionReferences(node)) |reference| targets.append(a, dec(reference.function_id)) catch oom();
            for (sortedSet(targets.items)) |callee| {
                const site: CallSite = .{ .caller = owner_id, .node = dec(str(node, "id")), .callee = callee };
                if (std.mem.eql(u16, callee, self_id)) callers.append(a, site) catch oom();
                if (std.mem.eql(u16, owner_id, self_id)) callees.append(a, site) catch oom();
            }
        }
    }
    const siteLess = struct {
        fn less(_: void, x: CallSite, y: CallSite) bool {
            var o = orderStr(x.caller, y.caller);
            if (o == .eq) o = orderStr(x.node, y.node);
            if (o == .eq) o = orderStr(x.callee, y.callee);
            return o == .lt;
        }
    }.less;
    std.mem.sort(CallSite, callers.items, {}, siteLess);
    std.mem.sort(CallSite, callees.items, {}, siteLess);
    return .{
        .node_types = node_types.items,
        .requirements = .{ .effects = sortedSet(effects.items), .evidence = distinct.items, .unresolved_functions = sortedSet(unresolved_functions.items), .unresolved_operations = sortedSet(unresolved_operations.items) },
        .wait_path = wait_path,
        .aborts = aborts.items,
        .callers = callers.items,
        .callees = callees.items,
    };
}

/// `FingerprintHash.sha256`: the domain is frozen; it is part of every reviewed fingerprint.
fn hashIn(domain: []const u8, canonical: []const u8) []const u8 {
    var hash = std.crypto.hash.sha2.Sha256.init(.{});
    hash.update(domain);
    hash.update(&.{0});
    hash.update(canonical);
    return fmt("sha256:{s}", .{std.fmt.bytesToHex(hash.finalResult(), .lower)});
}

/// `SnapshotCodec.toCanonicalJson` for a schema from the first record version on.
fn canonicalJson(source: []const u8) Failure![]const u8 {
    return canonicalOf(try parseDocument(source));
}

/// The dependency a resolved document must name (`SnapshotCodec.readDocument`): its snapshot ID and
/// the SHA-256 of its minimum-version canonical text. Null when the caller checks the marker.
var expected_marker: ?struct { id: Str, digest: Str } = null;
/// The dependency marker a resolved document's canonical text carries (`serializeResolved`), as JSON.
var write_marker: ?[]const u8 = null;

fn canonicalOf(root: Value) Failure![]const u8 {
    @memset(used, false);
    parsed_root = root;
    const top = [_][]const u8{ "schemaVersion", "snapshotId", "types", "effects", "capabilities", "functions", "obligations" };
    if (resolved_input) {
        try ensureProperties("Resolved snapshot", &(top ++ [_][]const u8{"dependency"}), &(top ++ [_][]const u8{ "parentSnapshotId", "dependency" }), root);
        const marker = try requireProperty("dependency", root);
        try ensureProperties("Dependency marker", &.{ "snapshotId", "digest" }, &.{ "snapshotId", "digest" }, marker);
        if (expected_marker) |expected| {
            const id = try readString("snapshotId", marker);
            const digest = try readString("digest", marker);
            if (!std.mem.eql(u16, id, expected.id) or !std.mem.eql(u16, digest, expected.digest)) return invalidShape("Dependency selection does not match the local snapshot.");
        }
    } else try ensureProperties("Snapshot", &top, &(top ++ [_][]const u8{"parentSnapshotId"}), root);
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
    o.put("schemaVersion", fmt("{d}", .{write_version orelse current}));
    o.put("snapshotId", if (neutral) quoted(L("semantic-fingerprint-root")) else quoted(snapshot_id));
    if (!neutral) if (write_marker) |marker| o.put("dependency", marker);
    if (!neutral) o.putOptional("parentSnapshotId", parent);
    o.put("types", types);
    o.put("effects", effects);
    o.put("capabilities", capabilities);
    o.put("functions", functions);
    o.put("obligations", if (neutral) try neutralObligations(root) else obligations);
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
pub const Scope = struct { bindings: []const Value, graph: Value };

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

// The `requirements` rows per node kind, in table order (a kind listed twice in a row counts twice).
var requirements_by_kind: ?std.StringHashMapUnmanaged([]const usize) = null;

fn requirementsOf(kind: []const u8) []const usize {
    if (requirements_by_kind == null) {
        var map: std.StringHashMapUnmanaged(List(usize)) = .empty;
        for (requirements, 0..) |r, index| {
            var listed = std.mem.tokenizeScalar(u8, r.kinds, ' ');
            while (listed.next()) |name| {
                const entry = map.getOrPut(a, name) catch oom();
                if (!entry.found_existing) entry.value_ptr.* = .empty;
                entry.value_ptr.append(a, index) catch oom();
            }
        }
        var out: std.StringHashMapUnmanaged([]const usize) = .empty;
        var it = map.iterator();
        while (it.next()) |entry| out.put(a, entry.key_ptr.*, entry.value_ptr.items) catch oom();
        requirements_by_kind = out;
    }
    return requirements_by_kind.?.get(kind) orelse &.{};
}

/// The table's requirements on one node, then `ValueEqual`.
fn scalarRules(node: Value) void {
    const kind = text(str(node, "kind"));
    var references: List(Str) = .empty;
    directValueReferences(node, &references);
    for (requirementsOf(kind)) |index| {
        const r = requirements[index];
        // The message is built only for a mismatch: most nodes have none.
        const actual = nodeType(references.items[r.operand]) orelse continue;
        if (typeEql(actual, requiredType(r.type))) continue;
        const name = if (r.name.len == 0) caseNameOf(str(node, "kind")) else asciiUnits(r.name);
        addIn(r.code, cat(&.{ name, L(" node '"), str(node, "id"), L("' "), asciiUnits(r.tail) }));
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
    const listed = comptime blk: {
        @setEvalBranchQuota(100 * names.len + 1000);
        var out: []const []const u8 = &.{};
        var it = std.mem.tokenizeScalar(u8, names, ' ');
        while (it.next()) |name| out = out ++ [_][]const u8{name};
        break :blk out;
    };
    inline for (listed) |name| if (std.mem.eql(u8, name, kind)) return true;
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

// Per call of `unorderedEffects` on a function body, by node identity: nested graphs are searched
// again at each level, and these facts depend only on the node (and the observable set).
var order_depth: usize = 0;
var dependencies_memo: std.AutoHashMapUnmanaged(usize, []const Str) = .empty;
var observable_memo: std.AutoHashMapUnmanaged(usize, bool) = .empty;

fn nodeKey(node: Value) usize {
    return @intFromPtr(node.object.ptr);
}

fn memoDependencies(node: Value) []const Str {
    if (dependencies_memo.get(nodeKey(node))) |deps| return deps;
    var deps: List(Str) = .empty;
    directValueReferences(node, &deps);
    for (nestedScopes(node)) |scope| {
        const inner_nodes = arrayOf(scope.graph, "nodes");
        const local = idSet(inner_nodes);
        for (inner_nodes) |inner| for (memoDependencies(inner)) |dep| if (!local.contains(idKey(dep))) deps.append(a, dep) catch oom();
    }
    dependencies_memo.put(a, nodeKey(node), deps.items) catch oom();
    return deps.items;
}

/// `GraphOrder.nodeObservable` with `semanticView`: Abort and the intrinsically effectful kinds
/// except ParallelAll, a call, effect callback, worker or body of an observable function, or an
/// observable node in a nested graph.
fn nodeObservable(node: Value, observable: *const Set) bool {
    if (observable_memo.get(nodeKey(node))) |known| return known;
    const kind = text(str(node, "kind"));
    const result = blk: {
        if (std.mem.eql(u8, kind, "abort")) break :blk true;
        if (!std.mem.eql(u8, kind, "parallelAll") and kindIs(kind, effectful_kinds)) break :blk true;
        for (functionReferences(node)) |reference| switch (reference.role) {
            .direct_call, .effect_callback, .worker, .body => if (observable.contains(idKey(reference.function_id))) break :blk true,
            else => {},
        };
        for (nestedScopes(node)) |scope| for (arrayOf(scope.graph, "nodes")) |inner| if (nodeObservable(inner, observable)) break :blk true;
        break :blk false;
    };
    observable_memo.put(a, nodeKey(node), result) catch oom();
    return result;
}

/// `GraphOrder.tryUnorderedEffects`: the first unordered observable pair of the graph's own nodes,
/// else of its nested graphs in order.
fn unorderedEffects(nodes: []const Value, observable: *const Set, by_id: bool) ?[2]Str {
    if (order_depth == 0) {
        dependencies_memo.clearRetainingCapacity();
        observable_memo.clearRetainingCapacity();
    }
    order_depth += 1;
    defer order_depth -= 1;
    var edges: Edges = .empty;
    var effectful: List(Str) = .empty;
    for (nodes) |node| {
        edges.put(a, idKey(str(node, "id")), memoDependencies(node)) catch oom();
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
    // The typed view lists a graph's nodes by ID and union arms by their first variant, the semantic
    // one both in document order.
    const ordered = if (by_id) blk: {
        const sorted = a.dupe(Value, nodes) catch oom();
        std.mem.sort(Value, sorted, {}, lessById);
        break :blk sorted;
    } else nodes;
    for (ordered) |node| {
        const scopes = a.dupe(Scope, nestedScopes(node)) catch oom();
        if (by_id and isName(str(node, "kind"), "matchUnion")) {
            const Keyed2 = struct { key: Str, scope: Scope };
            const arms = arrayOf(node, "arms");
            const keyed = a.alloc(Keyed2, arms.len) catch oom();
            for (arms, scopes, keyed) |arm, scope, *k| {
                var first: Str = &.{};
                for (armVariantIds(arm)) |variant| if (first.len == 0 or lessUnits(variant, first)) {
                    first = variant;
                };
                k.* = .{ .key = first, .scope = scope };
            }
            std.mem.sort(Keyed2, keyed, {}, struct {
                fn less(_: void, left: Keyed2, right: Keyed2) bool {
                    return lessUnits(left.key, right.key);
                }
            }.less);
            for (keyed, scopes) |k, *scope| scope.* = k.scope;
        }
        for (scopes) |scope| if (unorderedEffects(arrayOf(scope.graph, "nodes"), observable, by_id)) |pair| return pair;
    }
    return null;
}

/// The runtime-observable functions (`GraphOrder.semanticObservableFunctions`), set per validation.
var observable_functions: Set = .empty;

/// `GraphOrder.tryEffectOrderConflict` with the semantic view, in every function, and its message.
fn effectOrderRules() void {
    const function = in_function.function;
    const pair = unorderedEffects(arrayOf(get(function, "body"), "nodes"), &observable_functions, false) orelse return;
    const nodes_by_id = byId(in_function.all_nodes);
    var operation_names: std.StringHashMapUnmanaged(Str) = .empty;
    for (all_capabilities) |capability| for (arrayOf(capability, "operations")) |operation|
        operation_names.put(a, idKey(str(operation, "id")), cat(&.{ str(capability, "displayName"), L("."), str(operation, "displayName") })) catch oom();
    var described: [2]Str = undefined;
    for (pair, &described) |id, *d| {
        const node = nodes_by_id.get(idKey(id)).?;
        const kind = str(node, "kind");
        const subject: ?Str = if (isName(kind, "directCall"))
            (if (functions_by_id.get(idKey(str(node, "targetFunctionId")))) |f| cat(&.{ L("DirectCall to '"), str(f, "displayName"), L("'") }) else null)
        else if (isName(kind, "capabilityInvoke"))
            (if (operation_names.get(idKey(str(node, "operationId")))) |name| cat(&.{ L("CapabilityInvoke "), name }) else null)
        else
            null;
        d.* = cat(&.{ quote(id), L(" ("), subject orelse caseNameOf(kind), L(")") });
    }
    addIn("AmbiguousEffectOrder", cat(&.{ L("Function "), functionLabel(function), L(" has unordered observable nodes "), described[0], L(" and "), described[1], L(", whose effects (direct or through called functions) would run in no defined order. Sequence them with Bind in the intended order.") }));
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

/// Initial payload requirements, closed over transitive and recursive generic instantiations.
fn deriveOneShotRequirements(functions: []const Value, calls: []const Instantiation) Requirements {
    var derived: Requirements = .empty;
    for (functions) |function| {
        var payload: Set = .empty;
        oneShotParameters(typeRefOf(get(function, "resultType")), &payload);
        for (arrayOf(function, "parameters")) |parameter| oneShotParameters(parameterType(parameter), &payload);
        var nodes: List(Value) = .empty;
        collectNodes(get(function, "body"), &nodes);
        for (nodes.items) |node| {
            if (isName(str(node, "kind"), "oneShotCreate")) {
                oneShotParameters(builtin("oneShot", &.{typeRefOf(get(node, "valueType"))}), &payload);
            } else {
                var types: List(TypeRef) = .empty;
                payloadTypeRefs(node, &types);
                for (types.items) |t| oneShotParameters(t, &payload);
            }
        }
        derived.put(a, idKey(str(function, "id")), payload) catch oom();
    }
    propagate(&derived, calls);
    return derived;
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
    one_shot_requirements = deriveOneShotRequirements(functions, calls);
    var crossing: Requirements = .empty;
    for (functions, resolvers) |function, *r| {
        var nodes: List(Value) = .empty;
        collectNodes(get(function, "body"), &nodes);
        var crosses: Set = .empty;
        for (nodes.items) |node| {
            const kind = str(node, "kind");
            // The node's own type carries the worker result.
            if (isName(kind, "taskStart") or isName(kind, "parallelAll")) {
                var ids: List(Str) = .empty;
                ids.append(a, str(node, "id")) catch oom();
                ids.append(a, str(node, if (isName(kind, "taskStart")) "itemNodeId" else "sourceListNodeId")) catch oom();
                for (arrayOf(node, "context")) |binding| ids.append(a, str(binding, "valueNodeId")) catch oom();
                for (ids.items) |id| if (r.nodeType(id)) |t| occurringIds(t, &crosses);
            }
        }
        crossing.put(a, idKey(str(function, "id")), crosses) catch oom();
    }
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
    observable_functions = observableFunctions(functions);
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

/// The declaration tables and owners the rules and the node type resolver read, set from the
/// parsed document (last-wins where the F# validator's `Map.ofList` is). `validate` sets them first;
/// `--validated` sets only them.
fn prepareContext(root: Value) void {
    const types = arrayOf(root, "types");
    const capabilities = arrayOf(root, "capabilities");
    const functions = arrayOf(root, "functions");
    var operations: List(Value) = .empty;
    for (capabilities) |capability| operations.appendSlice(a, arrayOf(capability, "operations")) catch oom();
    effect_names = byId(arrayOf(root, "effects"));
    functions_by_id = byId(functions);
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
    for (arrayOf(root, "obligations")) |obligation| {
        if (isName(str(obligation, "status"), "open"))
            open_obligation_pairs.put(a, idKey(cat(&.{ str(obligation, "callNodeId"), L("\n"), str(obligation, "parameterId") })), {}) catch oom();
    }
    task_bound_capabilities = .empty;
    for (capabilities) |capability| {
        if (tryProperty("taskBound", capability) != null) task_bound_capabilities.put(a, idKey(str(capability, "id")), {}) catch oom();
    }
}

fn validate(root: Value) void {
    issues.clearRetainingCapacity();
    prepareContext(root);
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

    validateTypeReferences(types, operations.items, functions);

    validateFunctionGraphs(functions);
    waitingRules(capabilities, functions);

    std.mem.sort(Issue, issues.items, {}, issueLess);
}

// ---------------------------------------------------------------- lowering, stage 1: typed semantic IR

// `TypedSemanticIr.project` and `TypedSemanticIrSerializer.serialize` over the parsed document,
// with each node's completion from `Resolver`. The node properties are the model writer's (in
// `model_kinds.txt`'s written order) without the declared types the node type replaces, except the
// few `typedName` renames. See docs/decisions/zig-tools.md.

const LoweringFailure = error{ Invalid, Incomplete, MissingNodeType };
var validated_input = false;
var lowering_failure: []const u8 = "";

fn typedType(t: TypeRef) []const u8 {
    var o: Obj = .{};
    switch (t.kind) {
        .primitive => o.put("kind", fmt("\"primitive{s}\"", .{text(caseName(t))})),
        .named => {
            o.put("kind", "\"named\"");
            o.put("typeId", quoted(t.name));
            if (t.args.len > 0) o.put("arguments", typedTypes(t.args));
        },
        .type_parameter => {
            o.put("kind", "\"typeParameter\"");
            o.put("typeParameterId", quoted(t.name));
        },
        .capability => {
            o.put("kind", "\"capability\"");
            o.put("capabilityId", quoted(t.name));
        },
        .function => {
            o.put("kind", "\"function\"");
            o.put("parameters", typedTypes(t.args));
            o.put("resultType", typedType(t.result.?.*));
        },
        .generic => {
            o.put("kind", quoted(t.name));
            const slots: []const []const u8 = if (t.isGeneric("option") or t.isGeneric("list") or t.isGeneric("set")) &.{"itemType"} else if (t.isGeneric("result")) &.{ "okType", "errorType" } else if (t.isGeneric("map")) &.{ "keyType", "valueType" } else if (t.isGeneric("oneShot")) &.{"valueType"} else if (t.isGeneric("task")) &.{"resultType"} else &.{};
            for (slots, t.args) |slot, arg| o.put(slot, typedType(arg));
        },
    }
    return o.done();
}

fn typedTypes(types: []const TypeRef) []const u8 {
    const texts = a.alloc([]const u8, types.len) catch oom();
    for (types, texts) |t, *out| out.* = typedType(t);
    return array(texts);
}

fn typedTypeOf(element: Value) []const u8 {
    return typedType(typeRefOf(element));
}

/// `{ id, type }`, the shape of fields, parameters and pattern bindings.
fn typedSlot(element: Value) Keyed {
    var o: Obj = .{};
    o.put("id", quoted(str(element, "id")));
    o.put("type", typedTypeOf(get(element, "type")));
    return .{ .key = str(element, "id"), .text = o.done() };
}

fn typedSlots(elements: []const Value, sorted: bool) []const u8 {
    const out = a.alloc(Keyed, elements.len) catch oom();
    for (elements, out) |element, *o| o.* = typedSlot(element);
    return if (sorted) sortedArray(out) else plainArray(out);
}

fn typedTypeParameters(o: *Obj, element: Value) void {
    const parameters = arrayOf(element, "typeParameters");
    if (parameters.len == 0) return;
    const texts = a.alloc([]const u8, parameters.len) catch oom();
    for (parameters, texts) |parameter, *t| {
        var p: Obj = .{};
        p.put("typeParameterId", quoted(str(parameter, "typeParameterId")));
        p.put("displayName", quoted(str(parameter, "displayName")));
        t.* = p.done();
    }
    o.put("typeParameters", array(texts));
}

/// Arguments, capability arguments and field values, sorted by target.
fn typedTargets(elements: []const Value, target: []const u8) []const u8 {
    const out = a.alloc(Keyed, elements.len) catch oom();
    for (elements, out) |element, *o| {
        var obj: Obj = .{};
        obj.put(target, quoted(str(element, target)));
        obj.put("valueNodeId", quoted(str(element, "valueNodeId")));
        o.* = .{ .key = str(element, target), .text = obj.done() };
    }
    return sortedArray(out);
}

/// The renames of the typed writer, and the declared types it keeps.
fn typedName(kind: []const u8, read: Read) ?[]const u8 {
    if (std.mem.eql(u8, read.name, "patternBindingId")) return "bindingId";
    if (std.mem.eql(u8, read.name, "targetFunctionId")) return "functionId";
    if (std.mem.eql(u8, read.name, "typeId")) return null;
    if (read.shape == .targs) return null;
    if (read.shape == .type and !(std.mem.eql(u8, kind, "exceptionRethrow") or std.mem.eql(u8, kind, "oneShotCreate"))) return null;
    return read.name;
}

fn typedArm(resolver: *Resolver, arm: Value, binding: []const u8) []const u8 {
    var o: Obj = .{};
    o.put("binding", typedSlot(get(arm, binding)).text);
    o.put("body", typedGraph(resolver, get(arm, "body")));
    return o.done();
}

fn typedShape(resolver: *Resolver, read: Read, node: Value) ?[]const u8 {
    const value = tryProperty(read.name, node) orelse return null;
    return switch (read.shape) {
        .id, .optid, .lit, .mode => quoted(value.string),
        .ids => blk: {
            const texts = a.alloc([]const u8, value.array.len) catch oom();
            for (value.array, texts) |item, *t| t.* = quoted(item.string);
            break :blk array(texts);
        },
        .type => typedTypeOf(value),
        .targs => unreachable,
        .graph => typedGraph(resolver, value),
        .varm => typedArm(resolver, value, "valueBinding"),
        .errarm => typedArm(resolver, value, "errorBinding"),
        .ctx, .args => typedTargets(value.array, "targetParameterId"),
        .capargs => typedTargets(value.array, "targetCapabilityParameterId"),
        .fields => typedTargets(value.array, "targetFieldId"),
        .bool => if (value == .json_true) "true" else "false",
        .int => fmt("{d}", .{readInt64(read.name, node) catch unreachable}),
        .bits => readFloat64Bits(read.name, node) catch unreachable,
        .impls => blk: {
            const out = a.alloc(Keyed, value.array.len) catch oom();
            for (value.array, out) |item, *o| {
                var obj: Obj = .{};
                obj.put("operationId", quoted(str(item, "operationId")));
                obj.put("functionId", quoted(str(item, "functionId")));
                o.* = .{ .key = str(item, "operationId"), .text = obj.done() };
            }
            break :blk sortedArray(out);
        },
        .arms => blk: {
            const out = a.alloc(Keyed, value.array.len) catch oom();
            for (value.array, out) |arm, *o| {
                const variants = a.dupe(Str, armVariantIds(arm)) catch oom();
                std.mem.sort(Str, variants, {}, lessStr);
                var obj: Obj = .{};
                if (variants.len == 1) obj.put("variantId", quoted(variants[0])) else {
                    const texts = a.alloc([]const u8, variants.len) catch oom();
                    for (variants, texts) |variant, *t| t.* = quoted(variant);
                    obj.put("variantIds", array(texts));
                }
                const fields = arrayOf(arm, "fieldBindings");
                const bindings = a.alloc(Keyed, fields.len) catch oom();
                for (fields, bindings) |field, *b| {
                    var f: Obj = .{};
                    f.put("sourceFieldId", quoted(str(field, "sourceFieldId")));
                    f.put("binding", typedSlot(get(field, "binding")).text);
                    b.* = .{ .key = str(field, "sourceFieldId"), .text = f.done() };
                }
                obj.put("fieldBindings", sortedArray(bindings));
                obj.put("body", typedGraph(resolver, get(arm, "body")));
                o.* = .{ .key = if (variants.len > 0) variants[0] else &.{}, .text = obj.done() };
            }
            break :blk sortedArray(out);
        },
    };
}

fn typedNode(resolver: *Resolver, node: Value) []const u8 {
    const kind_name = text(str(node, "kind"));
    const kind = kinds.get(kind_name).?;
    var o: Obj = .{};
    o.put("originNodeId", quoted(str(node, "id")));
    switch (resolver.completion(str(node, "id")).?) {
        .normal => |t| o.put("type", typedType(t)),
        .aborts => o.put("completion", "\"abort\""),
    }
    o.put("kind", quoted(str(node, "kind")));
    for (kind.writes) |index| {
        const read = kind.reads[index];
        if (typedName(kind_name, read)) |name| o.putOptional(name, typedShape(resolver, read, node));
    }
    return o.done();
}

fn typedGraph(resolver: *Resolver, graph: Value) []const u8 {
    const nodes = arrayOf(graph, "nodes");
    const out = a.alloc(Keyed, nodes.len) catch oom();
    for (nodes, out) |node, *o| o.* = .{ .key = str(node, "id"), .text = typedNode(resolver, node) };
    var obj: Obj = .{};
    obj.put("nodes", sortedArray(out));
    obj.put("resultNodeId", quoted(str(graph, "resultNodeId")));
    return obj.done();
}

/// The first node without a completion, in `projectGraph`'s order (by ID, each node before its nested graphs).
fn missingNodeType(resolver: *Resolver, graph: Value) ?Str {
    const nodes = a.dupe(Value, arrayOf(graph, "nodes")) catch oom();
    std.mem.sort(Value, nodes, {}, lessById);
    for (nodes) |node| {
        if (resolver.completion(str(node, "id")) == null) return str(node, "id");
        for (nestedScopes(node)) |scope| if (missingNodeType(resolver, scope.graph)) |id| return id;
    }
    return null;
}

/// `ExecutionCallGraph.forSnapshotEntry`: the functions the entry reaches through any function
/// reference (nested graphs included) and the obligations on their call nodes, in document order.
fn selectEntry(root: Value, entry: Str) Value {
    const functions = arrayOf(root, "functions");
    const by_id = byId(functions);
    var seen: Set = .empty;
    var pending: List(Str) = .empty;
    pending.append(a, entry) catch oom();
    while (pending.pop()) |id| {
        if (seen.contains(idKey(id))) continue;
        seen.put(a, idKey(id), {}) catch oom();
        const function = by_id.get(idKey(id)) orelse continue;
        var nodes: List(Value) = .empty;
        collectNodes(get(function, "body"), &nodes);
        for (nodes.items) |node| for (functionReferences(node)) |reference| pending.append(a, reference.function_id) catch oom();
    }
    var selected: List(Value) = .empty;
    var node_ids: Set = .empty;
    for (functions) |function| if (seen.contains(idKey(str(function, "id")))) {
        selected.append(a, function) catch oom();
        var nodes: List(Value) = .empty;
        collectNodes(get(function, "body"), &nodes);
        for (nodes.items) |node| node_ids.put(a, idKey(str(node, "id")), {}) catch oom();
    };
    var obligations: List(Value) = .empty;
    for (arrayOf(root, "obligations")) |obligation| if (node_ids.contains(idKey(str(obligation, "callNodeId")))) obligations.append(a, obligation) catch oom();
    const members = a.dupe(Member, root.object) catch oom();
    for (members) |*member| {
        if (isName(member.name, "functions")) member.value = .{ .array = selected.items };
        if (isName(member.name, "obligations")) member.value = .{ .array = obligations.items };
    }
    return .{ .object = members };
}

const Ready = struct { function: Value, resolver: *Resolver };

/// `SnapshotReadiness.evaluate`, then each function (by ID) with its resolver; `MissingNodeType`
/// for the first function with a node the resolver cannot type.
fn readyFunctions(root: Value) LoweringFailure![]Ready {
    // `--validated`: the bytes passed the validator before, so only its context is prepared.
    if (validated_input) {
        issues.clearRetainingCapacity();
        prepareContext(root);
    } else validate(root);
    if (issues.items.len > 0) {
        lowering_failure = fmt("InvalidSnapshot: {d} issues", .{issues.items.len});
        return error.Invalid;
    }
    var open: List(Str) = .empty;
    for (arrayOf(root, "obligations")) |obligation| if (isName(str(obligation, "status"), "open")) open.append(a, str(obligation, "id")) catch oom();
    if (open.items.len > 0) {
        std.mem.sort(Str, open.items, {}, lessStr);
        var names: List(u8) = .empty;
        for (open.items, 0..) |id, i| names.print(a, "{s}{s}", .{ if (i == 0) "" else ", ", text(id) }) catch oom();
        lowering_failure = fmt("IncompleteSnapshot: {s}", .{names.items});
        return error.Incomplete;
    }
    const functions = a.dupe(Value, arrayOf(root, "functions")) catch oom();
    std.mem.sort(Value, functions, {}, lessById);
    const out = a.alloc(Ready, functions.len) catch oom();
    for (functions, out) |function, *ready| {
        const resolver = a.create(Resolver) catch oom();
        resolver.* = Resolver.init(function);
        if (missingNodeType(resolver, get(function, "body"))) |node_id| {
            lowering_failure = fmt("MissingNodeType: {s} {s}", .{ text(str(function, "id")), text(node_id) });
            return error.MissingNodeType;
        }
        ready.* = .{ .function = function, .resolver = resolver };
    }
    return out;
}

/// `TypedSemanticIr.project` (readiness first) on the selection, as the serializer's JSON.
fn typedIr(root: Value) LoweringFailure![]const u8 {
    const functions = try readyFunctions(root);
    const texts = a.alloc([]const u8, functions.len) catch oom();
    for (functions, texts) |ready, *t| {
        const function = ready.function;
        var o: Obj = .{};
        o.put("id", quoted(str(function, "id")));
        typedTypeParameters(&o, function);
        o.put("parameters", typedSlots(arrayOf(function, "parameters"), false));
        o.put("resultType", typedTypeOf(get(function, "resultType")));
        o.put("requiredEffects", sortedIds(arrayOf(function, "requiredEffects")));
        o.put("body", typedGraph(ready.resolver, get(function, "body")));
        t.* = o.done();
    }
    return program(root, sortedDeclarations(arrayOf(root, "types"), typedDeclaredType), texts);
}

/// `ExecutionIr.lower` of the projection, as the serializer's JSON.
fn executionIr(root: Value) (LoweringFailure || LowerFailure)![]const u8 {
    const functions = try readyFunctions(root);
    const texts = a.alloc([]const u8, functions.len) catch oom();
    const plain = a.alloc(Value, functions.len) catch oom();
    for (functions, plain) |ready, *function| function.* = ready.function;
    const observable = observableFunctions(plain);
    for (functions, texts) |ready, *t| t.* = try executionFunction(ready.function, ready.resolver, &observable);
    return program(root, sortedDeclarations(arrayOf(root, "types"), executionDeclaredType), texts);
}

fn program(root: Value, types: []const u8, functions: []const []const u8) []const u8 {
    var o: Obj = .{};
    o.put("schemaVersion", "14");
    o.put("sourceSnapshotId", quoted(str(root, "snapshotId")));
    o.put("types", types);
    o.put("effects", sortedIds(arrayOf(root, "effects")));
    o.put("capabilities", sortedDeclarations(arrayOf(root, "capabilities"), typedCapability));
    o.put("functions", array(functions));
    return o.done();
}

fn lessById(_: void, left: Value, right: Value) bool {
    return lessUnits(str(left, "id"), str(right, "id"));
}

/// IDs (strings, or declarations by their `id`), sorted.
fn sortedIds(values: []const Value) []const u8 {
    const out = a.alloc(Keyed, values.len) catch oom();
    for (values, out) |value, *o| {
        const id = if (value == .string) value.string else str(value, "id");
        o.* = .{ .key = id, .text = quoted(id) };
    }
    return sortedArray(out);
}

fn sortedDeclarations(values: []const Value, comptime writer: fn (Value) []const u8) []const u8 {
    const out = a.alloc(Keyed, values.len) catch oom();
    for (values, out) |value, *o| o.* = .{ .key = str(value, "id"), .text = writer(value) };
    return sortedArray(out);
}

fn typedDeclaredType(declaration: Value) []const u8 {
    var o: Obj = .{};
    o.put("id", quoted(str(declaration, "id")));
    typedTypeParameters(&o, declaration);
    if (isName(str(declaration, "kind"), "product")) {
        o.put("kind", "\"product\"");
        o.put("fields", typedSlots(arrayOf(declaration, "fields"), true));
    } else {
        o.put("kind", "\"union\"");
        const variants = arrayOf(declaration, "variants");
        const out = a.alloc(Keyed, variants.len) catch oom();
        for (variants, out) |variant, *v| {
            var obj: Obj = .{};
            obj.put("id", quoted(str(variant, "id")));
            obj.put("fields", typedSlots(arrayOf(variant, "fields"), true));
            v.* = .{ .key = str(variant, "id"), .text = obj.done() };
        }
        o.put("variants", sortedArray(out));
    }
    return o.done();
}

fn typedCapability(capability: Value) []const u8 {
    var o: Obj = .{};
    o.put("id", quoted(str(capability, "id")));
    o.put("operations", sortedDeclarations(arrayOf(capability, "operations"), typedOperation));
    return o.done();
}

fn typedOperation(operation: Value) []const u8 {
    var o: Obj = .{};
    o.put("id", quoted(str(operation, "id")));
    o.put("parameters", typedSlots(arrayOf(operation, "parameters"), false));
    o.put("resultType", typedTypeOf(get(operation, "resultType")));
    if (tryProperty("failureType", operation)) |failure| o.put("failureType", typedTypeOf(failure));
    o.put("effectId", quoted(str(operation, "effectId")));
    return o.done();
}

// ---------------------------------------------------------------- lowering, stage 2: Execution IR

// `ExecutionIr.lower` of the typed program and `ExecutionIrSerializer.serialize`. Instructions are
// written as they are made; a simple node's properties are its typed ones in order, a node
// reference becomes the value bound to it, and `instruction_kinds.txt` names them. Effect order is
// checked again by the rule the validator applies (`validateEffectOrder`), here with the typed view's
// order of nested graphs; its descriptor equals the semantic one (`EffectClassificationConsistencyTests`).

/// `GraphOrder.observableFunctions` over the program's functions: a function is observable when it
/// declares effects or holds an intrinsically observable node other than Abort, at any depth, or
/// when it calls, folds with, starts or runs as a body an observable function.
fn observableFunctions(functions: []const Value) Set {
    // `Map.ofSeq`: the last function of an ID gives its descriptor.
    const last = byId(functions);
    var observable: Set = .empty;
    var callers: std.StringHashMapUnmanaged(List(Str)) = .empty;
    var pending: List(Str) = .empty;
    for (last.values()) |function| {
        const id = str(function, "id");
        var seed = arrayOf(function, "requiredEffects").len > 0;
        var nodes: List(Value) = .empty;
        collectNodes(get(function, "body"), &nodes);
        for (nodes.items) |node| {
            const kind = text(str(node, "kind"));
            if (!std.mem.eql(u8, kind, "parallelAll") and kindIs(kind, effectful_kinds)) seed = true;
            for (functionReferences(node)) |reference| switch (reference.role) {
                .direct_call, .effect_callback, .worker, .body => {
                    const entry = callers.getOrPut(a, idKey(reference.function_id)) catch oom();
                    if (!entry.found_existing) entry.value_ptr.* = .empty;
                    entry.value_ptr.append(a, id) catch oom();
                },
                else => {},
            };
        }
        if (seed and !(observable.getOrPut(a, idKey(id)) catch oom()).found_existing) pending.append(a, id) catch oom();
    }
    while (pending.pop()) |id| if (callers.get(idKey(id))) |list| for (list.items) |caller| {
        if (!(observable.getOrPut(a, idKey(caller)) catch oom()).found_existing) pending.append(a, caller) catch oom();
    };
    return observable;
}

const instruction_kinds_text = @embedFile("instruction_kinds.txt");
var instruction_names: std.StringHashMapUnmanaged([]const []const u8) = .empty;

fn loadInstructionNames() void {
    var lines = std.mem.tokenizeAny(u8, instruction_kinds_text, "\r\n");
    while (lines.next()) |line| {
        const bar = std.mem.indexOfScalar(u8, line, '|').?;
        var names: List([]const u8) = .empty;
        var it = std.mem.tokenizeScalar(u8, line[bar + 1 ..], ' ');
        while (it.next()) |name| names.append(a, name) catch oom();
        instruction_names.put(a, line[0..bar], names.items) catch oom();
    }
}

/// `Uri.EscapeDataString` of an ID's UTF-8.
fn escapeData(id: Str) []const u8 {
    var out: List(u8) = .empty;
    for (text(id)) |byte| switch (byte) {
        'A'...'Z', 'a'...'z', '0'...'9', '-', '.', '_', '~' => out.append(a, byte) catch oom(),
        else => out.print(a, "%{X:0>2}", .{byte}) catch oom(),
    };
    return out.items;
}

/// `ExecutionIrIds`: `<prefix>/<escaped id>`.
fn executionId(prefix: []const u8, id: Str) []const u8 {
    return fmt("{s}/{s}", .{ prefix, escapeData(id) });
}

fn executionValue(id: []const u8, type_text: []const u8, origin: []const u8) []const u8 {
    var o: Obj = .{};
    o.put("id", quotedAscii(id));
    o.put("type", type_text);
    o.put("origin", origin);
    return o.done();
}

const Block = struct {
    id: []const u8,
    parameters: []const []const u8,
    instructions: List([]const u8) = .empty,
    terminator: ?[]const u8 = null,
};

const Env = struct {
    nodes: std.StringHashMapUnmanaged([]const u8) = .empty,
    patterns: std.StringHashMapUnmanaged([]const u8) = .empty,

    fn clone(self: Env) Env {
        return .{ .nodes = self.nodes.clone(a) catch oom(), .patterns = self.patterns.clone(a) catch oom() };
    }
};

const Endpoint = struct { block: *Block, value: []const u8 };
const Merge = struct { value: []const u8, value_id: []const u8, block: *Block };

const LowerFailure = error{Lowering};

const Lowering = struct {
    function: Value,
    resolver: *Resolver,
    parameters_by_id: ValueMap,
    blocks: std.StringArrayHashMapUnmanaged(*Block) = .empty,

    fn fail(self: *Lowering, case: []const u8, detail: []const u8) LowerFailure {
        lowering_failure = fmt("{s}: {s} {s}", .{ case, text(str(self.function, "id")), detail });
        return error.Lowering;
    }

    fn addBlock(self: *Lowering, id: []const u8, parameters: []const []const u8) *Block {
        const block = a.create(Block) catch oom();
        block.* = .{ .id = id, .parameters = parameters };
        self.blocks.put(a, id, block) catch oom();
        return block;
    }

    fn nodeType(self: *Lowering, node: Value) []const u8 {
        return typedType(normalType(self.resolver.completion(str(node, "id"))).?);
    }

    fn nodeValueId(node: Value) []const u8 {
        return executionId("node", str(node, "id"));
    }

    fn makeNodeValue(self: *Lowering, node: Value) []const u8 {
        var origin: Obj = .{};
        origin.put("kind", "\"semanticNode\"");
        origin.put("nodeId", quoted(str(node, "id")));
        return executionValue(nodeValueId(node), self.nodeType(node), origin.done());
    }

    fn addInstruction(self: *Lowering, block: *Block, node: Value, kind: []const u8, properties: []const u8) []const u8 {
        var o: Obj = .{};
        o.put("result", self.makeNodeValue(node));
        o.put("originNodeId", quoted(str(node, "id")));
        o.put("kind", quotedAscii(kind));
        if (properties.len > 2) {
            o.buf.append(a, ',') catch oom();
            o.buf.appendSlice(a, properties[1 .. properties.len - 1]) catch oom();
        }
        block.instructions.append(a, o.done()) catch oom();
        return nodeValueId(node);
    }

    fn addPatternExtraction(self: *Lowering, block: *Block, match_node: Value, binding: Value, kind: []const u8, properties: []const u8) []const u8 {
        _ = self;
        var origin: Obj = .{};
        origin.put("kind", "\"patternBinding\"");
        origin.put("matchNodeId", quoted(str(match_node, "id")));
        origin.put("bindingId", quoted(str(binding, "id")));
        const id = executionId("pattern", str(binding, "id"));
        var o: Obj = .{};
        o.put("result", executionValue(id, typedTypeOf(get(binding, "type")), origin.done()));
        o.put("originNodeId", quoted(str(match_node, "id")));
        o.put("kind", quotedAscii(kind));
        o.buf.append(a, ',') catch oom();
        o.buf.appendSlice(a, properties[1 .. properties.len - 1]) catch oom();
        block.instructions.append(a, o.done()) catch oom();
        return id;
    }

    fn requireNode(self: *Lowering, env: *const Env, id: Str) LowerFailure![]const u8 {
        return env.nodes.get(idKey(id)) orelse self.fail("MissingNodeValue", text(id));
    }

    /// Arguments, capability arguments and field values with their values, sorted by target.
    fn targets(self: *Lowering, env: *const Env, elements: []const Value, target: []const u8) LowerFailure![]const u8 {
        const out = a.alloc(Keyed, elements.len) catch oom();
        for (elements, out) |element, *o| {
            var obj: Obj = .{};
            obj.put(target, quoted(str(element, target)));
            obj.put("valueId", quotedAscii(try self.requireNode(env, str(element, "valueNodeId"))));
            o.* = .{ .key = str(element, target), .text = obj.done() };
        }
        return sortedArray(out);
    }

    /// A simple node's instruction properties (`{...}`), from its typed properties.
    fn simpleProperties(self: *Lowering, env: *const Env, node: Value, kind_name: []const u8) LowerFailure![]const u8 {
        const kind = kinds.get(kind_name).?;
        const names = instruction_names.get(kind_name).?;
        var o: Obj = .{};
        var position: usize = 0;
        for (kind.writes) |index| {
            const read = kind.reads[index];
            if (typedName(kind_name, read) == null) continue;
            const name = names[position];
            position += 1;
            const value = tryProperty(read.name, node) orelse continue;
            const reference = inNames(asciiUnits(read.name), kind.references);
            o.put(name, switch (read.shape) {
                .id, .optid => if (reference) quotedAscii(try self.requireNode(env, value.string)) else quoted(value.string),
                .ids => blk: {
                    const texts = a.alloc([]const u8, value.array.len) catch oom();
                    for (value.array, texts) |item, *t| t.* = quotedAscii(try self.requireNode(env, item.string));
                    break :blk array(texts);
                },
                .ctx, .args => try self.targets(env, value.array, "targetParameterId"),
                .capargs => try self.targets(env, value.array, "targetCapabilityParameterId"),
                .fields => try self.targets(env, value.array, "targetFieldId"),
                else => typedShape(self.resolver, read, node).?,
            });
        }
        std.debug.assert(position == names.len);
        return o.done();
    }

    fn lowerSimple(self: *Lowering, env: *Env, block: *Block, node: Value) LowerFailure!void {
        const kind_name = text(str(node, "kind"));
        const value_id = if (std.mem.eql(u8, kind_name, "parameterReference")) blk: {
            const parameter = str(node, "parameterId");
            if (self.parameters_by_id.get(idKey(parameter)) == null) return self.fail("MissingFunctionParameter", text(parameter));
            var o: Obj = .{};
            o.put("sourceValueId", quotedAscii(executionId("parameter", parameter)));
            break :blk self.addInstruction(block, node, "readParameter", o.done());
        } else if (std.mem.eql(u8, kind_name, "patternReference")) blk: {
            const binding = str(node, "patternBindingId");
            const source = env.patterns.get(idKey(binding)) orelse return self.fail("MissingPatternValue", text(binding));
            var o: Obj = .{};
            o.put("sourceValueId", quotedAscii(source));
            break :blk self.addInstruction(block, node, "readPatternBinding", o.done());
        } else self.addInstruction(block, node, kind_name, try self.simpleProperties(env, node, kind_name));
        env.nodes.put(a, idKey(str(node, "id")), value_id) catch oom();
    }

    /// `ExecutionIrAnalysis.dependencies`: direct references and nested free references, distinct and sorted.
    fn dependenciesOf(node: Value) []const Str {
        // The effect-order check of this function, which runs first, filled the memo.
        const all = memoDependencies(node);
        var seen: Set = .empty;
        var out: List(Str) = .empty;
        for (all) |id| if (!seen.contains(idKey(id))) {
            seen.put(a, idKey(id), {}) catch oom();
            out.append(a, id) catch oom();
        };
        std.mem.sort(Str, out.items, {}, lessStr);
        return out.items;
    }

    /// `isExactSelfCall`: a self call whose arguments have the parameters' exact types.
    fn isExactSelfCall(self: *Lowering, node: Value) bool {
        if (!same(str(node, "targetFunctionId"), str(self.function, "id"))) return false;
        for (arrayOf(node, "arguments")) |argument| {
            const parameter = self.parameters_by_id.get(idKey(str(argument, "targetParameterId"))) orelse return false;
            const actual = normalType(self.resolver.completion(str(argument, "valueNodeId"))) orelse return false;
            if (!typeEql(actual, typeRefOf(get(parameter, "type")))) return false;
        }
        return true;
    }

    /// `tailSelfCallNode`: the graph's result node, when it leads to a self call in tail position and
    /// no other node of the graph uses it.
    fn tailSelfCallNode(self: *Lowering, graph: Value) ?Value {
        const result_id = str(graph, "resultNodeId");
        const nodes = arrayOf(graph, "nodes");
        const result = for (nodes) |node| {
            if (same(str(node, "id"), result_id)) break node;
        } else return null;
        if (!self.leadsToTailSelfCall(result)) return null;
        for (nodes) |other| {
            if (same(str(other, "id"), result_id)) continue;
            for (dependenciesOf(other)) |dependency| if (same(dependency, result_id)) return null;
        }
        return result;
    }

    fn leadsToTailSelfCall(self: *Lowering, node: Value) bool {
        const kind = text(str(node, "kind"));
        if (std.mem.eql(u8, kind, "directCall")) return self.isExactSelfCall(node);
        if (!kindIs(kind, "bind conditional boolAnd boolOr matchUnion matchOption matchResult")) return false;
        for (nestedScopes(node)) |scope| if (self.tailSelfCallNode(scope.graph) != null) return true;
        return false;
    }

    fn jump(block: *Block, target: *Block, target_value: []const u8, value: []const u8) void {
        var argument: Obj = .{};
        argument.put("targetValueId", quotedAscii(target_value));
        argument.put("valueId", quotedAscii(value));
        var o: Obj = .{};
        o.put("kind", "\"jump\"");
        o.put("blockId", quotedAscii(target.id));
        o.put("arguments", array(&.{argument.done()}));
        block.terminator = o.done();
    }

    fn mergeFor(self: *Lowering, node: Value) ?Merge {
        if (self.resolver.completion(str(node, "id")).? == .aborts) return null;
        const value = self.makeNodeValue(node);
        const block = self.addBlock(fmt("{s}/merge", .{executionId("node", str(node, "id"))}), a.dupe([]const u8, &.{value}) catch oom());
        return .{ .value = value, .value_id = nodeValueId(node), .block = block };
    }

    /// `finishArms`: the arm endpoints jump to the merge, and the graph continues there; with no
    /// endpoint the merge block is dropped and the graph stops. Returns the block to continue in.
    fn finishArms(self: *Lowering, env: *Env, node: Value, merge: ?Merge, endpoints: []const ?Endpoint) ?*Block {
        const reached = for (endpoints) |endpoint| {
            if (endpoint != null) break true;
        } else false;
        if (merge) |m| if (!reached) {
            _ = self.blocks.orderedRemove(m.block.id);
            return null;
        };
        for (endpoints) |endpoint| if (endpoint) |e| jump(e.block, merge.?.block, merge.?.value_id, e.value);
        const m = merge orelse return null;
        env.nodes.put(a, idKey(str(node, "id")), m.value_id) catch oom();
        return m.block;
    }

    fn branch(block: *Block, kind: []const u8, input_name: []const u8, input: []const u8, names: [2][]const u8, blocks: [2]*Block) void {
        var o: Obj = .{};
        o.put("kind", quotedAscii(kind));
        o.put(input_name, quotedAscii(input));
        o.put(names[0], quotedAscii(blocks[0].id));
        o.put(names[1], quotedAscii(blocks[1].id));
        block.terminator = o.done();
    }

    fn roleBlock(self: *Lowering, node: Value, role: []const u8) *Block {
        return self.addBlock(fmt("{s}/{s}", .{ executionId("node", str(node, "id")), role }), &.{});
    }

    fn withBinding(env: *const Env, binding: Value, value_id: []const u8) Env {
        var out = env.clone();
        out.patterns.put(a, idKey(str(binding, "id")), value_id) catch oom();
        return out;
    }

    /// Lowers one structured node; returns the block to continue in, or null when the graph stops.
    fn lowerStructured(self: *Lowering, env: *Env, block: *Block, node: Value, is_tail: bool) LowerFailure!?*Block {
        const kind = text(str(node, "kind"));
        if (std.mem.eql(u8, kind, "bind")) {
            const input = try self.requireNode(env, str(node, "inputNodeId"));
            const arm = get(node, "boundBody");
            const binding = get(arm, "valueBinding");
            var origin: Obj = .{};
            origin.put("kind", "\"patternBinding\"");
            origin.put("matchNodeId", quoted(str(node, "id")));
            origin.put("bindingId", quoted(str(binding, "id")));
            const bound_id = executionId("pattern", str(binding, "id"));
            const bound = executionValue(bound_id, typedTypeOf(get(binding, "type")), origin.done());
            const body = self.addBlock(fmt("{s}/body", .{executionId("node", str(node, "id"))}), a.dupe([]const u8, &.{bound}) catch oom());
            jump(block, body, bound_id, input);
            const end = try self.lowerGraph(body, withBinding(env, binding, bound_id), is_tail, get(arm, "body"));
            return self.finishArms(env, node, self.mergeFor(node), &.{end});
        }
        if (std.mem.eql(u8, kind, "conditional")) {
            const condition = try self.requireNode(env, str(node, "conditionNodeId"));
            const true_block = self.roleBlock(node, "when-true");
            const false_block = self.roleBlock(node, "when-false");
            const merge = self.mergeFor(node);
            branch(block, "branch", "conditionValueId", condition, .{ "whenTrue", "whenFalse" }, .{ true_block, false_block });
            const true_end = try self.lowerGraph(true_block, env.clone(), is_tail, get(node, "whenTrue"));
            const false_end = try self.lowerGraph(false_block, env.clone(), is_tail, get(node, "whenFalse"));
            return self.finishArms(env, node, merge, &.{ true_end, false_end });
        }
        if (std.mem.eql(u8, kind, "boolAnd") or std.mem.eql(u8, kind, "boolOr")) {
            const left = try self.requireNode(env, str(node, "leftNodeId"));
            const right_block = self.roleBlock(node, "right");
            const decided_block = self.roleBlock(node, "decided");
            const merge = self.mergeFor(node);
            const blocks: [2]*Block = if (std.mem.eql(u8, kind, "boolAnd")) .{ right_block, decided_block } else .{ decided_block, right_block };
            branch(block, "branch", "conditionValueId", left, .{ "whenTrue", "whenFalse" }, blocks);
            const right_end = try self.lowerGraph(right_block, env.clone(), is_tail, get(node, "right"));
            return self.finishArms(env, node, merge, &.{ right_end, Endpoint{ .block = decided_block, .value = left } });
        }
        if (std.mem.eql(u8, kind, "matchUnion")) {
            const input = try self.requireNode(env, str(node, "inputNodeId"));
            const merge = self.mergeFor(node);
            const Arm = struct { arm: Value, variants: []Str, block: *Block = undefined };
            const items = arrayOf(node, "arms");
            const arms = a.alloc(Arm, items.len) catch oom();
            for (items, arms) |item, *arm| {
                arm.* = .{ .arm = item, .variants = a.dupe(Str, armVariantIds(item)) catch oom() };
                std.mem.sort(Str, arm.variants, {}, lessStr);
            }
            std.mem.sort(Arm, arms, {}, struct {
                fn less(_: void, left: Arm, right: Arm) bool {
                    return lessUnits(left.variants[0], right.variants[0]);
                }
            }.less);
            var targets_list: List([]const u8) = .empty;
            for (arms) |*arm| {
                arm.block = self.roleBlock(node, fmt("variant/{s}", .{escapeData(arm.variants[0])}));
                for (arm.variants) |variant| {
                    var t: Obj = .{};
                    t.put("variantId", quoted(variant));
                    t.put("blockId", quotedAscii(arm.block.id));
                    targets_list.append(a, t.done()) catch oom();
                }
            }
            // The serializer sorts the targets by variant; arms in order list them in that order already
            // only when their variant sets do not interleave.
            const sorted = a.alloc(Keyed, targets_list.items.len) catch oom();
            var at: usize = 0;
            for (arms) |arm| for (arm.variants) |variant| {
                sorted[at] = .{ .key = variant, .text = targets_list.items[at] };
                at += 1;
            };
            var o: Obj = .{};
            o.put("kind", "\"matchUnion\"");
            o.put("inputValueId", quotedAscii(input));
            o.put("targets", sortedArray(sorted));
            block.terminator = o.done();
            const endpoints = a.alloc(?Endpoint, arms.len) catch oom();
            for (arms, endpoints) |arm, *endpoint| {
                var arm_env = env.clone();
                const fields = a.dupe(Value, arrayOf(arm.arm, "fieldBindings")) catch oom();
                std.mem.sort(Value, fields, {}, struct {
                    fn less(_: void, left: Value, right: Value) bool {
                        return lessUnits(str(left, "sourceFieldId"), str(right, "sourceFieldId"));
                    }
                }.less);
                for (fields) |field| {
                    std.debug.assert(arm.variants.len == 1);
                    var p: Obj = .{};
                    p.put("inputValueId", quotedAscii(input));
                    p.put("variantId", quoted(arm.variants[0]));
                    p.put("fieldId", quoted(str(field, "sourceFieldId")));
                    const binding = get(field, "binding");
                    const extracted = self.addPatternExtraction(arm.block, node, binding, "extractUnionField", p.done());
                    arm_env.patterns.put(a, idKey(str(binding, "id")), extracted) catch oom();
                }
                endpoint.* = try self.lowerGraph(arm.block, arm_env, is_tail, get(arm.arm, "body"));
            }
            return self.finishArms(env, node, merge, endpoints);
        }
        if (std.mem.eql(u8, kind, "matchOption")) {
            const input = try self.requireNode(env, str(node, "inputNodeId"));
            const some_block = self.roleBlock(node, "some");
            const none_block = self.roleBlock(node, "none");
            const merge = self.mergeFor(node);
            branch(block, "matchOption", "inputValueId", input, .{ "someBlock", "noneBlock" }, .{ some_block, none_block });
            const some = get(node, "someArm");
            const binding = get(some, "valueBinding");
            const extracted = self.addPatternExtraction(some_block, node, binding, "extractOptionSome", inputProperty(input));
            const some_end = try self.lowerGraph(some_block, withBinding(env, binding, extracted), is_tail, get(some, "body"));
            const none_end = try self.lowerGraph(none_block, env.clone(), is_tail, get(node, "noneBody"));
            return self.finishArms(env, node, merge, &.{ some_end, none_end });
        }
        std.debug.assert(std.mem.eql(u8, kind, "matchResult"));
        const input = try self.requireNode(env, str(node, "inputNodeId"));
        const ok_block = self.roleBlock(node, "ok");
        const err_block = self.roleBlock(node, "err");
        const merge = self.mergeFor(node);
        branch(block, "matchResult", "inputValueId", input, .{ "okBlock", "errBlock" }, .{ ok_block, err_block });
        const ok_arm = get(node, "okArm");
        const err_arm = get(node, "errArm");
        const ok_binding = get(ok_arm, "valueBinding");
        const err_binding = get(err_arm, "errorBinding");
        const ok_value = self.addPatternExtraction(ok_block, node, ok_binding, "extractResultOk", inputProperty(input));
        const err_value = self.addPatternExtraction(err_block, node, err_binding, "extractResultErr", inputProperty(input));
        const ok_end = try self.lowerGraph(ok_block, withBinding(env, ok_binding, ok_value), is_tail, get(ok_arm, "body"));
        const err_end = try self.lowerGraph(err_block, withBinding(env, err_binding, err_value), is_tail, get(err_arm, "body"));
        return self.finishArms(env, node, merge, &.{ ok_end, err_end });
    }

    fn inputProperty(input: []const u8) []const u8 {
        var o: Obj = .{};
        o.put("inputValueId", quotedAscii(input));
        return o.done();
    }

    /// `lowerGraph`: Kahn's algorithm over the graph's nodes; among ready nodes the lowest ID goes
    /// first, a Bytes update after every ready non-update, and the tail node last.
    fn lowerGraph(self: *Lowering, start: *Block, initial: Env, is_tail: bool, graph: Value) LowerFailure!?Endpoint {
        var env = initial.clone();
        var block = start;
        const nodes = arrayOf(graph, "nodes");
        var pending = byId(nodes);
        const tail_id: ?Str = if (is_tail) if (self.tailSelfCallNode(graph)) |n| str(n, "id") else null else null;
        var waiting: std.StringHashMapUnmanaged(usize) = .empty;
        var dependents: std.StringHashMapUnmanaged(List(Str)) = .empty;
        const Candidate = struct { update: bool, id: Str };
        var ready: List(Candidate) = .empty;
        for (nodes) |node| {
            var unresolved: usize = 0;
            for (dependenciesOf(node)) |dependency| {
                if (env.nodes.contains(idKey(dependency))) continue;
                unresolved += 1;
                if (pending.contains(idKey(dependency))) {
                    const entry = dependents.getOrPut(a, idKey(dependency)) catch oom();
                    if (!entry.found_existing) entry.value_ptr.* = .empty;
                    entry.value_ptr.append(a, str(node, "id")) catch oom();
                }
            }
            waiting.put(a, idKey(str(node, "id")), unresolved) catch oom();
            if (unresolved == 0) ready.append(a, .{ .update = isBytesUpdate(node), .id = str(node, "id") }) catch oom();
        }
        var last_taken: ?Str = null;
        while (pending.count() > 0) {
            if (last_taken) |taken| if (env.nodes.contains(idKey(taken))) {
                if (dependents.get(idKey(taken))) |list| for (list.items) |dependent| {
                    const count = waiting.getPtr(idKey(dependent)).?;
                    count.* -= 1;
                    if (count.* == 0) ready.append(a, .{ .update = isBytesUpdate(pending.get(idKey(dependent)).?), .id = dependent }) catch oom();
                };
            };
            // The first ready node in (update, ID) order that is not the tail node, unless it is the last.
            var best: ?usize = null;
            for (ready.items, 0..) |item, i| {
                if (tail_id != null and same(item.id, tail_id.?) and pending.count() != 1) continue;
                if (best == null or readyLess(item, ready.items[best.?])) best = i;
            }
            const chosen = best orelse {
                const ids = a.dupe([]const u8, pending.keys()) catch oom();
                std.mem.sort([]const u8, ids, {}, lessKey);
                var names: List(u8) = .empty;
                for (ids, 0..) |id, i| names.print(a, "{s}{s}", .{ if (i == 0) "" else ", ", text(keyId(id)) }) catch oom();
                return self.fail("UnresolvedDependencies", names.items);
            };
            const id = ready.swapRemove(chosen).id;
            last_taken = id;
            const node = pending.get(idKey(id)).?;
            _ = pending.swapRemove(idKey(id));
            const kind = text(str(node, "kind"));
            const node_tail = tail_id != null and same(id, tail_id.?);
            if (std.mem.eql(u8, kind, "abort")) {
                const error_value = try self.requireNode(&env, str(node, "errorNodeId"));
                var o: Obj = .{};
                o.put("kind", "\"abort\"");
                o.put("originNodeId", quoted(id));
                o.put("errorValueId", quotedAscii(error_value));
                block.terminator = o.done();
                return null;
            } else if (kindIs(kind, "conditional boolAnd boolOr matchUnion bind matchOption matchResult")) {
                block = try self.lowerStructured(&env, block, node, node_tail) orelse return null;
            } else if (node_tail and std.mem.eql(u8, kind, "directCall")) {
                var o: Obj = .{};
                o.put("kind", "\"tailCall\"");
                o.put("originNodeId", quoted(id));
                o.put("arguments", try self.targets(&env, arrayOf(node, "arguments"), "targetParameterId"));
                block.terminator = o.done();
                return null;
            } else try self.lowerSimple(&env, block, node);
        }
        return .{ .block = block, .value = try self.requireNode(&env, str(graph, "resultNodeId")) };
    }

    fn readyLess(left: anytype, right: anytype) bool {
        if (left.update != right.update) return !left.update;
        return lessUnits(left.id, right.id);
    }
};

fn isBytesUpdate(node: Value) bool {
    return kindIs(text(str(node, "kind")), "bytesSet bytesAppend");
}

fn executionFunction(function: Value, resolver: *Resolver, observable: *const Set) LowerFailure![]const u8 {
    var lowering: Lowering = .{ .function = function, .resolver = resolver, .parameters_by_id = byId(arrayOf(function, "parameters")) };
    if (unorderedEffects(arrayOf(get(function, "body"), "nodes"), observable, true)) |pair| return lowering.fail("AmbiguousEffectOrder", fmt("{s} {s}", .{ text(pair[0]), text(pair[1]) }));
    const entry = lowering.addBlock(fmt("{s}/entry", .{executionId("function", str(function, "id"))}), &.{});
    if (try lowering.lowerGraph(entry, .{}, true, get(function, "body"))) |end| {
        var o: Obj = .{};
        o.put("kind", "\"return\"");
        o.put("valueId", quotedAscii(end.value));
        end.block.terminator = o.done();
    }
    const ids = a.dupe([]const u8, lowering.blocks.keys()) catch oom();
    std.mem.sort([]const u8, ids, {}, lessAscii);
    const blocks = a.alloc([]const u8, ids.len) catch oom();
    for (ids, blocks) |id, *out| {
        const block = lowering.blocks.get(id).?;
        var o: Obj = .{};
        o.put("id", quotedAscii(block.id));
        o.put("parameters", array(block.parameters));
        o.put("instructions", array(block.instructions.items));
        o.put("terminator", block.terminator orelse return lowering.fail("UnterminatedBlock", block.id));
        out.* = o.done();
    }
    const parameters = arrayOf(function, "parameters");
    const parameter_texts = a.alloc([]const u8, parameters.len) catch oom();
    for (parameters, parameter_texts) |parameter, *out| {
        var origin: Obj = .{};
        origin.put("kind", "\"functionParameter\"");
        origin.put("parameterId", quoted(str(parameter, "id")));
        var o: Obj = .{};
        o.put("semanticParameterId", quoted(str(parameter, "id")));
        o.put("value", executionValue(executionId("parameter", str(parameter, "id")), typedTypeOf(get(parameter, "type")), origin.done()));
        out.* = o.done();
    }
    var o: Obj = .{};
    o.put("semanticFunctionId", quoted(str(function, "id")));
    typedTypeParameters(&o, function);
    o.put("parameters", array(parameter_texts));
    o.put("resultType", typedTypeOf(get(function, "resultType")));
    o.put("requiredEffects", sortedIds(arrayOf(function, "requiredEffects")));
    o.put("entryBlockId", quotedAscii(entry.id));
    o.put("blocks", array(blocks));
    return o.done();
}

/// `ExecutionIrDeclarations.projectType` as written: the type parameters after the members.
fn executionDeclaredType(declaration: Value) []const u8 {
    var o: Obj = .{};
    o.put("id", quoted(str(declaration, "id")));
    if (isName(str(declaration, "kind"), "product")) {
        o.put("kind", "\"product\"");
        o.put("fields", typedSlots(arrayOf(declaration, "fields"), true));
    } else {
        o.put("kind", "\"union\"");
        const variants = arrayOf(declaration, "variants");
        const out = a.alloc(Keyed, variants.len) catch oom();
        for (variants, out) |variant, *v| {
            var obj: Obj = .{};
            obj.put("id", quoted(str(variant, "id")));
            obj.put("fields", typedSlots(arrayOf(variant, "fields"), true));
            v.* = .{ .key = str(variant, "id"), .text = obj.done() };
        }
        o.put("variants", sortedArray(out));
    }
    typedTypeParameters(&o, declaration);
    return o.done();
}

/// One line per issue, then `end`; a message is escaped as the reader's errors are (`oneLine`).
var resolved_input = false;

/// `--validate --dependency <dependency.json> <local.json>`: the local snapshot resolved against its
/// dependency, validated as `SnapshotCodec.deserializeResolved` validates it: the dependency's
/// declarations first, then the local ones. F# checks the marker, its digest and the local construct
/// minimums; a dependency at another schema version is left to F# (`skip`).
fn validateResolved(io: std.Io, out: *std.Io.Writer, dependency_path: []const u8, local_path: []const u8) !void {
    emit_text = false;
    const dependency = try std.Io.Dir.cwd().readFileAlloc(io, dependency_path, a, .unlimited);
    _ = canonicalJson(dependency) catch return out.print("skip dependency unreadable\n", .{});
    const dependency_root = parsed_root;
    const dependency_version = declared_version;
    const local = try std.Io.Dir.cwd().readFileAlloc(io, local_path, a, .unlimited);
    resolved_input = true;
    _ = canonicalJson(local) catch |e| return switch (e) {
        error.Json => out.print("error InvalidJson: {s}\n", .{message}),
        error.Shape => out.print("error InvalidShape: {s}\n", .{message}),
        error.Version => out.print("error UnsupportedSchemaVersion: {d}\n", .{declared_version}),
        error.Legacy => out.print("skip legacy schema {d}\n", .{declared_version}),
    };
    if (declared_version != dependency_version) return out.print("skip dependency schema {d}\n", .{dependency_version});
    var members: List(Member) = .empty;
    for (parsed_root.object) |member| {
        if (isName(member.name, "dependency")) continue;
        var value = member.value;
        inline for (.{ "types", "effects", "capabilities", "functions", "obligations" }) |name| {
            if (isName(member.name, name)) value = .{ .array = std.mem.concat(a, Value, &.{ arrayOf(dependency_root, name), member.value.array }) catch oom() };
        }
        members.append(a, .{ .name = member.name, .value = value }) catch oom();
    }
    validate(.{ .object = members.items });
    try printIssues(out);
}

fn printIssues(out: *std.Io.Writer) !void {
    for (issues.items) |issue| try out.print("issue {s}: {s}\n", .{ issue.code, oneLine(text(issue.message)) });
    try out.writeAll("end\n");
}

/// Every global back to its initial value and the tables loaded again: a fresh state for the
/// next document in one process (`--bench`, the session host).
fn resetState() void {
    message = "";
    declared_version = 0;
    emit_text = true;
    rows = .empty;
    used = &.{};
    first_supported = 0;
    first_record = 0;
    current = 0;
    wait_row = null;
    bytes_row = null;
    kinds = .empty;
    issues = .empty;
    parsed_root = .json_null;
    effect_names = .empty;
    types_by_id = .empty;
    all_types = &.{};
    capability_ids = .empty;
    type_parameter_names = .empty;
    variant_owners = .empty;
    functions_by_id = .empty;
    operations_by_id = .empty;
    product_fields_by_id = .empty;
    product_field_owners = .empty;
    all_capabilities = &.{};
    requirements_by_kind = null;
    variants_by_id = .empty;
    operation_owners = .empty;
    open_obligation_pairs = .empty;
    order_depth = 0;
    dependencies_memo = .empty;
    observable_memo = .empty;
    observable_functions = .empty;
    one_shot_requirements = .empty;
    task_bound_capabilities = .empty;
    validated_input = false;
    lowering_failure = "";
    instruction_names = .empty;
    resolved_input = false;
    write_version = null;
    neutral = false;
    legacy_names = .empty;
    expected_marker = null;
    write_marker = null;
    loadTables();
    loadInstructionNames();
}

/// The canonical text the F# writer gives a snapshot document at the current schema version
/// (`SnapshotCodec.serialize`, or `serializeResolved` for a resolved document's local part with
/// its dependency marker), and, for a plain document, its fingerprints.
pub const Written = struct { text: []const u8, fingerprints: ?Fingerprints };
pub fn writtenText(allocator: std.mem.Allocator, source: []const u8) Failure!Written {
    a = allocator;
    resetState();
    // Nothing of this document's form outlives the call (the host reads its model with the same globals).
    defer {
        resolved_input = false;
        write_marker = null;
    }
    const root = try parseDocument(source);
    if (tryProperty("dependency", root)) |marker| {
        resolved_input = true;
        var o: Obj = .{};
        o.put("snapshotId", quoted(try readString("snapshotId", marker)));
        o.put("digest", quoted(try readString("digest", marker)));
        write_marker = o.done();
    }
    const text_out = try canonicalOf(root);
    if (resolved_input) return .{ .text = text_out, .fingerprints = null };
    write_marker = null;
    return .{ .text = text_out, .fingerprints = try fingerprintsOf(root) };
}

pub fn currentVersion() i32 {
    if (current == 0) loadTables();
    return current;
}

/// `--typed-roundtrip <in.json> <out.json>`: the document read into the typed model and written
/// back (`writtenText` of `typed.writeDocument`), then `snapshot`/`semantic` lines for a plain one.
fn typedRoundTrip(io: std.Io, out: *std.Io.Writer, in_path: []const u8, out_path: []const u8) !void {
    const typed = @import("typed.zig");
    const input = try std.Io.Dir.cwd().readFileAlloc(io, in_path, a, .unlimited);
    resetState();
    const root = parseDocument(input) catch |e| return printFailure(out, e);
    resolved_input = root == .object and tryProperty("dependency", root) != null;
    // The read checks the marker's shape before it is taken.
    const canonical = canonicalOf(root) catch |e| return printFailure(out, e);
    const marker: ?typed.Marker = if (resolved_input) .{ .snapshot = decodeString(str(get(root, "dependency"), "snapshotId")) catch unreachable, .digest = decodeString(str(get(root, "dependency"), "digest")) catch unreachable } else null;
    const tree = parseDocument(canonical) catch unreachable;
    var interner: typed.Interner = .{ .gpa = a };
    const generation = try typed.load(a, &interner, tree, marker);
    var document: std.Io.Writer.Allocating = .init(a);
    try typed.writeDocument(&document.writer, &interner, &generation, currentVersion());
    const written = writtenText(a, document.written()) catch |e| return printFailure(out, e);
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = out_path, .data = written.text });
    if (written.fingerprints) |found| try out.print("snapshot {s}\nsemantic {s}\n", .{ found.snapshot, found.semantic }) else try out.writeAll("resolved\n");
}

/// `--bench <runs> --validate|--execution-ir <in.json>`: the median of `runs` in-process runs after
/// `runs` warm-up runs, in ms, on stderr, with the input read once; each run starts from a fresh arena and
/// fresh state (the tables are loaded before the clock starts), and must give the first run's output.
fn bench(io: std.Io, runs: usize, lowering: bool, path: []const u8) !void {
    const input = try std.Io.Dir.cwd().readFileAlloc(io, path, std.heap.page_allocator, .unlimited);
    var first: ?[]const u8 = null;
    const times = try std.heap.page_allocator.alloc(i96, runs);
    for (0..2 * runs) |run| {
        var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
        defer arena.deinit();
        a = arena.allocator();
        resetState();
        var out: std.Io.Writer.Allocating = .init(a);
        const start = std.Io.Timestamp.now(io, .awake);
        emit_text = false;
        const read = canonicalJson(input);
        emit_text = true;
        if (read) |_| {
            if (lowering) {
                try out.writer.writeAll(executionIr(parsed_root) catch lowering_failure);
            } else {
                validate(parsed_root);
                try printIssues(&out.writer);
            }
        } else |_| try out.writer.writeAll(message);
        const took = start.durationTo(std.Io.Timestamp.now(io, .awake)).toNanoseconds();
        if (first) |expected| {
            if (!std.mem.eql(u8, expected, out.written())) @panic("a bench run gave another output");
        } else first = try std.heap.page_allocator.dupe(u8, out.written());
        if (run >= runs) times[run - runs] = took;
    }
    std.mem.sort(i96, times, {}, std.sort.asc(i96));
    std.debug.print("{d:.2}\n", .{@as(f64, @floatFromInt(times[runs / 2])) / 1e6});
}

pub fn main(init: std.process.Init) !void {
    a = init.arena.allocator();
    var args = try init.minimal.args.toSlice(a);
    if (args.len == 5 and std.mem.eql(u8, args[1], "--bench"))
        return bench(init.io, try std.fmt.parseInt(usize, args[2], 10), std.mem.eql(u8, args[3], "--execution-ir"), args[4]);
    const validating = args.len >= 2 and std.mem.eql(u8, args[1], "--validate");
    const lowering = args.len >= 2 and std.mem.eql(u8, args[1], "--execution-ir");
    // `--execution-ir --validated <in> <out> <entry> ...`: the inputs already passed `--validate`.
    validated_input = lowering and args.len >= 3 and std.mem.eql(u8, args[2], "--validated");
    if (validated_input) {
        const rest = try a.alloc(@TypeOf(args[0]), args.len - 1);
        rest[0] = args[0];
        rest[1] = args[1];
        @memcpy(rest[2..], args[3..]);
        args = rest;
    }
    const typing = lowering or args.len >= 2 and std.mem.eql(u8, args[1], "--typed-ir");
    const round_trip = args.len == 4 and std.mem.eql(u8, args[1], "--typed-roundtrip");
    if (!round_trip and if (validating) args.len < 3 else if (typing) args.len < 5 or (args.len - 2) % 3 != 0 else args.len < 3 or args.len % 2 == 0) {
        std.debug.print("usage: model-reader <in.json> <out.json> [<in> <out> ...] | model-reader --validate <in.json> [<in.json> ...] | model-reader --typed-ir|--execution-ir <in.json> <out.json> <entry|-> [...] | model-reader --fingerprints <in.json> [<canonical.json> <normalized.json>] | model-reader --typed-roundtrip <in.json> <out.json>\n", .{});
        std.process.exit(64);
    }
    loadTables();
    loadInstructionNames();
    var buffer: [4096]u8 = undefined;
    var writer = std.Io.File.stdout().writer(init.io, &buffer);
    const out = &writer.interface;
    // `--fingerprints <in.json> [<canonical.json> <normalized.json>]`: the two fingerprint lines of a
    // valid snapshot, and optionally the texts they hash.
    if (args.len >= 3 and std.mem.eql(u8, args[1], "--fingerprints")) {
        const input = try std.Io.Dir.cwd().readFileAlloc(init.io, args[2], a, .unlimited);
        const result = fingerprints(input) catch |e| {
            try out.print("error {s}\n", .{@errorName(e)});
            return out.flush();
        };
        if (args.len == 5) {
            try std.Io.Dir.cwd().writeFile(init.io, .{ .sub_path = args[3], .data = result.canonical });
            try std.Io.Dir.cwd().writeFile(init.io, .{ .sub_path = args[4], .data = result.normalized });
        }
        try out.print("snapshot {s}\nsemantic {s}\n", .{ result.snapshot, result.semantic });
        return out.flush();
    }
    if (round_trip) {
        try typedRoundTrip(init.io, out, args[2], args[3]);
        return out.flush();
    }
    if (validating and args.len == 5 and std.mem.eql(u8, args[2], "--dependency")) {
        try validateResolved(init.io, out, args[3], args[4]);
        return out.flush();
    }
    var index: usize = if (validating or typing) 2 else 1;
    while (index < args.len) : (index += if (validating) 1 else if (typing) 3 else 2) {
        const input = try std.Io.Dir.cwd().readFileAlloc(init.io, args[index], a, .unlimited);
        emit_text = !(validating or typing);
        const read = canonicalJson(input);
        emit_text = true;
        if (read) |json| {
            if (typing) {
                const entry = args[index + 2];
                const root = if (std.mem.eql(u8, entry, "-")) parsed_root else selectEntry(parsed_root, try std.unicode.utf8ToUtf16LeAlloc(a, entry));
                if (if (lowering) executionIr(root) else typedIr(root)) |typed| {
                    try std.Io.Dir.cwd().writeFile(init.io, .{ .sub_path = args[index + 1], .data = typed });
                    try out.print("ok\n", .{});
                } else |_| try out.print("error {s}\n", .{lowering_failure});
            } else if (validating) {
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
