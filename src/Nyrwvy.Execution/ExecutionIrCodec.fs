namespace Nyrwvy.Core

open System.Globalization
open System.Text.Json

/// Canonical serialization for backend-neutral Execution IR.
/// Collection order is normalized only where order is non-semantic.
/// Instruction order and signature/block-parameter order are preserved.
[<RequireQualifiedAccess>]
module ExecutionIrCodec =
    let schemaVersion = 9

    let private quote (value: string): string =
        JsonSerializer.Serialize(value)

    let private property (name: string) (value: string): string =
        quote name + ":" + value

    let private objectValue (properties: string list): string =
        "{" + String.concat "," properties + "}"

    let private arrayValue (items: string list): string =
        "[" + String.concat "," items + "]"

    let private stableIdValue (id: StableId<'tag>): string =
        id |> StableId.value |> quote

    let private executionValueIdValue (id: ExecutionValueId): string =
        id |> ExecutionValueId.value |> quote

    let private executionBlockIdValue (id: ExecutionBlockId): string =
        id |> ExecutionBlockId.value |> quote

    let rec private writeTypeRef (typeRef: TypeRef): string =
        match typeRef with
        | TypeRef.Primitive PrimitiveType.String ->
            objectValue [ property "kind" (quote "primitiveString") ]
        | TypeRef.Primitive PrimitiveType.Bool ->
            objectValue [ property "kind" (quote "primitiveBool") ]
        | TypeRef.Primitive PrimitiveType.Unit ->
            objectValue [ property "kind" (quote "primitiveUnit") ]
        | TypeRef.Primitive PrimitiveType.Int64 ->
            objectValue [ property "kind" (quote "primitiveInt64") ]
        | TypeRef.Primitive PrimitiveType.Float64 ->
            objectValue [ property "kind" (quote "primitiveFloat64") ]
        | TypeRef.Primitive PrimitiveType.Bits32 ->
            objectValue [ property "kind" (quote "primitiveBits32") ]
        | TypeRef.TypeParameter(typeParameterId, displayName) ->
            objectValue
                [ property "kind" (quote "typeParameter")
                  property "typeParameterId" (quote (StableId.value typeParameterId))
                  property "displayName" (quote displayName) ]
        | TypeRef.Function(arguments, result) ->
            objectValue [ property "kind" (quote "function"); property "parameters" (arrayValue (List.map writeTypeRef arguments)); property "resultType" (writeTypeRef result) ]
        | TypeRef.Named typeId ->
            objectValue
                [ property "kind" (quote "named")
                  property "typeId" (stableIdValue typeId) ]
        | TypeRef.Capability capabilityId ->
            objectValue
                [ property "kind" (quote "capability")
                  property "capabilityId" (stableIdValue capabilityId) ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ]) ->
            objectValue
                [ property "kind" (quote "option")
                  property "itemType" (writeTypeRef itemType) ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
            objectValue
                [ property "kind" (quote "result")
                  property "okType" (writeTypeRef okType)
                  property "errorType" (writeTypeRef errorType) ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ keyType; valueType ]) ->
            objectValue
                [ property "kind" (quote "map")
                  property "keyType" (writeTypeRef keyType)
                  property "valueType" (writeTypeRef valueType) ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ]) ->
            objectValue
                [ property "kind" (quote "list")
                  property "itemType" (writeTypeRef itemType) ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ itemType ]) ->
            objectValue
                [ property "kind" (quote "set")
                  property "itemType" (writeTypeRef itemType) ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ resultType ]) ->
            objectValue
                [ property "kind" (quote "task")
                  property "resultType" (writeTypeRef resultType) ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, []) -> objectValue [ property "kind" (quote "taskScope") ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Task, arguments) ->
            invalidOp $"Execution IR contains Task with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, arguments) ->
            invalidOp $"Execution IR contains TaskScope with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, arguments) ->
            invalidOp $"Execution IR contains Option with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, arguments) ->
            invalidOp $"Execution IR contains Result with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Map, arguments) ->
            invalidOp $"Execution IR contains Map with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, arguments) ->
            invalidOp $"Execution IR contains List with {arguments.Length} type arguments."

        | TypeRef.BuiltinGeneric(BuiltinGenericType.Set, arguments) ->
            invalidOp $"Execution IR contains Set with {arguments.Length} type arguments."

    let private writeOrigin (origin: ExecutionOrigin): string =
        match origin with
        | ExecutionOrigin.FunctionParameter parameterId ->
            objectValue
                [ property "kind" (quote "functionParameter")
                  property "parameterId" (stableIdValue parameterId) ]
        | ExecutionOrigin.SemanticNode nodeId ->
            objectValue
                [ property "kind" (quote "semanticNode")
                  property "nodeId" (stableIdValue nodeId) ]
        | ExecutionOrigin.PatternBinding(matchNodeId, bindingId) ->
            objectValue
                [ property "kind" (quote "patternBinding")
                  property "matchNodeId" (stableIdValue matchNodeId)
                  property "bindingId" (stableIdValue bindingId) ]

    let private writeValue (value: ExecutionValue): string =
        objectValue
            [ property "id" (executionValueIdValue value.Id)
              property "type" (writeTypeRef value.Type)
              property "origin" (writeOrigin value.Origin) ]

    let private writeField (field: ExecutionField): string =
        objectValue
            [ property "id" (stableIdValue field.Id)
              property "type" (writeTypeRef field.Type) ]

    let private writeVariant (variant: ExecutionVariant): string =
        objectValue
            [ property "id" (stableIdValue variant.Id)
              property
                  "fields"
                  (variant.Fields
                   |> List.sortBy (fun field -> StableId.value field.Id)
                   |> List.map writeField
                   |> arrayValue) ]

    let private writeType (typeEntity: ExecutionType): string =
        match typeEntity.Kind with
        | ExecutionTypeKind.Product fields ->
            objectValue
                [ property "id" (stableIdValue typeEntity.Id)
                  property "kind" (quote "product")
                  property
                      "fields"
                      (fields
                       |> List.sortBy (fun field -> StableId.value field.Id)
                       |> List.map writeField
                       |> arrayValue) ]
        | ExecutionTypeKind.Union variants ->
            objectValue
                [ property "id" (stableIdValue typeEntity.Id)
                  property "kind" (quote "union")
                  property
                      "variants"
                      (variants
                       |> List.sortBy (fun variant -> StableId.value variant.Id)
                       |> List.map writeVariant
                       |> arrayValue) ]

    let private writeCapabilityParameter (parameter: ExecutionCapabilityParameter): string =
        objectValue
            [ property "id" (stableIdValue parameter.Id)
              property "type" (writeTypeRef parameter.Type) ]

    let private writeCapabilityOperation (operation: ExecutionCapabilityOperation): string =
        let baseProperties =
            [ property "id" (stableIdValue operation.Id)
              property "parameters" (operation.Parameters |> List.map writeCapabilityParameter |> arrayValue)
              property "resultType" (writeTypeRef operation.ResultType) ]

        let failureProperties =
            match operation.FailureType with
            | Some failureType -> [ property "failureType" (writeTypeRef failureType) ]
            | None -> []

        objectValue
            (baseProperties
             @ failureProperties
             @ [ property "effectId" (stableIdValue operation.EffectId) ])

    let private writeCapability (capability: ExecutionCapability): string =
        objectValue
            [ property "id" (stableIdValue capability.Id)
              property
                  "operations"
                  (capability.Operations
                   |> List.sortBy (fun operation -> StableId.value operation.Id)
                   |> List.map writeCapabilityOperation
                   |> arrayValue) ]

    let private writeCallArgument (argument: ExecutionCallArgument): string =
        objectValue
            [ property "targetParameterId" (stableIdValue argument.TargetParameterId)
              property "valueId" (executionValueIdValue argument.ValueId) ]

    let private writeCapabilityArgument (argument: ExecutionCapabilityArgument): string =
        objectValue
            [ property "targetCapabilityParameterId" (stableIdValue argument.TargetCapabilityParameterId)
              property "valueId" (executionValueIdValue argument.ValueId) ]

    let private writeFieldValue (field: ExecutionFieldValue): string =
        objectValue
            [ property "targetFieldId" (stableIdValue field.TargetFieldId)
              property "valueId" (executionValueIdValue field.ValueId) ]

    let private writeBinaryInstruction kind leftValueId rightValueId =
        [ property "kind" (quote kind)
          property "leftValueId" (executionValueIdValue leftValueId)
          property "rightValueId" (executionValueIdValue rightValueId) ]

    let private writeUnaryInstruction kind valueId =
        [ property "kind" (quote kind)
          property "valueId" (executionValueIdValue valueId) ]

    /// Callback context is written only when present, so context-free programs keep their bytes.
    let private writeCallbackContext (context: ExecutionCallArgument list) =
        if List.isEmpty context then []
        else
            [ property
                  "context"
                  (context
                   |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId)
                   |> List.map writeCallArgument
                   |> arrayValue) ]

    let private writeListCallbackInstruction kind sourceListValueId callbackFieldName callbackFunctionId context =
        [ property "kind" (quote kind)
          property "sourceListValueId" (executionValueIdValue sourceListValueId)
          property callbackFieldName (stableIdValue callbackFunctionId) ]
        @ writeCallbackContext context

    let private writeInstructionKind (kind: ExecutionInstructionKind): string list =
        match kind with
        | ExecutionInstructionKind.ReadParameter sourceValueId ->
            [ property "kind" (quote "readParameter")
              property "sourceValueId" (executionValueIdValue sourceValueId) ]
        | ExecutionInstructionKind.ReadPatternBinding sourceValueId ->
            [ property "kind" (quote "readPatternBinding")
              property "sourceValueId" (executionValueIdValue sourceValueId) ]
        | ExecutionInstructionKind.StringLiteral value ->
            [ property "kind" (quote "stringLiteral")
              property "value" (quote value) ]
        | ExecutionInstructionKind.BoolLiteral value ->
            [ property "kind" (quote "boolLiteral")
              property "value" (if value then "true" else "false") ]
        | ExecutionInstructionKind.UnitLiteral ->
            [ property "kind" (quote "unitLiteral") ]
        | ExecutionInstructionKind.Int64Literal value ->
            [ property "kind" (quote "int64Literal")
              property "value" (value.ToString(CultureInfo.InvariantCulture)) ]
        | ExecutionInstructionKind.Float64Literal bits ->
            [ property "kind" (quote "float64Literal")
              property "bits" (quote (Float64Bits.toHex bits)) ]
        | ExecutionInstructionKind.StringIsEmpty valueId ->
            writeUnaryInstruction "stringIsEmpty" valueId
        | ExecutionInstructionKind.Int64ToString valueId ->
            writeUnaryInstruction "int64ToString" valueId
        | ExecutionInstructionKind.Int64ToHexString valueId ->
            writeUnaryInstruction "int64ToHexString" valueId
        | ExecutionInstructionKind.StringParseInt64 valueId ->
            writeUnaryInstruction "stringParseInt64" valueId
        | ExecutionInstructionKind.StringParseInt64Hex valueId ->
            writeUnaryInstruction "stringParseInt64Hex" valueId
        | ExecutionInstructionKind.Float64ToString valueId ->
            writeUnaryInstruction "float64ToString" valueId
        | ExecutionInstructionKind.StringParseFloat64 valueId ->
            writeUnaryInstruction "stringParseFloat64" valueId
        | ExecutionInstructionKind.StringCodePoints valueId ->
            writeUnaryInstruction "stringCodePoints" valueId
        | ExecutionInstructionKind.StringToLower valueId ->
            writeUnaryInstruction "stringToLower" valueId
        | ExecutionInstructionKind.StringToUpper valueId ->
            writeUnaryInstruction "stringToUpper" valueId
        | ExecutionInstructionKind.StringDecodeUtf8Lossy valueId ->
            writeUnaryInstruction "stringDecodeUtf8Lossy" valueId
        | ExecutionInstructionKind.Float64RoundToInt64 valueId ->
            writeUnaryInstruction "float64RoundToInt64" valueId
        | ExecutionInstructionKind.Float64FloorToInt64 valueId ->
            writeUnaryInstruction "float64FloorToInt64" valueId
        | ExecutionInstructionKind.Float64Sqrt valueId ->
            writeUnaryInstruction "float64Sqrt" valueId
        | ExecutionInstructionKind.Float64Abs valueId ->
            writeUnaryInstruction "float64Abs" valueId
        | ExecutionInstructionKind.Float64Negate valueId ->
            writeUnaryInstruction "float64Negate" valueId
        | ExecutionInstructionKind.Float64Floor valueId ->
            writeUnaryInstruction "float64Floor" valueId
        | ExecutionInstructionKind.Float64Ceiling valueId ->
            writeUnaryInstruction "float64Ceiling" valueId
        | ExecutionInstructionKind.Float64Truncate valueId ->
            writeUnaryInstruction "float64Truncate" valueId
        | ExecutionInstructionKind.StringUtf8ByteCount valueId ->
            writeUnaryInstruction "stringUtf8ByteCount" valueId
        | ExecutionInstructionKind.StringFromCodePoint valueId ->
            writeUnaryInstruction "stringFromCodePoint" valueId
        | ExecutionInstructionKind.StringToCodePoint valueId ->
            writeUnaryInstruction "stringToCodePoint" valueId
        | ExecutionInstructionKind.StringLength valueId ->
            writeUnaryInstruction "stringLength" valueId
        | ExecutionInstructionKind.BoolNot valueId ->
            writeUnaryInstruction "boolNot" valueId
        | ExecutionInstructionKind.StringSlice(valueId, startId, endId) ->
            [ property "kind" (quote "stringSlice")
              property "valueId" (executionValueIdValue valueId)
              property "startId" (executionValueIdValue startId)
              property "endId" (executionValueIdValue endId) ]
        | ExecutionInstructionKind.StringContains(valueId, substringId) ->
            [ property "kind" (quote "stringContains")
              property "valueId" (executionValueIdValue valueId)
              property "substringId" (executionValueIdValue substringId) ]
        | ExecutionInstructionKind.StringIndexOf(valueId, substringId, fromId) ->
            [ property "kind" (quote "stringIndexOf")
              property "valueId" (executionValueIdValue valueId)
              property "substringId" (executionValueIdValue substringId)
              property "fromId" (executionValueIdValue fromId) ]
        | ExecutionInstructionKind.StringSpan(valueId, setId, fromId) ->
            [ property "kind" (quote "stringSpan")
              property "valueId" (executionValueIdValue valueId)
              property "setId" (executionValueIdValue setId)
              property "fromId" (executionValueIdValue fromId) ]
        | ExecutionInstructionKind.StringSpanBack(valueId, setId, endId) ->
            [ property "kind" (quote "stringSpanBack")
              property "valueId" (executionValueIdValue valueId)
              property "setId" (executionValueIdValue setId)
              property "endId" (executionValueIdValue endId) ]
        | ExecutionInstructionKind.StringLastIndexOf(valueId, otherId) ->
            [ property "kind" (quote "stringLastIndexOf")
              property "valueId" (executionValueIdValue valueId)
              property "substringId" (executionValueIdValue otherId) ]
        | ExecutionInstructionKind.StringStartsWith(valueId, otherId) ->
            [ property "kind" (quote "stringStartsWith")
              property "valueId" (executionValueIdValue valueId)
              property "prefixId" (executionValueIdValue otherId) ]
        | ExecutionInstructionKind.StringEndsWith(valueId, otherId) ->
            [ property "kind" (quote "stringEndsWith")
              property "valueId" (executionValueIdValue valueId)
              property "suffixId" (executionValueIdValue otherId) ]
        | ExecutionInstructionKind.StringSplit(valueId, otherId) ->
            [ property "kind" (quote "stringSplit")
              property "valueId" (executionValueIdValue valueId)
              property "separatorId" (executionValueIdValue otherId) ]
        | ExecutionInstructionKind.StringSplitLines(valueId, breaksId) ->
            [ property "kind" (quote "stringSplitLines")
              property "valueId" (executionValueIdValue valueId)
              property "breaksId" (executionValueIdValue breaksId) ]
        | ExecutionInstructionKind.StringJoin(listValueId, separatorId) ->
            [ property "kind" (quote "stringJoin")
              property "listValueId" (executionValueIdValue listValueId)
              property "separatorId" (executionValueIdValue separatorId) ]
        | ExecutionInstructionKind.StringEqual(leftValueId, rightValueId) ->
            writeBinaryInstruction "stringEqual" leftValueId rightValueId
        | ExecutionInstructionKind.StringLessThan(leftValueId, rightValueId) ->
            writeBinaryInstruction "stringLessThan" leftValueId rightValueId
        | ExecutionInstructionKind.ValueEqual(leftValueId, rightValueId) ->
            writeBinaryInstruction "valueEqual" leftValueId rightValueId
        | ExecutionInstructionKind.StringConcat(leftValueId, rightValueId) ->
            writeBinaryInstruction "stringConcat" leftValueId rightValueId
        | ExecutionInstructionKind.Int64WrappingAdd(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64WrappingAdd" leftValueId rightValueId
        | ExecutionInstructionKind.Int64WrappingSubtract(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64WrappingSubtract" leftValueId rightValueId
        | ExecutionInstructionKind.Int64WrappingMultiply(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64WrappingMultiply" leftValueId rightValueId
        | ExecutionInstructionKind.Int64FloorDivide(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64FloorDivide" leftValueId rightValueId
        | ExecutionInstructionKind.Int64FloorModulo(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64FloorModulo" leftValueId rightValueId
        | ExecutionInstructionKind.Int64Min(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64Min" leftValueId rightValueId
        | ExecutionInstructionKind.Int64Max(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64Max" leftValueId rightValueId
        | ExecutionInstructionKind.Int64CheckedAdd(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64CheckedAdd" leftValueId rightValueId
        | ExecutionInstructionKind.Int64CheckedSubtract(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64CheckedSubtract" leftValueId rightValueId
        | ExecutionInstructionKind.Int64CheckedMultiply(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64CheckedMultiply" leftValueId rightValueId
        | ExecutionInstructionKind.ListRange(startValueId, endValueId) ->
            [ property "kind" (quote "listRange")
              property "startValueId" (executionValueIdValue startValueId)
              property "endValueId" (executionValueIdValue endValueId) ]
        | ExecutionInstructionKind.StringRepeat(textValueId, countValueId) ->
            [ property "kind" (quote "stringRepeat")
              property "textValueId" (executionValueIdValue textValueId)
              property "countValueId" (executionValueIdValue countValueId) ]
        | ExecutionInstructionKind.Float64Round(valueId, digitsValueId) ->
            [ property "kind" (quote "float64Round")
              property "valueId" (executionValueIdValue valueId)
              property "digitsValueId" (executionValueIdValue digitsValueId) ]
        | ExecutionInstructionKind.Int64Equal(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64Equal" leftValueId rightValueId
        | ExecutionInstructionKind.Int64LessThan(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64LessThan" leftValueId rightValueId
        | ExecutionInstructionKind.Int64LessThanOrEqual(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64LessThanOrEqual" leftValueId rightValueId
        | ExecutionInstructionKind.Int64GreaterThan(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64GreaterThan" leftValueId rightValueId
        | ExecutionInstructionKind.Int64GreaterThanOrEqual(leftValueId, rightValueId) ->
            writeBinaryInstruction "int64GreaterThanOrEqual" leftValueId rightValueId
        | ExecutionInstructionKind.Float64Add(leftValueId, rightValueId) ->
            writeBinaryInstruction "float64Add" leftValueId rightValueId
        | ExecutionInstructionKind.Float64Subtract(leftValueId, rightValueId) ->
            writeBinaryInstruction "float64Subtract" leftValueId rightValueId
        | ExecutionInstructionKind.Float64Multiply(leftValueId, rightValueId) ->
            writeBinaryInstruction "float64Multiply" leftValueId rightValueId
        | ExecutionInstructionKind.Float64Divide(leftValueId, rightValueId) ->
            writeBinaryInstruction "float64Divide" leftValueId rightValueId
        | ExecutionInstructionKind.Float64Equal(leftValueId, rightValueId) ->
            writeBinaryInstruction "float64Equal" leftValueId rightValueId
        | ExecutionInstructionKind.Float64LessThan(leftValueId, rightValueId) ->
            writeBinaryInstruction "float64LessThan" leftValueId rightValueId
        | ExecutionInstructionKind.Float64IsNaN valueId ->
            writeUnaryInstruction "float64IsNaN" valueId
        | ExecutionInstructionKind.Float64IsFinite valueId ->
            writeUnaryInstruction "float64IsFinite" valueId
        | ExecutionInstructionKind.Float64IsInfinite valueId ->
            writeUnaryInstruction "float64IsInfinite" valueId
        | ExecutionInstructionKind.Int64ToFloat64 valueId ->
            writeUnaryInstruction "int64ToFloat64" valueId
        | ExecutionInstructionKind.Int64WrappingAbs valueId ->
            writeUnaryInstruction "int64WrappingAbs" valueId
        | ExecutionInstructionKind.Bits32FromInt64 valueId -> writeUnaryInstruction "bits32FromInt64" valueId
        | ExecutionInstructionKind.Bits32ToInt64 valueId -> writeUnaryInstruction "bits32ToInt64" valueId
        | ExecutionInstructionKind.Bits32Not valueId -> writeUnaryInstruction "bits32Not" valueId
        | ExecutionInstructionKind.Bits32Xor(leftValueId, rightValueId) -> writeBinaryInstruction "bits32Xor" leftValueId rightValueId
        | ExecutionInstructionKind.Bits32And(leftValueId, rightValueId) -> writeBinaryInstruction "bits32And" leftValueId rightValueId
        | ExecutionInstructionKind.Bits32Or(leftValueId, rightValueId) -> writeBinaryInstruction "bits32Or" leftValueId rightValueId
        | ExecutionInstructionKind.Bits32Add(leftValueId, rightValueId) -> writeBinaryInstruction "bits32Add" leftValueId rightValueId
        | ExecutionInstructionKind.Bits32RotateRight(valueId, amountValueId) ->
            [ property "kind" (quote "bits32RotateRight")
              property "valueId" (executionValueIdValue valueId)
              property "amountValueId" (executionValueIdValue amountValueId) ]
        | ExecutionInstructionKind.Bits32ShiftRight(valueId, amountValueId) ->
            [ property "kind" (quote "bits32ShiftRight")
              property "valueId" (executionValueIdValue valueId)
              property "amountValueId" (executionValueIdValue amountValueId) ]
        | ExecutionInstructionKind.Bits32ShiftLeft(valueId, amountValueId) ->
            [ property "kind" (quote "bits32ShiftLeft")
              property "valueId" (executionValueIdValue valueId)
              property "amountValueId" (executionValueIdValue amountValueId) ]
        | ExecutionInstructionKind.ListEmpty ->
            [ property "kind" (quote "listEmpty") ]
        | ExecutionInstructionKind.SetEmpty ->
            [ property "kind" (quote "setEmpty") ]
        | ExecutionInstructionKind.MapEmpty ->
            [ property "kind" (quote "mapEmpty") ]
        | ExecutionInstructionKind.ListConstruct itemValueIds ->
            [ property "kind" (quote "listConstruct")
              // List item order is semantic. Do not sort.
              property "itemValueIds" (itemValueIds |> List.map executionValueIdValue |> arrayValue) ]
        | ExecutionInstructionKind.ListLength listValueId ->
            [ property "kind" (quote "listLength")
              property "listValueId" (executionValueIdValue listValueId) ]
        | ExecutionInstructionKind.MapValues mapValueId ->
            [ property "kind" (quote "mapValues")
              property "mapValueId" (executionValueIdValue mapValueId) ]
        | ExecutionInstructionKind.MapKeys mapValueId ->
            [ property "kind" (quote "mapKeys")
              property "mapValueId" (executionValueIdValue mapValueId) ]
        | ExecutionInstructionKind.SetToList setValueId ->
            [ property "kind" (quote "setToList")
              property "setValueId" (executionValueIdValue setValueId) ]
        | ExecutionInstructionKind.MapSize mapValueId ->
            [ property "kind" (quote "mapSize")
              property "mapValueId" (executionValueIdValue mapValueId) ]
        | ExecutionInstructionKind.SetFromList setValueId ->
            [ property "kind" (quote "setFromList")
              property "listValueId" (executionValueIdValue setValueId) ]
        | ExecutionInstructionKind.SetSize setValueId ->
            [ property "kind" (quote "setSize")
              property "setValueId" (executionValueIdValue setValueId) ]
        | ExecutionInstructionKind.ListReverse listValueId ->
            [ property "kind" (quote "listReverse")
              property "listValueId" (executionValueIdValue listValueId) ]
        | ExecutionInstructionKind.ListLast listValueId ->
            [ property "kind" (quote "listLast")
              property "listValueId" (executionValueIdValue listValueId) ]
        | ExecutionInstructionKind.ListDropLast listValueId ->
            [ property "kind" (quote "listDropLast")
              property "listValueId" (executionValueIdValue listValueId) ]
        | ExecutionInstructionKind.ListItemAt(listValueId, indexValueId) ->
            [ property "kind" (quote "listItemAt")
              property "listValueId" (executionValueIdValue listValueId)
              property "indexValueId" (executionValueIdValue indexValueId) ]
        | ExecutionInstructionKind.MapRemove(mapValueId, keyValueId) ->
            [ property "kind" (quote "mapRemove")
              property "mapValueId" (executionValueIdValue mapValueId)
              property "keyValueId" (executionValueIdValue keyValueId) ]
        | ExecutionInstructionKind.SetInsert(setValueId, itemValueId) ->
            [ property "kind" (quote "setInsert")
              property "setValueId" (executionValueIdValue setValueId)
              property "itemValueId" (executionValueIdValue itemValueId) ]
        | ExecutionInstructionKind.SetRemove(setValueId, itemValueId) ->
            [ property "kind" (quote "setRemove")
              property "setValueId" (executionValueIdValue setValueId)
              property "itemValueId" (executionValueIdValue itemValueId) ]
        | ExecutionInstructionKind.SetUnion(leftSetValueId, rightSetValueId) ->
            [ property "kind" (quote "setUnion")
              property "leftSetValueId" (executionValueIdValue leftSetValueId)
              property "rightSetValueId" (executionValueIdValue rightSetValueId) ]
        | ExecutionInstructionKind.SetIntersection(leftSetValueId, rightSetValueId) ->
            [ property "kind" (quote "setIntersection")
              property "leftSetValueId" (executionValueIdValue leftSetValueId)
              property "rightSetValueId" (executionValueIdValue rightSetValueId) ]
        | ExecutionInstructionKind.SetDifference(leftSetValueId, rightSetValueId) ->
            [ property "kind" (quote "setDifference")
              property "leftSetValueId" (executionValueIdValue leftSetValueId)
              property "rightSetValueId" (executionValueIdValue rightSetValueId) ]
        | ExecutionInstructionKind.MapContainsKey(mapValueId, keyValueId) ->
            [ property "kind" (quote "mapContainsKey")
              property "mapValueId" (executionValueIdValue mapValueId)
              property "keyValueId" (executionValueIdValue keyValueId) ]
        | ExecutionInstructionKind.SetContains(setValueId, itemValueId) ->
            [ property "kind" (quote "setContains")
              property "setValueId" (executionValueIdValue setValueId)
              property "itemValueId" (executionValueIdValue itemValueId) ]
        | ExecutionInstructionKind.MapGet(mapValueId, keyValueId) ->
            [ property "kind" (quote "mapGet")
              property "mapValueId" (executionValueIdValue mapValueId)
              property "keyValueId" (executionValueIdValue keyValueId) ]
        | ExecutionInstructionKind.MapGetOrDefault(mapValueId, keyValueId, defaultValueId) ->
            [ property "kind" (quote "mapGetOrDefault")
              property "mapValueId" (executionValueIdValue mapValueId)
              property "keyValueId" (executionValueIdValue keyValueId)
              property "defaultValueId" (executionValueIdValue defaultValueId) ]
        | ExecutionInstructionKind.ListSlice(listValueId, startId, endId) ->
            [ property "kind" (quote "listSlice")
              property "listValueId" (executionValueIdValue listValueId)
              property "startId" (executionValueIdValue startId)
              property "endId" (executionValueIdValue endId) ]
        | ExecutionInstructionKind.MapInsert(mapValueId, keyValueId, valueValueId) ->
            [ property "kind" (quote "mapInsert")
              property "mapValueId" (executionValueIdValue mapValueId)
              property "keyValueId" (executionValueIdValue keyValueId)
              property "valueValueId" (executionValueIdValue valueValueId) ]
        | ExecutionInstructionKind.ListAppendItem(listValueId, itemValueId) ->
            [ property "kind" (quote "listAppendItem")
              property "listValueId" (executionValueIdValue listValueId)
              property "itemValueId" (executionValueIdValue itemValueId) ]
        | ExecutionInstructionKind.ListConcat(leftListValueId, rightListValueId) ->
            [ property "kind" (quote "listConcat")
              property "leftListValueId" (executionValueIdValue leftListValueId)
              property "rightListValueId" (executionValueIdValue rightListValueId) ]
        | ExecutionInstructionKind.ListFold(sourceListValueId, seedValueId, folderFunctionId, context) ->
            [ property "kind" (quote "listFold")
              property "sourceListValueId" (executionValueIdValue sourceListValueId)
              property "seedValueId" (executionValueIdValue seedValueId)
              property "folderFunctionId" (stableIdValue folderFunctionId) ]
            @ writeCallbackContext context
        | ExecutionInstructionKind.MapFold(sourceMapValueId, seedValueId, folderFunctionId, context) ->
            [ property "kind" (quote "mapFold")
              property "sourceMapValueId" (executionValueIdValue sourceMapValueId)
              property "seedValueId" (executionValueIdValue seedValueId)
              property "folderFunctionId" (stableIdValue folderFunctionId) ]
            @ writeCallbackContext context
        | ExecutionInstructionKind.ListEffectFold(sourceListValueId, seedValueId, folderFunctionId, context) ->
            [ property "kind" (quote "listEffectFold")
              property "sourceListValueId" (executionValueIdValue sourceListValueId)
              property "seedValueId" (executionValueIdValue seedValueId)
              property "folderFunctionId" (stableIdValue folderFunctionId) ]
            @ writeCallbackContext context
        | ExecutionInstructionKind.UseResource(acquireValueId, releaseCapabilityValueId, releaseOperationId, bodyFunctionId, context) ->
            [ property "kind" (quote "useResource")
              property "acquireValueId" (executionValueIdValue acquireValueId)
              property "releaseCapabilityValueId" (executionValueIdValue releaseCapabilityValueId)
              property "releaseOperationId" (stableIdValue releaseOperationId)
              property "bodyFunctionId" (stableIdValue bodyFunctionId) ]
            @ writeCallbackContext context
        | ExecutionInstructionKind.ParallelAll(sourceListValueId, workerFunctionId, context) ->
            [ property "kind" (quote "parallelAll")
              property "sourceListValueId" (executionValueIdValue sourceListValueId)
              property "workerFunctionId" (stableIdValue workerFunctionId) ]
            @ writeCallbackContext context
        | ExecutionInstructionKind.CancellationRequested -> [ property "kind" (quote "cancellationRequested") ]
        | ExecutionInstructionKind.TaskScope(bodyFunctionId, context) ->
            [ property "kind" (quote "taskScope")
              property "bodyFunctionId" (stableIdValue bodyFunctionId) ]
            @ writeCallbackContext context
        | ExecutionInstructionKind.TaskStart(scopeValueId, workerFunctionId, context, itemValueId) ->
            [ property "kind" (quote "taskStart")
              property "scopeValueId" (executionValueIdValue scopeValueId)
              property "workerFunctionId" (stableIdValue workerFunctionId)
              property "itemValueId" (executionValueIdValue itemValueId) ]
            @ writeCallbackContext context
        | ExecutionInstructionKind.TaskWait(tasksValueId, mode, limitValueId) ->
            [ property "kind" (quote "taskWait")
              property "tasksValueId" (executionValueIdValue tasksValueId)
              property "mode" (quote (match mode with TaskWaitMode.Any -> "any" | TaskWaitMode.All -> "all")) ]
            @ (limitValueId |> Option.map (fun limit -> property "limitValueId" (executionValueIdValue limit)) |> Option.toList)
        | ExecutionInstructionKind.TaskCancel taskValueId ->
            [ property "kind" (quote "taskCancel")
              property "taskValueId" (executionValueIdValue taskValueId) ]
        | ExecutionInstructionKind.ListMap(sourceListValueId, mapperFunctionId, context) ->
            writeListCallbackInstruction "listMap" sourceListValueId "mapperFunctionId" mapperFunctionId context
        | ExecutionInstructionKind.MapFromList(sourceListValueId, keyOfFunctionId, context) ->
            writeListCallbackInstruction "mapFromList" sourceListValueId "keyOfFunctionId" keyOfFunctionId context
        | ExecutionInstructionKind.ListFilter(sourceListValueId, predicateFunctionId, context) ->
            writeListCallbackInstruction "listFilter" sourceListValueId "predicateFunctionId" predicateFunctionId context
        | ExecutionInstructionKind.MapFilter(sourceMapValueId, keepFunctionId, context) ->
            [ property "kind" (quote "mapFilter")
              property "sourceMapValueId" (executionValueIdValue sourceMapValueId)
              property "keepFunctionId" (stableIdValue keepFunctionId) ]
            @ writeCallbackContext context
        | ExecutionInstructionKind.ListSortBy(sourceListValueId, lessThanFunctionId, context) ->
            writeListCallbackInstruction "listSortBy" sourceListValueId "lessThanFunctionId" lessThanFunctionId context
        | ExecutionInstructionKind.ListMinBy(sourceListValueId, lessThanFunctionId, context) ->
            writeListCallbackInstruction "listMinBy" sourceListValueId "lessThanFunctionId" lessThanFunctionId context
        | ExecutionInstructionKind.MapMapValues(sourceMapValueId, mapperFunctionId, context) ->
            [ property "kind" (quote "mapMapValues")
              property "sourceMapValueId" (executionValueIdValue sourceMapValueId)
              property "mapperFunctionId" (stableIdValue mapperFunctionId) ]
            @ writeCallbackContext context
        | ExecutionInstructionKind.ListAny(sourceListValueId, predicateFunctionId, context) ->
            writeListCallbackInstruction "listAny" sourceListValueId "predicateFunctionId" predicateFunctionId context
        | ExecutionInstructionKind.ListAll(sourceListValueId, predicateFunctionId, context) ->
            writeListCallbackInstruction "listAll" sourceListValueId "predicateFunctionId" predicateFunctionId context
        | ExecutionInstructionKind.CapabilityConstruct(capabilityId, implementations) ->
            [ property "kind" (quote "capabilityConstruct"); property "capabilityId" (stableIdValue capabilityId)
              property "implementations" (implementations |> List.sortBy fst |> List.map (fun (operationId, functionId) ->
                  objectValue [ property "operationId" (stableIdValue operationId); property "functionId" (stableIdValue functionId) ]) |> arrayValue) ]
        | ExecutionInstructionKind.FunctionReference target ->
            [ property "kind" (quote "functionReference"); property "functionId" (stableIdValue target) ]
        | ExecutionInstructionKind.FunctionInvoke(value, arguments) ->
            [ property "kind" (quote "functionInvoke"); property "functionValueId" (executionValueIdValue value)
              property "arguments" (arguments |> List.map executionValueIdValue |> arrayValue) ]
        | ExecutionInstructionKind.DirectCall(functionId, arguments) ->
            [ property "kind" (quote "directCall")
              property "functionId" (stableIdValue functionId)
              property
                  "arguments"
                  (arguments
                   |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId)
                   |> List.map writeCallArgument
                   |> arrayValue) ]
        | ExecutionInstructionKind.CapabilityInvoke(capabilityValueId, operationId, arguments) ->
            [ property "kind" (quote "capabilityInvoke")
              property "capabilityValueId" (executionValueIdValue capabilityValueId)
              property "operationId" (stableIdValue operationId)
              property
                  "arguments"
                  (arguments
                   |> List.sortBy (fun argument -> StableId.value argument.TargetCapabilityParameterId)
                   |> List.map writeCapabilityArgument
                   |> arrayValue) ]
        | ExecutionInstructionKind.ProductConstruct fields ->
            [ property "kind" (quote "productConstruct")
              property
                  "fields"
                  (fields
                   |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
                   |> List.map writeFieldValue
                   |> arrayValue) ]
        | ExecutionInstructionKind.FieldAccess(sourceValueId, fieldId) ->
            [ property "kind" (quote "fieldAccess")
              property "sourceValueId" (executionValueIdValue sourceValueId)
              property "fieldId" (stableIdValue fieldId) ]
        | ExecutionInstructionKind.ProductUpdate(sourceValueId, fields) ->
            [ property "kind" (quote "productUpdate")
              property "sourceValueId" (executionValueIdValue sourceValueId)
              property
                  "fields"
                  (fields
                   |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
                   |> List.map writeFieldValue
                   |> arrayValue) ]
        | ExecutionInstructionKind.UnionConstruct(variantId, fields) ->
            [ property "kind" (quote "unionConstruct")
              property "variantId" (stableIdValue variantId)
              property
                  "fields"
                  (fields
                   |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
                   |> List.map writeFieldValue
                   |> arrayValue) ]
        | ExecutionInstructionKind.OptionNone ->
            [ property "kind" (quote "optionNone") ]
        | ExecutionInstructionKind.OptionSome valueId ->
            writeUnaryInstruction "optionSome" valueId
        | ExecutionInstructionKind.ResultOk valueId ->
            writeUnaryInstruction "resultOk" valueId
        | ExecutionInstructionKind.ResultErr errorValueId ->
            [ property "kind" (quote "resultErr")
              property "errorValueId" (executionValueIdValue errorValueId) ]
        | ExecutionInstructionKind.ExtractUnionField(inputValueId, variantId, fieldId) ->
            [ property "kind" (quote "extractUnionField")
              property "inputValueId" (executionValueIdValue inputValueId)
              property "variantId" (stableIdValue variantId)
              property "fieldId" (stableIdValue fieldId) ]
        | ExecutionInstructionKind.ExtractOptionSome inputValueId ->
            [ property "kind" (quote "extractOptionSome")
              property "inputValueId" (executionValueIdValue inputValueId) ]
        | ExecutionInstructionKind.ExtractResultOk inputValueId ->
            [ property "kind" (quote "extractResultOk")
              property "inputValueId" (executionValueIdValue inputValueId) ]
        | ExecutionInstructionKind.ExtractResultErr inputValueId ->
            [ property "kind" (quote "extractResultErr")
              property "inputValueId" (executionValueIdValue inputValueId) ]

    let private writeInstruction (instruction: ExecutionInstruction): string =
        objectValue
            ([ property "result" (writeValue instruction.Result)
               property "originNodeId" (stableIdValue instruction.OriginNodeId) ]
             @ writeInstructionKind instruction.Kind)

    let private writeJumpArgument (argument: ExecutionJumpArgument): string =
        objectValue
            [ property "targetValueId" (executionValueIdValue argument.TargetValueId)
              property "valueId" (executionValueIdValue argument.ValueId) ]

    let private writeUnionTarget (target: ExecutionUnionTarget): string =
        objectValue
            [ property "variantId" (stableIdValue target.VariantId)
              property "blockId" (executionBlockIdValue target.BlockId) ]

    let private writeTerminator (terminator: ExecutionTerminator): string =
        match terminator with
        | ExecutionTerminator.Abort(nodeId, valueId) ->
            objectValue
                [ property "kind" (quote "abort")
                  property "originNodeId" (stableIdValue nodeId)
                  property "errorValueId" (executionValueIdValue valueId) ]
        | ExecutionTerminator.Return valueId ->
            objectValue
                [ property "kind" (quote "return")
                  property "valueId" (executionValueIdValue valueId) ]
        | ExecutionTerminator.Jump(blockId, arguments) ->
            objectValue
                [ property "kind" (quote "jump")
                  property "blockId" (executionBlockIdValue blockId)
                  property
                      "arguments"
                      (arguments
                       |> List.sortBy (fun argument -> ExecutionValueId.value argument.TargetValueId)
                       |> List.map writeJumpArgument
                       |> arrayValue) ]
        | ExecutionTerminator.Branch(conditionValueId, whenTrue, whenFalse) ->
            objectValue
                [ property "kind" (quote "branch")
                  property "conditionValueId" (executionValueIdValue conditionValueId)
                  property "whenTrue" (executionBlockIdValue whenTrue)
                  property "whenFalse" (executionBlockIdValue whenFalse) ]
        | ExecutionTerminator.MatchUnion(inputValueId, targets) ->
            objectValue
                [ property "kind" (quote "matchUnion")
                  property "inputValueId" (executionValueIdValue inputValueId)
                  property
                      "targets"
                      (targets
                       |> List.sortBy (fun target -> StableId.value target.VariantId)
                       |> List.map writeUnionTarget
                       |> arrayValue) ]
        | ExecutionTerminator.MatchOption(inputValueId, someBlock, noneBlock) ->
            objectValue
                [ property "kind" (quote "matchOption")
                  property "inputValueId" (executionValueIdValue inputValueId)
                  property "someBlock" (executionBlockIdValue someBlock)
                  property "noneBlock" (executionBlockIdValue noneBlock) ]
        | ExecutionTerminator.MatchResult(inputValueId, okBlock, errBlock) ->
            objectValue
                [ property "kind" (quote "matchResult")
                  property "inputValueId" (executionValueIdValue inputValueId)
                  property "okBlock" (executionBlockIdValue okBlock)
                  property "errBlock" (executionBlockIdValue errBlock) ]
        | ExecutionTerminator.TailCall(nodeId, arguments) ->
            objectValue
                [ property "kind" (quote "tailCall")
                  property "originNodeId" (stableIdValue nodeId)
                  property
                      "arguments"
                      (arguments
                       |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId)
                       |> List.map writeCallArgument
                       |> arrayValue) ]

    let private writeBlock (block: ExecutionBlock): string =
        objectValue
            [ property "id" (executionBlockIdValue block.Id)
              property "parameters" (block.Parameters |> List.map writeValue |> arrayValue)
              // Instruction order is executable meaning. Do not sort.
              property "instructions" (block.Instructions |> List.map writeInstruction |> arrayValue)
              property "terminator" (writeTerminator block.Terminator) ]

    let private writeParameter (parameter: ExecutionParameter): string =
        objectValue
            [ property "semanticParameterId" (stableIdValue parameter.SemanticParameterId)
              property "value" (writeValue parameter.Value) ]

    let private writeFunction (functionEntity: ExecutionFunction): string =
        objectValue
            [ property "semanticFunctionId" (stableIdValue functionEntity.SemanticFunctionId)
              property "parameters" (functionEntity.Parameters |> List.map writeParameter |> arrayValue)
              property "resultType" (writeTypeRef functionEntity.ResultType)
              property
                  "requiredEffects"
                  (functionEntity.RequiredEffects
                   |> List.sortBy StableId.value
                   |> List.map stableIdValue
                   |> arrayValue)
              property "entryBlockId" (executionBlockIdValue functionEntity.EntryBlockId)
              property
                  "blocks"
                  (functionEntity.Blocks
                   |> List.sortBy (fun block -> ExecutionBlockId.value block.Id)
                   |> List.map writeBlock
                   |> arrayValue) ]

    let serialize (program: ExecutionProgram): string =
        let blocks = program.Functions |> List.collect (fun fn -> fn.Blocks)
        let hasLimit =
            blocks |> List.exists (fun block ->
                block.Instructions |> List.exists (fun instruction ->
                    match instruction.Kind with
                    | ExecutionInstructionKind.TaskWait(_, _, Some _) -> true
                    | _ -> false))
        let hasAbort = blocks |> List.exists (fun block -> match block.Terminator with ExecutionTerminator.Abort _ -> true | _ -> false)
        let hasTailCall = blocks |> List.exists (fun block -> match block.Terminator with ExecutionTerminator.TailCall _ -> true | _ -> false)
        let version = if hasTailCall then schemaVersion elif hasLimit then 8 elif hasAbort then 7 else 6
        objectValue
            [ property "schemaVersion" (string version)
              property "sourceSnapshotId" (stableIdValue program.SourceSnapshotId)
              property
                  "types"
                  (program.Types
                   |> List.sortBy (fun typeEntity -> StableId.value typeEntity.Id)
                   |> List.map writeType
                   |> arrayValue)
              property
                  "effects"
                  (program.Effects
                   |> List.sortBy StableId.value
                   |> List.map stableIdValue
                   |> arrayValue)
              property
                  "capabilities"
                  (program.Capabilities
                   |> List.sortBy (fun capability -> StableId.value capability.Id)
                   |> List.map writeCapability
                   |> arrayValue)
              property
                  "functions"
                  (program.Functions
                   |> List.sortBy (fun functionEntity -> StableId.value functionEntity.SemanticFunctionId)
                   |> List.map writeFunction
                   |> arrayValue) ]
