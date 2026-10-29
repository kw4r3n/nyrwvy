//! The checks a decoded `simulateCreateFunction(ByName)` request passes before inference, as
//! `AgentExpressionStructuralWorkflow.FunctionCreation` runs them (`validateTypeParameters`, then
//! `validatePreflight` through `validateRecipeBounds`), each failure a `SimulationRejected` reply.
const std = @import("std");
const model = @import("model_reader");
const query = @import("query.zig");
const recipe = @import("recipe.zig");

const Allocator = std.mem.Allocator;
const Fail = anyerror;
const TypeRef = recipe.TypeRef;
const Node = recipe.Node;

pub const Parameter = struct { name: []const u8, type: TypeRef };

/// `FunctionCreation.Request`: `type_parameters` is the declared list (names, each its own identity), or null to derive it.
pub const Creation = struct {
    expected_snapshot_id: []const u8,
    display_name: []const u8,
    type_parameters: ?[]const []const u8,
    parameters: []const Parameter,
    result_type: TypeRef,
    required_effects: []const []const u8,
    body: *const Node,
};

/// `AgentStructuralWorkflowError.SimulationRejected` of one diagnostic, as `WireEncode.error` spells it.
pub fn rejected(r: *query.Request, code: []const u8, message: []const u8) Fail {
    r.diagnostic = .{ .code = code, .message = message };
    r.rejection = .{
        .code = "SimulationRejected",
        .message = "The structural request was rejected.",
        .details = try std.fmt.allocPrint(r.a, "{{\"diagnostics\":[{{\"code\":{s},\"message\":{s}}}]}}", .{ try query.stringOf(r.a, code), try query.stringOf(r.a, message) }),
    };
    return error.Rejected;
}

pub fn contains(items: []const []const u8, wanted: []const u8) bool {
    for (items) |item| if (std.mem.eql(u8, item, wanted)) return true;
    return false;
}

/// `TypeParameters.occurring`, appended to `out` without repeating: first-occurrence order.
pub fn occurring(a: Allocator, t: TypeRef, out: *std.ArrayList([]const u8)) !void {
    switch (t) {
        .type_parameter => |id| if (!contains(out.items, id)) try out.append(a, id),
        .function => |f| {
            for (f.parameters) |p| try occurring(a, p, out);
            try occurring(a, f.result.*, out);
        },
        .builtin => |b| for (b.arguments) |arg| try occurring(a, arg, out),
        .named => |n| for (n.arguments) |arg| try occurring(a, arg, out),
        .primitive, .capability => {},
    }
}

/// `List.countBy key |> List.tryFind (count > 1)`: the first key, by first occurrence, that repeats.
pub fn repeated(items: []const []const u8) ?[]const u8 {
    for (items, 0..) |item, i| {
        if (contains(items[0..i], item)) continue;
        if (contains(items[i + 1 ..], item)) return item;
    }
    return null;
}

/// `validateTypeParameters`, on the request's own identities (its names).
fn validateTypeParameters(r: *query.Request, c: Creation) Fail!void {
    const a = r.a;
    var introduced: std.ArrayList([]const u8) = .empty;
    for (c.parameters) |p| try occurring(a, p.type, &introduced);
    const declared = c.type_parameters orelse introduced.items;
    var mentioned: std.ArrayList([]const u8) = .empty;
    try occurring(a, c.result_type, &mentioned);
    for (c.parameters) |p| try occurring(a, p.type, &mentioned);
    var body_types: std.ArrayList(TypeRef) = .empty;
    try recipe.collectTypes(a, c.body, &body_types);
    for (body_types.items) |t| try occurring(a, t, &mentioned);
    for (declared) |name| if (try recipe.isBlank(a, name)) return rejected(r, "FunctionCreationTypeParameterNameBlank", "Type parameter names must not be blank.");
    // A declared identity is its name, so the identity check of F# can only repeat the name check.
    if (repeated(declared)) |name| return rejected(r, "FunctionCreationDuplicateTypeParameter", try std.fmt.allocPrint(a, "Type parameter '{s}' is declared more than once.", .{name}));
    for (mentioned.items) |id| if (!contains(declared, id))
        return rejected(r, "FunctionCreationTypeParameterNotDeclared", try std.fmt.allocPrint(a, "The function uses type parameter '{s}', which its typeParameters list does not declare.", .{id}));
    for (declared) |name| if (!contains(introduced.items, name))
        return rejected(r, "FunctionCreationTypeParameterNotInferable", try std.fmt.allocPrint(a, "Type parameter '{s}' is mentioned by no parameter type, so a call could not determine it; a type parameter must appear in at least one parameter type.", .{name}));
}

pub fn declaration(r: *query.Request, group: []const u8, id: []const u8) ?query.Value {
    for (model.arrayItems(r.model, group)) |item| if (std.mem.eql(u8, query.idOf(item), id)) return item;
    return null;
}

/// `ExpressionRecipeSupport.validateTypeRef` against the whole base snapshot.
pub fn validateTypeRef(r: *query.Request, t: TypeRef) Fail!void {
    const a = r.a;
    switch (t) {
        .primitive, .type_parameter => {},
        .named => |n| {
            const type_ = declaration(r, "types", n.id) orelse return rejected(r, "PureExpressionInvalidTypeRef", try std.fmt.allocPrint(a, "Type reference names unknown type '{s}'.", .{n.id}));
            const arity = model.arrayItems(type_, "typeParameters").len;
            if (arity != n.arguments.len) return rejected(r, "NamedTypeArityMismatch", try std.fmt.allocPrint(a, "{s} takes {d} type arguments, but the type reference gives {d}.", .{ query.nameOf(type_), arity, n.arguments.len }));
            for (n.arguments) |arg| try validateTypeRef(r, arg);
        },
        .capability => |id| if (declaration(r, "capabilities", id) == null)
            return rejected(r, "PureExpressionInvalidTypeRef", try std.fmt.allocPrint(a, "Type reference names unknown capability '{s}'.", .{id})),
        .function => |f| {
            for (f.parameters) |p| try validateTypeRef(r, p);
            try validateTypeRef(r, f.result.*);
        },
        .builtin => |b| {
            const eql = std.mem.eql;
            const arity: usize = if (eql(u8, b.generic, "exception") or eql(u8, b.generic, "taskScope")) 0 else if (eql(u8, b.generic, "result") or eql(u8, b.generic, "map")) 2 else 1;
            if (b.arguments.len != arity) {
                // `BuiltinGenericType` spells the case name.
                const case = try a.dupe(u8, b.generic);
                case[0] = std.ascii.toUpper(case[0]);
                return rejected(r, "PureExpressionInvalidTypeRef", try std.fmt.allocPrint(a, "Builtin generic type '{s}' requires {d} type argument(s).", .{ case, arity }));
            }
            for (b.arguments) |arg| try validateTypeRef(r, arg);
        },
    }
}

const max_depth = 32;
const max_nodes = 256;

fn depthOf(a: Allocator, node: *const Node) !usize {
    var deepest: usize = 0;
    for (try recipe.children(a, node)) |child| deepest = @max(deepest, try depthOf(a, child));
    return 1 + deepest;
}

const Subtree = struct { size: usize, path: []const u8, case: []const u8 };

/// `measure`: the size of `node`, listing every proper subtree in post-order.
fn measure(a: Allocator, path: []const u8, node: *const Node, subtrees: *std.ArrayList(Subtree)) !usize {
    var size: usize = 1;
    for (try recipe.children(a, node), 0..) |child, index| size += try measure(a, try std.fmt.allocPrint(a, "{s}/{d}", .{ path, index }), child, subtrees);
    if (path.len > 0) try subtrees.append(a, .{ .size = size, .path = path, .case = node.case });
    return size;
}

fn larger(_: void, x: Subtree, y: Subtree) bool {
    return x.size > y.size;
}

/// `validateRecipeBounds`: depth before node count, the latter naming the largest extractable subtrees.
fn validateRecipeBounds(r: *query.Request, body: *const Node) Fail!void {
    const a = r.a;
    const depth = try depthOf(a, body);
    if (depth > max_depth) return rejected(r, "StructuralRecipeDepthLimitExceeded", try std.fmt.allocPrint(a, "The structural recipe is deeper than {d} nodes (it is {d} nodes deep).", .{ max_depth, depth }));
    var subtrees: std.ArrayList(Subtree) = .empty;
    const total = try measure(a, "", body, &subtrees);
    if (total <= max_nodes) return;
    // A stable sort, as `Seq.sortByDescending` is.
    std.mem.sort(Subtree, subtrees.items, {}, larger);
    var listed: std.ArrayList([]const u8) = .empty;
    var listed_paths: std.ArrayList([]const u8) = .empty;
    for (subtrees.items) |subtree| {
        if (subtree.size > max_nodes or listed.items.len >= 3) continue;
        for (listed_paths.items) |path| {
            if (std.mem.startsWith(u8, subtree.path, try std.fmt.allocPrint(a, "{s}/", .{path}))) break;
        } else {
            try listed.append(a, try std.fmt.allocPrint(a, "{s} {s} ({d} nodes)", .{ subtree.path, subtree.case, subtree.size }));
            try listed_paths.append(a, subtree.path);
        }
    }
    const largest = if (listed.items.len == 0) "" else try std.fmt.allocPrint(a, " Largest subtrees within the limit (child-index paths from the root): {s}.", .{try std.mem.join(a, "; ", listed.items)});
    return rejected(r, "StructuralRecipeNodeLimitExceeded", try std.fmt.allocPrint(a, "The structural recipe contains {d} nodes, more than {d}.{s}", .{ total, max_nodes, largest }));
}

/// `simulateGroupWith`'s checks before inference, in F#'s order. F# allocates the fresh type-parameter
/// identities between the first check and the second; no reply here shows them.
pub fn run(r: *query.Request, c: Creation) Fail!void {
    const a = r.a;
    try validateTypeParameters(r, c);
    if (!std.mem.eql(u8, c.expected_snapshot_id, r.snapshot_id)) return rejected(r, "StructuralExpectedBaseMismatch", "The function-creation request targets a different base snapshot.");
    if (try recipe.isBlank(a, c.display_name)) return rejected(r, "FunctionCreationDisplayNameBlank", "Function display name must not be blank.");
    for (model.arrayItems(r.model, "functions")) |item| if (std.mem.eql(u8, query.nameOf(item), c.display_name))
        return rejected(r, "FunctionCreationDuplicateDisplayName", try std.fmt.allocPrint(a, "A function named '{s}' already exists; function display names must be unique.", .{c.display_name}));
    for (c.parameters, 0..) |p, index| if (try recipe.isBlank(a, p.name))
        return rejected(r, "FunctionCreationParameterDisplayNameBlank", try std.fmt.allocPrint(a, "Parameter at index {d} must have a nonblank display name.", .{index}));
    try validateTypeRef(r, c.result_type);
    for (c.parameters) |p| try validateTypeRef(r, p.type);
    if (repeated(c.required_effects)) |id| return rejected(r, "FunctionCreationDuplicateRequiredEffect", try std.fmt.allocPrint(a, "Required effect '{s}' appears more than once.", .{id}));
    for (c.required_effects) |id| if (declaration(r, "effects", id) == null)
        return rejected(r, "FunctionCreationRequiredEffectNotFound", try std.fmt.allocPrint(a, "Required effect '{s}' does not exist.", .{id}));
    try validateRecipeBounds(r, c.body);
}
