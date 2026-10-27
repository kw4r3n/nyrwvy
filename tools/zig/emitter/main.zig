//! The Nyrwvy Zig backend emitter: reads one Execution IR JSON file (the form
//! ExecutionIrSerializer writes) and prints the Zig source the F# `ZigBackend.emit` and
//! `ZigBackend.fakeCapabilities` produce for it. Exit 0 prints the source; exit 2 prints the
//! unsupported constructs, one per line. See docs/decisions/zig-tools.md.
//!
//! `runtime.zig` and `case_mapping.zig` beside this file are text the emitter copies into its
//! output, not modules of the emitter.
const std = @import("std");
const json = std.json;

const runtime = @embedFile("runtime.zig");
const case_mapping = @embedFile("case_mapping.zig");

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

/// `prefix` and then `raw` with every UTF-16 unit that is not an ASCII letter or digit as `_`.
fn identifier(prefix: []const u8, raw: []const u8) []const u8 {
    var out: List(u8) = .empty;
    out.appendSlice(a, prefix) catch oom();
    var view = std.unicode.Utf8View.initUnchecked(raw);
    var points = view.iterator();
    while (points.nextCodepoint()) |point| {
        if (point < 0x80 and std.ascii.isAlphanumeric(@intCast(point))) push(u8, &out, @intCast(point)) else {
            push(u8, &out, '_');
            if (point >= 0x10000) push(u8, &out, '_');
        }
    }
    return out.items;
}

fn v(id: []const u8) []const u8 {
    return identifier("v_", id);
}

fn blockName(id: []const u8) []const u8 {
    return identifier("b_", id);
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

/// A short stable suffix for one list of type arguments: the hash ZigBackend.instanceSuffix takes.
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
            return std.mem.order(u8, key(left), key(right)) == .lt;
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
        problem(cat(&.{ "type ", typeJson(t) }));
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
    /// declaration order.
    fn callback(self: *const Body, function_key: []const u8, context_key: []const u8, roles: []const Role, result: *const Type) []const u8 {
        const function_id = str(self.o, function_key);
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
                push([]const u8, &expressions, v(value_id));
                push(Field, &args, .{ .id = parameter.id, .type = self.valueType(value_id) });
            } else {
                push([]const u8, &expressions, remaining[0].expression);
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
        .boolLiteral => if (b.o.get("value").?.bool) "true" else "false",
        .unitLiteral => "NyUnit{}",
        .int64Literal => switch (b.o.get("value").?) {
            .integer => |value| std.fmt.allocPrint(a, "{d}", .{value}) catch oom(),
            .number_string => |text| text,
            else => @panic("int64Literal value"),
        },
        .stringLiteral => stringLiteral(str(b.o, "value")),
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
        .stringConcat => pair(b, "nyConcat"),
        .valueEqual => pair(b, "nyEqual"),
        .productConstruct => cat(&.{ "nyBox(", b.resultTypeName(), ", ", fieldList(arr(b.o, "fields")), ")" }),
        .fieldAccess => cat(&.{ b.f("sourceValueId"), ".", fieldName(str(b.o, "fieldId")) }),
        .productUpdate => blk: {
            const fields = a.dupe(json.Value, arr(b.o, "fields")) catch oom();
            byId(json.Value, fields, targetFieldKey);
            var updates: List([]const u8) = .empty;
            for (fields) |field| push([]const u8, &updates, cat(&.{ " copy.", fieldName(str(field.object, "targetFieldId")), " = ", v(str(field.object, "valueId")), ";" }));
            break :blk labeled(cat(&.{ "var copy = ", b.f("sourceValueId"), ".*;", cat(updates.items), " break :blk nyBox(", b.resultTypeName(), ", copy);" }));
        },
        .unionConstruct => cat(&.{ "nyBox(", b.resultTypeName(), ", .{ .", variantName(str(b.o, "variantId")), " = ", fieldList(arr(b.o, "fields")), " })" }),
        .optionNone => "null",
        .optionSome => b.f("valueId"),
        .resultOk => cat(&.{ ".{ .ok = ", b.f("valueId"), " }" }),
        .resultErr => cat(&.{ ".{ .err = ", b.f("errorValueId"), " }" }),
        .extractUnionField => cat(&.{ b.f("inputValueId"), ".", variantName(str(b.o, "variantId")), ".", fieldName(str(b.o, "fieldId")) }),
        .extractOptionSome => cat(&.{ b.f("inputValueId"), ".?" }),
        .extractResultOk => cat(&.{ b.f("inputValueId"), ".ok" }),
        .extractResultErr => cat(&.{ b.f("inputValueId"), ".err" }),
        .optionIsSome => cat(&.{ b.f("valueId"), " != null" }),
        .resultIsOk => cat(&.{ b.f("valueId"), " == .ok" }),
        .optionGetOrDefault => cat(&.{ b.f("optionValueId"), " orelse ", b.f("defaultValueId") }),
        .resultGetOrDefault => cat(&.{ "switch (", b.f("resultValueId"), ") { .ok => |item| item, .err => ", b.f("defaultValueId"), " }" }),
        .listEmpty => "&.{}",
        .listConstruct => blk: {
            var items: List([]const u8) = .empty;
            for (arr(b.o, "itemValueIds")) |item| push([]const u8, &items, v(item.string));
            const element = b.resultElement();
            break :blk cat(&.{ "nyListOf(", element, ", &[_]", b.resultElement(), "{ ", join(", ", items.items), " })" });
        },
        .listRange => wrap(b, "nyListRange", &.{ "startValueId", "endValueId" }),
        .listLength => cat(&.{ "@as(i64, @intCast(", b.f("listValueId"), ".len))" }),
        .listAppendItem => b.listCall("nyListAppend", "listValueId", &.{b.f("itemValueId")}),
        .listConcat => b.listCall("nyListConcat", "leftListValueId", &.{b.f("rightListValueId")}),
        .listReverse => b.listCall("nyListReverse", "listValueId", &.{}),
        .listItemAt => b.listCall("nyListItemAt", "listValueId", &.{b.f("indexValueId")}),
        .listLast => b.listCall("nyListLast", "listValueId", &.{}),
        .listDropLast => b.listCall("nyListDropLast", "listValueId", &.{}),
        .listSlice => b.listCall("nyListSlice", "listValueId", &.{ b.f("startId"), b.f("endId") }),
        .listSetAt => b.listCall("nyListSetAt", "listValueId", &.{ b.f("indexValueId"), b.f("itemValueId") }),
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
                .listFilter => cat(&.{ "const out = nyAlloc(", b.elementOf("sourceListValueId"), ", ", source, ".len); var count: usize = 0; for (", source, ") |item| { if (", text, ") { out[count] = item; count += 1; } } break :blk out[0..count];" }),
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
            break :blk labeled(cat(&.{ "var out: std.ArrayList(", b.resultElement(), ") = .empty; for (", b.f("sourceListValueId"), ") |item| out.appendSlice(nyAllocator(), ", text, ") catch @panic(\"out of memory\"); break :blk out.items;" }));
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
        .setEmpty => ".{ .items = &.{} }",
        .mapEmpty => ".{ .keys = &.{}, .values = &.{} }",
        .setFromList => b.listCall("nySetFromList", "listValueId", &.{}),
        .setToList => cat(&.{ b.f("setValueId"), ".items" }),
        .setSize => cat(&.{ "@as(i64, @intCast(", b.f("setValueId"), ".items.len))" }),
        .setInsert => b.listCall("nySetInsert", "setValueId", &.{b.f("itemValueId")}),
        .setRemove => b.listCall("nySetRemove", "setValueId", &.{b.f("itemValueId")}),
        .setContains => cat(&.{ "nyFind(", b.elementOf("setValueId"), ", ", b.f("setValueId"), ".items, ", b.f("itemValueId"), ") != null" }),
        .setUnion => b.listCall("nySetUnion", "leftSetValueId", &.{b.f("rightSetValueId")}),
        .setIntersection => b.listCall("nySetKeep", "leftSetValueId", &.{ b.f("rightSetValueId"), "true" }),
        .setDifference => b.listCall("nySetKeep", "leftSetValueId", &.{ b.f("rightSetValueId"), "false" }),
        .setIsSubset => b.listCall("nySetIsSubset", "leftSetValueId", &.{b.f("rightSetValueId")}),
        .setFilter => blk: {
            const text = b.callback("predicateFunctionId", "context", &.{role("item", itemType(b.typeAt("sourceSetValueId")))}, &bool_type);
            const source = b.f("sourceSetValueId");
            break :blk labeled(cat(&.{ "const out = nyAlloc(", b.elementOf("sourceSetValueId"), ", ", source, ".items.len); var count: usize = 0; for (", source, ".items) |item| { if (", text, ") { out[count] = item; count += 1; } } break :blk .{ .items = out[0..count] };" }));
        },
        .setMap => blk: {
            const text = b.callback("mapperFunctionId", "context", &.{role("item", itemType(b.typeAt("sourceSetValueId")))}, itemType(b.result));
            break :blk labeled(cat(&.{ "var out: ", zig(b.result), " = .{ .items = &.{} }; for (", b.f("sourceSetValueId"), ".items) |item| out = nySetInsert(", b.resultElement(), ", out, ", text, "); break :blk out;" }));
        },
        .mapSize => cat(&.{ "@as(i64, @intCast(", b.f("mapValueId"), ".keys.len))" }),
        .mapKeys => cat(&.{ b.f("mapValueId"), ".keys" }),
        .mapValues => cat(&.{ b.f("mapValueId"), ".values" }),
        .mapInsert => b.mapCall("nyMapInsert", "mapValueId", &.{ b.f("keyValueId"), b.f("valueValueId") }),
        .mapRemove => b.mapCall("nyMapRemove", "mapValueId", &.{b.f("keyValueId")}),
        .mapGet => b.mapCall("nyMapGet", "mapValueId", &.{b.f("keyValueId")}),
        .mapGetOrDefault => blk: {
            const types = b.mapTypes("mapValueId");
            break :blk cat(&.{ "nyMapGet(", zig(types[0]), ", ", zig(types[1]), ", ", b.f("mapValueId"), ", ", b.f("keyValueId"), ") orelse ", b.f("defaultValueId") });
        },
        .mapContainsKey => cat(&.{ "nyFind(", zig(b.mapTypes("mapValueId")[0]), ", ", b.f("mapValueId"), ".keys, ", b.f("keyValueId"), ") != null" }),
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
            break :blk labeled(cat(&.{ "var out: ", zig(b.result), " = .{ .keys = &.{}, .values = &.{} }; for (", source, ".keys, ", source, ".values) |key, value| { if (", text, ") out = nyMapInsert(", zig(types[0]), ", ", zig(types[1]), ", out, key, value); } break :blk out;" }));
        },
        .mapMapValues => blk: {
            const types = b.mapTypes("sourceMapValueId");
            const result_value = if (b.result.* == .builtin and b.result.builtin.args.len == 2) b.result.builtin.args[1] else b.result;
            const text = b.callback("mapperFunctionId", "context", &.{ role("key", types[0]), role("value", types[1]) }, result_value);
            const source = b.f("sourceMapValueId");
            break :blk labeled(cat(&.{ "const out = nyAlloc(", zig(result_value), ", ", source, ".keys.len); for (", source, ".keys, ", source, ".values, 0..) |key, value, at| out[at] = ", text, "; break :blk .{ .keys = ", source, ".keys, .values = out };" }));
        },
        .mapFromList => blk: {
            const key_type = if (b.result.* == .builtin and b.result.builtin.args.len == 2) b.result.builtin.args[0] else b.result;
            const element = itemType(b.typeAt("sourceListValueId"));
            const text = b.callback("keyOfFunctionId", "context", &.{role("item", element)}, key_type);
            break :blk labeled(cat(&.{ "var out: ", zig(b.result), " = .{ .keys = &.{}, .values = &.{} }; for (", b.f("sourceListValueId"), ") |item| out = nyMapInsert(", zig(key_type), ", ", zig(element), ", out, ", text, ", item); break :blk out;" }));
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
        .exceptionRethrow => cat(&.{ "try nyRethrow(", zig(parseType(b.o.get("resultType").?)), ", ", b.f("valueId"), ")" }),
        .exceptionMessage => cat(&.{ b.f("valueId"), ".message" }),
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
                cat(&.{ "const released = try ", release, "; if (outcome == .ok and released == .err) break :blk .{ .err = released.err }; break :blk outcome;" })
            else
                cat(&.{ "_ = try ", release, "; break :blk outcome;" });
            break :blk labeled(cat(&.{ "const acquired = ", b.f("acquireValueId"), "; if (acquired == .err) break :blk .{ .err = acquired.err }; const resource = acquired.ok; const outcome = ", guarded, "; ", finish }));
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
            break :blk labeled(cat(&.{ "const scope = nyScopeOpen(); const outcome = ", guarded, "; try nyScopeClose(scope, false); break :blk outcome;" }));
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
            break :blk labeled(cat(&.{ "const captured = ", travelled[0], "; break :blk nyStartTask(", zig(work), ", ", b.f("scopeValueId"), ", NyJob(@TypeOf(captured), ", result, ", struct { fn call(c: anytype) ", result, " { return ", attempt(worker, travelled[1]), "; } }.call){ .c = captured });" }));
        },
        .taskWait => blk: {
            const work = argumentOf(itemType(b.typeAt("tasksValueId")), 0);
            const all = if (eql(str(b.o, "mode"), "all")) "true" else "false";
            const limit = if (b.o.get("limitValueId")) |id| v(id.string) else "null";
            break :blk cat(&.{ "try nyTaskWait(", zig(work), ", ", b.f("tasksValueId"), ", ", all, ", ", limit, ")" });
        },
        .taskCancel => labeled(cat(&.{ b.f("taskValueId"), ".base.cancel.cancel(); break :blk NyUnit{};" })),
        .oneShotCreate => cat(&.{ "nyOneShotCreate(", zig(parseType(b.o.get("valueType").?)), ", ", b.f("scopeValueId"), ")" }),
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
        .stringTrim => cat(&.{ "nyStringTrimEnd(nyStringTrimStart(", b.f("valueId"), "))" }),
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

fn line(out: *List(u8), text: []const u8) void {
    out.appendSlice(a, text) catch oom();
    push(u8, out, '\n');
}

fn valueKey(item: Value) []const u8 {
    return item.id;
}

fn emitFunction(out: *List(u8), name: []const u8, fn_: Function, lines: []const []const u8) void {
    var parameters: List([]const u8) = .empty;
    for (fn_.parameters) |parameter| push([]const u8, &parameters, cat(&.{ identifier("p_", parameter.id), ": ", zigOf(parameter.value.type).? }));
    const failure = if (failing(fn_.id)) "NyError!" else "";
    line(out, cat(&.{ "fn ", name, "(", join(", ", parameters.items), ") ", failure, zigOf(fn_.result).?, " {" }));
    // Every value is a function-level variable, so blocks can assign it in any order; `_ = &x`
    // keeps Zig from rejecting a value that some path never reads.
    var values: List(Value) = .empty;
    outer: for (valuesOf(fn_)) |value| {
        for (values.items) |seen| if (eql(seen.id, value.id)) continue :outer;
        push(Value, &values, value);
    }
    byId(Value, values.items, valueKey);
    for (values.items) |value| {
        line(out, cat(&.{ "    var ", v(value.id), ": ", zigOf(value.type).?, " = undefined;" }));
        line(out, cat(&.{ "    _ = &", v(value.id), ";" }));
    }
    for (fn_.parameters) |parameter| line(out, cat(&.{ "    ", v(parameter.value.id), " = ", identifier("p_", parameter.id), ";" }));
    var names: List([]const u8) = .empty;
    for (fn_.blocks) |b| push([]const u8, &names, blockName(b.id));
    line(out, cat(&.{ "    const Block = enum { ", join(", ", names.items), " };" }));
    // Blocks become prongs of a labeled switch; a jump is `continue :body .target`. Zig rejects an
    // unused label, so a function whose blocks never jump gets none.
    var jumps = false;
    for (fn_.blocks) |b| {
        const kind = str(b.terminator, "kind");
        if (!eql(kind, "return") and !eql(kind, "abort")) jumps = true;
    }
    line(out, cat(&.{ "    ", if (jumps) "body: " else "", "switch (Block.", blockName(fn_.entry), ") {" }));
    var next: usize = 0;
    for (fn_.blocks) |b| {
        line(out, cat(&.{ "        .", blockName(b.id), " => {" }));
        for (b.instructions) |_| {
            line(out, cat(&.{ "            ", lines[next] }));
            next += 1;
        }
        const t = b.terminator;
        const kind = str(t, "kind");
        if (eql(kind, "return")) {
            line(out, cat(&.{ "            return ", v(str(t, "valueId")), ";" }));
        } else if (eql(kind, "abort")) {
            line(out, cat(&.{ "            return .{ .err = ", v(str(t, "errorValueId")), " };" }));
        } else if (eql(kind, "jump")) {
            var pairs: List([2][]const u8) = .empty;
            const arguments = a.dupe(json.Value, arr(t, "arguments")) catch oom();
            byId(json.Value, arguments, targetValueKey);
            for (arguments) |argument| push([2][]const u8, &pairs, .{ str(argument.object, "targetValueId"), str(argument.object, "valueId") });
            assignAll(out, pairs.items);
            line(out, cat(&.{ "            continue :body .", blockName(str(t, "blockId")), ";" }));
        } else if (eql(kind, "branch")) {
            line(out, cat(&.{ "            if (", v(str(t, "conditionValueId")), ") continue :body .", blockName(str(t, "whenTrue")), ";" }));
            line(out, cat(&.{ "            continue :body .", blockName(str(t, "whenFalse")), ";" }));
        } else if (eql(kind, "matchOption")) {
            line(out, cat(&.{ "            if (", v(str(t, "inputValueId")), " != null) continue :body .", blockName(str(t, "someBlock")), ";" }));
            line(out, cat(&.{ "            continue :body .", blockName(str(t, "noneBlock")), ";" }));
        } else if (eql(kind, "matchResult")) {
            line(out, cat(&.{ "            if (", v(str(t, "inputValueId")), " == .ok) continue :body .", blockName(str(t, "okBlock")), ";" }));
            line(out, cat(&.{ "            continue :body .", blockName(str(t, "errBlock")), ";" }));
        } else if (eql(kind, "matchUnion")) {
            line(out, cat(&.{ "            switch (", v(str(t, "inputValueId")), ".*) {" }));
            const targets = a.dupe(json.Value, arr(t, "targets")) catch oom();
            byId(json.Value, targets, variantKey);
            for (targets) |target| line(out, cat(&.{ "                .", variantName(str(target.object, "variantId")), " => continue :body .", blockName(str(target.object, "blockId")), "," }));
            line(out, "            }");
        } else if (eql(kind, "tailCall")) {
            var pairs: List([2][]const u8) = .empty;
            for (fn_.parameters) |parameter| {
                for (arr(t, "arguments")) |argument| {
                    if (eql(str(argument.object, "targetParameterId"), parameter.id)) push([2][]const u8, &pairs, .{ parameter.value.id, str(argument.object, "valueId") });
                }
            }
            assignAll(out, pairs.items);
            line(out, cat(&.{ "            continue :body .", blockName(fn_.entry), ";" }));
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
        line(out, cat(&.{ "const ", name, " = struct { ", fieldTypes(&map, fields), " };" }));
    } else {
        line(out, cat(&.{ "const ", name, " = union(enum) {" }));
        var ids: List([]const u8) = .empty;
        for (declaration.variants) |variant| {
            line(out, cat(&.{ "    ", variantName(variant.id), ": struct { ", fieldTypes(&map, variant.fields), " }," }));
            push([]const u8, &ids, cat(&.{ "\"", variant.id, "\"" }));
        }
        line(out, cat(&.{ "    pub const ny_ids = [_][]const u8{ ", join(", ", ids.items), " };" }));
        line(out, "};");
    }
    line(out, "");
}

fn fieldTypes(map: *const Bindings, fields: []const Field) []const u8 {
    var parts: List([]const u8) = .empty;
    for (fields) |field| push([]const u8, &parts, cat(&.{ fieldName(field.id), ": ", zig(substitute(map, field.type)) }));
    return join(", ", parts.items);
}

fn computeMayFail() void {
    may_fail = .init(a);
    var it = functions.valueIterator();
    while (it.next()) |fn_| {
        for (fn_.blocks) |b| for (b.instructions) |instruction| {
            const kind = str(instruction.o, "kind");
            for ([_][]const u8{ "capabilityInvoke", "exceptionRethrow", "useResource", "taskScope", "taskWait", "oneShotWait", "parallelAll" }) |direct| {
                if (eql(kind, direct)) may_fail.put(fn_.id, {}) catch oom();
            }
        };
    }
    const callees = [_][2][]const u8{ .{ "directCall", "functionId" }, .{ "exceptionCatch", "handlerFunctionId" }, .{ "cancellationShield", "bodyFunctionId" }, .{ "listEffectFold", "folderFunctionId" } };
    var changed = true;
    while (changed) {
        changed = false;
        var each = functions.valueIterator();
        while (each.next()) |fn_| {
            if (may_fail.contains(fn_.id)) continue;
            for (fn_.blocks) |b| for (b.instructions) |instruction| for (callees) |callee| {
                if (eql(str(instruction.o, "kind"), callee[0]) and may_fail.contains(str(instruction.o, callee[1]))) {
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
        if (problems.items.len == 0) emitFunction(&bodies, instanceName(instance.id, instance.args), fn_, lines.items);
    }
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
    for (capabilities) |capability| for (capability.operations) |operation| {
        var types: List(*const Type) = .empty;
        push(*const Type, &types, operation.result);
        for (operation.parameters) |parameter| push(*const Type, &types, parameter.type);
        for (types.items) |t| {
            if (t.* != .capability and zigType(t, false) != null) continue;
            const text = cat(&.{ "capability operation type ", typeJson(t) });
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
            line(&out, cat(&.{ "var ", identifier("ny_scripts_", operation.id), ": [16]NyScript(", zigType(operation.result, false).?, ") = undefined;" }));
        }
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

pub fn main(init: std.process.Init) !void {
    a = init.arena.allocator();
    const args = try init.minimal.args.toSlice(a);
    if (args.len != 2) {
        std.debug.print("usage: emitter <execution-ir.json>\n", .{});
        std.process.exit(64);
    }
    const text = try std.Io.Dir.cwd().readFileAlloc(init.io, args[1], a, .unlimited);
    const root = (try json.parseFromSliceLeaky(json.Value, a, text, .{})).object;

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

    var buffer: [65536]u8 = undefined;
    var writer = std.Io.File.stdout().writer(init.io, &buffer);
    const out = &writer.interface;
    const source = emit(ordered);
    const fakes = if (source != null) fakeCapabilities() else null;
    if (fakes) |text_fakes| {
        try out.writeAll(source.?);
        try out.writeAll(text_fakes);
        try out.flush();
        return;
    }
    var seen = std.StringHashMap(void).init(a);
    for (problems.items) |item| {
        if (seen.contains(item)) continue;
        try seen.put(item, {});
        try out.print("{s}\n", .{item});
    }
    try out.flush();
    std.process.exit(2);
}
