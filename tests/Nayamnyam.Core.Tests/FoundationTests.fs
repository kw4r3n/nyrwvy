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
        Assert.NotEqual(before.DisplayName, after.DisplayName)

    [<Fact>]
    let ``snapshot construction does not infer missing semantic facts`` () =
        let id = StableId.createForTesting<SnapshotTag> "snapshot-fixture-1"
        let snapshot = Snapshot.empty id

        Assert.Empty(snapshot.Types)
        Assert.Empty(snapshot.Functions)
        Assert.True(Option.isNone snapshot.ParentId)

    [<Fact>]
    let ``fixture ids reject ambiguous empty values`` () =
        Assert.Throws<System.ArgumentException>(fun () -> StableId.createForTesting<TypeTag> "" |> ignore)
        |> ignore
