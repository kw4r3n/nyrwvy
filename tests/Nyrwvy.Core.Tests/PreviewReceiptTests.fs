namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Xunit

module PreviewReceiptTests =
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s failed: %A" label error

    let private createWorkspace (fixture: MilestoneZeroFixture) =
        SemanticWorkspace.create fixture.Snapshot |> unwrap "workspace creation"

    let private renameRequest
        (fixture: MilestoneZeroFixture)
        (transactionText: string)
        (snapshotText: string)
        (newName: string)
        : TransactionRequest =
        { Id = StableId.createForTesting<TransactionTag> transactionText
          BaseSnapshotId = fixture.Snapshot.Id
          ProposedSnapshotId = StableId.createForTesting<SnapshotTag> snapshotText
          Operations =
            [ SemanticOperation.RenameDisplayName(
                RenameTarget.FunctionTarget fixture.ValidateFunctionId,
                newName
              ) ] }

    let private preview workspace request =
        match SemanticWorkspace.simulate workspace request with
        | WorkspaceSimulationResult.Preview value -> value
        | WorkspaceSimulationResult.Rejected diagnostics ->
            failwithf "Unexpected preview rejection: %A" diagnostics

    [<Fact>]
    let ``preview receipt binds exact base proposed and semantic fingerprints`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let request = renameRequest fixture "transaction-receipt-bind" "snapshot-receipt-bind" "validate_profile"
        let result = preview workspace request

        let expectedBase = SnapshotFingerprint.compute fixture.Snapshot |> unwrap "base fingerprint"
        let expectedProposed = SnapshotFingerprint.compute result.Simulation.ProposedSnapshot |> unwrap "proposed fingerprint"
        let expectedSemantic = SemanticFingerprint.compute result.Simulation.ProposedSnapshot |> unwrap "semantic fingerprint"

        if result.Receipt.TransactionId <> request.Id
           || result.Receipt.BaseSnapshotId <> request.BaseSnapshotId
           || result.Receipt.ProposedSnapshotId <> request.ProposedSnapshotId then
            failwithf "Preview receipt identities do not match the request: %A" result.Receipt
        if result.Receipt.BaseSnapshotFingerprint <> expectedBase then
            failwith "Preview receipt base fingerprint differs from direct computation."
        if result.Receipt.ProposedSnapshotFingerprint <> expectedProposed then
            failwith "Preview receipt proposed fingerprint differs from direct computation."
        if result.Receipt.ProposedSemanticFingerprint <> expectedSemantic then
            failwith "Preview receipt semantic fingerprint differs from direct computation."

    [<Fact>]
    let ``reviewed commit publishes the exact re-simulated receipt`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let request = renameRequest fixture "transaction-receipt-commit" "snapshot-receipt-commit" "validate_profile"
        let reviewed = preview workspace request

        match SemanticWorkspace.commitReviewed workspace request reviewed.Receipt with
        | WorkspaceCommitResult.Committed(nextWorkspace, publication) ->
            if publication.Receipt <> reviewed.Receipt then
                failwith "Published transaction receipt differs from the reviewed receipt."
            let published = SemanticWorkspace.currentSnapshot nextWorkspace
            let actual = SnapshotFingerprint.compute published |> unwrap "published fingerprint"
            if actual <> reviewed.Receipt.ProposedSnapshotFingerprint then
                failwith "Published snapshot does not match the reviewed proposed fingerprint."
        | WorkspaceCommitResult.Rejected diagnostics ->
            failwithf "Reviewed commit was unexpectedly rejected: %A" diagnostics

    [<Fact>]
    let ``reviewed commit rejects a receipt identifying another request`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let first = renameRequest fixture "transaction-receipt-first" "snapshot-receipt-first" "validate_profile"
        let second = renameRequest fixture "transaction-receipt-second" "snapshot-receipt-second" "validate_again"
        let reviewed = preview workspace first

        match SemanticWorkspace.commitReviewed workspace second reviewed.Receipt with
        | WorkspaceCommitResult.Rejected [ diagnostic ] when diagnostic.Code = "PreviewReceiptRequestMismatch" -> ()
        | result -> failwithf "Expected preview request-mismatch rejection, got %A" result

    [<Fact>]
    let ``reviewed commit rejects a mismatched base fingerprint`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let request = renameRequest fixture "transaction-receipt-base" "snapshot-receipt-base" "validate_profile"
        let reviewed = preview workspace request
        let mismatched =
            { reviewed.Receipt with
                BaseSnapshotFingerprint = reviewed.Receipt.ProposedSnapshotFingerprint }

        match SemanticWorkspace.commitReviewed workspace request mismatched with
        | WorkspaceCommitResult.Rejected [ diagnostic ] when diagnostic.Code = "PreviewReceiptBaseMismatch" -> ()
        | result -> failwithf "Expected preview base-fingerprint rejection, got %A" result

    [<Fact>]
    let ``reviewed commit rejects a mismatched proposed fingerprint`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let request = renameRequest fixture "transaction-receipt-result" "snapshot-receipt-result" "validate_profile"
        let reviewed = preview workspace request
        let mismatched =
            { reviewed.Receipt with
                ProposedSnapshotFingerprint = reviewed.Receipt.BaseSnapshotFingerprint }

        match SemanticWorkspace.commitReviewed workspace request mismatched with
        | WorkspaceCommitResult.Rejected [ diagnostic ] when diagnostic.Code = "PreviewReceiptResultMismatch" -> ()
        | result -> failwithf "Expected preview result-fingerprint rejection, got %A" result

    [<Fact>]
    let ``reviewed incomplete commit still requires explicit blocking-obligation policy`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let parameterId = StableId.createForTesting<ParameterTag> "parameter-receipt-policy"
        let obligationId = StableId.createForTesting<ObligationTag> "obligation-receipt-policy"
        let request: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-receipt-incomplete"
              BaseSnapshotId = fixture.Snapshot.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-receipt-incomplete"
              Operations =
                [ SemanticOperation.AddFunctionParameter
                    { TargetFunctionId = fixture.ValidateFunctionId
                      NewParameterId = parameterId
                      NewParameterDisplayName = "policy"
                      NewParameterType = TypeRef.Named fixture.PolicyTypeId
                      CallSitePlans = [ LeaveMissing(fixture.MainValidateCallNodeId, obligationId) ] } ] }
        let reviewed = preview workspace request

        match SemanticWorkspace.commitReviewed workspace request reviewed.Receipt with
        | WorkspaceCommitResult.Rejected [ diagnostic ] when diagnostic.Code = "BlockingObligations" -> ()
        | result -> failwithf "Expected conservative reviewed-commit rejection, got %A" result

        match
            SemanticWorkspace.commitReviewedWithPolicy
                workspace
                request
                reviewed.Receipt
                CommitPolicy.AllowBlockingObligations
        with
        | WorkspaceCommitResult.Committed(_, publication) ->
            if publication.Receipt <> reviewed.Receipt then
                failwith "Explicit incomplete publication did not retain the reviewed receipt."
            if publication.Readiness <> TransactionReadiness.Incomplete [ obligationId ] then
                failwithf "Unexpected incomplete publication readiness: %A" publication.Readiness
        | WorkspaceCommitResult.Rejected diagnostics ->
            failwithf "Explicit reviewed incomplete commit was rejected: %A" diagnostics
