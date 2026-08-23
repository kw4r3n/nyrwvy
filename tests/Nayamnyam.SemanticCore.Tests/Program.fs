open System
open Nayamnyam.SemanticCore

module Program =
    let private assertEqual expected actual message =
        if expected <> actual then
            failwith message

    let private requireId<'Tag> value =
        match StableId.create<'Tag> value with
        | Ok stableId ->
            stableId
        | Error StableIdError.BlankValue ->
            failwith "Expected a nonblank stable ID."

    let private functionId (value: string) : FunctionId =
        requireId<FunctionIdTag> value

    let private snapshotId (value: string) : SnapshotId =
        requireId<SnapshotIdTag> value

    let private transactionId (value: string) : TransactionId =
        requireId<TransactionIdTag> value

    let private requireSnapshot result =
        match result with
        | Ok snapshot ->
            snapshot
        | Error(SnapshotError.NegativeRevision revision) ->
            failwith $"Unexpected negative revision: {revision}"
        | Error(SnapshotError.DuplicateFunctionId duplicateFunctionId) ->
            failwith
                $"Unexpected duplicate function ID: {StableId.value duplicateFunctionId}"

    let private createBaseline () =
        let validateUser: FunctionEntity =
            { Id = functionId "function:validate-user"
              DisplayName = "validate_user" }

        let main: FunctionEntity =
            { Id = functionId "function:main"
              DisplayName = "main" }

        let snapshot =
            Snapshot.create (snapshotId "snapshot:0") 0 [ validateUser; main ]
            |> requireSnapshot

        snapshot, validateUser.Id

    let private createRenameRequest
        baseSnapshotId
        proposedSnapshotId
        targetFunctionId
        nextDisplayName
        : RenameFunctionRequest =
        { TransactionId = transactionId "transaction:rename-function"
          BaseSnapshotId = baseSnapshotId
          ProposedSnapshotId = proposedSnapshotId
          FunctionId = targetFunctionId
          NewDisplayName = nextDisplayName }

    let private testBlankStableIdIsRejected () =
        match StableId.create<FunctionIdTag> "   " with
        | Error StableIdError.BlankValue ->
            ()
        | Ok createdId ->
            failwith $"Blank ID was accepted: {StableId.value createdId}"

    let private testDuplicateFunctionIdsAreRejected () =
        let duplicateId = functionId "function:duplicate"

        let firstFunction: FunctionEntity =
            { Id = duplicateId
              DisplayName = "first" }

        let secondFunction: FunctionEntity =
            { Id = duplicateId
              DisplayName = "second" }

        match
            Snapshot.create
                (snapshotId "snapshot:duplicate")
                0
                [ firstFunction; secondFunction ]
        with
        | Error(SnapshotError.DuplicateFunctionId foundId) ->
            assertEqual duplicateId foundId "Duplicate function ID was not reported."
        | Error(SnapshotError.NegativeRevision reportedRevision) ->
            failwith $"Wrong validation error: negative revision {reportedRevision}."
        | Ok createdSnapshot ->
            failwith $"Duplicate function IDs were accepted in {StableId.value (Snapshot.id createdSnapshot)}."

    let private testRenamePreservesFunctionIdentity () =
        let snapshot, validateUserId = createBaseline ()

        let request =
            createRenameRequest
                (Snapshot.id snapshot)
                (snapshotId "snapshot:1")
                validateUserId
                "validate_profile"

        match Transactions.renameFunction request snapshot with
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            match Snapshot.tryFindFunction validateUserId committedSnapshot with
            | Some renamedFunction ->
                assertEqual
                    "validate_profile"
                    renamedFunction.DisplayName
                    "Rename did not update the display name."
            | None ->
                failwith "Rename removed the function instead of preserving its stable ID."

            match semanticDiff with
            | [ SemanticChange.FunctionRenamed(changedId, oldName, newName) ] ->
                assertEqual validateUserId changedId "Semantic diff changed the function ID."
                assertEqual "validate_user" oldName "Semantic diff recorded the wrong old name."
                assertEqual "validate_profile" newName "Semantic diff recorded the wrong new name."
            | [] ->
                failwith "Rename produced an empty semantic diff."
            | firstChange :: remainingChanges ->
                failwith
                    $"Rename produced unexpected diff entries: {firstChange}; {remainingChanges}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"Rename unexpectedly produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."
        | TransactionResult.Rejected operationError ->
            failwith $"Rename was rejected: {operationError}."

    let private testRenameToCurrentNameIsNoChange () =
        let snapshot, validateUserId = createBaseline ()

        let request =
            createRenameRequest
                (Snapshot.id snapshot)
                (snapshotId "snapshot:unused")
                validateUserId
                "validate_user"

        match Transactions.renameFunction request snapshot with
        | TransactionResult.NoChange unchangedSnapshot ->
            assertEqual
                (Snapshot.id snapshot)
                (Snapshot.id unchangedSnapshot)
                "No-change rename created a different snapshot."

            assertEqual
                (Snapshot.revision snapshot)
                (Snapshot.revision unchangedSnapshot)
                "No-change rename changed the revision."
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            failwith
                $"No-change rename committed {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff}."
        | TransactionResult.Rejected operationError ->
            failwith $"No-change rename was rejected: {operationError}."

    let private testStaleBaseIsRejected () =
        let snapshot, validateUserId = createBaseline ()

        let request =
            createRenameRequest
                (snapshotId "snapshot:stale")
                (snapshotId "snapshot:1")
                validateUserId
                "validate_profile"

        match Transactions.renameFunction request snapshot with
        | TransactionResult.Rejected(
            OperationError.StaleBaseSnapshot(requestedSnapshotId, actualSnapshotId)
          ) ->
            assertEqual
                (snapshotId "snapshot:stale")
                requestedSnapshotId
                "Stale-base error reported the wrong requested snapshot."

            assertEqual
                (Snapshot.id snapshot)
                actualSnapshotId
                "Stale-base error reported the wrong actual snapshot."
        | TransactionResult.Rejected otherError ->
            failwith $"Stale-base rename produced the wrong error: {otherError}."
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            failwith
                $"Stale-base rename committed {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"Stale-base rename produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."

    let private testReusedSnapshotIdIsRejected () =
        let snapshot, validateUserId = createBaseline ()

        let request =
            createRenameRequest
                (Snapshot.id snapshot)
                (Snapshot.id snapshot)
                validateUserId
                "validate_profile"

        match Transactions.renameFunction request snapshot with
        | TransactionResult.Rejected(OperationError.ReusedSnapshotId reusedSnapshotId) ->
            assertEqual
                (Snapshot.id snapshot)
                reusedSnapshotId
                "Reused-snapshot error reported the wrong snapshot ID."
        | TransactionResult.Rejected otherError ->
            failwith $"Reused-snapshot rename produced the wrong error: {otherError}."
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            failwith
                $"Reused-snapshot rename committed {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"Reused-snapshot rename produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."

    let private runTest (name, test) =
        try
            test ()
            printfn "PASS %s" name
            true
        with error ->
            eprintfn "FAIL %s: %s" name error.Message
            false

    [<EntryPoint>]
    let main _ =
        let tests =
            [ "blank stable ID is rejected", testBlankStableIdIsRejected
              "duplicate function IDs are rejected", testDuplicateFunctionIdsAreRejected
              "rename preserves function identity", testRenamePreservesFunctionIdentity
              "rename to current name is no change", testRenameToCurrentNameIsNoChange
              "stale base is rejected", testStaleBaseIsRejected
              "reused snapshot ID is rejected", testReusedSnapshotIdIsRejected ]

        let allPassed =
            tests
            |> List.map runTest
            |> List.forall (fun passed -> passed)

        if allPassed then 0 else 1
