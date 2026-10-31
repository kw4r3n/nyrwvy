//! Product-field and capability signature evolution reuse the ordinary replacement pipeline.
const std = @import("std");
const model = @import("model_reader");
const query = @import("query.zig");
const recipe = @import("recipe.zig");
const preflight = @import("preflight.zig");
const infer = @import("infer.zig");
const m = @import("materialize.zig");
const edit = @import("operation_model.zig");
const migration = @import("migration.zig");
const proposals = @import("proposals.zig");
const wire = @import("wire.zig");
const Value = model.Value;
const Json = std.json.Value;
const TypeRef = recipe.TypeRef;
const Rewrite = struct { id: []const u8, body: *const recipe.Node };
const Parameter = struct { id: []const u8, type: TypeRef };
const Repair = struct { rewrite: Rewrite, parameters: []const Parameter, result: TypeRef };

fn reject(r: *query.Request, prefix: []const u8, suffix: []const u8, message: []const u8) anyerror {
    return preflight.rejected(r, try std.mem.concat(r.a, u8, &.{ prefix, suffix }), message);
}
fn fresh(r: *query.Request) !Value {
    const next_id = try @import("ids.zig").next();
    return m.string(r.a, &next_id);
}
fn owner(d: *recipe.Decoder, f: Value) !void {
    const names = try d.r.a.alloc([]const u8, model.arrayItems(f, "parameters").len);
    for (model.arrayItems(f, "parameters"), names) |p, *n| n.* = query.nameOf(p);
    d.owner = .{ .name = query.nameOf(f), .parameters = names, .existing = f };
}
fn body(d: *recipe.Decoder, path: []const u8, value: Json, raw: []const u8) !*const recipe.Node {
    var resolved = value;
    try recipe.resolveNames(d, path, &resolved, raw);
    const tree = try d.recipe(path, resolved);
    return if (d.owner != null) recipe.bindExistingTypes(d, tree) else tree;
}
fn at(r: *query.Request, path: []const u8, key: []const u8) ![]const u8 {
    return std.fmt.allocPrint(r.a, "{s}/{s}", .{ path, key });
}
fn expected(d: *recipe.Decoder, root: Json) ![]const u8 {
    return if (root.object.get("expectedSnapshotId") != null) d.stableId("expectedSnapshotId", try d.requiredString(root, "expectedSnapshotId")) else d.r.snapshot_id;
}
fn id(d: *recipe.Decoder, item: Json, key: []const u8) ![]const u8 {
    return d.stableId(key, try d.requiredString(item, key));
}
fn parameters(d: *recipe.Decoder, item: Json, path: []const u8, f: Value, symbolic: bool, capability_: bool) ![]const Parameter {
    const entries = try d.array(item, "parameterTypes");
    const result = try d.r.a.alloc(Parameter, entries.len);
    for (entries, result, 0..) |entry, *p, i| {
        const key = if (symbolic) "parameter" else "parameterId";
        const ppath = try std.fmt.allocPrint(d.r.a, "{s}/parameterTypes/{d}", .{ path, i });
        try d.ensureFields(ppath, entry, if (capability_) "operation parameter type" else "function parameter type", &.{ key, "type" }, &.{ key, "type" });
        const pid = if (symbolic) query.idOf(if (capability_) try d.member("parameter on operation {s}", .{query.nameOf(f)}, f, "parameters", try d.requiredString(entry, key)) else try d.member("parameter on {s}", .{query.nameOf(f)}, f, "parameters", try d.requiredString(entry, key))) else try id(d, entry, key);
        var t = try d.anyType(try at(d.r, ppath, "type"), try d.property(entry, "type"));
        if (symbolic and !capability_) t = try recipe.bindType(d, t);
        p.* = .{ .id = pid, .type = t };
    }
    return result;
}

pub fn simulateRequest(r: *query.Request, out: *std.Io.Writer) anyerror!void {
    const op = r.root.get("op").?.string;
    const symbolic = std.mem.endsWith(u8, op, "ByName");
    const field = std.mem.indexOf(u8, op, "ProductField") != null;
    const allowed: []const []const u8 = if (field) (if (symbolic) &.{ "id", "op", "type", "field", "newFieldType", "functionRewrites", "structuredReview", "detail", "expectedSnapshotId" } else &.{ "id", "op", "fieldId", "newFieldType", "functionRewrites", "structuredReview", "detail", "expectedSnapshotId" }) else (if (symbolic) &.{ "id", "op", "capability", "operation", "parameterTypes", "resultType", "functionRepairs", "structuredReview", "detail", "expectedSnapshotId" } else &.{ "id", "op", "operationId", "parameterTypes", "resultType", "functionRepairs", "structuredReview", "detail", "expectedSnapshotId" });
    // Resolve declaration type names before each repair installs its existing-function context.
    var d: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line), .owner = if (symbolic) .{ .name = "" } else null };
    const root: Json = .{ .object = r.root };
    const context = if (field) (if (symbolic) "field type migration by name" else "simulateProductFieldTypeMigration request") else (if (symbolic) "operation signature migration by name" else "operation signature migration");
    try d.ensureFields("", root, context, allowed, allowed[0 .. allowed.len - 3]);
    // Exact requests evaluate expectedSnapshotId first; symbolic selectors run before it.
    var wanted: []const u8 = r.snapshot_id;
    if (!symbolic) wanted = try expected(&d, root);
    var selected: Value = .json_null;
    const target_id = if (symbolic) blk: {
        const group = if (field) "type" else "capability";
        const container = try d.selector(try at(r, "", group), try d.property(root, group), group);
        if (field and model.property(container, "fields") == null) return r.fail("Type '{s}' is not a product type.", .{query.nameOf(container)});
        const key = if (field) "field" else "operation";
        selected = if (field) try d.member("field on {s}", .{query.nameOf(container)}, container, "fields", try d.requiredString(root, key)) else try d.member("operation on {s}", .{query.nameOf(container)}, container, "operations", try d.requiredString(root, key));
        break :blk query.idOf(selected);
    } else try id(&d, root, if (field) "fieldId" else "operationId");
    if (symbolic) wanted = try expected(&d, root);
    const operation_parameters = if (!field) try parameters(&d, root, "", selected, symbolic, true) else &.{};
    const new_type = try d.anyType(if (field) "/newFieldType" else "/resultType", try d.property(root, if (field) "newFieldType" else "resultType"));
    if (field) {
        const entries = try d.array(root, "functionRewrites");
        const raw = try @import("recipe_file.zig").rawArrayItems(r.a, try recipe.rawProperty(r.a, r.line, "functionRewrites"));
        const rewrites = try r.a.alloc(Rewrite, entries.len);
        for (entries, rewrites, 0..) |entry, *rewrite, i| {
            const key = if (symbolic) "function" else "functionId";
            const path = try std.fmt.allocPrint(r.a, "/functionRewrites/{d}", .{i});
            try d.ensureFields(path, entry, if (symbolic) "function rewrite by name" else "function rewrite", &.{ key, "replacement" }, &.{ key, "replacement" });
            const fid = if (symbolic) blk: {
                const f = try d.selector(try at(r, path, key), try d.property(entry, key), "function");
                try owner(&d, f);
                break :blk query.idOf(f);
            } else try id(&d, entry, key);
            rewrite.* = .{ .id = fid, .body = try body(&d, try at(r, path, "replacement"), try d.property(entry, "replacement"), try recipe.rawProperty(r.a, raw[i], "replacement")) };
        }
        try r.ensureUsable();
        try proposals.publishMigration(r, out, try product(r, wanted, target_id, new_type, rewrites));
    } else {
        const entries = try d.array(root, "functionRepairs");
        const raw = try @import("recipe_file.zig").rawArrayItems(r.a, try recipe.rawProperty(r.a, r.line, "functionRepairs"));
        const repairs = try r.a.alloc(Repair, entries.len);
        for (entries, repairs, 0..) |entry, *repair, i| {
            const key = if (symbolic) "function" else "functionId";
            const path = try std.fmt.allocPrint(r.a, "/functionRepairs/{d}", .{i});
            try d.ensureFields(path, entry, "function repair", &.{ key, "parameterTypes", "resultType", "replacement" }, &.{ key, "parameterTypes", "resultType", "replacement" });
            var f: Value = .json_null;
            const fid = if (symbolic) blk: {
                f = try d.selector(try at(r, path, key), try d.property(entry, key), "function");
                try owner(&d, f);
                break :blk query.idOf(f);
            } else try id(&d, entry, key);
            const ps = try parameters(&d, entry, path, f, symbolic, false);
            var result = try d.anyType(try at(r, path, "resultType"), try d.property(entry, "resultType"));
            if (symbolic) result = try recipe.bindType(&d, result);
            repair.* = .{ .rewrite = .{ .id = fid, .body = try body(&d, try at(r, path, "replacement"), try d.property(entry, "replacement"), try recipe.rawProperty(r.a, raw[i], "replacement")) }, .parameters = ps, .result = result };
        }
        try r.ensureUsable();
        try proposals.publishMigration(r, out, try capability(r, wanted, target_id, operation_parameters, new_type, repairs));
    }
}

fn typeValue(r: *query.Request, t: TypeRef) !Value {
    var materializer: m.Materializer = .{ .a = r.a, .r = r, .types = undefined, .fresh = &.{}, .function_id = .json_null, .parameters = &.{} };
    return materializer.typeRef(t);
}
fn unchanged(r: *query.Request, old: Value, new: TypeRef) !bool {
    return infer.eqlType(try infer.typeOf(r.a, old), new);
}
fn declared(r: *query.Request, prefix: []const u8, context: []const u8, owner_type: ?Value, t: TypeRef) anyerror!void {
    const parameters_ = if (owner_type) |o| model.arrayItems(o, "typeParameters") else &.{};
    const names = try r.a.alloc([]const u8, parameters_.len);
    for (parameters_, names) |p, *name| name.* = edit.text(p, "typeParameterId");
    const validator: @import("declarations.zig").Validation = .{ .r = r, .subject = "", .prefix = prefix, .parameters = names, .structural = true };
    try validator.typeRef(context, t);
}
fn changedParameters(r: *query.Request, f: Value, types: []const Parameter) !Value {
    const ps = try r.a.dupe(Value, model.arrayItems(f, "parameters"));
    for (ps) |*p| for (types) |t| if (m.same(query.idOf(p.*), t.id)) { p.* = try edit.set(r.a, p.*, "type", try typeValue(r, t.type)); break; };
    return edit.set(r.a, f, "parameters", .{ .array = ps });
}
fn exactParameters(f: Value, types: []const Parameter) bool {
    const ps = model.arrayItems(f, "parameters");
    if (ps.len != types.len) return false;
    for (types, 0..) |t, i| {
        var found = false;
        for (ps) |p| if (m.same(query.idOf(p), t.id)) { found = true; break; };
        if (!found) return false;
        for (types[0..i]) |prior| if (m.same(prior.id, t.id)) return false;
    }
    return true;
}
fn containsValue(values: []const Value, wanted: []const u8) bool {
    for (values) |v| if (m.same(query.idOf(v), wanted)) return true;
    return false;
}
fn requested(rewrites: []const Rewrite, wanted: []const u8) ?*const recipe.Node {
    for (rewrites) |rewrite| if (m.same(rewrite.id, wanted)) return rewrite.body;
    return null;
}
fn productAffected(r: *query.Request, type_id: []const u8, field_id: []const u8) ![]const Value {
    var functions: std.ArrayList(Value) = .empty;
    for (model.arrayItems(r.model, "functions")) |f| for (model.bodyNodes(r.a, f)) |n| {
        const kind = edit.text(n, "kind");
        var affected = (m.same(kind, "productConstruct") and m.same(edit.text(n, "typeId"), type_id)) or (m.same(kind, "fieldAccess") and m.same(edit.text(n, "fieldId"), field_id));
        if (m.same(kind, "productUpdate")) for (model.arrayItems(n, "fields")) |binding| { if (m.same(edit.text(binding, "targetFieldId"), field_id)) affected = true; };
        if (affected) { try functions.append(r.a, f); break; }
    };
    return functions.items;
}
fn rewriteAll(r: *query.Request, candidate: Value, affected: []const Value, rewrites: []const Rewrite, prefix: []const u8, product_: bool) !Value {
    var next = r.*;
    next.model = try model.ownTyped(r.a, candidate, false);
    var result = candidate;
    var failure: ?query.Rejection = null;
    var diagnostic: query.Diagnostic = .{};
    for (affected) |f| {
        const current = edit.find(candidate, "functions", query.idOf(f)).?;
        const updated = migration.materializeReplacement(&next, current, requested(rewrites, query.idOf(f)).?) catch |err| {
            if (err != error.Rejected) return err;
            if (failure == null) {
                const detail = next.diagnostic;
                try tryFailure(&next, prefix, if (product_) "RewriteInvalid" else "RepairInvalid", if (product_) try std.fmt.allocPrint(r.a, "Function '{s}' rewrite is invalid: {s}: {s}", .{query.idOf(f), detail.code, detail.message}) else try std.fmt.allocPrint(r.a, "{s}: {s}", .{detail.code, detail.message}));
                failure = next.rejection;
                diagnostic = next.diagnostic;
            }
            continue;
        };
        result = try edit.replace(r.a, result, "functions", updated);
    }
    if (failure) |f| { r.rejection = f; r.diagnostic = diagnostic; return error.Rejected; }
    return result;
}
fn tryFailure(r: *query.Request, prefix: []const u8, suffix: []const u8, message: []const u8) !void {
    const err = reject(r, prefix, suffix, message);
    if (err != error.Rejected) return err;
}
fn finish(r: *query.Request, kind: []const u8, candidate: Value, before: []const Value, diff: Value, summary: []const u8) !migration.Result {
    const transaction = try fresh(r);
    const snapshot = try fresh(r);
    const final = try edit.set(r.a, try edit.set(r.a, candidate, "snapshotId", snapshot), "parentSnapshotId", try m.string(r.a, r.snapshot_id));
    try migration.validate(r, final);
    const after = try r.a.alloc(Value, before.len);
    for (before, after) |f, *next| next.* = edit.find(final, "functions", query.idOf(f)).?;
    return .{ .kind = kind, .transaction = transaction, .snapshot = snapshot, .candidate = final, .diff = diff, .rewritten = after, .summary = summary };
}
fn bodyDiffs(r: *query.Request, candidate: Value, affected: []const Value) !Value {
    const result = try r.a.alloc(Value, affected.len);
    for (affected, result) |f, *diff| diff.* = try migration.bodyDiff(r, candidate, f, edit.find(candidate, "functions", query.idOf(f)).?);
    return .{ .array = result };
}
fn wireType(r: *query.Request, value: Value) !Value {
    var w: std.Io.Writer.Allocating = .init(r.a);
    try wire.typeRef(&w.writer, value, &.{});
    return model.parseValue(r.a, w.written());
}
fn product(r: *query.Request, wanted: []const u8, fid: []const u8, new_type: TypeRef, rewrites: []const Rewrite) anyerror!migration.Result {
    const prefix = "FieldTypeMigration";
    if (!m.same(wanted, r.snapshot_id)) return reject(r, prefix, "ExpectedBaseMismatch", "The migration request targets a different base snapshot.");
    var owner_type: ?Value = null;
    var field: Value = undefined;
    for (model.arrayItems(r.model, "types")) |t| for (model.arrayItems(t, "fields")) |f| if (m.same(query.idOf(f), fid)) { owner_type = t; field = f; };
    if (owner_type == null) return reject(r, prefix, "FieldNotFound", "The field is not a field of an existing Product.");
    if (try unchanged(r, model.property(field, "type").?, new_type)) return reject(r, prefix, "TypeUnchanged", "The requested field type is already the current type.");
    try declared(r, prefix, "The new field type", owner_type, new_type);
    const affected = try productAffected(r, query.idOf(owner_type.?), fid);
    for (rewrites, 0..) |rewrite, i| for (rewrites[0..i]) |prior| if (m.same(rewrite.id, prior.id)) return reject(r, prefix, "DuplicateRewrite", "Each affected function may have only one replacement body.");
    var missing: std.ArrayList(Value) = .empty;
    var unrelated: std.ArrayList([]const u8) = .empty;
    for (affected) |f| if (requested(rewrites, query.idOf(f)) == null) try missing.append(r.a, f);
    for (rewrites) |rewrite| if (!containsValue(affected, rewrite.id)) try unrelated.append(r.a, rewrite.id);
    if (missing.items.len > 0 or unrelated.items.len > 0) return fieldMismatch(r, missing.items, unrelated.items);
    for (affected) |f| if (migration.bodyObligation(r, f)) |obligation| return reject(r, prefix, "BodyReferencedByObligation", try std.fmt.allocPrint(r.a, "Function '{s}': StructuralBodyReferencedByObligation: Obligation '{s}' targets the body being replaced.", .{query.idOf(f), obligation}));
    var updated_type = owner_type.?;
    const fields = try r.a.dupe(Value, model.arrayItems(updated_type, "fields"));
    for (fields) |*f| { if (m.same(query.idOf(f.*), fid)) f.* = try edit.set(r.a, f.*, "type", try typeValue(r, new_type)); }
    updated_type = try edit.set(r.a, updated_type, "fields", .{ .array = fields });
    var candidate = try edit.replace(r.a, r.model, "types", updated_type);
    candidate = try rewriteAll(r, candidate, affected, rewrites, prefix, true);
    var result = try finish(r, "productFieldTypeMigration", candidate, affected, .json_null, try std.fmt.allocPrint(r.a, "productFieldTypeMigration of '{s}' field '{s}'", .{ query.nameOf(updated_type), query.nameOf(field) }));
    const change = try m.object(r.a, &.{ try m.member(r.a, "typeId", model.property(updated_type, "id").?), try m.member(r.a, "fieldId", model.property(field, "id").?), try m.member(r.a, "oldType", try wireType(r, model.property(field, "type").?)), try m.member(r.a, "newType", try wireType(r, try typeValue(r, new_type))) });
    result.diff = try m.object(r.a, &.{ try m.member(r.a, "fieldChange", change), try m.member(r.a, "bodyRewrites", try bodyDiffs(r, result.candidate, affected)) });
    result.touched_types = try r.a.dupe([]const u8, &.{query.idOf(updated_type)});
    return result;
}

fn orderedIds(r: *query.Request, values: []const []const u8) ![]const []const u8 {
    const result = try r.a.dupe([]const u8, values);
    std.mem.sort([]const u8, result, {}, query.ordinal);
    return result;
}
fn nameCut(value: Value) []const u16 {
    const text = model.decoded(value);
    var n = @min(128, text.len);
    if (n > 0 and n < text.len and text[n - 1] >= 0xd800 and text[n - 1] <= 0xdbff) n -= 1;
    return text[0..n];
}
fn mismatchNames(r: *query.Request, ids_: []const []const u8) ![]const u8 {
    var text: std.Io.Writer.Allocating = .init(r.a);
    const ids_sorted = try orderedIds(r, ids_);
    for (ids_sorted[0..@min(5, ids_sorted.len)], 0..) |fid, i| {
        if (i > 0) try text.writer.writeAll(", ");
        if (edit.find(r.model, "functions", fid)) |f| {
            const name = model.property(f, "displayName").?;
            const cut = nameCut(name);
            try text.writer.print("'{s}{s}' ({s})", .{ model.utf8(cut), if (cut.len < model.decoded(name).len) @as([]const u8, "…") else "", fid });
        } else try text.writer.writeAll(fid);
    }
    if (ids_sorted.len > 5) try text.writer.print("; +{d} more (inspect declarations)", .{ids_sorted.len - 5});
    return text.written();
}
fn mismatchReferences(r: *query.Request, out: *std.Io.Writer, ids_: []const []const u8) !void {
    const functions = try query.visible(r, "function");
    const ids_sorted = try orderedIds(r, ids_);
    try out.writeAll("{\"items\":[");
    for (ids_sorted[0..@min(5, ids_sorted.len)], 0..) |fid, i| {
        if (i > 0) try out.writeByte(',');
        const f = for (functions) |function| {
            if (edit.hasId(function, fid)) break function;
        } else null;
        const sources = if (f != null) try query.sources(r, "function", fid) else &.{};
        const name = if (f) |function| model.property(function, "displayName").? else try m.string(r.a, "<unknown>");
        const cut = nameCut(name);
        try out.writeAll("{\"id\":"); try model.writeString(out, (try m.string(r.a, fid)).string);
        try out.writeAll(",\"displayName\":"); try model.writeString(out, cut);
        try out.print(",\"displayNameTruncated\":{s},\"ownership\":", .{if (cut.len < model.decoded(name).len) @as([]const u8, "true") else "false"});
        try model.writeString(out, (try m.string(r.a, if (f == null) "unknown" else if (sources.len == 0) "local" else "imported")).string);
        try out.writeAll(",\"sources\":[");
        for (sources[0..@min(5, sources.len)], 0..) |source, j| {
            if (j > 0) try out.writeByte(',');
            try out.writeAll("{\"snapshotId\":"); try model.writeString(out, (try m.string(r.a, source.id)).string);
            try out.writeAll(",\"fingerprint\":"); try model.writeString(out, (try m.string(r.a, source.fingerprint)).string);
            try out.writeByte('}');
        }
        try out.print("],\"sourceCount\":{d},\"sourcesTruncated\":{s}}}", .{ sources.len, if (sources.len > 5) @as([]const u8, "true") else "false" });
    }
    try out.print("],\"totalCount\":{d},\"truncated\":{s}}}", .{ ids_sorted.len, if (ids_sorted.len > 5) @as([]const u8, "true") else "false" });
}
fn fieldMismatch(r: *query.Request, missing: []const Value, unrelated: []const []const u8) anyerror {
    const missing_ids = try r.a.alloc([]const u8, missing.len);
    for (missing, missing_ids) |f, *fid| fid.* = query.idOf(f);
    const message = try std.fmt.allocPrint(r.a, "Rewrites must cover exactly the functions that construct the Product or read the field. Missing: [{s}]. Unrelated: [{s}].", .{ try mismatchNames(r, missing_ids), try mismatchNames(r, unrelated) });
    const failure = reject(r, "FieldTypeMigration", "RewriteMismatch", message);
    if (failure != error.Rejected) return failure;
    var details: std.Io.Writer.Allocating = .init(r.a);
    try details.writer.writeAll(r.rejection.details[0 .. r.rejection.details.len - 1]);
    try details.writer.writeAll(",\"repair\":{\"missing\":");
    try mismatchReferences(r, &details.writer, missing_ids);
    try details.writer.writeAll(",\"unrelated\":");
    try mismatchReferences(r, &details.writer, unrelated);
    try details.writer.writeAll(",\"inspectOperation\":\"declarations\"}}");
    r.rejection.details = details.written();
    return error.Rejected;
}
fn signatureChanged(r: *query.Request, before: Value, after: Value) !bool {
    if (!try unchanged(r, model.property(before, "resultType").?, try infer.typeOf(r.a, model.property(after, "resultType").?))) return true;
    for (model.arrayItems(before, "parameters"), model.arrayItems(after, "parameters")) |old, new| if (!try unchanged(r, model.property(old, "type").?, try infer.typeOf(r.a, model.property(new, "type").?))) return true;
    return false;
}
fn idList(r: *query.Request, values: []const []const u8) ![]const u8 {
    return std.mem.join(r.a, ", ", try orderedIds(r, values));
}
fn operationWire(r: *query.Request, value: Value) !Value {
    var text: std.Io.Writer.Allocating = .init(r.a);
    try wire.capabilityOperation(&text.writer, value);
    var result = try model.parseValue(r.a, text.written());
    for ([_][]const u8{ "failureType", "effectId", "mayWait" }) |key| result = try edit.remove(r.a, result, key);
    return result;
}
fn summaryWire(r: *query.Request, value: Value) !Value {
    var text: std.Io.Writer.Allocating = .init(r.a);
    try wire.functionSummary(&text.writer, value, model.bodyNodes(r.a, value).len);
    return model.parseValue(r.a, text.written());
}
fn capability(r: *query.Request, wanted: []const u8, operation_id: []const u8, types: []const Parameter, result_type: TypeRef, repairs: []const Repair) anyerror!migration.Result {
    const prefix = "OperationSignatureMigration";
    if (!m.same(wanted, r.snapshot_id)) return reject(r, prefix, "ExpectedBaseMismatch", "The request targets a different snapshot.");
    var cap: ?Value = null;
    var op: Value = undefined;
    for (model.arrayItems(r.model, "capabilities")) |c| for (model.arrayItems(c, "operations")) |o| if (m.same(query.idOf(o), operation_id)) { cap = c; op = o; };
    if (cap == null) return reject(r, prefix, "OperationNotFound", "The operation does not exist.");
    if (!exactParameters(op, types)) return reject(r, prefix, "ParameterMismatch", "Supply each existing operation parameter ID exactly once.");
    for (repairs, 0..) |repair, i| for (repairs[0..i]) |prior| if (m.same(repair.rewrite.id, prior.rewrite.id)) return reject(r, prefix, "DuplicateRepair", "Supply each repaired function once.");
    const updated_op = try edit.set(r.a, try changedParameters(r, op, types), "resultType", try typeValue(r, result_type));
    if (!try signatureChanged(r, op, updated_op)) return reject(r, prefix, "TypeUnchanged", "The operation signature is unchanged.");
    try declared(r, prefix, "Operation signature", null, result_type);
    for (types) |t| try declared(r, prefix, "Operation signature", null, t.type);
    for (repairs) |repair| {
        const f = edit.find(r.model, "functions", repair.rewrite.id) orelse return reject(r, prefix, "RepairSignatureMismatch", "A repair references an unknown function.");
        if (!exactParameters(f, repair.parameters)) return reject(r, prefix, "RepairSignatureMismatch", "Supply each existing function parameter ID exactly once.");
    }
    var candidate = r.model;
    const rewrites = try r.a.alloc(Rewrite, repairs.len);
    var changed: std.StringHashMapUnmanaged(void) = .empty;
    for (repairs, rewrites) |repair, *rewrite| {
        const f = edit.find(r.model, "functions", repair.rewrite.id).?;
        const updated = try edit.set(r.a, try changedParameters(r, f, repair.parameters), "resultType", try typeValue(r, repair.result));
        candidate = try edit.replace(r.a, candidate, "functions", updated);
        if (try signatureChanged(r, f, updated)) try changed.put(r.a, query.idOf(f), {});
        rewrite.* = repair.rewrite;
    }
    var required: std.StringHashMapUnmanaged(void) = .empty;
    for (model.arrayItems(r.model, "functions")) |f| for (model.bodyNodes(r.a, f)) |node| {
        const kind = edit.text(node, "kind");
        if (m.same(kind, "capabilityInvoke") and m.same(edit.text(node, "operationId"), operation_id)) try required.put(r.a, query.idOf(f), {});
        if (m.same(kind, "capabilityConstruct")) for (model.arrayItems(node, "implementations")) |binding| if (m.same(edit.text(binding, "operationId"), operation_id)) {
            try required.put(r.a, query.idOf(f), {});
            const implementation = edit.text(binding, "functionId");
            if (edit.find(r.model, "functions", implementation) != null) try required.put(r.a, implementation, {});
        };
    };
    var extended = true;
    while (extended) {
        extended = false;
        // Only changed members of the actual affected set seed caller expansion.
        for (model.arrayItems(r.model, "functions")) |f| if (required.contains(query.idOf(f)) and changed.contains(query.idOf(f))) {
            for (model.referencesTo(r.a, r.model, "function", model.decoded(model.property(f, "id").?)).?) |reference| if (reference.owner == .function and reference.site.tag == .node) {
                const caller = model.utf8(reference.owner_id);
                if (!required.contains(caller)) { try required.put(r.a, caller, {}); extended = true; }
            };
        };
    }
    var affected: std.ArrayList(Value) = .empty;
    var missing: std.ArrayList([]const u8) = .empty;
    var unrelated: std.ArrayList([]const u8) = .empty;
    for (model.arrayItems(r.model, "functions")) |f| if (required.contains(query.idOf(f))) { try affected.append(r.a, f); if (requested(rewrites, query.idOf(f)) == null) try missing.append(r.a, query.idOf(f)); };
    for (rewrites) |rewrite| if (!required.contains(rewrite.id)) try unrelated.append(r.a, rewrite.id);
    if (missing.items.len > 0 or unrelated.items.len > 0) return reject(r, prefix, "RepairMismatch", try std.fmt.allocPrint(r.a, "Repair all invokers, constructed implementations/owners and users of changed signatures. Missing: [{s}]. Unrelated: [{s}].", .{ try idList(r, missing.items), try idList(r, unrelated.items) }));
    for (affected.items) |f| if (migration.bodyObligation(r, f)) |obligation| return reject(r, prefix, "BodyReferencedByObligation", try std.fmt.allocPrint(r.a, "Obligation '{s}' targets the body being replaced.", .{obligation}));
    const updated_cap = try edit.replace(r.a, cap.?, "operations", updated_op);
    candidate = try edit.replace(r.a, candidate, "capabilities", updated_cap);
    candidate = try rewriteAll(r, candidate, affected.items, rewrites, prefix, false);
    var result = try finish(r, "capabilityOperationSignatureMigration", candidate, affected.items, .json_null, try std.fmt.allocPrint(r.a, "capabilityOperationSignatureMigration of '{s}' operation '{s}'", .{ query.nameOf(cap.?), query.nameOf(op) }));
    var signatures: std.ArrayList(Value) = .empty;
    for (affected.items) |f| try signatures.append(r.a, try m.object(r.a, &.{ try m.member(r.a, "before", try summaryWire(r, f)), try m.member(r.a, "after", try summaryWire(r, edit.find(candidate, "functions", query.idOf(f)).?)) }));
    result.diff = try m.object(r.a, &.{ try m.member(r.a, "capabilityId", model.property(cap.?, "id").?), try m.member(r.a, "oldOperation", try operationWire(r, op)), try m.member(r.a, "newOperation", try operationWire(r, updated_op)), try m.member(r.a, "signatureChanges", .{ .array = signatures.items }), try m.member(r.a, "bodyRewrites", try bodyDiffs(r, result.candidate, affected.items)) });
    result.touched_capabilities = try r.a.dupe([]const u8, &.{query.idOf(cap.?)});
    return result;
}

test "mismatch references report visible ownership and sources" {
    var arena: std.heap.ArenaAllocator = .init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const parsed = try model.parseValue(a,
        \\{"snapshotId":"s","types":[],"effects":[],"capabilities":[],"obligations":[],"functions":[{"id":"local","displayName":"Local","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"nodes":[{"id":"n1","kind":"unitLiteral"}],"resultNodeId":"n1"}},{"id":"imported","displayName":"Imported","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"nodes":[{"id":"n2","kind":"unitLiteral"}],"resultNodeId":"n2"}},{"id":"hidden","displayName":"Hidden","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"nodes":[{"id":"n3","kind":"unitLiteral"}],"resultNodeId":"n3"}}]}
    );
    const root = try model.ownTyped(a, parsed, false);
    var r: query.Request = .{ .a = a, .line = "", .root = .empty, .model = root, .provenance = .{
        .hidden = &.{"function:hidden"},
        .members = &.{.{ .id = "source", .fingerprint = "digest", .keys = &.{ "function:imported", "function:hidden" }, .path = "", .embedded = false }},
    } };
    var out: std.Io.Writer.Allocating = .init(a);
    try mismatchReferences(&r, &out.writer, &.{ "local", "imported", "hidden", "missing" });
    try std.testing.expectEqualStrings("{\"items\":[{\"id\":\"hidden\",\"displayName\":\"\\u003Cunknown\\u003E\",\"displayNameTruncated\":false,\"ownership\":\"unknown\",\"sources\":[],\"sourceCount\":0,\"sourcesTruncated\":false},{\"id\":\"imported\",\"displayName\":\"Imported\",\"displayNameTruncated\":false,\"ownership\":\"imported\",\"sources\":[{\"snapshotId\":\"source\",\"fingerprint\":\"digest\"}],\"sourceCount\":1,\"sourcesTruncated\":false},{\"id\":\"local\",\"displayName\":\"Local\",\"displayNameTruncated\":false,\"ownership\":\"local\",\"sources\":[],\"sourceCount\":0,\"sourcesTruncated\":false},{\"id\":\"missing\",\"displayName\":\"\\u003Cunknown\\u003E\",\"displayNameTruncated\":false,\"ownership\":\"unknown\",\"sources\":[],\"sourceCount\":0,\"sourcesTruncated\":false}],\"totalCount\":4,\"truncated\":false}", out.written());
}
