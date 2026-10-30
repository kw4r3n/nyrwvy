//! Pending immutable generations. Simulation never replaces the committed base.
const std = @import("std");
const model = @import("model_reader");
const typed = model.session_model;
const query = @import("query.zig");
const preflight = @import("preflight.zig");
const infer = @import("infer.zig");
const m = @import("materialize.zig");
const projection = @import("projection.zig");
const wire = @import("wire.zig");
const Value = model.Value;
const Allocator = std.mem.Allocator;
const Writer = std.Io.Writer;

/// The base arena owns the session interner and committed records. Each proposal owns only its
/// added record/body and pointer table. Until commit/reject (3e), all live until session close;
/// there are no dead interned entries to compact. Never retain a request-arena pointer here.
pub const Pending = struct {
    arena: *std.heap.ArenaAllocator,
    id: []const u8,
    base_id: []const u8,
    generation: typed.Generation,
    model: Value,
    // Preserve graph traversal order for WireEncodeCommon; model is canonical for WireGraph.
    created_function: Value,
    receipt: []const u8,
    name: []const u8,
};
var storage: ?*std.heap.ArenaAllocator = null;
var interner: typed.Interner = undefined;
var base: typed.Generation = undefined;
var pending: std.ArrayList(Pending) = .empty;

fn newArena() !*std.heap.ArenaAllocator {
    const arena = try std.heap.page_allocator.create(std.heap.ArenaAllocator);
    arena.* = .init(std.heap.page_allocator);
    return arena;
}
fn freeArena(arena: *std.heap.ArenaAllocator) void {
    arena.deinit();
    std.heap.page_allocator.destroy(arena);
}
pub fn deinit() void {
    for (pending.items) |p| freeArena(p.arena);
    pending.deinit(std.heap.page_allocator);
    pending = .empty;
    if (storage) |arena| freeArena(arena);
    storage = null;
}
pub fn count() usize { return pending.items.len; }
pub fn find(wanted: []const u8) ?*const Pending {
    for (pending.items) |*p| if (m.same(p.id, wanted)) return p;
    return null;
}

fn initialize(r: *query.Request) !void {
    if (storage != null) return;
    const arena = try newArena();
    errdefer freeArena(arena);
    interner = .{ .gpa = arena.allocator() };
    base = try typed.load(arena.allocator(), &interner, r.model, null);
    storage = arena;
}

const validation_bail = "The Zig host does not word proposed-snapshot validation failures yet.";
fn unsupported(r: *query.Request, message: []const u8) anyerror {
    r.rejection = .{ .code = "UnsupportedOperation", .message = message };
    return error.Rejected;
}

fn receipt(a: Allocator, transaction: Value, snapshot: Value, r: *query.Request, fingerprints: model.Fingerprints) ![]const u8 {
    const value = try m.object(a, &.{
        try m.member(a, "transactionId", transaction),
        try m.member(a, "baseSnapshotId", try m.string(a, r.snapshot_id)),
        try m.member(a, "baseSnapshotFingerprint", try m.string(a, r.snapshot_fingerprint)),
        try m.member(a, "proposedSnapshotId", snapshot),
        try m.member(a, "proposedSnapshotFingerprint", try m.string(a, fingerprints.snapshot)),
        try m.member(a, "proposedSemanticFingerprint", try m.string(a, fingerprints.semantic)),
    });
    var out: Writer.Allocating = .init(a);
    try m.write(&out.writer, value);
    return out.written();
}

fn readiness(out: *Writer, root: Value) !void {
    var first = true;
    for (model.arrayItems(root, "obligations")) |o| {
        const status = model.property(o, "status").?;
        if (!m.same(model.utf8(model.decoded(status)), "open")) continue;
        if (first) try out.writeAll("{\"kind\":\"incomplete\",\"obligationIds\":[") else try out.writeByte(',');
        first = false;
        try m.write(out, model.property(o, "id").?);
    }
    if (first) try out.writeAll("{\"kind\":\"semanticallyReady\"}") else try out.writeAll("]}");
}

pub fn create(r: *query.Request, out: *Writer, c: preflight.Creation, fresh: []const preflight.TypeParameter, inferred: *infer.Infer) !void {
    const created = try m.function(r, c, fresh, inferred);
    // simulateCandidate allocates these only after materialization, even when validation fails.
    const transaction = try m.id(r.a);
    const snapshot = try m.id(r.a);
    // Validate before interning: rejected candidates leave no dead session IDs/strings.
    const functions = try r.a.alloc(Value, model.arrayItems(r.model, "functions").len + 1);
    @memcpy(functions[0 .. functions.len - 1], model.arrayItems(r.model, "functions"));
    functions[functions.len - 1] = created;
    var fields: std.ArrayList(model.Member) = .empty;
    for (r.model.object) |f| {
        const name = model.utf8(f.name);
        if (m.same(name, "snapshotId") or m.same(name, "parentSnapshotId") or m.same(name, "functions")) continue;
        try fields.append(r.a, f);
    }
    try fields.appendSlice(r.a, &.{
        try m.member(r.a, "snapshotId", snapshot),
        try m.member(r.a, "parentSnapshotId", try m.string(r.a, r.snapshot_id)),
        try m.member(r.a, "functions", .{ .array = functions }),
    });
    var candidate: Writer.Allocating = .init(r.a);
    try m.write(&candidate.writer, .{ .object = fields.items });
    const read = model.readDocument(r.a, candidate.written());
    if (read.root == .json_null or read.fingerprints == null) return unsupported(r, validation_bail);
    try initialize(r);
    const arena = try newArena();
    var registered = false;
    defer if (!registered) freeArena(arena);
    const a = arena.allocator();
    const generation = try typed.withFunction(a, &interner, &base, model.decoded(snapshot), created);
    var source: Writer.Allocating = .init(r.a);
    try typed.writeDocument(&source.writer, &interner, &generation, model.currentVersion());
    // Project the owned typed generation for step-2 queries. SnapshotReadiness is validation
    // followed by sorted open-obligation IDs, not execution/lowering readiness.
    const owned_model = try model.parseValue(a, try a.dupe(u8, source.written()));
    const created_id = model.decoded(model.property(created, "id").?);
    const proposed = for (model.arrayItems(owned_model, "functions")) |item| {
        if (std.mem.eql(u16, model.decoded(model.property(item, "id").?), created_id)) break item;
    } else unreachable;
    const root = model.canonicalRoot(a, owned_model);
    const proof = try receipt(a, transaction, snapshot, r, read.fingerprints.?);
    const proposal_id = try a.dupe(u8, model.utf8(model.decoded(transaction)));
    try pending.append(std.heap.page_allocator, .{
        .arena = arena, .id = proposal_id, .base_id = try a.dupe(u8, r.snapshot_id),
        .generation = generation, .model = root, .created_function = proposed, .receipt = proof, .name = try a.dupe(u8, c.display_name),
    });
    registered = true;
    model.useAllocator(r.a);
    var raw: Writer.Allocating = .init(r.a);
    const w = &raw.writer;
    try w.writeAll("{\"proposalId\":");
    try m.write(w, transaction);
    const full = if (r.root.get("detail")) |detail| m.same(detail.string, "full") else false;
    const structured = if (r.root.get("structuredReview")) |value| value == .bool and value.bool else false;
    try w.writeAll(if (full) ",\"detail\":\"full\"" else ",\"detail\":\"compact\"");
    try w.writeAll(",\"review\":{\"kind\":\"createFunction\",\"receipt\":");
    try w.writeAll(proof);
    try w.writeAll(",\"readiness\":");
    try readiness(w, root);
    try w.writeAll(",\"createdFunction\":");
    // Plain reviews retain materialization order; WireGraph uses canonical node/arm order.
    const nodes = model.bodyNodes(r.a, proposed);
    if (!full) {
        try wire.functionSummary(w, proposed, nodes.len);
    } else if (structured) {
        const canonical = for (model.arrayItems(root, "functions")) |item| {
            if (std.mem.eql(u16, model.decoded(model.property(item, "id").?), created_id)) break item;
        } else unreachable;
        try wire.functionEntity(w, canonical);
    } else {
        try wire.functionNodes(w, proposed, nodes);
    }
    const focus = model.focusOf(r.a, root, proposed);
    try w.writeAll(",\"bodyEffectRequirements\":");
    try query.writeRequirements(w, focus.requirements);
    try w.writeAll(",\"aborts\":");
    try query.writeAborts(w, focus.aborts, model.decoded(model.property(created, "id").?));
    if (focus.wait_path) |path| {
        try w.writeAll(",\"mayWait\":true,\"waitPath\":");
        try model.writeString(w, path);
    }
    try w.writeAll("}}");
    try out.writeAll(if (full) raw.written() else try projection.compact(r.a, raw.written()));
}

pub fn listing(a: Allocator, out: *Writer, current: []const u8) !void {
    const items = try a.dupe(Pending, pending.items);
    std.mem.sort(Pending, items, {}, struct { fn less(_: void, x: Pending, y: Pending) bool { return std.mem.order(u8, x.id, y.id) == .lt; } }.less);
    try out.print("{{\"count\":{d},\"proposals\":[", .{items.len});
    for (items, 0..) |p, i| {
        if (i > 0) try out.writeByte(',');
        try out.writeAll("{\"proposalId\":");
        try @import("root").writeString(out, p.id);
        try out.writeAll(",\"kind\":\"createFunction\",\"baseSnapshotId\":");
        try @import("root").writeString(out, p.base_id);
        try out.writeAll(if (m.same(current, p.base_id)) ",\"baseIsCurrent\":true,\"summary\":" else ",\"baseIsCurrent\":false,\"summary\":");
        try @import("root").writeString(out, try std.fmt.allocPrint(a, "createFunction '{s}'", .{p.name}));
        try out.writeByte('}');
    }
    try out.writeAll("]}");
}
