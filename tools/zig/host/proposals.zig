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
/// added record/body and pointer table. Committed arenas retain shared ancestors until close.
/// Never retain a request-arena pointer here.
pub const Pending = struct {
    arena: *std.heap.ArenaAllocator,
    id: []const u8,
    base_id: []const u8,
    generation: typed.Generation,
    model: Value,
    session_model: Value,
    // Preserve graph traversal order for WireEncodeCommon; model is canonical for WireGraph.
    created_function: Value,
    receipt: []const u8,
    // Immutable wire reviews retain the simulation base, even after a later commit.
    full_review: []const u8,
    structured_review: []const u8,
    name: []const u8,
    declaration: ?Declaration = null,
    operations: bool = false,
    migration: ?struct { kind: []const u8, summary: []const u8, functions: []const Value, types: []const Value = &.{} } = null,
    dependency: ?@import("dependency_simulation.zig").Retained = null,
    batch: ?[]const query.Capture = null,
    operation_summary_data: Value = .json_null,
    replacing: bool = false,
    target_node: ?[]const u8 = null,
};
var storage: ?*std.heap.ArenaAllocator = null;
var interner: typed.Interner = undefined;
var base: typed.Generation = undefined;
var pending: std.ArrayList(Pending) = .empty;
var committed: std.ArrayList(*std.heap.ArenaAllocator) = .empty;

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
    for (committed.items) |arena| freeArena(arena);
    committed.deinit(std.heap.page_allocator);
    committed = .empty;
    pending.deinit(std.heap.page_allocator);
    pending = .empty;
    if (storage) |arena| freeArena(arena);
    storage = null;
}
pub fn count() usize {
    return pending.items.len;
}
pub fn find(wanted: []const u8) ?*const Pending {
    for (pending.items) |*p| if (m.same(p.id, wanted)) return p;
    return null;
}

/// Reserve before the disk write: installing a successfully persisted generation cannot fail.
pub fn prepareInstall() !void {
    try committed.ensureUnusedCapacity(std.heap.page_allocator, 1);
}
pub fn install(wanted: []const u8) void {
    for (pending.items, 0..) |p, i| if (m.same(p.id, wanted)) {
        base = p.generation;
        committed.appendAssumeCapacity(p.arena);
        _ = pending.orderedRemove(i);
        return;
    };
    unreachable;
}
pub fn reject(wanted: []const u8) void {
    for (pending.items, 0..) |p, i| if (m.same(p.id, wanted)) {
        freeArena(p.arena);
        _ = pending.orderedRemove(i);
        return;
    };
    unreachable;
}
pub fn document(a: Allocator, p: *const Pending) ![]const u8 {
    var out: Writer.Allocating = .init(a);
    try typed.writeDocument(&out.writer, &interner, &p.generation, model.currentVersion());
    return out.written();
}
pub fn writeReadiness(out: *Writer, root: Value) !void {
    try readiness(out, root);
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
    for (fields) |*f| if (m.same(model.utf8(f.name), "body")) {
        f.value = graph;
        break;
    };
    try publish(r, out, .{ .object = fields }, target, null);
}

pub const Declaration = struct {
    kind: []const u8,
    group: []const u8,
    subject: []const u8,
    items: []const Value,
};

pub fn declare(r: *query.Request, out: *Writer, declaration: Declaration) !void {
    try publishChange(r, out, declaration.items[0], null, null, declaration);
}

pub fn publish(r: *query.Request, out: *Writer, created: Value, previous: ?Value, change: ?targeted.Change) !void {
    try publishChange(r, out, created, previous, change, null);
}

fn publishChange(r: *query.Request, out: *Writer, created: Value, previous: ?Value, change: ?targeted.Change, declaration: ?Declaration) !void {
    // simulateCandidate allocates these only after materialization, even when validation fails.
    const transaction = try m.id(r.a);
    const snapshot = try m.id(r.a);
    // Validate before interning: rejected candidates leave no dead session IDs/strings.
    const group = if (declaration) |d| d.group else "functions";
    const added = if (declaration) |d| d.items else &.{created};
    const old_functions = model.arrayItems(r.model, group);
    const functions = try r.a.alloc(Value, old_functions.len + @as(usize, if (previous != null) 0 else added.len));
    @memcpy(functions[0..old_functions.len], old_functions);
    if (previous) |old| {
        for (functions) |*f| if (m.same(query.idOf(f.*), query.idOf(old))) {
            f.* = created;
            break;
        };
    } else @memcpy(functions[old_functions.len..], added);
    var fields: std.ArrayList(model.Member) = .empty;
    for (r.model.object) |f| {
        const name = model.utf8(f.name);
        if (m.same(name, "snapshotId") or m.same(name, "parentSnapshotId") or m.same(name, group)) continue;
        try fields.append(r.a, f);
    }
    try fields.appendSlice(r.a, &.{
        try m.member(r.a, "snapshotId", snapshot),
        try m.member(r.a, "parentSnapshotId", try m.string(r.a, r.snapshot_id)),
        try m.member(r.a, group, .{ .array = functions }),
    });
    var candidate: Writer.Allocating = .init(r.a);
    try m.write(&candidate.writer, .{ .object = fields.items });
    const read = model.readDocument(r.a, candidate.written());
    if (read.validation_issues.len > 0) {
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
        r.rejection = .{ .code = "SimulationRejected", .message = if (declaration) |d| if (m.same(d.group, "functions")) "The structural request was rejected." else try std.fmt.allocPrint(r.a, "The {s} declaration was rejected.", .{d.subject}) else "The structural request was rejected.", .details = details.written() };
        return error.Rejected;
    }
    if (read.root == .json_null or read.fingerprints == null) return unsupported(r, validation_bail);
    if (previous != null or declaration != null) if (r.provenance) |provenance| {
        if (previous) |old| if (try imported(r, provenance, "function", query.idOf(old)))
            return invalidSnapshot(r, "ImportedDeclarationModified", "Dependency declarations are read-only; no proposal was registered.");
        var hidden_names: std.ArrayList([]const u8) = .empty;
        for ([_][2][]const u8{ .{ "types", "type" }, .{ "functions", "function" }, .{ "effects", "effect" }, .{ "capabilities", "capability" } }) |category| for (model.arrayItems(read.root, category[0])) |item| {
            const key = try std.fmt.allocPrint(r.a, "{s}:{s}", .{ category[1], query.idOf(item) });
            if (!preflight.contains(provenance.hidden, key)) continue;
            const references = model.referencesTo(r.a, read.root, category[1], model.decoded(model.property(item, "id").?)) orelse continue;
            for (references) |reference| {
                if (!try imported(r, provenance, @tagName(reference.owner), model.utf8(reference.owner_id))) {
                    try hidden_names.append(r.a, try std.fmt.allocPrint(r.a, "{s} '{s}' ({s})", .{ category[1], query.nameOf(item), query.idOf(item) }));
                    break;
                }
            }
        };
        if (hidden_names.items.len > 0) return invalidSnapshot(r, "ImportedDeclarationNotExported", try std.fmt.allocPrint(r.a, "The dependency does not export {s}; a dependent may refer only to exported declarations. No proposal was registered.", .{try std.mem.join(r.a, ", ", hidden_names.items)}));
    };
    if (r.capture) |capture| {
        const candidate_model: Value = .{ .object = fields.items };
        const canonical = model.canonicalRoot(r.a, candidate_model);
        const proof = try receipt(r.a, transaction, snapshot, r, read.fingerprints.?);
        const projected_declaration: ?Declaration = if (declaration) |d| blk: {
            const items = try r.a.alloc(Value, d.items.len);
            for (d.items, items) |item, *projected| projected.* = @import("operation_model.zig").find(if (m.same(d.group, "functions")) candidate_model else canonical, d.group, query.idOf(item)).?;
            break :blk .{ .kind = d.kind, .group = d.group, .subject = d.subject, .items = items };
        } else null;
        var plain: Writer.Allocating = .init(r.a);
        var structured: Writer.Allocating = .init(r.a);
        var compact: Writer.Allocating = .init(r.a);
        try encodeChangeReview(projected_declaration, r, &plain.writer, created, previous, change, transaction, snapshot, canonical, created, proof, true, false);
        try encodeChangeReview(projected_declaration, r, &structured.writer, created, previous, change, transaction, snapshot, canonical, created, proof, true, true);
        try encodeChangeReview(projected_declaration, r, &compact.writer, created, previous, change, transaction, snapshot, canonical, created, proof, false, false);
        capture.* = .{ .candidate = candidate_model, .full_review = plain.written(), .structured_review = structured.written(), .compact_review = compact.written() };
        return;
    }
    try initialize(r);
    const arena = try newArena();
    var registered = false;
    defer if (!registered) freeArena(arena);
    const a = arena.allocator();
    const generation = if (declaration) |d| try typed.withDeclarations(a, &interner, &base, model.decoded(snapshot), d.group, d.items) else try typed.withFunction(a, &interner, &base, model.decoded(snapshot), created);
    var source: Writer.Allocating = .init(r.a);
    try typed.writeDocument(&source.writer, &interner, &generation, model.currentVersion());
    // Project the owned typed generation for step-2 queries. SnapshotReadiness is validation
    // followed by sorted open-obligation IDs, not execution/lowering readiness.
    const owned_model = try orderedProjection(a, try model.parseValue(a, try a.dupe(u8, source.written())), .{ .object = fields.items });
    const created_id = model.decoded(model.property(created, "id").?);
    const proposed = for (model.arrayItems(owned_model, group)) |item| {
        if (std.mem.eql(u16, model.decoded(model.property(item, "id").?), created_id)) break item;
    } else unreachable;
    const root = model.canonicalRoot(a, owned_model);
    const proof = try receipt(a, transaction, snapshot, r, read.fingerprints.?);
    const proposal_id = try a.dupe(u8, model.utf8(model.decoded(transaction)));
    // Wire declarations canonicalize unordered members, but retain request group order.
    const owned_declaration: ?Declaration = if (declaration) |d| blk: {
        const items = try a.alloc(Value, d.items.len);
        for (d.items, items) |item, *owned| {
            owned.* = for (model.arrayItems(if (m.same(d.group, "functions")) owned_model else root, d.group)) |entry| {
                if (m.same(query.idOf(item), query.idOf(entry))) break entry;
            } else unreachable;
        }
        break :blk .{ .kind = d.kind, .group = d.group, .subject = d.subject, .items = items };
    } else null;
    var plain: Writer.Allocating = .init(r.a);
    var structured_out: Writer.Allocating = .init(r.a);
    try encodeChangeReview(owned_declaration, r, &plain.writer, created, previous, change, transaction, snapshot, root, proposed, proof, true, false);
    try encodeChangeReview(owned_declaration, r, &structured_out.writer, created, previous, change, transaction, snapshot, root, proposed, proof, true, true);
    try pending.append(std.heap.page_allocator, .{
        .arena = arena,
        .declaration = owned_declaration,
        .id = proposal_id,
        .base_id = try a.dupe(u8, r.snapshot_id),
        .generation = generation,
        .model = root,
        .session_model = owned_model,
        .created_function = proposed,
        .receipt = proof,
        .full_review = try a.dupe(u8, plain.written()),
        .structured_review = try a.dupe(u8, structured_out.written()),
        .name = try a.dupe(u8, query.nameOf(created)),
        .replacing = previous != null,
        .target_node = if (change) |c| try a.dupe(u8, c.target) else null,
    });
    registered = true;
    const full = if (r.root.get("detail")) |detail| m.same(detail.string, "full") else false;
    const structured = if (r.root.get("structuredReview")) |value| value == .bool and value.bool else false;
    try encodeChangeReview(owned_declaration, r, out, created, previous, change, transaction, snapshot, root, proposed, proof, full, structured);
}

fn encodeChangeReview(declaration: ?Declaration, r: *query.Request, out: *Writer, created: Value, previous: ?Value, change: ?targeted.Change, transaction: Value, snapshot: Value, root: Value, proposed: Value, proof: []const u8, full: bool, structured: bool) !void {
    if (declaration) |d| {
        if (m.same(d.group, "functions")) {
            var encoded: Writer.Allocating = .init(r.a);
            try encodeReviewRaw(r, &encoded.writer, d.items[0], null, null, transaction, snapshot, root, proposed, proof, full, structured, false);
            if (d.items.len == 1) return out.writeAll(if (full or r.capture != null) encoded.written() else try projection.compact(r.a, encoded.written()));
            var combined: Writer.Allocating = .init(r.a);
            const target = &combined.writer;
            try target.writeAll(encoded.written()[0 .. encoded.written().len - 2]);
            try target.writeAll(",\"batch\":[");
            for (d.items[1..], 0..) |item, i| {
                if (i > 0) try target.writeByte(',');
                var extra: Writer.Allocating = .init(r.a);
                try encodeReviewRaw(r, &extra.writer, item, null, null, transaction, snapshot, root, item, proof, full, structured, false);
                const parsed = try model.parseValue(r.a, extra.written());
                const review = model.property(parsed, "review").?;
                try target.writeByte('{');
                var first = true;
                for (review.object) |field| {
                    const key = model.utf8(field.name);
                    if (m.same(key, "kind") or m.same(key, "receipt") or m.same(key, "readiness")) continue;
                    if (!first) try target.writeByte(',');
                    first = false;
                    try model.writeString(target, field.name);
                    try target.writeByte(':');
                    try m.write(target, field.value);
                }
                try target.writeByte('}');
            }
            try target.writeAll("]}}");
            return out.writeAll(if (full or r.capture != null) combined.written() else try projection.compact(r.a, combined.written()));
        }
        model.useAllocator(r.a);
        var raw: Writer.Allocating = .init(r.a);
        const w = &raw.writer;
        try w.writeAll("{\"proposalId\":");
        try m.write(w, transaction);
        try w.print(",\"detail\":\"{s}\",\"review\":{{\"kind\":\"{s}\",\"receipt\":{s},\"readiness\":", .{ if (full) @as([]const u8, "full") else "compact", d.kind, proof });
        try readiness(w, root);
        try w.print(",\"{s}\":", .{if (m.same(d.kind, "createTypes")) @as([]const u8, "createdTypes") else if (m.same(d.group, "types")) "createdType" else if (m.same(d.group, "effects")) "createdEffect" else "createdCapability"});
        if (m.same(d.kind, "createTypes")) try w.writeByte('[');
        for (d.items, 0..) |item, i| {
            if (i > 0) try w.writeByte(',');
            try declarationEntity(w, d.group, item);
        }
        if (m.same(d.kind, "createTypes")) try w.writeByte(']');
        try w.writeAll("}}");
        try out.writeAll(if (full or r.capture != null) raw.written() else try projection.compact(r.a, raw.written()));
    } else try encodeReview(r, out, created, previous, change, transaction, snapshot, root, proposed, proof, full, structured);
}

fn declarationEntity(out: *Writer, group: []const u8, item: Value) !void {
    if (m.same(group, "types")) return wire.semanticType(out, item);
    if (m.same(group, "capabilities")) return wire.capability(out, item);
    try out.writeAll("{\"id\":");
    try m.write(out, model.property(item, "id").?);
    try out.writeAll(",\"displayName\":");
    try m.write(out, model.property(item, "displayName").?);
    try out.writeByte('}');
}

pub fn proposalKind(p: *const Pending) []const u8 {
    return if (p.migration) |migration| migration.kind else if (p.batch != null) "batch" else if (p.operations) "operations" else if (p.declaration) |d| d.kind else if (p.target_node != null) "replaceTarget" else if (p.replacing) "replaceBody" else "createFunction";
}

fn encodeReview(r: *query.Request, out: *Writer, created: Value, previous: ?Value, change: ?targeted.Change, transaction: Value, snapshot: Value, root: Value, proposed: Value, proof: []const u8, full: bool, structured: bool) !void {
    return encodeReviewRaw(r, out, created, previous, change, transaction, snapshot, root, proposed, proof, full, structured, true);
}

fn encodeReviewRaw(r: *query.Request, out: *Writer, created: Value, previous: ?Value, change: ?targeted.Change, transaction: Value, snapshot: Value, root: Value, proposed: Value, proof: []const u8, full: bool, structured: bool, project_result: bool) !void {
    const created_id = model.decoded(model.property(created, "id").?);
    model.useAllocator(r.a);
    var raw: Writer.Allocating = .init(r.a);
    const w = &raw.writer;
    try w.writeAll("{\"proposalId\":");
    try m.write(w, transaction);
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
            fn less(_: void, x: Value, y: Value) bool {
                return std.mem.order(u16, model.decoded(model.property(x, "id").?), model.decoded(model.property(y, "id").?)) == .lt;
            }
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
    try out.writeAll(if (full or !project_result or r.capture != null) raw.written() else try projection.compact(r.a, raw.written()));
}

pub fn listing(a: Allocator, out: *Writer, current: []const u8, current_model: Value) !void {
    var summary_request: query.Request = .{ .a = a, .line = "", .root = .empty, .model = current_model, .provenance = null };
    const items = try a.dupe(Pending, pending.items);
    std.mem.sort(Pending, items, {}, struct {
        fn less(_: void, x: Pending, y: Pending) bool {
            return std.mem.order(u8, x.id, y.id) == .lt;
        }
    }.less);
    try out.print("{{\"count\":{d},\"proposals\":[", .{items.len});
    for (items, 0..) |p, i| {
        if (i > 0) try out.writeByte(',');
        try out.writeAll("{\"proposalId\":");
        try @import("root").writeString(out, p.id);
        try out.print(",\"kind\":\"{s}\",\"baseSnapshotId\":", .{proposalKind(&p)});
        try @import("root").writeString(out, p.base_id);
        try out.writeAll(if (m.same(current, p.base_id)) ",\"baseIsCurrent\":true,\"summary\":" else ",\"baseIsCurrent\":false,\"summary\":");
        if (p.migration != null) {
            try @import("root").writeString(out, try migrationSummary(a, &p, current_model));
        } else if (p.batch) |captures| {
            try @import("root").writeString(out, try batchSummary(&summary_request, captures));
        } else if (p.operations) {
            try @import("root").writeString(out, try @import("operation_engine.zig").summaryOperations(&summary_request, p.operation_summary_data));
        } else if (p.declaration) |d| {
            var names: std.ArrayList([]const u8) = .empty;
            for (d.items) |item| try names.append(a, try std.fmt.allocPrint(a, "{s}'{s}'", .{ if (m.same(d.group, "functions")) @as([]const u8, "createFunction ") else "", query.nameOf(item) }));
            try @import("root").writeString(out, if (m.same(d.group, "functions")) try std.mem.join(a, "; ", names.items) else try std.fmt.allocPrint(a, "{s} {s}", .{ d.kind, try std.mem.join(a, ", ", names.items) }));
        } else try @import("root").writeString(out, try std.fmt.allocPrint(a, "{s}'{s}'", .{ if (p.target_node) |id| try std.fmt.allocPrint(a, "replaceTarget {s} in ", .{id}) else if (p.replacing) @as([]const u8, "replaceBody of ") else "createFunction ", p.name }));
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

fn text(r: *query.Request, name: []const u8) ![]const u8 {
    const value = r.root.get(name) orelse return r.fail("Missing required property '{s}'.", .{name});
    if (value != .string) return r.fail("Property '{s}' must be a JSON string.", .{name});
    return query.readable(r, value.string);
}

pub fn lookup(r: *query.Request, wanted: []const u8) !*const Pending {
    if (r.session_invalidated) {
        r.rejection = .{ .code = "SessionInvalidated", .message = "The session was invalidated by external modification." };
        return error.Rejected;
    }
    return find(wanted) orelse {
        r.rejection = .{ .code = "ProposalNotFound", .message = try std.fmt.allocPrint(r.a, "No pending proposal has the ID '{s}'; it was committed, rejected, or discarded when its session closed.", .{wanted}) };
        return error.Rejected;
    };
}

fn integer(r: *query.Request, name: []const u8, fallback: i32) !i32 {
    const value = r.root.get(name) orelse return fallback;
    if (value == .integer or value == .float or value == .number_string) {
        // TryGetInt32 rejects decimals/exponents but accepts -0; std.json normalizes -0 to Float.
        const raw = model.property(try model.parseValue(r.a, r.line), name).?;
        return std.fmt.parseInt(i32, raw.number, 10) catch return r.fail("{s} must be a JSON Int32.", .{name});
    }
    return r.fail("{s} must be a JSON Int32.", .{name});
}

pub fn readProposal(r: *query.Request, out: *Writer) !void {
    if (try query.fieldProblem(r.a, r.line, &.{}, r.root, "readProposal request", &.{ "id", "op", "proposalId", "path", "offset", "limit", "structuredReview" }, &.{ "id", "op", "proposalId" })) |message| return r.fail("{s}", .{message});
    const path = if (r.root.get("path") != null) try text(r, "path") else "";
    const p = try lookup(r, try text(r, "proposalId"));
    var structured = true;
    if (r.root.get("structuredReview")) |value| {
        if (value != .bool) return r.fail("Property 'structuredReview' must be a JSON boolean.", .{});
        structured = value.bool;
    }
    const offset = try integer(r, "offset", 0);
    const limit = try integer(r, "limit", 20);
    try projection.page(r, out, p.id, m.same(r.snapshot_id, p.base_id), structured, if (structured) p.structured_review else p.full_review, path, offset, limit);
}

pub fn viewProposal(r: *query.Request, out: *Writer) !void {
    if (try query.fieldProblem(r.a, r.line, &.{}, r.root, "viewProposal request", &.{ "id", "op", "proposalId", "output" }, &.{ "id", "op", "proposalId" })) |message| return r.fail("{s}", .{message});
    const wanted = try text(r, "proposalId");
    const output = if (r.root.get("output") != null) try text(r, "output") else null;
    const p = try lookup(r, wanted);
    const path = try writeReview(r, p, output, null);
    try out.writeAll("{\"proposalId\":");
    try @import("root").writeString(out, p.id);
    try out.writeAll(",\"kind\":\"changeReview\",\"mediaType\":\"text/html; charset=utf-8\",\"path\":");
    try @import("root").writeString(out, path);
    try out.writeByte('}');
}

/// Same structural review and confined writer; verification adds only immutable evidence data.
pub fn writeReview(r: *query.Request, p: *const Pending, output: ?[]const u8, verification: ?[]const u8) ![]const u8 {
    const current = m.same(r.snapshot_id, p.base_id);
    const viewer = @import("viewer.zig");
    var data: Writer.Allocating = .init(r.a);
    const w = &data.writer;
    if (p.operations) {
        const review = model.property(try model.parseValue(r.a, p.structured_review), "review").?;
        try w.writeAll("{\"view\":\"changeReview\",\"transactionId\":");
        try @import("root").writeString(w, p.id);
        try w.writeAll(",\"baseSnapshotId\":");
        try @import("root").writeString(w, p.base_id);
        const snapshot = model.utf8(model.decoded(model.property(p.model, "snapshotId").?));
        try w.writeAll(",\"proposedSnapshotId\":");
        try @import("root").writeString(w, snapshot);
        try w.writeAll(if (current) ",\"baseIsCommitted\":true,\"readiness\":" else ",\"baseIsCommitted\":false,\"readiness\":");
        var state: Writer.Allocating = .init(r.a);
        try readiness(&state.writer, p.model);
        try w.writeAll(if (m.same(state.written(), "{\"kind\":\"semanticallyReady\"}")) "{\"kind\":\"complete\"}" else state.written());
        for ([_][]const u8{ "diff", "affectedFunctionIds", "affectedTypeIds" }) |key| {
            try w.print(",\"{s}\":", .{key});
            try m.write(w, model.property(review, key).?);
        }
        try w.writeAll(",\"functionFocus\":[");
        for (model.arrayItems(review, "affectedFunctionIds"), 0..) |id, i| {
            if (i > 0) try w.writeByte(',');
            const function = @import("operation_model.zig").find(p.model, "functions", model.utf8(model.decoded(id))).?;
            try w.writeAll(try viewer.focusValue(r.a, p.model, snapshot, function));
        }
        try w.writeAll("]}");
    } else {
        try w.writeAll("{\"view\":\"proposalReview\",\"transactionId\":");
        try @import("root").writeString(w, p.id);
        try w.writeAll(",\"proposalKind\":");
        try @import("root").writeString(w, proposalKind(p));
        try w.writeAll(",\"baseSnapshotId\":");
        try @import("root").writeString(w, p.base_id);
        const snapshot = model.utf8(model.decoded(model.property(p.model, "snapshotId").?));
        try w.writeAll(",\"proposedSnapshotId\":");
        try @import("root").writeString(w, snapshot);
        try w.writeAll(if (current) ",\"baseIsCommitted\":true,\"readiness\":" else ",\"baseIsCommitted\":false,\"readiness\":");
        try readiness(w, p.model);
        if (p.batch) |captures| {
            try w.writeAll(",\"changedFunctions\":[");
            for (try batchViewIds(r.a, captures, true), 0..) |id, i| {
                const function = @import("operation_model.zig").find(p.model, "functions", model.utf8(model.decoded(id))) orelse {
                    r.rejection = .{ .code = "ViewWriteFailed", .message = try std.fmt.allocPrint(r.a, "Proposed focus of function:{s} failed: FunctionNotFound", .{model.utf8(model.decoded(id))}) };
                    return error.Rejected;
                };
                if (i > 0) try w.writeByte(',');
                try w.writeAll("{\"committed\":");
                const before = if (current) @import("operation_model.zig").find(model.canonicalRoot(r.a, r.model), "functions", model.utf8(model.decoded(id))) else null;
                if (before) |old| try w.writeAll(try viewer.focusValue(r.a, model.canonicalRoot(r.a, r.model), r.snapshot_id, old)) else try w.writeAll("null");
                try w.writeAll(",\"proposed\":");
                try w.writeAll(try viewer.focusValue(r.a, p.model, snapshot, function));
                try w.writeByte('}');
            }
            try w.writeAll("],\"declaredTypes\":[");
            var first_type = true;
            for (try batchViewIds(r.a, captures, false)) |id| if (@import("operation_model.zig").find(p.model, "types", model.utf8(model.decoded(id)))) |item| {
                if (!first_type) try w.writeByte(',');
                first_type = false;
                try declarationEntity(w, "types", item);
            };
            try w.writeAll("],\"declaredCapabilities\":[],\"declaredEffects\":[],\"engineReview\":");
        } else if (if (p.declaration) |d| if (!m.same(d.group, "functions")) p.declaration else null else null) |d| {
            try w.writeAll(",\"changedFunctions\":[]");
            for ([_][2][]const u8{ .{ "types", "declaredTypes" }, .{ "capabilities", "declaredCapabilities" }, .{ "effects", "declaredEffects" } }) |entry| {
                try w.print(",\"{s}\":[", .{entry[1]});
                if (m.same(d.group, entry[0])) for (d.items, 0..) |item, i| {
                    if (i > 0) try w.writeByte(',');
                    if (m.same(d.group, "effects")) {
                        try w.writeAll("{\"id\":");
                        try m.write(w, model.property(item, "id").?);
                        try w.writeAll(",\"displayName\":");
                        try m.write(w, model.property(item, "displayName").?);
                        try w.writeAll(if (model.property(item, "exported") != null) ",\"exported\":true}" else ",\"exported\":false}");
                    } else try declarationEntity(w, d.group, item);
                };
                try w.writeByte(']');
            }
            try w.writeAll(",\"engineReview\":");
        } else {
            try w.writeAll(",\"changedFunctions\":[");
            const functions = if (p.migration) |migration| migration.functions else if (p.declaration) |d| d.items else &.{p.created_function};
            for (functions, 0..) |changed, i| {
                if (i > 0) try w.writeByte(',');
                try w.writeAll("{\"committed\":");
                var before: ?Value = null;
                const id = query.idOf(changed);
                const committed_root = model.canonicalRoot(r.a, r.model);
                if (current) for (model.arrayItems(committed_root, "functions")) |f| {
                    if (m.same(query.idOf(f), id)) {
                        before = f;
                        break;
                    }
                };
                if (before) |f| try w.writeAll(try viewer.focusValue(r.a, committed_root, r.snapshot_id, f)) else try w.writeAll("null");
                try w.writeAll(",\"proposed\":");
                const function = for (model.arrayItems(p.model, "functions")) |f| {
                    if (m.same(query.idOf(f), id)) break f;
                } else unreachable;
                try w.writeAll(try viewer.focusValue(r.a, p.model, snapshot, function));
                try w.writeByte('}');
            }
            try w.writeAll("],\"declaredTypes\":[");
            if (p.migration) |migration| for (migration.types, 0..) |item, i| {
                if (i > 0) try w.writeByte(',');
                try declarationEntity(w, "types", item);
            };
            try w.writeAll("],\"declaredCapabilities\":[],\"declaredEffects\":[],\"engineReview\":");
        }
        try w.writeAll(p.structured_review);
        if (verification) |evidence| {
            try w.writeAll(",\"verification\":");
            try w.writeAll(evidence);
        }
        try w.writeByte('}');
    }
    const name = try std.fmt.allocPrint(r.a, "nyrwvy-change-review-transaction-{x}.html", .{p.id});
    const content = try viewer.page(r.a, data.written());
    switch (try @import("view_file.zig").write(r.a, r.snapshot_path, output, ".html", name, viewer.header(), content)) {
        .refused => |message| {
            r.rejection = .{ .code = "ViewWriteFailed", .message = message };
            return error.Rejected;
        },
        .internal => {
            r.rejection = .{ .code = "InternalError", .message = "Unexpected internal failure; this request was not applied and the session is unchanged." };
            return error.Rejected;
        },
        .written => |path| return path,
    }
}

pub fn publishOperations(r: *query.Request, out: *Writer, result: @import("operation_engine.zig").Result) !void {
    try checkImports(r, result.candidate, result.diff);
    if (r.capture) |capture| {
        const canonical = model.canonicalRoot(r.a, result.candidate);
        var source: Writer.Allocating = .init(r.a);
        try m.write(&source.writer, result.candidate);
        const read = model.readDocument(r.a, source.written());
        const proof = try receipt(r.a, result.transaction, result.snapshot, r, read.fingerprints.?);
        var plain: Writer.Allocating = .init(r.a);
        var structured: Writer.Allocating = .init(r.a);
        var compact: Writer.Allocating = .init(r.a);
        try encodeOperations(r, &plain.writer, result, canonical, proof, true, false);
        try encodeOperations(r, &structured.writer, result, canonical, proof, true, true);
        try encodeOperations(r, &compact.writer, result, canonical, proof, false, false);
        capture.* = .{ .candidate = result.candidate, .full_review = plain.written(), .structured_review = structured.written(), .compact_review = compact.written(), .summary_operations = result.summary_operations };
        return;
    }
    try initialize(r);
    const arena = try newArena();
    var registered = false;
    defer if (!registered) freeArena(arena);
    const a = arena.allocator();
    const generation = try typed.withChanges(a, &interner, &base, r.model, result.candidate);
    var source: Writer.Allocating = .init(r.a);
    try typed.writeDocument(&source.writer, &interner, &generation, model.currentVersion());
    const owned_model = try orderedProjection(a, try model.parseValue(a, try a.dupe(u8, source.written())), result.candidate);
    const root = model.canonicalRoot(a, owned_model);
    const read = model.readDocument(r.a, source.written());
    const issues = try @import("operation_model.zig").validatedIssues(r, read);
    if (issues.len > 0) return @import("operation_engine.zig").rejected(r, "InvalidProposedSnapshot", try std.fmt.allocPrint(r.a, "{s}: {s}", .{ issues[0].code, model.utf8(issues[0].message) }));
    const proof = try receipt(a, result.transaction, result.snapshot, r, read.fingerprints.?);
    var plain: Writer.Allocating = .init(r.a);
    var structured: Writer.Allocating = .init(r.a);
    try encodeOperations(r, &plain.writer, result, root, proof, true, false);
    try encodeOperations(r, &structured.writer, result, root, proof, true, true);
    try pending.append(std.heap.page_allocator, .{
        .arena = arena,
        .id = try a.dupe(u8, model.utf8(model.decoded(result.transaction))),
        .base_id = try a.dupe(u8, r.snapshot_id),
        .generation = generation,
        .model = root,
        .session_model = owned_model,
        .created_function = .json_null,
        .receipt = proof,
        .full_review = try a.dupe(u8, plain.written()),
        .structured_review = try a.dupe(u8, structured.written()),
        .name = "",
        .operations = true,
        .operation_summary_data = try copyValue(a, result.summary_operations),
    });
    registered = true;
    const full = if (r.root.get("detail")) |value| m.same(value.string, "full") else false;
    const structured_review = if (r.root.get("structuredReview")) |value| value == .bool and value.bool else false;
    try encodeOperations(r, out, result, root, proof, full, structured_review);
}
fn affected(r: *query.Request, root: Value, diff: Value, functions: bool) ![]Value {
    var ids: std.ArrayList(Value) = .empty;
    for (diff.object) |field| {
        const key = model.utf8(field.name);
        for (field.value.array) |item| {
            var id: ?Value = null;
            if (m.same(key, "renames")) {
                const kind = @import("operation_model.zig").text(item, "kind");
                if (functions and m.same(kind, "function") or !functions and m.same(kind, "type")) id = model.property(item, "id") else if (functions and m.same(kind, "parameter")) id = model.property(item, "ownerFunctionId") else if (!functions and (m.same(kind, "field") or m.same(kind, "variant"))) id = model.property(item, "ownerTypeId");
            } else if (m.same(key, "visibilityChanges")) {
                if (m.same(@import("operation_model.zig").text(item, "kind"), if (functions) "function" else "type")) id = model.property(item, "id");
            } else if (functions and m.same(key, "attributeChanges")) {
                if (m.same(@import("operation_model.zig").text(item, "attribute"), "mustNotWait")) id = model.property(item, "id");
            } else if (functions and m.same(key, "requiredEffectsChanged")) id = model.property(item, "functionId") else id = model.property(item, if (functions) "ownerFunctionId" else "ownerTypeId");
            if (id) |value| try ids.append(r.a, value);
            if (functions) {
                for ([_][]const u8{ "callNodeId", "affectedCallNodeIds", "affectedMatchNodeIds", "affectedConstructNodeIds" }) |node_key| {
                    const nodes = model.property(item, node_key) orelse continue;
                    const values = if (nodes == .array) nodes.array else &.{nodes};
                    for (values) |node_id| for (model.arrayItems(root, "functions")) |function| for (model.bodyNodes(r.a, function)) |node| {
                        if (std.mem.eql(u16, model.decoded(model.property(node, "id").?), model.decoded(node_id))) try ids.append(r.a, model.property(function, "id").?);
                    };
                }
            }
        }
    }
    std.mem.sort(Value, ids.items, {}, struct {
        fn less(_: void, x: Value, y: Value) bool {
            return std.mem.order(u16, model.decoded(x), model.decoded(y)) == .lt;
        }
    }.less);
    var result: std.ArrayList(Value) = .empty;
    for (ids.items) |id| {
        if (@import("operation_model.zig").find(root, if (functions) "functions" else "types", model.utf8(model.decoded(id))) == null) continue;
        if (result.items.len > 0 and std.mem.eql(u16, model.decoded(result.items[result.items.len - 1]), model.decoded(id))) continue;
        try result.append(r.a, id);
    }
    return result.items;
}
fn encodeOperations(r: *query.Request, out: *Writer, result: @import("operation_engine.zig").Result, root: Value, proof: []const u8, full: bool, structured: bool) !void {
    model.useAllocator(r.a);
    var raw: Writer.Allocating = .init(r.a);
    const w = &raw.writer;
    try w.writeAll("{\"proposalId\":");
    try m.write(w, result.transaction);
    try w.print(",\"detail\":\"{s}\",\"review\":{{\"kind\":\"operations\",\"receipt\":{s},\"readiness\":", .{ if (full) @as([]const u8, "full") else "compact", proof });
    try readiness(w, root);
    try w.writeAll(",\"diff\":");
    try m.write(w, result.diff);
    const functions = try affected(r, root, result.diff, true);
    try w.writeAll(",\"affectedFunctionIds\":");
    try m.write(w, .{ .array = functions });
    try w.writeAll(",\"affectedTypeIds\":");
    try m.write(w, .{ .array = try affected(r, root, result.diff, false) });
    try w.writeAll(",\"functionFocus\":[");
    var candidate_request = r.*;
    candidate_request.model = if (structured) root else result.candidate;
    candidate_request.snapshot_id = model.utf8(model.decoded(result.snapshot));
    for (functions, 0..) |id, i| {
        if (i > 0) try w.writeByte(',');
        const function = @import("operation_model.zig").find(candidate_request.model, "functions", model.utf8(model.decoded(id))).?;
        try query.reviewFocus(&candidate_request, w, function, model.decoded(id), model.bodyNodes(r.a, function), full, structured);
    }
    try w.writeAll("]}}");
    try out.writeAll(if (full or r.capture != null) raw.written() else try projection.compact(r.a, raw.written()));
}

pub fn sameValue(x: Value, y: Value) bool {
    if (std.meta.activeTag(x) != std.meta.activeTag(y)) return false;
    return switch (x) {
        .object => |fields| blk: {
            if (fields.len != y.object.len) break :blk false;
            for (fields) |field| {
                const other = model.property(y, model.utf8(field.name)) orelse break :blk false;
                if (!sameValue(field.value, other)) break :blk false;
            }
            break :blk true;
        },
        .array => |items| blk: {
            if (items.len != y.array.len) break :blk false;
            for (items, y.array) |left, right| if (!sameValue(left, right)) break :blk false;
            break :blk true;
        },
        .string => std.mem.eql(u16, model.decoded(x), model.decoded(y)),
        .number => |value| std.mem.eql(u8, value, y.number),
        else => true,
    };
}

fn copyValue(a: Allocator, value: Value) !Value {
    var source: Writer.Allocating = .init(a);
    try m.write(&source.writer, value);
    return model.parseValue(a, source.written());
}

/// Typed pointer tables sort by ID; semantic snapshot lists retain insertion order for diagnostics.
fn orderedProjection(a: Allocator, owned: Value, candidate: Value) !Value {
    const fields = try a.dupe(model.Member, owned.object);
    for (fields) |*field| {
        const key = model.utf8(field.name);
        if (!preflight.contains(&.{ "functions", "types", "effects", "capabilities", "obligations" }, key)) continue;
        const order = model.arrayItems(candidate, key);
        const items = try a.alloc(Value, order.len);
        for (order, items) |source, *item| {
            item.* = @import("operation_model.zig").find(owned, key, query.idOf(source)) orelse unreachable;
        }
        field.value = .{ .array = items };
    }
    return .{ .object = fields };
}

fn batchReview(r: *query.Request, captures: []const query.Capture, transaction: Value, proof: []const u8, root: Value, full: bool, structured: bool) ![]const u8 {
    var fields: std.ArrayList(model.Member) = .empty;
    const first = try model.parseValue(r.a, if (!full) captures[0].compact_review else if (structured) captures[0].structured_review else captures[0].full_review);
    var state: Writer.Allocating = .init(r.a);
    try readiness(&state.writer, root);
    for (model.property(first, "review").?.object) |field| {
        const key = model.utf8(field.name);
        try fields.append(r.a, .{ .name = field.name, .value = if (m.same(key, "receipt")) try model.parseValue(r.a, proof) else if (m.same(key, "readiness")) try model.parseValue(r.a, state.written()) else field.value });
    }
    const later = try r.a.alloc(Value, captures.len - 1);
    for (captures[1..], later) |capture, *review| {
        const encoded = try model.parseValue(r.a, if (!full) capture.compact_review else if (structured) capture.structured_review else capture.full_review);
        review.* = try @import("operation_model.zig").remove(r.a, try @import("operation_model.zig").remove(r.a, model.property(encoded, "review").?, "receipt"), "readiness");
    }
    try fields.append(r.a, try m.member(r.a, "steps", .{ .array = later }));
    var encoded: Writer.Allocating = .init(r.a);
    try m.write(&encoded.writer, try m.object(r.a, &.{ try m.member(r.a, "proposalId", transaction), try m.member(r.a, "detail", try m.string(r.a, if (full) "full" else "compact")), try m.member(r.a, "review", .{ .object = fields.items }) }));
    return if (full) encoded.written() else projection.compact(r.a, encoded.written());
}

pub fn publishBatch(r: *query.Request, out: *Writer, captures: []const query.Capture, final: Value) !void {
    const transaction = try m.id(r.a);
    const snapshot = try m.id(r.a);
    const edit = @import("operation_model.zig");
    const candidate = try edit.set(r.a, try edit.set(r.a, final, "snapshotId", snapshot), "parentSnapshotId", try m.string(r.a, r.snapshot_id));
    var encoded: Writer.Allocating = .init(r.a);
    try m.write(&encoded.writer, candidate);
    const read = model.readDocument(r.a, encoded.written());
    const issues = try edit.validatedIssues(r, read);
    if (issues.len > 0) return preflight.rejected(r, "InvalidProposedSnapshot", try std.fmt.allocPrint(r.a, "{s}: {s}", .{ issues[0].code, model.utf8(issues[0].message) }));
    try initialize(r);
    const arena = try newArena();
    var registered = false;
    defer if (!registered) freeArena(arena);
    const a = arena.allocator();
    const generation = try typed.withChanges(a, &interner, &base, r.model, candidate);
    var source: Writer.Allocating = .init(r.a);
    try typed.writeDocument(&source.writer, &interner, &generation, model.currentVersion());
    const owned_model = try orderedProjection(a, try model.parseValue(a, try a.dupe(u8, source.written())), candidate);
    const root = model.canonicalRoot(a, owned_model);
    const proof = try receipt(a, transaction, snapshot, r, read.fingerprints.?);
    const owned_captures = try a.alloc(query.Capture, captures.len);
    for (captures, owned_captures) |capture, *owned| owned.* = .{ .full_review = try a.dupe(u8, capture.full_review), .summary_operations = try copyValue(a, capture.summary_operations) };
    try pending.append(std.heap.page_allocator, .{
        .arena = arena,
        .id = try a.dupe(u8, model.utf8(model.decoded(transaction))),
        .base_id = try a.dupe(u8, r.snapshot_id),
        .generation = generation,
        .model = root,
        .session_model = owned_model,
        .created_function = .json_null,
        .receipt = proof,
        .full_review = try a.dupe(u8, try batchReview(r, captures, transaction, proof, root, true, false)),
        .structured_review = try a.dupe(u8, try batchReview(r, captures, transaction, proof, root, true, true)),
        .name = "",
        .batch = owned_captures,
    });
    registered = true;
    const full = if (r.root.get("detail")) |detail| m.same(detail.string, "full") else false;
    const structured = if (r.root.get("structuredReview")) |value| value == .bool and value.bool else false;
    try out.writeAll(try batchReview(r, captures, transaction, proof, root, full, structured));
}

fn captureReview(a: Allocator, capture: query.Capture) !Value {
    return model.property(try model.parseValue(a, capture.full_review), "review").?;
}

fn batchSummary(r: *query.Request, captures: []const query.Capture) ![]const u8 {
    var parts: std.ArrayList([]const u8) = .empty;
    for (captures) |capture| {
        const review = try captureReview(r.a, capture);
        const kind = @import("operation_model.zig").text(review, "kind");
        if (capture.summary_operations != .json_null) {
            try parts.append(r.a, try @import("operation_engine.zig").summaryOperations(r, capture.summary_operations));
        } else if (m.same(kind, "createFunction")) {
            var functions: std.ArrayList([]const u8) = .empty;
            try functions.append(r.a, try std.fmt.allocPrint(r.a, "createFunction '{s}'", .{query.nameOf(model.property(review, "createdFunction").?)}));
            for (model.arrayItems(review, "batch")) |extra| try functions.append(r.a, try std.fmt.allocPrint(r.a, "createFunction '{s}'", .{query.nameOf(model.property(extra, "createdFunction").?)}));
            try parts.append(r.a, try std.mem.join(r.a, "; ", functions.items));
        } else if (m.same(kind, "createType") or m.same(kind, "createTypes")) {
            var names: std.ArrayList([]const u8) = .empty;
            const items = if (m.same(kind, "createType")) &.{model.property(review, "createdType").?} else model.arrayItems(review, "createdTypes");
            for (items) |item| try names.append(r.a, try std.fmt.allocPrint(r.a, "'{s}'", .{query.nameOf(item)}));
            try parts.append(r.a, try std.fmt.allocPrint(r.a, "{s} {s}", .{ kind, try std.mem.join(r.a, ", ", names.items) }));
        } else {
            const function = if (m.same(kind, "replaceBody")) model.property(review, "proposedFunction").? else model.property(model.property(review, "proposedFocus").?, "function").?;
            try parts.append(r.a, if (m.same(kind, "replaceBody")) try std.fmt.allocPrint(r.a, "replaceBody of '{s}'", .{query.nameOf(function)}) else try std.fmt.allocPrint(r.a, "replaceTarget {s} in '{s}'", .{ @import("operation_model.zig").text(model.property(review, "diff").?, "targetNodeId"), query.nameOf(function) }));
        }
    }
    return std.fmt.allocPrint(r.a, "batch of {s}", .{try std.mem.join(r.a, "; ", parts.items)});
}

fn appendDistinct(a: Allocator, ids: *std.ArrayList(Value), id: Value) !void {
    for (ids.items) |old| if (std.mem.eql(u16, model.decoded(old), model.decoded(id))) return;
    try ids.append(a, id);
}

fn batchViewIds(a: Allocator, captures: []const query.Capture, functions: bool) ![]const Value {
    var ids: std.ArrayList(Value) = .empty;
    // F# concatenates all signature owners before the touched function IDs.
    if (functions) for (captures) |capture| {
        const review = try captureReview(a, capture);
        if (model.property(review, "createdFunction")) |function| {
            try appendDistinct(a, &ids, model.property(function, "id").?);
            for (model.arrayItems(review, "batch")) |extra| try appendDistinct(a, &ids, model.property(model.property(extra, "createdFunction").?, "id").?);
        }
    };
    for (captures) |capture| {
        const review = try captureReview(a, capture);
        if (model.property(review, if (functions) "affectedFunctionIds" else "affectedTypeIds")) |affected_ids| for (affected_ids.array) |id| try appendDistinct(a, &ids, id);
        if (functions) {
            if (model.property(review, "diff")) |diff| {
                if (model.property(diff, "ownerFunctionId")) |id| try appendDistinct(a, &ids, id);
                for (model.arrayItems(diff, "functionsRemoved")) |removed| try appendDistinct(a, &ids, model.property(removed, "functionId").?);
            }
        } else {
            if (model.property(review, "createdType")) |item| try appendDistinct(a, &ids, model.property(item, "id").?);
            for (model.arrayItems(review, "createdTypes")) |item| try appendDistinct(a, &ids, model.property(item, "id").?);
            if (model.property(review, "diff")) |diff| for (model.arrayItems(diff, "typesRemoved")) |removed| try appendDistinct(a, &ids, model.property(removed, "typeId").?);
        }
    }
    return ids.items;
}

/// The pending view's signature owners and touched functions, in its established review order.
pub fn functionIds(r: *query.Request, p: *const Pending) ![]const Value {
    if (p.batch) |captures| return batchViewIds(r.a, captures, true);
    const functions = if (p.migration) |migration| migration.functions else if (p.declaration) |d| (if (m.same(d.group, "functions")) d.items else &.{}) else if (p.created_function != .json_null) &.{p.created_function} else &.{};
    var ids: std.ArrayList(Value) = .empty;
    for (functions) |function| try appendDistinct(r.a, &ids, model.property(function, "id").?);
    return ids.items;
}

fn checkImports(r: *query.Request, candidate: Value, diff: ?Value) !void {
    if (r.provenance) |provenance| {
        var hidden_names: std.ArrayList([]const u8) = .empty;
        for ([_][2][]const u8{ .{ "types", "type" }, .{ "functions", "function" }, .{ "effects", "effect" }, .{ "capabilities", "capability" } }) |category| {
            for (model.arrayItems(r.model, category[0])) |old| {
                if (!try imported(r, provenance, category[1], query.idOf(old))) continue;
                const proposed = @import("operation_model.zig").find(candidate, category[0], query.idOf(old));
                var touched = false;
                if (m.same(category[0], "functions") or m.same(category[0], "types")) {
                    if (diff != null) for (try affected(r, candidate, diff.?, m.same(category[0], "functions"))) |id| {
                        if (std.mem.eql(u16, model.decoded(id), model.decoded(model.property(old, "id").?))) {
                            touched = true;
                            break;
                        }
                    };
                } else if (m.same(category[0], "capabilities")) {
                    if (diff != null) for ([_][]const u8{ "capabilityOperationsAdded", "capabilityOperationsRemoved" }) |key| for (model.arrayItems(diff.?, key)) |change| {
                        if (m.same(@import("operation_model.zig").text(change, "ownerCapabilityId"), query.idOf(old))) touched = true;
                    };
                }
                if (touched or proposed == null or !sameValue(old, proposed.?)) return invalidSnapshot(r, "ImportedDeclarationModified", "Dependency declarations are read-only; no proposal was registered.");
            }
            for (model.arrayItems(candidate, category[0])) |item| {
                const key = try std.fmt.allocPrint(r.a, "{s}:{s}", .{ category[1], query.idOf(item) });
                if (!preflight.contains(provenance.hidden, key)) continue;
                const references = model.referencesTo(r.a, candidate, category[1], model.decoded(model.property(item, "id").?)) orelse continue;
                for (references) |reference| {
                    if (!try imported(r, provenance, @tagName(reference.owner), model.utf8(reference.owner_id))) {
                        try hidden_names.append(r.a, try std.fmt.allocPrint(r.a, "{s} '{s}' ({s})", .{ category[1], query.nameOf(item), query.idOf(item) }));
                        break;
                    }
                }
            }
        }
        if (hidden_names.items.len > 0) return invalidSnapshot(r, "ImportedDeclarationNotExported", try std.fmt.allocPrint(r.a, "The dependency does not export {s}; a dependent may refer only to exported declarations. No proposal was registered.", .{try std.mem.join(r.a, ", ", hidden_names.items)}));
    }
}

pub fn publishMigration(r: *query.Request, out: *Writer, result: @import("migration.zig").Result) !void {
    if (r.provenance) |provenance| for (provenance.members) |member| {
        for (result.touched_types) |id_| if (preflight.contains(member.keys, try std.fmt.allocPrint(r.a, "type:{s}", .{id_}))) return invalidSnapshot(r, "ImportedDeclarationModified", "Dependency declarations are read-only; no proposal was registered.");
        for (result.touched_capabilities) |id_| if (preflight.contains(member.keys, try std.fmt.allocPrint(r.a, "capability:{s}", .{id_}))) return invalidSnapshot(r, "ImportedDeclarationModified", "Dependency declarations are read-only; no proposal was registered.");
    };
    try checkImports(r, result.candidate, null);
    try initialize(r);
    const arena = try newArena();
    var registered = false;
    defer if (!registered) freeArena(arena);
    const a = arena.allocator();
    const generation = try typed.withChanges(a, &interner, &base, r.model, result.candidate);
    var source: Writer.Allocating = .init(r.a);
    try typed.writeDocument(&source.writer, &interner, &generation, model.currentVersion());
    const owned = try orderedProjection(a, try model.parseValue(a, try a.dupe(u8, source.written())), result.candidate);
    const root = model.canonicalRoot(a, owned);
    const read = model.readDocument(r.a, source.written());
    const proof = try receipt(a, result.transaction, result.snapshot, r, read.fingerprints.?);
    var plain: Writer.Allocating = .init(r.a);
    var structured: Writer.Allocating = .init(r.a);
    try encodeMigration(r, &plain.writer, result, root, proof, true, false);
    try encodeMigration(r, &structured.writer, result, root, proof, true, true);
    const functions = try a.alloc(Value, result.rewritten.len);
    for (result.rewritten, functions) |f, *item| item.* = @import("operation_model.zig").find(owned, "functions", query.idOf(f)).?;
    const changed_types = try a.alloc(Value, result.touched_types.len);
    for (result.touched_types, changed_types) |tid, *item| item.* = @import("operation_model.zig").find(root, "types", tid).?;
    try pending.append(std.heap.page_allocator, .{ .arena = arena, .id = try a.dupe(u8, model.utf8(model.decoded(result.transaction))), .base_id = try a.dupe(u8, r.snapshot_id), .generation = generation, .model = root, .session_model = owned, .created_function = .json_null, .receipt = proof, .full_review = try a.dupe(u8, plain.written()), .structured_review = try a.dupe(u8, structured.written()), .name = "", .migration = .{ .kind = result.kind, .summary = try a.dupe(u8, result.summary), .functions = functions, .types = changed_types } });
    registered = true;
    const full = if (r.root.get("detail")) |value| m.same(value.string, "full") else false;
    const structured_review = if (r.root.get("structuredReview")) |value| value == .bool and value.bool else false;
    try encodeMigration(r, out, result, root, proof, full, structured_review);
}
fn encodeMigration(r: *query.Request, out: *Writer, result: @import("migration.zig").Result, root: Value, proof: []const u8, full: bool, structured: bool) !void {
    var raw: Writer.Allocating = .init(r.a);
    const w = &raw.writer;
    try w.writeAll("{\"proposalId\":");
    try m.write(w, result.transaction);
    try w.print(",\"detail\":\"{s}\",\"review\":{{\"kind\":\"{s}\",\"receipt\":", .{ if (full) @as([]const u8, "full") else "compact", result.kind });
    try w.writeAll(proof);
    try w.writeAll(",\"readiness\":");
    try readiness(w, root);
    if (result.review_fields == .object) {
        for (result.review_fields.object) |field_| {
            try w.writeByte(',');
            try model.writeString(w, field_.name);
            try w.writeByte(':');
            try m.write(w, field_.value);
        }
    } else {
        try w.writeAll(",\"diff\":");
        try m.write(w, result.diff);
        try w.writeAll(",\"functionEffects\":[");
        const sorted = try r.a.dupe(Value, result.rewritten);
        std.mem.sort(Value, sorted, {}, struct {
            fn less(_: void, x: Value, y: Value) bool {
                return std.mem.order(u16, model.decoded(model.property(x, "id").?), model.decoded(model.property(y, "id").?)) == .lt;
            }
        }.less);
        for (sorted, 0..) |f, i| {
            if (i > 0) try w.writeByte(',');
            try w.writeAll("{\"functionId\":");
            try m.write(w, model.property(f, "id").?);
            try w.writeAll(",\"declaredEffectIds\":");
            try m.write(w, model.property(f, "requiredEffects").?);
            try w.writeAll(",\"bodyEffectRequirements\":");
            try query.writeRequirements(w, model.focusOf(r.a, root, f).requirements);
            try w.writeByte('}');
        }
        try w.writeByte(']');
    }
    if (full) {
        try w.writeAll(",\"rewrittenFunctions\":[");
        for (result.rewritten, 0..) |f, i| {
            if (i > 0) try w.writeByte(',');
            if (structured) try wire.functionEntity(w, @import("operation_model.zig").find(root, "functions", query.idOf(f)).?) else try wire.functionNodes(w, f, model.bodyNodes(r.a, f));
        }
        try w.writeByte(']');
    }
    try w.writeAll("}}");
    try out.writeAll(if (full) raw.written() else try projection.compact(r.a, raw.written()));
}

fn migrationSummary(a: Allocator, p: *const Pending, current: Value) ![]const u8 {
    const migration = p.migration.?;
    const parameter = m.same(migration.kind, "parameterTypeMigration");
    if (m.same(migration.kind, "productFieldTypeMigration") or m.same(migration.kind, "capabilityOperationSignatureMigration")) {
        const review = model.property(try model.parseValue(a, p.full_review), "review").?;
        const diff = model.property(review, "diff").?;
        const edit = @import("operation_model.zig");
        if (m.same(migration.kind, "productFieldTypeMigration")) {
            const change = model.property(diff, "fieldChange").?;
            const tid = edit.text(change, "typeId");
            const fid = edit.text(change, "fieldId");
            const t = edit.find(current, "types", tid);
            const type_name = if (t) |item| try std.fmt.allocPrint(a, "'{s}'", .{query.nameOf(item)}) else tid;
            var field_name: []const u8 = fid;
            for (model.arrayItems(current, "types")) |item| {
                for (model.arrayItems(item, "fields")) |field_| if (edit.hasId(field_, fid)) {
                    field_name = try std.fmt.allocPrint(a, "'{s}'", .{query.nameOf(field_)});
                };
                for (model.arrayItems(item, "variants")) |variant| for (model.arrayItems(variant, "fields")) |field_| if (edit.hasId(field_, fid)) {
                    field_name = try std.fmt.allocPrint(a, "'{s}'", .{query.nameOf(field_)});
                };
            }
            return std.fmt.allocPrint(a, "productFieldTypeMigration of {s} field {s}", .{ type_name, field_name });
        }
        const cid = edit.text(diff, "capabilityId");
        const c = edit.find(current, "capabilities", cid);
        const cap_name = if (c) |item| try std.fmt.allocPrint(a, "'{s}'", .{query.nameOf(item)}) else cid;
        return std.fmt.allocPrint(a, "capabilityOperationSignatureMigration of {s} operation '{s}'", .{ cap_name, query.nameOf(model.property(diff, "newOperation").?) });
    }
    if (!parameter and !m.same(migration.kind, "resultTypeMigration")) return migration.summary;
    const review = model.property(try model.parseValue(a, p.full_review), "review").?;
    const change = model.property(model.property(review, "diff").?, if (parameter) "parameterChange" else "resultChange").?;
    const fid = @import("operation_model.zig").text(change, "functionId");
    const f = @import("operation_model.zig").find(current, "functions", fid);
    const name = if (f) |item| try std.fmt.allocPrint(a, "'{s}'", .{query.nameOf(item)}) else fid;
    if (!parameter) return std.fmt.allocPrint(a, "resultTypeMigration of {s}", .{name});
    const pid = @import("operation_model.zig").text(change, "parameterId");
    var parameter_name: []const u8 = pid;
    for (model.arrayItems(current, "functions")) |function| for (model.arrayItems(function, "parameters")) |item| {
        if (m.same(query.idOf(item), pid)) parameter_name = try std.fmt.allocPrint(a, "'{s}'", .{query.nameOf(item)});
    };
    return std.fmt.allocPrint(a, "parameterTypeMigration of {s} parameter {s}", .{ name, parameter_name });
}

pub fn publishDependency(r: *query.Request, out: *Writer, result: @import("dependency_simulation.zig").Result) !void {
    var permissions = r.*;
    permissions.model = result.plan.dependency.root;
    permissions.provenance = result.plan.dependency.provenance;
    checkImports(&permissions, result.candidate, null) catch |err| {
        r.rejection = permissions.rejection;
        return err;
    };
    try initialize(r);
    const arena = try newArena();
    var registered = false;
    defer if (!registered) freeArena(arena);
    const a = arena.allocator();
    const generation = try typed.withChanges(a, &interner, &base, r.model, result.candidate);
    var source: Writer.Allocating = .init(a);
    try typed.writeDocument(&source.writer, &interner, &generation, model.currentVersion());
    const owned = try orderedProjection(a, try model.parseValue(a, source.written()), result.candidate);
    const root = model.canonicalRoot(a, owned);
    const read = model.readDocument(r.a, source.written());
    const proof = try receipt(a, result.transaction, result.snapshot, r, read.fingerprints.?);
    const dependency = try @import("dependency_simulation.zig").retain(a, r, result.plan);
    const functions = try a.alloc(Value, result.functions.len);
    for (result.functions, functions) |f, *copy| copy.* = @import("operation_model.zig").find(owned, "functions", query.idOf(f)).?;
    const types = try a.alloc(Value, result.types.len);
    for (result.types, types) |t, *copy| copy.* = @import("operation_model.zig").find(root, "types", query.idOf(t)).?;
    var plain: Writer.Allocating = .init(a);
    var structured: Writer.Allocating = .init(a);
    try encodeDependency(r, &plain.writer, result, root, proof, true, false);
    try encodeDependency(r, &structured.writer, result, root, proof, true, true);
    const summary = try std.fmt.allocPrint(a, "dependencyUpgrade: {d} selected, {d} functions and {d} types repaired", .{ model.arrayItems(result.plan.preview, "selected").len, functions.len, types.len });
    try pending.append(std.heap.page_allocator, .{
        .arena = arena,
        .id = try a.dupe(u8, model.utf8(model.decoded(result.transaction))),
        .base_id = try a.dupe(u8, r.snapshot_id),
        .generation = generation,
        .model = root,
        .session_model = owned,
        .created_function = .json_null,
        .receipt = proof,
        .full_review = plain.written(),
        .structured_review = structured.written(),
        .name = "",
        .migration = .{ .kind = "dependencyUpgrade", .summary = summary, .functions = functions, .types = types },
        .dependency = dependency,
    });
    registered = true;
    const full = if (r.root.get("detail")) |value| m.same(value.string, "full") else false;
    const structured_review = if (r.root.get("structuredReview")) |value| value == .bool and value.bool else false;
    try encodeDependency(r, out, result, root, proof, full, structured_review);
}
fn encodeDependency(r: *query.Request, out: *Writer, result: @import("dependency_simulation.zig").Result, root: Value, proof: []const u8, full: bool, structured: bool) !void {
    var raw: Writer.Allocating = .init(r.a);
    const w = &raw.writer;
    try w.writeAll("{\"proposalId\":");
    try m.write(w, result.transaction);
    try w.print(",\"detail\":\"{s}\",\"review\":{{\"kind\":\"dependencyUpgrade\",\"receipt\":", .{if (full) @as([]const u8, "full") else "compact"});
    try w.writeAll(proof);
    try w.writeAll(",\"readiness\":");
    try readiness(w, root);
    try w.writeAll(",\"dependencies\":{");
    for ([_][]const u8{ "replaced", "selected", "added", "removed", "changed" }, 0..) |key, i| {
        if (i > 0) try w.writeByte(',');
        try @import("root").writeString(w, key);
        try w.writeByte(':');
        const values = model.arrayItems(result.plan.preview, key);
        try w.writeByte('[');
        for (values, 0..) |item, index| {
            if (index > 0) try w.writeByte(',');
            try m.write(w, if (i < 2) item else try @import("operation_model.zig").remove(r.a, item, "exported"));
        }
        try w.writeByte(']');
    }
    try w.writeAll("},\"changedFunctions\":[");
    for (result.functions, 0..) |f, i| {
        if (i > 0) try w.writeByte(',');
        if (!full) try wire.functionSummary(w, f, model.bodyNodes(r.a, f).len) else if (structured) try wire.functionEntity(w, @import("operation_model.zig").find(root, "functions", query.idOf(f)).?) else try wire.functionNodes(w, f, model.bodyNodes(r.a, f));
    }
    try w.writeAll("],\"changedTypes\":[");
    for (result.types, 0..) |t, i| {
        if (i > 0) try w.writeByte(',');
        try wire.semanticType(w, @import("operation_model.zig").find(root, "types", query.idOf(t)).?);
    }
    try w.writeAll("],\"changedCapabilities\":[");
    for (result.capabilities, 0..) |c, i| {
        if (i > 0) try w.writeByte(',');
        try wire.capability(w, @import("operation_model.zig").find(root, "capabilities", query.idOf(c)).?);
    }
    try w.writeByte(']');
    if (full) {
        try w.writeAll(",\"rewrittenFunctions\":[");
        for (result.functions, 0..) |f, i| {
            if (i > 0) try w.writeByte(',');
            if (structured) try wire.functionEntity(w, @import("operation_model.zig").find(root, "functions", query.idOf(f)).?) else try wire.functionNodes(w, f, model.bodyNodes(r.a, f));
        }
        try w.writeByte(']');
    }
    try w.writeAll("}}");
    try out.writeAll(if (full) raw.written() else try projection.compact(r.a, raw.written()));
}
