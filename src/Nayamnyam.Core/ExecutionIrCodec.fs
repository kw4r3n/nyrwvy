namespace Nayamnyam.Core

open System.IO
open System.Text
open System.Text.Json

/// Canonical serialization for backend-neutral Execution IR.
/// Block/entity collection order is normalized; instruction order and function
/// parameter order are preserved because they are execution/signature semantics.
[<RequireQualifiedAccess>]
module ExecutionIrCodec =
    let schemaVersion = 1

    let rec private writeTypeRef (writer: Utf8JsonWriter) (typeRef: TypeRef) =
        writer.WriteStartObject()
        match typeRef with
        | TypeRef.Primitive PrimitiveType.String -> writer.WriteString("kind", "primitiveString")
        | TypeRef.Primitive PrimitiveType.Bool -> writer.WriteString("kind", "primitiveBool")
        | TypeRef.Primitive PrimitiveType.Unit -> writer.WriteString("kind", "primitiveUnit")
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
            invalidOp $"Execution IR contains Option with {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, arguments) ->
            invalidOp $"Execution IR contains Result with {arguments.Length} type arguments."
        writer.WriteEndObject()

    let private writeValueId (writer: Utf8JsonWriter) name valueId =
        writer.WriteString(name, ExecutionValueId.value valueId)

    let private writeBlockId (writer: Utf8JsonWriter) name blockId =
        writer.WriteString(name, ExecutionBlockId.value blockId)

    let private writeOrigin (writer: Utf8JsonWriter) (origin: ExecutionOrigin) =
        writer.WriteStartObject()
        match origin with
        | ExecutionOrigin.FunctionParameter parameterId ->
            writer.WriteString("kind", "functionParameter")
            writer.WriteString("parameterId", StableId.value parameterId)
        | ExecutionOrigin.SemanticNode nodeId ->
            writer.WriteString("kind", "semanticNode")
            writer.WriteString("nodeId", StableId.value nodeId)
        | ExecutionOrigin.PatternBinding(matchNodeId, bindingId) ->
            writer.WriteString("kind", "patternBinding")
            writer.WriteString("matchNodeId", StableId.value matchNodeId)
            writer.WriteString("bindingId", StableId.value bindingId)
        writer.WriteEndObject()

    let private writeValue (writer: Utf8JsonWriter) (value: ExecutionValue) =
        writer.WriteStartObject()
        writeValueId writer "id" value.Id
        writer.WritePropertyName("type")
        writeTypeRef writer value.Type
        writer.WritePropertyName("origin")
        writeOrigin writer value.Origin
        writer.WriteEndObject()

    let private writeField (writer: Utf8JsonWriter) (field: ExecutionField) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value field.Id)
        writer.WritePropertyName("type")
        writeTypeRef writer field.Type
        writer.WriteEndObject()

    let private writeVariant (writer: Utf8JsonWriter) (variant: ExecutionVariant) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value variant.Id)
        writer.WritePropertyName("fields")
        writer.WriteStartArray()
        variant.Fields
        |> List.sortBy (fun field -> StableId.value field.Id)
        |> List.iter (writeField writer)
        writer.WriteEndArray()
        writer.WriteEndObject()

    let private writeType (writer: Utf8JsonWriter) (typeEntity: ExecutionType) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value typeEntity.Id)
        match typeEntity.Kind with
        | ExecutionTypeKind.Product fields ->
            writer.WriteString("kind", "product")
            writer.WritePropertyName("fields")
            writer.WriteStartArray()
            fields
            |> List.sortBy (fun field -> StableId.value field.Id)
            |> List.iter (writeField writer)
            writer.WriteEndArray()
        | ExecutionTypeKind.Union variants ->
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
        (parameter: ExecutionCapabilityParameter) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value parameter.Id)
        writer.WritePropertyName("type")
        writeTypeRef writer parameter.Type
        writer.WriteEndObject()

    let private writeCapabilityOperation
        (writer: Utf8JsonWriter)
        (operation: ExecutionCapabilityOperation) =
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

    let private writeCapability (writer: Utf8JsonWriter) (capability: ExecutionCapability) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value capability.Id)
        writer.WritePropertyName("operations")
        writer.WriteStartArray()
        capability.Operations
        |> List.sortBy (fun operation -> StableId.value operation.Id)
        |> List.iter (writeCapabilityOperation writer)
        writer.WriteEndArray()
        writer.WriteEndObject()

    let private writeCallArgument (writer: Utf8JsonWriter) (argument: ExecutionCallArgument) =
        writer.WriteStartObject()
        writer.WriteString("targetParameterId", StableId.value argument.TargetParameterId)
        writeValueId writer "valueId" argument.ValueId
        writer.WriteEndObject()

    let private writeCapabilityArgument
        (writer: Utf8JsonWriter)
        (argument: ExecutionCapabilityArgument) =
        writer.WriteStartObject()
        writer.WriteString("targetCapabilityParameterId", StableId.value argument.TargetCapabilityParameterId)
        writeValueId writer "valueId" argument.ValueId
        writer.WriteEndObject()

    let private writeFieldValue (writer: Utf8JsonWriter) (field: ExecutionFieldValue) =
        writer.WriteStartObject()
        writer.WriteString("targetFieldId", StableId.value field.TargetFieldId)
        writeValueId writer "valueId" field.ValueId
        writer.WriteEndObject()

    let private writeInstructionKind (writer: Utf8JsonWriter) (kind: ExecutionInstructionKind) =
        match kind with
        | ExecutionInstructionKind.ReadParameter sourceValueId ->
            writer.WriteString("kind", "readParameter")
            writeValueId writer "sourceValueId" sourceValueId
        | ExecutionInstructionKind.ReadPatternBinding sourceValueId ->
            writer.WriteString("kind", "readPatternBinding")
            writeValueId writer "sourceValueId" sourceValueId
        | ExecutionInstructionKind.StringLiteral value ->
            writer.WriteString("kind", "stringLiteral")
            writer.WriteString("value", value)
        | ExecutionInstructionKind.BoolLiteral value ->
            writer.WriteString("kind", "boolLiteral")
            writer.WriteBoolean("value", value)
        | ExecutionInstructionKind.UnitLiteral -> writer.WriteString("kind", "unitLiteral")
        | ExecutionInstructionKind.StringIsEmpty valueId ->
            writer.WriteString("kind", "stringIsEmpty")
            writeValueId writer "valueId" valueId
        | ExecutionInstructionKind.StringContains(valueId, substringId) ->
            writer.WriteString("kind", "stringContains")
            writeValueId writer "valueId" valueId
            writeValueId writer "substringId" substringId
        | ExecutionInstructionKind.DirectCall(functionId, arguments) ->
            writer.WriteString("kind", "directCall")
            writer.WriteString("functionId", StableId.value functionId)
            writer.WritePropertyName("arguments")
            writer.WriteStartArray()
            arguments
            |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId)
            |> List.iter (writeCallArgument writer)
            writer.WriteEndArray()
        | ExecutionInstructionKind.CapabilityInvoke(capabilityValueId, operationId, arguments) ->
            writer.WriteString("kind", "capabilityInvoke")
            writeValueId writer "capabilityValueId" capabilityValueId
            writer.WriteString("operationId", StableId.value operationId)
            writer.WritePropertyName("arguments")
            writer.WriteStartArray()
            arguments
            |> List.sortBy (fun argument -> StableId.value argument.TargetCapabilityParameterId)
            |> List.iter (writeCapabilityArgument writer)
            writer.WriteEndArray()
        | ExecutionInstructionKind.ProductConstruct fields ->
            writer.WriteString("kind", "productConstruct")
            writer.WritePropertyName("fields")
            writer.WriteStartArray()
            fields
            |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
            |> List.iter (writeFieldValue writer)
            writer.WriteEndArray()
        | ExecutionInstructionKind.FieldAccess(sourceValueId, fieldId) ->
            writer.WriteString("kind", "fieldAccess")
            writeValueId writer "sourceValueId" sourceValueId
            writer.WriteString("fieldId", StableId.value fieldId)
        | ExecutionInstructionKind.UnionConstruct(variantId, fields) ->
            writer.WriteString("kind", "unionConstruct")
            writer.WriteString("variantId", StableId.value variantId)
            writer.WritePropertyName("fields")
            writer.WriteStartArray()
            fields
            |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
            |> List.iter (writeFieldValue writer)
            writer.WriteEndArray()
        | ExecutionInstructionKind.OptionNone -> writer.WriteString("kind", "optionNone")
        | ExecutionInstructionKind.OptionSome valueId ->
            writer.WriteString("kind", "optionSome")
            writeValueId writer "valueId" valueId
        | ExecutionInstructionKind.ResultOk valueId ->
            writer.WriteString("kind", "resultOk")
            writeValueId writer "valueId" valueId
        | ExecutionInstructionKind.ResultErr errorValueId ->
            writer.WriteString("kind", "resultErr")
            writeValueId writer "errorValueId" errorValueId
        | ExecutionInstructionKind.ExtractUnionField(inputValueId, variantId, fieldId) ->
            writer.WriteString("kind", "extractUnionField")
            writeValueId writer "inputValueId" inputValueId
            writer.WriteString("variantId", StableId.value variantId)
            writer.WriteString("fieldId", StableId.value fieldId)
        | ExecutionInstructionKind.ExtractOptionSome inputValueId ->
            writer.WriteString("kind", "extractOptionSome")
            writeValueId writer "inputValueId" inputValueId
        | ExecutionInstructionKind.ExtractResultOk inputValueId ->
            writer.WriteString("kind", "extractResultOk")
            writeValueId writer "inputValueId" inputValueId
        | ExecutionInstructionKind.ExtractResultErr inputValueId ->
            writer.WriteString("kind", "extractResultErr")
            writeValueId writer "inputValueId" inputValueId

    let private writeInstruction (writer: Utf8JsonWriter) (instruction: ExecutionInstruction) =
        writer.WriteStartObject()
        writer.WritePropertyName("result")
        writeValue writer instruction.Result
        writer.WriteString("originNodeId", StableId.value instruction.OriginNodeId)
        writeInstructionKind writer instruction.Kind
        writer.WriteEndObject()

    let private writeJumpArgument (writer: Utf8JsonWriter) (argument: ExecutionJumpArgument) =
        writer.WriteStartObject()
        writeValueId writer "targetValueId" argument.TargetValueId
        writeValueId writer "valueId" argument.ValueId
        writer.WriteEndObject()

    let private writeTerminator (writer: Utf8JsonWriter) (terminator: ExecutionTerminator) =
        writer.WriteStartObject()
        match terminator with
        | ExecutionTerminator.Return valueId ->
            writer.WriteString("kind", "return")
            writeValueId writer "valueId" valueId
        | ExecutionTerminator.Jump(blockId, arguments) ->
            writer.WriteString("kind", "jump")
            writeBlockId writer "blockId" blockId
            writer.WritePropertyName("arguments")
            writer.WriteStartArray()
            arguments
            |> List.sortBy (fun argument -> ExecutionValueId.value argument.TargetValueId)
            |> List.iter (writeJumpArgument writer)
            writer.WriteEndArray()
        | ExecutionTerminator.Branch(conditionValueId, whenTrue, whenFalse) ->
            writer.WriteString("kind", "branch")
            writeValueId writer "conditionValueId" conditionValueId
            writeBlockId writer "whenTrue" whenTrue
            writeBlockId writer "whenFalse" whenFalse
        | ExecutionTerminator.MatchUnion(inputValueId, targets) ->
            writer.WriteString("kind", "matchUnion")
            writeValueId writer "inputValueId" inputValueId
            writer.WritePropertyName("targets")
            writer.WriteStartArray()
            targets
            |> List.sortBy (fun target -> StableId.value target.VariantId)
            |> List.iter (fun target ->
                writer.WriteStartObject()
                writer.WriteString("variantId", StableId.value target.VariantId)
                writeBlockId writer "blockId" target.BlockId
                writer.WriteEndObject())
            writer.WriteEndArray()
        | ExecutionTerminator.MatchOption(inputValueId, someBlock, noneBlock) ->
            writer.WriteString("kind", "matchOption")
            writeValueId writer "inputValueId" inputValueId
            writeBlockId writer "someBlock" someBlock
            writeBlockId writer "noneBlock" noneBlock
        | ExecutionTerminator.MatchResult(inputValueId, okBlock, errBlock) ->
            writer.WriteString("kind", "matchResult")
            writeValueId writer "inputValueId" inputValueId
            writeBlockId writer "okBlock" okBlock
            writeBlockId writer "errBlock" errBlock
        writer.WriteEndObject()

    let private writeBlock (writer: Utf8JsonWriter) (block: ExecutionBlock) =
        writer.WriteStartObject()
        writeBlockId writer "id" block.Id
        writer.WritePropertyName("parameters")
        writer.WriteStartArray()
        block.Parameters |> List.iter (writeValue writer)
        writer.WriteEndArray()
        writer.WritePropertyName("instructions")
        writer.WriteStartArray()
        // Instruction order is semantic. Do not sort.
        block.Instructions |> List.iter (writeInstruction writer)
        writer.WriteEndArray()
        writer.WritePropertyName("terminator")
        writeTerminator writer block.Terminator
        writer.WriteEndObject()

    let private writeParameter (writer: Utf8JsonWriter) (parameter: ExecutionParameter) =
        writer.WriteStartObject()
        writer.WriteString("semanticParameterId", StableId.value parameter.SemanticParameterId)
        writer.WritePropertyName("value")
        writeValue writer parameter.Value
        writer.WriteEndObject()

    let private writeFunction (writer: Utf8JsonWriter) (functionEntity: ExecutionFunction) =
        writer.WriteStartObject()
        writer.WriteString("semanticFunctionId", StableId.value functionEntity.SemanticFunctionId)
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
        writeBlockId writer "entryBlockId" functionEntity.EntryBlockId
        writer.WritePropertyName("blocks")
        writer.WriteStartArray()
        functionEntity.Blocks
        |> List.sortBy (fun block -> ExecutionBlockId.value block.Id)
        |> List.iter (writeBlock writer)
        writer.WriteEndArray()
        writer.WriteEndObject()

    let serialize (program: ExecutionProgram): string =
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
        |> List.sortBy (fun functionEntity -> StableId.value functionEntity.SemanticFunctionId)
        |> List.iter (writeFunction writer)
        writer.WriteEndArray()

        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())
