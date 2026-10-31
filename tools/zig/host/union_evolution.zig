//! Add a Union variant by copying source fields and repairing every nested match atomically.
const std = @import("std");
const model = @import("model_reader");
const query = @import("query.zig");
const recipe = @import("recipe.zig");
const preflight = @import("preflight.zig");
const infer = @import("infer.zig");
const m = @import("materialize.zig");
const edit = @import("operation_model.zig");
const migration = @import("migration.zig");
const proposals = @import("proposals.zig");
const Value = model.Value;
const Field = struct { name: []const u8, type: recipe.TypeRef };
const Ids = std.StringHashMapUnmanaged(Value);

fn reject(r: *query.Request, suffix: []const u8, message: []const u8) anyerror {
    return preflight.rejected(r, try std.mem.concat(r.a, u8, &.{ "UnionVariantEvolution", suffix }), message);
}
fn typeValue(r: *query.Request, t: recipe.TypeRef) !Value {
    var materializer: m.Materializer = .{ .a = r.a, .r = r, .types = undefined, .fresh = &.{}, .function_id = .json_null, .parameters = &.{} };
    return materializer.typeRef(t);
}
pub fn simulateRequest(r: *query.Request, out: *std.Io.Writer) anyerror!void {
    const op = r.root.get("op").?.string;
    const symbolic = std.mem.endsWith(u8, op, "ByName");
    const allowed: []const []const u8 = if (symbolic) &.{ "id", "op", "type", "sourceVariant", "displayName", "additionalFields", "structuredReview", "detail", "expectedSnapshotId" } else &.{ "id", "op", "typeId", "sourceVariantId", "displayName", "additionalFields", "structuredReview", "detail", "expectedSnapshotId" };
    // Additional field types resolve declaration names without a function generic context.
    var d: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line), .owner = if (symbolic) .{ .name = "" } else null };
    const root: std.json.Value = .{ .object = r.root };
    try d.ensureFields("", root, try std.fmt.allocPrint(r.a, "{s} request", .{op}), allowed, allowed[0..5]);
    var owner_type: Value = .json_null;
    var source: Value = .json_null;
    if (symbolic) {
        owner_type = try d.selector("/type", try d.property(root, "type"), "type");
        if (model.property(owner_type, "variants") == null) return r.fail("Type '{s}' is not a union type.", .{query.nameOf(owner_type)});
        source = try d.member("variant on {s}", .{query.nameOf(owner_type)}, owner_type, "variants", try d.requiredString(root, "sourceVariant"));
    }
    const wanted = if (r.root.get("expectedSnapshotId") != null) try d.stableId("expectedSnapshotId", try d.requiredString(root, "expectedSnapshotId")) else r.snapshot_id;
    const type_id = if (symbolic) query.idOf(owner_type) else try d.stableId("typeId", try d.requiredString(root, "typeId"));
    const source_id = if (symbolic) query.idOf(source) else try d.stableId("sourceVariantId", try d.requiredString(root, "sourceVariantId"));
    const name = try d.requiredString(root, "displayName");
    var fields: std.ArrayList(Field) = .empty;
    if (root.object.get("additionalFields") != null) for (try d.array(root, "additionalFields"), 0..) |field, i| {
        const path = try std.fmt.allocPrint(r.a, "/additionalFields/{d}", .{i});
        try d.ensureFields(path, field, "type field", &.{ "displayName", "type" }, &.{ "displayName", "type" });
        try fields.append(r.a, .{ .name = try d.requiredString(field, "displayName"), .type = try d.anyType(try std.fmt.allocPrint(r.a, "{s}/type", .{path}), try d.property(field, "type")) });
    };
    try r.ensureUsable();
    try proposals.publishMigration(r, out, try simulate(r, wanted, type_id, source_id, name, fields.items));
}

// Node and binding IDs are different domains; declaration references and literal strings stay intact.
fn remap(r: *query.Request, value: Value, key: []const u8, nodes: *const Ids, patterns: *const Ids) anyerror!Value {
    switch (value) {
        .string => {
            const replacements = if (m.same(key, "patternBindingId")) patterns else if (std.mem.endsWith(u8, key, "NodeId") or std.mem.endsWith(u8, key, "NodeIds") or m.same(key, "arguments")) nodes else return value;
            return replacements.get(model.utf8(model.decoded(value))) orelse value;
        },
        .array => |items| {
            const copied = try r.a.alloc(Value, items.len);
            for (items, copied) |item, *next| next.* = try remap(r, item, key, nodes, patterns);
            return .{ .array = copied };
        },
        .object => |fields| {
            const copied = try r.a.dupe(model.Member, fields);
            for (copied) |*field| {
                const name = model.utf8(field.name);
                if (m.same(name, "id")) {
                    const replacements = if (model.property(value, "kind") != null) nodes else patterns;
                    field.value = replacements.get(model.utf8(model.decoded(field.value))) orelse field.value;
                } else field.value = try remap(r, field.value, name, nodes, patterns);
            }
            return .{ .object = copied };
        },
        else => return value,
    }
}
fn clone(r: *query.Request, outer: []const Value, graph: Value) !struct { graph: Value, ids: Ids } {
    var nodes: Ids = .empty;
    var patterns: Ids = .empty;
    const wrapper = try m.object(r.a, &.{try m.member(r.a, "body", graph)});
    for (model.bodyNodes(r.a, wrapper)) |node| {
        const node_id = query.idOf(node);
        if (!nodes.contains(node_id)) try nodes.put(r.a, node_id, try m.id(r.a));
    }
    for (outer) |binding| if (!patterns.contains(query.idOf(binding))) try patterns.put(r.a, query.idOf(binding), try m.id(r.a));
    for (model.patternBindings(r.a, graph)) |binding| if (!patterns.contains(query.idOf(binding))) try patterns.put(r.a, query.idOf(binding), try m.id(r.a));
    return .{ .graph = try remap(r, graph, "", &nodes, &patterns), .ids = patterns };
}
const RepairContext = struct {
    r: *query.Request,
    function: Value,
    owner: Value,
    source_id: []const u8,
    variant: Value,
    field_ids: *const Ids,
    extra_fields: []const Value,
    count: usize = 0,

    fn repair(self: *RepairContext, node: Value) anyerror!Value {
        if (!m.same(edit.text(node, "kind"), "matchUnion")) return node;
        const arms = model.arrayItems(node, "arms");
        for (arms, 0..) |source, index| {
            const variants = try edit.variantIds(self.r.a, source);
            var found = false;
            for (variants) |variant| if (m.same(model.utf8(model.decoded(variant)), self.source_id)) { found = true; break; };
            if (!found) continue;
            self.count += 1;
            if (variants.len > 1) {
                const joined = try self.r.a.dupe(Value, arms);
                joined[index] = try edit.set(self.r.a, try edit.remove(self.r.a, source, "variantId"), "variantIds", .{ .array = try std.mem.concat(self.r.a, Value, &.{ variants, &.{model.property(self.variant, "id").?} }) });
                return edit.set(self.r.a, node, "arms", .{ .array = joined });
            }
            const source_fields = model.arrayItems(source, "fieldBindings");
            const outer = try self.r.a.alloc(Value, source_fields.len);
            for (source_fields, outer) |field, *binding| binding.* = model.property(field, "binding").?;
            const cloned = try clone(self.r, outer, model.property(source, "body").?);
            var bindings: std.ArrayList(Value) = .empty;
            for (source_fields) |field| {
                var binding = model.property(field, "binding").?;
                binding = try edit.set(self.r.a, binding, "id", cloned.ids.get(query.idOf(binding)).?);
                try bindings.append(self.r.a, try m.object(self.r.a, &.{ try m.member(self.r.a, "sourceFieldId", self.field_ids.get(edit.text(field, "sourceFieldId")).?), try m.member(self.r.a, "binding", binding) }));
            }
            for (self.extra_fields) |field| {
                var field_type = try infer.typeOf(self.r.a, model.property(field, "type").?);
                if (model.completionOf(self.r.a, self.r.model, self.function, model.decoded(model.property(node, "inputNodeId").?))) |completion| if (completion == .normal) {
                    const input_type = try infer.typeOf(self.r.a, completion.normal);
                    if (input_type == .named) {
                        var renderer: infer.Infer = .{ .r = self.r, .a = self.r.a, .c = undefined, .local = &.{}, .fresh = &.{} };
                        field_type = try renderer.instantiate(self.owner, input_type.named.arguments, field_type);
                    }
                };
                const binding = try m.object(self.r.a, &.{ try m.member(self.r.a, "id", try m.id(self.r.a)), try m.member(self.r.a, "displayName", model.property(field, "displayName").?), try m.member(self.r.a, "type", try typeValue(self.r, field_type)) });
                try bindings.append(self.r.a, try m.object(self.r.a, &.{ try m.member(self.r.a, "sourceFieldId", model.property(field, "id").?), try m.member(self.r.a, "binding", binding) }));
            }
            const added_arm = try m.object(self.r.a, &.{ try m.member(self.r.a, "variantId", model.property(self.variant, "id").?), try m.member(self.r.a, "fieldBindings", .{ .array = bindings.items }), try m.member(self.r.a, "body", cloned.graph) });
            return edit.append(self.r.a, node, "arms", added_arm);
        }
        return node;
    }
};
fn simulate(r: *query.Request, wanted: []const u8, type_id: []const u8, source_id: []const u8, name: []const u8, additional: []const Field) anyerror!migration.Result {
    if (!m.same(wanted, r.snapshot_id)) return reject(r, "ExpectedBaseMismatch", "The Union-variant evolution request targets a different base snapshot.");
    if (try recipe.isBlank(r.a, name)) return reject(r, "DisplayNameBlank", "The new Union variant display name must not be blank.");
    const owner_type = edit.find(r.model, "types", type_id) orelse return reject(r, "TypeNotFound", "The target type does not exist.");
    if (model.property(owner_type, "variants") == null) return reject(r, "TargetNotUnion", "The target type must be a Union.");
    const source = edit.find(owner_type, "variants", source_id) orelse return reject(r, "SourceVariantNotFound", "The source variant does not belong to the target Union.");
    var names: std.ArrayList([]const u8) = .empty;
    for (model.arrayItems(source, "fields")) |field| try names.append(r.a, query.nameOf(field));
    for (additional) |field| try names.append(r.a, field.name);
    var invalid = preflight.repeated(names.items) != null;
    for (names.items) |field_name| if (try recipe.isBlank(r.a, field_name)) { invalid = true; break; };
    if (invalid) return reject(r, "FieldDisplayNameInvalid", "New variant field display names must be non-blank and distinct, including copied source fields.");
    for (model.arrayItems(owner_type, "variants")) |variant| if (m.same(query.nameOf(variant), name)) return reject(r, "DuplicateVariantDisplayName", "The Union already has a variant with this display name.");
    var copied: std.ArrayList(Value) = .empty;
    var field_ids: Ids = .empty;
    for (model.arrayItems(source, "fields")) |field| {
        const new_id = try m.id(r.a);
        try field_ids.put(r.a, query.idOf(field), new_id);
        try copied.append(r.a, try edit.set(r.a, field, "id", new_id));
    }
    var extra: std.ArrayList(Value) = .empty;
    for (additional) |field| try extra.append(r.a, try m.object(r.a, &.{ try m.member(r.a, "id", try m.id(r.a)), try m.member(r.a, "displayName", try m.string(r.a, field.name)), try m.member(r.a, "type", try typeValue(r, field.type)) }));
    const variant = try m.object(r.a, &.{ try m.member(r.a, "id", try m.id(r.a)), try m.member(r.a, "displayName", try m.string(r.a, name)), try m.member(r.a, "fields", .{ .array = try std.mem.concat(r.a, Value, &.{ copied.items, extra.items }) }) });
    const updated_type = try edit.append(r.a, owner_type, "variants", variant);
    var candidate = try edit.replace(r.a, r.model, "types", updated_type);
    var rewritten: std.ArrayList(Value) = .empty;
    var ids: std.ArrayList(Value) = .empty;
    var count: usize = 0;
    for (model.arrayItems(r.model, "functions")) |f| {
        var context: RepairContext = .{ .r = r, .function = f, .owner = owner_type, .source_id = source_id, .variant = variant, .field_ids = &field_ids, .extra_fields = extra.items };
        const graph = try edit.mapNodes(r.a, model.property(f, "body").?, &context, RepairContext.repair);
        if (context.count > 0) {
            const updated = try edit.set(r.a, f, "body", graph);
            candidate = try edit.replace(r.a, candidate, "functions", updated);
            try rewritten.append(r.a, updated);
            try ids.append(r.a, model.property(f, "id").?);
            count += context.count;
        }
    }
    const transaction = try m.id(r.a);
    const snapshot = try m.id(r.a);
    candidate = try edit.set(r.a, try edit.set(r.a, candidate, "snapshotId", snapshot), "parentSnapshotId", try m.string(r.a, r.snapshot_id));
    try migration.validate(r, candidate);
    var type_text: std.Io.Writer.Allocating = .init(r.a);
    const canonical = model.canonicalRoot(r.a, candidate);
    try @import("wire.zig").semanticType(&type_text.writer, edit.find(canonical, "types", type_id).?);
    const fields = try m.object(r.a, &.{ try m.member(r.a, "updatedType", try model.parseValue(r.a, type_text.written())), try m.member(r.a, "addedVariantId", model.property(variant, "id").?), try m.member(r.a, "addedVariantDisplayName", model.property(variant, "displayName").?), try m.member(r.a, "repairedFunctionIds", .{ .array = ids.items }), try m.member(r.a, "repairedMatchCount", try m.number(r.a, count)) });
    return .{ .kind = "unionVariantEvolution", .transaction = transaction, .snapshot = snapshot, .candidate = candidate, .diff = .json_null, .rewritten = rewritten.items, .summary = try std.fmt.allocPrint(r.a, "addUnionVariantLike '{s}' on '{s}' ({d} matches repaired)", .{ name, query.nameOf(owner_type), count }), .review_fields = fields, .touched_types = try r.a.dupe([]const u8, &.{type_id}) };
}
