//! Net semantic changes and review membership, compared against the immutable simulation base.
const std = @import("std");
const model = @import("model_reader");
const query = @import("query.zig");
const m = @import("materialize.zig");
const edit = @import("operation_model.zig");
const engine = @import("operation_engine.zig");
const Value = model.Value;
const Entry = struct { kind: []const u8, item: Value, owner: ?Value = null, variant: ?Value = null, operation: ?Value = null, siblings: []const Value };
fn entries(a: std.mem.Allocator, root: Value) ![]Entry {
    var list: std.ArrayList(Entry) = .empty;
    for ([_][2][]const u8{ .{ "types", "type" }, .{ "functions", "function" }, .{ "effects", "effect" }, .{ "capabilities", "capability" } }) |category| {
        const items = model.arrayItems(root, category[0]);
        for (items) |owner| {
            try list.append(a, .{ .kind = category[1], .item = owner, .siblings = items });
            for ([_][2][]const u8{ .{ "fields", "field" }, .{ "variants", "variant" }, .{ "parameters", "parameter" }, .{ "operations", "capabilityOperation" } }) |member| {
                const children = model.arrayItems(owner, member[0]);
                for (children) |item| {
                    try list.append(a, .{ .kind = member[1], .item = item, .owner = owner, .siblings = children });
                    const nested_key: []const u8 = if (m.same(member[0], "variants")) "fields" else if (m.same(member[0], "operations")) "parameters" else continue;
                    const nested = model.arrayItems(item, nested_key);
                    for (nested) |child| try list.append(a, .{ .kind = if (m.same(nested_key, "fields")) "field" else "capabilityParameter", .item = child, .owner = owner, .variant = if (m.same(nested_key, "fields")) item else null, .operation = if (m.same(nested_key, "parameters")) item else null, .siblings = nested });
                }
            }
        }
    }
    return list.items;
}
fn oldEntry(before: []const Entry, entry: Entry) ?Entry {
    for (before) |old| if (m.same(old.kind, entry.kind) and m.same(query.idOf(old.item), query.idOf(entry.item))) return old;
    return null;
}
fn stringField(a: std.mem.Allocator, key: []const u8, value: []const u8) !model.Member {
    return m.member(a, key, try m.string(a, value));
}
fn compareField(x: Value, y: Value, key: []const u8) std.math.Order {
    const xv = model.property(x, key) orelse return .eq;
    const yv = model.property(y, key) orelse return .eq;
    return std.mem.order(u16, model.decoded(xv), model.decoded(yv));
}
fn renameLess(_: void, x: Value, y: Value) bool {
    const kind = compareField(x, y, "kind");
    return if (kind == .eq) compareField(x, y, "id") == .lt else kind == .lt;
}
fn duplicate(r: *query.Request, entry: Entry) !void {
    for (entry.siblings) |sibling| {
        if (m.same(query.idOf(sibling), query.idOf(entry.item)) or !std.mem.eql(u16, model.decoded(model.property(sibling, "displayName").?), model.decoded(model.property(entry.item, "displayName").?))) continue;
        const kind: []const u8 = if (m.same(entry.kind, "capabilityOperation")) "operation" else if (m.same(entry.kind, "capabilityParameter")) "parameter" else entry.kind;
        var scope: []const u8 = "";
        var rule: []const u8 = "";
        if (entry.owner) |owner| {
            if (m.same(kind, "field")) {
                scope = if (entry.variant) |variant| try std.fmt.allocPrint(r.a, "of variant '{s}' of Union '{s}' ", .{ query.nameOf(variant), query.nameOf(owner) }) else try std.fmt.allocPrint(r.a, "of Product '{s}' ", .{query.nameOf(owner)});
                rule = " within their Product or Union variant";
            } else if (m.same(kind, "variant")) {
                scope = try std.fmt.allocPrint(r.a, "of Union '{s}' ", .{query.nameOf(owner)});
                rule = " within their Union";
            } else if (m.same(kind, "operation")) {
                scope = try std.fmt.allocPrint(r.a, "of Capability '{s}' ", .{query.nameOf(owner)});
                rule = " within their Capability";
            } else if (entry.operation) |operation| {
                scope = try std.fmt.allocPrint(r.a, "of operation '{s}' ", .{query.nameOf(operation)});
                rule = " within their operation";
            } else {
                scope = try std.fmt.allocPrint(r.a, "of function '{s}' ", .{query.nameOf(owner)});
                rule = " within their function";
            }
        }
        return engine.rejected(r, "DuplicateDisplayName", try std.fmt.allocPrint(r.a, "Another {s} {s}is already named '{s}'; {s} display names must be unique{s}.", .{ kind, scope, query.nameOf(entry.item), kind, rule }));
    }
}
pub fn finalizeDiff(r: *query.Request, candidate: Value, accumulated: Value) anyerror!Value {
    const a = r.a;
    const before = try entries(a, r.model);
    const after = try entries(a, candidate);
    var renames: std.ArrayList(Value) = .empty;
    var visibility: std.ArrayList(Value) = .empty;
    var attributes: std.ArrayList(Value) = .empty;
    for (after) |entry| {
        const old = oldEntry(before, entry) orelse continue;
        if (!std.mem.eql(u16, model.decoded(model.property(old.item, "displayName").?), model.decoded(model.property(entry.item, "displayName").?))) {
            var fields: std.ArrayList(model.Member) = .empty;
            try fields.appendSlice(a, &.{ try stringField(a, "kind", entry.kind), try m.member(a, "id", model.property(entry.item, "id").?) });
            if (entry.owner) |owner| try fields.append(a, try m.member(a, if (m.same(entry.kind, "field") or m.same(entry.kind, "variant")) "ownerTypeId" else if (m.same(entry.kind, "parameter")) "ownerFunctionId" else "ownerCapabilityId", model.property(owner, "id").?));
            if (entry.variant) |variant| try fields.append(a, try m.member(a, "ownerVariantId", model.property(variant, "id").?));
            if (entry.operation) |operation| try fields.append(a, try m.member(a, "ownerOperationId", model.property(operation, "id").?));
            try fields.appendSlice(a, &.{ try m.member(a, "oldDisplayName", model.property(old.item, "displayName").?), try m.member(a, "newDisplayName", model.property(entry.item, "displayName").?) });
            try renames.append(a, .{ .object = fields.items });
        }
        if (entry.owner == null and (model.property(old.item, "exported") != null) != (model.property(entry.item, "exported") != null)) try visibility.append(a, try m.object(a, &.{ try stringField(a, "kind", entry.kind), try m.member(a, "id", model.property(entry.item, "id").?), try m.member(a, "displayName", model.property(entry.item, "displayName").?), try m.member(a, "exported", if (model.property(entry.item, "exported") != null) .json_true else .json_false) }));
        const attribute: []const u8 = if (m.same(entry.kind, "function")) "mustNotWait" else if (m.same(entry.kind, "capability")) "taskBound" else if (m.same(entry.kind, "capabilityOperation")) "mayWait" else continue;
        if ((model.property(old.item, attribute) != null) != (model.property(entry.item, attribute) != null)) try attributes.append(a, try m.object(a, &.{ try stringField(a, "attribute", attribute), try m.member(a, "id", model.property(entry.item, "id").?), try stringField(a, "displayName", if (entry.owner) |owner| try std.fmt.allocPrint(a, "{s}.{s}", .{ query.nameOf(owner), query.nameOf(entry.item) }) else query.nameOf(entry.item)), try m.member(a, "value", if (model.property(entry.item, attribute) != null) .json_true else .json_false) }));
    }
    std.mem.sort(Value, renames.items, {}, renameLess);
    for (renames.items) |rename| for (after) |entry| {
        if (m.same(edit.text(rename, "kind"), entry.kind) and m.same(edit.text(rename, "id"), query.idOf(entry.item))) try duplicate(r, entry);
    };
    var fields: std.ArrayList(model.Member) = .empty;
    var has_changes = false;
    for (accumulated.object, 0..) |field, index| {
        const key = model.utf8(field.name);
        var values = if (m.same(key, "renames")) renames.items else if (m.same(key, "visibilityChanges")) visibility.items else if (m.same(key, "attributeChanges")) attributes.items else try a.dupe(Value, field.value.array);
        var kept: std.ArrayList(Value) = .empty;
        for (values) |value| {
            const owner_key: ?[]const u8 = if (m.same(key, "parametersAdded") or m.same(key, "productFieldsBound") or m.same(key, "capabilityImplementationsBound") or m.same(key, "unionMatchArmsAdded")) "ownerFunctionId" else if (m.same(key, "capabilityOperationsAdded")) "ownerCapabilityId" else if (m.same(key, "unionVariantsAdded")) "ownerTypeId" else null;
            if (owner_key) |owner| if (edit.find(candidate, if (m.same(owner, "ownerFunctionId")) "functions" else if (m.same(owner, "ownerTypeId")) "types" else "capabilities", edit.text(value, owner)) == null) continue;
            if (m.same(key, "requiredEffectsChanged")) {
                const was = hasEffect(r.model, edit.text(value, "functionId"), edit.text(value, "effectId"));
                const now = hasEffect(candidate, edit.text(value, "functionId"), edit.text(value, "effectId"));
                if (if (m.same(edit.text(value, "kind"), "added")) was or !now else !was or now) continue;
                var seen = false;
                for (kept.items) |prior| if (m.same(edit.text(prior, "functionId"), edit.text(value, "functionId")) and m.same(edit.text(prior, "effectId"), edit.text(value, "effectId")) and m.same(edit.text(prior, "kind"), edit.text(value, "kind"))) {
                    seen = true;
                    break;
                };
                if (seen) continue;
            }
            var encoded = value;
            if (m.same(key, "capabilityOperationsAdded")) {
                var source: std.Io.Writer.Allocating = .init(a);
                try @import("wire.zig").capabilityOperation(&source.writer, model.property(value, "operation").?);
                encoded = try edit.set(a, value, "operation", try model.parseValue(a, source.written()));
            }
            if (m.same(key, "unionVariantsAdded")) {
                const variant = model.property(value, "variant").?;
                const members = try a.dupe(Value, model.arrayItems(variant, "fields"));
                std.mem.sort(Value, members, {}, struct {
                    fn less(_: void, x: Value, y: Value) bool {
                        return compareField(x, y, "id") == .lt;
                    }
                }.less);
                encoded = try edit.set(a, value, "variant", try edit.set(a, variant, "fields", .{ .array = members }));
            }
            try kept.append(a, encoded);
        }
        values = kept.items;
        if (m.same(key, "productFieldsBound")) {
            // F# prepends each operation's bindings; equal construct keys retain that order.
            std.mem.reverse(Value, values);
            std.mem.sort(Value, values, key, changeLess);
        } else std.mem.sort(Value, values, key, changeLess);
        has_changes = has_changes or values.len > 0;
        if (index >= 14 and values.len == 0) continue;
        try fields.append(a, try m.member(a, key, .{ .array = values }));
    }
    if (!has_changes) return engine.rejected(r, "NoChange", "The transaction has no net semantic change.");
    return .{ .object = fields.items };
}
fn hasEffect(root: Value, function: []const u8, effect: []const u8) bool {
    const item = edit.find(root, "functions", function) orelse return false;
    for (model.arrayItems(item, "requiredEffects")) |id| if (m.same(model.utf8(model.decoded(id)), effect)) return true;
    return false;
}
fn changeLess(key: []const u8, x: Value, y: Value) bool {
    if (m.same(key, "renames") or m.same(key, "visibilityChanges")) return renameLess({}, x, y);
    const keys: []const []const u8 = if (m.same(key, "attributeChanges")) &.{ "attribute", "id" } else if (m.same(key, "requiredEffectsChanged")) &.{ "functionId", "effectId", "kind" } else if (m.same(key, "parametersAdded") or m.same(key, "parametersRemoved")) &.{ "ownerFunctionId", "parameterId" } else if (m.same(key, "productFieldsAdded") or m.same(key, "productFieldsRemoved")) &.{ "ownerTypeId", "fieldId" } else if (m.same(key, "capabilityImplementationsBound")) &.{ "ownerFunctionId", "constructNodeId", "operationId" } else if (m.same(key, "productFieldsBound")) &.{ "ownerFunctionId", "constructNodeId" } else if (m.same(key, "unionMatchArmsAdded")) &.{ "ownerFunctionId", "matchNodeId", "variantId" } else if (m.same(key, "argumentsAttached")) &.{ "callNodeId", "parameterId" } else if (m.same(key, "obligationsCreated") or m.same(key, "obligationsResolved")) &.{"obligationId"} else if (m.same(key, "unionVariantsRemoved")) &.{ "ownerTypeId", "variantId" } else if (m.same(key, "functionsRemoved")) &.{"functionId"} else if (m.same(key, "typesRemoved")) &.{"typeId"} else if (m.same(key, "effectsRemoved")) &.{"effectId"} else if (m.same(key, "capabilitiesRemoved")) &.{"capabilityId"} else if (m.same(key, "capabilityOperationsRemoved")) &.{"operationId"} else &.{};
    if (m.same(key, "capabilityOperationsAdded") or m.same(key, "unionVariantsAdded")) {
        const owner = compareField(x, y, if (m.same(key, "capabilityOperationsAdded")) "ownerCapabilityId" else "ownerTypeId");
        if (owner != .eq) return owner == .lt;
        return compareField(model.property(x, if (m.same(key, "capabilityOperationsAdded")) "operation" else "variant").?, model.property(y, if (m.same(key, "capabilityOperationsAdded")) "operation" else "variant").?, "id") == .lt;
    }
    for (keys) |field| {
        const order = compareField(x, y, field);
        if (order != .eq) return order == .lt;
    }
    return false;
}
