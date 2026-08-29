namespace NyamNyam.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text
open NyamNyam.Core
open Xunit

module AgentStructuralWorkflowTests =
    type private Fixture =
        { Snapshot: Snapshot
          TargetFunctionId: FunctionId
          TargetParameterId: ParameterId
          OldResultNodeId: GraphNodeId
          UnrelatedFunctionId: FunctionId
          UnrelatedResultNodeId: GraphNodeId }

    let private functionId name = StableId.createForTesting<FunctionTag> ("function-structural-" + name)
    let private parameterId name = StableId.createForTesting<ParameterTag> ("parameter-structural-" + name)
    let private nodeId name = StableId.createForTesting<GraphNodeTag> ("node-structural-" + name)
    let private snapshotId name = StableId.createForTesting<SnapshotTag> ("snapshot-structural-" + name)
    let private float64Type = TypeRef.Primitive PrimitiveType.Float64

    let private createFixture () =
        let parameter =
            { Id = parameterId "value"
              DisplayName = "value"
              Type = float64Type }
        let oldResultId = nodeId "old-result"
        let target =
            { Id = functionId "target"
              DisplayName = "target"
              Parameters = [ parameter ]
              ResultType = float64Type
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = oldResultId
                        Kind = SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 1.0) } ]
                  ResultNodeId = oldResultId } }
        let unrelatedResultId = nodeId "unrelated-result"
        let unrelated =
            { Id = functionId "unrelated"
              DisplayName = "unrelated"
              Parameters = []
              ResultType = float64Type
              RequiredEffects = []
              Body =
                { Nodes =
                    [ { Id = unrelatedResultId
                        Kind = SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 99.0) } ]
                  ResultNodeId = unrelatedResultId } }
        { Snapshot =
            { Id = snapshotId "initial"
              ParentId = None
              Types = []
              Effects = []
              Capabilities = []
              Functions = [ target; unrelated ]
              Obligations = [] }
          TargetFunctionId = target.Id
          TargetParameterId = parameter.Id
          OldResultNodeId = oldResultId
          UnrelatedFunctionId = unrelated.Id
          UnrelatedResultNodeId = unrelatedResultId }

    let private createWorkspace snapshot =
        match AgentWorkspace.create snapshot with
        | Ok workspace -> workspace
        | Error issues -> failwithf "Unexpected workspace creation failure: %A" issues

    let private request fixture recipe =
        { TargetFunctionId = fixture.TargetFunctionId
          ExpectedBaseSnapshotId = fixture.Snapshot.Id
          ExpectedCurrentResultNodeId = fixture.OldResultNodeId
          Replacement = recipe }

    let private simulate workspace structuralRequest =
        match AgentStructuralWorkflow.simulate workspace structuralRequest with
        | Ok proposal -> proposal
        | Error error -> failwithf "Unexpected structural simulation failure: %A" error

    let private assertUuidV7 id =
        let payload = StableId.value id
        let mutable parsed = Guid.Empty
        Assert.True(Guid.TryParseExact(payload, "D", &parsed), $"Not a GUID: {payload}")
        Assert.Equal('7', payload[14])

    [<Fact>]
    let ``structural simulation materializes a fresh reviewed Float64 body`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot
        let recipe =
            Float64ExpressionRecipe.Add(
                Float64ExpressionRecipe.Parameter fixture.TargetParameterId,
                Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 2.25)
            )
        let proposal = simulate workspace (request fixture recipe)
        let review = AgentStructuralChangeProposal.review proposal

        Assert.Equal(TransactionReadiness.SemanticallyReady, review.Readiness)
        Assert.Equal(fixture.TargetFunctionId, review.ResultingChange.OwnerFunctionId)
        Assert.Equal(fixture.OldResultNodeId, review.ResultingChange.OldResultNodeId)
        Assert.Equal(fixture.OldResultNodeId, review.BaseFocus.Function.Body.ResultNodeId)
        Assert.Equal(review.ResultingChange.NewResultNodeId, review.ProposedFocus.Function.Body.ResultNodeId)
        Assert.Equal(1, review.ResultingChange.RemovedNodes.Length)
        Assert.Equal(3, review.ResultingChange.AddedNodes.Length)
        Assert.Equal(
            [ "ParameterReference"; "Float64Literal"; "Float64Add" ],
            review.ResultingChange.AddedNodes |> List.map (fun node -> node.Role)
        )
        Assert.Equal(Some "4002000000000000", review.ResultingChange.AddedNodes[1].Float64BitsHex)

        let oldIds = review.ResultingChange.RemovedNodes |> List.map (fun node -> node.NodeId) |> Set.ofList
        let newIds = review.ResultingChange.AddedNodes |> List.map (fun node -> node.NodeId) |> Set.ofList
        Assert.True(Set.intersect oldIds newIds |> Set.isEmpty)
        review.ResultingChange.AddedNodes |> List.iter (fun node -> assertUuidV7 node.NodeId)
        assertUuidV7 (AgentStructuralChangeProposal.transactionId proposal)

    [<Fact>]
    let ``repeated parameter recipe occurrences receive distinct node identities`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot
        let proposal =
            request fixture
                (Float64ExpressionRecipe.Add(
                    Float64ExpressionRecipe.Parameter fixture.TargetParameterId,
                    Float64ExpressionRecipe.Parameter fixture.TargetParameterId
                ))
            |> simulate workspace
        let added = (AgentStructuralChangeProposal.review proposal).ResultingChange.AddedNodes
        Assert.Equal(3, added.Length)
        Assert.NotEqual(added[0].NodeId, added[1].NodeId)
        Assert.Equal(Some fixture.TargetParameterId, added[0].ParameterId)
        Assert.Equal(Some fixture.TargetParameterId, added[1].ParameterId)

    [<Fact>]
    let ``structural commit preserves owner parameter and unrelated identities`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot
        let proposal =
            request fixture
                (Float64ExpressionRecipe.Add(
                    Float64ExpressionRecipe.Parameter fixture.TargetParameterId,
                    Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 2.0)
                ))
            |> simulate workspace

        match AgentStructuralWorkflow.commit workspace proposal with
        | AgentStructuralCommitResult.Rejected diagnostics ->
            failwithf "Reviewed structural proposal was rejected: %A" diagnostics
        | AgentStructuralCommitResult.Committed(nextWorkspace, publication) ->
            Assert.Equal(AgentStructuralChangeProposal.proposedSnapshotId proposal, AgentWorkspace.currentSnapshotId nextWorkspace)
            Assert.Equal(publication.Receipt.ProposedSnapshotId, publication.PublishedSnapshotId)

            match AgentWorkspace.getFunction nextWorkspace fixture.TargetFunctionId with
            | Error error -> failwithf "Target disappeared: %A" error
            | Ok target ->
                Assert.Equal(fixture.TargetFunctionId, target.Id)
                Assert.Equal(1, target.Parameters.Length)
                Assert.Equal(fixture.TargetParameterId, target.Parameters.Head.Id)
                Assert.Equal(3, target.Body.Nodes.Length)
                Assert.False(target.Body.Nodes |> List.exists (fun node -> node.Id = fixture.OldResultNodeId))

            match AgentWorkspace.getFunction nextWorkspace fixture.UnrelatedFunctionId with
            | Error error -> failwithf "Unrelated function disappeared: %A" error
            | Ok unrelated -> Assert.Equal(fixture.UnrelatedResultNodeId, unrelated.Body.ResultNodeId)

    [<Fact>]
    let ``structural proposal rejects stale publication after ordinary workspace advance`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot
        let structural =
            request fixture (Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 3.0))
            |> simulate workspace

        let rename =
            AgentWorkflow.simulate
                workspace
                [ AgentOperationIntent.RenameDisplayName(
                    AgentRenameTarget.Function fixture.UnrelatedFunctionId,
                    "unrelated_advanced"
                  ) ]
            |> function
                | Ok proposal -> proposal
                | Error error -> failwithf "Advance simulation failed: %A" error

        let advanced =
            match AgentWorkflow.commit workspace rename with
            | AgentCommitResult.Committed(next, _) -> next
            | AgentCommitResult.Rejected diagnostics -> failwithf "Advance commit failed: %A" diagnostics

        match AgentStructuralWorkflow.commit advanced structural with
        | AgentStructuralCommitResult.Rejected [ diagnostic ] when diagnostic.Code = "StaleBaseSnapshot" -> ()
        | result -> failwithf "Expected stale structural rejection, got %A" result

    [<Fact>]
    let ``structural preflight rejects base result and foreign-parameter mismatches`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot
        let foreign = StableId.createForTesting<ParameterTag> "parameter-structural-foreign"

        let assertCode expected result =
            match result with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ]) when diagnostic.Code = expected -> ()
            | other -> failwithf "Expected %s, got %A" expected other

        { request fixture (Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 1.0)) with
            ExpectedBaseSnapshotId = snapshotId "wrong-base" }
        |> AgentStructuralWorkflow.simulate workspace
        |> assertCode "StructuralExpectedBaseMismatch"

        { request fixture (Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 1.0)) with
            ExpectedCurrentResultNodeId = nodeId "wrong-result" }
        |> AgentStructuralWorkflow.simulate workspace
        |> assertCode "StructuralExpectedResultMismatch"

        request fixture (Float64ExpressionRecipe.Parameter foreign)
        |> AgentStructuralWorkflow.simulate workspace
        |> assertCode "StructuralParameterOutOfScope"

    let rec private deepRecipe depth =
        if depth <= 1 then
            Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 1.0)
        else
            Float64ExpressionRecipe.Add(
                Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 1.0),
                deepRecipe (depth - 1)
            )

    let rec private balancedRecipe depth =
        if depth <= 1 then
            Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 1.0)
        else
            let child = balancedRecipe (depth - 1)
            Float64ExpressionRecipe.Add(child, child)

    [<Fact>]
    let ``structural recipe bounds have distinct diagnostics`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot

        match AgentStructuralWorkflow.simulate workspace (request fixture (deepRecipe 33)) with
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ])
            when diagnostic.Code = "StructuralRecipeDepthLimitExceeded" -> ()
        | result -> failwithf "Expected depth bound rejection, got %A" result

        match AgentStructuralWorkflow.simulate workspace (request fixture (balancedRecipe 7)) with
        | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ])
            when diagnostic.Code = "StructuralRecipeNodeLimitExceeded" -> ()
        | result -> failwithf "Expected node-count bound rejection, got %A" result

    let private obligationSnapshot (resolved: bool) =
        let fixture = createFixture ()
        let calleeParameter =
            { Id = parameterId "callee-value"
              DisplayName = "value"
              Type = float64Type }
        let calleeResultId = nodeId "callee-result"
        let callee =
            { Id = functionId "callee"
              DisplayName = "callee"
              Parameters = [ calleeParameter ]
              ResultType = float64Type
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = calleeResultId; Kind = SemanticNodeKind.ParameterReference calleeParameter.Id } ]
                  ResultNodeId = calleeResultId } }
        let callId = fixture.OldResultNodeId
        let valueId = nodeId "obligation-value"
        let arguments =
            if resolved then
                [ { TargetParameterId = calleeParameter.Id
                    ValueNodeId = valueId } ]
            else
                []
        let bodyNodes =
            (if resolved then [ { Id = valueId; Kind = SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 4.0) } ] else [])
            @ [ { Id = callId; Kind = SemanticNodeKind.DirectCall(callee.Id, arguments) } ]
        let target =
            fixture.Snapshot.Functions
            |> List.find (fun fn -> fn.Id = fixture.TargetFunctionId)
            |> fun fn -> { fn with Body = { Nodes = bodyNodes; ResultNodeId = callId } }
        let origin = StableId.createForTesting<TransactionTag> "transaction-structural-obligation"
        let status =
            if resolved then
                ObligationStatus.Resolved { TransactionId = origin; ValueNodeId = valueId }
            else
                ObligationStatus.Open
        let obligation =
            { Id = StableId.createForTesting<ObligationTag> (if resolved then "obligation-structural-resolved" else "obligation-structural-open")
              OriginTransactionId = origin
              CallNodeId = callId
              ParameterId = calleeParameter.Id
              Status = status }
        let snapshot =
            { fixture.Snapshot with
                Functions = target :: callee :: (fixture.Snapshot.Functions |> List.filter (fun fn -> fn.Id <> target.Id))
                Obligations = [ obligation ] }
        fixture, target, snapshot

    [<Fact>]
    let ``structural preflight rejects open and resolved obligations targeting the old body`` () =
        for resolved in [ false; true ] do
            let _, target, snapshot = obligationSnapshot resolved
            Assert.Empty(SnapshotValidator.validate snapshot)
            let workspace = createWorkspace snapshot
            let guardedRequest =
                { TargetFunctionId = target.Id
                  ExpectedBaseSnapshotId = snapshot.Id
                  ExpectedCurrentResultNodeId = target.Body.ResultNodeId
                  Replacement = Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 5.0) }

            match AgentStructuralWorkflow.simulate workspace guardedRequest with
            | Error(AgentStructuralWorkflowError.SimulationRejected [ diagnostic ])
                when diagnostic.Code = "StructuralBodyReferencedByObligation" -> ()
            | result -> failwithf "Expected durable obligation rejection, got %A" result

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

    [<Fact>]
    let ``committed structural body lowers emits builds and executes`` () =
        let fixture = createFixture ()
        let workspace = createWorkspace fixture.Snapshot
        let proposal =
            request fixture
                (Float64ExpressionRecipe.Add(
                    Float64ExpressionRecipe.Parameter fixture.TargetParameterId,
                    Float64ExpressionRecipe.Literal(Float64Bits.ofFloat 2.25)
                ))
            |> simulate workspace
        let committed =
            match AgentStructuralWorkflow.commit workspace proposal with
            | AgentStructuralCommitResult.Committed(next, _) -> next
            | AgentStructuralCommitResult.Rejected diagnostics -> failwithf "Commit failed: %A" diagnostics

        let typed =
            AgentExecution.projectTypedSemanticIr committed
            |> function
                | Ok value -> value
                | Error error -> failwithf "Typed projection failed: %A" error
        Assert.Equal(AgentWorkspace.currentSnapshotId committed, typed.SourceSnapshotId)
        let execution =
            ExecutionIr.lower typed
            |> function
                | Ok value -> value
                | Error error -> failwithf "Execution lowering failed: %A" error
        let artifact =
            CSharpBackend.emit execution CSharpBackendProfile.empty
            |> function
                | Ok value -> value
                | Error error -> failwithf "C# emission failed: %A" error

        let tempRoot = Path.Combine(Path.GetTempPath(), "nyamnyam-structural-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        try
            File.WriteAllText(
                Path.Combine(tempRoot, "Generated.csproj"),
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>disable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>""",
                UTF8Encoding(false)
            )
            File.WriteAllText(Path.Combine(tempRoot, "Generated.cs"), artifact.Source, UTF8Encoding(false))
            let functionName = CSharpBackendNames.functionName fixture.TargetFunctionId
            let harness =
                $"""using System;
using System.Globalization;
using NyamNyam.Generated;
Console.WriteLine(NyFunctions.{functionName}(1.5).ToString("R", CultureInfo.InvariantCulture));
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
