//! The Nyrwvy Zig backend emitter: reads one Execution IR JSON file (the form
//! ExecutionIrSerializer writes) and prints the runPure runner the F# `ZigBackend.runner` produces
//! for it, dispatching the entry named after the file or else every runnable function. Exit 0
//! prints the source; exit 2 prints unsupported constructs as JSON strings, one per line. See
//! docs/decisions/zig-tools.md.
//!
//! `runtime.zig`, `runner_support.zig` and `case_mapping.zig` beside this file are text the emitter copies into its
//! output, not modules of the emitter.
const std = @import("std");
const json = std.json;

const runtime = @embedFile("runtime.zig");
const case_mapping = @embedFile("case_mapping.zig");
const runner_support = @embedFile("runner_support.zig");
const library_support = @embedFile("library_support.zig");
pub const build_source = @embedFile("main.zig");

pub fn functionName(allocator: std.mem.Allocator, id: []const u8) []const u8 {
    a = allocator;
    return identifier("f_", id);
}

var a: std.mem.Allocator = undefined;

fn cat(parts: []const []const u8) []const u8 {
    return std.mem.concat(a, u8, parts) catch oom();
}

fn join(separator: []const u8, items: []const []const u8) []const u8 {
    return std.mem.join(a, separator, items) catch oom();
}

fn oom() noreturn {
    @panic("out of memory");
}

fn eql(left: []const u8, right: []const u8) bool {
    return std.mem.eql(u8, left, right);
}

fn List(comptime T: type) type {
    return std.ArrayList(T);
}

fn push(comptime T: type, list: *List(T), item: T) void {
    list.append(a, item) catch oom();
}

// ---------------------------------------------------------------- types

const Type = union(enum) {
    /// The IR kind, such as "primitiveInt64".
    primitive: []const u8,
    type_parameter: []const u8,
    named: struct { id: []const u8, args: []const *const Type },
    capability: []const u8,
    /// IR kind ("option", "result", "map", ...) and its type arguments in IR field order.
    builtin: struct { kind: []const u8, args: []const *const Type },
    function: struct { params: []const *const Type, result: *const Type },
};

/// The IR fields of a builtin generic's type arguments, in type-argument order.
fn builtinFields(kind: []const u8) []const []const u8 {
    const table = .{
        .{ "option", &[_][]const u8{"itemType"} },
        .{ "list", &[_][]const u8{"itemType"} },
        .{ "set", &[_][]const u8{"itemType"} },
        .{ "result", &[_][]const u8{ "okType", "errorType" } },
        .{ "map", &[_][]const u8{ "keyType", "valueType" } },
        .{ "oneShot", &[_][]const u8{"valueType"} },
        .{ "task", &[_][]const u8{"resultType"} },
        .{ "exception", &[_][]const u8{} },
        .{ "taskScope", &[_][]const u8{} },
    };
    inline for (table) |row| if (eql(kind, row[0])) return row[1];
    std.debug.panic("unknown type kind {s}", .{kind});
}

fn newType(value: Type) *const Type {
    const item = a.create(Type) catch oom();
    item.* = value;
    return item;
}

fn parseTypes(items: []const json.Value) []const *const Type {
    const out = a.alloc(*const Type, items.len) catch oom();
    for (items, 0..) |item, at| out[at] = parseType(item);
    return out;
}

fn parseType(value: json.Value) *const Type {
    const o = value.object;
    const kind = str(o, "kind");
    if (std.mem.startsWith(u8, kind, "primitive")) return newType(.{ .primitive = kind });
    if (eql(kind, "typeParameter")) return newType(.{ .type_parameter = str(o, "typeParameterId") });
    if (eql(kind, "capability")) return newType(.{ .capability = str(o, "capabilityId") });
    if (eql(kind, "named")) return newType(.{ .named = .{ .id = str(o, "typeId"), .args = parseTypes(arr(o, "arguments")) } });
    if (eql(kind, "function")) return newType(.{ .function = .{ .params = parseTypes(arr(o, "parameters")), .result = parseType(o.get("resultType").?) } });
    const fields = builtinFields(kind);
    const args = a.alloc(*const Type, fields.len) catch oom();
    for (fields, 0..) |field, at| args[at] = parseType(o.get(field).?);
    return newType(.{ .builtin = .{ .kind = kind, .args = args } });
}

/// A JSON string as System.Text.Json's default encoder writes it, so hashes match the F# side.
fn quote(out: *List(u8), text: []const u8) void {
    push(u8, out, '"');
    var view = std.unicode.Utf8View.initUnchecked(text);
    var points = view.iterator();
    while (points.nextCodepoint()) |point| {
        switch (point) {
            '\\' => out.appendSlice(a, "\\\\") catch oom(),
            '\n' => out.appendSlice(a, "\\n") catch oom(),
            '\r' => out.appendSlice(a, "\\r") catch oom(),
            '\t' => out.appendSlice(a, "\\t") catch oom(),
            0x08 => out.appendSlice(a, "\\b") catch oom(),
            0x0C => out.appendSlice(a, "\\f") catch oom(),
            '"', '&', '\'', '+', '<', '>', '`' => escapeUnit(out, point),
            0x20...0x21, 0x23...0x25, 0x28...0x2A, 0x2C...0x3B, 0x3D, 0x3F...0x5B, 0x5D...0x5F, 0x61...0x7E => push(u8, out, @intCast(point)),
            else => if (point >= 0x10000) {
                const shifted = point - 0x10000;
                escapeUnit(out, 0xD800 + (shifted >> 10));
                escapeUnit(out, 0xDC00 + (shifted & 0x3FF));
            } else escapeUnit(out, point),
        }
    }
    push(u8, out, '"');
}

fn escapeUnit(out: *List(u8), unit: u21) void {
    out.print(a, "\\u{X:0>4}", .{unit}) catch oom();
}

/// The type in IR JSON form, byte for byte as ExecutionIrSerializer writes it.
fn writeType(out: *List(u8), t: *const Type) void {
    const S = struct {
        fn prop(o: *List(u8), name: []const u8) void {
            o.print(a, ",\"{s}\":", .{name}) catch oom();
        }
        fn list(o: *List(u8), items: []const *const Type) void {
            push(u8, o, '[');
            for (items, 0..) |item, at| {
                if (at > 0) push(u8, o, ',');
                writeType(o, item);
            }
            push(u8, o, ']');
        }
    };
    out.appendSlice(a, "{\"kind\":") catch oom();
    switch (t.*) {
        .primitive => |kind| quote(out, kind),
        .type_parameter => |id| {
            quote(out, "typeParameter");
            S.prop(out, "typeParameterId");
            quote(out, id);
        },
        .capability => |id| {
            quote(out, "capability");
            S.prop(out, "capabilityId");
            quote(out, id);
        },
        .named => |named| {
            quote(out, "named");
            S.prop(out, "typeId");
            quote(out, named.id);
            if (named.args.len > 0) {
                S.prop(out, "arguments");
                S.list(out, named.args);
            }
        },
        .function => |function| {
            quote(out, "function");
            S.prop(out, "parameters");
            S.list(out, function.params);
            S.prop(out, "resultType");
            writeType(out, function.result);
        },
        .builtin => |builtin| {
            quote(out, builtin.kind);
            for (builtinFields(builtin.kind), builtin.args) |field, arg| {
                S.prop(out, field);
                writeType(out, arg);
            }
        },
    }
    push(u8, out, '}');
}

fn typeJson(t: *const Type) []const u8 {
    var out: List(u8) = .empty;
    writeType(&out, t);
    return out.items;
}

/// TypeRef.ToString's 80-column union/tuple/list layout, including nested unsupported types.
const TypeDoc = union(enum) {
    text: []const u8,
    line,
    sequence: []const TypeDoc,
    nest: struct { by: usize, child: *const TypeDoc },
    group: *const TypeDoc,
    aligned: *const TypeDoc,

    fn ptr(doc: TypeDoc) *const TypeDoc {
        const out = a.create(TypeDoc) catch oom();
        out.* = doc;
        return out;
    }
    fn seq(docs: []const TypeDoc) TypeDoc {
        return .{ .sequence = a.dupe(TypeDoc, docs) catch oom() };
    }
    fn nested(by: usize, doc: TypeDoc) TypeDoc {
        return .{ .nest = .{ .by = by, .child = ptr(doc) } };
    }
    fn grouped(doc: TypeDoc) TypeDoc {
        return .{ .aligned = ptr(.{ .group = ptr(doc) }) };
    }
    fn case(name: []const u8, payload: TypeDoc) TypeDoc {
        return grouped(seq(&.{ .{ .text = name }, nested(2, seq(&.{ .line, payload })) }));
    }
    fn tuple(first: TypeDoc, second: TypeDoc) TypeDoc {
        return grouped(seq(&.{ .{ .text = "(" }, first, .{ .text = "," }, nested(1, seq(&.{ .line, second })), .{ .text = ")" } }));
    }
};

fn typeDoc(t: *const Type) TypeDoc {
    return switch (t.*) {
        .primitive => |kind| TypeDoc.case("Primitive", .{ .text = kind["primitive".len..] }),
        .capability => |id| TypeDoc.case("Capability", stableIdDoc(id)),
        .type_parameter => |id| TypeDoc.case("TypeParameter", stableIdDoc(id)),
        .named => |named| TypeDoc.case("Named", TypeDoc.tuple(TypeDoc.case("StableId", .{ .text = diagnosticId(named.id) }), typesDoc(named.args))),
        .function => |function| TypeDoc.case("Function", TypeDoc.tuple(typesDoc(function.params), typeDoc(function.result))),
        .builtin => |builtin| TypeDoc.case("BuiltinGeneric", TypeDoc.tuple(.{ .text = cat(&.{ &.{std.ascii.toUpper(builtin.kind[0])}, builtin.kind[1..] }) }, typesDoc(builtin.args))),
    };
}

fn stableIdDoc(id: []const u8) TypeDoc {
    return TypeDoc.grouped(TypeDoc.seq(&.{ .{ .text = "(" }, TypeDoc.case("StableId", .{ .text = diagnosticId(id) }), .{ .text = ")" } }));
}

fn diagnosticId(id: []const u8) []const u8 {
    // StableId's F# union display quotes its payload verbatim, including quotes and backslashes.
    return cat(&.{ "\"", id, "\"" });
}

fn typesDoc(types: []const *const Type) TypeDoc {
    if (types.len == 0) return .{ .text = "[]" };
    var items: List(TypeDoc) = .empty;
    for (types, 0..) |t, i| {
        if (i > 0) items.appendSlice(a, &.{ .{ .text = ";" }, .line }) catch oom();
        push(TypeDoc, &items, typeDoc(t));
    }
    return TypeDoc.grouped(TypeDoc.seq(&.{ .{ .text = "[" }, TypeDoc.nested(1, TypeDoc.seq(items.items)), .{ .text = "]" } }));
}

const DocFrame = struct { doc: TypeDoc, indent: usize = 0, flat: bool = false };

fn docFits(remaining: usize, stack: []const DocFrame, candidate: DocFrame) bool {
    var pending_docs: List(DocFrame) = .empty;
    pending_docs.appendSlice(a, stack) catch oom();
    push(DocFrame, &pending_docs, candidate);
    var left = remaining;
    while (pending_docs.pop()) |frame| switch (frame.doc) {
        .text => |s| {
            const length = (std.unicode.utf8ToUtf16LeAlloc(a, s) catch oom()).len;
            if (length > left) return false;
            left -= length;
        },
        .line => if (!frame.flat) return true else if (left == 0) return false else {
            left -= 1;
        },
        .sequence => |docs| {
            var i = docs.len;
            while (i > 0) {
                i -= 1;
                push(DocFrame, &pending_docs, .{ .doc = docs[i], .flat = frame.flat });
            }
        },
        .nest => |nested| push(DocFrame, &pending_docs, .{ .doc = nested.child.*, .flat = frame.flat }),
        .group, .aligned => |child| push(DocFrame, &pending_docs, .{ .doc = child.*, .flat = frame.flat }),
    };
    return true;
}

fn typeDisplay(t: *const Type) []const u8 {
    var out: List(u8) = .empty;
    var stack: List(DocFrame) = .empty;
    push(DocFrame, &stack, .{ .doc = typeDoc(t) });
    var column: usize = 0;
    while (stack.pop()) |frame| switch (frame.doc) {
        .text => |s| {
            out.appendSlice(a, s) catch oom();
            column += (std.unicode.utf8ToUtf16LeAlloc(a, s) catch oom()).len;
        },
        .line => {
            if (frame.flat) {
                push(u8, &out, ' ');
                column += 1;
            } else {
                push(u8, &out, '\n');
                out.appendNTimes(a, ' ', frame.indent) catch oom();
                column = frame.indent;
            }
        },
        .sequence => |docs| {
            var i = docs.len;
            while (i > 0) {
                i -= 1;
                push(DocFrame, &stack, .{ .doc = docs[i], .indent = frame.indent, .flat = frame.flat });
            }
        },
        .nest => |nested| push(DocFrame, &stack, .{ .doc = nested.child.*, .indent = frame.indent + nested.by, .flat = frame.flat }),
        .aligned => |child| push(DocFrame, &stack, .{ .doc = child.*, .indent = column, .flat = frame.flat }),
        .group => |child| push(DocFrame, &stack, .{ .doc = child.*, .indent = frame.indent, .flat = frame.flat or docFits(80 -| column, stack.items, .{ .doc = child.*, .flat = true }) }),
    };
    return out.items;
}

test "unsupported type diagnostics preserve the F sharp nested layout" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    a = arena.allocator();
    var t = newType(.{ .primitive = "primitiveBits32" });
    for (0..4) |_| {
        const argument = try a.dupe(*const Type, &.{t});
        t = newType(.{ .builtin = .{ .kind = "list", .args = argument } });
    }
    try std.testing.expectEqualStrings("BuiltinGeneric\n  (List,\n   [BuiltinGeneric\n      (List,\n       [BuiltinGeneric (List, [BuiltinGeneric (List, [Primitive Bits32])])])])", typeDisplay(t));
    try std.testing.expectEqualStrings("Capability (StableId \"日本😀a\"b\\c\tb\")", typeDisplay(newType(.{ .capability = "日本😀a\"b\\c\tb" })));
    const framed = try std.json.Stringify.valueAlloc(a, typeDisplay(t), .{});
    const recovered = try std.json.parseFromSliceLeaky([]const u8, a, framed, .{});
    try std.testing.expectEqualStrings(typeDisplay(t), recovered);
}

fn typeListJson(items: []const *const Type) []const u8 {
    var out: List(u8) = .empty;
    push(u8, &out, '[');
    for (items, 0..) |item, at| {
        if (at > 0) push(u8, &out, ',');
        writeType(&out, item);
    }
    push(u8, &out, ']');
    return out.items;
}

fn sameType(left: *const Type, right: *const Type) bool {
    return eql(typeJson(left), typeJson(right));
}

const Bindings = std.StringHashMap(*const Type);

fn substitute(map: *const Bindings, t: *const Type) *const Type {
    return switch (t.*) {
        .type_parameter => |id| map.get(id) orelse t,
        .named => |named| newType(.{ .named = .{ .id = named.id, .args = substituteAll(map, named.args) } }),
        .builtin => |builtin| newType(.{ .builtin = .{ .kind = builtin.kind, .args = substituteAll(map, builtin.args) } }),
        .function => |function| newType(.{ .function = .{ .params = substituteAll(map, function.params), .result = substitute(map, function.result) } }),
        else => t,
    };
}

fn substituteAll(map: *const Bindings, items: []const *const Type) []const *const Type {
    const out = a.alloc(*const Type, items.len) catch oom();
    for (items, 0..) |item, at| out[at] = substitute(map, item);
    return out;
}

/// Binds the type parameters in `pattern` so that it matches `actual`.
fn unify(pattern: *const Type, actual: *const Type, bound: *Bindings) void {
    switch (pattern.*) {
        .type_parameter => |id| if (!bound.contains(id)) bound.put(id, actual) catch oom(),
        .named => |named| if (actual.* == .named and actual.named.args.len == named.args.len) {
            for (named.args, actual.named.args) |p, x| unify(p, x, bound);
        },
        .builtin => |builtin| if (actual.* == .builtin and actual.builtin.args.len == builtin.args.len) {
            for (builtin.args, actual.builtin.args) |p, x| unify(p, x, bound);
        },
        else => {},
    }
}

fn concrete(t: *const Type) bool {
    return switch (t.*) {
        .type_parameter => false,
        .named => |named| allConcrete(named.args),
        .builtin => |builtin| allConcrete(builtin.args),
        .function => |function| concrete(function.result) and allConcrete(function.params),
        else => true,
    };
}

fn allConcrete(items: []const *const Type) bool {
    for (items) |item| if (!concrete(item)) return false;
    return true;
}

// ---------------------------------------------------------------- names

/// `prefix` and then `raw` with every code point that is not an ASCII letter or digit as `_<hex>_`,
/// so distinct IDs never share a name.
fn identifier(prefix: []const u8, raw: []const u8) []const u8 {
    var out: List(u8) = .empty;
    out.appendSlice(a, prefix) catch oom();
    var view = std.unicode.Utf8View.initUnchecked(raw);
    var points = view.iterator();
    while (points.nextCodepoint()) |point| {
        if (point < 0x80 and std.ascii.isAlphanumeric(@intCast(point))) push(u8, &out, @intCast(point)) else out.print(a, "_{x}_", .{point}) catch oom();
    }
    return out.toOwnedSlice(a) catch oom();
}

test "identifiers keep distinct IDs distinct and literals escape IDs" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    a = arena.allocator();
    try std.testing.expectEqualStrings("v_x_3a_a_2d_b", identifier("v_", "x:a-b"));
    try std.testing.expectEqualStrings("v_x_3a_a_2e_b", identifier("v_", "x:a.b"));
    try std.testing.expectEqualStrings("v_a_5f_b", identifier("v_", "a_b"));
    try std.testing.expectEqualStrings("\"a\\x22\\x5c\\x09b\"", stringLiteral("a\"\\\tb"));
}

// Value and block identifiers by ID: each is written many times.
var value_names: std.StringHashMapUnmanaged([]const u8) = .empty;
var block_names: std.StringHashMapUnmanaged([]const u8) = .empty;

fn memoName(memo: *std.StringHashMapUnmanaged([]const u8), prefix: []const u8, id: []const u8) []const u8 {
    const entry = memo.getOrPut(a, id) catch oom();
    if (!entry.found_existing) entry.value_ptr.* = identifier(prefix, id);
    return entry.value_ptr.*;
}

fn v(id: []const u8) []const u8 {
    return memoName(&value_names, "v_", id);
}

fn blockName(id: []const u8) []const u8 {
    return memoName(&block_names, "b_", id);
}

fn fieldName(id: []const u8) []const u8 {
    return identifier("f_", id);
}

fn variantName(id: []const u8) []const u8 {
    return identifier("c_", id);
}

fn operationName(id: []const u8) []const u8 {
    return identifier("op_", id);
}

/// A short stable suffix for one list of type arguments: a hash of their JSON.
fn instanceSuffix(args: []const *const Type) []const u8 {
    if (args.len == 0) return "";
    var digest: [32]u8 = undefined;
    std.crypto.hash.sha2.Sha256.hash(typeListJson(args), &digest, .{});
    const hex = std.fmt.bytesToHex(digest, .upper);
    return cat(&.{ "__", hex[0..12] });
}

fn instanceName(function_id: []const u8, args: []const *const Type) []const u8 {
    return cat(&.{ identifier("f_", function_id), instanceSuffix(args) });
}

fn typeName(type_id: []const u8, args: []const *const Type) []const u8 {
    return cat(&.{ identifier("T_", type_id), instanceSuffix(args) });
}

fn stringLiteral(text: []const u8) []const u8 {
    var out: List(u8) = .empty;
    push(u8, &out, '"');
    for (text) |b| {
        if (b >= 0x20 and b < 0x7F and b != '"' and b != '\\') push(u8, &out, b) else out.print(a, "\\x{x:0>2}", .{b}) catch oom();
    }
    push(u8, &out, '"');
    return out.items;
}

// ---------------------------------------------------------------- the IR

fn str(o: json.ObjectMap, key: []const u8) []const u8 {
    return (o.get(key) orelse std.debug.panic("missing {s}", .{key})).string;
}

fn arr(o: json.ObjectMap, key: []const u8) []const json.Value {
    return if (o.get(key)) |item| item.array.items else &.{};
}

fn byId(comptime T: type, items: []T, comptime key: fn (T) []const u8) void {
    const S = struct {
        fn less(_: void, left: T, right: T) bool {
            // Stable IDs use String.CompareOrdinal, including supplementary UTF-16 pairs.
            const x = std.unicode.utf8ToUtf16LeAlloc(a, key(left)) catch oom();
            const y = std.unicode.utf8ToUtf16LeAlloc(a, key(right)) catch oom();
            return std.mem.lessThan(u16, x, y);
        }
    };
    std.mem.sort(T, items, {}, S.less);
}

const Value = struct { id: []const u8, type: *const Type };
const Parameter = struct { id: []const u8, value: Value };
const Instruction = struct { result: Value, o: json.ObjectMap };
const Block = struct { id: []const u8, parameters: []Value, instructions: []Instruction, terminator: json.ObjectMap };
const Function = struct {
    id: []const u8,
    type_parameters: []const []const u8,
    parameters: []Parameter,
    result: *const Type,
    entry: []const u8,
    blocks: []Block,
};
const Field = struct { id: []const u8, type: *const Type };
const Variant = struct { id: []const u8, fields: []Field };
const Declaration = struct { id: []const u8, type_parameters: []const []const u8, product: ?[]Field, variants: []Variant };
const Operation = struct { id: []const u8, parameters: []Field, result: *const Type };
const Capability = struct { id: []const u8, operations: []Operation };

fn parseValue(value: json.Value) Value {
    return .{ .id = str(value.object, "id"), .type = parseType(value.object.get("type").?) };
}

fn parseTypeParameters(o: json.ObjectMap) []const []const u8 {
    const items = arr(o, "typeParameters");
    const out = a.alloc([]const u8, items.len) catch oom();
    for (items, 0..) |item, at| out[at] = str(item.object, "typeParameterId");
    return out;
}

fn parseFields(items: []const json.Value) []Field {
    const out = a.alloc(Field, items.len) catch oom();
    for (items, 0..) |item, at| out[at] = .{ .id = str(item.object, "id"), .type = parseType(item.object.get("type").?) };
    byId(Field, out, fieldKey);
    return out;
}

fn fieldKey(item: Field) []const u8 {
    return item.id;
}

fn parseFunction(value: json.Value) Function {
    const o = value.object;
    const parameters = arr(o, "parameters");
    const blocks = arr(o, "blocks");
    const fn_ = Function{
        .id = str(o, "semanticFunctionId"),
        .type_parameters = parseTypeParameters(o),
        .parameters = a.alloc(Parameter, parameters.len) catch oom(),
        .result = parseType(o.get("resultType").?),
        .entry = str(o, "entryBlockId"),
        .blocks = a.alloc(Block, blocks.len) catch oom(),
    };
    for (parameters, 0..) |item, at| fn_.parameters[at] = .{ .id = str(item.object, "semanticParameterId"), .value = parseValue(item.object.get("value").?) };
    for (blocks, 0..) |item, at| {
        const b = item.object;
        const block_parameters = arr(b, "parameters");
        const instructions = arr(b, "instructions");
        fn_.blocks[at] = .{
            .id = str(b, "id"),
            .parameters = a.alloc(Value, block_parameters.len) catch oom(),
            .instructions = a.alloc(Instruction, instructions.len) catch oom(),
            .terminator = b.get("terminator").?.object,
        };
        for (block_parameters, 0..) |parameter, index| fn_.blocks[at].parameters[index] = parseValue(parameter);
        for (instructions, 0..) |instruction, index|
            fn_.blocks[at].instructions[index] = .{ .result = parseValue(instruction.object.get("result").?), .o = instruction.object };
    }
    byId(Block, fn_.blocks, blockKey);
    return fn_;
}

fn blockKey(item: Block) []const u8 {
    return item.id;
}

fn substituteFunction(map: *const Bindings, generic: Function) Function {
    if (map.count() == 0) return generic;
    var fn_ = generic;
    fn_.type_parameters = &.{};
    fn_.result = substitute(map, generic.result);
    fn_.parameters = a.dupe(Parameter, generic.parameters) catch oom();
    for (fn_.parameters) |*parameter| parameter.value.type = substitute(map, parameter.value.type);
    fn_.blocks = a.dupe(Block, generic.blocks) catch oom();
    for (fn_.blocks) |*block| {
        block.parameters = a.dupe(Value, block.parameters) catch oom();
        for (block.parameters) |*value| value.type = substitute(map, value.type);
        block.instructions = a.dupe(Instruction, block.instructions) catch oom();
        for (block.instructions) |*instruction| instruction.result.type = substitute(map, instruction.result.type);
    }
    return fn_;
}

/// Parameter values, then each block's parameters and instruction results.
fn valuesOf(fn_: Function) []Value {
    var out: List(Value) = .empty;
    for (fn_.parameters) |parameter| push(Value, &out, parameter.value);
    for (fn_.blocks) |block| {
        out.appendSlice(a, block.parameters) catch oom();
        for (block.instructions) |instruction| push(Value, &out, instruction.result);
    }
    return out.items;
}

// ---------------------------------------------------------------- the emitter

const Instance = struct { id: []const u8, args: []const *const Type };

var declarations: std.StringHashMap(Declaration) = undefined;
var functions: std.StringHashMap(Function) = undefined;
var capabilities: []Capability = undefined;
var may_fail: std.StringHashMap(void) = undefined;
var problems: List([]const u8) = .empty;
var type_instances: std.StringHashMap(void) = undefined;
var type_order: List(Instance) = .empty;
var function_instances: std.StringHashMap(void) = undefined;
var pending: List(Instance) = .empty;

fn instanceKey(id: []const u8, args: []const *const Type) []const u8 {
    return cat(&.{ id, "\x00", typeListJson(args) });
}

fn problem(text: []const u8) void {
    push([]const u8, &problems, text);
}

fn findCapability(id: []const u8) ?Capability {
    for (capabilities) |capability| if (eql(capability.id, id)) return capability;
    return null;
}

fn findOperation(id: []const u8) Operation {
    for (capabilities) |capability| for (capability.operations) |operation| if (eql(operation.id, id)) return operation;
    std.debug.panic("unknown operation {s}", .{id});
}

/// The Zig type of a supported value type, recording each declared type instance it names when
/// `record` is set. Products and Unions are boxed (`*const`), which keeps recursive types finite.
fn zigType(t: *const Type, record: bool) ?[]const u8 {
    switch (t.*) {
        .primitive => |kind| {
            const table = .{
                .{ "primitiveInt64", "i64" },         .{ "primitiveBool", "bool" },   .{ "primitiveUnit", "NyUnit" },
                .{ "primitiveString", "[]const u8" }, .{ "primitiveFloat64", "f64" }, .{ "primitiveBytes", "NyBytes" },
                .{ "primitiveBits32", "u32" },
            };
            inline for (table) |row| if (eql(kind, row[0])) return row[1];
            return null;
        },
        .builtin => |builtin| {
            const k = builtin.kind;
            if (eql(k, "exception")) return "NyFailure";
            if (eql(k, "taskScope")) return "*NyScope";
            if (eql(k, "map") or eql(k, "result")) {
                const first = zigType(builtin.args[0], record);
                const second = zigType(builtin.args[1], record);
                if (first == null or second == null) return null;
                return cat(&.{ if (eql(k, "map")) "NyMap(" else "NyResult(", first.?, ", ", second.?, ")" });
            }
            const item = zigType(builtin.args[0], record) orelse return null;
            if (eql(k, "option")) return cat(&.{ "?", item });
            if (eql(k, "task")) return cat(&.{ "*NyTask(", item, ")" });
            if (eql(k, "oneShot")) return cat(&.{ "*NyOneShot(", item, ")" });
            if (eql(k, "set")) return cat(&.{ "NySet(", item, ")" });
            return cat(&.{ "[]const ", item });
        },
        .named => |named| {
            const declaration = declarations.get(named.id) orelse return null;
            if (!allConcrete(named.args) or declaration.type_parameters.len != named.args.len) return null;
            if (record) {
                const key = instanceKey(named.id, named.args);
                if (!type_instances.contains(key)) {
                    type_instances.put(key, {}) catch oom();
                    push(Instance, &type_order, .{ .id = named.id, .args = named.args });
                }
            }
            return cat(&.{ "*const ", typeName(named.id, named.args) });
        },
        // A capability value's type is declared by whoever binds the capability (a runPure runner's
        // fakes for now), with one method per operation.
        .capability => |id| return if (findCapability(id) != null) identifier("C_", id) else null,
        .function => |function| {
            const result = zigType(function.result, record);
            var parts: List([]const u8) = .empty;
            var all = result != null;
            for (function.params) |parameter| {
                if (zigType(parameter, record)) |text| push([]const u8, &parts, text) else all = false;
            }
            if (!all) return null;
            return cat(&.{ "*const fn (", join(", ", parts.items), ") ", result.? });
        },
        .type_parameter => return null,
    }
}

fn zigOf(t: *const Type) ?[]const u8 {
    return zigType(t, true) orelse {
        problem(cat(&.{ "type ", typeDisplay(t) }));
        return null;
    };
}

fn zig(t: *const Type) []const u8 {
    return zigOf(t) orelse "void";
}

/// The instance name for a call of the function with the given argument type per parameter and the
/// given result type; a new instance is queued for emission.
fn call(function_id: []const u8, args: []const Field, result: *const Type) []const u8 {
    const fn_ = functions.get(function_id).?;
    var bound = Bindings.init(a);
    for (args) |arg| {
        for (fn_.parameters) |parameter| if (eql(parameter.id, arg.id)) unify(parameter.value.type, arg.type, &bound);
    }
    unify(fn_.result, result, &bound);
    const type_args = a.alloc(*const Type, fn_.type_parameters.len) catch oom();
    for (fn_.type_parameters, 0..) |id, at| type_args[at] = bound.get(id) orelse newType(.{ .type_parameter = id });
    if (!allConcrete(type_args)) problem("a generic call whose type arguments cannot be inferred");
    const key = instanceKey(function_id, type_args);
    if (!function_instances.contains(key)) {
        function_instances.put(key, {}) catch oom();
        push(Instance, &pending, .{ .id = function_id, .args = type_args });
    }
    return instanceName(function_id, type_args);
}

fn parametersOf(function_id: []const u8) []Parameter {
    return functions.get(function_id).?.parameters;
}

fn failing(function_id: []const u8) bool {
    return may_fail.contains(function_id);
}

fn attempt(function_id: []const u8, text: []const u8) []const u8 {
    return if (failing(function_id)) cat(&.{ "try ", text }) else text;
}

fn itemType(t: *const Type) *const Type {
    if (t.* == .builtin and (eql(t.builtin.kind, "list") or eql(t.builtin.kind, "set"))) return t.builtin.args[0];
    std.debug.panic("Expected a List type, got {s}.", .{typeJson(t)});
}

fn argumentOf(t: *const Type, index: usize) *const Type {
    if (t.* == .builtin) return t.builtin.args[index];
    std.debug.panic("Expected a generic type, got {s}.", .{typeJson(t)});
}

const unit_type = Type{ .primitive = "primitiveUnit" };
const bool_type = Type{ .primitive = "primitiveBool" };
const exception_type = Type{ .builtin = .{ .kind = "exception", .args = &.{} } };
const scope_type = Type{ .builtin = .{ .kind = "taskScope", .args = &.{} } };

/// Every instruction kind the backend emits, named as in the IR.
const Kind = enum {
    readParameter,
    readPatternBinding,
    boolLiteral,
    unitLiteral,
    int64Literal,
    stringLiteral,
    boolNot,
    int64WrappingAdd,
    int64WrappingSubtract,
    int64WrappingMultiply,
    int64FloorDivide,
    int64FloorModulo,
    int64Min,
    int64Max,
    int64BitAnd,
    int64BitOr,
    int64BitXor,
    int64BitNot,
    int64ShiftLeft,
    int64ShiftRight,
    int64WrappingAbs,
    int64Equal,
    int64LessThan,
    int64LessThanOrEqual,
    int64GreaterThan,
    int64GreaterThanOrEqual,
    int64ToString,
    stringIsEmpty,
    stringLength,
    stringEqual,
    stringConcat,
    valueEqual,
    productConstruct,
    fieldAccess,
    productUpdate,
    unionConstruct,
    optionNone,
    optionSome,
    resultOk,
    resultErr,
    extractUnionField,
    extractOptionSome,
    extractResultOk,
    extractResultErr,
    optionIsSome,
    resultIsOk,
    optionGetOrDefault,
    resultGetOrDefault,
    listEmpty,
    listConstruct,
    listRange,
    listLength,
    listAppendItem,
    listConcat,
    listReverse,
    listItemAt,
    listLast,
    listDropLast,
    listSlice,
    listSetAt,
    listInsertAt,
    listRemoveAt,
    listIndexOf,
    listLastIndexOf,
    listContains,
    listFold,
    listScan,
    listMap,
    listFilter,
    listAny,
    listAll,
    listFindIndex,
    listZipWith,
    listFlatMap,
    float64Literal,
    float64Add,
    float64Subtract,
    float64Multiply,
    float64Divide,
    float64Min,
    float64Max,
    float64FloorModulo,
    float64Equal,
    float64LessThan,
    float64IsNaN,
    float64IsFinite,
    float64IsInfinite,
    float64Sqrt,
    float64Abs,
    float64Negate,
    float64Floor,
    float64Ceiling,
    float64Truncate,
    float64RoundToInt64,
    float64FloorToInt64,
    int64ToFloat64,
    bits32FromInt64,
    bits32ToInt64,
    bits32Xor,
    bits32And,
    bits32Or,
    bits32Not,
    bits32Add,
    bits32RotateRight,
    bits32ShiftRight,
    bits32ShiftLeft,
    setEmpty,
    mapEmpty,
    setFromList,
    setToList,
    setSize,
    setInsert,
    setRemove,
    setContains,
    setUnion,
    setIntersection,
    setDifference,
    setIsSubset,
    setFold,
    setFilter,
    setMap,
    mapSize,
    mapKeys,
    mapValues,
    mapInsert,
    mapRemove,
    mapGet,
    mapGetOrDefault,
    mapContainsKey,
    mapUnion,
    mapFold,
    mapFilter,
    mapMapValues,
    mapFromList,
    bytesEmpty,
    bytesFill,
    bytesLength,
    bytesGet,
    bytesSet,
    bytesAppend,
    bytesConcat,
    bytesSlice,
    bytesFromLatin1,
    bytesToLatin1,
    capabilityInvoke,
    exceptionCatch,
    exceptionRethrow,
    exceptionMessage,
    exceptionIsCancellation,
    useResource,
    listSortBy,
    listMinBy,
    listMaxBy,
    functionReference,
    functionInvoke,
    cancellationRequested,
    cancellationShield,
    taskScope,
    taskStart,
    taskWait,
    taskCancel,
    oneShotCreate,
    oneShotComplete,
    oneShotWait,
    parallelAll,
    listEffectFold,
    int64CheckedAdd,
    int64CheckedSubtract,
    int64CheckedMultiply,
    int64ToHexString,
    stringParseInt64,
    stringParseInt64Hex,
    float64ToString,
    stringParseFloat64,
    float64Round,
    stringSlice,
    stringContains,
    stringIndexOf,
    stringLastIndexOf,
    stringLastIndexOfBefore,
    stringSpan,
    stringSpanBack,
    stringStartsWith,
    stringEndsWith,
    stringSplit,
    stringSplitLines,
    stringReplace,
    stringTrim,
    stringTrimStart,
    stringTrimEnd,
    stringJoin,
    stringLessThan,
    stringCodePoints,
    stringFromCodePoint,
    stringToCodePoint,
    stringUtf8ByteCount,
    stringEncodeUtf8,
    stringDecodeUtf8Lossy,
    stringRepeat,
    stringToLower,
    stringToUpper,
    directCall,
};

/// One function body being emitted: the type of each of its values.
const Body = struct {
    values: std.StringHashMap(*const Type),
    o: json.ObjectMap = undefined,
    result: *const Type = undefined,

    fn valueType(self: *const Body, id: []const u8) *const Type {
        return self.values.get(id) orelse std.debug.panic("unknown value {s}", .{id});
    }

    /// The value the instruction names under `key`.
    fn f(self: *const Body, key: []const u8) []const u8 {
        return v(str(self.o, key));
    }

    fn typeAt(self: *const Body, key: []const u8) *const Type {
        return self.valueType(str(self.o, key));
    }

    fn elementOf(self: *const Body, key: []const u8) []const u8 {
        return zig(itemType(self.typeAt(key)));
    }

    fn resultElement(self: *const Body) []const u8 {
        return zig(itemType(self.result));
    }

    fn resultTypeName(self: *const Body) []const u8 {
        if (self.result.* != .named) std.debug.panic("Expected a declared type, got {s}.", .{typeJson(self.result)});
        return zig(self.result)["*const ".len..];
    }

    fn listCall(self: *const Body, name: []const u8, list: []const u8, rest: []const []const u8) []const u8 {
        const head = [_][]const u8{ self.elementOf(list), self.f(list) };
        return cat(&.{ name, "(", join(", ", std.mem.concat(a, []const u8, &.{ &head, rest }) catch oom()), ")" });
    }

    fn mapTypes(self: *const Body, key: []const u8) [2]*const Type {
        const t = self.typeAt(key);
        if (t.* == .builtin and eql(t.builtin.kind, "map")) return .{ t.builtin.args[0], t.builtin.args[1] };
        std.debug.panic("Expected a Map type, got {s}.", .{typeJson(t)});
    }

    fn mapCall(self: *const Body, name: []const u8, map: []const u8, rest: []const []const u8) []const u8 {
        const types = self.mapTypes(map);
        const head = [_][]const u8{ zig(types[0]), zig(types[1]), self.f(map) };
        return cat(&.{ name, "(", join(", ", std.mem.concat(a, []const u8, &.{ &head, rest }) catch oom()), ")" });
    }

    fn bindings(self: *const Body, key: []const u8) []const json.Value {
        return arr(self.o, key);
    }

    /// A callback call: context-bound parameters take their values, the others take `roles` in
    /// declaration order. The callee owns its arguments, so a borrowed value (a context value, an
    /// item read from the source) is passed shared; a fold's state and a started task's captures move.
    fn callback(self: *const Body, function_key: []const u8, context_key: []const u8, roles: []const Role, result: *const Type) []const u8 {
        const function_id = str(self.o, function_key);
        const kind = str(self.o, "kind");
        const moves = eql(kind, "taskStart");
        const context = self.bindings(context_key);
        var remaining = roles;
        var expressions: List([]const u8) = .empty;
        var args: List(Field) = .empty;
        for (parametersOf(function_id)) |parameter| {
            var bound: ?[]const u8 = null;
            for (context) |binding| {
                if (eql(str(binding.object, "targetParameterId"), parameter.id)) bound = str(binding.object, "valueId");
            }
            if (bound) |value_id| {
                push([]const u8, &expressions, if (moves) v(value_id) else cat(&.{ "nyShare(", v(value_id), ")" }));
                push(Field, &args, .{ .id = parameter.id, .type = self.valueType(value_id) });
            } else {
                const role = remaining[0].expression;
                const borrowed = for ([_][]const u8{ "item", "l", "r", "key", "value", "resource" }) |name| {
                    if (eql(role, name)) break true;
                } else eql(kind, "listScan") and eql(role, "state");
                push([]const u8, &expressions, if (borrowed) cat(&.{ "nyShare(", role, ")" }) else role);
                push(Field, &args, .{ .id = parameter.id, .type = remaining[0].type });
                remaining = remaining[1..];
            }
        }
        const name = call(function_id, args.items, result);
        return cat(&.{ name, "(", join(", ", expressions.items), ")" });
    }

    /// The bound values of a callback's context, in IR order.
    fn captured(self: *const Body, context_key: []const u8) List([]const u8) {
        var out: List([]const u8) = .empty;
        for (self.bindings(context_key)) |binding| push([]const u8, &out, v(str(binding.object, "valueId")));
        return out;
    }

    /// A comparison callback as a Zig function value: bound values travel in `c`, the two compared
    /// items are `l` and `r`.
    fn comparator(self: *const Body, first: []const u8, second: []const u8) [2][]const u8 {
        const item = itemType(self.typeAt("sourceListValueId"));
        const text = self.callback("lessThanFunctionId", "context", &.{ .{ .expression = first, .type = item }, .{ .expression = second, .type = item } }, &bool_type);
        const names = self.captured("context");
        const travelled = deferred(text, names.items);
        return .{ travelled[0], cat(&.{ "struct { fn less(c: anytype, l: ", zig(item), ", r: ", zig(item), ") bool { _ = &c; return ", travelled[1], "; } }.less" }) };
    }
};

const Role = struct { expression: []const u8, type: *const Type };

/// A callback run later or elsewhere: the values it reads travel in `c`. Zig forbids shadowing the
/// outer locals, so the call reads them from `c` (value names have one fixed length, so no name is a
/// prefix of another).
fn deferred(text: []const u8, names: []const []const u8) [2][]const u8 {
    var distinct: List([]const u8) = .empty;
    outer: for (names) |name| {
        for (distinct.items) |seen| if (eql(seen, name)) continue :outer;
        push([]const u8, &distinct, name);
    }
    var inner = text;
    var fields: List([]const u8) = .empty;
    for (distinct.items) |name| {
        inner = std.mem.replaceOwned(u8, a, inner, name, cat(&.{ "c.", name })) catch oom();
        push([]const u8, &fields, cat(&.{ ".", name, " = ", name }));
    }
    return .{ cat(&.{ ".{ ", join(", ", fields.items), " }" }), inner };
}

fn fieldList(items: []const json.Value) []const u8 {
    const sorted = a.dupe(json.Value, items) catch oom();
    byId(json.Value, sorted, targetFieldKey);
    var parts: List([]const u8) = .empty;
    for (sorted) |item| push([]const u8, &parts, cat(&.{ ".", fieldName(str(item.object, "targetFieldId")), " = ", v(str(item.object, "valueId")) }));
    return cat(&.{ ".{ ", join(", ", parts.items), " }" });
}

fn targetFieldKey(item: json.Value) []const u8 {
    return str(item.object, "targetFieldId");
}

/// One Zig statement per instruction, or null when the instruction is outside the supported slice.
fn instructionLine(body: *Body, instruction: Instruction) ?[]const u8 {
    body.o = instruction.o;
    body.result = instruction.result.type;
    const kind = std.meta.stringToEnum(Kind, str(instruction.o, "kind")) orelse return null;
    const expression = instructionExpression(body, kind);
    return cat(&.{ v(instruction.result.id), " = ", expression, ";" });
}

/// Reports malformed IR as a problem; the placeholder is never emitted because problems stop emission.
fn malformed(text: []const u8) []const u8 {
    problem(text);
    return "undefined";
}

fn labeled(text: []const u8) []const u8 {
    return cat(&.{ "blk: { ", text, " }" });
}

fn binary(b: *const Body, operator: []const u8) []const u8 {
    return cat(&.{ b.f("leftValueId"), " ", operator, " ", b.f("rightValueId") });
}

fn wrap(b: *const Body, name: []const u8, keys: []const []const u8) []const u8 {
    var parts: List([]const u8) = .empty;
    for (keys) |key| push([]const u8, &parts, b.f(key));
    return cat(&.{ name, "(", join(", ", parts.items), ")" });
}

fn unary(b: *const Body, name: []const u8) []const u8 {
    return wrap(b, name, &.{"valueId"});
}

fn pair(b: *const Body, name: []const u8) []const u8 {
    return wrap(b, name, &.{ "leftValueId", "rightValueId" });
}

fn instructionExpression(b: *Body, kind: Kind) []const u8 {
    const role = struct {
        fn of(expression: []const u8, t: *const Type) Role {
            return .{ .expression = expression, .type = t };
        }
    }.of;
    return switch (kind) {
        .readParameter, .readPatternBinding => b.f("sourceValueId"),
        .boolLiteral => switch (b.o.get("value") orelse .null) {
            .bool => |value| if (value) "true" else "false",
            else => malformed("boolLiteral value"),
        },
        .unitLiteral => "NyUnit{}",
        .int64Literal => switch (b.o.get("value") orelse .null) {
            .integer => |value| std.fmt.allocPrint(a, "{d}", .{value}) catch oom(),
            else => malformed("int64Literal value"),
        },
        .stringLiteral => cat(&.{ "nyStatic(", stringLiteral(str(b.o, "value")), ")" }),
        .boolNot => cat(&.{ "!", b.f("valueId") }),
        .int64WrappingAdd => binary(b, "+%"),
        .int64WrappingSubtract => binary(b, "-%"),
        .int64WrappingMultiply => binary(b, "*%"),
        // Floor division and modulo never trap: divisor 0 gives 0 (the dividend for modulo), -1 wraps.
        .int64FloorDivide => pair(b, "nyFloorDivide"),
        .int64FloorModulo => pair(b, "nyFloorModulo"),
        .int64Min => pair(b, "@min"),
        .int64Max => pair(b, "@max"),
        .int64BitAnd => binary(b, "&"),
        .int64BitOr => binary(b, "|"),
        .int64BitXor => binary(b, "^"),
        .int64BitNot => cat(&.{ "~", b.f("valueId") }),
        .int64ShiftLeft => wrap(b, "nyShiftLeft", &.{ "valueId", "countValueId" }),
        .int64ShiftRight => wrap(b, "nyShiftRight", &.{ "valueId", "countValueId" }),
        .int64WrappingAbs => cat(&.{ "if (", b.f("valueId"), " < 0) 0 -% ", b.f("valueId"), " else ", b.f("valueId") }),
        .int64Equal => binary(b, "=="),
        .int64LessThan => binary(b, "<"),
        .int64LessThanOrEqual => binary(b, "<="),
        .int64GreaterThan => binary(b, ">"),
        .int64GreaterThanOrEqual => binary(b, ">="),
        .int64ToString => unary(b, "nyInt64ToString"),
        .stringIsEmpty => cat(&.{ b.f("valueId"), ".len == 0" }),
        .stringLength => unary(b, "nyStringLength"),
        .stringEqual => cat(&.{ "std.mem.eql(u8, ", b.f("leftValueId"), ", ", b.f("rightValueId"), ")" }),
        .stringConcat => pair(b, "nyConcatOwned"),
        .valueEqual => pair(b, "nyEqual"),
        .productConstruct => cat(&.{ "nyBox(", b.resultTypeName(), ", ", fieldList(arr(b.o, "fields")), ")" }),
        .fieldAccess => cat(&.{ "nyShare(", b.f("sourceValueId"), ".", fieldName(str(b.o, "fieldId")), ")" }),
        .productUpdate => blk: {
            const fields = a.dupe(json.Value, arr(b.o, "fields")) catch oom();
            byId(json.Value, fields, targetFieldKey);
            var updates: List([]const u8) = .empty;
            // The update consumes the source: a unique box is written in place; otherwise the copy
            // shares the source's fields and the source reference is dropped. An updated field drops
            // the old value and takes the new one.
            for (fields) |field| {
                const name = fieldName(str(field.object, "targetFieldId"));
                push([]const u8, &updates, cat(&.{ " nyRelease(copy.", name, "); copy.", name, " = ", v(str(field.object, "valueId")), ";" }));
            }
            const source = b.f("sourceValueId");
            break :blk labeled(cat(&.{ "if (nyUnique(", source, ")) { const copy = @constCast(", source, ");", cat(updates.items), " break :blk ", source, "; } var copy = ", source, ".*; nyRetainFields(copy); nyRelease(", source, ");", cat(updates.items), " break :blk nyBox(", b.resultTypeName(), ", copy);" }));
        },
        .unionConstruct => cat(&.{ "nyBox(", b.resultTypeName(), ", .{ .", variantName(str(b.o, "variantId")), " = ", fieldList(arr(b.o, "fields")), " })" }),
        .optionNone => "null",
        .optionSome => b.f("valueId"),
        .resultOk => cat(&.{ ".{ .ok = ", b.f("valueId"), " }" }),
        .resultErr => cat(&.{ ".{ .err = ", b.f("errorValueId"), " }" }),
        .extractUnionField => cat(&.{ "nyShare(", b.f("inputValueId"), ".", variantName(str(b.o, "variantId")), ".", fieldName(str(b.o, "fieldId")), ")" }),
        .extractOptionSome => cat(&.{ "nyShare(", b.f("inputValueId"), ".?)" }),
        .extractResultOk => cat(&.{ "nyShare(", b.f("inputValueId"), ".ok)" }),
        .extractResultErr => cat(&.{ "nyShare(", b.f("inputValueId"), ".err)" }),
        .optionIsSome => cat(&.{ b.f("valueId"), " != null" }),
        .resultIsOk => cat(&.{ b.f("valueId"), " == .ok" }),
        .optionGetOrDefault => cat(&.{ "nyShare(", b.f("optionValueId"), " orelse ", b.f("defaultValueId"), ")" }),
        .resultGetOrDefault => cat(&.{ "nyShare(switch (", b.f("resultValueId"), ") { .ok => |item| item, .err => ", b.f("defaultValueId"), " })" }),
        .listEmpty => "&.{}",
        .listConstruct => blk: {
            var items: List([]const u8) = .empty;
            for (arr(b.o, "itemValueIds")) |item| push([]const u8, &items, v(item.string));
            const element = b.resultElement();
            break :blk cat(&.{ "nyListMove(", element, ", &[_]", b.resultElement(), "{ ", join(", ", items.items), " })" });
        },
        .listRange => wrap(b, "nyListRange", &.{ "startValueId", "endValueId" }),
        .listLength => cat(&.{ "@as(i64, @intCast(", b.f("listValueId"), ".len))" }),
        .listAppendItem => b.listCall("nyListAppendOwned", "listValueId", &.{b.f("itemValueId")}),
        .listConcat => b.listCall("nyListConcatOwned", "leftListValueId", &.{b.f("rightListValueId")}),
        .listReverse => b.listCall("nyListReverse", "listValueId", &.{}),
        .listItemAt => b.listCall("nyListItemAt", "listValueId", &.{b.f("indexValueId")}),
        .listLast => b.listCall("nyListLast", "listValueId", &.{}),
        .listDropLast => b.listCall("nyListDropLast", "listValueId", &.{}),
        .listSlice => b.listCall("nyListSliceOwned", "listValueId", &.{ b.f("startId"), b.f("endId") }),
        .listSetAt => b.listCall("nyListSetAtOwned", "listValueId", &.{ b.f("indexValueId"), b.f("itemValueId") }),
        .listInsertAt => b.listCall("nyListInsertAt", "listValueId", &.{ b.f("indexValueId"), b.f("itemValueId") }),
        .listRemoveAt => b.listCall("nyListRemoveAt", "listValueId", &.{b.f("indexValueId")}),
        .listIndexOf => b.listCall("nyListIndexOf", "listValueId", &.{b.f("itemValueId")}),
        .listLastIndexOf => b.listCall("nyListLastIndexOf", "listValueId", &.{b.f("itemValueId")}),
        .listContains => cat(&.{ "nyListIndexOf(", b.elementOf("listValueId"), ", ", b.f("listValueId"), ", ", b.f("itemValueId"), ") != null" }),
        .listFold, .listScan, .setFold => blk: {
            const source_key = if (kind == .setFold) "sourceSetValueId" else "sourceListValueId";
            const state = b.typeAt("seedValueId");
            const text = b.callback("folderFunctionId", "context", &.{ role("state", state), role("item", itemType(b.typeAt(source_key))) }, state);
            const source = b.f(source_key);
            const seed = b.f("seedValueId");
            break :blk labeled(switch (kind) {
                .listFold => cat(&.{ "var state = ", seed, "; for (", source, ") |item| state = ", text, "; break :blk state;" }),
                .setFold => cat(&.{ "var state = ", seed, "; for (", source, ".items) |item| state = ", text, "; break :blk state;" }),
                else => cat(&.{ "const out = nyAlloc(", zig(state), ", ", source, ".len + 1); out[0] = ", seed, "; for (", source, ", 0..) |item, at| { const state = out[at]; out[at + 1] = ", text, "; } break :blk out;" }),
            });
        },
        .listMap => blk: {
            const text = b.callback("mapperFunctionId", "context", &.{role("item", itemType(b.typeAt("sourceListValueId")))}, itemType(b.result));
            const source = b.f("sourceListValueId");
            break :blk labeled(cat(&.{ "const out = nyAlloc(", b.resultElement(), ", ", source, ".len); for (", source, ", 0..) |item, at| out[at] = ", text, "; break :blk out;" }));
        },
        .listFilter, .listAny, .listAll, .listFindIndex => blk: {
            const text = b.callback("predicateFunctionId", "context", &.{role("item", itemType(b.typeAt("sourceListValueId")))}, &bool_type);
            const source = b.f("sourceListValueId");
            break :blk labeled(switch (kind) {
                .listFilter => cat(&.{ "const out = nyAlloc(", b.elementOf("sourceListValueId"), ", ", source, ".len); var count: usize = 0; for (", source, ") |item| { if (", text, ") { out[count] = nyShare(item); count += 1; } } break :blk nyShrink(", b.elementOf("sourceListValueId"), ", out, count);" }),
                .listAny => cat(&.{ "for (", source, ") |item| { if (", text, ") break :blk true; } break :blk false;" }),
                .listAll => cat(&.{ "for (", source, ") |item| { if (!", text, ") break :blk false; } break :blk true;" }),
                else => cat(&.{ "for (", source, ", 0..) |item, at| { if (", text, ") break :blk @as(i64, @intCast(at)); } break :blk null;" }),
            });
        },
        .listZipWith => blk: {
            const text = b.callback("combinerFunctionId", "context", &.{ role("l", itemType(b.typeAt("leftListValueId"))), role("r", itemType(b.typeAt("rightListValueId"))) }, itemType(b.result));
            const left = b.f("leftListValueId");
            const right = b.f("rightListValueId");
            break :blk labeled(cat(&.{ "const count = @min(", left, ".len, ", right, ".len); const out = nyAlloc(", b.resultElement(), ", count); for (", left, "[0..count], ", right, "[0..count], 0..) |l, r, at| out[at] = ", text, "; break :blk out;" }));
        },
        .listFlatMap => blk: {
            const text = b.callback("mapperFunctionId", "context", &.{role("item", itemType(b.typeAt("sourceListValueId")))}, b.result);
            break :blk labeled(cat(&.{ "var out: std.ArrayList(", b.resultElement(), ") = .empty; for (", b.f("sourceListValueId"), ") |item| { const part = ", text, "; for (part) |each| nyRetain(each); out.appendSlice(nyRaw(), part) catch @panic(\"out of memory\"); nyRelease(part); } break :blk nyAdoptList(", b.resultElement(), ", &out);" }));
        },
        .float64Literal => cat(&.{ "@as(f64, @bitCast(@as(u64, 0x", str(b.o, "bits"), ")))" }),
        .float64Add => binary(b, "+"),
        .float64Subtract => binary(b, "-"),
        .float64Multiply => binary(b, "*"),
        .float64Divide => binary(b, "/"),
        .float64Min => pair(b, "nyFloat64Min"),
        .float64Max => pair(b, "nyFloat64Max"),
        .float64FloorModulo => pair(b, "nyFloat64FloorModulo"),
        .float64Equal => binary(b, "=="),
        .float64LessThan => binary(b, "<"),
        .float64IsNaN => unary(b, "std.math.isNan"),
        .float64IsFinite => unary(b, "std.math.isFinite"),
        .float64IsInfinite => unary(b, "std.math.isInf"),
        .float64Sqrt => unary(b, "@sqrt"),
        .float64Abs => unary(b, "@abs"),
        .float64Negate => cat(&.{ "-", b.f("valueId") }),
        .float64Floor => unary(b, "@floor"),
        .float64Ceiling => unary(b, "@ceil"),
        .float64Truncate => unary(b, "@trunc"),
        // Zig's @round rounds halfway cases away from zero, as Float64RoundToInt64 requires.
        .float64RoundToInt64 => cat(&.{ "nyFloat64ToInt64(@round(", b.f("valueId"), "))" }),
        .float64FloorToInt64 => cat(&.{ "nyFloat64ToInt64(@floor(", b.f("valueId"), "))" }),
        .int64ToFloat64 => cat(&.{ "@as(f64, @floatFromInt(", b.f("valueId"), "))" }),
        // Bits32 is a u32 and every operation is total, as in the C# backend's uint.
        .bits32FromInt64 => unary(b, "nyBits32FromInt64"),
        .bits32ToInt64 => cat(&.{ "@as(i64, ", b.f("valueId"), ")" }),
        .bits32Xor => binary(b, "^"),
        .bits32And => binary(b, "&"),
        .bits32Or => binary(b, "|"),
        .bits32Not => cat(&.{ "~", b.f("valueId") }),
        .bits32Add => binary(b, "+%"),
        .bits32RotateRight => wrap(b, "nyBits32RotateRight", &.{ "valueId", "amountValueId" }),
        .bits32ShiftRight => wrap(b, "nyBits32ShiftRight", &.{ "valueId", "amountValueId" }),
        .bits32ShiftLeft => wrap(b, "nyBits32ShiftLeft", &.{ "valueId", "amountValueId" }),
        .setEmpty => ".{ .items = &.{} }",
        .mapEmpty => ".{ .keys = &.{}, .values = &.{} }",
        .setFromList => b.listCall("nySetFromList", "listValueId", &.{}),
        .setToList => cat(&.{ "nyShare(", b.f("setValueId"), ".items)" }),
        .setSize => cat(&.{ "@as(i64, @intCast(", b.f("setValueId"), ".items.len))" }),
        .setInsert => b.listCall("nySetInsertInto", "setValueId", &.{b.f("itemValueId")}),
        .setRemove => b.listCall("nySetRemove", "setValueId", &.{b.f("itemValueId")}),
        .setContains => cat(&.{ "nySetFind(", b.elementOf("setValueId"), ", ", b.f("setValueId"), ", ", b.f("itemValueId"), ") != null" }),
        .setUnion => b.listCall("nySetUnion", "leftSetValueId", &.{b.f("rightSetValueId")}),
        .setIntersection => b.listCall("nySetKeep", "leftSetValueId", &.{ b.f("rightSetValueId"), "true" }),
        .setDifference => b.listCall("nySetKeep", "leftSetValueId", &.{ b.f("rightSetValueId"), "false" }),
        .setIsSubset => b.listCall("nySetIsSubset", "leftSetValueId", &.{b.f("rightSetValueId")}),
        .setFilter => blk: {
            const text = b.callback("predicateFunctionId", "context", &.{role("item", itemType(b.typeAt("sourceSetValueId")))}, &bool_type);
            const source = b.f("sourceSetValueId");
            break :blk labeled(cat(&.{ "const out = nyAlloc(", b.elementOf("sourceSetValueId"), ", ", source, ".items.len); var count: usize = 0; for (", source, ".items) |item| { if (", text, ") { out[count] = nyShare(item); count += 1; } } break :blk nySetOf(", b.elementOf("sourceSetValueId"), ", nyShrink(", b.elementOf("sourceSetValueId"), ", out, count));" }));
        },
        .setMap => blk: {
            const text = b.callback("mapperFunctionId", "context", &.{role("item", itemType(b.typeAt("sourceSetValueId")))}, itemType(b.result));
            break :blk labeled(cat(&.{ "var out: ", zig(b.result), " = .{ .items = &.{} }; for (", b.f("sourceSetValueId"), ".items) |item| out = nySetInsertOwned(", b.resultElement(), ", out, ", text, "); break :blk out;" }));
        },
        .mapSize => cat(&.{ "@as(i64, @intCast(", b.f("mapValueId"), ".keys.len))" }),
        .mapKeys => cat(&.{ "nyShare(", b.f("mapValueId"), ".keys)" }),
        .mapValues => cat(&.{ "nyShare(", b.f("mapValueId"), ".values)" }),
        .mapInsert => b.mapCall("nyMapInsertInto", "mapValueId", &.{ b.f("keyValueId"), b.f("valueValueId") }),
        .mapRemove => b.mapCall("nyMapRemove", "mapValueId", &.{b.f("keyValueId")}),
        .mapGet => b.mapCall("nyMapGet", "mapValueId", &.{b.f("keyValueId")}),
        .mapGetOrDefault => blk: {
            const types = b.mapTypes("mapValueId");
            break :blk cat(&.{ "nyMapGet(", zig(types[0]), ", ", zig(types[1]), ", ", b.f("mapValueId"), ", ", b.f("keyValueId"), ") orelse nyShare(", b.f("defaultValueId"), ")" });
        },
        .mapContainsKey => cat(&.{ "nyMapFind(", zig(b.mapTypes("mapValueId")[0]), ", ", zig(b.mapTypes("mapValueId")[1]), ", ", b.f("mapValueId"), ", ", b.f("keyValueId"), ") != null" }),
        .mapUnion => b.mapCall("nyMapUnion", "leftMapValueId", &.{b.f("rightMapValueId")}),
        .mapFold => blk: {
            const types = b.mapTypes("sourceMapValueId");
            const state = b.typeAt("seedValueId");
            const text = b.callback("folderFunctionId", "context", &.{ role("state", state), role("key", types[0]), role("value", types[1]) }, state);
            const source = b.f("sourceMapValueId");
            break :blk labeled(cat(&.{ "var state = ", b.f("seedValueId"), "; for (", source, ".keys, ", source, ".values) |key, value| state = ", text, "; break :blk state;" }));
        },
        .mapFilter => blk: {
            const types = b.mapTypes("sourceMapValueId");
            const text = b.callback("keepFunctionId", "context", &.{ role("key", types[0]), role("value", types[1]) }, &bool_type);
            const source = b.f("sourceMapValueId");
            break :blk labeled(cat(&.{ "var out: ", zig(b.result), " = .{ .keys = &.{}, .values = &.{} }; for (", source, ".keys, ", source, ".values) |key, value| { if (", text, ") out = nyMapInsertOwned(", zig(types[0]), ", ", zig(types[1]), ", out, nyShare(key), nyShare(value)); } break :blk out;" }));
        },
        .mapMapValues => blk: {
            const types = b.mapTypes("sourceMapValueId");
            const result_value = if (b.result.* == .builtin and b.result.builtin.args.len == 2) b.result.builtin.args[1] else b.result;
            const text = b.callback("mapperFunctionId", "context", &.{ role("key", types[0]), role("value", types[1]) }, result_value);
            const source = b.f("sourceMapValueId");
            break :blk labeled(cat(&.{ "const out = nyAlloc(", zig(result_value), ", ", source, ".keys.len); for (", source, ".keys, ", source, ".values, 0..) |key, value, at| out[at] = ", text, "; break :blk .{ .keys = nyShare(", source, ".keys), .index = nyShare(", source, ".index), .values = out };" }));
        },
        .mapFromList => blk: {
            const key_type = if (b.result.* == .builtin and b.result.builtin.args.len == 2) b.result.builtin.args[0] else b.result;
            const element = itemType(b.typeAt("sourceListValueId"));
            const text = b.callback("keyOfFunctionId", "context", &.{role("item", element)}, key_type);
            break :blk labeled(cat(&.{ "var out: ", zig(b.result), " = .{ .keys = &.{}, .values = &.{} }; for (", b.f("sourceListValueId"), ") |item| out = nyMapInsertOwned(", zig(key_type), ", ", zig(element), ", out, ", text, ", nyShare(item)); break :blk out;" }));
        },
        .bytesEmpty => ".{ .data = &.{} }",
        .bytesFill => wrap(b, "nyBytesFill", &.{ "lengthId", "valueId" }),
        .bytesLength => cat(&.{ "@as(i64, @intCast(", b.f("valueId"), ".data.len))" }),
        .bytesGet => cat(&.{ "if (nyListItemAt(u8, ", b.f("bytesId"), ".data, ", b.f("indexId"), ")) |byte| @as(i64, byte) else null" }),
        .bytesSet => wrap(b, "nyBytesSet", &.{ "bytesId", "indexId", "valueId" }),
        .bytesAppend => cat(&.{ ".{ .data = nyListAppend(u8, ", b.f("bytesId"), ".data, @truncate(@as(u64, @bitCast(", b.f("valueId"), ")))) }" }),
        .bytesConcat => cat(&.{ ".{ .data = nyListConcat(u8, ", b.f("leftId"), ".data, ", b.f("rightId"), ".data) }" }),
        .bytesSlice => cat(&.{ ".{ .data = nyListSlice(u8, ", b.f("bytesId"), ".data, ", b.f("startId"), ", ", b.f("endId"), ") }" }),
        .bytesFromLatin1 => unary(b, "nyBytesFromLatin1"),
        .bytesToLatin1 => unary(b, "nyBytesToLatin1"),
        .capabilityInvoke => blk: {
            const operation = findOperation(str(b.o, "operationId"));
            var rendered: List([]const u8) = .empty;
            for (operation.parameters) |parameter| {
                for (arr(b.o, "arguments")) |argument| {
                    if (eql(str(argument.object, "targetCapabilityParameterId"), parameter.id)) push([]const u8, &rendered, v(str(argument.object, "valueId")));
                }
            }
            break :blk cat(&.{ "try ", b.f("capabilityValueId"), ".", operationName(operation.id), "(", join(", ", rendered.items), ")" });
        },
        .exceptionCatch => blk: {
            const body_call = b.callback("bodyFunctionId", "bodyContext", &.{role("NyUnit{}", &unit_type)}, b.result);
            if (!failing(str(b.o, "bodyFunctionId"))) break :blk body_call;
            const handler_call = b.callback("handlerFunctionId", "handlerContext", &.{role("failure", &exception_type)}, b.result);
            break :blk cat(&.{ body_call, " catch blk: { const failure = ny_failure; break :blk ", attempt(str(b.o, "handlerFunctionId"), handler_call), "; }" });
        },
        // The instruction's own types are the generic declaration's; the result type is the instance's.
        .exceptionRethrow => cat(&.{ "try nyRethrow(", zig(b.result), ", ", b.f("valueId"), ")" }),
        .exceptionMessage => cat(&.{ "nyListMove(u8, ", b.f("valueId"), ".message)" }),
        .exceptionIsCancellation => cat(&.{ b.f("valueId"), ".cancelled" }),
        // Release runs once acquisition succeeded: on Ok and Err exits, and before a failure of the
        // body propagates (a release failure then is ignored). A body Err wins over a release Err.
        .useResource => blk: {
            const acquired = b.typeAt("acquireValueId");
            const resource = if (acquired.* == .builtin and eql(acquired.builtin.kind, "result")) acquired.builtin.args[0] else acquired;
            const body_call = b.callback("bodyFunctionId", "context", &.{role("resource", resource)}, b.result);
            const operation = findOperation(str(b.o, "releaseOperationId"));
            const release = cat(&.{ b.f("releaseCapabilityValueId"), ".", operationName(operation.id), "(resource)" });
            const guarded = if (failing(str(b.o, "bodyFunctionId")))
                cat(&.{ body_call, " catch { const saved = ny_failure; _ = ", release, " catch {}; ny_failure = saved; return error.NyFailure; }" })
            else
                body_call;
            const finish = if (operation.result.* == .builtin and eql(operation.result.builtin.kind, "result"))
                cat(&.{ "const released = try ", release, "; if (outcome == .ok and released == .err) { nyRelease(outcome); break :blk .{ .err = released.err }; } nyRelease(released); break :blk outcome;" })
            else
                cat(&.{ "nyRelease(try ", release, "); break :blk outcome;" });
            break :blk labeled(cat(&.{ "const acquired = ", b.f("acquireValueId"), "; if (acquired == .err) break :blk .{ .err = nyShare(acquired.err) }; const resource = acquired.ok; const outcome = ", guarded, "; errdefer nyRelease(outcome); ", finish }));
        },
        .listSortBy, .listMinBy, .listMaxBy => blk: {
            // The best is replaced when lessThan(best, item): the same comparator with the items swapped.
            const parts = if (kind == .listMaxBy) b.comparator("r", "l") else b.comparator("l", "r");
            break :blk cat(&.{ if (kind == .listSortBy) "nySortBy(" else "nyBestBy(", b.elementOf("sourceListValueId"), ", ", b.f("sourceListValueId"), ", ", parts[0], ", ", parts[1], ")" });
        },
        .functionReference => blk: {
            if (b.result.* != .function) std.debug.panic("Expected a function type, got {s}.", .{typeJson(b.result)});
            const function_id = str(b.o, "functionId");
            const parameters = parametersOf(function_id);
            const args = a.alloc(Field, parameters.len) catch oom();
            for (parameters, b.result.function.params, 0..) |parameter, t, at| args[at] = .{ .id = parameter.id, .type = t };
            break :blk cat(&.{ "&", call(function_id, args, b.result.function.result) });
        },
        .functionInvoke => blk: {
            var args: List([]const u8) = .empty;
            for (arr(b.o, "arguments")) |item| push([]const u8, &args, v(item.string));
            break :blk cat(&.{ b.f("functionValueId"), "(", join(", ", args.items), ")" });
        },
        .cancellationRequested => "nyCancellationRequested()",
        .cancellationShield => blk: {
            const text = b.callback("bodyFunctionId", "context", &.{role("NyUnit{}", &unit_type)}, b.result);
            break :blk labeled(cat(&.{ "const saved = ny_token; ny_token = null; defer ny_token = saved; break :blk ", attempt(str(b.o, "bodyFunctionId"), text), ";" }));
        },
        .taskScope => blk: {
            const text = b.callback("bodyFunctionId", "context", &.{role("scope", &scope_type)}, b.result);
            const guarded = if (failing(str(b.o, "bodyFunctionId")))
                cat(&.{ text, " catch { const saved = ny_failure; nyScopeClose(scope, true) catch {}; ny_failure = saved; return error.NyFailure; }" })
            else
                text;
            break :blk labeled(cat(&.{ "const scope = nyScopeOpen(); const outcome = ", guarded, "; errdefer nyRelease(outcome); try nyScopeClose(scope, false); break :blk outcome;" }));
        },
        .taskStart => blk: {
            const work = argumentOf(b.result, 0);
            const item = b.f("itemValueId");
            const text = b.callback("workerFunctionId", "context", &.{role(item, b.typeAt("itemValueId"))}, work);
            var names = b.captured("context");
            names.insert(a, 0, item) catch oom();
            const travelled = deferred(text, names.items);
            const worker = str(b.o, "workerFunctionId");
            const result = cat(&.{ if (failing(worker)) "NyError!" else "", zig(work) });
            // A task that never starts releases the arguments the call would have moved in.
            const arguments = travelled[1][std.mem.indexOfScalar(u8, travelled[1], '(').? + 1 .. travelled[1].len - 1];
            break :blk labeled(cat(&.{ "const captured = ", travelled[0], "; break :blk nyStartTask(", zig(work), ", ", b.f("scopeValueId"), ", NyJob(@TypeOf(captured), ", result, ", struct { fn call(c: anytype) ", result, " { return ", attempt(worker, travelled[1]), "; } fn drop(c: anytype) void { _ = &c; nyReleaseAll(.{ ", arguments, " }); } }){ .c = captured });" }));
        },
        .taskWait => blk: {
            const work = argumentOf(itemType(b.typeAt("tasksValueId")), 0);
            const all = if (eql(str(b.o, "mode"), "all")) "true" else "false";
            const limit = if (b.o.get("limitValueId")) |id| v(id.string) else "null";
            break :blk cat(&.{ "try nyTaskWait(", zig(work), ", ", b.f("tasksValueId"), ", ", all, ", ", limit, ")" });
        },
        .taskCancel => labeled(cat(&.{ b.f("taskValueId"), ".base.cancel.cancel(); break :blk NyUnit{};" })),
        .oneShotCreate => cat(&.{ "nyOneShotCreate(", zig(argumentOf(b.result, 0)), ", ", b.f("scopeValueId"), ")" }),
        .oneShotComplete => cat(&.{ "nyOneShotComplete(", zig(argumentOf(b.typeAt("cellValueId"), 0)), ", ", b.f("cellValueId"), ", ", b.f("valueValueId"), ")" }),
        .oneShotWait => blk: {
            const limit = if (b.o.get("limitValueId")) |id| v(id.string) else "null";
            break :blk cat(&.{ "try nyOneShotWait(", zig(argumentOf(b.typeAt("cellValueId"), 0)), ", ", b.f("cellValueId"), ", ", limit, ")" });
        },
        .parallelAll => blk: {
            const element = itemType(b.typeAt("sourceListValueId"));
            const values = argumentOf(b.result, 0);
            const failure = argumentOf(b.result, 1);
            const outcome = newType(.{ .builtin = .{ .kind = "result", .args = a.dupe(*const Type, &.{ itemType(values), failure }) catch oom() } });
            const text = b.callback("workerFunctionId", "context", &.{role("item", element)}, outcome);
            const travelled = deferred(text, b.captured("context").items);
            const worker = str(b.o, "workerFunctionId");
            break :blk cat(&.{ "try nyParallelAll(", zig(element), ", ", zig(itemType(values)), ", ", zig(failure), ", ", b.f("sourceListValueId"), ", ", travelled[0], ", struct { fn call(c: anytype, item: ", zig(element), ") NyError!", zig(outcome), " { _ = &c; return ", attempt(worker, travelled[1]), "; } }.call)" });
        },
        .listEffectFold => blk: {
            const state = b.typeAt("seedValueId");
            const element = itemType(b.typeAt("sourceListValueId"));
            const folder = str(b.o, "folderFunctionId");
            const source = b.f("sourceListValueId");
            if (sameType(b.result, state)) {
                const text = b.callback("folderFunctionId", "context", &.{ role("state", state), role("item", element) }, state);
                break :blk labeled(cat(&.{ "var state = ", b.f("seedValueId"), "; for (", source, ") |item| state = ", attempt(folder, text), "; break :blk state;" }));
            }
            // The folder returns Result<S, E>: the fold stops at the first Err.
            const text = b.callback("folderFunctionId", "context", &.{ role("state.ok", state), role("item", element) }, b.result);
            break :blk labeled(cat(&.{ "var state: ", zig(b.result), " = .{ .ok = ", b.f("seedValueId"), " }; for (", source, ") |item| { if (state == .err) break; state = ", attempt(folder, text), "; } break :blk state;" }));
        },
        .int64CheckedAdd => cat(&.{ "nyChecked(", pair(b, "@addWithOverflow"), ")" }),
        .int64CheckedSubtract => cat(&.{ "nyChecked(", pair(b, "@subWithOverflow"), ")" }),
        .int64CheckedMultiply => cat(&.{ "nyChecked(", pair(b, "@mulWithOverflow"), ")" }),
        .int64ToHexString => unary(b, "nyInt64ToHexString"),
        .stringParseInt64 => unary(b, "nyStringParseInt64"),
        .stringParseInt64Hex => unary(b, "nyStringParseInt64Hex"),
        .float64ToString => unary(b, "nyFloat64ToString"),
        .stringParseFloat64 => unary(b, "nyStringParseFloat64"),
        .float64Round => wrap(b, "nyFloat64Round", &.{ "valueId", "digitsValueId" }),
        .stringSlice => wrap(b, "nyStringSlice", &.{ "valueId", "startId", "endId" }),
        .stringContains => cat(&.{ "std.mem.indexOf(u8, ", b.f("valueId"), ", ", b.f("substringId"), ") != null" }),
        .stringIndexOf => wrap(b, "nyStringIndexOf", &.{ "valueId", "substringId", "fromId" }),
        .stringLastIndexOf => cat(&.{ "nyStringLastIndexOfBefore(", b.f("valueId"), ", ", b.f("substringId"), ", std.math.maxInt(i64))" }),
        .stringLastIndexOfBefore => wrap(b, "nyStringLastIndexOfBefore", &.{ "valueId", "substringId", "endId" }),
        .stringSpan => wrap(b, "nyStringSpan", &.{ "valueId", "setId", "fromId" }),
        .stringSpanBack => wrap(b, "nyStringSpanBack", &.{ "valueId", "setId", "endId" }),
        .stringStartsWith => cat(&.{ "std.mem.startsWith(u8, ", b.f("valueId"), ", ", b.f("prefixId"), ")" }),
        .stringEndsWith => cat(&.{ "std.mem.endsWith(u8, ", b.f("valueId"), ", ", b.f("suffixId"), ")" }),
        .stringSplit => wrap(b, "nyStringSplit", &.{ "valueId", "separatorId" }),
        .stringSplitLines => wrap(b, "nyStringSplitLines", &.{ "valueId", "breaksId" }),
        .stringReplace => wrap(b, "nyStringReplace", &.{ "valueId", "patternId", "replacementId" }),
        .stringTrim => unary(b, "nyStringTrim"),
        .stringTrimStart => unary(b, "nyStringTrimStart"),
        .stringTrimEnd => unary(b, "nyStringTrimEnd"),
        .stringJoin => wrap(b, "nyStringJoin", &.{ "listValueId", "separatorId" }),
        .stringLessThan => cat(&.{ "std.mem.lessThan(u8, ", b.f("leftValueId"), ", ", b.f("rightValueId"), ")" }),
        .stringCodePoints => unary(b, "nyStringCodePoints"),
        .stringFromCodePoint => unary(b, "nyStringFromCodePoint"),
        .stringToCodePoint => unary(b, "nyStringToCodePoint"),
        .stringUtf8ByteCount => cat(&.{ "@as(i64, @intCast(", b.f("valueId"), ".len))" }),
        .stringEncodeUtf8 => unary(b, "nyStringEncodeUtf8"),
        .stringDecodeUtf8Lossy => unary(b, "nyStringDecodeUtf8Lossy"),
        .stringRepeat => wrap(b, "nyStringRepeat", &.{ "textValueId", "countValueId" }),
        .stringToLower => cat(&.{ "nyMapCase(", b.f("valueId"), ", &ny_lower_runs)" }),
        .stringToUpper => cat(&.{ "nyMapCase(", b.f("valueId"), ", &ny_upper_runs)" }),
        .directCall => blk: {
            const function_id = str(b.o, "functionId");
            const parameters = parametersOf(function_id);
            const args = a.alloc(Field, parameters.len) catch oom();
            const rendered = a.alloc([]const u8, parameters.len) catch oom();
            for (parameters, 0..) |parameter, at| {
                var value_id: ?[]const u8 = null;
                for (arr(b.o, "arguments")) |argument| {
                    if (eql(str(argument.object, "targetParameterId"), parameter.id)) value_id = str(argument.object, "valueId");
                }
                args[at] = .{ .id = parameter.id, .type = b.valueType(value_id.?) };
                rendered[at] = v(value_id.?);
            }
            const name = call(function_id, args, b.result);
            break :blk attempt(function_id, cat(&.{ name, "(", join(", ", rendered), ")" }));
        },
    };
}

/// A line of several parts, appended without joining them first.
fn lineCat(out: *List(u8), parts: []const []const u8) void {
    for (parts) |part| out.appendSlice(a, part) catch oom();
    push(u8, out, '\n');
}

fn line(out: *List(u8), text: []const u8) void {
    out.appendSlice(a, text) catch oom();
    push(u8, out, '\n');
}

fn valueKey(item: Value) []const u8 {
    return item.id;
}

// ---------------------------------------------------------------- reference counting (Perceus)

/// Whether a value of this type holds counted storage (runtime `nyCounted`); task and hand-off cell
/// handles count their records. Scopes, exceptions (their messages are static or runtime records),
/// capabilities and function values carry no count.
fn counted(t: *const Type) bool {
    return switch (t.*) {
        .primitive => |kind| eql(kind, "primitiveString") or eql(kind, "primitiveBytes"),
        .builtin => |builtin| blk: {
            for ([_][]const u8{ "taskScope", "exception" }) |kind| if (eql(builtin.kind, kind)) break :blk false;
            if (eql(builtin.kind, "option") or eql(builtin.kind, "result")) {
                for (builtin.args) |arg| if (counted(arg)) break :blk true;
                break :blk false;
            }
            break :blk true;
        },
        .named => true,
        else => false,
    };
}

/// The operands an instruction consumes (owned: stored or passed to a call); every other operand
/// is borrowed for the instruction.
fn consumedKey(kind: []const u8, key: []const u8) bool {
    const table = [_][2][]const u8{
        .{ "readParameter", "sourceValueId" }, .{ "readPatternBinding", "sourceValueId" }, .{ "optionSome", "valueId" },
        .{ "resultOk", "valueId" },            .{ "resultErr", "errorValueId" },           .{ "productConstruct", "fields" },
        .{ "unionConstruct", "fields" },       .{ "productUpdate", "fields" },             .{ "productUpdate", "sourceValueId" },
        .{ "stringConcat", "leftValueId" },    .{ "listAppendItem", "listValueId" },       .{ "listConcat", "leftListValueId" },
        .{ "listSlice", "listValueId" },       .{ "listSetAt", "listValueId" },            .{ "setInsert", "setValueId" },
        .{ "mapInsert", "mapValueId" },        .{ "listConstruct", "itemValueIds" },       .{ "directCall", "arguments" },
        .{ "functionInvoke", "arguments" },    .{ "listFold", "seedValueId" },             .{ "listScan", "seedValueId" },
        .{ "setFold", "seedValueId" },         .{ "mapFold", "seedValueId" },              .{ "listEffectFold", "seedValueId" },
        .{ "taskStart", "itemValueId" },       .{ "taskStart", "context" },                .{ "oneShotComplete", "valueValueId" },
        .{ "return", "valueId" },              .{ "abort", "errorValueId" },               .{ "jump", "arguments" },
        .{ "tailCall", "arguments" },
    };
    for (table) |row| if (eql(row[0], kind) and eql(row[1], key)) return true;
    return false;
}

const Use = struct { id: []const u8, consumed: bool };
const Set = std.StringArrayHashMapUnmanaged(void);

const Ownership = struct {
    values: *const std.StringHashMap(*const Type),
    fn_: Function,
    live_in: std.StringHashMap(Set),

    fn isCounted(self: *const Ownership, id: []const u8) bool {
        return if (self.values.get(id)) |t| counted(t) else false;
    }

    fn collect(self: *const Ownership, node: json.Value, consumed: bool, out: *List(Use)) void {
        switch (node) {
            .string => |id| if (self.isCounted(id)) push(Use, out, .{ .id = id, .consumed = consumed }),
            .array => |items| for (items.items) |item| self.collect(item, consumed, out),
            .object => |o| {
                var it = o.iterator();
                while (it.next()) |entry| {
                    if (std.mem.startsWith(u8, entry.key_ptr.*, "target")) continue;
                    self.collect(entry.value_ptr.*, consumed, out);
                }
            },
            else => {},
        }
    }

    /// The counted values an instruction or terminator reads, each with whether it is consumed.
    fn uses(self: *const Ownership, o: json.ObjectMap) []const Use {
        const kind = str(o, "kind");
        var out: List(Use) = .empty;
        var it = o.iterator();
        while (it.next()) |entry| {
            const key = entry.key_ptr.*;
            if (eql(key, "result") or eql(key, "kind") or std.mem.startsWith(u8, key, "target")) continue;
            self.collect(entry.value_ptr.*, consumedKey(kind, key), &out);
        }
        return out.items;
    }

    fn block(self: *const Ownership, id: []const u8) Block {
        for (self.fn_.blocks) |b| if (eql(b.id, id)) return b;
        std.debug.panic("unknown block {s}", .{id});
    }

    /// Each successor with the values its entry defines (block or function parameters).
    fn successors(self: *const Ownership, t: json.ObjectMap) []const []const u8 {
        const kind = str(t, "kind");
        var out: List([]const u8) = .empty;
        if (eql(kind, "jump")) push([]const u8, &out, str(t, "blockId"));
        if (eql(kind, "tailCall")) push([]const u8, &out, self.fn_.entry);
        for ([_][2][]const u8{ .{ "branch", "whenTrue" }, .{ "branch", "whenFalse" }, .{ "matchOption", "someBlock" }, .{ "matchOption", "noneBlock" }, .{ "matchResult", "okBlock" }, .{ "matchResult", "errBlock" } }) |row| {
            if (eql(kind, row[0])) push([]const u8, &out, str(t, row[1]));
        }
        if (eql(kind, "matchUnion")) for (arr(t, "targets")) |target| push([]const u8, &out, str(target.object, "blockId"));
        return out.items;
    }

    /// Values an edge into `target` defines: its block parameters, or the function parameters on a tail call.
    fn defined(self: *const Ownership, kind: []const u8, target: []const u8) []const []const u8 {
        var out: List([]const u8) = .empty;
        if (eql(kind, "tailCall")) {
            for (self.fn_.parameters) |parameter| push([]const u8, &out, parameter.value.id);
        } else for (self.block(target).parameters) |parameter| push([]const u8, &out, parameter.id);
        return out.items;
    }

    /// The values live on the edge into `target` after it defines its parameters' values.
    fn edgeLive(self: *const Ownership, kind: []const u8, target: []const u8) Set {
        var live = self.copy(self.live_in.get(target).?);
        for (self.defined(kind, target)) |id| _ = live.swapRemove(id);
        return live;
    }

    fn copy(_: *const Ownership, set: Set) Set {
        return set.clone(a) catch oom();
    }

    /// Values live before the terminator: its uses and what any successor needs.
    fn beforeTerminator(self: *const Ownership, b: Block) Set {
        var live = Set.empty;
        const kind = str(b.terminator, "kind");
        for (self.successors(b.terminator)) |target| {
            const entering = self.edgeLive(kind, target);
            for (entering.keys()) |id| live.put(a, id, {}) catch oom();
        }
        for (self.uses(b.terminator)) |use| live.put(a, use.id, {}) catch oom();
        return live;
    }

    /// Live-in per block (before its parameters are defined), to a fixpoint.
    fn analyse(self: *Ownership) void {
        for (self.fn_.blocks) |b| self.live_in.put(b.id, Set.empty) catch oom();
        var changed = true;
        while (changed) {
            changed = false;
            var index = self.fn_.blocks.len;
            while (index > 0) {
                index -= 1;
                const b = self.fn_.blocks[index];
                var live = self.beforeTerminator(b);
                var at = b.instructions.len;
                while (at > 0) {
                    at -= 1;
                    _ = live.swapRemove(b.instructions[at].result.id);
                    for (self.uses(b.instructions[at].o)) |use| live.put(a, use.id, {}) catch oom();
                }
                const old = self.live_in.getPtr(b.id).?;
                for (live.keys()) |id| if (!old.contains(id)) {
                    old.put(a, id, {}) catch oom();
                    changed = true;
                };
            }
        }
    }

    /// Retains before and releases after one instruction or terminator edge, given what is live after it.
    fn around(self: *const Ownership, used: []const Use, after: *const Set, before: *List([]const u8), released: *List([]const u8)) void {
        var seen = Set.empty;
        for (used) |use| {
            if ((seen.getOrPut(a, use.id) catch oom()).found_existing) continue;
            var consumed: usize = 0;
            var borrowed: usize = 0;
            for (used) |other| if (eql(other.id, use.id)) {
                if (other.consumed) consumed += 1 else borrowed += 1;
            };
            const dies = !after.contains(use.id);
            const retains = if (dies and consumed > 0 and borrowed == 0) consumed - 1 else consumed;
            for (0..retains) |_| push([]const u8, before, cat(&.{ "nyRetain(", v(use.id), ");" }));
            if (dies and (consumed == 0 or borrowed > 0)) push([]const u8, released, cat(&.{ "nyRelease(", v(use.id), ");" }));
        }
        _ = self;
    }

    /// The instruction lines of a block with their retains and releases.
    fn blockLines(self: *const Ownership, b: Block, lines: []const []const u8) []const []const u8 {
        var live = self.beforeTerminator(b);
        const out = a.alloc([]const u8, lines.len) catch oom();
        var at = b.instructions.len;
        while (at > 0) {
            at -= 1;
            const instruction = b.instructions[at];
            var before: List([]const u8) = .empty;
            var after: List([]const u8) = .empty;
            self.around(self.uses(instruction.o), &live, &before, &after);
            const result = instruction.result.id;
            if (counted(instruction.result.type) and !live.contains(result)) push([]const u8, &after, cat(&.{ "nyRelease(", v(result), ");" }));
            // A failure leaves the frame holding what is live after the instruction and what it
            // borrows; what it consumes belongs to the callee, which releases it on its own failure.
            var held: List([]const u8) = .empty;
            if (instructionFails(instruction.o)) {
                var seen = Set.empty;
                for (live.keys()) |id| if (!eql(id, result)) seen.put(a, id, {}) catch oom();
                for (self.uses(instruction.o)) |use| if (!use.consumed) seen.put(a, use.id, {}) catch oom();
                for (seen.keys()) |id| push([]const u8, &held, cat(&.{ "nyRelease(", v(id), ");" }));
            }
            out[at] = if (held.items.len > 0)
                cat(&.{ "{ errdefer { ", cat(held.items), " } ", cat(before.items), lines[at], " }", cat(after.items) })
            else
                cat(&.{ cat(before.items), lines[at], cat(after.items) });
            _ = live.swapRemove(result);
            for (self.uses(instruction.o)) |use| live.put(a, use.id, {}) catch oom();
        }
        return out;
    }

    /// Releases of the values an edge drops: the terminator's live values the target does not need
    /// (borrowed terminator operands included), plus retains of consumed arguments still live.
    fn edge(self: *const Ownership, b: Block, target: ?[]const u8) []const u8 {
        const kind = str(b.terminator, "kind");
        const live = self.beforeTerminator(b);
        const after = if (target) |id| self.edgeLive(kind, id) else Set.empty;
        const used = self.uses(b.terminator);
        var before: List([]const u8) = .empty;
        var released: List([]const u8) = .empty;
        self.around(used, &after, &before, &released);
        for (live.keys()) |id| {
            if (after.contains(id)) continue;
            const is_use = for (used) |use| {
                if (eql(use.id, id)) break true;
            } else false;
            if (!is_use) push([]const u8, &released, cat(&.{ "nyRelease(", v(id), ");" }));
        }
        return cat(&.{ cat(before.items), cat(released.items) });
    }

    /// Releases of parameters the entered block never reads, after an edge (or the call) defines them.
    fn deadParameters(self: *const Ownership, kind: []const u8, target: []const u8) []const u8 {
        const live = self.live_in.get(target).?;
        var out: List([]const u8) = .empty;
        if (eql(kind, "tailCall") or eql(kind, "call")) {
            for (self.fn_.parameters) |parameter| if (counted(parameter.value.type) and !live.contains(parameter.value.id))
                push([]const u8, &out, cat(&.{ "nyRelease(", v(parameter.value.id), ");" }));
        } else for (self.block(target).parameters) |parameter| if (counted(parameter.type) and !live.contains(parameter.id))
            push([]const u8, &out, cat(&.{ "nyRelease(", v(parameter.id), ");" }));
        return cat(out.items);
    }
};

fn emitFunction(out: *List(u8), name: []const u8, fn_: Function, lines: []const []const u8, values_of: *const std.StringHashMap(*const Type)) void {
    var own = Ownership{ .values = values_of, .fn_ = fn_, .live_in = .init(a) };
    own.analyse();
    var parameters: List([]const u8) = .empty;
    for (fn_.parameters) |parameter| push([]const u8, &parameters, cat(&.{ identifier("p_", parameter.id), ": ", zigOf(parameter.value.type).? }));
    const failure = if (failing(fn_.id)) "NyError!" else "";
    lineCat(out, &.{ "fn ", name, "(", join(", ", parameters.items), ") ", failure, zigOf(fn_.result).?, " {" });
    // Every value is a function-level variable, so blocks can assign it in any order; `_ = &x`
    // keeps Zig from rejecting a value that some path never reads.
    var values: List(Value) = .empty;
    var seen = std.StringHashMap(void).init(a);
    for (valuesOf(fn_)) |value| {
        if ((seen.getOrPut(value.id) catch oom()).found_existing) continue;
        push(Value, &values, value);
    }
    byId(Value, values.items, valueKey);
    for (values.items) |value| {
        lineCat(out, &.{ "    var ", v(value.id), ": ", zigOf(value.type).?, " = undefined;" });
        lineCat(out, &.{ "    _ = &", v(value.id), ";" });
    }
    for (fn_.parameters) |parameter| lineCat(out, &.{ "    ", v(parameter.value.id), " = ", identifier("p_", parameter.id), ";" });
    line(out, cat(&.{ "    ", own.deadParameters("call", fn_.entry) }));
    var names: List([]const u8) = .empty;
    for (fn_.blocks) |b| push([]const u8, &names, blockName(b.id));
    lineCat(out, &.{ "    const Block = enum { ", join(", ", names.items), " };" });
    // Blocks become prongs of a labeled switch; a jump is `continue :body .target`. Zig rejects an
    // unused label, so a function whose blocks never jump gets none.
    var jumps = false;
    for (fn_.blocks) |b| {
        const kind = str(b.terminator, "kind");
        if (!eql(kind, "return") and !eql(kind, "abort")) jumps = true;
    }
    lineCat(out, &.{ "    ", if (jumps) "body: " else "", "switch (Block.", blockName(fn_.entry), ") {" });
    var next: usize = 0;
    for (fn_.blocks) |b| {
        lineCat(out, &.{ "        .", blockName(b.id), " => {" });
        if (!eql(b.id, fn_.entry)) lineCat(out, &.{ "            ", own.deadParameters("jump", b.id) });
        for (own.blockLines(b, lines[next..][0..b.instructions.len])) |text| lineCat(out, &.{ "            ", text });
        next += b.instructions.len;
        const t = b.terminator;
        const kind = str(t, "kind");
        const edge = struct {
            fn to(o: *const Ownership, from: Block, target: []const u8) []const u8 {
                return cat(&.{ "{ ", o.edge(from, target), "continue :body .", blockName(target), "; }" });
            }
        }.to;
        if (eql(kind, "return")) {
            lineCat(out, &.{ "            ", own.edge(b, null), "return ", v(str(t, "valueId")), ";" });
        } else if (eql(kind, "abort")) {
            lineCat(out, &.{ "            ", own.edge(b, null), "return .{ .err = ", v(str(t, "errorValueId")), " };" });
        } else if (eql(kind, "jump")) {
            lineCat(out, &.{ "            ", own.edge(b, str(t, "blockId")) });
            var pairs: List([2][]const u8) = .empty;
            const arguments = a.dupe(json.Value, arr(t, "arguments")) catch oom();
            byId(json.Value, arguments, targetValueKey);
            for (arguments) |argument| push([2][]const u8, &pairs, .{ str(argument.object, "targetValueId"), str(argument.object, "valueId") });
            assignAll(out, pairs.items);
            lineCat(out, &.{ "            continue :body .", blockName(str(t, "blockId")), ";" });
        } else if (eql(kind, "branch")) {
            lineCat(out, &.{ "            if (", v(str(t, "conditionValueId")), ") ", edge(&own, b, str(t, "whenTrue")) });
            lineCat(out, &.{ "            ", edge(&own, b, str(t, "whenFalse")) });
        } else if (eql(kind, "matchOption")) {
            lineCat(out, &.{ "            if (", v(str(t, "inputValueId")), " != null) ", edge(&own, b, str(t, "someBlock")) });
            lineCat(out, &.{ "            ", edge(&own, b, str(t, "noneBlock")) });
        } else if (eql(kind, "matchResult")) {
            lineCat(out, &.{ "            if (", v(str(t, "inputValueId")), " == .ok) ", edge(&own, b, str(t, "okBlock")) });
            lineCat(out, &.{ "            ", edge(&own, b, str(t, "errBlock")) });
        } else if (eql(kind, "matchUnion")) {
            lineCat(out, &.{ "            switch (", v(str(t, "inputValueId")), ".*) {" });
            const targets = a.dupe(json.Value, arr(t, "targets")) catch oom();
            byId(json.Value, targets, variantKey);
            for (targets) |target| lineCat(out, &.{ "                .", variantName(str(target.object, "variantId")), " => ", edge(&own, b, str(target.object, "blockId")), "," });
            line(out, "            }");
        } else if (eql(kind, "tailCall")) {
            var pairs: List([2][]const u8) = .empty;
            for (fn_.parameters) |parameter| {
                for (arr(t, "arguments")) |argument| {
                    if (eql(str(argument.object, "targetParameterId"), parameter.id)) push([2][]const u8, &pairs, .{ parameter.value.id, str(argument.object, "valueId") });
                }
            }
            lineCat(out, &.{ "            ", own.edge(b, fn_.entry) });
            assignAll(out, pairs.items);
            lineCat(out, &.{ "            ", own.deadParameters("tailCall", fn_.entry), "continue :body .", blockName(fn_.entry), ";" });
        } else std.debug.panic("unknown terminator {s}", .{kind});
        line(out, "        },");
    }
    line(out, "    }");
    line(out, "}");
    line(out, "");
}

fn targetValueKey(item: json.Value) []const u8 {
    return str(item.object, "targetValueId");
}

fn variantKey(item: json.Value) []const u8 {
    return str(item.object, "variantId");
}

fn assignAll(out: *List(u8), pairs: []const [2][]const u8) void {
    if (pairs.len == 0) return;
    line(out, "            {");
    for (pairs, 0..) |item, index| line(out, std.fmt.allocPrint(a, "                const t{d} = {s};", .{ index, v(item[1]) }) catch oom());
    for (pairs, 0..) |item, index| line(out, std.fmt.allocPrint(a, "                {s} = t{d};", .{ v(item[0]), index }) catch oom());
    line(out, "            }");
}

/// Fields and variants are emitted in stable-ID order, the order runPure tokens and results use.
fn emitType(out: *List(u8), declaration: Declaration, args: []const *const Type) void {
    var map = Bindings.init(a);
    for (declaration.type_parameters, args) |id, arg| map.put(id, arg) catch oom();
    const name = typeName(declaration.id, args);
    if (declaration.product) |fields| {
        lineCat(out, &.{ "const ", name, " = struct { ", fieldTypes(&map, fields), " };" });
    } else {
        lineCat(out, &.{ "const ", name, " = union(enum) {" });
        var ids: List([]const u8) = .empty;
        for (declaration.variants) |variant| {
            lineCat(out, &.{ "    ", variantName(variant.id), ": struct { ", fieldTypes(&map, variant.fields), " }," });
            push([]const u8, &ids, stringLiteral(variant.id));
        }
        lineCat(out, &.{ "    pub const ny_ids = [_][]const u8{ ", join(", ", ids.items), " };" });
        line(out, "};");
    }
    line(out, "");
}

fn fieldTypes(map: *const Bindings, fields: []const Field) []const u8 {
    var parts: List([]const u8) = .empty;
    for (fields) |field| push([]const u8, &parts, cat(&.{ fieldName(field.id), ": ", zig(substitute(map, field.type)) }));
    return join(", ", parts.items);
}

/// Whether an instruction can raise a failure: an effectful kind, or a call of a function that may fail.
fn instructionFails(o: json.ObjectMap) bool {
    const kind = str(o, "kind");
    for ([_][]const u8{ "capabilityInvoke", "exceptionRethrow", "useResource", "taskScope", "taskWait", "oneShotWait", "parallelAll" }) |direct| {
        if (eql(kind, direct)) return true;
    }
    for ([_][2][]const u8{ .{ "directCall", "functionId" }, .{ "exceptionCatch", "handlerFunctionId" }, .{ "cancellationShield", "bodyFunctionId" }, .{ "listEffectFold", "folderFunctionId" } }) |callee| {
        if (eql(kind, callee[0]) and may_fail.contains(str(o, callee[1]))) return true;
    }
    return false;
}

fn computeMayFail() void {
    may_fail = .init(a);
    var changed = true;
    while (changed) {
        changed = false;
        var each = functions.valueIterator();
        while (each.next()) |fn_| {
            if (may_fail.contains(fn_.id)) continue;
            for (fn_.blocks) |b| for (b.instructions) |instruction| {
                if (!may_fail.contains(fn_.id) and instructionFails(instruction.o)) {
                    may_fail.put(fn_.id, {}) catch oom();
                    changed = true;
                }
            };
        }
    }
}

/// `ZigBackend.emit`: the program's non-generic functions and every instance of a generic function
/// or type they reach, or null after recording what is outside the supported slice.
fn emit(ordered: []const Function) ?[]const u8 {
    var builder: List(u8) = .empty;
    builder.appendSlice(a, "// Generated by the Nyrwvy Zig backend.\n\n") catch oom();
    builder.appendSlice(a, runtime) catch oom();
    push(u8, &builder, '\n');
    const maps_case = for (ordered) |fn_| {
        if (for (fn_.blocks) |b| {
            if (for (b.instructions) |instruction| {
                const kind = str(instruction.o, "kind");
                if (eql(kind, "stringToLower") or eql(kind, "stringToUpper")) break true;
            } else false) break true;
        } else false) break true;
    } else false;
    if (maps_case) {
        builder.appendSlice(a, case_mapping) catch oom();
        push(u8, &builder, '\n');
    }
    var bodies: List(u8) = .empty;
    for (ordered) |fn_| {
        if (fn_.type_parameters.len == 0) {
            const key = instanceKey(fn_.id, &.{});
            if (!function_instances.contains(key)) {
                function_instances.put(key, {}) catch oom();
                push(Instance, &pending, .{ .id = fn_.id, .args = &.{} });
            }
        }
    }
    var head: usize = 0;
    while (head < pending.items.len) : (head += 1) {
        const instance = pending.items[head];
        const generic = functions.get(instance.id).?;
        var map = Bindings.init(a);
        for (generic.type_parameters, instance.args) |id, arg| map.put(id, arg) catch oom();
        const fn_ = substituteFunction(&map, generic);
        const values = valuesOf(fn_);
        var body = Body{ .values = .init(a) };
        for (values) |value| body.values.put(value.id, value.type) catch oom();
        for (values) |value| _ = zigOf(value.type);
        _ = zigOf(fn_.result);
        var lines: List([]const u8) = .empty;
        for (fn_.blocks) |b| for (b.instructions) |instruction| {
            if (instructionLine(&body, instruction)) |text| push([]const u8, &lines, text) else {
                const kind = str(instruction.o, "kind");
                problem(cat(&.{ &.{std.ascii.toUpper(kind[0])}, kind[1..] }));
            }
        };
        if (problems.items.len == 0) emitFunction(&bodies, instanceName(instance.id, instance.args), fn_, lines.items, &body.values);
    }
    // Fake declarations also mention types that no selected function necessarily uses.
    for (capabilities) |capability| for (capability.operations) |operation| {
        _ = zigType(operation.result, true);
        for (operation.parameters) |parameter| _ = zigType(parameter.type, true);
    };
    // Field types can name further instances, so declarations are emitted until none is new.
    var types_out: List(u8) = .empty;
    var emitted: usize = 0;
    while (problems.items.len == 0 and emitted < type_order.items.len) : (emitted += 1) {
        const instance = type_order.items[emitted];
        emitType(&types_out, declarations.get(instance.id).?, instance.args);
    }
    if (problems.items.len > 0) return null;
    builder.appendSlice(a, types_out.items) catch oom();
    builder.appendSlice(a, bodies.items) catch oom();
    return builder.items;
}

/// `ZigBackend.fakeCapabilities`: scripted fake capabilities for a runPure runner.
fn fakeCapabilities() ?[]const u8 {
    var seen = std.StringHashMap(void).init(a);
    var slots: usize = 16;
    var entries = functions.valueIterator();
    while (entries.next()) |function| {
        var count: usize = 0;
        for (function.parameters) |parameter| if (parameter.value.type.* == .capability) {
            count += 1;
        };
        slots = @max(slots, count);
    }
    for (capabilities) |capability| for (capability.operations) |operation| {
        var types: List(*const Type) = .empty;
        push(*const Type, &types, operation.result);
        for (operation.parameters) |parameter| push(*const Type, &types, parameter.type);
        for (types.items) |t| {
            if (t.* != .capability and zigType(t, false) != null) continue;
            const text = cat(&.{ "capability operation type ", typeDisplay(t) });
            if (seen.contains(text)) continue;
            seen.put(text, {}) catch oom();
            problem(text);
        }
    };
    if (problems.items.len > 0) return null;
    var out: List(u8) = .empty;
    for (capabilities) |capability| {
        line(&out, cat(&.{ "const ", identifier("C_", capability.id), " = struct {" }));
        line(&out, "    fake: usize,");
        line(&out, "    site: usize,");
        for (capability.operations, 0..) |operation, index| {
            const result = zigType(operation.result, false).?;
            var parameters: List([]const u8) = .empty;
            var recorded: List([]const u8) = .empty;
            for (operation.parameters) |parameter| {
                push([]const u8, &parameters, cat(&.{ ", ", identifier("a_", parameter.id), ": ", zigType(parameter.type, false).? }));
                push([]const u8, &recorded, identifier("a_", parameter.id));
            }
            const keyed_fields = a.dupe(Field, operation.parameters) catch oom();
            byId(Field, keyed_fields, fieldKey);
            var keyed: List([]const u8) = .empty;
            for (keyed_fields) |parameter| push([]const u8, &keyed, identifier("a_", parameter.id));
            const scripts = identifier("ny_scripts_", operation.id);
            line(&out, cat(&.{ "    pub fn ", operationName(operation.id), "(self: @This()", cat(parameters.items), ") NyError!", result, " {" }));
            line(&out, cat(&.{ "        nyRecordCall(self.site + ", std.fmt.allocPrint(a, "{d}", .{index}) catch oom(), ", .{ ", join(", ", recorded.items), " });" }));
            line(&out, cat(&.{ "        return nyRespond(", result, ", &", scripts, "[self.fake], .{ ", join(", ", keyed.items), " });" }));
            line(&out, "    }");
        }
        line(&out, "};");
        for (capability.operations) |operation| {
            line(&out, cat(&.{ "var ", identifier("ny_scripts_", operation.id), ": [", std.fmt.allocPrint(a, "{d}", .{slots}) catch oom(), "]NyScript(", zigType(operation.result, false).?, ") = undefined;" }));
        }
        line(&out, "");
    }
    return out.items;
}

/// Function values, exceptions and task handles are not run values, so an entry taking or
/// returning one is not dispatched. A named type mentions one when any of its fields does.
fn mentionsFunction(t: *const Type) bool {
    var seen: List([]const u8) = .empty;
    return mentionsFunctionIn(t, &seen);
}

fn mentionsFunctionIn(t: *const Type, seen: *List([]const u8)) bool {
    return switch (t.*) {
        .function => true,
        .builtin => |builtin| for ([_][]const u8{ "task", "oneShot", "taskScope", "exception" }) |kind| {
            if (eql(builtin.kind, kind)) break true;
        } else for (builtin.args) |arg| {
            if (mentionsFunctionIn(arg, seen)) break true;
        } else false,
        .named => |named| {
            for (named.args) |arg| if (mentionsFunctionIn(arg, seen)) return true;
            // A recursive type is checked once.
            for (seen.items) |id| if (eql(id, named.id)) return false;
            push([]const u8, seen, named.id);
            const declaration = declarations.get(named.id) orelse return false;
            if (declaration.product) |fields| for (fields) |field| if (mentionsFunctionIn(field.type, seen)) return true;
            for (declaration.variants) |variant| for (variant.fields) |field| if (mentionsFunctionIn(field.type, seen)) return true;
            return false;
        },
        else => false,
    };
}

test "runner dispatch skips records whose fields hold task handles, including recursive ones" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    a = arena.allocator();
    declarations = .init(a);
    const int = newType(.{ .primitive = "primitiveInt64" });
    const handle = newType(.{ .builtin = .{ .kind = "oneShot", .args = try a.dupe(*const Type, &.{int}) } });
    const node = newType(.{ .named = .{ .id = "Node", .args = &.{} } });
    const holder = newType(.{ .named = .{ .id = "Holder", .args = &.{} } });
    try declarations.put("Node", .{ .id = "Node", .type_parameters = &.{}, .product = try a.dupe(Field, &.{ .{ .id = "value", .type = int }, .{ .id = "next", .type = newType(.{ .builtin = .{ .kind = "option", .args = try a.dupe(*const Type, &.{node}) } }) } }), .variants = &.{} });
    try declarations.put("Holder", .{ .id = "Holder", .type_parameters = &.{}, .product = null, .variants = try a.dupe(Variant, &.{.{ .id = "held", .fields = try a.dupe(Field, &.{ .{ .id = "list", .type = node }, .{ .id = "reply", .type = handle } }) }}) });
    try std.testing.expect(!mentionsFunction(node));
    try std.testing.expect(mentionsFunction(holder));
}

fn runnable(fn_: Function) bool {
    if (fn_.type_parameters.len > 0) return false;
    var types: List(*const Type) = .empty;
    push(*const Type, &types, fn_.result);
    for (fn_.parameters) |parameter| push(*const Type, &types, parameter.value.type);
    for (types.items) |t| if (mentionsFunction(t)) return false;
    for (types.items) |t| if (zigType(t, false) == null) return false;
    return true;
}

/// `ZigBackend.fakeArgument`: the statements that make the fake for one capability parameter,
/// reading its scripts from the argument tokens in canonical operation order.
fn fakeArgument(out: *List(u8), capability_id: []const u8, name: []const u8, fake: usize, site: usize) usize {
    const capability = findCapability(capability_id).?;
    out.print(a, "        const {s} = {s}{{ .fake = {d}, .site = {d} }};\n", .{ name, identifier("C_", capability_id), fake, site }) catch oom();
    for (capability.operations) |operation| {
        out.print(a, "        {s}[{d}] = nyReadScript({s}, &tokens);\n", .{ identifier("ny_scripts_", operation.id), fake, zigType(operation.result, false).? }) catch oom();
    }
    return capability.operations.len;
}

/// The runner support and a `main` that dispatches `entry`, or every runnable function, decoding
/// each argument from the command line.
fn runnerMain(ordered: []const Function, entry: ?[]const u8) []const u8 {
    var out: List(u8) = .empty;
    out.appendSlice(a, runner_support) catch oom();
    out.appendSlice(a,
        \\
        \\pub fn main(init: std.process.Init) !void {
        \\    // The host kills the runner's process group, which misses it when the host itself is SIGKILLed.
        \\    if (@import("builtin").os.tag == .linux) _ = std.os.linux.prctl(@intFromEnum(std.os.linux.PR.SET_PDEATHSIG), @intFromEnum(std.os.linux.SIG.KILL), 0, 0, 0);
        \\    const args = try init.minimal.args.toSlice(init.arena.allocator());
        \\    const entry = args[1];
        \\    var tokens = NyTokens{ .items = args, .index = 2 };
        \\    _ = &tokens;
        \\    var buffer: [4096]u8 = undefined;
        \\    var writer = std.Io.File.stdout().writer(init.io, &buffer);
        \\    const out = &writer.interface;
        \\    ny_out = out;
        \\    ny_io = init.io;
        \\    ny_calls = .init(nyAllocator());
        \\    nyUseValueAllocator();
        \\    var value: std.Io.Writer.Allocating = .init(nyAllocator());
        \\
    ) catch oom();
    for (ordered) |fn_| {
        if (if (entry) |id| !eql(fn_.id, id) else !runnable(fn_)) continue;
        const name = instanceName(fn_.id, &.{});
        out.print(a, "    if (std.mem.eql(u8, entry, \"{s}\")) {{\n", .{name}) catch oom();
        // A capability parameter is a scripted fake; its call sites follow the earlier fakes'.
        var fake: usize = 0;
        var site: usize = 0;
        var names: List([]const u8) = .empty;
        for (fn_.parameters, 0..) |parameter, index| {
            const argument = std.fmt.allocPrint(a, "a{d}", .{index}) catch oom();
            push([]const u8, &names, argument);
            if (parameter.value.type.* == .capability) {
                site += fakeArgument(&out, parameter.value.type.capability, argument, fake, site);
                fake += 1;
            } else out.print(a, "        const {s} = nyDecode({s}, &tokens);\n", .{ argument, zigType(parameter.value.type, false).? }) catch oom();
        }
        out.print(a, "        const result = nyValue({s}({s}));\n        try nyEncode(&value.writer, result);\n        nyRelease(result);\n    }}\n", .{ name, join(", ", names.items) }) catch oom();
    }
    out.appendSlice(a,
        \\    try out.print("[\"value\",{s},[{s}]]\n", .{ value.written(), ny_calls.written() });
        \\    try out.flush();
        \\    nyLeakCheck();
        \\}
        \\
    ) catch oom();
    return out.items;
}

/// A function the library exports, and its C symbol.
pub const Export = struct { id: []const u8, symbol: []const u8 };
var library_exports: ?[]const Export = null;

fn abiScalar(t: *const Type) ?[]const u8 {
    if (t.* != .primitive) return null;
    if (eql(t.primitive, "primitiveInt64")) return "i64";
    if (eql(t.primitive, "primitiveFloat64")) return "f64";
    if (eql(t.primitive, "primitiveBool")) return "bool";
    return null;
}

/// The item of a List whose items are Int64 or Float64.
fn abiListItem(t: *const Type) ?[]const u8 {
    if (t.* != .builtin or !eql(t.builtin.kind, "list")) return null;
    const item = abiScalar(t.builtin.args[0]) orelse return null;
    return if (eql(item, "bool")) null else item;
}

fn abiRowsItem(t: *const Type) ?[]const u8 {
    if (t.* != .builtin or !eql(t.builtin.kind, "list")) return null;
    return abiListItem(t.builtin.args[0]);
}

fn isString(t: *const Type) bool {
    return t.* == .primitive and eql(t.primitive, "primitiveString");
}

/// The fields of a non-generic Product whose fields are all scalars.
fn abiProduct(t: *const Type) ?[]Field {
    if (t.* != .named or t.named.args.len > 0) return null;
    const fields = (declarations.get(t.named.id) orelse return null).product orelse return null;
    for (fields) |field| if (abiScalar(field.type) == null) return null;
    return fields;
}

fn libraryExports(exports: []const Export) []const u8 {
    var out: List(u8) = .empty;
    out.appendSlice(a, library_support) catch oom();
    // Exports take no capability, so functions that do are never analysed; Zig still resolves their names.
    for (capabilities) |capability| lineCat(&out, &.{ "const ", identifier("C_", capability.id), " = struct {};" });
    line(&out, "");
    for (exports) |item| {
        const fn_ = functions.get(item.id) orelse {
            problem(cat(&.{ "export of unknown function ", item.id }));
            continue;
        };
        const valid = item.symbol.len > 0 and !std.ascii.isDigit(item.symbol[0]) and for (item.symbol) |c| {
            if (!std.ascii.isAlphanumeric(c) and c != '_') break false;
        } else true;
        if (!valid) problem(cat(&.{ "export symbol ", item.symbol }));
        if (fn_.type_parameters.len > 0) problem(cat(&.{ "export of generic function ", item.symbol }));
        if (!valid or fn_.type_parameters.len > 0) continue;
        var parameters: List([]const u8) = .empty;
        var arguments: List([]const u8) = .empty;
        var doc: List([]const u8) = .empty;
        for (fn_.parameters, 0..) |parameter, index| {
            const t = parameter.value.type;
            const p = std.fmt.allocPrint(a, "a{d}", .{index}) catch oom();
            if (abiScalar(t)) |zig_type| {
                push([]const u8, &parameters, cat(&.{ p, ": ", zig_type }));
                push([]const u8, &arguments, p);
            } else if (isString(t)) {
                push([]const u8, &parameters, cat(&.{ p, ": ?[*]const u8, ", p, "_len: usize" }));
                push([]const u8, &arguments, cat(&.{ "nyText(", p, ", ", p, "_len) orelse return nyExportInvalid(\"", p, " is not UTF-8\")" }));
            } else if (abiListItem(t)) |zig_type| {
                push([]const u8, &parameters, cat(&.{ p, ": ?[*]const ", zig_type, ", ", p, "_len: usize" }));
                push([]const u8, &arguments, cat(&.{ "nySlice(", zig_type, ", ", p, ", ", p, "_len)" }));
            } else if (abiRowsItem(t)) |zig_type| {
                push([]const u8, &parameters, cat(&.{ p, ": ?[*]const ", zig_type, ", ", p, "_row_lens: ?[*]const usize, ", p, "_rows: usize" }));
                push([]const u8, &arguments, cat(&.{ "nyRows(", zig_type, ", ", p, ", ", p, "_row_lens, ", p, "_rows)" }));
            } else if (abiProduct(t)) |fields| {
                var inits: List([]const u8) = .empty;
                var names: List([]const u8) = .empty;
                for (fields) |field| {
                    const name = cat(&.{ p, "_", fieldName(field.id) });
                    push([]const u8, &parameters, cat(&.{ name, ": ", abiScalar(field.type).? }));
                    push([]const u8, &inits, cat(&.{ ".", fieldName(field.id), " = ", name }));
                    push([]const u8, &names, field.id);
                }
                push([]const u8, &doc, cat(&.{ p, " fields in this order: ", join(", ", names.items) }));
                push([]const u8, &arguments, cat(&.{ "nyBox(", typeName(t.named.id, &.{}), ", .{ ", join(", ", inits.items), " })" }));
            } else problem(cat(&.{ "export parameter type ", typeDisplay(t) }));
        }
        const r = fn_.result;
        const copy = if (abiScalar(r)) |zig_type| blk: {
            push([]const u8, &parameters, cat(&.{ "out: *", zig_type }));
            break :blk "    out.* = result;\n    return 0;\n";
        } else if (r.* == .primitive and eql(r.primitive, "primitiveUnit")) "    _ = result;\n    return 0;\n" else if (isString(r) or abiListItem(r) != null) blk: {
            const zig_type = if (isString(r)) "u8" else abiListItem(r).?;
            push([]const u8, &parameters, cat(&.{ "out: ?[*]", zig_type, ", out_cap: usize, out_len: *usize" }));
            break :blk cat(&.{ "    return if (nyCopyOut(", zig_type, ", result, out, out_cap, out_len)) 0 else 3;\n" });
        } else if (abiRowsItem(r)) |zig_type| blk: {
            push([]const u8, &parameters, cat(&.{ "out: ?[*]", zig_type, ", out_cap: usize, out_len: *usize, out_row_lens: ?[*]usize, out_row_cap: usize, out_rows: *usize" }));
            break :blk cat(&.{ "    return if (nyCopyRows(", zig_type, ", result, out, out_cap, out_len, out_row_lens, out_row_cap, out_rows)) 0 else 3;\n" });
        } else blk: {
            problem(cat(&.{ "export result type ", typeDisplay(r) }));
            break :blk "";
        };
        for (doc.items) |text| lineCat(&out, &.{ "/// ", text });
        lineCat(&out, &.{ "export fn ", item.symbol, "(", join(", ", parameters.items), ") c_int {" });
        line(&out, "    nyExportEnter();");
        line(&out, "    defer nyExportLeave();");
        const fail = if (failing(fn_.id)) " catch return nyExportFail()" else "";
        lineCat(&out, &.{ "    const result = ", instanceName(fn_.id, &.{}), "(", join(", ", arguments.items), ")", fail, ";" });
        out.appendSlice(a, copy) catch oom();
        line(&out, "}");
        line(&out, "");
    }
    return out.items;
}

fn functionKey(item: Function) []const u8 {
    return item.id;
}

fn capabilityKey(item: Capability) []const u8 {
    return item.id;
}

fn operationKey(item: Operation) []const u8 {
    return item.id;
}

fn variantDeclKey(item: Variant) []const u8 {
    return item.id;
}

/// The emitter's whole output for the IR `text`, or null with `problems` set.
fn emitAll(text: []const u8, entry: ?[]const u8) !?[]const u8 {
    const root = (try parseJson(text)).object;

    declarations = .init(a);
    for (arr(root, "types")) |item| {
        const o = item.object;
        const is_product = eql(str(o, "kind"), "product");
        var declaration = Declaration{ .id = str(o, "id"), .type_parameters = parseTypeParameters(o), .product = null, .variants = &.{} };
        if (is_product) declaration.product = parseFields(arr(o, "fields")) else {
            const variants = arr(o, "variants");
            declaration.variants = a.alloc(Variant, variants.len) catch oom();
            for (variants, 0..) |variant, at| declaration.variants[at] = .{ .id = str(variant.object, "id"), .fields = parseFields(arr(variant.object, "fields")) };
            byId(Variant, declaration.variants, variantDeclKey);
        }
        try declarations.put(declaration.id, declaration);
    }
    const capability_items = arr(root, "capabilities");
    capabilities = try a.alloc(Capability, capability_items.len);
    for (capability_items, 0..) |item, at| {
        const operations = arr(item.object, "operations");
        capabilities[at] = .{ .id = str(item.object, "id"), .operations = try a.alloc(Operation, operations.len) };
        for (operations, 0..) |operation, index| {
            capabilities[at].operations[index] = .{
                .id = str(operation.object, "id"),
                .parameters = blk: {
                    // Operation parameters keep their declared order.
                    const parameters = arr(operation.object, "parameters");
                    const out = try a.alloc(Field, parameters.len);
                    for (parameters, 0..) |parameter, p| out[p] = .{ .id = str(parameter.object, "id"), .type = parseType(parameter.object.get("type").?) };
                    break :blk out;
                },
                .result = parseType(operation.object.get("resultType").?),
            };
        }
        byId(Operation, capabilities[at].operations, operationKey);
    }
    byId(Capability, capabilities, capabilityKey);
    const function_items = arr(root, "functions");
    const ordered = try a.alloc(Function, function_items.len);
    for (function_items, 0..) |item, at| ordered[at] = parseFunction(item);
    byId(Function, ordered, functionKey);
    functions = .init(a);
    for (ordered) |fn_| try functions.put(fn_.id, fn_);
    type_instances = .init(a);
    function_instances = .init(a);
    computeMayFail();
    const source = emit(ordered) orelse return null;
    if (library_exports) |exports| {
        const tail = libraryExports(exports);
        if (problems.items.len > 0) return null;
        return std.mem.concat(a, u8, &.{ source, tail }) catch oom();
    }
    const fakes = fakeCapabilities() orelse return null;
    return std.mem.concat(a, u8, &.{ source, fakes, runnerMain(ordered, entry) }) catch oom();
}

/// The host uses the same emitter as the command-line tool. Every mutable table belongs to
/// this invocation's allocator; no request-arena reference survives the next call.
pub const Runner = union(enum) { source: []const u8, unsupported: []const []const u8 };
pub fn runner(allocator: std.mem.Allocator, text: []const u8, entry: ?[]const u8) !Runner {
    library_exports = null;
    return emitRequest(allocator, text, entry);
}

/// Library mode (docs/decisions/zig-backend-memory-ownership.md, first milestone): the program without
/// the runner `main`, plus one C export per entry. runPure never takes this path, so the runner output
/// the emitter check compares is unchanged.
pub fn library(allocator: std.mem.Allocator, text: []const u8, exports: []const Export) !Runner {
    library_exports = exports;
    defer library_exports = null;
    return emitRequest(allocator, text, null);
}

fn emitRequest(allocator: std.mem.Allocator, text: []const u8, entry: ?[]const u8) !Runner {
    a = allocator;
    value_names = .empty;
    block_names = .empty;
    problems = .empty;
    type_order = .empty;
    pending = .empty;
    if (try emitAll(text, entry)) |source| return .{ .source = source };
    var unique: List([]const u8) = .empty;
    var seen = std.StringHashMap(void).init(a);
    for (problems.items) |item| {
        if ((try seen.getOrPut(item)).found_existing) continue;
        try unique.append(a, item);
    }
    return .{ .unsupported = unique.items };
}

pub fn main(init: std.process.Init) !void {
    a = init.arena.allocator();
    const args = try init.minimal.args.toSlice(a);
    if (args.len == 4 and std.mem.eql(u8, args[1], "--bench")) return bench(init.io, try std.fmt.parseInt(usize, args[2], 10), args[3]);
    if (args.len != 2 and args.len != 3) {
        std.debug.print("usage: emitter <execution-ir.json> [entry-function-id] | emitter --bench <runs> <execution-ir.json>\n", .{});
        std.process.exit(64);
    }
    const text = try std.Io.Dir.cwd().readFileAlloc(init.io, args[1], a, .unlimited);
    var buffer: [65536]u8 = undefined;
    var writer = std.Io.File.stdout().writer(init.io, &buffer);
    const out = &writer.interface;
    if (try emitAll(text, if (args.len == 3) args[2] else null)) |program| {
        try out.writeAll(program);
        try out.flush();
        return;
    }
    var seen = std.StringHashMap(void).init(a);
    for (problems.items) |item| {
        if (seen.contains(item)) continue;
        try seen.put(item, {});
        try std.json.Stringify.value(item, .{}, out);
        try out.writeByte('\n');
    }
    try out.flush();
    std.process.exit(2);
}

/// `--bench <runs> <ir>`: the median of `runs` in-process runs after `runs` warm-up runs, in ms, on stderr,
/// with the IR read once; each run starts from a fresh arena and fresh state, and must give the first run's output.
fn bench(io: std.Io, runs: usize, path: []const u8) !void {
    const text = try std.Io.Dir.cwd().readFileAlloc(io, path, std.heap.page_allocator, .unlimited);
    var first: ?[]const u8 = null;
    const times = try std.heap.page_allocator.alloc(i96, runs);
    for (0..2 * runs) |run| {
        var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
        defer arena.deinit();
        a = arena.allocator();
        value_names = .empty;
        block_names = .empty;
        problems = .empty;
        type_order = .empty;
        pending = .empty;
        const start = std.Io.Timestamp.now(io, .awake);
        const output = (try emitAll(text, null)) orelse try std.mem.join(a, "\n", problems.items);
        const took = start.durationTo(std.Io.Timestamp.now(io, .awake)).toNanoseconds();
        if (first) |expected| {
            if (!std.mem.eql(u8, expected, output)) @panic("a bench run gave another output");
        } else first = try std.heap.page_allocator.dupe(u8, output);
        if (run >= runs) times[run - runs] = took;
    }
    std.mem.sort(i96, times, {}, std.sort.asc(i96));
    std.debug.print("{d:.2}\n", .{@as(f64, @floatFromInt(times[runs / 2])) / 1e6});
}

// ---------------------------------------------------------------- JSON

/// The IR as `std.json.Value`, parsed directly: objects get their exact capacity and strings
/// without escapes stay slices of the input, which `parseFromSliceLeaky` does not do for maps.
const max_json_depth = 1000;

const Reader = struct {
    s: []const u8,
    i: usize = 0,
    scratch: List(json.Value) = .empty,
    names: List([]const u8) = .empty,
    depth: usize = 0,

    fn space(r: *Reader) void {
        while (r.i < r.s.len and (r.s[r.i] == ' ' or r.s[r.i] == '\t' or r.s[r.i] == '\n' or r.s[r.i] == '\r')) r.i += 1;
    }

    fn expect(r: *Reader, c: u8) !void {
        r.space();
        if (r.i >= r.s.len or r.s[r.i] != c) return error.SyntaxError;
        r.i += 1;
    }

    fn peek(r: *Reader) !u8 {
        r.space();
        if (r.i >= r.s.len) return error.UnexpectedEndOfInput;
        return r.s[r.i];
    }

    fn value(r: *Reader) anyerror!json.Value {
        switch (try r.peek()) {
            '{' => {
                r.i += 1;
                if (r.depth == max_json_depth) return error.SyntaxError;
                r.depth += 1;
                defer r.depth -= 1;
                const base = r.scratch.items.len;
                if (try r.peek() == '}') r.i += 1 else while (true) {
                    if (try r.peek() != '"') return error.SyntaxError;
                    push([]const u8, &r.names, try r.string());
                    try r.expect(':');
                    const item = try r.value();
                    push(json.Value, &r.scratch, item);
                    const c = try r.peek();
                    r.i += 1;
                    if (c == '}') break;
                    if (c != ',') return error.SyntaxError;
                }
                var map: json.ObjectMap = .empty;
                const count = r.scratch.items.len - base;
                map.ensureTotalCapacity(a, count) catch oom();
                const names = r.names.items[r.names.items.len - count ..];
                for (names, r.scratch.items[base..]) |name, item| {
                    const entry = map.getOrPutAssumeCapacity(name);
                    if (entry.found_existing) return error.DuplicateField;
                    entry.value_ptr.* = item;
                }
                r.scratch.shrinkRetainingCapacity(base);
                r.names.shrinkRetainingCapacity(r.names.items.len - count);
                return .{ .object = map };
            },
            '[' => {
                r.i += 1;
                if (r.depth == max_json_depth) return error.SyntaxError;
                r.depth += 1;
                defer r.depth -= 1;
                const base = r.scratch.items.len;
                if (try r.peek() == ']') r.i += 1 else while (true) {
                    const item = try r.value();
                    push(json.Value, &r.scratch, item);
                    const c = try r.peek();
                    r.i += 1;
                    if (c == ']') break;
                    if (c != ',') return error.SyntaxError;
                }
                var array = json.Array.init(a);
                array.appendSlice(r.scratch.items[base..]) catch oom();
                r.scratch.shrinkRetainingCapacity(base);
                return .{ .array = array };
            },
            '"' => return .{ .string = try r.string() },
            't' => return r.literal("true", .{ .bool = true }),
            'f' => return r.literal("false", .{ .bool = false }),
            'n' => return r.literal("null", .null),
            '-', '0'...'9' => {
                const start = r.i;
                if (r.s[r.i] == '-') r.i += 1;
                if (r.i < r.s.len and r.s[r.i] == '0') r.i += 1 else try r.digits();
                if (r.i < r.s.len and r.s[r.i] == '.') {
                    r.i += 1;
                    try r.digits();
                }
                if (r.i < r.s.len and (r.s[r.i] == 'e' or r.s[r.i] == 'E')) {
                    r.i += 1;
                    if (r.i < r.s.len and (r.s[r.i] == '+' or r.s[r.i] == '-')) r.i += 1;
                    try r.digits();
                }
                return json.Value.parseFromNumberSlice(r.s[start..r.i]);
            },
            else => return error.SyntaxError,
        }
    }

    fn digits(r: *Reader) !void {
        if (!(r.i < r.s.len and std.ascii.isDigit(r.s[r.i]))) return error.SyntaxError;
        while (r.i < r.s.len and std.ascii.isDigit(r.s[r.i])) r.i += 1;
    }

    fn literal(r: *Reader, word: []const u8, result: json.Value) !json.Value {
        if (!std.mem.startsWith(u8, r.s[r.i..], word)) return error.SyntaxError;
        r.i += word.len;
        return result;
    }

    fn string(r: *Reader) ![]const u8 {
        r.i += 1;
        const start = r.i;
        while (r.i < r.s.len) : (r.i += 1) {
            const c = r.s[r.i];
            if (c == '"') {
                r.i += 1;
                return r.s[start .. r.i - 1];
            }
            if (c == '\\' or c < 0x20) break;
        } else return error.UnexpectedEndOfInput;
        // With escapes: the whole string through std.json, which decodes them.
        var end = r.i;
        while (end < r.s.len and r.s[end] != '"') end += if (r.s[end] == '\\') 2 else 1;
        if (end >= r.s.len) return error.UnexpectedEndOfInput;
        r.i = end + 1;
        return try json.parseFromSliceLeaky([]const u8, a, r.s[start - 1 .. r.i], .{});
    }
};

fn parseJson(source: []const u8) !json.Value {
    // Strings without escapes stay unchecked slices, so the whole input is checked once here.
    if (!std.unicode.utf8ValidateSlice(source)) return error.InvalidUtf8;
    var reader: Reader = .{ .s = source };
    const root = try reader.value();
    reader.space();
    if (reader.i != source.len) return error.SyntaxError;
    return root;
}

test "malformed literals and over-deep JSON are reported, not panicked on or pasted" {
    var arena = std.heap.ArenaAllocator.init(std.testing.allocator);
    defer arena.deinit();
    a = arena.allocator();
    problems = .empty;
    var body = Body{ .values = .init(a) };
    for ([_][]const u8{ "{\"value\":1.0}", "{\"value\":99999999999999999999}" }) |text| {
        body.o = (try parseJson(text)).object;
        try std.testing.expectEqualStrings("undefined", instructionExpression(&body, .int64Literal));
    }
    body.o = (try parseJson("{\"value\":0}")).object;
    try std.testing.expectEqualStrings("undefined", instructionExpression(&body, .boolLiteral));
    try std.testing.expectEqual(3, problems.items.len);
    problems = .empty;
    const open: [1001]u8 = @splat('[');
    const close: [1001]u8 = @splat(']');
    const nested = open ++ close;
    try std.testing.expectError(error.SyntaxError, parseJson(&nested));
    _ = try parseJson(nested[1..2001]);
}
