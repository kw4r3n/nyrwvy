namespace Nyrwvy.Core

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json

[<RequireQualifiedAccess>]
type SnapshotCodecError =
    | InvalidJson of string
    | InvalidShape of string
    | UnsupportedSchemaVersion of int
    | InvalidSnapshot of ValidationIssue list

/// JSON remains a strict reference codec and benchmark baseline.
/// It is not a decision that JSON is Nyrwvy's long-term canonical encoding;
/// Nyason is evaluated separately from the semantic model.
[<RequireQualifiedAccess>]
module SnapshotCodec =
    let private schemaVersion = 9

    let private invalidShape message = raise (InvalidDataException message)

    let private ensureObject (context: string) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            invalidShape $"{context} must be a JSON object."

    let private ensureProperties
        (context: string)
        (required: Set<string>)
        (allowed: Set<string>)
        (element: JsonElement)
        =
        ensureObject context element

        let propertyNames =
            element.EnumerateObject()
            |> Seq.map (fun property -> property.Name)
            |> List.ofSeq

        let actual = Set.ofList propertyNames

        if propertyNames.Length <> actual.Count then
            invalidShape $"{context} contains duplicate property names."

        let missing = Set.difference required actual
        let unexpected = Set.difference actual allowed

        if not missing.IsEmpty then
            let missingText = String.Join(", ", missing)
            invalidShape $"{context} is missing required properties: {missingText}."

        if not unexpected.IsEmpty then
            let unexpectedText = String.Join(", ", unexpected)
            invalidShape $"{context} contains unsupported properties: {unexpectedText}."

    let private requireProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.TryGetProperty(name, &value) then value
        else invalidShape $"Missing required property '{name}'."

    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.TryGetProperty(name, &value) then Some value else None

    let private readString (name: string) (element: JsonElement) =
        let value = requireProperty name element
        if value.ValueKind <> JsonValueKind.String then
            invalidShape $"Property '{name}' must be a string."
        match value.GetString() with
        | null -> invalidShape $"Property '{name}' must not be null."
        | text -> text

    let private readBool (name: string) (element: JsonElement) =
        let value = requireProperty name element
        match value.ValueKind with
        | JsonValueKind.True -> true
        | JsonValueKind.False -> false
        | _ -> invalidShape $"Property '{name}' must be a boolean."

    let private readInt32 (name: string) (element: JsonElement) =
        let value = requireProperty name element
        let mutable number = 0
        if value.ValueKind <> JsonValueKind.Number || not (value.TryGetInt32(&number)) then
            invalidShape $"Property '{name}' must be a 32-bit integer."
        number

    let private readInt64 (name: string) (element: JsonElement) =
        let value = requireProperty name element
        let mutable number = 0L
        if value.ValueKind <> JsonValueKind.Number || not (value.TryGetInt64(&number)) then
            invalidShape $"Property '{name}' must be a signed 64-bit integer."
        number

    let private readFloat64Bits (name: string) (element: JsonElement) =
        match Float64Bits.tryParseHex (readString name element) with
        | Ok bits -> bits
        | Error error -> invalidShape $"Property '{name}' is not canonical binary64 bits: {error}"

    let private readId<'Kind> (name: string) (element: JsonElement): StableId<'Kind> =
        let text = readString name element
        match StableId.tryCreate<'Kind> text with
        | Ok id -> id
        | Error error -> invalidShape $"Property '{name}' contains an invalid stable ID: {error}."

    let private readArray name reader (root: JsonElement) =
        let value = requireProperty name root
        if value.ValueKind <> JsonValueKind.Array then
            invalidShape $"Property '{name}' must be an array."
        value.EnumerateArray() |> Seq.map reader |> List.ofSeq

    let private readIdArray<'Kind> name (root: JsonElement): StableId<'Kind> list =
        let value = requireProperty name root
        if value.ValueKind <> JsonValueKind.Array then
            invalidShape $"Property '{name}' must be an array."
        value.EnumerateArray()
        |> Seq.map (fun item ->
            if item.ValueKind <> JsonValueKind.String then
                invalidShape $"Property '{name}' must contain only stable-ID strings."
            match item.GetString() with
            | null -> invalidShape $"Property '{name}' contains null."
            | text ->
                match StableId.tryCreate<'Kind> text with
                | Ok id -> id
                | Error error -> invalidShape $"Property '{name}' contains an invalid stable ID: {error}.")
        |> List.ofSeq

    let rec private writeTypeRef (writer: Utf8JsonWriter) (typeRef: TypeRef) =
        writer.WriteStartObject()
        match typeRef with
        | TypeRef.Primitive primitive ->
            writer.WriteString("kind", "primitive")
            writer.WriteString(
                "primitive",
                match primitive with
                | PrimitiveType.String -> "string"
                | PrimitiveType.Bool -> "bool"
                | PrimitiveType.Unit -> "unit"
                | PrimitiveType.Int64 -> "int64"
                | PrimitiveType.Float64 -> "float64"
            )
        | TypeRef.Named typeId ->
            writer.WriteString("kind", "named")
            writer.WriteString("typeId", StableId.value typeId)
        | TypeRef.Capability capabilityId ->
            writer.WriteString("kind", "capability")
            writer.WriteString("capabilityId", StableId.value capabilityId)
        | TypeRef.BuiltinGeneric(genericType, arguments) ->
            writer.WriteString("kind", "builtinGeneric")
            writer.WriteString(
                "generic",
                match genericType with
                | BuiltinGenericType.Option -> "option"
                | BuiltinGenericType.Result -> "result"
                | BuiltinGenericType.List -> "list"
            )
            writer.WritePropertyName("arguments")
            writer.WriteStartArray()
            arguments |> List.iter (writeTypeRef writer)
            writer.WriteEndArray()
        writer.WriteEndObject()

    let private writeField (writer: Utf8JsonWriter) (field: SemanticField) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value field.Id)
        writer.WriteString("displayName", field.DisplayName)
        writer.WritePropertyName("type")
        writeTypeRef writer field.Type
        writer.WriteEndObject()

    let private writeVariant (writer: Utf8JsonWriter) (variant: SemanticVariant) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value variant.Id)
        writer.WriteString("displayName", variant.DisplayName)
        writer.WritePropertyName("fields")
        writer.WriteStartArray()
        variant.Fields
        |> List.sortBy (fun field -> StableId.value field.Id)
        |> List.iter (writeField writer)
        writer.WriteEndArray()
        writer.WriteEndObject()

    let private writeType (writer: Utf8JsonWriter) (item: SemanticType) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value item.Id)
        writer.WriteString("displayName", item.DisplayName)
        match item.Kind with
        | SemanticTypeKind.Product fields ->
            writer.WriteString("kind", "product")
            writer.WritePropertyName("fields")
            writer.WriteStartArray()
            fields
            |> List.sortBy (fun field -> StableId.value field.Id)
            |> List.iter (writeField writer)
            writer.WriteEndArray()
        | SemanticTypeKind.Union variants ->
            writer.WriteString("kind", "union")
            writer.WritePropertyName("variants")
            writer.WriteStartArray()
            variants
            |> List.sortBy (fun variant -> StableId.value variant.Id)
            |> List.iter (writeVariant writer)
            writer.WriteEndArray()
        writer.WriteEndObject()

    let private writeEffect (writer: Utf8JsonWriter) (effect: SemanticEffect) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value effect.Id)
        writer.WriteString("displayName", effect.DisplayName)
        writer.WriteEndObject()

    let private writeCapabilityParameter (writer: Utf8JsonWriter) (parameter: SemanticCapabilityParameter) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value parameter.Id)
        writer.WriteString("displayName", parameter.DisplayName)
        writer.WritePropertyName("type")
        writeTypeRef writer parameter.Type
        writer.WriteEndObject()

    let private writeCapabilityOperation (writer: Utf8JsonWriter) (operation: SemanticCapabilityOperation) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value operation.Id)
        writer.WriteString("displayName", operation.DisplayName)
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

    let private writeCapability (writer: Utf8JsonWriter) (capability: SemanticCapability) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value capability.Id)
        writer.WriteString("displayName", capability.DisplayName)
        writer.WritePropertyName("operations")
        writer.WriteStartArray()
        capability.Operations
        |> List.sortBy (fun operation -> StableId.value operation.Id)
        |> List.iter (writeCapabilityOperation writer)
        writer.WriteEndArray()
        writer.WriteEndObject()

    let private writeParameter (writer: Utf8JsonWriter) (parameter: SemanticParameter) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value parameter.Id)
        writer.WriteString("displayName", parameter.DisplayName)
        writer.WritePropertyName("type")
        writeTypeRef writer parameter.Type
        writer.WriteEndObject()

    let private writeArgument (writer: Utf8JsonWriter) (argument: ArgumentBinding) =
        writer.WriteStartObject()
        writer.WriteString("targetParameterId", StableId.value argument.TargetParameterId)
        writer.WriteString("valueNodeId", StableId.value argument.ValueNodeId)
        writer.WriteEndObject()

    let private writeCapabilityArgument (writer: Utf8JsonWriter) (argument: CapabilityArgumentBinding) =
        writer.WriteStartObject()
        writer.WriteString("targetCapabilityParameterId", StableId.value argument.TargetCapabilityParameterId)
        writer.WriteString("valueNodeId", StableId.value argument.ValueNodeId)
        writer.WriteEndObject()

    let private writeFieldValue (writer: Utf8JsonWriter) (binding: FieldValueBinding) =
        writer.WriteStartObject()
        writer.WriteString("targetFieldId", StableId.value binding.TargetFieldId)
        writer.WriteString("valueNodeId", StableId.value binding.ValueNodeId)
        writer.WriteEndObject()

    let private writePatternBinding (writer: Utf8JsonWriter) (binding: PatternBinding) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value binding.Id)
        match binding.DisplayName with
        | Some displayName -> writer.WriteString("displayName", displayName)
        | None -> ()
        writer.WritePropertyName("type")
        writeTypeRef writer binding.Type
        writer.WriteEndObject()

    let private writePatternFieldBinding (writer: Utf8JsonWriter) (binding: PatternFieldBinding) =
        writer.WriteStartObject()
        writer.WriteString("sourceFieldId", StableId.value binding.SourceFieldId)
        writer.WritePropertyName("binding")
        writePatternBinding writer binding.Binding
        writer.WriteEndObject()

    let private writeBinaryNode
        (writer: Utf8JsonWriter)
        (kind: string)
        (leftNodeId: GraphNodeId)
        (rightNodeId: GraphNodeId)
        =
        writer.WriteString("kind", kind)
        writer.WriteString("leftNodeId", StableId.value leftNodeId)
        writer.WriteString("rightNodeId", StableId.value rightNodeId)

    let private writeUnaryNode
        (writer: Utf8JsonWriter)
        (kind: string)
        (valueNodeId: GraphNodeId)
        =
        writer.WriteString("kind", kind)
        writer.WriteString("valueNodeId", StableId.value valueNodeId)

    let private writeListCallbackNode
        (writer: Utf8JsonWriter)
        (kind: string)
        (sourceListNodeId: GraphNodeId)
        (callbackFieldName: string)
        (callbackFunctionId: FunctionId)
        =
        writer.WriteString("kind", kind)
        writer.WriteString("sourceListNodeId", StableId.value sourceListNodeId)
        writer.WriteString(callbackFieldName, StableId.value callbackFunctionId)

    let rec private writeNode (writer: Utf8JsonWriter) (node: SemanticNode) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value node.Id)

        match node.Kind with
        | SemanticNodeKind.ParameterReference parameterId ->
            writer.WriteString("kind", "parameterReference")
            writer.WriteString("parameterId", StableId.value parameterId)
        | SemanticNodeKind.PatternReference bindingId ->
            writer.WriteString("kind", "patternReference")
            writer.WriteString("patternBindingId", StableId.value bindingId)
        | SemanticNodeKind.StringLiteral value ->
            writer.WriteString("kind", "stringLiteral")
            writer.WriteString("value", value)
        | SemanticNodeKind.BoolLiteral value ->
            writer.WriteString("kind", "boolLiteral")
            writer.WriteBoolean("value", value)
        | SemanticNodeKind.UnitLiteral ->
            writer.WriteString("kind", "unitLiteral")
        | SemanticNodeKind.Int64Literal value ->
            writer.WriteString("kind", "int64Literal")
            writer.WriteNumber("value", value)
        | SemanticNodeKind.Float64Literal bits ->
            writer.WriteString("kind", "float64Literal")
            writer.WriteString("bits", Float64Bits.toHex bits)
        | SemanticNodeKind.StringIsEmpty valueNodeId ->
            writer.WriteString("kind", "stringIsEmpty")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.StringContains(valueNodeId, substringNodeId) ->
            writer.WriteString("kind", "stringContains")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("substringNodeId", StableId.value substringNodeId)
        | SemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64WrappingAdd" leftNodeId rightNodeId
        | SemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64WrappingSubtract" leftNodeId rightNodeId
        | SemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64WrappingMultiply" leftNodeId rightNodeId
        | SemanticNodeKind.Int64Equal(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64Equal" leftNodeId rightNodeId
        | SemanticNodeKind.Int64LessThan(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64LessThan" leftNodeId rightNodeId
        | SemanticNodeKind.Int64LessThanOrEqual(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64LessThanOrEqual" leftNodeId rightNodeId
        | SemanticNodeKind.Int64GreaterThan(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64GreaterThan" leftNodeId rightNodeId
        | SemanticNodeKind.Int64GreaterThanOrEqual(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64GreaterThanOrEqual" leftNodeId rightNodeId
        | SemanticNodeKind.Float64Add(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "float64Add" leftNodeId rightNodeId
        | SemanticNodeKind.Float64Subtract(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "float64Subtract" leftNodeId rightNodeId
        | SemanticNodeKind.Float64Multiply(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "float64Multiply" leftNodeId rightNodeId
        | SemanticNodeKind.Float64Divide(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "float64Divide" leftNodeId rightNodeId
        | SemanticNodeKind.Float64Equal(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "float64Equal" leftNodeId rightNodeId
        | SemanticNodeKind.Float64LessThan(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "float64LessThan" leftNodeId rightNodeId
        | SemanticNodeKind.Float64IsNaN valueNodeId ->
            writeUnaryNode writer "float64IsNaN" valueNodeId
        | SemanticNodeKind.Float64IsFinite valueNodeId ->
            writeUnaryNode writer "float64IsFinite" valueNodeId
        | SemanticNodeKind.Float64IsInfinite valueNodeId ->
            writeUnaryNode writer "float64IsInfinite" valueNodeId
        | SemanticNodeKind.ListEmpty itemType ->
            writer.WriteString("kind", "listEmpty")
            writer.WritePropertyName("itemType")
            writeTypeRef writer itemType
        | SemanticNodeKind.ListConstruct(itemType, itemNodeIds) ->
            writer.WriteString("kind", "listConstruct")
            writer.WritePropertyName("itemType")
            writeTypeRef writer itemType
            writer.WritePropertyName("itemNodeIds")
            writer.WriteStartArray()
            // List item order is semantic. Do not sort.
            itemNodeIds |> List.iter (fun itemNodeId -> writer.WriteStringValue(StableId.value itemNodeId))
            writer.WriteEndArray()
        | SemanticNodeKind.ListLength listNodeId ->
            writer.WriteString("kind", "listLength")
            writer.WriteString("listNodeId", StableId.value listNodeId)
        | SemanticNodeKind.ListAppendItem(listNodeId, itemNodeId) ->
            writer.WriteString("kind", "listAppendItem")
            writer.WriteString("listNodeId", StableId.value listNodeId)
            writer.WriteString("itemNodeId", StableId.value itemNodeId)
        | SemanticNodeKind.ListConcat(leftListNodeId, rightListNodeId) ->
            writer.WriteString("kind", "listConcat")
            writer.WriteString("leftListNodeId", StableId.value leftListNodeId)
            writer.WriteString("rightListNodeId", StableId.value rightListNodeId)
        | SemanticNodeKind.ListMap(sourceListNodeId, mapperFunctionId) ->
            writeListCallbackNode writer "listMap" sourceListNodeId "mapperFunctionId" mapperFunctionId
        | SemanticNodeKind.ListFilter(sourceListNodeId, predicateFunctionId) ->
            writeListCallbackNode writer "listFilter" sourceListNodeId "predicateFunctionId" predicateFunctionId
        | SemanticNodeKind.ListAny(sourceListNodeId, predicateFunctionId) ->
            writeListCallbackNode writer "listAny" sourceListNodeId "predicateFunctionId" predicateFunctionId
        | SemanticNodeKind.ListAll(sourceListNodeId, predicateFunctionId) ->
            writeListCallbackNode writer "listAll" sourceListNodeId "predicateFunctionId" predicateFunctionId
        | SemanticNodeKind.DirectCall(targetFunctionId, arguments) ->
            writer.WriteString("kind", "directCall")
            writer.WriteString("targetFunctionId", StableId.value targetFunctionId)
            writer.WritePropertyName("arguments")
            writer.WriteStartArray()
            arguments
            |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId, StableId.value argument.ValueNodeId)
            |> List.iter (writeArgument writer)
            writer.WriteEndArray()
        | SemanticNodeKind.CapabilityInvoke(capabilityValueNodeId, operationId, arguments) ->
            writer.WriteString("kind", "capabilityInvoke")
            writer.WriteString("capabilityValueNodeId", StableId.value capabilityValueNodeId)
            writer.WriteString("operationId", StableId.value operationId)
            writer.WritePropertyName("arguments")
            writer.WriteStartArray()
            arguments
            |> List.sortBy (fun argument -> StableId.value argument.TargetCapabilityParameterId, StableId.value argument.ValueNodeId)
            |> List.iter (writeCapabilityArgument writer)
            writer.WriteEndArray()
        | SemanticNodeKind.ProductConstruct(typeId, fields) ->
            writer.WriteString("kind", "productConstruct")
            writer.WriteString("typeId", StableId.value typeId)
            writer.WritePropertyName("fields")
            writer.WriteStartArray()
            fields
            |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
            |> List.iter (writeFieldValue writer)
            writer.WriteEndArray()
        | SemanticNodeKind.FieldAccess(sourceNodeId, fieldId) ->
            writer.WriteString("kind", "fieldAccess")
            writer.WriteString("sourceNodeId", StableId.value sourceNodeId)
            writer.WriteString("fieldId", StableId.value fieldId)
        | SemanticNodeKind.UnionConstruct(variantId, fields) ->
            writer.WriteString("kind", "unionConstruct")
            writer.WriteString("variantId", StableId.value variantId)
            writer.WritePropertyName("fields")
            writer.WriteStartArray()
            fields
            |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
            |> List.iter (writeFieldValue writer)
            writer.WriteEndArray()
        | SemanticNodeKind.OptionNone itemType ->
            writer.WriteString("kind", "optionNone")
            writer.WritePropertyName("itemType")
            writeTypeRef writer itemType
        | SemanticNodeKind.OptionSome valueNodeId ->
            writer.WriteString("kind", "optionSome")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.ResultOk(errorType, valueNodeId) ->
            writer.WriteString("kind", "resultOk")
            writer.WritePropertyName("errorType")
            writeTypeRef writer errorType
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.ResultErr(okType, errorNodeId) ->
            writer.WriteString("kind", "resultErr")
            writer.WritePropertyName("okType")
            writeTypeRef writer okType
            writer.WriteString("errorNodeId", StableId.value errorNodeId)
        | SemanticNodeKind.Conditional(conditionNodeId, whenTrue, whenFalse) ->
            writer.WriteString("kind", "conditional")
            writer.WriteString("conditionNodeId", StableId.value conditionNodeId)
            writer.WritePropertyName("whenTrue")
            writeGraph writer whenTrue
            writer.WritePropertyName("whenFalse")
            writeGraph writer whenFalse
        | SemanticNodeKind.MatchUnion(inputNodeId, arms) ->
            writer.WriteString("kind", "matchUnion")
            writer.WriteString("inputNodeId", StableId.value inputNodeId)
            writer.WritePropertyName("arms")
            writer.WriteStartArray()
            arms
            |> List.sortBy (fun arm -> StableId.value arm.VariantId)
            |> List.iter (fun arm ->
                writer.WriteStartObject()
                writer.WriteString("variantId", StableId.value arm.VariantId)
                writer.WritePropertyName("fieldBindings")
                writer.WriteStartArray()
                arm.FieldBindings
                |> List.sortBy (fun binding -> StableId.value binding.SourceFieldId)
                |> List.iter (writePatternFieldBinding writer)
                writer.WriteEndArray()
                writer.WritePropertyName("body")
                writeGraph writer arm.Body
                writer.WriteEndObject())
            writer.WriteEndArray()
        | SemanticNodeKind.Bind(inputNodeId, body) ->
            writer.WriteString("kind", "bind")
            writer.WriteString("inputNodeId", StableId.value inputNodeId)
            writer.WritePropertyName("boundBody")
            writeValueArm writer body
        | SemanticNodeKind.MatchOption(inputNodeId, someArm, noneBody) ->
            writer.WriteString("kind", "matchOption")
            writer.WriteString("inputNodeId", StableId.value inputNodeId)
            writer.WritePropertyName("someArm")
            writeValueArm writer someArm
            writer.WritePropertyName("noneBody")
            writeGraph writer noneBody
        | SemanticNodeKind.MatchResult(inputNodeId, okArm, errArm) ->
            writer.WriteString("kind", "matchResult")
            writer.WriteString("inputNodeId", StableId.value inputNodeId)
            writer.WritePropertyName("okArm")
            writeValueArm writer okArm
            writer.WritePropertyName("errArm")
            writer.WriteStartObject()
            writer.WritePropertyName("errorBinding")
            writePatternBinding writer errArm.ErrorBinding
            writer.WritePropertyName("body")
            writeGraph writer errArm.Body
            writer.WriteEndObject()

        writer.WriteEndObject()

    and private writeValueArm (writer: Utf8JsonWriter) (arm: ValueMatchArm) =
        writer.WriteStartObject()
        writer.WritePropertyName("valueBinding")
        writePatternBinding writer arm.ValueBinding
        writer.WritePropertyName("body")
        writeGraph writer arm.Body
        writer.WriteEndObject()

    and private writeGraph (writer: Utf8JsonWriter) (graph: ValueGraph) =
        writer.WriteStartObject()
        writer.WritePropertyName("nodes")
        writer.WriteStartArray()
        graph.Nodes
        |> List.sortBy (fun node -> StableId.value node.Id)
        |> List.iter (writeNode writer)
        writer.WriteEndArray()
        writer.WriteString("resultNodeId", StableId.value graph.ResultNodeId)
        writer.WriteEndObject()

    let private writeFunction (writer: Utf8JsonWriter) (functionEntity: SemanticFunction) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value functionEntity.Id)
        writer.WriteString("displayName", functionEntity.DisplayName)
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

    let private writeObligation (writer: Utf8JsonWriter) (obligation: MissingArgumentObligation) =
        writer.WriteStartObject()
        writer.WriteString("id", StableId.value obligation.Id)
        writer.WriteString("kind", "missingArgument")
        writer.WriteString("originTransactionId", StableId.value obligation.OriginTransactionId)
        writer.WriteString("callNodeId", StableId.value obligation.CallNodeId)
        writer.WriteString("parameterId", StableId.value obligation.ParameterId)
        match obligation.Status with
        | ObligationStatus.Open -> writer.WriteString("status", "open")
        | ObligationStatus.Resolved resolution ->
            writer.WriteString("status", "resolved")
            writer.WriteString("resolutionTransactionId", StableId.value resolution.TransactionId)
            writer.WriteString("resolutionValueNodeId", StableId.value resolution.ValueNodeId)
        writer.WriteEndObject()

    let private writeSnapshot (snapshot: Snapshot) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", schemaVersion)
        writer.WriteString("snapshotId", StableId.value snapshot.Id)
        match snapshot.ParentId with
        | Some parentId -> writer.WriteString("parentSnapshotId", StableId.value parentId)
        | None -> ()
        writer.WritePropertyName("types")
        writer.WriteStartArray()
        snapshot.Types |> List.sortBy (fun item -> StableId.value item.Id) |> List.iter (writeType writer)
        writer.WriteEndArray()
        writer.WritePropertyName("effects")
        writer.WriteStartArray()
        snapshot.Effects |> List.sortBy (fun item -> StableId.value item.Id) |> List.iter (writeEffect writer)
        writer.WriteEndArray()
        writer.WritePropertyName("capabilities")
        writer.WriteStartArray()
        snapshot.Capabilities |> List.sortBy (fun item -> StableId.value item.Id) |> List.iter (writeCapability writer)
        writer.WriteEndArray()
        writer.WritePropertyName("functions")
        writer.WriteStartArray()
        snapshot.Functions |> List.sortBy (fun item -> StableId.value item.Id) |> List.iter (writeFunction writer)
        writer.WriteEndArray()
        writer.WritePropertyName("obligations")
        writer.WriteStartArray()
        snapshot.Obligations |> List.sortBy (fun item -> StableId.value item.Id) |> List.iter (writeObligation writer)
        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    let serialize (snapshot: Snapshot): Result<string, SnapshotCodecError> =
        match SnapshotValidator.validate snapshot with
        | [] -> Ok(writeSnapshot snapshot)
        | issues -> Error(SnapshotCodecError.InvalidSnapshot issues)

    let rec private readTypeRef (element: JsonElement): TypeRef =
        let kind = readString "kind" element
        match kind with
        | "primitive" ->
            ensureProperties "Primitive type reference" (set [ "kind"; "primitive" ]) (set [ "kind"; "primitive" ]) element
            let primitive =
                match readString "primitive" element with
                | "string" -> PrimitiveType.String
                | "bool" -> PrimitiveType.Bool
                | "unit" -> PrimitiveType.Unit
                | "int64" -> PrimitiveType.Int64
                | "float64" -> PrimitiveType.Float64
                | value -> invalidShape $"Unsupported primitive type '{value}'."
            TypeRef.Primitive primitive
        | "named" ->
            ensureProperties "Named type reference" (set [ "kind"; "typeId" ]) (set [ "kind"; "typeId" ]) element
            TypeRef.Named(readId<TypeTag> "typeId" element)
        | "capability" ->
            ensureProperties "Capability type reference" (set [ "kind"; "capabilityId" ]) (set [ "kind"; "capabilityId" ]) element
            TypeRef.Capability(readId<CapabilityTag> "capabilityId" element)
        | "builtinGeneric" ->
            ensureProperties "Builtin generic type reference" (set [ "kind"; "generic"; "arguments" ]) (set [ "kind"; "generic"; "arguments" ]) element
            let genericType =
                match readString "generic" element with
                | "option" -> BuiltinGenericType.Option
                | "result" -> BuiltinGenericType.Result
                | "list" -> BuiltinGenericType.List
                | value -> invalidShape $"Unsupported builtin generic type '{value}'."
            TypeRef.BuiltinGeneric(genericType, readArray "arguments" readTypeRef element)
        | value -> invalidShape $"Unsupported type reference kind '{value}'."

    let private readField (element: JsonElement): SemanticField =
        ensureProperties "Field" (set [ "id"; "displayName"; "type" ]) (set [ "id"; "displayName"; "type" ]) element
        { Id = readId<FieldTag> "id" element
          DisplayName = readString "displayName" element
          Type = readTypeRef (requireProperty "type" element) }

    let private readVariant (element: JsonElement): SemanticVariant =
        ensureProperties "Variant" (set [ "id"; "displayName"; "fields" ]) (set [ "id"; "displayName"; "fields" ]) element
        { Id = readId<VariantTag> "id" element
          DisplayName = readString "displayName" element
          Fields = readArray "fields" readField element }

    let private readType (element: JsonElement): SemanticType =
        let kind = readString "kind" element
        match kind with
        | "product" ->
            ensureProperties "Product type" (set [ "id"; "displayName"; "kind"; "fields" ]) (set [ "id"; "displayName"; "kind"; "fields" ]) element
            { Id = readId<TypeTag> "id" element
              DisplayName = readString "displayName" element
              Kind = SemanticTypeKind.Product(readArray "fields" readField element) }
        | "union" ->
            ensureProperties "Union type" (set [ "id"; "displayName"; "kind"; "variants" ]) (set [ "id"; "displayName"; "kind"; "variants" ]) element
            { Id = readId<TypeTag> "id" element
              DisplayName = readString "displayName" element
              Kind = SemanticTypeKind.Union(readArray "variants" readVariant element) }
        | value -> invalidShape $"Unsupported semantic type kind '{value}'."

    let private readEffect (element: JsonElement): SemanticEffect =
        ensureProperties "Effect" (set [ "id"; "displayName" ]) (set [ "id"; "displayName" ]) element
        { Id = readId<EffectTag> "id" element
          DisplayName = readString "displayName" element }

    let private readCapabilityParameter (element: JsonElement): SemanticCapabilityParameter =
        ensureProperties "Capability parameter" (set [ "id"; "displayName"; "type" ]) (set [ "id"; "displayName"; "type" ]) element
        { Id = readId<CapabilityParameterTag> "id" element
          DisplayName = readString "displayName" element
          Type = readTypeRef (requireProperty "type" element) }

    let private readCapabilityOperation (element: JsonElement): SemanticCapabilityOperation =
        ensureProperties
            "Capability operation"
            (set [ "id"; "displayName"; "parameters"; "resultType"; "effectId" ])
            (set [ "id"; "displayName"; "parameters"; "resultType"; "failureType"; "effectId" ])
            element
        let failureType = tryProperty "failureType" element |> Option.map readTypeRef
        { Id = readId<CapabilityOperationTag> "id" element
          DisplayName = readString "displayName" element
          Parameters = readArray "parameters" readCapabilityParameter element
          ResultType = readTypeRef (requireProperty "resultType" element)
          FailureType = failureType
          EffectId = readId<EffectTag> "effectId" element }

    let private readCapability (element: JsonElement): SemanticCapability =
        ensureProperties "Capability" (set [ "id"; "displayName"; "operations" ]) (set [ "id"; "displayName"; "operations" ]) element
        { Id = readId<CapabilityTag> "id" element
          DisplayName = readString "displayName" element
          Operations = readArray "operations" readCapabilityOperation element }

    let private readParameter (element: JsonElement): SemanticParameter =
        ensureProperties "Parameter" (set [ "id"; "displayName"; "type" ]) (set [ "id"; "displayName"; "type" ]) element
        { Id = readId<ParameterTag> "id" element
          DisplayName = readString "displayName" element
          Type = readTypeRef (requireProperty "type" element) }

    let private readArgument (element: JsonElement): ArgumentBinding =
        ensureProperties "Argument" (set [ "targetParameterId"; "valueNodeId" ]) (set [ "targetParameterId"; "valueNodeId" ]) element
        { TargetParameterId = readId<ParameterTag> "targetParameterId" element
          ValueNodeId = readId<GraphNodeTag> "valueNodeId" element }

    let private readCapabilityArgument (element: JsonElement): CapabilityArgumentBinding =
        ensureProperties
            "Capability argument"
            (set [ "targetCapabilityParameterId"; "valueNodeId" ])
            (set [ "targetCapabilityParameterId"; "valueNodeId" ])
            element
        { TargetCapabilityParameterId = readId<CapabilityParameterTag> "targetCapabilityParameterId" element
          ValueNodeId = readId<GraphNodeTag> "valueNodeId" element }

    let private readFieldValue (element: JsonElement): FieldValueBinding =
        ensureProperties "Field value" (set [ "targetFieldId"; "valueNodeId" ]) (set [ "targetFieldId"; "valueNodeId" ]) element
        { TargetFieldId = readId<FieldTag> "targetFieldId" element
          ValueNodeId = readId<GraphNodeTag> "valueNodeId" element }

    let private readPatternBinding (element: JsonElement): PatternBinding =
        ensureProperties "Pattern binding" (set [ "id"; "type" ]) (set [ "id"; "displayName"; "type" ]) element
        let displayName =
            match tryProperty "displayName" element with
            | Some _ -> Some(readString "displayName" element)
            | None -> None
        { Id = readId<PatternBindingTag> "id" element
          DisplayName = displayName
          Type = readTypeRef (requireProperty "type" element) }

    let private readPatternFieldBinding (element: JsonElement): PatternFieldBinding =
        ensureProperties "Pattern field binding" (set [ "sourceFieldId"; "binding" ]) (set [ "sourceFieldId"; "binding" ]) element
        { SourceFieldId = readId<FieldTag> "sourceFieldId" element
          Binding = readPatternBinding (requireProperty "binding" element) }

    let private readBinaryNode kind element constructor =
        ensureProperties
            kind
            (set [ "id"; "kind"; "leftNodeId"; "rightNodeId" ])
            (set [ "id"; "kind"; "leftNodeId"; "rightNodeId" ])
            element
        constructor
            (readId<GraphNodeTag> "leftNodeId" element)
            (readId<GraphNodeTag> "rightNodeId" element)

    let private readUnaryNode kind element constructor =
        ensureProperties
            kind
            (set [ "id"; "kind"; "valueNodeId" ])
            (set [ "id"; "kind"; "valueNodeId" ])
            element
        constructor (readId<GraphNodeTag> "valueNodeId" element)

    let private readListCallbackNode
        context
        callbackFieldName
        element
        constructor
        =
        ensureProperties
            context
            (set [ "id"; "kind"; "sourceListNodeId"; callbackFieldName ])
            (set [ "id"; "kind"; "sourceListNodeId"; callbackFieldName ])
            element
        constructor
            (readId<GraphNodeTag> "sourceListNodeId" element)
            (readId<FunctionTag> callbackFieldName element)

    let rec private readNode (element: JsonElement): SemanticNode =
        let id = readId<GraphNodeTag> "id" element
        let kind = readString "kind" element
        let nodeKind =
            match kind with
            | "parameterReference" ->
                ensureProperties "Parameter reference node" (set [ "id"; "kind"; "parameterId" ]) (set [ "id"; "kind"; "parameterId" ]) element
                SemanticNodeKind.ParameterReference(readId<ParameterTag> "parameterId" element)
            | "patternReference" ->
                ensureProperties "Pattern reference node" (set [ "id"; "kind"; "patternBindingId" ]) (set [ "id"; "kind"; "patternBindingId" ]) element
                SemanticNodeKind.PatternReference(readId<PatternBindingTag> "patternBindingId" element)
            | "stringLiteral" ->
                ensureProperties "String literal node" (set [ "id"; "kind"; "value" ]) (set [ "id"; "kind"; "value" ]) element
                SemanticNodeKind.StringLiteral(readString "value" element)
            | "boolLiteral" ->
                ensureProperties "Bool literal node" (set [ "id"; "kind"; "value" ]) (set [ "id"; "kind"; "value" ]) element
                SemanticNodeKind.BoolLiteral(readBool "value" element)
            | "unitLiteral" ->
                ensureProperties "Unit literal node" (set [ "id"; "kind" ]) (set [ "id"; "kind" ]) element
                SemanticNodeKind.UnitLiteral
            | "int64Literal" ->
                ensureProperties "Int64 literal node" (set [ "id"; "kind"; "value" ]) (set [ "id"; "kind"; "value" ]) element
                SemanticNodeKind.Int64Literal(readInt64 "value" element)
            | "float64Literal" ->
                ensureProperties "Float64 literal node" (set [ "id"; "kind"; "bits" ]) (set [ "id"; "kind"; "bits" ]) element
                SemanticNodeKind.Float64Literal(readFloat64Bits "bits" element)
            | "stringIsEmpty" ->
                ensureProperties "StringIsEmpty node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.StringIsEmpty(readId<GraphNodeTag> "valueNodeId" element)
            | "stringContains" ->
                ensureProperties
                    "StringContains node"
                    (set [ "id"; "kind"; "valueNodeId"; "substringNodeId" ])
                    (set [ "id"; "kind"; "valueNodeId"; "substringNodeId" ])
                    element
                SemanticNodeKind.StringContains(readId<GraphNodeTag> "valueNodeId" element, readId<GraphNodeTag> "substringNodeId" element)
            | "int64WrappingAdd" ->
                readBinaryNode "Int64WrappingAdd node" element (fun left right -> SemanticNodeKind.Int64WrappingAdd(left, right))
            | "int64WrappingSubtract" ->
                readBinaryNode "Int64WrappingSubtract node" element (fun left right -> SemanticNodeKind.Int64WrappingSubtract(left, right))
            | "int64WrappingMultiply" ->
                readBinaryNode "Int64WrappingMultiply node" element (fun left right -> SemanticNodeKind.Int64WrappingMultiply(left, right))
            | "int64Equal" ->
                readBinaryNode "Int64Equal node" element (fun left right -> SemanticNodeKind.Int64Equal(left, right))
            | "int64LessThan" ->
                readBinaryNode "Int64LessThan node" element (fun left right -> SemanticNodeKind.Int64LessThan(left, right))
            | "int64LessThanOrEqual" ->
                readBinaryNode "Int64LessThanOrEqual node" element (fun left right -> SemanticNodeKind.Int64LessThanOrEqual(left, right))
            | "int64GreaterThan" ->
                readBinaryNode "Int64GreaterThan node" element (fun left right -> SemanticNodeKind.Int64GreaterThan(left, right))
            | "int64GreaterThanOrEqual" ->
                readBinaryNode "Int64GreaterThanOrEqual node" element (fun left right -> SemanticNodeKind.Int64GreaterThanOrEqual(left, right))
            | "float64Add" ->
                readBinaryNode "Float64Add node" element (fun left right -> SemanticNodeKind.Float64Add(left, right))
            | "float64Subtract" ->
                readBinaryNode "Float64Subtract node" element (fun left right -> SemanticNodeKind.Float64Subtract(left, right))
            | "float64Multiply" ->
                readBinaryNode "Float64Multiply node" element (fun left right -> SemanticNodeKind.Float64Multiply(left, right))
            | "float64Divide" ->
                readBinaryNode "Float64Divide node" element (fun left right -> SemanticNodeKind.Float64Divide(left, right))
            | "float64Equal" ->
                readBinaryNode "Float64Equal node" element (fun left right -> SemanticNodeKind.Float64Equal(left, right))
            | "float64LessThan" ->
                readBinaryNode "Float64LessThan node" element (fun left right -> SemanticNodeKind.Float64LessThan(left, right))
            | "float64IsNaN" ->
                readUnaryNode "Float64IsNaN node" element SemanticNodeKind.Float64IsNaN
            | "float64IsFinite" ->
                readUnaryNode "Float64IsFinite node" element SemanticNodeKind.Float64IsFinite
            | "float64IsInfinite" ->
                readUnaryNode "Float64IsInfinite node" element SemanticNodeKind.Float64IsInfinite
            | "listEmpty" ->
                ensureProperties "ListEmpty node" (set [ "id"; "kind"; "itemType" ]) (set [ "id"; "kind"; "itemType" ]) element
                SemanticNodeKind.ListEmpty(readTypeRef (requireProperty "itemType" element))
            | "listConstruct" ->
                ensureProperties
                    "ListConstruct node"
                    (set [ "id"; "kind"; "itemType"; "itemNodeIds" ])
                    (set [ "id"; "kind"; "itemType"; "itemNodeIds" ])
                    element
                SemanticNodeKind.ListConstruct(
                    readTypeRef (requireProperty "itemType" element),
                    readIdArray<GraphNodeTag> "itemNodeIds" element
                )
            | "listLength" ->
                ensureProperties "ListLength node" (set [ "id"; "kind"; "listNodeId" ]) (set [ "id"; "kind"; "listNodeId" ]) element
                SemanticNodeKind.ListLength(readId<GraphNodeTag> "listNodeId" element)
            | "listAppendItem" ->
                ensureProperties
                    "ListAppendItem node"
                    (set [ "id"; "kind"; "listNodeId"; "itemNodeId" ])
                    (set [ "id"; "kind"; "listNodeId"; "itemNodeId" ])
                    element
                SemanticNodeKind.ListAppendItem(
                    readId<GraphNodeTag> "listNodeId" element,
                    readId<GraphNodeTag> "itemNodeId" element
                )
            | "listConcat" ->
                ensureProperties
                    "ListConcat node"
                    (set [ "id"; "kind"; "leftListNodeId"; "rightListNodeId" ])
                    (set [ "id"; "kind"; "leftListNodeId"; "rightListNodeId" ])
                    element
                SemanticNodeKind.ListConcat(
                    readId<GraphNodeTag> "leftListNodeId" element,
                    readId<GraphNodeTag> "rightListNodeId" element
                )
            | "listMap" ->
                readListCallbackNode "ListMap node" "mapperFunctionId" element (fun source callback ->
                    SemanticNodeKind.ListMap(source, callback))
            | "listFilter" ->
                readListCallbackNode "ListFilter node" "predicateFunctionId" element (fun source callback ->
                    SemanticNodeKind.ListFilter(source, callback))
            | "listAny" ->
                readListCallbackNode "ListAny node" "predicateFunctionId" element (fun source callback ->
                    SemanticNodeKind.ListAny(source, callback))
            | "listAll" ->
                readListCallbackNode "ListAll node" "predicateFunctionId" element (fun source callback ->
                    SemanticNodeKind.ListAll(source, callback))
            | "directCall" ->
                ensureProperties "Direct call node" (set [ "id"; "kind"; "targetFunctionId"; "arguments" ]) (set [ "id"; "kind"; "targetFunctionId"; "arguments" ]) element
                SemanticNodeKind.DirectCall(readId<FunctionTag> "targetFunctionId" element, readArray "arguments" readArgument element)
            | "capabilityInvoke" ->
                ensureProperties
                    "Capability invoke node"
                    (set [ "id"; "kind"; "capabilityValueNodeId"; "operationId"; "arguments" ])
                    (set [ "id"; "kind"; "capabilityValueNodeId"; "operationId"; "arguments" ])
                    element
                SemanticNodeKind.CapabilityInvoke(
                    readId<GraphNodeTag> "capabilityValueNodeId" element,
                    readId<CapabilityOperationTag> "operationId" element,
                    readArray "arguments" readCapabilityArgument element
                )
            | "productConstruct" ->
                ensureProperties "Product construct node" (set [ "id"; "kind"; "typeId"; "fields" ]) (set [ "id"; "kind"; "typeId"; "fields" ]) element
                SemanticNodeKind.ProductConstruct(readId<TypeTag> "typeId" element, readArray "fields" readFieldValue element)
            | "fieldAccess" ->
                ensureProperties "Field access node" (set [ "id"; "kind"; "sourceNodeId"; "fieldId" ]) (set [ "id"; "kind"; "sourceNodeId"; "fieldId" ]) element
                SemanticNodeKind.FieldAccess(readId<GraphNodeTag> "sourceNodeId" element, readId<FieldTag> "fieldId" element)
            | "unionConstruct" ->
                ensureProperties "Union construct node" (set [ "id"; "kind"; "variantId"; "fields" ]) (set [ "id"; "kind"; "variantId"; "fields" ]) element
                SemanticNodeKind.UnionConstruct(readId<VariantTag> "variantId" element, readArray "fields" readFieldValue element)
            | "optionNone" ->
                ensureProperties "Option.None node" (set [ "id"; "kind"; "itemType" ]) (set [ "id"; "kind"; "itemType" ]) element
                SemanticNodeKind.OptionNone(readTypeRef (requireProperty "itemType" element))
            | "optionSome" ->
                ensureProperties "Option.Some node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.OptionSome(readId<GraphNodeTag> "valueNodeId" element)
            | "resultOk" ->
                ensureProperties "Result.Ok node" (set [ "id"; "kind"; "errorType"; "valueNodeId" ]) (set [ "id"; "kind"; "errorType"; "valueNodeId" ]) element
                SemanticNodeKind.ResultOk(readTypeRef (requireProperty "errorType" element), readId<GraphNodeTag> "valueNodeId" element)
            | "resultErr" ->
                ensureProperties "Result.Err node" (set [ "id"; "kind"; "okType"; "errorNodeId" ]) (set [ "id"; "kind"; "okType"; "errorNodeId" ]) element
                SemanticNodeKind.ResultErr(readTypeRef (requireProperty "okType" element), readId<GraphNodeTag> "errorNodeId" element)
            | "conditional" ->
                ensureProperties "Conditional node" (set [ "id"; "kind"; "conditionNodeId"; "whenTrue"; "whenFalse" ]) (set [ "id"; "kind"; "conditionNodeId"; "whenTrue"; "whenFalse" ]) element
                SemanticNodeKind.Conditional(
                    readId<GraphNodeTag> "conditionNodeId" element,
                    readGraph (requireProperty "whenTrue" element),
                    readGraph (requireProperty "whenFalse" element)
                )
            | "matchUnion" ->
                ensureProperties "Union match node" (set [ "id"; "kind"; "inputNodeId"; "arms" ]) (set [ "id"; "kind"; "inputNodeId"; "arms" ]) element
                let readArm armElement =
                    ensureProperties "Union match arm" (set [ "variantId"; "fieldBindings"; "body" ]) (set [ "variantId"; "fieldBindings"; "body" ]) armElement
                    { VariantId = readId<VariantTag> "variantId" armElement
                      FieldBindings = readArray "fieldBindings" readPatternFieldBinding armElement
                      Body = readGraph (requireProperty "body" armElement) }
                SemanticNodeKind.MatchUnion(readId<GraphNodeTag> "inputNodeId" element, readArray "arms" readArm element)
            | "bind" ->
                ensureProperties "Bind node" (set [ "id"; "kind"; "inputNodeId"; "boundBody" ]) (set [ "id"; "kind"; "inputNodeId"; "boundBody" ]) element
                SemanticNodeKind.Bind(readId<GraphNodeTag> "inputNodeId" element, readValueArm (requireProperty "boundBody" element))
            | "matchOption" ->
                ensureProperties "Option match node" (set [ "id"; "kind"; "inputNodeId"; "someArm"; "noneBody" ]) (set [ "id"; "kind"; "inputNodeId"; "someArm"; "noneBody" ]) element
                let someArm = readValueArm (requireProperty "someArm" element)
                SemanticNodeKind.MatchOption(readId<GraphNodeTag> "inputNodeId" element, someArm, readGraph (requireProperty "noneBody" element))
            | "matchResult" ->
                ensureProperties "Result match node" (set [ "id"; "kind"; "inputNodeId"; "okArm"; "errArm" ]) (set [ "id"; "kind"; "inputNodeId"; "okArm"; "errArm" ]) element
                let errElement = requireProperty "errArm" element
                ensureProperties "Result.Err match arm" (set [ "errorBinding"; "body" ]) (set [ "errorBinding"; "body" ]) errElement
                let okArm = readValueArm (requireProperty "okArm" element)
                let errArm: ErrorMatchArm =
                    { ErrorBinding = readPatternBinding (requireProperty "errorBinding" errElement)
                      Body = readGraph (requireProperty "body" errElement) }
                SemanticNodeKind.MatchResult(readId<GraphNodeTag> "inputNodeId" element, okArm, errArm)
            | value -> invalidShape $"Unsupported graph node kind '{value}'."
        { Id = id; Kind = nodeKind }

    and private readValueArm (element: JsonElement): ValueMatchArm =
        ensureProperties "Value arm" (set [ "valueBinding"; "body" ]) (set [ "valueBinding"; "body" ]) element
        { ValueBinding = readPatternBinding (requireProperty "valueBinding" element)
          Body = readGraph (requireProperty "body" element) }

    and private readGraph (element: JsonElement): ValueGraph =
        ensureProperties "Value graph" (set [ "nodes"; "resultNodeId" ]) (set [ "nodes"; "resultNodeId" ]) element
        { Nodes = readArray "nodes" readNode element
          ResultNodeId = readId<GraphNodeTag> "resultNodeId" element }

    let private readFunction (element: JsonElement): SemanticFunction =
        ensureProperties
            "Function"
            (set [ "id"; "displayName"; "parameters"; "resultType"; "requiredEffects"; "body" ])
            (set [ "id"; "displayName"; "parameters"; "resultType"; "requiredEffects"; "body" ])
            element
        { Id = readId<FunctionTag> "id" element
          DisplayName = readString "displayName" element
          Parameters = readArray "parameters" readParameter element
          ResultType = readTypeRef (requireProperty "resultType" element)
          RequiredEffects = readIdArray<EffectTag> "requiredEffects" element
          Body = readGraph (requireProperty "body" element) }

    let private readObligation (element: JsonElement): MissingArgumentObligation =
        let status = readString "status" element
        let kind = readString "kind" element
        if kind <> "missingArgument" then
            invalidShape $"Unsupported obligation kind '{kind}'."
        match status with
        | "open" ->
            ensureProperties
                "Open missing-argument obligation"
                (set [ "id"; "kind"; "originTransactionId"; "callNodeId"; "parameterId"; "status" ])
                (set [ "id"; "kind"; "originTransactionId"; "callNodeId"; "parameterId"; "status" ])
                element
            { Id = readId<ObligationTag> "id" element
              OriginTransactionId = readId<TransactionTag> "originTransactionId" element
              CallNodeId = readId<GraphNodeTag> "callNodeId" element
              ParameterId = readId<ParameterTag> "parameterId" element
              Status = ObligationStatus.Open }
        | "resolved" ->
            ensureProperties
                "Resolved missing-argument obligation"
                (set [ "id"; "kind"; "originTransactionId"; "callNodeId"; "parameterId"; "status"; "resolutionTransactionId"; "resolutionValueNodeId" ])
                (set [ "id"; "kind"; "originTransactionId"; "callNodeId"; "parameterId"; "status"; "resolutionTransactionId"; "resolutionValueNodeId" ])
                element
            { Id = readId<ObligationTag> "id" element
              OriginTransactionId = readId<TransactionTag> "originTransactionId" element
              CallNodeId = readId<GraphNodeTag> "callNodeId" element
              ParameterId = readId<ParameterTag> "parameterId" element
              Status =
                ObligationStatus.Resolved
                    { TransactionId = readId<TransactionTag> "resolutionTransactionId" element
                      ValueNodeId = readId<GraphNodeTag> "resolutionValueNodeId" element } }
        | value -> invalidShape $"Unsupported obligation status '{value}'."

    let deserialize (json: string): Result<Snapshot, SnapshotCodecError> =
        if isNull json then
            Error(SnapshotCodecError.InvalidJson "Snapshot JSON must not be null.")
        else
            try
                use document = JsonDocument.Parse(json)
                let root = document.RootElement
                ensureProperties
                    "Snapshot"
                    (set [ "schemaVersion"; "snapshotId"; "types"; "effects"; "capabilities"; "functions"; "obligations" ])
                    (set [ "schemaVersion"; "snapshotId"; "parentSnapshotId"; "types"; "effects"; "capabilities"; "functions"; "obligations" ])
                    root
                let version = readInt32 "schemaVersion" root
                if version <> schemaVersion then
                    Error(SnapshotCodecError.UnsupportedSchemaVersion version)
                else
                    let parentId =
                        match tryProperty "parentSnapshotId" root with
                        | Some _ -> Some(readId<SnapshotTag> "parentSnapshotId" root)
                        | None -> None
                    let snapshot =
                        { Id = readId<SnapshotTag> "snapshotId" root
                          ParentId = parentId
                          Types = readArray "types" readType root
                          Effects = readArray "effects" readEffect root
                          Capabilities = readArray "capabilities" readCapability root
                          Functions = readArray "functions" readFunction root
                          Obligations = readArray "obligations" readObligation root }
                    match SnapshotValidator.validate snapshot with
                    | [] -> Ok snapshot
                    | issues -> Error(SnapshotCodecError.InvalidSnapshot issues)
            with
            | :? JsonException as error -> Error(SnapshotCodecError.InvalidJson error.Message)
            | :? InvalidDataException as error -> Error(SnapshotCodecError.InvalidShape error.Message)
            | :? ArgumentException as error -> Error(SnapshotCodecError.InvalidShape error.Message)
            | :? KeyNotFoundException as error -> Error(SnapshotCodecError.InvalidShape error.Message)
