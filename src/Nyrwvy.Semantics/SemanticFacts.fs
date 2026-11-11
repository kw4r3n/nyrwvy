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
          /// The Product that owns each Product field.
          ProductFieldOwners: Map<FieldId, SemanticType>
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

        let productFieldOwners: Map<FieldId, SemanticType> =
            snapshot.Types
            |> List.collect (fun typeEntity ->
                match typeEntity.Kind with
                | SemanticTypeKind.Product fields -> fields |> List.map (fun field -> field.Id, typeEntity)
                | SemanticTypeKind.Union _ -> [])
            |> Map.ofList

        { Functions = functionsById
          CapabilityOperations = capabilityOperationsById
          ProductFields = productFieldsById
          ProductFieldOwners = productFieldOwners
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
                    | SemanticNodeKind.StringEncodeUtf8 _
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
                    | SemanticNodeKind.Int64BitAnd _
                    | SemanticNodeKind.Int64BitOr _
                    | SemanticNodeKind.Int64BitXor _
                    | SemanticNodeKind.Int64ShiftLeft _
                    | SemanticNodeKind.Int64ShiftRight _
                    | SemanticNodeKind.Int64WrappingAbs _
                    | SemanticNodeKind.Int64BitNot _
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
                    | SemanticNodeKind.Float64Min _
                    | SemanticNodeKind.Float64Max _
                    | SemanticNodeKind.Float64Subtract _
                    | SemanticNodeKind.Float64Multiply _
                    | SemanticNodeKind.Float64Divide _
                    | SemanticNodeKind.Float64FloorModulo _
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
                    | SemanticNodeKind.BytesEmpty
                    | SemanticNodeKind.BytesFill _
                    | SemanticNodeKind.BytesSet _
                    | SemanticNodeKind.BytesAppend _
                    | SemanticNodeKind.BytesConcat _
                    | SemanticNodeKind.BytesSlice _
                    | SemanticNodeKind.BytesFromLatin1 _ ->
                        Some(TypeRef.Primitive PrimitiveType.Bytes)
                    | SemanticNodeKind.BytesLength _ -> Some(TypeRef.Primitive PrimitiveType.Int64)
                    | SemanticNodeKind.BytesGet _ ->
                        Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Primitive PrimitiveType.Int64 ]))
                    | SemanticNodeKind.BytesToLatin1 _ -> Some(TypeRef.Primitive PrimitiveType.String)
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
                    | SemanticNodeKind.StringLastIndexOfBefore _
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
                    | SemanticNodeKind.ListContains _
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
                    | SemanticNodeKind.OptionGetOrDefault(optionNodeId, _) ->
                        match tryNodeType nextVisiting optionNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])) -> Some itemType
                        | _ -> None
                    | SemanticNodeKind.ResultGetOrDefault(resultNodeId, _) ->
                        match tryNodeType nextVisiting resultNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; _ ])) -> Some okType
                        | _ -> None
                    | SemanticNodeKind.OptionOkOr(optionNodeId, errorNodeId) ->
                        match tryNodeType nextVisiting optionNodeId, tryNodeType nextVisiting errorNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])), Some errorType ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ itemType; errorType ]))
                        | _ -> None
                    | SemanticNodeKind.ResultOkValue resultNodeId ->
                        match tryNodeType nextVisiting resultNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; _ ])) ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ okType ]))
                        | _ -> None
                    | SemanticNodeKind.ResultErrValue resultNodeId ->
                        match tryNodeType nextVisiting resultNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ _; errorType ])) ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ errorType ]))
                        | _ -> None
                    | SemanticNodeKind.OptionIsSome _
                    | SemanticNodeKind.ResultIsOk _ ->
                        Some(TypeRef.Primitive PrimitiveType.Bool)
                    | SemanticNodeKind.OptionMap(sourceNodeId, mapperFunctionId, context) ->
                        match tryNodeType nextVisiting sourceNodeId, Map.tryFind mapperFunctionId index.Functions with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])), Some mapper ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ callbackResult nextVisiting mapper context [ itemType ] ]))
                        | _ -> None
                    | SemanticNodeKind.OptionAndThen(sourceNodeId, binderFunctionId, context) ->
                        match tryNodeType nextVisiting sourceNodeId, Map.tryFind binderFunctionId index.Functions with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ])), Some binder ->
                            Some(callbackResult nextVisiting binder context [ itemType ])
                        | _ -> None
                    | SemanticNodeKind.ResultMap(sourceNodeId, mapperFunctionId, context) ->
                        match tryNodeType nextVisiting sourceNodeId, Map.tryFind mapperFunctionId index.Functions with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])), Some mapper ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ callbackResult nextVisiting mapper context [ okType ]; errorType ]))
                        | _ -> None
                    | SemanticNodeKind.ResultMapErr(sourceNodeId, mapperFunctionId, context) ->
                        match tryNodeType nextVisiting sourceNodeId, Map.tryFind mapperFunctionId index.Functions with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])), Some mapper ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; callbackResult nextVisiting mapper context [ errorType ] ]))
                        | _ -> None
                    | SemanticNodeKind.ResultAndThen(sourceNodeId, binderFunctionId, context) ->
                        match tryNodeType nextVisiting sourceNodeId, Map.tryFind binderFunctionId index.Functions with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; _ ])), Some binder ->
                            Some(callbackResult nextVisiting binder context [ okType ])
                        | _ -> None
                    | SemanticNodeKind.ListMinBy(listNodeId, _, _) ->
                        match tryNodeType nextVisiting listNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])) ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ itemType ]))
                        | Some _
                        | None -> None
                    | SemanticNodeKind.ListMaxBy(listNodeId, _, _) ->
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
                    | SemanticNodeKind.SetFilter(sourceSetNodeId, _, _) ->
                        match tryNodeType nextVisiting sourceSetNodeId with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ _ ]) as setType) -> Some setType
                        | Some _
                        | None -> None
                    | SemanticNodeKind.SetMap(sourceSetNodeId, mapperFunctionId, context) ->
                        match tryNodeType nextVisiting sourceSetNodeId, Map.tryFind mapperFunctionId index.Functions with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ itemType ])), Some mapper ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Set, [ callbackResult nextVisiting mapper context [ itemType ] ]))
                        | _ -> None
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
                    | SemanticNodeKind.ListSetAt(listNodeId, _, _)
                    | SemanticNodeKind.ListInsertAt(listNodeId, _, _)
                    | SemanticNodeKind.ListRemoveAt(listNodeId, _)
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
                    | SemanticNodeKind.ListScan(_, seedNodeId, _, _) ->
                        // The seed followed by every intermediate state: a List of the seed type.
                        tryNodeType nextVisiting seedNodeId
                        |> Option.map (fun seedType -> TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ seedType ]))
                    | SemanticNodeKind.MapFold(_, seedNodeId, _, _)
                    | SemanticNodeKind.SetFold(_, seedNodeId, _, _) ->
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
                    | SemanticNodeKind.ListZipWith(leftListNodeId, rightListNodeId, combinerFunctionId, context) ->
                        match tryNodeType nextVisiting leftListNodeId, tryNodeType nextVisiting rightListNodeId, Map.tryFind combinerFunctionId index.Functions with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ leftItemType ])),
                          Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ rightItemType ])),
                          Some combiner ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ callbackResult nextVisiting combiner context [ leftItemType; rightItemType ] ]))
                        | _ -> None
                    | SemanticNodeKind.ListFlatMap(sourceListNodeId, mapperFunctionId, context) ->
                        // The mapper returns List<R>; the node is that same List<R>.
                        match tryNodeType nextVisiting sourceListNodeId, Map.tryFind mapperFunctionId index.Functions with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ])), Some mapper ->
                            match callbackResult nextVisiting mapper context [ itemType ] with
                            | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ _ ]) as resultType -> Some resultType
                            | _ -> None
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
                    | SemanticNodeKind.ProductConstruct(typeId, typeArguments, _) -> Some(TypeRef.Named(typeId, typeArguments))
                    // An update keeps its source's type, including its type arguments.
                    | SemanticNodeKind.ProductUpdate(typeId, sourceNodeId, _) ->
                        match tryNodeType nextVisiting sourceNodeId with
                        | Some(TypeRef.Named(sourceTypeId, _) as sourceType) when sourceTypeId = typeId -> Some sourceType
                        | _ -> Some(TypeRef.Named(typeId, []))
                    // A field of a generic Product reads at its source's type arguments.
                    | SemanticNodeKind.FieldAccess(sourceNodeId, fieldId) ->
                        match Map.tryFind fieldId index.ProductFields, Map.tryFind fieldId index.ProductFieldOwners with
                        | Some field, Some owner when List.isEmpty owner.TypeParameters -> Some field.Type
                        | Some field, Some owner ->
                            match tryNodeType nextVisiting sourceNodeId with
                            | Some(TypeRef.Named(sourceTypeId, arguments)) when sourceTypeId = owner.Id ->
                                Some(TypeParameters.instantiate owner arguments field.Type)
                            | _ -> None
                        | _ -> None
                    | SemanticNodeKind.UnionConstruct(variantId, typeArguments, _) ->
                        Map.tryFind variantId index.Variants
                        |> Option.map (fun typeId -> TypeRef.Named(typeId, typeArguments))
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

/// Moved from StructuralContracts.fs (removed with the authoring layer, audit #7): the Zig
/// host's dependency roles are defined by ValueDependency.ofNode, so it stays with the facts.
[<RequireQualifiedAccess>]
type ValueDependencyRole =
    | Value
    | Error
    | Substring
    | SliceStart
    | SliceEnd
    | SearchFrom
    | CharacterSet
    | Prefix
    | Suffix
    | Separator
    /// The text StringReplace puts in place of each match.
    | Replacement
    | LeftOperand
    | RightOperand
    | Source
    | Item
    | TaskScope
    | Limit
    | Index
    | Key
    /// The Int64 rotation or shift amount of a Bits32 operation.
    | Amount
    | ListItem of int
    | FunctionValue
    | FunctionArgument of int
    | CallArgument of ParameterId
    | CapabilityValue
    | CapabilityArgument of CapabilityParameterId
    | FieldValue of FieldId
    | BranchCondition
    | MatchInput
    | BindInput

type ValueDependency =
    { Role: ValueDependencyRole
      NodeId: GraphNodeId }

[<RequireQualifiedAccess>]
module ValueDependency =
    let private create role nodeId =
        { Role = role
          NodeId = nodeId }

    let private contextDependencies (context: ArgumentBinding list) =
        context |> List.map (fun binding -> create (ValueDependencyRole.CallArgument binding.TargetParameterId) binding.ValueNodeId)

    /// Canonical role-aware direct value dependencies for one semantic node.
    /// Nested branch result nodes remain represented by their child ValueGraphs.
    let ofNode (node: SemanticNode): ValueDependency list =
        match node.Kind with
        | SemanticNodeKind.StringIsEmpty sourceNodeId
        | SemanticNodeKind.Int64ToString sourceNodeId
        | SemanticNodeKind.Int64ToHexString sourceNodeId
        | SemanticNodeKind.Int64ToFloat64 sourceNodeId
        | SemanticNodeKind.Int64WrappingAbs sourceNodeId
        | SemanticNodeKind.Int64BitNot sourceNodeId
        | SemanticNodeKind.Bits32FromInt64 sourceNodeId
        | SemanticNodeKind.Bits32ToInt64 sourceNodeId
        | SemanticNodeKind.Bits32Not sourceNodeId
        | SemanticNodeKind.BytesLength sourceNodeId
        | SemanticNodeKind.BytesFromLatin1 sourceNodeId
        | SemanticNodeKind.BytesToLatin1 sourceNodeId
        | SemanticNodeKind.StringParseInt64 sourceNodeId
        | SemanticNodeKind.StringParseInt64Hex sourceNodeId
        | SemanticNodeKind.Float64ToString sourceNodeId
        | SemanticNodeKind.StringParseFloat64 sourceNodeId
        | SemanticNodeKind.StringCodePoints sourceNodeId
        | SemanticNodeKind.StringToLower sourceNodeId
        | SemanticNodeKind.StringToUpper sourceNodeId
        | SemanticNodeKind.StringTrim sourceNodeId
        | SemanticNodeKind.StringTrimStart sourceNodeId
        | SemanticNodeKind.StringTrimEnd sourceNodeId
        | SemanticNodeKind.StringDecodeUtf8Lossy sourceNodeId
        | SemanticNodeKind.StringEncodeUtf8 sourceNodeId
        | SemanticNodeKind.Float64RoundToInt64 sourceNodeId
        | SemanticNodeKind.Float64FloorToInt64 sourceNodeId
        | SemanticNodeKind.Float64Sqrt sourceNodeId
        | SemanticNodeKind.Float64Abs sourceNodeId
        | SemanticNodeKind.Float64Negate sourceNodeId
        | SemanticNodeKind.Float64Floor sourceNodeId
        | SemanticNodeKind.Float64Ceiling sourceNodeId
        | SemanticNodeKind.Float64Truncate sourceNodeId
        | SemanticNodeKind.StringUtf8ByteCount sourceNodeId
        | SemanticNodeKind.StringFromCodePoint sourceNodeId
        | SemanticNodeKind.StringToCodePoint sourceNodeId
        | SemanticNodeKind.StringLength sourceNodeId
        | SemanticNodeKind.BoolNot sourceNodeId ->
            [ create ValueDependencyRole.Source sourceNodeId ]
        | SemanticNodeKind.StringSlice(sourceNodeId, startNodeId, endNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.SliceStart startNodeId
              create ValueDependencyRole.SliceEnd endNodeId ]
        | SemanticNodeKind.StringContains(sourceNodeId, substringNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Substring substringNodeId ]
        | SemanticNodeKind.Bits32RotateRight(sourceNodeId, amountNodeId)
        | SemanticNodeKind.Bits32ShiftRight(sourceNodeId, amountNodeId)
        | SemanticNodeKind.Bits32ShiftLeft(sourceNodeId, amountNodeId)
        | SemanticNodeKind.Int64ShiftLeft(sourceNodeId, amountNodeId)
        | SemanticNodeKind.Int64ShiftRight(sourceNodeId, amountNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Amount amountNodeId ]
        | SemanticNodeKind.StringIndexOf(sourceNodeId, substringNodeId, fromNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Substring substringNodeId
              create ValueDependencyRole.SearchFrom fromNodeId ]
        | SemanticNodeKind.StringSpan(sourceNodeId, setNodeId, fromNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.CharacterSet setNodeId
              create ValueDependencyRole.SearchFrom fromNodeId ]
        | SemanticNodeKind.StringSpanBack(sourceNodeId, setNodeId, endNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.CharacterSet setNodeId
              create ValueDependencyRole.SliceEnd endNodeId ]
        | SemanticNodeKind.StringLastIndexOfBefore(sourceNodeId, substringNodeId, endNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Substring substringNodeId
              create ValueDependencyRole.SliceEnd endNodeId ]
        | SemanticNodeKind.StringLastIndexOf(sourceNodeId, substringNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Substring substringNodeId ]
        | SemanticNodeKind.StringStartsWith(sourceNodeId, prefixNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Prefix prefixNodeId ]
        | SemanticNodeKind.StringEndsWith(sourceNodeId, suffixNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Suffix suffixNodeId ]
        | SemanticNodeKind.StringSplit(sourceNodeId, separatorNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Separator separatorNodeId ]
        | SemanticNodeKind.StringSplitLines(sourceNodeId, breaksNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.CharacterSet breaksNodeId ]
        | SemanticNodeKind.StringReplace(sourceNodeId, patternNodeId, replacementNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Substring patternNodeId
              create ValueDependencyRole.Replacement replacementNodeId ]
        | SemanticNodeKind.StringJoin(sourceNodeId, separatorNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Separator separatorNodeId ]
        | SemanticNodeKind.StringEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.StringLessThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.ValueEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.StringConcat(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64FloorDivide(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64FloorModulo(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64Min(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64Max(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64BitAnd(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64BitOr(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64BitXor(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64CheckedAdd(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64CheckedSubtract(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64CheckedMultiply(leftNodeId, rightNodeId)
        | SemanticNodeKind.ListRange(leftNodeId, rightNodeId)
        | SemanticNodeKind.BytesFill(leftNodeId, rightNodeId)
        | SemanticNodeKind.BytesConcat(leftNodeId, rightNodeId)
        | SemanticNodeKind.StringRepeat(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Round(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64Equal(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64LessThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64LessThanOrEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64GreaterThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64GreaterThanOrEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Add(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Min(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Max(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Subtract(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Multiply(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Divide(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64FloorModulo(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Equal(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64LessThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.Bits32Xor(leftNodeId, rightNodeId)
        | SemanticNodeKind.Bits32And(leftNodeId, rightNodeId)
        | SemanticNodeKind.Bits32Or(leftNodeId, rightNodeId)
        | SemanticNodeKind.Bits32Add(leftNodeId, rightNodeId)
        | SemanticNodeKind.ListConcat(leftNodeId, rightNodeId)
        | SemanticNodeKind.SetUnion(leftNodeId, rightNodeId)
        | SemanticNodeKind.SetIntersection(leftNodeId, rightNodeId)
        | SemanticNodeKind.SetDifference(leftNodeId, rightNodeId)
        | SemanticNodeKind.SetIsSubset(leftNodeId, rightNodeId)
        | SemanticNodeKind.MapUnion(leftNodeId, rightNodeId) ->
            [ create ValueDependencyRole.LeftOperand leftNodeId
              create ValueDependencyRole.RightOperand rightNodeId ]
        | SemanticNodeKind.Float64IsNaN valueNodeId
        | SemanticNodeKind.Float64IsFinite valueNodeId
        | SemanticNodeKind.Float64IsInfinite valueNodeId ->
            [ create ValueDependencyRole.Value valueNodeId ]
        | SemanticNodeKind.ListConstruct(_, itemNodeIds) ->
            itemNodeIds
            |> List.mapi (fun index nodeId -> create (ValueDependencyRole.ListItem index) nodeId)
        | SemanticNodeKind.ListLength sourceNodeId
        | SemanticNodeKind.MapValues sourceNodeId
        | SemanticNodeKind.MapKeys sourceNodeId
        | SemanticNodeKind.SetToList sourceNodeId
        | SemanticNodeKind.MapSize sourceNodeId
        | SemanticNodeKind.SetFromList sourceNodeId
        | SemanticNodeKind.SetSize sourceNodeId
        | SemanticNodeKind.ListReverse sourceNodeId
        | SemanticNodeKind.ListLast sourceNodeId
        | SemanticNodeKind.ListDropLast sourceNodeId
        | SemanticNodeKind.FieldAccess(sourceNodeId, _) ->
            [ create ValueDependencyRole.Source sourceNodeId ]
        | SemanticNodeKind.ListZipWith(leftNodeId, rightNodeId, _, context) ->
            [ create ValueDependencyRole.LeftOperand leftNodeId
              create ValueDependencyRole.RightOperand rightNodeId ]
            @ contextDependencies context
        | SemanticNodeKind.ListFlatMap(sourceNodeId, _, context)
        | SemanticNodeKind.ListMap(sourceNodeId, _, context)
        | SemanticNodeKind.MapFromList(sourceNodeId, _, context)
        | SemanticNodeKind.ListFilter(sourceNodeId, _, context)
        | SemanticNodeKind.MapFilter(sourceNodeId, _, context)
        | SemanticNodeKind.SetFilter(sourceNodeId, _, context)
        | SemanticNodeKind.ListSortBy(sourceNodeId, _, context)
        | SemanticNodeKind.MapMapValues(sourceNodeId, _, context)
        | SemanticNodeKind.SetMap(sourceNodeId, _, context)
        | SemanticNodeKind.OptionMap(sourceNodeId, _, context)
        | SemanticNodeKind.OptionAndThen(sourceNodeId, _, context)
        | SemanticNodeKind.ResultMap(sourceNodeId, _, context)
        | SemanticNodeKind.ResultMapErr(sourceNodeId, _, context)
        | SemanticNodeKind.ResultAndThen(sourceNodeId, _, context)
        | SemanticNodeKind.ListMinBy(sourceNodeId, _, context)
        | SemanticNodeKind.ListMaxBy(sourceNodeId, _, context)
        | SemanticNodeKind.ListAny(sourceNodeId, _, context)
        | SemanticNodeKind.ListFindIndex(sourceNodeId, _, context)
        | SemanticNodeKind.ListAll(sourceNodeId, _, context) ->
            create ValueDependencyRole.Source sourceNodeId
            :: contextDependencies context
        | SemanticNodeKind.ListFold(sourceNodeId, seedNodeId, _, context)
        | SemanticNodeKind.ListScan(sourceNodeId, seedNodeId, _, context)
        | SemanticNodeKind.MapFold(sourceNodeId, seedNodeId, _, context)
        | SemanticNodeKind.SetFold(sourceNodeId, seedNodeId, _, context)
        | SemanticNodeKind.ListEffectFold(sourceNodeId, seedNodeId, _, context) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Value seedNodeId ]
            @ contextDependencies context
        | SemanticNodeKind.UseResource(acquireNodeId, releaseCapabilityNodeId, _, _, context) ->
            [ create ValueDependencyRole.Value acquireNodeId
              create ValueDependencyRole.CapabilityValue releaseCapabilityNodeId ]
            @ contextDependencies context
        | SemanticNodeKind.ParallelAll(sourceNodeId, _, context) ->
            create ValueDependencyRole.Source sourceNodeId
            :: contextDependencies context
        | SemanticNodeKind.CancellationRequested -> []
        | SemanticNodeKind.ExceptionCatch(_, bodyContext, _, handlerContext) -> contextDependencies bodyContext @ contextDependencies handlerContext
        | SemanticNodeKind.ExceptionIsCancellation value
        | SemanticNodeKind.ExceptionMessage value
        | SemanticNodeKind.ExceptionRethrow(value, _) -> [ create ValueDependencyRole.Value value ]
        | SemanticNodeKind.CancellationShield(_, context) ->
            contextDependencies context
        | SemanticNodeKind.TaskScope(_, context) ->
            contextDependencies context
        | SemanticNodeKind.TaskStart(scopeNodeId, _, context, itemNodeId) ->
            [ create ValueDependencyRole.TaskScope scopeNodeId
              create ValueDependencyRole.Item itemNodeId ]
            @ contextDependencies context
        | SemanticNodeKind.TaskWait(tasksNodeId, _, limitNodeId) ->
            create ValueDependencyRole.Source tasksNodeId :: (limitNodeId |> Option.map (create ValueDependencyRole.Limit) |> Option.toList)
        | SemanticNodeKind.OneShotCreate(scope, _) -> [ create ValueDependencyRole.TaskScope scope ]
        | SemanticNodeKind.OneShotComplete(cell, value) -> [ create ValueDependencyRole.Source cell; create ValueDependencyRole.Value value ]
        | SemanticNodeKind.OneShotWait(cell, limit) -> create ValueDependencyRole.Source cell :: (limit |> Option.map (create ValueDependencyRole.Limit) |> Option.toList)
        | SemanticNodeKind.TaskCancel taskNodeId -> [ create ValueDependencyRole.Value taskNodeId ]
        | SemanticNodeKind.ListAppendItem(sourceNodeId, itemNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Item itemNodeId ]
        | SemanticNodeKind.BytesAppend(sourceNodeId, itemNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Item itemNodeId ]
        | SemanticNodeKind.BytesEmpty -> []
        | SemanticNodeKind.BytesGet(sourceNodeId, indexNodeId)
        | SemanticNodeKind.ListItemAt(sourceNodeId, indexNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Index indexNodeId ]
        | SemanticNodeKind.MapRemove(sourceNodeId, keyNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Key keyNodeId ]
        | SemanticNodeKind.SetInsert(sourceNodeId, itemNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Item itemNodeId ]
        | SemanticNodeKind.SetRemove(sourceNodeId, itemNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Item itemNodeId ]
        | SemanticNodeKind.MapContainsKey(sourceNodeId, keyNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Key keyNodeId ]
        | SemanticNodeKind.SetContains(sourceNodeId, itemNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Item itemNodeId ]
        | SemanticNodeKind.ListIndexOf(sourceNodeId, itemNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Item itemNodeId ]
        | SemanticNodeKind.ListLastIndexOf(sourceNodeId, itemNodeId)
        | SemanticNodeKind.ListContains(sourceNodeId, itemNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Item itemNodeId ]
        | SemanticNodeKind.MapGet(sourceNodeId, keyNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Key keyNodeId ]
        | SemanticNodeKind.OptionGetOrDefault(sourceNodeId, defaultNodeId)
        | SemanticNodeKind.ResultGetOrDefault(sourceNodeId, defaultNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Value defaultNodeId ]
        | SemanticNodeKind.OptionOkOr(sourceNodeId, errorNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Error errorNodeId ]
        | SemanticNodeKind.ResultOkValue sourceNodeId
        | SemanticNodeKind.ResultErrValue sourceNodeId
        | SemanticNodeKind.OptionIsSome sourceNodeId
        | SemanticNodeKind.ResultIsOk sourceNodeId -> [ create ValueDependencyRole.Source sourceNodeId ]
        | SemanticNodeKind.MapGetOrDefault(sourceNodeId, keyNodeId, defaultNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Key keyNodeId
              create ValueDependencyRole.Value defaultNodeId ]
        | SemanticNodeKind.BytesSlice(sourceNodeId, startNodeId, endNodeId)
        | SemanticNodeKind.ListSlice(sourceNodeId, startNodeId, endNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.SliceStart startNodeId
              create ValueDependencyRole.SliceEnd endNodeId ]
        | SemanticNodeKind.BytesSet(sourceNodeId, indexNodeId, itemNodeId)
        | SemanticNodeKind.ListSetAt(sourceNodeId, indexNodeId, itemNodeId)
        | SemanticNodeKind.ListInsertAt(sourceNodeId, indexNodeId, itemNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Index indexNodeId
              create ValueDependencyRole.Item itemNodeId ]
        | SemanticNodeKind.ListRemoveAt(sourceNodeId, indexNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Index indexNodeId ]
        | SemanticNodeKind.MapInsert(sourceNodeId, keyNodeId, valueNodeId) ->
            [ create ValueDependencyRole.Source sourceNodeId
              create ValueDependencyRole.Key keyNodeId
              create ValueDependencyRole.Value valueNodeId ]
        | SemanticNodeKind.CapabilityConstruct _
        | SemanticNodeKind.FunctionReference _ -> []
        | SemanticNodeKind.FunctionInvoke(value, arguments) ->
            create ValueDependencyRole.FunctionValue value :: (arguments |> List.mapi (fun index id -> create (ValueDependencyRole.FunctionArgument index) id))
        | SemanticNodeKind.DirectCall(_, arguments) ->
            arguments
            |> List.sortBy (fun argument -> StableId.value argument.TargetParameterId)
            |> List.map (fun argument ->
                create (ValueDependencyRole.CallArgument argument.TargetParameterId) argument.ValueNodeId)
        | SemanticNodeKind.CapabilityInvoke(capabilityValueNodeId, _, arguments) ->
            create ValueDependencyRole.CapabilityValue capabilityValueNodeId
            :: (arguments
                |> List.sortBy (fun argument -> StableId.value argument.TargetCapabilityParameterId)
                |> List.map (fun argument ->
                    create
                        (ValueDependencyRole.CapabilityArgument argument.TargetCapabilityParameterId)
                        argument.ValueNodeId))
        | SemanticNodeKind.ProductConstruct(_, _, fields)
        | SemanticNodeKind.UnionConstruct(_, _, fields) ->
            fields
            |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
            |> List.map (fun field ->
                create (ValueDependencyRole.FieldValue field.TargetFieldId) field.ValueNodeId)
        | SemanticNodeKind.ProductUpdate(_, sourceNodeId, fields) ->
            create ValueDependencyRole.Source sourceNodeId
            :: (fields
                |> List.sortBy (fun field -> StableId.value field.TargetFieldId)
                |> List.map (fun field ->
                    create (ValueDependencyRole.FieldValue field.TargetFieldId) field.ValueNodeId))
        | SemanticNodeKind.OptionSome valueNodeId
        | SemanticNodeKind.ResultOk(_, valueNodeId) ->
            [ create ValueDependencyRole.Value valueNodeId ]
        | SemanticNodeKind.Abort errorNodeId
        | SemanticNodeKind.ResultErr(_, errorNodeId) ->
            [ create ValueDependencyRole.Error errorNodeId ]
        // The right operand of BoolAnd/BoolOr is a child ValueGraph, like a Conditional branch.
        | SemanticNodeKind.BoolAnd(leftNodeId, _)
        | SemanticNodeKind.BoolOr(leftNodeId, _) ->
            [ create ValueDependencyRole.LeftOperand leftNodeId ]
        | SemanticNodeKind.Conditional(conditionNodeId, _, _) ->
            [ create ValueDependencyRole.BranchCondition conditionNodeId ]
        | SemanticNodeKind.Bind(inputNodeId, _) ->
            [ create ValueDependencyRole.BindInput inputNodeId ]
        | SemanticNodeKind.MatchUnion(inputNodeId, _)
        | SemanticNodeKind.MatchOption(inputNodeId, _, _)
        | SemanticNodeKind.MatchResult(inputNodeId, _, _) ->
            [ create ValueDependencyRole.MatchInput inputNodeId ]
        | SemanticNodeKind.ParameterReference _
        | SemanticNodeKind.PatternReference _
        | SemanticNodeKind.StringLiteral _
        | SemanticNodeKind.BoolLiteral _
        | SemanticNodeKind.UnitLiteral
        | SemanticNodeKind.Int64Literal _
        | SemanticNodeKind.Float64Literal _
        | SemanticNodeKind.ListEmpty _
        | SemanticNodeKind.SetEmpty _
        | SemanticNodeKind.MapEmpty _
        | SemanticNodeKind.OptionNone _ -> []
