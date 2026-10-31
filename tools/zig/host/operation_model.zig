//! Immutable request-local edits. Unchanged Value slices identify records/bodies to share when
//! the final candidate becomes a typed generation; no edit writes into the committed tree.
const std = @import("std");
const model = @import("model_reader");
const m = @import("materialize.zig");
const Value = model.Value;
const Allocator = std.mem.Allocator;

pub fn text(value: Value, key: []const u8) []const u8 {
    return model.utf8(model.decoded(model.property(value, key).?));
}
pub fn hasId(value: Value, id: []const u8) bool {
    return m.same(text(value, "id"), id);
}
pub fn find(root: Value, group: []const u8, id: []const u8) ?Value {
    for (model.arrayItems(root, group)) |item| if (hasId(item, id)) return item;
    return null;
}
pub fn set(a: Allocator, value: Value, key: []const u8, replacement: Value) !Value {
    var fields: std.ArrayList(model.Member) = .empty;
    var found = false;
    for (model.objectMembers(value)) |field| {
        if (m.same(model.utf8(field.name), key)) {
            try fields.append(a, .{ .name = field.name, .value = replacement });
            found = true;
        } else try fields.append(a, field);
    }
    if (!found) try fields.append(a, try m.member(a, key, replacement));
    return .{ .object = fields.items };
}
pub fn remove(a: Allocator, value: Value, key: []const u8) !Value {
    if (model.property(value, key) == null) return value;
    var fields: std.ArrayList(model.Member) = .empty;
    for (model.objectMembers(value)) |field| if (!m.same(model.utf8(field.name), key)) try fields.append(a, field);
    return .{ .object = fields.items };
}
pub fn flag(a: Allocator, value: Value, key: []const u8, enabled: bool) !Value {
    // Snapshot flags are presence markers, not JSON false values.
    return if (enabled) set(a, value, key, .json_true) else remove(a, value, key);
}
pub fn append(a: Allocator, value: Value, key: []const u8, item: Value) !Value {
    return set(a, value, key, .{ .array = try std.mem.concat(a, Value, &.{ model.arrayItems(value, key), &.{item} }) });
}
pub fn replace(a: Allocator, root: Value, group: []const u8, item: Value) !Value {
    const items = try a.dupe(Value, model.arrayItems(root, group));
    for (items) |*current| if (m.same(text(current.*, "id"), text(item, "id"))) {
        current.* = item;
        return set(a, root, group, .{ .array = items });
    };
    return error.MissingOperationTarget;
}
pub fn drop(a: Allocator, root: Value, group: []const u8, id: []const u8) !Value {
    var items: std.ArrayList(Value) = .empty;
    for (model.arrayItems(root, group)) |item| if (!hasId(item, id)) try items.append(a, item);
    return set(a, root, group, .{ .array = items.items });
}

/// Changes only the branch that contains the exact node ID, including nested/shared arm graphs.
/// The callback sees the original node after its nested graphs have been visited.
pub fn mapNodes(a: Allocator, value: Value, context: anytype, comptime transform: anytype) anyerror!Value {
    switch (value) {
        .object, .typed => {
            const fields = model.objectMembers(value);
            var changed: ?[]model.Member = null;
            for (fields, 0..) |field, i| {
                const next = try mapNodes(a, field.value, context, transform);
                if (!identical(field.value, next)) {
                    if (changed == null) changed = try a.dupe(model.Member, fields);
                    changed.?[i].value = next;
                }
            }
            const next: Value = if (changed) |items| .{ .object = items } else value;
            if (model.property(next, "id") != null and model.property(next, "kind") != null) return transform(context, next);
            return next;
        },
        .array => |items| {
            var changed: ?[]Value = null;
            for (items, 0..) |item, i| {
                const next = try mapNodes(a, item, context, transform);
                if (!identical(item, next)) {
                    if (changed == null) changed = try a.dupe(Value, items);
                    changed.?[i] = next;
                }
            }
            return if (changed) |values| .{ .array = values } else value;
        },
        else => return value,
    }
}
pub fn identical(x: Value, y: Value) bool {
    return switch (x) {
        .typed => |reference| y == .typed and std.meta.eql(reference, y.typed),
        .object => |xs| y == .object and xs.ptr == y.object.ptr and xs.len == y.object.len,
        .array => |xs| y == .array and xs.ptr == y.array.ptr and xs.len == y.array.len,
        .string => |xs| y == .string and xs.ptr == y.string.ptr and xs.len == y.string.len,
        .number => |xs| y == .number and xs.ptr == y.number.ptr and xs.len == y.number.len,
        .json_null => y == .json_null,
        .json_true => y == .json_true,
        .json_false => y == .json_false,
    };
}

test "path-copy edits retain untouched records and nested graphs" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const root = try model.parseValue(a,
        \\{"functions":[{"id":"f","body":{"nodes":[{"id":"n","kind":"conditional","whenTrue":{"nodes":[{"id":"nested","kind":"unitLiteral"}],"resultNodeId":"nested"},"whenFalse":{"nodes":[{"id":"other","kind":"unitLiteral"}],"resultNodeId":"other"}}],"resultNodeId":"n"}},{"id":"untouched","body":{"nodes":[],"resultNodeId":"unused"}}]}
    );
    const Context = struct {
        a: Allocator,
        fn change(c: @This(), node: Value) !Value {
            return if (hasId(node, "nested")) set(c.a, node, "newProperty", .json_true) else node;
        }
    };
    const result = try mapNodes(a, root, Context{ .a = a }, Context.change);
    const before = model.arrayItems(root, "functions");
    const after = model.arrayItems(result, "functions");
    try std.testing.expect(identical(before[1], after[1]));
    const old_node = model.arrayItems(model.property(before[0], "body").?, "nodes")[0];
    const new_node = model.arrayItems(model.property(after[0], "body").?, "nodes")[0];
    try std.testing.expect(identical(model.property(old_node, "whenFalse").?, model.property(new_node, "whenFalse").?));
    try std.testing.expect(model.property(model.arrayItems(model.property(old_node, "whenTrue").?, "nodes")[0], "newProperty") == null);
    try std.testing.expect(model.property(model.arrayItems(model.property(new_node, "whenTrue").?, "nodes")[0], "newProperty") != null);
}

test "path-copy edits typed records without changing their source" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const read = model.readDocument(a, "{\"schemaVersion\":24,\"snapshotId\":\"s\",\"types\":[],\"effects\":[],\"capabilities\":[],\"functions\":[{\"id\":\"f\",\"displayName\":\"F\",\"parameters\":[],\"resultType\":{\"kind\":\"primitive\",\"primitive\":\"unit\"},\"requiredEffects\":[],\"body\":{\"nodes\":[{\"id\":\"n\",\"kind\":\"unitLiteral\"}],\"resultNodeId\":\"n\"}}],\"obligations\":[]}");
    const root = try model.ownTyped(a, read.root, false);
    const Context = struct {
        a: Allocator,
        fn change(c: @This(), node: Value) !Value {
            return if (hasId(node, "n")) set(c.a, node, "newProperty", .json_true) else node;
        }
    };
    const result = try mapNodes(a, root, Context{ .a = a }, Context.change);
    const old_node = model.bodyNodes(a, model.arrayItems(root, "functions")[0])[0];
    const new_node = model.bodyNodes(a, model.arrayItems(result, "functions")[0])[0];
    try std.testing.expect(model.property(old_node, "newProperty") == null);
    try std.testing.expect(model.property(new_node, "newProperty") != null);
}

pub fn variantIds(a: Allocator, arm: Value) ![]const Value {
    return if (model.property(arm, "variantId")) |single| a.dupe(Value, &.{single}) else model.arrayItems(arm, "variantIds");
}

/// A malformed generated candidate is an internal failure, not successful validation. Semantic
/// issues keep their exact ordering even though their rejected document has no root/fingerprints.
pub fn validatedIssues(r: *@import("query.zig").Request, read: model.DocumentRead) !@TypeOf(@as(model.DocumentRead, undefined).validation_issues) {
    if (read.validation_issues.len > 0) return read.validation_issues;
    if (read.root == .json_null or read.fingerprints == null) {
        r.rejection = .{ .code = "InternalError", .message = "Unexpected internal failure; this request was not applied and the session is unchanged." };
        return error.Rejected;
    }
    return read.validation_issues;
}

test "candidate shape failures cannot pass as successful semantic validation" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    var request: @import("query.zig").Request = .{ .a = a, .line = "", .root = .empty, .model = .json_null, .provenance = null };
    const malformed = model.readDocument(a, "{\"schemaVersion\":24,\"snapshotId\":\"s\",\"types\":[],\"effects\":[],\"capabilities\":[],\"functions\":[],\"obligations\":[{\"id\":\"o\",\"originTransactionId\":\"t\",\"callNodeId\":\"n\",\"parameterId\":\"p\",\"status\":\"open\"}]}");
    try std.testing.expectEqual(@as(usize, 0), malformed.validation_issues.len);
    try std.testing.expectError(error.Rejected, validatedIssues(&request, malformed));
    try std.testing.expectEqualStrings("InternalError", request.rejection.code);
    const semantic = model.readDocument(a, "{\"schemaVersion\":24,\"snapshotId\":\"s\",\"types\":[],\"effects\":[],\"capabilities\":[],\"functions\":[{\"id\":\"f\",\"displayName\":\"F\",\"parameters\":[{\"id\":\"p\",\"displayName\":\"P\",\"type\":{\"kind\":\"named\",\"typeId\":\"missing\"}}],\"resultType\":{\"kind\":\"primitive\",\"primitive\":\"unit\"},\"requiredEffects\":[],\"body\":{\"nodes\":[{\"id\":\"n\",\"kind\":\"unitLiteral\"}],\"resultNodeId\":\"n\"}}],\"obligations\":[]}");
    try std.testing.expect(semantic.validation_issues.len > 0);
    const issues = try validatedIssues(&request, semantic);
    try std.testing.expect(issues.ptr == semantic.validation_issues.ptr);
}
