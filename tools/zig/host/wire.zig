//! The read-only wire encoders (`WireEncodeCommon` in F#), over the model reader's tree of the
//! open model in canonical order. Strings stay UTF-16 units and are escaped as the F# writers do.
const std = @import("std");
const model = @import("model_reader");

const Value = model.Value;
const Writer = std.Io.Writer;

/// The selected dependency as `declarations` reports it (`PinnedDependency.withProvenance`): each
/// member's ID, fingerprint and declaration keys, and the imported declarations it does not export.
pub const Provenance = struct {
    members: []const Member,
    hidden: []const []const u8,
    pub const Member = struct { id: []const u8, fingerprint: []const u8, keys: []const []const u8, path: []const u8, embedded: bool };
};

fn string(out: *Writer, value: Value) !void {
    try model.writeString(out, model.decoded(value));
}

fn ascii(out: *Writer, text: []const u8) !void {
    try out.print("\"{s}\"", .{text});
}

fn field(out: *Writer, first: *bool, name: []const u8) !void {
    if (!first.*) try out.writeByte(',');
    first.* = false;
    try out.print("\"{s}\":", .{name});
}

fn exported(item: Value) bool {
    return model.property(item, "exported") != null;
}

fn contains(keys: []const []const u8, key: []const u8) bool {
    for (keys) |k| if (std.mem.eql(u8, k, key)) return true;
    return false;
}

/// A type in wire notation (`typeRefIn`); a type parameter occurrence gets its `displayName`
/// when `names` (the declaration's `typeParameters`) lists it.
pub fn typeRef(out: *Writer, value: Value, names: []const Value) !void {
    const kind = model.decoded(model.property(value, "kind").?);
    var first = true;
    try out.writeByte('{');
    try field(out, &first, "kind");
    try model.writeString(out, kind);
    const kind_text = model.utf8(kind);
    if (std.mem.eql(u8, kind_text, "primitive")) {
        try field(out, &first, "primitive");
        try string(out, model.property(value, "primitive").?);
    } else if (std.mem.eql(u8, kind_text, "function")) {
        try field(out, &first, "parameters");
        try typeRefs(out, model.arrayItems(value, "parameters"), names);
        try field(out, &first, "resultType");
        try typeRef(out, model.property(value, "resultType").?, names);
    } else if (std.mem.eql(u8, kind_text, "named")) {
        try field(out, &first, "typeId");
        try string(out, model.property(value, "typeId").?);
        if (model.property(value, "arguments") != null) {
            try field(out, &first, "arguments");
            try typeRefs(out, model.arrayItems(value, "arguments"), names);
        }
    } else if (std.mem.eql(u8, kind_text, "capability")) {
        try field(out, &first, "capabilityId");
        try string(out, model.property(value, "capabilityId").?);
    } else if (std.mem.eql(u8, kind_text, "typeParameter")) {
        const id = model.decoded(model.property(value, "typeParameterId").?);
        try field(out, &first, "typeParameterId");
        try model.writeString(out, id);
        for (names) |name| if (std.mem.eql(u16, model.decoded(model.property(name, "typeParameterId").?), id)) {
            try field(out, &first, "displayName");
            try string(out, model.property(name, "displayName").?);
            break;
        };
    } else {
        try field(out, &first, "generic");
        try string(out, model.property(value, "generic").?);
        try field(out, &first, "arguments");
        try typeRefs(out, model.arrayItems(value, "arguments"), names);
    }
    try out.writeByte('}');
}

fn typeRefs(out: *Writer, values: []const Value, names: []const Value) Writer.Error!void {
    try out.writeByte('[');
    for (values, 0..) |item, index| {
        if (index > 0) try out.writeByte(',');
        try typeRef(out, item, names);
    }
    try out.writeByte(']');
}

/// `{ id, displayName, type }` (fields, parameters, capability parameters).
fn typed(out: *Writer, items: []const Value, names: []const Value) !void {
    try out.writeByte('[');
    for (items, 0..) |item, index| {
        if (index > 0) try out.writeByte(',');
        var first = true;
        try out.writeByte('{');
        try field(out, &first, "id");
        try string(out, model.property(item, "id").?);
        try field(out, &first, "displayName");
        try string(out, model.property(item, "displayName").?);
        try field(out, &first, "type");
        try typeRef(out, model.property(item, "type").?, names);
        try out.writeByte('}');
    }
    try out.writeByte(']');
}

fn typeParameters(out: *Writer, first: *bool, item: Value) !void {
    const names = model.arrayItems(item, "typeParameters");
    if (names.len == 0) return;
    try field(out, first, "typeParameters");
    try out.writeByte('[');
    for (names, 0..) |name, index| {
        if (index > 0) try out.writeByte(',');
        try out.writeAll("{\"typeParameterId\":");
        try string(out, model.property(name, "typeParameterId").?);
        try out.writeAll(",\"displayName\":");
        try string(out, model.property(name, "displayName").?);
        try out.writeByte('}');
    }
    try out.writeByte(']');
}

/// `id`, `displayName` and `exported`, which the wire always writes.
fn identity(out: *Writer, first: *bool, item: Value) !void {
    try field(out, first, "id");
    try string(out, model.property(item, "id").?);
    try field(out, first, "displayName");
    try string(out, model.property(item, "displayName").?);
    try field(out, first, "exported");
    try out.writeAll(if (exported(item)) "true" else "false");
}

/// `signatureFields`.
fn function(out: *Writer, item: Value) !void {
    try out.writeByte('{');
    try signature(out, item);
    try out.writeByte('}');
}

/// `functionSummary`: the signature with the body's result node and node count.
pub fn functionSummary(out: *Writer, item: Value, node_count: usize) !void {
    try out.writeByte('{');
    try signature(out, item);
    try out.writeAll(",\"body\":{\"resultNodeId\":");
    try string(out, model.property(model.property(item, "body").?, "resultNodeId").?);
    try out.print(",\"nodeCount\":{d}}}}}", .{node_count});
}

fn signature(out: *Writer, item: Value) !void {
    const names = model.arrayItems(item, "typeParameters");
    var first = true;
    try identity(out, &first, item);
    if (model.property(item, "mustNotWait") != null) {
        try field(out, &first, "mustNotWait");
        try out.writeAll("true");
    }
    try typeParameters(out, &first, item);
    try field(out, &first, "parameters");
    try typed(out, model.arrayItems(item, "parameters"), names);
    try field(out, &first, "resultType");
    try typeRef(out, model.property(item, "resultType").?, names);
    try field(out, &first, "requiredEffects");
    try out.writeByte('[');
    for (model.arrayItems(item, "requiredEffects"), 0..) |effect, index| {
        if (index > 0) try out.writeByte(',');
        try string(out, effect);
    }
    try out.writeByte(']');
}

/// `semanticType`.
fn semanticType(out: *Writer, item: Value) !void {
    const names = model.arrayItems(item, "typeParameters");
    var first = true;
    try out.writeByte('{');
    try identity(out, &first, item);
    try typeParameters(out, &first, item);
    try field(out, &first, "typeKind");
    if (model.property(item, "fields") != null) {
        try out.writeAll("{\"kind\":\"product\",\"fields\":");
        try typed(out, model.arrayItems(item, "fields"), names);
    } else {
        try out.writeAll("{\"kind\":\"union\",\"variants\":[");
        for (model.arrayItems(item, "variants"), 0..) |variant, index| {
            if (index > 0) try out.writeByte(',');
            try out.writeAll("{\"id\":");
            try string(out, model.property(variant, "id").?);
            try out.writeAll(",\"displayName\":");
            try string(out, model.property(variant, "displayName").?);
            try out.writeAll(",\"fields\":");
            try typed(out, model.arrayItems(variant, "fields"), names);
            try out.writeByte('}');
        }
        try out.writeByte(']');
    }
    try out.writeAll("}}");
}

/// `capabilityEntity`.
fn capability(out: *Writer, item: Value) !void {
    var first = true;
    try out.writeByte('{');
    try identity(out, &first, item);
    if (model.property(item, "taskBound") != null) {
        try field(out, &first, "taskBound");
        try out.writeAll("true");
    }
    try field(out, &first, "operations");
    try out.writeByte('[');
    for (model.arrayItems(item, "operations"), 0..) |operation, index| {
        if (index > 0) try out.writeByte(',');
        var inner = true;
        try out.writeByte('{');
        try field(out, &inner, "id");
        try string(out, model.property(operation, "id").?);
        try field(out, &inner, "displayName");
        try string(out, model.property(operation, "displayName").?);
        try field(out, &inner, "parameters");
        try typed(out, model.arrayItems(operation, "parameters"), &.{});
        try field(out, &inner, "resultType");
        try typeRef(out, model.property(operation, "resultType").?, &.{});
        try field(out, &inner, "failureType");
        if (model.property(operation, "failureType")) |failure| try typeRef(out, failure, &.{}) else try out.writeAll("null");
        try field(out, &inner, "effectId");
        try string(out, model.property(operation, "effectId").?);
        if (model.property(operation, "mayWait") != null) {
            try field(out, &inner, "mayWait");
            try out.writeAll("true");
        }
        try out.writeByte('}');
    }
    try out.writeAll("]}");
}

const groups = [_][2][]const u8{ .{ "functions", "function" }, .{ "types", "type" }, .{ "effects", "effect" }, .{ "capabilities", "capability" } };

/// `WireEncodeCommon.declarations` of the visible declarations, with their provenance.
pub fn declarations(a: std.mem.Allocator, out: *Writer, root: Value, provenance: ?Provenance) !void {
    const hidden: []const []const u8 = if (provenance) |p| p.hidden else &.{};
    const keyOf = struct {
        fn key(allocator: std.mem.Allocator, kind: []const u8, item: Value) ![]const u8 {
            return std.fmt.allocPrint(allocator, "{s}:{s}", .{ kind, model.utf8(model.decoded(model.property(item, "id").?)) });
        }
    }.key;
    try out.writeAll("{\"provenance\":[");
    var count: usize = 0;
    for (groups) |group| for (model.arrayItems(root, group[0])) |item| {
        const key = try keyOf(a, group[1], item);
        if (contains(hidden, key)) continue;
        if (count > 0) try out.writeByte(',');
        count += 1;
        try out.writeAll("{\"kind\":");
        try ascii(out, group[1]);
        try out.writeAll(",\"id\":");
        try string(out, model.property(item, "id").?);
        var sources: usize = 0;
        if (provenance) |p| for (p.members) |member| {
            if (contains(member.keys, key)) sources += 1;
        };
        try out.writeAll(if (sources == 0) ",\"ownership\":\"local\",\"sources\":[" else ",\"ownership\":\"imported\",\"sources\":[");
        var written: usize = 0;
        if (provenance) |p| for (p.members) |member| if (contains(member.keys, key)) {
            if (written > 0) try out.writeByte(',');
            written += 1;
            try out.print("{{\"snapshotId\":\"{s}\",\"fingerprint\":\"{s}\"}}", .{ member.id, member.fingerprint });
        };
        try out.writeAll("]}");
    };
    try out.writeByte(']');
    for (groups) |group| {
        try out.print(",\"{s}\":[", .{group[0]});
        var written: usize = 0;
        for (model.arrayItems(root, group[0])) |item| {
            if (contains(hidden, try keyOf(a, group[1], item))) continue;
            if (written > 0) try out.writeByte(',');
            written += 1;
            if (std.mem.eql(u8, group[1], "function")) try function(out, item)
            else if (std.mem.eql(u8, group[1], "type")) try semanticType(out, item)
            else if (std.mem.eql(u8, group[1], "capability")) try capability(out, item)
            else {
                var first = true;
                try out.writeByte('{');
                try identity(out, &first, item);
                try out.writeByte('}');
            }
        }
        try out.writeByte(']');
    }
    try out.writeByte('}');
}
