//! ExpressionRecipeMaterialization: post-order nodes, owner-ordered bindings, lexical frames.
const std = @import("std");
const model = @import("model_reader");
const recipe = @import("recipe.zig");
const preflight = @import("preflight.zig");
const infer = @import("infer.zig");
const query = @import("query.zig");
const ids = @import("ids.zig");
const Allocator = std.mem.Allocator;
const Value = model.Value;
const Node = recipe.Node;

pub fn same(x: []const u8, y: []const u8) bool { return std.mem.eql(u8, x, y); }
pub fn string(a: Allocator, text: []const u8) !Value { return .{ .string = try std.unicode.utf8ToUtf16LeAlloc(a, text) }; }
pub fn number(a: Allocator, n: anytype) !Value { return .{ .number = try std.fmt.allocPrint(a, "{d}", .{n}) }; }
pub fn member(a: Allocator, name: []const u8, value: Value) !model.Member { return .{ .name = (try string(a, name)).string, .value = value }; }
pub fn object(a: Allocator, fields: []const model.Member) !Value { return .{ .object = try a.dupe(model.Member, fields) }; }
pub fn id(a: Allocator) !Value { const text = try ids.next(); return string(a, &text); }

/// Writes a model tree without changing it or borrowing request storage.
pub fn write(out: *std.Io.Writer, value: Value) anyerror!void {
    switch (value) {
        .json_null => try out.writeAll("null"),
        .json_true => try out.writeAll("true"),
        .json_false => try out.writeAll("false"),
        .number => |n| try out.writeAll(n),
        .string => |s| try model.writeString(out, s),
        .array => |items| {
            try out.writeByte('[');
            for (items, 0..) |item, i| { if (i > 0) try out.writeByte(','); try write(out, item); }
            try out.writeByte(']');
        },
        .object => |fields| {
            try out.writeByte('{');
            for (fields, 0..) |f, i| {
                if (i > 0) try out.writeByte(',');
                try model.writeString(out, f.name);
                try out.writeByte(':');
                try write(out, f.value);
            }
            try out.writeByte('}');
        },
    }
}

pub const Binding = struct { slot: []const u8, field: ?[]const u8 = null, value: Value };
pub const Frame = struct { bindings: []const Binding, next: ?*const Frame };

pub const Materializer = struct {
    a: Allocator,
    r: *query.Request,
    types: *infer.Infer,
    fresh: []const preflight.TypeParameter,
    function_id: Value,
    parameters: []const Value,
    original: ?Value = null,

    pub fn typeRef(self: *Materializer, t: recipe.TypeRef) anyerror!Value {
        const a = self.a;
        var fields: std.ArrayList(model.Member) = .empty;
        const kind: []const u8 = switch (t) { .primitive => "primitive", .type_parameter => "typeParameter", .capability => "capability", .named => "named", .function => "function", .builtin => "builtinGeneric" };
        try fields.append(a, try member(a, "kind", try string(a, kind)));
        switch (t) {
            .primitive => |name| try fields.append(a, try member(a, "primitive", try string(a, name))),
            .capability => |name| try fields.append(a, try member(a, "capabilityId", try string(a, name))),
            .type_parameter => |name| {
                var actual = name;
                for (self.fresh) |p| if (same(p.name, name)) { actual = p.id; break; };
                try fields.append(a, try member(a, "typeParameterId", try string(a, actual)));
            },
            .named => |n| {
                try fields.append(a, try member(a, "typeId", try string(a, n.id)));
                if (n.arguments.len > 0) try fields.append(a, try member(a, "arguments", try self.typeRefs(n.arguments)));
            },
            .builtin => |b| {
                try fields.append(a, try member(a, "generic", try string(a, b.generic)));
                try fields.append(a, try member(a, "arguments", try self.typeRefs(b.arguments)));
            },
            .function => |f| {
                try fields.append(a, try member(a, "parameters", try self.typeRefs(f.parameters)));
                try fields.append(a, try member(a, "resultType", try self.typeRef(f.result.*)));
            },
        }
        return .{ .object = fields.items };
    }

    fn typeRefs(self: *Materializer, ts: []const recipe.TypeRef) !Value {
        const out = try self.a.alloc(Value, ts.len);
        for (ts, out) |t, *v| v.* = try self.typeRef(t);
        return .{ .array = out };
    }

    fn binding(self: *Materializer, t: recipe.TypeRef) !Value {
        return object(self.a, &.{ try member(self.a, "id", try id(self.a)), try member(self.a, "type", try self.typeRef(t)) });
    }

    fn pushed(self: *Materializer, frames: ?*const Frame, bindings: []const Binding) !*const Frame {
        const frame = try self.a.create(Frame);
        frame.* = .{ .bindings = try self.a.dupe(Binding, bindings), .next = frames };
        return frame;
    }

    pub fn graph(self: *Materializer, frames: ?*const Frame, node: *const Node) anyerror!Value {
        var nodes: std.ArrayList(Value) = .empty;
        const result = try self.materializeNode(frames, node, &nodes);
        return object(self.a, &.{ try member(self.a, "nodes", .{ .array = nodes.items }), try member(self.a, "resultNodeId", result) });
    }

    fn valueArm(self: *Materializer, frames: ?*const Frame, b: Value, slot: []const u8, body: *const Node) !Value {
        const child = try self.pushed(frames, &.{.{ .slot = slot, .value = b }});
        const binding_name = if (same(slot, "error")) "errorBinding" else "valueBinding";
        return object(self.a, &.{ try member(self.a, binding_name, b), try member(self.a, "body", try self.graph(child, body)) });
    }

    fn getSlot(node: *const Node, name: []const u8) ?recipe.Data {
        for (node.slots) |s| if (same(s.name, name)) return s.data;
        return null;
    }

    fn declaration(self: *Materializer, group: []const u8, wanted: []const u8) Value {
        return preflight.declaration(self.r, group, wanted).?;
    }

    fn pairList(self: *Materializer, frames: ?*const Frame, pairs: []const recipe.Pair, expected: []const Value, key: []const u8, nodes: *std.ArrayList(Value)) !Value {
        var out: std.ArrayList(Value) = .empty;
        for (expected) |p| {
            const wanted = query.idOf(p);
            for (pairs) |pair| if (same(pair.id, wanted)) {
                const result = try self.materializeNode(frames, pair.value, nodes);
                try out.append(self.a, try object(self.a, &.{ try member(self.a, key, model.property(p, "id").?), try member(self.a, "valueNodeId", result) }));
                break;
            };
        }
        return .{ .array = out.items };
    }

    fn materializeNode(self: *Materializer, frames: ?*const Frame, n: *const Node, nodes: *std.ArrayList(Value)) anyerror!Value {
        const a = self.a;
        if (same(n.case, "OriginalTarget")) {
            try nodes.appendSlice(a, model.arrayItems(self.original.?, "nodes"));
            return model.property(self.original.?, "resultNodeId").?;
        }
        const kind = try a.dupe(u8, n.case);
        kind[0] = std.ascii.toLower(kind[0]);
        var fields: std.ArrayList(model.Member) = .empty;
        var actual_kind: []const u8 = kind;
        if (same(kind, "parameter") or same(kind, "parameterSlot")) {
            actual_kind = "parameterReference";
            const p = if (same(kind, "parameter")) try string(a, n.slots[0].data.id) else model.property(self.parameters[@intCast(n.slots[0].data.int)], "id").?;
            try fields.append(a, try member(a, "parameterId", p));
        } else if (same(kind, "patternReference")) {
            var frame = frames.?;
            var depth = getSlot(n, "scopeDepth").?.int;
            while (depth > 0) : (depth -= 1) frame = frame.next.?;
            const ref = getSlot(n, "slot").?.pattern;
            const resolved_binding = for (frame.bindings) |b| {
                if (same(b.slot, ref.slot) and (ref.field_id == null or (b.field != null and same(b.field.?, ref.field_id.?)))) break b.value;
            } else unreachable;
            try fields.append(a, try member(a, "patternBindingId", model.property(resolved_binding, "id").?));
        } else if (same(kind, "bind") or same(kind, "matchOption") or same(kind, "matchResult")) {
            const input = n.slots[0].data.recipe;
            try fields.append(a, try member(a, "inputNodeId", try self.materializeNode(frames, input, nodes)));
            const t = self.types.recovered(input);
            if (same(kind, "bind")) {
                const b = try self.binding(t);
                try fields.append(a, try member(a, "boundBody", try self.valueArm(frames, b, "value", n.slots[1].data.recipe)));
            } else if (same(kind, "matchOption")) {
                const b = try self.binding(t.builtin.arguments[0]);
                try fields.append(a, try member(a, "someArm", try self.valueArm(frames, b, "value", n.slots[1].data.recipe)));
                try fields.append(a, try member(a, "noneBody", try self.graph(try self.pushed(frames, &.{}), n.slots[2].data.recipe)));
            } else {
                // F# allocates BOTH bindings before either arm's body.
                const ok = try self.binding(t.builtin.arguments[0]);
                const err = try self.binding(t.builtin.arguments[1]);
                try fields.append(a, try member(a, "okArm", try self.valueArm(frames, ok, "value", n.slots[1].data.recipe)));
                try fields.append(a, try member(a, "errArm", try self.valueArm(frames, err, "error", n.slots[2].data.recipe)));
            }
        } else if (same(kind, "matchUnion")) {
            try fields.append(a, try member(a, "inputNodeId", try self.materializeNode(frames, n.slots[0].data.recipe, nodes)));
            var arms: std.ArrayList(Value) = .empty;
            for (try self.types.materializationArms(n)) |arm| {
                var af: std.ArrayList(model.Member) = .empty;
                const variants = try a.alloc(Value, arm.variants.len);
                for (arm.variants, variants) |v, *value| value.* = try string(a, v.id);
                try af.append(a, try member(a, if (variants.len == 1) "variantId" else "variantIds", if (variants.len == 1) variants[0] else .{ .array = variants }));
                var bindings: std.ArrayList(Value) = .empty;
                var visible: std.ArrayList(Binding) = .empty;
                if (arm.variants.len == 1) for (arm.variants[0].fields) |f| {
                    const b = try self.binding(f.type);
                    try bindings.append(a, try object(a, &.{ try member(a, "sourceFieldId", try string(a, f.id)), try member(a, "binding", b) }));
                    if (arm.binds_fields) try visible.append(a, .{ .slot = "field", .field = f.id, .value = b });
                };
                try af.append(a, try member(a, "fieldBindings", .{ .array = bindings.items }));
                try af.append(a, try member(a, "body", try self.graph(try self.pushed(frames, visible.items), arm.body)));
                try arms.append(a, .{ .object = af.items });
            }
            try fields.append(a, try member(a, "arms", .{ .array = arms.items }));
        } else if (same(kind, "selfCall")) {
            actual_kind = "directCall";
            try fields.append(a, try member(a, "targetFunctionId", self.function_id));
            const pairs = n.slots[0].data.pairs;
            var args: std.ArrayList(Value) = .empty;
            for (self.parameters, 0..) |p, i| for (pairs) |pair| if (pair.slot == @as(i64, @intCast(i))) {
                const result = try self.materializeNode(frames, pair.value, nodes);
                try args.append(a, try object(a, &.{ try member(a, "targetParameterId", model.property(p, "id").?), try member(a, "valueNodeId", result) }));
                break;
            };
            try fields.append(a, try member(a, "arguments", .{ .array = args.items }));
        } else {
            var lines = std.mem.tokenizeScalar(u8, @embedFile("materialize_kinds.txt"), '\n');
            const spec = while (lines.next()) |line| {
                if (line[0] == '#') continue;
                const bar = std.mem.indexOfScalar(u8, line, '|').?;
                if (same(line[0..bar], kind)) break line[bar + 1 ..];
            } else unreachable;
            var parts = std.mem.tokenizeScalar(u8, spec, ' ');
            while (parts.next()) |part| {
                var bits = std.mem.splitScalar(u8, part, ':');
                const name = bits.next().?;
                const shape = bits.next().?;
                const source = bits.next().?;
                const data = getSlot(n, source) orelse continue;
                if (same(shape, "ctx") and data.pairs.len == 0) continue;
                var value: Value = undefined;
                if (same(shape, "graph")) {
                    value = try self.graph(frames, data.recipe);
                } else if (same(shape, "ctx") or same(shape, "args") or same(shape, "capargs") or same(shape, "fields")) {
                    var expected: []const Value = undefined;
                    var key: []const u8 = "targetParameterId";
                    if (same(shape, "fields")) {
                        key = "targetFieldId";
                        var owner: Value = undefined;
                        if (getSlot(n, "typeId")) |t| owner = self.declaration("types", t.id) else {
                            owner = outer: for (model.arrayItems(self.r.model, "types")) |t| {
                                for (model.arrayItems(t, "variants")) |v| if (same(query.idOf(v), getSlot(n, "variantId").?.id)) break :outer v;
                            } else unreachable;
                        }
                        expected = model.arrayItems(owner, "fields");
                    } else if (same(shape, "capargs")) {
                        key = "targetCapabilityParameterId";
                        const op = getSlot(n, "operationId").?.id;
                        expected = outer: for (model.arrayItems(self.r.model, "capabilities")) |c| {
                            for (model.arrayItems(c, "operations")) |o| if (same(query.idOf(o), op)) break :outer model.arrayItems(o, "parameters");
                        } else unreachable;
                    } else {
                        const callback = if (same(source, "bodyContext")) "bodyFunctionId" else if (same(source, "handlerContext")) "handlerFunctionId" else blk: {
                            for (n.slots) |s| if (s.data == .id and (same(s.name, "functionId") or std.mem.endsWith(u8, s.name, "FunctionId"))) break :blk s.name;
                            unreachable;
                        };
                        expected = model.arrayItems(self.declaration("functions", getSlot(n, callback).?.id), "parameters");
                    }
                    value = try self.pairList(frames, data.pairs, expected, key, nodes);
                } else if (same(shape, "targs")) {
                    value = try self.typeRefs(self.types.recovered(n).named.arguments);
                    if (value.array.len == 0) continue;
                } else switch (data) {
                    .recipe => |child| value = try self.materializeNode(frames, child, nodes),
                    .recipes => |children| {
                        const items = try a.alloc(Value, children.len);
                        for (children, items) |child, *item| item.* = try self.materializeNode(frames, child, nodes);
                        value = .{ .array = items };
                    },
                    .id => |s| value = try string(a, s),
                    .text => |s| value = try string(a, s),
                    .int => |v| value = try number(a, v),
                    .bits => |v| value = try string(a, try std.fmt.allocPrint(a, "{x:0>16}", .{v})),
                    .bool => |b| value = if (b) .json_true else .json_false,
                    .type => |t| value = try self.typeRef(t),
                    .implementations => |impls| {
                        const items = try a.alloc(Value, impls.len);
                        for (impls, items) |impl, *item| item.* = try object(a, &.{ try member(a, "operationId", try string(a, impl.operation)), try member(a, "functionId", try string(a, impl.function)) });
                        std.mem.sort(Value, items, {}, struct {
                            fn less(_: void, x: Value, y: Value) bool {
                                return std.mem.order(u16, model.decoded(model.property(x, "operationId").?), model.decoded(model.property(y, "operationId").?)) == .lt;
                            }
                        }.less);
                        value = .{ .array = items };
                    },
                    else => unreachable,
                }
                try fields.append(a, try member(a, name, value));
            }
        }
        const own_id = try id(a);
        try fields.insert(a, 0, try member(a, "kind", try string(a, actual_kind)));
        try fields.insert(a, 0, try member(a, "id", own_id));
        try nodes.append(a, .{ .object = fields.items });
        return own_id;
    }
};

pub fn function(r: *query.Request, c: preflight.Creation, fresh: []const preflight.TypeParameter, types: *infer.Infer) !Value {
    const a = r.a;
    const fid = try id(a);
    const parameters = try a.alloc(Value, c.parameters.len);
    var m: Materializer = .{ .a = a, .r = r, .types = types, .fresh = fresh, .function_id = fid, .parameters = parameters };
    for (c.parameters, parameters) |p, *v| v.* = try object(a, &.{ try member(a, "id", try id(a)), try member(a, "displayName", try string(a, p.name)), try member(a, "type", try m.typeRef(p.type)) });
    var fields: std.ArrayList(model.Member) = .empty;
    try fields.appendSlice(a, &.{ try member(a, "id", fid), try member(a, "displayName", try string(a, c.display_name)) });
    for ([_][]const u8{ "exported", "mustNotWait" }) |flag| if (r.root.get(flag)) |v| if (v.bool) try fields.append(a, try member(a, flag, .json_true));
    if (fresh.len > 0) {
        const ps = try a.alloc(Value, fresh.len);
        for (fresh, ps) |p, *v| v.* = try object(a, &.{ try member(a, "typeParameterId", try string(a, p.id)), try member(a, "displayName", try string(a, p.name)) });
        try fields.append(a, try member(a, "typeParameters", .{ .array = ps }));
    }
    try fields.append(a, try member(a, "parameters", .{ .array = parameters }));
    try fields.append(a, try member(a, "resultType", try m.typeRef(c.result_type)));
    const es = try a.alloc(Value, c.required_effects.len);
    for (c.required_effects, es) |e, *v| v.* = try string(a, e);
    std.mem.sort(Value, es, {}, struct {
        fn less(_: void, x: Value, y: Value) bool { return std.mem.order(u16, x.string, y.string) == .lt; }
    }.less);
    try fields.append(a, try member(a, "requiredEffects", .{ .array = es }));
    try fields.append(a, try member(a, "body", try m.graph(null, c.body)));
    return .{ .object = fields.items };
}
