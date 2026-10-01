namespace Nyrwvy.Core

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

type QueryFunctionSignature =
    { FunctionId: FunctionId
      DisplayName: string
      Parameters: SemanticParameter list
      ResultType: TypeRef
      RequiredEffects: EffectId list }

/// A static local exit boundary; continuation IDs are value dependents of its enclosing controls,
/// not a claim about runtime reachability on every branch.
type QueryFunctionAbort =
    { OwnerFunctionId: FunctionId
      NodeId: GraphNodeId
      ErrorNodeId: GraphNodeId
      ErrorType: TypeRef option
      EnclosingControlNodeIds: GraphNodeId list
      DependentContinuationNodeIds: GraphNodeId list }

type FunctionFocusView =
    { SnapshotId: SnapshotId
      Function: SemanticFunction
      BodyEffectRequirements: BodyEffectRequirements
      NodeTypes: QueryNodeType list
      Aborts: QueryFunctionAbort list
      Callers: QueryCallSite list
      Callees: QueryCallSite list
      RelatedFunctionSignatures: QueryFunctionSignature list
      OpenObligations: QueryOpenObligation list
      ReferencedTypes: SemanticType list
      ReferencedCapabilities: SemanticCapability list
      ReferencedEffects: SemanticEffect list
      MissingFunctionIds: FunctionId list
      MissingTypeIds: TypeId list
      MissingCapabilityIds: CapabilityId list
      MissingEffectIds: EffectId list }

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
            |> List.collect (fun node ->
                let calleeIds =
                    match node.Kind with
                    | SemanticNodeKind.CapabilityConstruct(_, implementations) -> List.map snd implementations
                    | SemanticNodeKind.FunctionReference functionId
                    | SemanticNodeKind.DirectCall(functionId, _)
                    | SemanticNodeKind.ListFold(_, _, functionId, _)
                    | SemanticNodeKind.ListEffectFold(_, _, functionId, _)
                    | SemanticNodeKind.UseResource(_, _, _, functionId, _)
                    | SemanticNodeKind.ParallelAll(_, functionId, _)
                    | SemanticNodeKind.TaskScope(functionId, _)
                    | SemanticNodeKind.TaskStart(_, functionId, _, _)
                    | SemanticNodeKind.ListMap(_, functionId, _)
                    | SemanticNodeKind.ListFilter(_, functionId, _)
                    | SemanticNodeKind.ListSortBy(_, functionId, _)
                    | SemanticNodeKind.ListAny(_, functionId, _)
                    | SemanticNodeKind.ListAll(_, functionId, _) -> [ functionId ]
                    | SemanticNodeKind.FunctionInvoke _
                    | SemanticNodeKind.CancellationRequested
                    | SemanticNodeKind.TaskWait _
                    | SemanticNodeKind.TaskCancel _
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
                    | SemanticNodeKind.Float64ToString _
                    | SemanticNodeKind.StringParseFloat64 _
                    | SemanticNodeKind.StringCodePoints _
                    | SemanticNodeKind.StringToLower _
                    | SemanticNodeKind.StringUtf8ByteCount _
                    | SemanticNodeKind.StringFromCodePoint _
                    | SemanticNodeKind.StringToCodePoint _
                    | SemanticNodeKind.StringLength _
                    | SemanticNodeKind.StringSlice _
                    | SemanticNodeKind.StringContains _
                    | SemanticNodeKind.StringIndexOf _
                    | SemanticNodeKind.StringSpan _
                    | SemanticNodeKind.StringSpanBack _
                    | SemanticNodeKind.StringLastIndexOf _
                    | SemanticNodeKind.StringStartsWith _
                    | SemanticNodeKind.StringEndsWith _
                    | SemanticNodeKind.StringJoin _
                    | SemanticNodeKind.StringSplit _
                    | SemanticNodeKind.StringSplitLines _
                    | SemanticNodeKind.StringEqual _
                    | SemanticNodeKind.StringLessThan _
                    | SemanticNodeKind.ValueEqual _
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
                    | SemanticNodeKind.Int64ToFloat64 _
                    | SemanticNodeKind.Bits32FromInt64 _
                    | SemanticNodeKind.Bits32ToInt64 _
                    | SemanticNodeKind.Bits32Xor _
                    | SemanticNodeKind.Bits32And _
                    | SemanticNodeKind.Bits32Or _
                    | SemanticNodeKind.Bits32Not _
                    | SemanticNodeKind.Bits32Add _
                    | SemanticNodeKind.Bits32RotateRight _
                    | SemanticNodeKind.Bits32ShiftRight _
                    | SemanticNodeKind.Bits32ShiftLeft _
                    | SemanticNodeKind.ListEmpty _
                    | SemanticNodeKind.ListConstruct _
                    | SemanticNodeKind.ListLength _
                    | SemanticNodeKind.ListSlice _
                    | SemanticNodeKind.ListItemAt _
                    | SemanticNodeKind.ListAppendItem _
                    | SemanticNodeKind.ListConcat _
                    | SemanticNodeKind.CapabilityInvoke _
                    | SemanticNodeKind.ProductConstruct _
                    | SemanticNodeKind.FieldAccess _
                    | SemanticNodeKind.ProductUpdate _
                    | SemanticNodeKind.UnionConstruct _
                    | SemanticNodeKind.OptionNone _
                    | SemanticNodeKind.OptionSome _
                    | SemanticNodeKind.ResultOk _
                    | SemanticNodeKind.Abort _
                    | SemanticNodeKind.ResultErr _
                    | SemanticNodeKind.BoolNot _
                    | SemanticNodeKind.BoolAnd _
                    | SemanticNodeKind.BoolOr _
                    | SemanticNodeKind.Conditional _
                    | SemanticNodeKind.MatchUnion _
                    | SemanticNodeKind.Bind _
                    | SemanticNodeKind.MatchOption _
                    | SemanticNodeKind.MatchResult _ -> []

                calleeIds
                |> List.distinct
                |> List.map (fun functionId ->
                    { CallerFunctionId = owner.Id
                      CallNodeId = node.Id
                      CalleeFunctionId = functionId })))
        |> List.sortBy (fun site ->
            StableId.value site.CallerFunctionId,
            StableId.value site.CallNodeId,
            StableId.value site.CalleeFunctionId)

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
                SemanticNodeShape.directValueReferences node
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
                    if SemanticNodeShape.directValueReferences node |> List.contains nodeId then
                        Some node.Id
                    else
                        None)
                |> List.distinct
                |> List.sortBy StableId.value
                |> Ok)

    type private DeclarationClosure =
        { TypeIds: Set<TypeId>
          CapabilityIds: Set<CapabilityId>
          EffectIds: Set<EffectId>
          MissingTypeIds: Set<TypeId>
          MissingCapabilityIds: Set<CapabilityId> }

    let private declarationClosure
        (snapshot: Snapshot)
        (seedTypeRefs: TypeRef list)
        (seedEffectIds: EffectId list)
        : DeclarationClosure =
        let typesById = snapshot.Types |> List.map (fun item -> item.Id, item) |> Map.ofList
        let capabilitiesById = snapshot.Capabilities |> List.map (fun item -> item.Id, item) |> Map.ofList

        let mutable typeIds: Set<TypeId> = Set.empty
        let mutable capabilityIds: Set<CapabilityId> = Set.empty
        let mutable effectIds: Set<EffectId> = Set.ofList seedEffectIds
        let mutable missingTypeIds: Set<TypeId> = Set.empty
        let mutable missingCapabilityIds: Set<CapabilityId> = Set.empty

        let rec visitTypeRef (typeRef: TypeRef) =
            match typeRef with
            | TypeRef.Primitive _
            | TypeRef.TypeParameter _ -> ()
            | TypeRef.Function(arguments, result) -> (arguments @ [ result ]) |> List.iter visitTypeRef
            | TypeRef.BuiltinGeneric(_, arguments) -> arguments |> List.iter visitTypeRef
            | TypeRef.Named typeId ->
                if not (Set.contains typeId typeIds) then
                    typeIds <- Set.add typeId typeIds
                    match Map.tryFind typeId typesById with
                    | None -> missingTypeIds <- Set.add typeId missingTypeIds
                    | Some declaration ->
                        match declaration.Kind with
                        | SemanticTypeKind.Product fields ->
                            fields |> List.iter (fun field -> visitTypeRef field.Type)
                        | SemanticTypeKind.Union variants ->
                            variants
                            |> List.collect (fun variant -> variant.Fields)
                            |> List.iter (fun field -> visitTypeRef field.Type)
            | TypeRef.Capability capabilityId ->
                if not (Set.contains capabilityId capabilityIds) then
                    capabilityIds <- Set.add capabilityId capabilityIds
                    match Map.tryFind capabilityId capabilitiesById with
                    | None -> missingCapabilityIds <- Set.add capabilityId missingCapabilityIds
                    | Some capability ->
                        capability.Operations
                        |> List.iter (fun operation ->
                            effectIds <- Set.add operation.EffectId effectIds
                            operation.Parameters |> List.iter (fun parameter -> visitTypeRef parameter.Type)
                            visitTypeRef operation.ResultType
                            operation.FailureType |> Option.iter visitTypeRef)

        seedTypeRefs |> List.iter visitTypeRef

        { TypeIds = typeIds
          CapabilityIds = capabilityIds
          EffectIds = effectIds
          MissingTypeIds = missingTypeIds
          MissingCapabilityIds = missingCapabilityIds }

    let private signatureOf (functionEntity: SemanticFunction): QueryFunctionSignature =
        { FunctionId = functionEntity.Id
          DisplayName = functionEntity.DisplayName
          Parameters = functionEntity.Parameters
          ResultType = functionEntity.ResultType
          RequiredEffects = functionEntity.RequiredEffects }

    let private abortBoundaries (functionEntity: SemanticFunction) (nodeTypes: QueryNodeType list) =
        let nodes = ValueGraph.collectNodes functionEntity.Body
        let types = nodeTypes |> List.map (fun item -> item.NodeId, item.Type) |> Map.ofList
        let rec visit parents (graph: ValueGraph) =
            graph.Nodes |> List.collect (fun node ->
                match node.Kind with
                | SemanticNodeKind.Abort error ->
                    let seeds = node.Id :: parents |> Set.ofList
                    let mutable dependent = seeds
                    let mutable changed = true
                    while changed do
                        let next = nodes |> List.fold (fun found candidate ->
                            if SemanticNodeShape.directValueReferences candidate |> List.exists (fun id -> Set.contains id found)
                            then Set.add candidate.Id found else found) dependent
                        changed <- next <> dependent
                        dependent <- next
                    [ { OwnerFunctionId = functionEntity.Id; NodeId = node.Id; ErrorNodeId = error
                        ErrorType = Map.tryFind error types |> Option.flatten
                        EnclosingControlNodeIds = parents
                        DependentContinuationNodeIds = Set.difference dependent seeds |> Set.toList |> List.sortBy StableId.value } ]
                | _ -> SemanticNodeShape.nestedGraphs node |> List.collect (visit (node.Id :: parents)))
        visit [] functionEntity.Body |> List.sortBy (fun item -> StableId.value item.NodeId)

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

            match
                getCallers snapshot functionId,
                getCallees snapshot functionId,
                getOpenObligations snapshot functionId
            with
            | Error error, _, _
            | _, Error error, _
            | _, _, Error error -> Error error
            | Ok callers, Ok callees, Ok obligations ->
                let relatedFunctionIds =
                    (callers |> List.map (fun site -> site.CallerFunctionId))
                    @ (callees |> List.map (fun site -> site.CalleeFunctionId))
                    |> List.filter (fun id -> id <> functionId)
                    |> List.distinct
                    |> List.sortBy StableId.value

                let functionsById =
                    snapshot.Functions
                    |> List.map (fun item -> item.Id, item)
                    |> Map.ofList

                let relatedFunctions, missingFunctionIds =
                    relatedFunctionIds
                    |> List.fold
                        (fun (found, missing) id ->
                            match Map.tryFind id functionsById with
                            | Some related -> related :: found, missing
                            | None -> found, id :: missing)
                        ([], [])
                    |> fun (found, missing) -> List.rev found, List.rev missing

                let relatedSignatures = relatedFunctions |> List.map signatureOf

                let allTypeRefs =
                    (functionEntity.Parameters |> List.map (fun parameter -> parameter.Type))
                    @ [ functionEntity.ResultType ]
                    @ (nodeTypes |> List.choose (fun item -> item.Type))
                    @ (relatedFunctions
                       |> List.collect (fun related ->
                           (related.Parameters |> List.map (fun parameter -> parameter.Type))
                           @ [ related.ResultType ]))

                let seedEffectIds =
                    functionEntity.RequiredEffects
                    @ (relatedFunctions |> List.collect (fun related -> related.RequiredEffects))

                let closure = declarationClosure snapshot allTypeRefs seedEffectIds

                let referencedTypes =
                    snapshot.Types
                    |> List.filter (fun typeEntity -> Set.contains typeEntity.Id closure.TypeIds)
                    |> List.sortBy (fun typeEntity -> StableId.value typeEntity.Id)

                let referencedCapabilities =
                    snapshot.Capabilities
                    |> List.filter (fun capability -> Set.contains capability.Id closure.CapabilityIds)
                    |> List.sortBy (fun capability -> StableId.value capability.Id)

                let knownEffectIds = snapshot.Effects |> List.map (fun effect -> effect.Id) |> Set.ofList
                let referencedEffects =
                    snapshot.Effects
                    |> List.filter (fun effect -> Set.contains effect.Id closure.EffectIds)
                    |> List.sortBy (fun effect -> StableId.value effect.Id)

                let missingEffectIds =
                    closure.EffectIds
                    |> Set.filter (fun effectId -> not (Set.contains effectId knownEffectIds))
                    |> Set.toList
                    |> List.sortBy StableId.value

                Ok
                    { SnapshotId = snapshot.Id
                      Function = functionEntity
                      BodyEffectRequirements = EffectRequirements.derive snapshot functionEntity
                      NodeTypes = nodeTypes
                      Aborts = abortBoundaries functionEntity nodeTypes
                      Callers = callers
                      Callees = callees
                      RelatedFunctionSignatures = relatedSignatures
                      OpenObligations = obligations
                      ReferencedTypes = referencedTypes
                      ReferencedCapabilities = referencedCapabilities
                      ReferencedEffects = referencedEffects
                      MissingFunctionIds = missingFunctionIds
                      MissingTypeIds = closure.MissingTypeIds |> Set.toList |> List.sortBy StableId.value
                      MissingCapabilityIds = closure.MissingCapabilityIds |> Set.toList |> List.sortBy StableId.value
                      MissingEffectIds = missingEffectIds })
