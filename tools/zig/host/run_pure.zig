//! runPure's Zig backend: the existing lowering and emitter, with isolated compiled runners.
const std = @import("std");
const model = @import("model_reader");
const emitter = @import("zig_emitter");
const q = @import("query.zig");
const values = @import("run_value.zig");
const processes = @import("run_process.zig");
const m = @import("materialize.zig");
const root = @import("root");

fn backendName(r: *q.Request, name: []const u8) ![]const u8 {
    if (!m.same(name, "csharp") and !m.same(name, "zig")) return r.fail("Unknown backend '{s}'; expected \"csharp\" or \"zig\".", .{name});
    return name;
}

fn backend(r: *q.Request) ![]const u8 {
    if (r.root.get("backend")) |value| {
        if (value != .string) return r.fail("backend must be \"csharp\" or \"zig\".", .{});
        return backendName(r, try q.readable(r, value.string));
    }
    const path = try std.mem.concat(r.a, u8, &.{ r.snapshot_path, ".backends.json" });
    const text = root.readText(r.a, path) catch |e| switch (e) {
        error.FileNotFound, error.NotDir => return r.fail("The workspace declares no target backend. Create {s} with {{\"targets\": [\"csharp\"]}} (or list \"zig\"), or name the backend on the request.", .{path}),
        else => {
            r.rejection = .{ .code = "SessionError", .message = @errorName(e) };
            return error.Rejected;
        },
    };
    const parsed = std.json.parseFromSliceLeaky(std.json.Value, r.a, text, .{ .duplicate_field_behavior = .use_last }) catch |e|
        return r.fail("{s} is not valid JSON: {s}", .{ path, @errorName(e) });
    const targets = if (parsed == .object) parsed.object.get("targets") else null;
    if (targets == null or targets.? != .array or targets.?.array.items.len == 0) return r.fail("{s} must be an object with a non-empty \"targets\" array.", .{path});
    const names = try r.a.alloc([]const u8, targets.?.array.items.len);
    for (targets.?.array.items, names, 0..) |item, *name, i| {
        name.* = if (item == .string) item.string else "";
        for (names[0..i]) |prior| if (m.same(prior, name.*)) return r.fail("{s} lists a backend twice.", .{path});
    }
    for (names) |name| _ = try backendName(r, name);
    return names[0];
}

fn hostSources(r: *q.Request, input: *values.Input) !void {
    if (input.host_sources.len > 16) return values.diagnostic(r, input, "RunHostSourceInvalid", "At most 16 host source files are accepted.");
    const texts = try r.a.alloc([]const u8, input.host_sources.len);
    for (input.host_sources, texts) |path, *text| {
        const full = std.fs.path.resolveAlloc(r.a, &.{ std.fs.path.dirname(r.snapshot_path).?, path }) catch
            return values.diagnostic(r, input, "RunHostSourceInvalid", try std.fmt.allocPrint(r.a, "Host source '{s}' cannot be read.", .{path}));
        const stat = std.Io.Dir.cwd().statFile(root.host_io, full, .{}) catch null;
        if (!std.ascii.eqlIgnoreCase(std.fs.path.extension(full), ".cs") or stat == null or stat.?.kind != .file)
            return values.diagnostic(r, input, "RunHostSourceInvalid", try std.fmt.allocPrint(r.a, "Host source '{s}' is not an existing .cs file.", .{path}));
        if (stat.?.size > 1048576) return values.diagnostic(r, input, "RunHostSourceInvalid", try std.fmt.allocPrint(r.a, "Host source '{s}' exceeds 1 MiB.", .{path}));
        const bytes = std.Io.Dir.cwd().readFileAlloc(root.host_io, full, r.a, .limited(1048576)) catch return values.diagnostic(r, input, "RunHostSourceInvalid", try std.fmt.allocPrint(r.a, "Host source '{s}' cannot be read.", .{path}));
        text.* = try @import("managed_text.zig").decode(r.a, bytes);
    }
    input.host_source_texts = texts;
}

fn loweringError(r: *q.Request, failure: []const u8) !void {
    const case = failure[0 .. std.mem.indexOfScalar(u8, failure, ':') orelse failure.len];
    r.rejection = .{ .code = "ExecutionLoweringFailure", .message = case };
    if (!m.same(case, "AmbiguousEffectOrder")) return error.Rejected;
    const detail = failure[case.len + 2 ..];
    for (r.typedModel().functions()) |function| {
        const id = q.idOf(function);
        if (!std.mem.startsWith(u8, detail, id) or detail.len <= id.len or detail[id.len] != ' ') continue;
        const pair = detail[id.len + 1 ..];
        const nodes = model.typedBodyNodes(r.a, function);
        for (nodes) |first| {
            const first_id = q.idOf(first);
            if (!std.mem.startsWith(u8, pair, first_id) or pair.len <= first_id.len or pair[first_id.len] != ' ') continue;
            const second_id = pair[first_id.len + 1 ..];
            const operands = try r.a.alloc(model.Value, 2);
            operands[0] = try loweringOperand(r, first_id, nodes);
            operands[1] = try loweringOperand(r, second_id, nodes);
            const details = try m.object(r.a, &.{
                try m.member(r.a, "cause", try m.string(r.a, case)),
                try m.member(r.a, "functionId", try m.string(r.a, id)),
                try m.member(r.a, "function", try m.string(r.a, q.nameOf(function))),
                try m.member(r.a, "operands", .{ .array = operands }),
                try m.member(r.a, "repair", try m.object(r.a, &.{ try m.member(r.a, "kind", try m.string(r.a, "Bind")), try m.member(r.a, "requiresExplicitOrder", .json_true) })),
            });
            var encoded: std.Io.Writer.Allocating = .init(r.a);
            try m.write(&encoded.writer, details);
            r.rejection.message = try std.fmt.allocPrint(r.a, "Function '{s}' has unordered observable operations. Sequence them with Bind in the intended order, then simulate and review the edit.", .{q.nameOf(function)});
            r.rejection.details = encoded.written();
            return error.Rejected;
        }
    }
    return error.Rejected;
}

fn loweringOperand(r: *q.Request, id: []const u8, nodes: []const model.Model) !model.Value {
    var kind: []const u8 = "unknown";
    var name = id;
    for (nodes) |node| if (m.same(q.idOf(node), id)) {
        const tag = model.utf8(node.kind());
        kind = try r.a.dupe(u8, tag);
        @constCast(kind)[0] = std.ascii.toUpper(kind[0]);
        name = kind;
        if (m.same(tag, "directCall")) {
            const target = model.utf8(node.reference("targetFunctionId"));
            for (r.typedModel().functions()) |function| if (m.same(q.idOf(function), target)) {
                name = try std.mem.concat(r.a, u8, &.{ "Call to ", q.nameOf(function) });
                break;
            };
        } else if (m.same(tag, "capabilityInvoke")) {
            const operation_id = model.utf8(node.reference("operationId"));
            for (r.typedModel().capabilities()) |capability| for (capability.operations()) |operation| if (m.same(q.idOf(operation), operation_id)) {
                name = try std.mem.concat(r.a, u8, &.{ q.nameOf(capability), ".", q.nameOf(operation) });
            };
        }
        break;
    };
    return m.object(r.a, &.{ try m.member(r.a, "nodeId", try m.string(r.a, id)), try m.member(r.a, "kind", try m.string(r.a, kind)), try m.member(r.a, "displayName", try m.string(r.a, name)) });
}

pub fn lowered(r: *q.Request, entry: ?[]const u8) ![]const u8 {
    return switch (model.typedExecutionForEntry(r.a, r.typedModel(), entry)) {
        .ir => |ir| ir,
        .projection => |case| {
            r.rejection = .{ .code = "SemanticProjectionFailure", .message = case };
            return error.Rejected;
        },
        .lowering => |failure| {
            try loweringError(r, failure);
            unreachable;
        },
    };
}

fn runner(r: *q.Request, input: *values.Input, reach: []const u8) ![]const u8 {
    const first = switch (model.typedExecutionForEntry(r.a, r.typedModel(), null)) {
        .ir => |ir| ir,
        else => reach,
    };
    const initial = try emitter.runner(r.a, first, null);
    const emitted = switch (initial) {
        .source => return initial.source,
        .unsupported => try emitter.runner(r.a, reach, input.function_id),
    };
    return switch (emitted) {
        .source => |source| source,
        .unsupported => |problems| {
            try values.diagnostic(r, input, "RunBackendUnsupported", try std.fmt.allocPrint(r.a, "The Zig backend does not yet support: {s}.", .{try std.mem.join(r.a, ", ", problems[0..@min(problems.len, 8)])}));
            unreachable;
        },
    };
}

/// The F# backend installs its virtual clock only for the concurrency/task runtime.
pub fn concurrentValue(value: std.json.Value) bool {
    switch (value) {
        .array => |items| for (items.items) |item| {
            if (concurrentValue(item)) return true;
        },
        .object => |object| {
            if (object.get("kind")) |kind| if (kind == .string) {
                for ([_][]const u8{ "parallelAll", "cancellationRequested", "cancellationShield", "task", "oneShot", "taskScope", "taskStart", "taskWait", "taskCancel", "oneShotCreate", "oneShotComplete", "oneShotWait" }) |name|
                    if (m.same(kind.string, name)) return true;
            };
            for (object.values()) |item| if (concurrentValue(item)) return true;
        },
        else => {},
    }
    return false;
}

pub fn run(r: *q.Request, out: *std.Io.Writer) !void {
    var input = try values.decode(r);
    const selected = try backend(r);
    try r.ensureUsable();
    if (try root.verifyPersisted(r.a)) |rejection| {
        r.rejection = rejection;
        return error.Rejected;
    }
    try values.bounded(r, &input);
    if (!root.host_sources_allowed and (input.host_sources.len > 0 or input.has_host_binding))
        return values.diagnostic(r, &input, "RunHostSourcesDisabled", "Host sources and hostCapability bindings compile and run arbitrary C# with the user's permissions, so they are disabled unless the session was started with --allow-host-sources. Use fakeCapability bindings, or ask the operator to enable host sources.");
    try hostSources(r, &input);
    try values.validate(r, &input);
    if (m.same(selected, "csharp")) {
        const reach = try lowered(r, input.function_id);
        return @import("run_csharp.zig").run(r, &input, reach, concurrentValue(try std.json.parseFromSliceLeaky(std.json.Value, r.a, reach, .{})), out);
    }
    if (input.host_sources.len > 0) return values.diagnostic(r, &input, "RunBackendUnsupported", "The Zig backend does not yet support host sources.");
    const reach = try lowered(r, input.function_id);
    const source = try runner(r, &input, reach);
    if (input.waiting_responses and !concurrentValue(try std.json.parseFromSliceLeaky(std.json.Value, r.a, reach, .{})))
        return values.diagnostic(r, &input, "RunBindingShapeMismatch", "awaitCancellation and delayMillis responses need a program that uses ParallelAll, a task construct or CancellationRequested.");
    const built = processes.build(r.a, root.host_io, source) catch |e|
        return values.diagnostic(r, &input, "RunIoFailure", @errorName(e));
    const executable = switch (built) {
        .executable => |path| path,
        .failed => |failure| return values.diagnostic(r, &input, failure.code, failure.message),
    };
    const tokens = try values.tokens(r, &input);
    var argv: std.ArrayList([]const u8) = .empty;
    try argv.appendSlice(r.a, &.{ executable, emitter.functionName(r.a, input.function_id) });
    try argv.appendSlice(r.a, tokens);
    switch (try processes.launch(r.a, root.host_io, argv.items, std.fs.path.dirname(executable).?, "pure function", 5)) {
        .failed => |failure| return values.diagnostic(r, &input, failure.code, failure.message),
        .completed => |result| {
            if (!result.term.success()) return values.crash(r, &input, switch (result.term) {
                .exited => |code| @intCast(code),
                .signal => |signal| 128 + @as(i32, @intCast(@backingInt(signal))),
                else => -1,
            }, result.stderr);
            return values.envelope(r, &input, result.stdout, out);
        },
    }
}

test "runner dispatch name preserves arbitrary UTF-8 stable IDs" {
    const name = emitter.functionName(std.testing.allocator, "entry/日本");
    defer std.testing.allocator.free(name);
    try std.testing.expectEqualStrings("f_entry_2f__65e5__672c_", name);
}
