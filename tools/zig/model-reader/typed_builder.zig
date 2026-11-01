//! Typed graph construction and rebuilding. Borrowed TypeRef subtrees retain their source arena.
const std = @import("std");
const tm = @import("typed.zig");
const Allocator = std.mem.Allocator;

pub const NodeDraft = struct {
    id: tm.Id,
    kind: u8,
    payload: []const tm.Payload,
};
pub const Replacement = struct { node: tm.Id, nodes: []const NodeDraft };
pub const Transform = struct {
    context: *anyopaque,
    apply: *const fn (*anyopaque, Allocator, tm.Node) anyerror!?[]const NodeDraft,
};
pub const Edits = struct {
    replacements: []const Replacement = &.{},
    transform: ?Transform = null,
    append: []const NodeDraft = &.{},
    result: ?tm.Id = null,
};

pub fn kindIndex(name: []const u8) !u8 {
    for (tm.kindTable(), 0..) |kind, index| if (std.mem.eql(u8, kind.name, name)) return @intCast(index);
    return error.UnknownNodeKind;
}
pub fn fromNode(a: Allocator, node: tm.Node) !NodeDraft {
    const payload = try a.alloc(tm.Payload, tm.kindTable()[node.body.kinds[node.index]].reads.len);
    var fields = node.payload();
    for (payload) |*slot| slot.* = fields.next().?.value;
    return .{ .id = node.id(), .kind = node.body.kinds[node.index], .payload = payload };
}
pub fn build(a: Allocator, nodes: []const NodeDraft, result: tm.Id) !tm.Body {
    var builder: Builder = .{ .a = a };
    _ = try builder.graph(nodes, result);
    return builder.finish();
}
pub fn rebuild(a: Allocator, graph: tm.GraphView, edits: Edits) !tm.Body {
    var builder: Builder = .{ .a = a, .edits = edits };
    _ = try builder.importGraph(graph, true);
    return builder.finish();
}
/// Post-order domain walk: every nested graph is transformed before its owning node.
/// A rewritten result node selects its replacement's last node as the graph result.
pub fn mapPostOrder(a: Allocator, source: tm.GraphView, transform: Transform) anyerror!tm.Body {
    var nodes: std.ArrayList(NodeDraft) = .empty;
    var result = source.result();
    var iterator = source.nodes();
    while (iterator.next()) |node| {
        const draft = try fromNode(a, node);
        const payload = @constCast(draft.payload);
        const kind = tm.kindTable()[draft.kind];
        for (kind.writes) |index| {
            payload[index] = switch (payload[index]) {
                .graph => |graph| .{ .graph = try view(a, try mapPostOrder(a, graph, transform)) },
                .varm, .errarm => |arm| blk: {
                    const updated: tm.BindingArm = .{ .binding = arm.binding, .body = try view(a, try mapPostOrder(a, arm.body, transform)) };
                    break :blk if (kind.reads[index].shape == .varm) .{ .varm = updated } else .{ .errarm = updated };
                },
                .arms => |arms| blk: {
                    var updated: std.ArrayList(tm.UnionArm) = .empty;
                    var arms_iterator = arms.iterator();
                    while (arms_iterator.next()) |arm| {
                        var next = arm;
                        next.body = try view(a, try mapPostOrder(a, arm.body, transform));
                        try updated.append(a, next);
                    }
                    break :blk .{ .arms = try unionArms(a, updated.items) };
                },
                else => |value| value,
            };
        }
        const temporary = try build(a, &.{draft}, draft.id);
        const replacement = try transform.apply(transform.context, a, temporary.node(0));
        const selected = replacement orelse &.{draft};
        try nodes.appendSlice(a, selected);
        if (node.id() == source.result() and selected.len > 0) result = selected[selected.len - 1].id;
    }
    return build(a, nodes.items, result);
}

pub fn view(a: Allocator, body: tm.Body) !tm.GraphView {
    const owned = try a.create(tm.Body);
    owned.* = body;
    return owned.graph(0);
}
pub fn targets(a: Allocator, items: []const tm.Target) !tm.Targets {
    const words = try a.alloc(u32, items.len * 2);
    for (items, 0..) |item, index| {
        words[index * 2] = item.target;
        words[index * 2 + 1] = item.value;
    }
    return .{ .words = words };
}
pub fn implementations(a: Allocator, items: []const tm.Implementation) !tm.Implementations {
    const words = try a.alloc(u32, items.len * 2);
    for (items, 0..) |item, index| {
        words[index * 2] = item.operation;
        words[index * 2 + 1] = item.function;
    }
    return .{ .words = words };
}
pub fn typeArguments(a: Allocator, items: []const tm.TypeRef) !tm.TypeArguments {
    const indices = try a.alloc(u32, items.len);
    for (indices, 0..) |*slot, index| slot.* = @intCast(index);
    const body = try a.create(tm.Body);
    body.* = .{ .kinds = &.{}, .ids = &.{}, .starts = &.{}, .lens = &.{}, .extra = &.{}, .graphs = &.{}, .types = items, .bindings = &.{} };
    return .{ .body = body, .indices = indices };
}
pub fn unionArms(a: Allocator, arms: []const tm.UnionArm) !tm.UnionArms {
    var builder: Builder = .{ .a = a };
    var words: std.ArrayList(u32) = .empty;
    for (arms) |arm| {
        try words.append(a, @intCast(arm.variants.len));
        try words.appendSlice(a, arm.variants);
        try words.append(a, @intCast(arm.fields.len()));
        for (0..arm.fields.len()) |index| {
            const field = arm.fields.at(index);
            try words.append(a, field.source);
            try words.append(a, try builder.binding(field.binding.*));
        }
        try words.append(a, try builder.importGraph(arm.body, false));
    }
    const body = try a.create(tm.Body);
    body.* = builder.finish();
    return .{ .body = body, .words = words.items, .count = arms.len };
}
pub fn fieldBindings(a: Allocator, items: []const tm.FieldBinding) !tm.FieldBindings {
    const bindings = try a.alloc(tm.Binding, items.len);
    const words = try a.alloc(u32, items.len * 2);
    for (items, bindings, 0..) |item, *binding, index| {
        binding.* = item.binding.*;
        words[index * 2] = item.source;
        words[index * 2 + 1] = @intCast(index);
    }
    const body = try a.create(tm.Body);
    body.* = .{ .kinds = &.{}, .ids = &.{}, .starts = &.{}, .lens = &.{}, .extra = &.{}, .graphs = &.{}, .types = &.{}, .bindings = bindings };
    return .{ .body = body, .words = words };
}

const Builder = struct {
    a: Allocator,
    edits: Edits = .{},
    kinds: std.ArrayList(u8) = .empty,
    ids: std.ArrayList(tm.Id) = .empty,
    starts: std.ArrayList(u32) = .empty,
    lens: std.ArrayList(u32) = .empty,
    extra: std.ArrayList(u32) = .empty,
    graphs: std.ArrayList(tm.Graph) = .empty,
    types: std.ArrayList(tm.TypeRef) = .empty,
    bindings: std.ArrayList(tm.Binding) = .empty,

    fn finish(self: *Builder) tm.Body {
        return .{ .kinds = self.kinds.items, .ids = self.ids.items, .starts = self.starts.items, .lens = self.lens.items, .extra = self.extra.items, .graphs = self.graphs.items, .types = self.types.items, .bindings = self.bindings.items };
    }
    fn binding(self: *Builder, value: tm.Binding) !u32 {
        const index: u32 = @intCast(self.bindings.items.len);
        try self.bindings.append(self.a, value);
        return index;
    }
    fn typeRef(self: *Builder, value: tm.TypeRef) !u32 {
        const index: u32 = @intCast(self.types.items.len);
        try self.types.append(self.a, value);
        return index;
    }
    fn importGraph(self: *Builder, source: tm.GraphView, root: bool) anyerror!u32 {
        var nodes: std.ArrayList(NodeDraft) = .empty;
        var iterator = source.nodes();
        while (iterator.next()) |node| {
            var replaced = false;
            for (self.edits.replacements) |replacement| if (replacement.node == node.id()) {
                try nodes.appendSlice(self.a, replacement.nodes);
                replaced = true;
                break;
            };
            if (replaced) continue;
            if (self.edits.transform) |transform| if (try transform.apply(transform.context, self.a, node)) |replacement| {
                try nodes.appendSlice(self.a, replacement);
                continue;
            };
            try nodes.append(self.a, try fromNode(self.a, node));
        }
        if (root) try nodes.appendSlice(self.a, self.edits.append);
        return self.graph(nodes.items, if (root) self.edits.result orelse source.result() else source.result());
    }
    fn graph(self: *Builder, nodes: []const NodeDraft, result: tm.Id) anyerror!u32 {
        const index: u32 = @intCast(self.graphs.items.len);
        const first: u32 = @intCast(self.ids.items.len);
        try self.graphs.append(self.a, .{ .first = first, .count = @intCast(nodes.len), .result = result });
        for (nodes) |node| {
            if (node.kind >= tm.kindTable().len) return error.UnknownNodeKind;
            if (node.payload.len != tm.kindTable()[node.kind].reads.len) return error.InvalidPayload;
            try self.kinds.append(self.a, node.kind);
            try self.ids.append(self.a, node.id);
            try self.starts.append(self.a, 0);
            try self.lens.append(self.a, 0);
        }
        for (nodes, first..) |node, at| {
            var words: std.ArrayList(u32) = .empty;
            for (node.payload, tm.kindTable()[node.kind].reads) |payload, read| {
                if (std.meta.activeTag(payload) != read.shape) return error.InvalidPayload;
                try self.field(&words, payload);
            }
            self.starts.items[at] = @intCast(self.extra.items.len);
            self.lens.items[at] = @intCast(words.items.len);
            try self.extra.appendSlice(self.a, words.items);
        }
        return index;
    }
    fn field(self: *Builder, words: *std.ArrayList(u32), payload: tm.Payload) anyerror!void {
        switch (payload) {
            .id => |value| try words.append(self.a, value),
            .optid => |value| try words.append(self.a, value orelse tm.none),
            .ids => |values| {
                try words.append(self.a, @intCast(values.len));
                try words.appendSlice(self.a, values);
            },
            .type => |value| try words.append(self.a, try self.typeRef(value)),
            .targs => |values| {
                try words.append(self.a, @intCast(values.len()));
                for (0..values.len()) |index| try words.append(self.a, try self.typeRef(values.at(index)));
            },
            .graph => |value| try words.append(self.a, try self.importGraph(value, false)),
            .varm, .errarm => |arm| {
                try words.append(self.a, try self.binding(arm.binding.*));
                try words.append(self.a, try self.importGraph(arm.body, false));
            },
            .ctx, .args, .capargs, .fields => |values| {
                try words.append(self.a, @intCast(values.len()));
                try words.appendSlice(self.a, values.words);
            },
            .impls => |values| {
                try words.append(self.a, @intCast(values.len()));
                try words.appendSlice(self.a, values.words);
            },
            .lit => |value| try words.append(self.a, value),
            .bool => |value| try words.append(self.a, @intFromBool(value)),
            .int => |value| {
                const bits: u64 = @bitCast(value);
                try words.appendSlice(self.a, &.{ @truncate(bits), @truncate(bits >> 32) });
            },
            .bits => |bits| try words.appendSlice(self.a, &.{ @truncate(bits), @truncate(bits >> 32) }),
            .mode => |mode| try words.append(self.a, @intFromBool(mode == .all)),
            .arms => |arms| {
                try words.append(self.a, @intCast(arms.len()));
                var iterator = arms.iterator();
                while (iterator.next()) |arm| {
                    try words.append(self.a, @intCast(arm.variants.len));
                    try words.appendSlice(self.a, arm.variants);
                    try words.append(self.a, @intCast(arm.fields.len()));
                    for (0..arm.fields.len()) |index| {
                        const field_binding = arm.fields.at(index);
                        try words.append(self.a, field_binding.source);
                        try words.append(self.a, try self.binding(field_binding.binding.*));
                    }
                    try words.append(self.a, try self.importGraph(arm.body, false));
                }
            },
        }
    }
};

test "typed builder rebuilds nested graphs, replaces nodes and appends a result without changing the source" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const literal = try kindIndex("int64Literal");
    const child = try build(a, &.{.{ .id = 1, .kind = literal, .payload = &.{.{ .int = -9 }} }}, 1);
    const source = try build(a, &.{.{ .id = 2, .kind = try kindIndex("boolAnd"), .payload = &.{ .{ .id = 3 }, .{ .graph = try view(a, child) } } }}, 2);
    const replacement: NodeDraft = .{ .id = 1, .kind = literal, .payload = &.{.{ .int = 42 }} };
    const appended: NodeDraft = .{ .id = 4, .kind = literal, .payload = &.{.{ .int = 8 }} };
    const changed = try rebuild(a, source.graph(0), .{ .replacements = &.{.{ .node = 1, .nodes = &.{replacement} }}, .append = &.{appended}, .result = 4 });
    try std.testing.expectEqual(@as(tm.Id, 4), changed.graph(0).result());
    var old_payload = source.findNode(1).?.payload();
    var new_payload = changed.findNode(1).?.payload();
    try std.testing.expectEqual(@as(i64, -9), old_payload.next().?.value.int);
    try std.testing.expectEqual(@as(i64, 42), new_payload.next().?.value.int);
    try std.testing.expectEqual(@as(usize, 2), source.ids.len);
    try std.testing.expectEqual(@as(usize, 3), changed.ids.len);
}

test "typed builder rejects a payload whose shape does not match its node kind" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    try std.testing.expectError(error.InvalidPayload, build(arena.allocator(), &.{.{ .id = 1, .kind = try kindIndex("int64Literal"), .payload = &.{.{ .bool = true }} }}, 1));
}

test "postorder builder transforms nested nodes before parents and remaps graph results" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const child = try build(a, &.{.{ .id = 1, .kind = try kindIndex("int64Literal"), .payload = &.{.{ .int = 7 }} }}, 1);
    const source = try build(a, &.{.{ .id = 2, .kind = try kindIndex("boolAnd"), .payload = &.{ .{ .id = 3 }, .{ .graph = try view(a, child) } } }}, 2);
    const Context = struct {
        seen: std.ArrayList(tm.Id) = .empty,
        fn apply(raw: *anyopaque, allocator: Allocator, node: tm.Node) anyerror!?[]const NodeDraft {
            const self: *@This() = @ptrCast(@alignCast(raw));
            try self.seen.append(allocator, node.id());
            var draft = try fromNode(allocator, node);
            draft.id += 10;
            if (node.id() == 2) {
                var fields = node.payload();
                _ = fields.next();
                try std.testing.expectEqual(@as(tm.Id, 11), fields.next().?.value.graph.result());
            }
            return try allocator.dupe(NodeDraft, &.{draft});
        }
    };
    var context: Context = .{};
    const changed = try mapPostOrder(a, source.graph(0), .{ .context = &context, .apply = Context.apply });
    try std.testing.expectEqualSlices(tm.Id, &.{ 1, 2 }, context.seen.items);
    try std.testing.expectEqual(@as(tm.Id, 12), changed.graph(0).result());
    try std.testing.expect(changed.findNode(11) != null);
}
