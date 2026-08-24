namespace Nayamnyam.Core.Tests

open Nayamnyam.Core
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
                Functions =
                    fixture.Snapshot.Functions
                    |> List.rev
                    |> List.map (fun functionEntity ->
                        { functionEntity with Nodes = List.rev functionEntity.Nodes }) }

        let canonical = SnapshotCodec.serialize original |> unwrap
        let restored = SnapshotCodec.deserialize canonical |> unwrap
        let reserialized = SnapshotCodec.serialize restored |> unwrap

        Assert.Equal(canonical, reserialized)
        Assert.Equal(original.Id, restored.Id)
        Assert.Equal(original.ParentId, restored.ParentId)
        Assert.Equal(original.Obligations, restored.Obligations)

        let restoredRun = restored.Functions |> List.find (fun item -> item.Id = fixture.RunFunctionId)
        Assert.Equal(fixture.RunCallNodeId, restoredRun.ResultNodeId)
        Assert.Equal(3, restoredRun.Nodes.Length)

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
                    { FunctionId = fixture.ValidateFunctionId
                      ParameterId = parameterId
                      DisplayName = "policy"
                      TypeId = fixture.PolicyTypeId
                      CallPlans = [ LeaveMissing(fixture.RunCallNodeId, obligationId) ] } ] }

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
        let json = """{"schemaVersion":2,"snapshotId":"snapshot-a","snapshotId":"snapshot-b","types":[],"functions":[],"obligations":[]}"""
        match SnapshotCodec.deserialize json with
        | Error(SnapshotCodecError.InvalidShape message) -> Assert.Contains("duplicate", message)
        | result -> failwithf "Expected duplicate-property InvalidShape, got %A" result

    [<Fact>]
    let ``snapshot codec rejects obsolete schema versions`` () =
        let json = """{"schemaVersion":1,"snapshotId":"snapshot","types":[],"functions":[],"obligations":[]}"""
        match SnapshotCodec.deserialize json with
        | Error(SnapshotCodecError.UnsupportedSchemaVersion 1) -> ()
        | result -> failwithf "Expected UnsupportedSchemaVersion, got %A" result
