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
    /// Encoding would nest deeper than the codec's JSON depth allowance (the payload).
    | MaximumDepthExceeded of int

/// JSON remains a strict reference codec and benchmark baseline.
/// It is not a decision that JSON is Nyrwvy's long-term canonical encoding;
/// Nyason is evaluated separately from the semantic model.
[<RequireQualifiedAccess>]
module SnapshotCodec =
    let private schemaVersion = 9
    let private capabilitySchemaVersion = 10
    let private abortSchemaVersion = 11
    let private timedWaitSchemaVersion = 12
    // Retain the writer's existing depth allowance and apply it to reads too:
    // nested graph envelopes must not make a successfully saved snapshot unreadable.
    /// Public so that session responses can size their own allowance from it.
    let maxJsonDepth = 1000

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
            |> Seq.map (fun property ->
                try property.Name
                with :? InvalidOperationException as error ->
                    invalidShape $"Invalid JSON property name encoding: {error.Message}")
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
        ensureObject $"Container for property '{name}'" element
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.TryGetProperty(name, &value) then value
        else invalidShape $"Missing required property '{name}'."

    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.TryGetProperty(name, &value) then Some value else None

    let private decodeString (element: JsonElement) =
        try element.GetString()
        with :? InvalidOperationException as error ->
            invalidShape $"Invalid JSON string encoding: {error.Message}"

    let private readString (name: string) (element: JsonElement) =
        let value = requireProperty name element
        if value.ValueKind <> JsonValueKind.String then
            invalidShape $"Property '{name}' must be a string."
        match decodeString value with
        | null -> invalidShape $"Property '{name}' must not be null."
        | text -> text

    /// A String literal may hold a lone surrogate, which the writer escapes explicitly and
    /// `GetString` rejects, so such an escape is decoded from the token's raw text.
    let private readLiteralString (name: string) (element: JsonElement) =
        let value = requireProperty name element
        if value.ValueKind <> JsonValueKind.String then
            invalidShape $"Property '{name}' must be a string."
        try value.GetString()
        with :? InvalidOperationException -> LooseJsonString.decodeRaw (value.GetRawText())

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
            match decodeString item with
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
                | PrimitiveType.Bits32 -> "bits32"
            )
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
        | TypeRef.BuiltinGeneric(genericType, arguments) ->
            writer.WriteString("kind", "builtinGeneric")
            writer.WriteString(
                "generic",
                match genericType with
                | BuiltinGenericType.Option -> "option"
                | BuiltinGenericType.Result -> "result"
                | BuiltinGenericType.List -> "list"
                | BuiltinGenericType.Task -> "task"
                | BuiltinGenericType.TaskScope -> "taskScope"
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

    /// Callback context is written only when present, so context-free graphs keep their bytes.
    let private writeCallbackContext (writer: Utf8JsonWriter) (context: ArgumentBinding list) =
        if not (List.isEmpty context) then
            writer.WritePropertyName("context")
            writer.WriteStartArray()
            context
            |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId, StableId.value argument.ValueNodeId)
            |> List.iter (writeArgument writer)
            writer.WriteEndArray()

    let private writeListCallbackNode
        (writer: Utf8JsonWriter)
        (kind: string)
        (sourceListNodeId: GraphNodeId)
        (callbackFieldName: string)
        (callbackFunctionId: FunctionId)
        (context: ArgumentBinding list)
        =
        writer.WriteString("kind", kind)
        writer.WriteString("sourceListNodeId", StableId.value sourceListNodeId)
        writer.WriteString(callbackFieldName, StableId.value callbackFunctionId)
        writeCallbackContext writer context

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
            // The writer would replace a lone surrogate with U+FFFD; it is escaped explicitly instead.
            if LooseJsonString.hasLoneSurrogate value then
                writer.WritePropertyName("value")
                let encoder = if isNull writer.Options.Encoder then Encodings.Web.JavaScriptEncoder.Default else writer.Options.Encoder
                writer.WriteRawValue(LooseJsonString.encode encoder value, true)
            else
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
        | SemanticNodeKind.Int64ToString valueNodeId ->
            writer.WriteString("kind", "int64ToString")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.Int64ToHexString valueNodeId ->
            writer.WriteString("kind", "int64ToHexString")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.StringParseInt64 valueNodeId ->
            writer.WriteString("kind", "stringParseInt64")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.StringParseInt64Hex valueNodeId ->
            writer.WriteString("kind", "stringParseInt64Hex")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.Float64ToString valueNodeId ->
            writer.WriteString("kind", "float64ToString")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.StringParseFloat64 valueNodeId ->
            writer.WriteString("kind", "stringParseFloat64")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.StringCodePoints valueNodeId ->
            writer.WriteString("kind", "stringCodePoints")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.StringToLower valueNodeId ->
            writer.WriteString("kind", "stringToLower")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.StringDecodeUtf8Lossy valueNodeId ->
            writer.WriteString("kind", "stringDecodeUtf8Lossy")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.Float64RoundToInt64 valueNodeId ->
            writer.WriteString("kind", "float64RoundToInt64")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.Float64FloorToInt64 valueNodeId ->
            writer.WriteString("kind", "float64FloorToInt64")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.Float64Sqrt valueNodeId ->
            writer.WriteString("kind", "float64Sqrt")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.Float64Abs valueNodeId ->
            writer.WriteString("kind", "float64Abs")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.Float64Negate valueNodeId ->
            writer.WriteString("kind", "float64Negate")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.Float64Floor valueNodeId ->
            writer.WriteString("kind", "float64Floor")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.Float64Ceiling valueNodeId ->
            writer.WriteString("kind", "float64Ceiling")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.Float64Truncate valueNodeId ->
            writer.WriteString("kind", "float64Truncate")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.StringUtf8ByteCount valueNodeId ->
            writer.WriteString("kind", "stringUtf8ByteCount")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.StringFromCodePoint valueNodeId ->
            writer.WriteString("kind", "stringFromCodePoint")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.StringToCodePoint valueNodeId ->
            writer.WriteString("kind", "stringToCodePoint")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.StringLength valueNodeId ->
            writer.WriteString("kind", "stringLength")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
        | SemanticNodeKind.StringSlice(valueNodeId, startNodeId, endNodeId) ->
            writer.WriteString("kind", "stringSlice")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("startNodeId", StableId.value startNodeId)
            writer.WriteString("endNodeId", StableId.value endNodeId)
        | SemanticNodeKind.StringContains(valueNodeId, substringNodeId) ->
            writer.WriteString("kind", "stringContains")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("substringNodeId", StableId.value substringNodeId)
        | SemanticNodeKind.StringIndexOf(valueNodeId, substringNodeId, fromNodeId) ->
            writer.WriteString("kind", "stringIndexOf")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("substringNodeId", StableId.value substringNodeId)
            writer.WriteString("fromNodeId", StableId.value fromNodeId)
        | SemanticNodeKind.StringSpan(valueNodeId, setNodeId, fromNodeId) ->
            writer.WriteString("kind", "stringSpan")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("setNodeId", StableId.value setNodeId)
            writer.WriteString("fromNodeId", StableId.value fromNodeId)
        | SemanticNodeKind.StringSpanBack(valueNodeId, setNodeId, endNodeId) ->
            writer.WriteString("kind", "stringSpanBack")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("setNodeId", StableId.value setNodeId)
            writer.WriteString("endNodeId", StableId.value endNodeId)
        | SemanticNodeKind.StringLastIndexOf(valueNodeId, substringNodeId) ->
            writer.WriteString("kind", "stringLastIndexOf")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("substringNodeId", StableId.value substringNodeId)
        | SemanticNodeKind.StringStartsWith(valueNodeId, prefixNodeId) ->
            writer.WriteString("kind", "stringStartsWith")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("prefixNodeId", StableId.value prefixNodeId)
        | SemanticNodeKind.StringEndsWith(valueNodeId, suffixNodeId) ->
            writer.WriteString("kind", "stringEndsWith")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("suffixNodeId", StableId.value suffixNodeId)
        | SemanticNodeKind.StringSplit(valueNodeId, separatorNodeId) ->
            writer.WriteString("kind", "stringSplit")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("separatorNodeId", StableId.value separatorNodeId)
        | SemanticNodeKind.StringSplitLines(valueNodeId, breaksNodeId) ->
            writer.WriteString("kind", "stringSplitLines")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("breaksNodeId", StableId.value breaksNodeId)
        | SemanticNodeKind.StringJoin(listNodeId, separatorNodeId) ->
            writer.WriteString("kind", "stringJoin")
            writer.WriteString("listNodeId", StableId.value listNodeId)
            writer.WriteString("separatorNodeId", StableId.value separatorNodeId)
        | SemanticNodeKind.StringEqual(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "stringEqual" leftNodeId rightNodeId
        | SemanticNodeKind.StringLessThan(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "stringLessThan" leftNodeId rightNodeId
        | SemanticNodeKind.ValueEqual(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "valueEqual" leftNodeId rightNodeId
        | SemanticNodeKind.StringConcat(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "stringConcat" leftNodeId rightNodeId
        | SemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64WrappingAdd" leftNodeId rightNodeId
        | SemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64WrappingSubtract" leftNodeId rightNodeId
        | SemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64WrappingMultiply" leftNodeId rightNodeId
        | SemanticNodeKind.Int64FloorDivide(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64FloorDivide" leftNodeId rightNodeId
        | SemanticNodeKind.Int64FloorModulo(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64FloorModulo" leftNodeId rightNodeId
        | SemanticNodeKind.Int64Min(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64Min" leftNodeId rightNodeId
        | SemanticNodeKind.Int64Max(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64Max" leftNodeId rightNodeId
        | SemanticNodeKind.Int64CheckedAdd(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64CheckedAdd" leftNodeId rightNodeId
        | SemanticNodeKind.Int64CheckedSubtract(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64CheckedSubtract" leftNodeId rightNodeId
        | SemanticNodeKind.Int64CheckedMultiply(leftNodeId, rightNodeId) ->
            writeBinaryNode writer "int64CheckedMultiply" leftNodeId rightNodeId
        | SemanticNodeKind.ListRange(startNodeId, endNodeId) ->
            writer.WriteString("kind", "listRange")
            writer.WriteString("startNodeId", StableId.value startNodeId)
            writer.WriteString("endNodeId", StableId.value endNodeId)
        | SemanticNodeKind.StringRepeat(textNodeId, countNodeId) ->
            writer.WriteString("kind", "stringRepeat")
            writer.WriteString("textNodeId", StableId.value textNodeId)
            writer.WriteString("countNodeId", StableId.value countNodeId)
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
        | SemanticNodeKind.Int64ToFloat64 valueNodeId ->
            writeUnaryNode writer "int64ToFloat64" valueNodeId
        | SemanticNodeKind.Int64WrappingAbs valueNodeId ->
            writeUnaryNode writer "int64WrappingAbs" valueNodeId
        | SemanticNodeKind.Bits32FromInt64 valueNodeId -> writeUnaryNode writer "bits32FromInt64" valueNodeId
        | SemanticNodeKind.Bits32ToInt64 valueNodeId -> writeUnaryNode writer "bits32ToInt64" valueNodeId
        | SemanticNodeKind.Bits32Not valueNodeId -> writeUnaryNode writer "bits32Not" valueNodeId
        | SemanticNodeKind.Bits32Xor(leftNodeId, rightNodeId) -> writeBinaryNode writer "bits32Xor" leftNodeId rightNodeId
        | SemanticNodeKind.Bits32And(leftNodeId, rightNodeId) -> writeBinaryNode writer "bits32And" leftNodeId rightNodeId
        | SemanticNodeKind.Bits32Or(leftNodeId, rightNodeId) -> writeBinaryNode writer "bits32Or" leftNodeId rightNodeId
        | SemanticNodeKind.Bits32Add(leftNodeId, rightNodeId) -> writeBinaryNode writer "bits32Add" leftNodeId rightNodeId
        | SemanticNodeKind.Bits32RotateRight(valueNodeId, amountNodeId) ->
            writer.WriteString("kind", "bits32RotateRight")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("amountNodeId", StableId.value amountNodeId)
        | SemanticNodeKind.Bits32ShiftRight(valueNodeId, amountNodeId) ->
            writer.WriteString("kind", "bits32ShiftRight")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("amountNodeId", StableId.value amountNodeId)
        | SemanticNodeKind.Bits32ShiftLeft(valueNodeId, amountNodeId) ->
            writer.WriteString("kind", "bits32ShiftLeft")
            writer.WriteString("valueNodeId", StableId.value valueNodeId)
            writer.WriteString("amountNodeId", StableId.value amountNodeId)
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
        | SemanticNodeKind.ListReverse listNodeId ->
            writer.WriteString("kind", "listReverse")
            writer.WriteString("listNodeId", StableId.value listNodeId)
        | SemanticNodeKind.ListItemAt(listNodeId, indexNodeId) ->
            writer.WriteString("kind", "listItemAt")
            writer.WriteString("listNodeId", StableId.value listNodeId)
            writer.WriteString("indexNodeId", StableId.value indexNodeId)
        | SemanticNodeKind.ListSlice(listNodeId, startNodeId, endNodeId) ->
            writer.WriteString("kind", "listSlice")
            writer.WriteString("listNodeId", StableId.value listNodeId)
            writer.WriteString("startNodeId", StableId.value startNodeId)
            writer.WriteString("endNodeId", StableId.value endNodeId)
        | SemanticNodeKind.ListAppendItem(listNodeId, itemNodeId) ->
            writer.WriteString("kind", "listAppendItem")
            writer.WriteString("listNodeId", StableId.value listNodeId)
            writer.WriteString("itemNodeId", StableId.value itemNodeId)
        | SemanticNodeKind.ListConcat(leftListNodeId, rightListNodeId) ->
            writer.WriteString("kind", "listConcat")
            writer.WriteString("leftListNodeId", StableId.value leftListNodeId)
            writer.WriteString("rightListNodeId", StableId.value rightListNodeId)
        | SemanticNodeKind.ListFold(sourceListNodeId, seedNodeId, folderFunctionId, context) ->
            writer.WriteString("kind", "listFold")
            writer.WriteString("sourceListNodeId", StableId.value sourceListNodeId)
            writer.WriteString("seedNodeId", StableId.value seedNodeId)
            writer.WriteString("folderFunctionId", StableId.value folderFunctionId)
            writeCallbackContext writer context
        | SemanticNodeKind.ListEffectFold(sourceListNodeId, seedNodeId, folderFunctionId, context) ->
            writer.WriteString("kind", "listEffectFold")
            writer.WriteString("sourceListNodeId", StableId.value sourceListNodeId)
            writer.WriteString("seedNodeId", StableId.value seedNodeId)
            writer.WriteString("folderFunctionId", StableId.value folderFunctionId)
            writeCallbackContext writer context
        | SemanticNodeKind.UseResource(acquireNodeId, releaseCapabilityNodeId, releaseOperationId, bodyFunctionId, context) ->
            writer.WriteString("kind", "useResource")
            writer.WriteString("acquireNodeId", StableId.value acquireNodeId)
            writer.WriteString("releaseCapabilityNodeId", StableId.value releaseCapabilityNodeId)
            writer.WriteString("releaseOperationId", StableId.value releaseOperationId)
            writer.WriteString("bodyFunctionId", StableId.value bodyFunctionId)
            writeCallbackContext writer context
        | SemanticNodeKind.ParallelAll(sourceListNodeId, workerFunctionId, context) ->
            writer.WriteString("kind", "parallelAll")
            writer.WriteString("sourceListNodeId", StableId.value sourceListNodeId)
            writer.WriteString("workerFunctionId", StableId.value workerFunctionId)
            writeCallbackContext writer context
        | SemanticNodeKind.CancellationRequested ->
            writer.WriteString("kind", "cancellationRequested")
        | SemanticNodeKind.TaskScope(bodyFunctionId, context) ->
            writer.WriteString("kind", "taskScope")
            writer.WriteString("bodyFunctionId", StableId.value bodyFunctionId)
            writeCallbackContext writer context
        | SemanticNodeKind.TaskStart(scopeNodeId, workerFunctionId, context, itemNodeId) ->
            writer.WriteString("kind", "taskStart")
            writer.WriteString("scopeNodeId", StableId.value scopeNodeId)
            writer.WriteString("workerFunctionId", StableId.value workerFunctionId)
            writer.WriteString("itemNodeId", StableId.value itemNodeId)
            writeCallbackContext writer context
        | SemanticNodeKind.TaskWait(tasksNodeId, mode, limitNodeId) ->
            writer.WriteString("kind", "taskWait")
            writer.WriteString("tasksNodeId", StableId.value tasksNodeId)
            writer.WriteString(
                "mode",
                match mode with
                | TaskWaitMode.Any -> "any"
                | TaskWaitMode.All -> "all"
            )
            limitNodeId |> Option.iter (fun limit -> writer.WriteString("limitNodeId", StableId.value limit))
        | SemanticNodeKind.TaskCancel taskNodeId ->
            writer.WriteString("kind", "taskCancel")
            writer.WriteString("taskNodeId", StableId.value taskNodeId)
        | SemanticNodeKind.ListMap(sourceListNodeId, mapperFunctionId, context) ->
            writeListCallbackNode writer "listMap" sourceListNodeId "mapperFunctionId" mapperFunctionId context
        | SemanticNodeKind.ListFilter(sourceListNodeId, predicateFunctionId, context) ->
            writeListCallbackNode writer "listFilter" sourceListNodeId "predicateFunctionId" predicateFunctionId context
        | SemanticNodeKind.ListSortBy(sourceListNodeId, lessThanFunctionId, context) ->
            writeListCallbackNode writer "listSortBy" sourceListNodeId "lessThanFunctionId" lessThanFunctionId context
        | SemanticNodeKind.ListAny(sourceListNodeId, predicateFunctionId, context) ->
            writeListCallbackNode writer "listAny" sourceListNodeId "predicateFunctionId" predicateFunctionId context
        | SemanticNodeKind.ListAll(sourceListNodeId, predicateFunctionId, context) ->
            writeListCallbackNode writer "listAll" sourceListNodeId "predicateFunctionId" predicateFunctionId context
        | SemanticNodeKind.CapabilityConstruct(capabilityId, implementations) ->
            writer.WriteString("kind", "capabilityConstruct")
            writer.WriteString("capabilityId", StableId.value capabilityId)
            writer.WriteStartArray("implementations")
            for operationId, functionId in implementations |> List.sortBy (fst >> StableId.value) do
                writer.WriteStartObject()
                writer.WriteString("operationId", StableId.value operationId)
                writer.WriteString("functionId", StableId.value functionId)
                writer.WriteEndObject()
            writer.WriteEndArray()
        | SemanticNodeKind.FunctionReference target ->
            writer.WriteString("kind", "functionReference")
            writer.WriteString("targetFunctionId", StableId.value target)
        | SemanticNodeKind.FunctionInvoke(value, arguments) ->
            writer.WriteString("kind", "functionInvoke")
            writer.WriteString("functionValueNodeId", StableId.value value)
            writer.WritePropertyName("arguments")
            writer.WriteStartArray()
            arguments |> List.iter (fun id -> writer.WriteStringValue(StableId.value id))
            writer.WriteEndArray()
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
        | SemanticNodeKind.ProductUpdate(typeId, sourceNodeId, fields) ->
            writer.WriteString("kind", "productUpdate")
            writer.WriteString("typeId", StableId.value typeId)
            writer.WriteString("sourceNodeId", StableId.value sourceNodeId)
            writer.WritePropertyName("fields")
            writer.WriteStartArray()
            fields
            |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
            |> List.iter (writeFieldValue writer)
            writer.WriteEndArray()
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
        | SemanticNodeKind.Abort errorNodeId ->
            writer.WriteString("kind", "abort")
            writer.WriteString("errorNodeId", StableId.value errorNodeId)
        | SemanticNodeKind.ResultErr(okType, errorNodeId) ->
            writer.WriteString("kind", "resultErr")
            writer.WritePropertyName("okType")
            writeTypeRef writer okType
            writer.WriteString("errorNodeId", StableId.value errorNodeId)
        | SemanticNodeKind.BoolNot operandNodeId ->
            writer.WriteString("kind", "boolNot")
            writer.WriteString("operandNodeId", StableId.value operandNodeId)
        | SemanticNodeKind.BoolAnd(leftNodeId, right) ->
            writer.WriteString("kind", "boolAnd")
            writer.WriteString("leftNodeId", StableId.value leftNodeId)
            writer.WritePropertyName("right")
            writeGraph writer right
        | SemanticNodeKind.BoolOr(leftNodeId, right) ->
            writer.WriteString("kind", "boolOr")
            writer.WriteString("leftNodeId", StableId.value leftNodeId)
            writer.WritePropertyName("right")
            writeGraph writer right
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
            |> List.sortBy (fun arm -> StableId.value (UnionMatchArm.key arm))
            |> List.iter (fun arm ->
                writer.WriteStartObject()
                // A one-variant arm keeps the original single-ID form, so existing graphs and their
                // fingerprints are unchanged; only an arm with several variants writes the list.
                match UnionMatchArm.canonicalVariantIds arm with
                | [ variantId ] -> writer.WriteString("variantId", StableId.value variantId)
                | variantIds ->
                    writer.WritePropertyName("variantIds")
                    writer.WriteStartArray()
                    variantIds |> List.iter (fun variantId -> writer.WriteStringValue(StableId.value variantId))
                    writer.WriteEndArray()
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

    let private writeSnapshotBody (writer: Utf8JsonWriter) (dependencyMarker: (string * string) option) (snapshot: Snapshot) =
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", if TaskTimeLimits.isUsed snapshot then timedWaitSchemaVersion elif FunctionAbort.isUsed snapshot then abortSchemaVersion elif CapabilityImplementations.isUsed snapshot then capabilitySchemaVersion else schemaVersion)
        writer.WriteString("snapshotId", StableId.value snapshot.Id)
        match dependencyMarker with
        | Some(id, fingerprint) ->
            writer.WriteStartObject("dependency")
            writer.WriteString("snapshotId", id)
            writer.WriteString("digest", fingerprint)
            writer.WriteEndObject()
        | None -> ()
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

    /// A graph nested beyond `maxJsonDepth` is reported as `MaximumDepthExceeded`, never thrown,
    /// so that it cannot be saved and then fail to load.
    let private writeSnapshot (dependencyMarker: (string * string) option) (snapshot: Snapshot): Result<string, SnapshotCodecError> =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(MaxDepth = maxJsonDepth))
        try
            writeSnapshotBody writer dependencyMarker snapshot
            writer.Flush()
            Ok(Encoding.UTF8.GetString(stream.ToArray()))
        with :? InvalidOperationException when writer.CurrentDepth >= maxJsonDepth ->
            Error(SnapshotCodecError.MaximumDepthExceeded maxJsonDepth)

    /// Whether a function body nests graphs deeper than any encoding of `maxJsonDepth` can hold:
    /// every graph level costs at least three containers (graph, node list, node). Checked before
    /// validation, whose recursion over nested graphs would otherwise overflow the stack first.
    /// The probe stops at the limit, so it cannot overflow itself.
    let internal nestsBeyondMaxDepth (snapshot: Snapshot) =
        let rec beyond remaining (graph: ValueGraph) =
            remaining = 0
            || graph.Nodes |> List.exists (fun node -> SemanticNodeShape.nestedGraphs node |> List.exists (beyond (remaining - 1)))
        snapshot.Functions |> List.exists (fun fn -> beyond (maxJsonDepth / 3) fn.Body)

    let private serializeCached: Snapshot -> Result<string, SnapshotCodecError> =
        SnapshotComputationScope.Memoize("serialization", fun snapshot ->
            if nestsBeyondMaxDepth snapshot then Error(SnapshotCodecError.MaximumDepthExceeded maxJsonDepth)
            else
                match SnapshotValidator.validate snapshot with
                | [] -> writeSnapshot None snapshot
                | issues -> Error(SnapshotCodecError.InvalidSnapshot issues))

    let serialize (snapshot: Snapshot): Result<string, SnapshotCodecError> = serializeCached snapshot

    /// The imported declarations retain their original IDs; duplicate IDs are
    /// rejected by the ordinary snapshot validator before any local publication.
    let resolveDependency (dependency: Snapshot) (local: Snapshot): Snapshot =
        { local with
            Types = dependency.Types @ local.Types
            Effects = dependency.Effects @ local.Effects
            Capabilities = dependency.Capabilities @ local.Capabilities
            Functions = dependency.Functions @ local.Functions
            Obligations = dependency.Obligations @ local.Obligations }

    let private dependencyDigest (dependency: Snapshot): Result<string, SnapshotCodecError> =
        writeSnapshot None dependency
        |> Result.map (fun encoded ->
            encoded
            |> Encoding.UTF8.GetBytes
            |> System.Security.Cryptography.SHA256.HashData
            |> Convert.ToHexString
            |> fun value -> value.ToLowerInvariant())

    /// Encode local declarations only after validating their resolved graph with the
    /// selected dependency. The dependency is never written into the local snapshot.
    let serializeResolved (dependency: Snapshot) (local: Snapshot): Result<string, SnapshotCodecError> =
        let resolved = resolveDependency dependency local
        if nestsBeyondMaxDepth resolved then Error(SnapshotCodecError.MaximumDepthExceeded maxJsonDepth)
        else
            match SnapshotValidator.validate resolved with
            | issue :: rest -> Error(SnapshotCodecError.InvalidSnapshot(issue :: rest))
            | [] ->
                dependencyDigest dependency
                |> Result.bind (fun digest -> writeSnapshot (Some(StableId.value dependency.Id, digest)) local)

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
                | "bits32" -> PrimitiveType.Bits32
                | value -> invalidShape $"Unsupported primitive type '{value}'."
            TypeRef.Primitive primitive
        | "named" ->
            ensureProperties "Named type reference" (set [ "kind"; "typeId" ]) (set [ "kind"; "typeId" ]) element
            TypeRef.Named(readId<TypeTag> "typeId" element)
        | "function" ->
            ensureProperties "Function type" (set [ "kind"; "parameters"; "resultType" ]) (set [ "kind"; "parameters"; "resultType" ]) element
            TypeRef.Function(readArray "parameters" readTypeRef element, readTypeRef (requireProperty "resultType" element))
        | "typeParameter" ->
            ensureProperties "Type parameter reference" (set [ "kind"; "typeParameterId"; "displayName" ]) (set [ "kind"; "typeParameterId"; "displayName" ]) element
            TypeRef.TypeParameter(readId<TypeParameterTag> "typeParameterId" element, readString "displayName" element)
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
                | "task" -> BuiltinGenericType.Task
                | "taskScope" -> BuiltinGenericType.TaskScope
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

    let private readCallbackContext (element: JsonElement) =
        match tryProperty "context" element with
        | None -> []
        | Some _ ->
            match readArray "context" readArgument element with
            | [] -> invalidShape "Callback context must be omitted rather than empty."
            | context -> context

    let private readListCallbackNode
        context
        callbackFieldName
        element
        constructor
        =
        ensureProperties
            context
            (set [ "id"; "kind"; "sourceListNodeId"; callbackFieldName ])
            (set [ "id"; "kind"; "sourceListNodeId"; callbackFieldName; "context" ])
            element
        constructor
            (readId<GraphNodeTag> "sourceListNodeId" element)
            (readId<FunctionTag> callbackFieldName element)
            (readCallbackContext element)

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
                SemanticNodeKind.StringLiteral(readLiteralString "value" element)
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
            | "int64ToString" ->
                ensureProperties "Int64ToString node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.Int64ToString(readId<GraphNodeTag> "valueNodeId" element)
            | "int64ToHexString" ->
                ensureProperties "Int64ToHexString node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.Int64ToHexString(readId<GraphNodeTag> "valueNodeId" element)
            | "stringParseInt64" ->
                ensureProperties "StringParseInt64 node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.StringParseInt64(readId<GraphNodeTag> "valueNodeId" element)
            | "stringParseInt64Hex" ->
                ensureProperties "StringParseInt64Hex node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.StringParseInt64Hex(readId<GraphNodeTag> "valueNodeId" element)
            | "float64ToString" ->
                ensureProperties "Float64ToString node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.Float64ToString(readId<GraphNodeTag> "valueNodeId" element)
            | "stringParseFloat64" ->
                ensureProperties "StringParseFloat64 node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.StringParseFloat64(readId<GraphNodeTag> "valueNodeId" element)
            | "stringCodePoints" ->
                ensureProperties "StringCodePoints node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.StringCodePoints(readId<GraphNodeTag> "valueNodeId" element)
            | "stringToLower" ->
                ensureProperties "StringToLower node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.StringToLower(readId<GraphNodeTag> "valueNodeId" element)
            | "stringDecodeUtf8Lossy" ->
                ensureProperties "StringDecodeUtf8Lossy node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.StringDecodeUtf8Lossy(readId<GraphNodeTag> "valueNodeId" element)
            | "float64RoundToInt64" ->
                ensureProperties "Float64RoundToInt64 node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.Float64RoundToInt64(readId<GraphNodeTag> "valueNodeId" element)
            | "float64FloorToInt64" ->
                ensureProperties "Float64FloorToInt64 node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.Float64FloorToInt64(readId<GraphNodeTag> "valueNodeId" element)
            | "float64Sqrt" ->
                ensureProperties "Float64Sqrt node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.Float64Sqrt(readId<GraphNodeTag> "valueNodeId" element)
            | "float64Abs" ->
                ensureProperties "Float64Abs node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.Float64Abs(readId<GraphNodeTag> "valueNodeId" element)
            | "float64Negate" ->
                ensureProperties "Float64Negate node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.Float64Negate(readId<GraphNodeTag> "valueNodeId" element)
            | "float64Floor" ->
                ensureProperties "Float64Floor node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.Float64Floor(readId<GraphNodeTag> "valueNodeId" element)
            | "float64Ceiling" ->
                ensureProperties "Float64Ceiling node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.Float64Ceiling(readId<GraphNodeTag> "valueNodeId" element)
            | "float64Truncate" ->
                ensureProperties "Float64Truncate node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.Float64Truncate(readId<GraphNodeTag> "valueNodeId" element)
            | "stringUtf8ByteCount" ->
                ensureProperties "StringUtf8ByteCount node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.StringUtf8ByteCount(readId<GraphNodeTag> "valueNodeId" element)
            | "stringFromCodePoint" ->
                ensureProperties "StringFromCodePoint node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.StringFromCodePoint(readId<GraphNodeTag> "valueNodeId" element)
            | "stringToCodePoint" ->
                ensureProperties "StringToCodePoint node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.StringToCodePoint(readId<GraphNodeTag> "valueNodeId" element)
            | "stringLength" ->
                ensureProperties "StringLength node" (set [ "id"; "kind"; "valueNodeId" ]) (set [ "id"; "kind"; "valueNodeId" ]) element
                SemanticNodeKind.StringLength(readId<GraphNodeTag> "valueNodeId" element)
            | "stringSlice" ->
                ensureProperties
                    "StringSlice node"
                    (set [ "id"; "kind"; "valueNodeId"; "startNodeId"; "endNodeId" ])
                    (set [ "id"; "kind"; "valueNodeId"; "startNodeId"; "endNodeId" ])
                    element
                SemanticNodeKind.StringSlice(
                    readId<GraphNodeTag> "valueNodeId" element,
                    readId<GraphNodeTag> "startNodeId" element,
                    readId<GraphNodeTag> "endNodeId" element
                )
            | "stringContains" ->
                ensureProperties
                    "StringContains node"
                    (set [ "id"; "kind"; "valueNodeId"; "substringNodeId" ])
                    (set [ "id"; "kind"; "valueNodeId"; "substringNodeId" ])
                    element
                SemanticNodeKind.StringContains(readId<GraphNodeTag> "valueNodeId" element, readId<GraphNodeTag> "substringNodeId" element)
            | "stringIndexOf" ->
                ensureProperties
                    "StringIndexOf node"
                    (set [ "id"; "kind"; "valueNodeId"; "substringNodeId"; "fromNodeId" ])
                    (set [ "id"; "kind"; "valueNodeId"; "substringNodeId"; "fromNodeId" ])
                    element
                SemanticNodeKind.StringIndexOf(
                    readId<GraphNodeTag> "valueNodeId" element,
                    readId<GraphNodeTag> "substringNodeId" element,
                    readId<GraphNodeTag> "fromNodeId" element
                )
            | "stringSpan" ->
                ensureProperties
                    "StringSpan node"
                    (set [ "id"; "kind"; "valueNodeId"; "setNodeId"; "fromNodeId" ])
                    (set [ "id"; "kind"; "valueNodeId"; "setNodeId"; "fromNodeId" ])
                    element
                SemanticNodeKind.StringSpan(
                    readId<GraphNodeTag> "valueNodeId" element,
                    readId<GraphNodeTag> "setNodeId" element,
                    readId<GraphNodeTag> "fromNodeId" element
                )
            | "stringSpanBack" ->
                ensureProperties
                    "StringSpanBack node"
                    (set [ "id"; "kind"; "valueNodeId"; "setNodeId"; "endNodeId" ])
                    (set [ "id"; "kind"; "valueNodeId"; "setNodeId"; "endNodeId" ])
                    element
                SemanticNodeKind.StringSpanBack(
                    readId<GraphNodeTag> "valueNodeId" element,
                    readId<GraphNodeTag> "setNodeId" element,
                    readId<GraphNodeTag> "endNodeId" element
                )
            | "stringLastIndexOf"
            | "stringStartsWith"
            | "stringEndsWith"
            | "stringSplit"
            | "stringSplitLines" as kindName ->
                let label, other, build =
                    match kindName with
                    | "stringLastIndexOf" -> "StringLastIndexOf node", "substringNodeId", (fun v o -> SemanticNodeKind.StringLastIndexOf(v, o))
                    | "stringStartsWith" -> "StringStartsWith node", "prefixNodeId", (fun v o -> SemanticNodeKind.StringStartsWith(v, o))
                    | "stringEndsWith" -> "StringEndsWith node", "suffixNodeId", (fun v o -> SemanticNodeKind.StringEndsWith(v, o))
                    | "stringSplitLines" -> "StringSplitLines node", "breaksNodeId", (fun v o -> SemanticNodeKind.StringSplitLines(v, o))
                    | _ -> "StringSplit node", "separatorNodeId", (fun v o -> SemanticNodeKind.StringSplit(v, o))
                ensureProperties label (set [ "id"; "kind"; "valueNodeId"; other ]) (set [ "id"; "kind"; "valueNodeId"; other ]) element
                build (readId<GraphNodeTag> "valueNodeId" element) (readId<GraphNodeTag> other element)
            | "stringEqual" ->
                readBinaryNode "StringEqual node" element (fun left right -> SemanticNodeKind.StringEqual(left, right))
            | "stringLessThan" ->
                readBinaryNode "StringLessThan node" element (fun left right -> SemanticNodeKind.StringLessThan(left, right))
            | "valueEqual" ->
                readBinaryNode "ValueEqual node" element (fun left right -> SemanticNodeKind.ValueEqual(left, right))
            | "stringConcat" ->
                readBinaryNode "StringConcat node" element (fun left right -> SemanticNodeKind.StringConcat(left, right))
            | "int64WrappingAdd" ->
                readBinaryNode "Int64WrappingAdd node" element (fun left right -> SemanticNodeKind.Int64WrappingAdd(left, right))
            | "int64WrappingSubtract" ->
                readBinaryNode "Int64WrappingSubtract node" element (fun left right -> SemanticNodeKind.Int64WrappingSubtract(left, right))
            | "int64WrappingMultiply" ->
                readBinaryNode "Int64WrappingMultiply node" element (fun left right -> SemanticNodeKind.Int64WrappingMultiply(left, right))
            | "int64FloorDivide" ->
                readBinaryNode "Int64FloorDivide node" element (fun left right -> SemanticNodeKind.Int64FloorDivide(left, right))
            | "int64FloorModulo" ->
                readBinaryNode "Int64FloorModulo node" element (fun left right -> SemanticNodeKind.Int64FloorModulo(left, right))
            | "int64Min" ->
                readBinaryNode "Int64Min node" element (fun left right -> SemanticNodeKind.Int64Min(left, right))
            | "int64Max" ->
                readBinaryNode "Int64Max node" element (fun left right -> SemanticNodeKind.Int64Max(left, right))
            | "int64CheckedAdd" ->
                readBinaryNode "Int64CheckedAdd node" element (fun left right -> SemanticNodeKind.Int64CheckedAdd(left, right))
            | "int64CheckedSubtract" ->
                readBinaryNode "Int64CheckedSubtract node" element (fun left right -> SemanticNodeKind.Int64CheckedSubtract(left, right))
            | "int64CheckedMultiply" ->
                readBinaryNode "Int64CheckedMultiply node" element (fun left right -> SemanticNodeKind.Int64CheckedMultiply(left, right))
            | "listRange" ->
                ensureProperties
                    "ListRange node"
                    (set [ "id"; "kind"; "startNodeId"; "endNodeId" ])
                    (set [ "id"; "kind"; "startNodeId"; "endNodeId" ])
                    element
                SemanticNodeKind.ListRange(readId<GraphNodeTag> "startNodeId" element, readId<GraphNodeTag> "endNodeId" element)
            | "stringRepeat" ->
                ensureProperties
                    "StringRepeat node"
                    (set [ "id"; "kind"; "textNodeId"; "countNodeId" ])
                    (set [ "id"; "kind"; "textNodeId"; "countNodeId" ])
                    element
                SemanticNodeKind.StringRepeat(readId<GraphNodeTag> "textNodeId" element, readId<GraphNodeTag> "countNodeId" element)
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
            | "int64ToFloat64" ->
                readUnaryNode "Int64ToFloat64 node" element SemanticNodeKind.Int64ToFloat64
            | "int64WrappingAbs" ->
                readUnaryNode "Int64WrappingAbs node" element SemanticNodeKind.Int64WrappingAbs
            | "bits32FromInt64" -> readUnaryNode "Bits32FromInt64 node" element SemanticNodeKind.Bits32FromInt64
            | "bits32ToInt64" -> readUnaryNode "Bits32ToInt64 node" element SemanticNodeKind.Bits32ToInt64
            | "bits32Not" -> readUnaryNode "Bits32Not node" element SemanticNodeKind.Bits32Not
            | "bits32Xor" -> readBinaryNode "Bits32Xor node" element (fun left right -> SemanticNodeKind.Bits32Xor(left, right))
            | "bits32And" -> readBinaryNode "Bits32And node" element (fun left right -> SemanticNodeKind.Bits32And(left, right))
            | "bits32Or" -> readBinaryNode "Bits32Or node" element (fun left right -> SemanticNodeKind.Bits32Or(left, right))
            | "bits32Add" -> readBinaryNode "Bits32Add node" element (fun left right -> SemanticNodeKind.Bits32Add(left, right))
            | "bits32RotateRight" ->
                ensureProperties
                    "Bits32RotateRight node"
                    (set [ "id"; "kind"; "valueNodeId"; "amountNodeId" ])
                    (set [ "id"; "kind"; "valueNodeId"; "amountNodeId" ])
                    element
                SemanticNodeKind.Bits32RotateRight(readId<GraphNodeTag> "valueNodeId" element, readId<GraphNodeTag> "amountNodeId" element)
            | "bits32ShiftRight" ->
                ensureProperties
                    "Bits32ShiftRight node"
                    (set [ "id"; "kind"; "valueNodeId"; "amountNodeId" ])
                    (set [ "id"; "kind"; "valueNodeId"; "amountNodeId" ])
                    element
                SemanticNodeKind.Bits32ShiftRight(readId<GraphNodeTag> "valueNodeId" element, readId<GraphNodeTag> "amountNodeId" element)
            | "bits32ShiftLeft" ->
                ensureProperties
                    "Bits32ShiftLeft node"
                    (set [ "id"; "kind"; "valueNodeId"; "amountNodeId" ])
                    (set [ "id"; "kind"; "valueNodeId"; "amountNodeId" ])
                    element
                SemanticNodeKind.Bits32ShiftLeft(readId<GraphNodeTag> "valueNodeId" element, readId<GraphNodeTag> "amountNodeId" element)
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
            | "listReverse" ->
                ensureProperties "ListReverse node" (set [ "id"; "kind"; "listNodeId" ]) (set [ "id"; "kind"; "listNodeId" ]) element
                SemanticNodeKind.ListReverse(readId<GraphNodeTag> "listNodeId" element)
            | "listItemAt" ->
                ensureProperties
                    "ListItemAt node"
                    (set [ "id"; "kind"; "listNodeId"; "indexNodeId" ])
                    (set [ "id"; "kind"; "listNodeId"; "indexNodeId" ])
                    element
                SemanticNodeKind.ListItemAt(
                    readId<GraphNodeTag> "listNodeId" element,
                    readId<GraphNodeTag> "indexNodeId" element
                )
            | "listSlice" ->
                ensureProperties
                    "ListSlice node"
                    (set [ "id"; "kind"; "listNodeId"; "startNodeId"; "endNodeId" ])
                    (set [ "id"; "kind"; "listNodeId"; "startNodeId"; "endNodeId" ])
                    element
                SemanticNodeKind.ListSlice(
                    readId<GraphNodeTag> "listNodeId" element,
                    readId<GraphNodeTag> "startNodeId" element,
                    readId<GraphNodeTag> "endNodeId" element
                )
            | "stringJoin" ->
                ensureProperties
                    "StringJoin node"
                    (set [ "id"; "kind"; "listNodeId"; "separatorNodeId" ])
                    (set [ "id"; "kind"; "listNodeId"; "separatorNodeId" ])
                    element
                SemanticNodeKind.StringJoin(readId<GraphNodeTag> "listNodeId" element, readId<GraphNodeTag> "separatorNodeId" element)
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
            | "listFold" ->
                ensureProperties "ListFold node" (set [ "id"; "kind"; "sourceListNodeId"; "seedNodeId"; "folderFunctionId" ]) (set [ "id"; "kind"; "sourceListNodeId"; "seedNodeId"; "folderFunctionId"; "context" ]) element
                SemanticNodeKind.ListFold(readId<GraphNodeTag> "sourceListNodeId" element, readId<GraphNodeTag> "seedNodeId" element, readId<FunctionTag> "folderFunctionId" element, readCallbackContext element)
            | "listEffectFold" ->
                ensureProperties "ListEffectFold node" (set [ "id"; "kind"; "sourceListNodeId"; "seedNodeId"; "folderFunctionId" ]) (set [ "id"; "kind"; "sourceListNodeId"; "seedNodeId"; "folderFunctionId"; "context" ]) element
                SemanticNodeKind.ListEffectFold(readId<GraphNodeTag> "sourceListNodeId" element, readId<GraphNodeTag> "seedNodeId" element, readId<FunctionTag> "folderFunctionId" element, readCallbackContext element)
            | "useResource" ->
                let required = set [ "id"; "kind"; "acquireNodeId"; "releaseCapabilityNodeId"; "releaseOperationId"; "bodyFunctionId" ]
                ensureProperties "UseResource node" required (Set.add "context" required) element
                SemanticNodeKind.UseResource(
                    readId<GraphNodeTag> "acquireNodeId" element,
                    readId<GraphNodeTag> "releaseCapabilityNodeId" element,
                    readId<CapabilityOperationTag> "releaseOperationId" element,
                    readId<FunctionTag> "bodyFunctionId" element,
                    readCallbackContext element)
            | "parallelAll" ->
                ensureProperties "ParallelAll node" (set [ "id"; "kind"; "sourceListNodeId"; "workerFunctionId" ]) (set [ "id"; "kind"; "sourceListNodeId"; "workerFunctionId"; "context" ]) element
                SemanticNodeKind.ParallelAll(readId<GraphNodeTag> "sourceListNodeId" element, readId<FunctionTag> "workerFunctionId" element, readCallbackContext element)
            | "cancellationRequested" ->
                ensureProperties "CancellationRequested node" (set [ "id"; "kind" ]) (set [ "id"; "kind" ]) element
                SemanticNodeKind.CancellationRequested
            | "taskScope" ->
                ensureProperties "TaskScope node" (set [ "id"; "kind"; "bodyFunctionId" ]) (set [ "id"; "kind"; "bodyFunctionId"; "context" ]) element
                SemanticNodeKind.TaskScope(readId<FunctionTag> "bodyFunctionId" element, readCallbackContext element)
            | "taskStart" ->
                let required = set [ "id"; "kind"; "scopeNodeId"; "workerFunctionId"; "itemNodeId" ]
                ensureProperties "TaskStart node" required (Set.add "context" required) element
                SemanticNodeKind.TaskStart(
                    readId<GraphNodeTag> "scopeNodeId" element,
                    readId<FunctionTag> "workerFunctionId" element,
                    readCallbackContext element,
                    readId<GraphNodeTag> "itemNodeId" element)
            | "taskWait" ->
                ensureProperties "TaskWait node" (set [ "id"; "kind"; "tasksNodeId"; "mode" ]) (set [ "id"; "kind"; "tasksNodeId"; "mode"; "limitNodeId" ]) element
                let mode =
                    match readString "mode" element with
                    | "any" -> TaskWaitMode.Any
                    | "all" -> TaskWaitMode.All
                    | value -> invalidShape $"Unsupported task wait mode '{value}'."
                SemanticNodeKind.TaskWait(
                    readId<GraphNodeTag> "tasksNodeId" element,
                    mode,
                    tryProperty "limitNodeId" element |> Option.map (fun _ -> readId<GraphNodeTag> "limitNodeId" element))
            | "taskCancel" ->
                ensureProperties "TaskCancel node" (set [ "id"; "kind"; "taskNodeId" ]) (set [ "id"; "kind"; "taskNodeId" ]) element
                SemanticNodeKind.TaskCancel(readId<GraphNodeTag> "taskNodeId" element)
            | "listMap" ->
                readListCallbackNode "ListMap node" "mapperFunctionId" element (fun source callback context ->
                    SemanticNodeKind.ListMap(source, callback, context))
            | "listFilter" ->
                readListCallbackNode "ListFilter node" "predicateFunctionId" element (fun source callback context ->
                    SemanticNodeKind.ListFilter(source, callback, context))
            | "listSortBy" ->
                readListCallbackNode "ListSortBy node" "lessThanFunctionId" element (fun source callback context ->
                    SemanticNodeKind.ListSortBy(source, callback, context))
            | "listAny" ->
                readListCallbackNode "ListAny node" "predicateFunctionId" element (fun source callback context ->
                    SemanticNodeKind.ListAny(source, callback, context))
            | "listAll" ->
                readListCallbackNode "ListAll node" "predicateFunctionId" element (fun source callback context ->
                    SemanticNodeKind.ListAll(source, callback, context))
            | "capabilityConstruct" ->
                let fields = set [ "id"; "kind"; "capabilityId"; "implementations" ]
                ensureProperties "Capability construction" fields fields element
                let implementations =
                    readArray "implementations" (fun item -> item) element
                    |> List.map (fun item ->
                        let fields = set [ "operationId"; "functionId" ]
                        ensureProperties "Capability implementation" fields fields item
                        readId<CapabilityOperationTag> "operationId" item, readId<FunctionTag> "functionId" item)
                SemanticNodeKind.CapabilityConstruct(readId<CapabilityTag> "capabilityId" element, implementations)
            | "functionReference" ->
                ensureProperties "Function reference" (set [ "id"; "kind"; "targetFunctionId" ]) (set [ "id"; "kind"; "targetFunctionId" ]) element
                SemanticNodeKind.FunctionReference(readId<FunctionTag> "targetFunctionId" element)
            | "functionInvoke" ->
                ensureProperties "Function invocation" (set [ "id"; "kind"; "functionValueNodeId"; "arguments" ]) (set [ "id"; "kind"; "functionValueNodeId"; "arguments" ]) element
                SemanticNodeKind.FunctionInvoke(readId<GraphNodeTag> "functionValueNodeId" element, readIdArray<GraphNodeTag> "arguments" element)
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
            | "productUpdate" ->
                ensureProperties
                    "Product update node"
                    (set [ "id"; "kind"; "typeId"; "sourceNodeId"; "fields" ])
                    (set [ "id"; "kind"; "typeId"; "sourceNodeId"; "fields" ])
                    element
                SemanticNodeKind.ProductUpdate(
                    readId<TypeTag> "typeId" element,
                    readId<GraphNodeTag> "sourceNodeId" element,
                    readArray "fields" readFieldValue element
                )
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
            | "abort" ->
                ensureProperties "Abort node" (set [ "id"; "kind"; "errorNodeId" ]) (set [ "id"; "kind"; "errorNodeId" ]) element
                SemanticNodeKind.Abort(readId<GraphNodeTag> "errorNodeId" element)
            | "resultErr" ->
                ensureProperties "Result.Err node" (set [ "id"; "kind"; "okType"; "errorNodeId" ]) (set [ "id"; "kind"; "okType"; "errorNodeId" ]) element
                SemanticNodeKind.ResultErr(readTypeRef (requireProperty "okType" element), readId<GraphNodeTag> "errorNodeId" element)
            | "boolNot" ->
                ensureProperties "BoolNot node" (set [ "id"; "kind"; "operandNodeId" ]) (set [ "id"; "kind"; "operandNodeId" ]) element
                SemanticNodeKind.BoolNot(readId<GraphNodeTag> "operandNodeId" element)
            | "boolAnd" ->
                ensureProperties "BoolAnd node" (set [ "id"; "kind"; "leftNodeId"; "right" ]) (set [ "id"; "kind"; "leftNodeId"; "right" ]) element
                SemanticNodeKind.BoolAnd(readId<GraphNodeTag> "leftNodeId" element, readGraph (requireProperty "right" element))
            | "boolOr" ->
                ensureProperties "BoolOr node" (set [ "id"; "kind"; "leftNodeId"; "right" ]) (set [ "id"; "kind"; "leftNodeId"; "right" ]) element
                SemanticNodeKind.BoolOr(readId<GraphNodeTag> "leftNodeId" element, readGraph (requireProperty "right" element))
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
                    ensureProperties "Union match arm" (set [ "fieldBindings"; "body" ]) (set [ "variantId"; "variantIds"; "fieldBindings"; "body" ]) armElement
                    let variantIds =
                        match tryProperty "variantId" armElement, tryProperty "variantIds" armElement with
                        | Some _, None -> [ readId<VariantTag> "variantId" armElement ]
                        | None, Some _ ->
                            match readIdArray<VariantTag> "variantIds" armElement with
                            | _ :: _ :: _ as variantIds -> variantIds
                            | _ -> invalidShape "Union match arm 'variantIds' must name at least two variants; one variant uses 'variantId'."
                        | _ -> invalidShape "Union match arm requires exactly one of 'variantId' and 'variantIds'."
                    { VariantIds = variantIds
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

    let private deserializeUsing (dependency: Snapshot option) (json: string): Result<Snapshot, SnapshotCodecError> =
        if isNull json then
            Error(SnapshotCodecError.InvalidJson "Snapshot JSON must not be null.")
        else
            try
                use document = JsonDocument.Parse(json, JsonDocumentOptions(MaxDepth = maxJsonDepth))
                let root = document.RootElement
                let required = set [ "schemaVersion"; "snapshotId"; "types"; "effects"; "capabilities"; "functions"; "obligations" ]
                let allowed = Set.add "parentSnapshotId" required
                match dependency with
                | None -> ensureProperties "Snapshot" required allowed root
                | Some imported ->
                    ensureProperties "Resolved snapshot" (Set.add "dependency" required) (Set.add "dependency" allowed) root
                    let marker = requireProperty "dependency" root
                    ensureProperties "Dependency marker" (set [ "snapshotId"; "digest" ]) (set [ "snapshotId"; "digest" ]) marker
                    let digest =
                        match dependencyDigest imported with
                        | Ok digest -> digest
                        | Error _ -> invalidShape "The selected dependency exceeds the maximum JSON depth."
                    if readString "snapshotId" marker <> StableId.value imported.Id
                       || readString "digest" marker <> digest then
                        invalidShape "Dependency selection does not match the local snapshot."
                let version = readInt32 "schemaVersion" root
                if version <> schemaVersion && version <> capabilitySchemaVersion && version <> abortSchemaVersion && version <> timedWaitSchemaVersion then
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
                    if version < abortSchemaVersion && FunctionAbort.isUsed snapshot then
                        invalidShape "Function abort requires snapshot schemaVersion 11."
                    if version < timedWaitSchemaVersion && TaskTimeLimits.isUsed snapshot then
                        invalidShape "Timed TaskWait requires snapshot schemaVersion 12."
                    if version = schemaVersion && CapabilityImplementations.isUsed snapshot then
                        invalidShape "Capability construction requires snapshot schemaVersion 10."
                    let resolved =
                        match dependency with
                        | None -> snapshot
                        | Some imported -> resolveDependency imported snapshot
                    match SnapshotValidator.validate resolved with
                    | [] -> Ok snapshot
                    | issues -> Error(SnapshotCodecError.InvalidSnapshot issues)
            with
            | :? JsonException as error -> Error(SnapshotCodecError.InvalidJson error.Message)
            | :? InvalidDataException as error -> Error(SnapshotCodecError.InvalidShape error.Message)
            | :? ArgumentException as error -> Error(SnapshotCodecError.InvalidShape error.Message)
            | :? KeyNotFoundException as error -> Error(SnapshotCodecError.InvalidShape error.Message)

    let deserialize (json: string): Result<Snapshot, SnapshotCodecError> =
        deserializeUsing None json

    /// Strict parsing and validation with an explicitly resolved dependency graph.
    let deserializeResolved (dependency: Snapshot) (json: string): Result<Snapshot, SnapshotCodecError> =
        deserializeUsing (Some dependency) json
