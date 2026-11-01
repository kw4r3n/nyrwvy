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

fn field(out: *Writer, first: *bool, name: []const u8) !void {
    if (!first.*) try out.writeByte(',');
    first.* = false;
    try out.print("\"{s}\":", .{name});
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

/// `WireEncodeCommon.capabilityOperation`, shared by declaration and transaction reviews.
pub fn capabilityOperation(out: *Writer, operation: Value) !void {
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

const tm = model.session_model;

fn ordered(a: std.mem.Allocator, interner: *const tm.Interner, comptime T: type, comptime domain: tm.Domain, items: []const T) ![]T {
    const sorted = try a.dupe(T, items);
    std.mem.sort(T, sorted, interner, struct {
        fn less(i: *const tm.Interner, x: T, y: T) bool {
            return i.less(domain, x.id, y.id);
        }
    }.less);
    return sorted;
}

const TypedWire = struct {
    out: *Writer,
    i: *const tm.Interner,
    fn id(self: TypedWire, domain: tm.Domain, value: tm.Id) !void {
        try model.writeString(self.out, self.i.idText(domain, value));
    }
    fn name(self: TypedWire, value: u32) !void {
        try model.writeString(self.out, self.i.stringText(value));
    }
    fn identity(self: TypedWire, domain: tm.Domain, id_: tm.Id, name_: u32, exported_: bool) !void {
        try self.out.writeAll("\"id\":");
        try self.id(domain, id_);
        try self.out.writeAll(",\"displayName\":");
        try self.name(name_);
        try self.out.writeAll(if (exported_) ",\"exported\":true" else ",\"exported\":false");
    }
    fn typeRef(self: TypedWire, t: tm.TypeRef, names: []const tm.TypeParameter) anyerror!void {
        switch (t) {
            .primitive => |v| {
                try self.out.writeAll("{\"kind\":\"primitive\",\"primitive\":");
                try self.name(v);
            },
            .named => |v| {
                try self.out.writeAll("{\"kind\":\"named\",\"typeId\":");
                try self.id(.type, v.id);
                if (v.arguments) |args| {
                    try self.out.writeAll(",\"arguments\":");
                    try self.typeRefs(args, names);
                }
            },
            .function => |v| {
                try self.out.writeAll("{\"kind\":\"function\",\"parameters\":");
                try self.typeRefs(v.parameters, names);
                try self.out.writeAll(",\"resultType\":");
                try self.typeRef(v.result.*, names);
            },
            .type_parameter => |v| {
                try self.out.writeAll("{\"kind\":\"typeParameter\",\"typeParameterId\":");
                try self.id(.type_parameter, v);
                for (names) |n| if (n.id == v) {
                    try self.out.writeAll(",\"displayName\":");
                    try self.name(n.name);
                    break;
                };
            },
            .capability => |v| {
                try self.out.writeAll("{\"kind\":\"capability\",\"capabilityId\":");
                try self.id(.capability, v);
            },
            .generic => |v| {
                try self.out.writeAll("{\"kind\":\"builtinGeneric\",\"generic\":");
                try self.name(v.name);
                try self.out.writeAll(",\"arguments\":");
                try self.typeRefs(v.arguments, names);
            },
        }
        try self.out.writeByte('}');
    }
    fn typeRefs(self: TypedWire, ts: []const tm.TypeRef, names: []const tm.TypeParameter) anyerror!void {
        try self.out.writeByte('[');
        for (ts, 0..) |t, n| {
            if (n > 0) try self.out.writeByte(',');
            try self.typeRef(t, names);
        }
        try self.out.writeByte(']');
    }
    fn fields(self: TypedWire, domain: tm.Domain, values: []const tm.Typed, names: []const tm.TypeParameter) !void {
        const sorted = if (domain == .field) try ordered(std.heap.page_allocator, self.i, tm.Typed, .field, values) else null;
        defer if (sorted) |items| std.heap.page_allocator.free(items);
        try self.out.writeByte('[');
        for (if (sorted) |items| items else values, 0..) |v, n| {
            if (n > 0) try self.out.writeByte(',');
            try self.out.writeAll("{\"id\":");
            try self.id(domain, v.id);
            try self.out.writeAll(",\"displayName\":");
            try self.name(v.name);
            try self.out.writeAll(",\"type\":");
            try self.typeRef(v.type, names);
            try self.out.writeByte('}');
        }
        try self.out.writeByte(']');
    }
    fn parameters(self: TypedWire, names: []const tm.TypeParameter) !void {
        if (names.len == 0) return;
        try self.out.writeAll(",\"typeParameters\":[");
        for (names, 0..) |v, n| {
            if (n > 0) try self.out.writeByte(',');
            try self.out.writeAll("{\"typeParameterId\":");
            try self.id(.type_parameter, v.id);
            try self.out.writeAll(",\"displayName\":");
            try self.name(v.name);
            try self.out.writeByte('}');
        }
        try self.out.writeByte(']');
    }
    fn functionFields(self: TypedWire, v: *const tm.Function) !void {
        const names = v.type_parameters orelse &.{};
        try self.identity(.function, v.id, v.name, v.exported);
        if (v.must_not_wait) try self.out.writeAll(",\"mustNotWait\":true");
        try self.parameters(names);
        try self.out.writeAll(",\"parameters\":");
        try self.fields(.parameter, v.parameters, names);
        try self.out.writeAll(",\"resultType\":");
        try self.typeRef(v.result, names);
        try self.out.writeAll(",\"requiredEffects\":[");
        const effects = try std.heap.page_allocator.dupe(tm.Id, v.effects);
        defer std.heap.page_allocator.free(effects);
        std.mem.sort(tm.Id, effects, self.i, struct {
            fn less(i: *const tm.Interner, x: tm.Id, y: tm.Id) bool {
                return i.less(.effect, x, y);
            }
        }.less);
        for (effects, 0..) |e, n| {
            if (n > 0) try self.out.writeByte(',');
            try self.id(.effect, e);
        }
        try self.out.writeByte(']');
    }
    fn function(self: TypedWire, v: *const tm.Function) !void {
        try self.out.writeByte('{');
        try self.functionFields(v);
        try self.out.writeByte('}');
    }
    fn semanticType(self: TypedWire, v: *const tm.Type) !void {
        const names = v.type_parameters orelse &.{};
        try self.out.writeByte('{');
        try self.identity(.type, v.id, v.name, v.exported);
        try self.parameters(names);
        if (v.product) {
            try self.out.writeAll(",\"typeKind\":{\"kind\":\"product\",\"fields\":");
            try self.fields(.field, v.fields, names);
        } else {
            try self.out.writeAll(",\"typeKind\":{\"kind\":\"union\",\"variants\":[");
            const variants = try ordered(std.heap.page_allocator, self.i, tm.Variant, .variant, v.variants);
            defer std.heap.page_allocator.free(variants);
            for (variants, 0..) |variant, n| {
                if (n > 0) try self.out.writeByte(',');
                try self.out.writeAll("{\"id\":");
                try self.id(.variant, variant.id);
                try self.out.writeAll(",\"displayName\":");
                try self.name(variant.name);
                try self.out.writeAll(",\"fields\":");
                try self.fields(.field, variant.fields, names);
                try self.out.writeByte('}');
            }
            try self.out.writeByte(']');
        }
        try self.out.writeAll("}}");
    }
    fn capability(self: TypedWire, v: *const tm.Capability) !void {
        try self.out.writeByte('{');
        try self.identity(.capability, v.id, v.name, v.exported);
        if (v.task_bound) try self.out.writeAll(",\"taskBound\":true");
        try self.out.writeAll(",\"operations\":[");
        const operations = try ordered(std.heap.page_allocator, self.i, tm.Operation, .operation, v.operations);
        defer std.heap.page_allocator.free(operations);
        for (operations, 0..) |op, n| {
            if (n > 0) try self.out.writeByte(',');
            try self.out.writeAll("{\"id\":");
            try self.id(.operation, op.id);
            try self.out.writeAll(",\"displayName\":");
            try self.name(op.name);
            try self.out.writeAll(",\"parameters\":");
            try self.fields(.capability_parameter, op.parameters, &.{});
            try self.out.writeAll(",\"resultType\":");
            try self.typeRef(op.result, &.{});
            try self.out.writeAll(",\"failureType\":");
            if (op.failure) |failure| try self.typeRef(failure, &.{}) else try self.out.writeAll("null");
            try self.out.writeAll(",\"effectId\":");
            try self.id(.effect, op.effect);
            if (op.may_wait) try self.out.writeAll(",\"mayWait\":true");
            try self.out.writeByte('}');
        }
        try self.out.writeAll("]}");
    }
};

pub fn typedTypeRef(out: *Writer, interner: *const tm.Interner, value: tm.TypeRef, names: []const tm.TypeParameter) !void {
    try (TypedWire{ .out = out, .i = interner }).typeRef(value, names);
}

pub fn typedSemanticType(out: *Writer, interner: *const tm.Interner, item: *const tm.Type) !void {
    try (TypedWire{ .out = out, .i = interner }).semanticType(item);
}
pub fn typedCapabilityOperation(out: *Writer, interner: *const tm.Interner, op: *const tm.Operation) !void {
    const w: TypedWire = .{ .out = out, .i = interner };
    try out.writeAll("{\"id\":");
    try w.id(.operation, op.id);
    try out.writeAll(",\"displayName\":");
    try w.name(op.name);
    try out.writeAll(",\"parameters\":");
    try w.fields(.capability_parameter, op.parameters, &.{});
    try out.writeAll(",\"resultType\":");
    try w.typeRef(op.result, &.{});
    try out.writeAll(",\"failureType\":");
    if (op.failure) |failure| try w.typeRef(failure, &.{}) else try out.writeAll("null");
    try out.writeAll(",\"effectId\":");
    try w.id(.effect, op.effect);
    if (op.may_wait) try out.writeAll(",\"mayWait\":true");
    try out.writeByte('}');
}
pub fn typedCapability(out: *Writer, interner: *const tm.Interner, item: *const tm.Capability) !void {
    try (TypedWire{ .out = out, .i = interner }).capability(item);
}
pub fn typedFunctionNodes(out: *Writer, interner: *const tm.Interner, item: *const tm.Function, nodes: []const tm.Node) !void {
    const w: TypedWire = .{ .out = out, .i = interner };
    try out.writeByte('{');
    try w.functionFields(item);
    try out.writeAll(",\"body\":{\"nodes\":[");
    for (nodes, 0..) |node, index| {
        if (index > 0) try out.writeByte(',');
        try out.writeAll("{\"id\":");
        try w.id(.node, node.id());
        const kind = node.kind();
        try out.print(",\"kind\":\"{c}{s}\"}}", .{ std.ascii.toUpper(kind[0]), kind[1..] });
    }
    try out.writeAll("],\"resultNodeId\":");
    try w.id(.node, item.body.graph(0).result());
    try out.writeAll("}}");
}

/// Encode visible declarations directly from the immutable generation.
pub fn typedDeclarations(a: std.mem.Allocator, out: *Writer, g: *const tm.Generation, i: *const tm.Interner, provenance: ?Provenance) !void {
    const w: TypedWire = .{ .out = out, .i = i };
    const hidden = if (provenance) |p| p.hidden else &.{};
    var count: usize = 0;
    try out.writeAll("{\"provenance\":[");
    inline for (.{ .{ "function", .function, g.functions }, .{ "type", .type, g.types }, .{ "effect", .effect, g.effects }, .{ "capability", .capability, g.capabilities } }) |group| {
        const items = try ordered(a, i, @typeInfo(@TypeOf(group[2])).pointer.child, group[1], group[2]);
        for (items) |item| {
            const key = try std.fmt.allocPrint(a, "{s}:{s}", .{ group[0], model.utf8(i.idText(group[1], item.id)) });
            if (contains(hidden, key)) continue;
            if (count > 0) try out.writeByte(',');
            count += 1;
            try out.print("{{\"kind\":\"{s}\",\"id\":", .{group[0]});
            try w.id(group[1], item.id);
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
        }
    }
    try out.writeByte(']');
    inline for (.{ .{ "functions", "function", .function, g.functions }, .{ "types", "type", .type, g.types }, .{ "effects", "effect", .effect, g.effects }, .{ "capabilities", "capability", .capability, g.capabilities } }) |group| {
        try out.print(",\"{s}\":[", .{group[0]});
        var written: usize = 0;
        const items = try ordered(a, i, @typeInfo(@TypeOf(group[3])).pointer.child, group[2], group[3]);
        for (items) |item| {
            const key = try std.fmt.allocPrint(a, "{s}:{s}", .{ group[1], model.utf8(i.idText(group[2], item.id)) });
            if (contains(hidden, key)) continue;
            if (written > 0) try out.writeByte(',');
            written += 1;
            if (comptime group[2] == .function) try w.function(item) else if (comptime group[2] == .type) try w.semanticType(item) else if (comptime group[2] == .effect) {
                try out.writeByte('{');
                try w.identity(.effect, item.id, item.name, item.exported);
                try out.writeByte('}');
            } else try w.capability(item);
        }
        try out.writeByte(']');
    }
    try out.writeByte('}');
}

// ---------------------------------------------------------------- structured graph

/// One node kind's wire form (`WireGraph.kind`): its wire kind and, in wire order, each field's
/// wire name, snapshot property and shape (graph_kinds.txt).
const GraphKind = struct { name: []const u8, fields: []const GraphField };
const GraphField = struct { name: []const u8, source: []const u8, shape: []const u8 };

var graph_kinds: ?std.StringHashMapUnmanaged(GraphKind) = null;

fn graphKinds() std.StringHashMapUnmanaged(GraphKind) {
    if (graph_kinds) |kinds| return kinds;
    const a = std.heap.page_allocator;
    var kinds: std.StringHashMapUnmanaged(GraphKind) = .empty;
    var lines = std.mem.tokenizeScalar(u8, @embedFile("graph_kinds.txt"), '\n');
    while (lines.next()) |line| {
        if (line[0] == '#') continue;
        var parts = std.mem.splitScalar(u8, line, '|');
        const kind = parts.next().?;
        const name = parts.next().?;
        var fields: std.ArrayList(GraphField) = .empty;
        var items = std.mem.tokenizeScalar(u8, parts.next().?, ' ');
        while (items.next()) |item| {
            var pieces = std.mem.splitScalar(u8, item, ':');
            fields.append(a, .{ .name = pieces.next().?, .source = pieces.next().?, .shape = pieces.next().? }) catch @panic("out of memory");
        }
        kinds.put(a, kind, .{ .name = name, .fields = fields.items }) catch @panic("out of memory");
    }
    graph_kinds = kinds;
    return kinds;
}

pub fn inferredTypeRef(out: *Writer, value: Value, names: []const tm.TypeParameter, interner: *const tm.Interner) !void {
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
        try inferredTypeRefs(out, model.arrayItems(value, "parameters"), names, interner);
        try field(out, &first, "resultType");
        try inferredTypeRef(out, model.property(value, "resultType").?, names, interner);
    } else if (std.mem.eql(u8, kind_text, "named")) {
        try field(out, &first, "typeId");
        try string(out, model.property(value, "typeId").?);
        if (model.property(value, "arguments") != null) {
            try field(out, &first, "arguments");
            try inferredTypeRefs(out, model.arrayItems(value, "arguments"), names, interner);
        }
    } else if (std.mem.eql(u8, kind_text, "capability")) {
        try field(out, &first, "capabilityId");
        try string(out, model.property(value, "capabilityId").?);
    } else if (std.mem.eql(u8, kind_text, "typeParameter")) {
        const id = model.decoded(model.property(value, "typeParameterId").?);
        try field(out, &first, "typeParameterId");
        try model.writeString(out, id);
        for (names) |name| if (std.mem.eql(u16, interner.idText(.type_parameter, name.id), id)) {
            try field(out, &first, "displayName");
            try model.writeString(out, interner.stringText(name.name));
            break;
        };
    } else {
        try field(out, &first, "generic");
        try string(out, model.property(value, "generic").?);
        try field(out, &first, "arguments");
        try inferredTypeRefs(out, model.arrayItems(value, "arguments"), names, interner);
    }
    try out.writeByte('}');
}

fn inferredTypeRefs(out: *Writer, values: []const Value, names: []const tm.TypeParameter, interner: *const tm.Interner) Writer.Error!void {
    try out.writeByte('[');
    for (values, 0..) |item, index| {
        if (index > 0) try out.writeByte(',');
        try inferredTypeRef(out, item, names, interner);
    }
    try out.writeByte(']');
}

const TypedGraphWire = struct {
    a: std.mem.Allocator,
    w: TypedWire,
    names: []const tm.TypeParameter,
    fn binding(self: @This(), value: *const tm.Binding) !void {
        try self.w.out.writeAll("{\"id\":");
        try self.w.id(.binding, value.id);
        try self.w.out.writeAll(",\"displayName\":");
        if (value.name) |name| try self.w.name(name) else try self.w.out.writeAll("null");
        try self.w.out.writeAll(",\"type\":");
        try self.w.typeRef(value.type, self.names);
        try self.w.out.writeByte('}');
    }
    fn graph(self: @This(), graph_: tm.GraphView) anyerror!void {
        try self.w.out.writeAll("{\"resultNodeId\":");
        try self.w.id(.node, graph_.result());
        try self.w.out.writeAll(",\"nodes\":[");
        var nodes: std.ArrayList(tm.Node) = .empty;
        var it = graph_.nodes();
        while (it.next()) |item| try nodes.append(self.a, item);
        std.mem.sort(tm.Node, nodes.items, self.w.i, struct {
            fn less(i: *const tm.Interner, x: tm.Node, y: tm.Node) bool {
                return i.less(.node, x.id(), y.id());
            }
        }.less);
        for (nodes.items, 0..) |item, index| {
            if (index > 0) try self.w.out.writeByte(',');
            try self.w.out.writeAll("{\"id\":");
            try self.w.id(.node, item.id());
            try self.w.out.writeAll(",\"kind\":");
            try self.node(item);
            try self.w.out.writeByte('}');
        }
        try self.w.out.writeAll("]}");
    }
    fn node(self: @This(), node_: tm.Node) anyerror!void {
        const kind = graphKinds().get(node_.kind()).?;
        try self.w.out.print("{{\"kind\":\"{s}\"", .{kind.name});
        for (kind.fields) |spec| {
            var iterator = node_.payload();
            const entry = while (iterator.next()) |item| {
                if (std.mem.eql(u8, item.read.name, spec.source)) break item;
            } else continue;
            switch (entry.value) {
                .optid => |v| if (v == null) continue,
                .ctx => |v| if (v.len() == 0) continue,
                .targs => |v| if (v.len() == 0) continue,
                else => {},
            }
            try self.w.out.print(",\"{s}\":", .{spec.name});
            switch (entry.value) {
                .id => |v| try self.w.id(entry.read.domain.?, v),
                .optid => |v| try self.w.id(entry.read.domain.?, v.?),
                .ids => |ids| {
                    try self.w.out.writeByte('[');
                    for (ids, 0..) |v, n| {
                        if (n > 0) try self.w.out.writeByte(',');
                        try self.w.id(entry.read.domain.?, v);
                    }
                    try self.w.out.writeByte(']');
                },
                .type => |v| try self.w.typeRef(v, self.names),
                .targs => |v| {
                    try self.w.out.writeByte('[');
                    for (0..v.len()) |n| {
                        if (n > 0) try self.w.out.writeByte(',');
                        try self.w.typeRef(v.at(n), self.names);
                    }
                    try self.w.out.writeByte(']');
                },
                .graph => |v| try self.graph(v),
                .varm, .errarm => |v| {
                    try self.w.out.print("{{\"{s}\":", .{if (entry.value == .varm) "valueBinding" else "errorBinding"});
                    try self.binding(v.binding);
                    try self.w.out.writeAll(",\"body\":");
                    try self.graph(v.body);
                    try self.w.out.writeByte('}');
                },
                .args, .ctx, .capargs, .fields => |v| {
                    const domain: tm.Domain = if (entry.value == .capargs) .capability_parameter else if (entry.value == .fields) .field else .parameter;
                    const name = if (domain == .field) "fieldId" else if (domain == .capability_parameter) "capabilityParameterId" else "parameterId";
                    const items = try self.a.alloc(tm.Target, v.len());
                    for (items, 0..) |*item, n| item.* = v.at(n);
                    std.mem.sort(tm.Target, items, .{ self.w.i, domain }, struct {
                        fn less(ctx: struct { *const tm.Interner, tm.Domain }, x: tm.Target, y: tm.Target) bool {
                            return ctx[0].less(ctx[1], x.target, y.target);
                        }
                    }.less);
                    try self.w.out.writeByte('[');
                    for (items, 0..) |item, n| {
                        if (n > 0) try self.w.out.writeByte(',');
                        try self.w.out.print("{{\"{s}\":", .{name});
                        try self.w.id(domain, item.target);
                        try self.w.out.writeAll(",\"valueNodeId\":");
                        try self.w.id(.node, item.value);
                        try self.w.out.writeByte('}');
                    }
                    try self.w.out.writeByte(']');
                },
                .lit => |v| try self.w.name(v),
                .bool => |v| try self.w.out.writeAll(if (v) "true" else "false"),
                .int => |v| try self.w.out.print("\"{d}\"", .{v}),
                .bits => |v| try self.w.out.print("\"{x:0>16}\"", .{v}),
                .mode => |v| try self.w.out.print("\"{s}\"", .{@tagName(v)}),
                .impls => |v| {
                    const items = try self.a.alloc(tm.Implementation, v.len());
                    for (items, 0..) |*item, n| item.* = v.at(n);
                    std.mem.sort(tm.Implementation, items, self.w.i, struct {
                        fn less(i: *const tm.Interner, x: tm.Implementation, y: tm.Implementation) bool {
                            return i.less(.operation, x.operation, y.operation);
                        }
                    }.less);
                    try self.w.out.writeByte('[');
                    for (items, 0..) |item, n| {
                        if (n > 0) try self.w.out.writeByte(',');
                        try self.w.out.writeAll("{\"operationId\":");
                        try self.w.id(.operation, item.operation);
                        try self.w.out.writeAll(",\"functionId\":");
                        try self.w.id(.function, item.function);
                        try self.w.out.writeByte('}');
                    }
                    try self.w.out.writeByte(']');
                },
                .arms => |arms| {
                    try self.w.out.writeByte('[');
                    var iterator_ = arms.iterator();
                    const items = try self.a.alloc(tm.UnionArm, arms.len());
                    for (items) |*item| {
                        item.* = iterator_.next().?;
                        const variants = try self.a.dupe(tm.Id, item.variants);
                        std.mem.sort(tm.Id, variants, self.w.i, struct {
                            fn less(i: *const tm.Interner, x: tm.Id, y: tm.Id) bool {
                                return i.less(.variant, x, y);
                            }
                        }.less);
                        item.variants = variants;
                    }
                    std.mem.sort(tm.UnionArm, items, self.w.i, struct {
                        fn less(i: *const tm.Interner, x: tm.UnionArm, y: tm.UnionArm) bool {
                            return i.less(.variant, x.variants[0], y.variants[0]);
                        }
                    }.less);
                    for (items, 0..) |arm_, n| {
                        if (n > 0) try self.w.out.writeByte(',');
                        if (arm_.variants.len == 1) {
                            try self.w.out.writeAll("{\"variantId\":");
                            try self.w.id(.variant, arm_.variants[0]);
                        } else {
                            try self.w.out.writeAll("{\"variantIds\":[");
                            for (arm_.variants, 0..) |v, index| {
                                if (index > 0) try self.w.out.writeByte(',');
                                try self.w.id(.variant, v);
                            }
                            try self.w.out.writeByte(']');
                        }
                        try self.w.out.writeAll(",\"fieldBindings\":[");
                        const fields = try self.a.alloc(tm.FieldBinding, arm_.fields.len());
                        for (fields, 0..) |*f, index| f.* = arm_.fields.at(index);
                        std.mem.sort(tm.FieldBinding, fields, self.w.i, struct {
                            fn less(i: *const tm.Interner, x: tm.FieldBinding, y: tm.FieldBinding) bool {
                                return i.less(.field, x.source, y.source);
                            }
                        }.less);
                        for (fields, 0..) |f, index| {
                            if (index > 0) try self.w.out.writeByte(',');
                            try self.w.out.writeAll("{\"fieldId\":");
                            try self.w.id(.field, f.source);
                            try self.w.out.writeAll(",\"binding\":");
                            try self.binding(f.binding);
                            try self.w.out.writeByte('}');
                        }
                        try self.w.out.writeAll("],\"body\":");
                        try self.graph(arm_.body);
                        try self.w.out.writeByte('}');
                    }
                    try self.w.out.writeByte(']');
                },
            }
        }
        try self.w.out.writeByte('}');
    }
};

pub fn typedFunctionEntity(a: std.mem.Allocator, out: *Writer, i: *const tm.Interner, function_: *const tm.Function) !void {
    const w: TypedWire = .{ .out = out, .i = i };
    try out.writeByte('{');
    try w.functionFields(function_);
    try out.writeAll(",\"body\":");
    try (TypedGraphWire{ .a = a, .w = w, .names = function_.type_parameters orelse &.{} }).graph(function_.body.graph(0));
    try out.writeByte('}');
}
pub fn typedFunctionSummary(out: *Writer, i: *const tm.Interner, function_: *const tm.Function, count: usize) !void {
    const w: TypedWire = .{ .out = out, .i = i };
    try out.writeByte('{');
    try w.functionFields(function_);
    try out.writeAll(",\"body\":{\"resultNodeId\":");
    try w.id(.node, function_.body.graph(0).result());
    try out.print(",\"nodeCount\":{d}}}}}", .{count});
}

const Legacy = if (@import("builtin").is_test) struct {
    fn ascii(out: *Writer, text: []const u8) !void {
        try out.print("\"{s}\"", .{text});
    }

    fn exported(item: Value) bool {
        return model.property(item, "exported") != null;
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

    /// `WireEncodeCommon.functionEntity`: all nodes, including nested graphs, in traversal order.
    pub fn functionNodes(out: *Writer, item: Value, nodes: []const Value) !void {
        try out.writeByte('{');
        try signature(out, item);
        try out.writeAll(",\"body\":{\"nodes\":[");
        for (nodes, 0..) |node, index| {
            if (index > 0) try out.writeByte(',');
            try out.writeAll("{\"id\":");
            try string(out, model.property(node, "id").?);
            const kind = model.utf8(model.decoded(model.property(node, "kind").?));
            // SemanticNodeKind.name is the wire tag with its initial letter capitalized.
            try out.print(",\"kind\":\"{c}{s}\"}}", .{ std.ascii.toUpper(kind[0]), kind[1..] });
        }
        try out.writeAll("],\"resultNodeId\":");
        try string(out, model.property(model.property(item, "body").?, "resultNodeId").?);
        try out.writeAll("}}");
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
    pub fn semanticType(out: *Writer, item: Value) !void {
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
    pub fn capability(out: *Writer, item: Value) !void {
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
            try capabilityOperation(out, operation);
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
                if (std.mem.eql(u8, group[1], "function")) try function(out, item) else if (std.mem.eql(u8, group[1], "type")) try semanticType(out, item) else if (std.mem.eql(u8, group[1], "capability")) try capability(out, item) else {
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

    fn is(text: []const u8, name: []const u8) bool {
        return std.mem.eql(u8, text, name);
    }

    /// A pattern binding: its ID, display name (null when it has none) and type.
    fn binding(out: *Writer, value: Value, names: []const Value) !void {
        try out.writeAll("{\"id\":");
        try string(out, model.property(value, "id").?);
        try out.writeAll(",\"displayName\":");
        if (model.property(value, "displayName")) |name| try string(out, name) else try out.writeAll("null");
        try out.writeAll(",\"type\":");
        try typeRef(out, model.property(value, "type").?, names);
        try out.writeByte('}');
    }

    /// Bindings of one target each (`argumentList` and `fields`), already in target order.
    fn targets(out: *Writer, items: []const Value, source: []const u8, name: []const u8) !void {
        try out.writeByte('[');
        for (items, 0..) |item, index| {
            if (index > 0) try out.writeByte(',');
            try out.print("{{\"{s}\":", .{name});
            try string(out, model.property(item, source).?);
            try out.writeAll(",\"valueNodeId\":");
            try string(out, model.property(item, "valueNodeId").?);
            try out.writeByte('}');
        }
        try out.writeByte(']');
    }

    fn arm(out: *Writer, value: Value, comptime binding_name: []const u8, names: []const Value) !void {
        try out.writeAll("{\"" ++ binding_name ++ "\":");
        try binding(out, model.property(value, binding_name).?, names);
        try out.writeAll(",\"body\":");
        try graph(out, model.property(value, "body").?, names);
        try out.writeByte('}');
    }

    /// `WireGraph.graph`: the result node and the nodes in ID order (the model's order).
    fn graph(out: *Writer, value: Value, names: []const Value) anyerror!void {
        try out.writeAll("{\"resultNodeId\":");
        try string(out, model.property(value, "resultNodeId").?);
        try out.writeAll(",\"nodes\":[");
        for (model.arrayItems(value, "nodes"), 0..) |node, index| {
            if (index > 0) try out.writeByte(',');
            try out.writeAll("{\"id\":");
            try string(out, model.property(node, "id").?);
            try out.writeAll(",\"kind\":");
            try graphNode(out, node, names);
            try out.writeByte('}');
        }
        try out.writeAll("]}");
    }

    /// `WireGraph.kind`: a node's payload in its wire kind's fields.
    fn graphNode(out: *Writer, node: Value, names: []const Value) !void {
        const kind = graphKinds().get(model.utf8(model.decoded(model.property(node, "kind").?))).?;
        try out.print("{{\"kind\":\"{s}\"", .{kind.name});
        for (kind.fields) |f| {
            const value = model.property(node, f.source);
            const shape = f.shape;
            // Optional payloads are omitted, as the model omits them.
            if (value == null) continue;
            try out.print(",\"{s}\":", .{f.name});
            const v = value.?;
            if (is(shape, "id") or is(shape, "optid") or is(shape, "bits") or is(shape, "mode") or is(shape, "lit")) {
                try string(out, v);
            } else if (is(shape, "ids")) {
                try out.writeByte('[');
                for (v.array, 0..) |item, index| {
                    if (index > 0) try out.writeByte(',');
                    try string(out, item);
                }
                try out.writeByte(']');
            } else if (is(shape, "type")) {
                try typeRef(out, v, names);
            } else if (is(shape, "targs")) {
                try typeRefs(out, v.array, names);
            } else if (is(shape, "graph")) {
                try graph(out, v, names);
            } else if (is(shape, "varm")) {
                try arm(out, v, "valueBinding", names);
            } else if (is(shape, "errarm")) {
                try arm(out, v, "errorBinding", names);
            } else if (is(shape, "args") or is(shape, "ctx")) {
                try targets(out, v.array, "targetParameterId", "parameterId");
            } else if (is(shape, "capargs")) {
                try targets(out, v.array, "targetCapabilityParameterId", "capabilityParameterId");
            } else if (is(shape, "fields")) {
                try targets(out, v.array, "targetFieldId", "fieldId");
            } else if (is(shape, "bool")) {
                try out.writeAll(if (v == .json_true) "true" else "false");
            } else if (is(shape, "int")) {
                switch (v) {
                    .number => |digits| try out.print("\"{s}\"", .{digits}),
                    else => try string(out, v),
                }
            } else if (is(shape, "impls")) {
                try out.writeByte('[');
                for (v.array, 0..) |item, index| {
                    if (index > 0) try out.writeByte(',');
                    try out.writeAll("{\"operationId\":");
                    try string(out, model.property(item, "operationId").?);
                    try out.writeAll(",\"functionId\":");
                    try string(out, model.property(item, "functionId").?);
                    try out.writeByte('}');
                }
                try out.writeByte(']');
            } else if (is(shape, "arms")) {
                try out.writeByte('[');
                for (v.array, 0..) |item, index| {
                    if (index > 0) try out.writeByte(',');
                    if (model.property(item, "variantId")) |variant| {
                        try out.writeAll("{\"variantId\":");
                        try string(out, variant);
                    } else {
                        try out.writeAll("{\"variantIds\":[");
                        for (model.arrayItems(item, "variantIds"), 0..) |variant, i| {
                            if (i > 0) try out.writeByte(',');
                            try string(out, variant);
                        }
                        try out.writeByte(']');
                    }
                    try out.writeAll(",\"fieldBindings\":[");
                    for (model.arrayItems(item, "fieldBindings"), 0..) |field_binding, i| {
                        if (i > 0) try out.writeByte(',');
                        try out.writeAll("{\"fieldId\":");
                        try string(out, model.property(field_binding, "sourceFieldId").?);
                        try out.writeAll(",\"binding\":");
                        try binding(out, model.property(field_binding, "binding").?, names);
                        try out.writeByte('}');
                    }
                    try out.writeAll("],\"body\":");
                    try graph(out, model.property(item, "body").?, names);
                    try out.writeByte('}');
                }
                try out.writeByte(']');
            } else std.debug.panic("graph_kinds.txt: unknown shape '{s}'", .{shape});
        }
        try out.writeByte('}');
    }

    /// `WireGraph.functionEntity`: the signature and the structured body.
    pub fn functionEntity(out: *Writer, item: Value) !void {
        try out.writeByte('{');
        try signature(out, item);
        try out.writeAll(",\"body\":");
        try graph(out, model.property(item, "body").?, model.arrayItems(item, "typeParameters"));
        try out.writeByte('}');
    }
} else struct {};

test "typed declarations match tree encoding with provenance" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    model.useAllocator(a);
    const root = try model.parseValue(a,
        \\{"snapshotId":"s","types":[{"id":"t","displayName":"T","kind":"product","typeParameters":[{"typeParameterId":"tp","displayName":"A"}],"fields":[{"id":"field","displayName":"value","type":{"kind":"typeParameter","typeParameterId":"tp"}}]}],"effects":[{"id":"e","displayName":"Effect"}],"capabilities":[{"id":"c","displayName":"Cap","operations":[{"id":"o","displayName":"Op","parameters":[],"resultType":{"kind":"builtinGeneric","generic":"Option","arguments":[{"kind":"primitive","primitive":"unit"}]},"effectId":"e"}]}],"functions":[{"id":"f","displayName":"F","parameters":[],"resultType":{"kind":"named","typeId":"t","arguments":[{"kind":"primitive","primitive":"unit"}]},"requiredEffects":["e"],"body":{"nodes":[{"id":"b","kind":"bind","inputNodeId":"n","boundBody":{"valueBinding":{"id":"v","type":{"kind":"primitive","primitive":"unit"}},"body":{"nodes":[{"id":"x","kind":"unitLiteral"}],"resultNodeId":"x"}}},{"id":"n","kind":"unitLiteral"}],"resultNodeId":"b"}}],"obligations":[]}
    );
    var interner: tm.Interner = .{ .gpa = a };
    defer interner.deinit();
    const g = try tm.load(a, &interner, root, null);
    const provenance: Provenance = .{ .hidden = &.{"effect:e"}, .members = &.{.{ .id = "dep", .fingerprint = "fp", .keys = &.{"type:t"}, .path = "", .embedded = false }} };
    var expected: Writer.Allocating = .init(a);
    var actual: Writer.Allocating = .init(a);
    try Legacy.declarations(a, &expected.writer, root, provenance);
    try typedDeclarations(a, &actual.writer, &g, &interner, provenance);
    try std.testing.expectEqualStrings(expected.written(), actual.written());
    var legacy_graph: Writer.Allocating = .init(a);
    var typed_graph: Writer.Allocating = .init(a);
    try Legacy.functionEntity(&legacy_graph.writer, model.arrayItems(root, "functions")[0]);
    try typedFunctionEntity(a, &typed_graph.writer, &interner, g.functions[0]);
    try std.testing.expectEqualStrings(legacy_graph.written(), typed_graph.written());
    var old_summary: Writer.Allocating = .init(a);
    var new_summary: Writer.Allocating = .init(a);
    const f = model.arrayItems(root, "functions")[0];
    const nodes = model.bodyNodes(a, f);
    try Legacy.functionSummary(&old_summary.writer, f, nodes.len);
    try typedFunctionSummary(&new_summary.writer, &interner, g.functions[0], nodes.len);
    try std.testing.expectEqualStrings(old_summary.written(), new_summary.written());
    var old_nodes: Writer.Allocating = .init(a);
    var new_nodes: Writer.Allocating = .init(a);
    try Legacy.functionNodes(&old_nodes.writer, f, nodes);
    try typedFunctionNodes(&new_nodes.writer, &interner, g.functions[0], try g.functions[0].body.depthFirst(a));
    try std.testing.expectEqualStrings(old_nodes.written(), new_nodes.written());
}
