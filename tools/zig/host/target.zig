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
const Model = model.Model;
const tm = model.session_model;
const builder = tm.builder;
const edit = @import("typed_edit.zig");
const Allocator = std.mem.Allocator;
const Writer = std.Io.Writer;
const Set = std.StringHashMapUnmanaged(void);
fn sameId(node: Model, wanted: []const u8) bool {
    return m.same(query.idOf(node), wanted);
}
fn has(nodes: []const Model, id: []const u8) bool {
    for (nodes) |node| if (sameId(node, id)) return true;
    return false;
}
fn writeOptional(out: *Writer, units: ?model.Str) !void {
    if (units) |value| try model.writeString(out, value) else try out.writeAll("null");
}
fn originalUses(a: Allocator, n: *const recipe.Node) anyerror!usize {
    var count: usize = if (m.same(n.case, "OriginalTarget")) 1 else 0;
    for (try recipe.children(a, n)) |c| count += try originalUses(a, c);
    return count;
}
fn collect(a: Allocator, nodes: []const Model, target: []const u8, set: *Set, id: []const u8) anyerror!void {
    if (m.same(id, target) or set.contains(id)) return;
    for (nodes) |n| if (m.same(query.idOf(n), id)) {
        try set.put(a, id, {});
        for (model.typedOperandIdsOf(n)) |d| try collect(a, nodes, target, set, model.utf8(d));
        for (model.typedScopesOf(n)) |scope| for ((scope.graph).nodes()) |child| try collect(a, nodes, target, set, query.idOf(child));
        return;
    };
}
fn collectOwned(a: Allocator, graph: Model, out: *Set) anyerror!void {
    for (graph.nodes()) |node| {
        try out.put(a, query.idOf(node), {});
        for (model.typedScopesOf(node)) |scope| try collectOwned(a, scope.graph, out);
    }
}
const ResultUse = struct { id: []const u8, owner: ?Model };
fn results(a: Allocator, graph: Model, owner: ?Model, out: *std.ArrayList(ResultUse)) anyerror!void {
    try out.append(a, .{ .id = model.utf8(graph.resultNodeId()), .owner = owner });
    for ((graph).nodes()) |n| for (model.typedScopesOf(n)) |scope| try results(a, scope.graph, n, out);
}
fn owning(graph: Model, id: []const u8) ?Model {
    if (has((graph).nodes(), id)) return graph;
    for ((graph).nodes()) |n| for (model.typedScopesOf(n)) |scope| if (owning(scope.graph, id)) |g| return g;
    return null;
}
fn rewrite(a: Allocator, graph: tm.GraphView, target: ?tm.Id, replacement: []const builder.NodeDraft, removed: Set, strings: *const tm.Interner) !tm.Body {
    var replacements: std.ArrayList(builder.Replacement) = .empty;
    for (try graph.body.depthFirst(a)) |node| {
        const id = model.utf8(strings.idText(.node, node.id()));
        if (removed.contains(id)) try replacements.append(a, .{ .node = node.id(), .nodes = &.{} }) else if (target != null and node.id() == target.?) try replacements.append(a, .{ .node = node.id(), .nodes = replacement });
    }
    return builder.rebuild(a, graph, .{ .replacements = replacements.items });
}

pub const Change = struct { target: []const u8, removed: Set, retained: Set };

pub fn simulate(r: *query.Request, out: *Writer, function_id: []const u8, expected: []const u8, target_id: []const u8, tree: *const recipe.Node) anyerror!void {
    const a = r.a;
    if (!m.same(expected, r.snapshot_id)) return preflight.rejected(r, "StructuralExpectedBaseMismatch", "The structural request targets a different base snapshot.");
    const f = preflight.typedDeclaration(r, "functions", function_id) orelse return preflight.rejected(r, "StructuralTargetNotFound", "The target function does not exist.");
    const body = (f).body();
    const nodes = model.typedBodyNodes(a, f);
    const node = for (nodes) |n| {
        if (m.same(query.idOf(n), target_id)) break n;
    } else return preflight.rejected(r, "TargetedExpressionTargetNotFound", "The target node does not exist in the target function.");
    try preflight.validateRecipeBounds(r, tree);
    const completion = model.typedCompletionOf(a, r.typedModel(), f, node.id()) orelse return preflight.rejected(r, "TargetedExpressionTargetTypeUnavailable", "The target node type could not be derived from the validated base snapshot.");
    const target_type: infer.Completion = switch (completion) {
        .normal => |v| .{ .normal = try infer.typeOf(a, v) },
        .aborts => .aborts,
    };
    var candidates: Set = .empty;
    for (model.typedOperandIdsOf(node)) |d| try collect(a, nodes, target_id, &candidates, model.utf8(d));
    var owned: Set = .empty;
    for (model.typedScopesOf(node)) |scope| {
        for ((scope.graph).nodes()) |n| try collect(a, nodes, target_id, &candidates, query.idOf(n));
        // The owned cone is all nested nodes, even shared ones.
        try collectOwned(a, scope.graph, &owned);
    }
    var uses: std.ArrayList(ResultUse) = .empty;
    try results(a, body, null, &uses);
    var retained: Set = .empty;
    for (nodes) |n| if (!m.same(query.idOf(n), target_id) and !candidates.contains(query.idOf(n))) {
        for (model.typedOperandIdsOf(n)) |d| if (candidates.contains(model.utf8(d))) try collect(a, nodes, target_id, &retained, model.utf8(d));
    };
    for (uses.items) |use| if (candidates.contains(use.id) and (use.owner == null or (!m.same(query.idOf(use.owner.?), target_id) and !candidates.contains(query.idOf(use.owner.?))))) try collect(a, nodes, target_id, &retained, use.id);
    var removed = try owned.clone(a);
    var iter = candidates.keyIterator();
    while (iter.next()) |id| if (!retained.contains(id.*)) try removed.put(a, id.*, {});
    var shared: Set = .empty;
    iter = retained.keyIterator();
    while (iter.next()) |id| if (!owned.contains(id.*)) try shared.put(a, id.*, {});
    const count = try originalUses(a, tree);
    var original: ?tm.GraphView = null;
    var moved: Set = .empty;
    if (count == 1) {
        if (target_type == .aborts) return preflight.rejected(r, "TargetedExpressionOriginalTargetAborts", "originalTarget needs a target that produces a value, but the target node always aborts.");
        var original_nodes: std.ArrayList(builder.NodeDraft) = .empty;
        for (owning(body, target_id).?.nodes()) |n| if (removed.contains(query.idOf(n))) {
            try moved.put(a, query.idOf(n), {});
            try original_nodes.append(a, try builder.fromNode(a, n.data.node));
        };
        const fresh = try m.id(a);
        const root = try r.interner.?.id(.node, model.decoded(fresh));
        var original_root = try builder.fromNode(a, node.data.node);
        original_root.id = root;
        try original_nodes.append(a, original_root);
        original = try builder.view(a, try builder.build(a, original_nodes.items, root));
    } else if (count > 1) return preflight.rejected(r, "TargetedExpressionOriginalTargetRepeated", "originalTarget may appear at most once in a replacement.");
    const parameters = (f).parameters();
    const owner_parameters = try a.alloc(preflight.Parameter, parameters.len);
    for (parameters, owner_parameters) |p, *v| v.* = .{ .name = query.nameOf(p), .type = try infer.typeOf(a, (p).declaredType()) };
    const effects = (f).requiredEffects();
    const owner_effects = try a.alloc([]const u8, effects.len);
    for (effects, owner_effects) |e, *v| v.* = model.utf8(e.literal());
    const owner: preflight.Creation = .{ .expected_snapshot_id = expected, .display_name = query.nameOf(f), .type_parameters = null, .parameters = owner_parameters, .result_type = try infer.typeOf(a, (f).resultType()), .required_effects = owner_effects, .body = tree };
    const frames = (try query.typedFramesAt(a, body, &.{}, node.id())).?;
    var type_frames: ?*const infer.Frame = null;
    var value_frames: ?*const m.Frame = null;
    var index = frames.len;
    while (index > 0) {
        index -= 1;
        const frame = frames[index];
        const slots = try a.alloc(infer.Binding, frame.bindings.len);
        const values = try a.alloc(m.Binding, frame.bindings.len);
        for (frame.bindings, slots, values) |b, *t, *v| {
            t.* = .{ .key = if (b.field) |field| .{ .field = model.utf8(field) } else if (m.same(b.slot, "error")) .err else .value, .type = try infer.typeOf(a, (b.pattern).declaredType()) };
            v.* = .{ .slot = b.slot, .field = if (b.field) |field| model.utf8(field) else null, .value = b.pattern.data.binding };
        }
        const variants = try a.alloc([]const u8, frame.variants.len);
        for (frame.variants, variants) |v, *s| s.* = model.utf8(v.literal());
        const tf = try a.create(infer.Frame);
        tf.* = .{ .kind = if (m.same(frame.role, "unionArm")) .{ .union_arm = variants } else if (m.same(frame.role, "bind")) .bind_body else if (m.same(frame.role, "optionSome")) .option_some else if (m.same(frame.role, "optionNone")) .option_none else if (m.same(frame.role, "resultOk")) .result_ok else .result_err, .slots = slots, .next = type_frames };
        type_frames = tf;
        const vf = try a.create(m.Frame);
        vf.* = .{ .bindings = values, .next = value_frames };
        value_frames = vf;
    }
    var inferred = try infer.runTarget(r, owner, &.{}, f, if (original != null) target_type.normal else null, type_frames, target_type);
    if (inferred.completions.get(tree).?.value == .aborts) {
        for (nodes) |n| for (model.typedOperandIdsOf(n)) |d| if (m.same(model.utf8(d), target_id)) return preflight.rejected(r, "TargetedExpressionAbruptValueUse", try std.fmt.allocPrint(a, "The replacement recipe always aborts, but node '{s}' uses the target node as a value; an abrupt replacement is valid only where the target is not used as a value, such as a branch or function result.", .{query.idOf(n)}));
        var is_result = false;
        for (uses.items) |use| if (m.same(use.id, target_id)) {
            is_result = true;
            break;
        };
        if (m.same(tree.case, "Abort") and !is_result) return preflight.rejected(r, "TargetedExpressionAbortNotGraphResult", "The replacement recipe is an abort, but the target node is not the result of its graph; an abort may replace only a branch or function result.");
    }
    if (original != null) removed = .empty;
    for ((r.typedModel()).obligations()) |o| if (m.same(model.utf8(o.reference("callNodeId")), target_id) or removed.contains(model.utf8(o.reference("callNodeId")))) return preflight.rejected(r, "TargetedExpressionReferencedByObligation", try std.fmt.allocPrint(a, "Obligation '{s}' targets the expression being replaced or a node that replacement would remove.", .{query.idOf(o)}));
    var materializer: m.Materializer = .{ .a = a, .r = r, .types = &inferred, .fresh = &.{}, .function_id = f.data.function.id, .parameters = f.data.function.parameters, .original = original };
    const graph = try materializer.graph(value_frames, tree);
    const replacement = try a.alloc(builder.NodeDraft, graph.graphs[0].count);
    var iterator = graph.graph(0).nodes();
    for (replacement) |*draft| {
        draft.* = try builder.fromNode(a, iterator.next().?);
        if (draft.id == graph.graphs[0].result) draft.id = node.data.node.id();
    }
    const pruned = try rewrite(a, body.data.graph, null, &.{}, moved, r.interner.?);
    const replaced = try rewrite(a, pruned.graph(0), node.data.node.id(), replacement, .empty, r.interner.?);
    const result = try edit.changed(a, f, .{ .body = try rewrite(a, replaced.graph(0), null, &.{}, removed, r.interner.?) });
    try @import("proposals.zig").publish(r, out, result, f, .{ .target = target_id, .removed = removed, .retained = shared });
}

fn sorted(a: Allocator, nodes: []const Model) anyerror![]Model {
    const copy = try a.dupe(Model, nodes);
    std.mem.sort(Model, copy, {}, struct {
        fn less(_: void, x: Model, y: Model) bool {
            return std.mem.order(u16, x.id(), y.id()) == .lt;
        }
    }.less);
    return copy;
}
pub fn summaries(r: *query.Request, out: *Writer, f: Model, nodes: []const Model) anyerror!void {
    try out.writeByte('[');
    for (nodes, 0..) |n, i| {
        if (i > 0) try out.writeByte(',');
        try summary(r, out, f, n);
    }
    try out.writeByte(']');
}
fn summary(r: *query.Request, out: *Writer, f: Model, n: Model) anyerror!void {
    const a = r.a;
    const kind = model.utf8(n.kind());
    const role = try a.dupe(u8, kind);
    role[0] = std.ascii.toUpper(role[0]);
    try out.writeAll("{\"nodeId\":");
    try model.writeString(out, n.id());
    try out.writeAll(",\"role\":");
    try out.writeAll(try query.stringOf(a, role));
    try out.writeAll(",\"type\":");
    var node_type: ?model.SemanticType = null;
    const focus = model.typedFocusOf(a, r.typedModel(), f);
    for (focus.node_types) |t| if (m.same(model.utf8(t.id), query.idOf(n))) {
        node_type = t.type;
        break;
    };
    if (node_type) |t| try wire.typeRef(out, model.semanticTypeValue(t), &.{}) else try out.writeAll("null");
    try out.writeAll(",\"dependencies\":[");
    for (model.typedOperandIdsOf(n), 0..) |dependency, i| {
        if (i > 0) try out.writeByte(',');
        try model.writeString(out, dependency);
    }
    try out.writeAll("],\"valueDependencies\":");
    try viewer.typedWriteDependencies(out, n, kind);
    const parameter: ?model.Str = if (m.same(kind, "parameterReference")) n.reference("parameterId") else null;
    var function: ?model.Str = null;
    if (!m.same(kind, "capabilityConstruct")) {
        const functions = model.typedFunctionIdsOf(n);
        if (functions.len > 0) function = functions[0];
    }
    try out.writeAll(",\"parameterId\":");
    try writeOptional(out, parameter);
    try out.writeAll(",\"functionId\":");
    try writeOptional(out, function);
    try out.writeAll(",\"float64Bits\":");
    if (m.same(kind, "float64Literal")) try out.print("\"{x:0>16}\"", .{n.payload("bits").?.bits}) else try out.writeAll("null");
    try out.writeAll(",\"literal\":");
    if (m.same(kind, "boolLiteral") or m.same(kind, "int64Literal") or m.same(kind, "stringLiteral")) {
        try out.print("{{\"kind\":\"{s}\",\"value\":", .{kind[0 .. kind.len - 7]});
        const value = n.payload("value").?;
        if (m.same(kind, "stringLiteral")) {
            const units = n.context.interner.stringText(value.lit);
            if (units.len > 256) {
                const cut: usize = if (units[255] >= 0xd800 and units[255] <= 0xdbff and units[256] >= 0xdc00 and units[256] <= 0xdfff) 255 else 256;
                try model.writeString(out, units[0..cut]);
                try out.print(",\"truncated\":true,\"length\":{d}", .{units.len});
            } else try model.writeString(out, units);
        } else if (m.same(kind, "int64Literal")) try out.writeAll(try query.stringOf(a, try std.fmt.allocPrint(a, "{d}", .{value.int}))) else try out.writeAll(if (value.bool) "true" else "false");
        try out.writeByte('}');
    } else try out.writeAll("null");
    var name: ?model.Str = null;
    if (parameter) |id| {
        for (f.parameters()) |p| if (std.mem.eql(u16, p.id(), id)) {
            name = p.displayName();
            break;
        };
    } else if (m.same(kind, "fieldAccess") or m.same(kind, "unionConstruct")) {
        const field = m.same(kind, "fieldAccess");
        const wanted = n.reference(if (field) "fieldId" else "variantId");
        for (r.typedModel().types()) |t| {
            if (field) for (t.fields()) |p| if (std.mem.eql(u16, p.id(), wanted)) {
                name = p.displayName();
            };
            for (t.variants()) |variant| {
                if (!field and std.mem.eql(u16, variant.id(), wanted)) name = variant.displayName();
                if (field) for (variant.fields()) |p| if (std.mem.eql(u16, p.id(), wanted)) {
                    name = p.displayName();
                };
            }
        }
    } else if (m.same(kind, "productConstruct") or m.same(kind, "productUpdate") or m.same(kind, "capabilityConstruct")) {
        const cap = m.same(kind, "capabilityConstruct");
        if (preflight.typedDeclaration(r, if (cap) "capabilities" else "types", model.utf8(n.reference(if (cap) "capabilityId" else "typeId")))) |item| name = item.displayName();
    } else if (m.same(kind, "capabilityInvoke")) {
        for (r.typedModel().capabilities()) |cap| for (cap.operations()) |operation| if (std.mem.eql(u16, operation.id(), n.reference("operationId"))) {
            name = operation.displayName();
        };
    } else if (function) |id| if (preflight.typedDeclaration(r, "functions", model.utf8(id))) |item| {
        name = item.displayName();
    };
    try out.writeAll(",\"referenceDisplayName\":");
    try writeOptional(out, name);
    try out.writeByte('}');
}

fn bindings(a: Allocator, f: Model) anyerror![]const Model {
    var out: std.ArrayList(Model) = .empty;
    for (model.typedBodyNodes(a, f)) |n| for (model.typedScopesOf(n)) |scope| try out.appendSlice(a, scope.bindings);
    return out.items;
}
fn patternReferences(r: *query.Request, out: *Writer, f: Model, base_nodes: []const Model, target: []const u8) anyerror!void {
    try out.writeByte('[');
    var first = true;
    for (model.typedBodyNodes(r.a, f)) |node| {
        if (!m.same(model.utf8(node.kind()), "patternReference") or (has(base_nodes, query.idOf(node)) and !sameId(node, target))) continue;
        const frames = (try query.typedFramesAt(r.a, f.body(), &.{}, node.id())).?;
        for (frames, 0..) |frame, depth| for (frame.bindings) |binding| if (std.mem.eql(u16, binding.pattern.id(), node.reference("patternBindingId"))) {
            if (!first) try out.writeByte(',');
            first = false;
            try out.writeAll("{\"nodeId\":");
            try model.writeString(out, node.id());
            try out.print(",\"scopeDepth\":{d},\"ownerNodeId\":", .{depth});
            try model.writeString(out, frame.owner.id());
            try out.print(",\"role\":\"{s}\"", .{frame.role});
            if (frame.variants.len > 0) {
                try out.writeAll(",\"variantIds\":[");
                for (frame.variants, 0..) |variant, i| {
                    if (i > 0) try out.writeByte(',');
                    try model.writeString(out, variant.literal());
                }
                try out.writeByte(']');
            }
            try out.print(",\"binding\":{{\"slot\":\"{s}\"", .{binding.slot});
            if (binding.field) |field| {
                try out.writeAll(",\"fieldId\":");
                try model.writeString(out, field);
            }
            try out.writeAll(",\"bindingId\":");
            try model.writeString(out, binding.pattern.id());
            try out.writeAll(",\"type\":");
            try wire.typedTypeRef(out, r.interner.?, binding.pattern.data.binding.type, f.data.function.type_parameters orelse &.{});
            if (binding.pattern.optionalDisplayName()) |name| {
                try out.writeAll(",\"displayName\":");
                try model.writeString(out, name);
            }
            try out.writeAll("}}");
        };
    }
    try out.writeByte(']');
}

pub fn diff(r: *query.Request, out: *Writer, old: Model, proposed: Model, change: Change, proposed_root: Model) anyerror!void {
    const a = r.a;
    const base_nodes = model.typedBodyNodes(a, old);
    const new_nodes = model.typedBodyNodes(a, proposed);
    const previous = for (base_nodes) |n| {
        if (m.same(query.idOf(n), change.target)) break n;
    } else unreachable;
    const next = for (new_nodes) |n| {
        if (m.same(query.idOf(n), change.target)) break n;
    } else unreachable;
    var candidate = r.*;
    candidate.generation = proposed_root.data.snapshot;
    try out.writeAll("{\"ownerFunctionId\":");
    try model.writeString(out, old.id());
    try out.writeAll(",\"targetNodeId\":");
    try @import("root").writeString(out, change.target);
    try out.writeAll(",\"previousTarget\":");
    try summary(r, out, old, previous);
    try out.writeAll(",\"proposedTarget\":");
    try summary(&candidate, out, proposed, next);
    var removed: std.ArrayList(Model) = .empty;
    var retained: std.ArrayList(Model) = .empty;
    var added: std.ArrayList(Model) = .empty;
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
    var added_bindings: std.ArrayList(Model) = .empty;
    for (try bindings(a, proposed)) |b| if (!has(base_bindings, query.idOf(b))) try added_bindings.append(a, b);
    try out.writeAll(",\"addedPatternBindingIds\":[");
    for (try sorted(a, added_bindings.items), 0..) |b, i| {
        if (i > 0) try out.writeByte(',');
        try model.writeString(out, b.id());
    }
    try out.writeAll("],\"patternReferences\":");
    try patternReferences(r, out, proposed, base_nodes, change.target);
    try out.writeAll(",\"retainedSharedDependencies\":");
    try summaries(r, out, old, try sorted(a, retained.items));
    try out.writeAll(",\"affectedUses\":[");
    var first = true;
    for (base_nodes) |node| for (viewer.placesOf(model.utf8(node.kind()))) |place| {
        const payload = node.payload(place.property) orelse continue;
        const Use = struct { value: tm.Id, target: ?tm.Id = null, index: ?usize = null };
        var items: std.ArrayList(Use) = .empty;
        switch (payload) {
            .id => |id| try items.append(a, .{ .value = id }),
            .optid => |optional| if (optional) |id| {
                try items.append(a, .{ .value = id });
            },
            .ids => |ids| for (ids, 0..) |id, i| {
                try items.append(a, .{ .value = id, .index = i });
            },
            .ctx, .args, .capargs, .fields => |targets| for (0..targets.len()) |i| {
                const target = targets.at(i);
                try items.append(a, .{ .value = target.value, .target = target.target });
            },
            else => unreachable,
        }
        for (items.items) |item| {
            if (!m.same(model.utf8(r.interner.?.idText(.node, item.value)), change.target)) continue;
            if (!first) try out.writeByte(',');
            first = false;
            try out.writeAll("{\"kind\":\"nodeDependency\",\"consumerNodeId\":");
            try model.writeString(out, node.id());
            try out.print(",\"role\":\"{s}\"", .{place.role});
            if (item.target) |target| {
                const field = m.same(place.role, "FieldValue");
                const domain: tm.Domain = if (field) .field else if (m.same(place.role, "CapabilityArgument")) .capability_parameter else .parameter;
                try out.print(",\"{s}\":", .{if (field) @as([]const u8, "fieldId") else "parameterId"});
                try model.writeString(out, r.interner.?.idText(domain, target));
            } else if (item.index) |i| try out.print(",\"index\":{d}", .{i});
            try out.writeByte('}');
        }
    };
    var uses: std.ArrayList(ResultUse) = .empty;
    try results(a, (old).body(), null, &uses);
    for (uses.items) |use| if (m.same(use.id, change.target)) {
        if (!first) try out.writeByte(',');
        first = false;
        try out.writeAll("{\"kind\":\"graphResult\",\"ownerNodeId\":");
        try writeOptional(out, if (use.owner) |node| node.id() else null);
        try out.writeByte('}');
    };
    try out.writeAll("]}");
}
