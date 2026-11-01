//! Materialize recipe-bearing operation plans against the original base, before transaction IDs.
const std = @import("std");
const model = @import("model_reader");
const query = @import("query.zig");
const recipe = @import("recipe.zig");
const preflight = @import("preflight.zig");
const infer = @import("infer.zig");
const m = @import("materialize.zig");
const edit = @import("typed_edit.zig");
const types = @import("operation_types.zig");
const Value = model.Model;
const tm = model.session_model;

pub const Construction = struct { construct: []const u8, value: tm.Id, nodes: []const tm.builder.NodeDraft };
pub const Arm = struct { match: []const u8, bindings: tm.FieldBindings, body: tm.GraphView };
pub const Plans = struct { constructions: []const Construction = &.{}, arms: []const Arm = &.{} };

fn rejected(r: *query.Request, code: []const u8, message: []const u8) anyerror {
    const result = preflight.rejected(r, code, message);
    r.rejection.message = "The requested operations were rejected.";
    return result;
}
fn operationFailure(r: *query.Request, err: anyerror) anyerror {
    if (err == error.Rejected and m.same(r.rejection.code, "SimulationRejected")) r.rejection.message = "The requested operations were rejected.";
    return err;
}
const Located = struct { function: Value, node: Value, frames: []const query.TypedFrame };
fn locate(r: *query.Request, id: []const u8) !?Located {
    for (r.typedModel().functions()) |function| {
        for (model.typedBodyNodes(r.a, function)) |node| if (edit.hasId(node, id)) {
            return .{ .function = function, .node = node, .frames = (try query.typedFramesAt(r.a, function.body(), &.{}, node.id())).? };
        };
    }
    return null;
}
fn describe(r: *query.Request, function: Value) ![]const u8 {
    return std.fmt.allocPrint(r.a, "'{s}' ({s})", .{ query.nameOf(function), query.idOf(function) });
}
fn creation(r: *query.Request, function: Value, tree: *const recipe.Node) !preflight.Creation {
    const parameters = function.parameters();
    const owner_parameters = try r.a.alloc(preflight.Parameter, parameters.len);
    for (parameters, owner_parameters) |parameter, *p| p.* = .{ .name = query.nameOf(parameter), .type = try infer.typeOf(r.a, parameter.declaredType()) };
    const effects = function.requiredEffects();
    const owner_effects = try r.a.alloc([]const u8, effects.len);
    for (effects, owner_effects) |effect, *e| e.* = model.utf8(effect.literal());
    return .{ .expected_snapshot_id = r.snapshot_id, .display_name = query.nameOf(function), .type_parameters = null, .parameters = owner_parameters, .result_type = try infer.typeOf(r.a, function.resultType()), .required_effects = owner_effects, .body = tree };
}
const Frames = struct { types: ?*const infer.Frame = null, values: ?*const m.Frame = null };
fn lexicalFrames(r: *query.Request, frames: []const query.TypedFrame) !Frames {
    var result: Frames = .{};
    var index = frames.len;
    while (index > 0) {
        index -= 1;
        const frame = frames[index];
        const slots = try r.a.alloc(infer.Binding, frame.bindings.len);
        const values = try r.a.alloc(m.Binding, frame.bindings.len);
        for (frame.bindings, slots, values) |binding, *t, *v| {
            t.* = .{ .key = if (binding.field) |field| .{ .field = model.utf8(field) } else if (m.same(binding.slot, "error")) .err else .value, .type = try infer.typeOf(r.a, binding.pattern.declaredType()) };
            v.* = .{ .slot = binding.slot, .field = if (binding.field) |field| model.utf8(field) else null, .value = binding.pattern.data.binding };
        }
        const variants = try r.a.alloc([]const u8, frame.variants.len);
        for (frame.variants, variants) |variant, *v| v.* = model.utf8(variant.literal());
        const tf = try r.a.create(infer.Frame);
        tf.* = .{ .kind = if (m.same(frame.role, "unionArm")) .{ .union_arm = variants } else if (m.same(frame.role, "bind")) .bind_body else if (m.same(frame.role, "optionSome")) .option_some else if (m.same(frame.role, "optionNone")) .option_none else if (m.same(frame.role, "resultOk")) .result_ok else .result_err, .slots = slots, .next = result.types };
        result.types = tf;
        const vf = try r.a.create(m.Frame);
        vf.* = .{ .bindings = values, .next = result.values };
        result.values = vf;
    }
    return result;
}

pub fn prepare(r: *query.Request, intent: types.Intent) anyerror!Plans {
    return switch (intent) {
        .add_product_field => |field| product(r, field.field.type, field.constructions),
        .add_union_variant => |variant| unionVariant(r, variant.type, variant.variant, variant.arms),
        else => .{},
    };
}
fn product(r: *query.Request, field_type: recipe.TypeRef, plans: []const types.ValuePlan) !Plans {
    const prepared = try r.a.alloc(Construction, plans.len);
    for (plans, prepared) |plan, *result| {
        if (plan.value == .existing) {
            result.* = .{ .construct = plan.construct, .value = try r.interner.?.id(.node, try std.unicode.utf8ToUtf16LeAlloc(r.a, plan.value.existing)), .nodes = &.{} };
            continue;
        }
        const site = (try locate(r, plan.construct)) orelse return rejected(r, "ProductFieldPlanTargetNotFound", try std.fmt.allocPrint(r.a, "Construct node '{s}' does not exist in any function.", .{plan.construct}));
        const tree = plan.value.recipe;
        preflight.validateRecipeBounds(r, tree) catch |err| return operationFailure(r, err);
        const frames = try lexicalFrames(r, site.frames);
        var inferred = infer.runScoped(r, try creation(r, site.function, tree), site.function, frames.types, "value") catch |err| return operationFailure(r, err);
        const completion = inferred.completions.get(tree).?.value;
        if (completion != .normal or !infer.eqlType(completion.normal, field_type)) {
            var renderer = inferred;
            renderer.existing = null;
            return rejected(r, "ProductFieldValueTypeMismatch", try std.fmt.allocPrint(r.a, "The value for construct '{s}' in function {s} produces {s}, but the new field has type {s}.", .{ plan.construct, try describe(r, site.function), try renderer.completion(completion), try renderer.ty(field_type) }));
        }
        var materializer: m.Materializer = .{ .a = r.a, .r = r, .types = &inferred, .fresh = &.{}, .function_id = site.function.data.function.id, .parameters = site.function.data.function.parameters };
        const graph = try materializer.graph(frames.values, tree);
        result.* = .{ .construct = plan.construct, .value = graph.graph(0).result(), .nodes = try graphDrafts(r.a, graph.graph(0)) };
    }
    return .{ .constructions = prepared };
}
fn unionVariant(r: *query.Request, owner_id: []const u8, variant: Value, plans: []const types.ArmRecipe) !Plans {
    const owner = edit.find(r.typedModel(), .types, owner_id) orelse return rejected(r, "TargetNotFound", "The target type was not found.");
    if (owner.data.type_declaration.product) return rejected(r, "TargetNotUnion", "The target type of addUnionVariant must be a Union.");
    // Duplicate plans reject before any arm is prepared or any field bindings are allocated.
    for (plans, 0..) |plan, i| {
        for (plans[i + 1 ..]) |later| if (m.same(later.match, plan.match)) return rejected(r, "UnionMatchArmPlanMismatch", try std.fmt.allocPrint(r.a, "Match node '{s}' has more than one arm plan.", .{plan.match}));
    }
    var extended = r.*;
    extended.generation = (try edit.replace(r.a, r.typedModel(), .types, try edit.append(r.a, owner, .variants, variant))).data.snapshot;
    const prepared = try r.a.alloc(Arm, plans.len);
    for (plans, prepared) |plan, *result| {
        const site = (try locate(r, plan.match)) orelse return rejected(r, "UnionMatchArmPlanTargetNotFound", try std.fmt.allocPrint(r.a, "Match node '{s}' does not exist in any function.", .{plan.match}));
        const site_name = try std.fmt.allocPrint(r.a, "match '{s}' in function {s}", .{ plan.match, try describe(r, site.function) });
        if (!m.same(site.node.data.node.kind(), "matchUnion")) return rejected(r, "UnionMatchArmPlanTargetMismatch", try std.fmt.allocPrint(r.a, "Node '{s}' in function {s} is not a matchUnion.", .{ plan.match, try describe(r, site.function) }));
        var matching = false;
        for (site.node.arms()) |arm| for (arm.variantIds()) |existing_id| {
            for (owner.variants()) |existing| if (m.same(query.idOf(existing), model.utf8(existing_id.literal()))) {
                matching = true;
                break;
            };
        };
        if (!matching) return rejected(r, "UnionMatchArmPlanTargetMismatch", try std.fmt.allocPrint(r.a, "The {s} does not match over Union '{s}'.", .{ site_name, query.nameOf(owner) }));
        const tree = plan.body;
        const c = try creation(&extended, site.function, tree);
        var instantiator: infer.Infer = .{ .r = &extended, .a = r.a, .c = c, .existing = site.function, .local = &.{}, .fresh = &.{} };
        const input_completion = model.typedCompletionOf(r.a, r.typedModel(), site.function, site.node.reference("inputNodeId"));
        var arguments: []const recipe.TypeRef = &.{};
        if (input_completion) |completion| if (completion == .normal) {
            const input_type = try infer.typeOf(r.a, completion.normal);
            if (input_type == .named) arguments = input_type.named.arguments;
        };
        const fields = variant.fields();
        const bindings = try r.a.alloc(tm.FieldBinding, fields.len);
        const value_slots = try r.a.alloc(m.Binding, fields.len);
        const type_slots = try r.a.alloc(infer.Binding, fields.len);
        var frames = try lexicalFrames(r, site.frames);
        var materializer: m.Materializer = .{ .a = r.a, .r = &extended, .types = &instantiator, .fresh = &.{}, .function_id = site.function.data.function.id, .parameters = site.function.data.function.parameters };
        for (fields, bindings, value_slots, type_slots) |field, *binding, *v, *t| {
            // Allocate every arm field binding before bounds/completion/inference checks.
            const binding_id = try materializer.freshId(.binding);
            const field_type = try instantiator.instantiate(owner, arguments, try infer.typeOf(r.a, field.declaredType()));
            const pattern = try r.a.create(tm.Binding);
            pattern.* = .{ .id = binding_id, .name = field.data.slot.value.name, .type = try materializer.typeRef(field_type) };
            binding.* = .{ .source = field.data.slot.value.id, .binding = pattern };
            v.* = .{ .slot = "field", .field = query.idOf(field), .value = pattern };
            t.* = .{ .key = .{ .field = query.idOf(field) }, .type = field_type };
        }
        const tf = try r.a.create(infer.Frame);
        tf.* = .{ .kind = .{ .union_arm = try r.a.dupe([]const u8, &.{query.idOf(variant)}) }, .slots = type_slots, .next = frames.types };
        frames.types = tf;
        const vf = try r.a.create(m.Frame);
        vf.* = .{ .bindings = value_slots, .next = frames.values };
        frames.values = vf;
        preflight.validateRecipeBounds(r, tree) catch |err| return operationFailure(r, err);
        const expected = model.typedCompletionOf(r.a, r.typedModel(), site.function, site.node.id()) orelse return rejected(r, "UnionMatchArmTypeUnavailable", try std.fmt.allocPrint(r.a, "The type of {s} could not be derived from the validated base snapshot.", .{site_name}));
        var inferred = infer.runScoped(&extended, c, site.function, frames.types, "body") catch |err| {
            r.rejection = extended.rejection;
            r.diagnostic = extended.diagnostic;
            return operationFailure(r, err);
        };
        const completion = inferred.completions.get(tree).?.value;
        const expected_completion: infer.Completion = if (expected == .normal) .{ .normal = try infer.typeOf(r.a, expected.normal) } else .aborts;
        if (completion == .normal and expected_completion == .normal and !infer.eqlType(completion.normal, expected_completion.normal)) {
            var renderer = inferred;
            renderer.existing = null;
            return rejected(r, "UnionMatchArmTypeMismatch", try std.fmt.allocPrint(r.a, "The arm body for {s} produces {s}, but the match produces {s}.", .{ site_name, try renderer.ty(completion.normal), try renderer.completion(expected_completion) }));
        }
        materializer.types = &inferred;
        result.* = .{ .match = plan.match, .bindings = try tm.builder.fieldBindings(r.a, bindings), .body = try tm.builder.view(r.a, try materializer.graph(frames.values, tree)) };
    }
    return .{ .arms = prepared };
}

pub fn graphDrafts(a: std.mem.Allocator, graph: tm.GraphView) ![]const tm.builder.NodeDraft {
    var nodes: std.ArrayList(tm.builder.NodeDraft) = .empty;
    var iterator = graph.nodes();
    while (iterator.next()) |node| try nodes.append(a, try tm.builder.fromNode(a, node));
    return nodes.items;
}
