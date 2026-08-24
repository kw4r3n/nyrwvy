namespace Nayamnyam.Core.Tests

open Nayamnyam.Core
open Xunit

module FoundationTests =
    [<Fact>]
    let ``stable identity is independent from display names`` () =
        let fixture = TestGraph.create ()
        let before = fixture.Snapshot.Functions |> List.find (fun item -> item.Id = fixture.ValidateFunctionId)
        let after = { before with DisplayName = "validate_profile" }

        Assert.Equal(before.Id, after.Id)

        if before.DisplayName = after.DisplayName then
            failwith "Changing a display name unexpectedly changed the identity check."

    [<Fact>]
    let ``direct calls exist only as semantic graph nodes`` () =
        let fixture = TestGraph.create ()
        let run = fixture.Snapshot.Functions |> List.find (fun item -> item.Id = fixture.RunFunctionId)
        let calls =
            run.Nodes
            |> List.choose (fun node ->
                match node.Kind with
                | SemanticNodeKind.DirectCall(targetFunctionId, arguments) -> Some(node.Id, targetFunctionId, arguments)
                | SemanticNodeKind.ParameterReference _ -> None)

        let callNodeId, targetFunctionId, arguments = List.exactlyOne calls
        Assert.Equal(fixture.RunCallNodeId, callNodeId)
        Assert.Equal(fixture.ValidateFunctionId, targetFunctionId)
        Assert.Single(arguments) |> ignore

    [<Fact>]
    let ``empty snapshot does not infer semantic facts`` () =
        let id = StableId.createForTesting<SnapshotTag> "snapshot-fixture-1"
        let snapshot = Snapshot.empty id

        Assert.Empty(snapshot.Types)
        Assert.Empty(snapshot.Functions)
        Assert.Empty(snapshot.Obligations)

        if Option.isSome snapshot.ParentId then
            failwith "An empty snapshot unexpectedly inferred a parent snapshot."
