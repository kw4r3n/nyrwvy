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

/// A declaration, or a member of one, whose referencing sites a caller wants to repair before
/// deleting or removing it.
[<RequireQualifiedAccess>]
type ReferenceSubject =
    | Function of FunctionId
    | Type of TypeId
    | Variant of VariantId
    /// A Product field or a Union variant field.
    | Field of FieldId
    | Effect of EffectId
    | Capability of CapabilityId
    | CapabilityOperation of CapabilityOperationId

/// The declaration that owns a referencing site.
[<RequireQualifiedAccess>]
type ReferenceOwner =
    | Function of FunctionId
    | Type of TypeId
    | Capability of CapabilityId

/// Where inside its owner a reference occurs.
[<RequireQualifiedAccess>]
type ReferenceSite =
    | ParameterType of ParameterId
    | ResultType
    | RequiredEffect
    /// A field's declared type; `variantId` names the owning variant of a Union field.
    | FieldType of variantId: VariantId option * FieldId
    | OperationParameterType of CapabilityOperationId * CapabilityParameterId
    | OperationResultType of CapabilityOperationId
    | OperationFailureType of CapabilityOperationId
    | OperationEffect of CapabilityOperationId
    /// A node of the owning function's body (nested bodies included); `role` names how the node
    /// refers to the subject, for example `directCall`, `construct`, `fieldAccess`, `match`,
    /// `invoke`, `implementation`, `typeArgument` or `patternBinding`.
    | Node of GraphNodeId * role: string

type DeclarationReference =
    { Owner: ReferenceOwner
      Site: ReferenceSite }

[<RequireQualifiedAccess>]
type SemanticQueryError =
    | FunctionNotFound of FunctionId
    | TypeNotFound of TypeId
    | NodeNotFound of FunctionId * GraphNodeId
    /// The subject of a reference query is not declared; `kind` is its subject kind in camel case.
    | SubjectNotFound of kind: string * id: string

[<RequireQualifiedAccess>]
module SemanticQuery =
    let private callSites (snapshot: Snapshot): QueryCallSite list =
        snapshot.Functions
        |> List.collect (fun owner ->
            ValueGraph.collectNodes owner.Body
            |> List.collect (fun node ->
                let calleeIds =
                    SemanticNodeShape.functionReferences node |> List.map (fun reference -> reference.FunctionId)

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

    /// Whether a type reference mentions a type or capability, at any depth.
    let rec private typeRefMentions (predicate: TypeRef -> bool) (typeRef: TypeRef) =
        predicate typeRef
        || (match typeRef with
            | TypeRef.Primitive _
            | TypeRef.TypeParameter _
            | TypeRef.Capability _ -> false
            | TypeRef.Function(arguments, result) -> (arguments @ [ result ]) |> List.exists (typeRefMentions predicate)
            | TypeRef.BuiltinGeneric(_, arguments)
            | TypeRef.Named(_, arguments) -> arguments |> List.exists (typeRefMentions predicate))

    let private roleName (role: FunctionReferenceRole) =
        match role with
        | FunctionReferenceRole.DirectCall -> "directCall"
        | FunctionReferenceRole.PureListCallback
        | FunctionReferenceRole.EffectListCallback -> "callback"
        | FunctionReferenceRole.Worker -> "worker"
        | FunctionReferenceRole.Body -> "body"
        | FunctionReferenceRole.FunctionValue -> "functionValue"
        | FunctionReferenceRole.CapabilityImplementation _ -> "implementation"

    /// Every site of the snapshot that refers to `subject`: declaration signatures (function
    /// parameters and results, required effects, field types, operation signatures and effects)
    /// and body nodes, nested bodies included. Each node is reported once per role. The list is
    /// what must be repaired before `deleteType`, `deleteEffect`, `deleteCapability`,
    /// `deleteCapabilityOperation`, `deleteFunction`, `removeProductField` or `removeUnionVariant`
    /// can succeed; a function's references to itself are included and marked by their owner.
    let references (snapshot: Snapshot) (subject: ReferenceSubject): Result<DeclarationReference list, SemanticQueryError> =
        let variantsOf (typeId: TypeId) =
            snapshot.Types
            |> List.tryFind (fun item -> item.Id = typeId)
            |> Option.map (fun item ->
                match item.Kind with
                | SemanticTypeKind.Union variants -> variants |> List.map (fun variant -> variant.Id) |> Set.ofList
                | SemanticTypeKind.Product _ -> Set.empty)
            |> Option.defaultValue Set.empty
        let fieldsOf (typeId: TypeId) =
            snapshot.Types
            |> List.tryFind (fun item -> item.Id = typeId)
            |> Option.map (fun item ->
                match item.Kind with
                | SemanticTypeKind.Product fields -> fields |> List.map (fun field -> field.Id) |> Set.ofList
                | SemanticTypeKind.Union variants -> variants |> List.collect (fun variant -> variant.Fields |> List.map (fun field -> field.Id)) |> Set.ofList)
            |> Option.defaultValue Set.empty
        let operationsOf (capabilityId: CapabilityId) =
            snapshot.Capabilities
            |> List.tryFind (fun item -> item.Id = capabilityId)
            |> Option.map (fun item -> item.Operations |> List.map (fun operation -> operation.Id) |> Set.ofList)
            |> Option.defaultValue Set.empty
        let declared =
            match subject with
            | ReferenceSubject.Function id -> snapshot.Functions |> List.exists (fun item -> item.Id = id), "function", StableId.value id
            | ReferenceSubject.Type id -> snapshot.Types |> List.exists (fun item -> item.Id = id), "type", StableId.value id
            | ReferenceSubject.Variant id ->
                snapshot.Types |> List.exists (fun item -> Set.contains id (variantsOf item.Id)), "variant", StableId.value id
            | ReferenceSubject.Field id ->
                snapshot.Types |> List.exists (fun item -> Set.contains id (fieldsOf item.Id)), "field", StableId.value id
            | ReferenceSubject.Effect id -> snapshot.Effects |> List.exists (fun item -> item.Id = id), "effect", StableId.value id
            | ReferenceSubject.Capability id -> snapshot.Capabilities |> List.exists (fun item -> item.Id = id), "capability", StableId.value id
            | ReferenceSubject.CapabilityOperation id ->
                snapshot.Capabilities |> List.exists (fun item -> Set.contains id (operationsOf item.Id)), "capabilityOperation", StableId.value id
        match declared with
        | false, kind, id -> Error(SemanticQueryError.SubjectNotFound(kind, id))
        | true, _, _ ->
            // Type references mention a type or a capability; the other subjects never occur in one.
            let mentions: TypeRef -> bool =
                match subject with
                | ReferenceSubject.Type typeId -> typeRefMentions (function TypeRef.Named(id, _) -> id = typeId | _ -> false)
                | ReferenceSubject.Capability capabilityId -> typeRefMentions (function TypeRef.Capability id -> id = capabilityId | _ -> false)
                | _ -> fun _ -> false
            let effectMatches effectId =
                match subject with
                | ReferenceSubject.Effect id -> id = effectId
                | _ -> false
            let variants, fields, operations =
                match subject with
                | ReferenceSubject.Type typeId -> variantsOf typeId, fieldsOf typeId, Set.empty
                | ReferenceSubject.Variant variantId -> Set.singleton variantId, Set.empty, Set.empty
                | ReferenceSubject.Field fieldId -> Set.empty, Set.singleton fieldId, Set.empty
                | ReferenceSubject.Capability capabilityId -> Set.empty, Set.empty, operationsOf capabilityId
                | ReferenceSubject.CapabilityOperation operationId -> Set.empty, Set.empty, Set.singleton operationId
                | ReferenceSubject.Function _
                | ReferenceSubject.Effect _ -> Set.empty, Set.empty, Set.empty
            let constructsType typeId =
                match subject with
                | ReferenceSubject.Type id -> id = typeId
                | _ -> false
            let bindsField (bindings: FieldValueBinding list) = bindings |> List.exists (fun binding -> Set.contains binding.TargetFieldId fields)
            let functionSites (owner: SemanticFunction) =
                let signature =
                    (owner.Parameters |> List.choose (fun parameter ->
                        if mentions parameter.Type then Some(ReferenceSite.ParameterType parameter.Id) else None))
                    @ (if mentions owner.ResultType then [ ReferenceSite.ResultType ] else [])
                    @ (if owner.RequiredEffects |> List.exists effectMatches then [ ReferenceSite.RequiredEffect ] else [])
                let nodeSites (node: SemanticNode) =
                    let roles =
                        [ match subject with
                          | ReferenceSubject.Function functionId ->
                              for reference in SemanticNodeShape.functionReferences node do
                                  if reference.FunctionId = functionId then yield roleName reference.Role
                          | _ -> ()
                          if SemanticNodeShape.payloadTypeRefs node |> List.exists mentions then yield "typeArgument"
                          match node.Kind with
                          | SemanticNodeKind.ProductConstruct(typeId, _, bindings) ->
                              if constructsType typeId || bindsField bindings then yield "construct"
                          | SemanticNodeKind.UnionConstruct(variantId, _, bindings) ->
                              if Set.contains variantId variants || bindsField bindings then yield "construct"
                          | SemanticNodeKind.ProductUpdate(typeId, _, bindings) ->
                              if constructsType typeId || bindsField bindings then yield "productUpdate"
                          | SemanticNodeKind.FieldAccess(_, fieldId) -> if Set.contains fieldId fields then yield "fieldAccess"
                          | SemanticNodeKind.MatchUnion(_, arms) ->
                              if arms |> List.exists (fun arm -> arm.VariantIds |> List.exists (fun id -> Set.contains id variants)) then yield "match"
                              if arms |> List.exists (fun arm -> arm.FieldBindings |> List.exists (fun binding -> Set.contains binding.SourceFieldId fields)) then
                                  yield "patternBinding"
                          | SemanticNodeKind.CapabilityConstruct(capabilityId, implementations) ->
                              match subject with
                              | ReferenceSubject.Capability id when id = capabilityId -> yield "capabilityConstruct"
                              | _ -> ()
                              if implementations |> List.exists (fun (operationId, _) -> Set.contains operationId operations) then yield "implementation"
                          | SemanticNodeKind.CapabilityInvoke(_, operationId, _) -> if Set.contains operationId operations then yield "invoke"
                          | SemanticNodeKind.UseResource(_, _, releaseOperationId, _, _) ->
                              if Set.contains releaseOperationId operations then yield "release"
                          | _ -> ()
                          // The types a nested body binds (match arm fields, Bind values, Option/Result payloads).
                          let bindingTypes =
                              SemanticNodeShape.nestedScopes node
                              |> List.collect (fun scope -> scope.Bindings |> List.map (fun binding -> (NestedBinding.pattern binding).Type))
                          if bindingTypes |> List.exists mentions then yield "patternBinding" ]
                    roles |> List.distinct |> List.map (fun role -> ReferenceSite.Node(node.Id, role))
                let body = ValueGraph.collectNodes owner.Body |> List.collect nodeSites
                (signature @ body) |> List.map (fun site -> { Owner = ReferenceOwner.Function owner.Id; Site = site })
            let typeSites (owner: SemanticType) =
                let fieldSites variantId (declaredFields: SemanticField list) =
                    declaredFields |> List.choose (fun field ->
                        if mentions field.Type then Some { Owner = ReferenceOwner.Type owner.Id; Site = ReferenceSite.FieldType(variantId, field.Id) } else None)
                match owner.Kind with
                | SemanticTypeKind.Product declaredFields -> fieldSites None declaredFields
                | SemanticTypeKind.Union declaredVariants -> declaredVariants |> List.collect (fun variant -> fieldSites (Some variant.Id) variant.Fields)
            let capabilitySites (owner: SemanticCapability) =
                owner.Operations
                |> List.collect (fun operation ->
                    (operation.Parameters |> List.choose (fun parameter ->
                        if mentions parameter.Type then Some(ReferenceSite.OperationParameterType(operation.Id, parameter.Id)) else None))
                    @ (if mentions operation.ResultType then [ ReferenceSite.OperationResultType operation.Id ] else [])
                    @ (match operation.FailureType with
                       | Some failureType when mentions failureType -> [ ReferenceSite.OperationFailureType operation.Id ]
                       | _ -> [])
                    @ (if effectMatches operation.EffectId then [ ReferenceSite.OperationEffect operation.Id ] else []))
                |> List.map (fun site -> { Owner = ReferenceOwner.Capability owner.Id; Site = site })
            let ownerKey (reference: DeclarationReference) =
                match reference.Owner with
                | ReferenceOwner.Function id -> 0, StableId.value id
                | ReferenceOwner.Type id -> 1, StableId.value id
                | ReferenceOwner.Capability id -> 2, StableId.value id
            (snapshot.Functions |> List.sortBy (fun item -> StableId.value item.Id) |> List.collect functionSites)
            @ (snapshot.Types |> List.sortBy (fun item -> StableId.value item.Id) |> List.collect typeSites)
            @ (snapshot.Capabilities |> List.sortBy (fun item -> StableId.value item.Id) |> List.collect capabilitySites)
            |> List.sortBy (fun reference -> ownerKey reference, sprintf "%A" reference.Site)
            |> Ok

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
            | TypeRef.Named(typeId, arguments) ->
                arguments |> List.iter visitTypeRef
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
        let dependentsOf =
            nodes
            |> List.collect (fun node -> SemanticNodeShape.directValueReferences node |> List.map (fun id -> id, node.Id))
            |> List.groupBy fst
            |> List.map (fun (id, pairs) -> id, List.map snd pairs)
            |> Map.ofList
        // Every node that transitively consumes a value in `found`, by a worklist over `dependentsOf`.
        let rec close found pending =
            match pending with
            | [] -> found
            | id :: rest ->
                let added =
                    Map.tryFind id dependentsOf |> Option.defaultValue [] |> List.distinct
                    |> List.filter (fun dependent -> not (Set.contains dependent found))
                close (Set.union found (Set.ofList added)) (added @ rest)
        let rec visit parents (graph: ValueGraph) =
            graph.Nodes |> List.collect (fun node ->
                match node.Kind with
                | SemanticNodeKind.Abort error ->
                    let seeds = node.Id :: parents |> Set.ofList
                    let dependent = close seeds (Set.toList seeds)
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
