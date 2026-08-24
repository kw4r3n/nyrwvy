namespace Nayamnyam.Core

open System

[<RequireQualifiedAccess>]
module internal ExecutionIrIds =
    let private escape value = Uri.EscapeDataString value

    let private semanticId prefix id =
        prefix + "/" + escape (StableId.value id)

    let nodeValue nodeId =
        ExecutionValueId(semanticId "node" nodeId)

    let parameterValue parameterId =
        ExecutionValueId(semanticId "parameter" parameterId)

    let patternValue patternId =
        ExecutionValueId(semanticId "pattern" patternId)

    let entryBlock functionId =
        ExecutionBlockId(semanticId "function" functionId + "/entry")

    let nodeBlock nodeId role =
        ExecutionBlockId(semanticId "node" nodeId + "/" + role)

    let unionBlock nodeId variantId =
        nodeBlock nodeId ("variant/" + escape (StableId.value variantId))

[<RequireQualifiedAccess>]
module internal ExecutionIrAnalysis =
    let directDependencies (node: TypedSemanticNode): GraphNodeId list =
        match node.Kind with
        | TypedSemanticNodeKind.ParameterReference _
        | TypedSemanticNodeKind.PatternReference _
        | TypedSemanticNodeKind.StringLiteral _
        | TypedSemanticNodeKind.BoolLiteral _
        | TypedSemanticNodeKind.UnitLiteral
        | TypedSemanticNodeKind.OptionNone -> []
        | TypedSemanticNodeKind.StringIsEmpty valueNodeId -> [ valueNodeId ]
        | TypedSemanticNodeKind.StringContains(valueNodeId, substringNodeId) ->
            [ valueNodeId; substringNodeId ]
        | TypedSemanticNodeKind.DirectCall(_, arguments) ->
            arguments |> List.map (fun argument -> argument.ValueNodeId)
        | TypedSemanticNodeKind.CapabilityInvoke(capabilityValueNodeId, _, arguments) ->
            capabilityValueNodeId :: (arguments |> List.map (fun argument -> argument.ValueNodeId))
        | TypedSemanticNodeKind.ProductConstruct fields
        | TypedSemanticNodeKind.UnionConstruct(_, fields) ->
            fields |> List.map (fun field -> field.ValueNodeId)
        | TypedSemanticNodeKind.FieldAccess(sourceNodeId, _) -> [ sourceNodeId ]
        | TypedSemanticNodeKind.OptionSome valueNodeId -> [ valueNodeId ]
        | TypedSemanticNodeKind.ResultOk valueNodeId -> [ valueNodeId ]
        | TypedSemanticNodeKind.ResultErr errorNodeId -> [ errorNodeId ]
        | TypedSemanticNodeKind.Conditional(conditionNodeId, _, _) -> [ conditionNodeId ]
        | TypedSemanticNodeKind.MatchUnion(inputNodeId, _) -> [ inputNodeId ]
        | TypedSemanticNodeKind.MatchOption(inputNodeId, _, _) -> [ inputNodeId ]
        | TypedSemanticNodeKind.MatchResult(inputNodeId, _, _) -> [ inputNodeId ]

    let rec private graphHasEffects
        (functionsById: Map<FunctionId, TypedSemanticFunction>)
        (graph: TypedSemanticGraph)
        : bool =
        graph.Nodes |> List.exists (nodeHasEffects functionsById)

    and private nodeHasEffects
        (functionsById: Map<FunctionId, TypedSemanticFunction>)
        (node: TypedSemanticNode)
        : bool =
        match node.Kind with
        | TypedSemanticNodeKind.CapabilityInvoke _ -> true
        | TypedSemanticNodeKind.DirectCall(functionId, _) ->
            match Map.tryFind functionId functionsById with
            | Some functionEntity -> not (List.isEmpty functionEntity.RequiredEffects)
            | None -> false
        | TypedSemanticNodeKind.Conditional(_, whenTrue, whenFalse) ->
            graphHasEffects functionsById whenTrue || graphHasEffects functionsById whenFalse
        | TypedSemanticNodeKind.MatchUnion(_, arms) ->
            arms |> List.exists (fun arm -> graphHasEffects functionsById arm.Body)
        | TypedSemanticNodeKind.MatchOption(_, someArm, noneBody) ->
            graphHasEffects functionsById someArm.Body || graphHasEffects functionsById noneBody
        | TypedSemanticNodeKind.MatchResult(_, okArm, errArm) ->
            graphHasEffects functionsById okArm.Body || graphHasEffects functionsById errArm.Body
        | TypedSemanticNodeKind.ParameterReference _
        | TypedSemanticNodeKind.PatternReference _
        | TypedSemanticNodeKind.StringLiteral _
        | TypedSemanticNodeKind.BoolLiteral _
        | TypedSemanticNodeKind.UnitLiteral
        | TypedSemanticNodeKind.StringIsEmpty _
        | TypedSemanticNodeKind.StringContains _
        | TypedSemanticNodeKind.ProductConstruct _
        | TypedSemanticNodeKind.FieldAccess _
        | TypedSemanticNodeKind.UnionConstruct _
        | TypedSemanticNodeKind.OptionNone
        | TypedSemanticNodeKind.OptionSome _
        | TypedSemanticNodeKind.ResultOk _
        | TypedSemanticNodeKind.ResultErr _ -> false

    let rec validateEffectOrder
        (functionId: FunctionId)
        (functionsById: Map<FunctionId, TypedSemanticFunction>)
        (graph: TypedSemanticGraph)
        : Result<unit, ExecutionIrError> =
        let localNodes =
            graph.Nodes
            |> List.map (fun node -> node.OriginNodeId, node)
            |> Map.ofList

        let rec dependsOn visiting nodeId targetId =
            if nodeId = targetId then
                true
            elif Set.contains nodeId visiting then
                false
            else
                match Map.tryFind nodeId localNodes with
                | None -> false
                | Some node ->
                    let next = Set.add nodeId visiting
                    directDependencies node
                    |> List.exists (fun dependencyId ->
                        dependencyId = targetId
                        || dependsOn next dependencyId targetId)

        let effectfulNodes =
            graph.Nodes
            |> List.filter (nodeHasEffects functionsById)
            |> List.sortBy (fun node -> StableId.value node.OriginNodeId)

        let rec checkPairs nodes =
            match nodes with
            | []
            | [ _ ] -> Ok()
            | first :: rest ->
                match
                    rest
                    |> List.tryFind (fun second ->
                        not (dependsOn Set.empty first.OriginNodeId second.OriginNodeId)
                        && not (dependsOn Set.empty second.OriginNodeId first.OriginNodeId))
                with
                | Some second ->
                    Error(
                        ExecutionIrError.AmbiguousEffectOrder(
                            functionId,
                            first.OriginNodeId,
                            second.OriginNodeId
                        )
                    )
                | None -> checkPairs rest

        let validateChildren node =
            match node.Kind with
            | TypedSemanticNodeKind.Conditional(_, whenTrue, whenFalse) ->
                match
                    validateEffectOrder functionId functionsById whenTrue,
                    validateEffectOrder functionId functionsById whenFalse
                with
                | Ok(), Ok() -> Ok()
                | Error error, _
                | _, Error error -> Error error
            | TypedSemanticNodeKind.MatchUnion(_, arms) ->
                arms
                |> List.fold
                    (fun state arm ->
                        match state with
                        | Error error -> Error error
                        | Ok() -> validateEffectOrder functionId functionsById arm.Body)
                    (Ok())
            | TypedSemanticNodeKind.MatchOption(_, someArm, noneBody) ->
                match
                    validateEffectOrder functionId functionsById someArm.Body,
                    validateEffectOrder functionId functionsById noneBody
                with
                | Ok(), Ok() -> Ok()
                | Error error, _
                | _, Error error -> Error error
            | TypedSemanticNodeKind.MatchResult(_, okArm, errArm) ->
                match
                    validateEffectOrder functionId functionsById okArm.Body,
                    validateEffectOrder functionId functionsById errArm.Body
                with
                | Ok(), Ok() -> Ok()
                | Error error, _
                | _, Error error -> Error error
            | TypedSemanticNodeKind.ParameterReference _
            | TypedSemanticNodeKind.PatternReference _
            | TypedSemanticNodeKind.StringLiteral _
            | TypedSemanticNodeKind.BoolLiteral _
            | TypedSemanticNodeKind.UnitLiteral
            | TypedSemanticNodeKind.StringIsEmpty _
            | TypedSemanticNodeKind.StringContains _
            | TypedSemanticNodeKind.DirectCall _
            | TypedSemanticNodeKind.CapabilityInvoke _
            | TypedSemanticNodeKind.ProductConstruct _
            | TypedSemanticNodeKind.FieldAccess _
            | TypedSemanticNodeKind.UnionConstruct _
            | TypedSemanticNodeKind.OptionNone
            | TypedSemanticNodeKind.OptionSome _
            | TypedSemanticNodeKind.ResultOk _
            | TypedSemanticNodeKind.ResultErr _ -> Ok()

        match checkPairs effectfulNodes with
        | Error error -> Error error
        | Ok() ->
            graph.Nodes
            |> List.fold
                (fun state node ->
                    match state with
                    | Error error -> Error error
                    | Ok() -> validateChildren node)
                (Ok())
