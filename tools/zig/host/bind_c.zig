//! bind-c: clang declarations become by-name capability requests and C# mapping tables.
const std = @import("std");
const e = @import("csharp_emitter");
const root = @import("root");
const A = std.mem.Allocator;
const V = std.json.Value;
const Map = std.StringHashMapUnmanaged([]const u8);
pub const usage = "Usage: Nyrwvy.AuthoringSession bind-c --header <file.h> --library <native-name> --capability <Name> --effect <Name> --functions <f1,f2,...> --requests <out.jsonl> --table <out.csharp.json> [--prefix <c_prefix_>] [--type-prefix <CPrefix>] [--release <function>]... [--free <function>=<free-function>]... [--failure <function or result type>=null|negative|nonzero]... [--constant <parameter>=null|<integer>|<ENUMERATOR>]... [--buffer <capacity>[:<retry status>]] [--spec <operations.json>] [--clang-arg <arg>]...";
const Options = struct { header: []const u8, library: []const u8, capability: []const u8, effect: []const u8, prefix: []const u8 = "", type_prefix: []const u8 = "", functions: []const []const u8, requests: []const u8, table: []const u8, releases: []const []const u8, frees: Map = .empty, failures: Map = .empty, constants: Map = .empty, buffer: i32 = 256, retry: ?[]const u8 = null, spec: ?[]const u8 = null, clang_args: []const []const u8 };
const C = union(enum) { integer: struct { bits: i32, signed: bool }, boolean, void_, text: bool, byte_pointer, mutable_bytes, handle: []const u8, handle_out: []const u8, unmapped: []const u8 };
const Param = struct { name: []const u8, qual: []const u8 };
const Function = struct { qual: []const u8, parameters: []const Param, variadic: bool };
const Declarations = struct { typedefs: Map = .empty, complete: std.StringHashMapUnmanaged(void) = .empty, functions: std.StringHashMapUnmanaged(Function) = .empty, enums: std.StringHashMapUnmanaged(?i64) = .empty };
const Operation = struct { owner: []const u8, name: []const u8, declaration: V, entry: V, handles: []const []const u8 };
const OpResult = union(enum) { mapped: Operation, unmapped: []const u8 };
const Context = struct { a: A, io: std.Io, options: Options, declarations: Declarations, masked_spec: bool = false };
fn eq(x: []const u8, y: []const u8) bool {
    return std.mem.eql(u8, x, y);
}
fn fmt(a: A, comptime s: []const u8, args: anytype) ![]const u8 {
    return std.fmt.allocPrint(a, s, args);
}
fn obj(a: A, k: []const []const u8, v: []const V) !V {
    return e.object(a, k, v);
}
fn string(s: []const u8) V {
    return .{ .string = s };
}
fn has(v: V, k: []const u8) bool {
    return v == .object and v.object.contains(k);
}
fn text(v: V, k: []const u8) ?[]const u8 {
    const x = e.get(v, k);
    return if (x == .string) x.string else null;
}
fn set(a: A, v: *V, k: []const u8, x: V) !void {
    if (v.* != .object) return error.BadSpec;
    try v.object.put(a, k, x);
}
fn trim(s: []const u8) []const u8 {
    return std.mem.trim(u8, s, " \t\r\n");
}
fn contains(xs: []const []const u8, s: []const u8) bool {
    for (xs) |x| if (eq(x, s)) return true;
    return false;
}
fn trimEntries(s: []const u8) []const u8 {
    var it = std.unicode.Utf8View.initUnchecked(s).iterator();
    var offset: usize = 0;
    var begin: usize = 0;
    var end: usize = 0;
    var seen = false;
    while (it.nextCodepointSlice()) |part| {
        const point = std.unicode.utf8Decode(part) catch unreachable;
        const white = point <= 0xffff and @import("query.zig").isBlank(&.{@intCast(point)});
        if (!white) {
            if (!seen) begin = offset;
            seen = true;
            end = offset + part.len;
        }
        offset += part.len;
    }
    return s[begin..end];
}
fn split(a: A, s: []const u8, separator: u8) ![]const []const u8 {
    var out: std.ArrayList([]const u8) = .empty;
    var it = std.mem.splitScalar(u8, s, separator);
    while (it.next()) |part| {
        const x = trimEntries(part);
        if (x.len > 0) try out.append(a, x);
    }
    return out.items;
}
fn parse(a: A, args: []const []const u8) !?Options {
    if (args.len % 2 != 0) return null;
    var values: std.StringHashMapUnmanaged(std.ArrayList([]const u8)) = .empty;
    var i: usize = 0;
    while (i < args.len) : (i += 2) {
        const k = args[i];
        if (!contains(&.{ "--header", "--library", "--capability", "--effect", "--functions", "--requests", "--table", "--prefix", "--type-prefix", "--release", "--free", "--failure", "--constant", "--buffer", "--spec", "--clang-arg" }, k)) return null;
        const entry = try values.getOrPut(a, k);
        if (!entry.found_existing) entry.value_ptr.* = .empty;
        try entry.value_ptr.append(a, args[i + 1]);
    }
    const one = struct {
        fn get(v: std.StringHashMapUnmanaged(std.ArrayList([]const u8)), k: []const u8) ?[]const u8 {
            const list = v.get(k) orelse return null;
            return if (list.items.len == 1) list.items[0] else null;
        }
    }.get;
    var o: Options = .{ .header = one(values, "--header") orelse return null, .library = one(values, "--library") orelse return null, .capability = one(values, "--capability") orelse return null, .effect = one(values, "--effect") orelse return null, .functions = try split(a, one(values, "--functions") orelse return null, ','), .requests = one(values, "--requests") orelse return null, .table = one(values, "--table") orelse return null, .releases = if (values.get("--release")) |xs| xs.items else &.{}, .clang_args = if (values.get("--clang-arg")) |xs| xs.items else &.{}, .prefix = one(values, "--prefix") orelse "", .type_prefix = one(values, "--type-prefix") orelse "", .spec = one(values, "--spec") };
    if (one(values, "--buffer")) |b| {
        if (std.mem.indexOfScalar(u8, b, ':')) |colon| {
            o.buffer = std.fmt.parseInt(i32, trim(b[0..colon]), 10) catch return null;
            o.retry = b[colon + 1 ..];
        } else o.buffer = std.fmt.parseInt(i32, trim(b), 10) catch return null;
        if (o.buffer <= 0) return null;
    }
    inline for (.{ .{ "--free", "frees" }, .{ "--failure", "failures" }, .{ "--constant", "constants" } }) |row| if (values.get(row[0])) |xs| {
        for (xs.items) |assignment| {
            const at = std.mem.indexOfScalar(u8, assignment, '=');
            try @field(o, row[1]).put(a, if (at) |n| assignment[0..n] else "", if (at) |n| assignment[n + 1 ..] else "");
        }
    };
    return o;
}
fn qual(v: V) []const u8 {
    return text(e.get(v, "type"), "qualType") orelse "";
}
fn constantExpr(v: V) ?[]const u8 {
    if (eq(e.kind(v), "ConstantExpr")) if (text(v, "value")) |s| return s;
    for (e.arr(v, "inner")) |child| if (constantExpr(child)) |s| return s;
    return null;
}
fn declarations(a: A, v: V) !Declarations {
    var d: Declarations = .{};
    for (e.arr(v, "inner")) |node| {
        const k = e.kind(node);
        const name = text(node, "name");
        if (eq(k, "TypedefDecl") and name != null) try d.typedefs.put(a, name.?, qual(node));
        if (eq(k, "RecordDecl") and name != null and e.get(node, "completeDefinition") == .bool and e.get(node, "completeDefinition").bool) try d.complete.put(a, name.?, {});
        if (eq(k, "FunctionDecl") and name != null and !d.functions.contains(name.?)) {
            var params: std.ArrayList(Param) = .empty;
            for (e.arr(node, "inner")) |p| if (eq(e.kind(p), "ParmVarDecl")) try params.append(a, .{ .name = text(p, "name") orelse "", .qual = qual(p) });
            try d.functions.put(a, name.?, .{ .qual = qual(node), .parameters = params.items, .variadic = e.get(node, "variadic") == .bool and e.get(node, "variadic").bool });
        }
        if (eq(k, "EnumDecl")) {
            var next: ?i64 = 0;
            for (e.arr(node, "inner")) |item| if (eq(e.kind(item), "EnumConstantDecl")) {
                const n = if (constantExpr(item)) |literal| std.fmt.parseInt(i64, literal, 10) catch null else next;
                try d.enums.put(a, text(item, "name") orelse "", n);
                next = if (n) |x| if (x < std.math.maxInt(i64)) x + 1 else null else null;
            };
        }
    }
    return d;
}
fn strip(prefix: []const u8, s: []const u8) []const u8 {
    return if (prefix.len > 0 and std.mem.startsWith(u8, s, prefix)) s[prefix.len..] else s;
}
fn stripConst(s: []const u8) struct { value: []const u8, is_const: bool } {
    return if (std.mem.startsWith(u8, s, "const ")) .{ .value = trim(s[6..]), .is_const = true } else .{ .value = s, .is_const = false };
}
fn exact(name: []const u8) ?C {
    const rows = .{ .{ "int8_t", 8, true }, .{ "int16_t", 16, true }, .{ "int32_t", 32, true }, .{ "int64_t", 64, true }, .{ "uint8_t", 8, false }, .{ "uint16_t", 16, false }, .{ "uint32_t", 32, false }, .{ "uint64_t", 64, false }, .{ "size_t", 64, false }, .{ "ssize_t", 64, true }, .{ "intptr_t", 64, true }, .{ "uintptr_t", 64, false }, .{ "signed char", 8, true }, .{ "unsigned char", 8, false }, .{ "short", 16, true }, .{ "unsigned short", 16, false }, .{ "int", 32, true }, .{ "unsigned int", 32, false }, .{ "long", 64, true }, .{ "unsigned long", 64, false }, .{ "long long", 64, true }, .{ "unsigned long long", 64, false } };
    inline for (rows) |row| if (eq(name, row[0])) return .{ .integer = .{ .bits = row[1], .signed = row[2] } };
    return null;
}
fn incomplete(d: Declarations, s: []const u8) bool {
    return std.mem.startsWith(u8, s, "struct ") and !d.complete.contains(trim(s[7..]));
}
fn handleTypedef(d: Declarations, s: []const u8) ?[]const u8 {
    const target = d.typedefs.get(s) orelse return null;
    return if (std.mem.endsWith(u8, target, "*") and incomplete(d, stripConst(trim(target[0 .. target.len - 1])).value)) s else null;
}
fn resolve(d: Declarations, s: []const u8, depth: usize) []const u8 {
    if (depth > 16 or exact(s) != null) return s;
    const target = d.typedefs.get(s) orelse return s;
    if (eq(target, s)) return s;
    return resolve(d, stripConst(target).value, depth + 1);
}
fn classify(a: A, d: Declarations, original: []const u8) anyerror!C {
    const s = trim(original);
    if (std.mem.endsWith(u8, s, "*")) {
        const p = stripConst(trim(s[0 .. s.len - 1]));
        if (handleTypedef(d, p.value)) |name| return if (!p.is_const) .{ .handle_out = name } else .{ .unmapped = try fmt(a, "'{s}' points to a handle the function only reads (describe it in --spec)", .{s}) };
        const target = resolve(d, p.value, 0);
        if (std.mem.indexOfAny(u8, target, "*(") != null) return .{ .unmapped = try fmt(a, "'{s}' is a pointer to a pointer or function", .{s}) };
        if (eq(target, "char")) return .{ .text = p.is_const };
        if (eq(target, "unsigned char") or eq(target, "uint8_t")) return if (p.is_const) .byte_pointer else .mutable_bytes;
        if (incomplete(d, target)) return .{ .handle = trim(target[7..]) };
        if (std.mem.startsWith(u8, target, "struct ")) return .{ .unmapped = try fmt(a, "'{s}' points to a complete struct (describe it in --spec)", .{s}) };
        return .{ .unmapped = try fmt(a, "'{s}' is a pointer with no mapping (describe it in --spec)", .{s}) };
    }
    const bare = stripConst(s).value;
    if (handleTypedef(d, bare)) |name| return .{ .handle = name };
    const target = resolve(d, bare, 0);
    if (eq(target, "void")) return .void_;
    if (eq(target, "_Bool") or eq(target, "bool")) return .boolean;
    if (eq(target, "char")) return .{ .unmapped = "plain 'char' has target-dependent signedness; use signed char, unsigned char or int8_t/uint8_t" };
    if (std.mem.startsWith(u8, target, "enum ")) return .{ .integer = .{ .bits = 32, .signed = true } };
    if (std.mem.endsWith(u8, target, "*")) return classify(a, d, target);
    return exact(target) orelse .{ .unmapped = try fmt(a, "'{s}' has no mapping (structs by value and floats: describe them in --spec)", .{s}) };
}
fn mapCase(point: u21, runs: []const [4]i32) u21 {
    const code: i32 = point;
    var low: usize = 0;
    var high = runs.len;
    while (low < high) {
        const middle = (low + high) / 2;
        if (runs[middle][0] <= code) low = middle + 1 else high = middle;
    }
    if (low > 0) {
        const range = runs[low - 1];
        if (code <= range[1] and @mod(code - range[0], range[2]) == 0) return @intCast(code + range[3]);
    }
    return point;
}
fn changeCase(a: A, s: []const u8, upper_first: bool, char_only: bool) ![]const u8 {
    var out: std.ArrayList(u8) = .empty;
    var it = std.unicode.Utf8View.initUnchecked(s).iterator();
    var i: usize = 0;
    while (it.nextCodepoint()) |c| {
        const first = upper_first and i == 0;
        const runs = if (first) &@import("case_runs.zig").upper else &@import("case_runs.zig").lower;
        const mapped = if ((char_only or first) and c >= 0x10000) c else mapCase(c, runs);
        var buf: [4]u8 = undefined;
        const len = try std.unicode.utf8Encode(mapped, &buf);
        try out.appendSlice(a, buf[0..len]);
        i += 1;
    }
    return out.items;
}
fn upperFirst(a: A, s: []const u8) ![]const u8 {
    if (s.len == 0) return s;
    const n = std.unicode.utf8ByteSequenceLength(s[0]) catch 1;
    return std.mem.concat(a, u8, &.{ try changeCase(a, s[0..n], true, true), s[n..] });
}
fn camel(a: A, s: []const u8) ![]const u8 {
    var out: std.ArrayList(u8) = .empty;
    var parts = std.mem.tokenizeScalar(u8, s, '_');
    var index: usize = 0;
    while (parts.next()) |part| {
        try out.appendSlice(a, try changeCase(a, part, index > 0, false));
        index += 1;
    }
    return out.items;
}
fn pascal(a: A, s: []const u8) ![]const u8 {
    return upperFirst(a, try camel(a, s));
}
fn snake(a: A, s: []const u8) ![]const u8 {
    var out: std.ArrayList(u8) = .empty;
    var it = std.unicode.Utf8View.initUnchecked(s).iterator();
    var index: usize = 0;
    const chars = @embedFile("csharp_facade_chars.bin");
    while (it.nextCodepoint()) |c| {
        if (index > 0 and c < 0x10000) {
            const bit = @as(usize, c) * 3 + 2;
            if (chars[bit / 8] & (@as(u8, 1) << @intCast(bit % 8)) != 0) try out.append(a, '_');
        }
        const mapped = if (c >= 0x10000) c else mapCase(c, &@import("case_runs.zig").lower);
        var buf: [4]u8 = undefined;
        const n = try std.unicode.utf8Encode(mapped, &buf);
        try out.appendSlice(a, buf[0..n]);
        index += 1;
    }
    return out.items;
}
fn handleName(a: A, o: Options, tag: []const u8) ![]const u8 {
    const name = strip(o.type_prefix, strip(o.prefix, tag));
    if (name.len == 0 or std.mem.indexOfScalar(u8, name, '_') != null) return pascal(a, name);
    return upperFirst(a, name);
}
fn cName(a: A, c: C) !?[]const u8 {
    return switch (c) {
        .integer => |n| try fmt(a, "{s}{d}", .{ if (n.signed) "int" else "uint", n.bits }),
        .boolean => "bool",
        .text => |is_const| if (is_const) "utf8" else null,
        .handle => "handle",
        .void_ => "void",
        else => null,
    };
}
fn primitive(a: A, name: []const u8) !V {
    return obj(a, &.{ "kind", "primitive" }, &.{ string("primitive"), string(name) });
}
fn graphType(a: A, c: []const u8) !V {
    return primitive(a, if (eq(c, "bool") or eq(c, "intBool")) "bool" else if (eq(c, "float32") or eq(c, "float64")) "float64" else if (eq(c, "utf8")) "string" else if (eq(c, "bytes")) "bytes" else if (eq(c, "void")) "unit" else "int64");
}
const Constant = struct { value: ?i64, label: ?[]const u8 = null, problem: ?[]const u8 = null };
fn constant(a: A, d: Declarations, v: V) !Constant {
    if (v == .null) return .{ .value = null };
    if (v == .integer) return .{ .value = v.integer };
    if (v == .number_string) return .{ .value = std.fmt.parseInt(i64, v.number_string, 10) catch return error.SpecInteger };
    if (v == .float) return error.SpecInteger;
    if (v == .string) {
        const name = v.string;
        if (std.fmt.parseInt(i64, trim(name), 10)) |n| return .{ .value = n } else |_| {}
        if (eq(name, "null")) return .{ .value = null };
        if (d.enums.get(name)) |n| return if (n == null) .{ .value = null, .problem = try fmt(a, "enumerator '{s}' does not fit Int64", .{name}) } else .{ .value = n, .label = name };
        return .{ .value = null, .problem = try fmt(a, "'{s}' is not an enumerator of the header", .{name}) };
    }
    return .{ .value = null, .problem = "a constant is null, an integer or an enumerator name" };
}
const Complete = struct {
    c: Context,
    classified: []const C,
    params: []const Param,
    position: usize = 0,
    out_type: ?V = null,
    handles: std.ArrayList([]const u8) = .empty,
    parameters: std.ArrayList(V) = .empty,
    problems: std.ArrayList([]const u8) = .empty,
    fn problem(s: *Complete, message: []const u8) !void {
        try s.problems.append(s.c.a, message);
    }
    fn capability(s: *Complete, name: []const u8) !V {
        try s.handles.append(s.c.a, name);
        return obj(s.c.a, &.{ "kind", "name" }, &.{ string("capability"), string(name) });
    }
    fn handleAt(s: *Complete, index: usize, want_out: bool) !?[]const u8 {
        if (index >= s.classified.len) return null;
        const c = s.classified[index];
        if (!want_out and c == .handle) return try handleName(s.c.a, s.c.options, c.handle);
        if (want_out and c == .handle_out) return try handleName(s.c.a, s.c.options, c.handle_out);
        return null;
    }
    fn inferC(s: *Complete, item: *V, index: usize) !void {
        if (has(item.*, "c")) return;
        if (index < s.classified.len) {
            if (try cName(s.c.a, s.classified[index])) |c| {
                try set(s.c.a, item, "c", string(c));
                return;
            }
        }
        try s.problem(try fmt(s.c.a, "argument {d}: name its C type with \"c\"", .{index}));
    }
    fn visit(s: *Complete, item: *V, in_struct: bool) anyerror!void {
        const a = s.c.a;
        if (s.c.masked_spec and objectMarked(item.*)) return error.SpecReadUtf16;
        const index = s.position;
        if (has(item.*, "self")) {
            s.position += 1;
            return;
        }
        if (has(item.*, "out")) {
            const c = try specText(s.c, item.*, "out") orelse "";
            if (eq(c, "bytes")) {
                s.position += 3;
                s.out_type = try primitive(a, "bytes");
                if (has(item.*, "retry")) {
                    if (s.c.masked_spec and marked(e.get(item.*, "retry"))) return error.SpecReadUtf16;
                    const n = try constant(a, s.c.declarations, e.get(item.*, "retry"));
                    if (n.problem) |p| try s.problem(p) else if (n.value) |value| try set(a, item, "retry", .{ .integer = value }) else try s.problem("retry is an integer status");
                }
            } else if (eq(c, "handle")) {
                s.position += 1;
                if (try s.handleAt(index, true)) |handle| s.out_type = try s.capability(handle) else try s.problem(try fmt(a, "argument {d}: not a pointer to a handle", .{index}));
            } else {
                s.position += 1;
                s.out_type = try graphType(a, c);
            }
            return;
        }
        if (has(item.*, "struct")) {
            if (!in_struct) s.position += 1;
            const fields = item.object.getPtr("struct").?;
            if (fields.* == .array) {
                for (fields.array.items) |*field| if (field.* == .object) try s.visit(field, true) else try s.problem("a struct field is an object");
            } else try s.problem("struct is an array of fields");
            return;
        }
        if (has(item.*, "sizeof")) return;
        if (has(item.*, "value")) {
            if (!in_struct) s.position += 1;
            if (s.c.masked_spec and marked(e.get(item.*, "value"))) return error.SpecReadUtf16;
            const n = try constant(a, s.c.declarations, e.get(item.*, "value"));
            if (n.problem) |p| {
                try s.problem(p);
                return;
            }
            try set(a, item, "value", if (n.value) |value| .{ .integer = value } else .null);
            if (n.label) |label| try set(a, item, "name", string(label));
            if (!in_struct and !has(item.*, "c")) {
                if (index < s.params.len and std.mem.endsWith(u8, std.mem.trimEnd(u8, s.params[index].qual, " \t\r\n"), "*") and n.value == null) try set(a, item, "c", string("pointer")) else try s.inferC(item, index);
            }
            return;
        }
        if (!in_struct) {
            s.position += 1;
            try s.inferC(item, index);
        }
        const c = try specText(s.c, item.*, "c") orelse "";
        if (eq(c, "bytes")) s.position += 1;
        const hint = try specText(s.c, item.*, "capability");
        _ = item.object.orderedRemove("capability");
        const t = if (eq(c, "handle")) blk: {
            if ((try s.handleAt(index, false)) orelse hint) |handle| break :blk try s.capability(handle);
            try s.problem(try fmt(a, "argument {d}: not a handle (name its \"capability\")", .{index}));
            break :blk try primitive(a, "unit");
        } else try graphType(a, c);
        try s.parameters.append(a, try obj(a, &.{ "displayName", "type" }, &.{ string(try specText(s.c, item.*, "from") orelse ""), t }));
    }
};
fn complete(c: Context, owner: []const u8, name: []const u8, initial: V) !OpResult {
    const a = c.a;
    var entry = initial;
    const symbol = try specText(c, entry, "symbol") orelse "";
    const function = c.declarations.functions.get(symbol) orelse return .{ .unmapped = try fmt(a, "'{s}' is not declared in the header", .{symbol}) };
    const end = std.mem.indexOfScalar(u8, function.qual, '(') orelse 0;
    const return_text = trim(function.qual[0..end]);
    const classes = try a.alloc(C, function.parameters.len);
    for (function.parameters, classes) |p, *dest| dest.* = try classify(a, c.declarations, p.qual);
    var s: Complete = .{ .c = c, .classified = classes, .params = function.parameters };
    const args = entry.object.getPtr("arguments");
    if (args) |items| {
        if (items.* == .array) {
            for (items.array.items) |*item| if (item.* == .object) try s.visit(item, false) else try s.problem("an argument is an object");
        } else if (items.* != .null) try s.problem("arguments is an array");
    }
    if (s.problems.items.len == 0 and s.position != classes.len) try s.problem(try fmt(a, "the arguments cover {d} of {d} C parameters", .{ s.position, classes.len }));
    var failure = try specText(c, entry, "failure");
    if (failure == null) {
        failure = c.options.failures.get(symbol) orelse c.options.failures.get(return_text);
        if (failure) |value| try set(a, &entry, "failure", string(value));
    }
    const returned = try classify(a, c.declarations, return_text);
    if (!has(entry, "result")) {
        if (try cName(a, returned)) |cn| try set(a, &entry, "result", try obj(a, &.{"c"}, &.{string(cn)})) else try s.problem("result: name its C type with \"result\"");
    }
    const result_c = try specText(c, e.get(entry, "result"), "c") orelse "";
    const ok = s.out_type orelse if (failure != null and eq(failure.?, "nonzero")) try primitive(a, "unit") else if (eq(result_c, "handle")) blk: {
        if (returned == .handle) break :blk try s.capability(try handleName(a, c.options, returned.handle));
        try s.problem("result: not a handle");
        break :blk try primitive(a, "unit");
    } else try graphType(a, result_c);
    if (s.problems.items.len > 0) return .{ .unmapped = try std.mem.join(a, "; ", s.problems.items) };
    const result_type = if (failure != null) try obj(a, &.{ "kind", "generic", "arguments" }, &.{ string("builtinGeneric"), string("result"), try e.array(a, &.{ ok, try primitive(a, "string") }) }) else ok;
    return .{ .mapped = .{ .owner = owner, .name = name, .entry = entry, .handles = s.handles.items, .declaration = try obj(a, &.{ "displayName", "parameters", "resultType", "effect" }, &.{ string(name), try e.array(a, s.parameters.items), result_type, string(c.options.effect) }) } };
}
fn automatic(c: Context, function_name: []const u8) !OpResult {
    const a = c.a;
    const o = c.options;
    const function = c.declarations.functions.get(function_name) orelse return .{ .unmapped = "not declared in the header" };
    const end = std.mem.indexOfScalar(u8, function.qual, '(') orelse 0;
    const returned_text = function.qual[0..end];
    if (std.mem.indexOf(u8, function.qual, "(*") != null or trim(returned_text).len == 0) return .{ .unmapped = try fmt(a, "'{s}' returns a function pointer", .{function.qual}) };
    if (function.variadic or std.mem.indexOf(u8, function.qual, "...") != null) return .{ .unmapped = "variadic functions are out of scope" };
    if (std.mem.endsWith(u8, std.mem.trimEnd(u8, function.qual, " \t\r\n"), "()")) return .{ .unmapped = "the declaration does not specify its parameters (write '(void)')" };
    const is_release = contains(o.releases, function_name);
    var args: std.ArrayList(V) = .empty;
    var index: usize = 0;
    var receiver: ?[]const u8 = null;
    var handle_count: usize = 0;
    for (function.parameters) |p| {
        if ((try classify(a, c.declarations, p.qual)) == .handle) handle_count += 1;
    }
    while (index < function.parameters.len) {
        const p = function.parameters[index];
        if (p.name.len == 0) return .{ .unmapped = "a parameter has no name" };
        const ctype = try classify(a, c.declarations, p.qual);
        if (o.constants.get(p.name)) |value| {
            const pointer = std.mem.endsWith(u8, std.mem.trimEnd(u8, p.qual, " \t\r\n"), "*");
            const cn = if (pointer) "pointer" else (try cName(a, ctype)) orelse return .{ .unmapped = try fmt(a, "parameter {s}: a constant needs an integer or pointer parameter", .{p.name}) };
            try args.append(a, try obj(a, &.{ "value", "c" }, &.{ string(value), string(cn) }));
            index += 1;
            continue;
        }
        const is_size = index + 1 < function.parameters.len and eq(trim(function.parameters[index + 1].qual), "size_t");
        if (index + 2 < function.parameters.len and ((ctype == .text and !ctype.text) or ctype == .mutable_bytes) and is_size) {
            const written = try std.mem.replaceOwned(u8, a, function.parameters[index + 2].qual, " ", "");
            if (eq(written, "size_t*")) {
                var item = try obj(a, &.{ "out", "capacity" }, &.{ string("bytes"), .{ .integer = o.buffer } });
                if (o.retry) |retry| try set(a, &item, "retry", string(retry));
                try args.append(a, item);
                index += 3;
                continue;
            }
        }
        if ((ctype == .text or ctype == .byte_pointer or ctype == .mutable_bytes) and is_size) {
            try args.append(a, try obj(a, &.{ "from", "c" }, &.{ string(try camel(a, p.name)), string("bytes") }));
            index += 2;
            continue;
        }
        switch (ctype) {
            .unmapped => |reason| return .{ .unmapped = try fmt(a, "parameter {s}: {s}", .{ p.name, reason }) },
            .byte_pointer, .mutable_bytes => return .{ .unmapped = try fmt(a, "parameter {s}: a byte pointer must be followed by its size_t length", .{p.name}) },
            .text => |is_const| if (!is_const) return .{ .unmapped = try fmt(a, "parameter {s}: 'char *' may be written by the library; only 'const char *' maps to String", .{p.name}) },
            .void_ => return .{ .unmapped = try fmt(a, "parameter {s}: void", .{p.name}) },
            .handle_out => {
                try args.append(a, try obj(a, &.{"out"}, &.{string("handle")}));
                index += 1;
                continue;
            },
            .handle => |tag| {
                if (index == 0 and !is_release) {
                    receiver = tag;
                    try args.append(a, try obj(a, &.{"self"}, &.{.{ .bool = true }}));
                    index += 1;
                    continue;
                }
            },
            else => {},
        }
        var item = try obj(a, &.{ "from", "c" }, &.{ string(try camel(a, p.name)), string((try cName(a, ctype)).?) });
        if (is_release and ctype == .handle) try set(a, &item, "release", .{ .bool = true });
        try args.append(a, item);
        index += 1;
    }
    const returned = try classify(a, c.declarations, returned_text);
    const failure = o.failures.get(function_name) orelse o.failures.get(trim(returned_text));
    const free = o.frees.get(function_name);
    var outs: usize = 0;
    for (args.items) |arg| {
        if (has(arg, "out")) outs += 1;
    }
    const problem: ?[]const u8 = if (is_release and handle_count != 1) "a release function takes exactly one handle" else if (outs > 1) "more than one out-parameter" else if (returned == .unmapped) try fmt(a, "result: {s}", .{returned.unmapped}) else if (returned == .byte_pointer or returned == .mutable_bytes) "result: a byte pointer has no length (Bytes results are out of scope)" else if (returned == .text and !returned.text and free == null) "result: 'char *' is owned by the caller; name its free function with --free" else if (returned == .text and returned.text and free != null) "result: a 'const char *' result is borrowed; --free does not apply" else if (failure != null and eq(failure.?, "null") and returned != .text and returned != .handle) "--failure null needs a pointer result" else if (failure != null and eq(failure.?, "negative") and (returned != .integer or !returned.integer.signed)) "--failure negative needs a signed integer result" else if (failure != null and eq(failure.?, "nonzero") and returned != .integer) "--failure nonzero needs an integer result" else if (failure != null and !contains(&.{ "null", "negative", "nonzero" }, failure.?)) try fmt(a, "unknown failure '{s}'", .{failure.?}) else if (free != null and (returned != .text or returned.text)) "--free applies to a 'char *' result" else null;
    if (problem) |p| return .{ .unmapped = p };
    const result_c = if (returned == .text and !returned.text) "utf8" else (try cName(a, returned)).?;
    var result = try obj(a, &.{"c"}, &.{string(result_c)});
    if (free) |symbol| try set(a, &result, "free", string(symbol));
    var entry = try obj(a, &.{ "symbol", "arguments", "result" }, &.{ string(function_name), try e.array(a, args.items), result });
    if (failure) |value| try set(a, &entry, "failure", string(value));
    const stripped = strip(o.prefix, function_name);
    const owner = if (receiver) |tag| try handleName(a, o, tag) else o.capability;
    const name = if (receiver != null) try camel(a, strip(try fmt(a, "{s}_", .{try snake(a, owner)}), stripped)) else try camel(a, stripped);
    return complete(c, owner, name, entry);
}
const Result = union(enum) { report: V, problem: []const u8 };
fn clang(a: A, io: std.Io, o: Options) !Result {
    var args: std.ArrayList([]const u8) = .empty;
    try args.appendSlice(a, &.{ "clang", "-x", "c", "-dM", "-E" });
    try args.appendSlice(a, o.clang_args);
    try args.append(a, "/dev/null");
    const macros = try std.process.run(a, io, .{ .argv = args.items });
    if (!macros.term.success()) return .{ .problem = try fmt(a, "clang failed: {s}", .{macros.stderr}) };
    if (std.mem.indexOf(u8, macros.stdout, "#define __LP64__ 1") == null) return .{ .problem = "bind-c supports LP64 targets only (Linux, macOS); clang's target is not LP64." };
    args = .empty;
    try args.appendSlice(a, &.{ "clang", "-x", "c", "-Xclang", "-ast-dump=json", "-fsyntax-only" });
    try args.appendSlice(a, o.clang_args);
    try args.append(a, o.header);
    const ast = try std.process.run(a, io, .{ .argv = args.items });
    if (!ast.term.success()) return .{ .problem = try fmt(a, "clang failed: {s}", .{ast.stderr}) };
    return .{ .report = try std.json.parseFromSliceLeaky(V, a, ast.stdout, .{ .duplicate_field_behavior = .use_last }) };
}
fn json(out: *std.Io.Writer, v: V, pretty: bool, depth: usize) anyerror!void {
    switch (v) {
        .null => try out.writeAll("null"),
        .bool => |b| try out.writeAll(if (b) "true" else "false"),
        .integer => |n| try out.print("{d}", .{n}),
        .float => try std.json.Stringify.value(v, .{}, out),
        .number_string => |n| try out.writeAll(n),
        .string => |s| try root.writeString(out, s),
        .object => |map| {
            try out.writeByte('{');
            for (map.keys(), map.values(), 0..) |key, value, i| {
                if (i > 0) try out.writeByte(',');
                if (pretty) {
                    try out.writeByte('\n');
                    try out.splatByteAll(' ', (depth + 1) * 2);
                }
                try root.writeString(out, key);
                try out.writeAll(if (pretty) ": " else ":");
                try json(out, value, pretty, depth + 1);
            }
            if (pretty and map.count() > 0) {
                try out.writeByte('\n');
                try out.splatByteAll(' ', depth * 2);
            }
            try out.writeByte('}');
        },
        .array => |xs| {
            try out.writeByte('[');
            for (xs.items, 0..) |value, i| {
                if (i > 0) try out.writeByte(',');
                if (pretty) {
                    try out.writeByte('\n');
                    try out.splatByteAll(' ', (depth + 1) * 2);
                }
                try json(out, value, pretty, depth + 1);
            }
            if (pretty and xs.items.len > 0) {
                try out.writeByte('\n');
                try out.splatByteAll(' ', depth * 2);
            }
            try out.writeByte(']');
        },
    }
}
fn jsonText(a: A, v: V, pretty: bool) ![]const u8 {
    var out: std.Io.Writer.Allocating = .init(a);
    try json(&out.writer, v, pretty, 0);
    return out.written();
}
const Selected = struct { symbol: []const u8, result: OpResult };
const decodeText = @import("managed_text.zig").decode;
const SpecRole = enum { root, capabilities, capability, operations, entry, arguments, argument, result, ignored };
fn specRole(parent: SpecRole, key: []const u8) SpecRole {
    return switch (parent) {
        .root => if (eq(key, "capabilities")) .capabilities else .ignored,
        .capabilities => .capability,
        .capability => if (eq(key, "operations")) .operations else .ignored,
        .operations => .entry,
        .entry => if (eq(key, "arguments")) .arguments else if (eq(key, "result")) .result else .ignored,
        .arguments => .argument,
        .argument => if (eq(key, "struct")) .arguments else .ignored,
        else => .ignored,
    };
}
fn specPosition(a: A, source: []const u8, position: usize, message: []const u8) ![]const u8 {
    var line: usize = 0;
    var column: usize = 0;
    for (source[0..@min(position, source.len)]) |c| {
        if (c == '\n') {
            line += 1;
            column = 0;
        } else column += 1;
    }
    return fmt(a, "{s} LineNumber: {d} | BytePositionInLine: {d}.", .{ message, line, column });
}
fn specSyntaxProblem(a: A, source: []const u8, scanner: *std.json.Scanner, parent_object: bool, expecting_name: bool, depth: usize) ![]const u8 {
    const state = @tagName(scanner.state);
    const position = scanner.cursor;
    const end = position >= source.len;
    const byte = if (end) @as(u8, 0) else source[position];
    const printable = if (byte >= 0x20 and byte < 0x7f) try a.dupe(u8, &.{byte}) else try fmt(a, "0x{X:0>2}", .{byte});
    if (trim(source).len == 0) return specPosition(a, source, source.len, "The input does not contain any JSON tokens. Expected the input to start with a valid JSON token, when isFinalBlock is true.");
    if (std.mem.startsWith(u8, state, "literal_")) {
        const prefix = state[8..];
        const literal = if (prefix.len > 0 and prefix[0] == 't') "true" else if (prefix.len > 0 and prefix[0] == 'f') "false" else "null";
        const begin = position - @min(position, prefix.len);
        return specPosition(a, source, position, try fmt(a, "'{s}' is an invalid JSON literal. Expected the literal '{s}'.", .{ source[begin..@min(position + 1, source.len)], literal }));
    }
    if (std.mem.startsWith(u8, state, "number_")) {
        if (end) return specPosition(a, source, position, "Expected a digit ('0'-'9'), but instead reached end of data.");
        const after = if (eq(state, "number_post_dot")) "a decimal point ('.')" else "a sign character ('+' or '-')";
        return specPosition(a, source, position, try fmt(a, "'{s}' is invalid within a number, immediately after {s}. Expected a digit ('0'-'9').", .{ printable, after }));
    }
    if (std.mem.startsWith(u8, state, "string")) {
        if (end) return specPosition(a, source, position, "Expected end of string, but instead reached end of data.");
        if (eq(state, "string")) return specPosition(a, source, position, try fmt(a, "'{s}' is invalid within a JSON string. The string should be correctly escaped.", .{printable}));
        if (eq(state, "string_backslash")) return specPosition(a, source, position, try fmt(a, "'{s}' is an invalid escapable character within a JSON string. The string should be correctly escaped.", .{printable}));
        if (std.mem.startsWith(u8, state, "string_backslash_u")) return specPosition(a, source, position, try fmt(a, "'{s}' is not a hex digit following '\\u' within a JSON string. The string should be correctly escaped.", .{printable}));
    }
    if (end) {
        const previous = std.mem.trimEnd(u8, source, " \t\r\n");
        if (previous.len > 0 and previous[previous.len - 1] == ',') return specPosition(a, source, previous.len - 1, "Expected start of a property name or value, but instead reached end of data.");
        if (eq(state, "post_value") and parent_object and !expecting_name) return specPosition(a, source, position, "Expected a value, but instead reached end of data.");
        if (eq(state, "post_value") and depth > 0 and source.len > 0 and std.ascii.isDigit(source[source.len - 1])) return specPosition(a, source, position, try fmt(a, "'{c}' is an invalid end of a number. Expected a delimiter.", .{source[source.len - 1]}));
        return specPosition(a, source, position, "Expected depth to be zero at the end of the JSON payload. There is an open JSON object or array that should be closed.");
    }
    if ((eq(state, "value") and byte == ']') or (eq(state, "object_post_comma") and byte == '}')) return specPosition(a, source, position, try fmt(a, "The JSON {s} contains a trailing comma at the end which is not supported in this mode. Change the reader options.", .{if (byte == ']') "array" else "object"}));
    if (eq(state, "object_start") or eq(state, "object_post_comma")) return specPosition(a, source, position, try fmt(a, "'{s}' is an invalid start of a property name. Expected a '\"'.", .{printable}));
    if (eq(state, "post_value")) {
        if (position > 0 and source[position - 1] == '0' and std.ascii.isDigit(byte)) return specPosition(a, source, position, try fmt(a, "Invalid leading zero before '{s}'.", .{printable}));
        const suffix = if (depth == 0) "after a single JSON value. Expected end of data." else if (parent_object and !expecting_name) "after a property name. Expected a ':'." else "after a value. Expected either ',', '}', or ']'.";
        return specPosition(a, source, position, try fmt(a, "'{s}' is invalid {s}", .{ printable, suffix }));
    }
    return specPosition(a, source, position, try fmt(a, "'{s}' is an invalid start of a value.", .{printable}));
}
fn marked(value: V) bool {
    return value == .string and std.mem.indexOf(u8, value.string, @import("query.zig").lone_mark) != null;
}
fn objectMarked(value: V) bool {
    if (value != .object) return false;
    for (value.object.keys()) |key| if (std.mem.indexOf(u8, key, @import("query.zig").lone_mark) != null) return true;
    return false;
}
fn specText(c: Context, value: V, key: []const u8) !?[]const u8 {
    if (c.masked_spec and (objectMarked(value) or marked(e.get(value, key)))) return error.SpecReadUtf16;
    return text(value, key);
}
fn containsSpecMarks(value: V) bool {
    return switch (value) {
        .string => marked(value),
        .object => |map| blk: {
            for (map.keys(), map.values()) |key, item| {
                if (std.mem.indexOf(u8, key, @import("query.zig").lone_mark) != null or containsSpecMarks(item)) break :blk true;
            }
            break :blk false;
        },
        .array => |xs| blk: {
            for (xs.items) |item| if (containsSpecMarks(item)) break :blk true;
            break :blk false;
        },
        else => false,
    };
}
fn specInputProblem(a: A, source: []const u8) !?[]const u8 {
    const Frame = struct { role: SpecRole, object: bool, expecting_name: bool, key: []const u8 = "", names: std.StringHashMapUnmanaged(void) = .empty };
    var frames: std.ArrayList(Frame) = .empty;
    var scanner = std.json.Scanner.initCompleteInput(a, source);
    defer scanner.deinit();
    var duplicate: ?[]const u8 = null;
    while (true) {
        const token = scanner.nextAlloc(a, .alloc_always) catch {
            const parent = if (frames.items.len > 0) frames.items[frames.items.len - 1] else null;
            return try specSyntaxProblem(a, source, &scanner, if (parent) |frame| frame.object else false, if (parent) |frame| frame.expecting_name else false, frames.items.len);
        };
        switch (token) {
            .end_of_document => {
                if (trim(source).len == 0) return try specPosition(a, source, 0, "The input does not contain any JSON tokens. Expected the input to start with a valid JSON token, when isFinalBlock is true.");
                return if (duplicate) |key| try fmt(a, "An item with the same key has already been added. Key: {s} (Parameter 'key')", .{key}) else null;
            },
            .object_begin, .array_begin => {
                if (frames.items.len == 64) return try specPosition(a, source, scanner.cursor - 1, if (token == .object_begin) "The maximum configured depth of 64 has been exceeded. Cannot read next JSON object." else "The maximum configured depth of 64 has been exceeded. Cannot read next JSON array.");
                const role = if (frames.items.len == 0) SpecRole.root else blk: {
                    const parent = &frames.items[frames.items.len - 1];
                    parent.expecting_name = parent.object;
                    break :blk specRole(parent.role, parent.key);
                };
                try frames.append(a, .{ .role = role, .object = token == .object_begin, .expecting_name = token == .object_begin });
            },
            .object_end, .array_end => {
                _ = frames.pop();
            },
            .allocated_string, .string => |value| {
                if (frames.items.len > 0) {
                    const parent = &frames.items[frames.items.len - 1];
                    if (parent.expecting_name) {
                        if (parent.role != .ignored) {
                            if ((try parent.names.fetchPut(a, value, {})) != null and duplicate == null) duplicate = value;
                        }
                        parent.key = value;
                        parent.expecting_name = false;
                    } else parent.expecting_name = parent.object;
                }
            },
            else => {
                if (frames.items.len > 0) frames.items[frames.items.len - 1].expecting_name = frames.items[frames.items.len - 1].object;
            },
        }
    }
}
fn generate(a: A, io: std.Io, o: Options) !Result {
    const ast = switch (try clang(a, io, o)) {
        .problem => |p| return .{ .problem = p },
        .report => |v| v,
    };
    const c: Context = .{ .a = a, .io = io, .options = o, .declarations = try declarations(a, ast) };
    var described: std.ArrayList(Selected) = .empty;
    var masked_spec = false;
    if (o.spec) |path| {
        const bytes = std.Io.Dir.cwd().readFileAlloc(io, path, a, .unlimited) catch |err| {
            const full = try std.fs.path.resolveAlloc(a, &.{ try std.process.currentPathAlloc(io, a), path });
            const message = switch (err) {
                error.FileNotFound => try fmt(a, "Could not find file '{s}'.", .{full}),
                error.NotDir => try fmt(a, "Could not find a part of the path '{s}'.", .{full}),
                error.AccessDenied => try fmt(a, "Access to the path '{s}' is denied.", .{full}),
                else => @errorName(err),
            };
            return .{ .problem = try fmt(a, "--spec {s}: {s}", .{ path, message }) };
        };
        const decoded = try decodeText(a, bytes);
        const masked = try root.maskLoneSurrogates(a, decoded);
        masked_spec = masked.line != null;
        const input = masked.line orelse decoded;
        if (try specInputProblem(a, input)) |problem| return .{ .problem = try fmt(a, "--spec {s}: {s}", .{ path, problem }) };
        const spec = std.json.parseFromSliceLeaky(V, a, input, .{ .duplicate_field_behavior = .use_last, .parse_numbers = false }) catch |err| return .{ .problem = try fmt(a, "--spec {s}: {s}", .{ path, @errorName(err) }) };
        var spec_context = c;
        spec_context.masked_spec = masked_spec;
        if (masked_spec and objectMarked(spec)) return .{ .problem = try fmt(a, "--spec {s}: {s}", .{ path, @import("query.zig").lone_message }) };
        const caps = e.get(spec, "capabilities");
        if (spec == .null or (spec == .object and caps == .null)) return .{ .problem = try fmt(a, "--spec {s}: Object reference not set to an instance of an object.", .{path}) };
        if (caps != .object) return .{ .problem = try fmt(a, "--spec {s}: The node must be of type 'JsonObject'.", .{path}) };
        if (masked_spec and objectMarked(caps)) return .{ .problem = try fmt(a, "--spec {s}: {s}", .{ path, @import("query.zig").lone_message }) };
        for (caps.object.keys(), caps.object.values()) |owner, cap| {
            if (masked_spec and objectMarked(cap)) return .{ .problem = try fmt(a, "--spec {s}: {s}", .{ path, @import("query.zig").lone_message }) };
            const ops = e.get(cap, "operations");
            if (cap == .null or (cap == .object and ops == .null)) return .{ .problem = try fmt(a, "--spec {s}: Object reference not set to an instance of an object.", .{path}) };
            if (ops != .object) return .{ .problem = try fmt(a, "--spec {s}: The node must be of type 'JsonObject'.", .{path}) };
            if (masked_spec and objectMarked(ops)) return .{ .problem = try fmt(a, "--spec {s}: {s}", .{ path, @import("query.zig").lone_message }) };
            for (ops.object.keys(), ops.object.values()) |name, value| {
                if (value == .null) return .{ .problem = try fmt(a, "--spec {s}: Object reference not set to an instance of an object.", .{path}) };
                if (value != .object) return .{ .problem = try fmt(a, "--spec {s}: The node must be of type 'JsonObject'.", .{path}) };
                const symbol = (specText(spec_context, value, "symbol") catch return .{ .problem = try fmt(a, "--spec {s}: {s}", .{ path, @import("query.zig").lone_message }) }) orelse "";
                const result = complete(spec_context, owner, name, value) catch |err| switch (err) {
                    error.SpecReadUtf16 => return .{ .problem = try fmt(a, "--spec {s}: {s}", .{ path, @import("query.zig").lone_message }) },
                    error.SpecInteger => return .{ .problem = try fmt(a, "--spec {s}: An element of type 'Number' cannot be converted to a 'System.Int64'.", .{path}) },
                    else => return err,
                };
                try described.append(a, .{ .symbol = symbol, .result = switch (result) {
                    .mapped => result,
                    .unmapped => |p| .{ .unmapped = try fmt(a, "{s}.{s}: {s}", .{ owner, name, p }) },
                } });
            }
        }
    }
    var results: std.ArrayList(Selected) = .empty;
    for (o.functions) |name| {
        for (described.items) |item| {
            if (eq(item.symbol, name)) break;
        } else {
            try results.append(a, .{ .symbol = name, .result = try automatic(c, name) });
        }
    }
    try results.appendSlice(a, described.items);
    var mapped: std.ArrayList(Operation) = .empty;
    var unmapped: std.ArrayList(V) = .empty;
    for (results.items) |item| switch (item.result) {
        .mapped => |op| try mapped.append(a, op),
        .unmapped => |reason| try unmapped.append(a, try obj(a, &.{ "function", "reason" }, &.{ string(item.symbol), string(reason) })),
    };
    var unique: std.ArrayList(Operation) = .empty;
    for (mapped.items) |op| {
        var count: usize = 0;
        for (mapped.items) |other| {
            if (eq(op.owner, other.owner) and eq(op.name, other.name)) count += 1;
        }
        if (count > 1) try unmapped.append(a, try obj(a, &.{ "function", "reason" }, &.{ e.get(op.entry, "symbol"), string(try fmt(a, "operation name {s}.{s} is not unique", .{ op.owner, op.name })) })) else try unique.append(a, op);
    }
    var handles: std.ArrayList([]const u8) = .empty;
    for (unique.items) |op| for (op.handles) |handle| {
        if (!eq(handle, o.capability) and !contains(handles.items, handle)) try handles.append(a, handle);
    };
    for (unique.items) |op| if (!eq(op.owner, o.capability) and !contains(handles.items, op.owner)) try handles.append(a, op.owner);
    std.mem.sort([]const u8, handles.items, {}, @import("query.zig").ordinal);
    var ordered: std.ArrayList([]const u8) = .empty;
    var pending = handles;
    while (pending.items.len > 0) {
        var selected: ?usize = null;
        for (pending.items, 0..) |name, i| {
            var ready = true;
            for (unique.items) |op| if (eq(op.owner, name)) {
                for (op.handles) |handle| {
                    if (!eq(handle, name) and !contains(ordered.items, handle)) ready = false;
                }
            };
            if (ready) {
                selected = i;
                break;
            }
        }
        if (selected) |i| {
            try ordered.append(a, pending.items[i]);
            _ = pending.orderedRemove(i);
        } else break;
    }
    const cyclic = pending.items;
    if (cyclic.len == 0) try ordered.append(a, o.capability);
    var lines: std.Io.Writer.Allocating = .init(a);
    const effect = try obj(a, &.{ "id", "op", "displayName", "exported" }, &.{ string("bind-c-0"), string("simulateCreateEffect"), string(o.effect), .{ .bool = true } });
    try json(&lines.writer, effect, false, 0);
    try lines.writer.writeByte('\n');
    var table_caps: V = try obj(a, &.{}, &.{});
    for (ordered.items, 0..) |name, i| {
        var ops: std.ArrayList(V) = .empty;
        var entries = try obj(a, &.{}, &.{});
        for (unique.items) |op| if (eq(op.owner, name)) {
            try ops.append(a, op.declaration);
            try set(a, &entries, op.name, op.entry);
        };
        const request = try obj(a, &.{ "id", "op", "displayName", "exported", "operations" }, &.{ string(try fmt(a, "bind-c-{d}", .{i + 1})), string("simulateCreateCapabilityByName"), string(name), .{ .bool = true }, try e.array(a, ops.items) });
        try json(&lines.writer, request, false, 0);
        try lines.writer.writeByte('\n');
        const cap = if (eq(name, o.capability)) try obj(a, &.{"operations"}, &.{entries}) else try obj(a, &.{ "handle", "operations" }, &.{ .{ .bool = true }, entries });
        try set(a, &table_caps, name, cap);
    }
    const table = try obj(a, &.{ "format", "version", "library", "capabilities" }, &.{ string("nyrwvy.csharp-mapping"), .{ .integer = 1 }, string(o.library), table_caps });
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = o.requests, .data = lines.written() });
    if (masked_spec and containsSpecMarks(table)) return error.SpecInvalidUtf16;
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = o.table, .data = try std.mem.concat(a, u8, &.{ try jsonText(a, table, true), "\n" }) });
    var caps: std.ArrayList(V) = .empty;
    for (ordered.items) |name| try caps.append(a, string(name));
    var cycles: std.ArrayList(V) = .empty;
    for (cyclic) |name| try cycles.append(a, string(name));
    return .{ .report = try obj(a, &.{ "requests", "table", "capabilities", "unmapped", "cyclicHandles" }, &.{ string(o.requests), string(o.table), try e.array(a, caps.items), try e.array(a, unmapped.items), try e.array(a, cycles.items) }) };
}
pub fn run(a: A, io: std.Io, args: []const []const u8, out: *std.Io.Writer, err: *std.Io.Writer) !u8 {
    const options = (try parse(a, args)) orelse {
        try err.writeAll(usage);
        try err.writeByte('\n');
        return 2;
    };
    switch (try generate(a, io, options)) {
        .problem => |message| {
            try err.writeAll(message);
            try err.writeByte('\n');
            return 2;
        },
        .report => |report| {
            try json(out, report, false, 0);
            try out.writeByte('\n');
            return 0;
        },
    }
}

test "bind-c parse and LP64 classifications" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    try std.testing.expect((try parse(a, &.{"--header"})) == null);
    try std.testing.expect((try classify(a, .{}, "unsigned long")) == .integer);
    try std.testing.expectEqual(@as(i32, 64), (try classify(a, .{}, "unsigned long")).integer.bits);
    try std.testing.expect((try classify(a, .{}, "float")) == .unmapped);
    try std.testing.expectEqualStrings("helloWorld", try camel(a, "hello_WORLD"));
    try std.testing.expectEqualStrings("fK", try camel(a, "f_K"));
    try std.testing.expectEqualStrings("f", (try split(a, "\u{3000}f\u{a0}", ','))[0]);
}

test "bind-c specs decode BOM text and reject numeric constants before publication" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    try std.testing.expectEqualStrings("{}", try decodeText(a, "\xff\xfe{\x00}\x00"));
    try std.testing.expectEqualStrings("{}", try decodeText(a, "\xfe\xff\x00{\x00}"));
    try std.testing.expectEqualStrings("{}", try decodeText(a, "\xff\xfe\x00\x00{\x00\x00\x00}\x00\x00\x00"));
    try std.testing.expectEqualStrings("{}", try decodeText(a, "\xef\xbb\xbf{}"));
    try std.testing.expectError(error.SpecInteger, constant(a, .{}, .{ .float = 1.5 }));
    try std.testing.expectEqual(@as(?i64, 1), (try constant(a, .{}, .{ .number_string = "1" })).value);
    try std.testing.expectError(error.SpecInteger, constant(a, .{}, .{ .number_string = "1.5" }));
    const preserved = try std.json.parseFromSliceLeaky(V, a, "[1.5,1e+02,-0,1.2300,18446744073709551616]", .{ .parse_numbers = false });
    for (preserved.array.items, [_][]const u8{ "1.5", "1e+02", "-0", "1.2300", "18446744073709551616" }) |number, lexical| try std.testing.expectEqualStrings(lexical, number.number_string);
    try std.testing.expectEqualStrings("An item with the same key has already been added. Key: symbol (Parameter 'key')", (try specInputProblem(a, "{\"capabilities\":{\"X\":{\"operations\":{\"f\":{\"symbol\":\"x\",\"symbol\":\"y\"}}}}}")).?);
    try std.testing.expect((try specInputProblem(a, "{\"capabilities\":{},\"unused\":{\"x\":1,\"x\":2}}")) == null);
    const deep = try a.alloc(u8, 131);
    @memset(deep[0..65], '[');
    deep[65] = '0';
    @memset(deep[66..], ']');
    try std.testing.expect(std.mem.startsWith(u8, (try specInputProblem(a, deep)).?, "The maximum configured depth of 64 has been exceeded."));
}

test "bind-c syntax diagnostics retain JsonNode positions and accessed UTF16 strings" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    const a = arena.allocator();
    const rows = .{
        .{ "{x:1}", "'x' is an invalid start of a property name. Expected a '\"'. LineNumber: 0 | BytePositionInLine: 1." },
        .{ "[1,]", "The JSON array contains a trailing comma at the end which is not supported in this mode. Change the reader options. LineNumber: 0 | BytePositionInLine: 3." },
        .{ "1e+", "Expected a digit ('0'-'9'), but instead reached end of data. LineNumber: 0 | BytePositionInLine: 3." },
        .{ "{\"a\":1", "'1' is an invalid end of a number. Expected a delimiter. LineNumber: 0 | BytePositionInLine: 6." },
    };
    inline for (rows) |row| try std.testing.expectEqualStrings(row[1], (try specInputProblem(a, row[0])).?);
    const unused = try std.json.parseFromSliceLeaky(V, a, "{\"capabilities\":{},\"unused\":\"\\uFDD0\"}", .{});
    try std.testing.expect(!objectMarked(unused));
    const read = try std.json.parseFromSliceLeaky(V, a, "{\"capabilities\":{\"C\":{\"operations\":{\"f\":{\"symbol\":\"\\uFDD0\"}}}}}", .{});
    try std.testing.expect(containsSpecMarks(read));
}
