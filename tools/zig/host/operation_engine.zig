//! Transactions over immutable request-local model paths; publication owns the typed generation.
const std = @import("std");
const model = @import("model_reader");
const query = @import("query.zig");
const recipe = @import("recipe.zig");
const preflight = @import("preflight.zig");
const infer = @import("infer.zig");
const m = @import("materialize.zig");
const edit = @import("typed_edit.zig");
const types = @import("operation_types.zig");
const preparation = @import("operation_prepare.zig");
const Value = model.Value;
const Record = model.Model;
const tm = model.session_model;
const wireedit = @import("operation_model.zig");

fn idValue(record: Record) Value {
    return .{ .string = record.id() };
}
fn nameValue(record: Record) Value {
    return .{ .string = record.displayName() };
}
fn refValue(record: Record, name: []const u8) Value {
    return .{ .string = record.reference(name) };
}
fn intern(r: *query.Request, domain: tm.Domain, text: []const u8) !tm.Id {
    return r.interner.?.id(domain, try std.unicode.utf8ToUtf16LeAlloc(r.a, text));
}
fn materializer(r: *query.Request) m.Materializer {
    return .{ .a = r.a, .r = r, .types = undefined, .fresh = &.{}, .function_id = tm.none, .parameters = &.{} };
}

fn nodeValue(r: *query.Request, id: tm.Id) Value {
    return .{ .string = r.interner.?.idText(.node, id) };
}
fn wireRecord(a: std.mem.Allocator, record: Record) anyerror!Value {
    const interner = record.context.interner;
    var fields: std.ArrayList(model.Member) = .empty;
    try fields.appendSlice(a, &.{ try m.member(a, "id", idValue(record)), try m.member(a, "displayName", nameValue(record)) });
    switch (record.data) {
        .slot => |slot| try fields.append(a, try m.member(a, "type", try edit.wireType(a, interner, slot.value.type))),
        .variant => {
            const items = record.fields();
            const values = try a.alloc(Value, items.len);
            for (items, values) |item, *slot| slot.* = try wireRecord(a, item);
            try fields.append(a, try m.member(a, "fields", .{ .array = values }));
        },
        .operation => |operation| {
            const items = record.parameters();
            const values = try a.alloc(Value, items.len);
            for (items, values) |item, *slot| slot.* = try wireRecord(a, item);
            try fields.appendSlice(a, &.{ try m.member(a, "parameters", .{ .array = values }), try m.member(a, "resultType", try edit.wireType(a, interner, operation.result)) });
            if (operation.failure) |failure| try fields.append(a, try m.member(a, "failureType", try edit.wireType(a, interner, failure)));
            try fields.append(a, try m.member(a, "effectId", .{ .string = interner.idText(.effect, operation.effect) }));
            if (operation.may_wait) try fields.append(a, try m.member(a, "mayWait", .json_true));
        },
        else => unreachable,
    }
    return .{ .object = fields.items };
}
pub const Result = struct { transaction: Value, snapshot: Value, candidate: Record, diff: Value, summary_operations: Value };
pub const DiffKey = enum { renames, parametersAdded, productFieldsAdded, productFieldsBound, productFieldsRemoved, effectsRemoved, capabilitiesRemoved, capabilityOperationsRemoved, typesRemoved, functionsRemoved, argumentsAttached, obligationsCreated, obligationsResolved, requiredEffectsChanged, visibilityChanges, attributeChanges, parametersRemoved, unionVariantsRemoved, capabilityOperationsAdded, capabilityImplementationsBound, unionVariantsAdded, unionMatchArmsAdded };
const Prepared = struct { intent: types.Intent, plans: preparation.Plans, member: ?Record = null, missing: []const ?tm.Id = &.{} };

pub fn rejected(r: *query.Request, code: []const u8, message: []const u8) anyerror {
    const result = preflight.rejected(r, code, message);
    r.rejection.message = "The requested operations were rejected.";
    return result;
}
const Subject = struct { item: Record, owner: ?Record = null, variant: ?Record = null, operation: ?Record = null };
fn group(target: types.Target) edit.Collection {
    return switch (target) {
        .function, .parameter => .functions,
        .type, .field, .variant => .types,
        .effect => .effects,
        .capability, .capabilityOperation, .capabilityParameter => .capabilities,
    };
}
fn subject(root: Record, target: types.Target, id: []const u8) ?Subject {
    for (edit.children(root, group(target))) |owner| {
        switch (target) {
            .function, .type, .effect, .capability => if (edit.hasId(owner, id)) return .{ .item = owner },
            .parameter => for (owner.parameters()) |item| {
                if (edit.hasId(item, id)) return .{ .item = item, .owner = owner };
            },
            .variant => for (owner.variants()) |item| {
                if (edit.hasId(item, id)) return .{ .item = item, .owner = owner };
            },
            .field => {
                for (owner.fields()) |item| if (edit.hasId(item, id)) return .{ .item = item, .owner = owner };
                for (owner.variants()) |variant| for (variant.fields()) |item| {
                    if (edit.hasId(item, id)) return .{ .item = item, .owner = owner, .variant = variant };
                };
            },
            .capabilityOperation, .capabilityParameter => for (owner.operations()) |operation| {
                if (target == .capabilityOperation and edit.hasId(operation, id)) return .{ .item = operation, .owner = owner };
                if (target == .capabilityParameter) for (operation.parameters()) |item| {
                    if (edit.hasId(item, id)) return .{ .item = item, .owner = owner, .operation = operation };
                };
            },
        }
    }
    return null;
}
const State = struct {
    r: *query.Request,
    root: Record,
    transaction: Value,
    scheduled: []const Prepared,
    changes: [std.enums.values(DiffKey).len]std.ArrayList(Value) = @splat(.empty),

    fn add(self: *State, key: DiffKey, value: Value) !void {
        try self.changes[@backingInt(key)].append(self.r.a, value);
    }
    fn changed(self: *State, key: DiffKey, field: []const u8, id: []const u8) bool {
        for (self.changes[@backingInt(key)].items) |item| if (m.same(wireedit.text(item, field), id)) return true;
        return false;
    }
    fn fail(self: *State, code: []const u8, message: []const u8) anyerror {
        return rejected(self.r, code, message);
    }
    fn replaceSubject(self: *State, target: types.Target, found: Subject, item: Record) !void {
        const a = self.r.a;
        var result = item;
        if (found.operation) |operation| result = try edit.replace(a, operation, .parameters, result);
        if (found.variant) |variant| result = try edit.replace(a, variant, .fields, result);
        if (found.owner) |owner| result = try edit.replace(a, owner, if (found.operation != null or target == .capabilityOperation) .operations else if (found.variant != null or target == .variant) .variants else if (target == .field) .fields else .parameters, result);
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
        try self.replaceSubject(change.target, found, try edit.changed(self.r.a, found.item, .{ .name = try self.r.interner.?.string(try std.unicode.utf8ToUtf16LeAlloc(self.r.a, change.name)) }));
    }
    fn visibility(self: *State, change: types.Visibility) !void {
        const found = subject(self.root, change.target, change.id) orelse return self.fail("TargetNotFound", try std.fmt.allocPrint(self.r.a, "The declaration '{s}' to change the visibility of was not found.", .{change.id}));
        if (found.item.exported() == change.value) return self.fail("VisibilityUnchanged", try std.fmt.allocPrint(self.r.a, "The declaration '{s}' is already {s}.", .{ change.id, if (change.value) @as([]const u8, "exported") else "private" }));
        try self.replaceSubject(change.target, found, try edit.changed(self.r.a, found.item, .{ .exported = change.value }));
    }
    fn attribute(self: *State, change: types.Flag) !void {
        const target: types.Target = switch (change.attribute) {
            .mayWait => .capabilityOperation,
            .taskBound => .capability,
            .mustNotWait => .function,
        };
        const found = subject(self.root, target, change.id) orelse return self.fail("TargetNotFound", try std.fmt.allocPrint(self.r.a, "The declaration '{s}' to change was not found.", .{change.id}));
        const key = @tagName(change.attribute);
        const previous = switch (change.attribute) {
            .mayWait => found.item.mayWait(),
            .taskBound => found.item.taskBound(),
            .mustNotWait => found.item.mustNotWait(),
        };
        if (previous == change.value) return self.fail("AttributeUnchanged", try std.fmt.allocPrint(self.r.a, "The declaration '{s}' already has {s} {s}.", .{ change.id, key, if (change.value) @as([]const u8, "true") else "false" }));
        try self.replaceSubject(target, found, try edit.changed(self.r.a, found.item, switch (change.attribute) {
            .mayWait => .{ .may_wait = change.value },
            .taskBound => .{ .task_bound = change.value },
            .mustNotWait => .{ .must_not_wait = change.value },
        }));
    }
    fn requiredEffect(self: *State, change: types.RequiredEffect, adding: bool) !void {
        const function = edit.find(self.root, .functions, change.function) orelse return self.fail("TargetNotFound", "The target function was not found.");
        if (edit.find(self.root, .effects, change.effect) == null) return self.fail("EffectNotFound", try std.fmt.allocPrint(self.r.a, "Effect '{s}' was not found.", .{change.effect}));
        var present = false;
        var effects: std.ArrayList(tm.Id) = .empty;
        for (function.data.function.effects) |effect| {
            if (m.same(model.utf8(self.r.interner.?.idText(.effect, effect)), change.effect)) present = true else try effects.append(self.r.a, effect);
        }
        if (adding and present) return self.fail("FunctionRequiredEffectAlreadyPresent", "The function already declares the required effect.");
        if (!adding and !present) return self.fail("FunctionRequiredEffectNotPresent", "The function does not declare the required effect.");
        if (adding) try effects.append(self.r.a, try intern(self.r, .effect, change.effect));
        self.root = try edit.replace(self.r.a, self.root, .functions, try edit.changed(self.r.a, function, .{ .effects = effects.items }));
        try self.add(.requiredEffectsChanged, try m.object(self.r.a, &.{ try m.member(self.r.a, "kind", try m.string(self.r.a, if (adding) "added" else "removed")), try m.member(self.r.a, "functionId", try m.string(self.r.a, change.function)), try m.member(self.r.a, "effectId", try m.string(self.r.a, change.effect)) }));
    }
};

const OwnedNode = struct { function: Record, node: Record };
fn nodes(s: *State) ![]const OwnedNode {
    var result: std.ArrayList(OwnedNode) = .empty;
    for (s.root.functions()) |function| for (model.typedBodyNodes(s.r.a, function)) |node| try result.append(s.r.a, .{ .function = function, .node = node });
    return result.items;
}
fn updateNode(s: *State, id: []const u8, replacement: Record) !void {
    s.root = try edit.replaceNode(s.r.a, s.root, try intern(s.r, .node, id), replacement);
}
fn hasKind(node: Record, kind: []const u8) bool {
    return m.same(node.data.node.kind(), kind);
}
fn idLess(x: Record, y: Record) bool {
    return std.mem.order(u16, x.id(), y.id()) == .lt;
}
fn functionsById(s: *State) ![]const Record {
    const result = try s.r.a.dupe(Record, s.root.functions());
    std.mem.sort(Record, result, {}, struct {
        fn less(_: void, x: Record, y: Record) bool {
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
fn strings(a: std.mem.Allocator, items: []const Record) !Value {
    const result = try a.alloc(Value, items.len);
    for (items, result) |item, *id| id.* = idValue(item);
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
fn ownedDescription(s: *State, function: Record) ![]const u8 {
    return std.fmt.allocPrint(s.r.a, "'{s}' ({s})", .{ query.nameOf(function), query.idOf(function) });
}
fn typeValue(s: *State, t: recipe.TypeRef) !tm.TypeRef {
    var constructor = materializer(s.r);
    return constructor.typeRef(t);
}
fn typedMember(s: *State, field: types.Field, domain: tm.Domain, id: tm.Id) !Record {
    const record = try s.r.a.create(tm.Typed);
    var constructor = materializer(s.r);
    record.* = .{ .id = id, .name = try constructor.internString(field.name), .type = try constructor.typeRef(field.type) };
    return edit.borrow(s.root, .{ .slot = .{ .domain = domain, .value = record } });
}
fn materialize(r: *query.Request, intent: types.Intent, plans: preparation.Plans) !Prepared {
    var result: Prepared = .{ .intent = intent, .plans = plans };
    var scratch: State = .{ .r = r, .root = r.typedModel(), .transaction = .json_null, .scheduled = &.{} };
    var constructor = materializer(r);
    switch (intent) {
        .add_function_parameter => |parameter| {
            result.member = try typedMember(&scratch, parameter.field, .parameter, try constructor.freshId(.parameter));
            const obligations = try r.a.alloc(?tm.Id, parameter.calls.len);
            for (parameter.calls, obligations) |plan, *obligation| obligation.* = if (plan == .leave_missing) try constructor.freshId(.obligation) else null;
            result.missing = obligations;
        },
        .add_product_field => |field| result.member = try typedMember(&scratch, field.field, .field, try constructor.freshId(.field)),
        .add_capability_operation => |operation| {
            const operation_id = try constructor.freshId(.operation);
            const parameters = try r.a.alloc(tm.Typed, operation.parameters.len);
            for (operation.parameters, parameters) |parameter, *slot| {
                const parameter_id = try constructor.freshId(.capability_parameter);
                slot.* = (try typedMember(&scratch, parameter, .capability_parameter, parameter_id)).data.slot.value.*;
            }
            const record = try r.a.create(tm.Operation);
            record.* = .{ .id = operation_id, .name = try constructor.internString(operation.name), .parameters = parameters, .result = try typeValue(&scratch, operation.result), .effect = try intern(r, .effect, operation.effect), .failure = null, .may_wait = operation.may_wait };
            result.member = edit.borrow(scratch.root, .{ .operation = record });
        },
        else => {},
    }
    return result;
}
fn targetList(a: std.mem.Allocator, values: tm.Targets, addition: tm.Target) !tm.Targets {
    const words = try a.alloc(u32, values.words.len + 2);
    @memcpy(words[0..values.words.len], values.words);
    words[values.words.len] = addition.target;
    words[values.words.len + 1] = addition.value;
    return .{ .words = words };
}
fn attachArgument(s: *State, node: Record, parameter: tm.Id, value: tm.Id) !void {
    const a = s.r.a;
    const args = node.payload("arguments").?.args;
    try updateNode(s, query.idOf(node), try edit.nodePayload(a, node, "arguments", .{ .args = try targetList(a, args, .{ .target = parameter, .value = value }) }));
    try s.add(.argumentsAttached, try m.object(a, &.{ try m.member(a, "callNodeId", idValue(node)), try m.member(a, "parameterId", .{ .string = s.r.interner.?.idText(.parameter, parameter) }), try m.member(a, "valueNodeId", .{ .string = s.r.interner.?.idText(.node, value) }) }));
}
fn resolveMissing(s: *State, request: types.Intent) !void {
    const intent = request.resolve_missing_argument;
    const obligation = edit.find(s.root, .obligations, intent.obligation) orelse return s.fail("ObligationNotOpen", "The missing-argument obligation does not exist.");
    if (obligation.data.obligation.resolution != null) return s.fail("ObligationNotOpen", "The missing-argument obligation is already resolved.");
    const call_id = model.utf8(obligation.reference("callNodeId"));
    const site = for (try nodes(s)) |site| {
        if (edit.hasId(site.node, call_id) and hasKind(site.node, "directCall")) break site;
    } else return s.fail("ObligationTargetMismatch", "The obligated direct-call node no longer exists.");
    const value = try intern(s.r, .node, intent.value);
    try attachArgument(s, site.node, obligation.data.obligation.parameter, value);
    const transaction = try s.r.interner.?.id(.transaction, s.transaction.string);
    const resolved = try edit.changed(s.r.a, obligation, .{ .resolution = .{ .transaction = transaction, .value = value } });
    s.root = try edit.replace(s.r.a, s.root, .obligations, resolved);
    try s.add(.obligationsResolved, try m.object(s.r.a, &.{ try m.member(s.r.a, "obligationId", idValue(obligation)), try m.member(s.r.a, "callNodeId", refValue(obligation, "callNodeId")), try m.member(s.r.a, "parameterId", refValue(obligation, "parameterId")), try m.member(s.r.a, "valueNodeId", .{ .string = s.r.interner.?.idText(.node, value) }) }));
}

fn insertNodes(a: std.mem.Allocator, root: Record, plans: []const preparation.Construction) anyerror!Record {
    var result = root;
    for (root.functions()) |function| {
        var replacements: std.ArrayList(tm.builder.Replacement) = .empty;
        for (plans) |plan| {
            if (plan.nodes.len == 0) continue;
            const wanted = function.context.interner.findId(.node, try std.unicode.utf8ToUtf16LeAlloc(a, plan.construct)) orelse continue;
            const node = function.data.function.body.findNode(wanted) orelse continue;
            const draft = try tm.builder.fromNode(a, node);
            try replacements.append(a, .{ .node = wanted, .nodes = try std.mem.concat(a, tm.builder.NodeDraft, &.{ plan.nodes, &.{draft} }) });
        }
        if (replacements.items.len == 0) continue;
        const body = try tm.builder.rebuild(a, function.data.function.body.graph(0), .{ .replacements = replacements.items });
        result = try edit.replace(a, result, .functions, try edit.changed(a, function, .{ .body = body }));
    }
    return result;
}
fn addProduct(s: *State, p: Prepared) !void {
    const request = p.intent.add_product_field;
    const a = s.r.a;
    const owner = edit.find(s.root, .types, request.type) orelse return s.fail("TargetNotFound", "The target product type was not found.");
    if (!owner.data.type_declaration.product) return s.fail("ProductFieldTargetNotProduct", "The target type must be a Product.");
    var affected: std.ArrayList(OwnedNode) = .empty;
    for (try nodes(s)) |site| if (hasKind(site.node, "productConstruct") and m.same(model.utf8(site.node.reference("typeId")), request.type)) try affected.append(a, site);
    const planned = try a.alloc([]const u8, p.plans.constructions.len);
    for (p.plans.constructions, planned) |plan, *id| id.* = plan.construct;
    if (try recipe.isBlank(a, request.field.name)) return s.fail("InvalidDisplayName", "The new field display name cannot be blank.");
    for (owner.fields()) |field| if (m.same(query.nameOf(field), request.field.name)) return s.fail("DuplicateFieldDisplayName", "The product already has a field with this display name.");
    if (subject(s.root, .field, query.idOf(p.member.?)) != null) return s.fail("DuplicateFieldId", "The new field ID already exists.");
    if (!try idsEqual(a, affected.items, planned)) return s.fail("ProductFieldPlanMismatch", "Every affected ProductConstruct requires exactly one field-value plan.");
    const inserted = try insertNodes(a, s.root, p.plans.constructions);
    for (affected.items) |site| {
        const plan = for (p.plans.constructions) |plan| {
            if (edit.hasId(site.node, plan.construct)) break plan;
        } else unreachable;
        const function = edit.find(inserted, .functions, query.idOf(site.function)).?;
        const completion = model.typedCompletionOf(a, inserted, function, s.r.interner.?.idText(.node, plan.value));
        if (completion == null or completion.? != .normal or !infer.eqlType(try infer.typeOf(a, completion.?.normal), request.field.type)) return s.fail("ProductFieldValueTypeMismatch", "The planned field value must exist in its owning function and have the new field type.");
    }
    s.root = inserted;
    s.root = try edit.replace(a, s.root, .types, try edit.append(a, owner, .fields, p.member.?));
    for (affected.items) |site| {
        const plan = for (p.plans.constructions) |plan| {
            if (edit.hasId(site.node, plan.construct)) break plan;
        } else unreachable;
        // Fetch the node after insertion: nested plan nodes may already have changed its graph.
        const node = for (try nodes(s)) |current| {
            if (edit.hasId(current.node, plan.construct)) break current.node;
        } else unreachable;
        const binding: tm.Target = .{ .target = p.member.?.data.slot.value.id, .value = plan.value };
        try updateNode(s, plan.construct, try edit.nodePayload(a, node, "fields", .{ .fields = try targetList(a, node.payload("fields").?.fields, binding) }));
        try s.add(.productFieldsBound, try m.object(a, &.{ try m.member(a, "ownerFunctionId", idValue(site.function)), try m.member(a, "constructNodeId", idValue(node)), try m.member(a, "fieldId", idValue(p.member.?)), try m.member(a, "valueNodeId", nodeValue(s.r, plan.value)) }));
    }
    try s.add(.productFieldsAdded, try m.object(a, &.{ try m.member(a, "ownerTypeId", idValue(owner)), try m.member(a, "fieldId", idValue(p.member.?)), try m.member(a, "displayName", nameValue(p.member.?)), try m.member(a, "type", try edit.wireType(a, s.r.interner.?, p.member.?.data.slot.value.type)) }));
}
fn addUnion(s: *State, p: Prepared) !void {
    const request = p.intent.add_union_variant;
    const a = s.r.a;
    const variant = request.variant;
    const owner = edit.find(s.root, .types, request.type) orelse return s.fail("TargetNotFound", "The target type was not found.");
    if (owner.data.type_declaration.product) return s.fail("TargetNotUnion", "The target type of addUnionVariant must be a Union.");
    if (try recipe.isBlank(a, query.nameOf(variant))) return s.fail("InvalidDisplayName", "The new variant display name cannot be blank.");
    for (owner.variants()) |existing| if (m.same(query.nameOf(existing), query.nameOf(variant))) return s.fail("DuplicateUnionVariantDisplayName", try std.fmt.allocPrint(a, "Union '{s}' already has a variant named '{s}'; variant display names must be unique within their Union.", .{ query.nameOf(owner), query.nameOf(variant) }));
    const fields = variant.fields();
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
        for (site.node.arms()) |arm| for (arm.variantIds()) |id| for (owner.variants()) |existing| {
            if (m.same(query.idOf(existing), model.utf8(id.literal()))) matching = true;
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
    s.root = try edit.replace(a, s.root, .types, try edit.append(a, owner, .variants, variant));
    for (affected.items) |site| {
        const plan = for (p.plans.arms) |plan| {
            if (edit.hasId(site.node, plan.match)) break plan;
        } else unreachable;
        const node = for (try nodes(s)) |current| {
            if (edit.hasId(current.node, plan.match)) break current.node;
        } else unreachable;
        var arms: std.ArrayList(tm.UnionArm) = .empty;
        var iterator = node.payload("arms").?.arms.iterator();
        while (iterator.next()) |arm| try arms.append(a, arm);
        try arms.append(a, .{ .variants = try a.dupe(tm.Id, &.{variant.data.variant.id}), .fields = plan.bindings, .body = plan.body });
        try updateNode(s, plan.match, try edit.nodePayload(a, node, "arms", .{ .arms = try tm.builder.unionArms(a, arms.items) }));
        const ids = try a.alloc(Value, plan.bindings.len());
        for (ids, 0..) |*id, index| id.* = .{ .string = s.r.interner.?.idText(.binding, plan.bindings.at(index).binding.id) };
        try s.add(.unionMatchArmsAdded, try m.object(a, &.{ try m.member(a, "ownerFunctionId", idValue(site.function)), try m.member(a, "matchNodeId", idValue(node)), try m.member(a, "variantId", idValue(variant)), try m.member(a, "patternBindingIds", .{ .array = ids }) }));
    }
    try s.add(.unionVariantsAdded, try m.object(a, &.{ try m.member(a, "ownerTypeId", idValue(owner)), try m.member(a, "variant", try wireRecord(a, variant)) }));
}

fn nonCallUses(s: *State, id: []const u8) ![]const []const u8 {
    const a = s.r.a;
    var users: std.ArrayList([]const u8) = .empty;
    for (try functionsById(s)) |function| {
        var labels: std.ArrayList([]const u8) = .empty;
        for (model.typedBodyNodes(a, function)) |node| for (model.typedFunctionRelationsOf(node)) |reference| {
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
fn reconcile(s: *State, function: Record) !Record {
    const a = s.r.a;
    var occurring: std.ArrayList([]const u8) = .empty;
    for (function.parameters()) |parameter| try preflight.occurring(a, try infer.typeOf(a, parameter.declaredType()), &occurring);
    var parameters: std.ArrayList(tm.TypeParameter) = .empty;
    for (function.data.function.type_parameters orelse &.{}) |parameter| if (preflight.contains(occurring.items, model.utf8(s.r.interner.?.idText(.type_parameter, parameter.id)))) try parameters.append(a, parameter);
    for (occurring.items) |id| {
        const handle = try intern(s.r, .type_parameter, id);
        var present = false;
        for (parameters.items) |parameter| if (parameter.id == handle) {
            present = true;
            break;
        };
        if (!present) try parameters.append(a, .{ .id = handle, .name = try s.r.interner.?.string(try std.unicode.utf8ToUtf16LeAlloc(a, id)) });
    }
    return edit.changed(a, function, .{ .type_parameters = if (parameters.items.len == 0) null else parameters.items });
}
fn addParameter(s: *State, p: Prepared) !void {
    const request = p.intent.add_function_parameter;
    const a = s.r.a;
    if (try recipe.isBlank(a, request.field.name)) return s.fail("InvalidDisplayName", "A parameter display name cannot be blank.");
    if (subject(s.root, .parameter, query.idOf(p.member.?)) != null) return s.fail("DuplicateParameterId", "The new parameter ID already exists.");
    const function = edit.find(s.root, .functions, request.function) orelse return s.fail("TargetNotFound", "The target function was not found.");
    const users = try nonCallUses(s, request.function);
    if (users.len > 0) return s.fail("ParameterAdditionNonCallUse", try std.fmt.allocPrint(a, "Function {s} is used other than as a direct call by {s}. Those uses fix its signature, so only direct calls can supply a new parameter; replace those uses before adding a parameter to this function.", .{ try ownedDescription(s, function), try std.mem.join(a, "; ", users) }));
    var affected: std.ArrayList(OwnedNode) = .empty;
    for (try nodes(s)) |site| if (hasKind(site.node, "directCall") and m.same(model.utf8(site.node.reference("targetFunctionId")), request.function)) try affected.append(a, site);
    const planned = try a.alloc([]const u8, request.calls.len);
    for (request.calls, planned) |plan, *id| id.* = switch (plan) {
        .bind => |bind| bind.call,
        .leave_missing => |call| call,
    };
    if (!try idsEqual(a, affected.items, planned)) return s.fail("CallPlanMismatch", "Every affected direct call requires exactly one call-site plan.");
    s.root = try edit.replace(a, s.root, .functions, try reconcile(s, try edit.append(a, function, .parameters, p.member.?)));
    for (request.calls, p.missing) |plan, missing| switch (plan) {
        .bind => |bind| {
            const node = for (try nodes(s)) |site| {
                if (edit.hasId(site.node, bind.call)) break site.node;
            } else unreachable;
            try attachArgument(s, node, p.member.?.data.slot.value.id, try intern(s.r, .node, bind.value));
        },
        .leave_missing => |call| {
            const obligation = try a.create(tm.Obligation);
            obligation.* = .{ .id = missing.?, .origin = try s.r.interner.?.id(.transaction, s.transaction.string), .call = try intern(s.r, .node, call), .parameter = p.member.?.data.slot.value.id, .resolution = null };
            s.root = try edit.append(a, s.root, .obligations, edit.borrow(s.root, .{ .obligation = obligation }));
            try s.add(.obligationsCreated, try m.object(a, &.{ try m.member(a, "obligationId", .{ .string = s.r.interner.?.idText(.obligation, missing.?) }), try m.member(a, "callNodeId", try m.string(a, call)), try m.member(a, "parameterId", idValue(p.member.?)) }));
        },
    };
    try s.add(.parametersAdded, try m.object(a, &.{ try m.member(a, "ownerFunctionId", idValue(function)), try m.member(a, "parameterId", idValue(p.member.?)), try m.member(a, "displayName", nameValue(p.member.?)), try m.member(a, "type", try edit.wireType(a, s.r.interner.?, p.member.?.data.slot.value.type)) }));
}
fn sortObligations(items: []Value) void {
    std.mem.sort(Value, items, {}, struct {
        fn less(_: void, x: Value, y: Value) bool {
            return std.mem.order(u16, model.decoded(model.property(x, "obligationId").?), model.decoded(model.property(y, "obligationId").?)) == .lt;
        }
    }.less);
}
fn obligationRecord(a: std.mem.Allocator, obligation: Record) !Value {
    const o = obligation.data.obligation;
    const interner = obligation.context.interner;
    var status = try m.object(a, &.{try m.member(a, "kind", .{ .string = obligation.status() })});
    if (o.resolution) |resolution| {
        status = try wireedit.set(a, status, "transactionId", .{ .string = interner.idText(.transaction, resolution.transaction) });
        status = try wireedit.set(a, status, "valueNodeId", .{ .string = interner.idText(.node, resolution.value) });
    }
    return m.object(a, &.{ try m.member(a, "obligationId", idValue(obligation)), try m.member(a, "originTransactionId", .{ .string = interner.idText(.transaction, o.origin) }), try m.member(a, "callNodeId", refValue(obligation, "callNodeId")), try m.member(a, "parameterId", refValue(obligation, "parameterId")), try m.member(a, "status", status) });
}
fn dropBindings(a: std.mem.Allocator, node: Record, key: []const u8, target_key: []const u8, id: []const u8) !Record {
    _ = target_key;
    const field = node.payload(key).?;
    const values = switch (field) {
        .args => |v| v,
        .fields => |v| v,
        else => unreachable,
    };
    var remaining: std.ArrayList(tm.Target) = .empty;
    const domain: tm.Domain = if (field == .fields) .field else .parameter;
    for (0..values.len()) |index| {
        const target = values.at(index);
        if (!m.same(model.utf8(node.context.interner.idText(domain, target.target)), id)) try remaining.append(a, target);
    }
    const next = try tm.builder.targets(a, remaining.items);
    return edit.nodePayload(a, node, key, if (field == .fields) .{ .fields = next } else .{ .args = next });
}
fn removeParameter(s: *State, id: []const u8) !void {
    const found = subject(s.root, .parameter, id) orelse return s.fail("TargetNotFound", "The function parameter to remove was not found.");
    const a = s.r.a;
    const owner = found.owner.?;
    const description = try std.fmt.allocPrint(a, "Parameter '{s}' ({s}) of function {s}", .{ query.nameOf(found.item), id, try ownedDescription(s, owner) });
    if (s.changed(.parametersAdded, "parameterId", id)) return s.fail("ParameterCreatedInTransaction", "Remove a newly added parameter in a separate reviewed change.");
    for (model.typedBodyNodes(a, owner)) |node| if (hasKind(node, "parameterReference") and m.same(model.utf8(node.reference("parameterId")), id)) return s.fail("ParameterStillReferenced", try std.fmt.allocPrint(a, "{s} is still referenced by the function body; replace those references before removing it.", .{description}));
    const users = try nonCallUses(s, query.idOf(owner));
    if (users.len > 0) return s.fail("ParameterRemovalNonCallUse", try std.fmt.allocPrint(a, "Function {s} is used other than as a direct call by {s}. Those uses fix its signature, so only direct calls can drop an argument; replace those uses before removing a parameter of this function.", .{ try ownedDescription(s, owner), try std.mem.join(a, "; ", users) }));
    var affected: std.ArrayList(Value) = .empty;
    const all = try nodes(s);
    var effectful: ?Record = null;
    for (all) |site| {
        if (!hasKind(site.node, "directCall") or !m.same(model.utf8(site.node.reference("targetFunctionId")), query.idOf(owner))) continue;
        try affected.append(a, idValue(site.node));
        if (site.function.requiredEffects().len > 0) for (site.node.arguments()) |argument| if (m.same(model.utf8(argument.reference("targetParameterId")), id)) {
            if (effectful == null or idLess(site.function, effectful.?)) effectful = site.function;
        };
    }
    if (effectful) |function| return s.fail("ParameterRemovalMayDiscardEffects", try std.fmt.allocPrint(a, "Function {s} declares effects and binds an argument to {s}; preserve evaluation explicitly before removing the argument binding.", .{ try ownedDescription(s, function), description }));
    sortIdValues(affected.items);
    for (all) |site| if (hasKind(site.node, "directCall") and m.same(model.utf8(site.node.reference("targetFunctionId")), query.idOf(owner))) try updateNode(s, query.idOf(site.node), try dropBindings(a, site.node, "arguments", "targetParameterId", id));
    const current = edit.find(s.root, .functions, query.idOf(owner)).?;
    s.root = try edit.replace(a, s.root, .functions, try reconcile(s, try edit.drop(a, current, .parameters, id)));
    var retired: std.ArrayList(Value) = .empty;
    var kept: std.ArrayList(Record) = .empty;
    for (s.root.obligations()) |obligation| if (m.same(model.utf8(obligation.reference("parameterId")), id)) {
        try retired.append(a, try obligationRecord(a, obligation));
    } else {
        try kept.append(a, obligation);
    };
    sortObligations(retired.items);
    s.root = try edit.withChildren(a, s.root, .obligations, kept.items);
    try s.add(.parametersRemoved, try m.object(a, &.{ try m.member(a, "ownerFunctionId", idValue(owner)), try m.member(a, "parameterId", idValue(found.item)), try m.member(a, "displayName", nameValue(found.item)), try m.member(a, "affectedCallNodeIds", .{ .array = affected.items }), try m.member(a, "retiredObligations", .{ .array = retired.items }) }));
}

fn validate(s: *State, root: Record) !@TypeOf(@as(model.DocumentRead, undefined).validation_issues) {
    return model.validateTyped(s.r.a, s.r.interner.?, root.data.snapshot).validation_issues;
}
fn addCapability(s: *State, p: Prepared) !void {
    const request = p.intent.add_capability_operation;
    const a = s.r.a;
    const operation = p.member.?;
    const owner = edit.find(s.root, .capabilities, request.capability) orelse return s.fail("TargetNotFound", "The target capability was not found.");
    if (try recipe.isBlank(a, request.name)) return s.fail("InvalidDisplayName", "The new operation display name cannot be blank.");
    for (owner.operations()) |existing| if (m.same(query.nameOf(existing), request.name)) return s.fail("DuplicateCapabilityOperationDisplayName", try std.fmt.allocPrint(a, "Capability '{s}' already has an operation named '{s}'; operation display names must be unique within their Capability.", .{ query.nameOf(owner), request.name }));
    const names = try a.alloc([]const u8, request.parameters.len);
    for (request.parameters, names) |parameter, *name| {
        name.* = parameter.name;
        if (try recipe.isBlank(a, name.*)) return s.fail("InvalidDisplayName", "Operation parameter display names cannot be blank.");
    }
    if (preflight.repeated(names) != null) return s.fail("DuplicateCapabilityParameterDisplayName", "Operation parameter display names must be distinct within their operation.");
    if (subject(s.root, .capabilityOperation, query.idOf(operation)) != null) return s.fail("DuplicateCapabilityOperationId", "The new operation or parameter ID already exists.");
    for (operation.parameters()) |parameter| if (subject(s.root, .capabilityParameter, query.idOf(parameter)) != null) return s.fail("DuplicateCapabilityOperationId", "The new operation or parameter ID already exists.");
    if (edit.find(s.root, .effects, request.effect) == null) return s.fail("EffectNotFound", try std.fmt.allocPrint(a, "Effect '{s}' was not found.", .{request.effect}));
    var affected: std.ArrayList(OwnedNode) = .empty;
    for (try nodes(s)) |site| if (hasKind(site.node, "capabilityConstruct") and m.same(model.utf8(site.node.reference("capabilityId")), request.capability)) try affected.append(a, site);
    const planned = try a.alloc([]const u8, request.implementations.len);
    for (request.implementations, planned) |plan, *id| id.* = plan.construct;
    if (!try idsEqual(a, affected.items, planned)) {
        var sites: std.ArrayList([]const u8) = .empty;
        for (affected.items) |site| try sites.append(a, try std.fmt.allocPrint(a, "'{s}' in function {s}", .{ query.idOf(site.node), try ownedDescription(s, site.function) }));
        return s.fail("CapabilityImplementationPlanMismatch", try std.fmt.allocPrint(a, "Every program implementation (capabilityConstruct) of Capability '{s}' requires exactly one implementation plan for the new operation; the constructions are: {s}.", .{ query.nameOf(owner), if (sites.items.len == 0) @as([]const u8, "there are none") else try std.mem.join(a, ", ", sites.items) }));
    }
    s.root = try edit.replace(a, s.root, .capabilities, try edit.append(a, owner, .operations, operation));
    for (affected.items) |site| {
        const plan = for (request.implementations) |plan| {
            if (edit.hasId(site.node, plan.construct)) break plan;
        } else unreachable;
        const implementation: tm.Implementation = .{ .operation = operation.data.operation.id, .function = try intern(s.r, .function, plan.function) };
        const previous = site.node.payload("implementations").?.impls;
        const items = try a.alloc(tm.Implementation, previous.len() + 1);
        for (items[0..previous.len()], 0..) |*slot, index| slot.* = previous.at(index);
        items[previous.len()] = implementation;
        try updateNode(s, plan.construct, try edit.nodePayload(a, site.node, "implementations", .{ .impls = try tm.builder.implementations(a, items) }));
        // Check the newly bound implementation alone, preserving CapabilityImplementations order.
        var isolated = s.*;
        try updateNode(&isolated, plan.construct, try edit.nodePayload(a, site.node, "implementations", .{ .impls = try tm.builder.implementations(a, &.{implementation}) }));
        const issues = try validate(&isolated, isolated.root);
        const prefix = try std.fmt.allocPrint(a, "In function {s}: Capability construction '{s}': ", .{ try ownedDescription(s, site.function), plan.construct });
        for ([_][]const u8{ "UnknownCapabilityImplementationFunction", "GenericCapabilityImplementationUnsupported", "ImpureCapabilityImplementation", "CapabilityImplementationSignatureMismatch" }) |code| {
            for (issues) |issue| if (m.same(issue.code, code)) {
                const message = model.utf8(issue.message);
                if (std.mem.startsWith(u8, message, prefix)) return s.fail(code, try std.fmt.allocPrint(a, "Capability construction '{s}' in function {s}: {s}", .{ plan.construct, try ownedDescription(s, site.function), message[prefix.len..] }));
            };
        }
        try s.add(.capabilityImplementationsBound, try m.object(a, &.{ try m.member(a, "ownerFunctionId", idValue(site.function)), try m.member(a, "constructNodeId", idValue(site.node)), try m.member(a, "operationId", idValue(operation)), try m.member(a, "functionId", try m.string(a, plan.function)) }));
    }
    try s.add(.capabilityOperationsAdded, try m.object(a, &.{ try m.member(a, "ownerCapabilityId", idValue(owner)), try m.member(a, "operation", try wireRecord(a, operation)) }));
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
        if (hasKind(site.node, "fieldAccess") and m.same(model.utf8(site.node.reference("fieldId")), id)) referenced = true;
        if (hasKind(site.node, "productUpdate")) for (site.node.fields()) |binding| if (m.same(model.utf8(binding.reference("targetFieldId")), id)) {
            referenced = true;
        };
        if (hasKind(site.node, "productConstruct") and m.same(model.utf8(site.node.reference("typeId")), query.idOf(owner))) for (site.node.fields()) |binding| if (m.same(model.utf8(binding.reference("targetFieldId")), id)) {
            try affected.append(a, site);
        };
    }
    if (s.changed(.productFieldsAdded, "fieldId", id)) return s.fail("ProductFieldCreatedInTransaction", "Remove a newly added field in a separate reviewed change.");
    if (referenced) return s.fail("ProductFieldStillReferenced", "Repair all field accesses and product updates of this field before removing it.");
    for (affected.items) |site| if (site.function.requiredEffects().len > 0) return s.fail("ProductFieldMayDiscardEffects", "An affected function declares effects; preserve evaluation explicitly before removing its field binding.");
    s.root = try edit.replace(a, s.root, .types, try edit.drop(a, owner, .fields, id));
    var affected_ids: std.ArrayList(Value) = .empty;
    for (affected.items) |site| {
        try updateNode(s, query.idOf(site.node), try dropBindings(a, site.node, "fields", "targetFieldId", id));
        try affected_ids.append(a, idValue(site.node));
    }
    sortIdValues(affected_ids.items);
    try s.add(.productFieldsRemoved, try m.object(a, &.{ try m.member(a, "ownerTypeId", idValue(owner)), try m.member(a, "fieldId", idValue(found.item)), try m.member(a, "displayName", nameValue(found.item)), try m.member(a, "affectedConstructNodeIds", .{ .array = affected_ids.items }) }));
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
fn afterScheduled(s: *State, root: Record, current: ?types.Intent) !Record {
    var result = root;
    for (s.scheduled) |p| if (deletion(p.intent)) |deleted| {
        if (current) |operation| if (deletion(operation)) |skip| if (deleted.target == skip.target and m.same(deleted.id, skip.id)) {
            continue;
        };
        if (deleted.target == .capabilityOperation) {
            if (subject(result, .capabilityOperation, deleted.id)) |found| result = try edit.replace(s.r.a, result, .capabilities, try edit.drop(s.r.a, found.owner.?, .operations, deleted.id));
        } else result = try edit.drop(s.r.a, result, group(deleted.target), deleted.id);
    };
    return result;
}
fn deletionSafe(s: *State, before: Record, candidate: Record, scheduled: bool, current: ?types.Intent, code: []const u8, name: []const u8) !void {
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
            for (model.typedBodyNodes(a, function)) |node| for (model.typedFunctionIdsOf(node)) |reference| {
                if (m.same(model.utf8(reference), selected.id)) used = true;
            };
            if (used) try callers.append(a, try ownedDescription(s, function));
        }
        if (callers.items.len > 0) return s.fail("FunctionStillReferenced", try std.fmt.allocPrint(a, "Function {s} is still referenced by {s}. Repair or remove those direct calls, callbacks and function references before deleting this function.", .{ try ownedDescription(s, item), try std.mem.join(a, ", ", callers.items) }));
        const body = model.typedBodyNodes(a, item);
        var kept: std.ArrayList(Record) = .empty;
        var retired: std.ArrayList(Value) = .empty;
        for (s.root.obligations()) |obligation| {
            var owned = false;
            for (body) |node| if (edit.hasId(node, model.utf8(obligation.reference("callNodeId")))) {
                owned = true;
                break;
            };
            if (!owned) {
                try kept.append(a, obligation);
                continue;
            }
            if (m.same(model.utf8(obligation.status()), "open")) return s.fail("FunctionHasObligations", "Resolve open obligations attached to this function before deletion. Deleting the function retires only its resolved obligations, with their exact records in the reviewed diff.");
            try retired.append(a, try obligationRecord(a, obligation));
        }
        s.root = try edit.drop(a, s.root, .functions, selected.id);
        s.root = try edit.withChildren(a, s.root, .obligations, kept.items);
        sortObligations(retired.items);
        var diff = try m.object(a, &.{ try m.member(a, "functionId", idValue(item)), try m.member(a, "displayName", nameValue(item)), try m.member(a, "removedNodeIds", try strings(a, body)) });
        if (retired.items.len > 0) diff = try wireedit.set(a, diff, "retiredObligations", .{ .array = retired.items });
        try s.add(.functionsRemoved, diff);
        return;
    }
    if (selected.target == .type and (s.changed(.productFieldsAdded, "ownerTypeId", selected.id) or s.changed(.productFieldsRemoved, "ownerTypeId", selected.id) or s.changed(.unionVariantsRemoved, "ownerTypeId", selected.id))) return s.fail("TypeModifiedInTransaction", "Delete a type in a separate reviewed change from its field edits.");
    if (selected.target == .capabilityOperation) for (s.changes[@backingInt(DiffKey.capabilityOperationsAdded)].items) |added| if (wireedit.hasId(model.property(added, "operation").?, selected.id)) return s.fail("CapabilityOperationCreatedInTransaction", "Delete a newly added operation in a separate reviewed change.");
    const candidate = if (selected.target == .capabilityOperation) try edit.replace(a, s.root, .capabilities, try edit.drop(a, found.owner.?, .operations, selected.id)) else try edit.drop(a, s.root, group(selected.target), selected.id);
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
    if (found.owner) |owner| try fields.append(a, try m.member(a, "ownerCapabilityId", idValue(owner)));
    try fields.append(a, try m.member(a, switch (selected.target) {
        .type => "typeId",
        .effect => "effectId",
        .capability => "capabilityId",
        .capabilityOperation => "operationId",
        else => unreachable,
    }, idValue(item)));
    try fields.append(a, try m.member(a, "displayName", nameValue(item)));
    if (selected.target == .type) {
        const variants = item.variants();
        var removed_fields: std.ArrayList(Record) = .empty;
        try removed_fields.appendSlice(a, item.fields());
        for (variants) |variant| try removed_fields.appendSlice(a, variant.fields());
        try fields.append(a, try m.member(a, "removedVariantIds", try strings(a, variants)));
        try fields.append(a, try m.member(a, "removedFieldIds", try strings(a, removed_fields.items)));
    }
    if (selected.target == .capability or selected.target == .capabilityOperation) {
        const operations = if (selected.target == .capability) item.operations() else &.{item};
        var removed_parameters: std.ArrayList(Record) = .empty;
        for (operations) |operation| try removed_parameters.appendSlice(a, operation.parameters());
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

fn derivedBinding(s: *State, match_id: []const u8, field_id: []const u8) !tm.Id {
    const a = s.r.a;
    const transaction = s.transaction;
    const seed = try std.fmt.allocPrint(a, "{s}|{s}|{s}", .{ model.utf8(model.decoded(transaction)), match_id, field_id });
    var digest: [32]u8 = undefined;
    std.crypto.hash.sha2.Sha256.hash(seed, &digest, .{});
    std.mem.swap(u8, &digest[0], &digest[3]);
    std.mem.swap(u8, &digest[1], &digest[2]);
    std.mem.swap(u8, &digest[4], &digest[5]);
    std.mem.swap(u8, &digest[6], &digest[7]);
    const hex = std.fmt.bytesToHex(digest[0..16].*, .lower);
    return intern(s.r, .binding, try std.fmt.allocPrint(a, "{s}-{s}-{s}-{s}-{s}", .{ hex[0..8], hex[8..12], hex[12..16], hex[16..20], hex[20..32] }));
}
fn removeUnion(s: *State, id: []const u8) !void {
    const a = s.r.a;
    const found = subject(s.root, .variant, id) orelse return s.fail("TargetNotFound", "The Union variant to remove was not found.");
    for (s.changes[@backingInt(DiffKey.unionVariantsAdded)].items) |added| if (wireedit.hasId(model.property(added, "variant").?, id)) return s.fail("UnionVariantCreatedInTransaction", "The Union variant to remove was added in this transaction; leave it out of the batch instead.");
    const owner = found.owner.?;
    const description = try std.fmt.allocPrint(a, "Variant '{s}' ({s}) of Union '{s}' ({s})", .{ query.nameOf(found.item), id, query.nameOf(owner), query.idOf(owner) });
    const variants = owner.variants();
    var constructors: std.ArrayList([]const u8) = .empty;
    var matches: std.ArrayList(OwnedNode) = .empty;
    for (try nodes(s)) |site| {
        if (hasKind(site.node, "unionConstruct") and m.same(model.utf8(site.node.reference("variantId")), id) and !preflight.contains(constructors.items, query.idOf(site.function))) try constructors.append(a, query.idOf(site.function));
        if (hasKind(site.node, "matchUnion")) {
            var matching = false;
            for (site.node.arms()) |arm| for (arm.variantIds()) |variant| {
                if (m.same(model.utf8(variant.literal()), id)) matching = true;
            };
            if (matching) try matches.append(a, site);
        }
    }
    if (variants.len == 1) return s.fail("UnionVariantLast", try std.fmt.allocPrint(a, "{s} is the Union's only variant; delete the type instead.", .{description}));
    std.mem.sort([]const u8, constructors.items, {}, query.ordinal);
    if (constructors.items.len > 0) {
        var users: std.ArrayList([]const u8) = .empty;
        for (constructors.items) |function| try users.append(a, try ownedDescription(s, edit.find(s.root, .functions, function).?));
        return s.fail("UnionVariantStillConstructed", try std.fmt.allocPrint(a, "{s} is still constructed by {s}; replace those constructions before removing the variant.", .{ description, try std.mem.join(a, ", ", users.items) }));
    }
    var effectful: ?OwnedNode = null;
    for (matches.items) |site| if (site.function.requiredEffects().len > 0) for (site.node.arms()) |arm| {
        const selected = arm.variantIds();
        if (selected.len == 1 and m.same(model.utf8(selected[0].literal()), id)) {
            if (effectful == null or idLess(site.function, effectful.?.function) or (edit.hasId(site.function, query.idOf(effectful.?.function)) and idLess(site.node, effectful.?.node))) effectful = site;
        }
    };
    if (effectful) |site| return s.fail("UnionVariantRemovalMayDiscardEffects", try std.fmt.allocPrint(a, "Function {s} declares effects and its match '{s}' has an arm for {s} alone; preserve evaluation explicitly before removing that arm.", .{ try ownedDescription(s, site.function), query.idOf(site.node), description }));
    const before = s.root;
    s.root = try edit.replace(a, s.root, .types, try edit.drop(a, owner, .variants, id));
    var affected_ids: std.ArrayList(Value) = .empty;
    for (matches.items) |site| {
        try affected_ids.append(a, idValue(site.node));
        const node = for (try nodes(s)) |current| {
            if (edit.hasId(current.node, query.idOf(site.node))) break current.node;
        } else continue;
        var arms: std.ArrayList(tm.UnionArm) = .empty;
        var iterator = node.payload("arms").?.arms.iterator();
        while (iterator.next()) |arm| {
            var remaining: std.ArrayList(tm.Id) = .empty;
            var contains = false;
            for (arm.variants) |variant| if (m.same(model.utf8(s.r.interner.?.idText(.variant, variant)), id)) {
                contains = true;
            } else {
                try remaining.append(a, variant);
            };
            if (!contains) {
                try arms.append(a, arm);
                continue;
            }
            if (remaining.items.len == 0) continue;
            var narrowed = arm;
            narrowed.variants = remaining.items;
            if (remaining.items.len == 1) {
                const variant = subject(s.root, .variant, model.utf8(s.r.interner.?.idText(.variant, remaining.items[0]))).?.item;
                const fields = variant.fields();
                const bindings = try a.alloc(tm.FieldBinding, fields.len);
                var arguments: []const recipe.TypeRef = &.{};
                if (model.typedCompletionOf(a, before, site.function, site.node.reference("inputNodeId"))) |completion| if (completion == .normal) {
                    const input_type = try infer.typeOf(a, completion.normal);
                    if (input_type == .named) arguments = input_type.named.arguments;
                };
                var instantiator: infer.Infer = .{ .r = s.r, .a = a, .c = undefined, .local = &.{}, .fresh = &.{} };
                for (fields, bindings) |field, *binding| {
                    const field_type = try instantiator.instantiate(owner, arguments, try infer.typeOf(a, field.declaredType()));
                    const pattern = try a.create(tm.Binding);
                    pattern.* = .{ .id = try derivedBinding(s, query.idOf(node), query.idOf(field)), .name = null, .type = try typeValue(s, field_type) };
                    binding.* = .{ .source = field.data.slot.value.id, .binding = pattern };
                }
                narrowed.fields = try tm.builder.fieldBindings(a, bindings);
            }
            try arms.append(a, narrowed);
        }
        try updateNode(s, query.idOf(node), try edit.nodePayload(a, node, "arms", .{ .arms = try tm.builder.unionArms(a, arms.items) }));
    }
    try deletionSafe(s, before, s.root, false, null, "UnionVariantStillReferenced", "Union variant");
    sortIdValues(affected_ids.items);
    try s.add(.unionVariantsRemoved, try m.object(a, &.{ try m.member(a, "ownerTypeId", idValue(owner)), try m.member(a, "variantId", idValue(found.item)), try m.member(a, "displayName", nameValue(found.item)), try m.member(a, "removedFieldIds", try strings(a, found.item.fields())), try m.member(a, "affectedMatchNodeIds", .{ .array = affected_ids.items }) }));
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
    var state: State = .{ .r = r, .root = r.typedModel(), .transaction = transaction, .scheduled = prepared };
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
    state.root = try edit.changed(r.a, state.root, .{ .snapshot = try r.interner.?.id(.snapshot, snapshot.string) });
    state.root = try edit.changed(r.a, state.root, .{ .parent = try intern(r, .snapshot, r.snapshot_id) });
    const issues = try validate(&state, state.root);
    if (issues.len > 0) return rejected(r, "InvalidProposedSnapshot", try std.fmt.allocPrint(r.a, "{s}: {s}", .{ issues[0].code, model.utf8(issues[0].message) }));
    var fields: std.ArrayList(model.Member) = .empty;
    inline for (std.enums.values(DiffKey)) |key| try fields.append(r.a, try m.member(r.a, @tagName(key), .{ .array = state.changes[@backingInt(key)].items }));
    const diff = try @import("operation_review.zig").finalizeDiff(r, state.root, .{ .object = fields.items });
    return .{ .transaction = transaction, .snapshot = snapshot, .candidate = state.root, .diff = diff, .summary_operations = try summaryMetadata(r, intents) };
}
fn shown(r: *query.Request, target: types.Target, id: []const u8) ![]const u8 {
    return if (subject(r.typedModel(), target, id)) |found| std.fmt.allocPrint(r.a, "'{s}'", .{query.nameOf(found.item)}) else r.a.dupe(u8, id);
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
                value = try wireedit.set(a, value, "target", try m.string(a, @tagName(c.target)));
                value = try wireedit.set(a, value, "id", try m.string(a, c.id));
                value = try wireedit.set(a, value, "name", try m.string(a, c.name));
            },
            .set_exported => |c| {
                value = try wireedit.set(a, value, "target", try m.string(a, @tagName(c.target)));
                value = try wireedit.set(a, value, "id", try m.string(a, c.id));
                value = try wireedit.set(a, value, "value", if (c.value) .json_true else .json_false);
            },
            .set_attribute => |c| {
                value = try wireedit.set(a, value, "attribute", try m.string(a, @tagName(c.attribute)));
                value = try wireedit.set(a, value, "id", try m.string(a, c.id));
                value = try wireedit.set(a, value, "value", if (c.value) .json_true else .json_false);
            },
            .add_function_parameter => |c| {
                value = try wireedit.set(a, value, "id", try m.string(a, c.function));
                value = try wireedit.set(a, value, "name", try m.string(a, c.field.name));
            },
            .add_product_field => |c| {
                value = try wireedit.set(a, value, "id", try m.string(a, c.type));
                value = try wireedit.set(a, value, "name", try m.string(a, c.field.name));
            },
            .add_capability_operation => |c| {
                value = try wireedit.set(a, value, "id", try m.string(a, c.capability));
                value = try wireedit.set(a, value, "name", try m.string(a, c.name));
            },
            .add_union_variant => |c| {
                value = try wireedit.set(a, value, "id", try m.string(a, c.type));
                value = try wireedit.set(a, value, "name", try m.string(a, query.nameOf(c.variant)));
            },
            .resolve_missing_argument => |c| value = try wireedit.set(a, value, "id", try m.string(a, c.obligation)),
            .add_required_effect, .remove_required_effect => |c| {
                value = try wireedit.set(a, value, "id", try m.string(a, c.function));
                value = try wireedit.set(a, value, "effect", try m.string(a, c.effect));
            },
            .remove_product_field, .remove_function_parameter, .remove_union_variant, .delete_function, .delete_type, .delete_effect, .delete_capability, .delete_capability_operation => |id| value = try wireedit.set(a, value, "id", try m.string(a, id)),
        }
        item.* = value;
    }
    return .{ .array = items };
}
pub fn summaryOperations(r: *query.Request, metadata: Value) ![]const u8 {
    var parts: std.ArrayList([]const u8) = .empty;
    for (metadata.array) |item| {
        const tag = std.meta.stringToEnum(std.meta.Tag(types.Intent), wireedit.text(item, "tag")).?;
        const id = wireedit.text(item, "id");
        if (tag == .add_union_variant) {
            try parts.append(r.a, try std.fmt.allocPrint(r.a, "addUnionVariant '{s}' to {s}", .{ wireedit.text(item, "name"), try shown(r, .type, id) }));
            continue;
        }
        const intent: types.Intent = switch (tag) {
            .rename => .{ .rename = .{ .target = std.meta.stringToEnum(types.Target, wireedit.text(item, "target")).?, .id = id, .name = wireedit.text(item, "name") } },
            .set_exported => .{ .set_exported = .{ .target = std.meta.stringToEnum(types.Target, wireedit.text(item, "target")).?, .id = id, .value = model.property(item, "value").? == .json_true } },
            .set_attribute => .{ .set_attribute = .{ .attribute = std.meta.stringToEnum(types.Attribute, wireedit.text(item, "attribute")).?, .id = id, .value = model.property(item, "value").? == .json_true } },
            .add_function_parameter => .{ .add_function_parameter = .{ .function = id, .field = .{ .name = wireedit.text(item, "name"), .type = undefined }, .calls = &.{} } },
            .add_product_field => .{ .add_product_field = .{ .type = id, .field = .{ .name = wireedit.text(item, "name"), .type = undefined }, .constructions = &.{} } },
            .add_capability_operation => .{ .add_capability_operation = .{ .capability = id, .name = wireedit.text(item, "name"), .parameters = &.{}, .result = undefined, .effect = "", .may_wait = false, .implementations = &.{} } },
            .add_union_variant => unreachable,
            .resolve_missing_argument => .{ .resolve_missing_argument = .{ .obligation = id, .value = "" } },
            .add_required_effect => .{ .add_required_effect = .{ .function = id, .effect = wireedit.text(item, "effect") } },
            .remove_required_effect => .{ .remove_required_effect = .{ .function = id, .effect = wireedit.text(item, "effect") } },
            .remove_product_field => .{ .remove_product_field = id },
            .remove_function_parameter => .{ .remove_function_parameter = id },
            .remove_union_variant => .{ .remove_union_variant = id },
            .delete_function => .{ .delete_function = id },
            .delete_type => .{ .delete_type = id },
            .delete_effect => .{ .delete_effect = id },
            .delete_capability => .{ .delete_capability = id },
            .delete_capability_operation => .{ .delete_capability_operation = id },
        };
        try parts.append(r.a, try summary(r, &.{intent}));
    }
    return std.mem.join(r.a, "; ", parts.items);
}

test "typed operation transaction renames a declaration and preserves the committed record" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const read = model.readDocument(a, "{\"schemaVersion\":24,\"snapshotId\":\"s\",\"types\":[],\"effects\":[{\"id\":\"e\",\"displayName\":\"Before\"}],\"capabilities\":[],\"functions\":[],\"obligations\":[]}");
    var interner: tm.Interner = .{ .gpa = a };
    const generation = try tm.load(a, &interner, read.root, null);
    var request: query.Request = .{ .a = a, .line = "", .root = .empty, .provenance = null, .generation = &generation, .interner = &interner, .snapshot_id = "s" };
    const result = try simulate(&request, &.{.{ .rename = .{ .target = .effect, .id = "e", .name = "After" } }}, "s");
    try std.testing.expectEqualStrings("Before", query.nameOf(request.typedModel().effects()[0]));
    try std.testing.expectEqualStrings("After", query.nameOf(result.candidate.effects()[0]));
    try std.testing.expect(result.candidate.data.snapshot.effects[0] != generation.effects[0]);
}

test "typed operations add resolve and retire a missing argument without editing the base" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const read = model.readDocument(a,
        \\{"schemaVersion":24,"snapshotId":"s","types":[],"effects":[],"capabilities":[],"obligations":[],"functions":[{"id":"f","displayName":"F","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"nodes":[{"id":"n","kind":"unitLiteral"}],"resultNodeId":"n"}},{"id":"g","displayName":"G","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"nodes":[{"id":"v","kind":"unitLiteral"},{"id":"call","kind":"directCall","targetFunctionId":"f","arguments":[]}],"resultNodeId":"call"}}]}
    );
    var interner: tm.Interner = .{ .gpa = a };
    const generation = try tm.load(a, &interner, read.root, null);
    var request: query.Request = .{ .a = a, .line = "", .root = .empty, .provenance = null, .generation = &generation, .interner = &interner, .snapshot_id = "s" };
    const added = try simulate(&request, &.{.{ .add_function_parameter = .{ .function = "f", .field = .{ .name = "p", .type = .{ .primitive = "unit" } }, .calls = &.{.{ .leave_missing = "call" }} } }}, request.snapshot_id);
    try std.testing.expectEqual(@as(usize, 0), generation.functions[0].parameters.len);
    try std.testing.expectEqual(@as(usize, 1), added.candidate.data.snapshot.functions[0].parameters.len);
    try std.testing.expectEqual(@as(usize, 1), added.candidate.data.snapshot.obligations.len);
    const obligation = added.candidate.data.snapshot.obligations[0];
    request.generation = added.candidate.data.snapshot;
    request.snapshot_id = model.utf8(added.snapshot.string);
    const resolved = try simulate(&request, &.{.{ .resolve_missing_argument = .{ .obligation = model.utf8(interner.idText(.obligation, obligation.id)), .value = "v" } }}, request.snapshot_id);
    try std.testing.expect(resolved.candidate.data.snapshot.obligations[0].resolution != null);
    const call_id = interner.findId(.node, &.{ 'c', 'a', 'l', 'l' }).?;
    const call = resolved.candidate.data.snapshot.functions[1].body.findNode(call_id).?;
    var args = call.payload();
    _ = args.next();
    const target = args.next().?.value.args.at(0);
    try std.testing.expectEqual(obligation.parameter, target.target);
    try std.testing.expectEqual(interner.findId(.node, &.{'v'}).?, target.value);
    request.generation = resolved.candidate.data.snapshot;
    request.snapshot_id = model.utf8(resolved.snapshot.string);
    const removed = try simulate(&request, &.{.{ .remove_function_parameter = model.utf8(interner.idText(.parameter, obligation.parameter)) }}, request.snapshot_id);
    try std.testing.expectEqual(@as(usize, 0), removed.candidate.data.snapshot.functions[0].parameters.len);
    try std.testing.expectEqual(@as(usize, 0), removed.candidate.data.snapshot.obligations.len);
}

test "typed operations bind and remove a product field using existing value nodes" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const read = model.readDocument(a,
        \\{"schemaVersion":24,"snapshotId":"s","types":[{"id":"t","displayName":"T","kind":"product","fields":[]}],"effects":[],"capabilities":[],"obligations":[],"functions":[{"id":"f","displayName":"F","parameters":[],"resultType":{"kind":"named","typeId":"t"},"requiredEffects":[],"body":{"nodes":[{"id":"v","kind":"unitLiteral"},{"id":"construct","kind":"productConstruct","typeId":"t","fields":[]}],"resultNodeId":"construct"}}]}
    );
    var interner: tm.Interner = .{ .gpa = a };
    const generation = try tm.load(a, &interner, read.root, null);
    var request: query.Request = .{ .a = a, .line = "", .root = .empty, .provenance = null, .generation = &generation, .interner = &interner, .snapshot_id = "s" };
    const added = try simulate(&request, &.{.{ .add_product_field = .{ .type = "t", .field = .{ .name = "p", .type = .{ .primitive = "unit" } }, .constructions = &.{.{ .construct = "construct", .value = .{ .existing = "v" } }} } }}, request.snapshot_id);
    try std.testing.expectEqual(@as(usize, 0), generation.types[0].fields.len);
    const field = added.candidate.data.snapshot.types[0].fields[0];
    request.generation = added.candidate.data.snapshot;
    request.snapshot_id = model.utf8(added.snapshot.string);
    const removed = try simulate(&request, &.{.{ .remove_product_field = model.utf8(interner.idText(.field, field.id)) }}, request.snapshot_id);
    try std.testing.expectEqual(@as(usize, 0), removed.candidate.data.snapshot.types[0].fields.len);
}

test "typed operations materialize field recipe plans as native graph nodes" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const read = model.readDocument(a,
        \\{"schemaVersion":24,"snapshotId":"s","types":[{"id":"t","displayName":"T","kind":"product","fields":[]}],"effects":[],"capabilities":[],"obligations":[],"functions":[{"id":"f","displayName":"F","parameters":[],"resultType":{"kind":"named","typeId":"t"},"requiredEffects":[],"body":{"nodes":[{"id":"construct","kind":"productConstruct","typeId":"t","fields":[]}],"resultNodeId":"construct"}}]}
    );
    var interner: tm.Interner = .{ .gpa = a };
    const generation = try tm.load(a, &interner, read.root, null);
    var request: query.Request = .{ .a = a, .line = "", .root = .empty, .provenance = null, .generation = &generation, .interner = &interner, .snapshot_id = "s" };
    const value: recipe.Node = .{ .case = "UnitLiteral", .slots = &.{} };
    const result = try simulate(&request, &.{.{ .add_product_field = .{ .type = "t", .field = .{ .name = "p", .type = .{ .primitive = "unit" } }, .constructions = &.{.{ .construct = "construct", .value = .{ .recipe = &value } }} } }}, "s");
    try std.testing.expectEqual(@as(usize, 2), result.candidate.data.snapshot.functions[0].body.ids.len);
    try std.testing.expectEqual(@as(usize, 1), generation.functions[0].body.ids.len);
}

test "typed operation adds capability operations and binds native implementations" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const read = model.readDocument(a,
        \\{"schemaVersion":24,"snapshotId":"s","types":[],"effects":[{"id":"e","displayName":"E"}],"capabilities":[{"id":"c","displayName":"C","operations":[]}],"obligations":[],"functions":[{"id":"f","displayName":"F","parameters":[],"resultType":{"kind":"capability","capabilityId":"c"},"requiredEffects":[],"body":{"nodes":[{"id":"construct","kind":"capabilityConstruct","capabilityId":"c","implementations":[]}],"resultNodeId":"construct"}},{"id":"impl","displayName":"Impl","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"nodes":[{"id":"n","kind":"unitLiteral"}],"resultNodeId":"n"}}]}
    );
    var interner: tm.Interner = .{ .gpa = a };
    const generation = try tm.load(a, &interner, read.root, null);
    var request: query.Request = .{ .a = a, .line = "", .root = .empty, .provenance = null, .generation = &generation, .interner = &interner, .snapshot_id = "s" };
    const result = try simulate(&request, &.{.{ .add_capability_operation = .{ .capability = "c", .name = "op", .parameters = &.{}, .result = .{ .primitive = "unit" }, .effect = "e", .may_wait = false, .implementations = &.{.{ .construct = "construct", .function = "impl" }} } }}, "s");
    const operation = result.candidate.data.snapshot.capabilities[0].operations[0];
    var payload = result.candidate.data.snapshot.functions[0].body.node(0).payload();
    _ = payload.next();
    const implementation = payload.next().?.value.impls.at(0);
    try std.testing.expectEqual(operation.id, implementation.operation);
    try std.testing.expectEqual(interner.findId(.function, &.{ 'i', 'm', 'p', 'l' }).?, implementation.function);
    try std.testing.expectEqual(@as(usize, 0), generation.capabilities[0].operations.len);
}

test "typed removal narrows shared union arms and derives native field bindings" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const read = model.readDocument(a,
        \\{"schemaVersion":24,"snapshotId":"s","types":[{"id":"t","displayName":"T","kind":"union","variants":[{"id":"a","displayName":"A","fields":[{"id":"af","displayName":"AF","type":{"kind":"primitive","primitive":"unit"}}]},{"id":"b","displayName":"B","fields":[]},{"id":"c","displayName":"C","fields":[]}]}],"effects":[],"capabilities":[],"obligations":[],"functions":[{"id":"f","displayName":"F","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"nodes":[{"id":"construct","kind":"unionConstruct","variantId":"b","fields":[]},{"id":"match","kind":"matchUnion","inputNodeId":"construct","arms":[{"variantIds":["a","c"],"fieldBindings":[],"body":{"nodes":[{"id":"shared","kind":"unitLiteral"}],"resultNodeId":"shared"}},{"variantId":"b","fieldBindings":[],"body":{"nodes":[{"id":"single","kind":"unitLiteral"}],"resultNodeId":"single"}}]}],"resultNodeId":"match"}}]}
    );
    var interner: tm.Interner = .{ .gpa = a };
    const generation = try tm.load(a, &interner, read.root, null);
    var request: query.Request = .{ .a = a, .line = "", .root = .empty, .provenance = null, .generation = &generation, .interner = &interner, .snapshot_id = "s" };
    const result = try simulate(&request, &.{.{ .remove_union_variant = "c" }}, "s");
    const match_id = interner.findId(.node, &.{ 'm', 'a', 't', 'c', 'h' }).?;
    var payload = result.candidate.data.snapshot.functions[0].body.findNode(match_id).?.payload();
    _ = payload.next();
    var arms = payload.next().?.value.arms.iterator();
    const arm = arms.next().?;
    try std.testing.expectEqual(@as(usize, 1), arm.variants.len);
    try std.testing.expectEqual(@as(usize, 1), arm.fields.len());
    try std.testing.expectEqual(interner.findId(.field, &.{ 'a', 'f' }).?, arm.fields.at(0).source);
    try std.testing.expectEqual(@as(usize, 3), generation.types[0].variants.len);
    try std.testing.expectEqual(@as(usize, 2), result.candidate.data.snapshot.types[0].variants.len);
}
