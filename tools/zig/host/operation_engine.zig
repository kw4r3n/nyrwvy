//! Transactions over immutable request-local model paths; publication owns the typed generation.
const std = @import("std");
const model = @import("model_reader");
const query = @import("query.zig");
const recipe = @import("recipe.zig");
const preflight = @import("preflight.zig");
const infer = @import("infer.zig");
const m = @import("materialize.zig");
const edit = @import("operation_model.zig");
const types = @import("operation_types.zig");
const preparation = @import("operation_prepare.zig");
const Value = model.Value;

pub const Result = struct { transaction: Value, snapshot: Value, candidate: Value, diff: Value, summary_operations: Value };
pub const DiffKey = enum { renames, parametersAdded, productFieldsAdded, productFieldsBound, productFieldsRemoved, effectsRemoved, capabilitiesRemoved, capabilityOperationsRemoved, typesRemoved, functionsRemoved, argumentsAttached, obligationsCreated, obligationsResolved, requiredEffectsChanged, visibilityChanges, attributeChanges, parametersRemoved, unionVariantsRemoved, capabilityOperationsAdded, capabilityImplementationsBound, unionVariantsAdded, unionMatchArmsAdded };
const Prepared = struct { intent: types.Intent, plans: preparation.Plans, member: Value = .json_null, missing: []const Value = &.{} };

pub fn rejected(r: *query.Request, code: []const u8, message: []const u8) anyerror {
    const result = preflight.rejected(r, code, message);
    r.rejection.message = "The requested operations were rejected.";
    return result;
}
const Subject = struct { item: Value, owner: ?Value = null, variant: ?Value = null, operation: ?Value = null };
fn group(target: types.Target) []const u8 {
    return switch (target) {
        .function, .parameter => "functions",
        .type, .field, .variant => "types",
        .effect => "effects",
        .capability, .capabilityOperation, .capabilityParameter => "capabilities",
    };
}
fn subject(root: Value, target: types.Target, id: []const u8) ?Subject {
    for (model.arrayItems(root, group(target))) |owner| {
        switch (target) {
            .function, .type, .effect, .capability => if (edit.hasId(owner, id)) return .{ .item = owner },
            .parameter => for (model.arrayItems(owner, "parameters")) |item| {
                if (edit.hasId(item, id)) return .{ .item = item, .owner = owner };
            },
            .variant => for (model.arrayItems(owner, "variants")) |item| {
                if (edit.hasId(item, id)) return .{ .item = item, .owner = owner };
            },
            .field => {
                for (model.arrayItems(owner, "fields")) |item| if (edit.hasId(item, id)) return .{ .item = item, .owner = owner };
                for (model.arrayItems(owner, "variants")) |variant| for (model.arrayItems(variant, "fields")) |item| {
                    if (edit.hasId(item, id)) return .{ .item = item, .owner = owner, .variant = variant };
                };
            },
            .capabilityOperation, .capabilityParameter => for (model.arrayItems(owner, "operations")) |operation| {
                if (target == .capabilityOperation and edit.hasId(operation, id)) return .{ .item = operation, .owner = owner };
                if (target == .capabilityParameter) for (model.arrayItems(operation, "parameters")) |item| {
                    if (edit.hasId(item, id)) return .{ .item = item, .owner = owner, .operation = operation };
                };
            },
        }
    }
    return null;
}
const State = struct {
    r: *query.Request,
    root: Value,
    transaction: Value,
    scheduled: []const Prepared,
    changes: [std.enums.values(DiffKey).len]std.ArrayList(Value) = @splat(.empty),

    fn add(self: *State, key: DiffKey, value: Value) !void {
        try self.changes[@backingInt(key)].append(self.r.a, value);
    }
    fn changed(self: *State, key: DiffKey, field: []const u8, id: []const u8) bool {
        for (self.changes[@backingInt(key)].items) |item| if (m.same(edit.text(item, field), id)) return true;
        return false;
    }
    fn fail(self: *State, code: []const u8, message: []const u8) anyerror {
        return rejected(self.r, code, message);
    }
    fn replaceSubject(self: *State, target: types.Target, found: Subject, item: Value) !void {
        const a = self.r.a;
        var result = item;
        if (found.operation) |operation| result = try edit.replace(a, operation, "parameters", result);
        if (found.variant) |variant| result = try edit.replace(a, variant, "fields", result);
        if (found.owner) |owner| result = try edit.replace(a, owner, if (found.operation != null or target == .capabilityOperation) "operations" else if (found.variant != null or target == .variant) "variants" else if (target == .field) "fields" else "parameters", result);
        self.root = try edit.replace(a, self.root, group(target), result);
    }
    fn rename(self: *State, change: types.Rename) !void {
        if (try recipe.isBlank(self.r.a, change.name)) return self.fail("InvalidDisplayName", "A display name cannot be blank.");
        const found = subject(self.root, change.target, change.id) orelse return self.fail("TargetNotFound", try std.fmt.allocPrint(self.r.a, "{s} '{s}' was not found.", .{ switch (change.target) {
            .function => @as([]const u8, "Function"),
            .type => "Type",
            .field => "Field",
            .variant => "Union variant",
            .parameter => "Parameter",
            .effect => "Effect",
            .capability => "Capability",
            .capabilityOperation => "Capability operation",
            .capabilityParameter => "Capability parameter",
        }, change.id }));
        try self.replaceSubject(change.target, found, try edit.set(self.r.a, found.item, "displayName", try m.string(self.r.a, change.name)));
    }
    fn visibility(self: *State, change: types.Visibility) !void {
        const found = subject(self.root, change.target, change.id) orelse return self.fail("TargetNotFound", try std.fmt.allocPrint(self.r.a, "The declaration '{s}' to change the visibility of was not found.", .{change.id}));
        if ((model.property(found.item, "exported") != null) == change.value) return self.fail("VisibilityUnchanged", try std.fmt.allocPrint(self.r.a, "The declaration '{s}' is already {s}.", .{ change.id, if (change.value) @as([]const u8, "exported") else "private" }));
        try self.replaceSubject(change.target, found, try edit.flag(self.r.a, found.item, "exported", change.value));
    }
    fn attribute(self: *State, change: types.Flag) !void {
        const target: types.Target = switch (change.attribute) {
            .mayWait => .capabilityOperation,
            .taskBound => .capability,
            .mustNotWait => .function,
        };
        const found = subject(self.root, target, change.id) orelse return self.fail("TargetNotFound", try std.fmt.allocPrint(self.r.a, "The declaration '{s}' to change was not found.", .{change.id}));
        const key = @tagName(change.attribute);
        if ((model.property(found.item, key) != null) == change.value) return self.fail("AttributeUnchanged", try std.fmt.allocPrint(self.r.a, "The declaration '{s}' already has {s} {s}.", .{ change.id, key, if (change.value) @as([]const u8, "true") else "false" }));
        try self.replaceSubject(target, found, try edit.flag(self.r.a, found.item, key, change.value));
    }
    fn requiredEffect(self: *State, change: types.RequiredEffect, adding: bool) !void {
        const function = edit.find(self.root, "functions", change.function) orelse return self.fail("TargetNotFound", "The target function was not found.");
        if (edit.find(self.root, "effects", change.effect) == null) return self.fail("EffectNotFound", try std.fmt.allocPrint(self.r.a, "Effect '{s}' was not found.", .{change.effect}));
        var present = false;
        var effects: std.ArrayList(Value) = .empty;
        for (model.arrayItems(function, "requiredEffects")) |effect| {
            if (m.same(model.utf8(model.decoded(effect)), change.effect)) present = true else try effects.append(self.r.a, effect);
        }
        if (adding and present) return self.fail("FunctionRequiredEffectAlreadyPresent", "The function already declares the required effect.");
        if (!adding and !present) return self.fail("FunctionRequiredEffectNotPresent", "The function does not declare the required effect.");
        if (adding) try effects.append(self.r.a, try m.string(self.r.a, change.effect));
        self.root = try edit.replace(self.r.a, self.root, "functions", try edit.set(self.r.a, function, "requiredEffects", .{ .array = effects.items }));
        try self.add(.requiredEffectsChanged, try m.object(self.r.a, &.{ try m.member(self.r.a, "kind", try m.string(self.r.a, if (adding) "added" else "removed")), try m.member(self.r.a, "functionId", try m.string(self.r.a, change.function)), try m.member(self.r.a, "effectId", try m.string(self.r.a, change.effect)) }));
    }
};

const OwnedNode = struct { function: Value, node: Value };
fn nodes(s: *State) ![]const OwnedNode {
    var result: std.ArrayList(OwnedNode) = .empty;
    for (model.arrayItems(s.root, "functions")) |function| for (model.bodyNodes(s.r.a, function)) |node| try result.append(s.r.a, .{ .function = function, .node = node });
    return result.items;
}
fn updateNode(s: *State, id: []const u8, replacement: Value) !void {
    const Context = struct {
        id: []const u8,
        replacement: Value,
        fn change(c: @This(), node: Value) !Value {
            return if (edit.hasId(node, c.id)) c.replacement else node;
        }
    };
    const functions = model.property(s.root, "functions").?;
    const result = try edit.mapNodes(s.r.a, functions, Context{ .id = id, .replacement = replacement }, Context.change);
    if (!edit.identical(functions, result)) s.root = try edit.set(s.r.a, s.root, "functions", result);
}
fn hasKind(node: Value, kind: []const u8) bool {
    return m.same(edit.text(node, "kind"), kind);
}
fn idLess(x: Value, y: Value) bool {
    return std.mem.order(u16, model.decoded(model.property(x, "id").?), model.decoded(model.property(y, "id").?)) == .lt;
}
fn functionsById(s: *State) ![]const Value {
    const result = try s.r.a.dupe(Value, model.arrayItems(s.root, "functions"));
    std.mem.sort(Value, result, {}, struct {
        fn less(_: void, x: Value, y: Value) bool {
            return idLess(x, y);
        }
    }.less);
    return result;
}
fn sortIdValues(items: []Value) void {
    std.mem.sort(Value, items, {}, struct {
        fn less(_: void, x: Value, y: Value) bool {
            return std.mem.order(u16, model.decoded(x), model.decoded(y)) == .lt;
        }
    }.less);
}
fn strings(a: std.mem.Allocator, items: []const Value) !Value {
    const result = try a.alloc(Value, items.len);
    for (items, result) |item, *id| id.* = model.property(item, "id").?;
    sortIdValues(result);
    return .{ .array = result };
}
fn idsEqual(a: std.mem.Allocator, affected: []const OwnedNode, planned: []const []const u8) !bool {
    _ = a;
    if (affected.len != planned.len) return false;
    for (planned, 0..) |id, i| {
        for (planned[0..i]) |earlier| if (m.same(earlier, id)) return false;
        var found = false;
        for (affected) |site| if (edit.hasId(site.node, id)) {
            found = true;
            break;
        };
        if (!found) return false;
    }
    return true;
}
fn ownedDescription(s: *State, function: Value) ![]const u8 {
    return std.fmt.allocPrint(s.r.a, "'{s}' ({s})", .{ query.nameOf(function), query.idOf(function) });
}
fn typeValue(s: *State, t: recipe.TypeRef) !Value {
    var materializer: m.Materializer = .{ .a = s.r.a, .r = s.r, .types = undefined, .fresh = &.{}, .function_id = .json_null, .parameters = &.{} };
    return materializer.typeRef(t);
}
fn typedMember(s: *State, field: types.Field, id: Value) !Value {
    return m.object(s.r.a, &.{ try m.member(s.r.a, "id", id), try m.member(s.r.a, "displayName", try m.string(s.r.a, field.name)), try m.member(s.r.a, "type", try typeValue(s, field.type)) });
}
fn materialize(r: *query.Request, intent: types.Intent, plans: preparation.Plans) !Prepared {
    var result: Prepared = .{ .intent = intent, .plans = plans };
    var scratch: State = .{ .r = r, .root = r.model, .transaction = .json_null, .scheduled = &.{} };
    switch (intent) {
        .add_function_parameter => |parameter| {
            result.member = try typedMember(&scratch, parameter.field, try m.id(r.a));
            const obligations = try r.a.alloc(Value, parameter.calls.len);
            for (parameter.calls, obligations) |plan, *obligation| obligation.* = if (plan == .leave_missing) try m.id(r.a) else .json_null;
            result.missing = obligations;
        },
        .add_product_field => |field| result.member = try typedMember(&scratch, field.field, try m.id(r.a)),
        .add_capability_operation => |operation| {
            const operation_id = try m.id(r.a);
            const parameters = try r.a.alloc(Value, operation.parameters.len);
            for (operation.parameters, parameters) |parameter, *p| p.* = try typedMember(&scratch, parameter, try m.id(r.a));
            result.member = try m.object(r.a, &.{ try m.member(r.a, "id", operation_id), try m.member(r.a, "displayName", try m.string(r.a, operation.name)), try m.member(r.a, "parameters", .{ .array = parameters }), try m.member(r.a, "resultType", try typeValue(&scratch, operation.result)), try m.member(r.a, "effectId", try m.string(r.a, operation.effect)) });
            if (operation.may_wait) result.member = try edit.flag(r.a, result.member, "mayWait", true);
        },
        else => {},
    }
    return result;
}
fn attachArgument(s: *State, node: Value, parameter: Value, value: Value) !void {
    const a = s.r.a;
    const argument = try m.object(a, &.{ try m.member(a, "targetParameterId", parameter), try m.member(a, "valueNodeId", value) });
    try updateNode(s, query.idOf(node), try edit.append(a, node, "arguments", argument));
    try s.add(.argumentsAttached, try m.object(a, &.{ try m.member(a, "callNodeId", model.property(node, "id").?), try m.member(a, "parameterId", parameter), try m.member(a, "valueNodeId", value) }));
}
fn resolveMissing(s: *State, request: types.Intent) !void {
    const intent = request.resolve_missing_argument;
    const obligation = edit.find(s.root, "obligations", intent.obligation) orelse return s.fail("ObligationNotOpen", "The missing-argument obligation does not exist.");
    if (!m.same(edit.text(obligation, "status"), "open")) return s.fail("ObligationNotOpen", "The missing-argument obligation is already resolved.");
    const call_id = edit.text(obligation, "callNodeId");
    const site = for (try nodes(s)) |site| {
        if (edit.hasId(site.node, call_id) and hasKind(site.node, "directCall")) break site;
    } else return s.fail("ObligationTargetMismatch", "The obligated direct-call node no longer exists.");
    const value = try m.string(s.r.a, intent.value);
    try attachArgument(s, site.node, model.property(obligation, "parameterId").?, value);
    var resolved = try edit.set(s.r.a, obligation, "status", try m.string(s.r.a, "resolved"));
    resolved = try edit.set(s.r.a, resolved, "resolutionTransactionId", s.transaction);
    resolved = try edit.set(s.r.a, resolved, "resolutionValueNodeId", value);
    s.root = try edit.replace(s.r.a, s.root, "obligations", resolved);
    try s.add(.obligationsResolved, try m.object(s.r.a, &.{ try m.member(s.r.a, "obligationId", model.property(obligation, "id").?), try m.member(s.r.a, "callNodeId", model.property(obligation, "callNodeId").?), try m.member(s.r.a, "parameterId", model.property(obligation, "parameterId").?), try m.member(s.r.a, "valueNodeId", value) }));
}

fn insertNodes(a: std.mem.Allocator, value: Value, plans: []const preparation.Construction) anyerror!Value {
    switch (value) {
        .object => |fields| {
            var result = value;
            for (fields) |field| {
                var next = try insertNodes(a, field.value, plans);
                if (m.same(model.utf8(field.name), "nodes")) {
                    var items: std.ArrayList(Value) = .empty;
                    var added = false;
                    for (next.array) |node| {
                        for (plans) |plan| if (edit.hasId(node, plan.construct) and plan.nodes.len > 0) {
                            try items.appendSlice(a, plan.nodes);
                            added = true;
                        };
                        try items.append(a, node);
                    }
                    if (added) next = .{ .array = items.items };
                }
                if (!edit.identical(field.value, next)) result = try edit.set(a, result, model.utf8(field.name), next);
            }
            return result;
        },
        .array => |items| {
            var changed: ?[]Value = null;
            for (items, 0..) |item, i| {
                const next = try insertNodes(a, item, plans);
                if (!edit.identical(item, next)) {
                    if (changed == null) changed = try a.dupe(Value, items);
                    changed.?[i] = next;
                }
            }
            return if (changed) |result| .{ .array = result } else value;
        },
        else => return value,
    }
}
fn addProduct(s: *State, p: Prepared) !void {
    const request = p.intent.add_product_field;
    const a = s.r.a;
    const owner = edit.find(s.root, "types", request.type) orelse return s.fail("TargetNotFound", "The target product type was not found.");
    if (model.property(owner, "fields") == null) return s.fail("ProductFieldTargetNotProduct", "The target type must be a Product.");
    var affected: std.ArrayList(OwnedNode) = .empty;
    for (try nodes(s)) |site| if (hasKind(site.node, "productConstruct") and m.same(edit.text(site.node, "typeId"), request.type)) try affected.append(a, site);
    const planned = try a.alloc([]const u8, p.plans.constructions.len);
    for (p.plans.constructions, planned) |plan, *id| id.* = plan.construct;
    if (try recipe.isBlank(a, request.field.name)) return s.fail("InvalidDisplayName", "The new field display name cannot be blank.");
    for (model.arrayItems(owner, "fields")) |field| if (m.same(query.nameOf(field), request.field.name)) return s.fail("DuplicateFieldDisplayName", "The product already has a field with this display name.");
    if (subject(s.root, .field, query.idOf(p.member)) != null) return s.fail("DuplicateFieldId", "The new field ID already exists.");
    if (!try idsEqual(a, affected.items, planned)) return s.fail("ProductFieldPlanMismatch", "Every affected ProductConstruct requires exactly one field-value plan.");
    const inserted = try insertNodes(a, s.root, p.plans.constructions);
    for (affected.items) |site| {
        const plan = for (p.plans.constructions) |plan| {
            if (edit.hasId(site.node, plan.construct)) break plan;
        } else unreachable;
        const function = edit.find(inserted, "functions", query.idOf(site.function)).?;
        const completion = model.completionOf(a, inserted, function, model.decoded(plan.value));
        if (completion == null or completion.? != .normal or !infer.eqlType(try infer.typeOf(a, completion.?.normal), request.field.type)) return s.fail("ProductFieldValueTypeMismatch", "The planned field value must exist in its owning function and have the new field type.");
    }
    s.root = inserted;
    s.root = try edit.replace(a, s.root, "types", try edit.append(a, owner, "fields", p.member));
    for (affected.items) |site| {
        const plan = for (p.plans.constructions) |plan| {
            if (edit.hasId(site.node, plan.construct)) break plan;
        } else unreachable;
        // Fetch the node after insertion: nested plan nodes may already have changed its graph.
        const node = for (try nodes(s)) |current| {
            if (edit.hasId(current.node, plan.construct)) break current.node;
        } else unreachable;
        const binding = try m.object(a, &.{ try m.member(a, "targetFieldId", model.property(p.member, "id").?), try m.member(a, "valueNodeId", plan.value) });
        try updateNode(s, plan.construct, try edit.append(a, node, "fields", binding));
        try s.add(.productFieldsBound, try m.object(a, &.{ try m.member(a, "ownerFunctionId", model.property(site.function, "id").?), try m.member(a, "constructNodeId", model.property(node, "id").?), try m.member(a, "fieldId", model.property(p.member, "id").?), try m.member(a, "valueNodeId", plan.value) }));
    }
    try s.add(.productFieldsAdded, try m.object(a, &.{ try m.member(a, "ownerTypeId", model.property(owner, "id").?), try m.member(a, "fieldId", model.property(p.member, "id").?), try m.member(a, "displayName", model.property(p.member, "displayName").?), try m.member(a, "type", model.property(p.member, "type").?) }));
}
fn addUnion(s: *State, p: Prepared) !void {
    const request = p.intent.add_union_variant;
    const a = s.r.a;
    const variant = request.variant;
    const owner = edit.find(s.root, "types", request.type) orelse return s.fail("TargetNotFound", "The target type was not found.");
    if (model.property(owner, "variants") == null) return s.fail("TargetNotUnion", "The target type of addUnionVariant must be a Union.");
    if (try recipe.isBlank(a, query.nameOf(variant))) return s.fail("InvalidDisplayName", "The new variant display name cannot be blank.");
    for (model.arrayItems(owner, "variants")) |existing| if (m.same(query.nameOf(existing), query.nameOf(variant))) return s.fail("DuplicateUnionVariantDisplayName", try std.fmt.allocPrint(a, "Union '{s}' already has a variant named '{s}'; variant display names must be unique within their Union.", .{ query.nameOf(owner), query.nameOf(variant) }));
    const fields = model.arrayItems(variant, "fields");
    const names = try a.alloc([]const u8, fields.len);
    for (fields, names) |field, *name| {
        name.* = query.nameOf(field);
        if (try recipe.isBlank(a, name.*)) return s.fail("InvalidDisplayName", "Variant field display names cannot be blank.");
    }
    if (preflight.repeated(names) != null) return s.fail("DuplicateFieldDisplayName", "Variant field display names must be distinct within their variant.");
    if (subject(s.root, .variant, query.idOf(variant)) != null) return s.fail("DuplicateUnionVariantId", "The new variant or field ID already exists.");
    for (fields, 0..) |field, i| {
        if (subject(s.root, .field, query.idOf(field)) != null) return s.fail("DuplicateUnionVariantId", "The new variant or field ID already exists.");
        for (fields[0..i]) |earlier| if (edit.hasId(earlier, query.idOf(field))) return s.fail("DuplicateUnionVariantId", "The new variant or field ID already exists.");
    }
    var affected: std.ArrayList(OwnedNode) = .empty;
    for (try nodes(s)) |site| {
        if (!hasKind(site.node, "matchUnion")) continue;
        var matching = false;
        for (model.arrayItems(site.node, "arms")) |arm| for (try edit.variantIds(a, arm)) |id| for (model.arrayItems(owner, "variants")) |existing| {
            if (m.same(query.idOf(existing), model.utf8(model.decoded(id)))) matching = true;
        };
        if (matching) try affected.append(a, site);
    }
    const planned = try a.alloc([]const u8, p.plans.arms.len);
    for (p.plans.arms, planned) |plan, *id| id.* = plan.match;
    if (!try idsEqual(a, affected.items, planned)) {
        var sites: std.ArrayList([]const u8) = .empty;
        for (affected.items) |site| try sites.append(a, try std.fmt.allocPrint(a, "'{s}' in function {s}", .{ query.idOf(site.node), try ownedDescription(s, site.function) }));
        return s.fail("UnionMatchArmPlanMismatch", try std.fmt.allocPrint(a, "Every match (matchUnion) over Union '{s}' requires exactly one arm plan for the new variant; the matches are: {s}.", .{ query.nameOf(owner), if (sites.items.len == 0) @as([]const u8, "there are none") else try std.mem.join(a, ", ", sites.items) }));
    }
    s.root = try edit.replace(a, s.root, "types", try edit.append(a, owner, "variants", variant));
    for (affected.items) |site| {
        const plan = for (p.plans.arms) |plan| {
            if (edit.hasId(site.node, plan.match)) break plan;
        } else unreachable;
        const node = for (try nodes(s)) |current| {
            if (edit.hasId(current.node, plan.match)) break current.node;
        } else unreachable;
        const arm = try m.object(a, &.{ try m.member(a, "variantId", model.property(variant, "id").?), try m.member(a, "fieldBindings", .{ .array = plan.bindings }), try m.member(a, "body", plan.body) });
        try updateNode(s, plan.match, try edit.append(a, node, "arms", arm));
        const ids = try a.alloc(Value, plan.bindings.len);
        for (plan.bindings, ids) |binding, *id| id.* = model.property(model.property(binding, "binding").?, "id").?;
        try s.add(.unionMatchArmsAdded, try m.object(a, &.{ try m.member(a, "ownerFunctionId", model.property(site.function, "id").?), try m.member(a, "matchNodeId", model.property(node, "id").?), try m.member(a, "variantId", model.property(variant, "id").?), try m.member(a, "patternBindingIds", .{ .array = ids }) }));
    }
    try s.add(.unionVariantsAdded, try m.object(a, &.{ try m.member(a, "ownerTypeId", model.property(owner, "id").?), try m.member(a, "variant", variant) }));
}

fn nonCallUses(s: *State, id: []const u8) ![]const []const u8 {
    const a = s.r.a;
    var users: std.ArrayList([]const u8) = .empty;
    for (try functionsById(s)) |function| {
        var labels: std.ArrayList([]const u8) = .empty;
        for (model.bodyNodes(a, function)) |node| for (model.functionRelationsOf(node)) |reference| {
            if (m.same(model.utf8(reference.id), id)) if (reference.non_call_label) |label| if (!preflight.contains(labels.items, label)) {
                try labels.append(a, label);
            };
        };
        std.mem.sort([]const u8, labels.items, {}, struct {
            fn less(_: void, x: []const u8, y: []const u8) bool {
                return std.mem.order(u8, x, y) == .lt;
            }
        }.less);
        if (labels.items.len > 0) try users.append(a, try std.fmt.allocPrint(a, "{s} ({s})", .{ try ownedDescription(s, function), try std.mem.join(a, ", ", labels.items) }));
    }
    return users.items;
}
fn reconcile(s: *State, function: Value) !Value {
    const a = s.r.a;
    var occurring: std.ArrayList([]const u8) = .empty;
    for (model.arrayItems(function, "parameters")) |parameter| try preflight.occurring(a, try infer.typeOf(a, model.property(parameter, "type").?), &occurring);
    const old = model.arrayItems(function, "typeParameters");
    var parameters: std.ArrayList(Value) = .empty;
    for (old) |parameter| if (preflight.contains(occurring.items, edit.text(parameter, "typeParameterId"))) try parameters.append(a, parameter);
    for (occurring.items) |id| {
        var present = false;
        for (parameters.items) |parameter| if (m.same(edit.text(parameter, "typeParameterId"), id)) {
            present = true;
            break;
        };
        if (!present) try parameters.append(a, try m.object(a, &.{ try m.member(a, "typeParameterId", try m.string(a, id)), try m.member(a, "displayName", try m.string(a, id)) }));
    }
    return if (parameters.items.len == 0) edit.remove(a, function, "typeParameters") else edit.set(a, function, "typeParameters", .{ .array = parameters.items });
}
fn addParameter(s: *State, p: Prepared) !void {
    const request = p.intent.add_function_parameter;
    const a = s.r.a;
    if (try recipe.isBlank(a, request.field.name)) return s.fail("InvalidDisplayName", "A parameter display name cannot be blank.");
    if (subject(s.root, .parameter, query.idOf(p.member)) != null) return s.fail("DuplicateParameterId", "The new parameter ID already exists.");
    const function = edit.find(s.root, "functions", request.function) orelse return s.fail("TargetNotFound", "The target function was not found.");
    const users = try nonCallUses(s, request.function);
    if (users.len > 0) return s.fail("ParameterAdditionNonCallUse", try std.fmt.allocPrint(a, "Function {s} is used other than as a direct call by {s}. Those uses fix its signature, so only direct calls can supply a new parameter; replace those uses before adding a parameter to this function.", .{ try ownedDescription(s, function), try std.mem.join(a, "; ", users) }));
    var affected: std.ArrayList(OwnedNode) = .empty;
    for (try nodes(s)) |site| if (hasKind(site.node, "directCall") and m.same(edit.text(site.node, "targetFunctionId"), request.function)) try affected.append(a, site);
    const planned = try a.alloc([]const u8, request.calls.len);
    for (request.calls, planned) |plan, *id| id.* = switch (plan) {
        .bind => |bind| bind.call,
        .leave_missing => |call| call,
    };
    if (!try idsEqual(a, affected.items, planned)) return s.fail("CallPlanMismatch", "Every affected direct call requires exactly one call-site plan.");
    s.root = try edit.replace(a, s.root, "functions", try reconcile(s, try edit.append(a, function, "parameters", p.member)));
    for (request.calls, p.missing) |plan, missing| switch (plan) {
        .bind => |bind| {
            const node = for (try nodes(s)) |site| {
                if (edit.hasId(site.node, bind.call)) break site.node;
            } else unreachable;
            try attachArgument(s, node, model.property(p.member, "id").?, try m.string(a, bind.value));
        },
        .leave_missing => |call| {
            const obligation = try m.object(a, &.{ try m.member(a, "id", missing), try m.member(a, "kind", try m.string(a, "missingArgument")), try m.member(a, "originTransactionId", s.transaction), try m.member(a, "callNodeId", try m.string(a, call)), try m.member(a, "parameterId", model.property(p.member, "id").?), try m.member(a, "status", try m.string(a, "open")) });
            s.root = try edit.append(a, s.root, "obligations", obligation);
            try s.add(.obligationsCreated, try m.object(a, &.{ try m.member(a, "obligationId", missing), try m.member(a, "callNodeId", try m.string(a, call)), try m.member(a, "parameterId", model.property(p.member, "id").?) }));
        },
    };
    try s.add(.parametersAdded, try m.object(a, &.{ try m.member(a, "ownerFunctionId", model.property(function, "id").?), try m.member(a, "parameterId", model.property(p.member, "id").?), try m.member(a, "displayName", model.property(p.member, "displayName").?), try m.member(a, "type", model.property(p.member, "type").?) }));
}
fn sortObligations(items: []Value) void {
    std.mem.sort(Value, items, {}, struct {
        fn less(_: void, x: Value, y: Value) bool {
            return std.mem.order(u16, model.decoded(model.property(x, "obligationId").?), model.decoded(model.property(y, "obligationId").?)) == .lt;
        }
    }.less);
}
fn obligationRecord(a: std.mem.Allocator, obligation: Value) !Value {
    var status = try m.object(a, &.{try m.member(a, "kind", model.property(obligation, "status").?)});
    if (m.same(edit.text(obligation, "status"), "resolved")) {
        status = try edit.set(a, status, "transactionId", model.property(obligation, "resolutionTransactionId").?);
        status = try edit.set(a, status, "valueNodeId", model.property(obligation, "resolutionValueNodeId").?);
    }
    return m.object(a, &.{ try m.member(a, "obligationId", model.property(obligation, "id").?), try m.member(a, "originTransactionId", model.property(obligation, "originTransactionId").?), try m.member(a, "callNodeId", model.property(obligation, "callNodeId").?), try m.member(a, "parameterId", model.property(obligation, "parameterId").?), try m.member(a, "status", status) });
}
fn dropBindings(a: std.mem.Allocator, node: Value, key: []const u8, target_key: []const u8, id: []const u8) !Value {
    var bindings: std.ArrayList(Value) = .empty;
    for (model.arrayItems(node, key)) |binding| if (!m.same(edit.text(binding, target_key), id)) try bindings.append(a, binding);
    return edit.set(a, node, key, .{ .array = bindings.items });
}
fn removeParameter(s: *State, id: []const u8) !void {
    const found = subject(s.root, .parameter, id) orelse return s.fail("TargetNotFound", "The function parameter to remove was not found.");
    const a = s.r.a;
    const owner = found.owner.?;
    const description = try std.fmt.allocPrint(a, "Parameter '{s}' ({s}) of function {s}", .{ query.nameOf(found.item), id, try ownedDescription(s, owner) });
    if (s.changed(.parametersAdded, "parameterId", id)) return s.fail("ParameterCreatedInTransaction", "Remove a newly added parameter in a separate reviewed change.");
    for (model.bodyNodes(a, owner)) |node| if (hasKind(node, "parameterReference") and m.same(edit.text(node, "parameterId"), id)) return s.fail("ParameterStillReferenced", try std.fmt.allocPrint(a, "{s} is still referenced by the function body; replace those references before removing it.", .{description}));
    const users = try nonCallUses(s, query.idOf(owner));
    if (users.len > 0) return s.fail("ParameterRemovalNonCallUse", try std.fmt.allocPrint(a, "Function {s} is used other than as a direct call by {s}. Those uses fix its signature, so only direct calls can drop an argument; replace those uses before removing a parameter of this function.", .{ try ownedDescription(s, owner), try std.mem.join(a, "; ", users) }));
    var affected: std.ArrayList(Value) = .empty;
    const all = try nodes(s);
    var effectful: ?Value = null;
    for (all) |site| {
        if (!hasKind(site.node, "directCall") or !m.same(edit.text(site.node, "targetFunctionId"), query.idOf(owner))) continue;
        try affected.append(a, model.property(site.node, "id").?);
        if (model.arrayItems(site.function, "requiredEffects").len > 0) for (model.arrayItems(site.node, "arguments")) |argument| if (m.same(edit.text(argument, "targetParameterId"), id)) {
            if (effectful == null or idLess(site.function, effectful.?)) effectful = site.function;
        };
    }
    if (effectful) |function| return s.fail("ParameterRemovalMayDiscardEffects", try std.fmt.allocPrint(a, "Function {s} declares effects and binds an argument to {s}; preserve evaluation explicitly before removing the argument binding.", .{ try ownedDescription(s, function), description }));
    sortIdValues(affected.items);
    for (all) |site| if (hasKind(site.node, "directCall") and m.same(edit.text(site.node, "targetFunctionId"), query.idOf(owner))) try updateNode(s, query.idOf(site.node), try dropBindings(a, site.node, "arguments", "targetParameterId", id));
    const current = edit.find(s.root, "functions", query.idOf(owner)).?;
    s.root = try edit.replace(a, s.root, "functions", try reconcile(s, try edit.drop(a, current, "parameters", id)));
    var retired: std.ArrayList(Value) = .empty;
    var kept: std.ArrayList(Value) = .empty;
    for (model.arrayItems(s.root, "obligations")) |obligation| if (m.same(edit.text(obligation, "parameterId"), id)) {
        try retired.append(a, try obligationRecord(a, obligation));
    } else {
        try kept.append(a, obligation);
    };
    sortObligations(retired.items);
    s.root = try edit.set(a, s.root, "obligations", .{ .array = kept.items });
    try s.add(.parametersRemoved, try m.object(a, &.{ try m.member(a, "ownerFunctionId", model.property(owner, "id").?), try m.member(a, "parameterId", model.property(found.item, "id").?), try m.member(a, "displayName", model.property(found.item, "displayName").?), try m.member(a, "affectedCallNodeIds", .{ .array = affected.items }), try m.member(a, "retiredObligations", .{ .array = retired.items }) }));
}

fn validate(s: *State, root: Value) !@TypeOf(@as(model.DocumentRead, undefined).validation_issues) {
    var source: std.Io.Writer.Allocating = .init(s.r.a);
    try m.write(&source.writer, root);
    return edit.validatedIssues(s.r, model.readDocument(s.r.a, source.written()));
}
fn addCapability(s: *State, p: Prepared) !void {
    const request = p.intent.add_capability_operation;
    const a = s.r.a;
    const operation = p.member;
    const owner = edit.find(s.root, "capabilities", request.capability) orelse return s.fail("TargetNotFound", "The target capability was not found.");
    if (try recipe.isBlank(a, request.name)) return s.fail("InvalidDisplayName", "The new operation display name cannot be blank.");
    for (model.arrayItems(owner, "operations")) |existing| if (m.same(query.nameOf(existing), request.name)) return s.fail("DuplicateCapabilityOperationDisplayName", try std.fmt.allocPrint(a, "Capability '{s}' already has an operation named '{s}'; operation display names must be unique within their Capability.", .{ query.nameOf(owner), request.name }));
    const names = try a.alloc([]const u8, request.parameters.len);
    for (request.parameters, names) |parameter, *name| {
        name.* = parameter.name;
        if (try recipe.isBlank(a, name.*)) return s.fail("InvalidDisplayName", "Operation parameter display names cannot be blank.");
    }
    if (preflight.repeated(names) != null) return s.fail("DuplicateCapabilityParameterDisplayName", "Operation parameter display names must be distinct within their operation.");
    if (subject(s.root, .capabilityOperation, query.idOf(operation)) != null) return s.fail("DuplicateCapabilityOperationId", "The new operation or parameter ID already exists.");
    for (model.arrayItems(operation, "parameters")) |parameter| if (subject(s.root, .capabilityParameter, query.idOf(parameter)) != null) return s.fail("DuplicateCapabilityOperationId", "The new operation or parameter ID already exists.");
    if (edit.find(s.root, "effects", request.effect) == null) return s.fail("EffectNotFound", try std.fmt.allocPrint(a, "Effect '{s}' was not found.", .{request.effect}));
    var affected: std.ArrayList(OwnedNode) = .empty;
    for (try nodes(s)) |site| if (hasKind(site.node, "capabilityConstruct") and m.same(edit.text(site.node, "capabilityId"), request.capability)) try affected.append(a, site);
    const planned = try a.alloc([]const u8, request.implementations.len);
    for (request.implementations, planned) |plan, *id| id.* = plan.construct;
    if (!try idsEqual(a, affected.items, planned)) {
        var sites: std.ArrayList([]const u8) = .empty;
        for (affected.items) |site| try sites.append(a, try std.fmt.allocPrint(a, "'{s}' in function {s}", .{ query.idOf(site.node), try ownedDescription(s, site.function) }));
        return s.fail("CapabilityImplementationPlanMismatch", try std.fmt.allocPrint(a, "Every program implementation (capabilityConstruct) of Capability '{s}' requires exactly one implementation plan for the new operation; the constructions are: {s}.", .{ query.nameOf(owner), if (sites.items.len == 0) @as([]const u8, "there are none") else try std.mem.join(a, ", ", sites.items) }));
    }
    s.root = try edit.replace(a, s.root, "capabilities", try edit.append(a, owner, "operations", operation));
    for (affected.items) |site| {
        const plan = for (request.implementations) |plan| {
            if (edit.hasId(site.node, plan.construct)) break plan;
        } else unreachable;
        const implementation = try m.object(a, &.{ try m.member(a, "operationId", model.property(operation, "id").?), try m.member(a, "functionId", try m.string(a, plan.function)) });
        try updateNode(s, plan.construct, try edit.append(a, site.node, "implementations", implementation));
        // Check the newly bound implementation alone, preserving CapabilityImplementations order.
        var isolated = s.*;
        try updateNode(&isolated, plan.construct, try edit.set(a, site.node, "implementations", .{ .array = try a.dupe(Value, &.{implementation}) }));
        const issues = try validate(&isolated, isolated.root);
        const prefix = try std.fmt.allocPrint(a, "In function {s}: Capability construction '{s}': ", .{ try ownedDescription(s, site.function), plan.construct });
        for ([_][]const u8{ "UnknownCapabilityImplementationFunction", "GenericCapabilityImplementationUnsupported", "ImpureCapabilityImplementation", "CapabilityImplementationSignatureMismatch" }) |code| {
            for (issues) |issue| if (m.same(issue.code, code)) {
                const message = model.utf8(issue.message);
                if (std.mem.startsWith(u8, message, prefix)) return s.fail(code, try std.fmt.allocPrint(a, "Capability construction '{s}' in function {s}: {s}", .{ plan.construct, try ownedDescription(s, site.function), message[prefix.len..] }));
            };
        }
        try s.add(.capabilityImplementationsBound, try m.object(a, &.{ try m.member(a, "ownerFunctionId", model.property(site.function, "id").?), try m.member(a, "constructNodeId", model.property(site.node, "id").?), try m.member(a, "operationId", model.property(operation, "id").?), try m.member(a, "functionId", try m.string(a, plan.function)) }));
    }
    try s.add(.capabilityOperationsAdded, try m.object(a, &.{ try m.member(a, "ownerCapabilityId", model.property(owner, "id").?), try m.member(a, "operation", operation) }));
}
fn removeProduct(s: *State, id: []const u8) !void {
    const a = s.r.a;
    const found = subject(s.root, .field, id) orelse return s.fail("TargetNotFound", "The Product field to remove was not found.");
    if (found.variant != null) return s.fail("TargetNotFound", "The Product field to remove was not found.");
    const owner = found.owner.?;
    const all = try nodes(s);
    var affected: std.ArrayList(OwnedNode) = .empty;
    var referenced = false;
    for (all) |site| {
        if (hasKind(site.node, "fieldAccess") and m.same(edit.text(site.node, "fieldId"), id)) referenced = true;
        if (hasKind(site.node, "productUpdate")) for (model.arrayItems(site.node, "fields")) |binding| if (m.same(edit.text(binding, "targetFieldId"), id)) {
            referenced = true;
        };
        if (hasKind(site.node, "productConstruct") and m.same(edit.text(site.node, "typeId"), query.idOf(owner))) for (model.arrayItems(site.node, "fields")) |binding| if (m.same(edit.text(binding, "targetFieldId"), id)) {
            try affected.append(a, site);
        };
    }
    if (s.changed(.productFieldsAdded, "fieldId", id)) return s.fail("ProductFieldCreatedInTransaction", "Remove a newly added field in a separate reviewed change.");
    if (referenced) return s.fail("ProductFieldStillReferenced", "Repair all field accesses and product updates of this field before removing it.");
    for (affected.items) |site| if (model.arrayItems(site.function, "requiredEffects").len > 0) return s.fail("ProductFieldMayDiscardEffects", "An affected function declares effects; preserve evaluation explicitly before removing its field binding.");
    s.root = try edit.replace(a, s.root, "types", try edit.drop(a, owner, "fields", id));
    var affected_ids: std.ArrayList(Value) = .empty;
    for (affected.items) |site| {
        try updateNode(s, query.idOf(site.node), try dropBindings(a, site.node, "fields", "targetFieldId", id));
        try affected_ids.append(a, model.property(site.node, "id").?);
    }
    sortIdValues(affected_ids.items);
    try s.add(.productFieldsRemoved, try m.object(a, &.{ try m.member(a, "ownerTypeId", model.property(owner, "id").?), try m.member(a, "fieldId", model.property(found.item, "id").?), try m.member(a, "displayName", model.property(found.item, "displayName").?), try m.member(a, "affectedConstructNodeIds", .{ .array = affected_ids.items }) }));
}

fn deletion(intent: types.Intent) ?struct { target: types.Target, id: []const u8 } {
    return switch (intent) {
        .delete_function => |id| .{ .target = .function, .id = id },
        .delete_type => |id| .{ .target = .type, .id = id },
        .delete_effect => |id| .{ .target = .effect, .id = id },
        .delete_capability => |id| .{ .target = .capability, .id = id },
        .delete_capability_operation => |id| .{ .target = .capabilityOperation, .id = id },
        else => null,
    };
}
fn afterScheduled(s: *State, root: Value, current: ?types.Intent) !Value {
    var result = root;
    for (s.scheduled) |p| if (deletion(p.intent)) |deleted| {
        if (current) |operation| if (deletion(operation)) |skip| if (deleted.target == skip.target and m.same(deleted.id, skip.id)) {
            continue;
        };
        if (deleted.target == .capabilityOperation) {
            if (subject(result, .capabilityOperation, deleted.id)) |found| result = try edit.replace(s.r.a, result, "capabilities", try edit.drop(s.r.a, found.owner.?, "operations", deleted.id));
        } else result = try edit.drop(s.r.a, result, group(deleted.target), deleted.id);
    };
    return result;
}
fn deletionSafe(s: *State, before: Value, candidate: Value, scheduled: bool, current: ?types.Intent, code: []const u8, name: []const u8) !void {
    const after_issues = try validate(s, if (scheduled) try afterScheduled(s, candidate, null) else candidate);
    if (after_issues.len == 0) return;
    const before_issues = try validate(s, if (scheduled) try afterScheduled(s, before, current) else before);
    const consumed = try s.r.a.alloc(bool, before_issues.len);
    @memset(consumed, false);
    var introduced: std.ArrayList([]const u8) = .empty;
    for (after_issues) |issue| {
        var matched = false;
        for (before_issues, 0..) |old, i| if (!consumed[i] and m.same(issue.code, old.code) and std.mem.eql(u16, issue.message, old.message)) {
            consumed[i] = true;
            matched = true;
            break;
        };
        if (!matched) try introduced.append(s.r.a, try std.fmt.allocPrint(s.r.a, "{s}: {s}", .{ issue.code, model.utf8(issue.message) }));
    }
    if (introduced.items.len > 0) return s.fail(code, try std.fmt.allocPrint(s.r.a, "Repair all references before deleting this {s}{s}: {s}", .{ name, if (introduced.items.len == 1) @as([]const u8, "") else try std.fmt.allocPrint(s.r.a, " ({d} blocking issues)", .{introduced.items.len}), try std.mem.join(s.r.a, " ; ", introduced.items) }));
}
fn deleteDeclaration(s: *State, intent: types.Intent) !void {
    const selected = deletion(intent).?;
    const a = s.r.a;
    const found = subject(s.root, selected.target, selected.id) orelse return s.fail("TargetNotFound", try std.fmt.allocPrint(a, "The {s} to delete was not found.", .{switch (selected.target) {
        .function => @as([]const u8, "function"),
        .type => "type",
        .effect => "effect",
        .capability => "capability",
        .capabilityOperation => "capability operation",
        else => unreachable,
    }}));
    const item = found.item;
    if (selected.target == .function) {
        var callers: std.ArrayList([]const u8) = .empty;
        for (try functionsById(s)) |function| {
            if (edit.hasId(function, selected.id)) continue;
            var scheduled = false;
            for (s.scheduled) |p| if (p.intent == .delete_function and edit.hasId(function, p.intent.delete_function)) {
                scheduled = true;
                break;
            };
            if (scheduled) continue;
            var used = false;
            for (model.bodyNodes(a, function)) |node| for (model.functionIdsOf(node)) |reference| {
                if (m.same(model.utf8(reference), selected.id)) used = true;
            };
            if (used) try callers.append(a, try ownedDescription(s, function));
        }
        if (callers.items.len > 0) return s.fail("FunctionStillReferenced", try std.fmt.allocPrint(a, "Function {s} is still referenced by {s}. Repair or remove those direct calls, callbacks and function references before deleting this function.", .{ try ownedDescription(s, item), try std.mem.join(a, ", ", callers.items) }));
        const body = model.bodyNodes(a, item);
        var kept: std.ArrayList(Value) = .empty;
        var retired: std.ArrayList(Value) = .empty;
        for (model.arrayItems(s.root, "obligations")) |obligation| {
            var owned = false;
            for (body) |node| if (edit.hasId(node, edit.text(obligation, "callNodeId"))) {
                owned = true;
                break;
            };
            if (!owned) {
                try kept.append(a, obligation);
                continue;
            }
            if (m.same(edit.text(obligation, "status"), "open")) return s.fail("FunctionHasObligations", "Resolve open obligations attached to this function before deletion. Deleting the function retires only its resolved obligations, with their exact records in the reviewed diff.");
            try retired.append(a, try obligationRecord(a, obligation));
        }
        s.root = try edit.drop(a, s.root, "functions", selected.id);
        s.root = try edit.set(a, s.root, "obligations", .{ .array = kept.items });
        sortObligations(retired.items);
        var diff = try m.object(a, &.{ try m.member(a, "functionId", model.property(item, "id").?), try m.member(a, "displayName", model.property(item, "displayName").?), try m.member(a, "removedNodeIds", try strings(a, body)) });
        if (retired.items.len > 0) diff = try edit.set(a, diff, "retiredObligations", .{ .array = retired.items });
        try s.add(.functionsRemoved, diff);
        return;
    }
    if (selected.target == .type and (s.changed(.productFieldsAdded, "ownerTypeId", selected.id) or s.changed(.productFieldsRemoved, "ownerTypeId", selected.id) or s.changed(.unionVariantsRemoved, "ownerTypeId", selected.id))) return s.fail("TypeModifiedInTransaction", "Delete a type in a separate reviewed change from its field edits.");
    if (selected.target == .capabilityOperation) for (s.changes[@backingInt(DiffKey.capabilityOperationsAdded)].items) |added| if (edit.hasId(model.property(added, "operation").?, selected.id)) return s.fail("CapabilityOperationCreatedInTransaction", "Delete a newly added operation in a separate reviewed change.");
    const candidate = if (selected.target == .capabilityOperation) try edit.replace(a, s.root, "capabilities", try edit.drop(a, found.owner.?, "operations", selected.id)) else try edit.drop(a, s.root, group(selected.target), selected.id);
    try deletionSafe(s, s.root, candidate, true, intent, switch (selected.target) {
        .type => "TypeStillReferenced",
        .effect => "EffectStillReferenced",
        .capability => "CapabilityStillReferenced",
        .capabilityOperation => "CapabilityOperationStillReferenced",
        else => unreachable,
    }, switch (selected.target) {
        .type => "type",
        .effect => "effect",
        .capability => "capability",
        .capabilityOperation => "capability operation",
        else => unreachable,
    });
    s.root = candidate;
    var fields: std.ArrayList(model.Member) = .empty;
    if (found.owner) |owner| try fields.append(a, try m.member(a, "ownerCapabilityId", model.property(owner, "id").?));
    try fields.append(a, try m.member(a, switch (selected.target) {
        .type => "typeId",
        .effect => "effectId",
        .capability => "capabilityId",
        .capabilityOperation => "operationId",
        else => unreachable,
    }, model.property(item, "id").?));
    try fields.append(a, try m.member(a, "displayName", model.property(item, "displayName").?));
    if (selected.target == .type) {
        const variants = model.arrayItems(item, "variants");
        var removed_fields: std.ArrayList(Value) = .empty;
        try removed_fields.appendSlice(a, model.arrayItems(item, "fields"));
        for (variants) |variant| try removed_fields.appendSlice(a, model.arrayItems(variant, "fields"));
        try fields.append(a, try m.member(a, "removedVariantIds", try strings(a, variants)));
        try fields.append(a, try m.member(a, "removedFieldIds", try strings(a, removed_fields.items)));
    }
    if (selected.target == .capability or selected.target == .capabilityOperation) {
        const operations = if (selected.target == .capability) model.arrayItems(item, "operations") else &.{item};
        var removed_parameters: std.ArrayList(Value) = .empty;
        for (operations) |operation| try removed_parameters.appendSlice(a, model.arrayItems(operation, "parameters"));
        if (selected.target == .capability) try fields.append(a, try m.member(a, "removedOperationIds", try strings(a, operations)));
        try fields.append(a, try m.member(a, "removedParameterIds", try strings(a, removed_parameters.items)));
    }
    try s.add(switch (selected.target) {
        .type => .typesRemoved,
        .effect => .effectsRemoved,
        .capability => .capabilitiesRemoved,
        .capabilityOperation => .capabilityOperationsRemoved,
        else => unreachable,
    }, .{ .object = fields.items });
}

fn derivedBinding(a: std.mem.Allocator, transaction: Value, match_id: []const u8, field_id: []const u8) !Value {
    const seed = try std.fmt.allocPrint(a, "{s}|{s}|{s}", .{ model.utf8(model.decoded(transaction)), match_id, field_id });
    var digest: [32]u8 = undefined;
    std.crypto.hash.sha2.Sha256.hash(seed, &digest, .{});
    std.mem.swap(u8, &digest[0], &digest[3]);
    std.mem.swap(u8, &digest[1], &digest[2]);
    std.mem.swap(u8, &digest[4], &digest[5]);
    std.mem.swap(u8, &digest[6], &digest[7]);
    const hex = std.fmt.bytesToHex(digest[0..16].*, .lower);
    return m.string(a, try std.fmt.allocPrint(a, "{s}-{s}-{s}-{s}-{s}", .{ hex[0..8], hex[8..12], hex[12..16], hex[16..20], hex[20..32] }));
}
fn removeUnion(s: *State, id: []const u8) !void {
    const a = s.r.a;
    const found = subject(s.root, .variant, id) orelse return s.fail("TargetNotFound", "The Union variant to remove was not found.");
    for (s.changes[@backingInt(DiffKey.unionVariantsAdded)].items) |added| if (edit.hasId(model.property(added, "variant").?, id)) return s.fail("UnionVariantCreatedInTransaction", "The Union variant to remove was added in this transaction; leave it out of the batch instead.");
    const owner = found.owner.?;
    const description = try std.fmt.allocPrint(a, "Variant '{s}' ({s}) of Union '{s}' ({s})", .{ query.nameOf(found.item), id, query.nameOf(owner), query.idOf(owner) });
    const variants = model.arrayItems(owner, "variants");
    var constructors: std.ArrayList([]const u8) = .empty;
    var matches: std.ArrayList(OwnedNode) = .empty;
    for (try nodes(s)) |site| {
        if (hasKind(site.node, "unionConstruct") and m.same(edit.text(site.node, "variantId"), id) and !preflight.contains(constructors.items, query.idOf(site.function))) try constructors.append(a, query.idOf(site.function));
        if (hasKind(site.node, "matchUnion")) {
            var matching = false;
            for (model.arrayItems(site.node, "arms")) |arm| for (try edit.variantIds(a, arm)) |variant| {
                if (m.same(model.utf8(model.decoded(variant)), id)) matching = true;
            };
            if (matching) try matches.append(a, site);
        }
    }
    if (variants.len == 1) return s.fail("UnionVariantLast", try std.fmt.allocPrint(a, "{s} is the Union's only variant; delete the type instead.", .{description}));
    std.mem.sort([]const u8, constructors.items, {}, query.ordinal);
    if (constructors.items.len > 0) {
        var users: std.ArrayList([]const u8) = .empty;
        for (constructors.items) |function| try users.append(a, try ownedDescription(s, edit.find(s.root, "functions", function).?));
        return s.fail("UnionVariantStillConstructed", try std.fmt.allocPrint(a, "{s} is still constructed by {s}; replace those constructions before removing the variant.", .{ description, try std.mem.join(a, ", ", users.items) }));
    }
    var effectful: ?OwnedNode = null;
    for (matches.items) |site| if (model.arrayItems(site.function, "requiredEffects").len > 0) for (model.arrayItems(site.node, "arms")) |arm| {
        const selected = try edit.variantIds(a, arm);
        if (selected.len == 1 and m.same(model.utf8(model.decoded(selected[0])), id)) {
            if (effectful == null or idLess(site.function, effectful.?.function) or (edit.hasId(site.function, query.idOf(effectful.?.function)) and idLess(site.node, effectful.?.node))) effectful = site;
        }
    };
    if (effectful) |site| return s.fail("UnionVariantRemovalMayDiscardEffects", try std.fmt.allocPrint(a, "Function {s} declares effects and its match '{s}' has an arm for {s} alone; preserve evaluation explicitly before removing that arm.", .{ try ownedDescription(s, site.function), query.idOf(site.node), description }));
    const before = s.root;
    s.root = try edit.replace(a, s.root, "types", try edit.drop(a, owner, "variants", id));
    var affected_ids: std.ArrayList(Value) = .empty;
    for (matches.items) |site| {
        try affected_ids.append(a, model.property(site.node, "id").?);
        const node = for (try nodes(s)) |current| {
            if (edit.hasId(current.node, query.idOf(site.node))) break current.node;
        } else continue;
        var arms: std.ArrayList(Value) = .empty;
        for (model.arrayItems(node, "arms")) |arm| {
            var remaining: std.ArrayList(Value) = .empty;
            var contains = false;
            for (try edit.variantIds(a, arm)) |variant| if (m.same(model.utf8(model.decoded(variant)), id)) {
                contains = true;
            } else {
                try remaining.append(a, variant);
            };
            if (!contains) {
                try arms.append(a, arm);
                continue;
            }
            if (remaining.items.len == 0) continue;
            var narrowed = try edit.remove(a, try edit.remove(a, arm, "variantId"), "variantIds");
            narrowed = if (remaining.items.len == 1) try edit.set(a, narrowed, "variantId", remaining.items[0]) else try edit.set(a, narrowed, "variantIds", .{ .array = remaining.items });
            if (remaining.items.len == 1) {
                const variant = subject(s.root, .variant, model.utf8(model.decoded(remaining.items[0]))).?.item;
                const fields = model.arrayItems(variant, "fields");
                const bindings = try a.alloc(Value, fields.len);
                var arguments: []const recipe.TypeRef = &.{};
                if (model.completionOf(a, before, site.function, model.decoded(model.property(site.node, "inputNodeId").?))) |completion| if (completion == .normal) {
                    const input_type = try infer.typeOf(a, completion.normal);
                    if (input_type == .named) arguments = input_type.named.arguments;
                };
                var instantiator: infer.Infer = .{ .r = s.r, .a = a, .c = undefined, .local = &.{}, .fresh = &.{} };
                for (fields, bindings) |field, *binding| {
                    const field_type = try instantiator.instantiate(owner, arguments, try infer.typeOf(a, model.property(field, "type").?));
                    const pattern = try m.object(a, &.{ try m.member(a, "id", try derivedBinding(a, s.transaction, query.idOf(node), query.idOf(field))), try m.member(a, "type", try typeValue(s, field_type)) });
                    binding.* = try m.object(a, &.{ try m.member(a, "sourceFieldId", model.property(field, "id").?), try m.member(a, "binding", pattern) });
                }
                narrowed = try edit.set(a, narrowed, "fieldBindings", .{ .array = bindings });
            }
            try arms.append(a, narrowed);
        }
        try updateNode(s, query.idOf(node), try edit.set(a, node, "arms", .{ .array = arms.items }));
    }
    try deletionSafe(s, before, s.root, false, null, "UnionVariantStillReferenced", "Union variant");
    sortIdValues(affected_ids.items);
    try s.add(.unionVariantsRemoved, try m.object(a, &.{ try m.member(a, "ownerTypeId", model.property(owner, "id").?), try m.member(a, "variantId", model.property(found.item, "id").?), try m.member(a, "displayName", model.property(found.item, "displayName").?), try m.member(a, "removedFieldIds", try strings(a, model.arrayItems(found.item, "fields"))), try m.member(a, "affectedMatchNodeIds", .{ .array = affected_ids.items }) }));
}

pub fn simulate(r: *query.Request, intents: []const types.Intent, expected: []const u8) anyerror!Result {
    try r.ensureUsable();
    const plans = try r.a.alloc(preparation.Plans, intents.len);
    for (intents, plans) |intent, *plan| plan.* = try preparation.prepare(r, intent);
    const transaction = try m.id(r.a);
    const snapshot = try m.id(r.a);
    const prepared = try r.a.alloc(Prepared, intents.len);
    for (intents, plans, prepared) |intent, plan, *p| p.* = try materialize(r, intent, plan);
    if (!m.same(expected, r.snapshot_id)) return rejected(r, "StaleBaseSnapshot", "The transaction base snapshot does not match the supplied snapshot.");
    if (intents.len == 0) return rejected(r, "NoOperations", "A transaction must contain at least one semantic operation.");
    var state: State = .{ .r = r, .root = r.model, .transaction = transaction, .scheduled = prepared };
    for (prepared) |p| switch (p.intent) {
        .rename => |change| try state.rename(change),
        .set_exported => |change| try state.visibility(change),
        .set_attribute => |change| try state.attribute(change),
        .add_required_effect => |change| try state.requiredEffect(change, true),
        .remove_required_effect => |change| try state.requiredEffect(change, false),
        .add_function_parameter => try addParameter(&state, p),
        .add_product_field => try addProduct(&state, p),
        .add_capability_operation => try addCapability(&state, p),
        .add_union_variant => try addUnion(&state, p),
        .remove_function_parameter => |id| try removeParameter(&state, id),
        .remove_product_field => |id| try removeProduct(&state, id),
        .remove_union_variant => |id| try removeUnion(&state, id),
        .delete_function, .delete_type, .delete_effect, .delete_capability, .delete_capability_operation => try deleteDeclaration(&state, p.intent),
        .resolve_missing_argument => try resolveMissing(&state, p.intent),
    };
    state.root = try edit.set(r.a, state.root, "snapshotId", snapshot);
    state.root = try edit.set(r.a, state.root, "parentSnapshotId", try m.string(r.a, r.snapshot_id));
    const issues = try validate(&state, state.root);
    if (issues.len > 0) return rejected(r, "InvalidProposedSnapshot", try std.fmt.allocPrint(r.a, "{s}: {s}", .{ issues[0].code, model.utf8(issues[0].message) }));
    var fields: std.ArrayList(model.Member) = .empty;
    inline for (std.enums.values(DiffKey)) |key| try fields.append(r.a, try m.member(r.a, @tagName(key), .{ .array = state.changes[@backingInt(key)].items }));
    const diff = try @import("operation_review.zig").finalizeDiff(r, state.root, .{ .object = fields.items });
    return .{ .transaction = transaction, .snapshot = snapshot, .candidate = state.root, .diff = diff, .summary_operations = try summaryMetadata(r, intents) };
}
fn shown(r: *query.Request, target: types.Target, id: []const u8) ![]const u8 {
    return if (subject(r.model, target, id)) |found| std.fmt.allocPrint(r.a, "'{s}'", .{query.nameOf(found.item)}) else r.a.dupe(u8, id);
}
fn summary(r: *query.Request, intents: []const types.Intent) ![]const u8 {
    var parts: std.ArrayList([]const u8) = .empty;
    for (intents) |intent| {
        const part = switch (intent) {
            .rename => |c| try std.fmt.allocPrint(r.a, "rename{s} {s} to '{s}'", .{ switch (c.target) {
                .function => @as([]const u8, "Function"),
                .type => "Type",
                .field => "Field",
                .variant => "Variant",
                .parameter => "Parameter",
                .effect => "Effect",
                .capability => "Capability",
                .capabilityOperation => "CapabilityOperation",
                .capabilityParameter => "CapabilityParameter",
            }, try shown(r, c.target, c.id), c.name }),
            .set_exported => |c| try std.fmt.allocPrint(r.a, "setExported {s} to {s}", .{ try shown(r, c.target, c.id), if (c.value) @as([]const u8, "exported") else "private" }),
            .set_attribute => |c| try std.fmt.allocPrint(r.a, "set{s} {s} to {s}", .{ switch (c.attribute) {
                .mayWait => @as([]const u8, "MayWait"),
                .taskBound => "TaskBound",
                .mustNotWait => "MustNotWait",
            }, try shown(r, switch (c.attribute) {
                .mayWait => types.Target.capabilityOperation,
                .taskBound => .capability,
                .mustNotWait => .function,
            }, c.id), if (c.value) @as([]const u8, "True") else "False" }),
            .add_function_parameter => |c| try std.fmt.allocPrint(r.a, "addFunctionParameter '{s}' to {s}", .{ c.field.name, try shown(r, .function, c.function) }),
            .add_product_field => |c| try std.fmt.allocPrint(r.a, "addProductField '{s}' to {s}", .{ c.field.name, try shown(r, .type, c.type) }),
            .add_capability_operation => |c| try std.fmt.allocPrint(r.a, "addCapabilityOperation '{s}' to {s}", .{ c.name, try shown(r, .capability, c.capability) }),
            .add_union_variant => |c| try std.fmt.allocPrint(r.a, "addUnionVariant '{s}' to {s}", .{ query.nameOf(c.variant), try shown(r, .type, c.type) }),
            .remove_product_field => |id| try std.fmt.allocPrint(r.a, "removeProductField {s}", .{try shown(r, .field, id)}),
            .remove_function_parameter => |id| try std.fmt.allocPrint(r.a, "removeFunctionParameter {s}", .{try shown(r, .parameter, id)}),
            .remove_union_variant => |id| try std.fmt.allocPrint(r.a, "removeUnionVariant {s}", .{try shown(r, .variant, id)}),
            .delete_function, .delete_type, .delete_effect, .delete_capability, .delete_capability_operation => blk: {
                const d = deletion(intent).?;
                break :blk try std.fmt.allocPrint(r.a, "delete{s} {s}", .{ switch (d.target) {
                    .function => @as([]const u8, "Function"),
                    .type => "Type",
                    .effect => "Effect",
                    .capability => "Capability",
                    .capabilityOperation => "CapabilityOperation",
                    else => unreachable,
                }, try shown(r, d.target, d.id) });
            },
            .resolve_missing_argument => |c| try std.fmt.allocPrint(r.a, "resolveMissingArgument {s}", .{c.obligation}),
            .add_required_effect => |c| try std.fmt.allocPrint(r.a, "addRequiredEffect {s} on {s}", .{ try shown(r, .effect, c.effect), try shown(r, .function, c.function) }),
            .remove_required_effect => |c| try std.fmt.allocPrint(r.a, "removeRequiredEffect {s} from {s}", .{ try shown(r, .effect, c.effect), try shown(r, .function, c.function) }),
        };
        try parts.append(r.a, part);
    }
    return std.mem.join(r.a, "; ", parts.items);
}

pub fn simulateRequest(r: *query.Request, out: *std.Io.Writer) anyerror!void {
    const by_name = m.same((r.root.get("op") orelse unreachable).string, "simulateOperationsByName");
    const intents = try @import("operation_decode.zig").decode(r, by_name);
    var d: recipe.Decoder = .{ .r = r, .duplicates = .empty };
    const expected = if (r.root.get("expectedSnapshotId") != null) try d.stableId("expectedSnapshotId", try d.requiredString(.{ .object = r.root }, "expectedSnapshotId")) else r.snapshot_id;
    try @import("proposals.zig").publishOperations(r, out, try simulate(r, intents, expected));
}

fn summaryMetadata(r: *query.Request, intents: []const types.Intent) !Value {
    const a = r.a;
    const items = try a.alloc(Value, intents.len);
    for (intents, items) |intent, *item| {
        var value = try m.object(a, &.{try m.member(a, "tag", try m.string(a, @tagName(intent)))});
        switch (intent) {
            .rename => |c| {
                value = try edit.set(a, value, "target", try m.string(a, @tagName(c.target)));
                value = try edit.set(a, value, "id", try m.string(a, c.id));
                value = try edit.set(a, value, "name", try m.string(a, c.name));
            },
            .set_exported => |c| {
                value = try edit.set(a, value, "target", try m.string(a, @tagName(c.target)));
                value = try edit.set(a, value, "id", try m.string(a, c.id));
                value = try edit.set(a, value, "value", if (c.value) .json_true else .json_false);
            },
            .set_attribute => |c| {
                value = try edit.set(a, value, "attribute", try m.string(a, @tagName(c.attribute)));
                value = try edit.set(a, value, "id", try m.string(a, c.id));
                value = try edit.set(a, value, "value", if (c.value) .json_true else .json_false);
            },
            .add_function_parameter => |c| {
                value = try edit.set(a, value, "id", try m.string(a, c.function));
                value = try edit.set(a, value, "name", try m.string(a, c.field.name));
            },
            .add_product_field => |c| {
                value = try edit.set(a, value, "id", try m.string(a, c.type));
                value = try edit.set(a, value, "name", try m.string(a, c.field.name));
            },
            .add_capability_operation => |c| {
                value = try edit.set(a, value, "id", try m.string(a, c.capability));
                value = try edit.set(a, value, "name", try m.string(a, c.name));
            },
            .add_union_variant => |c| {
                value = try edit.set(a, value, "id", try m.string(a, c.type));
                value = try edit.set(a, value, "name", try m.string(a, query.nameOf(c.variant)));
            },
            .resolve_missing_argument => |c| value = try edit.set(a, value, "id", try m.string(a, c.obligation)),
            .add_required_effect, .remove_required_effect => |c| {
                value = try edit.set(a, value, "id", try m.string(a, c.function));
                value = try edit.set(a, value, "effect", try m.string(a, c.effect));
            },
            .remove_product_field, .remove_function_parameter, .remove_union_variant, .delete_function, .delete_type, .delete_effect, .delete_capability, .delete_capability_operation => |id| value = try edit.set(a, value, "id", try m.string(a, id)),
        }
        item.* = value;
    }
    return .{ .array = items };
}
pub fn summaryOperations(r: *query.Request, metadata: Value) ![]const u8 {
    const intents = try r.a.alloc(types.Intent, metadata.array.len);
    for (metadata.array, intents) |item, *intent| {
        const tag = std.meta.stringToEnum(std.meta.Tag(types.Intent), edit.text(item, "tag")).?;
        const id = edit.text(item, "id");
        intent.* = switch (tag) {
            .rename => .{ .rename = .{ .target = std.meta.stringToEnum(types.Target, edit.text(item, "target")).?, .id = id, .name = edit.text(item, "name") } },
            .set_exported => .{ .set_exported = .{ .target = std.meta.stringToEnum(types.Target, edit.text(item, "target")).?, .id = id, .value = model.property(item, "value").? == .json_true } },
            .set_attribute => .{ .set_attribute = .{ .attribute = std.meta.stringToEnum(types.Attribute, edit.text(item, "attribute")).?, .id = id, .value = model.property(item, "value").? == .json_true } },
            .add_function_parameter => .{ .add_function_parameter = .{ .function = id, .field = .{ .name = edit.text(item, "name"), .type = undefined }, .calls = &.{} } },
            .add_product_field => .{ .add_product_field = .{ .type = id, .field = .{ .name = edit.text(item, "name"), .type = undefined }, .constructions = &.{} } },
            .add_capability_operation => .{ .add_capability_operation = .{ .capability = id, .name = edit.text(item, "name"), .parameters = &.{}, .result = undefined, .effect = "", .may_wait = false, .implementations = &.{} } },
            .add_union_variant => .{ .add_union_variant = .{ .type = id, .variant = try m.object(r.a, &.{try m.member(r.a, "displayName", model.property(item, "name").?)}), .arms = &.{} } },
            .resolve_missing_argument => .{ .resolve_missing_argument = .{ .obligation = id, .value = "" } },
            .add_required_effect => .{ .add_required_effect = .{ .function = id, .effect = edit.text(item, "effect") } },
            .remove_required_effect => .{ .remove_required_effect = .{ .function = id, .effect = edit.text(item, "effect") } },
            .remove_product_field => .{ .remove_product_field = id },
            .remove_function_parameter => .{ .remove_function_parameter = id },
            .remove_union_variant => .{ .remove_union_variant = id },
            .delete_function => .{ .delete_function = id },
            .delete_type => .{ .delete_type = id },
            .delete_effect => .{ .delete_effect = id },
            .delete_capability => .{ .delete_capability = id },
            .delete_capability_operation => .{ .delete_capability_operation = id },
        };
    }
    return summary(r, intents);
}
