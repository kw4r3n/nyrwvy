//! Local repairs against a new pinned graph; only the final candidate enters the registry.
const std = @import("std");
const model = @import("model_reader");
const host = @import("main.zig");
const planning = @import("dependency_upgrade.zig");
const query = @import("query.zig");
const recipe = @import("recipe.zig");
const migration = @import("migration.zig");
const edit = @import("operation_model.zig");
const preflight = @import("preflight.zig");
const m = @import("materialize.zig");
const Value = model.Value;
const Json = std.json.Value;
const Allocator = std.mem.Allocator;
pub const Retained = struct { selected: host.Dependency, manifest: []const u8, request: []const u8, review_fingerprint: []const u8 };
pub const Result = struct { transaction: Value, snapshot: Value, candidate: Value, plan: planning.Plan, functions: []const Value, types: []const Value, capabilities: []const Value };
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
fn owner(d: *recipe.Decoder, function: Value) !void {
    const ps = model.arrayItems(function, "parameters");
    const names = try d.r.a.alloc([]const u8, ps.len);
    for (ps, names) |p, *name| name.* = query.nameOf(p);
    d.owner = .{ .name = query.nameOf(function), .parameters = names, .existing = function };
}
fn typeRef(d: *recipe.Decoder, path: []const u8, value: Json, named: bool) !recipe.TypeRef {
    const result = try d.anyType(path, value);
    return if (named and d.owner != null and d.owner.?.existing != null) recipe.bindType(d, result) else result;
}
fn operation(root: Value, id: []const u8) ?Value {
    for (model.arrayItems(root, "capabilities")) |cap| for (model.arrayItems(cap, "operations")) |op| if (m.same(query.idOf(op), id)) return op;
    return null;
}
fn decode(r: *query.Request, plan: planning.Plan, named: bool) !Repairs {
    var names = r.*;
    names.model = try model.ownTyped(r.a, plan.candidate, false);
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
        var parent: ?Value = null;
        const id = if (named) blk: {
            parent = try d.selector(try at(r, path, "type"), try d.property(item, "type"), "type");
            break :blk query.idOf(parent.?);
        } else try d.stableId("typeId", try d.requiredString(item, "typeId"));
        const field_id = if (named) blk: {
            if (model.property(parent.?, "fields") == null) return names.fail("Type '{s}' is not a product type.", .{query.nameOf(parent.?)});
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
            for (try query.visible(&names, "function")) |f| {
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
            for (try query.visible(&names, "capability")) |cap| for (model.arrayItems(cap, "operations")) |op| {
                if (m.same(query.idOf(op), id)) break :blk op;
            };
            return names.fail("Unknown repair operation.", .{});
        };
        const parameters = try decodeParameters(&d, item, path, target, named, true);
        try operations.append(r.a, .{ .id = query.idOf(target), .parameters = parameters, .result = try typeRef(&d, try at(r, path, "resultType"), try d.property(item, "resultType"), named) });
    }
    return .{ .fields = fields.items, .functions = functions.items, .operations = operations.items };
}
fn decodeParameters(d: *recipe.Decoder, item: Json, path: []const u8, target: Value, named: bool, comptime cap: bool) ![]const Parameter {
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
fn signature(r: *query.Request, target: Value, repair: Signature) !Value {
    var materializer: m.Materializer = .{ .a = r.a, .r = r, .types = undefined, .fresh = &.{}, .function_id = .json_null, .parameters = &.{} };
    const parameters = try r.a.dupe(Value, model.arrayItems(target, "parameters"));
    for (parameters) |*parameter| for (repair.parameters) |new| {
        if (m.same(query.idOf(parameter.*), new.id)) {
            parameter.* = try edit.set(r.a, parameter.*, "type", try materializer.typeRef(new.type));
            break;
        }
    };
    return edit.set(r.a, try edit.set(r.a, target, "parameters", .{ .array = parameters }), "resultType", try materializer.typeRef(repair.result));
}
fn exactParameters(target: Value, supplied: []const Parameter) bool {
    const expected = model.arrayItems(target, "parameters");
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
    const graphs = try planning.mergeModel(r, r.model, plan.dependency);
    const local = graphs[0];
    for (supplied.fields, 0..) |f, i| for (supplied.fields[0..i]) |old| if (m.same(f.owner, old.owner) and m.same(f.id, old.id)) return invalid(r, "Each local field, function or operation may be repaired once.");
    for ([_][]const Signature{ supplied.functions, supplied.operations }) |repairs| for (repairs, 0..) |item, i| for (repairs[0..i]) |old| if (m.same(item.id, old.id)) return invalid(r, "Each local field, function or operation may be repaired once.");
    const invalid_message = "Repair only existing locally owned declarations; supply every existing signature parameter ID exactly once. Imported declarations are read-only.";
    for (supplied.fields) |f| {
        const target = edit.find(local, "types", f.owner) orelse return invalid(r, invalid_message);
        if (model.property(target, "fields") == null or edit.find(target, "fields", f.id) == null) return invalid(r, invalid_message);
    }
    for (supplied.functions) |f| {
        const target = edit.find(local, "functions", f.id) orelse return invalid(r, invalid_message);
        if (!exactParameters(target, f.parameters)) return invalid(r, invalid_message);
    }
    for (supplied.operations) |op| {
        const target = operation(local, op.id) orelse return invalid(r, invalid_message);
        if (!exactParameters(target, op.parameters)) return invalid(r, invalid_message);
    }
    var candidate = graphs[1];
    for (supplied.fields) |f| {
        const target = edit.find(candidate, "types", f.owner).?;
        const field = edit.find(target, "fields", f.id).?;
        var materializer: m.Materializer = .{ .a = r.a, .r = r, .types = undefined, .fresh = &.{}, .function_id = .json_null, .parameters = &.{} };
        candidate = try edit.replace(r.a, candidate, "types", try edit.replace(r.a, target, "fields", try edit.set(r.a, field, "type", try materializer.typeRef(f.type))));
    }
    for (supplied.functions) |f| candidate = try edit.replace(r.a, candidate, "functions", try signature(r, edit.find(candidate, "functions", f.id).?, f));
    for (model.arrayItems(candidate, "capabilities")) |cap| {
        var updated = cap;
        for (supplied.operations) |op| if (edit.find(cap, "operations", op.id)) |target| {
            updated = try edit.replace(r.a, updated, "operations", try signature(r, target, op));
        };
        if (!edit.identical(updated, cap)) candidate = try edit.replace(r.a, candidate, "capabilities", updated);
    }
    var materialization = r.*;
    materialization.model = try model.ownTyped(r.a, candidate, false);
    materialization.provenance = plan.dependency.provenance;
    defer {
        r.rejection = materialization.rejection;
        r.diagnostic = materialization.diagnostic;
    }
    var repaired = candidate;
    for (supplied.functions) |f| {
        const before = edit.find(local, "functions", f.id).?;
        if (migration.bodyObligation(r, before)) |id| return preflight.rejected(&materialization, "StructuralBodyReferencedByObligation", try std.fmt.allocPrint(r.a, "Obligation '{s}' targets the body being replaced.", .{id}));
        const after = try migration.materializeReplacement(&materialization, edit.find(candidate, "functions", f.id).?, f.body.?);
        repaired = try edit.replace(r.a, repaired, "functions", after);
    }
    const transaction = try m.id(r.a);
    const snapshot = try m.id(r.a);
    repaired = try edit.set(r.a, try edit.set(r.a, repaired, "snapshotId", snapshot), "parentSnapshotId", try m.string(r.a, r.snapshot_id));
    try migration.validate(&materialization, repaired);
    var functions: std.ArrayList(Value) = .empty;
    var types: std.ArrayList(Value) = .empty;
    var capabilities: std.ArrayList(Value) = .empty;
    for (model.arrayItems(repaired, "functions")) |f| for (supplied.functions) |s| {
        if (m.same(query.idOf(f), s.id)) {
            try functions.append(r.a, f);
            break;
        }
    };
    for (model.arrayItems(repaired, "types")) |t| for (supplied.fields) |f| {
        if (m.same(query.idOf(t), f.owner)) {
            try types.append(r.a, t);
            break;
        }
    };
    for (model.arrayItems(repaired, "capabilities")) |c| for (supplied.operations) |op| {
        if (edit.find(c, "operations", op.id) != null) {
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
    const owned = try model.ownTyped(a, plan.dependency.root, false);
    const read = model.readTypedDocument(a, owned);
    const provenance = plan.dependency.provenance;
    const members = try a.alloc(@import("wire.zig").Provenance.Member, provenance.members.len);
    for (provenance.members, members) |member, *copy| copy.* = .{ .id = try a.dupe(u8, member.id), .fingerprint = try a.dupe(u8, member.fingerprint), .keys = try copyTexts(a, member.keys), .path = try a.dupe(u8, member.path), .embedded = member.embedded };
    return .{
        .selected = .{ .root = owned, .canonical = read.canonical, .id = read.id, .digest = read.digest, .fingerprint = read.fingerprints.?.snapshot, .subjects = read.subjects, .provenance = .{ .members = members, .hidden = try copyTexts(a, provenance.hidden) }, .set = plan.dependency.set },
        .manifest = try a.dupe(u8, plan.manifest),
        .request = try a.dupe(u8, r.line),
        .review_fingerprint = try a.dupe(u8, plan.fingerprint),
    };
}
