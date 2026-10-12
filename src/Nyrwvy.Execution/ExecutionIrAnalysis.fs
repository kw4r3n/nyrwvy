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
    let rec dependencies (node: TypedSemanticNode): GraphNodeId list =
        let nestedFree = TypedSemanticNodeKind.subgraphs node.Kind |> List.collect graphFreeReferences

        TypedSemanticNodeKind.valueReferences node.Kind @ nestedFree
        |> List.distinct
        |> List.sortBy StableId.value

    and graphFreeReferences (graph: TypedSemanticGraph): GraphNodeId list =
        let localIds = graph.Nodes |> List.map (fun (node: TypedSemanticNode) -> node.OriginNodeId) |> Set.ofList
        graph.Nodes
        |> List.collect dependencies
        |> List.filter (fun nodeId -> not (Set.contains nodeId localIds))
        |> List.distinct
        |> List.sortBy StableId.value

    /// Each typed node supplies an intrinsic seed and the named functions it actually runs.
    /// FunctionReference/CapabilityConstruct are values, and pure callbacks are purity-validated.
    let private orderingDescriptor (node: TypedSemanticNode): bool * FunctionId list =
        match node.Kind with
        | TypedSemanticNodeKind.CapabilityInvoke _ -> true, []
        | TypedSemanticNodeKind.ListEffectFold(_, _, functionId, _)
        | TypedSemanticNodeKind.ParallelAll(_, functionId, _)
        | TypedSemanticNodeKind.DirectCall(functionId, _) -> false, [ functionId ]
        | TypedSemanticNodeKind.UseResource(_, _, _, body, _) -> true, [ body ]
        | TypedSemanticNodeKind.CancellationRequested -> true, []
        | TypedSemanticNodeKind.ExceptionCatch(body, _, handler, _) -> true, [ body; handler ]
        | TypedSemanticNodeKind.ExceptionMessage _ -> false, []
        | TypedSemanticNodeKind.ExceptionRethrow _ -> true, []
        | TypedSemanticNodeKind.CancellationShield(body, _) -> true, [ body ]
        | TypedSemanticNodeKind.TaskScope(body, _) -> true, [ body ]
        | TypedSemanticNodeKind.TaskStart(_, worker, _, _) -> true, [ worker ]
        | TypedSemanticNodeKind.TaskWait _
        | TypedSemanticNodeKind.OneShotCreate _
        | TypedSemanticNodeKind.OneShotComplete _
        | TypedSemanticNodeKind.OneShotWait _
        | TypedSemanticNodeKind.TaskCancel _ -> true, []
        | TypedSemanticNodeKind.Conditional _
        | TypedSemanticNodeKind.BoolAnd _
        | TypedSemanticNodeKind.BoolOr _
        | TypedSemanticNodeKind.MatchUnion _
        | TypedSemanticNodeKind.Bind _
        | TypedSemanticNodeKind.MatchOption _
        | TypedSemanticNodeKind.MatchResult _ ->
            false, []
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
        | TypedSemanticNodeKind.StringToUpper _
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
        | TypedSemanticNodeKind.Int64CheckedAdd _
        | TypedSemanticNodeKind.Int64CheckedSubtract _
        | TypedSemanticNodeKind.Int64CheckedMultiply _
        | TypedSemanticNodeKind.ListRange _
        | TypedSemanticNodeKind.StringRepeat _
        | TypedSemanticNodeKind.Float64Round _
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
        | TypedSemanticNodeKind.SetEmpty
        | TypedSemanticNodeKind.MapEmpty
        | TypedSemanticNodeKind.ListConstruct _
        | TypedSemanticNodeKind.ListLength _
        | TypedSemanticNodeKind.MapValues _
        | TypedSemanticNodeKind.MapKeys _
        | TypedSemanticNodeKind.SetToList _
        | TypedSemanticNodeKind.MapSize _
        | TypedSemanticNodeKind.SetFromList _
        | TypedSemanticNodeKind.SetSize _
        | TypedSemanticNodeKind.ListReverse _
        | TypedSemanticNodeKind.ListLast _
        | TypedSemanticNodeKind.ListDropLast _
        | TypedSemanticNodeKind.ListSlice _
        | TypedSemanticNodeKind.MapInsert _
        | TypedSemanticNodeKind.ListItemAt _
        | TypedSemanticNodeKind.MapRemove _
        | TypedSemanticNodeKind.SetInsert _
        | TypedSemanticNodeKind.SetRemove _
        | TypedSemanticNodeKind.SetUnion _
        | TypedSemanticNodeKind.SetIntersection _
        | TypedSemanticNodeKind.SetDifference _
        | TypedSemanticNodeKind.MapContainsKey _
        | TypedSemanticNodeKind.SetContains _
        | TypedSemanticNodeKind.ListIndexOf _
        | TypedSemanticNodeKind.ListLastIndexOf _
        | TypedSemanticNodeKind.MapGet _
        | TypedSemanticNodeKind.MapGetOrDefault _
        | TypedSemanticNodeKind.ListAppendItem _
        | TypedSemanticNodeKind.ListConcat _
        | TypedSemanticNodeKind.ListFold _
        | TypedSemanticNodeKind.MapFold _
        | TypedSemanticNodeKind.ListMap _
        | TypedSemanticNodeKind.MapFromList _
        | TypedSemanticNodeKind.ListFilter _
        | TypedSemanticNodeKind.MapFilter _
        | TypedSemanticNodeKind.ListSortBy _
        | TypedSemanticNodeKind.MapMapValues _
        | TypedSemanticNodeKind.ListMinBy _
        | TypedSemanticNodeKind.ListAny _
        | TypedSemanticNodeKind.ListFindIndex _
        | TypedSemanticNodeKind.ListAll _
        | TypedSemanticNodeKind.ProductConstruct _
        | TypedSemanticNodeKind.FieldAccess _
        | TypedSemanticNodeKind.ProductUpdate _
        | TypedSemanticNodeKind.UnionConstruct _
        | TypedSemanticNodeKind.OptionNone
        | TypedSemanticNodeKind.OptionSome _
        | TypedSemanticNodeKind.ResultOk _
        | TypedSemanticNodeKind.ResultErr _ -> false, []
        | TypedSemanticNodeKind.Abort _ -> true, []

    let observableFunctions (functionsById: Map<FunctionId, TypedSemanticFunction>) =
        let rec nodes (graph: TypedSemanticGraph) =
            graph.Nodes @ (graph.Nodes |> List.collect (fun node -> TypedSemanticNodeKind.subgraphs node.Kind |> List.collect nodes))
        functionsById
        |> Map.map (fun _ fn ->
            let descriptors = nodes fn.Body |> List.map (fun node ->
                let intrinsic, targets = orderingDescriptor node
                // Abort is observable only inside the function whose control flow it exits.
                let seed = match node.Kind with TypedSemanticNodeKind.Abort _ -> false | _ -> intrinsic
                seed, targets)
            (not (List.isEmpty fn.RequiredEffects) || (descriptors |> List.exists fst)),
            (descriptors |> List.collect snd |> List.distinct))
        |> GraphOrder.observableClosure

    let rec private nodeHasEffects observable (node: TypedSemanticNode) =
        let intrinsic, targets = orderingDescriptor node
        intrinsic
        || (targets |> List.exists (fun id -> Set.contains id observable))
        || (TypedSemanticNodeKind.subgraphs node.Kind
            |> List.exists (fun graph -> graph.Nodes |> List.exists (nodeHasEffects observable)))

    let validateEffectOrder
        (functionId: FunctionId)
        (observable: Set<FunctionId>)
        (graph: TypedSemanticGraph)
        : Result<unit, ExecutionIrError> =
        let rec validate (graph: TypedSemanticGraph) =
            let edges = graph.Nodes |> List.map (fun node -> node.OriginNodeId, dependencies node) |> Map.ofList
            let effectfulIds = graph.Nodes |> List.filter (nodeHasEffects observable) |> List.map (fun node -> node.OriginNodeId)
            let ordered =
                match GraphOrder.tryUnorderedPair edges effectfulIds with
                | Some(first, second) -> Error(ExecutionIrError.AmbiguousEffectOrder(functionId, first, second))
                | None -> Ok()
            // Nested scopes retain their own dependencies: enclosing Bind/branch edges own order.
            graph.Nodes
            |> List.collect (fun node -> TypedSemanticNodeKind.subgraphs node.Kind)
            |> List.fold (fun result child -> result |> Result.bind (fun () -> validate child)) ordered
        validate graph
