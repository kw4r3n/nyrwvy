//! Immutable edits to domain records; unchanged declarations keep their original storage.
const std = @import("std");
const model = @import("model_reader");
const tm = model.session_model;
const Record = model.Model;
const Data = @FieldType(Record, "data");
const Allocator = std.mem.Allocator;

pub const Collection = enum { functions, types, effects, capabilities, obligations, parameters, fields, variants, operations };
pub const Change = union(enum) {
    name: u32,
    exported: bool,
    may_wait: bool,
    task_bound: bool,
    must_not_wait: bool,
    parameters: []const tm.Typed,
    fields: []const tm.Typed,
    variants: []const tm.Variant,
    operations: []const tm.Operation,
    type_parameters: ?[]const tm.TypeParameter,
    result: tm.TypeRef,
    type: tm.TypeRef,
    effects: []const tm.Id,
    body: tm.Body,
    resolution: @FieldType(tm.Obligation, "resolution"),
    snapshot: tm.Id,
    parent: tm.Id,
    obligations: []const *const tm.Obligation,
};

pub fn borrow(owner: Record, data: Data) Record {
    return .{ .context = owner.context, .data = data };
}
fn copy(a: Allocator, original: anytype, comptime field: []const u8, value: anytype) !@TypeOf(original) {
    const T = @typeInfo(@TypeOf(original)).pointer.child;
    const result = try a.create(T);
    result.* = original.*;
    @field(result, field) = value;
    return result;
}

pub fn changed(a: Allocator, original: Record, change: Change) !Record {
    switch (change) {
        inline else => |value, field| switch (original.data) {
            .slot => |slot| switch (field) {
                .name, .type => return borrow(original, .{ .slot = .{ .domain = slot.domain, .value = try copy(a, slot.value, @tagName(field), value) } }),
                else => unreachable,
            },
            inline .snapshot, .function, .type_declaration, .effect, .capability, .operation, .obligation, .variant, .type_parameter, .binding => |record, tag| {
                const T = @typeInfo(@TypeOf(record)).pointer.child;
                if (comptime @hasField(T, @tagName(field))) {
                    if (comptime @TypeOf(value) == @FieldType(T, @tagName(field))) return borrow(original, @unionInit(Data, @tagName(tag), try copy(a, record, @tagName(field), value)));
                }
                unreachable;
            },
            else => unreachable,
        },
    }
}

pub fn children(owner: Record, collection: Collection) []const Record {
    return switch (collection) {
        .functions => owner.functions(),
        .types => owner.types(),
        .effects => owner.effects(),
        .capabilities => owner.capabilities(),
        .obligations => owner.obligations(),
        .parameters => owner.parameters(),
        .fields => owner.fields(),
        .variants => owner.variants(),
        .operations => owner.operations(),
    };
}
pub fn find(owner: Record, collection: Collection, wanted: []const u8) ?Record {
    for (children(owner, collection)) |record| if (std.mem.eql(u8, model.utf8(record.id()), wanted)) return record;
    return null;
}
pub fn hasId(record: Record, wanted: []const u8) bool {
    return std.mem.eql(u8, model.utf8(record.id()), wanted);
}

pub fn withChildren(a: Allocator, owner: Record, collection: Collection, records: []const Record) !Record {
    switch (collection) {
        inline .functions, .types, .effects, .capabilities, .obligations => |group| {
            const tag = comptime switch (group) {
                .functions => "function",
                .types => "type_declaration",
                .effects => "effect",
                .capabilities => "capability",
                .obligations => "obligation",
                else => unreachable,
            };
            const T = @FieldType(Data, tag);
            const pointers = try a.alloc(T, records.len);
            for (records, pointers) |record, *slot| slot.* = @field(record.data, tag);
            return borrow(owner, .{ .snapshot = try copy(a, owner.data.snapshot, @tagName(group), @as([]const T, pointers)) });
        },
        .parameters, .fields => {
            const items = try a.alloc(tm.Typed, records.len);
            for (records, items) |record, *slot| slot.* = record.data.slot.value.*;
            return changed(a, owner, if (collection == .parameters) .{ .parameters = items } else .{ .fields = items });
        },
        .variants => {
            const items = try a.alloc(tm.Variant, records.len);
            for (records, items) |record, *slot| slot.* = record.data.variant.*;
            return changed(a, owner, .{ .variants = items });
        },
        .operations => {
            const items = try a.alloc(tm.Operation, records.len);
            for (records, items) |record, *slot| slot.* = record.data.operation.*;
            return changed(a, owner, .{ .operations = items });
        },
    }
}
pub fn replace(a: Allocator, owner: Record, collection: Collection, replacement: Record) !Record {
    const records = try a.dupe(Record, children(owner, collection));
    for (records) |*record| if (std.mem.eql(u16, record.id(), replacement.id())) {
        record.* = replacement;
        return withChildren(a, owner, collection, records);
    };
    return error.MissingOperationTarget;
}
pub fn append(a: Allocator, owner: Record, collection: Collection, addition: Record) !Record {
    return withChildren(a, owner, collection, try std.mem.concat(a, Record, &.{ children(owner, collection), &.{addition} }));
}
pub fn drop(a: Allocator, owner: Record, collection: Collection, wanted: []const u8) !Record {
    var records: std.ArrayList(Record) = .empty;
    for (children(owner, collection)) |record| if (!hasId(record, wanted)) try records.append(a, record);
    return withChildren(a, owner, collection, records.items);
}

pub fn nodePayload(a: Allocator, node: Record, name: []const u8, value: tm.Payload) !Record {
    const draft = try tm.builder.fromNode(a, node.data.node);
    const payload = @constCast(draft.payload);
    for (tm.kindTable()[draft.kind].reads, payload) |read, *field| if (std.mem.eql(u8, name, read.name)) {
        std.debug.assert(read.shape == std.meta.activeTag(value));
        field.* = value;
        const body = try a.create(tm.Body);
        body.* = try tm.builder.build(a, &.{draft}, draft.id);
        return borrow(node, .{ .node = body.node(0) });
    };
    return error.MissingNodePayload;
}
pub fn replaceNode(a: Allocator, root: Record, wanted: tm.Id, replacement: Record) !Record {
    var result = root;
    for (root.functions()) |function| if (function.data.function.body.findNode(wanted) != null) {
        const draft = try tm.builder.fromNode(a, replacement.data.node);
        const body = try tm.builder.rebuild(a, function.data.function.body.graph(0), .{ .replacements = &.{.{ .node = wanted, .nodes = &.{draft} }} });
        result = try replace(a, result, .functions, try changed(a, function, .{ .body = body }));
    };
    return result;
}

pub fn typeRef(record: Record, value: tm.TypeRef) Record {
    return borrow(record, .{ .type_ref = value });
}

// Output fragments are constructed only for wire/review encoding, never as snapshot state.
pub fn wireType(a: Allocator, interner: *const tm.Interner, value: tm.TypeRef) anyerror!model.Value {
    const m = @import("materialize.zig");
    var fields: std.ArrayList(model.Member) = .empty;
    const kind: []const u8 = switch (value) {
        .primitive => "primitive",
        .named => "named",
        .function => "function",
        .type_parameter => "typeParameter",
        .capability => "capability",
        .generic => "builtinGeneric",
    };
    try fields.append(a, try m.member(a, "kind", try m.string(a, kind)));
    switch (value) {
        .primitive => |name| try fields.append(a, try m.member(a, "primitive", .{ .string = interner.stringText(name) })),
        .named => |named| {
            try fields.append(a, try m.member(a, "typeId", .{ .string = interner.idText(.type, named.id) }));
            if (named.arguments) |arguments| try fields.append(a, try m.member(a, "arguments", try wireTypes(a, interner, arguments)));
        },
        .function => |function| {
            try fields.append(a, try m.member(a, "parameters", try wireTypes(a, interner, function.parameters)));
            try fields.append(a, try m.member(a, "resultType", try wireType(a, interner, function.result.*)));
        },
        .type_parameter => |id| try fields.append(a, try m.member(a, "typeParameterId", .{ .string = interner.idText(.type_parameter, id) })),
        .capability => |id| try fields.append(a, try m.member(a, "capabilityId", .{ .string = interner.idText(.capability, id) })),
        .generic => |generic| {
            try fields.append(a, try m.member(a, "generic", .{ .string = interner.stringText(generic.name) }));
            try fields.append(a, try m.member(a, "arguments", try wireTypes(a, interner, generic.arguments)));
        },
    }
    return .{ .object = fields.items };
}
fn wireTypes(a: Allocator, interner: *const tm.Interner, items: []const tm.TypeRef) !model.Value {
    const values = try a.alloc(model.Value, items.len);
    for (items, values) |item, *slot| slot.* = try wireType(a, interner, item);
    return .{ .array = values };
}

test "typed edits copy changed domain records and preserve untouched functions" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    var interner: tm.Interner = .{ .gpa = a };
    const name = try interner.string(&.{'F'});
    const unit: tm.TypeRef = .{ .primitive = try interner.string(&.{ 'u', 'n', 'i', 't' }) };
    const child = try tm.builder.build(a, &.{.{ .id = 1, .kind = try tm.builder.kindIndex("int64Literal"), .payload = &.{.{ .int = 7 }} }}, 1);
    const body = try tm.builder.build(a, &.{.{ .id = 2, .kind = try tm.builder.kindIndex("boolAnd"), .payload = &.{ .{ .id = 3 }, .{ .graph = try tm.builder.view(a, child) } } }}, 2);
    const f: tm.Function = .{ .id = 0, .name = name, .exported = false, .must_not_wait = false, .type_parameters = null, .parameters = &.{}, .result = unit, .effects = &.{}, .body = body };
    const g: tm.Function = .{ .id = 1, .name = name, .exported = false, .must_not_wait = false, .type_parameters = null, .parameters = &.{}, .result = unit, .effects = &.{}, .body = try tm.builder.build(a, &.{.{ .id = 4, .kind = try tm.builder.kindIndex("unitLiteral"), .payload = &.{} }}, 4) };
    _ = try interner.id(.function, &.{'f'});
    _ = try interner.id(.function, &.{'g'});
    _ = try interner.id(.node, &.{'u'});
    _ = try interner.id(.node, &.{'c'});
    _ = try interner.id(.node, &.{'p'});
    _ = try interner.id(.node, &.{'b'});
    _ = try interner.id(.node, &.{'v'});
    const generation: tm.Generation = .{ .snapshot = 0, .parent = tm.none, .marker = null, .types = &.{}, .effects = &.{}, .capabilities = &.{}, .functions = &.{ &f, &g }, .obligations = &.{} };
    const root = model.typedRoot(a, &interner, &generation);
    const function = root.functions()[0];
    const renamed = try changed(a, function, .{ .name = try interner.string(&.{'R'}) });
    try std.testing.expect(renamed.data.function != function.data.function);
    try std.testing.expect(renamed.data.function.body.ids.ptr == function.data.function.body.ids.ptr);
    const node = borrow(root, .{ .node = f.body.findNode(1).? });
    const replacement = try nodePayload(a, node, "value", .{ .int = 42 });
    const edited = try replaceNode(a, root, 1, replacement);
    try std.testing.expect(edited.data.snapshot.functions[1] == generation.functions[1]);
    try std.testing.expect(edited.data.snapshot.functions[0] != generation.functions[0]);
    var old_payload = generation.functions[0].body.findNode(1).?.payload();
    var new_payload = edited.data.snapshot.functions[0].body.findNode(1).?.payload();
    try std.testing.expectEqual(@as(i64, 7), old_payload.next().?.value.int);
    try std.testing.expectEqual(@as(i64, 42), new_payload.next().?.value.int);
}
