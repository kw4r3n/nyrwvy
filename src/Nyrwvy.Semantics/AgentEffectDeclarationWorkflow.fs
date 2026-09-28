namespace Nyrwvy.Core

open System

type AgentCreateEffectDeclarationRequest =
    { ExpectedBaseSnapshotId: SnapshotId
      DisplayName: string }

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

[<RequireQualifiedAccess>]
module AgentEffectDeclarationWorkflow =
    let private diagnostic code message: TransactionDiagnostic =
        { Code = code
          Message = message }

    let private validationDiagnostics (issues: ValidationIssue list) =
        issues
        |> List.map (fun issue -> diagnostic ("Validation." + issue.Code) issue.Message)

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
          DisplayName = request.DisplayName }

    let simulate
        (workspace: AgentWorkspace)
        (request: AgentCreateEffectDeclarationRequest)
        : Result<AgentEffectDeclarationProposal, AgentEffectDeclarationWorkflowError> =
        let baseSnapshot = AgentWorkspace.snapshotForStructuralEditing workspace

        match SnapshotValidator.validate baseSnapshot with
        | issue :: rest ->
            Error(
                AgentEffectDeclarationWorkflowError.SimulationRejected(
                    validationDiagnostics (issue :: rest)
                )
            )
        | [] ->
            match validatePreflight baseSnapshot request with
            | Error error ->
                Error(AgentEffectDeclarationWorkflowError.SimulationRejected [ error ])
            | Ok() ->
                let createdEffect = materialize request
                let transactionId = StableIdAllocator.newTransactionId ()
                let proposedSnapshot =
                    { baseSnapshot with
                        Id = StableIdAllocator.newSnapshotId ()
                        ParentId = Some baseSnapshot.Id
                        Effects = baseSnapshot.Effects @ [ createdEffect ] }

                match SnapshotValidator.validate proposedSnapshot with
                | issue :: rest ->
                    Error(
                        AgentEffectDeclarationWorkflowError.SimulationRejected(
                            validationDiagnostics (issue :: rest)
                        )
                    )
                | [] ->
                    match StructuralTransactionInternals.classifyReadiness proposedSnapshot with
                    | Error diagnostics ->
                        Error(AgentEffectDeclarationWorkflowError.SimulationRejected diagnostics)
                    | Ok readiness ->
                        match StructuralTransactionInternals.computeReceipt transactionId baseSnapshot proposedSnapshot with
                        | Error error -> Error(AgentEffectDeclarationWorkflowError.FingerprintFailed error)
                        | Ok receipt ->
                            let review =
                                { RequestedChange = request
                                  Receipt = receipt
                                  Readiness = readiness
                                  CreatedEffect = createdEffect }

                            Ok(
                                AgentEffectDeclarationProposal
                                    { TransactionId = transactionId
                                      ProposedSnapshot = proposedSnapshot
                                      Review = review }
                            )

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

    let reject
        (workspace: AgentWorkspace)
        (AgentEffectDeclarationProposal proposal)
        : AgentWorkspace * TransactionRejectionReceipt =
        workspace,
        { TransactionId = proposal.TransactionId
          RetainedSnapshotId = AgentWorkspace.currentSnapshotId workspace }
