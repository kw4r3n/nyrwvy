//! Catchable termination kills owned groups, then cancels serving so their owners can reap them.
const std = @import("std");
const os = @import("builtin").os.tag;
const windows = os == .windows;
const win = @import("windows_process.zig");
const linux = std.os.linux;
const Group = if (windows) std.os.windows.HANDLE else std.posix.pid_t;
var stopping: std.atomic.Value(bool) = .init(false);
pub const Owner = enum { foreground, worker };
var groups: [2]?Group = @splat(null);
var registry_gate: std.Io.Mutex = .init;

fn killGroup(group: Group) void {
    if (windows) win.kill(group) else std.posix.kill(-group, .KILL) catch {};
}
pub fn register(io: std.Io, owner: Owner, group: Group) void {
    registry_gate.lockUncancelable(io);
    defer registry_gate.unlock(io);
    const slot = &groups[@backingInt(owner)];
    std.debug.assert(slot.* == null);
    slot.* = group;
    if (isStopping()) killGroup(group);
}
/// Remove before reaping; closing a Windows job also kills descendants after a normal exit.
pub fn unregister(io: std.Io, owner: Owner) void {
    registry_gate.lockUncancelable(io);
    defer registry_gate.unlock(io);
    const slot = &groups[@backingInt(owner)];
    if (slot.*) |group| {
        if (windows) win.close(group) else killGroup(group);
        slot.* = null;
    }
}
pub fn kill(io: std.Io, owner: Owner) void {
    registry_gate.lockUncancelable(io);
    defer registry_gate.unlock(io);
    if (groups[@backingInt(owner)]) |group| killGroup(group);
}
fn killGroups(io: std.Io) void {
    registry_gate.lockUncancelable(io);
    defer registry_gate.unlock(io);
    for (groups) |group| if (group) |value| killGroup(value);
}
pub fn spawn(io: std.Io, owner: Owner, options: std.process.SpawnOptions) !std.process.Child {
    var configured = options;
    if (windows) configured.start_suspended = true else configured.pgid = 0;
    var child = try std.process.spawn(io, configured);
    errdefer child.kill(io);
    const group = if (windows) try win.create(child.id.?) else child.id.?;
    register(io, owner, group);
    errdefer unregister(io, owner);
    if (isStopping()) return error.Canceled;
    if (windows) try win.resumeChild(&child);
    return child;
}

const Darwin = struct {
    extern "c" fn waitid(c_uint, c_uint, *std.c.siginfo_t, c_int) c_int;
};
/// Observe exit without reaping, so the registry never retains a recycled POSIX group ID.
pub fn waitForExit(io: std.Io, child: *const std.process.Child) !void {
    if (windows) return win.waitForExit(io, child.id.?);
    if (os == .linux) {
        var info: linux.siginfo_t = undefined;
        while (true) switch (linux.errno(linux.waitid(.PID, child.id.?, &info, linux.W.EXITED | linux.W.NOWAIT, null))) {
            .SUCCESS => return,
            .INTR => try io.checkCancel(),
            else => return error.Unexpected,
        };
    } else {
        var info: std.c.siginfo_t = undefined;
        // Darwin sys/wait.h: P_PID=1, WEXITED=4, WNOWAIT=0x20.
        while (true) switch (std.posix.errno(Darwin.waitid(1, @intCast(child.id.?), &info, 4 | 0x20))) {
            .SUCCESS => return,
            .INTR => try io.checkCancel(),
            else => return error.Unexpected,
        };
    }
}
fn terminate(_: std.posix.SIG) callconv(.c) void {
    stopping.store(true, .release);
}
fn consoleTerminate(event: std.os.windows.DWORD) callconv(.winapi) std.os.windows.BOOL {
    switch (event) {
        0, 1, 2, 5, 6 => {
            stopping.store(true, .release);
            return .TRUE;
        },
        else => return .FALSE,
    }
}
pub fn isStopping() bool {
    return stopping.load(.acquire);
}
fn waitForTermination(io: std.Io) std.Io.Cancelable!void {
    while (!isStopping()) try std.Io.sleep(io, .fromMilliseconds(50), .awake);
    killGroups(io);
}
pub fn run(io: std.Io, comptime serve: fn (std.Io) anyerror!void) !void {
    stopping.store(false, .release);
    const Handlers = struct {
        var term: std.posix.Sigaction = undefined;
        var interrupt: std.posix.Sigaction = undefined;
    };
    var console_registered = false;
    if (windows) {
        console_registered = win.SetConsoleCtrlHandler(consoleTerminate, .TRUE).toBool();
        // Pipe-launched MCP sessions may have no console; jobs still cover abrupt termination.
        if (!console_registered and std.os.windows.GetLastError() != .INVALID_HANDLE) return error.SignalHandlerUnavailable;
    } else {
        const action: std.posix.Sigaction = .{ .handler = .{ .handler = terminate }, .mask = std.posix.sigemptyset(), .flags = 0 };
        std.posix.sigaction(.TERM, &action, &Handlers.term);
        std.posix.sigaction(.INT, &action, &Handlers.interrupt);
    }
    defer if (windows) {
        if (console_registered) _ = win.SetConsoleCtrlHandler(consoleTerminate, .FALSE);
    } else {
        std.posix.sigaction(.TERM, &Handlers.term, null);
        std.posix.sigaction(.INT, &Handlers.interrupt, null);
    };
    const Result = union(enum) { serving: anyerror!void, termination: std.Io.Cancelable!void };
    var buffer: [2]Result = undefined;
    var tasks = std.Io.Select(Result).init(io, &buffer);
    defer {
        stopping.store(true, .release);
        killGroups(io);
        tasks.cancelDiscard();
    }
    try tasks.concurrent(.serving, serve, .{io});
    try tasks.concurrent(.termination, waitForTermination, .{io});
    switch (try tasks.await()) {
        .serving => |result| try result,
        .termination => |result| try result,
    }
}

fn expectExitedBeforeReap(io: std.Io, pid: linux.pid_t) !void {
    for (0..200) |_| {
        var info = std.mem.zeroes(linux.siginfo_t);
        try std.testing.expectEqual(linux.E.SUCCESS, linux.errno(linux.waitid(.PID, pid, &info, linux.W.EXITED | linux.W.NOWAIT | linux.W.NOHANG, null)));
        if (info.fields.common.first.piduid.pid == pid) return;
        try std.Io.sleep(io, .fromMilliseconds(10), .awake);
    }
    return error.ChildSurvivedTermination;
}

test "termination kills an owned group before a CPU-bound serving task can unwind" {
    if (os != .linux) return error.SkipZigTest;
    const Stalled = struct {
        var entered: std.Io.Event = .unset;
        var release: std.atomic.Value(bool) = .init(false);
        var finished: std.atomic.Value(bool) = .init(false);
        var pid: linux.pid_t = undefined;
        fn host(io: std.Io) !void {
            try run(io, serve);
        }
        fn serve(io: std.Io) !void {
            var child = try std.process.spawn(io, .{ .argv = &.{ "/bin/sh", "-c", "trap '' TERM INT; exec sleep 60" }, .stdin = .ignore, .stdout = .ignore, .stderr = .ignore, .pgid = 0 });
            pid = child.id.?;
            register(io, .worker, pid);
            defer {
                unregister(io, .worker);
                _ = linux.kill(-pid, .KILL);
                child.kill(io);
                finished.store(true, .release);
            }
            entered.set(io);
            while (!release.load(.acquire)) std.atomic.spinLoopHint();
        }
    };
    const io = std.testing.io;
    defer stopping.store(false, .release);
    var running = try io.concurrent(Stalled.host, .{io});
    defer {
        Stalled.release.store(true, .release);
        _ = running.cancel(io) catch {};
    }
    try Stalled.entered.waitTimeout(io, .{ .duration = .{ .raw = .fromSeconds(3), .clock = .awake } });
    try std.testing.expectEqual(linux.E.SUCCESS, linux.errno(linux.kill(linux.getpid(), .TERM)));
    try expectExitedBeforeReap(io, Stalled.pid);
    try std.testing.expect(!Stalled.finished.load(.acquire));
    Stalled.release.store(true, .release);
    try running.await(io);
    try std.testing.expect(Stalled.finished.load(.acquire));
}

test "a child registered after shutdown is killed even after the monitor has finished" {
    if (os != .linux) return error.SkipZigTest;
    const io = std.testing.io;
    stopping.store(true, .release);
    defer stopping.store(false, .release);
    var child = try std.process.spawn(io, .{ .argv = &.{ "/bin/sleep", "60" }, .stdin = .ignore, .stdout = .ignore, .stderr = .ignore, .pgid = 0 });
    const pid = child.id.?;
    defer {
        unregister(io, .foreground);
        _ = linux.kill(-pid, .KILL);
        child.kill(io);
    }
    register(io, .foreground, pid);
    try expectExitedBeforeReap(io, pid);
}

test "owned children are registered before execution and reaped" {
    const io = std.testing.io;
    defer stopping.store(false, .release);
    const argv: []const []const u8 = if (windows) &.{ "cmd.exe", "/d", "/c", "exit 0" } else &.{ "/bin/sh", "-c", "exit 0" };
    var child = try spawn(io, .foreground, .{ .argv = argv, .stdin = .ignore, .stdout = .ignore, .stderr = .ignore });
    defer {
        unregister(io, .foreground);
        child.kill(io);
    }
    try std.testing.expect(groups[@backingInt(Owner.foreground)] != null);
    try waitForExit(io, &child);
    unregister(io, .foreground);
    try std.testing.expect((try child.wait(io)).success());
    try std.testing.expect(groups[@backingInt(Owner.foreground)] == null);
}
