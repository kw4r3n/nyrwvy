//! Catchable termination kills owned groups, then cancels serving so their owners can reap them.
const std = @import("std");
const linux = std.os.linux;
var stopping: std.atomic.Value(bool) = .init(false);
// Requests serialize foreground launches; only the session worker can coexist with one.
pub const Owner = enum { foreground, worker };
var groups = [_]std.atomic.Value(linux.pid_t){ .init(0), .init(0) };
var registry_gate: std.Io.Mutex = .init;

pub fn register(io: std.Io, owner: Owner, pid: linux.pid_t) void {
    registry_gate.lockUncancelable(io);
    defer registry_gate.unlock(io);
    groups[@backingInt(owner)].store(pid, .release);
    // A spawn that raced the monitor must not introduce a surviving group after shutdown.
    if (isStopping()) _ = linux.kill(-pid, .KILL);
}

/// Remove before reaping, so the monitor cannot signal a recycled group ID.
pub fn unregister(io: std.Io, owner: Owner) void {
    registry_gate.lockUncancelable(io);
    defer registry_gate.unlock(io);
    groups[@backingInt(owner)].store(0, .release);
}

fn killGroups(io: std.Io) void {
    registry_gate.lockUncancelable(io);
    defer registry_gate.unlock(io);
    for (&groups) |*group| {
        const pid = group.load(.acquire);
        if (pid != 0) _ = linux.kill(-pid, .KILL);
    }
}

fn terminate(_: linux.SIG) callconv(.c) void {
    // Signal context must not allocate, lock, or enter the Io runtime.
    stopping.store(true, .release);
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
    const action: linux.Sigaction = .{ .handler = .{ .handler = terminate }, .mask = linux.sigemptyset(), .flags = 0 };
    var previous_term: linux.Sigaction = undefined;
    if (linux.errno(linux.sigaction(.TERM, &action, &previous_term)) != .SUCCESS) return error.SignalHandlerUnavailable;
    defer _ = linux.sigaction(.TERM, &previous_term, null);
    var previous_int: linux.Sigaction = undefined;
    if (linux.errno(linux.sigaction(.INT, &action, &previous_int)) != .SUCCESS) return error.SignalHandlerUnavailable;
    defer _ = linux.sigaction(.INT, &previous_int, null);
    const Result = union(enum) { serving: anyerror!void, termination: std.Io.Cancelable!void };
    var buffer: [2]Result = undefined;
    var tasks = std.Io.Select(Result).init(io, &buffer);
    // Child groups are killed before joining, even when native work delays cancellation.
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
