namespace Nyrwvy.Core

open System
open System.Globalization
open System.Text

[<RequireQualifiedAccess>]
module CSharpBackend =
    type private SourceWriter() =
        let builder = StringBuilder()
        let mutable indent = 0

        member _.Line(text: string) =
            if text.Length > 0 then
                builder.Append(' ', indent * 4) |> ignore
            builder.Append(text).Append('\n') |> ignore

        member _.Push() = indent <- indent + 1
        member _.Pop() = indent <- indent - 1
        override _.ToString() = builder.ToString()

    let private escapeStringLiteral (value: string) =
        let builder = StringBuilder()
        builder.Append('"') |> ignore
        for character in value do
            match character with
            | '"' -> builder.Append("\\\"") |> ignore
            | '\\' -> builder.Append("\\\\") |> ignore
            | '\r' -> builder.Append("\\r") |> ignore
            | '\n' -> builder.Append("\\n") |> ignore
            | '\t' -> builder.Append("\\t") |> ignore
            | character when int character >= 0x20 && int character <= 0x7e ->
                builder.Append(character) |> ignore
            | character ->
                builder.Append("\\u") |> ignore
                builder.Append((int character).ToString("X4")) |> ignore
        builder.Append('"') |> ignore
        builder.ToString()

    let private renderInt64Literal (value: int64) =
        if value = Int64.MinValue then
            "long.MinValue"
        else
            value.ToString(CultureInfo.InvariantCulture) + "L"

    let private renderFloat64Literal bits =
        "BitConverter.UInt64BitsToDouble(0x" + Float64Bits.toHex bits + "UL)"

    let rec private formatType (typeRef: TypeRef) =
        match typeRef with
        | TypeRef.Primitive PrimitiveType.String -> "string"
        | TypeRef.Primitive PrimitiveType.Bool -> "bool"
        | TypeRef.Primitive PrimitiveType.Unit -> "NyUnit"
        | TypeRef.Primitive PrimitiveType.Int64 -> "long"
        | TypeRef.Primitive PrimitiveType.Float64 -> "double"
        | TypeRef.Named typeId -> CSharpBackendNames.typeName typeId
        | TypeRef.Capability capabilityId -> CSharpBackendNames.capabilityName capabilityId
        | TypeRef.Function(arguments, result) ->
            "NyFunction" + string arguments.Length + "<" + (arguments @ [ result ] |> List.map formatType |> String.concat ", ") + ">"
        | TypeRef.TypeParameter(typeParameterId, _) -> CSharpBackendNames.typeParameterName typeParameterId
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ]) ->
            "NyOption<" + formatType itemType + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
            "NyResult<" + formatType okType + ", " + formatType errorType + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ]) ->
            "NyList<" + formatType itemType + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, arguments) ->
            invalidOp $"C# backend received Option with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, arguments) ->
            invalidOp $"C# backend received Result with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, arguments) ->
            invalidOp $"C# backend received List with {arguments.Length} type arguments."

    let private emitRuntime (writer: SourceWriter) =
        writer.Line("public readonly record struct NyUnit")
        writer.Line("{")
        writer.Push()
        writer.Line("public static NyUnit Value => default;")
        writer.Pop()
        writer.Line("}")
        writer.Line("")
        writer.Line("public readonly record struct NyOption<T>(bool HasValue, T Value)")
        writer.Line("{")
        writer.Push()
        writer.Line("public static NyOption<T> Some(T value) => new(true, value);")
        writer.Line("public static NyOption<T> None() => new(false, default!);")
        writer.Pop()
        writer.Line("}")
        writer.Line("")
        writer.Line("public readonly record struct NyResult<TOk, TError>(bool IsOk, TOk OkValue, TError ErrorValue)")
        writer.Line("{")
        writer.Push()
        writer.Line("public static NyResult<TOk, TError> Ok(TOk value) => new(true, value, default!);")
        writer.Line("public static NyResult<TOk, TError> Err(TError error) => new(false, default!, error);")
        writer.Pop()
        writer.Line("}")
        writer.Line("")
        writer.Line("public sealed class NyList<T>")
        writer.Line("{")
        writer.Push()
        writer.Line("private readonly T[] _items;")
        writer.Line("private NyList(T[] items) => _items = items;")
        writer.Line("internal static NyList<T> FromOwnedArray(T[] items) => new(items);")
        writer.Line("public static NyList<T> Empty() => new(Array.Empty<T>());")
        writer.Line("public static NyList<T> Create(params T[] items) => new((T[])items.Clone());")
        writer.Line("public long Length => _items.LongLength;")
        writer.Line("public NyList<T> AppendItem(T item)")
        writer.Line("{")
        writer.Push()
        writer.Line("var next = new T[_items.Length + 1];")
        writer.Line("Array.Copy(_items, next, _items.Length);")
        writer.Line("next[_items.Length] = item;")
        writer.Line("return new NyList<T>(next);")
        writer.Pop()
        writer.Line("}")
        writer.Line("public NyList<T> Concat(NyList<T> other)")
        writer.Line("{")
        writer.Push()
        writer.Line("var next = new T[_items.Length + other._items.Length];")
        writer.Line("Array.Copy(_items, 0, next, 0, _items.Length);")
        writer.Line("Array.Copy(other._items, 0, next, _items.Length, other._items.Length);")
        writer.Line("return new NyList<T>(next);")
        writer.Pop()
        writer.Line("}")
        writer.Line("public S Fold<S>(S seed, Func<S, T, S> folder)")
        writer.Line("{")
        writer.Push()
        writer.Line("var state = seed;")
        writer.Line("for (var i = 0; i < _items.Length; i++) state = folder(state, _items[i]);")
        writer.Line("return state;")
        writer.Pop()
        writer.Line("}")
        writer.Line("public NyList<U> Map<U>(Func<T, U> mapper)")
        writer.Line("{")
        writer.Push()
        writer.Line("var next = new U[_items.Length];")
        writer.Line("for (var i = 0; i < _items.Length; i++)")
        writer.Push()
        writer.Line("next[i] = mapper(_items[i]);")
        writer.Pop()
        writer.Line("return NyList<U>.FromOwnedArray(next);")
        writer.Pop()
        writer.Line("}")
        writer.Line("public NyList<T> Filter(Func<T, bool> predicate)")
        writer.Line("{")
        writer.Push()
        writer.Line("var buffer = new T[_items.Length];")
        writer.Line("var count = 0;")
        writer.Line("for (var i = 0; i < _items.Length; i++)")
        writer.Line("{")
        writer.Push()
        writer.Line("if (!predicate(_items[i])) continue;")
        writer.Line("buffer[count++] = _items[i];")
        writer.Pop()
        writer.Line("}")
        writer.Line("var next = new T[count];")
        writer.Line("Array.Copy(buffer, next, count);")
        writer.Line("return new NyList<T>(next);")
        writer.Pop()
        writer.Line("}")
        writer.Line("public bool Any(Func<T, bool> predicate)")
        writer.Line("{")
        writer.Push()
        writer.Line("for (var i = 0; i < _items.Length; i++)")
        writer.Push()
        writer.Line("if (predicate(_items[i])) return true;")
        writer.Pop()
        writer.Line("return false;")
        writer.Pop()
        writer.Line("}")
        writer.Line("public bool All(Func<T, bool> predicate)")
        writer.Line("{")
        writer.Push()
        writer.Line("for (var i = 0; i < _items.Length; i++)")
        writer.Push()
        writer.Line("if (!predicate(_items[i])) return false;")
        writer.Pop()
        writer.Line("return true;")
        writer.Pop()
        writer.Line("}")
        writer.Pop()
        writer.Line("}")
        writer.Line("")

    let private fieldParameter (field: ExecutionField) =
        formatType field.Type + " " + CSharpBackendNames.fieldName field.Id

    /// Ambient cancellation for ParallelAll workers, emitted only for programs that use concurrency so
    /// other generated programs are unchanged. External implementations read the same token.
    let private emitConcurrencyRuntime (writer: SourceWriter) (program: ExecutionProgram) =
        let usesConcurrency =
            program.Functions
            |> List.exists (fun functionEntity ->
                functionEntity.Blocks
                |> List.exists (fun block ->
                    block.Instructions
                    |> List.exists (fun instruction ->
                        match instruction.Kind with
                        | ExecutionInstructionKind.ParallelAll _
                        | ExecutionInstructionKind.CancellationRequested -> true
                        | _ -> false)))
        if usesConcurrency then
            writer.Line("public static class NyRuntime")
            writer.Line("{")
            writer.Push()
            writer.Line("public static readonly System.Threading.AsyncLocal<System.Threading.CancellationToken> Cancellation = new();")
            writer.Line("public static bool CancellationRequested => Cancellation.Value.IsCancellationRequested;")
            writer.Line("public static void WaitForCancellation() => Cancellation.Value.WaitHandle.WaitOne();")
            writer.Pop()
            writer.Line("}")
            writer.Line("")

    let private emitFunctionTypes (writer: SourceWriter) (program: ExecutionProgram) =
        let rec arities = function
            | TypeRef.Function(arguments, result) -> arguments.Length :: List.collect arities (arguments @ [ result ])
            | TypeRef.BuiltinGeneric(_, arguments) -> List.collect arities arguments
            | _ -> []
        let declaredTypes = program.Types |> List.collect (fun t ->
            match t.Kind with
            | ExecutionTypeKind.Product fields -> fields |> List.map (fun f -> f.Type)
            | ExecutionTypeKind.Union variants -> variants |> List.collect (fun v -> v.Fields |> List.map (fun f -> f.Type)))
        let capabilityTypes = program.Capabilities |> List.collect (fun c -> c.Operations |> List.collect (fun op -> op.ResultType :: List.map (fun (p: ExecutionCapabilityParameter) -> p.Type) op.Parameters))
        let functionTypes = program.Functions |> List.collect (fun fn ->
            fn.ResultType :: (fn.Parameters |> List.map (fun p -> p.Value.Type))
            @ (fn.Blocks |> List.collect (fun block -> List.map (fun (p: ExecutionValue) -> p.Type) block.Parameters @ List.map (fun i -> i.Result.Type) block.Instructions)))
        let requiredArities = declaredTypes @ capabilityTypes @ functionTypes |> List.collect arities |> List.distinct |> List.sort
        for count in requiredArities do
            let types = [ for index in 0 .. count - 1 -> "T" + string index ]
            let generics = String.concat ", " (types @ [ "TResult" ])
            let arguments = types |> List.mapi (fun index t -> t + " arg" + string index) |> String.concat ", "
            writer.Line("public delegate TResult NyFunction" + string count + "<" + generics + ">(" + arguments + ");")
        if not (List.isEmpty requiredArities) then writer.Line("")

    let private emitTypes (writer: SourceWriter) (program: ExecutionProgram) =
        for typeEntity in program.Types |> List.sortBy (fun item -> StableId.value item.Id) do
            let typeName = CSharpBackendNames.typeName typeEntity.Id
            match typeEntity.Kind with
            | ExecutionTypeKind.Product fields ->
                let parameters =
                    fields
                    |> List.sortBy (fun field -> StableId.value field.Id)
                    |> List.map fieldParameter
                    |> String.concat ", "
                writer.Line("public sealed record " + typeName + "(" + parameters + ");")
            | ExecutionTypeKind.Union variants ->
                writer.Line("public abstract record " + typeName + ";")
                for variant in variants |> List.sortBy (fun item -> StableId.value item.Id) do
                    let parameters =
                        variant.Fields
                        |> List.sortBy (fun field -> StableId.value field.Id)
                        |> List.map fieldParameter
                        |> String.concat ", "
                    let variantName = CSharpBackendNames.variantName variant.Id
                    writer.Line("public sealed record " + variantName + "(" + parameters + ") : " + typeName + ";")
            writer.Line("")

    let private capabilityParameter (parameter: ExecutionCapabilityParameter) =
        formatType parameter.Type + " " + CSharpBackendNames.capabilityParameterName parameter.Id

    let private emitCapabilities (writer: SourceWriter) (program: ExecutionProgram) =
        for capability in program.Capabilities |> List.sortBy (fun item -> StableId.value item.Id) do
            let capabilityName = CSharpBackendNames.capabilityName capability.Id
            writer.Line("public interface " + capabilityName)
            writer.Line("{")
            writer.Push()
            for operation in capability.Operations |> List.sortBy (fun item -> StableId.value item.Id) do
                let parameters =
                    operation.Parameters
                    |> List.map capabilityParameter
                    |> String.concat ", "
                let methodName = CSharpBackendNames.capabilityOperationName operation.Id
                writer.Line(formatType operation.ResultType + " " + methodName + "(" + parameters + ");")
            writer.Pop()
            writer.Line("}")
            writer.Line("")

    let private hasCapabilityAdapter
        (context: CSharpBackendValidation.Context)
        (capability: ExecutionCapability) =
        let allBound =
            capability.Operations
            |> List.forall (fun operation -> Map.containsKey operation.Id context.BindingsByOperation)
        let anyBound =
            capability.Operations
            |> List.exists (fun operation -> Map.containsKey operation.Id context.BindingsByOperation)
        allBound
        && anyBound
        && (capability.Operations
            |> List.forall (fun operation ->
                Map.find operation.Id context.BindingsByOperation = CSharpCapabilityBindingKind.SystemConsoleWriteLine))

    let private emitAdapters
        (writer: SourceWriter)
        (context: CSharpBackendValidation.Context)
        (program: ExecutionProgram) =
        for capability in program.Capabilities |> List.sortBy (fun item -> StableId.value item.Id) do
            if hasCapabilityAdapter context capability then
                let capabilityName = CSharpBackendNames.capabilityName capability.Id
                let adapterName = CSharpBackendNames.capabilityAdapterName capability.Id
                writer.Line("public sealed class " + adapterName + " : " + capabilityName)
                writer.Line("{")
                writer.Push()
                for operation in capability.Operations |> List.sortBy (fun item -> StableId.value item.Id) do
                    let parameters =
                        operation.Parameters
                        |> List.map capabilityParameter
                        |> String.concat ", "
                    let methodName = CSharpBackendNames.capabilityOperationName operation.Id
                    writer.Line("public " + formatType operation.ResultType + " " + methodName + "(" + parameters + ")")
                    writer.Line("{")
                    writer.Push()
                    match Map.find operation.Id context.BindingsByOperation with
                    | CSharpCapabilityBindingKind.SystemConsoleWriteLine ->
                        let parameter = List.exactlyOne operation.Parameters
                        let parameterName = CSharpBackendNames.capabilityParameterName parameter.Id
                        writer.Line("System.Console.WriteLine(" + parameterName + ");")
                        writer.Line("return NyUnit.Value;")
                    | CSharpCapabilityBindingKind.ExternalImplementation ->
                        invalidOp "An external implementation does not generate an adapter."
                    writer.Pop()
                    writer.Line("}")
                writer.Pop()
                writer.Line("}")
                writer.Line("")

    let private valueName (valueId: ExecutionValueId) =
        CSharpBackendNames.executionValueName valueId

    let private callbackName (functionId: FunctionId) =
        CSharpBackendNames.functionContainerName + "." + CSharpBackendNames.functionName functionId

    let private renderNamedCallArguments (arguments: ExecutionCallArgument list) =
        arguments
        |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId)
        |> List.map (fun argument ->
            CSharpBackendNames.parameterName argument.TargetParameterId
            + ": "
            + valueName argument.ValueId)
        |> String.concat ", "

    let private renderCapabilityArguments (arguments: ExecutionCapabilityArgument list) =
        arguments
        |> List.sortBy (fun argument -> StableId.value argument.TargetCapabilityParameterId)
        |> List.map (fun argument ->
            CSharpBackendNames.capabilityParameterName argument.TargetCapabilityParameterId
            + ": "
            + valueName argument.ValueId)
        |> String.concat ", "

    let private renderFieldArguments (fields: ExecutionFieldValue list) =
        fields
        |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
        |> List.map (fun field ->
            CSharpBackendNames.fieldName field.TargetFieldId
            + ": "
            + valueName field.ValueId)
        |> String.concat ", "

    let private renderListItems (itemValueIds: ExecutionValueId list) =
        itemValueIds |> List.map valueName |> String.concat ", "

    let private emitBinaryExpression
        (writer: SourceWriter)
        target
        leftValueId
        operatorText
        rightValueId
        wrap =
        let expression = valueName leftValueId + " " + operatorText + " " + valueName rightValueId
        if wrap then
            writer.Line(target + " = unchecked(" + expression + ");")
        else
            writer.Line(target + " = " + expression + ";")

    /// A callback with context becomes a lambda that passes context values and the iteration values
    /// by parameter name; without context the method group is emitted unchanged.
    /// Calls a callback with its context values and the given iteration values, all by parameter name.
    let private renderInvocation (parametersOf: FunctionId -> (ParameterId * TypeRef) list) (functionId: FunctionId) (context: ExecutionCallArgument list) (iterationValues: string list) =
        let bound = context |> List.map (fun argument -> argument.TargetParameterId) |> Set.ofList
        let iteration = parametersOf functionId |> List.map fst |> List.filter (fun parameterId -> not (Set.contains parameterId bound))
        let arguments =
            (context |> List.map (fun argument -> CSharpBackendNames.parameterName argument.TargetParameterId + ": " + valueName argument.ValueId))
            @ (List.map2 (fun parameterId value -> CSharpBackendNames.parameterName parameterId + ": " + value) iteration iterationValues)
        callbackName functionId + "(" + String.concat ", " arguments + ")"

    let private renderCallback (parametersOf: FunctionId -> (ParameterId * TypeRef) list) (target: string) (functionId: FunctionId) (context: ExecutionCallArgument list) =
        let generic = parametersOf functionId |> List.exists (fun (_, parameterType) -> not (List.isEmpty (TypeParameters.occurring parameterType)))
        // A generic method group cannot be inferred by C#; a lambda lets the call infer its type arguments.
        if List.isEmpty context && not generic then callbackName functionId
        else
            let unbound = (parametersOf functionId).Length - context.Length
            let names = List.init unbound (fun index -> target + "_it" + string index)
            "(" + String.concat ", " names + ") => " + renderInvocation parametersOf functionId context names

    let private emitInstruction
        (writer: SourceWriter)
        (parametersOf: FunctionId -> (ParameterId * TypeRef) list)
        (valueTypeOf: ExecutionValueId -> TypeRef)
        (operationResultOf: CapabilityOperationId -> TypeRef)
        (instruction: ExecutionInstruction) =
        let target = valueName instruction.Result.Id
        let callback = renderCallback parametersOf target
        match instruction.Kind with
        | ExecutionInstructionKind.ReadParameter sourceValueId
        | ExecutionInstructionKind.ReadPatternBinding sourceValueId ->
            writer.Line(target + " = " + valueName sourceValueId + ";")
        | ExecutionInstructionKind.StringLiteral value ->
            writer.Line(target + " = " + escapeStringLiteral value + ";")
        | ExecutionInstructionKind.BoolLiteral value ->
            writer.Line(target + " = " + (if value then "true" else "false") + ";")
        | ExecutionInstructionKind.UnitLiteral ->
            writer.Line(target + " = NyUnit.Value;")
        | ExecutionInstructionKind.Int64Literal value ->
            writer.Line(target + " = " + renderInt64Literal value + ";")
        | ExecutionInstructionKind.Float64Literal bits ->
            writer.Line(target + " = " + renderFloat64Literal bits + ";")
        | ExecutionInstructionKind.StringIsEmpty sourceValueId ->
            writer.Line(target + " = " + valueName sourceValueId + ".Length == 0;")
        | ExecutionInstructionKind.Int64ToString sourceValueId ->
            writer.Line(target + " = " + valueName sourceValueId + ".ToString(System.Globalization.CultureInfo.InvariantCulture);")
        | ExecutionInstructionKind.StringParseInt64 sourceValueId ->
            // Only '-'? ASCII digits are accepted; TryParse then enforces the Int64 range.
            writer.Line("{")
            writer.Push()
            writer.Line("var text = " + valueName sourceValueId + ";")
            writer.Line("var start = text.Length > 0 && text[0] == '-' ? 1 : 0;")
            writer.Line("var digits = text.Length > start;")
            writer.Line("for (var index = start; digits && index < text.Length; index++) digits = text[index] >= '0' && text[index] <= '9';")
            writer.Line(target + " = digits && long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? NyOption<long>.Some(parsed) : NyOption<long>.None();")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.StringContains(sourceValueId, substringId) ->
            writer.Line(target + " = " + valueName sourceValueId + ".Contains(" + valueName substringId + ", StringComparison.Ordinal);")
        | ExecutionInstructionKind.StringEqual(leftValueId, rightValueId) ->
            writer.Line(target + " = string.Equals(" + valueName leftValueId + ", " + valueName rightValueId + ", StringComparison.Ordinal);")
        | ExecutionInstructionKind.StringConcat(leftValueId, rightValueId) ->
            writer.Line(target + " = string.Concat(" + valueName leftValueId + ", " + valueName rightValueId + ");")
        | ExecutionInstructionKind.Int64WrappingAdd(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "+" rightValueId true
        | ExecutionInstructionKind.Int64WrappingSubtract(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "-" rightValueId true
        | ExecutionInstructionKind.Int64WrappingMultiply(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "*" rightValueId true
        | ExecutionInstructionKind.Int64Equal(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "==" rightValueId false
        | ExecutionInstructionKind.Int64LessThan(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "<" rightValueId false
        | ExecutionInstructionKind.Int64LessThanOrEqual(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "<=" rightValueId false
        | ExecutionInstructionKind.Int64GreaterThan(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId ">" rightValueId false
        | ExecutionInstructionKind.Int64GreaterThanOrEqual(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId ">=" rightValueId false
        | ExecutionInstructionKind.Float64Add(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "+" rightValueId false
        | ExecutionInstructionKind.Float64Subtract(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "-" rightValueId false
        | ExecutionInstructionKind.Float64Multiply(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "*" rightValueId false
        | ExecutionInstructionKind.Float64Divide(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "/" rightValueId false
        | ExecutionInstructionKind.Float64Equal(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "==" rightValueId false
        | ExecutionInstructionKind.Float64LessThan(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "<" rightValueId false
        | ExecutionInstructionKind.Float64IsNaN valueId ->
            writer.Line(target + " = double.IsNaN(" + valueName valueId + ");")
        | ExecutionInstructionKind.Float64IsFinite valueId ->
            writer.Line(target + " = double.IsFinite(" + valueName valueId + ");")
        | ExecutionInstructionKind.Float64IsInfinite valueId ->
            writer.Line(target + " = double.IsInfinity(" + valueName valueId + ");")
        | ExecutionInstructionKind.ListEmpty ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".Empty();")
        | ExecutionInstructionKind.ListConstruct itemValueIds ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".Create(" + renderListItems itemValueIds + ");")
        | ExecutionInstructionKind.ListLength listValueId ->
            writer.Line(target + " = " + valueName listValueId + ".Length;")
        | ExecutionInstructionKind.ListAppendItem(listValueId, itemValueId) ->
            writer.Line(target + " = " + valueName listValueId + ".AppendItem(" + valueName itemValueId + ");")
        | ExecutionInstructionKind.ListConcat(leftListValueId, rightListValueId) ->
            writer.Line(target + " = " + valueName leftListValueId + ".Concat(" + valueName rightListValueId + ");")
        | ExecutionInstructionKind.ListFold(sourceListValueId, seedValueId, folderFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".Fold(" + valueName seedValueId + ", " + callback folderFunctionId context + ");")
        | ExecutionInstructionKind.ListEffectFold(sourceListValueId, seedValueId, folderFunctionId, context) ->
            if instruction.Result.Type = valueTypeOf seedValueId then
                writer.Line(target + " = " + valueName sourceListValueId + ".Fold(" + valueName seedValueId + ", " + callback folderFunctionId context + ");")
            else
                // Result accumulator: after the first Err the folder, and so its effects, are not called again.
                let state, item = target + "_state", target + "_item"
                let resultType = formatType instruction.Result.Type
                let step = renderInvocation parametersOf folderFunctionId context [ state + ".OkValue"; item ]
                writer.Line(target + " = " + valueName sourceListValueId + ".Fold(" + resultType + ".Ok(" + valueName seedValueId + "), (" + state + ", " + item + ") => " + state + ".IsOk ? " + step + " : " + state + ");")
        | ExecutionInstructionKind.UseResource(acquireValueId, releaseCapabilityValueId, releaseOperationId, bodyFunctionId, context) ->
            // Release always runs after acquisition succeeded: on Ok and Err exits, and before a host
            // exception from the body propagates. A body Err takes precedence over a release Err.
            let resultType = formatType instruction.Result.Type
            let acquired, resource, body, released = target + "_acquired", target + "_resource", target + "_body", target + "_released"
            let release = valueName releaseCapabilityValueId + "." + CSharpBackendNames.capabilityOperationName releaseOperationId + "(" + resource + ")"
            writer.Line("{")
            writer.Push()
            writer.Line("var " + acquired + " = " + valueName acquireValueId + ";")
            writer.Line("if (!" + acquired + ".IsOk) " + target + " = " + resultType + ".Err(" + acquired + ".ErrorValue);")
            writer.Line("else")
            writer.Line("{")
            writer.Push()
            writer.Line("var " + resource + " = " + acquired + ".OkValue;")
            writer.Line(resultType + " " + body + ";")
            writer.Line("try { " + body + " = " + renderInvocation parametersOf bodyFunctionId context [ resource ] + "; }")
            writer.Line("catch { try { " + release + "; } catch { } throw; }")
            match operationResultOf releaseOperationId with
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, _) ->
                writer.Line("var " + released + " = " + release + ";")
                writer.Line(target + " = " + body + ".IsOk && !" + released + ".IsOk ? " + resultType + ".Err(" + released + ".ErrorValue) : " + body + ";")
            | _ ->
                writer.Line(release + ";")
                writer.Line(target + " = " + body + ";")
            writer.Pop()
            writer.Line("}")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.CancellationRequested ->
            writer.Line(target + " = NyRuntime.CancellationRequested;")
        | ExecutionInstructionKind.ParallelAll(sourceListValueId, workerFunctionId, context) ->
            // Structured: every worker is awaited before the node completes. The first Err or host
            // exception cancels the shared token; faults win, then the lowest-index Err, else all Ok.
            // Link the ambient token so cancellation also reaches workers of nested ParallelAll nodes.
            let itemType =
                match valueTypeOf sourceListValueId with
                | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ]) -> formatType item
                | other -> invalidOp $"ParallelAll source must be a List, got {other}."
            let workerResult, okType =
                match instruction.Result.Type with
                | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ ok ]); error ]) ->
                    "NyResult<" + formatType ok + ", " + formatType error + ">", formatType ok
                | other -> invalidOp $"ParallelAll result must be Result<List<U>, E>, got {other}."
            let resultType = formatType instruction.Result.Type
            let name suffix = target + "_" + suffix
            let items, cts, tasks, index, item, result, task = name "items", name "cts", name "tasks", name "i", name "item", name "result", name "task"
            writer.Line("{")
            writer.Push()
            writer.Line("var " + items + " = " + valueName sourceListValueId + ".Fold(new System.Collections.Generic.List<" + itemType + ">(), (" + name "acc" + ", " + name "x" + ") => { " + name "acc" + ".Add(" + name "x" + "); return " + name "acc" + "; });")
            writer.Line("using var " + cts + " = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(NyRuntime.Cancellation.Value);")
            writer.Line("var " + tasks + " = new System.Threading.Tasks.Task<" + workerResult + ">[" + items + ".Count];")
            writer.Line("for (var " + index + " = 0; " + index + " < " + items + ".Count; " + index + "++)")
            writer.Line("{")
            writer.Push()
            writer.Line("var " + item + " = " + items + "[" + index + "];")
            writer.Line(tasks + "[" + index + "] = System.Threading.Tasks.Task.Run(() =>")
            writer.Line("{")
            writer.Push()
            writer.Line("NyRuntime.Cancellation.Value = " + cts + ".Token;")
            writer.Line("try")
            writer.Line("{")
            writer.Push()
            writer.Line("var " + result + " = " + renderInvocation parametersOf workerFunctionId context [ item ] + ";")
            writer.Line("if (!" + result + ".IsOk) " + cts + ".Cancel();")
            writer.Line("return " + result + ";")
            writer.Pop()
            writer.Line("}")
            writer.Line("catch { " + cts + ".Cancel(); throw; }")
            writer.Pop()
            writer.Line("});")
            writer.Pop()
            writer.Line("}")
            writer.Line("foreach (var " + task + " in " + tasks + ") { try { " + task + ".Wait(); } catch (System.AggregateException) { } }")
            writer.Line("var " + name "faulted" + " = System.Array.Find(" + tasks + ", " + name "t" + " => " + name "t" + ".IsFaulted);")
            writer.Line("if (" + name "faulted" + " != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(" + name "faulted" + ".Exception!.InnerException!).Throw();")
            writer.Line("var " + name "failed" + " = System.Array.Find(" + tasks + ", " + name "t" + " => !" + name "t" + ".Result.IsOk);")
            writer.Line(target + " = " + name "failed" + " != null ? " + resultType + ".Err(" + name "failed" + ".Result.ErrorValue) : " + resultType + ".Ok(NyList<" + okType + ">.Create(System.Array.ConvertAll(" + tasks + ", " + name "t" + " => " + name "t" + ".Result.OkValue)));")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.ListMap(sourceListValueId, mapperFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".Map(" + callback mapperFunctionId context + ");")
        | ExecutionInstructionKind.ListFilter(sourceListValueId, predicateFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".Filter(" + callback predicateFunctionId context + ");")
        | ExecutionInstructionKind.ListAny(sourceListValueId, predicateFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".Any(" + callback predicateFunctionId context + ");")
        | ExecutionInstructionKind.ListAll(sourceListValueId, predicateFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".All(" + callback predicateFunctionId context + ");")
        | ExecutionInstructionKind.FunctionReference functionId ->
            writer.Line(target + " = " + CSharpBackendNames.functionName functionId + ";")
        | ExecutionInstructionKind.FunctionInvoke(value, arguments) ->
            writer.Line(target + " = " + valueName value + "(" + (arguments |> List.map valueName |> String.concat ", ") + ");")
        | ExecutionInstructionKind.DirectCall(functionId, arguments) ->
            let functionName = CSharpBackendNames.functionName functionId
            writer.Line(target + " = NyFunctions." + functionName + "(" + renderNamedCallArguments arguments + ");")
        | ExecutionInstructionKind.CapabilityInvoke(capabilityValueId, operationId, arguments) ->
            let methodName = CSharpBackendNames.capabilityOperationName operationId
            writer.Line(target + " = " + valueName capabilityValueId + "." + methodName + "(" + renderCapabilityArguments arguments + ");")
        | ExecutionInstructionKind.ProductConstruct fields ->
            match instruction.Result.Type with
            | TypeRef.Named typeId ->
                writer.Line(target + " = new " + CSharpBackendNames.typeName typeId + "(" + renderFieldArguments fields + ");")
            | _ -> invalidOp "ProductConstruct result does not carry a named product type."
        | ExecutionInstructionKind.FieldAccess(sourceValueId, fieldId) ->
            writer.Line(target + " = " + valueName sourceValueId + "." + CSharpBackendNames.fieldName fieldId + ";")
        | ExecutionInstructionKind.UnionConstruct(variantId, fields) ->
            writer.Line(target + " = new " + CSharpBackendNames.variantName variantId + "(" + renderFieldArguments fields + ");")
        | ExecutionInstructionKind.OptionNone ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".None();")
        | ExecutionInstructionKind.OptionSome sourceValueId ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".Some(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.ResultOk sourceValueId ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".Ok(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.ResultErr errorValueId ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".Err(" + valueName errorValueId + ");")
        | ExecutionInstructionKind.ExtractUnionField(inputValueId, variantId, fieldId) ->
            writer.Line(target + " = ((" + CSharpBackendNames.variantName variantId + ")" + valueName inputValueId + ")." + CSharpBackendNames.fieldName fieldId + ";")
        | ExecutionInstructionKind.ExtractOptionSome inputValueId ->
            writer.Line(target + " = " + valueName inputValueId + ".Value;")
        | ExecutionInstructionKind.ExtractResultOk inputValueId ->
            writer.Line(target + " = " + valueName inputValueId + ".OkValue;")
        | ExecutionInstructionKind.ExtractResultErr inputValueId ->
            writer.Line(target + " = " + valueName inputValueId + ".ErrorValue;")

    let private emitTerminator
        (writer: SourceWriter)
        (block: ExecutionBlock)
        (terminator: ExecutionTerminator) =
        match terminator with
        | ExecutionTerminator.Return resultValueId ->
            writer.Line("return " + valueName resultValueId + ";")
        | ExecutionTerminator.Jump(targetBlockId, arguments) ->
            for index, argument in arguments |> List.indexed do
                let temporary = CSharpBackendNames.jumpTemporaryName block.Id index
                writer.Line(temporary + " = " + valueName argument.ValueId + ";")
            for index, argument in arguments |> List.indexed do
                let temporary = CSharpBackendNames.jumpTemporaryName block.Id index
                writer.Line(valueName argument.TargetValueId + " = " + temporary + ";")
            writer.Line("goto " + CSharpBackendNames.executionBlockLabel targetBlockId + ";")
        | ExecutionTerminator.Branch(conditionValueId, whenTrue, whenFalse) ->
            writer.Line("if (" + valueName conditionValueId + ") goto " + CSharpBackendNames.executionBlockLabel whenTrue + ";")
            writer.Line("goto " + CSharpBackendNames.executionBlockLabel whenFalse + ";")
        | ExecutionTerminator.MatchUnion(inputValueId, targets) ->
            match targets |> List.sortBy (fun target -> StableId.value target.VariantId) with
            | [] -> writer.Line("throw new InvalidOperationException(\"Execution IR union match has no targets.\");")
            | orderedTargets ->
                for index, target in orderedTargets |> List.indexed do
                    let prefix = if index = 0 then "if" else "else if"
                    writer.Line(prefix + " (" + valueName inputValueId + " is " + CSharpBackendNames.variantName target.VariantId + ") goto " + CSharpBackendNames.executionBlockLabel target.BlockId + ";")
                writer.Line("else throw new InvalidOperationException(\"Execution IR union value did not match a declared variant.\");")
        | ExecutionTerminator.MatchOption(inputValueId, someBlock, noneBlock) ->
            writer.Line("if (" + valueName inputValueId + ".HasValue) goto " + CSharpBackendNames.executionBlockLabel someBlock + ";")
            writer.Line("goto " + CSharpBackendNames.executionBlockLabel noneBlock + ";")
        | ExecutionTerminator.MatchResult(inputValueId, okBlock, errBlock) ->
            writer.Line("if (" + valueName inputValueId + ".IsOk) goto " + CSharpBackendNames.executionBlockLabel okBlock + ";")
            writer.Line("goto " + CSharpBackendNames.executionBlockLabel errBlock + ";")

    let private emitFunction
        (writer: SourceWriter)
        (parametersOf: FunctionId -> (ParameterId * TypeRef) list)
        (operationResultOf: CapabilityOperationId -> TypeRef)
        (functionEntity: ExecutionFunction) =
        let parameters =
            functionEntity.Parameters
            |> List.map (fun parameter ->
                formatType parameter.Value.Type
                + " "
                + CSharpBackendNames.parameterName parameter.SemanticParameterId)
            |> String.concat ", "

        let typeParameters =
            functionEntity.Parameters
            |> List.collect (fun parameter -> TypeParameters.occurring parameter.Value.Type)
            |> List.distinctBy fst
            |> List.map (fst >> CSharpBackendNames.typeParameterName)
        let generic = if List.isEmpty typeParameters then "" else "<" + String.concat ", " typeParameters + ">"
        writer.Line("public static " + formatType functionEntity.ResultType + " " + CSharpBackendNames.functionName functionEntity.SemanticFunctionId + generic + "(" + parameters + ")")
        writer.Line("{")
        writer.Push()

        let parameterValues =
            functionEntity.Parameters
            |> List.map (fun parameter -> parameter.Value)

        let parameterValueIds = parameterValues |> List.map (fun value -> value.Id) |> Set.ofList

        let bodyValues =
            functionEntity.Blocks
            |> List.collect (fun block ->
                block.Parameters
                @ (block.Instructions |> List.map (fun instruction -> instruction.Result)))
            |> List.filter (fun value -> not (Set.contains value.Id parameterValueIds))
            |> List.distinctBy (fun value -> value.Id)
            |> List.sortBy (fun value -> ExecutionValueId.value value.Id)

        let allExecutionValues =
            parameterValues @ bodyValues
            |> List.distinctBy (fun value -> value.Id)
            |> List.sortBy (fun value -> ExecutionValueId.value value.Id)

        for value in allExecutionValues do
            writer.Line(formatType value.Type + " " + valueName value.Id + " = default!;")

        let valueTypes =
            allExecutionValues
            |> List.map (fun value -> value.Id, value.Type)
            |> Map.ofList

        let jumpTemporaries =
            functionEntity.Blocks
            |> List.sortBy (fun block -> ExecutionBlockId.value block.Id)
            |> List.collect (fun block ->
                match block.Terminator with
                | ExecutionTerminator.Jump(_, arguments) ->
                    arguments
                    |> List.indexed
                    |> List.map (fun (index, argument) ->
                        block.Id,
                        index,
                        Map.find argument.TargetValueId valueTypes)
                | _ -> [])

        for sourceBlockId, index, valueType in jumpTemporaries do
            writer.Line(formatType valueType + " " + CSharpBackendNames.jumpTemporaryName sourceBlockId index + " = default!;")

        for parameter in functionEntity.Parameters do
            writer.Line(valueName parameter.Value.Id + " = " + CSharpBackendNames.parameterName parameter.SemanticParameterId + ";")

        writer.Line("goto " + CSharpBackendNames.executionBlockLabel functionEntity.EntryBlockId + ";")
        writer.Line("")

        for block in functionEntity.Blocks |> List.sortBy (fun item -> ExecutionBlockId.value item.Id) do
            writer.Pop()
            writer.Line(CSharpBackendNames.executionBlockLabel block.Id + ":")
            writer.Push()
            for instruction in block.Instructions do
                emitInstruction writer parametersOf (fun valueId -> Map.find valueId valueTypes) operationResultOf instruction
            emitTerminator writer block block.Terminator
            writer.Line("")

        writer.Pop()
        writer.Line("}")
        writer.Line("")

    let private emitFunctions (writer: SourceWriter) (program: ExecutionProgram) =
        writer.Line("public static class " + CSharpBackendNames.functionContainerName)
        writer.Line("{")
        writer.Push()
        let parameters =
            program.Functions
            |> List.map (fun item -> item.SemanticFunctionId, item.Parameters |> List.map (fun parameter -> parameter.SemanticParameterId, parameter.Value.Type))
            |> Map.ofList
        let operationResults =
            program.Capabilities
            |> List.collect (fun capability -> capability.Operations |> List.map (fun operation -> operation.Id, operation.ResultType))
            |> Map.ofList
        for functionEntity in program.Functions |> List.sortBy (fun item -> StableId.value item.SemanticFunctionId) do
            emitFunction writer (fun functionId -> Map.find functionId parameters) (fun operationId -> Map.find operationId operationResults) functionEntity
        writer.Pop()
        writer.Line("}")

    let private symbol (id: 'Id) name : CSharpSymbol<'Id> =
        { Id = id; Name = name }

    let private symbolManifest
        (context: CSharpBackendValidation.Context)
        (program: ExecutionProgram) =
        let types = program.Types |> List.sortBy (fun item -> StableId.value item.Id)
        let fields =
            types
            |> List.collect (fun typeEntity ->
                match typeEntity.Kind with
                | ExecutionTypeKind.Product items -> items
                | ExecutionTypeKind.Union variants -> variants |> List.collect (fun variant -> variant.Fields))
            |> List.distinctBy (fun item -> StableId.value item.Id)
            |> List.sortBy (fun item -> StableId.value item.Id)
        let variants =
            types
            |> List.collect (fun typeEntity ->
                match typeEntity.Kind with
                | ExecutionTypeKind.Product _ -> []
                | ExecutionTypeKind.Union items -> items)
            |> List.sortBy (fun item -> StableId.value item.Id)
        let functions = program.Functions |> List.sortBy (fun item -> StableId.value item.SemanticFunctionId)
        let parameters =
            functions
            |> List.collect (fun functionEntity -> functionEntity.Parameters)
            |> List.sortBy (fun item -> StableId.value item.SemanticParameterId)
        let capabilities = program.Capabilities |> List.sortBy (fun item -> StableId.value item.Id)
        let operations =
            capabilities
            |> List.collect (fun capability -> capability.Operations)
            |> List.sortBy (fun item -> StableId.value item.Id)
        let capabilityParameters =
            operations
            |> List.collect (fun operation -> operation.Parameters)
            |> List.sortBy (fun item -> StableId.value item.Id)

        { Namespace = CSharpBackendNames.generatedNamespace
          FunctionContainer = CSharpBackendNames.functionContainerName
          Types = types |> List.map (fun item -> symbol item.Id (CSharpBackendNames.typeName item.Id))
          Fields = fields |> List.map (fun item -> symbol item.Id (CSharpBackendNames.fieldName item.Id))
          Variants = variants |> List.map (fun item -> symbol item.Id (CSharpBackendNames.variantName item.Id))
          Functions =
            functions
            |> List.map (fun item ->
                symbol item.SemanticFunctionId (CSharpBackendNames.functionName item.SemanticFunctionId))
          Parameters =
            parameters
            |> List.map (fun item ->
                symbol item.SemanticParameterId (CSharpBackendNames.parameterName item.SemanticParameterId))
          Capabilities =
            capabilities
            |> List.map (fun item -> symbol item.Id (CSharpBackendNames.capabilityName item.Id))
          CapabilityOperations =
            operations
            |> List.map (fun item -> symbol item.Id (CSharpBackendNames.capabilityOperationName item.Id))
          CapabilityParameters =
            capabilityParameters
            |> List.map (fun item -> symbol item.Id (CSharpBackendNames.capabilityParameterName item.Id))
          CapabilityAdapters =
            capabilities
            |> List.filter (hasCapabilityAdapter context)
            |> List.map (fun item -> symbol item.Id (CSharpBackendNames.capabilityAdapterName item.Id)) }

    let emit
        (program: ExecutionProgram)
        (profile: CSharpBackendProfile)
        : Result<CSharpArtifact, CSharpBackendError> =
        match CSharpBackendValidation.prepare program profile with
        | Error error -> Error error
        | Ok context ->
            let writer = SourceWriter()
            writer.Line("// Generated by Nyrwvy. Do not edit as authoritative source.")
            writer.Line("#nullable enable")
            writer.Line("#pragma warning disable CS0219 // Preserve Execution IR assignments even when their values are unused.")
            writer.Line("using System;")
            writer.Line("")
            writer.Line("namespace " + CSharpBackendNames.generatedNamespace + ";")
            writer.Line("")
            emitRuntime writer
            emitConcurrencyRuntime writer program
            emitFunctionTypes writer program
            emitTypes writer program
            emitCapabilities writer program
            emitAdapters writer context program
            emitFunctions writer program
            writer.Line("#pragma warning restore CS0219")
            Ok
                { Source = writer.ToString()
                  Symbols = symbolManifest context program }
