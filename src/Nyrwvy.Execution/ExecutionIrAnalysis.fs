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
        | TypedSemanticNodeKind.Int64ToHexString valueNodeId
        | TypedSemanticNodeKind.StringParseInt64 valueNodeId
        | TypedSemanticNodeKind.StringParseInt64Hex valueNodeId
        | TypedSemanticNodeKind.Float64ToString valueNodeId
        | TypedSemanticNodeKind.StringParseFloat64 valueNodeId
        | TypedSemanticNodeKind.StringCodePoints valueNodeId
        | TypedSemanticNodeKind.StringToLower valueNodeId
        | TypedSemanticNodeKind.StringDecodeUtf8Lossy valueNodeId
        | TypedSemanticNodeKind.Float64RoundToInt64 valueNodeId
        | TypedSemanticNodeKind.Float64FloorToInt64 valueNodeId
        | TypedSemanticNodeKind.Float64Sqrt valueNodeId
        | TypedSemanticNodeKind.Float64Abs valueNodeId
        | TypedSemanticNodeKind.Float64Negate valueNodeId
        | TypedSemanticNodeKind.Float64Floor valueNodeId
        | TypedSemanticNodeKind.Float64Ceiling valueNodeId
        | TypedSemanticNodeKind.Float64Truncate valueNodeId
        | TypedSemanticNodeKind.StringUtf8ByteCount valueNodeId
        | TypedSemanticNodeKind.StringFromCodePoint valueNodeId
        | TypedSemanticNodeKind.StringToCodePoint valueNodeId
        | TypedSemanticNodeKind.StringLength valueNodeId
        | TypedSemanticNodeKind.StringIsEmpty valueNodeId -> [ valueNodeId ]
        | TypedSemanticNodeKind.StringSlice(valueNodeId, startNodeId, endNodeId) -> [ valueNodeId; startNodeId; endNodeId ]
        | TypedSemanticNodeKind.StringContains(valueNodeId, substringNodeId) -> [ valueNodeId; substringNodeId ]
        | TypedSemanticNodeKind.StringIndexOf(valueNodeId, substringNodeId, fromNodeId) -> [ valueNodeId; substringNodeId; fromNodeId ]
        | TypedSemanticNodeKind.StringSpan(valueNodeId, setNodeId, boundNodeId)
        | TypedSemanticNodeKind.StringSpanBack(valueNodeId, setNodeId, boundNodeId) -> [ valueNodeId; setNodeId; boundNodeId ]
        | TypedSemanticNodeKind.StringLastIndexOf(valueNodeId, otherNodeId)
        | TypedSemanticNodeKind.StringStartsWith(valueNodeId, otherNodeId)
        | TypedSemanticNodeKind.StringEndsWith(valueNodeId, otherNodeId)
        | TypedSemanticNodeKind.StringSplit(valueNodeId, otherNodeId)
        | TypedSemanticNodeKind.StringSplitLines(valueNodeId, otherNodeId) -> [ valueNodeId; otherNodeId ]
        | TypedSemanticNodeKind.StringJoin(listNodeId, separatorNodeId) -> [ listNodeId; separatorNodeId ]
        | TypedSemanticNodeKind.StringEqual(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.StringLessThan(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.ValueEqual(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.StringConcat(leftNodeId, rightNodeId) -> [ leftNodeId; rightNodeId ]
        | TypedSemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64FloorDivide(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64FloorModulo(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64Min(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Int64Max(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.ListRange(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.StringRepeat(leftNodeId, rightNodeId)
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
        | TypedSemanticNodeKind.Bits32Xor(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Bits32And(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Bits32Or(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Bits32Add(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Bits32RotateRight(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Bits32ShiftRight(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.Bits32ShiftLeft(leftNodeId, rightNodeId)
        | TypedSemanticNodeKind.ListConcat(leftNodeId, rightNodeId) ->
            [ leftNodeId; rightNodeId ]
        | TypedSemanticNodeKind.Float64IsNaN valueNodeId
        | TypedSemanticNodeKind.Float64IsFinite valueNodeId
        | TypedSemanticNodeKind.Int64ToFloat64 valueNodeId
        | TypedSemanticNodeKind.Int64WrappingAbs valueNodeId
        | TypedSemanticNodeKind.Bits32FromInt64 valueNodeId
        | TypedSemanticNodeKind.Bits32ToInt64 valueNodeId
        | TypedSemanticNodeKind.Bits32Not valueNodeId
        | TypedSemanticNodeKind.Float64IsInfinite valueNodeId -> [ valueNodeId ]
        | TypedSemanticNodeKind.ListConstruct itemNodeIds -> itemNodeIds
        | TypedSemanticNodeKind.ListLength listNodeId -> [ listNodeId ]
        | TypedSemanticNodeKind.ListReverse listNodeId -> [ listNodeId ]
        | TypedSemanticNodeKind.ListMap(listNodeId, _, context)
        | TypedSemanticNodeKind.ListFilter(listNodeId, _, context)
        | TypedSemanticNodeKind.ListSortBy(listNodeId, _, context)
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
        | TypedSemanticNodeKind.TaskScope(_, context) -> context |> List.map (fun argument -> argument.ValueNodeId)
        | TypedSemanticNodeKind.TaskStart(scopeNodeId, _, context, itemNodeId) ->
            scopeNodeId :: itemNodeId :: (context |> List.map (fun argument -> argument.ValueNodeId))
        | TypedSemanticNodeKind.TaskWait(tasksNodeId, _, limitNodeId) -> tasksNodeId :: Option.toList limitNodeId
        | TypedSemanticNodeKind.TaskCancel taskNodeId -> [ taskNodeId ]
        | TypedSemanticNodeKind.ListAppendItem(listNodeId, itemNodeId) -> [ listNodeId; itemNodeId ]
        | TypedSemanticNodeKind.ListItemAt(listNodeId, indexNodeId) -> [ listNodeId; indexNodeId ]
        | TypedSemanticNodeKind.ListSlice(listNodeId, startNodeId, endNodeId) -> [ listNodeId; startNodeId; endNodeId ]
        | TypedSemanticNodeKind.CapabilityConstruct _
        | TypedSemanticNodeKind.FunctionReference _ -> []
        | TypedSemanticNodeKind.FunctionInvoke(value, arguments) -> value :: arguments
        | TypedSemanticNodeKind.DirectCall(_, arguments) -> arguments |> List.map (fun argument -> argument.ValueNodeId)
        | TypedSemanticNodeKind.CapabilityInvoke(capabilityValueNodeId, _, arguments) ->
            capabilityValueNodeId :: (arguments |> List.map (fun argument -> argument.ValueNodeId))
        | TypedSemanticNodeKind.ProductConstruct fields
        | TypedSemanticNodeKind.UnionConstruct(_, fields) -> fields |> List.map (fun field -> field.ValueNodeId)
        | TypedSemanticNodeKind.FieldAccess(sourceNodeId, _) -> [ sourceNodeId ]
        | TypedSemanticNodeKind.ProductUpdate(sourceNodeId, fields) ->
            sourceNodeId :: (fields |> List.map (fun field -> field.ValueNodeId))
        | TypedSemanticNodeKind.OptionSome valueNodeId -> [ valueNodeId ]
        | TypedSemanticNodeKind.ResultOk valueNodeId -> [ valueNodeId ]
        | TypedSemanticNodeKind.Abort errorNodeId
        | TypedSemanticNodeKind.ResultErr errorNodeId -> [ errorNodeId ]
        | TypedSemanticNodeKind.BoolNot operandNodeId
        | TypedSemanticNodeKind.BoolAnd(operandNodeId, _)
        | TypedSemanticNodeKind.BoolOr(operandNodeId, _) -> [ operandNodeId ]
        | TypedSemanticNodeKind.Conditional(conditionNodeId, _, _) -> [ conditionNodeId ]
        | TypedSemanticNodeKind.MatchUnion(inputNodeId, _) -> [ inputNodeId ]
        | TypedSemanticNodeKind.Bind(inputNodeId, _) -> [ inputNodeId ]
        | TypedSemanticNodeKind.MatchOption(inputNodeId, _, _) -> [ inputNodeId ]
        | TypedSemanticNodeKind.MatchResult(inputNodeId, _, _) -> [ inputNodeId ]

    let rec dependencies (node: TypedSemanticNode): GraphNodeId list =
        let nestedFree = TypedSemanticNodeKind.subgraphs node.Kind |> List.collect graphFreeReferences

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
        | TypedSemanticNodeKind.TaskScope _
        | TypedSemanticNodeKind.TaskStart _
        | TypedSemanticNodeKind.TaskWait _
        | TypedSemanticNodeKind.TaskCancel _ -> true
        | TypedSemanticNodeKind.ParallelAll(_, functionId, _) ->
            match Map.tryFind functionId functionsById with
            | Some (functionEntity: TypedSemanticFunction) -> not (List.isEmpty functionEntity.RequiredEffects)
            | None -> false
        | TypedSemanticNodeKind.DirectCall(functionId, _) ->
            match Map.tryFind functionId functionsById with
            | Some functionEntity -> not (List.isEmpty functionEntity.RequiredEffects)
            | None -> false
        | TypedSemanticNodeKind.Conditional _
        | TypedSemanticNodeKind.BoolAnd _
        | TypedSemanticNodeKind.BoolOr _
        | TypedSemanticNodeKind.MatchUnion _
        | TypedSemanticNodeKind.Bind _
        | TypedSemanticNodeKind.MatchOption _
        | TypedSemanticNodeKind.MatchResult _ ->
            TypedSemanticNodeKind.subgraphs node.Kind |> List.exists (graphHasEffects functionsById)
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
        | TypedSemanticNodeKind.Int64ToHexString _
        | TypedSemanticNodeKind.StringParseInt64 _
        | TypedSemanticNodeKind.StringParseInt64Hex _
        | TypedSemanticNodeKind.Float64ToString _
        | TypedSemanticNodeKind.StringParseFloat64 _
        | TypedSemanticNodeKind.StringCodePoints _
        | TypedSemanticNodeKind.StringToLower _
        | TypedSemanticNodeKind.StringDecodeUtf8Lossy _
        | TypedSemanticNodeKind.Float64RoundToInt64 _
        | TypedSemanticNodeKind.Float64FloorToInt64 _
        | TypedSemanticNodeKind.Float64Sqrt _
        | TypedSemanticNodeKind.Float64Abs _
        | TypedSemanticNodeKind.Float64Negate _
        | TypedSemanticNodeKind.Float64Floor _
        | TypedSemanticNodeKind.Float64Ceiling _
        | TypedSemanticNodeKind.Float64Truncate _
        | TypedSemanticNodeKind.StringUtf8ByteCount _
        | TypedSemanticNodeKind.StringFromCodePoint _
        | TypedSemanticNodeKind.StringToCodePoint _
        | TypedSemanticNodeKind.StringLength _
        | TypedSemanticNodeKind.StringSlice _
        | TypedSemanticNodeKind.BoolNot _
        | TypedSemanticNodeKind.StringContains _
        | TypedSemanticNodeKind.StringIndexOf _
        | TypedSemanticNodeKind.StringSpan _
        | TypedSemanticNodeKind.StringSpanBack _
        | TypedSemanticNodeKind.StringLastIndexOf _
        | TypedSemanticNodeKind.StringStartsWith _
        | TypedSemanticNodeKind.StringEndsWith _
        | TypedSemanticNodeKind.StringJoin _
        | TypedSemanticNodeKind.StringSplit _
        | TypedSemanticNodeKind.StringSplitLines _
        | TypedSemanticNodeKind.StringEqual _
        | TypedSemanticNodeKind.StringLessThan _
        | TypedSemanticNodeKind.ValueEqual _
        | TypedSemanticNodeKind.StringConcat _
        | TypedSemanticNodeKind.Int64WrappingAdd _
        | TypedSemanticNodeKind.Int64WrappingSubtract _
        | TypedSemanticNodeKind.Int64WrappingMultiply _
        | TypedSemanticNodeKind.Int64FloorDivide _
        | TypedSemanticNodeKind.Int64FloorModulo _
        | TypedSemanticNodeKind.Int64Min _
        | TypedSemanticNodeKind.Int64Max _
        | TypedSemanticNodeKind.ListRange _
        | TypedSemanticNodeKind.StringRepeat _
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
        | TypedSemanticNodeKind.Int64ToFloat64 _
        | TypedSemanticNodeKind.Int64WrappingAbs _
        | TypedSemanticNodeKind.Bits32FromInt64 _
        | TypedSemanticNodeKind.Bits32ToInt64 _
        | TypedSemanticNodeKind.Bits32Xor _
        | TypedSemanticNodeKind.Bits32And _
        | TypedSemanticNodeKind.Bits32Or _
        | TypedSemanticNodeKind.Bits32Not _
        | TypedSemanticNodeKind.Bits32Add _
        | TypedSemanticNodeKind.Bits32RotateRight _
        | TypedSemanticNodeKind.Bits32ShiftRight _
        | TypedSemanticNodeKind.Bits32ShiftLeft _
        | TypedSemanticNodeKind.ListEmpty
        | TypedSemanticNodeKind.ListConstruct _
        | TypedSemanticNodeKind.ListLength _
        | TypedSemanticNodeKind.ListReverse _
        | TypedSemanticNodeKind.ListSlice _
        | TypedSemanticNodeKind.ListItemAt _
        | TypedSemanticNodeKind.ListAppendItem _
        | TypedSemanticNodeKind.ListConcat _
        | TypedSemanticNodeKind.ListFold _
        | TypedSemanticNodeKind.ListMap _
        | TypedSemanticNodeKind.ListFilter _
        | TypedSemanticNodeKind.ListSortBy _
        | TypedSemanticNodeKind.ListAny _
        | TypedSemanticNodeKind.ListAll _
        | TypedSemanticNodeKind.ProductConstruct _
        | TypedSemanticNodeKind.FieldAccess _
        | TypedSemanticNodeKind.ProductUpdate _
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

        // The first error in evaluation order, as `subgraphs` lists the nested graphs.
        let validateChildren (node: TypedSemanticNode): Result<unit, ExecutionIrError> =
            TypedSemanticNodeKind.subgraphs node.Kind
            |> List.fold
                (fun result child -> result |> Result.bind (fun () -> validateEffectOrder functionId functionsById child))
                (Ok())

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
