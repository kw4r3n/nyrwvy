namespace Nayamnyam.Core

open System.IO
open System.Text
open System.Text.Json

/// Explicit canonical serialization for the derived Typed Semantic IR.
/// This is an IR schema, not a semantic-graph persistence format.
[<RequireQualifiedAccess>]
module TypedSemanticIrCodec =
    let schemaVersion = 2

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
        | TypeRef.Named typeId ->
            writer.WriteString("kind", "named")
            writer.WriteString("typeId", StableId.value typeId)
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
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, arguments) ->
            invalidOp $"Typed Semantic IR contains Option with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, arguments) ->
            invalidOp $"Typed Semantic IR contains Result with {arguments.Length} type arguments."
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

    and private writeNode (writer: Utf8JsonWriter) (node: TypedSemanticNode) =
        writer.WriteStartObject()
        writer.WriteString("originNodeId", StableId.value node.OriginNodeId)
        writer.WritePropertyName("type")
        writeTypeRef writer node.Type

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
        | TypedSemanticNodeKind.StringIsEmpty valueNodeId ->
            writer.WriteString("kind", "stringIsEmpty")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | TypedSemanticNodeKind.StringContains(valueNodeId, substringNodeId) ->
            writer.WriteString("kind", "stringContains")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("substringNodeId", StableId.value substringNodeId)
        | TypedSemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64WrappingAdd" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64WrappingSubtract" leftNodeId rightNodeId
        | TypedSemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64WrappingMultiply" leftNodeId rightNodeId
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
            writer.WriteString("kind", "optionSome")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | TypedSemanticNodeKind.ResultOk valueNodeId ->
            writer.WriteString("kind", "resultOk")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | TypedSemanticNodeKind.ResultErr errorNodeId ->
            writer.WriteString("kind", "resultErr")
            writer.WriteString("errorNodeId", StableId.value errorNodeId)
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
        writer.WriteNumber("schemaVersion", schemaVersion)
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