//! The startup modes beside `open` (`Program.fs` `CommandLine.parse`): create, pin and the three
//! upgrade forms, as `AuthoringSession.createNewWithDependencies`, `pinExistingDependency` and
//! `upgradeUsing` give them. Each ends with the written files loaded as `open` loads them.
const std = @import("std");
const model = @import("model_reader");
const records = @import("snapshot_reader");
const host = @import("main.zig");
const query = @import("query.zig");
const planning = @import("dependency_upgrade.zig");
const recipe = @import("recipe.zig");
const Allocator = std.mem.Allocator;
const Json = std.json.Value;
const tm = model.session_model;

pub const Replacement = struct { source: []const u8, replaces: []const u8 };
pub const Mode = union(enum) {
    open: bool,
    create: []const []const u8,
    pin: ?[]const u8,
    upgrade: struct { source: []const u8, replaces: ?[]const u8, expect: []const u8 },
    upgrades: struct { items: []const Replacement, review: []const u8 },
    selection: struct { sources: []const []const u8, replaces: []const []const u8, review: []const u8 },
};

fn eql(x: []const u8, y: []const u8) bool {
    return std.mem.eql(u8, x, y);
}

fn blank(a: Allocator, text: []const u8) bool {
    return recipe.isBlank(a, text) catch true;
}

/// `CommandLine.parse` after `--allow-host-sources` is removed; null prints the usage.
pub fn parse(a: Allocator, args: []const []const u8) ?Mode {
    if (args.len < 3 or !eql(args[1], "--snapshot")) return null;
    const verb = args[0];
    const rest = args[3..];
    if (eql(verb, "open")) {
        if (rest.len == 0) return .{ .open = false };
        if (rest.len == 1 and eql(rest[0], "--read-only")) return .{ .open = true };
    } else if (eql(verb, "create")) {
        if (rest.len % 2 != 0) return null;
        const paths = a.alloc([]const u8, rest.len / 2) catch return null;
        for (paths, 0..) |*path, i| {
            if (!eql(rest[2 * i], "--dependency")) return null;
            path.* = rest[2 * i + 1];
        }
        return .{ .create = paths };
    } else if (eql(verb, "pin")) {
        if (rest.len == 0) return .{ .pin = null };
        if (rest.len == 2 and eql(rest[0], "--dependency")) return .{ .pin = rest[1] };
    } else if (eql(verb, "upgrade") and rest.len >= 4) {
        if (eql(rest[0], "--dependency") and eql(rest[2], "--expect")) {
            if (rest.len == 4) return .{ .upgrade = .{ .source = rest[1], .replaces = null, .expect = rest[3] } };
            if (rest.len == 6 and eql(rest[4], "--replaces")) return .{ .upgrade = .{ .source = rest[1], .replaces = rest[5], .expect = rest[3] } };
        } else if (rest.len == 4 and eql(rest[2], "--review")) {
            if (eql(rest[0], "--upgrades")) return upgrades(a, rest[1], rest[3]);
            if (eql(rest[0], "--selection")) return selection(a, rest[1], rest[3]);
        }
    }
    return null;
}

/// `JsonDocument` keeps a repeated name, which no accepted shape allows.
fn document(a: Allocator, payload: []const u8) ?Json {
    return std.json.parseFromSliceLeaky(Json, a, payload, .{ .duplicate_field_behavior = .@"error" }) catch null;
}

/// A non-empty array of `{source,replaces}` objects with non-blank strings.
fn upgrades(a: Allocator, payload: []const u8, review: []const u8) ?Mode {
    const root = document(a, payload) orelse return null;
    if (root != .array or root.array.items.len == 0 or blank(a, review)) return null;
    const items = a.alloc(Replacement, root.array.items.len) catch return null;
    for (root.array.items, items) |item, *slot| {
        if (item != .object or item.object.count() != 2) return null;
        const source = item.object.get("source") orelse return null;
        const replaces = item.object.get("replaces") orelse return null;
        if (source != .string or replaces != .string or blank(a, source.string) or blank(a, replaces.string)) return null;
        slot.* = .{ .source = source.string, .replaces = replaces.string };
    }
    return .{ .upgrades = .{ .items = items, .review = review } };
}

/// A `{sources,replaces}` object of string arrays.
fn selection(a: Allocator, payload: []const u8, review: []const u8) ?Mode {
    const root = document(a, payload) orelse return null;
    if (root != .object or root.object.count() != 2 or blank(a, review)) return null;
    var lists: [2][]const []const u8 = undefined;
    for ([_][]const u8{ "sources", "replaces" }, &lists) |name, *slot| {
        const value = root.object.get(name) orelse return null;
        if (value != .array) return null;
        const texts = a.alloc([]const u8, value.array.items.len) catch return null;
        for (value.array.items, texts) |item, *text| text.* = switch (item) {
            .string => |s| s,
            // `GetString` gives null for a JSON null, which the planner reports as blank.
            .null => "",
            else => return null,
        };
        slot.* = texts;
    }
    return .{ .selection = .{ .sources = lists[0], .replaces = lists[1], .review = review } };
}

pub fn start(io: std.Io, a: Allocator, given: []const u8, mode: Mode) !void {
    switch (mode) {
        .open => unreachable,
        .create => |sources| try create(io, a, given, sources),
        .pin => |source| try pin(io, a, given, source),
        else => try upgrade(io, a, given, mode),
    }
}

fn rejected(r: *query.Request) noreturn {
    host.startupFailure("{s}: {s}", .{ r.rejection.code, r.rejection.message });
}

fn refused(problem: ?query.Rejection) void {
    if (problem) |p| host.startupFailure("{s}: {s}", .{ p.code, p.message });
}

/// `SessionStorage.fullPath` as `pinSource` applies it; an empty path stays invalid.
fn sourcePath(io: std.Io, a: Allocator, given: []const u8) ![]const u8 {
    return if (given.len == 0) given else host.absolute(io, a, given);
}

fn emptyObject(a: Allocator) !std.json.ObjectMap {
    return (try std.json.parseFromSliceLeaky(Json, a, "{}", .{})).object;
}

fn createAtomically(a: Allocator, path: []const u8, content: []const u8) !?query.Rejection {
    return switch (try @import("view_file.zig").createAtomic(a, path, content)) {
        .refused => |message| .{ .code = "IoFailure", .message = message },
        .internal => unreachable,
        .written => null,
    };
}

/// `createNewWithDependencies`: the sidecar of the combined pins first, then the empty snapshot
/// with a new ID, then the default backend declaration when none is there.
fn create(io: std.Io, a: Allocator, given: []const u8, sources: []const []const u8) !void {
    const path = try host.absolute(io, a, given);
    host.snapshot_path = path;
    host.writable = true;
    const sidecar = try std.mem.concat(a, u8, &.{ path, ".dependency.json" });
    std.Io.Dir.cwd().createDirPath(io, std.fs.path.dirname(path) orelse "/") catch |e| host.startupFailure("IoFailure: {s}", .{@errorName(e)});
    const taken = "SnapshotFileAlreadyExists: A snapshot already exists at {s}. Use open to resume it; create never overwrites.";
    if (host.exists(io, path) or host.exists(io, sidecar)) host.startupFailure(taken, .{path});
    try host.lock(io, a, path, false);
    if (host.exists(io, path) or host.exists(io, sidecar)) host.startupFailure(taken, .{path});
    var r: query.Request = .{ .a = a, .line = "", .root = try emptyObject(a), .provenance = null, .snapshot_path = path };
    var pinned: std.ArrayList(planning.Pinned) = .empty;
    for (sources) |source| try pinned.append(a, planning.pin(&r, try sourcePath(io, a, source)) catch |e| switch (e) {
        error.Rejected => rejected(&r),
        else => return e,
    });
    for (pinned.items, 0..) |item, i| for (pinned.items[0..i]) |before| if (eql(item.source, before.source))
        host.startupFailure("DependencyFailure: Each dependency may be selected only once.", .{});
    const selected = if (pinned.items.len == 0) null else planning.combine(&r, pinned.items) catch |e| switch (e) {
        error.Rejected => rejected(&r),
        else => return e,
    };
    // After the pins: their IDs raise the seeded allocator's high-water mark.
    const id = try @import("ids.zig").next();
    // An empty typed generation with the new ID, marked with the combined selection.
    var interner: tm.Interner = .{ .gpa = a };
    var generation: tm.Generation = .{ .snapshot = tm.none, .parent = tm.none, .marker = null, .types = &.{}, .effects = &.{}, .capabilities = &.{}, .functions = &.{}, .obligations = &.{} };
    generation.snapshot = try interner.id(.snapshot, try std.unicode.utf8ToUtf16LeAlloc(a, &id));
    const encoded = try publish(a, path, model.typedRoot(a, &interner, &generation), if (selected) |s| s[0] else null);
    if (selected) |s| refused(try createAtomically(a, sidecar, s[1]));
    if (try createAtomically(a, path, encoded)) |problem| {
        // Only while no snapshot owns it: a racing writer's snapshot keeps its sidecar.
        if (selected != null and !host.exists(io, path)) std.Io.Dir.cwd().deleteFile(io, sidecar) catch {};
        refused(problem);
    }
    // A failed declaration only leaves the workspace undeclared, which runPure reports.
    const backends = try std.mem.concat(a, u8, &.{ path, ".backends.json" });
    if (!host.exists(io, backends)) _ = createAtomically(a, backends, "{\"targets\": [\"csharp\"]}\n") catch null;
    try host.load(io, a, path, false);
}

/// `pinExistingDependency`: open, then re-embed a legacy or relocated single pin's exact content.
fn pin(io: std.Io, a: Allocator, given: []const u8, source: ?[]const u8) !void {
    const path = try host.existing(io, a, given, true);
    try host.lock(io, a, path, true);
    host.source_override = if (source) |s| try sourcePath(io, a, s) else null;
    try host.load(io, a, path, false);
    host.source_override = null;
    const selected = host.selection orelse host.startupFailure("DependencyFailure: This workspace has no dependency selection to pin.", .{});
    const member = selected.provenance.members[0];
    if (source == null and member.embedded) return;
    // `persistPin`: only the manifest is replaced, and only while both files are as read.
    const manifest = try planning.write(a, try planning.embedded(a, selected, member.path));
    const sidecar = try std.mem.concat(a, u8, &.{ path, ".dependency.json" });
    const current = host.readText(a, path) catch |e| host.startupFailure("IoFailure: {s}", .{@errorName(e)});
    const saved = host.readText(a, sidecar) catch |e| host.startupFailure("IoFailure: {s}", .{@errorName(e)});
    if (!eql(current, host.persisted_source) or !eql(saved, host.dependency_manifest.?)) host.startupFailure("ExternalModification: The snapshot file changed outside this session.", .{});
    refused(try host.replaceAtomically(a, sidecar, manifest));
    // Not reloaded: F# keeps the opened state, and rereading would observe IDs in the new sidecar's path.
    const members = try a.dupe(@TypeOf(member), selected.provenance.members);
    members[0].embedded = true;
    host.selection.?.provenance.members = members;
    host.dependency_manifest = manifest;
}

fn writeReplacement(w: *std.Io.Writer, source: []const u8, replaces: []const u8) !void {
    try w.writeAll("{\"source\":");
    try host.writeString(w, source);
    try w.writeAll(",\"replaces\":");
    try host.writeString(w, replaces);
    try w.writeByte('}');
}

/// `upgradeUsing`: the reviewed final selection planned as the preview plans it, then the
/// resolved snapshot and its sidecar written together.
fn upgrade(io: std.Io, a: Allocator, given: []const u8, mode: Mode) !void {
    const path = try host.existing(io, a, given, true);
    try host.lock(io, a, path, false);
    try host.load(io, a, path, false);
    const no_dependency = "DependencyFailure: This workspace has no dependency to upgrade; add one with a selection whose replaces is empty.";
    var line: std.Io.Writer.Allocating = .init(a);
    const w = &line.writer;
    try w.writeAll("{\"id\":\"startup\",\"op\":\"previewDependencyUpgrades\",");
    switch (mode) {
        .upgrade => |single| {
            if (host.selection == null) host.startupFailure(no_dependency, .{});
            var r = try host.startupRequest(a, "{}", try emptyObject(a));
            const current = planning.members(&r) catch |e| switch (e) {
                error.Rejected => rejected(&r),
                else => return e,
            };
            if (single.replaces == null and current.len != 1) host.startupFailure("DependencyFailure: Name the dependency to replace by its SnapshotId when the workspace has several.", .{});
            const target = for (current) |item| {
                if (single.replaces == null or eql(item.dependency.id, single.replaces.?)) break item;
            } else host.startupFailure("DependencyFailure: No selected dependency has SnapshotId '{s}'.", .{single.replaces.?});
            // shortcut: planned as a one-item batch, which also refuses a duplicate final SnapshotId
            // before combining; F# reports the combination's failure for that case.
            try w.writeAll("\"upgrades\":[");
            try writeReplacement(w, try sourcePath(io, a, single.source), target.dependency.id);
            try w.writeByte(']');
        },
        .upgrades => |batch| {
            if (host.selection == null) host.startupFailure(no_dependency, .{});
            try w.writeAll("\"upgrades\":[");
            for (batch.items, 0..) |item, i| {
                if (i > 0) try w.writeByte(',');
                try writeReplacement(w, try sourcePath(io, a, item.source), item.replaces);
            }
            try w.writeByte(']');
        },
        .selection => |change| {
            try w.writeAll("\"selection\":{\"sources\":[");
            for (change.sources, 0..) |source, i| {
                if (i > 0) try w.writeByte(',');
                // Blank sources reach the planner's own check unresolved.
                try host.writeString(w, if (blank(a, source)) source else try host.absolute(io, a, source));
            }
            try w.writeAll("],\"replaces\":[");
            for (change.replaces, 0..) |target, i| {
                if (i > 0) try w.writeByte(',');
                try host.writeString(w, target);
            }
            try w.writeAll("]}");
        },
        else => unreachable,
    }
    try w.writeByte('}');
    const root = try std.json.parseFromSliceLeaky(Json, a, line.written(), .{});
    var r = try host.startupRequest(a, line.written(), root.object);
    const plan = planning.planChange(&r) catch |e| switch (e) {
        error.Rejected => rejected(&r),
        else => return e,
    };
    switch (mode) {
        .upgrade => |single| {
            const fingerprint = model.utf8(model.decoded(model.property(model.arrayItems(plan.preview, "selected")[0], "fingerprint").?));
            if (!eql(fingerprint, single.expect)) host.startupFailure("DependencyFailure: The source differs from the reviewed upgrade version.", .{});
        },
        .upgrades => |batch| if (!eql(plan.fingerprint, batch.review))
            host.startupFailure("DependencyFailure: The workspace, current pins, targets or new selected content differ from the reviewed batch. Preview the dependency upgrades again.", .{}),
        .selection => |change| if (!eql(plan.fingerprint, change.review))
            host.startupFailure("DependencyFailure: The workspace, current pins, targets or new selected content differ from the reviewed selection. Preview the dependency upgrades again.", .{}),
        else => unreachable,
    }
    const issues = model.arrayItems(plan.preview, "issues");
    if (issues.len > 0) {
        const code = model.utf8(model.decoded(model.property(issues[0], "code").?));
        const message = model.utf8(model.decoded(model.property(issues[0], "message").?));
        if (issues.len == 1) host.startupFailure("WorkspaceValidationFailure: The workspace graph is invalid: {s}: {s}", .{ code, message });
        host.startupFailure("WorkspaceValidationFailure: The workspace graph is invalid: {s}: {s} ({d} issues; see details)", .{ code, message, issues.len });
    }
    // `SnapshotCodec.serializeResolved`: the local declarations at the current schema, marked with the new selection.
    const encoded = try publish(a, path, plan.local, plan.dependency);
    refused(if (host.selection != null) try host.replaceDependencyPair(a, encoded, plan.manifest) else try addDependencyPair(io, a, path, encoded, plan.manifest));
    try host.load(io, a, path, false);
}

/// `root` written at the current schema, marked with `selected`, in the form `path`'s extension selects.
fn publish(a: Allocator, path: []const u8, root: model.Model, selected: ?host.Dependency) ![]const u8 {
    var generation = root.data.snapshot.*;
    generation.marker = if (selected) |dep| .{ .snapshot = try std.unicode.utf8ToUtf16LeAlloc(a, dep.id), .digest = try std.unicode.utf8ToUtf16LeAlloc(a, dep.digest) } else null;
    var out: std.Io.Writer.Allocating = .init(a);
    try model.session_writer.writeDocument(&out.writer, root.context.interner, &generation, model.currentVersion());
    return if (std.mem.endsWith(u8, path, ".nyrec")) try records.recordsOf(a, out.written()) else out.written();
}

/// `SessionStorage.addDependencyPair`: a first dependency's sidecar is created, then the snapshot
/// replaced; a failed snapshot write removes the sidecar again.
fn addDependencyPair(io: std.Io, a: Allocator, path: []const u8, source: []const u8, manifest: []const u8) !?query.Rejection {
    const sidecar = try std.mem.concat(a, u8, &.{ path, ".dependency.json" });
    const current = host.readText(a, path) catch |e| return .{ .code = "IoFailure", .message = @errorName(e) };
    if (!eql(current, host.persisted_source) or host.exists(io, sidecar)) return .{ .code = "ExternalModification", .message = "The snapshot file changed outside this session." };
    if (try createAtomically(a, sidecar, manifest)) |problem| return problem;
    const problem: ?query.Rejection = host.replaceAtomically(a, path, source) catch |e| .{ .code = "IoFailure", .message = @errorName(e) };
    if (problem != null) std.Io.Dir.cwd().deleteFile(io, sidecar) catch {};
    return problem;
}

test "startup arguments follow CommandLine.parse" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    try std.testing.expect(parse(a, &.{ "open", "--snapshot", "x" }).?.open == false);
    try std.testing.expect(parse(a, &.{ "open", "--snapshot", "x", "--read-only" }).?.open);
    try std.testing.expectEqual(@as(usize, 2), parse(a, &.{ "create", "--snapshot", "x", "--dependency", "a", "--dependency", "b" }).?.create.len);
    try std.testing.expect(parse(a, &.{ "create", "--snapshot", "x", "--dependency" }) == null);
    try std.testing.expect(parse(a, &.{ "pin", "--snapshot", "x", "--dependency", "a", "--dependency", "b" }) == null);
    try std.testing.expect(parse(a, &.{ "migrate", "--snapshot", "x" }) == null);
    try std.testing.expect(parse(a, &.{ "open", "--snapshot", "x", "--dependency", "d" }) == null);
    try std.testing.expectEqualStrings("r", parse(a, &.{ "upgrade", "--snapshot", "x", "--dependency", "d", "--expect", "f", "--replaces", "r" }).?.upgrade.replaces.?);
    try std.testing.expect(parse(a, &.{ "upgrade", "--snapshot", "x", "--upgrades", "[{\"source\":\"s\",\"replaces\":\"r\"}]", "--review", "f" }) != null);
    for ([_][]const u8{ "[]", "{}", "[{\"source\":\"s\"}]", "[{\"source\":\"s\",\"replaces\":\" \"}]", "[{\"source\":\"s\",\"source\":\"s\",\"replaces\":\"r\"}]", "[{\"source\":null,\"replaces\":\"r\"}]" }) |payload|
        try std.testing.expect(parse(a, &.{ "upgrade", "--snapshot", "x", "--upgrades", payload, "--review", "f" }) == null);
    try std.testing.expectEqualStrings("", parse(a, &.{ "upgrade", "--snapshot", "x", "--selection", "{\"sources\":[null],\"replaces\":[]}", "--review", "f" }).?.selection.sources[0]);
    for ([_][]const u8{ "[]", "{\"sources\":[1],\"replaces\":[]}", "{\"sources\":[],\"replaces\":[],\"x\":1}", "{\"sources\":\"s\",\"replaces\":[]}", "not json" }) |payload|
        try std.testing.expect(parse(a, &.{ "upgrade", "--snapshot", "x", "--selection", payload, "--review", "f" }) == null);
    try std.testing.expect(parse(a, &.{ "upgrade", "--snapshot", "x", "--selection", "{\"sources\":[],\"replaces\":[]}", "--review", " " }) == null);
}
