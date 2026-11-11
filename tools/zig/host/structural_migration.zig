//! Product-field and capability signature evolution reuse the ordinary replacement pipeline.
const std = @import("std");
const model = @import("model_reader");
const query = @import("query.zig");
const recipe = @import("recipe.zig");
const preflight = @import("preflight.zig");
const infer = @import("infer.zig");
const m = @import("materialize.zig");
const edit = @import("typed_edit.zig");
const tm = model.session_model;
const Model = model.Model;
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
fn owner(d: *recipe.Decoder, f: Model) !void {
    const names = try d.r.a.alloc([]const u8, (f).parameters().len);
    for ((f).parameters(), names) |p, *n| n.* = query.nameOf(p);
    d.owner = .{ .name = query.nameOf(f), .parameters = names, .existing = f };
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
fn parameters(d: *recipe.Decoder, item: Json, path: []const u8, f: ?Model, symbolic: bool, capability_: bool) ![]const Parameter {
    const entries = try d.array(item, "parameterTypes");
    const result = try d.r.a.alloc(Parameter, entries.len);
    for (entries, result, 0..) |entry, *p, i| {
        const key = if (symbolic) "parameter" else "parameterId";
        const ppath = try std.fmt.allocPrint(d.r.a, "{s}/parameterTypes/{d}", .{ path, i });
        try d.ensureFields(ppath, entry, if (capability_) "operation parameter type" else "function parameter type", &.{ key, "type" }, &.{ key, "type" });
        const pid = if (symbolic) query.idOf(if (capability_) try d.member("parameter on operation {s}", .{query.nameOf(f.?)}, f.?, "parameters", try d.requiredString(entry, key)) else try d.member("parameter on {s}", .{query.nameOf(f.?)}, f.?, "parameters", try d.requiredString(entry, key))) else try id(d, entry, key);
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
    var selected: ?Model = null;
    const target_id = if (symbolic) blk: {
        const group = if (field) "type" else "capability";
        const container = try d.selector(try at(r, "", group), try d.property(root, group), group);
        if (field and !container.data.type_declaration.product) return r.fail("Type '{s}' is not a product type.", .{query.nameOf(container)});
        const key = if (field) "field" else "operation";
        selected = if (field) try d.member("field on {s}", .{query.nameOf(container)}, container, "fields", try d.requiredString(root, key)) else try d.member("operation on {s}", .{query.nameOf(container)}, container, "operations", try d.requiredString(root, key));
        break :blk query.idOf(selected.?);
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
            rewrite.* = .{ .id = fid, .body = try migration.decodedBody(&d, try at(r, path, "replacement"), try d.property(entry, "replacement"), try recipe.rawProperty(r.a, raw[i], "replacement")) };
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
            var f: ?Model = null;
            const fid = if (symbolic) blk: {
                f = try d.selector(try at(r, path, key), try d.property(entry, key), "function");
                try owner(&d, f.?);
                break :blk query.idOf(f.?);
            } else try id(&d, entry, key);
            const ps = try parameters(&d, entry, path, f, symbolic, false);
            var result = try d.anyType(try at(r, path, "resultType"), try d.property(entry, "resultType"));
            if (symbolic) result = try recipe.bindType(&d, result);
            repair.* = .{ .rewrite = .{ .id = fid, .body = try migration.decodedBody(&d, try at(r, path, "replacement"), try d.property(entry, "replacement"), try recipe.rawProperty(r.a, raw[i], "replacement")) }, .parameters = ps, .result = result };
        }
        try r.ensureUsable();
        try proposals.publishMigration(r, out, try capability(r, wanted, target_id, operation_parameters, new_type, repairs));
    }
}

fn typeValue(r: *query.Request, t: TypeRef) !tm.TypeRef {
    var materializer: m.Materializer = .{ .a = r.a, .r = r, .types = undefined, .fresh = &.{}, .function_id = tm.none, .parameters = &.{} };
    return materializer.typeRef(t);
}
fn unchanged(r: *query.Request, old: Model, new: TypeRef) !bool {
    return infer.eqlType(try infer.typeOf(r.a, old), new);
}
fn declared(r: *query.Request, prefix: []const u8, context: []const u8, owner_type: ?Model, t: TypeRef) anyerror!void {
    const parameters_ = if (owner_type) |o| (o).typeParameters() else &.{};
    const names = try r.a.alloc([]const u8, parameters_.len);
    for (parameters_, names) |p, *name| name.* = model.utf8((p).reference("typeParameterId"));
    const validator: @import("declarations.zig").Validation = .{ .r = r, .subject = "", .prefix = prefix, .parameters = names, .structural = true };
    try validator.typeRef(context, t);
}
fn changedParameters(r: *query.Request, f: Model, types: []const Parameter) !Model {
    const ps = try r.a.dupe(Model, (f).parameters());
    for (ps) |*p| for (types) |t| if (m.same(query.idOf(p.*), t.id)) {
        p.* = try edit.changed(r.a, p.*, .{ .type = try typeValue(r, t.type) });
        break;
    };
    return edit.withChildren(r.a, f, .parameters, ps);
}
fn exactParameters(f: Model, types: []const Parameter) bool {
    const ps = (f).parameters();
    if (ps.len != types.len) return false;
    for (types, 0..) |t, i| {
        var found = false;
        for (ps) |p| if (m.same(query.idOf(p), t.id)) {
            found = true;
            break;
        };
        if (!found) return false;
        for (types[0..i]) |prior| if (m.same(prior.id, t.id)) return false;
    }
    return true;
}
fn containsValue(values: []const Model, wanted: []const u8) bool {
    for (values) |v| if (m.same(query.idOf(v), wanted)) return true;
    return false;
}
fn requested(rewrites: []const Rewrite, wanted: []const u8) ?*const recipe.Node {
    for (rewrites) |rewrite| if (m.same(rewrite.id, wanted)) return rewrite.body;
    return null;
}
fn productAffected(r: *query.Request, type_id: []const u8, field_id: []const u8) ![]const Model {
    var functions: std.ArrayList(Model) = .empty;
    for ((r.typedModel()).functions()) |f| for (model.typedBodyNodes(r.a, f)) |n| {
        const kind = model.utf8((n).kind());
        var affected = (m.same(kind, "productConstruct") and m.same(model.utf8((n).reference("typeId")), type_id)) or (m.same(kind, "fieldAccess") and m.same(model.utf8((n).reference("fieldId")), field_id));
        if (m.same(kind, "productUpdate")) for ((n).fields()) |binding| {
            if (m.same(model.utf8((binding).reference("targetFieldId")), field_id)) affected = true;
        };
        if (affected) {
            try functions.append(r.a, f);
            break;
        }
    };
    return functions.items;
}
fn rewriteAll(r: *query.Request, candidate: Model, affected: []const Model, rewrites: []const Rewrite, prefix: []const u8, product_: bool) !Model {
    var next = r.*;
    next.generation = candidate.data.snapshot;
    var result = candidate;
    var failure: ?query.Rejection = null;
    var diagnostic: query.Diagnostic = .{};
    for (affected) |f| {
        const current = edit.find(candidate, .functions, query.idOf(f)).?;
        const updated = migration.materializeReplacement(&next, current, requested(rewrites, query.idOf(f)).?) catch |err| {
            if (err != error.Rejected) return err;
            if (failure == null) {
                const detail = next.diagnostic;
                try tryFailure(&next, prefix, if (product_) "RewriteInvalid" else "RepairInvalid", if (product_) try std.fmt.allocPrint(r.a, "Function '{s}' rewrite is invalid: {s}: {s}", .{ query.idOf(f), detail.code, detail.message }) else try std.fmt.allocPrint(r.a, "{s}: {s}", .{ detail.code, detail.message }));
                failure = next.rejection;
                diagnostic = next.diagnostic;
            }
            continue;
        };
        result = try edit.replace(r.a, result, .functions, updated);
    }
    if (failure) |f| {
        r.rejection = f;
        r.diagnostic = diagnostic;
        return error.Rejected;
    }
    return result;
}
fn tryFailure(r: *query.Request, prefix: []const u8, suffix: []const u8, message: []const u8) !void {
    const err = reject(r, prefix, suffix, message);
    if (err != error.Rejected) return err;
}
fn finish(r: *query.Request, kind: []const u8, candidate: Model, before: []const Model, diff: Value, summary: []const u8) !migration.Result {
    const transaction = try fresh(r);
    const snapshot = try fresh(r);
    const final = try migration.stamp(r, candidate, snapshot);
    try migration.validate(r, final);
    const after = try r.a.alloc(Model, before.len);
    for (before, after) |f, *next| next.* = edit.find(final, .functions, query.idOf(f)).?;
    return .{ .kind = kind, .transaction = transaction, .snapshot = snapshot, .candidate = final, .diff = diff, .rewritten = after, .summary = summary };
}
fn bodyDiffs(r: *query.Request, candidate: Model, affected: []const Model) !Value {
    const result = try r.a.alloc(Value, affected.len);
    for (affected, result) |f, *diff| diff.* = try migration.bodyDiff(r, candidate, f, edit.find(candidate, .functions, query.idOf(f)).?);
    return .{ .array = result };
}
fn wireType(r: *query.Request, value: anytype) !Value {
    const native: tm.TypeRef = if (comptime @TypeOf(value) == Model) value.data.type_ref else value;
    var out: std.Io.Writer.Allocating = .init(r.a);
    try wire.typedTypeRef(&out.writer, r.interner.?, native, &.{});
    return model.parseValue(r.a, out.written());
}

fn product(r: *query.Request, wanted: []const u8, fid: []const u8, new_type: TypeRef, rewrites: []const Rewrite) anyerror!migration.Result {
    const prefix = "FieldTypeMigration";
    if (!m.same(wanted, r.snapshot_id)) return reject(r, prefix, "ExpectedBaseMismatch", "The migration request targets a different base snapshot.");
    var owner_type: ?Model = null;
    var field: Model = undefined;
    for ((r.typedModel()).types()) |t| for ((t).fields()) |f| if (m.same(query.idOf(f), fid)) {
        owner_type = t;
        field = f;
    };
    if (owner_type == null) return reject(r, prefix, "FieldNotFound", "The field is not a field of an existing Product.");
    if (try unchanged(r, (field).declaredType(), new_type)) return reject(r, prefix, "TypeUnchanged", "The requested field type is already the current type.");
    try declared(r, prefix, "The new field type", owner_type, new_type);
    const affected = try productAffected(r, query.idOf(owner_type.?), fid);
    for (rewrites, 0..) |rewrite, i| for (rewrites[0..i]) |prior| if (m.same(rewrite.id, prior.id)) return reject(r, prefix, "DuplicateRewrite", "Each affected function may have only one replacement body.");
    var missing: std.ArrayList(Model) = .empty;
    var unrelated: std.ArrayList([]const u8) = .empty;
    for (affected) |f| if (requested(rewrites, query.idOf(f)) == null) try missing.append(r.a, f);
    for (rewrites) |rewrite| if (!containsValue(affected, rewrite.id)) try unrelated.append(r.a, rewrite.id);
    if (missing.items.len > 0 or unrelated.items.len > 0) return fieldMismatch(r, missing.items, unrelated.items);
    for (affected) |f| if (migration.bodyObligation(r, f)) |obligation| return reject(r, prefix, "BodyReferencedByObligation", try std.fmt.allocPrint(r.a, "Function '{s}': StructuralBodyReferencedByObligation: Obligation '{s}' targets the body being replaced.", .{ query.idOf(f), obligation }));
    var updated_type = owner_type.?;
    const fields = try r.a.dupe(Model, (updated_type).fields());
    for (fields) |*f| {
        if (m.same(query.idOf(f.*), fid)) f.* = try edit.changed(r.a, f.*, .{ .type = try typeValue(r, new_type) });
    }
    updated_type = try edit.withChildren(r.a, updated_type, .fields, fields);
    var candidate = try edit.replace(r.a, r.typedModel(), .types, updated_type);
    candidate = try rewriteAll(r, candidate, affected, rewrites, prefix, true);
    var result = try finish(r, "productFieldTypeMigration", candidate, affected, .json_null, try std.fmt.allocPrint(r.a, "productFieldTypeMigration of '{s}' field '{s}'", .{ query.nameOf(updated_type), query.nameOf(field) }));
    const change = try m.object(r.a, &.{ try m.member(r.a, "typeId", migration.wireId(updated_type)), try m.member(r.a, "fieldId", migration.wireId(field)), try m.member(r.a, "oldType", try wireType(r, (field).declaredType())), try m.member(r.a, "newType", try wireType(r, try typeValue(r, new_type))) });
    result.diff = try m.object(r.a, &.{ try m.member(r.a, "fieldChange", change), try m.member(r.a, "bodyRewrites", try bodyDiffs(r, result.candidate, affected)) });
    result.touched_types = try r.a.dupe([]const u8, &.{query.idOf(updated_type)});
    return result;
}

fn orderedIds(r: *query.Request, values: []const []const u8) ![]const []const u8 {
    const result = try r.a.dupe([]const u8, values);
    std.mem.sort([]const u8, result, {}, query.ordinal);
    return result;
}
fn nameCut(text: model.Str) model.Str {
    var n = @min(128, text.len);
    if (n > 0 and n < text.len and text[n - 1] >= 0xd800 and text[n - 1] <= 0xdbff) n -= 1;
    return text[0..n];
}
fn mismatchNames(r: *query.Request, ids_: []const []const u8) ![]const u8 {
    var text: std.Io.Writer.Allocating = .init(r.a);
    const sorted = try orderedIds(r, ids_);
    for (sorted[0..@min(5, sorted.len)], 0..) |fid, i| {
        if (i > 0) try text.writer.writeAll(", ");
        if (edit.find(r.typedModel(), .functions, fid)) |function| {
            const name = function.displayName();
            const cut = nameCut(name);
            try text.writer.print("'{s}{s}' ({s})", .{ model.utf8(cut), if (cut.len < name.len) @as([]const u8, "…") else "", fid });
        } else try text.writer.writeAll(fid);
    }
    if (sorted.len > 5) try text.writer.print("; +{d} more (inspect declarations)", .{sorted.len - 5});
    return text.written();
}
fn mismatchReferences(r: *query.Request, out: *std.Io.Writer, ids_: []const []const u8) !void {
    const visible = try query.typedVisible(r, "function");
    const sorted = try orderedIds(r, ids_);
    try out.writeAll("{\"items\":[");
    for (sorted[0..@min(5, sorted.len)], 0..) |fid, i| {
        if (i > 0) try out.writeByte(',');
        const function: ?Model = for (visible) |item| {
            if (m.same(query.idOf(item), fid)) break item;
        } else null;
        const name = if (function) |item| item.displayName() else try std.unicode.utf8ToUtf16LeAlloc(r.a, "<unknown>");
        const cut = nameCut(name);
        const sources = if (function != null) try query.sources(r, "function", fid) else &.{};
        try out.writeAll("{\"id\":");
        try out.writeAll(try query.stringOf(r.a, fid));
        try out.writeAll(",\"displayName\":");
        try model.writeString(out, cut);
        try out.print(",\"displayNameTruncated\":{s},\"ownership\":", .{if (cut.len < name.len) @as([]const u8, "true") else "false"});
        try out.writeAll(try query.stringOf(r.a, if (function != null) (if (sources.len == 0) @as([]const u8, "local") else "imported") else "unknown"));
        try out.writeAll(",\"sources\":[");
        for (sources[0..@min(5, sources.len)], 0..) |source, j| {
            if (j > 0) try out.writeByte(',');
            try out.writeAll("{\"snapshotId\":");
            try out.writeAll(try query.stringOf(r.a, source.id));
            try out.writeAll(",\"fingerprint\":");
            try out.writeAll(try query.stringOf(r.a, source.fingerprint));
            try out.writeByte('}');
        }
        try out.print("],\"sourceCount\":{d},\"sourcesTruncated\":{s}}}", .{ sources.len, if (sources.len > 5) @as([]const u8, "true") else "false" });
    }
    try out.print("],\"totalCount\":{d},\"truncated\":{s}}}", .{ sorted.len, if (sorted.len > 5) @as([]const u8, "true") else "false" });
}

fn fieldMismatch(r: *query.Request, missing: []const Model, unrelated: []const []const u8) anyerror {
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
fn signatureChanged(r: *query.Request, before: Model, after: Model) !bool {
    if (!try unchanged(r, (before).resultType(), try infer.typeOf(r.a, (after).resultType()))) return true;
    for ((before).parameters(), (after).parameters()) |old, new| if (!try unchanged(r, (old).declaredType(), try infer.typeOf(r.a, (new).declaredType()))) return true;
    return false;
}
fn idList(r: *query.Request, values: []const []const u8) ![]const u8 {
    return std.mem.join(r.a, ", ", try orderedIds(r, values));
}
fn operationWire(r: *query.Request, value: Model) !Value {
    var text: std.Io.Writer.Allocating = .init(r.a);
    try wire.typedCapabilityOperation(&text.writer, r.interner.?, value.data.operation);
    const result = try model.parseValue(r.a, text.written());
    var fields: std.ArrayList(model.Member) = .empty;
    for (result.object) |field| {
        const name = model.utf8(field.name);
        if (!m.same(name, "failureType") and !m.same(name, "effectId") and !m.same(name, "mayWait")) try fields.append(r.a, field);
    }
    return .{ .object = fields.items };
}
fn summaryWire(r: *query.Request, value: Model) !Value {
    var text: std.Io.Writer.Allocating = .init(r.a);
    try wire.typedFunctionSummary(&text.writer, r.interner.?, value.data.function, model.typedBodyNodes(r.a, value).len);
    return model.parseValue(r.a, text.written());
}
fn capability(r: *query.Request, wanted: []const u8, operation_id: []const u8, types: []const Parameter, result_type: TypeRef, repairs: []const Repair) anyerror!migration.Result {
    const prefix = "OperationSignatureMigration";
    if (!m.same(wanted, r.snapshot_id)) return reject(r, prefix, "ExpectedBaseMismatch", "The request targets a different snapshot.");
    var cap: ?Model = null;
    var op: Model = undefined;
    for ((r.typedModel()).capabilities()) |c| for ((c).operations()) |o| if (m.same(query.idOf(o), operation_id)) {
        cap = c;
        op = o;
    };
    if (cap == null) return reject(r, prefix, "OperationNotFound", "The operation does not exist.");
    if (!exactParameters(op, types)) return reject(r, prefix, "ParameterMismatch", "Supply each existing operation parameter ID exactly once.");
    for (repairs, 0..) |repair, i| for (repairs[0..i]) |prior| if (m.same(repair.rewrite.id, prior.rewrite.id)) return reject(r, prefix, "DuplicateRepair", "Supply each repaired function once.");
    const updated_op = try edit.changed(r.a, try changedParameters(r, op, types), .{ .result = try typeValue(r, result_type) });
    if (!try signatureChanged(r, op, updated_op)) return reject(r, prefix, "TypeUnchanged", "The operation signature is unchanged.");
    try declared(r, prefix, "Operation signature", null, result_type);
    for (types) |t| try declared(r, prefix, "Operation signature", null, t.type);
    for (repairs) |repair| {
        const f = edit.find(r.typedModel(), .functions, repair.rewrite.id) orelse return reject(r, prefix, "RepairSignatureMismatch", "A repair references an unknown function.");
        if (!exactParameters(f, repair.parameters)) return reject(r, prefix, "RepairSignatureMismatch", "Supply each existing function parameter ID exactly once.");
    }
    var candidate = r.typedModel();
    const rewrites = try r.a.alloc(Rewrite, repairs.len);
    var changed: std.StringHashMapUnmanaged(void) = .empty;
    for (repairs, rewrites) |repair, *rewrite| {
        const f = edit.find(r.typedModel(), .functions, repair.rewrite.id).?;
        const updated = try edit.changed(r.a, try changedParameters(r, f, repair.parameters), .{ .result = try typeValue(r, repair.result) });
        candidate = try edit.replace(r.a, candidate, .functions, updated);
        if (try signatureChanged(r, f, updated)) try changed.put(r.a, query.idOf(f), {});
        rewrite.* = repair.rewrite;
    }
    var required: std.StringHashMapUnmanaged(void) = .empty;
    for ((r.typedModel()).functions()) |f| for (model.typedBodyNodes(r.a, f)) |node| {
        const kind = model.utf8((node).kind());
        if (m.same(kind, "capabilityInvoke") and m.same(model.utf8((node).reference("operationId")), operation_id)) try required.put(r.a, query.idOf(f), {});
        if (m.same(kind, "useResource") and m.same(model.utf8((node).reference("releaseOperationId")), operation_id)) try required.put(r.a, query.idOf(f), {});
        if (m.same(kind, "capabilityConstruct")) for ((node).implementations()) |binding| if (m.same(model.utf8((binding).reference("operationId")), operation_id)) {
            try required.put(r.a, query.idOf(f), {});
            const implementation = model.utf8((binding).reference("functionId"));
            if (edit.find(r.typedModel(), .functions, implementation) != null) try required.put(r.a, implementation, {});
        };
    };
    var extended = true;
    while (extended) {
        extended = false;
        // Only changed members of the actual affected set seed caller expansion.
        for ((r.typedModel()).functions()) |f| if (required.contains(query.idOf(f)) and changed.contains(query.idOf(f))) {
            for (model.typedReferencesTo(r.a, r.typedModel(), "function", f.id()).?) |reference| if (reference.owner == .function and reference.site.tag == .node) {
                const caller = model.utf8(reference.owner_id);
                if (!required.contains(caller)) {
                    try required.put(r.a, caller, {});
                    extended = true;
                }
            };
        };
    }
    var affected: std.ArrayList(Model) = .empty;
    var missing: std.ArrayList([]const u8) = .empty;
    var unrelated: std.ArrayList([]const u8) = .empty;
    for ((r.typedModel()).functions()) |f| if (required.contains(query.idOf(f))) {
        try affected.append(r.a, f);
        if (requested(rewrites, query.idOf(f)) == null) try missing.append(r.a, query.idOf(f));
    };
    for (rewrites) |rewrite| if (!required.contains(rewrite.id)) try unrelated.append(r.a, rewrite.id);
    if (missing.items.len > 0 or unrelated.items.len > 0) return reject(r, prefix, "RepairMismatch", try std.fmt.allocPrint(r.a, "Repair all invokers, constructed implementations/owners and users of changed signatures. Missing: [{s}]. Unrelated: [{s}].", .{ try idList(r, missing.items), try idList(r, unrelated.items) }));
    for (affected.items) |f| if (migration.bodyObligation(r, f)) |obligation| return reject(r, prefix, "BodyReferencedByObligation", try std.fmt.allocPrint(r.a, "Obligation '{s}' targets the body being replaced.", .{obligation}));
    const updated_cap = try edit.replace(r.a, cap.?, .operations, updated_op);
    candidate = try edit.replace(r.a, candidate, .capabilities, updated_cap);
    candidate = try rewriteAll(r, candidate, affected.items, rewrites, prefix, false);
    var result = try finish(r, "capabilityOperationSignatureMigration", candidate, affected.items, .json_null, try std.fmt.allocPrint(r.a, "capabilityOperationSignatureMigration of '{s}' operation '{s}'", .{ query.nameOf(cap.?), query.nameOf(op) }));
    var signatures: std.ArrayList(Value) = .empty;
    for (affected.items) |f| try signatures.append(r.a, try m.object(r.a, &.{ try m.member(r.a, "before", try summaryWire(r, f)), try m.member(r.a, "after", try summaryWire(r, edit.find(candidate, .functions, query.idOf(f)).?)) }));
    result.diff = try m.object(r.a, &.{ try m.member(r.a, "capabilityId", migration.wireId(cap.?)), try m.member(r.a, "oldOperation", try operationWire(r, op)), try m.member(r.a, "newOperation", try operationWire(r, updated_op)), try m.member(r.a, "signatureChanges", .{ .array = signatures.items }), try m.member(r.a, "bodyRewrites", try bodyDiffs(r, result.candidate, affected.items)) });
    result.touched_capabilities = try r.a.dupe([]const u8, &.{query.idOf(cap.?)});
    return result;
}
