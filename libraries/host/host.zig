// Zig implementations of the host library's capabilities, bound by host.zig.json. Distributed as
// source: copy both files beside the emitted library (see README). An error's name is its `Err` text.
const std = @import("std");
const Allocator = std.mem.Allocator;

/// Shared by declarations that run with the library lock released (`mayWait`): it has no allocator and
/// no `io.concurrent`; its own state is behind its mutex. Two threads in it at once are not yet exercised.
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

// shortcut: each Threaded installs and on deinit restores the process's SIGIO and SIGPIPE handlers, and
// Process.run now runs with the library lock released, so two overlapping calls can restore a stale
// handler; keep one process-lifetime Io if a host runs processes from several threads at once.
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

// Ptys (Linux with glibc; posix_spawn, as NyHost.cs). glibc declarations std.c leaves out:
const SpawnBuffer = extern struct { bytes: [512]u8 align(16) }; // glibc's types are 80 and 336 bytes
extern "c" fn posix_openpt(flags: c_int) c_int;
extern "c" fn grantpt(fd: c_int) c_int;
extern "c" fn unlockpt(fd: c_int) c_int;
extern "c" fn ptsname_r(fd: c_int, buffer: [*]u8, len: usize) c_int;
extern "c" fn tcgetpgrp(fd: c_int) c_int;
extern "c" fn posix_spawn_file_actions_init(actions: *SpawnBuffer) c_int;
extern "c" fn posix_spawn_file_actions_destroy(actions: *SpawnBuffer) c_int;
extern "c" fn posix_spawn_file_actions_addopen(actions: *SpawnBuffer, fd: c_int, path: [*:0]const u8, flags: c_int, mode: c_uint) c_int;
extern "c" fn posix_spawn_file_actions_adddup2(actions: *SpawnBuffer, fd: c_int, new_fd: c_int) c_int;
extern "c" fn posix_spawn_file_actions_addchdir_np(actions: *SpawnBuffer, path: [*:0]const u8) c_int;
extern "c" fn posix_spawnattr_init(attr: *SpawnBuffer) c_int;
extern "c" fn posix_spawnattr_destroy(attr: *SpawnBuffer) c_int;
extern "c" fn posix_spawnattr_setflags(attr: *SpawnBuffer, flags: c_short) c_int;
extern "c" fn posix_spawnp(pid: *c_int, file: [*:0]const u8, actions: *const SpawnBuffer, attr: *const SpawnBuffer, argv: [*:null]const ?[*:0]const u8, envp: [*:null]const ?[*:0]const u8) c_int;
const POSIX_SPAWN_SETSID = 0x80;

fn openFlags(o: std.c.O) c_int {
    return @bitCast(o);
}

/// A program on a pseudo terminal, the `Pty` handle's state. Never freed: a read or write that passed
/// the handle check runs with the library lock released, so it may start after the close.
pub const Pty = struct {
    master: c_int,
    /// An eventfd the close signals, so a blocked read returns None.
    wake: c_int,
    pid: c_int,
    /// The open handle plus each read or write in progress; the last to leave closes the fds.
    users: std.atomic.Value(u32) = .init(1),

    fn enter(self: *Pty) bool {
        var users = self.users.load(.acquire);
        while (users > 0) users = self.users.cmpxchgWeak(users, users + 1, .acq_rel, .acquire) orelse return true;
        return false;
    }

    /// Waits until the master is ready for `events`; false once the close signals `wake`.
    fn wait(self: *Pty, events: i16) bool {
        var fds = [_]std.c.pollfd{ .{ .fd = self.master, .events = events, .revents = 0 }, .{ .fd = self.wake, .events = std.c.POLL.IN, .revents = 0 } };
        while (std.c.poll(&fds, fds.len, -1) < 0) if (std.c.errno(-1) != .INTR) return false;
        return fds[1].revents == 0;
    }

    fn leave(self: *Pty) void {
        if (self.users.fetchSub(1, .acq_rel) > 1) return;
        _ = std.c.close(self.master);
        _ = std.c.close(self.wake);
    }
};

/// A `Map<String, String>` as the emitted library passes it.
pub const Environment = struct { keys: []const []const u8, values: []const []const u8 };

fn setSize(fd: c_int, cols: i64, rows: i64) void {
    var size: std.posix.winsize = .{ .col = @intCast(std.math.clamp(cols, 1, 65535)), .row = @intCast(std.math.clamp(rows, 1, 65535)), .xpixel = 0, .ypixel = 0 };
    _ = std.c.ioctl(fd, std.posix.T.IOCSWINSZ, &size);
}

/// The child inherits this process's environment with `environment`'s variables replaced or added.
pub fn ptyOpen(allocator: Allocator, argv: []const []const u8, environment: Environment, cols: i64, rows: i64) !*Pty {
    return ptyOpenImpl(allocator, argv, environment, cols, rows, null);
}

/// `ptyOpen` starting in `cwd` instead of this process's working directory. A bad `cwd` fails the open.
pub fn ptyOpenAt(allocator: Allocator, argv: []const []const u8, environment: Environment, cols: i64, rows: i64, cwd: ?[]const u8) !*Pty {
    return ptyOpenImpl(allocator, argv, environment, cols, rows, cwd);
}

fn ptyOpenImpl(allocator: Allocator, argv: []const []const u8, environment: Environment, cols: i64, rows: i64, cwd: ?[]const u8) !*Pty {
    if (argv.len == 0) return error.EmptyArgv;
    const c_argv = try allocator.allocSentinel(?[*:0]const u8, argv.len, null);
    for (c_argv, argv) |*slot, item| slot.* = try allocator.dupeSentinel(u8, item, 0);
    var env: std.ArrayList(?[*:0]const u8) = .empty;
    for (std.mem.span(std.c.environ)) |entry| {
        const text = std.mem.span(entry.?);
        const name = text[0 .. std.mem.indexOfScalar(u8, text, '=') orelse text.len];
        for (environment.keys) |key| {
            if (std.mem.eql(u8, key, name)) break;
        } else try env.append(allocator, entry.?);
    }
    for (environment.keys, environment.values) |key, value| try env.append(allocator, try std.fmt.allocPrintSentinel(allocator, "{s}={s}", .{ key, value }, 0));
    try env.append(allocator, null);

    // Close-on-exec from the start, and no slave fd in this process: neither can leak into a child
    // that another thread starts, which would keep this pty's reads from ever seeing EIO.
    const master = posix_openpt(openFlags(.{ .ACCMODE = .RDWR, .NOCTTY = true, .CLOEXEC = true, .NONBLOCK = true }));
    if (master < 0) return error.PtyUnavailable;
    errdefer _ = std.c.close(master);
    var name: [128:0]u8 = @splat(0);
    if (grantpt(master) != 0 or unlockpt(master) != 0 or ptsname_r(master, &name, name.len) != 0) return error.PtyUnavailable;
    setSize(master, cols, rows);
    const wake = std.c.eventfd(0, std.os.linux.EFD.CLOEXEC);
    if (wake < 0) return error.PtyUnavailable;
    errdefer _ = std.c.close(wake);
    var actions: SpawnBuffer = undefined;
    var attr: SpawnBuffer = undefined;
    if (posix_spawn_file_actions_init(&actions) != 0) return error.SpawnFailed;
    defer _ = posix_spawn_file_actions_destroy(&actions);
    if (posix_spawnattr_init(&attr) != 0) return error.SpawnFailed;
    defer _ = posix_spawnattr_destroy(&attr);
    // A session leader that opens the slave by path gets it as its controlling terminal.
    if (posix_spawn_file_actions_addopen(&actions, 0, &name, openFlags(.{ .ACCMODE = .RDWR }), 0) != 0 or
        posix_spawn_file_actions_adddup2(&actions, 0, 1) != 0 or posix_spawn_file_actions_adddup2(&actions, 0, 2) != 0 or
        posix_spawnattr_setflags(&attr, POSIX_SPAWN_SETSID) != 0) return error.SpawnFailed;
    if (cwd) |dir| {
        const c_dir = try allocator.dupeSentinel(u8, dir, 0);
        if (posix_spawn_file_actions_addchdir_np(&actions, c_dir) != 0) return error.SpawnFailed;
    }
    var pid: c_int = 0;
    switch (posix_spawnp(&pid, c_argv[0].?, &actions, &attr, c_argv, @ptrCast(env.items.ptr))) {
        0 => {},
        @intFromEnum(std.c.E.NOENT) => return error.FileNotFound,
        @intFromEnum(std.c.E.ACCES) => return error.AccessDenied,
        else => return error.SpawnFailed,
    }
    const pty = try std.heap.smp_allocator.create(Pty);
    pty.* = .{ .master = master, .wake = wake, .pid = pid };
    return pty;
}

/// Blocks for the next chunk, at most 64 KiB: a C host passes a buffer that large, since status 3
/// re-runs the read and loses the chunk. None once the child has exited or the pty is closed.
pub fn ptyRead(allocator: Allocator, pty: *Pty) !?[]const u8 {
    if (!pty.enter()) return null;
    defer pty.leave();
    const buffer = try allocator.alloc(u8, 64 * 1024);
    while (true) {
        if (!pty.wait(std.c.POLL.IN)) return null;
        const n = std.c.read(pty.master, buffer.ptr, buffer.len);
        if (n > 0) return buffer[0..@intCast(n)];
        // EIO on the master once the child exits; EAGAIN and EINTR wait again.
        if (n == 0 or (std.c.errno(n) != .AGAIN and std.c.errno(n) != .INTR)) return null;
    }
}

/// Blocks while the pty's buffer is full, until the child reads.
pub fn ptyWrite(_: Allocator, pty: *Pty, data: []const u8) !void {
    if (!pty.enter()) return error.Closed;
    defer pty.leave();
    var rest = data;
    while (rest.len > 0) {
        // Close wakes a write blocked on a full buffer, as it does a read.
        if (!pty.wait(std.c.POLL.OUT)) return error.Closed;
        const n = std.c.write(pty.master, rest.ptr, rest.len);
        if (n < 0) {
            if (std.c.errno(n) == .INTR or std.c.errno(n) == .AGAIN) continue;
            return error.WriteFailed;
        }
        rest = rest[@intCast(n)..];
    }
}

pub fn ptyResize(_: Allocator, pty: *Pty, cols: i64, rows: i64) void {
    setSize(pty.master, cols, rows);
}

/// The name of the foreground process group's leader (the running command, or the shell itself).
pub fn ptyForeground(allocator: Allocator, pty: *Pty) !?[]const u8 {
    const path = try std.fmt.allocPrintSentinel(allocator, "/proc/{d}/comm", .{tcgetpgrp(pty.master)}, 0);
    // A /proc file reports size 0, so it is read with plain calls rather than readFileAlloc.
    const fd = std.c.open(path, .{ .CLOEXEC = true });
    if (fd < 0) return null;
    defer _ = std.c.close(fd);
    const buffer = try allocator.alloc(u8, 256);
    const n = std.c.read(fd, buffer.ptr, buffer.len);
    if (n <= 0) return null;
    return try decoded(allocator, std.mem.trim(u8, buffer[0..@intCast(n)], " \r\n"));
}

/// Hangs up the child's process group, as closing a terminal does, and reaps the child: its exit code,
/// or None when a signal ended it. A child still running 2 s after the hangup is killed.
pub fn ptyClose(_: Allocator, pty: *Pty) ?i64 {
    // The eventfd stays readable, so a read blocked now or starting later returns None.
    const one: u64 = 1;
    _ = std.c.write(pty.wake, std.mem.asBytes(&one), @sizeOf(u64));
    _ = std.c.kill(-pty.pid, .HUP);
    var status: c_int = 0;
    var reaped = std.c.waitpid(pty.pid, &status, std.posix.W.NOHANG);
    var waited: usize = 0;
    while (reaped == 0) : (waited += 1) {
        if (waited == 200) {
            _ = std.c.kill(-pty.pid, .KILL);
            reaped = std.c.waitpid(pty.pid, &status, 0);
            break;
        }
        std.Io.Clock.Duration.sleep(.{ .clock = .awake, .raw = .fromMilliseconds(10) }, io()) catch {};
        reaped = std.c.waitpid(pty.pid, &status, std.posix.W.NOHANG);
    }
    pty.leave();
    // A failed wait (for example ECHILD when SIGCHLD is ignored) leaves no status to report.
    return if (reaped == pty.pid and status & 0x7f == 0) (status >> 8) & 0xff else null;
}
