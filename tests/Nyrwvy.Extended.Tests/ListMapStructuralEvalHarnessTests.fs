namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.AgentEval.ListMapStructural
open Xunit

[<Trait("Suite", "Extended")>]
module ListMapStructuralEvalHarnessTests =
    let private tempDirectory () =
        let path = Path.Combine(Path.GetTempPath(), "nyrwvy-listmap-harness-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(path) |> ignore
        path

    let private createWorkspace (snapshot: Snapshot): AgentWorkspace =
        match AgentWorkspace.create snapshot with
        | Ok workspace -> workspace
        | Error issues -> failwithf "Unexpected workspace creation failure: %A" issues

    let private harnessConfig
        (directory: string)
        (fixture: string)
        (task: string)
        (stale: bool)
        : ListMapStructuralHarnessConfig =
        { ArtifactDirectory = directory
          FixtureName = fixture
          TaskId = task
          EnableStalePerturbation = stale
          RunMetadata =
            { ListMapStructuralHarnessRunMetadata.defaultValue with
                Model = Some "openai/gpt-5.6-terra"
                ProviderRuntime = Some "opencode"
                ReasoningConfig = Some "high"
                OpenCodeVersion = Some "test"
                PromptRevision = Some FrozenM2ListMapCorpus.promptRevision
                ToolRevision = Some "condition-test"
                RepositoryRevision = Some "test-revision" } }

    let private createHarness
        (snapshot: Snapshot)
        (config: ListMapStructuralHarnessConfig)
        : ListMapStructuralHarness =
        match ListMapStructuralHarness.Create(snapshot, config) with
        | Ok harness -> harness
        | Error error -> failwithf "Unexpected ListMap harness creation failure: %s" error

    let private request
        (fixture: M2ListMapEvaluationFixture)
        (workspace: AgentWorkspace)
        (sourceParameterId: ParameterId)
        (mapperFunctionId: FunctionId)
        : AgentReplaceFunctionBodyWithListMap =
        let target =
            match AgentWorkspace.getFunction workspace fixture.TargetFunctionId with
            | Ok value -> value
            | Error error -> failwithf "Target lookup failed: %A" error
        { TargetFunctionId = fixture.TargetFunctionId
          ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId workspace
          ExpectedCurrentResultNodeId = target.Body.ResultNodeId
          SourceParameterId = sourceParameterId
          MapperFunctionId = mapperFunctionId }

    let private simulate
        (workspace: AgentWorkspace)
        (structuralRequest: AgentReplaceFunctionBodyWithListMap)
        : AgentListMapStructuralChangeProposal =
        match AgentListMapStructuralWorkflow.simulate workspace structuralRequest with
        | Ok proposal -> proposal
        | Error error -> failwithf "Unexpected ListMap simulation failure: %A" error

    let private commit
        (workspace: AgentWorkspace)
        (proposal: AgentListMapStructuralChangeProposal)
        : AgentWorkspace * PublishedStructuralTransaction =
        match AgentListMapStructuralWorkflow.commit workspace proposal with
        | AgentStructuralCommitResult.Committed(next, publication) -> next, publication
        | AgentStructuralCommitResult.Rejected diagnostics ->
            failwithf "Unexpected ListMap commit rejection: %A" diagnostics

    let private usage commits commitAttempts simulations: ListMapStructuralSessionUsage =
        { ToolCalls = simulations + commitAttempts + 2
          FocusQueries = 0
          Simulations = simulations
          CommitAttempts = commitAttempts
          Commits = commits
          Rejects = 0
          ProtocolErrors = 0 }

    [<Fact>]
    let ``controlled ListMap CLI preserves ordinary mode and validates harness options`` () =
        match
            ListMapStructuralAgentEvalCommandLine.parse
                [ "--protocol"; ListMapStructuralProtocol.version
                  "--input"; "fixture.json" ]
        with
        | Error error -> failwithf "Ordinary ListMap CLI regressed: %s" error
        | Ok parsed -> Assert.True(Option.isNone parsed.HarnessConfig)

        match
            ListMapStructuralAgentEvalCommandLine.parse
                [ "--protocol"; ListMapStructuralProtocol.version
                  "--input"; "fixture.json"
                  "--artifact-dir"; "/tmp/listmap-eval"
                  "--fixture"; "m2-listmap-ready"
                  "--task-id"; "M2-L10"
                  "--stale-perturbation"; "m2-l10"
                  "--repository-revision"; "revision" ]
        with
        | Error error -> failwithf "Controlled ListMap CLI parse failed: %s" error
        | Ok parsed ->
            let harness = parsed.HarnessConfig |> Option.defaultWith (fun () -> failwith "Missing harness config.")
            Assert.Equal("M2-L10", harness.TaskId)
            Assert.True(harness.EnableStalePerturbation)
            Assert.Equal(Some "revision", harness.RunMetadata.RepositoryRevision)

        match
            ListMapStructuralAgentEvalCommandLine.parse
                [ "--protocol"; ListMapStructuralProtocol.version
                  "--input"; "fixture.json"
                  "--artifact-dir"; "/tmp/listmap-eval"
                  "--fixture"; "m2-listmap-ready" ]
        with
        | Ok _ -> failwith "Partial ListMap harness option group must reject."
        | Error error -> Assert.Contains("requires --artifact-dir, --fixture, and --task-id", error)

    [<Fact>]
    let ``controlled ListMap harness binds exact frozen fixture task and metadata`` () =
        let fixture = M2ListMapStructuralEvaluationCorpus.ready ()
        let directory = tempDirectory ()
        try
            let harness = createHarness fixture.Snapshot (harnessConfig directory fixture.Name "M2-L1" false)
            Assert.Equal("M2-L1", harness.Config.TaskId)
            Assert.Equal(fixture.TargetFunctionId, harness.Fixture.TargetFunctionId)
            Assert.Equal(fixture.SourceListParameterId, harness.Fixture.SourceListParameterId)
            Assert.Equal(fixture.CompatibleMapperFunctionId, harness.Fixture.CompatibleMapperFunctionId)
            Assert.True(File.Exists(Path.Combine(directory, "events.jsonl")))
            Assert.True(File.Exists(Path.Combine(directory, "harness-events.jsonl")))
            Assert.True(File.Exists(Path.Combine(directory, "run-metadata.json")))
            Assert.True(File.Exists(Path.Combine(directory, "run-state.json")))

            use metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "run-metadata.json")))
            let root = metadata.RootElement
            Assert.Equal(M2ListMapStructuralEvaluationCorpus.corpusId, root.GetProperty("corpusId").GetString())
            Assert.Equal(FrozenM2ListMapCorpus.corpusRevision, root.GetProperty("corpusRevision").GetString())
            Assert.Equal(ListMapStructuralProtocol.version, root.GetProperty("protocolVersion").GetString())
            Assert.Equal(FrozenM2ListMapCorpus.promptRevision, root.GetProperty("promptRevision").GetString())

            match
                ListMapStructuralHarness.Create(
                    fixture.Snapshot,
                    harnessConfig directory "m2-listmap-open-protected" "M2-L1" false
                )
            with
            | Ok _ -> failwith "Fixture/task mismatch must reject."
            | Error _ -> ()
        finally
            if Directory.Exists directory then Directory.Delete(directory, true)

    [<Fact>]
    let ``M2-L2 rejected source task finalizes to passed frozen oracle without mutation`` () =
        let fixture = M2ListMapStructuralEvaluationCorpus.ready ()
        let directory = tempDirectory ()
        try
            let workspace = createWorkspace fixture.Snapshot
            let harness = createHarness fixture.Snapshot (harnessConfig directory fixture.Name "M2-L2" false)
            request fixture workspace fixture.ForeignListParameterId fixture.CompatibleMapperFunctionId
            |> AgentListMapStructuralWorkflow.simulate workspace
            |> function
                | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) ->
                    Assert.Equal("StructuralListSourceParameterOutOfScope", diagnostic.Code)
                | other -> failwithf "Expected source-scope rejection, got %A" other

            harness.RecordEvent(
                "{\"command\":\"simulateListMapV1\"}",
                "{\"ok\":true,\"result\":{\"status\":\"rejected\",\"diagnostics\":[{\"code\":\"StructuralListSourceParameterOutOfScope\",\"message\":\"expected\"}]}}"
            )
            Assert.True(harness.Finalize(workspace, usage 0 0 1, 0))
            use metrics = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "metrics.json")))
            Assert.True(metrics.RootElement.GetProperty("oraclePassed").GetBoolean())
            Assert.Equal(0, metrics.RootElement.GetProperty("commits").GetInt32())
            Assert.True(metrics.RootElement.GetProperty("zeroUnrelatedSemanticChurn").GetBoolean())
        finally
            if Directory.Exists directory then Directory.Delete(directory, true)

    [<Fact>]
    let ``M2-L1 commit writes durable true execution verification and passed oracle`` () =
        let fixture = M2ListMapStructuralEvaluationCorpus.ready ()
        let directory = tempDirectory ()
        try
            let workspace = createWorkspace fixture.Snapshot
            let harness = createHarness fixture.Snapshot (harnessConfig directory fixture.Name "M2-L1" false)
            let proposal =
                request fixture workspace fixture.SourceListParameterId fixture.CompatibleMapperFunctionId
                |> simulate workspace
            let committed, publication = commit workspace proposal
            harness.VerifyCommit(committed, publication)
            Assert.True(harness.Finalize(committed, usage 1 1 1, 0))

            let verificationDirectory = Path.Combine(directory, "verification-" + StableId.value publication.TransactionId)
            use verification = JsonDocument.Parse(File.ReadAllText(Path.Combine(verificationDirectory, "verification.json")))
            let root = verification.RootElement
            Assert.Equal("passed", root.GetProperty("status").GetString())
            Assert.True(root.GetProperty("evidenceDurable").GetBoolean())
            Assert.Equal("true", root.GetProperty("actualOutput").GetString())
            Assert.Equal("true", root.GetProperty("expectedOutput").GetString())

            use metrics = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "metrics.json")))
            Assert.True(metrics.RootElement.GetProperty("oraclePassed").GetBoolean())
            Assert.Equal(1, metrics.RootElement.GetProperty("verificationReports").GetArrayLength())
        finally
            if Directory.Exists directory then Directory.Delete(directory, true)

    [<Fact>]
    let ``M2-L10 harness publication makes proposal stale then fresh commit preserves advancement`` () =
        let fixture = M2ListMapStructuralEvaluationCorpus.ready ()
        let directory = tempDirectory ()
        try
            let workspace = createWorkspace fixture.Snapshot
            let harness = createHarness fixture.Snapshot (harnessConfig directory fixture.Name "M2-L10" true)
            let staleProposal =
                request fixture workspace fixture.SourceListParameterId fixture.CompatibleMapperFunctionId
                |> simulate workspace
            let advanced = harness.MaybeApplyStalePerturbation(workspace, fixture.TargetFunctionId)
            Assert.True(harness.PerturbationApplied)
            Assert.NotEqual(AgentWorkspace.currentSnapshotId workspace, AgentWorkspace.currentSnapshotId advanced)

            match AgentListMapStructuralWorkflow.commit advanced staleProposal with
            | AgentStructuralCommitResult.Rejected [ diagnostic ] -> Assert.Equal("StaleBaseSnapshot", diagnostic.Code)
            | other -> failwithf "Expected stale ListMap proposal rejection, got %A" other

            harness.RecordEvent(
                "{\"command\":\"commitListMapV1\"}",
                "{\"ok\":true,\"result\":{\"status\":\"rejected\",\"diagnostics\":[{\"code\":\"StaleBaseSnapshot\",\"message\":\"expected\"}]}}"
            )

            let freshProposal =
                request fixture advanced fixture.SourceListParameterId fixture.CompatibleMapperFunctionId
                |> simulate advanced
            let committed, publication = commit advanced freshProposal
            harness.VerifyCommit(committed, publication)
            Assert.True(harness.Finalize(committed, usage 1 2 2, 1))

            let unrelated =
                match AgentWorkspace.getFunction committed fixture.UnrelatedFunctionId with
                | Ok value -> value
                | Error error -> failwithf "Unrelated function lookup failed: %A" error
            Assert.Equal("unrelated_marker_harness_advanced", unrelated.DisplayName)

            use metrics = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "metrics.json")))
            Assert.True(metrics.RootElement.GetProperty("oraclePassed").GetBoolean())
            Assert.True(metrics.RootElement.GetProperty("harnessPerturbationApplied").GetBoolean())
            Assert.True(metrics.RootElement.GetProperty("harnessPerturbationPreserved").GetBoolean())
            Assert.Contains("StaleBaseSnapshot", metrics.RootElement.GetProperty("observedDiagnosticCodes").GetRawText())
        finally
            if Directory.Exists directory then Directory.Delete(directory, true)
