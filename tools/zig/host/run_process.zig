//! The Zig compiler and isolated runner children. No child or request allocation survives a run.
const std = @import("std");
const Allocator = std.mem.Allocator;
const lifetime = @import("process_lifetime.zig");
const windows = @import("builtin").os.tag == .windows;
pub const Diagnostic = struct { code: []const u8, message: []const u8 };

var compiler: []const u8 = "zig";
var directory: []const u8 = ".";
var cache: []const u8 = "";
var executable_search: []const u8 = "";
var compiler_stamp: ?[32]u8 = null;
var compiler_digest: [32]u8 = undefined;

var optimize: []const u8 = "ReleaseSafe";
// Debug info is two thirds of a ReleaseSafe runner's build (16 s, 5.5 s without it), and a crash reply
// reads only the runner's `panic: ` line, never its stack trace. Debug runners keep it for debugging.
var strip: []const u8 = "-fstrip";

pub fn init(a: Allocator, io: std.Io, env: *const std.process.Environ.Map) !void {
    compiler = env.get("NYRWVY_ZIG") orelse "zig";
    // Tests can trade the optimized build for a compile about 14 times faster. Anything else is the default.
    if (env.get("NYRWVY_ZIG_RUNNER_OPTIMIZE")) |mode| {
        if (std.mem.eql(u8, mode, "Debug")) {
            optimize = "Debug";
            strip = "-fno-strip";
        }
    }
    executable_search = env.get("PATH") orelse if (windows) "" else "/usr/bin:/bin";
    if (compiler.len == 0) compiler = "zig";
    const build_info = @import("build_info");
    const repository_root: ?[]const u8 = if (@hasDecl(build_info, "repository_root")) build_info.repository_root else null;
    // The adapter names the checkout; otherwise the one the host was built from, then the working directory.
    const named = if (env.get("NYRWVY_REPOSITORY_ROOT")) |path| (if (path.len > 0) path else null) else null;
    directory = named orelse repository_root orelse try std.process.currentPathAlloc(io, a);
    const root = if (env.get("XDG_CACHE_HOME")) |path| (if (path.len > 0) path else null) else null;
    const base = root orelse if (windows) env.get("LOCALAPPDATA") orelse return error.CacheDirectoryUnavailable else try std.fs.path.join(a, &.{ env.get("HOME") orelse ".", ".cache" });
    cache_root = try std.fs.path.join(a, &.{ base, "nyrwvy" });
    cache = try std.fs.path.join(a, &.{ cache_root, "zig-runners" });
}

/// `$XDG_CACHE_HOME/nyrwvy` (`%LOCALAPPDATA%\nyrwvy` on Windows), or `~/.cache/nyrwvy`.
pub var cache_root: []const u8 = "";

pub const Outcome = union(enum) { completed: std.process.RunResult, failed: Diagnostic };

fn collected(a: Allocator, io: std.Io, spawned: std.process.Child, done: *std.Io.Event, max_bytes: usize) std.process.RunError!std.process.RunResult {
    defer done.set(io);
    var child = spawned;
    defer {
        lifetime.unregister(io, .foreground);
        // std.Child.kill sends SIGTERM, which arbitrary compiler wrappers can ignore.
        lifetime.kill(io, .foreground);
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
    // Keep the leader unreaped until the monitor can no longer signal its group ID.
    try lifetime.waitForExit(io, &child);
    lifetime.unregister(io, .foreground);
    const term = try child.wait(io);
    return .{ .term = term, .stdout = try streams.toOwnedSlice(0), .stderr = try streams.toOwnedSlice(1) };
}

/// The deadline covers both stream draining and process wait, including a child that closes
/// its streams then hangs. Kill the process group before awaiting its owner, so neither wait
/// cancellation nor inherited pipe handles can lose the process before it is reaped.
pub fn launch(a: Allocator, io: std.Io, argv: []const []const u8, cwd: []const u8, phase: []const u8, seconds: i64) !Outcome {
    return launchBounded(a, io, argv, cwd, phase, seconds, 1048576, null);
}

/// Managed text may use four bytes per UTF-16 unit; its caller applies the decoded text limit.
pub fn launchBounded(a: Allocator, io: std.Io, argv: []const []const u8, cwd: []const u8, phase: []const u8, seconds: i64, max_bytes: usize, environ_map: ?*const std.process.Environ.Map) !Outcome {
    var child = lifetime.spawn(io, .foreground, .{ .argv = argv, .cwd = .{ .path = cwd }, .environ_map = environ_map, .stdin = .ignore, .stdout = .pipe, .stderr = .pipe }) catch |e|
        return .{ .failed = .{ .code = "RunStartupFailure", .message = @errorName(e) } };
    var done: std.Io.Event = .unset;
    var running = io.concurrent(collected, .{ a, io, child, &done, max_bytes }) catch |e| {
        lifetime.unregister(io, .foreground);
        lifetime.kill(io, .foreground);
        child.kill(io);
        return .{ .failed = .{ .code = "RunStartupFailure", .message = @errorName(e) } };
    };
    const deadline: std.Io.Timeout = .{ .deadline = .fromNow(io, .{ .raw = .fromSeconds(seconds), .clock = .awake }) };
    while (!done.isSet()) done.waitTimeout(io, deadline) catch |e| {
        if (e == error.Timeout and std.Io.Clock.Timestamp.compare(.now(io, .awake), .lt, deadline.toTimestamp(io).?)) continue;
        lifetime.kill(io, .foreground);
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
    _ = try std.Io.Dir.cwd().createDirPathStatus(io, path, if (windows) .default_dir else .fromMode(0o700));
}

pub const Built = union(enum) { executable: []const u8, failed: Diagnostic };

fn invokedCompiler(a: Allocator, io: std.Io) !?[]const u8 {
    if (std.mem.indexOfScalar(u8, compiler, '/') != null or (windows and std.mem.indexOfScalar(u8, compiler, '\\') != null)) return try std.fs.path.resolveAlloc(a, &.{ directory, compiler });
    var paths = std.mem.splitScalar(u8, executable_search, std.fs.path.delimiter);
    while (paths.next()) |part| {
        const path = try std.fs.path.resolveAlloc(a, &.{ directory, part, if (windows and !std.ascii.endsWithIgnoreCase(compiler, ".exe")) try std.mem.concat(a, u8, &.{ compiler, ".exe" }) else compiler });
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
    for ([_][]const u8{ compiler, compiler_env, "build-exe", optimize, strip, @import("zig_emitter").build_source, source }) |part| {
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
            // Streamed: the compiler binary is far too large to hold in the request arena.
            const file = try std.Io.Dir.cwd().openFile(io, path, .{});
            defer file.close(io);
            var reader_buffer: [4096]u8 = undefined;
            var reader = file.reader(io, &reader_buffer);
            var chunk: [65536]u8 = undefined;
            var file_hash = std.crypto.hash.sha2.Sha256.init(.{});
            while (true) {
                const size = try reader.interface.readSliceShort(&chunk);
                file_hash.update(chunk[0..size]);
                if (size < chunk.len) break;
            }
            file_hash.final(&compiler_digest);
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
    const binary = try std.fs.path.join(a, &.{ target, if (windows) "runner.exe" else "runner" });
    if (std.Io.Dir.cwd().statFile(io, binary, .{})) |stat| {
        if (stat.kind == .file) return .{ .executable = binary };
    } else |_| {}
    try privateDirectory(io, cache);
    try privateDirectory(io, target);
    const input = try std.fs.path.join(a, &.{ target, "runner.zig" });
    var random: [16]u8 = undefined;
    io.random(&random);
    const suffix = std.fmt.bytesToHex(random, .lower);
    // Renamed into place, so a concurrent session's compiler never reads a half-rewritten source.
    const written = try std.fmt.allocPrint(a, "{s}.{s}", .{ input, suffix });
    defer std.Io.Dir.cwd().deleteFile(io, written) catch {};
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = written, .data = source, .flags = .{ .permissions = if (windows) .default_file else .fromMode(0o600) } });
    try std.Io.Dir.renameAbsolute(written, input, io);
    const partial = try std.fmt.allocPrint(a, "{s}.{s}{s}", .{ binary, suffix, if (windows) ".exe" else "" });
    defer std.Io.Dir.cwd().deleteFile(io, partial) catch {};
    var arguments: std.ArrayList([]const u8) = .empty;
    try arguments.appendSlice(a, &.{ compiler, "build-exe", input, "-O", optimize, strip, "--cache-dir", try std.fs.path.join(a, &.{ cache, "cache" }), try std.mem.concat(a, u8, &.{ "-femit-bin=", partial }) });
    if (@import("builtin").os.tag == .macos) try arguments.append(a, "-lc");
    const built = switch (try launch(a, io, arguments.items, directory, "Zig build", 300)) {
        .failed => |failure| return .{ .failed = failure },
        .completed => |result| result,
    };
    if (!built.term.success()) return .{ .failed = try processFailure(a, built) };
    // Synced before the rename, so a crash cannot leave a truncated runner that later runs as a hit.
    {
        const file = try std.Io.Dir.cwd().openFile(io, partial, .{ .mode = .read_write });
        defer file.close(io);
        try file.sync(io);
    }
    try std.Io.Dir.renameAbsolute(partial, binary, io);
    return .{ .executable = binary };
}

test "compiler child is bounded even after closing its output streams" {
    if (@import("builtin").os.tag != .linux) return error.SkipZigTest;
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
    if (windows) return error.SkipZigTest;
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const result = try launch(arena.allocator(), std.testing.io, &.{ "/bin/sh", "-c", "head -c 1048577 /dev/zero; head -c 1048577 /dev/zero >&2" }, ".", "pure function", 5);
    try std.testing.expect(result == .failed);
    try std.testing.expectEqualStrings("RunOutputTooLarge", result.failed.code);
}

test "both child streams drain beyond pipe capacity without deadlock" {
    if (windows) return error.SkipZigTest;
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

test "owned process trees close inherited output pipes on timeout" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const io = std.testing.io;
    const started = std.Io.Clock.Timestamp.now(io, .awake);
    const argv: []const []const u8 = if (windows)
        &.{ "cmd.exe", "/d", "/c", "ping -n 30 127.0.0.1" }
    else
        &.{ "/bin/sh", "-c", "trap '' TERM INT; sleep 30" };
    const outcome = try launch(arena.allocator(), io, argv, ".", "pure function", 1);
    try std.testing.expect(outcome == .failed);
    try std.testing.expectEqualStrings("RunTimeout", outcome.failed.code);
    try std.testing.expect(started.durationTo(.now(io, .awake)).raw.nanoseconds < 10 * std.time.ns_per_s);
}
