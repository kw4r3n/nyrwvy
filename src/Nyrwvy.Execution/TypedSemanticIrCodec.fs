namespace Nyrwvy.Core

open System.IO
open System.Text
open System.Text.Json

/// Explicit canonical serialization for the derived Typed Semantic IR.
/// This is an IR schema, not a semantic-graph persistence format.
[<RequireQualifiedAccess>]
module TypedSemanticIrCodec =
    let schemaVersion = 9

    let rec private writeTypeRef (writer: Utf8JsonWriter) (typeRef: TypeRef) =
        writer.WriteStartObject()
        match typeRef with
        | TypeRef.Primitive PrimitiveType.String ->
            writer.WriteString("kind", "primitiveString")
        | TypeRef.Primitive PrimitiveType.Bool ->
            writer.WriteString("kind", "primitiveBool")
        | TypeRef.Primitive PrimitiveType.Unit ->
            writer.WriteString("kind", "primitiveUnit")
        | TypeRef.Primitive PrimitiveType.Int64 ->
            writer.WriteString("kind", "primitiveInt64")
        | TypeRef.Primitive PrimitiveType.Float64 ->
            writer.WriteString("kind", "primitiveFloat64")
        | TypeRef.Primitive PrimitiveType.Bits32 ->
            writer.WriteString("kind", "primitiveBits32")
        | TypeRef.Function(arguments, result) ->
            writer.WriteString("kind", "function")
            writer.WritePropertyName("parameters")
            writer.WriteStartArray()
            arguments |> List.iter (writeTypeRef writer)
            writer.WriteEndArray()
            writer.WritePropertyName("resultType")
            writeTypeRef writer result
        | TypeRef.Named typeId ->
            writer.WriteString("kind", "named")
            writer.WriteString("typeId", StableId.value typeId)
        | TypeRef.TypeParameter(typeParameterId, displayName) ->
            writer.WriteString("kind", "typeParameter")
            writer.WriteString("typeParameterId", StableId.value typeParameterId)
            writer.WriteString("displayName", displayName)
        | TypeRef.Capability capabilityId ->
            writer.WriteString("kind", "capability")
            writer.WriteString("capabilityId", StableId.value capabilityId)
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ]) ->
            writer.WriteString("kind", "option")
            writer.WritePropertyName("itemType")
            writeTypeRef writer itemType
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
            writer.WriteString("kind", "result")
            writer.WritePropertyName("okType")
            writeTypeRef writer okType
            writer.WritePropertyName("errorType")
            writeTypeRef writer errorType
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ]) ->
            writer.WriteString("kind", "list")
            writer.WritePropertyName("itemType")
            writeTypeRef writer itemType
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ resultType ]) ->
            writer.WriteString("kind", "task")
            writer.WritePropertyName("resultType")
            writeTypeRef writer resultType
        | TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, []) -> writer.WriteString("kind", "taskScope")
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Task, arguments) ->
            invalidOp $"Typed Semantic IR contains Task with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, arguments) ->
            invalidOp $"Typed Semantic IR contains TaskScope with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, arguments) ->
            invalidOp $"Typed Semantic IR contains Option with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, arguments) ->
            invalidOp $"Typed Semantic IR contains Result with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, arguments) ->
            invalidOp $"Typed Semantic IR contains List with {arguments.Length} type arguments."
        writer.WriteEndObject()

    let private writeField (writer: Utf8JsonWriter) (field: TypedSemanticField) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value field.Id)
        writer.WritePropertyName("type")
        writeTypeRef writer field.Type
        writer.WriteEndObject()

    let private writeVariant (writer: Utf8JsonWriter) (variant: TypedSemanticVariant) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value variant.Id)
        writer.WritePropertyName("fields")
        writer.WriteStartArray()
        variant.Fields
        |> List.sortBy (fun field -> StableId.value field.Id)
        |> List.iter (writeField writer)
        writer.WriteEndArray()
        writer.WriteEndObject()

    let private writeType (writer: Utf8JsonWriter) (typeEntity: TypedSemanticType) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value typeEntity.Id)
        match typeEntity.Kind with
        | TypedSemanticTypeKind.Product fields ->
            writer.WriteString("kind", "product")
            writer.WritePropertyName("fields")
            writer.WriteStartArray()
            fields
            |> List.sortBy (fun field -> StableId.value field.Id)
            |> List.iter (writeField writer)
            writer.WriteEndArray()
        | TypedSemanticTypeKind.Union variants ->
            writer.WriteString("kind", "union")
            writer.WritePropertyName("variants")
            writer.WriteStartArray()
            variants
            |> List.sortBy (fun variant -> StableId.value variant.Id)
            |> List.iter (writeVariant writer)
            writer.WriteEndArray()
        writer.WriteEndObject()

    let private writeCapabilityParameter
        (writer: Utf8JsonWriter)
        (parameter: TypedSemanticCapabilityParameter)
        =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value parameter.Id)
        writer.WritePropertyName("type")
        writeTypeRef writer parameter.Type
        writer.WriteEndObject()

    let private writeCapabilityOperation
        (writer: Utf8JsonWriter)
        (operation: TypedSemanticCapabilityOperation)
        =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value operation.Id)
        writer.WritePropertyName("parameters")
        writer.WriteStartArray()
        operation.Parameters |> List.iter (writeCapabilityParameter writer)
        writer.WriteEndArray()
        writer.WritePropertyName("resultType")
        writeTypeRef writer operation.ResultType
        match operation.FailureType with
        | Some failureType ->
            writer.WritePropertyName("failureType")
            writeTypeRef writer failureType
        | None -> ()
        writer.WriteString("effectId", StableId.value operation.EffectId)
        writer.WriteEndObject()

    let private writeCapability (writer: Utf8JsonWriter) (capability: TypedSemanticCapability) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value capability.Id)
        writer.WritePropertyName("operations")
        writer.WriteStartArray()
        capability.Operations
        |> List.sortBy (fun operation -> StableId.value operation.Id)
        |> List.iter (writeCapabilityOperation writer)
        writer.WriteEndArray()
        writer.WriteEndObject()

    let private writeParameter (writer: Utf8JsonWriter) (parameter: TypedSemanticParameter) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value parameter.Id)
        writer.WritePropertyName("type")
        writeTypeRef writer parameter.Type
        writer.WriteEndObject()

    let private writeArgument (writer: Utf8JsonWriter) (argument: TypedSemanticArgument) =
        writer.WriteStartObject()
        writer.WriteString("targetParameterId", StableId.value argument.TargetParameterId)
        writer.WriteString("valueNodeId", StableId.value argument.ValueNodeId)
        writer.WriteEndObject()

    let private writeCapabilityArgument
        (writer: Utf8JsonWriter)
        (argument: TypedSemanticCapabilityArgument)
        =
        writer.WriteStartObject()
        writer.WriteString("targetCapabilityParameterId", StableId.value argument.TargetCapabilityParameterId)
        writer.WriteString("valueNodeId", StableId.value argument.ValueNodeId)
        writer.WriteEndObject()

    let private writeFieldValue (writer: Utf8JsonWriter) (binding: TypedSemanticFieldValue) =
        writer.WriteStartObject()
        writer.WriteString("targetFieldId", StableId.value binding.TargetFieldId)
        writer.WriteString("valueNodeId", StableId.value binding.ValueNodeId)
        writer.WriteEndObject()

    let private writePatternBinding
        (writer: Utf8JsonWriter)
        (binding: TypedSemanticPatternBinding)
        =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value binding.Id)
        writer.WritePropertyName("type")
        writeTypeRef writer binding.Type
        writer.WriteEndObject()

    let private writePatternField
        (writer: Utf8JsonWriter)
        (binding: TypedSemanticPatternField)
        =
        writer.WriteStartObject()
        writer.WriteString("sourceFieldId", StableId.value binding.SourceFieldId)
        writer.WritePropertyName("binding")
        writePatternBinding writer binding.Binding
        writer.WriteEndObject()

    let rec private writeGraph (writer: Utf8JsonWriter) (graph: TypedSemanticGraph) =
        writer.WriteStartObject()
        writer.WritePropertyName("nodes")
        writer.WriteStartArray()
        graph.Nodes
        |> List.sortBy (fun node -> StableId.value node.OriginNodeId)
        |> List.iter (writeNode writer)
        writer.WriteEndArray()
        writer.WriteString("resultNodeId", StableId.value graph.ResultNodeId)
        writer.WriteEndObject()

    and private writeUnionArm (writer: Utf8JsonWriter) (arm: TypedSemanticUnionArm) =
        writer.WriteStartObject()
        writer.WriteString("variantId", StableId.value arm.VariantId)
        writer.WritePropertyName("fieldBindings")
        writer.WriteStartArray()
        arm.FieldBindings
        |> List.sortBy (fun binding -> StableId.value binding.SourceFieldId)
        |> List.iter (writePatternField writer)
        writer.WriteEndArray()
        writer.WritePropertyName("body")
        writeGraph writer arm.Body
        writer.WriteEndObject()

    and private writeValueArm (writer: Utf8JsonWriter) (arm: TypedSemanticValueArm) =
        writer.WriteStartObject()
        writer.WritePropertyName("binding")
        writePatternBinding writer arm.ValueBinding
        writer.WritePropertyName("body")
        writeGraph writer arm.Body
        writer.WriteEndObject()

    and private writeErrorArm (writer: Utf8JsonWriter) (arm: TypedSemanticErrorArm) =
        writer.WriteStartObject()
        writer.WritePropertyName("binding")
        writePatternBinding writer arm.ErrorBinding
        writer.WritePropertyName("body")
        writeGraph writer arm.Body
        writer.WriteEndObject()

    and private writeBinaryNode
        (writer: Utf8JsonWriter)
        (kind: string)
        (leftNodeId: GraphNodeId)
        (rightNodeId: GraphNodeId)
        =
        writer.WriteString("kind", kind)
        writer.WriteString("leftNodeId", StableId.value leftNodeId)
        writer.WriteString("rightNodeId", StableId.value rightNodeId)

    and private writeUnaryNode
        (writer: Utf8JsonWriter)
        (kind: string)
        (valueNodeId: GraphNodeId)
        =
        writer.WriteString("kind", kind)
        writer.WriteString("valueNodeId", StableId.value valueNodeId)

    and private writeCallbackContext (writer: Utf8JsonWriter) (context: TypedSemanticArgument list) =
        if not (List.isEmpty context) then
            writer.WritePropertyName("context")
            writer.WriteStartArray()
            context
            |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId)
            |> List.iter (writeArgument writer)
            writer.WriteEndArray()

    and private writeListCallbackNode
        (writer: Utf8JsonWriter)
        (kind: string)
        (sourceListNodeId: GraphNodeId)
        (callbackFieldName: string)
        (callbackFunctionId: FunctionId)
        (context: TypedSemanticArgument list)
        =
        writer.WriteString("kind", kind)
        writer.WriteString("sourceListNodeId", StableId.value sourceListNodeId)
        writer.WriteString(callbackFieldName, StableId.value callbackFunctionId)
        writeCallbackContext writer context

    and private writeNode (writer: Utf8JsonWriter) (node: TypedSemanticNode) =
        writer.WriteStartObject()
        writer.WriteString("originNodeId", StableId.value node.OriginNodeId)
        match node.Completion with
        | GraphCompletion.Normal value ->
            writer.WritePropertyName("type")
            writeTypeRef writer value
        | GraphCompletion.Aborts -> writer.WriteString("completion", "abort")

        match node.Kind with
        | TypedSemanticNodeKind.ParameterReference parameterId ->
            writer.WriteString("kind", "parameterReference")
            writer.WriteString("parameterId", StableId.value parameterId)
        | TypedSemanticNodeKind.PatternReference bindingId ->
            writer.WriteString("kind", "patternReference")
            writer.WriteString("bindingId", StableId.value bindingId)
        | TypedSemanticNodeKind.StringLiteral value ->
            writer.WriteString("kind", "stringLiteral")
            writer.WriteString("value", value)
        | TypedSemanticNodeKind.BoolLiteral value ->
            writer.WriteString("kind", "boolLiteral")
            writer.WriteBoolean("value", value)
        | TypedSemanticNodeKind.UnitLiteral ->
            writer.WriteString("kind", "unitLiteral")
        | TypedSemanticNodeKind.Int64Literal value ->
            writer.WriteString("kind", "int64Literal")
            writer.WriteNumber("value", value)
        | TypedSemanticNodeKind.Float64Literal bits ->
            writer.WriteString("kind", "float64Literal")
            writer.WriteString("bits", Float64Bits.toHex bits)
        | TypedSemanticNodeKind.StringIsEmpty valueNodeId ->
            writeUnaryNode writer "stringIsEmpty" valueNodeId
        | TypedSemanticNodeKind.Int64ToString valueNodeId ->
            writeUnaryNode writer "int64ToString" valueNodeId
        | TypedSemanticNodeKind.Int64ToHexString valueNodeId ->
            writeUnaryNode writer "int64ToHexString" valueNodeId
        | TypedSemanticNodeKind.StringParseInt64 valueNodeId ->
            writeUnaryNode writer "stringParseInt64" valueNodeId
        | TypedSemanticNodeKind.StringParseInt64Hex valueNodeId ->
            writeUnaryNode writer "stringParseInt64Hex" valueNodeId
        | TypedSemanticNodeKind.Float64ToString valueNodeId ->
            writeUnaryNode writer "float64ToString" valueNodeId
        | TypedSemanticNodeKind.StringParseFloat64 valueNodeId ->
            writeUnaryNode writer "stringParseFloat64" valueNodeId
        | TypedSemanticNodeKind.StringCodePoints valueNodeId ->
            writeUnaryNode writer "stringCodePoints" valueNodeId
        | TypedSemanticNodeKind.StringToLower valueNodeId ->
            writeUnaryNode writer "stringToLower" valueNodeId
        | TypedSemanticNodeKind.StringDecodeUtf8Lossy valueNodeId ->
            writeUnaryNode writer "stringDecodeUtf8Lossy" valueNodeId
        | TypedSemanticNodeKind.Float64RoundToInt64 valueNodeId ->
            writeUnaryNode writer "float64RoundToInt64" valueNodeId
        | TypedSemanticNodeKind.Float64FloorToInt64 valueNodeId ->
            writeUnaryNode writer "float64FloorToInt64" valueNodeId
        | TypedSemanticNodeKind.Float64Sqrt valueNodeId ->
            writeUnaryNode writer "float64Sqrt" valueNodeId
        | TypedSemanticNodeKind.Float64Abs valueNodeId ->
            writeUnaryNode writer "float64Abs" valueNodeId
        | TypedSemanticNodeKind.Float64Negate valueNodeId ->
            writeUnaryNode writer "float64Negate" valueNodeId
        | TypedSemanticNodeKind.Float64Floor valueNodeId ->
            writeUnaryNode writer "float64Floor" valueNodeId
        | TypedSemanticNodeKind.Float64Ceiling valueNodeId ->
            writeUnaryNode writer "float64Ceiling" valueNodeId
        | TypedSemanticNodeKind.Float64Truncate valueNodeId ->
            writeUnaryNode writer "float64Truncate" valueNodeId
        | TypedSemanticNodeKind.StringUtf8ByteCount valueNodeId ->
            writeUnaryNode writer "stringUtf8ByteCount" valueNodeId
        | TypedSemanticNodeKind.StringFromCodePoint valueNodeId ->
            writeUnaryNode writer "stringFromCodePoint" valueNodeId
        | TypedSemanticNodeKind.StringToCodePoint valueNodeId ->
            writeUnaryNode writer "stringToCodePoint" valueNodeId
        | TypedSemanticNodeKind.StringLength valueNodeId ->
            writeUnaryNode writer "stringLength" valueNodeId
        | TypedSemanticNodeKind.StringSlice(valueNodeId, startNodeId, endNodeId) ->
            writer.WriteString("kind", "stringSlice")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("startNodeId", StableId.value startNodeId)
            writer.WriteString("endNodeId", StableId.value endNodeId)
        | TypedSemanticNodeKind.StringContains(valueNodeId, substringNodeId) ->
            writer.WriteString("kind", "stringContains")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("substringNodeId", StableId.value substringNodeId)
        | TypedSemanticNodeKind.StringIndexOf(valueNodeId, substringNodeId, fromNodeId) ->
            writer.WriteString("kind", "stringIndexOf")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("substringNodeId", StableId.value substringNodeId)
            writer.WriteString("fromNodeId", StableId.value fromNodeId)
        | TypedSemanticNodeKind.StringSpan(valueNodeId, setNodeId, fromNodeId) ->
            writer.WriteString("kind", "stringSpan")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("setNodeId", StableId.value setNodeId)
            writer.WriteString("fromNodeId", StableId.value fromNodeId)
        | TypedSemanticNodeKind.StringSpanBack(valueNodeId, setNodeId, endNodeId) ->
            writer.WriteString("kind", "stringSpanBack")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("setNodeId", StableId.value setNodeId)
            writer.WriteString("endNodeId", StableId.value endNodeId)
        | TypedSemanticNodeKind.StringLastIndexOf(valueNodeId, otherNodeId) ->
            writer.WriteString("kind", "stringLastIndexOf")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("substringNodeId", StableId.value otherNodeId)
        | TypedSemanticNodeKind.StringStartsWith(valueNodeId, otherNodeId) ->
            writer.WriteString("kind", "stringStartsWith")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("prefixNodeId", StableId.value otherNodeId)
        | TypedSemanticNodeKind.StringEndsWith(valueNodeId, otherNodeId) ->
            writer.WriteString("kind", "stringEndsWith")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("suffixNodeId", StableId.value otherNodeId)
        | TypedSemanticNodeKind.StringSplit(valueNodeId, otherNodeId) ->
            writer.WriteString("kind", "stringSplit")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("separatorNodeId", StableId.value otherNodeId)
        | TypedSemanticNodeKind.StringSplitLines(valueNodeId, breaksNodeId) ->
            writer.WriteString("kind", "stringSplitLines")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("breaksNodeId", StableId.value breaksNodeId)
        | TypedSemanticNodeKind.StringJoin(listNodeId, separatorNodeId) ->
            writer.WriteString("kind", "stringJoin")
            writer.WriteString("listNodeId", StableId.value listNodeId)
            writer.WriteString("separatorNodeId", StableId.value separatorNodeId)
        | TypedSemanticNodeKind.StringEqual(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "stringEqual" leftNodeId rightNodeId
        | TypedSemanticNodeKind.StringLessThan(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "stringLessThan" leftNodeId rightNodeId
        | TypedSemanticNodeKind.ValueEqual(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "valueEqual" leftNodeId rightNodeId
        | TypedSemanticNodeKind.StringConcat(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "stringConcat" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64WrappingAdd" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64WrappingSubtract" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64WrappingMultiply" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Int64FloorDivide(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64FloorDivide" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Int64FloorModulo(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64FloorModulo" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Int64Min(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64Min" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Int64Max(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64Max" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Int64CheckedAdd(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64CheckedAdd" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Int64CheckedSubtract(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64CheckedSubtract" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Int64CheckedMultiply(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64CheckedMultiply" leftNodeId rightNodeId
        | TypedSemanticNodeKind.ListRange(startNodeId, endNodeId) ->
            writer.WriteString("kind", "listRange")
            writer.WriteString("startNodeId", StableId.value startNodeId)
            writer.WriteString("endNodeId", StableId.value endNodeId)
        | TypedSemanticNodeKind.StringRepeat(textNodeId, countNodeId) ->
            writer.WriteString("kind", "stringRepeat")
            writer.WriteString("textNodeId", StableId.value textNodeId)
            writer.WriteString("countNodeId", StableId.value countNodeId)
        | TypedSemanticNodeKind.Int64Equal(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64Equal" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Int64LessThan(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64LessThan" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Int64LessThanOrEqual(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64LessThanOrEqual" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Int64GreaterThan(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64GreaterThan" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Int64GreaterThanOrEqual(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64GreaterThanOrEqual" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Float64Add(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "float64Add" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Float64Subtract(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "float64Subtract" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Float64Multiply(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "float64Multiply" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Float64Divide(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "float64Divide" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Float64Equal(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "float64Equal" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Float64LessThan(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "float64LessThan" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Float64IsNaN valueNodeId ->
            writeUnaryNode writer "float64IsNaN" valueNodeId
        | TypedSemanticNodeKind.Float64IsFinite valueNodeId ->
            writeUnaryNode writer "float64IsFinite" valueNodeId
        | TypedSemanticNodeKind.Float64IsInfinite valueNodeId ->
            writeUnaryNode writer "float64IsInfinite" valueNodeId
        | TypedSemanticNodeKind.Int64ToFloat64 valueNodeId ->
            writeUnaryNode writer "int64ToFloat64" valueNodeId
        | TypedSemanticNodeKind.Int64WrappingAbs valueNodeId ->
            writeUnaryNode writer "int64WrappingAbs" valueNodeId
        | TypedSemanticNodeKind.Bits32FromInt64 valueNodeId -> writeUnaryNode writer "bits32FromInt64" valueNodeId
        | TypedSemanticNodeKind.Bits32ToInt64 valueNodeId -> writeUnaryNode writer "bits32ToInt64" valueNodeId
        | TypedSemanticNodeKind.Bits32Not valueNodeId -> writeUnaryNode writer "bits32Not" valueNodeId
        | TypedSemanticNodeKind.Bits32Xor(leftNodeId, rightNodeId) -> writeBinaryNode writer "bits32Xor" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Bits32And(leftNodeId, rightNodeId) -> writeBinaryNode writer "bits32And" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Bits32Or(leftNodeId, rightNodeId) -> writeBinaryNode writer "bits32Or" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Bits32Add(leftNodeId, rightNodeId) -> writeBinaryNode writer "bits32Add" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Bits32RotateRight(valueNodeId, amountNodeId) ->
            writer.WriteString("kind", "bits32RotateRight")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("amountNodeId", StableId.value amountNodeId)
        | TypedSemanticNodeKind.Bits32ShiftRight(valueNodeId, amountNodeId) ->
            writer.WriteString("kind", "bits32ShiftRight")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("amountNodeId", StableId.value amountNodeId)
        | TypedSemanticNodeKind.Bits32ShiftLeft(valueNodeId, amountNodeId) ->
            writer.WriteString("kind", "bits32ShiftLeft")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("amountNodeId", StableId.value amountNodeId)
        | TypedSemanticNodeKind.ListEmpty ->
            writer.WriteString("kind", "listEmpty")
        | TypedSemanticNodeKind.ListConstruct itemNodeIds ->
            writer.WriteString("kind", "listConstruct")
            writer.WritePropertyName("itemNodeIds")
            writer.WriteStartArray()
            // List item order is semantic. Do not sort.
            itemNodeIds |> List.iter (fun itemNodeId -> writer.WriteStringValue(StableId.value itemNodeId))
            writer.WriteEndArray()
        | TypedSemanticNodeKind.ListLength listNodeId ->
            writer.WriteString("kind", "listLength")
            writer.WriteString("listNodeId", StableId.value listNodeId)
        | TypedSemanticNodeKind.ListReverse listNodeId ->
            writer.WriteString("kind", "listReverse")
            writer.WriteString("listNodeId", StableId.value listNodeId)
        | TypedSemanticNodeKind.ListItemAt(listNodeId, indexNodeId) ->
            writer.WriteString("kind", "listItemAt")
            writer.WriteString("listNodeId", StableId.value listNodeId)
            writer.WriteString("indexNodeId", StableId.value indexNodeId)
        | TypedSemanticNodeKind.ListSlice(listNodeId, startNodeId, endNodeId) ->
            writer.WriteString("kind", "listSlice")
            writer.WriteString("listNodeId", StableId.value listNodeId)
            writer.WriteString("startNodeId", StableId.value startNodeId)
            writer.WriteString("endNodeId", StableId.value endNodeId)
        | TypedSemanticNodeKind.ListAppendItem(listNodeId, itemNodeId) ->
            writer.WriteString("kind", "listAppendItem")
            writer.WriteString("listNodeId", StableId.value listNodeId)
            writer.WriteString("itemNodeId", StableId.value itemNodeId)
        | TypedSemanticNodeKind.ListConcat(leftListNodeId, rightListNodeId) ->
            writer.WriteString("kind", "listConcat")
            writer.WriteString("leftListNodeId", StableId.value leftListNodeId)
            writer.WriteString("rightListNodeId", StableId.value rightListNodeId)
        | TypedSemanticNodeKind.ListFold(sourceListNodeId, seedNodeId, folderFunctionId, context) ->
            writer.WriteString("kind", "listFold")
            writer.WriteString("sourceListNodeId", StableId.value sourceListNodeId)
            writer.WriteString("seedNodeId", StableId.value seedNodeId)
            writer.WriteString("folderFunctionId", StableId.value folderFunctionId)
            writeCallbackContext writer context
        | TypedSemanticNodeKind.ListEffectFold(sourceListNodeId, seedNodeId, folderFunctionId, context) ->
            writer.WriteString("kind", "listEffectFold")
            writer.WriteString("sourceListNodeId", StableId.value sourceListNodeId)
            writer.WriteString("seedNodeId", StableId.value seedNodeId)
            writer.WriteString("folderFunctionId", StableId.value folderFunctionId)
            writeCallbackContext writer context
        | TypedSemanticNodeKind.UseResource(acquireNodeId, releaseCapabilityNodeId, releaseOperationId, bodyFunctionId, context) ->
            writer.WriteString("kind", "useResource")
            writer.WriteString("acquireNodeId", StableId.value acquireNodeId)
            writer.WriteString("releaseCapabilityNodeId", StableId.value releaseCapabilityNodeId)
            writer.WriteString("releaseOperationId", StableId.value releaseOperationId)
            writer.WriteString("bodyFunctionId", StableId.value bodyFunctionId)
            writeCallbackContext writer context
        | TypedSemanticNodeKind.ParallelAll(sourceListNodeId, workerFunctionId, context) ->
            writer.WriteString("kind", "parallelAll")
            writer.WriteString("sourceListNodeId", StableId.value sourceListNodeId)
            writer.WriteString("workerFunctionId", StableId.value workerFunctionId)
            writeCallbackContext writer context
        | TypedSemanticNodeKind.CancellationRequested -> writer.WriteString("kind", "cancellationRequested")
        | TypedSemanticNodeKind.TaskScope(bodyFunctionId, context) ->
            writer.WriteString("kind", "taskScope")
            writer.WriteString("bodyFunctionId", StableId.value bodyFunctionId)
            writeCallbackContext writer context
        | TypedSemanticNodeKind.TaskStart(scopeNodeId, workerFunctionId, context, itemNodeId) ->
            writer.WriteString("kind", "taskStart")
            writer.WriteString("scopeNodeId", StableId.value scopeNodeId)
            writer.WriteString("workerFunctionId", StableId.value workerFunctionId)
            writer.WriteString("itemNodeId", StableId.value itemNodeId)
            writeCallbackContext writer context
        | TypedSemanticNodeKind.TaskWait(tasksNodeId, mode, limitNodeId) ->
            writer.WriteString("kind", "taskWait")
            writer.WriteString("tasksNodeId", StableId.value tasksNodeId)
            writer.WriteString("mode", (match mode with TaskWaitMode.Any -> "any" | TaskWaitMode.All -> "all"))
            limitNodeId |> Option.iter (fun limit -> writer.WriteString("limitNodeId", StableId.value limit))
        | TypedSemanticNodeKind.TaskCancel taskNodeId ->
            writer.WriteString("kind", "taskCancel")
            writer.WriteString("taskNodeId", StableId.value taskNodeId)
        | TypedSemanticNodeKind.ListMap(sourceListNodeId, mapperFunctionId, context) ->
            writeListCallbackNode writer "listMap" sourceListNodeId "mapperFunctionId" mapperFunctionId context
        | TypedSemanticNodeKind.ListFilter(sourceListNodeId, predicateFunctionId, context) ->
            writeListCallbackNode writer "listFilter" sourceListNodeId "predicateFunctionId" predicateFunctionId context
        | TypedSemanticNodeKind.ListSortBy(sourceListNodeId, lessThanFunctionId, context) ->
            writeListCallbackNode writer "listSortBy" sourceListNodeId "lessThanFunctionId" lessThanFunctionId context
        | TypedSemanticNodeKind.ListAny(sourceListNodeId, predicateFunctionId, context) ->
            writeListCallbackNode writer "listAny" sourceListNodeId "predicateFunctionId" predicateFunctionId context
        | TypedSemanticNodeKind.ListAll(sourceListNodeId, predicateFunctionId, context) ->
            writeListCallbackNode writer "listAll" sourceListNodeId "predicateFunctionId" predicateFunctionId context
        | TypedSemanticNodeKind.CapabilityConstruct(capabilityId, implementations) ->
            writer.WriteString("kind", "capabilityConstruct")
            writer.WriteString("capabilityId", StableId.value capabilityId)
            writer.WriteStartArray("implementations")
            for operationId, functionId in implementations |> List.sortBy (fst >> StableId.value) do
                writer.WriteStartObject()
                writer.WriteString("operationId", StableId.value operationId)
                writer.WriteString("functionId", StableId.value functionId)
                writer.WriteEndObject()
            writer.WriteEndArray()
        | TypedSemanticNodeKind.FunctionReference target ->
            writer.WriteString("kind", "functionReference")
            writer.WriteString("functionId", StableId.value target)
        | TypedSemanticNodeKind.FunctionInvoke(value, arguments) ->
            writer.WriteString("kind", "functionInvoke")
            writer.WriteString("functionValueNodeId", StableId.value value)
            writer.WritePropertyName("arguments")
            writer.WriteStartArray()
            arguments |> List.iter (fun id -> writer.WriteStringValue(StableId.value id))
            writer.WriteEndArray()
        | TypedSemanticNodeKind.DirectCall(functionId, arguments) ->
            writer.WriteString("kind", "directCall")
            writer.WriteString("functionId", StableId.value functionId)
            writer.WritePropertyName("arguments")
            writer.WriteStartArray()
            arguments
            |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId)
            |> List.iter (writeArgument writer)
            writer.WriteEndArray()
        | TypedSemanticNodeKind.CapabilityInvoke(capabilityNodeId, operationId, arguments) ->
            writer.WriteString("kind", "capabilityInvoke")
            writer.WriteString("capabilityValueNodeId", StableId.value capabilityNodeId)
            writer.WriteString("operationId", StableId.value operationId)
            writer.WritePropertyName("arguments")
            writer.WriteStartArray()
            arguments
            |> List.sortBy (fun argument -> StableId.value argument.TargetCapabilityParameterId)
            |> List.iter (writeCapabilityArgument writer)
            writer.WriteEndArray()
        | TypedSemanticNodeKind.ProductConstruct fields ->
            writer.WriteString("kind", "productConstruct")
            writer.WritePropertyName("fields")
            writer.WriteStartArray()
            fields
            |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
            |> List.iter (writeFieldValue writer)
            writer.WriteEndArray()
        | TypedSemanticNodeKind.FieldAccess(sourceNodeId, fieldId) ->
            writer.WriteString("kind", "fieldAccess")
            writer.WriteString("sourceNodeId", StableId.value sourceNodeId)
            writer.WriteString("fieldId", StableId.value fieldId)
        | TypedSemanticNodeKind.ProductUpdate(sourceNodeId, fields) ->
            writer.WriteString("kind", "productUpdate")
            writer.WriteString("sourceNodeId", StableId.value sourceNodeId)
            writer.WritePropertyName("fields")
            writer.WriteStartArray()
            fields
            |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
            |> List.iter (writeFieldValue writer)
            writer.WriteEndArray()
        | TypedSemanticNodeKind.UnionConstruct(variantId, fields) ->
            writer.WriteString("kind", "unionConstruct")
            writer.WriteString("variantId", StableId.value variantId)
            writer.WritePropertyName("fields")
            writer.WriteStartArray()
            fields
            |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
            |> List.iter (writeFieldValue writer)
            writer.WriteEndArray()
        | TypedSemanticNodeKind.OptionNone ->
            writer.WriteString("kind", "optionNone")
        | TypedSemanticNodeKind.OptionSome valueNodeId ->
            writeUnaryNode writer "optionSome" valueNodeId
        | TypedSemanticNodeKind.ResultOk valueNodeId ->
            writeUnaryNode writer "resultOk" valueNodeId
        | TypedSemanticNodeKind.Abort errorNodeId ->
            writer.WriteString("kind", "abort")
            writer.WriteString("errorNodeId", StableId.value errorNodeId)
        | TypedSemanticNodeKind.ResultErr errorNodeId ->
            writer.WriteString("kind", "resultErr")
            writer.WriteString("errorNodeId", StableId.value errorNodeId)
        | TypedSemanticNodeKind.BoolNot operandNodeId ->
            writer.WriteString("kind", "boolNot")
            writer.WriteString("operandNodeId", StableId.value operandNodeId)
        | TypedSemanticNodeKind.BoolAnd(leftNodeId, right) ->
            writer.WriteString("kind", "boolAnd")
            writer.WriteString("leftNodeId", StableId.value leftNodeId)
            writer.WritePropertyName("right")
            writeGraph writer right
        | TypedSemanticNodeKind.BoolOr(leftNodeId, right) ->
            writer.WriteString("kind", "boolOr")
            writer.WriteString("leftNodeId", StableId.value leftNodeId)
            writer.WritePropertyName("right")
            writeGraph writer right
        | TypedSemanticNodeKind.Conditional(conditionNodeId, whenTrue, whenFalse) ->
            writer.WriteString("kind", "conditional")
            writer.WriteString("conditionNodeId", StableId.value conditionNodeId)
            writer.WritePropertyName("whenTrue")
            writeGraph writer whenTrue
            writer.WritePropertyName("whenFalse")
            writeGraph writer whenFalse
        | TypedSemanticNodeKind.MatchUnion(inputNodeId, arms) ->
            writer.WriteString("kind", "matchUnion")
            writer.WriteString("inputNodeId", StableId.value inputNodeId)
            writer.WritePropertyName("arms")
            writer.WriteStartArray()
            arms
            |> List.sortBy (fun arm -> StableId.value arm.VariantId)
            |> List.iter (writeUnionArm writer)
            writer.WriteEndArray()
        | TypedSemanticNodeKind.Bind(inputNodeId, body) ->
            writer.WriteString("kind", "bind")
            writer.WriteString("inputNodeId", StableId.value inputNodeId)
            writer.WritePropertyName("boundBody")
            writeValueArm writer body
        | TypedSemanticNodeKind.MatchOption(inputNodeId, someArm, noneBody) ->
            writer.WriteString("kind", "matchOption")
            writer.WriteString("inputNodeId", StableId.value inputNodeId)
            writer.WritePropertyName("someArm")
            writeValueArm writer someArm
            writer.WritePropertyName("noneBody")
            writeGraph writer noneBody
        | TypedSemanticNodeKind.MatchResult(inputNodeId, okArm, errArm) ->
            writer.WriteString("kind", "matchResult")
            writer.WriteString("inputNodeId", StableId.value inputNodeId)
            writer.WritePropertyName("okArm")
            writeValueArm writer okArm
            writer.WritePropertyName("errArm")
            writeErrorArm writer errArm

        writer.WriteEndObject()

    let private writeFunction (writer: Utf8JsonWriter) (functionEntity: TypedSemanticFunction) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value functionEntity.Id)
        writer.WritePropertyName("parameters")
        writer.WriteStartArray()
        functionEntity.Parameters |> List.iter (writeParameter writer)
        writer.WriteEndArray()
        writer.WritePropertyName("resultType")
        writeTypeRef writer functionEntity.ResultType
        writer.WritePropertyName("requiredEffects")
        writer.WriteStartArray()
        functionEntity.RequiredEffects
        |> List.sortBy StableId.value
        |> List.iter (fun effectId -> writer.WriteStringValue(StableId.value effectId))
        writer.WriteEndArray()
        writer.WritePropertyName("body")
        writeGraph writer functionEntity.Body
        writer.WriteEndObject()

    let serialize (program: TypedSemanticProgram): string =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)

        writer.WriteStartObject()
        let rec has matches (graph: TypedSemanticGraph) =
            graph.Nodes |> List.exists (fun node ->
                matches node.Kind || TypedSemanticNodeKind.subgraphs node.Kind |> List.exists (has matches))
        let uses matches = program.Functions |> List.exists (fun fn -> has matches fn.Body)
        let version =
            if uses (function TypedSemanticNodeKind.TaskWait(_, _, Some _) -> true | _ -> false) then schemaVersion
            elif uses (function TypedSemanticNodeKind.Abort _ -> true | _ -> false) then 8
            else 7
        writer.WriteNumber("schemaVersion", version)
        writer.WriteString("sourceSnapshotId", StableId.value program.SourceSnapshotId)

        writer.WritePropertyName("types")
        writer.WriteStartArray()
        program.Types
        |> List.sortBy (fun typeEntity -> StableId.value typeEntity.Id)
        |> List.iter (writeType writer)
        writer.WriteEndArray()

        writer.WritePropertyName("effects")
        writer.WriteStartArray()
        program.Effects
        |> List.sortBy StableId.value
        |> List.iter (fun effectId -> writer.WriteStringValue(StableId.value effectId))
        writer.WriteEndArray()

        writer.WritePropertyName("capabilities")
        writer.WriteStartArray()
        program.Capabilities
        |> List.sortBy (fun capability -> StableId.value capability.Id)
        |> List.iter (writeCapability writer)
        writer.WriteEndArray()

        writer.WritePropertyName("functions")
        writer.WriteStartArray()
        program.Functions
        |> List.sortBy (fun functionEntity -> StableId.value functionEntity.Id)
        |> List.iter (writeFunction writer)
        writer.WriteEndArray()

        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())
