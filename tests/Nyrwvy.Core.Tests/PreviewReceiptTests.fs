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

    [<Theory>]
    [<InlineData("type")>]
    [<InlineData("function")>]
    [<InlineData("field")>]
    [<InlineData("variant")>]
    [<InlineData("parameter")>]
    [<InlineData("effect")>]
    [<InlineData("capability")>]
    [<InlineData("operation")>]
    [<InlineData("capability-parameter")>]
    let ``every display-name rename retains the direct proposed semantic fingerprint`` (kind: string) =
        Assert.Equal(9, Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<RenameTarget>).Length)
        let fixture = MilestoneZeroGraph.create ()
        let target =
            match kind with
            | "type" -> RenameTarget.TypeTarget fixture.PolicyTypeId
            | "function" -> RenameTarget.FunctionTarget fixture.ValidateFunctionId
            | "field" ->
                fixture.Snapshot.Types |> List.pick (fun t ->
                    match t.Kind with
                    | SemanticTypeKind.Product(field :: _) -> Some(RenameTarget.FieldTarget field.Id)
                    | _ -> None)
            | "variant" ->
                fixture.Snapshot.Types |> List.pick (fun t ->
                    match t.Kind with
                    | SemanticTypeKind.Union(variant :: _) -> Some(RenameTarget.VariantTarget variant.Id)
                    | _ -> None)
            | "parameter" -> RenameTarget.ParameterTarget fixture.ValidateInputParameterId
            | "effect" -> RenameTarget.EffectTarget fixture.ConsoleEffectId
            | "capability" -> RenameTarget.CapabilityTarget fixture.ConsoleCapabilityId
            | "operation" -> RenameTarget.CapabilityOperationTarget fixture.ConsoleWriteLineOperationId
            | "capability-parameter" ->
                fixture.Snapshot.Capabilities
                |> List.collect (fun c -> c.Operations)
                |> List.pick (fun op -> op.Parameters |> List.tryHead)
                |> fun p -> RenameTarget.CapabilityParameterTarget p.Id
            | _ -> failwith "unknown rename target"
        let request =
            { renameRequest fixture ("rename-" + kind) ("renamed-" + kind) "renamed" with
                Operations = [ SemanticOperation.RenameDisplayName(target, "renamed") ] }
        let result = preview (createWorkspace fixture) request
        let direct = SemanticFingerprint.compute result.Simulation.ProposedSnapshot |> unwrap "direct fingerprint"
        Assert.Equal(direct, result.Receipt.ProposedSemanticFingerprint)

    [<Fact>]
    let ``reviewed rename reuses base semantic facts but still validates the regenerated candidate`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let request = renameRequest fixture "rename-reuse" "snapshot-rename-reuse" "renamed"
        let reviewed =
            use scope = SnapshotComputationScope.Start(measure = true)
            let result = preview workspace request
            let semantic = scope.Measurements |> List.find (fun m -> m.Name = "semanticFingerprint")
            Assert.Equal(1, semantic.Computations)
            result
        use scope = SnapshotComputationScope.Start(measure = true)
        match SemanticWorkspace.commitReviewed workspace request reviewed.Receipt with
        | WorkspaceCommitResult.Rejected errors -> failwithf "commit failed: %A" errors
        | WorkspaceCommitResult.Committed(_, publication) -> Assert.Equal(reviewed.Receipt, publication.Receipt)
        let semantic = scope.Measurements |> List.find (fun m -> m.Name = "semanticFingerprint")
        Assert.Equal(0, semantic.Computations)
        Assert.Equal(1, semantic.CacheHits)
        let validation = scope.Measurements |> List.find (fun m -> m.Name = "validation")
        Assert.Equal(1, validation.Computations)

    [<Fact>]
    let ``a mixed rename and visibility edit computes the proposed semantic state`` () =
        let fixture = MilestoneZeroGraph.create ()
        let request =
            { renameRequest fixture "rename-and-visibility" "snapshot-rename-and-visibility" "renamed" with
                Operations =
                    [ SemanticOperation.RenameDisplayName(RenameTarget.FunctionTarget fixture.ValidateFunctionId, "renamed")
                      SemanticOperation.SetExported(VisibilityTarget.FunctionTarget fixture.ValidateFunctionId, false) ] }
        let result =
            use scope = SnapshotComputationScope.Start(measure = true)
            let result = preview (createWorkspace fixture) request
            let semantic = scope.Measurements |> List.find (fun m -> m.Name = "semanticFingerprint")
            Assert.Equal(1, semantic.Computations)
            result
        let direct = SemanticFingerprint.compute result.Simulation.ProposedSnapshot |> unwrap "direct fingerprint"
        let original = SemanticFingerprint.compute fixture.Snapshot |> unwrap "base fingerprint"
        Assert.Equal(direct, result.Receipt.ProposedSemanticFingerprint)
        Assert.NotEqual(original, result.Receipt.ProposedSemanticFingerprint)

    [<Fact>]
    let ``a rename followed by an unchanged visibility operation is still rejected`` () =
        let fixture = MilestoneZeroGraph.create ()
        let request =
            { renameRequest fixture "rename-noop" "snapshot-rename-noop" "renamed" with
                Operations =
                    [ SemanticOperation.RenameDisplayName(RenameTarget.FunctionTarget fixture.ValidateFunctionId, "renamed")
                      SemanticOperation.SetExported(VisibilityTarget.FunctionTarget fixture.ValidateFunctionId, true) ] }
        match SemanticWorkspace.simulate (createWorkspace fixture) request with
        | WorkspaceSimulationResult.Rejected [ diagnostic ] -> Assert.Equal("VisibilityUnchanged", diagnostic.Code)
        | other -> failwithf "Expected unchanged-visibility rejection, got %A" other

    [<Fact>]
    let ``an empty request cannot reach receipt creation`` () =
        let fixture = MilestoneZeroGraph.create ()
        let request = { renameRequest fixture "empty-request" "empty-proposed" "unused" with Operations = [] }
        match SemanticWorkspace.simulate (createWorkspace fixture) request with
        | WorkspaceSimulationResult.Rejected [ diagnostic ] -> Assert.Equal("NoOperations", diagnostic.Code)
        | other -> failwithf "Expected empty-operation rejection, got %A" other

    [<Fact>]
    let ``reviewed commit publishes the exact re-simulated receipt`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let request = renameRequest fixture "transaction-receipt-commit" "snapshot-receipt-commit" "validate_profile"
        let reviewed = preview workspace request

        let equivalentRequest = renameRequest fixture "transaction-receipt-commit" "snapshot-receipt-commit" "validate_profile"
        Assert.False(obj.ReferenceEquals(request, equivalentRequest))

        match SemanticWorkspace.commitReviewed workspace equivalentRequest reviewed.Receipt with
        | WorkspaceCommitResult.Committed(nextWorkspace, publication) ->
            if publication.Receipt <> reviewed.Receipt then
                failwith "Published transaction receipt differs from the reviewed receipt."
            let published = SemanticWorkspace.currentSnapshot nextWorkspace
            let actual = SnapshotFingerprint.compute published |> unwrap "published fingerprint"
            if actual <> reviewed.Receipt.ProposedSnapshotFingerprint then
                failwith "Published snapshot does not match the reviewed proposed fingerprint."
        | WorkspaceCommitResult.Rejected diagnostics ->
            failwithf "Reviewed commit was unexpectedly rejected: %A" diagnostics

    [<Theory>]
    [<InlineData("insert")>]
    [<InlineData("remove")>]
    [<InlineData("replace")>]
    [<InlineData("reorder")>]
    [<InlineData("duplicate")>]
    [<InlineData("roundtrip")>]
    let ``reviewed commit rejects substituted ordered intent even when the exact result matches`` substitution =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let rename name =
            SemanticOperation.RenameDisplayName(RenameTarget.FunctionTarget fixture.ValidateFunctionId, name)
        let finalRename = rename "validate_profile"
        let intermediateRename = rename "validate_intermediate"
        let typeRename =
            SemanticOperation.RenameDisplayName(RenameTarget.TypeTarget fixture.PolicyTypeId, "ProfilePolicy")
        let reviewedOperations, substitutedOperations =
            match substitution with
            | "insert" -> [ finalRename ], [ intermediateRename; finalRename ]
            | "remove" -> [ intermediateRename; finalRename ], [ finalRename ]
            | "replace" -> [ intermediateRename; finalRename ], [ rename "validate_other"; finalRename ]
            | "reorder" -> [ finalRename; typeRename ], [ typeRename; finalRename ]
            | "duplicate" -> [ finalRename ], [ finalRename; finalRename ]
            | "roundtrip" ->
                [ finalRename ],
                [ typeRename
                  SemanticOperation.RenameDisplayName(RenameTarget.TypeTarget fixture.PolicyTypeId, "ValidationPolicy")
                  finalRename ]
            | other -> failwithf "Unknown substitution: %s" other
        let request =
            { renameRequest fixture "transaction-receipt-intent" "snapshot-receipt-intent" "validate_profile" with
                Operations = reviewedOperations }
        let substituted = { request with Operations = substitutedOperations }
        let reviewed = preview workspace request
        let substitutedPreview = preview workspace substituted

        Assert.Equal(reviewed.Simulation.ProposedSnapshot, substitutedPreview.Simulation.ProposedSnapshot)
        Assert.Equal(reviewed.Receipt.BaseSnapshotFingerprint, substitutedPreview.Receipt.BaseSnapshotFingerprint)
        Assert.Equal(reviewed.Receipt.ProposedSnapshotFingerprint, substitutedPreview.Receipt.ProposedSnapshotFingerprint)
        Assert.Equal(reviewed.Receipt.ProposedSemanticFingerprint, substitutedPreview.Receipt.ProposedSemanticFingerprint)

        for policy in [ CommitPolicy.RejectBlockingObligations; CommitPolicy.AllowBlockingObligations ] do
            match SemanticWorkspace.commitReviewedWithPolicy workspace substituted reviewed.Receipt policy with
            | WorkspaceCommitResult.Rejected [ diagnostic ] when diagnostic.Code = "PreviewReceiptRequestMismatch" -> ()
            | result -> failwithf "Expected ordered request-mismatch rejection, got %A" result

        Assert.Equal(fixture.Snapshot, SemanticWorkspace.currentSnapshot workspace)
        Assert.Empty(SemanticWorkspace.previousSnapshots workspace)

        for originalRequest, ownPreview in [ request, reviewed; substituted, substitutedPreview ] do
            match SemanticWorkspace.commitReviewed workspace originalRequest ownPreview.Receipt with
            | WorkspaceCommitResult.Committed(nextWorkspace, publication) ->
                Assert.Equal(ownPreview.Receipt, publication.Receipt)
                Assert.Equal(ownPreview.Simulation.ProposedSnapshot, SemanticWorkspace.currentSnapshot nextWorkspace)
            | WorkspaceCommitResult.Rejected diagnostics ->
                failwithf "An unchanged reviewed request was rejected: %A" diagnostics

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
        let changedBase =
            { fixture.Snapshot with
                Functions =
                    fixture.Snapshot.Functions
                    |> List.map (fun functionEntity ->
                        if functionEntity.Id = fixture.ReportFunctionId then
                            { functionEntity with DisplayName = "report_changed" }
                        else
                            functionEntity) }
        let changedWorkspace = SemanticWorkspace.create changedBase |> unwrap "changed workspace"

        match SemanticWorkspace.commitReviewed changedWorkspace request reviewed.Receipt with
        | WorkspaceCommitResult.Rejected [ diagnostic ] when diagnostic.Code = "PreviewReceiptBaseMismatch" -> ()
        | result -> failwithf "Expected preview base-fingerprint rejection, got %A" result

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``reviewed commit rejects a mismatched proposed fingerprint`` semanticMismatch =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let request = renameRequest fixture "transaction-receipt-result" "snapshot-receipt-result" "validate_profile"
        let reviewed = preview workspace request
        let otherSemantic = Snapshot.empty fixture.Snapshot.Id |> SemanticFingerprint.compute |> unwrap "other semantic fingerprint"
        let mismatched =
            TransactionPreviewReceipt.create
                (Some request)
                request.Id
                request.BaseSnapshotId
                reviewed.Receipt.BaseSnapshotFingerprint
                request.ProposedSnapshotId
                (if semanticMismatch then reviewed.Receipt.ProposedSnapshotFingerprint else reviewed.Receipt.BaseSnapshotFingerprint)
                (if semanticMismatch then otherSemantic else reviewed.Receipt.ProposedSemanticFingerprint)

        match SemanticWorkspace.commitReviewed workspace request mismatched with
        | WorkspaceCommitResult.Rejected [ diagnostic ] when diagnostic.Code = "PreviewReceiptResultMismatch" -> ()
        | result -> failwithf "Expected preview result-fingerprint rejection, got %A" result

    [<Fact>]
    let ``reviewed commit preserves result mismatch diagnostics for a changed request`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let request = renameRequest fixture "transaction-receipt-result-intent" "snapshot-receipt-result-intent" "validate_profile"
        let reviewed = preview workspace request
        let changed = renameRequest fixture "transaction-receipt-result-intent" "snapshot-receipt-result-intent" "validate_other"

        match SemanticWorkspace.commitReviewed workspace changed reviewed.Receipt with
        | WorkspaceCommitResult.Rejected [ diagnostic ] when diagnostic.Code = "PreviewReceiptResultMismatch" -> ()
        | result -> failwithf "Expected existing result-mismatch rejection before intent comparison, got %A" result

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``reviewed commit preserves stale and already published diagnostics`` alreadyPublished =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let request = renameRequest fixture "transaction-receipt-stale" "snapshot-receipt-stale" "validate_profile"
        let reviewed = preview workspace request
        let advancingRequest =
            if alreadyPublished then request
            else renameRequest fixture "transaction-receipt-advance" "snapshot-receipt-advance" "validate_advanced"
        let advanced =
            match SemanticWorkspace.commit workspace advancingRequest with
            | WorkspaceCommitResult.Committed(nextWorkspace, _) -> nextWorkspace
            | WorkspaceCommitResult.Rejected diagnostics -> failwithf "Unexpected advancing commit rejection: %A" diagnostics
        let expected = if alreadyPublished then "SnapshotAlreadyPublished" else "StaleBaseSnapshot"
        let substituted = { request with Operations = request.Operations @ request.Operations }

        for attempted in [ request; substituted ] do
            match SemanticWorkspace.commitReviewed advanced attempted reviewed.Receipt with
            | WorkspaceCommitResult.Rejected [ diagnostic ] when diagnostic.Code = expected -> ()
            | result -> failwithf "Expected %s before intent comparison, got %A" expected result
        Assert.Equal(advancingRequest.ProposedSnapshotId, (SemanticWorkspace.currentSnapshot advanced).Id)

    [<Fact>]
    let ``snapshot only structural receipt cannot authorize an ordinary transaction`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let request = renameRequest fixture "transaction-receipt-unbound" "snapshot-receipt-unbound" "validate_profile"
        let reviewed = preview workspace request
        let structuralReceipt =
            StructuralTransactionInternals.createReceipt request.Id fixture.Snapshot reviewed.Simulation.ProposedSnapshot
            |> unwrap "structural receipt"

        Assert.Equal(reviewed.Receipt.BaseSnapshotFingerprint, structuralReceipt.BaseSnapshotFingerprint)
        Assert.Equal(reviewed.Receipt.ProposedSnapshotFingerprint, structuralReceipt.ProposedSnapshotFingerprint)
        Assert.Equal(reviewed.Receipt.ProposedSemanticFingerprint, structuralReceipt.ProposedSemanticFingerprint)
        match SemanticWorkspace.commitReviewed workspace request structuralReceipt with
        | WorkspaceCommitResult.Rejected [ diagnostic ] when diagnostic.Code = "PreviewReceiptRequestMismatch" -> ()
        | result -> failwithf "Expected rejection of a receipt without ordered intent, got %A" result

        match SemanticWorkspace.commitReviewed workspace request reviewed.Receipt with
        | WorkspaceCommitResult.Committed _ -> ()
        | result -> failwithf "The receipt bound to the original request should commit, got %A" result

    [<Fact>]
    let ``receipt exposes the same read only facts without public construction authority`` () =
        let receiptType = typeof<TransactionPreviewReceipt>
        Assert.Empty(receiptType.GetConstructors())
        let properties = receiptType.GetProperties()
        let expected =
            [ "TransactionId"; "BaseSnapshotId"; "BaseSnapshotFingerprint"
              "ProposedSnapshotId"; "ProposedSnapshotFingerprint"; "ProposedSemanticFingerprint" ]
            |> Set.ofList
        let actual = properties |> Array.map (fun property -> property.Name) |> Set.ofArray
        Assert.Equal<Set<string>>(expected, actual)
        for property in properties do
            Assert.Null(property.GetSetMethod())

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
                      NewParameterType = TypeRef.Named(fixture.PolicyTypeId, [])
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
