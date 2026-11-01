//! Canonical ID order for an imported generation, without serializing it.
const std = @import("std");
const tm = @import("typed.zig");
const A = std.mem.Allocator;

fn ordered(a: A, i: *const tm.Interner, comptime T: type, comptime domain: tm.Domain, values: []const T) ![]T {
    const result = try a.dupe(T, values);
    std.mem.sort(T, result, i, struct {
        fn less(interner: *const tm.Interner, x: T, y: T) bool {
            return interner.less(domain, x.id, y.id);
        }
    }.less);
    return result;
}
fn targets(a: A, i: *const tm.Interner, values: tm.Targets, domain: tm.Domain) !tm.Targets {
    const items = try a.alloc(tm.Target, values.len());
    for (items, 0..) |*item, n| item.* = values.at(n);
    std.mem.sort(tm.Target, items, .{ i, domain }, struct {
        fn less(ctx: struct { *const tm.Interner, tm.Domain }, x: tm.Target, y: tm.Target) bool {
            if (x.target != y.target) return ctx[0].less(ctx[1], x.target, y.target);
            return ctx[1] != .field and ctx[0].less(.node, x.value, y.value);
        }
    }.less);
    return tm.builder.targets(a, items);
}
fn graph(a: A, i: *const tm.Interner, source: tm.GraphView) anyerror!tm.Body {
    var drafts: std.ArrayList(tm.builder.NodeDraft) = .empty;
    var nodes = source.nodes();
    while (nodes.next()) |node| {
        const draft = try tm.builder.fromNode(a, node);
        const payload = @constCast(draft.payload);
        for (payload) |*field| field.* = switch (field.*) {
            .graph => |v| .{ .graph = try tm.builder.view(a, try graph(a, i, v)) },
            .varm => |v| .{ .varm = .{ .binding = v.binding, .body = try tm.builder.view(a, try graph(a, i, v.body)) } },
            .errarm => |v| .{ .errarm = .{ .binding = v.binding, .body = try tm.builder.view(a, try graph(a, i, v.body)) } },
            .ctx => |v| .{ .ctx = try targets(a, i, v, .parameter) },
            .args => |v| .{ .args = try targets(a, i, v, .parameter) },
            .capargs => |v| .{ .capargs = try targets(a, i, v, .capability_parameter) },
            .fields => |v| .{ .fields = try targets(a, i, v, .field) },
            .impls => |v| blk: {
                const items = try a.alloc(tm.Implementation, v.len());
                for (items, 0..) |*item, n| item.* = v.at(n);
                std.mem.sort(tm.Implementation, items, i, struct {
                    fn less(interner: *const tm.Interner, x: tm.Implementation, y: tm.Implementation) bool {
                        return interner.less(.operation, x.operation, y.operation);
                    }
                }.less);
                break :blk .{ .impls = try tm.builder.implementations(a, items) };
            },
            .arms => |v| blk: {
                const arms = try a.alloc(tm.UnionArm, v.len());
                var it = v.iterator();
                for (arms) |*arm| {
                    arm.* = it.next().?;
                    const variants = try a.dupe(tm.Id, arm.variants);
                    std.mem.sort(tm.Id, variants, i, struct {
                        fn less(interner: *const tm.Interner, x: tm.Id, y: tm.Id) bool {
                            return interner.less(.variant, x, y);
                        }
                    }.less);
                    arm.variants = variants;
                    const fields = try a.alloc(tm.FieldBinding, arm.fields.len());
                    for (fields, 0..) |*item, n| item.* = arm.fields.at(n);
                    std.mem.sort(tm.FieldBinding, fields, i, struct {
                        fn less(interner: *const tm.Interner, x: tm.FieldBinding, y: tm.FieldBinding) bool {
                            return interner.less(.field, x.source, y.source);
                        }
                    }.less);
                    arm.fields = try tm.builder.fieldBindings(a, fields);
                    arm.body = try tm.builder.view(a, try graph(a, i, arm.body));
                }
                std.mem.sort(tm.UnionArm, arms, i, struct {
                    fn less(interner: *const tm.Interner, x: tm.UnionArm, y: tm.UnionArm) bool {
                        return interner.less(.variant, x.variants[0], y.variants[0]);
                    }
                }.less);
                break :blk .{ .arms = try tm.builder.unionArms(a, arms) };
            },
            else => |v| v,
        };
        try drafts.append(a, draft);
    }
    std.mem.sort(tm.builder.NodeDraft, drafts.items, i, struct {
        fn less(interner: *const tm.Interner, x: tm.builder.NodeDraft, y: tm.builder.NodeDraft) bool {
            return interner.less(.node, x.id, y.id);
        }
    }.less);
    return tm.builder.build(a, drafts.items, source.result());
}
/// Canonical order for all ID-keyed collections; semantic parameter and type argument order is retained.
pub fn generation(a: A, i: *const tm.Interner, source: *const tm.Generation) !tm.Generation {
    var result = source.*;
    const types = try ordered(a, i, *const tm.Type, .type, source.types);
    for (types) |*item| {
        const copy = try a.create(tm.Type);
        copy.* = item.*.*;
        copy.fields = try ordered(a, i, tm.Typed, .field, copy.fields);
        const variants = try ordered(a, i, tm.Variant, .variant, copy.variants);
        for (variants) |*variant| variant.fields = try ordered(a, i, tm.Typed, .field, variant.fields);
        copy.variants = variants;
        item.* = copy;
    }
    result.types = types;
    result.effects = try ordered(a, i, *const tm.Effect, .effect, source.effects);
    const caps = try ordered(a, i, *const tm.Capability, .capability, source.capabilities);
    for (caps) |*item| {
        const copy = try a.create(tm.Capability);
        copy.* = item.*.*;
        copy.operations = try ordered(a, i, tm.Operation, .operation, copy.operations);
        item.* = copy;
    }
    result.capabilities = caps;
    const functions = try ordered(a, i, *const tm.Function, .function, source.functions);
    for (functions) |*item| {
        const copy = try a.create(tm.Function);
        copy.* = item.*.*;
        const effects = try a.dupe(tm.Id, copy.effects);
        std.mem.sort(tm.Id, effects, i, struct {
            fn less(interner: *const tm.Interner, x: tm.Id, y: tm.Id) bool {
                return interner.less(.effect, x, y);
            }
        }.less);
        copy.effects = effects;
        copy.body = try graph(a, i, copy.body.graph(0));
        item.* = copy;
    }
    result.functions = functions;
    result.obligations = try ordered(a, i, *const tm.Obligation, .obligation, source.obligations);
    return result;
}

test "canonical order sorts ID keyed collections and retains parameter order" {
    const reader = @import("main.zig");
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    reader.useAllocator(a);
    const root = try reader.parseValue(a,
        \\{"snapshotId":"s","types":[],"effects":[],"capabilities":[],"functions":[{"id":"z","displayName":"Z","parameters":[{"id":"p2","displayName":"two","type":{"kind":"primitive","primitive":"unit"}},{"id":"p1","displayName":"one","type":{"kind":"primitive","primitive":"unit"}}],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"nodes":[{"id":"z","kind":"unitLiteral"},{"id":"a","kind":"unitLiteral"}],"resultNodeId":"z"}},{"id":"a","displayName":"A","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"nodes":[{"id":"n","kind":"unitLiteral"}],"resultNodeId":"n"}}],"obligations":[]}
    );
    var interner: tm.Interner = .{ .gpa = a };
    defer interner.deinit();
    const before = try tm.load(a, &interner, root, null);
    const after = try generation(a, &interner, &before);
    try std.testing.expectEqualStrings("a", reader.utf8(interner.idText(.function, after.functions[0].id)));
    const f = after.functions[1];
    try std.testing.expectEqualStrings("p2", reader.utf8(interner.idText(.parameter, f.parameters[0].id)));
    const nodes = f.body.graph(0).nodes();
    try std.testing.expectEqualStrings("a", reader.utf8(interner.idText(.node, nodes.body.ids[nodes.at])));
    try std.testing.expectEqualStrings("z", reader.utf8(interner.idText(.function, before.functions[0].id)));
}

test "canonical order sorts nested graph nodes and target bindings" {
    const reader = @import("main.zig");
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    reader.useAllocator(a);
    const root = try reader.parseValue(a,
        \\{"snapshotId":"s","types":[],"effects":[],"capabilities":[],"functions":[{"id":"f","displayName":"F","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"nodes":[{"id":"outer","kind":"bind","inputNodeId":"input","boundBody":{"valueBinding":{"id":"v","type":{"kind":"primitive","primitive":"unit"}},"body":{"nodes":[{"id":"z","kind":"directCall","targetFunctionId":"f","arguments":[{"targetParameterId":"p2","valueNodeId":"x"},{"targetParameterId":"p1","valueNodeId":"x"}]},{"id":"x","kind":"unitLiteral"}],"resultNodeId":"z"}}},{"id":"input","kind":"unitLiteral"}],"resultNodeId":"outer"}}],"obligations":[]}
    );
    var interner: tm.Interner = .{ .gpa = a };
    defer interner.deinit();
    const before = try tm.load(a, &interner, root, null);
    const after = try generation(a, &interner, &before);
    var iterator = after.functions[0].body.graph(0).nodes();
    try std.testing.expectEqualStrings("input", reader.utf8(interner.idText(.node, iterator.next().?.id())));
    var payload = iterator.next().?.payload();
    _ = payload.next().?;
    const body = payload.next().?.value.varm.body;
    var nested = body.nodes();
    try std.testing.expectEqualStrings("x", reader.utf8(interner.idText(.node, nested.next().?.id())));
    var call = nested.next().?.payload();
    _ = call.next().?;
    const arguments = call.next().?.value.args;
    try std.testing.expectEqualStrings("p1", reader.utf8(interner.idText(.parameter, arguments.at(0).target)));
}
