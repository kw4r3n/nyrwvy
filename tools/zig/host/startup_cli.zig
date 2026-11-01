//! Startup forms share dependency planning and durable publication with the JSONL host.
const std = @import("std");
const host = @import("main.zig");
const model = @import("model_reader");
const records = @import("snapshot_reader");
const query = @import("query.zig");
const m = @import("materialize.zig");
const edit = @import("operation_model.zig");
const planning = @import("dependency_upgrade.zig");
const files = @import("view_file.zig");
const A = std.mem.Allocator;
const Json = std.json.Value;
const Value = model.Value;

pub const usage = "Usage: Nyrwvy.AuthoringSession <open|create|pin> --snapshot <snapshot-path> [--dependency <snapshot-path> ...] [--allow-host-sources] (create may name several dependencies; pin takes at most one same-content source)\n       Nyrwvy.AuthoringSession open --snapshot <snapshot-path> --read-only [--allow-host-sources] (takes no lock, so it works while another session has the snapshot open; commit is refused)\n       Nyrwvy.AuthoringSession migrate --snapshot <snapshot-path> [--dependency <exact copy of a legacy pin's source>] [--to <new-path>] (rewrites the snapshot and its dependency sidecar to the current schema, prints the migration report and exits; no session is started; --to writes the result to a new path instead, in the form its extension selects (.nyrec for records), and leaves the source untouched)\n       Nyrwvy.AuthoringSession upgrade --snapshot <snapshot-path> --dependency <new-version-path> --expect <reviewed-fingerprint> [--replaces <snapshot-id>] [--allow-host-sources]\n       Nyrwvy.AuthoringSession upgrade --snapshot <snapshot-path> --upgrades <JSON array of {source,replaces}> --review <reviewFingerprint> [--allow-host-sources]\n       Nyrwvy.AuthoringSession upgrade --snapshot <snapshot-path> --selection <JSON {sources:[paths],replaces:[current SnapshotIds]}> --review <reviewFingerprint> [--allow-host-sources]\n\n       Nyrwvy.AuthoringSession bind-c --header <file.h> ... (proposes capabilities and a C# mapping table for C functions; run bind-c alone for its options)\n--allow-host-sources lets runPure/runPureByName compile and run hostSources C# with your permissions; it is off by default.";

pub const Mode = enum { open, read_only, create, pin, single, upgrades, selection };
pub const Command = struct { mode: Mode, path: []const u8, sources: []const []const u8 = &.{}, payload: Json = .null, fingerprint: []const u8 = "", replaces: ?[]const u8 = null };

fn same(a: []const u8, b: []const u8) bool {
    return std.mem.eql(u8, a, b);
}
fn blank(source: []const u8) bool {
    var it = std.unicode.Utf8View.initUnchecked(source).iterator();
    while (it.nextCodepoint()) |c| switch (c) {
        0x09...0x0D, 0x20, 0x85, 0xA0, 0x1680, 0x2000...0x200A, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000 => {},
        else => return false,
    };
    return true;
}
fn nullableString(value: Json) bool {
    return value == .string or value == .null;
}

pub fn parse(a: A, args: []const []const u8) !?Command {
    if (args.len < 3 or !same(args[1], "--snapshot")) return null;
    var command: Command = .{ .mode = .open, .path = args[2] };
    if (same(args[0], "open")) {
        if (args.len == 3) return command;
        if (args.len == 4 and same(args[3], "--read-only")) {
            command.mode = .read_only;
            return command;
        }
    } else if (same(args[0], "create") or same(args[0], "pin")) {
        command.mode = if (same(args[0], "create")) .create else .pin;
        if ((args.len - 3) % 2 != 0 or (command.mode == .pin and args.len > 5)) return null;
        var sources: std.ArrayList([]const u8) = .empty;
        var i: usize = 3;
        while (i < args.len) : (i += 2) {
            if (!same(args[i], "--dependency")) return null;
            try sources.append(a, args[i + 1]);
        }
        command.sources = sources.items;
        return command;
    } else if (same(args[0], "upgrade") and (args.len == 7 or args.len == 9)) {
        if (same(args[3], "--dependency") and same(args[5], "--expect") and (args.len == 7 or same(args[7], "--replaces"))) {
            command.mode = .single;
            command.sources = try a.dupe([]const u8, args[4..5]);
            command.fingerprint = args[6];
            if (args.len == 9) command.replaces = args[8];
            return command;
        }
        if (args.len != 7 or !same(args[5], "--review") or blank(args[6])) return null;
        const payload = std.json.parseFromSliceLeaky(Json, a, args[4], .{ .duplicate_field_behavior = .@"error" }) catch return null;
        if (same(args[3], "--upgrades")) {
            if (payload != .array or payload.array.items.len == 0) return null;
            for (payload.array.items) |item| {
                if (item != .object or item.object.count() != 2) return null;
                for ([_][]const u8{ "source", "replaces" }) |key| {
                    const value = item.object.get(key) orelse return null;
                    if (value != .string or blank(value.string)) return null;
                }
            }
            command.mode = .upgrades;
        } else if (same(args[3], "--selection")) {
            if (payload != .object or payload.object.count() != 2) return null;
            for ([_][]const u8{ "sources", "replaces" }) |key| {
                const value = payload.object.get(key) orelse return null;
                if (value != .array) return null;
                for (value.array.items) |item| if (!nullableString(item)) return null;
            }
            command.mode = .selection;
        } else return null;
        command.payload = payload;
        command.fingerprint = args[6];
        return command;
    }
    return null;
}

fn fail(a: A, code: []const u8, message: []const u8) error{Startup} {
    host.failure_line = std.fmt.allocPrint(a, "{s}: {s}", .{ code, message }) catch "out of memory";
    return error.Startup;
}
fn rejected(a: A, r: query.Request) error{Startup} {
    return fail(a, r.rejection.code, r.rejection.message);
}
fn request(a: A, path: []const u8) query.Request {
    return .{ .a = a, .line = "{}", .root = .{}, .model = .json_null, .provenance = null, .snapshot_path = path };
}
fn text(a: A, value: Value) ![]const u8 {
    var out: std.Io.Writer.Allocating = .init(a);
    try m.write(&out.writer, value);
    return out.written();
}
fn createAtomic(a: A, path: []const u8, content: []const u8) !void {
    switch (try files.createAtomic(a, path, content)) {
        .written => {},
        .refused => |message| return fail(a, "IoFailure", message),
        .internal => unreachable,
    }
}
fn publishSource(a: A, path: []const u8, root: Value, selected: ?host.Dependency) ![]const u8 {
    var persisted = root;
    if (selected) |dep| {
        for ([_][]const u8{ "types", "effects", "capabilities", "functions", "obligations" }) |name| {
            var locals: std.ArrayList(Value) = .empty;
            for (model.arrayItems(root, name)) |item| if (edit.find(dep.root, name, query.idOf(item)) == null) try locals.append(a, item);
            persisted = try edit.set(a, persisted, name, .{ .array = locals.items });
        }
        persisted = try edit.set(a, persisted, "dependency", try m.object(a, &.{ try m.member(a, "snapshotId", try m.string(a, dep.id)), try m.member(a, "digest", try m.string(a, dep.digest)) }));
    }
    const encoded = (try model.writtenText(a, try text(a, persisted))).text;
    return if (std.mem.endsWith(u8, path, ".nyrec")) try records.recordsOf(a, encoded) else encoded;
}

fn fileExists(io: std.Io, path: []const u8) bool {
    const stat = std.Io.Dir.cwd().statFile(io, path, .{}) catch return false;
    return stat.kind == .file;
}

fn create(io: std.Io, a: A, command: Command) !void {
    const path = try std.fs.path.resolveAlloc(a, &.{ try std.process.currentPathAlloc(io, a), command.path });
    try std.Io.Dir.cwd().createDirPath(io, std.fs.path.dirname(path) orelse "/");
    const sidecar = try std.mem.concat(a, u8, &.{ path, ".dependency.json" });
    if (fileExists(io, path) or fileExists(io, sidecar)) return fail(a, "SnapshotFileAlreadyExists", try std.fmt.allocPrint(a, "A snapshot already exists at {s}. Use open to resume it; create never overwrites.", .{path}));
    try host.acquireSnapshotLock(io, a, path, false);
    if (fileExists(io, path) or fileExists(io, sidecar)) return fail(a, "SnapshotFileAlreadyExists", try std.fmt.allocPrint(a, "A snapshot already exists at {s}. Use open to resume it; create never overwrites.", .{path}));
    var r = request(a, path);
    var pins: std.ArrayList(planning.Pinned) = .empty;
    for (command.sources) |source| try pins.append(a, planning.pin(&r, source) catch |err| switch (err) {
        error.Rejected => return rejected(a, r),
        else => return err,
    });
    for (pins.items, 0..) |pin, i| for (pins.items[0..i]) |previous| if (same(pin.source, previous.source)) return fail(a, "DependencyFailure", "Each dependency may be selected only once.");
    const combined = if (pins.items.len > 0) planning.combine(&r, pins.items) catch |err| switch (err) {
        error.Rejected => return rejected(a, r),
        else => return err,
    } else null;
    const id = try @import("ids.zig").next();
    var fields: std.ArrayList(model.Member) = .empty;
    try fields.append(a, try m.member(a, "schemaVersion", try m.number(a, model.currentVersion())));
    try fields.append(a, try m.member(a, "snapshotId", try m.string(a, &id)));
    for ([_][]const u8{ "types", "effects", "capabilities", "functions", "obligations" }) |name| try fields.append(a, try m.member(a, name, .{ .array = &.{} }));
    var root: Value = .{ .object = fields.items };
    if (combined) |pair| for ([_][]const u8{ "types", "effects", "capabilities", "functions", "obligations" }) |name| {
        root = try edit.set(a, root, name, .{ .array = model.arrayItems(pair[0].root, name) });
    };
    const selected: ?host.Dependency = if (combined) |pair| pair[0] else null;
    const source = try publishSource(a, path, root, selected);
    const read = try host.readSource(a, std.mem.endsWith(u8, path, ".nyrec"), source, selected);
    if (combined) |pair| try createAtomic(a, sidecar, pair[1]);
    createAtomic(a, path, source) catch |err| {
        if (combined != null and !fileExists(io, path)) std.Io.Dir.cwd().deleteFile(io, sidecar) catch {};
        return err;
    };
    const backends = try std.mem.concat(a, u8, &.{ path, ".backends.json" });
    if (!fileExists(io, backends)) createAtomic(a, backends, "{\"targets\": [\"csharp\"]}\n") catch {};
    host.dependency_manifest = if (combined) |pair| pair[1] else null;
    host.install(a, path, source, read, selected);
}

pub fn persistPin(a: A, path: []const u8, source_override: ?[]const u8) !void {
    const selected = host.selection orelse return fail(a, "DependencyFailure", "This workspace has no dependency selection to pin.");
    if (selected.provenance.members[0].embedded and source_override == null) return;
    const provenance = selected.provenance.members[0];
    var pinned = selected;
    const members = try a.dupe(@TypeOf(provenance), selected.provenance.members);
    members[0].embedded = true;
    pinned.provenance.members = members;
    const is_record = std.mem.endsWith(u8, provenance.path, ".nyrec");
    const json = (try model.writtenText(a, selected.canonical)).text;
    const manifest = try text(a, try m.object(a, &.{
        try m.member(a, "fingerprint", try m.string(a, selected.fingerprint)),
        try m.member(a, "path", try m.string(a, provenance.path)),
        try m.member(a, "snapshotId", try m.string(a, selected.id)),
        try m.member(a, if (is_record) "snapshotRecords" else "snapshotJson", try m.string(a, if (is_record) try records.recordsOf(a, json) else json)),
    }));
    const sidecar = try std.mem.concat(a, u8, &.{ path, ".dependency.json" });
    if (!same(try host.readText(a, path), host.persisted_source) or !same(try host.readText(a, sidecar), host.dependency_manifest.?)) return fail(a, "ExternalModification", "The snapshot file changed outside this session.");
    if (try host.replaceAtomically(a, sidecar, manifest)) |problem| return fail(a, problem.code, problem.message);
    host.dependency_manifest = manifest;
    host.selection = pinned;
}

pub fn start(io: std.Io, a: A, command: Command) !void {
    if (command.mode == .create) return create(io, a, command);
    const upgrading = command.mode == .single or command.mode == .upgrades or command.mode == .selection;
    try host.open(io, a, command.path, command.mode == .read_only, command.mode == .pin, if (command.sources.len > 0 and command.mode == .pin) command.sources[0] else null, !upgrading);
    if (!upgrading) return;
    const path = try std.fs.path.resolveAlloc(a, &.{ try std.process.currentPathAlloc(io, a), command.path });
    var r = request(a, path);
    try r.root.put(a, "op", .{ .string = "previewDependencyUpgrades" });
    const current = host.selection;
    if ((command.mode == .single or command.mode == .upgrades) and current == null) return fail(a, "DependencyFailure", "This workspace has no dependency to upgrade; add one with a selection whose replaces is empty.");
    if (command.mode == .single) {
        const members = current.?.provenance.members;
        if (command.replaces == null and members.len != 1) return fail(a, "DependencyFailure", "Name the dependency to replace by its SnapshotId when the workspace has several.");
        const target = command.replaces orelse members[0].id;
        const payload = try std.fmt.allocPrint(a, "[{{\"source\":{s},\"replaces\":{s}}}]", .{ try std.json.Stringify.valueAlloc(a, command.sources[0], .{}), try std.json.Stringify.valueAlloc(a, target, .{}) });
        try r.root.put(a, "upgrades", try std.json.parseFromSliceLeaky(Json, a, payload, .{}));
    } else try r.root.put(a, if (command.mode == .upgrades) "upgrades" else "selection", command.payload);
    if (command.mode == .selection) {
        if (command.payload.object.get("sources").?.array.items.len == 0) return fail(a, "DependencyFailure", "A selection change needs at least one source.");
        for ([_][]const u8{ "sources", "replaces" }) |name| for (command.payload.object.get(name).?.array.items) |item| {
            if (item == .null or blank(item.string)) return fail(a, "DependencyFailure", "Selection upgrade sources and targets must be non-blank.");
        };
    }
    const plan = planning.planChange(&r) catch |err| switch (err) {
        error.Rejected => return rejected(a, r),
        else => return err,
    };
    if (command.mode == .single) {
        const fingerprint = model.utf8(model.decoded(model.property(model.property(plan.preview, "selected").?.array[0], "fingerprint").?));
        if (!same(fingerprint, command.fingerprint)) return fail(a, "DependencyFailure", "The source differs from the reviewed upgrade version.");
    } else if (!same(plan.fingerprint, command.fingerprint)) return fail(a, "DependencyFailure", if (command.mode == .upgrades) "The workspace, current pins, targets or new selected content differ from the reviewed batch. Preview the dependency upgrades again." else "The workspace, current pins, targets or new selected content differ from the reviewed selection. Preview the dependency upgrades again.");
    const issues = model.property(plan.preview, "issues").?.array;
    if (issues.len > 0) {
        const first = issues[0];
        const message = try std.fmt.allocPrint(a, "The workspace graph is invalid: {s}: {s}{s}", .{ model.utf8(model.decoded(model.property(first, "code").?)), model.utf8(model.decoded(model.property(first, "message").?)), if (issues.len == 1) "" else try std.fmt.allocPrint(a, " ({d} issues; see details)", .{issues.len}) });
        return fail(a, "WorkspaceValidationFailure", message);
    }
    const source = try publishSource(a, path, plan.candidate, plan.dependency);
    const read = try host.readSource(a, std.mem.endsWith(u8, path, ".nyrec"), source, plan.dependency);
    if (current != null) {
        if (try host.replaceDependencyPair(a, source, plan.manifest)) |problem| return fail(a, problem.code, problem.message);
    } else {
        const sidecar = try std.mem.concat(a, u8, &.{ path, ".dependency.json" });
        if (!same(try host.readText(a, path), host.persisted_source) or fileExists(io, sidecar)) return fail(a, "ExternalModification", "The snapshot file changed outside this session.");
        try createAtomic(a, sidecar, plan.manifest);
        const problem = host.replaceAtomically(a, path, source) catch |err| {
            std.Io.Dir.cwd().deleteFile(io, sidecar) catch {};
            return err;
        };
        if (problem) |failure| {
            std.Io.Dir.cwd().deleteFile(io, sidecar) catch {};
            return fail(a, failure.code, failure.message);
        }
    }
    host.dependency_manifest = plan.manifest;
    host.install(a, path, source, read, plan.dependency);
}

test "startup parser preserves positional CLI forms" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    try std.testing.expectEqual(Mode.create, (try parse(a, &.{ "create", "--snapshot", "a", "--dependency", "b", "--dependency", "c" })).?.mode);
    try std.testing.expect((try parse(a, &.{ "open", "--snapshot", "a", "--dependency", "b" })) == null);
    try std.testing.expect((try parse(a, &.{ "pin", "--snapshot", "a", "--dependency", "b", "--dependency", "c" })) == null);
    try std.testing.expect((try parse(a, &.{ "upgrade", "--snapshot", "a", "--upgrades", "[]", "--review", "x" })) == null);
}
