//! Readable C# facade over visible declarations. Character flags were generated with
//! Roslyn 5.0.0 SyntaxFacts and .NET 10 Char.IsUpper for each UTF-16 code unit.
const std = @import("std");
const model = @import("model_reader");
const q = @import("query.zig");
const infer = @import("infer.zig");
const Type = @import("recipe.zig").TypeRef;
const A = std.mem.Allocator;
const V = model.Model;
const chars = @embedFile("csharp_facade_chars.bin");

pub const Declaration = struct { kind: []const u8, id: []const u8 };
pub const Problem = struct { code: []const u8, name: []const u8, declarations: []const Declaration };
pub const Result = union(enum) { source: []const u8, problems: []const Problem };
const Entry = struct { name: []const u8, declaration: Declaration };
const Aliases = std.StringHashMapUnmanaged([]const u8);
fn eq(x: []const u8, y: []const u8) bool {
    return std.mem.eql(u8, x, y);
}
fn id(v: V) []const u8 {
    return q.idOf(v);
}
fn display(v: V) []const u8 {
    return q.nameOf(v);
}
fn contains(xs: []const []const u8, x: []const u8) bool {
    for (xs) |v| if (eq(v, x)) return true;
    return false;
}
fn generated(a: A, prefix: []const u8, s: []const u8) ![]const u8 {
    const out = try a.alloc(u8, prefix.len + s.len * 2);
    @memcpy(out[0..prefix.len], prefix);
    for (s, 0..) |b, i| {
        out[prefix.len + i * 2] = "0123456789ABCDEF"[b >> 4];
        out[prefix.len + i * 2 + 1] = "0123456789ABCDEF"[b & 15];
    }
    return out;
}
fn flag(c: u16, bit: usize) bool {
    const n = @as(usize, c) * 3 + bit;
    return (chars[n / 8] & (@as(u8, 1) << @intCast(n % 8))) != 0;
}
fn invalid(a: A, name: []const u8) !bool {
    const units = try std.unicode.utf8ToUtf16LeAlloc(a, name);
    if (units.len == 0 or !flag(units[0], 0) or std.mem.startsWith(u8, name, "__")) return true;
    for (units[1..]) |c| if (!flag(c, 1)) return true;
    return units.len > 2 and units[0] == 'N' and units[1] == 'y' and (units[2] == '_' or flag(units[2], 2));
}
fn keyword(name: []const u8) bool {
    const words = " abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while ";
    var it = std.mem.tokenizeScalar(u8, words, ' ');
    while (it.next()) |w| if (eq(w, name)) return true;
    return false;
}
fn ident(a: A, name: []const u8) ![]const u8 {
    return if (keyword(name)) std.fmt.allocPrint(a, "@{s}", .{name}) else name;
}
fn addEntry(a: A, list: *std.ArrayList(Entry), name: []const u8, kind: []const u8, id_: []const u8) !void {
    try list.append(a, .{ .name = name, .declaration = .{ .kind = kind, .id = id_ } });
}
fn local(r: *const q.Request, kind: []const u8, id_: []const u8) !bool {
    const key = try std.fmt.allocPrint(r.a, "{s}:{s}", .{ kind, id_ });
    if (r.provenance) |p| for (p.members) |member| if (contains(member.keys, key)) return false;
    return true;
}
fn sorted(a: A, xs: []const V) ![]const V {
    const out = try a.dupe(V, xs);
    std.mem.sort(V, out, {}, struct {
        fn less(_: void, x: V, y: V) bool {
            return q.ordinal({}, id(x), id(y));
        }
    }.less);
    return out;
}
fn find(xs: []const V, wanted: []const u8) ?V {
    for (xs) |x| if (eq(id(x), wanted)) return x;
    return null;
}
const Reach = struct {
    a: A,
    all_types: []const V,
    all_caps: []const V,
    types: std.StringHashMapUnmanaged(void) = .empty,
    caps: std.StringHashMapUnmanaged(void) = .empty,
    fn typeRef(self: *Reach, t: Type) anyerror!void {
        switch (t) {
            .named => |n| {
                for (n.arguments) |arg| try self.typeRef(arg);
                try self.typeId(n.id);
            },
            .capability => |c| try self.capId(c),
            .builtin => |b| for (b.arguments) |arg| try self.typeRef(arg),
            .function => |f| {
                try self.typeRef(f.result.*);
                for (f.parameters) |arg| try self.typeRef(arg);
            },
            else => {},
        }
    }
    fn from(self: *Reach, v: V) !void {
        try self.typeRef(try infer.typeOf(self.a, v));
    }
    fn typeId(self: *Reach, id_: []const u8) anyerror!void {
        const t = find(self.all_types, id_) orelse return;
        if (self.types.contains(id_)) return;
        try self.types.put(self.a, id_, {});
        if (t.data.type_declaration.product) {
            for (t.fields()) |f| try self.from(f.declaredType());
        } else for (t.variants()) |v| for (v.fields()) |f| try self.from(f.declaredType());
    }
    fn capId(self: *Reach, id_: []const u8) anyerror!void {
        const c = find(self.all_caps, id_) orelse return;
        if (self.caps.contains(id_)) return;
        try self.caps.put(self.a, id_, {});
        for (c.operations()) |op| {
            try self.from(op.resultType());
            for (op.parameters()) |p| try self.from(p.declaredType());
        }
    }
};
fn typeName(a: A, t: Type, aliases: *const Aliases) anyerror![]const u8 {
    return switch (t) {
        .primitive => |p| blk: {
            const table = .{ .{ "unit", "NyUnit" }, .{ "bool", "bool" }, .{ "int64", "long" }, .{ "float64", "double" }, .{ "bits32", "uint" }, .{ "string", "NyStr" }, .{ "bytes", "NyBytes" } };
            inline for (table) |row| if (eq(p, row[0])) break :blk row[1];
            return error.InvalidType;
        },
        .type_parameter => |p| generated(a, "T_", p),
        .capability => |c| aliases.get(try std.fmt.allocPrint(a, "capability:{s}", .{c})) orelse try generated(a, "NyC_", c),
        .named => |n| try std.mem.concat(a, u8, &.{ aliases.get(try std.fmt.allocPrint(a, "type:{s}", .{n.id})) orelse try generated(a, "NyT_", n.id), try typeArgs(a, n.arguments, aliases) }),
        .function => |f| blk: {
            var ts: std.ArrayList(Type) = .empty;
            try ts.appendSlice(a, f.parameters);
            try ts.append(a, f.result.*);
            break :blk try std.fmt.allocPrint(a, "NyFunction{d}{s}", .{ f.parameters.len, try typeArgs(a, ts.items, aliases) });
        },
        .builtin => |b| blk: {
            if (eq(b.generic, "exception")) break :blk "System.Runtime.ExceptionServices.ExceptionDispatchInfo";
            if (eq(b.generic, "taskScope")) break :blk "NyTaskScope";
            const table = .{ .{ "option", "NyOption" }, .{ "result", "NyResult" }, .{ "list", "NyList" }, .{ "map", "NyMap" }, .{ "set", "NySet" }, .{ "oneShot", "NyOneShot" }, .{ "task", "NyTask" } };
            inline for (table) |row| if (eq(b.generic, row[0])) break :blk try std.mem.concat(a, u8, &.{ row[1], try typeArgs(a, b.arguments, aliases) });
            return error.InvalidType;
        },
    };
}
fn typeArgs(a: A, ts: []const Type, aliases: *const Aliases) anyerror![]const u8 {
    if (ts.len == 0) return "";
    var list: std.ArrayList([]const u8) = .empty;
    for (ts) |t| try list.append(a, try typeName(a, t, aliases));
    return std.fmt.allocPrint(a, "<{s}>", .{try std.mem.join(a, ", ", list.items)});
}
fn generic(a: A, v: V) ![]const u8 {
    var names: std.ArrayList([]const u8) = .empty;
    for (v.typeParameters()) |p| try names.append(a, try generated(a, "T_", id(p)));
    return if (names.items.len == 0) "" else std.fmt.allocPrint(a, "<{s}>", .{try std.mem.join(a, ", ", names.items)});
}
fn parameters(a: A, xs: []const V, aliases: *const Aliases, generated_prefix: ?[]const u8) ![]const u8 {
    var names: std.ArrayList([]const u8) = .empty;
    for (xs) |p| try names.append(a, try std.fmt.allocPrint(a, "{s} {s}", .{ try typeName(a, try infer.typeOf(a, p.declaredType()), aliases), if (generated_prefix) |prefix| try generated(a, prefix, id(p)) else try ident(a, display(p)) }));
    return std.mem.join(a, ", ", names.items);
}
fn fields(v: V) []const V {
    return v.fields();
}
fn memberEntries(a: A, out: *std.ArrayList(Entry), xs: []const V, kind: []const u8) !void {
    for (xs) |x| try addEntry(a, out, display(x), kind, id(x));
}
fn sameDecl(x: Declaration, y: Declaration) bool {
    return eq(x.kind, y.kind) and eq(x.id, y.id);
}
fn declarationsFor(a: A, entries: []const Entry, name: []const u8, distinct: bool) ![]const Declaration {
    var out: std.ArrayList(Declaration) = .empty;
    for (entries) |e| if (eq(e.name, name)) {
        var found = false;
        if (distinct) for (out.items) |d| {
            if (sameDecl(d, e.declaration)) {
                found = true;
                break;
            }
        };
        if (!found) try out.append(a, e.declaration);
    };
    return out.items;
}
fn hasName(xs: []const Problem, name: []const u8) bool {
    for (xs) |p| if (eq(p.name, name)) return true;
    return false;
}
fn problems(a: A, scoped: []const Entry, members: []const Entry) ![]const Problem {
    var all: std.ArrayList(Entry) = .empty;
    try all.appendSlice(a, scoped);
    try all.appendSlice(a, members);
    var out: std.ArrayList(Problem) = .empty;
    for (all.items) |e| if (try invalid(a, e.name)) {
        if (!hasName(out.items, e.name)) try out.append(a, .{ .code = "FacadeNameInvalid", .name = e.name, .declarations = try declarationsFor(a, all.items, e.name, false) });
    };
    for (members) |e| if (eq(e.declaration.kind, "field") and contains(&.{ "Equals", "GetHashCode", "ToString", "GetType", "Deconstruct", "EqualityContract" }, e.name)) {
        try out.append(a, .{ .code = "FacadeNameInvalid", .name = e.name, .declarations = try a.dupe(Declaration, &.{e.declaration}) });
    };
    var seen: std.StringHashMapUnmanaged(void) = .empty;
    for (scoped) |e| {
        if (seen.contains(e.name)) continue;
        try seen.put(a, e.name, {});
        const ds = try declarationsFor(a, scoped, e.name, true);
        if (ds.len > 1) try out.append(a, .{ .code = "FacadeNameConflict", .name = e.name, .declarations = ds });
    }
    return out.items;
}
fn line(a: A, out: *std.ArrayList(u8), s: []const u8) !void {
    try out.appendSlice(a, s);
    try out.append(a, '\n');
}
fn block(a: A, out: *std.ArrayList(u8), receiver: []const u8, generics: []const u8, fs: []const V, aliases: *const Aliases) !void {
    if (fs.len == 0) return;
    try line(a, out, try std.fmt.allocPrint(a, "    extension{s}({s} __value)", .{ generics, receiver }));
    try line(a, out, "    {");
    for (fs) |f| try line(a, out, try std.fmt.allocPrint(a, "        public {s} {s} => __value.{s};", .{ try typeName(a, try infer.typeOf(a, f.declaredType()), aliases), try ident(a, display(f)), try generated(a, "f_", id(f)) }));
    try line(a, out, "    }");
}
fn constructor(a: A, out: *std.ArrayList(u8), shown: []const u8, name: []const u8, generics: []const u8, receiver: []const u8, fs: []const V, aliases: *const Aliases) !void {
    try block(a, out, receiver, generics, fs, aliases);
    var args: std.ArrayList([]const u8) = .empty;
    for (fs) |f| try args.append(a, try std.fmt.allocPrint(a, "{s}: {s}", .{ try generated(a, "f_", id(f)), try ident(a, display(f)) }));
    try line(a, out, try std.fmt.allocPrint(a, "    public static {s} New{s}{s}({s}) => new {s}({s});", .{ shown, name, generics, try parameters(a, fs, aliases, null), receiver, try std.mem.join(a, ", ", args.items) }));
}
pub fn generate(r: *const q.Request, host_operation_ids: []const []const u8, table_capability_ids: []const []const u8) !Result {
    const a = r.a;
    const all_types = try q.typedVisible(r, "type");
    const all_caps = try q.typedVisible(r, "capability");
    const all_functions = try q.typedVisible(r, "function");
    var functions: std.ArrayList(V) = .empty;
    var reach: Reach = .{ .a = a, .all_types = all_types, .all_caps = all_caps };
    for (all_functions) |f| if (f.exported() and try local(r, "function", id(f))) {
        try functions.append(a, f);
        try reach.from(f.resultType());
        for (f.parameters()) |p| try reach.from(p.declaredType());
    };
    for (all_types) |t| if (t.exported() and try local(r, "type", id(t))) try reach.typeId(id(t));
    for (all_caps) |c| {
        var include = c.exported() and try local(r, "capability", id(c));
        for (c.operations()) |op| if (contains(host_operation_ids, id(op))) {
            include = true;
            break;
        };
        if (include or contains(table_capability_ids, id(c))) try reach.capId(id(c));
    }
    var ts: std.ArrayList(V) = .empty;
    var cs: std.ArrayList(V) = .empty;
    for (all_types) |t| if (reach.types.contains(id(t))) try ts.append(a, t);
    for (all_caps) |c| if (reach.caps.contains(id(c))) try cs.append(a, c);
    const types = try sorted(a, ts.items);
    const caps = try sorted(a, cs.items);
    const fns = try sorted(a, functions.items);
    var scoped: std.ArrayList(Entry) = .empty;
    var members: std.ArrayList(Entry) = .empty;
    for (types) |t| {
        const n = display(t);
        const is_generic = t.typeParameters().len != 0;
        if (!is_generic) try addEntry(a, &scoped, n, "type", id(t));
        if (t.data.type_declaration.product) {
            try addEntry(a, &scoped, try std.fmt.allocPrint(a, "New{s}", .{n}), "type", id(t));
            try memberEntries(a, &members, fields(t), "field");
        } else {
            for (try sorted(a, t.variants())) |v| {
                const vn = try std.mem.concat(a, u8, &.{ n, display(v) });
                if (!is_generic) try addEntry(a, &scoped, vn, "variant", id(v));
                try addEntry(a, &scoped, try std.fmt.allocPrint(a, "New{s}", .{vn}), "variant", id(v));
            }
            for (t.variants()) |v| try memberEntries(a, &members, fields(v), "field");
        }
    }
    for (caps) |c| {
        try addEntry(a, &scoped, display(c), "capability", id(c));
        try addEntry(a, &scoped, try std.fmt.allocPrint(a, "{s}Base", .{display(c)}), "capability", id(c));
        if (contains(table_capability_ids, id(c))) try addEntry(a, &scoped, try std.fmt.allocPrint(a, "New{s}", .{display(c)}), "capability", id(c));
        for (c.operations()) |op| {
            try addEntry(a, &members, display(op), "capabilityOperation", id(op));
            try memberEntries(a, &members, op.parameters(), "capabilityParameter");
        }
    }
    for (fns) |f| {
        try addEntry(a, &scoped, display(f), "function", id(f));
        try memberEntries(a, &members, f.parameters(), "parameter");
    }
    const failures = try problems(a, scoped.items, members.items);
    if (failures.len != 0) return .{ .problems = failures };
    var aliases: Aliases = .empty;
    for (types) |t| if (t.typeParameters().len == 0) try aliases.put(a, try std.fmt.allocPrint(a, "type:{s}", .{id(t)}), try ident(a, display(t)));
    for (caps) |c| try aliases.put(a, try std.fmt.allocPrint(a, "capability:{s}", .{id(c)}), try ident(a, display(c)));
    const none: Aliases = .empty;
    var out: std.ArrayList(u8) = .empty;
    try line(a, &out, "// Generated by Nyrwvy.");
    try line(a, &out, "// Readable names for the exported declarations of one snapshot; regenerate it with the program.");
    try line(a, &out, "// A renamed declaration renames its facade name, so a host that uses the old name stops compiling.");
    try line(a, &out, "// Requires C# 14 (extension members). Hosts import it with: using static Nyrwvy.Generated.NyFacade;");
    for (types) |t| if (t.typeParameters().len == 0) {
        try line(a, &out, try std.fmt.allocPrint(a, "global using {s} = Nyrwvy.Generated.{s};", .{ try ident(a, display(t)), try generated(a, "NyT_", id(t)) }));
        for (try sorted(a, t.variants())) |v| try line(a, &out, try std.fmt.allocPrint(a, "global using {s}{s} = Nyrwvy.Generated.{s};", .{ display(t), display(v), try generated(a, "NyV_", id(v)) }));
    };
    for (caps) |c| try line(a, &out, try std.fmt.allocPrint(a, "global using {s} = Nyrwvy.Generated.{s};", .{ try ident(a, display(c)), try generated(a, "NyC_", id(c)) }));
    try line(a, &out, "");
    try line(a, &out, "namespace Nyrwvy.Generated;");
    try line(a, &out, "");
    try line(a, &out, "public static class NyFacade");
    try line(a, &out, "{");
    for (types) |t| {
        const gs = try generic(a, t);
        const tn = try std.mem.concat(a, u8, &.{ try generated(a, "NyT_", id(t)), gs });
        const shown = if (gs.len == 0) try ident(a, display(t)) else tn;
        if (t.data.type_declaration.product) try constructor(a, &out, shown, display(t), gs, tn, fields(t), &aliases) else for (try sorted(a, t.variants())) |v| try constructor(a, &out, shown, try std.mem.concat(a, u8, &.{ display(t), display(v) }), gs, try std.mem.concat(a, u8, &.{ try generated(a, "NyV_", id(v)), gs }), fields(v), &aliases);
    }
    for (caps) |c| if (contains(table_capability_ids, id(c))) try line(a, &out, try std.fmt.allocPrint(a, "    public static {s} New{s}() => new {s}();", .{ try ident(a, display(c)), display(c), try generated(a, "NyA_", id(c)) }));
    for (fns) |f| {
        const gs = try generic(a, f);
        var args: std.ArrayList([]const u8) = .empty;
        for (f.parameters()) |p| try args.append(a, try std.fmt.allocPrint(a, "{s}: {s}", .{ try generated(a, "p_", id(p)), try ident(a, display(p)) }));
        try line(a, &out, try std.fmt.allocPrint(a, "    public static {s} {s}{s}({s}) => NyFunctions.{s}{s}({s});", .{ try typeName(a, try infer.typeOf(a, f.resultType()), &aliases), try ident(a, display(f)), gs, try parameters(a, f.parameters(), &aliases, null), try generated(a, "NyF_", id(f)), gs, try std.mem.join(a, ", ", args.items) }));
    }
    try line(a, &out, "}");
    for (caps) |c| {
        const interface = try generated(a, "NyC_", id(c));
        try line(a, &out, "");
        try line(a, &out, try std.fmt.allocPrint(a, "public abstract class {s}Base : {s}", .{ display(c), interface }));
        try line(a, &out, "{");
        for (try sorted(a, c.operations())) |op| {
            const result = try infer.typeOf(a, op.resultType());
            try line(a, &out, try std.fmt.allocPrint(a, "    public abstract {s} {s}({s});", .{ try typeName(a, result, &aliases), try ident(a, display(op)), try parameters(a, op.parameters(), &aliases, null) }));
            var args: std.ArrayList([]const u8) = .empty;
            for (op.parameters()) |p| try args.append(a, try generated(a, "cp_", id(p)));
            try line(a, &out, try std.fmt.allocPrint(a, "    {s} {s}.{s}({s}) => {s}({s});", .{ try typeName(a, result, &none), interface, try generated(a, "Op_", id(op)), try parameters(a, op.parameters(), &none, "cp_"), try ident(a, display(op)), try std.mem.join(a, ", ", args.items) }));
        }
        try line(a, &out, "}");
    }
    return .{ .source = out.items };
}

test "empty facade and Unicode name checks" {
    const a = std.testing.allocator;
    var arena = std.heap.ArenaAllocator.init(a);
    defer arena.deinit();
    const alloc = arena.allocator();
    const empty = try model.parseValue(alloc, "{\"snapshotId\":\"s\"}");
    var interner: model.session_model.Interner = .{ .gpa = alloc };
    defer interner.deinit();
    const generation = try model.session_model.load(alloc, &interner, empty, null);
    const r: q.Request = .{ .a = alloc, .line = "", .root = .empty, .generation = &generation, .interner = &interner, .provenance = null };
    const result = try generate(&r, &.{}, &.{});
    try std.testing.expect(result == .source);
    try std.testing.expect(std.mem.endsWith(u8, result.source, "public static class NyFacade\n{\n}\n"));
    try std.testing.expect(!try invalid(alloc, "日本語"));
    try std.testing.expect(!try invalid(alloc, "class"));
    try std.testing.expect(try invalid(alloc, "𐐀"));
    try std.testing.expect(try invalid(alloc, "NyÉ"));
}

test "import visibility and external capability roots" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const root = try model.parseValue(a,
        \\{"snapshotId":"s","types":[{"id":"t","displayName":"Payload","kind":"product","fields":[]}],
        \\ "capabilities":[{"id":"c","displayName":"Host","operations":[{"id":"op","displayName":"send","parameters":[{"id":"p","displayName":"value","type":{"kind":"named","typeId":"t","arguments":[]}}],"resultType":{"kind":"primitive","primitive":"unit"},"effectId":"e"}]},{"id":"hidden","displayName":"Hidden","exported":true,"operations":[]}],
        \\ "functions":[{"id":"foreign","displayName":"Foreign","exported":true,"parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"nodes":[{"id":"n","kind":"unitLiteral"}],"resultNodeId":"n"}}]}
    );
    var interner: model.session_model.Interner = .{ .gpa = a };
    defer interner.deinit();
    const generation = try model.session_model.load(a, &interner, root, null);
    const r: q.Request = .{ .a = a, .line = "", .root = .empty, .generation = &generation, .interner = &interner, .provenance = .{ .hidden = &.{"capability:hidden"}, .members = &.{.{ .id = "", .fingerprint = "", .keys = &.{ "function:foreign", "capability:c", "type:t" }, .path = "", .embedded = false }} } };
    const result = try generate(&r, &.{"op"}, &.{"c"});
    try std.testing.expect(result == .source);
    try std.testing.expect(std.mem.indexOf(u8, result.source, "global using Payload = ") != null);
    try std.testing.expect(std.mem.indexOf(u8, result.source, "public abstract class HostBase") != null);
    try std.testing.expect(std.mem.indexOf(u8, result.source, " NewHost()") != null);
    try std.testing.expect(std.mem.indexOf(u8, result.source, "Foreign") == null);
    try std.testing.expect(std.mem.indexOf(u8, result.source, "Hidden") == null);
}

test "facade name collisions and reserved fields" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const root = try model.parseValue(a,
        \\{"snapshotId":"s","types":[{"id":"a","displayName":"Same","exported":true,"kind":"product","fields":[{"id":"x","displayName":"Equals","type":{"kind":"primitive","primitive":"int64"}}]},{"id":"b","displayName":"Same","exported":true,"kind":"product","fields":[]}],"capabilities":[],"functions":[]}
    );
    var interner: model.session_model.Interner = .{ .gpa = a };
    defer interner.deinit();
    const generation = try model.session_model.load(a, &interner, root, null);
    const r: q.Request = .{ .a = a, .line = "", .root = .empty, .generation = &generation, .interner = &interner, .provenance = null };
    const result = try generate(&r, &.{}, &.{});
    try std.testing.expect(result == .problems);
    try std.testing.expectEqual(@as(usize, 3), result.problems.len);
    try std.testing.expectEqualStrings("FacadeNameInvalid", result.problems[0].code);
    try std.testing.expectEqualStrings("Equals", result.problems[0].name);
    try std.testing.expectEqualStrings("FacadeNameConflict", result.problems[1].code);
    try std.testing.expectEqualStrings("Same", result.problems[1].name);
}
