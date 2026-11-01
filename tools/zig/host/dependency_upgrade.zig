//! Exact dependency-selection planning; previews allocate no transaction or proposal IDs.
const std = @import("std");
const model = @import("model_reader");
const records = @import("snapshot_reader");
const host = @import("main.zig");
const query = @import("query.zig");
const m = @import("materialize.zig");
const edit = @import("operation_model.zig");
const recipe = @import("recipe.zig");
const Value = model.Value;
const Model = model.Model;
const tm = model.session_model;
const typed_edit = @import("typed_edit.zig");
const Json = std.json.Value;
const Allocator = std.mem.Allocator;
const Writer = std.Io.Writer;
pub const Pinned = struct { dependency: host.Dependency, manifest: Value, source: []const u8 };
pub const Plan = struct { dependency: host.Dependency, manifest: []const u8, local: Model, candidate: Model, preview: Value, fingerprint: []const u8 };
const Replacement = struct { before: Pinned, after: Pinned };

fn failure(r: *query.Request, message: []const u8) anyerror {
    r.rejection = .{ .code = "DependencyFailure", .message = message };
    return error.Rejected;
}
fn storageFailure(r: *query.Request) anyerror {
    const line = host.failure_line;
    const split = std.mem.indexOf(u8, line, ": ") orelse return failure(r, line);
    r.rejection = .{ .code = line[0..split], .message = line[split + 2 ..], .details = host.failure_details };
    return error.Rejected;
}
fn write(a: Allocator, value: Value) ![]const u8 {
    var out: Writer.Allocating = .init(a);
    try m.write(&out.writer, value);
    return out.written();
}
/// The same persisted-source checks as publication, including legacy source authority.
pub fn checkPersisted(r: *query.Request) !void {
    try r.ensureUsable();
    if (try host.verifyPersisted(r.a)) |problem| {
        r.rejection = problem;
        return error.Rejected;
    }
}
pub fn pin(r: *query.Request, source: []const u8) !Pinned {
    if (source.len == 0 or std.mem.indexOfScalar(u8, source, 0) != null) return failure(r, "Invalid dependency path.");
    const path = try std.fs.path.resolveAlloc(r.a, &.{ try std.process.currentPathAlloc(host.host_io, r.a), source });
    const stat = std.Io.Dir.cwd().statFile(host.host_io, path, .{}) catch null;
    if (m.same(path, r.snapshot_path) or stat == null or stat.?.kind != .file) return failure(r, "The selected dependency is unavailable.");
    const sidecar = try std.mem.concat(r.a, u8, &.{ path, ".dependency.json" });
    const sidecar_stat = std.Io.Dir.cwd().statFile(host.host_io, sidecar, .{}) catch null;
    const nested = if (sidecar_stat != null and sidecar_stat.?.kind == .file) host.loadSidecar(host.host_io, r.a, sidecar) catch return storageFailure(r) else null;
    const content = host.readText(r.a, path) catch |e| {
        r.rejection = .{ .code = "IoFailure", .message = @errorName(e) };
        return error.Rejected;
    };
    @import("ids.zig").observe(content);
    if (nested == null and host.bound(r.a, std.mem.endsWith(u8, path, ".nyrec"), content)) return failure(r, try std.fmt.allocPrint(r.a, "The dependency sidecar '{s}.dependency.json' is missing. Restore the exact sidecar that belongs to this snapshot; pinning a new version cannot recover a lost selection.", .{path}));
    const dependency = if (nested) |selected| blk: {
        const resolved = host.readSource(r.a, std.mem.endsWith(u8, path, ".nyrec"), content, selected) catch return storageFailure(r);
        if (resolved.open_obligations) return failure(r, "The selected dependency has open obligations; complete it before pinning.");
        break :blk host.dependencyFromRead(r.a, resolved, path, true) catch return storageFailure(r);
    } else host.decodeDependency(r.a, std.mem.endsWith(u8, path, ".nyrec"), content, path, true) catch return storageFailure(r);
    const json = dependency.canonical;
    const is_record = std.mem.endsWith(u8, path, ".nyrec");
    const manifest = try m.object(r.a, &.{
        try m.member(r.a, "fingerprint", try m.string(r.a, dependency.fingerprint)),
        try m.member(r.a, "path", try m.string(r.a, path)),
        try m.member(r.a, "snapshotId", try m.string(r.a, dependency.id)),
        try m.member(r.a, if (is_record) "snapshotRecords" else "snapshotJson", try m.string(r.a, if (is_record) try records.recordsOf(r.a, json) else json)),
    });
    return .{ .dependency = dependency, .manifest = manifest, .source = path };
}
fn members(r: *query.Request) ![]const Pinned {
    const text = host.dependency_manifest orelse return &.{};
    const root = try std.json.parseFromSliceLeaky(Json, r.a, text, .{});
    const set = root.object.get("dependencies");
    const items: []const Json = if (set) |value| value.array.items else &.{root};
    const result = try r.a.alloc(Pinned, items.len);
    for (items, result) |item, *slot| {
        const dependency = host.loadMember(host.host_io, r.a, item.object) catch return storageFailure(r);
        const encoded = try std.json.Stringify.valueAlloc(r.a, item, .{});
        slot.* = .{ .dependency = dependency, .manifest = try model.parseValue(r.a, encoded), .source = item.object.get("path").?.string };
    }
    return result;
}
pub fn combine(r: *query.Request, items: []const Pinned) !struct { host.Dependency, []const u8 } {
    if (items.len == 1) return .{ items[0].dependency, try write(r.a, items[0].manifest) };
    const dependencies = try r.a.alloc(host.Dependency, items.len);
    const manifests = try r.a.alloc(Value, items.len);
    for (items, dependencies, manifests) |item, *dependency, *manifest| {
        dependency.* = item.dependency;
        manifest.* = item.manifest;
    }
    const combined = host.combineMembers(r.a, dependencies) catch return storageFailure(r);
    return .{ combined, try write(r.a, try m.object(r.a, &.{try m.member(r.a, "dependencies", .{ .array = manifests })})) };
}
fn merged(r: *query.Request, selected: host.Dependency) !struct { Model, Model } {
    const saved = host.readSource(r.a, std.mem.endsWith(u8, r.snapshot_path, ".nyrec"), host.persisted_source, host.selection) catch return storageFailure(r);
    return mergeModel(r, model.typedRoot(r.a, saved.interner.?, saved.generation.?), selected);
}
fn remap(r: *query.Request, root: Model) !Model {
    if (root.context.interner == r.interner.?) return root;
    var compactor: tm.Compactor = .{ .arena = r.a, .scratch = r.a, .old_interner = root.context.interner, .interner = r.interner.? };
    const generation = try r.a.create(tm.Generation);
    generation.* = try compactor.generation(root.data.snapshot);
    return model.typedRoot(r.a, r.interner.?, generation);
}
pub fn mergeModel(r: *query.Request, source: Model, selected: host.Dependency) !struct { Model, Model } {
    const root = try remap(r, source);
    const imported: ?Model = if (host.selection) |old| old.typedModel(r.a) else null;
    const next = try remap(r, selected.typedModel(r.a));
    var local = root;
    var candidate = root;
    for ([_]typed_edit.Collection{ .types, .effects, .capabilities, .functions, .obligations }) |group| {
        var own: std.ArrayList(Model) = .empty;
        for (typed_edit.children(root, group)) |item| {
            if (imported == null or typed_edit.find(imported.?, group, query.idOf(item)) == null) try own.append(r.a, item);
        }
        local = try typed_edit.withChildren(r.a, local, group, own.items);
        candidate = try typed_edit.withChildren(r.a, candidate, group, try std.mem.concat(r.a, Model, &.{ typed_edit.children(next, group), own.items }));
    }
    return .{ local, candidate };
}
fn exported(item: Model) bool {
    return item.exported();
}
const Entry = struct { kind: []const u8, item: Model };
fn entries(a: Allocator, root: ?Model) ![]Entry {
    if (root == null) return &.{};
    var items: std.ArrayList(Entry) = .empty;
    for ([_][2][]const u8{ .{ "types", "type" }, .{ "effects", "effect" }, .{ "capabilities", "capability" }, .{ "functions", "function" } }) |group|
        for (root.?.declarations(group[0])) |item| try items.append(a, .{ .kind = group[1], .item = item });
    std.mem.sort(Entry, items.items, {}, struct {
        fn less(_: void, x: Entry, y: Entry) bool {
            if (exported(x.item) != exported(y.item)) return exported(x.item);
            if (!m.same(x.kind, y.kind)) return query.ordinal({}, x.kind, y.kind);
            return query.ordinal({}, query.idOf(x.item), query.idOf(y.item));
        }
    }.less);
    return items.items;
}
fn changes(r: *query.Request, before: ?Model, after: Model) !Value {
    var added: std.ArrayList(Value) = .empty;
    var removed: std.ArrayList(Value) = .empty;
    var changed: std.ArrayList(Value) = .empty;
    const old = try entries(r.a, before);
    const new = try entries(r.a, after);
    for (new) |entry| {
        const value = try m.object(r.a, &.{ try m.member(r.a, "kind", try m.string(r.a, entry.kind)), try m.member(r.a, "id", try m.string(r.a, query.idOf(entry.item))), try m.member(r.a, "exported", if (exported(entry.item)) .json_true else .json_false) });
        for (old) |previous| {
            if (m.same(entry.kind, previous.kind) and m.same(query.idOf(entry.item), query.idOf(previous.item))) {
                if (!try @import("proposals.zig").sameDeclaration(r.a, entry.item, previous.item)) try changed.append(r.a, value);
                break;
            }
        } else try added.append(r.a, value);
    }
    for (old) |entry| {
        for (new) |current| {
            if (m.same(entry.kind, current.kind) and m.same(query.idOf(entry.item), query.idOf(current.item))) break;
        } else try removed.append(r.a, try m.object(r.a, &.{ try m.member(r.a, "kind", try m.string(r.a, entry.kind)), try m.member(r.a, "id", try m.string(r.a, query.idOf(entry.item))), try m.member(r.a, "exported", if (exported(entry.item)) .json_true else .json_false) }));
    }
    return m.object(r.a, &.{ try m.member(r.a, "added", .{ .array = added.items }), try m.member(r.a, "removed", .{ .array = removed.items }), try m.member(r.a, "changed", .{ .array = changed.items }) });
}
fn issue(a: Allocator, code: []const u8, message: []const u8) !Value {
    return m.object(a, &.{ try m.member(a, "code", try m.string(a, code)), try m.member(a, "message", try m.string(a, message)) });
}
fn compatibility(r: *query.Request, selected: host.Dependency, candidate: Model) !Value {
    const read = model.validateTyped(r.a, candidate.context.interner, candidate.data.snapshot);
    const diagnostics = read.validation_issues;
    var issues: std.ArrayList(Value) = .empty;
    for (diagnostics) |diagnostic| try issues.append(r.a, try issue(r.a, diagnostic.code, model.utf8(diagnostic.message)));
    const imported = selected.typedModel(r.a);
    for ([_][2][]const u8{ .{ "types", "type" }, .{ "functions", "function" }, .{ "effects", "effect" }, .{ "capabilities", "capability" } }) |group| for (imported.declarations(group[0])) |item| {
        if (exported(item)) continue;
        const references = model.typedReferencesTo(r.a, candidate, group[1], item.id()) orelse continue;
        for (references) |reference| {
            const owner_group: []const u8 = switch (reference.owner) {
                .function => "functions",
                .type => "types",
                .capability => "capabilities",
            };
            if (typed_edit.find(imported, std.meta.stringToEnum(typed_edit.Collection, owner_group).?, model.utf8(reference.owner_id)) == null) {
                try issues.append(r.a, try issue(r.a, "ImportedDeclarationNotExported", try std.fmt.allocPrint(r.a, "The dependency does not export {s} '{s}' ({s}); a dependent may refer only to exported declarations.", .{ group[1], query.nameOf(item), query.idOf(item) })));
                break;
            }
        }
    };
    return .{ .array = issues.items };
}
fn description(a: Allocator, item: Pinned) !Value {
    return m.object(a, &.{ try m.member(a, "snapshotId", try m.string(a, item.dependency.id)), try m.member(a, "fingerprint", try m.string(a, item.dependency.fingerprint)) });
}
fn memberPreview(r: *query.Request, replacement: Replacement, issues: ?Value) !Value {
    var result = try changes(r, replacement.before.dependency.typedModel(r.a), replacement.after.dependency.typedModel(r.a));
    result = try edit.set(r.a, result, "replacedSnapshotId", try m.string(r.a, replacement.before.dependency.id));
    result = try edit.set(r.a, result, "replacedFingerprint", try m.string(r.a, replacement.before.dependency.fingerprint));
    result = try edit.set(r.a, result, "newSnapshotId", try m.string(r.a, replacement.after.dependency.id));
    result = try edit.set(r.a, result, "newFingerprint", try m.string(r.a, replacement.after.dependency.fingerprint));
    if (issues) |items| {
        result = try edit.set(r.a, result, "compatible", if (items.array.len == 0) .json_true else .json_false);
        result = try edit.set(r.a, result, "issues", items);
    }
    return result;
}
fn hash(a: Allocator, text: []const u8) ![]const u8 {
    var digest: [32]u8 = undefined;
    std.crypto.hash.sha2.Sha256.hash(text, &digest, .{});
    return std.fmt.allocPrint(a, "{x}", .{digest});
}
pub fn preview(r: *query.Request, out: *Writer) !void {
    var d: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line) };
    const single = m.same(r.root.get("op").?.string, "previewDependencyUpgrade");
    if (single) {
        try d.ensureFields("", .{ .object = r.root }, "previewDependencyUpgrade request", &.{ "id", "op", "source", "replaces" }, &.{ "id", "op", "source" });
        const replaces = if (r.root.get("replaces") != null) try d.requiredString(.{ .object = r.root }, "replaces") else null;
        const source = try d.requiredString(.{ .object = r.root }, "source");
        try checkPersisted(r);
        if (host.selection == null) return failure(r, "This workspace has no dependency to upgrade.");
        const current = try members(r);
        if (replaces == null and current.len != 1) return failure(r, "Name the dependency to replace by its SnapshotId when the workspace has several.");
        const target = for (current) |item| {
            if (replaces == null or m.same(item.dependency.id, replaces.?)) break item;
        } else return failure(r, try std.fmt.allocPrint(r.a, "No selected dependency has SnapshotId '{s}'.", .{replaces.?}));
        const replacement = try pin(r, source);
        if (m.same(replacement.dependency.fingerprint, target.dependency.fingerprint)) return failure(r, "The source is the version already selected; use pin to retain or relocate it.");
        const upgraded = try r.a.dupe(Pinned, current);
        for (upgraded) |*item| if (m.same(item.dependency.id, target.dependency.id)) {
            item.* = replacement;
            break;
        };
        const selected = (try combine(r, upgraded))[0];
        const candidate = (try merged(r, selected))[1];
        const issues = try compatibility(r, selected, candidate);
        const result = try memberPreview(r, .{ .before = target, .after = replacement }, issues);
        // Public single-preview order differs from the nested member object.
        try out.writeAll("{");
        for ([_][]const u8{ "replacedSnapshotId", "replacedFingerprint", "newSnapshotId", "newFingerprint", "compatible", "added", "removed", "changed", "issues" }, 0..) |name, i| {
            if (i > 0) try out.writeByte(',');
            try host.writeString(out, name);
            try out.writeByte(':');
            try m.write(out, model.property(result, name).?);
        }
        return out.writeByte('}');
    }
    try d.ensureFields("", .{ .object = r.root }, "previewDependencyUpgrades request", &.{ "id", "op", "upgrades", "selection" }, &.{ "id", "op" });
    const plan = try planChange(r);
    try m.write(out, plan.preview);
}

pub fn planChange(r: *query.Request) !Plan {
    var d: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line) };
    const simulation = std.mem.startsWith(u8, r.root.get("op").?.string, "simulate");
    const has_upgrades = r.root.get("upgrades") != null;
    const has_selection = r.root.get("selection") != null;
    if (has_upgrades == has_selection) return r.fail("Name exactly one of upgrades or selection.", .{});
    var requests: std.ArrayList(struct { source: []const u8, replaces: []const u8 }) = .empty;
    var sources: std.ArrayList([]const u8) = .empty;
    var replaces: std.ArrayList([]const u8) = .empty;
    if (has_upgrades) {
        for (try d.array(.{ .object = r.root }, "upgrades"), 0..) |item, index| {
            if (item != .object) return r.fail("Expected a JSON object.", .{});
            try d.ensureFields(try std.fmt.allocPrint(r.a, "/upgrades/{d}", .{index}), item, "dependency replacement", &.{ "source", "replaces" }, &.{ "source", "replaces" });
            try requests.append(r.a, .{ .source = try d.requiredString(item, "source"), .replaces = try d.requiredString(item, "replaces") });
        }
    } else {
        const selected = r.root.get("selection").?;
        if (selected != .object) return r.fail("Expected a JSON object.", .{});
        try d.ensureFields("/selection", selected, if (simulation) "dependency selection" else "dependency selection upgrade", &.{ "sources", "replaces" }, &.{ "sources", "replaces" });
        for ([_][]const u8{ "sources", "replaces" }) |name| for (try d.array(selected, name)) |item| {
            if (item != .string) {
                if (simulation) return r.fail("Selection entries must be strings.", .{});
                return r.fail("{s} entries must be strings.", .{name});
            }
            try (if (m.same(name, "sources")) &sources else &replaces).append(r.a, try query.readable(r, item.string));
        };
    }
    try checkPersisted(r);
    if (has_upgrades and host.selection == null) return failure(r, if (simulation) "This workspace has no dependency to upgrade; add one with a selection whose replaces is empty." else "This workspace has no dependency to upgrade.");
    const current = try members(r);
    var changed: std.ArrayList(Replacement) = .empty;
    var selected: std.ArrayList(Pinned) = .empty;
    var replaced: std.ArrayList(Pinned) = .empty;
    var final: std.ArrayList(Pinned) = .empty;
    if (has_upgrades) {
        if (requests.items.len == 0) return failure(r, "An upgrade batch needs at least one replacement.");
        for (requests.items) |request| if (try recipe.isBlank(r.a, request.source) or try recipe.isBlank(r.a, request.replaces)) return failure(r, "Every replacement needs a non-blank source and the current SnapshotId to replace.");
        for (requests.items, 0..) |request, i| for (requests.items[0..i]) |before| if (m.same(request.replaces, before.replaces)) return failure(r, "Each current dependency may be replaced only once.");
        for (requests.items) |request| {
            const target = for (current) |item| {
                if (m.same(item.dependency.id, request.replaces)) break item;
            } else return failure(r, try std.fmt.allocPrint(r.a, "No selected dependency has SnapshotId '{s}'.", .{request.replaces}));
            const replacement = try pin(r, request.source);
            if (m.same(target.dependency.fingerprint, replacement.dependency.fingerprint)) return failure(r, "The source is the version already selected; use pin to retain or relocate it.");
            try changed.append(r.a, .{ .before = target, .after = replacement });
            try replaced.append(r.a, target);
            try selected.append(r.a, replacement);
        }
        for (current) |item| {
            const next = for (changed.items) |replacement| {
                if (m.same(item.dependency.id, replacement.before.dependency.id)) break replacement.after;
            } else item;
            try final.append(r.a, next);
        }
        for (final.items, 0..) |item, i| for (final.items[0..i]) |before| if (m.same(item.dependency.id, before.dependency.id)) return failure(r, "The final selection must have distinct dependency SnapshotIds.");
    } else {
        if (sources.items.len == 0) return failure(r, "A selection change needs at least one source.");
        for (sources.items) |source| if (try recipe.isBlank(r.a, source)) return failure(r, "Selection upgrade sources and targets must be non-blank.");
        for (replaces.items) |target| if (try recipe.isBlank(r.a, target)) return failure(r, "Selection upgrade sources and targets must be non-blank.");
        for (replaces.items, 0..) |target, i| for (replaces.items[0..i]) |previous| if (m.same(target, previous)) return failure(r, "Each current dependency may be replaced only once.");
        for (replaces.items) |target| {
            for (current) |item| {
                if (m.same(target, item.dependency.id)) break;
            } else return failure(r, "Every replacement target must be a currently selected dependency SnapshotId.");
        }
        for (sources.items) |source| try selected.append(r.a, try pin(r, source));
        std.mem.sort(Pinned, selected.items, {}, struct {
            fn less(_: void, x: Pinned, y: Pinned) bool {
                return query.ordinal({}, x.source, y.source);
            }
        }.less);
        for (current) |item| {
            for (replaces.items) |target| {
                if (m.same(target, item.dependency.id)) {
                    try replaced.append(r.a, item);
                    break;
                }
            } else {
                try final.append(r.a, item);
            }
        }
        try final.appendSlice(r.a, selected.items);
        for (final.items, 0..) |item, i| for (final.items[0..i]) |before| if (m.same(item.source, before.source) or m.same(item.dependency.id, before.dependency.id)) return failure(r, "Each final dependency source and SnapshotId must be selected only once.");
        var same = current.len == final.items.len;
        if (same) for (current) |item| {
            for (final.items) |next| {
                if (m.same(item.dependency.id, next.dependency.id) and m.same(item.dependency.fingerprint, next.dependency.fingerprint)) break;
            } else {
                same = false;
                break;
            }
        };
        if (same) return failure(r, "The final selection is already pinned; use pin to retain or relocate it.");
    }
    const combined = try combine(r, final.items);
    const graphs = try merged(r, combined[0]);
    const issues = try compatibility(r, combined[0], graphs[1]);
    var member_previews: std.ArrayList(Value) = .empty;
    for (changed.items) |replacement| {
        const item = try memberPreview(r, replacement, null);
        var ordered: std.ArrayList(model.Member) = .empty;
        for ([_][]const u8{ "replacedSnapshotId", "replacedFingerprint", "newSnapshotId", "newFingerprint", "added", "removed", "changed" }) |name| try ordered.append(r.a, try m.member(r.a, name, model.property(item, name).?));
        try member_previews.append(r.a, .{ .object = ordered.items });
    }
    var review_fields: std.ArrayList(model.Member) = .empty;
    try review_fields.append(r.a, try m.member(r.a, "localSource", try m.string(r.a, host.persisted_source)));
    try review_fields.append(r.a, try m.member(r.a, "manifest", try m.string(r.a, host.dependency_manifest orelse "")));
    try review_fields.append(r.a, try m.member(r.a, "path", try m.string(r.a, r.snapshot_path)));
    if (has_upgrades) {
        std.mem.sort(Replacement, changed.items, {}, struct {
            fn less(_: void, x: Replacement, y: Replacement) bool {
                return query.ordinal({}, x.before.dependency.id, y.before.dependency.id);
            }
        }.less);
        var upgrades: std.ArrayList(Value) = .empty;
        for (changed.items) |replacement| try upgrades.append(r.a, try m.object(r.a, &.{ try m.member(r.a, "manifest", try m.string(r.a, try write(r.a, replacement.after.manifest))), try m.member(r.a, "replaces", try m.string(r.a, replacement.before.dependency.id)) }));
        try review_fields.append(r.a, try m.member(r.a, "upgrades", .{ .array = upgrades.items }));
    } else {
        std.mem.sort([]const u8, replaces.items, {}, query.ordinal);
        var targets: std.ArrayList(Value) = .empty;
        for (replaces.items) |target| try targets.append(r.a, try m.string(r.a, target));
        var manifests: std.ArrayList(Value) = .empty;
        for (selected.items) |item| try manifests.append(r.a, try m.string(r.a, try write(r.a, item.manifest)));
        try review_fields.append(r.a, try m.member(r.a, "replaces", .{ .array = targets.items }));
        try review_fields.append(r.a, try m.member(r.a, "selected", .{ .array = manifests.items }));
    }
    try review_fields.append(r.a, try m.member(r.a, "version", .{ .number = if (has_upgrades) "1" else "2" }));
    const fingerprint = try hash(r.a, try write(r.a, .{ .object = review_fields.items }));
    const aggregate = try changes(r, if (host.selection) |old| old.typedModel(r.a) else null, combined[0].typedModel(r.a));
    var replaced_values: std.ArrayList(Value) = .empty;
    for (replaced.items) |item| try replaced_values.append(r.a, try description(r.a, item));
    var selected_values: std.ArrayList(Value) = .empty;
    for (selected.items) |item| try selected_values.append(r.a, try description(r.a, item));
    const result = try m.object(r.a, &.{
        try m.member(r.a, "reviewFingerprint", try m.string(r.a, fingerprint)),                  try m.member(r.a, "upgrades", .{ .array = member_previews.items }),
        try m.member(r.a, "replaced", .{ .array = replaced_values.items }),                      try m.member(r.a, "selected", .{ .array = selected_values.items }),
        try m.member(r.a, "compatible", if (issues.array.len == 0) .json_true else .json_false), try m.member(r.a, "added", model.property(aggregate, "added").?),
        try m.member(r.a, "removed", model.property(aggregate, "removed").?),                    try m.member(r.a, "changed", model.property(aggregate, "changed").?),
        try m.member(r.a, "issues", issues),
    });
    return .{ .dependency = combined[0], .manifest = combined[1], .local = graphs[0], .candidate = graphs[1], .preview = result, .fingerprint = fingerprint };
}
