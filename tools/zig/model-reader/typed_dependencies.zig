//! Dependency identities are merged only when direct canonical declaration bytes agree.
const std = @import("std");
const tm = @import("typed.zig");
const writer = @import("typed_writer.zig");
const Allocator = std.mem.Allocator;
pub const Member = struct { generation: *const tm.Generation, interner: *const tm.Interner };
pub const Conflict = struct { kind: []const u8, id: []const u8, names: []const ?[]const u8 };
pub const Combined = union(enum) { generation: tm.Generation, conflict: Conflict };
const groups = .{
    .{ "types", "type", tm.Type, tm.Domain.type },
    .{ "effects", "effect", tm.Effect, tm.Domain.effect },
    .{ "capabilities", "capability", tm.Capability, tm.Domain.capability },
    .{ "functions", "function", tm.Function, tm.Domain.function },
    .{ "obligations", "obligation", tm.Obligation, tm.Domain.obligation },
};

fn utf8(a: Allocator, text: []const u16) ![]const u8 {
    return std.unicode.utf16LeToUtf8Alloc(a, text);
}

/// Inputs remain immutable. A collision is detected before any entry is interned in `target`.
/// The result owns its records in `a`; `scratch` can be discarded after this call.
pub fn combine(a: Allocator, scratch: Allocator, target: *tm.Interner, members: []const Member, combined_id: []const u16) !Combined {
    inline for (groups) |group| {
        var seen: std.StringHashMapUnmanaged([]const u8) = .empty;
        for (members) |member| for (@field(member.generation, group[0])) |record| {
            const id_text = member.interner.idText(group[3], record.id);
            const key = try utf8(scratch, id_text);
            var encoded: std.Io.Writer.Allocating = .init(scratch);
            try writer.writeDeclaration(&encoded.writer, scratch, member.interner, @unionInit(writer.Declaration, group[1], record));
            if (seen.get(key)) |before| {
                if (std.mem.eql(u8, before, encoded.written())) continue;
                const names = try a.alloc(?[]const u8, members.len);
                for (members, names) |other, *name| {
                    name.* = null;
                    if (comptime group[2] != tm.Obligation) {
                        for (@field(other.generation, group[0])) |candidate| {
                            if (std.mem.eql(u16, other.interner.idText(group[3], candidate.id), id_text)) name.* = try utf8(a, other.interner.stringText(candidate.name));
                        }
                    }
                }
                return .{ .conflict = .{ .kind = group[1], .id = try utf8(a, id_text), .names = names } };
            }
            try seen.put(scratch, key, encoded.written());
        };
    }
    const compactors = try scratch.alloc(tm.Compactor, members.len);
    for (members, compactors) |member, *compactor| compactor.* = .{ .arena = a, .scratch = scratch, .old_interner = member.interner, .interner = target };
    var generation: tm.Generation = .{ .snapshot = try target.id(.snapshot, combined_id), .parent = tm.none, .marker = null, .types = &.{}, .effects = &.{}, .capabilities = &.{}, .functions = &.{}, .obligations = &.{} };
    inline for (groups) |group| {
        var seen: std.StringHashMapUnmanaged(void) = .empty;
        var records: std.ArrayList(*const group[2]) = .empty;
        for (members, compactors) |member, *compactor| for (@field(member.generation, group[0])) |record| {
            const key = try utf8(scratch, member.interner.idText(group[3], record.id));
            if (seen.contains(key)) continue;
            try seen.put(scratch, key, {});
            try records.append(a, try compactor.cloneRecord(group[2], record));
        };
        @field(generation, group[0]) = records.items;
    }
    return .{ .generation = generation };
}

fn appendSubject(a: Allocator, out: *std.ArrayList([]const u8), interner: *const tm.Interner, domain: tm.Domain, label: []const u8, id: tm.Id) !void {
    try out.append(a, try std.fmt.allocPrint(a, "{s}:{s}", .{ label, try utf8(a, interner.idText(domain, id)) }));
}

/// `RecordCodec.dependencySubjects`: declarations and their members, in source order.
pub fn subjectsOf(a: Allocator, generation: *const tm.Generation, interner: *const tm.Interner) ![]const []const u8 {
    var out: std.ArrayList([]const u8) = .empty;
    for (generation.types) |t| {
        try appendSubject(a, &out, interner, .type, "type", t.id);
        for (t.type_parameters orelse &.{}) |parameter| try appendSubject(a, &out, interner, .type_parameter, "type-parameter", parameter.id);
        for (t.fields) |field| try appendSubject(a, &out, interner, .field, "field", field.id);
        for (t.variants) |variant| {
            try appendSubject(a, &out, interner, .variant, "variant", variant.id);
            for (variant.fields) |field| try appendSubject(a, &out, interner, .field, "field", field.id);
        }
    }
    for (generation.effects) |effect| try appendSubject(a, &out, interner, .effect, "effect", effect.id);
    for (generation.capabilities) |capability| {
        try appendSubject(a, &out, interner, .capability, "capability", capability.id);
        for (capability.operations) |operation| {
            try appendSubject(a, &out, interner, .operation, "operation", operation.id);
            for (operation.parameters) |parameter| try appendSubject(a, &out, interner, .capability_parameter, "capability-parameter", parameter.id);
        }
    }
    for (generation.functions) |function| {
        try appendSubject(a, &out, interner, .function, "function", function.id);
        for (function.type_parameters orelse &.{}) |parameter| try appendSubject(a, &out, interner, .type_parameter, "type-parameter", parameter.id);
        for (function.parameters) |parameter| try appendSubject(a, &out, interner, .parameter, "parameter", parameter.id);
    }
    return out.items;
}

fn fixture(a: Allocator, interner: *tm.Interner, id: []const u16, name: []const u16) !*const tm.Function {
    const function = try a.create(tm.Function);
    const node_id = try interner.id(.node, id);
    function.* = .{ .id = try interner.id(.function, id), .name = try interner.string(name), .exported = true, .must_not_wait = false, .type_parameters = null, .parameters = &.{}, .result = .{ .primitive = try interner.string(&.{ 'u', 'n', 'i', 't' }) }, .effects = &.{}, .body = try tm.builder.build(a, &.{.{ .id = node_id, .kind = try tm.builder.kindIndex("unitLiteral"), .payload = &.{} }}, node_id) };
    return function;
}
fn empty(interner: *tm.Interner) !tm.Generation {
    return .{ .snapshot = try interner.id(.snapshot, &.{'s'}), .parent = tm.none, .marker = null, .types = &.{}, .effects = &.{}, .capabilities = &.{}, .functions = &.{}, .obligations = &.{} };
}

test "typed dependency merge remaps independent interners, deduplicates and preserves first occurrence order" {
    var source_arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    const source = source_arena.allocator();
    var target_arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer target_arena.deinit();
    const a = target_arena.allocator();
    var scratch_arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer scratch_arena.deinit();
    var left: tm.Interner = .{ .gpa = source };
    var right: tm.Interner = .{ .gpa = source };
    _ = try right.id(.node, &.{ 'd', 'e', 'a', 'd' });
    _ = try right.string(&.{ 'd', 'e', 'a', 'd' });
    var first = try empty(&left);
    var second = try empty(&right);
    first.functions = try source.dupe(*const tm.Function, &.{ try fixture(source, &left, &.{'g'}, &.{'G'}), try fixture(source, &left, &.{'f'}, &.{'F'}) });
    second.functions = try source.dupe(*const tm.Function, &.{ try fixture(source, &right, &.{'f'}, &.{'F'}), try fixture(source, &right, &.{'h'}, &.{'H'}) });
    var target: tm.Interner = .{ .gpa = a };
    const combined = try combine(a, scratch_arena.allocator(), &target, &.{ .{ .generation = &first, .interner = &left }, .{ .generation = &second, .interner = &right } }, &.{ 's', 'e', 't' });
    try std.testing.expect(combined == .generation);
    var expected: std.Io.Writer.Allocating = .init(a);
    try writer.writeDeclaration(&expected.writer, a, &left, .{ .function = first.functions[1] });
    source_arena.deinit();
    const generation = combined.generation;
    try std.testing.expectEqual(@as(usize, 3), generation.functions.len);
    for (generation.functions, [_][]const u16{ &.{'g'}, &.{'f'}, &.{'h'} }) |function, text| try std.testing.expectEqualSlices(u16, text, target.idText(.function, function.id));
    try std.testing.expect(target.findId(.node, &.{ 'd', 'e', 'a', 'd' }) == null);
    var actual: std.Io.Writer.Allocating = .init(a);
    try writer.writeDeclaration(&actual.writer, a, &target, .{ .function = generation.functions[1] });
    try std.testing.expectEqualStrings(expected.written(), actual.written());
}

test "typed dependency collision names every provider without changing the target interner" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    var left: tm.Interner = .{ .gpa = a };
    var right: tm.Interner = .{ .gpa = a };
    var first = try empty(&left);
    var second = try empty(&right);
    const absent = try empty(&right);
    first.functions = &.{try fixture(a, &left, &.{'f'}, &.{ 'O', 'l', 'd' })};
    second.functions = &.{try fixture(a, &right, &.{'f'}, &.{ 'N', 'e', 'w' })};
    var target: tm.Interner = .{ .gpa = a };
    _ = try target.id(.snapshot, &.{ 'k', 'e', 'p', 't' });
    const checkpoint = target.checkpoint();
    const combined = try combine(a, a, &target, &.{ .{ .generation = &first, .interner = &left }, .{ .generation = &second, .interner = &right }, .{ .generation = &absent, .interner = &right } }, &.{ 's', 'e', 't' });
    try std.testing.expect(combined == .conflict);
    try std.testing.expectEqualStrings("function", combined.conflict.kind);
    try std.testing.expectEqualStrings("f", combined.conflict.id);
    try std.testing.expectEqualStrings("Old", combined.conflict.names[0].?);
    try std.testing.expectEqualStrings("New", combined.conflict.names[1].?);
    try std.testing.expect(combined.conflict.names[2] == null);
    try std.testing.expectEqualDeep(checkpoint, target.checkpoint());
}

test "typed dependency subjects include declaration members in source order" {
    const reader = @import("main.zig");
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const root = try reader.parseValue(a,
        \\{"snapshotId":"s","types":[{"id":"t","displayName":"T","typeParameters":[{"typeParameterId":"tt","displayName":"TT"}],"kind":"union","variants":[{"id":"v","displayName":"V","fields":[{"id":"vf","displayName":"VF","type":{"kind":"primitive","primitive":"unit"}}]}]}],"effects":[{"id":"e","displayName":"E"}],"capabilities":[{"id":"c","displayName":"C","operations":[{"id":"o","displayName":"O","parameters":[{"id":"cp","displayName":"CP","type":{"kind":"primitive","primitive":"unit"}}],"resultType":{"kind":"primitive","primitive":"unit"},"effectId":"e"}]}],"functions":[{"id":"f","displayName":"F","typeParameters":[{"typeParameterId":"ft","displayName":"FT"}],"parameters":[{"id":"p","displayName":"P","type":{"kind":"primitive","primitive":"unit"}}],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"nodes":[{"id":"n","kind":"unitLiteral"}],"resultNodeId":"n"}}],"obligations":[]}
    );
    var interner: tm.Interner = .{ .gpa = a };
    const generation = try tm.load(a, &interner, root, null);
    const actual = try subjectsOf(a, &generation, &interner);
    const expected = [_][]const u8{ "type:t", "type-parameter:tt", "variant:v", "field:vf", "effect:e", "capability:c", "operation:o", "capability-parameter:cp", "function:f", "type-parameter:ft", "parameter:p" };
    try std.testing.expectEqual(expected.len, actual.len);
    for (expected, actual) |x, y| try std.testing.expectEqualStrings(x, y);
}

test "dependency collision compares canonical graph order and exact obligation content" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    var source: tm.Interner = .{ .gpa = a };
    var first = try empty(&source);
    var second = try empty(&source);
    var f = (try fixture(a, &source, &.{'f'}, &.{'F'})).*;
    const z = try source.id(.node, &.{'z'});
    const f_node = f.body.node(0);
    const z_node: tm.builder.NodeDraft = .{ .id = z, .kind = try tm.builder.kindIndex("unitLiteral"), .payload = &.{} };
    f.body = try tm.builder.build(a, &.{ try tm.builder.fromNode(a, f_node), z_node }, f_node.id());
    var reordered = f;
    reordered.body = try tm.builder.build(a, &.{ z_node, try tm.builder.fromNode(a, f_node) }, f_node.id());
    first.functions = &.{&f};
    second.functions = &.{&reordered};
    const obligation: tm.Obligation = .{ .id = try source.id(.obligation, &.{'o'}), .origin = try source.id(.transaction, &.{'t'}), .call = f_node.id(), .parameter = try source.id(.parameter, &.{'p'}), .resolution = .{ .transaction = try source.id(.transaction, &.{'r'}), .value = z } };
    first.obligations = &.{&obligation};
    second.obligations = &.{&obligation};
    var target: tm.Interner = .{ .gpa = a };
    const merged = try combine(a, a, &target, &.{ .{ .generation = &first, .interner = &source }, .{ .generation = &second, .interner = &source } }, &.{'c'});
    try std.testing.expect(merged == .generation);
    try std.testing.expectEqual(@as(usize, 1), merged.generation.functions.len);
    try std.testing.expectEqual(@as(usize, 1), merged.generation.obligations.len);
    var encoded: std.Io.Writer.Allocating = .init(a);
    try writer.writeDeclaration(&encoded.writer, a, &source, .{ .obligation = &obligation });
    try std.testing.expectEqualStrings("{\"id\":\"o\",\"kind\":\"missingArgument\",\"originTransactionId\":\"t\",\"callNodeId\":\"f\",\"parameterId\":\"p\",\"status\":\"resolved\",\"resolutionTransactionId\":\"r\",\"resolutionValueNodeId\":\"z\"}", encoded.written());
    var conflict = obligation;
    conflict.resolution = null;
    second.obligations = &.{&conflict};
    const rejected = try combine(a, a, &target, &.{ .{ .generation = &first, .interner = &source }, .{ .generation = &second, .interner = &source } }, &.{'c'});
    try std.testing.expectEqualStrings("obligation", rejected.conflict.kind);
    for (rejected.conflict.names) |name| try std.testing.expect(name == null);
}
