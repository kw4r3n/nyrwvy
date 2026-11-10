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
pub const Rejection = struct { code: []const u8, message: []const u8, details: []const u8 = "", fields: []const u8 = "" };
pub const Answer = union(enum) { result: []const u8, rejected: Rejection };

const Fail = anyerror;

/// Request-arena scratch publication; mixed batches never intern or register their steps.
pub const Capture = struct {
    candidate: ?model.Model = null,
    full_review: []const u8 = "",
    structured_review: []const u8 = "",
    compact_review: []const u8 = "",
    summary_operations: Value = .json_null,
};

pub const Diagnostic = struct { code: []const u8 = "", message: []const u8 = "" };

/// The request being answered, and where a rejection's text goes.
pub const Request = struct {
    a: Allocator,
    line: []const u8,
    root: std.json.ObjectMap,
    /// The selected dependency's provenance.
    provenance: ?wire.Provenance,
    generation: ?*const model.session_model.Generation = null,
    interner: ?*model.session_model.Interner = null,
    interner_checkpoint: ?model.session_model.Interner.Checkpoint = null,
    snapshot_id: []const u8 = "",
    snapshot_fingerprint: []const u8 = "",
    /// The snapshot file, absolute, beside which views are written.
    snapshot_path: []const u8 = "",
    session_invalidated: bool = false,
    rejection: Rejection = .{ .code = "", .message = "" },
    /// The diagnostic behind a `SimulationRejected` rejection, for a message appended to later.
    diagnostic: Diagnostic = .{},
    capture: ?*Capture = null,

    pub fn typedModel(self: *const Request) model.Model {
        return model.typedRoot(self.a, self.interner.?, self.generation.?);
    }

    pub fn ensureUsable(self: *Request) Fail!void {
        if (!self.session_invalidated) return;
        self.rejection = .{ .code = "SessionInvalidated", .message = "The session was invalidated by external modification." };
        return error.Rejected;
    }

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
pub fn ordinal(_: void, x: []const u8, y: []const u8) bool {
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
fn upperPoints(a: Allocator, units: []const u16) ![]u21 {
    const p = try points(a, units);
    for (p) |*c| c.* = mapPoint(c.*, &case_runs.upper);
    return p;
}
/// `n` is the needle already through `upperPoints`, so a search upper-cases it once.
fn containsIgnoringCase(a: Allocator, haystack: []const u16, n: []const u21) !bool {
    if (haystack.len < n.len) return false; // a haystack has at most one point per unit
    const h = try upperPoints(a, haystack);
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

pub fn isBlank(units: []const u16) bool {
    for (units) |unit| if (!model.isWhiteSpace(unit)) return false;
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
        const threshold = @max(2, wanted.len / 3);
        const contained = wanted.len >= 3 and (std.mem.indexOf(u16, lower, wanted) != null or std.mem.indexOf(u16, wanted, lower) != null);
        // The distance is at least the length difference, so a far-off length cannot qualify.
        if (!contained and (if (lower.len > wanted.len) lower.len - wanted.len else wanted.len - lower.len) > threshold) continue;
        const distance = try editDistance(a, wanted, lower);
        if (distance <= threshold or contained)
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

pub fn nameOf(item: anytype) []const u8 {
    if (@TypeOf(item) == model.Model) return model.utf8(item.displayName());
    return model.utf8(model.decoded(model.property(item, "displayName").?));
}

pub fn idOf(item: anytype) []const u8 {
    if (@TypeOf(item) == model.Model) return model.utf8(item.id());
    return model.utf8(model.decoded(model.property(item, "id").?));
}

fn contains(keys: []const []const u8, key: []const u8) bool {
    for (keys) |k| if (std.mem.eql(u8, k, key)) return true;
    return false;
}

fn hidden(r: *const Request) []const []const u8 {
    return if (r.provenance) |p| p.hidden else &.{};
}

/// The members of the selected dependency that provide `kind:id`; none for a local declaration.
pub fn sources(r: *const Request, kind: []const u8, id: []const u8) ![]const wire.Provenance.Member {
    const p = r.provenance orelse return &.{};
    const key = try std.fmt.allocPrint(r.a, "{s}:{s}", .{ kind, id });
    var out: std.ArrayList(wire.Provenance.Member) = .empty;
    for (p.members) |member| if (contains(member.keys, key)) try out.append(r.a, member);
    return out.items;
}

pub fn ownership(r: *const Request, kind: []const u8, id: []const u8) ![]const u8 {
    return if ((try sources(r, kind, id)).len == 0) "local" else "imported";
}

/// `JsonSerializer.Serialize` of a string.
pub fn stringOf(a: Allocator, text: []const u8) ![]const u8 {
    var out: Writer.Allocating = .init(a);
    try model.writeString(&out.writer, try utf16(a, text));
    return out.written();
}

fn typedRoot(r: *const Request) model.Model {
    return model.typedRoot(r.a, r.interner.?, r.generation.?);
}
fn typedNameOf(item: model.Model) []const u8 {
    return model.utf8(item.displayName());
}
fn typedIdOf(item: model.Model) []const u8 {
    return model.utf8(item.id());
}
pub fn typedVisible(r: *const Request, kind: []const u8) ![]const model.Model {
    const group = if (std.mem.eql(u8, kind, "function")) "functions" else if (std.mem.eql(u8, kind, "type")) "types" else if (std.mem.eql(u8, kind, "effect")) "effects" else "capabilities";
    var out: std.ArrayList(model.Model) = .empty;
    for (typedRoot(r).declarations(group)) |item| {
        if (!contains(hidden(r), try std.fmt.allocPrint(r.a, "{s}:{s}", .{ kind, typedIdOf(item) }))) try out.append(r.a, item);
    }
    std.mem.sort(model.Model, out.items, {}, struct {
        fn less(_: void, x: model.Model, y: model.Model) bool {
            return std.mem.order(u16, x.id(), y.id()) == .lt;
        }
    }.less);
    return out.items;
}
pub fn typedExactOne(r: *Request, label: []const u8, wanted: []const u8, items: []const model.Model) Fail!model.Model {
    var found: ?model.Model = null;
    var count: usize = 0;
    var names: std.ArrayList([]const u8) = .empty;
    for (items) |item| {
        try names.append(r.a, typedNameOf(item));
        if (std.mem.eql(u8, typedNameOf(item), wanted)) {
            found = item;
            count += 1;
        }
    }
    if (count == 1) return found.?;
    if (count == 0) return r.fail("Unknown {s} '{s}'.{s}", .{ label, wanted, try candidateHint(r.a, wanted, names.items) });
    return r.fail("Ambiguous {s} '{s}'.", .{ label, wanted });
}

pub fn typedResolve(r: *Request, kind: []const u8, object: std.json.ObjectMap, key: []const u8, path: []const []const u8) Fail!model.Model {
    const items = try typedVisible(r, kind);
    const selector = object.get(key) orelse return r.fail("Missing required property '{s}'.", .{key});
    if (selector == .string) return typedExactOne(r, kind, try readable(r, selector.string), items);
    if (selector != .object) return r.fail("Expected a JSON object.", .{});
    const fields = &.{ "id", "name", "dependencySnapshotId", "ownership" };
    try lookable(r, selector.object);
    if (try fieldProblem(r.a, r.line, path, selector.object, try std.fmt.allocPrint(r.a, "{s} selector", .{kind}), fields, &.{})) |message| return r.fail("{s}", .{message});
    return typedGuarded(r, kind, items, selector.object);
}

/// The rest of `WireSymbolic.resolveSelector` for an object selector whose fields were checked.
pub fn typedGuarded(r: *Request, kind: []const u8, items: []const model.Model, object: std.json.ObjectMap) Fail!model.Model {
    const fields = &.{ "id", "name", "dependencySnapshotId", "ownership" };
    var guards: [4]?[]const u8 = .{ null, null, null, null };
    inline for (fields, 0..) |field, index| {
        if (object.get(field) != null) guards[index] = try requiredString(r, object, field);
    }
    const id, const wanted_name, const dependency, const ownership_guard = guards;
    if (id == null and wanted_name == null) return r.fail("A declaration selector needs id or name.", .{});
    if (ownership_guard) |owned| if (!std.mem.eql(u8, owned, "local") and !std.mem.eql(u8, owned, "imported")) return r.fail("Selector ownership must be local or imported.", .{});
    var candidates: std.ArrayList(model.Model) = .empty;
    for (items) |item| {
        const provided = try sources(r, kind, typedIdOf(item));
        if (id) |wanted| if (!std.mem.eql(u8, typedIdOf(item), wanted)) continue;
        if (wanted_name) |wanted| if (!std.mem.eql(u8, typedNameOf(item), wanted)) continue;
        if (dependency) |pin| {
            for (provided) |member| {
                if (std.mem.eql(u8, member.id, pin)) break;
            } else continue;
        }
        if (ownership_guard) |owned| if ((provided.len == 0) != std.mem.eql(u8, owned, "local")) continue;
        try candidates.append(r.a, item);
    }
    if (candidates.items.len == 1) return candidates.items[0];
    if (candidates.items.len > 1) return r.fail("Ambiguous {s} selector; specify an exact id or a providing dependencySnapshotId.", .{kind});
    // Advice never participates in resolution.
    var related: std.ArrayList(model.Model) = .empty;
    for (items) |item| {
        if (id) |wanted| {
            if (std.mem.eql(u8, typedIdOf(item), wanted)) try related.append(r.a, item);
        } else if (std.mem.eql(u8, typedNameOf(item), wanted_name.?)) try related.append(r.a, item);
    }
    std.mem.sort(model.Model, related.items, {}, struct {
        fn less(_: void, x: model.Model, y: model.Model) bool {
            return std.mem.order(u16, x.id(), y.id()) == .lt;
        }
    }.less);
    var advice: std.ArrayList([]const u8) = .empty;
    for (related.items[0..@min(5, related.items.len)]) |item| {
        const provided = try sources(r, kind, typedIdOf(item));
        const owner = if (provided.len == 0) "local" else "imported";
        var pins: std.ArrayList([]const u8) = .empty;
        for (provided) |member| try pins.append(r.a, member.id);
        std.mem.sort([]const u8, pins.items, {}, ordinal);
        var mismatched: std.ArrayList([]const u8) = .empty;
        if (wanted_name) |wanted| if (!std.mem.eql(u8, wanted, typedNameOf(item))) try mismatched.append(r.a, "name");
        if (dependency) |pin| if (!contains(pins.items, pin)) try mismatched.append(r.a, "dependencySnapshotId");
        if (ownership_guard) |owned| if (!std.mem.eql(u8, owned, owner)) try mismatched.append(r.a, "ownership");
        const omitted_pins = if (pins.items.len > 5) try std.fmt.allocPrint(r.a, " (+{d} more)", .{pins.items.len - 5}) else "";
        try advice.append(r.a, try std.fmt.allocPrint(r.a, "id={s}, name={s}, ownership={s}, providing pins=[{s}]{s}, mismatched guards=[{s}]", .{
            typedIdOf(item),                                                     try stringOf(r.a, typedNameOf(item)), owner,
            try std.mem.join(r.a, ", ", pins.items[0..@min(5, pins.items.len)]), omitted_pins,                         try std.mem.join(r.a, ", ", mismatched.items),
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
    const needle_points = try upperPoints(r.a, needle);
    var limit: i64 = 50;
    if (r.root.get("limit")) |value| {
        limit = switch (value) {
            .integer => |number| if (number >= std.math.minInt(i32) and number <= std.math.maxInt(i32)) number else return r.fail("limit must be a JSON Int32.", .{}),
            else => return r.fail("limit must be a JSON Int32.", .{}),
        };
    }
    if (limit < 1 or limit > 200) return r.fail("limit must be 1..200.", .{});
    var matches: std.ArrayList([]const u8) = .empty;
    _ = r.generation orelse return error.MissingTypedGeneration;
    const i = r.interner orelse return error.MissingTypedGeneration;
    const named = struct {
        fn f(a: Allocator, interner: *const model.session_model.Interner, kind: []const u8, key: []const u8, domain: model.session_model.Domain, id: u32, name: u32) ![]const u8 {
            var entry: Writer.Allocating = .init(a);
            try entry.writer.print("{{\"kind\":\"{s}\",\"name\":", .{kind});
            try model.writeString(&entry.writer, interner.stringText(name));
            try entry.writer.print(",\"{s}\":", .{key});
            try model.writeString(&entry.writer, interner.idText(domain, id));
            try entry.writer.writeByte('}');
            return entry.written();
        }
    }.f;
    inline for (.{ .{ "function", "functionId", .function }, .{ "type", "typeId", .type }, .{ "effect", "effectId", .effect } }) |group| {
        for (try typedVisible(r, group[0])) |item| {
            if (try containsIgnoringCase(r.a, item.displayName(), needle_points)) try matches.append(r.a, try named(r.a, i, group[0], group[1], group[2], switch (item.data) {
                .function => |v| v.id,
                .type_declaration => |v| v.id,
                .effect => |v| v.id,
                else => unreachable,
            }, switch (item.data) {
                .function => |v| v.name,
                .type_declaration => |v| v.name,
                .effect => |v| v.name,
                else => unreachable,
            }));
        }
    }
    for (try typedVisible(r, "capability")) |record| {
        const item = record.data.capability;
        if (try containsIgnoringCase(r.a, i.stringText(item.name), needle_points)) try matches.append(r.a, try named(r.a, i, "capability", "capabilityId", .capability, item.id, item.name));
        const operations = try r.a.dupe(model.session_model.Operation, item.operations);
        std.mem.sort(model.session_model.Operation, operations, i, struct {
            fn less(interner: *const model.session_model.Interner, x: model.session_model.Operation, y: model.session_model.Operation) bool {
                return interner.less(.operation, x.id, y.id);
            }
        }.less);
        for (operations) |operation| if (try containsIgnoringCase(r.a, i.stringText(operation.name), needle_points)) {
            var entry: Writer.Allocating = .init(r.a);
            try entry.writer.writeAll("{\"kind\":\"capabilityOperation\",\"capability\":");
            try model.writeString(&entry.writer, i.stringText(item.name));
            try entry.writer.writeAll(",\"name\":");
            try model.writeString(&entry.writer, i.stringText(operation.name));
            try entry.writer.writeAll(",\"operationId\":");
            try model.writeString(&entry.writer, i.idText(.operation, operation.id));
            try entry.writer.writeByte('}');
            try matches.append(r.a, entry.written());
        };
    }
    for (try typedVisible(r, "function")) |record| {
        const function = record.data.function;
        for (try function.body.depthFirst(r.a)) |node| {
            const handle = node.stringLiteral() orelse continue;
            const value = i.stringText(handle);
            if (!try containsIgnoringCase(r.a, value, needle_points)) continue;
            var entry: Writer.Allocating = .init(r.a);
            try entry.writer.writeAll("{\"kind\":\"stringLiteral\",\"function\":");
            try model.writeString(&entry.writer, i.stringText(function.name));
            try entry.writer.writeAll(",\"functionId\":");
            try model.writeString(&entry.writer, i.idText(.function, function.id));
            try entry.writer.writeAll(",\"nodeId\":");
            try model.writeString(&entry.writer, i.idText(.node, node.id()));
            try entry.writer.writeAll(",\"value\":");
            try model.writeString(&entry.writer, if (value.len <= 120) value else try std.mem.concat(r.a, u16, &.{ value[0..120], &.{0x2026} }));
            try entry.writer.writeByte('}');
            try matches.append(r.a, entry.written());
        }
    }
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
        return .{ .kind = kind, .id = typedIdOf(try typedResolve(r, kind, r.root, kind, &.{kind})) };
    if (is(kind, "capabilityOperation")) {
        const owner = try typedResolve(r, "capability", r.root, "capability", &.{"capability"});
        const label = try std.fmt.allocPrint(r.a, "operation on {s}", .{typedNameOf(owner)});
        return .{ .kind = kind, .id = typedIdOf(try typedExactOne(r, label, try requiredString(r, r.root, "operation"), owner.operations())) };
    }
    // F# reads each display name before the member lookup that uses it.
    const owner = try typedResolve(r, "type", r.root, "type", &.{"type"});
    const union_type = !owner.data.type_declaration.product;
    if (is(kind, "variant") or has(r.root, "variant")) {
        const variant_name = try requiredString(r, r.root, "variant");
        if (!union_type) return r.fail("Type '{s}' is not a union type.", .{typedNameOf(owner)});
        const variant = try typedExactOne(r, try std.fmt.allocPrint(r.a, "variant on {s}", .{typedNameOf(owner)}), variant_name, owner.variants());
        if (is(kind, "variant")) return .{ .kind = kind, .id = typedIdOf(variant) };
        return .{ .kind = kind, .id = typedIdOf(try typedExactOne(r, try std.fmt.allocPrint(r.a, "field on variant {s}", .{typedNameOf(variant)}), try requiredString(r, r.root, "field"), variant.fields())) };
    }
    const field_name = try requiredString(r, r.root, "field");
    if (union_type) return r.fail("Type '{s}' is not a product type.", .{typedNameOf(owner)});
    return .{ .kind = kind, .id = typedIdOf(try typedExactOne(r, try std.fmt.allocPrint(r.a, "field on {s}", .{typedNameOf(owner)}), field_name, owner.fields())) };
}

pub fn writeId(out: *Writer, units: Str) !void {
    try model.writeString(out, units);
}

/// `"displayName"` of a visible `kind` declaration.
fn typedVisibleNamed(r: *const Request, kind: []const u8, id: Str) !?model.Model {
    for (try typedVisible(r, kind)) |item| if (std.mem.eql(u16, item.id(), id)) return item;
    return null;
}

fn typedWriteNamed(out: *Writer, field: []const u8, item: model.Model) !void {
    try out.print(",\"{s}\":", .{field});
    try model.writeString(out, item.displayName());
}

fn typedSame(item: model.Model, id: Str) bool {
    return std.mem.eql(u16, item.id(), id);
}

/// `WireEncode.references`.
pub fn references(r: *Request, out: *Writer) Fail!void {
    const subject = try subjectOf(r);
    const id = try utf16(r.a, subject.id);
    const found = model.typedReferencesTo(r.a, typedRoot(r), subject.kind, id) orelse {
        r.rejection = .{ .code = "SemanticQueryFailure", .message = try std.fmt.allocPrint(r.a, "No {s} has the ID '{s}'.", .{ subject.kind, subject.id }) };
        return error.Rejected;
    };
    try out.print("{{\"subject\":{{\"kind\":\"{s}\",\"id\":", .{subject.kind});
    try writeId(out, id);
    const kind = subject.kind;
    if (std.mem.eql(u8, kind, "variant") or std.mem.eql(u8, kind, "field")) {
        for (try typedVisible(r, "type")) |item| {
            for (item.fields()) |field| if (std.mem.eql(u8, kind, "field") and typedSame(field, id)) {
                try typedWriteNamed(out, "displayName", field);
                try out.writeAll(",\"ownerTypeId\":");
                try writeId(out, item.id());
                try typedWriteNamed(out, "ownerDisplayName", item);
                break;
            };
            for (item.variants()) |variant| {
                if (std.mem.eql(u8, kind, "variant") and typedSame(variant, id)) {
                    try typedWriteNamed(out, "displayName", variant);
                    try out.writeAll(",\"ownerTypeId\":");
                    try writeId(out, item.id());
                    try typedWriteNamed(out, "ownerDisplayName", item);
                }
                for (variant.fields()) |field| if (std.mem.eql(u8, kind, "field") and typedSame(field, id)) {
                    try typedWriteNamed(out, "displayName", field);
                    try out.writeAll(",\"ownerTypeId\":");
                    try writeId(out, item.id());
                    try typedWriteNamed(out, "ownerDisplayName", item);
                    try out.writeAll(",\"ownerVariantId\":");
                    try writeId(out, variant.id());
                    try typedWriteNamed(out, "ownerVariantDisplayName", variant);
                };
            }
        }
    } else if (std.mem.eql(u8, kind, "capabilityOperation")) {
        for (try typedVisible(r, "capability")) |item| for (item.operations()) |operation| if (typedSame(operation, id)) {
            try typedWriteNamed(out, "displayName", operation);
            try out.writeAll(",\"ownerCapabilityId\":");
            try writeId(out, item.id());
            try typedWriteNamed(out, "ownerDisplayName", item);
        };
    } else if (try typedVisibleNamed(r, kind, id)) |item| try typedWriteNamed(out, "displayName", item);
    try out.print("}},\"count\":{d},\"references\":[", .{found.len});
    for (found, 0..) |reference, index| {
        if (index > 0) try out.writeByte(',');
        const owner_kind = @tagName(reference.owner);
        try out.print("{{\"ownerKind\":\"{s}\",\"ownerId\":", .{owner_kind});
        try writeId(out, reference.owner_id);
        if (try typedVisibleNamed(r, owner_kind, reference.owner_id)) |item| try typedWriteNamed(out, "ownerDisplayName", item);
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
const KeyContext = struct {
    pub fn hash(_: KeyContext, key: Key) u64 {
        var hasher = std.hash.Wyhash.init(0);
        hasher.update(key.kind);
        hasher.update(std.mem.sliceAsBytes(key.id));
        return hasher.final();
    }
    pub fn eql(_: KeyContext, x: Key, y: Key) bool {
        return keyEql(x, y);
    }
};
const KeySet = std.HashMapUnmanaged(Key, void, KeyContext, 80);

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
    const functions = try typedVisible(r, "function");
    const types = try typedVisible(r, "type");
    const capabilities = try typedVisible(r, "capability");
    const Subject_ = struct { key: Key, kind: []const u8 };
    var subjects: std.ArrayList(Subject_) = .empty;
    const decodedId = struct {
        fn f(item: model.Model) Str {
            return item.id();
        }
    }.f;
    for (functions) |item| try subjects.append(r.a, .{ .key = .{ .kind = "function", .id = decodedId(item) }, .kind = "function" });
    for (types) |item| try subjects.append(r.a, .{ .key = .{ .kind = "type", .id = decodedId(item) }, .kind = "type" });
    for (capabilities) |item| for (item.operations()) |operation|
        try subjects.append(r.a, .{ .key = .{ .kind = "operation", .id = decodedId(operation) }, .kind = "capabilityOperation" });
    for (capabilities) |item| try subjects.append(r.a, .{ .key = .{ .kind = "capability", .id = decodedId(item) }, .kind = "capability" });
    var subject_keys: KeySet = .empty;
    for (subjects.items) |subject| try subject_keys.put(r.a, subject.key, {});
    // Each owner's references to subjects (`typedReferencesTo` per subject, in one walk).
    var edges: std.HashMapUnmanaged(Key, std.ArrayList(Key), KeyContext, 80) = .empty;
    for (model.typedReferenceEdges(r.a, typedRoot(r))) |edge| {
        const target: Key = .{ .kind = if (std.mem.eql(u8, edge.kind, "capabilityOperation")) "operation" else edge.kind, .id = edge.id };
        if (!subject_keys.contains(target)) continue;
        const reference = edge.reference;
        const owner: Key = switch (reference.owner) {
            .function => .{ .kind = "function", .id = reference.owner_id },
            .type => .{ .kind = "type", .id = reference.owner_id },
            .capability => switch (reference.site.tag) {
                .operation_parameter_type, .operation_result_type, .operation_failure_type, .operation_effect => .{ .kind = "operation", .id = reference.site.a.? },
                else => .{ .kind = "capability", .id = reference.owner_id },
            },
        };
        const targets = try edges.getOrPut(r.a, owner);
        if (!targets.found_existing) targets.value_ptr.* = .empty;
        try targets.value_ptr.append(r.a, target);
    }
    var roots: std.ArrayList(Key) = .empty;
    if (root_names) |names| for (names) |wanted| {
        var match: ?model.Model = null;
        var count: usize = 0;
        for (functions) |item| if (std.mem.eql(u8, typedNameOf(item), wanted)) {
            match = item;
            count += 1;
        };
        if (count == 0) return r.fail("Unknown function '{s}'.", .{wanted});
        if (count > 1) return r.fail("Ambiguous function '{s}'.", .{wanted});
        try roots.append(r.a, .{ .kind = "function", .id = decodedId(match.?) });
    };
    // The keys that stay: unreferenced by anything but themselves, or unreachable from the roots.
    var marked: KeySet = .empty;
    if (root_names == null) {
        var owners = edges.iterator();
        while (owners.next()) |entry| for (entry.value_ptr.items) |target| if (!keyEql(entry.key_ptr.*, target)) try marked.put(r.a, target, {});
    } else {
        var frontier = roots;
        while (frontier.pop()) |item| {
            if ((try marked.getOrPut(r.a, item)).found_existing) continue;
            if (edges.get(item)) |targets| try frontier.appendSlice(r.a, targets.items);
        }
    }
    const keep = struct {
        fn f(m: KeySet, key: Key) bool {
            return !m.contains(key);
        }
    }.f;
    const Row = struct { sort: []const Str, item: model.Model, owner: ?model.Model = null };
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
        fn f(req: *const Request, kind: []const u8, item: model.Model) !bool {
            return (try sources(req, kind, typedIdOf(item))).len == 0;
        }
    }.f;
    const displayName = struct {
        fn f(item: model.Model) Str {
            return item.displayName();
        }
    }.f;
    var unused_functions: std.ArrayList(Row) = .empty;
    for (functions) |item| if (try local(r, "function", item) and keep(marked, .{ .kind = "function", .id = decodedId(item) }))
        try unused_functions.append(r.a, .{ .sort = try r.a.dupe(Str, &.{ displayName(item), decodedId(item) }), .item = item });
    var unused_operations: std.ArrayList(Row) = .empty;
    for (capabilities) |capability| if (try local(r, "capability", capability)) for (capability.operations()) |operation| {
        if (keep(marked, .{ .kind = "operation", .id = decodedId(operation) }))
            try unused_operations.append(r.a, .{ .sort = try r.a.dupe(Str, &.{ displayName(operation), displayName(capability), decodedId(operation) }), .item = operation, .owner = capability });
    };
    var unused_types: std.ArrayList(Row) = .empty;
    for (types) |item| if (try local(r, "type", item) and keep(marked, .{ .kind = "type", .id = decodedId(item) }))
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
                try typedWriteNamed(out, "capability", capability);
                try out.writeByte(',');
            }
            try out.writeAll("\"id\":");
            try writeId(out, decodedId(row.item));
            try typedWriteNamed(out, "name", row.item);
            try out.writeByte('}');
        }
        try out.writeByte(']');
    }
    try out.writeByte('}');
}

// ---------------------------------------------------------------- bindingScopes

pub const TypedFrame = struct { owner: model.Model, role: []const u8, variants: []const model.Model = &.{}, bindings: []const TypedBinding };
pub const TypedBinding = struct { slot: []const u8, field: ?Str = null, pattern: model.Model };

pub fn typedFramesAt(a: Allocator, graph: model.Model, enclosing: []const TypedFrame, wanted: Str) anyerror!?[]const TypedFrame {
    for (graph.nodes()) |node| {
        if (std.mem.eql(u16, node.id(), wanted)) return enclosing;
        const Nested = struct { graph: model.Model, frame: ?TypedFrame };
        var nested: std.ArrayList(Nested) = .empty;
        const one = struct {
            fn f(al: Allocator, slot: []const u8, pattern: model.Model) ![]const TypedBinding {
                return al.dupe(TypedBinding, &.{.{ .slot = slot, .pattern = pattern }});
            }
        }.f;
        const kind = node.data.node.kind();
        if (std.mem.eql(u8, kind, "conditional")) {
            try nested.appendSlice(a, &.{ .{ .graph = node.graphPayload("whenTrue"), .frame = null }, .{ .graph = node.graphPayload("whenFalse"), .frame = null } });
        } else if (std.mem.eql(u8, kind, "boolAnd") or std.mem.eql(u8, kind, "boolOr")) {
            try nested.append(a, .{ .graph = node.graphPayload("right"), .frame = null });
        } else if (std.mem.eql(u8, kind, "matchUnion")) {
            for (node.arms()) |arm| {
                var bindings: std.ArrayList(TypedBinding) = .empty;
                for (arm.fieldBindings()) |field| try bindings.append(a, .{ .slot = "field", .field = field.reference("sourceFieldId"), .pattern = field.boundBinding() });
                try nested.append(a, .{ .graph = arm.body(), .frame = .{ .owner = node, .role = "unionArm", .variants = arm.variantIds(), .bindings = bindings.items } });
            }
        } else if (std.mem.eql(u8, kind, "bind")) {
            const body = node.arm("boundBody");
            try nested.append(a, .{ .graph = body.body(), .frame = .{ .owner = node, .role = "bind", .bindings = try one(a, "value", body.armBinding()) } });
        } else if (std.mem.eql(u8, kind, "matchOption")) {
            const some = node.arm("someArm");
            try nested.append(a, .{ .graph = some.body(), .frame = .{ .owner = node, .role = "optionSome", .bindings = try one(a, "value", some.armBinding()) } });
            try nested.append(a, .{ .graph = node.graphPayload("noneBody"), .frame = .{ .owner = node, .role = "optionNone", .bindings = &.{} } });
        } else if (std.mem.eql(u8, kind, "matchResult")) {
            const ok = node.arm("okArm");
            const err = node.arm("errArm");
            try nested.append(a, .{ .graph = ok.body(), .frame = .{ .owner = node, .role = "resultOk", .bindings = try one(a, "value", ok.armBinding()) } });
            try nested.append(a, .{ .graph = err.body(), .frame = .{ .owner = node, .role = "resultErr", .bindings = try one(a, "error", err.armBinding()) } });
        }
        for (nested.items) |scope| {
            const frames = if (scope.frame) |frame| try std.mem.concat(a, TypedFrame, &.{ &.{frame}, enclosing }) else enclosing;
            if (try typedFramesAt(a, scope.graph, frames, wanted)) |found| return found;
        }
    }
    return null;
}
fn typedMemberName(r: *const Request, comptime variants: bool, id: Str) !?model.Model {
    for (try typedVisible(r, "type")) |item| {
        if (!variants) for (item.fields()) |field| if (typedSame(field, id)) return field;
        for (item.variants()) |variant| {
            if (variants and typedSame(variant, id)) return variant;
            if (!variants) for (variant.fields()) |field| if (typedSame(field, id)) return field;
        }
    }
    return null;
}

/// `bindingScopes`: the frames visible at one node of a function, innermost first.
pub fn bindingScopes(r: *Request, out: *Writer) Fail!void {
    if (try fieldProblem(r.a, r.line, &.{}, r.root, "bindingScopes request", &.{ "id", "op", "function", "functionId", "nodeId" }, &.{ "id", "op", "nodeId" })) |message| return r.fail("{s}", .{message});
    const by_name, const by_id = .{ r.root.get("function") != null, r.root.get("functionId") != null };
    if (by_name == by_id) return r.fail("bindingScopes requires exactly one of function and functionId.", .{});
    const function = if (by_name) try typedResolve(r, "function", r.root, "function", &.{"function"}) else blk: {
        const wanted = try exactId(r, "functionId");
        for (try typedVisible(r, "function")) |item| if (std.mem.eql(u8, typedIdOf(item), wanted)) break :blk item;
        return r.fail("No function has the ID '{s}'.", .{wanted});
    };
    const node_id = try exactId(r, "nodeId");
    const frames = try typedFramesAt(r.a, function.body(), &.{}, try utf16(r.a, node_id)) orelse
        return r.fail("Function '{s}' has no node '{s}'.", .{ typedNameOf(function), node_id });
    const names = function.data.function.type_parameters orelse &.{};
    try out.writeAll("{\"functionId\":");
    try writeId(out, function.id());
    try out.writeAll(",\"nodeId\":");
    try writeId(out, try utf16(r.a, node_id));
    try out.writeAll(",\"frames\":[");
    for (frames, 0..) |frame, depth| {
        if (depth > 0) try out.writeByte(',');
        try out.print("{{\"scopeDepth\":{d},\"ownerNodeId\":", .{depth});
        try writeId(out, frame.owner.id());
        try out.print(",\"role\":\"{s}\"", .{frame.role});
        if (frame.variants.len > 0) {
            try out.writeAll(",\"variantIds\":[");
            var known = true;
            for (frame.variants, 0..) |variant, index| {
                if (index > 0) try out.writeByte(',');
                try writeId(out, variant.literal());
                known = known and try typedMemberName(r, true, variant.literal()) != null;
            }
            try out.writeByte(']');
            if (known) {
                try out.writeAll(",\"variants\":[");
                for (frame.variants, 0..) |variant, index| {
                    if (index > 0) try out.writeByte(',');
                    try model.writeString(out, (try typedMemberName(r, true, variant.literal())).?.displayName());
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
                try writeId(out, source);
                if (try typedMemberName(r, false, source)) |f| try typedWriteNamed(out, "field", f);
            }
            try out.writeAll(",\"bindingId\":");
            try writeId(out, binding.pattern.id());
            try out.writeAll(",\"type\":");
            try wire.typedTypeRef(out, r.interner.?, binding.pattern.data.binding.type, names);
            if (binding.pattern.optionalDisplayName()) |display| {
                try out.writeAll(",\"displayName\":");
                try model.writeString(out, display);
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

fn typedLiterals(r: *Request, out: *Writer, function: model.Model) !void {
    try out.writeByte('[');
    var count: usize = 0;
    for (model.typedBodyNodes(r.a, function)) |node| {
        const kind = node.kind();
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
        try writeId(out, node.id());
        const value = node.payload("value").?;
        if (is(kind, "boolLiteral")) {
            try out.writeAll(if (value.bool) ",\"literal\":{\"kind\":\"bool\",\"value\":true}}" else ",\"literal\":{\"kind\":\"bool\",\"value\":false}}");
        } else if (is(kind, "int64Literal")) {
            try out.writeAll(",\"literal\":{\"kind\":\"int64\",\"value\":");
            try out.print("\"{d}\"", .{value.int});
            try out.writeAll("}}");
        } else {
            const text = node.context.interner.stringText(value.lit);
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
    const wanted = if (by_name) (try typedResolve(r, "function", r.root, "function", &.{"function"})).id() else try utf16(r.a, try exactId(r, "functionId"));
    // `FocusFunction` reads the whole model, the dependency's private functions included.
    const function = for (typedRoot(r).functions()) |item| {
        if (typedSame(item, wanted)) break item;
    } else {
        r.rejection = .{ .code = "SemanticQueryFailure", .message = try std.fmt.allocPrint(r.a, "No function has the ID '{s}'.", .{model.utf8(wanted)}) };
        return error.Rejected;
    };
    const nodes = model.typedBodyNodes(r.a, function);
    const updates = for (nodes) |node| {
        if (isKind(node.kind(), "bytesSet") or isKind(node.kind(), "bytesAppend")) break true;
    } else false;
    // The compact focus, open at its end, so each detail appends its own fields.
    var compact: Writer.Allocating = .init(r.a);
    const full = std.mem.eql(u8, detail, "full");
    try typedFocusFields(r, &compact.writer, typedRoot(r), function, wanted, nodes, full, true);
    if (full) {
        try out.writeAll(compact.written());
    } else if (std.mem.eql(u8, detail, "outline")) {
        try typedOutline(r, out, compact.written(), function, nodes);
    } else {
        try out.writeAll(compact.written());
        try out.writeAll(",\"literals\":");
        try typedLiterals(r, out, function);
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
    const ir = model.typedExecutionIrOf(a, typedRoot(r)) orelse return;
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
pub fn typedWriteAborts(out: *Writer, aborts: []const model.TypedAbort, owner: Str) !void {
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
        if (item.error_type) |t| try wire.typeRef(out, model.semanticTypeValue(t), &.{}) else try out.writeAll("null");
        try out.writeAll(",\"enclosingControlNodeIds\":");
        try writeIds(out, item.enclosing);
        try out.writeAll(",\"dependentContinuationNodeIds\":");
        try writeIds(out, item.dependents);
        try out.writeByte('}');
    }
    try out.writeByte(']');
}

pub fn typedReviewFocus(r: *Request, out: *Writer, root: model.Model, function: model.Model, wanted: Str, nodes: []const model.Model, full: bool, structured: bool) !void {
    try typedFocusFields(r, out, root, function, wanted, nodes, full, structured);
    try out.writeByte('}');
}
fn typedFocusFields(r: *Request, out: *Writer, root: model.Model, function: model.Model, wanted: Str, nodes: []const model.Model, full: bool, structured: bool) !void {
    const focus = model.typedFocusOf(r.a, root, function);
    try out.writeAll("{\"snapshotId\":");
    try model.writeString(out, try utf16(r.a, r.snapshot_id));
    try out.writeAll(",\"function\":");
    if (full and structured) try wire.typedFunctionEntity(r.a, out, r.interner.?, function.data.function) else if (full) {
        const typed_nodes = try r.a.alloc(model.session_model.Node, nodes.len);
        for (nodes, typed_nodes) |node, *item| item.* = node.data.node;
        try wire.typedFunctionNodes(out, r.interner.?, function.data.function, typed_nodes);
    } else try wire.typedFunctionSummary(out, r.interner.?, function.data.function, nodes.len);
    try out.writeAll(",\"bodyEffectRequirements\":");
    try writeRequirements(out, focus.requirements);
    if (focus.wait_path) |path| {
        try out.writeAll(",\"mayWait\":true,\"waitPath\":");
        try model.writeString(out, path);
    }
    try out.writeAll(",\"aborts\":");
    try typedWriteAborts(out, focus.aborts, wanted);
    if (full) {
        try out.writeAll(",\"nodeTypes\":[");
        for (focus.node_types, 0..) |item, index| {
            if (index > 0) try out.writeByte(',');
            try out.writeAll("{\"nodeId\":");
            try writeId(out, item.id);
            try out.writeAll(",\"type\":");
            if (item.type) |t| try wire.inferredTypeRef(out, model.semanticTypeValue(t), function.data.function.type_parameters orelse &.{}, r.interner.?) else try out.writeAll("null");
            try out.writeByte('}');
        }
        try out.writeByte(']');
    }
    try out.writeAll(",\"callers\":");
    try writeSites(out, focus.callers);
    try out.writeAll(",\"callees\":");
    try writeSites(out, focus.callees);
    const Obligation = struct { id: Str, item: model.Model };
    var open: std.ArrayList(Obligation) = .empty;
    for (root.obligations()) |obligation| {
        if (obligation.data.obligation.resolution != null) continue;
        const call = obligation.reference("callNodeId");
        for (nodes) |node| if (typedSame(node, call)) {
            try open.append(r.a, .{ .id = obligation.id(), .item = obligation });
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
        try writeId(out, obligation.item.reference("callNodeId"));
        try out.writeAll(",\"parameterId\":");
        try writeId(out, obligation.item.reference("parameterId"));
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

const TypedOutline = struct {
    r: *Request,
    names: std.StringHashMapUnmanaged(Str) = .empty,
    nodes: std.StringHashMapUnmanaged(model.Model) = .empty,
    seen: std.StringHashMapUnmanaged(void) = .empty,
    lines: std.ArrayList([]const u8) = .empty,
    /// The reply's bytes with the lines so far, `outlineTruncated: false` and the budget.
    size: usize,
    truncated: bool = false,

    const max_bytes = 16384;
    const Scope = struct { depth: usize, bindings: []const model.Model };

    fn add(self: *TypedOutline, item: model.Model) !void {
        try self.names.put(self.r.a, model.utf8(item.id()), item.displayName());
    }

    fn named(self: *TypedOutline, id: Str) Str {
        return self.names.get(model.utf8(id)) orelse id;
    }

    fn idAt(node: model.Model, name: []const u8) Str {
        return if (std.mem.eql(u8, name, "id")) node.id() else node.reference(name);
    }

    /// A pattern binding's display name, or `fallback` when it has none.
    fn bindingName(binding: model.Model, fallback: ?Str) ?Str {
        return binding.optionalDisplayName() orelse fallback;
    }

    fn addLine(self: *TypedOutline, line: []const u16) !void {
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

    fn visit(self: *TypedOutline, depth: usize, scopes: []const Scope, id: Str) !void {
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
        const kind = node.kind();
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
        for (model.typedOperandIdsOf(node)) |child| try self.visit(depth + 1, scopes, child);
        for (model.typedScopesOf(node)) |scope| {
            var nested: std.ArrayList(Scope) = .empty;
            if (scope.bindings.len > 0) {
                try nested.append(a, .{ .depth = 0, .bindings = scope.bindings });
                for (scopes) |outer| try nested.append(a, .{ .depth = outer.depth + 1, .bindings = outer.bindings });
            } else try nested.appendSlice(a, scopes);
            try self.visit(depth + 1, nested.items, scope.graph.resultNodeId());
        }
    }

    fn text(self: *TypedOutline, ascii: []const u8) !Str {
        const units = try self.r.a.alloc(u16, ascii.len);
        for (ascii, units) |c, *unit| unit.* = c;
        return units;
    }

    /// The operands `WireTypedOutline.focus` names after a node's kind and ID.
    fn operands(self: *TypedOutline, out: *std.ArrayList(Str), node: model.Model, kind: Str, scopes: []const Scope) !void {
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
            const value = node.context.interner.stringText(node.payload("value").?.lit);
            const shown = if (value.len > 64) try std.mem.concat(a, u16, &.{ value[0..64], &.{0x2026} }) else value;
            var literal: Writer.Allocating = .init(a);
            try model.writeString(&literal.writer, shown);
            try out.append(a, try self.text(literal.written()));
        } else if (isKind(kind, "int64Literal")) {
            try out.append(a, try self.text(try std.fmt.allocPrint(a, "{d}", .{node.payload("value").?.int})));
        } else if (isKind(kind, "boolLiteral")) {
            try out.append(a, try self.text(if (node.payload("value").?.bool) "true" else "false"));
        } else if (isKind(kind, "float64Literal")) {
            try out.append(a, try self.text(try std.fmt.allocPrint(a, "{x:0>16}", .{node.payload("bits").?.bits})));
        } else if (isKind(kind, "fieldAccess")) {
            try out.append(a, self.named(idAt(node, "fieldId")));
        } else if (isKind(kind, "unionConstruct")) {
            try out.append(a, self.named(idAt(node, "variantId")));
        } else if (isKind(kind, "productConstruct") or isKind(kind, "productUpdate")) {
            try out.append(a, self.named(idAt(node, "typeId")));
        } else if (isKind(kind, "capabilityInvoke")) {
            try out.append(a, self.named(idAt(node, "operationId")));
        } else if (isKind(kind, "bind")) {
            const binding = node.arm("boundBody").armBinding();
            try out.append(a, bindingName(binding, try self.text("value")).?);
        } else if (isKind(kind, "matchOption")) {
            const binding = node.arm("someArm").armBinding();
            if (bindingName(binding, null)) |name| try out.append(a, name);
        } else if (isKind(kind, "matchResult")) {
            if (bindingName(node.arm("okArm").armBinding(), null)) |name| try out.append(a, name);
            if (bindingName(node.arm("errArm").armBinding(), null)) |name| try out.append(a, name);
        } else if (isKind(kind, "matchUnion")) {
            for (node.arms()) |arm| for (arm.variantIds()) |variant| try out.append(a, self.named(variant.literal()));
        } else for (model.typedFunctionIdsOf(node)) |function_id| try out.append(a, self.named(function_id));
    }
};

/// `WireTypedOutline.focus`: the compact focus and a bounded outline, one line per node from the result
/// node through operands and nested bodies, cut before the reply passes 16384 bytes.
fn typedOutline(r: *Request, out: *Writer, compact: []const u8, function: model.Model, nodes: []const model.Model) !void {
    const tail_false = "],\"outlineTruncated\":false,\"outlineMaxBytes\":16384";
    var o: TypedOutline = .{ .r = r, .size = compact.len + ",\"outline\":[".len + tail_false.len + "}".len };
    for (function.parameters()) |parameter| try o.add(parameter);
    for (try typedVisible(r, "function")) |item| {
        try o.add(item);
        for (item.parameters()) |parameter| try o.add(parameter);
    }
    for (try typedVisible(r, "type")) |item| {
        try o.add(item);
        for (item.fields()) |field| try o.add(field);
        for (item.variants()) |variant| {
            try o.add(variant);
            for (variant.fields()) |field| try o.add(field);
        }
    }
    for (try typedVisible(r, "capability")) |item| {
        try o.add(item);
        for (item.operations()) |operation| try o.add(operation);
    }
    for (nodes) |node| try o.nodes.put(r.a, model.utf8(TypedOutline.idAt(node, "id")), node);
    try o.visit(0, &.{}, function.body().resultNodeId());
    try out.writeAll(compact);
    try out.writeAll(",\"outline\":[");
    for (o.lines.items, 0..) |line, index| {
        if (index > 0) try out.writeByte(',');
        try out.writeAll(line);
    }
    try out.writeAll(if (o.truncated) "],\"outlineTruncated\":true,\"outlineMaxBytes\":16384" else tail_false);
}

// ---------------------------------------------------------------- viewFunction

/// `viewFunction`: the committed function's Function Focus page, written beside the snapshot.
pub fn viewFunction(r: *Request, out: *Writer) Fail!void {
    if (try fieldProblem(r.a, r.line, &.{}, r.root, "viewFunction request", &.{ "id", "op", "function", "functionId", "output", "format" }, &.{ "id", "op" })) |message| return r.fail("{s}", .{message});
    const by_name, const by_id = .{ r.root.get("function") != null, r.root.get("functionId") != null };
    if (by_name == by_id) return r.fail("viewFunction requires exactly one of function and functionId.", .{});
    const wanted = if (by_name) (try typedResolve(r, "function", r.root, "function", &.{"function"})).id() else try utf16(r.a, try exactId(r, "functionId"));
    const output = if (r.root.get("output") != null) try requiredString(r, r.root, "output") else null;
    const format = try viewFormat(r);
    const function = for (typedRoot(r).functions()) |item| {
        if (typedSame(item, wanted)) break item;
    } else {
        r.rejection = .{ .code = "SemanticQueryFailure", .message = try std.fmt.allocPrint(r.a, "No function has the ID '{s}'.", .{model.utf8(wanted)}) };
        return error.Rejected;
    };
    // Named for a person by the display name, and kept unique by the stable ID (names can repeat across dependencies).
    const name = try std.mem.concat(r.a, u8, &.{ "nyrwvy-focus-", try fileNamePart(r.a, model.utf8(function.displayName())), "-", try fileNameId(r.a, model.utf8(wanted)) });
    const path = try writeView(r, format, output, name, try viewer.typedFocusData(r.a, typedRoot(r), r.snapshot_id, function));
    try out.writeAll("{\"functionId\":");
    try writeId(out, wanted);
    try out.writeAll(",\"displayName\":");
    try model.writeString(out, function.displayName());
    try out.print(",\"kind\":\"functionFocus\",\"mediaType\":\"{s}\",\"path\":", .{viewer.mediaType(format)});
    try @import("root").writeString(out, path);
    try out.writeByte('}');
}

/// The request's optional `format`: "html" (the default) for a person, "json" for an agent.
pub fn viewFormat(r: *Request) Fail!viewer.Format {
    const value = r.root.get("format") orelse return .html;
    if (value == .string) if (std.meta.stringToEnum(viewer.Format, value.string)) |format| return format;
    return r.fail("format must be \"html\" or \"json\".", .{});
}

/// A readable file-name part of display text: ASCII letters, digits, `-` and `_` kept, each run of
/// other bytes one `_`, at most 48 bytes; `_` when nothing is left.
pub fn fileNamePart(a: Allocator, text: []const u8) ![]const u8 {
    var out: std.ArrayList(u8) = .empty;
    for (text) |c| {
        if (out.items.len >= 48) break;
        if (std.ascii.isAlphanumeric(c) or c == '-' or c == '_') try out.append(a, c) else if (out.items.len == 0 or out.items[out.items.len - 1] != '_') try out.append(a, '_');
    }
    return if (out.items.len == 0) "_" else out.items;
}

/// A stable ID as a file-name part: as it is when every byte is safe (UUIDs are), else its bytes in lower-case hex.
pub fn fileNameId(a: Allocator, id: []const u8) ![]const u8 {
    for (id) |c| {
        if (!(std.ascii.isAlphanumeric(c) or c == '-' or c == '_')) return std.fmt.allocPrint(a, "{x}", .{id});
    }
    return if (id.len == 0) "_" else id;
}

test "view file names are readable and keep unusual bytes out" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    try std.testing.expectEqualStrings("csvStreamChunk", try fileNamePart(a, "csvStreamChunk"));
    try std.testing.expectEqualStrings("a_b_c", try fileNamePart(a, "a/../b c"));
    try std.testing.expectEqualStrings("_", try fileNamePart(a, "\u{3042}"));
    try std.testing.expectEqual(@as(usize, 48), (try fileNamePart(a, "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")).len);
    try std.testing.expectEqualStrings("01a16ced-9003-7f53", try fileNameId(a, "01a16ced-9003-7f53"));
    try std.testing.expectEqualStrings("612f62", try fileNameId(a, "a/b"));
}

/// Rewrites `nyrwvy-views/index.html`, the page listing every view page there. Best effort: a view
/// already written stays written when its index cannot be.
fn writeIndex(r: *Request) void {
    const files = view_file.listViews(r.a, r.snapshot_path) catch return;
    var data: Writer.Allocating = .init(r.a);
    const w = &data.writer;
    w.writeAll("{\"view\":\"index\",\"files\":[") catch return;
    for (files, 0..) |file, index| {
        if (index > 0) w.writeByte(',') catch return;
        @import("root").writeString(w, file) catch return;
    }
    w.writeAll("]}") catch return;
    const page = viewer.file(r.a, .html, data.written()) catch return;
    _ = view_file.write(r.a, r.snapshot_path, null, ".html", "index.html", viewer.fileHeader(.html), page) catch {};
}

/// Writes one view beside the snapshot (or at `output`) as `<name>.html` or `<name>.json`; returns its path.
pub fn writeView(r: *Request, format: viewer.Format, output: ?[]const u8, name: []const u8, data: []const u8) Fail![]const u8 {
    const file_name = try std.mem.concat(r.a, u8, &.{ name, viewer.extension(format) });
    switch (try view_file.write(r.a, r.snapshot_path, output, viewer.extension(format), file_name, viewer.fileHeader(format), try viewer.file(r.a, format, data))) {
        .refused => |message| {
            r.rejection = .{ .code = "ViewWriteFailed", .message = message };
            return error.Rejected;
        },
        .internal => {
            r.rejection = .{ .code = "InternalError", .message = "Unexpected internal failure; this request was not applied and the session is unchanged." };
            return error.Rejected;
        },
        .written => |path| {
            if (output == null and format == .html) writeIndex(r);
            return path;
        },
    }
}

// ---------------------------------------------------------------- viewProgram

/// `viewProgram`: every committed function and declaration in one view, written beside the snapshot.
pub fn viewProgram(r: *Request, out: *Writer) Fail!void {
    if (try fieldProblem(r.a, r.line, &.{}, r.root, "viewProgram request", &.{ "id", "op", "output", "format" }, &.{ "id", "op" })) |message| return r.fail("{s}", .{message});
    const output = if (r.root.get("output") != null) try requiredString(r, r.root, "output") else null;
    const format = try viewFormat(r);
    const root = typedRoot(r);
    const path = try writeView(r, format, output, "nyrwvy-program", try viewer.typedProgramData(r.a, root, r.snapshot_id));
    try out.print("{{\"kind\":\"program\",\"functionCount\":{d},\"mediaType\":\"{s}\",\"path\":", .{ root.functions().len, viewer.mediaType(format) });
    try @import("root").writeString(out, path);
    try out.writeByte('}');
}

// ---------------------------------------------------------------- executionReadiness

/// `executionReadiness`: whether the committed snapshot projects and lowers to an execution program.
pub fn executionReadiness(r: *Request, out: *Writer) Fail!void {
    if (try fieldProblem(r.a, r.line, &.{}, r.root, "executionReadiness request", &.{ "id", "op" }, &.{ "id", "op" })) |message| return r.fail("{s}", .{message});
    switch (model.typedExecutionReadinessOf(r.a, typedRoot(r))) {
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
const preflight = @import("preflight.zig");
const infer = @import("infer.zig");

/// The request's `typeParameters` list, or null when it gives none (the list is then derived).
fn typeParameterNames(r: *Request, d: *recipe.Decoder, root: std.json.Value) Fail!?[]const []const u8 {
    if (r.root.get("typeParameters") == null) return null;
    var names: std.ArrayList([]const u8) = .empty;
    for (try d.array(root, "typeParameters")) |item| {
        if (item != .string) return r.fail("typeParameters must list type parameter names as strings.", .{});
        const name = try readable(r, item.string);
        if (isBlank(try utf16(r.a, name))) return r.fail("Type parameter names must not be blank.", .{});
        try names.append(r.a, try d.stableId("typeParameters", item.string));
    }
    return names.items;
}

/// `expectedSnapshotId ()`: the request's, or the open snapshot's.
fn expectedSnapshotId(r: *Request, d: *recipe.Decoder, root: std.json.Value) Fail![]const u8 {
    if (r.root.get("expectedSnapshotId") == null) return r.snapshot_id;
    return d.stableId("expectedSnapshotId", try d.requiredString(root, "expectedSnapshotId"));
}

/// `simulateCreateFunction`: the request read as `Protocol.fs` reads it, field by field in the
/// order F# evaluates them, with each rejection, then the preflight and the inference.
pub fn simulateCreateFunction(r: *Request, out: *Writer) Fail!void {
    if (try fieldProblem(r.a, r.line, &.{}, r.root, "simulateCreateFunction request", &.{ "id", "op", "displayName", "exported", "mustNotWait", "typeParameters", "parameters", "resultType", "requiredEffects", "body", "structuredReview", "detail", "expectedSnapshotId" }, &.{ "id", "op", "displayName", "parameters", "resultType", "requiredEffects", "body" })) |message| return r.fail("{s}", .{message});
    var d: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line) };
    var root: std.json.Value = .{ .object = r.root };
    var parameters: std.ArrayList(preflight.Parameter) = .empty;
    for (try d.array(root, "parameters"), 0..) |item, i| {
        const path = try std.fmt.allocPrint(r.a, "/parameters/{d}", .{i});
        try d.ensureFields(path, item, "function parameter", &.{ "displayName", "type" }, &.{ "displayName", "type" });
        const name = try d.requiredString(item, "displayName");
        try parameters.append(r.a, .{ .name = name, .type = try d.typeRef(try std.fmt.allocPrint(r.a, "{s}/type", .{path}), try d.property(item, "type")) });
    }
    var effects: std.ArrayList([]const u8) = .empty;
    for (try d.array(root, "requiredEffects")) |item| {
        if (item != .string) return r.fail("requiredEffects entries must be stable-ID strings.", .{});
        try effects.append(r.a, try d.stableId("requiredEffects", try readable(r, item.string)));
    }
    const expected = try expectedSnapshotId(r, &d, root);
    const display_name = try d.requiredString(root, "displayName");
    try d.optionalBool(root, "exported");
    try d.optionalBool(root, "mustNotWait");
    const type_parameters = try typeParameterNames(r, &d, root);
    const result_type = try d.typeRef("/resultType", try d.property(root, "resultType"));
    const body = root.object.getPtr("body").?;
    try recipe.resolveNames(&d, "/body", body, try recipe.rawProperty(r.a, r.line, "body"));
    const tree = try d.recipe("/body", body.*);
    const creation: preflight.Creation = .{ .expected_snapshot_id = expected, .display_name = display_name, .type_parameters = type_parameters, .parameters = parameters.items, .result_type = result_type, .required_effects = effects.items, .body = tree };
    try r.ensureUsable();
    const fresh = try preflight.run(r, creation);
    var inferred = try infer.run(r, creation, fresh);
    try @import("proposals.zig").create(r, out, creation, fresh, &inferred);
}

/// `simulateCreateFunctionByName` as `functionCreationByName` reads it, then the preflight and the inference.
pub fn simulateCreateFunctionByName(r: *Request, out: *Writer) Fail!void {
    if (try fieldProblem(r.a, r.line, &.{}, r.root, "simulateCreateFunctionByName request", &.{ "id", "op", "displayName", "exported", "mustNotWait", "typeParameters", "parameters", "resultType", "requiredEffects", "body", "bodyFile", "structuredReview", "detail", "expectedSnapshotId" }, &.{ "id", "op", "displayName", "parameters", "resultType", "requiredEffects" })) |message| return r.fail("{s}", .{message});
    var decoder: recipe.Decoder = .{ .r = r, .duplicates = .empty };
    const creation = try functionCreationByName(r, try expectedSnapshotId(r, &decoder, .{ .object = r.root }));
    try r.ensureUsable();
    const fresh = try preflight.run(r, creation);
    var inferred = try infer.run(r, creation, fresh);
    try @import("proposals.zig").create(r, out, creation, fresh, &inferred);
}

pub fn functionCreationByName(r: *Request, expected_snapshot: []const u8) Fail!preflight.Creation {
    var d: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line) };
    const root: std.json.Value = .{ .object = r.root };
    const expected = expected_snapshot;
    const display_name = try d.requiredString(root, "displayName");
    for (try typedVisible(r, "function")) |item| if (std.mem.eql(u8, nameOf(item), display_name))
        return r.fail("Function display name '{s}' already exists. Choose a unique name or use the exact-ID authoring surface.", .{display_name});
    d.owner = .{ .name = display_name };
    var parameters: std.ArrayList(preflight.Parameter) = .empty;
    var names: std.ArrayList([]const u8) = .empty;
    for (try d.array(root, "parameters"), 0..) |item, i| {
        const path = try std.fmt.allocPrint(r.a, "/parameters/{d}", .{i});
        try d.ensureFields(path, item, "function parameter", &.{ "displayName", "type" }, &.{ "displayName", "type" });
        const name = try d.requiredString(item, "displayName");
        try names.append(r.a, name);
        try parameters.append(r.a, .{ .name = name, .type = try d.anyType(try std.fmt.allocPrint(r.a, "{s}/type", .{path}), try d.property(item, "type")) });
    }
    d.owner.?.parameters = names.items;
    try d.optionalBool(root, "exported");
    try d.optionalBool(root, "mustNotWait");
    const type_parameters = try typeParameterNames(r, &d, root);
    const result_type = try d.anyType("/resultType", try d.property(root, "resultType"));
    var effects: std.ArrayList([]const u8) = .empty;
    for (try d.array(root, "requiredEffects"), 0..) |item, i| try effects.append(r.a, idOf(try d.selector(try std.fmt.allocPrint(r.a, "/requiredEffects/{d}", .{i}), item, "effect")));
    const tree = try @import("recipe_file.zig").input(&d, "body", "bodyFile");
    return preflight.Creation{ .expected_snapshot_id = expected, .display_name = display_name, .type_parameters = type_parameters, .parameters = parameters.items, .result_type = result_type, .required_effects = effects.items, .body = tree };
}

// ---------------------------------------------------------------- simulateReplaceBody

pub fn simulateReplaceBody(r: *Request, out: *Writer) Fail!void {
    if (try fieldProblem(r.a, r.line, &.{}, r.root, "simulateReplaceBody request", &.{ "id", "op", "functionId", "expectedCurrentResultNodeId", "replacement", "structuredReview", "detail", "expectedSnapshotId" }, &.{ "id", "op", "functionId", "expectedCurrentResultNodeId", "replacement" })) |message| return r.fail("{s}", .{message});
    var d: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line) };
    const root: std.json.Value = .{ .object = r.root };
    const function_id = try d.stableId("functionId", try d.requiredString(root, "functionId"));
    const expected = try expectedSnapshotId(r, &d, root);
    const result_id = try d.stableId("expectedCurrentResultNodeId", try d.requiredString(root, "expectedCurrentResultNodeId"));
    const replacement = r.root.getPtr("replacement").?;
    try recipe.resolveNames(&d, "/replacement", replacement, try recipe.rawProperty(r.a, r.line, "replacement"));
    const tree = try d.recipe("/replacement", replacement.*);
    try replaceBody(r, out, function_id, expected, result_id, tree);
}

pub fn simulateReplaceBodyByName(r: *Request, out: *Writer) Fail!void {
    if (try fieldProblem(r.a, r.line, &.{}, r.root, "simulateReplaceBodyByName request", &.{ "id", "op", "function", "replacement", "replacementFile", "structuredReview", "detail", "expectedSnapshotId", "expectedResultNodeId" }, &.{ "id", "op", "function" })) |message| return r.fail("{s}", .{message});
    var d: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line) };
    const root: std.json.Value = .{ .object = r.root };
    const target = try d.selector("/function", try d.property(root, "function"), "function");
    const expected = try expectedSnapshotId(r, &d, root);
    const result_id = if (r.root.get("expectedResultNodeId") != null)
        try d.stableId("expectedResultNodeId", try d.requiredString(root, "expectedResultNodeId"))
    else
        model.utf8(target.body().resultNodeId());
    const parameters = target.parameters();
    const names = try r.a.alloc([]const u8, parameters.len);
    for (parameters, names) |p, *name| name.* = nameOf(p);
    d.owner = .{ .name = nameOf(target), .parameters = names, .existing = target };
    const tree = try recipe.bindExistingTypes(&d, try @import("recipe_file.zig").input(&d, "replacement", "replacementFile"));
    try replaceBody(r, out, idOf(target), expected, result_id, tree);
}

pub fn simulateReplaceTarget(r: *Request, out: *Writer) Fail!void {
    if (try fieldProblem(r.a, r.line, &.{}, r.root, "simulateReplaceTarget request", &.{ "id", "op", "functionId", "targetNodeId", "replacement", "structuredReview", "detail", "expectedSnapshotId" }, &.{ "id", "op", "functionId", "targetNodeId", "replacement" })) |message| return r.fail("{s}", .{message});
    var d: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line) };
    const root: std.json.Value = .{ .object = r.root };
    const function_id = try d.stableId("functionId", try d.requiredString(root, "functionId"));
    const expected = try expectedSnapshotId(r, &d, root);
    const result_id = try d.stableId("targetNodeId", try d.requiredString(root, "targetNodeId"));
    const replacement = r.root.getPtr("replacement").?;
    try recipe.resolveNames(&d, "/replacement", replacement, try recipe.rawProperty(r.a, r.line, "replacement"));
    const tree = try d.recipe("/replacement", replacement.*);
    try r.ensureUsable();
    try @import("target.zig").simulate(r, out, function_id, expected, result_id, tree);
}

pub fn simulateReplaceTargetByName(r: *Request, out: *Writer) Fail!void {
    if (try fieldProblem(r.a, r.line, &.{}, r.root, "simulateReplaceTargetByName request", &.{ "id", "op", "function", "replacement", "replacementFile", "structuredReview", "detail", "expectedSnapshotId", "targetNodeId" }, &.{ "id", "op", "function", "targetNodeId" })) |message| return r.fail("{s}", .{message});
    var d: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line) };
    const root: std.json.Value = .{ .object = r.root };
    const target = try d.selector("/function", try d.property(root, "function"), "function");
    const expected = try expectedSnapshotId(r, &d, root);
    const result_id = try d.stableId("targetNodeId", try d.requiredString(root, "targetNodeId"));
    const parameters = target.parameters();
    const names = try r.a.alloc([]const u8, parameters.len);
    for (parameters, names) |p, *name| name.* = nameOf(p);
    d.owner = .{ .name = nameOf(target), .parameters = names, .existing = target };
    const tree = try recipe.bindExistingTypes(&d, try @import("recipe_file.zig").input(&d, "replacement", "replacementFile"));
    try r.ensureUsable();
    try @import("target.zig").simulate(r, out, idOf(target), expected, result_id, tree);
}

fn usesOriginal(a: Allocator, node: *const recipe.Node) Fail!bool {
    if (std.mem.eql(u8, node.case, "OriginalTarget")) return true;
    for (try recipe.children(a, node)) |child| if (try usesOriginal(a, child)) return true;
    return false;
}

fn replaceBody(r: *Request, out: *Writer, function_id: []const u8, expected: []const u8, result_id: []const u8, tree: *const recipe.Node) Fail!void {
    try r.ensureUsable();
    if (!std.mem.eql(u8, expected, r.snapshot_id)) return preflight.rejected(r, "StructuralExpectedBaseMismatch", "The structural request targets a different base snapshot.");
    const target = preflight.typedDeclaration(r, "functions", function_id) orelse return preflight.rejected(r, "StructuralTargetNotFound", "The target function does not exist.");
    const current = model.utf8(target.body().resultNodeId());
    if (!std.mem.eql(u8, current, result_id)) return preflight.rejected(r, "StructuralExpectedResultMismatch", "The target function result node changed after inspection.");
    if (try usesOriginal(r.a, tree)) {
        return @import("target.zig").simulate(r, out, function_id, expected, result_id, tree);
    }
    try preflight.validateRecipeBounds(r, tree);
    const parameters = target.parameters();
    const owner_parameters = try r.a.alloc(preflight.Parameter, parameters.len);
    for (parameters, owner_parameters) |p, *v| v.* = .{ .name = nameOf(p), .type = try infer.typeOf(r.a, p.declaredType()) };
    const effects = target.requiredEffects();
    const owner_effects = try r.a.alloc([]const u8, effects.len);
    for (effects, owner_effects) |e, *v| v.* = model.utf8(e.literal());
    const owner: preflight.Creation = .{ .expected_snapshot_id = expected, .display_name = nameOf(target), .type_parameters = null, .parameters = owner_parameters, .result_type = try infer.typeOf(r.a, target.resultType()), .required_effects = owner_effects, .body = tree };
    var inferred = try infer.runIn(r, owner, &.{}, target);
    const nodes = model.typedBodyNodes(r.a, target);
    for (r.typedModel().obligations()) |obligation| {
        const call = model.utf8(obligation.reference("callNodeId"));
        for (nodes) |node| if (std.mem.eql(u8, idOf(node), call))
            return preflight.rejected(r, "StructuralBodyReferencedByObligation", try std.fmt.allocPrint(r.a, "Obligation '{s}' targets the body being replaced.", .{idOf(obligation)}));
    }
    try @import("proposals.zig").replace(r, out, target, tree, &inferred);
}

test "typed search finds visible names and literals" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    model.useAllocator(a);
    const root = try model.parseValue(a,
        \\{"snapshotId":"s","types":[],"effects":[{"id":"e","displayName":"Hidden needle"}],"capabilities":[],"functions":[{"id":"f","displayName":"Needle function","parameters":[],"resultType":{"kind":"primitive","primitive":"string"},"requiredEffects":[],"body":{"nodes":[{"id":"n","kind":"stringLiteral","value":"a needle"}],"resultNodeId":"n"}}],"obligations":[]}
    );
    var interner: model.session_model.Interner = .{ .gpa = a };
    defer interner.deinit();
    const generation = try model.session_model.load(a, &interner, root, null);
    const line = "{\"id\":1,\"op\":\"search\",\"text\":\"needle\"}";
    const request = try std.json.parseFromSlice(std.json.Value, a, line, .{});
    var r: Request = .{ .a = a, .line = line, .root = request.value.object, .provenance = .{ .hidden = &.{"effect:e"}, .members = &.{} }, .generation = &generation, .interner = &interner };
    var out: Writer.Allocating = .init(a);
    try search(&r, &out.writer);
    try std.testing.expectEqualStrings("{\"text\":\"needle\",\"totalCount\":2,\"matches\":[{\"kind\":\"function\",\"name\":\"Needle function\",\"functionId\":\"f\"},{\"kind\":\"stringLiteral\",\"function\":\"Needle function\",\"functionId\":\"f\",\"nodeId\":\"n\",\"value\":\"a needle\"}]}", out.written());
}

test "typed references and binding scopes do not read a value model" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    model.useAllocator(a);
    const root = try model.parseValue(a,
        \\{"snapshotId":"s","types":[],"effects":[],"capabilities":[],"functions":[{"id":"f","displayName":"F","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"nodes":[{"id":"n","kind":"unitLiteral"},{"id":"b","kind":"bind","inputNodeId":"n","boundBody":{"valueBinding":{"id":"v","displayName":"value","type":{"kind":"primitive","primitive":"unit"}},"body":{"nodes":[{"id":"x","kind":"unitLiteral"}],"resultNodeId":"x"}}}],"resultNodeId":"b"}}],"obligations":[]}
    );
    var interner: model.session_model.Interner = .{ .gpa = a };
    defer interner.deinit();
    const generation = try model.session_model.load(a, &interner, root, null);
    const lines = [_][]const u8{ "{\"id\":1,\"op\":\"references\",\"kind\":\"function\",\"function\":\"F\"}", "{\"id\":2,\"op\":\"unreferenced\"}", "{\"id\":3,\"op\":\"bindingScopes\",\"functionId\":\"f\",\"nodeId\":\"n\"}", "{\"id\":4,\"op\":\"bindingScopes\",\"functionId\":\"f\",\"nodeId\":\"x\"}" };
    const expected = [_][]const u8{ "{\"subject\":{\"kind\":\"function\",\"id\":\"f\",\"displayName\":\"F\"},\"count\":0,\"references\":[]}", "{\"functions\":[{\"id\":\"f\",\"name\":\"F\"}],\"operations\":[],\"types\":[]}", "{\"functionId\":\"f\",\"nodeId\":\"n\",\"frames\":[]}", "{\"functionId\":\"f\",\"nodeId\":\"x\",\"frames\":[{\"scopeDepth\":0,\"ownerNodeId\":\"b\",\"role\":\"bind\",\"bindings\":[{\"slot\":\"value\",\"bindingId\":\"v\",\"type\":{\"kind\":\"primitive\",\"primitive\":\"unit\"},\"displayName\":\"value\"}]}]}" };
    for (lines, expected, [_]*const fn (*Request, *Writer) anyerror!void{ references, unreferenced, bindingScopes, bindingScopes }) |line, answer, run| {
        const parsed = try std.json.parseFromSlice(std.json.Value, a, line, .{});
        var r: Request = .{ .a = a, .line = line, .root = parsed.value.object, .provenance = null, .generation = &generation, .interner = &interner };
        var out: Writer.Allocating = .init(a);
        try run(&r, &out.writer);
        try std.testing.expectEqualStrings(answer, out.written());
    }
}

test "typed focus and viewer use the generation without a value model" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    model.beginRequest(a);
    const root = try model.parseValue(a,
        \\{"snapshotId":"s","types":[],"effects":[],"capabilities":[],"functions":[{"id":"f","displayName":"F","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"nodes":[{"id":"n","kind":"unitLiteral"}],"resultNodeId":"n"}}],"obligations":[]}
    );
    var interner: model.session_model.Interner = .{ .gpa = a };
    defer interner.deinit();
    const generation = try model.session_model.load(a, &interner, root, null);
    var r: Request = .{ .a = a, .line = "", .root = .empty, .provenance = null, .generation = &generation, .interner = &interner, .snapshot_id = "s" };
    const typed_root = r.typedModel();
    const function = typed_root.functions()[0];
    const nodes = model.typedBodyNodes(a, function);
    var out: Writer.Allocating = .init(a);
    try typedReviewFocus(&r, &out.writer, typed_root, function, function.id(), nodes, true, true);
    try std.testing.expect(std.mem.indexOf(u8, out.written(), "\"resultNodeId\":\"n\"") != null);
    var outline_out: Writer.Allocating = .init(a);
    try typedOutline(&r, &outline_out.writer, "{}", function, nodes);
    try std.testing.expect(std.mem.indexOf(u8, outline_out.written(), "unitLiteral n") != null);
    const page = try viewer.typedFocusValue(a, typed_root, "s", function);
    try std.testing.expect(std.mem.indexOf(u8, page, "\"graph\":{\"resultNodeId\":\"n\"") != null);
}
