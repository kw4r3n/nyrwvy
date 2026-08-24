namespace Nayamnyam.Core.Tests

open Nayamnyam.Core
open Xunit

module TransactionTests =
    let private baseSnapshot (): Snapshot =
        let typeId = StableId.createForTesting<TypeTag> "type-raw-user-input"
        let validateId = StableId.createForTesting<FunctionTag> "function-validate"
        let runId = StableId.createForTesting<FunctionTag> "function-run"
        let semanticType: SemanticType =
            { Id = typeId
              DisplayName = "RawUserInput" }
        let validateFunction: SemanticFunction =
            { Id = validateId
              DisplayName = "validate_user" }
        let runFunction: SemanticFunction =
            { Id = runId
              DisplayName = "run" }

        { Id = StableId.createForTesting<SnapshotTag> "snapshot-base"
          ParentId = Some(StableId.createForTesting<SnapshotTag> "snapshot-ancestor")
          Types = [ semanticType ]
          Functions = [ validateFunction; runFunction ] }

    let private request baseId proposedId operation =
        { Id = StableId.createForTesting<TransactionTag> "transaction-rename"
          BaseSnapshotId = baseId
          ProposedSnapshotId = proposedId
          Operations = [ operation ] }

    let private unwrap = function
        | Proposed simulation -> simulation
        | Rejected diagnostics -> failwithf "Unexpected rejection: %A" diagnostics

    [<Fact>]
    let ``rename transaction preserves all untouched snapshot state`` () =
        let original = baseSnapshot ()
        let target = original.Functions.Head
        let proposedId = StableId.createForTesting<SnapshotTag> "snapshot-proposed"
        let simulation =
            Transaction.simulate
                original
                (request
                    original.Id
                    proposedId
                    (RenameDisplayName(RenameTarget.FunctionTarget target.Id, "validate_profile")))
            |> unwrap

        let expectedFunctions =
            original.Functions
            |> List.map (fun item ->
                if item.Id = target.Id then
                    { item with DisplayName = "validate_profile" }
                else
                    item)

        let expected =
            { original with
                Id = proposedId
                ParentId = Some original.Id
                Functions = expectedFunctions }

        Assert.Equal(expected, simulation.ProposedSnapshot)
        Assert.True(original.Types = simulation.ProposedSnapshot.Types)
        Assert.True(original.Functions.Tail = simulation.ProposedSnapshot.Functions.Tail)

        match Assert.Single(simulation.Diff.Renames) with
        | FunctionRenamed(id, oldName, newName) ->
            Assert.Equal(target.Id, id)
            Assert.Equal("validate_user", oldName)
            Assert.Equal("validate_profile", newName)
        | rename -> failwithf "Expected a function rename, got %A" rename

    [<Fact>]
    let ``rename preview is canonically equivalent to an explicit record update`` () =
        let original = baseSnapshot ()
        let target = original.Types.Head
        let proposedId = StableId.createForTesting<SnapshotTag> "snapshot-proposed"
        let simulation =
            Transaction.simulate
                original
                (request
                    original.Id
                    proposedId
                    (RenameDisplayName(RenameTarget.TypeTarget target.Id, "Input")))
            |> unwrap

        let expected =
            { original with
                Id = proposedId
                ParentId = Some original.Id
                Types = [ { target with DisplayName = "Input" } ] }

        let actualJson = SnapshotCodec.serialize simulation.ProposedSnapshot |> function | Ok value -> value | Error error -> failwithf "%A" error
        let expectedJson = SnapshotCodec.serialize expected |> function | Ok value -> value | Error error -> failwithf "%A" error
        Assert.Equal(expectedJson, actualJson)

    [<Fact>]
    let ``stale base is rejected before applying operations`` () =
        let original = baseSnapshot ()
        let target = original.Functions.Head
        let staleBase = StableId.createForTesting<SnapshotTag> "snapshot-stale"
        let proposedId = StableId.createForTesting<SnapshotTag> "snapshot-proposed"
        let result =
            Transaction.simulate
                original
                (request
                    staleBase
                    proposedId
                    (RenameDisplayName(RenameTarget.FunctionTarget target.Id, "renamed")))

        match result with
        | Rejected diagnostics -> Assert.Equal("StaleBaseSnapshot", Assert.Single(diagnostics).Code)
        | Proposed _ -> failwith "Expected stale-base rejection."

    [<Fact>]
    let ``missing rename target is rejected without inventing an entity`` () =
        let original = baseSnapshot ()
        let missing = StableId.createForTesting<FunctionTag> "function-missing"
        let proposedId = StableId.createForTesting<SnapshotTag> "snapshot-proposed"
        let result =
            Transaction.simulate
                original
                (request
                    original.Id
                    proposedId
                    (RenameDisplayName(RenameTarget.FunctionTarget missing, "invented")))

        match result with
        | Rejected diagnostics -> Assert.Equal("TargetNotFound", Assert.Single(diagnostics).Code)
        | Proposed _ -> failwith "Expected missing-target rejection."

    [<Fact>]
    let ``blank rename is rejected rather than normalized or guessed`` () =
        let original = baseSnapshot ()
        let target = original.Functions.Head
        let proposedId = StableId.createForTesting<SnapshotTag> "snapshot-proposed"
        let result =
            Transaction.simulate
                original
                (request
                    original.Id
                    proposedId
                    (RenameDisplayName(RenameTarget.FunctionTarget target.Id, "   ")))

        match result with
        | Rejected diagnostics -> Assert.Equal("InvalidDisplayName", Assert.Single(diagnostics).Code)
        | Proposed _ -> failwith "Expected blank-name rejection."
