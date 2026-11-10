//! Explicit read-only observations of two immutable proposal contexts; never an equivalence proof.
const std = @import("std");
const model = @import("model_reader");
const q = @import("query.zig");
const m = @import("materialize.zig");
const recipe = @import("recipe.zig");
const proposals = @import("proposals.zig");
const values = @import("run_value.zig");
const pure = @import("run_pure.zig");
const root = @import("root");
const Value = model.Value;
const Json = std.json.Value;
const Fail = anyerror;

fn failure(r: *q.Request, code: []const u8, message: []const u8) Fail {
    r.rejection = .{ .code = code, .message = message };
    return error.Rejected;
}
fn object(r: *q.Request, pairs: anytype) !Value {
    var fields: std.ArrayList(model.Member) = .empty;
    inline for (pairs) |pair| try fields.append(r.a, try m.member(r.a, pair[0], pair[1]));
    return .{ .object = fields.items };
}
fn text(r: *q.Request, name: []const u8) ![]const u8 {
    const value = r.root.get(name) orelse return r.fail("Missing required property '{s}'.", .{name});
    if (value != .string) return r.fail("Property '{s}' must be a JSON string.", .{name});
    return q.readable(r, value.string);
}
fn prop(value: Value, name: []const u8) []const u8 {
    const field = model.property(value, name) orelse return "";
    return if (field == .string) model.utf8(model.decoded(field)) else "";
}
fn persisted(r: *q.Request) !void {
    try r.ensureUsable();
    if (try root.verifyPersisted(r.a)) |rejection| {
        r.rejection = rejection;
        return error.Rejected;
    }
}
fn jsonModel(r: *q.Request, json: Json) !Value {
    var out: std.Io.Writer.Allocating = .init(r.a);
    try std.json.Stringify.value(json, .{}, &out.writer);
    return model.parseValue(r.a, out.written());
}
fn errorValue(r: *q.Request) !Value {
    var out: std.Io.Writer.Allocating = .init(r.a);
    try out.writer.writeAll("{\"code\":");
    try root.writeString(&out.writer, r.rejection.code);
    try out.writer.writeAll(",\"message\":");
    try root.writeString(&out.writer, r.rejection.message);
    if (r.rejection.details.len > 0) {
        try out.writer.writeAll(",\"details\":");
        try out.writer.writeAll(r.rejection.details);
    }
    try out.writer.writeAll(r.rejection.fields);
    try out.writer.writeByte('}');
    return model.parseValue(r.a, out.written());
}
fn runContext(r: *q.Request, case: values.VerificationCase, expected: ?values.RunValue, capture: *values.Capture, out: *std.Io.Writer) Fail!void {
    var input = try values.inputForCase(r, case, expected);
    input.capture = capture;
    values.bounded(r, &input) catch |e| {
        if (m.same(r.rejection.code, "RunArgumentTooLarge")) r.rejection.message = "Verification values exceed the runPure bound.";
        return e;
    };
    if (input.has_host_binding) return values.diagnostic(r, &input, "VerificationHostUnsupported", "Change verification accepts fake capabilities only.");
    try values.validate(r, &input);
    const ir = try pure.lowered(r, case.function_id);
    return @import("run_csharp.zig").run(r, &input, ir, pure.concurrentValue(try std.json.parseFromSliceLeaky(Json, r.a, ir, .{})), out);
}
const Observation = struct { wire: Value, captured: values.Capture = .{} };
fn observe(context: *q.Request, case: values.VerificationCase, expected: ?values.RunValue) Fail!Observation {
    context.rejection = .{ .code = "", .message = "" };
    var result: std.Io.Writer.Allocating = .init(context.a);
    var capture: values.Capture = .{};
    runContext(context, case, expected, &capture, &result.writer) catch |e| switch (e) {
        error.Rejected => return .{ .wire = try object(context, .{.{ "error", try errorValue(context) }}) },
        else => return e,
    };
    const value = try model.parseValue(context.a, result.written());
    return .{ .wire = try object(context, .{ .{ "actual", model.property(value, "actual").? }, .{ "calls", model.property(value, "calls").? }, .{ "matches", model.property(value, "matches").? }, .{ "mismatch", model.property(value, "mismatch").? } }), .captured = capture };
}
/// Instruction uses matter here; unused Task declarations do not make observations concurrent.
fn concurrent(context: *q.Request, id: []const u8) bool {
    const text_ = switch (model.typedExecutionForEntry(context.a, context.typedModel(), id)) {
        .ir => |ir| ir,
        else => return true,
    };
    const ir = std.json.parseFromSliceLeaky(Json, context.a, text_, .{}) catch return true;
    if (ir != .object) return true;
    const functions = ir.object.get("functions") orelse return true;
    for (functions.array.items) |f| for (f.object.get("blocks").?.array.items) |block| for (block.object.get("instructions").?.array.items) |instruction| {
        const kind = instruction.object.get("kind").?.string;
        for ([_][]const u8{ "parallelAll", "cancellationRequested", "cancellationShield", "taskScope", "taskStart", "taskWait", "taskCancel", "oneShotCreate", "oneShotComplete", "oneShotWait" }) |tag| if (m.same(kind, tag)) return true;
    };
    return false;
}
fn sameCalls(before: []const values.CapturedCall, after: []const values.CapturedCall) bool {
    if (before.len != after.len) return false;
    for (before, after) |old, fresh| {
        if (!m.same(old.parameter_id, fresh.parameter_id) or !m.same(old.operation_id, fresh.operation_id) or old.arguments.len != fresh.arguments.len) return false;
        for (old.arguments, fresh.arguments) |x, y| if (!m.same(x.id, y.id) or !values.equal(x.value, y.value)) return false;
    }
    return true;
}
fn concreteFailure(error_: Value, id: []const u8) bool {
    const exception = prop(error_, "exceptionType");
    const recognized = [_][]const u8{ "FakeCapabilityException", "FakeArgumentsUnmatchedException", "FakeResponsesExhaustedException", "System.ArgumentException", "System.ArgumentOutOfRangeException", "System.InvalidOperationException", "System.DivideByZeroException", "System.OverflowException", "System.IndexOutOfRangeException", "System.NullReferenceException" };
    var known = false;
    for (recognized) |name| if (m.same(exception, name)) {
        known = true;
        break;
    };
    if (!known) return false;
    for (model.arrayItems(error_, "frames")) |frame| if (m.same(prop(frame, "kind"), "function") and m.same(prop(frame, "functionId"), id)) return true;
    return false;
}
fn classify(is_concurrent: bool, expected: bool, before: Observation, after: Observation, id: []const u8) []const u8 {
    if (is_concurrent) return "unknown";
    if (model.property(before.wire, "error") != null) return "unknown";
    if (model.property(after.wire, "error")) |error_| return if (concreteFailure(error_, id)) "counterexample" else "unknown";
    if (!sameCalls(before.captured.calls, after.captured.calls)) return "counterexample";
    if (expected) return if (model.property(after.wire, "matches").? == .json_true) "intended-change-tested" else "counterexample";
    return if (values.equal(before.captured.actual.?, after.captured.actual.?)) "tested-agreement" else "counterexample";
}
fn contains(items: []const Value, id: []const u8) bool {
    for (items) |item| if (m.same(model.utf8(model.decoded(item)), id)) return true;
    return false;
}
fn affected(r: *q.Request, p: *const proposals.Pending, candidate: model.Model) ![]const Value {
    var ids: std.ArrayList(Value) = .empty;
    try ids.appendSlice(r.a, try proposals.functionIds(r, p));
    model.useAllocator(r.a);
    while (true) {
        const count = ids.items.len;
        for ([_]model.Model{ r.typedModel(), candidate }) |graph| for (graph.functions()) |function| {
            if (contains(ids.items, q.idOf(function))) continue;
            var found = false;
            for (model.typedBodyNodes(r.a, function)) |node| for (model.typedFunctionIdsOf(node)) |id| {
                if (contains(ids.items, model.utf8(id))) found = true;
            };
            if (found) try ids.append(r.a, .{ .string = function.id() });
        };
        if (ids.items.len == count) break;
    }
    std.mem.sort(Value, ids.items, {}, struct {
        fn less(_: void, x: Value, y: Value) bool {
            return std.mem.order(u16, model.decoded(x), model.decoded(y)) == .lt;
        }
    }.less);
    return ids.items;
}

pub fn run(r: *q.Request, out: *std.Io.Writer) !void {
    const op = r.root.get("op").?.string;
    if (try q.fieldProblem(r.a, r.line, &.{}, r.root, try std.fmt.allocPrint(r.a, "{s} request", .{op}), &.{ "id", "op", "proposalId", "cases", "output" }, &.{ "id", "op", "proposalId", "cases" })) |message| return r.fail("{s}", .{message});
    const wanted = try text(r, "proposalId");
    const pending = proposals.find(wanted);
    var baseline = r.*;
    var candidate = r.*;
    if (pending) |p| if (!r.session_invalidated and !p.operations) {
        candidate.generation = &p.generation;
        candidate.interner = r.interner;
        if (p.dependency) |dependency| candidate.provenance = dependency.selected.provenance;
    };
    const cases = try values.verificationCases(r, &candidate, m.same(op, "verifyChangeByName"));
    if (cases.len == 0 or cases.len > 32) return failure(r, "DependencyFailure", "Verification requires 1 to 32 explicit cases.");
    for (cases) |case| if (q.isBlank(try std.unicode.utf8ToUtf16LeAlloc(r.a, case.label))) return failure(r, "DependencyFailure", "Every verification case needs a nonblank condition label.");
    try persisted(r);
    const p = try proposals.lookup(r, wanted);
    if (!m.same(p.base_id, r.snapshot_id)) return failure(r, "ExternalModification", "The snapshot file changed outside this session.");
    if (p.operations) return failure(r, "DependencyFailure", "Candidate execution is unsupported for operations proposals.");
    // A batch may run operations steps, but one that leaves new missing arguments cannot execute.
    for (p.generation.obligations) |obligation| if (obligation.resolution == null and r.typedModel().data.snapshot.findObligation(obligation.id) == null) return failure(r, "DependencyFailure", "Candidate execution is unsupported while the proposal leaves missing-argument obligations open.");
    candidate.generation = &p.generation;
    candidate.interner = r.interner;
    candidate.snapshot_id = model.utf8(candidate.typedModel().id());
    const receipt = try model.parseValue(r.a, p.receipt);
    candidate.snapshot_fingerprint = prop(receipt, "proposedSnapshotFingerprint");
    var observations: std.ArrayList(Value) = .empty;
    var entries: std.ArrayList(Value) = .empty;
    for (cases) |case| {
        const original_generation = baseline.generation;
        baseline.generation = (try values.declarationView(&baseline)).data.snapshot;
        const arguments = try r.a.alloc(Value, case.arguments.len);
        for (case.arguments, arguments) |arg, *slot| slot.* = try values.encodedInput(&baseline, arg);
        baseline.generation = original_generation;
        const candidate_generation = candidate.generation;
        candidate.generation = (try values.declarationView(&candidate)).data.snapshot;
        const expected = if (case.expected) |value| try values.encodedInput(&candidate, value) else .json_null;
        candidate.generation = candidate_generation;
        const before = try observe(&baseline, case, null);
        const after = try observe(&candidate, case, case.expected);
        var fields: std.ArrayList(model.Member) = .empty;
        try fields.append(r.a, try m.member(r.a, "label", try m.string(r.a, case.label)));
        try fields.append(r.a, try m.member(r.a, "functionId", try m.string(r.a, case.function_id)));
        for (try q.typedVisible(&baseline, "function")) |f| if (m.same(q.idOf(f), case.function_id)) {
            try fields.append(r.a, try m.member(r.a, "function", try m.string(r.a, q.nameOf(f))));
            break;
        };
        const status = classify(concurrent(&baseline, case.function_id) or concurrent(&candidate, case.function_id), case.expected != null, before, after, case.function_id);
        inline for (.{ .{ "arguments", Value{ .array = arguments } }, .{ "expectedCandidate", expected }, .{ "status", try m.string(r.a, status) }, .{ "baseline", before.wire }, .{ "candidate", after.wire } }) |pair| try fields.append(r.a, try m.member(r.a, pair[0], pair[1]));
        try observations.append(r.a, .{ .object = fields.items });
        if (!contains(entries.items, case.function_id)) try entries.append(r.a, try m.string(r.a, case.function_id));
    }
    const verifier = try jsonModel(r, try @import("verification_identity.zig").get(r));
    try persisted(r);
    const request = try recipe.rawProperty(r.a, r.line, "cases");
    var digest: [32]u8 = undefined;
    std.crypto.hash.sha2.Sha256.hash(request, &digest, .{});
    const evidence = try object(r, .{ .{ "proposalId", try m.string(r.a, p.id) }, .{ "baselineSnapshotId", try m.string(r.a, baseline.snapshot_id) }, .{ "candidateSnapshotId", try m.string(r.a, candidate.snapshot_id) }, .{ "caseRequest", try m.string(r.a, request) }, .{ "casesDigest", try m.string(r.a, try std.fmt.allocPrint(r.a, "{X}", .{digest})) }, .{ "verifierIdentity", verifier }, .{ "baselineFingerprint", try m.string(r.a, baseline.snapshot_fingerprint) }, .{ "candidateFingerprint", try m.string(r.a, candidate.snapshot_fingerprint) }, .{ "coverage", try m.string(r.a, "Explicit cases only; no proof of equivalence. Recognized mapped candidate-only program/fake failures are counterexamples after baseline success; other failures and concurrency are unknown. Effects must be preserved. Entry points are not dynamic coverage.") }, .{ "caseEntryFunctionIds", Value{ .array = entries.items } }, .{ "affectedFunctionIds", Value{ .array = try affected(r, p, candidate.typedModel()) } }, .{ "cases", Value{ .array = observations.items } } });
    var encoded: std.Io.Writer.Allocating = .init(r.a);
    try m.write(&encoded.writer, evidence);
    const output = if (r.root.get("output") != null) try text(r, "output") else null;
    const path = try proposals.writeReview(r, p, output, encoded.written());
    try m.write(out, try object(r, .{ .{ "evidence", evidence }, .{ "reviewPath", try m.string(r.a, path) } }));
}

test "verification classification ignores display names and keeps failures/concurrency conservative" {
    var arena: std.heap.ArenaAllocator = .init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const before = try model.parseValue(a,
        \\{"actual":{"kind":"product","typeId":"t","type":"Old","fields":[]},"calls":[],"matches":null,"mismatch":null}
    );
    const renamed = try model.parseValue(a,
        \\{"actual":{"kind":"product","typeId":"t","type":"New","fields":[]},"calls":[],"matches":true,"mismatch":null}
    );
    const changed_calls = try model.parseValue(a,
        \\{"actual":{"kind":"product","typeId":"t","fields":[]},"calls":[{"parameterId":"p","operationId":"o","arguments":[]}],"matches":true,"mismatch":null}
    );
    const mapped = try model.parseValue(a,
        \\{"error":{"exceptionType":"System.DivideByZeroException","frames":[{"kind":"function","functionId":"entry"}]}}
    );
    const unmapped = try model.parseValue(a,
        \\{"error":{"exceptionType":"System.DivideByZeroException","frames":[{"kind":"function","functionId":"other"}]}}
    );
    const actual: values.RunValue = .{ .kind = .unit, .type = .{ .primitive = "unit" } };
    const old: Observation = .{ .wire = before, .captured = .{ .actual = actual } };
    const fresh: Observation = .{ .wire = renamed, .captured = .{ .actual = actual } };
    const changed: Observation = .{ .wire = changed_calls, .captured = .{ .actual = actual, .calls = &.{.{ .parameter_id = "p", .operation_id = "o", .arguments = &.{} }} } };
    const mapped_error: Observation = .{ .wire = mapped };
    const unmapped_error: Observation = .{ .wire = unmapped };
    try std.testing.expectEqualStrings("tested-agreement", classify(false, false, old, fresh, "entry"));
    try std.testing.expectEqualStrings("intended-change-tested", classify(false, true, old, fresh, "entry"));
    try std.testing.expectEqualStrings("counterexample", classify(false, true, old, changed, "entry"));
    try std.testing.expectEqualStrings("counterexample", classify(false, false, old, mapped_error, "entry"));
    try std.testing.expectEqualStrings("unknown", classify(false, false, old, unmapped_error, "entry"));
    try std.testing.expectEqualStrings("unknown", classify(false, false, mapped_error, fresh, "entry"));
    try std.testing.expectEqualStrings("unknown", classify(true, true, old, fresh, "entry"));
    var zero_before = old;
    var zero_after = fresh;
    zero_before.captured.actual = .{ .kind = .float64, .type = .{ .primitive = "float64" }, .bits = 0 };
    zero_after.captured.actual = .{ .kind = .float64, .type = .{ .primitive = "float64" }, .bits = 0x8000000000000000 };
    try std.testing.expectEqualStrings("counterexample", classify(false, false, zero_before, zero_after, "entry"));
}
