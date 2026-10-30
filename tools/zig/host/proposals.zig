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
pub fn count() usize { return pending.items.len; }
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
    for (fields) |*f| if (m.same(model.utf8(f.name), "body")) { f.value = graph; break; };
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
        for (functions) |*f| if (m.same(query.idOf(f.*), query.idOf(old))) { f.* = created; break; };
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
    if ((previous != null or declaration != null) and read.validation_issues.len > 0) {
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
        r.rejection = .{ .code = "SimulationRejected", .message = if (declaration) |d| try std.fmt.allocPrint(r.a, "The {s} declaration was rejected.", .{d.subject}) else "The structural request was rejected.", .details = details.written() };
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
    const owned_model = try model.parseValue(a, try a.dupe(u8, source.written()));
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
            owned.* = for (model.arrayItems(root, d.group)) |entry| {
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
        .arena = arena, .declaration = owned_declaration, .id = proposal_id, .base_id = try a.dupe(u8, r.snapshot_id),
        .generation = generation, .model = root, .session_model = owned_model, .created_function = proposed, .receipt = proof, .full_review = try a.dupe(u8, plain.written()), .structured_review = try a.dupe(u8, structured_out.written()), .name = try a.dupe(u8, query.nameOf(created)), .replacing = previous != null, .target_node = if (change) |c| try a.dupe(u8, c.target) else null,
    });
    registered = true;
    const full = if (r.root.get("detail")) |detail| m.same(detail.string, "full") else false;
    const structured = if (r.root.get("structuredReview")) |value| value == .bool and value.bool else false;
    try encodeChangeReview(owned_declaration, r, out, created, previous, change, transaction, snapshot, root, proposed, proof, full, structured);
}

fn encodeChangeReview(declaration: ?Declaration, r: *query.Request, out: *Writer, created: Value, previous: ?Value, change: ?targeted.Change, transaction: Value, snapshot: Value, root: Value, proposed: Value, proof: []const u8, full: bool, structured: bool) !void {
    if (declaration) |d| {
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
        try out.writeAll(if (full) raw.written() else try projection.compact(r.a, raw.written()));
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
    return if (p.declaration) |d| d.kind else if (p.target_node != null) "replaceTarget" else if (p.replacing) "replaceBody" else "createFunction";
}

fn encodeReview(r: *query.Request, out: *Writer, created: Value, previous: ?Value, change: ?targeted.Change, transaction: Value, snapshot: Value, root: Value, proposed: Value, proof: []const u8, full: bool, structured: bool) !void {
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
        try out.print(",\"kind\":\"{s}\",\"baseSnapshotId\":", .{proposalKind(&p)});
        try @import("root").writeString(out, p.base_id);
        try out.writeAll(if (m.same(current, p.base_id)) ",\"baseIsCurrent\":true,\"summary\":" else ",\"baseIsCurrent\":false,\"summary\":");
        if (p.declaration) |d| {
            var names: std.ArrayList([]const u8) = .empty;
            for (d.items) |item| try names.append(a, try std.fmt.allocPrint(a, "'{s}'", .{query.nameOf(item)}));
            try @import("root").writeString(out, try std.fmt.allocPrint(a, "{s} {s}", .{d.kind, try std.mem.join(a, ", ", names.items)}));
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

fn lookup(r: *query.Request, wanted: []const u8) !*const Pending {
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
    const current = m.same(r.snapshot_id, p.base_id);
    const viewer = @import("viewer.zig");
    var data: Writer.Allocating = .init(r.a);
    const w = &data.writer;
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
    if (p.declaration) |d| {
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
        try w.writeAll(",\"changedFunctions\":[{\"committed\":");
        var before: ?Value = null;
        const id = query.idOf(p.created_function);
        const committed_root = model.canonicalRoot(r.a, r.model);
        if (current) for (model.arrayItems(committed_root, "functions")) |f| {
            if (m.same(query.idOf(f), id)) { before = f; break; }
        };
        if (before) |f| try w.writeAll(try viewer.focusValue(r.a, committed_root, r.snapshot_id, f)) else try w.writeAll("null");
        try w.writeAll(",\"proposed\":");
        const function = for (model.arrayItems(p.model, "functions")) |f| {
            if (m.same(query.idOf(f), id)) break f;
        } else unreachable;
        try w.writeAll(try viewer.focusValue(r.a, p.model, snapshot, function));
        try w.writeAll("}],\"declaredTypes\":[],\"declaredCapabilities\":[],\"declaredEffects\":[],\"engineReview\":");
    }
    try w.writeAll(p.structured_review);
    try w.writeByte('}');
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
        .written => |path| {
            try out.writeAll("{\"proposalId\":");
            try @import("root").writeString(out, p.id);
            try out.writeAll(",\"kind\":\"changeReview\",\"mediaType\":\"text/html; charset=utf-8\",\"path\":");
            try @import("root").writeString(out, path);
            try out.writeByte('}');
        },
    }
}
