//! One managed worker per native session. Every owner is reaped before its arena is freed.
const std = @import("std");
const p = @import("run_process.zig");
const Diagnostic = p.Diagnostic;
const A = std.mem.Allocator;
const Fail = anyerror;
const Worker = struct {
    arena: std.heap.ArenaAllocator,
    child: std.process.Child,
    errors_file: std.Io.File = undefined,
    errors: ?std.Io.Future(Fail![]const u8) = null,
    served: usize = 0,
    last: std.Io.Clock.Timestamp,
};
var current: ?*Worker = null;
var gate: std.Io.Mutex = .init;
var idle_task: ?std.Io.Future(void) = null;
var closed = false;

fn stderrRead(w: *Worker, io: std.Io) Fail![]const u8 {
    var data: std.ArrayList(u8) = .empty;
    var buffer: [8192]u8 = undefined;
    while (true) {
        const n = w.errors_file.readStreaming(io, &.{&buffer}) catch |e| switch (e) {
            error.EndOfStream => break,
            else => return e,
        };
        if (n == 0) break;
        const keep = @min(n, 1048577 - data.items.len);
        try data.appendSlice(w.arena.allocator(), buffer[0..keep]);
    }
    return data.items;
}
fn retire(io: std.Io) void {
    const w = current orelse return;
    current = null;
    if (w.child.id) |pid| _ = std.os.linux.kill(-pid, .KILL);
    w.child.kill(io);
    if (w.errors) |*task| _ = task.cancel(io) catch {};
    w.errors_file.close(io);
    if (w.child.stdin) |f| f.close(io);
    if (w.child.stdout) |f| f.close(io);
    if (w.child.stderr) |f| f.close(io);
    w.arena.deinit();
    std.heap.page_allocator.destroy(w);
}
fn idle(io: std.Io) void {
    while (true) {
        std.Io.sleep(io, .fromSeconds(60), .awake) catch return;
        if (!gate.tryLock()) continue;
        defer gate.unlock(io);
        if (closed) return;
        if (current) |w| if (w.last.durationTo(.now(io, .awake)).raw.nanoseconds > 600 * std.time.ns_per_s) retire(io);
    }
}
pub fn deinit(io: std.Io) void {
    if (idle_task) |*task| _ = task.cancel(io);
    idle_task = null;
    gate.lockUncancelable(io);
    defer gate.unlock(io);
    closed = true;
    retire(io);
}
const Line = union(enum) { text: []const u8, failed: Diagnostic };
fn readLine(a: A, io: std.Io, file: std.Io.File, request: ?[]const u8, input: ?std.Io.File, done: *std.Io.Event) Fail![]const u8 {
    defer done.set(io);
    if (request) |text| try input.?.writeStreamingAll(io, text);
    var buffer: [8192]u8 = undefined;
    var reader = file.readerStreaming(io, &buffer);
    var writer: std.Io.Writer.Allocating = .init(a);
    _ = try reader.interface.streamDelimiterLimit(&writer.writer, '\n', .limited(8 * 1048576));
    if ((try reader.interface.takeByte()) != '\n') return error.EndOfStream;
    return writer.written();
}
fn exchange(a: A, io: std.Io, w: *Worker, request: ?[]const u8, seconds: i64, phase: []const u8) !Line {
    var done: std.Io.Event = .unset;
    var task = try io.concurrent(readLine, .{ a, io, w.child.stdout.?, request, w.child.stdin, &done });
    const deadline: std.Io.Timeout = .{ .deadline = .fromNow(io, .{ .raw = .fromSeconds(seconds), .clock = .awake }) };
    while (!done.isSet()) done.waitTimeout(io, deadline) catch |e| {
        if (e == error.Timeout and std.Io.Clock.Timestamp.compare(.now(io, .awake), .lt, deadline.toTimestamp(io).?)) continue;
        // Killing first releases any inherited pipe handles before the reader is joined.
        if (w.child.id) |pid| _ = std.os.linux.kill(-pid, .KILL);
        _ = task.await(io) catch {};
        return .{ .failed = .{ .code = if (request == null) "RunStartupFailure" else "RunTimeout", .message = try std.fmt.allocPrint(a, "The {s} exceeded its time limit of {d} s.", .{ phase, seconds }) } };
    };
    return .{ .text = task.await(io) catch |e| {
        return .{ .failed = .{ .code = if (e == error.WriteFailed) "RunIoFailure" else if (e == error.StreamTooLong) "RunOutputTooLarge" else "RunInvalidResult", .message = @errorName(e) } };
    } };
}
pub const Outcome = union(enum) { completed: []const u8, crashed: []const u8, failed: Diagnostic };
pub fn run(a: A, io: std.Io, worker_assembly: []const u8, assembly: []const u8, input: []const u8) Fail!Outcome {
    gate.lockUncancelable(io);
    defer gate.unlock(io);
    errdefer retire(io);
    if (closed) return .{ .failed = .{ .code = "RunStartupFailure", .message = "The session's runner worker is closed." } };
    if (current) |w| if (w.served >= 64) retire(io);
    if (current == null) {
        const w = try std.heap.page_allocator.create(Worker);
        w.* = .{ .arena = .init(std.heap.page_allocator), .child = std.process.spawn(io, .{ .argv = &.{ @import("run_csharp_build.zig").dotnet, worker_assembly }, .cwd = .{ .path = std.fs.path.dirname(worker_assembly).? }, .stdin = .pipe, .stdout = .pipe, .stderr = .pipe, .pgid = 0 }) catch |e| {
            std.heap.page_allocator.destroy(w);
            return .{ .failed = .{ .code = "RunStartupFailure", .message = @errorName(e) } };
        }, .last = .now(io, .awake) };
        current = w;
        w.errors_file = w.child.stderr.?;
        w.child.stderr = null;
        w.errors = io.concurrent(stderrRead, .{ w, io }) catch |e| {
            retire(io);
            return .{ .failed = .{ .code = "RunStartupFailure", .message = @errorName(e) } };
        };
        const ready = try exchange(a, io, w, null, 30, "runner worker startup");
        if (ready == .failed) {
            const failure = ready.failed;
            retire(io);
            return .{ .failed = .{ .code = "RunStartupFailure", .message = failure.message } };
        }
        if (!std.mem.eql(u8, std.mem.trimEnd(u8, ready.text, "\r"), "[\"ready\"]")) {
            retire(io);
            return .{ .failed = .{ .code = "RunStartupFailure", .message = "The runner worker did not announce readiness." } };
        }
        if (idle_task == null) idle_task = try io.concurrent(idle, .{io});
    }
    const w = current.?;
    w.served += 1;
    w.last = .now(io, .awake);
    var request: std.Io.Writer.Allocating = .init(a);
    try request.writer.writeAll("[\"run\",");
    try @import("root").writeString(&request.writer, assembly);
    try request.writer.writeByte(',');
    try @import("root").writeString(&request.writer, try std.fs.path.join(a, &.{ std.fs.path.dirname(assembly).?, "Generated.pdb" }));
    try request.writer.writeByte(',');
    try @import("root").writeString(&request.writer, input);
    try request.writer.writeAll("]\n");
    const reply = try exchange(a, io, w, request.written(), 5, "pure function");
    if (reply == .failed) {
        const failure = reply.failed;
        if (std.mem.eql(u8, failure.code, "RunInvalidResult")) {
            if (w.child.id) |pid| _ = std.os.linux.kill(-pid, .KILL);
            w.child.kill(io);
            const errors = if (w.errors) |*task| try a.dupe(u8, task.await(io) catch |e| @errorName(e)) else "";
            retire(io);
            return .{ .crashed = errors };
        }
        retire(io);
        return .{ .failed = failure };
    }
    const parsed = std.json.parseFromSliceLeaky(std.json.Value, a, reply.text, .{}) catch {
        retire(io);
        return .{ .failed = .{ .code = "RunInvalidResult", .message = "The runner worker did not answer with one result line." } };
    };
    if (parsed == .array and parsed.array.items.len >= 2 and parsed.array.items[0] == .string and parsed.array.items[1] == .string) {
        const items = parsed.array.items;
        if (std.mem.eql(u8, items[0].string, "ok") and items.len == 2) return .{ .completed = items[1].string };
        if (std.mem.eql(u8, items[0].string, "crash") and items.len == 3 and items[2] == .string) {
            const errors = items[2].string;
            retire(io);
            return .{ .crashed = errors };
        }
        if (std.mem.eql(u8, items[0].string, "refused") and items.len == 2) {
            const errors = items[1].string;
            retire(io);
            return .{ .crashed = errors };
        }
    }
    retire(io);
    return .{ .failed = .{ .code = "RunInvalidResult", .message = "The runner worker did not answer with one result line." } };
}
