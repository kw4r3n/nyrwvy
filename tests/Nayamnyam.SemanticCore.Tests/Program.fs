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
        | Error snapshotError ->
            failwith $"Unexpected snapshot error: {snapshotError}"

    let private assertSnapshotError expected result message =
        match result with
        | Error actual ->
            assertEqual expected actual message
        | Ok snapshot ->
            failwith $"{message} Accepted snapshot {StableId.value (Snapshot.id snapshot)}."

    let private requireCommittedSnapshot result =
        match result with
        | TransactionResult.Executable(snapshot, _) ->
            snapshot
        | TransactionResult.Incomplete(snapshot, _, _) ->
            snapshot
        | TransactionResult.NoChange snapshot ->
            failwith $"Expected a committed snapshot, received no change at {StableId.value (Snapshot.id snapshot)}."
        | TransactionResult.Rejected operationError ->
            failwith $"Expected a committed snapshot, received {operationError}."

    let private createBaseline () =
        let rawInput: Parameter =
            { Id = parameterId "parameter:raw-input"
              DisplayName = "raw_input" }

        let rawValue: ValueEntity =
            { Id = valueId "value:raw-input" }

        let policyValue: ValueEntity =
            { Id = valueId "value:policy" }

        let rawBinding: ArgumentBinding =
            { ParameterId = rawInput.Id
              ValueId = rawValue.Id }

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
            Snapshot.create
                (snapshotId "snapshot:0")
                0
                [ rawValue; policyValue ]
                [ validateUser; main ]
            |> requireSnapshot

        snapshot, validateUser.Id, rawInput.Id, validateCall.Id

    let private createRenameRequest
        baseSnapshot
        proposedSnapshotId
        targetFunctionId
        nextDisplayName
        : RenameFunctionRequest =
        { TransactionId = transactionId $"transaction:rename:{StableId.value proposedSnapshotId}"
          BaseSnapshot = baseSnapshot
          ProposedSnapshotId = proposedSnapshotId
          FunctionId = targetFunctionId
          NewDisplayName = nextDisplayName }

    let private createAddParameterRequest
        baseSnapshot
        proposedSnapshotId
        targetFunctionId
        requestTransactionId
        parameter
        : AddParameterRequest =
        { TransactionId = requestTransactionId
          BaseSnapshot = baseSnapshot
          ProposedSnapshotId = proposedSnapshotId
          FunctionId = targetFunctionId
          Parameter = parameter }

    let private createResolveMissingArgumentRequest
        baseSnapshot
        proposedSnapshotId
        obligationId
        valueId
        requestTransactionId
        : ResolveMissingArgumentRequest =
        { TransactionId = requestTransactionId
          BaseSnapshot = baseSnapshot
          ProposedSnapshotId = proposedSnapshotId
          ObligationId = obligationId
          ValueId = valueId }

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
                []
                [ firstFunction; secondFunction ]
        with
        | Error(SnapshotError.DuplicateFunctionId foundId) ->
            assertEqual duplicateId foundId "Duplicate function ID was not reported."
        | Error(SnapshotError.NegativeRevision reportedRevision) ->
            failwith $"Wrong validation error: negative revision {reportedRevision}."
        | Error(SnapshotError.DuplicateParameterId foundParameterId) ->
            failwith $"Wrong validation error: duplicate parameter {StableId.value foundParameterId}."
        | Error(SnapshotError.DuplicateCallId foundCallId) ->
            failwith $"Wrong validation error: duplicate call {StableId.value foundCallId}."
        | Error otherError ->
            failwith $"Wrong validation error: {otherError}."
        | Ok createdSnapshot ->
            failwith
                $"Duplicate function IDs were accepted in {StableId.value (Snapshot.id createdSnapshot)}."

    let private testDuplicateValueIdsAreRejected () =
        let duplicateValue: ValueEntity =
            { Id = valueId "value:duplicate" }

        assertSnapshotError
            (SnapshotError.DuplicateValueId duplicateValue.Id)
            (Snapshot.create
                (snapshotId "snapshot:duplicate-value")
                0
                [ duplicateValue; duplicateValue ]
                [])
            "Duplicate value IDs were accepted."

    let private testDuplicateParameterIdsAcrossFunctionsAreRejected () =
        let duplicateParameter: Parameter =
            { Id = parameterId "parameter:duplicate"
              DisplayName = "duplicate" }

        let firstFunction: FunctionEntity =
            { Id = functionId "function:first"
              DisplayName = "first"
              Parameters = [ duplicateParameter ]
              Calls = [] }

        let secondFunction: FunctionEntity =
            { Id = functionId "function:second"
              DisplayName = "second"
              Parameters = [ duplicateParameter ]
              Calls = [] }

        assertSnapshotError
            (SnapshotError.DuplicateParameterId duplicateParameter.Id)
            (Snapshot.create
                (snapshotId "snapshot:duplicate-parameter")
                0
                []
                [ firstFunction; secondFunction ])
            "Duplicate parameter IDs across functions were accepted."

    let private testCallEdgesAreValidated () =
        let missingTargetId = functionId "function:missing"

        let unknownTargetCall: Call =
            { Id = callId "call:unknown-target"
              TargetFunctionId = missingTargetId
              ArgumentBindings = Map.empty }

        let unknownTargetCaller: FunctionEntity =
            { Id = functionId "function:unknown-target-caller"
              DisplayName = "unknown_target_caller"
              Parameters = []
              Calls = [ unknownTargetCall ] }

        assertSnapshotError
            (SnapshotError.UnknownTargetFunction(unknownTargetCall.Id, missingTargetId))
            (Snapshot.create
                (snapshotId "snapshot:unknown-target")
                0
                []
                [ unknownTargetCaller ])
            "A call with an unknown target function was accepted."

        let targetParameter: Parameter =
            { Id = parameterId "parameter:target"
              DisplayName = "target" }

        let otherParameter: Parameter =
            { Id = parameterId "parameter:other"
              DisplayName = "other" }

        let targetFunction: FunctionEntity =
            { Id = functionId "function:target"
              DisplayName = "target"
              Parameters = [ targetParameter ]
              Calls = [] }

        let otherFunction: FunctionEntity =
            { Id = functionId "function:other"
              DisplayName = "other"
              Parameters = [ otherParameter ]
              Calls = [] }

        let mismatchedBinding: ArgumentBinding =
            { ParameterId = otherParameter.Id
              ValueId = valueId "value:mismatch" }

        let mismatchedKeyCall: Call =
            { Id = callId "call:mismatched-key"
              TargetFunctionId = targetFunction.Id
              ArgumentBindings = Map.ofList [ (targetParameter.Id, mismatchedBinding) ] }

        let mismatchedKeyCaller: FunctionEntity =
            { Id = functionId "function:mismatched-key-caller"
              DisplayName = "mismatched_key_caller"
              Parameters = []
              Calls = [ mismatchedKeyCall ] }

        assertSnapshotError
            (SnapshotError.ArgumentBindingKeyMismatch(
                mismatchedKeyCall.Id,
                targetParameter.Id,
                otherParameter.Id
            ))
            (Snapshot.create
                (snapshotId "snapshot:mismatched-key")
                0
                []
                [ targetFunction; otherFunction; mismatchedKeyCaller ])
            "An argument binding whose key disagreed with its payload was accepted."

        let wrongOwnerBinding: ArgumentBinding =
            { ParameterId = otherParameter.Id
              ValueId = valueId "value:wrong-owner" }

        let wrongOwnerCall: Call =
            { Id = callId "call:wrong-owner"
              TargetFunctionId = targetFunction.Id
              ArgumentBindings = Map.ofList [ (otherParameter.Id, wrongOwnerBinding) ] }

        let wrongOwnerCaller: FunctionEntity =
            { Id = functionId "function:wrong-owner-caller"
              DisplayName = "wrong_owner_caller"
              Parameters = []
              Calls = [ wrongOwnerCall ] }

        assertSnapshotError
            (SnapshotError.ArgumentParameterTargetsWrongFunction(
                wrongOwnerCall.Id,
                targetFunction.Id,
                otherParameter.Id
            ))
            (Snapshot.create
                (snapshotId "snapshot:wrong-owner")
                0
                []
                [ targetFunction; otherFunction; wrongOwnerCaller ])
            "An argument binding for a different target function was accepted."

        let unknownParameterId = parameterId "parameter:unknown"

        let unknownParameterBinding: ArgumentBinding =
            { ParameterId = unknownParameterId
              ValueId = valueId "value:unknown-parameter" }

        let unknownParameterCall: Call =
            { Id = callId "call:unknown-parameter"
              TargetFunctionId = targetFunction.Id
              ArgumentBindings = Map.ofList [ (unknownParameterId, unknownParameterBinding) ] }

        let unknownParameterCaller: FunctionEntity =
            { Id = functionId "function:unknown-parameter-caller"
              DisplayName = "unknown_parameter_caller"
              Parameters = []
              Calls = [ unknownParameterCall ] }

        assertSnapshotError
            (SnapshotError.UnknownArgumentParameter(unknownParameterCall.Id, unknownParameterId))
            (Snapshot.create
                (snapshotId "snapshot:unknown-parameter")
                0
                []
                [ targetFunction; unknownParameterCaller ])
            "A call with an unknown argument parameter was accepted."

        let unknownValueId = valueId "value:unknown"

        let unknownValueBinding: ArgumentBinding =
            { ParameterId = targetParameter.Id
              ValueId = unknownValueId }

        let unknownValueCall: Call =
            { Id = callId "call:unknown-value"
              TargetFunctionId = targetFunction.Id
              ArgumentBindings = Map.ofList [ (targetParameter.Id, unknownValueBinding) ] }

        let unknownValueCaller: FunctionEntity =
            { Id = functionId "function:unknown-value-caller"
              DisplayName = "unknown_value_caller"
              Parameters = []
              Calls = [ unknownValueCall ] }

        assertSnapshotError
            (SnapshotError.UnknownArgumentValue(unknownValueCall.Id, unknownValueId))
            (Snapshot.create
                (snapshotId "snapshot:unknown-value")
                0
                []
                [ targetFunction; unknownValueCaller ])
            "A call with an unknown argument value was accepted."

        let missingArgumentCall: Call =
            { Id = callId "call:missing-argument"
              TargetFunctionId = targetFunction.Id
              ArgumentBindings = Map.empty }

        let missingArgumentCaller: FunctionEntity =
            { Id = functionId "function:missing-argument-caller"
              DisplayName = "missing_argument_caller"
              Parameters = []
              Calls = [ missingArgumentCall ] }

        assertSnapshotError
            (SnapshotError.MissingArgument(missingArgumentCall.Id, targetParameter.Id))
            (Snapshot.create
                (snapshotId "snapshot:missing-argument")
                0
                []
                [ targetFunction; missingArgumentCaller ])
            "A call with a missing required argument was accepted."

    let private testRenamePreservesFunctionIdentity () =
        let snapshot, validateUserId, _, _ = createBaseline ()

        let request =
            createRenameRequest
                (Snapshot.reference snapshot)
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

            assertEqual
                1
                (Snapshot.revision committedSnapshot)
                "Rename did not increment the revision."

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
                (Snapshot.reference snapshot)
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

        let staleBase =
            { Snapshot.reference snapshot with
                Id = snapshotId "snapshot:stale" }

        let request =
            createRenameRequest
                staleBase
                (snapshotId "snapshot:1")
                validateUserId
                "validate_profile"

        match Transactions.renameFunction request snapshot with
        | TransactionResult.Rejected(OperationError.StaleBaseSnapshot(requested, actual)) ->
            assertEqual
                staleBase
                requested
                "Stale-base error reported the wrong requested reference."

            assertEqual
                (Snapshot.reference snapshot)
                actual
                "Stale-base error reported the wrong actual reference."
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

    let private createSnapshotWithReturnedId () =
        let originalSnapshot, validateUserId, _, _ = createBaseline ()

        let firstRequest =
            createRenameRequest
                (Snapshot.reference originalSnapshot)
                (snapshotId "snapshot:intermediate")
                validateUserId
                "validate_profile"

        let intermediateSnapshot =
            Transactions.renameFunction firstRequest originalSnapshot
            |> requireCommittedSnapshot

        let returnedIdRequest =
            createRenameRequest
                (Snapshot.reference intermediateSnapshot)
                (Snapshot.id originalSnapshot)
                validateUserId
                "validate_account"

        let returnedIdSnapshot =
            Transactions.renameFunction returnedIdRequest intermediateSnapshot
            |> requireCommittedSnapshot

        originalSnapshot, returnedIdSnapshot, validateUserId

    let private testReturnedSnapshotIdDoesNotAcceptStaleRevision () =
        let originalSnapshot, currentSnapshot, validateUserId = createSnapshotWithReturnedId ()

        assertEqual
            (Snapshot.id originalSnapshot)
            (Snapshot.id currentSnapshot)
            "The setup did not return to the original snapshot ID."

        assertEqual
            2
            (Snapshot.revision currentSnapshot)
            "The setup did not advance to the expected revision."

        let staleRequest =
            createRenameRequest
                (Snapshot.reference originalSnapshot)
                (snapshotId "snapshot:stale-revision")
                validateUserId
                "validate_legacy"

        match Transactions.renameFunction staleRequest currentSnapshot with
        | TransactionResult.Rejected(OperationError.StaleBaseSnapshot(requested, actual)) ->
            assertEqual
                (Snapshot.reference originalSnapshot)
                requested
                "The stale request reported the wrong base reference."

            assertEqual
                (Snapshot.reference currentSnapshot)
                actual
                "The stale error reported the wrong current reference."

            match Snapshot.tryFindFunction validateUserId currentSnapshot with
            | Some currentFunction ->
                assertEqual
                    "validate_account"
                    currentFunction.DisplayName
                    "A stale rename changed the current snapshot."
            | None ->
                failwith "The stale rename removed the target function."
        | TransactionResult.Rejected otherError ->
            failwith $"The stale revision produced the wrong error: {otherError}."
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            failwith
                $"A stale revision committed {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff}."
        | TransactionResult.Incomplete(committedSnapshot, semanticDiff, blockingObligations) ->
            failwith
                $"A stale revision committed incomplete snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff} and {blockingObligations}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"A stale revision returned no change at {StableId.value (Snapshot.id unchangedSnapshot)}."

    let private testStaleRevisionRejectsParameterAddition () =
        let originalSnapshot, currentSnapshot, validateUserId = createSnapshotWithReturnedId ()

        let request =
            createAddParameterRequest
                (Snapshot.reference originalSnapshot)
                (snapshotId "snapshot:stale-parameter")
                validateUserId
                (transactionId "transaction:stale-parameter")
                { Id = parameterId "parameter:late"
                  DisplayName = "late" }

        match Transactions.addParameter request currentSnapshot with
        | TransactionResult.Rejected(OperationError.StaleBaseSnapshot(requested, actual)) ->
            assertEqual
                (Snapshot.reference originalSnapshot)
                requested
                "The stale add-parameter request reported the wrong base reference."

            assertEqual
                (Snapshot.reference currentSnapshot)
                actual
                "The stale add-parameter error reported the wrong current reference."

            match Snapshot.tryFindFunction validateUserId currentSnapshot with
            | Some currentFunction ->
                assertEqual
                    1
                    (List.length currentFunction.Parameters)
                    "A stale add-parameter request changed the current function."
            | None ->
                failwith "The stale add-parameter request removed the target function."
        | TransactionResult.Rejected otherError ->
            failwith $"The stale add-parameter request produced the wrong error: {otherError}."
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            failwith
                $"A stale add-parameter request committed {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff}."
        | TransactionResult.Incomplete(committedSnapshot, semanticDiff, blockingObligations) ->
            failwith
                $"A stale add-parameter request committed incomplete snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff} and {blockingObligations}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"A stale add-parameter request returned no change at {StableId.value (Snapshot.id unchangedSnapshot)}."

    let private testDivergentSnapshotsDoNotShareBaseReference () =
        let snapshot, validateUserId, _, _ = createBaseline ()
        let proposedSnapshotId = snapshotId "snapshot:divergent"

        let leftRequestTemplate =
            createRenameRequest
                (Snapshot.reference snapshot)
                proposedSnapshotId
                validateUserId
                "validate_left"

        let leftRequest =
            { leftRequestTemplate with
                TransactionId = transactionId "transaction:left" }

        let rightRequestTemplate =
            createRenameRequest
                (Snapshot.reference snapshot)
                proposedSnapshotId
                validateUserId
                "validate_right"

        let rightRequest =
            { rightRequestTemplate with
                TransactionId = transactionId "transaction:right" }

        let leftSnapshot =
            Transactions.renameFunction leftRequest snapshot
            |> requireCommittedSnapshot

        let rightSnapshot =
            Transactions.renameFunction rightRequest snapshot
            |> requireCommittedSnapshot

        assertEqual
            (Snapshot.id leftSnapshot)
            (Snapshot.id rightSnapshot)
            "The setup did not produce matching snapshot IDs."

        assertEqual
            (Snapshot.revision leftSnapshot)
            (Snapshot.revision rightSnapshot)
            "The setup did not produce matching revisions."

        assertEqual
            false
            (Snapshot.reference leftSnapshot = Snapshot.reference rightSnapshot)
            "Divergent commits produced the same snapshot reference."

        let requestBoundToLeft =
            createRenameRequest
                (Snapshot.reference leftSnapshot)
                (snapshotId "snapshot:after-left")
                validateUserId
                "validate_left_next"

        match Transactions.renameFunction requestBoundToLeft rightSnapshot with
        | TransactionResult.Rejected(OperationError.StaleBaseSnapshot(requested, actual)) ->
            assertEqual
                (Snapshot.reference leftSnapshot)
                requested
                "The divergent stale request reported the wrong requested reference."

            assertEqual
                (Snapshot.reference rightSnapshot)
                actual
                "The divergent stale request reported the wrong actual reference."
        | TransactionResult.Rejected otherError ->
            failwith $"Divergent snapshots produced the wrong error: {otherError}."
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            failwith
                $"A request for the left branch committed {StableId.value (Snapshot.id committedSnapshot)} on the right branch with {semanticDiff}."
        | TransactionResult.Incomplete(committedSnapshot, semanticDiff, blockingObligations) ->
            failwith
                $"A request for the left branch committed incomplete snapshot {StableId.value (Snapshot.id committedSnapshot)} on the right branch with {semanticDiff} and {blockingObligations}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"A request for the left branch returned no change at {StableId.value (Snapshot.id unchangedSnapshot)}."

    let private testEquivalentOperationsDeriveDeterministicObligationIds () =
        let firstSnapshot, firstTargetFunctionId, _, _ = createBaseline ()
        let secondSnapshot, secondTargetFunctionId, _, _ = createBaseline ()

        let parameter: Parameter =
            { Id = parameterId "parameter:deterministic"
              DisplayName = "deterministic" }

        let createRequest snapshot targetFunctionId =
            createAddParameterRequest
                (Snapshot.reference snapshot)
                (snapshotId "snapshot:deterministic")
                targetFunctionId
                (transactionId "transaction:deterministic")
                parameter

        let requireObligationId result =
            match result with
            | TransactionResult.Incomplete(_, _, [ obligation ]) ->
                obligation.Id
            | TransactionResult.Incomplete(_, _, obligations) ->
                failwith $"Expected one blocking obligation, found {obligations}."
            | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
                failwith
                    $"Expected an incomplete snapshot, committed {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff}."
            | TransactionResult.NoChange unchangedSnapshot ->
                failwith
                    $"Expected an incomplete snapshot, received no change at {StableId.value (Snapshot.id unchangedSnapshot)}."
            | TransactionResult.Rejected operationError ->
                failwith $"Expected an incomplete snapshot, received {operationError}."

        let firstObligationId =
            Transactions.addParameter
                (createRequest firstSnapshot firstTargetFunctionId)
                firstSnapshot
            |> requireObligationId

        let secondObligationId =
            Transactions.addParameter
                (createRequest secondSnapshot secondTargetFunctionId)
                secondSnapshot
            |> requireObligationId

        assertEqual
            firstObligationId
            secondObligationId
            "Equivalent parameter additions derived different obligation IDs."

    let private testReusedSnapshotIdIsRejected () =
        let snapshot, validateUserId, _, _ = createBaseline ()

        let request =
            createRenameRequest
                (Snapshot.reference snapshot)
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
                (Snapshot.reference snapshot)
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

            assertEqual
                1
                (Snapshot.revision committedSnapshot)
                "Adding a parameter did not increment the revision."

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
                SemanticChange.ObligationCreated createdObligationId
                SemanticChange.ExecutionReadinessChanged(
                    ExecutionReadiness.Executable,
                    ExecutionReadiness.Incomplete
                ) ] ->
                assertEqual
                    validateUserId
                    changedFunctionId
                    "Parameter-added diff referenced the wrong function."

                assertEqual
                    policyParameter.Id
                    changedParameterId
                    "Parameter-added diff referenced the wrong parameter."

                assertEqual
                    (List.exactlyOne blockingObligations).Id
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

    let private createIncompletePolicySnapshot () =
        let snapshot, validateUserId, _, validateCallId = createBaseline ()

        let policyParameter: Parameter =
            { Id = parameterId "parameter:policy"
              DisplayName = "policy" }

        let addRequest =
            createAddParameterRequest
                (Snapshot.reference snapshot)
                (snapshotId "snapshot:policy-incomplete")
                validateUserId
                (transactionId "transaction:add-policy")
                policyParameter

        match Transactions.addParameter addRequest snapshot with
        | TransactionResult.Incomplete(committedSnapshot, _, [ obligation ]) ->
            committedSnapshot, policyParameter, validateCallId, obligation
        | TransactionResult.Incomplete(_, _, blockingObligations) ->
            failwith $"Expected one blocking obligation, found {blockingObligations}."
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            failwith
                $"Adding the policy parameter unexpectedly committed executable snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"Adding the policy parameter unexpectedly produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."
        | TransactionResult.Rejected operationError ->
            failwith $"Adding the policy parameter was rejected: {operationError}."

    let private assertCallHasNoArgumentBinding
        (callId: CallId)
        (parameterId: ParameterId)
        (snapshot: Snapshot)
        message
        =
        let matchingCalls =
            Snapshot.allCalls snapshot
            |> List.map snd
            |> List.filter (fun call -> call.Id = callId)

        match matchingCalls with
        | [ call ] ->
            assertEqual None (Map.tryFind parameterId call.ArgumentBindings) message
        | [] ->
            failwith $"Expected call {StableId.value callId} to remain in the base snapshot."
        | firstCall :: remainingCalls ->
            failwith
                $"Expected one call {StableId.value callId}, found {firstCall}; {remainingCalls}."

    let private testResolveMissingArgumentRestoresExecutable () =
        let incompleteSnapshot, policyParameter, validateCallId, obligation =
            createIncompletePolicySnapshot ()

        let policyValueId = valueId "value:policy"
        let resolveTransactionId = transactionId "transaction:resolve-policy"

        let request =
            createResolveMissingArgumentRequest
                (Snapshot.reference incompleteSnapshot)
                (snapshotId "snapshot:policy-resolved")
                obligation.Id
                policyValueId
                resolveTransactionId

        match Transactions.resolveMissingArgument request incompleteSnapshot with
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            assertEqual
                2
                (Snapshot.revision committedSnapshot)
                "Resolving the final obligation did not increment the revision."

            assertEqual
                []
                (Snapshot.openObligations committedSnapshot)
                "Resolving the final obligation left a blocking obligation open."

            let matchingCalls =
                Snapshot.allCalls committedSnapshot
                |> List.map snd
                |> List.filter (fun call -> call.Id = validateCallId)

            match matchingCalls with
            | [ resolvedCall ] ->
                assertEqual
                    (Some
                        { ParameterId = policyParameter.Id
                          ValueId = policyValueId })
                    (Map.tryFind policyParameter.Id resolvedCall.ArgumentBindings)
                    "Resolution did not attach the requested value to the missing parameter."
            | [] ->
                failwith "Resolution removed the affected call."
            | firstCall :: remainingCalls ->
                failwith
                    $"Expected one affected call after resolution, found {firstCall}; {remainingCalls}."

            match Snapshot.tryFindObligation obligation.Id committedSnapshot with
            | Some resolvedObligation ->
                match resolvedObligation.Status with
                | ObligationStatus.Open ->
                    failwith "Resolution left the obligation open."
                | ObligationStatus.Resolved resolution ->
                    assertEqual
                        resolveTransactionId
                        resolution.TransactionId
                        "Resolution provenance recorded the wrong transaction."

                    assertEqual
                        policyValueId
                        resolution.ValueId
                        "Resolution provenance recorded the wrong value."
            | None ->
                failwith "Resolution removed the obligation instead of retaining its history."

            assertEqual
                [ SemanticChange.ArgumentBound(
                      validateCallId,
                      policyParameter.Id,
                      policyValueId
                  )
                  SemanticChange.ObligationResolved obligation.Id
                  SemanticChange.ExecutionReadinessChanged(
                      ExecutionReadiness.Incomplete,
                      ExecutionReadiness.Executable
                  ) ]
                semanticDiff
                "Resolution produced the wrong semantic diff."

            match Snapshot.tryFindObligation obligation.Id incompleteSnapshot with
            | Some originalObligation ->
                assertEqual
                    ObligationStatus.Open
                    originalObligation.Status
                    "Resolution mutated the base snapshot obligation."
            | None ->
                failwith "The base snapshot lost its obligation during resolution."
        | TransactionResult.Incomplete(committedSnapshot, semanticDiff, blockingObligations) ->
            failwith
                $"Resolving the final obligation left incomplete snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff} and {blockingObligations}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"Resolving an open obligation unexpectedly produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."
        | TransactionResult.Rejected operationError ->
            failwith $"Resolving an open obligation was rejected: {operationError}."

    let private testResolveMissingArgumentRejectsUnknownValue () =
        let incompleteSnapshot, policyParameter, validateCallId, obligation =
            createIncompletePolicySnapshot ()

        let request =
            createResolveMissingArgumentRequest
                (Snapshot.reference incompleteSnapshot)
                (snapshotId "snapshot:policy-unknown-value")
                obligation.Id
                (valueId "value:unknown")
                (transactionId "transaction:resolve-unknown-value")

        match Transactions.resolveMissingArgument request incompleteSnapshot with
        | TransactionResult.Rejected(OperationError.ValueNotFound missingValueId) ->
            assertEqual
                (valueId "value:unknown")
                missingValueId
                "Unknown-value rejection reported the wrong value ID."

            assertEqual
                [ obligation ]
                (Snapshot.openObligations incompleteSnapshot)
                "Unknown-value resolution mutated the base snapshot."

            assertCallHasNoArgumentBinding
                validateCallId
                policyParameter.Id
                incompleteSnapshot
                "Unknown-value resolution added an argument binding to the base snapshot."
        | TransactionResult.Rejected otherError ->
            failwith $"Unknown-value resolution produced the wrong error: {otherError}."
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            failwith
                $"Unknown-value resolution committed {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff}."
        | TransactionResult.Incomplete(committedSnapshot, semanticDiff, blockingObligations) ->
            failwith
                $"Unknown-value resolution committed incomplete snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff} and {blockingObligations}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"Unknown-value resolution produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."

    let private testResolveMissingArgumentRejectsUnknownObligation () =
        let incompleteSnapshot, policyParameter, validateCallId, obligation =
            createIncompletePolicySnapshot ()

        let missingObligationId = obligationId "obligation:missing"

        let request =
            createResolveMissingArgumentRequest
                (Snapshot.reference incompleteSnapshot)
                (Snapshot.id incompleteSnapshot)
                missingObligationId
                (valueId "value:unknown")
                (transactionId "transaction:resolve-missing-obligation")

        match Transactions.resolveMissingArgument request incompleteSnapshot with
        | TransactionResult.Rejected(OperationError.ObligationNotFound reportedObligationId) ->
            assertEqual
                missingObligationId
                reportedObligationId
                "Unknown-obligation rejection reported the wrong obligation ID."

            assertEqual
                [ obligation ]
                (Snapshot.openObligations incompleteSnapshot)
                "Unknown-obligation resolution mutated the base snapshot."

            assertCallHasNoArgumentBinding
                validateCallId
                policyParameter.Id
                incompleteSnapshot
                "Unknown-obligation resolution added an argument binding to the base snapshot."
        | TransactionResult.Rejected otherError ->
            failwith $"Unknown-obligation resolution produced the wrong error: {otherError}."
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            failwith
                $"Unknown-obligation resolution committed {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff}."
        | TransactionResult.Incomplete(committedSnapshot, semanticDiff, blockingObligations) ->
            failwith
                $"Unknown-obligation resolution committed incomplete snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff} and {blockingObligations}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"Unknown-obligation resolution produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."

    let private testResolveMissingArgumentRejectsReusedSnapshotId () =
        let incompleteSnapshot, policyParameter, validateCallId, obligation =
            createIncompletePolicySnapshot ()

        let request =
            createResolveMissingArgumentRequest
                (Snapshot.reference incompleteSnapshot)
                (Snapshot.id incompleteSnapshot)
                obligation.Id
                (valueId "value:unknown")
                (transactionId "transaction:resolve-reused-snapshot")

        match Transactions.resolveMissingArgument request incompleteSnapshot with
        | TransactionResult.Rejected(OperationError.ReusedSnapshotId reportedSnapshotId) ->
            assertEqual
                (Snapshot.id incompleteSnapshot)
                reportedSnapshotId
                "Reused-snapshot resolution reported the wrong snapshot ID."

            assertEqual
                [ obligation ]
                (Snapshot.openObligations incompleteSnapshot)
                "Reused-snapshot resolution mutated the base snapshot."

            assertCallHasNoArgumentBinding
                validateCallId
                policyParameter.Id
                incompleteSnapshot
                "Reused-snapshot resolution added an argument binding to the base snapshot."
        | TransactionResult.Rejected otherError ->
            failwith $"Reused-snapshot resolution produced the wrong error: {otherError}."
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            failwith
                $"Reused-snapshot resolution committed {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff}."
        | TransactionResult.Incomplete(committedSnapshot, semanticDiff, blockingObligations) ->
            failwith
                $"Reused-snapshot resolution committed incomplete snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff} and {blockingObligations}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"Reused-snapshot resolution produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."

    let private testResolveMissingArgumentRejectsResolvedObligation () =
        let incompleteSnapshot, _, _, obligation = createIncompletePolicySnapshot ()
        let policyValueId = valueId "value:policy"

        let firstRequest =
            createResolveMissingArgumentRequest
                (Snapshot.reference incompleteSnapshot)
                (snapshotId "snapshot:policy-first-resolution")
                obligation.Id
                policyValueId
                (transactionId "transaction:resolve-first")

        let resolvedSnapshot =
            match Transactions.resolveMissingArgument firstRequest incompleteSnapshot with
            | TransactionResult.Executable(committedSnapshot, _) ->
                committedSnapshot
            | TransactionResult.Incomplete(committedSnapshot, semanticDiff, blockingObligations) ->
                failwith
                    $"Resolving the only obligation left incomplete snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff} and {blockingObligations}."
            | TransactionResult.NoChange unchangedSnapshot ->
                failwith
                    $"Resolving the only obligation unexpectedly produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."
            | TransactionResult.Rejected operationError ->
                failwith $"Resolving the only obligation was rejected: {operationError}."

        let repeatedRequest =
            createResolveMissingArgumentRequest
                (Snapshot.reference resolvedSnapshot)
                (Snapshot.id resolvedSnapshot)
                obligation.Id
                (valueId "value:unknown")
                (transactionId "transaction:resolve-second")

        match Transactions.resolveMissingArgument repeatedRequest resolvedSnapshot with
        | TransactionResult.Rejected(OperationError.ObligationAlreadyResolved resolvedObligationId) ->
            assertEqual
                obligation.Id
                resolvedObligationId
                "Resolved-obligation rejection reported the wrong obligation."
        | TransactionResult.Rejected otherError ->
            failwith $"Resolved-obligation resolution produced the wrong error: {otherError}."
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            failwith
                $"Resolved-obligation resolution committed {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff}."
        | TransactionResult.Incomplete(committedSnapshot, semanticDiff, blockingObligations) ->
            failwith
                $"Resolved-obligation resolution committed incomplete snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff} and {blockingObligations}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"Resolved-obligation resolution produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."

    let private testResolveMissingArgumentRejectsStaleBase () =
        let incompleteSnapshot, _, _, obligation = createIncompletePolicySnapshot ()

        let staleBase =
            { Snapshot.reference incompleteSnapshot with
                Revision = 0 }

        let request =
            createResolveMissingArgumentRequest
                staleBase
                (snapshotId "snapshot:policy-stale")
                obligation.Id
                (valueId "value:unknown")
                (transactionId "transaction:resolve-stale")

        match Transactions.resolveMissingArgument request incompleteSnapshot with
        | TransactionResult.Rejected(OperationError.StaleBaseSnapshot(requested, actual)) ->
            assertEqual staleBase requested "Stale resolution reported the wrong base reference."

            assertEqual
                (Snapshot.reference incompleteSnapshot)
                actual
                "Stale resolution reported the wrong current reference."
        | TransactionResult.Rejected otherError ->
            failwith $"Stale resolution produced the wrong error: {otherError}."
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            failwith
                $"Stale resolution committed {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff}."
        | TransactionResult.Incomplete(committedSnapshot, semanticDiff, blockingObligations) ->
            failwith
                $"Stale resolution committed incomplete snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff} and {blockingObligations}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"Stale resolution produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."

    let private testResolveMissingArgumentRejectsRevisionLimit () =
        let baselineSnapshot, validateUserId, _, validateCallId = createBaseline ()

        let nearLimitSnapshot =
            Snapshot.create
                (snapshotId "snapshot:policy-near-limit")
                (Int32.MaxValue - 1)
                (Snapshot.values baselineSnapshot)
                (Snapshot.functions baselineSnapshot)
            |> requireSnapshot

        let policyParameter: Parameter =
            { Id = parameterId "parameter:policy"
              DisplayName = "policy" }

        let maximumRevisionSnapshot, obligation =
            match
                Transactions.addParameter
                    (createAddParameterRequest
                        (Snapshot.reference nearLimitSnapshot)
                        (snapshotId "snapshot:policy-at-limit")
                        validateUserId
                        (transactionId "transaction:add-policy-at-limit")
                        policyParameter)
                    nearLimitSnapshot
            with
            | TransactionResult.Incomplete(committedSnapshot, _, [ createdObligation ]) ->
                committedSnapshot, createdObligation
            | TransactionResult.Incomplete(_, _, blockingObligations) ->
                failwith $"Expected one blocking obligation at the revision limit, found {blockingObligations}."
            | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
                failwith
                    $"Adding the limit parameter unexpectedly committed executable snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff}."
            | TransactionResult.NoChange unchangedSnapshot ->
                failwith
                    $"Adding the limit parameter unexpectedly produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."
            | TransactionResult.Rejected operationError ->
                failwith $"Adding the limit parameter was rejected: {operationError}."

        assertEqual
            Int32.MaxValue
            (Snapshot.revision maximumRevisionSnapshot)
            "Adding the parameter did not reach the maximum revision."

        let request =
            createResolveMissingArgumentRequest
                (Snapshot.reference maximumRevisionSnapshot)
                (snapshotId "snapshot:policy-overflow")
                obligation.Id
                (valueId "value:unknown")
                (transactionId "transaction:resolve-policy-overflow")

        match Transactions.resolveMissingArgument request maximumRevisionSnapshot with
        | TransactionResult.Rejected(OperationError.RevisionLimitReached revision) ->
            assertEqual
                Int32.MaxValue
                revision
                "Revision-limit resolution reported the wrong revision."

            assertEqual
                [ obligation ]
                (Snapshot.openObligations maximumRevisionSnapshot)
                "Revision-limit resolution mutated the base snapshot."

            assertCallHasNoArgumentBinding
                validateCallId
                policyParameter.Id
                maximumRevisionSnapshot
                "Revision-limit resolution added an argument binding to the base snapshot."
        | TransactionResult.Rejected otherError ->
            failwith $"Revision-limit resolution produced the wrong error: {otherError}."
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            failwith
                $"Revision-limit resolution committed {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff}."
        | TransactionResult.Incomplete(committedSnapshot, semanticDiff, blockingObligations) ->
            failwith
                $"Revision-limit resolution committed incomplete snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff} and {blockingObligations}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"Revision-limit resolution produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."

    let private testAddParameterRejectsDuplicateId () =
        let snapshot, validateUserId, rawInputId, _ = createBaseline ()

        let duplicateParameter: Parameter =
            { Id = rawInputId
              DisplayName = "replacement" }

        let request =
            createAddParameterRequest
                (Snapshot.reference snapshot)
                (snapshotId "snapshot:duplicate-parameter")
                validateUserId
                (transactionId "transaction:duplicate-parameter")
                duplicateParameter

        match Transactions.addParameter request snapshot with
        | TransactionResult.Rejected(OperationError.ParameterAlreadyExists foundParameterId) ->
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

    let private testAddParameterRejectsGlobalDuplicateId () =
        let snapshot, _, rawInputId, _ = createBaseline ()
        let mainFunctionId = functionId "function:main"

        let request =
            createAddParameterRequest
                (Snapshot.reference snapshot)
                (snapshotId "snapshot:global-duplicate-parameter")
                mainFunctionId
                (transactionId "transaction:global-duplicate-parameter")
                { Id = rawInputId
                  DisplayName = "replacement" }

        match Transactions.addParameter request snapshot with
        | TransactionResult.Rejected(OperationError.ParameterAlreadyExists foundParameterId) ->
            assertEqual
                rawInputId
                foundParameterId
                "Global duplicate-parameter rejection reported the wrong parameter."
        | TransactionResult.Rejected otherError ->
            failwith $"Global duplicate parameter produced the wrong error: {otherError}."
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            failwith
                $"Global duplicate parameter committed {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff}."
        | TransactionResult.Incomplete(committedSnapshot, semanticDiff, blockingObligations) ->
            failwith
                $"Global duplicate parameter committed incomplete snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff} and {blockingObligations}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"Global duplicate parameter produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."

    let private createObligationCollisionBaseline () =
        let resolutionValue: ValueEntity =
            { Id = valueId "value:resolution" }

        let firstTarget: FunctionEntity =
            { Id = functionId "function:first-target"
              DisplayName = "first_target"
              Parameters = []
              Calls = [] }

        let secondTarget: FunctionEntity =
            { Id = functionId "function:second-target"
              DisplayName = "second_target"
              Parameters = []
              Calls = [] }

        let firstCall: Call =
            { Id = callId "a:b"
              TargetFunctionId = firstTarget.Id
              ArgumentBindings = Map.empty }

        let secondCall: Call =
            { Id = callId "a"
              TargetFunctionId = secondTarget.Id
              ArgumentBindings = Map.empty }

        let firstCaller: FunctionEntity =
            { Id = functionId "function:first-caller"
              DisplayName = "first_caller"
              Parameters = []
              Calls = [ firstCall ] }

        let secondCaller: FunctionEntity =
            { Id = functionId "function:second-caller"
              DisplayName = "second_caller"
              Parameters = []
              Calls = [ secondCall ] }

        let snapshot =
            Snapshot.create
                (snapshotId "snapshot:obligation-collision")
                0
                [ resolutionValue ]
                [ firstTarget; secondTarget; firstCaller; secondCaller ]
            |> requireSnapshot

        snapshot, firstTarget.Id, secondTarget.Id, firstCall.Id, secondCall.Id

    let private testObligationIdsDoNotCollide () =
        let snapshot, firstTargetId, secondTargetId, firstCallId, secondCallId =
            createObligationCollisionBaseline ()

        let firstParameter: Parameter =
            { Id = parameterId "c"
              DisplayName = "first" }

        let afterFirst =
            Transactions.addParameter
                (createAddParameterRequest
                    (Snapshot.reference snapshot)
                    (snapshotId "snapshot:obligation-first")
                    firstTargetId
                    (transactionId "transaction:obligation-first")
                    firstParameter)
                snapshot
            |> requireCommittedSnapshot

        let secondParameter: Parameter =
            { Id = parameterId "b:c"
              DisplayName = "second" }

        let afterSecond =
            Transactions.addParameter
                (createAddParameterRequest
                    (Snapshot.reference afterFirst)
                    (snapshotId "snapshot:obligation-second")
                    secondTargetId
                    (transactionId "transaction:obligation-second")
                    secondParameter)
                afterFirst
            |> requireCommittedSnapshot

        let obligations = Snapshot.openObligations afterSecond

        assertEqual 2 (List.length obligations) "One colliding obligation overwrote another."

        let firstPairExists =
            obligations
            |> List.exists (fun obligation ->
                obligation.CallId = firstCallId
                && obligation.ParameterId = firstParameter.Id)

        let secondPairExists =
            obligations
            |> List.exists (fun obligation ->
                obligation.CallId = secondCallId
                && obligation.ParameterId = secondParameter.Id)

        assertEqual true firstPairExists "The first missing-argument obligation was lost."
        assertEqual true secondPairExists "The second missing-argument obligation was lost."

        match obligations with
        | [ firstObligation; secondObligation ] ->
            assertEqual
                false
                (firstObligation.Id = secondObligation.Id)
                "Distinct missing arguments received the same obligation ID."
        | [] ->
            failwith "No obligations remained after adding two parameters."
        | [ obligation ] ->
            failwith $"Only one obligation remained: {obligation}."
        | firstObligation :: secondObligation :: remainingObligations ->
            failwith
                $"Expected two obligations, found {firstObligation}; {secondObligation}; {remainingObligations}."

    let private testResolvingOneOfSeveralObligationsRemainsIncomplete () =
        let snapshot, firstTargetId, secondTargetId, firstCallId, secondCallId =
            createObligationCollisionBaseline ()

        let firstParameter: Parameter =
            { Id = parameterId "c"
              DisplayName = "first" }

        let afterFirst =
            Transactions.addParameter
                (createAddParameterRequest
                    (Snapshot.reference snapshot)
                    (snapshotId "snapshot:resolve-first")
                    firstTargetId
                    (transactionId "transaction:resolve-first")
                    firstParameter)
                snapshot
            |> requireCommittedSnapshot

        let secondParameter: Parameter =
            { Id = parameterId "b:c"
              DisplayName = "second" }

        let afterSecond =
            Transactions.addParameter
                (createAddParameterRequest
                    (Snapshot.reference afterFirst)
                    (snapshotId "snapshot:resolve-second")
                    secondTargetId
                    (transactionId "transaction:resolve-second")
                    secondParameter)
                afterFirst
            |> requireCommittedSnapshot

        let firstObligation =
            Snapshot.openObligations afterSecond
            |> List.find (fun obligation ->
                obligation.CallId = firstCallId
                && obligation.ParameterId = firstParameter.Id)

        let resolveTransactionId = transactionId "transaction:resolve-one-of-many"

        let resolveRequest =
            createResolveMissingArgumentRequest
                (Snapshot.reference afterSecond)
                (snapshotId "snapshot:resolve-one-of-many")
                firstObligation.Id
                (valueId "value:resolution")
                resolveTransactionId

        match Transactions.resolveMissingArgument resolveRequest afterSecond with
        | TransactionResult.Incomplete(committedSnapshot, semanticDiff, blockingObligations) ->
            match blockingObligations with
            | [ remainingObligation ] ->
                assertEqual
                    secondCallId
                    remainingObligation.CallId
                    "Resolving one obligation removed the wrong remaining obligation."

                assertEqual
                    secondParameter.Id
                    remainingObligation.ParameterId
                    "Resolving one obligation changed the remaining obligation parameter."
            | [] ->
                failwith "Resolving one of two obligations incorrectly restored executability."
            | firstObligation :: secondObligation :: remainingObligations ->
                failwith
                    $"Expected one remaining obligation, found {firstObligation}; {secondObligation}; {remainingObligations}."

            assertEqual
                [ SemanticChange.ArgumentBound(
                      firstCallId,
                      firstParameter.Id,
                      valueId "value:resolution"
                  )
                  SemanticChange.ObligationResolved firstObligation.Id ]
                semanticDiff
                "Resolving one of several obligations produced the wrong semantic diff."
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            failwith
                $"Resolving one of two obligations unexpectedly committed executable snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"Resolving one of two obligations unexpectedly produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."
        | TransactionResult.Rejected operationError ->
            failwith $"Resolving one of two obligations was rejected: {operationError}."

    let private testObligationChangesAreSorted () =
        let targetFunction: FunctionEntity =
            { Id = functionId "function:ordered-target"
              DisplayName = "ordered_target"
              Parameters = []
              Calls = [] }

        let laterCall: Call =
            { Id = callId "call:z"
              TargetFunctionId = targetFunction.Id
              ArgumentBindings = Map.empty }

        let earlierCall: Call =
            { Id = callId "call:a"
              TargetFunctionId = targetFunction.Id
              ArgumentBindings = Map.empty }

        let caller: FunctionEntity =
            { Id = functionId "function:ordered-caller"
              DisplayName = "ordered_caller"
              Parameters = []
              Calls = [ laterCall; earlierCall ] }

        let snapshot =
            Snapshot.create
                (snapshotId "snapshot:ordered-obligations")
                0
                []
                [ targetFunction; caller ]
            |> requireSnapshot

        let request =
            createAddParameterRequest
                (Snapshot.reference snapshot)
                (snapshotId "snapshot:ordered-obligations-next")
                targetFunction.Id
                (transactionId "transaction:ordered-obligations")
                { Id = parameterId "parameter:ordered"
                  DisplayName = "ordered" }

        match Transactions.addParameter request snapshot with
        | TransactionResult.Incomplete(_, semanticDiff, _) ->
            let createdObligationIds =
                semanticDiff
                |> List.choose (fun change ->
                    match change with
                    | SemanticChange.FunctionRenamed _ ->
                        None
                    | SemanticChange.ParameterAdded _ ->
                        None
                    | SemanticChange.ObligationCreated obligationId ->
                        Some obligationId
                    | SemanticChange.ArgumentBound _ ->
                        None
                    | SemanticChange.ObligationResolved _ ->
                        None
                    | SemanticChange.ExecutionReadinessChanged _ ->
                        None)

            let sortedObligationIds =
                createdObligationIds
                |> List.sortWith (fun left right ->
                    StringComparer.Ordinal.Compare(StableId.value left, StableId.value right))

            assertEqual 2 (List.length createdObligationIds) "Expected two obligation changes."

            assertEqual
                sortedObligationIds
                createdObligationIds
                "Obligation-created changes were not ordered by obligation ID."
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            failwith
                $"Multiple missing arguments unexpectedly committed executable snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"Multiple missing arguments unexpectedly produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."
        | TransactionResult.Rejected operationError ->
            failwith $"Multiple missing arguments were rejected: {operationError}."

    let private testRevisionLimitIsRejected () =
        let baselineSnapshot, validateUserId, _, _ = createBaseline ()

        let maximumRevisionSnapshot =
            Snapshot.create
                (snapshotId "snapshot:maximum-revision")
                Int32.MaxValue
                (Snapshot.values baselineSnapshot)
                (Snapshot.functions baselineSnapshot)
            |> requireSnapshot

        let request =
            createRenameRequest
                (Snapshot.reference maximumRevisionSnapshot)
                (snapshotId "snapshot:overflow")
                validateUserId
                "validate_profile"

        match Transactions.renameFunction request maximumRevisionSnapshot with
        | TransactionResult.Rejected(OperationError.RevisionLimitReached revision) ->
            assertEqual Int32.MaxValue revision "Revision-limit rejection reported the wrong revision."
        | TransactionResult.Rejected otherError ->
            failwith $"Revision-limit rename produced the wrong error: {otherError}."
        | TransactionResult.Executable(committedSnapshot, semanticDiff) ->
            failwith
                $"Revision-limit rename committed {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff}."
        | TransactionResult.Incomplete(committedSnapshot, semanticDiff, blockingObligations) ->
            failwith
                $"Revision-limit rename committed incomplete snapshot {StableId.value (Snapshot.id committedSnapshot)} with {semanticDiff} and {blockingObligations}."
        | TransactionResult.NoChange unchangedSnapshot ->
            failwith
                $"Revision-limit rename unexpectedly produced no change at {StableId.value (Snapshot.id unchangedSnapshot)}."

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
              "duplicate value IDs are rejected", testDuplicateValueIdsAreRejected
              "duplicate parameter IDs across functions are rejected",
              testDuplicateParameterIdsAcrossFunctionsAreRejected
              "call graph edges are validated", testCallEdgesAreValidated
              "rename preserves function identity", testRenamePreservesFunctionIdentity
              "rename to current name is no change", testRenameToCurrentNameIsNoChange
              "stale base is rejected", testStaleBaseIsRejected
              "returned snapshot IDs do not accept stale revisions",
              testReturnedSnapshotIdDoesNotAcceptStaleRevision
              "stale revisions reject parameter addition",
              testStaleRevisionRejectsParameterAddition
              "divergent snapshots do not share base references",
              testDivergentSnapshotsDoNotShareBaseReference
              "reused snapshot ID is rejected", testReusedSnapshotIdIsRejected
              "adding a parameter creates a missing-argument obligation",
              testAddParameterCreatesMissingArgumentObligation
              "resolving a missing argument restores executability", testResolveMissingArgumentRestoresExecutable
              "resolving a missing argument rejects unknown values", testResolveMissingArgumentRejectsUnknownValue
              "resolving a missing argument rejects unknown obligations", testResolveMissingArgumentRejectsUnknownObligation
              "resolving a missing argument rejects reused snapshot IDs", testResolveMissingArgumentRejectsReusedSnapshotId
              "resolving a missing argument rejects resolved obligations", testResolveMissingArgumentRejectsResolvedObligation
              "resolving a missing argument rejects stale bases", testResolveMissingArgumentRejectsStaleBase
              "resolving a missing argument rejects revision limits", testResolveMissingArgumentRejectsRevisionLimit
              "adding a parameter rejects duplicate IDs", testAddParameterRejectsDuplicateId
              "adding a parameter rejects global duplicate IDs",
              testAddParameterRejectsGlobalDuplicateId
              "obligation IDs do not collide", testObligationIdsDoNotCollide
              "resolving one of several obligations remains incomplete",
              testResolvingOneOfSeveralObligationsRemainsIncomplete
              "equivalent operations derive deterministic obligation IDs",
              testEquivalentOperationsDeriveDeterministicObligationIds
              "obligation changes are sorted", testObligationChangesAreSorted
              "revision limits are rejected", testRevisionLimitIsRejected ]

        let allPassed =
            tests
            |> List.map runTest
            |> List.forall (fun passed -> passed)

        if allPassed then 0 else 1
