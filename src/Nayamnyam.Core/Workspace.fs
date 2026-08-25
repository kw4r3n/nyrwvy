namespace Nayamnyam.Core

[<RequireQualifiedAccess>]
type TransactionReadiness =
    | SemanticallyReady
    | Incomplete of ObligationId list

[<RequireQualifiedAccess>]
type CommitPolicy =
    | RejectBlockingObligations
    | AllowBlockingObligations

type WorkspaceState =
    private
        { CurrentSnapshot: Snapshot
          PreviousSnapshots: Snapshot list }

type WorkspacePreview =
    { Simulation: TransactionSimulation
      Readiness: TransactionReadiness }

[<RequireQualifiedAccess>]
type WorkspaceSimulationResult =
    | Rejected of TransactionDiagnostic list
    | Preview of WorkspacePreview

type PublishedTransaction =
    { TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      PublishedSnapshotId: SnapshotId
      Diff: SemanticDiff
      Readiness: TransactionReadiness }

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

    let private classifyReadiness
        (snapshot: Snapshot)
        : Result<TransactionReadiness, TransactionDiagnostic list> =
        match SnapshotReadiness.evaluate snapshot with
        | Error issues -> Error(validationDiagnostics issues)
        | Ok SemanticReadiness.Complete -> Ok TransactionReadiness.SemanticallyReady
        | Ok(SemanticReadiness.Incomplete obligationIds) ->
            Ok(TransactionReadiness.Incomplete obligationIds)

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
                WorkspaceSimulationResult.Preview
                    { Simulation = simulation
                      Readiness = readiness }

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
              Readiness = preview.Readiness }

        nextWorkspace, publication

    let commit
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

    let reject
        (workspace: WorkspaceState)
        (request: TransactionRequest)
        : WorkspaceState * TransactionRejectionReceipt =
        workspace,
        { TransactionId = request.Id
          RetainedSnapshotId = workspace.CurrentSnapshot.Id }
