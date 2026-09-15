namespace NyamNyam.Tools.AuthoringSession

open System
open System.Collections.Generic
open System.Text.Json
open NyamNyam.Core

module private WireJson =
    let obj (pairs: (string * obj) list): obj =
        let value = Dictionary<string, obj>(StringComparer.Ordinal)
        for key, item in pairs do
            value[key] <- item
        box value

    let arr (items: obj list): obj = items |> List.toArray |> box
    let text value = JsonSerializer.Serialize(value)

module private WireInput =
    exception ProtocolInputException of string

    let fail message = raise (ProtocolInputException message)

    let document line =
        try JsonDocument.Parse(line)
        with :? JsonException as error -> fail $"Invalid JSON: {error.Message}"

    let private names (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then fail "Expected a JSON object."
        element.EnumerateObject() |> Seq.map (fun item -> item.Name) |> Set.ofSeq

    let fields context allowed required element =
        let actual = names element
        let unknown = Set.difference actual (Set.ofList allowed) |> Set.toList |> List.sort
        let missing = Set.difference (Set.ofList required) actual |> Set.toList |> List.sort
        if not unknown.IsEmpty then fail $"{context} contains unsupported properties: {String.concat ", " unknown}."
        if not missing.IsEmpty then fail $"{context} is missing required properties: {String.concat ", " missing}."

    let prop name (element: JsonElement) =
        match element.TryGetProperty(name) with
        | true, value -> value
        | false, _ -> fail $"Missing required property '{name}'."

    let tryProp name (element: JsonElement) =
        match element.TryGetProperty(name) with
        | true, value -> Some value
        | false, _ -> None

    let str name element =
        let value = prop name element
        if value.ValueKind <> JsonValueKind.String then fail $"Property '{name}' must be a JSON string."
        value.GetString()

    let bool name element =
        let value = prop name element
        if value.ValueKind <> JsonValueKind.True && value.ValueKind <> JsonValueKind.False then
            fail $"Property '{name}' must be a JSON boolean."
        value.GetBoolean()

    let optionalBool name fallback element =
        match tryProp name element with
        | None -> fallback
        | Some value when value.ValueKind = JsonValueKind.True || value.ValueKind = JsonValueKind.False -> value.GetBoolean()
        | Some _ -> fail $"Property '{name}' must be a JSON boolean."

    let int name element =
        let value = prop name element
        match value.TryGetInt32() with
        | true, result -> result
        | false, _ -> fail $"Property '{name}' must be a 32-bit integer."

    let int64 name element =
        let value = prop name element
        match value.TryGetInt64() with
        | true, result -> result
        | false, _ -> fail $"Property '{name}' must be a 64-bit integer."

    let items name element =
        let value = prop name element
        if value.ValueKind <> JsonValueKind.Array then fail $"Property '{name}' must be a JSON array."
        value.EnumerateArray() |> Seq.toList

    let stable<'Kind> label text: StableId<'Kind> =
        match StableId.tryCreate<'Kind> text with
        | Ok value -> value
        | Error error -> fail $"{label} is not a valid stable ID: {error}."

    let id<'Kind> name element = stable<'Kind> name (str name element)

    let rec typeRef element =
        match str "kind" element with
        | "primitive" ->
            fields "primitive type" [ "kind"; "primitive" ] [ "kind"; "primitive" ] element
            match str "primitive" element with
            | "string" -> TypeRef.Primitive PrimitiveType.String
            | "bool" -> TypeRef.Primitive PrimitiveType.Bool
            | "unit" -> TypeRef.Primitive PrimitiveType.Unit
            | "int64" -> TypeRef.Primitive PrimitiveType.Int64
            | "float64" -> TypeRef.Primitive PrimitiveType.Float64
            | value -> fail $"Unsupported primitive type '{value}'."
        | "named" ->
            fields "named type" [ "kind"; "typeId" ] [ "kind"; "typeId" ] element
            TypeRef.Named(id<TypeTag> "typeId" element)
        | "capability" ->
            fields "capability type" [ "kind"; "capabilityId" ] [ "kind"; "capabilityId" ] element
            TypeRef.Capability(id<CapabilityTag> "capabilityId" element)
        | "builtinGeneric" ->
            fields "builtin generic type" [ "kind"; "generic"; "arguments" ] [ "kind"; "generic"; "arguments" ] element
            let generic =
                match str "generic" element with
                | "option" -> BuiltinGenericType.Option
                | "result" -> BuiltinGenericType.Result
                | "list" -> BuiltinGenericType.List
                | value -> fail $"Unsupported builtin generic '{value}'."
            TypeRef.BuiltinGeneric(generic, items "arguments" element |> List.map typeRef)
        | value -> fail $"Unsupported type kind '{value}'."

    let rec recipe element =
        let binary context constructor =
            fields context [ "kind"; "left"; "right" ] [ "kind"; "left"; "right" ] element
            constructor(recipe (prop "left" element), recipe (prop "right" element))
        let unary context constructor =
            fields context [ "kind"; "value" ] [ "kind"; "value" ] element
            constructor(recipe (prop "value" element))
        let fieldValues name =
            items name element
            |> List.map (fun item ->
                fields "field recipe" [ "fieldId"; "value" ] [ "fieldId"; "value" ] item
                id<FieldTag> "fieldId" item, recipe (prop "value" item))
        let arguments name =
            items name element
            |> List.map (fun item ->
                fields "call argument" [ "parameterId"; "value" ] [ "parameterId"; "value" ] item
                id<ParameterTag> "parameterId" item, recipe (prop "value" item))
        let capabilityArguments name =
            items name element
            |> List.map (fun item ->
                fields "capability argument" [ "capabilityParameterId"; "value" ] [ "capabilityParameterId"; "value" ] item
                id<CapabilityParameterTag> "capabilityParameterId" item, recipe (prop "value" item))

        match str "kind" element with
        | "parameter" ->
            fields "parameter recipe" [ "kind"; "parameterId" ] [ "kind"; "parameterId" ] element
            ExpressionRecipe.Parameter(id<ParameterTag> "parameterId" element)
        | "parameterSlot" ->
            fields "parameter-slot recipe" [ "kind"; "index" ] [ "kind"; "index" ] element
            ExpressionRecipe.ParameterSlot(int "index" element)
        | "patternReference" ->
            fields "pattern-reference recipe" [ "kind"; "scopeDepth"; "slot"; "fieldId" ] [ "kind"; "scopeDepth"; "slot" ] element
            let slot =
                match str "slot" element with
                | "value" -> RecipePatternSlot.Value
                | "error" -> RecipePatternSlot.Error
                | "field" -> RecipePatternSlot.Field(id<FieldTag> "fieldId" element)
                | value -> fail $"Unsupported pattern slot '{value}'."
            ExpressionRecipe.PatternReference { ScopeDepth = int "scopeDepth" element; Slot = slot }
        | "stringLiteral" ->
            fields "string literal recipe" [ "kind"; "value" ] [ "kind"; "value" ] element
            ExpressionRecipe.StringLiteral(str "value" element)
        | "boolLiteral" ->
            fields "bool literal recipe" [ "kind"; "value" ] [ "kind"; "value" ] element
            ExpressionRecipe.BoolLiteral(bool "value" element)
        | "unitLiteral" -> fields "unit literal recipe" [ "kind" ] [ "kind" ] element; ExpressionRecipe.UnitLiteral
        | "int64Literal" ->
            fields "int64 literal recipe" [ "kind"; "value" ] [ "kind"; "value" ] element
            ExpressionRecipe.Int64Literal(int64 "value" element)
        | "float64Literal" ->
            fields "float64 literal recipe" [ "kind"; "bits" ] [ "kind"; "bits" ] element
            match Float64Bits.tryParseHex (str "bits" element) with
            | Ok bits -> ExpressionRecipe.Float64Literal bits
            | Error error -> fail $"Invalid Float64 bits: {error}."
        | "stringIsEmpty" -> unary "string-is-empty recipe" ExpressionRecipe.StringIsEmpty
        | "stringContains" ->
            fields "string-contains recipe" [ "kind"; "value"; "substring" ] [ "kind"; "value"; "substring" ] element
            ExpressionRecipe.StringContains(recipe (prop "value" element), recipe (prop "substring" element))
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
            fields "list-empty recipe" [ "kind"; "itemType" ] [ "kind"; "itemType" ] element
            ExpressionRecipe.ListEmpty(typeRef (prop "itemType" element))
        | "listConstruct" ->
            fields "list-construct recipe" [ "kind"; "itemType"; "items" ] [ "kind"; "itemType"; "items" ] element
            ExpressionRecipe.ListConstruct(typeRef (prop "itemType" element), items "items" element |> List.map recipe)
        | "listLength" -> unary "list-length recipe" ExpressionRecipe.ListLength
        | "listAppendItem" ->
            fields "list-append-item recipe" [ "kind"; "list"; "item" ] [ "kind"; "list"; "item" ] element
            ExpressionRecipe.ListAppendItem(recipe (prop "list" element), recipe (prop "item" element))
        | "listConcat" -> binary "list-concat recipe" ExpressionRecipe.ListConcat
        | "listMap" | "listFilter" | "listAny" | "listAll" as kind ->
            fields $"{kind} recipe" [ "kind"; "source"; "functionId" ] [ "kind"; "source"; "functionId" ] element
            let source = recipe (prop "source" element)
            let functionId = id<FunctionTag> "functionId" element
            match kind with
            | "listMap" -> ExpressionRecipe.ListMap(source, functionId)
            | "listFilter" -> ExpressionRecipe.ListFilter(source, functionId)
            | "listAny" -> ExpressionRecipe.ListAny(source, functionId)
            | _ -> ExpressionRecipe.ListAll(source, functionId)
        | "productConstruct" ->
            fields "product-construct recipe" [ "kind"; "typeId"; "fields" ] [ "kind"; "typeId"; "fields" ] element
            ExpressionRecipe.ProductConstruct(id<TypeTag> "typeId" element, fieldValues "fields")
        | "fieldAccess" ->
            fields "field-access recipe" [ "kind"; "source"; "fieldId" ] [ "kind"; "source"; "fieldId" ] element
            ExpressionRecipe.FieldAccess(recipe (prop "source" element), id<FieldTag> "fieldId" element)
        | "unionConstruct" ->
            fields "union-construct recipe" [ "kind"; "variantId"; "fields" ] [ "kind"; "variantId"; "fields" ] element
            ExpressionRecipe.UnionConstruct(id<VariantTag> "variantId" element, fieldValues "fields")
        | "optionNone" ->
            fields "option-none recipe" [ "kind"; "itemType" ] [ "kind"; "itemType" ] element
            ExpressionRecipe.OptionNone(typeRef (prop "itemType" element))
        | "optionSome" -> unary "option-some recipe" ExpressionRecipe.OptionSome
        | "resultOk" ->
            fields "result-ok recipe" [ "kind"; "errorType"; "value" ] [ "kind"; "errorType"; "value" ] element
            ExpressionRecipe.ResultOk(typeRef (prop "errorType" element), recipe (prop "value" element))
        | "resultErr" ->
            fields "result-err recipe" [ "kind"; "okType"; "error" ] [ "kind"; "okType"; "error" ] element
            ExpressionRecipe.ResultErr(typeRef (prop "okType" element), recipe (prop "error" element))
        | "directCall" ->
            fields "direct-call recipe" [ "kind"; "functionId"; "arguments" ] [ "kind"; "functionId"; "arguments" ] element
            ExpressionRecipe.DirectCall(id<FunctionTag> "functionId" element, arguments "arguments")
        | "capabilityInvoke" ->
            fields "capability-invoke recipe" [ "kind"; "capabilityValue"; "operationId"; "arguments" ] [ "kind"; "capabilityValue"; "operationId"; "arguments" ] element
            ExpressionRecipe.CapabilityInvoke(recipe (prop "capabilityValue" element), id<CapabilityOperationTag> "operationId" element, capabilityArguments "arguments")
        | "conditional" ->
            fields "conditional recipe" [ "kind"; "condition"; "whenTrue"; "whenFalse" ] [ "kind"; "condition"; "whenTrue"; "whenFalse" ] element
            ExpressionRecipe.Conditional(recipe (prop "condition" element), recipe (prop "whenTrue" element), recipe (prop "whenFalse" element))
        | "matchUnion" ->
            fields "match-union recipe" [ "kind"; "input"; "arms" ] [ "kind"; "input"; "arms" ] element
            let arms =
                items "arms" element
                |> List.map (fun arm ->
                    fields "union arm" [ "variantId"; "body" ] [ "variantId"; "body" ] arm
                    id<VariantTag> "variantId" arm, recipe (prop "body" arm))
            ExpressionRecipe.MatchUnion(recipe (prop "input" element), arms)
        | "bind" ->
            fields "bind recipe" [ "kind"; "value"; "body" ] [ "kind"; "value"; "body" ] element
            ExpressionRecipe.Bind(recipe (prop "value" element), recipe (prop "body" element))
        | "matchOption" ->
            fields "match-option recipe" [ "kind"; "input"; "someBody"; "noneBody" ] [ "kind"; "input"; "someBody"; "noneBody" ] element
            ExpressionRecipe.MatchOption(recipe (prop "input" element), recipe (prop "someBody" element), recipe (prop "noneBody" element))
        | "matchResult" ->
            fields "match-result recipe" [ "kind"; "input"; "okBody"; "errBody" ] [ "kind"; "input"; "okBody"; "errBody" ] element
            ExpressionRecipe.MatchResult(recipe (prop "input" element), recipe (prop "okBody" element), recipe (prop "errBody" element))
        | value -> fail $"Unsupported expression recipe kind '{value}'."

    let operation element =
        match str "kind" element with
        | "renameFunction" ->
            fields "rename-function operation" [ "kind"; "functionId"; "displayName" ] [ "kind"; "functionId"; "displayName" ] element
            AgentOperationIntent.RenameDisplayName(AgentRenameTarget.Function(id<FunctionTag> "functionId" element), str "displayName" element)
        | "renameType" ->
            fields "rename-type operation" [ "kind"; "typeId"; "displayName" ] [ "kind"; "typeId"; "displayName" ] element
            AgentOperationIntent.RenameDisplayName(AgentRenameTarget.Type(id<TypeTag> "typeId" element), str "displayName" element)
        | "addFunctionParameter" ->
            fields "add-function-parameter operation" [ "kind"; "functionId"; "displayName"; "type"; "callSites" ] [ "kind"; "functionId"; "displayName"; "type"; "callSites" ] element
            let plans =
                items "callSites" element
                |> List.map (fun plan ->
                    match str "kind" plan with
                    | "bind" ->
                        fields "bind call-site plan" [ "kind"; "callNodeId"; "valueNodeId" ] [ "kind"; "callNodeId"; "valueNodeId" ] plan
                        AgentCallPlan.BindExistingValue(id<GraphNodeTag> "callNodeId" plan, id<GraphNodeTag> "valueNodeId" plan)
                    | "leaveMissing" ->
                        fields "leave-missing call-site plan" [ "kind"; "callNodeId" ] [ "kind"; "callNodeId" ] plan
                        AgentCallPlan.LeaveMissing(id<GraphNodeTag> "callNodeId" plan)
                    | value -> fail $"Unsupported call-site plan '{value}'.")
            AgentOperationIntent.AddFunctionParameter
                { TargetFunctionId = id<FunctionTag> "functionId" element
                  NewParameterDisplayName = str "displayName" element
                  NewParameterType = typeRef (prop "type" element)
                  CallSitePlans = plans }
        | "resolveMissingArgument" ->
            fields "resolve-missing-argument operation" [ "kind"; "obligationId"; "valueNodeId" ] [ "kind"; "obligationId"; "valueNodeId" ] element
            AgentOperationIntent.ResolveMissingArgument
                { TargetObligationId = id<ObligationTag> "obligationId" element
                  ResolutionValueNodeId = id<GraphNodeTag> "valueNodeId" element }
        | "addRequiredEffect" | "removeRequiredEffect" as kind ->
            fields $"{kind} operation" [ "kind"; "functionId"; "effectId" ] [ "kind"; "functionId"; "effectId" ] element
            let intent = { TargetFunctionId = id<FunctionTag> "functionId" element; EffectId = id<EffectTag> "effectId" element }
            if kind = "addRequiredEffect" then AgentOperationIntent.AddFunctionRequiredEffect intent
            else AgentOperationIntent.RemoveFunctionRequiredEffect intent
        | value -> fail $"Unsupported operation kind '{value}'."

module private WireEncode =
    open WireJson

    let optional value encode = match value with | Some item -> encode item | None -> null

    let rec typeRef = function
        | TypeRef.Primitive primitive ->
            let name = match primitive with | PrimitiveType.String -> "string" | PrimitiveType.Bool -> "bool" | PrimitiveType.Unit -> "unit" | PrimitiveType.Int64 -> "int64" | PrimitiveType.Float64 -> "float64"
            obj [ "kind", box "primitive"; "primitive", box name ]
        | TypeRef.Named id -> obj [ "kind", box "named"; "typeId", box (StableId.value id) ]
        | TypeRef.Capability id -> obj [ "kind", box "capability"; "capabilityId", box (StableId.value id) ]
        | TypeRef.BuiltinGeneric(generic, arguments) ->
            let name = match generic with | BuiltinGenericType.Option -> "option" | BuiltinGenericType.Result -> "result" | BuiltinGenericType.List -> "list"
            obj [ "kind", box "builtinGeneric"; "generic", box name; "arguments", arguments |> List.map typeRef |> arr ]

    let parameter (value: SemanticParameter) =
        obj [ "id", box (StableId.value value.Id); "displayName", box value.DisplayName; "type", typeRef value.Type ]

    let functionEntity (value: SemanticFunction) =
        let nodes = ValueGraph.collectNodes value.Body |> List.map (fun node -> obj [ "id", box (StableId.value node.Id); "kind", box (sprintf "%A" node.Kind) ])
        obj
            [ "id", box (StableId.value value.Id)
              "displayName", box value.DisplayName
              "parameters", value.Parameters |> List.map parameter |> arr
              "resultType", typeRef value.ResultType
              "requiredEffects", value.RequiredEffects |> List.map (StableId.value >> box) |> arr
              "body", obj [ "nodes", arr nodes; "resultNodeId", box (StableId.value value.Body.ResultNodeId) ] ]

    let private field (value: SemanticField) = obj [ "id", box (StableId.value value.Id); "displayName", box value.DisplayName; "type", typeRef value.Type ]

    let private semanticType (value: SemanticType) =
        let kind =
            match value.Kind with
            | SemanticTypeKind.Product fields -> obj [ "kind", box "product"; "fields", fields |> List.map field |> arr ]
            | SemanticTypeKind.Union variants ->
                obj
                    [ "kind", box "union"
                      "variants",
                        variants
                        |> List.map (fun variant -> obj [ "id", box (StableId.value variant.Id); "displayName", box variant.DisplayName; "fields", variant.Fields |> List.map field |> arr ])
                        |> arr ]
        obj [ "id", box (StableId.value value.Id); "displayName", box value.DisplayName; "typeKind", kind ]

    let declarations (value: AuthoringDeclarations) =
        let effects = value.Effects |> List.map (fun item -> obj [ "id", box (StableId.value item.Id); "displayName", box item.DisplayName ])
        let capabilities =
            value.Capabilities
            |> List.map (fun capability ->
                let operations =
                    capability.Operations
                    |> List.map (fun operation ->
                        obj
                            [ "id", box (StableId.value operation.Id)
                              "displayName", box operation.DisplayName
                              "effectId", box (StableId.value operation.EffectId)
                              "resultType", typeRef operation.ResultType
                              "failureType", optional operation.FailureType typeRef ])
                obj [ "id", box (StableId.value capability.Id); "displayName", box capability.DisplayName; "operations", arr operations ])
        obj
            [ "functions", value.Functions |> List.map functionEntity |> arr
              "types", value.Types |> List.map semanticType |> arr
              "effects", arr effects
              "capabilities", arr capabilities ]

    let readiness = function
        | TransactionReadiness.SemanticallyReady -> obj [ "kind", box "semanticallyReady" ]
        | TransactionReadiness.Incomplete ids -> obj [ "kind", box "incomplete"; "obligationIds", ids |> List.map (StableId.value >> box) |> arr ]

    let receipt (value: TransactionPreviewReceipt) =
        obj
            [ "transactionId", box (StableId.value value.TransactionId)
              "baseSnapshotId", box (StableId.value value.BaseSnapshotId)
              "baseSnapshotFingerprint", box (SnapshotFingerprint.value value.BaseSnapshotFingerprint)
              "proposedSnapshotId", box (StableId.value value.ProposedSnapshotId)
              "proposedSnapshotFingerprint", box (SnapshotFingerprint.value value.ProposedSnapshotFingerprint)
              "proposedSemanticFingerprint", box (SemanticFingerprint.value value.ProposedSemanticFingerprint) ]

    let nodeSummary (value: StructuralNodeSummary) =
        obj
            [ "nodeId", box (StableId.value value.NodeId)
              "role", box value.Role
              "type", optional value.Type typeRef
              "dependencies", value.Dependencies |> List.map (StableId.value >> box) |> arr
              "parameterId", optional value.ParameterId (StableId.value >> box)
              "functionId", optional value.FunctionId (StableId.value >> box) ]

    let private callSite (value: QueryCallSite) = obj [ "callerFunctionId", box (StableId.value value.CallerFunctionId); "callNodeId", box (StableId.value value.CallNodeId); "calleeFunctionId", box (StableId.value value.CalleeFunctionId) ]

    let focus (value: FunctionFocusView) =
        obj
            [ "snapshotId", box (StableId.value value.SnapshotId)
              "function", functionEntity value.Function
              "nodeTypes", value.NodeTypes |> List.map (fun item -> obj [ "nodeId", box (StableId.value item.NodeId); "type", optional item.Type typeRef ]) |> arr
              "callers", value.Callers |> List.map callSite |> arr
              "callees", value.Callees |> List.map callSite |> arr
              "openObligations", value.OpenObligations |> List.map (fun item -> obj [ "obligationId", box (StableId.value item.ObligationId); "callNodeId", box (StableId.value item.CallNodeId); "parameterId", box (StableId.value item.ParameterId) ]) |> arr ]

    let private semanticDiff (value: SemanticDiff) =
        obj
            [ "renames", value.Renames |> List.map (sprintf "%A" >> box) |> arr
              "parametersAdded", value.ParametersAdded |> List.map (fun item -> obj [ "ownerFunctionId", box (StableId.value item.OwnerFunctionId); "parameterId", box (StableId.value item.AddedParameterId) ]) |> arr
              "argumentsAttached", value.ArgumentsAttached |> List.map (fun item -> obj [ "callNodeId", box (StableId.value item.CallNodeId); "parameterId", box (StableId.value item.TargetParameterId); "valueNodeId", box (StableId.value item.ValueNodeId) ]) |> arr
              "obligationsCreated", value.ObligationsCreated |> List.map (fun item -> obj [ "obligationId", box (StableId.value item.ObligationId); "callNodeId", box (StableId.value item.CallNodeId); "parameterId", box (StableId.value item.ParameterId) ]) |> arr
              "obligationsResolved", value.ObligationsResolved |> List.map (fun item -> obj [ "obligationId", box (StableId.value item.ObligationId); "valueNodeId", box (StableId.value item.ValueNodeId) ]) |> arr
              "requiredEffectsChanged", value.FunctionRequiredEffectsChanged |> List.map (sprintf "%A" >> box) |> arr ]

    let private bodyDiff (value: FunctionBodyReplacementDiff) =
        obj [ "ownerFunctionId", box (StableId.value value.OwnerFunctionId); "oldResultNodeId", box (StableId.value value.OldResultNodeId); "newResultNodeId", box (StableId.value value.NewResultNodeId); "removedNodes", value.RemovedNodes |> List.map nodeSummary |> arr; "addedNodes", value.AddedNodes |> List.map nodeSummary |> arr ]

    let private targetDiff (value: AgentExpressionStructuralWorkflow.Targeted.ReplacementDiff) =
        obj
            [ "ownerFunctionId", box (StableId.value value.OwnerFunctionId)
              "targetNodeId", box (StableId.value value.TargetNodeId)
              "previousTarget", nodeSummary value.PreviousTarget
              "removedNodes", value.RemovedNodes |> List.map nodeSummary |> arr
              "addedNodes", value.AddedNodes |> List.map nodeSummary |> arr
              "addedPatternBindingIds", value.AddedPatternBindings |> List.map (fun item -> box (StableId.value item.Id)) |> arr
              "retainedSharedDependencies", value.RetainedSharedDependencies |> List.map nodeSummary |> arr
              "affectedUses", value.AffectedUses |> List.map (sprintf "%A" >> box) |> arr ]

    let proposal (value: AuthoringProposal) =
        let review =
            match value.Review with
            | AuthoringProposalReview.Operations item ->
                obj [ "kind", box "operations"; "receipt", receipt item.Receipt; "readiness", readiness item.Readiness; "diff", semanticDiff item.ResultingChange.Diff; "affectedFunctionIds", item.ResultingChange.AffectedFunctionIds |> List.map (StableId.value >> box) |> arr; "affectedTypeIds", item.ResultingChange.AffectedTypeIds |> List.map (StableId.value >> box) |> arr ]
            | AuthoringProposalReview.ReplaceBody item ->
                obj [ "kind", box "replaceBody"; "receipt", receipt item.Receipt; "readiness", readiness item.Readiness; "diff", bodyDiff item.ResultingChange; "baseFocus", focus item.BaseFocus; "proposedFocus", focus item.ProposedFocus ]
            | AuthoringProposalReview.ReplaceTarget item ->
                obj [ "kind", box "replaceTarget"; "receipt", receipt item.Receipt; "readiness", readiness item.Readiness; "diff", targetDiff item.ResultingChange; "baseFocus", focus item.BaseFocus; "proposedFocus", focus item.ProposedFocus ]
            | AuthoringProposalReview.CreateFunction item ->
                obj [ "kind", box "createFunction"; "receipt", receipt item.Receipt; "readiness", readiness item.Readiness; "createdFunction", functionEntity item.CreatedFunction; "materializedBody", item.MaterializedBody |> List.map nodeSummary |> arr; "proposedFocus", focus item.ProposedFocus ]
        obj [ "proposalId", box value.ProposalId; "review", review ]

    let status (value: AuthoringSessionStatus) = obj [ "snapshotId", box (StableId.value value.SnapshotId); "snapshotFingerprint", box (SnapshotFingerprint.value value.SnapshotFingerprint); "semanticFingerprint", box (SemanticFingerprint.value value.SemanticFingerprint) ]
    let commit (value: AuthoringCommitReceipt) = obj [ "transactionId", box (StableId.value value.TransactionId); "publishedSnapshotId", box (StableId.value value.PublishedSnapshotId); "readiness", readiness value.Readiness ]
    let reject (value: AuthoringRejectionReceipt) = obj [ "transactionId", box (StableId.value value.TransactionId); "retainedSnapshotId", box (StableId.value value.RetainedSnapshotId) ]
    let diagnostics values = values |> List.map (fun item -> obj [ "code", box item.Code; "message", box item.Message ]) |> arr

    let error (value: AuthoringSessionError) =
        let code, message, detail =
            match value with
            | AuthoringSessionError.SnapshotFileNotFound path -> "SnapshotFileNotFound", path, None
            | AuthoringSessionError.SnapshotFileAlreadyExists path -> "SnapshotFileAlreadyExists", path, None
            | AuthoringSessionError.LockUnavailable path -> "LockUnavailable", path, None
            | AuthoringSessionError.IoFailure message -> "IoFailure", message, None
            | AuthoringSessionError.SnapshotCodecFailure error -> "SnapshotCodecFailure", sprintf "%A" error, None
            | AuthoringSessionError.WorkspaceValidationFailure issues -> "WorkspaceValidationFailure", sprintf "%A" issues, None
            | AuthoringSessionError.ProposalNotFound proposalId -> "ProposalNotFound", proposalId, None
            | AuthoringSessionError.SessionClosed -> "SessionClosed", "The session is closed.", None
            | AuthoringSessionError.ExternalModification -> "ExternalModification", "The snapshot file changed outside this session.", None
            | AuthoringSessionError.SessionInvalidated -> "SessionInvalidated", "The session was invalidated by external modification.", None
            | AuthoringSessionError.WorkflowSimulationFailure(AgentWorkflowError.SimulationRejected values) -> "SimulationRejected", "The requested operations were rejected.", Some(obj [ "diagnostics", diagnostics values ])
            | AuthoringSessionError.WorkflowSimulationFailure error -> "WorkflowSimulationFailure", sprintf "%A" error, None
            | AuthoringSessionError.StructuralSimulationFailure(AgentStructuralWorkflowError.SimulationRejected values) -> "SimulationRejected", "The structural request was rejected.", Some(obj [ "diagnostics", diagnostics values ])
            | AuthoringSessionError.StructuralSimulationFailure error -> "StructuralSimulationFailure", sprintf "%A" error, None
            | AuthoringSessionError.CommitRejected values -> "CommitRejected", "The reviewed proposal could not be committed.", Some(obj [ "diagnostics", diagnostics values ])
            | AuthoringSessionError.SemanticProjectionFailure error -> "SemanticProjectionFailure", sprintf "%A" error, None
            | AuthoringSessionError.ExecutionLoweringFailure error -> "ExecutionLoweringFailure", sprintf "%A" error, None
            | AuthoringSessionError.BackendFailure error -> "BackendFailure", sprintf "%A" error, None
        match detail with
        | None -> obj [ "code", box code; "message", box message ]
        | Some value -> obj [ "code", box code; "message", box message; "details", value ]

[<RequireQualifiedAccess>]
module AuthoringProtocol =
    open WireInput
    open WireJson

    let private ok id result = obj [ "id", box id; "ok", box true; "result", result ] |> text
    let private failResponse id error = obj [ "id", if isNull id then null else box id; "ok", box false; "error", error ] |> text
    let private sessionError id error = failResponse id (WireEncode.error error)

    let private currentSnapshotId (session: AuthoringSession) =
        match session.Status() with
        | Ok status -> status.SnapshotId
        | Error error -> raise (InvalidOperationException(sprintf "%A" error))

    let executeLine (session: AuthoringSession) line =
        let mutable requestId: string = null
        try
            use parsed = document line
            let root = parsed.RootElement
            requestId <- str "id" root
            let opName = str "op" root
            match opName with
            | "status" ->
                fields "status request" [ "id"; "op" ] [ "id"; "op" ] root
                match session.Status() with | Ok value -> ok requestId (WireEncode.status value) | Error error -> sessionError requestId error
            | "declarations" ->
                fields "declarations request" [ "id"; "op" ] [ "id"; "op" ] root
                ok requestId (WireEncode.declarations (session.Declarations()))
            | "focusFunction" ->
                fields "focusFunction request" [ "id"; "op"; "functionId" ] [ "id"; "op"; "functionId" ] root
                match session.FocusFunction(id<FunctionTag> "functionId" root) with
                | Ok value -> ok requestId (WireEncode.focus value)
                | Error error -> failResponse requestId (obj [ "code", box "SemanticQueryFailure"; "message", box (sprintf "%A" error) ])
            | "simulateOperations" ->
                fields "simulateOperations request" [ "id"; "op"; "operations" ] [ "id"; "op"; "operations" ] root
                match session.SimulateOperations(items "operations" root |> List.map operation) with
                | Ok value -> ok requestId (WireEncode.proposal value)
                | Error error -> sessionError requestId error
            | "simulateCreateFunction" ->
                fields "simulateCreateFunction request" [ "id"; "op"; "displayName"; "parameters"; "resultType"; "requiredEffects"; "body" ] [ "id"; "op"; "displayName"; "parameters"; "resultType"; "requiredEffects"; "body" ] root
                let parameters =
                    items "parameters" root
                    |> List.map (fun item ->
                        fields "function parameter" [ "displayName"; "type" ] [ "displayName"; "type" ] item
                        { AgentExpressionStructuralWorkflow.FunctionCreation.ParameterIntent.DisplayName = str "displayName" item
                          Type = typeRef (prop "type" item) })
                let effects =
                    items "requiredEffects" root
                    |> List.map (fun item ->
                        if item.ValueKind <> JsonValueKind.String then WireInput.fail "requiredEffects entries must be stable-ID strings."
                        stable<EffectTag> "requiredEffects" (item.GetString()))
                let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                    { ExpectedBaseSnapshotId = currentSnapshotId session
                      DisplayName = str "displayName" root
                      Parameters = parameters
                      ResultType = typeRef (prop "resultType" root)
                      RequiredEffects = effects
                      Body = recipe (prop "body" root) }
                match session.SimulateCreateFunction request with | Ok value -> ok requestId (WireEncode.proposal value) | Error error -> sessionError requestId error
            | "simulateReplaceBody" ->
                fields "simulateReplaceBody request" [ "id"; "op"; "functionId"; "expectedCurrentResultNodeId"; "replacement" ] [ "id"; "op"; "functionId"; "expectedCurrentResultNodeId"; "replacement" ] root
                let request: AgentReplaceFunctionBodyWithExpressionRecipe =
                    { TargetFunctionId = id<FunctionTag> "functionId" root
                      ExpectedBaseSnapshotId = currentSnapshotId session
                      ExpectedCurrentResultNodeId = id<GraphNodeTag> "expectedCurrentResultNodeId" root
                      Replacement = recipe (prop "replacement" root) }
                match session.SimulateReplaceBody request with | Ok value -> ok requestId (WireEncode.proposal value) | Error error -> sessionError requestId error
            | "simulateReplaceTarget" ->
                fields "simulateReplaceTarget request" [ "id"; "op"; "functionId"; "targetNodeId"; "replacement" ] [ "id"; "op"; "functionId"; "targetNodeId"; "replacement" ] root
                let request: AgentExpressionStructuralWorkflow.Targeted.Request =
                    { TargetFunctionId = id<FunctionTag> "functionId" root
                      ExpectedBaseSnapshotId = currentSnapshotId session
                      TargetNodeId = id<GraphNodeTag> "targetNodeId" root
                      Replacement = recipe (prop "replacement" root) }
                match session.SimulateReplaceTarget request with | Ok value -> ok requestId (WireEncode.proposal value) | Error error -> sessionError requestId error
            | "commit" ->
                fields "commit request" [ "id"; "op"; "proposalId"; "allowIncomplete" ] [ "id"; "op"; "proposalId" ] root
                let policy = if optionalBool "allowIncomplete" false root then CommitPolicy.AllowBlockingObligations else CommitPolicy.RejectBlockingObligations
                match session.Commit(str "proposalId" root, policy) with | Ok value -> ok requestId (WireEncode.commit value) | Error error -> sessionError requestId error
            | "reject" ->
                fields "reject request" [ "id"; "op"; "proposalId" ] [ "id"; "op"; "proposalId" ] root
                match session.Reject(str "proposalId" root) with | Ok value -> ok requestId (WireEncode.reject value) | Error error -> sessionError requestId error
            | "executionReadiness" ->
                fields "executionReadiness request" [ "id"; "op" ] [ "id"; "op" ] root
                match session.ProjectExecution() with
                | Ok value -> ok requestId (obj [ "kind", box "ready"; "sourceSnapshotId", box (StableId.value value.SourceSnapshotId); "functionCount", box value.Functions.Length ])
                | Error error -> sessionError requestId error
            | "emitCSharp" ->
                fields "emitCSharp request" [ "id"; "op"; "profile" ] [ "id"; "op"; "profile" ] root
                let profileValue = prop "profile" root
                let profile =
                    match str "kind" profileValue with
                    | "empty" -> fields "empty C# profile" [ "kind" ] [ "kind" ] profileValue; CSharpBackendProfile.empty
                    | "systemConsoleWriteLine" ->
                        fields "System.Console.WriteLine C# profile" [ "kind"; "operationId" ] [ "kind"; "operationId" ] profileValue
                        CSharpBackendProfile.systemConsoleWriteLine(id<CapabilityOperationTag> "operationId" profileValue)
                    | value -> WireInput.fail $"Unsupported C# profile '{value}'."
                match session.EmitCSharp profile with | Ok artifact -> ok requestId (obj [ "source", box artifact.Source ]) | Error error -> sessionError requestId error
            | value -> failResponse requestId (obj [ "code", box "UnsupportedOperation"; "message", box $"Unsupported operation '{value}'." ])
        with
        | WireInput.ProtocolInputException message -> failResponse requestId (obj [ "code", box "ProtocolError"; "message", box message ])
        | :? InvalidOperationException as error -> failResponse requestId (obj [ "code", box "SessionError"; "message", box error.Message ])
