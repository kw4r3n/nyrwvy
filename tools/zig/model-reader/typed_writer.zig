//! Direct serialization of session typed records.
const std = @import("std");
const reader = @import("main.zig");
const tm = @import("typed.zig");
const Domain = tm.Domain;
const Id = tm.Id;
const Interner = tm.Interner;
const TypeRef = tm.TypeRef;
const Typed = tm.Typed;
const TypeParameter = tm.TypeParameter;
const Binding = tm.Binding;
const Body = tm.Body;
const Generation = tm.Generation;
const none = tm.none;
const kindTable = tm.kindTable;

// ---------------------------------------------------------------- writing

const Writer = std.Io.Writer;

const Emitter = struct {
    out: *Writer,
    interner: *const Interner,
    arena: std.mem.Allocator,
    version: i32,
    neutral: bool = false,
    canonical_order: bool = true,
    names: std.AutoHashMapUnmanaged(Id, []const u16) = .empty,

    fn ordered(self: Emitter, comptime T: type, comptime domain: Domain, items: []const T) ![]const T {
        if (!self.canonical_order) return items;
        const result = try self.arena.dupe(T, items);
        std.mem.sort(T, result, self.interner, struct {
            fn less(i: *const Interner, x: T, y: T) bool {
                return i.less(domain, x.id, y.id);
            }
        }.less);
        return result;
    }

    fn ids(self: Emitter, domain: Domain, items: []const Id, sort: bool) !void {
        const values = try self.arena.dupe(Id, items);
        if (sort and self.canonical_order) std.mem.sort(Id, values, .{ self.interner, domain }, struct {
            fn less(ctx: struct { *const Interner, Domain }, x: Id, y: Id) bool {
                return ctx[0].less(ctx[1], x, y);
            }
        }.less);
        try self.out.writeByte('[');
        for (values, 0..) |value, index| {
            if (index > 0) try self.out.writeByte(',');
            try self.id(domain, value);
        }
        try self.out.writeByte(']');
    }

    fn display(self: Emitter, handle: u32) !void {
        if (self.neutral) try self.out.writeAll("\"_\"") else try self.string(handle);
    }

    fn id(self: Emitter, domain: Domain, value: Id) !void {
        try reader.writeString(self.out, self.interner.idText(domain, value));
    }

    fn string(self: Emitter, handle: u32) !void {
        try reader.writeString(self.out, self.interner.stringText(handle));
    }

    fn key(self: Emitter, name: []const u8) !void {
        try self.out.print(",\"{s}\":", .{name});
    }

    fn typeRef(self: Emitter, t: TypeRef) anyerror!void {
        switch (t) {
            .primitive => |name| {
                try self.out.writeAll("{\"kind\":\"primitive\",\"primitive\":");
                try self.string(name);
            },
            .named => |named| {
                try self.out.writeAll("{\"kind\":\"named\",\"typeId\":");
                try self.id(.type, named.id);
                if (named.arguments) |arguments| {
                    try self.key("arguments");
                    try self.typeRefs(arguments);
                }
            },
            .function => |function| {
                try self.out.writeAll("{\"kind\":\"function\",\"parameters\":");
                try self.typeRefs(function.parameters);
                try self.key("resultType");
                try self.typeRef(function.result.*);
            },
            .type_parameter => |value| {
                try self.out.writeAll("{\"kind\":\"typeParameter\",\"typeParameterId\":");
                try self.id(.type_parameter, value);
                if (self.version < 21) {
                    try self.key("displayName");
                    try reader.writeString(self.out, self.names.get(value) orelse self.interner.idText(.type_parameter, value));
                }
            },
            .capability => |value| {
                try self.out.writeAll("{\"kind\":\"capability\",\"capabilityId\":");
                try self.id(.capability, value);
            },
            .generic => |generic| {
                try self.out.writeAll("{\"kind\":\"builtinGeneric\",\"generic\":");
                try self.string(generic.name);
                try self.key("arguments");
                try self.typeRefs(generic.arguments);
            },
        }
        try self.out.writeByte('}');
    }

    fn typeRefs(self: Emitter, items: []const TypeRef) !void {
        try self.out.writeByte('[');
        for (items, 0..) |item, index| {
            if (index > 0) try self.out.writeByte(',');
            try self.typeRef(item);
        }
        try self.out.writeByte(']');
    }

    fn typed(self: Emitter, comptime domain: Domain, items: []const Typed) !void {
        try self.out.writeByte('[');
        for (if (domain == .field) try self.ordered(Typed, domain, items) else items, 0..) |item, index| {
            if (index > 0) try self.out.writeByte(',');
            try self.out.writeAll("{\"id\":");
            try self.id(domain, item.id);
            try self.key("displayName");
            try self.display(item.name);
            try self.key("type");
            try self.typeRef(item.type);
            try self.out.writeByte('}');
        }
        try self.out.writeByte(']');
    }

    fn head(self: Emitter, domain: Domain, value: Id, name: u32, exported: bool) !void {
        try self.out.writeAll("{\"id\":");
        try self.id(domain, value);
        try self.key("displayName");
        try self.display(name);
        if (exported and self.version >= 21) try self.out.writeAll(",\"exported\":true");
    }

    fn typeParameters(self: Emitter, parameters: ?[]const TypeParameter) !void {
        const items = parameters orelse return;
        try self.key("typeParameters");
        try self.out.writeByte('[');
        for (items, 0..) |item, index| {
            if (index > 0) try self.out.writeByte(',');
            try self.out.writeAll("{\"typeParameterId\":");
            try self.id(.type_parameter, item.id);
            try self.key("displayName");
            if (self.neutral) {
                if (index == 0) try self.out.writeAll("\"_\"") else try self.out.print("\"_{d}\"", .{index});
            } else try self.string(item.name);
            try self.out.writeByte('}');
        }
        try self.out.writeByte(']');
    }

    fn binding(self: Emitter, value: Binding) !void {
        try self.out.writeAll("{\"id\":");
        try self.id(.binding, value.id);
        if (!self.neutral) if (value.name) |name| {
            try self.key("displayName");
            try self.string(name);
        };
        try self.key("type");
        try self.typeRef(value.type);
        try self.out.writeByte('}');
    }

    fn graph(self: Emitter, body: *const Body, index: u32) anyerror!void {
        const g = body.graphs[index];
        const nodes = try self.arena.alloc(tm.Node, g.count);
        for (nodes, g.first..) |*node_ref, at| node_ref.* = body.node(at);
        if (self.canonical_order) std.mem.sort(tm.Node, nodes, self.interner, struct {
            fn less(i: *const Interner, x: tm.Node, y: tm.Node) bool {
                return i.less(.node, x.id(), y.id());
            }
        }.less);
        try self.out.writeAll("{\"nodes\":[");
        for (nodes, 0..) |node_ref, at| {
            if (at > 0) try self.out.writeByte(',');
            try self.node(node_ref);
        }
        try self.out.writeAll("],\"resultNodeId\":");
        try self.id(.node, g.result);
        try self.out.writeByte('}');
    }

    fn targets(self: Emitter, values: tm.Targets, target: []const u8, domain: Domain) !void {
        const items = try self.arena.alloc(tm.Target, values.len());
        for (items, 0..) |*item, index| item.* = values.at(index);
        if (self.canonical_order) std.mem.sort(tm.Target, items, .{ self.interner, domain }, struct {
            fn less(ctx: struct { *const Interner, Domain }, x: tm.Target, y: tm.Target) bool {
                if (x.target != y.target) return ctx[0].less(ctx[1], x.target, y.target);
                return ctx[1] != .field and ctx[0].less(.node, x.value, y.value);
            }
        }.less);
        try self.out.writeByte('[');
        for (items, 0..) |item, index| {
            if (index > 0) try self.out.writeByte(',');
            try self.out.print("{{\"{s}\":", .{target});
            try self.id(domain, item.target);
            try self.key("valueNodeId");
            try self.id(.node, item.value);
            try self.out.writeByte('}');
        }
        try self.out.writeByte(']');
    }

    fn unionArms(self: Emitter, arms: tm.UnionArms) !void {
        const items = try self.arena.alloc(tm.UnionArm, arms.len());
        var iterator = arms.iterator();
        for (items) |*item| {
            item.* = iterator.next().?;
            const variants = try self.arena.dupe(Id, item.variants);
            if (self.canonical_order) std.mem.sort(Id, variants, self.interner, struct {
                fn less(i: *const Interner, x: Id, y: Id) bool {
                    return i.less(.variant, x, y);
                }
            }.less);
            item.variants = variants;
        }
        if (self.canonical_order) std.mem.sort(tm.UnionArm, items, self.interner, struct {
            fn less(i: *const Interner, x: tm.UnionArm, y: tm.UnionArm) bool {
                return i.less(.variant, x.variants[0], y.variants[0]);
            }
        }.less);
        try self.out.writeByte('[');
        for (items, 0..) |arm, index| {
            if (index > 0) try self.out.writeByte(',');
            if (arm.variants.len == 1) {
                try self.out.writeAll("{\"variantId\":");
                try self.id(.variant, arm.variants[0]);
            } else {
                try self.out.writeAll("{\"variantIds\":");
                try self.ids(.variant, arm.variants, false);
            }
            const fields = try self.arena.alloc(tm.FieldBinding, arm.fields.len());
            for (fields, 0..) |*field, at| field.* = arm.fields.at(at);
            if (self.canonical_order) std.mem.sort(tm.FieldBinding, fields, self.interner, struct {
                fn less(i: *const Interner, x: tm.FieldBinding, y: tm.FieldBinding) bool {
                    return i.less(.field, x.source, y.source);
                }
            }.less);
            try self.out.writeAll(",\"fieldBindings\":[");
            for (fields, 0..) |field, at| {
                if (at > 0) try self.out.writeByte(',');
                try self.out.writeAll("{\"sourceFieldId\":");
                try self.id(.field, field.source);
                try self.key("binding");
                try self.binding(field.binding.*);
                try self.out.writeByte('}');
            }
            try self.out.writeAll("],\"body\":");
            try self.graph(arm.body.body, arm.body.index);
            try self.out.writeByte('}');
        }
        try self.out.writeByte(']');
    }

    fn node(self: Emitter, node_ref: tm.Node) !void {
        const kind = kindTable()[node_ref.body.kinds[node_ref.index]];
        const fields = try self.arena.alloc(tm.PayloadField, kind.reads.len);
        var iterator = node_ref.payload();
        for (fields) |*field| field.* = iterator.next().?;
        try self.out.writeAll("{\"id\":");
        try self.id(.node, node_ref.id());
        try self.out.print(",\"kind\":\"{s}\"", .{kind.name});
        for (kind.writes) |index| {
            const field = fields[index];
            switch (field.value) {
                .optid => |value| if (value == null) {
                    continue;
                },
                .targs => |value| if (value.len() == 0) {
                    continue;
                },
                .ctx => |value| if (value.len() == 0) {
                    continue;
                },
                else => {},
            }
            try self.key(field.read.name);
            switch (field.value) {
                .id => |value| try self.id(field.read.domain.?, value),
                .optid => |value| try self.id(field.read.domain.?, value.?),
                .ids => |values| try self.ids(field.read.domain.?, values, false),
                .type => |value| try self.typeRef(value),
                .targs => |values| {
                    try self.out.writeByte('[');
                    for (0..values.len()) |at| {
                        if (at > 0) try self.out.writeByte(',');
                        try self.typeRef(values.at(at));
                    }
                    try self.out.writeByte(']');
                },
                .graph => |value| try self.graph(value.body, value.index),
                .varm, .errarm => |value| {
                    try self.out.writeAll(if (field.read.shape == .varm) "{\"valueBinding\":" else "{\"errorBinding\":");
                    try self.binding(value.binding.*);
                    try self.key("body");
                    try self.graph(value.body.body, value.body.index);
                    try self.out.writeByte('}');
                },
                .ctx, .args => |value| try self.targets(value, "targetParameterId", .parameter),
                .capargs => |value| try self.targets(value, "targetCapabilityParameterId", .capability_parameter),
                .fields => |value| try self.targets(value, "targetFieldId", .field),
                .lit => |value| try self.string(value),
                .bool => |value| try self.out.writeAll(if (value) "true" else "false"),
                .int => |value| try self.out.print("{d}", .{value}),
                .bits => |value| try self.out.print("\"{x:0>16}\"", .{value}),
                .mode => |value| try self.out.print("\"{s}\"", .{@tagName(value)}),
                .impls => |value| {
                    const items = try self.arena.alloc(tm.Implementation, value.len());
                    for (items, 0..) |*item, at| item.* = value.at(at);
                    if (self.canonical_order) std.mem.sort(tm.Implementation, items, self.interner, struct {
                        fn less(i: *const Interner, x: tm.Implementation, y: tm.Implementation) bool {
                            return i.less(.operation, x.operation, y.operation);
                        }
                    }.less);
                    try self.out.writeByte('[');
                    for (items, 0..) |item, at| {
                        if (at > 0) try self.out.writeByte(',');
                        try self.out.writeAll("{\"operationId\":");
                        try self.id(.operation, item.operation);
                        try self.key("functionId");
                        try self.id(.function, item.function);
                        try self.out.writeByte('}');
                    }
                    try self.out.writeByte(']');
                },
                .arms => |value| try self.unionArms(value),
            }
        }
        try self.out.writeByte('}');
    }
};

/// Canonical snapshot bytes directly from typed records; no model-tree round trip.
pub fn writeDocument(out: *Writer, interner: *const Interner, generation: *const Generation, version: i32) !void {
    var scratch = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer scratch.deinit();
    return write(out, scratch.allocator(), interner, generation, version, false);
}

fn write(out: *Writer, arena: std.mem.Allocator, interner: *const Interner, generation: *const Generation, version: i32, neutral: bool) !void {
    var e: Emitter = .{ .out = out, .interner = interner, .arena = arena, .version = version, .neutral = neutral };
    inline for (.{ generation.types, generation.functions }) |group| for (group) |declaration| {
        for (declaration.type_parameters orelse &.{}, 0..) |parameter, index| {
            const name = if (!neutral) interner.stringText(parameter.name) else blk: {
                const ascii = if (index == 0) "_" else try std.fmt.allocPrint(arena, "_{d}", .{index});
                break :blk try std.unicode.utf8ToUtf16LeAlloc(arena, ascii);
            };
            try e.names.put(arena, parameter.id, name);
        }
    };
    try out.print("{{\"schemaVersion\":{d},\"snapshotId\":", .{version});
    if (neutral) try out.writeAll("\"semantic-fingerprint-root\"") else try e.id(.snapshot, generation.snapshot);
    if (!neutral) if (generation.marker) |marker| {
        try out.writeAll(",\"dependency\":{\"snapshotId\":");
        try reader.writeString(out, marker.snapshot);
        try out.writeAll(",\"digest\":");
        try reader.writeString(out, marker.digest);
        try out.writeByte('}');
    };
    if (!neutral and generation.parent != none) {
        try e.key("parentSnapshotId");
        try e.id(.snapshot, generation.parent);
    }
    try out.writeAll(",\"types\":[");
    for (try e.ordered(*const tm.Type, .type, generation.types), 0..) |t, index| {
        if (index > 0) try out.writeByte(',');
        try writeType(e, t);
    }
    try out.writeAll("],\"effects\":[");
    for (try e.ordered(*const tm.Effect, .effect, generation.effects), 0..) |effect, index| {
        if (index > 0) try out.writeByte(',');
        try writeEffect(e, effect);
    }
    try out.writeAll("],\"capabilities\":[");
    for (try e.ordered(*const tm.Capability, .capability, generation.capabilities), 0..) |capability, index| {
        if (index > 0) try out.writeByte(',');
        try writeCapability(e, capability);
    }
    try out.writeAll("],\"functions\":[");
    for (try e.ordered(*const tm.Function, .function, generation.functions), 0..) |function, index| {
        if (index > 0) try out.writeByte(',');
        try writeFunction(e, function);
    }
    try out.writeAll("],\"obligations\":[");
    const obligations = try arena.dupe(*const tm.Obligation, generation.obligations);
    std.mem.sort(*const tm.Obligation, obligations, .{ interner, neutral }, struct {
        fn less(ctx: struct { *const Interner, bool }, x: *const tm.Obligation, y: *const tm.Obligation) bool {
            const i = ctx[0];
            if (!ctx[1]) return i.less(.obligation, x.id, y.id);
            if (x.call != y.call) return i.less(.node, x.call, y.call);
            if (x.parameter != y.parameter) return i.less(.parameter, x.parameter, y.parameter);
            if (x.resolution) |xr| {
                if (y.resolution) |yr| return i.less(.node, xr.value, yr.value);
                return false;
            }
            return y.resolution != null;
        }
    }.less);
    for (obligations, 0..) |obligation, index| {
        if (index > 0) try out.writeByte(',');
        try out.writeAll("{\"id\":");
        if (neutral) try out.print("\"semantic-fingerprint-obligation-{d:0>8}\"", .{index}) else try e.id(.obligation, obligation.id);
        try out.writeAll(",\"kind\":\"missingArgument\",\"originTransactionId\":");
        if (neutral) try out.writeAll("\"semantic-fingerprint-transaction\"") else try e.id(.transaction, obligation.origin);
        try e.key("callNodeId");
        try e.id(.node, obligation.call);
        try e.key("parameterId");
        try e.id(.parameter, obligation.parameter);
        if (obligation.resolution) |resolution| {
            try out.writeAll(",\"status\":\"resolved\",\"resolutionTransactionId\":");
            if (neutral) try out.writeAll("\"semantic-fingerprint-transaction\"") else try e.id(.transaction, resolution.transaction);
            try e.key("resolutionValueNodeId");
            try e.id(.node, resolution.value);
        } else try out.writeAll(",\"status\":\"open\"");
        try out.writeByte('}');
    }
    try out.writeAll("]}");
}

const Minimum = struct {
    version: i32 = 9,
    interner: *const Interner,

    fn feature(self: *Minimum, name: []const u8) void {
        var lines = std.mem.tokenizeScalar(u8, @embedFile("schema_minimums.txt"), '\n');
        _ = lines.next();
        while (lines.next()) |line| {
            var parts = std.mem.splitScalar(u8, line, '|');
            const version = std.fmt.parseInt(i32, parts.next().?, 10) catch unreachable;
            if (std.mem.eql(u8, parts.next().?, name)) {
                self.version = @max(self.version, version);
                return;
            }
        }
        unreachable;
    }

    fn typeRef(self: *Minimum, value: TypeRef) void {
        switch (value) {
            .primitive => |name| if (equalText(self.interner.stringText(name), "bytes")) {
                self.feature("Bytes");
            },
            .named => |named| if (named.arguments) |arguments| {
                self.feature("GenericTypes");
                for (arguments) |argument| self.typeRef(argument);
            },
            .function => |function| {
                for (function.parameters) |parameter| self.typeRef(parameter);
                self.typeRef(function.result.*);
            },
            .generic => |generic| {
                const name = self.interner.stringText(generic.name);
                inline for (.{ .{ "exception", "ExceptionBoundary" }, .{ "oneShot", "OneShot" }, .{ "map", "Map" }, .{ "set", "Set" } }) |entry| {
                    if (equalText(name, entry[0])) self.feature(entry[1]);
                }
                for (generic.arguments) |argument| self.typeRef(argument);
            },
            .type_parameter, .capability => {},
        }
    }

    fn members(self: *Minimum, items: []const Typed) void {
        for (items) |item| self.typeRef(item.type);
    }

    fn node(self: *Minimum, value: tm.Node) void {
        var lines = std.mem.tokenizeScalar(u8, @embedFile("schema_minimums.txt"), '\n');
        _ = lines.next();
        while (lines.next()) |line| {
            var parts = std.mem.splitScalar(u8, line, '|');
            const version = std.fmt.parseInt(i32, parts.next().?, 10) catch unreachable;
            _ = parts.next();
            _ = parts.next();
            var names = std.mem.tokenizeScalar(u8, parts.next().?, ' ');
            while (names.next()) |name| if (std.mem.eql(u8, name, value.kind())) {
                self.version = @max(self.version, version);
            };
        }
        var payload = value.payload();
        while (payload.next()) |field| switch (field.value) {
            .type => |t| self.typeRef(t),
            .targs => |args| {
                if (args.len() > 0) self.feature("GenericTypes");
                for (0..args.len()) |index| self.typeRef(args.at(index));
            },
            .optid => |id| if (id != null and std.mem.eql(u8, value.kind(), "taskWait")) {
                self.feature("TimedTaskWait");
            },
            .arms => |arms| {
                var iterator = arms.iterator();
                while (iterator.next()) |arm| if (arm.variants.len > 1) {
                    self.feature("SharedUnionMatchArm");
                };
            },
            else => {},
        };
    }
};

fn equalText(text: []const u16, ascii: []const u8) bool {
    if (text.len != ascii.len) return false;
    for (text, ascii) |unit, byte| if (unit != byte) return false;
    return true;
}

fn occurring(arena: std.mem.Allocator, value: TypeRef, found: *std.ArrayList(Id)) anyerror!void {
    switch (value) {
        .type_parameter => |id| {
            if (std.mem.indexOfScalar(Id, found.items, id) == null) try found.append(arena, id);
        },
        .named => |named| if (named.arguments) |arguments| {
            for (arguments) |argument| try occurring(arena, argument, found);
        },
        .function => |function| {
            for (function.parameters) |parameter| try occurring(arena, parameter, found);
            try occurring(arena, function.result.*, found);
        },
        .generic => |generic| {
            for (generic.arguments) |argument| try occurring(arena, argument, found);
        },
        else => {},
    }
}

pub fn minimumVersion(arena: std.mem.Allocator, interner: *const Interner, generation: *const Generation) !i32 {
    var minimum: Minimum = .{ .interner = interner };
    for (generation.types) |t| {
        if (!t.exported) minimum.feature("DeclarationVisibility");
        if (t.type_parameters != null) minimum.feature("GenericTypes");
        minimum.members(t.fields);
        for (t.variants) |variant| minimum.members(variant.fields);
    }
    for (generation.effects) |effect| if (!effect.exported) {
        minimum.feature("DeclarationVisibility");
    };
    for (generation.capabilities) |capability| {
        if (!capability.exported) minimum.feature("DeclarationVisibility");
        if (capability.task_bound) minimum.feature("WaitAttributes");
        for (capability.operations) |operation| {
            if (operation.may_wait) minimum.feature("WaitAttributes");
            minimum.members(operation.parameters);
            minimum.typeRef(operation.result);
            if (operation.failure) |failure| minimum.typeRef(failure);
        }
    }
    for (generation.functions) |function| {
        if (!function.exported) minimum.feature("DeclarationVisibility");
        if (function.must_not_wait) minimum.feature("WaitAttributes");
        minimum.members(function.parameters);
        minimum.typeRef(function.result);
        var implied: std.ArrayList(Id) = .empty;
        for (function.parameters) |parameter| try occurring(arena, parameter.type, &implied);
        const declared = function.type_parameters orelse &.{};
        if (declared.len != implied.items.len) {
            minimum.feature("DeclaredFunctionTypeParameters");
        } else for (declared, implied.items) |parameter, id| {
            if (parameter.id != id) {
                minimum.feature("DeclaredFunctionTypeParameters");
                break;
            }
        }
        for (function.body.types) |t| minimum.typeRef(t);
        for (function.body.bindings) |binding| minimum.typeRef(binding.type);
        for (function.body.kinds, 0..) |_, index| minimum.node(function.body.node(index));
    }
    return minimum.version;
}

fn hash(arena: std.mem.Allocator, domain: []const u8, bytes: []const u8) ![]const u8 {
    var state = std.crypto.hash.sha2.Sha256.init(.{});
    state.update(domain);
    state.update(&.{0});
    state.update(bytes);
    const digest = state.finalResult();
    return std.fmt.allocPrint(arena, "sha256:{s}", .{std.fmt.bytesToHex(digest, .lower)});
}

pub fn fingerprints(arena: std.mem.Allocator, interner: *const Interner, generation: *const Generation) !reader.Fingerprints {
    const version = try minimumVersion(arena, interner, generation);
    var canonical: Writer.Allocating = .init(arena);
    var normalized: Writer.Allocating = .init(arena);
    var plain = generation.*;
    plain.marker = null;
    try write(&canonical.writer, arena, interner, &plain, version, false);
    try write(&normalized.writer, arena, interner, &plain, version, true);
    return .{
        .snapshot = try hash(arena, "nayamnyam.snapshot-fingerprint.v6", canonical.written()),
        .semantic = try hash(arena, "nayamnyam.semantic-fingerprint.v6", normalized.written()),
        .canonical = canonical.written(),
        .normalized = normalized.written(),
    };
}

fn writeType(e: Emitter, t: *const tm.Type) !void {
    const out = e.out;

    try e.head(.type, t.id, t.name, t.exported);
    try e.typeParameters(t.type_parameters);
    if (t.product) {
        try out.writeAll(",\"kind\":\"product\",\"fields\":");
        try e.typed(.field, t.fields);
    } else {
        try out.writeAll(",\"kind\":\"union\",\"variants\":[");
        for (try e.ordered(tm.Variant, .variant, t.variants), 0..) |variant, v| {
            if (v > 0) try out.writeByte(',');
            try e.head(.variant, variant.id, variant.name, false);
            try e.key("fields");
            try e.typed(.field, variant.fields);
            try out.writeByte('}');
        }
        try out.writeByte(']');
    }
    try out.writeByte('}');
}

fn writeEffect(e: Emitter, effect: *const tm.Effect) !void {
    const out = e.out;

    try e.head(.effect, effect.id, effect.name, effect.exported);
    try out.writeByte('}');
}

fn writeCapability(e: Emitter, capability: *const tm.Capability) !void {
    const out = e.out;

    try e.head(.capability, capability.id, capability.name, capability.exported);
    if (capability.task_bound) try out.writeAll(",\"taskBound\":true");
    try out.writeAll(",\"operations\":[");
    for (try e.ordered(tm.Operation, .operation, capability.operations), 0..) |operation, o| {
        if (o > 0) try out.writeByte(',');
        try e.head(.operation, operation.id, operation.name, false);
        try e.key("parameters");
        try e.typed(.capability_parameter, operation.parameters);
        try e.key("resultType");
        try e.typeRef(operation.result);
        if (operation.failure) |failure| {
            try e.key("failureType");
            try e.typeRef(failure);
        }
        try e.key("effectId");
        try e.id(.effect, operation.effect);
        if (operation.may_wait) try out.writeAll(",\"mayWait\":true");
        try out.writeByte('}');
    }
    try out.writeAll("]}");
}

fn writeFunction(e: Emitter, function: *const tm.Function) !void {
    const out = e.out;
    const version = e.version;

    try e.head(.function, function.id, function.name, function.exported);
    if (function.must_not_wait) try out.writeAll(",\"mustNotWait\":true");
    if (version >= 21) try e.typeParameters(function.type_parameters);
    try e.key("parameters");
    try e.typed(.parameter, function.parameters);
    try e.key("resultType");
    try e.typeRef(function.result);
    try e.key("requiredEffects");
    try e.ids(.effect, function.effects, true);
    try e.key("body");
    try e.graph(&function.body, 0);
    try out.writeByte('}');
}

pub const Declaration = union(enum) { function: *const tm.Function, type: *const tm.Type, effect: *const tm.Effect, capability: *const tm.Capability, obligation: *const tm.Obligation };
pub fn writeDeclaration(out: *Writer, arena: std.mem.Allocator, interner: *const Interner, declaration: Declaration) !void {
    return writeDeclarationOrdered(out, arena, interner, declaration, true);
}

pub fn writeDeclarationInSourceOrder(out: *Writer, arena: std.mem.Allocator, interner: *const Interner, declaration: Declaration) !void {
    return writeDeclarationOrdered(out, arena, interner, declaration, false);
}

fn writeDeclarationOrdered(out: *Writer, arena: std.mem.Allocator, interner: *const Interner, declaration: Declaration, canonical_order: bool) !void {
    const emitter: Emitter = .{ .out = out, .arena = arena, .interner = interner, .version = reader.currentVersion(), .canonical_order = canonical_order };
    switch (declaration) {
        .function => |value| try writeFunction(emitter, value),
        .type => |value| try writeType(emitter, value),
        .effect => |value| try writeEffect(emitter, value),
        .capability => |value| try writeCapability(emitter, value),
        .obligation => |value| try writeObligation(emitter, value),
    }
}

fn writeObligation(e: Emitter, value: *const tm.Obligation) !void {
    try e.out.writeAll("{\"id\":");
    try e.id(.obligation, value.id);
    try e.out.writeAll(",\"kind\":\"missingArgument\",\"originTransactionId\":");
    try e.id(.transaction, value.origin);
    try e.key("callNodeId");
    try e.id(.node, value.call);
    try e.key("parameterId");
    try e.id(.parameter, value.parameter);
    if (value.resolution) |resolution| {
        try e.out.writeAll(",\"status\":\"resolved\",\"resolutionTransactionId\":");
        try e.id(.transaction, resolution.transaction);
        try e.key("resolutionValueNodeId");
        try e.id(.node, resolution.value);
    } else try e.out.writeAll(",\"status\":\"open\"");
    try e.out.writeByte('}');
}
