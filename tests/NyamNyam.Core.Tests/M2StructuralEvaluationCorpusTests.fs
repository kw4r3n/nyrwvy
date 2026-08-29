namespace NyamNyam.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open NyamNyam.Core
open NyamNyam.AgentEval.Structural
open Xunit

module M2StructuralEvaluationCorpusTests =
    let private createWorkspace (snapshot: Snapshot): AgentWorkspace =
        match AgentWorkspace.create snapshot with
        | Ok workspace -> workspace
        | Error issues -> failwithf "Unexpected workspace creation failure: %A" issues

    let private request
        (fixture: M2StructuralEvaluationFixture)
        (baseSnapshotId: SnapshotId)
        (resultNodeId: GraphNodeId)
        (recipe: Float64ExpressionRecipe)
        : AgentReplaceFunctionBodyWithFloat64Recipe =
        { TargetFunctionId = fixture.TargetFunctionId
          ExpectedBaseSnapshotId = baseSnapshotId
          ExpectedCurrentResultNodeId = resultNodeId
          Replacement = recipe }

    let private successRecipe (fixture: M2StructuralEvaluationFixture): Float64ExpressionRecipe =
        Float64ExpressionRecipe.Add(
            Float64ExpressionRecipe.Parameter fixture.TargetFloatParameterId,
            Float64ExpressionRecipe.Literal(Float64Bits.create 0x4002000000000000UL)
        )

    let private simulate
        (workspace: AgentWorkspace)
        (structuralRequest: AgentReplaceFunctionBodyWithFloat64Recipe)
        : AgentStructuralChangeProposal =
        match AgentStructuralWorkflow.simulate workspace structuralRequest with
        | Ok proposal -> proposal
        | Error error -> failwithf "Unexpected structural simulation failure: %A" error

    let private commit
        (workspace: AgentWorkspace)
        (proposal: AgentStructuralChangeProposal)
        : AgentWorkspace * PublishedStructuralTransaction =
        match AgentStructuralWorkflow.commit workspace proposal with
        | AgentStructuralCommitResult.Committed(nextWorkspace, publication) -> nextWorkspace, publication
        | AgentStructuralCommitResult.Rejected diagnostics ->
            failwithf "Unexpected structural commit rejection: %A" diagnostics

    let private assertSimulationRejected
        (expected: string)
        (result: Result<AgentStructuralChangeProposal, AgentStructuralWorkflowError>) =
        match result with
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) when diagnostic.Code = expected -> ()
        | other -> failwithf "Expected simulation diagnostic %s, got %A" expected other

    let rec private deepRecipe (depth: int): Float64ExpressionRecipe =
        if depth <= 1 then
            Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 1.0)
        else
            Float64ExpressionRecipe.Add(
                Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 1.0),
                deepRecipe (depth - 1)
            )

    let rec private balancedLeaves (leaves: int): Float64ExpressionRecipe =
        if leaves <= 1 then
            Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 1.0)
        else
            let leftLeaves = leaves / 2
            let rightLeaves = leaves - leftLeaves
            Float64ExpressionRecipe.Add(balancedLeaves leftLeaves, balancedLeaves rightLeaves)

    let private runDotnet (workingDirectory: string) (arguments: string list) =
        let startInfo = ProcessStartInfo("dotnet")
        startInfo.WorkingDirectory <- workingDirectory
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] <- "1"
        startInfo.Environment["DOTNET_NOLOGO"] <- "1"
        for argument in arguments do
            startInfo.ArgumentList.Add(argument)

        use child = Process.Start(startInfo)
        if isNull child then failwith "Failed to start dotnet."
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()
        child.WaitForExit()
        child.ExitCode, stdout.Result, stderr.Result

    let private hasProperty (name: string) (element: JsonElement): bool =
        match element.TryGetProperty(name) with
        | true, _ -> true
        | false, _ -> false

    [<Fact>]
    let ``M2 structural corpus fixtures validate and protocol condition matches runner`` () =
        let fixtures =
            [ M2StructuralEvaluationCorpus.ready ()
              M2StructuralEvaluationCorpus.openProtected ()
              M2StructuralEvaluationCorpus.resolvedProtected () ]

        for fixture in fixtures do
            let issues = SnapshotValidator.validate fixture.Snapshot
            if not (List.isEmpty issues) then
                failwithf "Fixture %s is invalid: %A" fixture.Name issues

        if M2StructuralEvaluationCorpus.protocolVersion <> StructuralProtocol.version then
            failwithf
                "Corpus protocol %s does not match runner %s."
                M2StructuralEvaluationCorpus.protocolVersion
                StructuralProtocol.version

        let taskIds = M2StructuralEvaluationCorpus.tasks |> List.map (fun task -> task.Id)
        let distinct = taskIds |> Set.ofList
        Assert.Equal(10, taskIds.Length)
        Assert.Equal(taskIds.Length, distinct.Count)

    [<Fact>]
    let ``machine-readable corpus matches deterministic fixture bindings and oracle matrix`` () =
        let path =
            Path.Combine(AppContext.BaseDirectory, "evaluation", "m2-agent-corpus-v1", "corpus.json")
        if not (File.Exists path) then failwithf "Corpus file was not copied to test output: %s" path

        use document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8))
        let root = document.RootElement
        let corpusId = root.GetProperty("corpusId").GetString()
        let protocol = root.GetProperty("protocolVersion").GetString()
        if corpusId <> M2StructuralEvaluationCorpus.corpusId then
            failwithf "Corpus ID mismatch: %s" corpusId
        if protocol <> M2StructuralEvaluationCorpus.protocolVersion then
            failwithf "Protocol mismatch: %s" protocol

        let ready = M2StructuralEvaluationCorpus.ready ()
        let readyJson = root.GetProperty("fixtures").GetProperty(M2StructuralEvaluationCorpus.readyFixtureName)
        let expectStable (property: string) (expected: string) =
            let actual = readyJson.GetProperty(property).GetString()
            if actual <> expected then
                failwithf "Fixture binding %s mismatch. Expected %s, got %s" property expected actual

        expectStable "snapshotId" (StableId.value ready.Snapshot.Id)
        expectStable "targetFunctionId" (StableId.value ready.TargetFunctionId)
        expectStable "targetFloatParameterId" (StableId.value ready.TargetFloatParameterId)
        expectStable "targetWrongTypeParameterId" (StableId.value ready.TargetWrongTypeParameterId)
        expectStable "targetResultNodeId" (StableId.value ready.TargetResultNodeId)
        expectStable "callerFunctionId" (StableId.value ready.CallerFunctionId)
        expectStable "callerCallNodeId" (StableId.value ready.CallerCallNodeId)
        expectStable "foreignFunctionId" (StableId.value ready.ForeignFunctionId)
        expectStable "foreignParameterId" (StableId.value ready.ForeignParameterId)
        expectStable "unrelatedFunctionId" (StableId.value ready.UnrelatedFunctionId)
        expectStable "unrelatedResultNodeId" (StableId.value ready.UnrelatedResultNodeId)

        let taskElements = root.GetProperty("tasks").EnumerateArray() |> Seq.toList
        let machineIds = taskElements |> List.map (fun task -> task.GetProperty("id").GetString())
        let codeIds = M2StructuralEvaluationCorpus.tasks |> List.map (fun task -> task.Id)
        if machineIds <> codeIds then
            failwithf "Machine corpus task order/IDs differ from code oracle matrix. JSON=%A code=%A" machineIds codeIds

        let machineDiagnostic (task: JsonElement) =
            if hasProperty "firstExpectedDiagnostic" task then
                Some(task.GetProperty("firstExpectedDiagnostic").GetString())
            else
                let expected = task.GetProperty("expected")
                if hasProperty "diagnostic" expected then
                    Some(expected.GetProperty("diagnostic").GetString())
                elif hasProperty "firstCommitDiagnostic" expected then
                    Some(expected.GetProperty("firstCommitDiagnostic").GetString())
                else
                    None

        for taskSpec, taskJson in List.zip M2StructuralEvaluationCorpus.tasks taskElements do
            let fixtureName = taskJson.GetProperty("fixture").GetString()
            if fixtureName <> taskSpec.FixtureName then
                failwithf "%s fixture mismatch: %s" taskSpec.Id fixtureName
            if machineDiagnostic taskJson <> taskSpec.ExpectedDiagnostic then
                failwithf "%s diagnostic mismatch." taskSpec.Id

            let expectedKind = taskJson.GetProperty("expected").GetProperty("kind").GetString()
            let shouldCommit = expectedKind = "commit" || expectedKind = "recoverThenCommit" || expectedKind = "staleThenRecover"
            let recovery = expectedKind = "recoverThenCommit" || expectedKind = "staleThenRecover"
            Assert.Equal(taskSpec.ExpectedCommit, shouldCommit)
            Assert.Equal(taskSpec.RecoveryRequired, recovery)

    [<Fact>]
    let ``M2-E1 success oracle preserves review identities execution and unrelated semantics`` () =
        let fixture = M2StructuralEvaluationCorpus.ready ()
        let workspace = createWorkspace fixture.Snapshot
        let proposal =
            request fixture fixture.Snapshot.Id fixture.TargetResultNodeId (successRecipe fixture)
            |> simulate workspace
        let review = AgentStructuralChangeProposal.review proposal

        Assert.Equal(TransactionReadiness.SemanticallyReady, review.Readiness)
        Assert.Equal(fixture.TargetResultNodeId, review.BaseFocus.Function.Body.ResultNodeId)
        Assert.Equal(review.ResultingChange.NewResultNodeId, review.ProposedFocus.Function.Body.ResultNodeId)

        let roles = review.ResultingChange.AddedNodes |> List.map (fun node -> node.Role)
        if roles <> [ "ParameterReference"; "Float64Literal"; "Float64Add" ] then
            failwithf "Unexpected materialization order: %A" roles
        Assert.Equal(Some "4002000000000000", review.ResultingChange.AddedNodes[1].Float64BitsHex)

        let removedIds = review.ResultingChange.RemovedNodes |> List.map (fun node -> node.NodeId) |> Set.ofList
        let addedIds = review.ResultingChange.AddedNodes |> List.map (fun node -> node.NodeId) |> Set.ofList
        Assert.True(Set.intersect removedIds addedIds |> Set.isEmpty)

        let committed, publication = commit workspace proposal
        Assert.Equal(AgentWorkspace.currentSnapshotId committed, publication.PublishedSnapshotId)

        let findFunction (id: FunctionId) (snapshot: Snapshot) =
            snapshot.Functions |> List.find (fun fn -> fn.Id = id)
        let after = AgentWorkspace.snapshotForStructuralEditing committed
        let beforeTarget = findFunction fixture.TargetFunctionId fixture.Snapshot
        let afterTarget = findFunction fixture.TargetFunctionId after
        Assert.Equal(beforeTarget.Id, afterTarget.Id)
        if beforeTarget.Parameters <> afterTarget.Parameters then failwith "Target parameters changed."
        Assert.Equal(beforeTarget.ResultType, afterTarget.ResultType)
        if beforeTarget.RequiredEffects <> afterTarget.RequiredEffects then failwith "Target effects changed."

        for id in [ fixture.CallerFunctionId; fixture.ForeignFunctionId; fixture.UnrelatedFunctionId ] do
            if findFunction id fixture.Snapshot <> findFunction id after then
                failwithf "Unrelated function %s changed." (StableId.value id)

        if fixture.Snapshot.Types <> after.Types
           || fixture.Snapshot.Effects <> after.Effects
           || fixture.Snapshot.Capabilities <> after.Capabilities
           || fixture.Snapshot.Obligations <> after.Obligations then
            failwith "Unrelated declaration or obligation collections changed."

        let typed =
            match AgentExecution.projectTypedSemanticIr committed with
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

        let tempRoot = Path.Combine(Path.GetTempPath(), "nyamnyam-m2-corpus-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        try
            File.WriteAllText(
                Path.Combine(tempRoot, "Generated.csproj"),
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>disable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>""",
                UTF8Encoding(false)
            )
            File.WriteAllText(Path.Combine(tempRoot, "Generated.cs"), artifact.Source, UTF8Encoding(false))
            let callerName = CSharpBackendNames.functionName fixture.CallerFunctionId
            let harness =
                $"""using System;
using System.Globalization;
using NyamNyam.Generated;
Console.WriteLine(NyFunctions.{callerName}().ToString("R", CultureInfo.InvariantCulture));
"""
            File.WriteAllText(Path.Combine(tempRoot, "Program.cs"), harness, UTF8Encoding(false))

            let buildExit, buildOut, buildErr =
                runDotnet tempRoot [ "build"; "Generated.csproj"; "--configuration"; "Release"; "--nologo"; "--verbosity"; "quiet" ]
            if buildExit <> 0 then failwithf "Generated build failed.\n%s\n%s\n%s" buildOut buildErr artifact.Source

            let dll = Path.Combine(tempRoot, "bin", "Release", "net10.0", "Generated.dll")
            let runExit, runOut, runErr = runDotnet tempRoot [ dll ]
            if runExit <> 0 then failwithf "Generated execution failed.\n%s\n%s" runOut runErr
            Assert.Equal("3.75", runOut.Trim())
        finally
            if Directory.Exists tempRoot then Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``M2-E2 through E5 guard and parameter tasks reject for intended reasons and recovery succeeds`` () =
        let fixture = M2StructuralEvaluationCorpus.ready ()
        let workspace = createWorkspace fixture.Snapshot
        let literal = Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 5.0)

        request fixture M2StructuralEvaluationCorpus.wrongBaseSnapshotId fixture.TargetResultNodeId literal
        |> AgentStructuralWorkflow.simulate workspace
        |> assertSimulationRejected "StructuralExpectedBaseMismatch"

        let baseRecovery = request fixture fixture.Snapshot.Id fixture.TargetResultNodeId literal |> simulate workspace
        let _, basePublication = commit workspace baseRecovery
        Assert.Equal(fixture.Snapshot.Id, basePublication.BaseSnapshotId)

        request fixture fixture.Snapshot.Id M2StructuralEvaluationCorpus.wrongResultNodeId literal
        |> AgentStructuralWorkflow.simulate workspace
        |> assertSimulationRejected "StructuralExpectedResultMismatch"

        let resultRecovery = request fixture fixture.Snapshot.Id fixture.TargetResultNodeId literal |> simulate workspace
        let _, resultPublication = commit workspace resultRecovery
        Assert.Equal(fixture.Snapshot.Id, resultPublication.BaseSnapshotId)

        request fixture fixture.Snapshot.Id fixture.TargetResultNodeId (Float64ExpressionRecipe.Parameter fixture.ForeignParameterId)
        |> AgentStructuralWorkflow.simulate workspace
        |> assertSimulationRejected "StructuralParameterOutOfScope"

        request fixture fixture.Snapshot.Id fixture.TargetResultNodeId (Float64ExpressionRecipe.Parameter fixture.TargetWrongTypeParameterId)
        |> AgentStructuralWorkflow.simulate workspace
        |> assertSimulationRejected "StructuralParameterTypeMismatch"

    [<Fact>]
    let ``M2-E6 and E7 recipe limit tasks reject for intended reasons`` () =
        let fixture = M2StructuralEvaluationCorpus.ready ()
        let workspace = createWorkspace fixture.Snapshot

        request fixture fixture.Snapshot.Id fixture.TargetResultNodeId (deepRecipe 33)
        |> AgentStructuralWorkflow.simulate workspace
        |> assertSimulationRejected "StructuralRecipeDepthLimitExceeded"

        request fixture fixture.Snapshot.Id fixture.TargetResultNodeId (balancedLeaves 33)
        |> AgentStructuralWorkflow.simulate workspace
        |> assertSimulationRejected "StructuralRecipeNodeLimitExceeded"

    [<Fact>]
    let ``M2-E8 and E9 protected body tasks reject open and resolved obligations`` () =
        for fixture in [ M2StructuralEvaluationCorpus.openProtected (); M2StructuralEvaluationCorpus.resolvedProtected () ] do
            let workspace = createWorkspace fixture.Snapshot
            request
                fixture
                fixture.Snapshot.Id
                fixture.TargetResultNodeId
                (Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 5.0))
            |> AgentStructuralWorkflow.simulate workspace
            |> assertSimulationRejected "StructuralBodyReferencedByObligation"

    [<Fact>]
    let ``M2-E10 stale reviewed proposal fails closed and fresh recovery preserves harness advance`` () =
        let fixture = M2StructuralEvaluationCorpus.ready ()
        let workspace = createWorkspace fixture.Snapshot
        let targetProposal =
            request fixture fixture.Snapshot.Id fixture.TargetResultNodeId (successRecipe fixture)
            |> simulate workspace

        let harnessRequest: AgentReplaceFunctionBodyWithFloat64Recipe =
            { TargetFunctionId = fixture.UnrelatedFunctionId
              ExpectedBaseSnapshotId = fixture.Snapshot.Id
              ExpectedCurrentResultNodeId = fixture.UnrelatedResultNodeId
              Replacement = Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 100.0) }
        let harnessProposal = simulate workspace harnessRequest
        let advanced, harnessPublication = commit workspace harnessProposal

        match AgentStructuralWorkflow.commit advanced targetProposal with
        | AgentStructuralCommitResult.Rejected [ diagnostic ] when diagnostic.Code = "StaleBaseSnapshot" -> ()
        | result -> failwithf "Expected stale reviewed proposal rejection, got %A" result

        let freshRequest =
            request
                fixture
                (AgentWorkspace.currentSnapshotId advanced)
                fixture.TargetResultNodeId
                (successRecipe fixture)
        let freshProposal = simulate advanced freshRequest
        let recovered, _ = commit advanced freshProposal

        let unrelated =
            match AgentWorkspace.getFunction recovered fixture.UnrelatedFunctionId with
            | Ok value -> value
            | Error error -> failwithf "Unrelated harness target missing: %A" error
        if unrelated.Body.ResultNodeId = fixture.UnrelatedResultNodeId then
            failwith "Harness-owned unrelated replacement was lost during recovery."
        Assert.Equal(harnessPublication.PublishedSnapshotId, freshRequest.ExpectedBaseSnapshotId)
