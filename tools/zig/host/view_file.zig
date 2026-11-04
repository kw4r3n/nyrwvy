//! Writing a viewer page (`ViewerFile.write` and `ConfinedDirectory`): the output resolves against
//! the snapshot's directory and must stay inside it, never passes through a link, replaces only a
//! page this tool wrote, and is written through directory handles below that directory and renamed
//! into place, so a component swapped for a link after the checks cannot carry the write elsewhere.
const std = @import("std");
const windows = @import("builtin").os.tag == .windows;
const separators = if (windows) "/\\" else "/";

fn getIo() std.Io {
    return if (@import("builtin").is_test) std.testing.io else @import("main.zig").host_io;
}

const Allocator = std.mem.Allocator;

/// Default directory, beside the snapshot, for views written without an explicit output path.
pub const default_directory_name = "nyrwvy-views";

/// The path written, or why nothing was (the `ViewWriteFailed` message).
pub const Outcome = union(enum) {
    written: []const u8,
    refused: []const u8,
    /// A NUL in the output path: .NET's `Path.GetFullPath` throws, which the F# host reports as an internal failure.
    internal,
};

fn endsWithIgnoringCase(text: []const u8, suffix: []const u8) bool {
    return text.len >= suffix.len and std.ascii.eqlIgnoreCase(text[text.len - suffix.len ..], suffix);
}

/// `isInside`: the relative path from `directory` is neither rooted nor above it, nor the
/// directory itself (F# accepts that and then fails the write with an I/O error).
fn isInside(a: Allocator, directory: []const u8, path: []const u8) !bool {
    const relative = try std.fs.path.relativeAlloc(a, directory, null, directory, path);
    return relative.len > 0 and !std.fs.path.isAbsolute(relative) and !std.mem.eql(u8, relative, "..") and !(relative.len > 2 and std.mem.startsWith(u8, relative, "..") and std.mem.indexOfScalar(u8, separators, relative[2]) != null);
}

fn isLink(_: Allocator, path: []const u8) !bool {
    var buffer: [std.fs.max_path_bytes]u8 = undefined;
    return if (std.Io.Dir.cwd().readLink(getIo(), path, &buffer)) |_| true else |_| false;
}

/// `linkedComponent`: the first existing component below `directory` on the way to `path`, the file
/// included, that is a symbolic link.
pub fn linkedComponent(a: Allocator, directory: []const u8, path: []const u8) !?[]const u8 {
    const relative = try std.fs.path.relativeAlloc(a, directory, null, directory, path);
    var current: []const u8 = directory;
    var parts = std.mem.tokenizeAny(u8, relative, separators);
    while (parts.next()) |part| {
        current = try std.fs.path.join(a, &.{ current, part });
        if (try isLink(a, current)) return current;
    }
    return null;
}

/// `replaceable`: `path` is not an existing file (`File.Exists` is false for a directory), or it
/// starts with `header`.
fn replaceable(a: Allocator, path: []const u8, header: []const u8) !bool {
    const io = getIo();
    const file = std.Io.Dir.cwd().openFile(io, path, .{}) catch |err| switch (err) {
        error.FileNotFound, error.NotDir => return true,
        else => return false,
    };
    defer file.close(io);
    if ((try file.stat(io)).kind == .directory) return true;
    const buffer = try a.alloc(u8, header.len);
    var filled: usize = 0;
    while (filled < buffer.len) {
        const count = file.readStreaming(io, &.{buffer[filled..]}) catch |err| switch (err) {
            error.EndOfStream => break,
            else => return false,
        };
        if (count == 0) break;
        filled += count;
    }
    return filled == header.len and std.mem.eql(u8, buffer, header);
}

fn failure(a: Allocator, err: anyerror, operation: []const u8, name: []const u8) !Outcome {
    if (err == error.SymLinkLoop or err == error.NotDir) return .{ .refused = try std.fmt.allocPrint(a, "output must not pass through a symbolic link or reparse point; {s} is one or is not a directory.", .{name}) };
    return .{ .refused = try std.fmt.allocPrint(a, "I/O error: {s} {s} failed: {s}", .{ operation, name, @errorName(err) }) };
}

/// Every descendant is opened relative to the retained parent handle and refuses links.
fn writeConfined(a: Allocator, directory: []const u8, parents: []const []const u8, name: []const u8, content: []const u8, snapshot: bool, exclusive: bool) !Outcome {
    const io = getIo();
    var current = std.Io.Dir.cwd().openDir(io, directory, .{}) catch |err| return failure(a, err, "open", directory);
    defer current.close(io);
    for (parents) |part| {
        const next = current.openDir(io, part, .{ .follow_symlinks = false }) catch |err| blk: {
            if (err != error.FileNotFound) return failure(a, err, "open", part);
            current.createDir(io, part, .default_dir) catch |create_err| switch (create_err) {
                error.PathAlreadyExists => {},
                else => return failure(a, create_err, "create", part),
            };
            break :blk current.openDir(io, part, .{ .follow_symlinks = false }) catch |open_err| return failure(a, open_err, "open", part);
        };
        // Windows opens the reparse point itself; reject it before using it as a parent.
        const stat = next.stat(io) catch |err| {
            next.close(io);
            return failure(a, err, "stat", part);
        };
        if (stat.kind != .directory) {
            next.close(io);
            return failure(a, error.NotDir, "open", part);
        }
        current.close(io);
        current = next;
    }
    var random: [16]u8 = undefined;
    io.randomSecure(&random) catch return .{ .refused = "I/O error: no random bytes for a temporary name." };
    const temporary = if (snapshot) try std.fmt.allocPrint(a, ".{s}.{x}.tmp", .{ name, random }) else try std.fmt.allocPrint(a, ".nyrwvy-{x}.tmp", .{random});
    // A replaced regular file keeps its mode (a user's chmod 600), which a default-mode temporary would reset; a symlink's own mode is not copied.
    const kept: ?std.Io.File.Permissions = if (windows) null else if (current.statFile(io, name, .{ .follow_symlinks = false })) |stat| (if (stat.kind == .file) std.Io.File.Permissions.fromMode(stat.permissions.toMode() & 0o7777) else null) else |_| null;
    const file = current.createFile(io, temporary, .{ .exclusive = true, .permissions = kept orelse .default_file }) catch |err| return failure(a, err, "create", temporary);
    var renamed = false;
    defer if (!renamed) {
        current.deleteFile(io, temporary) catch {};
    };
    {
        defer file.close(io);
        // createFile applies the umask; set the kept mode exactly.
        if (kept) |permissions| file.setPermissions(io, permissions) catch |err| return failure(a, err, "chmod", temporary);
        file.writeStreamingAll(io, content) catch |err| return failure(a, err, "write", temporary);
        file.sync(io) catch |err| return failure(a, err, "sync", temporary);
    }
    if (exclusive) {
        current.renamePreserve(temporary, current, name, io) catch |err| return failure(a, err, "rename", name);
    } else current.rename(temporary, current, name, io) catch |err| return failure(a, err, "rename", name);
    renamed = true;
    // The rename survives a crash only once the directory is synced (Windows has no directory fsync).
    // Best effort: the snapshot is already replaced, so a sync failure must not report it unwritten.
    // `current` is an O_PATH handle on Linux, which fsync rejects, so sync a readable handle to it.
    if (snapshot and !windows) if (current.openDir(io, ".", .{ .iterate = true })) |readable| {
        defer readable.close(io);
        (std.Io.File{ .handle = readable.handle, .flags = .{ .nonblocking = false } }).sync(io) catch {};
    } else |_| {};
    return .{ .written = "" };
}

const Identity = struct { device: u64, inode: u64 };
fn identity(io: std.Io, file: std.Io.File) !Identity {
    const os = @import("builtin").os.tag;
    if (os == .windows) {
        const w = std.os.windows;
        var status: w.IO_STATUS_BLOCK = undefined;
        var volume: w.FILE.FS_VOLUME_INFORMATION = undefined;
        while (true) switch (w.ntdll.NtQueryVolumeInformationFile(file.handle, &status, &volume, @sizeOf(@TypeOf(volume)), .Volume)) {
            .SUCCESS, .BUFFER_OVERFLOW => return .{ .device = volume.VolumeSerialNumber, .inode = @bitCast((try file.stat(io)).inode) },
            .CANCELLED => try io.checkCancel(),
            else => return error.FileIdentityUnavailable,
        };
    } else if (os == .linux) {
        const linux = std.os.linux;
        var stat: linux.Statx = undefined;
        while (true) switch (linux.errno(linux.statx(file.handle, "", linux.AT.EMPTY_PATH, .{ .INO = true }, &stat))) {
            .SUCCESS => {
                if (!stat.mask.INO) return error.FileIdentityUnavailable;
                return .{ .device = @as(u64, stat.dev_major) << 32 | stat.dev_minor, .inode = stat.ino };
            },
            .INTR => try io.checkCancel(),
            else => return error.FileIdentityUnavailable,
        };
    } else {
        var stat: std.c.Stat = undefined;
        while (true) switch (std.posix.errno(std.c.fstat(file.handle, &stat))) {
            .SUCCESS => return .{ .device = @as(u32, @bitCast(stat.dev)), .inode = stat.ino },
            .INTR => try io.checkCancel(),
            else => return error.FileIdentityUnavailable,
        };
    }
}
/// Filesystem identity catches Unicode/case/normalization aliases without guessing its collation.
pub fn sameFile(first: []const u8, second: []const u8) !bool {
    const io = getIo();
    const left = std.Io.Dir.cwd().openFile(io, first, .{ .follow_symlinks = false, .allow_directory = false }) catch |err| switch (err) {
        error.FileNotFound, error.NotDir => return false,
        else => return err,
    };
    defer left.close(io);
    const right = std.Io.Dir.cwd().openFile(io, second, .{ .follow_symlinks = false, .allow_directory = false }) catch |err| switch (err) {
        error.FileNotFound, error.NotDir => return false,
        else => return err,
    };
    defer right.close(io);
    return std.meta.eql(try identity(io, left), try identity(io, right));
}

/// Snapshot publication already holds the snapshot lock; unlike viewer output it replaces any file.
pub fn replaceAtomic(a: Allocator, path: []const u8, content: []const u8) !Outcome {
    return writeConfined(a, std.fs.path.dirname(path) orelse "/", &.{}, std.fs.path.basename(path), content, true, false);
}

/// `ViewerFile.write` of `content` for the snapshot at `snapshot_path` (absolute): to `requested`,
/// or to `default_name` in the default directory.
pub fn check(a: Allocator, snapshot_path: []const u8, requested: ?[]const u8, extension: []const u8, default_name: []const u8, header: []const u8) !Outcome {
    const directory = std.fs.path.dirname(snapshot_path) orelse "/";
    const path = if (requested) |given| blk: {
        if (@import("managed_text.zig").blank(given)) return .{ .refused = "output must not be blank." };
        if (!endsWithIgnoringCase(given, extension)) return .{ .refused = try std.fmt.allocPrint(a, "output must name a {s} file.", .{extension}) };
        if (std.mem.indexOfScalar(u8, given, 0) != null) return .internal;
        const resolved = try std.fs.path.resolveAlloc(a, &.{ directory, given });
        if (!try isInside(a, directory, resolved)) return .{ .refused = try std.fmt.allocPrint(a, "output must stay inside the snapshot's directory {s}; {s} resolves to {s}.", .{ directory, given, resolved }) };
        for ([_][]const u8{ "", ".lock", ".dependency.json", ".backends.json" }) |suffix| {
            if (if (windows) std.ascii.eqlIgnoreCase(resolved, try std.mem.concat(a, u8, &.{ snapshot_path, suffix })) else std.mem.eql(u8, resolved, try std.mem.concat(a, u8, &.{ snapshot_path, suffix }))) return .{ .refused = try std.fmt.allocPrint(a, "output must not replace the snapshot, its lock or its dependency or backends sidecar; {s} resolves to {s}.", .{ given, resolved }) };
        }
        break :blk resolved;
    } else try std.fs.path.join(a, &.{ directory, default_directory_name, default_name });
    if (try linkedComponent(a, directory, path)) |link| return .{ .refused = try std.fmt.allocPrint(a, "output must not pass through a symbolic link or reparse point; {s} is one.", .{link}) };
    if (!try replaceable(a, path, header)) return .{ .refused = try std.fmt.allocPrint(a, "output must not replace a file this tool did not write; {s} exists without the generated-file marker.", .{path}) };
    return .{ .written = path };
}

pub fn write(a: Allocator, snapshot_path: []const u8, requested: ?[]const u8, extension: []const u8, default_name: []const u8, header: []const u8, content: []const u8) !Outcome {
    const directory = std.fs.path.dirname(snapshot_path) orelse "/";
    const path = switch (try check(a, snapshot_path, requested, extension, default_name, header)) {
        .written => |path| path,
        .refused => |message| return .{ .refused = message },
        .internal => return .internal,
    };
    var components: std.ArrayList([]const u8) = .empty;
    var parts = std.mem.tokenizeAny(u8, try std.fs.path.relativeAlloc(a, directory, null, directory, path), separators);
    while (parts.next()) |part| try components.append(a, part);
    const outcome = try writeConfined(a, directory, components.items[0 .. components.items.len - 1], components.items[components.items.len - 1], content, false, false);
    return switch (outcome) {
        .written => .{ .written = path },
        else => outcome,
    };
}

test "generated output preflight refuses invalid paths without publication" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const blank_path = try check(a, "/tmp/project/model.json", " ", ".cs", "", "// Generated by Nyrwvy.");
    try std.testing.expectEqualStrings("output must not be blank.", blank_path.refused);
    const outside = try check(a, "/tmp/project/model.json", "../outside.cs", ".cs", "", "// Generated by Nyrwvy.");
    try std.testing.expect(outside == .refused);
    const itself = try check(a, "/tmp/project.cs/model.json", "../project.cs", ".cs", "", "// Generated by Nyrwvy.");
    // `resolve` writes Windows separators; `dirname` keeps the given ones.
    const resolved = if (windows) "\\tmp\\project.cs" else "/tmp/project.cs";
    try std.testing.expectEqualStrings("output must stay inside the snapshot's directory /tmp/project.cs; ../project.cs resolves to " ++ resolved ++ ".", itself.refused);
    const nul = try check(a, "/tmp/project/model.json", "bad\x00.cs", ".cs", "", "// Generated by Nyrwvy.");
    try std.testing.expect(nul == .internal);
}

/// Create without replacing a racing writer, after flushing the complete temporary.
pub fn createAtomic(a: Allocator, path: []const u8, content: []const u8) !Outcome {
    return writeConfined(a, std.fs.path.dirname(path) orelse "/", &.{}, std.fs.path.basename(path), content, true, true);
}

test "exclusive snapshot creation preserves an existing destination and removes its temporary" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    var temporary = std.testing.tmpDir(.{});
    defer temporary.cleanup();
    const io = std.testing.io;
    const directory = try temporary.dir.realPathFileAlloc(io, ".", a);
    const path = try std.fs.path.join(a, &.{ directory, "app.json" });
    try std.testing.expect((try createAtomic(a, path, "original")) == .written);
    try std.testing.expect((try createAtomic(a, path, "replacement")) == .refused);
    try std.testing.expectEqualStrings("original", try temporary.dir.readFileAlloc(io, "app.json", a, .unlimited));
    var listing = try temporary.dir.openDir(io, ".", .{ .iterate = true });
    defer listing.close(io);
    var entries = listing.iterate();
    var count: usize = 0;
    while (try entries.next(io)) |entry| {
        try std.testing.expectEqualStrings("app.json", entry.name);
        count += 1;
    }
    try std.testing.expectEqual(@as(usize, 1), count);
}

test "portable atomic publication preserves an existing target and cleans temporary files" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const io = std.testing.io;
    var temporary = std.testing.tmpDir(.{ .iterate = true });
    defer temporary.cleanup();
    const directory = try temporary.dir.realPathFileAlloc(io, ".", a);
    const target = try std.fs.path.join(a, &.{ directory, "snapshot.json" });
    try std.testing.expect((try createAtomic(a, target, "first")).written.len == 0);
    try std.testing.expect((try createAtomic(a, target, "lost")) == .refused);
    try std.testing.expectEqualStrings("first", try temporary.dir.readFileAlloc(io, "snapshot.json", a, .unlimited));
    try std.testing.expect((try replaceAtomic(a, target, "next")) == .written);
    try std.testing.expectEqualStrings("next", try temporary.dir.readFileAlloc(io, "snapshot.json", a, .unlimited));
    var entries = temporary.dir.iterate();
    var count: usize = 0;
    while (try entries.next(io)) |entry| {
        try std.testing.expectEqualStrings("snapshot.json", entry.name);
        count += 1;
    }
    try std.testing.expectEqual(@as(usize, 1), count);
}

test "atomic replacement keeps the target's file mode" {
    if (windows) return error.SkipZigTest;
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const io = std.testing.io;
    var temporary = std.testing.tmpDir(.{});
    defer temporary.cleanup();
    // 0o664 and 0o601 lie outside the usual 022/077 umasks, so only an explicit chmod keeps them.
    for ([_]std.posix.mode_t{ 0o600, 0o664, 0o601 }) |mode| {
        const original = try temporary.dir.createFile(io, "snapshot.json", .{});
        try original.setPermissions(io, .fromMode(mode));
        original.close(io);
        const target = try std.fs.path.join(a, &.{ try temporary.dir.realPathFileAlloc(io, ".", a), "snapshot.json" });
        try std.testing.expect((try replaceAtomic(a, target, "next")) == .written);
        try std.testing.expectEqual(mode, (try temporary.dir.statFile(io, "snapshot.json", .{})).permissions.toMode() & 0o777);
    }
}

test "publication identity distinguishes files and detects filesystem aliases" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const io = std.testing.io;
    var temporary = std.testing.tmpDir(.{});
    defer temporary.cleanup();
    const directory = try temporary.dir.realPathFileAlloc(io, ".", a);
    const first = try std.fs.path.join(a, &.{ directory, "Generated.cs" });
    const other = try std.fs.path.join(a, &.{ directory, "Other.cs" });
    const case_alias = try std.fs.path.join(a, &.{ directory, "generated.cs" });
    try std.testing.expect(!try sameFile(first, case_alias));
    _ = try createAtomic(a, first, "implementation");
    _ = try createAtomic(a, other, "other");
    try std.testing.expect(try sameFile(first, first));
    try std.testing.expect(!try sameFile(first, other));
    const case_exists = if (temporary.dir.statFile(io, "generated.cs", .{})) |_| true else |_| false;
    try std.testing.expectEqual(case_exists, try sameFile(first, case_alias));
    const composed = try std.fs.path.join(a, &.{ directory, "\u{e9}.cs" });
    const decomposed = try std.fs.path.join(a, &.{ directory, "e\u{301}.cs" });
    _ = try createAtomic(a, composed, "unicode implementation");
    const normalized_exists = if (temporary.dir.statFile(io, "e\u{301}.cs", .{})) |_| true else |_| false;
    try std.testing.expectEqual(normalized_exists, try sameFile(composed, decomposed));
    try std.testing.expectEqualStrings("implementation", try temporary.dir.readFileAlloc(io, "Generated.cs", a, .unlimited));
}
