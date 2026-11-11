//! Native C# runPure orchestration. Managed children only compile source or run generated code.
const std = @import("std");
const emitter = @import("csharp_emitter");
const q = @import("query.zig");
const v = @import("run_value.zig");
const m = @import("materialize.zig");
const p = @import("run_process.zig");
const build = @import("run_csharp_build.zig");
const worker = @import("run_csharp_worker.zig");
const root = @import("root");
const managed_text = @import("managed_text.zig");
var lifetime: std.mem.Allocator = undefined;
var worker_assembly: ?[]const u8 = null;
pub fn init(a: std.mem.Allocator, io: std.Io, env: *const std.process.Environ.Map) !void {
    lifetime = a;
    try build.init(a, io, env);
}
pub fn deinit(io: std.Io) void {
    worker.deinit(io);
    if (worker_assembly) |path| std.Io.Dir.cwd().deleteTree(io, std.fs.path.dirname(path).?) catch {};
    build.deinit(io);
}

fn profile(a: std.mem.Allocator, ir: std.json.Value, input: *v.Input) !std.json.Value {
    var bindings: std.ArrayList(std.json.Value) = .empty;
    var concurrent: std.ArrayList(std.json.Value) = .empty;
    for (emitter.arr(ir, "capabilities")) |cap| {
        const ops = emitter.arr(cap, "operations");
        for (ops) |op| try bindings.append(a, try emitter.object(a, &.{ "operationId", "kind" }, &.{ emitter.string(emitter.str(op, "id")), emitter.string("externalImplementation") }));
        if (ops.len == 0) continue;
        var sequential = false;
        for (input.arguments) |arg| if (arg.kind == .host and !arg.boolean and m.same(arg.type.capability, emitter.str(cap, "id"))) {
            sequential = true;
        };
        if (!sequential) try concurrent.append(a, emitter.string(emitter.str(cap, "id")));
    }
    return emitter.object(a, &.{ "capabilityBindings", "concurrentCapabilityIds" }, &.{ try emitter.array(a, bindings.items), try emitter.array(a, concurrent.items) });
}
fn ensureWorker(r: *q.Request, input: *v.Input) ![]const u8 {
    if (worker_assembly) |path| return path;
    const directory = try build.temporary(r.a, root.host_io, "nyrwvy-csharp-worker");
    errdefer std.Io.Dir.cwd().deleteTree(root.host_io, directory) catch {};
    const built = try build.uncached(r.a, root.host_io, directory, &.{.{ .name = "Worker.cs", .text = emitter.worker_source }});
    switch (built) {
        .failed => |d| {
            try v.diagnostic(r, input, "RunStartupFailure", try std.fmt.allocPrint(r.a, "The runner worker could not be built: {s}", .{d.message}));
            unreachable;
        },
        .assembly => |path| {
            worker_assembly = try lifetime.dupe(u8, path);
            return worker_assembly.?;
        },
    }
}
pub fn run(r: *q.Request, input: *v.Input, reach: []const u8, concurrent: bool, out: *std.Io.Writer) !void {
    return runChecked(r, input, reach, concurrent, out) catch |e| switch (e) {
        error.Rejected => return e,
        else => return v.diagnostic(r, input, "RunIoFailure", @errorName(e)),
    };
}
fn runChecked(r: *q.Request, input: *v.Input, reach: []const u8, concurrent: bool, out: *std.Io.Writer) !void {
    const ir = try std.json.parseFromSliceLeaky(std.json.Value, r.a, reach, .{});
    const artifact = switch (try emitter.emit(r.a, reach, try profile(r.a, ir, input))) {
        .artifact => |artifact| artifact,
        .failure => |failure| {
            r.rejection = .{ .code = failure.code, .message = failure.message };
            if (failure.details != .null) {
                var details: std.Io.Writer.Allocating = .init(r.a);
                try std.json.Stringify.value(failure.details, .{}, &details.writer);
                r.rejection.details = details.written();
            }
            return error.Rejected;
        },
    };
    if (input.waiting_responses and !concurrent) return v.diagnostic(r, input, "RunBindingShapeMismatch", "awaitCancellation and delayMillis responses need a program that uses ParallelAll, a task construct or CancellationRequested.");
    input.csharp_symbols = artifact.symbols;
    const source = try @import("run_csharp_runner.zig").source(r, input, concurrent);
    const directory = try build.temporary(r.a, root.host_io, "nyrwvy-pure-run");
    defer std.Io.Dir.cwd().deleteTree(root.host_io, directory) catch {};
    var sources: std.ArrayList(build.Source) = .empty;
    try sources.append(r.a, .{ .name = "Generated.cs", .text = artifact.source });
    for (input.host_source_texts, 0..) |text, index| {
        try sources.append(r.a, .{ .name = try std.fmt.allocPrint(r.a, "HostSource{d}.cs", .{index}), .text = text });
    }
    try sources.append(r.a, .{ .name = "Program.cs", .text = source });
    const assembly = switch (try build.build(r.a, root.host_io, directory, sources.items)) {
        .assembly => |path| path,
        .failed => |d| return v.diagnostic(r, input, d.code, d.message),
    };
    const input_path = try std.fs.path.join(r.a, &.{ directory, "input.txt" });
    try std.Io.Dir.cwd().writeFile(root.host_io, .{ .sub_path = input_path, .data = try std.mem.join(r.a, " ", try v.tokens(r, input)) });
    const stdout = if (input.host_sources.len > 0) raw: {
        switch (try p.launchBounded(r.a, root.host_io, &.{ build.dotnet, assembly, input_path }, directory, "pure function", 5, 4 * 1048577 + 4, &build.run_environ)) {
            .failed => |d| return v.diagnostic(r, input, d.code, d.message),
            .completed => |done| {
                const output = try managed_text.decode(r.a, done.stdout);
                const errors = try managed_text.decode(r.a, done.stderr);
                if (managed_text.units(output) > 1048576 or managed_text.units(errors) > 1048576) return v.diagnostic(r, input, "RunOutputTooLarge", "The process produced more than 1 MiB of output.");
                if (!done.term.success()) return v.csharpCrash(r, input, done.term, output, errors);
                break :raw output;
            },
        }
    } else managed: {
        switch (try worker.run(r.a, root.host_io, try ensureWorker(r, input), assembly, input_path)) {
            .failed => |d| return v.diagnostic(r, input, d.code, d.message),
            .crashed => |errors| return v.csharpCrash(r, input, null, "", try managed_text.decode(r.a, errors)),
            .completed => |output| break :managed output,
        }
    };
    if (managed_text.units(stdout) > 1048576) return v.diagnostic(r, input, "RunOutputTooLarge", "The process produced more than 1 MiB of output.");
    return v.envelope(r, input, stdout, out);
}
