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

    /// The C# source type of a type reference, as emitted programs spell it. runPure's runner
    /// uses it to declare its argument and result values.
    let rec formatType (typeRef: TypeRef) =
        match typeRef with
        | TypeRef.Primitive PrimitiveType.String -> "NyStr"
        | TypeRef.Primitive PrimitiveType.Bool -> "bool"
        | TypeRef.Primitive PrimitiveType.Unit -> "NyUnit"
        | TypeRef.Primitive PrimitiveType.Int64 -> "long"
        | TypeRef.Primitive PrimitiveType.Float64 -> "double"
        | TypeRef.Primitive PrimitiveType.Bits32 -> "uint"
        | TypeRef.Primitive PrimitiveType.Bytes -> "NyBytes"
        | TypeRef.Named(typeId, arguments) -> CSharpBackendNames.typeName typeId + typeArgumentList arguments
        | TypeRef.Capability capabilityId -> CSharpBackendNames.capabilityName capabilityId
        | TypeRef.Function(arguments, result) ->
            "NyFunction" + string arguments.Length + "<" + (arguments @ [ result ] |> List.map formatType |> String.concat ", ") + ">"
        | TypeRef.TypeParameter typeParameterId -> CSharpBackendNames.typeParameterName typeParameterId
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ]) ->
            "NyOption<" + formatType itemType + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
            "NyResult<" + formatType okType + ", " + formatType errorType + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ keyType; valueType ]) ->
            "NyMap<" + formatType keyType + ", " + formatType valueType + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ itemType ]) ->
            "NySet<" + formatType itemType + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ]) ->
            "NyList<" + formatType itemType + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, [ resultType ]) ->
            "NyOneShot<" + formatType resultType + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ resultType ]) ->
            "NyTask<" + formatType resultType + ">"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Exception, []) -> "System.Runtime.ExceptionServices.ExceptionDispatchInfo"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, []) -> "NyTaskScope"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, arguments) ->
            invalidOp $"C# backend received OneShot with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Task, arguments) ->
            invalidOp $"C# backend received Task with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Exception, arguments) ->
            invalidOp $"Exception unexpectedly has {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, arguments) ->
            invalidOp $"C# backend received TaskScope with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, arguments) ->
            invalidOp $"C# backend received Option with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, arguments) ->
            invalidOp $"C# backend received Result with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Map, arguments) ->
            invalidOp $"C# backend received Map with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Set, arguments) ->
            invalidOp $"C# backend received Set with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, arguments) ->
            invalidOp $"C# backend received List with {arguments.Length} type arguments."


    /// `<A, B>` for the type arguments of a generic Product or Union; empty for a monomorphic one.
    and private typeArgumentList (arguments: TypeRef list) =
        if List.isEmpty arguments then "" else "<" + (arguments |> List.map formatType |> String.concat ", ") + ">"

    let private namedTypeArguments (typeRef: TypeRef) =
        match typeRef with
        | TypeRef.Named(_, arguments) -> arguments
        | _ -> []
    let private emitRuntime (writer: SourceWriter) =
        // Core is shared verbatim with the Zig emitter; the last LF already belongs to the resource.
        for line in CSharpBackendRuntime.coreRuntime do
            writer.Line(line)
        for line in CSharpBackendRuntime.listRuntime do
            writer.Line(line)
        writer.Line("")
        for line in CSharpBackendRuntime.bytesRuntime do
            writer.Line(line)
        writer.Line("")

    let private fieldParameter (field: ExecutionField) =
        formatType field.Type + " " + CSharpBackendNames.fieldName field.Id

    let private usesTaskTypes (program: ExecutionProgram) =
        // A declared field may hold a Task handle, so its record needs NyTask even when no function
        // of the program mentions tasks.
        program.Types
        |> List.exists (fun typeEntity ->
            match typeEntity.Kind with
            | ExecutionTypeKind.Product fields -> fields |> List.exists (fun field -> TaskTypes.mentions field.Type)
            | ExecutionTypeKind.Union variants ->
                variants |> List.exists (fun variant -> variant.Fields |> List.exists (fun field -> TaskTypes.mentions field.Type)))
        || program.Functions
        |> List.exists (fun functionEntity ->
            TaskTypes.mentions functionEntity.ResultType
            || functionEntity.Parameters |> List.exists (fun parameter -> TaskTypes.mentions parameter.Value.Type)
            || functionEntity.Blocks
               |> List.exists (fun block ->
                   block.Instructions
                   |> List.exists (fun instruction ->
                       TaskTypes.mentions instruction.Result.Type
                       || ExecutionInstructionKind.uses instruction.Kind |> List.contains ExecutionInstructionUse.TaskRuntime)))

    /// Whether the generated source contains NyRuntime and NyVirtualClock, so a runner can install a
    /// virtual clock without inspecting the source text.
    let usesConcurrencyRuntime (program: ExecutionProgram) =
        usesTaskTypes program
        || program.Functions
           |> List.exists (fun functionEntity ->
               functionEntity.Blocks
               |> List.exists (fun block ->
                   block.Instructions
                   |> List.exists (fun instruction ->
                       ExecutionInstructionKind.uses instruction.Kind |> List.contains ExecutionInstructionUse.ConcurrencyRuntime)))

    /// Ambient cancellation for ParallelAll workers and tasks, emitted only for programs that use
    /// concurrency so other generated programs are unchanged. External implementations read the same token.
    let private emitConcurrencyRuntime (writer: SourceWriter) (program: ExecutionProgram) =
        let usesTasks = usesTaskTypes program
        if usesConcurrencyRuntime program then
            for line in CSharpBackendRuntime.concurrencyRuntime do
                writer.Line(line)
        if usesTasks then
            for line in CSharpBackendRuntime.taskRuntime do
                writer.Line(line)

    let private emitFunctionTypes (writer: SourceWriter) (program: ExecutionProgram) =
        let rec arities = function
            | TypeRef.Function(arguments, result) -> arguments.Length :: List.collect arities (arguments @ [ result ])
            | TypeRef.BuiltinGeneric(_, arguments)
            | TypeRef.Named(_, arguments) -> List.collect arities arguments
            | TypeRef.Primitive _
            | TypeRef.Capability _
            | TypeRef.TypeParameter _ -> []
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

    /// Products emitted as readonly record structs, so a fold that rebuilds its state record each step
    /// does not allocate. A product that reaches itself through fields held by value (products, Option,
    /// Result, and type arguments of products) stays a class, since a struct cannot contain itself.
    /// shortcut: every non-cyclic product becomes a struct whatever its field count; add a size cutoff
    /// if copying a wide product regresses.
    let private structProducts (program: ExecutionProgram) =
        let products =
            program.Types
            |> List.choose (fun t ->
                match t.Kind with
                | ExecutionTypeKind.Product fields -> Some(t.Id, fields)
                | ExecutionTypeKind.Union _ -> None)
            |> Map.ofList
        let rec embedded (typeRef: TypeRef) =
            match typeRef with
            | TypeRef.Named(typeId, arguments) when products.ContainsKey typeId -> typeId :: List.collect embedded arguments
            | TypeRef.BuiltinGeneric((BuiltinGenericType.Option | BuiltinGenericType.Result), arguments) -> List.collect embedded arguments
            | _ -> []
        let reachesItself start =
            let rec visit seen pending =
                match pending with
                | [] -> false
                | typeId :: rest when typeId = start -> true
                | typeId :: rest when Set.contains typeId seen -> visit seen rest
                | typeId :: rest -> visit (Set.add typeId seen) ((products[typeId] |> List.collect (fun f -> embedded f.Type)) @ rest)
            visit Set.empty (products[start] |> List.collect (fun f -> embedded f.Type))
        products |> Map.keys |> Seq.filter (reachesItself >> not) |> Set.ofSeq

    let private emitTypes (writer: SourceWriter) (program: ExecutionProgram) =
        let structs = structProducts program
        for typeEntity in program.Types |> List.sortBy (fun item -> StableId.value item.Id) do
            // A generic Product or Union is a generic record; its variants share its type parameters.
            let parameterList =
                if List.isEmpty typeEntity.TypeParameters then ""
                else "<" + (typeEntity.TypeParameters |> List.map (fst >> CSharpBackendNames.typeParameterName) |> String.concat ", ") + ">"
            let typeName = CSharpBackendNames.typeName typeEntity.Id + parameterList
            match typeEntity.Kind with
            | ExecutionTypeKind.Product fields ->
                let parameters =
                    fields
                    |> List.sortBy (fun field -> StableId.value field.Id)
                    |> List.map fieldParameter
                    |> String.concat ", "
                let kind = if structs.Contains typeEntity.Id then "public readonly record struct " else "public sealed record "
                writer.Line(kind + typeName + "(" + parameters + ");")
            | ExecutionTypeKind.Union variants ->
                writer.Line("public abstract record " + typeName + ";")
                for variant in variants |> List.sortBy (fun item -> StableId.value item.Id) do
                    let parameters =
                        variant.Fields
                        |> List.sortBy (fun field -> StableId.value field.Id)
                        |> List.map fieldParameter
                        |> String.concat ", "
                    let variantName = CSharpBackendNames.variantName variant.Id + parameterList
                    writer.Line("public sealed record " + variantName + "(" + parameters + ") : " + typeName + ";")
            writer.Line("")

    let private capabilityParameter (parameter: ExecutionCapabilityParameter) =
        formatType parameter.Type + " " + CSharpBackendNames.capabilityParameterName parameter.Id

    let private operationParameterList (operation: ExecutionCapabilityOperation) =
        operation.Parameters |> List.map capabilityParameter |> String.concat ", "

    let private emitCapabilities (writer: SourceWriter) (program: ExecutionProgram) =
        for capability in program.Capabilities |> List.sortBy (fun item -> StableId.value item.Id) do
            let capabilityName = CSharpBackendNames.capabilityName capability.Id
            writer.Line("public interface " + capabilityName)
            writer.Line("{")
            writer.Push()
            for operation in capability.Operations |> List.sortBy (fun item -> StableId.value item.Id) do
                let parameters = operationParameterList operation
                let methodName = CSharpBackendNames.capabilityOperationName operation.Id
                writer.Line(formatType operation.ResultType + " " + methodName + "(" + parameters + ");")
            writer.Pop()
            writer.Line("}")
            writer.Line("")

    let private hasCapabilityAdapter
        (context: CSharpBackendValidation.Context)
        (capability: ExecutionCapability) =
        not (List.isEmpty capability.Operations)
        && capability.Operations
           |> List.forall (fun operation ->
               match Map.tryFind operation.Id context.BindingsByOperation with
               | Some CSharpCapabilityBindingKind.SystemConsoleWriteLine
               | Some(CSharpCapabilityBindingKind.Native _) -> true
               | Some CSharpCapabilityBindingKind.ExternalImplementation
               | None -> false)

    let private nativeType (value: CSharpNativeValue) =
        match value with
        | CSharpNativeValue.Integer(8, signed) -> if signed then "sbyte" else "byte"
        | CSharpNativeValue.Integer(16, signed) -> if signed then "short" else "ushort"
        | CSharpNativeValue.Integer(32, signed) -> if signed then "int" else "uint"
        | CSharpNativeValue.Integer(_, signed) -> if signed then "long" else "ulong"
        | CSharpNativeValue.Bool -> "byte"
        | CSharpNativeValue.IntBool -> "int"
        | CSharpNativeValue.Utf8 _
        | CSharpNativeValue.Handle _ -> "System.IntPtr"
        | CSharpNativeValue.Bytes -> "System.IntPtr pointer, nuint length"
        | CSharpNativeValue.Void -> "void"
        | CSharpNativeValue.Bits64 -> "ulong"
        | CSharpNativeValue.Float 32 -> "float"
        | CSharpNativeValue.Float _ -> "double"
        | CSharpNativeValue.Pointer -> "System.IntPtr"

    let private aggressiveInlining =
        "[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]"


    let private dllImport (library: string) (symbol: string) =
        "[System.Runtime.InteropServices.DllImport(" + escapeStringLiteral library + ", EntryPoint = " + escapeStringLiteral symbol
        + ", ExactSpelling = true, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]"

    /// Marshalling helpers shared by every table-bound adapter; only blittable types cross P/Invoke.
    let private emitNativeSupport (writer: SourceWriter) =
        writer.Line("internal static class NyNative")
        writer.Line("{")
        writer.Push()
        writer.Line("public static System.IntPtr Utf8(string text) => text.Contains('\\0') ? throw new System.ArgumentException(\"A String passed to native code contains U+0000.\") : System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(text);")
        writer.Line("public static void Free(System.IntPtr pointer) => System.Runtime.InteropServices.Marshal.FreeCoTaskMem(pointer);")
        writer.Line("public static System.IntPtr Copy(byte[] bytes) { var pointer = System.Runtime.InteropServices.Marshal.AllocCoTaskMem(System.Math.Max(1, bytes.Length)); System.Runtime.InteropServices.Marshal.Copy(bytes, 0, pointer, bytes.Length); return pointer; }")
        writer.Line("public static float Float32(double value) { var single = (float)value; return float.IsInfinity(single) && !double.IsInfinity(value) ? throw new System.OverflowException(\"A Float64 passed to native code is outside float's range.\") : single; }")
        writer.Line("public static System.IntPtr NotNull(System.IntPtr pointer, string symbol) => pointer != System.IntPtr.Zero ? pointer : throw new System.InvalidOperationException(symbol + \" returned null\");")
        writer.Line("public static NyStr ReadUtf8(System.IntPtr pointer, string symbol) => System.Runtime.InteropServices.Marshal.PtrToStringUTF8(NotNull(pointer, symbol))!;")
        // An out value is a plain local of the adapter method (stack memory, never moved by the GC), passed by
        // address: a by-ref `out` makes P/Invoke build a marshalling stub (about 5.5 ns a call), a pointer does not.
        writer.Line("public static System.IntPtr Address<T>(ref T local) where T : unmanaged => System.Runtime.CompilerServices.Unsafe.ByteOffset(ref System.Runtime.CompilerServices.Unsafe.NullRef<T>(), ref local);")
        // Kept out of line so an inlined adapter method carries no string building on its hot path.
        writer.Line("[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] public static string Failed<T>(string symbol, T code) => symbol + \" failed with \" + code;")
        writer.Pop()
        writer.Line("}")
        writer.Line("")

    let private emitNativeOperation
        (writer: SourceWriter)
        (capabilityName: string)
        (operation: ExecutionCapabilityOperation)
        (binding: CSharpNativeBinding) =
        let methodName = CSharpBackendNames.capabilityOperationName operation.Id
        let signature = "public " + formatType operation.ResultType + " " + methodName + "(" + operationParameterList operation + ")"
        match binding.Call with
        | None ->
            writer.Line(signature + " => throw new System.NotSupportedException(\"This operation is unavailable on the C# backend.\");")
        | Some call ->
            let externName = "N_" + methodName
            let freeName = "F_" + methodName
            let structName index = "S_" + methodName + "_" + string index
            let indexed = List.indexed call.Arguments
            let interop = "System.Runtime.InteropServices."
            // Blittable structs; Sequential layout gives C's natural alignment, Size pads a union tail.
            for index, argument in indexed do
                match argument with
                | CSharpNativeArgument.Struct(fields, size, _) ->
                    let sized = match size with Some bytes -> ", Size = " + string bytes | None -> ""
                    let fieldType = function
                        | CSharpNativeArgument.Parameter(_, value) | CSharpNativeArgument.Constant(_, value) | CSharpNativeArgument.SizeOf value -> nativeType value
                        | _ -> invalidOp "A struct field was validated to a scalar."
                    let declared = fields |> List.mapi (fun field item -> "public " + fieldType item + " f" + string field + ";") |> String.concat " "
                    writer.Line("[" + interop + "StructLayout(" + interop + "LayoutKind.Sequential" + sized + ")] private struct " + structName index + " { " + declared + " }")
                | _ -> ()
            let externParameters =
                indexed |> List.map (fun (index, argument) ->
                    let name = " a" + string index
                    match argument with
                    | CSharpNativeArgument.Self _ -> "System.IntPtr" + name
                    | CSharpNativeArgument.Parameter(_, CSharpNativeValue.Bytes) -> "System.IntPtr" + name + ", nuint n" + string index
                    | CSharpNativeArgument.Parameter(_, value) | CSharpNativeArgument.Constant(_, value) -> nativeType value + name
                    | CSharpNativeArgument.Reference(_, value) -> "ref " + nativeType value + name
                    | CSharpNativeArgument.Retained(_, CSharpNativeValue.Bytes) -> "System.IntPtr" + name + ", nuint n" + string index
                    | CSharpNativeArgument.Retained _ -> "System.IntPtr" + name
                    | CSharpNativeArgument.Out _ -> "System.IntPtr" + name
                    | CSharpNativeArgument.OutBytes _ -> "System.IntPtr" + name + ", nuint n" + string index + ", out nuint w" + string index
                    | CSharpNativeArgument.Struct(_, _, byPointer) -> (if byPointer then "ref " else "") + structName index + name
                    | CSharpNativeArgument.SizeOf _ -> invalidOp "sizeof was validated to a struct field.")
                |> String.concat ", "
            writer.Line(dllImport call.Library call.Symbol)
            writer.Line("private static extern " + nativeType call.Returns + " " + externName + "(" + externParameters + ");")
            match call.Returns with
            | CSharpNativeValue.Utf8(Some free) ->
                writer.Line(dllImport call.Library free)
                writer.Line("private static extern void " + freeName + "(System.IntPtr pointer);")
            | _ -> ()
            let needsCleanup =
                call.Arguments |> List.exists (function
                    | CSharpNativeArgument.Parameter(_, (CSharpNativeValue.Utf8 _ | CSharpNativeValue.Bytes))
                    | CSharpNativeArgument.OutBytes _ | CSharpNativeArgument.Retained _ -> true
                    | _ -> false)
            // Without cleanup the method is a handle check, the C call and a status check: inlined into the
            // caller (and the caller's guarded call, see CapabilityInvoke) it costs what a hand-written call does.
            if not needsCleanup then writer.Line(aggressiveInlining)
            writer.Line(signature)
            writer.Line("{")
            writer.Push()
            let handleClass typeRef =
                match typeRef with
                | TypeRef.Capability id -> CSharpBackendNames.capabilityAdapterName id
                | _ -> invalidOp "A native handle argument has a capability type."
            let parameterType id = (operation.Parameters |> List.find (fun parameter -> parameter.Id = id)).Type
            // Allocations are made inside try so a failure between them still frees the earlier ones.
            for index, argument in indexed do
                match argument with
                | CSharpNativeArgument.Parameter(_, CSharpNativeValue.Utf8 _) -> writer.Line("var s" + string index + " = System.IntPtr.Zero;")
                | CSharpNativeArgument.Parameter(_, CSharpNativeValue.Bytes)
                | CSharpNativeArgument.OutBytes _ -> writer.Line("var g" + string index + " = default(" + interop + "GCHandle);")
                | CSharpNativeArgument.Retained _ -> writer.Line("var k" + string index + " = System.IntPtr.Zero;")
                | _ -> ()
            if needsCleanup then
                writer.Line("try")
                writer.Line("{")
                writer.Push()
            // Every argument is converted into a local first; a released handle is cleared last, so a
            // conversion that throws never leaves the handle closed without the C call having run.
            let released = ResizeArray<string>()
            let local index expression =
                writer.Line("var a" + string index + " = " + expression + ";")
                "a" + string index
            let handleArgument index owner value release =
                if release then
                    writer.Line(owner + ".Open(" + value + ");")
                    released.Add("var a" + string index + " = " + owner + ".Release(" + value + ");")
                    "a" + string index
                else local index (owner + ".Open(" + value + ")")
            let convert value name =
                match value with
                | CSharpNativeValue.Integer _ -> "checked((" + nativeType value + ")" + name + ")"
                | CSharpNativeValue.Bits64 -> "unchecked((ulong)" + name + ")"
                | CSharpNativeValue.Float 32 -> "NyNative.Float32(" + name + ")"
                | CSharpNativeValue.Float _ -> name
                | CSharpNativeValue.Bool -> "(byte)(" + name + " ? 1 : 0)"
                | CSharpNativeValue.IntBool -> "(" + name + " ? 1 : 0)"
                | _ -> invalidOp "Only scalars convert inline."
            let constant (value: int64) native =
                match native with
                | CSharpNativeValue.Pointer -> "System.IntPtr.Zero"
                | _ -> "unchecked((" + nativeType native + ")(" + string value + "L))"
            let field structType = function
                | CSharpNativeArgument.Parameter(id, value) -> convert value (CSharpBackendNames.capabilityParameterName id)
                | CSharpNativeArgument.Constant(value, native) -> constant value native
                | CSharpNativeArgument.SizeOf value -> "checked((" + nativeType value + ")" + interop + "Marshal.SizeOf<" + structType + ">())"
                | _ -> invalidOp "A struct field was validated to a scalar."
            let arguments =
                indexed |> List.map (fun (index, argument) ->
                    match argument with
                    | CSharpNativeArgument.Self false ->
                        // `this` is the adapter already; read the handle once so a racing release cannot slip in between.
                        writer.Line("var a" + string index + " = _handle;")
                        writer.Line("if (a" + string index + " == System.IntPtr.Zero) throw new System.InvalidOperationException(\"Handle is not open\");")
                        "a" + string index
                    | CSharpNativeArgument.Self true -> handleArgument index capabilityName "this" true
                    | CSharpNativeArgument.Constant(value, native) -> constant value native
                    | CSharpNativeArgument.Out value ->
                        writer.Line(nativeType value + " a" + string index + " = default;")
                        "NyNative.Address(ref a" + string index + ")"
                    | CSharpNativeArgument.OutBytes(capacity, _) ->
                        writer.Line("var b" + string index + " = new byte[" + string capacity + "];")
                        writer.Line("g" + string index + " = " + interop + "GCHandle.Alloc(b" + string index + ", " + interop + "GCHandleType.Pinned);")
                        writer.Line("nuint w" + string index + ";")
                        "g" + string index + ".AddrOfPinnedObject(), (nuint)b" + string index + ".Length, out w" + string index
                    | CSharpNativeArgument.Struct(fields, _, byPointer) ->
                        let assignments =
                            fields |> List.mapi (fun position item -> "f" + string position + " = " + field (structName index) item)
                            |> String.concat ", "
                        (if byPointer then "ref " else "") + local index ("new " + structName index + " { " + assignments + " }")
                    | CSharpNativeArgument.SizeOf _ -> invalidOp "sizeof was validated to a struct field."
                    | CSharpNativeArgument.Retained(id, value) ->
                        let name = CSharpBackendNames.capabilityParameterName id
                        let i = string index
                        match value with
                        | CSharpNativeValue.Bytes ->
                            writer.Line("var d" + i + " = " + name + ".Span.ToArray();")
                            writer.Line("k" + i + " = NyNative.Copy(d" + i + ");")
                            "k" + i + ", (nuint)d" + i + ".Length"
                        | _ ->
                            writer.Line("k" + i + " = NyNative.Utf8(" + name + ");")
                            "k" + i
                    | CSharpNativeArgument.Reference(id, value) ->
                        let name = CSharpBackendNames.capabilityParameterName id
                        match value with
                        | CSharpNativeValue.Handle _ -> "ref " + local index (handleClass (parameterType id) + ".Open(" + name + ")")
                        | _ -> "ref " + local index (convert value name)
                    | CSharpNativeArgument.Parameter(id, value) ->
                        let name = CSharpBackendNames.capabilityParameterName id
                        match value with
                        | CSharpNativeValue.Utf8 _ ->
                            writer.Line("s" + string index + " = NyNative.Utf8(" + name + ");")
                            "s" + string index
                        | CSharpNativeValue.Bytes ->
                            writer.Line("g" + string index + " = " + interop + "GCHandle.Alloc(" + name + ".Span.ToArray(), " + interop + "GCHandleType.Pinned);")
                            "g" + string index + ".AddrOfPinnedObject(), (nuint)" + name + ".Span.Length"
                        | CSharpNativeValue.Handle release -> handleArgument index (handleClass (parameterType id)) name release
                        | CSharpNativeValue.Void | CSharpNativeValue.Pointer -> invalidOp "A void or pointer argument was validated away."
                        | _ -> local index (convert value name))
                |> String.concat ", "
            for line in released do writer.Line line
            let invocation = externName + "(" + arguments + ")"
            let okType, isResult =
                match operation.ResultType with
                | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ ok; _ ]) -> ok, true
                | other -> other, false
            if call.Returns = CSharpNativeValue.Void then writer.Line(invocation + ";")
            else writer.Line("var r = " + invocation + ";")
            let fail message =
                if isResult then "return " + formatType operation.ResultType + ".Err(" + message + ");"
                else "throw new System.InvalidOperationException(" + message + ");"
            let symbolText = escapeStringLiteral call.Symbol
            let maxBuffer = 16 * 1024 * 1024
            // Too small a buffer: the C function wrote the size it needs; run once more with that size.
            for index, argument in indexed do
                match argument with
                | CSharpNativeArgument.OutBytes(_, Some retry) ->
                    let i = string index
                    writer.Line("if (r == " + string retry + "L)")
                    writer.Line("{")
                    writer.Push()
                    writer.Line("if (w" + i + " > (nuint)" + string maxBuffer + ") " + fail (symbolText + " + \" asked for a buffer of \" + w" + i + " + \" bytes, above the limit\""))
                    writer.Line("g" + i + ".Free();")
                    writer.Line("b" + i + " = new byte[checked((int)w" + i + ")];")
                    writer.Line("g" + i + " = " + interop + "GCHandle.Alloc(b" + i + ", " + interop + "GCHandleType.Pinned);")
                    writer.Line("r = " + invocation + ";")
                    writer.Pop()
                    writer.Line("}")
                | _ -> ()
            // The library now points into the new copy; the one it held before is no longer referenced.
            for index, argument in indexed do
                match argument with
                | CSharpNativeArgument.Retained _ ->
                    let field = "_kept_" + methodName + "_" + string index
                    writer.Line("{ var previous = " + field + "; " + field + " = k" + string index + "; k" + string index + " = System.IntPtr.Zero; if (previous != System.IntPtr.Zero) NyNative.Free(previous); }")
                | _ -> ()
            let symbol = escapeStringLiteral call.Symbol
            match call.Failure with
            | None -> ()
            | Some failure ->
                let condition, message =
                    match failure with
                    | CSharpNativeFailure.NullPointer -> "r == System.IntPtr.Zero", symbol + " + \" returned null\""
                    | CSharpNativeFailure.Negative -> "r < 0", "NyNative.Failed(" + symbol + ", r)"
                    | CSharpNativeFailure.Nonzero -> "r != 0", "NyNative.Failed(" + symbol + ", r)"
                if isResult then writer.Line("if (" + condition + ") return " + formatType operation.ResultType + ".Err(" + message + ");")
                else writer.Line("if (" + condition + ") throw new System.InvalidOperationException(" + message + ");")
            for index, argument in indexed do
                match argument with
                | CSharpNativeArgument.OutBytes _ ->
                    let i = string index
                    writer.Line("if (w" + i + " > (nuint)b" + i + ".Length) " + fail (symbol + " + \" reported \" + w" + i + " + \" bytes written into a buffer of \" + b" + i + ".Length"))
                | _ -> ()
            let scalarValue native expression =
                match native with
                | CSharpNativeValue.Integer _ -> "checked((long)" + expression + ")"
                | CSharpNativeValue.Bits64 -> "unchecked((long)" + expression + ")"
                | CSharpNativeValue.Float _ -> "(double)" + expression
                | CSharpNativeValue.Bool | CSharpNativeValue.IntBool -> expression + " != 0"
                | CSharpNativeValue.Handle _ -> "new " + handleClass okType + "(NyNative.NotNull(" + expression + ", " + symbol + "))"
                | _ -> invalidOp "A native result was validated away."
            let outArgument =
                indexed |> List.tryPick (fun (index, argument) ->
                    match argument with
                    | CSharpNativeArgument.Out native -> Some(scalarValue native ("a" + string index))
                    | CSharpNativeArgument.OutBytes _ ->
                        let i = string index
                        Some("NyBytes.FromOwnedArray(new System.ReadOnlySpan<byte>(b" + i + ", 0, checked((int)w" + i + ")).ToArray())")
                    | _ -> None)
            let value =
                match outArgument, call.Returns, okType with
                | Some value, _, _ -> value
                | None, _, TypeRef.Primitive PrimitiveType.Unit -> "NyUnit.Value"
                | None, CSharpNativeValue.Utf8 free, _ ->
                    match free with
                    | Some _ ->
                        writer.Line("NyStr text;")
                        writer.Line("try { text = NyNative.ReadUtf8(r, " + symbol + "); }")
                        writer.Line("finally { if (r != System.IntPtr.Zero) " + freeName + "(r); }")
                        "text"
                    | None -> "NyNative.ReadUtf8(r, " + symbol + ")"
                | None, native, _ -> scalarValue native "r"
            writer.Line("return " + (if isResult then formatType operation.ResultType + ".Ok(" + value + ")" else value) + ";")
            if needsCleanup then
                writer.Pop()
                writer.Line("}")
                writer.Line("finally")
                writer.Line("{")
                writer.Push()
                for index, argument in indexed do
                    match argument with
                    | CSharpNativeArgument.Parameter(_, CSharpNativeValue.Utf8 _) -> writer.Line("NyNative.Free(s" + string index + ");")
                    | CSharpNativeArgument.Parameter(_, CSharpNativeValue.Bytes)
                    | CSharpNativeArgument.OutBytes _ -> writer.Line("if (g" + string index + ".IsAllocated) g" + string index + ".Free();")
                    | CSharpNativeArgument.Retained _ -> writer.Line("if (k" + string index + " != System.IntPtr.Zero) NyNative.Free(k" + string index + ");")
                    | _ -> ()
                writer.Pop()
                writer.Line("}")
            writer.Pop()
            writer.Line("}")

    let private emitAdapters
        (writer: SourceWriter)
        (context: CSharpBackendValidation.Context)
        (program: ExecutionProgram) =
        let isNative = function CSharpCapabilityBindingKind.Native _ -> true | _ -> false
        if context.BindingsByOperation |> Map.exists (fun _ kind -> isNative kind) then emitNativeSupport writer
        for capability in program.Capabilities |> List.sortBy (fun item -> StableId.value item.Id) do
            if hasCapabilityAdapter context capability then
                let capabilityName = CSharpBackendNames.capabilityName capability.Id
                let adapterName = CSharpBackendNames.capabilityAdapterName capability.Id
                writer.Line("public sealed class " + adapterName + " : " + capabilityName)
                writer.Line("{")
                writer.Push()
                let isHandle =
                    capability.Operations |> List.exists (fun operation ->
                        match Map.find operation.Id context.BindingsByOperation with
                        | CSharpCapabilityBindingKind.Native binding -> binding.Handle
                        | _ -> false)
                if isHandle then
                    // A handle value owns its pointer until the operation that releases it runs.
                    writer.Line("private System.IntPtr _handle;")
                    writer.Line("internal " + adapterName + "(System.IntPtr handle) { _handle = handle; }")
                    writer.Line(aggressiveInlining)
                    writer.Line("internal static System.IntPtr Open(" + capabilityName + " value) => value is " + adapterName
                                + " native && native._handle != System.IntPtr.Zero ? native._handle : throw new System.InvalidOperationException(\"Handle is not open\");")
                    // Exchange, so two racing releases cannot both pass the pointer to the C release function.
                    // Copies the library retains (a `retain` argument) live as long as the handle; release frees them.
                    let kept =
                        capability.Operations |> List.collect (fun operation ->
                            match Map.find operation.Id context.BindingsByOperation with
                            | CSharpCapabilityBindingKind.Native { Call = Some call } ->
                                call.Arguments |> List.indexed |> List.choose (fun (index, argument) ->
                                    match argument with
                                    | CSharpNativeArgument.Retained _ -> Some("_kept_" + CSharpBackendNames.capabilityOperationName operation.Id + "_" + string index)
                                    | _ -> None)
                            | _ -> [])
                    for field in kept do writer.Line("private System.IntPtr " + field + ";")
                    let freeKept =
                        kept |> List.map (fun field -> "NyNative.Free(System.Threading.Interlocked.Exchange(ref owner." + field + ", System.IntPtr.Zero)); ") |> String.concat ""
                    writer.Line("internal static System.IntPtr Release(" + capabilityName + " value) { var handle = value is " + adapterName
                                + " native ? System.Threading.Interlocked.Exchange(ref native._handle, System.IntPtr.Zero) : System.IntPtr.Zero; if (handle == System.IntPtr.Zero) throw new System.InvalidOperationException(\"Handle is not open\"); "
                                + (if kept.IsEmpty then "" else "var owner = (" + adapterName + ")value; " + freeKept) + "return handle; }")
                for operation in capability.Operations |> List.sortBy (fun item -> StableId.value item.Id) do
                  match Map.find operation.Id context.BindingsByOperation with
                  | CSharpCapabilityBindingKind.Native binding -> emitNativeOperation writer adapterName operation binding
                  | _ ->
                    let parameters = operationParameterList operation
                    let methodName = CSharpBackendNames.capabilityOperationName operation.Id
                    writer.Line("public " + formatType operation.ResultType + " " + methodName + "(" + parameters + ")")
                    writer.Line("{")
                    writer.Push()
                    match Map.find operation.Id context.BindingsByOperation with
                    | CSharpCapabilityBindingKind.Native _ -> invalidOp "Native operations are emitted above."
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

    let private emitProgramImplementations (writer: SourceWriter) (context: CSharpBackendValidation.Context) (program: ExecutionProgram) =
        for instruction in program.Functions |> List.collect ExecutionCallGraph.instructions |> List.sortBy (fun item -> StableId.value item.OriginNodeId) do
            for used in ExecutionInstructionKind.uses instruction.Kind do
                match used with
                | ExecutionInstructionUse.CapabilityImplementationClass(capabilityId, implementations) ->
                    let capability = Map.find capabilityId context.CapabilitiesById
                    let implementationName = CSharpBackendNames.capabilityImplementationName instruction.OriginNodeId
                    writer.Line("internal sealed class " + implementationName + " : " + CSharpBackendNames.capabilityName capabilityId)
                    writer.Line("{")
                    writer.Push()
                    for operation in capability.Operations |> List.sortBy (fun operation -> StableId.value operation.Id) do
                        let functionId = implementations |> List.find (fun (id, _) -> id = operation.Id) |> snd
                        let fn = Map.find functionId context.FunctionsById
                        let parameters = operationParameterList operation
                        // Declaration order defines the correspondence. Name each target parameter
                        // explicitly, independently of the operation parameter identities.
                        let arguments = List.zip fn.Parameters operation.Parameters |> List.map (fun (parameter, supplied) ->
                            CSharpBackendNames.parameterName parameter.SemanticParameterId + ": " + CSharpBackendNames.capabilityParameterName supplied.Id) |> String.concat ", "
                        writer.Line("public " + formatType operation.ResultType + " " + CSharpBackendNames.capabilityOperationName operation.Id + "(" + parameters + ") => "
                                    + CSharpBackendNames.functionContainerName + "." + CSharpBackendNames.functionName functionId + "(" + arguments + ");")
                    writer.Pop()
                    writer.Line("}")
                    writer.Line("")
                | ExecutionInstructionUse.Callback _
                | ExecutionInstructionUse.CapabilityOperation _
                | ExecutionInstructionUse.ConcurrencyRuntime
                | ExecutionInstructionUse.TaskRuntime -> ()

    let private valueName (valueId: ExecutionValueId) =
        CSharpBackendNames.executionValueName valueId

    let private callbackName (functionId: FunctionId) =
        CSharpBackendNames.functionContainerName + "." + CSharpBackendNames.functionName functionId

    let private renderNamedArguments
        (idOf: 'item -> StableId<'kind>)
        (nameOf: StableId<'kind> -> string)
        (valueIdOf: 'item -> ExecutionValueId)
        (items: 'item list) =
        items
        |> List.sortBy (idOf >> StableId.value)
        |> List.map (fun item -> nameOf (idOf item) + ": " + valueName (valueIdOf item))
        |> String.concat ", "

    let private renderNamedCallArguments arguments =
        renderNamedArguments (fun (argument: ExecutionCallArgument) -> argument.TargetParameterId) CSharpBackendNames.parameterName (fun argument -> argument.ValueId) arguments

    let private renderCapabilityArguments arguments =
        renderNamedArguments (fun (argument: ExecutionCapabilityArgument) -> argument.TargetCapabilityParameterId) CSharpBackendNames.capabilityParameterName (fun argument -> argument.ValueId) arguments

    let private renderFieldArguments fields =
        renderNamedArguments (fun (field: ExecutionFieldValue) -> field.TargetFieldId) CSharpBackendNames.fieldName (fun field -> field.ValueId) fields

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
    let private renderInvocationOf (parametersOf: FunctionId -> (ParameterId * TypeRef) list) (functionId: FunctionId) (context: (ParameterId * string) list) (iterationValues: string list) =
        let bound = context |> List.map fst |> Set.ofList
        let iteration = parametersOf functionId |> List.map fst |> List.filter (fun parameterId -> not (Set.contains parameterId bound))
        let arguments =
            (context |> List.map (fun (parameterId, value) -> CSharpBackendNames.parameterName parameterId + ": " + value))
            @ (List.map2 (fun parameterId value -> CSharpBackendNames.parameterName parameterId + ": " + value) iteration iterationValues)
        callbackName functionId + "(" + String.concat ", " arguments + ")"

    let private renderInvocation parametersOf functionId (context: ExecutionCallArgument list) iterationValues =
        renderInvocationOf parametersOf functionId (context |> List.map (fun argument -> argument.TargetParameterId, valueName argument.ValueId)) iterationValues

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
        (adapterOf: CapabilityId -> string option)
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
        | ExecutionInstructionKind.Int64ToHexString sourceValueId ->
            // Python format(value, "x"): lowercase digits of the magnitude with a leading '-' for negative
            // values; the unsigned negation also gives Int64.MinValue its magnitude 2^63.
            writer.Line("{")
            writer.Push()
            writer.Line("var number = " + valueName sourceValueId + ";")
            writer.Line(target + " = number < 0L ? \"-\" + unchecked((ulong)(-number)).ToString(\"x\", System.Globalization.CultureInfo.InvariantCulture) : number.ToString(\"x\", System.Globalization.CultureInfo.InvariantCulture);")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.StringParseInt64 sourceValueId ->
            // Only '-'? ASCII digits are accepted; TryParse then enforces the Int64 range.
            writer.Line("{")
            writer.Push()
            writer.Line("string text = " + valueName sourceValueId + ";")
            writer.Line("var start = text.Length > 0 && text[0] == '-' ? 1 : 0;")
            writer.Line("var digits = text.Length > start;")
            writer.Line("for (var index = start; digits && index < text.Length; index++) digits = text[index] >= '0' && text[index] <= '9';")
            writer.Line(target + " = digits && long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? NyOption<long>.Some(parsed) : NyOption<long>.None();")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.StringParseInt64Hex sourceValueId ->
            // Only '-'? followed by ASCII hex digits of either case is accepted. The magnitude is accumulated
            // as ulong, stopping before it could overflow, and must be at most 2^63 - 1, or 2^63 when negative.
            writer.Line("{")
            writer.Push()
            writer.Line("string text = " + valueName sourceValueId + ";")
            writer.Line("var negative = text.Length > 0 && text[0] == '-';")
            writer.Line("var start = negative ? 1 : 0;")
            writer.Line("var digits = text.Length > start;")
            writer.Line("var magnitude = 0UL;")
            writer.Line("for (var index = start; digits && index < text.Length; index++)")
            writer.Line("{")
            writer.Push()
            writer.Line("var c = text[index];")
            writer.Line("var digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;")
            writer.Line("digits = digit >= 0 && magnitude <= (ulong.MaxValue >> 4);")
            writer.Line("if (digits) magnitude = (magnitude << 4) + (ulong)digit;")
            writer.Pop()
            writer.Line("}")
            writer.Line(target + " = digits && magnitude <= (negative ? 9223372036854775808UL : 9223372036854775807UL) ? NyOption<long>.Some(negative ? unchecked(-(long)magnitude) : (long)magnitude) : NyOption<long>.None();")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.BoolNot sourceValueId ->
            writer.Line(target + " = !" + valueName sourceValueId + ";")
        | ExecutionInstructionKind.StringLength sourceValueId ->
            // Code points: a surrogate pair counts once, an unpaired surrogate code unit counts once.
            writer.Line(target + " = NyText.CodePointLength(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.StringToLower sourceValueId ->
            writer.Line(target + " = NyText.ToLower(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.StringToUpper sourceValueId ->
            writer.Line(target + " = NyText.ToUpper(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.StringDecodeUtf8Lossy sourceValueId ->
            writer.Line(target + " = NyText.DecodeUtf8Lossy(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.StringEncodeUtf8 sourceValueId ->
            writer.Line(target + " = NyText.EncodeUtf8(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.Float64RoundToInt64 sourceValueId ->
            writer.Line(target + " = NyNumber.RoundToInt64(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.Float64FloorToInt64 sourceValueId ->
            writer.Line(target + " = NyNumber.FloorToInt64(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.StringUtf8ByteCount sourceValueId ->
            writer.Line(target + " = NyText.Utf8ByteCount(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.Float64ToString sourceValueId ->
            writer.Line(target + " = NyText.FromFloat64(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.StringParseFloat64 sourceValueId ->
            writer.Line(target + " = NyText.ParseFloat64(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.StringFromCodePoint sourceValueId ->
            writer.Line(target + " = NyText.FromCodePoint(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.StringToCodePoint sourceValueId ->
            writer.Line(target + " = NyText.ToCodePoint(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.StringCodePoints sourceValueId ->
            // One String per code point, walked once: a surrogate pair stays together and an unpaired
            // surrogate code unit is its own element, as StringLength counts it.
            writer.Line("{")
            writer.Push()
            writer.Line("string text = " + valueName sourceValueId + ";")
            writer.Line("var codePoints = new System.Collections.Generic.List<NyStr>(text.Length);")
            writer.Line("for (var index = 0; index < text.Length; index++)")
            writer.Line("{")
            writer.Push()
            writer.Line("var width = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;")
            writer.Line("codePoints.Add(text.Substring(index, width));")
            writer.Line("index += width - 1;")
            writer.Pop()
            writer.Line("}")
            writer.Line(target + " = NyList<NyStr>.FromOwnedArray(codePoints.ToArray());")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.StringSlice(sourceValueId, startId, endId) ->
            // Clamp both code-point bounds to [0, length], then map them to UTF-16 offsets without splitting a pair.
            writer.Line("{")
            writer.Push()
            writer.Line("string text = " + valueName sourceValueId + ";")
            writer.Line("var start = System.Math.Max(0L, " + valueName startId + ");")
            writer.Line("var end = System.Math.Max(0L, " + valueName endId + ");")
            writer.Line("var startOffset = NyText.OffsetOf(text, start);")
            writer.Line("var endOffset = end > start ? NyText.OffsetOf(text, end) : startOffset;")
            writer.Line(target + " = endOffset > startOffset ? text.Substring(startOffset, endOffset - startOffset) : \"\";")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.StringContains(sourceValueId, substringId) ->
            writer.Line(target + " = NyText.Contains(" + valueName sourceValueId + ", " + valueName substringId + ");")
        | ExecutionInstructionKind.StringIndexOf(sourceValueId, substringId, fromId) ->
            writer.Line(target + " = NyText.IndexOf(" + valueName sourceValueId + ", " + valueName substringId + ", " + valueName fromId + ");")
        | ExecutionInstructionKind.StringSpan(sourceValueId, setId, fromId) ->
            writer.Line(target + " = NyText.Span(" + valueName sourceValueId + ", " + valueName setId + ", " + valueName fromId + ");")
        | ExecutionInstructionKind.StringSpanBack(sourceValueId, setId, endId) ->
            writer.Line(target + " = NyText.SpanBack(" + valueName sourceValueId + ", " + valueName setId + ", " + valueName endId + ");")
        | ExecutionInstructionKind.StringLastIndexOfBefore(sourceValueId, substringId, endId) ->
            writer.Line(target + " = NyText.LastIndexOfBefore(" + valueName sourceValueId + ", " + valueName substringId + ", " + valueName endId + ");")
        | ExecutionInstructionKind.StringLastIndexOf(sourceValueId, otherId) ->
            writer.Line(target + " = NyText.LastIndexOf(" + valueName sourceValueId + ", " + valueName otherId + ");")
        | ExecutionInstructionKind.StringStartsWith(sourceValueId, otherId) ->
            writer.Line(target + " = NyText.StartsWith(" + valueName sourceValueId + ", " + valueName otherId + ");")
        | ExecutionInstructionKind.StringEndsWith(sourceValueId, otherId) ->
            writer.Line(target + " = NyText.EndsWith(" + valueName sourceValueId + ", " + valueName otherId + ");")
        | ExecutionInstructionKind.StringSplit(sourceValueId, otherId) ->
            writer.Line(target + " = NyText.Split(" + valueName sourceValueId + ", " + valueName otherId + ");")
        | ExecutionInstructionKind.StringSplitLines(sourceValueId, breaksId) ->
            writer.Line(target + " = NyText.SplitLines(" + valueName sourceValueId + ", " + valueName breaksId + ");")
        | ExecutionInstructionKind.StringReplace(sourceValueId, patternId, replacementId) ->
            writer.Line(target + " = NyText.Replace(" + valueName sourceValueId + ", " + valueName patternId + ", " + valueName replacementId + ");")
        | ExecutionInstructionKind.StringTrim sourceValueId ->
            writer.Line(target + " = NyText.Trim(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.StringTrimStart sourceValueId ->
            writer.Line(target + " = NyText.TrimStart(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.StringTrimEnd sourceValueId ->
            writer.Line(target + " = NyText.TrimEnd(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.StringJoin(listValueId, separatorId) ->
            writer.Line(target + " = NyText.Join(" + valueName listValueId + ", " + valueName separatorId + ");")
        | ExecutionInstructionKind.StringEqual(leftValueId, rightValueId) ->
            writer.Line(target + " = string.Equals(" + valueName leftValueId + ", " + valueName rightValueId + ", StringComparison.Ordinal);")
        | ExecutionInstructionKind.StringLessThan(leftValueId, rightValueId) ->
            writer.Line(target + " = NyText.LessThan(" + valueName leftValueId + ", " + valueName rightValueId + ");")
        | ExecutionInstructionKind.ValueEqual(leftValueId, rightValueId) ->
            writer.Line(target + " = NyEquality.Equal(" + valueName leftValueId + ", " + valueName rightValueId + ");")
        | ExecutionInstructionKind.StringConcat(leftValueId, rightValueId) ->
            writer.Line(target + " = NyStr.Concat(" + valueName leftValueId + ", " + valueName rightValueId + ");")
        | ExecutionInstructionKind.Int64WrappingAdd(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "+" rightValueId true
        | ExecutionInstructionKind.Int64WrappingSubtract(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "-" rightValueId true
        | ExecutionInstructionKind.Int64WrappingMultiply(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "*" rightValueId true
        | ExecutionInstructionKind.Int64FloorDivide(leftValueId, rightValueId) ->
            // Floor division that never throws: divisor 0 yields 0 and MinValue / -1 wraps to MinValue.
            writer.Line("{")
            writer.Push()
            writer.Line("var dividend = " + valueName leftValueId + ";")
            writer.Line("var divisor = " + valueName rightValueId + ";")
            writer.Line("if (divisor == 0L) " + target + " = 0L;")
            writer.Line("else if (divisor == -1L) " + target + " = unchecked(-dividend);")
            writer.Line("else")
            writer.Line("{")
            writer.Push()
            writer.Line("var quotient = dividend / divisor;")
            writer.Line("if (dividend % divisor != 0L && (dividend < 0L) != (divisor < 0L)) quotient -= 1L;")
            writer.Line(target + " = quotient;")
            writer.Pop()
            writer.Line("}")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.Int64FloorModulo(leftValueId, rightValueId) ->
            // Floor modulo with the divisor's sign that never throws: divisor 0 yields the dividend and x % -1 is 0.
            writer.Line("{")
            writer.Push()
            writer.Line("var dividend = " + valueName leftValueId + ";")
            writer.Line("var divisor = " + valueName rightValueId + ";")
            writer.Line("if (divisor == 0L) " + target + " = dividend;")
            writer.Line("else if (divisor == -1L) " + target + " = 0L;")
            writer.Line("else")
            writer.Line("{")
            writer.Push()
            writer.Line("var remainder = dividend % divisor;")
            writer.Line("if (remainder != 0L && (remainder < 0L) != (divisor < 0L)) remainder += divisor;")
            writer.Line(target + " = remainder;")
            writer.Pop()
            writer.Line("}")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.Int64Min(leftValueId, rightValueId) ->
            writer.Line(target + " = System.Math.Min(" + valueName leftValueId + ", " + valueName rightValueId + ");")
        | ExecutionInstructionKind.Int64Max(leftValueId, rightValueId) ->
            writer.Line(target + " = System.Math.Max(" + valueName leftValueId + ", " + valueName rightValueId + ");")
        | ExecutionInstructionKind.Int64BitAnd(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "&" rightValueId false
        | ExecutionInstructionKind.Int64BitOr(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "|" rightValueId false
        | ExecutionInstructionKind.Int64BitXor(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "^" rightValueId false
        | ExecutionInstructionKind.Int64ShiftLeft(valueId, countValueId) ->
            // The count is clamped explicitly instead of relying on C#'s 6-bit masking of long shift counts.
            let count = valueName countValueId
            writer.Line(target + " = " + count + " <= 0L ? " + valueName valueId + " : " + count + " >= 64L ? 0L : " + valueName valueId + " << (int)" + count + ";")
        | ExecutionInstructionKind.Int64ShiftRight(valueId, countValueId) ->
            // Arithmetic shift with the count clamped explicitly: 64 or more leaves only the sign.
            let count = valueName countValueId
            let value = valueName valueId
            writer.Line(target + " = " + count + " <= 0L ? " + value + " : " + count + " >= 64L ? (" + value + " < 0L ? -1L : 0L) : " + value + " >> (int)" + count + ";")
        | ExecutionInstructionKind.Int64CheckedAdd(leftValueId, rightValueId) ->
            writer.Line(target + " = NyNumber.CheckedAdd(" + valueName leftValueId + ", " + valueName rightValueId + ");")
        | ExecutionInstructionKind.Int64CheckedSubtract(leftValueId, rightValueId) ->
            writer.Line(target + " = NyNumber.CheckedSubtract(" + valueName leftValueId + ", " + valueName rightValueId + ");")
        | ExecutionInstructionKind.Int64CheckedMultiply(leftValueId, rightValueId) ->
            writer.Line(target + " = NyNumber.CheckedMultiply(" + valueName leftValueId + ", " + valueName rightValueId + ");")
        | ExecutionInstructionKind.ListRange(startValueId, endValueId) ->
            // Python list(range(start, end)). end - start is taken as unsigned, so the full Int64 span is
            // counted without overflow; a list longer than an array can hold fails as allocation does.
            writer.Line("{")
            writer.Push()
            writer.Line("var first = " + valueName startValueId + ";")
            writer.Line("var last = " + valueName endValueId + ";")
            writer.Line("if (last <= first) " + target + " = NyList<long>.Empty();")
            writer.Line("else")
            writer.Line("{")
            writer.Push()
            writer.Line("var count = unchecked((ulong)(last - first));")
            writer.Line("if (count > (ulong)Array.MaxLength) throw new OutOfMemoryException();")
            writer.Line("var items = new long[(int)count];")
            writer.Line("for (var index = 0; index < items.Length; index++) items[index] = first + index;")
            writer.Line(target + " = NyList<long>.FromOwnedArray(items);")
            writer.Pop()
            writer.Line("}")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.StringRepeat(textValueId, countValueId) ->
            // Python text * count: "" for count <= 0; a result longer than a String can hold fails as allocation does.
            writer.Line("{")
            writer.Push()
            writer.Line("string text = " + valueName textValueId + ";")
            writer.Line("var count = " + valueName countValueId + ";")
            writer.Line("if (count <= 0L || text.Length == 0) " + target + " = \"\";")
            writer.Line("else if (count > int.MaxValue / text.Length) throw new OutOfMemoryException();")
            writer.Line("else " + target + " = new System.Text.StringBuilder(text.Length * (int)count).Insert(0, text, (int)count).ToString();")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.Float64Round(valueId, digitsValueId) ->
            writer.Line(target + " = NyNumber.Round(" + valueName valueId + ", " + valueName digitsValueId + ");")
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
        | ExecutionInstructionKind.Float64Min(leftValueId, rightValueId) ->
            // .NET Math.Min on double is IEEE 754-2019 minimum: NaN propagates and -0.0 < 0.0.
            writer.Line(target + " = System.Math.Min(" + valueName leftValueId + ", " + valueName rightValueId + ");")
        | ExecutionInstructionKind.Float64Max(leftValueId, rightValueId) ->
            // .NET Math.Max on double is IEEE 754-2019 maximum: NaN propagates and -0.0 < 0.0.
            writer.Line(target + " = System.Math.Max(" + valueName leftValueId + ", " + valueName rightValueId + ");")
        | ExecutionInstructionKind.Float64Subtract(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "-" rightValueId false
        | ExecutionInstructionKind.Float64Multiply(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "*" rightValueId false
        | ExecutionInstructionKind.Float64Divide(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "/" rightValueId false
        | ExecutionInstructionKind.Float64FloorModulo(leftValueId, rightValueId) ->
            writer.Line(target + " = NyNumber.FloorModulo(" + valueName leftValueId + ", " + valueName rightValueId + ");")
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
        | ExecutionInstructionKind.Int64ToFloat64 valueId ->
            // .NET converts long to double with IEEE 754 round to nearest, ties to even.
            writer.Line(target + " = (double)" + valueName valueId + ";")
        | ExecutionInstructionKind.Int64WrappingAbs valueId ->
            // Two's-complement negation wraps, so Int64.MinValue stays Int64.MinValue.
            writer.Line(target + " = " + valueName valueId + " < 0L ? unchecked(-" + valueName valueId + ") : " + valueName valueId + ";")
        | ExecutionInstructionKind.Int64BitNot valueId ->
            writer.Line(target + " = ~" + valueName valueId + ";")
        | ExecutionInstructionKind.Float64Sqrt valueId ->
            writer.Line(target + " = System.Math.Sqrt(" + valueName valueId + ");")
        | ExecutionInstructionKind.Float64Abs valueId ->
            writer.Line(target + " = System.Math.Abs(" + valueName valueId + ");")
        | ExecutionInstructionKind.Float64Negate valueId ->
            writer.Line(target + " = -" + valueName valueId + ";")
        | ExecutionInstructionKind.Float64Floor valueId ->
            writer.Line(target + " = System.Math.Floor(" + valueName valueId + ");")
        | ExecutionInstructionKind.Float64Ceiling valueId ->
            writer.Line(target + " = System.Math.Ceiling(" + valueName valueId + ");")
        | ExecutionInstructionKind.Float64Truncate valueId ->
            writer.Line(target + " = System.Math.Truncate(" + valueName valueId + ");")
        // Bits32 is a C# uint. Every operation is total: conversions keep the low 32 bits or read
        // them unsigned, Add wraps, rotation takes the amount floor-modulo 32 (amount & 31 in two's
        // complement), and shifts give the value for amount <= 0 and zero for amount >= 32.
        | ExecutionInstructionKind.Bits32FromInt64 valueId ->
            writer.Line(target + " = unchecked((uint)" + valueName valueId + ");")
        | ExecutionInstructionKind.Bits32ToInt64 valueId ->
            writer.Line(target + " = (long)" + valueName valueId + ";")
        | ExecutionInstructionKind.Bits32Not valueId ->
            writer.Line(target + " = ~" + valueName valueId + ";")
        | ExecutionInstructionKind.Bits32Xor(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "^" rightValueId false
        | ExecutionInstructionKind.Bits32And(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "&" rightValueId false
        | ExecutionInstructionKind.Bits32Or(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "|" rightValueId false
        | ExecutionInstructionKind.Bits32Add(leftValueId, rightValueId) ->
            emitBinaryExpression writer target leftValueId "+" rightValueId true
        | ExecutionInstructionKind.Bits32RotateRight(valueId, amountValueId) ->
            writer.Line(
                target + " = System.Numerics.BitOperations.RotateRight(" + valueName valueId + ", (int)(" + valueName amountValueId + " & 31L));"
            )
        | ExecutionInstructionKind.Bits32ShiftRight(valueId, amountValueId) ->
            let amount = valueName amountValueId
            writer.Line(target + " = " + amount + " <= 0L ? " + valueName valueId + " : " + amount + " >= 32L ? 0u : " + valueName valueId + " >> (int)" + amount + ";")
        | ExecutionInstructionKind.Bits32ShiftLeft(valueId, amountValueId) ->
            let amount = valueName amountValueId
            writer.Line(target + " = " + amount + " <= 0L ? " + valueName valueId + " : " + amount + " >= 32L ? 0u : " + valueName valueId + " << (int)" + amount + ";")
        // Bytes is NyBytes; every operation is total and returns a new value (see CSharpBackendRuntime.bytesRuntime).
        | ExecutionInstructionKind.BytesEmpty -> writer.Line(target + " = NyBytes.Empty();")
        | ExecutionInstructionKind.BytesFill(lengthId, valueId) ->
            writer.Line(target + " = NyBytes.Fill(" + valueName lengthId + ", " + valueName valueId + ");")
        | ExecutionInstructionKind.BytesLength valueId -> writer.Line(target + " = " + valueName valueId + ".Length;")
        | ExecutionInstructionKind.BytesGet(bytesId, indexId) ->
            writer.Line(target + " = " + valueName bytesId + ".Get(" + valueName indexId + ");")
        // emitFunction writes updates itself, with the owner token and the in-place decision.
        | ExecutionInstructionKind.BytesSet _
        | ExecutionInstructionKind.BytesAppend _ -> failwith "Bytes updates are emitted by emitFunction."
        | ExecutionInstructionKind.BytesConcat(leftId, rightId) ->
            writer.Line(target + " = " + valueName leftId + ".Concat(" + valueName rightId + ");")
        | ExecutionInstructionKind.BytesSlice(bytesId, startId, endId) ->
            writer.Line(target + " = " + valueName bytesId + ".Slice(" + valueName startId + ", " + valueName endId + ");")
        | ExecutionInstructionKind.BytesFromLatin1 valueId -> writer.Line(target + " = NyBytes.FromLatin1(" + valueName valueId + ");")
        | ExecutionInstructionKind.BytesToLatin1 valueId -> writer.Line(target + " = " + valueName valueId + ".ToLatin1();")
        | ExecutionInstructionKind.ListEmpty ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".Empty();")
        | ExecutionInstructionKind.SetEmpty ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".Empty();")
        | ExecutionInstructionKind.MapEmpty ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".Empty();")
        | ExecutionInstructionKind.ListConstruct itemValueIds ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".Create(" + renderListItems itemValueIds + ");")
        | ExecutionInstructionKind.ListLength listValueId ->
            writer.Line(target + " = " + valueName listValueId + ".Length;")
        | ExecutionInstructionKind.MapValues mapValueId ->
            writer.Line(target + " = " + valueName mapValueId + ".Values();")
        | ExecutionInstructionKind.MapKeys mapValueId ->
            writer.Line(target + " = " + valueName mapValueId + ".Keys();")
        | ExecutionInstructionKind.SetToList setValueId ->
            writer.Line(target + " = " + valueName setValueId + ".ToList();")
        | ExecutionInstructionKind.MapSize mapValueId ->
            writer.Line(target + " = " + valueName mapValueId + ".Size;")
        | ExecutionInstructionKind.SetFromList listValueId ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".FromList(" + valueName listValueId + ");")
        | ExecutionInstructionKind.SetSize setValueId ->
            writer.Line(target + " = " + valueName setValueId + ".Size;")
        | ExecutionInstructionKind.ListReverse listValueId ->
            writer.Line(target + " = " + valueName listValueId + ".Reverse();")
        | ExecutionInstructionKind.ListLast listValueId ->
            writer.Line(target + " = " + valueName listValueId + ".Last();")
        | ExecutionInstructionKind.ListDropLast listValueId ->
            writer.Line(target + " = " + valueName listValueId + ".DropLast();")
        | ExecutionInstructionKind.ListItemAt(listValueId, indexValueId) ->
            writer.Line(target + " = " + valueName listValueId + ".ItemAt(" + valueName indexValueId + ");")
        | ExecutionInstructionKind.MapRemove(mapValueId, keyValueId) ->
            writer.Line(target + " = " + valueName mapValueId + ".Remove(" + valueName keyValueId + ");")
        | ExecutionInstructionKind.SetInsert(setValueId, itemValueId) ->
            writer.Line(target + " = " + valueName setValueId + ".Insert(" + valueName itemValueId + ");")
        | ExecutionInstructionKind.SetRemove(setValueId, itemValueId) ->
            writer.Line(target + " = " + valueName setValueId + ".Remove(" + valueName itemValueId + ");")
        | ExecutionInstructionKind.SetUnion(leftSetValueId, rightSetValueId) ->
            writer.Line(target + " = " + valueName leftSetValueId + ".Union(" + valueName rightSetValueId + ");")
        | ExecutionInstructionKind.SetIntersection(leftSetValueId, rightSetValueId) ->
            writer.Line(target + " = " + valueName leftSetValueId + ".Intersection(" + valueName rightSetValueId + ");")
        | ExecutionInstructionKind.SetDifference(leftSetValueId, rightSetValueId) ->
            writer.Line(target + " = " + valueName leftSetValueId + ".Difference(" + valueName rightSetValueId + ");")
        | ExecutionInstructionKind.SetIsSubset(leftSetValueId, rightSetValueId) ->
            writer.Line(target + " = " + valueName leftSetValueId + ".IsSubsetOf(" + valueName rightSetValueId + ");")
        | ExecutionInstructionKind.MapUnion(leftMapValueId, rightMapValueId) ->
            writer.Line(target + " = " + valueName leftMapValueId + ".Union(" + valueName rightMapValueId + ");")
        | ExecutionInstructionKind.MapContainsKey(mapValueId, keyValueId) ->
            writer.Line(target + " = " + valueName mapValueId + ".ContainsKey(" + valueName keyValueId + ");")
        | ExecutionInstructionKind.SetContains(setValueId, itemValueId) ->
            writer.Line(target + " = " + valueName setValueId + ".Contains(" + valueName itemValueId + ");")
        | ExecutionInstructionKind.ListIndexOf(listValueId, itemValueId) ->
            writer.Line(target + " = " + valueName listValueId + ".IndexOf(" + valueName itemValueId + ");")
        | ExecutionInstructionKind.ListLastIndexOf(listValueId, itemValueId) ->
            writer.Line(target + " = " + valueName listValueId + ".LastIndexOf(" + valueName itemValueId + ");")
        | ExecutionInstructionKind.ListContains(listValueId, itemValueId) ->
            writer.Line(target + " = " + valueName listValueId + ".Contains(" + valueName itemValueId + ");")
        | ExecutionInstructionKind.MapGet(mapValueId, keyValueId) ->
            writer.Line(target + " = " + valueName mapValueId + ".Get(" + valueName keyValueId + ");")
        | ExecutionInstructionKind.OptionGetOrDefault(optionValueId, defaultValueId) ->
            let option = valueName optionValueId
            writer.Line(target + " = " + option + ".HasValue ? " + option + ".Value : " + valueName defaultValueId + ";")
        // The Option and Result accessors are one conditional (or one member read) over HasValue or IsOk,
        // with every operand already evaluated.
        | ExecutionInstructionKind.ResultGetOrDefault(resultValueId, defaultValueId) ->
            let result = valueName resultValueId
            writer.Line(target + " = " + result + ".IsOk ? " + result + ".OkValue : " + valueName defaultValueId + ";")
        | ExecutionInstructionKind.OptionOkOr(optionValueId, errorValueId) ->
            let option = valueName optionValueId
            let resultType = formatType instruction.Result.Type
            writer.Line(target + " = " + option + ".HasValue ? " + resultType + ".Ok(" + option + ".Value) : " + resultType + ".Err(" + valueName errorValueId + ");")
        | ExecutionInstructionKind.ResultOkValue resultValueId ->
            let result = valueName resultValueId
            let optionType = formatType instruction.Result.Type
            writer.Line(target + " = " + result + ".IsOk ? " + optionType + ".Some(" + result + ".OkValue) : " + optionType + ".None();")
        | ExecutionInstructionKind.ResultErrValue resultValueId ->
            let result = valueName resultValueId
            let optionType = formatType instruction.Result.Type
            writer.Line(target + " = " + result + ".IsOk ? " + optionType + ".None() : " + optionType + ".Some(" + result + ".ErrorValue);")
        | ExecutionInstructionKind.OptionIsSome optionValueId ->
            writer.Line(target + " = " + valueName optionValueId + ".HasValue;")
        | ExecutionInstructionKind.ResultIsOk resultValueId ->
            writer.Line(target + " = " + valueName resultValueId + ".IsOk;")
        | ExecutionInstructionKind.MapGetOrDefault(mapValueId, keyValueId, defaultValueId) ->
            writer.Line(target + " = " + valueName mapValueId + ".GetOrDefault(" + valueName keyValueId + ", " + valueName defaultValueId + ");")
        | ExecutionInstructionKind.ListSlice(listValueId, startId, endId) ->
            writer.Line(target + " = " + valueName listValueId + ".Slice(" + valueName startId + ", " + valueName endId + ");")
        | ExecutionInstructionKind.ListSetAt(listValueId, indexValueId, itemValueId) ->
            writer.Line(target + " = " + valueName listValueId + ".SetAt(" + valueName indexValueId + ", " + valueName itemValueId + ");")
        | ExecutionInstructionKind.ListInsertAt(listValueId, indexValueId, itemValueId) ->
            writer.Line(target + " = " + valueName listValueId + ".InsertAt(" + valueName indexValueId + ", " + valueName itemValueId + ");")
        | ExecutionInstructionKind.ListRemoveAt(listValueId, indexValueId) ->
            writer.Line(target + " = " + valueName listValueId + ".RemoveAt(" + valueName indexValueId + ");")
        | ExecutionInstructionKind.MapInsert(mapValueId, keyId, valueId) ->
            writer.Line(target + " = " + valueName mapValueId + ".Insert(" + valueName keyId + ", " + valueName valueId + ");")
        | ExecutionInstructionKind.ListAppendItem(listValueId, itemValueId) ->
            writer.Line(target + " = " + valueName listValueId + ".AppendItem(" + valueName itemValueId + ");")
        | ExecutionInstructionKind.ListConcat(leftListValueId, rightListValueId) ->
            writer.Line(target + " = " + valueName leftListValueId + ".Concat(" + valueName rightListValueId + ");")
        | ExecutionInstructionKind.ListFold(sourceListValueId, seedValueId, folderFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".Fold(" + valueName seedValueId + ", " + callback folderFunctionId context + ");")
        | ExecutionInstructionKind.ListScan(sourceListValueId, seedValueId, folderFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".Scan(" + valueName seedValueId + ", " + callback folderFunctionId context + ");")
        | ExecutionInstructionKind.MapFold(sourceMapValueId, seedValueId, folderFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceMapValueId + ".Fold(" + valueName seedValueId + ", " + callback folderFunctionId context + ");")
        | ExecutionInstructionKind.SetFold(sourceSetValueId, seedValueId, folderFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceSetValueId + ".Fold(" + valueName seedValueId + ", " + callback folderFunctionId context + ");")
        | ExecutionInstructionKind.ListEffectFold(sourceListValueId, seedValueId, folderFunctionId, context) ->
            if instruction.Result.Type = valueTypeOf seedValueId then
                writer.Line(target + " = " + valueName sourceListValueId + ".Fold(" + valueName seedValueId + ", " + callback folderFunctionId context + ");")
            else
                // Result accumulator: after the first Err the folder, and so its effects, are not called
                // again, and the loop ends there without visiting the remaining items.
                let state, item = target + "_state", target + "_item"
                let resultType = formatType instruction.Result.Type
                let step = renderInvocation parametersOf folderFunctionId context [ state + ".OkValue"; item ]
                writer.Line(target + " = " + valueName sourceListValueId + ".FoldWhile(" + resultType + ".Ok(" + valueName seedValueId + "), (" + state + ", " + item + ") => " + step + ", " + state + " => " + state + ".IsOk);")
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
        | ExecutionInstructionKind.ExceptionCatch(body, bodyContext, handler, handlerContext) ->
            let error = target + "_exception"
            writer.Line("try { " + target + " = " + renderInvocation parametersOf body bodyContext [ "NyUnit.Value" ] + "; }")
            // Allocation failure is fatal in every backend, so no handler sees it.
            writer.Line("catch (Exception " + error + ") when (" + error + " is not (OutOfMemoryException or InsufficientExecutionStackException)) { " + target + " = " + renderInvocation parametersOf handler handlerContext [ "System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(" + error + ")" ] + "; }")
        | ExecutionInstructionKind.ExceptionIsCancellation value ->
            writer.Line(target + " = " + valueName value + ".SourceException is System.OperationCanceledException;")
        | ExecutionInstructionKind.ExceptionMessage value ->
            writer.Line(target + " = (NyStr)" + valueName value + ".SourceException.Message;")
        | ExecutionInstructionKind.ExceptionRethrow(value, _) ->
            writer.Line(valueName value + ".Throw();")
            // Throw never returns; an assignment keeps C# definite-assignment analysis simple.
            writer.Line(target + " = default!;")
        | ExecutionInstructionKind.CancellationShield(bodyFunctionId, context) ->
            let previous = target + "_cancellation"
            writer.Line("{")
            writer.Push()
            writer.Line("var " + previous + " = NyRuntime.Cancellation.Value;")
            writer.Line("NyRuntime.Cancellation.Value = System.Threading.CancellationToken.None;")
            writer.Line("try { " + target + " = " + renderInvocation parametersOf bodyFunctionId context [ "NyUnit.Value" ] + "; }")
            writer.Line("finally { NyRuntime.Cancellation.Value = " + previous + "; }")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.TaskScope(bodyFunctionId, context) ->
            // Every exit of the body, including a host exception, cancels and awaits the scope's tasks.
            let scope, body = target + "_scope", target + "_body"
            writer.Line("{")
            writer.Push()
            writer.Line("var " + scope + " = new NyTaskScope(NyRuntime.Cancellation.Value);")
            writer.Line(formatType instruction.Result.Type + " " + body + ";")
            writer.Line("try { " + body + " = " + renderInvocation parametersOf bodyFunctionId context [ scope ] + "; }")
            writer.Line("catch { " + scope + ".Close(bodyThrew: true); throw; }")
            writer.Line(scope + ".Close(bodyThrew: false);")
            writer.Line(target + " = " + body + ";")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.TaskStart(scopeValueId, workerFunctionId, context, itemValueId) ->
            // The worker runs after this instruction, so its lambda must not capture value locals: a tail
            // call reassigns them for the next turn while the task may still be starting. Block-scoped
            // copies are fresh variables each time the block is entered, so each task keeps its own values.
            let captured index = target + "_c" + string index
            writer.Line("{")
            writer.Push()
            for index, argument in context |> List.indexed do
                writer.Line("var " + captured index + " = " + valueName argument.ValueId + ";")
            let item = target + "_item"
            writer.Line("var " + item + " = " + valueName itemValueId + ";")
            // Enter through consuming ref parameters: a Tier0 callback frame must not retain
            // by-value argument copies for the lifetime of a long-running worker.
            let copiedContext = context |> List.mapi (fun index argument -> argument.TargetParameterId, "ref " + captured index)
            let invocation = renderInvocationOf parametersOf workerFunctionId copiedContext [ "ref " + item ]
            writer.Line(target + " = " + valueName scopeValueId + ".Start(() => " + invocation + ");")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.TaskWait(tasksValueId, mode, limitValueId) ->
            let any = match mode with TaskWaitMode.Any -> "true" | TaskWaitMode.All -> "false"
            let limit = limitValueId |> Option.map valueName |> Option.defaultValue "NyOption<long>.None()"
            writer.Line(target + " = NyTasks.Wait(" + valueName tasksValueId + ", any: " + any + ", limit: " + limit + ");")
        | ExecutionInstructionKind.OneShotCreate(scope, valueType) ->
            writer.Line(target + " = " + valueName scope + ".CreateOneShot<" + formatType valueType + ">();")
        | ExecutionInstructionKind.OneShotComplete(cell, value) ->
            writer.Line(target + " = " + valueName cell + ".Complete(" + valueName value + ");")
        | ExecutionInstructionKind.OneShotWait(cell, limit) ->
            let limit = limit |> Option.map valueName |> Option.defaultValue "NyOption<long>.None()"
            writer.Line(target + " = " + valueName cell + ".Wait(" + limit + ");")
        | ExecutionInstructionKind.TaskCancel taskValueId ->
            writer.Line(valueName taskValueId + ".Cancel();")
            writer.Line(target + " = NyUnit.Value;")
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
            let items, cts, path, tasks, index, item, result = name "items", name "cts", name "path", name "tasks", name "i", name "item", name "result"
            writer.Line("{")
            writer.Push()
            writer.Line("var " + items + " = " + valueName sourceListValueId + ".Fold(new System.Collections.Generic.List<" + itemType + ">(), (" + name "acc" + ", " + name "x" + ") => { " + name "acc" + ".Add(" + name "x" + "); return " + name "acc" + "; });")
            writer.Line("using var " + cts + " = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(NyRuntime.Cancellation.Value);")
            // Scopes the workers create see the path above the workers' token.
            writer.Line("var " + path + " = new NyCancellationPath(" + cts + ".Token, NyCancellationPath.Of(NyRuntime.Cancellation.Value));")
            writer.Line("var " + tasks + " = new System.Threading.Tasks.Task<" + workerResult + ">[" + items + ".Count];")
            writer.Line("for (var " + index + " = 0; " + index + " < " + items + ".Count; " + index + "++)")
            writer.Line("{")
            writer.Push()
            writer.Line("var " + item + " = " + items + "[" + index + "];")
            writer.Line(tasks + "[" + index + "] = NyRuntime.Run(() =>")
            writer.Line("{")
            writer.Push()
            writer.Line("NyRuntime.Cancellation.Value = " + cts + ".Token;")
            writer.Line("NyRuntime.CancellationPath.Value = " + path + ";")
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
            writer.Line("NyRuntime.WaitAll(" + tasks + ");")
            writer.Line("var " + name "faulted" + " = System.Array.Find(" + tasks + ", " + name "t" + " => " + name "t" + ".IsFaulted);")
            writer.Line("if (" + name "faulted" + " != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(" + name "faulted" + ".Exception!.InnerException!).Throw();")
            writer.Line("var " + name "failed" + " = System.Array.Find(" + tasks + ", " + name "t" + " => !" + name "t" + ".Result.IsOk);")
            writer.Line(target + " = " + name "failed" + " != null ? " + resultType + ".Err(" + name "failed" + ".Result.ErrorValue) : " + resultType + ".Ok(NyList<" + okType + ">.Create(System.Array.ConvertAll(" + tasks + ", " + name "t" + " => " + name "t" + ".Result.OkValue)));")
            writer.Pop()
            writer.Line("}")
        | ExecutionInstructionKind.ListMap(sourceListValueId, mapperFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".Map(" + callback mapperFunctionId context + ");")
        | ExecutionInstructionKind.ListZipWith(leftListValueId, rightListValueId, combinerFunctionId, context) ->
            writer.Line(target + " = " + valueName leftListValueId + ".ZipWith(" + valueName rightListValueId + ", " + callback combinerFunctionId context + ");")
        | ExecutionInstructionKind.ListFlatMap(sourceListValueId, mapperFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".FlatMap(" + callback mapperFunctionId context + ");")
        | ExecutionInstructionKind.MapFromList(sourceListValueId, keyOfFunctionId, context) ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".FromList(" + valueName sourceListValueId + ", " + callback keyOfFunctionId context + ");")
        | ExecutionInstructionKind.ListFilter(sourceListValueId, predicateFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".Filter(" + callback predicateFunctionId context + ");")
        | ExecutionInstructionKind.MapFilter(sourceMapValueId, keepFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceMapValueId + ".Filter(" + callback keepFunctionId context + ");")
        | ExecutionInstructionKind.SetFilter(sourceSetValueId, predicateFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceSetValueId + ".Filter(" + callback predicateFunctionId context + ");")
        | ExecutionInstructionKind.MapMapValues(sourceMapValueId, mapperFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceMapValueId + ".MapValues(" + callback mapperFunctionId context + ");")
        | ExecutionInstructionKind.SetMap(sourceSetValueId, mapperFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceSetValueId + ".Map(" + callback mapperFunctionId context + ");")
        // Option and Result combinators use the same HasValue/Value and IsOk/OkValue/ErrorValue members
        // as MatchOption and MatchResult; the callback is invoked only in the matching case.
        | ExecutionInstructionKind.OptionMap(sourceValueId, mapperFunctionId, context) ->
            let source = valueName sourceValueId
            let resultType = formatType instruction.Result.Type
            writer.Line(target + " = " + source + ".HasValue ? " + resultType + ".Some(" + renderInvocation parametersOf mapperFunctionId context [ source + ".Value" ] + ") : " + resultType + ".None();")
        | ExecutionInstructionKind.OptionAndThen(sourceValueId, binderFunctionId, context) ->
            let source = valueName sourceValueId
            writer.Line(target + " = " + source + ".HasValue ? " + renderInvocation parametersOf binderFunctionId context [ source + ".Value" ] + " : " + formatType instruction.Result.Type + ".None();")
        | ExecutionInstructionKind.ResultMap(sourceValueId, mapperFunctionId, context) ->
            let source = valueName sourceValueId
            let resultType = formatType instruction.Result.Type
            writer.Line(target + " = " + source + ".IsOk ? " + resultType + ".Ok(" + renderInvocation parametersOf mapperFunctionId context [ source + ".OkValue" ] + ") : " + resultType + ".Err(" + source + ".ErrorValue);")
        | ExecutionInstructionKind.ResultMapErr(sourceValueId, mapperFunctionId, context) ->
            let source = valueName sourceValueId
            let resultType = formatType instruction.Result.Type
            writer.Line(target + " = " + source + ".IsOk ? " + resultType + ".Ok(" + source + ".OkValue) : " + resultType + ".Err(" + renderInvocation parametersOf mapperFunctionId context [ source + ".ErrorValue" ] + ");")
        | ExecutionInstructionKind.ResultAndThen(sourceValueId, binderFunctionId, context) ->
            let source = valueName sourceValueId
            writer.Line(target + " = " + source + ".IsOk ? " + renderInvocation parametersOf binderFunctionId context [ source + ".OkValue" ] + " : " + formatType instruction.Result.Type + ".Err(" + source + ".ErrorValue);")
        | ExecutionInstructionKind.ListSortBy(sourceListValueId, lessThanFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".SortBy(" + callback lessThanFunctionId context + ");")
        | ExecutionInstructionKind.ListMinBy(sourceListValueId, lessThanFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".MinBy(" + callback lessThanFunctionId context + ");")
        | ExecutionInstructionKind.ListMaxBy(sourceListValueId, lessThanFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".MaxBy(" + callback lessThanFunctionId context + ");")
        | ExecutionInstructionKind.ListAny(sourceListValueId, predicateFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".Any(" + callback predicateFunctionId context + ");")
        | ExecutionInstructionKind.ListFindIndex(sourceListValueId, predicateFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".FindIndex(" + callback predicateFunctionId context + ");")
        | ExecutionInstructionKind.ListAll(sourceListValueId, predicateFunctionId, context) ->
            writer.Line(target + " = " + valueName sourceListValueId + ".All(" + callback predicateFunctionId context + ");")
        | ExecutionInstructionKind.CapabilityConstruct _ ->
            writer.Line(target + " = new " + CSharpBackendNames.capabilityImplementationName instruction.OriginNodeId + "();")
        | ExecutionInstructionKind.FunctionReference functionId ->
            writer.Line(target + " = " + CSharpBackendNames.functionName functionId + ";")
        | ExecutionInstructionKind.FunctionInvoke(value, arguments) ->
            writer.Line(target + " = " + valueName value + "(" + (arguments |> List.map valueName |> String.concat ", ") + ");")
        | ExecutionInstructionKind.DirectCall(functionId, arguments) ->
            writer.Line(target + " = " + callbackName functionId + "(" + renderNamedCallArguments arguments + ");")
        | ExecutionInstructionKind.CapabilityInvoke(capabilityValueId, operationId, arguments) ->
            let methodName = CSharpBackendNames.capabilityOperationName operationId
            let receiver = valueName capabilityValueId
            let call value = value + "." + methodName + "(" + renderCapabilityArguments arguments + ")"
            match valueTypeOf capabilityValueId with
            | TypeRef.Capability id when (adapterOf id).IsSome ->
                // The generated adapter is sealed: test for it, so the call is direct and inlines without runtime
                // profile data (a caller with stackalloc is compiled once, fully optimized, and never profiled).
                let adapter = "a_" + target
                writer.Line(target + " = " + receiver + " is " + Option.get (adapterOf id) + " " + adapter + " ? " + call adapter + " : " + call receiver + ";")
            | _ -> writer.Line(target + " = " + call receiver + ";")
        | ExecutionInstructionKind.ProductConstruct fields ->
            match instruction.Result.Type with
            | TypeRef.Named _ as productType ->
                writer.Line(target + " = new " + formatType productType + "(" + renderFieldArguments fields + ");")
            | _ -> invalidOp "ProductConstruct result does not carry a named product type."
        | ExecutionInstructionKind.FieldAccess(sourceValueId, fieldId) ->
            writer.Line(target + " = " + valueName sourceValueId + "." + CSharpBackendNames.fieldName fieldId + ";")
        | ExecutionInstructionKind.ProductUpdate(sourceValueId, fields) ->
            let assignments =
                fields
                |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
                |> List.map (fun field -> CSharpBackendNames.fieldName field.TargetFieldId + " = " + valueName field.ValueId)
                |> String.concat ", "
            writer.Line(target + " = " + valueName sourceValueId + " with { " + assignments + " };")
        | ExecutionInstructionKind.UnionConstruct(variantId, fields) ->
            writer.Line(target + " = new " + CSharpBackendNames.variantName variantId + typeArgumentList (namedTypeArguments instruction.Result.Type) + "(" + renderFieldArguments fields + ");")
        | ExecutionInstructionKind.OptionNone ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".None();")
        | ExecutionInstructionKind.OptionSome sourceValueId ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".Some(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.ResultOk sourceValueId ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".Ok(" + valueName sourceValueId + ");")
        | ExecutionInstructionKind.ResultErr errorValueId ->
            writer.Line(target + " = " + formatType instruction.Result.Type + ".Err(" + valueName errorValueId + ");")
        | ExecutionInstructionKind.ExtractUnionField(inputValueId, variantId, fieldId) ->
            writer.Line(target + " = ((" + CSharpBackendNames.variantName variantId + typeArgumentList (namedTypeArguments (valueTypeOf inputValueId)) + ")" + valueName inputValueId + ")." + CSharpBackendNames.fieldName fieldId + ";")
        | ExecutionInstructionKind.ExtractOptionSome inputValueId ->
            writer.Line(target + " = " + valueName inputValueId + ".Value;")
        | ExecutionInstructionKind.ExtractResultOk inputValueId ->
            writer.Line(target + " = " + valueName inputValueId + ".OkValue;")
        | ExecutionInstructionKind.ExtractResultErr inputValueId ->
            writer.Line(target + " = " + valueName inputValueId + ".ErrorValue;")

    /// The two-phase copy of a jump or tail call: every source is read into a temporary before
    /// any target is written, so targets that feed one another (`f(a: b, b: a)`) swap correctly.
    let private emitParallelAssignment (writer: SourceWriter) (blockId: ExecutionBlockId) (assignments: (ExecutionValueId * ExecutionValueId) list) =
        for index, (_, sourceValueId) in assignments |> List.indexed do
            writer.Line(CSharpBackendNames.jumpTemporaryName blockId index + " = " + valueName sourceValueId + ";")
        for index, (targetValueId, _) in assignments |> List.indexed do
            writer.Line(valueName targetValueId + " = " + CSharpBackendNames.jumpTemporaryName blockId index + ";")

    /// The parameter value local each tail-call argument assigns, in argument order.
    let private tailCallAssignments (functionEntity: ExecutionFunction) (arguments: ExecutionCallArgument list) =
        arguments
        |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId)
        |> List.map (fun argument ->
            let parameter =
                functionEntity.Parameters
                |> List.find (fun parameter -> parameter.SemanticParameterId = argument.TargetParameterId)
            parameter.Value, argument.ValueId)

    let private emitTerminator
        (functionEntity: ExecutionFunction)
        (valueTypeOf: ExecutionValueId -> TypeRef)
        (writer: SourceWriter)
        (block: ExecutionBlock)
        (terminator: ExecutionTerminator) =
        let resultType = functionEntity.ResultType
        match terminator with
        | ExecutionTerminator.TailCall(_, arguments) ->
            // The prologue copied the C# parameters into these value locals once; reassigning them and
            // jumping back to the entry block reruns the body without a new stack frame.
            tailCallAssignments functionEntity arguments
            |> List.map (fun (parameterValue, sourceValueId) -> parameterValue.Id, sourceValueId)
            |> emitParallelAssignment writer block.Id
            writer.Line("goto " + CSharpBackendNames.executionBlockLabel functionEntity.EntryBlockId + ";")
        | ExecutionTerminator.Abort(_, errorValueId) ->
            writer.Line("return " + formatType resultType + ".Err(" + valueName errorValueId + ");")
        | ExecutionTerminator.Return resultValueId ->
            writer.Line("return " + valueName resultValueId + ";")
        | ExecutionTerminator.Jump(targetBlockId, arguments) ->
            arguments
            |> List.map (fun argument -> argument.TargetValueId, argument.ValueId)
            |> emitParallelAssignment writer block.Id
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
                    writer.Line(prefix + " (" + valueName inputValueId + " is " + CSharpBackendNames.variantName target.VariantId + typeArgumentList (namedTypeArguments (valueTypeOf inputValueId)) + ") goto " + CSharpBackendNames.executionBlockLabel target.BlockId + ";")
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
        (adapterOf: CapabilityId -> string option)
        (consumeCaptures: bool)
        (functionEntity: ExecutionFunction) =
        let parameters =
            functionEntity.Parameters
            |> List.map (fun parameter ->
                (if consumeCaptures then "ref " else "")
                + formatType parameter.Value.Type
                + " "
                + CSharpBackendNames.parameterName parameter.SemanticParameterId)
            |> String.concat ", "

        // The generic method's type parameters follow the function's declared order.
        let typeParameters =
            functionEntity.TypeParameters
            |> List.map (fst >> CSharpBackendNames.typeParameterName)
        let generic = if List.isEmpty typeParameters then "" else "<" + String.concat ", " typeParameters + ">"
        let visibility = if consumeCaptures then "private" else "public"
        // A forwarder to a generated adapter (one block, a few instructions) inlines into its caller, so the
        // adapter test in CapabilityInvoke and the adapter method itself inline there too.
        let forwardsToAdapter =
            match functionEntity.Blocks with
            | [ block ] when List.length block.Instructions <= 4 ->
                block.Instructions |> List.exists (fun instruction ->
                    match instruction.Kind with
                    | ExecutionInstructionKind.CapabilityInvoke(capabilityValueId, _, _) ->
                        block.Parameters @ (block.Instructions |> List.map (fun item -> item.Result)) @ (functionEntity.Parameters |> List.map (fun item -> item.Value))
                        |> List.exists (fun value -> value.Id = capabilityValueId && (match value.Type with TypeRef.Capability id -> (adapterOf id).IsSome | _ -> false))
                    | _ -> false)
            | _ -> false
        if forwardsToAdapter then writer.Line(aggressiveInlining)
        writer.Line(visibility + " static " + formatType functionEntity.ResultType + " " + CSharpBackendNames.functionName functionEntity.SemanticFunctionId + generic + "(" + parameters + ")")
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
                | ExecutionTerminator.TailCall(_, arguments) ->
                    tailCallAssignments functionEntity arguments
                    |> List.indexed
                    |> List.map (fun (index, (parameterValue, _)) -> block.Id, index, parameterValue.Type)
                | ExecutionTerminator.Abort _
                | ExecutionTerminator.Return _
                | ExecutionTerminator.Branch _
                | ExecutionTerminator.MatchUnion _
                | ExecutionTerminator.MatchOption _
                | ExecutionTerminator.MatchResult _ -> [])

        for sourceBlockId, index, valueType in jumpTemporaries do
            writer.Line(formatType valueType + " " + CSharpBackendNames.jumpTemporaryName sourceBlockId index + " = default!;")

        for parameter in functionEntity.Parameters do
            writer.Line(valueName parameter.Value.Id + " = " + CSharpBackendNames.parameterName parameter.SemanticParameterId + ";")

        // The invocation's owner token for in-place Bytes updates; set before the entry label, so a tail
        // self-call keeps it and later loop turns keep writing in place.
        // shortcut: a ListFold folder is its own invocation, so an accumulator it updates copies on every
        // call (quadratic build); share the caller's token with fold-driven calls when a consumer needs it.
        let bytesOwnership = ExecutionIrBytesOwnership.plan functionEntity
        let bytesOwner = "nyBytesOwner"
        let updatesBytes =
            functionEntity.Blocks
            |> List.exists (fun block ->
                block.Instructions
                |> List.exists (fun instruction ->
                    match instruction.Kind with
                    | ExecutionInstructionKind.BytesSet _
                    | ExecutionInstructionKind.BytesAppend _ -> true
                    | _ -> false))
        if updatesBytes then writer.Line("object " + bytesOwner + " = new();")
        let releaseBytes (valueIds: ExecutionValueId list) =
            for valueId in valueIds do writer.Line(valueName valueId + ".ReleaseOwner();")

        // The ref overload is used only by TaskStart with fresh, distinct snapshot slots.
        // Clear those slots after all values reach the loop locals, before any worker code runs.
        if consumeCaptures then
            for parameter in functionEntity.Parameters do
                writer.Line(CSharpBackendNames.parameterName parameter.SemanticParameterId + " = default!;")

        writer.Line("goto " + CSharpBackendNames.executionBlockLabel functionEntity.EntryBlockId + ";")
        writer.Line("")

        for block in functionEntity.Blocks |> List.sortBy (fun item -> ExecutionBlockId.value item.Id) do
            writer.Pop()
            writer.Line(CSharpBackendNames.executionBlockLabel block.Id + ":")
            writer.Push()
            for instruction in block.Instructions do
                let inPlace = bytesOwnership |> Option.exists (fun plan -> Set.contains instruction.Result.Id plan.InPlace)
                let flag = if inPlace then "true" else "false"
                bytesOwnership |> Option.bind (fun plan -> Map.tryFind instruction.Result.Id plan.Escapes) |> Option.iter releaseBytes
                match instruction.Kind with
                | ExecutionInstructionKind.BytesSet(bytesId, indexId, valueId) ->
                    writer.Line(valueName instruction.Result.Id + " = " + valueName bytesId + ".Set(" + valueName indexId + ", " + valueName valueId + ", " + bytesOwner + ", " + flag + ");")
                | ExecutionInstructionKind.BytesAppend(bytesId, valueId) ->
                    writer.Line(valueName instruction.Result.Id + " = " + valueName bytesId + ".Append(" + valueName valueId + ", " + bytesOwner + ", " + flag + ");")
                | _ -> emitInstruction writer parametersOf (fun valueId -> Map.find valueId valueTypes) operationResultOf adapterOf instruction
            bytesOwnership |> Option.bind (fun plan -> Map.tryFind block.Id plan.TailCallReleases) |> Option.iter releaseBytes
            emitTerminator functionEntity (fun valueId -> Map.find valueId valueTypes) writer block block.Terminator
            writer.Line("")

        writer.Pop()
        writer.Line("}")
        writer.Line("")

    let private emitFunctions (writer: SourceWriter) (context: CSharpBackendValidation.Context) (program: ExecutionProgram) =
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
        let taskWorkers =
            program.Functions
            |> List.collect (fun fn -> fn.Blocks |> List.collect (fun block -> block.Instructions))
            |> List.choose (fun instruction ->
                match instruction.Kind with
                | ExecutionInstructionKind.TaskStart(_, workerFunctionId, _, _) -> Some workerFunctionId
                | _ -> None)
            |> Set.ofList
        let adapters =
            program.Capabilities
            |> List.filter (hasCapabilityAdapter context)
            |> List.map (fun capability -> capability.Id, CSharpBackendNames.capabilityAdapterName capability.Id)
            |> Map.ofList
        let adapterOf id = Map.tryFind id adapters
        for functionEntity in program.Functions |> List.sortBy (fun item -> StableId.value item.SemanticFunctionId) do
            emitFunction writer (fun functionId -> Map.find functionId parameters) (fun operationId -> Map.find operationId operationResults) adapterOf false functionEntity
            if Set.contains functionEntity.SemanticFunctionId taskWorkers then
                emitFunction writer (fun functionId -> Map.find functionId parameters) (fun operationId -> Map.find operationId operationResults) adapterOf true functionEntity
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
            emitProgramImplementations writer context program
            emitFunctions writer context program
            writer.Line("#pragma warning restore CS0219")
            Ok
                { Source = writer.ToString()
                  Symbols = symbolManifest context program }
