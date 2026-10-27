namespace Nyrwvy.Core

open System
open System.Text

/// Emits Zig source from Execution IR. The first slice covers Int64, Bool and Unit values, their
/// operations, direct calls and the block terminators that need no other types; anything else is
/// reported as unsupported instead of emitted.
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

    /// The Zig type of a supported value type.
    let zigType (typeRef: TypeRef) =
        match typeRef with
        | TypeRef.Primitive PrimitiveType.Int64 -> Some "i64"
        | TypeRef.Primitive PrimitiveType.Bool -> Some "bool"
        | TypeRef.Primitive PrimitiveType.Unit -> Some "NyUnit"
        | _ -> None

    let private typeOf (typeRef: TypeRef) =
        zigType typeRef |> Option.defaultWith (fun () -> invalidOp $"Unsupported type {typeRef} reached emission.")

    /// One line per instruction, or None when the instruction is outside the supported slice.
    let private instructionLine
        (parametersOf: FunctionId -> ParameterId list)
        (instruction: ExecutionInstruction)
        : string option =
        let target = valueName instruction.Result.Id
        let v = valueName
        let assign (expression: string) = Some(target + " = " + expression + ";")
        let binary left operator right = assign (v left + " " + operator + " " + v right)
        match instruction.Kind with
        | ExecutionInstructionKind.ReadParameter source -> assign (v source)
        | ExecutionInstructionKind.BoolLiteral value -> assign (if value then "true" else "false")
        | ExecutionInstructionKind.UnitLiteral -> assign "NyUnit{}"
        | ExecutionInstructionKind.Int64Literal value -> assign (string value)
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
        | ExecutionInstructionKind.DirectCall(functionId, arguments) ->
            let byParameter = arguments |> List.map (fun argument -> argument.TargetParameterId, argument.ValueId) |> Map.ofList
            let rendered = parametersOf functionId |> List.map (fun parameterId -> v (Map.find parameterId byParameter))
            assign (functionName functionId + "(" + String.Join(", ", rendered) + ")")
        | _ -> None

    let private caseName (value: obj) =
        let text = sprintf "%A" value
        let cut = text.IndexOfAny([| ' '; '\n'; '(' |])
        if cut < 0 then text else text.Substring(0, cut)

    /// What keeps a function out of the supported slice, if anything.
    let private unsupported (fn: ExecutionFunction) =
        let values =
            (fn.Parameters |> List.map (fun parameter -> parameter.Value))
            @ (fn.Blocks |> List.collect (fun block -> block.Parameters @ (block.Instructions |> List.map (fun instruction -> instruction.Result))))
        let types =
            fn.ResultType :: (values |> List.map (fun value -> value.Type))
            |> List.filter (zigType >> Option.isNone)
            |> List.distinct
            |> List.map (fun typeRef -> $"type {typeRef}")
        let instructions =
            fn.Blocks
            |> List.collect (fun block -> block.Instructions)
            |> List.filter (fun instruction -> Option.isNone (instructionLine (fun _ -> []) instruction))
            |> List.map (fun instruction -> caseName (box instruction.Kind))
        let terminators =
            fn.Blocks
            |> List.choose (fun block ->
                match block.Terminator with
                | ExecutionTerminator.Return _
                | ExecutionTerminator.Jump _
                | ExecutionTerminator.Branch _
                | ExecutionTerminator.TailCall _ -> None
                | other -> Some(caseName (box other)))
        List.distinct (types @ instructions @ terminators)

    let private runtime = """const NyUnit = struct {};

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
"""

    let private emitFunction (builder: StringBuilder) (parametersOf: FunctionId -> ParameterId list) (fn: ExecutionFunction) =
        let line (text: string) = builder.Append(text).Append('\n') |> ignore
        let parameters =
            fn.Parameters
            |> List.map (fun parameter -> parameterName parameter.SemanticParameterId + ": " + typeOf parameter.Value.Type)
        line ("pub fn " + functionName fn.SemanticFunctionId + "(" + String.Join(", ", parameters) + ") " + typeOf fn.ResultType + " {")
        let values =
            (fn.Parameters |> List.map (fun parameter -> parameter.Value))
            @ (fn.Blocks |> List.collect (fun block -> block.Parameters @ (block.Instructions |> List.map (fun instruction -> instruction.Result))))
            |> List.distinctBy (fun value -> value.Id)
            |> List.sortBy (fun value -> ExecutionValueId.value value.Id)
        // Every value is a function-level variable, so blocks can assign it in any order; `_ = &x`
        // keeps Zig from rejecting a value that some path never reads.
        for value in values do
            line ("    var " + valueName value.Id + ": " + typeOf value.Type + " = undefined;")
            line ("    _ = &" + valueName value.Id + ";")
        for parameter in fn.Parameters do
            line ("    " + valueName parameter.Value.Id + " = " + parameterName parameter.SemanticParameterId + ";")
        let blocks = fn.Blocks |> List.sortBy (fun block -> ExecutionBlockId.value block.Id)
        line ("    const Block = enum { " + String.Join(", ", blocks |> List.map (fun block -> blockName block.Id)) + " };")
        // Blocks become prongs of a labeled switch; a jump is `continue :body .target`.
        // Zig rejects an unused label, so a function whose blocks never jump gets none.
        let jumps = blocks |> List.exists (fun block -> match block.Terminator with ExecutionTerminator.Return _ -> false | _ -> true)
        line ("    " + (if jumps then "body: " else "") + "switch (Block." + blockName fn.EntryBlockId + ") {")
        let assignAll (assignments: (ExecutionValueId * ExecutionValueId) list) =
            if not (List.isEmpty assignments) then
                line "            {"
                for index, (_, source) in List.indexed assignments do
                    line ("                const t" + string index + " = " + valueName source + ";")
                for index, (target, _) in List.indexed assignments do
                    line ("                " + valueName target + " = t" + string index + ";")
                line "            }"
        for block in blocks do
            line ("        ." + blockName block.Id + " => {")
            for instruction in block.Instructions do
                line ("            " + (instructionLine parametersOf instruction).Value)
            match block.Terminator with
            | ExecutionTerminator.Return value -> line ("            return " + valueName value + ";")
            | ExecutionTerminator.Jump(target, arguments) ->
                assignAll (arguments |> List.map (fun argument -> argument.TargetValueId, argument.ValueId))
                line ("            continue :body ." + blockName target + ";")
            | ExecutionTerminator.Branch(condition, whenTrue, whenFalse) ->
                line ("            if (" + valueName condition + ") continue :body ." + blockName whenTrue + ";")
                line ("            continue :body ." + blockName whenFalse + ";")
            | ExecutionTerminator.TailCall(_, arguments) ->
                let byParameter = arguments |> List.map (fun argument -> argument.TargetParameterId, argument.ValueId) |> Map.ofList
                fn.Parameters
                |> List.map (fun parameter -> parameter.Value.Id, Map.find parameter.SemanticParameterId byParameter)
                |> assignAll
                line ("            continue :body ." + blockName fn.EntryBlockId + ";")
            | other -> invalidOp $"Unsupported terminator {other} reached emission."
            line "        },"
        line "    }"
        line "}"
        line ""

    /// Zig source for every function of the program, or what is outside the supported slice.
    let emit (program: ExecutionProgram) : Result<string, string list> =
        let problems =
            program.Functions
            |> List.collect (fun fn ->
                (if List.isEmpty fn.TypeParameters then [] else [ "generic function" ]) @ unsupported fn)
            |> List.distinct
        if not (List.isEmpty problems) then Error problems
        else
            let parameters =
                program.Functions
                |> List.map (fun fn -> fn.SemanticFunctionId, fn.Parameters |> List.map (fun parameter -> parameter.SemanticParameterId))
                |> Map.ofList
            let builder = StringBuilder()
            builder.Append("// Generated by the Nyrwvy Zig backend from snapshot ").Append(StableId.value program.SourceSnapshotId).Append(".\n\n") |> ignore
            builder.Append(runtime).Append('\n') |> ignore
            for fn in program.Functions |> List.sortBy (fun fn -> StableId.value fn.SemanticFunctionId) do
                emitFunction builder (fun id -> Map.find id parameters) fn
            Ok(builder.ToString())
