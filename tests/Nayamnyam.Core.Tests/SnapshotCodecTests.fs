namespace Nayamnyam.Core.Tests

open Nayamnyam.Core
open Xunit

module SnapshotCodecTests =
    let private unwrap = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected codec error: %A" error

    let private snapshot (): Snapshot =
        let typeA: SemanticType =
            { Id = StableId.createForTesting<TypeTag> "type-a"
              DisplayName = "Alpha" }

        let typeB: SemanticType =
            { Id = StableId.createForTesting<TypeTag> "type-b"
              DisplayName = "Beta" }

        let functionA: SemanticFunction =
            { Id = StableId.createForTesting<FunctionTag> "function-a"
              DisplayName = "first" }

        let functionB: SemanticFunction =
            { Id = StableId.createForTesting<FunctionTag> "function-b"
              DisplayName = "second" }

        { Id = StableId.createForTesting<SnapshotTag> "snapshot-current"
          ParentId = Some(StableId.createForTesting<SnapshotTag> "snapshot-parent")
          Types = [ typeB; typeA ]
          Functions = [ functionB; functionA ] }

    [<Fact>]
    let ``canonical snapshot serialization round-trips every current field`` () =
        let original = snapshot ()
        let canonical = SnapshotCodec.serialize original |> unwrap
        let restored = SnapshotCodec.deserialize canonical |> unwrap
        let reserialized = SnapshotCodec.serialize restored |> unwrap
        let expectedTypes = original.Types |> List.sortBy (fun item -> StableId.value item.Id)
        let expectedFunctions = original.Functions |> List.sortBy (fun item -> StableId.value item.Id)
        let typeAIndex = canonical.IndexOf("type-a", System.StringComparison.Ordinal)
        let typeBIndex = canonical.IndexOf("type-b", System.StringComparison.Ordinal)
        let functionAIndex = canonical.IndexOf("function-a", System.StringComparison.Ordinal)
        let functionBIndex = canonical.IndexOf("function-b", System.StringComparison.Ordinal)

        Assert.Equal(canonical, reserialized)
        Assert.Equal(original.Id, restored.Id)
        Assert.Equal(original.ParentId, restored.ParentId)
        Assert.True(expectedTypes = restored.Types)
        Assert.True(expectedFunctions = restored.Functions)

        if typeAIndex = -1 || typeBIndex = -1 then
            failwith "Canonical JSON did not contain both type IDs."

        if functionAIndex = -1 || functionBIndex = -1 then
            failwith "Canonical JSON did not contain both function IDs."

        if compare typeAIndex typeBIndex <> -1 then
            failwith "Canonical JSON did not sort types by stable ID."

        if compare functionAIndex functionBIndex <> -1 then
            failwith "Canonical JSON did not sort functions by stable ID."

    [<Fact>]
    let ``snapshot codec rejects unknown fields instead of discarding them`` () =
        let json = """{"schemaVersion":1,"snapshotId":"snapshot","types":[],"functions":[],"extra":true}"""

        match SnapshotCodec.deserialize json with
        | Error(SnapshotCodecError.InvalidShape message) -> Assert.Contains("unsupported", message)
        | result -> failwithf "Expected InvalidShape, got %A" result

    [<Fact>]
    let ``snapshot codec rejects duplicate property names`` () =
        let json = """{"schemaVersion":1,"snapshotId":"snapshot-a","snapshotId":"snapshot-b","types":[],"functions":[]}"""

        match SnapshotCodec.deserialize json with
        | Error(SnapshotCodecError.InvalidShape message) -> Assert.Contains("duplicate", message)
        | result -> failwithf "Expected duplicate-property InvalidShape, got %A" result

    [<Fact>]
    let ``snapshot codec rejects unsupported schema versions`` () =
        let json = """{"schemaVersion":2,"snapshotId":"snapshot","types":[],"functions":[]}"""

        match SnapshotCodec.deserialize json with
        | Error(SnapshotCodecError.UnsupportedSchemaVersion 2) -> ()
        | result -> failwithf "Expected UnsupportedSchemaVersion, got %A" result

    [<Fact>]
    let ``serialization rejects semantically invalid snapshots`` () =
        let original = snapshot ()
        let duplicate = original.Types.Head
        let invalid = { original with Types = duplicate :: duplicate :: original.Types.Tail }

        match SnapshotCodec.serialize invalid with
        | Error(SnapshotCodecError.InvalidSnapshot issues) ->
            Assert.True(issues |> List.exists (fun issue -> issue.Code = "DuplicateTypeId"))
        | result -> failwithf "Expected InvalidSnapshot, got %A" result
