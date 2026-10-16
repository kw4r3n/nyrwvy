namespace Nyrwvy.Core

/// Reproducible derived facts over one semantic snapshot.
/// This module never mutates the graph and never invents fallback semantics.
[<RequireQualifiedAccess>]
module SemanticFacts =
    /// Stored nodes execute even if the result does not depend on their values.
    let graphCompletion resolve (graph: ValueGraph) =
        if graph.Nodes |> List.exists (fun node -> resolve node.Id = Some GraphCompletion.Aborts) then
            Some GraphCompletion.Aborts
        else resolve graph.ResultNodeId

    let rec collectPatternBindings (graph: ValueGraph): PatternBinding list =
        graph.Nodes
        |> List.collect (fun node ->
            SemanticNodeShape.nestedScopes node
            |> List.collect (fun scope ->
                (scope.Bindings |> List.map NestedBinding.pattern) @ collectPatternBindings scope.Graph))

    type private SnapshotTypeIndex =
        { Functions: Map<FunctionId, SemanticFunction>
          CapabilityOperations: Map<CapabilityOperationId, SemanticCapabilityOperation>
          ProductFields: Map<FieldId, SemanticField>
          Variants: Map<VariantId, TypeId> }

    let private indexSnapshot (snapshot: Snapshot): SnapshotTypeIndex =
        let functionsById =
            snapshot.Functions
            |> List.map (fun item -> item.Id, item)
            |> Map.ofList

        let capabilityOperationsById: Map<CapabilityOperationId, SemanticCapabilityOperation> =
            snapshot.Capabilities
            |> List.collect (fun capability -> capability.Operations)
            |> List.map (fun operation -> operation.Id, operation)
            |> Map.ofList

        let productFieldsById: Map<FieldId, SemanticField> =
            snapshot.Types
            |> List.collect (fun typeEntity ->
                match typeEntity.Kind with
                | SemanticTypeKind.Product fields -> fields
                | SemanticTypeKind.Union _ -> [])
            |> List.map (fun field -> field.Id, field)
            |> Map.ofList

        let variantsById: Map<VariantId, TypeId> =
            snapshot.Types
            |> List.collect (fun typeEntity ->
                match typeEntity.Kind with
                | SemanticTypeKind.Product _ -> []
                | SemanticTypeKind.Union variants ->
                    variants |> List.map (fun variant -> variant.Id, typeEntity.Id))
            |> Map.ofList

        { Functions = functionsById
          CapabilityOperations = capabilityOperationsById
          ProductFields = productFieldsById
          Variants = variantsById }

    let private createCompletionResolver
        (index: SnapshotTypeIndex)
        (functionEntity: SemanticFunction)
        : GraphNodeId -> GraphCompletion option =
        // These indexes and the memo belong to this function only. In particular,
        // an invalid graph reusing another function's node IDs cannot borrow types.
        let nodesById =
            ValueGraph.collectNodes functionEntity.Body
            |> List.map (fun node -> node.Id, node)
            |> Map.ofList

        let parametersById =
            functionEntity.Parameters
            |> List.map (fun parameter -> parameter.Id, parameter)
            |> Map.ofList

        let patternBindingsById =
            collectPatternBindings functionEntity.Body
            |> List.map (fun binding -> binding.Id, binding)
            |> Map.ofList

        let resolvedTypes = System.Collections.Generic.Dictionary<GraphNodeId, GraphCompletion option>()

        let rec tryNodeType visiting nodeId =
            tryCompletion visiting nodeId |> Option.bind GraphCompletion.normalType

        and tryCompletion (visiting: Set<GraphNodeId>) (nodeId: GraphNodeId): GraphCompletion option =
            match resolvedTypes.TryGetValue nodeId with
            | true, result -> result
            | false, _ ->
                let result =
                    if Set.contains nodeId visiting then None
                    else
                        let next = Set.add nodeId visiting
                        let graph (g: ValueGraph) = graphCompletion (tryCompletion next) g
                        match Map.tryFind nodeId nodesById with
                        | Some { Kind = SemanticNodeKind.Abort _ } -> Some GraphCompletion.Aborts
                        | Some { Kind = SemanticNodeKind.Conditional(_, a, b) } -> GraphCompletion.merge [ graph a; graph b ]
                        | Some { Kind = SemanticNodeKind.MatchUnion(_, arms) } -> arms |> List.map (fun arm -> graph arm.Body) |> GraphCompletion.merge
                        | Some { Kind = SemanticNodeKind.Bind(_, arm) } -> graph arm.Body
                        | Some { Kind = SemanticNodeKind.MatchOption(_, arm, other) } -> GraphCompletion.merge [ graph arm.Body; graph other ]
                        | Some { Kind = SemanticNodeKind.MatchResult(_, a, b) } -> GraphCompletion.merge [ graph a.Body; graph b.Body ]
                        | _ -> inferNodeType visiting nodeId |> Option.map GraphCompletion.Normal
                // Cache failures as well: shared invalid subgraphs otherwise have
                // the same exponential traversal cost as shared valid subgraphs.
                resolvedTypes[nodeId] <- result
                result

        /// A callback's result type with the type arguments its context and iteration values determine.
        and callbackResult (visiting: Set<GraphNodeId>) (callback: SemanticFunction) (context: ArgumentBinding list) (iteration: TypeRef list) =
            let contextPairs =
                context |> List.choose (fun binding ->
                    match callback.Parameters |> List.tryFind (fun parameter -> parameter.Id = binding.TargetParameterId), tryNodeType visiting binding.ValueNodeId with
                    | Some parameter, Some actual -> Some(parameter.Type, actual)
                    | _ -> None)
            let unbound =
                callback.Parameters
                |> List.filter (fun parameter -> not (context |> List.exists (fun binding -> binding.TargetParameterId = parameter.Id)))
                |> List.map (fun parameter -> parameter.Type)
            let iterationPairs = List.zip (List.truncate iteration.Length unbound) (List.truncate unbound.Length iteration)
            match TypeParameters.matchAll (contextPairs @ iterationPairs) with
            | Some substitution -> TypeParameters.substitute substitution callback.ResultType
            | None -> callback.ResultType

        and inferNodeType (visiting: Set<GraphNodeId>) (nodeId: GraphNodeId): TypeRef option =
            if Set.contains nodeId visiting then
                None
            else
                match Map.tryFind nodeId nodesById with
                | None -> None
                | Some node ->
                    let nextVisiting = Set.add nodeId visiting

                    match node.Kind with
                    | SemanticNodeKind.Abort _ -> None
                    | SemanticNodeKind.ParameterReference parameterId ->
                        Map.tryFind parameterId parametersById
                        |> Option.map (fun parameter -> parameter.Type)
                    | SemanticNodeKind.PatternReference bindingId ->
                        Map.tryFind bindingId patternBindingsById
                        |> Option.map (fun binding -> binding.Type)
                    | SemanticNodeKind.StringLiteral _
                    | SemanticNodeKind.StringConcat _
                    | SemanticNodeKind.StringToLower _
                    | SemanticNodeKind.StringToUpper _
                    | SemanticNodeKind.StringReplace _
                    | SemanticNodeKind.StringTrim _
                    | SemanticNodeKind.StringTrimStart _
                    | SemanticNodeKind.StringTrimEnd _
                    | SemanticNodeKind.StringDecodeUtf8Lossy _
                    | SemanticNodeKind.StringJoin _
                    | SemanticNodeKind.StringRepeat _
                    | SemanticNodeKind.StringSlice _ ->
                        Some(TypeRef.Primitive PrimitiveType.String)
                    | SemanticNodeKind.ListRange _ ->
                        Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ TypeRef.Primitive PrimitiveType.Int64 ]))
                    // BoolAnd/BoolOr complete normally even when their right graph aborts,
                    // because the left operand alone can decide the result.
                    | SemanticNodeKind.BoolLiteral _
                    | SemanticNodeKind.BoolNot _
                    | SemanticNodeKind.BoolAnd _
                    | SemanticNodeKind.BoolOr _ ->
                        Some(TypeRef.Primitive PrimitiveType.Bool)
                    | SemanticNodeKind.UnitLiteral ->
                        Some(TypeRef.Primitive PrimitiveType.Unit)
                    | SemanticNodeKind.Int64Literal _
                    | SemanticNodeKind.Int64WrappingAdd _
                    | SemanticNodeKind.Int64WrappingSubtract _
                    | SemanticNodeKind.Int64WrappingMultiply _
                    | SemanticNodeKind.Int64FloorDivide _
                    | SemanticNodeKind.Int64FloorModulo _
                    | SemanticNodeKind.Int64Min _
                    | SemanticNodeKind.Int64Max _
                    | SemanticNodeKind.Int64WrappingAbs _
                    | SemanticNodeKind.StringLength _
                    | SemanticNodeKind.StringUtf8ByteCount _
                    | SemanticNodeKind.StringSpan _
                    | SemanticNodeKind.StringSpanBack _
                    | SemanticNodeKind.ListLength _ ->
                        Some(TypeRef.Primitive PrimitiveType.Int64)
                    | SemanticNodeKind.MapSize _ ->
                        Some(TypeRef.Primitive PrimitiveType.Int64)
                    | SemanticNodeKind.SetSize _ ->
                        Some(TypeRef.Primitive PrimitiveType.Int64)
                    | SemanticNodeKind.Float64Literal _
                    | SemanticNodeKind.Float64Round _
                    | SemanticNodeKind.Float64Add _
                    | SemanticNodeKind.Float64Subtract _
                    | SemanticNodeKind.Float64Multiply _
                    | SemanticNodeKind.Float64Divide _
                    | SemanticNodeKind.Int64ToFloat64 _
                    | SemanticNodeKind.Float64Sqrt _
                    | SemanticNodeKind.Float64Abs _
                    | SemanticNodeKind.Float64Negate _
                    | SemanticNodeKind.Float64Floor _
                    | SemanticNodeKind.Float64Ceiling _
                    | SemanticNodeKind.Float64Truncate _ ->
                        Some(TypeRef.Primitive PrimitiveType.Float64)
                    | SemanticNodeKind.Bits32ToInt64 _ -> Some(TypeRef.Primitive PrimitiveType.Int64)
                    | SemanticNodeKind.Bits32FromInt64 _
                    | SemanticNodeKind.Bits32Xor _
                    | SemanticNodeKind.Bits32And _
                    | SemanticNodeKind.Bits32Or _
                    | SemanticNodeKind.Bits32Not _
                    | SemanticNodeKind.Bits32Add _
                    | SemanticNodeKind.Bits32RotateRight _
                    | SemanticNodeKind.Bits32ShiftRight _
                    | SemanticNodeKind.Bits32ShiftLeft _ ->
                        Some(TypeRef.Primitive PrimitiveType.Bits32)
                    | SemanticNodeKind.Int64ToString _ -> Some(TypeRef.Primitive PrimitiveType.String)
                    | SemanticNodeKind.Int64ToHexString _ -> Some(TypeRef.Primitive PrimitiveType.String)
                    | SemanticNodeKind.StringParseInt64 _ ->
                        Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Primitive PrimitiveType.Int64 ]))
                    | SemanticNodeKind.StringParseInt64Hex _ ->
                        Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Primitive PrimitiveType.Int64 ]))
                    | SemanticNodeKind.Float64ToString _ -> Some(TypeRef.Primitive PrimitiveType.String)
                    | SemanticNodeKind.StringParseFloat64 _ ->
                        Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Primitive PrimitiveType.Float64 ]))
                    | SemanticNodeKind.StringIndexOf _
                    | SemanticNodeKind.StringLastIndexOf _
                    | SemanticNodeKind.StringToCodePoint _
                    | SemanticNodeKind.Int64CheckedAdd _
                    | SemanticNodeKind.Int64CheckedSubtract _
                    | SemanticNodeKind.Int64CheckedMultiply _
                    | SemanticNodeKind.Float64RoundToInt64 _
                    | SemanticNodeKind.Float64FloorToInt64 _ ->
                        Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Primitive PrimitiveType.Int64 ]))
                    | SemanticNodeKind.StringFromCodePoint _ ->
                        Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Primitive PrimitiveType.String ]))
                    | SemanticNodeKind.StringCodePoints _
                    | SemanticNodeKind.StringSplitLines _
                    | SemanticNodeKind.StringSplit _ ->
                        Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ TypeRef.Primitive PrimitiveType.String ]))
                    | SemanticNodeKind.StringIsEmpty _
                    | SemanticNodeKind.StringContains _
                    | SemanticNodeKind.StringStartsWith _
                    | SemanticNodeKind.StringEndsWith _
                    | SemanticNodeKind.StringEqual _
                    | SemanticNodeKind.StringLessThan _
                    | SemanticNodeKind.ValueEqual _
                    | SemanticNodeKind.Int64Equal _
                    | SemanticNodeKind.Int64LessThan _
                    | SemanticNodeKind.Int64LessThanOrEqual _
                    | SemanticNodeKind.Int64GreaterThan _
                    | SemanticNodeKind.Int64GreaterThanOrEqual _
                    | SemanticNodeKind.Float64Equal _
                    | SemanticNodeKind.Float64LessThan _
                    | SemanticNodeKind.Float64IsNaN _
                    | SemanticNodeKind.Float64IsFinite _
                    | SemanticNodeKind.Float64IsInfinite _
                    | SemanticNodeKind.ListAny _
                    | SemanticNodeKind.ListAll _
                    | SemanticNodeKind.MapContainsKey _ ->
                        Some(TypeRef.Primitive PrimitiveType.Bool)
                    | SemanticNodeKind.SetContains _
                    | SemanticNodeKind.SetIsSubset _ ->
                        Some(TypeRef.Primitive PrimitiveType.Bool)
                    | SemanticNodeKind.ListFindIndex _
                    | SemanticNodeKind.ListIndexOf _ ->
                        Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Primitive PrimitiveType.Int64 ]))
                    | SemanticNodeKind.ListLastIndexOf _ ->
                        Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Primitive PrimitiveType.Int64 ]))
                    | SemanticNodeKind.MapEmpty(keyType, valueType) ->
                        Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ keyType; valueType ]))
                    | SemanticNodeKind.MapInsert(sourceMapNodeId, _, _)
                    | SemanticNodeKind.MapRemove(sourceMapNodeId, _)
                    | SemanticNodeKind.MapUnion(sourceMapNodeId, _) ->
                        match tryNodeType nextVisiting sourceMapNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ _; _ ]) as mapType) -> Some mapType
                        | Some _
                        | None -> None
                    | SemanticNodeKind.MapGetOrDefault(sourceMapNodeId, _, _) ->
                        match tryNodeType nextVisiting sourceMapNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ _; valueType ])) -> Some valueType
                        | Some _
                        | None -> None
                    | SemanticNodeKind.MapMapValues(sourceMapNodeId, mapperFunctionId, context) ->
                        match tryNodeType nextVisiting sourceMapNodeId, Map.tryFind mapperFunctionId index.Functions with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ keyType; valueType ])), Some mapper ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ keyType; callbackResult nextVisiting mapper context [ keyType; valueType ] ]))
                        | _ -> None
                    | SemanticNodeKind.ListMinBy(listNodeId, _, _) ->
                        match tryNodeType nextVisiting listNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])) ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ]))
                        | Some _
                        | None -> None
                    | SemanticNodeKind.MapGet(sourceMapNodeId, _) ->
                        match tryNodeType nextVisiting sourceMapNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ _; valueType ])) ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ valueType ]))
                        | Some _
                        | None -> None
                    | SemanticNodeKind.MapFilter(sourceMapNodeId, _, _) ->
                        match tryNodeType nextVisiting sourceMapNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ _; _ ]) as mapType) -> Some mapType
                        | Some _
                        | None -> None
                    | SemanticNodeKind.MapKeys sourceMapNodeId ->
                        match tryNodeType nextVisiting sourceMapNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ keyType; _ ])) ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ keyType ]))
                        | Some _
                        | None -> None
                    | SemanticNodeKind.MapValues sourceMapNodeId ->
                        match tryNodeType nextVisiting sourceMapNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ _; valueType ])) ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ valueType ]))
                        | Some _
                        | None -> None
                    | SemanticNodeKind.SetEmpty itemType ->
                        Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ itemType ]))
                    | SemanticNodeKind.SetInsert(sourceSetNodeId, _)
                    | SemanticNodeKind.SetRemove(sourceSetNodeId, _)
                    | SemanticNodeKind.SetUnion(sourceSetNodeId, _)
                    | SemanticNodeKind.SetIntersection(sourceSetNodeId, _)
                    | SemanticNodeKind.SetDifference(sourceSetNodeId, _) ->
                        match tryNodeType nextVisiting sourceSetNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ _ ]) as setType) -> Some setType
                        | Some _
                        | None -> None
                    | SemanticNodeKind.SetToList sourceSetNodeId ->
                        match tryNodeType nextVisiting sourceSetNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ itemType ])) ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ]))
                        | Some _
                        | None -> None
                    | SemanticNodeKind.SetFromList sourceListNodeId ->
                        match tryNodeType nextVisiting sourceListNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])) ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ itemType ]))
                        | Some _
                        | None -> None
                    | SemanticNodeKind.ListEmpty itemType
                    | SemanticNodeKind.ListConstruct(itemType, _) ->
                        Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ]))
                    | SemanticNodeKind.ListItemAt(listNodeId, _)
                    | SemanticNodeKind.ListLast listNodeId ->
                        match tryNodeType nextVisiting listNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])) ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ]))
                        | Some _
                        | None -> None
                    | SemanticNodeKind.ListAppendItem(listNodeId, _)
                    | SemanticNodeKind.ListSlice(listNodeId, _, _)
                    | SemanticNodeKind.ListReverse listNodeId
                    | SemanticNodeKind.ListDropLast listNodeId
                    | SemanticNodeKind.ListFilter(listNodeId, _, _)
                    | SemanticNodeKind.ListSortBy(listNodeId, _, _) ->
                        match tryNodeType nextVisiting listNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])) ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ]))
                        | Some _
                        | None -> None
                    | SemanticNodeKind.ListConcat(leftListNodeId, rightListNodeId) ->
                        match
                            tryNodeType nextVisiting leftListNodeId,
                            tryNodeType nextVisiting rightListNodeId
                        with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ leftItemType ])),
                          Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ rightItemType ]))
                            when leftItemType = rightItemType ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ leftItemType ]))
                        | _ -> None
                    | SemanticNodeKind.ListFold(_, seedNodeId, _, _) ->
                        tryNodeType nextVisiting seedNodeId
                    | SemanticNodeKind.MapFold(_, seedNodeId, _, _) ->
                        tryNodeType nextVisiting seedNodeId
                    | SemanticNodeKind.ListEffectFold(sourceListNodeId, seedNodeId, folderFunctionId, context) ->
                        // A folder returning Result<S, E> for seed S stops at the first Err.
                        match tryNodeType nextVisiting seedNodeId, Map.tryFind folderFunctionId index.Functions with
                        | Some seedType, Some folder ->
                            let iteration =
                                match tryNodeType nextVisiting sourceListNodeId with
                                | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])) -> [ seedType; itemType ]
                                | _ -> [ seedType ]
                            match callbackResult nextVisiting folder context iteration with
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; _ ]) as resultType when okType = seedType -> Some resultType
                            | _ -> Some seedType
                        | seedType, _ -> seedType
                    | SemanticNodeKind.UseResource(acquireNodeId, _, _, bodyFunctionId, context) ->
                        match Map.tryFind bodyFunctionId index.Functions with
                        | None -> None
                        | Some body ->
                            let resource =
                                match tryNodeType nextVisiting acquireNodeId with
                                | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ resourceType; _ ])) -> [ resourceType ]
                                | _ -> []
                            Some(callbackResult nextVisiting body context resource)
                    | SemanticNodeKind.CancellationRequested -> Some(TypeRef.Primitive PrimitiveType.Bool)
                    | SemanticNodeKind.ExceptionCatch(bodyFunctionId, context, _, _) ->
                        Map.tryFind bodyFunctionId index.Functions
                        |> Option.map (fun body -> callbackResult nextVisiting body context [ TypeRef.Primitive PrimitiveType.Unit ])
                    | SemanticNodeKind.ExceptionIsCancellation _ -> Some(TypeRef.Primitive PrimitiveType.Bool)
                    | SemanticNodeKind.ExceptionMessage _ -> Some(TypeRef.Primitive PrimitiveType.String)
                    | SemanticNodeKind.ExceptionRethrow(_, resultType) -> Some resultType
                    | SemanticNodeKind.CancellationShield(bodyFunctionId, context) ->
                        Map.tryFind bodyFunctionId index.Functions
                        |> Option.map (fun body -> callbackResult nextVisiting body context [ TypeRef.Primitive PrimitiveType.Unit ])
                    | SemanticNodeKind.TaskScope(bodyFunctionId, context) ->
                        Map.tryFind bodyFunctionId index.Functions
                        |> Option.map (fun body -> callbackResult nextVisiting body context [ TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, []) ])
                    | SemanticNodeKind.TaskStart(_, workerFunctionId, context, itemNodeId) ->
                        match tryNodeType nextVisiting itemNodeId, Map.tryFind workerFunctionId index.Functions with
                        | Some itemType, Some worker ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ callbackResult nextVisiting worker context [ itemType ] ]))
                        | _ -> None
                    | SemanticNodeKind.TaskWait(tasksNodeId, _, _) ->
                        match tryNodeType nextVisiting tasksNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ TypeRef.BuiltinGeneric(BuiltinGenericType.Task, [ resultType ]) ])) ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ resultType ]) ]))
                        | _ -> None
                    | SemanticNodeKind.OneShotCreate(_, valueType) -> Some(TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, [ valueType ]))
                    | SemanticNodeKind.OneShotComplete _ -> Some(TypeRef.Primitive PrimitiveType.Bool)
                    | SemanticNodeKind.OneShotWait(cell, _) ->
                        match tryNodeType nextVisiting cell with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.OneShot, [ valueType ])) -> Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ valueType ]))
                        | _ -> None
                    | SemanticNodeKind.TaskCancel _ -> Some(TypeRef.Primitive PrimitiveType.Unit)
                    | SemanticNodeKind.ParallelAll(sourceListNodeId, workerFunctionId, context) ->
                        match tryNodeType nextVisiting sourceListNodeId, Map.tryFind workerFunctionId index.Functions with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])), Some worker ->
                            match callbackResult nextVisiting worker context [ itemType ] with
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]) ->
                                Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ okType ]); errorType ]))
                            | _ -> None
                        | _ -> None
                    | SemanticNodeKind.ListMap(sourceListNodeId, mapperFunctionId, context) ->
                        match tryNodeType nextVisiting sourceListNodeId, Map.tryFind mapperFunctionId index.Functions with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])), Some mapper ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ callbackResult nextVisiting mapper context [ itemType ] ]))
                        | _ -> None
                    | SemanticNodeKind.MapFromList(sourceListNodeId, keyOfFunctionId, context) ->
                        match tryNodeType nextVisiting sourceListNodeId, Map.tryFind keyOfFunctionId index.Functions with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])), Some keyOf ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Map, [ callbackResult nextVisiting keyOf context [ itemType ]; itemType ]))
                        | _ -> None
                    | SemanticNodeKind.CapabilityConstruct(capabilityId, _) -> Some(TypeRef.Capability capabilityId)
                    | SemanticNodeKind.FunctionReference target ->
                        Map.tryFind target index.Functions |> Option.map (fun fn -> TypeRef.Function(List.map (fun (p: SemanticParameter) -> p.Type) fn.Parameters, fn.ResultType))
                    | SemanticNodeKind.FunctionInvoke(value, _) ->
                        match tryNodeType nextVisiting value with
                        | Some(TypeRef.Function(_, result)) -> Some result
                        | _ -> None
                    | SemanticNodeKind.DirectCall(targetFunctionId, arguments) ->
                        Map.tryFind targetFunctionId index.Functions
                        |> Option.map (fun target -> callbackResult nextVisiting target arguments [])
                    | SemanticNodeKind.CapabilityInvoke(_, operationId, _) ->
                        Map.tryFind operationId index.CapabilityOperations
                        |> Option.map (fun operation -> operation.ResultType)
                    | SemanticNodeKind.ProductConstruct(typeId, _)
                    | SemanticNodeKind.ProductUpdate(typeId, _, _) ->
                        Some(TypeRef.Named typeId)
                    | SemanticNodeKind.FieldAccess(_, fieldId) ->
                        Map.tryFind fieldId index.ProductFields
                        |> Option.map (fun field -> field.Type)
                    | SemanticNodeKind.UnionConstruct(variantId, _) ->
                        Map.tryFind variantId index.Variants
                        |> Option.map TypeRef.Named
                    | SemanticNodeKind.OptionNone itemType ->
                        Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ]))
                    | SemanticNodeKind.OptionSome valueNodeId ->
                        tryNodeType nextVisiting valueNodeId
                        |> Option.map (fun valueType ->
                            TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ valueType ]))
                    | SemanticNodeKind.ResultOk(errorType, valueNodeId) ->
                        tryNodeType nextVisiting valueNodeId
                        |> Option.map (fun valueType ->
                            TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ valueType; errorType ]))
                    | SemanticNodeKind.ResultErr(okType, errorNodeId) ->
                        tryNodeType nextVisiting errorNodeId
                        |> Option.map (fun errorType ->
                            TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ]))
                    // Structured results are completion facts, derived by tryCompletion above.
                    | SemanticNodeKind.Conditional _
                    | SemanticNodeKind.MatchUnion _
                    | SemanticNodeKind.MatchOption _
                    | SemanticNodeKind.Bind _
                    | SemanticNodeKind.MatchResult _ -> None

        // Resolve every node after the nodes it reads (its value operands and the nodes of its
        // nested graphs), so the recursion above always finds its operands memoised and its depth
        // no longer grows with the length of a dependency chain, which only the node count bounds.
        // The search keeps its own stack for the same reason. A node reached again while it is
        // being resolved lies on a dependency cycle, which the validator reports; it has no
        // derivable type, so it is memoised as None before anything resolves through it.
        let readsOf (node: SemanticNode) =
            SemanticNodeShape.directValueReferences node
            @ (SemanticNodeShape.nestedGraphs node |> List.collect (fun graph -> graph.Nodes |> List.map (fun nested -> nested.Id)))
        let finished = System.Collections.Generic.HashSet<GraphNodeId>()
        let onPath = System.Collections.Generic.HashSet<GraphNodeId>()
        let frames = System.Collections.Generic.Stack<struct (GraphNodeId * GraphNodeId list)>()
        let enter nodeId =
            match Map.tryFind nodeId nodesById with
            | None -> ()
            | Some node ->
                onPath.Add nodeId |> ignore
                frames.Push(struct (nodeId, readsOf node))
        for KeyValue(rootId, _) in nodesById do
            if not (finished.Contains rootId) then
                enter rootId
                while frames.Count > 0 do
                    let struct (nodeId, remaining) = frames.Pop()
                    match remaining with
                    | [] ->
                        onPath.Remove nodeId |> ignore
                        finished.Add nodeId |> ignore
                        tryCompletion Set.empty nodeId |> ignore
                    | next :: rest ->
                        frames.Push(struct (nodeId, rest))
                        if onPath.Contains next then resolvedTypes[next] <- None
                        elif not (finished.Contains next) then enter next

        // Keep concurrent calls to the public resolver safe without sharing
        // mutable memo state between different function resolvers.
        fun nodeId -> lock resolvedTypes (fun () -> tryCompletion Set.empty nodeId)

    let internal createNodeCompletionResolverFactory (snapshot: Snapshot) =
        let index = indexSnapshot snapshot
        fun functionEntity -> createCompletionResolver index functionEntity

    let createNodeCompletionResolver snapshot functionEntity =
        createNodeCompletionResolverFactory snapshot functionEntity

    /// Builds snapshot-wide indexes once for a validation/projection pass. Each
    /// function gets its own memoized resolver. No cache is keyed by SnapshotId:
    /// edited snapshots may retain an ID while carrying different declarations.
    let internal createNodeTypeResolverFactory
        (snapshot: Snapshot)
        : SemanticFunction -> GraphNodeId -> TypeRef option =
        let index = indexSnapshot snapshot
        fun functionEntity ->
            let resolve = createCompletionResolver index functionEntity
            fun nodeId -> resolve nodeId |> Option.bind GraphCompletion.normalType

    /// Creates one type resolver for a function. The resolver uses only semantic
    /// facts in the supplied snapshot and returns None when the graph is not
    /// sufficiently valid to derive a type, or when the node has no normal completion.
    /// Use createNodeCompletionResolver to distinguish abrupt completion from unknown typing.
    /// Callers must not substitute a fallback.
    let createNodeTypeResolver
        (snapshot: Snapshot)
        (functionEntity: SemanticFunction)
        : GraphNodeId -> TypeRef option =
        createNodeTypeResolverFactory snapshot functionEntity

    let nodeTypes
        (snapshot: Snapshot)
        (functionEntity: SemanticFunction)
        : Map<GraphNodeId, TypeRef option> =
        let resolve = createNodeTypeResolver snapshot functionEntity

        ValueGraph.collectNodes functionEntity.Body
        |> List.map (fun node -> node.Id, resolve node.Id)
        |> Map.ofList
