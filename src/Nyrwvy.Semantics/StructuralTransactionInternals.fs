namespace Nyrwvy.Core

open Microsoft.FSharp.Reflection

module internal StructuralTransactionInternals =
    let diagnostic code message: TransactionDiagnostic =
        { Code = code
          Message = message }

    let rejected code message =
        Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic code message ])

    let validateRecipeBounds
        (children: 'Recipe -> seq<'Recipe>)
        (recipe: 'Recipe)
        : Result<unit, TransactionDiagnostic> =
        let rec visit depth remaining current =
            if depth > 32 then
                Error(
                    diagnostic
                        "StructuralRecipeDepthLimitExceeded"
                        "The structural recipe is deeper than 32 nodes."
                )
            elif remaining <= 0 then
                Error(
                    diagnostic
                        "StructuralRecipeNodeLimitExceeded"
                        "The structural recipe contains more than 64 nodes."
                )
            else
                let mutable nextRemaining = remaining - 1
                let mutable failure: TransactionDiagnostic option = None
                use enumerator = (children current).GetEnumerator()

                while Option.isNone failure && enumerator.MoveNext() do
                    match visit (depth + 1) nextRemaining enumerator.Current with
                    | Ok remainingAfterChild -> nextRemaining <- remainingAfterChild
                    | Error error -> failure <- Some error

                match failure with
                | Some error -> Error error
                | None -> Ok nextRemaining

        visit 1 64 recipe |> Result.map ignore

    let private unionCaseName (kind: SemanticNodeKind) =
        let case, _ = FSharpValue.GetUnionFields(box kind, typeof<SemanticNodeKind>)
        case.Name

    let private summaryExtra (node: SemanticNode) =
        match node.Kind with
        | SemanticNodeKind.ParameterReference parameterId -> Some parameterId, None, None
        | SemanticNodeKind.Float64Literal bits -> None, None, Some(Float64Bits.toHex bits)
        | SemanticNodeKind.DirectCall(functionId, _)
        | SemanticNodeKind.ListMap(_, functionId)
        | SemanticNodeKind.ListFilter(_, functionId)
        | SemanticNodeKind.ListAny(_, functionId)
        | SemanticNodeKind.ListAll(_, functionId) -> None, Some functionId, None
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

    let publishReviewedWithPolicy
        (workspace: AgentWorkspace)
        (transactionId: TransactionId)
        (proposedSnapshot: Snapshot)
        (diff: FunctionBodyReplacementDiff)
        (readiness: TransactionReadiness)
        (receipt: TransactionPreviewReceipt)
        (policy: CommitPolicy)
        : AgentStructuralCommitResult =
        let currentSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace

        if currentSnapshot.Id <> receipt.BaseSnapshotId then
            AgentStructuralCommitResult.Rejected
                [ diagnostic
                      "StaleBaseSnapshot"
                      "The workspace advanced beyond the reviewed structural proposal." ]
        else
            match SnapshotFingerprint.compute currentSnapshot with
            | Error _ ->
                AgentStructuralCommitResult.Rejected
                    [ diagnostic
                          "FingerprintFailure"
                          "The base fingerprint could not be recomputed." ]
            | Ok baseFingerprint when baseFingerprint <> receipt.BaseSnapshotFingerprint ->
                AgentStructuralCommitResult.Rejected
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
                        AgentStructuralCommitResult.Rejected
                            [ diagnostic
                                  "BlockingObligations"
                                  $"The proposed snapshot has blocking obligations: {rendered}." ]
                    | _ ->
                        match AgentWorkspace.publishValidatedSnapshot workspace proposedSnapshot with
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
                | Ok _, Ok _ ->
                    AgentStructuralCommitResult.Rejected
                        [ diagnostic
                              "PreviewReceiptResultMismatch"
                              "The proposed state differs from the reviewed receipt." ]
                | Error _, _
                | _, Error _ ->
                    AgentStructuralCommitResult.Rejected
                        [ diagnostic
                              "FingerprintFailure"
                              "The proposed fingerprint could not be recomputed." ]
