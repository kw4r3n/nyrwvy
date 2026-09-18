namespace Nyrwvy.Core.Tests

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.AgentEval
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
                  "--opencode-version"
                  "1.2.3"
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
            Assert.Equal(Some "1.2.3", arguments.RunMetadata.OpenCodeVersion)
            Assert.Equal(Some "prompt-v2", arguments.RunMetadata.PromptRevision)
            Assert.Equal(Some "tool-v3", arguments.RunMetadata.ToolRevision)

        match Program.AgentEvalCommandLine.parse [ "--input"; "snapshot.json" ] with
        | Error error -> failwithf "Unexpected default CLI parse failure: %s" error
        | Ok arguments -> Assert.Equal(SessionRunMetadata.defaultValue, arguments.RunMetadata)

        match Program.AgentEvalCommandLine.parse [ "--input"; "snapshot.json"; "--model" ] with
        | Ok _ -> failwith "A metadata option without a value must be rejected."
        | Error error -> Assert.Contains("missing its value", error, StringComparison.Ordinal)

    [<Fact>]
    let ``CLI stale perturbation requires a complete startup trigger and advance configuration`` () =
        let parsed =
            Program.AgentEvalCommandLine.parse
                [ "--input"
                  "snapshot.json"
                  "--stale-perturbation-trigger-function-id"
                  "function-validate-user"
                  "--stale-perturbation-trigger-name"
                  "validate_profile"
                  "--stale-perturbation-type-id"
                  "type-raw-user-input"
                  "--stale-perturbation-name"
                  "RawAccountInput" ]

        match parsed with
        | Error error -> failwithf "Unexpected stale perturbation parse failure: %s" error
        | Ok arguments ->
            let perturbation = arguments.StalePerturbation |> Option.defaultWith (fun () -> failwith "Missing perturbation.")
            Assert.Equal("function-validate-user", StableId.value perturbation.TriggerFunctionId)
            Assert.Equal("validate_profile", perturbation.TriggerNewDisplayName)
            Assert.Equal("type-raw-user-input", StableId.value perturbation.AdvanceTypeId)
            Assert.Equal("RawAccountInput", perturbation.AdvanceNewDisplayName)

        match
            Program.AgentEvalCommandLine.parse
                [ "--input"
                  "snapshot.json"
                  "--stale-perturbation-type-id"
                  "type-raw-user-input"
                  "--stale-perturbation-name"
                  "RawAccountInput" ]
        with
        | Ok _ -> failwith "A stale perturbation without its trigger configuration must be rejected."
        | Error error -> Assert.Contains("supplied together", error, StringComparison.Ordinal)

    let private simulateRenameFunction functionId newDisplayName =
        command
            [ "command", box "simulate"
              "operations",
                arrayValue
                    [ objectValue
                        [ "kind", box "renameFunction"
                          "functionId", box (StableId.value functionId)
                          "newDisplayName", box newDisplayName ] ] ]

    let private simulateRenameType typeId newDisplayName =
        command
            [ "command", box "simulate"
              "operations",
                arrayValue
                    [ objectValue
                        [ "kind", box "renameType"
                          "typeId", box (StableId.value typeId)
                          "newDisplayName", box newDisplayName ] ] ]

    let private simulateOperations operations =
        command
            [ "command", box "simulate"
              "operations", arrayValue operations ]

    let private commitCommand commandName handle =
        command
            [ "command", box commandName
              "proposalHandle", box handle ]

    [<Fact>]
    let ``runner returns a failure exit code for an invalidated final artifact status`` () =
        let validStatus =
            { Enabled = true
              Invalidated = false
              Durable = true
              Terminal = true
              FinalizationAttempted = true
              FinalizationDurable = true
              Failures = [] }

        let invalidatedStatus = { validStatus with Invalidated = true; Durable = false }
        Assert.Equal(0, Program.finalizationExitCode validStatus)
        Assert.Equal(1, Program.finalizationExitCode invalidatedStatus)

        let failedFinalizationStatus = { validStatus with Terminal = false; FinalizationDurable = false }
        Assert.Equal(1, Program.finalizationExitCode failedFinalizationStatus)

        let notAttemptedStatus = { validStatus with FinalizationAttempted = false }
        Assert.Equal(1, Program.finalizationExitCode notAttemptedStatus)

        let disabledStatus = { validStatus with Enabled = false; Durable = false; Terminal = false; FinalizationDurable = false }
        Assert.Equal(0, Program.finalizationExitCode disabledStatus)

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
                        { TriggerFunctionId = fixture.ValidateFunctionId
                          TriggerNewDisplayName = "validate_profile"
                          AdvanceTypeId = rawUserInput.Id
                          AdvanceNewDisplayName = "RawAccountInput" } }
        let session = createSession fixture.Snapshot config

        let unrelatedProposal =
            simulateRenameType fixture.PolicyTypeId "ValidationPolicyPreview"
            |> execute session

        Assert.Equal("proposed", resultStatus unrelatedProposal)
        Assert.Empty(session.Usage.HarnessReceipts)
        Assert.Empty(session.Usage.HarnessEvents)

        let staleProposal =
            simulateRenameFunction fixture.ValidateFunctionId "validate_profile"
            |> execute session
        let staleHandle = proposalHandle staleProposal
        use staleProposalDocument = JsonDocument.Parse(staleProposal)
        let reviewedRename =
            staleProposalDocument.RootElement
                .GetProperty("result")
                .GetProperty("review")
                .GetProperty("diff")
                .GetProperty("renames")
            |> fun values -> values.EnumerateArray() |> Seq.exactlyOne
        Assert.Equal("function", reviewedRename.GetProperty("kind").GetString())
        Assert.Equal(StableId.value fixture.ValidateFunctionId, reviewedRename.GetProperty("id").GetString())
        Assert.Equal("validate_profile", reviewedRename.GetProperty("newDisplayName").GetString())
        Assert.Equal(1, session.Usage.HarnessReceipts.Length)

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
        Assert.DoesNotContain("harnessEvents", metricsResponse, StringComparison.Ordinal)
        Assert.DoesNotContain("harnessReceipts", metricsResponse, StringComparison.Ordinal)
        let agentMetrics = metricsDocument.RootElement.GetProperty("result").GetProperty("metrics")
        let mutable exposedHarnessEvents = Unchecked.defaultof<JsonElement>
        let mutable exposedHarnessReceipts = Unchecked.defaultof<JsonElement>
        Assert.False(agentMetrics.TryGetProperty("harnessEvents", &exposedHarnessEvents))
        Assert.False(agentMetrics.TryGetProperty("harnessReceipts", &exposedHarnessReceipts))
        let harnessEvents =
            session.Usage.HarnessEvents
        Assert.Equal(1, harnessEvents.Length)

        let harnessReceipt = session.Usage.HarnessReceipts |> List.exactlyOne
        Assert.Equal("stalePerturbation", harnessReceipt.Kind)
        Assert.False(String.IsNullOrWhiteSpace(harnessReceipt.TransactionId |> StableId.value))
        Assert.Equal(fixture.Snapshot.Id, harnessReceipt.BaseSnapshotId)
        Assert.False(String.IsNullOrWhiteSpace(harnessReceipt.PublishedSnapshotId |> StableId.value))
        Assert.Equal(TransactionReadiness.SemanticallyReady, harnessReceipt.Readiness)
        Assert.Single(harnessReceipt.Diff.Renames) |> ignore
        Assert.Equal(
            rawUserInput.Id,
            match harnessReceipt.Diff.Renames with
            | [ TypeRenamed(typeId, _, _) ] -> typeId
            | _ -> failwith "Expected one injected type rename."
        )

    [<Fact>]
    let ``E5 trigger requires one effective target rename and ignores requested operation lists`` () =
        let fixture = MilestoneZeroGraph.create ()
        let rawUserInput =
            fixture.Snapshot.Types
            |> List.find (fun semanticType -> semanticType.DisplayName = "RawUserInput")

        let config =
            { SessionConfig.defaultValue with
                StalePerturbation =
                    Some
                        { TriggerFunctionId = fixture.ValidateFunctionId
                          TriggerNewDisplayName = "validate_profile"
                          AdvanceTypeId = rawUserInput.Id
                          AdvanceNewDisplayName = "RawAccountInput" } }
        let session = createSession fixture.Snapshot config

        let multiOperationResponse =
            simulateOperations
                [ objectValue
                      [ "kind", box "renameFunction"
                        "functionId", box (StableId.value fixture.ValidateFunctionId)
                        "newDisplayName", box "validate_profile" ]
                  objectValue
                      [ "kind", box "renameType"
                        "typeId", box (StableId.value fixture.PolicyTypeId)
                        "newDisplayName", box "ValidationPolicyPreview" ] ]
            |> execute session

        Assert.Equal("proposed", resultStatus multiOperationResponse)
        Assert.Empty(session.Usage.HarnessReceipts)
        Assert.Empty(session.Usage.HarnessEvents)

        let nonTargetResponse = simulateRenameType fixture.PolicyTypeId "ValidationPolicyOther" |> execute session
        Assert.Equal("proposed", resultStatus nonTargetResponse)
        Assert.Empty(session.Usage.HarnessReceipts)
        Assert.Empty(session.Usage.HarnessEvents)

        let targetResponse = simulateRenameFunction fixture.ValidateFunctionId "validate_profile" |> execute session
        Assert.Equal("proposed", resultStatus targetResponse)
        Assert.Single(session.Usage.HarnessReceipts) |> ignore

    [<Fact>]
    let ``failed E5 harness advance invalidates the run and discards the triggering proposal`` () =
        let fixture = MilestoneZeroGraph.create ()
        let missingTypeId = StableId.createForTesting<TypeTag> "type-missing-for-e5"
        let artifactDirectory =
            Path.Combine(Path.GetTempPath(), "nyrwvy-agent-eval-harness-failure-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(artifactDirectory) |> ignore

        try
            let config =
                { SessionConfig.defaultValue with
                    StalePerturbation =
                        Some
                            { TriggerFunctionId = fixture.ValidateFunctionId
                              TriggerNewDisplayName = "validate_profile"
                              AdvanceTypeId = missingTypeId
                              AdvanceNewDisplayName = "RawAccountInput" } }
            let session =
                match Session.createWithArtifactDirectory fixture.Snapshot config artifactDirectory with
                | Ok value -> value
                | Error error -> failwithf "Unexpected harness-failure session creation error: %A" error

            let failedResponse = simulateRenameFunction fixture.ValidateFunctionId "validate_profile" |> execute session
            Assert.Equal("harnessFailure", resultStatus failedResponse)
            Assert.Contains("run is invalidated", failedResponse, StringComparison.Ordinal)
            Assert.True(session.ArtifactStatus.Invalidated)
            Assert.Empty(session.Usage.HarnessReceipts)
            Assert.Contains(
                session.Usage.HarnessEvents,
                fun message -> message.StartsWith("stale-perturbation-advance-failed:", StringComparison.Ordinal)
            )

            let paths = session.ArtifactPaths |> Option.defaultWith (fun () -> failwith "Missing artifact paths.")
            use stateDocument = JsonDocument.Parse(File.ReadAllText(paths.RunStatePath, Encoding.UTF8))
            Assert.Equal("invalidated", stateDocument.RootElement.GetProperty("artifactPersistence").GetProperty("status").GetString())

            let blockedCommit = commitCommand "commit" "proposal-1" |> execute session
            Assert.Equal("harnessFailure", resultStatus blockedCommit)
            Assert.Equal(0, session.Usage.CommitAttemptCount)
            Assert.Equal(0, session.Usage.CommitCount)
        finally
            if Directory.Exists(artifactDirectory) then
                Directory.Delete(artifactDirectory, true)

    [<Fact>]
    let ``harness receipt stays in artifacts and operator usage rather than protocol metrics`` () =
        let fixture = MilestoneZeroGraph.create ()
        let rawUserInput =
            fixture.Snapshot.Types
            |> List.find (fun semanticType -> semanticType.DisplayName = "RawUserInput")
        let artifactDirectory =
            Path.Combine(Path.GetTempPath(), "nyrwvy-agent-eval-harness-artifact-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(artifactDirectory) |> ignore

        try
            let config =
                { SessionConfig.defaultValue with
                    StalePerturbation =
                        Some
                            { TriggerFunctionId = fixture.ValidateFunctionId
                              TriggerNewDisplayName = "validate_profile"
                              AdvanceTypeId = rawUserInput.Id
                              AdvanceNewDisplayName = "RawAccountInput" } }

            let session =
                match Session.createWithArtifactDirectory fixture.Snapshot config artifactDirectory with
                | Ok value -> value
                | Error error -> failwithf "Unexpected artifact session creation failure: %A" error

            let response = simulateRenameFunction fixture.ValidateFunctionId "validate_profile" |> execute session
            Assert.DoesNotContain("harnessEvents", response, StringComparison.Ordinal)
            Assert.DoesNotContain("harnessReceipts", response, StringComparison.Ordinal)

            let paths = session.ArtifactPaths |> Option.defaultWith (fun () -> failwith "Missing artifact paths.")
            use metricsDocument = JsonDocument.Parse(File.ReadAllText(paths.MetricsPath, Encoding.UTF8))
            let metrics = metricsDocument.RootElement
            let persistedReceipts = metrics.GetProperty("harnessReceipts")
            Assert.Single(persistedReceipts.EnumerateArray()) |> ignore

            let events = File.ReadAllLines(paths.EventLogPath, Encoding.UTF8)
            Assert.Single(events) |> ignore
            use eventDocument = JsonDocument.Parse(events[0])
            Assert.Single(eventDocument.RootElement.GetProperty("harnessReceipts").EnumerateArray()) |> ignore
            Assert.Single(session.Usage.HarnessReceipts) |> ignore
        finally
            if Directory.Exists(artifactDirectory) then
                Directory.Delete(artifactDirectory, true)

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
            Path.Combine(Path.GetTempPath(), "nyrwvy-agent-eval-" + Guid.NewGuid().ToString("N"))

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
            let mutable harnessEvents = Unchecked.defaultof<JsonElement>
            let mutable harnessReceipts = Unchecked.defaultof<JsonElement>
            Assert.True(metrics.TryGetProperty("harnessEvents", &harnessEvents))
            Assert.True(metrics.TryGetProperty("harnessReceipts", &harnessReceipts))
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
    let ``artifact root session directory and files are private on Unix`` () =
        let fixture = MilestoneZeroGraph.create ()
        let tempRoot =
            Path.Combine(Path.GetTempPath(), "nyrwvy-agent-eval-permissions-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(tempRoot) |> ignore

        try
            let session =
                match Session.createWithArtifactDirectory fixture.Snapshot SessionConfig.defaultValue tempRoot with
                | Ok value -> value
                | Error error -> failwithf "Unexpected artifact session creation failure: %A" error

            if not (OperatingSystem.IsWindows()) then
                let privateDirectory =
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                let privateFile = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
                let paths = session.ArtifactPaths |> Option.defaultWith (fun () -> failwith "Missing artifact paths.")
                Assert.Equal(privateDirectory, File.GetUnixFileMode(tempRoot))
                Assert.Equal(privateDirectory, File.GetUnixFileMode(paths.DirectoryPath))

                [ paths.EventLogPath; paths.MetricsPath; paths.RunMetadataPath; paths.RunStatePath ]
                |> List.iter (fun path -> Assert.Equal(privateFile, File.GetUnixFileMode(path)))
        finally
            if Directory.Exists(tempRoot) then
                Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``finalized session rejects commands without changing workspace or artifacts`` () =
        let fixture = MilestoneZeroGraph.create ()
        let tempRoot =
            Path.Combine(Path.GetTempPath(), "nyrwvy-agent-eval-finalized-" + Guid.NewGuid().ToString("N"))

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
            Path.Combine(Path.GetTempPath(), "nyrwvy-agent-eval-metadata-" + Guid.NewGuid().ToString("N"))

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
            Assert.Equal("unavailable", metadata.GetProperty("openCodeVersion").GetString())
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
            Path.Combine(Path.GetTempPath(), "nyrwvy-agent-eval-supplied-metadata-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(tempRoot) |> ignore

        try
            let config =
                { SessionConfig.defaultValue with
                    RunMetadata =
                        { SessionRunMetadata.defaultValue with
                            Model = Some "test-model"
                            ProviderRuntime = Some "test-provider/runtime"
                            ReasoningConfig = Some "reasoning=low"
                            OpenCodeVersion = Some "1.2.3"
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
            Assert.Equal("1.2.3", metadata.GetProperty("openCodeVersion").GetString())
            Assert.Equal("prompt-v2", metadata.GetProperty("promptRevision").GetString())
            Assert.Equal("unavailable", metadata.GetProperty("toolRevision").GetString())
        finally
            if Directory.Exists(tempRoot) then
                Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``CLI startup metadata is persisted in run metadata`` () =
        let fixture = MilestoneZeroGraph.create ()
        let tempRoot =
            Path.Combine(Path.GetTempPath(), "nyrwvy-agent-eval-cli-metadata-" + Guid.NewGuid().ToString("N"))

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
                          "--opencode-version"
                          "1.2.3"
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
            Assert.Equal("1.2.3", metadata.GetProperty("openCodeVersion").GetString())
            Assert.Equal("prompt-v2", metadata.GetProperty("promptRevision").GetString())
            Assert.Equal("tool-v3", metadata.GetProperty("toolRevision").GetString())
        finally
            if Directory.Exists(tempRoot) then
                Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``artifact sessions under one root keep distinct prior evidence`` () =
        let fixture = MilestoneZeroGraph.create ()
        let tempRoot =
            Path.Combine(Path.GetTempPath(), "nyrwvy-agent-eval-shared-" + Guid.NewGuid().ToString("N"))

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
            Path.Combine(Path.GetTempPath(), "nyrwvy-agent-eval-write-failure-" + Guid.NewGuid().ToString("N"))

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
            Path.Combine(Path.GetTempPath(), "nyrwvy-agent-eval-injected-writer-" + Guid.NewGuid().ToString("N"))

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
            Assert.Equal(1, Program.finalizationExitCode finalStatus)
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
