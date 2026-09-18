namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.AgentEval.Structural
open Xunit

module StructuralEvalHarnessTests =
    let private tempDirectory () =
        let path = Path.Combine(Path.GetTempPath(), "nyrwvy-structural-harness-" + Guid.NewGuid().ToString("N"))
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
        : StructuralHarnessConfig =
        { ArtifactDirectory = directory
          FixtureName = fixture
          TaskId = task
          EnableStalePerturbation = stale
          RunMetadata =
            { StructuralHarnessRunMetadata.defaultValue with
                Model = Some "provider/model"
                ProviderRuntime = Some "opencode"
                ReasoningConfig = Some "high"
                OpenCodeVersion = Some "test"
                PromptRevision = Some "m2-structural-agent-v1"
                ToolRevision = Some "m2-controlled-harness-v1"
                RepositoryRevision = Some "test-revision" } }

    let private createHarness (snapshot: Snapshot) (config: StructuralHarnessConfig): StructuralHarness =
        match StructuralHarness.Create(snapshot, config) with
        | Ok harness -> harness
        | Error error -> failwithf "Unexpected harness creation failure: %s" error

    let private successRequest
        (fixture: M2StructuralEvaluationFixture)
        (baseSnapshotId: SnapshotId)
        (resultNodeId: GraphNodeId)
        : AgentReplaceFunctionBodyWithFloat64Recipe =
        { TargetFunctionId = fixture.TargetFunctionId
          ExpectedBaseSnapshotId = baseSnapshotId
          ExpectedCurrentResultNodeId = resultNodeId
          Replacement =
            Float64ExpressionRecipe.Add(
                Float64ExpressionRecipe.Parameter fixture.TargetFloatParameterId,
                Float64ExpressionRecipe.Literal(Float64Bits.create 0x4002000000000000UL)
            ) }

    let private simulate
        (workspace: AgentWorkspace)
        (request: AgentReplaceFunctionBodyWithFloat64Recipe)
        : AgentStructuralChangeProposal =
        match AgentStructuralWorkflow.simulate workspace request with
        | Ok proposal -> proposal
        | Error error -> failwithf "Unexpected simulation failure: %A" error

    let private commit
        (workspace: AgentWorkspace)
        (proposal: AgentStructuralChangeProposal)
        : AgentWorkspace * PublishedStructuralTransaction =
        match AgentStructuralWorkflow.commit workspace proposal with
        | AgentStructuralCommitResult.Committed(next, publication) -> next, publication
        | AgentStructuralCommitResult.Rejected diagnostics -> failwithf "Unexpected commit rejection: %A" diagnostics

    let private usage: StructuralSessionUsage =
        { ToolCalls = 4
          FocusQueries = 1
          Simulations = 1
          CommitAttempts = 1
          Commits = 1
          Rejects = 0
          ProtocolErrors = 0 }

    [<Fact>]
    let ``controlled structural CLI preserves ordinary protocol and validates harness option groups`` () =
        match StructuralAgentEvalCommandLine.parse [ "--protocol"; StructuralProtocol.version; "--input"; "fixture.json" ] with
        | Error error -> failwithf "Ordinary structural CLI regressed: %s" error
        | Ok parsed -> Assert.True(Option.isNone parsed.HarnessConfig)

        match
            StructuralAgentEvalCommandLine.parse
                [ "--protocol"; StructuralProtocol.version
                  "--input"; "fixture.json"
                  "--artifact-dir"; "/tmp/structural-eval"
                  "--fixture"; "m2-ready"
                  "--task-id"; "M2-E10"
                  "--stale-perturbation"; "m2-e10"
                  "--repository-revision"; "revision" ]
        with
        | Error error -> failwithf "Controlled structural CLI parse failed: %s" error
        | Ok parsed ->
            let harness = parsed.HarnessConfig |> Option.defaultWith (fun () -> failwith "Missing harness config.")
            Assert.Equal("M2-E10", harness.TaskId)
            Assert.True(harness.EnableStalePerturbation)
            Assert.Equal(Some "revision", harness.RunMetadata.RepositoryRevision)

        match
            StructuralAgentEvalCommandLine.parse
                [ "--protocol"; StructuralProtocol.version
                  "--input"; "fixture.json"
                  "--artifact-dir"; "/tmp/structural-eval"
                  "--fixture"; "m2-ready" ]
        with
        | Ok _ -> failwith "Partial harness option group must reject."
        | Error error -> Assert.Contains("requires --artifact-dir, --fixture, and --task-id", error)

    [<Fact>]
    let ``controlled harness binds exact frozen fixture task and metadata`` () =
        let fixture = M2StructuralEvaluationCorpus.ready ()
        let directory = tempDirectory ()
        try
            let harness = createHarness fixture.Snapshot (harnessConfig directory fixture.Name "M2-E1" false)
            Assert.Equal("M2-E1", harness.Config.TaskId)
            Assert.Equal(fixture.TargetFunctionId, harness.Fixture.TargetFunctionId)
            Assert.Equal(fixture.ForeignFunctionId, harness.Fixture.ForeignFunctionId)
            Assert.True(File.Exists(Path.Combine(directory, "events.jsonl")))
            Assert.True(File.Exists(Path.Combine(directory, "harness-events.jsonl")))
            Assert.True(File.Exists(Path.Combine(directory, "run-metadata.json")))
            Assert.True(File.Exists(Path.Combine(directory, "run-state.json")))

            use metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "run-metadata.json")))
            let root = metadata.RootElement
            Assert.Equal(M2StructuralEvaluationCorpus.corpusId, root.GetProperty("corpusId").GetString())
            Assert.Equal(FrozenM2StructuralCorpus.corpusRevision, root.GetProperty("corpusRevision").GetString())
            Assert.Equal(StructuralProtocol.version, root.GetProperty("protocolVersion").GetString())
            Assert.Equal("provider/model", root.GetProperty("model").GetString())

            match StructuralHarness.Create(fixture.Snapshot, harnessConfig directory "m2-open-protected" "M2-E1" false) with
            | Ok _ -> failwith "Fixture/task mismatch must reject."
            | Error _ -> ()
        finally
            if Directory.Exists directory then Directory.Delete(directory, true)

    [<Fact>]
    let ``M2-E10 harness advance makes reviewed proposal stale and records private receipt`` () =
        let fixture = M2StructuralEvaluationCorpus.ready ()
        let directory = tempDirectory ()
        try
            let harness = createHarness fixture.Snapshot (harnessConfig directory fixture.Name "M2-E10" true)
            let workspace = createWorkspace fixture.Snapshot
            let request = successRequest fixture fixture.Snapshot.Id fixture.TargetResultNodeId
            let proposal = simulate workspace request
            let advanced = harness.MaybeApplyStalePerturbation(workspace, fixture.TargetFunctionId)
            if AgentWorkspace.currentSnapshotId workspace = AgentWorkspace.currentSnapshotId advanced then
                failwith "Harness stale perturbation did not advance the workspace."
            Assert.True(harness.PerturbationApplied)

            match AgentStructuralWorkflow.commit advanced proposal with
            | AgentStructuralCommitResult.Rejected [ diagnostic ] -> Assert.Equal("StaleBaseSnapshot", diagnostic.Code)
            | other -> failwithf "Expected stale proposal rejection, got %A" other

            let harnessEvents = File.ReadAllText(Path.Combine(directory, "harness-events.jsonl"))
            Assert.Contains("stalePerturbationCommitted", harnessEvents)
            Assert.Contains(StableId.value fixture.UnrelatedFunctionId, harnessEvents)

            Assert.True(harness.Finalize(advanced, usage, 1))
            use metrics = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "metrics.json")))
            Assert.True(metrics.RootElement.GetProperty("zeroUnrelatedSemanticChurn").GetBoolean())
            Assert.True(metrics.RootElement.GetProperty("harnessPerturbationApplied").GetBoolean())
            Assert.True(metrics.RootElement.GetProperty("harnessPerturbationPreserved").GetBoolean())
        finally
            if Directory.Exists directory then Directory.Delete(directory, true)

    [<Fact>]
    let ``committed M2-E1 writes exact durable execution verification and terminal metrics`` () =
        let fixture = M2StructuralEvaluationCorpus.ready ()
        let directory = tempDirectory ()
        try
            let harness = createHarness fixture.Snapshot (harnessConfig directory fixture.Name "M2-E1" false)
            let workspace = createWorkspace fixture.Snapshot
            let proposal = successRequest fixture fixture.Snapshot.Id fixture.TargetResultNodeId |> simulate workspace
            let committed, publication = commit workspace proposal

            harness.RecordEvent("{\"command\":\"simulateStructuralV1\"}", "{\"ok\":true}")
            harness.VerifyCommit(committed, publication)
            Assert.True(harness.Finalize(committed, usage, 0))

            let verificationDirectory = Path.Combine(directory, "verification-" + StableId.value publication.TransactionId)
            let verificationPath = Path.Combine(verificationDirectory, "verification.json")
            Assert.True(File.Exists verificationPath)
            use verification = JsonDocument.Parse(File.ReadAllText verificationPath)
            let root = verification.RootElement
            Assert.Equal("passed", root.GetProperty("status").GetString())
            Assert.True(root.GetProperty("evidenceDurable").GetBoolean())
            Assert.Equal("3.75", root.GetProperty("actualOutput").GetString())
            Assert.Equal("3.75", root.GetProperty("expectedOutput").GetString())

            use metrics = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "metrics.json")))
            Assert.Equal(4, metrics.RootElement.GetProperty("toolCalls").GetInt32())
            Assert.Equal(1, metrics.RootElement.GetProperty("verificationReports").GetArrayLength())
            Assert.True(metrics.RootElement.GetProperty("zeroUnrelatedSemanticChurn").GetBoolean())
            Assert.True(metrics.RootElement.GetProperty("harnessPerturbationPreserved").GetBoolean())

            use state = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "run-state.json")))
            Assert.Equal("terminal", state.RootElement.GetProperty("state").GetString())
            Assert.Equal(StableId.value publication.PublishedSnapshotId, state.RootElement.GetProperty("currentSnapshotId").GetString())
            Assert.True(state.RootElement.GetProperty("zeroUnrelatedSemanticChurn").GetBoolean())
        finally
            if Directory.Exists directory then Directory.Delete(directory, true)
