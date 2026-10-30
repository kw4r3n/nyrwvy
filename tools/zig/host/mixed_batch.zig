//! Ordered by-name workflows publish into request-local scratch models, then one real proposal.
const std = @import("std");
const model = @import("model_reader");
const query = @import("query.zig");
const recipe = @import("recipe.zig");
const m = @import("materialize.zig");
const proposals = @import("proposals.zig");
const Writer = std.Io.Writer;
const Step = struct { kind: []const u8, op: []const u8, fields: []const []const u8, required: []const []const u8, run: *const fn (*query.Request, *Writer) anyerror!void };
const steps = [_]Step{
    .{ .kind = "createType", .op = "simulateCreateTypeByName", .fields = &.{ "kind", "displayName", "exported", "typeParameters", "typeKind" }, .required = &.{ "kind", "displayName", "typeKind" }, .run = @import("declarations.zig").createType },
    .{ .kind = "createTypes", .op = "simulateCreateTypesByName", .fields = &.{ "kind", "types" }, .required = &.{ "kind", "types" }, .run = @import("declarations.zig").createType },
    .{ .kind = "createFunction", .op = "simulateCreateFunctionByName", .fields = &.{ "kind", "displayName", "exported", "mustNotWait", "typeParameters", "parameters", "resultType", "requiredEffects", "body", "bodyFile" }, .required = &.{ "kind", "displayName", "parameters", "resultType", "requiredEffects" }, .run = query.simulateCreateFunctionByName },
    .{ .kind = "createFunctions", .op = "simulateCreateFunctionsByName", .fields = &.{ "kind", "functions", "functionsFile" }, .required = &.{"kind"}, .run = @import("function_batch.zig").create },
    .{ .kind = "operations", .op = "simulateOperationsByName", .fields = &.{ "kind", "operations" }, .required = &.{ "kind", "operations" }, .run = @import("operation_engine.zig").simulateRequest },
    .{ .kind = "replaceBody", .op = "simulateReplaceBodyByName", .fields = &.{ "kind", "function", "replacement", "replacementFile", "expectedResultNodeId" }, .required = &.{ "kind", "function" }, .run = query.simulateReplaceBodyByName },
    .{ .kind = "replaceTarget", .op = "simulateReplaceTargetByName", .fields = &.{ "kind", "function", "targetNodeId", "replacement", "replacementFile" }, .required = &.{ "kind", "function", "targetNodeId" }, .run = query.simulateReplaceTargetByName },
};

pub fn simulate(r: *query.Request, out: *Writer) anyerror!void {
    if (try query.fieldProblem(r.a, r.line, &.{}, r.root, "simulateBatchByName request", &.{ "id", "op", "steps", "stepsFile", "structuredReview", "detail", "expectedSnapshotId" }, &.{ "id", "op" })) |message| return r.fail("{s}", .{message});
    var decoder: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line) };
    const input = try @import("recipe_file.zig").jsonInput(&decoder, "steps", "stepsFile");
    if (input.value != .array) return r.fail("steps must be an array.", .{});
    if (input.value.array.items.len == 0) return r.fail("steps must list at least one step.", .{});
    const expected = if (r.root.get("expectedSnapshotId") != null) try decoder.stableId("expectedSnapshotId", try decoder.requiredString(.{ .object = r.root }, "expectedSnapshotId")) else r.snapshot_id;
    try r.ensureUsable();
    const sources = try @import("recipe_file.zig").rawArrayItems(r.a, input.source);
    const captures = try r.a.alloc(query.Capture, sources.len);
    var candidate = r.model;
    for (input.value.array.items, sources, captures, 0..) |item, source, *capture, index| {
        capture.* = .{};
        var scratch = r.*;
        scratch.model = candidate;
        scratch.line = source;
        scratch.root = if (item == .object) item.object else .empty;
        scratch.capture = capture;
        scratch.snapshot_id = model.utf8(model.decoded(model.property(candidate, "snapshotId").?));
        if (index > 0) {
            var encoded: Writer.Allocating = .init(r.a);
            try m.write(&encoded.writer, candidate);
            scratch.snapshot_fingerprint = model.readDocument(r.a, encoded.written()).fingerprints.?.snapshot;
        }
        if (item == .object) try kindLookup(r, source);
        const label = if (item == .object) blk: {
            const kind = item.object.get("kind") orelse break :blk "<unknown>";
            break :blk if (kind == .string) kind.string else "<unknown>";
        } else "<unknown>";
        _ = try query.readable(r, label);
        runStep(&scratch, item, label, if (index == 0) expected else scratch.snapshot_id) catch |err| {
            if (err != error.Rejected) return err;
            if (m.same(scratch.rejection.code, "SessionError")) {
                r.rejection = scratch.rejection;
                return err;
            }
            return rejectStep(r, &scratch, index, label);
        };
        candidate = capture.candidate;
    }
    try proposals.publishBatch(r, out, captures, candidate);
}

// JsonElement looks for the last kind before ensureFields enumerates every property name.
fn kindLookup(r: *query.Request, source: []const u8) !void {
    if (!query.masked) return;
    var scanner = std.json.Scanner.initCompleteInput(r.a, source);
    defer scanner.deinit();
    var depth: usize = 0;
    var name_next = false;
    var invalid_after = false;
    while (true) {
        const token = try scanner.nextAlloc(r.a, .alloc_always);
        var ended = false;
        switch (token) {
            .end_of_document => break,
            .object_begin, .array_begin => {
                depth += 1;
                name_next = depth == 1;
            },
            .object_end, .array_end => {
                depth -= 1;
                ended = depth == 1;
            },
            .allocated_string => |text| {
                if (depth == 1 and name_next) {
                    name_next = false;
                    if (m.same(text, "kind")) {
                        invalid_after = false;
                    } else if (std.mem.indexOf(u8, text, query.lone_mark) != null) {
                        invalid_after = true;
                    }
                } else ended = depth == 1;
            },
            else => ended = depth == 1,
        }
        if (ended) name_next = true;
    }
    if (invalid_after) _ = try query.readable(r, query.lone_mark);
}

fn runStep(r: *query.Request, item: std.json.Value, label: []const u8, expected: []const u8) anyerror!void {
    if (item != .object) return r.fail("A step must be a JSON object.", .{});
    const step = for (steps) |step| {
        if (m.same(step.kind, label)) break step;
    } else return r.fail("A step needs a kind: createType, createTypes, createFunction, createFunctions, operations, replaceBody, replaceTarget.", .{});
    try query.lookable(r, r.root);
    if (try query.fieldProblem(r.a, r.line, &.{}, r.root, try std.fmt.allocPrint(r.a, "{s} step", .{label}), step.fields, step.required)) |message| return r.fail("{s}", .{message});
    // Keep the raw step text for nested duplicate checks and file/recipe spelling.
    var root = try r.root.clone(r.a);
    _ = root.swapRemove("kind");
    try root.put(r.a, "id", .{ .string = "" });
    try root.put(r.a, "op", .{ .string = step.op });
    try root.put(r.a, "expectedSnapshotId", .{ .string = expected });
    r.root = root;
    var discarded: Writer.Allocating = .init(r.a);
    try step.run(r, &discarded.writer);
}

fn rejectStep(r: *query.Request, scratch: *query.Request, index: usize, kind: []const u8) anyerror {
    const inner = scratch.rejection;
    var encoded: Writer.Allocating = .init(r.a);
    const w = &encoded.writer;
    try w.print(",\"stepIndex\":{d},\"stepKind\":", .{index});
    try @import("root").writeString(w, kind);
    try w.writeAll(",\"stepError\":{\"code\":");
    try @import("root").writeString(w, inner.code);
    try w.writeAll(",\"message\":");
    try @import("root").writeString(w, inner.message);
    if (inner.details.len > 0) try w.print(",\"details\":{s}", .{inner.details});
    try w.writeAll(inner.fields);
    try w.writeByte('}');
    var message = inner.message;
    if (inner.details.len > 0) {
        const details = try model.parseValue(r.a, inner.details);
        const diagnostics = model.arrayItems(details, "diagnostics");
        if (diagnostics.len > 0) message = try std.fmt.allocPrint(r.a, "{s} {s}: {s}", .{ message, model.utf8(model.decoded(model.property(diagnostics[0], "code").?)), model.utf8(model.decoded(model.property(diagnostics[0], "message").?)) });
    }
    r.rejection = .{ .code = "BatchStepRejected", .message = try std.fmt.allocPrint(r.a, "Step {d} ({s}) was rejected: {s}", .{ index, kind, message }), .fields = encoded.written() };
    return error.Rejected;
}
