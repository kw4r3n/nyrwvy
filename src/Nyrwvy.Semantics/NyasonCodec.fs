namespace Nyrwvy.Core

open System
open System.Globalization

[<RequireQualifiedAccess>]
type NyasonCodecError =
    | InvalidSyntax of NyasonSyntaxError
    | UnsupportedSchema of string
    | InvalidShape of string
    | InvalidSnapshot of ValidationIssue list

exception private NyasonShapeException of string
exception private NyasonSchemaException of string

[<RequireQualifiedAccess>]
module NyasonCodec =
    let schemaId = "nayamnyam.snapshot.v5"
    let abortSchemaId = "nayamnyam.snapshot.v7"
    let timedWaitSchemaId = "nayamnyam.snapshot.v8"
    let capabilitySchemaId = "nayamnyam.snapshot.v6"

    let private shape message = raise (NyasonShapeException message)

    let private record tag fields = NyasonValue.Record(tag, fields)
    let private list values = NyasonValue.List values
    let private text value = NyasonValue.Text value
    let private atom value = NyasonValue.Atom value

    let private encodeId prefix id =
        NyasonValue.Atom(prefix + ":" + Uri.EscapeDataString(StableId.value id))

    let private decodeId<'Kind> prefix value: StableId<'Kind> =
        match value with
        | NyasonValue.Atom encoded ->
            let expected = prefix + ":"
            if not (encoded.StartsWith(expected, StringComparison.Ordinal)) then
                shape $"Expected {prefix} stable ID, got '{encoded}'."

            let escaped = encoded.Substring(expected.Length)
            let raw =
                try Uri.UnescapeDataString escaped
                with :? UriFormatException ->
                    shape $"Stable ID '{encoded}' contains an invalid percent escape."
                    String.Empty

            match StableId.tryCreate<'Kind> raw with
            | Ok id -> id
            | Error error -> shape $"Stable ID '{encoded}' is invalid: {error}."
        | _ -> shape $"Expected {prefix} stable ID atom."

    let private recordFields expectedTag required optional value =
        match value with
        | NyasonValue.Record(tag, fields) when tag = expectedTag ->
            let names = fields |> List.map fst
            let distinct = names |> Set.ofList
            if names.Length <> distinct.Count then
                shape $"Record '{expectedTag}' contains duplicate fields."

            let actual = distinct
            let requiredSet = Set.ofList required
            let allowed = Set.union requiredSet (Set.ofList optional)
            let missing = Set.difference requiredSet actual
            let unexpected = Set.difference actual allowed

            if not missing.IsEmpty then
                let missingText = String.Join(", ", missing)
                shape $"Record '{expectedTag}' is missing required fields: {missingText}."
            if not unexpected.IsEmpty then
                let unexpectedText = String.Join(", ", unexpected)
                shape $"Record '{expectedTag}' contains unknown fields: {unexpectedText}."

            Map.ofList fields
        | NyasonValue.Record(tag, _) -> shape $"Expected record '{expectedTag}', got '{tag}'."
        | _ -> shape $"Expected record '{expectedTag}'."

    let private require name (fields: Map<string, NyasonValue>) =
        match Map.tryFind name fields with
        | Some value -> value
        | None -> shape $"Missing required field '{name}'."

    let private optional name (fields: Map<string, NyasonValue>) = Map.tryFind name fields

    let private decodeText value =
        match value with
        | NyasonValue.Text value -> value
        | _ -> shape "Expected string value."

    let private decodeBoolean value =
        match value with
        | NyasonValue.Boolean value -> value
        | _ -> shape "Expected boolean value."

    let private decodeInt64 value =
        match value with
        | NyasonValue.Atom encoded ->
            match Int64.TryParse(encoded, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, number -> number
            | false, _ -> shape $"Expected signed 64-bit integer atom, got '{encoded}'."
        | _ -> shape "Expected signed 64-bit integer atom."

    let private decodeFloat64Bits value =
        match value with
        | NyasonValue.Atom encoded ->
            match Float64Bits.tryParseHex encoded with
            | Ok bits -> bits
            | Error error -> shape $"Expected 16-digit binary64 bit atom, got '{encoded}': {error}"
        | _ -> shape "Expected binary64 bit atom."

    let private decodeList decoder value =
        match value with
        | NyasonValue.List values -> values |> List.map decoder
        | _ -> shape "Expected list value."

    let rec private encodeTypeRef typeRef =
        match typeRef with
        | TypeRef.Primitive PrimitiveType.String -> record "primitive-string" []
        | TypeRef.Primitive PrimitiveType.Bool -> record "primitive-bool" []
        | TypeRef.Primitive PrimitiveType.Unit -> record "primitive-unit" []
        | TypeRef.Primitive PrimitiveType.Int64 -> record "primitive-int64" []
        | TypeRef.Primitive PrimitiveType.Float64 -> record "primitive-float64" []
        | TypeRef.Primitive PrimitiveType.Bits32 -> record "primitive-bits32" []
        | TypeRef.Function(arguments, result) ->
            record "function-type" [ "parameters", NyasonValue.List(List.map encodeTypeRef arguments); "result", encodeTypeRef result ]
        | TypeRef.Named typeId -> record "named-type" [ "id", encodeId "type" typeId ]
        | TypeRef.TypeParameter(typeParameterId, displayName) ->
            record "type-parameter" [ "id", encodeId "type-parameter" typeParameterId; "name", text displayName ]
        | TypeRef.Capability capabilityId ->
            record "capability-type" [ "capability", encodeId "capability" capabilityId ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ]) ->
            record "option-type" [ "item", encodeTypeRef itemType ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
            record "result-type" [ "ok", encodeTypeRef okType; "error", encodeTypeRef errorType ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ]) ->
            record "list-type" [ "item", encodeTypeRef itemType ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ resultType ]) ->
            record "task-type" [ "result", encodeTypeRef resultType ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, []) -> record "task-scope-type" []
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, arguments) ->
            invalidOp $"Validated Option type unexpectedly has {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, arguments) ->
            invalidOp $"Validated Result type unexpectedly has {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, arguments) ->
            invalidOp $"Validated List type unexpectedly has {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Task, arguments) ->
            invalidOp $"Validated Task type unexpectedly has {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, arguments) ->
            invalidOp $"Validated TaskScope type unexpectedly has {arguments.Length} type arguments."

    let rec private decodeTypeRef value =
        match value with
        | NyasonValue.Record("primitive-string", _) ->
            recordFields "primitive-string" [] [] value |> ignore
            TypeRef.Primitive PrimitiveType.String
        | NyasonValue.Record("primitive-bool", _) ->
            recordFields "primitive-bool" [] [] value |> ignore
            TypeRef.Primitive PrimitiveType.Bool
        | NyasonValue.Record("primitive-unit", _) ->
            recordFields "primitive-unit" [] [] value |> ignore
            TypeRef.Primitive PrimitiveType.Unit
        | NyasonValue.Record("primitive-int64", _) ->
            recordFields "primitive-int64" [] [] value |> ignore
            TypeRef.Primitive PrimitiveType.Int64
        | NyasonValue.Record("primitive-float64", _) ->
            recordFields "primitive-float64" [] [] value |> ignore
            TypeRef.Primitive PrimitiveType.Float64
        | NyasonValue.Record("primitive-bits32", _) ->
            recordFields "primitive-bits32" [] [] value |> ignore
            TypeRef.Primitive PrimitiveType.Bits32
        | NyasonValue.Record("function-type", _) ->

            let fields = recordFields "function-type" [ "parameters"; "result" ] [] value
            TypeRef.Function(decodeList decodeTypeRef (require "parameters" fields), decodeTypeRef (require "result" fields))
        | NyasonValue.Record("type-parameter", _) ->
            let fields = recordFields "type-parameter" [ "id"; "name" ] [] value
            TypeRef.TypeParameter(decodeId<TypeParameterTag> "type-parameter" (require "id" fields), decodeText (require "name" fields))
        | NyasonValue.Record("named-type", _) ->
            let fields = recordFields "named-type" [ "id" ] [] value
            TypeRef.Named(decodeId<TypeTag> "type" (require "id" fields))
        | NyasonValue.Record("capability-type", _) ->
            let fields = recordFields "capability-type" [ "capability" ] [] value
            TypeRef.Capability(decodeId<CapabilityTag> "capability" (require "capability" fields))
        | NyasonValue.Record("option-type", _) ->
            let fields = recordFields "option-type" [ "item" ] [] value
            TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ decodeTypeRef (require "item" fields) ])
        | NyasonValue.Record("result-type", _) ->
            let fields = recordFields "result-type" [ "ok"; "error" ] [] value
            TypeRef.BuiltinGeneric(
                BuiltinGenericType.Result,
                [ decodeTypeRef (require "ok" fields); decodeTypeRef (require "error" fields) ]
            )
        | NyasonValue.Record("list-type", _) ->
            let fields = recordFields "list-type" [ "item" ] [] value
            TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ decodeTypeRef (require "item" fields) ])
        | NyasonValue.Record("task-type", _) ->
            let fields = recordFields "task-type" [ "result" ] [] value
            TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ decodeTypeRef (require "result" fields) ])
        | NyasonValue.Record("task-scope-type", _) ->
            recordFields "task-scope-type" [] [] value |> ignore
            TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, [])
        | NyasonValue.Record(tag, _) -> shape $"Unsupported type reference tag '{tag}'."
        | _ -> shape "Expected tagged type reference."

    let private encodeField (field: SemanticField) =
        record
            "field"
            [ "id", encodeId "field" field.Id
              "display-name", text field.DisplayName
              "type", encodeTypeRef field.Type ]

    let private decodeField value: SemanticField =
        let fields = recordFields "field" [ "id"; "display-name"; "type" ] [] value
        { Id = decodeId<FieldTag> "field" (require "id" fields)
          DisplayName = decodeText (require "display-name" fields)
          Type = decodeTypeRef (require "type" fields) }

    let private encodeVariant (variant: SemanticVariant) =
        record
            "variant"
            [ "id", encodeId "variant" variant.Id
              "display-name", text variant.DisplayName
              "fields", variant.Fields |> List.sortBy (fun item -> StableId.value item.Id) |> List.map encodeField |> list ]

    let private decodeVariant value: SemanticVariant =
        let fields = recordFields "variant" [ "id"; "display-name"; "fields" ] [] value
        { Id = decodeId<VariantTag> "variant" (require "id" fields)
          DisplayName = decodeText (require "display-name" fields)
          Fields = decodeList decodeField (require "fields" fields) }

    let private encodeType (item: SemanticType) =
        match item.Kind with
        | SemanticTypeKind.Product fields ->
            record
                "product-type"
                [ "id", encodeId "type" item.Id
                  "display-name", text item.DisplayName
                  "fields", fields |> List.sortBy (fun field -> StableId.value field.Id) |> List.map encodeField |> list ]
        | SemanticTypeKind.Union variants ->
            record
                "union-type"
                [ "id", encodeId "type" item.Id
                  "display-name", text item.DisplayName
                  "variants", variants |> List.sortBy (fun variant -> StableId.value variant.Id) |> List.map encodeVariant |> list ]

    let private decodeType value: SemanticType =
        match value with
        | NyasonValue.Record("product-type", _) ->
            let fields = recordFields "product-type" [ "id"; "display-name"; "fields" ] [] value
            { Id = decodeId<TypeTag> "type" (require "id" fields)
              DisplayName = decodeText (require "display-name" fields)
              Kind = SemanticTypeKind.Product(decodeList decodeField (require "fields" fields)) }
        | NyasonValue.Record("union-type", _) ->
            let fields = recordFields "union-type" [ "id"; "display-name"; "variants" ] [] value
            { Id = decodeId<TypeTag> "type" (require "id" fields)
              DisplayName = decodeText (require "display-name" fields)
              Kind = SemanticTypeKind.Union(decodeList decodeVariant (require "variants" fields)) }
        | NyasonValue.Record(tag, _) -> shape $"Unsupported semantic type tag '{tag}'."
        | _ -> shape "Expected semantic type record."

    let private encodeEffect (effect: SemanticEffect) =
        record "effect" [ "id", encodeId "effect" effect.Id; "display-name", text effect.DisplayName ]

    let private decodeEffect value: SemanticEffect =
        let fields = recordFields "effect" [ "id"; "display-name" ] [] value
        { Id = decodeId<EffectTag> "effect" (require "id" fields)
          DisplayName = decodeText (require "display-name" fields) }

    let private encodeCapabilityParameter (parameter: SemanticCapabilityParameter) =
        record
            "capability-parameter"
            [ "id", encodeId "capability-parameter" parameter.Id
              "display-name", text parameter.DisplayName
              "type", encodeTypeRef parameter.Type ]

    let private decodeCapabilityParameter value: SemanticCapabilityParameter =
        let fields = recordFields "capability-parameter" [ "id"; "display-name"; "type" ] [] value
        { Id = decodeId<CapabilityParameterTag> "capability-parameter" (require "id" fields)
          DisplayName = decodeText (require "display-name" fields)
          Type = decodeTypeRef (require "type" fields) }

    let private encodeCapabilityOperation (operation: SemanticCapabilityOperation) =
        let baseFields =
            [ "id", encodeId "capability-operation" operation.Id
              "display-name", text operation.DisplayName
              "parameters", operation.Parameters |> List.map encodeCapabilityParameter |> list
              "result-type", encodeTypeRef operation.ResultType
              "effect", encodeId "effect" operation.EffectId ]

        let fields =
            match operation.FailureType with
            | Some failureType -> baseFields @ [ "failure-type", encodeTypeRef failureType ]
            | None -> baseFields

        record "capability-operation" fields

    let private decodeCapabilityOperation value: SemanticCapabilityOperation =
        let fields =
            recordFields
                "capability-operation"
                [ "id"; "display-name"; "parameters"; "result-type"; "effect" ]
                [ "failure-type" ]
                value

        { Id = decodeId<CapabilityOperationTag> "capability-operation" (require "id" fields)
          DisplayName = decodeText (require "display-name" fields)
          Parameters = decodeList decodeCapabilityParameter (require "parameters" fields)
          ResultType = decodeTypeRef (require "result-type" fields)
          FailureType = optional "failure-type" fields |> Option.map decodeTypeRef
          EffectId = decodeId<EffectTag> "effect" (require "effect" fields) }

    let private encodeCapability (capability: SemanticCapability) =
        record
            "capability"
            [ "id", encodeId "capability" capability.Id
              "display-name", text capability.DisplayName
              "operations",
              capability.Operations
              |> List.sortBy (fun operation -> StableId.value operation.Id)
              |> List.map encodeCapabilityOperation
              |> list ]

    let private decodeCapability value: SemanticCapability =
        let fields = recordFields "capability" [ "id"; "display-name"; "operations" ] [] value
        { Id = decodeId<CapabilityTag> "capability" (require "id" fields)
          DisplayName = decodeText (require "display-name" fields)
          Operations = decodeList decodeCapabilityOperation (require "operations" fields) }

    let private encodeParameter (parameter: SemanticParameter) =
        record
            "parameter"
            [ "id", encodeId "parameter" parameter.Id
              "display-name", text parameter.DisplayName
              "type", encodeTypeRef parameter.Type ]

    let private decodeParameter value: SemanticParameter =
        let fields = recordFields "parameter" [ "id"; "display-name"; "type" ] [] value
        { Id = decodeId<ParameterTag> "parameter" (require "id" fields)
          DisplayName = decodeText (require "display-name" fields)
          Type = decodeTypeRef (require "type" fields) }

    let private encodeArgument (argument: ArgumentBinding) =
        record
            "argument"
            [ "target-parameter", encodeId "parameter" argument.TargetParameterId
              "value", encodeId "node" argument.ValueNodeId ]

    let private decodeArgument value: ArgumentBinding =
        let fields = recordFields "argument" [ "target-parameter"; "value" ] [] value
        { TargetParameterId = decodeId<ParameterTag> "parameter" (require "target-parameter" fields)
          ValueNodeId = decodeId<GraphNodeTag> "node" (require "value" fields) }

    let private encodeCapabilityArgument (argument: CapabilityArgumentBinding) =
        record
            "capability-argument"
            [ "target-parameter", encodeId "capability-parameter" argument.TargetCapabilityParameterId
              "value", encodeId "node" argument.ValueNodeId ]

    let private decodeCapabilityArgument value: CapabilityArgumentBinding =
        let fields = recordFields "capability-argument" [ "target-parameter"; "value" ] [] value
        { TargetCapabilityParameterId =
            decodeId<CapabilityParameterTag> "capability-parameter" (require "target-parameter" fields)
          ValueNodeId = decodeId<GraphNodeTag> "node" (require "value" fields) }

    let private encodeFieldValue (binding: FieldValueBinding) =
        record
            "field-value"
            [ "target-field", encodeId "field" binding.TargetFieldId
              "value", encodeId "node" binding.ValueNodeId ]

    let private decodeFieldValue value: FieldValueBinding =
        let fields = recordFields "field-value" [ "target-field"; "value" ] [] value
        { TargetFieldId = decodeId<FieldTag> "field" (require "target-field" fields)
          ValueNodeId = decodeId<GraphNodeTag> "node" (require "value" fields) }

    let private encodePatternBinding (binding: PatternBinding) =
        let displayFields =
            match binding.DisplayName with
            | Some displayName -> [ "display-name", text displayName ]
            | None -> []

        let fields =
            [ "id", encodeId "pattern-binding" binding.Id
              "type", encodeTypeRef binding.Type ]
            @ displayFields

        record "pattern-binding" fields

    let private decodePatternBinding value: PatternBinding =
        let fields = recordFields "pattern-binding" [ "id"; "type" ] [ "display-name" ] value
        { Id = decodeId<PatternBindingTag> "pattern-binding" (require "id" fields)
          DisplayName = optional "display-name" fields |> Option.map decodeText
          Type = decodeTypeRef (require "type" fields) }

    let private encodePatternField (binding: PatternFieldBinding) =
        record
            "pattern-field"
            [ "source-field", encodeId "field" binding.SourceFieldId
              "binding", encodePatternBinding binding.Binding ]

    let private decodePatternField value: PatternFieldBinding =
        let fields = recordFields "pattern-field" [ "source-field"; "binding" ] [] value
        { SourceFieldId = decodeId<FieldTag> "field" (require "source-field" fields)
          Binding = decodePatternBinding (require "binding" fields) }

    let rec private encodeGraph (graph: ValueGraph) =
        record
            "value-graph"
            [ "nodes",
              graph.Nodes
              |> List.sortBy (fun node -> StableId.value node.Id)
              |> List.map encodeNode
              |> list
              "result", encodeId "node" graph.ResultNodeId ]

    and private encodeUnionArm (arm: UnionMatchArm) =
        record
            "union-arm"
            [ "variant", encodeId "variant" arm.VariantId
              "fields",
              arm.FieldBindings
              |> List.sortBy (fun binding -> StableId.value binding.SourceFieldId)
              |> List.map encodePatternField
              |> list
              "body", encodeGraph arm.Body ]

    and private encodeValueArm (arm: ValueMatchArm) =
        record "value-arm" [ "binding", encodePatternBinding arm.ValueBinding; "body", encodeGraph arm.Body ]

    and private encodeErrorArm (arm: ErrorMatchArm) =
        record "error-arm" [ "binding", encodePatternBinding arm.ErrorBinding; "body", encodeGraph arm.Body ]

    and private encodeBinaryNode tag nodeId leftNodeId rightNodeId =
        record
            tag
            [ nodeId
              "left", encodeId "node" leftNodeId
              "right", encodeId "node" rightNodeId ]

    and private encodeUnaryNode tag nodeId valueNodeId =
        record tag [ nodeId; "value", encodeId "node" valueNodeId ]

    and private encodeCallbackContext (context: ArgumentBinding list) =
        if List.isEmpty context then []
        else [ "context", context |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId) |> List.map encodeArgument |> list ]

    and private encodeListCallbackNode tag nodeId sourceListNodeId callbackFunctionId context =
        record
            tag
            ([ nodeId
               "source", encodeId "node" sourceListNodeId
               "callback", encodeId "function" callbackFunctionId ]
             @ encodeCallbackContext context)

    and private encodeNode (node: SemanticNode) =
        let nodeId = "id", encodeId "node" node.Id
        match node.Kind with
        | SemanticNodeKind.ParameterReference parameterId ->
            record "parameter-reference" [ nodeId; "parameter", encodeId "parameter" parameterId ]
        | SemanticNodeKind.PatternReference bindingId ->
            record "pattern-reference" [ nodeId; "binding", encodeId "pattern-binding" bindingId ]
        | SemanticNodeKind.StringLiteral value -> record "string-literal" [ nodeId; "value", text value ]
        | SemanticNodeKind.BoolLiteral value -> record "bool-literal" [ nodeId; "value", NyasonValue.Boolean value ]
        | SemanticNodeKind.UnitLiteral -> record "unit-literal" [ nodeId ]
        | SemanticNodeKind.Int64Literal value ->
            record "int64-literal" [ nodeId; "value", atom (value.ToString(CultureInfo.InvariantCulture)) ]
        | SemanticNodeKind.Float64Literal bits ->
            record "float64-literal" [ nodeId; "bits", atom (Float64Bits.toHex bits) ]
        | SemanticNodeKind.StringIsEmpty valueNodeId ->
            record "string-is-empty" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.Int64ToString valueNodeId ->
            record "int64-to-string" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringParseInt64 valueNodeId ->
            record "string-parse-int64" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.Float64ToString valueNodeId ->
            record "float64-to-string" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringParseFloat64 valueNodeId ->
            record "string-parse-float64" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringCodePoints valueNodeId ->
            record "string-code-points" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringToLower valueNodeId ->
            record "string-to-lower" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringDecodeUtf8Lossy valueNodeId ->
            record "string-decode-utf8-lossy" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.Float64RoundToInt64 valueNodeId ->
            record "float64-round-to-int64" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.Float64FloorToInt64 valueNodeId ->
            record "float64-floor-to-int64" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringUtf8ByteCount valueNodeId ->
            record "string-utf8-byte-count" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringFromCodePoint valueNodeId ->
            record "string-from-code-point" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringToCodePoint valueNodeId ->
            record "string-to-code-point" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringLength valueNodeId ->
            record "string-length" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringSlice(valueNodeId, startNodeId, endNodeId) ->
            record
                "string-slice"
                [ nodeId
                  "value", encodeId "node" valueNodeId
                  "start", encodeId "node" startNodeId
                  "end", encodeId "node" endNodeId ]
        | SemanticNodeKind.StringContains(valueNodeId, substringNodeId) ->
            record
                "string-contains"
                [ nodeId
                  "value", encodeId "node" valueNodeId
                  "substring", encodeId "node" substringNodeId ]
        | SemanticNodeKind.StringIndexOf(valueNodeId, substringNodeId, fromNodeId) ->
            record
                "string-index-of"
                [ nodeId
                  "value", encodeId "node" valueNodeId
                  "substring", encodeId "node" substringNodeId
                  "from", encodeId "node" fromNodeId ]
        | SemanticNodeKind.StringSpan(valueNodeId, setNodeId, fromNodeId) ->
            record
                "string-span"
                [ nodeId
                  "value", encodeId "node" valueNodeId
                  "set", encodeId "node" setNodeId
                  "from", encodeId "node" fromNodeId ]
        | SemanticNodeKind.StringSpanBack(valueNodeId, setNodeId, endNodeId) ->
            record
                "string-span-back"
                [ nodeId
                  "value", encodeId "node" valueNodeId
                  "set", encodeId "node" setNodeId
                  "end", encodeId "node" endNodeId ]
        | SemanticNodeKind.StringLastIndexOf(valueNodeId, substringNodeId) ->
            record
                "string-last-index-of"
                [ nodeId
                  "value", encodeId "node" valueNodeId
                  "substring", encodeId "node" substringNodeId ]
        | SemanticNodeKind.StringStartsWith(valueNodeId, prefixNodeId) ->
            record "string-starts-with" [ nodeId; "value", encodeId "node" valueNodeId; "prefix", encodeId "node" prefixNodeId ]
        | SemanticNodeKind.StringEndsWith(valueNodeId, suffixNodeId) ->
            record "string-ends-with" [ nodeId; "value", encodeId "node" valueNodeId; "suffix", encodeId "node" suffixNodeId ]
        | SemanticNodeKind.StringSplit(valueNodeId, separatorNodeId) ->
            record
                "string-split"
                [ nodeId
                  "value", encodeId "node" valueNodeId
                  "separator", encodeId "node" separatorNodeId ]
        | SemanticNodeKind.StringSplitLines(valueNodeId, breaksNodeId) ->
            record
                "string-split-lines"
                [ nodeId
                  "value", encodeId "node" valueNodeId
                  "breaks", encodeId "node" breaksNodeId ]
        | SemanticNodeKind.StringEqual(leftNodeId, rightNodeId) ->
            encodeBinaryNode "string-equal" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.StringLessThan(leftNodeId, rightNodeId) ->
            encodeBinaryNode "string-less-than" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.ValueEqual(leftNodeId, rightNodeId) ->
            encodeBinaryNode "value-equal" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.StringConcat(leftNodeId, rightNodeId) ->
            encodeBinaryNode "string-concat" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId) ->
            encodeBinaryNode "int64-wrapping-add" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId) ->
            encodeBinaryNode "int64-wrapping-subtract" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId) ->
            encodeBinaryNode "int64-wrapping-multiply" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Int64FloorDivide(leftNodeId, rightNodeId) ->
            encodeBinaryNode "int64-floor-divide" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Int64FloorModulo(leftNodeId, rightNodeId) ->
            encodeBinaryNode "int64-floor-modulo" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Int64Equal(leftNodeId, rightNodeId) ->
            encodeBinaryNode "int64-equal" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Int64LessThan(leftNodeId, rightNodeId) ->
            encodeBinaryNode "int64-less-than" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Int64LessThanOrEqual(leftNodeId, rightNodeId) ->
            encodeBinaryNode "int64-less-than-or-equal" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Int64GreaterThan(leftNodeId, rightNodeId) ->
            encodeBinaryNode "int64-greater-than" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Int64GreaterThanOrEqual(leftNodeId, rightNodeId) ->
            encodeBinaryNode "int64-greater-than-or-equal" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Float64Add(leftNodeId, rightNodeId) ->
            encodeBinaryNode "float64-add" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Float64Subtract(leftNodeId, rightNodeId) ->
            encodeBinaryNode "float64-subtract" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Float64Multiply(leftNodeId, rightNodeId) ->
            encodeBinaryNode "float64-multiply" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Float64Divide(leftNodeId, rightNodeId) ->
            encodeBinaryNode "float64-divide" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Float64Equal(leftNodeId, rightNodeId) ->
            encodeBinaryNode "float64-equal" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Float64LessThan(leftNodeId, rightNodeId) ->
            encodeBinaryNode "float64-less-than" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Float64IsNaN valueNodeId ->
            encodeUnaryNode "float64-is-nan" nodeId valueNodeId
        | SemanticNodeKind.Float64IsFinite valueNodeId ->
            encodeUnaryNode "float64-is-finite" nodeId valueNodeId
        | SemanticNodeKind.Float64IsInfinite valueNodeId ->
            encodeUnaryNode "float64-is-infinite" nodeId valueNodeId
        | SemanticNodeKind.Int64ToFloat64 valueNodeId ->
            encodeUnaryNode "int64-to-float64" nodeId valueNodeId
        | SemanticNodeKind.Bits32FromInt64 valueNodeId -> encodeUnaryNode "bits32-from-int64" nodeId valueNodeId
        | SemanticNodeKind.Bits32ToInt64 valueNodeId -> encodeUnaryNode "bits32-to-int64" nodeId valueNodeId
        | SemanticNodeKind.Bits32Not valueNodeId -> encodeUnaryNode "bits32-not" nodeId valueNodeId
        | SemanticNodeKind.Bits32Xor(leftNodeId, rightNodeId) -> encodeBinaryNode "bits32-xor" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Bits32And(leftNodeId, rightNodeId) -> encodeBinaryNode "bits32-and" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Bits32Or(leftNodeId, rightNodeId) -> encodeBinaryNode "bits32-or" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Bits32Add(leftNodeId, rightNodeId) -> encodeBinaryNode "bits32-add" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Bits32RotateRight(valueNodeId, amountNodeId) ->
            record "bits32-rotate-right" [ nodeId; "value", encodeId "node" valueNodeId; "amount", encodeId "node" amountNodeId ]
        | SemanticNodeKind.Bits32ShiftRight(valueNodeId, amountNodeId) ->
            record "bits32-shift-right" [ nodeId; "value", encodeId "node" valueNodeId; "amount", encodeId "node" amountNodeId ]
        | SemanticNodeKind.Bits32ShiftLeft(valueNodeId, amountNodeId) ->
            record "bits32-shift-left" [ nodeId; "value", encodeId "node" valueNodeId; "amount", encodeId "node" amountNodeId ]
        | SemanticNodeKind.ListEmpty itemType ->
            record "list-empty" [ nodeId; "item-type", encodeTypeRef itemType ]
        | SemanticNodeKind.ListConstruct(itemType, itemNodeIds) ->
            record
                "list-construct"
                [ nodeId
                  "item-type", encodeTypeRef itemType
                  // List item order is semantic. Do not sort.
                  "items", itemNodeIds |> List.map (encodeId "node") |> list ]
        | SemanticNodeKind.StringJoin(listNodeId, separatorNodeId) ->
            record
                "string-join"
                [ nodeId
                  "list", encodeId "node" listNodeId
                  "separator", encodeId "node" separatorNodeId ]
        | SemanticNodeKind.ListSlice(listNodeId, startNodeId, endNodeId) ->
            record
                "list-slice"
                [ nodeId
                  "list", encodeId "node" listNodeId
                  "start", encodeId "node" startNodeId
                  "end", encodeId "node" endNodeId ]
        | SemanticNodeKind.ListLength listNodeId ->
            record "list-length" [ nodeId; "list", encodeId "node" listNodeId ]
        | SemanticNodeKind.ListItemAt(listNodeId, indexNodeId) ->
            record
                "list-item-at"
                [ nodeId
                  "list", encodeId "node" listNodeId
                  "index", encodeId "node" indexNodeId ]
        | SemanticNodeKind.ListAppendItem(listNodeId, itemNodeId) ->
            record
                "list-append-item"
                [ nodeId
                  "list", encodeId "node" listNodeId
                  "item", encodeId "node" itemNodeId ]
        | SemanticNodeKind.ListConcat(leftListNodeId, rightListNodeId) ->
            record
                "list-concat"
                [ nodeId
                  "left", encodeId "node" leftListNodeId
                  "right", encodeId "node" rightListNodeId ]
        | SemanticNodeKind.ListFold(sourceListNodeId, seedNodeId, folderFunctionId, context) ->
            record "list-fold" ([ nodeId; "source", encodeId "node" sourceListNodeId; "seed", encodeId "node" seedNodeId; "folder", encodeId "function" folderFunctionId ] @ encodeCallbackContext context)
        | SemanticNodeKind.ListEffectFold(sourceListNodeId, seedNodeId, folderFunctionId, context) ->
            record "list-effect-fold" ([ nodeId; "source", encodeId "node" sourceListNodeId; "seed", encodeId "node" seedNodeId; "folder", encodeId "function" folderFunctionId ] @ encodeCallbackContext context)
        | SemanticNodeKind.UseResource(acquireNodeId, releaseCapabilityNodeId, releaseOperationId, bodyFunctionId, context) ->
            record
                "use-resource"
                ([ nodeId
                   "acquire", encodeId "node" acquireNodeId
                   "release-capability", encodeId "node" releaseCapabilityNodeId
                   "release-operation", encodeId "capability-operation" releaseOperationId
                   "body", encodeId "function" bodyFunctionId ]
                 @ encodeCallbackContext context)
        | SemanticNodeKind.ParallelAll(sourceListNodeId, workerFunctionId, context) ->
            record "parallel-all" ([ nodeId; "source", encodeId "node" sourceListNodeId; "worker", encodeId "function" workerFunctionId ] @ encodeCallbackContext context)
        | SemanticNodeKind.CancellationRequested -> record "cancellation-requested" [ nodeId ]
        | SemanticNodeKind.TaskScope(bodyFunctionId, context) ->
            record "task-scope" ([ nodeId; "body", encodeId "function" bodyFunctionId ] @ encodeCallbackContext context)
        | SemanticNodeKind.TaskStart(scopeNodeId, workerFunctionId, context, itemNodeId) ->
            record
                "task-start"
                ([ nodeId
                   "scope", encodeId "node" scopeNodeId
                   "worker", encodeId "function" workerFunctionId
                   "item", encodeId "node" itemNodeId ]
                 @ encodeCallbackContext context)
        | SemanticNodeKind.TaskWait(tasksNodeId, mode, limitNodeId) ->
            let modeAtom =
                match mode with
                | TaskWaitMode.Any -> "any"
                | TaskWaitMode.All -> "all"
            record "task-wait"
                ([ nodeId; "tasks", encodeId "node" tasksNodeId; "mode", atom modeAtom ]
                 @ (limitNodeId |> Option.map (fun limit -> "limit", encodeId "node" limit) |> Option.toList))
        | SemanticNodeKind.TaskCancel taskNodeId -> record "task-cancel" [ nodeId; "task", encodeId "node" taskNodeId ]
        | SemanticNodeKind.ListMap(sourceListNodeId, mapperFunctionId, context) ->
            encodeListCallbackNode "list-map" nodeId sourceListNodeId mapperFunctionId context
        | SemanticNodeKind.ListFilter(sourceListNodeId, predicateFunctionId, context) ->
            encodeListCallbackNode "list-filter" nodeId sourceListNodeId predicateFunctionId context
        | SemanticNodeKind.ListSortBy(sourceListNodeId, lessThanFunctionId, context) ->
            encodeListCallbackNode "list-sort-by" nodeId sourceListNodeId lessThanFunctionId context
        | SemanticNodeKind.ListAny(sourceListNodeId, predicateFunctionId, context) ->
            encodeListCallbackNode "list-any" nodeId sourceListNodeId predicateFunctionId context
        | SemanticNodeKind.ListAll(sourceListNodeId, predicateFunctionId, context) ->
            encodeListCallbackNode "list-all" nodeId sourceListNodeId predicateFunctionId context
        | SemanticNodeKind.CapabilityConstruct(capabilityId, implementations) ->
            record "capability-construct"
                [ nodeId; "capability", encodeId "capability" capabilityId
                  "implementations", list (implementations |> List.sortBy (fst >> StableId.value) |> List.map (fun (operationId, functionId) ->
                      record "implementation" [ "operation", encodeId "capability-operation" operationId; "function", encodeId "function" functionId ])) ]
        | SemanticNodeKind.FunctionReference target ->
            record "function-reference" [ nodeId; "target", encodeId "function" target ]
        | SemanticNodeKind.FunctionInvoke(value, arguments) ->
            record "function-invoke" [ nodeId; "value", encodeId "node" value; "arguments", list (List.map (encodeId "node") arguments) ]
        | SemanticNodeKind.DirectCall(functionId, arguments) ->
            record
                "direct-call"
                [ nodeId
                  "target-function", encodeId "function" functionId
                  "arguments",
                  arguments
                  |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId)
                  |> List.map encodeArgument
                  |> list ]
        | SemanticNodeKind.CapabilityInvoke(capabilityNodeId, operationId, arguments) ->
            record
                "capability-invoke"
                [ nodeId
                  "capability-value", encodeId "node" capabilityNodeId
                  "operation", encodeId "capability-operation" operationId
                  "arguments",
                  arguments
                  |> List.sortBy (fun argument -> StableId.value argument.TargetCapabilityParameterId)
                  |> List.map encodeCapabilityArgument
                  |> list ]
        | SemanticNodeKind.ProductConstruct(typeId, fields) ->
            record
                "product-construct"
                [ nodeId
                  "type", encodeId "type" typeId
                  "fields",
                  fields
                  |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
                  |> List.map encodeFieldValue
                  |> list ]
        | SemanticNodeKind.FieldAccess(sourceNodeId, fieldId) ->
            record
                "field-access"
                [ nodeId; "source", encodeId "node" sourceNodeId; "field", encodeId "field" fieldId ]
        | SemanticNodeKind.ProductUpdate(typeId, sourceNodeId, fields) ->
            record
                "product-update"
                [ nodeId
                  "type", encodeId "type" typeId
                  "source", encodeId "node" sourceNodeId
                  "fields",
                  fields
                  |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
                  |> List.map encodeFieldValue
                  |> list ]
        | SemanticNodeKind.UnionConstruct(variantId, fields) ->
            record
                "union-construct"
                [ nodeId
                  "variant", encodeId "variant" variantId
                  "fields",
                  fields
                  |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
                  |> List.map encodeFieldValue
                  |> list ]
        | SemanticNodeKind.OptionNone itemType ->
            record "option-none" [ nodeId; "item-type", encodeTypeRef itemType ]
        | SemanticNodeKind.OptionSome valueNodeId ->
            record "option-some" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.ResultOk(errorType, valueNodeId) ->
            record
                "result-ok"
                [ nodeId; "error-type", encodeTypeRef errorType; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.Abort errorNodeId -> record "abort" [ nodeId; "error", encodeId "node" errorNodeId ]
        | SemanticNodeKind.ResultErr(okType, errorNodeId) ->
            record
                "result-err"
                [ nodeId; "ok-type", encodeTypeRef okType; "error", encodeId "node" errorNodeId ]
        | SemanticNodeKind.BoolNot operandNodeId ->
            record "bool-not" [ nodeId; "operand", encodeId "node" operandNodeId ]
        | SemanticNodeKind.BoolAnd(leftNodeId, right) ->
            record "bool-and" [ nodeId; "left", encodeId "node" leftNodeId; "right", encodeGraph right ]
        | SemanticNodeKind.BoolOr(leftNodeId, right) ->
            record "bool-or" [ nodeId; "left", encodeId "node" leftNodeId; "right", encodeGraph right ]
        | SemanticNodeKind.Conditional(conditionNodeId, whenTrue, whenFalse) ->
            record
                "conditional"
                [ nodeId
                  "condition", encodeId "node" conditionNodeId
                  "when-true", encodeGraph whenTrue
                  "when-false", encodeGraph whenFalse ]
        | SemanticNodeKind.MatchUnion(inputNodeId, arms) ->
            record
                "match-union"
                [ nodeId
                  "input", encodeId "node" inputNodeId
                  "arms",
                  arms
                  |> List.sortBy (fun arm -> StableId.value arm.VariantId)
                  |> List.map encodeUnionArm
                  |> list ]
        | SemanticNodeKind.Bind(inputNodeId, body) ->
            record
                "bind"
                [ nodeId
                  "input", encodeId "node" inputNodeId
                  "bound-body", encodeValueArm body ]
        | SemanticNodeKind.MatchOption(inputNodeId, someArm, noneBody) ->
            record
                "match-option"
                [ nodeId
                  "input", encodeId "node" inputNodeId
                  "some", encodeValueArm someArm
                  "none", encodeGraph noneBody ]
        | SemanticNodeKind.MatchResult(inputNodeId, okArm, errArm) ->
            record
                "match-result"
                [ nodeId
                  "input", encodeId "node" inputNodeId
                  "ok", encodeValueArm okArm
                  "err", encodeErrorArm errArm ]

    let rec private decodeGraph value: ValueGraph =
        let fields = recordFields "value-graph" [ "nodes"; "result" ] [] value
        { Nodes = decodeList decodeNode (require "nodes" fields)
          ResultNodeId = decodeId<GraphNodeTag> "node" (require "result" fields) }

    and private decodeUnionArm value: UnionMatchArm =
        let fields = recordFields "union-arm" [ "variant"; "fields"; "body" ] [] value
        { VariantId = decodeId<VariantTag> "variant" (require "variant" fields)
          FieldBindings = decodeList decodePatternField (require "fields" fields)
          Body = decodeGraph (require "body" fields) }

    and private decodeValueArm value: ValueMatchArm =
        let fields = recordFields "value-arm" [ "binding"; "body" ] [] value
        { ValueBinding = decodePatternBinding (require "binding" fields)
          Body = decodeGraph (require "body" fields) }

    and private decodeErrorArm value: ErrorMatchArm =
        let fields = recordFields "error-arm" [ "binding"; "body" ] [] value
        { ErrorBinding = decodePatternBinding (require "binding" fields)
          Body = decodeGraph (require "body" fields) }

    and private decodeBinaryNode tag value constructor =
        let fields = recordFields tag [ "id"; "left"; "right" ] [] value
        { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
          Kind =
            constructor
                (decodeId<GraphNodeTag> "node" (require "left" fields))
                (decodeId<GraphNodeTag> "node" (require "right" fields)) }

    and private decodeUnaryNode tag value constructor =
        let fields = recordFields tag [ "id"; "value" ] [] value
        { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
          Kind = constructor (decodeId<GraphNodeTag> "node" (require "value" fields)) }

    and private decodeCallbackContext fields =
        match optional "context" fields with
        | None -> []
        | Some value ->
            match decodeList decodeArgument value with
            | [] -> shape "Callback context must be omitted rather than empty."
            | context -> context

    and private decodeListCallbackNode tag value constructor =
        let fields = recordFields tag [ "id"; "source"; "callback" ] [ "context" ] value
        { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
          Kind =
            constructor
                (decodeId<GraphNodeTag> "node" (require "source" fields))
                (decodeId<FunctionTag> "function" (require "callback" fields))
                (decodeCallbackContext fields) }

    and private decodeNode value: SemanticNode =
        match value with
        | NyasonValue.Record("parameter-reference", _) ->
            let fields = recordFields "parameter-reference" [ "id"; "parameter" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ParameterReference(decodeId<ParameterTag> "parameter" (require "parameter" fields)) }
        | NyasonValue.Record("pattern-reference", _) ->
            let fields = recordFields "pattern-reference" [ "id"; "binding" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.PatternReference(decodeId<PatternBindingTag> "pattern-binding" (require "binding" fields)) }
        | NyasonValue.Record("string-literal", _) ->
            let fields = recordFields "string-literal" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.StringLiteral(decodeText (require "value" fields)) }
        | NyasonValue.Record("bool-literal", _) ->
            let fields = recordFields "bool-literal" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.BoolLiteral(decodeBoolean (require "value" fields)) }
        | NyasonValue.Record("unit-literal", _) ->
            let fields = recordFields "unit-literal" [ "id" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.UnitLiteral }
        | NyasonValue.Record("int64-literal", _) ->
            let fields = recordFields "int64-literal" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.Int64Literal(decodeInt64 (require "value" fields)) }
        | NyasonValue.Record("float64-literal", _) ->
            let fields = recordFields "float64-literal" [ "id"; "bits" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.Float64Literal(decodeFloat64Bits (require "bits" fields)) }
        | NyasonValue.Record("string-is-empty", _) ->
            let fields = recordFields "string-is-empty" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.StringIsEmpty(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("int64-to-string", _) ->
            let fields = recordFields "int64-to-string" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.Int64ToString(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("string-parse-int64", _) ->
            let fields = recordFields "string-parse-int64" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.StringParseInt64(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("float64-to-string", _) ->
            let fields = recordFields "float64-to-string" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.Float64ToString(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("string-parse-float64", _) ->
            let fields = recordFields "string-parse-float64" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.StringParseFloat64(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("string-code-points", _) ->
            let fields = recordFields "string-code-points" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.StringCodePoints(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("string-to-lower", _) ->
            let fields = recordFields "string-to-lower" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.StringToLower(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("string-decode-utf8-lossy", _) ->
            let fields = recordFields "string-decode-utf8-lossy" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.StringDecodeUtf8Lossy(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("float64-round-to-int64", _) ->
            let fields = recordFields "float64-round-to-int64" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.Float64RoundToInt64(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("float64-floor-to-int64", _) ->
            let fields = recordFields "float64-floor-to-int64" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.Float64FloorToInt64(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("string-utf8-byte-count", _) ->
            let fields = recordFields "string-utf8-byte-count" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.StringUtf8ByteCount(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("string-from-code-point", _) ->
            let fields = recordFields "string-from-code-point" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.StringFromCodePoint(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("string-to-code-point", _) ->
            let fields = recordFields "string-to-code-point" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.StringToCodePoint(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("string-length", _) ->
            let fields = recordFields "string-length" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.StringLength(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("string-slice", _) ->
            let fields = recordFields "string-slice" [ "id"; "value"; "start"; "end" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.StringSlice(
                    decodeId<GraphNodeTag> "node" (require "value" fields),
                    decodeId<GraphNodeTag> "node" (require "start" fields),
                    decodeId<GraphNodeTag> "node" (require "end" fields)
                ) }
        | NyasonValue.Record("string-contains", _) ->
            let fields = recordFields "string-contains" [ "id"; "value"; "substring" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.StringContains(
                    decodeId<GraphNodeTag> "node" (require "value" fields),
                    decodeId<GraphNodeTag> "node" (require "substring" fields)
                ) }
        | NyasonValue.Record("string-index-of", _) ->
            let fields = recordFields "string-index-of" [ "id"; "value"; "substring"; "from" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.StringIndexOf(
                    decodeId<GraphNodeTag> "node" (require "value" fields),
                    decodeId<GraphNodeTag> "node" (require "substring" fields),
                    decodeId<GraphNodeTag> "node" (require "from" fields)
                ) }
        | NyasonValue.Record(("string-span" | "string-span-back") as tag, _) ->
            let bound, build =
                match tag with
                | "string-span" -> "from", (fun v s b -> SemanticNodeKind.StringSpan(v, s, b))
                | _ -> "end", (fun v s b -> SemanticNodeKind.StringSpanBack(v, s, b))
            let fields = recordFields tag [ "id"; "value"; "set"; bound ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                build
                    (decodeId<GraphNodeTag> "node" (require "value" fields))
                    (decodeId<GraphNodeTag> "node" (require "set" fields))
                    (decodeId<GraphNodeTag> "node" (require bound fields)) }
        | NyasonValue.Record(("string-last-index-of" | "string-starts-with" | "string-ends-with" | "string-split" | "string-split-lines") as tag, _) ->
            let other, build =
                match tag with
                | "string-last-index-of" -> "substring", (fun v o -> SemanticNodeKind.StringLastIndexOf(v, o))
                | "string-starts-with" -> "prefix", (fun v o -> SemanticNodeKind.StringStartsWith(v, o))
                | "string-ends-with" -> "suffix", (fun v o -> SemanticNodeKind.StringEndsWith(v, o))
                | "string-split-lines" -> "breaks", (fun v o -> SemanticNodeKind.StringSplitLines(v, o))
                | _ -> "separator", (fun v o -> SemanticNodeKind.StringSplit(v, o))
            let fields = recordFields tag [ "id"; "value"; other ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                build
                    (decodeId<GraphNodeTag> "node" (require "value" fields))
                    (decodeId<GraphNodeTag> "node" (require other fields)) }
        | NyasonValue.Record("string-equal", _) ->
            decodeBinaryNode "string-equal" value (fun left right -> SemanticNodeKind.StringEqual(left, right))
        | NyasonValue.Record("string-less-than", _) ->
            decodeBinaryNode "string-less-than" value (fun left right -> SemanticNodeKind.StringLessThan(left, right))
        | NyasonValue.Record("value-equal", _) ->
            decodeBinaryNode "value-equal" value (fun left right -> SemanticNodeKind.ValueEqual(left, right))
        | NyasonValue.Record("string-concat", _) ->
            decodeBinaryNode "string-concat" value (fun left right -> SemanticNodeKind.StringConcat(left, right))
        | NyasonValue.Record("int64-wrapping-add", _) ->
            decodeBinaryNode "int64-wrapping-add" value (fun left right -> SemanticNodeKind.Int64WrappingAdd(left, right))
        | NyasonValue.Record("int64-wrapping-subtract", _) ->
            decodeBinaryNode "int64-wrapping-subtract" value (fun left right -> SemanticNodeKind.Int64WrappingSubtract(left, right))
        | NyasonValue.Record("int64-wrapping-multiply", _) ->
            decodeBinaryNode "int64-wrapping-multiply" value (fun left right -> SemanticNodeKind.Int64WrappingMultiply(left, right))
        | NyasonValue.Record("int64-floor-divide", _) ->
            decodeBinaryNode "int64-floor-divide" value (fun left right -> SemanticNodeKind.Int64FloorDivide(left, right))
        | NyasonValue.Record("int64-floor-modulo", _) ->
            decodeBinaryNode "int64-floor-modulo" value (fun left right -> SemanticNodeKind.Int64FloorModulo(left, right))
        | NyasonValue.Record("int64-equal", _) ->
            decodeBinaryNode "int64-equal" value (fun left right -> SemanticNodeKind.Int64Equal(left, right))
        | NyasonValue.Record("int64-less-than", _) ->
            decodeBinaryNode "int64-less-than" value (fun left right -> SemanticNodeKind.Int64LessThan(left, right))
        | NyasonValue.Record("int64-less-than-or-equal", _) ->
            decodeBinaryNode "int64-less-than-or-equal" value (fun left right -> SemanticNodeKind.Int64LessThanOrEqual(left, right))
        | NyasonValue.Record("int64-greater-than", _) ->
            decodeBinaryNode "int64-greater-than" value (fun left right -> SemanticNodeKind.Int64GreaterThan(left, right))
        | NyasonValue.Record("int64-greater-than-or-equal", _) ->
            decodeBinaryNode "int64-greater-than-or-equal" value (fun left right -> SemanticNodeKind.Int64GreaterThanOrEqual(left, right))
        | NyasonValue.Record("float64-add", _) ->
            decodeBinaryNode "float64-add" value (fun left right -> SemanticNodeKind.Float64Add(left, right))
        | NyasonValue.Record("float64-subtract", _) ->
            decodeBinaryNode "float64-subtract" value (fun left right -> SemanticNodeKind.Float64Subtract(left, right))
        | NyasonValue.Record("float64-multiply", _) ->
            decodeBinaryNode "float64-multiply" value (fun left right -> SemanticNodeKind.Float64Multiply(left, right))
        | NyasonValue.Record("float64-divide", _) ->
            decodeBinaryNode "float64-divide" value (fun left right -> SemanticNodeKind.Float64Divide(left, right))
        | NyasonValue.Record("float64-equal", _) ->
            decodeBinaryNode "float64-equal" value (fun left right -> SemanticNodeKind.Float64Equal(left, right))
        | NyasonValue.Record("float64-less-than", _) ->
            decodeBinaryNode "float64-less-than" value (fun left right -> SemanticNodeKind.Float64LessThan(left, right))
        | NyasonValue.Record("float64-is-nan", _) ->
            decodeUnaryNode "float64-is-nan" value SemanticNodeKind.Float64IsNaN
        | NyasonValue.Record("float64-is-finite", _) ->
            decodeUnaryNode "float64-is-finite" value SemanticNodeKind.Float64IsFinite
        | NyasonValue.Record("float64-is-infinite", _) ->
            decodeUnaryNode "float64-is-infinite" value SemanticNodeKind.Float64IsInfinite
        | NyasonValue.Record("bits32-from-int64", _) -> decodeUnaryNode "bits32-from-int64" value SemanticNodeKind.Bits32FromInt64
        | NyasonValue.Record("bits32-to-int64", _) -> decodeUnaryNode "bits32-to-int64" value SemanticNodeKind.Bits32ToInt64
        | NyasonValue.Record("bits32-not", _) -> decodeUnaryNode "bits32-not" value SemanticNodeKind.Bits32Not
        | NyasonValue.Record("bits32-xor", _) ->
            decodeBinaryNode "bits32-xor" value (fun left right -> SemanticNodeKind.Bits32Xor(left, right))
        | NyasonValue.Record("bits32-and", _) ->
            decodeBinaryNode "bits32-and" value (fun left right -> SemanticNodeKind.Bits32And(left, right))
        | NyasonValue.Record("bits32-or", _) ->
            decodeBinaryNode "bits32-or" value (fun left right -> SemanticNodeKind.Bits32Or(left, right))
        | NyasonValue.Record("bits32-add", _) ->
            decodeBinaryNode "bits32-add" value (fun left right -> SemanticNodeKind.Bits32Add(left, right))
        | NyasonValue.Record("bits32-rotate-right", _) ->
            let fields = recordFields "bits32-rotate-right" [ "id"; "value"; "amount" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.Bits32RotateRight(
                    decodeId<GraphNodeTag> "node" (require "value" fields),
                    decodeId<GraphNodeTag> "node" (require "amount" fields)
                ) }
        | NyasonValue.Record("bits32-shift-right", _) ->
            let fields = recordFields "bits32-shift-right" [ "id"; "value"; "amount" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.Bits32ShiftRight(
                    decodeId<GraphNodeTag> "node" (require "value" fields),
                    decodeId<GraphNodeTag> "node" (require "amount" fields)
                ) }
        | NyasonValue.Record("bits32-shift-left", _) ->
            let fields = recordFields "bits32-shift-left" [ "id"; "value"; "amount" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.Bits32ShiftLeft(
                    decodeId<GraphNodeTag> "node" (require "value" fields),
                    decodeId<GraphNodeTag> "node" (require "amount" fields)
                ) }
        | NyasonValue.Record("int64-to-float64", _) ->

            decodeUnaryNode "int64-to-float64" value SemanticNodeKind.Int64ToFloat64
        | NyasonValue.Record("list-empty", _) ->
            let fields = recordFields "list-empty" [ "id"; "item-type" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ListEmpty(decodeTypeRef (require "item-type" fields)) }
        | NyasonValue.Record("list-construct", _) ->
            let fields = recordFields "list-construct" [ "id"; "item-type"; "items" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.ListConstruct(
                    decodeTypeRef (require "item-type" fields),
                    decodeList (decodeId<GraphNodeTag> "node") (require "items" fields)
                ) }
        | NyasonValue.Record("list-length", _) ->
            let fields = recordFields "list-length" [ "id"; "list" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ListLength(decodeId<GraphNodeTag> "node" (require "list" fields)) }
        | NyasonValue.Record("string-join", _) ->
            let fields = recordFields "string-join" [ "id"; "list"; "separator" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.StringJoin(
                    decodeId<GraphNodeTag> "node" (require "list" fields),
                    decodeId<GraphNodeTag> "node" (require "separator" fields)
                ) }
        | NyasonValue.Record("list-slice", _) ->
            let fields = recordFields "list-slice" [ "id"; "list"; "start"; "end" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.ListSlice(
                    decodeId<GraphNodeTag> "node" (require "list" fields),
                    decodeId<GraphNodeTag> "node" (require "start" fields),
                    decodeId<GraphNodeTag> "node" (require "end" fields)
                ) }
        | NyasonValue.Record("list-item-at", _) ->
            let fields = recordFields "list-item-at" [ "id"; "list"; "index" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.ListItemAt(
                    decodeId<GraphNodeTag> "node" (require "list" fields),
                    decodeId<GraphNodeTag> "node" (require "index" fields)
                ) }
        | NyasonValue.Record("list-append-item", _) ->
            let fields = recordFields "list-append-item" [ "id"; "list"; "item" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.ListAppendItem(
                    decodeId<GraphNodeTag> "node" (require "list" fields),
                    decodeId<GraphNodeTag> "node" (require "item" fields)
                ) }
        | NyasonValue.Record("list-concat", _) ->
            let fields = recordFields "list-concat" [ "id"; "left"; "right" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.ListConcat(
                    decodeId<GraphNodeTag> "node" (require "left" fields),
                    decodeId<GraphNodeTag> "node" (require "right" fields)
                ) }
        | NyasonValue.Record("list-fold", _) ->
            let fields = recordFields "list-fold" [ "id"; "source"; "seed"; "folder" ] [ "context" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ListFold(decodeId<GraphNodeTag> "node" (require "source" fields), decodeId<GraphNodeTag> "node" (require "seed" fields), decodeId<FunctionTag> "function" (require "folder" fields), decodeCallbackContext fields) }
        | NyasonValue.Record("list-effect-fold", _) ->
            let fields = recordFields "list-effect-fold" [ "id"; "source"; "seed"; "folder" ] [ "context" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ListEffectFold(decodeId<GraphNodeTag> "node" (require "source" fields), decodeId<GraphNodeTag> "node" (require "seed" fields), decodeId<FunctionTag> "function" (require "folder" fields), decodeCallbackContext fields) }
        | NyasonValue.Record("use-resource", _) ->
            let fields = recordFields "use-resource" [ "id"; "acquire"; "release-capability"; "release-operation"; "body" ] [ "context" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.UseResource(
                    decodeId<GraphNodeTag> "node" (require "acquire" fields),
                    decodeId<GraphNodeTag> "node" (require "release-capability" fields),
                    decodeId<CapabilityOperationTag> "capability-operation" (require "release-operation" fields),
                    decodeId<FunctionTag> "function" (require "body" fields),
                    decodeCallbackContext fields) }
        | NyasonValue.Record("parallel-all", _) ->
            let fields = recordFields "parallel-all" [ "id"; "source"; "worker" ] [ "context" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ParallelAll(decodeId<GraphNodeTag> "node" (require "source" fields), decodeId<FunctionTag> "function" (require "worker" fields), decodeCallbackContext fields) }
        | NyasonValue.Record("cancellation-requested", _) ->
            let fields = recordFields "cancellation-requested" [ "id" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields); Kind = SemanticNodeKind.CancellationRequested }
        | NyasonValue.Record("task-scope", _) ->
            let fields = recordFields "task-scope" [ "id"; "body" ] [ "context" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.TaskScope(decodeId<FunctionTag> "function" (require "body" fields), decodeCallbackContext fields) }
        | NyasonValue.Record("task-start", _) ->
            let fields = recordFields "task-start" [ "id"; "scope"; "worker"; "item" ] [ "context" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.TaskStart(
                    decodeId<GraphNodeTag> "node" (require "scope" fields),
                    decodeId<FunctionTag> "function" (require "worker" fields),
                    decodeCallbackContext fields,
                    decodeId<GraphNodeTag> "node" (require "item" fields)) }
        | NyasonValue.Record("task-wait", _) ->
            let fields = recordFields "task-wait" [ "id"; "tasks"; "mode" ] [ "limit" ] value
            let mode =
                match require "mode" fields with
                | NyasonValue.Atom "any" -> TaskWaitMode.Any
                | NyasonValue.Atom "all" -> TaskWaitMode.All
                | _ -> shape "Expected task wait mode atom 'any' or 'all'."
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.TaskWait(decodeId<GraphNodeTag> "node" (require "tasks" fields), mode, optional "limit" fields |> Option.map (decodeId<GraphNodeTag> "node")) }
        | NyasonValue.Record("task-cancel", _) ->
            let fields = recordFields "task-cancel" [ "id"; "task" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.TaskCancel(decodeId<GraphNodeTag> "node" (require "task" fields)) }
        | NyasonValue.Record("list-map", _) ->
            decodeListCallbackNode "list-map" value (fun source callback context -> SemanticNodeKind.ListMap(source, callback, context))
        | NyasonValue.Record("list-filter", _) ->
            decodeListCallbackNode "list-filter" value (fun source callback context -> SemanticNodeKind.ListFilter(source, callback, context))
        | NyasonValue.Record("list-sort-by", _) ->
            decodeListCallbackNode "list-sort-by" value (fun source callback context -> SemanticNodeKind.ListSortBy(source, callback, context))
        | NyasonValue.Record("list-any", _) ->
            decodeListCallbackNode "list-any" value (fun source callback context -> SemanticNodeKind.ListAny(source, callback, context))
        | NyasonValue.Record("list-all", _) ->
            decodeListCallbackNode "list-all" value (fun source callback context -> SemanticNodeKind.ListAll(source, callback, context))
        | NyasonValue.Record("capability-construct", _) ->
            let fields = recordFields "capability-construct" [ "id"; "capability"; "implementations" ] [] value
            let implementations = decodeList (fun item ->
                let fields = recordFields "implementation" [ "operation"; "function" ] [] item
                decodeId<CapabilityOperationTag> "capability-operation" (require "operation" fields), decodeId<FunctionTag> "function" (require "function" fields)) (require "implementations" fields)
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.CapabilityConstruct(decodeId<CapabilityTag> "capability" (require "capability" fields), implementations) }
        | NyasonValue.Record("function-reference", _) ->
            let fields = recordFields "function-reference" [ "id"; "target" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.FunctionReference(decodeId<FunctionTag> "function" (require "target" fields)) }
        | NyasonValue.Record("function-invoke", _) ->
            let fields = recordFields "function-invoke" [ "id"; "value"; "arguments" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.FunctionInvoke(decodeId<GraphNodeTag> "node" (require "value" fields), decodeList (decodeId<GraphNodeTag> "node") (require "arguments" fields)) }
        | NyasonValue.Record("direct-call", _) ->
            let fields = recordFields "direct-call" [ "id"; "target-function"; "arguments" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.DirectCall(
                    decodeId<FunctionTag> "function" (require "target-function" fields),
                    decodeList decodeArgument (require "arguments" fields)
                ) }
        | NyasonValue.Record("capability-invoke", _) ->
            let fields =
                recordFields "capability-invoke" [ "id"; "capability-value"; "operation"; "arguments" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.CapabilityInvoke(
                    decodeId<GraphNodeTag> "node" (require "capability-value" fields),
                    decodeId<CapabilityOperationTag> "capability-operation" (require "operation" fields),
                    decodeList decodeCapabilityArgument (require "arguments" fields)
                ) }
        | NyasonValue.Record("product-construct", _) ->
            let fields = recordFields "product-construct" [ "id"; "type"; "fields" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.ProductConstruct(
                    decodeId<TypeTag> "type" (require "type" fields),
                    decodeList decodeFieldValue (require "fields" fields)
                ) }
        | NyasonValue.Record("field-access", _) ->
            let fields = recordFields "field-access" [ "id"; "source"; "field" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.FieldAccess(
                    decodeId<GraphNodeTag> "node" (require "source" fields),
                    decodeId<FieldTag> "field" (require "field" fields)
                ) }
        | NyasonValue.Record("product-update", _) ->
            let fields = recordFields "product-update" [ "id"; "type"; "source"; "fields" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.ProductUpdate(
                    decodeId<TypeTag> "type" (require "type" fields),
                    decodeId<GraphNodeTag> "node" (require "source" fields),
                    decodeList decodeFieldValue (require "fields" fields)
                ) }
        | NyasonValue.Record("union-construct", _) ->
            let fields = recordFields "union-construct" [ "id"; "variant"; "fields" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.UnionConstruct(
                    decodeId<VariantTag> "variant" (require "variant" fields),
                    decodeList decodeFieldValue (require "fields" fields)
                ) }
        | NyasonValue.Record("option-none", _) ->
            let fields = recordFields "option-none" [ "id"; "item-type" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.OptionNone(decodeTypeRef (require "item-type" fields)) }
        | NyasonValue.Record("option-some", _) ->
            let fields = recordFields "option-some" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.OptionSome(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("result-ok", _) ->
            let fields = recordFields "result-ok" [ "id"; "error-type"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.ResultOk(
                    decodeTypeRef (require "error-type" fields),
                    decodeId<GraphNodeTag> "node" (require "value" fields)
                ) }
        | NyasonValue.Record("abort", _) ->
            let fields = recordFields "abort" [ "id"; "error" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.Abort(decodeId<GraphNodeTag> "node" (require "error" fields)) }
        | NyasonValue.Record("result-err", _) ->
            let fields = recordFields "result-err" [ "id"; "ok-type"; "error" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.ResultErr(
                    decodeTypeRef (require "ok-type" fields),
                    decodeId<GraphNodeTag> "node" (require "error" fields)
                ) }
        | NyasonValue.Record("bool-not", _) ->
            let fields = recordFields "bool-not" [ "id"; "operand" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.BoolNot(decodeId<GraphNodeTag> "node" (require "operand" fields)) }
        | NyasonValue.Record("bool-and", _) ->
            let fields = recordFields "bool-and" [ "id"; "left"; "right" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.BoolAnd(
                    decodeId<GraphNodeTag> "node" (require "left" fields),
                    decodeGraph (require "right" fields)
                ) }
        | NyasonValue.Record("bool-or", _) ->
            let fields = recordFields "bool-or" [ "id"; "left"; "right" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.BoolOr(
                    decodeId<GraphNodeTag> "node" (require "left" fields),
                    decodeGraph (require "right" fields)
                ) }
        | NyasonValue.Record("conditional", _) ->
            let fields = recordFields "conditional" [ "id"; "condition"; "when-true"; "when-false" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.Conditional(
                    decodeId<GraphNodeTag> "node" (require "condition" fields),
                    decodeGraph (require "when-true" fields),
                    decodeGraph (require "when-false" fields)
                ) }
        | NyasonValue.Record("match-union", _) ->
            let fields = recordFields "match-union" [ "id"; "input"; "arms" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.MatchUnion(
                    decodeId<GraphNodeTag> "node" (require "input" fields),
                    decodeList decodeUnionArm (require "arms" fields)
                ) }
        | NyasonValue.Record("bind", _) ->
            let fields = recordFields "bind" [ "id"; "input"; "bound-body" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.Bind(
                    decodeId<GraphNodeTag> "node" (require "input" fields),
                    decodeValueArm (require "bound-body" fields)
                ) }
        | NyasonValue.Record("match-option", _) ->
            let fields = recordFields "match-option" [ "id"; "input"; "some"; "none" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.MatchOption(
                    decodeId<GraphNodeTag> "node" (require "input" fields),
                    decodeValueArm (require "some" fields),
                    decodeGraph (require "none" fields)
                ) }
        | NyasonValue.Record("match-result", _) ->
            let fields = recordFields "match-result" [ "id"; "input"; "ok"; "err" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.MatchResult(
                    decodeId<GraphNodeTag> "node" (require "input" fields),
                    decodeValueArm (require "ok" fields),
                    decodeErrorArm (require "err" fields)
                ) }
        | NyasonValue.Record(tag, _) -> shape $"Unsupported semantic node tag '{tag}'."
        | _ -> shape "Expected semantic node record."

    let private encodeFunction (functionEntity: SemanticFunction) =
        record
            "function"
            [ "id", encodeId "function" functionEntity.Id
              "display-name", text functionEntity.DisplayName
              "parameters", functionEntity.Parameters |> List.map encodeParameter |> list
              "result-type", encodeTypeRef functionEntity.ResultType
              "required-effects",
              functionEntity.RequiredEffects
              |> List.sortBy StableId.value
              |> List.map (encodeId "effect")
              |> list
              "body", encodeGraph functionEntity.Body ]

    let private decodeFunction value: SemanticFunction =
        let fields =
            recordFields
                "function"
                [ "id"; "display-name"; "parameters"; "result-type"; "required-effects"; "body" ]
                []
                value

        { Id = decodeId<FunctionTag> "function" (require "id" fields)
          DisplayName = decodeText (require "display-name" fields)
          Parameters = decodeList decodeParameter (require "parameters" fields)
          ResultType = decodeTypeRef (require "result-type" fields)
          RequiredEffects = decodeList (decodeId<EffectTag> "effect") (require "required-effects" fields)
          Body = decodeGraph (require "body" fields) }

    let private encodeObligation (obligation: MissingArgumentObligation) =
        match obligation.Status with
        | ObligationStatus.Open ->
            record
                "missing-argument-open"
                [ "id", encodeId "obligation" obligation.Id
                  "origin-transaction", encodeId "transaction" obligation.OriginTransactionId
                  "call", encodeId "node" obligation.CallNodeId
                  "parameter", encodeId "parameter" obligation.ParameterId ]
        | ObligationStatus.Resolved resolution ->
            record
                "missing-argument-resolved"
                [ "id", encodeId "obligation" obligation.Id
                  "origin-transaction", encodeId "transaction" obligation.OriginTransactionId
                  "call", encodeId "node" obligation.CallNodeId
                  "parameter", encodeId "parameter" obligation.ParameterId
                  "resolution-transaction", encodeId "transaction" resolution.TransactionId
                  "resolution-value", encodeId "node" resolution.ValueNodeId ]

    let private decodeObligation value: MissingArgumentObligation =
        match value with
        | NyasonValue.Record("missing-argument-open", _) ->
            let fields =
                recordFields
                    "missing-argument-open"
                    [ "id"; "origin-transaction"; "call"; "parameter" ]
                    []
                    value
            { Id = decodeId<ObligationTag> "obligation" (require "id" fields)
              OriginTransactionId = decodeId<TransactionTag> "transaction" (require "origin-transaction" fields)
              CallNodeId = decodeId<GraphNodeTag> "node" (require "call" fields)
              ParameterId = decodeId<ParameterTag> "parameter" (require "parameter" fields)
              Status = ObligationStatus.Open }
        | NyasonValue.Record("missing-argument-resolved", _) ->
            let fields =
                recordFields
                    "missing-argument-resolved"
                    [ "id"; "origin-transaction"; "call"; "parameter"; "resolution-transaction"; "resolution-value" ]
                    []
                    value
            { Id = decodeId<ObligationTag> "obligation" (require "id" fields)
              OriginTransactionId = decodeId<TransactionTag> "transaction" (require "origin-transaction" fields)
              CallNodeId = decodeId<GraphNodeTag> "node" (require "call" fields)
              ParameterId = decodeId<ParameterTag> "parameter" (require "parameter" fields)
              Status =
                ObligationStatus.Resolved
                    { TransactionId = decodeId<TransactionTag> "transaction" (require "resolution-transaction" fields)
                      ValueNodeId = decodeId<GraphNodeTag> "node" (require "resolution-value" fields) } }
        | NyasonValue.Record(tag, _) -> shape $"Unsupported obligation tag '{tag}'."
        | _ -> shape "Expected obligation record."

    let private encodeSnapshot (snapshot: Snapshot) =
        let parentFields =
            match snapshot.ParentId with
            | Some parentId -> [ "parent", encodeId "snapshot" parentId ]
            | None -> []

        let collectionFields =
            [ "types", snapshot.Types |> List.sortBy (fun item -> StableId.value item.Id) |> List.map encodeType |> list
              "effects", snapshot.Effects |> List.sortBy (fun item -> StableId.value item.Id) |> List.map encodeEffect |> list
              "capabilities",
              snapshot.Capabilities
              |> List.sortBy (fun item -> StableId.value item.Id)
              |> List.map encodeCapability
              |> list
              "functions",
              snapshot.Functions
              |> List.sortBy (fun item -> StableId.value item.Id)
              |> List.map encodeFunction
              |> list
              "obligations",
              snapshot.Obligations
              |> List.sortBy (fun item -> StableId.value item.Id)
              |> List.map encodeObligation
              |> list ]

        record "snapshot" ([ "id", encodeId "snapshot" snapshot.Id ] @ parentFields @ collectionFields)

    let private decodeSnapshot value: Snapshot =
        let fields =
            recordFields
                "snapshot"
                [ "id"; "types"; "effects"; "capabilities"; "functions"; "obligations" ]
                [ "parent" ]
                value

        { Id = decodeId<SnapshotTag> "snapshot" (require "id" fields)
          ParentId = optional "parent" fields |> Option.map (decodeId<SnapshotTag> "snapshot")
          Types = decodeList decodeType (require "types" fields)
          Effects = decodeList decodeEffect (require "effects" fields)
          Capabilities = decodeList decodeCapability (require "capabilities" fields)
          Functions = decodeList decodeFunction (require "functions" fields)
          Obligations = decodeList decodeObligation (require "obligations" fields) }

    let serialize (snapshot: Snapshot): Result<string, NyasonCodecError> =
        match SnapshotValidator.validate snapshot with
        | issue :: rest -> Error(NyasonCodecError.InvalidSnapshot(issue :: rest))
        | [] ->
            let root =
                record
                    "nyason"
                    [ "schema", text (if TaskTimeLimits.isUsed snapshot then timedWaitSchemaId elif FunctionAbort.isUsed snapshot then abortSchemaId elif CapabilityImplementations.isUsed snapshot then capabilitySchemaId else schemaId)
                      "snapshot", encodeSnapshot snapshot ]
            Ok(NyasonSyntax.render root)

    let deserialize (source: string): Result<Snapshot, NyasonCodecError> =
        match NyasonSyntax.parse source with
        | Error error -> Error(NyasonCodecError.InvalidSyntax error)
        | Ok root ->
            try
                let fields = recordFields "nyason" [ "schema"; "snapshot" ] [] root
                let actualSchema = decodeText (require "schema" fields)
                if actualSchema <> schemaId && actualSchema <> capabilitySchemaId && actualSchema <> abortSchemaId && actualSchema <> timedWaitSchemaId then
                    raise (NyasonSchemaException actualSchema)

                let snapshot = decodeSnapshot (require "snapshot" fields)
                if actualSchema <> abortSchemaId && actualSchema <> timedWaitSchemaId && FunctionAbort.isUsed snapshot then
                    raise (NyasonShapeException "Function abort requires nayamnyam.snapshot.v7.")
                if actualSchema <> timedWaitSchemaId && TaskTimeLimits.isUsed snapshot then
                    raise (NyasonShapeException "Timed TaskWait requires nayamnyam.snapshot.v8.")
                if actualSchema = schemaId && CapabilityImplementations.isUsed snapshot then
                    raise (NyasonShapeException "Capability construction requires nayamnyam.snapshot.v6.")
                match SnapshotValidator.validate snapshot with
                | [] -> Ok snapshot
                | issues -> Error(NyasonCodecError.InvalidSnapshot issues)
            with
            | NyasonSchemaException actual -> Error(NyasonCodecError.UnsupportedSchema actual)
            | NyasonShapeException message -> Error(NyasonCodecError.InvalidShape message)
            | :? ArgumentException as error -> Error(NyasonCodecError.InvalidShape error.Message)
