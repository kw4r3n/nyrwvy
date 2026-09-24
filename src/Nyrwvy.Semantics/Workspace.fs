namespace Nyrwvy.Core

[<RequireQualifiedAccess>]
type TransactionReadiness =
    | SemanticallyReady
    | Incomplete of ObligationId list

[<RequireQualifiedAccess>]
type CommitPolicy =
    | RejectBlockingObligations
    | AllowBlockingObligations

type WorkspaceState = private { CurrentSnapshot: Snapshot; PreviousSnapshots: Snapshot list }

/// In-process review binding, not a receipt reconstructed from wire fingerprint facts.
/// Structural workflows retain their intent in opaque proposals instead of a TransactionRequest.
type TransactionPreviewReceipt =
    private
        { RecordedTransactionId: TransactionId
          RecordedBaseSnapshotId: SnapshotId
          RecordedBaseSnapshotFingerprint: SnapshotFingerprint
          RecordedProposedSnapshotId: SnapshotId
          RecordedProposedSnapshotFingerprint: SnapshotFingerprint
          RecordedProposedSemanticFingerprint: SemanticFingerprint
          ReviewedRequest: TransactionRequest option }

    member this.TransactionId = this.RecordedTransactionId
    member this.BaseSnapshotId = this.RecordedBaseSnapshotId
    member this.BaseSnapshotFingerprint = this.RecordedBaseSnapshotFingerprint
    member this.ProposedSnapshotId = this.RecordedProposedSnapshotId
    member this.ProposedSnapshotFingerprint = this.RecordedProposedSnapshotFingerprint
    member this.ProposedSemanticFingerprint = this.RecordedProposedSemanticFingerprint

    member internal this.MatchesRequest(request: TransactionRequest) =
        this.ReviewedRequest = Some request

module internal TransactionPreviewReceipt =
    let create reviewedRequest transactionId baseSnapshotId baseFingerprint proposedSnapshotId proposedFingerprint semanticFingerprint =
        { RecordedTransactionId = transactionId
          RecordedBaseSnapshotId = baseSnapshotId
          RecordedBaseSnapshotFingerprint = baseFingerprint
          RecordedProposedSnapshotId = proposedSnapshotId
          RecordedProposedSnapshotFingerprint = proposedFingerprint
          RecordedProposedSemanticFingerprint = semanticFingerprint
          ReviewedRequest = reviewedRequest }

type WorkspacePreview =
    { Simulation: TransactionSimulation
      Readiness: TransactionReadiness
      Receipt: TransactionPreviewReceipt }

[<RequireQualifiedAccess>]
type WorkspaceSimulationResult =
    | Rejected of TransactionDiagnostic list
    | Preview of WorkspacePreview

type PublishedTransaction =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      PublishedSnapshotId: SnapshotId
      Diff: SemanticDiff
      Readiness: TransactionReadiness
      Receipt: TransactionPreviewReceipt }

[<RequireQualifiedAccess>]
type WorkspaceCommitResult =
    | Rejected of TransactionDiagnostic list
    | Committed of WorkspaceState * PublishedTransaction

type TransactionRejectionReceipt =
    { TransactionId: TransactionId
      RetainedSnapshotId: SnapshotId }

[<RequireQualifiedAccess>]
module SemanticWorkspace =
    let private diagnostic code message: TransactionDiagnostic =
        { Code = code
          Message = message }

    let private validationDiagnostics (issues: ValidationIssue list): TransactionDiagnostic list =
        issues
        |> List.map (fun issue ->
            diagnostic
                ("Validation." + issue.Code)
                issue.Message)

    let private fingerprintDiagnostic context error =
        diagnostic
            "FingerprintFailure"
            $"{context} fingerprint could not be computed: {error}."

    let create (initialSnapshot: Snapshot): Result<WorkspaceState, ValidationIssue list> =
        match SnapshotValidator.validate initialSnapshot with
        | [] ->
            Ok
                { CurrentSnapshot = initialSnapshot
                  PreviousSnapshots = [] }
        | issues -> Error issues

    let currentSnapshot (workspace: WorkspaceState): Snapshot =
        workspace.CurrentSnapshot

    let previousSnapshots (workspace: WorkspaceState): Snapshot list =
        workspace.PreviousSnapshots

    let allSnapshots (workspace: WorkspaceState): Snapshot list =
        workspace.CurrentSnapshot :: workspace.PreviousSnapshots

    let tryGetSnapshot
        (snapshotId: SnapshotId)
        (workspace: WorkspaceState)
        : Snapshot option =
        allSnapshots workspace
        |> List.tryFind (fun snapshot -> snapshot.Id = snapshotId)

    let private hasPublishedSnapshotId
        (snapshotId: SnapshotId)
        (workspace: WorkspaceState)
        : bool =
        tryGetSnapshot snapshotId workspace |> Option.isSome

    /// Trusted publication primitive for semantic infrastructure that has already
    /// produced an exact validated snapshot outside the ordinary SemanticOperation
    /// transaction path. It preserves the same workspace-history invariant as
    /// normal publication and remains internal to the semantics assembly.
    let internal publishValidatedSnapshot
        (workspace: WorkspaceState)
        (snapshot: Snapshot)
        : Result<WorkspaceState, TransactionDiagnostic list> =
        if snapshot.ParentId <> Some workspace.CurrentSnapshot.Id then
            Error
                [ diagnostic
                    "StaleBaseSnapshot"
                    "The validated snapshot does not name the current workspace snapshot as its parent." ]
        elif hasPublishedSnapshotId snapshot.Id workspace then
            Error
                [ diagnostic
                    "SnapshotAlreadyPublished"
                    "The proposed snapshot ID has already been published in this workspace." ]
        else
            match SnapshotValidator.validate snapshot with
            | issue :: rest -> Error(validationDiagnostics (issue :: rest))
            | [] ->
                Ok
                    { CurrentSnapshot = snapshot
                      PreviousSnapshots = workspace.CurrentSnapshot :: workspace.PreviousSnapshots }

    let private classifyReadiness
        (snapshot: Snapshot)
        : Result<TransactionReadiness, TransactionDiagnostic list> =
        match SnapshotReadiness.evaluate snapshot with
        | Error issues -> Error(validationDiagnostics issues)
        | Ok SemanticReadiness.Complete -> Ok TransactionReadiness.SemanticallyReady
        | Ok(SemanticReadiness.Incomplete obligationIds) ->
            Ok(TransactionReadiness.Incomplete obligationIds)

    let private createReceipt
        (request: TransactionRequest)
        (baseSnapshot: Snapshot)
        (simulation: TransactionSimulation)
        : Result<TransactionPreviewReceipt, TransactionDiagnostic list> =
        match SnapshotFingerprint.compute baseSnapshot with
        | Error error -> Error [ fingerprintDiagnostic "Base snapshot" error ]
        | Ok baseFingerprint ->
            match SnapshotFingerprint.compute simulation.ProposedSnapshot with
            | Error error -> Error [ fingerprintDiagnostic "Proposed snapshot" error ]
            | Ok proposedFingerprint ->
                match SemanticFingerprint.compute simulation.ProposedSnapshot with
                | Error error -> Error [ fingerprintDiagnostic "Proposed semantic state" error ]
                | Ok semanticFingerprint ->
                    Ok(
                        TransactionPreviewReceipt.create
                            (Some request)
                            simulation.TransactionId
                            simulation.BaseSnapshotId
                            baseFingerprint
                            simulation.ProposedSnapshot.Id
                            proposedFingerprint
                            semanticFingerprint
                    )

    let simulate
        (workspace: WorkspaceState)
        (request: TransactionRequest)
        : WorkspaceSimulationResult =
        match Transaction.simulate workspace.CurrentSnapshot request with
        | SimulationResult.Rejected diagnostics ->
            WorkspaceSimulationResult.Rejected diagnostics
        | SimulationResult.Proposed simulation ->
            match classifyReadiness simulation.ProposedSnapshot with
            | Error diagnostics -> WorkspaceSimulationResult.Rejected diagnostics
            | Ok readiness ->
                match createReceipt request workspace.CurrentSnapshot simulation with
                | Error diagnostics -> WorkspaceSimulationResult.Rejected diagnostics
                | Ok receipt ->
                    WorkspaceSimulationResult.Preview
                        { Simulation = simulation
                          Readiness = readiness
                          Receipt = receipt }

    let private publish
        (workspace: WorkspaceState)
        (preview: WorkspacePreview)
        : WorkspaceState * PublishedTransaction =
        let simulation = preview.Simulation
        let nextWorkspace =
            { CurrentSnapshot = simulation.ProposedSnapshot
              PreviousSnapshots = workspace.CurrentSnapshot :: workspace.PreviousSnapshots }

        let publication =
            { TransactionId = simulation.TransactionId
              BaseSnapshotId = simulation.BaseSnapshotId
              PublishedSnapshotId = simulation.ProposedSnapshot.Id
              Diff = simulation.Diff
              Readiness = preview.Readiness
              Receipt = preview.Receipt }

        nextWorkspace, publication

    let private publishWithPolicy
        (workspace: WorkspaceState)
        (preview: WorkspacePreview)
        (policy: CommitPolicy)
        : WorkspaceCommitResult =
        match preview.Readiness, policy with
        | TransactionReadiness.SemanticallyReady, _ ->
            let nextWorkspace, publication = publish workspace preview
            WorkspaceCommitResult.Committed(nextWorkspace, publication)
        | TransactionReadiness.Incomplete _, CommitPolicy.AllowBlockingObligations ->
            let nextWorkspace, publication = publish workspace preview
            WorkspaceCommitResult.Committed(nextWorkspace, publication)
        | TransactionReadiness.Incomplete obligationIds, CommitPolicy.RejectBlockingObligations ->
            let ids =
                obligationIds
                |> List.map StableId.value
                |> String.concat ", "
            WorkspaceCommitResult.Rejected
                [ diagnostic
                    "BlockingObligations"
                    $"The proposed snapshot has blocking obligations: {ids}. Use AllowBlockingObligations explicitly to publish it." ]

    let commitWithPolicy
        (workspace: WorkspaceState)
        (request: TransactionRequest)
        (policy: CommitPolicy)
        : WorkspaceCommitResult =
        if hasPublishedSnapshotId request.ProposedSnapshotId workspace then
            WorkspaceCommitResult.Rejected
                [ diagnostic
                    "SnapshotAlreadyPublished"
                    "The proposed snapshot ID has already been published in this workspace." ]
        else
            match simulate workspace request with
            | WorkspaceSimulationResult.Rejected diagnostics ->
                WorkspaceCommitResult.Rejected diagnostics
            | WorkspaceSimulationResult.Preview preview ->
                publishWithPolicy workspace preview policy

    let commit
        (workspace: WorkspaceState)
        (request: TransactionRequest)
        : WorkspaceCommitResult =
        commitWithPolicy workspace request CommitPolicy.RejectBlockingObligations

    let commitReviewedWithPolicy
        (workspace: WorkspaceState)
        (request: TransactionRequest)
        (reviewedReceipt: TransactionPreviewReceipt)
        (policy: CommitPolicy)
        : WorkspaceCommitResult =
        if reviewedReceipt.TransactionId <> request.Id
           || reviewedReceipt.BaseSnapshotId <> request.BaseSnapshotId
           || reviewedReceipt.ProposedSnapshotId <> request.ProposedSnapshotId then
            WorkspaceCommitResult.Rejected
                [ diagnostic
                    "PreviewReceiptRequestMismatch"
                    "The reviewed preview receipt does not identify the transaction request being committed." ]
        elif hasPublishedSnapshotId request.ProposedSnapshotId workspace then
            WorkspaceCommitResult.Rejected
                [ diagnostic
                    "SnapshotAlreadyPublished"
                    "The proposed snapshot ID has already been published in this workspace." ]
        else
            match simulate workspace request with
            | WorkspaceSimulationResult.Rejected diagnostics ->
                WorkspaceCommitResult.Rejected diagnostics
            | WorkspaceSimulationResult.Preview regenerated ->
                if regenerated.Receipt.BaseSnapshotFingerprint <> reviewedReceipt.BaseSnapshotFingerprint then
                    WorkspaceCommitResult.Rejected
                        [ diagnostic
                            "PreviewReceiptBaseMismatch"
                            "The current base snapshot fingerprint differs from the reviewed preview receipt." ]
                elif regenerated.Receipt.ProposedSnapshotFingerprint <> reviewedReceipt.ProposedSnapshotFingerprint
                     || regenerated.Receipt.ProposedSemanticFingerprint <> reviewedReceipt.ProposedSemanticFingerprint then
                    WorkspaceCommitResult.Rejected
                        [ diagnostic
                            "PreviewReceiptResultMismatch"
                            "Re-simulation produced a proposed state different from the reviewed preview receipt." ]
                elif not (reviewedReceipt.MatchesRequest request) then
                    WorkspaceCommitResult.Rejected
                        [ diagnostic
                            "PreviewReceiptRequestMismatch"
                            "The reviewed preview receipt is not bound to this exact ordered transaction request." ]
                else
                    publishWithPolicy workspace regenerated policy

    let commitReviewed
        (workspace: WorkspaceState)
        (request: TransactionRequest)
        (reviewedReceipt: TransactionPreviewReceipt)
        : WorkspaceCommitResult =
        commitReviewedWithPolicy
            workspace
            request
            reviewedReceipt
            CommitPolicy.RejectBlockingObligations

    let reject
        (workspace: WorkspaceState)
        (request: TransactionRequest)
        : WorkspaceState * TransactionRejectionReceipt =
        workspace,
        { TransactionId = request.Id
          RetainedSnapshotId = workspace.CurrentSnapshot.Id }
