namespace NyamNyam.Core

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
    open StructuralTransactionInternals

    let private listItemType typeRef =
        match typeRef with
        | TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ itemType ]) -> Some itemType
        | _ -> None

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
        publishReviewedWithPolicy
            workspace
            proposal.TransactionId
            proposal.ProposedSnapshot
            proposal.Review.ResultingChange
            proposal.Review.Readiness
            proposal.Review.Receipt
            policy

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
