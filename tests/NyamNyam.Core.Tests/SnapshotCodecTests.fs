namespace NyamNyam.Core.Tests

open NyamNyam.Core
open Xunit

module SnapshotCodecTests =
    let private unwrap = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected codec error: %A" error

    [<Fact>]
    let ``canonical snapshot serialization round-trips the authoritative graph`` () =
        let fixture = TestGraph.create ()
        let original =
            { fixture.Snapshot with
                Types = List.rev fixture.Snapshot.Types
                Effects = List.rev fixture.Snapshot.Effects
                Capabilities = List.rev fixture.Snapshot.Capabilities
                Functions =
                    fixture.Snapshot.Functions
                    |> List.rev
                    |> List.map (fun functionEntity ->
                        { functionEntity with
                            Body = { functionEntity.Body with Nodes = List.rev functionEntity.Body.Nodes } }) }

        let canonical = SnapshotCodec.serialize original |> unwrap
        let restored = SnapshotCodec.deserialize canonical |> unwrap
        let reserialized = SnapshotCodec.serialize restored |> unwrap

        Assert.Equal(canonical, reserialized)
        Assert.Equal(original.Id, restored.Id)
        Assert.Equal(original.ParentId, restored.ParentId)

        if original.Obligations <> restored.Obligations then
            failwith "Obligations did not round-trip exactly."

        let restoredRun = restored.Functions |> List.find (fun item -> item.Id = fixture.RunFunctionId)
        Assert.Equal(fixture.RunCallNodeId, restoredRun.Body.ResultNodeId)
        Assert.Equal(3, restoredRun.Body.Nodes.Length)

    [<Fact>]
    let ``snapshot codec preserves open missing-argument obligations`` () =
        let fixture = TestGraph.create ()
        let parameterId = StableId.createForTesting<ParameterTag> "parameter-validate-policy"
        let obligationId = StableId.createForTesting<ObligationTag> "obligation-policy"
        let request: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-add-policy"
              BaseSnapshotId = fixture.Snapshot.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-with-obligation"
              Operations =
                [ AddFunctionParameter
                    { TargetFunctionId = fixture.ValidateFunctionId
                      NewParameterId = parameterId
                      NewParameterDisplayName = "policy"
                      NewParameterType = TypeRef.Named fixture.PolicyTypeId
                      CallSitePlans = [ LeaveMissing(fixture.RunCallNodeId, obligationId) ] } ] }

        let proposed =
            match Transaction.simulate fixture.Snapshot request with
            | Proposed simulation -> simulation.ProposedSnapshot
            | Rejected diagnostics -> failwithf "Unexpected rejection: %A" diagnostics

        let canonical = SnapshotCodec.serialize proposed |> unwrap
        let restored = SnapshotCodec.deserialize canonical |> unwrap
        let obligation = List.exactlyOne restored.Obligations

        Assert.Equal(obligationId, obligation.Id)
        Assert.Equal(fixture.RunCallNodeId, obligation.CallNodeId)

        match obligation.Status with
        | ObligationStatus.Open -> ()
        | ObligationStatus.Resolved _ -> failwith "Open obligation was restored as resolved."

    [<Fact>]
    let ``snapshot codec rejects duplicate property names`` () =
        let json = """{"schemaVersion":9,"snapshotId":"snapshot-a","snapshotId":"snapshot-b","types":[],"effects":[],"capabilities":[],"functions":[],"obligations":[]}"""
        match SnapshotCodec.deserialize json with
        | Error(SnapshotCodecError.InvalidShape message) -> Assert.Contains("duplicate", message)
        | result -> failwithf "Expected duplicate-property InvalidShape, got %A" result

    [<Fact>]
    let ``snapshot codec rejects obsolete schema versions`` () =
        let json = """{"schemaVersion":7,"snapshotId":"snapshot","types":[],"effects":[],"capabilities":[],"functions":[],"obligations":[]}"""
        match SnapshotCodec.deserialize json with
        | Error(SnapshotCodecError.UnsupportedSchemaVersion 7) -> ()
        | result -> failwithf "Expected UnsupportedSchemaVersion, got %A" result
