namespace Nayamnyam.Core.Tests

open Nayamnyam.Core
open Xunit

module FoundationTests =
    [<Fact>]
    let ``stable identity is independent from display names`` () =
        let id = StableId.createForTesting<FunctionTag> "function-fixture-1"
        let before: SemanticFunction =
            { Id = id
              DisplayName = "validate_user" }
        let after = { before with DisplayName = "validate_profile" }

        Assert.Equal(before.Id, after.Id)

        if before.DisplayName = after.DisplayName then
            failwith "Changing a display name unexpectedly changed the identity check."

    [<Fact>]
    let ``snapshot construction does not infer missing semantic facts`` () =
        let id = StableId.createForTesting<SnapshotTag> "snapshot-fixture-1"
        let snapshot = Snapshot.empty id

        Assert.Empty(snapshot.Types)
        Assert.Empty(snapshot.Functions)

        if Option.isSome snapshot.ParentId then
            failwith "An empty snapshot unexpectedly inferred a parent snapshot."

    [<Fact>]
    let ``fixture ids reject ambiguous empty values`` () =
        Assert.Throws<System.ArgumentException>(fun () -> StableId.createForTesting<TypeTag> "" |> ignore)
        |> ignore
