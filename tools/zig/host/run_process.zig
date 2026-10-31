//! The Zig compiler and isolated runner children. No child or request allocation survives a run.
const std = @import("std");
const Allocator = std.mem.Allocator;
pub const Diagnostic = struct { code: []const u8, message: []const u8 };

var compiler: []const u8 = "zig";
var directory: []const u8 = ".";
var cache: []const u8 = "";
var executable_search: []const u8 = "";
var compiler_stamp: ?[32]u8 = null;
var compiler_digest: [32]u8 = undefined;

pub fn init(a: Allocator, io: std.Io, env: *const std.process.Environ.Map) !void {
    compiler = env.get("NYRWVY_ZIG") orelse "zig";
    executable_search = env.get("PATH") orelse "/usr/bin:/bin";
    if (compiler.len == 0) compiler = "zig";
    // The host is installed under the repository's mise.toml. A workspace can be elsewhere.
    var executable_path: []const u8 = @src().file;
    if (!std.fs.path.isAbsolute(executable_path)) executable_path = try std.fs.path.resolveAlloc(a, &.{ try std.process.currentPathAlloc(io, a), executable_path });
    while (std.fs.path.dirname(executable_path)) |parent| {
        const pin = try std.fs.path.join(a, &.{ parent, "mise.toml" });
        if (std.Io.Dir.cwd().access(io, pin, .{})) |_| {
            directory = parent;
            break;
        } else |_| {}
        executable_path = parent;
    }
    if (std.mem.eql(u8, directory, ".")) directory = try std.process.currentPathAlloc(io, a);
    const root = if (env.get("XDG_CACHE_HOME")) |path| (if (path.len > 0) path else null) else null;
    cache = try std.fs.path.join(a, &.{ root orelse try std.fs.path.join(a, &.{ env.get("HOME") orelse ".", ".cache" }), "nyrwvy", "zig-runners" });
}

pub const Outcome = union(enum) { completed: std.process.RunResult, failed: Diagnostic };

fn collected(a: Allocator, io: std.Io, spawned: std.process.Child, done: *std.Io.Event, max_bytes: usize) std.process.RunError!std.process.RunResult {
    defer done.set(io);
    var child = spawned;
    defer {
        // std.Child.kill sends SIGTERM, which arbitrary compiler wrappers can ignore.
        if (child.id) |pid| _ = std.os.linux.kill(-pid, .KILL);
        child.kill(io);
    }
    var buffers: std.Io.File.MultiReader.Buffer(2) = undefined;
    var streams: std.Io.File.MultiReader = undefined;
    streams.init(a, io, buffers.toStreams(), &.{ child.stdout.?, child.stderr.? });
    defer streams.deinit();
    while (streams.fill(4096, .none)) |_| {
        if (streams.reader(0).buffered().len > max_bytes or streams.reader(1).buffered().len > max_bytes) return error.StreamTooLong;
    } else |e| switch (e) {
        error.EndOfStream => {},
        error.Canceled => return error.Canceled,
        error.Timeout => return error.Timeout,
        error.ConcurrencyUnavailable => return error.ConcurrencyUnavailable,
    }
    try streams.checkAnyError();
    const term = try child.wait(io);
    return .{ .term = term, .stdout = try streams.toOwnedSlice(0), .stderr = try streams.toOwnedSlice(1) };
}

/// The deadline covers both stream draining and process wait, including a child that closes
/// its streams then hangs. Kill the process group before awaiting its owner, so neither wait
/// cancellation nor inherited pipe handles can lose the process before it is reaped.
pub fn launch(a: Allocator, io: std.Io, argv: []const []const u8, cwd: []const u8, phase: []const u8, seconds: i64) !Outcome {
    return launchBounded(a, io, argv, cwd, phase, seconds, 1048576);
}

/// Managed text may use four bytes per UTF-16 unit; its caller applies the decoded text limit.
pub fn launchBounded(a: Allocator, io: std.Io, argv: []const []const u8, cwd: []const u8, phase: []const u8, seconds: i64, max_bytes: usize) !Outcome {
    var child = std.process.spawn(io, .{ .argv = argv, .cwd = .{ .path = cwd }, .stdin = .ignore, .stdout = .pipe, .stderr = .pipe, .pgid = 0 }) catch |e|
        return .{ .failed = .{ .code = "RunStartupFailure", .message = @errorName(e) } };
    const pid = child.id.?;
    var done: std.Io.Event = .unset;
    var running = io.concurrent(collected, .{ a, io, child, &done, max_bytes }) catch |e| {
        _ = std.os.linux.kill(-pid, .KILL);
        child.kill(io);
        return .{ .failed = .{ .code = "RunStartupFailure", .message = @errorName(e) } };
    };
    const deadline: std.Io.Timeout = .{ .deadline = .fromNow(io, .{ .raw = .fromSeconds(seconds), .clock = .awake }) };
    while (!done.isSet()) done.waitTimeout(io, deadline) catch |e| {
        if (e == error.Timeout and std.Io.Clock.Timestamp.compare(.now(io, .awake), .lt, deadline.toTimestamp(io).?)) continue;
        _ = std.os.linux.kill(-pid, .KILL);
        _ = running.await(io) catch {};
        return .{ .failed = .{ .code = "RunTimeout", .message = try std.fmt.allocPrint(a, "The {s} exceeded its time limit of {d} s.", .{ phase, seconds }) } };
    };
    const result = running.await(io) catch |e| return .{ .failed = switch (e) {
        error.StreamTooLong => .{ .code = "RunOutputTooLarge", .message = "The process produced more than 1 MiB of output." },
        error.FileNotFound, error.AccessDenied, error.InvalidExe => .{ .code = "RunStartupFailure", .message = @errorName(e) },
        else => .{ .code = "RunIoFailure", .message = @errorName(e) },
    } };
    return .{ .completed = result };
}

pub fn processFailure(a: Allocator, result: std.process.RunResult) !Diagnostic {
    const text = @import("managed_text.zig");
    const details = try std.mem.concat(a, u8, &.{ try text.decode(a, result.stdout), "\n", try text.decode(a, result.stderr) });
    return .{ .code = "RunProcessFailure", .message = try text.prefix(a, details, 4096) };
}

fn privateDirectory(io: std.Io, path: []const u8) !void {
    _ = try std.Io.Dir.cwd().createDirPathStatus(io, path, .fromMode(0o700));
}

pub const Built = union(enum) { executable: []const u8, failed: Diagnostic };

fn invokedCompiler(a: Allocator, io: std.Io) !?[]const u8 {
    if (std.mem.indexOfScalar(u8, compiler, '/') != null) return try std.fs.path.resolveAlloc(a, &.{ directory, compiler });
    var paths = std.mem.splitScalar(u8, executable_search, ':');
    while (paths.next()) |part| {
        const path = try std.fs.path.resolveAlloc(a, &.{ directory, part, compiler });
        if (std.Io.Dir.cwd().access(io, path, .{ .execute = true })) |_| return path else |_| {}
    }
    return null;
}

pub fn build(a: Allocator, io: std.Io, source: []const u8) !Built {
    // `zig env` binds the compiler, target, library and environment configuration. Its resolved
    // binary metadata also invalidates a replacement compiler reporting the same version.
    const compiler_env = switch (try launch(a, io, &.{ compiler, "env" }, directory, "Zig compiler identification", 30)) {
        .failed => |failure| return .{ .failed = failure },
        .completed => |result| if (result.term.success()) result.stdout else return .{ .failed = try processFailure(a, result) },
    };
    var hash = std.crypto.hash.sha2.Sha256.init(.{});
    for ([_][]const u8{ compiler, compiler_env, "build-exe\x00ReleaseSafe", @import("zig_emitter").build_source, source }) |part| {
        hash.update(part);
        hash.update("\x00");
    }
    if (try invokedCompiler(a, io)) |path| {
        hash.update(path);
        // Same-version wrappers can change compiler flags without changing `zig env`.
        const stat = try std.Io.Dir.cwd().statFile(io, path, .{});
        var stamp: [32]u8 = undefined;
        std.crypto.hash.sha2.Sha256.hash(try std.fmt.allocPrint(a, "{s}:{d}:{d}", .{ path, stat.size, stat.mtime.nanoseconds }), &stamp, .{});
        if (compiler_stamp == null or !std.mem.eql(u8, &compiler_stamp.?, &stamp)) {
            std.crypto.hash.sha2.Sha256.hash(try std.Io.Dir.cwd().readFileAlloc(io, path, a, .unlimited), &compiler_digest, .{});
            compiler_stamp = stamp;
        }
        hash.update(&compiler_digest);
    }
    if (std.mem.indexOf(u8, compiler_env, ".zig_exe = ")) |at| {
        const rest = compiler_env[at + ".zig_exe = ".len ..];
        if (std.mem.indexOf(u8, rest, ",\n")) |end| {
            const path = std.json.parseFromSliceLeaky([]const u8, a, rest[0..end], .{}) catch "";
            if (std.Io.Dir.cwd().statFile(io, path, .{})) |stat| {
                hash.update(try std.fmt.allocPrint(a, "{d}:{d}", .{ stat.size, stat.mtime.nanoseconds }));
            } else |_| {}
        }
    }
    var digest: [32]u8 = undefined;
    hash.final(&digest);
    const stem = std.fmt.bytesToHex(digest, .upper);
    const target = try std.fs.path.join(a, &.{ cache, &stem });
    const binary = try std.fs.path.join(a, &.{ target, "runner" });
    if (std.Io.Dir.cwd().statFile(io, binary, .{})) |stat| {
        if (stat.kind == .file) return .{ .executable = binary };
    } else |_| {}
    try privateDirectory(io, cache);
    try privateDirectory(io, target);
    const input = try std.fs.path.join(a, &.{ target, "runner.zig" });
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = input, .data = source, .flags = .{ .permissions = .fromMode(0o600) } });
    var random: [16]u8 = undefined;
    io.random(&random);
    const suffix = std.fmt.bytesToHex(random, .lower);
    const partial = try std.fmt.allocPrint(a, "{s}.{s}", .{ binary, suffix });
    defer std.Io.Dir.cwd().deleteFile(io, partial) catch {};
    const built = switch (try launch(a, io, &.{ compiler, "build-exe", input, "-O", "ReleaseSafe", "--cache-dir", try std.fs.path.join(a, &.{ cache, "cache" }), try std.mem.concat(a, u8, &.{ "-femit-bin=", partial }) }, directory, "Zig build", 300)) {
        .failed => |failure| return .{ .failed = failure },
        .completed => |result| result,
    };
    if (!built.term.success()) return .{ .failed = try processFailure(a, built) };
    try std.Io.Dir.renameAbsolute(partial, binary, io);
    return .{ .executable = binary };
}

test "compiler child is bounded even after closing its output streams" {
    const a = std.testing.allocator;
    const io = std.testing.io;
    var temporary = std.testing.tmpDir(.{});
    defer temporary.cleanup();
    const cwd = try temporary.dir.realPathFileAlloc(io, ".", a);
    defer a.free(cwd);
    const result = try launch(a, io, &.{ "/bin/sh", "-c", "trap '' TERM; echo $$ > pid; exec 1>&- 2>&-; exec sleep 30" }, cwd, "pure function", 1);
    switch (result) {
        .failed => |failure| {
            defer a.free(failure.message);
            try std.testing.expectEqualStrings("RunTimeout", failure.code);
            const text = try temporary.dir.readFileAlloc(io, "pid", a, .limited(100));
            defer a.free(text);
            const pid = try std.fmt.parseInt(i32, std.mem.trim(u8, text, "\n"), 10);
            try std.testing.expectEqual(std.os.linux.E.SRCH, std.os.linux.errno(std.os.linux.kill(pid, @fromBackingInt(@intCast(0)))));
        },
        .completed => |output| {
            defer a.free(output.stdout);
            defer a.free(output.stderr);
            return error.TestUnexpectedResult;
        },
    }
}

test "child output is bounded while both redirected streams drain" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const result = try launch(arena.allocator(), std.testing.io, &.{ "/bin/sh", "-c", "head -c 1048577 /dev/zero; head -c 1048577 /dev/zero >&2" }, ".", "pure function", 5);
    try std.testing.expect(result == .failed);
    try std.testing.expectEqualStrings("RunOutputTooLarge", result.failed.code);
}

test "both child streams drain beyond pipe capacity without deadlock" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const result = try launch(arena.allocator(), std.testing.io, &.{ "/bin/sh", "-c", "head -c 900000 /dev/zero & head -c 900000 /dev/zero >&2 & wait" }, ".", "pure function", 5);
    try std.testing.expect(result == .completed);
    try std.testing.expect(result.completed.term.success());
    try std.testing.expectEqual(@as(usize, 900000), result.completed.stdout.len);
    try std.testing.expectEqual(@as(usize, 900000), result.completed.stderr.len);
}

test "compiler diagnostics decode malformed text before a UTF16 prefix reaches the wire" {
    var arena: std.heap.ArenaAllocator = .init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const replaced = try processFailure(a, .{ .term = .{ .exited = 1 }, .stdout = try a.dupe(u8, "\xff"), .stderr = try a.dupe(u8, "\xe1\x80") });
    try std.testing.expectEqualStrings("�\n�", replaced.message);
    const ascii = try a.alloc(u8, 4095);
    @memset(ascii, 'a');
    const stdout = try std.mem.concat(a, u8, &.{ ascii, "😀" });
    const clipped = try processFailure(a, .{ .term = .{ .exited = 1 }, .stdout = stdout, .stderr = "" });
    try std.testing.expectEqual(@as(usize, 4096), @import("managed_text.zig").units(clipped.message));
    try std.testing.expect(std.mem.endsWith(u8, clipped.message, "�"));
    try std.testing.expect(std.unicode.utf8ValidateSlice(clipped.message));
}
