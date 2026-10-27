namespace Nyrwvy.Core

open System
open System.Collections.Generic
open System.Text

/// Emits Zig source from Execution IR. The supported slice covers Int64, Bool, Unit and String
/// values, Option, Result, List and declared Product and Union types (generic ones included),
/// their operations, list callbacks, direct calls and every block terminator; anything else is
/// reported as unsupported instead of emitted. Generic functions and types are instantiated per
/// use (monomorphized), since a Zig function cannot infer a type parameter from a runtime value.
[<RequireQualifiedAccess>]
module ZigBackend =
    let private identifier (prefix: string) (raw: string) =
        let builder = StringBuilder(prefix)
        for c in raw do builder.Append(if Char.IsAsciiLetterOrDigit c then c else '_') |> ignore
        builder.ToString()

    let private valueName (valueId: ExecutionValueId) = identifier "v_" (ExecutionValueId.value valueId)
    let private blockName (blockId: ExecutionBlockId) = identifier "b_" (ExecutionBlockId.value blockId)
    let private parameterName (parameterId: ParameterId) = identifier "p_" (StableId.value parameterId)
    let private fieldName (fieldId: FieldId) = identifier "f_" (StableId.value fieldId)
    let private variantName (variantId: VariantId) = identifier "c_" (StableId.value variantId)

    /// A short stable suffix for one list of type arguments.
    let private instanceSuffix (arguments: TypeRef list) =
        if List.isEmpty arguments then ""
        else "__" + Convert.ToHexString(Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sprintf "%A" arguments))).Substring(0, 12)

    let private instanceName (functionId: FunctionId) (arguments: TypeRef list) =
        identifier "f_" (StableId.value functionId) + instanceSuffix arguments

    /// The Zig name of a non-generic function.
    let functionName (functionId: FunctionId) = instanceName functionId []

    let private typeName (typeId: TypeId) (arguments: TypeRef list) = identifier "T_" (StableId.value typeId) + instanceSuffix arguments

    let rec private substitute (map: Map<TypeParameterId, TypeRef>) (typeRef: TypeRef) =
        match typeRef with
        | TypeRef.TypeParameter id -> Map.tryFind id map |> Option.defaultValue typeRef
        | TypeRef.Named(id, arguments) -> TypeRef.Named(id, List.map (substitute map) arguments)
        | TypeRef.BuiltinGeneric(kind, arguments) -> TypeRef.BuiltinGeneric(kind, List.map (substitute map) arguments)
        | TypeRef.Function(parameters, result) -> TypeRef.Function(List.map (substitute map) parameters, substitute map result)
        | other -> other

    /// Binds the type parameters in `pattern` so that it matches `actual`.
    let rec private unify (pattern: TypeRef) (actual: TypeRef) (bound: Map<TypeParameterId, TypeRef>) =
        match pattern, actual with
        | TypeRef.TypeParameter id, _ when not (Map.containsKey id bound) -> Map.add id actual bound
        | TypeRef.Named(_, patterns), TypeRef.Named(_, actuals)
        | TypeRef.BuiltinGeneric(_, patterns), TypeRef.BuiltinGeneric(_, actuals) when patterns.Length = actuals.Length ->
            List.fold2 (fun state p a -> unify p a state) bound patterns actuals
        | _ -> bound

    let rec private concrete (typeRef: TypeRef) =
        match typeRef with
        | TypeRef.TypeParameter _ -> false
        | TypeRef.Named(_, arguments)
        | TypeRef.BuiltinGeneric(_, arguments) -> List.forall concrete arguments
        | TypeRef.Function(parameters, result) -> List.forall concrete (result :: parameters)
        | _ -> true

    /// The Zig type of a supported value type, recording each declared type instance it names.
    /// Products and Unions are boxed (`*const`), which keeps recursive types finite.
    let private capabilityName (capabilityId: CapabilityId) = identifier "C_" (StableId.value capabilityId)
    let private operationName (operationId: CapabilityOperationId) = identifier "op_" (StableId.value operationId)

    let rec private zigType (types: Map<TypeId, ExecutionType>) (capabilities: Set<CapabilityId>) (record: TypeId * TypeRef list -> unit) (typeRef: TypeRef) : string option =
        let inner = zigType types capabilities record
        match typeRef with
        | TypeRef.Primitive PrimitiveType.Int64 -> Some "i64"
        | TypeRef.Primitive PrimitiveType.Bool -> Some "bool"
        | TypeRef.Primitive PrimitiveType.Unit -> Some "NyUnit"
        | TypeRef.Primitive PrimitiveType.String -> Some "[]const u8"
        | TypeRef.Primitive PrimitiveType.Float64 -> Some "f64"
        | TypeRef.Primitive PrimitiveType.Bytes -> Some "NyBytes"
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ]) -> inner item |> Option.map (fun item -> "?" + item)
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ item ]) -> inner item |> Option.map (fun item -> "NySet(" + item + ")")
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ key; value ]) ->
            match inner key, inner value with
            | Some key, Some value -> Some("NyMap(" + key + ", " + value + ")")
            | _ -> None
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ]) -> inner item |> Option.map (fun item -> "[]const " + item)
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ ok; error ]) ->
            match inner ok, inner error with
            | Some ok, Some error -> Some("NyResult(" + ok + ", " + error + ")")
            | _ -> None
        | TypeRef.Named(typeId, arguments) when Map.containsKey typeId types && List.forall concrete arguments
                                                && (Map.find typeId types).TypeParameters.Length = arguments.Length ->
            record (typeId, arguments)
            Some("*const " + typeName typeId arguments)
        // A capability value's type is declared by whoever binds the capability (a runPure runner's
        // fakes for now), with one method per operation.
        | TypeRef.Capability capabilityId when Set.contains capabilityId capabilities -> Some(capabilityName capabilityId)
        | TypeRef.Function(parameters, result) ->
            let parts = (result :: parameters) |> List.map inner
            if List.forall Option.isSome parts then
                let parts = parts |> List.map Option.get
                Some("*const fn (" + String.Join(", ", List.tail parts) + ") " + List.head parts)
            else None
        | _ -> None

    /// The Zig type of a supported value type of a program.
    let typeOf (program: ExecutionProgram) (typeRef: TypeRef) =
        zigType
            (program.Types |> List.map (fun item -> item.Id, item) |> Map.ofList)
            (program.Capabilities |> List.map (fun item -> item.Id) |> Set.ofList)
            ignore
            typeRef

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

    /// What one function body needs from the program while it is emitted.
    type private Context =
        { ZigType: TypeRef -> string option
          ValueType: ExecutionValueId -> TypeRef
          /// The instance name for a call of the function with the given argument type per
          /// parameter and the given result type.
          Call: FunctionId -> (ParameterId * TypeRef) list -> TypeRef -> string
          Parameters: FunctionId -> (ParameterId * TypeRef) list
          OperationParameters: CapabilityOperationId -> CapabilityParameterId list }

    let private itemType (typeRef: TypeRef) =
        match typeRef with
        | TypeRef.BuiltinGeneric((BuiltinGenericType.List | BuiltinGenericType.Set), [ item ]) -> item
        | other -> invalidOp $"Expected a List type, got {other}."

    /// One Zig statement per instruction, or None when the instruction is outside the supported slice.
    let private instructionLine (context: Context) (instruction: ExecutionInstruction) : string option =
        let target = valueName instruction.Result.Id
        let v = valueName
        let zig typeRef = context.ZigType typeRef |> Option.defaultValue "void"
        let assign (expression: string) = Some(target + " = " + expression + ";")
        let binary left operator right = assign (v left + " " + operator + " " + v right)
        let resultTypeName () =
            match instruction.Result.Type with
            | TypeRef.Named _ as named -> (zig named).Substring("*const ".Length)
            | other -> invalidOp $"Expected a declared type, got {other}."
        let elementOf (listValue: ExecutionValueId) = zig (itemType (context.ValueType listValue))
        let resultElement () = zig (itemType instruction.Result.Type)
        let listCall name (listValue: ExecutionValueId) (rest: string list) =
            assign (name + "(" + String.Join(", ", elementOf listValue :: v listValue :: rest) + ")")
        /// A callback call: context-bound parameters take their values, the others take `roles` in
        /// declaration order.
        let callback (functionId: FunctionId) (bindings: ExecutionCallArgument list) (roles: (string * TypeRef) list) (result: TypeRef) =
            let bound = bindings |> List.map (fun binding -> binding.TargetParameterId, binding.ValueId) |> Map.ofList
            let mutable remaining = roles
            let arguments =
                context.Parameters functionId
                |> List.map (fun (parameterId, _) ->
                    match Map.tryFind parameterId bound with
                    | Some valueId -> parameterId, v valueId, context.ValueType valueId
                    | None ->
                        let expression, roleType = List.head remaining
                        remaining <- List.tail remaining
                        parameterId, expression, roleType)
            let name = context.Call functionId (arguments |> List.map (fun (id, _, typeRef) -> id, typeRef)) result
            name + "(" + String.Join(", ", arguments |> List.map (fun (_, expression, _) -> expression)) + ")"
        let block (body: string) = assign ("blk: { " + body + " }")
        /// A comparison callback as a Zig function value: bound values travel in `c`, the two
        /// compared items are `l` and `r`.
        let comparator (functionId: FunctionId) (bindings: ExecutionCallArgument list) (item: TypeRef) (first: string) (second: string) =
            let call = callback functionId bindings [ first, item; second, item ] (TypeRef.Primitive PrimitiveType.Bool)
            let captured = bindings |> List.map (fun binding -> v binding.ValueId) |> List.distinct
            // Zig forbids shadowing the outer locals, so the call reads them from `c` (value names
            // have one fixed length, so no name is a prefix of another).
            let inner = captured |> List.fold (fun (text: string) name -> text.Replace(name, "c." + name)) call
            let context = ".{ " + String.Join(", ", captured |> List.map (fun name -> "." + name + " = " + name)) + " }"
            context, "struct { fn less(c: anytype, l: " + zig item + ", r: " + zig item + ") bool { _ = &c; return " + inner + "; } }.less"
        let mapTypes (mapValue: ExecutionValueId) =
            match context.ValueType mapValue with
            | TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ key; value ]) -> key, value
            | other -> invalidOp $"Expected a Map type, got {other}."
        let mapCall name (mapValue: ExecutionValueId) (rest: string list) =
            let key, value = mapTypes mapValue
            assign (name + "(" + String.Join(", ", zig key :: zig value :: v mapValue :: rest) + ")")
        let bool = TypeRef.Primitive PrimitiveType.Bool
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
            block ("var copy = " + v source + ".*;" + updates + " break :blk nyBox(" + resultTypeName () + ", copy);")
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
        | ExecutionInstructionKind.ListEmpty -> assign "&.{}"
        | ExecutionInstructionKind.ListConstruct items ->
            assign ("nyListOf(" + resultElement () + ", &[_]" + resultElement () + "{ " + String.Join(", ", items |> List.map v) + " })")
        | ExecutionInstructionKind.ListRange(start, finish) -> assign ("nyListRange(" + v start + ", " + v finish + ")")
        | ExecutionInstructionKind.ListLength list -> assign ("@as(i64, @intCast(" + v list + ".len))")
        | ExecutionInstructionKind.ListAppendItem(list, item) -> listCall "nyListAppend" list [ v item ]
        | ExecutionInstructionKind.ListConcat(left, right) -> listCall "nyListConcat" left [ v right ]
        | ExecutionInstructionKind.ListReverse list -> listCall "nyListReverse" list []
        | ExecutionInstructionKind.ListItemAt(list, index) -> listCall "nyListItemAt" list [ v index ]
        | ExecutionInstructionKind.ListLast list -> listCall "nyListLast" list []
        | ExecutionInstructionKind.ListDropLast list -> listCall "nyListDropLast" list []
        | ExecutionInstructionKind.ListSlice(list, start, finish) -> listCall "nyListSlice" list [ v start; v finish ]
        | ExecutionInstructionKind.ListSetAt(list, index, item) -> listCall "nyListSetAt" list [ v index; v item ]
        | ExecutionInstructionKind.ListInsertAt(list, index, item) -> listCall "nyListInsertAt" list [ v index; v item ]
        | ExecutionInstructionKind.ListRemoveAt(list, index) -> listCall "nyListRemoveAt" list [ v index ]
        | ExecutionInstructionKind.ListIndexOf(list, item) -> listCall "nyListIndexOf" list [ v item ]
        | ExecutionInstructionKind.ListLastIndexOf(list, item) -> listCall "nyListLastIndexOf" list [ v item ]
        | ExecutionInstructionKind.ListContains(list, item) -> assign ("nyListIndexOf(" + elementOf list + ", " + v list + ", " + v item + ") != null")
        | ExecutionInstructionKind.ListFold(source, seed, folder, bindings) ->
            let state = context.ValueType seed
            let call = callback folder bindings [ "state", state; "item", itemType (context.ValueType source) ] state
            block ("var state = " + v seed + "; for (" + v source + ") |item| state = " + call + "; break :blk state;")
        | ExecutionInstructionKind.ListScan(source, seed, folder, bindings) ->
            let state = context.ValueType seed
            let call = callback folder bindings [ "state", state; "item", itemType (context.ValueType source) ] state
            block ("const out = nyAlloc(" + zig state + ", " + v source + ".len + 1); out[0] = " + v seed + "; for (" + v source + ", 0..) |item, at| { const state = out[at]; out[at + 1] = " + call + "; } break :blk out;")
        | ExecutionInstructionKind.ListMap(source, mapper, bindings) ->
            let call = callback mapper bindings [ "item", itemType (context.ValueType source) ] (itemType instruction.Result.Type)
            block ("const out = nyAlloc(" + resultElement () + ", " + v source + ".len); for (" + v source + ", 0..) |item, at| out[at] = " + call + "; break :blk out;")
        | ExecutionInstructionKind.ListFilter(source, predicate, bindings) ->
            let call = callback predicate bindings [ "item", itemType (context.ValueType source) ] (TypeRef.Primitive PrimitiveType.Bool)
            block ("const out = nyAlloc(" + elementOf source + ", " + v source + ".len); var count: usize = 0; for (" + v source + ") |item| { if (" + call + ") { out[count] = item; count += 1; } } break :blk out[0..count];")
        | ExecutionInstructionKind.ListAny(source, predicate, bindings) ->
            let call = callback predicate bindings [ "item", itemType (context.ValueType source) ] (TypeRef.Primitive PrimitiveType.Bool)
            block ("for (" + v source + ") |item| { if (" + call + ") break :blk true; } break :blk false;")
        | ExecutionInstructionKind.ListAll(source, predicate, bindings) ->
            let call = callback predicate bindings [ "item", itemType (context.ValueType source) ] (TypeRef.Primitive PrimitiveType.Bool)
            block ("for (" + v source + ") |item| { if (!" + call + ") break :blk false; } break :blk true;")
        | ExecutionInstructionKind.ListFindIndex(source, predicate, bindings) ->
            let call = callback predicate bindings [ "item", itemType (context.ValueType source) ] (TypeRef.Primitive PrimitiveType.Bool)
            block ("for (" + v source + ", 0..) |item, at| { if (" + call + ") break :blk @as(i64, @intCast(at)); } break :blk null;")
        | ExecutionInstructionKind.ListZipWith(left, right, combiner, bindings) ->
            let call = callback combiner bindings [ "l", itemType (context.ValueType left); "r", itemType (context.ValueType right) ] (itemType instruction.Result.Type)
            block ("const count = @min(" + v left + ".len, " + v right + ".len); const out = nyAlloc(" + resultElement () + ", count); for (" + v left + "[0..count], " + v right + "[0..count], 0..) |l, r, at| out[at] = " + call + "; break :blk out;")
        | ExecutionInstructionKind.ListFlatMap(source, mapper, bindings) ->
            let call = callback mapper bindings [ "item", itemType (context.ValueType source) ] instruction.Result.Type
            block ("var out: std.ArrayList(" + resultElement () + ") = .empty; for (" + v source + ") |item| out.appendSlice(nyAllocator(), " + call + ") catch @panic(\"out of memory\"); break :blk out.items;")
        | ExecutionInstructionKind.Float64Literal bits -> assign ("@as(f64, @bitCast(@as(u64, 0x" + (Float64Bits.value bits).ToString("x16") + ")))")
        | ExecutionInstructionKind.Float64Add(left, right) -> binary left "+" right
        | ExecutionInstructionKind.Float64Subtract(left, right) -> binary left "-" right
        | ExecutionInstructionKind.Float64Multiply(left, right) -> binary left "*" right
        | ExecutionInstructionKind.Float64Divide(left, right) -> binary left "/" right
        | ExecutionInstructionKind.Float64Min(left, right) -> assign ("nyFloat64Min(" + v left + ", " + v right + ")")
        | ExecutionInstructionKind.Float64Max(left, right) -> assign ("nyFloat64Max(" + v left + ", " + v right + ")")
        | ExecutionInstructionKind.Float64FloorModulo(left, right) -> assign ("nyFloat64FloorModulo(" + v left + ", " + v right + ")")
        | ExecutionInstructionKind.Float64Equal(left, right) -> binary left "==" right
        | ExecutionInstructionKind.Float64LessThan(left, right) -> binary left "<" right
        | ExecutionInstructionKind.Float64IsNaN source -> assign ("std.math.isNan(" + v source + ")")
        | ExecutionInstructionKind.Float64IsFinite source -> assign ("std.math.isFinite(" + v source + ")")
        | ExecutionInstructionKind.Float64IsInfinite source -> assign ("std.math.isInf(" + v source + ")")
        | ExecutionInstructionKind.Float64Sqrt source -> assign ("@sqrt(" + v source + ")")
        | ExecutionInstructionKind.Float64Abs source -> assign ("@abs(" + v source + ")")
        | ExecutionInstructionKind.Float64Negate source -> assign ("-" + v source)
        | ExecutionInstructionKind.Float64Floor source -> assign ("@floor(" + v source + ")")
        | ExecutionInstructionKind.Float64Ceiling source -> assign ("@ceil(" + v source + ")")
        | ExecutionInstructionKind.Float64Truncate source -> assign ("@trunc(" + v source + ")")
        // Zig's @round rounds halfway cases away from zero, as Float64RoundToInt64 requires.
        | ExecutionInstructionKind.Float64RoundToInt64 source -> assign ("nyFloat64ToInt64(@round(" + v source + "))")
        | ExecutionInstructionKind.Float64FloorToInt64 source -> assign ("nyFloat64ToInt64(@floor(" + v source + "))")
        | ExecutionInstructionKind.Int64ToFloat64 source -> assign ("@as(f64, @floatFromInt(" + v source + "))")
        | ExecutionInstructionKind.SetEmpty -> assign ".{ .items = &.{} }"
        | ExecutionInstructionKind.MapEmpty -> assign ".{ .keys = &.{}, .values = &.{} }"
        | ExecutionInstructionKind.SetFromList list -> listCall "nySetFromList" list []
        | ExecutionInstructionKind.SetToList set -> assign (v set + ".items")
        | ExecutionInstructionKind.SetSize set -> assign ("@as(i64, @intCast(" + v set + ".items.len))")
        | ExecutionInstructionKind.SetInsert(set, item) -> listCall "nySetInsert" set [ v item ]
        | ExecutionInstructionKind.SetRemove(set, item) -> listCall "nySetRemove" set [ v item ]
        | ExecutionInstructionKind.SetContains(set, item) -> assign ("nyFind(" + elementOf set + ", " + v set + ".items, " + v item + ") != null")
        | ExecutionInstructionKind.SetUnion(left, right) -> listCall "nySetUnion" left [ v right ]
        | ExecutionInstructionKind.SetIntersection(left, right) -> listCall "nySetKeep" left [ v right; "true" ]
        | ExecutionInstructionKind.SetDifference(left, right) -> listCall "nySetKeep" left [ v right; "false" ]
        | ExecutionInstructionKind.SetIsSubset(left, right) -> listCall "nySetIsSubset" left [ v right ]
        | ExecutionInstructionKind.SetFold(source, seed, folder, bindings) ->
            let state = context.ValueType seed
            let call = callback folder bindings [ "state", state; "item", itemType (context.ValueType source) ] state
            block ("var state = " + v seed + "; for (" + v source + ".items) |item| state = " + call + "; break :blk state;")
        | ExecutionInstructionKind.SetFilter(source, predicate, bindings) ->
            let call = callback predicate bindings [ "item", itemType (context.ValueType source) ] bool
            block ("const out = nyAlloc(" + elementOf source + ", " + v source + ".items.len); var count: usize = 0; for (" + v source + ".items) |item| { if (" + call + ") { out[count] = item; count += 1; } } break :blk .{ .items = out[0..count] };")
        | ExecutionInstructionKind.SetMap(source, mapper, bindings) ->
            let call = callback mapper bindings [ "item", itemType (context.ValueType source) ] (itemType instruction.Result.Type)
            block ("var out: " + zig instruction.Result.Type + " = .{ .items = &.{} }; for (" + v source + ".items) |item| out = nySetInsert(" + resultElement () + ", out, " + call + "); break :blk out;")
        | ExecutionInstructionKind.MapSize map -> assign ("@as(i64, @intCast(" + v map + ".keys.len))")
        | ExecutionInstructionKind.MapKeys map -> assign (v map + ".keys")
        | ExecutionInstructionKind.MapValues map -> assign (v map + ".values")
        | ExecutionInstructionKind.MapInsert(map, key, value) -> mapCall "nyMapInsert" map [ v key; v value ]
        | ExecutionInstructionKind.MapRemove(map, key) -> mapCall "nyMapRemove" map [ v key ]
        | ExecutionInstructionKind.MapGet(map, key) -> mapCall "nyMapGet" map [ v key ]
        | ExecutionInstructionKind.MapGetOrDefault(map, key, fallback) ->
            let keyType, valueType = mapTypes map
            assign ("nyMapGet(" + zig keyType + ", " + zig valueType + ", " + v map + ", " + v key + ") orelse " + v fallback)
        | ExecutionInstructionKind.MapContainsKey(map, key) -> assign ("nyFind(" + zig (fst (mapTypes map)) + ", " + v map + ".keys, " + v key + ") != null")
        | ExecutionInstructionKind.MapUnion(left, right) -> mapCall "nyMapUnion" left [ v right ]
        | ExecutionInstructionKind.MapFold(source, seed, folder, bindings) ->
            let keyType, valueType = mapTypes source
            let state = context.ValueType seed
            let call = callback folder bindings [ "state", state; "key", keyType; "value", valueType ] state
            block ("var state = " + v seed + "; for (" + v source + ".keys, " + v source + ".values) |key, value| state = " + call + "; break :blk state;")
        | ExecutionInstructionKind.MapFilter(source, keep, bindings) ->
            let keyType, valueType = mapTypes source
            let call = callback keep bindings [ "key", keyType; "value", valueType ] bool
            block ("var out: " + zig instruction.Result.Type + " = .{ .keys = &.{}, .values = &.{} }; for (" + v source + ".keys, " + v source + ".values) |key, value| { if (" + call + ") out = nyMapInsert(" + zig keyType + ", " + zig valueType + ", out, key, value); } break :blk out;")
        | ExecutionInstructionKind.MapMapValues(source, mapper, bindings) ->
            let keyType, valueType = mapTypes source
            let resultValue = match instruction.Result.Type with TypeRef.BuiltinGeneric(_, [ _; value ]) -> value | other -> other
            let call = callback mapper bindings [ "key", keyType; "value", valueType ] resultValue
            block ("const out = nyAlloc(" + zig resultValue + ", " + v source + ".keys.len); for (" + v source + ".keys, " + v source + ".values, 0..) |key, value, at| out[at] = " + call + "; break :blk .{ .keys = " + v source + ".keys, .values = out };")
        | ExecutionInstructionKind.MapFromList(source, keyOf, bindings) ->
            let keyType = match instruction.Result.Type with TypeRef.BuiltinGeneric(_, [ key; _ ]) -> key | other -> other
            let element = itemType (context.ValueType source)
            let call = callback keyOf bindings [ "item", element ] keyType
            block ("var out: " + zig instruction.Result.Type + " = .{ .keys = &.{}, .values = &.{} }; for (" + v source + ") |item| out = nyMapInsert(" + zig keyType + ", " + zig element + ", out, " + call + ", item); break :blk out;")
        | ExecutionInstructionKind.BytesEmpty -> assign ".{ .data = &.{} }"
        | ExecutionInstructionKind.BytesFill(length, value) -> assign ("nyBytesFill(" + v length + ", " + v value + ")")
        | ExecutionInstructionKind.BytesLength bytes -> assign ("@as(i64, @intCast(" + v bytes + ".data.len))")
        | ExecutionInstructionKind.BytesGet(bytes, index) -> assign ("if (nyListItemAt(u8, " + v bytes + ".data, " + v index + ")) |byte| @as(i64, byte) else null")
        | ExecutionInstructionKind.BytesSet(bytes, index, value) -> assign ("nyBytesSet(" + v bytes + ", " + v index + ", " + v value + ")")
        | ExecutionInstructionKind.BytesAppend(bytes, value) -> assign (".{ .data = nyListAppend(u8, " + v bytes + ".data, @truncate(@as(u64, @bitCast(" + v value + ")))) }")
        | ExecutionInstructionKind.BytesConcat(left, right) -> assign (".{ .data = nyListConcat(u8, " + v left + ".data, " + v right + ".data) }")
        | ExecutionInstructionKind.BytesSlice(bytes, start, finish) -> assign (".{ .data = nyListSlice(u8, " + v bytes + ".data, " + v start + ", " + v finish + ") }")
        | ExecutionInstructionKind.BytesFromLatin1 text -> assign ("nyBytesFromLatin1(" + v text + ")")
        | ExecutionInstructionKind.BytesToLatin1 bytes -> assign ("nyBytesToLatin1(" + v bytes + ")")
        | ExecutionInstructionKind.CapabilityInvoke(capability, operationId, arguments) ->
            let byParameter = arguments |> List.map (fun argument -> argument.TargetCapabilityParameterId, argument.ValueId) |> Map.ofList
            let rendered = context.OperationParameters operationId |> List.map (fun id -> v (Map.find id byParameter))
            assign (v capability + "." + operationName operationId + "(" + String.Join(", ", rendered) + ")")
        | ExecutionInstructionKind.ListSortBy(source, lessThan, bindings) ->
            let context, less = comparator lessThan bindings (itemType (context.ValueType source)) "l" "r"
            assign ("nySortBy(" + elementOf source + ", " + v source + ", " + context + ", " + less + ")")
        | ExecutionInstructionKind.ListMinBy(source, lessThan, bindings) ->
            let context, less = comparator lessThan bindings (itemType (context.ValueType source)) "l" "r"
            assign ("nyBestBy(" + elementOf source + ", " + v source + ", " + context + ", " + less + ")")
        | ExecutionInstructionKind.ListMaxBy(source, lessThan, bindings) ->
            // The best is replaced when lessThan(best, item): the same comparator with the items swapped.
            let context, less = comparator lessThan bindings (itemType (context.ValueType source)) "r" "l"
            assign ("nyBestBy(" + elementOf source + ", " + v source + ", " + context + ", " + less + ")")
        | ExecutionInstructionKind.FunctionReference functionId ->
            match instruction.Result.Type with
            | TypeRef.Function(parameters, result) ->
                let ids = context.Parameters functionId |> List.map fst
                assign ("&" + context.Call functionId (List.zip ids parameters) result)
            | other -> invalidOp $"Expected a function type, got {other}."
        | ExecutionInstructionKind.FunctionInvoke(target, arguments) ->
            assign (v target + "(" + String.Join(", ", arguments |> List.map v) + ")")
        | ExecutionInstructionKind.DirectCall(functionId, arguments) ->
            let byParameter = arguments |> List.map (fun argument -> argument.TargetParameterId, argument.ValueId) |> Map.ofList
            let parameters = context.Parameters functionId |> List.map fst
            let name = context.Call functionId (parameters |> List.map (fun id -> id, context.ValueType (Map.find id byParameter))) instruction.Result.Type
            assign (name + "(" + String.Join(", ", parameters |> List.map (fun id -> v (Map.find id byParameter))) + ")")
        | _ -> None

    let private caseName (value: obj) =
        let text = sprintf "%A" value
        let cut = text.IndexOfAny([| ' '; '\n'; '(' |])
        if cut < 0 then text else text.Substring(0, cut)

    let private valuesOf (fn: ExecutionFunction) =
        (fn.Parameters |> List.map (fun parameter -> parameter.Value))
        @ (fn.Blocks |> List.collect (fun block -> block.Parameters @ (block.Instructions |> List.map (fun instruction -> instruction.Result))))

    let private substituteFunction (map: Map<TypeParameterId, TypeRef>) (fn: ExecutionFunction) =
        if Map.isEmpty map then fn
        else
            let value (item: ExecutionValue) = { item with Type = substitute map item.Type }
            { fn with
                TypeParameters = []
                ResultType = substitute map fn.ResultType
                Parameters = fn.Parameters |> List.map (fun parameter -> { parameter with Value = value parameter.Value })
                Blocks =
                    fn.Blocks |> List.map (fun block ->
                        { block with
                            Parameters = List.map value block.Parameters
                            Instructions = block.Instructions |> List.map (fun instruction -> { instruction with Result = value instruction.Result }) }) }

    let private emitFunction (builder: StringBuilder) (context: Context) (name: string) (fn: ExecutionFunction) =
        let line (text: string) = builder.Append(text).Append('\n') |> ignore
        let zig typeRef = (context.ZigType typeRef).Value
        let parameters =
            fn.Parameters
            |> List.map (fun parameter -> parameterName parameter.SemanticParameterId + ": " + zig parameter.Value.Type)
        line ("fn " + name + "(" + String.Join(", ", parameters) + ") " + zig fn.ResultType + " {")
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
                line ("            " + (instructionLine context instruction).Value)
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

    /// Fields and variants are emitted in stable-ID order, the order runPure tokens and results use.
    let private emitType (builder: StringBuilder) (zig: TypeRef -> string) (item: ExecutionType) (arguments: TypeRef list) =
        let line (text: string) = builder.Append(text).Append('\n') |> ignore
        let map = List.zip (item.TypeParameters |> List.map fst) arguments |> Map.ofList
        let fields (items: ExecutionField list) =
            items
            |> List.sortBy (fun field -> StableId.value field.Id)
            |> List.map (fun field -> fieldName field.Id + ": " + zig (substitute map field.Type))
        let name = typeName item.Id arguments
        match item.Kind with
        | ExecutionTypeKind.Product items ->
            line ("const " + name + " = struct { " + String.Join(", ", fields items) + " };")
        | ExecutionTypeKind.Union variants ->
            let ordered = variants |> List.sortBy (fun variant -> StableId.value variant.Id)
            line ("const " + name + " = union(enum) {")
            for variant in ordered do
                line ("    " + variantName variant.Id + ": struct { " + String.Join(", ", fields variant.Fields) + " },")
            let ids = ordered |> List.map (fun variant -> "\"" + StableId.value variant.Id + "\"")
            line ("    pub const ny_ids = [_][]const u8{ " + String.Join(", ", ids) + " };")
            line "};"
        line ""

    /// Zig source for the program's non-generic functions and every instance of a generic function
    /// or type they reach, or what is outside the supported slice.
    let emit (program: ExecutionProgram) : Result<string, string list> =
        let types = program.Types |> List.map (fun item -> item.Id, item) |> Map.ofList
        let functions = program.Functions |> List.map (fun fn -> fn.SemanticFunctionId, fn) |> Map.ofList
        let parametersOf functionId =
            (Map.find functionId functions).Parameters |> List.map (fun parameter -> parameter.SemanticParameterId, parameter.Value.Type)
        let problems = ResizeArray<string>()
        let typeInstances = HashSet<TypeId * TypeRef list>()
        let typeOrder = ResizeArray<TypeId * TypeRef list>()
        let record instance = if typeInstances.Add instance then typeOrder.Add instance
        let capabilities = program.Capabilities |> List.map (fun item -> item.Id) |> Set.ofList
        let operationParameters =
            program.Capabilities
            |> List.collect (fun capability -> capability.Operations |> List.map (fun operation -> operation.Id, operation.Parameters |> List.map (fun parameter -> parameter.Id)))
            |> Map.ofList
        let zigOf typeRef =
            match zigType types capabilities record typeRef with
            | Some text -> Some text
            | None ->
                problems.Add $"type {typeRef}"
                None
        let pending = Queue<FunctionId * TypeRef list>()
        let functionInstances = HashSet<FunctionId * TypeRef list>()
        let call functionId (arguments: (ParameterId * TypeRef) list) (result: TypeRef) =
            let fn = Map.find functionId functions
            let parameterTypes = fn.Parameters |> List.map (fun parameter -> parameter.SemanticParameterId, parameter.Value.Type) |> Map.ofList
            let bound =
                arguments
                |> List.fold (fun state (id, actual) -> unify (Map.find id parameterTypes) actual state) Map.empty
                |> unify fn.ResultType result
            let typeArguments = fn.TypeParameters |> List.map (fun (id, _) -> Map.tryFind id bound |> Option.defaultValue (TypeRef.TypeParameter id))
            if not (List.forall concrete typeArguments) then problems.Add "a generic call whose type arguments cannot be inferred"
            if functionInstances.Add((functionId, typeArguments)) then pending.Enqueue((functionId, typeArguments))
            instanceName functionId typeArguments
        let builder = StringBuilder()
        builder.Append("// Generated by the Nyrwvy Zig backend.\n\n").Append(ZigRuntime.runtime).Append('\n') |> ignore
        let bodies = StringBuilder()
        for fn in program.Functions |> List.filter (fun fn -> List.isEmpty fn.TypeParameters) do
            if functionInstances.Add((fn.SemanticFunctionId, [])) then pending.Enqueue((fn.SemanticFunctionId, []))
        while pending.Count > 0 do
            let functionId, typeArguments = pending.Dequeue()
            let generic = Map.find functionId functions
            let fn = substituteFunction (List.zip (generic.TypeParameters |> List.map fst) typeArguments |> Map.ofList) generic
            let valueTypes = valuesOf fn |> List.map (fun value -> value.Id, value.Type) |> Map.ofList
            let context =
                { ZigType = zigOf
                  ValueType = fun id -> Map.find id valueTypes
                  Call = call
                  Parameters = parametersOf
                  OperationParameters = fun id -> Map.find id operationParameters }
            for value in valuesOf fn do zigOf value.Type |> ignore
            zigOf fn.ResultType |> ignore
            let unsupported =
                fn.Blocks
                |> List.collect (fun block -> block.Instructions)
                |> List.filter (fun instruction -> Option.isNone (instructionLine context instruction))
                |> List.map (fun instruction -> caseName (box instruction.Kind))
            problems.AddRange unsupported
            if problems.Count = 0 then emitFunction bodies context (instanceName functionId typeArguments) fn
        // Field types can name further instances, so declarations are emitted until none is new.
        let declarations = StringBuilder()
        let mutable emitted = 0
        while problems.Count = 0 && emitted < typeOrder.Count do
            let typeId, arguments = typeOrder[emitted]
            emitted <- emitted + 1
            emitType declarations (fun typeRef -> zigOf typeRef |> Option.defaultValue "void") (Map.find typeId types) arguments
        if problems.Count > 0 then Error(problems |> Seq.distinct |> List.ofSeq)
        else Ok(builder.Append(declarations).Append(bodies).ToString())

    /// Scripted fake capabilities for a runPure runner: one struct per capability whose methods record
    /// each call under its site, select the scripted response (by the call's argument key for a keyed
    /// script) and return it, or end the run as the C# runner's fake exceptions do. `fake` indexes the
    /// per-operation script arrays; `site` is the capability's first call site.
    let fakeCapabilities (program: ExecutionProgram) : Result<string, string list> =
        let zig typeRef = typeOf program typeRef
        let problems =
            program.Capabilities
            |> List.collect (fun capability -> capability.Operations)
            |> List.collect (fun operation -> operation.ResultType :: (operation.Parameters |> List.map (fun parameter -> parameter.Type)))
            |> List.filter (fun typeRef -> match typeRef with TypeRef.Capability _ -> true | other -> Option.isNone (zig other))
            |> List.distinct
            |> List.map (fun typeRef -> $"capability operation type {typeRef}")
        if not (List.isEmpty problems) then Error problems
        else
            let builder = StringBuilder()
            let line (text: string) = builder.Append(text).Append('\n') |> ignore
            for capability in program.Capabilities do
                line ("const " + capabilityName capability.Id + " = struct {")
                line "    fake: usize,"
                line "    site: usize,"
                for index, operation in capability.Operations |> List.sortBy (fun operation -> StableId.value operation.Id) |> List.indexed do
                    let result = (zig operation.ResultType).Value
                    let parameters = operation.Parameters |> List.map (fun parameter -> identifier "a_" (StableId.value parameter.Id) + ": " + (zig parameter.Type).Value)
                    let recorded = operation.Parameters |> List.map (fun parameter -> identifier "a_" (StableId.value parameter.Id))
                    let keyed = operation.Parameters |> List.sortBy (fun parameter -> StableId.value parameter.Id) |> List.map (fun parameter -> identifier "a_" (StableId.value parameter.Id))
                    let scripts = identifier "ny_scripts_" (StableId.value operation.Id)
                    line ("    pub fn " + operationName operation.Id + "(self: @This()" + String.concat "" (parameters |> List.map (fun item -> ", " + item)) + ") " + result + " {")
                    line ("        nyRecordCall(self.site + " + string index + ", .{ " + String.Join(", ", recorded) + " });")
                    line ("        return nyRespond(" + result + ", &" + scripts + "[self.fake], .{ " + String.Join(", ", keyed) + " });")
                    line "    }"
                line "};"
                for operation in capability.Operations do
                    line ("var " + identifier "ny_scripts_" (StableId.value operation.Id) + ": [16]NyScript(" + (zig operation.ResultType).Value + ") = undefined;")
                line ""
            Ok(builder.ToString())

    /// The Zig statements a runner's entry dispatch uses to make the fake for one capability
    /// parameter, reading its scripts from the argument tokens in canonical operation order.
    let fakeArgument (program: ExecutionProgram) (capabilityId: CapabilityId) (name: string) (fake: int) (site: int) =
        let capability = program.Capabilities |> List.find (fun item -> item.Id = capabilityId)
        let reads =
            capability.Operations
            |> List.sortBy (fun operation -> StableId.value operation.Id)
            |> List.map (fun operation ->
                let scripts = identifier "ny_scripts_" (StableId.value operation.Id)
                let result = (typeOf program operation.ResultType).Value
                $"        {scripts}[{fake}] = nyReadScript({result}, &tokens);\n")
        let typeName = capabilityName capabilityId
        $"        const {name} = {typeName}{{ .fake = {fake}, .site = {site} }};\n" + String.concat "" reads, capability.Operations.Length
