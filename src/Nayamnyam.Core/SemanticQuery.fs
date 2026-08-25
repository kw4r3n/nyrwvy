namespace Nayamnyam.Core

type QueryCallSite =
    { CallerFunctionId: FunctionId
      CallNodeId: GraphNodeId
      CalleeFunctionId: FunctionId }

type QueryNodeType =
    { NodeId: GraphNodeId
      Type: TypeRef option }

type QueryOpenObligation =
    { ObligationId: ObligationId
      OriginTransactionId: TransactionId
      CallNodeId: GraphNodeId
      ParameterId: ParameterId }

type FunctionFocusView =
    { SnapshotId: SnapshotId
      Function: SemanticFunction
      NodeTypes: QueryNodeType list
      Callers: QueryCallSite list
      Callees: QueryCallSite list
      OpenObligations: QueryOpenObligation list
      ReferencedTypes: SemanticType list
      ReferencedCapabilities: SemanticCapability list
      RequiredEffects: SemanticEffect list }

[<RequireQualifiedAccess>]
type SemanticQueryError =
    | FunctionNotFound of FunctionId
    | TypeNotFound of TypeId
    | NodeNotFound of FunctionId * GraphNodeId

[<RequireQualifiedAccess>]
module SemanticQuery =
    let private callSites (snapshot: Snapshot): QueryCallSite list =
        snapshot.Functions
        |> List.collect (fun owner ->
            ValueGraph.collectNodes owner.Body
            |> List.choose (fun node ->
                match node.Kind with
                | SemanticNodeKind.DirectCall(calleeId, _) ->
                    Some
                        { CallerFunctionId = owner.Id
                          CallNodeId = node.Id
                          CalleeFunctionId = calleeId }
                | SemanticNodeKind.ParameterReference _
                | SemanticNodeKind.PatternReference _
                | SemanticNodeKind.StringLiteral _
                | SemanticNodeKind.BoolLiteral _
                | SemanticNodeKind.UnitLiteral
                | SemanticNodeKind.StringIsEmpty _
                | SemanticNodeKind.StringContains _
                | SemanticNodeKind.CapabilityInvoke _
                | SemanticNodeKind.ProductConstruct _
                | SemanticNodeKind.FieldAccess _
                | SemanticNodeKind.UnionConstruct _
                | SemanticNodeKind.OptionNone _
                | SemanticNodeKind.OptionSome _
                | SemanticNodeKind.ResultOk _
                | SemanticNodeKind.ResultErr _
                | SemanticNodeKind.Conditional _
                | SemanticNodeKind.MatchUnion _
                | SemanticNodeKind.MatchOption _
                | SemanticNodeKind.MatchResult _ -> None))
        |> List.sortBy (fun site ->
            StableId.value site.CallerFunctionId,
            StableId.value site.CallNodeId,
            StableId.value site.CalleeFunctionId)

    let rec private collectReferencedTypeIds (typeRef: TypeRef): TypeId list =
        match typeRef with
        | TypeRef.Primitive _
        | TypeRef.Capability _ -> []
        | TypeRef.Named typeId -> [ typeId ]
        | TypeRef.BuiltinGeneric(_, arguments) ->
            arguments |> List.collect collectReferencedTypeIds

    let rec private collectReferencedCapabilityIds (typeRef: TypeRef): CapabilityId list =
        match typeRef with
        | TypeRef.Primitive _
        | TypeRef.Named _ -> []
        | TypeRef.Capability capabilityId -> [ capabilityId ]
        | TypeRef.BuiltinGeneric(_, arguments) ->
            arguments |> List.collect collectReferencedCapabilityIds

    let getFunction
        (snapshot: Snapshot)
        (functionId: FunctionId)
        : Result<SemanticFunction, SemanticQueryError> =
        match snapshot.Functions |> List.tryFind (fun functionEntity -> functionEntity.Id = functionId) with
        | Some functionEntity -> Ok functionEntity
        | None -> Error(SemanticQueryError.FunctionNotFound functionId)

    let getType
        (snapshot: Snapshot)
        (typeId: TypeId)
        : Result<SemanticType, SemanticQueryError> =
        match snapshot.Types |> List.tryFind (fun typeEntity -> typeEntity.Id = typeId) with
        | Some typeEntity -> Ok typeEntity
        | None -> Error(SemanticQueryError.TypeNotFound typeId)

    let getCallers
        (snapshot: Snapshot)
        (functionId: FunctionId)
        : Result<QueryCallSite list, SemanticQueryError> =
        getFunction snapshot functionId
        |> Result.map (fun _ ->
            callSites snapshot
            |> List.filter (fun site -> site.CalleeFunctionId = functionId))

    let getCallees
        (snapshot: Snapshot)
        (functionId: FunctionId)
        : Result<QueryCallSite list, SemanticQueryError> =
        getFunction snapshot functionId
        |> Result.map (fun _ ->
            callSites snapshot
            |> List.filter (fun site -> site.CallerFunctionId = functionId))

    let getOpenObligations
        (snapshot: Snapshot)
        (functionId: FunctionId)
        : Result<QueryOpenObligation list, SemanticQueryError> =
        getFunction snapshot functionId
        |> Result.map (fun functionEntity ->
            let ownedNodeIds =
                ValueGraph.collectNodes functionEntity.Body
                |> List.map (fun node -> node.Id)
                |> Set.ofList

            snapshot.Obligations
            |> List.choose (fun obligation ->
                match obligation.Status with
                | ObligationStatus.Open when Set.contains obligation.CallNodeId ownedNodeIds ->
                    Some
                        { ObligationId = obligation.Id
                          OriginTransactionId = obligation.OriginTransactionId
                          CallNodeId = obligation.CallNodeId
                          ParameterId = obligation.ParameterId }
                | ObligationStatus.Open
                | ObligationStatus.Resolved _ -> None)
            |> List.sortBy (fun obligation -> StableId.value obligation.ObligationId))

    let getDependencies
        (snapshot: Snapshot)
        (functionId: FunctionId)
        (nodeId: GraphNodeId)
        : Result<GraphNodeId list, SemanticQueryError> =
        getFunction snapshot functionId
        |> Result.bind (fun functionEntity ->
            match ValueGraph.collectNodes functionEntity.Body |> List.tryFind (fun node -> node.Id = nodeId) with
            | None -> Error(SemanticQueryError.NodeNotFound(functionId, nodeId))
            | Some node ->
                SemanticFacts.directValueDependencies node
                |> List.distinct
                |> List.sortBy StableId.value
                |> Ok)

    let getUses
        (snapshot: Snapshot)
        (functionId: FunctionId)
        (nodeId: GraphNodeId)
        : Result<GraphNodeId list, SemanticQueryError> =
        getFunction snapshot functionId
        |> Result.bind (fun functionEntity ->
            let nodes = ValueGraph.collectNodes functionEntity.Body
            if nodes |> List.exists (fun node -> node.Id = nodeId) |> not then
                Error(SemanticQueryError.NodeNotFound(functionId, nodeId))
            else
                nodes
                |> List.choose (fun node ->
                    if SemanticFacts.directValueDependencies node |> List.contains nodeId then
                        Some node.Id
                    else
                        None)
                |> List.distinct
                |> List.sortBy StableId.value
                |> Ok)

    let focusFunction
        (snapshot: Snapshot)
        (functionId: FunctionId)
        : Result<FunctionFocusView, SemanticQueryError> =
        getFunction snapshot functionId
        |> Result.bind (fun functionEntity ->
            let nodeTypes =
                SemanticFacts.nodeTypes snapshot functionEntity
                |> Map.toList
                |> List.map (fun (nodeId, nodeType) ->
                    { NodeId = nodeId
                      Type = nodeType })
                |> List.sortBy (fun item -> StableId.value item.NodeId)

            let allTypeRefs =
                (functionEntity.Parameters |> List.map (fun parameter -> parameter.Type))
                @ [ functionEntity.ResultType ]
                @ (nodeTypes |> List.choose (fun item -> item.Type))

            let referencedTypeIds =
                allTypeRefs
                |> List.collect collectReferencedTypeIds
                |> Set.ofList

            let referencedCapabilityIds =
                allTypeRefs
                |> List.collect collectReferencedCapabilityIds
                |> Set.ofList

            let referencedTypes =
                snapshot.Types
                |> List.filter (fun typeEntity -> Set.contains typeEntity.Id referencedTypeIds)
                |> List.sortBy (fun typeEntity -> StableId.value typeEntity.Id)

            let referencedCapabilities =
                snapshot.Capabilities
                |> List.filter (fun capability -> Set.contains capability.Id referencedCapabilityIds)
                |> List.sortBy (fun capability -> StableId.value capability.Id)

            let requiredEffects =
                let ids = Set.ofList functionEntity.RequiredEffects
                snapshot.Effects
                |> List.filter (fun effect -> Set.contains effect.Id ids)
                |> List.sortBy (fun effect -> StableId.value effect.Id)

            match
                getCallers snapshot functionId,
                getCallees snapshot functionId,
                getOpenObligations snapshot functionId
            with
            | Ok callers, Ok callees, Ok obligations ->
                Ok
                    { SnapshotId = snapshot.Id
                      Function = functionEntity
                      NodeTypes = nodeTypes
                      Callers = callers
                      Callees = callees
                      OpenObligations = obligations
                      ReferencedTypes = referencedTypes
                      ReferencedCapabilities = referencedCapabilities
                      RequiredEffects = requiredEffects }
            | Error error, _, _
            | _, Error error, _
            | _, _, Error error -> Error error)