namespace NyamNyam.Tools.AuthoringSession

open System
open System.Collections.Generic
open System.Text.Json
open NyamNyam.Core

module internal WireJson =
    let objectValue (pairs: (string * obj) list): obj =
        let value = Dictionary<string, obj>(StringComparer.Ordinal)
        for key, item in pairs do
            value[key] <- item
        box value

    let arrayValue (items: obj list): obj = items |> List.toArray |> box
    let serialize (value: obj): string = JsonSerializer.Serialize(value)

module internal WireInput =
    exception ProtocolInputException of string

    let fail message = raise (ProtocolInputException message)

    let parseDocument line =
        try JsonDocument.Parse(line)
        with :? JsonException as error -> fail $"Invalid JSON: {error.Message}"

    let private propertyNames (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then fail "Expected a JSON object."
        element.EnumerateObject() |> Seq.map (fun item -> item.Name) |> Set.ofSeq

    let ensureFields context allowed required element =
        let actual = propertyNames element
        let unknown = Set.difference actual (Set.ofList allowed) |> Set.toList |> List.sort
        let missing = Set.difference (Set.ofList required) actual |> Set.toList |> List.sort
        if not (List.isEmpty unknown) then
            let rendered = String.concat ", " unknown
            fail $"{context} contains unsupported properties: {rendered}."
        if not (List.isEmpty missing) then
            let rendered = String.concat ", " missing
            fail $"{context} is missing required properties: {rendered}."

    let property name (element: JsonElement) =
        match element.TryGetProperty(name) with
        | true, value -> value
        | false, _ -> fail $"Missing required property '{name}'."

    let tryProperty name (element: JsonElement) =
        match element.TryGetProperty(name) with
        | true, value -> Some value
        | false, _ -> None

    let requiredString name element =
        let value = property name element
        if value.ValueKind <> JsonValueKind.String then fail $"Property '{name}' must be a JSON string."
        value.GetString()

    let requiredBool name element =
        let value = property name element
        if value.ValueKind <> JsonValueKind.True && value.ValueKind <> JsonValueKind.False then
            fail $"Property '{name}' must be a JSON boolean."
        value.GetBoolean()

    let optionalBool name fallback element =
        match tryProperty name element with
        | None -> fallback
        | Some value when value.ValueKind = JsonValueKind.True || value.ValueKind = JsonValueKind.False -> value.GetBoolean()
        | Some _ -> fail $"Property '{name}' must be a JSON boolean."

    let requiredInt name element =
        let value = property name element
        match value.TryGetInt32() with
        | true, result -> result
        | false, _ -> fail $"Property '{name}' must be a 32-bit integer."

    let requiredInt64 name element =
        let value = property name element
        match value.TryGetInt64() with
        | true, result -> result
        | false, _ -> fail $"Property '{name}' must be a 64-bit integer."

    let array name element =
        let value = property name element
        if value.ValueKind <> JsonValueKind.Array then fail $"Property '{name}' must be a JSON array."
        value.EnumerateArray() |> Seq.toList

    let stableId<'Kind> label text: StableId<'Kind> =
        match StableId.tryCreate<'Kind> text with
        | Ok value -> value
        | Error error -> fail $"{label} is not a valid stable ID: {error}."

    let id<'Kind> name element = stableId<'Kind> name (requiredString name element)

    let rec typeRef element =
        match requiredString "kind" element with
        | "primitive" ->
            ensureFields "primitive type" [ "kind"; "primitive" ] [ "kind"; "primitive" ] element
            match requiredString "primitive" element with
            | "string" -> TypeRef.Primitive PrimitiveType.String
            | "bool" -> TypeRef.Primitive PrimitiveType.Bool
            | "unit" -> TypeRef.Primitive PrimitiveType.Unit
            | "int64" -> TypeRef.Primitive PrimitiveType.Int64
            | "float64" -> TypeRef.Primitive PrimitiveType.Float64
            | value -> fail $"Unsupported primitive type '{value}'."
        | "named" ->
            ensureFields "named type" [ "kind"; "typeId" ] [ "kind"; "typeId" ] element
            TypeRef.Named(id<TypeTag> "typeId" element)
        | "capability" ->
            ensureFields "capability type" [ "kind"; "capabilityId" ] [ "kind"; "capabilityId" ] element
            TypeRef.Capability(id<CapabilityTag> "capabilityId" element)
        | "builtinGeneric" ->
            ensureFields
                "builtin generic type"
                [ "kind"; "generic"; "arguments" ]
                [ "kind"; "generic"; "arguments" ]
                element
            let generic =
                match requiredString "generic" element with
                | "option" -> BuiltinGenericType.Option
                | "result" -> BuiltinGenericType.Result
                | "list" -> BuiltinGenericType.List
                | value -> fail $"Unsupported builtin generic '{value}'."
            TypeRef.BuiltinGeneric(generic, array "arguments" element |> List.map typeRef)
        | value -> fail $"Unsupported type kind '{value}'."

    let rec recipe element =
        let binary context constructor =
            ensureFields context [ "kind"; "left"; "right" ] [ "kind"; "left"; "right" ] element
            constructor(recipe (property "left" element), recipe (property "right" element))

        let unary context constructor =
            ensureFields context [ "kind"; "value" ] [ "kind"; "value" ] element
            constructor(recipe (property "value" element))

        let fieldValues name =
            array name element
            |> List.map (fun item ->
                ensureFields "field recipe" [ "fieldId"; "value" ] [ "fieldId"; "value" ] item
                id<FieldTag> "fieldId" item, recipe (property "value" item))

        let callArguments name =
            array name element
            |> List.map (fun item ->
                ensureFields "call argument" [ "parameterId"; "value" ] [ "parameterId"; "value" ] item
                id<ParameterTag> "parameterId" item, recipe (property "value" item))

        let capabilityArguments name =
            array name element
            |> List.map (fun item ->
                ensureFields
                    "capability argument"
                    [ "capabilityParameterId"; "value" ]
                    [ "capabilityParameterId"; "value" ]
                    item
                id<CapabilityParameterTag> "capabilityParameterId" item,
                recipe (property "value" item))

        match requiredString "kind" element with
        | "parameter" ->
            ensureFields "parameter recipe" [ "kind"; "parameterId" ] [ "kind"; "parameterId" ] element
            ExpressionRecipe.Parameter(id<ParameterTag> "parameterId" element)
        | "parameterSlot" ->
            ensureFields "parameter-slot recipe" [ "kind"; "index" ] [ "kind"; "index" ] element
            ExpressionRecipe.ParameterSlot(requiredInt "index" element)
        | "patternReference" ->
            ensureFields
                "pattern-reference recipe"
                [ "kind"; "scopeDepth"; "slot"; "fieldId" ]
                [ "kind"; "scopeDepth"; "slot" ]
                element
            let slot =
                match requiredString "slot" element with
                | "value" -> RecipePatternSlot.Value
                | "error" -> RecipePatternSlot.Error
                | "field" -> RecipePatternSlot.Field(id<FieldTag> "fieldId" element)
                | value -> fail $"Unsupported pattern slot '{value}'."
            ExpressionRecipe.PatternReference
                { ScopeDepth = requiredInt "scopeDepth" element
                  Slot = slot }
        | "stringLiteral" ->
            ensureFields "string literal recipe" [ "kind"; "value" ] [ "kind"; "value" ] element
            ExpressionRecipe.StringLiteral(requiredString "value" element)
        | "boolLiteral" ->
            ensureFields "bool literal recipe" [ "kind"; "value" ] [ "kind"; "value" ] element
            ExpressionRecipe.BoolLiteral(requiredBool "value" element)
        | "unitLiteral" ->
            ensureFields "unit literal recipe" [ "kind" ] [ "kind" ] element
            ExpressionRecipe.UnitLiteral
        | "int64Literal" ->
            ensureFields "int64 literal recipe" [ "kind"; "value" ] [ "kind"; "value" ] element
            ExpressionRecipe.Int64Literal(requiredInt64 "value" element)
        | "float64Literal" ->
            ensureFields "float64 literal recipe" [ "kind"; "bits" ] [ "kind"; "bits" ] element
            match Float64Bits.tryParseHex (requiredString "bits" element) with
            | Ok bits -> ExpressionRecipe.Float64Literal bits
            | Error error -> fail $"Invalid Float64 bits: {error}."
        | "stringIsEmpty" -> unary "string-is-empty recipe" ExpressionRecipe.StringIsEmpty
        | "stringContains" ->
            ensureFields
                "string-contains recipe"
                [ "kind"; "value"; "substring" ]
                [ "kind"; "value"; "substring" ]
                element
            ExpressionRecipe.StringContains(
                recipe (property "value" element),
                recipe (property "substring" element)
            )
        | "int64WrappingAdd" -> binary "int64-wrapping-add recipe" ExpressionRecipe.Int64WrappingAdd
        | "int64WrappingSubtract" -> binary "int64-wrapping-subtract recipe" ExpressionRecipe.Int64WrappingSubtract
        | "int64WrappingMultiply" -> binary "int64-wrapping-multiply recipe" ExpressionRecipe.Int64WrappingMultiply
        | "int64Equal" -> binary "int64-equal recipe" ExpressionRecipe.Int64Equal
        | "int64LessThan" -> binary "int64-less-than recipe" ExpressionRecipe.Int64LessThan
        | "int64LessThanOrEqual" -> binary "int64-less-than-or-equal recipe" ExpressionRecipe.Int64LessThanOrEqual
        | "int64GreaterThan" -> binary "int64-greater-than recipe" ExpressionRecipe.Int64GreaterThan
        | "int64GreaterThanOrEqual" -> binary "int64-greater-than-or-equal recipe" ExpressionRecipe.Int64GreaterThanOrEqual
        | "float64Add" -> binary "float64-add recipe" ExpressionRecipe.Float64Add
        | "float64Subtract" -> binary "float64-subtract recipe" ExpressionRecipe.Float64Subtract
        | "float64Multiply" -> binary "float64-multiply recipe" ExpressionRecipe.Float64Multiply
        | "float64Divide" -> binary "float64-divide recipe" ExpressionRecipe.Float64Divide
        | "float64Equal" -> binary "float64-equal recipe" ExpressionRecipe.Float64Equal
        | "float64LessThan" -> binary "float64-less-than recipe" ExpressionRecipe.Float64LessThan
        | "float64IsNaN" -> unary "float64-is-nan recipe" ExpressionRecipe.Float64IsNaN
        | "float64IsFinite" -> unary "float64-is-finite recipe" ExpressionRecipe.Float64IsFinite
        | "float64IsInfinite" -> unary "float64-is-infinite recipe" ExpressionRecipe.Float64IsInfinite
        | "listEmpty" ->
            ensureFields "list-empty recipe" [ "kind"; "itemType" ] [ "kind"; "itemType" ] element
            ExpressionRecipe.ListEmpty(typeRef (property "itemType" element))
        | "listConstruct" ->
            ensureFields
                "list-construct recipe"
                [ "kind"; "itemType"; "items" ]
                [ "kind"; "itemType"; "items" ]
                element
            ExpressionRecipe.ListConstruct(
                typeRef (property "itemType" element),
                array "items" element |> List.map recipe
            )
        | "listLength" -> unary "list-length recipe" ExpressionRecipe.ListLength
        | "listAppendItem" ->
            ensureFields
                "list-append-item recipe"
                [ "kind"; "list"; "item" ]
                [ "kind"; "list"; "item" ]
                element
            ExpressionRecipe.ListAppendItem(
                recipe (property "list" element),
                recipe (property "item" element)
            )
        | "listConcat" -> binary "list-concat recipe" ExpressionRecipe.ListConcat
        | "listMap"
        | "listFilter"
        | "listAny"
        | "listAll" as kind ->
            ensureFields
                $"{kind} recipe"
                [ "kind"; "source"; "functionId" ]
                [ "kind"; "source"; "functionId" ]
                element
            let source = recipe (property "source" element)
            let functionId = id<FunctionTag> "functionId" element
            match kind with
            | "listMap" -> ExpressionRecipe.ListMap(source, functionId)
            | "listFilter" -> ExpressionRecipe.ListFilter(source, functionId)
            | "listAny" -> ExpressionRecipe.ListAny(source, functionId)
            | _ -> ExpressionRecipe.ListAll(source, functionId)
        | "productConstruct" ->
            ensureFields
                "product-construct recipe"
                [ "kind"; "typeId"; "fields" ]
                [ "kind"; "typeId"; "fields" ]
                element
            ExpressionRecipe.ProductConstruct(id<TypeTag> "typeId" element, fieldValues "fields")
        | "fieldAccess" ->
            ensureFields
                "field-access recipe"
                [ "kind"; "source"; "fieldId" ]
                [ "kind"; "source"; "fieldId" ]
                element
            ExpressionRecipe.FieldAccess(
                recipe (property "source" element),
                id<FieldTag> "fieldId" element
            )
        | "unionConstruct" ->
            ensureFields
                "union-construct recipe"
                [ "kind"; "variantId"; "fields" ]
                [ "kind"; "variantId"; "fields" ]
                element
            ExpressionRecipe.UnionConstruct(id<VariantTag> "variantId" element, fieldValues "fields")
        | "optionNone" ->
            ensureFields "option-none recipe" [ "kind"; "itemType" ] [ "kind"; "itemType" ] element
            ExpressionRecipe.OptionNone(typeRef (property "itemType" element))
        | "optionSome" -> unary "option-some recipe" ExpressionRecipe.OptionSome
        | "resultOk" ->
            ensureFields
                "result-ok recipe"
                [ "kind"; "errorType"; "value" ]
                [ "kind"; "errorType"; "value" ]
                element
            ExpressionRecipe.ResultOk(
                typeRef (property "errorType" element),
                recipe (property "value" element)
            )
        | "resultErr" ->
            ensureFields
                "result-err recipe"
                [ "kind"; "okType"; "error" ]
                [ "kind"; "okType"; "error" ]
                element
            ExpressionRecipe.ResultErr(
                typeRef (property "okType" element),
                recipe (property "error" element)
            )
        | "directCall" ->
            ensureFields
                "direct-call recipe"
                [ "kind"; "functionId"; "arguments" ]
                [ "kind"; "functionId"; "arguments" ]
                element
            ExpressionRecipe.DirectCall(
                id<FunctionTag> "functionId" element,
                callArguments "arguments"
            )
        | "capabilityInvoke" ->
            ensureFields
                "capability-invoke recipe"
                [ "kind"; "capabilityValue"; "operationId"; "arguments" ]
                [ "kind"; "capabilityValue"; "operationId"; "arguments" ]
                element
            ExpressionRecipe.CapabilityInvoke(
                recipe (property "capabilityValue" element),
                id<CapabilityOperationTag> "operationId" element,
                capabilityArguments "arguments"
            )
        | "conditional" ->
            ensureFields
                "conditional recipe"
                [ "kind"; "condition"; "whenTrue"; "whenFalse" ]
                [ "kind"; "condition"; "whenTrue"; "whenFalse" ]
                element
            ExpressionRecipe.Conditional(
                recipe (property "condition" element),
                recipe (property "whenTrue" element),
                recipe (property "whenFalse" element)
            )
        | "matchUnion" ->
            ensureFields
                "match-union recipe"
                [ "kind"; "input"; "arms" ]
                [ "kind"; "input"; "arms" ]
                element
            let arms =
                array "arms" element
                |> List.map (fun arm ->
                    ensureFields "union arm" [ "variantId"; "body" ] [ "variantId"; "body" ] arm
                    id<VariantTag> "variantId" arm, recipe (property "body" arm))
            ExpressionRecipe.MatchUnion(recipe (property "input" element), arms)
        | "bind" ->
            ensureFields "bind recipe" [ "kind"; "value"; "body" ] [ "kind"; "value"; "body" ] element
            ExpressionRecipe.Bind(recipe (property "value" element), recipe (property "body" element))
        | "matchOption" ->
            ensureFields
                "match-option recipe"
                [ "kind"; "input"; "someBody"; "noneBody" ]
                [ "kind"; "input"; "someBody"; "noneBody" ]
                element
            ExpressionRecipe.MatchOption(
                recipe (property "input" element),
                recipe (property "someBody" element),
                recipe (property "noneBody" element)
            )
        | "matchResult" ->
            ensureFields
                "match-result recipe"
                [ "kind"; "input"; "okBody"; "errBody" ]
                [ "kind"; "input"; "okBody"; "errBody" ]
                element
            ExpressionRecipe.MatchResult(
                recipe (property "input" element),
                recipe (property "okBody" element),
                recipe (property "errBody" element)
            )
        | value -> fail $"Unsupported expression recipe kind '{value}'."

    let operation element =
        match requiredString "kind" element with
        | "renameFunction" ->
            ensureFields
                "rename-function operation"
                [ "kind"; "functionId"; "displayName" ]
                [ "kind"; "functionId"; "displayName" ]
                element
            AgentOperationIntent.RenameDisplayName(
                AgentRenameTarget.Function(id<FunctionTag> "functionId" element),
                requiredString "displayName" element
            )
        | "renameType" ->
            ensureFields
                "rename-type operation"
                [ "kind"; "typeId"; "displayName" ]
                [ "kind"; "typeId"; "displayName" ]
                element
            AgentOperationIntent.RenameDisplayName(
                AgentRenameTarget.Type(id<TypeTag> "typeId" element),
                requiredString "displayName" element
            )
        | "addFunctionParameter" ->
            ensureFields
                "add-function-parameter operation"
                [ "kind"; "functionId"; "displayName"; "type"; "callSites" ]
                [ "kind"; "functionId"; "displayName"; "type"; "callSites" ]
                element
            let plans =
                array "callSites" element
                |> List.map (fun plan ->
                    match requiredString "kind" plan with
                    | "bind" ->
                        ensureFields
                            "bind call-site plan"
                            [ "kind"; "callNodeId"; "valueNodeId" ]
                            [ "kind"; "callNodeId"; "valueNodeId" ]
                            plan
                        AgentCallPlan.BindExistingValue(
                            id<GraphNodeTag> "callNodeId" plan,
                            id<GraphNodeTag> "valueNodeId" plan
                        )
                    | "leaveMissing" ->
                        ensureFields
                            "leave-missing call-site plan"
                            [ "kind"; "callNodeId" ]
                            [ "kind"; "callNodeId" ]
                            plan
                        AgentCallPlan.LeaveMissing(id<GraphNodeTag> "callNodeId" plan)
                    | value -> fail $"Unsupported call-site plan '{value}'.")
            AgentOperationIntent.AddFunctionParameter
                { TargetFunctionId = id<FunctionTag> "functionId" element
                  NewParameterDisplayName = requiredString "displayName" element
                  NewParameterType = typeRef (property "type" element)
                  CallSitePlans = plans }
        | "resolveMissingArgument" ->
            ensureFields
                "resolve-missing-argument operation"
                [ "kind"; "obligationId"; "valueNodeId" ]
                [ "kind"; "obligationId"; "valueNodeId" ]
                element
            AgentOperationIntent.ResolveMissingArgument
                { TargetObligationId = id<ObligationTag> "obligationId" element
                  ResolutionValueNodeId = id<GraphNodeTag> "valueNodeId" element }
        | "addRequiredEffect"
        | "removeRequiredEffect" as kind ->
            ensureFields
                $"{kind} operation"
                [ "kind"; "functionId"; "effectId" ]
                [ "kind"; "functionId"; "effectId" ]
                element
            let intent: AgentFunctionRequiredEffectIntent =
                { TargetFunctionId = id<FunctionTag> "functionId" element
                  EffectId = id<EffectTag> "effectId" element }
            if kind = "addRequiredEffect" then
                AgentOperationIntent.AddFunctionRequiredEffect intent
            else
                AgentOperationIntent.RemoveFunctionRequiredEffect intent
        | value -> fail $"Unsupported operation kind '{value}'."

module internal WireEncode =
    open WireJson

    let private optional value encode =
        match value with
        | Some item -> encode item
        | None -> null

    let rec typeRef = function
        | TypeRef.Primitive primitive ->
            let name =
                match primitive with
                | PrimitiveType.String -> "string"
                | PrimitiveType.Bool -> "bool"
                | PrimitiveType.Unit -> "unit"
                | PrimitiveType.Int64 -> "int64"
                | PrimitiveType.Float64 -> "float64"
            objectValue [ "kind", box "primitive"; "primitive", box name ]
        | TypeRef.Named typeId ->
            objectValue [ "kind", box "named"; "typeId", box (StableId.value typeId) ]
        | TypeRef.Capability capabilityId ->
            objectValue
                [ "kind", box "capability"
                  "capabilityId", box (StableId.value capabilityId) ]
        | TypeRef.BuiltinGeneric(generic, arguments) ->
            let name =
                match generic with
                | BuiltinGenericType.Option -> "option"
                | BuiltinGenericType.Result -> "result"
                | BuiltinGenericType.List -> "list"
            objectValue
                [ "kind", box "builtinGeneric"
                  "generic", box name
                  "arguments", arguments |> List.map typeRef |> arrayValue ]

    let private parameter (value: SemanticParameter) =
        objectValue
            [ "id", box (StableId.value value.Id)
              "displayName", box value.DisplayName
              "type", typeRef value.Type ]

    let functionEntity (value: SemanticFunction) =
        let nodes =
            ValueGraph.collectNodes value.Body
            |> List.map (fun node ->
                objectValue
                    [ "id", box (StableId.value node.Id)
                      "kind", box (sprintf "%A" node.Kind) ])
        objectValue
            [ "id", box (StableId.value value.Id)
              "displayName", box value.DisplayName
              "parameters", value.Parameters |> List.map parameter |> arrayValue
              "resultType", typeRef value.ResultType
              "requiredEffects", value.RequiredEffects |> List.map (StableId.value >> box) |> arrayValue
              "body",
                objectValue
                    [ "nodes", arrayValue nodes
                      "resultNodeId", box (StableId.value value.Body.ResultNodeId) ] ]

    let private field (value: SemanticField) =
        objectValue
            [ "id", box (StableId.value value.Id)
              "displayName", box value.DisplayName
              "type", typeRef value.Type ]

    let private semanticType (value: SemanticType) =
        let kind =
            match value.Kind with
            | SemanticTypeKind.Product fields ->
                objectValue
                    [ "kind", box "product"
                      "fields", fields |> List.map field |> arrayValue ]
            | SemanticTypeKind.Union variants ->
                objectValue
                    [ "kind", box "union"
                      "variants",
                        variants
                        |> List.map (fun variant ->
                            objectValue
                                [ "id", box (StableId.value variant.Id)
                                  "displayName", box variant.DisplayName
                                  "fields", variant.Fields |> List.map field |> arrayValue ])
                        |> arrayValue ]
        objectValue
            [ "id", box (StableId.value value.Id)
              "displayName", box value.DisplayName
              "typeKind", kind ]

    let declarations (value: AuthoringDeclarations) =
        let effects =
            value.Effects
            |> List.map (fun effect ->
                objectValue
                    [ "id", box (StableId.value effect.Id)
                      "displayName", box effect.DisplayName ])
        let capabilities =
            value.Capabilities
            |> List.map (fun capability ->
                let operations =
                    capability.Operations
                    |> List.map (fun operation ->
                        let parameters =
                            operation.Parameters
                            |> List.map (fun parameter ->
                                objectValue
                                    [ "id", box (StableId.value parameter.Id)
                                      "displayName", box parameter.DisplayName
                                      "type", typeRef parameter.Type ])
                        objectValue
                            [ "id", box (StableId.value operation.Id)
                              "displayName", box operation.DisplayName
                              "parameters", arrayValue parameters
                              "resultType", typeRef operation.ResultType
                              "failureType", optional operation.FailureType typeRef
                              "effectId", box (StableId.value operation.EffectId) ])
                objectValue
                    [ "id", box (StableId.value capability.Id)
                      "displayName", box capability.DisplayName
                      "operations", arrayValue operations ])
        objectValue
            [ "functions", value.Functions |> List.map functionEntity |> arrayValue
              "types", value.Types |> List.map semanticType |> arrayValue
              "effects", arrayValue effects
              "capabilities", arrayValue capabilities ]

    let readiness = function
        | TransactionReadiness.SemanticallyReady ->
            objectValue [ "kind", box "semanticallyReady" ]
        | TransactionReadiness.Incomplete obligationIds ->
            objectValue
                [ "kind", box "incomplete"
                  "obligationIds", obligationIds |> List.map (StableId.value >> box) |> arrayValue ]

    let private receipt (value: TransactionPreviewReceipt) =
        objectValue
            [ "transactionId", box (StableId.value value.TransactionId)
              "baseSnapshotId", box (StableId.value value.BaseSnapshotId)
              "baseSnapshotFingerprint", box (SnapshotFingerprint.value value.BaseSnapshotFingerprint)
              "proposedSnapshotId", box (StableId.value value.ProposedSnapshotId)
              "proposedSnapshotFingerprint", box (SnapshotFingerprint.value value.ProposedSnapshotFingerprint)
              "proposedSemanticFingerprint", box (SemanticFingerprint.value value.ProposedSemanticFingerprint) ]

    let private nodeSummary (value: StructuralNodeSummary) =
        objectValue
            [ "nodeId", box (StableId.value value.NodeId)
              "role", box value.Role
              "type", optional value.Type typeRef
              "dependencies", value.Dependencies |> List.map (StableId.value >> box) |> arrayValue
              "valueDependencies",
                value.ValueDependencies
                |> List.map (fun dependency ->
                    objectValue
                        [ "role", box (sprintf "%A" dependency.Role)
                          "nodeId", box (StableId.value dependency.NodeId) ])
                |> arrayValue
              "parameterId", optional value.ParameterId (StableId.value >> box)
              "functionId", optional value.FunctionId (StableId.value >> box)
              "float64Bits", optional value.Float64BitsHex box ]

    let private callSite (value: QueryCallSite) =
        objectValue
            [ "callerFunctionId", box (StableId.value value.CallerFunctionId)
              "callNodeId", box (StableId.value value.CallNodeId)
              "calleeFunctionId", box (StableId.value value.CalleeFunctionId) ]

    let focus (value: FunctionFocusView) =
        objectValue
            [ "snapshotId", box (StableId.value value.SnapshotId)
              "function", functionEntity value.Function
              "nodeTypes",
                value.NodeTypes
                |> List.map (fun item ->
                    objectValue
                        [ "nodeId", box (StableId.value item.NodeId)
                          "type", optional item.Type typeRef ])
                |> arrayValue
              "callers", value.Callers |> List.map callSite |> arrayValue
              "callees", value.Callees |> List.map callSite |> arrayValue
              "openObligations",
                value.OpenObligations
                |> List.map (fun item ->
                    objectValue
                        [ "obligationId", box (StableId.value item.ObligationId)
                          "callNodeId", box (StableId.value item.CallNodeId)
                          "parameterId", box (StableId.value item.ParameterId) ])
                |> arrayValue ]

    let private semanticDiff (value: SemanticDiff) =
        objectValue
            [ "renames", value.Renames |> List.map (sprintf "%A" >> box) |> arrayValue
              "parametersAdded",
                value.ParametersAdded
                |> List.map (fun item ->
                    objectValue
                        [ "ownerFunctionId", box (StableId.value item.OwnerFunctionId)
                          "parameterId", box (StableId.value item.AddedParameterId)
                          "displayName", box item.AddedDisplayName
                          "type", typeRef item.AddedType ])
                |> arrayValue
              "argumentsAttached",
                value.ArgumentsAttached
                |> List.map (fun item ->
                    objectValue
                        [ "callNodeId", box (StableId.value item.CallNodeId)
                          "parameterId", box (StableId.value item.TargetParameterId)
                          "valueNodeId", box (StableId.value item.ValueNodeId) ])
                |> arrayValue
              "obligationsCreated",
                value.ObligationsCreated
                |> List.map (fun item ->
                    objectValue
                        [ "obligationId", box (StableId.value item.ObligationId)
                          "callNodeId", box (StableId.value item.CallNodeId)
                          "parameterId", box (StableId.value item.ParameterId) ])
                |> arrayValue
              "obligationsResolved",
                value.ObligationsResolved
                |> List.map (fun item ->
                    objectValue
                        [ "obligationId", box (StableId.value item.ObligationId)
                          "callNodeId", box (StableId.value item.CallNodeId)
                          "parameterId", box (StableId.value item.ParameterId)
                          "valueNodeId", box (StableId.value item.ValueNodeId) ])
                |> arrayValue
              "requiredEffectsChanged",
                value.FunctionRequiredEffectsChanged |> List.map (sprintf "%A" >> box) |> arrayValue ]

    let private bodyDiff (value: FunctionBodyReplacementDiff) =
        objectValue
            [ "ownerFunctionId", box (StableId.value value.OwnerFunctionId)
              "oldResultNodeId", box (StableId.value value.OldResultNodeId)
              "newResultNodeId", box (StableId.value value.NewResultNodeId)
              "removedNodes", value.RemovedNodes |> List.map nodeSummary |> arrayValue
              "addedNodes", value.AddedNodes |> List.map nodeSummary |> arrayValue ]

    let private targetDiff (value: AgentExpressionStructuralWorkflow.Targeted.ReplacementDiff) =
        objectValue
            [ "ownerFunctionId", box (StableId.value value.OwnerFunctionId)
              "targetNodeId", box (StableId.value value.TargetNodeId)
              "previousTarget", nodeSummary value.PreviousTarget
              "removedNodes", value.RemovedNodes |> List.map nodeSummary |> arrayValue
              "addedNodes", value.AddedNodes |> List.map nodeSummary |> arrayValue
              "addedPatternBindingIds",
                value.AddedPatternBindings
                |> List.map (fun binding -> box (StableId.value binding.Id))
                |> arrayValue
              "retainedSharedDependencies",
                value.RetainedSharedDependencies |> List.map nodeSummary |> arrayValue
              "affectedUses", value.AffectedUses |> List.map (sprintf "%A" >> box) |> arrayValue ]

    let proposal (value: AuthoringProposal) =
        let review =
            match value.Review with
            | AuthoringProposalReview.Operations item ->
                objectValue
                    [ "kind", box "operations"
                      "receipt", receipt item.Receipt
                      "readiness", readiness item.Readiness
                      "diff", semanticDiff item.ResultingChange.Diff
                      "affectedFunctionIds",
                        item.ResultingChange.AffectedFunctionIds
                        |> List.map (StableId.value >> box)
                        |> arrayValue
                      "affectedTypeIds",
                        item.ResultingChange.AffectedTypeIds
                        |> List.map (StableId.value >> box)
                        |> arrayValue
                      "functionFocus", item.ResultingChange.FunctionFocus |> List.map focus |> arrayValue ]
            | AuthoringProposalReview.ReplaceBody item ->
                objectValue
                    [ "kind", box "replaceBody"
                      "receipt", receipt item.Receipt
                      "readiness", readiness item.Readiness
                      "diff", bodyDiff item.ResultingChange
                      "baseFocus", focus item.BaseFocus
                      "proposedFocus", focus item.ProposedFocus ]
            | AuthoringProposalReview.ReplaceTarget item ->
                objectValue
                    [ "kind", box "replaceTarget"
                      "receipt", receipt item.Receipt
                      "readiness", readiness item.Readiness
                      "diff", targetDiff item.ResultingChange
                      "baseFocus", focus item.BaseFocus
                      "proposedFocus", focus item.ProposedFocus ]
            | AuthoringProposalReview.CreateFunction item ->
                objectValue
                    [ "kind", box "createFunction"
                      "receipt", receipt item.Receipt
                      "readiness", readiness item.Readiness
                      "createdFunction", functionEntity item.CreatedFunction
                      "materializedBody", item.MaterializedBody |> List.map nodeSummary |> arrayValue
                      "proposedFocus", focus item.ProposedFocus ]
        objectValue
            [ "proposalId", box value.ProposalId
              "review", review ]

    let status (value: AuthoringSessionStatus) =
        objectValue
            [ "snapshotId", box (StableId.value value.SnapshotId)
              "snapshotFingerprint", box (SnapshotFingerprint.value value.SnapshotFingerprint)
              "semanticFingerprint", box (SemanticFingerprint.value value.SemanticFingerprint) ]

    let commitReceipt (value: AuthoringCommitReceipt) =
        objectValue
            [ "transactionId", box (StableId.value value.TransactionId)
              "publishedSnapshotId", box (StableId.value value.PublishedSnapshotId)
              "readiness", readiness value.Readiness ]

    let rejectionReceipt (value: AuthoringRejectionReceipt) =
        objectValue
            [ "transactionId", box (StableId.value value.TransactionId)
              "retainedSnapshotId", box (StableId.value value.RetainedSnapshotId) ]

    let diagnostics values =
        values
        |> List.map (fun item ->
            objectValue
                [ "code", box item.Code
                  "message", box item.Message ])
        |> arrayValue

    let error value =
        let code, message, details =
            match value with
            | AuthoringSessionError.SnapshotFileNotFound path ->
                "SnapshotFileNotFound", path, None
            | AuthoringSessionError.SnapshotFileAlreadyExists path ->
                "SnapshotFileAlreadyExists", path, None
            | AuthoringSessionError.LockUnavailable path ->
                "LockUnavailable", path, None
            | AuthoringSessionError.IoFailure message ->
                "IoFailure", message, None
            | AuthoringSessionError.SnapshotCodecFailure error ->
                "SnapshotCodecFailure", sprintf "%A" error, None
            | AuthoringSessionError.WorkspaceValidationFailure issues ->
                "WorkspaceValidationFailure", sprintf "%A" issues, None
            | AuthoringSessionError.ProposalNotFound proposalId ->
                "ProposalNotFound", proposalId, None
            | AuthoringSessionError.SessionClosed ->
                "SessionClosed", "The session is closed.", None
            | AuthoringSessionError.ExternalModification ->
                "ExternalModification", "The snapshot file changed outside this session.", None
            | AuthoringSessionError.SessionInvalidated ->
                "SessionInvalidated", "The session was invalidated by external modification.", None
            | AuthoringSessionError.WorkflowSimulationFailure(AgentWorkflowError.SimulationRejected values) ->
                "SimulationRejected",
                "The requested operations were rejected.",
                Some(objectValue [ "diagnostics", diagnostics values ])
            | AuthoringSessionError.WorkflowSimulationFailure other ->
                "WorkflowSimulationFailure", sprintf "%A" other, None
            | AuthoringSessionError.StructuralSimulationFailure(AgentStructuralWorkflowError.SimulationRejected values) ->
                "SimulationRejected",
                "The structural request was rejected.",
                Some(objectValue [ "diagnostics", diagnostics values ])
            | AuthoringSessionError.StructuralSimulationFailure other ->
                "StructuralSimulationFailure", sprintf "%A" other, None
            | AuthoringSessionError.CommitRejected values ->
                "CommitRejected",
                "The reviewed proposal could not be committed.",
                Some(objectValue [ "diagnostics", diagnostics values ])
            | AuthoringSessionError.SemanticProjectionFailure other ->
                "SemanticProjectionFailure", sprintf "%A" other, None
            | AuthoringSessionError.ExecutionLoweringFailure other ->
                "ExecutionLoweringFailure", sprintf "%A" other, None
            | AuthoringSessionError.BackendFailure other ->
                "BackendFailure", sprintf "%A" other, None
        match details with
        | None -> objectValue [ "code", box code; "message", box message ]
        | Some detail ->
            objectValue
                [ "code", box code
                  "message", box message
                  "details", detail ]
