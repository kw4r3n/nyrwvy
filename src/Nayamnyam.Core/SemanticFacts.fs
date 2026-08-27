namespace Nayamnyam.Core

/// Reproducible derived facts over one semantic snapshot.
/// This module never mutates the graph and never invents fallback semantics.
[<RequireQualifiedAccess>]
module SemanticFacts =
    /// Direct value-node references required to interpret one node locally.
    /// Nested branch result nodes are intentionally not included: callers that
    /// need structured control-flow dependencies should traverse the child graphs.
    let directValueDependencies (node: SemanticNode): GraphNodeId list =
        match node.Kind with
        | SemanticNodeKind.StringIsEmpty valueNodeId -> [ valueNodeId ]
        | SemanticNodeKind.StringContains(valueNodeId, substringNodeId) ->
            [ valueNodeId; substringNodeId ]
        | SemanticNodeKind.Int64WrappingAdd(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64WrappingSubtract(leftNodeId, rightNodeId)
        | SemanticNodeKind.Int64WrappingMultiply(leftNodeId, rightNodeId)
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
        | SemanticNodeKind.ListLength listNodeId
        | SemanticNodeKind.ListMap(listNodeId, _)
        | SemanticNodeKind.ListFilter(listNodeId, _)
        | SemanticNodeKind.ListAny(listNodeId, _)
        | SemanticNodeKind.ListAll(listNodeId, _) -> [ listNodeId ]
        | SemanticNodeKind.ListAppendItem(listNodeId, itemNodeId) -> [ listNodeId; itemNodeId ]
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
        | SemanticNodeKind.ResultErr(_, errorNodeId) -> [ errorNodeId ]
        | SemanticNodeKind.Conditional(conditionNodeId, _, _) -> [ conditionNodeId ]
        | SemanticNodeKind.MatchUnion(inputNodeId, _) -> [ inputNodeId ]
        | SemanticNodeKind.MatchOption(inputNodeId, _, _) -> [ inputNodeId ]
        | SemanticNodeKind.MatchResult(inputNodeId, _) -> [ inputNodeId ]
        | SemanticNodeKind.ParameterReference _
        | SemanticNodeKind.PatternReference _
        | SemanticNodeKind.StringLiteral _
        | SemanticNodeKind.BoolLiteral _
        | SemanticNodeKind.UnitLiteral
        | SemanticNodeKind.Int64Literal _
        | SemanticNodeKind.Float64Literal _
        | SemanticNodeKind.ListEmpty _
        | SemanticNodeKind.OptionNone _ -> []

    let rec collectPatternBindings (graph: ValueGraph): PatternBinding list =
        graph.Nodes
        |> List.collect (fun node ->
            match node.Kind with
            | SemanticNodeKind.Conditional(_, whenTrue, whenFalse) ->
                collectPatternBindings whenTrue @ collectPatternBindings whenFalse
            | SemanticNodeKind.MatchUnion(_, arms) ->
                arms
                |> List.collect (fun arm ->
                    (arm.FieldBindings |> List.map (fun fieldBinding -> fieldBinding.Binding))
                    @ collectPatternBindings arm.Body)
            | SemanticNodeKind.MatchOption(_, someArm, noneBody) ->
                someArm.ValueBinding
                :: (collectPatternBindings someArm.Body @ collectPatternBindings noneBody)
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
            | SemanticNodeKind.StringContains _
            | SemanticNodeKind.Int64WrappingAdd _
            | SemanticNodeKind.Int64WrappingSubtract _
            | SemanticNodeKind.Int64WrappingMultiply _
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
            | SemanticNodeKind.ListMap _
            | SemanticNodeKind.ListFilter _
            | SemanticNodeKind.ListAny _
            | SemanticNodeKind.ListAll _
            | SemanticNodeKind.DirectCall _
            | SemanticNodeKind.CapabilityInvoke _
            | SemanticNodeKind.ProductConstruct _
            | SemanticNodeKind.FieldAccess _
            | SemanticNodeKind.UnionConstruct _
            | SemanticNodeKind.OptionNone _
            | SemanticNodeKind.OptionSome _
            | SemanticNodeKind.ResultOk _
            | SemanticNodeKind.ResultErr _ -> [])

    let private commonType (types: TypeRef option list): TypeRef option =
        match types with
        | [] -> None
        | Some first :: rest when rest |> List.forall (fun item -> item = Some first) -> Some first
        | Some _ :: _
        | None :: _ -> None

    /// Creates one type resolver for a function. The resolver uses only semantic
    /// facts in the supplied snapshot and returns None when the graph is not
    /// sufficiently valid to derive a type. Callers must not substitute a fallback.
    let createNodeTypeResolver
        (snapshot: Snapshot)
        (functionEntity: SemanticFunction)
        : GraphNodeId -> TypeRef option =
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

        let rec tryNodeType (visiting: Set<GraphNodeId>) (nodeId: GraphNodeId): TypeRef option =
            if Set.contains nodeId visiting then
                None
            else
                match Map.tryFind nodeId nodesById with
                | None -> None
                | Some node ->
                    let nextVisiting = Set.add nodeId visiting
                    let graphResultType (graph: ValueGraph) = tryNodeType nextVisiting graph.ResultNodeId

                    match node.Kind with
                    | SemanticNodeKind.ParameterReference parameterId ->
                        Map.tryFind parameterId parametersById
                        |> Option.map (fun parameter -> parameter.Type)
                    | SemanticNodeKind.PatternReference bindingId ->
                        Map.tryFind bindingId patternBindingsById
                        |> Option.map (fun binding -> binding.Type)
                    | SemanticNodeKind.StringLiteral _ ->
                        Some(TypeRef.Primitive PrimitiveType.String)
                    | SemanticNodeKind.BoolLiteral _ ->
                        Some(TypeRef.Primitive PrimitiveType.Bool)
                    | SemanticNodeKind.UnitLiteral ->
                        Some(TypeRef.Primitive PrimitiveType.Unit)
                    | SemanticNodeKind.Int64Literal _
                    | SemanticNodeKind.Int64WrappingAdd _
                    | SemanticNodeKind.Int64WrappingSubtract _
                    | SemanticNodeKind.Int64WrappingMultiply _
                    | SemanticNodeKind.ListLength _ ->
                        Some(TypeRef.Primitive PrimitiveType.Int64)
                    | SemanticNodeKind.Float64Literal _
                    | SemanticNodeKind.Float64Add _
                    | SemanticNodeKind.Float64Subtract _
                    | SemanticNodeKind.Float64Multiply _
                    | SemanticNodeKind.Float64Divide _ ->
                        Some(TypeRef.Primitive PrimitiveType.Float64)
                    | SemanticNodeKind.StringIsEmpty _
                    | SemanticNodeKind.StringContains _
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
                    | SemanticNodeKind.ListFilter(listNodeId, _) ->
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
                    | SemanticNodeKind.ListMap(sourceListNodeId, mapperFunctionId) ->
                        match tryNodeType nextVisiting sourceListNodeId, Map.tryFind mapperFunctionId functionsById with
                        | Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ _ ])), Some mapper ->
                            Some(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ mapper.ResultType ]))
                        | _ -> None
                    | SemanticNodeKind.DirectCall(targetFunctionId, _) ->
                        Map.tryFind targetFunctionId functionsById
                        |> Option.map (fun target -> target.ResultType)
                    | SemanticNodeKind.CapabilityInvoke(_, operationId, _) ->
                        Map.tryFind operationId capabilityOperationsById
                        |> Option.map (fun operation -> operation.ResultType)
                    | SemanticNodeKind.ProductConstruct(typeId, _) ->
                        Some(TypeRef.Named typeId)
                    | SemanticNodeKind.FieldAccess(_, fieldId) ->
                        Map.tryFind fieldId productFieldsById
                        |> Option.map (fun field -> field.Type)
                    | SemanticNodeKind.UnionConstruct(variantId, _) ->
                        Map.tryFind variantId variantsById
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
                    | SemanticNodeKind.Conditional(_, whenTrue, whenFalse) ->
                        commonType [ graphResultType whenTrue; graphResultType whenFalse ]
                    | SemanticNodeKind.MatchUnion(_, arms) ->
                        arms
                        |> List.map (fun arm -> graphResultType arm.Body)
                        |> commonType
                    | SemanticNodeKind.MatchOption(_, someArm, noneBody) ->
                        commonType [ graphResultType someArm.Body; graphResultType noneBody ]
                    | SemanticNodeKind.MatchResult(_, okArm, errArm) ->
                        commonType [ graphResultType okArm.Body; graphResultType errArm.Body ]

        tryNodeType Set.empty

    let nodeTypes
        (snapshot: Snapshot)
        (functionEntity: SemanticFunction)
        : Map<GraphNodeId, TypeRef option> =
        let resolve = createNodeTypeResolver snapshot functionEntity

        ValueGraph.collectNodes functionEntity.Body
        |> List.map (fun node -> node.Id, resolve node.Id)
        |> Map.ofList
