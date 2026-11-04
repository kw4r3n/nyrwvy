//! The Nyrwvy snapshot reader, first stage: record text (`.nyrec`) to the snapshot JSON the strict
//! writer produces, with every record-form check of the F# `RecordCodec`, which is its reference
//! (`RecordCodec.toJsonDeferred`). The model reader (`tools/zig/model-reader`) validates that JSON.
//! See docs/decisions/zig-tools.md.
//!
//! Usage: `snapshot-reader <in.nyrec> <out.json> [<in> <out> ...]`. One line per pair on standard
//! output: `ok`; `deferred <line>:<column>: <message>` when the JSON was written but the `position=`
//! placement error stands unless the model check fails first; `error MaximumDepthExceeded: 1000`;
//! or `error <line>:<column>: <message>` (columns count UTF-16 code units, as the F# reader does).
//! Exit 0 unless the arguments or files cannot be used.
//!
//! `record_kinds.txt` beside this file is the per-kind key table, shared with the F# codec.
const std = @import("std");

const kind_table_text = @embedFile("record_kinds.txt");
/// `SnapshotSchema.firstRecord` and `SnapshotSchema.current`; a fast test compares them.
const schema_first_record = 21;
const schema_current = 24;
const max_json_depth = 1000;

var a: std.mem.Allocator = undefined;

fn oom() noreturn {
    @panic("out of memory");
}

fn eql(left: []const u8, right: []const u8) bool {
    return std.mem.eql(u8, left, right);
}

fn List(comptime T: type) type {
    return std.ArrayList(T);
}

fn fmt(comptime format: []const u8, args: anytype) []const u8 {
    return std.fmt.allocPrint(a, format, args) catch oom();
}

fn less(left: []const u8, right: []const u8) bool {
    return std.mem.order(u8, left, right) == .lt;
}

// ---------------------------------------------------------------- failures

const Failure = struct { line: usize, column: usize, message: []const u8 };
var failure: Failure = undefined;
const RecordError = error{Record};

fn fail(line: usize, col: usize, message: []const u8) RecordError {
    failure = .{ .line = line, .column = col, .message = message };
    return error.Record;
}

// ---------------------------------------------------------------- the kind table

const Shape = union(enum) {
    meta,
    id: []const u8,
    bool,
    int,
    str,
    graph,
    value_arm: []const u8,
    arms,
    type_expr,
    type_list,
    id_list: []const u8,
    pairs: [4][]const u8,
    members: []const u8,
};

const Key = struct { json: []const u8, record: []const u8, shape: Shape, optional: bool, omit_empty: bool };
const Kind = struct { name: []const u8, json_kind: ?[]const u8, keys: []const Key };

var kinds: std.StringHashMap(Kind) = undefined;
var member_kinds: std.StringHashMap(void) = undefined;

fn parseShape(s: []const u8) Shape {
    const paren = std.mem.indexOfScalar(u8, s, '(');
    const head = if (paren) |p| s[0..p] else s;
    const inner = if (paren) |p| s[p + 1 .. s.len - 1] else "";
    if (eql(head, "id")) return .{ .id = inner };
    if (eql(head, "bool")) return .bool;
    if (eql(head, "int")) return .int;
    if (eql(head, "str")) return .str;
    if (eql(head, "graph")) return .graph;
    if (eql(head, "arm")) return .{ .value_arm = inner };
    if (eql(head, "arms")) return .arms;
    if (eql(head, "type")) return .type_expr;
    if (eql(head, "types")) return .type_list;
    if (eql(head, "ids")) return .{ .id_list = inner };
    if (eql(head, "members")) return .{ .members = inner };
    if (eql(head, "pairs")) {
        const comma = std.mem.indexOfScalar(u8, inner, ',').?;
        const first = inner[0..comma];
        const second = inner[comma + 1 ..];
        const c1 = std.mem.indexOfScalar(u8, first, ':').?;
        const c2 = std.mem.indexOfScalar(u8, second, ':').?;
        return .{ .pairs = .{ first[0..c1], first[c1 + 1 ..], second[0..c2], second[c2 + 1 ..] } };
    }
    std.debug.panic("record_kinds.txt: unknown shape '{s}'", .{head});
}

fn loadKinds() void {
    if (kinds_loaded) return;
    // Both entry points reuse these immutable tables across request arenas.
    const table_allocator = std.heap.page_allocator;
    kinds = .init(table_allocator);
    member_kinds = .init(table_allocator);
    var lines = std.mem.tokenizeScalar(u8, kind_table_text, '\n');
    while (lines.next()) |line| {
        const colon = std.mem.indexOf(u8, line, ": ").?;
        const head = line[0..colon];
        const eq_at = std.mem.indexOfScalar(u8, head, '=');
        var keys: List(Key) = .empty;
        var tokens = std.mem.splitScalar(u8, line[colon + 2 ..], ' ');
        while (tokens.next()) |whole| {
            if (eql(whole, "id") or eql(whole, "kind") or eql(whole, "typeParameterId")) {
                keys.append(table_allocator, .{ .json = whole, .record = whole, .shape = .meta, .optional = false, .omit_empty = false }) catch oom();
                continue;
            }
            var token = whole;
            const omit_empty = std.mem.endsWith(u8, token, "~");
            token = std.mem.trimEnd(u8, token, "~");
            const optional = std.mem.endsWith(u8, token, "?");
            token = std.mem.trimEnd(u8, token, "?");
            const slash = std.mem.indexOfScalar(u8, token, '/').?;
            const shape_at = std.mem.indexOfScalar(u8, token, ':').?;
            const shape = parseShape(token[shape_at + 1 ..]);
            if (shape == .members) member_kinds.put(shape.members, {}) catch oom();
            keys.append(table_allocator, .{ .json = token[0..slash], .record = token[slash + 1 .. shape_at], .shape = shape, .optional = optional, .omit_empty = omit_empty }) catch oom();
        }
        const name = if (eq_at) |i| head[0..i] else head;
        kinds.put(name, .{ .name = name, .json_kind = if (eq_at) |i| head[i + 1 ..] else null, .keys = keys.items }) catch oom();
    }
    kinds_loaded = true;
}

/// Declaration groups in schema order: the JSON collection and the subject domain.
const groups = [_][]const u8{ "types", "effects", "capabilities", "functions", "obligations" };

fn groupOfKind(kind: []const u8) ?usize {
    const table = .{ .{ "product", 0 }, .{ "union", 0 }, .{ "effect", 1 }, .{ "capability", 2 }, .{ "function", 3 }, .{ "missing-argument", 4 } };
    inline for (table) |row| if (eql(kind, row[0])) return row[1];
    return null;
}

fn inSet(item: []const u8, comptime set: []const []const u8) bool {
    inline for (set) |member| if (eql(item, member)) return true;
    return false;
}

/// Collections whose order is semantic and whose records carry `position=`.
const ordered = [_][]const u8{ "parameters", "typeParameters" };
const declaration_domains = [_][]const u8{ "type", "effect", "capability", "function", "obligation" };
/// ID lists the writer sorts by ID.
const sorted_id_lists = [_][]const u8{"requiredEffects"};
const member_domains = [_][]const u8{ "node", "binding", "parameter", "capability-parameter", "type-parameter", "field", "variant", "operation", "type", "effect", "capability", "function", "obligation" };
const primitives = [_][]const u8{ "string", "bool", "unit", "int64", "float64", "bits32", "bytes" };

fn genericJson(name: []const u8) ?[]const u8 {
    const table = .{ .{ "option", "option" }, .{ "result", "result" }, .{ "map", "map" }, .{ "list", "list" }, .{ "set", "set" }, .{ "one-shot", "oneShot" }, .{ "task", "task" }, .{ "exception", "exception" }, .{ "task-scope", "taskScope" } };
    inline for (table) |row| if (eql(name, row[0])) return row[1];
    return null;
}

/// The ID domain of a record's subject.
fn subjectDomain(kind: []const u8, owner: ?[]const u8) []const u8 {
    if (eql(kind, "parameter")) return if (owner != null and std.mem.startsWith(u8, owner.?, "operation:")) "capability-parameter" else "parameter";
    if (inSet(kind, &.{ "type-parameter", "field", "variant", "operation", "binding", "effect", "capability", "function" })) return kind;
    if (eql(kind, "product") or eql(kind, "union")) return "type";
    if (eql(kind, "missing-argument")) return "obligation";
    return "node";
}

// ---------------------------------------------------------------- token grammar

fn isHex(c: u8) bool {
    return (c >= '0' and c <= '9') or (c >= 'a' and c <= 'f');
}

fn isDomainChar(c: u8) bool {
    return (c >= 'a' and c <= 'z') or c == '-';
}

fn isUuid(s: []const u8) bool {
    if (s.len != 36) return false;
    for (s, 0..) |c, i| {
        if (i == 8 or i == 13 or i == 18 or i == 23) {
            if (c != '-') return false;
        } else if (!isHex(c)) return false;
    }
    return true;
}

fn isId(s: []const u8) bool {
    const colon = std.mem.indexOfScalar(u8, s, ':') orelse return false;
    if (colon == 0 or s.len != colon + 37 or !isUuid(s[colon + 1 ..])) return false;
    for (s[0..colon]) |c| if (!isDomainChar(c)) return false;
    return true;
}

fn hasDomain(s: []const u8, domain: []const u8) bool {
    return isId(s) and s.len == domain.len + 37 and std.mem.startsWith(u8, s, domain);
}

/// `id`, `id/role` or `id/arm/variant:uuid`.
fn isSubjectSyntax(s: []const u8) bool {
    const slash = std.mem.indexOfScalar(u8, s, '/') orelse return isId(s);
    if (!isId(s[0..slash])) return false;
    const rest = s[slash + 1 ..];
    const slash2 = std.mem.indexOfScalar(u8, rest, '/');
    const role = if (slash2) |i| rest[0..i] else rest;
    if (role.len == 0) return false;
    for (role) |c| if (!isDomainChar(c)) return false;
    return slash2 == null or hasDomain(rest[slash2.? + 1 ..], "variant");
}

/// The integer spelling of Utf8JsonWriter: no sign on zero, no leading zeros, no plus.
fn isCanonicalInt(s: []const u8) bool {
    const digits = if (s.len > 0 and s[0] == '-') s[1..] else s;
    var ok = digits.len > 0 and (digits.len == 1 or digits[0] != '0') and !(digits.len == 1 and digits[0] == '0' and s.len == 2);
    for (digits) |c| if (c < '0' or c > '9') {
        ok = false;
    };
    return ok;
}

fn isHexAny(c: u8) bool {
    return isHex(c) or (c >= 'A' and c <= 'F');
}

/// The JSON string-literal grammar for the escapes and control characters between the quotes.
fn validStringBody(source: []const u8, start: usize, stop: usize) bool {
    var i = start;
    while (i < stop) {
        const c = source[i];
        if (c < ' ') return false;
        if (c != '\\') {
            i += 1;
            continue;
        }
        if (i + 1 >= stop) return false;
        switch (source[i + 1]) {
            '"', '\\', '/', 'b', 'f', 'n', 'r', 't' => i += 2,
            'u' => {
                if (!(i + 5 < stop and isHexAny(source[i + 2]) and isHexAny(source[i + 3]) and isHexAny(source[i + 4]) and isHexAny(source[i + 5]))) return false;
                i += 6;
            },
            else => return false,
        }
    }
    return true;
}

/// Whether a string literal is spelled as the writer's encoder (JavaScriptEncoder.Default) spells its
/// content: `LooseJsonString.encode` of `LooseJsonString.decodeRaw`. That encoder escapes every
/// non-ASCII UTF-16 unit as `\uXXXX`, so a canonical literal is ASCII.
fn isCanonicalString(raw: []const u8) bool {
    var units: List(u16) = .empty;
    var i: usize = 1;
    const last = raw.len - 1;
    while (i < last) {
        if (raw[i] == '\\') {
            const escaped = raw[i + 1];
            i += 2;
            switch (escaped) {
                'b' => units.append(a, 8) catch oom(),
                'f' => units.append(a, 12) catch oom(),
                'n' => units.append(a, 10) catch oom(),
                'r' => units.append(a, 13) catch oom(),
                't' => units.append(a, 9) catch oom(),
                'u' => {
                    units.append(a, std.fmt.parseInt(u16, raw[i .. i + 4], 16) catch unreachable) catch oom();
                    i += 4;
                },
                else => units.append(a, escaped) catch oom(),
            }
        } else {
            const size = std.unicode.utf8ByteSequenceLength(raw[i]) catch 1;
            const point = std.unicode.utf8Decode(raw[i .. i + size]) catch raw[i];
            if (point >= 0x10000) {
                units.append(a, @intCast(0xD800 + ((point - 0x10000) >> 10))) catch oom();
                units.append(a, @intCast(0xDC00 + ((point - 0x10000) & 0x3FF))) catch oom();
            } else units.append(a, @intCast(point)) catch oom();
            i += size;
        }
    }
    var encoded: List(u8) = .empty;
    encoded.append(a, '"') catch oom();
    for (units.items) |unit| escapeUnit(&encoded, unit);
    encoded.append(a, '"') catch oom();
    return eql(encoded.items, raw);
}

/// One UTF-16 unit as JavaScriptEncoder.Default writes it.
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

/// Columns count UTF-16 code units from the line start, as the F# reader's string indices do.
fn column(source: []const u8, line_start: usize, at: usize) usize {
    var units: usize = 1;
    for (source[line_start..at]) |b| {
        if (b & 0xC0 != 0x80) units += 1;
        if (b >= 0xF0) units += 1;
    }
    return units;
}

// ---------------------------------------------------------------- the writer

/// The compact JSON Utf8JsonWriter writes, with its default encoder and maximum depth.
const Writer = struct {
    out: List(u8) = .empty,
    /// Per open container: whether it has an item yet.
    stack: List(bool) = .empty,
    after_name: bool = false,

    fn separate(self: *Writer) void {
        if (self.after_name) {
            self.after_name = false;
            return;
        }
        if (self.stack.items.len > 0) {
            if (self.stack.items[self.stack.items.len - 1]) self.out.append(a, ',') catch oom();
            self.stack.items[self.stack.items.len - 1] = true;
        }
    }

    fn quoted(self: *Writer, content: []const u8) void {
        self.out.append(a, '"') catch oom();
        var view = std.unicode.Utf8View.initUnchecked(content);
        var points = view.iterator();
        while (points.nextCodepoint()) |point| {
            if (point >= 0x10000) {
                escapeUnit(&self.out, @intCast(0xD800 + ((point - 0x10000) >> 10)));
                escapeUnit(&self.out, @intCast(0xDC00 + ((point - 0x10000) & 0x3FF)));
            } else escapeUnit(&self.out, @intCast(point));
        }
        self.out.append(a, '"') catch oom();
    }

    fn name(self: *Writer, key: []const u8) void {
        self.separate();
        self.quoted(key);
        self.out.append(a, ':') catch oom();
        self.after_name = true;
    }

    fn open(self: *Writer, key: ?[]const u8, bracket: u8) RecordError!void {
        if (self.stack.items.len >= max_json_depth) {
            depth_exceeded = true;
            return fail(1, 1, fmt("Maximum JSON depth of {d} exceeded.", .{max_json_depth}));
        }
        if (key) |k| self.name(k);
        self.separate();
        self.out.append(a, bracket) catch oom();
        self.stack.append(a, false) catch oom();
    }

    fn startObject(self: *Writer, key: ?[]const u8) RecordError!void {
        return self.open(key, '{');
    }

    fn startArray(self: *Writer, key: ?[]const u8) RecordError!void {
        return self.open(key, '[');
    }

    fn end(self: *Writer, bracket: u8) void {
        _ = self.stack.pop();
        self.out.append(a, bracket) catch oom();
    }

    fn string(self: *Writer, key: ?[]const u8, item: []const u8) void {
        if (key) |k| self.name(k);
        self.separate();
        self.quoted(item);
    }

    fn raw(self: *Writer, key: ?[]const u8, item: []const u8) void {
        if (key) |k| self.name(k);
        self.separate();
        self.out.appendSlice(a, item) catch oom();
    }
};

var w: Writer = .{};

// ---------------------------------------------------------------- rows

const Field = struct { key: []const u8, start: usize, len: usize, column: usize };

const Record = struct {
    line: usize,
    /// The subject's column.
    column: usize,
    end_column: usize,
    kind: []const u8,
    subject: []const u8,
    owner: ?[]const u8,
    fields: []Field,
    /// The declaration block (consecutive lines) the record sits in.
    block: *List(*Record),
    consumed: bool = false,
};

var text: []const u8 = undefined;

/// Splits `kind subject key=value ...`; values are a JSON string literal, a bracket list or a bare token.
fn parseLine(line: usize, line_start: usize, line_end: usize, block: *List(*Record)) RecordError!*Record {
    const n = line_end;
    var i = line_start;
    const S = struct {
        fn token(index: *usize, end: usize, l: usize, start_of_line: usize) RecordError!usize {
            const start = index.*;
            while (index.* < end and text[index.*] != ' ') index.* += 1;
            if (index.* == start) return fail(l, column(text, start_of_line, start), "empty token");
            return start;
        }
        fn space(index: *usize, end: usize, l: usize, start_of_line: usize) RecordError!void {
            if (index.* < end) {
                if (text[index.*] != ' ') return fail(l, column(text, start_of_line, index.*), "expected a space");
                index.* += 1;
                if (index.* >= end or text[index.*] == ' ') return fail(l, column(text, start_of_line, index.*), "single spaces only, no trailing space");
            }
        }
    };
    const kind_start = try S.token(&i, n, line, line_start);
    for (text[kind_start..i]) |c| {
        if (!((c >= 'a' and c <= 'z') or (c >= '0' and c <= '9') or c == '-')) return fail(line, column(text, line_start, kind_start), fmt("bad record kind '{s}'", .{text[kind_start..i]}));
    }
    const kind = text[kind_start..i];
    try S.space(&i, n, line, line_start);
    const subject_start = try S.token(&i, n, line, line_start);
    const subject = text[subject_start..i];
    var fields: List(Field) = .empty;
    while (i < n) {
        try S.space(&i, n, line, line_start);
        const start = i;
        while (i < n and text[i] != '=' and text[i] != ' ') i += 1;
        if (i >= n or text[i] != '=') return fail(line, column(text, line_start, start), "expected key=value");
        if (i == start) return fail(line, column(text, line_start, start), "empty key");
        const key = text[start..i];
        i += 1;
        const vstart = i;
        if (i < n and text[i] == '"') {
            i += 1;
            while (i < n and text[i] != '"') {
                if (text[i] == '\\') i += 1;
                i += 1;
            }
            if (i >= n) return fail(line, column(text, line_start, vstart), "unterminated string");
            i += 1;
            if (!validStringBody(text, vstart + 1, i - 1)) return fail(line, column(text, line_start, vstart), "bad string literal");
        } else if (i < n and text[i] == '[') {
            while (i < n and text[i] != ']') i += 1;
            if (i >= n) return fail(line, column(text, line_start, vstart), "unterminated list");
            i += 1;
        } else {
            while (i < n and text[i] != ' ') i += 1;
            if (i == vstart) return fail(line, column(text, line_start, vstart), fmt("empty value for {s}", .{key}));
        }
        fields.append(a, .{ .key = key, .start = vstart, .len = i - vstart, .column = column(text, line_start, start) }) catch oom();
    }
    for (fields.items, 0..) |first, at| {
        for (fields.items[at + 1 ..]) |second| if (eql(first.key, second.key)) return fail(line, second.column, "duplicate key");
    }
    const owner = if (fields.items.len > 0 and eql(fields.items[0].key, "in")) value(fields.items[0]) else null;
    const record = a.create(Record) catch oom();
    record.* = .{ .line = line, .column = column(text, line_start, subject_start), .end_column = column(text, line_start, n), .kind = kind, .subject = subject, .owner = owner, .fields = fields.items, .block = block };
    return record;
}

fn value(f: Field) []const u8 {
    return text[f.start .. f.start + f.len];
}

// ---------------------------------------------------------------- type expressions

fn writeType(line: usize, col: usize, start: usize, len: usize) RecordError!void {
    const s = text[start .. start + len];
    const bad = struct {
        fn of(l: usize, c: usize, expression: []const u8) RecordError {
            return fail(l, c, fmt("bad type expression '{s}'", .{expression}));
        }
    }.of;
    if (len == 0) return bad(line, col, s);
    if (s[0] == '(') {
        var depth: i64 = 0;
        var i: usize = 0;
        var close: ?usize = null;
        while (close == null and i < len) : (i += 1) {
            if (s[i] == '(') depth += 1 else if (s[i] == ')') depth -= 1;
            if (depth == 0) close = i;
        }
        if (close == null or close.? + 3 >= len or s[close.? + 1] != '-' or s[close.? + 2] != '>') return bad(line, col, s);
        try w.startObject(null);
        w.string("kind", "function");
        try w.startArray("parameters");
        try writeArgs(line, col, start + 1, close.? - 1);
        w.end(']');
        w.name("resultType");
        try writeType(line, col, start + close.? + 3, len - close.? - 3);
        w.end('}');
        return;
    }
    const paren = std.mem.indexOfScalar(u8, s, '(');
    const head = if (paren) |p| s[0..p] else s;
    // A generic may take no arguments (`task-scope()`); a named type omits empty arguments.
    if (paren) |p| if (s[len - 1] != ')' or (@as(i64, @intCast(len)) - @as(i64, @intCast(p)) - 2 <= 0 and std.mem.indexOfScalar(u8, s, ':') != null)) return bad(line, col, s);
    if (std.mem.indexOfScalar(u8, head, ':')) |colon| {
        if (!isId(head)) return bad(line, col, s);
        const uuid = head[colon + 1 ..];
        try w.startObject(null);
        if (std.mem.startsWith(u8, head, "type:")) {
            w.string("kind", "named");
            w.string("typeId", uuid);
            if (paren) |p| {
                try w.startArray("arguments");
                try writeArgs(line, col, start + p + 1, len - p - 2);
                w.end(']');
            }
        } else if (paren != null) {
            return bad(line, col, s);
        } else if (std.mem.startsWith(u8, head, "type-parameter:")) {
            w.string("kind", "typeParameter");
            w.string("typeParameterId", uuid);
        } else if (std.mem.startsWith(u8, head, "capability:")) {
            w.string("kind", "capability");
            w.string("capabilityId", uuid);
        } else return bad(line, col, s);
        w.end('}');
    } else if (paren == null) {
        if (!inSet(head, &primitives)) return bad(line, col, s);
        try w.startObject(null);
        w.string("kind", "primitive");
        w.string("primitive", head);
        w.end('}');
    } else {
        const generic = genericJson(head) orelse return bad(line, col, s);
        try w.startObject(null);
        w.string("kind", "builtinGeneric");
        w.string("generic", generic);
        try w.startArray("arguments");
        try writeArgs(line, col, start + paren.? + 1, len - paren.? - 2);
        w.end(']');
        w.end('}');
    }
}

/// Comma-separated type expressions (commas inside parentheses belong to the argument).
fn writeArgs(line: usize, col: usize, start: usize, len: usize) RecordError!void {
    if (len == 0) return;
    const s = text[start .. start + len];
    var depth: i64 = 0;
    var from: usize = 0;
    for (s, 0..) |c, i| {
        switch (c) {
            ',' => if (depth == 0) {
                try writeType(line, col, start + from, i - from);
                from = i + 1;
            },
            '(' => depth += 1,
            ')' => depth -= 1,
            else => {},
        }
    }
    try writeType(line, col, start + from, len - from);
}

// ---------------------------------------------------------------- decode

fn editDistanceOne(x: []const u8, y: []const u8) bool {
    if (x.len == y.len) {
        var differences: usize = 0;
        for (x, y) |p, q| {
            if (p != q) differences += 1;
        }
        return differences == 1;
    }
    if (x.len + 1 != y.len and y.len + 1 != x.len) return false;
    const longer = if (x.len > y.len) x else y;
    const shorter = if (x.len > y.len) y else x;
    var i: usize = 0;
    while (i < shorter.len and longer[i] == shorter[i]) i += 1;
    return eql(longer[i + 1 ..], shorter[i..]);
}

/// -1 when absent, -2 when not canonical.
fn positionValue(r: *const Record) i64 {
    if (r.fields.len > 1 and eql(r.fields[1].key, "position")) {
        const span = value(r.fields[1]);
        if (isCanonicalInt(span) and span[0] != '-') {
            return std.fmt.parseInt(i32, span, 10) catch return -2;
        }
        return -2;
    }
    return -1;
}

var records: List(*Record) = .empty;
var subjects: std.StringHashMap(*Record) = undefined;
/// The dependency's subjects a resolved local may reference without a record (`--dependency`).
var external: std.StringHashMapUnmanaged(void) = .empty;
var owned: std.StringArrayHashMapUnmanaged(List(*Record)) = undefined;
var arms: std.StringArrayHashMapUnmanaged(List(*Record)) = undefined;
var blocks: List(*List(*Record)) = .empty;
/// The `position=` placement error, which `RecordCodec.deserialize` reports only after the model check.
var deferred: ?Failure = null;
/// The writer's depth limit, which `RecordCodec.deserialize` reports as `MaximumDepthExceeded`.
var depth_exceeded = false;

fn deferFailure(line: usize, col: usize, message: []const u8) void {
    if (deferred == null) deferred = .{ .line = line, .column = col, .message = message };
}

fn addTo(map: *std.StringArrayHashMapUnmanaged(List(*Record)), key: []const u8, record: *Record) void {
    const entry = map.getOrPut(a, key) catch oom();
    if (!entry.found_existing) entry.value_ptr.* = .empty;
    entry.value_ptr.append(a, record) catch oom();
}

fn lineCount(upto: []const u8) usize {
    return std.mem.count(u8, upto, "\n") + 1;
}

fn decode() RecordError![]const u8 {
    w = .{};
    records = .empty;
    blocks = .empty;
    deferred = null;
    depth_exceeded = false;
    subjects = .init(a);
    owned = .empty;
    arms = .empty;
    if (text.len == 0) return fail(1, 1, "missing header");
    if (std.mem.startsWith(u8, text, "\xEF\xBB\xBF")) return fail(1, 1, "byte order mark");
    if (std.mem.indexOfScalar(u8, text, '\r')) |cr| {
        const before = text[0..cr];
        const line_start = if (std.mem.lastIndexOfScalar(u8, before, '\n')) |i| i + 1 else 0;
        return fail(lineCount(before), column(text, line_start, cr), "carriage return");
    }
    if (text[text.len - 1] != '\n') {
        const line_start = if (std.mem.lastIndexOfScalar(u8, text, '\n')) |i| i + 1 else 0;
        return fail(lineCount(text), column(text, line_start, text.len), "missing final newline");
    }
    // Header.
    const header_end = std.mem.indexOfScalar(u8, text, '\n').?;
    if (header_end == 0) return fail(1, 1, "missing header");
    var no_block: List(*Record) = .empty;
    const head = try parseLine(1, 0, header_end, &no_block);
    if (!eql(head.kind, "snapshot")) return fail(1, 1, "header must start with 'snapshot'");
    if (!hasDomain(head.subject, "snapshot")) return fail(1, head.column, fmt("expected a snapshot ID, got '{s}'", .{head.subject}));
    try w.startObject(null);
    const fields = head.fields;
    var fi: usize = 0;
    if (fi >= fields.len or !eql(fields[fi].key, "schema")) return fail(1, head.end_column, "missing schema=");
    if (!isCanonicalInt(value(fields[fi]))) return fail(1, fields[fi].column, "schema= must be an integer");
    // `serialize` writes the current version, but a document saved at an earlier record version
    // still decodes; the JSON reader checks construct minimums.
    const declared = std.fmt.parseInt(i32, value(fields[fi]), 10) catch null;
    if (declared == null or declared.? < schema_first_record or declared.? > schema_current)
        return fail(1, fields[fi].column, fmt("schema= must be from {d} to {d}", .{ schema_first_record, schema_current }));
    w.raw("schemaVersion", value(fields[fi]));
    w.string("snapshotId", head.subject[9..]);
    fi += 1;
    if (fi < fields.len and eql(fields[fi].key, "parent")) {
        const v = value(fields[fi]);
        if (!hasDomain(v, "snapshot")) return fail(1, fields[fi].column, "parent= must be a snapshot ID");
        w.string("parentSnapshotId", v[9..]);
        fi += 1;
    }
    if (fi < fields.len and eql(fields[fi].key, "dependency")) {
        const d = fields[fi];
        if (text[d.start] != '"' or !isCanonicalString(value(d))) return fail(1, d.column, "dependency= must be a quoted string as the writer's encoder spells it");
        if (fi + 1 >= fields.len or !eql(fields[fi + 1].key, "digest"))
            return fail(1, if (fi + 1 < fields.len) fields[fi + 1].column else head.end_column, "dependency= must be followed by digest=");
        const g = fields[fi + 1];
        for (value(g)) |c| if (!isHex(c)) return fail(1, g.column, "digest= must be lowercase hex");
        try w.startObject("dependency");
        w.raw("snapshotId", value(d));
        w.string("digest", value(g));
        w.end('}');
        fi += 2;
    }
    if (fi < fields.len) return fail(1, fields[fi].column, fmt("unknown header key {s}", .{fields[fi].key}));

    // Rows: tokenize, index and check the block grammar in one pass.
    var pos = header_end + 1;
    var line: usize = 2;
    // 0: after the header, 1: after a blank line, 2: inside a block.
    var state: u8 = 0;
    var block: *List(*Record) = &no_block;
    var declaration: *Record = head;
    var phase: u8 = 0;
    var next_position: i64 = 0;
    var previous_subject: ?[]const u8 = null;
    var previous_loose: ?[]const u8 = null;
    var group_index: i64 = -1;
    var last_declaration: ?[]const u8 = null;
    while (pos < text.len) {
        const line_end = std.mem.indexOfScalarPos(u8, text, pos, '\n').?;
        if (line_end == pos) {
            if (state == 1) return fail(line, 1, "expected a declaration record after a blank line");
            state = 1;
        } else {
            if (state == 0) return fail(line, 1, "expected a blank line before a declaration");
            if (state == 1) {
                block = a.create(List(*Record)) catch oom();
                block.* = .empty;
                blocks.append(a, block) catch oom();
            }
            const r = try parseLine(line, pos, line_end, block);
            if (!isSubjectSyntax(r.subject)) return fail(r.line, r.column, fmt("malformed subject '{s}'", .{r.subject}));
            if (r.owner != null and !isSubjectSyntax(r.owner.?)) return fail(r.line, r.fields[0].column, fmt("malformed owner '{s}'", .{r.owner.?}));
            if (subjects.contains(r.subject)) return fail(r.line, r.column, fmt("duplicate record {s}", .{r.subject}));
            subjects.put(r.subject, r) catch oom();
            records.append(a, r) catch oom();
            block.append(a, r) catch oom();
            if (eql(r.kind, "arm")) {
                if (r.owner != null) return fail(r.line, r.fields[0].column, "an arm record carries no in=");
                const slash = std.mem.indexOfScalar(u8, r.subject, '/');
                if (slash == null or !std.mem.startsWith(u8, r.subject[slash.?..], "/arm/variant:")) return fail(r.line, r.column, fmt("malformed arm subject '{s}'", .{r.subject}));
                addTo(&arms, r.subject[0..slash.?], r);
            } else if (std.mem.indexOfScalar(u8, r.subject, '/') != null) return fail(r.line, r.column, "a locator cannot be a record subject here");
            if (r.owner) |o| addTo(&owned, o, r);
            if (state == 1) {
                if (eql(r.kind, "arm") or r.owner != null) return fail(r.line, 1, "expected a declaration record after a blank line");
                const g = groupOfKind(r.kind) orelse return fail(r.line, 1, fmt("'{s}' is not a declaration kind", .{r.kind}));
                if (@as(i64, @intCast(g)) < group_index or (@as(i64, @intCast(g)) == group_index and last_declaration != null and !less(last_declaration.?, r.subject)))
                    return fail(r.line, r.column, fmt("declaration {s} is out of canonical order", .{r.subject}));
                group_index = @intCast(g);
                last_declaration = r.subject;
                declaration = r;
                phase = 0;
                next_position = 0;
                previous_subject = null;
                previous_loose = null;
                state = 2;
            } else {
                if (r.owner == null and !eql(r.kind, "arm")) {
                    if (groupOfKind(r.kind) != null) return fail(r.line, 1, "expected a blank line before a declaration");
                    return fail(r.line, 1, fmt("'{s}' is not a declaration kind", .{r.kind}));
                }
                // Records without `position=` must be in subject order (reported at once).
                if (positionValue(r) == -1) {
                    if (previous_loose != null and less(r.subject, previous_loose.?)) return fail(r.line, r.column, fmt("record {s} is out of canonical order", .{r.subject}));
                    previous_loose = r.subject;
                }
                const positioned = eql(r.kind, "type-parameter") or eql(r.kind, "parameter");
                if (positioned and r.owner != null and eql(r.owner.?, declaration.subject)) {
                    const p: u8 = if (eql(r.kind, "type-parameter")) 0 else 1;
                    if (p < phase or phase == 2) deferFailure(r.line, r.column, fmt("record {s} is out of canonical order", .{r.subject}));
                    if (p > phase) {
                        phase = p;
                        next_position = 0;
                    }
                    const at = positionValue(r);
                    if (at == -1) {
                        deferFailure(r.line, r.end_column, "missing position=");
                    } else if (at == -2) {
                        deferFailure(r.line, r.fields[1].column, "position= must be an integer");
                    } else if (at != next_position) {
                        deferFailure(r.line, r.fields[1].column, fmt("position= must be {d}", .{next_position}));
                    } else next_position += 1;
                } else {
                    phase = 2;
                    if (previous_subject != null and less(r.subject, previous_subject.?)) deferFailure(r.line, r.column, fmt("record {s} is out of canonical order", .{r.subject}));
                    previous_subject = r.subject;
                }
            }
        }
        pos = line_end + 1;
        line += 1;
    }
    if (state == 1) return fail(line - 1, 1, "trailing blank line");
    try structuralChecks();
    // Declarations in file order, which the block grammar verified is group order.
    var g: usize = 0;
    try w.startArray(groups[0]);
    for (blocks.items) |b| {
        const d = b.items[0];
        const dg = groupOfKind(d.kind).?;
        while (g < dg) {
            w.end(']');
            g += 1;
            try w.startArray(groups[g]);
        }
        try materialize(b, d);
    }
    while (g < groups.len - 1) {
        w.end(']');
        g += 1;
        try w.startArray(groups[g]);
    }
    w.end(']');
    w.end('}');
    for (records.items) |r| {
        if (!r.consumed) return fail(r.line, r.column, if (r.owner) |o| fmt("owner {s} not found", .{o}) else "orphan record");
    }
    return w.out.items;
}

fn startsWithDomain(subject: []const u8, domain: []const u8) bool {
    return subject.len > domain.len and std.mem.startsWith(u8, subject, domain) and subject[domain.len] == ':';
}

fn lastOf(items: []const *Record) *Record {
    return items[items.len - 1];
}

/// The line where a missing record would sit in canonical order.
fn slot(block: *List(*Record), domain: []const u8, subject: []const u8, fallback: usize) usize {
    var same: List(*Record) = .empty;
    for (block.items) |r| if (startsWithDomain(r.subject, domain)) same.append(a, r) catch oom();
    if (eql(domain, "parameter") or eql(domain, "type-parameter")) {
        var positioned: List(*Record) = .empty;
        for (same.items) |r| if (positionValue(r) != -1) positioned.append(a, r) catch oom();
        for (positioned.items, 0..) |r, i| if (positionValue(r) != @as(i64, @intCast(i))) return r.line;
        if (positioned.items.len > 0) return lastOf(positioned.items).line + 1;
        return block.items[0].line + 1;
    }
    for (same.items) |r| if (less(subject, r.subject)) return r.line;
    if (same.items.len > 0) return lastOf(same.items).line + 1;
    return if (inSet(domain, &declaration_domains)) block.items[0].line else fallback;
}

fn misspelled(subject: []const u8) bool {
    var keys = subjects.keyIterator();
    while (keys.next()) |key| if (editDistanceOne(subject, key.*)) return true;
    return false;
}

fn fieldNamed(r: *const Record, name: []const u8) ?[]const u8 {
    for (r.fields) |f| if (eql(f.key, name)) return value(f);
    return null;
}

fn blockOfRecord(subject: ?[]const u8) ?*List(*Record) {
    return if (subjects.get(subject orelse return null)) |r| r.block else null;
}

/// Missing owners and dangling references, reported on the line where the record is or should be.
fn structuralChecks() RecordError!void {
    for (records.items) |r| {
        if (r.owner) |o| {
            const needed = if (std.mem.indexOf(u8, o, "/arm/") != null) o else if (std.mem.indexOfScalar(u8, o, '/')) |i| o[0..i] else o;
            if (!subjects.contains(needed)) {
                const domain = needed[0..std.mem.indexOfScalar(u8, needed, ':').?];
                if (misspelled(needed)) return fail(r.line, r.fields[0].column, fmt("owner {s} not found", .{o}));
                return fail(slot(r.block, domain, needed, r.line), 1, fmt("owner {s} not found", .{o}));
            }
        }
        for (r.fields) |f| {
            if (eql(f.key, "in") or text[f.start] == '"') continue;
            // Every `domain:uuid` token in the value (lists, pairs and type expressions included).
            const stop = f.start + f.len;
            var i = f.start;
            while (i < stop) {
                if (text[i] == ':' and i + 37 <= stop and isUuid(text[i + 1 .. i + 37])) {
                    var d = i;
                    while (d > f.start and isDomainChar(text[d - 1])) d -= 1;
                    const subject = text[d .. i + 37];
                    const domain = text[d..i];
                    if (d < i and inSet(domain, &member_domains) and !subjects.contains(subject) and !external.contains(subject)) {
                        var line = r.line;
                        var col = f.column;
                        if (misspelled(subject)) {} else if ((eql(domain, "node") or eql(domain, "binding") or eql(domain, "type-parameter")) and r.owner != null) {
                            line = slot(r.block, domain, subject, r.line);
                            col = 1;
                        } else if (eql(domain, "parameter")) {
                            if (blockOfRecord(fieldNamed(r, "target-function"))) |callee| {
                                line = slot(callee, domain, subject, r.line);
                                col = 1;
                            } else if (r.owner != null) {
                                line = slot(r.block, domain, subject, r.line);
                                col = 1;
                            }
                        } else if (eql(domain, "capability-parameter")) {
                            if (blockOfRecord(fieldNamed(r, "operation"))) |callee| {
                                line = slot(callee, domain, subject, r.line);
                                col = 1;
                            }
                        } else {
                            // The block whose same-domain subjects bracket the missing one, else this line.
                            for (blocks.items) |b| {
                                var first: ?*Record = null;
                                var last: ?*Record = null;
                                for (b.items) |x| if (startsWithDomain(x.subject, domain)) {
                                    if (first == null) first = x;
                                    last = x;
                                };
                                if (first != null and less(first.?.subject, subject) and less(subject, last.?.subject)) {
                                    line = slot(b, domain, subject, r.line);
                                    col = 1;
                                    break;
                                }
                            }
                        }
                        return fail(line, col, fmt("{s}= refers to {s}, which has no record", .{ f.key, subject }));
                    }
                    i += 37;
                } else i += 1;
            }
        }
    }
}

// ---------------------------------------------------------------- materialize

fn badValue(r: *const Record, f: Field, key: Key) RecordError {
    return fail(r.line, f.column, fmt("bad value for {s}=", .{key.record}));
}

fn listBounds(r: *const Record, f: Field, record_key: []const u8) RecordError![2]usize {
    if (text[f.start] != '[' or text[f.start + f.len - 1] != ']') return fail(r.line, f.column, fmt("{s}= must be a list", .{record_key}));
    if (f.len == 2) return fail(r.line, f.column, fmt("{s}= must not be empty", .{record_key}));
    return .{ f.start + 1, f.start + f.len - 1 };
}

/// The items of a list value, one at a time (so an item's own error comes before a later order
/// error), checked to increase strictly when `sorted`.
const ListItems = struct {
    r: *const Record,
    f: Field,
    record_key: []const u8,
    sorted: bool,
    i: usize,
    stop: usize,
    from: usize,
    previous: ?usize = null,

    fn init(r: *const Record, f: Field, record_key: []const u8, sorted: bool) RecordError!ListItems {
        const bounds = try listBounds(r, f, record_key);
        return .{ .r = r, .f = f, .record_key = record_key, .sorted = sorted, .i = bounds[0], .stop = bounds[1], .from = bounds[0] };
    }

    fn next(it: *ListItems) RecordError!?[2]usize {
        while (it.i <= it.stop) {
            const i = it.i;
            it.i += 1;
            if (i == it.stop or text[i] == ' ') {
                if (i == it.from) return fail(it.r.line, it.f.column, fmt("bad list for {s}=", .{it.record_key}));
                if (it.sorted) if (it.previous) |p| if (!less(text[p .. it.from - 1], text[it.from..i])) return fail(it.r.line, it.f.column, fmt("{s}= is not in canonical order", .{it.record_key}));
                const item = [2]usize{ it.from, i - it.from };
                it.previous = it.from;
                it.from = i + 1;
                return item;
            }
        }
        return null;
    }
};

fn byPosition(_: void, x: *Record, y: *Record) bool {
    return positionValue(x) < positionValue(y);
}

fn materialize(block: *List(*Record), r: *Record) RecordError!void {
    r.consumed = true;
    if (r.block != block) return fail(r.line, r.column, fmt("record {s} belongs in the block of its declaration", .{r.subject}));
    const kind = kinds.get(r.kind) orelse return fail(r.line, 1, fmt("unknown record kind '{s}'", .{r.kind}));
    const domain = subjectDomain(r.kind, r.owner);
    if (!hasDomain(r.subject, domain)) return fail(r.line, r.column, fmt("expected a {s} ID, got '{s}'", .{ domain, r.subject }));
    var fi: usize = if (r.owner == null) 0 else 1;
    const positioned = eql(r.kind, "parameter") or eql(r.kind, "type-parameter");
    if (fi < r.fields.len and eql(r.fields[fi].key, "position")) {
        if (!positioned) return fail(r.line, r.fields[fi].column, "position= is only for parameters and type parameters");
        if (positionValue(r) < 0) return fail(r.line, r.fields[fi].column, "position= must be an integer");
        fi += 1;
    } else if (positioned) return fail(r.line, r.end_column, "missing position=");
    try w.startObject(null);
    try body(block, r, kind, fi, domain);
    w.end('}');
}

fn body(block: *List(*Record), r: *Record, kind: Kind, first: usize, domain: []const u8) RecordError!void {
    var fi = first;
    const members = owned.get(r.subject);
    for (kind.keys) |key| {
        switch (key.shape) {
            .meta => if (eql(key.json, "kind")) w.string("kind", kind.json_kind.?) else w.string(key.json, r.subject[domain.len + 1 ..]),
            .members => |m| {
                var items: List(*Record) = .empty;
                if (members) |list| for (list.items) |x| if (eql(x.kind, m)) items.append(a, x) catch oom();
                if (items.items.len > 0 or !key.omit_empty) {
                    try w.startArray(key.json);
                    if (inSet(key.json, &ordered)) {
                        std.mem.sort(*Record, items.items, {}, byPosition);
                        for (items.items, 0..) |x, i| {
                            const col = if (x.fields.len > 1) x.fields[1].column else x.end_column;
                            const at = positionValue(x);
                            if (at == -2) return fail(x.line, col, "position= must be an integer");
                            if (at != @as(i64, @intCast(i))) return fail(x.line, col, fmt("position= must be {d}", .{i}));
                        }
                    }
                    for (items.items) |x| try materialize(block, x);
                    w.end(']');
                }
            },
            .arms => {
                try w.startArray(key.json);
                if (arms.get(r.subject)) |list| for (list.items) |x| try arm(block, r, x);
                w.end(']');
            },
            else => {
                if (fi < r.fields.len and eql(r.fields[fi].key, key.record)) {
                    try writeValue(block, r, r.fields[fi], key);
                    fi += 1;
                } else {
                    for (r.fields) |f| if (eql(f.key, key.record)) return fail(r.line, f.column, fmt("{s}= is out of canonical order", .{key.record}));
                    switch (key.shape) {
                        .graph, .value_arm => return fail(r.line, r.end_column, fmt("missing {s}=", .{key.record})),
                        .type_list, .id_list, .pairs => if (!key.omit_empty) {
                            try w.startArray(key.json);
                            w.end(']');
                        },
                        else => if (!key.optional) return fail(r.line, r.end_column, fmt("missing {s}=", .{key.record})),
                    }
                }
            },
        }
    }
    if (fi < r.fields.len) return fail(r.line, r.fields[fi].column, fmt("unknown key {s}= on {s}", .{ r.fields[fi].key, r.kind }));
    if (members) |list| for (list.items) |x| if (!x.consumed) return fail(x.line, x.column, fmt("{s} record cannot be owned by {s} {s}", .{ x.kind, r.kind, r.subject }));
}

fn writeValue(block: *List(*Record), r: *Record, f: Field, key: Key) RecordError!void {
    const s = value(f);
    switch (key.shape) {
        .id => |d| {
            if (!hasDomain(s, d)) return fail(r.line, f.column, fmt("expected a {s} ID, got '{s}'", .{ d, s }));
            w.string(key.json, s[d.len + 1 ..]);
        },
        .bool => {
            if (eql(s, "true")) {
                w.raw(key.json, "true");
            } else if (eql(s, "false") and key.optional) {
                // The writer omits an optional boolean that is false.
                return fail(r.line, f.column, fmt("{s}=false is omitted", .{key.record}));
            } else if (eql(s, "false")) {
                w.raw(key.json, "false");
            } else return badValue(r, f, key);
        },
        .int => {
            if (!isCanonicalInt(s)) return badValue(r, f, key);
            w.raw(key.json, s);
        },
        .str => {
            if (s[0] != '"') return badValue(r, f, key);
            if (!isCanonicalString(s)) return fail(r.line, f.column, fmt("{s}= is not spelled as the writer's encoder spells it", .{key.record}));
            w.raw(key.json, s);
        },
        .type_expr => {
            w.name(key.json);
            try writeType(r.line, f.column, f.start, f.len);
        },
        .type_list => {
            try w.startArray(key.json);
            var items = try ListItems.init(r, f, key.record, false);
            while (try items.next()) |item| try writeType(r.line, f.column, item[0], item[1]);
            w.end(']');
        },
        .id_list => |d| {
            try w.startArray(key.json);
            var items = try ListItems.init(r, f, key.record, inSet(key.json, &sorted_id_lists));
            while (try items.next()) |item| {
                const id = text[item[0] .. item[0] + item[1]];
                if (!hasDomain(id, d)) return fail(r.line, f.column, fmt("expected a {s} ID, got '{s}'", .{ d, id }));
                w.string(null, id[d.len + 1 ..]);
            }
            w.end(']');
        },
        .pairs => |p| {
            try w.startArray(key.json);
            var items = try ListItems.init(r, f, key.record, true);
            while (try items.next()) |item| {
                const pair = text[item[0] .. item[0] + item[1]];
                const eq_at = std.mem.indexOfScalar(u8, pair, '=');
                if (eq_at == null or !hasDomain(pair[0..eq_at.?], p[1]) or !hasDomain(pair[eq_at.? + 1 ..], p[3]))
                    return fail(r.line, f.column, fmt("bad pair '{s}' in {s}=", .{ pair, key.record }));
                try w.startObject(null);
                w.string(p[0], pair[p[1].len + 1 ..][0..36]);
                w.string(p[2], pair[eq_at.? + 1 + p[3].len + 1 ..][0..36]);
                w.end('}');
            }
            w.end(']');
        },
        .graph => {
            if (!hasDomain(s, "node")) return fail(r.line, f.column, fmt("expected a node ID, got '{s}'", .{s}));
            w.name(key.json);
            try graph(block, std.mem.concat(a, u8, &.{ r.subject, "/", key.record }) catch oom(), f, false);
        },
        .value_arm => |binding_key| {
            if (!hasDomain(s, "node")) return fail(r.line, f.column, fmt("expected a node ID, got '{s}'", .{s}));
            const loc = std.mem.concat(a, u8, &.{ r.subject, "/", key.record }) catch oom();
            var binding: ?*Record = null;
            var count: usize = 0;
            if (owned.get(loc)) |list| for (list.items) |m| if (eql(m.kind, "binding")) {
                binding = m;
                count += 1;
            };
            if (count != 1) return fail(r.line, f.column, fmt("{s}= needs exactly one binding record in {s}", .{ key.record, loc }));
            try w.startObject(key.json);
            w.name(binding_key);
            try writeBinding(block, binding.?, false);
            w.name("body");
            try graph(block, loc, f, true);
            w.end('}');
        },
        .meta, .arms, .members => return fail(r.line, f.column, fmt("{s}= cannot be given inline", .{key.record})),
    }
}

/// `{nodes, resultNodeId}` for the graph at `loc`; `result` holds `node:R`.
fn graph(block: *List(*Record), loc: []const u8, result: Field, skip_bindings: bool) RecordError!void {
    try w.startObject(null);
    try w.startArray("nodes");
    if (owned.get(loc)) |list| for (list.items) |m| {
        if (eql(m.kind, "binding") and skip_bindings) continue;
        if (eql(m.kind, "binding") or eql(m.kind, "arm") or member_kinds.contains(m.kind) or groupOfKind(m.kind) != null)
            return fail(m.line, m.column, fmt("{s} record cannot be owned by graph {s}", .{ m.kind, loc }));
        try materialize(block, m);
    };
    w.end(']');
    w.string("resultNodeId", text[result.start + 5 .. result.start + 5 + 36]);
    w.end('}');
}

fn writeBinding(block: *List(*Record), b: *Record, with_source_field: bool) RecordError!void {
    b.consumed = true;
    if (b.block != block) return fail(b.line, b.column, fmt("record {s} belongs in the block of its declaration", .{b.subject}));
    if (!eql(b.kind, "binding")) return fail(b.line, 1, fmt("expected a binding record, got {s}", .{b.kind}));
    if (!hasDomain(b.subject, "binding")) return fail(b.line, b.column, fmt("expected a binding ID, got '{s}'", .{b.subject}));
    var fi: usize = 1;
    if (with_source_field) {
        if (fi >= b.fields.len or !eql(b.fields[fi].key, "source-field")) return fail(b.line, if (fi < b.fields.len) b.fields[fi].column else b.end_column, "missing source-field=");
        const f = b.fields[fi];
        if (!hasDomain(value(f), "field")) return fail(b.line, f.column, fmt("expected a field ID, got '{s}'", .{value(f)}));
        try w.startObject(null);
        w.string("sourceFieldId", text[f.start + 6 .. f.start + 6 + 36]);
        try w.startObject("binding");
        fi += 1;
    } else try w.startObject(null);
    try body(block, b, kinds.get("binding").?, fi, "binding");
    w.end('}');
    if (with_source_field) w.end('}');
}

const SourcedBinding = struct { source: []const u8, record: *Record };

fn bySource(_: void, x: SourcedBinding, y: SourcedBinding) bool {
    return less(x.source, y.source);
}

fn arm(block: *List(*Record), m: *Record, x: *Record) RecordError!void {
    x.consumed = true;
    if (x.block != block) return fail(x.line, x.column, fmt("record {s} belongs in the block of its declaration", .{x.subject}));
    const key = x.subject[m.subject.len + 5 ..];
    if (!hasDomain(key, "variant")) return fail(x.line, x.column, fmt("malformed arm subject '{s}'", .{x.subject}));
    var fi: usize = 0;
    try w.startObject(null);
    if (fi < x.fields.len and eql(x.fields[fi].key, "variants")) {
        const f = x.fields[fi];
        const bounds = try listBounds(x, f, "variants");
        const message = "variants= must list two or more variants starting with the locator's key";
        var count: usize = 0;
        var previous_item: usize = 0;
        try w.startArray("variantIds");
        var from = bounds[0];
        var i = bounds[0];
        while (i <= bounds[1]) : (i += 1) {
            if (i == bounds[1] or text[i] == ' ') {
                const item = text[from..i];
                if (!hasDomain(item, "variant")) return fail(x.line, f.column, message);
                if (count == 0 and !eql(item, key)) return fail(x.line, f.column, message);
                if (count > 0 and !less(text[previous_item .. from - 1], item)) return fail(x.line, f.column, "variants= is not in canonical order");
                previous_item = from;
                w.string(null, item[8..]);
                count += 1;
                from = i + 1;
            }
        }
        w.end(']');
        if (count < 2) return fail(x.line, f.column, message);
        fi += 1;
    } else w.string("variantId", key[8..]);
    if (fi >= x.fields.len or !eql(x.fields[fi].key, "body")) return fail(x.line, if (fi < x.fields.len) x.fields[fi].column else x.end_column, "missing body=");
    const result = x.fields[fi];
    if (!hasDomain(value(result), "node")) return fail(x.line, result.column, fmt("expected a node ID, got '{s}'", .{value(result)}));
    fi += 1;
    if (fi < x.fields.len) return fail(x.line, x.fields[fi].column, fmt("unknown key {s}= on arm", .{x.fields[fi].key}));
    var bindings: List(SourcedBinding) = .empty;
    if (owned.get(x.subject)) |list| for (list.items) |b| if (eql(b.kind, "binding")) {
        const source = if (b.fields.len > 1 and eql(b.fields[1].key, "source-field")) value(b.fields[1]) else "";
        bindings.append(a, .{ .source = source, .record = b }) catch oom();
    };
    std.mem.sort(SourcedBinding, bindings.items, {}, bySource);
    try w.startArray("fieldBindings");
    for (bindings.items) |b| try writeBinding(block, b.record, true);
    w.end(']');
    w.name("body");
    try graph(block, x.subject, result, true);
    w.end('}');
}

/// One record text as the session host reads it: the protocol line (`ok`, `deferred …` or
/// `error …`, as `main` prints it) and the writer's JSON unless the line is an error. `allocator`
/// holds the result; `dependency_subjects` are the dependency's (`--dependency`), none without one.
pub fn readRecords(allocator: std.mem.Allocator, input: []const u8, dependency_subjects: []const []const u8) struct { line: []const u8, json: ?[]const u8 } {
    a = allocator;
    defer {
        text = "";
        records = .empty;
        subjects = .init(std.heap.page_allocator);
        external = .empty;
        owned = .empty;
        arms = .empty;
        blocks = .empty;
        deferred = null;
        failure = undefined;
        w = .{};
    }
    external = .empty;
    for (dependency_subjects) |subject| external.put(a, subject, {}) catch oom();
    loadKinds();
    text = if (std.unicode.utf8ValidateSlice(input)) input else fmt("{f}", .{std.unicode.fmtUtf8(input)});
    if (decode()) |json| {
        const line = if (deferred) |d| fmt("deferred {d}:{d}: {s}", .{ d.line, d.column, d.message }) else "ok";
        return .{ .line = line, .json = json };
    } else |_| return .{ .line = if (depth_exceeded) fmt("error MaximumDepthExceeded: {d}", .{max_json_depth}) else fmt("error {d}:{d}: {s}", .{ failure.line, failure.column, failure.message }), .json = null };
}
var kinds_loaded = false;

// ---------------------------------------------------------------- encode: the writer's JSON -> record text

const Json = std.json.Value;

/// A JSON string spelled as the F# writer spells it (`JavaScriptEncoder.Default`), so a value read
/// from the writer's canonical JSON is written back byte for byte.
fn appendJsonString(out: *List(u8), input: []const u8) void {
    out.append(a, '"') catch oom();
    var it = std.unicode.Utf8View.initUnchecked(input).iterator();
    while (it.nextCodepoint()) |c| switch (c) {
        8 => out.appendSlice(a, "\\b") catch oom(),
        9 => out.appendSlice(a, "\\t") catch oom(),
        10 => out.appendSlice(a, "\\n") catch oom(),
        12 => out.appendSlice(a, "\\f") catch oom(),
        13 => out.appendSlice(a, "\\r") catch oom(),
        '\\' => out.appendSlice(a, "\\\\") catch oom(),
        0x20...0x21, 0x23...0x25, 0x28...0x2A, 0x2C...0x3B, 0x3D, 0x3F...0x5B, 0x5D...0x5F, 0x61...0x7E => out.append(a, @intCast(c)) catch oom(),
        else => if (c >= 0x10000) {
            const v = c - 0x10000;
            out.print(a, "\\u{X:0>4}\\u{X:0>4}", .{ 0xD800 + (v >> 10), 0xDC00 + (v & 0x3FF) }) catch oom();
        } else out.print(a, "\\u{X:0>4}", .{c}) catch oom(),
    };
    out.append(a, '"') catch oom();
}

/// `GetRawText` of a scalar of the writer's JSON.
fn rawText(out: *List(u8), scalar: Json) ShapeError!void {
    switch (scalar) {
        .string => |text_value| appendJsonString(out, text_value),
        .integer => |n| out.print(a, "{d}", .{n}) catch oom(),
        .bool => |b| out.appendSlice(a, if (b) "true" else "false") catch oom(),
        .number_string => |digits| out.appendSlice(a, digits) catch oom(),
        else => return error.InvalidShape,
    }
}

/// The record writer's input is JSON from outside this process; a wrong shape is an error, not a panic.
const ShapeError = error{InvalidShape};

fn jsonAs(comptime tag: std.meta.Tag(Json), element: Json) ShapeError!@FieldType(Json, @tagName(tag)) {
    return if (element == tag) @field(element, @tagName(tag)) else error.InvalidShape;
}

fn jsonGet(element: Json, name: []const u8) ShapeError!?Json {
    return (try jsonAs(.object, element)).get(name);
}

fn jsonField(element: Json, name: []const u8) ShapeError!Json {
    return try jsonGet(element, name) orelse error.InvalidShape;
}

fn jsonItems(element: Json) ShapeError![]Json {
    return (try jsonAs(.array, element)).items;
}

fn jsonStr(element: Json, name: []const u8) ShapeError![]const u8 {
    return jsonAs(.string, try jsonField(element, name));
}

const generics_by_json = [_][2][]const u8{ .{ "option", "option" }, .{ "result", "result" }, .{ "map", "map" }, .{ "list", "list" }, .{ "set", "set" }, .{ "oneShot", "one-shot" }, .{ "task", "task" }, .{ "exception", "exception" }, .{ "taskScope", "task-scope" } };

fn typeExpr(out: *List(u8), t: Json) ShapeError!void {
    const kind = try jsonStr(t, "kind");
    const args = struct {
        fn f(o: *List(u8), items: Json) ShapeError!void {
            for (try jsonItems(items), 0..) |item, i| {
                if (i > 0) o.append(a, ',') catch oom();
                try typeExpr(o, item);
            }
        }
    }.f;
    if (eql(kind, "primitive")) {
        out.appendSlice(a, try jsonStr(t, "primitive")) catch oom();
    } else if (eql(kind, "named")) {
        out.print(a, "type:{s}", .{try jsonStr(t, "typeId")}) catch oom();
        if (try jsonGet(t, "arguments")) |arguments| {
            out.append(a, '(') catch oom();
            try args(out, arguments);
            out.append(a, ')') catch oom();
        }
    } else if (eql(kind, "builtinGeneric")) {
        const generic = try jsonStr(t, "generic");
        const name = for (generics_by_json) |pair| {
            if (eql(pair[0], generic)) break pair[1];
        } else return error.InvalidShape;
        out.print(a, "{s}(", .{name}) catch oom();
        try args(out, try jsonField(t, "arguments"));
        out.append(a, ')') catch oom();
    } else if (eql(kind, "typeParameter")) {
        out.print(a, "type-parameter:{s}", .{try jsonStr(t, "typeParameterId")}) catch oom();
    } else if (eql(kind, "capability")) {
        out.print(a, "capability:{s}", .{try jsonStr(t, "capabilityId")}) catch oom();
    } else if (eql(kind, "function")) {
        out.append(a, '(') catch oom();
        try args(out, try jsonField(t, "parameters"));
        out.appendSlice(a, ")->") catch oom();
        try typeExpr(out, try jsonField(t, "resultType"));
    } else return error.InvalidShape;
}

/// A member line with its place in the declaration's block: the declaration's own type
/// parameters (phase 0) and parameters (phase 1) by position, then everything else by subject.
const Line = struct { phase: u8, position: usize, subject: []const u8, text: []const u8 };

fn lineLess(_: void, x: Line, y: Line) bool {
    if (x.phase != y.phase) return x.phase < y.phase;
    if (x.position != y.position) return x.position < y.position;
    return less(x.subject, y.subject);
}

fn kindOf(element: Json, fallback: []const u8) ShapeError!Kind {
    if (try jsonGet(element, "kind")) |kind| {
        const name = try jsonAs(.string, kind);
        var it = kinds.valueIterator();
        while (it.next()) |candidate| if (candidate.json_kind) |json_kind| if (eql(json_kind, name)) return candidate.*;
        return error.InvalidShape;
    }
    return kinds.get(fallback) orelse error.InvalidShape;
}

const Encoder = struct {
    acc: *List(Line),

    fn object(self: Encoder, declaration: []const u8, kind: Kind, subject: []const u8, obj: Json, owner: ?[]const u8, position: ?usize) ShapeError![]const u8 {
        var sb: List(u8) = .empty;
        sb.print(a, "{s} {s}", .{ kind.name, subject }) catch oom();
        if (owner) |o| sb.print(a, " in={s}", .{o}) catch oom();
        if (position) |p| sb.print(a, " position={d}", .{p}) catch oom();
        for (kind.keys) |key| {
            if (key.shape == .meta) continue;
            const v = try jsonGet(obj, key.json) orelse continue;
            const field = struct {
                fn f(o: *List(u8), name: []const u8) void {
                    o.print(a, " {s}=", .{name}) catch oom();
                }
            }.f;
            switch (key.shape) {
                .meta => {},
                .id => |domain| {
                    field(&sb, key.record);
                    sb.print(a, "{s}:{s}", .{ domain, try jsonAs(.string, v) }) catch oom();
                },
                .bool, .int, .str => {
                    field(&sb, key.record);
                    try rawText(&sb, v);
                },
                .type_expr => {
                    field(&sb, key.record);
                    try typeExpr(&sb, v);
                },
                .type_list, .id_list, .pairs => {
                    const list = try jsonItems(v);
                    if (list.len == 0) continue;
                    field(&sb, key.record);
                    sb.append(a, '[') catch oom();
                    for (list, 0..) |item, i| {
                        if (i > 0) sb.append(a, ' ') catch oom();
                        switch (key.shape) {
                            .type_list => try typeExpr(&sb, item),
                            .id_list => |domain| sb.print(a, "{s}:{s}", .{ domain, try jsonAs(.string, item) }) catch oom(),
                            .pairs => |p| sb.print(a, "{s}:{s}={s}:{s}", .{ p[1], try jsonStr(item, p[0]), p[3], try jsonStr(item, p[2]) }) catch oom(),
                            else => unreachable,
                        }
                    }
                    sb.append(a, ']') catch oom();
                },
                .graph => {
                    const result = try self.graph(declaration, v, fmt("{s}/{s}", .{ subject, key.record }));
                    field(&sb, key.record);
                    sb.print(a, "node:{s}", .{result}) catch oom();
                },
                .value_arm => |binding_key| {
                    const loc = fmt("{s}/{s}", .{ subject, key.record });
                    self.acc.append(a, try binding(try jsonField(v, binding_key), loc, null)) catch oom();
                    const result = try self.graph(declaration, try jsonField(v, "body"), loc);
                    field(&sb, key.record);
                    sb.print(a, "node:{s}", .{result}) catch oom();
                },
                .arms => for (try jsonItems(v)) |match_arm| {
                    var variants: List([]const u8) = .empty;
                    if (try jsonGet(match_arm, "variantIds")) |ids| {
                        for (try jsonItems(ids)) |id| variants.append(a, try jsonAs(.string, id)) catch oom();
                    } else variants.append(a, try jsonStr(match_arm, "variantId")) catch oom();
                    if (variants.items.len == 0) return error.InvalidShape;
                    const loc = fmt("{s}/arm/variant:{s}", .{ subject, variants.items[0] });
                    for (try jsonItems(try jsonField(match_arm, "fieldBindings"))) |fb| {
                        self.acc.append(a, try binding(try jsonField(fb, "binding"), loc, fmt("field:{s}", .{try jsonStr(fb, "sourceFieldId")}))) catch oom();
                    }
                    const arm_body = try self.graph(declaration, try jsonField(match_arm, "body"), loc);
                    var text_out: List(u8) = .empty;
                    text_out.print(a, "arm {s}", .{loc}) catch oom();
                    if (variants.items.len > 1) {
                        text_out.appendSlice(a, " variants=[") catch oom();
                        for (variants.items, 0..) |id, i| text_out.print(a, "{s}variant:{s}", .{ if (i > 0) " " else "", id }) catch oom();
                        text_out.append(a, ']') catch oom();
                    }
                    text_out.print(a, " body=node:{s}", .{arm_body}) catch oom();
                    self.acc.append(a, .{ .phase = 2, .position = 0, .subject = loc, .text = text_out.items }) catch oom();
                },
                .members => |m| {
                    const is_ordered = eql(key.json, "parameters") or eql(key.json, "typeParameters");
                    const domain = if (eql(m, "parameter") and eql(kind.name, "operation")) "capability-parameter" else m;
                    for (try jsonItems(v), 0..) |item, i| {
                        const id = if (try jsonGet(item, "id")) |id| try jsonAs(.string, id) else try jsonStr(item, "typeParameterId");
                        const member_subject = fmt("{s}:{s}", .{ domain, id });
                        const rendered = try self.object(declaration, try kindOf(item, m), member_subject, item, subject, if (is_ordered) i else null);
                        const phase: u8 = if (is_ordered and eql(subject, declaration)) (if (eql(m, "type-parameter")) 0 else 1) else 2;
                        self.acc.append(a, .{ .phase = phase, .position = if (phase == 2) 0 else i, .subject = member_subject, .text = rendered }) catch oom();
                    }
                },
            }
        }
        return sb.items;
    }

    fn graph(self: Encoder, declaration: []const u8, g: Json, loc: []const u8) ShapeError![]const u8 {
        for (try jsonItems(try jsonField(g, "nodes"))) |n| {
            const subject = fmt("node:{s}", .{try jsonStr(n, "id")});
            const rendered = try self.object(declaration, try kindOf(n, ""), subject, n, loc, null);
            self.acc.append(a, .{ .phase = 2, .position = 0, .subject = subject, .text = rendered }) catch oom();
        }
        return jsonStr(g, "resultNodeId");
    }

    fn binding(b: Json, owner: []const u8, source_field: ?[]const u8) ShapeError!Line {
        const subject = fmt("binding:{s}", .{try jsonStr(b, "id")});
        var sb: List(u8) = .empty;
        sb.print(a, "binding {s} in={s}", .{ subject, owner }) catch oom();
        if (source_field) |f| sb.print(a, " source-field={s}", .{f}) catch oom();
        for (kinds.get("binding").?.keys) |key| {
            const v = try jsonGet(b, key.json) orelse continue;
            switch (key.shape) {
                .type_expr => {
                    sb.print(a, " {s}=", .{key.record}) catch oom();
                    try typeExpr(&sb, v);
                },
                .bool, .int, .str => {
                    sb.print(a, " {s}=", .{key.record}) catch oom();
                    try rawText(&sb, v);
                },
                else => {},
            }
        }
        return .{ .phase = 2, .position = 0, .subject = subject, .text = sb.items };
    }
};

/// `RecordCodec.ofJson`: the writer's JSON (`SnapshotCodec.serialize` or `serializeResolved`) as record text.
pub fn recordsOf(allocator: std.mem.Allocator, json: []const u8) ![]const u8 {
    a = allocator;
    loadKinds();
    const root = try std.json.parseFromSliceLeaky(Json, a, json, .{});
    var sb: List(u8) = .empty;
    sb.print(a, "snapshot snapshot:{s} schema={d}", .{ try jsonStr(root, "snapshotId"), try jsonAs(.integer, try jsonField(root, "schemaVersion")) }) catch oom();
    if (try jsonGet(root, "parentSnapshotId")) |p| sb.print(a, " parent=snapshot:{s}", .{try jsonAs(.string, p)}) catch oom();
    if (try jsonGet(root, "dependency")) |d| {
        sb.appendSlice(a, " dependency=") catch oom();
        appendJsonString(&sb, try jsonStr(d, "snapshotId"));
        sb.print(a, " digest={s}", .{try jsonStr(d, "digest")}) catch oom();
    }
    sb.append(a, '\n') catch oom();
    const declaration_groups = [_][2][]const u8{ .{ "types", "type" }, .{ "effects", "effect" }, .{ "capabilities", "capability" }, .{ "functions", "function" }, .{ "obligations", "obligation" } };
    for (declaration_groups) |group| {
        for (try jsonItems(try jsonField(root, group[0]))) |d| {
            const subject = fmt("{s}:{s}", .{ group[1], try jsonStr(d, "id") });
            var acc: List(Line) = .empty;
            const encoder: Encoder = .{ .acc = &acc };
            const first = try encoder.object(subject, try kindOf(d, group[1]), subject, d, null, null);
            std.mem.sort(Line, acc.items, {}, lineLess);
            sb.print(a, "\n{s}\n", .{first}) catch oom();
            for (acc.items) |l| sb.print(a, "{s}\n", .{l.text}) catch oom();
        }
    }
    return sb.items;
}

test "records of wrong-shaped JSON are an error" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    for ([_][]const u8{ "{}", "[]", "{\"snapshotId\":1}", "{\"snapshotId\":\"s\",\"schemaVersion\":1}" }) |json|
        try std.testing.expectError(error.InvalidShape, recordsOf(arena.allocator(), json));
}

pub fn main(init: std.process.Init) !void {
    a = init.arena.allocator();
    const args = try init.minimal.args.toSlice(a);
    // `--records <in.json> <out.nyrec>`: the writer's JSON as record text (`RecordCodec.ofJson`).
    if (args.len == 4 and eql(args[1], "--records")) {
        const input = try std.Io.Dir.cwd().readFileAlloc(init.io, args[2], a, .unlimited);
        try std.Io.Dir.cwd().writeFile(init.io, .{ .sub_path = args[3], .data = try recordsOf(a, input) });
        return;
    }
    var index: usize = 1;
    // `--dependency <file>`: the dependency's subjects, one `domain:uuid` per line, for every pair.
    if (args.len > 2 and eql(args[1], "--dependency")) {
        const list = try std.Io.Dir.cwd().readFileAlloc(init.io, args[2], a, .unlimited);
        var lines = std.mem.tokenizeScalar(u8, list, '\n');
        while (lines.next()) |subject| external.put(a, subject, {}) catch oom();
        index = 3;
    }
    if (args.len < index + 2 or (args.len - index) % 2 != 0) {
        std.debug.print("usage: snapshot-reader [--dependency <subjects>] <in.nyrec> <out.json> [<in> <out> ...]\n", .{});
        std.process.exit(64);
    }
    loadKinds();
    var buffer: [4096]u8 = undefined;
    var writer = std.Io.File.stdout().writer(init.io, &buffer);
    const out = &writer.interface;
    while (index < args.len) : (index += 2) {
        const input = try std.Io.Dir.cwd().readFileAlloc(init.io, args[index], a, .unlimited);
        // Ill-formed bytes become U+FFFD per maximal subpart, as .NET's UTF8Encoding decodes them.
        text = if (std.unicode.utf8ValidateSlice(input)) input else fmt("{f}", .{std.unicode.fmtUtf8(input)});
        if (decode()) |json| {
            try std.Io.Dir.cwd().writeFile(init.io, .{ .sub_path = args[index + 1], .data = json });
            if (deferred) |d| try out.print("deferred {d}:{d}: {s}\n", .{ d.line, d.column, d.message }) else try out.print("ok\n", .{});
        } else |_| if (depth_exceeded)
            try out.print("error MaximumDepthExceeded: {d}\n", .{max_json_depth})
        else
            try out.print("error {d}:{d}: {s}\n", .{ failure.line, failure.column, failure.message });
    }
    try out.flush();
}
