//! Protocol declaration intents and the declaration workflows, before the shared publication path.
const std = @import("std");
const model = @import("model_reader");
const query = @import("query.zig");
const recipe = @import("recipe.zig");
const preflight = @import("preflight.zig");
const m = @import("materialize.zig");
const ids = @import("ids.zig");
const proposals = @import("proposals.zig");
const Json = std.json.Value;
const Value = model.Value;
const Writer = std.Io.Writer;
const TypeRef = recipe.TypeRef;
const Field = preflight.Parameter;
const Variant = struct { name: []const u8, fields: []const Field };
const Kind = union(enum) { product: []const Field, union_: []const Variant };
const Intent = struct { name: []const u8, exported: bool, parameters: []const []const u8, raw: Json, path: []const u8, kind: Kind = undefined, reference: []const u8 = "", actual: []const u8 = "" };
const Operation = struct { name: []const u8, parameters: []const Field, result: TypeRef, effect: []const u8, may_wait: bool };

fn at(r: *query.Request, path: []const u8, key: anytype) ![]const u8 {
    return std.fmt.allocPrint(r.a, "{s}/{s}", .{path, key});
}
fn index(r: *query.Request, path: []const u8, i: usize) ![]const u8 {
    return std.fmt.allocPrint(r.a, "{s}/{d}", .{path, i});
}
fn boolean(d: *recipe.Decoder, root: Json, name: []const u8) !bool {
    try d.optionalBool(root, name);
    return if (root.object.get(name)) |v| v.bool else false;
}
fn expected(d: *recipe.Decoder, root: Json) ![]const u8 {
    return if (root.object.get("expectedSnapshotId") != null) try d.stableId("expectedSnapshotId", try d.requiredString(root, "expectedSnapshotId")) else d.r.snapshot_id;
}
fn fresh(r: *query.Request) ![]const u8 {
    const id = try ids.next();
    return r.a.dupe(u8, &id);
}
fn parameters(d: *recipe.Decoder, item: Json) ![]const []const u8 {
    if (item.object.get("typeParameters") == null) return &.{};
    var names: std.ArrayList([]const u8) = .empty;
    for (try d.array(item, "typeParameters")) |value| {
        if (value != .string) return d.r.fail("typeParameters must list type parameter names as strings.", .{});
        const name = try query.readable(d.r, value.string);
        if (try recipe.isBlank(d.r.a, name)) return d.r.fail("Type parameter names must not be blank.", .{});
        try names.append(d.r.a, try d.stableId("typeParameters", name));
    }
    return names.items;
}
fn metadata(d: *recipe.Decoder, item: Json, path: []const u8) !Intent {
    return .{ .name = try d.requiredString(item, "displayName"), .exported = try boolean(d, item, "exported"), .parameters = try parameters(d, item), .raw = try d.property(item, "typeKind"), .path = try at(d.r, path, "typeKind") };
}
fn decodeFields(d: *recipe.Decoder, item: Json, path: []const u8, label: []const u8) anyerror![]const Field {
    var result: std.ArrayList(Field) = .empty;
    for (try d.array(item, "fields"), 0..) |field, i| {
        const p = try index(d.r, try at(d.r, path, "fields"), i);
        try d.ensureFields(p, field, label, &.{ "displayName", "type" }, &.{ "displayName", "type" });
        try result.append(d.r.a, .{ .name = try d.requiredString(field, "displayName"), .type = try d.anyType(try at(d.r, p, "type"), try d.property(field, "type")) });
    }
    return result.items;
}
fn decodeKind(d: *recipe.Decoder, item: Json, path: []const u8) !Kind {
    const tag = try d.requiredString(item, "kind");
    if (m.same(tag, "product")) {
        try d.ensureFields(path, item, "product type kind", &.{ "kind", "fields" }, &.{ "kind", "fields" });
        return .{ .product = try decodeFields(d, item, path, "type field") };
    }
    if (m.same(tag, "union")) {
        try d.ensureFields(path, item, "union type kind", &.{ "kind", "variants" }, &.{ "kind", "variants" });
        var variants: std.ArrayList(Variant) = .empty;
        for (try d.array(item, "variants"), 0..) |variant, i| {
            const p = try index(d.r, try at(d.r, path, "variants"), i);
            try d.ensureFields(p, variant, "union variant", &.{ "displayName", "fields" }, &.{ "displayName", "fields" });
            try variants.append(d.r.a, .{ .name = try d.requiredString(variant, "displayName"), .fields = try decodeFields(d, variant, p, "type field") });
        }
        return .{ .union_ = variants.items };
    }
    return d.r.fail("Unsupported type declaration kind '{s}'.", .{tag});
}
fn reject(r: *query.Request, subject: []const u8, code: []const u8, message: []const u8) anyerror {
    const failure = preflight.rejected(r, code, message);
    if (failure != error.Rejected) return failure;
    r.rejection.message = try std.fmt.allocPrint(r.a, "The {s} declaration was rejected.", .{subject});
    return error.Rejected;
}
fn checkBase(r: *query.Request, subject: []const u8, prefix: []const u8, wanted: []const u8) !void {
    if (!m.same(wanted, r.snapshot_id)) return reject(r, subject, try std.mem.concat(r.a, u8, &.{prefix, "ExpectedBaseMismatch"}), try std.fmt.allocPrint(r.a, "The {s}-declaration request targets a different base snapshot.", .{subject}));
}
fn checkName(r: *query.Request, subject: []const u8, prefix: []const u8, group: []const u8, name: []const u8) !void {
    if (try recipe.isBlank(r.a, name)) {
        const capital = try r.a.dupe(u8, subject);
        capital[0] = std.ascii.toUpper(capital[0]);
        return reject(r, subject, try std.mem.concat(r.a, u8, &.{prefix, "DisplayNameBlank"}), try std.fmt.allocPrint(r.a, "{s} display name must not be blank.", .{capital}));
    }
    for (model.arrayItems(r.model, group)) |item| if (m.same(query.nameOf(item), name))
        return reject(r, subject, try std.mem.concat(r.a, u8, &.{prefix, "DuplicateDisplayName"}), try std.fmt.allocPrint(r.a, "{s} {s} named '{s}' already exists; {s} display names must be unique.", .{ if (m.same(subject, "effect")) @as([]const u8, "An") else "A", subject, name, subject }));
}

pub const Validation = struct {
    r: *query.Request,
    subject: []const u8,
    prefix: []const u8,
    group: []const recipe.GroupType = &.{},
    parameters: []const []const u8 = &.{},
    member: ?[]const u8 = null,
    structural: bool = false,

    fn fail(v: Validation, suffix: []const u8, message: []const u8) anyerror {
        if (v.structural) return preflight.rejected(v.r, try std.mem.concat(v.r.a, u8, &.{ v.prefix, suffix }), message);
        return reject(v.r, v.subject, try std.mem.concat(v.r.a, u8, &.{v.prefix, suffix}), if (v.member) |name| try std.fmt.allocPrint(v.r.a, "Type '{s}': {s}", .{name, message}) else message);
    }
    pub fn typeRef(v: Validation, context: []const u8, t: TypeRef) anyerror!void {
        const a = v.r.a;
        switch (t) {
            .primitive => {},
            .type_parameter => |id| if (!preflight.contains(v.parameters, id)) return v.fail("TypeParameterNotAllowed", try std.fmt.allocPrint(a, "{s} cannot use type parameter '{s}'; only the declaring type's own type parameters may occur.", .{context, id})),
            .named => |n| {
                var known = preflight.declaration(v.r, "types", n.id) != null;
                for (v.group) |g| if (m.same(g.id, n.id)) { known = true; break; };
                if (!known) return v.fail("UnknownNamedType", try std.fmt.allocPrint(a, "{s} references unknown type '{s}'.", .{context, n.id}));
                for (n.arguments) |arg| try v.typeRef(context, arg);
            },
            .capability => |id| if (preflight.declaration(v.r, "capabilities", id) == null) return v.fail("UnknownCapabilityType", try std.fmt.allocPrint(a, "{s} references unknown capability '{s}'.", .{context, id})),
            .function => |f| {
                for (f.parameters) |arg| try v.typeRef(context, arg);
                try v.typeRef(context, f.result.*);
            },
            .builtin => |b| {
                const arity: usize = if (m.same(b.generic, "exception") or m.same(b.generic, "taskScope")) 0 else if (m.same(b.generic, "result") or m.same(b.generic, "map")) 2 else 1;
                if (b.arguments.len != arity) {
                    const case = try a.dupe(u8, b.generic);
                    case[0] = std.ascii.toUpper(case[0]);
                    return v.fail("BuiltinGenericArityMismatch", try std.fmt.allocPrint(a, "{s} uses {s} with {d} type arguments; exactly {d} required.", .{context, case, b.arguments.len, arity}));
                }
                for (b.arguments) |arg| try v.typeRef(context, arg);
            },
        }
    }
    fn fields(v: Validation, context: []const u8, fs: []const Field) !void {
        const names = try v.r.a.alloc([]const u8, fs.len);
        for (fs, names, 0..) |field, *name, i| {
            name.* = field.name;
            if (try recipe.isBlank(v.r.a, field.name)) return v.fail("FieldDisplayNameBlank", try std.fmt.allocPrint(v.r.a, "{s} field at index {d} must have a nonblank display name.", .{context, i}));
            try v.typeRef(try std.fmt.allocPrint(v.r.a, "{s} field at index {d}", .{context, i}), field.type);
        }
        if (preflight.repeated(names) != null) return v.fail("DuplicateFieldDisplayName", try std.fmt.allocPrint(v.r.a, "{s} field display names must be distinct.", .{context}));
    }
    fn kind(v: Validation, k: Kind) !void {
        if (preflight.repeated(v.parameters)) |name| return v.fail("DuplicateTypeParameter", try std.fmt.allocPrint(v.r.a, "Type parameter '{s}' is declared more than once.", .{name}));
        switch (k) {
            .product => |fs| try v.fields("Product", fs),
            .union_ => |variants| {
                const names = try v.r.a.alloc([]const u8, variants.len);
                for (variants, names, 0..) |variant, *name, i| {
                    name.* = variant.name;
                    if (try recipe.isBlank(v.r.a, variant.name)) return v.fail("VariantDisplayNameBlank", try std.fmt.allocPrint(v.r.a, "Union variant at index {d} must have a nonblank display name.", .{i}));
                    try v.fields(try std.fmt.allocPrint(v.r.a, "Union variant at index {d}", .{i}), variant.fields);
                }
                if (preflight.repeated(names) != null) return v.fail("DuplicateVariantDisplayName", "Union variant display names must be unique within their Union.");
            },
        }
    }
};

fn mentions(t: TypeRef, id: []const u8) bool {
    switch (t) {
        .named => |n| { if (m.same(n.id, id)) return true; for (n.arguments) |arg| if (mentions(arg, id)) return true; },
        .builtin => |b| for (b.arguments) |arg| { if (mentions(arg, id)) return true; },
        .function => |f| { for (f.parameters) |arg| if (mentions(arg, id)) return true; return mentions(f.result.*, id); },
        else => {},
    }
    return false;
}
fn kindMentions(k: Kind, id: []const u8) bool {
    switch (k) {
        .product => |fs| for (fs) |f| { if (mentions(f.type, id)) return true; },
        .union_ => |vs| for (vs) |v| { for (v.fields) |f| if (mentions(f.type, id)) return true; },
    }
    return false;
}
fn inhabited(t: TypeRef, intents: []const Intent, known: []const bool) bool {
    switch (t) {
        .named => |n| { for (intents, 0..) |item, i| if (m.same(item.reference, n.id)) return known[i]; },
        .builtin => |b| if (m.same(b.generic, "result")) { for (b.arguments) |arg| if (inhabited(arg, intents, known)) return true; return false; },
        else => {},
    }
    return true;
}
fn fieldsInhabited(fs: []const Field, intents: []const Intent, known: []const bool) bool {
    for (fs) |f| if (!inhabited(f.type, intents, known)) return false;
    return true;
}
fn kindInhabited(k: Kind, intents: []const Intent, known: []const bool) bool {
    switch (k) {
        .product => |fs| return fieldsInhabited(fs, intents, known),
        .union_ => |vs| { for (vs) |v| if (fieldsInhabited(v.fields, intents, known)) return true; return false; },
    }
}
fn checkInhabited(r: *query.Request, intents: []const Intent) !void {
    const known = try r.a.alloc(bool, intents.len);
    @memset(known, false);
    while (true) {
        var changed = false;
        for (intents, 0..) |item, i| if (!known[i] and kindInhabited(item.kind, intents, known)) { known[i] = true; changed = true; };
        if (!changed) break;
    }
    var names: std.ArrayList([]const u8) = .empty;
    for (intents, known) |item, yes| if (!yes) try names.append(r.a, try std.fmt.allocPrint(r.a, "'{s}'", .{item.name}));
    if (names.items.len > 0) return reject(r, "type", "TypeDeclarationUninhabited", try std.fmt.allocPrint(r.a, "Type {s} has no finite value: its Product fields, or some field of each of its Union variants, need a type of this group that has none. Break the cycle with a List, an Option or a Union variant that does not.", .{try std.mem.join(r.a, ", ", names.items)}));
}

fn rewrite(r: *query.Request, t: TypeRef, intents: []const Intent) anyerror!TypeRef {
    switch (t) {
        .named => |n| {
            var id = n.id;
            for (intents) |item| if (m.same(item.reference, id)) { id = item.actual; break; };
            return .{ .named = .{ .id = id, .arguments = try rewriteAll(r, n.arguments, intents) } };
        },
        .builtin => |b| return .{ .builtin = .{ .generic = b.generic, .arguments = try rewriteAll(r, b.arguments, intents) } },
        .function => |f| {
            const result = try r.a.create(TypeRef);
            result.* = try rewrite(r, f.result.*, intents);
            return .{ .function = .{ .parameters = try rewriteAll(r, f.parameters, intents), .result = result } };
        },
        else => return t,
    }
}
fn rewriteAll(r: *query.Request, ts: []const TypeRef, intents: []const Intent) ![]const TypeRef {
    const result = try r.a.alloc(TypeRef, ts.len);
    for (ts, result) |t, *out| out.* = try rewrite(r, t, intents);
    return result;
}
fn materializeFields(materializer: *m.Materializer, fs: []const Field, intents: []const Intent) !Value {
    const a = materializer.a;
    const result = try a.alloc(Value, fs.len);
    for (fs, result) |f, *out| out.* = try m.object(a, &.{
        try m.member(a, "id", try m.id(a)), try m.member(a, "displayName", try m.string(a, f.name)),
        try m.member(a, "type", try materializer.typeRef(try rewrite(materializer.r, f.type, intents))),
    });
    return .{ .array = result };
}
fn materializeType(r: *query.Request, item: Intent, intents: []const Intent, group: bool) !Value {
    const a = r.a;
    const fresh_parameters = try a.alloc(preflight.TypeParameter, item.parameters.len);
    const names = try a.alloc(Value, item.parameters.len);
    for (item.parameters, fresh_parameters, names) |name, *p, *value| {
        p.* = .{ .name = name, .id = try fresh(r) };
        value.* = try m.object(a, &.{ try m.member(a, "typeParameterId", try m.string(a, p.id)), try m.member(a, "displayName", try m.string(a, name)) });
    }
    var result: std.ArrayList(model.Member) = .empty;
    try result.appendSlice(a, &.{ try m.member(a, "id", try m.string(a, if (group) item.actual else try fresh(r))), try m.member(a, "displayName", try m.string(a, item.name)) });
    if (item.exported) try result.append(a, try m.member(a, "exported", .json_true));
    if (names.len > 0) try result.append(a, try m.member(a, "typeParameters", .{ .array = names }));
    var materializer: m.Materializer = .{ .a = a, .r = r, .types = undefined, .fresh = fresh_parameters, .function_id = .json_null, .parameters = &.{} };
    switch (item.kind) {
        .product => |fs| {
            try result.append(a, try m.member(a, "kind", try m.string(a, "product")));
            try result.append(a, try m.member(a, "fields", try materializeFields(&materializer, fs, intents)));
        },
        .union_ => |vs| {
            try result.append(a, try m.member(a, "kind", try m.string(a, "union")));
            const variants = try a.alloc(Value, vs.len);
            for (vs, variants) |v, *out| out.* = try m.object(a, &.{ try m.member(a, "id", try m.id(a)), try m.member(a, "displayName", try m.string(a, v.name)), try m.member(a, "fields", try materializeFields(&materializer, v.fields, intents)) });
            try result.append(a, try m.member(a, "variants", .{ .array = variants }));
        },
    }
    return .{ .object = result.items };
}

pub fn createType(r: *query.Request, out: *Writer) !void {
    const op = r.root.get("op").?.string;
    const plural = m.same(op, "simulateCreateTypesByName");
    const by_name = !m.same(op, "simulateCreateType");
    const allowed: []const []const u8 = if (plural) &.{ "id", "op", "types", "structuredReview", "detail", "expectedSnapshotId" } else &.{ "id", "op", "displayName", "exported", "typeParameters", "typeKind", "structuredReview", "detail", "expectedSnapshotId" };
    const required: []const []const u8 = if (plural) &.{ "id", "op", "types" } else &.{ "id", "op", "displayName", "typeKind" };
    if (try query.fieldProblem(r.a, r.line, &.{}, r.root, try std.fmt.allocPrint(r.a, "{s} request", .{op}), allowed, required)) |message| return r.fail("{s}", .{message});
    var d: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line) };
    if (by_name) d.owner = .{ .name = "" };
    const root: Json = .{ .object = r.root };
    var wanted: []const u8 = r.snapshot_id;
    if (!by_name) wanted = try expected(&d, root);
    var intents: std.ArrayList(Intent) = .empty;
    if (plural) {
        for (try d.array(root, "types"), 0..) |item, i| {
            const path = try index(r, "/types", i);
            try d.ensureFields(path, item, "type declaration", &.{ "displayName", "exported", "typeParameters", "typeKind" }, &.{ "displayName", "typeKind" });
            try intents.append(r.a, try metadata(&d, item, path));
        }
    } else try intents.append(r.a, try metadata(&d, root, ""));
    const references = try r.a.alloc(recipe.GroupType, if (by_name) intents.items.len else 0);
    if (by_name) for (intents.items, references) |*item, *reference| {
        item.reference = try fresh(r);
        reference.* = .{ .name = item.name, .id = item.reference };
    };
    d.type_group = references;
    if (plural) wanted = try expected(&d, root);
    for (intents.items) |*item| item.kind = try decodeKind(&d, item.raw, item.path);
    if (by_name and !plural) wanted = try expected(&d, root);
    const group = plural or (by_name and kindMentions(intents.items[0].kind, intents.items[0].reference));
    try r.ensureUsable();
    try checkBase(r, "type", "TypeDeclaration", wanted);
    if (plural and intents.items.len == 0) return reject(r, "type", "TypeDeclarationGroupEmpty", "A type group must declare at least one type.");
    // A single nonrecursive by-name request consumed its self reference, but is not a group.
    for (intents.items, 0..) |item, i| {
        try checkName(r, "type", "TypeDeclaration", "types", item.name);
        for (intents.items[0..i]) |earlier| if (m.same(earlier.name, item.name)) return reject(r, "type", "TypeDeclarationDuplicateDisplayName", try std.fmt.allocPrint(r.a, "The type group declares '{s}' more than once; type display names must be unique.", .{item.name}));
        const v: Validation = .{ .r = r, .subject = "type", .prefix = "TypeDeclaration", .group = if (group) references else &.{}, .parameters = item.parameters, .member = if (group) item.name else null };
        try v.kind(item.kind);
    }
    if (group) try checkInhabited(r, intents.items);
    if (group) for (intents.items) |*item| { item.actual = try fresh(r); };
    const created = try r.a.alloc(Value, intents.items.len);
    for (intents.items, created) |item, *value| value.* = try materializeType(r, item, if (group) intents.items else &.{}, group);
    try proposals.declare(r, out, .{ .kind = if (group) "createTypes" else "createType", .group = "types", .subject = "type", .items = created });
}

pub fn createEffect(r: *query.Request, out: *Writer) !void {
    if (try query.fieldProblem(r.a, r.line, &.{}, r.root, "simulateCreateEffect request", &.{ "id", "op", "displayName", "exported", "structuredReview", "detail", "expectedSnapshotId" }, &.{ "id", "op", "displayName" })) |message| return r.fail("{s}", .{message});
    var d: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line) };
    const root: Json = .{ .object = r.root };
    const wanted = try expected(&d, root);
    const name = try d.requiredString(root, "displayName");
    const exported = try boolean(&d, root, "exported");
    try r.ensureUsable();
    try checkBase(r, "effect", "EffectDeclaration", wanted);
    try checkName(r, "effect", "EffectDeclaration", "effects", name);
    var members: std.ArrayList(model.Member) = .empty;
    try members.appendSlice(r.a, &.{ try m.member(r.a, "id", try m.id(r.a)), try m.member(r.a, "displayName", try m.string(r.a, name)) });
    if (exported) try members.append(r.a, try m.member(r.a, "exported", .json_true));
    const created: Value = .{ .object = members.items };
    try proposals.declare(r, out, .{ .kind = "createEffect", .group = "effects", .subject = "effect", .items = &.{created} });
}

pub fn createCapability(r: *query.Request, out: *Writer) !void {
    const op = r.root.get("op").?.string;
    const by_name = m.same(op, "simulateCreateCapabilityByName");
    if (try query.fieldProblem(r.a, r.line, &.{}, r.root, try std.fmt.allocPrint(r.a, "{s} request", .{op}), &.{ "id", "op", "displayName", "exported", "taskBound", "operations", "structuredReview", "detail", "expectedSnapshotId" }, &.{ "id", "op", "displayName", "operations" })) |message| return r.fail("{s}", .{message});
    var d: recipe.Decoder = .{ .r = r, .duplicates = try recipe.duplicatePaths(r.a, r.line) };
    if (by_name) d.owner = .{ .name = "" };
    const root: Json = .{ .object = r.root };
    var wanted: []const u8 = r.snapshot_id;
    if (!by_name) wanted = try expected(&d, root);
    const name = try d.requiredString(root, "displayName");
    if (by_name) for (try query.visible(r, "capability")) |item| if (m.same(query.nameOf(item), name)) return r.fail("Capability display name '{s}' already exists. Choose a unique name or use the exact-ID authoring surface.", .{name});
    if (by_name) wanted = try expected(&d, root);
    const exported = try boolean(&d, root, "exported");
    const task_bound = try boolean(&d, root, "taskBound");
    var operations: std.ArrayList(Operation) = .empty;
    for (try d.array(root, "operations"), 0..) |item, i| {
        const path = try index(r, "/operations", i);
        const effect_key: []const u8 = if (by_name) "effect" else "effectId";
        try d.ensureFields(path, item, "capability operation", &.{ "displayName", "parameters", "resultType", "failureType", effect_key, "mayWait" }, &.{ "displayName", "parameters", "resultType", effect_key });
        if (item.object.get("failureType")) |failure| if (failure != .null) return r.fail("failureType is retired; declare the operation's resultType as Result<T, F> to model an expected failure.", .{});
        const operation_name = try d.requiredString(item, "displayName");
        var ps: std.ArrayList(Field) = .empty;
        for (try d.array(item, "parameters"), 0..) |parameter, j| {
            const p = try index(r, try at(r, path, "parameters"), j);
            try d.ensureFields(p, parameter, "capability parameter", &.{ "displayName", "type" }, &.{ "displayName", "type" });
            try ps.append(r.a, .{ .name = try d.requiredString(parameter, "displayName"), .type = try d.anyType(try at(r, p, "type"), try d.property(parameter, "type")) });
        }
        const result = try d.anyType(try at(r, path, "resultType"), try d.property(item, "resultType"));
        const effect = if (by_name) query.idOf(try d.selector(try at(r, path, effect_key), try d.property(item, effect_key), "effect")) else try d.stableId(effect_key, try d.requiredString(item, effect_key));
        try operations.append(r.a, .{ .name = operation_name, .parameters = ps.items, .result = result, .effect = effect, .may_wait = try boolean(&d, item, "mayWait") });
    }
    try r.ensureUsable();
    try checkBase(r, "capability", "CapabilityDeclaration", wanted);
    try checkName(r, "capability", "CapabilityDeclaration", "capabilities", name);
    const v: Validation = .{ .r = r, .subject = "capability", .prefix = "CapabilityDeclaration" };
    const names = try r.a.alloc([]const u8, operations.items.len);
    for (operations.items, names) |operation, *n| n.* = operation.name;
    if (preflight.repeated(names)) |duplicate| return v.fail("DuplicateOperationDisplayName", try std.fmt.allocPrint(r.a, "Capability operation '{s}' is declared more than once; operation display names must be unique within their Capability.", .{duplicate}));
    for (operations.items, 0..) |operation, i| {
        if (try recipe.isBlank(r.a, operation.name)) return v.fail("OperationDisplayNameBlank", try std.fmt.allocPrint(r.a, "Capability operation at index {d} must have a nonblank display name.", .{i}));
        if (preflight.declaration(r, "effects", operation.effect) == null) return v.fail("UnknownEffect", try std.fmt.allocPrint(r.a, "Capability operation at index {d} references unknown effect '{s}'.", .{i, operation.effect}));
        const ps = try r.a.alloc([]const u8, operation.parameters.len);
        for (operation.parameters, ps, 0..) |parameter, *n, j| {
            n.* = parameter.name;
            if (try recipe.isBlank(r.a, parameter.name)) return v.fail("ParameterDisplayNameBlank", try std.fmt.allocPrint(r.a, "Operation at index {d} parameter at index {d} must have a nonblank display name.", .{i, j}));
            try v.typeRef(try std.fmt.allocPrint(r.a, "Operation at index {d} parameter at index {d}", .{i, j}), parameter.type);
        }
        if (preflight.repeated(ps) != null) return v.fail("DuplicateParameterDisplayName", try std.fmt.allocPrint(r.a, "Operation at index {d} parameter display names must be distinct within their operation.", .{i}));
        try v.typeRef(try std.fmt.allocPrint(r.a, "Capability operation at index {d} result", .{i}), operation.result);
    }
    const a = r.a;
    const id = try m.id(a);
    var materializer: m.Materializer = .{ .a = a, .r = r, .types = undefined, .fresh = &.{}, .function_id = .json_null, .parameters = &.{} };
    const ops = try a.alloc(Value, operations.items.len);
    for (operations.items, ops) |operation, *value| {
        const operation_id = try m.id(a);
        const ps = try a.alloc(Value, operation.parameters.len);
        for (operation.parameters, ps) |parameter, *p| p.* = try m.object(a, &.{ try m.member(a, "id", try m.id(a)), try m.member(a, "displayName", try m.string(a, parameter.name)), try m.member(a, "type", try materializer.typeRef(parameter.type)) });
        var members: std.ArrayList(model.Member) = .empty;
        try members.appendSlice(a, &.{ try m.member(a, "id", operation_id), try m.member(a, "displayName", try m.string(a, operation.name)), try m.member(a, "parameters", .{ .array = ps }), try m.member(a, "resultType", try materializer.typeRef(operation.result)), try m.member(a, "effectId", try m.string(a, operation.effect)) });
        if (operation.may_wait) try members.append(a, try m.member(a, "mayWait", .json_true));
        value.* = .{ .object = members.items };
    }
    var members: std.ArrayList(model.Member) = .empty;
    try members.appendSlice(a, &.{ try m.member(a, "id", id), try m.member(a, "displayName", try m.string(a, name)), try m.member(a, "operations", .{ .array = ops }) });
    if (exported) try members.append(a, try m.member(a, "exported", .json_true));
    if (task_bound) try members.append(a, try m.member(a, "taskBound", .json_true));
    try proposals.declare(r, out, .{ .kind = "createCapability", .group = "capabilities", .subject = "capability", .items = &.{.{ .object = members.items }} });
}
