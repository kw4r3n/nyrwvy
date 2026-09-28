namespace Nyrwvy.Core

/// Reproducible derived facts over one semantic snapshot.
/// This module never mutates the graph and never invents fallback semantics.
[<RequireQualifiedAccess>]
module SemanticFacts =
    /// Direct value-node references required to interpret one node locally.
    /// Nested branch result nodes are intentionally not included: callers that
    /// need structured control-flow dependencies should traverse the child graphs.
    let directValueDependencies (node: SemanticNode): GraphNodeId list =
        match node.Kind with
        | SemanticNodeKind.StringEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.StringConcat(leftNodeId, rightNodeId) -> [ leftNodeId; rightNodeId ]
        | SemanticNodeKind.Int64ToString valueNodeId
        | SemanticNodeKind.StringParseInt64 valueNodeId
        | SemanticNodeKind.StringLength valueNodeId
        | SemanticNodeKind.StringIsEmpty valueNodeId -> [ valueNodeId ]
        | SemanticNodeKind.StringSlice(valueNodeId, startNodeId, endNodeId) -> [ valueNodeId; startNodeId; endNodeId ]
        | SemanticNodeKind.StringContains(valueNodeId, substringNodeId) ->
            [ valueNodeId; substringNodeId ]
        | SemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64FloorDivide(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64FloorModulo(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64Equal(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64LessThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64LessThanOrEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64GreaterThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64GreaterThanOrEqual(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Add(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Subtract(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Multiply(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Divide(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64Equal(leftNodeId, rightNodeId)
        | SemanticNodeKind.Float64LessThan(leftNodeId, rightNodeId)
        | SemanticNodeKind.ListConcat(leftNodeId, rightNodeId) ->
            [ leftNodeId; rightNodeId ]
        | SemanticNodeKind.Float64IsNaN valueNodeId
        | SemanticNodeKind.Float64IsFinite valueNodeId
        | SemanticNodeKind.Float64IsInfinite valueNodeId -> [ valueNodeId ]
        | SemanticNodeKind.ListConstruct(_, itemNodeIds) -> itemNodeIds
        | SemanticNodeKind.ListLength listNodeId -> [ listNodeId ]
        | SemanticNodeKind.ListMap(listNodeId, _, context)
        | SemanticNodeKind.ListFilter(listNodeId, _, context)
        | SemanticNodeKind.ListAny(listNodeId, _, context)
        | SemanticNodeKind.ListAll(listNodeId, _, context) -> listNodeId :: (context |> List.map (fun binding -> binding.ValueNodeId))
        | SemanticNodeKind.ListAppendItem(listNodeId, itemNodeId) -> [ listNodeId; itemNodeId ]
        | SemanticNodeKind.ListFold(listNodeId, seedNodeId, _, context) ->
            listNodeId :: seedNodeId :: (context |> List.map (fun binding -> binding.ValueNodeId))
        | SemanticNodeKind.ListEffectFold(listNodeId, seedNodeId, _, context) ->
            listNodeId :: seedNodeId :: (context |> List.map (fun binding -> binding.ValueNodeId))
        | SemanticNodeKind.UseResource(acquireNodeId, releaseCapabilityNodeId, _, _, context) ->
            acquireNodeId :: releaseCapabilityNodeId :: (context |> List.map (fun binding -> binding.ValueNodeId))
        | SemanticNodeKind.ParallelAll(sourceListNodeId, _, context) ->
            sourceListNodeId :: (context |> List.map (fun binding -> binding.ValueNodeId))
        | SemanticNodeKind.CancellationRequested -> []
        | SemanticNodeKind.CapabilityConstruct _
        | SemanticNodeKind.FunctionReference _ -> []
        | SemanticNodeKind.FunctionInvoke(value, arguments) -> value :: arguments
        | SemanticNodeKind.TaskScope(_, context) -> context |> List.map (fun binding -> binding.ValueNodeId)
        | SemanticNodeKind.TaskStart(scopeNodeId, _, context, itemNodeId) ->
            scopeNodeId :: itemNodeId :: (context |> List.map (fun binding -> binding.ValueNodeId))
        | SemanticNodeKind.TaskWait(tasksNodeId, _, limitNodeId) -> tasksNodeId :: Option.toList limitNodeId
        | SemanticNodeKind.TaskCancel taskNodeId -> [ taskNodeId ]
        | SemanticNodeKind.DirectCall(_, arguments) ->
            arguments |> List.map (fun argument -> argument.ValueNodeId)
        | SemanticNodeKind.CapabilityInvoke(capabilityValueNodeId, _, arguments) ->
            capabilityValueNodeId :: (arguments |> List.map (fun argument -> argument.ValueNodeId))
        | SemanticNodeKind.ProductConstruct(_, fields)
        | SemanticNodeKind.UnionConstruct(_, fields) ->
            fields |> List.map (fun field -> field.ValueNodeId)
        | SemanticNodeKind.FieldAccess(sourceNodeId, _) -> [ sourceNodeId ]
        | SemanticNodeKind.OptionSome valueNodeId -> [ valueNodeId ]
        | SemanticNodeKind.ResultOk(_, valueNodeId) -> [ valueNodeId ]
        | SemanticNodeKind.Abort errorNodeId
        | SemanticNodeKind.ResultErr(_, errorNodeId) -> [ errorNodeId ]
        | SemanticNodeKind.BoolNot operandNodeId
        | SemanticNodeKind.BoolAnd(operandNodeId, _)
        | SemanticNodeKind.BoolOr(operandNodeId, _) -> [ operandNodeId ]
        | SemanticNodeKind.Conditional(conditionNodeId, _, _) -> [ conditionNodeId ]
        | SemanticNodeKind.MatchUnion(inputNodeId, _) -> [ inputNodeId ]
        | SemanticNodeKind.Bind(inputNodeId, _) -> [ inputNodeId ]
        | SemanticNodeKind.MatchOption(inputNodeId, _, _) -> [ inputNodeId ]
        | SemanticNodeKind.MatchResult(inputNodeId, _, _) -> [ inputNodeId ]
        | SemanticNodeKind.ParameterReference _
        | SemanticNodeKind.PatternReference _
        | SemanticNodeKind.StringLiteral _
        | SemanticNodeKind.BoolLiteral _
        | SemanticNodeKind.UnitLiteral
        | SemanticNodeKind.Int64Literal _
        | SemanticNodeKind.Float64Literal _
        | SemanticNodeKind.ListEmpty _
        | SemanticNodeKind.OptionNone _ -> []

    let nestedGraphs (node: SemanticNode) =
        match node.Kind with
        | SemanticNodeKind.Conditional(_, a, b) -> [ a; b ]
        | SemanticNodeKind.BoolAnd(_, right)
        | SemanticNodeKind.BoolOr(_, right) -> [ right ]
        | SemanticNodeKind.MatchUnion(_, arms) -> arms |> List.map (fun arm -> arm.Body)
        | SemanticNodeKind.Bind(_, arm) -> [ arm.Body ]
        | SemanticNodeKind.MatchOption(_, arm, other) -> [ arm.Body; other ]
        | SemanticNodeKind.MatchResult(_, a, b) -> [ a.Body; b.Body ]
        | _ -> []

    /// Stored nodes execute even if the result does not depend on their values.
    let graphCompletion resolve (graph: ValueGraph) =
        if graph.Nodes |> List.exists (fun node -> resolve node.Id = Some GraphCompletion.Aborts) then
            Some GraphCompletion.Aborts
        else resolve graph.ResultNodeId

    let rec collectPatternBindings (graph: ValueGraph): PatternBinding list =
        graph.Nodes
        |> List.collect (fun node ->
            match node.Kind with
            | SemanticNodeKind.Conditional(_, whenTrue, whenFalse) ->
                collectPatternBindings whenTrue @ collectPatternBindings whenFalse
            | SemanticNodeKind.BoolAnd(_, right)
            | SemanticNodeKind.BoolOr(_, right) -> collectPatternBindings right
            | SemanticNodeKind.MatchUnion(_, arms) ->
                arms
                |> List.collect (fun arm ->
                    (arm.FieldBindings |> List.map (fun fieldBinding -> fieldBinding.Binding))
                    @ collectPatternBindings arm.Body)
            | SemanticNodeKind.MatchOption(_, someArm, noneBody) ->
                someArm.ValueBinding
                :: (collectPatternBindings someArm.Body @ collectPatternBindings noneBody)
            | SemanticNodeKind.Bind(_, body) ->
                body.ValueBinding :: collectPatternBindings body.Body
            | SemanticNodeKind.MatchResult(_, okArm, errArm) ->
                [ okArm.ValueBinding; errArm.ErrorBinding ]
                @ collectPatternBindings okArm.Body
                @ collectPatternBindings errArm.Body
            | SemanticNodeKind.ParameterReference _
            | SemanticNodeKind.PatternReference _
            | SemanticNodeKind.StringLiteral _
            | SemanticNodeKind.BoolLiteral _
            | SemanticNodeKind.UnitLiteral
            | SemanticNodeKind.Int64Literal _
            | SemanticNodeKind.Float64Literal _
            | SemanticNodeKind.StringIsEmpty _
            | SemanticNodeKind.Int64ToString _
            | SemanticNodeKind.StringParseInt64 _
            | SemanticNodeKind.StringLength _
            | SemanticNodeKind.StringSlice _
            | SemanticNodeKind.BoolNot _
            | SemanticNodeKind.StringContains _
            | SemanticNodeKind.StringEqual _
            | SemanticNodeKind.StringConcat _
            | SemanticNodeKind.Int64WrappingAdd _
            | SemanticNodeKind.Int64WrappingSubtract _
            | SemanticNodeKind.Int64WrappingMultiply _
            | SemanticNodeKind.Int64FloorDivide _
            | SemanticNodeKind.Int64FloorModulo _
            | SemanticNodeKind.Int64Equal _
            | SemanticNodeKind.Int64LessThan _
            | SemanticNodeKind.Int64LessThanOrEqual _
            | SemanticNodeKind.Int64GreaterThan _
            | SemanticNodeKind.Int64GreaterThanOrEqual _
            | SemanticNodeKind.Float64Add _
            | SemanticNodeKind.Float64Subtract _
            | SemanticNodeKind.Float64Multiply _
            | SemanticNodeKind.Float64Divide _
            | SemanticNodeKind.Float64Equal _
            | SemanticNodeKind.Float64LessThan _
            | SemanticNodeKind.Float64IsNaN _
            | SemanticNodeKind.Float64IsFinite _
            | SemanticNodeKind.Float64IsInfinite _
            | SemanticNodeKind.ListEmpty _
            | SemanticNodeKind.ListConstruct _
            | SemanticNodeKind.ListLength _
            | SemanticNodeKind.ListAppendItem _
            | SemanticNodeKind.ListConcat _
            | SemanticNodeKind.ListFold _
            | SemanticNodeKind.ListEffectFold _
            | SemanticNodeKind.UseResource _
            | SemanticNodeKind.ParallelAll _
            | SemanticNodeKind.CancellationRequested
            | SemanticNodeKind.TaskScope _
            | SemanticNodeKind.TaskStart _
            | SemanticNodeKind.TaskWait _
            | SemanticNodeKind.TaskCancel _
            | SemanticNodeKind.ListMap _
            | SemanticNodeKind.ListFilter _
            | SemanticNodeKind.ListAny _
            | SemanticNodeKind.ListAll _
            | SemanticNodeKind.CapabilityConstruct _
            | SemanticNodeKind.FunctionReference _
            | SemanticNodeKind.FunctionInvoke _
            | SemanticNodeKind.DirectCall _
            | SemanticNodeKind.CapabilityInvoke _
            | SemanticNodeKind.ProductConstruct _
            | SemanticNodeKind.FieldAccess _
            | SemanticNodeKind.UnionConstruct _
            | SemanticNodeKind.OptionNone _
            | SemanticNodeKind.OptionSome _
            | SemanticNodeKind.ResultOk _
            | SemanticNodeKind.Abort _
            | SemanticNodeKind.ResultErr _ -> [])

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
                    | SemanticNodeKind.StringSlice _ ->
                        Some(TypeRef.Primitive PrimitiveType.String)
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
                    | SemanticNodeKind.StringLength _
                    | SemanticNodeKind.ListLength _ ->
                        Some(TypeRef.Primitive PrimitiveType.Int64)
                    | SemanticNodeKind.Float64Literal _
                    | SemanticNodeKind.Float64Add _
                    | SemanticNodeKind.Float64Subtract _
                    | SemanticNodeKind.Float64Multiply _
                    | SemanticNodeKind.Float64Divide _ ->
                        Some(TypeRef.Primitive PrimitiveType.Float64)
                    | SemanticNodeKind.Int64ToString _ -> Some(TypeRef.Primitive PrimitiveType.String)
                    | SemanticNodeKind.StringParseInt64 _ ->
                        Some(TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ TypeRef.Primitive PrimitiveType.Int64 ]))
                    | SemanticNodeKind.StringIsEmpty _
                    | SemanticNodeKind.StringContains _
                    | SemanticNodeKind.StringEqual _
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
                    | SemanticNodeKind.ListAll _ ->
                        Some(TypeRef.Primitive PrimitiveType.Bool)
                    | SemanticNodeKind.ListEmpty itemType
                    | SemanticNodeKind.ListConstruct(itemType, _) ->
                        Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ]))
                    | SemanticNodeKind.ListAppendItem(listNodeId, _)
                    | SemanticNodeKind.ListFilter(listNodeId, _, _) ->
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
                    | SemanticNodeKind.CapabilityConstruct(capabilityId, _) -> Some(TypeRef.Capability capabilityId)
                    | SemanticNodeKind.FunctionReference target ->
                        Map.tryFind target index.Functions |> Option.map (fun fn -> TypeRef.Function(List.map (fun (p: SemanticParameter) -> p.Type) fn.Parameters, fn.ResultType))
                    | SemanticNodeKind.FunctionInvoke(value, _) ->
                        match tryNodeType nextVisiting value with
                        | Some(TypeRef.Function(_, result)) -> Some result
                        | _ -> None
                    | SemanticNodeKind.DirectCall(targetFunctionId, arguments) ->
                        Map.tryFind targetFunctionId index.Functions
                        |> Option.map (fun target ->
                            let pairs =
                                arguments |> List.choose (fun argument ->
                                    match target.Parameters |> List.tryFind (fun parameter -> parameter.Id = argument.TargetParameterId), tryNodeType nextVisiting argument.ValueNodeId with
                                    | Some parameter, Some actual -> Some(parameter.Type, actual)
                                    | _ -> None)
                            match TypeParameters.matchAll pairs with
                            | Some substitution -> TypeParameters.substitute substitution target.ResultType
                            | None -> target.ResultType)
                    | SemanticNodeKind.CapabilityInvoke(_, operationId, _) ->
                        Map.tryFind operationId index.CapabilityOperations
                        |> Option.map (fun operation -> operation.ResultType)
                    | SemanticNodeKind.ProductConstruct(typeId, _) ->
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
