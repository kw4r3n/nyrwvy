//! Signature migrations materialize temporary functions, then restore the original identities.
const std = @import("std");
const model = @import("model_reader");
const query = @import("query.zig");
const recipe = @import("recipe.zig");
const preflight = @import("preflight.zig");
const infer = @import("infer.zig");
const m = @import("materialize.zig");
const edit = @import("operation_model.zig");
const proposals = @import("proposals.zig");
const Value = model.Value;
const Node = recipe.Node;
const TypeRef = recipe.TypeRef;
const Rewrite = struct { id: []const u8, body: *const Node };
pub const Result = struct { kind: []const u8, transaction: Value, snapshot: Value, candidate: Value, diff: Value, rewritten: []const Value, summary: []const u8, review_fields: Value = .json_null, touched_types: []const []const u8 = &.{}, touched_capabilities: []const []const u8 = &.{} };

fn reject(r: *query.Request, prefix: []const u8, suffix: []const u8, message: []const u8) anyerror {
    return preflight.rejected(r, try std.fmt.allocPrint(r.a, "{s}{s}", .{ prefix, suffix }), message);
}
fn owner(d: *recipe.Decoder, f: Value) !void {
    const ps = model.arrayItems(f, "parameters");
    const names = try d.r.a.alloc([]const u8, ps.len);
    for (ps, names) |p, *n| n.* = query.nameOf(p);
    d.owner = .{ .name = query.nameOf(f), .parameters = names, .existing = f };
}
fn decodedBody(d: *recipe.Decoder, path: []const u8, original: std.json.Value, raw: []const u8) !*const Node {
    var v = original;
    try recipe.resolveNames(d, path, &v, raw);
    if (d.owner != null) return recipe.bindExistingTypes(d, try d.recipe(path, v));
    return d.recipe(path, v);
}

pub fn simulateRequest(r: *query.Request, out: *std.Io.Writer) anyerror!void {
    const op = r.root.get("op").?.string;
    const by_name = std.mem.endsWith(u8, op, "ByName");
    const parameter = std.mem.indexOf(u8, op, "Parameter") != null;
    const allowed: []const []const u8 = if (by_name) (if (parameter) &.{ "id", "op", "function", "parameter", "newParameterType", "targetReplacement", "callerRewrites", "structuredReview", "detail", "expectedSnapshotId" } else &.{ "id", "op", "function", "newResultType", "targetReplacement", "callerRewrites", "structuredReview", "detail", "expectedSnapshotId" }) else (if (parameter) &.{ "id", "op", "targetFunctionId", "targetParameterId", "newParameterType", "targetReplacement", "callerRewrites", "structuredReview", "detail", "expectedSnapshotId" } else &.{ "id", "op", "targetFunctionId", "newResultType", "targetReplacement", "callerRewrites", "structuredReview", "detail", "expectedSnapshotId" });
    const required = allowed[0 .. allowed.len - 3];
    var d: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line) };
    const root: std.json.Value = .{ .object = r.root };
    try d.ensureFields("", root, try std.fmt.allocPrint(r.a, "{s} request", .{op}), allowed, required);
    var expected: []const u8 = r.snapshot_id;
    if (!by_name and r.root.get("expectedSnapshotId") != null) expected = try d.stableId("expectedSnapshotId", try d.requiredString(root, "expectedSnapshotId"));
    var target: ?Value = null;
    const fid = if (by_name) blk: {
        target = try d.selector("/function", try d.property(root, "function"), "function");
        break :blk query.idOf(target.?);
    } else try d.stableId("targetFunctionId", try d.requiredString(root, "targetFunctionId"));
    const pid = if (!parameter) "" else if (by_name) query.idOf(try d.member("parameter on {s}", .{query.nameOf(target.?)}, target.?, "parameters", try d.requiredString(root, "parameter"))) else try d.stableId("targetParameterId", try d.requiredString(root, "targetParameterId"));
    if (by_name and r.root.get("expectedSnapshotId") != null) expected = try d.stableId("expectedSnapshotId", try d.requiredString(root, "expectedSnapshotId"));
    if (by_name) try owner(&d, target.?);
    const type_key = if (parameter) "newParameterType" else "newResultType";
    var new_type = try d.anyType(try std.fmt.allocPrint(r.a, "/{s}", .{type_key}), try d.property(root, type_key));
    if (by_name) new_type = try recipe.bindType(&d, new_type);
    const body = try decodedBody(&d, "/targetReplacement", try d.property(root, "targetReplacement"), try recipe.rawProperty(r.a, r.line, "targetReplacement"));
    const items = try d.array(root, "callerRewrites");
    const rewrites = try r.a.alloc(Rewrite, items.len);
    const sources = try @import("recipe_file.zig").rawArrayItems(r.a, try recipe.rawProperty(r.a, r.line, "callerRewrites"));
    for (items, rewrites, 0..) |item, *rewrite, i| {
        const path = try std.fmt.allocPrint(r.a, "/callerRewrites/{d}", .{i});
        const key = if (by_name) "function" else "callerFunctionId";
        try d.ensureFields(path, item, if (by_name) "parameter type caller rewrite by name" else "parameter type caller rewrite", &.{ key, "replacement" }, &.{ key, "replacement" });
        const id = if (by_name) blk: {
            const f = try d.selector(try std.fmt.allocPrint(r.a, "{s}/function", .{path}), try d.property(item, key), "function");
            try owner(&d, f);
            break :blk query.idOf(f);
        } else try d.stableId(key, try d.requiredString(item, key));
        rewrite.* = .{ .id = id, .body = try decodedBody(&d, try std.fmt.allocPrint(r.a, "{s}/replacement", .{path}), try d.property(item, "replacement"), try recipe.rawProperty(r.a, sources[i], "replacement")) };
    }
    try r.ensureUsable();
    const result = try simulate(r, parameter, fid, pid, new_type, body, rewrites, expected);
    try proposals.publishMigration(r, out, result);
}

pub fn creation(r: *query.Request, f: Value, body: *const Node) !preflight.Creation {
    const ps = model.arrayItems(f, "parameters");
    const parameters = try r.a.alloc(preflight.Parameter, ps.len);
    for (ps, parameters) |p, *v| v.* = .{ .name = query.nameOf(p), .type = try infer.typeOf(r.a, model.property(p, "type").?) };
    const es = model.arrayItems(f, "requiredEffects");
    const effects = try r.a.alloc([]const u8, es.len);
    for (es, effects) |e, *v| v.* = model.utf8(model.decoded(e));
    return .{ .expected_snapshot_id = r.snapshot_id, .display_name = query.nameOf(f), .type_parameters = null, .parameters = parameters, .result_type = try infer.typeOf(r.a, model.property(f, "resultType").?), .required_effects = effects, .body = body };
}

/// Dependency and field/signature repairs replace bodies against already-installed signatures.
pub fn materializeReplacement(r: *query.Request, f: Value, body: *const Node) anyerror!Value {
    try preflight.validateRecipeBounds(r, body);
    const c = try creation(r, f, body);
    var inferred = try infer.runWithPath(r, c, &.{}, f, null, null, null, "");
    var materializer: m.Materializer = .{ .a = r.a, .r = r, .types = &inferred, .fresh = &.{}, .function_id = model.property(f, "id").?, .parameters = model.arrayItems(f, "parameters") };
    return edit.set(r.a, f, "body", try materializer.graph(null, body));
}

pub fn validate(r: *query.Request, candidate: Value) anyerror!void {
    var source: std.Io.Writer.Allocating = .init(r.a);
    try m.write(&source.writer, candidate);
    const read = model.readDocument(r.a, source.written());
    const issues = try edit.validatedIssues(r, read);
    if (issues.len == 0) return;
    var details: std.Io.Writer.Allocating = .init(r.a);
    try details.writer.writeAll("{\"diagnostics\":[");
    for (issues, 0..) |issue, i| {
        if (i > 0) try details.writer.writeByte(',');
        try details.writer.writeAll("{\"code\":\"InvalidProposedSnapshot\",\"message\":");
        try @import("root").writeString(&details.writer, try std.fmt.allocPrint(r.a, "{s}: {s}", .{ issue.code, model.utf8(issue.message) }));
        try details.writer.writeByte('}');
    }
    try details.writer.writeAll("]}");
    r.diagnostic = .{ .code = "InvalidProposedSnapshot", .message = try std.fmt.allocPrint(r.a, "{s}: {s}", .{ issues[0].code, model.utf8(issues[0].message) }) };
    r.rejection = .{ .code = "SimulationRejected", .message = "The structural request was rejected.", .details = details.written() };
    return error.Rejected;
}

pub fn bodyObligation(r: *query.Request, f: Value) ?[]const u8 {
    for (model.arrayItems(r.model, "obligations")) |o| for (model.bodyNodes(r.a, f)) |n| if (m.same(query.idOf(n), edit.text(o, "callNodeId"))) return query.idOf(o);
    return null;
}
fn slot(node: *const Node, name: []const u8) ?recipe.Data {
    for (node.slots) |s| if (m.same(s.name, name)) return s.data;
    return null;
}
fn make(r: *query.Request, case: []const u8, slots: []const recipe.Slot) !*const Node {
    const n = try r.a.create(Node);
    n.* = .{ .case = case, .slots = try r.a.dupe(recipe.Slot, slots) };
    return n;
}
const Context = struct { prefix: []const u8, owner: Value, target: Value, temporary: ?Value };
fn rewriteTree(r: *query.Request, context: Context, node: *const Node) anyerror!*const Node {
    const ps = model.arrayItems(context.owner, "parameters");
    if (m.same(node.case, "Parameter")) {
        const id = slot(node, "parameterId").?.id;
        for (ps, 0..) |p, i| if (m.same(query.idOf(p), id)) return make(r, "ParameterSlot", &.{.{ .name = "slot", .data = .{ .int = @intCast(i) } }});
        return node;
    }
    if (m.same(node.case, "ParameterSlot")) return reject(r, context.prefix, "ParameterSlotUnavailable", "Migration recipes must reference existing parameters by stable ID.");
    if (!m.same(node.case, "DirectCall")) {
        for (node.slots) |s| {
            var uses_target = false;
            if (s.data == .id and (m.same(s.name, "functionId") or std.mem.endsWith(u8, s.name, "FunctionId")) and m.same(s.data.id, query.idOf(context.target))) uses_target = true;
            if (s.data == .implementations) for (s.data.implementations) |impl| { if (m.same(impl.function, query.idOf(context.target))) uses_target = true; };
            if (uses_target) return reject(r, context.prefix, "UnsupportedRewriteUse", try std.fmt.allocPrint(r.a, "{s} uses the target function '{s}' as a callback or function value; this bounded migration rewrites only direct calls to the target.", .{ if (context.temporary != null) try std.fmt.allocPrint(r.a, "Caller '{s}' rewrite", .{query.idOf(context.owner)}) else "The target replacement", query.idOf(context.target) }));
        }
    }
    // The recipe shape already lists all child fields; preserve its evaluation order.
    const slots = try r.a.dupe(recipe.Slot, node.slots);
    for (0..slots.len) |step| {
        // F# rewrites TaskStart's item before its context, despite its constructor order.
        const index = if (m.same(node.case, "TaskStart") and step >= 2) 5 - step else step;
        const s = &slots[index];
        switch (s.data) {
        .recipe => |n| s.data = .{ .recipe = try rewriteTree(r, context, n) },
        .recipes => |ns| {
            const mapped = try r.a.alloc(*const Node, ns.len);
            for (ns, mapped) |n, *v| v.* = try rewriteTree(r, context, n);
            s.data = .{ .recipes = mapped };
        },
        .pairs => |pairs| {
            const mapped = try r.a.dupe(recipe.Pair, pairs);
            for (mapped) |*p| p.value = try rewriteTree(r, context, p.value);
            s.data = .{ .pairs = mapped };
        },
        .arms => |arms| {
            const mapped = try r.a.dupe(recipe.Arm, arms);
            for (mapped) |*arm| arm.body = try rewriteTree(r, context, arm.body);
            s.data = .{ .arms = mapped };
        },
        else => {},
        }
    }
    const result = try make(r, node.case, slots);
    if (m.same(node.case, "DirectCall") and m.same(slot(node, "functionId").?.id, query.idOf(context.target))) {
        const pairs = try r.a.dupe(recipe.Pair, slot(result, "arguments").?.pairs);
        if (context.temporary) |temp| {
            for (pairs) |*p| for (model.arrayItems(context.target, "parameters"), model.arrayItems(temp, "parameters")) |original, temp_parameter| {
                if (m.same(p.id, query.idOf(original))) p.id = query.idOf(temp_parameter);
            };
            return make(r, "DirectCall", &.{ .{ .name = "functionId", .data = .{ .id = query.idOf(temp) } }, .{ .name = "arguments", .data = .{ .pairs = pairs } } });
        }
        for (pairs) |*p| {
            for (ps, 0..) |parameter, i| {
                if (m.same(p.id, query.idOf(parameter))) { p.slot = @intCast(i); break; }
            } else return reject(r, context.prefix, "TargetSelfCallArgumentNotFound", try std.fmt.allocPrint(r.a, "The target's call to itself binds parameter '{s}', which the target does not own.", .{p.id}));
        }
        return make(r, "SelfCall", &.{.{ .name = "arguments", .data = .{ .pairs = pairs } }});
    }
    if (m.same(node.case, "SelfCall") and context.temporary != null) {
        const pairs = try r.a.dupe(recipe.Pair, slot(result, "arguments").?.pairs);
        for (pairs) |p| if (p.slot < 0 or p.slot >= ps.len) return result;
        for (pairs) |*p| p.id = query.idOf(ps[@intCast(p.slot)]);
        return make(r, "DirectCall", &.{ .{ .name = "functionId", .data = .{ .id = query.idOf(context.owner) } }, .{ .name = "arguments", .data = .{ .pairs = pairs } } });
    }
    return result;
}
fn wrapped(r: *query.Request, prefix: []const u8, caller: ?Value) anyerror {
    if (!m.same(r.rejection.code, "SimulationRejected") or m.same(r.diagnostic.code, try std.fmt.allocPrint(r.a, "{s}UnsupportedRewriteUse", .{prefix}))) return error.Rejected;
    return reject(r, prefix, if (caller != null) "CallerRewriteInvalid" else "TargetRewriteInvalid", try std.fmt.allocPrint(r.a, "{s}: {s}: {s}", .{ if (caller) |f| try std.fmt.allocPrint(r.a, "Caller '{s}' rewrite is invalid", .{query.idOf(f)}) else "The migrated target body is invalid", r.diagnostic.code, r.diagnostic.message }));
}
fn temporary(r: *query.Request, context: Context, original: Value, body: *const Node) anyerror!Value {
    const tree = rewriteTree(r, context, body) catch {
        if (m.same(r.diagnostic.code, try std.fmt.allocPrint(r.a, "{s}UnsupportedRewriteUse", .{context.prefix}))) return error.Rejected;
        return reject(r, context.prefix, if (context.temporary != null) "CallerRewriteInvalid" else "TargetRewriteInvalid", try std.fmt.allocPrint(r.a, "{s}{s}: {s}", .{ if (context.temporary != null) try std.fmt.allocPrint(r.a, "Caller '{s}' rewrite is invalid: ", .{query.idOf(context.owner)}) else "", r.diagnostic.code, r.diagnostic.message }));
    };
    const c = try creation(r, original, tree);
    const fresh = preflight.runReplacement(r, c) catch return wrapped(r, context.prefix, if (context.temporary != null) context.owner else null);
    var inferred = infer.runWithPath(r, c, fresh, null, null, null, null, "") catch return wrapped(r, context.prefix, if (context.temporary != null) context.owner else null);
    var scratch = r.*;
    scratch.root = .empty;
    for ([_][]const u8{ "exported", "mustNotWait" }) |flag| if (model.property(original, flag) != null) try scratch.root.put(r.a, flag, .{ .bool = true });
    return m.function(&scratch, c, fresh, &inferred);
}
const Remap = struct { original: Value, temporary: Value, target: Value, temporary_target: Value };
fn remapValue(a: std.mem.Allocator, value: Value, maps: Remap) anyerror!Value {
    if (value == .array) {
        const items = try a.alloc(Value, value.array.len);
        for (value.array, items) |v, *o| o.* = try remapValue(a, v, maps);
        return .{ .array = items };
    }
    if (value != .object) return value;
    var result = value;
    const kind = if (model.property(value, "kind")) |v| model.utf8(model.decoded(v)) else "";
    if (m.same(kind, "typeParameter")) {
        var occurring: std.ArrayList([]const u8) = .empty;
        for (model.arrayItems(maps.original, "parameters")) |p| try preflight.occurring(a, try infer.typeOf(a, model.property(p, "type").?), &occurring);
        for (model.arrayItems(maps.temporary, "typeParameters"), 0..) |t, i| if (m.same(edit.text(value, "typeParameterId"), edit.text(t, "typeParameterId"))) { result = try edit.set(a, result, "typeParameterId", try m.string(a, occurring.items[i])); };
    } else if (m.same(kind, "parameterReference")) {
        for (model.arrayItems(maps.temporary, "parameters"), model.arrayItems(maps.original, "parameters")) |t, o| if (m.same(edit.text(value, "parameterId"), query.idOf(t))) { result = try edit.set(a, result, "parameterId", model.property(o, "id").?); };
    } else if (m.same(kind, "directCall") and m.same(edit.text(value, "targetFunctionId"), query.idOf(maps.temporary_target))) {
        result = try edit.set(a, result, "targetFunctionId", model.property(maps.target, "id").?);
        const args = try a.dupe(Value, model.arrayItems(value, "arguments"));
        for (args) |*arg| for (model.arrayItems(maps.temporary_target, "parameters"), model.arrayItems(maps.target, "parameters")) |t, o| { if (m.same(edit.text(arg.*, "targetParameterId"), query.idOf(t))) arg.* = try edit.set(a, arg.*, "targetParameterId", model.property(o, "id").?); };
        result = try edit.set(a, result, "arguments", .{ .array = args });
    }
    const fields = try a.dupe(model.Member, result.object);
    for (fields) |*f| f.value = try remapValue(a, f.value, maps);
    return .{ .object = fields };
}
fn sortedNodes(r: *query.Request, f: Value) ![]Value {
    const nodes = try r.a.dupe(Value, model.bodyNodes(r.a, f));
    std.mem.sort(Value, nodes, {}, struct { fn less(_: void, x: Value, y: Value) bool { return std.mem.order(u16, model.decoded(model.property(x, "id").?), model.decoded(model.property(y, "id").?)) == .lt; } }.less);
    return nodes;
}
pub fn bodyDiff(r: *query.Request, candidate: Value, before: Value, after: Value) anyerror!Value {
    var source: std.Io.Writer.Allocating = .init(r.a);
    const w = &source.writer;
    try w.writeAll("{\"ownerFunctionId\":"); try m.write(w, model.property(before, "id").?);
    try w.writeAll(",\"oldResultNodeId\":"); try m.write(w, model.property(model.property(before, "body").?, "resultNodeId").?);
    try w.writeAll(",\"newResultNodeId\":"); try m.write(w, model.property(model.property(after, "body").?, "resultNodeId").?);
    try w.writeAll(",\"removedNodes\":"); try @import("target.zig").summaries(r, w, before, try sortedNodes(r, before));
    var next = r.*; next.model = candidate;
    try w.writeAll(",\"addedNodes\":"); try @import("target.zig").summaries(&next, w, after, try sortedNodes(r, after));
    try w.writeByte('}');
    return model.parseValue(r.a, source.written());
}

fn simulate(r: *query.Request, parameter: bool, fid: []const u8, pid: []const u8, new_type: TypeRef, body: *const Node, rewrites: []const Rewrite, expected: []const u8) anyerror!Result {
    const prefix = if (parameter) "ParameterTypeMigration" else "ResultTypeMigration";
    if (!m.same(expected, r.snapshot_id)) return reject(r, prefix, "ExpectedBaseMismatch", "The migration request targets a different base snapshot.");
    const target = edit.find(r.model, "functions", fid) orelse return reject(r, prefix, "TargetNotFound", "The target function does not exist.");
    var signature = target;
    var old_type = try infer.typeOf(r.a, model.property(target, "resultType").?);
    var materializer: m.Materializer = .{ .a = r.a, .r = r, .types = undefined, .fresh = &.{}, .function_id = .json_null, .parameters = &.{} };
    if (parameter) {
        const ps = try r.a.dupe(Value, model.arrayItems(target, "parameters"));
        for (ps) |*p| {
            if (!m.same(query.idOf(p.*), pid)) continue;
            old_type = try infer.typeOf(r.a, model.property(p.*, "type").?);
            p.* = try edit.set(r.a, p.*, "type", try materializer.typeRef(new_type));
            break;
        } else return reject(r, prefix, "ParameterNotFound", "The target parameter is not owned by the target function.");
        signature = try edit.set(r.a, target, "parameters", .{ .array = ps });
    } else signature = try edit.set(r.a, target, "resultType", try materializer.typeRef(new_type));
    if (infer.eqlType(old_type, new_type)) return reject(r, prefix, "TypeUnchanged", if (parameter) "The requested parameter type is already the current type." else "The requested result type is already the current type.");
    const focus = model.focusOf(r.a, r.model, target);
    var sites: std.ArrayList(Value) = .empty;
    var caller_ids: std.ArrayList([]const u8) = .empty;
    for (focus.callers) |site| {
        const caller_id = model.utf8(site.caller);
        if (m.same(caller_id, fid)) continue;
        const caller = edit.find(r.model, "functions", caller_id).?;
        for (model.bodyNodes(r.a, caller)) |node| if (std.mem.eql(u16, model.decoded(model.property(node, "id").?), site.node)) {
            if (!m.same(edit.text(node, "kind"), "directCall")) return reject(r, prefix, "UnsupportedCallerUse", try std.fmt.allocPrint(r.a, "Caller use '{s}' is not a DirectCall; this bounded migration does not rewrite callback references.", .{query.idOf(node)}));
        };
        try sites.append(r.a, try m.object(r.a, &.{ try m.member(r.a, "callerFunctionId", try m.string(r.a, caller_id)), try m.member(r.a, "callNodeId", try m.string(r.a, model.utf8(site.node))), try m.member(r.a, "calleeFunctionId", try m.string(r.a, fid)) }));
        if (!preflight.contains(caller_ids.items, caller_id)) try caller_ids.append(r.a, caller_id);
    }
    const requested = try r.a.alloc([]const u8, rewrites.len);
    for (rewrites, requested) |rewrite, *id| id.* = rewrite.id;
    if (preflight.repeated(requested)) |id| return reject(r, prefix, "DuplicateCallerRewrite", try std.fmt.allocPrint(r.a, "Caller '{s}' has more than one replacement body.", .{id}));
    std.mem.sort([]const u8, caller_ids.items, {}, struct { fn less(_: void, x: []const u8, y: []const u8) bool { return query.ordinal({}, x, y); } }.less);
    var missing: std.ArrayList([]const u8) = .empty;
    var unrelated: std.ArrayList([]const u8) = .empty;
    for (caller_ids.items) |id| if (!preflight.contains(requested, id)) try missing.append(r.a, id);
    for (requested) |id| if (!preflight.contains(caller_ids.items, id)) try unrelated.append(r.a, id);
    std.mem.sort([]const u8, unrelated.items, {}, struct { fn less(_: void, x: []const u8, y: []const u8) bool { return query.ordinal({}, x, y); } }.less);
    if (missing.items.len + unrelated.items.len > 0) return reject(r, prefix, "CallerRewriteMismatch", try std.fmt.allocPrint(r.a, "Caller rewrites must match the complete affected caller set. Missing: [{s}]. Unrelated: [{s}].", .{ try std.mem.join(r.a, ", ", missing.items), try std.mem.join(r.a, ", ", unrelated.items) }));
    const originals = try r.a.alloc(Value, caller_ids.items.len + 1);
    originals[0] = target;
    for (caller_ids.items, originals[1..]) |id, *f| f.* = edit.find(r.model, "functions", id).?;
    for (originals, 0..) |f, i| if (bodyObligation(r, f)) |id| return reject(r, prefix, if (i == 0) "TargetBodyReferencedByObligation" else "CallerBodyReferencedByObligation", try std.fmt.allocPrint(r.a, "{s}StructuralBodyReferencedByObligation: Obligation '{s}' targets the body being replaced.", .{ if (i == 0) "" else try std.fmt.allocPrint(r.a, "Caller '{s}': ", .{query.idOf(f)}), id }));
    const temp = try temporary(r, .{ .prefix = prefix, .owner = target, .target = target, .temporary = null }, signature, body);
    _ = try m.id(r.a); // The temporary target's ordinary creation transaction.
    const temp_snapshot = try m.id(r.a);
    var temp_root = try edit.append(r.a, r.model, "functions", temp);
    temp_root = try edit.set(r.a, temp_root, "snapshotId", temp_snapshot);
    temp_root = try edit.set(r.a, temp_root, "parentSnapshotId", try m.string(r.a, r.snapshot_id));
    validate(r, temp_root) catch return wrapped(r, prefix, null);
    var scratch = r.*;
    scratch.model = temp_root;
    scratch.snapshot_id = model.utf8(model.decoded(temp_snapshot));
    const rewritten = try r.a.alloc(Value, originals.len);
    rewritten[0] = try edit.set(r.a, signature, "body", try remapValue(r.a, model.property(temp, "body").?, .{ .original = signature, .temporary = temp, .target = target, .temporary_target = temp }));
    for (originals[1..], rewritten[1..]) |f, *after| {
        const replacement = for (rewrites) |rewrite| { if (m.same(rewrite.id, query.idOf(f))) break rewrite.body; } else unreachable;
        const caller = temporary(&scratch, .{ .prefix = prefix, .owner = f, .target = target, .temporary = temp }, f, replacement) catch |err| { r.rejection = scratch.rejection; return err; };
        after.* = try edit.set(r.a, f, "body", try remapValue(r.a, model.property(caller, "body").?, .{ .original = f, .temporary = caller, .target = target, .temporary_target = temp }));
    }
    const transaction = try m.id(r.a);
    const snapshot = try m.id(r.a);
    var candidate = r.model;
    for (rewritten) |f| candidate = try edit.replace(r.a, candidate, "functions", f);
    candidate = try edit.set(r.a, candidate, "snapshotId", snapshot);
    candidate = try edit.set(r.a, candidate, "parentSnapshotId", try m.string(r.a, r.snapshot_id));
    try validate(r, candidate);
    const diffs = try r.a.alloc(Value, originals.len);
    for (originals, rewritten, diffs) |before, after, *diff| diff.* = try bodyDiff(r, candidate, before, after);
    var change_fields: std.ArrayList(model.Member) = .empty;
    try change_fields.append(r.a, try m.member(r.a, "functionId", model.property(target, "id").?));
    if (parameter) try change_fields.append(r.a, try m.member(r.a, "parameterId", try m.string(r.a, pid)));
    try change_fields.appendSlice(r.a, &.{ try m.member(r.a, "oldType", try materializer.typeRef(old_type)), try m.member(r.a, "newType", try materializer.typeRef(new_type)) });
    const diff = try m.object(r.a, &.{ try m.member(r.a, if (parameter) "parameterChange" else "resultChange", .{ .object = change_fields.items }), try m.member(r.a, "affectedCallSites", .{ .array = sites.items }), try m.member(r.a, "bodyRewrites", .{ .array = diffs }) });
    return .{ .kind = if (parameter) "parameterTypeMigration" else "resultTypeMigration", .transaction = transaction, .snapshot = snapshot, .candidate = candidate, .diff = diff, .rewritten = rewritten, .summary = if (parameter) try std.fmt.allocPrint(r.a, "parameterTypeMigration of '{s}' parameter '{s}'", .{ query.nameOf(target), blk: { for (model.arrayItems(target, "parameters")) |p| if (m.same(query.idOf(p), pid)) break :blk query.nameOf(p); unreachable; } }) else try std.fmt.allocPrint(r.a, "resultTypeMigration of '{s}'", .{query.nameOf(target)}) };
}
