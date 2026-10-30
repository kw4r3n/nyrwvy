//! Type inference of a decoded `simulateCreateFunction(ByName)` body, as `ExpressionRecipeInference`
//! runs it for the function being created, and the result-type check after it
//! (`FunctionCreationResultTypeMismatch`). Each rejection is a `SimulationRejected` reply located at
//! its node (`locate`: ` At body.… (kind).`). A recipe kind not ported yet, a diagnostic that would
//! spell an allocated type-parameter identity, and a body that infers cleanly each answer a fixed
//! `UnsupportedOperation` (see `unported`, `type_parameter_identity`, `not_materialized`).
const std = @import("std");
const model = @import("model_reader");
const query = @import("query.zig");
const recipe = @import("recipe.zig");
const preflight = @import("preflight.zig");

const Allocator = std.mem.Allocator;
const Fail = anyerror;
const TypeRef = recipe.TypeRef;
const Node = recipe.Node;
const Value = query.Value;

/// `UnsupportedOperation` messages; the parity tests tell them apart.
pub const unported = "The Zig host does not infer {s} recipes yet (at {s}).";
pub const type_parameter_identity = "The Zig host does not spell allocated type-parameter identities in diagnostics yet.";
pub const not_materialized = "The Zig host infers simulateCreateFunction bodies but does not materialize proposals yet.";

const description = "the function being created";

fn same(x: []const u8, y: []const u8) bool {
    return std.mem.eql(u8, x, y);
}

fn text(v: Value) []const u8 {
    return model.utf8(model.decoded(v));
}

fn prop(v: Value, name: []const u8) []const u8 {
    return text(model.property(v, name).?);
}

/// The wire spelling with its first letter in the other case: `int64` ⇄ `Int64`, `oneShot` ⇄ `OneShot`.
fn capitalized(a: Allocator, word: []const u8) ![]const u8 {
    const out = try a.dupe(u8, word);
    out[0] = std.ascii.toUpper(out[0]);
    return out;
}

fn uncapitalized(a: Allocator, word: []const u8) ![]const u8 {
    const out = try a.dupe(u8, word);
    out[0] = std.ascii.toLower(out[0]);
    return out;
}

// ---------------------------------------------------------------- types

fn eqlType(x: TypeRef, y: TypeRef) bool {
    if (std.meta.activeTag(x) != std.meta.activeTag(y)) return false;
    return switch (x) {
        .primitive => |p| same(p, y.primitive),
        .type_parameter => |p| same(p, y.type_parameter),
        .capability => |p| same(p, y.capability),
        .named => |n| same(n.id, y.named.id) and eqlTypes(n.arguments, y.named.arguments),
        .function => |f| eqlTypes(f.parameters, y.function.parameters) and eqlType(f.result.*, y.function.result.*),
        .builtin => |b| same(b.generic, y.builtin.generic) and eqlTypes(b.arguments, y.builtin.arguments),
    };
}

fn eqlTypes(xs: []const TypeRef, ys: []const TypeRef) bool {
    if (xs.len != ys.len) return false;
    for (xs, ys) |x, y| if (!eqlType(x, y)) return false;
    return true;
}

fn primitive(name: []const u8) TypeRef {
    return .{ .primitive = name };
}

fn generic(a: Allocator, name: []const u8, arguments: []const TypeRef) !TypeRef {
    return .{ .builtin = .{ .generic = name, .arguments = try a.dupe(TypeRef, arguments) } };
}

fn builtinOf(t: TypeRef, name: []const u8, arity: usize) ?[]const TypeRef {
    if (t != .builtin or !same(t.builtin.generic, name) or t.builtin.arguments.len != arity) return null;
    return t.builtin.arguments;
}

/// A type of the open model (canonical snapshot JSON) as a `TypeRef`.
fn typeOf(a: Allocator, v: Value) Fail!TypeRef {
    const kind = prop(v, "kind");
    if (same(kind, "primitive")) return primitive(prop(v, "primitive"));
    if (same(kind, "named")) return .{ .named = .{ .id = prop(v, "typeId"), .arguments = try typesOf(a, model.arrayItems(v, "arguments")) } };
    if (same(kind, "capability")) return .{ .capability = prop(v, "capabilityId") };
    if (same(kind, "typeParameter")) return .{ .type_parameter = prop(v, "typeParameterId") };
    if (same(kind, "function")) {
        const result = try a.create(TypeRef);
        result.* = try typeOf(a, model.property(v, "resultType").?);
        return .{ .function = .{ .parameters = try typesOf(a, model.arrayItems(v, "parameters")), .result = result } };
    }
    return .{ .builtin = .{ .generic = prop(v, "generic"), .arguments = try typesOf(a, model.arrayItems(v, "arguments")) } };
}

fn typesOf(a: Allocator, items: []const Value) Fail![]const TypeRef {
    const out = try a.alloc(TypeRef, items.len);
    for (items, out) |item, *slot| slot.* = try typeOf(a, item);
    return out;
}

/// `TypeParameters.substitute` and `matchType`, keyed by type-parameter identity.
const Subst = std.StringArrayHashMapUnmanaged(TypeRef);

fn substitute(a: Allocator, s: *const Subst, t: TypeRef) Fail!TypeRef {
    switch (t) {
        .type_parameter => |id| return s.get(id) orelse t,
        .function => |f| {
            const result = try a.create(TypeRef);
            result.* = try substitute(a, s, f.result.*);
            return .{ .function = .{ .parameters = try substituteAll(a, s, f.parameters), .result = result } };
        },
        .builtin => |b| return .{ .builtin = .{ .generic = b.generic, .arguments = try substituteAll(a, s, b.arguments) } },
        .named => |n| return .{ .named = .{ .id = n.id, .arguments = try substituteAll(a, s, n.arguments) } },
        .primitive, .capability => return t,
    }
}

fn substituteAll(a: Allocator, s: *const Subst, items: []const TypeRef) Fail![]const TypeRef {
    const out = try a.alloc(TypeRef, items.len);
    for (items, out) |item, *slot| slot.* = try substitute(a, s, item);
    return out;
}

/// Extends `s` so that `declared` becomes `actual`; false on a conflict (`s` is then discarded).
fn matchType(a: Allocator, s: *Subst, declared: TypeRef, actual: TypeRef) Fail!bool {
    switch (declared) {
        .type_parameter => |id| {
            if (s.get(id)) |bound| return eqlType(bound, actual);
            try s.put(a, id, actual);
            return true;
        },
        .function => |f| if (actual == .function and f.parameters.len == actual.function.parameters.len) {
            if (!try matchAll(a, s, f.parameters, actual.function.parameters)) return false;
            return matchType(a, s, f.result.*, actual.function.result.*);
        },
        .builtin => |b| if (actual == .builtin and same(b.generic, actual.builtin.generic) and b.arguments.len == actual.builtin.arguments.len)
            return matchAll(a, s, b.arguments, actual.builtin.arguments),
        .named => |n| if (actual == .named and same(n.id, actual.named.id) and n.arguments.len == actual.named.arguments.len)
            return matchAll(a, s, n.arguments, actual.named.arguments),
        .primitive, .capability => {},
    }
    return eqlType(declared, actual);
}

fn matchAll(a: Allocator, s: *Subst, declared: []const TypeRef, actual: []const TypeRef) Fail!bool {
    for (declared, actual) |d, x| if (!try matchType(a, s, d, x)) return false;
    return true;
}

// ---------------------------------------------------------------- fixed signatures

const SigType = union(enum) { primitive: []const u8, variable: []const u8, generic: struct { generic: []const u8, arguments: []const SigType } };
const Operand = struct { field: []const u8, side: []const u8, type: SigType, text: []const u8 };
const Equatable = struct { variable: []const u8, code: []const u8, message: []const u8 };
/// One line of `recipe_signatures.txt`, keyed by the F# case name.
const Signature = struct { operands: []const Operand, result: SigType, equatable: ?Equatable };

var signatures: ?std.StringHashMapUnmanaged(Signature) = null;

fn signatureOf(case: []const u8) ?Signature {
    if (signatures == null) signatures = parseSignatures();
    return signatures.?.get(case);
}

fn parseSignatures() std.StringHashMapUnmanaged(Signature) {
    const gpa = std.heap.page_allocator;
    var table: std.StringHashMapUnmanaged(Signature) = .empty;
    var lines = std.mem.tokenizeScalar(u8, @embedFile("recipe_signatures.txt"), '\n');
    while (lines.next()) |line| {
        if (line[0] == '#') continue;
        var parts = std.mem.splitScalar(u8, line, '|');
        const kind = parts.next().?;
        var operands: std.ArrayList(Operand) = .empty;
        var items = std.mem.tokenizeScalar(u8, parts.next().?, ';');
        while (items.next()) |item| {
            var bits = std.mem.splitScalar(u8, item, ':');
            const field = bits.next().?;
            const side = bits.next().?;
            const type_text = bits.next().?;
            operands.append(gpa, .{ .field = field, .side = side, .type = parseSigType(gpa, type_text), .text = type_text }) catch @panic("out of memory");
        }
        const result = parseSigType(gpa, parts.next().?);
        const variable = parts.next().?;
        const code = parts.next().?;
        const message = parts.next().?;
        const equatable: ?Equatable = if (variable.len == 0) null else .{ .variable = variable, .code = code, .message = message };
        table.put(gpa, capitalized(gpa, kind) catch @panic("out of memory"), .{ .operands = operands.items, .result = result, .equatable = equatable }) catch @panic("out of memory");
    }
    return table;
}

/// `OperationSignatures.render` read back: `Map<K, V>`, `List<T>`, `Int64`, `T`.
fn parseSigType(gpa: Allocator, s: []const u8) SigType {
    const open = std.mem.indexOfScalar(u8, s, '<') orelse return if (s.len == 1) .{ .variable = s } else .{ .primitive = uncapitalized(gpa, s) catch @panic("out of memory") };
    const inner = s[open + 1 .. s.len - 1];
    var arguments: std.ArrayList(SigType) = .empty;
    var depth: usize = 0;
    var start: usize = 0;
    for (inner, 0..) |c, i| switch (c) {
        '<' => depth += 1,
        '>' => depth -= 1,
        ',' => if (depth == 0) {
            arguments.append(gpa, parseSigType(gpa, inner[start..i])) catch @panic("out of memory");
            start = i + 2;
        },
        else => {},
    };
    arguments.append(gpa, parseSigType(gpa, inner[start..])) catch @panic("out of memory");
    return .{ .generic = .{ .generic = uncapitalized(gpa, s[0..open]) catch @panic("out of memory"), .arguments = arguments.items } };
}

/// `OperationSignatures.instantiate`: null while a variable is unbound.
fn instantiateSig(a: Allocator, bindings: *const Subst, pattern: SigType) Fail!?TypeRef {
    switch (pattern) {
        .primitive => |p| return primitive(p),
        .variable => |name| return bindings.get(name),
        .generic => |g| {
            const arguments = try a.alloc(TypeRef, g.arguments.len);
            for (g.arguments, arguments) |item, *slot| slot.* = (try instantiateSig(a, bindings, item)) orelse return null;
            return .{ .builtin = .{ .generic = g.generic, .arguments = arguments } };
        },
    }
}

/// `OperationSignatures.bind`: false when `actual` lacks the pattern's shape.
fn bindSig(a: Allocator, bindings: *Subst, pattern: SigType, actual: TypeRef) Fail!bool {
    switch (pattern) {
        .primitive => |p| return eqlType(actual, primitive(p)),
        .variable => |name| {
            if (bindings.get(name)) |bound| return eqlType(bound, actual);
            try bindings.put(a, name, actual);
            return true;
        },
        .generic => |g| {
            const arguments = builtinOf(actual, g.generic, g.arguments.len) orelse return false;
            for (g.arguments, arguments) |item, argument| if (!try bindSig(a, bindings, item, argument)) return false;
            return true;
        },
    }
}

// ---------------------------------------------------------------- scope frames

const Completion = union(enum) { normal: TypeRef, aborts };
const Key = union(enum) { value, err, field: []const u8 };
const Binding = struct { key: Key, type: TypeRef };
const FrameKind = union(enum) { union_arm: []const []const u8, bind_body, option_some, option_none, result_ok, result_err };
/// `PatternTypeFrame`, innermost first.
const Frame = struct { kind: FrameKind, slots: []const Binding, next: ?*const Frame };

fn eqlKey(x: Key, y: Key) bool {
    if (std.meta.activeTag(x) != std.meta.activeTag(y)) return false;
    return x != .field or same(x.field, y.field);
}

const Field = struct { id: []const u8, name: []const u8, type: TypeRef };
const Variant = struct { id: []const u8, name: []const u8, fields: []const Field };
/// A `MatchUnion` arm resolved: the variants it covers, its body and whether the body sees the fields.
const ResolvedArm = struct { variants: []const Variant, body: *const Node, binds_fields: bool, index: usize };

// ---------------------------------------------------------------- inference

const Infer = struct {
    r: *query.Request,
    a: Allocator,
    c: preflight.Creation,
    /// The request's own type parameters (declared or derived), fresh identities in F#.
    local: []const []const u8,
    /// The innermost node whose own check produced the propagating diagnostic (`track`), located by
    /// `run`: a node that reports another diagnostic than the one recorded displaces it.
    failed: ?*const Node = null,
    failed_diagnostic: query.Diagnostic = .{},
    /// `InferenceCache`: each node's successful result under the frames it was inferred in. A node is
    /// inferred along one path, so one entry per node suffices; without it a generic constructor's
    /// field values (derived type arguments, then the bindings) would be inferred twice per level.
    values: std.AutoHashMapUnmanaged(*const Node, Cached(TypeRef)) = .empty,
    completions: std.AutoHashMapUnmanaged(*const Node, Cached(Completion)) = .empty,
    one_shot_requirements: ?[]const model.OneShotRequirement = null,

    fn Cached(comptime T: type) type {
        return struct { frames: ?*const Frame, value: T };
    }

    fn reject(self: *Infer, code: []const u8, message: []const u8) Fail {
        return preflight.rejected(self.r, code, message);
    }

    fn unsupported(self: *Infer, message: []const u8) Fail {
        self.r.rejection = .{ .code = "UnsupportedOperation", .message = message };
        return error.Unsupported;
    }

    fn fmt(self: *Infer, comptime format: []const u8, args: anytype) Fail![]const u8 {
        return std.fmt.allocPrint(self.a, format, args);
    }

    fn typeDeclaration(self: *Infer, id: []const u8) ?Value {
        return preflight.declaration(self.r, "types", id);
    }

    // ------------------------------------------------------------ rendering

    const Mode = enum { owner, with_ids };

    /// `TypeParameters.names snapshot`: the display name of a declared type parameter.
    fn declaredTypeParameterName(self: *Infer, id: []const u8) ?[]const u8 {
        for ([_][]const u8{ "types", "functions" }) |group| for (model.arrayItems(self.r.model, group)) |item| for (model.arrayItems(item, "typeParameters")) |p| {
            if (same(prop(p, "typeParameterId"), id)) return query.nameOf(p);
        };
        return null;
    }

    /// `DiagnosticText.render` with the owner's type-parameter names (`typeRefIn owner.TypeParameters`,
    /// every creation diagnostic since PR #571); `with_ids` appends each type parameter's ID.
    fn render(self: *Infer, t: TypeRef, mode: Mode) Fail![]const u8 {
        switch (t) {
            .primitive => |p| return capitalized(self.a, p),
            .named => |n| {
                const name = if (self.typeDeclaration(n.id)) |d| query.nameOf(d) else try self.fmt("type '{s}'", .{n.id});
                return if (n.arguments.len == 0) name else self.renderGeneric(name, n.arguments, mode);
            },
            .capability => |id| return if (preflight.declaration(self.r, "capabilities", id)) |d| query.nameOf(d) else try self.fmt("capability '{s}'", .{id}),
            .function => |f| return self.fmt("({s}) -> {s}", .{ try self.renderList(f.parameters, mode), try self.render(f.result.*, mode) }),
            .type_parameter => |id| {
                // The request's type parameters are fresh identities in F#; a message spelling one is not answered.
                if (preflight.contains(self.local, id)) return if (mode == .with_ids) self.unsupported(type_parameter_identity) else id;
                const name = self.declaredTypeParameterName(id) orelse id;
                return if (mode == .with_ids) self.fmt("{s} (type parameter {s})", .{ name, id }) else name;
            },
            .builtin => |b| {
                const name = try capitalized(self.a, b.generic);
                if (b.arguments.len == 0 and (same(b.generic, "exception") or same(b.generic, "taskScope"))) return name;
                return self.renderGeneric(name, b.arguments, mode);
            },
        }
    }

    fn renderGeneric(self: *Infer, name: []const u8, arguments: []const TypeRef, mode: Mode) Fail![]const u8 {
        return self.fmt("{s}<{s}>", .{ name, try self.renderList(arguments, mode) });
    }

    fn renderList(self: *Infer, items: []const TypeRef, mode: Mode) Fail![]const u8 {
        var texts: std.ArrayList([]const u8) = .empty;
        for (items) |item| try texts.append(self.a, try self.render(item, mode));
        return std.mem.join(self.a, ", ", texts.items);
    }

    fn ty(self: *Infer, t: TypeRef) Fail![]const u8 {
        return self.render(t, .owner);
    }

    /// `typeRefPairIn`: both texts, with type-parameter IDs when they would otherwise read the same.
    fn tyPair(self: *Infer, expected: TypeRef, actual: TypeRef) Fail![2][]const u8 {
        const expected_text = try self.ty(expected);
        const actual_text = try self.ty(actual);
        if (same(expected_text, actual_text) and !eqlType(expected, actual)) return .{ try self.render(expected, .with_ids), try self.render(actual, .with_ids) };
        return .{ expected_text, actual_text };
    }

    /// `StableId.describe`: `'name' (id)`.
    fn describe(self: *Infer, name: []const u8, id: []const u8) Fail![]const u8 {
        return self.fmt("'{s}' ({s})", .{ name, id });
    }

    /// `DiagnosticText.namedType`.
    fn namedType(self: *Infer, id: []const u8) Fail![]const u8 {
        return if (self.typeDeclaration(id)) |d| self.describe(query.nameOf(d), id) else self.fmt("type '{s}'", .{id});
    }

    /// `DiagnosticText.typeRefWithIdIn owner.TypeParameters`.
    fn typeRefWithId(self: *Infer, t: TypeRef) Fail![]const u8 {
        if (t == .named and t.named.arguments.len == 0) return self.namedType(t.named.id);
        return self.ty(t);
    }

    /// `DiagnosticText.completionPairIn owner.TypeParameters`.
    fn completionPair(self: *Infer, left: Completion, right: Completion) Fail![2][]const u8 {
        if (left == .normal and right == .normal) return self.tyPair(left.normal, right.normal);
        return .{ try self.completion(left), try self.completion(right) };
    }

    fn completion(self: *Infer, c: Completion) Fail![]const u8 {
        return switch (c) {
            .normal => |t| self.ty(t),
            .aborts => "no value (the expression always aborts)",
        };
    }

    // ------------------------------------------------------------ equatable types

    /// `EquatableTypes.describeIn`: display names, a bare ID for an unknown type, `capability 'id'`.
    fn describeEq(self: *Infer, t: TypeRef) Fail![]const u8 {
        switch (t) {
            .primitive => |p| return capitalized(self.a, p),
            .named => |n| {
                const name = if (self.typeDeclaration(n.id)) |d| query.nameOf(d) else n.id;
                return if (n.arguments.len == 0) name else self.fmt("{s}<{s}>", .{ name, try self.describeEqList(n.arguments) });
            },
            .capability => |id| return self.fmt("capability '{s}'", .{id}),
            .function => |f| return self.fmt("({s}) -> {s}", .{ try self.describeEqList(f.parameters), try self.describeEq(f.result.*) }),
            .builtin => |b| {
                if (same(b.generic, "taskScope")) return "TaskScope";
                return self.fmt("{s}<{s}>", .{ try capitalized(self.a, b.generic), try self.describeEqList(b.arguments) });
            },
            .type_parameter => |id| return if (preflight.contains(self.local, id)) id else self.declaredTypeParameterName(id) orelse id,
        }
    }

    fn describeEqList(self: *Infer, items: []const TypeRef) Fail![]const u8 {
        var texts: std.ArrayList([]const u8) = .empty;
        for (items) |item| try texts.append(self.a, try self.describeEq(item));
        return std.mem.join(self.a, ", ", texts.items);
    }

    const NotEquatable = struct { path: []const []const u8, offending: TypeRef };

    /// `EquatableTypes.firstNotEquatable`: the path to the first part that is not equatable.
    fn firstNotEquatable(self: *Infer, t: TypeRef) Fail!?NotEquatable {
        var visited: std.ArrayList(TypeRef) = .empty;
        return self.visitEquatable(&visited, &.{}, t);
    }

    fn visitEquatable(self: *Infer, visited: *std.ArrayList(TypeRef), path: []const []const u8, t: TypeRef) Fail!?NotEquatable {
        const a = self.a;
        switch (t) {
            .primitive => |p| return if (same(p, "float64")) .{ .path = path, .offending = t } else null,
            .function, .capability, .type_parameter => return .{ .path = path, .offending = t },
            .builtin => |b| {
                if (same(b.generic, "exception") or same(b.generic, "oneShot") or same(b.generic, "task") or same(b.generic, "taskScope")) return .{ .path = path, .offending = t };
                for (b.arguments, 0..) |argument, index| {
                    const step = if (same(b.generic, "list")) "List item" else if (same(b.generic, "set")) "Set item" else if (same(b.generic, "option")) "Option value" else if (same(b.generic, "map")) (if (index == 0) "Map key" else "Map value") else if (index == 0) "Result ok" else "Result err";
                    if (try self.visitEquatable(visited, try appended(a, path, step), argument)) |found| return found;
                }
                return null;
            },
            .named => |n| {
                for (visited.items) |seen| if (eqlType(seen, t)) return null;
                try visited.append(a, t);
                const declared = self.typeDeclaration(n.id) orelse return null;
                const type_name = query.nameOf(declared);
                if (model.property(declared, "variants") != null) {
                    for (model.arrayItems(declared, "variants")) |variant| for (model.arrayItems(variant, "fields")) |field| {
                        const step = try self.fmt("{s}.{s}.{s}", .{ type_name, query.nameOf(variant), query.nameOf(field) });
                        if (try self.visitEquatable(visited, try appended(a, path, step), try self.instantiate(declared, n.arguments, try typeOf(a, model.property(field, "type").?)))) |found| return found;
                    };
                } else for (model.arrayItems(declared, "fields")) |field| {
                    const step = try self.fmt("{s}.{s}", .{ type_name, query.nameOf(field) });
                    if (try self.visitEquatable(visited, try appended(a, path, step), try self.instantiate(declared, n.arguments, try typeOf(a, model.property(field, "type").?)))) |found| return found;
                }
                return null;
            },
        }
    }

    fn isEquatable(self: *Infer, t: TypeRef) Fail!bool {
        return (try self.firstNotEquatable(t)) == null;
    }

    /// `EquatableTypes.explainIn`.
    fn explainEquatable(self: *Infer, t: TypeRef) Fail![]const u8 {
        const found = (try self.firstNotEquatable(t)) orelse return self.fmt("{s} is equatable.", .{try self.describeEq(t)});
        const reason: []const u8 = switch (found.offending) {
            .primitive => "Float64 is not equatable because of NaN; compare with Float64Equal",
            .function => "function values are not equatable",
            .builtin => "runtime handles and scopes are not equatable",
            .capability => "capability values are not equatable",
            .type_parameter => "type parameters are never equatable, because nothing constrains their type arguments, so a Set item or Map key type must be concrete",
            .named => "it is not equatable",
        };
        const location = if (found.path.len == 0) "" else try self.fmt(" at {s}", .{try std.mem.join(self.a, " -> ", found.path)});
        return self.fmt("{s} is not equatable: {s}{s}; {s}.", .{ try self.describeEq(t), try self.describeEq(found.offending), location, reason });
    }

    // ------------------------------------------------------------ declared types

    /// `TypeParameters.instantiate`: a field type as seen through a reference with these arguments.
    fn instantiate(self: *Infer, declared: Value, arguments: []const TypeRef, t: TypeRef) Fail!TypeRef {
        const parameters = model.arrayItems(declared, "typeParameters");
        if (parameters.len == 0) return t;
        var s: Subst = .empty;
        for (parameters, 0..) |p, index| {
            if (index >= arguments.len) break;
            try s.put(self.a, prop(p, "typeParameterId"), arguments[index]);
        }
        return substitute(self.a, &s, t);
    }

    fn fieldsOf(self: *Infer, declared: Value, owner: Value, arguments: []const TypeRef) Fail![]const Field {
        const items = model.arrayItems(owner, "fields");
        const out = try self.a.alloc(Field, items.len);
        for (items, out) |item, *slot| slot.* = .{ .id = query.idOf(item), .name = query.nameOf(item), .type = try self.instantiate(declared, arguments, try typeOf(self.a, model.property(item, "type").?)) };
        return out;
    }

    fn variantsOf(self: *Infer, declared: Value, arguments: []const TypeRef) Fail![]const Variant {
        const items = model.arrayItems(declared, "variants");
        const out = try self.a.alloc(Variant, items.len);
        for (items, out) |item, *slot| slot.* = .{ .id = query.idOf(item), .name = query.nameOf(item), .fields = try self.fieldsOf(declared, item, arguments) };
        return out;
    }

    /// `describeOwnerParameters` for the function being created: slots have no IDs yet.
    fn describeOwnerParameters(self: *Infer) Fail![]const u8 {
        if (self.c.parameters.len == 0) return self.fmt("{s} has no parameters.", .{description});
        var entries: std.ArrayList([]const u8) = .empty;
        for (self.c.parameters, 0..) |p, slot| try entries.append(self.a, try self.fmt("{d} = '{s}'", .{ slot, p.name }));
        return self.fmt("Parameters of {s}: {s}.", .{ description, try std.mem.join(self.a, ", ", entries.items) });
    }

    // ------------------------------------------------------------ pattern references

    const FieldOwner = struct { field: Value, owner: []const u8 };

    /// `tryFindFieldWithOwner`.
    fn fieldWithOwner(self: *Infer, id: []const u8) Fail!?FieldOwner {
        for (model.arrayItems(self.r.model, "types")) |t| {
            if (model.property(t, "variants") != null) {
                for (model.arrayItems(t, "variants")) |variant| for (model.arrayItems(variant, "fields")) |field| if (same(query.idOf(field), id))
                    return .{ .field = field, .owner = try self.fmt("variant '{s}' of '{s}'", .{ query.nameOf(variant), query.nameOf(t) }) };
            } else for (model.arrayItems(t, "fields")) |field| if (same(query.idOf(field), id))
                return .{ .field = field, .owner = try self.fmt("product '{s}'", .{query.nameOf(t)}) };
        }
        return null;
    }

    fn describeSlot(self: *Infer, key: Key) Fail![]const u8 {
        switch (key) {
            .value => return "Value",
            .err => return "Error",
            .field => |id| {
                const found = (try self.fieldWithOwner(id)) orelse return self.fmt("field '{s}' (not a declared field)", .{id});
                return self.fmt("field {s} of {s}", .{ try self.describe(query.nameOf(found.field), id), found.owner });
            },
        }
    }

    fn shortSlotName(self: *Infer, key: Key) Fail![]const u8 {
        switch (key) {
            .value => return "Value",
            .err => return "Error",
            .field => |id| return self.fmt("'{s}'", .{if (try self.fieldWithOwner(id)) |found| query.nameOf(found.field) else id}),
        }
    }

    /// `describeScopeFrame`: `arm 'Stop' of MatchUnion on 'Action' (binds 'member')`.
    fn describeFrame(self: *Infer, frame: *const Frame) Fail![]const u8 {
        const opener: []const u8 = switch (frame.kind) {
            .union_arm => |ids| blk: {
                for (model.arrayItems(self.r.model, "types")) |t| {
                    if (model.property(t, "variants") == null) continue;
                    var names: std.ArrayList([]const u8) = .empty;
                    for (model.arrayItems(t, "variants")) |variant| if (preflight.contains(ids, query.idOf(variant))) try names.append(self.a, query.nameOf(variant));
                    if (names.items.len > 0) break :blk try self.fmt("arm '{s}' of MatchUnion on '{s}'", .{ try std.mem.join(self.a, "|", names.items), query.nameOf(t) });
                }
                break :blk try self.fmt("arm '{s}' of MatchUnion", .{try std.mem.join(self.a, "|", ids)});
            },
            .bind_body => "Bind body",
            .option_some => "Some arm of MatchOption",
            .option_none => "None arm of MatchOption",
            .result_ok => "Ok arm of MatchResult",
            .result_err => "Err arm of MatchResult",
        };
        var bound: std.ArrayList([]const u8) = .empty;
        for (frame.slots) |slot| try bound.append(self.a, try self.shortSlotName(slot.key));
        const bindings = if (bound.items.len == 0) "no bindings" else try self.fmt("binds {s}", .{try std.mem.join(self.a, ", ", bound.items)});
        return self.fmt("{s} ({s})", .{ opener, bindings });
    }

    /// `describeVisibleFrames`.
    fn describeVisibleFrames(self: *Infer, frames: ?*const Frame, key: Key) Fail![]const u8 {
        var entries: std.ArrayList([]const u8) = .empty;
        var found: std.ArrayList([]const u8) = .empty;
        var next = frames;
        var depth: usize = 0;
        while (next) |frame| : (next = frame.next) {
            try entries.append(self.a, try self.fmt("depth {d} = {s}", .{ depth, try self.describeFrame(frame) }));
            for (frame.slots) |slot| if (eqlKey(slot.key, key)) {
                try found.append(self.a, try self.fmt("{d}", .{depth}));
                break;
            };
            depth += 1;
        }
        const listing = if (entries.items.len == 0) "No frames are visible here; only a Bind body or a match arm opens one." else try self.fmt("Visible frames, innermost first: {s}.", .{try std.mem.join(self.a, "; ", entries.items)});
        return switch (found.items.len) {
            0 => listing,
            1 => self.fmt("The slot is found at depth {s}. {s}", .{ found.items[0], listing }),
            else => self.fmt("The slot is found at depths {s}; choose the frame that binds the intended value. Empty frames still count toward scope depth. {s}", .{ try std.mem.join(self.a, ", ", found.items), listing }),
        };
    }

    /// `resolvePatternType`.
    fn resolvePattern(self: *Infer, frames: ?*const Frame, node: *const Node) Fail!TypeRef {
        const pattern = node.slots[0].data.pattern;
        const key: Key = if (same(pattern.slot, "value")) .value else if (same(pattern.slot, "error")) .err else .{ .field = pattern.field_id.? };
        const depth = node.slots[1].data.int;
        if (depth < 0) return self.reject("PureExpressionPatternScopeDepthInvalid", try self.fmt("Pattern-reference scope depth must be non-negative (slot {s}). {s}", .{ try self.describeSlot(key), try self.describeVisibleFrames(frames, key) }));
        var next = frames;
        var remaining = depth;
        const frame = while (next) |frame| : (next = frame.next) {
            if (remaining == 0) break frame;
            remaining -= 1;
        } else return self.reject("PureExpressionPatternScopeNotFound", try self.fmt("Pattern-reference scope depth {d} for slot {s} is not visible here. {s}", .{ depth, try self.describeSlot(key), try self.describeVisibleFrames(frames, key) }));
        for (frame.slots) |slot| if (eqlKey(slot.key, key)) return slot.type;
        return self.reject("PureExpressionPatternSlotNotFound", try self.fmt("Pattern-reference slot {s} does not exist in scope depth {d}. {s}", .{ try self.describeSlot(key), depth, try self.describeVisibleFrames(frames, key) }));
    }

    // ------------------------------------------------------------ nodes

    /// `infer`: the first failing node is remembered for `locate`.
    fn infer(self: *Infer, frames: ?*const Frame, node: *const Node) Fail!TypeRef {
        if (self.values.get(node)) |known| if (known.frames == frames) return known.value;
        const value = self.inferNode(frames, node) catch |e| {
            if (e == error.Rejected) self.track(node);
            return e;
        };
        try self.values.put(self.a, node, .{ .frames = frames, .value = value });
        return value;
    }

    fn inferCompletion(self: *Infer, frames: ?*const Frame, node: *const Node) Fail!Completion {
        if (self.completions.get(node)) |known| if (known.frames == frames) return known.value;
        const value = self.inferCompletionNode(frames, node) catch |e| {
            if (e == error.Rejected) self.track(node);
            return e;
        };
        try self.completions.put(self.a, node, .{ .frames = frames, .value = value });
        return value;
    }

    fn track(self: *Infer, node: *const Node) void {
        const d = self.r.diagnostic;
        if (self.failed != null and same(self.failed_diagnostic.code, d.code) and same(self.failed_diagnostic.message, d.message)) return;
        self.failed = node;
        self.failed_diagnostic = d;
    }

    fn typeMismatch(self: *Infer, role: []const u8, side: []const u8, expected: TypeRef, actual: TypeRef) Fail {
        const pair = try self.tyPair(expected, actual);
        return self.reject("PureExpressionOperandTypeMismatch", try self.fmt("{s} {s} operand requires {s}, but the recipe produces {s}.", .{ role, side, pair[0], pair[1] }));
    }

    fn child(node: *const Node, index: usize) *const Node {
        return node.slots[index].data.recipe;
    }

    fn inferNode(self: *Infer, frames: ?*const Frame, node: *const Node) Fail!TypeRef {
        const a = self.a;
        const case = node.case;
        if (signatureOf(case)) |entry| return self.fixedSignature(frames, node, entry);
        if (same(case, "OriginalTarget")) return self.reject("OriginalTargetOutsideTargetedReplacement", "originalTarget is accepted only in the replacement of a targeted replacement.");
        if (same(case, "Parameter")) {
            // The function being created owns no parameter by ID yet.
            const id = node.slots[0].data.id;
            const foreign = blk: {
                for (model.arrayItems(self.r.model, "functions")) |f| for (model.arrayItems(f, "parameters")) |p| if (same(query.idOf(p), id))
                    break :blk try self.fmt("Parameter {s} of function {s}", .{ try self.describe(query.nameOf(p), id), try self.describe(query.nameOf(f), query.idOf(f)) });
                break :blk try self.fmt("Parameter '{s}'", .{id});
            };
            return self.reject("PureExpressionParameterOutOfScope", try self.fmt("{s} is not owned by {s}. {s}", .{ foreign, description, try self.describeOwnerParameters() }));
        }
        if (same(case, "ParameterSlot")) {
            const index = node.slots[0].data.int;
            if (index < 0 or index >= self.c.parameters.len) return self.reject("PureExpressionParameterSlotOutOfRange", try self.fmt("Parameter slot {d} is outside the requested parameter list. {s}", .{ index, try self.describeOwnerParameters() }));
            return self.c.parameters[@intCast(index)].type;
        }
        if (same(case, "PatternReference")) return self.resolvePattern(frames, node);
        if (same(case, "StringLiteral")) return primitive("string");
        if (same(case, "BoolLiteral")) return primitive("bool");
        if (same(case, "UnitLiteral")) return primitive("unit");
        if (same(case, "Int64Literal")) return primitive("int64");
        if (same(case, "Float64Literal")) return primitive("float64");
        if (same(case, "BoolAnd") or same(case, "BoolOr")) {
            const left = try self.infer(frames, child(node, 0));
            if (!eqlType(left, primitive("bool"))) return self.typeMismatch(case, "left", primitive("bool"), left);
            // The right operand is a nested branch: it may abort, otherwise it must be Bool.
            const right = try self.inferCompletion(frames, child(node, 1));
            if (right == .normal and !eqlType(right.normal, primitive("bool"))) return self.typeMismatch(case, "right", primitive("bool"), right.normal);
            return primitive("bool");
        }
        if (same(case, "ValueEqual")) {
            const left = try self.infer(frames, child(node, 0));
            const right = try self.infer(frames, child(node, 1));
            if (!eqlType(right, left)) return self.typeMismatch("ValueEqual", "right", left, right);
            if (!try self.isEquatable(left)) return self.reject("ValueEqualTypeNotEquatable", try self.fmt("ValueEqual compares values of a type that is not equatable: {s}", .{try self.explainEquatable(left)}));
            return primitive("bool");
        }
        if (same(case, "ListEmpty")) {
            const item = node.slots[0].data.type;
            try preflight.validateTypeRef(self.r, item);
            return generic(a, "list", &.{item});
        }
        if (same(case, "SetEmpty")) {
            const item = node.slots[0].data.type;
            try preflight.validateTypeRef(self.r, item);
            if (!try self.isEquatable(item)) return self.reject("SetItemTypeNotEquatable", try self.fmt("Set item type is not equatable: {s}", .{try self.explainEquatable(item)}));
            return generic(a, "set", &.{item});
        }
        if (same(case, "MapEmpty")) {
            const key = node.slots[0].data.type;
            const value = node.slots[1].data.type;
            try preflight.validateTypeRef(self.r, key);
            try preflight.validateTypeRef(self.r, value);
            if (!try self.isEquatable(key)) return self.reject("MapKeyTypeNotEquatable", try self.fmt("Map key type is not equatable: {s}", .{try self.explainEquatable(key)}));
            return generic(a, "map", &.{ key, value });
        }
        if (same(case, "ListConstruct")) {
            const item = node.slots[0].data.type;
            try preflight.validateTypeRef(self.r, item);
            for (node.slots[1].data.recipes) |element| {
                const actual = try self.infer(frames, element);
                if (!eqlType(actual, item)) return self.typeMismatch("ListConstruct", "item", item, actual);
            }
            return generic(a, "list", &.{item});
        }
        if (same(case, "ProductConstruct")) {
            const id = node.slots[0].data.id;
            const declared = self.typeDeclaration(id) orelse return self.reject("PureExpressionProductTypeNotFound", try self.fmt("Product construction references unknown type '{s}'.", .{id}));
            if (model.property(declared, "variants") != null) return self.reject("PureExpressionProductTargetMismatch", try self.fmt("Product construction target '{s}' is not a product type.", .{id}));
            const fields = node.slots[2].data.pairs;
            const arguments = try self.typeArgumentsFor(frames, "ProductConstruct", declared, try self.fieldsOf(declared, declared, &.{}), node.slots[1].data.types, fields);
            try self.validateFieldBindings(frames, "ProductConstruct", try self.fieldsOf(declared, declared, arguments), fields);
            return .{ .named = .{ .id = id, .arguments = arguments } };
        }
        if (same(case, "FieldAccess")) {
            const id = node.slots[1].data.id;
            const owner, const field = blk: {
                for (model.arrayItems(self.r.model, "types")) |t| {
                    if (model.property(t, "variants") != null) continue;
                    for (model.arrayItems(t, "fields")) |field| if (same(query.idOf(field), id)) break :blk .{ t, field };
                }
                return self.reject("PureExpressionProductFieldNotFound", try self.fmt("Field access references field '{s}' that is not owned by a product type.", .{id}));
            };
            const source = try self.infer(frames, child(node, 0));
            if (source == .named and same(source.named.id, query.idOf(owner))) return self.instantiate(owner, source.named.arguments, try typeOf(a, model.property(field, "type").?));
            return self.reject("PureExpressionFieldAccessTypeMismatch", try self.fmt("Field access to {s} requires {s}, but the source recipe produces {s}.", .{ try self.describe(query.nameOf(field), id), try self.namedType(query.idOf(owner)), try self.typeRefWithId(source) }));
        }
        if (same(case, "ProductUpdate")) {
            const id = node.slots[0].data.id;
            const declared = self.typeDeclaration(id) orelse return self.reject("PureExpressionProductTypeNotFound", try self.fmt("Product update references unknown type '{s}'.", .{id}));
            if (model.property(declared, "variants") != null) return self.reject("PureExpressionProductTargetMismatch", try self.fmt("Product update target '{s}' is not a product type.", .{id}));
            const fields = node.slots[2].data.pairs;
            if (fields.len == 0) return self.reject("PureExpressionEmptyProductUpdate", try self.fmt("Product update of {s} must replace at least one field.", .{try self.namedType(id)}));
            const declared_fields = try self.fieldsOf(declared, declared, &.{});
            for (fields) |pair| if (findField(declared_fields, pair.id) == null)
                return self.reject("PureExpressionFieldBindingMismatch", try self.fmt("Product update of {s} binds field '{s}', which that type does not declare.", .{ try self.namedType(id), pair.id }));
            const source = try self.infer(frames, child(node, 1));
            if (source != .named or !same(source.named.id, id))
                return self.reject("PureExpressionProductUpdateSourceMismatch", try self.fmt("Product update of {s} requires a source of that type, but the source recipe produces {s}.", .{ try self.namedType(id), try self.typeRefWithId(source) }));
            var updated: std.ArrayList(Field) = .empty;
            for (try self.fieldsOf(declared, declared, source.named.arguments)) |field| for (fields) |pair| if (same(pair.id, field.id)) {
                try updated.append(a, field);
                break;
            };
            try self.validateFieldBindings(frames, "ProductUpdate", updated.items, fields);
            return source;
        }
        if (same(case, "UnionConstruct")) {
            const id = node.slots[0].data.id;
            const owner, const variant = blk: {
                for (model.arrayItems(self.r.model, "types")) |t| for (model.arrayItems(t, "variants")) |variant| if (same(query.idOf(variant), id)) break :blk .{ t, variant };
                return self.reject("PureExpressionUnionVariantNotFound", try self.fmt("Union construction references unknown variant '{s}'.", .{id}));
            };
            const fields = node.slots[2].data.pairs;
            const arguments = try self.typeArgumentsFor(frames, "UnionConstruct", owner, try self.fieldsOf(owner, variant, &.{}), node.slots[1].data.types, fields);
            try self.validateFieldBindings(frames, "UnionConstruct", try self.fieldsOf(owner, variant, arguments), fields);
            return .{ .named = .{ .id = query.idOf(owner), .arguments = arguments } };
        }
        if (same(case, "OptionNone")) {
            const item = node.slots[0].data.type;
            try preflight.validateTypeRef(self.r, item);
            return generic(a, "option", &.{item});
        }
        if (same(case, "ResultOk")) {
            const err = node.slots[0].data.type;
            try preflight.validateTypeRef(self.r, err);
            return generic(a, "result", &.{ try self.infer(frames, child(node, 1)), err });
        }
        if (same(case, "ResultErr")) {
            const ok = node.slots[0].data.type;
            try preflight.validateTypeRef(self.r, ok);
            return generic(a, "result", &.{ ok, try self.infer(frames, child(node, 1)) });
        }
        if (isCompletionKind(case)) {
            return switch (try self.inferCompletion(frames, node)) {
                .normal => |t| t,
                .aborts => self.reject("AbortValueUse", try self.fmt("An abrupt expression cannot be used as a value in {s}.", .{description})),
            };
        }
        if (try self.inferMore(frames, node)) |t| return t;
        return self.unsupported(try self.fmt(unported, .{ try uncapitalized(a, case), (try recipePath(a, "body", self.c.body, node)).? }));
    }

    fn isCompletionKind(case: []const u8) bool {
        for ([_][]const u8{ "Abort", "Conditional", "MatchUnion", "Bind", "MatchOption", "MatchResult" }) |kind| if (same(case, kind)) return true;
        return false;
    }

    /// `fixedSignature`: `OperationSignatures.check` over the node's children in order.
    fn fixedSignature(self: *Infer, frames: ?*const Frame, node: *const Node, entry: Signature) Fail!TypeRef {
        const a = self.a;
        const role = node.case;
        var bindings: Subst = .empty;
        for (entry.operands, try recipe.children(a, node)) |operand, operand_node| {
            const actual = try self.infer(frames, operand_node);
            if (try instantiateSig(a, &bindings, operand.type)) |expected| {
                if (!eqlType(expected, actual)) return self.typeMismatch(role, operand.side, expected, actual);
                continue;
            }
            var extended = try bindings.clone(a);
            if (!try bindSig(a, &extended, operand.type, actual))
                return self.reject("PureExpressionOperandTypeMismatch", try self.fmt("{s} {s} operand requires {s}, but the recipe produces {s}.", .{ role, operand.side, operand.text, try self.ty(actual) }));
            // The equatable constraint is checked as soon as an operand binds its variable.
            if (entry.equatable) |constraint| if (!bindings.contains(constraint.variable)) if (extended.get(constraint.variable)) |bound| if (!try self.isEquatable(bound))
                return self.reject(constraint.code, try self.fmt("{s}{s}", .{ constraint.message, try self.explainEquatable(bound) }));
            bindings = extended;
        }
        return (try instantiateSig(a, &bindings, entry.result)).?;
    }

    fn findField(fields: []const Field, id: []const u8) ?Field {
        for (fields) |field| if (same(field.id, id)) return field;
        return null;
    }

    /// `validateFieldBindings`.
    fn validateFieldBindings(self: *Infer, frames: ?*const Frame, role: []const u8, expected: []const Field, bindings: []const recipe.Pair) Fail!void {
        var ids: std.ArrayList([]const u8) = .empty;
        for (bindings) |pair| try ids.append(self.a, pair.id);
        if (preflight.repeated(ids.items)) |id| return self.reject("PureExpressionDuplicateFieldBinding", try self.fmt("{s} binds field '{s}' more than once.", .{ role, id }));
        var matched = expected.len == bindings.len;
        for (expected) |field| if (!preflight.contains(ids.items, field.id)) {
            matched = false;
        };
        if (!matched) return self.reject("PureExpressionFieldBindingMismatch", try self.fmt("{s} must bind exactly the fields declared by its target.", .{role}));
        for (expected) |field| {
            const value = for (bindings) |pair| {
                if (same(pair.id, field.id)) break pair.value;
            } else unreachable;
            const actual = try self.infer(frames, value);
            if (!eqlType(actual, field.type)) {
                const pair = try self.tyPair(field.type, actual);
                return self.reject("PureExpressionFieldBindingTypeMismatch", try self.fmt("{s} field {s} requires {s}, but the recipe produces {s}.", .{ role, try self.describe(field.name, field.id), pair[0], pair[1] }));
            }
        }
    }

    /// `typeArgumentsFor`: the given type arguments, checked, or the ones the field values determine.
    fn typeArgumentsFor(self: *Infer, frames: ?*const Frame, role: []const u8, owner: Value, declared_fields: []const Field, given: []const TypeRef, bindings: []const recipe.Pair) Fail![]const TypeRef {
        const a = self.a;
        const parameters = model.arrayItems(owner, "typeParameters");
        const owner_name = query.nameOf(owner);
        if (parameters.len == 0 and given.len == 0) return &.{};
        if (given.len > 0) {
            if (given.len != parameters.len) return self.reject("PureExpressionTypeArgumentArityMismatch", try self.fmt("{s} of {s} gives {d} type arguments; {s} declares {d}.", .{ role, owner_name, given.len, owner_name, parameters.len }));
            for (given) |t| try preflight.validateTypeRef(self.r, t);
            return given;
        }
        var s: Subst = .empty;
        for (declared_fields) |field| {
            const value = for (bindings) |pair| {
                if (same(pair.id, field.id)) break pair.value;
            } else continue;
            const actual = try self.infer(frames, value);
            var attempt = try s.clone(a);
            if (!try matchType(a, &attempt, field.type, actual)) {
                const pair = try self.tyPair(try substitute(a, &s, field.type), actual);
                return self.reject("PureExpressionFieldBindingTypeMismatch", try self.fmt("{s} field {s} requires {s}, but the recipe produces {s}.", .{ role, try self.describe(field.name, field.id), pair[0], pair[1] }));
            }
            s = attempt;
        }
        var missing: std.ArrayList([]const u8) = .empty;
        var arguments: std.ArrayList(TypeRef) = .empty;
        for (parameters) |p| {
            if (s.get(prop(p, "typeParameterId"))) |bound| try arguments.append(a, bound) else try missing.append(a, query.nameOf(p));
        }
        if (missing.items.len > 0) return self.reject("PureExpressionTypeArgumentsRequired", try self.fmt("{s} of {s} cannot derive type parameter {s} from its field values; give typeArguments.", .{ role, owner_name, try std.mem.join(a, ", ", missing.items) }));
        return arguments.items;
    }

    // ------------------------------------------------------------ functions and effects

    const Param = struct { id: []const u8, name: []const u8, type: TypeRef };
    /// A declared function as inference reads it.
    const Function = struct { id: []const u8, name: []const u8, parameters: []const Param, result: TypeRef, effects: []const []const u8, generic: bool };

    fn functionOf(self: *Infer, id: []const u8) Fail!?Function {
        const declared = preflight.declaration(self.r, "functions", id) orelse return null;
        var effects: std.ArrayList([]const u8) = .empty;
        for (model.arrayItems(declared, "requiredEffects")) |e| try effects.append(self.a, text(e));
        return .{ .id = id, .name = query.nameOf(declared), .parameters = try self.paramsOf(declared), .result = try typeOf(self.a, model.property(declared, "resultType").?), .effects = effects.items, .generic = model.arrayItems(declared, "typeParameters").len > 0 };
    }

    fn paramsOf(self: *Infer, declared: Value) Fail![]const Param {
        const items = model.arrayItems(declared, "parameters");
        const out = try self.a.alloc(Param, items.len);
        for (items, out) |item, *slot| slot.* = .{ .id = query.idOf(item), .name = query.nameOf(item), .type = try typeOf(self.a, model.property(item, "type").?) };
        return out;
    }

    /// `DiagnosticText.functionName`: `'name' (id)`.
    fn callbackName(self: *Infer, f: Function) Fail![]const u8 {
        return self.describe(f.name, f.id);
    }

    /// `DiagnosticText.signature`: `(line: String, acc: Int64) -> Int64`.
    fn callbackSignature(self: *Infer, f: Function) Fail![]const u8 {
        var parts: std.ArrayList([]const u8) = .empty;
        for (f.parameters) |p| try parts.append(self.a, try self.fmt("{s}: {s}", .{ p.name, try self.ty(p.type) }));
        return self.fmt("({s}) -> {s}", .{ try std.mem.join(self.a, ", ", parts.items), try self.ty(f.result) });
    }

    /// `PureFunctions.isPure`: no required effects, no intrinsically effectful node, transitively.
    fn isPure(self: *Infer, id: []const u8) Fail!bool {
        var seen: std.ArrayList([]const u8) = .empty;
        var pending: std.ArrayList([]const u8) = .empty;
        try pending.append(self.a, id);
        while (pending.pop()) |current| {
            if (preflight.contains(seen.items, current)) continue;
            try seen.append(self.a, current);
            const declared = preflight.declaration(self.r, "functions", current) orelse return false;
            if (model.arrayItems(declared, "requiredEffects").len > 0) return false;
            for (model.bodyNodes(self.a, declared)) |node| {
                const kind = prop(node, "kind");
                for ([_][]const u8{ "parallelAll", "cancellationRequested", "useResource", "exceptionCatch", "exceptionRethrow", "cancellationShield", "taskScope", "taskStart", "taskWait", "oneShotCreate", "oneShotComplete", "oneShotWait", "taskCancel", "capabilityInvoke" }) |effectful| if (same(kind, effectful)) return false;
                for (model.functionIdsOf(node)) |target| try pending.append(self.a, model.utf8(target));
            }
        }
        return true;
    }

    /// `missingEffect`: the first of `effects` the function being created does not declare.
    fn missingEffect(self: *Infer, effects: []const []const u8) ?[]const u8 {
        for (effects) |e| if (!preflight.contains(self.c.required_effects, e)) return e;
        return null;
    }

    fn effectLabel(self: *Infer, id: []const u8) Fail![]const u8 {
        return if (preflight.declaration(self.r, "effects", id)) |e| self.describe(query.nameOf(e), id) else self.fmt("'{s}'", .{id});
    }

    fn missingEffectError(self: *Infer, subject: []const u8, id: []const u8) Fail {
        return self.reject("ExpressionCallMissingEffectRequirement", try self.fmt("{s} requires effect {s} not declared by {s}.", .{ subject, try self.effectLabel(id), description }));
    }

    /// `TaskTypes.mentionsScope`.
    fn mentionsScope(t: TypeRef) bool {
        switch (t) {
            .builtin => |b| {
                if (same(b.generic, "taskScope")) return true;
                for (b.arguments) |arg| if (mentionsScope(arg)) return true;
                return false;
            },
            .named => |n| {
                for (n.arguments) |arg| if (mentionsScope(arg)) return true;
                return false;
            },
            .function => |f| {
                for (f.parameters) |p| if (mentionsScope(p)) return true;
                return mentionsScope(f.result.*);
            },
            .primitive, .capability, .type_parameter => return false,
        }
    }

    const Context = struct { unbound: []const Param, subst: Subst };

    /// `validateCallbackContext`: types the context bindings and returns the unbound parameters.
    fn validateCallbackContext(self: *Infer, frames: ?*const Frame, role: []const u8, callback: Function, context: []const recipe.Pair) Fail!Context {
        const a = self.a;
        var ids: std.ArrayList([]const u8) = .empty;
        for (context) |pair| try ids.append(a, pair.id);
        if (preflight.repeated(ids.items)) |id| return self.reject("PureExpressionDuplicateCallArgument", try self.fmt("{s} binds callback parameter '{s}' more than once.", .{ role, id }));
        var subst: Subst = .empty;
        for (context) |pair| {
            const parameter = for (callback.parameters) |p| {
                if (same(p.id, pair.id)) break p;
            } else return self.reject("PureExpressionCallArgumentTargetNotFound", try self.fmt("{s} context binds '{s}', which is not a parameter of callback {s}: {s}.", .{ role, pair.id, try self.callbackName(callback), try self.callbackSignature(callback) }));
            const actual = try self.infer(frames, pair.value);
            if (!try matchType(a, &subst, parameter.type, actual))
                return self.reject("PureExpressionCallbackContextTypeMismatch", try self.fmt("{s} context value for parameter {s} of callback {s} has type {s}, but the parameter expects {s}.", .{ role, try self.describe(parameter.name, pair.id), try self.callbackName(callback), try self.ty(actual), try self.ty(parameter.type) }));
        }
        var unbound: std.ArrayList(Param) = .empty;
        for (callback.parameters) |p| if (!preflight.contains(ids.items, p.id)) try unbound.append(a, p);
        return .{ .unbound = unbound.items, .subst = subst };
    }

    fn callbackNotFound(self: *Infer, role: []const u8, id: []const u8) Fail {
        return self.reject("PureExpressionCallbackNotFound", try self.fmt("{s} callback function '{s}' does not exist.", .{ role, id }));
    }

    fn arityMismatch(self: *Infer, role: []const u8, callback: Function, wanted: []const u8, context_count: usize) Fail {
        return self.reject("PureExpressionCallbackArityMismatch", try self.fmt("{s} callback function {s} must have exactly {s} not bound by context; it has type {s} and context binds {d}.", .{ role, try self.callbackName(callback), wanted, try self.callbackSignature(callback), context_count }));
    }

    fn effectsNotAllowed(self: *Infer, role: []const u8, what: []const u8, callback: Function, fix: []const u8) Fail {
        return self.reject("PureExpressionCallbackEffectsNotAllowed", try self.fmt("{s} {s} {s} must be pure, including its transitive calls and ambient task or cancellation behavior.{s}", .{ role, what, try self.callbackName(callback), fix }));
    }

    const Resolved = struct { callback: Function, subst: Subst };

    /// `validateListCallback`: one unbound parameter of the item type, pure, Bool for a predicate.
    fn validateListCallback(self: *Infer, frames: ?*const Frame, role: []const u8, id: []const u8, context: []const recipe.Pair, item: TypeRef, predicate: bool) Fail!Resolved {
        const callback = (try self.functionOf(id)) orelse return self.callbackNotFound(role, id);
        var ctx = try self.validateCallbackContext(frames, role, callback, context);
        if (ctx.unbound.len != 1) return self.arityMismatch(role, callback, "one parameter", context.len);
        if (!try matchType(self.a, &ctx.subst, ctx.unbound[0].type, item))
            return self.reject("PureExpressionCallbackInputTypeMismatch", try self.fmt("{s} callback function {s} input type does not match the source list item type {s}; the callback has type {s}.", .{ role, try self.callbackName(callback), try self.ty(item), try self.callbackSignature(callback) }));
        if (predicate and !eqlType(try substitute(self.a, &ctx.subst, callback.result), primitive("bool")))
            return self.reject("PureExpressionCallbackResultTypeMismatch", try self.fmt("{s} predicate {s} must return Bool; it has type {s}.", .{ role, try self.callbackName(callback), try self.callbackSignature(callback) }));
        if (!try self.isPure(callback.id)) {
            // `effectsFix`.
            const fix = if (std.mem.startsWith(u8, role, "List") or same(role, "MapFromList")) " To run effects once per item, in order, use ListEffectFold instead (its folder takes the accumulator and the item)." else " Do the effectful work outside the callback and pass only pure values into it.";
            return self.effectsNotAllowed(role, "callback function", callback, fix);
        }
        return .{ .callback = callback, .subst = ctx.subst };
    }

    /// `validateCaseCallback`: the Option and Result callbacks; returns the substituted result type.
    fn validateCaseCallback(self: *Infer, frames: ?*const Frame, role: []const u8, id: []const u8, context: []const recipe.Pair, payload: TypeRef, payload_name: []const u8) Fail!struct { Function, TypeRef } {
        const callback = (try self.functionOf(id)) orelse return self.callbackNotFound(role, id);
        var ctx = try self.validateCallbackContext(frames, role, callback, context);
        if (ctx.unbound.len != 1) return self.arityMismatch(role, callback, "one parameter", context.len);
        if (!try matchType(self.a, &ctx.subst, ctx.unbound[0].type, payload))
            return self.reject("PureExpressionCallbackInputTypeMismatch", try self.fmt("{s} callback function {s} input type does not match the {s} type {s}; the callback has type {s}.", .{ role, try self.callbackName(callback), payload_name, try self.ty(payload), try self.callbackSignature(callback) }));
        if (!try self.isPure(callback.id)) return self.effectsNotAllowed(role, "callback function", callback, "");
        return .{ callback, try substitute(self.a, &ctx.subst, callback.result) };
    }

    /// `validateLessThanCallback`: two unbound parameters of the item type, returning Bool, pure.
    fn validateLessThanCallback(self: *Infer, frames: ?*const Frame, role: []const u8, id: []const u8, context: []const recipe.Pair, item: TypeRef) Fail!void {
        const callback = (try self.functionOf(id)) orelse return self.callbackNotFound(role, id);
        var ctx = try self.validateCallbackContext(frames, role, callback, context);
        if (ctx.unbound.len != 2) return self.reject("PureExpressionCallbackArityMismatch", try self.fmt("{s} callback function {s} must have exactly two parameters not bound by context (left, right); it has type {s} and context binds {d}.", .{ role, try self.callbackName(callback), try self.callbackSignature(callback), context.len }));
        if (!try matchType(self.a, &ctx.subst, ctx.unbound[0].type, item) or !try matchType(self.a, &ctx.subst, ctx.unbound[1].type, item))
            return self.reject("PureExpressionCallbackInputTypeMismatch", try self.fmt("{s} callback function {s} input types must both match the source list item type {s}; the callback has type {s}.", .{ role, try self.callbackName(callback), try self.ty(item), try self.callbackSignature(callback) }));
        if (!eqlType(try substitute(self.a, &ctx.subst, callback.result), primitive("bool")))
            return self.reject("PureExpressionCallbackResultTypeMismatch", try self.fmt("{s} less-than callback {s} must return Bool; it has type {s}.", .{ role, try self.callbackName(callback), try self.callbackSignature(callback) }));
        if (!try self.isPure(callback.id)) return self.effectsNotAllowed(role, "callback function", callback, "");
    }

    fn shapeMismatch(self: *Infer, role: []const u8, side: []const u8, shape: []const u8, actual: TypeRef) Fail {
        return self.reject("PureExpressionOperandTypeMismatch", try self.fmt("{s} {s} operand requires {s}, but the recipe produces {s}.", .{ role, side, shape, try self.ty(actual) }));
    }

    fn listItem(self: *Infer, role: []const u8, side: []const u8, t: TypeRef) Fail!TypeRef {
        return (builtinOf(t, "list", 1) orelse return self.shapeMismatch(role, side, "List<T>", t))[0];
    }

    fn mapEntry(self: *Infer, role: []const u8, t: TypeRef) Fail![]const TypeRef {
        return builtinOf(t, "map", 2) orelse self.shapeMismatch(role, "map", "Map<K, V>", t);
    }

    fn setItem(self: *Infer, role: []const u8, t: TypeRef) Fail!TypeRef {
        return (builtinOf(t, "set", 1) orelse return self.shapeMismatch(role, "set", "Set<T>", t))[0];
    }

    /// A folder with `arity` unbound parameters (`what` names them), checked against `inputs` in order.
    fn validateFolder(self: *Infer, frames: ?*const Frame, role: []const u8, id: []const u8, context: []const recipe.Pair, what: []const u8, inputs: []const TypeRef) Fail!struct { Function, ?Subst } {
        const folder = (try self.functionOf(id)) orelse return self.reject("PureExpressionCallbackNotFound", try self.fmt("{s} folder '{s}' does not exist.", .{ role, id }));
        var ctx = try self.validateCallbackContext(frames, role, folder, context);
        if (ctx.unbound.len != inputs.len) return self.reject("PureExpressionCallbackArityMismatch", try self.fmt("{s} folder {s} must have exactly {s} not bound by context {s}; it has type {s} and context binds {d}.", .{ role, try self.callbackName(folder), if (inputs.len == 3) "three parameters" else "two parameters", what, try self.callbackSignature(folder), context.len }));
        for (ctx.unbound, inputs) |p, input| if (!try matchType(self.a, &ctx.subst, p.type, input)) return .{ folder, null };
        return .{ folder, ctx.subst };
    }

    fn folderTypeText(self: *Infer, inputs: []const TypeRef) Fail![]const u8 {
        var parts: std.ArrayList([]const u8) = .empty;
        for (inputs) |input| try parts.append(self.a, try self.ty(input));
        return std.mem.join(self.a, ", ", parts.items);
    }

    /// Kinds that name functions, capabilities, effects, tasks and exceptions; null for any other.
    fn inferMore(self: *Infer, frames: ?*const Frame, node: *const Node) Fail!?TypeRef {
        const a = self.a;
        const case = node.case;
        if (same(case, "ListFold") or same(case, "ListScan") or same(case, "SetFold")) {
            const source = try self.infer(frames, child(node, 0));
            const item = if (same(case, "SetFold")) try self.setItem(case, source) else try self.listItem(case, "source", source);
            const seed = try self.infer(frames, child(node, 1));
            const folder, const matched = try self.validateFolder(frames, case, node.slots[2].data.id, node.slots[3].data.pairs, "(accumulator, item)", &.{ seed, item });
            if (matched) |s| {
                const result = try substitute(a, &s, folder.result);
                if (eqlType(result, seed)) {
                    if (!try self.isPure(folder.id)) return self.effectsNotAllowed(case, "folder", folder, "");
                    return if (same(case, "ListScan")) try generic(a, "list", &.{seed}) else seed;
                }
                if (same(case, "ListFold")) if (builtinOf(result, "result", 2)) |r| if (eqlType(r[0], seed))
                    return self.reject("PureExpressionCallbackInputTypeMismatch", try self.fmt("ListFold folder {s} must return the accumulator type {s}; the folder has type {s}. To stop at the first Err, use ListEffectFold with the same seed and folder: it accepts a pure folder returning Result<S, E> and yields Result<S, E>.", .{ try self.callbackName(folder), try self.ty(seed), try self.callbackSignature(folder) }));
            }
            return self.reject("PureExpressionCallbackInputTypeMismatch", try self.fmt("{s} folder {s} must have type (accumulator, item) -> accumulator, here ({s}) -> {s}; the folder has type {s}.", .{ case, try self.callbackName(folder), try self.folderTypeText(&.{ seed, item }), try self.ty(seed), try self.callbackSignature(folder) }));
        }
        if (same(case, "MapFold")) {
            const entry = try self.mapEntry(case, try self.infer(frames, child(node, 0)));
            const seed = try self.infer(frames, child(node, 1));
            const folder, const matched = try self.validateFolder(frames, case, node.slots[2].data.id, node.slots[3].data.pairs, "(accumulator, key, value)", &.{ seed, entry[0], entry[1] });
            if (matched) |s| if (eqlType(try substitute(a, &s, folder.result), seed)) {
                if (!try self.isPure(folder.id)) return self.effectsNotAllowed(case, "folder", folder, "");
                return seed;
            };
            return self.reject("PureExpressionCallbackInputTypeMismatch", try self.fmt("MapFold folder {s} must have type (accumulator, key, value) -> accumulator, here ({s}) -> {s}; the folder has type {s}.", .{ try self.callbackName(folder), try self.folderTypeText(&.{ seed, entry[0], entry[1] }), try self.ty(seed), try self.callbackSignature(folder) }));
        }
        if (same(case, "MapFilter") or same(case, "MapMapValues")) {
            const source = try self.infer(frames, child(node, 0));
            const entry = try self.mapEntry(case, source);
            const id = node.slots[1].data.id;
            const callback = (try self.functionOf(id)) orelse return self.reject("PureExpressionCallbackNotFound", try self.fmt("{s} callback '{s}' does not exist.", .{ case, id }));
            const context = node.slots[2].data.pairs;
            var ctx = try self.validateCallbackContext(frames, case, callback, context);
            if (ctx.unbound.len != 2) return self.reject("PureExpressionCallbackArityMismatch", try self.fmt("{s} callback {s} must have exactly two parameters not bound by context (key, value); it has type {s} and context binds {d}.", .{ case, try self.callbackName(callback), try self.callbackSignature(callback), context.len }));
            const matched = try matchType(a, &ctx.subst, ctx.unbound[0].type, entry[0]) and try matchType(a, &ctx.subst, ctx.unbound[1].type, entry[1]);
            if (same(case, "MapFilter")) {
                if (matched and eqlType(try substitute(a, &ctx.subst, callback.result), primitive("bool"))) {
                    if (!try self.isPure(callback.id)) return self.effectsNotAllowed(case, "callback", callback, "");
                    return source;
                }
                return self.reject("PureExpressionCallbackInputTypeMismatch", try self.fmt("MapFilter callback {s} must have type (key, value) -> Bool, here ({s}) -> Bool; the callback has type {s}.", .{ try self.callbackName(callback), try self.folderTypeText(entry), try self.callbackSignature(callback) }));
            }
            if (matched) {
                if (!try self.isPure(callback.id)) return self.effectsNotAllowed(case, "callback", callback, "");
                return try generic(a, "map", &.{ entry[0], try substitute(a, &ctx.subst, callback.result) });
            }
            return self.reject("PureExpressionCallbackInputTypeMismatch", try self.fmt("MapMapValues callback {s} must have type (key, value) -> U, here ({s}) -> U; the callback has type {s}.", .{ try self.callbackName(callback), try self.folderTypeText(entry), try self.callbackSignature(callback) }));
        }
        if (same(case, "ListEffectFold")) {
            const item = try self.listItem(case, "source", try self.infer(frames, child(node, 0)));
            const seed = try self.infer(frames, child(node, 1));
            const folder, const matched = try self.validateFolder(frames, case, node.slots[2].data.id, node.slots[3].data.pairs, "(accumulator, item)", &.{ seed, item });
            const s = matched orelse return self.reject("PureExpressionCallbackInputTypeMismatch", try self.fmt("ListEffectFold folder {s} must take (accumulator, item), here ({s}); the folder has type {s}.", .{ try self.callbackName(folder), try self.folderTypeText(&.{ seed, item }), try self.callbackSignature(folder) }));
            if (self.missingEffect(folder.effects)) |e| return self.missingEffectError(try self.fmt("ListEffectFold folder {s}", .{try self.callbackName(folder)}), e);
            const result = try substitute(a, &s, folder.result);
            if (eqlType(result, seed)) return seed;
            if (builtinOf(result, "result", 2)) |r| if (eqlType(r[0], seed)) return result;
            return self.reject("PureExpressionCallbackInputTypeMismatch", try self.fmt("ListEffectFold folder {s} must return the accumulator type S or Result<S, E>, here {s} or Result<{s}, E>; the folder has type {s}.", .{ try self.callbackName(folder), try self.ty(seed), try self.ty(seed), try self.callbackSignature(folder) }));
        }
        if (same(case, "ListZipWith")) {
            const left = try self.listItem(case, "left", try self.infer(frames, child(node, 0)));
            const right = try self.listItem(case, "right", try self.infer(frames, child(node, 1)));
            const id = node.slots[2].data.id;
            const combiner = (try self.functionOf(id)) orelse return self.reject("PureExpressionCallbackNotFound", try self.fmt("ListZipWith combiner '{s}' does not exist.", .{id}));
            const context = node.slots[3].data.pairs;
            var ctx = try self.validateCallbackContext(frames, case, combiner, context);
            if (ctx.unbound.len != 2) return self.reject("PureExpressionCallbackArityMismatch", try self.fmt("ListZipWith combiner {s} must have exactly two parameters not bound by context (left item, right item); it has type {s} and context binds {d}.", .{ try self.callbackName(combiner), try self.callbackSignature(combiner), context.len }));
            if (!try matchType(a, &ctx.subst, ctx.unbound[0].type, left) or !try matchType(a, &ctx.subst, ctx.unbound[1].type, right))
                return self.reject("PureExpressionCallbackInputTypeMismatch", try self.fmt("ListZipWith combiner {s} must take (left item, right item), here ({s}, {s}); the combiner has type {s}.", .{ try self.callbackName(combiner), try self.ty(left), try self.ty(right), try self.callbackSignature(combiner) }));
            if (!try self.isPure(combiner.id)) return self.effectsNotAllowed(case, "combiner", combiner, "");
            return try generic(a, "list", &.{try substitute(a, &ctx.subst, combiner.result)});
        }
        if (same(case, "ListMap") or same(case, "ListFlatMap") or same(case, "MapFromList") or same(case, "ListFilter") or same(case, "ListAny") or same(case, "ListFindIndex") or same(case, "ListAll")) {
            const source = try self.infer(frames, child(node, 0));
            const item = try self.listItem(case, "source", source);
            const predicate = same(case, "ListFilter") or same(case, "ListAny") or same(case, "ListFindIndex") or same(case, "ListAll");
            const resolved = try self.validateListCallback(frames, case, node.slots[1].data.id, node.slots[2].data.pairs, item, predicate);
            if (same(case, "ListFilter")) return source;
            if (same(case, "ListAny") or same(case, "ListAll")) return primitive("bool");
            if (same(case, "ListFindIndex")) return try generic(a, "option", &.{primitive("int64")});
            const result = try substitute(a, &resolved.subst, resolved.callback.result);
            if (same(case, "ListMap")) return try generic(a, "list", &.{result});
            if (same(case, "ListFlatMap")) {
                if (builtinOf(result, "list", 1) != null) return result;
                return self.reject("PureExpressionCallbackResultTypeMismatch", try self.fmt("ListFlatMap mapper {s} must return List<R>, not {s}; it has type {s}. Use ListMap for a mapper that returns one item.", .{ try self.callbackName(resolved.callback), try self.ty(result), try self.callbackSignature(resolved.callback) }));
            }
            if (!try self.isEquatable(result)) return self.reject("MapKeyTypeNotEquatable", try self.fmt("Map key type is not equatable: {s}", .{try self.explainEquatable(result)}));
            return try generic(a, "map", &.{ result, item });
        }
        if (same(case, "SetFilter") or same(case, "SetMap")) {
            const source = try self.infer(frames, child(node, 0));
            const item = try self.setItem(case, source);
            const resolved = try self.validateListCallback(frames, case, node.slots[1].data.id, node.slots[2].data.pairs, item, same(case, "SetFilter"));
            if (same(case, "SetFilter")) return source;
            const result = try substitute(a, &resolved.subst, resolved.callback.result);
            if (!try self.isEquatable(result)) return self.reject("SetItemTypeNotEquatable", try self.fmt("Set item type is not equatable: {s}", .{try self.explainEquatable(result)}));
            return try generic(a, "set", &.{result});
        }
        if (same(case, "ListSortBy") or same(case, "ListMinBy") or same(case, "ListMaxBy")) {
            const source = try self.infer(frames, child(node, 0));
            const item = try self.listItem(case, "source", source);
            try self.validateLessThanCallback(frames, case, node.slots[1].data.id, node.slots[2].data.pairs, item);
            return if (same(case, "ListSortBy")) source else try generic(a, "option", &.{item});
        }
        if (same(case, "OptionMap") or same(case, "OptionAndThen")) {
            const source = try self.infer(frames, child(node, 0));
            const item = builtinOf(source, "option", 1) orelse return self.shapeMismatch(case, "option", "Option<T>", source);
            const callback, const mapped = try self.validateCaseCallback(frames, case, node.slots[1].data.id, node.slots[2].data.pairs, item[0], "Some value");
            if (same(case, "OptionMap")) return try generic(a, "option", &.{mapped});
            if (builtinOf(mapped, "option", 1) != null) return mapped;
            return self.reject("PureExpressionCallbackResultTypeMismatch", try self.fmt("OptionAndThen callback function {s} must return Option<R>; it has type {s}.", .{ try self.callbackName(callback), try self.callbackSignature(callback) }));
        }
        if (same(case, "ResultMap") or same(case, "ResultMapErr") or same(case, "ResultAndThen")) {
            const source = try self.infer(frames, child(node, 0));
            const parts = builtinOf(source, "result", 2) orelse return self.shapeMismatch(case, "result", "Result<T, E>", source);
            if (same(case, "ResultMapErr")) {
                _, const mapped = try self.validateCaseCallback(frames, case, node.slots[1].data.id, node.slots[2].data.pairs, parts[1], "Err error");
                return try generic(a, "result", &.{ parts[0], mapped });
            }
            const callback, const mapped = try self.validateCaseCallback(frames, case, node.slots[1].data.id, node.slots[2].data.pairs, parts[0], "Ok value");
            if (same(case, "ResultMap")) return try generic(a, "result", &.{ mapped, parts[1] });
            if (builtinOf(mapped, "result", 2)) |bound| if (eqlType(bound[1], parts[1])) return mapped;
            return self.reject("PureExpressionCallbackResultTypeMismatch", try self.fmt("ResultAndThen callback function {s} must return Result<R, {s}>; it has type {s}.", .{ try self.callbackName(callback), try self.ty(parts[1]), try self.callbackSignature(callback) }));
        }
        if (same(case, "ParallelAll")) {
            const source = try self.infer(frames, child(node, 0));
            const id = node.slots[1].data.id;
            const item = try self.listItem(case, "source", source);
            const worker = (try self.functionOf(id)) orelse return self.reject("PureExpressionCallbackNotFound", try self.fmt("ParallelAll worker '{s}' does not exist.", .{id}));
            var ctx = try self.validateCallbackContext(frames, case, worker, node.slots[2].data.pairs);
            if (ctx.unbound.len != 1 or !try matchType(a, &ctx.subst, ctx.unbound[0].type, item))
                return self.reject("PureExpressionCallbackInputTypeMismatch", try self.fmt("ParallelAll worker {s} must have exactly one parameter not bound by context, of the item type {s}; the worker has type {s}.", .{ try self.callbackName(worker), try self.ty(item), try self.callbackSignature(worker) }));
            if (self.missingEffect(worker.effects)) |e| return self.missingEffectError(try self.fmt("ParallelAll worker {s}", .{try self.callbackName(worker)}), e);
            const result = try substitute(a, &ctx.subst, worker.result);
            const parts = builtinOf(result, "result", 2) orelse return self.reject("PureExpressionCallbackInputTypeMismatch", try self.fmt("ParallelAll worker {s} must return Result<U, E>, not {s}.", .{ try self.callbackName(worker), try self.ty(result) }));
            return try generic(a, "result", &.{ try generic(a, "list", &.{parts[0]}), parts[1] });
        }
        if (same(case, "TaskStart")) {
            const scope_result = self.infer(frames, child(node, 0));
            const scope_failure = try self.failureOf(TypeRef, scope_result);
            const item = self.infer(frames, child(node, 3)) catch |e| return if (scope_failure) |d| self.reject(d.code, d.message) else e;
            if (scope_failure) |d| return self.reject(d.code, d.message);
            const scope = try scope_result;
            if (!eqlType(scope, try generic(a, "taskScope", &.{}))) return self.reject("TaskStartScopeTypeMismatch", try self.fmt("TaskStart scope must be a TaskScope, not {s}.", .{try self.ty(scope)}));
            const id = node.slots[1].data.id;
            const worker = (try self.functionOf(id)) orelse return self.reject("PureExpressionCallbackNotFound", try self.fmt("TaskStart worker '{s}' does not exist.", .{id}));
            var ctx = try self.validateCallbackContext(frames, case, worker, node.slots[2].data.pairs);
            if (ctx.unbound.len != 1 or !try matchType(a, &ctx.subst, ctx.unbound[0].type, item))
                return self.reject("PureExpressionCallbackInputTypeMismatch", try self.fmt("TaskStart worker {s} must have exactly one parameter not bound by context, of the item type {s}; the worker has type {s}.", .{ try self.callbackName(worker), try self.ty(item), try self.callbackSignature(worker) }));
            if (self.missingEffect(worker.effects)) |e| return self.missingEffectError(try self.fmt("TaskStart worker {s}", .{try self.callbackName(worker)}), e);
            const result = try substitute(a, &ctx.subst, worker.result);
            if (builtinOf(result, "result", 2) == null) return self.reject("PureExpressionCallbackInputTypeMismatch", try self.fmt("TaskStart worker {s} must return Result<U, E>, not {s}.", .{ try self.callbackName(worker), try self.ty(result) }));
            return try generic(a, "task", &.{result});
        }
        if (same(case, "CancellationShield") or same(case, "TaskScope")) {
            const id = node.slots[0].data.id;
            const body = (try self.functionOf(id)) orelse return self.reject("PureExpressionCallbackNotFound", try self.fmt("{s} body '{s}' does not exist.", .{ case, id }));
            var ctx = try self.validateCallbackContext(frames, case, body, node.slots[1].data.pairs);
            const scope = if (same(case, "TaskScope")) try generic(a, "taskScope", &.{}) else primitive("unit");
            if (ctx.unbound.len != 1 or !try matchType(a, &ctx.subst, ctx.unbound[0].type, scope))
                return self.reject(try self.fmt("{s}BodyMismatch", .{case}), try self.fmt("{s} body {s} must have exactly one parameter not bound by context, of type {s}; the body has type {s}.", .{ case, try self.callbackName(body), if (same(case, "TaskScope")) "TaskScope" else "Unit", try self.callbackSignature(body) }));
            if (self.missingEffect(body.effects)) |e| return self.missingEffectError(try self.fmt("{s} body {s}", .{ case, try self.callbackName(body) }), e);
            const result = try substitute(a, &ctx.subst, body.result);
            if (builtinOf(result, "result", 2) != null) return result;
            return self.reject(try self.fmt("{s}ResultMismatch", .{case}), try self.fmt("{s} body {s} must return Result<T, E>, not {s}.", .{ case, try self.callbackName(body), try self.ty(result) }));
        }
        if (same(case, "UseResource")) {
            const acquired = try self.infer(frames, child(node, 0));
            const parts = builtinOf(acquired, "result", 2) orelse return self.reject("UseResourceAcquireTypeMismatch", try self.fmt("UseResource acquisition must produce Result<R, E>, not {s}.", .{try self.ty(acquired)}));
            const body_id = node.slots[3].data.id;
            const operation_id = node.slots[2].data.id;
            const body = (try self.functionOf(body_id)) orelse return self.reject("PureExpressionCallbackNotFound", try self.fmt("UseResource body '{s}' does not exist.", .{body_id}));
            const owner_capability, const operation = (try self.operationOf(operation_id)) orelse return self.reject("ExpressionCapabilityOperationNotFound", try self.fmt("UseResource references unknown release operation '{s}'.", .{operation_id}));
            // F# types the context and the receiver together; the context's diagnostic wins.
            const context = self.validateCallbackContext(frames, case, body, node.slots[4].data.pairs);
            const context_failure = try self.failureOf(Context, context);
            const receiver = self.infer(frames, child(node, 1)) catch |e| return if (context_failure) |d| self.reject(d.code, d.message) else e;
            if (context_failure) |d| return self.reject(d.code, d.message);
            var ctx = try context;
            if (ctx.unbound.len != 1 or !try matchType(a, &ctx.subst, ctx.unbound[0].type, parts[0]))
                return self.reject("UseResourceBodyMismatch", try self.fmt("UseResource body {s} must have exactly one parameter not bound by context, of the acquired resource type {s}; the body has type {s}.", .{ try self.callbackName(body), try self.ty(parts[0]), try self.callbackSignature(body) }));
            if (!eqlType(receiver, .{ .capability = query.idOf(owner_capability) })) return self.reject("UseResourceReleaseReceiverMismatch", "UseResource release capability value does not own the release operation.");
            const body_result = try substitute(a, &ctx.subst, body.result);
            const effects = try appended(a, body.effects, prop(operation, "effectId"));
            if (self.missingEffect(effects)) |e| return self.missingEffectError("UseResource", e);
            const result_parts = builtinOf(body_result, "result", 2);
            if (result_parts == null or !eqlType(result_parts.?[1], parts[1]))
                return self.reject("UseResourceResultMismatch", try self.fmt("UseResource body {s} must return Result<T, E> with the acquisition error type E = {s}; it returns {s}.", .{ try self.callbackName(body), try self.ty(parts[1]), try self.ty(body_result) }));
            const operation_parameters = model.arrayItems(operation, "parameters");
            const operation_result = try typeOf(a, model.property(operation, "resultType").?);
            const releases_result = eqlType(operation_result, try generic(a, "result", &.{ primitive("unit"), parts[1] }));
            if (operation_parameters.len == 1 and eqlType(try typeOf(a, model.property(operation_parameters[0], "type").?), parts[0]) and (eqlType(operation_result, primitive("unit")) or releases_result)) return body_result;
            return self.reject("UseResourceReleaseSignatureMismatch", "The release operation must take exactly the resource and return Unit or Result<Unit, E>.");
        }
        if (same(case, "ExceptionCatch")) {
            const body_result = self.exceptionCallback(frames, "body", node.slots[0].data.id, node.slots[1].data.pairs, primitive("unit"));
            const body_failure = try self.failureOf(TypeRef, body_result);
            const handler = self.exceptionCallback(frames, "handler", node.slots[2].data.id, node.slots[3].data.pairs, try generic(a, "exception", &.{})) catch |e| return if (body_failure) |d| self.reject(d.code, d.message) else e;
            if (body_failure) |d| return self.reject(d.code, d.message);
            const body = try body_result;
            if (eqlType(body, handler) and builtinOf(body, "result", 2) != null) return body;
            return self.reject("ExceptionCatchResultMismatch", "ExceptionCatch callbacks must return the same Result<T, E>.");
        }
        if (same(case, "ExceptionIsCancellation") or same(case, "ExceptionMessage")) {
            const value = try self.infer(frames, child(node, 0));
            if (builtinOf(value, "exception", 0) == null) return self.reject("ExceptionValueTypeMismatch", try self.fmt("{s} requires Exception.", .{case}));
            return primitive(if (same(case, "ExceptionMessage")) "string" else "bool");
        }
        if (same(case, "ExceptionRethrow")) {
            const result = node.slots[1].data.type;
            try preflight.validateTypeRef(self.r, result);
            if (builtinOf(try self.infer(frames, child(node, 0)), "exception", 0) == null) return self.reject("ExceptionValueTypeMismatch", "ExceptionRethrow requires Exception.");
            return result;
        }
        if (same(case, "TaskWait") or same(case, "OneShotWait")) {
            // The limit is typed first, but the first operand's diagnostic wins.
            var limit_failure: ?query.Diagnostic = null;
            if (slotNamed(node, "limit")) |limit| {
                const limit_type = self.infer(frames, limit.data.recipe) catch |e| blk: {
                    if (e != error.Rejected) return e;
                    limit_failure = self.r.diagnostic;
                    break :blk null;
                };
                if (limit_type) |t| if (!eqlType(t, try generic(a, "option", &.{primitive("int64")}))) {
                    limit_failure = if (same(case, "TaskWait"))
                        .{ .code = "TaskWaitLimitTypeMismatch", .message = try self.fmt("TaskWait limit requires Option<Int64>, not {s}.", .{try self.ty(t)}) }
                    else
                        .{ .code = "OneShotLimitTypeMismatch", .message = "OneShotWait requires Option<Int64> limit." };
                };
            }
            const first = try self.infer(frames, child(node, 0));
            if (limit_failure) |d| return self.reject(d.code, d.message);
            if (same(case, "TaskWait")) {
                if (builtinOf(first, "list", 1)) |items| if (builtinOf(items[0], "task", 1)) |result| return try generic(a, "list", &.{try generic(a, "option", &.{result[0]})});
                return self.reject("TaskWaitInputTypeMismatch", try self.fmt("TaskWait requires List<Task<T>>, not {s}.", .{try self.ty(first)}));
            }
            const value = builtinOf(first, "oneShot", 1) orelse return self.reject("OneShotCellTypeMismatch", "OneShotWait requires OneShot<T>.");
            return try generic(a, "option", &.{value[0]});
        }
        if (same(case, "OneShotCreate")) {
            const scope = try self.infer(frames, child(node, 0));
            const value = node.slots[1].data.type;
            try preflight.validateTypeRef(self.r, value);
            if (!eqlType(scope, try generic(a, "taskScope", &.{}))) return self.reject("OneShotScopeTypeMismatch", "OneShotCreate requires TaskScope.");
            if (mentionsScope(value)) return self.reject("OneShotScopePayloadUnsupported", "A OneShot payload cannot contain TaskScope.");
            return try generic(a, "oneShot", &.{value});
        }
        if (same(case, "OneShotComplete")) {
            const cell_result = self.infer(frames, child(node, 0));
            const cell_failure = try self.failureOf(TypeRef, cell_result);
            const value = self.infer(frames, child(node, 1)) catch |e| return if (cell_failure) |d| self.reject(d.code, d.message) else e;
            if (cell_failure) |d| return self.reject(d.code, d.message);
            const cell = try cell_result;
            const expected = builtinOf(cell, "oneShot", 1) orelse return self.reject("OneShotCellTypeMismatch", "OneShotComplete requires OneShot<T>.");
            if (!eqlType(expected[0], value)) return self.reject("OneShotValueTypeMismatch", "OneShotComplete payload must match the cell's exact type.");
            return primitive("bool");
        }
        if (same(case, "TaskCancel")) {
            const task = try self.infer(frames, child(node, 0));
            if (builtinOf(task, "task", 1) == null) return self.reject("TaskCancelInputTypeMismatch", try self.fmt("TaskCancel requires Task<T>, not {s}.", .{try self.ty(task)}));
            return primitive("unit");
        }
        if (same(case, "CapabilityConstruct")) return try self.capabilityConstruct(node.slots[1].data.id, node.slots[0].data.implementations);
        if (same(case, "FunctionReference")) {
            const id = node.slots[0].data.id;
            const f = (try self.functionOf(id)) orelse return self.reject("UnknownFunctionReference", "The referenced function does not exist.");
            if (f.generic) return self.reject("GenericFunctionReferenceUnsupported", "Use a monomorphic wrapper for a generic function reference.");
            if (!try self.isPure(id)) return self.reject("ImpureFunctionReference", "Function references require transitive purity and no ambient cancellation reads.");
            const parameters = try a.alloc(TypeRef, f.parameters.len);
            for (f.parameters, parameters) |p, *slot| slot.* = p.type;
            const result = try a.create(TypeRef);
            result.* = f.result;
            return .{ .function = .{ .parameters = parameters, .result = result } };
        }
        if (same(case, "FunctionInvoke")) {
            const value = try self.infer(frames, child(node, 0));
            const arguments = node.slots[1].data.recipes;
            if (value != .function) return self.reject("FunctionInvokeTargetTypeMismatch", "Function invocation requires a function value.");
            if (value.function.parameters.len != arguments.len) return self.reject("FunctionInvokeArityMismatch", "Function invocation arguments must match its positional parameter count.");
            for (value.function.parameters, arguments) |expected, argument| if (!eqlType(try self.infer(frames, argument), expected))
                return self.reject("FunctionInvokeArgumentTypeMismatch", "Function invocation argument has the wrong type.");
            return value.function.result.*;
        }
        if (same(case, "DirectCall")) return try self.directCall(frames, node);
        if (same(case, "SelfCall")) return try self.selfCall(frames, node.slots[0].data.pairs);
        if (same(case, "CapabilityInvoke")) return try self.capabilityInvoke(frames, node);
        return null;
    }

    fn slotNamed(node: *const Node, name: []const u8) ?recipe.Slot {
        for (node.slots) |slot| if (same(slot.name, name)) return slot;
        return null;
    }

    /// An `ExceptionCatch` callback: one unbound input of `input`, its effects declared, its result substituted.
    fn exceptionCallback(self: *Infer, frames: ?*const Frame, role: []const u8, id: []const u8, context: []const recipe.Pair, input: TypeRef) Fail!TypeRef {
        const callback = (try self.functionOf(id)) orelse return self.reject("PureExpressionCallbackNotFound", try self.fmt("ExceptionCatch {s} function does not exist.", .{role}));
        var ctx = try self.validateCallbackContext(frames, "ExceptionCatch", callback, context);
        if (ctx.unbound.len != 1 or !try matchType(self.a, &ctx.subst, ctx.unbound[0].type, input))
            return self.reject("ExceptionCatchCallbackMismatch", try self.fmt("ExceptionCatch {s} must have one unbound input of type {s}.", .{ role, try self.ty(input) }));
        if (self.missingEffect(callback.effects)) |e| return self.missingEffectError("ExceptionCatch", e);
        return substitute(self.a, &ctx.subst, callback.result);
    }

    /// `capabilityOperationsById`: the owning capability and the operation.
    fn operationOf(self: *Infer, id: []const u8) Fail!?struct { Value, Value } {
        for (model.arrayItems(self.r.model, "capabilities")) |capability| for (model.arrayItems(capability, "operations")) |operation| if (same(query.idOf(operation), id)) return .{ capability, operation };
        return null;
    }

    /// `CapabilityImplementations.validate`: the first problem, in its order.
    fn capabilityConstruct(self: *Infer, capability_id: []const u8, implementations: []const recipe.Implementation) Fail!TypeRef {
        const a = self.a;
        const capability = preflight.declaration(self.r, "capabilities", capability_id) orelse return self.reject("UnknownCapabilityConstructTarget", "A capability construction must name an existing capability.");
        const operations = model.arrayItems(capability, "operations");
        var ids: std.ArrayList([]const u8) = .empty;
        for (implementations) |i| try ids.append(a, i.operation);
        if (preflight.repeated(ids.items) != null) return self.reject("DuplicateCapabilityImplementation", "Each capability operation must have exactly one implementation.");
        var covered = operations.len == ids.items.len;
        for (operations) |operation| if (!preflight.contains(ids.items, query.idOf(operation))) {
            covered = false;
        };
        if (!covered) return self.reject("CapabilityImplementationSetMismatch", "Implementations must cover exactly the operations of the constructed capability.");
        for (implementations) |i| {
            const f = (try self.functionOf(i.function)) orelse return self.reject("UnknownCapabilityImplementationFunction", try self.fmt("Implementation function '{s}' does not exist.", .{i.function}));
            const operation = for (operations) |operation| {
                if (same(query.idOf(operation), i.operation)) break operation;
            } else continue;
            if (f.generic) return self.reject("GenericCapabilityImplementationUnsupported", "Use a monomorphic wrapper for a generic implementation function.");
            if (!try self.isPure(f.id)) return self.reject("ImpureCapabilityImplementation", try self.fmt("Implementation '{s}' must be transitively pure, including ambient cancellation behavior.", .{i.function}));
            const operation_parameters = model.arrayItems(operation, "parameters");
            var matches = operation_parameters.len == f.parameters.len and eqlType(f.result, try typeOf(a, model.property(operation, "resultType").?));
            if (matches) for (operation_parameters, f.parameters) |op, p| if (!eqlType(try typeOf(a, model.property(op, "type").?), p.type)) {
                matches = false;
            };
            if (!matches) return self.reject("CapabilityImplementationSignatureMismatch", try self.fmt("Implementation '{s}' must match operation '{s}' parameter order, parameter types and result type exactly.", .{ i.function, i.operation }));
        }
        return .{ .capability = capability_id };
    }

    /// `DirectCall` with `validateCallArguments`.
    fn directCall(self: *Infer, frames: ?*const Frame, node: *const Node) Fail!TypeRef {
        const a = self.a;
        const id = node.slots[0].data.id;
        const bindings = node.slots[1].data.pairs;
        const callee = (try self.functionOf(id)) orelse return self.reject("PureExpressionCallTargetNotFound", try self.fmt("DirectCall references unknown function '{s}'.", .{id}));
        if (self.missingEffect(callee.effects)) |e| return self.missingEffectError(try self.fmt("DirectCall callee {s}", .{try self.callbackName(callee)}), e);
        var ids: std.ArrayList([]const u8) = .empty;
        for (bindings) |pair| try ids.append(a, pair.id);
        if (preflight.repeated(ids.items)) |duplicate| return self.reject("PureExpressionDuplicateCallArgument", try self.fmt("DirectCall binds parameter '{s}' more than once.", .{duplicate}));
        for (bindings) |pair| {
            for (callee.parameters) |p| {
                if (same(p.id, pair.id)) break;
            } else return self.reject("PureExpressionCallArgumentTargetNotFound", try self.fmt("DirectCall targets parameter '{s}' not owned by callee {s}: {s}.", .{ pair.id, try self.callbackName(callee), try self.callbackSignature(callee) }));
        }
        for (callee.parameters) |p| if (!preflight.contains(ids.items, p.id))
            return self.reject("PureExpressionCallArgumentMissing", try self.fmt("DirectCall is missing required parameter {s} for callee {s}: {s}.", .{ try self.describe(p.name, p.id), try self.callbackName(callee), try self.callbackSignature(callee) }));
        // The function being created has no identity yet, so no call is a self call here.
        var subst: Subst = .empty;
        for (callee.parameters) |p| {
            const value = for (bindings) |pair| {
                if (same(pair.id, p.id)) break pair.value;
            } else unreachable;
            const actual = try self.infer(frames, value);
            var attempt = try subst.clone(a);
            if (!try matchType(a, &attempt, p.type, actual)) {
                const pair = try self.tyPair(try substitute(a, &subst, p.type), actual);
                return self.reject("PureExpressionCallArgumentTypeMismatch", try self.fmt("DirectCall parameter {s} of callee {s} requires {s}, but the recipe produces {s}.", .{ try self.describe(p.name, p.id), try self.callbackName(callee), pair[0], pair[1] }));
            }
            subst = attempt;
        }
        // The snapshot-wide derivation is lazy and shared by every generic call in this request.
        for (subst.values()) |bound| if (mentionsScope(bound)) {
            if (self.one_shot_requirements == null) self.one_shot_requirements = model.oneShotPayloadRequirementsOf(a, self.r.model);
            for (self.one_shot_requirements.?) |requirement| if (same(requirement.function, callee.id)) {
                for (requirement.parameters) |parameter| if (subst.get(parameter)) |actual| if (mentionsScope(actual))
                    return self.reject("OneShotScopePayloadUnsupported", "This generic call would place TaskScope inside a OneShot payload, directly or through another generic call.");
                break;
            };
            break;
        };
        return substitute(a, &subst, callee.result);
    }

    /// `SelfCall`: the function being created, at its own parameter types.
    fn selfCall(self: *Infer, frames: ?*const Frame, arguments: []const recipe.Pair) Fail!TypeRef {
        const count = self.c.parameters.len;
        // `tryFindDuplicate`: the first slot, by first occurrence, that repeats.
        for (arguments, 0..) |pair, index| if (countSlot(arguments[0..index], pair.slot) == 0 and countSlot(arguments[index + 1 ..], pair.slot) > 0)
            return self.reject("PureExpressionDuplicateCallArgument", try self.fmt("SelfCall binds parameter slot {d} more than once.", .{pair.slot}));
        for (arguments) |pair| if (pair.slot < 0 or pair.slot >= count)
            return self.reject("PureExpressionCallArgumentTargetNotFound", try self.fmt("SelfCall targets parameter slot {d}, but {s} has {d} parameter(s). {s}", .{ pair.slot, description, count, try self.describeOwnerParameters() }));
        for (self.c.parameters, 0..) |p, slot| {
            if (countSlot(arguments, @intCast(slot)) == 0) return self.reject("PureExpressionCallArgumentMissing", try self.fmt("SelfCall is missing required parameter slot {d} ('{s}') of {s}.", .{ slot, p.name, description }));
        }
        for (self.c.parameters, 0..) |p, slot| {
            const value = for (arguments) |pair| {
                if (pair.slot == slot) break pair.value;
            } else unreachable;
            const actual = try self.infer(frames, value);
            if (!eqlType(actual, p.type)) {
                const pair = try self.tyPair(p.type, actual);
                return self.reject("PureExpressionCallArgumentTypeMismatch", try self.fmt("SelfCall parameter slot {d} requires {s}, but the recipe produces {s}. A function calls itself at its own parameter types; there is no polymorphic recursion.", .{ slot, pair[0], pair[1] }));
            }
        }
        return self.c.result_type;
    }

    fn countSlot(arguments: []const recipe.Pair, slot: i64) usize {
        var n: usize = 0;
        for (arguments) |pair| if (pair.slot == slot) {
            n += 1;
        };
        return n;
    }

    /// `CapabilityInvoke` with `validateCapabilityArguments`.
    fn capabilityInvoke(self: *Infer, frames: ?*const Frame, node: *const Node) Fail!TypeRef {
        const a = self.a;
        const id = node.slots[1].data.id;
        const bindings = node.slots[2].data.pairs;
        const capability, const operation = (try self.operationOf(id)) orelse return self.reject("ExpressionCapabilityOperationNotFound", try self.fmt("CapabilityInvoke references unknown operation '{s}'.", .{id}));
        const operation_label = try self.describe(query.nameOf(operation), id);
        const receiver = try self.infer(frames, child(node, 0));
        const owner_type: TypeRef = .{ .capability = query.idOf(capability) };
        if (!eqlType(receiver, owner_type)) return self.reject("ExpressionCapabilityValueTypeMismatch", try self.fmt("CapabilityInvoke operation {s} requires {s}, but the receiver produces {s}.", .{ operation_label, try self.ty(owner_type), try self.ty(receiver) }));
        const effect = prop(operation, "effectId");
        if (!preflight.contains(self.c.required_effects, effect)) return self.reject("ExpressionCapabilityMissingEffectRequirement", try self.fmt("CapabilityInvoke operation {s} requires effect {s} not declared by {s}.", .{ operation_label, try self.effectLabel(effect), description }));
        var ids: std.ArrayList([]const u8) = .empty;
        for (bindings) |pair| try ids.append(a, pair.id);
        if (preflight.repeated(ids.items)) |duplicate| return self.reject("ExpressionDuplicateCapabilityArgument", try self.fmt("CapabilityInvoke binds parameter '{s}' more than once.", .{duplicate}));
        const parameters = model.arrayItems(operation, "parameters");
        var matched = parameters.len == bindings.len;
        for (parameters) |p| if (!preflight.contains(ids.items, query.idOf(p))) {
            matched = false;
        };
        if (!matched) return self.reject("ExpressionCapabilityArgumentSetMismatch", try self.fmt("CapabilityInvoke must bind exactly the parameters declared by operation {s}.", .{operation_label}));
        for (parameters) |p| {
            const value = for (bindings) |pair| {
                if (same(pair.id, query.idOf(p))) break pair.value;
            } else unreachable;
            const expected = try typeOf(a, model.property(p, "type").?);
            const actual = try self.infer(frames, value);
            if (!eqlType(actual, expected)) {
                const pair = try self.tyPair(expected, actual);
                return self.reject("ExpressionCapabilityArgumentTypeMismatch", try self.fmt("CapabilityInvoke parameter {s} of operation {s} requires {s}, but the recipe produces {s}.", .{ try self.describe(query.nameOf(p), query.idOf(p)), operation_label, pair[0], pair[1] }));
            }
        }
        return typeOf(a, model.property(operation, "resultType").?);
    }

    /// A rejection a sibling operand's diagnostic must not displace: F# types operand pairs together
    /// and reports the first (`| Error error, _ | _, Error error`).
    fn failureOf(self: *Infer, comptime T: type, result: Fail!T) Fail!?query.Diagnostic {
        if (result) |_| return null else |e| return if (e == error.Rejected) self.r.diagnostic else e;
    }

    // ------------------------------------------------------------ completions

    /// `GraphCompletion.merge` of two arms.
    fn merge(self: *Infer, role: []const u8, left: Completion, right: Completion) Fail!Completion {
        if (left == .aborts) return right;
        if (right == .aborts) return left;
        if (eqlType(left.normal, right.normal)) return left;
        const pair = try self.completionPair(left, right);
        return self.reject("PureExpressionBranchTypeMismatch", try self.fmt("{s} normally completing branches must produce one common type; found {s} and {s}.", .{ role, pair[0], pair[1] }));
    }

    fn pushed(self: *Infer, frames: ?*const Frame, kind: FrameKind, slots: []const Binding) Fail!*const Frame {
        const frame = try self.a.create(Frame);
        frame.* = .{ .kind = kind, .slots = try self.a.dupe(Binding, slots), .next = frames };
        return frame;
    }

    fn inferCompletionNode(self: *Infer, frames: ?*const Frame, node: *const Node) Fail!Completion {
        const case = node.case;
        if (same(case, "Abort")) {
            const result = builtinOf(self.c.result_type, "result", 2) orelse return self.reject("AbortOwnerResultTypeMismatch", try self.fmt("Abort requires {s} to return Result<T, E>.", .{description}));
            const actual = try self.infer(frames, child(node, 0));
            if (eqlType(actual, result[1])) return .aborts;
            const pair = try self.tyPair(result[1], actual);
            return self.reject("AbortPayloadTypeMismatch", try self.fmt("Abort payload has type {s}, but {s} requires {s}.", .{ pair[1], description, pair[0] }));
        }
        if (same(case, "Conditional")) {
            const condition = try self.infer(frames, child(node, 0));
            if (!eqlType(condition, primitive("bool"))) return self.typeMismatch("Conditional", "condition", primitive("bool"), condition);
            const when_true = try self.inferCompletion(frames, child(node, 1));
            const when_false = try self.inferCompletion(frames, child(node, 2));
            return self.merge("Conditional", when_true, when_false);
        }
        if (same(case, "MatchUnion")) {
            const input = try self.infer(frames, child(node, 0));
            const mismatch = "MatchUnion input must produce an existing named union type.";
            if (input != .named) return self.reject("PureExpressionMatchInputTypeMismatch", mismatch);
            const declared = self.typeDeclaration(input.named.id) orelse return self.reject("PureExpressionMatchInputTypeMismatch", mismatch);
            if (model.property(declared, "variants") == null) return self.reject("PureExpressionMatchInputTypeMismatch", mismatch);
            const variants = try self.variantsOf(declared, input.named.arguments);
            var expected: ?Completion = null;
            for (try self.resolveUnionArms(variants, node.slots[1].data.arms)) |arm| {
                var ids: std.ArrayList([]const u8) = .empty;
                for (arm.variants) |variant| try ids.append(self.a, variant.id);
                var slots: std.ArrayList(Binding) = .empty;
                if (arm.binds_fields) for (arm.variants[0].fields) |field| try slots.append(self.a, .{ .key = .{ .field = field.id }, .type = field.type });
                const actual = try self.inferCompletion(try self.pushed(frames, .{ .union_arm = ids.items }, slots.items), arm.body);
                expected = if (expected) |known| try self.merge("MatchUnion", known, actual) else actual;
            }
            return expected orelse self.reject("PureExpressionMatchHasNoArms", "MatchUnion requires at least one declared variant to derive a result type.");
        }
        if (same(case, "Bind")) {
            const input = try self.infer(frames, child(node, 0));
            return self.inferCompletion(try self.pushed(frames, .bind_body, &.{.{ .key = .value, .type = input }}), child(node, 1));
        }
        if (same(case, "MatchOption")) {
            const input = try self.infer(frames, child(node, 0));
            const item = builtinOf(input, "option", 1) orelse return self.reject("PureExpressionMatchInputTypeMismatch", "MatchOption input must produce Option<T>.");
            const some = try self.inferCompletion(try self.pushed(frames, .option_some, &.{.{ .key = .value, .type = item[0] }}), child(node, 1));
            const none = try self.inferCompletion(try self.pushed(frames, .option_none, &.{}), child(node, 2));
            return self.merge("MatchOption", some, none);
        }
        if (same(case, "MatchResult")) {
            const input = try self.infer(frames, child(node, 0));
            const arguments = builtinOf(input, "result", 2) orelse return self.reject("PureExpressionMatchInputTypeMismatch", "MatchResult input must produce Result<T,E>.");
            const ok = try self.inferCompletion(try self.pushed(frames, .result_ok, &.{.{ .key = .value, .type = arguments[0] }}), child(node, 1));
            const err = try self.inferCompletion(try self.pushed(frames, .result_err, &.{.{ .key = .err, .type = arguments[1] }}), child(node, 2));
            return self.merge("MatchResult", ok, err);
        }
        return .{ .normal = try self.infer(frames, node) };
    }

    /// `resolveUnionArms`: the arms in declaration order of each arm's first variant.
    fn resolveUnionArms(self: *Infer, variants: []const Variant, arms: []const recipe.Arm) Fail![]const ResolvedArm {
        const a = self.a;
        var named: std.ArrayList([]const u8) = .empty;
        var otherwise: usize = 0;
        for (arms) |arm| {
            if (arm.variants.len == 0) otherwise += 1;
            try named.appendSlice(a, arm.variants);
        }
        var remaining: std.ArrayList(Variant) = .empty;
        for (variants) |variant| if (!preflight.contains(named.items, variant.id)) try remaining.append(a, variant);
        if (preflight.repeated(named.items)) |id| return self.reject("PureExpressionMatchArmDuplicate", try self.fmt("MatchUnion contains variant '{s}' more than once.", .{id}));
        for (named.items) |id| if (variantIndex(variants, id) == null)
            return self.reject("PureExpressionMatchArmMismatch", "MatchUnion must contain every declared variant exactly once and no other variants.");
        if (otherwise > 1) return self.reject("PureExpressionMatchOtherwiseDuplicate", "MatchUnion has more than one otherwise arm.");
        if (otherwise == 1 and remaining.items.len == 0) return self.reject("PureExpressionMatchOtherwiseUnused", "MatchUnion has an otherwise arm, but every variant is already named by another arm.");
        if (otherwise == 0 and remaining.items.len > 0) return self.reject("PureExpressionMatchArmMismatch", "MatchUnion must contain every declared variant exactly once and no other variants.");
        var resolved: std.ArrayList(ResolvedArm) = .empty;
        for (arms) |arm| if (arm.variants.len > 0) {
            var covered: std.ArrayList(Variant) = .empty;
            for (variants) |variant| if (preflight.contains(arm.variants, variant.id)) try covered.append(a, variant);
            try resolved.append(a, .{ .variants = covered.items, .body = arm.body, .binds_fields = arm.variants.len == 1, .index = variantIndex(variants, covered.items[0].id).? });
        };
        for (arms) |arm| if (arm.variants.len == 0) try resolved.append(a, .{ .variants = remaining.items, .body = arm.body, .binds_fields = false, .index = variantIndex(variants, remaining.items[0].id).? });
        // A stable sort, as `List.sortBy` is.
        std.mem.sort(ResolvedArm, resolved.items, {}, earlier);
        return resolved.items;
    }

    fn earlier(_: void, x: ResolvedArm, y: ResolvedArm) bool {
        return x.index < y.index;
    }

    fn variantIndex(variants: []const Variant, id: []const u8) ?usize {
        for (variants, 0..) |variant, index| if (same(variant.id, id)) return index;
        return null;
    }
};

fn appended(a: Allocator, items: []const []const u8, item: []const u8) ![]const []const u8 {
    const out = try a.alloc([]const u8, items.len + 1);
    @memcpy(out[0..items.len], items);
    out[items.len] = item;
    return out;
}

/// The F# field name of a slot (`recipeFieldName`), where it differs from the wire name.
fn pathName(case: []const u8, slot: []const u8) []const u8 {
    const renamed = [_][3][]const u8{
        .{ "ListRange", "start", "left" },          .{ "ListRange", "end", "right" },
        .{ "StringRepeat", "text", "left" },        .{ "StringRepeat", "count", "right" },
        .{ "Float64Round", "value", "left" },       .{ "Float64Round", "digits", "right" },
        .{ "SetInsert", "set", "map" },             .{ "SetInsert", "item", "key" },
        .{ "SetRemove", "set", "map" },             .{ "SetRemove", "item", "key" },
        .{ "SetContains", "set", "map" },           .{ "SetContains", "item", "key" },
        .{ "OptionGetOrDefault", "default", "defaultValue" }, .{ "ResultGetOrDefault", "default", "defaultValue" },
        .{ "MapGetOrDefault", "default", "defaultValue" },   .{ "MapInsert", "value", "end" },
    };
    if (same(slot, "value") and (same(case, "ExceptionMessage") or same(case, "ExceptionIsCancellation") or same(case, "ExceptionRethrow"))) return "exceptionValue";
    if (same(slot, "map") and (same(case, "MapFold") or same(case, "MapFilter") or same(case, "MapMapValues"))) return "source";
    if (same(slot, "set") and (same(case, "SetFold") or same(case, "SetFilter") or same(case, "SetMap"))) return "source";
    if (same(slot, "option") and (same(case, "OptionMap") or same(case, "OptionAndThen"))) return "source";
    if (same(slot, "result") and (same(case, "ResultMap") or same(case, "ResultMapErr") or same(case, "ResultAndThen"))) return "source";
    for (renamed) |entry| if (same(entry[0], case) and same(entry[1], slot)) return entry[2];
    return slot;
}

/// `recipePath`: the path of `target` inside `node`, in the request's property names. A field-less
/// F# case is one object, so the first node of that case is found, as `obj.ReferenceEquals` finds it.
fn recipePath(a: Allocator, path: []const u8, node: *const Node, target: *const Node) Fail!?[]const u8 {
    if (node == target or (target.slots.len == 0 and same(node.case, target.case))) return path;
    for (node.slots) |slot| {
        const name = pathName(node.case, slot.name);
        switch (slot.data) {
            .recipe => |item| if (try recipePath(a, try std.fmt.allocPrint(a, "{s}.{s}", .{ path, name }), item, target)) |found| return found,
            .recipes => |items| for (items, 0..) |item, index| if (try recipePath(a, try std.fmt.allocPrint(a, "{s}.{s}[{d}]", .{ path, name, index }), item, target)) |found| return found,
            .pairs => |pairs| for (pairs, 0..) |pair, index| if (try recipePath(a, try std.fmt.allocPrint(a, "{s}.{s}[{d}].value", .{ path, name, index }), pair.value, target)) |found| return found,
            .arms => |arms| for (arms, 0..) |arm, index| if (try recipePath(a, try std.fmt.allocPrint(a, "{s}.{s}[{d}].body", .{ path, name, index }), arm.body, target)) |found| return found,
            else => {},
        }
    }
    return null;
}

/// Infers the body after the preflight and checks it against the result type; a body that passes
/// answers `not_materialized` until 3c-4.
pub fn run(r: *query.Request, c: preflight.Creation) Fail!void {
    var local: std.ArrayList([]const u8) = .empty;
    for (c.parameters) |p| try preflight.occurring(r.a, p.type, &local);
    var self: Infer = .{ .r = r, .a = r.a, .c = c, .local = c.type_parameters orelse local.items };
    const completion = self.inferCompletion(null, c.body) catch |e| switch (e) {
        error.Unsupported => return error.Rejected,
        error.Rejected => {
            // `locate`: the failing node's path and kind, appended to the message.
            if (try recipePath(r.a, "body", c.body, self.failed.?)) |path|
                return preflight.rejected(r, r.diagnostic.code, try std.fmt.allocPrint(r.a, "{s} At {s} ({s}).", .{ r.diagnostic.message, path, try uncapitalized(r.a, self.failed.?.case) }));
            return e;
        },
        else => return e,
    };
    if (completion == .normal and !eqlType(completion.normal, c.result_type)) {
        return preflight.rejected(r, "FunctionCreationResultTypeMismatch", try std.fmt.allocPrint(r.a, "The function body produces {s}, but the requested result type is {s}.", .{ try self.ty(completion.normal), try self.ty(c.result_type) }));
    }
    // shortcut: materialization, the transaction and the review come in 3c-4.
    r.rejection = .{ .code = "UnsupportedOperation", .message = not_materialized };
    return error.Rejected;
}
