// Zig implementations of the host library's capabilities, bound by host.zig.json. Distributed as
// source: copy both files beside the emitted library (see README). An error's name is its `Err` text.
const std = @import("std");
const Allocator = std.mem.Allocator;

fn io() std.Io {
    return std.Io.Threaded.global_single_threaded.io();
}

/// Invalid bytes read as U+FFFD, as in NyHost.cs.
fn decoded(allocator: Allocator, bytes: []const u8) ![]const u8 {
    if (std.unicode.utf8ValidateSlice(bytes)) return bytes;
    return std.fmt.allocPrint(allocator, "{f}", .{std.unicode.fmtUtf8(bytes)});
}

pub fn readBytes(allocator: Allocator, path: []const u8) ![]const u8 {
    return std.Io.Dir.cwd().readFileAlloc(io(), path, allocator, .unlimited);
}

/// A UTF-8 byte order mark is dropped.
pub fn readText(allocator: Allocator, path: []const u8) ![]const u8 {
    const bytes = try readBytes(allocator, path);
    return decoded(allocator, if (std.mem.startsWith(u8, bytes, "\xef\xbb\xbf")) bytes[3..] else bytes);
}

/// Creates or replaces the file.
pub fn writeBytes(_: Allocator, path: []const u8, data: []const u8) !void {
    try std.Io.Dir.cwd().writeFile(io(), .{ .sub_path = path, .data = data });
}

pub fn envGet(allocator: Allocator, name: []const u8) !?[]const u8 {
    if (std.mem.indexOfScalar(u8, name, 0) != null) return null;
    const value = std.c.getenv(try allocator.dupeSentinel(u8, name, 0)) orelse return null;
    return try decoded(allocator, std.mem.span(value));
}

/// An Io that sees libc's environment; a shared library gets none from Zig's `main`. Spawning needs it for PATH and the child.
fn processIo(allocator: Allocator) std.Io.Threaded {
    return .init(allocator, .{ .environ = .{ .block = .{ .slice = @ptrCast(std.mem.span(std.c.environ)) } } });
}

fn command(allocator: Allocator, program: []const u8, args: []const []const u8) ![]const []const u8 {
    const argv = try allocator.alloc([]const u8, args.len + 1);
    argv[0] = program;
    @memcpy(argv[1..], args);
    return argv;
}

/// A child nobody waits for would stay a zombie until this process exits.
fn reap(pid: std.posix.pid_t) void {
    _ = std.c.waitpid(pid, null, 0);
}

/// Starts the program without waiting for it.
pub fn processSpawn(allocator: Allocator, program: []const u8, args: []const []const u8) !void {
    var threaded = processIo(allocator);
    defer threaded.deinit();
    const child = try std.process.spawn(threaded.io(), .{ .argv = try command(allocator, program, args) });
    (try std.Thread.spawn(.{}, reap, .{child.id.?})).detach();
}

/// Runs the program to completion with empty standard input, until stdout and stderr close.
pub fn processRun(allocator: Allocator, program: []const u8, args: []const []const u8) !struct { exitCode: i64, stdout: []const u8, stderr: []const u8 } {
    var threaded = processIo(allocator);
    defer threaded.deinit();
    const result = try std.process.run(allocator, threaded.io(), .{ .argv = try command(allocator, program, args) });
    return .{
        .exitCode = switch (result.term) {
            .exited => |code| code,
            // The shell's convention, which .NET's ExitCode follows.
            .signal => |signal| 128 + @as(i64, @intFromEnum(signal)),
            .stopped, .unknown => -1,
        },
        .stdout = result.stdout,
        .stderr = result.stderr,
    };
}
