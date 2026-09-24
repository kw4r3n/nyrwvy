namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.AgentEval.ListMapStructural
open Xunit

[<Trait("Suite", "Extended")>]
module M2ListMapStructuralEvaluationCorpusTests =
    let private createWorkspace (snapshot: Snapshot): AgentWorkspace =
        match AgentWorkspace.create snapshot with
        | Ok workspace -> workspace
        | Error issues -> failwithf "Unexpected workspace creation failure: %A" issues

    let private request
        (fixture: M2ListMapEvaluationFixture)
        (workspace: AgentWorkspace)
        (sourceParameterId: ParameterId)
        (mapperFunctionId: FunctionId)
        : AgentReplaceFunctionBodyWithListMap =
        { TargetFunctionId = fixture.TargetFunctionId
          ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId workspace
          ExpectedCurrentResultNodeId = fixture.TargetResultNodeId
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
        | AgentStructuralCommitResult.Committed(nextWorkspace, publication) -> nextWorkspace, publication
        | AgentStructuralCommitResult.Rejected diagnostics ->
            failwithf "Unexpected ListMap commit rejection: %A" diagnostics

    let private assertSimulationRejected
        (expected: string)
        (result: Result<AgentListMapStructuralChangeProposal, AgentStructuralWorkflowError>) =
        match result with
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) when diagnostic.Code = expected -> ()
        | other -> failwithf "Expected ListMap diagnostic %s, got %A" expected other

    let private findFunction (id: FunctionId) (snapshot: Snapshot): SemanticFunction =
        snapshot.Functions |> List.find (fun fn -> fn.Id = id)

    let private assertUuidV7 (id: GraphNodeId) =
        let payload = StableId.value id
        let mutable parsed = Guid.Empty
        Assert.True(Guid.TryParseExact(payload, "D", &parsed), $"Not a GUID: {payload}")
        Assert.Equal('7', payload[14])

    let private runDotnet (workingDirectory: string) (arguments: string list) =
        let startInfo = ProcessStartInfo("dotnet")
        startInfo.WorkingDirectory <- workingDirectory
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] <- "1"
        startInfo.Environment["DOTNET_NOLOGO"] <- "1"
        for argument in arguments do startInfo.ArgumentList.Add(argument)
        use child = Process.Start(startInfo)
        if isNull child then failwith "Failed to start dotnet."
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()
        child.WaitForExit()
        child.ExitCode, stdout.Result, stderr.Result

    let private verifyCallerExecution
        (fixture: M2ListMapEvaluationFixture)
        (workspace: AgentWorkspace)
        : unit =
        let typed =
            match AgentExecution.projectTypedSemanticIr workspace with
            | Ok value -> value
            | Error error -> failwithf "Typed projection failed: %A" error
        let execution =
            match ExecutionIr.lower typed with
            | Ok value -> value
            | Error error -> failwithf "Execution lowering failed: %A" error
        let artifact =
            match CSharpBackend.emit execution CSharpBackendProfile.empty with
            | Ok value -> value
            | Error error -> failwithf "C# emission failed: %A" error

        Assert.Contains(".Map(", artifact.Source)
        Assert.Contains(
            "NyFunctions." + CSharpBackendNames.functionName fixture.CompatibleMapperFunctionId,
            artifact.Source
        )

        let tempRoot = Path.Combine(Path.GetTempPath(), "nyrwvy-m2-listmap-corpus-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        try
            File.WriteAllText(
                Path.Combine(tempRoot, "Generated.csproj"),
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>disable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>""",
                UTF8Encoding(false)
            )
            File.WriteAllText(Path.Combine(tempRoot, "Generated.cs"), artifact.Source, UTF8Encoding(false))
            let callerName = CSharpBackendNames.functionName fixture.CallerFunctionId
            File.WriteAllText(
                Path.Combine(tempRoot, "Program.cs"),
                $"""using System;
using Nyrwvy.Generated;
Console.WriteLine(NyFunctions.{callerName}() ? "true" : "false");
""",
                UTF8Encoding(false)
            )
            let buildExit, buildOut, buildErr =
                runDotnet
                    tempRoot
                    [ "build"
                      "Generated.csproj"
                      "--configuration"
                      "Release"
                      "--nologo"
                      "--verbosity"
                      "quiet" ]
            if buildExit <> 0 then
                failwithf "Generated ListMap corpus build failed.\n%s\n%s\n%s" buildOut buildErr artifact.Source
            let dll = Path.Combine(tempRoot, "bin", "Release", "net10.0", "Generated.dll")
            let runExit, runOut, runErr = runDotnet tempRoot [ dll ]
            if runExit <> 0 then
                failwithf "Generated ListMap corpus execution failed.\n%s\n%s" runOut runErr
            Assert.Equal("true", runOut.Trim())
        finally
            if Directory.Exists tempRoot then Directory.Delete(tempRoot, true)

    let private hasProperty (name: string) (element: JsonElement): bool =
        match element.TryGetProperty(name) with
        | true, _ -> true
        | false, _ -> false

    [<Fact>]
    let ``ListMap corpus fixtures validate protocol matches and sampling is fixed`` () =
        let fixtures =
            [ M2ListMapStructuralEvaluationCorpus.ready ()
              M2ListMapStructuralEvaluationCorpus.openProtected ()
              M2ListMapStructuralEvaluationCorpus.resolvedProtected () ]

        for fixture in fixtures do
            let issues = SnapshotValidator.validate fixture.Snapshot
            if not (List.isEmpty issues) then
                failwithf "Fixture %s is invalid: %A" fixture.Name issues

        Assert.Equal(ListMapStructuralProtocol.version, M2ListMapStructuralEvaluationCorpus.protocolVersion)

        let taskIds = M2ListMapStructuralEvaluationCorpus.tasks |> List.map (fun task -> task.Id)
        Assert.Equal(10, taskIds.Length)
        Assert.Equal(taskIds.Length, taskIds |> Set.ofList |> Set.count)
        let expectedTaskIds = [ for index in 1 .. 10 -> $"M2-L{index}" ]
        if taskIds <> expectedTaskIds then
            failwithf "Unexpected ListMap corpus task IDs/order: %A" taskIds

    [<Fact>]
    let ``machine ListMap corpus matches deterministic fixture bindings oracle matrix and sampling`` () =
        let path =
            Path.Combine(AppContext.BaseDirectory, "evaluation", "m2-listmap-agent-corpus-v1", "corpus.json")
        if not (File.Exists path) then failwithf "ListMap corpus file was not copied to test output: %s" path

        use document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8))
        let root = document.RootElement
        Assert.Equal(M2ListMapStructuralEvaluationCorpus.corpusId, root.GetProperty("corpusId").GetString())
        Assert.Equal(M2ListMapStructuralEvaluationCorpus.protocolVersion, root.GetProperty("protocolVersion").GetString())

        let sampling = root.GetProperty("samplingPlan")
        Assert.Equal("openai/gpt-5.6-terra", sampling.GetProperty("model").GetString())
        Assert.Equal("high", sampling.GetProperty("reasoning").GetString())
        Assert.Equal(3, sampling.GetProperty("attemptsPerTask").GetInt32())
        Assert.Equal(10, sampling.GetProperty("taskCount").GetInt32())
        Assert.Equal(30, sampling.GetProperty("targetValidAttempts").GetInt32())

        let ready = M2ListMapStructuralEvaluationCorpus.ready ()
        let readyJson =
            root.GetProperty("fixtures").GetProperty(M2ListMapStructuralEvaluationCorpus.readyFixtureName)
        let expectStable (property: string) (expected: string) =
            let actual = readyJson.GetProperty(property).GetString()
            if actual <> expected then
                failwithf "ListMap fixture binding %s mismatch. Expected %s, got %s" property expected actual

        expectStable "snapshotId" (StableId.value ready.Snapshot.Id)
        expectStable "targetFunctionId" (StableId.value ready.TargetFunctionId)
        expectStable "sourceListParameterId" (StableId.value ready.SourceListParameterId)
        expectStable "nonListParameterId" (StableId.value ready.NonListParameterId)
        expectStable "targetResultNodeId" (StableId.value ready.TargetResultNodeId)
        expectStable "compatibleMapperFunctionId" (StableId.value ready.CompatibleMapperFunctionId)
        expectStable "wrongInputMapperFunctionId" (StableId.value ready.WrongInputMapperFunctionId)
        expectStable "wrongResultMapperFunctionId" (StableId.value ready.WrongResultMapperFunctionId)
        expectStable "effectfulMapperFunctionId" (StableId.value ready.EffectfulMapperFunctionId)
        expectStable "missingMapperFunctionId" (StableId.value M2ListMapStructuralEvaluationCorpus.missingMapperFunctionId)
        expectStable "callerFunctionId" (StableId.value ready.CallerFunctionId)
        expectStable "callerCallNodeId" (StableId.value ready.CallerCallNodeId)
        expectStable "foreignFunctionId" (StableId.value ready.ForeignFunctionId)
        expectStable "foreignListParameterId" (StableId.value ready.ForeignListParameterId)
        expectStable "unrelatedFunctionId" (StableId.value ready.UnrelatedFunctionId)
        expectStable "unrelatedResultNodeId" (StableId.value ready.UnrelatedResultNodeId)

        let taskElements = root.GetProperty("tasks").EnumerateArray() |> Seq.toList
        let machineIds = taskElements |> List.map (fun task -> task.GetProperty("id").GetString())
        let codeIds = M2ListMapStructuralEvaluationCorpus.tasks |> List.map (fun task -> task.Id)
        if machineIds <> codeIds then
            failwithf "ListMap machine corpus task IDs differ from code matrix. JSON=%A code=%A" machineIds codeIds

        let machineDiagnostic (task: JsonElement) =
            let expected = task.GetProperty("expected")
            if hasProperty "diagnostic" expected then
                Some(expected.GetProperty("diagnostic").GetString())
            elif hasProperty "firstCommitDiagnostic" expected then
                Some(expected.GetProperty("firstCommitDiagnostic").GetString())
            else
                None

        for taskSpec, taskJson in List.zip M2ListMapStructuralEvaluationCorpus.tasks taskElements do
            Assert.Equal(taskSpec.FixtureName, taskJson.GetProperty("fixture").GetString())
            Assert.Equal(taskSpec.ExpectedDiagnostic, machineDiagnostic taskJson)
            let expectedKind = taskJson.GetProperty("expected").GetProperty("kind").GetString()
            let shouldCommit = expectedKind = "commit" || expectedKind = "staleThenRecover"
            let recovery = expectedKind = "staleThenRecover"
            Assert.Equal(taskSpec.ExpectedCommit, shouldCommit)
            Assert.Equal(taskSpec.RecoveryRequired, recovery)

    [<Fact>]
    let ``M2-L1 success oracle preserves review authority execution and unrelated semantics`` () =
        let fixture = M2ListMapStructuralEvaluationCorpus.ready ()
        let workspace = createWorkspace fixture.Snapshot
        let proposal =
            request fixture workspace fixture.SourceListParameterId fixture.CompatibleMapperFunctionId
            |> simulate workspace
        let review = AgentListMapStructuralChangeProposal.review proposal

        Assert.Equal(TransactionReadiness.SemanticallyReady, review.Readiness)
        Assert.Equal(fixture.SourceListParameterId, review.RequestedChange.SourceParameterId)
        Assert.Equal(fixture.CompatibleMapperFunctionId, review.RequestedChange.MapperFunctionId)
        Assert.Equal(fixture.TargetResultNodeId, review.BaseFocus.Function.Body.ResultNodeId)
        Assert.Equal(review.ResultingChange.NewResultNodeId, review.ProposedFocus.Function.Body.ResultNodeId)

        let roles = review.ResultingChange.AddedNodes |> List.map (fun node -> node.Role)
        if roles <> [ "ParameterReference"; "ListMap" ] then
            failwithf "Unexpected ListMap corpus materialization order: %A" roles

        let sourceNode = review.ResultingChange.AddedNodes[0]
        let mapNode = review.ResultingChange.AddedNodes[1]
        Assert.Equal(Some fixture.SourceListParameterId, sourceNode.ParameterId)
        Assert.Equal(Some fixture.CompatibleMapperFunctionId, mapNode.FunctionId)
        if mapNode.Dependencies <> [ sourceNode.NodeId ] then
            failwithf "Unexpected ListMap corpus dependency: %A" mapNode.Dependencies

        let removedIds = review.ResultingChange.RemovedNodes |> List.map (fun node -> node.NodeId) |> Set.ofList
        let addedIds = review.ResultingChange.AddedNodes |> List.map (fun node -> node.NodeId) |> Set.ofList
        Assert.True(Set.intersect removedIds addedIds |> Set.isEmpty)
        review.ResultingChange.AddedNodes |> List.iter (fun node -> assertUuidV7 node.NodeId)

        let committed, publication = commit workspace proposal
        Assert.Equal(AgentWorkspace.currentSnapshotId committed, publication.PublishedSnapshotId)

        let before = fixture.Snapshot
        let after = AgentWorkspace.snapshotForStructuralEditing committed
        let beforeTarget = findFunction fixture.TargetFunctionId before
        let afterTarget = findFunction fixture.TargetFunctionId after
        Assert.Equal(beforeTarget.Id, afterTarget.Id)
        Assert.Equal(beforeTarget.DisplayName, afterTarget.DisplayName)
        if beforeTarget.Parameters <> afterTarget.Parameters then failwith "ListMap target parameters changed."
        Assert.Equal(beforeTarget.ResultType, afterTarget.ResultType)
        if beforeTarget.RequiredEffects <> afterTarget.RequiredEffects then failwith "ListMap target effects changed."

        for id in [ fixture.CallerFunctionId; fixture.ForeignFunctionId; fixture.UnrelatedFunctionId ] do
            if findFunction id before <> findFunction id after then
                failwithf "Unrelated ListMap fixture function %s changed." (StableId.value id)

        if before.Types <> after.Types
           || before.Effects <> after.Effects
           || before.Capabilities <> after.Capabilities
           || before.Obligations <> after.Obligations then
            failwith "ListMap corpus changed unrelated declaration/obligation collections."

        verifyCallerExecution fixture committed

    [<Fact>]
    let ``M2-L2 through L7 reject exact ListMap source mapper type and effect violations`` () =
        let fixture = M2ListMapStructuralEvaluationCorpus.ready ()
        let workspace = createWorkspace fixture.Snapshot

        let cases: (ParameterId * FunctionId * string) list =
            [ (fixture.ForeignListParameterId,
               fixture.CompatibleMapperFunctionId,
               "StructuralListSourceParameterOutOfScope")
              (fixture.NonListParameterId,
               fixture.CompatibleMapperFunctionId,
               "StructuralListSourceTypeMismatch")
              (fixture.SourceListParameterId,
               M2ListMapStructuralEvaluationCorpus.missingMapperFunctionId,
               "StructuralListMapperNotFound")
              (fixture.SourceListParameterId,
               fixture.WrongInputMapperFunctionId,
               "StructuralListMapperInputTypeMismatch")
              (fixture.SourceListParameterId,
               fixture.WrongResultMapperFunctionId,
               "StructuralListMapperResultTypeMismatch")
              (fixture.SourceListParameterId,
               fixture.EffectfulMapperFunctionId,
               "StructuralListMapperEffectsNotAllowed") ]

        for sourceId, mapperId, diagnostic in cases do
            request fixture workspace sourceId mapperId
            |> AgentListMapStructuralWorkflow.simulate workspace
            |> assertSimulationRejected diagnostic

        Assert.Equal(fixture.Snapshot.Id, AgentWorkspace.currentSnapshotId workspace)

    [<Fact>]
    let ``M2-L8 and L9 reject open and resolved obligation protected bodies`` () =
        for fixture in
            [ M2ListMapStructuralEvaluationCorpus.openProtected ()
              M2ListMapStructuralEvaluationCorpus.resolvedProtected () ] do
            let workspace = createWorkspace fixture.Snapshot
            request fixture workspace fixture.SourceListParameterId fixture.CompatibleMapperFunctionId
            |> AgentListMapStructuralWorkflow.simulate workspace
            |> assertSimulationRejected "StructuralBodyReferencedByObligation"
            Assert.Equal(fixture.Snapshot.Id, AgentWorkspace.currentSnapshotId workspace)

    [<Fact>]
    let ``M2-L10 stale target proposal recovers after harness rename and preserves advancement`` () =
        let fixture = M2ListMapStructuralEvaluationCorpus.ready ()
        let workspace = createWorkspace fixture.Snapshot
        let targetProposal =
            request fixture workspace fixture.SourceListParameterId fixture.CompatibleMapperFunctionId
            |> simulate workspace

        let rename =
            AgentWorkflow.simulate
                workspace
                [ AgentOperationIntent.RenameDisplayName(
                    AgentRenameTarget.Function fixture.UnrelatedFunctionId,
                    "unrelated_marker_harness_advanced"
                  ) ]
            |> function
                | Ok proposal -> proposal
                | Error error -> failwithf "Harness rename simulation failed: %A" error

        let advanced =
            match AgentWorkflow.commit workspace rename with
            | AgentCommitResult.Committed(nextWorkspace, _) -> nextWorkspace
            | AgentCommitResult.Rejected diagnostics -> failwithf "Harness rename commit failed: %A" diagnostics

        match AgentListMapStructuralWorkflow.commit advanced targetProposal with
        | AgentStructuralCommitResult.Rejected [ diagnostic ] when diagnostic.Code = "StaleBaseSnapshot" -> ()
        | other -> failwithf "Expected stale ListMap target proposal, got %A" other

        let advancedSnapshot = AgentWorkspace.snapshotForStructuralEditing advanced
        let advancedUnrelated = findFunction fixture.UnrelatedFunctionId advancedSnapshot
        Assert.Equal("unrelated_marker_harness_advanced", advancedUnrelated.DisplayName)

        let freshProposal =
            request fixture advanced fixture.SourceListParameterId fixture.CompatibleMapperFunctionId
            |> simulate advanced
        let committed, _ = commit advanced freshProposal
        let after = AgentWorkspace.snapshotForStructuralEditing committed
        let afterUnrelated = findFunction fixture.UnrelatedFunctionId after
        Assert.Equal("unrelated_marker_harness_advanced", afterUnrelated.DisplayName)

        for fn in advancedSnapshot.Functions do
            if fn.Id <> fixture.TargetFunctionId then
                let afterFn = findFunction fn.Id after
                if fn <> afterFn then
                    failwithf "M2-L10 changed unrelated function %s after harness advancement." (StableId.value fn.Id)

        if advancedSnapshot.Types <> after.Types
           || advancedSnapshot.Effects <> after.Effects
           || advancedSnapshot.Capabilities <> after.Capabilities
           || advancedSnapshot.Obligations <> after.Obligations then
            failwith "M2-L10 changed unrelated collections after harness advancement."

        verifyCallerExecution fixture committed
