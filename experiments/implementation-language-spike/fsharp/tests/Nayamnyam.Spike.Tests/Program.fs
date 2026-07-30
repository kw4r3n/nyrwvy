namespace Nayamnyam.Spike.Tests

open System
open System.IO
open Nayamnyam.Spike

module Program =
    let rec private findFixture (directory: DirectoryInfo) =
        let candidate =
            Path.Combine(
                directory.FullName,
                "shared",
                "public",
                "fixtures",
                "initial-snapshot-v1.json"
            )

        if File.Exists candidate then
            candidate
        elif isNull directory.Parent then
            Assertions.fail
                "Could not locate shared/public/fixtures/initial-snapshot-v1.json."
        else
            findFixture directory.Parent

    let private fixturePath =
        findFixture (DirectoryInfo AppContext.BaseDirectory)

    let private loadFixture () =
        File.ReadAllText fixturePath |> CanonicalJson.readSnapshot

    let private newParameter () =
        {
            Id = ParameterId.create "parameter:context"
            Name = "context"
            TypeId = TypeId.create "type:string"
        }

    let private stableIdsRejectBlankStrings () =
        Assertions.throwsArgument
            (fun () ->
                let id: FunctionId = StableId.create " "
                ignore id)
            "A blank stable ID must be rejected."

    let private stableIdsRejectInvalidDomainsAndTails () =
        let invalidIds =
            [
                (fun () ->
                    StableId.create<FunctionTag> "type:wrong-domain"
                    |> ignore)
                (fun () ->
                    StableId.create<FunctionTag> "function:"
                    |> ignore)
                (fun () ->
                    StableId.create<FunctionTag> "function:two:tails"
                    |> ignore)
                (fun () ->
                    StableId.create<FunctionTag> "function:line\nbreak"
                    |> ignore)
                (fun () ->
                    StableId.create<ObligationTag> "obligation:too:few"
                    |> ignore)
                (fun () ->
                    StableId.create<ObligationTag>
                        "obligation:too:many:tail:parts"
                    |> ignore)
            ]

        invalidIds
        |> List.iter (fun createInvalid ->
            Assertions.throwsArgument
                createInvalid
                "An invalid stable-ID domain or tail was accepted.")

    let private assertFunctionRenamedChange
        expectedFunctionId
        expectedOldName
        expectedNewName
        change
        =
        match change with
        | FunctionRenamed (functionId, oldName, newName) ->
            Assertions.equal
                expectedFunctionId
                functionId
                "Rename change contains the wrong function ID."

            Assertions.equal
                expectedOldName
                oldName
                "Rename change contains the wrong old name."

            Assertions.equal
                expectedNewName
                newName
                "Rename change contains the wrong new name."
        | ParameterAdded (functionId, parameterId) ->
            Assertions.fail
                $"Expected FunctionRenamed, found ParameterAdded({StableId.value functionId}, {StableId.value parameterId})."
        | ObligationCreated obligationId ->
            Assertions.fail
                $"Expected FunctionRenamed, found ObligationCreated({StableId.value obligationId})."

    let private assertParameterAddedChange
        expectedFunctionId
        expectedParameterId
        change
        =
        match change with
        | FunctionRenamed (functionId, oldName, newName) ->
            Assertions.fail
                $"Expected ParameterAdded, found FunctionRenamed({StableId.value functionId}, {oldName}, {newName})."
        | ParameterAdded (functionId, parameterId) ->
            Assertions.equal
                expectedFunctionId
                functionId
                "ParameterAdded contains the wrong function ID."

            Assertions.equal
                expectedParameterId
                parameterId
                "ParameterAdded contains the wrong parameter ID."
        | ObligationCreated obligationId ->
            Assertions.fail
                $"Expected ParameterAdded, found ObligationCreated({StableId.value obligationId})."

    let private assertObligationCreatedChange expectedObligationId change =
        match change with
        | FunctionRenamed (functionId, oldName, newName) ->
            Assertions.fail
                $"Expected ObligationCreated, found FunctionRenamed({StableId.value functionId}, {oldName}, {newName})."
        | ParameterAdded (functionId, parameterId) ->
            Assertions.fail
                $"Expected ObligationCreated, found ParameterAdded({StableId.value functionId}, {StableId.value parameterId})."
        | ObligationCreated obligationId ->
            Assertions.equal
                expectedObligationId
                obligationId
                "ObligationCreated contains the wrong obligation ID."

    let private canonicalFixtureRoundTripsByteForByte () =
        let expected = File.ReadAllText fixturePath
        let actual = loadFixture () |> CanonicalJson.writeSnapshot
        Assertions.equal expected actual "Canonical fixture bytes changed."

    let private initialScenarioMatchesCanonicalFixture () =
        let expected = File.ReadAllText fixturePath
        let actual = CanonicalJson.writeSnapshot Scenarios.initialSnapshot
        Assertions.equal expected actual "Initial scenario does not match the shared fixture."

    let private fixtureSummaryMatchesExpectedBytes () =
        let expected =
            String.concat
                "\n"
                [
                    "{"
                    "  \"schema_version\": 1,"
                    "  \"snapshot_id\": \"snapshot:initial\","
                    "  \"type_count\": 3,"
                    "  \"function_count\": 2,"
                    "  \"call_count\": 1,"
                    "  \"obligation_count\": 0"
                    "}"
                    ""
                ]

        let actual = loadFixture () |> CanonicalJson.writeSummary
        Assertions.equal expected actual "Summary bytes changed."

    let private functionRenamePreservesIdAndCallTarget () =
        let snapshot = loadFixture ()
        let originalJson = CanonicalJson.writeSnapshot snapshot
        let functionId = FunctionId.create "function:validate-user"
        let transactionId = TransactionId.create "transaction:rename-validate"

        let result =
            Operations.renameFunction
                snapshot
                snapshot.Id
                transactionId
                functionId
                "validate_account"

        match result with
        | Rejected (baseSnapshotId, diagnostics) ->
            Assertions.fail
                $"Rename was rejected for {StableId.value baseSnapshotId} with {List.length diagnostics} diagnostics."
        | Incomplete (candidate, diff, blocking) ->
            Assertions.fail
                $"Rename unexpectedly returned incomplete snapshot {StableId.value candidate.Id} with {List.length diff} changes and {List.length blocking} obligations."
        | Executable (candidate, diff) ->
            let renamed =
                candidate.Functions
                |> List.find (fun functionEntity -> functionEntity.Id = functionId)

            let originalCall = List.exactlyOne snapshot.Calls
            let candidateCall = List.exactlyOne candidate.Calls
            Assertions.equal functionId renamed.Id "Function ID changed during rename."
            Assertions.equal "validate_account" renamed.Name "Function name was not changed."
            Assertions.equal originalCall.CalleeId candidateCall.CalleeId "Call target changed."
            Assertions.equal functionId candidateCall.CalleeId "Call no longer targets the renamed function."
            Assertions.equal 1 (List.length diff) "Rename semantic diff must contain one change."

            diff
            |> List.exactlyOne
            |> assertFunctionRenamedChange
                functionId
                "validate_user"
                "validate_account"

            Assertions.equal
                originalJson
                (CanonicalJson.writeSnapshot snapshot)
                "Rename mutated the input snapshot."

    let private staleBaseRenameRejectsWithoutMutation () =
        let snapshot = loadFixture ()
        let originalFunctions = snapshot.Functions
        let originalCalls = snapshot.Calls
        let originalJson = CanonicalJson.writeSnapshot snapshot

        let result =
            Operations.renameFunction
                snapshot
                (SnapshotId.create "snapshot:stale")
                (TransactionId.create "transaction:stale-rename")
                (FunctionId.create "function:validate-user")
                "ignored"

        match result with
        | Rejected (baseSnapshotId, diagnostics) ->
            Assertions.equal
                (SnapshotId.create "snapshot:stale")
                baseSnapshotId
                "Rejected result did not preserve the supplied base snapshot ID."

            Assertions.isTrue
                (not (List.isEmpty diagnostics))
                "Stale-base rejection must include a diagnostic."
        | Incomplete (candidate, diff, blocking) ->
            Assertions.fail
                $"Stale rename produced incomplete snapshot {StableId.value candidate.Id} with {List.length diff} changes and {List.length blocking} obligations."
        | Executable (candidate, diff) ->
            Assertions.fail
                $"Stale rename produced executable snapshot {StableId.value candidate.Id} with {List.length diff} changes."

        Assertions.same originalFunctions snapshot.Functions "Original function graph was replaced."
        Assertions.same originalCalls snapshot.Calls "Original call graph was replaced."
        Assertions.equal originalJson (CanonicalJson.writeSnapshot snapshot) "Original snapshot mutated."

    let private addParameterAppendsExactlyOneParameter () =
        let snapshot = loadFixture ()
        let originalJson = CanonicalJson.writeSnapshot snapshot
        let functionId = FunctionId.create "function:validate-user"

        let result =
            Operations.addParameter
                snapshot
                snapshot.Id
                (TransactionId.create "transaction:add-context")
                functionId
                (newParameter ())

        match result with
        | Rejected (baseSnapshotId, diagnostics) ->
            Assertions.fail
                $"Add parameter was rejected for {StableId.value baseSnapshotId} with {List.length diagnostics} diagnostics."
        | Incomplete (candidate, diff, blocking) ->
            let changed =
                candidate.Functions
                |> List.find (fun functionEntity -> functionEntity.Id = functionId)

            Assertions.equal 2 (List.length changed.Parameters) "Parameter count did not increase by one."
            Assertions.equal
                (ParameterId.create "parameter:context")
                (List.last changed.Parameters).Id
                "New parameter was not appended."

            Assertions.equal 2 (List.length diff) "Add-parameter diff has the wrong length."
            Assertions.equal 1 (List.length blocking) "Add-parameter blocking list has the wrong length."

            assertParameterAddedChange
                functionId
                (ParameterId.create "parameter:context")
                (List.head diff)

            assertObligationCreatedChange
                (ObligationId.create
                    "obligation:add-context:main-validate:context")
                (List.last diff)

            Assertions.equal
                originalJson
                (CanonicalJson.writeSnapshot snapshot)
                "Add parameter mutated the input snapshot."
        | Executable (candidate, diff) ->
            Assertions.fail
                $"Add parameter unexpectedly returned executable snapshot {StableId.value candidate.Id} with {List.length diff} changes."

    let private addParameterSortsObligationChangesOrdinally () =
        let fixture = loadFixture ()
        let originalCall = List.exactlyOne fixture.Calls

        let snapshot =
            {
                fixture with
                    Calls =
                        [
                            originalCall
                            {
                                originalCall with
                                    Id = CallId.create "call:aaa"
                            }
                        ]
            }

        let functionId = FunctionId.create "function:validate-user"

        let result =
            Operations.addParameter
                snapshot
                snapshot.Id
                (TransactionId.create "transaction:multi")
                functionId
                (newParameter ())

        match result with
        | Rejected (baseSnapshotId, diagnostics) ->
            Assertions.fail
                $"Multi-call add parameter was rejected for {StableId.value baseSnapshotId} with {List.length diagnostics} diagnostics."
        | Incomplete (candidate, diff, blocking) ->
            ignore candidate
            Assertions.equal 3 (List.length diff) "Multi-call semantic diff has the wrong length."

            assertParameterAddedChange
                functionId
                (ParameterId.create "parameter:context")
                (List.head diff)

            assertObligationCreatedChange
                (ObligationId.create
                    "obligation:multi:aaa:context")
                (List.item 1 diff)

            assertObligationCreatedChange
                (ObligationId.create
                    "obligation:multi:main-validate:context")
                (List.item 2 diff)

            Assertions.equal
                [
                    ObligationId.create
                        "obligation:multi:aaa:context"
                    ObligationId.create
                        "obligation:multi:main-validate:context"
                ]
                blocking
                "Multi-call blocking IDs are not ordinally sorted."
        | Executable (candidate, diff) ->
            Assertions.fail
                $"Multi-call add parameter unexpectedly returned executable snapshot {StableId.value candidate.Id} with {List.length diff} changes."

    let private addParameterCreatesMissingArgumentObligation () =
        let snapshot = loadFixture ()

        let result =
            Operations.addParameter
                snapshot
                snapshot.Id
                (TransactionId.create "transaction:add-context")
                (FunctionId.create "function:validate-user")
                (newParameter ())

        match result with
        | Rejected (baseSnapshotId, diagnostics) ->
            Assertions.fail
                $"Add parameter was rejected for {StableId.value baseSnapshotId} with {List.length diagnostics} diagnostics."
        | Incomplete (candidate, diff, blocking) ->
            let obligation = List.exactlyOne candidate.Obligations

            Assertions.equal
                (ObligationId.create "obligation:add-context:main-validate:context")
                obligation.Id
                "Derived obligation ID is incorrect."

            match obligation.Kind with
            | MissingArgument (callId, parameterId) ->
                Assertions.equal
                    (CallId.create "call:main-validate")
                    callId
                    "Missing-argument obligation points to the wrong call."

                Assertions.equal
                    (ParameterId.create "parameter:context")
                    parameterId
                    "Missing-argument obligation points to the wrong parameter."

            Assertions.equal 2 (List.length diff) "Missing-argument semantic diff is incomplete."
            Assertions.equal [ obligation.Id ] blocking "Blocking obligation IDs are incorrect."
        | Executable (candidate, diff) ->
            Assertions.fail
                $"Add parameter unexpectedly returned executable snapshot {StableId.value candidate.Id} with {List.length diff} changes."

    let private addParameterReturnsIncomplete () =
        let snapshot = loadFixture ()

        let result =
            Operations.addParameter
                snapshot
                snapshot.Id
                (TransactionId.create "transaction:add-context")
                (FunctionId.create "function:validate-user")
                (newParameter ())

        match result with
        | Rejected (baseSnapshotId, diagnostics) ->
            Assertions.fail
                $"Add parameter was rejected for {StableId.value baseSnapshotId} with {List.length diagnostics} diagnostics."
        | Incomplete (candidate, diff, blocking) ->
            Assertions.equal
                (SnapshotId.create "snapshot:add-context")
                candidate.Id
                "Candidate snapshot ID is incorrect."

            Assertions.equal 2 (List.length diff) "Incomplete semantic diff is incorrect."
            Assertions.equal 1 (List.length blocking) "Incomplete blocking list is incorrect."
        | Executable (candidate, diff) ->
            Assertions.fail
                $"Add parameter unexpectedly returned executable snapshot {StableId.value candidate.Id} with {List.length diff} changes."

    let private constructionOrderDoesNotChangeCanonicalOutput () =
        let fixture = loadFixture ()
        let functionId = FunctionId.create "function:validate-user"

        let orderedParameters: Parameter list =
            [
                {
                    Id = ParameterId.create "parameter:zeta"
                    Name = "zeta"
                    TypeId = TypeId.create "type:string"
                }
                {
                    Id = ParameterId.create "parameter:input"
                    Name = "input"
                    TypeId = TypeId.create "type:raw-user-input"
                }
                {
                    Id = ParameterId.create "parameter:alpha"
                    Name = "alpha"
                    TypeId = TypeId.create "type:string"
                }
            ]

        let functions =
            fixture.Functions
            |> List.map (fun functionEntity ->
                if functionEntity.Id = functionId then
                    {
                        functionEntity with
                            Parameters = orderedParameters
                    }
                else
                    functionEntity)

        let calls: CallEntity list =
            [
                {
                    Id = CallId.create "call:z"
                    CalleeId = functionId
                    Arguments =
                        [
                            {
                                ParameterId =
                                    ParameterId.create
                                        "parameter:input"
                                ValueRef = "local:input-z"
                            }
                            {
                                ParameterId =
                                    ParameterId.create
                                        "parameter:alpha"
                                ValueRef = "local:alpha-z"
                            }
                        ]
                }
                {
                    Id = CallId.create "call:a"
                    CalleeId = functionId
                    Arguments =
                        [
                            {
                                ParameterId =
                                    ParameterId.create
                                        "parameter:zeta"
                                ValueRef = "local:zeta-a"
                            }
                            {
                                ParameterId =
                                    ParameterId.create
                                        "parameter:input"
                                ValueRef = "local:input-a"
                            }
                        ]
                }
            ]

        let obligations: Obligation list =
            [
                {
                    Id =
                        ObligationId.create
                            "obligation:order:z:zeta"
                    Kind =
                        MissingArgument(
                            CallId.create "call:z",
                            ParameterId.create "parameter:zeta"
                        )
                    Status = Open
                }
                {
                    Id =
                        ObligationId.create
                            "obligation:order:a:alpha"
                    Kind =
                        MissingArgument(
                            CallId.create "call:a",
                            ParameterId.create "parameter:alpha"
                        )
                    Status = Open
                }
            ]

        let firstConstruction =
            {
                fixture with
                    Types = List.rev fixture.Types
                    Functions = List.rev functions
                    Calls = calls
                    Obligations = obligations
            }

        let secondConstruction =
            {
                fixture with
                    Types = fixture.Types
                    Functions = functions
                    Calls = List.rev calls
                    Obligations = List.rev obligations
            }

        let firstJson = CanonicalJson.writeSnapshot firstConstruction

        Assertions.equal
            firstJson
            (CanonicalJson.writeSnapshot secondConstruction)
            "Snapshot-level construction order changed canonical output."

        let roundTripped = CanonicalJson.readSnapshot firstJson

        let roundTrippedFunction =
            roundTripped.Functions
            |> List.find (fun functionEntity ->
                functionEntity.Id = functionId)

        Assertions.equal
            (orderedParameters
             |> List.map (fun parameter -> parameter.Id))
            (roundTrippedFunction.Parameters
             |> List.map (fun parameter -> parameter.Id))
            "Function parameter order was sorted instead of preserved."

        let roundTrippedCall =
            roundTripped.Calls
            |> List.find (fun call ->
                call.Id = CallId.create "call:z")

        Assertions.equal
            [
                ParameterId.create "parameter:input"
                ParameterId.create "parameter:alpha"
            ]
            (roundTrippedCall.Arguments
             |> List.map (fun argument -> argument.ParameterId))
            "Call argument order was sorted instead of preserved."

    let private tests =
        [
            "stable IDs reject blank strings", stableIdsRejectBlankStrings
            "stable IDs reject invalid domains and tails", stableIdsRejectInvalidDomainsAndTails
            "canonical fixture round-trips byte-for-byte", canonicalFixtureRoundTripsByteForByte
            "initial scenario matches canonical fixture", initialScenarioMatchesCanonicalFixture
            "fixture summary matches expected bytes", fixtureSummaryMatchesExpectedBytes
            "function rename preserves FunctionId and call target", functionRenamePreservesIdAndCallTarget
            "stale-base rename rejects without mutation", staleBaseRenameRejectsWithoutMutation
            "add-parameter appends exactly one parameter", addParameterAppendsExactlyOneParameter
            "add-parameter sorts obligation changes ordinally", addParameterSortsObligationChangesOrdinally
            "add-parameter creates missing-argument obligation", addParameterCreatesMissingArgumentObligation
            "add-parameter returns incomplete", addParameterReturnsIncomplete
            "construction order does not change canonical output", constructionOrderDoesNotChangeCanonicalOutput
        ]

    let rec private run testsToRun =
        match testsToRun with
        | [] -> 0
        | (name, test) :: remaining ->
            try
                test ()
                printfn $"PASS {name}"
                run remaining
            with exceptionValue ->
                eprintfn $"FAIL {name}: {exceptionValue.Message}"
                1

    [<EntryPoint>]
    let main arguments =
        ignore arguments
        run tests
