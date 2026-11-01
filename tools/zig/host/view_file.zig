//! Writing a viewer page (`ViewerFile.write` and `ConfinedDirectory`): the output resolves against
//! the snapshot's directory and must stay inside it, never passes through a link, replaces only a
//! page this tool wrote, and is written through directory handles below that directory and renamed
//! into place, so a component swapped for a link after the checks cannot carry the write elsewhere.
const std = @import("std");
const linux = std.os.linux;

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

/// `String.IsNullOrWhiteSpace`: empty, or only Unicode white space.
fn blank(text: []const u8) bool {
    var it = std.unicode.Utf8View.initUnchecked(text).iterator();
    while (it.nextCodepoint()) |c| switch (c) {
        0x09...0x0D, 0x20, 0x85, 0xA0, 0x1680, 0x2000...0x200A, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000 => {},
        else => return false,
    };
    return true;
}

fn endsWithIgnoringCase(text: []const u8, suffix: []const u8) bool {
    return text.len >= suffix.len and std.ascii.eqlIgnoreCase(text[text.len - suffix.len ..], suffix);
}

/// `isInside`: the relative path from `directory` is neither rooted nor above it.
fn isInside(a: Allocator, directory: []const u8, path: []const u8) !bool {
    const relative = try std.fs.path.relativeAlloc(a, "/", null, directory, path);
    return !std.fs.path.isAbsolute(relative) and !std.mem.eql(u8, relative, "..") and !std.mem.startsWith(u8, relative, "../");
}

fn isLink(a: Allocator, path: []const u8) !bool {
    var buffer: [std.fs.max_path_bytes]u8 = undefined;
    const z = try a.dupeSentinel(u8, path, 0);
    const rc = linux.readlink(z, &buffer, buffer.len);
    return linux.errno(rc) == .SUCCESS;
}

/// `linkedComponent`: the first existing component below `directory` on the way to `path`, the file
/// included, that is a symbolic link.
pub fn linkedComponent(a: Allocator, directory: []const u8, path: []const u8) !?[]const u8 {
    const relative = try std.fs.path.relativeAlloc(a, "/", null, directory, path);
    var current: []const u8 = directory;
    var parts = std.mem.tokenizeScalar(u8, relative, '/');
    while (parts.next()) |part| {
        current = try std.fs.path.join(a, &.{ current, part });
        if (try isLink(a, current)) return current;
    }
    return null;
}

/// `replaceable`: `path` is not an existing file (`File.Exists` is false for a directory), or it
/// starts with `header`.
fn replaceable(a: Allocator, path: []const u8, header: []const u8) !bool {
    const fd = linux.open(try a.dupeSentinel(u8, path, 0), .{ .ACCMODE = .RDONLY, .CLOEXEC = true }, 0);
    switch (linux.errno(fd)) {
        .SUCCESS => {},
        .NOENT, .NOTDIR => return true,
        else => return false,
    }
    defer _ = linux.close(@intCast(fd));
    const buffer = try a.alloc(u8, header.len);
    var filled: usize = 0;
    while (filled < buffer.len) {
        const rc = linux.read(@intCast(fd), buffer[filled..].ptr, buffer.len - filled);
        switch (linux.errno(rc)) {
            .SUCCESS => {},
            .ISDIR => return true,
            else => return false,
        }
        if (rc == 0) break;
        filled += rc;
    }
    return filled == header.len and std.mem.eql(u8, buffer, header);
}

fn strerror(e: linux.E) []const u8 {
    return @tagName(e);
}

/// `ConfinedDirectory.failure`: a link or non-directory on the way, or the system's error.
fn failure(a: Allocator, e: linux.E, operation: []const u8, name: []const u8) !Outcome {
    if (e == .LOOP or e == .NOTDIR) return .{ .refused = try std.fmt.allocPrint(a, "output must not pass through a symbolic link or reparse point; {s} is one or is not a directory.", .{name}) };
    // shortcut: the system's message is the errno name, not .NET's strerror text; parity leaves these cases out.
    return .{ .refused = try std.fmt.allocPrint(a, "I/O error: {s} {s} failed: {s}", .{ operation, name, strerror(e) }) };
}

const directory_flags: linux.O = .{ .DIRECTORY = true, .CLOEXEC = true };

/// `ConfinedDirectory.openBelow` and `writeFile`: `directory` (following links: the caller's anchor),
/// then each parent below it without following links, creating missing ones, then `content` into a
/// new temporary file there, renamed over the target name.
fn writeConfined(a: Allocator, directory: []const u8, parents: []const []const u8, name: []const u8, content: []const u8, snapshot: bool) !Outcome {
    return writeConfinedAs(a, directory, parents, name, content, snapshot, true);
}

fn writeConfinedAs(a: Allocator, directory: []const u8, parents: []const []const u8, name: []const u8, content: []const u8, snapshot: bool, replace: bool) !Outcome {
    var current: i32 = blk: {
        const fd = linux.openat(linux.AT.FDCWD, try a.dupeSentinel(u8, directory, 0), directory_flags, 0);
        if (linux.errno(fd) != .SUCCESS) return failure(a, linux.errno(fd), "open", directory);
        break :blk @intCast(fd);
    };
    defer _ = linux.close(current);
    for (parents) |part| {
        const z = try a.dupeSentinel(u8, part, 0);
        var flags = directory_flags;
        flags.NOFOLLOW = true;
        var fd = linux.openat(current, z, flags, 0);
        var e = linux.errno(fd);
        if (e == .NOENT) {
            // The last call's error is reported, as `Marshal.GetLastPInvokeError` reads it.
            e = linux.errno(linux.mkdirat(current, z, 0o777));
            if (e == .SUCCESS or e == .EXIST) {
                fd = linux.openat(current, z, flags, 0);
                e = linux.errno(fd);
            }
        }
        if (e != .SUCCESS) return failure(a, e, "open", part);
        _ = linux.close(current);
        current = @intCast(fd);
    }
    var random: [16]u8 = undefined;
    if (linux.getrandom(&random, random.len, 0) != random.len) return .{ .refused = "I/O error: no random bytes for a temporary name." };
    // Viewer names stay short; snapshot temporaries match the startup recovery sweep.
    const temporary = if (snapshot)
        try std.fmt.allocPrintSentinel(a, ".{s}.{x}.tmp", .{ name, random }, 0)
    else
        try std.fmt.allocPrintSentinel(a, ".nyrwvy-{x}.tmp", .{random}, 0);
    const fd = linux.openat(current, temporary, .{ .ACCMODE = .WRONLY, .CREAT = true, .EXCL = true, .NOFOLLOW = true, .CLOEXEC = true }, 0o666);
    if (linux.errno(fd) != .SUCCESS) return failure(a, linux.errno(fd), "create", temporary);
    var renamed = false;
    defer if (!renamed) {
        _ = linux.unlinkat(current, temporary, 0);
    };
    {
        defer _ = linux.close(@intCast(fd));
        var written: usize = 0;
        while (written < content.len) {
            const rc = linux.write(@intCast(fd), content[written..].ptr, content.len - written);
            if (linux.errno(rc) != .SUCCESS) return .{ .refused = try std.fmt.allocPrint(a, "I/O error: {s}", .{strerror(linux.errno(rc))}) };
            written += rc;
        }
        if (linux.errno(linux.fsync(@intCast(fd))) != .SUCCESS) return .{ .refused = "I/O error: fsync failed." };
    }
    const target = try a.dupeSentinel(u8, name, 0);
    const rc = if (replace) linux.renameat(current, temporary, current, target) else linux.renameat2(current, temporary, current, target, .{ .NOREPLACE = true });
    if (linux.errno(rc) != .SUCCESS) return failure(a, linux.errno(rc), "rename", name);
    renamed = true;
    return .{ .written = "" };
}

/// Snapshot publication already holds the snapshot lock; unlike viewer output it replaces any file.
pub fn replaceAtomic(a: Allocator, path: []const u8, content: []const u8) !Outcome {
    return writeConfined(a, std.fs.path.dirname(path) orelse "/", &.{}, std.fs.path.basename(path), content, true);
}

/// `SessionStorage.createAtomically`: like `replaceAtomic`, but an existing file is never replaced.
pub fn createAtomic(a: Allocator, path: []const u8, content: []const u8) !Outcome {
    return writeConfinedAs(a, std.fs.path.dirname(path) orelse "/", &.{}, std.fs.path.basename(path), content, true, false);
}

/// `ViewerFile.write` of `content` for the snapshot at `snapshot_path` (absolute): to `requested`,
/// or to `default_name` in the default directory.
pub fn check(a: Allocator, snapshot_path: []const u8, requested: ?[]const u8, extension: []const u8, default_name: []const u8, header: []const u8) !Outcome {
    const directory = std.fs.path.dirname(snapshot_path) orelse "/";
    const path = if (requested) |given| blk: {
        if (blank(given)) return .{ .refused = "output must not be blank." };
        if (!endsWithIgnoringCase(given, extension)) return .{ .refused = try std.fmt.allocPrint(a, "output must name a {s} file.", .{extension}) };
        if (std.mem.indexOfScalar(u8, given, 0) != null) return .internal;
        const resolved = try std.fs.path.resolveAlloc(a, &.{ directory, given });
        if (!try isInside(a, directory, resolved)) return .{ .refused = try std.fmt.allocPrint(a, "output must stay inside the snapshot's directory {s}; {s} resolves to {s}.", .{ directory, given, resolved }) };
        for ([_][]const u8{ "", ".lock", ".dependency.json", ".backends.json" }) |suffix| {
            if (std.mem.eql(u8, resolved, try std.mem.concat(a, u8, &.{ snapshot_path, suffix }))) return .{ .refused = try std.fmt.allocPrint(a, "output must not replace the snapshot, its lock or its dependency or backends sidecar; {s} resolves to {s}.", .{ given, resolved }) };
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
    var parts = std.mem.tokenizeScalar(u8, try std.fs.path.relativeAlloc(a, "/", null, directory, path), '/');
    while (parts.next()) |part| try components.append(a, part);
    const outcome = try writeConfined(a, directory, components.items[0 .. components.items.len - 1], components.items[components.items.len - 1], content, false);
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
    const nul = try check(a, "/tmp/project/model.json", "bad\x00.cs", ".cs", "", "// Generated by Nyrwvy.");
    try std.testing.expect(nul == .internal);
}
