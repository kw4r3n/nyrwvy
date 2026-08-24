namespace Nayamnyam.Core

open System

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
    let schemaId = "nayamnyam.snapshot.v0"

    let private shape message = raise (NyasonShapeException message)

    let private record tag fields = NyasonValue.Record(tag, fields)
    let private list values = NyasonValue.List values
    let private text value = NyasonValue.Text value

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

    let private decodeList decoder value =
        match value with
        | NyasonValue.List values -> values |> List.map decoder
        | _ -> shape "Expected list value."

    let rec private encodeTypeRef typeRef =
        match typeRef with
        | TypeRef.Primitive PrimitiveType.String -> record "primitive-string" []
        | TypeRef.Primitive PrimitiveType.Bool -> record "primitive-bool" []
        | TypeRef.Primitive PrimitiveType.Unit -> record "primitive-unit" []
        | TypeRef.Named typeId -> record "named-type" [ "id", encodeId "type" typeId ]
        | TypeRef.Capability capabilityId ->
            record "capability-type" [ "capability", encodeId "capability" capabilityId ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ]) ->
            record "option-type" [ "item", encodeTypeRef itemType ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
            record "result-type" [ "ok", encodeTypeRef okType; "error", encodeTypeRef errorType ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, arguments) ->
            invalidOp $"Validated Option type unexpectedly has {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, arguments) ->
            invalidOp $"Validated Result type unexpectedly has {arguments.Length} type arguments."

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
        | SemanticNodeKind.StringIsEmpty valueNodeId ->
            record "string-is-empty" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringContains(valueNodeId, substringNodeId) ->
            record
                "string-contains"
                [ nodeId
                  "value", encodeId "node" valueNodeId
                  "substring", encodeId "node" substringNodeId ]
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
        | SemanticNodeKind.ResultErr(okType, errorNodeId) ->
            record
                "result-err"
                [ nodeId; "ok-type", encodeTypeRef okType; "error", encodeId "node" errorNodeId ]
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
        | NyasonValue.Record("string-is-empty", _) ->
            let fields = recordFields "string-is-empty" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.StringIsEmpty(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("string-contains", _) ->
            let fields = recordFields "string-contains" [ "id"; "value"; "substring" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.StringContains(
                    decodeId<GraphNodeTag> "node" (require "value" fields),
                    decodeId<GraphNodeTag> "node" (require "substring" fields)
                ) }
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
        | NyasonValue.Record("result-err", _) ->
            let fields = recordFields "result-err" [ "id"; "ok-type"; "error" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.ResultErr(
                    decodeTypeRef (require "ok-type" fields),
                    decodeId<GraphNodeTag> "node" (require "error" fields)
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
                    [ "schema", text schemaId
                      "snapshot", encodeSnapshot snapshot ]
            Ok(NyasonSyntax.render root)

    let deserialize (source: string): Result<Snapshot, NyasonCodecError> =
        match NyasonSyntax.parse source with
        | Error error -> Error(NyasonCodecError.InvalidSyntax error)
        | Ok root ->
            try
                let fields = recordFields "nyason" [ "schema"; "snapshot" ] [] root
                let actualSchema = decodeText (require "schema" fields)
                if actualSchema <> schemaId then
                    raise (NyasonSchemaException actualSchema)

                let snapshot = decodeSnapshot (require "snapshot" fields)
                match SnapshotValidator.validate snapshot with
                | [] -> Ok snapshot
                | issues -> Error(NyasonCodecError.InvalidSnapshot issues)
            with
            | NyasonSchemaException actual -> Error(NyasonCodecError.UnsupportedSchema actual)
            | NyasonShapeException message -> Error(NyasonCodecError.InvalidShape message)
            | :? ArgumentException as error -> Error(NyasonCodecError.InvalidShape error.Message)
