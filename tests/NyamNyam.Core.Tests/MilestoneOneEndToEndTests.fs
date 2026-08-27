namespace NyamNyam.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text
open NyamNyam.Core
open Xunit

module MilestoneOneEndToEndTests =
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s failed: %A" label error

    let private assertUuidV7Payload label payload =
        let mutable parsed = Guid.Empty
        if not (Guid.TryParseExact(payload, "D", &parsed)) || payload.Length <> 36 || payload[14] <> '7' then
            failwithf "%s was not allocated as UUIDv7: %s" label payload

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
        let validateUsersFunctionName = CSharpBackendNames.functionName fixture.ValidateUsersFunctionId
        let countInvalidFunctionName = CSharpBackendNames.functionName fixture.CountInvalidResultsFunctionId

        $"""using System;
using NyamNyam.Generated;

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

var validated = NyFunctions.{validateUsersFunctionName}(mixed);
Console.WriteLine(NyFunctions.{countInvalidFunctionName}(validated));
"""

    let private lowerAndEmit
        (fixture: MilestoneOneFixture)
        (snapshot: Snapshot)
        : ExecutionProgram * CSharpArtifact =
        let typed = TypedSemanticIr.project snapshot |> unwrap "Typed Semantic IR projection"
        let execution = ExecutionIr.lower typed |> unwrap "Execution IR lowering"
        let profile =
            CSharpBackendProfile.systemConsoleWriteLine fixture.MilestoneZero.ConsoleWriteLineOperationId
        let artifact = CSharpBackend.emit execution profile |> unwrap "C# backend emission"
        execution, artifact

    let private executeArtifact
        (fixture: MilestoneOneFixture)
        (artifact: CSharpArtifact)
        : string =
        let tempRoot =
            Path.Combine(Path.GetTempPath(), "nayamnyam-m1-expanded-" + Guid.NewGuid().ToString("N"))
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
                    "Generated M1 C# build failed.\nstdout:\n%s\nstderr:\n%s\nsource:\n%s"
                    buildOut
                    buildErr
                    artifact.Source

            let assemblyPath = Path.Combine(tempRoot, "bin", "Release", "net10.0", "Generated.dll")
            let runExit, runOut, runErr = runDotnet tempRoot [ assemblyPath ]
            if runExit <> 0 then
                failwithf "Generated M1 C# execution failed.\nstdout:\n%s\nstderr:\n%s" runOut runErr

            runOut.Replace("\r\n", "\n").Trim()
        finally
            if Directory.Exists tempRoot then
                Directory.Delete(tempRoot, true)

    let private createAgentWorkspace snapshot =
        match AgentWorkspace.create snapshot with
        | Ok workspace -> workspace
        | Error issues -> failwithf "Agent workspace creation failed: %A" issues

    let private containsHigherOrderCaller (fixture: MilestoneOneFixture) callers =
        callers
        |> List.exists (fun callSite ->
            callSite.CallerFunctionId = fixture.ValidateUsersFunctionId
            && callSite.CallNodeId = fixture.ValidateUsersMapNodeId
            && callSite.CalleeFunctionId = fixture.ValidateFunctionId)

    let private containsReportCaller (fixture: MilestoneOneFixture) callers =
        callers
        |> List.exists (fun callSite ->
            callSite.CallerFunctionId = fixture.MainFunctionId
            && callSite.CallNodeId = fixture.MainReportResultsCallNodeId
            && callSite.CalleeFunctionId = fixture.ReportResultsFunctionId)

    [<Fact>]
    let ``M1 expanded validation program composes List Int64 Result effects and reviewed cross-function editing`` () =
        let fixture = MilestoneOneGraph.create ()

        let issues = SnapshotValidator.validate fixture.Snapshot
        Assert.Empty(issues)
        match SnapshotReadiness.evaluate fixture.Snapshot with
        | Ok SemanticReadiness.Complete -> ()
        | readiness -> failwithf "Expanded M1 snapshot is not complete: %A" readiness

        let canonical = SnapshotCodec.serialize fixture.Snapshot |> unwrap "M1 snapshot serialization"
        let reloaded = SnapshotCodec.deserialize canonical |> unwrap "M1 snapshot reload"
        let canonicalAgain = SnapshotCodec.serialize reloaded |> unwrap "M1 snapshot reserialization"
        Assert.Equal(canonical, canonicalAgain)

        let initialValidateCallers =
            SemanticQuery.getCallers reloaded fixture.ValidateFunctionId
            |> unwrap "validate_user callers"
        if not (containsHigherOrderCaller fixture initialValidateCallers) then
            failwithf "validate_user is not referenced by the expected ListMap call site: %A" initialValidateCallers

        let initialReportCallers =
            SemanticQuery.getCallers reloaded fixture.ReportResultsFunctionId
            |> unwrap "report_results callers"
        if not (containsReportCaller fixture initialReportCallers) then
            failwithf "report_results is not referenced by the expected m1_main call site: %A" initialReportCallers

        let validateUsersFocus =
            SemanticQuery.focusFunction reloaded fixture.ValidateUsersFunctionId
            |> unwrap "validate_users focus"
        Assert.Contains(
            validateUsersFocus.Callees,
            fun site ->
                site.CallerFunctionId = fixture.ValidateUsersFunctionId
                && site.CallNodeId = fixture.ValidateUsersMapNodeId
                && site.CalleeFunctionId = fixture.ValidateFunctionId
        )

        let execution, artifact = lowerAndEmit fixture reloaded
        let executionJson = ExecutionIrCodec.serialize execution
        Assert.Contains("\"listMap\"", executionJson)
        Assert.Contains("\"listFilter\"", executionJson)
        Assert.Contains(StableId.value fixture.ValidateFunctionId, executionJson)

        let initialOutput = executeArtifact fixture artifact
        let expectedOutput =
            String.concat "\n"
                [ "validation errors present"
                  "all users valid"
                  "no users"
                  "2" ]
        Assert.Equal(expectedOutput, initialOutput)

        let agentWorkspace = createAgentWorkspace reloaded
        let beforeSnapshotFingerprint =
            AgentWorkspace.currentSnapshotFingerprint agentWorkspace
            |> unwrap "agent snapshot fingerprint before edit"
        let beforeSemanticFingerprint =
            AgentWorkspace.currentSemanticFingerprint agentWorkspace
            |> unwrap "agent semantic fingerprint before edit"

        let agentReportCallers =
            AgentWorkspace.getCallers agentWorkspace fixture.ReportResultsFunctionId
            |> unwrap "agent report_results callers"
        if not (containsReportCaller fixture agentReportCallers) then
            failwith "Agent bounded query did not expose the report_results caller."

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
            match AgentWorkflow.simulate agentWorkspace [ intent ] with
            | Ok proposal -> proposal
            | Error error -> failwithf "Agent cross-function simulation failed: %A" error
        let review = AgentChangeProposal.review proposal

        if review.Readiness <> TransactionReadiness.SemanticallyReady then
            failwithf "Cross-function review is not semantically ready: %A" review.Readiness
        if review.RequestedOperations <> [ intent ] then
            failwithf "Requested cross-function intent changed in review: %A" review.RequestedOperations

        let addedParameterId =
            match review.ResultingChange.Diff.ParametersAdded,
                  review.ResultingChange.Diff.ArgumentsAttached,
                  review.ResultingChange.Diff.ObligationsCreated with
            | [ parameter ], [ argument ], []
                when parameter.OwnerFunctionId = fixture.ReportResultsFunctionId
                     && parameter.AddedDisplayName = "context"
                     && parameter.AddedType = TypeRef.Primitive PrimitiveType.Unit
                     && argument.CallNodeId = fixture.MainReportResultsCallNodeId
                     && argument.TargetParameterId = parameter.AddedParameterId
                     && argument.ValueNodeId = fixture.MainUnitNodeId ->
                parameter.AddedParameterId
            | changes -> failwithf "Unexpected cross-function semantic diff: %A" changes

        assertUuidV7Payload "agent-added parameter ID" (StableId.value addedParameterId)

        let committedWorkspace =
            match AgentWorkflow.commit agentWorkspace proposal with
            | AgentCommitResult.Committed(nextWorkspace, publication) ->
                if publication.Readiness <> TransactionReadiness.SemanticallyReady then
                    failwithf "Committed cross-function edit is not ready: %A" publication.Readiness
                nextWorkspace
            | AgentCommitResult.Rejected diagnostics ->
                failwithf "Reviewed M1 cross-function edit was rejected: %A" diagnostics

        match AgentWorkspace.getFunction committedWorkspace fixture.ReportResultsFunctionId with
        | Ok functionEntity ->
            match functionEntity.Parameters |> List.tryFind (fun parameter -> parameter.Id = addedParameterId) with
            | Some parameter
                when parameter.DisplayName = "context"
                     && parameter.Type = TypeRef.Primitive PrimitiveType.Unit -> ()
            | parameter -> failwithf "Committed report_results parameter is wrong: %A" parameter
        | Error error -> failwithf "Committed report_results lookup failed: %A" error

        match AgentWorkspace.getFunction committedWorkspace fixture.MainFunctionId with
        | Ok mainFunction ->
            match mainFunction.Body.Nodes |> List.tryFind (fun node -> node.Id = fixture.MainReportResultsCallNodeId) with
            | Some { Kind = SemanticNodeKind.DirectCall(targetFunctionId, arguments) }
                when targetFunctionId = fixture.ReportResultsFunctionId ->
                if
                    arguments
                    |> List.exists (fun argument ->
                        argument.TargetParameterId = addedParameterId
                        && argument.ValueNodeId = fixture.MainUnitNodeId)
                    |> not
                then
                    failwithf "Committed m1_main call does not bind the new parameter: %A" arguments
            | node -> failwithf "Committed m1_main report call is wrong: %A" node
        | Error error -> failwithf "Committed m1_main lookup failed: %A" error

        let afterReportCallers =
            AgentWorkspace.getCallers committedWorkspace fixture.ReportResultsFunctionId
            |> unwrap "post-edit report_results callers"
        if not (containsReportCaller fixture afterReportCallers) then
            failwithf "report_results caller relation changed after signature edit: %A" afterReportCallers

        let afterValidateCallers =
            AgentWorkspace.getCallers committedWorkspace fixture.ValidateFunctionId
            |> unwrap "post-edit validate_user callers"
        if not (containsHigherOrderCaller fixture afterValidateCallers) then
            failwithf "Stable ListMap callback relation changed after unrelated cross-function edit: %A" afterValidateCallers

        let afterSnapshotFingerprint =
            AgentWorkspace.currentSnapshotFingerprint committedWorkspace
            |> unwrap "agent snapshot fingerprint after edit"
        let afterSemanticFingerprint =
            AgentWorkspace.currentSemanticFingerprint committedWorkspace
            |> unwrap "agent semantic fingerprint after edit"
        if beforeSnapshotFingerprint = afterSnapshotFingerprint then
            failwith "Cross-function edit did not change the exact snapshot fingerprint."
        if beforeSemanticFingerprint = afterSemanticFingerprint then
            failwith "Cross-function edit did not change the semantic fingerprint."
