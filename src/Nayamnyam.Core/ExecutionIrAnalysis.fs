namespace Nayamnyam.Core

open System

[<RequireQualifiedAccess>]
module internal ExecutionIrIds =
    let private escape (value: string) = Uri.EscapeDataString value

    let private semanticId prefix id =
        prefix + "/" + escape (StableId.value id)

    let nodeValue nodeId = ExecutionValueId(semanticId "node" nodeId)
    let parameterValue parameterId = ExecutionValueId(semanticId "parameter" parameterId)
    let patternValue patternId = ExecutionValueId(semanticId "pattern" patternId)
    let entryBlock functionId = ExecutionBlockId(semanticId "function" functionId + "/entry")
    let nodeBlock nodeId role = ExecutionBlockId(semanticId "node" nodeId + "/" + role)
    let unionBlock nodeId variantId =
        nodeBlock nodeId ("variant/" + escape (StableId.value variantId))

[<RequireQualifiedAccess>]
module internal ExecutionIrAnalysis =
    let private immediateDependencies (node: TypedSemanticNode): GraphNodeId list =
        match node.Kind with
        | TypedSemanticNodeKind.ParameterReference _
        | TypedSemanticNodeKind.PatternReference _
        | TypedSemanticNodeKind.StringLiteral _
        | TypedSemanticNodeKind.BoolLiteral _
        | TypedSemanticNodeKind.UnitLiteral
        | TypedSemanticNodeKind.OptionNone -> []
        | TypedSemanticNodeKind.StringIsEmpty valueNodeId -> [ valueNodeId ]
        | TypedSemanticNodeKind.StringContains(valueNodeId, substringNodeId) -> [ valueNodeId; substringNodeId ]
        | TypedSemanticNodeKind.DirectCall(_, arguments) -> arguments |> List.map (fun argument -> argument.ValueNodeId)
        | TypedSemanticNodeKind.CapabilityInvoke(capabilityValueNodeId, _, arguments) ->
            capabilityValueNodeId :: (arguments |> List.map (fun argument -> argument.ValueNodeId))
        | TypedSemanticNodeKind.ProductConstruct fields
        | TypedSemanticNodeKind.UnionConstruct(_, fields) -> fields |> List.map (fun field -> field.ValueNodeId)
        | TypedSemanticNodeKind.FieldAccess(sourceNodeId, _) -> [ sourceNodeId ]
        | TypedSemanticNodeKind.OptionSome valueNodeId -> [ valueNodeId ]
        | TypedSemanticNodeKind.ResultOk valueNodeId -> [ valueNodeId ]
        | TypedSemanticNodeKind.ResultErr errorNodeId -> [ errorNodeId ]
        | TypedSemanticNodeKind.Conditional(conditionNodeId, _, _) -> [ conditionNodeId ]
        | TypedSemanticNodeKind.MatchUnion(inputNodeId, _) -> [ inputNodeId ]
        | TypedSemanticNodeKind.MatchOption(inputNodeId, _, _) -> [ inputNodeId ]
        | TypedSemanticNodeKind.MatchResult(inputNodeId, _, _) -> [ inputNodeId ]

    let rec dependencies (node: TypedSemanticNode): GraphNodeId list =
        let nestedFree =
            match node.Kind with
            | TypedSemanticNodeKind.Conditional(_, whenTrue, whenFalse) ->
                graphFreeReferences whenTrue @ graphFreeReferences whenFalse
            | TypedSemanticNodeKind.MatchUnion(_, arms) ->
                arms |> List.collect (fun (arm: TypedSemanticUnionArm) -> graphFreeReferences arm.Body)
            | TypedSemanticNodeKind.MatchOption(_, someArm, noneBody) ->
                graphFreeReferences someArm.Body @ graphFreeReferences noneBody
            | TypedSemanticNodeKind.MatchResult(_, okArm, errArm) ->
                graphFreeReferences okArm.Body @ graphFreeReferences errArm.Body
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
            | TypedSemanticNodeKind.ResultErr _ -> []

        immediateDependencies node @ nestedFree
        |> List.distinct
        |> List.sortBy StableId.value

    and graphFreeReferences (graph: TypedSemanticGraph): GraphNodeId list =
        let localIds = graph.Nodes |> List.map (fun (node: TypedSemanticNode) -> node.OriginNodeId) |> Set.ofList
        graph.Nodes
        |> List.collect dependencies
        |> List.filter (fun nodeId -> not (Set.contains nodeId localIds))
        |> List.distinct
        |> List.sortBy StableId.value

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
            arms |> List.exists (fun (arm: TypedSemanticUnionArm) -> graphHasEffects functionsById arm.Body)
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
        let localNodes: Map<GraphNodeId, TypedSemanticNode> =
            graph.Nodes
            |> List.map (fun (node: TypedSemanticNode) -> node.OriginNodeId, node)
            |> Map.ofList

        let rec dependsOn (visiting: Set<GraphNodeId>) nodeId targetId =
            if nodeId = targetId then
                true
            elif Set.contains nodeId visiting then
                false
            else
                match Map.tryFind nodeId localNodes with
                | None -> false
                | Some node ->
                    let next = Set.add nodeId visiting
                    dependencies node
                    |> List.exists (fun dependencyId ->
                        dependencyId = targetId || dependsOn next dependencyId targetId)

        let effectfulNodes: TypedSemanticNode list =
            graph.Nodes
            |> List.filter (nodeHasEffects functionsById)
            |> List.sortBy (fun node -> StableId.value node.OriginNodeId)

        let rec checkPairs (nodes: TypedSemanticNode list) =
            match nodes with
            | []
            | [ _ ] -> Ok ()
            | first :: rest ->
                let unorderedSecond =
                    rest
                    |> List.tryFind (fun second ->
                        not (dependsOn Set.empty first.OriginNodeId second.OriginNodeId)
                        && not (dependsOn Set.empty second.OriginNodeId first.OriginNodeId))
                match unorderedSecond with
                | Some second ->
                    Error(ExecutionIrError.AmbiguousEffectOrder(functionId, first.OriginNodeId, second.OriginNodeId))
                | None -> checkPairs rest

        let validateChildren (node: TypedSemanticNode): Result<unit, ExecutionIrError> =
            match node.Kind with
            | TypedSemanticNodeKind.Conditional(_, whenTrue, whenFalse) ->
                match validateEffectOrder functionId functionsById whenTrue,
                      validateEffectOrder functionId functionsById whenFalse with
                | Ok (), Ok () -> Ok ()
                | Error error, _
                | _, Error error -> Error error
            | TypedSemanticNodeKind.MatchUnion(_, arms) ->
                let rec loop remaining =
                    match remaining with
                    | [] -> Ok ()
                    | (arm: TypedSemanticUnionArm) :: rest ->
                        match validateEffectOrder functionId functionsById arm.Body with
                        | Error error -> Error error
                        | Ok () -> loop rest
                loop arms
            | TypedSemanticNodeKind.MatchOption(_, someArm, noneBody) ->
                match validateEffectOrder functionId functionsById someArm.Body,
                      validateEffectOrder functionId functionsById noneBody with
                | Ok (), Ok () -> Ok ()
                | Error error, _
                | _, Error error -> Error error
            | TypedSemanticNodeKind.MatchResult(_, okArm, errArm) ->
                match validateEffectOrder functionId functionsById okArm.Body,
                      validateEffectOrder functionId functionsById errArm.Body with
                | Ok (), Ok () -> Ok ()
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
            | TypedSemanticNodeKind.ResultErr _ -> Ok ()

        match checkPairs effectfulNodes with
        | Error error -> Error error
        | Ok () ->
            let rec validateAll remaining =
                match remaining with
                | [] -> Ok ()
                | (node: TypedSemanticNode) :: rest ->
                    match validateChildren node with
                    | Error error -> Error error
                    | Ok () -> validateAll rest
            validateAll graph.Nodes
