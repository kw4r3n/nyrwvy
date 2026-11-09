//! Borrowed semantic records. These handles never own a JSON object or property tree.
const std = @import("std");
const model = @import("typed.zig");
const reader = @import("main.zig");
const Str = reader.Str;
pub const Context = struct { allocator: std.mem.Allocator, interner: *const model.Interner };
var active: *const Context = undefined;
var allocator: std.mem.Allocator = undefined;
var interner: *const model.Interner = undefined;
pub fn borrow(data: Data) Record {
    return .{ .context = active, .data = data };
}

pub fn clear() void {
    active = undefined;
    allocator = undefined;
    interner = undefined;
}
pub fn currentInterner() *const model.Interner {
    return interner;
}
pub fn begin(a: std.mem.Allocator, strings: *const model.Interner) void {
    const context = a.create(Context) catch @panic("out of memory");
    context.* = .{ .allocator = a, .interner = strings };
    active = context;
    allocator = a;
    interner = strings;
}
pub fn idTextOf(domain: model.Domain, id: model.Id) Str {
    return idText(domain, id);
}
pub fn stringText(handle: u32) Str {
    return nameText(handle);
}
fn idText(domain: model.Domain, id: model.Id) Str {
    return interner.idText(domain, id);
}
fn nameText(id: u32) Str {
    return interner.stringText(id);
}
fn fail() noreturn {
    @panic("invalid semantic record access");
}
fn list(comptime T: type, items: []const T, comptime tag: std.meta.Tag(Data)) []const Record {
    const out = allocator.alloc(Record, items.len) catch @panic("out of memory");
    for (items, out) |*item, *slot| slot.* = borrow(@unionInit(Data, @tagName(tag), if (@typeInfo(T) == .pointer) item.* else item));
    return out;
}
pub const Slot = struct { value: *const model.Typed, domain: model.Domain };
pub const Target = struct { value: model.Target, domain: model.Domain };
pub const Data = union(enum) {
    snapshot: *const model.Generation,
    function: *const model.Function,
    type_declaration: *const model.Type,
    effect: *const model.Effect,
    capability: *const model.Capability,
    operation: *const model.Operation,
    obligation: *const model.Obligation,
    variant: *const model.Variant,
    slot: Slot,
    type_parameter: *const model.TypeParameter,
    binding: *const model.Binding,
    node: model.Node,
    graph: model.GraphView,
    type_ref: model.TypeRef,
    target: Target,
    implementation: model.Implementation,
    union_arm: model.UnionArm,
    field_binding: model.FieldBinding,
    binding_arm: model.BindingArm,
    effect_reference: model.Id,
    variant_reference: model.Id,
    node_reference: model.Id,
};
pub const Record = struct {
    context: *const Context,
    data: Data,
    fn activate(self: Record) void {
        active = self.context;
        allocator = active.allocator;
        interner = active.interner;
    }

    pub fn id(self: Record) Str {
        self.activate();
        return switch (self.data) {
            .snapshot => |p| idText(.snapshot, p.snapshot),
            .function => |p| idText(.function, p.id),
            .type_declaration => |p| idText(.type, p.id),
            .effect => |p| idText(.effect, p.id),
            .capability => |p| idText(.capability, p.id),
            .operation => |p| idText(.operation, p.id),
            .obligation => |p| idText(.obligation, p.id),
            .variant => |p| idText(.variant, p.id),
            .slot => |p| idText(p.domain, p.value.id),
            .binding => |p| idText(.binding, p.id),
            .node => |p| idText(.node, p.id()),
            .type_parameter => |p| idText(.type_parameter, p.id),
            else => fail(),
        };
    }
    pub fn displayName(self: Record) Str {
        self.activate();
        return nameText(switch (self.data) {
            .function => |p| p.name,
            .type_declaration => |p| p.name,
            .effect => |p| p.name,
            .capability => |p| p.name,
            .operation => |p| p.name,
            .variant => |p| p.name,
            .slot => |p| p.value.name,
            .type_parameter => |p| p.name,
            .binding => |p| p.name orelse return &.{},
            else => fail(),
        });
    }
    pub fn optionalDisplayName(self: Record) ?Str {
        self.activate();
        return if (self.data.binding.name) |n| nameText(n) else null;
    }
    /// `kind()` as UTF-8; a node's comes straight from the kind table, with no conversion.
    pub fn kindText(self: Record) []const u8 {
        self.activate();
        return switch (self.data) {
            .node => |p| p.kind(),
            else => std.unicode.utf16LeToUtf8Alloc(allocator, self.kind()) catch @panic("out of memory"),
        };
    }
    pub fn kind(self: Record) Str {
        self.activate();
        return switch (self.data) {
            .node => |p| kind16(p),
            .type_declaration => |p| if (p.product) &.{ 'p', 'r', 'o', 'd', 'u', 'c', 't' } else &.{ 'u', 'n', 'i', 'o', 'n' },
            .type_ref => |t| switch (t) {
                .primitive => &.{ 'p', 'r', 'i', 'm', 'i', 't', 'i', 'v', 'e' },
                .named => &.{ 'n', 'a', 'm', 'e', 'd' },
                .function => &.{ 'f', 'u', 'n', 'c', 't', 'i', 'o', 'n' },
                .type_parameter => &.{ 't', 'y', 'p', 'e', 'P', 'a', 'r', 'a', 'm', 'e', 't', 'e', 'r' },
                .capability => &.{ 'c', 'a', 'p', 'a', 'b', 'i', 'l', 'i', 't', 'y' },
                .generic => &.{ 'b', 'u', 'i', 'l', 't', 'i', 'n', 'G', 'e', 'n', 'e', 'r', 'i', 'c' },
            },
            else => fail(),
        };
    }
    pub fn declarations(self: Record, group: []const u8) []const Record {
        self.activate();
        if (std.mem.eql(u8, group, "functions")) return self.functions();
        if (std.mem.eql(u8, group, "types")) return self.types();
        if (std.mem.eql(u8, group, "effects")) return self.effects();
        if (std.mem.eql(u8, group, "capabilities")) return self.capabilities();
        fail();
    }
    pub fn idReferences(_: Record, ids: []const model.Id) []const Record {
        const out = allocator.alloc(Record, ids.len) catch @panic("out of memory");
        for (ids, out) |reference_id, *slot| slot.* = borrow(.{ .node_reference = reference_id });
        return out;
    }
    pub fn functions(self: Record) []const Record {
        self.activate();
        return list(*const model.Function, self.data.snapshot.functions, .function);
    }
    pub fn types(self: Record) []const Record {
        self.activate();
        return list(*const model.Type, self.data.snapshot.types, .type_declaration);
    }
    pub fn effects(self: Record) []const Record {
        self.activate();
        return list(*const model.Effect, self.data.snapshot.effects, .effect);
    }
    pub fn capabilities(self: Record) []const Record {
        self.activate();
        return list(*const model.Capability, self.data.snapshot.capabilities, .capability);
    }
    pub fn obligations(self: Record) []const Record {
        self.activate();
        return list(*const model.Obligation, self.data.snapshot.obligations, .obligation);
    }
    pub fn operations(self: Record) []const Record {
        self.activate();
        return list(model.Operation, self.data.capability.operations, .operation);
    }
    pub fn variants(self: Record) []const Record {
        self.activate();
        return list(model.Variant, self.data.type_declaration.variants, .variant);
    }
    fn slots(items: []const model.Typed, domain: model.Domain) []const Record {
        const out = allocator.alloc(Record, items.len) catch @panic("out of memory");
        for (items, out) |*item, *slot| slot.* = borrow(.{ .slot = .{ .value = item, .domain = domain } });
        return out;
    }
    pub fn fields(self: Record) []const Record {
        self.activate();
        return switch (self.data) {
            .type_declaration => |p| slots(p.fields, .field),
            .variant => |p| slots(p.fields, .field),
            .node => self.targets("fields"),
            else => fail(),
        };
    }
    pub fn parameters(self: Record) []const Record {
        self.activate();
        return switch (self.data) {
            .function => |p| slots(p.parameters, .parameter),
            .operation => |p| slots(p.parameters, .capability_parameter),
            .type_ref => |t| typeRefs(t.function.parameters),
            else => fail(),
        };
    }
    pub fn typeParameters(self: Record) []const Record {
        self.activate();
        const items = switch (self.data) {
            .function => |p| p.type_parameters,
            .type_declaration => |p| p.type_parameters,
            else => fail(),
        };
        return list(model.TypeParameter, items orelse &.{}, .type_parameter);
    }
    pub fn requiredEffects(self: Record) []const Record {
        self.activate();
        const items = self.data.function.effects;
        const out = allocator.alloc(Record, items.len) catch @panic("out of memory");
        for (items, out) |item, *slot| slot.* = borrow(.{ .effect_reference = item });
        return out;
    }
    pub fn body(self: Record) Record {
        self.activate();
        return borrow(.{ .graph = switch (self.data) {
            .function => |p| p.body.graph(0),
            .union_arm => |p| p.body,
            .binding_arm => |p| p.body,
            else => fail(),
        } });
    }
    pub fn nodes(self: Record) []const Record {
        self.activate();
        const graph = self.data.graph;
        var iterator = graph.nodes();
        var out: std.ArrayList(Record) = .empty;
        while (iterator.next()) |node| out.append(allocator, borrow(.{ .node = node })) catch @panic("out of memory");
        return out.items;
    }
    pub fn resultNodeId(self: Record) Str {
        self.activate();
        return idText(.node, self.data.graph.result());
    }
    pub fn declaredType(self: Record) Record {
        self.activate();
        return borrow(.{ .type_ref = switch (self.data) {
            .slot => |p| p.value.type,
            .binding => |p| p.type,
            else => fail(),
        } });
    }
    pub fn resultType(self: Record) Record {
        self.activate();
        return borrow(.{ .type_ref = switch (self.data) {
            .function => |p| p.result,
            .operation => |p| p.result,
            .type_ref => |p| p.function.result.*,
            .node => self.payloadType("resultType").data.type_ref,
            else => fail(),
        } });
    }
    pub fn failureType(self: Record) ?Record {
        self.activate();
        return if (self.data.operation.failure) |t| borrow(.{ .type_ref = t }) else null;
    }
    pub fn exported(self: Record) bool {
        self.activate();
        return switch (self.data) {
            .function => |p| p.exported,
            .type_declaration => |p| p.exported,
            .effect => |p| p.exported,
            .capability => |p| p.exported,
            else => fail(),
        };
    }
    pub fn taskBound(self: Record) bool {
        self.activate();
        return self.data.capability.task_bound;
    }
    pub fn mayWait(self: Record) bool {
        self.activate();
        return self.data.operation.may_wait;
    }
    pub fn mustNotWait(self: Record) bool {
        self.activate();
        return self.data.function.must_not_wait;
    }
    pub fn status(self: Record) Str {
        self.activate();
        return if (self.data.obligation.resolution == null) &.{ 'o', 'p', 'e', 'n' } else &.{ 'r', 'e', 's', 'o', 'l', 'v', 'e', 'd' };
    }
    pub fn parentSnapshotId(self: Record) ?Str {
        self.activate();
        return if (self.data.snapshot.parent == model.none) null else idText(.snapshot, self.data.snapshot.parent);
    }
    pub fn literal(self: Record) Str {
        self.activate();
        return switch (self.data) {
            .effect_reference => |p| idText(.effect, p),
            .variant_reference => |p| idText(.variant, p),
            .node_reference => |p| idText(.node, p),
            else => fail(),
        };
    }
    pub fn primitive(self: Record) Str {
        self.activate();
        return nameText(self.data.type_ref.primitive);
    }
    pub fn generic(self: Record) Str {
        self.activate();
        return nameText(self.data.type_ref.generic.name);
    }
    fn typeRefs(items: []const model.TypeRef) []const Record {
        const out = allocator.alloc(Record, items.len) catch @panic("out of memory");
        for (items, out) |item, *slot| slot.* = borrow(.{ .type_ref = item });
        return out;
    }
    pub fn arguments(self: Record) []const Record {
        self.activate();
        return switch (self.data) {
            .type_ref => |t| typeRefs(switch (t) {
                .named => |p| p.arguments orelse &.{},
                .generic => |p| p.arguments,
                else => fail(),
            }),
            .node => if (self.payload("arguments")) |payload_| switch (payload_) {
                .ids => |ids| self.idReferences(ids),
                else => self.targets("arguments"),
            } else &.{},
            else => fail(),
        };
    }
    pub fn payload(self: Record, name: []const u8) ?model.Payload {
        self.activate();
        var payloads = self.data.node.payload();
        while (payloads.next()) |field| if (std.mem.eql(u8, field.read.name, name)) return field.value;
        return null;
    }
    pub fn reference(self: Record, name: []const u8) Str {
        self.activate();
        return switch (self.data) {
            .node => blk: {
                var payloads = self.data.node.payload();
                while (payloads.next()) |field| {
                    if (std.mem.eql(u8, field.read.name, name)) {
                        const reference_id = switch (field.value) {
                            .id => |p| p,
                            .optid => |p| p orelse fail(),
                            else => fail(),
                        };
                        break :blk idText(field.read.domain.?, reference_id);
                    }
                }
                fail();
            },
            .type_ref => |t| switch (t) {
                .named => |p| idText(.type, p.id),
                .type_parameter => |p| idText(.type_parameter, p),
                .capability => |p| idText(.capability, p),
                else => fail(),
            },
            .target => |p| if (std.mem.eql(u8, name, "valueNodeId")) idText(.node, p.value.value) else idText(p.domain, p.value.target),
            .implementation => |p| if (std.mem.eql(u8, name, "operationId")) idText(.operation, p.operation) else idText(.function, p.function),
            .field_binding => |p| idText(.field, p.source),
            .type_parameter => self.id(),
            .operation => |p| idText(.effect, p.effect),
            .obligation => |p| if (std.mem.eql(u8, name, "callNodeId")) idText(.node, p.call) else if (std.mem.eql(u8, name, "parameterId")) idText(.parameter, p.parameter) else if (std.mem.eql(u8, name, "resolutionValueNodeId")) idText(.node, p.resolution.?.value) else fail(),
            .snapshot => self.id(),
            else => fail(),
        };
    }
    pub fn optionalReference(self: Record, name: []const u8) ?Str {
        self.activate();
        return switch (self.data) {
            .node => if (self.payload(name)) |p| switch (p) {
                .optid => |reference_id| if (reference_id) |v| idText(.node, v) else null,
                .id => self.reference(name),
                else => fail(),
            } else null,
            .union_arm => |p| if (p.variants.len == 1) idText(.variant, p.variants[0]) else null,
            else => fail(),
        };
    }
    pub fn payloadType(self: Record, name: []const u8) Record {
        self.activate();
        return borrow(.{ .type_ref = self.payload(name).?.type });
    }
    pub fn graphPayload(self: Record, name: []const u8) Record {
        self.activate();
        return borrow(.{ .graph = self.payload(name).?.graph });
    }
    pub fn arm(self: Record, name: []const u8) Record {
        self.activate();
        const p = self.payload(name).?;
        return borrow(.{ .binding_arm = switch (p) {
            .varm => |v| v,
            .errarm => |v| v,
            else => fail(),
        } });
    }
    pub fn armBinding(self: Record) Record {
        self.activate();
        return borrow(.{ .binding = self.data.binding_arm.binding });
    }
    pub fn boundBinding(self: Record) Record {
        self.activate();
        return borrow(.{ .binding = self.data.field_binding.binding });
    }
    pub fn targets(self: Record, name: []const u8) []const Record {
        self.activate();
        const p = self.payload(name) orelse return &.{};
        const targets_ = switch (p) {
            .ctx => |v| v,
            .args => |v| v,
            .capargs => |v| v,
            .fields => |v| v,
            else => fail(),
        };
        const domain: model.Domain = if (std.meta.activeTag(p) == .capargs) .capability_parameter else if (std.meta.activeTag(p) == .fields) .field else .parameter;
        const out = allocator.alloc(Record, targets_.len()) catch @panic("out of memory");
        for (out, 0..) |*slot, i| slot.* = borrow(.{ .target = .{ .value = targets_.at(i), .domain = domain } });
        return out;
    }
    pub fn arms(self: Record) []const Record {
        self.activate();
        const items = self.payload("arms").?.arms;
        var iterator = items.iterator();
        var out: std.ArrayList(Record) = .empty;
        while (iterator.next()) |arm_| out.append(allocator, borrow(.{ .union_arm = arm_ })) catch @panic("out of memory");
        return out.items;
    }
    pub fn fieldBindings(self: Record) []const Record {
        self.activate();
        const fields_ = self.data.union_arm.fields;
        const out = allocator.alloc(Record, fields_.len()) catch @panic("out of memory");
        for (out, 0..) |*slot, i| slot.* = borrow(.{ .field_binding = fields_.at(i) });
        return out;
    }
    pub fn variantIds(self: Record) []const Record {
        self.activate();
        const ids = self.data.union_arm.variants;
        const out = allocator.alloc(Record, ids.len) catch @panic("out of memory");
        for (ids, out) |reference_id, *slot| slot.* = borrow(.{ .variant_reference = reference_id });
        return out;
    }
    pub fn implementations(self: Record) []const Record {
        self.activate();
        const payload_ = self.payload("implementations") orelse return &.{};
        const items = payload_.impls;
        const out = allocator.alloc(Record, items.len()) catch @panic("out of memory");
        for (out, 0..) |*slot, i| slot.* = borrow(.{ .implementation = items.at(i) });
        return out;
    }
    pub fn typeArguments(self: Record) []const Record {
        self.activate();
        const p = self.payload("typeArguments") orelse return &.{};
        const args = p.targs;
        const out = allocator.alloc(Record, args.len()) catch @panic("out of memory");
        for (out, 0..) |*slot, i| slot.* = borrow(.{ .type_ref = args.at(i) });
        return out;
    }
};

test "borrowed semantic handles retain their own interner across temporary models" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    var first_strings: model.Interner = .{ .gpa = a };
    var second_strings: model.Interner = .{ .gpa = a };
    const first_id = try first_strings.id(.snapshot, &.{ 'f', 'i', 'r', 's', 't' });
    const second_id = try second_strings.id(.snapshot, &.{ 's', 'e', 'c', 'o', 'n', 'd' });
    const first_generation: model.Generation = .{ .snapshot = first_id, .parent = model.none, .marker = null, .types = &.{}, .effects = &.{}, .capabilities = &.{}, .functions = &.{}, .obligations = &.{} };
    const second_generation: model.Generation = .{ .snapshot = second_id, .parent = model.none, .marker = null, .types = &.{}, .effects = &.{}, .capabilities = &.{}, .functions = &.{}, .obligations = &.{} };
    begin(a, &first_strings);
    const first = borrow(.{ .snapshot = &first_generation });
    begin(a, &second_strings);
    const second = borrow(.{ .snapshot = &second_generation });
    try std.testing.expectEqualSlices(u16, &.{ 'f', 'i', 'r', 's', 't' }, first.id());
    try std.testing.expectEqualSlices(u16, &.{ 's', 'e', 'c', 'o', 'n', 'd' }, second.id());
    try std.testing.expectEqualSlices(u16, &.{ 'f', 'i', 'r', 's', 't' }, first.id());
}

/// A node kind's UTF-16 name per kind-table index (at most 256), converted once per process like the
/// table itself, so `kind()` on a node does not allocate.
var kind16_cache: [256]?Str = @splat(null);
fn kind16(p: model.Node) Str {
    const k = p.body.kinds[p.index];
    if (kind16_cache[k]) |cached| return cached;
    const units = std.unicode.utf8ToUtf16LeAlloc(std.heap.page_allocator, p.kind()) catch @panic("out of memory");
    kind16_cache[k] = units;
    return units;
}
