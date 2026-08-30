namespace NyamNyam.AgentEval.ListMapStructural

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open NyamNyam.Core
open NyamNyam.AgentEval

type ListMapStructuralHarnessRunMetadata =
    { Model: string option
      ProviderRuntime: string option
      ReasoningConfig: string option
      OpenCodeVersion: string option
      PromptRevision: string option
      ToolRevision: string option
      RepositoryRevision: string option }

[<RequireQualifiedAccess>]
module ListMapStructuralHarnessRunMetadata =
    let defaultValue =
        { Model = None
          ProviderRuntime = None
          ReasoningConfig = None
          OpenCodeVersion = None
          PromptRevision = None
          ToolRevision = None
          RepositoryRevision = None }

type ListMapStructuralHarnessConfig =
    { ArtifactDirectory: string
      FixtureName: string
      TaskId: string
      EnableStalePerturbation: bool
      RunMetadata: ListMapStructuralHarnessRunMetadata }

type private ListMapStructuralHarnessEvent =
    { Sequence: int
      Request: string
      Response: string
      RequestBytes: int64
      ResponseBytes: int64 }

module private ListMapHarnessJson =
    let objectValue (pairs: (string * obj) list): obj =
        let value = Dictionary<string, obj>(StringComparer.Ordinal)
        for key, item in pairs do value[key] <- item
        box value

    let arrayValue (items: obj list): obj = items |> List.toArray |> box

    let optionalString = function
        | Some value -> box value
        | None -> null

    let serialize (value: obj): string = JsonSerializer.Serialize(value)
    let utf8Bytes (value: string): int64 = UTF8Encoding(false).GetByteCount(value) |> int64

module private ListMapHarnessArtifacts =
    let private utf8 = UTF8Encoding(false)

    let writeText (path: string) (mode: FileMode) (value: string) =
        let bytes = utf8.GetBytes(value)
        use stream =
            new FileStream(path, mode, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough)
        stream.Write(bytes, 0, bytes.Length)
        stream.Flush(true)
        ArtifactPermissions.ensureFile path

    let writeAtomically (path: string) (value: string) =
        let temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N")
        try
            writeText temporaryPath FileMode.CreateNew value
            File.Move(temporaryPath, path, true)
            ArtifactPermissions.ensureFile path
        finally
            if File.Exists temporaryPath then File.Delete temporaryPath

[<Sealed>]
type ListMapStructuralHarness private
    (config: ListMapStructuralHarnessConfig,
     fixture: ListMapStructuralHarnessFixtureBinding,
     task: ListMapStructuralHarnessTaskBinding,
     startingSnapshotFingerprint: SnapshotFingerprint,
     startingSemanticFingerprint: SemanticFingerprint,
     baselineFunctions: Map<FunctionId, SemanticFunction>,
     eventPath: string,
     metricsPath: string,
     runStatePath: string,
     harnessEventPath: string) =

    let perturbationDisplayName = "unrelated_marker_harness_advanced"
    let mutable eventSequence = 0
    let mutable requestBytes = 0L
    let mutable responseBytes = 0L
    let mutable rejectedSimulationCount = 0
    let mutable rejectedCommitCount = 0
    let mutable perturbationApplied = false
    let mutable observedDiagnosticCodes: string list = []
    let mutable verificationReports: ListMapStructuralVerificationReport list = []

    let writeHarnessEvent (kind: string) (details: (string * string) list) =
        let detailValue =
            details
            |> List.map (fun (key, value) -> key, box value)
            |> ListMapHarnessJson.objectValue
        ListMapHarnessJson.objectValue
            [ "sequence", box eventSequence
              "kind", box kind
              "details", detailValue ]
        |> ListMapHarnessJson.serialize
        |> fun line -> ListMapHarnessArtifacts.writeText harnessEventPath FileMode.Append (line + "\n")
        ArtifactPermissions.restrictTree config.ArtifactDirectory

    let appendDiagnostics (result: JsonElement) =
        match result.TryGetProperty("diagnostics") with
        | true, diagnostics when diagnostics.ValueKind = JsonValueKind.Array ->
            diagnostics.EnumerateArray()
            |> Seq.iter (fun diagnostic ->
                match diagnostic.TryGetProperty("code") with
                | true, code when code.ValueKind = JsonValueKind.String ->
                    observedDiagnosticCodes <- observedDiagnosticCodes @ [ code.GetString() ]
                | _ -> ())
        | _ -> ()

    let recordPrivateOutcome (request: string) (response: string) =
        try
            use requestDocument = JsonDocument.Parse(request)
            use responseDocument = JsonDocument.Parse(response)
            let requestRoot = requestDocument.RootElement
            let responseRoot = responseDocument.RootElement
            if requestRoot.ValueKind = JsonValueKind.Object
               && responseRoot.ValueKind = JsonValueKind.Object
               && responseRoot.GetProperty("ok").GetBoolean() then
                let command = requestRoot.GetProperty("command").GetString()
                let result = responseRoot.GetProperty("result")
                let status = result.GetProperty("status").GetString()
                if command = "simulateListMapV1" && status = "rejected" then
                    rejectedSimulationCount <- rejectedSimulationCount + 1
                    appendDiagnostics result
                elif command = "commitListMapV1" && status = "rejected" then
                    rejectedCommitCount <- rejectedCommitCount + 1
                    appendDiagnostics result
        with
        | _ -> ()

    let zeroUnrelatedSemanticChurn (workspace: AgentWorkspace): bool =
        baselineFunctions
        |> Map.toList
        |> List.filter (fun (functionId, _) ->
            not (config.EnableStalePerturbation && functionId = fixture.UnrelatedFunctionId))
        |> List.forall (fun (functionId, before) ->
            match AgentWorkspace.getFunction workspace functionId with
            | Ok after -> before = after
            | Error _ -> false)

    let harnessPerturbationPreserved (workspace: AgentWorkspace): bool =
        if not config.EnableStalePerturbation then
            true
        else
            match AgentWorkspace.getFunction workspace fixture.UnrelatedFunctionId with
            | Ok current -> current.DisplayName = perturbationDisplayName
            | Error _ -> false

    let currentFingerprints (workspace: AgentWorkspace) =
        let snapshot = AgentWorkspace.currentSnapshotFingerprint workspace |> Result.toOption
        let semantic = AgentWorkspace.currentSemanticFingerprint workspace |> Result.toOption
        snapshot, semantic

    let verificationPassed () =
        if not task.ExpectedCommit then
            List.isEmpty verificationReports
        else
            verificationReports.Length = 1
            && (verificationReports
                |> List.forall (fun report ->
                    report.Status = ListMapStructuralVerificationStatus.Passed
                    && report.EvidenceDurable))

    let oracleChecks
        (workspace: AgentWorkspace)
        (usage: ListMapStructuralSessionUsage) =
        let snapshotFingerprint, semanticFingerprint = currentFingerprints workspace
        let expectedDiagnosticObserved =
            match task.ExpectedDiagnostic with
            | None -> true
            | Some expected -> observedDiagnosticCodes |> List.contains expected
        let expectedCommitCount = usage.Commits = (if task.ExpectedCommit then 1 else 0)
        let unchangedWhenCommitForbidden =
            if task.ExpectedCommit then
                true
            else
                snapshotFingerprint = Some startingSnapshotFingerprint
                && semanticFingerprint = Some startingSemanticFingerprint
        let perturbationState =
            if task.EnableStalePerturbation then
                perturbationApplied && harnessPerturbationPreserved workspace
            else
                not perturbationApplied
        let zeroChurn = zeroUnrelatedSemanticChurn workspace
        let durableVerification = verificationPassed ()
        let passed =
            expectedDiagnosticObserved
            && expectedCommitCount
            && unchangedWhenCommitForbidden
            && perturbationState
            && zeroChurn
            && durableVerification

        passed,
        [ "expectedDiagnosticObserved", expectedDiagnosticObserved
          "expectedCommitCount", expectedCommitCount
          "unchangedWhenCommitForbidden", unchangedWhenCommitForbidden
          "perturbationState", perturbationState
          "zeroUnrelatedSemanticChurn", zeroChurn
          "durableCommittedVerification", durableVerification ]

    member _.Config = config
    member _.Fixture = fixture
    member _.Task = task
    member _.PerturbationApplied = perturbationApplied

    member _.RecordEvent(request: string, response: string) =
        let requestSize = ListMapHarnessJson.utf8Bytes request
        let responseSize = ListMapHarnessJson.utf8Bytes response
        let event =
            { Sequence = eventSequence
              Request = request
              Response = response
              RequestBytes = requestSize
              ResponseBytes = responseSize }
        eventSequence <- eventSequence + 1
        requestBytes <- requestBytes + requestSize
        responseBytes <- responseBytes + responseSize
        recordPrivateOutcome request response

        ListMapHarnessJson.objectValue
            [ "sequence", box event.Sequence
              "request", box event.Request
              "response", box event.Response
              "requestBytes", box event.RequestBytes
              "responseBytes", box event.ResponseBytes ]
        |> ListMapHarnessJson.serialize
        |> fun line -> ListMapHarnessArtifacts.writeText eventPath FileMode.Append (line + "\n")
        ArtifactPermissions.restrictTree config.ArtifactDirectory

    member _.MaybeApplyStalePerturbation(workspace: AgentWorkspace, targetFunctionId: FunctionId): AgentWorkspace =
        if not config.EnableStalePerturbation
           || perturbationApplied
           || targetFunctionId <> fixture.TargetFunctionId then
            workspace
        else
            let beforeSnapshot = AgentWorkspace.currentSnapshotId workspace
            let beforeSnapshotFingerprint =
                AgentWorkspace.currentSnapshotFingerprint workspace
                |> Result.defaultWith (fun _ -> raise (InvalidOperationException("ListMap harness could not capture stale-perturbation snapshot fingerprint.")))
            let beforeSemanticFingerprint =
                AgentWorkspace.currentSemanticFingerprint workspace
                |> Result.defaultWith (fun _ -> raise (InvalidOperationException("ListMap harness could not capture stale-perturbation semantic fingerprint.")))

            let rename =
                AgentWorkflow.simulate
                    workspace
                    [ AgentOperationIntent.RenameDisplayName(
                        AgentRenameTarget.Function fixture.UnrelatedFunctionId,
                        perturbationDisplayName
                      ) ]

            match rename with
            | Error error ->
                writeHarnessEvent "stalePerturbationFailed" [ "stage", "simulate"; "detail", sprintf "%A" error ]
                raise (InvalidOperationException("ListMap evaluation harness perturbation failed."))
            | Ok proposal ->
                match AgentWorkflow.commit workspace proposal with
                | AgentCommitResult.Rejected diagnostics ->
                    writeHarnessEvent "stalePerturbationFailed" [ "stage", "commit"; "detail", sprintf "%A" diagnostics ]
                    raise (InvalidOperationException("ListMap evaluation harness perturbation failed."))
                | AgentCommitResult.Committed(nextWorkspace, _) ->
                    perturbationApplied <- true
                    let afterSnapshot = AgentWorkspace.currentSnapshotId nextWorkspace
                    let afterSnapshotFingerprint =
                        AgentWorkspace.currentSnapshotFingerprint nextWorkspace
                        |> Result.defaultWith (fun _ -> raise (InvalidOperationException("ListMap harness could not capture advanced snapshot fingerprint.")))
                    let afterSemanticFingerprint =
                        AgentWorkspace.currentSemanticFingerprint nextWorkspace
                        |> Result.defaultWith (fun _ -> raise (InvalidOperationException("ListMap harness could not capture advanced semantic fingerprint.")))
                    writeHarnessEvent
                        "stalePerturbationCommitted"
                        [ "baseSnapshotId", StableId.value beforeSnapshot
                          "publishedSnapshotId", StableId.value afterSnapshot
                          "baseSnapshotFingerprint", SnapshotFingerprint.value beforeSnapshotFingerprint
                          "publishedSnapshotFingerprint", SnapshotFingerprint.value afterSnapshotFingerprint
                          "baseSemanticFingerprint", SemanticFingerprint.value beforeSemanticFingerprint
                          "publishedSemanticFingerprint", SemanticFingerprint.value afterSemanticFingerprint
                          "targetFunctionId", StableId.value fixture.UnrelatedFunctionId
                          "newDisplayName", perturbationDisplayName ]
                    nextWorkspace

    member _.VerifyCommit(workspace: AgentWorkspace, publication: PublishedStructuralTransaction) =
        if publication.Readiness = TransactionReadiness.SemanticallyReady then
            let report =
                ListMapStructuralVerification.run
                    config.FixtureName
                    config.TaskId
                    workspace
                    publication
                    config.ArtifactDirectory
            verificationReports <- report :: verificationReports
            writeHarnessEvent
                "commitVerification"
                [ "transactionId", StableId.value publication.TransactionId
                  "publishedSnapshotId", StableId.value publication.PublishedSnapshotId
                  "status", ListMapStructuralVerificationStatus.name report.Status
                  "evidenceDurable", string report.EvidenceDurable ]

    member _.Finalize
        (workspace: AgentWorkspace,
         usage: ListMapStructuralSessionUsage,
         pendingProposalCount: int)
        : bool =
        try
            let snapshotFingerprint, semanticFingerprint = currentFingerprints workspace
            let zeroChurn = zeroUnrelatedSemanticChurn workspace
            let perturbationPreserved = harnessPerturbationPreserved workspace
            let oraclePassed, checks = oracleChecks workspace usage

            let reports =
                verificationReports
                |> List.rev
                |> List.map (fun report ->
                    ListMapHarnessJson.objectValue
                        [ "transactionId", box (StableId.value report.TransactionId)
                          "publishedSnapshotId", box (StableId.value report.PublishedSnapshotId)
                          "status", box (ListMapStructuralVerificationStatus.name report.Status)
                          "evidenceDurable", box report.EvidenceDurable
                          "evidencePath", box report.EvidencePath ])
                |> ListMapHarnessJson.arrayValue

            let diagnostics =
                observedDiagnosticCodes |> List.map box |> ListMapHarnessJson.arrayValue
            let checkObject =
                checks
                |> List.map (fun (name, value) -> name, box value)
                |> ListMapHarnessJson.objectValue

            ListMapHarnessJson.objectValue
                [ "schemaVersion", box 1
                  "taskId", box config.TaskId
                  "fixture", box config.FixtureName
                  "protocolVersion", box ListMapStructuralProtocol.version
                  "toolCalls", box usage.ToolCalls
                  "focusQueries", box usage.FocusQueries
                  "simulations", box usage.Simulations
                  "commitAttempts", box usage.CommitAttempts
                  "commits", box usage.Commits
                  "agentRejects", box usage.Rejects
                  "protocolErrors", box usage.ProtocolErrors
                  "rejectedSimulations", box rejectedSimulationCount
                  "rejectedCommits", box rejectedCommitCount
                  "observedDiagnosticCodes", diagnostics
                  "eventCount", box eventSequence
                  "requestBytes", box requestBytes
                  "responseBytes", box responseBytes
                  "pendingProposalCount", box pendingProposalCount
                  "zeroUnrelatedSemanticChurn", box zeroChurn
                  "harnessPerturbationApplied", box perturbationApplied
                  "harnessPerturbationPreserved", box perturbationPreserved
                  "verificationReports", reports
                  "oraclePassed", box oraclePassed
                  "oracleChecks", checkObject ]
            |> ListMapHarnessJson.serialize
            |> ListMapHarnessArtifacts.writeAtomically metricsPath

            let optionalSnapshotFingerprint = function
                | Some value -> box (SnapshotFingerprint.value value)
                | None -> null
            let optionalSemanticFingerprint = function
                | Some value -> box (SemanticFingerprint.value value)
                | None -> null

            ListMapHarnessJson.objectValue
                [ "schemaVersion", box 1
                  "state", box "terminal"
                  "taskId", box config.TaskId
                  "fixture", box config.FixtureName
                  "protocolVersion", box ListMapStructuralProtocol.version
                  "currentSnapshotId", box (StableId.value (AgentWorkspace.currentSnapshotId workspace))
                  "currentSnapshotFingerprint", optionalSnapshotFingerprint snapshotFingerprint
                  "currentSemanticFingerprint", optionalSemanticFingerprint semanticFingerprint
                  "pendingProposalCount", box pendingProposalCount
                  "zeroUnrelatedSemanticChurn", box zeroChurn
                  "harnessPerturbationApplied", box perturbationApplied
                  "harnessPerturbationPreserved", box perturbationPreserved
                  "verificationReportCount", box verificationReports.Length
                  "oraclePassed", box oraclePassed ]
            |> ListMapHarnessJson.serialize
            |> ListMapHarnessArtifacts.writeAtomically runStatePath

            ArtifactPermissions.restrictTree config.ArtifactDirectory
            true
        with
        | _ -> false

    static member Create(snapshot: Snapshot, config: ListMapStructuralHarnessConfig): Result<ListMapStructuralHarness, string> =
        try
            if String.IsNullOrWhiteSpace config.ArtifactDirectory then
                Error "Artifact directory must not be blank."
            else
                let fullDirectory = Path.GetFullPath(config.ArtifactDirectory)
                match FrozenM2ListMapCorpus.fixtureByName config.FixtureName, FrozenM2ListMapCorpus.taskById config.TaskId with
                | Error error, _
                | _, Error error -> Error error
                | Ok fixture, Ok task when task.FixtureName <> config.FixtureName ->
                    Error $"Task {config.TaskId} requires fixture {task.FixtureName}, not {config.FixtureName}."
                | Ok _, Ok task when task.EnableStalePerturbation <> config.EnableStalePerturbation ->
                    if task.EnableStalePerturbation then
                        Error "M2-L10 requires the harness-owned stale perturbation."
                    else
                        Error "The ListMap stale perturbation is permitted only for M2-L10."
                | Ok fixture, Ok task ->
                    let issues = SnapshotValidator.validate snapshot
                    let target = snapshot.Functions |> List.tryFind (fun fn -> fn.Id = fixture.TargetFunctionId)
                    let caller = snapshot.Functions |> List.tryFind (fun fn -> fn.Id = fixture.CallerFunctionId)
                    let foreign = snapshot.Functions |> List.tryFind (fun fn -> fn.Id = fixture.ForeignFunctionId)
                    let unrelated = snapshot.Functions |> List.tryFind (fun fn -> fn.Id = fixture.UnrelatedFunctionId)
                    let mapperIds =
                        [ fixture.CompatibleMapperFunctionId
                          fixture.WrongInputMapperFunctionId
                          fixture.WrongResultMapperFunctionId
                          fixture.EffectfulMapperFunctionId ]
                    let mappersPresent =
                        mapperIds
                        |> List.forall (fun id -> snapshot.Functions |> List.exists (fun fn -> fn.Id = id))
                    let targetParametersMatch =
                        target
                        |> Option.exists (fun fn ->
                            fn.Parameters |> List.exists (fun parameter -> parameter.Id = fixture.SourceListParameterId)
                            && fn.Parameters |> List.exists (fun parameter -> parameter.Id = fixture.NonListParameterId))
                    let foreignParameterPresent =
                        foreign
                        |> Option.exists (fun fn ->
                            fn.Parameters |> List.exists (fun parameter -> parameter.Id = fixture.ForeignListParameterId))

                    if not (List.isEmpty issues)
                       || snapshot.Id <> fixture.SnapshotId
                       || (target |> Option.forall (fun fn -> fn.Body.ResultNodeId <> fixture.TargetResultNodeId))
                       || not targetParametersMatch
                       || Option.isNone caller
                       || not foreignParameterPresent
                       || not mappersPresent
                       || (unrelated |> Option.forall (fun fn -> fn.Body.ResultNodeId <> fixture.UnrelatedResultNodeId)) then
                        Error "The exported ListMap fixture does not match the frozen corpus identity bindings."
                    else
                        match SnapshotFingerprint.compute snapshot, SemanticFingerprint.compute snapshot with
                        | Ok actualSnapshot, Ok actualSemantic ->
                            let baselineFunctions =
                                snapshot.Functions
                                |> List.filter (fun fn -> fn.Id <> fixture.TargetFunctionId)
                                |> List.map (fun fn -> fn.Id, fn)
                                |> Map.ofList
                            ArtifactPermissions.ensureDirectory fullDirectory
                            let eventPath = Path.Combine(fullDirectory, "events.jsonl")
                            let metricsPath = Path.Combine(fullDirectory, "metrics.json")
                            let runMetadataPath = Path.Combine(fullDirectory, "run-metadata.json")
                            let runStatePath = Path.Combine(fullDirectory, "run-state.json")
                            let harnessEventPath = Path.Combine(fullDirectory, "harness-events.jsonl")
                            ListMapHarnessArtifacts.writeText eventPath FileMode.Create String.Empty
                            ListMapHarnessArtifacts.writeText harnessEventPath FileMode.Create String.Empty

                            ListMapHarnessJson.objectValue
                                [ "schemaVersion", box 1
                                  "corpusId", box FrozenM2ListMapCorpus.corpusId
                                  "corpusRevision", box FrozenM2ListMapCorpus.corpusRevision
                                  "protocolVersion", box ListMapStructuralProtocol.version
                                  "taskId", box config.TaskId
                                  "fixture", box config.FixtureName
                                  "startingSnapshotId", box (StableId.value snapshot.Id)
                                  "startingSnapshotFingerprint", box (SnapshotFingerprint.value actualSnapshot)
                                  "startingSemanticFingerprint", box (SemanticFingerprint.value actualSemantic)
                                  "model", ListMapHarnessJson.optionalString config.RunMetadata.Model
                                  "providerRuntime", ListMapHarnessJson.optionalString config.RunMetadata.ProviderRuntime
                                  "reasoningConfig", ListMapHarnessJson.optionalString config.RunMetadata.ReasoningConfig
                                  "openCodeVersion", ListMapHarnessJson.optionalString config.RunMetadata.OpenCodeVersion
                                  "promptRevision", ListMapHarnessJson.optionalString config.RunMetadata.PromptRevision
                                  "toolRevision", ListMapHarnessJson.optionalString config.RunMetadata.ToolRevision
                                  "repositoryRevision", ListMapHarnessJson.optionalString config.RunMetadata.RepositoryRevision ]
                            |> ListMapHarnessJson.serialize
                            |> ListMapHarnessArtifacts.writeAtomically runMetadataPath

                            ListMapHarnessJson.objectValue
                                [ "schemaVersion", box 1
                                  "state", box "inProgress"
                                  "taskId", box config.TaskId
                                  "fixture", box config.FixtureName
                                  "protocolVersion", box ListMapStructuralProtocol.version
                                  "startingSnapshotId", box (StableId.value snapshot.Id) ]
                            |> ListMapHarnessJson.serialize
                            |> ListMapHarnessArtifacts.writeAtomically runStatePath

                            ArtifactPermissions.restrictTree fullDirectory
                            Ok(
                                ListMapStructuralHarness(
                                    { config with ArtifactDirectory = fullDirectory },
                                    fixture,
                                    task,
                                    actualSnapshot,
                                    actualSemantic,
                                    baselineFunctions,
                                    eventPath,
                                    metricsPath,
                                    runStatePath,
                                    harnessEventPath
                                )
                            )
                        | _ -> Error "The exported ListMap fixture fingerprints could not be computed."
        with
        | _ -> Error "ListMap structural evaluation harness initialization failed."
