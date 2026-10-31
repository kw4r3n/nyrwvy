//! CLI startup only: plan dependency changes directly, then publish the validated pair.
const std = @import("std");
const host = @import("main.zig");
const model = @import("model_reader");
const records = @import("snapshot_reader");
const planning = @import("dependency_upgrade.zig");
const query = @import("query.zig");
const m = @import("materialize.zig");
const edit = @import("operation_model.zig");
const Allocator = std.mem.Allocator;
const Value = model.Value;
const Json = std.json.Value;
pub const usage = "Usage: Nyrwvy.AuthoringSession <open|create|pin> --snapshot <snapshot-path> [--dependency <snapshot-path> ...] [--allow-host-sources] (create may name several dependencies; pin takes at most one same-content source)\n       Nyrwvy.AuthoringSession open --snapshot <snapshot-path> --read-only [--allow-host-sources] (takes no lock, so it works while another session has the snapshot open; commit is refused)\n       Nyrwvy.AuthoringSession migrate --snapshot <snapshot-path> [--dependency <exact copy of a legacy pin's source>] [--to <new-path>] (rewrites the snapshot and its dependency sidecar to the current schema, prints the migration report and exits; no session is started; --to writes the result to a new path instead, in the form its extension selects (.nyrec for records), and leaves the source untouched)\n       Nyrwvy.AuthoringSession upgrade --snapshot <snapshot-path> --dependency <new-version-path> --expect <reviewed-fingerprint> [--replaces <snapshot-id>] [--allow-host-sources]\n       Nyrwvy.AuthoringSession upgrade --snapshot <snapshot-path> --upgrades <JSON array of {source,replaces}> --review <reviewFingerprint> [--allow-host-sources]\n       Nyrwvy.AuthoringSession upgrade --snapshot <snapshot-path> --selection <JSON {sources:[paths],replaces:[current SnapshotIds]}> --review <reviewFingerprint> [--allow-host-sources]\n\n       Nyrwvy.AuthoringSession bind-c --header <file.h> ... (proposes capabilities and a C# mapping table for C functions; run bind-c alone for its options)\n--allow-host-sources lets runPure/runPureByName compile and run hostSources C# with your permissions; it is off by default.";

const Mode = enum { open, read_only, create, pin, single, batch, selection };
const Command = struct { mode: Mode, path: []const u8, sources: []const []const u8 = &.{}, payload: []const u8 = "", fingerprint: []const u8 = "", replaces: ?[]const u8 = null };
fn same(x: []const u8, y: []const u8) bool {
    return std.mem.eql(u8, x, y);
}
fn blank(text: []const u8) bool {
    var it = std.unicode.Utf8View.initUnchecked(text).iterator();
    while (it.nextCodepoint()) |c| switch (c) {
        0x09...0x0D, 0x20, 0x85, 0xA0, 0x1680, 0x2000...0x200A, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000 => {},
        else => return false,
    };
    return true;
}
fn exact(root: Json, names: []const []const u8) bool {
    if (root != .object or root.object.count() != names.len) return false;
    for (names) |name| if (root.object.get(name) == null) return false;
    return true;
}
fn string(value: Json) bool {
    return value == .string or value == .null;
}
fn parse(a: Allocator, args: []const []const u8) !?Command {
    if (args.len < 3 or !same(args[1], "--snapshot")) return null;
    const path = args[2];
    if (same(args[0], "open")) {
        if (args.len == 3) return .{ .mode = .open, .path = path };
        if (args.len == 4 and same(args[3], "--read-only")) return .{ .mode = .read_only, .path = path };
    } else if (same(args[0], "create") or same(args[0], "pin")) {
        if ((args.len - 3) % 2 != 0 or (same(args[0], "pin") and args.len > 5)) return null;
        var sources: std.ArrayList([]const u8) = .empty;
        var at: usize = 3;
        while (at < args.len) : (at += 2) {
            if (!same(args[at], "--dependency")) return null;
            try sources.append(a, args[at + 1]);
        }
        return .{ .mode = if (same(args[0], "pin")) .pin else .create, .path = path, .sources = sources.items };
    } else if (same(args[0], "upgrade") and args.len >= 7) {
        if (same(args[3], "--dependency") and same(args[5], "--expect")) {
            if (args.len != 7 and !(args.len == 9 and same(args[7], "--replaces"))) return null;
            return .{ .mode = .single, .path = path, .sources = try a.dupe([]const u8, &.{args[4]}), .fingerprint = args[6], .replaces = if (args.len == 9) args[8] else null };
        }
        if (args.len != 7 or !same(args[5], "--review") or blank(args[6])) return null;
        const batch = same(args[3], "--upgrades");
        if (!batch and !same(args[3], "--selection")) return null;
        // JsonDocument preserves duplicate field names; exact member lists reject them.
        const root = std.json.parseFromSliceLeaky(Json, a, args[4], .{ .duplicate_field_behavior = .@"error" }) catch return null;
        if (batch) {
            if (root != .array or root.array.items.len == 0) return null;
            for (root.array.items) |item| {
                if (!exact(item, &.{ "source", "replaces" })) return null;
                for ([_][]const u8{ "source", "replaces" }) |name| {
                    const value = item.object.get(name).?;
                    if (value != .string or blank(value.string)) return null;
                }
            }
        } else {
            if (!exact(root, &.{ "sources", "replaces" })) return null;
            for ([_][]const u8{ "sources", "replaces" }) |name| {
                const values = root.object.get(name).?;
                if (values != .array) return null;
                for (values.array.items) |*value| {
                    if (!string(value.*)) return null;
                    if (value.* == .null) value.* = .{ .string = "" };
                }
            }
        }
        return .{ .mode = if (batch) .batch else .selection, .path = path, .payload = try std.json.Stringify.valueAlloc(a, root, .{}), .fingerprint = args[6] };
    }
    return null;
}
fn rejection(problem: query.Rejection) noreturn {
    host.startupFailure("{s}: {s}", .{ problem.code, problem.message });
}
fn failed(r: *query.Request) noreturn {
    rejection(r.rejection);
}
fn write(a: Allocator, value: Value) ![]const u8 {
    var out: std.Io.Writer.Allocating = .init(a);
    try m.write(&out.writer, value);
    return out.written();
}
fn encode(a: Allocator, path: []const u8, local: Value, dependency: ?host.Dependency) ![]const u8 {
    var document = local;
    if (dependency) |d| document = try edit.set(a, document, "dependency", try m.object(a, &.{ try m.member(a, "snapshotId", try m.string(a, d.id)), try m.member(a, "digest", try m.string(a, d.digest)) }));
    const written = try model.writtenText(a, try write(a, document));
    return if (std.mem.endsWith(u8, path, ".nyrec")) try records.recordsOf(a, written.text) else written.text;
}
fn createAtomic(a: Allocator, path: []const u8, content: []const u8) !void {
    switch (try @import("view_file.zig").createAtomic(a, path, content)) {
        .written => {},
        .refused => |message| host.startupFailure("IoFailure: {s}", .{message}),
        .internal => unreachable,
    }
}
fn create(io: std.Io, a: Allocator, command: Command) !void {
    const path = try std.fs.path.resolveAlloc(a, &.{ try std.process.currentPathAlloc(io, a), command.path });
    host.snapshot_path = path;
    host.writable = true;
    std.Io.Dir.cwd().createDirPath(io, std.fs.path.dirname(path) orelse "/") catch |err| host.startupFailure("IoFailure: {s}", .{@errorName(err)});
    const sidecar = try std.mem.concat(a, u8, &.{ path, ".dependency.json" });
    if (fileExists(io, path) or fileExists(io, sidecar)) alreadyExists(path);
    try host.acquireLock(io, a, path);
    if (fileExists(io, path) or fileExists(io, sidecar)) alreadyExists(path);
    var request: query.Request = .{ .a = a, .line = "{}", .root = .{}, .model = .json_null, .provenance = null, .snapshot_path = path };
    var pinned: std.ArrayList(planning.Pinned) = .empty;
    for (command.sources) |source| try pinned.append(a, planning.pin(&request, source) catch failed(&request));
    for (pinned.items, 0..) |item, i| for (pinned.items[0..i]) |previous| if (same(item.source, previous.source)) host.startupFailure("DependencyFailure: Each dependency may be selected only once.", .{});
    var selected: ?host.Dependency = null;
    var manifest: ?[]const u8 = null;
    if (pinned.items.len > 0) {
        const combined = planning.combine(&request, pinned.items) catch failed(&request);
        selected = combined[0];
        manifest = combined[1];
    }
    const id = try @import("ids.zig").next();
    const local = try model.parseValue(a, try std.fmt.allocPrint(a, "{{\"schemaVersion\":{d},\"snapshotId\":\"{s}\",\"types\":[],\"effects\":[],\"capabilities\":[],\"functions\":[],\"obligations\":[]}}", .{ model.currentVersion(), id }));
    const source = try encode(a, path, local, selected);
    const read = host.readSource(a, std.mem.endsWith(u8, path, ".nyrec"), source, selected) catch host.startupFailure("{s}", .{host.failure_line});
    if (manifest) |content| try createAtomic(a, sidecar, content);
    switch (try @import("view_file.zig").createAtomic(a, path, source)) {
        .written => {},
        .refused => |message| {
            if (manifest != null and !fileExists(io, path)) std.Io.Dir.cwd().deleteFile(io, sidecar) catch {};
            host.startupFailure("IoFailure: {s}", .{message});
        },
        .internal => unreachable,
    }
    const backend = try std.mem.concat(a, u8, &.{ path, ".backends.json" });
    if (!fileExists(io, backend)) _ = @import("view_file.zig").createAtomic(a, backend, "{\"targets\": [\"csharp\"]}\n") catch {};
    host.selection = selected;
    host.dependency_manifest = manifest;
    host.persisted_source = source;
    host.install(a, read);
}
fn alreadyExists(path: []const u8) noreturn {
    host.startupFailure("SnapshotFileAlreadyExists: A snapshot already exists at {s}. Use open to resume it; create never overwrites.", .{path});
}
fn fileExists(io: std.Io, path: []const u8) bool {
    return if (std.Io.Dir.cwd().statFile(io, path, .{})) |stat| stat.kind != .directory else |_| false;
}

fn verifyPinPersisted(a: Allocator, sidecar: []const u8) !?query.Rejection {
    const source = host.readText(a, host.snapshot_path) catch |err| return .{ .code = "IoFailure", .message = @errorName(err) };
    const manifest = host.readText(a, sidecar) catch |err| return .{ .code = "IoFailure", .message = @errorName(err) };
    if (!same(source, host.persisted_source) or !same(manifest, host.dependency_manifest.?)) return .{ .code = "ExternalModification", .message = "The snapshot file changed outside this session." };
    return null;
}

pub fn persistPin(a: Allocator, source_override: ?[]const u8) !void {
    const selected = host.selection orelse host.startupFailure("DependencyFailure: This workspace has no dependency selection to pin.", .{});
    if (selected.provenance.members[0].embedded and source_override == null) return;
    const path = selected.provenance.members[0].path;
    const json = (try model.writtenText(a, selected.canonical)).text;
    const record = std.mem.endsWith(u8, path, ".nyrec");
    const next = try write(a, try m.object(a, &.{ try m.member(a, "fingerprint", try m.string(a, selected.fingerprint)), try m.member(a, "path", try m.string(a, path)), try m.member(a, "snapshotId", try m.string(a, selected.id)), try m.member(a, if (record) "snapshotRecords" else "snapshotJson", try m.string(a, if (record) try records.recordsOf(a, json) else json)) }));
    // A pin may recover a missing legacy authority from an exact copy; verify the bytes, not that old path.
    const sidecar = try std.mem.concat(a, u8, &.{ host.snapshot_path, ".dependency.json" });
    if (try verifyPinPersisted(a, sidecar)) |problem| rejection(problem);
    if (try host.replaceAtomically(a, sidecar, next)) |problem| rejection(problem);
    host.dependency_manifest = next;
    host.selection = host.decodeDependency(a, false, json, path, true) catch host.startupFailure("{s}", .{host.failure_line});
}
fn upgrade(io: std.Io, a: Allocator, command: Command) !void {
    try host.openUsing(io, a, command.path, false, false, null, false);
    if (command.mode != .selection and host.selection == null) host.startupFailure("DependencyFailure: This workspace has no dependency to upgrade; add one with a selection whose replaces is empty.", .{});
    const line = if (command.mode == .single) "{\"op\":\"previewDependencyUpgrade\"}" else try std.fmt.allocPrint(a, "{{\"op\":\"previewDependencyUpgrades\",\"{s}\":{s}}}", .{ if (command.mode == .batch) @as([]const u8, "upgrades") else "selection", command.payload });
    var request = try host.startupRequest(a, line);
    const plan = if (command.mode == .single) planning.planSingle(&request, command.sources[0], command.replaces) catch failed(&request) else planning.planChange(&request) catch failed(&request);
    if (!same(plan.fingerprint, command.fingerprint)) host.startupFailure("DependencyFailure: {s}", .{switch (command.mode) {
        .single => @as([]const u8, "The source differs from the reviewed upgrade version."),
        .batch => "The workspace, current pins, targets or new selected content differ from the reviewed batch. Preview the dependency upgrades again.",
        else => "The workspace, current pins, targets or new selected content differ from the reviewed selection. Preview the dependency upgrades again.",
    }});
    const issues = model.property(plan.preview, "issues").?.array;
    if (issues.len > 0) {
        const first = issues[0];
        const tail = if (issues.len == 1) "" else try std.fmt.allocPrint(a, " ({d} issues; see details)", .{issues.len});
        host.startupFailure("WorkspaceValidationFailure: The workspace graph is invalid: {s}: {s}{s}", .{ model.utf8(model.decoded(model.property(first, "code").?)), model.utf8(model.decoded(model.property(first, "message").?)), tail });
    }
    const source = try encode(a, host.snapshot_path, plan.local, plan.dependency);
    const read = host.readSource(a, std.mem.endsWith(u8, host.snapshot_path, ".nyrec"), source, plan.dependency) catch host.startupFailure("{s}", .{host.failure_line});
    if (host.selection != null) {
        if (try host.replaceDependencyPair(a, source, plan.manifest)) |problem| rejection(problem);
    } else {
        if (try host.verifyPersisted(a)) |problem| rejection(problem);
        const sidecar = try std.mem.concat(a, u8, &.{ host.snapshot_path, ".dependency.json" });
        if (fileExists(io, sidecar)) rejection(.{ .code = "ExternalModification", .message = "The snapshot file changed outside this session." });
        try createAtomic(a, sidecar, plan.manifest);
        if (try host.replaceAtomically(a, host.snapshot_path, source)) |problem| {
            std.Io.Dir.cwd().deleteFile(io, sidecar) catch {};
            rejection(problem);
        }
    }
    host.selection = plan.dependency;
    host.dependency_manifest = plan.manifest;
    host.persisted_source = source;
    host.install(a, read);
}
pub fn run(io: std.Io, a: Allocator, args: []const []const u8) !bool {
    model.beginRequest(a);
    const command = (try parse(a, args)) orelse return false;
    switch (command.mode) {
        .open, .read_only => try host.open(io, a, command.path, command.mode == .read_only),
        .pin => try host.openUsing(io, a, command.path, false, true, if (command.sources.len == 1) command.sources[0] else null, false),
        .create => try create(io, a, command),
        .single, .batch, .selection => try upgrade(io, a, command),
    }
    return true;
}

test "startup accepts only exact CLI forms and payloads" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    try std.testing.expect((try parse(a, &.{ "create", "--snapshot", "x", "--dependency", "one", "--dependency", "two" })) != null);
    try std.testing.expect((try parse(a, &.{ "pin", "--snapshot", "x", "--dependency", "one", "--dependency", "two" })) == null);
    for ([_][]const u8{ "[]", "[{\"source\":\"x\",\"replaces\":\"a\",\"extra\":0}]", "[{\"source\":\"x\",\"source\":\"y\",\"replaces\":\"a\"}]", "[{\"source\":null,\"replaces\":\"a\"}]" }) |payload| try std.testing.expect((try parse(a, &.{ "upgrade", "--snapshot", "x", "--upgrades", payload, "--review", "f" })) == null);
    try std.testing.expect((try parse(a, &.{ "upgrade", "--snapshot", "x", "--selection", "{\"sources\":[],\"replaces\":[]}", "--review", "f" })) != null);
}

test "pin final reread reports snapshot and manifest I/O failures without publication" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    var temporary = std.testing.tmpDir(.{});
    defer temporary.cleanup();
    const saved_path = host.snapshot_path;
    const saved_io = host.host_io;
    const saved_source = host.persisted_source;
    const saved_manifest = host.dependency_manifest;
    defer {
        host.snapshot_path = saved_path;
        host.host_io = saved_io;
        host.persisted_source = saved_source;
        host.dependency_manifest = saved_manifest;
    }
    host.host_io = std.testing.io;
    const directory = try temporary.dir.realPathFileAlloc(host.host_io, ".", a);
    host.snapshot_path = try std.fs.path.join(a, &.{ directory, "app.json" });
    host.persisted_source = "snapshot";
    host.dependency_manifest = "manifest";
    const sidecar = try std.mem.concat(a, u8, &.{ host.snapshot_path, ".dependency.json" });
    try std.testing.expectEqualStrings("IoFailure", (try verifyPinPersisted(a, sidecar)).?.code);
    try std.testing.expect((try host.replaceAtomically(a, host.snapshot_path, host.persisted_source)) == null);
    try std.testing.expectEqualStrings("IoFailure", (try verifyPinPersisted(a, sidecar)).?.code);
    try std.testing.expectEqualStrings("snapshot", try host.readText(a, host.snapshot_path));
    try std.testing.expectError(error.FileNotFound, host.readText(a, sidecar));
}
