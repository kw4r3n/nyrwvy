//! WireOperation's two recipe-bearing operations. Decode against the original declarations;
//! only a new union variant's recipe resolver sees that variant appended to its owner.
const std = @import("std");
const model = @import("model_reader");
const query = @import("query.zig");
const recipe = @import("recipe.zig");
const types = @import("operation_types.zig");
const edit = @import("typed_edit.zig");
const m = @import("materialize.zig");
const Json = std.json.Value;
const Value = model.Model;
const tm = model.session_model;

fn at(d: *recipe.Decoder, path: []const u8, key: []const u8) ![]const u8 {
    return std.fmt.allocPrint(d.r.a, "{s}/{s}", .{ path, key });
}
fn index(d: *recipe.Decoder, path: []const u8, i: usize) ![]const u8 {
    return std.fmt.allocPrint(d.r.a, "{s}/{d}", .{ path, i });
}
fn id(d: *recipe.Decoder, item: Json, key: []const u8) ![]const u8 {
    return d.stableId(key, try d.requiredString(item, key));
}

/// Retrieve a nested recipe's original spelling, preserving name-rewrite and duplicate gates.
fn rawAt(d: *recipe.Decoder, path: []const u8) ![]const u8 {
    var raw = d.r.line;
    var keys = std.mem.tokenizeScalar(u8, path, '/');
    while (keys.next()) |key| {
        const trimmed = std.mem.trimStart(u8, raw, " \t\r\n");
        if (trimmed.len > 0 and trimmed[0] == '[') {
            const items = try @import("recipe_file.zig").rawArrayItems(d.r.a, raw);
            raw = items[try std.fmt.parseInt(usize, key, 10)];
        } else raw = try recipe.rawProperty(d.r.a, raw, key);
    }
    return raw;
}

fn owningFunction(d: *recipe.Decoder, node_id: []const u8) !Value {
    for (try query.typedVisible(d.r, "function")) |function| {
        if (containsNode(function, node_id)) return function;
    }
    return d.r.fail("Unknown node '{s}'; plans name an existing node by its exact ID.", .{node_id});
}
fn containsNode(function: Value, node_id: []const u8) bool {
    for (model.typedBodyNodes(function.context.allocator, function)) |node| if (edit.hasId(node, node_id)) return true;
    return false;
}
fn planRecipe(d: *recipe.Decoder, path: []const u8, input: Json, node_id: []const u8, by_name: bool, fields: ?[]const Value) !*const recipe.Node {
    var body = input;
    var local = d.*;
    local.new_fields = fields;
    if (by_name) {
        const owner = try owningFunction(&local, node_id);
        const parameters = owner.parameters();
        const names = try d.r.a.alloc([]const u8, parameters.len);
        for (parameters, names) |parameter, *name| name.* = query.nameOf(parameter);
        local.owner = .{ .name = query.nameOf(owner), .parameters = names, .existing = owner };
    } else local.owner = null;
    try recipe.resolveNames(&local, path, &body, try rawAt(&local, path));
    const result = try local.recipe(path, body);
    return if (by_name) recipe.bindExistingTypes(&local, result) else result;
}

fn bindUnionType(d: *recipe.Decoder, owner: Value, t: recipe.TypeRef) anyerror!recipe.TypeRef {
    return switch (t) {
        .type_parameter => |name| blk: {
            for (owner.typeParameters()) |parameter| {
                if (m.same(query.nameOf(parameter), name)) break :blk .{ .type_parameter = model.utf8(parameter.id()) };
            }
            return d.r.fail("Union '{s}' declares no type parameter named '{s}'; a new variant's field types may use only the Union's own type parameters.", .{ query.nameOf(owner), name });
        },
        .named => |n| .{ .named = .{ .id = n.id, .arguments = try bindUnionTypes(d, owner, n.arguments) } },
        .builtin => |b| .{ .builtin = .{ .generic = b.generic, .arguments = try bindUnionTypes(d, owner, b.arguments) } },
        .function => |f| blk: {
            const result = try d.r.a.create(recipe.TypeRef);
            const parameters = try bindUnionTypes(d, owner, f.parameters);
            result.* = try bindUnionType(d, owner, f.result.*);
            break :blk .{ .function = .{ .parameters = parameters, .result = result } };
        },
        else => t,
    };
}
fn bindUnionTypes(d: *recipe.Decoder, owner: Value, ts: []const recipe.TypeRef) ![]const recipe.TypeRef {
    const result = try d.r.a.alloc(recipe.TypeRef, ts.len);
    for (ts, result) |t, *bound| bound.* = try bindUnionType(d, owner, t);
    return result;
}

pub fn decode(d: *recipe.Decoder, item: Json, path: []const u8, by_name: bool) anyerror!types.Intent {
    const kind = try d.requiredString(item, "kind");
    if (m.same(kind, "addUnionVariant")) return unionVariant(d, item, path, by_name);
    return productField(d, item, path, by_name);
}

fn productField(d: *recipe.Decoder, item: Json, path: []const u8, by_name: bool) !types.Intent {
    const keys: []const []const u8 = if (by_name) &.{ "kind", "type", "displayName", "fieldType", "constructions" } else &.{ "kind", "typeId", "displayName", "type", "constructions" };
    try d.ensureFields(path, item, "add-product-field operation", keys, keys);
    const owner = if (by_name) query.idOf(try query.typedExactOne(d.r, "type", try d.requiredString(item, "type"), try query.typedVisible(d.r, "type"))) else try id(d, item, "typeId");
    const name = try d.requiredString(item, "displayName");
    var local = d.*;
    local.owner = if (by_name) .{ .name = "" } else null;
    const type_key: []const u8 = if (by_name) "fieldType" else "type";
    const field_type = try local.anyType(try at(d, path, type_key), try d.property(item, type_key));
    var plans: std.ArrayList(types.ValuePlan) = .empty;
    for (try d.array(item, "constructions"), 0..) |plan, i| {
        const p = try index(d, try at(d, path, "constructions"), i);
        try d.ensureFields(p, plan, "product construction plan", &.{ "constructNodeId", "valueNodeId", "value" }, &.{"constructNodeId"});
        const construct = try id(d, plan, "constructNodeId");
        const value = plan.object.get("value");
        const existing = plan.object.get("valueNodeId");
        const result: types.ValuePlan = if (existing != null and value == null) .{ .construct = construct, .value = .{ .existing = try id(d, plan, "valueNodeId") } } else if (existing == null and value != null) .{ .construct = construct, .value = .{ .recipe = try planRecipe(d, try at(d, p, "value"), value.?, construct, by_name, null) } } else return d.r.fail("A product construction plan takes exactly one of valueNodeId or value.", .{});
        try plans.append(d.r.a, result);
    }
    return .{ .add_product_field = .{ .type = owner, .field = .{ .name = name, .type = field_type }, .constructions = plans.items } };
}

fn unionVariant(d: *recipe.Decoder, item: Json, path: []const u8, by_name: bool) !types.Intent {
    const keys: []const []const u8 = if (by_name) &.{ "kind", "type", "displayName", "fields", "arms" } else &.{ "kind", "typeId", "displayName", "fields", "arms" };
    try d.ensureFields(path, item, "add-union-variant operation", keys, keys);
    const owner = if (by_name) try d.selector(try at(d, path, "type"), try d.property(item, "type"), "type") else blk: {
        const type_id = try id(d, item, "typeId");
        for (try query.typedVisible(d.r, "type")) |declared| if (edit.hasId(declared, type_id)) break :blk declared;
        return d.r.fail("Unknown type '{s}'.", .{type_id});
    };
    if (owner.data.type_declaration.product) return d.r.fail("Type '{s}' is a Product; addUnionVariant targets a Union.", .{query.nameOf(owner)});
    var local = d.*;
    local.owner = if (by_name) .{ .name = "" } else null;
    var fields: std.ArrayList(types.Field) = .empty;
    for (try d.array(item, "fields"), 0..) |field, i| {
        const p = try index(d, try at(d, path, "fields"), i);
        try d.ensureFields(p, field, "variant field", &.{ "displayName", "type" }, &.{ "displayName", "type" });
        const name = try d.requiredString(field, "displayName");
        const field_type = try local.anyType(try at(d, p, "type"), try d.property(field, "type"));
        try fields.append(d.r.a, .{ .name = name, .type = try bindUnionType(d, owner, field_type) });
    }
    const name = try d.requiredString(item, "displayName");
    var materializer: m.Materializer = .{ .a = d.r.a, .r = d.r, .types = undefined, .fresh = &.{}, .function_id = tm.none, .parameters = &.{} };
    const variant_id = try materializer.freshId(.variant);
    const allocated_fields = try d.r.a.alloc(tm.Typed, fields.items.len);
    for (fields.items, allocated_fields) |field, *allocated| {
        const field_id = try materializer.freshId(.field);
        allocated.* = .{ .id = field_id, .name = try materializer.internString(field.name), .type = try materializer.typeRef(field.type) };
    }
    const variant_record = try d.r.a.create(tm.Variant);
    variant_record.* = .{ .id = variant_id, .name = try materializer.internString(name), .fields = allocated_fields };
    const variant = edit.borrow(owner, .{ .variant = variant_record });
    var request = d.r.*;
    if (by_name) {
        const extended = try edit.replace(d.r.a, request.typedModel(), .types, try edit.append(d.r.a, owner, .variants, variant));
        request.generation = extended.data.snapshot;
    }
    local.r = &request;
    var arms: std.ArrayList(types.ArmRecipe) = .empty;
    for (try d.array(item, "arms"), 0..) |arm, i| {
        const p = try index(d, try at(d, path, "arms"), i);
        try d.ensureFields(p, arm, "union match arm plan", &.{ "matchNodeId", "body" }, &.{ "matchNodeId", "body" });
        const match_id = try id(d, arm, "matchNodeId");
        const body = planRecipe(&local, try at(d, p, "body"), try d.property(arm, "body"), match_id, by_name, if (by_name) null else variant.fields()) catch |err| {
            d.r.rejection = request.rejection;
            return err;
        };
        try arms.append(d.r.a, .{ .match = match_id, .body = body });
    }
    return .{ .add_union_variant = .{ .type = query.idOf(owner), .variant = variant, .arms = arms.items } };
}
