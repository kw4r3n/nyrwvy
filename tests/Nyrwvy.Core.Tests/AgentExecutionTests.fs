namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text
open Nyrwvy.Core
open Xunit

module AgentExecutionTests =
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s failed: %A" label error

    let private createWorkspace snapshot =
        match AgentWorkspace.create snapshot with
        | Ok workspace -> workspace
        | Error issues -> failwithf "Agent workspace creation failed: %A" issues

    let private runDotnet (workingDirectory: string) (arguments: string list): int * string * string =
        let startInfo = ProcessStartInfo("dotnet")
        startInfo.WorkingDirectory <- workingDirectory
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] <- "1"
        startInfo.Environment["DOTNET_NOLOGO"] <- "1"
        for argument in arguments do
            startInfo.ArgumentList.Add(argument)

        use childProcess = Process.Start(startInfo)
        if isNull childProcess then
            failwith "Failed to start dotnet process."

        let stdoutTask = childProcess.StandardOutput.ReadToEndAsync()
        let stderrTask = childProcess.StandardError.ReadToEndAsync()
        childProcess.WaitForExit()
        childProcess.ExitCode, stdoutTask.Result, stderrTask.Result

    let private buildHarness (fixture: MilestoneOneFixture): string =
        let rawTypeName = CSharpBackendNames.typeName fixture.RawUserInputTypeId
        let nameFieldName = CSharpBackendNames.fieldName fixture.RawNameFieldId
        let emailFieldName = CSharpBackendNames.fieldName fixture.RawEmailFieldId
        let displayNameFieldName = CSharpBackendNames.fieldName fixture.RawDisplayNameFieldId
        let adapterName = CSharpBackendNames.capabilityAdapterName fixture.MilestoneZero.ConsoleCapabilityId
        let mainFunctionName = CSharpBackendNames.functionName fixture.MainFunctionId

        $"""using Nyrwvy.Generated;

var console = new {adapterName}();
var valid = new {rawTypeName}(
    {nameFieldName}: "Alice",
    {emailFieldName}: "alice@example.test",
    {displayNameFieldName}: NyOption<string>.None());
var emptyName = new {rawTypeName}(
    {nameFieldName}: "",
    {emailFieldName}: "empty@example.test",
    {displayNameFieldName}: NyOption<string>.None());
var invalidEmail = new {rawTypeName}(
    {nameFieldName}: "Bob",
    {emailFieldName}: "not-an-email",
    {displayNameFieldName}: NyOption<string>.None());

var mixed = NyList<{rawTypeName}>.Create(valid, emptyName, invalidEmail);
NyFunctions.{mainFunctionName}(mixed, console);

var allValid = NyList<{rawTypeName}>.Create(valid, valid);
NyFunctions.{mainFunctionName}(allValid, console);

var empty = NyList<{rawTypeName}>.Empty();
NyFunctions.{mainFunctionName}(empty, console);
"""

    let private executeArtifact (fixture: MilestoneOneFixture) (artifact: CSharpArtifact): string =
        let tempRoot =
            Path.Combine(Path.GetTempPath(), "nyrwvy-agent-execution-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore

        try
            let projectFile =
                """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
"""
            File.WriteAllText(Path.Combine(tempRoot, "Generated.csproj"), projectFile, UTF8Encoding(false))
            File.WriteAllText(Path.Combine(tempRoot, "Generated.cs"), artifact.Source, UTF8Encoding(false))
            File.WriteAllText(Path.Combine(tempRoot, "Program.cs"), buildHarness fixture, UTF8Encoding(false))

            let buildExit, buildOut, buildErr =
                runDotnet
                    tempRoot
                    [ "build"
                      "Generated.csproj"
                      "--configuration"; "Release"
                      "--nologo"
                      "--verbosity"; "quiet" ]
            if buildExit <> 0 then
                failwithf
                    "Generated post-agent-commit C# build failed.\nstdout:\n%s\nstderr:\n%s\nsource:\n%s"
                    buildOut
                    buildErr
                    artifact.Source

            let assemblyPath = Path.Combine(tempRoot, "bin", "Release", "net10.0", "Generated.dll")
            let runExit, runOut, runErr = runDotnet tempRoot [ assemblyPath ]
            if runExit <> 0 then
                failwithf
                    "Generated post-agent-commit C# execution failed.\nstdout:\n%s\nstderr:\n%s"
                    runOut
                    runErr

            runOut.Replace("\r\n", "\n").Trim()
        finally
            if Directory.Exists tempRoot then
                Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``uncommitted proposal cannot replace the workspace execution source`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture.Snapshot
        let proposal =
            AgentWorkflow.simulate
                workspace
                [ AgentOperationIntent.RenameDisplayName(
                      AgentRenameTarget.Function fixture.ValidateFunctionId,
                      "validate_uncommitted"
                  ) ]
            |> unwrap "agent simulation"

        let typed =
            AgentExecution.projectTypedSemanticIr workspace
            |> unwrap "agent execution projection"

        if typed.SourceSnapshotId <> fixture.Snapshot.Id then
            failwithf "Execution projected the wrong published snapshot: %A" typed.SourceSnapshotId
        if typed.SourceSnapshotId = AgentChangeProposal.proposedSnapshotId proposal then
            failwith "An uncommitted proposal became the execution source."

    [<Fact>]
    let ``incomplete published agent workspace remains non-executable`` () =
        let fixture = MilestoneZeroGraph.create ()
        let workspace = createWorkspace fixture.Snapshot
        let proposal =
            AgentWorkflow.simulate
                workspace
                [ AgentOperationIntent.AddFunctionParameter
                      { TargetFunctionId = fixture.ValidateFunctionId
                        NewParameterDisplayName = "policy"
                        NewParameterType = TypeRef.Named fixture.PolicyTypeId
                        CallSitePlans = [ AgentCallPlan.LeaveMissing fixture.MainValidateCallNodeId ] } ]
            |> unwrap "incomplete agent simulation"

        let incompleteWorkspace =
            match AgentWorkflow.commitWithPolicy workspace proposal CommitPolicy.AllowBlockingObligations with
            | AgentCommitResult.Committed(nextWorkspace, publication) ->
                match publication.Readiness with
                | TransactionReadiness.Incomplete [ _ ] -> nextWorkspace
                | readiness -> failwithf "Expected incomplete publication, got %A" readiness
            | AgentCommitResult.Rejected diagnostics ->
                failwithf "Explicit incomplete publication was rejected: %A" diagnostics

        match AgentExecution.projectTypedSemanticIr incompleteWorkspace with
        | Error(TypedSemanticIrError.IncompleteSnapshot [ _ ]) -> ()
        | result -> failwithf "Expected incomplete execution rejection, got %A" result

    [<Fact; Trait("Suite", "Extended")>]
    let ``reviewed M1 cross-function commit lowers and executes from the exact committed workspace`` () =
        let fixture = MilestoneOneGraph.create ()
        let workspace = createWorkspace fixture.Snapshot
        let intent =
            AgentOperationIntent.AddFunctionParameter
                { TargetFunctionId = fixture.ReportResultsFunctionId
                  NewParameterDisplayName = "context"
                  NewParameterType = TypeRef.Primitive PrimitiveType.Unit
                  CallSitePlans =
                    [ AgentCallPlan.BindExistingValue(
                          fixture.MainReportResultsCallNodeId,
                          fixture.MainUnitNodeId
                      ) ] }
        let proposal =
            AgentWorkflow.simulate workspace [ intent ]
            |> unwrap "M1 cross-function simulation"

        let committedWorkspace =
            match AgentWorkflow.commit workspace proposal with
            | AgentCommitResult.Committed(nextWorkspace, publication)
                when publication.Readiness = TransactionReadiness.SemanticallyReady -> nextWorkspace
            | AgentCommitResult.Committed(_, publication) ->
                failwithf "Committed M1 edit is not semantically ready: %A" publication.Readiness
            | AgentCommitResult.Rejected diagnostics ->
                failwithf "Reviewed M1 edit was rejected: %A" diagnostics

        let typed =
            AgentExecution.projectTypedSemanticIr committedWorkspace
            |> unwrap "post-commit typed semantic projection"
        let committedSnapshotId = AgentWorkspace.currentSnapshotId committedWorkspace
        if typed.SourceSnapshotId <> committedSnapshotId then
            failwithf
                "Execution projection lost exact committed identity. Expected %s, got %s"
                (StableId.value committedSnapshotId)
                (StableId.value typed.SourceSnapshotId)
        if typed.SourceSnapshotId <> AgentChangeProposal.proposedSnapshotId proposal then
            failwith "Execution projection is not bound to the exact reviewed proposal that was committed."

        let execution = ExecutionIr.lower typed |> unwrap "post-commit Execution IR lowering"
        let profile =
            CSharpBackendProfile.systemConsoleWriteLine fixture.MilestoneZero.ConsoleWriteLineOperationId
        let artifact = CSharpBackend.emit execution profile |> unwrap "post-commit C# emission"
        let output = executeArtifact fixture artifact
        let expectedOutput =
            String.concat "\n"
                [ "validation errors present"
                  "all users valid"
                  "no users" ]
        Assert.Equal(expectedOutput, output)
