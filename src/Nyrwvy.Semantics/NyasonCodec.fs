namespace Nyrwvy.Core

open System
open System.Globalization

[<RequireQualifiedAccess>]
type NyasonCodecError =
    | InvalidSyntax of NyasonSyntaxError
    | UnsupportedSchema of string
    | InvalidShape of string
    | InvalidSnapshot of ValidationIssue list
    /// Encoding would nest deeper than `NyasonSyntax.maxDepth` (the payload).
    | MaximumDepthExceeded of int

exception private NyasonShapeException of string
exception private NyasonSchemaException of string

[<RequireQualifiedAccess>]
module NyasonCodec =
    /// The schema of graphs that need no newer construct; `SnapshotSchema` lists the others.
    let schemaId = SnapshotSchema.baseline.Nyason

    let private shape message = raise (NyasonShapeException message)

    /// Before `nayamnyam.snapshot.v17` a type parameter occurrence carried its name and a function
    /// listed no type parameters (the parameter types implied the list). The form is fixed once per
    /// document; see the same device in `SnapshotCodec`.
    let private legacyWriteNames = new System.Threading.ThreadLocal<Map<TypeParameterId, string> option>(fun () -> None)
    let private legacyReadNames = new System.Threading.ThreadLocal<System.Collections.Generic.Dictionary<TypeParameterId, string> option>(fun () -> None)

    let private withLegacyWriteNames (names: Map<TypeParameterId, string> option) (action: unit -> 'T) =
        let previous = legacyWriteNames.Value
        legacyWriteNames.Value <- names
        try action () finally legacyWriteNames.Value <- previous

    let private withLegacyReadNames (names: System.Collections.Generic.Dictionary<TypeParameterId, string> option) (action: unit -> 'T) =
        let previous = legacyReadNames.Value
        legacyReadNames.Value <- names
        try action () finally legacyReadNames.Value <- previous

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
        | TypeRef.Named(typeId, arguments) ->
            record "named-type" ([ "id", encodeId "type" typeId ] @ encodeTypeArguments "arguments" arguments)
        | TypeRef.TypeParameter typeParameterId ->
            match legacyWriteNames.Value with
            | Some names -> record "type-parameter" [ "id", encodeId "type-parameter" typeParameterId; "name", text (TypeParameters.nameIn names typeParameterId) ]
            | None -> record "type-parameter" [ "id", encodeId "type-parameter" typeParameterId ]
        | TypeRef.Capability capabilityId ->
            record "capability-type" [ "capability", encodeId "capability" capabilityId ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ]) ->
            record "option-type" [ "item", encodeTypeRef itemType ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
            record "result-type" [ "ok", encodeTypeRef okType; "error", encodeTypeRef errorType ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ keyType; valueType ]) ->
            record "map-type" [ "key", encodeTypeRef keyType; "value", encodeTypeRef valueType ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ]) ->
            record "list-type" [ "item", encodeTypeRef itemType ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ itemType ]) ->
            record "set-type" [ "item", encodeTypeRef itemType ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, [ resultType ]) ->
            record "one-shot-type" [ "value", encodeTypeRef resultType ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ resultType ]) ->
            record "task-type" [ "result", encodeTypeRef resultType ]
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Exception, []) -> record "exception-type" []
        | TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, []) -> record "task-scope-type" []
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Option, arguments) ->
            invalidOp $"Validated Option type unexpectedly has {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, arguments) ->
            invalidOp $"Validated Result type unexpectedly has {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Map, arguments) ->
            invalidOp $"Validated Map type unexpectedly has {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, arguments) ->
            invalidOp $"Validated List type unexpectedly has {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Set, arguments) ->
            invalidOp $"Validated Set type unexpectedly has {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, arguments) ->
            invalidOp $"Validated OneShot type unexpectedly has {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Task, arguments) ->
            invalidOp $"Validated Task type unexpectedly has {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.Exception, arguments) -> invalidOp $"Exception unexpectedly has {arguments.Length} type arguments."
        | TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, arguments) ->
            invalidOp $"Validated TaskScope type unexpectedly has {arguments.Length} type arguments."


    /// Type arguments of a named type or a construct node; absent when empty, so a monomorphic
    /// graph keeps its bytes.
    and private encodeTypeArguments name (arguments: TypeRef list) =
        if List.isEmpty arguments then [] else [ name, NyasonValue.List(List.map encodeTypeRef arguments) ]
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
            match legacyReadNames.Value with
            | Some names ->
                // The name is optional so that a newer document relabelled with an older schema
                // fails on the construct it needs (`firstUnsupported`), not on this shape.
                let fields = recordFields "type-parameter" [ "id" ] [ "name" ] value
                let typeParameterId = decodeId<TypeParameterTag> "type-parameter" (require "id" fields)
                match optional "name" fields with
                | Some name when not (names.ContainsKey typeParameterId) -> names.[typeParameterId] <- decodeText name
                | _ -> ()
                TypeRef.TypeParameter typeParameterId
            | None ->
                let fields = recordFields "type-parameter" [ "id" ] [] value
                TypeRef.TypeParameter(decodeId<TypeParameterTag> "type-parameter" (require "id" fields))
        | NyasonValue.Record("named-type", _) ->
            let fields = recordFields "named-type" [ "id" ] [ "arguments" ] value
            TypeRef.Named(decodeId<TypeTag> "type" (require "id" fields), decodeTypeArguments "arguments" fields)
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
        | NyasonValue.Record("map-type", _) ->
            let fields = recordFields "map-type" [ "key"; "value" ] [] value
            TypeRef.BuiltinGeneric(
                BuiltinGenericType.Map,
                [ decodeTypeRef (require "key" fields); decodeTypeRef (require "value" fields) ]
            )
        | NyasonValue.Record("list-type", _) ->
            let fields = recordFields "list-type" [ "item" ] [] value
            TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ decodeTypeRef (require "item" fields) ])
        | NyasonValue.Record("set-type", _) ->
            let fields = recordFields "set-type" [ "item" ] [] value
            TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ decodeTypeRef (require "item" fields) ])
        | NyasonValue.Record("one-shot-type", _) ->
            let fields = recordFields "one-shot-type" [ "value" ] [] value
            TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, [ decodeTypeRef (require "value" fields) ])
        | NyasonValue.Record("task-type", _) ->
            let fields = recordFields "task-type" [ "result" ] [] value
            TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ decodeTypeRef (require "result" fields) ])
        | NyasonValue.Record("exception-type", _) ->
            recordFields "exception-type" [] [] value |> ignore
            TypeRef.BuiltinGeneric(BuiltinGenericType.Exception, [])
        | NyasonValue.Record("task-scope-type", _) ->
            recordFields "task-scope-type" [] [] value |> ignore
            TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, [])
        | NyasonValue.Record(tag, _) -> shape $"Unsupported type reference tag '{tag}'."
        | _ -> shape "Expected tagged type reference."


    /// Optional type arguments; an empty list is not canonical, since the encoder omits it.
    and private decodeTypeArguments name (fields: Map<string, NyasonValue>) =
        match optional name fields with
        | None -> []
        | Some value ->
            match decodeList decodeTypeRef value with
            | [] -> shape $"Field '{name}' must be omitted rather than empty."
            | arguments -> arguments
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

    let private encodeTypeParameters (typeParameters: (TypeParameterId * string) list) =
        if List.isEmpty typeParameters then []
        else
            [ "type-parameters",
              typeParameters
              |> List.map (fun (typeParameterId, displayName) ->
                  record "type-parameter-declaration" [ "id", encodeId "type-parameter" typeParameterId; "name", text displayName ])
              |> list ]

    /// Declaration visibility (`DeclarationVisibility`): from `nayamnyam.snapshot.v17` on an
    /// exported declaration carries `exported true` and a private one omits the field; below that
    /// schema nothing is written and every declaration reads as exported, so older bytes are kept.
    let private visibilityFields (explicitVisibility: bool) (exported: bool) =
        if explicitVisibility && exported then [ "exported", NyasonValue.Boolean true ] else []

    let private visibilityOptional (explicitVisibility: bool) =
        if explicitVisibility then [ "exported" ] else []

    let private decodeVisibility (explicitVisibility: bool) (fields: Map<string, NyasonValue>) =
        if not explicitVisibility then true
        else
            match optional "exported" fields with
            | None -> false
            | Some value ->
                if not (decodeBoolean value) then shape "Field 'exported' must be true or omitted (a private declaration omits it)."
                true

    let private encodeType (explicitVisibility: bool) (item: SemanticType) =
        let visibility = visibilityFields explicitVisibility item.Exported
        // Declaration order is semantic (it is the order of type arguments); absent when empty.
        let typeParameters = encodeTypeParameters item.TypeParameters
        match item.Kind with
        | SemanticTypeKind.Product fields ->
            record
                "product-type"
                ([ "id", encodeId "type" item.Id
                   "display-name", text item.DisplayName ]
                 @ visibility
                 @ [ "fields", fields |> List.sortBy (fun field -> StableId.value field.Id) |> List.map encodeField |> list ]
                 @ typeParameters)
        | SemanticTypeKind.Union variants ->
            record
                "union-type"
                ([ "id", encodeId "type" item.Id
                   "display-name", text item.DisplayName ]
                 @ visibility
                 @ [ "variants", variants |> List.sortBy (fun variant -> StableId.value variant.Id) |> List.map encodeVariant |> list ]
                 @ typeParameters)

    let private decodeTypeParameters (fields: Map<string, NyasonValue>) =
        match optional "type-parameters" fields with
        | None -> []
        | Some value ->
            let decodeOne item =
                let parameter = recordFields "type-parameter-declaration" [ "id"; "name" ] [] item
                decodeId<TypeParameterTag> "type-parameter" (require "id" parameter), decodeText (require "name" parameter)
            match decodeList decodeOne value with
            | [] -> shape "Field 'type-parameters' must be omitted rather than empty."
            | parameters -> parameters

    let private decodeType (explicitVisibility: bool) value: SemanticType =
        let optionalFields = "type-parameters" :: visibilityOptional explicitVisibility
        match value with
        | NyasonValue.Record("product-type", _) ->
            let fields = recordFields "product-type" [ "id"; "display-name"; "fields" ] optionalFields value
            { Id = decodeId<TypeTag> "type" (require "id" fields)
              DisplayName = decodeText (require "display-name" fields)
              Exported = decodeVisibility explicitVisibility fields
              TypeParameters = decodeTypeParameters fields
              Kind = SemanticTypeKind.Product(decodeList decodeField (require "fields" fields)) }
        | NyasonValue.Record("union-type", _) ->
            let fields = recordFields "union-type" [ "id"; "display-name"; "variants" ] optionalFields value
            { Id = decodeId<TypeTag> "type" (require "id" fields)
              DisplayName = decodeText (require "display-name" fields)
              Exported = decodeVisibility explicitVisibility fields
              TypeParameters = decodeTypeParameters fields
              Kind = SemanticTypeKind.Union(decodeList decodeVariant (require "variants" fields)) }
        | NyasonValue.Record(tag, _) -> shape $"Unsupported semantic type tag '{tag}'."
        | _ -> shape "Expected semantic type record."

    let private encodeEffect (explicitVisibility: bool) (effect: SemanticEffect) =
        record "effect" ([ "id", encodeId "effect" effect.Id; "display-name", text effect.DisplayName ] @ visibilityFields explicitVisibility effect.Exported)

    let private decodeEffect (explicitVisibility: bool) value: SemanticEffect =
        let fields = recordFields "effect" [ "id"; "display-name" ] (visibilityOptional explicitVisibility) value
        { Id = decodeId<EffectTag> "effect" (require "id" fields)
          DisplayName = decodeText (require "display-name" fields)
          Exported = decodeVisibility explicitVisibility fields }

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

    let private encodeCapability (explicitVisibility: bool) (capability: SemanticCapability) =
        record
            "capability"
            ([ "id", encodeId "capability" capability.Id
               "display-name", text capability.DisplayName ]
             @ visibilityFields explicitVisibility capability.Exported
             @ [ "operations",
                 capability.Operations
                 |> List.sortBy (fun operation -> StableId.value operation.Id)
                 |> List.map encodeCapabilityOperation
                 |> list ])

    let private decodeCapability (explicitVisibility: bool) value: SemanticCapability =
        let fields = recordFields "capability" [ "id"; "display-name"; "operations" ] (visibilityOptional explicitVisibility) value
        { Id = decodeId<CapabilityTag> "capability" (require "id" fields)
          DisplayName = decodeText (require "display-name" fields)
          Exported = decodeVisibility explicitVisibility fields
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
            [ (match UnionMatchArm.canonicalVariantIds arm with
               | [ variantId ] -> "variant", encodeId "variant" variantId
               | variantIds -> "variants", variantIds |> List.map (encodeId "variant") |> list)
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

    and private encodeNamedCallbackContext name (context: ArgumentBinding list) =
        if List.isEmpty context then []
        else [ name, context |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId) |> List.map encodeArgument |> list ]

    and private encodeCallbackContext context = encodeNamedCallbackContext "context" context

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
        | SemanticNodeKind.Int64ToHexString valueNodeId ->
            record "int64-to-hex-string" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringParseInt64 valueNodeId ->
            record "string-parse-int64" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringParseInt64Hex valueNodeId ->
            record "string-parse-int64-hex" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.Float64ToString valueNodeId ->
            record "float64-to-string" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringParseFloat64 valueNodeId ->
            record "string-parse-float64" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringCodePoints valueNodeId ->
            record "string-code-points" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringToLower valueNodeId ->
            record "string-to-lower" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringToUpper valueNodeId ->
            record "string-to-upper" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringDecodeUtf8Lossy valueNodeId ->
            record "string-decode-utf8-lossy" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringEncodeUtf8 valueNodeId ->
            record "string-encode-utf8" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.Float64RoundToInt64 valueNodeId ->
            record "float64-round-to-int64" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.Float64FloorToInt64 valueNodeId ->
            record "float64-floor-to-int64" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.Float64Sqrt valueNodeId ->
            record "float64-sqrt" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.Float64Abs valueNodeId ->
            record "float64-abs" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.Float64Negate valueNodeId ->
            record "float64-negate" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.Float64Floor valueNodeId ->
            record "float64-floor" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.Float64Ceiling valueNodeId ->
            record "float64-ceiling" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.Float64Truncate valueNodeId ->
            record "float64-truncate" [ nodeId; "value", encodeId "node" valueNodeId ]
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
        | SemanticNodeKind.StringLastIndexOfBefore(valueNodeId, substringNodeId, endNodeId) ->
            record
                "string-last-index-of-before"
                [ nodeId
                  "value", encodeId "node" valueNodeId
                  "substring", encodeId "node" substringNodeId
                  "end", encodeId "node" endNodeId ]
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
        | SemanticNodeKind.StringReplace(valueNodeId, patternNodeId, replacementNodeId) ->
            record
                "string-replace"
                [ nodeId
                  "value", encodeId "node" valueNodeId
                  "pattern", encodeId "node" patternNodeId
                  "replacement", encodeId "node" replacementNodeId ]
        | SemanticNodeKind.StringTrim valueNodeId ->
            record "string-trim" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringTrimStart valueNodeId ->
            record "string-trim-start" [ nodeId; "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.StringTrimEnd valueNodeId ->
            record "string-trim-end" [ nodeId; "value", encodeId "node" valueNodeId ]
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
        | SemanticNodeKind.Int64Min(leftNodeId, rightNodeId) ->
            encodeBinaryNode "int64-min" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Int64Max(leftNodeId, rightNodeId) ->
            encodeBinaryNode "int64-max" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Int64BitAnd(leftNodeId, rightNodeId) ->
            encodeBinaryNode "int64-bit-and" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Int64BitOr(leftNodeId, rightNodeId) ->
            encodeBinaryNode "int64-bit-or" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Int64BitXor(leftNodeId, rightNodeId) ->
            encodeBinaryNode "int64-bit-xor" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Int64ShiftLeft(valueNodeId, countNodeId) ->
            record "int64-shift-left" [ nodeId; "value", encodeId "node" valueNodeId; "count", encodeId "node" countNodeId ]
        | SemanticNodeKind.Int64ShiftRight(valueNodeId, countNodeId) ->
            record "int64-shift-right" [ nodeId; "value", encodeId "node" valueNodeId; "count", encodeId "node" countNodeId ]
        | SemanticNodeKind.Int64CheckedAdd(leftNodeId, rightNodeId) ->
            encodeBinaryNode "int64-checked-add" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Int64CheckedSubtract(leftNodeId, rightNodeId) ->
            encodeBinaryNode "int64-checked-subtract" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Int64CheckedMultiply(leftNodeId, rightNodeId) ->
            encodeBinaryNode "int64-checked-multiply" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.ListRange(startNodeId, endNodeId) ->
            record "list-range" [ nodeId; "start", encodeId "node" startNodeId; "end", encodeId "node" endNodeId ]
        | SemanticNodeKind.StringRepeat(textNodeId, countNodeId) ->
            record "string-repeat" [ nodeId; "text", encodeId "node" textNodeId; "count", encodeId "node" countNodeId ]
        | SemanticNodeKind.Float64Round(valueNodeId, digitsNodeId) ->
            record "float64-round" [ nodeId; "value", encodeId "node" valueNodeId; "digits", encodeId "node" digitsNodeId ]
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
        | SemanticNodeKind.Float64Min(leftNodeId, rightNodeId) ->
            encodeBinaryNode "float64-min" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Float64Max(leftNodeId, rightNodeId) ->
            encodeBinaryNode "float64-max" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Float64Subtract(leftNodeId, rightNodeId) ->
            encodeBinaryNode "float64-subtract" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Float64Multiply(leftNodeId, rightNodeId) ->
            encodeBinaryNode "float64-multiply" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Float64Divide(leftNodeId, rightNodeId) ->
            encodeBinaryNode "float64-divide" nodeId leftNodeId rightNodeId
        | SemanticNodeKind.Float64FloorModulo(leftNodeId, rightNodeId) ->
            encodeBinaryNode "float64-floor-modulo" nodeId leftNodeId rightNodeId
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
        | SemanticNodeKind.Int64WrappingAbs valueNodeId ->
            encodeUnaryNode "int64-wrapping-abs" nodeId valueNodeId
        | SemanticNodeKind.Int64BitNot valueNodeId ->
            encodeUnaryNode "int64-bit-not" nodeId valueNodeId
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
        | SemanticNodeKind.SetEmpty itemType ->
            record "set-empty" [ nodeId; "item-type", encodeTypeRef itemType ]
        | SemanticNodeKind.MapEmpty(keyType, valueType) ->
            record "map-empty" [ nodeId; "key-type", encodeTypeRef keyType; "value-type", encodeTypeRef valueType ]
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
        | SemanticNodeKind.ListSetAt(listNodeId, indexNodeId, itemNodeId) ->
            record
                "list-set-at"
                [ nodeId
                  "list", encodeId "node" listNodeId
                  "index", encodeId "node" indexNodeId
                  "item", encodeId "node" itemNodeId ]
        | SemanticNodeKind.ListInsertAt(listNodeId, indexNodeId, itemNodeId) ->
            record
                "list-insert-at"
                [ nodeId
                  "list", encodeId "node" listNodeId
                  "index", encodeId "node" indexNodeId
                  "item", encodeId "node" itemNodeId ]
        | SemanticNodeKind.ListRemoveAt(listNodeId, indexNodeId) ->
            record
                "list-remove-at"
                [ nodeId
                  "list", encodeId "node" listNodeId
                  "index", encodeId "node" indexNodeId ]
        | SemanticNodeKind.MapInsert(mapNodeId, keyNodeId, valueNodeId) ->
            record
                "map-insert"
                [ nodeId
                  "map", encodeId "node" mapNodeId
                  "key", encodeId "node" keyNodeId
                  "value", encodeId "node" valueNodeId ]
        | SemanticNodeKind.ListLength listNodeId ->
            record "list-length" [ nodeId; "list", encodeId "node" listNodeId ]
        | SemanticNodeKind.MapValues mapNodeId ->
            record "map-values" [ nodeId; "map", encodeId "node" mapNodeId ]
        | SemanticNodeKind.MapKeys mapNodeId ->
            record "map-keys" [ nodeId; "map", encodeId "node" mapNodeId ]
        | SemanticNodeKind.SetToList mapNodeId ->
            record "set-to-list" [ nodeId; "set", encodeId "node" mapNodeId ]
        | SemanticNodeKind.MapSize mapNodeId ->
            record "map-size" [ nodeId; "map", encodeId "node" mapNodeId ]
        | SemanticNodeKind.SetFromList mapNodeId ->
            record "set-from-list" [ nodeId; "list", encodeId "node" mapNodeId ]
        | SemanticNodeKind.SetSize mapNodeId ->
            record "set-size" [ nodeId; "set", encodeId "node" mapNodeId ]
        | SemanticNodeKind.ListReverse listNodeId ->
            record "list-reverse" [ nodeId; "list", encodeId "node" listNodeId ]
        | SemanticNodeKind.ListLast listNodeId ->
            record "list-last" [ nodeId; "list", encodeId "node" listNodeId ]
        | SemanticNodeKind.ListDropLast listNodeId ->
            record "list-drop-last" [ nodeId; "list", encodeId "node" listNodeId ]
        | SemanticNodeKind.ListItemAt(listNodeId, indexNodeId) ->
            record
                "list-item-at"
                [ nodeId
                  "list", encodeId "node" listNodeId
                  "index", encodeId "node" indexNodeId ]
        | SemanticNodeKind.MapRemove(mapNodeId, keyNodeId) ->
            record
                "map-remove"
                [ nodeId
                  "map", encodeId "node" mapNodeId
                  "key", encodeId "node" keyNodeId ]
        | SemanticNodeKind.SetInsert(mapNodeId, itemNodeId) ->
            record
                "set-insert"
                [ nodeId
                  "set", encodeId "node" mapNodeId
                  "item", encodeId "node" itemNodeId ]
        | SemanticNodeKind.SetRemove(mapNodeId, itemNodeId) ->
            record
                "set-remove"
                [ nodeId
                  "set", encodeId "node" mapNodeId
                  "item", encodeId "node" itemNodeId ]
        | SemanticNodeKind.SetUnion(leftSetNodeId, rightSetNodeId) ->
            record "set-union" [ nodeId; "left", encodeId "node" leftSetNodeId; "right", encodeId "node" rightSetNodeId ]
        | SemanticNodeKind.SetIntersection(leftSetNodeId, rightSetNodeId) ->
            record "set-intersection" [ nodeId; "left", encodeId "node" leftSetNodeId; "right", encodeId "node" rightSetNodeId ]
        | SemanticNodeKind.SetDifference(leftSetNodeId, rightSetNodeId) ->
            record "set-difference" [ nodeId; "left", encodeId "node" leftSetNodeId; "right", encodeId "node" rightSetNodeId ]
        | SemanticNodeKind.SetIsSubset(leftSetNodeId, rightSetNodeId) ->
            record "set-is-subset" [ nodeId; "left", encodeId "node" leftSetNodeId; "right", encodeId "node" rightSetNodeId ]
        | SemanticNodeKind.MapUnion(leftMapNodeId, rightMapNodeId) ->
            record "map-union" [ nodeId; "left", encodeId "node" leftMapNodeId; "right", encodeId "node" rightMapNodeId ]
        | SemanticNodeKind.MapContainsKey(mapNodeId, keyNodeId) ->
            record
                "map-contains-key"
                [ nodeId
                  "map", encodeId "node" mapNodeId
                  "key", encodeId "node" keyNodeId ]
        | SemanticNodeKind.SetContains(mapNodeId, itemNodeId) ->
            record
                "set-contains"
                [ nodeId
                  "set", encodeId "node" mapNodeId
                  "item", encodeId "node" itemNodeId ]
        | SemanticNodeKind.ListContains(listNodeId, itemNodeId) ->
            record "list-contains" [ nodeId; "list", encodeId "node" listNodeId; "item", encodeId "node" itemNodeId ]
        | SemanticNodeKind.ListIndexOf(mapNodeId, itemNodeId) ->
            record
                "list-index-of"
                [ nodeId
                  "list", encodeId "node" mapNodeId
                  "item", encodeId "node" itemNodeId ]
        | SemanticNodeKind.ListLastIndexOf(mapNodeId, itemNodeId) ->
            record
                "list-last-index-of"
                [ nodeId
                  "list", encodeId "node" mapNodeId
                  "item", encodeId "node" itemNodeId ]
        | SemanticNodeKind.MapGet(mapNodeId, keyNodeId) ->
            record
                "map-get"
                [ nodeId
                  "map", encodeId "node" mapNodeId
                  "key", encodeId "node" keyNodeId ]
        | SemanticNodeKind.MapGetOrDefault(mapNodeId, keyNodeId, defaultNodeId) ->
            record
                "map-get-or-default"
                [ nodeId
                  "map", encodeId "node" mapNodeId
                  "key", encodeId "node" keyNodeId
                  "default", encodeId "node" defaultNodeId ]
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
        | SemanticNodeKind.MapFold(sourceMapNodeId, seedNodeId, folderFunctionId, context) ->
            record "map-fold" ([ nodeId; "map", encodeId "node" sourceMapNodeId; "seed", encodeId "node" seedNodeId; "folder", encodeId "function" folderFunctionId ] @ encodeCallbackContext context)
        | SemanticNodeKind.SetFold(sourceSetNodeId, seedNodeId, folderFunctionId, context) ->
            record "set-fold" ([ nodeId; "set", encodeId "node" sourceSetNodeId; "seed", encodeId "node" seedNodeId; "folder", encodeId "function" folderFunctionId ] @ encodeCallbackContext context)
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
        | SemanticNodeKind.ExceptionCatch(body, bodyContext, handler, handlerContext) ->
            record "exception-catch" ([ nodeId; "body", encodeId "function" body; "handler", encodeId "function" handler ] @ encodeNamedCallbackContext "body-context" bodyContext @ encodeNamedCallbackContext "handler-context" handlerContext)
        | SemanticNodeKind.ExceptionIsCancellation value -> encodeUnaryNode "exception-is-cancellation" nodeId value
        | SemanticNodeKind.ExceptionMessage value -> encodeUnaryNode "exception-message" nodeId value
        | SemanticNodeKind.ExceptionRethrow(value, resultType) -> record "exception-rethrow" [ nodeId; "value", encodeId "node" value; "result-type", encodeTypeRef resultType ]
        | SemanticNodeKind.CancellationShield(bodyFunctionId, context) ->
            record "cancellation-shield" ([ nodeId; "body", encodeId "function" bodyFunctionId ] @ encodeCallbackContext context)
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
        | SemanticNodeKind.OneShotCreate(scope, valueType) -> record "one-shot-create" [ nodeId; "scope", encodeId "node" scope; "value-type", encodeTypeRef valueType ]
        | SemanticNodeKind.OneShotComplete(cell, value) -> record "one-shot-complete" [ nodeId; "cell", encodeId "node" cell; "value", encodeId "node" value ]
        | SemanticNodeKind.OneShotWait(cell, limit) -> record "one-shot-wait" ([ nodeId; "cell", encodeId "node" cell ] @ (limit |> Option.map (fun item -> "limit", encodeId "node" item) |> Option.toList))
        | SemanticNodeKind.TaskCancel taskNodeId -> record "task-cancel" [ nodeId; "task", encodeId "node" taskNodeId ]
        | SemanticNodeKind.ListMap(sourceListNodeId, mapperFunctionId, context) ->
            encodeListCallbackNode "list-map" nodeId sourceListNodeId mapperFunctionId context
        | SemanticNodeKind.ListZipWith(leftListNodeId, rightListNodeId, combinerFunctionId, context) ->
            record "list-zip-with" ([ nodeId; "left", encodeId "node" leftListNodeId; "right", encodeId "node" rightListNodeId; "combiner", encodeId "function" combinerFunctionId ] @ encodeCallbackContext context)
        | SemanticNodeKind.ListFlatMap(sourceListNodeId, mapperFunctionId, context) ->
            encodeListCallbackNode "list-flat-map" nodeId sourceListNodeId mapperFunctionId context
        | SemanticNodeKind.MapFromList(sourceListNodeId, keyOfFunctionId, context) ->
            encodeListCallbackNode "map-from-list" nodeId sourceListNodeId keyOfFunctionId context
        | SemanticNodeKind.ListFilter(sourceListNodeId, predicateFunctionId, context) ->
            encodeListCallbackNode "list-filter" nodeId sourceListNodeId predicateFunctionId context
        | SemanticNodeKind.MapFilter(sourceMapNodeId, keepFunctionId, context) ->
            record "map-filter" ([ nodeId; "map", encodeId "node" sourceMapNodeId; "keep", encodeId "function" keepFunctionId ] @ encodeCallbackContext context)
        | SemanticNodeKind.SetFilter(sourceSetNodeId, predicateFunctionId, context) ->
            record "set-filter" ([ nodeId; "set", encodeId "node" sourceSetNodeId; "predicate", encodeId "function" predicateFunctionId ] @ encodeCallbackContext context)
        | SemanticNodeKind.OptionGetOrDefault(optionNodeId, defaultNodeId) ->
            record "option-get-or-default" [ nodeId; "option", encodeId "node" optionNodeId; "default", encodeId "node" defaultNodeId ]
        | SemanticNodeKind.ResultGetOrDefault(resultNodeId, defaultNodeId) ->
            record "result-get-or-default" [ nodeId; "result", encodeId "node" resultNodeId; "default", encodeId "node" defaultNodeId ]
        | SemanticNodeKind.OptionOkOr(optionNodeId, errorNodeId) ->
            record "option-ok-or" [ nodeId; "option", encodeId "node" optionNodeId; "error", encodeId "node" errorNodeId ]
        | SemanticNodeKind.ResultOkValue resultNodeId ->
            record "result-ok-value" [ nodeId; "value", encodeId "node" resultNodeId ]
        | SemanticNodeKind.ResultErrValue resultNodeId ->
            record "result-err-value" [ nodeId; "value", encodeId "node" resultNodeId ]
        | SemanticNodeKind.OptionIsSome optionNodeId ->
            record "option-is-some" [ nodeId; "value", encodeId "node" optionNodeId ]
        | SemanticNodeKind.ResultIsOk resultNodeId ->
            record "result-is-ok" [ nodeId; "value", encodeId "node" resultNodeId ]
        | SemanticNodeKind.OptionMap(sourceNodeId, functionId, context) ->
            record "option-map" ([ nodeId; "option", encodeId "node" sourceNodeId; "mapper", encodeId "function" functionId ] @ encodeCallbackContext context)
        | SemanticNodeKind.OptionAndThen(sourceNodeId, functionId, context) ->
            record "option-and-then" ([ nodeId; "option", encodeId "node" sourceNodeId; "binder", encodeId "function" functionId ] @ encodeCallbackContext context)
        | SemanticNodeKind.ResultMap(sourceNodeId, functionId, context) ->
            record "result-map" ([ nodeId; "result", encodeId "node" sourceNodeId; "mapper", encodeId "function" functionId ] @ encodeCallbackContext context)
        | SemanticNodeKind.ResultMapErr(sourceNodeId, functionId, context) ->
            record "result-map-err" ([ nodeId; "result", encodeId "node" sourceNodeId; "mapper", encodeId "function" functionId ] @ encodeCallbackContext context)
        | SemanticNodeKind.ResultAndThen(sourceNodeId, functionId, context) ->
            record "result-and-then" ([ nodeId; "result", encodeId "node" sourceNodeId; "binder", encodeId "function" functionId ] @ encodeCallbackContext context)
        | SemanticNodeKind.MapMapValues(sourceMapNodeId, mapperFunctionId, context) ->
            record "map-map-values" ([ nodeId; "map", encodeId "node" sourceMapNodeId; "mapper", encodeId "function" mapperFunctionId ] @ encodeCallbackContext context)
        | SemanticNodeKind.SetMap(sourceSetNodeId, mapperFunctionId, context) ->
            record "set-map" ([ nodeId; "set", encodeId "node" sourceSetNodeId; "mapper", encodeId "function" mapperFunctionId ] @ encodeCallbackContext context)
        | SemanticNodeKind.ListSortBy(sourceListNodeId, lessThanFunctionId, context) ->
            encodeListCallbackNode "list-sort-by" nodeId sourceListNodeId lessThanFunctionId context
        | SemanticNodeKind.ListMinBy(sourceListNodeId, lessThanFunctionId, context) ->
            encodeListCallbackNode "list-min-by" nodeId sourceListNodeId lessThanFunctionId context
        | SemanticNodeKind.ListMaxBy(sourceListNodeId, lessThanFunctionId, context) ->
            encodeListCallbackNode "list-max-by" nodeId sourceListNodeId lessThanFunctionId context
        | SemanticNodeKind.ListAny(sourceListNodeId, predicateFunctionId, context) ->
            encodeListCallbackNode "list-any" nodeId sourceListNodeId predicateFunctionId context
        | SemanticNodeKind.ListFindIndex(sourceListNodeId, predicateFunctionId, context) ->
            encodeListCallbackNode "list-find-index" nodeId sourceListNodeId predicateFunctionId context
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
        | SemanticNodeKind.ProductConstruct(typeId, typeArguments, fields) ->
            record
                "product-construct"
                ([ nodeId
                   "type", encodeId "type" typeId
                   "fields",
                   fields
                   |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
                   |> List.map encodeFieldValue
                   |> list ]
                 @ encodeTypeArguments "type-arguments" typeArguments)
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
        | SemanticNodeKind.UnionConstruct(variantId, typeArguments, fields) ->
            record
                "union-construct"
                ([ nodeId
                   "variant", encodeId "variant" variantId
                   "fields",
                   fields
                   |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
                   |> List.map encodeFieldValue
                   |> list ]
                 @ encodeTypeArguments "type-arguments" typeArguments)
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
                  |> List.sortBy (fun arm -> UnionMatchArm.key arm)
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
        let fields = recordFields "union-arm" [ "fields"; "body" ] [ "variant"; "variants" ] value
        let variantIds =
            match optional "variant" fields, optional "variants" fields with
            | Some variant, None -> [ decodeId<VariantTag> "variant" variant ]
            | None, Some variants ->
                match decodeList (decodeId<VariantTag> "variant") variants with
                | _ :: _ :: _ as variantIds -> variantIds
                | _ -> shape "Record 'union-arm' field 'variants' must name at least two variants; one variant uses 'variant'."
            | _ -> shape "Record 'union-arm' requires exactly one of the fields 'variant' and 'variants'."
        { VariantIds = variantIds
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

    and private decodeNamedCallbackContext name fields =
        match optional name fields with
        | None -> []
        | Some value ->
            match decodeList decodeArgument value with
            | [] -> shape "Callback context must be omitted rather than empty."
            | context -> context

    and private decodeCallbackContext fields = decodeNamedCallbackContext "context" fields

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
        | NyasonValue.Record("int64-to-hex-string", _) ->
            let fields = recordFields "int64-to-hex-string" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.Int64ToHexString(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("string-parse-int64", _) ->
            let fields = recordFields "string-parse-int64" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.StringParseInt64(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("string-parse-int64-hex", _) ->
            let fields = recordFields "string-parse-int64-hex" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.StringParseInt64Hex(decodeId<GraphNodeTag> "node" (require "value" fields)) }
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
        | NyasonValue.Record("string-to-upper", _) ->
            let fields = recordFields "string-to-upper" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.StringToUpper(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("string-decode-utf8-lossy", _) ->
            let fields = recordFields "string-decode-utf8-lossy" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.StringDecodeUtf8Lossy(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("string-encode-utf8", _) ->
            let fields = recordFields "string-encode-utf8" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.StringEncodeUtf8(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("float64-round-to-int64", _) ->
            let fields = recordFields "float64-round-to-int64" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.Float64RoundToInt64(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("float64-floor-to-int64", _) ->
            let fields = recordFields "float64-floor-to-int64" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.Float64FloorToInt64(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("float64-sqrt", _) ->
            let fields = recordFields "float64-sqrt" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.Float64Sqrt(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("float64-abs", _) ->
            let fields = recordFields "float64-abs" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.Float64Abs(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("float64-negate", _) ->
            let fields = recordFields "float64-negate" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.Float64Negate(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("float64-floor", _) ->
            let fields = recordFields "float64-floor" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.Float64Floor(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("float64-ceiling", _) ->
            let fields = recordFields "float64-ceiling" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.Float64Ceiling(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("float64-truncate", _) ->
            let fields = recordFields "float64-truncate" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.Float64Truncate(decodeId<GraphNodeTag> "node" (require "value" fields)) }
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
        | NyasonValue.Record("string-replace", _) ->
            let fields = recordFields "string-replace" [ "id"; "value"; "pattern"; "replacement" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.StringReplace(
                    decodeId<GraphNodeTag> "node" (require "value" fields),
                    decodeId<GraphNodeTag> "node" (require "pattern" fields),
                    decodeId<GraphNodeTag> "node" (require "replacement" fields)
                ) }
        | NyasonValue.Record(("string-trim" | "string-trim-start" | "string-trim-end") as tag, _) ->
            let build =
                match tag with
                | "string-trim-start" -> SemanticNodeKind.StringTrimStart
                | "string-trim-end" -> SemanticNodeKind.StringTrimEnd
                | _ -> SemanticNodeKind.StringTrim
            let fields = recordFields tag [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = build (decodeId<GraphNodeTag> "node" (require "value" fields)) }
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
        | NyasonValue.Record("string-last-index-of-before", _) ->
            let fields = recordFields "string-last-index-of-before" [ "id"; "value"; "substring"; "end" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.StringLastIndexOfBefore(
                    decodeId<GraphNodeTag> "node" (require "value" fields),
                    decodeId<GraphNodeTag> "node" (require "substring" fields),
                    decodeId<GraphNodeTag> "node" (require "end" fields)
                ) }
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
        | NyasonValue.Record("int64-min", _) ->
            decodeBinaryNode "int64-min" value (fun left right -> SemanticNodeKind.Int64Min(left, right))
        | NyasonValue.Record("int64-max", _) ->
            decodeBinaryNode "int64-max" value (fun left right -> SemanticNodeKind.Int64Max(left, right))
        | NyasonValue.Record("int64-bit-and", _) ->
            decodeBinaryNode "int64-bit-and" value (fun left right -> SemanticNodeKind.Int64BitAnd(left, right))
        | NyasonValue.Record("int64-bit-or", _) ->
            decodeBinaryNode "int64-bit-or" value (fun left right -> SemanticNodeKind.Int64BitOr(left, right))
        | NyasonValue.Record("int64-bit-xor", _) ->
            decodeBinaryNode "int64-bit-xor" value (fun left right -> SemanticNodeKind.Int64BitXor(left, right))
        | NyasonValue.Record("int64-shift-left", _) ->
            let fields = recordFields "int64-shift-left" [ "id"; "value"; "count" ] [] value

            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.Int64ShiftLeft(
                    decodeId<GraphNodeTag> "node" (require "value" fields),
                    decodeId<GraphNodeTag> "node" (require "count" fields)
                ) }
        | NyasonValue.Record("int64-shift-right", _) ->
            let fields = recordFields "int64-shift-right" [ "id"; "value"; "count" ] [] value

            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.Int64ShiftRight(
                    decodeId<GraphNodeTag> "node" (require "value" fields),
                    decodeId<GraphNodeTag> "node" (require "count" fields)
                ) }
        | NyasonValue.Record("int64-checked-add", _) ->
            decodeBinaryNode "int64-checked-add" value (fun left right -> SemanticNodeKind.Int64CheckedAdd(left, right))
        | NyasonValue.Record("int64-checked-subtract", _) ->
            decodeBinaryNode "int64-checked-subtract" value (fun left right -> SemanticNodeKind.Int64CheckedSubtract(left, right))
        | NyasonValue.Record("int64-checked-multiply", _) ->
            decodeBinaryNode "int64-checked-multiply" value (fun left right -> SemanticNodeKind.Int64CheckedMultiply(left, right))
        | NyasonValue.Record("list-range", _) ->
            let fields = recordFields "list-range" [ "id"; "start"; "end" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.ListRange(
                    decodeId<GraphNodeTag> "node" (require "start" fields),
                    decodeId<GraphNodeTag> "node" (require "end" fields)
                ) }
        | NyasonValue.Record("string-repeat", _) ->
            let fields = recordFields "string-repeat" [ "id"; "text"; "count" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.StringRepeat(
                    decodeId<GraphNodeTag> "node" (require "text" fields),
                    decodeId<GraphNodeTag> "node" (require "count" fields)
                ) }
        | NyasonValue.Record("float64-round", _) ->
            let fields = recordFields "float64-round" [ "id"; "value"; "digits" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.Float64Round(
                    decodeId<GraphNodeTag> "node" (require "value" fields),
                    decodeId<GraphNodeTag> "node" (require "digits" fields)
                ) }
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
        | NyasonValue.Record("float64-min", _) ->
            decodeBinaryNode "float64-min" value (fun left right -> SemanticNodeKind.Float64Min(left, right))
        | NyasonValue.Record("float64-max", _) ->
            decodeBinaryNode "float64-max" value (fun left right -> SemanticNodeKind.Float64Max(left, right))
        | NyasonValue.Record("float64-subtract", _) ->
            decodeBinaryNode "float64-subtract" value (fun left right -> SemanticNodeKind.Float64Subtract(left, right))
        | NyasonValue.Record("float64-multiply", _) ->
            decodeBinaryNode "float64-multiply" value (fun left right -> SemanticNodeKind.Float64Multiply(left, right))
        | NyasonValue.Record("float64-divide", _) ->
            decodeBinaryNode "float64-divide" value (fun left right -> SemanticNodeKind.Float64Divide(left, right))
        | NyasonValue.Record("float64-floor-modulo", _) ->
            decodeBinaryNode "float64-floor-modulo" value (fun left right -> SemanticNodeKind.Float64FloorModulo(left, right))
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
        | NyasonValue.Record("int64-wrapping-abs", _) ->
            decodeUnaryNode "int64-wrapping-abs" value SemanticNodeKind.Int64WrappingAbs
        | NyasonValue.Record("int64-bit-not", _) ->
            decodeUnaryNode "int64-bit-not" value SemanticNodeKind.Int64BitNot
        | NyasonValue.Record("list-empty", _) ->
            let fields = recordFields "list-empty" [ "id"; "item-type" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ListEmpty(decodeTypeRef (require "item-type" fields)) }
        | NyasonValue.Record("set-empty", _) ->
            let fields = recordFields "set-empty" [ "id"; "item-type" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.SetEmpty(decodeTypeRef (require "item-type" fields)) }
        | NyasonValue.Record("map-empty", _) ->
            let fields = recordFields "map-empty" [ "id"; "key-type"; "value-type" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.MapEmpty(decodeTypeRef (require "key-type" fields), decodeTypeRef (require "value-type" fields)) }
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
        | NyasonValue.Record("map-values", _) ->
            let fields = recordFields "map-values" [ "id"; "map" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.MapValues(decodeId<GraphNodeTag> "node" (require "map" fields)) }
        | NyasonValue.Record("map-keys", _) ->
            let fields = recordFields "map-keys" [ "id"; "map" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.MapKeys(decodeId<GraphNodeTag> "node" (require "map" fields)) }
        | NyasonValue.Record("set-to-list", _) ->
            let fields = recordFields "set-to-list" [ "id"; "set" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.SetToList(decodeId<GraphNodeTag> "node" (require "set" fields)) }
        | NyasonValue.Record("map-size", _) ->
            let fields = recordFields "map-size" [ "id"; "map" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.MapSize(decodeId<GraphNodeTag> "node" (require "map" fields)) }
        | NyasonValue.Record("set-from-list", _) ->
            let fields = recordFields "set-from-list" [ "id"; "list" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.SetFromList(decodeId<GraphNodeTag> "node" (require "list" fields)) }
        | NyasonValue.Record("set-size", _) ->
            let fields = recordFields "set-size" [ "id"; "set" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.SetSize(decodeId<GraphNodeTag> "node" (require "set" fields)) }
        | NyasonValue.Record("list-reverse", _) ->
            let fields = recordFields "list-reverse" [ "id"; "list" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ListReverse(decodeId<GraphNodeTag> "node" (require "list" fields)) }
        | NyasonValue.Record("list-last", _) ->
            let fields = recordFields "list-last" [ "id"; "list" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ListLast(decodeId<GraphNodeTag> "node" (require "list" fields)) }
        | NyasonValue.Record("list-drop-last", _) ->
            let fields = recordFields "list-drop-last" [ "id"; "list" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ListDropLast(decodeId<GraphNodeTag> "node" (require "list" fields)) }
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
        | NyasonValue.Record("list-set-at", _) ->
            let fields = recordFields "list-set-at" [ "id"; "list"; "index"; "item" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.ListSetAt(
                    decodeId<GraphNodeTag> "node" (require "list" fields),
                    decodeId<GraphNodeTag> "node" (require "index" fields),
                    decodeId<GraphNodeTag> "node" (require "item" fields)
                ) }
        | NyasonValue.Record("list-insert-at", _) ->
            let fields = recordFields "list-insert-at" [ "id"; "list"; "index"; "item" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.ListInsertAt(
                    decodeId<GraphNodeTag> "node" (require "list" fields),
                    decodeId<GraphNodeTag> "node" (require "index" fields),
                    decodeId<GraphNodeTag> "node" (require "item" fields)
                ) }
        | NyasonValue.Record("list-remove-at", _) ->
            let fields = recordFields "list-remove-at" [ "id"; "list"; "index" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.ListRemoveAt(
                    decodeId<GraphNodeTag> "node" (require "list" fields),
                    decodeId<GraphNodeTag> "node" (require "index" fields)
                ) }
        | NyasonValue.Record("map-insert", _) ->
            let fields = recordFields "map-insert" [ "id"; "map"; "key"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.MapInsert(
                    decodeId<GraphNodeTag> "node" (require "map" fields),
                    decodeId<GraphNodeTag> "node" (require "key" fields),
                    decodeId<GraphNodeTag> "node" (require "value" fields)
                ) }
        | NyasonValue.Record("list-item-at", _) ->
            let fields = recordFields "list-item-at" [ "id"; "list"; "index" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.ListItemAt(
                    decodeId<GraphNodeTag> "node" (require "list" fields),
                    decodeId<GraphNodeTag> "node" (require "index" fields)
                ) }
        | NyasonValue.Record("map-remove", _) ->
            let fields = recordFields "map-remove" [ "id"; "map"; "key" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.MapRemove(
                    decodeId<GraphNodeTag> "node" (require "map" fields),
                    decodeId<GraphNodeTag> "node" (require "key" fields)
                ) }
        | NyasonValue.Record("set-insert", _) ->
            let fields = recordFields "set-insert" [ "id"; "set"; "item" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.SetInsert(
                    decodeId<GraphNodeTag> "node" (require "set" fields),
                    decodeId<GraphNodeTag> "node" (require "item" fields)
                ) }
        | NyasonValue.Record("set-remove", _) ->
            let fields = recordFields "set-remove" [ "id"; "set"; "item" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.SetRemove(
                    decodeId<GraphNodeTag> "node" (require "set" fields),
                    decodeId<GraphNodeTag> "node" (require "item" fields)
                ) }
        | NyasonValue.Record("set-union", _) ->
            let fields = recordFields "set-union" [ "id"; "left"; "right" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.SetUnion(decodeId<GraphNodeTag> "node" (require "left" fields), decodeId<GraphNodeTag> "node" (require "right" fields)) }
        | NyasonValue.Record("set-intersection", _) ->
            let fields = recordFields "set-intersection" [ "id"; "left"; "right" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.SetIntersection(decodeId<GraphNodeTag> "node" (require "left" fields), decodeId<GraphNodeTag> "node" (require "right" fields)) }
        | NyasonValue.Record("set-difference", _) ->
            let fields = recordFields "set-difference" [ "id"; "left"; "right" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.SetDifference(decodeId<GraphNodeTag> "node" (require "left" fields), decodeId<GraphNodeTag> "node" (require "right" fields)) }
        | NyasonValue.Record("set-is-subset", _) ->
            let fields = recordFields "set-is-subset" [ "id"; "left"; "right" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.SetIsSubset(decodeId<GraphNodeTag> "node" (require "left" fields), decodeId<GraphNodeTag> "node" (require "right" fields)) }
        | NyasonValue.Record("map-union", _) ->
            let fields = recordFields "map-union" [ "id"; "left"; "right" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.MapUnion(decodeId<GraphNodeTag> "node" (require "left" fields), decodeId<GraphNodeTag> "node" (require "right" fields)) }
        | NyasonValue.Record("map-contains-key", _) ->
            let fields = recordFields "map-contains-key" [ "id"; "map"; "key" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.MapContainsKey(
                    decodeId<GraphNodeTag> "node" (require "map" fields),
                    decodeId<GraphNodeTag> "node" (require "key" fields)
                ) }
        | NyasonValue.Record("set-contains", _) ->
            let fields = recordFields "set-contains" [ "id"; "set"; "item" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.SetContains(
                    decodeId<GraphNodeTag> "node" (require "set" fields),
                    decodeId<GraphNodeTag> "node" (require "item" fields)
                ) }
        | NyasonValue.Record("list-contains", _) ->
            let fields = recordFields "list-contains" [ "id"; "list"; "item" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ListContains(decodeId<GraphNodeTag> "node" (require "list" fields), decodeId<GraphNodeTag> "node" (require "item" fields)) }
        | NyasonValue.Record("list-index-of", _) ->
            let fields = recordFields "list-index-of" [ "id"; "list"; "item" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.ListIndexOf(
                    decodeId<GraphNodeTag> "node" (require "list" fields),
                    decodeId<GraphNodeTag> "node" (require "item" fields)
                ) }
        | NyasonValue.Record("list-last-index-of", _) ->
            let fields = recordFields "list-last-index-of" [ "id"; "list"; "item" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.ListLastIndexOf(
                    decodeId<GraphNodeTag> "node" (require "list" fields),
                    decodeId<GraphNodeTag> "node" (require "item" fields)
                ) }
        | NyasonValue.Record("map-get", _) ->
            let fields = recordFields "map-get" [ "id"; "map"; "key" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.MapGet(
                    decodeId<GraphNodeTag> "node" (require "map" fields),
                    decodeId<GraphNodeTag> "node" (require "key" fields)
                ) }
        | NyasonValue.Record("map-get-or-default", _) ->
            let fields = recordFields "map-get-or-default" [ "id"; "map"; "key"; "default" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.MapGetOrDefault(
                    decodeId<GraphNodeTag> "node" (require "map" fields),
                    decodeId<GraphNodeTag> "node" (require "key" fields),
                    decodeId<GraphNodeTag> "node" (require "default" fields)
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
        | NyasonValue.Record("map-fold", _) ->
            let fields = recordFields "map-fold" [ "id"; "map"; "seed"; "folder" ] [ "context" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.MapFold(decodeId<GraphNodeTag> "node" (require "map" fields), decodeId<GraphNodeTag> "node" (require "seed" fields), decodeId<FunctionTag> "function" (require "folder" fields), decodeCallbackContext fields) }
        | NyasonValue.Record("set-fold", _) ->
            let fields = recordFields "set-fold" [ "id"; "set"; "seed"; "folder" ] [ "context" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.SetFold(decodeId<GraphNodeTag> "node" (require "set" fields), decodeId<GraphNodeTag> "node" (require "seed" fields), decodeId<FunctionTag> "function" (require "folder" fields), decodeCallbackContext fields) }
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
        | NyasonValue.Record("exception-catch", _) ->
            let fields = recordFields "exception-catch" [ "id"; "body"; "handler" ] [ "body-context"; "handler-context" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ExceptionCatch(decodeId<FunctionTag> "function" (require "body" fields), decodeNamedCallbackContext "body-context" fields, decodeId<FunctionTag> "function" (require "handler" fields), decodeNamedCallbackContext "handler-context" fields) }
        | NyasonValue.Record("exception-is-cancellation", _) -> decodeUnaryNode "exception-is-cancellation" value SemanticNodeKind.ExceptionIsCancellation
        | NyasonValue.Record("exception-message", _) -> decodeUnaryNode "exception-message" value SemanticNodeKind.ExceptionMessage
        | NyasonValue.Record("exception-rethrow", _) ->
            let fields = recordFields "exception-rethrow" [ "id"; "value"; "result-type" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ExceptionRethrow(decodeId<GraphNodeTag> "node" (require "value" fields), decodeTypeRef (require "result-type" fields)) }
        | NyasonValue.Record("cancellation-shield", _) ->
            let fields = recordFields "cancellation-shield" [ "id"; "body" ] [ "context" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.CancellationShield(decodeId<FunctionTag> "function" (require "body" fields), decodeCallbackContext fields) }
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
        | NyasonValue.Record("one-shot-create", _) ->
            let fields = recordFields "one-shot-create" [ "id"; "scope"; "value-type" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields); Kind = SemanticNodeKind.OneShotCreate(decodeId<GraphNodeTag> "node" (require "scope" fields), decodeTypeRef (require "value-type" fields)) }
        | NyasonValue.Record("one-shot-complete", _) ->
            let fields = recordFields "one-shot-complete" [ "id"; "cell"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields); Kind = SemanticNodeKind.OneShotComplete(decodeId<GraphNodeTag> "node" (require "cell" fields), decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("one-shot-wait", _) ->
            let fields = recordFields "one-shot-wait" [ "id"; "cell" ] [ "limit" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields); Kind = SemanticNodeKind.OneShotWait(decodeId<GraphNodeTag> "node" (require "cell" fields), optional "limit" fields |> Option.map (decodeId<GraphNodeTag> "node")) }
        | NyasonValue.Record("task-cancel", _) ->
            let fields = recordFields "task-cancel" [ "id"; "task" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.TaskCancel(decodeId<GraphNodeTag> "node" (require "task" fields)) }
        | NyasonValue.Record("list-map", _) ->
            decodeListCallbackNode "list-map" value (fun source callback context -> SemanticNodeKind.ListMap(source, callback, context))
        | NyasonValue.Record("list-zip-with", _) ->
            let fields = recordFields "list-zip-with" [ "id"; "left"; "right"; "combiner" ] [ "context" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ListZipWith(decodeId<GraphNodeTag> "node" (require "left" fields), decodeId<GraphNodeTag> "node" (require "right" fields), decodeId<FunctionTag> "function" (require "combiner" fields), decodeCallbackContext fields) }
        | NyasonValue.Record("list-flat-map", _) ->
            decodeListCallbackNode "list-flat-map" value (fun source callback context -> SemanticNodeKind.ListFlatMap(source, callback, context))
        | NyasonValue.Record("map-from-list", _) ->
            decodeListCallbackNode "map-from-list" value (fun source callback context -> SemanticNodeKind.MapFromList(source, callback, context))
        | NyasonValue.Record("list-filter", _) ->
            decodeListCallbackNode "list-filter" value (fun source callback context -> SemanticNodeKind.ListFilter(source, callback, context))
        | NyasonValue.Record("map-filter", _) ->
            let fields = recordFields "map-filter" [ "id"; "map"; "keep" ] [ "context" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.MapFilter(decodeId<GraphNodeTag> "node" (require "map" fields), decodeId<FunctionTag> "function" (require "keep" fields), decodeCallbackContext fields) }
        | NyasonValue.Record("set-filter", _) ->
            let fields = recordFields "set-filter" [ "id"; "set"; "predicate" ] [ "context" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.SetFilter(decodeId<GraphNodeTag> "node" (require "set" fields), decodeId<FunctionTag> "function" (require "predicate" fields), decodeCallbackContext fields) }
        | NyasonValue.Record("map-map-values", _) ->
            let fields = recordFields "map-map-values" [ "id"; "map"; "mapper" ] [ "context" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.MapMapValues(decodeId<GraphNodeTag> "node" (require "map" fields), decodeId<FunctionTag> "function" (require "mapper" fields), decodeCallbackContext fields) }
        | NyasonValue.Record("set-map", _) ->
            let fields = recordFields "set-map" [ "id"; "set"; "mapper" ] [ "context" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.SetMap(decodeId<GraphNodeTag> "node" (require "set" fields), decodeId<FunctionTag> "function" (require "mapper" fields), decodeCallbackContext fields) }
        | NyasonValue.Record("option-get-or-default", _) ->
            let fields = recordFields "option-get-or-default" [ "id"; "option"; "default" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.OptionGetOrDefault(decodeId<GraphNodeTag> "node" (require "option" fields), decodeId<GraphNodeTag> "node" (require "default" fields)) }
        | NyasonValue.Record("result-get-or-default", _) ->
            let fields = recordFields "result-get-or-default" [ "id"; "result"; "default" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ResultGetOrDefault(decodeId<GraphNodeTag> "node" (require "result" fields), decodeId<GraphNodeTag> "node" (require "default" fields)) }
        | NyasonValue.Record("option-ok-or", _) ->
            let fields = recordFields "option-ok-or" [ "id"; "option"; "error" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.OptionOkOr(decodeId<GraphNodeTag> "node" (require "option" fields), decodeId<GraphNodeTag> "node" (require "error" fields)) }
        | NyasonValue.Record("result-ok-value", _) ->
            let fields = recordFields "result-ok-value" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ResultOkValue(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("result-err-value", _) ->
            let fields = recordFields "result-err-value" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ResultErrValue(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("option-is-some", _) ->
            let fields = recordFields "option-is-some" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.OptionIsSome(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("result-is-ok", _) ->
            let fields = recordFields "result-is-ok" [ "id"; "value" ] [] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ResultIsOk(decodeId<GraphNodeTag> "node" (require "value" fields)) }
        | NyasonValue.Record("option-map", _) ->
            let fields = recordFields "option-map" [ "id"; "option"; "mapper" ] [ "context" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.OptionMap(decodeId<GraphNodeTag> "node" (require "option" fields), decodeId<FunctionTag> "function" (require "mapper" fields), decodeCallbackContext fields) }
        | NyasonValue.Record("option-and-then", _) ->
            let fields = recordFields "option-and-then" [ "id"; "option"; "binder" ] [ "context" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.OptionAndThen(decodeId<GraphNodeTag> "node" (require "option" fields), decodeId<FunctionTag> "function" (require "binder" fields), decodeCallbackContext fields) }
        | NyasonValue.Record("result-map", _) ->
            let fields = recordFields "result-map" [ "id"; "result"; "mapper" ] [ "context" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ResultMap(decodeId<GraphNodeTag> "node" (require "result" fields), decodeId<FunctionTag> "function" (require "mapper" fields), decodeCallbackContext fields) }
        | NyasonValue.Record("result-map-err", _) ->
            let fields = recordFields "result-map-err" [ "id"; "result"; "mapper" ] [ "context" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ResultMapErr(decodeId<GraphNodeTag> "node" (require "result" fields), decodeId<FunctionTag> "function" (require "mapper" fields), decodeCallbackContext fields) }
        | NyasonValue.Record("result-and-then", _) ->
            let fields = recordFields "result-and-then" [ "id"; "result"; "binder" ] [ "context" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind = SemanticNodeKind.ResultAndThen(decodeId<GraphNodeTag> "node" (require "result" fields), decodeId<FunctionTag> "function" (require "binder" fields), decodeCallbackContext fields) }
        | NyasonValue.Record("list-sort-by", _) ->
            decodeListCallbackNode "list-sort-by" value (fun source callback context -> SemanticNodeKind.ListSortBy(source, callback, context))
        | NyasonValue.Record("list-min-by", _) ->
            decodeListCallbackNode "list-min-by" value (fun source callback context -> SemanticNodeKind.ListMinBy(source, callback, context))
        | NyasonValue.Record("list-max-by", _) ->
            decodeListCallbackNode "list-max-by" value (fun source callback context -> SemanticNodeKind.ListMaxBy(source, callback, context))
        | NyasonValue.Record("list-any", _) ->
            decodeListCallbackNode "list-any" value (fun source callback context -> SemanticNodeKind.ListAny(source, callback, context))
        | NyasonValue.Record("list-find-index", _) ->
            decodeListCallbackNode "list-find-index" value (fun source callback context -> SemanticNodeKind.ListFindIndex(source, callback, context))
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
            let fields = recordFields "product-construct" [ "id"; "type"; "fields" ] [ "type-arguments" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.ProductConstruct(
                    decodeId<TypeTag> "type" (require "type" fields),
                    decodeTypeArguments "type-arguments" fields,
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
            let fields = recordFields "union-construct" [ "id"; "variant"; "fields" ] [ "type-arguments" ] value
            { Id = decodeId<GraphNodeTag> "node" (require "id" fields)
              Kind =
                SemanticNodeKind.UnionConstruct(
                    decodeId<VariantTag> "variant" (require "variant" fields),
                    decodeTypeArguments "type-arguments" fields,
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

    let private encodeFunction (explicitVisibility: bool) (functionEntity: SemanticFunction) =
        // Declared order; absent when empty, and absent before v17, where the parameter types
        // imply the list.
        let typeParameters = if legacyWriteNames.Value.IsSome then [] else encodeTypeParameters functionEntity.TypeParameters
        record
            "function"
            ([ "id", encodeId "function" functionEntity.Id
               "display-name", text functionEntity.DisplayName ]
             @ visibilityFields explicitVisibility functionEntity.Exported
             @ [ "parameters", functionEntity.Parameters |> List.map encodeParameter |> list
                 "result-type", encodeTypeRef functionEntity.ResultType
                 "required-effects",
                 functionEntity.RequiredEffects
                 |> List.sortBy StableId.value
                 |> List.map (encodeId "effect")
                 |> list
                 "body", encodeGraph functionEntity.Body ]
             @ typeParameters)

    let private decodeFunction (explicitVisibility: bool) value: SemanticFunction =
        let fields =
            recordFields
                "function"
                [ "id"; "display-name"; "parameters"; "result-type"; "required-effects"; "body" ]
                (visibilityOptional explicitVisibility @ [ "type-parameters" ])
                value
        let parameters = decodeList decodeParameter (require "parameters" fields)
        let typeParameters =
            match legacyReadNames.Value with
            | None -> decodeTypeParameters fields
            | Some _ when (optional "type-parameters" fields).IsSome ->
                // An explicit list in an older document is read so that `firstUnsupported` can name
                // the construct and the schema it needs.
                decodeTypeParameters fields
            | Some names ->
                // Before v17 the list was implied: the parameters' type parameters in
                // first-appearance order, each named as its occurrences spelled it.
                TypeParameters.derived (parameters |> List.map (fun parameter -> parameter.Type))
                |> List.map (fun id ->
                    id,
                    (match names.TryGetValue id with
                     | true, name -> name
                     | _ -> StableId.value id))

        { Id = decodeId<FunctionTag> "function" (require "id" fields)
          DisplayName = decodeText (require "display-name" fields)
          Exported = decodeVisibility explicitVisibility fields
          TypeParameters = typeParameters
          Parameters = parameters
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

    let private encodeSnapshot (explicitVisibility: bool) (snapshot: Snapshot) =
        let parentFields =
            match snapshot.ParentId with
            | Some parentId -> [ "parent", encodeId "snapshot" parentId ]
            | None -> []

        let collectionFields =
            [ "types", snapshot.Types |> List.sortBy (fun item -> StableId.value item.Id) |> List.map (encodeType explicitVisibility) |> list
              "effects", snapshot.Effects |> List.sortBy (fun item -> StableId.value item.Id) |> List.map (encodeEffect explicitVisibility) |> list
              "capabilities",
              snapshot.Capabilities
              |> List.sortBy (fun item -> StableId.value item.Id)
              |> List.map (encodeCapability explicitVisibility)
              |> list
              "functions",
              snapshot.Functions
              |> List.sortBy (fun item -> StableId.value item.Id)
              |> List.map (encodeFunction explicitVisibility)
              |> list
              "obligations",
              snapshot.Obligations
              |> List.sortBy (fun item -> StableId.value item.Id)
              |> List.map encodeObligation
              |> list ]

        record "snapshot" ([ "id", encodeId "snapshot" snapshot.Id ] @ parentFields @ collectionFields)

    let private decodeSnapshot (explicitVisibility: bool) value: Snapshot =
        let fields =
            recordFields
                "snapshot"
                [ "id"; "types"; "effects"; "capabilities"; "functions"; "obligations" ]
                [ "parent" ]
                value

        { Id = decodeId<SnapshotTag> "snapshot" (require "id" fields)
          ParentId = optional "parent" fields |> Option.map (decodeId<SnapshotTag> "snapshot")
          Types = decodeList (decodeType explicitVisibility) (require "types" fields)
          Effects = decodeList (decodeEffect explicitVisibility) (require "effects" fields)
          Capabilities = decodeList (decodeCapability explicitVisibility) (require "capabilities" fields)
          Functions = decodeList (decodeFunction explicitVisibility) (require "functions" fields)
          Obligations = decodeList decodeObligation (require "obligations" fields) }

    let private serializeAt (required: SnapshotSchemaVersion) (snapshot: Snapshot): Result<string, NyasonCodecError> =
        let tooDeep = Error(NyasonCodecError.MaximumDepthExceeded NyasonSyntax.maxDepth)
        if SnapshotCodec.nestsBeyondMaxDepth snapshot then tooDeep
        else
            match SnapshotValidator.validate snapshot with
            | issue :: rest -> Error(NyasonCodecError.InvalidSnapshot(issue :: rest))
            | [] ->
                let legacyNames =
                    if required.Json < SnapshotSchema.declaredFunctionTypeParameters.Json then Some(TypeParameters.names snapshot) else None
                let root =
                    record
                        "nyason"
                        [ "schema", text required.Nyason
                          "snapshot", withLegacyWriteNames legacyNames (fun () -> encodeSnapshot (SnapshotSchema.writesVisibility required) snapshot) ]
                if NyasonSyntax.exceedsMaxDepth root then tooDeep else Ok(NyasonSyntax.render root)

    let serialize (snapshot: Snapshot): Result<string, NyasonCodecError> =
        serializeAt SnapshotSchema.current snapshot

    /// Historical minimum-version encoding for compatibility fixtures, not ordinary persistence.
    let internal serializeCanonical (snapshot: Snapshot): Result<string, NyasonCodecError> =
        serializeAt (SnapshotSchema.required snapshot) snapshot

    let deserialize (source: string): Result<Snapshot, NyasonCodecError> =
        match NyasonSyntax.parse source with
        | Error error -> Error(NyasonCodecError.InvalidSyntax error)
        | Ok root ->
            try
                let fields = recordFields "nyason" [ "schema"; "snapshot" ] [] root
                let actualSchema = decodeText (require "schema" fields)
                let declared =
                    match SnapshotSchema.tryNyason actualSchema with
                    | Some declared -> declared
                    | None -> raise (NyasonSchemaException actualSchema)

                // Version-gated defaults: below v17 every declaration reads as exported and each
                // function's type parameter list is derived from its named occurrences.
                let legacyNames =
                    if declared.Json < SnapshotSchema.declaredFunctionTypeParameters.Json then Some(System.Collections.Generic.Dictionary<TypeParameterId, string>())
                    else None
                let snapshot = withLegacyReadNames legacyNames (fun () -> decodeSnapshot (SnapshotSchema.writesVisibility declared) (require "snapshot" fields))
                match SnapshotSchema.firstUnsupported declared snapshot with
                | Some(_, minimum, name) -> raise (NyasonShapeException $"{name} requires {minimum.Nyason}.")
                | None -> ()
                match SnapshotValidator.validate snapshot with
                | [] -> Ok snapshot
                | issues -> Error(NyasonCodecError.InvalidSnapshot issues)
            with
            | NyasonSchemaException actual -> Error(NyasonCodecError.UnsupportedSchema actual)
            | NyasonShapeException message -> Error(NyasonCodecError.InvalidShape message)
            | :? ArgumentException as error -> Error(NyasonCodecError.InvalidShape error.Message)
