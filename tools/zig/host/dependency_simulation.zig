//! Local repairs against a new pinned graph; only the final candidate enters the registry.
const std = @import("std");
const model = @import("model_reader");
const host = @import("main.zig");
const planning = @import("dependency_upgrade.zig");
const query = @import("query.zig");
const recipe = @import("recipe.zig");
const migration = @import("migration.zig");
const edit = @import("typed_edit.zig");
const Model = model.Model;
const tm = model.session_model;
const preflight = @import("preflight.zig");
const m = @import("materialize.zig");
const Value = model.Value;
const Json = std.json.Value;
const Allocator = std.mem.Allocator;
pub const Retained = struct { selected: host.Dependency, manifest: []const u8, request: []const u8, review_fingerprint: []const u8 };
pub const Result = struct { transaction: Value, snapshot: Value, candidate: Model, plan: planning.Plan, functions: []const Model, types: []const Model, capabilities: []const Model };
const Parameter = struct { id: []const u8, type: recipe.TypeRef };
const Signature = struct { id: []const u8, parameters: []const Parameter, result: recipe.TypeRef, body: ?*const recipe.Node = null };
const Field = struct { owner: []const u8, id: []const u8, type: recipe.TypeRef };
const Repairs = struct { fields: []const Field, functions: []const Signature, operations: []const Signature };

fn at(r: *query.Request, path: []const u8, key: []const u8) ![]const u8 {
    return std.fmt.allocPrint(r.a, "{s}/{s}", .{ path, key });
}
fn failure(r: *query.Request, message: []const u8) anyerror {
    r.rejection = .{ .code = "DependencyFailure", .message = message };
    return error.Rejected;
}
fn invalid(r: *query.Request, message: []const u8) anyerror {
    return preflight.rejected(r, "DependencyRepairInvalid", message);
}
fn items(d: *recipe.Decoder, root: Json, key: []const u8) ![]const Json {
    return if (root.object.get(key) == null) &.{} else d.array(root, key);
}
fn owner(d: *recipe.Decoder, function: Model) !void {
    const ps = (function).parameters();
    const names = try d.r.a.alloc([]const u8, ps.len);
    for (ps, names) |p, *name| name.* = query.nameOf(p);
    d.owner = .{ .name = query.nameOf(function), .parameters = names, .existing = function };
}
fn typeRef(d: *recipe.Decoder, path: []const u8, value: Json, named: bool) !recipe.TypeRef {
    const result = try d.anyType(path, value);
    return if (named and d.owner != null and d.owner.?.existing != null) recipe.bindType(d, result) else result;
}
fn operation(root: Model, id: []const u8) ?Model {
    for ((root).capabilities()) |cap| for ((cap).operations()) |op| if (m.same(query.idOf(op), id)) return op;
    return null;
}
fn decode(r: *query.Request, plan: planning.Plan, named: bool) !Repairs {
    var names = r.*;
    names.generation = plan.candidate.data.snapshot;
    names.provenance = plan.dependency.provenance;
    var d: recipe.Decoder = .{ .r = &names, .duplicates = try recipe.duplicatePaths(r.a, r.line), .owner = if (named) .{ .name = "" } else null };
    defer {
        r.rejection = names.rejection;
        r.diagnostic = names.diagnostic;
    }
    const values = r.root.get("repairs").?;
    try d.ensureFields("/repairs", values, "local repairs", &.{ "fields", "functions", "operations" }, &.{});
    var fields: std.ArrayList(Field) = .empty;
    for (try items(&d, values, "fields"), 0..) |item, index| {
        const path = try std.fmt.allocPrint(r.a, "/repairs/fields/{d}", .{index});
        const owner_key = if (named) "type" else "typeId";
        const field_key = if (named) "field" else "fieldId";
        try d.ensureFields(path, item, "local field repair", &.{ owner_key, field_key, "newType" }, &.{ owner_key, field_key, "newType" });
        var parent: ?Model = null;
        const id = if (named) blk: {
            parent = try d.selector(try at(r, path, "type"), try d.property(item, "type"), "type");
            break :blk query.idOf(parent.?);
        } else try d.stableId("typeId", try d.requiredString(item, "typeId"));
        const field_id = if (named) blk: {
            if (!parent.?.data.type_declaration.product) return names.fail("Type '{s}' is not a product type.", .{query.nameOf(parent.?)});
            break :blk query.idOf(try d.member("field on {s}", .{query.nameOf(parent.?)}, parent.?, "fields", try d.requiredString(item, "field")));
        } else try d.stableId("fieldId", try d.requiredString(item, "fieldId"));
        try fields.append(r.a, .{ .owner = id, .id = field_id, .type = try typeRef(&d, try at(r, path, "newType"), try d.property(item, "newType"), named) });
    }
    var functions: std.ArrayList(Signature) = .empty;
    const raw_repairs = try recipe.rawProperty(r.a, r.line, "repairs");
    const raw_functions = if (values.object.get("functions") != null) try @import("recipe_file.zig").rawArrayItems(r.a, try recipe.rawProperty(r.a, raw_repairs, "functions")) else &.{};
    for (try items(&d, values, "functions"), 0..) |item, index| {
        const path = try std.fmt.allocPrint(r.a, "/repairs/functions/{d}", .{index});
        const key = if (named) "function" else "functionId";
        try d.ensureFields(path, item, "local function repair", &.{ key, "parameterTypes", "resultType", "replacement" }, &.{ key, "parameterTypes", "resultType", "replacement" });
        const target = if (named) try d.selector(try at(r, path, key), try d.property(item, key), "function") else blk: {
            const id = try d.stableId(key, try d.requiredString(item, key));
            for (try query.typedVisible(&names, "function")) |f| {
                if (m.same(query.idOf(f), id)) break :blk f;
            }
            return names.fail("Unknown repair function.", .{});
        };
        if (named) try owner(&d, target);
        const parameters = try decodeParameters(&d, item, path, target, named, false);
        const result = try typeRef(&d, try at(r, path, "resultType"), try d.property(item, "resultType"), named);
        var replacement = try d.property(item, "replacement");
        if (named) try recipe.resolveNames(&d, try at(r, path, "replacement"), &replacement, try recipe.rawProperty(r.a, raw_functions[index], "replacement"));
        var body = try d.recipe(try at(r, path, "replacement"), replacement);
        if (named) body = try recipe.bindExistingTypes(&d, body);
        try functions.append(r.a, .{ .id = query.idOf(target), .parameters = parameters, .result = result, .body = body });
        d.owner = if (named) .{ .name = "" } else null;
    }
    var operations: std.ArrayList(Signature) = .empty;
    for (try items(&d, values, "operations"), 0..) |item, index| {
        const path = try std.fmt.allocPrint(r.a, "/repairs/operations/{d}", .{index});
        const keys: []const []const u8 = if (named) &.{ "capability", "operation", "parameterTypes", "resultType" } else &.{ "operationId", "parameterTypes", "resultType" };
        try d.ensureFields(path, item, "local operation repair", keys, keys);
        const target = if (named) blk: {
            const cap = try d.selector(try at(r, path, "capability"), try d.property(item, "capability"), "capability");
            break :blk try d.member("operation on {s}", .{query.nameOf(cap)}, cap, "operations", try d.requiredString(item, "operation"));
        } else blk: {
            const id = try d.stableId("operationId", try d.requiredString(item, "operationId"));
            for (try query.typedVisible(&names, "capability")) |cap| for ((cap).operations()) |op| {
                if (m.same(query.idOf(op), id)) break :blk op;
            };
            return names.fail("Unknown repair operation.", .{});
        };
        const parameters = try decodeParameters(&d, item, path, target, named, true);
        try operations.append(r.a, .{ .id = query.idOf(target), .parameters = parameters, .result = try typeRef(&d, try at(r, path, "resultType"), try d.property(item, "resultType"), named) });
    }
    return .{ .fields = fields.items, .functions = functions.items, .operations = operations.items };
}
fn decodeParameters(d: *recipe.Decoder, item: Json, path: []const u8, target: Model, named: bool, comptime cap: bool) ![]const Parameter {
    var result: std.ArrayList(Parameter) = .empty;
    for (try d.array(item, "parameterTypes"), 0..) |parameter, i| {
        const p = try std.fmt.allocPrint(d.r.a, "{s}/parameterTypes/{d}", .{ path, i });
        const key = if (named) "parameter" else "parameterId";
        try d.ensureFields(p, parameter, if (cap) "operation parameter type" else "function parameter type", &.{ key, "type" }, &.{ key, "type" });
        const id = if (named) query.idOf(try d.member(if (cap) "parameter on operation {s}" else "parameter on {s}", .{query.nameOf(target)}, target, "parameters", try d.requiredString(parameter, key))) else try d.stableId(key, try d.requiredString(parameter, key));
        try result.append(d.r.a, .{ .id = id, .type = try typeRef(d, try at(d.r, p, "type"), try d.property(parameter, "type"), named) });
    }
    return result.items;
}
fn signature(r: *query.Request, target: Model, repair: Signature) !Model {
    var materializer: m.Materializer = .{ .a = r.a, .r = r, .types = undefined, .fresh = &.{}, .function_id = tm.none, .parameters = &.{} };
    const parameters = try r.a.dupe(Model, target.parameters());
    for (parameters) |*parameter| for (repair.parameters) |new| {
        if (m.same(query.idOf(parameter.*), new.id)) {
            parameter.* = try edit.changed(r.a, parameter.*, .{ .type = try materializer.typeRef(new.type) });
            break;
        }
    };
    return edit.changed(r.a, try edit.withChildren(r.a, target, .parameters, parameters), .{ .result = try materializer.typeRef(repair.result) });
}
fn exactParameters(target: Model, supplied: []const Parameter) bool {
    const expected = (target).parameters();
    if (expected.len != supplied.len) return false;
    for (supplied, 0..) |p, i| {
        for (supplied[0..i]) |before| if (m.same(before.id, p.id)) return false;
        for (expected) |e| {
            if (m.same(query.idOf(e), p.id)) break;
        } else return false;
    }
    return true;
}
fn applyRepairs(r: *query.Request, plan: planning.Plan, supplied: Repairs) !Result {
    const graphs = try planning.mergeModel(r, r.typedModel(), plan.dependency);
    const local = graphs[0];
    for (supplied.fields, 0..) |f, i| for (supplied.fields[0..i]) |old| if (m.same(f.owner, old.owner) and m.same(f.id, old.id)) return invalid(r, "Each local field, function or operation may be repaired once.");
    for ([_][]const Signature{ supplied.functions, supplied.operations }) |repairs| for (repairs, 0..) |item, i| for (repairs[0..i]) |old| if (m.same(item.id, old.id)) return invalid(r, "Each local field, function or operation may be repaired once.");
    const invalid_message = "Repair only existing locally owned declarations; supply every existing signature parameter ID exactly once. Imported declarations are read-only.";
    for (supplied.fields) |f| {
        const target = edit.find(local, .types, f.owner) orelse return invalid(r, invalid_message);
        if (!target.data.type_declaration.product or edit.find(target, .fields, f.id) == null) return invalid(r, invalid_message);
    }
    for (supplied.functions) |f| {
        const target = edit.find(local, .functions, f.id) orelse return invalid(r, invalid_message);
        if (!exactParameters(target, f.parameters)) return invalid(r, invalid_message);
    }
    for (supplied.operations) |op| {
        const target = operation(local, op.id) orelse return invalid(r, invalid_message);
        if (!exactParameters(target, op.parameters)) return invalid(r, invalid_message);
    }
    var candidate = graphs[1];
    for (supplied.fields) |f| {
        const target = edit.find(candidate, .types, f.owner).?;
        const field = edit.find(target, .fields, f.id).?;
        var materializer: m.Materializer = .{ .a = r.a, .r = r, .types = undefined, .fresh = &.{}, .function_id = tm.none, .parameters = &.{} };
        candidate = try edit.replace(r.a, candidate, .types, try edit.replace(r.a, target, .fields, try edit.changed(r.a, field, .{ .type = try materializer.typeRef(f.type) })));
    }
    for (supplied.functions) |f| candidate = try edit.replace(r.a, candidate, .functions, try signature(r, edit.find(candidate, .functions, f.id).?, f));
    for ((candidate).capabilities()) |cap| {
        var updated = cap;
        for (supplied.operations) |op| if (edit.find(cap, .operations, op.id)) |target| {
            updated = try edit.replace(r.a, updated, .operations, try signature(r, target, op));
        };
        if (updated.data.capability != cap.data.capability) candidate = try edit.replace(r.a, candidate, .capabilities, updated);
    }
    var materialization = r.*;
    materialization.generation = candidate.data.snapshot;
    materialization.provenance = plan.dependency.provenance;
    defer {
        r.rejection = materialization.rejection;
        r.diagnostic = materialization.diagnostic;
    }
    var repaired = candidate;
    for (supplied.functions) |f| {
        const before = edit.find(local, .functions, f.id).?;
        if (migration.bodyObligation(r, before)) |id| return preflight.rejected(&materialization, "StructuralBodyReferencedByObligation", try std.fmt.allocPrint(r.a, "Obligation '{s}' targets the body being replaced.", .{id}));
        const after = try migration.materializeReplacement(&materialization, edit.find(candidate, .functions, f.id).?, f.body.?);
        repaired = try edit.replace(r.a, repaired, .functions, after);
    }
    const transaction = try m.id(r.a);
    const snapshot = try m.id(r.a);
    repaired = try migration.stamp(r, repaired, snapshot);
    try migration.validate(&materialization, repaired);
    var functions: std.ArrayList(Model) = .empty;
    var types: std.ArrayList(Model) = .empty;
    var capabilities: std.ArrayList(Model) = .empty;
    for ((repaired).functions()) |f| for (supplied.functions) |s| {
        if (m.same(query.idOf(f), s.id)) {
            try functions.append(r.a, f);
            break;
        }
    };
    for ((repaired).types()) |t| for (supplied.fields) |f| {
        if (m.same(query.idOf(t), f.owner)) {
            try types.append(r.a, t);
            break;
        }
    };
    for ((repaired).capabilities()) |c| for (supplied.operations) |op| {
        if (edit.find(c, .operations, op.id) != null) {
            try capabilities.append(r.a, c);
            break;
        }
    };
    return .{ .transaction = transaction, .snapshot = snapshot, .candidate = repaired, .plan = plan, .functions = functions.items, .types = types.items, .capabilities = capabilities.items };
}
pub fn simulate(r: *query.Request, out: *std.Io.Writer) !void {
    var d: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line) };
    try d.ensureFields("", .{ .object = r.root }, "dependency upgrade repair", &.{ "id", "op", "upgrades", "selection", "repairs", "expectedSnapshotId", "structuredReview", "detail" }, &.{ "id", "op", "repairs" });
    const named = std.mem.endsWith(u8, r.root.get("op").?.string, "ByName");
    const declarations = try planning.planChange(r);
    const repairs = try decode(r, declarations, named);
    const expected = if (r.root.get("expectedSnapshotId") != null) try d.stableId("expectedSnapshotId", try d.requiredString(.{ .object = r.root }, "expectedSnapshotId")) else r.snapshot_id;
    try planning.checkPersisted(r);
    if (!m.same(expected, r.snapshot_id)) return failure(r, "The dependency repair targets a different base snapshot.");
    if (host.selection == null) return failure(r, "This workspace has no dependency to repair against; add one by opening it in upgrade mode with a selection whose replaces is empty.");
    const plan = try planning.planChange(r);
    const result = try applyRepairs(r, plan, repairs);
    try @import("proposals.zig").publishDependency(r, out, result);
}
pub fn replan(r: *query.Request, pending: Retained) !void {
    const request = try std.json.parseFromSliceLeaky(Json, r.a, pending.request, .{ .duplicate_field_behavior = .use_last });
    var planned = r.*;
    planned.root = request.object;
    planned.line = pending.request;
    defer r.rejection = planned.rejection;
    const plan = try planning.planChange(&planned);
    if (!m.same(plan.fingerprint, pending.review_fingerprint)) return failure(&planned, "The workspace, pins, targets or selected source changed after dependency repair review. Simulate again.");
}

fn copyTexts(a: Allocator, source: []const []const u8) ![]const []const u8 {
    const result = try a.alloc([]const u8, source.len);
    for (source, result) |text, *copy| copy.* = try a.dupe(u8, text);
    return result;
}
/// Every selected graph/provenance/request pointer is owned by the one proposal arena.
pub fn retain(a: Allocator, r: *query.Request, plan: planning.Plan) !Retained {
    const strings = try a.create(tm.Interner);
    strings.* = .{ .gpa = a };
    var compactor: tm.Compactor = .{ .arena = a, .scratch = r.a, .old_interner = plan.dependency.interner.?, .interner = strings };
    const generation = try a.create(tm.Generation);
    generation.* = try compactor.generation(plan.dependency.generation.?);
    const provenance = plan.dependency.provenance;
    const members = try a.alloc(@import("wire.zig").Provenance.Member, provenance.members.len);
    for (provenance.members, members) |member, *copy| copy.* = .{ .id = try a.dupe(u8, member.id), .fingerprint = try a.dupe(u8, member.fingerprint), .keys = try copyTexts(a, member.keys), .path = try a.dupe(u8, member.path), .embedded = member.embedded };
    return .{
        .selected = .{ .generation = generation, .interner = strings, .canonical = try a.dupe(u8, plan.dependency.canonical), .id = try a.dupe(u8, plan.dependency.id), .digest = try a.dupe(u8, plan.dependency.digest), .fingerprint = try a.dupe(u8, plan.dependency.fingerprint), .subjects = try copyTexts(a, plan.dependency.subjects), .provenance = .{ .members = members, .hidden = try copyTexts(a, provenance.hidden) }, .set = plan.dependency.set },
        .manifest = try a.dupe(u8, plan.manifest),
        .request = try a.dupe(u8, r.line),
        .review_fingerprint = try a.dupe(u8, plan.fingerprint),
    };
}

test "retained dependency owns typed graph interner and provenance after source arena release" {
    var retained_arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer retained_arena.deinit();
    const a = retained_arena.allocator();
    const retained = blk: {
        var source_arena = std.heap.ArenaAllocator.init(std.testing.allocator);
        defer source_arena.deinit();
        const source = source_arena.allocator();
        const strings = try source.create(tm.Interner);
        strings.* = .{ .gpa = source };
        const value = try model.parseValue(source,
            \\{"snapshotId":"snapshot","functions":[{"id":"function","displayName":"Function","parameters":[],"resultType":{"kind":"primitive","primitive":"string"},"body":{"nodes":[{"id":"node","kind":"stringLiteral","value":"owned literal"}],"resultNodeId":"node"}}]}
        );
        const generation = try source.create(tm.Generation);
        generation.* = try tm.load(source, strings, value, null);
        var request: query.Request = .{ .a = source, .line = try source.dupe(u8, "request"), .root = .empty, .provenance = null, .generation = generation, .interner = strings };
        const texts = try source.alloc([]const u8, 1);
        texts[0] = try source.dupe(u8, "function:function");
        const members = try source.alloc(@import("wire.zig").Provenance.Member, 1);
        members[0] = .{ .id = try source.dupe(u8, "member"), .fingerprint = try source.dupe(u8, "fingerprint"), .keys = texts, .path = try source.dupe(u8, "member-path"), .embedded = true };
        const dependency: host.Dependency = .{ .generation = generation, .interner = strings, .canonical = try source.dupe(u8, "canonical"), .id = try source.dupe(u8, "snapshot"), .digest = try source.dupe(u8, "digest"), .fingerprint = try source.dupe(u8, "dependency-fingerprint"), .subjects = texts, .provenance = .{ .members = members, .hidden = texts }, .set = true };
        const graph = request.typedModel();
        const plan: planning.Plan = .{ .dependency = dependency, .manifest = try source.dupe(u8, "manifest"), .local = graph, .candidate = graph, .preview = .json_null, .fingerprint = try source.dupe(u8, "review-fingerprint") };
        break :blk try retain(a, &request, plan);
    };
    const graph = retained.selected.typedModel(a);
    try std.testing.expectEqualSlices(u16, &.{ 's', 'n', 'a', 'p', 's', 'h', 'o', 't' }, graph.id());
    const function = graph.functions()[0];
    const node = function.data.function.body.node(0);
    try std.testing.expectEqualStrings("owned literal", model.utf8(retained.selected.interner.?.stringText(node.stringLiteral().?)));
    try std.testing.expectEqualStrings("canonical", retained.selected.canonical);
    try std.testing.expectEqualStrings("dependency-fingerprint", retained.selected.fingerprint);
    try std.testing.expectEqualStrings("function:function", retained.selected.subjects[0]);
    try std.testing.expectEqualStrings("member-path", retained.selected.provenance.members[0].path);
    try std.testing.expectEqualStrings("function:function", retained.selected.provenance.members[0].keys[0]);
    try std.testing.expectEqualStrings("function:function", retained.selected.provenance.hidden[0]);
    try std.testing.expectEqualStrings("request", retained.request);
    try std.testing.expectEqualStrings("manifest", retained.manifest);
    try std.testing.expectEqualStrings("review-fingerprint", retained.review_fingerprint);
    try std.testing.expect(retained.selected.set);
}
