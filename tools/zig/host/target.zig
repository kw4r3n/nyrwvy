//! TargetedReplacementGraph and AgentExpressionStructuralWorkflow.Targeted.
const std = @import("std");
const model = @import("model_reader");
const query = @import("query.zig");
const recipe = @import("recipe.zig");
const preflight = @import("preflight.zig");
const infer = @import("infer.zig");
const m = @import("materialize.zig");
const viewer = @import("viewer.zig");
const wire = @import("wire.zig");
const Value = model.Value;
const Allocator = std.mem.Allocator;
const Writer = std.Io.Writer;
const Set = std.StringHashMapUnmanaged(void);
fn text(v: Value) []const u8 { return model.utf8(model.decoded(v)); }
fn prop(v: Value, key: []const u8) []const u8 { return text(model.property(v, key).?); }
fn has(nodes: []const Value, id: []const u8) bool {
    for (nodes) |n| if (m.same(query.idOf(n), id)) return true;
    return false;
}
fn changed(a: Allocator, v: Value, key: []const u8, value: Value) anyerror!Value {
    const fields = try a.dupe(model.Member, model.objectMembers(v));
    for (fields) |*f| if (m.same(model.utf8(f.name), key)) { f.value = value; break; };
    return .{ .object = fields };
}
fn originalUses(a: Allocator, n: *const recipe.Node) anyerror!usize {
    var count: usize = if (m.same(n.case, "OriginalTarget")) 1 else 0;
    for (try recipe.children(a, n)) |c| count += try originalUses(a, c);
    return count;
}
fn collect(a: Allocator, nodes: []const Value, target: []const u8, set: *Set, id: []const u8) anyerror!void {
    if (m.same(id, target) or set.contains(id)) return;
    for (nodes) |n| if (m.same(query.idOf(n), id)) {
        try set.put(a, id, {});
        for (model.operandIdsOf(n)) |d| try collect(a, nodes, target, set, model.utf8(d));
        for (model.scopesOf(n)) |scope| for (model.arrayItems(scope.graph, "nodes")) |child| try collect(a, nodes, target, set, query.idOf(child));
        return;
    };
}
const ResultUse = struct { id: []const u8, owner: ?Value };
fn results(a: Allocator, graph: Value, owner: ?Value, out: *std.ArrayList(ResultUse)) anyerror!void {
    try out.append(a, .{ .id = prop(graph, "resultNodeId"), .owner = owner });
    for (model.arrayItems(graph, "nodes")) |n| for (model.scopesOf(n)) |scope| try results(a, scope.graph, n, out);
}
fn owning(graph: Value, id: []const u8) ?Value {
    if (has(model.arrayItems(graph, "nodes"), id)) return graph;
    for (model.arrayItems(graph, "nodes")) |n| for (model.scopesOf(n)) |scope| if (owning(scope.graph, id)) |g| return g;
    return null;
}
/// Map nested graphs without rewriting any operand IDs. The replacement root retains the target ID.
fn rewrite(a: Allocator, value: Value, target: []const u8, replacement: []const Value, removed: Set) anyerror!Value {
    switch (value) {
        .object, .typed => {
            const fields = model.objectMembers(value);
            const copy = try a.dupe(model.Member, fields);
            for (copy) |*f| {
                if (m.same(model.utf8(f.name), "nodes")) {
                    var nodes: std.ArrayList(Value) = .empty;
                    for (f.value.array) |n| {
                        if (removed.contains(query.idOf(n))) continue;
                        if (m.same(query.idOf(n), target)) try nodes.appendSlice(a, replacement)
                        else try nodes.append(a, try rewrite(a, n, target, replacement, removed));
                    }
                    f.value = .{ .array = nodes.items };
                } else f.value = try rewrite(a, f.value, target, replacement, removed);
            }
            return .{ .object = copy };
        },
        .array => |items| {
            const copy = try a.alloc(Value, items.len);
            for (items, copy) |v, *dest| dest.* = try rewrite(a, v, target, replacement, removed);
            return .{ .array = copy };
        },
        else => return value,
    }
}

pub const Change = struct { target: []const u8, removed: Set, retained: Set };

pub fn simulate(r: *query.Request, out: *Writer, function_id: []const u8, expected: []const u8, target_id: []const u8, tree: *const recipe.Node) anyerror!void {
    const a = r.a;
    if (!m.same(expected, r.snapshot_id)) return preflight.rejected(r, "StructuralExpectedBaseMismatch", "The structural request targets a different base snapshot.");
    const f = preflight.declaration(r, "functions", function_id) orelse return preflight.rejected(r, "StructuralTargetNotFound", "The target function does not exist.");
    const body = model.property(f, "body").?;
    const nodes = model.bodyNodes(a, f);
    const node = for (nodes) |n| { if (m.same(query.idOf(n), target_id)) break n; } else return preflight.rejected(r, "TargetedExpressionTargetNotFound", "The target node does not exist in the target function.");
    try preflight.validateRecipeBounds(r, tree);
    const completion = model.completionOf(a, r.model, f, model.property(node, "id").?.string) orelse return preflight.rejected(r, "TargetedExpressionTargetTypeUnavailable", "The target node type could not be derived from the validated base snapshot.");
    const target_type: infer.Completion = switch (completion) { .normal => |v| .{ .normal = try infer.typeOf(a, v) }, .aborts => .aborts };
    var candidates: Set = .empty;
    for (model.operandIdsOf(node)) |d| try collect(a, nodes, target_id, &candidates, model.utf8(d));
    var owned: Set = .empty;
    for (model.scopesOf(node)) |scope| {
        for (model.arrayItems(scope.graph, "nodes")) |n| try collect(a, nodes, target_id, &candidates, query.idOf(n));
        // The owned cone is all nested nodes, even shared ones.
        const fake = try m.object(a, &.{try m.member(a, "body", scope.graph)});
        for (model.bodyNodes(a, fake)) |n| try owned.put(a, query.idOf(n), {});
    }
    var uses: std.ArrayList(ResultUse) = .empty;
    try results(a, body, null, &uses);
    var retained: Set = .empty;
    for (nodes) |n| if (!m.same(query.idOf(n), target_id) and !candidates.contains(query.idOf(n))) {
        for (model.operandIdsOf(n)) |d| if (candidates.contains(model.utf8(d))) try collect(a, nodes, target_id, &retained, model.utf8(d));
    };
    for (uses.items) |use| if (candidates.contains(use.id) and (use.owner == null or (!m.same(query.idOf(use.owner.?), target_id) and !candidates.contains(query.idOf(use.owner.?))))) try collect(a, nodes, target_id, &retained, use.id);
    var removed = try owned.clone(a);
    var iter = candidates.keyIterator();
    while (iter.next()) |id| if (!retained.contains(id.*)) try removed.put(a, id.*, {});
    var shared: Set = .empty;
    iter = retained.keyIterator();
    while (iter.next()) |id| if (!owned.contains(id.*)) try shared.put(a, id.*, {});
    const count = try originalUses(a, tree);
    var original: ?Value = null;
    var moved: Set = .empty;
    if (count == 1) {
        if (target_type == .aborts) return preflight.rejected(r, "TargetedExpressionOriginalTargetAborts", "originalTarget needs a target that produces a value, but the target node always aborts.");
        var original_nodes: std.ArrayList(Value) = .empty;
        for (model.arrayItems(owning(body, target_id).?, "nodes")) |n| if (removed.contains(query.idOf(n))) {
            try moved.put(a, query.idOf(n), {});
            try original_nodes.append(a, n);
        };
        const root = try m.id(a);
        try original_nodes.append(a, try changed(a, node, "id", root));
        original = try m.object(a, &.{ try m.member(a, "nodes", .{ .array = original_nodes.items }), try m.member(a, "resultNodeId", root) });
    } else if (count > 1) return preflight.rejected(r, "TargetedExpressionOriginalTargetRepeated", "originalTarget may appear at most once in a replacement.");
    const parameters = model.arrayItems(f, "parameters");
    const owner_parameters = try a.alloc(preflight.Parameter, parameters.len);
    for (parameters, owner_parameters) |p, *v| v.* = .{ .name = query.nameOf(p), .type = try infer.typeOf(a, model.property(p, "type").?) };
    const effects = model.arrayItems(f, "requiredEffects");
    const owner_effects = try a.alloc([]const u8, effects.len);
    for (effects, owner_effects) |e, *v| v.* = text(e);
    const owner: preflight.Creation = .{ .expected_snapshot_id = expected, .display_name = query.nameOf(f), .type_parameters = null, .parameters = owner_parameters, .result_type = try infer.typeOf(a, model.property(f, "resultType").?), .required_effects = owner_effects, .body = tree };
    const frames = (try query.framesAt(a, body, &.{}, model.decoded(model.property(node, "id").?))).?;
    var type_frames: ?*const infer.Frame = null;
    var value_frames: ?*const m.Frame = null;
    var index = frames.len;
    while (index > 0) {
        index -= 1;
        const frame = frames[index];
        const slots = try a.alloc(infer.Binding, frame.bindings.len);
        const values = try a.alloc(m.Binding, frame.bindings.len);
        for (frame.bindings, slots, values) |b, *t, *v| {
            t.* = .{ .key = if (b.field) |field| .{ .field = text(field) } else if (m.same(b.slot, "error")) .err else .value, .type = try infer.typeOf(a, model.property(b.pattern, "type").?) };
            v.* = .{ .slot = b.slot, .field = if (b.field) |field| text(field) else null, .value = b.pattern };
        }
        const variants = try a.alloc([]const u8, frame.variants.len);
        for (frame.variants, variants) |v, *s| s.* = text(v);
        const tf = try a.create(infer.Frame);
        tf.* = .{ .kind = if (m.same(frame.role, "unionArm")) .{ .union_arm = variants } else if (m.same(frame.role, "bind")) .bind_body else if (m.same(frame.role, "optionSome")) .option_some else if (m.same(frame.role, "optionNone")) .option_none else if (m.same(frame.role, "resultOk")) .result_ok else .result_err, .slots = slots, .next = type_frames };
        type_frames = tf;
        const vf = try a.create(m.Frame);
        vf.* = .{ .bindings = values, .next = value_frames };
        value_frames = vf;
    }
    var inferred = try infer.runTarget(r, owner, &.{}, f, if (original != null) target_type.normal else null, type_frames, target_type);
    if (inferred.completions.get(tree).?.value == .aborts) {
        for (nodes) |n| for (model.operandIdsOf(n)) |d| if (m.same(model.utf8(d), target_id)) return preflight.rejected(r, "TargetedExpressionAbruptValueUse", try std.fmt.allocPrint(a, "The replacement recipe always aborts, but node '{s}' uses the target node as a value; an abrupt replacement is valid only where the target is not used as a value, such as a branch or function result.", .{query.idOf(n)}));
        var is_result = false;
        for (uses.items) |use| if (m.same(use.id, target_id)) { is_result = true; break; };
        if (m.same(tree.case, "Abort") and !is_result) return preflight.rejected(r, "TargetedExpressionAbortNotGraphResult", "The replacement recipe is an abort, but the target node is not the result of its graph; an abort may replace only a branch or function result.");
    }
    if (original != null) removed = .empty;
    for (model.arrayItems(r.model, "obligations")) |o| if (m.same(prop(o, "callNodeId"), target_id) or removed.contains(prop(o, "callNodeId"))) return preflight.rejected(r, "TargetedExpressionReferencedByObligation", try std.fmt.allocPrint(a, "Obligation '{s}' targets the expression being replaced or a node that replacement would remove.", .{query.idOf(o)}));
    var materializer: m.Materializer = .{ .a = a, .r = r, .types = &inferred, .fresh = &.{}, .function_id = model.property(f, "id").?, .parameters = parameters, .original = original };
    const graph = try materializer.graph(value_frames, tree);
    const replacement = try a.dupe(Value, model.arrayItems(graph, "nodes"));
    for (replacement) |*n| if (m.same(query.idOf(n.*), prop(graph, "resultNodeId"))) { n.* = try changed(a, n.*, "id", try m.string(a, target_id)); };
    const pruned = try rewrite(a, body, "", &.{}, moved);
    const replaced = try rewrite(a, pruned, target_id, replacement, .empty);
    const result = try changed(a, f, "body", try rewrite(a, replaced, "", &.{}, removed));
    try @import("proposals.zig").publish(r, out, result, f, .{ .target = target_id, .removed = removed, .retained = shared });
}

fn sorted(a: Allocator, nodes: []const Value) anyerror![]Value {
    const copy = try a.dupe(Value, nodes);
    std.mem.sort(Value, copy, {}, struct { fn less(_: void, x: Value, y: Value) bool { return std.mem.order(u16, model.decoded(model.property(x, "id").?), model.decoded(model.property(y, "id").?)) == .lt; } }.less);
    return copy;
}
pub fn summaries(r: *query.Request, out: *Writer, f: Value, nodes: []const Value) anyerror!void {
    try out.writeByte('[');
    for (nodes, 0..) |n, i| { if (i > 0) try out.writeByte(','); try summary(r, out, f, n); }
    try out.writeByte(']');
}
fn summary(r: *query.Request, out: *Writer, f: Value, n: Value) anyerror!void {
    const a = r.a;
    const kind = prop(n, "kind");
    const role = try a.dupe(u8, kind);
    role[0] = std.ascii.toUpper(role[0]);
    try out.writeAll("{\"nodeId\":");
    try m.write(out, model.property(n, "id").?);
    try out.writeAll(",\"role\":");
    try @import("root").writeString(out, role);
    try out.writeAll(",\"type\":");
    var node_type: ?Value = null;
    const focus = model.focusOf(a, r.model, f);
    for (focus.node_types) |t| if (m.same(model.utf8(t.id), query.idOf(n))) { node_type = t.type; break; };
    // Structural summaries use typeRef (no owner's type-parameter names).
    if (node_type) |t| try wire.typeRef(out, t, &.{}) else try out.writeAll("null");
    try out.writeAll(",\"dependencies\":[");
    for (model.operandIdsOf(n), 0..) |d, i| { if (i > 0) try out.writeByte(','); try model.writeString(out, d); }
    try out.writeAll("],\"valueDependencies\":");
    try viewer.writeDependencies(out, n, kind);
    const parameter = if (m.same(kind, "parameterReference")) model.property(n, "parameterId") else null;
    var function: ?Value = null;
    if (!m.same(kind, "capabilityConstruct")) {
        const functions = model.functionIdsOf(n);
        if (functions.len > 0) function = try m.string(a, model.utf8(functions[0]));
    }
    try out.writeAll(",\"parameterId\":");
    try m.write(out, parameter orelse .json_null);
    try out.writeAll(",\"functionId\":");
    try m.write(out, function orelse .json_null);
    try out.writeAll(",\"float64Bits\":");
    try m.write(out, if (m.same(kind, "float64Literal")) model.property(n, "bits").? else .json_null);
    try out.writeAll(",\"literal\":");
    if (m.same(kind, "boolLiteral") or m.same(kind, "int64Literal") or m.same(kind, "stringLiteral")) {
        try out.print("{{\"kind\":\"{s}\",\"value\":", .{kind[0 .. kind.len - 7]});
        const v = model.property(n, "value").?;
        if (m.same(kind, "stringLiteral") and model.decoded(v).len > 256) {
            const units = model.decoded(v);
            const cut: usize = if (units[255] >= 0xd800 and units[255] <= 0xdbff and units[256] >= 0xdc00 and units[256] <= 0xdfff) 255 else 256;
            try model.writeString(out, units[0..cut]);
            try out.print(",\"truncated\":true,\"length\":{d}", .{units.len});
        } else if (m.same(kind, "int64Literal") and v == .number) try @import("root").writeString(out, v.number)
        else try m.write(out, v);
        try out.writeByte('}');
    } else try out.writeAll("null");
    var name: ?Value = null;
    if (parameter) |p| {
        for (model.arrayItems(f, "parameters")) |v| if (m.same(query.idOf(v), text(p))) { name = model.property(v, "displayName"); break; };
    } else if (m.same(kind, "fieldAccess") or m.same(kind, "unionConstruct")) {
        const key: []const u8 = if (m.same(kind, "fieldAccess")) "fieldId" else "variantId";
        for (model.arrayItems(r.model, "types")) |t| {
            if (m.same(key, "fieldId")) for (model.arrayItems(t, "fields")) |v| if (m.same(query.idOf(v), prop(n, key))) { name = model.property(v, "displayName"); };
            for (model.arrayItems(t, "variants")) |v| {
                if (m.same(key, "variantId") and m.same(query.idOf(v), prop(n, key))) name = model.property(v, "displayName");
                if (m.same(key, "fieldId")) for (model.arrayItems(v, "fields")) |field| if (m.same(query.idOf(field), prop(n, key))) { name = model.property(field, "displayName"); };
            }
        }
    } else if (m.same(kind, "productConstruct") or m.same(kind, "productUpdate") or m.same(kind, "capabilityConstruct")) {
        const cap = m.same(kind, "capabilityConstruct");
        if (preflight.declaration(r, if (cap) "capabilities" else "types", prop(n, if (cap) "capabilityId" else "typeId"))) |v| name = model.property(v, "displayName");
    } else if (m.same(kind, "capabilityInvoke")) {
        for (model.arrayItems(r.model, "capabilities")) |c| for (model.arrayItems(c, "operations")) |v| if (m.same(query.idOf(v), prop(n, "operationId"))) { name = model.property(v, "displayName"); };
    } else if (function) |id| {
        if (preflight.declaration(r, "functions", text(id))) |v| name = model.property(v, "displayName");
    }
    try out.writeAll(",\"referenceDisplayName\":");
    try m.write(out, name orelse .json_null);
    try out.writeByte('}');
}

fn bindings(a: Allocator, f: Value) anyerror![]const Value {
    var out: std.ArrayList(Value) = .empty;
    for (model.bodyNodes(a, f)) |n| for (model.scopesOf(n)) |scope| try out.appendSlice(a, scope.bindings);
    return out.items;
}
fn patternReferences(r: *query.Request, out: *Writer, f: Value, base_nodes: []const Value, target: []const u8) anyerror!void {
    try out.writeByte('[');
    var first = true;
    for (model.bodyNodes(r.a, f)) |n| {
        if (!m.same(prop(n, "kind"), "patternReference") or (has(base_nodes, query.idOf(n)) and !m.same(query.idOf(n), target))) continue;
        const frames = (try query.framesAt(r.a, model.property(f, "body").?, &.{}, model.decoded(model.property(n, "id").?))).?;
        for (frames, 0..) |frame, depth| for (frame.bindings) |b| if (m.same(query.idOf(b.pattern), prop(n, "patternBindingId"))) {
            if (!first) try out.writeByte(',');
            first = false;
            try out.writeAll("{\"nodeId\":");
            try m.write(out, model.property(n, "id").?);
            try out.print(",\"scopeDepth\":{d},\"ownerNodeId\":", .{depth});
            try m.write(out, model.property(frame.owner, "id").?);
            try out.print(",\"role\":\"{s}\"", .{frame.role});
            if (frame.variants.len > 0) {
                try out.writeAll(",\"variantIds\":[");
                for (frame.variants, 0..) |v, i| { if (i > 0) try out.writeByte(','); try m.write(out, v); }
                try out.writeByte(']');
            }
            try out.print(",\"binding\":{{\"slot\":\"{s}\"", .{b.slot});
            if (b.field) |field| { try out.writeAll(",\"fieldId\":"); try m.write(out, field); }
            try out.writeAll(",\"bindingId\":");
            try m.write(out, model.property(b.pattern, "id").?);
            try out.writeAll(",\"type\":");
            try wire.typeRef(out, model.property(b.pattern, "type").?, model.arrayItems(f, "typeParameters"));
            if (model.property(b.pattern, "displayName")) |name| { try out.writeAll(",\"displayName\":"); try m.write(out, name); }
            try out.writeAll("}}");
        };
    }
    try out.writeByte(']');
}

pub fn diff(r: *query.Request, out: *Writer, old: Value, proposed: Value, change: Change, proposed_root: Value) anyerror!void {
    const a = r.a;
    const base_nodes = model.bodyNodes(a, old);
    const new_nodes = model.bodyNodes(a, proposed);
    const previous = for (base_nodes) |n| { if (m.same(query.idOf(n), change.target)) break n; } else unreachable;
    const next = for (new_nodes) |n| { if (m.same(query.idOf(n), change.target)) break n; } else unreachable;
    var candidate = r.*;
    candidate.model = try model.ownTyped(r.a, proposed_root, false);
    try out.writeAll("{\"ownerFunctionId\":");
    try m.write(out, model.property(old, "id").?);
    try out.writeAll(",\"targetNodeId\":");
    try @import("root").writeString(out, change.target);
    try out.writeAll(",\"previousTarget\":");
    try summary(r, out, old, previous);
    try out.writeAll(",\"proposedTarget\":");
    try summary(&candidate, out, proposed, next);
    var removed: std.ArrayList(Value) = .empty;
    var retained: std.ArrayList(Value) = .empty;
    var added: std.ArrayList(Value) = .empty;
    for (base_nodes) |n| {
        if (change.removed.contains(query.idOf(n))) try removed.append(a, n);
        if (change.retained.contains(query.idOf(n))) try retained.append(a, n);
    }
    for (new_nodes) |n| if (!has(base_nodes, query.idOf(n))) try added.append(a, n);
    try out.writeAll(",\"removedNodes\":");
    try summaries(r, out, old, try sorted(a, removed.items));
    try out.writeAll(",\"addedNodes\":");
    try summaries(&candidate, out, proposed, added.items);
    const base_bindings = try bindings(a, old);
    var added_bindings: std.ArrayList(Value) = .empty;
    for (try bindings(a, proposed)) |b| if (!has(base_bindings, query.idOf(b))) try added_bindings.append(a, b);
    try out.writeAll(",\"addedPatternBindingIds\":[");
    for (try sorted(a, added_bindings.items), 0..) |b, i| { if (i > 0) try out.writeByte(','); try m.write(out, model.property(b, "id").?); }
    try out.writeAll("],\"patternReferences\":");
    try patternReferences(r, out, proposed, base_nodes, change.target);
    try out.writeAll(",\"retainedSharedDependencies\":");
    try summaries(r, out, old, try sorted(a, retained.items));
    try out.writeAll(",\"affectedUses\":[");
    var first = true;
    for (base_nodes) |n| for (viewer.placesOf(prop(n, "kind"))) |place| {
        const value = model.property(n, place.property) orelse continue;
        const items: []const Value = if (place.array) value.array else &.{value};
        for (items, 0..) |item, i| {
            const id = if (item == .string) text(item) else prop(item, "valueNodeId");
            if (!m.same(id, change.target)) continue;
            if (!first) try out.writeByte(',');
            first = false;
            try out.writeAll("{\"kind\":\"nodeDependency\",\"consumerNodeId\":");
            try m.write(out, model.property(n, "id").?);
            try out.print(",\"role\":\"{s}\"", .{place.role});
            if (item == .string) {
                if (place.array) try out.print(",\"index\":{d}", .{i});
            } else {
                const field = m.same(place.role, "FieldValue");
                const key: []const u8 = if (field) "targetFieldId" else if (m.same(place.role, "CapabilityArgument")) "targetCapabilityParameterId" else "targetParameterId";
                try out.print(",\"{s}\":", .{if (field) @as([]const u8, "fieldId") else "parameterId"});
                try m.write(out, model.property(item, key).?);
            }
            try out.writeByte('}');
        }
    };
    var uses: std.ArrayList(ResultUse) = .empty;
    try results(a, model.property(old, "body").?, null, &uses);
    for (uses.items) |use| if (m.same(use.id, change.target)) {
        if (!first) try out.writeByte(',');
        first = false;
        try out.writeAll("{\"kind\":\"graphResult\",\"ownerNodeId\":");
        try m.write(out, if (use.owner) |n| model.property(n, "id").? else .json_null);
        try out.writeByte('}');
    };
    try out.writeAll("]}");
}
