namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
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
        Assert.Equal(fixture.Snapshot.Types.Length, decoded.Types.Length)
        Assert.Equal(fixture.Snapshot.Functions.Length, decoded.Functions.Length)
        Assert.Equal(fixture.ConsoleCapabilityId, (List.exactlyOne decoded.Capabilities).Id)

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
        requireReadiness SemanticReadiness.Complete simulation.ProposedSnapshot

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
                      NewParameterType = TypeRef.Named(fixture.PolicyTypeId, [])
                      CallSitePlans = [ LeaveMissing(fixture.MainValidateCallNodeId, obligationId) ] } ] }

        let incomplete = Transaction.simulate fixture.Snapshot addRequest |> unwrapSimulation
        requireReadiness (SemanticReadiness.Incomplete [ obligationId ]) incomplete.ProposedSnapshot
        Assert.Single(incomplete.Diff.ParametersAdded) |> ignore
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
        Assert.Single(resolved.Diff.ArgumentsAttached) |> ignore
        Assert.Single(resolved.Diff.ObligationsResolved) |> ignore

        let main =
            resolved.ProposedSnapshot.Functions
            |> List.find (fun functionEntity -> functionEntity.Id = fixture.MainFunctionId)
        let call =
            ValueGraph.collectNodes main.Body
            |> List.find (fun node -> node.Id = fixture.MainValidateCallNodeId)

        match call.Kind with
        | SemanticNodeKind.DirectCall(targetFunctionId, arguments) ->
            Assert.Equal(fixture.ValidateFunctionId, targetFunctionId)
            let binding = arguments |> List.find (fun item -> item.TargetParameterId = parameterId)
            Assert.Equal(fixture.MainPolicyNodeId, binding.ValueNodeId)
        | _ -> failwith "The obligated call changed semantic node kind during resolution."

        let obligation = List.exactlyOne resolved.ProposedSnapshot.Obligations
        match obligation.Status with
        | ObligationStatus.Open -> failwith "Resolved milestone-zero obligation remained open."
        | ObligationStatus.Resolved resolution ->
            Assert.Equal(fixture.MainPolicyNodeId, resolution.ValueNodeId)

    /// The real graph with an open MissingArgument obligation on main's validate call, and the same
    /// graph after resolving it with main's policy value.
    let private obligationSnapshots () =
        let fixture = MilestoneZeroGraph.create ()
        let parameterId = StableId.createForTesting<ParameterTag> "parameter-validate-policy"
        let obligationId = StableId.createForTesting<ObligationTag> "obligation-main-policy"
        let incomplete =
            Transaction.simulate fixture.Snapshot
                { Id = StableId.createForTesting<TransactionTag> "transaction-m0-obligation-add"
                  BaseSnapshotId = fixture.Snapshot.Id
                  ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-m0-obligation-open"
                  Operations =
                    [ AddFunctionParameter
                        { TargetFunctionId = fixture.ValidateFunctionId
                          NewParameterId = parameterId
                          NewParameterDisplayName = "policy"
                          NewParameterType = TypeRef.Named(fixture.PolicyTypeId, [])
                          CallSitePlans = [ LeaveMissing(fixture.MainValidateCallNodeId, obligationId) ] } ] }
            |> unwrapSimulation
        let resolved =
            Transaction.simulate incomplete.ProposedSnapshot
                { Id = StableId.createForTesting<TransactionTag> "transaction-m0-obligation-resolve"
                  BaseSnapshotId = incomplete.ProposedSnapshot.Id
                  ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-m0-obligation-resolved"
                  Operations =
                    [ ResolveMissingArgument
                        { TargetObligationId = obligationId
                          ResolutionValueNodeId = fixture.MainPolicyNodeId } ] }
            |> unwrapSimulation
        fixture, incomplete.ProposedSnapshot, resolved.ProposedSnapshot

    let private codes snapshot = SnapshotValidator.validate snapshot |> List.map (fun issue -> issue.Code)

    [<Fact>]
    let ``persisted obligations must agree with the call they target`` () =
        let fixture, incomplete, resolved = obligationSnapshots ()
        Assert.Empty(SnapshotValidator.validate incomplete)
        Assert.Empty(SnapshotValidator.validate resolved)
        let opened = List.exactlyOne incomplete.Obligations

        // Two open obligations for the same call and parameter.
        let duplicate =
            { incomplete with
                Obligations =
                    incomplete.Obligations
                    @ [ { opened with Id = StableId.createForTesting<ObligationTag> "obligation-main-policy-again" } ] }
        Assert.Equal<string list>([ "DuplicateOpenMissingArgumentObligation" ], codes duplicate)

        // An obligation left open although the call already binds the parameter.
        let satisfied =
            { resolved with
                Obligations = resolved.Obligations |> List.map (fun item -> { item with Status = ObligationStatus.Open }) }
        Assert.Equal<string list>([ "SatisfiedOpenObligation" ], codes satisfied)

        // A resolution whose value, and the matching binding, live in another function.
        let foreignNodeId = StableId.createForTesting<GraphNodeTag> "node-validate-input"
        let rebind (node: SemanticNode) =
            match node.Kind with
            | SemanticNodeKind.DirectCall(targetId, arguments) when node.Id = fixture.MainValidateCallNodeId ->
                let arguments =
                    arguments
                    |> List.map (fun argument ->
                        if argument.TargetParameterId = opened.ParameterId then
                            { argument with ValueNodeId = foreignNodeId }
                        else
                            argument)
                { node with Kind = SemanticNodeKind.DirectCall(targetId, arguments) }
            | _ -> node
        let foreign =
            { resolved with
                Functions = resolved.Functions |> List.map (fun fn -> { fn with Body = ValueGraph.mapNodes rebind fn.Body })
                Obligations =
                    resolved.Obligations
                    |> List.map (fun item ->
                        match item.Status with
                        | ObligationStatus.Resolved resolution ->
                            { item with Status = ObligationStatus.Resolved { resolution with ValueNodeId = foreignNodeId } }
                        | ObligationStatus.Open -> item) }
        let foreignCodes = codes foreign
        Assert.Contains("InvalidObligationResolutionValue", foreignCodes)
        Assert.DoesNotContain("InvalidObligationResolution", foreignCodes)

    [<Fact>]
    let ``deleting an unreferenced function refuses obligations on its body`` () =
        let fixture, incomplete, _ = obligationSnapshots ()
        let request: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-m0-delete-main"
              BaseSnapshotId = incomplete.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-m0-main-deleted"
              Operations = [ DeleteFunction fixture.MainFunctionId ] }
        match Transaction.simulate incomplete request with
        | Rejected [ diagnostic ] -> Assert.Equal("FunctionHasObligations", diagnostic.Code)
        | other -> failwithf "Expected FunctionHasObligations, got %A" other
