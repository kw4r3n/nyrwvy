namespace NyamNyam.Core.Tests

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open NyamNyam.Core
open NyamNyam.AgentEval
open Xunit

module AgentEvalProtocolTests =
    let private objectValue (pairs: (string * obj) list): obj =
        let value = Dictionary<string, obj>(StringComparer.Ordinal)
        for key, item in pairs do
            value[key] <- item
        box value

    let private arrayValue items: obj = items |> List.toArray |> box

    let private command pairs =
        objectValue pairs |> JsonSerializer.Serialize

    let private createSession snapshot config =
        match Session.create snapshot config with
        | Ok session -> session
        | Error issues -> failwithf "Unexpected evaluation session creation failure: %A" issues

    let private execute (session: Session) payload =
        let response = session.ExecuteLine(payload)
        use document = JsonDocument.Parse(response)
        let root = document.RootElement
        if not (root.GetProperty("ok").GetBoolean()) then
            failwithf "Protocol command failed: %s" response
        response

    let private resultStatus (response: string) =
        use document = JsonDocument.Parse(response)
        document.RootElement.GetProperty("result").GetProperty("status").GetString()

    let private proposalHandle (response: string) =
        use document = JsonDocument.Parse(response)
        document.RootElement.GetProperty("result").GetProperty("proposalHandle").GetString()

    let private firstDiagnosticCode (response: string) =
        use document = JsonDocument.Parse(response)
        let diagnostics =
            document.RootElement
                .GetProperty("result")
                .GetProperty("diagnostics")

        diagnostics.EnumerateArray()
        |> Seq.head
        |> fun diagnostic -> diagnostic.GetProperty("code").GetString()

    let private firstCreatedObligationId (response: string) =
        use document = JsonDocument.Parse(response)
        let obligationsCreated =
            document.RootElement
                .GetProperty("result")
                .GetProperty("review")
                .GetProperty("diff")
                .GetProperty("obligationsCreated")

        obligationsCreated.EnumerateArray()
        |> Seq.head
        |> fun obligation -> obligation.GetProperty("obligationId").GetString()

    let private focusEntityCountInResponse (response: string) =
        use document = JsonDocument.Parse(response)
        let focus =
            document.RootElement
                .GetProperty("result")
                .GetProperty("value")

        1
        + focus.GetProperty("nodeTypes").GetArrayLength()
        + focus.GetProperty("callers").GetArrayLength()
        + focus.GetProperty("callees").GetArrayLength()
        + focus.GetProperty("relatedFunctionSignatures").GetArrayLength()
        + focus.GetProperty("openObligations").GetArrayLength()
        + focus.GetProperty("referencedTypes").GetArrayLength()
        + focus.GetProperty("referencedCapabilities").GetArrayLength()
        + focus.GetProperty("referencedEffects").GetArrayLength()
        + focus.GetProperty("missingFunctionIds").GetArrayLength()
        + focus.GetProperty("missingTypeIds").GetArrayLength()
        + focus.GetProperty("missingCapabilityIds").GetArrayLength()
        + focus.GetProperty("missingEffectIds").GetArrayLength()

    let private metricInt (name: string) (response: string) =
        use document = JsonDocument.Parse(response)
        document.RootElement.GetProperty("metrics").GetProperty(name).GetInt32()

    [<Fact>]
    let ``CLI startup metadata options parse independently and preserve unavailable defaults`` () =
        let parsed =
            Program.AgentEvalCommandLine.parse
                [ "--input"
                  "snapshot.json"
                  "--model"
                  "test-model"
                  "--provider-runtime"
                  "test-provider/runtime"
                  "--reasoning-config"
                  "reasoning=low"
                  "--prompt-revision"
                  "prompt-v2"
                  "--tool-revision"
                  "tool-v3" ]

        match parsed with
        | Error error -> failwithf "Unexpected CLI parse failure: %s" error
        | Ok arguments ->
            Assert.Equal("snapshot.json", arguments.InputPath)
            Assert.Equal(Some "test-model", arguments.RunMetadata.Model)
            Assert.Equal(Some "test-provider/runtime", arguments.RunMetadata.ProviderRuntime)
            Assert.Equal(Some "reasoning=low", arguments.RunMetadata.ReasoningConfig)
            Assert.Equal(Some "prompt-v2", arguments.RunMetadata.PromptRevision)
            Assert.Equal(Some "tool-v3", arguments.RunMetadata.ToolRevision)

        match Program.AgentEvalCommandLine.parse [ "--input"; "snapshot.json" ] with
        | Error error -> failwithf "Unexpected default CLI parse failure: %s" error
        | Ok arguments -> Assert.Equal(SessionRunMetadata.defaultValue, arguments.RunMetadata)

        match Program.AgentEvalCommandLine.parse [ "--input"; "snapshot.json"; "--model" ] with
        | Ok _ -> failwith "A metadata option without a value must be rejected."
        | Error error -> Assert.Contains("missing its value", error, StringComparison.Ordinal)

    let private simulateRenameFunction functionId newDisplayName =
        command
            [ "command", box "simulate"
              "operations",
                arrayValue
                    [ objectValue
                        [ "kind", box "renameFunction"
                          "functionId", box (StableId.value functionId)
                          "newDisplayName", box newDisplayName ] ] ]

    let private commitCommand commandName handle =
        command
            [ "command", box commandName
              "proposalHandle", box handle ]

    [<Fact>]
    let ``evaluation protocol preserves reviewed proposal across rename commit`` () =
        let fixture = MilestoneZeroGraph.create ()
        let session = createSession fixture.Snapshot SessionConfig.defaultValue

        let proposalResponse =
            simulateRenameFunction fixture.ValidateFunctionId "validate_profile"
         |> execute session

        Assert.Equal("proposed", resultStatus proposalResponse)
        let handle = proposalHandle proposalResponse

        let commitResponse = commitCommand "commit" handle |> execute session
        Assert.Equal("committed", resultStatus commitResponse)

        let queryResponse =
            command
                [ "command", box "getFunction"
                  "functionId", box (StableId.value fixture.ValidateFunctionId) ]
            |> execute session

        use queryDocument = JsonDocument.Parse(queryResponse)
        let displayName =
            queryDocument.RootElement
                .GetProperty("result")
                .GetProperty("value")
                .GetProperty("displayName")
                .GetString()
        Assert.Equal("validate_profile", displayName)

    [<Fact>]
    let ``verification fixtures require artifact-backed session creation`` () =
        let fixture = MilestoneZeroGraph.create ()
        let config =
            { SessionConfig.defaultValue with
                VerificationFixture = Some VerificationFixture.MilestoneZero }

        match Session.create fixture.Snapshot config with
        | Ok _ -> failwith "A verification fixture must not create a session without artifacts."
        | Error issues ->
            Assert.True(issues |> List.exists (fun issue -> issue.Code = "VerificationRequiresArtifacts"))

        match Session.createWithArtifacts fixture.Snapshot config None with
        | Ok _ -> failwith "A verification fixture must not create an artifactless session."
        | Error(SessionCreationError.InvalidConfiguration message) ->
            Assert.Contains("artifact directory", message, StringComparison.Ordinal)
        | Error error -> failwithf "Unexpected session creation error: %A" error

    [<Fact>]
    let ``evaluation protocol supports explicit incomplete publication and obligation resolution`` () =
        let fixture = MilestoneZeroGraph.create ()
        let session = createSession fixture.Snapshot SessionConfig.defaultValue

        let addParameterCommand =
            command
                [ "command", box "simulate"
                  "operations",
                    arrayValue
                        [ objectValue
                            [ "kind", box "addFunctionParameter"
                              "functionId", box (StableId.value fixture.ValidateFunctionId)
                              "displayName", box "policy"
                              "type",
                                objectValue
                                    [ "kind", box "named"
                                      "id", box (StableId.value fixture.PolicyTypeId) ]
                              "callSitePlans",
                                arrayValue
                                    [ objectValue
                                        [ "kind", box "leaveMissing"
                                          "callNodeId", box (StableId.value fixture.MainValidateCallNodeId) ] ] ] ] ]

        let addResponse = execute session addParameterCommand
        Assert.Equal("proposed", resultStatus addResponse)
        let addHandle = proposalHandle addResponse
        let obligationId = firstCreatedObligationId addResponse

        let conservativeCommit = commitCommand "commit" addHandle |> execute session
        Assert.Equal("rejected", resultStatus conservativeCommit)
        Assert.Equal("BlockingObligations", firstDiagnosticCode conservativeCommit)

        let incompleteCommit = commitCommand "commitAllowIncomplete" addHandle |> execute session
        Assert.Equal("committed", resultStatus incompleteCommit)

        let resolveCommand =
            command
                [ "command", box "simulate"
                  "operations",
                    arrayValue
                        [ objectValue
                            [ "kind", box "resolveMissingArgument"
                              "obligationId", box obligationId
                              "valueNodeId", box (StableId.value fixture.MainPolicyNodeId) ] ] ]

        let resolveResponse = execute session resolveCommand
        let resolveHandle = proposalHandle resolveResponse
        let resolvedCommit = commitCommand "commit" resolveHandle |> execute session
        Assert.Equal("committed", resultStatus resolvedCommit)

        let obligationsResponse =
            command
                [ "command", box "getOpenObligations"
                  "functionId", box (StableId.value fixture.MainFunctionId) ]
            |> execute session

        use obligationsDocument = JsonDocument.Parse(obligationsResponse)
        let obligations =
            obligationsDocument.RootElement
                .GetProperty("result")
                .GetProperty("value")
        Assert.Equal(0, obligations.GetArrayLength())

    [<Fact>]
    let ``evaluation harness stale perturbation is not an agent command and forces fresh review`` () =
        let fixture = MilestoneZeroGraph.create ()
        let rawUserInput =
            fixture.Snapshot.Types
            |> List.find (fun semanticType -> semanticType.DisplayName = "RawUserInput")

        let config =
            { SessionConfig.defaultValue with
                StalePerturbation =
                    Some
                        { TypeId = rawUserInput.Id
                          NewDisplayName = "RawAccountInput" } }
        let session = createSession fixture.Snapshot config

        let staleProposal =
            simulateRenameFunction fixture.ValidateFunctionId "validate_profile"
            |> execute session
        let staleHandle = proposalHandle staleProposal

        let staleCommit = commitCommand "commit" staleHandle |> execute session
        Assert.Equal("rejected", resultStatus staleCommit)
        Assert.Equal("StaleBaseSnapshot", firstDiagnosticCode staleCommit)

        let freshProposal =
            simulateRenameFunction fixture.ValidateFunctionId "validate_profile"
            |> execute session
        let freshHandle = proposalHandle freshProposal
        let freshCommit = commitCommand "commit" freshHandle |> execute session
        Assert.Equal("committed", resultStatus freshCommit)

        let agentAttempt =
            command
                [ "command", box "harnessAdvance"
                  "typeId", box (StableId.value rawUserInput.Id) ]
        let rejectedProtocolResponse = session.ExecuteLine(agentAttempt)
        use rejectedProtocolDocument = JsonDocument.Parse(rejectedProtocolResponse)
        Assert.False(rejectedProtocolDocument.RootElement.GetProperty("ok").GetBoolean())
        Assert.Equal(
            "ProtocolError",
            rejectedProtocolDocument.RootElement.GetProperty("error").GetProperty("code").GetString()
        )

        let metricsResponse = command [ "command", box "metrics" ] |> execute session
        use metricsDocument = JsonDocument.Parse(metricsResponse)
        let harnessEvents =
            metricsDocument.RootElement
                .GetProperty("result")
                .GetProperty("metrics")
                .GetProperty("harnessEvents")
        Assert.Equal(1, harnessEvents.GetArrayLength())

        let harnessReceipts =
            metricsDocument.RootElement
                .GetProperty("result")
                .GetProperty("metrics")
                .GetProperty("harnessReceipts")
        Assert.Equal(1, harnessReceipts.GetArrayLength())
        let harnessReceipt = harnessReceipts[0]
        Assert.Equal("stalePerturbation", harnessReceipt.GetProperty("kind").GetString())
        Assert.False(harnessReceipt.GetProperty("transactionId").GetString() |> String.IsNullOrWhiteSpace)
        Assert.Equal(StableId.value fixture.Snapshot.Id, harnessReceipt.GetProperty("baseSnapshotId").GetString())
        Assert.False(harnessReceipt.GetProperty("publishedSnapshotId").GetString() |> String.IsNullOrWhiteSpace)
        Assert.False(harnessReceipt.GetProperty("baseSnapshotFingerprint").GetString() |> String.IsNullOrWhiteSpace)
        Assert.False(harnessReceipt.GetProperty("publishedSnapshotFingerprint").GetString() |> String.IsNullOrWhiteSpace)
        Assert.False(harnessReceipt.GetProperty("baseSemanticFingerprint").GetString() |> String.IsNullOrWhiteSpace)
        Assert.False(harnessReceipt.GetProperty("semanticFingerprint").GetString() |> String.IsNullOrWhiteSpace)
        Assert.Equal("semanticallyReady", harnessReceipt.GetProperty("readiness").GetProperty("kind").GetString())
        Assert.Equal(1, harnessReceipt.GetProperty("semanticDiff").GetProperty("renames").GetArrayLength())
        let harnessRename =
            harnessReceipt.GetProperty("semanticDiff").GetProperty("renames")
            |> fun values -> values.EnumerateArray() |> Seq.head
        Assert.Equal(
            StableId.value rawUserInput.Id,
            harnessRename.GetProperty("id").GetString()
        )

    [<Fact>]
    let ``session usage separates queries focus simulations commits rejects and protocol errors`` () =
        let fixture = MilestoneZeroGraph.create ()
        let session = createSession fixture.Snapshot SessionConfig.defaultValue

        command
            [ "command", box "getFunction"
              "functionId", box (StableId.value fixture.ValidateFunctionId) ]
        |> execute session
        |> ignore

        command
            [ "command", box "focusFunction"
              "functionId", box (StableId.value fixture.ValidateFunctionId) ]
        |> execute session
        |> fun response ->
            Assert.Equal(focusEntityCountInResponse response, metricInt "focusEntityCount" response)
        |> ignore

        let proposalResponse =
            simulateRenameFunction fixture.ValidateFunctionId "validate_profile"
            |> execute session

        let handle = proposalHandle proposalResponse
        commitCommand "commit" handle |> execute session |> ignore

        let rejectedProposal =
            simulateRenameFunction fixture.ValidateFunctionId "validate_again"
            |> execute session

        let rejectedHandle = proposalHandle rejectedProposal
        let rejectResponse = commitCommand "reject" rejectedHandle |> execute session
        Assert.Equal("rejectedByAgent", resultStatus rejectResponse)

        let protocolError = session.ExecuteLine(command [ "command", box "unsupported" ])
        use protocolErrorDocument = JsonDocument.Parse(protocolError)
        Assert.False(protocolErrorDocument.RootElement.GetProperty("ok").GetBoolean())

        let usage = session.Usage
        Assert.Equal(7, usage.EventCount)
        Assert.Equal(7, usage.ToolCalls)
        Assert.Equal(1, usage.QueryCount)
        Assert.Equal(1, usage.FocusQueryCount)
        Assert.Equal(2, usage.Queries)
        Assert.Equal(2, usage.SimulationCount)
        Assert.Equal(1, usage.CommitAttemptCount)
        Assert.Equal(1, usage.CommitCount)
        Assert.Equal(1, usage.AgentRejectCount)
        Assert.Equal(1, usage.RejectCount)
        Assert.Equal(1, usage.ProtocolErrorCount)
        Assert.Equal(7, usage.Events.Length)
        Assert.True(usage.RequestBytes > 0L)
        Assert.True(usage.ResponseBytes > 0L)
        Assert.True(usage.ContextBytes > 0L)
        Assert.True(usage.QueryContextBytes > 0L)
        Assert.True(usage.FocusContextBytes > 0L)
        Assert.True(usage.FocusEntityCount > 0)

        let focusEvent = usage.Events |> List.find (fun event -> event.FocusEntityCount > 0)
        Assert.Equal(usage.FocusContextBytes, focusEvent.FocusContextBytes)
        Assert.Equal(usage.FocusEntityCount, focusEvent.FocusEntityCount)

        usage.Events
        |> List.iter (fun event ->
            Assert.Equal(int64 (Encoding.UTF8.GetByteCount(event.Request)), event.RequestBytes)
            Assert.Equal(int64 (Encoding.UTF8.GetByteCount(event.Response)), event.ResponseBytes))

    [<Fact>]
    let ``artifact-enabled session durably records every request response and final metrics`` () =
        let fixture = MilestoneZeroGraph.create ()
        let tempRoot =
            Path.Combine(Path.GetTempPath(), "nyamnyam-agent-eval-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(tempRoot) |> ignore

        try
            let session =
                match Session.createWithArtifactDirectory fixture.Snapshot SessionConfig.defaultValue tempRoot with
                | Ok value -> value
                | Error error -> failwithf "Unexpected artifact session creation failure: %A" error

            let queryRequest =
                command
                    [ "command", box "getType"
                      "typeId", box (StableId.value fixture.PolicyTypeId) ]

            let queryResponse = session.ExecuteLine(queryRequest)
            let focusRequest =
                command
                    [ "command", box "focusFunction"
                      "functionId", box (StableId.value fixture.ValidateFunctionId) ]

            let focusResponse = session.ExecuteLine(focusRequest)
            let protocolError = session.ExecuteLine("not json")
            let paths = session.ArtifactPaths |> Option.defaultWith (fun () -> failwith "Missing artifact paths.")

            use queryResponseDocument = JsonDocument.Parse(queryResponse)
            Assert.True(queryResponseDocument.RootElement.GetProperty("metrics").GetProperty("responseBytes").GetInt64() > 0L)

            Assert.True(File.Exists(paths.EventLogPath))
            Assert.True(File.Exists(paths.MetricsPath))
            Assert.True(File.Exists(paths.RunMetadataPath))
            Assert.True(File.Exists(paths.RunStatePath))

            use initialStateDocument = JsonDocument.Parse(File.ReadAllText(paths.RunStatePath, Encoding.UTF8))
            Assert.Equal("inProgress", initialStateDocument.RootElement.GetProperty("state").GetString())

            let events = File.ReadAllLines(paths.EventLogPath)
            Assert.Equal(3, events.Length)

            use firstEventDocument = JsonDocument.Parse(events[0])
            let firstEvent = firstEventDocument.RootElement
            Assert.Equal(1, firstEvent.GetProperty("sequence").GetInt32())
            Assert.Equal(queryRequest, firstEvent.GetProperty("request").GetString())
            Assert.Equal(queryResponse, firstEvent.GetProperty("response").GetString())
            Assert.Equal(int64 (Encoding.UTF8.GetByteCount(queryRequest)), firstEvent.GetProperty("requestBytes").GetInt64())
            Assert.Equal(int64 (Encoding.UTF8.GetByteCount(queryResponse)), firstEvent.GetProperty("responseBytes").GetInt64())
            Assert.True(firstEvent.GetProperty("contextBytes").GetInt64() > 0L)
            Assert.Equal(0L, firstEvent.GetProperty("focusContextBytes").GetInt64())
            Assert.Equal(0, firstEvent.GetProperty("focusEntityCount").GetInt32())

            use secondEventDocument = JsonDocument.Parse(events[1])
            let secondEvent = secondEventDocument.RootElement
            Assert.Equal(2, secondEvent.GetProperty("sequence").GetInt32())
            Assert.Equal(focusRequest, secondEvent.GetProperty("request").GetString())
            Assert.Equal(focusResponse, secondEvent.GetProperty("response").GetString())
            Assert.True(secondEvent.GetProperty("focusContextBytes").GetInt64() > 0L)
            Assert.Equal(secondEvent.GetProperty("contextBytes").GetInt64(), secondEvent.GetProperty("focusContextBytes").GetInt64())
            Assert.True(secondEvent.GetProperty("focusEntityCount").GetInt32() > 0)

            use thirdEventDocument = JsonDocument.Parse(events[2])
            let thirdEvent = thirdEventDocument.RootElement
            Assert.Equal(3, thirdEvent.GetProperty("sequence").GetInt32())
            Assert.Equal(protocolError, thirdEvent.GetProperty("response").GetString())
            Assert.False(thirdEvent.GetProperty("response").GetString().Contains("\n", StringComparison.Ordinal))

            use metricsDocument = JsonDocument.Parse(File.ReadAllText(paths.MetricsPath, Encoding.UTF8))
            let metrics = metricsDocument.RootElement
            let usage = session.Usage
            Assert.Equal(3, metrics.GetProperty("eventCount").GetInt32())
            Assert.Equal(1, metrics.GetProperty("queryCount").GetInt32())
            Assert.Equal(1, metrics.GetProperty("focusQueryCount").GetInt32())
            Assert.Equal(usage.FocusContextBytes, metrics.GetProperty("focusContextBytes").GetInt64())
            Assert.Equal(usage.FocusEntityCount, metrics.GetProperty("focusEntityCount").GetInt32())
            Assert.Equal(1, metrics.GetProperty("protocolErrors").GetInt32())
            Assert.Equal(usage.ResponseBytes, metrics.GetProperty("responseBytes").GetInt64())
            Assert.Equal(usage.ContextBytes, metrics.GetProperty("contextBytes").GetInt64())
            Assert.Equal(usage.QueryContextBytes, metrics.GetProperty("queryContextBytes").GetInt64())

            let finalStatus = session.Finalize()
            Assert.True(finalStatus.Terminal)
            Assert.True(finalStatus.Durable)
            use finalStateDocument = JsonDocument.Parse(File.ReadAllText(paths.RunStatePath, Encoding.UTF8))
            let finalState = finalStateDocument.RootElement
            Assert.Equal("terminal", finalState.GetProperty("state").GetString())
            let finalStatePersistence = finalState.GetProperty("artifactPersistence")
            Assert.Equal("finalized", finalStatePersistence.GetProperty("status").GetString())

            use finalMetricsDocument = JsonDocument.Parse(File.ReadAllText(paths.MetricsPath, Encoding.UTF8))
            let finalMetricsPersistence = finalMetricsDocument.RootElement.GetProperty("artifactPersistence")
            Assert.Equal(
                finalStatePersistence.GetProperty("status").GetString(),
                finalMetricsPersistence.GetProperty("status").GetString()
            )
            Assert.True(finalMetricsPersistence.GetProperty("terminal").GetBoolean())
            Assert.True(finalMetricsPersistence.GetProperty("finalizationDurable").GetBoolean())
        finally
            if Directory.Exists(tempRoot) then
                Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``finalized session rejects commands without changing workspace or artifacts`` () =
        let fixture = MilestoneZeroGraph.create ()
        let tempRoot =
            Path.Combine(Path.GetTempPath(), "nyamnyam-agent-eval-finalized-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(tempRoot) |> ignore

        try
            let session =
                match Session.createWithArtifactDirectory fixture.Snapshot SessionConfig.defaultValue tempRoot with
                | Ok value -> value
                | Error error -> failwithf "Unexpected artifact session creation failure: %A" error

            let proposalResponse =
                simulateRenameFunction fixture.ValidateFunctionId "validate_profile"
                |> execute session

            let proposalHandle = proposalHandle proposalResponse
            let paths = session.ArtifactPaths |> Option.defaultWith (fun () -> failwith "Missing artifact paths.")
            let finalStatus = session.Finalize()
            Assert.True(finalStatus.Terminal)

            let eventLogAfterFinalize = File.ReadAllText(paths.EventLogPath, Encoding.UTF8)
            let metricsAfterFinalize = File.ReadAllText(paths.MetricsPath, Encoding.UTF8)
            let runStateAfterFinalize = File.ReadAllText(paths.RunStatePath, Encoding.UTF8)
            let postFinalizeResponse = session.ExecuteLine(commitCommand "commit" proposalHandle)

            use responseDocument = JsonDocument.Parse(postFinalizeResponse)
            let responseRoot = responseDocument.RootElement
            Assert.False(responseRoot.GetProperty("ok").GetBoolean())
            Assert.Equal("ProtocolError", responseRoot.GetProperty("error").GetProperty("code").GetString())
            Assert.Equal("Session has already been finalized.", responseRoot.GetProperty("error").GetProperty("message").GetString())

            let usage = session.Usage
            Assert.Equal(0, usage.CommitAttemptCount)
            Assert.Equal(0, usage.CommitCount)
            Assert.Equal(1, usage.EventCount)
            Assert.Equal(eventLogAfterFinalize, File.ReadAllText(paths.EventLogPath, Encoding.UTF8))
            Assert.Equal(metricsAfterFinalize, File.ReadAllText(paths.MetricsPath, Encoding.UTF8))
            Assert.Equal(runStateAfterFinalize, File.ReadAllText(paths.RunStatePath, Encoding.UTF8))
        finally
            if Directory.Exists(tempRoot) then
                Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``artifact initialization persists starting identity and unavailable metadata before any proposal`` () =
        let fixture = MilestoneZeroGraph.create ()
        let tempRoot =
            Path.Combine(Path.GetTempPath(), "nyamnyam-agent-eval-metadata-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(tempRoot) |> ignore

        try
            let session =
                match Session.createWithArtifactDirectory fixture.Snapshot SessionConfig.defaultValue tempRoot with
                | Ok value -> value
                | Error error -> failwithf "Unexpected artifact session creation failure: %A" error

            let paths = session.ArtifactPaths |> Option.defaultWith (fun () -> failwith "Missing artifact paths.")
            let startingSnapshotFingerprint =
                match SnapshotFingerprint.compute fixture.Snapshot with
                | Ok value -> SnapshotFingerprint.value value
                | Error error -> failwithf "Unexpected snapshot fingerprint failure: %A" error
            let startingSemanticFingerprint =
                match SemanticFingerprint.compute fixture.Snapshot with
                | Ok value -> SemanticFingerprint.value value
                | Error error -> failwithf "Unexpected semantic fingerprint failure: %A" error

            use metadataDocument = JsonDocument.Parse(File.ReadAllText(paths.RunMetadataPath, Encoding.UTF8))
            let metadata = metadataDocument.RootElement
            Assert.Equal(StableId.value fixture.Snapshot.Id, metadata.GetProperty("startingSnapshotId").GetString())
            Assert.Equal(startingSnapshotFingerprint, metadata.GetProperty("startingSnapshotFingerprint").GetString())
            Assert.Equal(startingSemanticFingerprint, metadata.GetProperty("startingSemanticFingerprint").GetString())
            Assert.Equal("unavailable", metadata.GetProperty("model").GetString())
            Assert.Equal("unavailable", metadata.GetProperty("providerRuntime").GetString())
            Assert.Equal("unavailable", metadata.GetProperty("reasoningConfig").GetString())
            Assert.Equal("unavailable", metadata.GetProperty("promptRevision").GetString())
            Assert.Equal("unavailable", metadata.GetProperty("toolRevision").GetString())
            Assert.Equal(StableId.value fixture.Snapshot.Id, StableId.value session.RunMetadata.StartingSnapshotId)
            Assert.Equal(startingSnapshotFingerprint, SnapshotFingerprint.value session.RunMetadata.StartingSnapshotFingerprint)
            Assert.Equal(startingSemanticFingerprint, SemanticFingerprint.value session.RunMetadata.StartingSemanticFingerprint)
            Assert.Empty(File.ReadAllLines(paths.EventLogPath))

            use metricsDocument = JsonDocument.Parse(File.ReadAllText(paths.MetricsPath, Encoding.UTF8))
            Assert.Equal(0, metricsDocument.RootElement.GetProperty("eventCount").GetInt32())

            let stateResponse = session.ExecuteLine(command [ "command", box "state" ])
            Assert.DoesNotContain("startingSnapshot", stateResponse, StringComparison.Ordinal)
        finally
            if Directory.Exists(tempRoot) then
                Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``caller supplied run metadata is durably recorded without invented defaults`` () =
        let fixture = MilestoneZeroGraph.create ()
        let tempRoot =
            Path.Combine(Path.GetTempPath(), "nyamnyam-agent-eval-supplied-metadata-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(tempRoot) |> ignore

        try
            let config =
                { SessionConfig.defaultValue with
                    RunMetadata =
                        { SessionRunMetadata.defaultValue with
                            Model = Some "test-model"
                            ProviderRuntime = Some "test-provider/runtime"
                            ReasoningConfig = Some "reasoning=low"
                            PromptRevision = Some "prompt-v2" } }

            let session =
                match Session.createWithArtifactDirectory fixture.Snapshot config tempRoot with
                | Ok value -> value
                | Error error -> failwithf "Unexpected artifact session creation failure: %A" error

            let paths = session.ArtifactPaths |> Option.defaultWith (fun () -> failwith "Missing artifact paths.")
            use metadataDocument = JsonDocument.Parse(File.ReadAllText(paths.RunMetadataPath, Encoding.UTF8))
            let metadata = metadataDocument.RootElement
            Assert.Equal("test-model", metadata.GetProperty("model").GetString())
            Assert.Equal("test-provider/runtime", metadata.GetProperty("providerRuntime").GetString())
            Assert.Equal("reasoning=low", metadata.GetProperty("reasoningConfig").GetString())
            Assert.Equal("prompt-v2", metadata.GetProperty("promptRevision").GetString())
            Assert.Equal("unavailable", metadata.GetProperty("toolRevision").GetString())
        finally
            if Directory.Exists(tempRoot) then
                Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``CLI startup metadata is persisted in run metadata`` () =
        let fixture = MilestoneZeroGraph.create ()
        let tempRoot =
            Path.Combine(Path.GetTempPath(), "nyamnyam-agent-eval-cli-metadata-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(tempRoot) |> ignore

        try
            let parsed =
                match
                    Program.AgentEvalCommandLine.parse
                        [ "--input"
                          "snapshot.json"
                          "--model"
                          "test-model"
                          "--provider-runtime"
                          "test-provider/runtime"
                          "--reasoning-config"
                          "reasoning=low"
                          "--prompt-revision"
                          "prompt-v2"
                          "--tool-revision"
                          "tool-v3" ]
                with
                | Ok value -> value
                | Error error -> failwithf "Unexpected CLI parse failure: %s" error

            let config =
                { SessionConfig.defaultValue with
                    RunMetadata = parsed.RunMetadata }

            let session =
                match Session.createWithArtifactDirectory fixture.Snapshot config tempRoot with
                | Ok value -> value
                | Error error -> failwithf "Unexpected artifact session creation failure: %A" error

            let paths = session.ArtifactPaths |> Option.defaultWith (fun () -> failwith "Missing artifact paths.")
            use metadataDocument = JsonDocument.Parse(File.ReadAllText(paths.RunMetadataPath, Encoding.UTF8))
            let metadata = metadataDocument.RootElement
            Assert.Equal("test-model", metadata.GetProperty("model").GetString())
            Assert.Equal("test-provider/runtime", metadata.GetProperty("providerRuntime").GetString())
            Assert.Equal("reasoning=low", metadata.GetProperty("reasoningConfig").GetString())
            Assert.Equal("prompt-v2", metadata.GetProperty("promptRevision").GetString())
            Assert.Equal("tool-v3", metadata.GetProperty("toolRevision").GetString())
        finally
            if Directory.Exists(tempRoot) then
                Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``artifact sessions under one root keep distinct prior evidence`` () =
        let fixture = MilestoneZeroGraph.create ()
        let tempRoot =
            Path.Combine(Path.GetTempPath(), "nyamnyam-agent-eval-shared-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(tempRoot) |> ignore

        try
            let firstSession =
                match Session.createWithArtifactDirectory fixture.Snapshot SessionConfig.defaultValue tempRoot with
                | Ok value -> value
                | Error error -> failwithf "Unexpected first artifact session creation failure: %A" error

            let firstRequest =
                command
                    [ "command", box "getType"
                      "typeId", box (StableId.value fixture.PolicyTypeId) ]

            firstSession.ExecuteLine(firstRequest) |> ignore
            let firstPaths = firstSession.ArtifactPaths |> Option.defaultWith (fun () -> failwith "Missing first artifact paths.")

            let secondSession =
                match Session.createWithArtifactDirectory fixture.Snapshot SessionConfig.defaultValue tempRoot with
                | Ok value -> value
                | Error error -> failwithf "Unexpected second artifact session creation failure: %A" error

            let secondRequest = command [ "command", box "state" ]
            secondSession.ExecuteLine(secondRequest) |> ignore
            let secondPaths = secondSession.ArtifactPaths |> Option.defaultWith (fun () -> failwith "Missing second artifact paths.")

            Assert.NotEqual<string>(firstPaths.DirectoryPath, secondPaths.DirectoryPath)
            Assert.NotEqual<string>(firstPaths.EventLogPath, secondPaths.EventLogPath)
            Assert.Equal(1, File.ReadAllLines(firstPaths.EventLogPath).Length)
            Assert.Equal(1, File.ReadAllLines(secondPaths.EventLogPath).Length)
        finally
            if Directory.Exists(tempRoot) then
                Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``artifact failure after commit preserves the committed protocol response`` () =
        let fixture = MilestoneZeroGraph.create ()
        let tempRoot =
            Path.Combine(Path.GetTempPath(), "nyamnyam-agent-eval-write-failure-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(tempRoot) |> ignore

        try
            let session =
                match Session.createWithArtifactDirectory fixture.Snapshot SessionConfig.defaultValue tempRoot with
                | Ok value -> value
                | Error error -> failwithf "Unexpected artifact session creation failure: %A" error

            let proposalResponse =
                simulateRenameFunction fixture.ValidateFunctionId "validate_profile"
                |> execute session

            let paths = session.ArtifactPaths |> Option.defaultWith (fun () -> failwith "Missing artifact paths.")
            Directory.Delete(paths.DirectoryPath, true)

            let response = commitCommand "commit" (proposalHandle proposalResponse) |> execute session
            Assert.Equal("committed", resultStatus response)

            use responseDocument = JsonDocument.Parse(response)
            let persistence = responseDocument.RootElement.GetProperty("artifactPersistence")
            Assert.Equal("invalidated", persistence.GetProperty("status").GetString())
            Assert.True(persistence.GetProperty("runInvalidated").GetBoolean())
            Assert.False(persistence.GetProperty("durable").GetBoolean())

            let harnessEvents = session.Usage.HarnessEvents
            Assert.Contains("artifact-write-failed:event-log", harnessEvents)
            Assert.Contains("artifact-write-failed:metrics", harnessEvents)
            harnessEvents
            |> List.iter (fun message -> Assert.DoesNotContain(tempRoot, message, StringComparison.Ordinal))
        finally
            if Directory.Exists(tempRoot) then
                Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``failing artifact writer invalidates client evidence and finalizes best effort`` () =
        let fixture = MilestoneZeroGraph.create ()
        let tempRoot =
            Path.Combine(Path.GetTempPath(), "nyamnyam-agent-eval-injected-writer-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(tempRoot) |> ignore

        try
            let paths =
                { DirectoryPath = tempRoot
                  EventLogPath = Path.Combine(tempRoot, "events.jsonl")
                  MetricsPath = Path.Combine(tempRoot, "metrics.json")
                  RunMetadataPath = Path.Combine(tempRoot, "run-metadata.json")
                  RunStatePath = Path.Combine(tempRoot, "run-state.json") }

            let mutable metricsWriteCount = 0

            let writer =
                { Paths = paths
                  WriteRunMetadata = fun value -> File.WriteAllText(paths.RunMetadataPath, value, Encoding.UTF8)
                  WriteEvent = fun value -> File.AppendAllText(paths.EventLogPath, value, Encoding.UTF8)
                  WriteMetrics =
                    fun value ->
                        metricsWriteCount <- metricsWriteCount + 1

                        if metricsWriteCount = 1 then
                            File.WriteAllText(paths.MetricsPath, value, Encoding.UTF8)
                        else
                            raise (IOException "Injected metrics writer failure.")
                  WriteRunState = fun value -> File.WriteAllText(paths.RunStatePath, value, Encoding.UTF8) }

            let session =
                match Session.createWithArtifactWriter fixture.Snapshot SessionConfig.defaultValue writer with
                | Ok value -> value
                | Error error -> failwithf "Unexpected injected-writer session creation failure: %A" error

            let response =
                command
                    [ "command", box "getType"
                      "typeId", box (StableId.value fixture.PolicyTypeId) ]
                |> execute session

            use responseDocument = JsonDocument.Parse(response)
            let persistence = responseDocument.RootElement.GetProperty("artifactPersistence")
            Assert.Equal("invalidated", persistence.GetProperty("status").GetString())
            Assert.True(persistence.GetProperty("runInvalidated").GetBoolean())
            Assert.False(persistence.GetProperty("durable").GetBoolean())
            Assert.Contains(
                persistence.GetProperty("failures").EnumerateArray(),
                fun failure -> failure.GetProperty("artifact").GetString() = "metrics"
            )

            let events = File.ReadAllLines(paths.EventLogPath)
            Assert.Single(events) |> ignore
            use eventDocument = JsonDocument.Parse(events[0])
            Assert.Equal(response, eventDocument.RootElement.GetProperty("response").GetString())

            let finalStatus = session.Finalize()
            Assert.True(finalStatus.Terminal)
            Assert.True(finalStatus.FinalizationDurable)
            Assert.True(finalStatus.Invalidated)
            Assert.False(finalStatus.Durable)
            Assert.True(File.Exists(paths.RunStatePath))

            use stateDocument = JsonDocument.Parse(File.ReadAllText(paths.RunStatePath, Encoding.UTF8))
            let state = stateDocument.RootElement
            Assert.Equal("terminal", state.GetProperty("state").GetString())
            let persistedStatus = state.GetProperty("artifactPersistence")
            Assert.Equal("invalidated", persistedStatus.GetProperty("status").GetString())
            Assert.False(persistedStatus.GetProperty("durable").GetBoolean())
            Assert.True(persistedStatus.GetProperty("terminal").GetBoolean())
        finally
            if Directory.Exists(tempRoot) then
                Directory.Delete(tempRoot, true)
