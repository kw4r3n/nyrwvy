namespace Nyrwvy.Core.Tests

open System.Text.Json
open Nyrwvy.Core
open Xunit

module TypedSemanticIrCodecTests =
    let private project snapshot =
        match TypedSemanticIr.project snapshot with
        | Ok value -> value
        | Error error -> failwithf "Unexpected Typed Semantic IR error: %A" error

    [<Fact>]
    let ``Typed Semantic IR serialization is byte-for-byte canonical`` () =
        let fixture = MilestoneZeroGraph.create ()
        let reorderFunction (functionEntity: SemanticFunction) =
            { functionEntity with
                Body = { functionEntity.Body with Nodes = List.rev functionEntity.Body.Nodes } }

        let reordered =
            { fixture.Snapshot with
                Types = List.rev fixture.Snapshot.Types
                Effects = List.rev fixture.Snapshot.Effects
                Capabilities = List.rev fixture.Snapshot.Capabilities
                Functions = fixture.Snapshot.Functions |> List.rev |> List.map reorderFunction }

        let expected = fixture.Snapshot |> project |> TypedSemanticIrCodec.serialize
        let actual = reordered |> project |> TypedSemanticIrCodec.serialize
        Assert.Equal(expected, actual)

    [<Fact>]
    let ``Typed Semantic IR serialization contains execution semantics but no editing presentation metadata`` () =
        let fixture = MilestoneZeroGraph.create ()
        let encoded = fixture.Snapshot |> project |> TypedSemanticIrCodec.serialize

        use document = JsonDocument.Parse(encoded)
        let root = document.RootElement
        // Graphs without Abort retain their previous canonical IR schema.
        Assert.Equal(7, root.GetProperty("schemaVersion").GetInt32())
        Assert.Equal(StableId.value fixture.Snapshot.Id, root.GetProperty("sourceSnapshotId").GetString())

        Assert.DoesNotContain("displayName", encoded)
        Assert.DoesNotContain("obligations", encoded)
        Assert.DoesNotContain("originTransaction", encoded)
        Assert.Contains("originNodeId", encoded)
        Assert.Contains("requiredEffects", encoded)
        Assert.Contains("capabilityInvoke", encoded)
