//! The viewer data of a Function Focus page: `ViewerData.ofFocus` over `ViewerProjection.focus` of
//! `SemanticQuery.focusFunction`, written byte for byte as the F# host writes it, and the page that
//! embeds it in the viewer asset (`ViewerHtml.page`).
const std = @import("std");
const model = @import("model_reader");
const wire = @import("wire.zig");
const query = @import("query.zig");
const asset = @import("viewer_asset");

const Value = model.Value;
const Str = model.Str;
const Writer = std.Io.Writer;
const Allocator = std.mem.Allocator;

/// Marks a page as viewer output (`ViewerHtml.generatorMeta`).
pub const generator_meta = "<meta name=\"generator\" content=\"Nyrwvy viewer\">";

/// `ViewerHtml.page`: the asset with the data where it says `@DATA@`.
pub fn page(a: Allocator, data: []const u8) ![]const u8 {
    const index = std.mem.indexOf(u8, asset.page, "@DATA@").?;
    return std.mem.concat(a, u8, &.{ asset.page[0..index], data, asset.page[index + "@DATA@".len ..] });
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

fn decodedAt(item: Value, name: []const u8) Str {
    return model.decoded(model.property(item, name).?);
}

fn same(item: Value, id: Str) bool {
    return std.mem.eql(u16, decodedAt(item, "id"), id);
}

fn lessStr(_: void, x: Str, y: Str) bool {
    return std.mem.order(u16, x, y) == .lt;
}

fn writeValues(out: *Writer, items: []const Value) !void {
    try out.writeByte('[');
    for (items, 0..) |item, index| {
        if (index > 0) try out.writeByte(',');
        try model.writeString(out, model.decoded(item));
    }
    try out.writeByte(']');
}

// ---------------------------------------------------------------- dependency roles

/// One operand place of a node kind (dependency_roles.txt): its property, whether it is an array
/// (of node IDs or of bindings whose `valueNodeId` is the operand), and its `ValueDependencyRole`.
const Place = struct { property: []const u8, array: bool, role: []const u8 };

var dependency_roles: ?std.StringHashMapUnmanaged([]const Place) = null;

fn placesOf(kind: []const u8) []const Place {
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

/// `ValueDependency.ofNode`, each as `dependencyRole` and the operand's `nodeId`.
fn writeDependencies(out: *Writer, node: Value, kind: []const u8) !void {
    try out.writeByte('[');
    var count: usize = 0;
    for (placesOf(kind)) |place| {
        const value = model.property(node, place.property) orelse continue;
        const items: []const Value = if (place.array) value.array else &.{value};
        for (items, 0..) |item, index| {
            if (count > 0) try out.writeByte(',');
            count += 1;
            try out.print("{{\"role\":\"{s}\"", .{place.role});
            if (item == .string) {
                if (place.array) try out.print(",\"index\":{d}", .{index});
                try out.writeAll(",\"nodeId\":");
                try model.writeString(out, model.decoded(item));
            } else {
                const target, const key = if (std.mem.eql(u8, place.role, "FieldValue")) .{ "targetFieldId", "fieldId" } else if (std.mem.eql(u8, place.role, "CapabilityArgument")) .{ "targetCapabilityParameterId", "parameterId" } else .{ "targetParameterId", "parameterId" };
                try out.print(",\"{s}\":", .{key});
                try model.writeString(out, decodedAt(item, target));
                try out.writeAll(",\"nodeId\":");
                try model.writeString(out, decodedAt(item, "valueNodeId"));
            }
            try out.writeByte('}');
        }
    }
    try out.writeByte(']');
}

// ---------------------------------------------------------------- graph

const Viewer = struct {
    a: Allocator,
    types: std.StringHashMapUnmanaged(?Value) = .empty,

    /// `ViewerProjection.role`.
    fn role(out: *Writer, node: Value, kind: Str) !void {
        if (isKind(kind, "stringLiteral")) {
            try out.writeAll("{\"kind\":\"stringLiteral\",\"value\":");
            try model.writeString(out, decodedAt(node, "value"));
        } else if (isKind(kind, "boolLiteral")) {
            try out.writeAll(if (model.property(node, "value").? == .json_true) "{\"kind\":\"boolLiteral\",\"value\":true" else "{\"kind\":\"boolLiteral\",\"value\":false");
        } else if (isKind(kind, "int64Literal")) {
            try out.writeAll("{\"kind\":\"int64Literal\",\"value\":");
            switch (model.property(node, "value").?) {
                .number => |digits| try out.print("\"{s}\"", .{digits}),
                else => |value| try model.writeString(out, model.decoded(value)),
            }
        } else if (isKind(kind, "float64Literal")) {
            try out.writeAll("{\"kind\":\"float64Literal\",\"bits\":");
            try model.writeString(out, decodedAt(node, "bits"));
        } else if (isKind(kind, "taskWait")) {
            try out.writeAll("{\"kind\":\"taskWait\",\"mode\":");
            try model.writeString(out, decodedAt(node, "mode"));
        } else if (isKind(kind, "capabilityConstruct")) {
            try out.writeAll("{\"kind\":\"capabilityConstruct\",\"capabilityId\":");
            try model.writeString(out, decodedAt(node, "capabilityId"));
            try out.writeAll(",\"implementations\":[");
            for (model.arrayItems(node, "implementations"), 0..) |item, index| {
                if (index > 0) try out.writeByte(',');
                try out.writeAll("{\"operationId\":");
                try model.writeString(out, decodedAt(item, "operationId"));
                try out.writeAll(",\"functionId\":");
                try model.writeString(out, decodedAt(item, "functionId"));
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
    fn relations(self: *Viewer, out: *Writer, node: Value, kind: Str) !void {
        try out.writeByte('[');
        var count: usize = 0;
        const semantic: ?struct { []const u8, []const u8 } =
            if (isKind(kind, "parameterReference")) .{ "ReadsParameter", "parameterId" }
            else if (isKind(kind, "patternReference")) .{ "ReadsPatternBinding", "patternBindingId" }
            else if (isKind(kind, "capabilityInvoke")) .{ "InvokesCapabilityOperation", "operationId" }
            else if (isKind(kind, "productConstruct") or isKind(kind, "productUpdate")) .{ "ConstructsType", "typeId" }
            else if (isKind(kind, "fieldAccess")) .{ "AccessesField", "fieldId" }
            else if (isKind(kind, "unionConstruct")) .{ "ConstructsVariant", "variantId" }
            else if (isKind(kind, "conditional")) .{ "BranchCondition", "conditionNodeId" }
            else if (isKind(kind, "matchUnion") or isKind(kind, "matchOption") or isKind(kind, "matchResult")) .{ "MatchInput", "inputNodeId" }
            else if (isKind(kind, "bind")) .{ "BindInput", "inputNodeId" }
            else null;
        if (semantic) |relation| {
            try out.print("{{\"kind\":\"{s}\",\"id\":", .{relation[0]});
            try model.writeString(out, decodedAt(node, relation[1]));
            try out.writeByte('}');
            count += 1;
        }
        const functions = model.functionRelationsOf(node);
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

    fn binding(out: *Writer, value: Value, source_field: ?Value) !void {
        try out.writeAll("{\"id\":");
        try model.writeString(out, decodedAt(value, "id"));
        try out.writeAll(",\"displayName\":");
        if (model.property(value, "displayName")) |name| try model.writeString(out, model.decoded(name)) else try out.writeAll("null");
        try out.writeAll(",\"type\":");
        try wire.typeRef(out, model.property(value, "type").?, &.{});
        try out.writeAll(",\"sourceFieldId\":");
        if (source_field) |field| try model.writeString(out, model.decoded(field)) else try out.writeAll("null");
        try out.writeByte('}');
    }

    fn branch(self: *Viewer, out: *Writer, first: *bool, role_name: []const u8, bindings: []const Value, graph_value: Value) !void {
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

    /// The node's nested scopes as `ViewerBranch`es, union arms in stable ID order.
    fn branches(self: *Viewer, out: *Writer, node: Value, kind: Str) !void {
        try out.writeByte('[');
        var first = true;
        const get = model.property;
        if (isKind(kind, "conditional")) {
            try self.branch(out, &first, "WhenTrue", &.{}, get(node, "whenTrue").?);
            try self.branch(out, &first, "WhenFalse", &.{}, get(node, "whenFalse").?);
        } else if (isKind(kind, "boolAnd") or isKind(kind, "boolOr")) {
            try self.branch(out, &first, "RightOperand", &.{}, get(node, "right").?);
        } else if (isKind(kind, "matchUnion")) {
            for (model.arrayItems(node, "arms")) |arm| {
                if (!first) try out.writeByte(',');
                first = false;
                try out.writeAll("{\"role\":\"UnionVariant\",\"variantIds\":");
                if (get(arm, "variantId")) |variant| try writeValues(out, &.{variant}) else try writeValues(out, model.arrayItems(arm, "variantIds"));
                try out.writeAll(",\"bindings\":[");
                for (model.arrayItems(arm, "fieldBindings"), 0..) |field, index| {
                    if (index > 0) try out.writeByte(',');
                    try binding(out, get(field, "binding").?, get(field, "sourceFieldId").?);
                }
                try out.writeAll("],\"graph\":");
                try self.graph(out, get(arm, "body").?);
                try out.writeByte('}');
            }
        } else if (isKind(kind, "bind")) {
            const body = get(node, "boundBody").?;
            try self.branch(out, &first, "BindBody", &.{get(body, "valueBinding").?}, get(body, "body").?);
        } else if (isKind(kind, "matchOption")) {
            const some = get(node, "someArm").?;
            try self.branch(out, &first, "OptionSome", &.{get(some, "valueBinding").?}, get(some, "body").?);
            try self.branch(out, &first, "OptionNone", &.{}, get(node, "noneBody").?);
        } else if (isKind(kind, "matchResult")) {
            const ok_arm, const err_arm = .{ get(node, "okArm").?, get(node, "errArm").? };
            try self.branch(out, &first, "ResultOk", &.{get(ok_arm, "valueBinding").?}, get(ok_arm, "body").?);
            try self.branch(out, &first, "ResultErr", &.{get(err_arm, "errorBinding").?}, get(err_arm, "body").?);
        }
        try out.writeByte(']');
    }

    /// `projectGraph`: the nodes in stable ID order (the model's order).
    fn graph(self: *Viewer, out: *Writer, value: Value) anyerror!void {
        try out.writeAll("{\"resultNodeId\":");
        try model.writeString(out, decodedAt(value, "resultNodeId"));
        try out.writeAll(",\"nodes\":[");
        for (model.arrayItems(value, "nodes"), 0..) |node, index| {
            if (index > 0) try out.writeByte(',');
            const kind = decodedAt(node, "kind");
            const node_id = decodedAt(node, "id");
            try out.writeAll("{\"id\":");
            try model.writeString(out, node_id);
            try out.writeAll(",\"type\":");
            if (self.types.get(model.utf8(node_id)) orelse null) |t| try wire.typeRef(out, t, &.{}) else try out.writeAll("null");
            try out.writeAll(",\"role\":");
            try role(out, node, kind);
            try out.writeAll(",\"relations\":");
            try self.relations(out, node, kind);
            try out.writeAll(",\"valueDependencies\":");
            try writeDependencies(out, node, model.utf8(kind));
            try out.writeAll(",\"branches\":");
            try self.branches(out, node, kind);
            try out.writeByte('}');
        }
        try out.writeAll("]}");
    }
};

// ---------------------------------------------------------------- declarations

/// `declarationClosure`: the types and capabilities the seed types reach, and the effects of the
/// seeds and of those capabilities' operations, with the IDs the model does not declare.
const Closure = struct {
    a: Allocator,
    root: Value,
    types: std.ArrayList(Str) = .empty,
    capabilities: std.ArrayList(Str) = .empty,
    effects: std.ArrayList(Str) = .empty,
    missing_types: std.ArrayList(Str) = .empty,
    missing_capabilities: std.ArrayList(Str) = .empty,

    fn has(list: []const Str, id: Str) bool {
        for (list) |item| if (std.mem.eql(u16, item, id)) return true;
        return false;
    }

    fn addEffect(self: *Closure, id: Str) !void {
        if (!has(self.effects.items, id)) try self.effects.append(self.a, id);
    }

    fn find(self: *Closure, group: []const u8, id: Str) ?Value {
        for (model.arrayItems(self.root, group)) |item| if (same(item, id)) return item;
        return null;
    }

    fn visit(self: *Closure, type_ref: Value) anyerror!void {
        const kind = decodedAt(type_ref, "kind");
        if (isKind(kind, "function")) {
            for (model.arrayItems(type_ref, "parameters")) |item| try self.visit(item);
            try self.visit(model.property(type_ref, "resultType").?);
        } else if (isKind(kind, "builtinGeneric")) {
            for (model.arrayItems(type_ref, "arguments")) |item| try self.visit(item);
        } else if (isKind(kind, "named")) {
            for (model.arrayItems(type_ref, "arguments")) |item| try self.visit(item);
            const id = decodedAt(type_ref, "typeId");
            if (has(self.types.items, id)) return;
            try self.types.append(self.a, id);
            const declaration = self.find("types", id) orelse return self.missing_types.append(self.a, id);
            for (model.arrayItems(declaration, "fields")) |field| try self.visit(model.property(field, "type").?);
            for (model.arrayItems(declaration, "variants")) |variant| {
                for (model.arrayItems(variant, "fields")) |field| try self.visit(model.property(field, "type").?);
            }
        } else if (isKind(kind, "capability")) {
            const id = decodedAt(type_ref, "capabilityId");
            if (has(self.capabilities.items, id)) return;
            try self.capabilities.append(self.a, id);
            const capability = self.find("capabilities", id) orelse return self.missing_capabilities.append(self.a, id);
            for (model.arrayItems(capability, "operations")) |operation| {
                try self.addEffect(decodedAt(operation, "effectId"));
                for (model.arrayItems(operation, "parameters")) |parameter| try self.visit(model.property(parameter, "type").?);
                try self.visit(model.property(operation, "resultType").?);
                if (model.property(operation, "failureType")) |failure| try self.visit(failure);
            }
        }
    }
};

fn writeTypeParameters(out: *Writer, item: Value) !void {
    try out.writeByte('[');
    for (model.arrayItems(item, "typeParameters"), 0..) |parameter, index| {
        if (index > 0) try out.writeByte(',');
        try out.writeAll("{\"typeParameterId\":");
        try model.writeString(out, decodedAt(parameter, "typeParameterId"));
        try out.writeAll(",\"displayName\":");
        try model.writeString(out, decodedAt(parameter, "displayName"));
        try out.writeByte('}');
    }
    try out.writeByte(']');
}

/// A signature as `ViewerData` writes it: `typeParameters` always, types without display names.
fn writeSignature(out: *Writer, function: Value, with_exported: bool) !void {
    try out.writeAll("{\"id\":");
    try model.writeString(out, decodedAt(function, "id"));
    try out.writeAll(",\"displayName\":");
    try model.writeString(out, decodedAt(function, "displayName"));
    if (with_exported) try out.writeAll(if (model.property(function, "exported") != null) ",\"exported\":true" else ",\"exported\":false");
    try out.writeAll(",\"typeParameters\":");
    try writeTypeParameters(out, function);
    try out.writeAll(",\"parameters\":[");
    for (model.arrayItems(function, "parameters"), 0..) |parameter, index| {
        if (index > 0) try out.writeByte(',');
        try out.writeAll("{\"id\":");
        try model.writeString(out, decodedAt(parameter, "id"));
        try out.writeAll(",\"displayName\":");
        try model.writeString(out, decodedAt(parameter, "displayName"));
        try out.writeAll(",\"type\":");
        try wire.typeRef(out, model.property(parameter, "type").?, &.{});
        try out.writeByte('}');
    }
    try out.writeAll("],\"resultType\":");
    try wire.typeRef(out, model.property(function, "resultType").?, &.{});
    try out.writeAll(",\"requiredEffects\":");
    try writeValues(out, model.arrayItems(function, "requiredEffects"));
    try out.writeByte('}');
}

fn writeSorted(a: Allocator, out: *Writer, ids: []const Str) !void {
    const sorted = try a.dupe(Str, ids);
    std.mem.sort(Str, sorted, {}, lessStr);
    try query.writeIds(out, sorted);
}

/// `ViewerData.ofFocus` of `function` in `root`, the open model; `snapshot_id` is its snapshot.
pub fn focusData(a: Allocator, root: Value, snapshot_id: []const u8, function: Value) ![]const u8 {
    const wanted = decodedAt(function, "id");
    const focus = model.focusOf(a, root, function);
    var data: Writer.Allocating = .init(a);
    const out = &data.writer;
    try out.writeAll("{\"view\":\"functionFocus\",\"focus\":{\"snapshotId\":");
    try @import("root").writeString(out, snapshot_id);
    try out.writeAll(",\"function\":");
    try writeSignature(out, function, true);
    try out.writeAll(if (model.property(function, "mustNotWait") != null) ",\"mustNotWait\":true,\"waitPath\":" else ",\"mustNotWait\":false,\"waitPath\":");
    if (focus.wait_path) |path| try model.writeString(out, path) else try out.writeAll("null");
    try out.writeAll(",\"bodyEffectRequirements\":");
    try query.writeRequirements(out, focus.requirements);
    try out.writeAll(",\"aborts\":");
    try query.writeAborts(out, focus.aborts, wanted);
    try out.writeAll(",\"callers\":");
    try query.writeSites(out, focus.callers);
    try out.writeAll(",\"callees\":");
    try query.writeSites(out, focus.callees);

    // The callers and callees but the function itself, by ID; those the model lacks are missing.
    var related_ids: std.ArrayList(Str) = .empty;
    for (focus.callers) |site| if (!Closure.has(related_ids.items, site.caller)) try related_ids.append(a, site.caller);
    for (focus.callees) |site| if (!Closure.has(related_ids.items, site.callee)) try related_ids.append(a, site.callee);
    var others: std.ArrayList(Str) = .empty;
    for (related_ids.items) |id| if (!std.mem.eql(u16, id, wanted)) try others.append(a, id);
    std.mem.sort(Str, others.items, {}, lessStr);
    var related: std.ArrayList(Value) = .empty;
    var missing_functions: std.ArrayList(Str) = .empty;
    for (others.items) |id| {
        const found = for (model.arrayItems(root, "functions")) |item| {
            if (same(item, id)) break item;
        } else null;
        if (found) |item| try related.append(a, item) else try missing_functions.append(a, id);
    }
    try out.writeAll(",\"relatedFunctionSignatures\":[");
    for (related.items, 0..) |item, index| {
        if (index > 0) try out.writeByte(',');
        try writeSignature(out, item, false);
    }

    // `getOpenObligations`: the open obligations whose call node the function owns.
    const nodes = model.bodyNodes(a, function);
    const Obligation = struct { id: Str, item: Value };
    var open: std.ArrayList(Obligation) = .empty;
    for (model.arrayItems(root, "obligations")) |obligation| {
        const status = model.property(obligation, "status") orelse continue;
        if (status != .string or !isKind(model.decoded(status), "open")) continue;
        const call = decodedAt(obligation, "callNodeId");
        for (nodes) |node| if (same(node, call)) {
            try open.append(a, .{ .id = decodedAt(obligation, "id"), .item = obligation });
            break;
        };
    }
    std.mem.sort(Obligation, open.items, {}, struct {
        fn less(_: void, x: Obligation, y: Obligation) bool {
            return std.mem.order(u16, x.id, y.id) == .lt;
        }
    }.less);
    try out.writeAll("],\"openObligations\":[");
    for (open.items, 0..) |obligation, index| {
        if (index > 0) try out.writeByte(',');
        try out.writeAll("{\"obligationId\":");
        try model.writeString(out, obligation.id);
        inline for (.{ "callNodeId", "parameterId", "originTransactionId" }) |name| {
            try out.writeAll(",\"" ++ name ++ "\":");
            try model.writeString(out, decodedAt(obligation.item, name));
        }
        try out.writeByte('}');
    }

    var closure: Closure = .{ .a = a, .root = root };
    for (model.arrayItems(function, "requiredEffects")) |effect| try closure.addEffect(model.decoded(effect));
    for (related.items) |item| for (model.arrayItems(item, "requiredEffects")) |effect| try closure.addEffect(model.decoded(effect));
    for (model.arrayItems(function, "parameters")) |parameter| try closure.visit(model.property(parameter, "type").?);
    try closure.visit(model.property(function, "resultType").?);
    for (focus.node_types) |item| if (item.type) |t| try closure.visit(t);
    for (related.items) |item| {
        for (model.arrayItems(item, "parameters")) |parameter| try closure.visit(model.property(parameter, "type").?);
        try closure.visit(model.property(item, "resultType").?);
    }
    try out.writeAll("],\"referencedTypes\":[");
    var count: usize = 0;
    for (model.arrayItems(root, "types")) |item| if (Closure.has(closure.types.items, decodedAt(item, "id"))) {
        if (count > 0) try out.writeByte(',');
        count += 1;
        try wire.semanticType(out, item);
    };
    try out.writeAll("],\"referencedCapabilities\":[");
    count = 0;
    for (model.arrayItems(root, "capabilities")) |item| if (Closure.has(closure.capabilities.items, decodedAt(item, "id"))) {
        if (count > 0) try out.writeByte(',');
        count += 1;
        try wire.capability(out, item);
    };
    try out.writeAll("],\"referencedEffects\":[");
    count = 0;
    var missing_effects: std.ArrayList(Str) = .empty;
    for (closure.effects.items) |id| {
        const known = for (model.arrayItems(root, "effects")) |item| {
            if (same(item, id)) break true;
        } else false;
        if (!known) try missing_effects.append(a, id);
    }
    for (model.arrayItems(root, "effects")) |item| if (Closure.has(closure.effects.items, decodedAt(item, "id"))) {
        if (count > 0) try out.writeByte(',');
        count += 1;
        try out.writeAll("{\"id\":");
        try model.writeString(out, decodedAt(item, "id"));
        try out.writeAll(",\"displayName\":");
        try model.writeString(out, decodedAt(item, "displayName"));
        try out.writeAll(if (model.property(item, "exported") != null) ",\"exported\":true}" else ",\"exported\":false}");
    };
    try out.writeAll("],\"missingFunctionIds\":");
    try query.writeIds(out, missing_functions.items);
    try out.writeAll(",\"missingTypeIds\":");
    try writeSorted(a, out, closure.missing_types.items);
    try out.writeAll(",\"missingCapabilityIds\":");
    try writeSorted(a, out, closure.missing_capabilities.items);
    try out.writeAll(",\"missingEffectIds\":");
    try writeSorted(a, out, missing_effects.items);

    var viewer: Viewer = .{ .a = a };
    for (focus.node_types) |item| try viewer.types.put(a, model.utf8(item.id), item.type);
    try out.writeAll(",\"graph\":");
    try viewer.graph(out, model.property(function, "body").?);
    try out.writeAll("}}");
    return data.written();
}
