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
const tm = model.session_model;
const builder = tm.builder;

pub fn same(x: []const u8, y: []const u8) bool {
    return std.mem.eql(u8, x, y);
}
pub fn string(a: Allocator, text: []const u8) !Value {
    return .{ .string = try std.unicode.utf8ToUtf16LeAlloc(a, text) };
}
pub fn number(a: Allocator, n: anytype) !Value {
    return .{ .number = try std.fmt.allocPrint(a, "{d}", .{n}) };
}
pub fn member(a: Allocator, name: []const u8, value: Value) !model.Member {
    return .{ .name = (try string(a, name)).string, .value = value };
}
pub fn object(a: Allocator, fields: []const model.Member) !Value {
    return .{ .object = try a.dupe(model.Member, fields) };
}
pub fn id(a: Allocator) !Value {
    const text = try ids.next();
    return string(a, &text);
}

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
            for (items, 0..) |item, i| {
                if (i > 0) try out.writeByte(',');
                try write(out, item);
            }
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

pub const Binding = struct { slot: []const u8, field: ?[]const u8 = null, value: *const tm.Binding };
pub const Frame = struct { bindings: []const Binding, next: ?*const Frame };

pub const Materializer = struct {
    a: Allocator,
    r: *query.Request,
    types: *infer.Infer,
    fresh: []const preflight.TypeParameter,
    function_id: tm.Id,
    parameters: []const tm.Typed,
    original: ?tm.GraphView = null,

    pub fn internId(self: *Materializer, domain: tm.Domain, text: []const u8) !tm.Id {
        return self.r.interner.?.id(domain, try std.unicode.utf8ToUtf16LeAlloc(self.a, text));
    }
    pub fn internString(self: *Materializer, text: []const u8) !u32 {
        return self.r.interner.?.string(try std.unicode.utf8ToUtf16LeAlloc(self.a, text));
    }
    pub fn freshId(self: *Materializer, domain: tm.Domain) !tm.Id {
        const text = try ids.next();
        return self.internId(domain, &text);
    }
    pub fn typeRef(self: *Materializer, t: recipe.TypeRef) anyerror!tm.TypeRef {
        return switch (t) {
            .primitive => |name| .{ .primitive = try self.internString(name) },
            .capability => |name| .{ .capability = try self.internId(.capability, name) },
            .type_parameter => |name| blk: {
                const actual = for (self.fresh) |p| {
                    if (same(p.name, name)) break p.id;
                } else name;
                break :blk .{ .type_parameter = try self.internId(.type_parameter, actual) };
            },
            .named => |n| .{ .named = .{ .id = try self.internId(.type, n.id), .arguments = if (n.arguments.len == 0) null else try self.typeRefs(n.arguments) } },
            .builtin => |b| .{ .generic = .{ .name = try self.internString(b.generic), .arguments = try self.typeRefs(b.arguments) } },
            .function => |f| blk: {
                const result = try self.a.create(tm.TypeRef);
                const parameters = try self.typeRefs(f.parameters);
                result.* = try self.typeRef(f.result.*);
                break :blk .{ .function = .{ .parameters = parameters, .result = result } };
            },
        };
    }
    fn typeRefs(self: *Materializer, ts: []const recipe.TypeRef) ![]const tm.TypeRef {
        const out = try self.a.alloc(tm.TypeRef, ts.len);
        for (ts, out) |t, *v| v.* = try self.typeRef(t);
        return out;
    }
    fn binding(self: *Materializer, t: recipe.TypeRef) !*const tm.Binding {
        const result = try self.a.create(tm.Binding);
        const binding_id = try self.freshId(.binding);
        result.* = .{ .id = binding_id, .name = null, .type = try self.typeRef(t) };
        return result;
    }
    fn pushed(self: *Materializer, frames: ?*const Frame, bindings: []const Binding) !*const Frame {
        const frame = try self.a.create(Frame);
        frame.* = .{ .bindings = try self.a.dupe(Binding, bindings), .next = frames };
        return frame;
    }
    pub fn graph(self: *Materializer, frames: ?*const Frame, node: *const Node) anyerror!tm.Body {
        var nodes: std.ArrayList(builder.NodeDraft) = .empty;
        const result = try self.materializeNode(frames, node, &nodes);
        return builder.build(self.a, nodes.items, result);
    }
    fn valueArm(self: *Materializer, frames: ?*const Frame, b: *const tm.Binding, slot: []const u8, body: *const Node) !tm.BindingArm {
        const child = try self.pushed(frames, &.{.{ .slot = slot, .value = b }});
        return .{ .binding = b, .body = try builder.view(self.a, try self.graph(child, body)) };
    }
    fn getSlot(node: *const Node, name: []const u8) ?recipe.Data {
        for (node.slots) |s| if (same(s.name, name)) return s.data;
        return null;
    }
    fn functionDeclaration(self: *Materializer, wanted: []const u8) !*const tm.Function {
        return self.r.generation.?.findFunction(try self.internId(.function, wanted)).?;
    }
    fn pairList(self: *Materializer, frames: ?*const Frame, pairs: []const recipe.Pair, expected: []const tm.Typed, domain: tm.Domain, nodes: *std.ArrayList(builder.NodeDraft)) !tm.Targets {
        var out: std.ArrayList(tm.Target) = .empty;
        for (expected) |p| {
            // Parameter and field IDs share text ordering but live in separate intern tables.
            for (pairs) |pair| {
                const wanted = try std.unicode.utf8ToUtf16LeAlloc(self.a, pair.id);
                if (!std.mem.eql(u16, self.r.interner.?.idText(domain, p.id), wanted)) continue;
                try out.append(self.a, .{ .target = p.id, .value = try self.materializeNode(frames, pair.value, nodes) });
                break;
            }
        }
        return builder.targets(self.a, out.items);
    }

    const empty_body: tm.Body = .{ .kinds = &.{}, .ids = &.{}, .starts = &.{}, .lens = &.{}, .extra = &.{}, .graphs = &.{}, .types = &.{}, .bindings = &.{} };
    fn defaultPayload(read: tm.Read) tm.Payload {
        return switch (read.shape) {
            .optid => .{ .optid = null },
            .targs => .{ .targs = .{ .body = &empty_body, .indices = &.{} } },
            .ctx => .{ .ctx = .{ .words = &.{} } },
            else => undefined,
        };
    }
    fn setPayload(kind: u8, payload: []tm.Payload, name: []const u8, value: tm.Payload) void {
        for (tm.kindTable()[kind].reads, payload) |read, *slot| if (same(read.name, name)) {
            slot.* = value;
            return;
        };
        unreachable;
    }
    fn payloadRead(kind: u8, name: []const u8) tm.Read {
        for (tm.kindTable()[kind].reads) |read| if (same(read.name, name)) return read;
        unreachable;
    }
    fn materializeNode(self: *Materializer, frames: ?*const Frame, n: *const Node, nodes: *std.ArrayList(builder.NodeDraft)) anyerror!tm.Id {
        const a = self.a;
        if (same(n.case, "OriginalTarget")) {
            var original = self.original.?.nodes();
            while (original.next()) |node| try nodes.append(a, try builder.fromNode(a, node));
            return self.original.?.result();
        }
        const kind = try a.dupe(u8, n.case);
        kind[0] = std.ascii.toLower(kind[0]);
        const actual_kind: []const u8 = if (same(kind, "parameter") or same(kind, "parameterSlot")) "parameterReference" else if (same(kind, "selfCall")) "directCall" else kind;
        const kind_index = try builder.kindIndex(actual_kind);
        const payload = try a.alloc(tm.Payload, tm.kindTable()[kind_index].reads.len);
        for (tm.kindTable()[kind_index].reads, payload) |read, *slot| slot.* = defaultPayload(read);
        if (same(kind, "parameter") or same(kind, "parameterSlot")) {
            const p = if (same(kind, "parameter")) try self.internId(.parameter, n.slots[0].data.id) else self.parameters[@intCast(n.slots[0].data.int)].id;
            setPayload(kind_index, payload, "parameterId", .{ .id = p });
        } else if (same(kind, "patternReference")) {
            var frame = frames.?;
            var depth = getSlot(n, "scopeDepth").?.int;
            while (depth > 0) : (depth -= 1) frame = frame.next.?;
            const ref = getSlot(n, "slot").?.pattern;
            const resolved = for (frame.bindings) |b| {
                if (same(b.slot, ref.slot) and (ref.field_id == null or (b.field != null and same(b.field.?, ref.field_id.?)))) break b.value;
            } else unreachable;
            setPayload(kind_index, payload, "patternBindingId", .{ .id = resolved.id });
        } else if (same(kind, "bind") or same(kind, "matchOption") or same(kind, "matchResult")) {
            const input = n.slots[0].data.recipe;
            setPayload(kind_index, payload, "inputNodeId", .{ .id = try self.materializeNode(frames, input, nodes) });
            const t = self.types.recovered(input);
            if (same(kind, "bind")) {
                const b = try self.binding(t);
                setPayload(kind_index, payload, "boundBody", .{ .varm = try self.valueArm(frames, b, "value", n.slots[1].data.recipe) });
            } else if (same(kind, "matchOption")) {
                const b = try self.binding(t.builtin.arguments[0]);
                setPayload(kind_index, payload, "someArm", .{ .varm = try self.valueArm(frames, b, "value", n.slots[1].data.recipe) });
                setPayload(kind_index, payload, "noneBody", .{ .graph = try builder.view(a, try self.graph(try self.pushed(frames, &.{}), n.slots[2].data.recipe)) });
            } else {
                // F# allocates both bindings before either arm's body.
                const ok = try self.binding(t.builtin.arguments[0]);
                const err = try self.binding(t.builtin.arguments[1]);
                setPayload(kind_index, payload, "okArm", .{ .varm = try self.valueArm(frames, ok, "value", n.slots[1].data.recipe) });
                setPayload(kind_index, payload, "errArm", .{ .errarm = try self.valueArm(frames, err, "error", n.slots[2].data.recipe) });
            }
        } else if (same(kind, "matchUnion")) {
            setPayload(kind_index, payload, "inputNodeId", .{ .id = try self.materializeNode(frames, n.slots[0].data.recipe, nodes) });
            var arms: std.ArrayList(tm.UnionArm) = .empty;
            for (try self.types.materializationArms(n)) |arm| {
                const variants = try a.alloc(tm.Id, arm.variants.len);
                for (arm.variants, variants) |v, *slot| slot.* = try self.internId(.variant, v.id);
                var bindings: std.ArrayList(tm.FieldBinding) = .empty;
                var visible: std.ArrayList(Binding) = .empty;
                if (arm.variants.len == 1) for (arm.variants[0].fields) |f| {
                    const b = try self.binding(f.type);
                    try bindings.append(a, .{ .source = try self.internId(.field, f.id), .binding = b });
                    if (arm.binds_fields) try visible.append(a, .{ .slot = "field", .field = f.id, .value = b });
                };
                try arms.append(a, .{ .variants = variants, .fields = try builder.fieldBindings(a, bindings.items), .body = try builder.view(a, try self.graph(try self.pushed(frames, visible.items), arm.body)) });
            }
            setPayload(kind_index, payload, "arms", .{ .arms = try builder.unionArms(a, arms.items) });
        } else if (same(kind, "selfCall")) {
            setPayload(kind_index, payload, "targetFunctionId", .{ .id = self.function_id });
            const pairs = n.slots[0].data.pairs;
            var args: std.ArrayList(tm.Target) = .empty;
            for (self.parameters, 0..) |p, index| for (pairs) |pair| if (pair.slot == @as(i64, @intCast(index))) {
                try args.append(a, .{ .target = p.id, .value = try self.materializeNode(frames, pair.value, nodes) });
                break;
            };
            setPayload(kind_index, payload, "arguments", .{ .args = try builder.targets(a, args.items) });
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
                const read = payloadRead(kind_index, name);
                const value: tm.Payload = if (same(shape, "graph")) .{ .graph = try builder.view(a, try self.graph(frames, data.recipe)) } else if (same(shape, "ctx") or same(shape, "args") or same(shape, "capargs") or same(shape, "fields")) blk: {
                    var expected: []const tm.Typed = undefined;
                    var domain: tm.Domain = .parameter;
                    if (same(shape, "fields")) {
                        domain = .field;
                        if (getSlot(n, "typeId")) |type_id| {
                            expected = self.r.generation.?.findType(try self.internId(.type, type_id.id)).?.fields;
                        } else {
                            const variant = try self.internId(.variant, getSlot(n, "variantId").?.id);
                            expected = outer: for (self.r.generation.?.types) |type_| {
                                for (type_.variants) |v| if (v.id == variant) break :outer v.fields;
                            } else unreachable;
                        }
                    } else if (same(shape, "capargs")) {
                        domain = .capability_parameter;
                        const operation = try self.internId(.operation, getSlot(n, "operationId").?.id);
                        expected = outer: for (self.r.generation.?.capabilities) |capability| {
                            for (capability.operations) |op| if (op.id == operation) break :outer op.parameters;
                        } else unreachable;
                    } else {
                        const callback = if (same(source, "bodyContext")) "bodyFunctionId" else if (same(source, "handlerContext")) "handlerFunctionId" else callback: {
                            for (n.slots) |s| if (s.data == .id and (same(s.name, "functionId") or std.mem.endsWith(u8, s.name, "FunctionId"))) break :callback s.name;
                            unreachable;
                        };
                        expected = (try self.functionDeclaration(getSlot(n, callback).?.id)).parameters;
                    }
                    const pairs = try self.pairList(frames, data.pairs, expected, domain, nodes);
                    break :blk switch (read.shape) {
                        .ctx => .{ .ctx = pairs },
                        .args => .{ .args = pairs },
                        .capargs => .{ .capargs = pairs },
                        .fields => .{ .fields = pairs },
                        else => unreachable,
                    };
                } else if (same(shape, "targs")) .{ .targs = try builder.typeArguments(a, try self.typeRefs(self.types.recovered(n).named.arguments)) } else switch (data) {
                    .recipe => |child| if (read.shape == .optid) .{ .optid = try self.materializeNode(frames, child, nodes) } else .{ .id = try self.materializeNode(frames, child, nodes) },
                    .recipes => |children| blk: {
                        const items = try a.alloc(tm.Id, children.len);
                        for (children, items) |child, *slot| slot.* = try self.materializeNode(frames, child, nodes);
                        break :blk .{ .ids = items };
                    },
                    .id => |text| .{ .id = try self.internId(read.domain.?, text) },
                    .text => |text| if (read.shape == .mode) .{ .mode = if (same(text, "all")) .all else .any } else .{ .lit = try self.internString(text) },
                    .int => |integer| .{ .int = integer },
                    .bits => |raw| .{ .bits = raw },
                    .bool => |boolean| .{ .bool = boolean },
                    .type => |type_| .{ .type = try self.typeRef(type_) },
                    .implementations => |impls| blk: {
                        const items = try a.alloc(tm.Implementation, impls.len);
                        for (impls, items) |impl, *slot| slot.* = .{ .operation = try self.internId(.operation, impl.operation), .function = try self.internId(.function, impl.function) };
                        std.mem.sort(tm.Implementation, items, @as(*const tm.Interner, self.r.interner.?), struct {
                            fn less(interner: *const tm.Interner, x: tm.Implementation, y: tm.Implementation) bool {
                                return interner.less(.operation, x.operation, y.operation);
                            }
                        }.less);
                        break :blk .{ .impls = try builder.implementations(a, items) };
                    },
                    else => unreachable,
                };
                setPayload(kind_index, payload, name, value);
            }
        }
        const own_id = try self.freshId(.node);
        try nodes.append(a, .{ .id = own_id, .kind = kind_index, .payload = payload });
        return own_id;
    }
};

pub fn function(r: *query.Request, c: preflight.Creation, fresh: []const preflight.TypeParameter, types: *infer.Infer) !*const tm.Function {
    const a = r.a;
    var materializer: Materializer = .{ .a = a, .r = r, .types = types, .fresh = fresh, .function_id = tm.none, .parameters = &.{} };
    materializer.function_id = try materializer.freshId(.function);
    const parameters = try a.alloc(tm.Typed, c.parameters.len);
    for (c.parameters, parameters) |p, *slot| {
        const parameter_id = try materializer.freshId(.parameter);
        slot.* = .{ .id = parameter_id, .name = try materializer.internString(p.name), .type = try materializer.typeRef(p.type) };
    }
    materializer.parameters = parameters;
    const type_parameters = if (fresh.len == 0) null else try a.alloc(tm.TypeParameter, fresh.len);
    if (type_parameters) |items| for (fresh, items) |p, *slot| {
        slot.* = .{ .id = try materializer.internId(.type_parameter, p.id), .name = try materializer.internString(p.name) };
    };
    const effects = try a.alloc(tm.Id, c.required_effects.len);
    for (c.required_effects, effects) |effect, *slot| slot.* = try materializer.internId(.effect, effect);
    std.mem.sort(tm.Id, effects, @as(*const tm.Interner, r.interner.?), struct {
        fn less(interner: *const tm.Interner, x: tm.Id, y: tm.Id) bool {
            return interner.less(.effect, x, y);
        }
    }.less);
    const result = try a.create(tm.Function);
    result.* = .{ .id = materializer.function_id, .name = try materializer.internString(c.display_name), .exported = if (r.root.get("exported")) |flag| flag.bool else false, .must_not_wait = if (r.root.get("mustNotWait")) |flag| flag.bool else false, .type_parameters = type_parameters, .parameters = parameters, .result = try materializer.typeRef(c.result_type), .effects = effects, .body = try materializer.graph(null, c.body) };
    return result;
}

test "typed materializer creates postorder nodes and fresh parameter references" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    var interner: tm.Interner = .{ .gpa = a };
    const parameter: Node = .{ .case = "ParameterSlot", .slots = &.{.{ .name = "index", .data = .{ .int = 0 } }} };
    const literal: Node = .{ .case = "Int64Literal", .slots = &.{.{ .name = "value", .data = .{ .int = -42 } }} };
    const add: Node = .{ .case = "Int64WrappingAdd", .slots = &.{ .{ .name = "left", .data = .{ .recipe = &parameter } }, .{ .name = "right", .data = .{ .recipe = &literal } } } };
    const creation: preflight.Creation = .{ .expected_snapshot_id = "s", .display_name = "Created", .type_parameters = null, .parameters = &.{.{ .name = "p", .type = .{ .primitive = "int64" } }}, .result_type = .{ .primitive = "int64" }, .required_effects = &.{}, .body = &add };
    var request: query.Request = .{ .a = a, .line = "", .root = .empty, .provenance = null, .interner = &interner };
    var inferred: infer.Infer = undefined;
    const created = try function(&request, creation, &.{}, &inferred);
    try std.testing.expectEqual(@as(usize, 3), created.body.ids.len);
    var nodes = created.body.graph(0).nodes();
    const ref = nodes.next().?;
    const value = nodes.next().?;
    const sum = nodes.next().?;
    try std.testing.expectEqualStrings("parameterReference", ref.kind());
    var ref_payload = ref.payload();
    try std.testing.expectEqual(created.parameters[0].id, ref_payload.next().?.value.id);
    var literal_payload = value.payload();
    try std.testing.expectEqual(@as(i64, -42), literal_payload.next().?.value.int);
    var operands = sum.payload();
    try std.testing.expectEqual(ref.id(), operands.next().?.value.id);
    try std.testing.expectEqual(value.id(), operands.next().?.value.id);
    try std.testing.expectEqual(sum.id(), created.body.graph(0).result());
}

test "typed materializer allocates both result bindings before arm nodes and resolves lexical slots" {
    ids.init(std.testing.io, "typed-materializer-order");
    defer ids.init(std.testing.io, null);
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    var interner: tm.Interner = .{ .gpa = a };
    const value: Node = .{ .case = "Int64Literal", .slots = &.{.{ .name = "value", .data = .{ .int = 7 } }} };
    const input: Node = .{ .case = "ResultOk", .slots = &.{ .{ .name = "errorType", .data = .{ .type = .{ .primitive = "bool" } } }, .{ .name = "value", .data = .{ .recipe = &value } } } };
    const ok: Node = .{ .case = "PatternReference", .slots = &.{ .{ .name = "scopeDepth", .data = .{ .int = 0 } }, .{ .name = "slot", .data = .{ .pattern = .{ .slot = "value", .field_id = null } } } } };
    const err: Node = .{ .case = "PatternReference", .slots = &.{ .{ .name = "scopeDepth", .data = .{ .int = 0 } }, .{ .name = "slot", .data = .{ .pattern = .{ .slot = "error", .field_id = null } } } } };
    const match: Node = .{ .case = "MatchResult", .slots = &.{ .{ .name = "input", .data = .{ .recipe = &input } }, .{ .name = "ok", .data = .{ .recipe = &ok } }, .{ .name = "err", .data = .{ .recipe = &err } } } };
    var request: query.Request = .{ .a = a, .line = "", .root = .empty, .provenance = null, .interner = &interner };
    var inferred: infer.Infer = .{ .r = &request, .a = a, .c = undefined, .local = &.{}, .fresh = &.{} };
    try inferred.values.put(a, &input, .{ .frames = null, .value = .{ .builtin = .{ .generic = "result", .arguments = &.{ .{ .primitive = "int64" }, .{ .primitive = "bool" } } } } });
    var materializer: Materializer = .{ .a = a, .r = &request, .types = &inferred, .fresh = &.{}, .function_id = tm.none, .parameters = &.{} };
    const body = try materializer.graph(null, &match);
    const node = body.findNode(body.graph(0).result()).?;
    var payload = node.payload();
    const err_arm = payload.next().?.value.errarm;
    const ok_arm = payload.next().?.value.varm;
    var ok_nodes = ok_arm.body.nodes();
    const ok_node = ok_nodes.next().?;
    var err_nodes = err_arm.body.nodes();
    const err_node = err_nodes.next().?;
    var ok_payload = ok_node.payload();
    var err_payload = err_node.payload();
    try std.testing.expectEqual(ok_arm.binding.id, ok_payload.next().?.value.id);
    try std.testing.expectEqual(err_arm.binding.id, err_payload.next().?.value.id);
    // UUIDv7 timestamps retain allocation order across ID domains.
    const ok_id = interner.idText(.binding, ok_arm.binding.id);
    const err_id = interner.idText(.binding, err_arm.binding.id);
    const first_arm_node = interner.idText(.node, ok_node.id());
    try std.testing.expect(std.mem.order(u16, ok_id, err_id) == .lt);
    try std.testing.expect(std.mem.order(u16, err_id, first_arm_node) == .lt);
}
