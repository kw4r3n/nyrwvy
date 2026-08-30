namespace NyamNyam.AgentEval.Structural

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open NyamNyam.Core
open NyamNyam.AgentEval

type StructuralHarnessRunMetadata =
    { Model: string option
      ProviderRuntime: string option
      ReasoningConfig: string option
      OpenCodeVersion: string option
      PromptRevision: string option
      ToolRevision: string option
      RepositoryRevision: string option }

[<RequireQualifiedAccess>]
module StructuralHarnessRunMetadata =
    let defaultValue =
        { Model = None
          ProviderRuntime = None
          ReasoningConfig = None
          OpenCodeVersion = None
          PromptRevision = None
          ToolRevision = None
          RepositoryRevision = None }

type StructuralHarnessConfig =
    { ArtifactDirectory: string
      FixtureName: string
      TaskId: string
      EnableStalePerturbation: bool
      RunMetadata: StructuralHarnessRunMetadata }

type private StructuralHarnessEvent =
    { Sequence: int
      Request: string
      Response: string
      RequestBytes: int64
      ResponseBytes: int64 }

module private StructuralHarnessJson =
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

module private StructuralHarnessArtifacts =
    let private utf8 = UTF8Encoding(false)

    let writeText path mode (value: string) =
        let bytes = utf8.GetBytes(value)
        use stream =
            new FileStream(path, mode, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough)
        stream.Write(bytes, 0, bytes.Length)
        stream.Flush(true)
        ArtifactPermissions.ensureFile path

    let writeAtomically path (value: string) =
        let temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N")
        try
            writeText temporaryPath FileMode.CreateNew value
            File.Move(temporaryPath, path, true)
            ArtifactPermissions.ensureFile path
        finally
            if File.Exists temporaryPath then File.Delete temporaryPath

[<Sealed>]
type StructuralHarness private
    (config: StructuralHarnessConfig,
     fixture: StructuralHarnessFixtureBinding,
     baselineFunctions: Map<FunctionId, SemanticFunction>,
     eventPath: string,
     metricsPath: string,
     runStatePath: string,
     harnessEventPath: string) =

    let mutable eventSequence = 0
    let mutable requestBytes = 0L
    let mutable responseBytes = 0L
    let mutable rejectedSimulationCount = 0
    let mutable rejectedCommitCount = 0
    let mutable perturbationApplied = false
    let mutable perturbationResultNodeId: GraphNodeId option = None
    let mutable verificationReports: StructuralVerificationReport list = []

    let writeHarnessEvent kind (details: (string * string) list) =
        let detailValue =
            details
            |> List.map (fun (key, value) -> key, box value)
            |> StructuralHarnessJson.objectValue
        StructuralHarnessJson.objectValue
            [ "sequence", box eventSequence
              "kind", box kind
              "details", detailValue ]
        |> StructuralHarnessJson.serialize
        |> fun line -> StructuralHarnessArtifacts.writeText harnessEventPath FileMode.Append (line + "\n")
        ArtifactPermissions.restrictTree config.ArtifactDirectory

    let recordPrivateOutcome (request: string) (response: string) =
        try
            use requestDocument = JsonDocument.Parse(request)
            use responseDocument = JsonDocument.Parse(response)
            let command = requestDocument.RootElement.GetProperty("command").GetString()
            let responseRoot = responseDocument.RootElement
            if responseRoot.GetProperty("ok").GetBoolean() then
                let status = responseRoot.GetProperty("result").GetProperty("status").GetString()
                if command = "simulateStructuralV1" && status = "rejected" then
                    rejectedSimulationCount <- rejectedSimulationCount + 1
                elif (command = "commitStructuralV1" || command = "commitStructuralAllowIncompleteV1")
                     && status = "rejected" then
                    rejectedCommitCount <- rejectedCommitCount + 1
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
            match perturbationResultNodeId, AgentWorkspace.getFunction workspace fixture.UnrelatedFunctionId with
            | Some expectedResult, Ok current -> current.Body.ResultNodeId = expectedResult
            | _ -> false

    member _.Config = config
    member _.Fixture = fixture
    member _.PerturbationApplied = perturbationApplied

    member _.RecordEvent(request: string, response: string) =
        let requestSize = StructuralHarnessJson.utf8Bytes request
        let responseSize = StructuralHarnessJson.utf8Bytes response
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

        StructuralHarnessJson.objectValue
            [ "sequence", box event.Sequence
              "request", box event.Request
              "response", box event.Response
              "requestBytes", box event.RequestBytes
              "responseBytes", box event.ResponseBytes ]
        |> StructuralHarnessJson.serialize
        |> fun line -> StructuralHarnessArtifacts.writeText eventPath FileMode.Append (line + "\n")
        ArtifactPermissions.restrictTree config.ArtifactDirectory

    member _.MaybeApplyStalePerturbation(workspace: AgentWorkspace, targetFunctionId: FunctionId): AgentWorkspace =
        if not config.EnableStalePerturbation
           || perturbationApplied
           || targetFunctionId <> fixture.TargetFunctionId then
            workspace
        else
            let baseSnapshotId = AgentWorkspace.currentSnapshotId workspace
            let baseSnapshotFingerprint =
                match AgentWorkspace.currentSnapshotFingerprint workspace with
                | Ok value -> value
                | Error _ -> raise (InvalidOperationException("Structural evaluation harness could not capture the perturbation base fingerprint."))
            let baseSemanticFingerprint =
                match AgentWorkspace.currentSemanticFingerprint workspace with
                | Ok value -> value
                | Error _ -> raise (InvalidOperationException("Structural evaluation harness could not capture the perturbation base semantic fingerprint."))

            let unrelated =
                match AgentWorkspace.getFunction workspace fixture.UnrelatedFunctionId with
                | Ok value -> value
                | Error _ -> raise (InvalidOperationException("Structural evaluation harness could not locate the unrelated perturbation target."))

            let perturbationRequest: AgentReplaceFunctionBodyWithFloat64Recipe =
                { TargetFunctionId = fixture.UnrelatedFunctionId
                  ExpectedBaseSnapshotId = baseSnapshotId
                  ExpectedCurrentResultNodeId = unrelated.Body.ResultNodeId
                  Replacement = Float64ExpressionRecipe.Literal(Float64Bits.create 0x4059000000000000UL) }

            match AgentStructuralWorkflow.simulate workspace perturbationRequest with
            | Error error ->
                writeHarnessEvent "stalePerturbationFailed" [ "stage", "simulate"; "detail", sprintf "%A" error ]
                raise (InvalidOperationException("Structural evaluation harness perturbation failed."))
            | Ok proposal ->
                match AgentStructuralWorkflow.commit workspace proposal with
                | AgentStructuralCommitResult.Rejected diagnostics ->
                    writeHarnessEvent "stalePerturbationFailed" [ "stage", "commit"; "detail", sprintf "%A" diagnostics ]
                    raise (InvalidOperationException("Structural evaluation harness perturbation failed."))
                | AgentStructuralCommitResult.Committed(nextWorkspace, publication) ->
                    perturbationApplied <- true
                    perturbationResultNodeId <- Some publication.Diff.NewResultNodeId
                    writeHarnessEvent
                        "stalePerturbationCommitted"
                        [ "transactionId", StableId.value publication.TransactionId
                          "baseSnapshotId", StableId.value publication.BaseSnapshotId
                          "publishedSnapshotId", StableId.value publication.PublishedSnapshotId
                          "baseSnapshotFingerprint", SnapshotFingerprint.value baseSnapshotFingerprint
                          "publishedSnapshotFingerprint", SnapshotFingerprint.value publication.Receipt.ProposedSnapshotFingerprint
                          "baseSemanticFingerprint", SemanticFingerprint.value baseSemanticFingerprint
                          "publishedSemanticFingerprint", SemanticFingerprint.value publication.Receipt.ProposedSemanticFingerprint
                          "targetFunctionId", StableId.value fixture.UnrelatedFunctionId
                          "newResultNodeId", StableId.value publication.Diff.NewResultNodeId ]
                    nextWorkspace

    member _.VerifyCommit(workspace: AgentWorkspace, publication: PublishedStructuralTransaction) =
        if publication.Readiness = TransactionReadiness.SemanticallyReady then
            let report =
                StructuralVerification.run config.FixtureName config.TaskId workspace publication config.ArtifactDirectory
            verificationReports <- report :: verificationReports
            writeHarnessEvent
                "commitVerification"
                [ "transactionId", StableId.value publication.TransactionId
                  "publishedSnapshotId", StableId.value publication.PublishedSnapshotId
                  "status", StructuralVerificationStatus.name report.Status
                  "evidenceDurable", string report.EvidenceDurable ]

    member _.Finalize
        (workspace: AgentWorkspace,
         usage: StructuralSessionUsage,
         pendingProposalCount: int)
        : bool =
        try
            let snapshotFingerprint =
                match AgentWorkspace.currentSnapshotFingerprint workspace with
                | Ok value -> SnapshotFingerprint.value value
                | Error _ -> "unavailable"
            let semanticFingerprint =
                match AgentWorkspace.currentSemanticFingerprint workspace with
                | Ok value -> SemanticFingerprint.value value
                | Error _ -> "unavailable"
            let zeroChurn = zeroUnrelatedSemanticChurn workspace
            let perturbationPreserved = harnessPerturbationPreserved workspace

            let reports =
                verificationReports
                |> List.rev
                |> List.map (fun report ->
                    StructuralHarnessJson.objectValue
                        [ "transactionId", box (StableId.value report.TransactionId)
                          "publishedSnapshotId", box (StableId.value report.PublishedSnapshotId)
                          "status", box (StructuralVerificationStatus.name report.Status)
                          "evidenceDurable", box report.EvidenceDurable
                          "evidencePath", box report.EvidencePath ])
                |> StructuralHarnessJson.arrayValue

            StructuralHarnessJson.objectValue
                [ "schemaVersion", box 1
                  "taskId", box config.TaskId
                  "fixture", box config.FixtureName
                  "protocolVersion", box StructuralProtocol.version
                  "toolCalls", box usage.ToolCalls
                  "focusQueries", box usage.FocusQueries
                  "simulations", box usage.Simulations
                  "commitAttempts", box usage.CommitAttempts
                  "commits", box usage.Commits
                  "agentRejects", box usage.Rejects
                  "protocolErrors", box usage.ProtocolErrors
                  "rejectedSimulations", box rejectedSimulationCount
                  "rejectedCommits", box rejectedCommitCount
                  "eventCount", box eventSequence
                  "requestBytes", box requestBytes
                  "responseBytes", box responseBytes
                  "pendingProposalCount", box pendingProposalCount
                  "zeroUnrelatedSemanticChurn", box zeroChurn
                  "harnessPerturbationApplied", box perturbationApplied
                  "harnessPerturbationPreserved", box perturbationPreserved
                  "verificationReports", reports ]
            |> StructuralHarnessJson.serialize
            |> StructuralHarnessArtifacts.writeAtomically metricsPath

            StructuralHarnessJson.objectValue
                [ "schemaVersion", box 1
                  "state", box "terminal"
                  "taskId", box config.TaskId
                  "fixture", box config.FixtureName
                  "protocolVersion", box StructuralProtocol.version
                  "currentSnapshotId", box (StableId.value (AgentWorkspace.currentSnapshotId workspace))
                  "currentSnapshotFingerprint", box snapshotFingerprint
                  "currentSemanticFingerprint", box semanticFingerprint
                  "pendingProposalCount", box pendingProposalCount
                  "zeroUnrelatedSemanticChurn", box zeroChurn
                  "harnessPerturbationApplied", box perturbationApplied
                  "harnessPerturbationPreserved", box perturbationPreserved
                  "verificationReportCount", box verificationReports.Length ]
            |> StructuralHarnessJson.serialize
            |> StructuralHarnessArtifacts.writeAtomically runStatePath

            ArtifactPermissions.restrictTree config.ArtifactDirectory
            true
        with
        | _ -> false

    static member Create(snapshot: Snapshot, config: StructuralHarnessConfig): Result<StructuralHarness, string> =
        try
            if String.IsNullOrWhiteSpace config.ArtifactDirectory then
                Error "Artifact directory must not be blank."
            else
                let fullDirectory = Path.GetFullPath(config.ArtifactDirectory)
                match FrozenM2StructuralCorpus.fixtureByName config.FixtureName with
                | Error error -> Error error
                | Ok fixture ->
                    match FrozenM2StructuralCorpus.expectedFixtureForTask config.TaskId with
                    | Error error -> Error error
                    | Ok expectedFixture when expectedFixture <> config.FixtureName ->
                        Error $"Task {config.TaskId} requires fixture {expectedFixture}, not {config.FixtureName}."
                    | Ok _ when config.EnableStalePerturbation && config.TaskId <> "M2-E10" ->
                        Error "The M2 stale perturbation is permitted only for M2-E10."
                    | Ok _ when config.TaskId = "M2-E10" && not config.EnableStalePerturbation ->
                        Error "M2-E10 requires the harness-owned stale perturbation."
                    | Ok _ ->
                        let issues = SnapshotValidator.validate snapshot
                        let target = snapshot.Functions |> List.tryFind (fun fn -> fn.Id = fixture.TargetFunctionId)
                        let caller = snapshot.Functions |> List.tryFind (fun fn -> fn.Id = fixture.CallerFunctionId)
                        let foreign = snapshot.Functions |> List.tryFind (fun fn -> fn.Id = fixture.ForeignFunctionId)
                        let unrelated = snapshot.Functions |> List.tryFind (fun fn -> fn.Id = fixture.UnrelatedFunctionId)
                        if not (List.isEmpty issues)
                           || snapshot.Id <> fixture.SnapshotId
                           || (target |> Option.forall (fun fn -> fn.Body.ResultNodeId <> fixture.TargetResultNodeId))
                           || Option.isNone caller
                           || Option.isNone foreign
                           || (unrelated |> Option.forall (fun fn -> fn.Body.ResultNodeId <> fixture.UnrelatedResultNodeId)) then
                            Error "The exported structural fixture does not match the frozen corpus identity bindings."
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
                                StructuralHarnessArtifacts.writeText eventPath FileMode.Create String.Empty
                                StructuralHarnessArtifacts.writeText harnessEventPath FileMode.Create String.Empty

                                StructuralHarnessJson.objectValue
                                    [ "schemaVersion", box 1
                                      "corpusId", box FrozenM2StructuralCorpus.corpusId
                                      "corpusRevision", box FrozenM2StructuralCorpus.corpusRevision
                                      "protocolVersion", box StructuralProtocol.version
                                      "taskId", box config.TaskId
                                      "fixture", box config.FixtureName
                                      "startingSnapshotId", box (StableId.value snapshot.Id)
                                      "startingSnapshotFingerprint", box (SnapshotFingerprint.value actualSnapshot)
                                      "startingSemanticFingerprint", box (SemanticFingerprint.value actualSemantic)
                                      "model", StructuralHarnessJson.optionalString config.RunMetadata.Model
                                      "providerRuntime", StructuralHarnessJson.optionalString config.RunMetadata.ProviderRuntime
                                      "reasoningConfig", StructuralHarnessJson.optionalString config.RunMetadata.ReasoningConfig
                                      "openCodeVersion", StructuralHarnessJson.optionalString config.RunMetadata.OpenCodeVersion
                                      "promptRevision", StructuralHarnessJson.optionalString config.RunMetadata.PromptRevision
                                      "toolRevision", StructuralHarnessJson.optionalString config.RunMetadata.ToolRevision
                                      "repositoryRevision", StructuralHarnessJson.optionalString config.RunMetadata.RepositoryRevision ]
                                |> StructuralHarnessJson.serialize
                                |> StructuralHarnessArtifacts.writeAtomically runMetadataPath

                                StructuralHarnessJson.objectValue
                                    [ "schemaVersion", box 1
                                      "state", box "inProgress"
                                      "taskId", box config.TaskId
                                      "fixture", box config.FixtureName ]
                                |> StructuralHarnessJson.serialize
                                |> StructuralHarnessArtifacts.writeAtomically runStatePath
                                ArtifactPermissions.restrictTree fullDirectory

                                Ok(
                                    StructuralHarness(
                                        { config with ArtifactDirectory = fullDirectory },
                                        fixture,
                                        baselineFunctions,
                                        eventPath,
                                        metricsPath,
                                        runStatePath,
                                        harnessEventPath
                                    )
                                )
                            | _ -> Error "The structural evaluation fixture fingerprints could not be computed."
        with
        | _ -> Error "Structural evaluation harness initialization failed."
