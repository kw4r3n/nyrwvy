//! Lazy read handles over immutable typed records. Only immediate member/array wrappers are
//! allocated; compound children keep their typed storage identity and never become JSON trees.
const std = @import("std");
const reader = @import("main.zig");
const t = @import("typed.zig");
const A = std.mem.Allocator;
const V = reader.Value;
const M = reader.Member;
const BodyAt = struct { body: *const t.Body, at: u32 };
const Target = struct { domain: t.Domain, id: t.Id, value: t.Id, key: []const u8 };
const FieldBinding = struct { body: *const t.Body, field: t.Id, binding: u32 };
const Arm = struct { body: *const t.Body, payload: []const u32 };
pub const Entity = union(enum) {
    generation: *const t.Generation,
    declaration_type: *const t.Type,
    effect: *const t.Effect,
    capability: *const t.Capability,
    function: *const t.Function,
    operation: *const t.Operation,
    variant: *const t.Variant,
    obligation: *const t.Obligation,
    slot: struct { item: *const t.Typed, domain: t.Domain },
    type_parameter: *const t.TypeParameter,
    type_ref: *const t.TypeRef,
    binding: *const t.Binding,
    graph: BodyAt,
    node: BodyAt,
    target: Target,
    implementation: struct { operation: t.Id, function: t.Id },
    branch: struct { body: *const t.Body, binding: u32, graph: u32, failure: bool },
    arm: Arm,
    field_binding: FieldBinding,
    marker: t.Marker,
};
pub const Reference = struct {
    interner: *const t.Interner,
    entity: Entity,
    canonical: bool = false,

    fn child(self: Reference, entity: Entity) V {
        return .{ .typed = .{ .interner = self.interner, .entity = entity, .canonical = self.canonical } };
    }
    fn id(self: Reference, domain: t.Domain, value: t.Id) V {
        return .{ .string = self.interner.idText(domain, value) };
    }
    fn string(self: Reference, handle: u32) V {
        return .{ .string = self.interner.stringText(handle) };
    }
    fn ascii(a: A, value: []const u8) V {
        return .{ .string = std.unicode.utf8ToUtf16LeAlloc(a, value) catch @panic("out of memory") };
    }
    fn array(a: A, count: usize) []V {
        return a.alloc(V, count) catch @panic("out of memory");
    }
    fn sort(self: Reference, items: []V, key: ?[]const u8) V {
        if (self.canonical) std.mem.sort(V, items, key, struct {
            fn less(k: ?[]const u8, x: V, y: V) bool {
                const left = if (k) |name| reader.property(x, name).? else x;
                const right = if (k) |name| reader.property(y, name).? else y;
                return std.mem.order(u16, reader.decoded(left), reader.decoded(right)) == .lt;
            }
        }.less);
        return .{ .array = items };
    }
    fn slots(self: Reference, a: A, domain: t.Domain, items: []const t.Typed, ordered: bool) V {
        const values = array(a, items.len);
        for (items, values) |*item, *value| value.* = self.child(.{ .slot = .{ .item = item, .domain = domain } });
        return if (ordered) self.sort(values, "id") else .{ .array = values };
    }
    fn types(self: Reference, a: A, items: []const t.TypeRef) V {
        const values = array(a, items.len);
        for (items, values) |*item, *value| value.* = self.child(.{ .type_ref = item });
        return .{ .array = values };
    }
    fn parameters(self: Reference, b: *Builder, items: ?[]const t.TypeParameter) void {
        const listed = items orelse return;
        const values = array(b.a, listed.len);
        for (listed, values) |*item, *value| value.* = self.child(.{ .type_parameter = item });
        b.put("typeParameters", .{ .array = values });
    }
    fn head(self: Reference, b: *Builder, domain: t.Domain, record: anytype) void {
        b.put("id", self.id(domain, record.id));
        b.put("displayName", self.string(record.name));
        if (comptime @hasField(@TypeOf(record.*), "exported")) if (record.exported) b.put("exported", .json_true);
    }
    fn targets(self: Reference, a: A, payload: []const u32, at: *usize, key: []const u8, domain: t.Domain) V {
        const values = array(a, payload[at.*]);
        at.* += 1;
        for (values) |*value| {
            value.* = self.child(.{ .target = .{ .domain = domain, .id = payload[at.*], .value = payload[at.* + 1], .key = key } });
            at.* += 2;
        }
        return self.sort(values, key);
    }
    fn declarations(self: Reference, a: A, name: []const u8) ?V {
        const g = self.entity.generation;
        inline for (.{ .{ "types", "declaration_type" }, .{ "effects", "effect" }, .{ "capabilities", "capability" }, .{ "functions", "function" }, .{ "obligations", "obligation" } }) |group| {
            if (std.mem.eql(u8, name, group[0])) {
                const records = @field(g, group[0]);
                const values = array(a, records.len);
                for (records, values) |record, *value| value.* = self.child(@unionInit(Entity, group[1], record));
                return self.sort(values, "id");
            }
        }
        return null;
    }
    pub fn property(self: Reference, a: A, name: []const u8) ?V {
        if (self.entity == .generation) {
            const g = self.entity.generation;
            if (std.mem.eql(u8, name, "schemaVersion")) return .{ .number = std.fmt.allocPrint(a, "{d}", .{reader.currentVersion()}) catch @panic("out of memory") };
            if (std.mem.eql(u8, name, "snapshotId")) return self.id(.snapshot, g.snapshot);
            if (std.mem.eql(u8, name, "parentSnapshotId")) return if (g.parent != t.none) self.id(.snapshot, g.parent) else null;
            if (std.mem.eql(u8, name, "dependency")) return if (g.marker) |marker| self.child(.{ .marker = marker }) else null;
            return self.declarations(a, name);
        }
        if (std.mem.eql(u8, name, "id")) switch (self.entity) {
            .declaration_type => |record| return self.id(.type, record.id),
            .effect => |record| return self.id(.effect, record.id),
            .capability => |record| return self.id(.capability, record.id),
            .function => |record| return self.id(.function, record.id),
            .operation => |record| return self.id(.operation, record.id),
            .variant => |record| return self.id(.variant, record.id),
            .obligation => |record| return self.id(.obligation, record.id),
            .slot => |slot| return self.id(slot.domain, slot.item.id),
            .binding => |record| return self.id(.binding, record.id),
            .node => |where| return self.id(.node, where.body.ids[where.at]),
            else => {},
        };
        if (std.mem.eql(u8, name, "displayName")) switch (self.entity) {
            .declaration_type => |record| return self.string(record.name),
            .effect => |record| return self.string(record.name),
            .capability => |record| return self.string(record.name),
            .function => |record| return self.string(record.name),
            .operation => |record| return self.string(record.name),
            .variant => |record| return self.string(record.name),
            .type_parameter => |record| return self.string(record.name),
            .slot => |slot| return self.string(slot.item.name),
            .binding => |record| return if (record.name) |handle| self.string(handle) else null,
            else => {},
        };
        if (self.entity == .node and std.mem.eql(u8, name, "kind")) {
            const where = self.entity.node;
            return ascii(a, t.kindTable()[where.body.kinds[where.at]].name);
        }
        for (self.members(a)) |member| {
            if (member.name.len != name.len) continue;
            for (member.name, name) |unit, byte| {
                if (unit != byte) break;
            } else return member.value;
        }
        return null;
    }
    pub fn members(self: Reference, a: A) []const M {
        var b: Builder = .{ .a = a };
        switch (self.entity) {
            .generation => |g| {
                b.put("schemaVersion", .{ .number = std.fmt.allocPrint(a, "{d}", .{reader.currentVersion()}) catch @panic("out of memory") });
                b.put("snapshotId", self.id(.snapshot, g.snapshot));
                if (g.marker) |marker| b.put("dependency", self.child(.{ .marker = marker }));
                if (g.parent != t.none) b.put("parentSnapshotId", self.id(.snapshot, g.parent));
                inline for (.{ .{ "types", "declaration_type" }, .{ "effects", "effect" }, .{ "capabilities", "capability" }, .{ "functions", "function" }, .{ "obligations", "obligation" } }) |group| {
                    const records = @field(g, group[0]);
                    const values = array(a, records.len);
                    for (records, values) |record, *value| value.* = self.child(@unionInit(Entity, group[1], record));
                    b.put(group[0], self.sort(values, "id"));
                }
            },
            .declaration_type => |record| {
                self.head(&b, .type, record);
                self.parameters(&b, record.type_parameters);
                b.text("kind", if (record.product) "product" else "union");
                if (record.product) b.put("fields", self.slots(a, .field, record.fields, true)) else {
                    const values = array(a, record.variants.len);
                    for (record.variants, values) |*item, *value| value.* = self.child(.{ .variant = item });
                    b.put("variants", self.sort(values, "id"));
                }
            },
            .effect => |record| self.head(&b, .effect, record),
            .capability => |record| {
                self.head(&b, .capability, record);
                if (record.task_bound) b.put("taskBound", .json_true);
                const values = array(a, record.operations.len);
                for (record.operations, values) |*item, *value| value.* = self.child(.{ .operation = item });
                b.put("operations", self.sort(values, "id"));
            },
            .function => |record| {
                self.head(&b, .function, record);
                if (record.must_not_wait) b.put("mustNotWait", .json_true);
                self.parameters(&b, record.type_parameters);
                b.put("parameters", self.slots(a, .parameter, record.parameters, false));
                b.put("resultType", self.child(.{ .type_ref = &record.result }));
                const effects = array(a, record.effects.len);
                for (record.effects, effects) |effect, *value| value.* = self.id(.effect, effect);
                b.put("requiredEffects", self.sort(effects, null));
                b.put("body", self.child(.{ .graph = .{ .body = &record.body, .at = 0 } }));
            },
            .operation => |record| {
                self.head(&b, .operation, record);
                b.put("parameters", self.slots(a, .capability_parameter, record.parameters, false));
                b.put("resultType", self.child(.{ .type_ref = &record.result }));
                if (record.failure) |*failure| b.put("failureType", self.child(.{ .type_ref = failure }));
                b.put("effectId", self.id(.effect, record.effect));
                if (record.may_wait) b.put("mayWait", .json_true);
            },
            .variant => |record| {
                self.head(&b, .variant, record);
                b.put("fields", self.slots(a, .field, record.fields, true));
            },
            .slot => |slot| {
                self.head(&b, slot.domain, slot.item);
                b.put("type", self.child(.{ .type_ref = &slot.item.type }));
            },
            .type_parameter => |record| {
                b.put("typeParameterId", self.id(.type_parameter, record.id));
                b.put("displayName", self.string(record.name));
            },
            .type_ref => |record| switch (record.*) {
                .primitive => |name| {
                    b.text("kind", "primitive");
                    b.put("primitive", self.string(name));
                },
                .named => |*named| {
                    b.text("kind", "named");
                    b.put("typeId", self.id(.type, named.id));
                    if (named.arguments) |arguments| b.put("arguments", self.types(a, arguments));
                },
                .function => |*function| {
                    b.text("kind", "function");
                    b.put("parameters", self.types(a, function.parameters));
                    b.put("resultType", self.child(.{ .type_ref = function.result }));
                },
                .type_parameter => |handle| {
                    b.text("kind", "typeParameter");
                    b.put("typeParameterId", self.id(.type_parameter, handle));
                },
                .capability => |handle| {
                    b.text("kind", "capability");
                    b.put("capabilityId", self.id(.capability, handle));
                },
                .generic => |*generic| {
                    b.text("kind", "builtinGeneric");
                    b.put("generic", self.string(generic.name));
                    b.put("arguments", self.types(a, generic.arguments));
                },
            },
            .binding => |record| {
                b.put("id", self.id(.binding, record.id));
                if (record.name) |name| b.put("displayName", self.string(name));
                b.put("type", self.child(.{ .type_ref = &record.type }));
            },
            .graph => |where| {
                const graph = where.body.graphs[where.at];
                b.put("resultNodeId", self.id(.node, graph.result));
                const nodes = array(a, graph.count);
                for (nodes, graph.first..) |*value, at| value.* = self.child(.{ .node = .{ .body = where.body, .at = @intCast(at) } });
                b.put("nodes", self.sort(nodes, "id"));
            },
            .node => |where| self.node(&b, where),
            .target => |target| {
                b.put(target.key, self.id(target.domain, target.id));
                b.put("valueNodeId", self.id(.node, target.value));
            },
            .implementation => |impl| {
                b.put("operationId", self.id(.operation, impl.operation));
                b.put("functionId", self.id(.function, impl.function));
            },
            .branch => |branch| {
                b.put(if (branch.failure) "errorBinding" else "valueBinding", self.child(.{ .binding = &branch.body.bindings[branch.binding] }));
                b.put("body", self.child(.{ .graph = .{ .body = branch.body, .at = branch.graph } }));
            },
            .arm => |arm| {
                var at: usize = 1;
                const count = arm.payload[0];
                if (count == 1) b.put("variantId", self.id(.variant, arm.payload[at])) else {
                    const variants = array(a, count);
                    for (variants, arm.payload[at..][0..count]) |*value, handle| value.* = self.id(.variant, handle);
                    b.put("variantIds", self.sort(variants, null));
                }
                at += count;
                const bindings = array(a, arm.payload[at]);
                at += 1;
                for (bindings) |*value| {
                    value.* = self.child(.{ .field_binding = .{ .body = arm.body, .field = arm.payload[at], .binding = arm.payload[at + 1] } });
                    at += 2;
                }
                b.put("fieldBindings", self.sort(bindings, "sourceFieldId"));
                b.put("body", self.child(.{ .graph = .{ .body = arm.body, .at = arm.payload[at] } }));
            },
            .field_binding => |field| {
                b.put("sourceFieldId", self.id(.field, field.field));
                b.put("binding", self.child(.{ .binding = &field.body.bindings[field.binding] }));
            },
            .marker => |marker| {
                b.put("snapshotId", .{ .string = marker.snapshot });
                b.put("digest", .{ .string = marker.digest });
            },
            .obligation => |record| {
                b.put("id", self.id(.obligation, record.id));
                b.text("kind", "missingArgument");
                b.put("originTransactionId", self.id(.transaction, record.origin));
                b.put("callNodeId", self.id(.node, record.call));
                b.put("parameterId", self.id(.parameter, record.parameter));
                if (record.resolution) |resolution| {
                    b.text("status", "resolved");
                    b.put("resolutionTransactionId", self.id(.transaction, resolution.transaction));
                    b.put("resolutionValueNodeId", self.id(.node, resolution.value));
                } else b.text("status", "open");
            },
        }
        return b.fields.items;
    }
    fn node(self: Reference, b: *Builder, where: BodyAt) void {
        const body = where.body;
        const kind = t.kindTable()[body.kinds[where.at]];
        const payload = body.extra[body.starts[where.at] .. body.starts[where.at] + body.lens[where.at]];
        b.put("id", self.id(.node, body.ids[where.at]));
        b.text("kind", kind.name);
        var i: usize = 0;
        for (kind.reads) |read| {
            switch (read.shape) {
                .optid => if (payload[i] == t.none) {
                    i += 1;
                    continue;
                },
                .targs, .ctx => if (payload[i] == 0) {
                    i += 1;
                    continue;
                },
                else => {},
            }
            const value: V = switch (read.shape) {
                .id, .optid => blk: {
                    const value = self.id(read.domain.?, payload[i]);
                    i += 1;
                    break :blk value;
                },
                .ids => blk: {
                    const values = array(b.a, payload[i]);
                    i += 1;
                    for (values) |*value| {
                        value.* = self.id(read.domain.?, payload[i]);
                        i += 1;
                    }
                    break :blk .{ .array = values };
                },
                .type => blk: {
                    const value = self.child(.{ .type_ref = &body.types[payload[i]] });
                    i += 1;
                    break :blk value;
                },
                .targs => blk: {
                    const values = array(b.a, payload[i]);
                    i += 1;
                    for (values) |*value| {
                        value.* = self.child(.{ .type_ref = &body.types[payload[i]] });
                        i += 1;
                    }
                    break :blk .{ .array = values };
                },
                .graph => blk: {
                    const value = self.child(.{ .graph = .{ .body = body, .at = payload[i] } });
                    i += 1;
                    break :blk value;
                },
                .varm, .errarm => blk: {
                    const value = self.child(.{ .branch = .{ .body = body, .binding = payload[i], .graph = payload[i + 1], .failure = read.shape == .errarm } });
                    i += 2;
                    break :blk value;
                },
                .ctx, .args => self.targets(b.a, payload, &i, "targetParameterId", .parameter),
                .capargs => self.targets(b.a, payload, &i, "targetCapabilityParameterId", .capability_parameter),
                .fields => self.targets(b.a, payload, &i, "targetFieldId", .field),
                .lit => blk: {
                    const value = self.string(payload[i]);
                    i += 1;
                    break :blk value;
                },
                .bool => blk: {
                    const value: V = if (payload[i] == 1) .json_true else .json_false;
                    i += 1;
                    break :blk value;
                },
                .int, .bits => blk: {
                    const bits = @as(u64, payload[i]) | (@as(u64, payload[i + 1]) << 32);
                    i += 2;
                    if (read.shape == .bits) break :blk ascii(b.a, std.fmt.allocPrint(b.a, "{x:0>16}", .{bits}) catch @panic("out of memory"));
                    break :blk .{ .number = std.fmt.allocPrint(b.a, "{d}", .{@as(i64, @bitCast(bits))}) catch @panic("out of memory") };
                },
                .mode => blk: {
                    const value = ascii(b.a, if (payload[i] == 1) "all" else "any");
                    i += 1;
                    break :blk value;
                },
                .impls => blk: {
                    const values = array(b.a, payload[i]);
                    i += 1;
                    for (values) |*value| {
                        value.* = self.child(.{ .implementation = .{ .operation = payload[i], .function = payload[i + 1] } });
                        i += 2;
                    }
                    break :blk self.sort(values, "operationId");
                },
                .arms => blk: {
                    const values = array(b.a, payload[i]);
                    i += 1;
                    for (values) |*value| {
                        const start = i;
                        const variants = payload[i];
                        i += 1 + variants;
                        const bindings = payload[i];
                        i += 1 + 2 * bindings + 1;
                        value.* = self.child(.{ .arm = .{ .body = body, .payload = payload[start..i] } });
                    }
                    if (self.canonical) std.mem.sort(V, values, {}, struct {
                        fn less(_: void, x: V, y: V) bool {
                            const xp = x.typed.entity.arm.payload;
                            const yp = y.typed.entity.arm.payload;
                            var left = xp[1];
                            var right = yp[1];
                            for (xp[1..][0..xp[0]]) |value| if (x.typed.interner.less(.variant, value, left)) {
                                left = value;
                            };
                            for (yp[1..][0..yp[0]]) |value| if (y.typed.interner.less(.variant, value, right)) {
                                right = value;
                            };
                            return x.typed.interner.less(.variant, left, right);
                        }
                    }.less);
                    break :blk .{ .array = values };
                },
            };
            b.put(read.name, value);
        }
        std.debug.assert(i == payload.len);
    }
};
const Builder = struct {
    a: A,
    fields: std.ArrayList(M) = .empty,
    fn put(self: *Builder, name: []const u8, value: V) void {
        self.fields.append(self.a, .{ .name = std.unicode.utf8ToUtf16LeAlloc(self.a, name) catch @panic("out of memory"), .value = value }) catch @panic("out of memory");
    }
    fn text(self: *Builder, name: []const u8, value: []const u8) void {
        self.put(name, Reference.ascii(self.a, value));
    }
};
pub fn root(interner: *const t.Interner, generation: *const t.Generation, canonical: bool) V {
    return .{ .typed = .{ .interner = interner, .entity = .{ .generation = generation }, .canonical = canonical } };
}

test "typed reads and lowering survive destruction of every decoded source value" {
    var records = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer records.deinit();
    var source_arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    const source_a = source_arena.allocator();
    reader.useAllocator(source_a);
    const source = try std.fmt.allocPrint(source_a, "{{\"schemaVersion\":{d},\"snapshotId\":\"00000000000000000000000000000001\",\"types\":[],\"effects\":[],\"capabilities\":[],\"functions\":[{{\"id\":\"00000000000000000000000000000002\",\"displayName\":\"Owned\\nName\",\"parameters\":[],\"resultType\":{{\"kind\":\"primitive\",\"primitive\":\"unit\"}},\"requiredEffects\":[],\"body\":{{\"nodes\":[{{\"id\":\"00000000000000000000000000000003\",\"kind\":\"unitLiteral\"}}],\"resultNodeId\":\"00000000000000000000000000000003\"}}}}],\"obligations\":[]}}", .{reader.currentVersion()});
    const boundary = reader.readDocument(source_a, source);
    try std.testing.expect(boundary.fingerprints != null);
    const expected_fingerprint = try records.allocator().dupe(u8, boundary.fingerprints.?.snapshot);
    const expected_canonical = try records.allocator().dupe(u8, boundary.canonical);
    const decoded = boundary.root;
    const root_value = try reader.ownTyped(records.allocator(), decoded, true);
    source_arena.deinit();
    var request = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer request.deinit();
    reader.beginRequest(request.allocator());
    try std.testing.expect(root_value == .typed);
    const functions = reader.arrayItems(root_value, "functions");
    try std.testing.expectEqual(@as(usize, 1), functions.len);
    try std.testing.expect(functions[0] == .typed);
    const name = reader.decoded(reader.property(functions[0], "displayName").?);
    try std.testing.expectEqualStrings("Owned\nName", reader.utf8(name));
    const body = reader.property(functions[0], "body").?;
    try std.testing.expect(body == .typed);
    const nodes = reader.arrayItems(body, "nodes");
    try std.testing.expect(nodes[0] == .typed);
    try std.testing.expectEqualStrings("unitLiteral", reader.utf8(reader.decoded(reader.property(nodes[0], "kind").?)));
    try expectTypedObjects(root_value);
    const validated = reader.readTypedDocument(request.allocator(), root_value);
    try std.testing.expect(validated.root == .typed);
    try std.testing.expectEqualStrings(expected_fingerprint, validated.fingerprints.?.snapshot);
    try std.testing.expectEqualStrings(expected_canonical, validated.canonical);
    try std.testing.expect(reader.executionIrOf(request.allocator(), root_value) != null);
}

fn expectTypedObjects(value: V) anyerror!void {
    switch (value) {
        .typed => for (reader.objectMembers(value)) |member| try expectTypedObjects(member.value),
        .array => |items| for (items) |item| try expectTypedObjects(item),
        .object => return error.ParsedModelObjectRetained,
        else => {},
    }
}

test "host typed ingestion preserves supported normalization and ordered rejection diagnostics" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    reader.useAllocator(a);
    const unit = "{\"id\":\"f\",\"displayName\":\"F\",\"parameters\":[],\"resultType\":{\"kind\":\"primitive\",\"primitive\":\"unit\"},\"requiredEffects\":[],\"body\":{\"nodes\":[{\"id\":\"n\",\"kind\":\"unitLiteral\"}],\"resultNodeId\":\"n\"}}";
    for ([_]u8{ 9, 20, 21, 22, 23, 24, 25 }) |version| {
        const source = try fixture(a, version, unit);
        try expectSessionParity(a, source);
    }
    const invalid_functions = [_][]const u8{
        "{\"id\":\"f\",\"displayName\":\"F\",\"parameters\":[],\"resultType\":{\"kind\":\"primitive\",\"primitive\":\"unit\"},\"requiredEffects\":[\"missing\"],\"body\":{\"nodes\":[{\"id\":\"n\",\"kind\":\"parameterReference\",\"parameterId\":\"missing\"}],\"resultNodeId\":\"missing\"}}",
        "{\"id\":\"f\",\"displayName\":\"F\",\"parameters\":[{\"id\":\"p\",\"displayName\":\"P\",\"type\":{\"kind\":\"named\",\"typeId\":\"missing\"}},{\"id\":\"p\",\"displayName\":\"Q\",\"type\":{\"kind\":\"primitive\",\"primitive\":\"unit\"}}],\"resultType\":{\"kind\":\"primitive\",\"primitive\":\"unit\"},\"requiredEffects\":[],\"body\":{\"nodes\":[{\"id\":\"n\",\"kind\":\"unitLiteral\"},{\"id\":\"n\",\"kind\":\"int64Literal\",\"value\":5}],\"resultNodeId\":\"n\"}}",
        "{\"id\":\"f\",\"displayName\":\"F\",\"parameters\":[],\"resultType\":{\"kind\":\"primitive\",\"primitive\":\"unit\"},\"requiredEffects\":[],\"body\":{\"nodes\":[{\"id\":\"n\",\"kind\":\"unknown\"}],\"resultNodeId\":\"missing\"}}",
        "{\"id\":\"f\",\"displayName\":\"F\",\"parameters\":[],\"resultType\":{\"kind\":\"primitive\",\"primitive\":\"unit\"},\"requiredEffects\":[],\"body\":{\"resultNodeId\":\"missing\"}}",
    };
    for (invalid_functions) |function| try expectSessionParity(a, try fixture(a, 24, function));
    try expectSessionParity(a, "{");
    try expectSessionParity(a, "{\"schemaVersion\":24,\"schemaVersion\":23,\"snapshotId\":\"s\",\"types\":[],\"effects\":[],\"capabilities\":[],\"functions\":[],\"obligations\":[]}");
    const future_feature = try std.mem.replaceOwned(u8, a, unit, "\"parameters\":[]", "\"mustNotWait\":true,\"parameters\":[]");
    try expectSessionParity(a, try fixture(a, 21, future_feature));

    // Invalid references are represented by typed IDs; only validation resolves them.
    var boundary = std.heap.ArenaAllocator.init(std.testing.allocator);
    const source = try fixture(boundary.allocator(), 24, invalid_functions[0]);
    const decoded = try reader.parseValue(boundary.allocator(), source);
    const owned = try reader.ownTyped(a, decoded, false);
    boundary.deinit();
    reader.beginRequest(a);
    try expectTypedObjects(owned);
    const rejected = reader.readTypedDocument(a, owned);
    try std.testing.expect(rejected.validation_issues.len > 0);
    try std.testing.expect(rejected.fingerprints == null);
}

fn fixture(a: A, version: u8, functions: []const u8) ![]const u8 {
    return std.fmt.allocPrint(a, "{{\"schemaVersion\":{d},\"snapshotId\":\"s\",\"types\":[],\"effects\":[],\"capabilities\":[],\"functions\":[{s}],\"obligations\":[]}}", .{ version, functions });
}
fn expectSessionParity(a: A, source: []const u8) !void {
    const expected = reader.readDocument(a, source);
    const found = reader.readSessionDocument(a, source);
    try std.testing.expectEqualStrings(expected.lines, found.lines);
    try std.testing.expectEqual(expected.validation_issues.len, found.validation_issues.len);
    try std.testing.expectEqual(expected.fingerprints == null, found.fingerprints == null);
    if (expected.fingerprints) |fingerprints| {
        try std.testing.expect(found.root == .typed);
        try expectTypedObjects(found.root);
        try std.testing.expectEqualStrings(fingerprints.snapshot, found.fingerprints.?.snapshot);
        try std.testing.expectEqualStrings(fingerprints.semantic, found.fingerprints.?.semantic);
        try std.testing.expectEqualStrings(expected.canonical, found.canonical);
        try std.testing.expectEqualStrings(expected.digest, found.digest);
    }
}

test "typed dependency merge and collision policy match wire reference without semantic reparsing" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    reader.useAllocator(a);
    const function = "{\"id\":\"f\",\"displayName\":\"F\",\"parameters\":[],\"resultType\":{\"kind\":\"primitive\",\"primitive\":\"unit\"},\"requiredEffects\":[],\"body\":{\"nodes\":[{\"id\":\"n\",\"kind\":\"unitLiteral\"}],\"resultNodeId\":\"n\"}}";
    const source = try fixture(a, 24, function);
    const dependency = reader.readSessionDocument(a, source);
    try std.testing.expect(dependency.fingerprints != null);
    const local = try std.fmt.allocPrint(a, "{{\"schemaVersion\":24,\"snapshotId\":\"local\",\"dependency\":{{\"snapshotId\":\"s\",\"digest\":\"{s}\"}},\"types\":[],\"effects\":[],\"capabilities\":[],\"functions\":[],\"obligations\":[]}}", .{dependency.digest});
    const expected = reader.readResolvedDocument(a, dependency.canonical, dependency.id, dependency.digest, local);
    const found = reader.readSessionResolvedDocument(a, dependency.root, dependency.id, dependency.digest, local);
    try std.testing.expectEqualStrings(expected.lines, found.lines);
    try std.testing.expect(found.root == .typed);
    try expectTypedObjects(found.root);
    try std.testing.expectEqualStrings(expected.fingerprints.?.snapshot, found.fingerprints.?.snapshot);
    try std.testing.expectEqualStrings(expected.fingerprints.?.semantic, found.fingerprints.?.semantic);
    const wrong_marker = try std.mem.replaceOwned(u8, a, local, dependency.digest, "wrong");
    const expected_wrong = reader.readResolvedDocument(a, dependency.canonical, dependency.id, dependency.digest, wrong_marker);
    const found_wrong = reader.readSessionResolvedDocument(a, dependency.root, dependency.id, dependency.digest, wrong_marker);
    try std.testing.expectEqualStrings(expected_wrong.lines, found_wrong.lines);
    const duplicated_local = try std.mem.replaceOwned(u8, a, local, "\"functions\":[]", try std.fmt.allocPrint(a, "\"functions\":[{s}]", .{function}));
    const expected_duplicate = reader.readResolvedDocument(a, dependency.canonical, dependency.id, dependency.digest, duplicated_local);
    const found_duplicate = reader.readSessionResolvedDocument(a, dependency.root, dependency.id, dependency.digest, duplicated_local);
    try std.testing.expectEqualStrings(expected_duplicate.lines, found_duplicate.lines);
    try std.testing.expect(found_duplicate.validation_issues.len > 0);

    const combined = reader.combineSession(a, &.{ dependency.root, dependency.root }, "combined");
    try std.testing.expect(combined == .root);
    const combined_read = reader.readTypedDocument(a, combined.root);
    try std.testing.expect(combined_read.fingerprints != null);
    const old_combined = reader.combine(a, &.{ dependency.canonical, dependency.canonical }, "combined");
    try std.testing.expect(old_combined == .text);
    const old_read = reader.readDocument(a, old_combined.text);
    try std.testing.expectEqualStrings(old_read.fingerprints.?.snapshot, combined_read.fingerprints.?.snapshot);
    try std.testing.expectEqualStrings(old_read.canonical, combined_read.canonical);
    const conflicting_source = try std.mem.replaceOwned(u8, a, source, "\"displayName\":\"F\"", "\"displayName\":\"Other\"");
    const other = reader.readSessionDocument(a, conflicting_source);
    const conflict = reader.combineSession(a, &.{ dependency.root, other.root }, "combined");
    const old_conflict = reader.combine(a, &.{ dependency.canonical, other.canonical }, "combined");
    try std.testing.expect(conflict == .conflict);
    try std.testing.expect(old_conflict == .conflict);
    try std.testing.expectEqualStrings(old_conflict.conflict.kind, conflict.conflict.kind);
    try std.testing.expectEqualStrings(old_conflict.conflict.id, conflict.conflict.id);
    for (old_conflict.conflict.names, conflict.conflict.names) |old_name, name| try std.testing.expectEqualStrings(old_name.?, name.?);
}

test "typed ingestion preserves unpaired literal UTF16 for semantic rejection" {
    var owned_arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer owned_arena.deinit();
    const a = owned_arena.allocator();
    reader.useAllocator(a);
    const dependency = reader.readSessionDocument(a, try fixture(a, 24, ""));
    try std.testing.expect(dependency.root == .typed);
    for ([_][]const u8{ "\\uD800", "\\uDC00" }) |literal| {
        var boundary = std.heap.ArenaAllocator.init(std.testing.allocator);
        const boundary_a = boundary.allocator();
        const function = try std.fmt.allocPrint(boundary_a, "{{\"id\":\"f\",\"displayName\":\"F\",\"parameters\":[],\"resultType\":{{\"kind\":\"primitive\",\"primitive\":\"string\"}},\"requiredEffects\":[],\"body\":{{\"nodes\":[{{\"id\":\"n\",\"kind\":\"stringLiteral\",\"value\":\"{s}\"}}],\"resultNodeId\":\"n\"}}}}", .{literal});
        const source = try fixture(boundary_a, 24, function);
        const reference = reader.readDocument(a, source);
        const plain = reader.readSessionDocument(a, source);
        try expectSurrogateIssue(plain);
        try std.testing.expectEqualStrings(reference.lines, plain.lines);
        const decoded = try reader.parseValue(boundary_a, source);
        const candidate = reader.readCandidate(a, decoded);
        try expectSurrogateIssue(candidate);
        try std.testing.expectEqualStrings(reference.lines, candidate.lines);
        const local = try std.mem.replaceOwned(u8, boundary_a, source, "\"snapshotId\":\"s\"", try std.fmt.allocPrint(boundary_a, "\"snapshotId\":\"local\",\"dependency\":{{\"snapshotId\":\"s\",\"digest\":\"{s}\"}}", .{dependency.digest}));
        const resolved = reader.readSessionResolvedDocument(a, dependency.root, dependency.id, dependency.digest, local);
        try expectSurrogateIssue(resolved);
        const strict_id = try std.mem.replaceOwned(u8, boundary_a, source, "\"id\":\"f\"", "\"id\":\"\\uD800\"");
        const rejected_id = reader.readSessionDocument(a, strict_id);
        try std.testing.expect(std.mem.indexOf(u8, rejected_id.lines, "InvalidShape") != null);
        try std.testing.expectEqual(@as(usize, 0), rejected_id.validation_issues.len);
        const paired = try std.mem.replaceOwned(u8, boundary_a, source, literal, "\\uD800\\uDC00");
        try std.testing.expect(reader.readSessionDocument(a, paired).fingerprints != null);
        reader.useAllocator(a);
        const owned = try reader.ownTyped(a, decoded, false);
        boundary.deinit();
        reader.beginRequest(a);
        try expectTypedObjects(owned);
        const node = reader.arrayItems(reader.property(reader.arrayItems(owned, "functions")[0], "body").?, "nodes")[0];
        try std.testing.expectEqual(@as(usize, 1), reader.property(node, "value").?.string.len);
        try expectSurrogateIssue(reader.readTypedDocument(a, owned));
    }
}
fn expectSurrogateIssue(read: reader.DocumentRead) !void {
    try std.testing.expect(read.fingerprints == null);
    try std.testing.expectEqual(@as(usize, 1), read.validation_issues.len);
    try std.testing.expectEqualStrings("StringLiteralUnpairedSurrogate", read.validation_issues[0].code);
}
