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
const Model = model.Model;
const typed_edit = @import("typed_edit.zig");
const Allocator = std.mem.Allocator;
const Writer = std.Io.Writer;

/// Typed records live separately from owned proposal projections/reviews. A prepared live-set
/// rebuild drops historical records and dead interned texts on each commit/rejection.
/// Never retain a request-arena pointer here.
pub const Pending = struct {
    arena: *std.heap.ArenaAllocator,
    generation_arena: ?*std.heap.ArenaAllocator = null,
    interner_checkpoint: ?typed.Interner.Checkpoint = null,
    id: []const u8,
    base_id: []const u8,
    generation: typed.Generation,
    function_ids: []const model.Str = &.{},
    type_ids: []const model.Str = &.{},
    receipt: []const u8,
    // Immutable wire reviews retain the simulation base, even after a later commit.
    full_review: []const u8,
    structured_review: []const u8,
    name: []const u8,
    declaration: ?DeclarationInfo = null,
    operations: bool = false,
    migration: ?struct { kind: []const u8, summary: []const u8 } = null,
    dependency: ?@import("dependency_simulation.zig").Retained = null,
    batch: ?[]const query.Capture = null,
    operation_summary_data: Value = .json_null,
    replacing: bool = false,
    target_node: ?[]const u8 = null,
};
var storage: ?*std.heap.ArenaAllocator = null;
const InternerHeap = std.heap.DebugAllocator(.{ .safety = false, .thread_safe = false, .enable_memory_limit = @import("builtin").is_test });
var interner_heap: *InternerHeap = undefined;
var interner: typed.Interner = undefined;
var base: typed.Generation = undefined;
var pending: std.ArrayList(Pending) = .empty;
var current_payload: ?*std.heap.ArenaAllocator = null;
// Main keeps the selected dependency/manifest across ordinary commits.
var dependency_payload: ?*std.heap.ArenaAllocator = null;
const Prepared = struct {
    interner_heap: *InternerHeap,
    arena: *std.heap.ArenaAllocator,
    interner: typed.Interner,
    base: typed.Generation,
    generations: []typed.Generation,
};
var prepared: ?Prepared = null;

fn newInternerHeap() !*InternerHeap {
    const heap = try std.heap.page_allocator.create(InternerHeap);
    heap.* = .init;
    return heap;
}
fn freeInternerHeap(heap: *InternerHeap) void {
    _ = heap.deinit();
    std.heap.page_allocator.destroy(heap);
}

fn newArena() !*std.heap.ArenaAllocator {
    const arena = try std.heap.page_allocator.create(std.heap.ArenaAllocator);
    arena.* = .init(std.heap.page_allocator);
    return arena;
}
fn freeArena(arena: *std.heap.ArenaAllocator) void {
    arena.deinit();
    std.heap.page_allocator.destroy(arena);
}
fn freePending(p: Pending) void {
    if (p.generation_arena) |arena| freeArena(arena);
    freeArena(p.arena);
}
pub fn cancelInstall() void {
    if (prepared) |value| {
        var next_interner = value.interner;
        next_interner.deinit();
        freeInternerHeap(value.interner_heap);
        freeArena(value.arena);
    }
    prepared = null;
}
pub fn deinit() void {
    cancelInstall();
    for (pending.items) |p| freePending(p);
    pending.deinit(std.heap.page_allocator);
    pending = .empty;
    if (current_payload) |arena| if (dependency_payload != arena) freeArena(arena);
    if (dependency_payload) |arena| freeArena(arena);
    current_payload = null;
    dependency_payload = null;
    if (storage) |arena| {
        interner.deinit();
        freeInternerHeap(interner_heap);
        freeArena(arena);
    }
    storage = null;
}
/// Each pending proposal keeps its own arena and is rebuilt on every commit or reject, so a session
/// that never resolves them would grow without bound; simulate is refused at this many.
/// NYRWVY_TEST_PENDING_LIMIT overrides it, so tests that keep many proposals pending can lift it.
pub var pending_limit: usize = 64;
pub fn count() usize {
    return pending.items.len;
}
/// An encoder can fail after registering a proposal. Drop this request's additions on error.
pub fn discardAfter(previous_count: usize) void {
    while (pending.items.len > previous_count) {
        const p = pending.pop().?;
        if (p.interner_checkpoint) |checkpoint| interner.restore(checkpoint);
        freePending(p);
    }
}
pub fn find(wanted: []const u8) ?*const Pending {
    for (pending.items) |*p| if (m.same(p.id, wanted)) return p;
    return null;
}

fn prepare(wanted: []const u8, commit: bool) !void {
    cancelInstall();
    const selected = find(wanted) orelse unreachable;
    const arena = try newArena();
    errdefer freeArena(arena);
    const a = arena.allocator();
    const next_heap = try newInternerHeap();
    errdefer freeInternerHeap(next_heap);
    var next_interner: typed.Interner = .{ .gpa = next_heap.allocator() };
    errdefer next_interner.deinit();
    var scratch = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer scratch.deinit();
    var compactor: typed.Compactor = .{ .arena = a, .scratch = scratch.allocator(), .old_interner = &interner, .interner = &next_interner };
    const next_base = try compactor.generation(if (commit) &selected.generation else &base);
    const generations = try a.alloc(typed.Generation, pending.items.len - 1);
    var index: usize = 0;
    for (pending.items) |p| {
        if (m.same(p.id, wanted)) continue;
        generations[index] = try compactor.generation(&p.generation);
        index += 1;
    }
    prepared = .{ .interner_heap = next_heap, .arena = arena, .interner = next_interner, .base = next_base, .generations = generations };
}
/// Build all retained typed state before persistence; installation after rename cannot fail.
pub fn prepareInstall(wanted: []const u8) !void {
    try prepare(wanted, true);
}
pub fn prepareReject(wanted: []const u8) !void {
    try prepare(wanted, false);
}
fn installPrepared() void {
    const next = prepared orelse unreachable;
    if (storage) |arena| {
        interner.deinit();
        freeInternerHeap(interner_heap);
        freeArena(arena);
    }
    for (pending.items, next.generations) |*p, generation| {
        if (p.generation_arena) |arena| freeArena(arena);
        p.generation_arena = null;
        p.interner_checkpoint = null;
        p.generation = generation;
    }
    storage = next.arena;
    interner_heap = next.interner_heap;
    interner = next.interner;
    base = next.base;
    prepared = null;
}
pub fn install(wanted: []const u8) void {
    for (pending.items, 0..) |p, i| if (m.same(p.id, wanted)) {
        const previous = current_payload;
        const previous_dependency = dependency_payload;
        if (p.dependency != null) dependency_payload = p.arena;
        current_payload = p.arena;

        _ = pending.orderedRemove(i);
        if (p.generation_arena) |arena| freeArena(arena);
        installPrepared();
        if (previous) |arena| if (dependency_payload != arena) freeArena(arena);
        if (previous_dependency) |arena| if (dependency_payload != arena and previous != arena) freeArena(arena);
        return;
    };
    unreachable;
}
pub fn reject(wanted: []const u8) void {
    for (pending.items, 0..) |p, i| if (m.same(p.id, wanted)) {
        _ = pending.orderedRemove(i);
        installPrepared();
        freePending(p);
        return;
    };
    unreachable;
}
pub fn document(a: Allocator, p: *const Pending) ![]const u8 {
    var out: Writer.Allocating = .init(a);
    try typed.writeDocument(&out.writer, &interner, &p.generation, model.currentVersion());
    return out.written();
}

pub fn validate(a: Allocator, p: *const Pending) !struct { fingerprints: ?model.Fingerprints, id: []const u8 } {
    const checked = model.validateTyped(a, &interner, &p.generation);
    return .{
        .fingerprints = if (checked.valid) try @import("model_reader").session_writer.fingerprints(a, &interner, &p.generation) else null,
        .id = model.utf8(interner.idText(.snapshot, p.generation.snapshot)),
    };
}

pub fn openObligations(a: Allocator, p: *const Pending) ![]const []const u8 {
    var records: std.ArrayList(*const typed.Obligation) = .empty;
    for (p.generation.obligations) |obligation| if (obligation.resolution == null) try records.append(a, obligation);
    std.mem.sort(*const typed.Obligation, records.items, @as(*const typed.Interner, &interner), struct {
        fn less(i: *const typed.Interner, x: *const typed.Obligation, y: *const typed.Obligation) bool {
            return i.less(.obligation, x.id, y.id);
        }
    }.less);
    const result = try a.alloc([]const u8, records.items.len);
    for (records.items, result) |obligation, *name| name.* = model.utf8(interner.idText(.obligation, obligation.id));
    return result;
}

pub fn persistedDocument(a: Allocator, p: *const Pending, dependency: ?@import("main.zig").Dependency) ![]const u8 {
    var local = p.generation;
    if (dependency) |d| {
        const imported_generation = d.generation.?;
        const imported_interner = d.interner.?;
        inline for (.{ .{ "types", typed.Type, typed.Domain.type }, .{ "effects", typed.Effect, typed.Domain.effect }, .{ "capabilities", typed.Capability, typed.Domain.capability }, .{ "functions", typed.Function, typed.Domain.function }, .{ "obligations", typed.Obligation, typed.Domain.obligation } }) |entry| {
            // Keyed by the ID's UTF-16 bytes, so membership is the same as comparing the u16 texts.
            var imported_ids: std.StringHashMapUnmanaged(void) = .empty;
            for (@field(imported_generation, entry[0])) |other| try imported_ids.put(a, std.mem.sliceAsBytes(imported_interner.idText(entry[2], other.id)), {});
            var records: std.ArrayList(*const entry[1]) = .empty;
            for (@field(local, entry[0])) |record| {
                if (!imported_ids.contains(std.mem.sliceAsBytes(interner.idText(entry[2], record.id)))) try records.append(a, record);
            }
            @field(local, entry[0]) = records.items;
        }
        local.marker = .{ .snapshot = try std.unicode.utf8ToUtf16LeAlloc(a, d.id), .digest = try std.unicode.utf8ToUtf16LeAlloc(a, d.digest) };
    }
    var out: Writer.Allocating = .init(a);
    try typed.writeDocument(&out.writer, &interner, &local, model.currentVersion());
    return out.written();
}
pub fn writeReadiness(out: *Writer, root: Model) !void {
    try readiness(out, root);
}

pub fn initializeSnapshot(source: *const typed.Generation, source_interner: *const typed.Interner) !void {
    deinit();
    const arena = try newArena();
    errdefer freeArena(arena);
    interner_heap = try newInternerHeap();
    errdefer freeInternerHeap(interner_heap);
    interner = .{ .gpa = interner_heap.allocator() };
    errdefer interner.deinit();
    var scratch = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer scratch.deinit();
    var compactor: typed.Compactor = .{ .arena = arena.allocator(), .scratch = scratch.allocator(), .old_interner = source_interner, .interner = &interner };
    const ordered = try typed.order.generation(scratch.allocator(), source_interner, source);
    base = try compactor.generation(&ordered);
    storage = arena;
}

pub fn bindTypedRead(r: *query.Request) !void {
    std.debug.assert(storage != null);
    r.generation = &base;
    r.interner = &interner;
    if (r.interner_checkpoint == null) r.interner_checkpoint = interner.checkpoint();
}

pub fn rollbackRequest(checkpoint: typed.Interner.Checkpoint, previous_count: usize) void {
    discardAfter(previous_count);
    interner.restore(checkpoint);
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

fn readiness(out: *Writer, root: Model) !void {
    var first = true;
    const obligations = try root.context.allocator.dupe(*const typed.Obligation, root.data.snapshot.obligations);
    std.mem.sort(*const typed.Obligation, obligations, root.context.interner, struct {
        fn less(i: *const typed.Interner, x: *const typed.Obligation, y: *const typed.Obligation) bool {
            return i.less(.obligation, x.id, y.id);
        }
    }.less);
    for (obligations) |obligation| {
        if (obligation.resolution != null) continue;
        if (first) try out.writeAll("{\"kind\":\"incomplete\",\"obligationIds\":[") else try out.writeByte(',');
        first = false;
        try model.writeString(out, root.context.interner.idText(.obligation, obligation.id));
    }
    if (first) try out.writeAll("{\"kind\":\"semanticallyReady\"}") else try out.writeAll("]}");
}

pub fn typedModel(a: Allocator, p: *const Pending) Model {
    return model.typedRoot(a, &interner, &p.generation);
}

pub fn currentModel(a: Allocator) Model {
    return model.typedRoot(a, &interner, &base);
}

pub fn create(r: *query.Request, out: *Writer, c: preflight.Creation, fresh: []const preflight.TypeParameter, inferred: *infer.Infer) !void {
    const created = try m.function(r, c, fresh, inferred);
    try publish(r, out, typed_edit.borrow(r.typedModel(), .{ .function = created }), null, null);
}

pub fn replace(r: *query.Request, out: *Writer, target: Model, body: *const @import("recipe.zig").Node, inferred: *infer.Infer) !void {
    const function = target.data.function;
    var materializer: m.Materializer = .{ .a = r.a, .r = r, .types = inferred, .fresh = &.{}, .function_id = function.id, .parameters = function.parameters };
    const graph = try materializer.graph(null, body);
    try publish(r, out, try typed_edit.changed(r.a, target, .{ .body = graph }), target, null);
}

pub const Declaration = struct { kind: []const u8, group: []const u8, subject: []const u8, items: []const Model };
const DeclarationInfo = struct { kind: []const u8, group: []const u8, subject: []const u8, ids: []const model.Str, names: []const model.Str };

pub fn declare(r: *query.Request, out: *Writer, declaration: Declaration) !void {
    try publishChange(r, out, declaration.items[0], null, null, declaration);
}

pub fn publish(r: *query.Request, out: *Writer, created: Model, previous: ?Model, change: ?targeted.Change) !void {
    try publishChange(r, out, created, previous, change, null);
}

fn cloneGeneration(arena: Allocator, scratch: Allocator, candidate: *const typed.Generation) !typed.Generation {
    var compactor: typed.Compactor = .{ .arena = arena, .scratch = scratch, .old_interner = &interner, .interner = &interner };
    inline for (.{ base.types, base.effects, base.capabilities, base.functions, base.obligations }) |records| {
        for (records) |record| try compactor.records.put(scratch, @intFromPtr(record), @intFromPtr(record));
    }
    for (base.functions) |function| try compactor.bodies.put(scratch, @intFromPtr(function.body.graphs.ptr), function.body);
    return compactor.generation(candidate);
}

fn ownIds(a: Allocator, records: []const Model) ![]const model.Str {
    const result = try a.alloc(model.Str, records.len);
    for (records, result) |record, *id| id.* = try a.dupe(u16, record.id());
    return result;
}

const Publication = struct {
    declaration: ?Declaration = null,
    functions: []const Model = &.{},
    types: []const Model = &.{},
    name: []const u8 = "",
    replacing: bool = false,
    target: ?[]const u8 = null,
    operations: bool = false,
    summary_operations: Value = .json_null,
    migration: ?struct { kind: []const u8, summary: []const u8 } = null,
    captures: ?[]const query.Capture = null,
    dependency_plan: ?@import("dependency_upgrade.zig").Plan = null,
};

fn register(r: *query.Request, candidate: Model, transaction: Value, proof: []const u8, plain: []const u8, structured: []const u8, options: Publication) !void {
    const arena = try newArena();
    errdefer freeArena(arena);
    const a = arena.allocator();
    const generation_arena = try newArena();
    errdefer freeArena(generation_arena);
    const generation = try cloneGeneration(generation_arena.allocator(), r.a, candidate.data.snapshot);
    const declaration: ?DeclarationInfo = if (options.declaration) |d| blk: {
        const names = try a.alloc(model.Str, d.items.len);
        for (d.items, names) |item, *name| name.* = try a.dupe(u16, item.displayName());
        break :blk .{ .kind = try a.dupe(u8, d.kind), .group = try a.dupe(u8, d.group), .subject = try a.dupe(u8, d.subject), .ids = try ownIds(a, d.items), .names = names };
    } else null;
    const captures: ?[]const query.Capture = if (options.captures) |items| blk: {
        const owned = try a.alloc(query.Capture, items.len);
        for (items, owned) |item, *slot| slot.* = .{ .full_review = try a.dupe(u8, item.full_review), .summary_operations = try copyValue(a, item.summary_operations) };
        break :blk owned;
    } else null;
    try pending.append(std.heap.page_allocator, .{
        .arena = arena,
        .generation_arena = generation_arena,
        .interner_checkpoint = r.interner_checkpoint,
        .id = try a.dupe(u8, model.utf8(model.decoded(transaction))),
        .base_id = try a.dupe(u8, r.snapshot_id),
        .generation = generation,
        .receipt = try a.dupe(u8, proof),
        .full_review = try a.dupe(u8, plain),
        .structured_review = try a.dupe(u8, structured),
        .name = try a.dupe(u8, options.name),
        .replacing = options.replacing,
        .target_node = if (options.target) |target| try a.dupe(u8, target) else null,
        .function_ids = try ownIds(a, options.functions),
        .type_ids = try ownIds(a, options.types),
        .declaration = declaration,
        .operations = options.operations,
        .operation_summary_data = try copyValue(a, options.summary_operations),
        .batch = captures,
        .migration = if (options.migration) |migration| .{ .kind = try a.dupe(u8, migration.kind), .summary = try a.dupe(u8, migration.summary) } else null,
        .dependency = if (options.dependency_plan) |plan| try @import("dependency_simulation.zig").retain(a, r, plan) else null,
    });
}

fn publishChange(r: *query.Request, out: *Writer, created: Model, previous: ?Model, change: ?targeted.Change, declaration: ?Declaration) !void {
    const transaction = try m.id(r.a);
    const snapshot = try m.id(r.a);
    const group = if (declaration) |d| d.group else "functions";
    const collection = std.meta.stringToEnum(typed_edit.Collection, group).?;
    const added = if (declaration) |d| d.items else &.{created};
    var candidate = r.typedModel();
    if (previous != null) {
        candidate = try typed_edit.replace(r.a, candidate, collection, created);
    } else for (added) |item| {
        candidate = try typed_edit.append(r.a, candidate, collection, item);
    }
    candidate = try typed_edit.changed(r.a, candidate, .{ .snapshot = try interner.id(.snapshot, model.decoded(snapshot)) });
    candidate = try typed_edit.changed(r.a, candidate, .{ .parent = r.generation.?.snapshot });
    const read = model.validateTyped(r.a, &interner, candidate.data.snapshot);
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
    // F# `computeReceipt` -> `FingerprintFailed`, worded by WireEncode's `caseName` arm; no valid request reaches it.
    if (!read.valid) {
        const group_code: []const u8 = if (declaration) |d| if (m.same(d.group, "types")) "TypeDeclarationSimulationFailure" else if (m.same(d.group, "effects")) "EffectDeclarationSimulationFailure" else if (m.same(d.group, "capabilities")) "CapabilityDeclarationSimulationFailure" else "StructuralSimulationFailure" else "StructuralSimulationFailure";
        r.rejection = .{ .code = group_code, .message = "FingerprintFailed" };
        return error.Rejected;
    }
    if (previous != null or declaration != null) try checkImports(r, candidate, null);
    const found = try model.session_writer.fingerprints(r.a, &interner, candidate.data.snapshot);
    const proof = try receipt(r.a, transaction, snapshot, r, found);
    var plain: Writer.Allocating = .init(r.a);
    var structured: Writer.Allocating = .init(r.a);
    try encodeChangeReview(declaration, r, &plain.writer, created, previous, change, transaction, snapshot, candidate, created, proof, true, false);
    try encodeChangeReview(declaration, r, &structured.writer, created, previous, change, transaction, snapshot, candidate, created, proof, true, true);
    if (r.capture) |capture| {
        var compact: Writer.Allocating = .init(r.a);
        try encodeChangeReview(declaration, r, &compact.writer, created, previous, change, transaction, snapshot, candidate, created, proof, false, false);
        capture.* = .{ .candidate = candidate, .full_review = plain.written(), .structured_review = structured.written(), .compact_review = compact.written() };
        return;
    }
    try register(r, candidate, transaction, proof, plain.written(), structured.written(), .{
        .declaration = declaration,
        .functions = if (m.same(group, "functions")) added else &.{},
        .types = if (m.same(group, "types")) added else &.{},
        .name = query.nameOf(created),
        .replacing = previous != null,
        .target = if (change) |c| c.target else null,
    });
    const full = if (r.root.get("detail")) |detail| m.same(detail.string, "full") else false;
    const structured_review = if (r.root.get("structuredReview")) |value| value == .bool and value.bool else false;
    try encodeChangeReview(declaration, r, out, created, previous, change, transaction, snapshot, candidate, created, proof, full, structured_review);
}

fn encodeChangeReview(declaration: ?Declaration, r: *query.Request, out: *Writer, created: Model, previous: ?Model, change: ?targeted.Change, transaction: Value, snapshot: Value, root: Model, proposed: Model, proof: []const u8, full: bool, structured: bool) !void {
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

fn declarationEntity(out: *Writer, group: []const u8, item: Model) !void {
    if (m.same(group, "types")) return wire.typedSemanticType(out, item.context.interner, item.data.type_declaration);
    if (m.same(group, "capabilities")) return wire.typedCapability(out, item.context.interner, item.data.capability);
    try out.writeAll("{\"id\":");
    try m.write(out, idValue(item));
    try out.writeAll(",\"displayName\":");
    try m.write(out, nameValue(item));
    try out.writeByte('}');
}

pub fn proposalKind(p: *const Pending) []const u8 {
    return if (p.migration) |migration| migration.kind else if (p.batch != null) "batch" else if (p.operations) "operations" else if (p.declaration) |d| d.kind else if (p.target_node != null) "replaceTarget" else if (p.replacing) "replaceBody" else "createFunction";
}

fn encodeReview(r: *query.Request, out: *Writer, created: Model, previous: ?Model, change: ?targeted.Change, transaction: Value, snapshot: Value, root: Model, proposed: Model, proof: []const u8, full: bool, structured: bool) !void {
    return encodeReviewRaw(r, out, created, previous, change, transaction, snapshot, root, proposed, proof, full, structured, true);
}

fn encodeReviewRaw(r: *query.Request, out: *Writer, created: Model, previous: ?Model, change: ?targeted.Change, transaction: Value, snapshot: Value, root: Model, proposed: Model, proof: []const u8, full: bool, structured: bool, project_result: bool) !void {
    const created_id = created.id();
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
        try m.write(w, idValue(old));
        try w.writeAll(",\"oldResultNodeId\":");
        try m.write(w, .{ .string = old.body().resultNodeId() });
        try w.writeAll(",\"newResultNodeId\":");
        try m.write(w, .{ .string = proposed.body().resultNodeId() });
        try w.writeAll(",\"removedNodeIds\":");
        const removed = try r.a.dupe(Model, model.typedBodyNodes(r.a, old));
        std.mem.sort(Model, removed, {}, struct {
            fn less(_: void, x: Model, y: Model) bool {
                return std.mem.order(u16, x.id(), y.id()) == .lt;
            }
        }.less);
        try nodeIds(w, removed);
        try w.writeAll(",\"addedNodeIds\":");
        try nodeIds(w, model.typedBodyNodes(r.a, proposed));
        try w.writeByte('}');
    }
    if (change != null) {
        try w.writeAll(",\"baseFocus\":");
        const old = previous.?;
        try query.typedReviewFocus(r, w, r.typedModel(), old, old.id(), model.typedBodyNodes(r.a, old), full, structured);
        var candidate_request = r.*;
        candidate_request.generation = root.data.snapshot;
        candidate_request.snapshot_id = model.utf8(model.decoded(snapshot));
        try w.writeAll(",\"proposedFocus\":");
        const focus_function = if (structured) blk: {
            for (root.functions()) |f| if (std.mem.eql(u16, f.id(), created_id)) break :blk f;
            unreachable;
        } else proposed;
        try query.typedReviewFocus(&candidate_request, w, root, focus_function, created_id, model.typedBodyNodes(r.a, focus_function), full, structured);
    } else {
        try w.writeAll(if (previous != null) ",\"proposedFunction\":" else ",\"createdFunction\":");
        // Plain reviews retain materialization order; WireGraph uses canonical node/arm order.
        const nodes = model.typedBodyNodes(r.a, proposed);
        if (!full) {
            try functionSummary(w, proposed, nodes.len);
        } else if (structured) {
            const canonical = for (root.functions()) |item| {
                if (std.mem.eql(u16, item.id(), created_id)) break item;
            } else unreachable;
            try functionEntity(w, canonical);
        } else {
            try functionNodes(w, proposed, nodes);
        }
        const focus = model.typedFocusOf(r.a, root, proposed);
        try w.writeAll(",\"bodyEffectRequirements\":");
        try query.writeRequirements(w, focus.requirements);
        try w.writeAll(",\"aborts\":");
        try query.typedWriteAborts(w, focus.aborts, created.id());
        if (focus.wait_path) |path| {
            try w.writeAll(",\"mayWait\":true,\"waitPath\":");
            try model.writeString(w, path);
        }
    }
    try w.writeAll("}}");
    try out.writeAll(if (full or !project_result or r.capture != null) raw.written() else try projection.compact(r.a, raw.written()));
}

pub fn listing(a: Allocator, out: *Writer, current: []const u8, current_model: Model) !void {
    var summary_request: query.Request = .{ .a = a, .line = "", .root = .empty, .generation = current_model.data.snapshot, .interner = &interner, .provenance = null };
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
            for (d.names) |name| try names.append(a, try std.fmt.allocPrint(a, "{s}'{s}'", .{ if (m.same(d.group, "functions")) @as([]const u8, "createFunction ") else "", model.utf8(name) }));
            try @import("root").writeString(out, if (m.same(d.group, "functions")) try std.mem.join(a, "; ", names.items) else try std.fmt.allocPrint(a, "{s} {s}", .{ d.kind, try std.mem.join(a, ", ", names.items) }));
        } else try @import("root").writeString(out, try std.fmt.allocPrint(a, "{s}'{s}'", .{ if (p.target_node) |id| try std.fmt.allocPrint(a, "replaceTarget {s} in ", .{id}) else if (p.replacing) @as([]const u8, "replaceBody of ") else "createFunction ", p.name }));
        try out.writeByte('}');
    }
    try out.writeAll("]}");
}

fn idValue(record: Model) Value {
    return .{ .string = record.id() };
}
fn nameValue(record: Model) Value {
    return .{ .string = record.displayName() };
}
fn nodeIds(out: *Writer, nodes: []const Model) !void {
    try out.writeByte('[');
    for (nodes, 0..) |node, i| {
        if (i > 0) try out.writeByte(',');
        try model.writeString(out, node.id());
    }
    try out.writeByte(']');
}
fn functionSummary(out: *Writer, function: Model, count_: usize) !void {
    try wire.typedFunctionSummary(out, function.context.interner, function.data.function, count_);
}
fn functionEntity(out: *Writer, function: Model) !void {
    try wire.typedFunctionEntity(function.context.allocator, out, function.context.interner, function.data.function);
}
fn functionNodes(out: *Writer, function: Model, nodes: []const Model) !void {
    const native = try function.context.allocator.alloc(typed.Node, nodes.len);
    for (nodes, native) |node, *slot| slot.* = node.data.node;
    try wire.typedFunctionNodes(out, function.context.interner, function.data.function, native);
}
fn effectIds(out: *Writer, function: Model) !void {
    const effects = function.requiredEffects();
    try out.writeByte('[');
    for (effects, 0..) |effect, index| {
        if (index > 0) try out.writeByte(',');
        try model.writeString(out, effect.literal());
    }
    try out.writeByte(']');
}
fn pendingRecords(a: Allocator, p: *const Pending, collection: typed_edit.Collection, ids: []const model.Str) ![]const Model {
    const root = typedModel(a, p);
    const result = try a.alloc(Model, ids.len);
    for (ids, result) |id, *slot| slot.* = typed_edit.find(root, collection, model.utf8(id)) orelse return error.MissingProposalRecord;
    return result;
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
    const proposed_root = typedModel(r.a, p);
    const committed_root = r.typedModel();
    const viewer = @import("viewer.zig");
    var data: Writer.Allocating = .init(r.a);
    const w = &data.writer;
    if (p.operations) {
        const review = model.property(try model.parseValue(r.a, p.structured_review), "review").?;
        try w.writeAll("{\"view\":\"changeReview\",\"transactionId\":");
        try @import("root").writeString(w, p.id);
        try w.writeAll(",\"baseSnapshotId\":");
        try @import("root").writeString(w, p.base_id);
        const snapshot = model.utf8(proposed_root.id());
        try w.writeAll(",\"proposedSnapshotId\":");
        try @import("root").writeString(w, snapshot);
        try w.writeAll(if (current) ",\"baseIsCommitted\":true,\"readiness\":" else ",\"baseIsCommitted\":false,\"readiness\":");
        var state: Writer.Allocating = .init(r.a);
        try readiness(&state.writer, proposed_root);
        try w.writeAll(if (m.same(state.written(), "{\"kind\":\"semanticallyReady\"}")) "{\"kind\":\"complete\"}" else state.written());
        for ([_][]const u8{ "diff", "affectedFunctionIds", "affectedTypeIds" }) |key| {
            try w.print(",\"{s}\":", .{key});
            try m.write(w, model.property(review, key).?);
        }
        try w.writeAll(",\"functionFocus\":[");
        for (model.arrayItems(review, "affectedFunctionIds"), 0..) |id, i| {
            if (i > 0) try w.writeByte(',');
            const function = typed_edit.find(proposed_root, .functions, model.utf8(model.decoded(id))).?;
            try w.writeAll(try viewer.typedFocusValue(r.a, proposed_root, snapshot, function));
        }
        try w.writeAll("]}");
    } else {
        try w.writeAll("{\"view\":\"proposalReview\",\"transactionId\":");
        try @import("root").writeString(w, p.id);
        try w.writeAll(",\"proposalKind\":");
        try @import("root").writeString(w, proposalKind(p));
        try w.writeAll(",\"baseSnapshotId\":");
        try @import("root").writeString(w, p.base_id);
        const snapshot = model.utf8(proposed_root.id());
        try w.writeAll(",\"proposedSnapshotId\":");
        try @import("root").writeString(w, snapshot);
        try w.writeAll(if (current) ",\"baseIsCommitted\":true,\"readiness\":" else ",\"baseIsCommitted\":false,\"readiness\":");
        try readiness(w, proposed_root);
        if (p.batch) |captures| {
            try w.writeAll(",\"changedFunctions\":[");
            var first_function = true;
            for (try batchViewIds(r.a, captures, true)) |id| {
                // A function deleted by a step has no proposed focus, as in the single-proposal path.
                const function = typed_edit.find(proposed_root, .functions, model.utf8(model.decoded(id))) orelse continue;
                if (!first_function) try w.writeByte(',');
                first_function = false;
                try w.writeAll("{\"committed\":");
                const before = if (current) typed_edit.find(committed_root, .functions, model.utf8(model.decoded(id))) else null;
                if (before) |old| try w.writeAll(try viewer.typedFocusValue(r.a, committed_root, r.snapshot_id, old)) else try w.writeAll("null");
                try w.writeAll(",\"proposed\":");
                try w.writeAll(try viewer.typedFocusValue(r.a, proposed_root, snapshot, function));
                try w.writeByte('}');
            }
            try w.writeAll("],\"declaredTypes\":[");
            var first_type = true;
            for (try batchViewIds(r.a, captures, false)) |id| if (typed_edit.find(proposed_root, .types, model.utf8(model.decoded(id)))) |item| {
                if (!first_type) try w.writeByte(',');
                first_type = false;
                try declarationEntity(w, "types", item);
            };
            try w.writeAll("],\"declaredCapabilities\":[],\"declaredEffects\":[],\"engineReview\":");
        } else if (if (p.declaration) |d| if (!m.same(d.group, "functions")) p.declaration else null else null) |d| {
            try w.writeAll(",\"changedFunctions\":[]");
            for ([_][2][]const u8{ .{ "types", "declaredTypes" }, .{ "capabilities", "declaredCapabilities" }, .{ "effects", "declaredEffects" } }) |entry| {
                try w.print(",\"{s}\":[", .{entry[1]});
                if (m.same(d.group, entry[0])) for (try pendingRecords(r.a, p, std.meta.stringToEnum(typed_edit.Collection, d.group).?, d.ids), 0..) |item, i| {
                    if (i > 0) try w.writeByte(',');
                    if (m.same(d.group, "effects")) {
                        try w.writeAll("{\"id\":");
                        try m.write(w, idValue(item));
                        try w.writeAll(",\"displayName\":");
                        try m.write(w, nameValue(item));
                        try w.writeAll(if (item.exported()) ",\"exported\":true}" else ",\"exported\":false}");
                    } else try declarationEntity(w, d.group, item);
                };
                try w.writeByte(']');
            }
            try w.writeAll(",\"engineReview\":");
        } else {
            try w.writeAll(",\"changedFunctions\":[");
            const functions = try pendingRecords(r.a, p, .functions, p.function_ids);
            for (functions, 0..) |changed, i| {
                if (i > 0) try w.writeByte(',');
                try w.writeAll("{\"committed\":");
                var before: ?Model = null;
                const id = query.idOf(changed);
                if (current) for (committed_root.functions()) |f| {
                    if (m.same(query.idOf(f), id)) {
                        before = f;
                        break;
                    }
                };
                if (before) |f| try w.writeAll(try viewer.typedFocusValue(r.a, committed_root, r.snapshot_id, f)) else try w.writeAll("null");
                try w.writeAll(",\"proposed\":");
                const function = for (proposed_root.functions()) |f| {
                    if (m.same(query.idOf(f), id)) break f;
                } else unreachable;
                try w.writeAll(try viewer.typedFocusValue(r.a, proposed_root, snapshot, function));
                try w.writeByte('}');
            }
            try w.writeAll("],\"declaredTypes\":[");
            if (p.migration != null) for (try pendingRecords(r.a, p, .types, p.type_ids), 0..) |item, i| {
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
    const read = model.validateTyped(r.a, &interner, result.candidate.data.snapshot);
    if (read.validation_issues.len > 0) return @import("operation_engine.zig").rejected(r, "InvalidProposedSnapshot", try std.fmt.allocPrint(r.a, "{s}: {s}", .{ read.validation_issues[0].code, model.utf8(read.validation_issues[0].message) }));
    const found = try model.session_writer.fingerprints(r.a, &interner, result.candidate.data.snapshot);
    const proof = try receipt(r.a, result.transaction, result.snapshot, r, found);
    var plain: Writer.Allocating = .init(r.a);
    var structured: Writer.Allocating = .init(r.a);
    try encodeOperations(r, &plain.writer, result, result.candidate, proof, true, false);
    try encodeOperations(r, &structured.writer, result, result.candidate, proof, true, true);
    if (r.capture) |capture| {
        var compact: Writer.Allocating = .init(r.a);
        try encodeOperations(r, &compact.writer, result, result.candidate, proof, false, false);
        capture.* = .{ .candidate = result.candidate, .full_review = plain.written(), .structured_review = structured.written(), .compact_review = compact.written(), .summary_operations = result.summary_operations };
        return;
    }
    try register(r, result.candidate, result.transaction, proof, plain.written(), structured.written(), .{ .operations = true, .summary_operations = result.summary_operations });
    const full = if (r.root.get("detail")) |value| m.same(value.string, "full") else false;
    const structured_review = if (r.root.get("structuredReview")) |value| value == .bool and value.bool else false;
    try encodeOperations(r, out, result, result.candidate, proof, full, structured_review);
}

fn affected(r: *query.Request, root: Model, diff: Value, functions: bool) ![]Value {
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
                    for (values) |node_id| for (root.functions()) |function| for (model.typedBodyNodes(r.a, function)) |node| {
                        if (std.mem.eql(u16, node.id(), model.decoded(node_id))) try ids.append(r.a, idValue(function));
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
        if (typed_edit.find(root, if (functions) .functions else .types, model.utf8(model.decoded(id))) == null) continue;
        if (result.items.len > 0 and std.mem.eql(u16, model.decoded(result.items[result.items.len - 1]), model.decoded(id))) continue;
        try result.append(r.a, id);
    }
    return result.items;
}
fn encodeOperations(r: *query.Request, out: *Writer, result: @import("operation_engine.zig").Result, root: Model, proof: []const u8, full: bool, structured: bool) !void {
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
    candidate_request.generation = root.data.snapshot;
    candidate_request.snapshot_id = model.utf8(model.decoded(result.snapshot));
    for (functions, 0..) |id, i| {
        if (i > 0) try w.writeByte(',');
        const function = typed_edit.find(root, .functions, model.utf8(model.decoded(id))).?;
        try query.typedReviewFocus(&candidate_request, w, root, function, model.decoded(id), model.typedBodyNodes(r.a, function), full, structured);
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

fn batchReview(r: *query.Request, captures: []const query.Capture, transaction: Value, proof: []const u8, root: Model, full: bool, structured: bool) ![]const u8 {
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

pub fn publishBatch(r: *query.Request, out: *Writer, captures: []const query.Capture, final: Model) !void {
    const transaction = try m.id(r.a);
    const snapshot = try m.id(r.a);
    var candidate = try typed_edit.changed(r.a, final, .{ .snapshot = try interner.id(.snapshot, model.decoded(snapshot)) });
    candidate = try typed_edit.changed(r.a, candidate, .{ .parent = r.generation.?.snapshot });
    const read = model.validateTyped(r.a, &interner, candidate.data.snapshot);
    if (read.validation_issues.len > 0) return preflight.rejected(r, "InvalidProposedSnapshot", try std.fmt.allocPrint(r.a, "{s}: {s}", .{ read.validation_issues[0].code, model.utf8(read.validation_issues[0].message) }));
    const found = try model.session_writer.fingerprints(r.a, &interner, candidate.data.snapshot);
    const proof = try receipt(r.a, transaction, snapshot, r, found);
    const plain = try batchReview(r, captures, transaction, proof, candidate, true, false);
    const structured = try batchReview(r, captures, transaction, proof, candidate, true, true);
    try register(r, candidate, transaction, proof, plain, structured, .{ .captures = captures });
    const full = if (r.root.get("detail")) |detail| m.same(detail.string, "full") else false;
    const structured_review = if (r.root.get("structuredReview")) |value| value == .bool and value.bool else false;
    try out.writeAll(try batchReview(r, captures, transaction, proof, candidate, full, structured_review));
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
    var ids: std.ArrayList(Value) = .empty;
    for (p.function_ids) |id| try appendDistinct(r.a, &ids, .{ .string = id });
    return ids.items;
}

pub fn sameDeclaration(a: Allocator, left: Model, right: Model) !bool {
    var l: Writer.Allocating = .init(a);
    var r: Writer.Allocating = .init(a);
    for ([_]struct { Model, *Writer }{ .{ left, &l.writer }, .{ right, &r.writer } }) |entry| {
        const declaration: model.session_writer.Declaration = switch (entry[0].data) {
            .function => |value| .{ .function = value },
            .type_declaration => |value| .{ .type = value },
            .effect => |value| .{ .effect = value },
            .capability => |value| .{ .capability = value },
            else => unreachable,
        };
        try model.session_writer.writeDeclarationInSourceOrder(entry[1], a, entry[0].context.interner, declaration);
    }
    return std.mem.eql(u8, l.written(), r.written());
}

fn checkImports(r: *query.Request, candidate: Model, diff: ?Value) !void {
    if (r.provenance) |provenance| {
        var hidden_names: std.ArrayList([]const u8) = .empty;
        for ([_][2][]const u8{ .{ "types", "type" }, .{ "functions", "function" }, .{ "effects", "effect" }, .{ "capabilities", "capability" } }) |category| {
            for (r.typedModel().declarations(category[0])) |old| {
                if (!try imported(r, provenance, category[1], query.idOf(old))) continue;
                const proposed = typed_edit.find(candidate, std.meta.stringToEnum(typed_edit.Collection, category[0]).?, query.idOf(old));
                var touched = false;
                if (m.same(category[0], "functions") or m.same(category[0], "types")) {
                    if (diff != null) for (try affected(r, candidate, diff.?, m.same(category[0], "functions"))) |id| {
                        if (std.mem.eql(u16, model.decoded(id), old.id())) {
                            touched = true;
                            break;
                        }
                    };
                } else if (m.same(category[0], "capabilities")) {
                    if (diff != null) for ([_][]const u8{ "capabilityOperationsAdded", "capabilityOperationsRemoved" }) |key| for (model.arrayItems(diff.?, key)) |change| {
                        if (m.same(@import("operation_model.zig").text(change, "ownerCapabilityId"), query.idOf(old))) touched = true;
                    };
                }
                if (touched or proposed == null or !try sameDeclaration(r.a, old, proposed.?)) return invalidSnapshot(r, "ImportedDeclarationModified", "Dependency declarations are read-only; no proposal was registered.");
            }
            for (candidate.declarations(category[0])) |item| {
                const key = try std.fmt.allocPrint(r.a, "{s}:{s}", .{ category[1], query.idOf(item) });
                if (!preflight.contains(provenance.hidden, key)) continue;
                const references = model.typedReferencesTo(r.a, candidate, category[1], item.id()) orelse continue;
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
    const read = model.validateTyped(r.a, &interner, result.candidate.data.snapshot);
    if (read.validation_issues.len > 0) return preflight.rejected(r, "InvalidProposedSnapshot", try std.fmt.allocPrint(r.a, "{s}: {s}", .{ read.validation_issues[0].code, model.utf8(read.validation_issues[0].message) }));
    const found = try model.session_writer.fingerprints(r.a, &interner, result.candidate.data.snapshot);
    const proof = try receipt(r.a, result.transaction, result.snapshot, r, found);
    var plain: Writer.Allocating = .init(r.a);
    var structured: Writer.Allocating = .init(r.a);
    try encodeMigration(r, &plain.writer, result, result.candidate, proof, true, false);
    try encodeMigration(r, &structured.writer, result, result.candidate, proof, true, true);
    const changed_types = try r.a.alloc(Model, result.touched_types.len);
    for (result.touched_types, changed_types) |id, *item| item.* = typed_edit.find(result.candidate, .types, id).?;
    try register(r, result.candidate, result.transaction, proof, plain.written(), structured.written(), .{
        .functions = result.rewritten,
        .types = changed_types,
        .migration = .{ .kind = result.kind, .summary = result.summary },
    });
    const full = if (r.root.get("detail")) |value| m.same(value.string, "full") else false;
    const structured_review = if (r.root.get("structuredReview")) |value| value == .bool and value.bool else false;
    try encodeMigration(r, out, result, result.candidate, proof, full, structured_review);
}

fn encodeMigration(r: *query.Request, out: *Writer, result: @import("migration.zig").Result, root: Model, proof: []const u8, full: bool, structured: bool) !void {
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
        const sorted = try r.a.dupe(Model, result.rewritten);
        std.mem.sort(Model, sorted, {}, struct {
            fn less(_: void, x: Model, y: Model) bool {
                return std.mem.order(u16, x.id(), y.id()) == .lt;
            }
        }.less);
        for (sorted, 0..) |f, i| {
            if (i > 0) try w.writeByte(',');
            try w.writeAll("{\"functionId\":");
            try m.write(w, idValue(f));
            try w.writeAll(",\"declaredEffectIds\":");
            try effectIds(w, f);
            try w.writeAll(",\"bodyEffectRequirements\":");
            try query.writeRequirements(w, model.typedFocusOf(r.a, root, f).requirements);
            try w.writeByte('}');
        }
        try w.writeByte(']');
    }
    if (full) {
        try w.writeAll(",\"rewrittenFunctions\":[");
        for (result.rewritten, 0..) |f, i| {
            if (i > 0) try w.writeByte(',');
            if (structured) try functionEntity(w, typed_edit.find(root, .functions, query.idOf(f)).?) else try functionNodes(w, f, model.typedBodyNodes(r.a, f));
        }
        try w.writeByte(']');
    }
    try w.writeAll("}}");
    try out.writeAll(if (full) raw.written() else try projection.compact(r.a, raw.written()));
}

fn migrationSummary(a: Allocator, p: *const Pending, current: Model) ![]const u8 {
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
            const t = typed_edit.find(current, .types, tid);
            const type_name = if (t) |item| try std.fmt.allocPrint(a, "'{s}'", .{query.nameOf(item)}) else tid;
            var field_name: []const u8 = fid;
            for (current.types()) |item| {
                for (item.fields()) |field_| if (typed_edit.hasId(field_, fid)) {
                    field_name = try std.fmt.allocPrint(a, "'{s}'", .{query.nameOf(field_)});
                };
                for (item.variants()) |variant| for (variant.fields()) |field_| if (typed_edit.hasId(field_, fid)) {
                    field_name = try std.fmt.allocPrint(a, "'{s}'", .{query.nameOf(field_)});
                };
            }
            return std.fmt.allocPrint(a, "productFieldTypeMigration of {s} field {s}", .{ type_name, field_name });
        }
        const cid = edit.text(diff, "capabilityId");
        const c = typed_edit.find(current, .capabilities, cid);
        const cap_name = if (c) |item| try std.fmt.allocPrint(a, "'{s}'", .{query.nameOf(item)}) else cid;
        return std.fmt.allocPrint(a, "capabilityOperationSignatureMigration of {s} operation '{s}'", .{ cap_name, query.nameOf(model.property(diff, "newOperation").?) });
    }
    if (!parameter and !m.same(migration.kind, "resultTypeMigration")) return migration.summary;
    const review = model.property(try model.parseValue(a, p.full_review), "review").?;
    const change = model.property(model.property(review, "diff").?, if (parameter) "parameterChange" else "resultChange").?;
    const fid = @import("operation_model.zig").text(change, "functionId");
    const f = typed_edit.find(current, .functions, fid);
    const name = if (f) |item| try std.fmt.allocPrint(a, "'{s}'", .{query.nameOf(item)}) else fid;
    if (!parameter) return std.fmt.allocPrint(a, "resultTypeMigration of {s}", .{name});
    const pid = @import("operation_model.zig").text(change, "parameterId");
    var parameter_name: []const u8 = pid;
    for (current.functions()) |function| for (function.parameters()) |item| {
        if (m.same(query.idOf(item), pid)) parameter_name = try std.fmt.allocPrint(a, "'{s}'", .{query.nameOf(item)});
    };
    return std.fmt.allocPrint(a, "parameterTypeMigration of {s} parameter {s}", .{ name, parameter_name });
}

pub fn publishDependency(r: *query.Request, out: *Writer, result: @import("dependency_simulation.zig").Result) !void {
    var permissions = r.*;
    permissions.generation = result.plan.dependency.generation;
    permissions.interner = @constCast(result.plan.dependency.interner.?);
    permissions.provenance = result.plan.dependency.provenance;
    checkImports(&permissions, result.candidate, null) catch |err| {
        r.rejection = permissions.rejection;
        return err;
    };
    const found = try model.session_writer.fingerprints(r.a, &interner, result.candidate.data.snapshot);
    const proof = try receipt(r.a, result.transaction, result.snapshot, r, found);
    var plain: Writer.Allocating = .init(r.a);
    var structured: Writer.Allocating = .init(r.a);
    try encodeDependency(r, &plain.writer, result, result.candidate, proof, true, false);
    try encodeDependency(r, &structured.writer, result, result.candidate, proof, true, true);
    const summary = try std.fmt.allocPrint(r.a, "dependencyUpgrade: {d} selected, {d} functions and {d} types repaired", .{ model.arrayItems(result.plan.preview, "selected").len, result.functions.len, result.types.len });
    try register(r, result.candidate, result.transaction, proof, plain.written(), structured.written(), .{
        .functions = result.functions,
        .types = result.types,
        .migration = .{ .kind = "dependencyUpgrade", .summary = summary },
        .dependency_plan = result.plan,
    });
    const full = if (r.root.get("detail")) |value| m.same(value.string, "full") else false;
    const structured_review = if (r.root.get("structuredReview")) |value| value == .bool and value.bool else false;
    try encodeDependency(r, out, result, result.candidate, proof, full, structured_review);
}

fn encodeDependency(r: *query.Request, out: *Writer, result: @import("dependency_simulation.zig").Result, root: Model, proof: []const u8, full: bool, structured: bool) !void {
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
        if (!full) try functionSummary(w, f, model.typedBodyNodes(r.a, f).len) else if (structured) try functionEntity(w, typed_edit.find(root, .functions, query.idOf(f)).?) else try functionNodes(w, f, model.typedBodyNodes(r.a, f));
    }
    try w.writeAll("],\"changedTypes\":[");
    for (result.types, 0..) |t, i| {
        if (i > 0) try w.writeByte(',');
        try declarationEntity(w, "types", typed_edit.find(root, .types, query.idOf(t)).?);
    }
    try w.writeAll("],\"changedCapabilities\":[");
    for (result.capabilities, 0..) |c, i| {
        if (i > 0) try w.writeByte(',');
        try declarationEntity(w, "capabilities", typed_edit.find(root, .capabilities, query.idOf(c)).?);
    }
    try w.writeByte(']');
    if (full) {
        try w.writeAll(",\"rewrittenFunctions\":[");
        for (result.functions, 0..) |f, i| {
            if (i > 0) try w.writeByte(',');
            if (structured) try functionEntity(w, typed_edit.find(root, .functions, query.idOf(f)).?) else try functionNodes(w, f, model.typedBodyNodes(r.a, f));
        }
        try w.writeByte(']');
    }
    try w.writeAll("}}");
    try out.writeAll(if (full) raw.written() else try projection.compact(r.a, raw.written()));
}

const LifecycleTest = struct {
    const fixture =
        \\{"snapshotId":"base","types":[],"effects":[],"capabilities":[],"functions":[{"id":"f","displayName":"F","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"nodes":[{"id":"n","kind":"unitLiteral"}],"resultNodeId":"n"}}],"obligations":[]}
    ;

    fn add(index: usize) ![]const u8 {
        const arena = try newArena();
        errdefer freeArena(arena);
        const a = arena.allocator();
        model.useAllocator(a);
        const generation_arena = try newArena();
        errdefer freeArena(generation_arena);
        const checkpoint = interner.checkpoint();
        errdefer interner.restore(checkpoint);
        const name = try std.fmt.allocPrint(a, "Version-{d}", .{index});
        const id = try std.fmt.allocPrint(a, "snapshot-{d}", .{index});
        const root = currentModel(a);
        const function = try typed_edit.changed(a, root.functions()[0], .{ .name = try interner.string(try std.unicode.utf8ToUtf16LeAlloc(a, name)) });
        var candidate = try typed_edit.replace(a, root, .functions, function);
        candidate = try typed_edit.changed(a, candidate, .{ .snapshot = try interner.id(.snapshot, try std.unicode.utf8ToUtf16LeAlloc(a, id)) });
        candidate = try typed_edit.changed(a, candidate, .{ .parent = base.snapshot });
        const generation = try cloneGeneration(generation_arena.allocator(), a, candidate.data.snapshot);
        try pending.append(std.heap.page_allocator, .{
            .arena = arena,
            .generation_arena = generation_arena,
            .interner_checkpoint = checkpoint,
            .id = id,
            .base_id = try a.dupe(u8, model.utf8(interner.idText(.snapshot, base.snapshot))),
            .generation = generation,
            .receipt = "receipt",
            .full_review = try a.dupe(u8, name),
            .structured_review = "review",
            .name = name,
        });
        return id;
    }

    fn capacity() usize {
        var bytes: usize = (if (storage) |arena| arena.queryCapacity() else 0) + interner_heap.total_requested_bytes;
        if (current_payload) |arena| bytes += arena.queryCapacity();
        if (dependency_payload) |arena| if (current_payload != arena) {
            bytes += arena.queryCapacity();
        };
        for (pending.items) |p| {
            bytes += p.arena.queryCapacity();
            if (p.generation_arena) |arena| bytes += arena.queryCapacity();
        }
        return bytes;
    }
};

test "live-set replacement commits and rejection stay bounded and retain stale proposals" {
    var scratch = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer scratch.deinit();
    const a = scratch.allocator();
    const source_model = try model.parseValue(a, LifecycleTest.fixture);
    var source_interner: typed.Interner = .{ .gpa = a };
    const source_generation = try typed.load(a, &source_interner, source_model, null);
    try initializeSnapshot(&source_generation, &source_interner);
    defer deinit();
    const stale = try LifecycleTest.add(0);
    const stale_document = try document(a, find(stale).?);
    var max_capacity: usize = 0;
    var baseline_capacity: usize = 0;
    var selected_manifest: []const u8 = "";
    for (1..201) |index| {
        const id = try LifecycleTest.add(index);
        if (index == 1 or index == 101) {
            const p = &pending.items[pending.items.len - 1];
            selected_manifest = try p.arena.allocator().dupe(u8, p.name);
            p.dependency = .{ .selected = .{ .canonical = p.name, .id = p.id, .digest = p.name, .fingerprint = p.name, .subjects = &.{}, .provenance = .{ .members = &.{}, .hidden = &.{} } }, .manifest = selected_manifest, .request = "", .review_fingerprint = "" };
        }
        // Cancelled preparation models a persistence failure: no live pointer changes.
        try prepareInstall(id);
        const previous_snapshot = base.snapshot;
        cancelInstall();
        try std.testing.expectEqual(previous_snapshot, base.snapshot);
        try std.testing.expect(find(id) != null);
        try prepareInstall(id);
        install(id);
        try std.testing.expectEqual(@as(usize, 1), count());
        try std.testing.expectEqualStrings(if (index < 101) "Version-1" else "Version-101", selected_manifest);
        try std.testing.expectEqualStrings("Version-0", find(stale).?.full_review);
        try std.testing.expectEqualStrings(stale_document, try document(a, find(stale).?));
        try std.testing.expect(interner.ids[@backingInt(typed.Domain.snapshot)].texts.items.len <= 4);
        try std.testing.expect(interner.strings.texts.items.len <= 4);
        const retained_capacity = LifecycleTest.capacity();
        if (index == 10) baseline_capacity = retained_capacity;
        if (index > 10) try std.testing.expect(retained_capacity <= baseline_capacity * 2);
        max_capacity = @max(max_capacity, retained_capacity);
        const rejected = try LifecycleTest.add(1000 + index);
        try prepareReject(rejected);
        reject(rejected);
        try std.testing.expectEqual(@as(usize, 1), count());
        // Encoder rollback also releases its newly interned strings.
        _ = try LifecycleTest.add(2000 + index);
        discardAfter(1);
        model.useAllocator(a);
        try std.testing.expectEqual(@as(usize, 1), count());
        try std.testing.expect(interner.strings.texts.items.len <= 4);
    }
    std.debug.print("live-set retained bytes (arenas + interner requests): baseline={d}, max={d}\n", .{ baseline_capacity, max_capacity });
    try std.testing.expect(max_capacity < 1024 * 1024);
    try prepareReject(stale);
    reject(stale);
    try std.testing.expectEqual(@as(usize, 0), count());
    try std.testing.expectEqualStrings("Version-200", query.nameOf(currentModel(a).functions()[0]));
    try std.testing.expectEqual(@as(usize, 2), interner.ids[@backingInt(typed.Domain.snapshot)].texts.items.len);
}
