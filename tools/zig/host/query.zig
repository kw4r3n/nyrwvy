//! The name-based read operations (`search`, `references`, `unreferenced`) and the request
//! input they share with F#'s `WireInput` and `WireSymbolic`: field checks, exact IDs and
//! declaration selectors.
const std = @import("std");
const model = @import("model_reader");
const wire = @import("wire.zig");
const case_runs = @import("case_runs.zig");
const viewer = @import("viewer.zig");
const view_file = @import("view_file.zig");

pub const Value = model.Value;
const Str = model.Str;
const Writer = std.Io.Writer;
const Allocator = std.mem.Allocator;

/// A request this module rejects: the reply's error code and message.
pub const Rejection = struct { code: []const u8, message: []const u8 };
pub const Answer = union(enum) { result: []const u8, rejected: Rejection };

const Fail = anyerror;

/// The request being answered, and where a rejection's text goes.
pub const Request = struct {
    a: Allocator,
    line: []const u8,
    root: std.json.ObjectMap,
    /// The open model in canonical order and the selected dependency's provenance.
    model: Value,
    provenance: ?wire.Provenance,
    snapshot_id: []const u8 = "",
    /// The snapshot file, absolute, beside which views are written.
    snapshot_path: []const u8 = "",
    rejection: Rejection = .{ .code = "", .message = "" },

    pub fn fail(self: *Request, comptime format: []const u8, args: anytype) Fail {
        self.rejection = .{ .code = "ProtocolError", .message = try std.fmt.allocPrint(self.a, format, args) };
        return error.Rejected;
    }
};

/// What a lone surrogate escape reads as once `main.maskLoneSurrogates` has replaced it, and whether this request has one.
pub const lone_mark = "\u{FDD0}";
pub var masked = false;
pub const lone_message = "Cannot read incomplete UTF-16 JSON text as string with missing low surrogate.";

/// `JsonElement.GetString` throws on a lone surrogate, which `executeLine` answers as `SessionError`
/// (the parity test compares that code only).
/// .NET's property lookup reads the names of the object, so one with a lone surrogate throws as `readable` does.
pub fn lookable(r: *Request, object: std.json.ObjectMap) Fail!void {
    if (!masked) return;
    for (object.keys()) |key| _ = try readable(r, key);
}

pub fn readable(r: *Request, text: []const u8) Fail![]const u8 {
    if (masked and std.mem.indexOf(u8, text, lone_mark) != null) {
        r.rejection = .{ .code = "SessionError", .message = lone_message };
        return error.Rejected;
    }
    return text;
}

/// `String.CompareOrdinal` of UTF-8 text: UTF-16 unit order, where a supplementary code point
/// (a surrogate pair) sorts below U+E000..U+FFFF.
fn ordinal(_: void, x: []const u8, y: []const u8) bool {
    var xs = std.unicode.Utf8View.initUnchecked(x).iterator();
    var ys = std.unicode.Utf8View.initUnchecked(y).iterator();
    const unit = struct {
        fn f(c: u21) u21 {
            return if (c >= 0x10000) 0xD800 + ((c - 0x10000) >> 10) else c;
        }
    }.f;
    while (true) {
        const p = xs.nextCodepoint() orelse return ys.nextCodepoint() != null;
        const q = ys.nextCodepoint() orelse return false;
        if (p == q) continue;
        if (unit(p) != unit(q)) return unit(p) < unit(q);
        return p < q;
    }
}

/// `WireInput.ensureFields` of the object at `path` below the request root: duplicate names
/// first, then the unknown and the missing ones, each sorted.
pub fn fieldProblem(a: Allocator, line: []const u8, path: []const []const u8, object: std.json.ObjectMap, context: []const u8, fields: []const []const u8, required: []const []const u8) !?[]const u8 {
    if (try duplicateNames(a, line, path)) return "JSON object contains duplicate property names.";
    var unknown: std.ArrayList([]const u8) = .empty;
    for (object.keys()) |key| {
        for (fields) |field| {
            if (std.mem.eql(u8, key, field)) break;
        } else try unknown.append(a, key);
    }
    var missing: std.ArrayList([]const u8) = .empty;
    for (required) |field| if (object.get(field) == null) try missing.append(a, field);
    std.mem.sort([]const u8, unknown.items, {}, ordinal);
    std.mem.sort([]const u8, missing.items, {}, ordinal);
    if (unknown.items.len > 0) return try std.fmt.allocPrint(a, "{s} contains unsupported properties: {s}. Allowed: {s}.", .{ context, try std.mem.join(a, ", ", unknown.items), try std.mem.join(a, ", ", fields) });
    if (missing.items.len > 0) return try std.fmt.allocPrint(a, "{s} is missing required properties: {s}.", .{ context, try std.mem.join(a, ", ", missing.items) });
    return null;
}

/// Whether the request object (`path` empty) or the object value of its property `path[0]`
/// names a property twice. The parse keeps the last occurrence of a name, so the last such
/// property is the one checked.
fn duplicateNames(a: Allocator, line: []const u8, path: []const []const u8) !bool {
    std.debug.assert(path.len <= 1);
    const target: usize = path.len + 1;
    var scanner = std.json.Scanner.initCompleteInput(a, line);
    var names: std.StringHashMapUnmanaged(void) = .empty;
    var duplicate = false;
    // Per open container: whether it is an object, and whether its next string is a name.
    var objects: std.ArrayList(bool) = .empty;
    var expecting_name = false;
    var selected = path.len == 0;
    var follows_step = false;
    while (true) {
        const token = try scanner.nextAlloc(a, .alloc_always);
        const value_follows = follows_step;
        follows_step = false;
        switch (token) {
            .end_of_document => return duplicate,
            .object_begin, .array_begin => {
                const object = token == .object_begin;
                try objects.append(a, object);
                if (object and objects.items.len == target and (path.len == 0 or value_follows)) {
                    selected = true;
                    names = .empty;
                    duplicate = false;
                } else if (objects.items.len == target) selected = false;
                expecting_name = object;
                continue;
            },
            .object_end, .array_end => _ = objects.pop(),
            .allocated_string => |text| if (expecting_name) {
                if (objects.items.len == target and selected) {
                    if ((try names.fetchPut(a, text, {})) != null) duplicate = true;
                } else if (path.len == 1 and objects.items.len == 1) follows_step = std.mem.eql(u8, text, path[0]);
                expecting_name = false;
                continue;
            },
            else => {},
        }
        expecting_name = objects.items.len > 0 and objects.items[objects.items.len - 1];
    }
}

fn requiredString(r: *Request, object: std.json.ObjectMap, name: []const u8) Fail![]const u8 {
    const value = object.get(name) orelse return r.fail("Missing required property '{s}'.", .{name});
    if (value != .string) return r.fail("Property '{s}' must be a JSON string.", .{name});
    return readable(r, value.string);
}

// ---------------------------------------------------------------- text

fn utf16(a: Allocator, text: []const u8) ![]const u16 {
    return std.unicode.utf8ToUtf16LeAlloc(a, text) catch |e| switch (e) {
        error.OutOfMemory => error.OutOfMemory,
        else => &.{},
    };
}

/// The code points of UTF-16 text; a lone surrogate stands for itself.
fn points(a: Allocator, units: []const u16) ![]u21 {
    var out: std.ArrayList(u21) = .empty;
    var i: usize = 0;
    while (i < units.len) : (i += 1) {
        const unit = units[i];
        if (unit >= 0xD800 and unit <= 0xDBFF and i + 1 < units.len and units[i + 1] >= 0xDC00 and units[i + 1] <= 0xDFFF) {
            try out.append(a, 0x10000 + ((@as(u21, unit) - 0xD800) << 10) + (units[i + 1] - 0xDC00));
            i += 1;
        } else try out.append(a, unit);
    }
    return out.items;
}

/// One code point through the .NET invariant case runs (`ZigRuntime.caseRunTables`).
fn mapPoint(point: u21, runs: []const [4]i32) u21 {
    const code: i32 = point;
    var low: usize = 0;
    var high: usize = runs.len;
    while (low < high) {
        const middle = (low + high) / 2;
        if (runs[middle][0] <= code) low = middle + 1 else high = middle;
    }
    if (low > 0) {
        const run = runs[low - 1];
        if (code <= run[1] and @mod(code - run[0], run[2]) == 0) return @intCast(code + run[3]);
    }
    return point;
}

/// `String.Contains(text, StringComparison.OrdinalIgnoreCase)`: both sides upper-cased by code point.
// shortcut: .NET's ordinal casing is taken to be the invariant simple upper case; the parity transcript checks non-ASCII cases.
fn containsIgnoringCase(a: Allocator, haystack: []const u16, needle: []const u16) !bool {
    const h = try points(a, haystack);
    const n = try points(a, needle);
    for (h) |*p| p.* = mapPoint(p.*, &case_runs.upper);
    for (n) |*p| p.* = mapPoint(p.*, &case_runs.upper);
    return std.mem.indexOf(u21, h, n) != null;
}

/// `String.ToLowerInvariant`.
fn lowered(a: Allocator, units: []const u16) ![]const u16 {
    var out: std.ArrayList(u16) = .empty;
    for (try points(a, units)) |point| {
        const mapped = mapPoint(point, &case_runs.lower);
        if (mapped >= 0x10000) {
            const v = mapped - 0x10000;
            try out.appendSlice(a, &.{ @intCast(0xD800 + (v >> 10)), @intCast(0xDC00 + (v & 0x3ff)) });
        } else try out.append(a, @intCast(mapped));
    }
    return out.items;
}

/// `Char.IsWhiteSpace`.
fn isWhiteSpace(unit: u16) bool {
    return switch (unit) {
        0x09...0x0D, 0x20, 0x85, 0xA0, 0x1680, 0x2000...0x200A, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000 => true,
        else => false,
    };
}

fn isBlank(units: []const u16) bool {
    for (units) |unit| if (!isWhiteSpace(unit)) return false;
    return true;
}

/// `editDistance` of `WireSymbolic` (UTF-16 units).
fn editDistance(a: Allocator, x: []const u16, y: []const u16) !usize {
    var previous = try a.alloc(usize, y.len + 1);
    for (previous, 0..) |*cell, j| cell.* = j;
    var current = try a.alloc(usize, y.len + 1);
    for (1..x.len + 1) |i| {
        current[0] = i;
        for (1..y.len + 1) |j| {
            const substitution = previous[j - 1] + @intFromBool(x[i - 1] != y[j - 1]);
            current[j] = @min(substitution, @min(previous[j], current[j - 1]) + 1);
        }
        std.mem.swap([]usize, &previous, &current);
    }
    return previous[y.len];
}

fn quotedList(a: Allocator, names: []const []const u8) ![]const u8 {
    var out: std.ArrayList(u8) = .empty;
    for (names, 0..) |name, index| {
        if (index > 0) try out.appendSlice(a, ", ");
        try out.print(a, "'{s}'", .{name});
    }
    return out.items;
}

/// `candidateHint`: every name when there are few, otherwise the closest few.
pub fn candidateHint(a: Allocator, wanted_name: []const u8, all: []const []const u8) ![]const u8 {
    var names: std.ArrayList([]const u8) = .empty;
    for (all) |name| {
        if (name.len == 0) continue;
        for (names.items) |seen| {
            if (std.mem.eql(u8, seen, name)) break;
        } else try names.append(a, name);
    }
    if (names.items.len == 0) return "";
    if (names.items.len <= 10) return std.fmt.allocPrint(a, " Available: {s}.", .{try quotedList(a, names.items)});
    const wanted = try lowered(a, try utf16(a, wanted_name));
    const Close = struct { name: []const u8, distance: usize };
    var close: std.ArrayList(Close) = .empty;
    for (names.items) |name| {
        const lower = try lowered(a, try utf16(a, name));
        const distance = try editDistance(a, wanted, lower);
        if (distance <= @max(2, wanted.len / 3) or (wanted.len >= 3 and (std.mem.indexOf(u16, lower, wanted) != null or std.mem.indexOf(u16, wanted, lower) != null)))
            try close.append(a, .{ .name = name, .distance = distance });
    }
    // Stable, as `List.sortBy` is: ties keep the names' order.
    std.sort.insertion(Close, close.items, {}, struct {
        fn less(_: void, x: Close, y: Close) bool {
            return x.distance < y.distance;
        }
    }.less);
    if (close.items.len == 0) return "";
    var shown: std.ArrayList([]const u8) = .empty;
    for (close.items[0..@min(3, close.items.len)]) |item| try shown.append(a, item.name);
    return std.fmt.allocPrint(a, " Did you mean {s}?", .{try quotedList(a, shown.items)});
}

// ---------------------------------------------------------------- declarations

pub fn nameOf(item: Value) []const u8 {
    return model.utf8(model.decoded(model.property(item, "displayName").?));
}

pub fn idOf(item: Value) []const u8 {
    return model.utf8(model.decoded(model.property(item, "id").?));
}

fn contains(keys: []const []const u8, key: []const u8) bool {
    for (keys) |k| if (std.mem.eql(u8, k, key)) return true;
    return false;
}

fn hidden(r: *const Request) []const []const u8 {
    return if (r.provenance) |p| p.hidden else &.{};
}

/// The `kind` declarations of `AuthoringDeclarations`: the model's, less the hidden imports.
pub fn visible(r: *const Request, kind: []const u8) ![]const Value {
    const group = if (std.mem.eql(u8, kind, "function")) "functions" else if (std.mem.eql(u8, kind, "type")) "types" else if (std.mem.eql(u8, kind, "effect")) "effects" else "capabilities";
    var out: std.ArrayList(Value) = .empty;
    for (model.arrayItems(r.model, group)) |item| {
        if (!contains(hidden(r), try std.fmt.allocPrint(r.a, "{s}:{s}", .{ kind, idOf(item) }))) try out.append(r.a, item);
    }
    return out.items;
}

/// The members of the selected dependency that provide `kind:id`; none for a local declaration.
fn sources(r: *const Request, kind: []const u8, id: []const u8) ![]const wire.Provenance.Member {
    const p = r.provenance orelse return &.{};
    const key = try std.fmt.allocPrint(r.a, "{s}:{s}", .{ kind, id });
    var out: std.ArrayList(wire.Provenance.Member) = .empty;
    for (p.members) |member| if (contains(member.keys, key)) try out.append(r.a, member);
    return out.items;
}

pub fn exactOne(r: *Request, label: []const u8, wanted: []const u8, items: []const Value) Fail!Value {
    var found: ?Value = null;
    var count: usize = 0;
    var names: std.ArrayList([]const u8) = .empty;
    for (items) |item| {
        try names.append(r.a, nameOf(item));
        if (std.mem.eql(u8, nameOf(item), wanted)) {
            found = item;
            count += 1;
        }
    }
    if (count == 1) return found.?;
    if (count == 0) return r.fail("Unknown {s} '{s}'.{s}", .{ label, wanted, try candidateHint(r.a, wanted, names.items) });
    return r.fail("Ambiguous {s} '{s}'.", .{ label, wanted });
}

/// `JsonSerializer.Serialize` of a string.
fn stringOf(a: Allocator, text: []const u8) ![]const u8 {
    var out: Writer.Allocating = .init(a);
    try @import("root").writeString(&out.writer, text);
    return out.written();
}

/// `WireSymbolic.resolveSelector` of the property `key` of `object` (at `path`).
fn resolve(r: *Request, kind: []const u8, object: std.json.ObjectMap, key: []const u8, path: []const []const u8) Fail!Value {
    const items = try visible(r, kind);
    const selector = object.get(key) orelse return r.fail("Missing required property '{s}'.", .{key});
    if (selector == .string) return exactOne(r, kind, try readable(r, selector.string), items);
    if (selector != .object) return r.fail("Expected a JSON object.", .{});
    const fields = &.{ "id", "name", "dependencySnapshotId", "ownership" };
    try lookable(r, selector.object);
    if (try fieldProblem(r.a, r.line, path, selector.object, try std.fmt.allocPrint(r.a, "{s} selector", .{kind}), fields, &.{})) |message| return r.fail("{s}", .{message});
    return guarded(r, kind, items, selector.object);
}

/// The rest of `WireSymbolic.resolveSelector` for an object selector whose fields were checked.
pub fn guarded(r: *Request, kind: []const u8, items: []const Value, object: std.json.ObjectMap) Fail!Value {
    const fields = &.{ "id", "name", "dependencySnapshotId", "ownership" };
    var guards: [4]?[]const u8 = .{ null, null, null, null };
    inline for (fields, 0..) |field, index| {
        if (object.get(field) != null) guards[index] = try requiredString(r, object, field);
    }
    const id, const wanted_name, const dependency, const ownership = guards;
    if (id == null and wanted_name == null) return r.fail("A declaration selector needs id or name.", .{});
    if (ownership) |owned| if (!std.mem.eql(u8, owned, "local") and !std.mem.eql(u8, owned, "imported")) return r.fail("Selector ownership must be local or imported.", .{});
    var candidates: std.ArrayList(Value) = .empty;
    for (items) |item| {
        const provided = try sources(r, kind, idOf(item));
        if (id) |wanted| if (!std.mem.eql(u8, idOf(item), wanted)) continue;
        if (wanted_name) |wanted| if (!std.mem.eql(u8, nameOf(item), wanted)) continue;
        if (dependency) |pin| {
            for (provided) |member| {
                if (std.mem.eql(u8, member.id, pin)) break;
            } else continue;
        }
        if (ownership) |owned| if ((provided.len == 0) != std.mem.eql(u8, owned, "local")) continue;
        try candidates.append(r.a, item);
    }
    if (candidates.items.len == 1) return candidates.items[0];
    if (candidates.items.len > 1) return r.fail("Ambiguous {s} selector; specify an exact id or a providing dependencySnapshotId.", .{kind});
    // Advice never participates in resolution.
    var related: std.ArrayList(Value) = .empty;
    for (items) |item| {
        if (id) |wanted| {
            if (std.mem.eql(u8, idOf(item), wanted)) try related.append(r.a, item);
        } else if (std.mem.eql(u8, nameOf(item), wanted_name.?)) try related.append(r.a, item);
    }
    std.mem.sort(Value, related.items, {}, struct {
        fn less(_: void, x: Value, y: Value) bool {
            return std.mem.order(u16, model.decoded(model.property(x, "id").?), model.decoded(model.property(y, "id").?)) == .lt;
        }
    }.less);
    var advice: std.ArrayList([]const u8) = .empty;
    for (related.items[0..@min(5, related.items.len)]) |item| {
        const provided = try sources(r, kind, idOf(item));
        const owner = if (provided.len == 0) "local" else "imported";
        var pins: std.ArrayList([]const u8) = .empty;
        for (provided) |member| try pins.append(r.a, member.id);
        std.mem.sort([]const u8, pins.items, {}, ordinal);
        var mismatched: std.ArrayList([]const u8) = .empty;
        if (wanted_name) |wanted| if (!std.mem.eql(u8, wanted, nameOf(item))) try mismatched.append(r.a, "name");
        if (dependency) |pin| if (!contains(pins.items, pin)) try mismatched.append(r.a, "dependencySnapshotId");
        if (ownership) |owned| if (!std.mem.eql(u8, owned, owner)) try mismatched.append(r.a, "ownership");
        const omitted_pins = if (pins.items.len > 5) try std.fmt.allocPrint(r.a, " (+{d} more)", .{pins.items.len - 5}) else "";
        try advice.append(r.a, try std.fmt.allocPrint(r.a, "id={s}, name={s}, ownership={s}, providing pins=[{s}]{s}, mismatched guards=[{s}]", .{
            idOf(item),                                                 try stringOf(r.a, nameOf(item)),            owner,
            try std.mem.join(r.a, ", ", pins.items[0..@min(5, pins.items.len)]), omitted_pins, try std.mem.join(r.a, ", ", mismatched.items),
        }));
    }
    const omitted = if (related.items.len > 5) try std.fmt.allocPrint(r.a, " (+{d} more candidates)", .{related.items.len - 5}) else "";
    const context = if (advice.items.len == 0) "" else try std.fmt.allocPrint(r.a, " Current candidates (advice only): {s}{s}.", .{ try std.mem.join(r.a, "; ", advice.items), omitted });
    return r.fail("No current {s} matches the explicit declaration selector; inspect declarations and its provenance.{s}", .{ kind, context });
}

/// `WireInput.id`: an exact ID property, valid as `StableId.tryCreate` requires.
fn exactId(r: *Request, key: []const u8) Fail![]const u8 {
    const value = try requiredString(r, r.root, key);
    const units = try utf16(r.a, value);
    if (isBlank(units)) return r.fail("{s} is not a valid stable ID: BlankValue.", .{key});
    if (std.mem.indexOfAny(u8, value, "\r\n") != null) return r.fail("{s} is not a valid stable ID: ContainsLineBreak.", .{key});
    return value;
}

// ---------------------------------------------------------------- search

/// `WireEncode.search`: declarations whose display name contains `text` and string literals
/// that contain it, both ignoring case.
pub fn search(r: *Request, out: *Writer) Fail!void {
    if (try fieldProblem(r.a, r.line, &.{}, r.root, "search request", &.{ "id", "op", "text", "limit" }, &.{ "id", "op", "text" })) |message| return r.fail("{s}", .{message});
    const text = try requiredString(r, r.root, "text");
    const needle = try utf16(r.a, text);
    if (isBlank(needle)) return r.fail("text must not be empty.", .{});
    var limit: i64 = 50;
    if (r.root.get("limit")) |value| {
        limit = switch (value) {
            .integer => |number| if (number >= std.math.minInt(i32) and number <= std.math.maxInt(i32)) number else return r.fail("limit must be a JSON Int32.", .{}),
            else => return r.fail("limit must be a JSON Int32.", .{}),
        };
    }
    if (limit < 1 or limit > 200) return r.fail("limit must be 1..200.", .{});
    var matches: std.ArrayList([]const u8) = .empty;
    const hit = struct {
        fn f(a: Allocator, value: Value, n: []const u16) !bool {
            return containsIgnoringCase(a, model.decoded(value), n);
        }
    }.f;
    const named = struct {
        fn f(a: Allocator, kind: []const u8, key: []const u8, item: Value) ![]const u8 {
            var entry: Writer.Allocating = .init(a);
            try entry.writer.print("{{\"kind\":\"{s}\",\"name\":", .{kind});
            try model.writeString(&entry.writer, model.decoded(model.property(item, "displayName").?));
            try entry.writer.print(",\"{s}\":", .{key});
            try model.writeString(&entry.writer, model.decoded(model.property(item, "id").?));
            try entry.writer.writeByte('}');
            return entry.written();
        }
    }.f;
    const groups = [_][3][]const u8{ .{ "function", "function", "functionId" }, .{ "type", "type", "typeId" }, .{ "effect", "effect", "effectId" } };
    for (groups) |group| for (try visible(r, group[0])) |item| {
        if (try hit(r.a, model.property(item, "displayName").?, needle)) try matches.append(r.a, try named(r.a, group[1], group[2], item));
    };
    for (try visible(r, "capability")) |item| {
        if (try hit(r.a, model.property(item, "displayName").?, needle)) try matches.append(r.a, try named(r.a, "capability", "capabilityId", item));
        for (model.arrayItems(item, "operations")) |operation| if (try hit(r.a, model.property(operation, "displayName").?, needle)) {
            var entry: Writer.Allocating = .init(r.a);
            try entry.writer.writeAll("{\"kind\":\"capabilityOperation\",\"capability\":");
            try model.writeString(&entry.writer, model.decoded(model.property(item, "displayName").?));
            try entry.writer.writeAll(",\"name\":");
            try model.writeString(&entry.writer, model.decoded(model.property(operation, "displayName").?));
            try entry.writer.writeAll(",\"operationId\":");
            try model.writeString(&entry.writer, model.decoded(model.property(operation, "id").?));
            try entry.writer.writeByte('}');
            try matches.append(r.a, entry.written());
        };
    }
    for (try visible(r, "function")) |function| for (model.bodyNodes(r.a, function)) |node| {
        if (!std.mem.eql(u16, model.decoded(model.property(node, "kind").?), &.{ 's', 't', 'r', 'i', 'n', 'g', 'L', 'i', 't', 'e', 'r', 'a', 'l' })) continue;
        const value = model.decoded(model.property(node, "value").?);
        if (!try containsIgnoringCase(r.a, value, needle)) continue;
        var entry: Writer.Allocating = .init(r.a);
        try entry.writer.writeAll("{\"kind\":\"stringLiteral\",\"function\":");
        try model.writeString(&entry.writer, model.decoded(model.property(function, "displayName").?));
        try entry.writer.writeAll(",\"functionId\":");
        try model.writeString(&entry.writer, model.decoded(model.property(function, "id").?));
        try entry.writer.writeAll(",\"nodeId\":");
        try model.writeString(&entry.writer, model.decoded(model.property(node, "id").?));
        try entry.writer.writeAll(",\"value\":");
        try model.writeString(&entry.writer, if (value.len <= 120) value else try std.mem.concat(r.a, u16, &.{ value[0..120], &.{0x2026} }));
        try entry.writer.writeByte('}');
        try matches.append(r.a, entry.written());
    };
    try out.writeAll("{\"text\":");
    try model.writeString(out, needle);
    try out.print(",\"totalCount\":{d},\"matches\":[", .{matches.items.len});
    try out.writeAll(try std.mem.join(r.a, ",", matches.items[0..@min(matches.items.len, @as(usize, @intCast(limit)))]));
    try out.writeAll("]}");
}

// ---------------------------------------------------------------- references

const Subject = struct { kind: []const u8, id: []const u8 };

/// `referenceSubject`: the subject by its exact ID key or by display names.
fn subjectOf(r: *Request) Fail!Subject {
    const kind = try requiredString(r, r.root, "kind");
    const has = struct {
        fn f(object: std.json.ObjectMap, key: []const u8) bool {
            return object.get(key) != null;
        }
    }.f;
    const Form = struct { exact: []const []const u8, names: []const []const u8 };
    const forms = [_]struct { []const u8, Form }{
        .{ "function", .{ .exact = &.{"functionId"}, .names = &.{"function"} } },
        .{ "type", .{ .exact = &.{"typeId"}, .names = &.{"type"} } },
        .{ "effect", .{ .exact = &.{"effectId"}, .names = &.{"effect"} } },
        .{ "capability", .{ .exact = &.{"capabilityId"}, .names = &.{"capability"} } },
        .{ "capabilityOperation", .{ .exact = &.{"operationId"}, .names = &.{ "capability", "operation" } } },
        .{ "variant", .{ .exact = &.{"variantId"}, .names = &.{ "type", "variant" } } },
        .{ "field", .{ .exact = &.{"fieldId"}, .names = if (has(r.root, "variant")) &.{ "type", "variant", "field" } else &.{ "type", "field" } } },
    };
    const form = for (forms) |entry| {
        if (std.mem.eql(u8, entry[0], kind)) break entry[1];
    } else return r.fail("Unsupported references subject kind '{s}'; use function, type, variant, field, effect, capability or capabilityOperation.", .{kind});
    // `exactly`: the fields, then exactly one complete form.
    var allowed: std.ArrayList([]const u8) = .empty;
    try allowed.appendSlice(r.a, &.{ "id", "op", "kind" });
    try allowed.appendSlice(r.a, form.exact);
    try allowed.appendSlice(r.a, form.names);
    if (try fieldProblem(r.a, r.line, &.{}, r.root, try std.fmt.allocPrint(r.a, "references request for a {s}", .{kind}), allowed.items, &.{ "id", "op", "kind" })) |message| return r.fail("{s}", .{message});
    var exact = true;
    for (form.exact) |key| exact = exact and has(r.root, key);
    var named = true;
    for (form.names) |key| named = named and has(r.root, key);
    var present: usize = 0;
    for (allowed.items[3..]) |key| present += @intFromBool(has(r.root, key));
    if (exact == named or present > (if (exact) form.exact.len else form.names.len))
        return r.fail("A references request for a {s} names its subject by exactly one of {s} (exact) and {s} (display names).", .{ kind, try std.mem.join(r.a, ", ", form.exact), try std.mem.join(r.a, ", ", form.names) });
    if (exact) return .{ .kind = kind, .id = try exactId(r, form.exact[0]) };
    const is = struct {
        fn f(x: []const u8, comptime y: []const u8) bool {
            return std.mem.eql(u8, x, y);
        }
    }.f;
    if (is(kind, "function") or is(kind, "type") or is(kind, "effect") or is(kind, "capability"))
        return .{ .kind = kind, .id = idOf(try resolve(r, kind, r.root, kind, &.{kind})) };
    if (is(kind, "capabilityOperation")) {
        const owner = try resolve(r, "capability", r.root, "capability", &.{"capability"});
        const label = try std.fmt.allocPrint(r.a, "operation on {s}", .{nameOf(owner)});
        return .{ .kind = kind, .id = idOf(try exactOne(r, label, try requiredString(r, r.root, "operation"), model.arrayItems(owner, "operations"))) };
    }
    // F# reads each display name before the member lookup that uses it.
    const owner = try resolve(r, "type", r.root, "type", &.{"type"});
    const union_type = model.property(owner, "variants") != null;
    if (is(kind, "variant") or has(r.root, "variant")) {
        const variant_name = try requiredString(r, r.root, "variant");
        if (!union_type) return r.fail("Type '{s}' is not a union type.", .{nameOf(owner)});
        const variant = try exactOne(r, try std.fmt.allocPrint(r.a, "variant on {s}", .{nameOf(owner)}), variant_name, model.arrayItems(owner, "variants"));
        if (is(kind, "variant")) return .{ .kind = kind, .id = idOf(variant) };
        return .{ .kind = kind, .id = idOf(try exactOne(r, try std.fmt.allocPrint(r.a, "field on variant {s}", .{nameOf(variant)}), try requiredString(r, r.root, "field"), model.arrayItems(variant, "fields"))) };
    }
    const field_name = try requiredString(r, r.root, "field");
    if (union_type) return r.fail("Type '{s}' is not a product type.", .{nameOf(owner)});
    return .{ .kind = kind, .id = idOf(try exactOne(r, try std.fmt.allocPrint(r.a, "field on {s}", .{nameOf(owner)}), field_name, model.arrayItems(owner, "fields"))) };
}

pub fn writeId(out: *Writer, units: Str) !void {
    try model.writeString(out, units);
}

/// `"displayName"` of a visible `kind` declaration.
fn visibleNamed(r: *const Request, kind: []const u8, id: Str) !?Value {
    for (try visible(r, kind)) |item| if (std.mem.eql(u16, model.decoded(model.property(item, "id").?), id)) return item;
    return null;
}

fn writeNamed(out: *Writer, field: []const u8, item: Value) !void {
    try out.print(",\"{s}\":", .{field});
    try model.writeString(out, model.decoded(model.property(item, "displayName").?));
}

fn same(item: Value, id: Str) bool {
    return std.mem.eql(u16, model.decoded(model.property(item, "id").?), id);
}

/// `WireEncode.references`.
pub fn references(r: *Request, out: *Writer) Fail!void {
    const subject = try subjectOf(r);
    const id = try utf16(r.a, subject.id);
    const found = model.referencesTo(r.a, r.model, subject.kind, id) orelse {
        r.rejection = .{ .code = "SemanticQueryFailure", .message = try std.fmt.allocPrint(r.a, "No {s} has the ID '{s}'.", .{ subject.kind, subject.id }) };
        return error.Rejected;
    };
    try out.print("{{\"subject\":{{\"kind\":\"{s}\",\"id\":", .{subject.kind});
    try writeId(out, id);
    const kind = subject.kind;
    if (std.mem.eql(u8, kind, "variant") or std.mem.eql(u8, kind, "field")) {
        for (try visible(r, "type")) |item| {
            for (model.arrayItems(item, "fields")) |field| if (std.mem.eql(u8, kind, "field") and same(field, id)) {
                try writeNamed(out, "displayName", field);
                try out.writeAll(",\"ownerTypeId\":");
                try writeId(out, model.decoded(model.property(item, "id").?));
                try writeNamed(out, "ownerDisplayName", item);
                break;
            };
            for (model.arrayItems(item, "variants")) |variant| {
                if (std.mem.eql(u8, kind, "variant") and same(variant, id)) {
                    try writeNamed(out, "displayName", variant);
                    try out.writeAll(",\"ownerTypeId\":");
                    try writeId(out, model.decoded(model.property(item, "id").?));
                    try writeNamed(out, "ownerDisplayName", item);
                }
                for (model.arrayItems(variant, "fields")) |field| if (std.mem.eql(u8, kind, "field") and same(field, id)) {
                    try writeNamed(out, "displayName", field);
                    try out.writeAll(",\"ownerTypeId\":");
                    try writeId(out, model.decoded(model.property(item, "id").?));
                    try writeNamed(out, "ownerDisplayName", item);
                    try out.writeAll(",\"ownerVariantId\":");
                    try writeId(out, model.decoded(model.property(variant, "id").?));
                    try writeNamed(out, "ownerVariantDisplayName", variant);
                };
            }
        }
    } else if (std.mem.eql(u8, kind, "capabilityOperation")) {
        for (try visible(r, "capability")) |item| for (model.arrayItems(item, "operations")) |operation| if (same(operation, id)) {
            try writeNamed(out, "displayName", operation);
            try out.writeAll(",\"ownerCapabilityId\":");
            try writeId(out, model.decoded(model.property(item, "id").?));
            try writeNamed(out, "ownerDisplayName", item);
        };
    } else if (try visibleNamed(r, kind, id)) |item| try writeNamed(out, "displayName", item);
    try out.print("}},\"count\":{d},\"references\":[", .{found.len});
    for (found, 0..) |reference, index| {
        if (index > 0) try out.writeByte(',');
        const owner_kind = @tagName(reference.owner);
        try out.print("{{\"ownerKind\":\"{s}\",\"ownerId\":", .{owner_kind});
        try writeId(out, reference.owner_id);
        if (try visibleNamed(r, owner_kind, reference.owner_id)) |item| try writeNamed(out, "ownerDisplayName", item);
        const site = reference.site;
        const op = ",\"operationId\":";
        switch (site.tag) {
            .parameter_type => {
                try out.writeAll(",\"site\":\"parameterType\",\"parameterId\":");
                try writeId(out, site.a.?);
            },
            .result_type => try out.writeAll(",\"site\":\"resultType\""),
            .required_effect => try out.writeAll(",\"site\":\"requiredEffect\""),
            .field_type => {
                try out.writeAll(",\"site\":\"fieldType\",\"fieldId\":");
                try writeId(out, site.b);
                if (site.a) |variant| {
                    try out.writeAll(",\"variantId\":");
                    try writeId(out, variant);
                }
            },
            .operation_parameter_type => {
                try out.writeAll(",\"site\":\"operationParameterType\"" ++ op);
                try writeId(out, site.a.?);
                try out.writeAll(",\"parameterId\":");
                try writeId(out, site.b);
            },
            .operation_result_type, .operation_failure_type, .operation_effect => {
                try out.print(",\"site\":\"{s}\"" ++ op, .{switch (site.tag) {
                    .operation_result_type => "operationResultType",
                    .operation_failure_type => "operationFailureType",
                    else => "operationEffect",
                }});
                try writeId(out, site.a.?);
            },
            .node => {
                try out.writeAll(",\"site\":\"node\",\"nodeId\":");
                try writeId(out, site.a.?);
                try out.print(",\"role\":\"{s}\"", .{site.role});
            },
        }
        try out.writeByte('}');
    }
    try out.writeAll("]}");
}

// ---------------------------------------------------------------- unreferenced

const Key = struct { kind: []const u8, id: Str };

fn keyEql(x: Key, y: Key) bool {
    return std.mem.eql(u8, x.kind, y.kind) and std.mem.eql(u16, x.id, y.id);
}

/// `unreferenced` of `AuthoringProtocol`: the local functions, capability operations and types
/// nothing else refers to, or that `roots` (function names) do not reach.
pub fn unreferenced(r: *Request, out: *Writer) Fail!void {
    if (try fieldProblem(r.a, r.line, &.{}, r.root, "unreferenced request", &.{ "id", "op", "roots" }, &.{ "id", "op" })) |message| return r.fail("{s}", .{message});
    var root_names: ?[]const []const u8 = null;
    if (r.root.get("roots")) |value| {
        if (value != .array) return r.fail("Property 'roots' must be a JSON array.", .{});
        var names: std.ArrayList([]const u8) = .empty;
        for (value.array.items) |item| {
            if (item != .string) return r.fail("roots entries must be function names.", .{});
            try names.append(r.a, try readable(r, item.string));
        }
        root_names = names.items;
    }
    const functions = try visible(r, "function");
    const types = try visible(r, "type");
    const capabilities = try visible(r, "capability");
    const Subject_ = struct { key: Key, kind: []const u8 };
    var subjects: std.ArrayList(Subject_) = .empty;
    const decodedId = struct {
        fn f(item: Value) Str {
            return model.decoded(model.property(item, "id").?);
        }
    }.f;
    for (functions) |item| try subjects.append(r.a, .{ .key = .{ .kind = "function", .id = decodedId(item) }, .kind = "function" });
    for (types) |item| try subjects.append(r.a, .{ .key = .{ .kind = "type", .id = decodedId(item) }, .kind = "type" });
    for (capabilities) |item| for (model.arrayItems(item, "operations")) |operation|
        try subjects.append(r.a, .{ .key = .{ .kind = "operation", .id = decodedId(operation) }, .kind = "capabilityOperation" });
    for (capabilities) |item| try subjects.append(r.a, .{ .key = .{ .kind = "capability", .id = decodedId(item) }, .kind = "capability" });
    const Edge = struct { owner: Key, target: Key };
    var edges: std.ArrayList(Edge) = .empty;
    for (subjects.items) |subject| {
        const found = model.referencesTo(r.a, r.model, subject.kind, subject.key.id) orelse continue;
        for (found) |reference| {
            const owner: Key = switch (reference.owner) {
                .function => .{ .kind = "function", .id = reference.owner_id },
                .type => .{ .kind = "type", .id = reference.owner_id },
                .capability => switch (reference.site.tag) {
                    .operation_parameter_type, .operation_result_type, .operation_failure_type, .operation_effect => .{ .kind = "operation", .id = reference.site.a.? },
                    else => .{ .kind = "capability", .id = reference.owner_id },
                },
            };
            try edges.append(r.a, .{ .owner = owner, .target = subject.key });
        }
    }
    var roots: std.ArrayList(Key) = .empty;
    if (root_names) |names| for (names) |wanted| {
        var match: ?Value = null;
        var count: usize = 0;
        for (functions) |item| if (std.mem.eql(u8, nameOf(item), wanted)) {
            match = item;
            count += 1;
        };
        if (count == 0) return r.fail("Unknown function '{s}'.", .{wanted});
        if (count > 1) return r.fail("Ambiguous function '{s}'.", .{wanted});
        try roots.append(r.a, .{ .kind = "function", .id = decodedId(match.?) });
    };
    // The keys that stay: unreferenced by anything but themselves, or unreachable from the roots.
    var marked: std.ArrayList(Key) = .empty;
    if (root_names == null) {
        for (edges.items) |edge| if (!keyEql(edge.owner, edge.target)) try marked.append(r.a, edge.target);
    } else {
        var frontier = roots;
        while (frontier.pop()) |item| {
            const seen = for (marked.items) |m| {
                if (keyEql(m, item)) break true;
            } else false;
            if (seen) continue;
            try marked.append(r.a, item);
            for (edges.items) |edge| if (keyEql(edge.owner, item)) try frontier.append(r.a, edge.target);
        }
    }
    const keep = struct {
        fn f(m: []const Key, key: Key) bool {
            for (m) |item| if (keyEql(item, key)) return false;
            return true;
        }
    }.f;
    const Row = struct { sort: []const Str, item: Value, owner: ?Value = null };
    const rowLess = struct {
        fn less(_: void, x: Row, y: Row) bool {
            for (x.sort, y.sort) |p, q| switch (std.mem.order(u16, p, q)) {
                .lt => return true,
                .gt => return false,
                .eq => {},
            };
            return false;
        }
    }.less;
    const local = struct {
        fn f(req: *const Request, kind: []const u8, item: Value) !bool {
            return (try sources(req, kind, idOf(item))).len == 0;
        }
    }.f;
    const displayName = struct {
        fn f(item: Value) Str {
            return model.decoded(model.property(item, "displayName").?);
        }
    }.f;
    var unused_functions: std.ArrayList(Row) = .empty;
    for (functions) |item| if (try local(r, "function", item) and keep(marked.items, .{ .kind = "function", .id = decodedId(item) }))
        try unused_functions.append(r.a, .{ .sort = try r.a.dupe(Str, &.{ displayName(item), decodedId(item) }), .item = item });
    var unused_operations: std.ArrayList(Row) = .empty;
    for (capabilities) |capability| if (try local(r, "capability", capability)) for (model.arrayItems(capability, "operations")) |operation| {
        if (keep(marked.items, .{ .kind = "operation", .id = decodedId(operation) }))
            try unused_operations.append(r.a, .{ .sort = try r.a.dupe(Str, &.{ displayName(operation), displayName(capability), decodedId(operation) }), .item = operation, .owner = capability });
    };
    var unused_types: std.ArrayList(Row) = .empty;
    for (types) |item| if (try local(r, "type", item) and keep(marked.items, .{ .kind = "type", .id = decodedId(item) }))
        try unused_types.append(r.a, .{ .sort = try r.a.dupe(Str, &.{ displayName(item), decodedId(item) }), .item = item });
    inline for (.{ &unused_functions, &unused_operations, &unused_types }) |rows| std.mem.sort(Row, rows.items, {}, rowLess);
    const lists = [_]struct { []const u8, []const Row }{ .{ "functions", unused_functions.items }, .{ "operations", unused_operations.items }, .{ "types", unused_types.items } };
    try out.writeByte('{');
    for (lists, 0..) |list, index| {
        if (index > 0) try out.writeByte(',');
        try out.print("\"{s}\":[", .{list[0]});
        for (list[1], 0..) |row, at| {
            if (at > 0) try out.writeByte(',');
            try out.writeByte('{');
            if (row.owner) |capability| {
                try out.writeAll("\"capabilityId\":");
                try writeId(out, decodedId(capability));
                try writeNamed(out, "capability", capability);
                try out.writeByte(',');
            }
            try out.writeAll("\"id\":");
            try writeId(out, decodedId(row.item));
            try writeNamed(out, "name", row.item);
            try out.writeByte('}');
        }
        try out.writeByte(']');
    }
    try out.writeByte('}');
}

// ---------------------------------------------------------------- bindingScopes

/// `BindingFrame`: a nested scope that binds (Bool operands and branches open none).
const Frame = struct { owner: Value, role: []const u8, variants: []const Value = &.{}, bindings: []const Binding };
/// A frame's binding: its slot (`field` with the source field, `value` or `error`) and pattern.
const Binding = struct { slot: []const u8, field: ?Value = null, pattern: Value };

fn kindIs(node: Value, comptime kind: []const u8) bool {
    const units = model.decoded(model.property(node, "kind").?);
    if (units.len != kind.len) return false;
    for (units, kind) |unit, c| if (unit != c) return false;
    return true;
}

/// `WireEncodeCommon.bindingFrames` up to the node `wanted`: its enclosing frames, innermost first.
fn framesAt(a: Allocator, graph: Value, enclosing: []const Frame, wanted: Str) !?[]const Frame {
    for (model.arrayItems(graph, "nodes")) |node| {
        if (std.mem.eql(u16, model.decoded(model.property(node, "id").?), wanted)) return enclosing;
        const Nested = struct { graph: Value, frame: ?Frame };
        var nested: std.ArrayList(Nested) = .empty;
        const field = struct {
            fn f(n: Value, key: []const u8) Value {
                return model.property(n, key).?;
            }
        }.f;
        const one = struct {
            fn f(al: Allocator, slot: []const u8, pattern: Value) ![]const Binding {
                return al.dupe(Binding, &.{.{ .slot = slot, .pattern = pattern }});
            }
        }.f;
        if (kindIs(node, "conditional")) {
            try nested.appendSlice(a, &.{ .{ .graph = field(node, "whenTrue"), .frame = null }, .{ .graph = field(node, "whenFalse"), .frame = null } });
        } else if (kindIs(node, "boolAnd") or kindIs(node, "boolOr")) {
            try nested.append(a, .{ .graph = field(node, "right"), .frame = null });
        } else if (kindIs(node, "matchUnion")) {
            for (model.arrayItems(node, "arms")) |arm| {
                var bindings: std.ArrayList(Binding) = .empty;
                for (model.arrayItems(arm, "fieldBindings")) |item|
                    try bindings.append(a, .{ .slot = "field", .field = field(item, "sourceFieldId"), .pattern = field(item, "binding") });
                const variants = if (model.property(arm, "variantId")) |single| try a.dupe(Value, &.{single}) else model.arrayItems(arm, "variantIds");
                try nested.append(a, .{ .graph = field(arm, "body"), .frame = .{ .owner = node, .role = "unionArm", .variants = variants, .bindings = bindings.items } });
            }
        } else if (kindIs(node, "bind")) {
            const body = field(node, "boundBody");
            try nested.append(a, .{ .graph = field(body, "body"), .frame = .{ .owner = node, .role = "bind", .bindings = try one(a, "value", field(body, "valueBinding")) } });
        } else if (kindIs(node, "matchOption")) {
            const some = field(node, "someArm");
            try nested.append(a, .{ .graph = field(some, "body"), .frame = .{ .owner = node, .role = "optionSome", .bindings = try one(a, "value", field(some, "valueBinding")) } });
            try nested.append(a, .{ .graph = field(node, "noneBody"), .frame = .{ .owner = node, .role = "optionNone", .bindings = &.{} } });
        } else if (kindIs(node, "matchResult")) {
            const ok_arm = field(node, "okArm");
            const err_arm = field(node, "errArm");
            try nested.append(a, .{ .graph = field(ok_arm, "body"), .frame = .{ .owner = node, .role = "resultOk", .bindings = try one(a, "value", field(ok_arm, "valueBinding")) } });
            try nested.append(a, .{ .graph = field(err_arm, "body"), .frame = .{ .owner = node, .role = "resultErr", .bindings = try one(a, "error", field(err_arm, "errorBinding")) } });
        }
        for (nested.items) |scope| {
            const frames = if (scope.frame) |frame| try std.mem.concat(a, Frame, &.{ &.{frame}, enclosing }) else enclosing;
            if (try framesAt(a, scope.graph, frames, wanted)) |found| return found;
        }
    }
    return null;
}

/// The display name of a visible type's variant or field with ID `id` (`variants` or `fields`).
fn memberName(r: *const Request, comptime variants: bool, id: Str) !?Value {
    for (try visible(r, "type")) |item| {
        if (!variants) for (model.arrayItems(item, "fields")) |f| if (same(f, id)) return f;
        for (model.arrayItems(item, "variants")) |variant| {
            if (variants and same(variant, id)) return variant;
            if (!variants) for (model.arrayItems(variant, "fields")) |f| if (same(f, id)) return f;
        }
    }
    return null;
}

/// `bindingScopes`: the frames visible at one node of a function, innermost first.
pub fn bindingScopes(r: *Request, out: *Writer) Fail!void {
    if (try fieldProblem(r.a, r.line, &.{}, r.root, "bindingScopes request", &.{ "id", "op", "function", "functionId", "nodeId" }, &.{ "id", "op", "nodeId" })) |message| return r.fail("{s}", .{message});
    const by_name, const by_id = .{ r.root.get("function") != null, r.root.get("functionId") != null };
    if (by_name == by_id) return r.fail("bindingScopes requires exactly one of function and functionId.", .{});
    const function = if (by_name) try resolve(r, "function", r.root, "function", &.{"function"}) else blk: {
        const wanted = try exactId(r, "functionId");
        for (try visible(r, "function")) |item| if (std.mem.eql(u8, idOf(item), wanted)) break :blk item;
        return r.fail("No function has the ID '{s}'.", .{wanted});
    };
    const node_id = try exactId(r, "nodeId");
    const frames = try framesAt(r.a, model.property(function, "body").?, &.{}, try utf16(r.a, node_id)) orelse
        return r.fail("Function '{s}' has no node '{s}'.", .{ nameOf(function), node_id });
    const names = model.arrayItems(function, "typeParameters");
    try out.writeAll("{\"functionId\":");
    try writeId(out, model.decoded(model.property(function, "id").?));
    try out.writeAll(",\"nodeId\":");
    try writeId(out, try utf16(r.a, node_id));
    try out.writeAll(",\"frames\":[");
    for (frames, 0..) |frame, depth| {
        if (depth > 0) try out.writeByte(',');
        try out.print("{{\"scopeDepth\":{d},\"ownerNodeId\":", .{depth});
        try writeId(out, model.decoded(model.property(frame.owner, "id").?));
        try out.print(",\"role\":\"{s}\"", .{frame.role});
        if (frame.variants.len > 0) {
            try out.writeAll(",\"variantIds\":[");
            var known = true;
            for (frame.variants, 0..) |variant, index| {
                if (index > 0) try out.writeByte(',');
                try writeId(out, model.decoded(variant));
                known = known and try memberName(r, true, model.decoded(variant)) != null;
            }
            try out.writeByte(']');
            if (known) {
                try out.writeAll(",\"variants\":[");
                for (frame.variants, 0..) |variant, index| {
                    if (index > 0) try out.writeByte(',');
                    try model.writeString(out, model.decoded(model.property((try memberName(r, true, model.decoded(variant))).?, "displayName").?));
                }
                try out.writeByte(']');
            }
        }
        try out.writeAll(",\"bindings\":[");
        for (frame.bindings, 0..) |binding, index| {
            if (index > 0) try out.writeByte(',');
            try out.print("{{\"slot\":\"{s}\"", .{binding.slot});
            if (binding.field) |source| {
                try out.writeAll(",\"fieldId\":");
                try writeId(out, model.decoded(source));
                if (try memberName(r, false, model.decoded(source))) |f| try writeNamed(out, "field", f);
            }
            try out.writeAll(",\"bindingId\":");
            try writeId(out, model.decoded(model.property(binding.pattern, "id").?));
            try out.writeAll(",\"type\":");
            try wire.typeRef(out, model.property(binding.pattern, "type").?, names);
            if (model.property(binding.pattern, "displayName")) |display| {
                try out.writeAll(",\"displayName\":");
                try model.writeString(out, model.decoded(display));
            }
            try out.writeByte('}');
        }
        try out.writeAll("]}");
    }
    try out.writeAll("]}");
}

// ---------------------------------------------------------------- readFunction

pub fn writeIds(out: *Writer, ids: []const Str) !void {
    try out.writeByte('[');
    for (ids, 0..) |id, index| {
        if (index > 0) try out.writeByte(',');
        try writeId(out, id);
    }
    try out.writeByte(']');
}

pub fn writeSites(out: *Writer, sites: []const model.CallSite) !void {
    try out.writeByte('[');
    for (sites, 0..) |site, index| {
        if (index > 0) try out.writeByte(',');
        try out.writeAll("{\"callerFunctionId\":");
        try writeId(out, site.caller);
        try out.writeAll(",\"callNodeId\":");
        try writeId(out, site.node);
        try out.writeAll(",\"calleeFunctionId\":");
        try writeId(out, site.callee);
        try out.writeByte('}');
    }
    try out.writeByte(']');
}

/// `literals`: the String, Int64 and Bool literals of a body as `nodeLiteral` writes them.
fn literals(r: *Request, out: *Writer, function: Value) !void {
    try out.writeByte('[');
    var count: usize = 0;
    for (model.bodyNodes(r.a, function)) |node| {
        const kind = model.decoded(model.property(node, "kind").?);
        const is = struct {
            fn f(units: Str, comptime name: []const u8) bool {
                if (units.len != name.len) return false;
                for (units, name) |unit, c| if (unit != c) return false;
                return true;
            }
        }.f;
        if (!is(kind, "stringLiteral") and !is(kind, "int64Literal") and !is(kind, "boolLiteral")) continue;
        if (count > 0) try out.writeByte(',');
        count += 1;
        try out.writeAll("{\"nodeId\":");
        try writeId(out, model.decoded(model.property(node, "id").?));
        const value = model.property(node, "value").?;
        if (is(kind, "boolLiteral")) {
            try out.writeAll(if (value == .json_true) ",\"literal\":{\"kind\":\"bool\",\"value\":true}}" else ",\"literal\":{\"kind\":\"bool\",\"value\":false}}");
        } else if (is(kind, "int64Literal")) {
            try out.writeAll(",\"literal\":{\"kind\":\"int64\",\"value\":");
            switch (value) {
                .number => |digits| try out.print("\"{s}\"", .{digits}),
                else => try writeId(out, model.decoded(value)),
            }
            try out.writeAll("}}");
        } else {
            const text = model.decoded(value);
            const limit = 256;
            try out.writeAll(",\"literal\":{\"kind\":\"string\",\"value\":");
            if (text.len <= limit) {
                try model.writeString(out, text);
            } else {
                // Never split a surrogate pair at the cut.
                const cut: usize = if (text[limit - 1] >= 0xD800 and text[limit - 1] <= 0xDBFF and text[limit] >= 0xDC00 and text[limit] <= 0xDFFF) limit - 1 else limit;
                try model.writeString(out, text[0..cut]);
                try out.print(",\"truncated\":true,\"length\":{d}", .{text.len});
            }
            try out.writeAll("}}");
        }
    }
    try out.writeByte(']');
}

/// `readFunction` at `detail: "compact"` (`WireEncodeCommon.compactFocus` and the literals) or
/// `"outline"` (`WireOutline.focus`).
pub fn readFunction(r: *Request, out: *Writer) Fail!void {
    if (try fieldProblem(r.a, r.line, &.{}, r.root, "readFunction request", &.{ "id", "op", "function", "functionId", "detail" }, &.{ "id", "op" })) |message| return r.fail("{s}", .{message});
    const detail = if (r.root.get("detail") != null) try requiredString(r, r.root, "detail") else "full";
    const details = [_][]const u8{ "compact", "outline", "full" };
    for (details) |known| {
        if (std.mem.eql(u8, known, detail)) break;
    } else return r.fail("detail must be compact, outline or full.", .{});
    const by_name, const by_id = .{ r.root.get("function") != null, r.root.get("functionId") != null };
    if (by_name == by_id) return r.fail("readFunction requires exactly one of function and functionId.", .{});
    const wanted = if (by_name) model.decoded(model.property(try resolve(r, "function", r.root, "function", &.{"function"}), "id").?) else try utf16(r.a, try exactId(r, "functionId"));
    // `FocusFunction` reads the whole model, the dependency's private functions included.
    const function = for (model.arrayItems(r.model, "functions")) |item| {
        if (same(item, wanted)) break item;
    } else {
        r.rejection = .{ .code = "SemanticQueryFailure", .message = try std.fmt.allocPrint(r.a, "No function has the ID '{s}'.", .{model.utf8(wanted)}) };
        return error.Rejected;
    };
    const nodes = model.bodyNodes(r.a, function);
    const updates = for (nodes) |node| {
        if (kindIs(node, "bytesSet") or kindIs(node, "bytesAppend")) break true;
    } else false;
    // The compact focus, open at its end, so each detail appends its own fields.
    var compact: Writer.Allocating = .init(r.a);
    const full = std.mem.eql(u8, detail, "full");
    try compactFocus(r, &compact.writer, function, wanted, nodes, full);
    if (full) {
        try out.writeAll(compact.written());
    } else if (std.mem.eql(u8, detail, "outline")) {
        try outline(r, out, compact.written(), function, nodes);
    } else {
        try out.writeAll(compact.written());
        try out.writeAll(",\"literals\":");
        try literals(r, out, function);
    }
    if (updates) try copiedUpdates(r, out, wanted);
    try out.writeByte('}');
}

// ---------------------------------------------------------------- copiedUpdates

const Json = std.json.Value;

fn jsonString(value: Json, name: []const u8) []const u8 {
    return value.object.get(name).?.string;
}

/// `ExecutionIrBytesOwnership.plan`'s copied updates of one lowered function: each Bytes update
/// whose input (or a name of the same buffer) has a use that can run after it, with that use's origin.
const Plan = struct {
    a: Allocator,
    defined: std.StringHashMapUnmanaged(void) = .empty,
    parent: std.StringHashMapUnmanaged([]const u8) = .empty,

    fn find(self: *Plan, id: []const u8) []const u8 {
        var current = id;
        while (self.parent.get(current)) |next| {
            if (std.mem.eql(u8, next, current)) break;
            current = next;
        }
        return current;
    }

    fn join(self: *Plan, x: []const u8, y: []const u8) !void {
        const left, const right = .{ self.find(x), self.find(y) };
        if (!std.mem.eql(u8, left, right)) try self.parent.put(self.a, left, right);
    }

    /// `valueIds`: the value IDs an instruction kind or terminator mentions. Every string that names
    /// a value of the function is one, but for a String literal's text.
    fn valueIds(self: *Plan, out: *std.ArrayList([]const u8), value: Json) !void {
        switch (value) {
            .string => |text| if (self.defined.contains(text)) try out.append(self.a, text),
            .array => |items| for (items.items) |item| try self.valueIds(out, item),
            .object => |object| {
                const literal = if (object.get("kind")) |kind| kind == .string and std.mem.eql(u8, kind.string, "stringLiteral") else false;
                var fields = object.iterator();
                while (fields.next()) |entry| {
                    const name = entry.key_ptr.*;
                    if (std.mem.eql(u8, name, "result") or std.mem.eql(u8, name, "originNodeId") or std.mem.eql(u8, name, "kind")) continue;
                    if (literal and std.mem.eql(u8, name, "value")) continue;
                    try self.valueIds(out, entry.value_ptr.*);
                }
            },
            else => {},
        }
    }
};

const Use = struct { block: usize, position: usize, origin: ?[]const u8, update: bool, id: []const u8 };

fn successors(a: Allocator, terminator: Json, blocks: []const Json) ![]const usize {
    const kind = jsonString(terminator, "kind");
    var names: std.ArrayList([]const u8) = .empty;
    if (std.mem.eql(u8, kind, "jump")) {
        try names.append(a, jsonString(terminator, "blockId"));
    } else if (std.mem.eql(u8, kind, "branch")) {
        try names.appendSlice(a, &.{ jsonString(terminator, "whenTrue"), jsonString(terminator, "whenFalse") });
    } else if (std.mem.eql(u8, kind, "matchUnion")) {
        for (terminator.object.get("targets").?.array.items) |target| try names.append(a, jsonString(target, "blockId"));
    } else if (std.mem.eql(u8, kind, "matchOption")) {
        try names.appendSlice(a, &.{ jsonString(terminator, "someBlock"), jsonString(terminator, "noneBlock") });
    } else if (std.mem.eql(u8, kind, "matchResult")) {
        try names.appendSlice(a, &.{ jsonString(terminator, "okBlock"), jsonString(terminator, "errBlock") });
    }
    const out = try a.alloc(usize, names.items.len);
    for (names.items, out) |name, *index| index.* = for (blocks, 0..) |block, i| {
        if (std.mem.eql(u8, jsonString(block, "id"), name)) break i;
    } else unreachable;
    return out;
}

/// `copiedUpdates` (`withCopiedUpdates`): written only when the model lowers and an update must copy.
fn copiedUpdates(r: *Request, out: *Writer, wanted: Str) !void {
    const a = r.a;
    const ir = model.executionIrOf(a, r.model) orelse return;
    const program = try std.json.parseFromSliceLeaky(Json, a, ir, .{});
    const semantic_id = model.utf8(wanted);
    const function = for (program.object.get("functions").?.array.items) |item| {
        if (std.mem.eql(u8, jsonString(item, "semanticFunctionId"), semantic_id)) break item;
    } else return;
    const blocks = function.object.get("blocks").?.array.items;
    var plan: Plan = .{ .a = a };
    for (function.object.get("parameters").?.array.items) |parameter| try plan.defined.put(a, jsonString(parameter.object.get("value").?, "id"), {});
    for (blocks) |block| {
        for (block.object.get("parameters").?.array.items) |parameter| try plan.defined.put(a, jsonString(parameter, "id"), {});
        for (block.object.get("instructions").?.array.items) |instruction| try plan.defined.put(a, jsonString(instruction.object.get("result").?, "id"), {});
    }
    const moved = struct {
        fn f(instruction: Json) ?[]const u8 {
            const kind = jsonString(instruction, "kind");
            return if (std.mem.eql(u8, kind, "readParameter") or std.mem.eql(u8, kind, "readPatternBinding")) jsonString(instruction, "sourceValueId") else null;
        }
    }.f;
    // One buffer under several names is one alias class: jump arguments and their block
    // parameters, and parameter and pattern reads and what they read.
    for (blocks) |block| {
        const terminator = block.object.get("terminator").?;
        if (std.mem.eql(u8, jsonString(terminator, "kind"), "jump")) {
            for (terminator.object.get("arguments").?.array.items) |argument| try plan.join(jsonString(argument, "valueId"), jsonString(argument, "targetValueId"));
        }
        for (block.object.get("instructions").?.array.items) |instruction| {
            if (moved(instruction)) |source| try plan.join(jsonString(instruction.object.get("result").?, "id"), source);
        }
    }
    var uses: std.ArrayList(Use) = .empty;
    for (blocks, 0..) |block, block_index| {
        const instructions = block.object.get("instructions").?.array.items;
        for (instructions, 0..) |instruction, index| {
            if (moved(instruction) != null) continue;
            var ids: std.ArrayList([]const u8) = .empty;
            try plan.valueIds(&ids, instruction);
            const kind = jsonString(instruction, "kind");
            const update = std.mem.eql(u8, kind, "bytesSet") or std.mem.eql(u8, kind, "bytesAppend");
            for (ids.items) |id| try uses.append(a, .{ .block = block_index, .position = index, .origin = jsonString(instruction, "originNodeId"), .update = update, .id = id });
        }
        const terminator = block.object.get("terminator").?;
        const kind = jsonString(terminator, "kind");
        if (std.mem.eql(u8, kind, "jump")) continue;
        // Return and branch inputs carry no origin of their own; the block's last instruction, if any, stands in.
        const origin: ?[]const u8 = if (std.mem.eql(u8, kind, "tailCall") or std.mem.eql(u8, kind, "abort")) jsonString(terminator, "originNodeId") else if (instructions.len > 0) jsonString(instructions[instructions.len - 1], "originNodeId") else null;
        var ids: std.ArrayList([]const u8) = .empty;
        try plan.valueIds(&ids, terminator);
        for (ids.items) |id| try uses.append(a, .{ .block = block_index, .position = instructions.len, .origin = origin, .update = false, .id = id });
    }
    var count: usize = 0;
    for (blocks, 0..) |block, block_index| {
        // The blocks reachable from this one, by its successors.
        const reachable = try a.alloc(bool, blocks.len);
        @memset(reachable, false);
        var pending: std.ArrayList(usize) = .empty;
        try pending.appendSlice(a, try successors(a, block.object.get("terminator").?, blocks));
        while (pending.pop()) |next| {
            if (reachable[next]) continue;
            reachable[next] = true;
            try pending.appendSlice(a, try successors(a, blocks[next].object.get("terminator").?, blocks));
        }
        for (block.object.get("instructions").?.array.items, 0..) |instruction, index| {
            const kind = jsonString(instruction, "kind");
            if (!std.mem.eql(u8, kind, "bytesSet") and !std.mem.eql(u8, kind, "bytesAppend")) continue;
            const alias = plan.find(jsonString(instruction, "bytesId"));
            const later = for (uses.items) |use| {
                if (!std.mem.eql(u8, plan.find(use.id), alias)) continue;
                if (use.update and use.block == block_index and use.position == index) continue;
                if ((use.block == block_index and use.position > index) or (use.block != block_index and reachable[use.block])) break use;
            } else null;
            const forcing = later orelse continue;
            try out.writeAll(if (count == 0) ",\"copiedUpdates\":[" else ",");
            count += 1;
            const origin = jsonString(instruction, "originNodeId");
            try out.writeAll("{\"updateNodeId\":");
            try writeId(out, try utf16(a, origin));
            try out.writeAll(",\"forcedByNodeId\":");
            try writeId(out, try utf16(a, forcing.origin orelse origin));
            try out.writeByte('}');
        }
    }
    if (count > 0) try out.writeByte(']');
}

/// `bodyEffectRequirements`.
pub fn writeRequirements(out: *Writer, requirements: model.BodyRequirements) !void {
    try out.writeAll("{\"effectIds\":");
    try writeIds(out, requirements.effects);
    try out.writeAll(",\"evidence\":[");
    for (requirements.evidence, 0..) |item, index| {
        if (index > 0) try out.writeByte(',');
        try out.writeAll("{\"effectId\":");
        try writeId(out, item.effect);
        try out.writeAll(",\"nodeId\":");
        try writeId(out, item.node);
        try out.writeAll(if (item.operation) ",\"source\":{\"kind\":\"capabilityOperation\",\"operationId\":" else ",\"source\":{\"kind\":\"functionContract\",\"functionId\":");
        try writeId(out, item.source);
        try out.writeAll("}}");
    }
    try out.writeAll("],\"unresolvedFunctionIds\":");
    try writeIds(out, requirements.unresolved_functions);
    try out.writeAll(",\"unresolvedOperationIds\":");
    try writeIds(out, requirements.unresolved_operations);
    try out.writeByte('}');
}

/// `aborts`, owned by `owner`.
pub fn writeAborts(out: *Writer, aborts: []const model.Abort, owner: Str) !void {
    try out.writeByte('[');
    for (aborts, 0..) |item, index| {
        if (index > 0) try out.writeByte(',');
        try out.writeAll("{\"ownerFunctionId\":");
        try writeId(out, owner);
        try out.writeAll(",\"nodeId\":");
        try writeId(out, item.node);
        try out.writeAll(",\"errorNodeId\":");
        try writeId(out, item.error_node);
        try out.writeAll(",\"errorType\":");
        if (item.error_type) |t| try wire.typeRef(out, t, &.{}) else try out.writeAll("null");
        try out.writeAll(",\"enclosingControlNodeIds\":");
        try writeIds(out, item.enclosing);
        try out.writeAll(",\"dependentContinuationNodeIds\":");
        try writeIds(out, item.dependents);
        try out.writeByte('}');
    }
    try out.writeByte(']');
}

/// `focusFields`: compact (the function summarized) or, for `full`, with the structured body and the node types.
fn compactFocus(r: *Request, out: *Writer, function: Value, wanted: Str, nodes: []const Value, full: bool) !void {
    const focus = model.focusOf(r.a, r.model, function);
    try out.writeAll("{\"snapshotId\":");
    try @import("root").writeString(out, r.snapshot_id);
    try out.writeAll(",\"function\":");
    if (full) try wire.functionEntity(out, function) else try wire.functionSummary(out, function, nodes.len);
    try out.writeAll(",\"bodyEffectRequirements\":");
    try writeRequirements(out, focus.requirements);
    if (focus.wait_path) |path| {
        try out.writeAll(",\"mayWait\":true,\"waitPath\":");
        try model.writeString(out, path);
    }
    try out.writeAll(",\"aborts\":");
    try writeAborts(out, focus.aborts, wanted);
    if (full) {
        try out.writeAll(",\"nodeTypes\":[");
        for (focus.node_types, 0..) |item, index| {
            if (index > 0) try out.writeByte(',');
            try out.writeAll("{\"nodeId\":");
            try writeId(out, item.id);
            try out.writeAll(",\"type\":");
            if (item.type) |t| try wire.typeRef(out, t, model.arrayItems(function, "typeParameters")) else try out.writeAll("null");
            try out.writeByte('}');
        }
        try out.writeByte(']');
    }
    try out.writeAll(",\"callers\":");
    try writeSites(out, focus.callers);
    try out.writeAll(",\"callees\":");
    try writeSites(out, focus.callees);
    // `getOpenObligations`: the open obligations whose call node the function owns.
    const Obligation = struct { id: Str, item: Value };
    var open: std.ArrayList(Obligation) = .empty;
    for (model.arrayItems(r.model, "obligations")) |obligation| {
        if (!kindIsField(obligation, "status", "open")) continue;
        const call = model.decoded(model.property(obligation, "callNodeId").?);
        for (nodes) |node| if (same(node, call)) {
            try open.append(r.a, .{ .id = model.decoded(model.property(obligation, "id").?), .item = obligation });
            break;
        };
    }
    std.mem.sort(Obligation, open.items, {}, struct {
        fn less(_: void, x: Obligation, y: Obligation) bool {
            return std.mem.order(u16, x.id, y.id) == .lt;
        }
    }.less);
    try out.writeAll(",\"openObligations\":[");
    for (open.items, 0..) |obligation, index| {
        if (index > 0) try out.writeByte(',');
        try out.writeAll("{\"obligationId\":");
        try writeId(out, obligation.id);
        try out.writeAll(",\"callNodeId\":");
        try writeId(out, model.decoded(model.property(obligation.item, "callNodeId").?));
        try out.writeAll(",\"parameterId\":");
        try writeId(out, model.decoded(model.property(obligation.item, "parameterId").?));
        try out.writeByte('}');
    }
    try out.writeByte(']');
}

// ---------------------------------------------------------------- outline

fn isKind(kind: Str, comptime name: []const u8) bool {
    if (kind.len != name.len) return false;
    for (kind, name) |unit, c| if (unit != c) return false;
    return true;
}

const Outline = struct {
    r: *Request,
    names: std.StringHashMapUnmanaged(Str) = .empty,
    nodes: std.StringHashMapUnmanaged(Value) = .empty,
    seen: std.StringHashMapUnmanaged(void) = .empty,
    lines: std.ArrayList([]const u8) = .empty,
    /// The reply's bytes with the lines so far, `outlineTruncated: false` and the budget.
    size: usize,
    truncated: bool = false,

    const max_bytes = 16384;
    const Scope = struct { depth: usize, bindings: []const Value };

    fn add(self: *Outline, item: Value) !void {
        try self.names.put(self.r.a, model.utf8(model.decoded(model.property(item, "id").?)), model.decoded(model.property(item, "displayName").?));
    }

    fn named(self: *Outline, id: Str) Str {
        return self.names.get(model.utf8(id)) orelse id;
    }

    fn idAt(node: Value, name: []const u8) Str {
        return model.decoded(model.property(node, name).?);
    }

    /// A pattern binding's display name, or `fallback` when it has none.
    fn bindingName(binding: Value, fallback: ?Str) ?Str {
        return if (model.property(binding, "displayName")) |name| model.decoded(name) else fallback;
    }

    fn addLine(self: *Outline, line: []const u16) !void {
        if (self.truncated) return;
        var encoded: Writer.Allocating = .init(self.r.a);
        try model.writeString(&encoded.writer, line);
        const grown = self.size + encoded.written().len + @as(usize, if (self.lines.items.len > 0) 1 else 0);
        if (grown > max_bytes) {
            self.truncated = true;
            // `true` is one byte shorter than `false`.
            self.size -= 1;
            return;
        }
        self.size = grown;
        try self.lines.append(self.r.a, encoded.written());
    }

    fn visit(self: *Outline, depth: usize, scopes: []const Scope, id: Str) !void {
        if (self.truncated) return;
        const a = self.r.a;
        var line: std.ArrayList(u16) = .empty;
        try line.appendNTimes(a, ' ', @as(usize, @min(depth, 64)) * 2);
        if ((try self.seen.getOrPut(a, model.utf8(id))).found_existing) {
            for ("ref ") |c| try line.append(a, c);
            try line.appendSlice(a, id);
            return self.addLine(line.items);
        }
        const node = self.nodes.get(model.utf8(id)).?;
        const kind = model.decoded(model.property(node, "kind").?);
        if (isKind(kind, "parameterReference")) {
            for ("parameter") |c| try line.append(a, c);
        } else try line.appendSlice(a, kind);
        try line.append(a, ' ');
        try line.appendSlice(a, id);
        var detail: std.ArrayList(Str) = .empty;
        try self.operands(&detail, node, kind, scopes);
        for (detail.items) |item| {
            try line.append(a, ' ');
            try line.appendSlice(a, item);
        }
        try self.addLine(line.items);
        for (model.operandIdsOf(node)) |child| try self.visit(depth + 1, scopes, child);
        for (model.scopesOf(node)) |scope| {
            var nested: std.ArrayList(Scope) = .empty;
            if (scope.bindings.len > 0) {
                try nested.append(a, .{ .depth = 0, .bindings = scope.bindings });
                for (scopes) |outer| try nested.append(a, .{ .depth = outer.depth + 1, .bindings = outer.bindings });
            } else try nested.appendSlice(a, scopes);
            try self.visit(depth + 1, nested.items, idAt(scope.graph, "resultNodeId"));
        }
    }

    fn text(self: *Outline, ascii: []const u8) !Str {
        const units = try self.r.a.alloc(u16, ascii.len);
        for (ascii, units) |c, *unit| unit.* = c;
        return units;
    }

    /// The operands `WireOutline.focus` names after a node's kind and ID.
    fn operands(self: *Outline, out: *std.ArrayList(Str), node: Value, kind: Str, scopes: []const Scope) !void {
        const a = self.r.a;
        if (isKind(kind, "parameterReference")) {
            try out.append(a, self.named(idAt(node, "parameterId")));
        } else if (isKind(kind, "patternReference")) {
            const wanted = idAt(node, "patternBindingId");
            for (scopes) |scope| for (scope.bindings) |binding| if (std.mem.eql(u16, idAt(binding, "id"), wanted)) {
                try out.append(a, try self.text(try std.fmt.allocPrint(a, "scopeDepth={d}", .{scope.depth})));
                try out.append(a, bindingName(binding, try self.text("value")).?);
                return;
            };
            try out.append(a, wanted);
        } else if (isKind(kind, "stringLiteral")) {
            // The first 64 UTF-16 units, as a JSON string literal.
            const value = model.decoded(model.property(node, "value").?);
            const shown = if (value.len > 64) try std.mem.concat(a, u16, &.{ value[0..64], &.{0x2026} }) else value;
            var literal: Writer.Allocating = .init(a);
            try model.writeString(&literal.writer, shown);
            try out.append(a, try self.text(literal.written()));
        } else if (isKind(kind, "int64Literal")) {
            switch (model.property(node, "value").?) {
                .number => |digits| try out.append(a, try self.text(digits)),
                else => |value| try out.append(a, model.decoded(value)),
            }
        } else if (isKind(kind, "boolLiteral")) {
            try out.append(a, try self.text(if (model.property(node, "value").? == .json_true) "true" else "false"));
        } else if (isKind(kind, "float64Literal")) {
            try out.append(a, idAt(node, "bits"));
        } else if (isKind(kind, "fieldAccess")) {
            try out.append(a, self.named(idAt(node, "fieldId")));
        } else if (isKind(kind, "unionConstruct")) {
            try out.append(a, self.named(idAt(node, "variantId")));
        } else if (isKind(kind, "productConstruct") or isKind(kind, "productUpdate")) {
            try out.append(a, self.named(idAt(node, "typeId")));
        } else if (isKind(kind, "capabilityInvoke")) {
            try out.append(a, self.named(idAt(node, "operationId")));
        } else if (isKind(kind, "bind")) {
            const binding = model.property(model.property(node, "boundBody").?, "valueBinding").?;
            try out.append(a, bindingName(binding, try self.text("value")).?);
        } else if (isKind(kind, "matchOption")) {
            const binding = model.property(model.property(node, "someArm").?, "valueBinding").?;
            if (bindingName(binding, null)) |name| try out.append(a, name);
        } else if (isKind(kind, "matchResult")) {
            if (bindingName(model.property(model.property(node, "okArm").?, "valueBinding").?, null)) |name| try out.append(a, name);
            if (bindingName(model.property(model.property(node, "errArm").?, "errorBinding").?, null)) |name| try out.append(a, name);
        } else if (isKind(kind, "matchUnion")) {
            for (model.arrayItems(node, "arms")) |arm| {
                if (model.property(arm, "variantId")) |variant| {
                    try out.append(a, self.named(model.decoded(variant)));
                } else for (model.arrayItems(arm, "variantIds")) |variant| try out.append(a, self.named(model.decoded(variant)));
            }
        } else for (model.functionIdsOf(node)) |function_id| try out.append(a, self.named(function_id));
    }
};

/// `WireOutline.focus`: the compact focus and a bounded outline, one line per node from the result
/// node through operands and nested bodies, cut before the reply passes 16384 bytes.
fn outline(r: *Request, out: *Writer, compact: []const u8, function: Value, nodes: []const Value) !void {
    const tail_false = "],\"outlineTruncated\":false,\"outlineMaxBytes\":16384";
    var o: Outline = .{ .r = r, .size = compact.len + ",\"outline\":[".len + tail_false.len + "}".len };
    for (model.arrayItems(function, "parameters")) |parameter| try o.add(parameter);
    for (try visible(r, "function")) |item| {
        try o.add(item);
        for (model.arrayItems(item, "parameters")) |parameter| try o.add(parameter);
    }
    for (try visible(r, "type")) |item| {
        try o.add(item);
        for (model.arrayItems(item, "fields")) |field| try o.add(field);
        for (model.arrayItems(item, "variants")) |variant| {
            try o.add(variant);
            for (model.arrayItems(variant, "fields")) |field| try o.add(field);
        }
    }
    for (try visible(r, "capability")) |item| {
        try o.add(item);
        for (model.arrayItems(item, "operations")) |operation| try o.add(operation);
    }
    for (nodes) |node| try o.nodes.put(r.a, model.utf8(Outline.idAt(node, "id")), node);
    try o.visit(0, &.{}, Outline.idAt(model.property(function, "body").?, "resultNodeId"));
    try out.writeAll(compact);
    try out.writeAll(",\"outline\":[");
    for (o.lines.items, 0..) |line, index| {
        if (index > 0) try out.writeByte(',');
        try out.writeAll(line);
    }
    try out.writeAll(if (o.truncated) "],\"outlineTruncated\":true,\"outlineMaxBytes\":16384" else tail_false);
}


fn kindIsField(item: Value, name: []const u8, comptime expected: []const u8) bool {
    const value = model.property(item, name) orelse return false;
    if (value != .string) return false;
    const units = model.decoded(value);
    if (units.len != expected.len) return false;
    for (units, expected) |unit, c| if (unit != c) return false;
    return true;
}

// ---------------------------------------------------------------- viewFunction

/// `viewFunction`: the committed function's Function Focus page, written beside the snapshot.
pub fn viewFunction(r: *Request, out: *Writer) Fail!void {
    if (try fieldProblem(r.a, r.line, &.{}, r.root, "viewFunction request", &.{ "id", "op", "function", "functionId", "output" }, &.{ "id", "op" })) |message| return r.fail("{s}", .{message});
    const by_name, const by_id = .{ r.root.get("function") != null, r.root.get("functionId") != null };
    if (by_name == by_id) return r.fail("viewFunction requires exactly one of function and functionId.", .{});
    const wanted = if (by_name) model.decoded(model.property(try resolve(r, "function", r.root, "function", &.{"function"}), "id").?) else try utf16(r.a, try exactId(r, "functionId"));
    const output = if (r.root.get("output") != null) try requiredString(r, r.root, "output") else null;
    const function = for (model.arrayItems(r.model, "functions")) |item| {
        if (same(item, wanted)) break item;
    } else {
        r.rejection = .{ .code = "SemanticQueryFailure", .message = try std.fmt.allocPrint(r.a, "No function has the ID '{s}'.", .{model.utf8(wanted)}) };
        return error.Rejected;
    };
    const id_text = model.utf8(wanted);
    // `ViewerArtifact.focusFileName`: named by the function's ID, its UTF-8 bytes in lower-case hex.
    const name = try std.fmt.allocPrint(r.a, "nyrwvy-focus-function-{x}.html", .{id_text});
    const content = try viewer.page(r.a, try viewer.focusData(r.a, r.model, r.snapshot_id, function));
    switch (try view_file.write(r.a, r.snapshot_path, output, ".html", name, viewer.header(), content)) {
        .refused => |message| {
            r.rejection = .{ .code = "ViewWriteFailed", .message = message };
            return error.Rejected;
        },
        .internal => {
            r.rejection = .{ .code = "InternalError", .message = "Unexpected internal failure; this request was not applied and the session is unchanged." };
            return error.Rejected;
        },
        .written => |path| {
            try out.writeAll("{\"functionId\":");
            try writeId(out, wanted);
            try out.writeAll(",\"displayName\":");
            try model.writeString(out, model.decoded(model.property(function, "displayName").?));
            try out.writeAll(",\"kind\":\"functionFocus\",\"mediaType\":\"text/html; charset=utf-8\",\"path\":");
            try @import("root").writeString(out, path);
            try out.writeByte('}');
        },
    }
}

// ---------------------------------------------------------------- executionReadiness

/// `executionReadiness`: whether the committed snapshot projects and lowers to an execution program.
pub fn executionReadiness(r: *Request, out: *Writer) Fail!void {
    if (try fieldProblem(r.a, r.line, &.{}, r.root, "executionReadiness request", &.{ "id", "op" }, &.{ "id", "op" })) |message| return r.fail("{s}", .{message});
    switch (model.executionReadinessOf(r.a, r.model)) {
        .ready => |count| {
            try out.writeAll("{\"kind\":\"ready\",\"sourceSnapshotId\":");
            try @import("root").writeString(out, r.snapshot_id);
            try out.print(",\"functionCount\":{d}}}", .{count});
        },
        .projection => |case| {
            r.rejection = .{ .code = "SemanticProjectionFailure", .message = case };
            return error.Rejected;
        },
        .lowering => |case| {
            r.rejection = .{ .code = "ExecutionLoweringFailure", .message = case };
            return error.Rejected;
        },
    }
}

// ---------------------------------------------------------------- simulateCreateFunction

const recipe = @import("recipe.zig");

/// `simulateCreateFunction`: the request read as `Protocol.fs` reads it, field by field in the
/// order F# evaluates them, with each rejection.
// shortcut: a request that decodes is not simulated yet (inference, materialization and the review come in 3c-3 and 3c-4).
pub fn simulateCreateFunction(r: *Request, out: *Writer) Fail!void {
    _ = out;
    if (try fieldProblem(r.a, r.line, &.{}, r.root, "simulateCreateFunction request", &.{ "id", "op", "displayName", "exported", "mustNotWait", "typeParameters", "parameters", "resultType", "requiredEffects", "body", "structuredReview", "detail", "expectedSnapshotId" }, &.{ "id", "op", "displayName", "parameters", "resultType", "requiredEffects", "body" })) |message| return r.fail("{s}", .{message});
    var d: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line) };
    var root: std.json.Value = .{ .object = r.root };
    for (try d.array(root, "parameters"), 0..) |item, i| {
        const path = try std.fmt.allocPrint(r.a, "/parameters/{d}", .{i});
        try d.ensureFields(path, item, "function parameter", &.{ "displayName", "type" }, &.{ "displayName", "type" });
        _ = try d.requiredString(item, "displayName");
        try d.typeRef(try std.fmt.allocPrint(r.a, "{s}/type", .{path}), try d.property(item, "type"));
    }
    for (try d.array(root, "requiredEffects")) |item| {
        if (item != .string) return r.fail("requiredEffects entries must be stable-ID strings.", .{});
        _ = try d.stableId("requiredEffects", try readable(r, item.string));
    }
    if (r.root.get("expectedSnapshotId") != null) _ = try d.stableId("expectedSnapshotId", try d.requiredString(root, "expectedSnapshotId"));
    _ = try d.requiredString(root, "displayName");
    try d.optionalBool(root, "exported");
    try d.optionalBool(root, "mustNotWait");
    if (r.root.get("typeParameters") != null) {
        for (try d.array(root, "typeParameters")) |item| {
            if (item != .string) return r.fail("typeParameters must list type parameter names as strings.", .{});
            if (isBlank(try utf16(r.a, try readable(r, item.string)))) return r.fail("Type parameter names must not be blank.", .{});
            _ = try d.stableId("typeParameters", item.string);
        }
    }
    try d.typeRef("/resultType", try d.property(root, "resultType"));
    const body = root.object.getPtr("body").?;
    try recipe.resolveNames(&d, body, try recipe.rawProperty(r.a, r.line, "body"));
    try d.recipe("/body", body.*);
    r.rejection = .{ .code = "UnsupportedOperation", .message = "The Zig host reads simulateCreateFunction requests but does not simulate them yet." };
    return error.Rejected;
}

/// `simulateCreateFunctionByName` as `functionCreationByName` reads it; a request that decodes is not simulated yet.
pub fn simulateCreateFunctionByName(r: *Request, out: *Writer) Fail!void {
    _ = out;
    if (try fieldProblem(r.a, r.line, &.{}, r.root, "simulateCreateFunctionByName request", &.{ "id", "op", "displayName", "exported", "mustNotWait", "typeParameters", "parameters", "resultType", "requiredEffects", "body", "bodyFile", "structuredReview", "detail", "expectedSnapshotId" }, &.{ "id", "op", "displayName", "parameters", "resultType", "requiredEffects" })) |message| return r.fail("{s}", .{message});
    var d: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line) };
    const root: std.json.Value = .{ .object = r.root };
    if (r.root.get("expectedSnapshotId") != null) _ = try d.stableId("expectedSnapshotId", try d.requiredString(root, "expectedSnapshotId"));
    const display_name = try d.requiredString(root, "displayName");
    for (try visible(r, "function")) |item| if (std.mem.eql(u8, nameOf(item), display_name))
        return r.fail("Function display name '{s}' already exists. Choose a unique name or use the exact-ID authoring surface.", .{display_name});
    d.owner = .{ .name = display_name };
    var parameters: std.ArrayList([]const u8) = .empty;
    for (try d.array(root, "parameters"), 0..) |item, i| {
        const path = try std.fmt.allocPrint(r.a, "/parameters/{d}", .{i});
        try d.ensureFields(path, item, "function parameter", &.{ "displayName", "type" }, &.{ "displayName", "type" });
        try parameters.append(r.a, try d.requiredString(item, "displayName"));
        try d.anyType(try std.fmt.allocPrint(r.a, "{s}/type", .{path}), try d.property(item, "type"));
    }
    d.owner.?.parameters = parameters.items;
    try d.optionalBool(root, "exported");
    try d.optionalBool(root, "mustNotWait");
    if (r.root.get("typeParameters") != null) {
        for (try d.array(root, "typeParameters")) |item| {
            if (item != .string) return r.fail("typeParameters must list type parameter names as strings.", .{});
            if (isBlank(try utf16(r.a, try readable(r, item.string)))) return r.fail("Type parameter names must not be blank.", .{});
            _ = try d.stableId("typeParameters", item.string);
        }
    }
    try d.anyType("/resultType", try d.property(root, "resultType"));
    for (try d.array(root, "requiredEffects"), 0..) |item, i| _ = try d.selector(try std.fmt.allocPrint(r.a, "/requiredEffects/{d}", .{i}), item, "effect");
    // `recipeInput`: exactly one of `body` and `bodyFile`.
    const inline_body, const file = .{ r.root.getPtr("body"), r.root.get("bodyFile") };
    if (inline_body == null and file != null) {
        if (file.? != .string or isBlank(try utf16(r.a, try readable(r, file.?.string)))) return r.fail("bodyFile must be a file path.", .{});
        // shortcut: bodyFile is not read yet (`confinedJsonFile`); port it with the simulation (3c-4).
        r.rejection = .{ .code = "UnsupportedOperation", .message = "The Zig host does not read bodyFile yet." };
        return error.Rejected;
    }
    if (inline_body == null or file != null) return r.fail("Pass exactly one of body and bodyFile.", .{});
    try recipe.resolveNames(&d, inline_body.?, try recipe.rawProperty(r.a, r.line, "body"));
    try d.recipe("/body", inline_body.?.*);
    r.rejection = .{ .code = "UnsupportedOperation", .message = "The Zig host reads simulateCreateFunctionByName requests but does not simulate them yet." };
    return error.Rejected;
}
