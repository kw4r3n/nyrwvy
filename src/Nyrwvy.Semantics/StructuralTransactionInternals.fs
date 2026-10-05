namespace Nyrwvy.Core

open Microsoft.FSharp.Reflection

/// Recipe bounds for ordinary expression authoring. Public so that wire readers can derive their
/// JSON depth allowance from them instead of repeating the numbers.
[<RequireQualifiedAccess>]
module ExpressionRecipeBounds =
    let maxDepth = 32
    let maxNodes = 256

module internal StructuralTransactionInternals =
    let diagnostic code message: TransactionDiagnostic =
        { Code = code
          Message = message }

    let rejected code message =
        Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic code message ])

    /// Checks a type reference in a declaration (monomorphic, so no type parameters) against the
    /// known type and capability IDs. Diagnostic codes are `codePrefix` plus the failure name.
    let validateDeclaredTypeRef
        (codePrefix: string)
        (typeIds: Set<TypeId>)
        (capabilityIds: Set<CapabilityId>)
        (context: string)
        (typeRef: TypeRef)
        =
        let rec validate current =
            match current with
            | TypeRef.Primitive _ -> Ok()
            | TypeRef.TypeParameter _ ->
                Error(diagnostic $"{codePrefix}TypeParameterNotAllowed" $"{context} cannot use a function type parameter; declared types are monomorphic.")
            | TypeRef.Named typeId when Set.contains typeId typeIds -> Ok()
            | TypeRef.Named typeId ->
                Error(
                    diagnostic
                        $"{codePrefix}UnknownNamedType"
                        $"{context} references unknown type '{StableId.value typeId}'."
                )
            | TypeRef.Capability capabilityId when Set.contains capabilityId capabilityIds -> Ok()
            | TypeRef.Capability capabilityId ->
                Error(
                    diagnostic
                        $"{codePrefix}UnknownCapabilityType"
                        $"{context} references unknown capability '{StableId.value capabilityId}'."
                )
            | TypeRef.Function(arguments, result) ->
                (arguments @ [ result ]) |> List.fold (fun state arg -> state |> Result.bind (fun () -> validate arg)) (Ok())
            | TypeRef.BuiltinGeneric(kind, arguments) ->
                let expectedArity =
                    match kind with
                    | BuiltinGenericType.TaskScope -> 0
                    | BuiltinGenericType.Option
                    | BuiltinGenericType.List
                    | BuiltinGenericType.Set
                    | BuiltinGenericType.Task -> 1
                    | BuiltinGenericType.Result
                    | BuiltinGenericType.Map -> 2

                if List.length arguments <> expectedArity then
                    Error(
                        diagnostic
                            $"{codePrefix}BuiltinGenericArityMismatch"
                            $"{context} uses {kind} with {arguments.Length} type arguments; exactly {expectedArity} required."
                    )
                else
                    let rec loop = function
                        | [] -> Ok()
                        | argument :: rest ->
                            match validate argument with
                            | Error error -> Error error
                            | Ok() -> loop rest
                    loop arguments

        validate typeRef

    type RecipeBounds = { MaxDepth: int; MaxNodes: int }

    /// The frozen Milestone 2 bounded Float64 replacement contract.
    let boundedFloat64Bounds = { MaxDepth = 32; MaxNodes = 64 }

    /// Ordinary expression authoring: function creation, body replacement and targeted replacement.
    let expressionAuthoringBounds = { MaxDepth = ExpressionRecipeBounds.maxDepth; MaxNodes = ExpressionRecipeBounds.maxNodes }

    let private recipeCaseName (recipe: 'Recipe) =
        if FSharpType.IsUnion(recipe.GetType(), true) then
            let case, _ = FSharpValue.GetUnionFields(box recipe, recipe.GetType(), true)
            case.Name
        else
            recipe.GetType().Name

    /// Depth is checked before node count so the two diagnostic classes remain
    /// distinguishable for a recipe that violates both. A node-count failure reports
    /// the full count and the largest subtrees, which are the candidates to extract.
    let validateRecipeBoundsWith
        (bounds: RecipeBounds)
        (children: 'Recipe -> seq<'Recipe>)
        (recipe: 'Recipe)
        : Result<unit, TransactionDiagnostic> =
        let rec depthWithin depth current =
            depth <= bounds.MaxDepth && (children current |> Seq.forall (depthWithin (depth + 1)))

        let rec depthOf current =
            1 + (children current |> Seq.fold (fun deepest child -> max deepest (depthOf child)) 0)

        // (size, path, case name) for every proper subtree, collected in one pass.
        let rec measure path current (subtrees: ResizeArray<int * string * string>) =
            let size =
                children current
                |> Seq.mapi (fun index child -> measure $"{path}/{index}" child subtrees)
                |> Seq.sum
                |> (+) 1
            if path <> "" then subtrees.Add((size, path, recipeCaseName current))
            size

        if not (depthWithin 1 recipe) then
            Error(
                diagnostic
                    "StructuralRecipeDepthLimitExceeded"
                    $"The structural recipe is deeper than {bounds.MaxDepth} nodes (it is {depthOf recipe} nodes deep)."
            )
        else
            let subtrees = ResizeArray()
            let total = measure "" recipe subtrees
            if total <= bounds.MaxNodes then
                Ok()
            else
                // Largest subtrees that could become a helper within the limit; nested ones are skipped
                // when an enclosing subtree is already listed.
                let listed = ResizeArray<string>()
                for size, path, name in subtrees |> Seq.filter (fun (size, _, _) -> size <= bounds.MaxNodes) |> Seq.sortByDescending (fun (size, _, _) -> size) do
                    if listed.Count < 3 && not (listed |> Seq.exists (fun item -> path.StartsWith(item.Split(' ')[0] + "/"))) then
                        listed.Add $"{path} {name} ({size} nodes)"
                let largest =
                    if listed.Count = 0 then ""
                    else " Largest subtrees within the limit (child-index paths from the root): " + String.concat "; " listed + "."
                Error(
                    diagnostic
                        "StructuralRecipeNodeLimitExceeded"
                        $"The structural recipe contains {total} nodes, more than {bounds.MaxNodes}.{largest}"
                )

    let validateRecipeBounds children recipe =
        validateRecipeBoundsWith expressionAuthoringBounds children recipe

    let private unionCaseName (kind: SemanticNodeKind) =
        let case, _ = FSharpValue.GetUnionFields(box kind, typeof<SemanticNodeKind>)
        case.Name

    let private summaryExtra (node: SemanticNode) =
        match node.Kind with
        | SemanticNodeKind.ParameterReference parameterId -> Some parameterId, None, None
        | SemanticNodeKind.Float64Literal bits -> None, None, Some(Float64Bits.toHex bits)
        // Every other kind: the one function it names, if any. A capability construction names one
        // function per operation and shows none here.
        | _ ->
            let functionId =
                SemanticNodeShape.functionReferences node
                |> List.tryPick (fun reference ->
                    match reference.Role with
                    | FunctionReferenceRole.CapabilityImplementation _ -> None
                    | FunctionReferenceRole.DirectCall
                    | FunctionReferenceRole.PureListCallback
                    | FunctionReferenceRole.EffectListCallback
                    | FunctionReferenceRole.Worker
                    | FunctionReferenceRole.Body
                    | FunctionReferenceRole.FunctionValue -> Some reference.FunctionId)
            None, functionId, None

    let summarizeNodes
        (snapshot: Snapshot)
        (functionEntity: SemanticFunction)
        (nodes: SemanticNode list)
        : StructuralNodeSummary list =
        let nodeTypes = SemanticFacts.nodeTypes snapshot functionEntity
        // Name tables built at most once per call; the first entry wins, as a linear search would.
        let namesById entries = entries |> List.distinctBy fst |> Map.ofList
        let functionNames = lazy (snapshot.Functions |> List.map (fun item -> item.Id, item.DisplayName) |> namesById)
        let fieldNames =
            lazy (
                snapshot.Types
                |> List.collect (fun item ->
                    match item.Kind with
                    | SemanticTypeKind.Product fields -> fields
                    | SemanticTypeKind.Union variants -> variants |> List.collect (fun variant -> variant.Fields))
                |> List.map (fun item -> item.Id, item.DisplayName)
                |> namesById)
        let variantNames =
            lazy (
                snapshot.Types
                |> List.collect (fun item ->
                    match item.Kind with
                    | SemanticTypeKind.Union variants -> variants
                    | SemanticTypeKind.Product _ -> [])
                |> List.map (fun item -> item.Id, item.DisplayName)
                |> namesById)
        let functionName id =
            if id = functionEntity.Id then Some functionEntity.DisplayName
            else Map.tryFind id functionNames.Value
        let referenceName (node: SemanticNode) functionId =
            match node.Kind with
            | SemanticNodeKind.ParameterReference parameterId ->
                functionEntity.Parameters |> List.tryFind (fun item -> item.Id = parameterId) |> Option.map (fun item -> item.DisplayName)
            | SemanticNodeKind.FieldAccess(_, fieldId) ->
                Map.tryFind fieldId fieldNames.Value
            | SemanticNodeKind.UnionConstruct(variantId, _) ->
                Map.tryFind variantId variantNames.Value
            | SemanticNodeKind.ProductConstruct(typeId, _)
            | SemanticNodeKind.ProductUpdate(typeId, _, _) ->
                snapshot.Types |> List.tryFind (fun item -> item.Id = typeId) |> Option.map (fun item -> item.DisplayName)
            | SemanticNodeKind.CapabilityInvoke(_, operationId, _) ->
                snapshot.Capabilities |> List.collect (fun item -> item.Operations)
                |> List.tryFind (fun item -> item.Id = operationId) |> Option.map (fun item -> item.DisplayName)
            | SemanticNodeKind.CapabilityConstruct(capabilityId, _) ->
                snapshot.Capabilities |> List.tryFind (fun item -> item.Id = capabilityId) |> Option.map (fun item -> item.DisplayName)
            | _ -> functionId |> Option.bind functionName
        let literal (node: SemanticNode) =
            match node.Kind with
            | SemanticNodeKind.BoolLiteral value -> Some(StructuralNodeLiteral.Bool value)
            | SemanticNodeKind.Int64Literal value -> Some(StructuralNodeLiteral.Int64 value)
            | SemanticNodeKind.StringLiteral value -> Some(StructuralNodeLiteral.String value)
            | _ -> None
        nodes
        |> List.map (fun node ->
            let parameterId, functionId, bitsHex = summaryExtra node
            let valueDependencies = ValueDependency.ofNode node
            { NodeId = node.Id
              Role = unionCaseName node.Kind
              Type = Map.tryFind node.Id nodeTypes |> Option.defaultValue None
              Dependencies = valueDependencies |> List.map (fun dependency -> dependency.NodeId)
              ValueDependencies = valueDependencies
              ParameterId = parameterId
              FunctionId = functionId
              Float64BitsHex = bitsHex
              Literal = literal node
              ReferenceDisplayName = referenceName node functionId })

    let validationDiagnostics (issues: ValidationIssue list) =
        issues |> List.map (fun issue -> diagnostic ("Validation." + issue.Code) issue.Message)

    let classifyReadiness snapshot =
        match SnapshotReadiness.evaluate snapshot with
        | Error issues -> Error(validationDiagnostics issues)
        | Ok SemanticReadiness.Complete -> Ok TransactionReadiness.SemanticallyReady
        | Ok(SemanticReadiness.Incomplete ids) -> Ok(TransactionReadiness.Incomplete ids)

    /// Fingerprints the base and proposed snapshots into a preview receipt. Workflows with their
    /// own error union map the fingerprint failure into it.
    let computeReceipt
        (transactionId: TransactionId)
        (baseSnapshot: Snapshot)
        (proposedSnapshot: Snapshot)
        : Result<TransactionPreviewReceipt, SnapshotCodecError> =
        match SnapshotFingerprint.compute baseSnapshot with
        | Error error -> Error error
        | Ok baseFingerprint ->
            match SnapshotFingerprint.compute proposedSnapshot with
            | Error error -> Error error
            | Ok proposedFingerprint ->
                match SemanticFingerprint.compute proposedSnapshot with
                | Error error -> Error error
                | Ok semanticFingerprint ->
                    Ok(
                        TransactionPreviewReceipt.create
                            None
                            transactionId
                            baseSnapshot.Id
                            baseFingerprint
                            proposedSnapshot.Id
                            proposedFingerprint
                            semanticFingerprint
                    )

    type SimulatedCandidate<'Prepared> =
        { TransactionId: TransactionId
          BaseSnapshot: Snapshot
          ProposedSnapshot: Snapshot
          Readiness: TransactionReadiness
          Receipt: TransactionPreviewReceipt
          Prepared: 'Prepared }

    /// A snapshot that fails validation, reported by its first validator issue.
    let private invalidSnapshot code (issue: ValidationIssue) =
        diagnostic code $"{issue.Code}: {issue.Message}"

    /// The simulate pipeline shared by the structural, migration and declaration workflows:
    /// validate the base, `prepare` against it (preflight and materialization), derive the
    /// proposed snapshot with `propose`, validate it, classify readiness and fingerprint the
    /// receipt. `propose` receives the base under the new snapshot identity. Every workflow
    /// reports an invalid base as `InvalidBaseSnapshot` and an invalid proposal as
    /// `InvalidProposedSnapshot`; `rejectWith` and `fingerprintFailed` map into its error union.
    let simulateCandidate
        (rejectWith: TransactionDiagnostic list -> 'Error)
        (fingerprintFailed: SnapshotCodecError -> 'Error)
        (workspace: AgentWorkspace)
        (prepare: Snapshot -> Result<'Prepared, 'Error>)
        (propose: 'Prepared -> Snapshot -> Snapshot)
        : Result<SimulatedCandidate<'Prepared>, 'Error> =
        let baseSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace

        match SnapshotValidator.validate baseSnapshot with
        | issue :: _ -> Error(rejectWith [ invalidSnapshot "InvalidBaseSnapshot" issue ])
        | [] ->
            match prepare baseSnapshot with
            | Error error -> Error error
            | Ok prepared ->
                let transactionId = StableIdAllocator.newTransactionId ()
                let proposedSnapshot =
                    propose
                        prepared
                        { baseSnapshot with
                            Id = StableIdAllocator.newSnapshotId ()
                            ParentId = Some baseSnapshot.Id }

                match SnapshotValidator.validate proposedSnapshot with
                | issue :: _ -> Error(rejectWith [ invalidSnapshot "InvalidProposedSnapshot" issue ])
                | [] ->
                    match classifyReadiness proposedSnapshot with
                    | Error diagnostics -> Error(rejectWith diagnostics)
                    | Ok readiness ->
                        match computeReceipt transactionId baseSnapshot proposedSnapshot with
                        | Error error -> Error(fingerprintFailed error)
                        | Ok receipt ->
                            Ok
                                { TransactionId = transactionId
                                  BaseSnapshot = baseSnapshot
                                  ProposedSnapshot = proposedSnapshot
                                  Readiness = readiness
                                  Receipt = receipt
                                  Prepared = prepared }

    /// The shared pipeline for workflows that report through `AgentStructuralWorkflowError`.
    let simulateStructural workspace prepare propose =
        simulateCandidate
            AgentStructuralWorkflowError.SimulationRejected
            AgentStructuralWorkflowError.FingerprintFailed
            workspace
            prepare
            propose

    [<RequireQualifiedAccess>]
    type DeclarationSimulationFailure =
        | Rejected of TransactionDiagnostic list
        | FingerprintFailed of SnapshotCodecError

    /// The shared pipeline for the declaration workflows: `preflight` checks the base, `create`
    /// builds the declarations and `append` adds them to the proposed snapshot's collection.
    let simulateDeclaration
        (workspace: AgentWorkspace)
        (preflight: Snapshot -> Result<unit, TransactionDiagnostic>)
        (create: unit -> 'Created)
        (append: 'Created -> Snapshot -> Snapshot)
        : Result<SimulatedCandidate<'Created>, DeclarationSimulationFailure> =
        simulateCandidate
            DeclarationSimulationFailure.Rejected
            DeclarationSimulationFailure.FingerprintFailed
            workspace
            (fun baseSnapshot ->
                match preflight baseSnapshot with
                | Error error -> Error(DeclarationSimulationFailure.Rejected [ error ])
                | Ok() -> Ok(create ()))
            append

    let createReceipt transactionId baseSnapshot proposedSnapshot =
        computeReceipt transactionId baseSnapshot proposedSnapshot
        |> Result.mapError AgentStructuralWorkflowError.FingerprintFailed

    let bodyObligationError (target: SemanticFunction) (snapshot: Snapshot) =
        let oldBodyIds =
            ValueGraph.collectNodes target.Body
            |> List.map (fun node -> node.Id)
            |> Set.ofList

        snapshot.Obligations
        |> List.tryFind (fun obligation -> Set.contains obligation.CallNodeId oldBodyIds)
        |> Option.map (fun obligation ->
            diagnostic
                "StructuralBodyReferencedByObligation"
                $"Obligation '{StableId.value obligation.Id}' targets the body being replaced.")

    /// Workflow-specific wording for the publication diagnostics. The codes and the order of the
    /// checks are shared; only the human-readable subject differs between workflows.
    type CandidatePublicationMessages =
        { StaleBase: string
          BaseFingerprintFailure: string
          ProposedFingerprintFailure: string
          ResultMismatch: string }

    let structuralPublicationMessages =
        { StaleBase = "The workspace advanced beyond the reviewed structural proposal."
          BaseFingerprintFailure = "The base fingerprint could not be recomputed."
          ProposedFingerprintFailure = "The proposed fingerprint could not be recomputed."
          ResultMismatch = "The proposed state differs from the reviewed receipt." }

    /// Messages for the declaration workflows ("type", "effect", "capability").
    let declarationPublicationMessages (subject: string) =
        { StaleBase = $"The workspace advanced beyond the reviewed {subject}-declaration proposal."
          BaseFingerprintFailure = "The base snapshot fingerprint could not be recomputed."
          ProposedFingerprintFailure = "The proposed snapshot fingerprint could not be recomputed."
          ResultMismatch = $"The proposed {subject} declaration differs from the reviewed result." }

    /// Publishes a reviewed candidate after stale-base, fingerprint and readiness checks.
    let publishCandidateWithMessages
        (messages: CandidatePublicationMessages)
        (workspace: AgentWorkspace)
        (proposedSnapshot: Snapshot)
        (readiness: TransactionReadiness)
        (receipt: TransactionPreviewReceipt)
        (policy: CommitPolicy)
        : Result<AgentWorkspace, TransactionDiagnostic list> =
        let currentSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace

        if currentSnapshot.Id <> receipt.BaseSnapshotId then
            Error [ diagnostic "StaleBaseSnapshot" messages.StaleBase ]
        else
            match SnapshotFingerprint.compute currentSnapshot with
            | Error _ -> Error [ diagnostic "FingerprintFailure" messages.BaseFingerprintFailure ]
            | Ok baseFingerprint when baseFingerprint <> receipt.BaseSnapshotFingerprint ->
                Error
                    [ diagnostic
                          "PreviewReceiptBaseMismatch"
                          "The current base content differs from the reviewed base." ]
            | Ok _ ->
                match SnapshotFingerprint.compute proposedSnapshot, SemanticFingerprint.compute proposedSnapshot with
                | Ok proposedFingerprint, Ok semanticFingerprint
                    when proposedFingerprint = receipt.ProposedSnapshotFingerprint
                         && semanticFingerprint = receipt.ProposedSemanticFingerprint ->
                    match readiness, policy with
                    | TransactionReadiness.Incomplete ids, CommitPolicy.RejectBlockingObligations ->
                        let rendered = ids |> List.map StableId.value |> String.concat ", "
                        Error
                            [ diagnostic
                                  "BlockingObligations"
                                  $"The proposed snapshot has blocking obligations: {rendered}." ]
                    | _ -> AgentWorkspace.publishValidatedSnapshot workspace proposedSnapshot
                | Ok _, Ok _ -> Error [ diagnostic "PreviewReceiptResultMismatch" messages.ResultMismatch ]
                | Error _, _
                | _, Error _ -> Error [ diagnostic "FingerprintFailure" messages.ProposedFingerprintFailure ]

    /// Publishes a reviewed structural candidate after stale-base, fingerprint and readiness checks.
    let publishCandidateWithPolicy workspace proposedSnapshot readiness receipt policy =
        publishCandidateWithMessages structuralPublicationMessages workspace proposedSnapshot readiness receipt policy

    let publishReviewedWithPolicy
        (workspace: AgentWorkspace)
        (transactionId: TransactionId)
        (proposedSnapshot: Snapshot)
        (diff: FunctionBodyReplacementDiff)
        (readiness: TransactionReadiness)
        (receipt: TransactionPreviewReceipt)
        (policy: CommitPolicy)
        : AgentStructuralCommitResult =
        match publishCandidateWithPolicy workspace proposedSnapshot readiness receipt policy with
        | Error diagnostics -> AgentStructuralCommitResult.Rejected diagnostics
        | Ok nextWorkspace ->
            AgentStructuralCommitResult.Committed(
                nextWorkspace,
                { TransactionId = transactionId
                  BaseSnapshotId = receipt.BaseSnapshotId
                  PublishedSnapshotId = proposedSnapshot.Id
                  Diff = diff
                  Readiness = readiness
                  Receipt = receipt }
            )
