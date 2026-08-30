namespace NyamNyam.Core

open Microsoft.FSharp.Reflection

/// Shallow M2.3 request: existing semantic IDs are agent-controlled inputs;
/// all replacement graph-node IDs remain system-owned.
type AgentReplaceFunctionBodyWithListMap =
    { TargetFunctionId: FunctionId
      ExpectedBaseSnapshotId: SnapshotId
      ExpectedCurrentResultNodeId: GraphNodeId
      SourceParameterId: ParameterId
      MapperFunctionId: FunctionId }

type AgentListMapStructuralChangeReview =
    { RequestedChange: AgentReplaceFunctionBodyWithListMap
      Receipt: TransactionPreviewReceipt
      Readiness: TransactionReadiness
      ResultingChange: FunctionBodyReplacementDiff
      BaseFocus: FunctionFocusView
      ProposedFocus: FunctionFocusView }

type private ListMapStructuralProposalState =
    { TransactionId: TransactionId
      ProposedSnapshot: Snapshot
      Review: AgentListMapStructuralChangeReview }

type AgentListMapStructuralChangeProposal = private AgentListMapStructuralChangeProposal of ListMapStructuralProposalState

[<RequireQualifiedAccess>]
module AgentListMapStructuralChangeProposal =
    let private state (AgentListMapStructuralChangeProposal state) = state
    let review proposal = (state proposal).Review
    let transactionId proposal = (state proposal).TransactionId
    let proposedSnapshotId proposal = (state proposal).ProposedSnapshot.Id

[<RequireQualifiedAccess>]
module AgentListMapStructuralWorkflow =
    let private diagnostic code message: TransactionDiagnostic =
        { Code = code
          Message = message }

    let private rejected code message =
        Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic code message ])

    let private listItemType typeRef =
        match typeRef with
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ]) -> Some itemType
        | _ -> None

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

    let private summarizeNodes
        (snapshot: Snapshot)
        (functionEntity: SemanticFunction)
        (nodes: SemanticNode list)
        : StructuralNodeSummary list =
        let nodeTypes = SemanticFacts.nodeTypes snapshot functionEntity
        nodes
        |> List.map (fun node ->
            let parameterId, functionId, bitsHex = summaryExtra node
            { NodeId = node.Id
              Role = unionCaseName node.Kind
              Type = Map.tryFind node.Id nodeTypes |> Option.defaultValue None
              Dependencies =
                SemanticFacts.directValueDependencies node
                |> List.distinct
                |> List.sortBy StableId.value
              ParameterId = parameterId
              FunctionId = functionId
              Float64BitsHex = bitsHex })

    let private classifyReadiness snapshot =
        match SnapshotReadiness.evaluate snapshot with
        | Error issues ->
            Error(
                issues
                |> List.map (fun issue ->
                    diagnostic ("Validation." + issue.Code) issue.Message)
            )
        | Ok SemanticReadiness.Complete -> Ok TransactionReadiness.SemanticallyReady
        | Ok(SemanticReadiness.Incomplete ids) -> Ok(TransactionReadiness.Incomplete ids)

    let private createReceipt transactionId baseSnapshot proposedSnapshot =
        match SnapshotFingerprint.compute baseSnapshot with
        | Error error -> Error(AgentStructuralWorkflowError.FingerprintFailed error)
        | Ok baseFingerprint ->
            match SnapshotFingerprint.compute proposedSnapshot with
            | Error error -> Error(AgentStructuralWorkflowError.FingerprintFailed error)
            | Ok proposedFingerprint ->
                match SemanticFingerprint.compute proposedSnapshot with
                | Error error -> Error(AgentStructuralWorkflowError.FingerprintFailed error)
                | Ok semanticFingerprint ->
                    Ok
                        { TransactionId = transactionId
                          BaseSnapshotId = baseSnapshot.Id
                          BaseSnapshotFingerprint = baseFingerprint
                          ProposedSnapshotId = proposedSnapshot.Id
                          ProposedSnapshotFingerprint = proposedFingerprint
                          ProposedSemanticFingerprint = semanticFingerprint }

    let private bodyObligationError (target: SemanticFunction) (snapshot: Snapshot) =
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

    /// Validates every agent-controlled semantic fact before production IDs are allocated.
    let private validatePreflight
        (request: AgentReplaceFunctionBodyWithListMap)
        (baseSnapshot: Snapshot)
        : Result<SemanticFunction * SemanticParameter * SemanticFunction, TransactionDiagnostic> =
        if request.ExpectedBaseSnapshotId <> baseSnapshot.Id then
            Error(
                diagnostic
                    "StructuralExpectedBaseMismatch"
                    "The structural request targets a different base snapshot."
            )
        else
            match baseSnapshot.Functions |> List.tryFind (fun fn -> fn.Id = request.TargetFunctionId) with
            | None ->
                Error(diagnostic "StructuralTargetNotFound" "The target function does not exist.")
            | Some target when target.Body.ResultNodeId <> request.ExpectedCurrentResultNodeId ->
                Error(
                    diagnostic
                        "StructuralExpectedResultMismatch"
                        "The target function result node changed after inspection."
                )
            | Some target ->
                match listItemType target.ResultType with
                | None ->
                    Error(
                        diagnostic
                            "StructuralListTargetResultTypeMismatch"
                            "The target function does not return List<R>."
                    )
                | Some targetItemType ->
                    match target.Parameters |> List.tryFind (fun parameter -> parameter.Id = request.SourceParameterId) with
                    | None ->
                        Error(
                            diagnostic
                                "StructuralListSourceParameterOutOfScope"
                                $"Parameter '{StableId.value request.SourceParameterId}' is not owned by the target function."
                        )
                    | Some sourceParameter ->
                        match listItemType sourceParameter.Type with
                        | None ->
                            Error(
                                diagnostic
                                    "StructuralListSourceTypeMismatch"
                                    $"Parameter '{StableId.value sourceParameter.Id}' is not List<T>."
                            )
                        | Some sourceItemType ->
                            match baseSnapshot.Functions |> List.tryFind (fun fn -> fn.Id = request.MapperFunctionId) with
                            | None ->
                                Error(
                                    diagnostic
                                        "StructuralListMapperNotFound"
                                        $"Mapper function '{StableId.value request.MapperFunctionId}' does not exist."
                                )
                            | Some mapper when mapper.Parameters.Length <> 1 ->
                                Error(
                                    diagnostic
                                        "StructuralListMapperArityMismatch"
                                        $"Mapper function '{StableId.value mapper.Id}' must have exactly one parameter."
                                )
                            | Some mapper when mapper.Parameters.Head.Type <> sourceItemType ->
                                Error(
                                    diagnostic
                                        "StructuralListMapperInputTypeMismatch"
                                        $"Mapper function '{StableId.value mapper.Id}' input type does not match the source list item type."
                                )
                            | Some mapper when mapper.ResultType <> targetItemType ->
                                Error(
                                    diagnostic
                                        "StructuralListMapperResultTypeMismatch"
                                        $"Mapper function '{StableId.value mapper.Id}' result type does not match the target list item type."
                                )
                            | Some mapper when not (List.isEmpty mapper.RequiredEffects) ->
                                Error(
                                    diagnostic
                                        "StructuralListMapperEffectsNotAllowed"
                                        $"Mapper function '{StableId.value mapper.Id}' must be pure."
                                )
                            | Some mapper ->
                                match bodyObligationError target baseSnapshot with
                                | Some error -> Error error
                                | None -> Ok(target, sourceParameter, mapper)

    let private materialize sourceParameterId mapperFunctionId =
        let sourceNodeId = StableIdAllocator.newGraphNodeId ()
        let mapNodeId = StableIdAllocator.newGraphNodeId ()
        [ { Id = sourceNodeId
            Kind = SemanticNodeKind.ParameterReference sourceParameterId }
          { Id = mapNodeId
            Kind = SemanticNodeKind.ListMap(sourceNodeId, mapperFunctionId) } ],
        mapNodeId

    let simulate
        (workspace: AgentWorkspace)
        (request: AgentReplaceFunctionBodyWithListMap)
        : Result<AgentListMapStructuralChangeProposal, AgentStructuralWorkflowError> =
        let baseSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace

        match SnapshotValidator.validate baseSnapshot with
        | issue :: _ -> rejected "InvalidBaseSnapshot" $"{issue.Code}: {issue.Message}"
        | [] ->
            match validatePreflight request baseSnapshot with
            | Error error -> Error(AgentStructuralWorkflowError.SimulationRejected [ error ])
            | Ok(target, sourceParameter, mapper) ->
                let newNodes, newResultId = materialize sourceParameter.Id mapper.Id
                let replacementBody =
                    { Nodes = newNodes
                      ResultNodeId = newResultId }
                let proposedSnapshotId = StableIdAllocator.newSnapshotId ()
                let transactionId = StableIdAllocator.newTransactionId ()
                let proposedSnapshot =
                    { baseSnapshot with
                        Id = proposedSnapshotId
                        ParentId = Some baseSnapshot.Id
                        Functions =
                            baseSnapshot.Functions
                            |> List.map (fun fn ->
                                if fn.Id = target.Id then
                                    { fn with Body = replacementBody }
                                else
                                    fn) }

                match SnapshotValidator.validate proposedSnapshot with
                | issue :: _ -> rejected "InvalidProposedSnapshot" $"{issue.Code}: {issue.Message}"
                | [] ->
                    match classifyReadiness proposedSnapshot with
                    | Error diagnostics ->
                        Error(AgentStructuralWorkflowError.SimulationRejected diagnostics)
                    | Ok readiness ->
                        match createReceipt transactionId baseSnapshot proposedSnapshot with
                        | Error error -> Error error
                        | Ok receipt ->
                            match
                                SemanticQuery.focusFunction baseSnapshot target.Id,
                                SemanticQuery.focusFunction proposedSnapshot target.Id
                            with
                            | Error error, _
                            | _, Error error ->
                                Error(AgentStructuralWorkflowError.ReviewProjectionFailed error)
                            | Ok baseFocus, Ok proposedFocus ->
                                let proposedTarget =
                                    proposedSnapshot.Functions
                                    |> List.find (fun fn -> fn.Id = target.Id)

                                let diff =
                                    { OwnerFunctionId = target.Id
                                      OldResultNodeId = target.Body.ResultNodeId
                                      NewResultNodeId = newResultId
                                      RemovedNodes =
                                        ValueGraph.collectNodes target.Body
                                        |> summarizeNodes baseSnapshot target
                                        |> List.sortBy (fun node -> StableId.value node.NodeId)
                                      AddedNodes =
                                        summarizeNodes
                                            proposedSnapshot
                                            proposedTarget
                                            proposedTarget.Body.Nodes }

                                let review =
                                    { RequestedChange = request
                                      Receipt = receipt
                                      Readiness = readiness
                                      ResultingChange = diff
                                      BaseFocus = baseFocus
                                      ProposedFocus = proposedFocus }

                                Ok(
                                    AgentListMapStructuralChangeProposal
                                        { TransactionId = transactionId
                                          ProposedSnapshot = proposedSnapshot
                                          Review = review }
                                )

    let private publishWithPolicy
        (workspace: AgentWorkspace)
        (AgentListMapStructuralChangeProposal proposal)
        (policy: CommitPolicy)
        : AgentStructuralCommitResult =
        let currentSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace
        let receipt = proposal.Review.Receipt

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
                match
                    SnapshotFingerprint.compute proposal.ProposedSnapshot,
                    SemanticFingerprint.compute proposal.ProposedSnapshot
                with
                | Ok proposedFingerprint, Ok semanticFingerprint
                    when proposedFingerprint = receipt.ProposedSnapshotFingerprint
                         && semanticFingerprint = receipt.ProposedSemanticFingerprint ->
                    match proposal.Review.Readiness, policy with
                    | TransactionReadiness.Incomplete ids, CommitPolicy.RejectBlockingObligations ->
                        let rendered = ids |> List.map StableId.value |> String.concat ", "
                        AgentStructuralCommitResult.Rejected
                            [ diagnostic
                                  "BlockingObligations"
                                  $"The proposed snapshot has blocking obligations: {rendered}." ]
                    | _ ->
                        match AgentWorkspace.publishValidatedSnapshot workspace proposal.ProposedSnapshot with
                        | Error diagnostics -> AgentStructuralCommitResult.Rejected diagnostics
                        | Ok nextWorkspace ->
                            AgentStructuralCommitResult.Committed(
                                nextWorkspace,
                                { TransactionId = proposal.TransactionId
                                  BaseSnapshotId = receipt.BaseSnapshotId
                                  PublishedSnapshotId = proposal.ProposedSnapshot.Id
                                  Diff = proposal.Review.ResultingChange
                                  Readiness = proposal.Review.Readiness
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

    let commit workspace proposal =
        publishWithPolicy workspace proposal CommitPolicy.RejectBlockingObligations

    let commitWithPolicy workspace proposal policy =
        publishWithPolicy workspace proposal policy

    let reject
        (workspace: AgentWorkspace)
        (AgentListMapStructuralChangeProposal proposal)
        : AgentWorkspace * AgentStructuralRejectionReceipt =
        workspace,
        { TransactionId = proposal.TransactionId
          RetainedSnapshotId = AgentWorkspace.currentSnapshotId workspace }
