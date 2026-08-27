namespace Nayamnyam.Core.Tests

open Nayamnyam.Core
open Xunit

module MilestoneZeroTests =
    let private unwrapSimulation = function
        | Proposed simulation -> simulation
        | Rejected diagnostics -> failwithf "Unexpected transaction rejection: %A" diagnostics

    let private requireReadiness expected snapshot =
        match SnapshotReadiness.evaluate snapshot with
        | Error issues -> failwithf "Unexpected validation failure: %A" issues
        | Ok actual when actual = expected -> ()
        | Ok actual -> failwithf "Expected readiness %A, got %A" expected actual

    [<Fact>]
    let ``representative Milestone 0 graph is semantically complete`` () =
        let fixture = MilestoneZeroGraph.create ()
        Assert.Empty(SnapshotValidator.validate fixture.Snapshot)
        requireReadiness SemanticReadiness.Complete fixture.Snapshot

    [<Fact>]
    let ``representative Milestone 0 graph round-trips through strict JSON canonically`` () =
        let fixture = MilestoneZeroGraph.create ()
        let encoded =
            match SnapshotCodec.serialize fixture.Snapshot with
            | Ok value -> value
            | Error error -> failwithf "Unexpected JSON serialization failure: %A" error
        let decoded =
            match SnapshotCodec.deserialize encoded with
            | Ok value -> value
            | Error error -> failwithf "Unexpected JSON deserialization failure: %A" error
        let reencoded =
            match SnapshotCodec.serialize decoded with
            | Ok value -> value
            | Error error -> failwithf "Unexpected JSON reserialization failure: %A" error

        Assert.Equal(encoded, reencoded)
        Assert.Equal(fixture.Snapshot.Id, decoded.Id)

    [<Fact>]
    let ``rename preserves validation function identity and direct-call target`` () =
        let fixture = MilestoneZeroGraph.create ()
        let request: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-m0-rename"
              BaseSnapshotId = fixture.Snapshot.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-m0-renamed"
              Operations =
                [ RenameDisplayName(
                    RenameTarget.FunctionTarget fixture.ValidateFunctionId,
                    "validate_profile"
                  ) ] }

        let simulation = Transaction.simulate fixture.Snapshot request |> unwrapSimulation
        let renamed =
            simulation.ProposedSnapshot.Functions
            |> List.find (fun functionEntity -> functionEntity.Id = fixture.ValidateFunctionId)
        let main =
            simulation.ProposedSnapshot.Functions
            |> List.find (fun functionEntity -> functionEntity.Id = fixture.MainFunctionId)
        let call =
            ValueGraph.collectNodes main.Body
            |> List.find (fun node -> node.Id = fixture.MainValidateCallNodeId)

        Assert.Equal("validate_profile", renamed.DisplayName)
        Assert.Equal(fixture.ValidateFunctionId, renamed.Id)
        match call.Kind with
        | SemanticNodeKind.DirectCall(targetFunctionId, _) -> Assert.Equal(fixture.ValidateFunctionId, targetFunctionId)
        | _ -> failwith "The validation call changed semantic node kind during rename."
        Assert.Single(simulation.Diff.Renames) |> ignore

    [<Fact>]
    let ``signature evolution creates and resolves an explicit MissingArgument on the real graph`` () =
        let fixture = MilestoneZeroGraph.create ()
        let parameterId = StableId.createForTesting<ParameterTag> "parameter-validate-policy"
        let obligationId = StableId.createForTesting<ObligationTag> "obligation-main-policy"
        let addRequest: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-m0-add-policy"
              BaseSnapshotId = fixture.Snapshot.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-m0-incomplete"
              Operations =
                [ AddFunctionParameter
                    { TargetFunctionId = fixture.ValidateFunctionId
                      NewParameterId = parameterId
                      NewParameterDisplayName = "policy"
                      NewParameterType = TypeRef.Named fixture.PolicyTypeId
                      CallSitePlans = [ LeaveMissing(fixture.MainValidateCallNodeId, obligationId) ] } ] }

        let incomplete = Transaction.simulate fixture.Snapshot addRequest |> unwrapSimulation
        requireReadiness (SemanticReadiness.Incomplete [ obligationId ]) incomplete.ProposedSnapshot
        Assert.Single(incomplete.Diff.ObligationsCreated) |> ignore

        let resolveRequest: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-m0-resolve-policy"
              BaseSnapshotId = incomplete.ProposedSnapshot.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-m0-complete"
              Operations =
                [ ResolveMissingArgument
                    { TargetObligationId = obligationId
                      ResolutionValueNodeId = fixture.MainPolicyNodeId } ] }

        let resolved = Transaction.simulate incomplete.ProposedSnapshot resolveRequest |> unwrapSimulation
        requireReadiness SemanticReadiness.Complete resolved.ProposedSnapshot
        Assert.Single(resolved.Diff.ObligationsResolved) |> ignore

        let main =
            resolved.ProposedSnapshot.Functions
            |> List.find (fun functionEntity -> functionEntity.Id = fixture.MainFunctionId)
        let call =
            ValueGraph.collectNodes main.Body
            |> List.find (fun node -> node.Id = fixture.MainValidateCallNodeId)

        match call.Kind with
        | SemanticNodeKind.DirectCall(_, arguments) ->
            let binding = arguments |> List.find (fun item -> item.TargetParameterId = parameterId)
            Assert.Equal(fixture.MainPolicyNodeId, binding.ValueNodeId)
        | _ -> failwith "The obligated call changed semantic node kind during resolution."
