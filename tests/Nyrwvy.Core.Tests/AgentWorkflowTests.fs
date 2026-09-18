namespace Nyrwvy.Core.Tests

open System
open Nyrwvy.Core
open Xunit

module AgentWorkflowTests =
    let private createWorkspace (fixture: MilestoneZeroFixture) =
        match AgentWorkspace.create fixture.Snapshot with
        | Ok workspace -> workspace
        | Error issues -> failwithf "Unexpected agent workspace creation failure: %A" issues

    let private simulate workspace operations =
        match AgentWorkflow.simulate workspace operations with
        | Ok proposal -> proposal
        | Error error -> failwithf "Unexpected agent simulation failure: %A" error

    let private assertUuidV7Payload label payload =
        let mutable parsed = Guid.Empty
        if not (Guid.TryParseExact(payload, "D", &parsed)) || payload.Length <> 36 || payload[14] <> '7' then
            failwithf "%s was not allocated as UUIDv7: %s" label payload

    let private renameIntent (fixture: MilestoneZeroFixture) name =
        AgentOperationIntent.RenameDisplayName(
            AgentRenameTarget.Function fixture.ValidateFunctionId,
            name
        )

    [<Fact>]
    let ``agent queries read the current semantic workspace without exposing raw mutation`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture

        match AgentWorkspace.focusFunction workspace fixture.ValidateFunctionId with
        | Ok focus when focus.Function.Id = fixture.ValidateFunctionId ->
            if focus.Function.DisplayName <> "validate_user" then
                failwithf "Unexpected function display name: %s" focus.Function.DisplayName
        | result -> failwithf "Unexpected agent focus result: %A" result

        if AgentWorkspace.currentSnapshotId workspace <> fixture.Snapshot.Id then
            failwith "Agent workspace current snapshot identity changed unexpectedly."

    [<Fact>]
    let ``agent simulation allocates transaction and proposed snapshot identities and exposes intent plus resulting diff`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let intent = renameIntent fixture "validate_profile"
        let proposal = simulate workspace [ intent ]
        let review = AgentChangeProposal.review proposal

        assertUuidV7Payload "transaction ID" (StableId.value (AgentChangeProposal.transactionId proposal))
        assertUuidV7Payload "proposed snapshot ID" (StableId.value (AgentChangeProposal.proposedSnapshotId proposal))

        if review.RequestedOperations <> [ intent ] then
            failwithf "Requested operation intent changed in review: %A" review.RequestedOperations

        match review.ResultingChange.Diff.Renames with
        | [ FunctionRenamed(id, "validate_user", "validate_profile") ] when id = fixture.ValidateFunctionId -> ()
        | diff -> failwithf "Unexpected resulting rename diff: %A" diff

        if review.Receipt.TransactionId <> AgentChangeProposal.transactionId proposal then
            failwith "Review receipt is not bound to the opaque proposal transaction."

    [<Fact>]
    let ``agent add-parameter intent allocates new parameter and obligation identities`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let intent =
            AgentOperationIntent.AddFunctionParameter
                { TargetFunctionId = fixture.ValidateFunctionId
                  NewParameterDisplayName = "policy"
                  NewParameterType = TypeRef.Named fixture.PolicyTypeId
                  CallSitePlans = [ AgentCallPlan.LeaveMissing fixture.MainValidateCallNodeId ] }

        let proposal = simulate workspace [ intent ]
        let review = AgentChangeProposal.review proposal

        match review.ResultingChange.Diff.ParametersAdded, review.ResultingChange.Diff.ObligationsCreated with
        | [ parameter ], [ obligation ] ->
            assertUuidV7Payload "parameter ID" (StableId.value parameter.AddedParameterId)
            assertUuidV7Payload "obligation ID" (StableId.value obligation.ObligationId)
            if parameter.OwnerFunctionId <> fixture.ValidateFunctionId then
                failwith "Agent facade allocated the parameter for the wrong function."
            if obligation.CallNodeId <> fixture.MainValidateCallNodeId then
                failwith "Agent facade allocated the obligation for the wrong call site."
        | changes -> failwithf "Unexpected generated parameter/obligation changes: %A" changes

        match review.Readiness with
        | TransactionReadiness.Incomplete [ obligationId ] ->
            if obligationId <> review.ResultingChange.Diff.ObligationsCreated.Head.ObligationId then
                failwith "Readiness obligation does not match the resulting semantic diff."
        | readiness -> failwithf "Expected incomplete review, got %A" readiness

    [<Fact>]
    let ``agent commit publishes only the reviewed opaque proposal`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let proposal = simulate workspace [ renameIntent fixture "validate_profile" ]
        let review = AgentChangeProposal.review proposal

        match AgentWorkflow.commit workspace proposal with
        | AgentCommitResult.Committed(nextWorkspace, publication) ->
            if AgentWorkspace.currentSnapshotId nextWorkspace <> AgentChangeProposal.proposedSnapshotId proposal then
                failwith "Agent commit did not publish the reviewed proposed snapshot."
            if publication.Receipt <> review.Receipt then
                failwith "Published transaction receipt differs from the reviewed proposal receipt."
            match AgentWorkspace.getFunction nextWorkspace fixture.ValidateFunctionId with
            | Ok functionEntity when functionEntity.DisplayName = "validate_profile" -> ()
            | result -> failwithf "Committed agent workspace does not contain the reviewed rename: %A" result
        | AgentCommitResult.Rejected diagnostics ->
            failwithf "Reviewed agent commit was unexpectedly rejected: %A" diagnostics

    [<Fact>]
    let ``agent proposal becomes stale after another proposal advances the workspace`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let first = simulate workspace [ renameIntent fixture "validate_profile" ]
        let stale = simulate workspace [ renameIntent fixture "validate_again" ]

        let advanced =
            match AgentWorkflow.commit workspace first with
            | AgentCommitResult.Committed(nextWorkspace, _) -> nextWorkspace
            | AgentCommitResult.Rejected diagnostics -> failwithf "Unexpected first commit rejection: %A" diagnostics

        match AgentWorkflow.commit advanced stale with
        | AgentCommitResult.Rejected [ diagnostic ] when diagnostic.Code = "StaleBaseSnapshot" -> ()
        | result -> failwithf "Expected stale reviewed proposal rejection, got %A" result

    [<Fact>]
    let ``agent default commit rejects incomplete proposal while explicit policy permits it`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let intent =
            AgentOperationIntent.AddFunctionParameter
                { TargetFunctionId = fixture.ValidateFunctionId
                  NewParameterDisplayName = "policy"
                  NewParameterType = TypeRef.Named fixture.PolicyTypeId
                  CallSitePlans = [ AgentCallPlan.LeaveMissing fixture.MainValidateCallNodeId ] }
        let proposal = simulate workspace [ intent ]

        match AgentWorkflow.commit workspace proposal with
        | AgentCommitResult.Rejected [ diagnostic ] when diagnostic.Code = "BlockingObligations" -> ()
        | result -> failwithf "Expected conservative agent commit rejection, got %A" result

        match AgentWorkflow.commitWithPolicy workspace proposal CommitPolicy.AllowBlockingObligations with
        | AgentCommitResult.Committed(nextWorkspace, publication) ->
            match publication.Readiness with
            | TransactionReadiness.Incomplete [ _ ] -> ()
            | readiness -> failwithf "Explicit incomplete publication has wrong readiness: %A" readiness
            if AgentWorkspace.currentSnapshotId nextWorkspace <> AgentChangeProposal.proposedSnapshotId proposal then
                failwith "Explicit incomplete agent commit published the wrong snapshot."
        | AgentCommitResult.Rejected diagnostics ->
            failwithf "Explicit incomplete agent commit was unexpectedly rejected: %A" diagnostics

    [<Fact>]
    let ``agent reject preserves the exact current semantic state`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let beforeSnapshot = AgentWorkspace.currentSnapshotId workspace
        let beforeFingerprint = AgentWorkspace.currentSnapshotFingerprint workspace
        let proposal = simulate workspace [ renameIntent fixture "validate_profile" ]

        let retained, receipt = AgentWorkflow.reject workspace proposal
        if AgentWorkspace.currentSnapshotId retained <> beforeSnapshot then
            failwith "Agent reject changed current snapshot identity."
        if AgentWorkspace.currentSnapshotFingerprint retained <> beforeFingerprint then
            failwith "Agent reject changed current snapshot content."
        if receipt.TransactionId <> AgentChangeProposal.transactionId proposal then
            failwith "Agent rejection receipt identifies the wrong proposal."

    [<Fact>]
    let ``agent can resolve an automatically allocated obligation without allocating replacement identities`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture
        let addIntent =
            AgentOperationIntent.AddFunctionParameter
                { TargetFunctionId = fixture.ValidateFunctionId
                  NewParameterDisplayName = "policy"
                  NewParameterType = TypeRef.Named fixture.PolicyTypeId
                  CallSitePlans = [ AgentCallPlan.LeaveMissing fixture.MainValidateCallNodeId ] }
        let addProposal = simulate workspace [ addIntent ]
        let obligationId =
            match (AgentChangeProposal.review addProposal).ResultingChange.Diff.ObligationsCreated with
            | [ obligation ] -> obligation.ObligationId
            | changes -> failwithf "Expected one automatically allocated obligation, got %A" changes

        let incompleteWorkspace =
            match AgentWorkflow.commitWithPolicy workspace addProposal CommitPolicy.AllowBlockingObligations with
            | AgentCommitResult.Committed(nextWorkspace, _) -> nextWorkspace
            | AgentCommitResult.Rejected diagnostics -> failwithf "Unexpected incomplete publication rejection: %A" diagnostics

        let resolveIntent =
            AgentOperationIntent.ResolveMissingArgument
                { TargetObligationId = obligationId
                  ResolutionValueNodeId = fixture.MainPolicyNodeId }
        let resolveProposal = simulate incompleteWorkspace [ resolveIntent ]

        match AgentWorkflow.commit incompleteWorkspace resolveProposal with
        | AgentCommitResult.Committed(resolvedWorkspace, publication) ->
            if publication.Readiness <> TransactionReadiness.SemanticallyReady then
                failwithf "Resolved agent workspace is not semantically ready: %A" publication.Readiness
            match AgentWorkspace.getOpenObligations resolvedWorkspace fixture.MainFunctionId with
            | Ok [] -> ()
            | result -> failwithf "Resolved agent workspace still exposes open obligations: %A" result
        | AgentCommitResult.Rejected diagnostics ->
            failwithf "Agent obligation resolution was unexpectedly rejected: %A" diagnostics
