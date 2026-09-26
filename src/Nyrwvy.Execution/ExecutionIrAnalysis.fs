namespace Nyrwvy.Core

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
        | TypedSemanticNodeKind.Int64Literal _
        | TypedSemanticNodeKind.Float64Literal _
        | TypedSemanticNodeKind.ListEmpty
        | TypedSemanticNodeKind.OptionNone -> []
        | TypedSemanticNodeKind.Int64ToString valueNodeId
        | TypedSemanticNodeKind.StringParseInt64 valueNodeId
        | TypedSemanticNodeKind.StringIsEmpty valueNodeId -> [ valueNodeId ]
        | TypedSemanticNodeKind.StringContains(valueNodeId, substringNodeId) -> [ valueNodeId; substringNodeId ]
        | TypedSemanticNodeKind.StringEqual(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.StringConcat(leftNodeId, rightNodeId) -> [ leftNodeId; rightNodeId ]
        | TypedSemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64Equal(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64LessThan(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64LessThanOrEqual(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64GreaterThan(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64GreaterThanOrEqual(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Float64Add(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Float64Subtract(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Float64Multiply(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Float64Divide(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Float64Equal(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Float64LessThan(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.ListConcat(leftNodeId, rightNodeId) ->
            [ leftNodeId; rightNodeId ]
        | TypedSemanticNodeKind.Float64IsNaN valueNodeId
        | TypedSemanticNodeKind.Float64IsFinite valueNodeId
        | TypedSemanticNodeKind.Float64IsInfinite valueNodeId -> [ valueNodeId ]
        | TypedSemanticNodeKind.ListConstruct itemNodeIds -> itemNodeIds
        | TypedSemanticNodeKind.ListLength listNodeId -> [ listNodeId ]
        | TypedSemanticNodeKind.ListMap(listNodeId, _, context)
        | TypedSemanticNodeKind.ListFilter(listNodeId, _, context)
        | TypedSemanticNodeKind.ListAny(listNodeId, _, context)
        | TypedSemanticNodeKind.ListAll(listNodeId, _, context) -> listNodeId :: (context |> List.map (fun argument -> argument.ValueNodeId))
        | TypedSemanticNodeKind.ListFold(listNodeId, seedNodeId, _, context) ->
            listNodeId :: seedNodeId :: (context |> List.map (fun argument -> argument.ValueNodeId))
        | TypedSemanticNodeKind.ListEffectFold(listNodeId, seedNodeId, _, context) ->
            listNodeId :: seedNodeId :: (context |> List.map (fun argument -> argument.ValueNodeId))
        | TypedSemanticNodeKind.UseResource(acquireNodeId, releaseCapabilityNodeId, _, _, context) ->
            acquireNodeId :: releaseCapabilityNodeId :: (context |> List.map (fun argument -> argument.ValueNodeId))
        | TypedSemanticNodeKind.ParallelAll(sourceListNodeId, _, context) ->
            sourceListNodeId :: (context |> List.map (fun argument -> argument.ValueNodeId))
        | TypedSemanticNodeKind.CancellationRequested -> []
        | TypedSemanticNodeKind.ListAppendItem(listNodeId, itemNodeId) -> [ listNodeId; itemNodeId ]
        | TypedSemanticNodeKind.CapabilityConstruct _
        | TypedSemanticNodeKind.FunctionReference _ -> []
        | TypedSemanticNodeKind.FunctionInvoke(value, arguments) -> value :: arguments
        | TypedSemanticNodeKind.DirectCall(_, arguments) -> arguments |> List.map (fun argument -> argument.ValueNodeId)
        | TypedSemanticNodeKind.CapabilityInvoke(capabilityValueNodeId, _, arguments) ->
            capabilityValueNodeId :: (arguments |> List.map (fun argument -> argument.ValueNodeId))
        | TypedSemanticNodeKind.ProductConstruct fields
        | TypedSemanticNodeKind.UnionConstruct(_, fields) -> fields |> List.map (fun field -> field.ValueNodeId)
        | TypedSemanticNodeKind.FieldAccess(sourceNodeId, _) -> [ sourceNodeId ]
        | TypedSemanticNodeKind.OptionSome valueNodeId -> [ valueNodeId ]
        | TypedSemanticNodeKind.ResultOk valueNodeId -> [ valueNodeId ]
        | TypedSemanticNodeKind.Abort errorNodeId
        | TypedSemanticNodeKind.ResultErr errorNodeId -> [ errorNodeId ]
        | TypedSemanticNodeKind.Conditional(conditionNodeId, _, _) -> [ conditionNodeId ]
        | TypedSemanticNodeKind.MatchUnion(inputNodeId, _) -> [ inputNodeId ]
        | TypedSemanticNodeKind.Bind(inputNodeId, _) -> [ inputNodeId ]
        | TypedSemanticNodeKind.MatchOption(inputNodeId, _, _) -> [ inputNodeId ]
        | TypedSemanticNodeKind.MatchResult(inputNodeId, _, _) -> [ inputNodeId ]

    let rec dependencies (node: TypedSemanticNode): GraphNodeId list =
        let nestedFree =
            match node.Kind with
            | TypedSemanticNodeKind.Conditional(_, whenTrue, whenFalse) ->
                graphFreeReferences whenTrue @ graphFreeReferences whenFalse
            | TypedSemanticNodeKind.MatchUnion(_, arms) ->
                arms |> List.collect (fun (arm: TypedSemanticUnionArm) -> graphFreeReferences arm.Body)
            | TypedSemanticNodeKind.Bind(_, body) -> graphFreeReferences body.Body
            | TypedSemanticNodeKind.MatchOption(_, someArm, noneBody) ->
                graphFreeReferences someArm.Body @ graphFreeReferences noneBody
            | TypedSemanticNodeKind.MatchResult(_, okArm, errArm) ->
                graphFreeReferences okArm.Body @ graphFreeReferences errArm.Body
            | TypedSemanticNodeKind.StringConcat _
            | TypedSemanticNodeKind.StringEqual _
            | TypedSemanticNodeKind.ParameterReference _
            | TypedSemanticNodeKind.PatternReference _
            | TypedSemanticNodeKind.StringLiteral _
            | TypedSemanticNodeKind.BoolLiteral _
            | TypedSemanticNodeKind.UnitLiteral
            | TypedSemanticNodeKind.Int64Literal _
            | TypedSemanticNodeKind.Float64Literal _
            | TypedSemanticNodeKind.StringIsEmpty _
            | TypedSemanticNodeKind.Int64ToString _
            | TypedSemanticNodeKind.StringParseInt64 _
            | TypedSemanticNodeKind.StringContains _
            | TypedSemanticNodeKind.Int64WrappingAdd _
            | TypedSemanticNodeKind.Int64WrappingSubtract _
            | TypedSemanticNodeKind.Int64WrappingMultiply _
            | TypedSemanticNodeKind.Int64Equal _
            | TypedSemanticNodeKind.Int64LessThan _
            | TypedSemanticNodeKind.Int64LessThanOrEqual _
            | TypedSemanticNodeKind.Int64GreaterThan _
            | TypedSemanticNodeKind.Int64GreaterThanOrEqual _
            | TypedSemanticNodeKind.Float64Add _
            | TypedSemanticNodeKind.Float64Subtract _
            | TypedSemanticNodeKind.Float64Multiply _
            | TypedSemanticNodeKind.Float64Divide _
            | TypedSemanticNodeKind.Float64Equal _
            | TypedSemanticNodeKind.Float64LessThan _
            | TypedSemanticNodeKind.Float64IsNaN _
            | TypedSemanticNodeKind.Float64IsFinite _
            | TypedSemanticNodeKind.Float64IsInfinite _
            | TypedSemanticNodeKind.ListEmpty
            | TypedSemanticNodeKind.ListConstruct _
            | TypedSemanticNodeKind.ListLength _
            | TypedSemanticNodeKind.ListAppendItem _
            | TypedSemanticNodeKind.ListConcat _
            | TypedSemanticNodeKind.ListFold _
            | TypedSemanticNodeKind.ListEffectFold _
            | TypedSemanticNodeKind.UseResource _
            | TypedSemanticNodeKind.ParallelAll _
            | TypedSemanticNodeKind.CancellationRequested
            | TypedSemanticNodeKind.ListMap _
            | TypedSemanticNodeKind.ListFilter _
            | TypedSemanticNodeKind.ListAny _
            | TypedSemanticNodeKind.ListAll _
            | TypedSemanticNodeKind.CapabilityConstruct _
            | TypedSemanticNodeKind.FunctionReference _
            | TypedSemanticNodeKind.FunctionInvoke _
            | TypedSemanticNodeKind.DirectCall _
            | TypedSemanticNodeKind.CapabilityInvoke _
            | TypedSemanticNodeKind.ProductConstruct _
            | TypedSemanticNodeKind.FieldAccess _
            | TypedSemanticNodeKind.UnionConstruct _
            | TypedSemanticNodeKind.OptionNone
            | TypedSemanticNodeKind.OptionSome _
            | TypedSemanticNodeKind.ResultOk _
            | TypedSemanticNodeKind.Abort _
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
        | TypedSemanticNodeKind.ListEffectFold(_, _, functionId, _) ->
            match Map.tryFind functionId functionsById with
            | Some (functionEntity: TypedSemanticFunction) -> not (List.isEmpty functionEntity.RequiredEffects)
            | None -> false
        | TypedSemanticNodeKind.UseResource _ -> true
        | TypedSemanticNodeKind.CancellationRequested -> true
        | TypedSemanticNodeKind.ParallelAll(_, functionId, _) ->
            match Map.tryFind functionId functionsById with
            | Some (functionEntity: TypedSemanticFunction) -> not (List.isEmpty functionEntity.RequiredEffects)
            | None -> false
        | TypedSemanticNodeKind.DirectCall(functionId, _) ->
            match Map.tryFind functionId functionsById with
            | Some functionEntity -> not (List.isEmpty functionEntity.RequiredEffects)
            | None -> false
        | TypedSemanticNodeKind.Conditional(_, whenTrue, whenFalse) ->
            graphHasEffects functionsById whenTrue || graphHasEffects functionsById whenFalse
        | TypedSemanticNodeKind.MatchUnion(_, arms) ->
            arms |> List.exists (fun (arm: TypedSemanticUnionArm) -> graphHasEffects functionsById arm.Body)
        | TypedSemanticNodeKind.Bind(_, body) -> graphHasEffects functionsById body.Body
        | TypedSemanticNodeKind.MatchOption(_, someArm, noneBody) ->
            graphHasEffects functionsById someArm.Body || graphHasEffects functionsById noneBody
        | TypedSemanticNodeKind.MatchResult(_, okArm, errArm) ->
            graphHasEffects functionsById okArm.Body || graphHasEffects functionsById errArm.Body
        | TypedSemanticNodeKind.CapabilityConstruct _
        | TypedSemanticNodeKind.FunctionReference _
        | TypedSemanticNodeKind.FunctionInvoke _
        | TypedSemanticNodeKind.ParameterReference _
        | TypedSemanticNodeKind.PatternReference _
        | TypedSemanticNodeKind.StringLiteral _
        | TypedSemanticNodeKind.BoolLiteral _
        | TypedSemanticNodeKind.UnitLiteral
        | TypedSemanticNodeKind.Int64Literal _
        | TypedSemanticNodeKind.Float64Literal _
        | TypedSemanticNodeKind.StringIsEmpty _
        | TypedSemanticNodeKind.Int64ToString _
        | TypedSemanticNodeKind.StringParseInt64 _
        | TypedSemanticNodeKind.StringContains _
        | TypedSemanticNodeKind.StringEqual _
        | TypedSemanticNodeKind.StringConcat _
        | TypedSemanticNodeKind.Int64WrappingAdd _
        | TypedSemanticNodeKind.Int64WrappingSubtract _
        | TypedSemanticNodeKind.Int64WrappingMultiply _
        | TypedSemanticNodeKind.Int64Equal _
        | TypedSemanticNodeKind.Int64LessThan _
        | TypedSemanticNodeKind.Int64LessThanOrEqual _
        | TypedSemanticNodeKind.Int64GreaterThan _
        | TypedSemanticNodeKind.Int64GreaterThanOrEqual _
        | TypedSemanticNodeKind.Float64Add _
        | TypedSemanticNodeKind.Float64Subtract _
        | TypedSemanticNodeKind.Float64Multiply _
        | TypedSemanticNodeKind.Float64Divide _
        | TypedSemanticNodeKind.Float64Equal _
        | TypedSemanticNodeKind.Float64LessThan _
        | TypedSemanticNodeKind.Float64IsNaN _
        | TypedSemanticNodeKind.Float64IsFinite _
        | TypedSemanticNodeKind.Float64IsInfinite _
        | TypedSemanticNodeKind.ListEmpty
        | TypedSemanticNodeKind.ListConstruct _
        | TypedSemanticNodeKind.ListLength _
        | TypedSemanticNodeKind.ListAppendItem _
        | TypedSemanticNodeKind.ListConcat _
        | TypedSemanticNodeKind.ListFold _
        | TypedSemanticNodeKind.ListMap _
        | TypedSemanticNodeKind.ListFilter _
        | TypedSemanticNodeKind.ListAny _
        | TypedSemanticNodeKind.ListAll _
        | TypedSemanticNodeKind.ProductConstruct _
        | TypedSemanticNodeKind.FieldAccess _
        | TypedSemanticNodeKind.UnionConstruct _
        | TypedSemanticNodeKind.OptionNone
        | TypedSemanticNodeKind.OptionSome _
        | TypedSemanticNodeKind.ResultOk _
        | TypedSemanticNodeKind.ResultErr _ -> false
        | TypedSemanticNodeKind.Abort _ -> true

    let rec validateEffectOrder
        (functionId: FunctionId)
        (functionsById: Map<FunctionId, TypedSemanticFunction>)
        (graph: TypedSemanticGraph)
        : Result<unit, ExecutionIrError> =
        let edges = graph.Nodes |> List.map (fun node -> node.OriginNodeId, dependencies node) |> Map.ofList
        let effectfulIds = graph.Nodes |> List.filter (nodeHasEffects functionsById) |> List.map (fun node -> node.OriginNodeId)
        let ordered =
            match GraphOrder.tryUnorderedPair edges effectfulIds with
            | Some(first, second) -> Error(ExecutionIrError.AmbiguousEffectOrder(functionId, first, second))
            | None -> Ok()

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
            | TypedSemanticNodeKind.Bind(_, body) -> validateEffectOrder functionId functionsById body.Body
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
            | TypedSemanticNodeKind.StringConcat _
            | TypedSemanticNodeKind.StringEqual _
            | TypedSemanticNodeKind.ParameterReference _
            | TypedSemanticNodeKind.PatternReference _
            | TypedSemanticNodeKind.StringLiteral _
            | TypedSemanticNodeKind.BoolLiteral _
            | TypedSemanticNodeKind.UnitLiteral
            | TypedSemanticNodeKind.Int64Literal _
            | TypedSemanticNodeKind.Float64Literal _
            | TypedSemanticNodeKind.StringIsEmpty _
            | TypedSemanticNodeKind.Int64ToString _
            | TypedSemanticNodeKind.StringParseInt64 _
            | TypedSemanticNodeKind.StringContains _
            | TypedSemanticNodeKind.Int64WrappingAdd _
            | TypedSemanticNodeKind.Int64WrappingSubtract _
            | TypedSemanticNodeKind.Int64WrappingMultiply _
            | TypedSemanticNodeKind.Int64Equal _
            | TypedSemanticNodeKind.Int64LessThan _
            | TypedSemanticNodeKind.Int64LessThanOrEqual _
            | TypedSemanticNodeKind.Int64GreaterThan _
            | TypedSemanticNodeKind.Int64GreaterThanOrEqual _
            | TypedSemanticNodeKind.Float64Add _
            | TypedSemanticNodeKind.Float64Subtract _
            | TypedSemanticNodeKind.Float64Multiply _
            | TypedSemanticNodeKind.Float64Divide _
            | TypedSemanticNodeKind.Float64Equal _
            | TypedSemanticNodeKind.Float64LessThan _
            | TypedSemanticNodeKind.Float64IsNaN _
            | TypedSemanticNodeKind.Float64IsFinite _
            | TypedSemanticNodeKind.Float64IsInfinite _
            | TypedSemanticNodeKind.ListEmpty
            | TypedSemanticNodeKind.ListConstruct _
            | TypedSemanticNodeKind.ListLength _
            | TypedSemanticNodeKind.ListAppendItem _
            | TypedSemanticNodeKind.ListConcat _
            | TypedSemanticNodeKind.ListFold _
            | TypedSemanticNodeKind.ListEffectFold _
            | TypedSemanticNodeKind.UseResource _
            | TypedSemanticNodeKind.ParallelAll _
            | TypedSemanticNodeKind.CancellationRequested
            | TypedSemanticNodeKind.ListMap _
            | TypedSemanticNodeKind.ListFilter _
            | TypedSemanticNodeKind.ListAny _
            | TypedSemanticNodeKind.ListAll _
            | TypedSemanticNodeKind.CapabilityConstruct _
            | TypedSemanticNodeKind.FunctionReference _
            | TypedSemanticNodeKind.FunctionInvoke _
            | TypedSemanticNodeKind.DirectCall _
            | TypedSemanticNodeKind.CapabilityInvoke _
            | TypedSemanticNodeKind.ProductConstruct _
            | TypedSemanticNodeKind.FieldAccess _
            | TypedSemanticNodeKind.UnionConstruct _
            | TypedSemanticNodeKind.OptionNone
            | TypedSemanticNodeKind.OptionSome _
            | TypedSemanticNodeKind.ResultOk _
            | TypedSemanticNodeKind.Abort _
            | TypedSemanticNodeKind.ResultErr _ -> Ok ()

        match ordered with
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
