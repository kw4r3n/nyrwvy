namespace Nayamnyam.Core.Tests

open Nayamnyam.Core
open Xunit

module ChangeReviewQueryTests =
    let private proposedSnapshot simulationResult =
        match simulationResult with
        | Proposed simulation -> simulation
        | Rejected diagnostics -> failwithf "Unexpected transaction rejection: %A" diagnostics

    let private review simulation =
        match ChangeReviewQuery.project simulation with
        | Ok value -> value
        | Error error -> failwithf "Unexpected change review query error: %A" error

    [<Fact>]
    let ``rename review preserves semantic diff and focuses the stable function`` () =
        let fixture = MilestoneZeroGraph.create ()
        let request: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-review-rename"
              BaseSnapshotId = fixture.Snapshot.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-review-rename"
              Operations =
                [ SemanticOperation.RenameDisplayName(
                    RenameTarget.FunctionTarget fixture.ValidateFunctionId,
                    "validate_profile"
                  ) ] }

        let simulation = Transaction.simulate fixture.Snapshot request |> proposedSnapshot
        let view = review simulation

        if view.Readiness <> ChangeReviewReadiness.Complete then
            failwithf "Rename review unexpectedly changed readiness: %A" view.Readiness

        if view.AffectedFunctionIds <> [ fixture.ValidateFunctionId ] then
            failwithf "Unexpected rename affected functions: %A" view.AffectedFunctionIds

        match view.Diff.Renames with
        | [ FunctionRenamed(functionId, "validate_user", "validate_profile") ]
            when functionId = fixture.ValidateFunctionId -> ()
        | renames -> failwithf "Unexpected rename diff: %A" renames

        match view.FunctionFocus with
        | [ focus ] when focus.Function.Id = fixture.ValidateFunctionId && focus.Function.DisplayName = "validate_profile" -> ()
        | focus -> failwithf "Unexpected rename focus payload: %A" focus

    [<Fact>]
    let ``missing argument review links signature impact to the caller obligation`` () =
        let fixture = MilestoneZeroGraph.create ()
        let newParameterId = StableId.createForTesting<ParameterTag> "parameter-review-policy"
        let obligationId = StableId.createForTesting<ObligationTag> "obligation-review-policy"
        let request: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-review-add-parameter"
              BaseSnapshotId = fixture.Snapshot.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-review-add-parameter"
              Operations =
                [ SemanticOperation.AddFunctionParameter
                    { TargetFunctionId = fixture.ValidateFunctionId
                      NewParameterId = newParameterId
                      NewParameterDisplayName = "policy_for_review"
                      NewParameterType = TypeRef.Named fixture.PolicyTypeId
                      CallSitePlans = [ LeaveMissing(fixture.MainValidateCallNodeId, obligationId) ] } ] }

        let simulation = Transaction.simulate fixture.Snapshot request |> proposedSnapshot
        let view = review simulation

        match view.Readiness with
        | ChangeReviewReadiness.Incomplete ids when ids = [ obligationId ] -> ()
        | state -> failwithf "Expected one blocking obligation, got %A" state

        let affected = Set.ofList view.AffectedFunctionIds
        let expected = Set.ofList [ fixture.ValidateFunctionId; fixture.MainFunctionId ]
        if affected <> expected then
            failwithf "Unexpected signature-change affected functions: %A" view.AffectedFunctionIds

        let mainFocus =
            view.FunctionFocus
            |> List.find (fun focus -> focus.Function.Id = fixture.MainFunctionId)

        match mainFocus.OpenObligations with
        | [ obligation ] when obligation.ObligationId = obligationId && obligation.CallNodeId = fixture.MainValidateCallNodeId -> ()
        | obligations -> failwithf "Caller focus did not expose the blocking obligation: %A" obligations

    [<Fact>]
    let ``obligation resolution review restores semantic readiness`` () =
        let fixture = MilestoneZeroGraph.create ()
        let newParameterId = StableId.createForTesting<ParameterTag> "parameter-review-resolve-policy"
        let obligationId = StableId.createForTesting<ObligationTag> "obligation-review-resolve-policy"

        let addRequest: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-review-resolve-add"
              BaseSnapshotId = fixture.Snapshot.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-review-resolve-incomplete"
              Operations =
                [ SemanticOperation.AddFunctionParameter
                    { TargetFunctionId = fixture.ValidateFunctionId
                      NewParameterId = newParameterId
                      NewParameterDisplayName = "policy_for_resolution"
                      NewParameterType = TypeRef.Named fixture.PolicyTypeId
                      CallSitePlans = [ LeaveMissing(fixture.MainValidateCallNodeId, obligationId) ] } ] }

        let incomplete = Transaction.simulate fixture.Snapshot addRequest |> proposedSnapshot

        let resolveRequest: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-review-resolve"
              BaseSnapshotId = incomplete.ProposedSnapshot.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-review-resolved"
              Operations =
                [ SemanticOperation.ResolveMissingArgument
                    { TargetObligationId = obligationId
                      ResolutionValueNodeId = fixture.MainPolicyNodeId } ] }

        let resolved = Transaction.simulate incomplete.ProposedSnapshot resolveRequest |> proposedSnapshot
        let view = review resolved

        if view.Readiness <> ChangeReviewReadiness.Complete then
            failwithf "Resolved review did not restore semantic readiness: %A" view.Readiness

        if view.AffectedFunctionIds <> [ fixture.MainFunctionId ] then
            failwithf "Resolution should focus only the caller function, got %A" view.AffectedFunctionIds

        match view.Diff.ObligationsResolved with
        | [ item ] when item.ObligationId = obligationId && item.ValueNodeId = fixture.MainPolicyNodeId -> ()
        | items -> failwithf "Resolution diff was not preserved: %A" items
