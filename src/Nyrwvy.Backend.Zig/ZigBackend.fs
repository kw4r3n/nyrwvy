namespace Nyrwvy.Core

open System
open System.Text

/// Emits Zig source from Execution IR. The supported slice covers Int64, Bool, Unit and String
/// values, Option, Result and monomorphic Product and Union types, their operations, direct calls
/// and the block terminators over them; anything else is reported as unsupported instead of
/// emitted.
[<RequireQualifiedAccess>]
module ZigBackend =
    let private identifier (prefix: string) (raw: string) =
        let builder = StringBuilder(prefix)
        for c in raw do builder.Append(if Char.IsAsciiLetterOrDigit c then c else '_') |> ignore
        builder.ToString()

    let functionName (functionId: FunctionId) = identifier "f_" (StableId.value functionId)
    let private valueName (valueId: ExecutionValueId) = identifier "v_" (ExecutionValueId.value valueId)
    let private blockName (blockId: ExecutionBlockId) = identifier "b_" (ExecutionBlockId.value blockId)
    let private parameterName (parameterId: ParameterId) = identifier "p_" (StableId.value parameterId)
    let private typeName (typeId: TypeId) = identifier "T_" (StableId.value typeId)
    let private fieldName (fieldId: FieldId) = identifier "f_" (StableId.value fieldId)
    let private variantName (variantId: VariantId) = identifier "c_" (StableId.value variantId)

    /// The Zig type of a supported value type. Products and Unions are boxed (`*const`), which keeps
    /// recursive types finite; generic declared types are not supported yet.
    let rec typeOf (types: Map<TypeId, ExecutionType>) (typeRef: TypeRef) : string option =
        match typeRef with
        | TypeRef.Primitive PrimitiveType.Int64 -> Some "i64"
        | TypeRef.Primitive PrimitiveType.Bool -> Some "bool"
        | TypeRef.Primitive PrimitiveType.Unit -> Some "NyUnit"
        | TypeRef.Primitive PrimitiveType.String -> Some "[]const u8"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ]) -> typeOf types item |> Option.map (fun item -> "?" + item)
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ ok; error ]) ->
            match typeOf types ok, typeOf types error with
            | Some ok, Some error -> Some("NyResult(" + ok + ", " + error + ")")
            | _ -> None
        | TypeRef.Named(typeId, []) when types |> Map.tryFind typeId |> Option.exists (fun item -> List.isEmpty item.TypeParameters) ->
            Some("*const " + typeName typeId)
        | _ -> None

    let private stringLiteral (text: string) =
        let builder = StringBuilder("\"")
        for b in Encoding.UTF8.GetBytes text do
            if b >= 0x20uy && b < 0x7Fuy && b <> byte '"' && b <> byte '\\' then builder.Append(char b) |> ignore
            else builder.Append("\\x").Append(b.ToString("x2")) |> ignore
        builder.Append('"').ToString()

    let private fieldList (fields: ExecutionFieldValue list) =
        fields
        |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
        |> List.map (fun field -> "." + fieldName field.TargetFieldId + " = " + valueName field.ValueId)
        |> fun items -> ".{ " + String.Join(", ", items) + " }"

    /// One line per instruction, or None when the instruction is outside the supported slice.
    let private instructionLine
        (parametersOf: FunctionId -> ParameterId list)
        (instruction: ExecutionInstruction)
        : string option =
        let target = valueName instruction.Result.Id
        let v = valueName
        let assign (expression: string) = Some(target + " = " + expression + ";")
        let binary left operator right = assign (v left + " " + operator + " " + v right)
        let resultTypeName () =
            match instruction.Result.Type with
            | TypeRef.Named(typeId, _) -> typeName typeId
            | other -> invalidOp $"Expected a declared type, got {other}."
        match instruction.Kind with
        | ExecutionInstructionKind.ReadParameter source
        | ExecutionInstructionKind.ReadPatternBinding source -> assign (v source)
        | ExecutionInstructionKind.BoolLiteral value -> assign (if value then "true" else "false")
        | ExecutionInstructionKind.UnitLiteral -> assign "NyUnit{}"
        | ExecutionInstructionKind.Int64Literal value -> assign (string value)
        | ExecutionInstructionKind.StringLiteral value -> assign (stringLiteral value)
        | ExecutionInstructionKind.BoolNot source -> assign ("!" + v source)
        | ExecutionInstructionKind.Int64WrappingAdd(left, right) -> binary left "+%" right
        | ExecutionInstructionKind.Int64WrappingSubtract(left, right) -> binary left "-%" right
        | ExecutionInstructionKind.Int64WrappingMultiply(left, right) -> binary left "*%" right
        // Floor division and modulo never trap: divisor 0 gives 0 (the dividend for modulo), -1 wraps.
        | ExecutionInstructionKind.Int64FloorDivide(left, right) -> assign ("nyFloorDivide(" + v left + ", " + v right + ")")
        | ExecutionInstructionKind.Int64FloorModulo(left, right) -> assign ("nyFloorModulo(" + v left + ", " + v right + ")")
        | ExecutionInstructionKind.Int64Min(left, right) -> assign ("@min(" + v left + ", " + v right + ")")
        | ExecutionInstructionKind.Int64Max(left, right) -> assign ("@max(" + v left + ", " + v right + ")")
        | ExecutionInstructionKind.Int64BitAnd(left, right) -> binary left "&" right
        | ExecutionInstructionKind.Int64BitOr(left, right) -> binary left "|" right
        | ExecutionInstructionKind.Int64BitXor(left, right) -> binary left "^" right
        | ExecutionInstructionKind.Int64BitNot source -> assign ("~" + v source)
        | ExecutionInstructionKind.Int64ShiftLeft(value, count) -> assign ("nyShiftLeft(" + v value + ", " + v count + ")")
        | ExecutionInstructionKind.Int64ShiftRight(value, count) -> assign ("nyShiftRight(" + v value + ", " + v count + ")")
        | ExecutionInstructionKind.Int64WrappingAbs source -> assign ("if (" + v source + " < 0) 0 -% " + v source + " else " + v source)
        | ExecutionInstructionKind.Int64Equal(left, right) -> binary left "==" right
        | ExecutionInstructionKind.Int64LessThan(left, right) -> binary left "<" right
        | ExecutionInstructionKind.Int64LessThanOrEqual(left, right) -> binary left "<=" right
        | ExecutionInstructionKind.Int64GreaterThan(left, right) -> binary left ">" right
        | ExecutionInstructionKind.Int64GreaterThanOrEqual(left, right) -> binary left ">=" right
        | ExecutionInstructionKind.Int64ToString source -> assign ("nyInt64ToString(" + v source + ")")
        | ExecutionInstructionKind.StringIsEmpty source -> assign (v source + ".len == 0")
        | ExecutionInstructionKind.StringLength source -> assign ("nyStringLength(" + v source + ")")
        | ExecutionInstructionKind.StringEqual(left, right) -> assign ("std.mem.eql(u8, " + v left + ", " + v right + ")")
        | ExecutionInstructionKind.StringConcat(left, right) -> assign ("nyConcat(" + v left + ", " + v right + ")")
        | ExecutionInstructionKind.ValueEqual(left, right) -> assign ("nyEqual(" + v left + ", " + v right + ")")
        | ExecutionInstructionKind.ProductConstruct fields -> assign ("nyBox(" + resultTypeName () + ", " + fieldList fields + ")")
        | ExecutionInstructionKind.FieldAccess(source, fieldId) -> assign (v source + "." + fieldName fieldId)
        | ExecutionInstructionKind.ProductUpdate(source, fields) ->
            let updates = fields |> List.map (fun field -> " copy." + fieldName field.TargetFieldId + " = " + v field.ValueId + ";") |> String.concat ""
            assign ("blk: { var copy = " + v source + ".*;" + updates + " break :blk nyBox(" + resultTypeName () + ", copy); }")
        | ExecutionInstructionKind.UnionConstruct(variantId, fields) ->
            assign ("nyBox(" + resultTypeName () + ", .{ ." + variantName variantId + " = " + fieldList fields + " })")
        | ExecutionInstructionKind.OptionNone -> assign "null"
        | ExecutionInstructionKind.OptionSome source -> assign (v source)
        | ExecutionInstructionKind.ResultOk source -> assign (".{ .ok = " + v source + " }")
        | ExecutionInstructionKind.ResultErr source -> assign (".{ .err = " + v source + " }")
        | ExecutionInstructionKind.ExtractUnionField(input, variantId, fieldId) -> assign (v input + "." + variantName variantId + "." + fieldName fieldId)
        | ExecutionInstructionKind.ExtractOptionSome input -> assign (v input + ".?")
        | ExecutionInstructionKind.ExtractResultOk input -> assign (v input + ".ok")
        | ExecutionInstructionKind.ExtractResultErr input -> assign (v input + ".err")
        | ExecutionInstructionKind.OptionIsSome input -> assign (v input + " != null")
        | ExecutionInstructionKind.ResultIsOk input -> assign (v input + " == .ok")
        | ExecutionInstructionKind.OptionGetOrDefault(option, fallback) -> assign (v option + " orelse " + v fallback)
        | ExecutionInstructionKind.ResultGetOrDefault(result, fallback) -> assign ("switch (" + v result + ") { .ok => |item| item, .err => " + v fallback + " }")
        | ExecutionInstructionKind.DirectCall(functionId, arguments) ->
            let byParameter = arguments |> List.map (fun argument -> argument.TargetParameterId, argument.ValueId) |> Map.ofList
            let rendered = parametersOf functionId |> List.map (fun parameterId -> v (Map.find parameterId byParameter))
            assign (functionName functionId + "(" + String.Join(", ", rendered) + ")")
        | _ -> None

    let private caseName (value: obj) =
        let text = sprintf "%A" value
        let cut = text.IndexOfAny([| ' '; '\n'; '(' |])
        if cut < 0 then text else text.Substring(0, cut)

    let private valuesOf (fn: ExecutionFunction) =
        (fn.Parameters |> List.map (fun parameter -> parameter.Value))
        @ (fn.Blocks |> List.collect (fun block -> block.Parameters @ (block.Instructions |> List.map (fun instruction -> instruction.Result))))

    /// What keeps a function out of the supported slice, if anything. Every terminator is supported.
    let private unsupported (types: Map<TypeId, ExecutionType>) (fn: ExecutionFunction) =
        let typeProblems =
            fn.ResultType :: (valuesOf fn |> List.map (fun value -> value.Type))
            |> List.filter (typeOf types >> Option.isNone)
            |> List.distinct
            |> List.map (fun typeRef -> $"type {typeRef}")
        let instructions =
            fn.Blocks
            |> List.collect (fun block -> block.Instructions)
            |> List.filter (fun instruction -> Option.isNone (instructionLine (fun _ -> []) instruction))
            |> List.map (fun instruction -> caseName (box instruction.Kind))
        List.distinct (typeProblems @ instructions)

    let private runtime = """const std = @import("std");

const NyUnit = struct {};

fn NyResult(comptime Ok: type, comptime Err: type) type {
    return union(enum) { ok: Ok, err: Err };
}

// shortcut: every String and boxed value lives in one arena for the life of the process; replace
// with reference counting (plan: Zig backend notes) before long-running programs use this backend.
var ny_arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);

fn nyAllocator() std.mem.Allocator {
    return ny_arena.allocator();
}

fn nyBox(comptime T: type, value: T) *const T {
    const slot = nyAllocator().create(T) catch @panic("out of memory");
    slot.* = value;
    return slot;
}

fn nyFloorDivide(dividend: i64, divisor: i64) i64 {
    if (divisor == 0) return 0;
    if (divisor == -1) return 0 -% dividend;
    return @divFloor(dividend, divisor);
}

fn nyFloorModulo(dividend: i64, divisor: i64) i64 {
    if (divisor == 0) return dividend;
    if (divisor == -1) return 0;
    const remainder = @rem(dividend, divisor);
    return if (remainder != 0 and (remainder < 0) != (divisor < 0)) remainder + divisor else remainder;
}

fn nyShiftLeft(value: i64, count: i64) i64 {
    if (count <= 0) return value;
    if (count >= 64) return 0;
    return value << @as(u6, @intCast(count));
}

fn nyShiftRight(value: i64, count: i64) i64 {
    if (count <= 0) return value;
    if (count >= 64) return if (value < 0) -1 else 0;
    return value >> @as(u6, @intCast(count));
}

fn nyInt64ToString(value: i64) []const u8 {
    return std.fmt.allocPrint(nyAllocator(), "{d}", .{value}) catch @panic("out of memory");
}

/// Strings are UTF-8 sequences of Unicode scalar values, so this never fails.
fn nyStringLength(text: []const u8) i64 {
    return @intCast(std.unicode.utf8CountCodepoints(text) catch unreachable);
}

fn nyConcat(left: []const u8, right: []const u8) []const u8 {
    if (left.len == 0) return right;
    if (right.len == 0) return left;
    const joined = nyAllocator().alloc(u8, left.len + right.len) catch @panic("out of memory");
    @memcpy(joined[0..left.len], left);
    @memcpy(joined[left.len..], right);
    return joined;
}

/// Structural equality over every supported value type.
fn nyEqual(left: anytype, right: @TypeOf(left)) bool {
    const T = @TypeOf(left);
    switch (@typeInfo(T)) {
        .int, .bool => return left == right,
        .optional => {
            if (left) |l| {
                if (right) |r| return nyEqual(l, r);
                return false;
            }
            return right == null;
        },
        .pointer => |pointer| {
            if (pointer.size == .slice) return std.mem.eql(pointer.child, left, right);
            return nyEqual(left.*, right.*);
        },
        .@"struct" => |info| {
            inline for (info.field_names) |name| {
                if (!nyEqual(@field(left, name), @field(right, name))) return false;
            }
            return true;
        },
        .@"union" => |info| {
            const tag = std.meta.activeTag(left);
            if (tag != std.meta.activeTag(right)) return false;
            inline for (info.field_names) |name| {
                if (tag == @field(info.tag_type.?, name)) return nyEqual(@field(left, name), @field(right, name));
            }
            unreachable;
        },
        else => @compileError("nyEqual does not support " ++ @typeName(T)),
    }
}
"""

    /// Zig support for a runPure runner: reads argument tokens in the order the authoring session
    /// writes them and writes a result in the session's envelope form.
    let runnerSupport = """
const NyTokens = struct {
    items: []const [:0]const u8,
    index: usize = 1,

    fn next(self: *NyTokens) []const u8 {
        const token = self.items[self.index];
        self.index += 1;
        return token;
    }
};

/// A String token is "s" and the Base64 of its UTF-16LE code units.
fn nyDecodeText(token: []const u8) []const u8 {
    const encoded = token[1..];
    const size = std.base64.standard.Decoder.calcSizeForSlice(encoded) catch @panic("bad String token");
    const units = nyAllocator().alloc(u16, size / 2) catch @panic("out of memory");
    std.base64.standard.Decoder.decode(std.mem.sliceAsBytes(units), encoded) catch @panic("bad String token");
    for (units) |*unit| unit.* = std.mem.littleToNative(u16, unit.*);
    return std.unicode.utf16LeToUtf8Alloc(nyAllocator(), units) catch @panic("bad String token");
}

fn nyEncodeText(out: *std.Io.Writer, text: []const u8) !void {
    const units = std.unicode.utf8ToUtf16LeAlloc(nyAllocator(), text) catch @panic("bad String");
    const bytes = std.mem.sliceAsBytes(units);
    const encoded = nyAllocator().alloc(u8, std.base64.standard.Encoder.calcSize(bytes.len)) catch @panic("out of memory");
    try out.print("\"s{s}\"", .{std.base64.standard.Encoder.encode(encoded, bytes)});
}

fn nyDecode(comptime T: type, tokens: *NyTokens) T {
    switch (@typeInfo(T)) {
        .int => return std.fmt.parseInt(i64, tokens.next(), 10) catch @panic("bad Int64 token"),
        .bool => return std.mem.eql(u8, tokens.next(), "1"),
        .optional => |info| {
            if (std.mem.eql(u8, tokens.next(), "1")) return nyDecode(info.child, tokens);
            return null;
        },
        .pointer => |info| {
            if (info.size == .slice) return nyDecodeText(tokens.next());
            return nyBox(info.child, nyDecode(info.child, tokens));
        },
        .@"struct" => |info| {
            if (T == NyUnit) {
                _ = tokens.next();
                return NyUnit{};
            }
            var value: T = undefined;
            inline for (info.field_names, info.field_types) |name, FieldType| @field(value, name) = nyDecode(FieldType, tokens);
            return value;
        },
        .@"union" => |info| {
            if (@hasDecl(T, "ny_ids")) {
                const index = std.fmt.parseInt(usize, tokens.next(), 10) catch @panic("bad variant token");
                inline for (info.field_names, info.field_types, 0..) |name, FieldType, position| {
                    if (position == index) return @unionInit(T, name, nyDecode(FieldType, tokens));
                }
                @panic("bad variant token");
            }
            if (std.mem.eql(u8, tokens.next(), "1")) return .{ .ok = nyDecode(@FieldType(T, "ok"), tokens) };
            return .{ .err = nyDecode(@FieldType(T, "err"), tokens) };
        },
        else => @compileError("nyDecode does not support " ++ @typeName(T)),
    }
}

fn nyEncode(out: *std.Io.Writer, value: anytype) !void {
    const T = @TypeOf(value);
    switch (@typeInfo(T)) {
        .int => try out.print("{d}", .{value}),
        .bool => try out.writeAll(if (value) "true" else "false"),
        .optional => {
            if (value) |item| {
                try out.writeAll("[");
                try nyEncode(out, item);
                try out.writeAll("]");
            } else try out.writeAll("[]");
        },
        .pointer => |info| {
            if (info.size == .slice) return nyEncodeText(out, value);
            return nyEncode(out, value.*);
        },
        .@"struct" => |info| {
            if (T == NyUnit) return out.writeAll("\"unit\"");
            try out.writeAll("[");
            inline for (info.field_names, 0..) |name, position| {
                if (position > 0) try out.writeAll(",");
                try nyEncode(out, @field(value, name));
            }
            try out.writeAll("]");
        },
        .@"union" => |info| {
            if (@hasDecl(T, "ny_ids")) {
                const tag = std.meta.activeTag(value);
                inline for (info.field_names, 0..) |name, position| {
                    if (tag == @field(info.tag_type.?, name)) {
                        try out.print("[\"{s}\",", .{T.ny_ids[position]});
                        try nyEncode(out, @field(value, name));
                        try out.writeAll("]");
                    }
                }
                return;
            }
            switch (value) {
                .ok => |item| {
                    try out.writeAll("[true,");
                    try nyEncode(out, item);
                    try out.writeAll("]");
                },
                .err => |item| {
                    try out.writeAll("[false,");
                    try nyEncode(out, item);
                    try out.writeAll("]");
                },
            }
        },
        else => @compileError("nyEncode does not support " ++ @typeName(T)),
    }
}
"""

    /// Fields and variants are emitted in stable-ID order, the order runPure tokens and results use.
    let private emitType (builder: StringBuilder) (types: Map<TypeId, ExecutionType>) (item: ExecutionType) =
        let line (text: string) = builder.Append(text).Append('\n') |> ignore
        let fields (items: ExecutionField list) =
            items
            |> List.sortBy (fun field -> StableId.value field.Id)
            |> List.map (fun field -> fieldName field.Id + ": " + (typeOf types field.Type).Value)
        match item.Kind with
        | ExecutionTypeKind.Product items ->
            line ("const " + typeName item.Id + " = struct { " + String.Join(", ", fields items) + " };")
        | ExecutionTypeKind.Union variants ->
            let ordered = variants |> List.sortBy (fun variant -> StableId.value variant.Id)
            line ("const " + typeName item.Id + " = union(enum) {")
            for variant in ordered do
                line ("    " + variantName variant.Id + ": struct { " + String.Join(", ", fields variant.Fields) + " },")
            let ids = ordered |> List.map (fun variant -> "\"" + StableId.value variant.Id + "\"")
            line ("    pub const ny_ids = [_][]const u8{ " + String.Join(", ", ids) + " };")
            line "};"
        line ""

    let private emitFunction
        (builder: StringBuilder)
        (types: Map<TypeId, ExecutionType>)
        (parametersOf: FunctionId -> ParameterId list)
        (fn: ExecutionFunction) =
        let line (text: string) = builder.Append(text).Append('\n') |> ignore
        let zig typeRef = (typeOf types typeRef).Value
        let parameters =
            fn.Parameters
            |> List.map (fun parameter -> parameterName parameter.SemanticParameterId + ": " + zig parameter.Value.Type)
        line ("pub fn " + functionName fn.SemanticFunctionId + "(" + String.Join(", ", parameters) + ") " + zig fn.ResultType + " {")
        let values =
            valuesOf fn
            |> List.distinctBy (fun value -> value.Id)
            |> List.sortBy (fun value -> ExecutionValueId.value value.Id)
        // Every value is a function-level variable, so blocks can assign it in any order; `_ = &x`
        // keeps Zig from rejecting a value that some path never reads.
        for value in values do
            line ("    var " + valueName value.Id + ": " + zig value.Type + " = undefined;")
            line ("    _ = &" + valueName value.Id + ";")
        for parameter in fn.Parameters do
            line ("    " + valueName parameter.Value.Id + " = " + parameterName parameter.SemanticParameterId + ";")
        let blocks = fn.Blocks |> List.sortBy (fun block -> ExecutionBlockId.value block.Id)
        line ("    const Block = enum { " + String.Join(", ", blocks |> List.map (fun block -> blockName block.Id)) + " };")
        // Blocks become prongs of a labeled switch; a jump is `continue :body .target`. Zig rejects an
        // unused label, so a function whose blocks never jump gets none.
        let jumps =
            blocks |> List.exists (fun block ->
                match block.Terminator with
                | ExecutionTerminator.Return _
                | ExecutionTerminator.Abort _ -> false
                | _ -> true)
        line ("    " + (if jumps then "body: " else "") + "switch (Block." + blockName fn.EntryBlockId + ") {")
        let assignAll (assignments: (ExecutionValueId * ExecutionValueId) list) =
            if not (List.isEmpty assignments) then
                line "            {"
                for index, (_, source) in List.indexed assignments do
                    line ("                const t" + string index + " = " + valueName source + ";")
                for index, (target, _) in List.indexed assignments do
                    line ("                " + valueName target + " = t" + string index + ";")
                line "            }"
        let goTo (blockId: ExecutionBlockId) = "continue :body ." + blockName blockId
        for block in blocks do
            line ("        ." + blockName block.Id + " => {")
            for instruction in block.Instructions do
                line ("            " + (instructionLine parametersOf instruction).Value)
            match block.Terminator with
            | ExecutionTerminator.Return value -> line ("            return " + valueName value + ";")
            | ExecutionTerminator.Abort(_, error) -> line ("            return .{ .err = " + valueName error + " };")
            | ExecutionTerminator.Jump(target, arguments) ->
                assignAll (arguments |> List.map (fun argument -> argument.TargetValueId, argument.ValueId))
                line ("            " + goTo target + ";")
            | ExecutionTerminator.Branch(condition, whenTrue, whenFalse) ->
                line ("            if (" + valueName condition + ") " + goTo whenTrue + ";")
                line ("            " + goTo whenFalse + ";")
            | ExecutionTerminator.MatchOption(input, someBlock, noneBlock) ->
                line ("            if (" + valueName input + " != null) " + goTo someBlock + ";")
                line ("            " + goTo noneBlock + ";")
            | ExecutionTerminator.MatchResult(input, okBlock, errBlock) ->
                line ("            if (" + valueName input + " == .ok) " + goTo okBlock + ";")
                line ("            " + goTo errBlock + ";")
            | ExecutionTerminator.MatchUnion(input, targets) ->
                line ("            switch (" + valueName input + ".*) {")
                for target in targets |> List.sortBy (fun target -> StableId.value target.VariantId) do
                    line ("                ." + variantName target.VariantId + " => " + goTo target.BlockId + ",")
                line "            }"
            | ExecutionTerminator.TailCall(_, arguments) ->
                let byParameter = arguments |> List.map (fun argument -> argument.TargetParameterId, argument.ValueId) |> Map.ofList
                fn.Parameters
                |> List.map (fun parameter -> parameter.Value.Id, Map.find parameter.SemanticParameterId byParameter)
                |> assignAll
                line ("            " + goTo fn.EntryBlockId + ";")
            line "        },"
        line "    }"
        line "}"
        line ""

    /// Zig source for every type and function of the program, or what is outside the supported slice.
    let emit (program: ExecutionProgram) : Result<string, string list> =
        let types = program.Types |> List.map (fun item -> item.Id, item) |> Map.ofList
        let monomorphic = program.Types |> List.filter (fun item -> List.isEmpty item.TypeParameters)
        let typeProblems =
            monomorphic
            |> List.collect (fun item ->
                match item.Kind with
                | ExecutionTypeKind.Product fields -> fields
                | ExecutionTypeKind.Union variants -> variants |> List.collect (fun variant -> variant.Fields))
            |> List.filter (fun field -> Option.isNone (typeOf types field.Type))
            |> List.map (fun field -> $"type {field.Type}")
        let problems =
            typeProblems
            @ (program.Functions |> List.collect (fun fn -> (if List.isEmpty fn.TypeParameters then [] else [ "generic function" ]) @ unsupported types fn))
            |> List.distinct
        if not (List.isEmpty problems) then Error problems
        else
            let parameters =
                program.Functions
                |> List.map (fun fn -> fn.SemanticFunctionId, fn.Parameters |> List.map (fun parameter -> parameter.SemanticParameterId))
                |> Map.ofList
            let builder = StringBuilder()
            builder.Append("// Generated by the Nyrwvy Zig backend.\n\n").Append(runtime).Append('\n') |> ignore
            for item in monomorphic |> List.sortBy (fun item -> StableId.value item.Id) do
                emitType builder types item
            for fn in program.Functions |> List.sortBy (fun fn -> StableId.value fn.SemanticFunctionId) do
                emitFunction builder types (fun id -> Map.find id parameters) fn
            Ok(builder.ToString())
