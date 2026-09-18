namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Xunit

module TransactionTests =
    let private unwrap = function
        | Proposed simulation -> simulation
        | Rejected diagnostics -> failwithf "Unexpected rejection: %A" diagnostics

    let private requireReadiness expected snapshot =
        match SnapshotReadiness.evaluate snapshot with
        | Error issues -> failwithf "Unexpected validation failure: %A" issues
        | Ok actual when actual = expected -> ()
        | Ok actual -> failwithf "Expected readiness %A, got %A" expected actual

    [<Fact>]
    let ``rename preserves the complete graph except display metadata`` () =
        let fixture = TestGraph.create ()
        let proposedId = StableId.createForTesting<SnapshotTag> "snapshot-renamed"
        let request: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-rename"
              BaseSnapshotId = fixture.Snapshot.Id
              ProposedSnapshotId = proposedId
              Operations =
                [ RenameDisplayName(RenameTarget.FunctionTarget fixture.ValidateFunctionId, "validate_profile") ] }

        let simulation = Transaction.simulate fixture.Snapshot request |> unwrap
        let expectedFunctions =
            fixture.Snapshot.Functions
            |> List.map (fun functionEntity ->
                if functionEntity.Id = fixture.ValidateFunctionId then
                    { functionEntity with DisplayName = "validate_profile" }
                else functionEntity)
        let expected =
            { fixture.Snapshot with
                Id = proposedId
                ParentId = Some fixture.Snapshot.Id
                Functions = expectedFunctions }

        Assert.Equal(expected, simulation.ProposedSnapshot)
        Assert.Single(simulation.Diff.Renames) |> ignore

    [<Fact>]
    let ``add parameter can create an explicit blocking missing-argument obligation`` () =
        let fixture = TestGraph.create ()
        let parameterId = StableId.createForTesting<ParameterTag> "parameter-validate-policy"
        let obligationId = StableId.createForTesting<ObligationTag> "obligation-policy"
        let request: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-add-policy"
              BaseSnapshotId = fixture.Snapshot.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-incomplete"
              Operations =
                [ AddFunctionParameter
                    { TargetFunctionId = fixture.ValidateFunctionId
                      NewParameterId = parameterId
                      NewParameterDisplayName = "policy"
                      NewParameterType = TypeRef.Named fixture.PolicyTypeId
                      CallSitePlans = [ LeaveMissing(fixture.RunCallNodeId, obligationId) ] } ] }

        let simulation = Transaction.simulate fixture.Snapshot request |> unwrap
        let obligation = List.exactlyOne simulation.ProposedSnapshot.Obligations
        let target = simulation.ProposedSnapshot.Functions |> List.find (fun item -> item.Id = fixture.ValidateFunctionId)

        Assert.Equal(parameterId, (List.last target.Parameters).Id)
        Assert.Equal(obligationId, obligation.Id)
        Assert.Single(simulation.Diff.ParametersAdded) |> ignore
        Assert.Single(simulation.Diff.ObligationsCreated) |> ignore
        requireReadiness (SemanticReadiness.Incomplete [ obligationId ]) simulation.ProposedSnapshot

    [<Fact>]
    let ``resolving missing argument updates the same authoritative call node`` () =
        let fixture = TestGraph.create ()
        let parameterId = StableId.createForTesting<ParameterTag> "parameter-validate-policy"
        let obligationId = StableId.createForTesting<ObligationTag> "obligation-policy"
        let addRequest: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-add-policy"
              BaseSnapshotId = fixture.Snapshot.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-incomplete"
              Operations =
                [ AddFunctionParameter
                    { TargetFunctionId = fixture.ValidateFunctionId
                      NewParameterId = parameterId
                      NewParameterDisplayName = "policy"
                      NewParameterType = TypeRef.Named fixture.PolicyTypeId
                      CallSitePlans = [ LeaveMissing(fixture.RunCallNodeId, obligationId) ] } ] }
        let incomplete = (Transaction.simulate fixture.Snapshot addRequest |> unwrap).ProposedSnapshot

        let resolveRequest: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-resolve-policy"
              BaseSnapshotId = incomplete.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-complete"
              Operations =
                [ ResolveMissingArgument
                    { TargetObligationId = obligationId
                      ResolutionValueNodeId = fixture.RunPolicyNodeId } ] }

        let simulation = Transaction.simulate incomplete resolveRequest |> unwrap
        let run = simulation.ProposedSnapshot.Functions |> List.find (fun item -> item.Id = fixture.RunFunctionId)
        let call = ValueGraph.collectNodes run.Body |> List.find (fun node -> node.Id = fixture.RunCallNodeId)

        match call.Kind with
        | SemanticNodeKind.DirectCall(_, arguments) ->
            let binding = arguments |> List.find (fun item -> item.TargetParameterId = parameterId)
            Assert.Equal(fixture.RunPolicyNodeId, binding.ValueNodeId)
        | _ -> failwith "Call node changed semantic kind during obligation resolution."

        let obligation = List.exactlyOne simulation.ProposedSnapshot.Obligations
        match obligation.Status with
        | ObligationStatus.Open -> failwith "Resolved obligation remained open."
        | ObligationStatus.Resolved resolution -> Assert.Equal(fixture.RunPolicyNodeId, resolution.ValueNodeId)

        Assert.Single(simulation.Diff.ArgumentsAttached) |> ignore
        Assert.Single(simulation.Diff.ObligationsResolved) |> ignore
        requireReadiness SemanticReadiness.Complete simulation.ProposedSnapshot

    [<Fact>]
    let ``add parameter can bind an existing caller value without an obligation`` () =
        let fixture = TestGraph.create ()
        let parameterId = StableId.createForTesting<ParameterTag> "parameter-validate-policy"
        let request: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-add-policy"
              BaseSnapshotId = fixture.Snapshot.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-complete"
              Operations =
                [ AddFunctionParameter
                    { TargetFunctionId = fixture.ValidateFunctionId
                      NewParameterId = parameterId
                      NewParameterDisplayName = "policy"
                      NewParameterType = TypeRef.Named fixture.PolicyTypeId
                      CallSitePlans = [ BindExistingValue(fixture.RunCallNodeId, fixture.RunPolicyNodeId) ] } ] }

        let simulation = Transaction.simulate fixture.Snapshot request |> unwrap
        Assert.Empty(simulation.ProposedSnapshot.Obligations)
        Assert.Single(simulation.Diff.ArgumentsAttached) |> ignore
        requireReadiness SemanticReadiness.Complete simulation.ProposedSnapshot

    [<Fact>]
    let ``add parameter refuses incomplete call-site plans`` () =
        let fixture = TestGraph.create ()
        let parameterId = StableId.createForTesting<ParameterTag> "parameter-validate-policy"
        let request: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-add-policy"
              BaseSnapshotId = fixture.Snapshot.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-invalid"
              Operations =
                [ AddFunctionParameter
                    { TargetFunctionId = fixture.ValidateFunctionId
                      NewParameterId = parameterId
                      NewParameterDisplayName = "policy"
                      NewParameterType = TypeRef.Named fixture.PolicyTypeId
                      CallSitePlans = [] } ] }

        match Transaction.simulate fixture.Snapshot request with
        | Rejected diagnostics -> Assert.Equal("CallPlanMismatch", (List.exactlyOne diagnostics).Code)
        | Proposed _ -> failwith "Incomplete call-site plans were accepted."
