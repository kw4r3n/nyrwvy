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

    let private parameterId (value: string) : ParameterId =
        requireId<ParameterIdTag> value

    let private callId (value: string) : CallId =
        requireId<CallIdTag> value

    let private valueId (value: string) : ValueId =
        requireId<ValueIdTag> value

    let private obligationId (value: string) : ObligationId =
        requireId<ObligationIdTag> value

    let private requireSnapshot result =
        match result with
        | Ok snapshot ->
            snapshot
        | Error(SnapshotError.NegativeRevision revision) ->
            failwith $"Unexpected negative revision: {revision}"
        | Error(SnapshotError.DuplicateFunctionId duplicateFunctionId) ->
            failwith
                $"Unexpected duplicate function ID: {StableId.value duplicateFunctionId}"
        | Error(SnapshotError.DuplicateParameterId(duplicateFunctionId, duplicateParameterId)) ->
            failwith
                $"Unexpected duplicate parameter ID in {StableId.value duplicateFunctionId}: {StableId.value duplicateParameterId}"
        | Error(SnapshotError.DuplicateCallId duplicateCallId) ->
            failwith $"Unexpected duplicate call ID: {StableId.value duplicateCallId}"

    let private createBaseline () =
        let rawInput: Parameter =
            { Id = parameterId "parameter:raw-input"
              DisplayName = "raw_input" }

        let rawBinding: ArgumentBinding =
            { ParameterId = rawInput.Id
              ValueId = valueId "value:raw-input" }

        let validateUser: FunctionEntity =
            { Id = functionId "function:validate-user"
              DisplayName = "validate_user"
              Parameters = [ rawInput ]
              Calls = [] }

        let validateCall: Call =
            { Id = callId "call:main-validate"
              TargetFunctionId = validateUser.Id
              ArgumentBindings = Map.ofList [ (rawInput.Id, rawBinding) ] }

        let main: FunctionEntity =
            { Id = functionId "function:main"
              DisplayName = "main"
              Parameters = []
              Calls = [ validateCall ] }

        let snapshot =
            Snapshot.create (snapshotId "snapshot:0") 0 [ validateUser; main ]
            |> requireSnapshot

        snapshot, validateUser.Id, rawInput.Id, validateCall.Id

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

    let private createAddParameterRequest
        baseSnapshotId
        proposedSnapshotId
        targetFunctionId
        requestTransactionId
        parameter
        : AddParameterRequest =
        { TransactionId = requestTransactionId
          BaseSnapshotId = baseSnapshotId
          ProposedSnapshotId = proposedSnapshotId
          FunctionId = targetFunctionId
          Parameter = parameter }

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
              DisplayName = "first"
              Parameters = []
              Calls = [] }

        let secondFunction: FunctionEntity =
            { Id = duplicateId
              DisplayName = "second"
              Parameters = []
              Calls = [] }

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
        | Error(SnapshotError.DuplicateParameterId(foundFunctionId, foundParameterId)) ->
            failwith
                $"Wrong validation error: duplicate parameter {StableId.value foundParameterId} in {StableId.value foundFunctionId}."
        | Error(SnapshotError.DuplicateCallId foundCallId) ->
            failwith $"Wrong validation error: duplicate call {StableId.value foundCallId}."
        | Ok createdSnapshot ->
            failwith
                $"Duplicate function IDs were accepted in {StableId.value (Snapshot.id createdSnapshot)}."

    let private testRenamePreservesFunctionIdentity () =
        let snapshot, validateUserId, _, _ = createBaseline ()

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
        | TransactionResult.Incomplete(committedSnapshot, semanticDiff, blockingObligations) ->
            failwith
                $"Rename unexpectedly produced incomplete snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff} and {blockingObligations}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"Rename unexpectedly produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."
        | TransactionResult.Rejected operationError ->
            failwith $"Rename was rejected: {operationError}."

    let private testRenameToCurrentNameIsNoChange () =
        let snapshot, validateUserId, _, _ = createBaseline ()

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
        | TransactionResult.Incomplete(committedSnapshot, semanticDiff, blockingObligations) ->
            failwith
                $"No-change rename unexpectedly produced incomplete snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff} and {blockingObligations}."
        | TransactionResult.Rejected operationError ->
            failwith $"No-change rename was rejected: {operationError}."

    let private testStaleBaseIsRejected () =
        let snapshot, validateUserId, _, _ = createBaseline ()

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
        | TransactionResult.Incomplete(committedSnapshot, semanticDiff, blockingObligations) ->
            failwith
                $"Stale-base rename unexpectedly produced incomplete snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff} and {blockingObligations}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"Stale-base rename produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."

    let private testReusedSnapshotIdIsRejected () =
        let snapshot, validateUserId, _, _ = createBaseline ()

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
        | TransactionResult.Incomplete(committedSnapshot, semanticDiff, blockingObligations) ->
            failwith
                $"Reused-snapshot rename unexpectedly produced incomplete snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff} and {blockingObligations}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"Reused-snapshot rename produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."

    let private testAddParameterCreatesMissingArgumentObligation () =
        let snapshot, validateUserId, rawInputId, validateCallId = createBaseline ()
        let policyParameter: Parameter =
            { Id = parameterId "parameter:policy"
              DisplayName = "policy" }

        let request =
            createAddParameterRequest
                (Snapshot.id snapshot)
                (snapshotId "snapshot:2")
                validateUserId
                (transactionId "transaction:add-parameter")
                policyParameter

        match Transactions.addParameter request snapshot with
        | TransactionResult.Incomplete(committedSnapshot, semanticDiff, blockingObligations) ->
            match Snapshot.tryFindFunction validateUserId committedSnapshot with
            | Some updatedFunction ->
                assertEqual
                    [ rawInputId; policyParameter.Id ]
                    (updatedFunction.Parameters |> List.map (fun parameter -> parameter.Id))
                    "Adding a parameter did not preserve the existing parameter or append the new one."
            | None ->
                failwith "Adding a parameter removed the target function."

            let matchingCalls =
                Snapshot.allCalls committedSnapshot
                |> List.map snd
                |> List.filter (fun call -> call.Id = validateCallId)

            match matchingCalls with
            | [ updatedCall ] ->
                assertEqual
                    true
                    (Map.containsKey rawInputId updatedCall.ArgumentBindings)
                    "Adding a parameter removed an existing argument binding."

                assertEqual
                    false
                    (Map.containsKey policyParameter.Id updatedCall.ArgumentBindings)
                    "The new parameter was unexpectedly bound automatically."
            | [] ->
                failwith "Adding a parameter removed the affected call."
            | firstCall :: remainingCalls ->
                failwith
                    $"Expected one affected call, found {firstCall}; {remainingCalls}."

            match blockingObligations with
            | [ obligation ] ->
                let expectedObligationId =
                    obligationId
                        $"obligation:missing-argument:{StableId.value validateCallId}:{StableId.value policyParameter.Id}"

                assertEqual
                    expectedObligationId
                    obligation.Id
                    "Missing-argument obligation did not receive the deterministic ID."

                assertEqual
                    ObligationKind.MissingArgument
                    obligation.Kind
                    "Missing-argument obligation had the wrong kind."

                assertEqual
                    validateCallId
                    obligation.CallId
                    "Missing-argument obligation referenced the wrong call."

                assertEqual
                    policyParameter.Id
                    obligation.ParameterId
                    "Missing-argument obligation referenced the wrong parameter."

                assertEqual
                    (transactionId "transaction:add-parameter")
                    obligation.OriginTransactionId
                    "Missing-argument obligation referenced the wrong originating transaction."

                assertEqual
                    ObligationStatus.Open
                    obligation.Status
                    "Missing-argument obligation was not open."

                assertEqual
                    (Some obligation)
                    (Snapshot.tryFindObligation obligation.Id committedSnapshot)
                    "Committed snapshot did not retain the missing-argument obligation."
            | [] ->
                failwith "Adding a parameter did not create a blocking obligation."
            | firstObligation :: remainingObligations ->
                failwith
                    $"Expected one blocking obligation, found {firstObligation}; {remainingObligations}."

            match semanticDiff with
            | [ SemanticChange.ParameterAdded(changedFunctionId, changedParameterId)
                SemanticChange.ObligationCreated createdObligationId ] ->
                assertEqual
                    validateUserId
                    changedFunctionId
                    "Parameter-added diff referenced the wrong function."

                assertEqual
                    policyParameter.Id
                    changedParameterId
                    "Parameter-added diff referenced the wrong parameter."

                assertEqual
                    (obligationId
                        $"obligation:missing-argument:{StableId.value validateCallId}:{StableId.value policyParameter.Id}")
                    createdObligationId
                    "Obligation-created diff referenced the wrong obligation."
            | [] ->
                failwith "Adding a parameter produced an empty semantic diff."
            | firstChange :: remainingChanges ->
                failwith
                    $"Adding a parameter produced unexpected diff entries: {firstChange}; {remainingChanges}."
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            failwith
                $"Adding a parameter unexpectedly produced executable snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"Adding a parameter unexpectedly produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."
        | TransactionResult.Rejected operationError ->
            failwith $"Adding a parameter was rejected: {operationError}."

    let private testAddParameterRejectsDuplicateId () =
        let snapshot, validateUserId, rawInputId, _ = createBaseline ()

        let duplicateParameter: Parameter =
            { Id = rawInputId
              DisplayName = "replacement" }

        let request =
            createAddParameterRequest
                (Snapshot.id snapshot)
                (snapshotId "snapshot:duplicate-parameter")
                validateUserId
                (transactionId "transaction:duplicate-parameter")
                duplicateParameter

        match Transactions.addParameter request snapshot with
        | TransactionResult.Rejected(
            OperationError.ParameterAlreadyExists(foundFunctionId, foundParameterId)
          ) ->
            assertEqual
                validateUserId
                foundFunctionId
                "Duplicate-parameter error reported the wrong function."

            assertEqual
                rawInputId
                foundParameterId
                "Duplicate-parameter error reported the wrong parameter."
        | TransactionResult.Rejected otherError ->
            failwith $"Duplicate parameter produced the wrong error: {otherError}."
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            failwith
                $"Duplicate parameter committed {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff}."
        | TransactionResult.Incomplete(committedSnapshot, semanticDiff, blockingObligations) ->
            failwith
                $"Duplicate parameter unexpectedly produced incomplete snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff} and {blockingObligations}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"Duplicate parameter produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."

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
              "reused snapshot ID is rejected", testReusedSnapshotIdIsRejected
              "adding a parameter creates a missing-argument obligation",
              testAddParameterCreatesMissingArgumentObligation
              "adding a parameter rejects duplicate IDs", testAddParameterRejectsDuplicateId ]

        let allPassed =
            tests
            |> List.map runTest
            |> List.forall (fun passed -> passed)

        if allPassed then 0 else 1
