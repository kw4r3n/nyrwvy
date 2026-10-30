//! Pending immutable generations. Simulation never replaces the committed base.
const std = @import("std");
const model = @import("model_reader");
const typed = model.session_model;
const query = @import("query.zig");
const preflight = @import("preflight.zig");
const infer = @import("infer.zig");
const m = @import("materialize.zig");
const projection = @import("projection.zig");
const targeted = @import("target.zig");
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
    replacing: bool = false,
    target_node: ?[]const u8 = null,
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
    try publish(r, out, created, null, null);
}

pub fn replace(r: *query.Request, out: *Writer, target: Value, body: *const @import("recipe.zig").Node, inferred: *infer.Infer) !void {
    var materializer: m.Materializer = .{ .a = r.a, .r = r, .types = inferred, .fresh = &.{}, .function_id = model.property(target, "id").?, .parameters = model.arrayItems(target, "parameters") };
    const graph = try materializer.graph(null, body);
    const fields = try r.a.dupe(model.Member, target.object);
    for (fields) |*f| if (m.same(model.utf8(f.name), "body")) { f.value = graph; break; };
    try publish(r, out, .{ .object = fields }, target, null);
}

pub fn publish(r: *query.Request, out: *Writer, created: Value, previous: ?Value, change: ?targeted.Change) !void {
    // simulateCandidate allocates these only after materialization, even when validation fails.
    const transaction = try m.id(r.a);
    const snapshot = try m.id(r.a);
    // Validate before interning: rejected candidates leave no dead session IDs/strings.
    const old_functions = model.arrayItems(r.model, "functions");
    const functions = try r.a.alloc(Value, old_functions.len + @as(usize, if (previous != null) 0 else 1));
    @memcpy(functions[0..old_functions.len], old_functions);
    if (previous) |old| {
        for (functions) |*f| if (m.same(query.idOf(f.*), query.idOf(old))) { f.* = created; break; };
    } else functions[functions.len - 1] = created;
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
    if (previous != null and read.validation_issues.len > 0) {
        var details: Writer.Allocating = .init(r.a);
        try details.writer.writeAll("{\"diagnostics\":[");
        for (read.validation_issues, 0..) |issue, i| {
            if (i > 0) try details.writer.writeByte(',');
            try details.writer.writeAll("{\"code\":\"InvalidProposedSnapshot\",\"message\":");
            const prefix = try std.unicode.utf8ToUtf16LeAlloc(r.a, try std.fmt.allocPrint(r.a, "{s}: ", .{issue.code}));
            try model.writeString(&details.writer, try std.mem.concat(r.a, u16, &.{ prefix, issue.message }));
            try details.writer.writeByte('}');
        }
        try details.writer.writeAll("]}");
        r.rejection = .{ .code = "SimulationRejected", .message = "The structural request was rejected.", .details = details.written() };
        return error.Rejected;
    }
    if (read.root == .json_null or read.fingerprints == null) return unsupported(r, validation_bail);
    if (previous) |old| if (r.provenance) |provenance| {
        if (try imported(r, provenance, "function", query.idOf(old)))
            return invalidSnapshot(r, "ImportedDeclarationModified", "Dependency declarations are read-only; no proposal was registered.");
        var hidden_names: std.ArrayList([]const u8) = .empty;
        for ([_][2][]const u8{ .{ "types", "type" }, .{ "functions", "function" }, .{ "effects", "effect" }, .{ "capabilities", "capability" } }) |group| for (model.arrayItems(read.root, group[0])) |item| {
            const key = try std.fmt.allocPrint(r.a, "{s}:{s}", .{ group[1], query.idOf(item) });
            if (!preflight.contains(provenance.hidden, key)) continue;
            const references = model.referencesTo(r.a, read.root, group[1], model.decoded(model.property(item, "id").?)) orelse continue;
            for (references) |reference| {
                if (!try imported(r, provenance, @tagName(reference.owner), model.utf8(reference.owner_id))) {
                    try hidden_names.append(r.a, try std.fmt.allocPrint(r.a, "{s} '{s}' ({s})", .{ group[1], query.nameOf(item), query.idOf(item) }));
                    break;
                }
            }
        };
        if (hidden_names.items.len > 0) return invalidSnapshot(r, "ImportedDeclarationNotExported", try std.fmt.allocPrint(r.a, "The dependency does not export {s}; a dependent may refer only to exported declarations. No proposal was registered.", .{try std.mem.join(r.a, ", ", hidden_names.items)}));
    };
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
        .generation = generation, .model = root, .created_function = proposed, .receipt = proof, .name = try a.dupe(u8, query.nameOf(created)), .replacing = previous != null, .target_node = if (change) |c| try a.dupe(u8, c.target) else null,
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
    try w.writeAll(if (change != null) ",\"review\":{\"kind\":\"replaceTarget\",\"receipt\":" else if (previous != null) ",\"review\":{\"kind\":\"replaceBody\",\"receipt\":" else ",\"review\":{\"kind\":\"createFunction\",\"receipt\":");
    try w.writeAll(proof);
    try w.writeAll(",\"readiness\":");
    try readiness(w, root);
    if (change) |c| {
        try w.writeAll(",\"diff\":");
        try targeted.diff(r, w, previous.?, proposed, c, root);
    } else if (previous) |old| {
        try w.writeAll(",\"diff\":{\"ownerFunctionId\":");
        try m.write(w, model.property(old, "id").?);
        try w.writeAll(",\"oldResultNodeId\":");
        try m.write(w, model.property(model.property(old, "body").?, "resultNodeId").?);
        try w.writeAll(",\"newResultNodeId\":");
        try m.write(w, model.property(model.property(proposed, "body").?, "resultNodeId").?);
        try w.writeAll(",\"removedNodeIds\":");
        const removed = try r.a.dupe(Value, model.bodyNodes(r.a, old));
        std.mem.sort(Value, removed, {}, struct {
            fn less(_: void, x: Value, y: Value) bool { return std.mem.order(u16, model.decoded(model.property(x, "id").?), model.decoded(model.property(y, "id").?)) == .lt; }
        }.less);
        try nodeIds(w, removed);
        try w.writeAll(",\"addedNodeIds\":");
        try nodeIds(w, model.bodyNodes(r.a, proposed));
        try w.writeByte('}');
    }
    if (change != null) {
        try w.writeAll(",\"baseFocus\":");
        const old = previous.?;
        try query.reviewFocus(r, w, old, model.decoded(model.property(old, "id").?), model.bodyNodes(r.a, old), full, structured);
        var candidate_request = r.*;
        candidate_request.model = root;
        candidate_request.snapshot_id = model.utf8(model.decoded(snapshot));
        try w.writeAll(",\"proposedFocus\":");
        const focus_function = if (structured) blk: {
            for (model.arrayItems(root, "functions")) |f| if (std.mem.eql(u16, model.decoded(model.property(f, "id").?), created_id)) break :blk f;
            unreachable;
        } else proposed;
        try query.reviewFocus(&candidate_request, w, focus_function, created_id, model.bodyNodes(r.a, focus_function), full, structured);
    } else {
        try w.writeAll(if (previous != null) ",\"proposedFunction\":" else ",\"createdFunction\":");
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
        try out.writeAll(if (p.target_node != null) ",\"kind\":\"replaceTarget\",\"baseSnapshotId\":" else if (p.replacing) ",\"kind\":\"replaceBody\",\"baseSnapshotId\":" else ",\"kind\":\"createFunction\",\"baseSnapshotId\":");
        try @import("root").writeString(out, p.base_id);
        try out.writeAll(if (m.same(current, p.base_id)) ",\"baseIsCurrent\":true,\"summary\":" else ",\"baseIsCurrent\":false,\"summary\":");
        try @import("root").writeString(out, try std.fmt.allocPrint(a, "{s}'{s}'", .{ if (p.target_node) |id| try std.fmt.allocPrint(a, "replaceTarget {s} in ", .{id}) else if (p.replacing) @as([]const u8, "replaceBody of ") else "createFunction ", p.name }));
        try out.writeByte('}');
    }
    try out.writeAll("]}");
}

fn nodeIds(out: *Writer, nodes: []const Value) !void {
    try out.writeByte('[');
    for (nodes, 0..) |node, i| {
        if (i > 0) try out.writeByte(',');
        try m.write(out, model.property(node, "id").?);
    }
    try out.writeByte(']');
}

fn imported(r: *query.Request, provenance: wire.Provenance, kind: []const u8, wanted: []const u8) !bool {
    const key = try std.fmt.allocPrint(r.a, "{s}:{s}", .{ kind, wanted });
    for (provenance.members) |provider| if (preflight.contains(provider.keys, key)) return true;
    return false;
}

fn invalidSnapshot(r: *query.Request, code: []const u8, message: []const u8) anyerror {
    r.rejection = .{
        .code = "SnapshotCodecFailure",
        .message = try std.fmt.allocPrint(r.a, "The snapshot is invalid: {s}: {s}", .{ code, message }),
        .details = try std.fmt.allocPrint(r.a, "{{\"issues\":[{{\"code\":{s},\"message\":{s}}}]}}", .{ try query.stringOf(r.a, code), try query.stringOf(r.a, message) }),
    };
    return error.Rejected;
}
