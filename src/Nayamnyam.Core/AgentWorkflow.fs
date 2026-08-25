namespace Nayamnyam.Core

[<RequireQualifiedAccess>]
type AgentRenameTarget =
    | Type of TypeId
    | Function of FunctionId

[<RequireQualifiedAccess>]
type AgentCallPlan =
    | BindExistingValue of callNodeId: GraphNodeId * valueNodeId: GraphNodeId
    | LeaveMissing of callNodeId: GraphNodeId

type AgentAddFunctionParameterIntent =
    { TargetFunctionId: FunctionId
      NewParameterDisplayName: string
      NewParameterType: TypeRef
      CallSitePlans: AgentCallPlan list }

type AgentResolveMissingArgumentIntent =
    { TargetObligationId: ObligationId
      ResolutionValueNodeId: GraphNodeId }

[<RequireQualifiedAccess>]
type AgentOperationIntent =
    | RenameDisplayName of AgentRenameTarget * newDisplayName: string
    | AddFunctionParameter of AgentAddFunctionParameterIntent
    | ResolveMissingArgument of AgentResolveMissingArgumentIntent

type AgentWorkspace = private AgentWorkspace of WorkspaceState

type AgentChangeReview =
    { RequestedOperations: AgentOperationIntent list
      Receipt: TransactionPreviewReceipt
      Readiness: TransactionReadiness
      ResultingChange: ChangeReviewView }

type AgentChangeProposal =
    private
        { Request: TransactionRequest
          Preview: WorkspacePreview
          Review: AgentChangeReview }

[<RequireQualifiedAccess>]
type AgentWorkflowError =
    | SimulationRejected of TransactionDiagnostic list
    | ReviewProjectionFailed of ChangeReviewQueryError

[<RequireQualifiedAccess>]
type AgentCommitResult =
    | Rejected of TransactionDiagnostic list
    | Committed of AgentWorkspace * PublishedTransaction

[<RequireQualifiedAccess>]
module AgentChangeProposal =
    let review (proposal: AgentChangeProposal): AgentChangeReview = proposal.Review
    let transactionId (proposal: AgentChangeProposal): TransactionId = proposal.Request.Id
    let proposedSnapshotId (proposal: AgentChangeProposal): SnapshotId = proposal.Request.ProposedSnapshotId

[<RequireQualifiedAccess>]
module AgentWorkspace =
    let create (initialSnapshot: Snapshot): Result<AgentWorkspace, ValidationIssue list> =
        SemanticWorkspace.create initialSnapshot |> Result.map AgentWorkspace

    let currentSnapshotId (AgentWorkspace workspace): SnapshotId =
        SemanticWorkspace.currentSnapshot workspace |> fun snapshot -> snapshot.Id

    let currentSnapshotFingerprint
        (AgentWorkspace workspace)
        : Result<SnapshotFingerprint, SnapshotCodecError> =
        SemanticWorkspace.currentSnapshot workspace |> SnapshotFingerprint.compute

    let currentSemanticFingerprint
        (AgentWorkspace workspace)
        : Result<SemanticFingerprint, SnapshotCodecError> =
        SemanticWorkspace.currentSnapshot workspace |> SemanticFingerprint.compute

    let private snapshot (AgentWorkspace workspace): Snapshot = SemanticWorkspace.currentSnapshot workspace

    let getFunction workspace functionId = SemanticQuery.getFunction (snapshot workspace) functionId
    let getType workspace typeId = SemanticQuery.getType (snapshot workspace) typeId
    let getCallers workspace functionId = SemanticQuery.getCallers (snapshot workspace) functionId
    let getCallees workspace functionId = SemanticQuery.getCallees (snapshot workspace) functionId
    let getOpenObligations workspace functionId = SemanticQuery.getOpenObligations (snapshot workspace) functionId
    let getDependencies workspace functionId nodeId = SemanticQuery.getDependencies (snapshot workspace) functionId nodeId
    let getUses workspace functionId nodeId = SemanticQuery.getUses (snapshot workspace) functionId nodeId
    let focusFunction workspace functionId = SemanticQuery.focusFunction (snapshot workspace) functionId

[<RequireQualifiedAccess>]
module AgentWorkflow =
    let private materializeCallPlan plan =
        match plan with
        | AgentCallPlan.BindExistingValue(callNodeId, valueNodeId) ->
            CallPlan.BindExistingValue(callNodeId, valueNodeId)
        | AgentCallPlan.LeaveMissing callNodeId ->
            CallPlan.LeaveMissing(callNodeId, StableIdAllocator.newObligationId ())

    let private materializeOperation intent =
        match intent with
        | AgentOperationIntent.RenameDisplayName(AgentRenameTarget.Type typeId, newDisplayName) ->
            SemanticOperation.RenameDisplayName(RenameTarget.TypeTarget typeId, newDisplayName)
        | AgentOperationIntent.RenameDisplayName(AgentRenameTarget.Function functionId, newDisplayName) ->
            SemanticOperation.RenameDisplayName(RenameTarget.FunctionTarget functionId, newDisplayName)
        | AgentOperationIntent.AddFunctionParameter request ->
            SemanticOperation.AddFunctionParameter
                { TargetFunctionId = request.TargetFunctionId
                  NewParameterId = StableIdAllocator.newParameterId ()
                  NewParameterDisplayName = request.NewParameterDisplayName
                  NewParameterType = request.NewParameterType
                  CallSitePlans = request.CallSitePlans |> List.map materializeCallPlan }
        | AgentOperationIntent.ResolveMissingArgument request ->
            SemanticOperation.ResolveMissingArgument
                { TargetObligationId = request.TargetObligationId
                  ResolutionValueNodeId = request.ResolutionValueNodeId }

    let simulate
        (AgentWorkspace workspace)
        (requestedOperations: AgentOperationIntent list)
        : Result<AgentChangeProposal, AgentWorkflowError> =
        let request: TransactionRequest =
            { Id = StableIdAllocator.newTransactionId ()
              BaseSnapshotId = SemanticWorkspace.currentSnapshot workspace |> fun snapshot -> snapshot.Id
              ProposedSnapshotId = StableIdAllocator.newSnapshotId ()
              Operations = requestedOperations |> List.map materializeOperation }

        match SemanticWorkspace.simulate workspace request with
        | WorkspaceSimulationResult.Rejected diagnostics ->
            Error(AgentWorkflowError.SimulationRejected diagnostics)
        | WorkspaceSimulationResult.Preview preview ->
            match ChangeReviewQuery.project preview.Simulation with
            | Error error -> Error(AgentWorkflowError.ReviewProjectionFailed error)
            | Ok resultingChange ->
                let review =
                    { RequestedOperations = requestedOperations
                      Receipt = preview.Receipt
                      Readiness = preview.Readiness
                      ResultingChange = resultingChange }

                Ok
                    { Request = request
                      Preview = preview
                      Review = review }

    let private mapCommitResult result =
        match result with
        | WorkspaceCommitResult.Rejected diagnostics -> AgentCommitResult.Rejected diagnostics
        | WorkspaceCommitResult.Committed(workspace, publication) ->
            AgentCommitResult.Committed(AgentWorkspace workspace, publication)

    let commit
        (AgentWorkspace workspace)
        (proposal: AgentChangeProposal)
        : AgentCommitResult =
        SemanticWorkspace.commitReviewed
            workspace
            proposal.Request
            proposal.Preview.Receipt
        |> mapCommitResult

    let commitWithPolicy
        (AgentWorkspace workspace)
        (proposal: AgentChangeProposal)
        (policy: CommitPolicy)
        : AgentCommitResult =
        SemanticWorkspace.commitReviewedWithPolicy
            workspace
            proposal.Request
            proposal.Preview.Receipt
            policy
        |> mapCommitResult

    let reject
        (AgentWorkspace workspace)
        (proposal: AgentChangeProposal)
        : AgentWorkspace * TransactionRejectionReceipt =
        let retained, receipt = SemanticWorkspace.reject workspace proposal.Request
        AgentWorkspace retained, receipt
