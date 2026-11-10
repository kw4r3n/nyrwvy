//! Add a Union variant by copying source fields and repairing every nested match atomically.
const std = @import("std");
const model = @import("model_reader");
const query = @import("query.zig");
const recipe = @import("recipe.zig");
const preflight = @import("preflight.zig");
const infer = @import("infer.zig");
const m = @import("materialize.zig");
const edit = @import("typed_edit.zig");
const tm = model.session_model;
const builder = tm.builder;
const Model = model.Model;
const migration = @import("migration.zig");
const proposals = @import("proposals.zig");
const Value = model.Value;
const Field = struct { name: []const u8, type: recipe.TypeRef };
const Ids = std.AutoHashMapUnmanaged(tm.Id, tm.Id);

fn reject(r: *query.Request, suffix: []const u8, message: []const u8) anyerror {
    return preflight.rejected(r, try std.mem.concat(r.a, u8, &.{ "UnionVariantEvolution", suffix }), message);
}
fn typeValue(r: *query.Request, t: recipe.TypeRef) !tm.TypeRef {
    var materializer: m.Materializer = .{ .a = r.a, .r = r, .types = undefined, .fresh = &.{}, .function_id = tm.none, .parameters = &.{} };
    return materializer.typeRef(t);
}
pub fn simulateRequest(r: *query.Request, out: *std.Io.Writer) anyerror!void {
    const op = r.root.get("op").?.string;
    const symbolic = std.mem.endsWith(u8, op, "ByName");
    const allowed: []const []const u8 = if (symbolic) &.{ "id", "op", "type", "sourceVariant", "displayName", "additionalFields", "structuredReview", "detail", "expectedSnapshotId" } else &.{ "id", "op", "typeId", "sourceVariantId", "displayName", "additionalFields", "structuredReview", "detail", "expectedSnapshotId" };
    // Additional field types resolve declaration names without a function generic context.
    var d: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line), .owner = if (symbolic) .{ .name = "" } else null };
    const root: std.json.Value = .{ .object = r.root };
    try d.ensureFields("", root, try std.fmt.allocPrint(r.a, "{s} request", .{op}), allowed, allowed[0..5]);
    var owner_type: Model = undefined;
    var source: Model = undefined;
    if (symbolic) {
        owner_type = try d.selector("/type", try d.property(root, "type"), "type");
        if (owner_type.data.type_declaration.product) return r.fail("Type '{s}' is not a union type.", .{query.nameOf(owner_type)});
        source = try d.member("variant on {s}", .{query.nameOf(owner_type)}, owner_type, "variants", try d.requiredString(root, "sourceVariant"));
    }
    const wanted = if (r.root.get("expectedSnapshotId") != null) try d.stableId("expectedSnapshotId", try d.requiredString(root, "expectedSnapshotId")) else r.snapshot_id;
    const type_id = if (symbolic) query.idOf(owner_type) else try d.stableId("typeId", try d.requiredString(root, "typeId"));
    const source_id = if (symbolic) query.idOf(source) else try d.stableId("sourceVariantId", try d.requiredString(root, "sourceVariantId"));
    const name = try d.requiredString(root, "displayName");
    var fields: std.ArrayList(Field) = .empty;
    if (root.object.get("additionalFields") != null) for (try d.array(root, "additionalFields"), 0..) |field, i| {
        const path = try std.fmt.allocPrint(r.a, "/additionalFields/{d}", .{i});
        try d.ensureFields(path, field, "type field", &.{ "displayName", "type" }, &.{ "displayName", "type" });
        try fields.append(r.a, .{ .name = try d.requiredString(field, "displayName"), .type = try d.anyType(try std.fmt.allocPrint(r.a, "{s}/type", .{path}), try d.property(field, "type")) });
    };
    try r.ensureUsable();
    try proposals.publishMigration(r, out, try simulate(r, wanted, type_id, source_id, name, fields.items));
}

fn fresh(r: *query.Request, domain: tm.Domain) !tm.Id {
    const text = try @import("ids.zig").next();
    return r.interner.?.id(domain, try std.unicode.utf8ToUtf16LeAlloc(r.a, &text));
}
fn collectNodes(a: std.mem.Allocator, graph: Model, out: *std.ArrayList(Model)) anyerror!void {
    for (graph.nodes()) |node| {
        try out.append(a, node);
        for (model.typedScopesOf(node)) |scope| try collectNodes(a, scope.graph, out);
    }
}
const Clone = struct {
    r: *query.Request,
    nodes: Ids = .empty,
    patterns: Ids = .empty,
    fn binding(self: *Clone, original: *const tm.Binding) !*const tm.Binding {
        const result = try self.r.a.create(tm.Binding);
        result.* = original.*;
        result.id = self.patterns.get(original.id) orelse original.id;
        return result;
    }
    fn graph(self: *Clone, original: tm.GraphView) anyerror!tm.GraphView {
        const drafts = try self.r.a.alloc(builder.NodeDraft, original.body.graphs[original.index].count);
        var iterator = original.nodes();
        for (drafts) |*draft| {
            draft.* = try builder.fromNode(self.r.a, iterator.next().?);
            draft.id = self.nodes.get(draft.id) orelse draft.id;
            const payload = @constCast(draft.payload);
            for (tm.kindTable()[draft.kind].reads, payload) |read, *value| switch (value.*) {
                .id => |id| if (read.domain == .node) {
                    value.* = .{ .id = self.nodes.get(id) orelse id };
                } else if (read.domain == .binding) {
                    value.* = .{ .id = self.patterns.get(id) orelse id };
                },
                .optid => |id| if (id) |present| {
                    value.* = .{ .optid = self.nodes.get(present) orelse present };
                },
                .ids => |ids| {
                    const mapped = try self.r.a.dupe(tm.Id, ids);
                    for (mapped) |*id| id.* = self.nodes.get(id.*) orelse id.*;
                    value.* = .{ .ids = mapped };
                },
                .graph => |body| value.* = .{ .graph = try self.graph(body) },
                .varm => |arm| value.* = .{ .varm = .{ .binding = try self.binding(arm.binding), .body = try self.graph(arm.body) } },
                .errarm => |arm| value.* = .{ .errarm = .{ .binding = try self.binding(arm.binding), .body = try self.graph(arm.body) } },
                .ctx, .args, .capargs, .fields => |targets| {
                    const mapped = try self.r.a.alloc(tm.Target, targets.len());
                    for (mapped, 0..) |*pair, index| {
                        pair.* = targets.at(index);
                        pair.value = self.nodes.get(pair.value) orelse pair.value;
                    }
                    const result = try builder.targets(self.r.a, mapped);
                    value.* = switch (read.shape) {
                        .ctx => .{ .ctx = result },
                        .args => .{ .args = result },
                        .capargs => .{ .capargs = result },
                        .fields => .{ .fields = result },
                        else => unreachable,
                    };
                },
                .arms => |arms| {
                    const mapped = try self.r.a.alloc(tm.UnionArm, arms.len());
                    var arm_iterator = arms.iterator();
                    for (mapped) |*arm| {
                        arm.* = arm_iterator.next().?;
                        const bindings = try self.r.a.alloc(tm.FieldBinding, arm.fields.len());
                        for (bindings, 0..) |*field, index| {
                            field.* = arm.fields.at(index);
                            field.binding = try self.binding(field.binding);
                        }
                        arm.fields = try builder.fieldBindings(self.r.a, bindings);
                        arm.body = try self.graph(arm.body);
                    }
                    value.* = .{ .arms = try builder.unionArms(self.r.a, mapped) };
                },
                else => {},
            };
        }
        return builder.view(self.r.a, try builder.build(self.r.a, drafts, self.nodes.get(original.result()) orelse original.result()));
    }
};
/// State shared by every clone of one evolution: the cloned-node budget and the open
/// missing-argument obligations copied onto cloned calls.
const Copies = struct {
    nodes: usize = 0,
    obligations: std.ArrayList(*const tm.Obligation) = .empty,
};
// shortcut: nested matches on the source variant clone already-repaired arms (about 2^k nodes for
// k levels), so one evolution clones at most 10_000 nodes; raise it if real programs need more.
const max_cloned_nodes = 10_000;
fn clone(r: *query.Request, owner: Model, outer: tm.FieldBindings, graph: tm.GraphView, copies: *Copies) anyerror!struct { graph: tm.GraphView, ids: Ids } {
    var context: Clone = .{ .r = r };
    const graph_model = edit.borrow(owner, .{ .graph = graph });
    var nodes: std.ArrayList(Model) = .empty;
    try collectNodes(r.a, graph_model, &nodes);
    copies.nodes += nodes.items.len;
    if (copies.nodes > max_cloned_nodes) return reject(r, "TooLarge", try std.fmt.allocPrint(r.a, "Repairing the matches would clone more than {d} nodes; nested matches on the source variant multiply each copy.", .{max_cloned_nodes}));
    for (nodes.items) |node| if (!context.nodes.contains(node.data.node.id())) try context.nodes.put(r.a, node.data.node.id(), try fresh(r, .node));
    for (0..outer.len()) |index| {
        const binding = outer.at(index).binding;
        if (!context.patterns.contains(binding.id)) try context.patterns.put(r.a, binding.id, try fresh(r, .binding));
    }
    for (model.typedPatternBindings(r.a, graph_model)) |binding| if (!context.patterns.contains(binding.data.binding.id)) try context.patterns.put(r.a, binding.data.binding.id, try fresh(r, .binding));
    // A cloned call keeps its open missing-argument obligations, re-targeted at the copy; earlier
    // copies count too, since an outer arm may hold an inner arm that was already cloned.
    const copied = copies.obligations.items.len;
    for ([_][]const *const tm.Obligation{ r.typedModel().data.snapshot.obligations, copies.obligations.items[0..copied] }) |group| for (group) |obligation| {
        if (obligation.resolution != null) continue;
        const call = context.nodes.get(obligation.call) orelse continue;
        const next = try r.a.create(tm.Obligation);
        next.* = .{ .id = try fresh(r, .obligation), .origin = obligation.origin, .call = call, .parameter = obligation.parameter, .resolution = null };
        try copies.obligations.append(r.a, next);
    };
    return .{ .graph = try context.graph(graph), .ids = context.patterns };
}
const RepairContext = struct {
    r: *query.Request,
    function: Model,
    owner: Model,
    source_id: tm.Id,
    variant: tm.Id,
    field_ids: *const Ids,
    extra_fields: []const tm.Typed,
    copies: *Copies,
    count: usize = 0,
    fn repair(raw: *anyopaque, a: std.mem.Allocator, node: tm.Node) anyerror!?[]const builder.NodeDraft {
        const self: *RepairContext = @ptrCast(@alignCast(raw));
        if (!m.same(node.kind(), "matchUnion")) return null;
        const draft = try builder.fromNode(a, node);
        const payload = @constCast(draft.payload);
        for (tm.kindTable()[draft.kind].reads, payload) |read, *value| if (m.same(read.name, "arms")) {
            const arms = value.arms;
            var iterator = arms.iterator();
            const mapped = try a.alloc(tm.UnionArm, arms.len() + 1);
            for (mapped[0..arms.len()]) |*arm| arm.* = iterator.next().?;
            for (mapped[0..arms.len()], 0..) |source, index| {
                const found = for (source.variants) |variant| {
                    if (variant == self.source_id) break true;
                } else false;
                if (!found) continue;
                self.count += 1;
                if (source.variants.len > 1) {
                    mapped[index].variants = try std.mem.concat(a, tm.Id, &.{ source.variants, &.{self.variant} });
                    value.* = .{ .arms = try builder.unionArms(a, mapped[0..arms.len()]) };
                    return try a.dupe(builder.NodeDraft, &.{draft});
                }
                const copied = try clone(self.r, self.function, source.fields, source.body, self.copies);
                var bindings: std.ArrayList(tm.FieldBinding) = .empty;
                for (0..source.fields.len()) |field_index| {
                    const field = source.fields.at(field_index);
                    const binding = try a.create(tm.Binding);
                    binding.* = field.binding.*;
                    binding.id = copied.ids.get(binding.id).?;
                    try bindings.append(a, .{ .source = self.field_ids.get(field.source).?, .binding = binding });
                }
                const node_model = edit.borrow(self.function, .{ .node = node });
                for (self.extra_fields) |field| {
                    var field_type = try infer.typeOf(a, edit.typeRef(self.function, field.type));
                    if (model.typedCompletionOf(a, self.r.typedModel(), self.function, node_model.reference("inputNodeId"))) |completion| if (completion == .normal) {
                        const input_type = try infer.typeOf(a, completion.normal);
                        if (input_type == .named) {
                            var renderer: infer.Infer = .{ .r = self.r, .a = a, .c = undefined, .local = &.{}, .fresh = &.{} };
                            field_type = try renderer.instantiate(self.owner, input_type.named.arguments, field_type);
                        }
                    };
                    const binding_id = try fresh(self.r, .binding);
                    const binding = try a.create(tm.Binding);
                    binding.* = .{ .id = binding_id, .name = field.name, .type = try typeValue(self.r, field_type) };
                    try bindings.append(a, .{ .source = field.id, .binding = binding });
                }
                mapped[arms.len()] = .{ .variants = try a.dupe(tm.Id, &.{self.variant}), .fields = try builder.fieldBindings(a, bindings.items), .body = copied.graph };
                value.* = .{ .arms = try builder.unionArms(a, mapped) };
                return try a.dupe(builder.NodeDraft, &.{draft});
            }
        };
        return null;
    }
};
fn sortedType(r: *query.Request, original: *const tm.Type) !*const tm.Type {
    const result = try r.a.create(tm.Type);
    result.* = original.*;
    const variants = try r.a.dupe(tm.Variant, original.variants);
    for (variants) |*variant| {
        const fields = try r.a.dupe(tm.Typed, variant.fields);
        std.mem.sort(tm.Typed, fields, r.interner.?, struct {
            fn less(i: *const tm.Interner, x: tm.Typed, y: tm.Typed) bool {
                return i.less(.field, x.id, y.id);
            }
        }.less);
        variant.fields = fields;
    }
    std.mem.sort(tm.Variant, variants, r.interner.?, struct {
        fn less(i: *const tm.Interner, x: tm.Variant, y: tm.Variant) bool {
            return i.less(.variant, x.id, y.id);
        }
    }.less);
    result.variants = variants;
    return result;
}
fn simulate(r: *query.Request, wanted: []const u8, type_id: []const u8, source_id: []const u8, name: []const u8, additional: []const Field) anyerror!migration.Result {
    if (!m.same(wanted, r.snapshot_id)) return reject(r, "ExpectedBaseMismatch", "The Union-variant evolution request targets a different base snapshot.");
    if (try recipe.isBlank(r.a, name)) return reject(r, "DisplayNameBlank", "The new Union variant display name must not be blank.");
    const owner_type = edit.find(r.typedModel(), .types, type_id) orelse return reject(r, "TypeNotFound", "The target type does not exist.");
    if (owner_type.data.type_declaration.product) return reject(r, "TargetNotUnion", "The target type must be a Union.");
    const source = edit.find(owner_type, .variants, source_id) orelse return reject(r, "SourceVariantNotFound", "The source variant does not belong to the target Union.");
    var names: std.ArrayList([]const u8) = .empty;
    for (source.fields()) |field| try names.append(r.a, query.nameOf(field));
    for (additional) |field| try names.append(r.a, field.name);
    var invalid = preflight.repeated(names.items) != null;
    for (names.items) |field_name| if (try recipe.isBlank(r.a, field_name)) {
        invalid = true;
        break;
    };
    if (invalid) return reject(r, "FieldDisplayNameInvalid", "New variant field display names must be non-blank and distinct, including copied source fields.");
    for (owner_type.variants()) |variant| if (m.same(query.nameOf(variant), name)) return reject(r, "DuplicateVariantDisplayName", "The Union already has a variant with this display name.");
    var copied: std.ArrayList(tm.Typed) = .empty;
    var field_ids: Ids = .empty;
    for (source.data.variant.fields) |field| {
        var next = field;
        next.id = try fresh(r, .field);
        try field_ids.put(r.a, field.id, next.id);
        try copied.append(r.a, next);
    }
    const extra = try r.a.alloc(tm.Typed, additional.len);
    for (additional, extra) |field, *next| {
        const field_id = try fresh(r, .field);
        next.* = .{ .id = field_id, .name = try r.interner.?.string(try std.unicode.utf8ToUtf16LeAlloc(r.a, field.name)), .type = try typeValue(r, field.type) };
    }
    const variant_id = try fresh(r, .variant);
    const variant = try r.a.create(tm.Variant);
    variant.* = .{ .id = variant_id, .name = try r.interner.?.string(try std.unicode.utf8ToUtf16LeAlloc(r.a, name)), .fields = try std.mem.concat(r.a, tm.Typed, &.{ copied.items, extra }) };
    const updated_type = try edit.append(r.a, owner_type, .variants, edit.borrow(owner_type, .{ .variant = variant }));
    var candidate = try edit.replace(r.a, r.typedModel(), .types, updated_type);
    var rewritten: std.ArrayList(Model) = .empty;
    var ids: std.ArrayList(Value) = .empty;
    var count: usize = 0;
    var copies: Copies = .{};
    for (r.typedModel().functions()) |function| {
        var context: RepairContext = .{ .r = r, .function = function, .owner = owner_type, .source_id = source.data.variant.id, .variant = variant_id, .field_ids = &field_ids, .extra_fields = extra, .copies = &copies };
        const graph = try builder.mapPostOrder(r.a, function.data.function.body.graph(0), .{ .context = &context, .apply = RepairContext.repair });
        if (context.count > 0) {
            const updated = try edit.changed(r.a, function, .{ .body = graph });
            candidate = try edit.replace(r.a, candidate, .functions, updated);
            try rewritten.append(r.a, updated);
            try ids.append(r.a, migration.wireId(function));
            count += context.count;
        }
    }
    for (copies.obligations.items) |obligation| candidate = try edit.append(r.a, candidate, .obligations, edit.borrow(candidate, .{ .obligation = obligation }));
    const transaction = try m.id(r.a);
    const snapshot = try m.id(r.a);
    candidate = try migration.stamp(r, candidate, snapshot);
    try migration.validate(r, candidate);
    var output: std.Io.Writer.Allocating = .init(r.a);
    try @import("wire.zig").typedSemanticType(&output.writer, r.interner.?, try sortedType(r, updated_type.data.type_declaration));
    const fields = try m.object(r.a, &.{ try m.member(r.a, "updatedType", try model.parseValue(r.a, output.written())), try m.member(r.a, "addedVariantId", .{ .string = r.interner.?.idText(.variant, variant_id) }), try m.member(r.a, "addedVariantDisplayName", .{ .string = r.interner.?.stringText(variant.name) }), try m.member(r.a, "repairedFunctionIds", .{ .array = ids.items }), try m.member(r.a, "repairedMatchCount", try m.number(r.a, count)) });
    return .{ .kind = "unionVariantEvolution", .transaction = transaction, .snapshot = snapshot, .candidate = candidate, .diff = .json_null, .rewritten = rewritten.items, .summary = try std.fmt.allocPrint(r.a, "addUnionVariantLike '{s}' on '{s}' ({d} matches repaired)", .{ name, query.nameOf(owner_type), count }), .review_fields = fields, .touched_types = try r.a.dupe([]const u8, &.{type_id}) };
}

test "union clone remaps nested results and separate node binding domains while retaining literals" {
    const ids = @import("ids.zig");
    ids.init(std.testing.io, "typed-union-clone");
    defer ids.init(std.testing.io, null);
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    var strings: tm.Interner = .{ .gpa = a };
    const root = try model.parseValue(a,
        \\{"snapshotId":"s","functions":[{"id":"f","displayName":"F","parameters":[],"resultType":{"kind":"primitive","primitive":"string"},"requiredEffects":[],"body":{"nodes":[{"id":"input","kind":"stringLiteral","value":"input"},{"id":"root","kind":"bind","inputNodeId":"input","boundBody":{"valueBinding":{"id":"shared","type":{"kind":"primitive","primitive":"string"}},"body":{"nodes":[{"id":"shared","kind":"patternReference","patternBindingId":"shared"},{"id":"literal","kind":"stringLiteral","value":"shared"}],"resultNodeId":"literal"}}}],"resultNodeId":"root"}}]}
    );
    const generation = try tm.load(a, &strings, root, null);
    var request: query.Request = .{ .a = a, .line = "", .root = .empty, .provenance = null, .generation = &generation, .interner = &strings };
    const function = request.typedModel().functions()[0];
    var copies: Copies = .{};
    const copied = try clone(&request, function, try builder.fieldBindings(a, &.{}), function.data.function.body.graph(0), &copies);
    const root_node = copied.graph.body.findNode(copied.graph.result()).?;
    var root_payload = root_node.payload();
    _ = root_payload.next();
    const arm = root_payload.next().?.value.varm;
    var children = arm.body.nodes();
    const pattern = children.next().?;
    const literal = children.next().?;
    var pattern_payload = pattern.payload();
    try std.testing.expectEqual(arm.binding.id, pattern_payload.next().?.value.id);
    try std.testing.expectEqual(literal.id(), arm.body.result());
    try std.testing.expectEqualSlices(u16, &.{ 's', 'h', 'a', 'r', 'e', 'd' }, strings.stringText(literal.stringLiteral().?));
    try std.testing.expect(!std.mem.eql(u16, strings.idText(.node, pattern.id()), strings.idText(.binding, arm.binding.id)));
    try std.testing.expectEqualSlices(u16, &.{ 'r', 'o', 'o', 't' }, strings.idText(.node, generation.functions[0].body.graph(0).result()));
}
