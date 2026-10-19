namespace Nyrwvy.Core

open System

type AgentCreateEffectDeclarationRequest =
    { ExpectedBaseSnapshotId: SnapshotId
      DisplayName: string
      /// Whether snapshots that depend on this one may use the effect; private when false.
      Exported: bool }

type AgentEffectDeclarationReview =
    { RequestedChange: AgentCreateEffectDeclarationRequest
      Receipt: TransactionPreviewReceipt
      Readiness: TransactionReadiness
      CreatedEffect: SemanticEffect }

type AgentEffectDeclarationPublishedTransaction =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      PublishedSnapshotId: SnapshotId
      CreatedEffectId: EffectId
      Readiness: TransactionReadiness
      Receipt: TransactionPreviewReceipt }

type private AgentEffectDeclarationProposalState =
    { TransactionId: TransactionId
      ProposedSnapshot: Snapshot
      Review: AgentEffectDeclarationReview }

type AgentEffectDeclarationProposal =
    private
    | AgentEffectDeclarationProposal of AgentEffectDeclarationProposalState

[<RequireQualifiedAccess>]
type AgentEffectDeclarationWorkflowError =
    | SimulationRejected of TransactionDiagnostic list
    | FingerprintFailed of SnapshotCodecError

[<RequireQualifiedAccess>]
type AgentEffectDeclarationCommitResult =
    | Rejected of TransactionDiagnostic list
    | Committed of AgentWorkspace * AgentEffectDeclarationPublishedTransaction

[<RequireQualifiedAccess>]
module AgentEffectDeclarationProposal =
    let private state (AgentEffectDeclarationProposal state) = state
    let review proposal = (state proposal).Review
    let transactionId proposal = (state proposal).TransactionId
    let proposedSnapshotId proposal = (state proposal).ProposedSnapshot.Id
    /// The proposed snapshot, read-only, for rendering a human review page of the proposal.
    let proposedSnapshot proposal = (state proposal).ProposedSnapshot

[<RequireQualifiedAccess>]
module AgentEffectDeclarationWorkflow =
    open StructuralTransactionInternals

    let private validatePreflight
        (snapshot: Snapshot)
        (request: AgentCreateEffectDeclarationRequest)
        : Result<unit, TransactionDiagnostic> =
        if request.ExpectedBaseSnapshotId <> snapshot.Id then
            Error(
                diagnostic
                    "EffectDeclarationExpectedBaseMismatch"
                    "The effect-declaration request targets a different base snapshot."
            )
        elif String.IsNullOrWhiteSpace request.DisplayName then
            Error(
                diagnostic
                    "EffectDeclarationDisplayNameBlank"
                    "Effect display name must not be blank."
            )
        elif snapshot.Effects |> List.exists (fun item -> item.DisplayName = request.DisplayName) then
            Error(
                diagnostic
                    "EffectDeclarationDuplicateDisplayName"
                    $"An effect named '{request.DisplayName}' already exists; effect display names must be unique."
            )
        else
            Ok()

    let private materialize (request: AgentCreateEffectDeclarationRequest): SemanticEffect =
        { Id = StableIdAllocator.newEffectId ()
          DisplayName = request.DisplayName
          Exported = request.Exported }

    let simulate
        (workspace: AgentWorkspace)
        (request: AgentCreateEffectDeclarationRequest)
        : Result<AgentEffectDeclarationProposal, AgentEffectDeclarationWorkflowError> =
        simulateDeclaration
            workspace
            (fun snapshot -> validatePreflight snapshot request)
            (fun () -> materialize request)
            (fun created snapshot -> { snapshot with Effects = snapshot.Effects @ [ created ] })
        |> Result.map (fun candidate ->
            AgentEffectDeclarationProposal
                { TransactionId = candidate.TransactionId
                  ProposedSnapshot = candidate.ProposedSnapshot
                  Review =
                    { RequestedChange = request
                      Receipt = candidate.Receipt
                      Readiness = candidate.Readiness
                      CreatedEffect = candidate.Prepared } })
        |> Result.mapError (function
            | DeclarationSimulationFailure.Rejected diagnostics -> AgentEffectDeclarationWorkflowError.SimulationRejected diagnostics
            | DeclarationSimulationFailure.FingerprintFailed error -> AgentEffectDeclarationWorkflowError.FingerprintFailed error)

    let private publishWithPolicy
        (workspace: AgentWorkspace)
        (AgentEffectDeclarationProposal proposal)
        (policy: CommitPolicy)
        : AgentEffectDeclarationCommitResult =
        let receipt = proposal.Review.Receipt

        if receipt.TransactionId <> proposal.TransactionId
           || receipt.ProposedSnapshotId <> proposal.ProposedSnapshot.Id then
            AgentEffectDeclarationCommitResult.Rejected
                [ diagnostic
                    "PreviewReceiptRequestMismatch"
                    "The reviewed receipt does not identify this effect-declaration proposal." ]
        else
            match
                StructuralTransactionInternals.publishCandidateWithMessages
                    (StructuralTransactionInternals.declarationPublicationMessages "effect")
                    workspace
                    proposal.ProposedSnapshot
                    proposal.Review.Readiness
                    receipt
                    policy
            with
            | Error diagnostics -> AgentEffectDeclarationCommitResult.Rejected diagnostics
            | Ok nextWorkspace ->
                AgentEffectDeclarationCommitResult.Committed(
                    nextWorkspace,
                    { TransactionId = proposal.TransactionId
                      BaseSnapshotId = receipt.BaseSnapshotId
                      PublishedSnapshotId = proposal.ProposedSnapshot.Id
                      CreatedEffectId = proposal.Review.CreatedEffect.Id
                      Readiness = proposal.Review.Readiness
                      Receipt = receipt }
                )

    let commit workspace proposal =
        publishWithPolicy workspace proposal CommitPolicy.RejectBlockingObligations

    let commitWithPolicy workspace proposal policy =
        publishWithPolicy workspace proposal policy
