namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Xunit

module MilestoneZeroGraphTests =
    let private unwrapSimulation = function
        | Proposed simulation -> simulation
        | Rejected diagnostics -> failwithf "Unexpected transaction rejection: %A" diagnostics

    let private requireReadiness expected snapshot =
        match SnapshotReadiness.evaluate snapshot with
        | Error issues -> failwithf "Unexpected validation failure: %A" issues
        | Ok actual when actual = expected -> ()
        | Ok actual -> failwithf "Expected readiness %A, got %A" expected actual

    [<Fact>]
    let ``representative milestone zero semantic program validates`` () =
        let fixture = MilestoneZeroGraph.create ()
        Assert.Empty(SnapshotValidator.validate fixture.Snapshot)
        requireReadiness SemanticReadiness.Complete fixture.Snapshot

    [<Fact>]
    let ``representative milestone zero graph round-trips canonically through reference JSON`` () =
        let fixture = MilestoneZeroGraph.create ()
        let encoded =
            match SnapshotCodec.serialize fixture.Snapshot with
            | Ok value -> value
            | Error error -> failwithf "Unexpected serialization failure: %A" error
        let restored =
            match SnapshotCodec.deserialize encoded with
            | Ok value -> value
            | Error error -> failwithf "Unexpected deserialization failure: %A" error
        let reencoded =
            match SnapshotCodec.serialize restored with
            | Ok value -> value
            | Error error -> failwithf "Unexpected reserialization failure: %A" error

        Assert.Equal(encoded, reencoded)
        Assert.Equal(fixture.Snapshot.Id, restored.Id)
        Assert.Equal(fixture.Snapshot.Types.Length, restored.Types.Length)
        Assert.Equal(fixture.Snapshot.Functions.Length, restored.Functions.Length)
        Assert.Equal(fixture.ConsoleCapabilityId, (List.exactlyOne restored.Capabilities).Id)

    [<Fact>]
    let ``milestone zero rename preserves function identity and direct-call target`` () =
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
        let validateCall =
            ValueGraph.collectNodes main.Body
            |> List.find (fun node -> node.Id = fixture.MainValidateCallNodeId)

        Assert.Equal("validate_profile", renamed.DisplayName)
        Assert.Equal(fixture.ValidateFunctionId, renamed.Id)

        match validateCall.Kind with
        | SemanticNodeKind.DirectCall(targetFunctionId, _) ->
            Assert.Equal(fixture.ValidateFunctionId, targetFunctionId)
        | _ -> failwith "The milestone-zero validate call changed semantic kind during rename."

        Assert.Single(simulation.Diff.Renames) |> ignore
        requireReadiness SemanticReadiness.Complete simulation.ProposedSnapshot

    [<Fact>]
    let ``milestone zero signature change becomes explicit obligation and resolves on the same call node`` () =
        let fixture = MilestoneZeroGraph.create ()
        let newParameterId = StableId.createForTesting<ParameterTag> "parameter-validate-policy"
        let obligationId = StableId.createForTesting<ObligationTag> "obligation-main-policy"

        let addRequest: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-m0-add-policy"
              BaseSnapshotId = fixture.Snapshot.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-m0-policy-incomplete"
              Operations =
                [ AddFunctionParameter
                    { TargetFunctionId = fixture.ValidateFunctionId
                      NewParameterId = newParameterId
                      NewParameterDisplayName = "policy"
                      NewParameterType = TypeRef.Named fixture.PolicyTypeId
                      CallSitePlans =
                        [ LeaveMissing(
                            fixture.MainValidateCallNodeId,
                            obligationId
                          ) ] } ] }

        let incomplete = Transaction.simulate fixture.Snapshot addRequest |> unwrapSimulation
        Assert.Single(incomplete.Diff.ParametersAdded) |> ignore
        Assert.Single(incomplete.Diff.ObligationsCreated) |> ignore
        requireReadiness
            (SemanticReadiness.Incomplete [ obligationId ])
            incomplete.ProposedSnapshot

        let resolveRequest: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-m0-resolve-policy"
              BaseSnapshotId = incomplete.ProposedSnapshot.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-m0-policy-complete"
              Operations =
                [ ResolveMissingArgument
                    { TargetObligationId = obligationId
                      ResolutionValueNodeId = fixture.MainPolicyNodeId } ] }

        let resolved = Transaction.simulate incomplete.ProposedSnapshot resolveRequest |> unwrapSimulation
        let main =
            resolved.ProposedSnapshot.Functions
            |> List.find (fun functionEntity -> functionEntity.Id = fixture.MainFunctionId)
        let call =
            ValueGraph.collectNodes main.Body
            |> List.find (fun node -> node.Id = fixture.MainValidateCallNodeId)

        match call.Kind with
        | SemanticNodeKind.DirectCall(targetFunctionId, arguments) ->
            Assert.Equal(fixture.ValidateFunctionId, targetFunctionId)
            let policyBinding =
                arguments
                |> List.find (fun argument -> argument.TargetParameterId = newParameterId)
            Assert.Equal(fixture.MainPolicyNodeId, policyBinding.ValueNodeId)
        | _ -> failwith "The obligated milestone-zero call changed semantic kind."

        let obligation = List.exactlyOne resolved.ProposedSnapshot.Obligations
        match obligation.Status with
        | ObligationStatus.Open -> failwith "Resolved milestone-zero obligation remained open."
        | ObligationStatus.Resolved resolution ->
            Assert.Equal(fixture.MainPolicyNodeId, resolution.ValueNodeId)

        Assert.Single(resolved.Diff.ArgumentsAttached) |> ignore
        Assert.Single(resolved.Diff.ObligationsResolved) |> ignore
        requireReadiness SemanticReadiness.Complete resolved.ProposedSnapshot
