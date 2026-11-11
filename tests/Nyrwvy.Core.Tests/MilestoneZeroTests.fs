namespace Nyrwvy.Core.Tests

open Nyrwvy.Core
open Xunit

module MilestoneZeroTests =
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
        let proposed =
            { fixture.Snapshot with
                Id = StableId.createForTesting<SnapshotTag> "snapshot-m0-renamed"
                ParentId = Some fixture.Snapshot.Id
                Functions =
                    fixture.Snapshot.Functions |> List.map (fun functionEntity ->
                        if functionEntity.Id = fixture.ValidateFunctionId then
                            { functionEntity with DisplayName = "validate_profile" }
                        else functionEntity) }
        let renamed =
            proposed.Functions
            |> List.find (fun functionEntity -> functionEntity.Id = fixture.ValidateFunctionId)
        let main =
            proposed.Functions
            |> List.find (fun functionEntity -> functionEntity.Id = fixture.MainFunctionId)
        let call =
            ValueGraph.collectNodes main.Body
            |> List.find (fun node -> node.Id = fixture.MainValidateCallNodeId)

        Assert.Equal("validate_profile", renamed.DisplayName)
        Assert.Equal(fixture.ValidateFunctionId, renamed.Id)
        match call.Kind with
        | SemanticNodeKind.DirectCall(targetFunctionId, _) -> Assert.Equal(fixture.ValidateFunctionId, targetFunctionId)
        | _ -> failwith "The validation call changed semantic node kind during rename."
        requireReadiness SemanticReadiness.Complete proposed

    [<Fact>]
    let ``signature evolution creates and resolves an explicit MissingArgument on the real graph`` () =
        let fixture = MilestoneZeroGraph.create ()
        let parameterId = StableId.createForTesting<ParameterTag> "parameter-validate-policy"
        let obligationId = StableId.createForTesting<ObligationTag> "obligation-main-policy"
        let incomplete =
            TestSnapshotEdits.addParameterWithOpenObligation fixture.Snapshot
                (StableId.createForTesting<SnapshotTag> "snapshot-m0-incomplete")
                (StableId.createForTesting<TransactionTag> "transaction-m0-add-policy")
                fixture.ValidateFunctionId parameterId "policy"
                (TypeRef.Named(fixture.PolicyTypeId, [])) fixture.MainValidateCallNodeId obligationId
        requireReadiness (SemanticReadiness.Incomplete [ obligationId ]) incomplete

        let resolved =
            TestSnapshotEdits.resolveOpenObligation incomplete
                (StableId.createForTesting<SnapshotTag> "snapshot-m0-complete")
                (StableId.createForTesting<TransactionTag> "transaction-m0-resolve-policy")
                obligationId fixture.MainPolicyNodeId
        requireReadiness SemanticReadiness.Complete resolved

        let main =
            resolved.Functions
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

        let obligation = List.exactlyOne resolved.Obligations
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
            TestSnapshotEdits.addParameterWithOpenObligation fixture.Snapshot
                (StableId.createForTesting<SnapshotTag> "snapshot-m0-obligation-open")
                (StableId.createForTesting<TransactionTag> "transaction-m0-obligation-add")
                fixture.ValidateFunctionId parameterId "policy"
                (TypeRef.Named(fixture.PolicyTypeId, [])) fixture.MainValidateCallNodeId obligationId
        let resolved =
            TestSnapshotEdits.resolveOpenObligation incomplete
                (StableId.createForTesting<SnapshotTag> "snapshot-m0-obligation-resolved")
                (StableId.createForTesting<TransactionTag> "transaction-m0-obligation-resolve")
                obligationId fixture.MainPolicyNodeId
        fixture, incomplete, resolved

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
        // The persisted graph that deleting main would leave behind: its body's call node is
        // gone, so the obligation that targets it no longer agrees with any call.
        let deleted =
            { incomplete with
                Functions = incomplete.Functions |> List.filter (fun fn -> fn.Id <> fixture.MainFunctionId) }
        Assert.NotEmpty(SnapshotValidator.validate deleted)
