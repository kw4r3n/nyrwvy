//! The viewer data of a Function Focus page: `ViewerData.ofFocus` over `ViewerProjection.focus` of
//! `SemanticQuery.focusFunction`, written byte for byte as the removed F# host wrote it, and the page
//! that embeds it in the viewer asset.
const std = @import("std");
const model = @import("model_reader");
const wire = @import("wire.zig");
const query = @import("query.zig");
const asset = @import("viewer_asset");

const Value = model.Value;
const Str = model.Str;
const Writer = std.Io.Writer;
const Allocator = std.mem.Allocator;

/// Marks a page as viewer output.
pub const generator_meta = "<meta name=\"generator\" content=\"Nyrwvy viewer\">";

/// The asset with the data where it says `@DATA@`.
pub fn page(a: Allocator, data: []const u8) ![]const u8 {
    const index = std.mem.indexOf(u8, asset.page, "@DATA@").?;
    return std.mem.concat(a, u8, &.{ asset.page[0..index], data, asset.page[index + "@DATA@".len ..] });
}

/// A view's file form: the page for a person, or its data alone as JSON for an agent.
pub const Format = enum { html, json };

/// Marks a JSON view file as viewer output, as `generator_meta` marks a page.
pub const json_marker = "{\"generator\":\"Nyrwvy viewer\"";

/// The file of one view: the page with its data embedded, or the data itself behind the marker.
pub fn file(a: Allocator, format: Format, data: []const u8) ![]const u8 {
    return switch (format) {
        .html => page(a, data),
        .json => std.mem.concat(a, u8, &.{ json_marker, ",", data[1..], "\n" }),
    };
}

/// The bytes an existing file must start with to be replaced by a view of this format.
pub fn fileHeader(format: Format) []const u8 {
    return switch (format) {
        .html => header(),
        .json => json_marker,
    };
}

pub fn extension(format: Format) []const u8 {
    return switch (format) {
        .html => ".html",
        .json => ".json",
    };
}

pub fn mediaType(format: Format) []const u8 {
    return switch (format) {
        .html => "text/html; charset=utf-8",
        .json => "application/json",
    };
}

/// The page's bytes up to the end of its generator marker: an existing file is replaced only when it
/// starts with them (`ViewerFile.replaceable`).
pub fn header() []const u8 {
    const index = std.mem.indexOf(u8, asset.page, generator_meta).?;
    return asset.page[0 .. index + generator_meta.len];
}

fn isKind(kind: Str, comptime name: []const u8) bool {
    if (kind.len != name.len) return false;
    for (kind, name) |unit, c| if (unit != c) return false;
    return true;
}

fn lessStr(_: void, x: Str, y: Str) bool {
    return std.mem.order(u16, x, y) == .lt;
}

const Place = struct { property: []const u8, array: bool, role: []const u8 };

var dependency_roles: ?std.StringHashMapUnmanaged([]const Place) = null;

pub fn placesOf(kind: []const u8) []const Place {
    if (dependency_roles == null) {
        const a = std.heap.page_allocator;
        var table: std.StringHashMapUnmanaged([]const Place) = .empty;
        var lines = std.mem.tokenizeScalar(u8, @embedFile("dependency_roles.txt"), '\n');
        while (lines.next()) |line| {
            var parts = std.mem.splitScalar(u8, line, '|');
            const name = parts.next().?;
            var places: std.ArrayList(Place) = .empty;
            var entries = std.mem.tokenizeScalar(u8, parts.next().?, ' ');
            while (entries.next()) |entry| {
                const colon = std.mem.indexOfScalar(u8, entry, ':').?;
                const property = entry[0..colon];
                const array = std.mem.endsWith(u8, property, "[]");
                places.append(a, .{ .property = if (array) property[0 .. property.len - 2] else property, .array = array, .role = entry[colon + 1 ..] }) catch @panic("out of memory");
            }
            table.put(a, name, places.items) catch @panic("out of memory");
        }
        dependency_roles = table;
    }
    return dependency_roles.?.get(kind).?;
}

pub fn typedWriteDependencies(out: *Writer, node: model.Model, kind: []const u8) !void {
    try out.writeByte('[');
    var count: usize = 0;
    for (placesOf(kind)) |place| {
        const value = node.payload(place.property) orelse continue;
        switch (value) {
            .id, .optid => {
                const id = node.optionalReference(place.property) orelse continue;
                if (count > 0) try out.writeByte(',');
                count += 1;
                try out.print("{{\"role\":\"{s}\",\"nodeId\":", .{place.role});
                try model.writeString(out, id);
                try out.writeByte('}');
            },
            .ids => |ids| for (ids, 0..) |id, index| {
                if (count > 0) try out.writeByte(',');
                count += 1;
                try out.print("{{\"role\":\"{s}\",\"index\":{d},\"nodeId\":", .{ place.role, index });
                try model.writeString(out, node.context.interner.idText(.node, id));
                try out.writeByte('}');
            },
            .args, .ctx, .capargs, .fields => {
                const target, const key = if (std.mem.eql(u8, place.role, "FieldValue")) .{ "targetFieldId", "fieldId" } else if (std.mem.eql(u8, place.role, "CapabilityArgument")) .{ "targetCapabilityParameterId", "parameterId" } else .{ "targetParameterId", "parameterId" };
                const items = try node.context.allocator.dupe(model.Model, node.targets(place.property));
                std.mem.sort(model.Model, items, target, struct {
                    fn less(field: []const u8, left: model.Model, right: model.Model) bool {
                        return std.mem.order(u16, left.reference(field), right.reference(field)) == .lt;
                    }
                }.less);
                for (items) |item| {
                    if (count > 0) try out.writeByte(',');
                    count += 1;
                    try out.print("{{\"role\":\"{s}\",\"{s}\":", .{ place.role, key });
                    try model.writeString(out, item.reference(target));
                    try out.writeAll(",\"nodeId\":");
                    try model.writeString(out, item.reference("valueNodeId"));
                    try out.writeByte('}');
                }
            },
            else => unreachable,
        }
    }
    try out.writeByte(']');
}

// ---------------------------------------------------------------- graph

const TypedViewer = struct {
    a: Allocator,
    types: std.StringHashMapUnmanaged(?Value) = .empty,

    /// `TypedViewerProjection.role`.
    fn role(out: *Writer, node: model.Model, kind: Str) !void {
        if (isKind(kind, "stringLiteral")) {
            try out.writeAll("{\"kind\":\"stringLiteral\",\"value\":");
            try model.writeString(out, node.context.interner.stringText(node.payload("value").?.lit));
        } else if (isKind(kind, "boolLiteral")) {
            try out.writeAll(if (node.payload("value").?.bool) "{\"kind\":\"boolLiteral\",\"value\":true" else "{\"kind\":\"boolLiteral\",\"value\":false");
        } else if (isKind(kind, "int64Literal")) {
            try out.writeAll("{\"kind\":\"int64Literal\",\"value\":");
            try out.print("\"{d}\"", .{node.payload("value").?.int});
        } else if (isKind(kind, "float64Literal")) {
            try out.writeAll("{\"kind\":\"float64Literal\",\"bits\":");
            try out.print("\"{x:0>16}\"", .{node.payload("bits").?.bits});
        } else if (isKind(kind, "taskWait")) {
            try out.writeAll("{\"kind\":\"taskWait\",\"mode\":");
            try out.print("\"{s}\"", .{@tagName(node.payload("mode").?.mode)});
        } else if (isKind(kind, "capabilityConstruct")) {
            try out.writeAll("{\"kind\":\"capabilityConstruct\",\"capabilityId\":");
            try model.writeString(out, node.reference("capabilityId"));
            try out.writeAll(",\"implementations\":[");
            for (node.implementations(), 0..) |item, index| {
                if (index > 0) try out.writeByte(',');
                try out.writeAll("{\"operationId\":");
                try model.writeString(out, item.reference("operationId"));
                try out.writeAll(",\"functionId\":");
                try model.writeString(out, item.reference("functionId"));
                try out.writeByte('}');
            }
            try out.writeByte(']');
        } else {
            // Every other kind by its `SemanticNodeKind.name`: the wire kind with its first letter raised.
            try out.writeAll("{\"kind\":\"operation\",\"operation\":\"");
            try out.writeByte(std.ascii.toUpper(@intCast(kind[0])));
            for (kind[1..]) |unit| try out.writeByte(@intCast(unit));
            try out.writeByte('"');
        }
        try out.writeByte('}');
    }

    /// `semanticRelations`, then the functions the node names (distinct), without `DependsOn`.
    fn relations(self: *TypedViewer, out: *Writer, node: model.Model, kind: Str) !void {
        try out.writeByte('[');
        var count: usize = 0;
        const semantic: ?struct { []const u8, []const u8 } =
            if (isKind(kind, "parameterReference")) .{ "ReadsParameter", "parameterId" } else if (isKind(kind, "patternReference")) .{ "ReadsPatternBinding", "patternBindingId" } else if (isKind(kind, "capabilityInvoke")) .{ "InvokesCapabilityOperation", "operationId" } else if (isKind(kind, "productConstruct") or isKind(kind, "productUpdate")) .{ "ConstructsType", "typeId" } else if (isKind(kind, "fieldAccess")) .{ "AccessesField", "fieldId" } else if (isKind(kind, "unionConstruct")) .{ "ConstructsVariant", "variantId" } else if (isKind(kind, "conditional")) .{ "BranchCondition", "conditionNodeId" } else if (isKind(kind, "matchUnion") or isKind(kind, "matchOption") or isKind(kind, "matchResult")) .{ "MatchInput", "inputNodeId" } else if (isKind(kind, "bind")) .{ "BindInput", "inputNodeId" } else null;
        if (semantic) |relation| {
            try out.print("{{\"kind\":\"{s}\",\"id\":", .{relation[0]});
            try model.writeString(out, node.reference(relation[1]));
            try out.writeByte('}');
            count += 1;
        }
        const functions = model.typedFunctionRelationsOf(node);
        for (functions, 0..) |function, index| {
            const repeated = for (functions[0..index]) |earlier| {
                if (earlier.invoked == function.invoked and std.mem.eql(u16, earlier.id, function.id)) break true;
            } else false;
            if (repeated) continue;
            if (count > 0) try out.writeByte(',');
            count += 1;
            try out.writeAll(if (function.invoked) "{\"kind\":\"CallsFunction\",\"id\":" else "{\"kind\":\"ReferencesFunction\",\"id\":");
            try model.writeString(out, function.id);
            try out.writeByte('}');
        }
        _ = self;
        try out.writeByte(']');
    }

    fn binding(out: *Writer, value: model.Model, source_field: ?Str) !void {
        try out.writeAll("{\"id\":");
        try model.writeString(out, value.id());
        try out.writeAll(",\"displayName\":");
        if (value.optionalDisplayName()) |name| try model.writeString(out, name) else try out.writeAll("null");
        try out.writeAll(",\"type\":");
        try wire.typedTypeRef(out, value.context.interner, value.data.binding.type, &.{});
        try out.writeAll(",\"sourceFieldId\":");
        if (source_field) |field| try model.writeString(out, field) else try out.writeAll("null");
        try out.writeByte('}');
    }

    fn branch(self: *TypedViewer, out: *Writer, first: *bool, role_name: []const u8, bindings: []const model.Model, graph_value: model.Model) !void {
        if (!first.*) try out.writeByte(',');
        first.* = false;
        try out.print("{{\"role\":\"{s}\",\"bindings\":[", .{role_name});
        for (bindings, 0..) |item, index| {
            if (index > 0) try out.writeByte(',');
            try binding(out, item, null);
        }
        try out.writeAll("],\"graph\":");
        try self.graph(out, graph_value);
        try out.writeByte('}');
    }

    /// The node's nested scopes as `TypedViewerBranch`es, union arms in stable ID order.
    fn branches(self: *TypedViewer, out: *Writer, node: model.Model, kind: Str) !void {
        try out.writeByte('[');
        var first = true;
        if (isKind(kind, "conditional")) {
            try self.branch(out, &first, "WhenTrue", &.{}, node.graphPayload("whenTrue"));
            try self.branch(out, &first, "WhenFalse", &.{}, node.graphPayload("whenFalse"));
        } else if (isKind(kind, "boolAnd") or isKind(kind, "boolOr")) {
            try self.branch(out, &first, "RightOperand", &.{}, node.graphPayload("right"));
        } else if (isKind(kind, "matchUnion")) {
            const arms = try self.a.dupe(model.Model, node.arms());
            std.mem.sort(model.Model, arms, {}, struct {
                fn minimum(arm: model.Model) Str {
                    const variants = arm.variantIds();
                    var result = variants[0].literal();
                    for (variants[1..]) |variant| {
                        const id = variant.literal();
                        if (std.mem.order(u16, id, result) == .lt) result = id;
                    }
                    return result;
                }
                fn less(_: void, x: model.Model, y: model.Model) bool {
                    return std.mem.order(u16, minimum(x), minimum(y)) == .lt;
                }
            }.less);
            for (arms) |arm| {
                if (!first) try out.writeByte(',');
                first = false;
                try out.writeAll("{\"role\":\"UnionVariant\",\"variantIds\":");
                try out.writeByte('[');
                const variants = try self.a.dupe(model.Model, arm.variantIds());
                std.mem.sort(model.Model, variants, {}, struct {
                    fn less(_: void, x: model.Model, y: model.Model) bool {
                        return std.mem.order(u16, x.literal(), y.literal()) == .lt;
                    }
                }.less);
                for (variants, 0..) |variant, index| {
                    if (index > 0) try out.writeByte(',');
                    try model.writeString(out, variant.literal());
                }
                try out.writeByte(']');
                try out.writeAll(",\"bindings\":[");
                const fields = try self.a.dupe(model.Model, arm.fieldBindings());
                std.mem.sort(model.Model, fields, {}, struct {
                    fn less(_: void, x: model.Model, y: model.Model) bool {
                        return std.mem.order(u16, x.reference("sourceFieldId"), y.reference("sourceFieldId")) == .lt;
                    }
                }.less);
                for (fields, 0..) |field, index| {
                    if (index > 0) try out.writeByte(',');
                    try binding(out, field.boundBinding(), field.reference("sourceFieldId"));
                }
                try out.writeAll("],\"graph\":");
                try self.graph(out, arm.body());
                try out.writeByte('}');
            }
        } else if (isKind(kind, "bind")) {
            const body = node.arm("boundBody");
            try self.branch(out, &first, "BindBody", &.{body.armBinding()}, body.body());
        } else if (isKind(kind, "matchOption")) {
            const some = node.arm("someArm");
            try self.branch(out, &first, "OptionSome", &.{some.armBinding()}, some.body());
            try self.branch(out, &first, "OptionNone", &.{}, node.graphPayload("noneBody"));
        } else if (isKind(kind, "matchResult")) {
            const ok_arm, const err_arm = .{ node.arm("okArm"), node.arm("errArm") };
            try self.branch(out, &first, "ResultOk", &.{ok_arm.armBinding()}, ok_arm.body());
            try self.branch(out, &first, "ResultErr", &.{err_arm.armBinding()}, err_arm.body());
        }
        try out.writeByte(']');
    }

    /// `projectGraph`: the nodes in stable ID order (the model's order).
    fn graph(self: *TypedViewer, out: *Writer, value: model.Model) anyerror!void {
        try out.writeAll("{\"resultNodeId\":");
        try model.writeString(out, value.resultNodeId());
        try out.writeAll(",\"nodes\":[");
        const nodes = try self.a.dupe(model.Model, value.nodes());
        std.mem.sort(model.Model, nodes, {}, struct {
            fn less(_: void, x: model.Model, y: model.Model) bool {
                return std.mem.order(u16, x.id(), y.id()) == .lt;
            }
        }.less);
        for (nodes, 0..) |node, index| {
            if (index > 0) try out.writeByte(',');
            const kind = node.kind();
            const node_id = node.id();
            try out.writeAll("{\"id\":");
            try model.writeString(out, node_id);
            try out.writeAll(",\"type\":");
            if (self.types.get(model.utf8(node_id)) orelse null) |t| try wire.typeRef(out, t, &.{}) else try out.writeAll("null");
            try out.writeAll(",\"role\":");
            try role(out, node, kind);
            try out.writeAll(",\"relations\":");
            try self.relations(out, node, kind);
            try out.writeAll(",\"valueDependencies\":");
            try typedWriteDependencies(out, node, model.utf8(kind));
            try out.writeAll(",\"branches\":");
            try self.branches(out, node, kind);
            try out.writeByte('}');
        }
        try out.writeAll("]}");
    }
};

// ---------------------------------------------------------------- declarations

fn has(items: []const Str, id: Str) bool {
    for (items) |item| if (std.mem.eql(u16, item, id)) return true;
    return false;
}

fn writeSorted(a: Allocator, out: *Writer, ids: []const Str) !void {
    const sorted = try a.dupe(Str, ids);
    std.mem.sort(Str, sorted, {}, lessStr);
    try query.writeIds(out, sorted);
}

const TypedClosure = struct {
    a: Allocator,
    root: model.Model,
    types: std.ArrayList(Str) = .empty,
    capabilities: std.ArrayList(Str) = .empty,
    effects: std.ArrayList(Str) = .empty,
    missing_types: std.ArrayList(Str) = .empty,
    missing_capabilities: std.ArrayList(Str) = .empty,
    fn addEffect(self: *TypedClosure, id: Str) !void {
        if (!has(self.effects.items, id)) try self.effects.append(self.a, id);
    }
    fn named(self: *TypedClosure, id: Str) anyerror!void {
        if (has(self.types.items, id)) return;
        try self.types.append(self.a, id);
        const declaration = for (self.root.types()) |item| {
            if (std.mem.eql(u16, item.id(), id)) break item;
        } else return self.missing_types.append(self.a, id);
        for (declaration.fields()) |field| try self.visit(field.data.slot.value.type);
        for (declaration.variants()) |variant| for (variant.fields()) |field| try self.visit(field.data.slot.value.type);
    }
    fn capability(self: *TypedClosure, id: Str) anyerror!void {
        if (has(self.capabilities.items, id)) return;
        try self.capabilities.append(self.a, id);
        const declaration = for (self.root.capabilities()) |item| {
            if (std.mem.eql(u16, item.id(), id)) break item;
        } else return self.missing_capabilities.append(self.a, id);
        for (declaration.operations()) |operation| {
            try self.addEffect(operation.reference("effectId"));
            for (operation.parameters()) |parameter| try self.visit(parameter.data.slot.value.type);
            try self.visit(operation.data.operation.result);
            if (operation.data.operation.failure) |failure| try self.visit(failure);
        }
    }
    fn visit(self: *TypedClosure, t: model.session_model.TypeRef) anyerror!void {
        switch (t) {
            .named => |v| {
                if (v.arguments) |args| for (args) |arg| try self.visit(arg);
                try self.named(self.root.context.interner.idText(.type, v.id));
            },
            .capability => |v| try self.capability(self.root.context.interner.idText(.capability, v)),
            .function => |v| {
                for (v.parameters) |p| try self.visit(p);
                try self.visit(v.result.*);
            },
            .generic => |v| for (v.arguments) |arg| try self.visit(arg),
            else => {},
        }
    }
    fn visitInferred(self: *TypedClosure, t: model.SemanticType) anyerror!void {
        switch (t.kind) {
            .function => {
                for (t.args) |arg| try self.visitInferred(arg);
                try self.visitInferred(t.result.?.*);
            },
            .generic => for (t.args) |arg| try self.visitInferred(arg),
            .named => {
                for (t.args) |arg| try self.visitInferred(arg);
                try self.named(t.name);
            },
            .capability => try self.capability(t.name),
            else => {},
        }
    }
};

fn typedSignature(out: *Writer, function: model.Model, with_exported: bool) !void {
    const i = function.context.interner;
    const f = function.data.function;
    try out.writeAll("{\"id\":");
    try model.writeString(out, function.id());
    try out.writeAll(",\"displayName\":");
    try model.writeString(out, function.displayName());
    if (with_exported) try out.writeAll(if (f.exported) ",\"exported\":true" else ",\"exported\":false");
    try out.writeAll(",\"typeParameters\":[");
    for (f.type_parameters orelse &.{}, 0..) |parameter, n| {
        if (n > 0) try out.writeByte(',');
        try out.writeAll("{\"typeParameterId\":");
        try model.writeString(out, i.idText(.type_parameter, parameter.id));
        try out.writeAll(",\"displayName\":");
        try model.writeString(out, i.stringText(parameter.name));
        try out.writeByte('}');
    }
    try out.writeAll("],\"parameters\":[");
    for (f.parameters, 0..) |parameter, n| {
        if (n > 0) try out.writeByte(',');
        try out.writeAll("{\"id\":");
        try model.writeString(out, i.idText(.parameter, parameter.id));
        try out.writeAll(",\"displayName\":");
        try model.writeString(out, i.stringText(parameter.name));
        try out.writeAll(",\"type\":");
        try wire.typedTypeRef(out, i, parameter.type, &.{});
        try out.writeByte('}');
    }
    try out.writeAll("],\"resultType\":");
    try wire.typedTypeRef(out, i, f.result, &.{});
    try out.writeAll(",\"requiredEffects\":[");
    for (f.effects, 0..) |effect, n| {
        if (n > 0) try out.writeByte(',');
        try model.writeString(out, i.idText(.effect, effect));
    }
    try out.writeAll("]}");
}

pub fn typedFocusData(a: Allocator, root: model.Model, snapshot_id: []const u8, function: model.Model) ![]const u8 {
    return std.mem.concat(a, u8, &.{ "{\"view\":\"functionFocus\",\"focus\":", try typedFocusValue(a, root, snapshot_id, function), "}" });
}
pub fn typedFocusValue(a: Allocator, root: model.Model, snapshot_id: []const u8, function: model.Model) ![]const u8 {
    return typedFocusValueOf(a, root, snapshot_id, function, false);
}
/// One function's focus. `shared` leaves out the related signatures and referenced declarations, which
/// a program view lists once for every function.
pub fn typedFocusValueOf(a: Allocator, root: model.Model, snapshot_id: []const u8, function: model.Model, shared: bool) ![]const u8 {
    const wanted = function.id();
    const focus = model.typedFocusOf(a, root, function);
    var data: Writer.Allocating = .init(a);
    const out = &data.writer;
    if (!shared) {
        try out.writeAll("{\"snapshotId\":");
        try model.writeString(out, try std.unicode.utf8ToUtf16LeAlloc(a, snapshot_id));
        try out.writeAll(",\"function\":");
    } else try out.writeAll("{\"function\":");
    try typedSignature(out, function, true);
    try out.writeAll(if (function.mustNotWait()) ",\"mustNotWait\":true,\"waitPath\":" else ",\"mustNotWait\":false,\"waitPath\":");
    if (focus.wait_path) |path| try model.writeString(out, path) else try out.writeAll("null");
    try out.writeAll(",\"bodyEffectRequirements\":");
    try query.writeRequirements(out, focus.requirements);
    try out.writeAll(",\"aborts\":");
    try query.typedWriteAborts(out, focus.aborts, wanted);
    try out.writeAll(",\"callers\":");
    try query.writeSites(out, focus.callers);
    try out.writeAll(",\"callees\":");
    try query.writeSites(out, focus.callees);
    var related_ids: std.ArrayList(Str) = .empty;
    for (focus.callers) |site| if (!has(related_ids.items, site.caller)) try related_ids.append(a, site.caller);
    for (focus.callees) |site| if (!has(related_ids.items, site.callee)) try related_ids.append(a, site.callee);
    var others: std.ArrayList(Str) = .empty;
    for (related_ids.items) |id| if (!std.mem.eql(u16, id, wanted)) try others.append(a, id);
    std.mem.sort(Str, others.items, {}, lessStr);
    var related: std.ArrayList(model.Model) = .empty;
    var missing_functions: std.ArrayList(Str) = .empty;
    for (others.items) |id| {
        const found = for (root.functions()) |item| {
            if (std.mem.eql(u16, item.id(), id)) break item;
        } else null;
        if (found) |item| try related.append(a, item) else try missing_functions.append(a, id);
    }
    if (!shared) {
        try out.writeAll(",\"relatedFunctionSignatures\":[");
        for (related.items, 0..) |item, n| {
            if (n > 0) try out.writeByte(',');
            try typedSignature(out, item, false);
        }
        try out.writeByte(']');
    }
    const nodes = model.typedBodyNodes(a, function);
    const Obligation = struct { id: Str, item: model.Model };
    var open: std.ArrayList(Obligation) = .empty;
    for (root.obligations()) |obligation| {
        if (obligation.data.obligation.resolution != null) continue;
        const call = obligation.reference("callNodeId");
        for (nodes) |node| if (std.mem.eql(u16, node.id(), call)) {
            try open.append(a, .{ .id = obligation.id(), .item = obligation });
            break;
        };
    }
    std.mem.sort(Obligation, open.items, {}, struct {
        fn less(_: void, x: Obligation, y: Obligation) bool {
            return std.mem.order(u16, x.id, y.id) == .lt;
        }
    }.less);
    try out.writeAll(",\"openObligations\":[");
    for (open.items, 0..) |obligation, n| {
        if (n > 0) try out.writeByte(',');
        try out.writeAll("{\"obligationId\":");
        try model.writeString(out, obligation.id);
        inline for (.{ "callNodeId", "parameterId", "originTransactionId" }) |name| {
            try out.writeAll(",\"" ++ name ++ "\":");
            try model.writeString(out, if (comptime std.mem.eql(u8, name, "originTransactionId")) root.context.interner.idText(.transaction, obligation.item.data.obligation.origin) else obligation.item.reference(name));
        }
        try out.writeByte('}');
    }
    var closure: TypedClosure = .{ .a = a, .root = root };
    const i = root.context.interner;
    for (function.data.function.effects) |e| try closure.addEffect(i.idText(.effect, e));
    for (related.items) |item| for (item.data.function.effects) |e| try closure.addEffect(i.idText(.effect, e));
    for (function.data.function.parameters) |p| try closure.visit(p.type);
    try closure.visit(function.data.function.result);
    for (focus.node_types) |item| if (item.type) |t| try closure.visitInferred(t);
    for (related.items) |item| {
        for (item.data.function.parameters) |p| try closure.visit(p.type);
        try closure.visit(item.data.function.result);
    }
    try out.writeByte(']');
    var count: usize = 0;
    if (!shared) {
        try out.writeAll(",\"referencedTypes\":[");
        for (root.types()) |item| if (has(closure.types.items, item.id())) {
            if (count > 0) try out.writeByte(',');
            count += 1;
            try wire.typedSemanticType(out, i, item.data.type_declaration);
        };
        try out.writeAll("],\"referencedCapabilities\":[");
        count = 0;
        for (root.capabilities()) |item| if (has(closure.capabilities.items, item.id())) {
            if (count > 0) try out.writeByte(',');
            count += 1;
            try wire.typedCapability(out, i, item.data.capability);
        };
        try out.writeByte(']');
    }
    var missing_effects: std.ArrayList(Str) = .empty;
    for (closure.effects.items) |id| {
        const known = for (root.effects()) |item| {
            if (std.mem.eql(u16, item.id(), id)) break true;
        } else false;
        if (!known) try missing_effects.append(a, id);
    }
    if (!shared) {
        try out.writeAll(",\"referencedEffects\":[");
        count = 0;
        for (root.effects()) |item| if (has(closure.effects.items, item.id())) {
            if (count > 0) try out.writeByte(',');
            count += 1;
            try writeEffect(out, item);
        };
        try out.writeByte(']');
    }
    try out.writeAll(",\"missingFunctionIds\":");
    try query.writeIds(out, missing_functions.items);
    try out.writeAll(",\"missingTypeIds\":");
    try writeSorted(a, out, closure.missing_types.items);
    try out.writeAll(",\"missingCapabilityIds\":");
    try writeSorted(a, out, closure.missing_capabilities.items);
    try out.writeAll(",\"missingEffectIds\":");
    try writeSorted(a, out, missing_effects.items);
    var viewer: TypedViewer = .{ .a = a };
    for (focus.node_types) |item| try viewer.types.put(a, model.utf8(item.id), if (item.type) |ty| model.semanticTypeValue(ty) else null);
    try out.writeAll(",\"graph\":");
    try viewer.graph(out, function.body());
    try out.writeByte('}');
    return data.written();
}

fn writeEffect(out: *Writer, item: model.Model) !void {
    try out.writeAll("{\"id\":");
    try model.writeString(out, item.id());
    try out.writeAll(",\"displayName\":");
    try model.writeString(out, item.displayName());
    try out.writeAll(if (item.exported()) ",\"exported\":true}" else ",\"exported\":false}");
}

/// The program view: every declaration once, then each function's shared focus, which refers to them by ID.
pub fn typedProgramData(a: Allocator, root: model.Model, snapshot_id: []const u8) ![]const u8 {
    var data: Writer.Allocating = .init(a);
    const out = &data.writer;
    const i = root.context.interner;
    try out.writeAll("{\"view\":\"program\",\"snapshotId\":");
    try model.writeString(out, try std.unicode.utf8ToUtf16LeAlloc(a, snapshot_id));
    try out.writeAll(",\"types\":[");
    for (root.types(), 0..) |item, n| {
        if (n > 0) try out.writeByte(',');
        try wire.typedSemanticType(out, i, item.data.type_declaration);
    }
    try out.writeAll("],\"capabilities\":[");
    for (root.capabilities(), 0..) |item, n| {
        if (n > 0) try out.writeByte(',');
        try wire.typedCapability(out, i, item.data.capability);
    }
    try out.writeAll("],\"effects\":[");
    for (root.effects(), 0..) |item, n| {
        if (n > 0) try out.writeByte(',');
        try writeEffect(out, item);
    }
    try out.writeAll("],\"functions\":[");
    for (root.functions(), 0..) |item, n| {
        if (n > 0) try out.writeByte(',');
        try out.writeAll(try typedFocusValueOf(a, root, snapshot_id, item, true));
    }
    try out.writeAll("]}");
    return data.written();
}
