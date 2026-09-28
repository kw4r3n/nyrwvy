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
        | SemanticNodeKind.ListAll(_, functionId, _) -> None, Some functionId, None
        | _ -> None, None, None

    let summarizeNodes
        (snapshot: Snapshot)
        (functionEntity: SemanticFunction)
        (nodes: SemanticNode list)
        : StructuralNodeSummary list =
        let nodeTypes = SemanticFacts.nodeTypes snapshot functionEntity
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
              Float64BitsHex = bitsHex })

    let classifyReadiness snapshot =
        match SnapshotReadiness.evaluate snapshot with
        | Error issues ->
            Error(
                issues
                |> List.map (fun issue -> diagnostic ("Validation." + issue.Code) issue.Message)
            )
        | Ok SemanticReadiness.Complete -> Ok TransactionReadiness.SemanticallyReady
        | Ok(SemanticReadiness.Incomplete ids) -> Ok(TransactionReadiness.Incomplete ids)

    let createReceipt transactionId baseSnapshot proposedSnapshot =
        match SnapshotFingerprint.compute baseSnapshot with
        | Error error -> Error(AgentStructuralWorkflowError.FingerprintFailed error)
        | Ok baseFingerprint ->
            match SnapshotFingerprint.compute proposedSnapshot with
            | Error error -> Error(AgentStructuralWorkflowError.FingerprintFailed error)
            | Ok proposedFingerprint ->
                match SemanticFingerprint.compute proposedSnapshot with
                | Error error -> Error(AgentStructuralWorkflowError.FingerprintFailed error)
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

    /// Publishes a reviewed candidate after stale-base, fingerprint and readiness checks.
    let publishCandidateWithPolicy
        (workspace: AgentWorkspace)
        (proposedSnapshot: Snapshot)
        (readiness: TransactionReadiness)
        (receipt: TransactionPreviewReceipt)
        (policy: CommitPolicy)
        : Result<AgentWorkspace, TransactionDiagnostic list> =
        let currentSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace

        if currentSnapshot.Id <> receipt.BaseSnapshotId then
            Error
                [ diagnostic
                      "StaleBaseSnapshot"
                      "The workspace advanced beyond the reviewed structural proposal." ]
        else
            match SnapshotFingerprint.compute currentSnapshot with
            | Error _ ->
                Error
                    [ diagnostic
                          "FingerprintFailure"
                          "The base fingerprint could not be recomputed." ]
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
                | Ok _, Ok _ ->
                    Error
                        [ diagnostic
                              "PreviewReceiptResultMismatch"
                              "The proposed state differs from the reviewed receipt." ]
                | Error _, _
                | _, Error _ ->
                    Error
                        [ diagnostic
                              "FingerprintFailure"
                              "The proposed fingerprint could not be recomputed." ]

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
