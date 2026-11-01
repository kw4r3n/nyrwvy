//! Edits to protocol metadata and immutable review objects.
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
pub fn set(a: Allocator, value: Value, key: []const u8, replacement: Value) !Value {
    var fields: std.ArrayList(model.Member) = .empty;
    var found = false;
    for (value.object) |field| {
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
    for (value.object) |field| if (!m.same(model.utf8(field.name), key)) try fields.append(a, field);
    return .{ .object = fields.items };
}
