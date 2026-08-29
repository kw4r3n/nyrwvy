namespace NyamNyam.Core.Tests

open NyamNyam.Core
open Xunit

module WorkspaceTests =
    let private createWorkspace (fixture: MilestoneZeroFixture) =
        match SemanticWorkspace.create fixture.Snapshot with
        | Ok workspace -> workspace
        | Error issues -> failwithf "Unexpected workspace creation failure: %A" issues

    let private current workspace = SemanticWorkspace.currentSnapshot workspace

    let private renameRequest
        (fixture: MilestoneZeroFixture)
        (baseId: SnapshotId)
        (transactionText: string)
        (snapshotText: string)
        (newName: string)
        : TransactionRequest =
        { Id = StableId.createForTesting<TransactionTag> transactionText
          BaseSnapshotId = baseId
          ProposedSnapshotId = StableId.createForTesting<SnapshotTag> snapshotText
          Operations =
            [ SemanticOperation.RenameDisplayName(
                RenameTarget.FunctionTarget fixture.ValidateFunctionId,
                newName
              ) ] }

    let private incompleteParameterRequest
        (fixture: MilestoneZeroFixture)
        (baseId: SnapshotId)
        (transactionText: string)
        (snapshotText: string)
        (parameterId: ParameterId)
        (obligationId: ObligationId)
        : TransactionRequest =
        { Id = StableId.createForTesting<TransactionTag> transactionText
          BaseSnapshotId = baseId
          ProposedSnapshotId = StableId.createForTesting<SnapshotTag> snapshotText
          Operations =
            [ SemanticOperation.AddFunctionParameter
                { TargetFunctionId = fixture.ValidateFunctionId
                  NewParameterId = parameterId
                  NewParameterDisplayName = "policy"
                  NewParameterType = TypeRef.Named fixture.PolicyTypeId
                  CallSitePlans = [ LeaveMissing(fixture.MainValidateCallNodeId, obligationId) ] } ] }

    [<Fact>]
    let ``workspace preview classifies a semantically ready transaction without publishing it`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let request = renameRequest fixture fixture.Snapshot.Id "transaction-workspace-preview" "snapshot-workspace-preview" "validate_profile"

        match SemanticWorkspace.simulate workspace request with
        | WorkspaceSimulationResult.Preview preview ->
            if preview.Readiness <> TransactionReadiness.SemanticallyReady then
                failwithf "Unexpected preview readiness: %A" preview.Readiness
            if current workspace <> fixture.Snapshot then
                failwith "Preview changed the authoritative current snapshot."
            if preview.Simulation.ProposedSnapshot.Id <> request.ProposedSnapshotId then
                failwith "Preview did not preserve the requested proposed snapshot identity."
        | WorkspaceSimulationResult.Rejected diagnostics ->
            failwithf "Unexpected preview rejection: %A" diagnostics

    [<Fact>]
    let ``commit publishes exactly the snapshot produced by preview`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let request = renameRequest fixture fixture.Snapshot.Id "transaction-workspace-commit" "snapshot-workspace-commit" "validate_profile"

        let preview =
            match SemanticWorkspace.simulate workspace request with
            | WorkspaceSimulationResult.Preview value -> value
            | WorkspaceSimulationResult.Rejected diagnostics -> failwithf "Unexpected preview rejection: %A" diagnostics

        match SemanticWorkspace.commit workspace request with
        | WorkspaceCommitResult.Committed(nextWorkspace, publication) ->
            if current nextWorkspace <> preview.Simulation.ProposedSnapshot then
                failwith "Commit did not publish the exact revalidated preview snapshot."
            if publication.Diff <> preview.Simulation.Diff || publication.Readiness <> preview.Readiness then
                failwith "Commit publication does not match preview facts."
            if SemanticWorkspace.previousSnapshots nextWorkspace <> [ fixture.Snapshot ] then
                failwithf "Unexpected workspace history: %A" (SemanticWorkspace.previousSnapshots nextWorkspace)
        | WorkspaceCommitResult.Rejected diagnostics -> failwithf "Unexpected commit rejection: %A" diagnostics

    [<Fact>]
    let ``explicit reject returns the exact unchanged workspace`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let request = renameRequest fixture fixture.Snapshot.Id "transaction-workspace-reject" "snapshot-workspace-reject" "validate_profile"
        let retained, receipt = SemanticWorkspace.reject workspace request

        if retained <> workspace then failwith "Reject changed workspace state."
        if receipt.TransactionId <> request.Id || receipt.RetainedSnapshotId <> fixture.Snapshot.Id then
            failwithf "Unexpected rejection receipt: %A" receipt

    [<Fact>]
    let ``commit rejects stale base requests after the current snapshot advances`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let firstRequest = renameRequest fixture fixture.Snapshot.Id "transaction-workspace-first" "snapshot-workspace-first" "validate_profile"

        let advanced =
            match SemanticWorkspace.commit workspace firstRequest with
            | WorkspaceCommitResult.Committed(nextWorkspace, _) -> nextWorkspace
            | WorkspaceCommitResult.Rejected diagnostics -> failwithf "Unexpected first commit rejection: %A" diagnostics

        let staleRequest = renameRequest fixture fixture.Snapshot.Id "transaction-workspace-stale" "snapshot-workspace-stale" "validate_again"
        match SemanticWorkspace.commit advanced staleRequest with
        | WorkspaceCommitResult.Rejected [ diagnostic ] when diagnostic.Code = "StaleBaseSnapshot" -> ()
        | result -> failwithf "Expected stale-base rejection, got %A" result

        if (current advanced).Id <> firstRequest.ProposedSnapshotId then
            failwith "Rejected stale commit changed the current snapshot."

    [<Fact>]
    let ``blocking obligations require explicit incomplete commit policy`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let parameterId = StableId.createForTesting<ParameterTag> "parameter-workspace-policy"
        let obligationId = StableId.createForTesting<ObligationTag> "obligation-workspace-policy"
        let request = incompleteParameterRequest fixture fixture.Snapshot.Id "transaction-workspace-incomplete" "snapshot-workspace-incomplete" parameterId obligationId

        match SemanticWorkspace.simulate workspace request with
        | WorkspaceSimulationResult.Preview preview when preview.Readiness = TransactionReadiness.Incomplete [ obligationId ] -> ()
        | result -> failwithf "Unexpected incomplete preview result: %A" result

        match SemanticWorkspace.commit workspace request with
        | WorkspaceCommitResult.Rejected [ diagnostic ] when diagnostic.Code = "BlockingObligations" -> ()
        | result -> failwithf "Expected conservative blocking-obligation rejection, got %A" result

        if current workspace <> fixture.Snapshot then failwith "Rejected incomplete commit changed the original workspace."

        match SemanticWorkspace.commitWithPolicy workspace request CommitPolicy.AllowBlockingObligations with
        | WorkspaceCommitResult.Committed(nextWorkspace, publication) ->
            if publication.Readiness <> TransactionReadiness.Incomplete [ obligationId ] then
                failwithf "Unexpected published incomplete readiness: %A" publication.Readiness
            if (current nextWorkspace).Id <> request.ProposedSnapshotId then
                failwith "Explicit incomplete commit did not publish the proposed snapshot."
        | WorkspaceCommitResult.Rejected diagnostics -> failwithf "Explicit incomplete commit was unexpectedly rejected: %A" diagnostics

    [<Fact>]
    let ``resolving a published obligation restores semantic readiness`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let parameterId = StableId.createForTesting<ParameterTag> "parameter-workspace-resolve-policy"
        let obligationId = StableId.createForTesting<ObligationTag> "obligation-workspace-resolve-policy"
        let incompleteRequest = incompleteParameterRequest fixture fixture.Snapshot.Id "transaction-workspace-resolve-add" "snapshot-workspace-resolve-incomplete" parameterId obligationId

        let incompleteWorkspace =
            match SemanticWorkspace.commitWithPolicy workspace incompleteRequest CommitPolicy.AllowBlockingObligations with
            | WorkspaceCommitResult.Committed(nextWorkspace, _) -> nextWorkspace
            | WorkspaceCommitResult.Rejected diagnostics -> failwithf "Unexpected incomplete commit rejection: %A" diagnostics

        let resolveRequest: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-workspace-resolve"
              BaseSnapshotId = (current incompleteWorkspace).Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-workspace-resolved"
              Operations =
                [ SemanticOperation.ResolveMissingArgument
                    { TargetObligationId = obligationId
                      ResolutionValueNodeId = fixture.MainPolicyNodeId } ] }

        match SemanticWorkspace.commit incompleteWorkspace resolveRequest with
        | WorkspaceCommitResult.Committed(resolvedWorkspace, publication) ->
            if publication.Readiness <> TransactionReadiness.SemanticallyReady then
                failwithf "Resolved publication is not semantically ready: %A" publication.Readiness
            match SnapshotReadiness.evaluate (current resolvedWorkspace) with
            | Ok SemanticReadiness.Complete -> ()
            | readiness -> failwithf "Resolved current snapshot is not semantically complete: %A" readiness
        | WorkspaceCommitResult.Rejected diagnostics -> failwithf "Unexpected resolution commit rejection: %A" diagnostics

    [<Fact>]
    let ``workspace rejects reuse of a previously published snapshot identity`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let firstRequest = renameRequest fixture fixture.Snapshot.Id "transaction-workspace-history-first" "snapshot-workspace-history-first" "validate_profile"

        let advanced =
            match SemanticWorkspace.commit workspace firstRequest with
            | WorkspaceCommitResult.Committed(nextWorkspace, _) -> nextWorkspace
            | WorkspaceCommitResult.Rejected diagnostics -> failwithf "Unexpected first commit rejection: %A" diagnostics

        let reuseRequest: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-workspace-history-reuse"
              BaseSnapshotId = (current advanced).Id
              ProposedSnapshotId = fixture.Snapshot.Id
              Operations =
                [ SemanticOperation.RenameDisplayName(
                    RenameTarget.FunctionTarget fixture.ReportFunctionId,
                    "report_again"
                  ) ] }

        match SemanticWorkspace.commit advanced reuseRequest with
        | WorkspaceCommitResult.Rejected [ diagnostic ] when diagnostic.Code = "SnapshotAlreadyPublished" -> ()
        | result -> failwithf "Expected published-snapshot ID reuse rejection, got %A" result

    [<Fact>]
    let ``trusted validated publication preserves prior workspace history`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let firstRequest =
            renameRequest
                fixture
                fixture.Snapshot.Id
                "transaction-workspace-trusted-first"
                "snapshot-workspace-trusted-first"
                "validate_profile"

        let advanced =
            match SemanticWorkspace.commit workspace firstRequest with
            | WorkspaceCommitResult.Committed(nextWorkspace, _) -> nextWorkspace
            | WorkspaceCommitResult.Rejected diagnostics ->
                failwithf "Unexpected ordinary commit rejection: %A" diagnostics

        let beforeTrustedPublish = current advanced
        let trustedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-workspace-trusted-published"
        let trustedSnapshot =
            { beforeTrustedPublish with
                Id = trustedSnapshotId
                ParentId = Some beforeTrustedPublish.Id
                Functions =
                    beforeTrustedPublish.Functions
                    |> List.map (fun functionEntity ->
                        if functionEntity.Id = fixture.ReportFunctionId then
                            { functionEntity with DisplayName = "report_trusted" }
                        else
                            functionEntity) }

        match SemanticWorkspace.publishValidatedSnapshot advanced trustedSnapshot with
        | Error diagnostics -> failwithf "Trusted publication was rejected: %A" diagnostics
        | Ok published ->
            if current published <> trustedSnapshot then
                failwith "Trusted publication did not install the exact validated snapshot."

            let expectedHistory = [ beforeTrustedPublish; fixture.Snapshot ]
            if SemanticWorkspace.previousSnapshots published <> expectedHistory then
                failwithf
                    "Trusted publication lost workspace history. Expected %A, got %A"
                    (expectedHistory |> List.map (fun snapshot -> snapshot.Id))
                    (SemanticWorkspace.previousSnapshots published |> List.map (fun snapshot -> snapshot.Id))
