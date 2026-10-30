//! Ordinary WireOperation decoding; recipe-bearing cases delegate to the special decoder.
const std = @import("std");
const query = @import("query.zig");
const recipe = @import("recipe.zig");
const types = @import("operation_types.zig");
const special = @import("operation_special_decode.zig");
const model = @import("model_reader");
const Json = std.json.Value;

fn id(d: *recipe.Decoder, item: Json, key: []const u8) ![]const u8 {
    return d.stableId(key, try d.requiredString(item, key));
}
fn index(d: *recipe.Decoder, path: []const u8, i: usize) ![]const u8 {
    return std.fmt.allocPrint(d.r.a, "{s}/{d}", .{ path, i });
}
fn named(d: *recipe.Decoder, kind: []const u8, name: []const u8) !query.Value {
    return query.exactOne(d.r, kind, try query.readable(d.r, name), try query.visible(d.r, kind));
}
fn member(d: *recipe.Decoder, label: []const u8, parent: query.Value, group: []const u8, name: []const u8) !query.Value {
    const marker = std.mem.indexOf(u8, label, "{s}") orelse unreachable;
    const rendered = try std.mem.concat(d.r.a, u8, &.{ label[0..marker], query.nameOf(parent), label[marker + 3 ..] });
    return query.exactOne(d.r, rendered, try query.readable(d.r, name), model.arrayItems(parent, group));
}
fn productField(d: *recipe.Decoder, owner: query.Value, name: []const u8) !query.Value {
    if (model.property(owner, "variants") != null) return d.r.fail("Type '{s}' is not a product type.", .{query.nameOf(owner)});
    return member(d, "field on {s}", owner, "fields", name);
}
fn unionVariant(d: *recipe.Decoder, owner: query.Value, name: []const u8) !query.Value {
    if (model.property(owner, "variants") == null) return d.r.fail("Type '{s}' is not a union type.", .{query.nameOf(owner)});
    return member(d, "variant on {s}", owner, "variants", name);
}
fn decodeId(d: *recipe.Decoder, item: Json, by_name: bool, exact_key: []const u8, name_key: []const u8, kind: []const u8) ![]const u8 {
    return if (by_name) query.idOf(try named(d, kind, try d.requiredString(item, name_key))) else try id(d, item, exact_key);
}
fn ensure(d: *recipe.Decoder, path: []const u8, item: Json, context: []const u8, allowed: []const []const u8) !void {
    try d.ensureFields(path, item, context, allowed, allowed);
}

pub fn decode(r: *query.Request, by_name: bool) anyerror![]const types.Intent {
    var d: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line) };
    const input = Json{ .object = r.root };
    try d.ensureFields("", input, if (by_name) "simulateOperationsByName request" else "simulateOperations request", &.{ "id", "op", "operations", "structuredReview", "detail", "expectedSnapshotId" }, &.{ "id", "op", "operations" });
    const operations = try d.array(input, "operations");
    var out: std.ArrayList(types.Intent) = .empty;
    for (operations, 0..) |item, i| try out.append(r.a, try decodeItem(&d, item, try index(&d, "/operations", i), by_name));
    return out.items;
}

fn decodeItem(d: *recipe.Decoder, item: Json, path: []const u8, by_name: bool) anyerror!types.Intent {
    const kind = try d.requiredString(item, "kind");
    if (std.mem.eql(u8, kind, "addUnionVariant") or std.mem.eql(u8, kind, "addProductField")) return special.decode(d, item, path, by_name);

    inline for (.{
        .{ "renameFunction", "function", "functionId", "function", "rename-function operation" },
        .{ "renameType", "type", "typeId", "type", "rename-type operation" },
        .{ "renameEffect", "effect", "effectId", "effect", "rename-effect operation" },
        .{ "renameCapability", "capability", "capabilityId", "capability", "rename-capability operation" },
        .{ "renameField", "field", "fieldId", "field", "rename-field operation" },
        .{ "renameVariant", "variant", "variantId", "variant", "rename-variant operation" },
        .{ "renameParameter", "parameter", "parameterId", "parameter", "rename-parameter operation" },
        .{ "renameCapabilityOperation", "capabilityOperation", "operationId", "operation", "rename-capability-operation operation" },
        .{ "renameCapabilityParameter", "capabilityParameter", "parameterId", "parameter", "rename-capability-parameter operation" },
    }) |entry| if (std.mem.eql(u8, kind, entry[0])) return rename(d, item, path, by_name, kind, entry[1], entry[2], entry[3], entry[4]);

    if (std.mem.eql(u8, kind, "setMayWait")) return flag(d, item, path, by_name, "set-may-wait operation", "mayWait", "operationId", "operation", "operation", .mayWait, "capabilityId", "capability", "capability");
    if (std.mem.eql(u8, kind, "setTaskBound")) return flag(d, item, path, by_name, "set-task-bound operation", "taskBound", "capabilityId", "capability", "capability", .taskBound, "", "", "");
    if (std.mem.eql(u8, kind, "setMustNotWait")) return flag(d, item, path, by_name, "set-must-not-wait operation", "mustNotWait", "functionId", "function", "function", .mustNotWait, "", "", "");
    if (std.mem.eql(u8, kind, "setExported")) return visibility(d, item, path, by_name);
    if (std.mem.eql(u8, kind, "addFunctionParameter")) return addFunctionParameter(d, item, path, by_name);
    if (std.mem.eql(u8, kind, "addCapabilityOperation")) return addCapabilityOperation(d, item, path, by_name);
    if (std.mem.eql(u8, kind, "removeProductField")) return removeMember(d, item, path, by_name, "remove-product-field operation", "typeId", "type", "type", "fieldId", "field", "fields", "field on {s}", .remove_product_field);
    if (std.mem.eql(u8, kind, "removeFunctionParameter")) return removeMember(d, item, path, by_name, "remove-function-parameter operation", "functionId", "function", "function", "parameterId", "parameter", "parameters", "parameter on {s}", .remove_function_parameter);
    if (std.mem.eql(u8, kind, "removeUnionVariant")) return removeMember(d, item, path, by_name, "remove-union-variant operation", "typeId", "type", "type", "variantId", "variant", "variants", "variant on {s}", .remove_union_variant);
    if (std.mem.eql(u8, kind, "deleteFunction")) return deletion(d, item, path, by_name, "delete-function operation", "functionId", "function", "function", .delete_function);
    if (std.mem.eql(u8, kind, "deleteType")) return deletion(d, item, path, by_name, "delete-type operation", "typeId", "type", "type", .delete_type);
    if (std.mem.eql(u8, kind, "deleteEffect")) return deletion(d, item, path, by_name, "delete-effect operation", "effectId", "effect", "effect", .delete_effect);
    if (std.mem.eql(u8, kind, "deleteCapability")) return deletion(d, item, path, by_name, "delete-capability operation", "capabilityId", "capability", "capability", .delete_capability);
    if (std.mem.eql(u8, kind, "deleteCapabilityOperation")) return capabilityOperationDelete(d, item, path, by_name);
    if (std.mem.eql(u8, kind, "resolveMissingArgument")) {
        try ensure(d, path, item, "resolve-missing-argument operation", &.{ "kind", "obligationId", "valueNodeId" });
        return .{ .resolve_missing_argument = .{ .obligation = try id(d, item, "obligationId"), .value = try id(d, item, "valueNodeId") } };
    }
    if (std.mem.eql(u8, kind, "addRequiredEffect") or std.mem.eql(u8, kind, "removeRequiredEffect")) {
        const by_keys: []const []const u8 = if (by_name) &.{ "kind", "function", "effect" } else &.{ "kind", "functionId", "effectId" };
        try ensure(d, path, item, if (std.mem.eql(u8, kind, "addRequiredEffect")) "addRequiredEffect operation" else "removeRequiredEffect operation", by_keys);
        const function = try decodeId(d, item, by_name, "functionId", "function", "function");
        const effect = try decodeId(d, item, by_name, "effectId", "effect", "effect");
        return if (std.mem.eql(u8, kind, "addRequiredEffect")) .{ .add_required_effect = .{ .function = function, .effect = effect } } else .{ .remove_required_effect = .{ .function = function, .effect = effect } };
    }
    return unsupported(d, kind, by_name);
}

fn unsupported(d: *recipe.Decoder, kind: []const u8, by_name: bool) anyerror {
    if (by_name) return d.r.fail("Unsupported name-resolved operation kind '{s}'. simulateOperationsByName accepts the rename operations (renameFunction, renameType, renameField, renameVariant, renameParameter, renameEffect, renameCapability, renameCapabilityOperation, renameCapabilityParameter), setExported, setMayWait, setTaskBound, setMustNotWait, addFunctionParameter, addProductField, addCapabilityOperation, addUnionVariant, addRequiredEffect, removeRequiredEffect, resolveMissingArgument, removeProductField, removeFunctionParameter, removeUnionVariant, deleteFunction, deleteType, deleteEffect, deleteCapability and deleteCapabilityOperation, each naming its targets by display name.", .{kind});
    return d.r.fail("Unsupported operation kind '{s}'.", .{kind});
}

fn rename(d: *recipe.Decoder, item: Json, path: []const u8, by_name: bool, kind: []const u8, target_kind_name: []const u8, exact_key: []const u8, name_key: []const u8, context: []const u8) !types.Intent {
    const target_kind: types.Target = if (std.mem.eql(u8, target_kind_name, "function")) .function else if (std.mem.eql(u8, target_kind_name, "type")) .type else if (std.mem.eql(u8, target_kind_name, "effect")) .effect else if (std.mem.eql(u8, target_kind_name, "capability")) .capability else if (std.mem.eql(u8, target_kind_name, "field")) .field else if (std.mem.eql(u8, target_kind_name, "variant")) .variant else if (std.mem.eql(u8, target_kind_name, "parameter")) .parameter else if (std.mem.eql(u8, target_kind_name, "capabilityOperation")) .capabilityOperation else .capabilityParameter;
    var keys: []const []const u8 = undefined;
    if (!by_name) keys = &.{ "kind", exact_key, "displayName" } else if (std.mem.eql(u8, kind, "renameField")) keys = &.{ "kind", "type", "variant", "field", "displayName" } else if (std.mem.eql(u8, kind, "renameVariant")) keys = &.{ "kind", "displayName", "type", "variant" } else if (std.mem.eql(u8, kind, "renameParameter")) keys = &.{ "kind", "displayName", "function", "parameter" } else if (std.mem.eql(u8, kind, "renameCapabilityOperation")) keys = &.{ "kind", "displayName", "capability", "operation" } else if (std.mem.eql(u8, kind, "renameCapabilityParameter")) keys = &.{ "kind", "displayName", "capability", "operation", "parameter" } else keys = &.{ "kind", "displayName", name_key };
    if (by_name and target_kind == .field) {
        try d.ensureFields(path, item, context, keys, &.{ "kind", "type", "field", "displayName" });
    } else try ensure(d, path, item, context, keys);
    var resolved: []const u8 = undefined;
    if (!by_name) resolved = try id(d, item, exact_key) else if (target_kind == .function or target_kind == .type or target_kind == .effect or target_kind == .capability) resolved = query.idOf(try named(d, target_kind_name, try d.requiredString(item, name_key))) else if (target_kind == .field) {
        const owner = try named(d, "type", try d.requiredString(item, "type"));
        if (try optional(d, item, "variant")) |variant_name| {
            const variant = try unionVariant(d, owner, variant_name);
            resolved = query.idOf(try member(d, "field on variant {s}", variant, "fields", try d.requiredString(item, "field")));
        } else resolved = query.idOf(try productField(d, owner, try d.requiredString(item, "field")));
    } else if (target_kind == .variant) resolved = query.idOf(try unionVariant(d, try named(d, "type", try d.requiredString(item, "type")), try d.requiredString(item, "variant"))) else if (target_kind == .parameter) resolved = query.idOf(try member(d, "parameter on {s}", try named(d, "function", try d.requiredString(item, "function")), "parameters", try d.requiredString(item, "parameter"))) else {
        const cap = try named(d, "capability", try d.requiredString(item, "capability"));
        const op = try member(d, "operation on {s}", cap, "operations", try d.requiredString(item, "operation"));
        resolved = if (target_kind == .capabilityOperation) query.idOf(op) else query.idOf(try query.exactOne(d.r, try std.fmt.allocPrint(d.r.a, "parameter on operation {s}", .{query.nameOf(op)}), try d.requiredString(item, "parameter"), model.arrayItems(op, "parameters")));
    }
    return .{ .rename = .{ .target = target_kind, .id = resolved, .name = try d.requiredString(item, "displayName") } };
}
fn optional(d: *recipe.Decoder, item: Json, key: []const u8) !?[]const u8 {
    const value = item.object.get(key) orelse return null;
    if (value != .string) return d.r.fail("Property '{s}' must be a JSON string.", .{key});
    return try query.readable(d.r, value.string);
}
fn flag(d: *recipe.Decoder, item: Json, path: []const u8, by_name: bool, context: []const u8, value_key: []const u8, exact_key: []const u8, name_key: []const u8, target_kind: []const u8, attribute: types.Attribute, owner_id_key: []const u8, owner_name_key: []const u8, owner_kind: []const u8) !types.Intent {
    var keys: []const []const u8 = undefined;
    if (owner_kind.len > 0 and by_name) keys = &.{ "kind", value_key, owner_name_key, name_key } else keys = if (by_name) &.{ "kind", value_key, name_key } else &.{ "kind", value_key, exact_key };
    try ensure(d, path, item, context, keys);
    const resolved = if (std.mem.eql(u8, target_kind, "operation")) blk: {
        if (by_name) {
            const cap = try named(d, "capability", try d.requiredString(item, owner_name_key));
            break :blk query.idOf(try member(d, "operation on {s}", cap, "operations", try d.requiredString(item, name_key)));
        }
        break :blk try id(d, item, exact_key);
    } else try decodeId(d, item, by_name, if (owner_kind.len > 0) owner_id_key else exact_key, if (owner_kind.len > 0) owner_name_key else name_key, if (owner_kind.len > 0) owner_kind else target_kind);
    const value = try d.property(item, value_key);
    if (value != .bool) return d.r.fail("Property '{s}' must be a JSON boolean.", .{value_key});
    return .{ .set_attribute = .{ .attribute = attribute, .id = resolved, .value = value.bool } };
}
fn visibility(d: *recipe.Decoder, item: Json, path: []const u8, by_name: bool) !types.Intent {
    const keys: []const []const u8 = if (by_name) &.{ "kind", "exported", "type", "function", "effect", "capability" } else &.{ "kind", "exported", "typeId", "functionId", "effectId", "capabilityId" };
    try d.ensureFields(path, item, "set-exported operation", keys, &.{ "kind", "exported" });
    const names = if (by_name) [_][]const u8{ "type", "function", "effect", "capability" } else [_][]const u8{ "typeId", "functionId", "effectId", "capabilityId" };
    var selected: ?[]const u8 = null;
    for (names) |key| if (item.object.get(key) != null) {
        if (selected != null) return d.r.fail("A set-exported operation names exactly one declaration with one of {s}.", .{try std.mem.join(d.r.a, ", ", &names)});
        selected = key;
    };
    const key = selected orelse return d.r.fail("A set-exported operation names exactly one declaration with one of {s}.", .{try std.mem.join(d.r.a, ", ", &names)});
    const target_kind: types.Target = if (std.mem.eql(u8, key, names[0])) .type else if (std.mem.eql(u8, key, names[1])) .function else if (std.mem.eql(u8, key, names[2])) .effect else .capability;
    const target_name = switch (target_kind) { .type => "type", .function => "function", .effect => "effect", .capability => "capability", else => unreachable };
    const selector = try d.requiredString(item, key);
    var result: types.Visibility = .{ .target = target_kind, .id = if (by_name) query.idOf(try named(d, target_name, selector)) else try d.stableId(key, selector), .value = undefined };
    const exported = try d.property(item, "exported");
    if (exported != .bool) return d.r.fail("Property 'exported' must be a JSON boolean.", .{});
    result.value = exported.bool;
    return .{ .set_exported = result };
}
fn deletion(d: *recipe.Decoder, item: Json, path: []const u8, by_name: bool, context: []const u8, exact_key: []const u8, name_key: []const u8, kind: []const u8, comptime tag: std.meta.FieldEnum(types.Intent)) !types.Intent {
    const keys: []const []const u8 = if (by_name) &.{ "kind", name_key } else &.{ "kind", exact_key };
    try ensure(d, path, item, context, keys);
    const value = try decodeId(d, item, by_name, exact_key, name_key, kind);
    return @unionInit(types.Intent, @tagName(tag), value);
}
fn removeMember(d: *recipe.Decoder, item: Json, path: []const u8, by_name: bool, context: []const u8, owner_id_key: []const u8, owner_key: []const u8, owner_kind: []const u8, exact_key: []const u8, name_key: []const u8, group: []const u8, label: []const u8, comptime tag: std.meta.FieldEnum(types.Intent)) !types.Intent {
    const keys: []const []const u8 = if (by_name) &.{ "kind", owner_key, name_key } else &.{ "kind", exact_key };
    try ensure(d, path, item, context, keys);
    const value = if (!by_name) try id(d, item, exact_key) else blk: {
        const owner = try named(d, owner_kind, try d.requiredString(item, owner_key));
        const resolved = if (std.mem.eql(u8, group, "fields")) try productField(d, owner, try d.requiredString(item, name_key)) else if (std.mem.eql(u8, group, "variants")) try unionVariant(d, owner, try d.requiredString(item, name_key)) else try member(d, label, owner, group, try d.requiredString(item, name_key));
        break :blk query.idOf(resolved);
    };
    _ = owner_id_key;
    return @unionInit(types.Intent, @tagName(tag), value);
}
fn capabilityOperationDelete(d: *recipe.Decoder, item: Json, path: []const u8, by_name: bool) !types.Intent {
    const keys: []const []const u8 = if (by_name) &.{ "kind", "capability", "operation" } else &.{ "kind", "operationId" };
    try ensure(d, path, item, "delete-capability-operation operation", keys);
    const value = if (by_name) blk: {
        const cap = try named(d, "capability", try d.requiredString(item, "capability"));
        break :blk query.idOf(try member(d, "operation on {s}", cap, "operations", try d.requiredString(item, "operation")));
    } else try id(d, item, "operationId");
    return .{ .delete_capability_operation = value };
}
fn addFunctionParameter(d: *recipe.Decoder, item: Json, path: []const u8, by_name: bool) !types.Intent {
    const keys: []const []const u8 = if (by_name) &.{ "kind", "function", "displayName", "type", "callSites" } else &.{ "kind", "functionId", "displayName", "type", "callSites" };
    try ensure(d, path, item, "add-function-parameter operation", keys);
    const function = try decodeId(d, item, by_name, "functionId", "function", "function");
    const display_name = try d.requiredString(item, "displayName");
    const old_owner = d.owner;
    defer d.owner = old_owner;
    var function_value: ?query.Value = null;
    if (by_name) {
        function_value = try named(d, "function", try d.requiredString(item, "function"));
        d.owner = .{ .name = "" };
    }
    const field_type = try d.anyType(try std.fmt.allocPrint(d.r.a, "{s}/type", .{path}), try d.property(item, "type"));
    d.owner = old_owner;
    const bound_type = if (function_value) |fn_value| try bindDeclaredTypeParameters(d.r.a, fn_value, field_type) else field_type;
    const plan_values = try d.array(item, "callSites");
    var plans: std.ArrayList(types.CallPlan) = .empty;
    for (plan_values, 0..) |plan, i| {
        const p = try index(d, try std.fmt.allocPrint(d.r.a, "{s}/callSites", .{path}), i);
        const plan_kind = try d.requiredString(plan, "kind");
        if (std.mem.eql(u8, plan_kind, "bind")) {
            try d.ensureFields(p, plan, "bind call-site plan", &.{ "kind", "callNodeId", "valueNodeId" }, &.{ "kind", "callNodeId", "valueNodeId" });
            try plans.append(d.r.a, .{ .bind = .{ .call = try id(d, plan, "callNodeId"), .value = try id(d, plan, "valueNodeId") } });
        } else if (std.mem.eql(u8, plan_kind, "leaveMissing")) {
            try d.ensureFields(p, plan, "leave-missing call-site plan", &.{ "kind", "callNodeId" }, &.{ "kind", "callNodeId" });
            try plans.append(d.r.a, .{ .leave_missing = try id(d, plan, "callNodeId") });
        } else return d.r.fail("Unsupported call-site plan '{s}'.", .{plan_kind});
    }
    return .{ .add_function_parameter = .{ .function = function, .field = .{ .name = display_name, .type = bound_type }, .calls = plans.items } };
}

fn bindDeclaredTypeParameters(a: std.mem.Allocator, function: query.Value, value: recipe.TypeRef) anyerror!recipe.TypeRef {
    return switch (value) {
        .type_parameter => |name| blk: {
            for (model.arrayItems(function, "typeParameters")) |parameter| if (std.mem.eql(u8, query.nameOf(parameter), name)) {
                break :blk .{ .type_parameter = model.utf8(model.decoded(model.property(parameter, "typeParameterId").?)) };
            };
            break :blk value;
        },
        .named => |named_type| .{ .named = .{ .id = named_type.id, .arguments = try bindTypes(a, function, named_type.arguments) } },
        .builtin => |builtin| .{ .builtin = .{ .generic = builtin.generic, .arguments = try bindTypes(a, function, builtin.arguments) } },
        .function => |function_type| blk: {
            const result = try a.create(recipe.TypeRef);
            result.* = try bindDeclaredTypeParameters(a, function, function_type.result.*);
            break :blk .{ .function = .{ .parameters = try bindTypes(a, function, function_type.parameters), .result = result } };
        },
        else => value,
    };
}
fn bindTypes(a: std.mem.Allocator, function: query.Value, values: []const recipe.TypeRef) anyerror![]const recipe.TypeRef {
    const result = try a.alloc(recipe.TypeRef, values.len);
    for (values, result) |value, *bound| bound.* = try bindDeclaredTypeParameters(a, function, value);
    return result;
}
fn addCapabilityOperation(d: *recipe.Decoder, item: Json, path: []const u8, by_name: bool) !types.Intent {
    const keys: []const []const u8 = if (by_name) &.{ "kind", "capability", "displayName", "parameters", "resultType", "failureType", "effect", "implementations", "mayWait" } else &.{ "kind", "capabilityId", "displayName", "parameters", "resultType", "failureType", "effectId", "implementations", "mayWait" };
    const required: []const []const u8 = if (by_name) &.{ "kind", "capability", "displayName", "parameters", "resultType", "effect", "implementations" } else &.{ "kind", "capabilityId", "displayName", "parameters", "resultType", "effectId", "implementations" };
    try d.ensureFields(path, item, "add-capability-operation operation", keys, required);
    const cap = try decodeId(d, item, by_name, "capabilityId", "capability", "capability");
    const display_name = try d.requiredString(item, "displayName");
    const old_owner = d.owner;
    defer d.owner = old_owner;
    if (by_name) d.owner = .{ .name = "" };
    var parameters: std.ArrayList(types.Field) = .empty;
    const params = try d.array(item, "parameters");
    for (params, 0..) |param, i| {
        const p = try index(d, try std.fmt.allocPrint(d.r.a, "{s}/parameters", .{path}), i);
        try d.ensureFields(p, param, "capability parameter", &.{ "displayName", "type" }, &.{ "displayName", "type" });
        try parameters.append(d.r.a, .{ .name = try d.requiredString(param, "displayName"), .type = try d.anyType(try std.fmt.allocPrint(d.r.a, "{s}/type", .{p}), try d.property(param, "type")) });
    }
    const result = try d.anyType(try std.fmt.allocPrint(d.r.a, "{s}/resultType", .{path}), try d.property(item, "resultType"));
    if (item.object.get("failureType")) |failure_type| if (failure_type != .null)
        return d.r.fail("failureType is retired; declare the operation's resultType as Result<T, F> to model an expected failure.", .{});
    const effect = try decodeId(d, item, by_name, "effectId", "effect", "effect");
    try d.optionalBool(item, "mayWait");
    const may_wait = if (item.object.get("mayWait")) |v| v.bool else false;
    const impls = try d.array(item, "implementations");
    var implementations: std.ArrayList(types.Implementation) = .empty;
    for (impls, 0..) |plan, i| {
        const p = try index(d, try std.fmt.allocPrint(d.r.a, "{s}/implementations", .{path}), i);
        const function_key: []const u8 = if (by_name) "function" else "functionId";
        try d.ensureFields(p, plan, "capability implementation plan", &.{ "constructNodeId", function_key }, &.{ "constructNodeId", function_key });
        const construct = try id(d, plan, "constructNodeId");
        const implementation = if (by_name) query.idOf(try d.selector(try std.fmt.allocPrint(d.r.a, "{s}/function", .{p}), try d.property(plan, "function"), "function")) else try id(d, plan, "functionId");
        try implementations.append(d.r.a, .{ .construct = construct, .function = implementation });
    }
    return .{ .add_capability_operation = .{ .capability = cap, .name = display_name, .parameters = parameters.items, .result = result, .effect = effect, .may_wait = may_wait, .implementations = implementations.items } };
}
