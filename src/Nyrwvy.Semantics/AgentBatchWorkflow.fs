namespace Nyrwvy.Core

open StructuralTransactionInternals

/// One proposal over a base snapshot S0 whose candidate is the result of several chained steps
/// (docs/decisions/mixed-authoring-batches.md).
type AgentBatchProposal =
    private
        { TransactionId: TransactionId
          ProposedSnapshot: Snapshot
          Readiness: TransactionReadiness
          Receipt: TransactionPreviewReceipt }

[<RequireQualifiedAccess>]
module AgentBatchProposal =
    let transactionId (proposal: AgentBatchProposal): TransactionId = proposal.TransactionId
    let proposedSnapshot (proposal: AgentBatchProposal): Snapshot = proposal.ProposedSnapshot
    let readiness (proposal: AgentBatchProposal): TransactionReadiness = proposal.Readiness
    let receipt (proposal: AgentBatchProposal): TransactionPreviewReceipt = proposal.Receipt

[<RequireQualifiedAccess>]
module AgentBatchWorkflow =
    /// Re-parents `final`, the snapshot the last step produced, onto the workspace's snapshot with
    /// a fresh ID, validates it once against that snapshot and fingerprints the receipt over it.
    /// Readiness is classified on the final candidate only, so an intermediate step may leave
    /// obligations that a later one resolves.
    let simulate (workspace: AgentWorkspace) (final: Snapshot): Result<AgentBatchProposal, AgentStructuralWorkflowError> =
        simulateStructural
            workspace
            (fun _ -> Ok final)
            (fun final proposed -> { final with Id = proposed.Id; ParentId = proposed.ParentId })
        |> Result.map (fun candidate ->
            { TransactionId = candidate.TransactionId
              ProposedSnapshot = candidate.ProposedSnapshot
              Readiness = candidate.Readiness
              Receipt = candidate.Receipt })

    /// Publishes the reviewed candidate; a workspace that moved on since the base is rejected as
    /// `StaleBaseSnapshot`.
    let commitWithPolicy (workspace: AgentWorkspace) (proposal: AgentBatchProposal) (policy: CommitPolicy): Result<AgentWorkspace, TransactionDiagnostic list> =
        publishCandidateWithPolicy workspace proposal.ProposedSnapshot proposal.Readiness proposal.Receipt policy
