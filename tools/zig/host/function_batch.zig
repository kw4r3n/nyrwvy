//! Ordered function builders share decoding, preflight and materialization with single creation.
const std = @import("std");
const model = @import("model_reader");
const query = @import("query.zig");
const recipe = @import("recipe.zig");
const preflight = @import("preflight.zig");
const infer = @import("infer.zig");
const m = @import("materialize.zig");
const proposals = @import("proposals.zig");

pub fn create(r: *query.Request, out: *std.Io.Writer) anyerror!void {
    if (try query.fieldProblem(r.a, r.line, &.{}, r.root, "simulateCreateFunctionsByName request", &.{ "id", "op", "functions", "functionsFile", "structuredReview", "detail", "expectedSnapshotId" }, &.{ "id", "op" })) |message| return r.fail("{s}", .{message});
    var decoder: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line) };
    const input = try @import("recipe_file.zig").jsonInput(&decoder, "functions", "functionsFile");
    if (input.value != .array) return r.fail("functions must be an array.", .{});
    if (input.value.array.items.len == 0) return r.fail("functions must list at least one function.", .{});
    const expected = if (r.root.get("expectedSnapshotId") != null) try decoder.stableId("expectedSnapshotId", try decoder.requiredString(.{ .object = r.root }, "expectedSnapshotId")) else r.snapshot_id;
    try r.ensureUsable();
    var created: std.ArrayList(model.Model) = .empty;
    var problems: std.ArrayList(model.Value) = .empty;
    var messages: std.ArrayList([]const u8) = .empty;
    const sources = try @import("recipe_file.zig").rawArrayItems(r.a, input.source);
    for (input.value.array.items, 0..) |item, index| {
        var scratch = r.*;
        // A builder sees the original declarations plus successfully materialized predecessors.
        var generation = r.generation.?.*;
        const functions = try r.a.alloc(*const model.session_model.Function, generation.functions.len + created.items.len);
        @memcpy(functions[0..generation.functions.len], generation.functions);
        for (created.items, functions[generation.functions.len..]) |function, *slot| slot.* = function.data.function;
        generation.functions = functions;
        scratch.generation = &generation;
        const source = sources[index];
        scratch.line = source;
        scratch.root = if (item == .object) item.object else .empty;
        const creation = build(&scratch, item, expected) catch |err| {
            if (err != error.Rejected or !m.same(scratch.rejection.code, "ProtocolError")) {
                r.rejection = scratch.rejection;
                return err;
            }
            const display_name = if (item == .object) blk: {
                const name = item.object.get("displayName") orelse break :blk "<unknown>";
                break :blk if (name == .string) name.string else "<unknown>";
            } else "<unknown>";
            var members: std.ArrayList(model.Member) = .empty;
            try members.appendSlice(r.a, &.{
                try m.member(r.a, "index", try m.number(r.a, index)),
                try m.member(r.a, "displayName", try m.string(r.a, display_name)),
                try m.member(r.a, "message", try m.string(r.a, scratch.rejection.message)),
            });
            // A recipe decoding error names its recipe (recipe.zig); keep that per item.
            if (scratch.rejection.details.len > 0) {
                const details = try std.json.parseFromSliceLeaky(std.json.Value, r.a, scratch.rejection.details, .{});
                if (details.object.get("recipeKind")) |kind| try members.appendSlice(r.a, &.{
                    try m.member(r.a, "recipeKind", try m.string(r.a, kind.string)),
                    try m.member(r.a, "recipePath", try m.string(r.a, details.object.get("recipePath").?.string)),
                    try m.member(r.a, "byName", if (details.object.get("byName").?.bool) .json_true else .json_false),
                });
            }
            try problems.append(r.a, try m.object(r.a, members.items));
            try messages.append(r.a, try std.fmt.allocPrint(r.a, "[{d}] {s}: {s}", .{ index, display_name, scratch.rejection.message }));
            continue;
        };
        const fresh = preflight.run(&scratch, creation) catch |err| {
            r.rejection = scratch.rejection;
            return err;
        };
        var inferred = infer.run(&scratch, creation, fresh) catch |err| {
            r.rejection = scratch.rejection;
            return err;
        };
        const function = try m.function(&scratch, creation, fresh, &inferred);
        const context = model.typedRoot(r.a, r.interner.?, &generation).context;
        try created.append(r.a, .{ .context = context, .data = .{ .function = function } });
    }
    if (problems.items.len > 0) {
        var details: std.Io.Writer.Allocating = .init(r.a);
        try details.writer.writeAll(",\"problems\":");
        try m.write(&details.writer, .{ .array = problems.items });
        r.rejection = .{ .code = "ProtocolError", .message = try std.mem.join(r.a, "\n", messages.items), .fields = details.written() };
        return error.Rejected;
    }
    try proposals.declare(r, out, .{ .kind = "createFunction", .group = "functions", .subject = "function", .items = created.items });
}

fn build(r: *query.Request, item: std.json.Value, expected: []const u8) anyerror!preflight.Creation {
    if (item != .object) return r.fail("Expected a JSON object.", .{});
    if (try query.fieldProblem(r.a, r.line, &.{}, r.root, "function in functions", &.{ "displayName", "exported", "mustNotWait", "typeParameters", "parameters", "resultType", "requiredEffects", "body", "bodyFile" }, &.{ "displayName", "parameters", "resultType", "requiredEffects" })) |message| return r.fail("{s}", .{message});
    return query.functionCreationByName(r, expected);
}
