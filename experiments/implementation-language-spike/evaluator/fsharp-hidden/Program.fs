namespace Nayamnyam.Spike.Hidden

open System
open System.IO
open System.Text.Json
open Nayamnyam.Spike

module Program =
    let private fail message =
        raise (InvalidOperationException message)

    let private ensure condition message =
        if not condition then
            fail message

    let private equal expected actual message =
        if expected <> actual then
            fail $"{message} Expected: {expected}. Actual: {actual}."

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
            fail "Could not locate the public v1 fixture."
        else
            findFixture directory.Parent

    let private fixturePath =
        findFixture (DirectoryInfo AppContext.BaseDirectory)

    let private loadInitial () =
        File.ReadAllText fixturePath
        |> CanonicalJson.readSnapshot

    let private duplicateNameParameterObligations () =
        let snapshot = loadInitial ()
        let functionId = FunctionId.create "function:validate-user"
        let existingParameterId = ParameterId.create "parameter:input"
        let addedParameterId = ParameterId.create "parameter:duplicate-input"

        let parameter: Parameter =
            {
                Id = addedParameterId
                Name = "input"
                TypeId = TypeId.create "type:string"
            }

        let result =
            Operations.addParameter
                snapshot
                snapshot.Id
                (TransactionId.create "transaction:duplicate-name")
                functionId
                parameter

        match result with
        | Incomplete (candidate, diff, blocking) ->
            let conflictIds =
                candidate.Obligations
                |> List.choose (fun obligation ->
                    match obligation.Kind with
                    | MissingArgument (callId, parameterId) ->
                        ignore callId
                        ignore parameterId
                        None
                    | ParameterNameConflict (
                        conflictFunctionId,
                        conflictExistingId,
                        conflictAddedId
                      ) ->
                        ensure
                            (conflictFunctionId = functionId)
                            "Conflict obligation has the wrong function ID."

                        ensure
                            (conflictExistingId = existingParameterId)
                            "Conflict obligation has the wrong existing parameter ID."

                        ensure
                            (conflictAddedId = addedParameterId)
                            "Conflict obligation has the wrong added parameter ID."

                        Some obligation.Id)

            let missingIds =
                candidate.Obligations
                |> List.choose (fun obligation ->
                    match obligation.Kind with
                    | MissingArgument (callId, parameterId) ->
                        ensure
                            (callId = CallId.create "call:main-validate")
                            "Missing-argument obligation has the wrong call ID."

                        ensure
                            (parameterId = addedParameterId)
                            "Missing-argument obligation has the wrong parameter ID."

                        Some obligation.Id
                    | ParameterNameConflict (
                        conflictFunctionId,
                        conflictExistingId,
                        conflictAddedId
                      ) ->
                        ignore conflictFunctionId
                        ignore conflictExistingId
                        ignore conflictAddedId
                        None)

            equal 1 (List.length conflictIds) "Expected one conflict obligation."
            equal 1 (List.length missingIds) "Expected one missing-argument obligation."
            equal 3 (List.length diff) "Duplicate-name add diff has the wrong size."

            ensure
                (List.forall (fun id -> List.contains id blocking) (conflictIds @ missingIds))
                "Created obligations are not all blocking."
        | Rejected (baseSnapshotId, diagnostics) ->
            fail
                $"Duplicate-name add was rejected for {StableId.value baseSnapshotId} with {List.length diagnostics} diagnostics."
        | Executable (candidate, diff) ->
            fail
                $"Duplicate-name add unexpectedly produced executable snapshot {StableId.value candidate.Id} with {List.length diff} changes."
        | NoChange (baseSnapshotId, diff) ->
            fail
                $"Duplicate-name add unexpectedly returned NoChange for {StableId.value baseSnapshotId} with {List.length diff} changes."

    let private sameNameRenameIsNoChange () =
        let snapshot = loadInitial ()

        let result =
            Operations.renameSymbol
                snapshot
                snapshot.Id
                (TransactionId.create "transaction:unused-no-change")
                (RenameTarget.Function(
                    FunctionId.create "function:validate-user"
                ))
                "validate_user"

        match result with
        | NoChange (baseSnapshotId, diff) ->
            equal
                snapshot.Id
                baseSnapshotId
                "NoChange did not preserve the base snapshot ID."

            equal 0 (List.length diff) "NoChange semantic diff must be empty."
        | Rejected (baseSnapshotId, diagnostics) ->
            fail
                $"Same-name rename was rejected for {StableId.value baseSnapshotId} with {List.length diagnostics} diagnostics."
        | Incomplete (candidate, diff, blocking) ->
            fail
                $"Same-name rename produced incomplete snapshot {StableId.value candidate.Id} with {List.length diff} changes and {List.length blocking} blockers."
        | Executable (candidate, diff) ->
            fail
                $"Same-name rename produced executable snapshot {StableId.value candidate.Id} with {List.length diff} changes."

    let private typeRenamePreservesIdentityAndReferences () =
        let snapshot = loadInitial ()
        let originalJson = CanonicalJson.writeSnapshot snapshot
        let typeId = TypeId.create "type:raw-user-input"

        let originalReferences =
            snapshot.Functions
            |> List.collect (fun functionEntity ->
                functionEntity.Parameters
                |> List.map (fun parameter -> parameter.Id, parameter.TypeId))

        let expectedTypes =
            snapshot.Types
            |> List.map (fun typeEntity ->
                if typeEntity.Id = typeId then
                    typeEntity.Id, "raw_input"
                else
                    typeEntity.Id, typeEntity.Name)

        let result =
            Operations.renameSymbol
                snapshot
                snapshot.Id
                (TransactionId.create "transaction:rename-type")
                (RenameTarget.Type typeId)
                "raw_input"

        match result with
        | Executable (candidate, diff) ->
            let renamed =
                candidate.Types
                |> List.find (fun typeEntity -> typeEntity.Id = typeId)

            equal typeId renamed.Id "TypeId changed during type rename."
            equal "raw_input" renamed.Name "Type display name did not change."

            equal
                expectedTypes
                (candidate.Types
                 |> List.map (fun typeEntity ->
                     typeEntity.Id, typeEntity.Name))
                "Type rename changed more than the target display name."

            equal
                originalReferences
                (candidate.Functions
                 |> List.collect (fun functionEntity ->
                     functionEntity.Parameters
                     |> List.map (fun parameter ->
                         parameter.Id, parameter.TypeId)))
                "Type rename changed a parameter type reference."

            let expectedCandidateJson =
                originalJson
                    .Replace(
                        "\"id\": \"snapshot:initial\"",
                        "\"id\": \"snapshot:rename-type\"",
                        StringComparison.Ordinal
                    )
                    .Replace(
                        "\"name\": \"raw_user_input\"",
                        "\"name\": \"raw_input\"",
                        StringComparison.Ordinal
                    )

            equal
                expectedCandidateJson
                (CanonicalJson.writeSnapshot candidate)
                "Type rename changed snapshot content beyond ID and display name."

            equal 1 (List.length diff) "Type rename diff must have one change."

            match List.exactlyOne diff with
            | TypeRenamed (changedTypeId, oldName, newName) ->
                equal typeId changedTypeId "TypeRenamed has the wrong TypeId."
                equal "raw_user_input" oldName "TypeRenamed has the wrong old name."
                equal "raw_input" newName "TypeRenamed has the wrong new name."
            | FunctionRenamed (functionId, oldName, newName) ->
                fail
                    $"Expected TypeRenamed, found FunctionRenamed({StableId.value functionId}, {oldName}, {newName})."
            | ParameterAdded (functionId, parameterId) ->
                fail
                    $"Expected TypeRenamed, found ParameterAdded({StableId.value functionId}, {StableId.value parameterId})."
            | ObligationCreated obligationId ->
                fail
                    $"Expected TypeRenamed, found ObligationCreated({StableId.value obligationId})."

            equal
                originalJson
                (CanonicalJson.writeSnapshot snapshot)
                "Type rename mutated the input snapshot."
        | Rejected (baseSnapshotId, diagnostics) ->
            fail
                $"Type rename was rejected for {StableId.value baseSnapshotId} with {List.length diagnostics} diagnostics."
        | Incomplete (candidate, diff, blocking) ->
            fail
                $"Type rename unexpectedly produced incomplete snapshot {StableId.value candidate.Id} with {List.length diff} changes and {List.length blocking} blockers."
        | NoChange (baseSnapshotId, diff) ->
            fail
                $"Type rename unexpectedly returned NoChange for {StableId.value baseSnapshotId} with {List.length diff} changes."

    let private migrationFixture =
        String.concat
            "\n"
            [
                "{"
                "  \"schema_version\": 1,"
                "  \"id\": \"snapshot:migration\","
                "  \"types\": ["
                "    {"
                "      \"id\": \"type:string\","
                "      \"name\": \"string\""
                "    }"
                "  ],"
                "  \"functions\": ["
                "    {"
                "      \"id\": \"function:validate-user\","
                "      \"name\": \"validate_user\","
                "      \"parameters\": ["
                "        {"
                "          \"id\": \"parameter:input\","
                "          \"name\": \"input\","
                "          \"type_id\": \"type:string\""
                "        }"
                "      ]"
                "    }"
                "  ],"
                "  \"calls\": ["
                "    {"
                "      \"id\": \"call:main-validate\","
                "      \"callee_id\": \"function:validate-user\","
                "      \"arguments\": []"
                "    }"
                "  ],"
                "  \"obligations\": ["
                "    {"
                "      \"id\": \"obligation:migrate:main-validate:input\","
                "      \"kind\": {"
                "        \"kind\": \"missing_argument\","
                "        \"call_id\": \"call:main-validate\","
                "        \"parameter_id\": \"parameter:input\""
                "      },"
                "      \"status\": \"open\""
                "    }"
                "  ]"
                "}"
                ""
            ]

    let private obligationOrigins (json: string) =
        use document = JsonDocument.Parse json
        let root = document.RootElement

        root.GetProperty("obligations").EnumerateArray()
        |> Seq.map (fun obligation ->
            obligation.GetProperty("origin_transaction_id").GetString())
        |> Seq.toList

    let private schemaV2MigrationAndCanonicalStability () =
        let migrated =
            CanonicalJson.readSnapshot migrationFixture
            |> CanonicalJson.writeSnapshot

        use document = JsonDocument.Parse migrated

        equal
            2
            (document.RootElement.GetProperty("schema_version").GetInt32())
            "Migrated writer output is not schema v2."

        equal
            [ "transaction:migrated" ]
            (obligationOrigins migrated)
            "Migrated obligation has the wrong origin transaction."

        let reloaded =
            CanonicalJson.readSnapshot migrated
            |> CanonicalJson.writeSnapshot

        equal migrated reloaded "Canonical v2 output is not stable across two loads."

        let snapshot = loadInitial ()

        let nativeResult =
            Operations.addParameter
                snapshot
                snapshot.Id
                (TransactionId.create "transaction:native")
                (FunctionId.create "function:validate-user")
                {
                    Id = ParameterId.create "parameter:native"
                    Name = "native"
                    TypeId = TypeId.create "type:string"
                }

        match nativeResult with
        | Incomplete (candidate, diff, blocking) ->
            ignore diff
            ignore blocking

            equal
                [ "transaction:native" ]
                (CanonicalJson.writeSnapshot candidate
                 |> obligationOrigins)
                "Native v2 obligation lost its creating transaction."
        | Rejected (baseSnapshotId, diagnostics) ->
            fail
                $"Native obligation setup was rejected for {StableId.value baseSnapshotId} with {List.length diagnostics} diagnostics."
        | Executable (candidate, diff) ->
            fail
                $"Native obligation setup unexpectedly produced executable snapshot {StableId.value candidate.Id} with {List.length diff} changes."
        | NoChange (baseSnapshotId, diff) ->
            fail
                $"Native obligation setup unexpectedly returned NoChange for {StableId.value baseSnapshotId} with {List.length diff} changes."

    let private tests =
        [
            "duplicate-name parameter obligations", duplicateNameParameterObligations
            "same-name rename is no change", sameNameRenameIsNoChange
            "type rename preserves identity and references", typeRenamePreservesIdentityAndReferences
            "schema v2 migration and canonical stability", schemaV2MigrationAndCanonicalStability
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
