namespace Nayamnyam.Core.Tests

open System
open System.Diagnostics
open System.IO
open Nayamnyam.Core
open Xunit

module CSharpBackendTests =
    let private lowerRepresentative () =
        let fixture = MilestoneZeroGraph.create ()
        let typed =
            match TypedSemanticIr.project fixture.Snapshot with
            | Ok value -> value
            | Error error -> failwithf "Unexpected Typed Semantic IR error: %A" error
        let execution =
            match ExecutionIr.lower typed with
            | Ok value -> value
            | Error error -> failwithf "Unexpected Execution IR error: %A" error
        fixture, execution

    let private emitRepresentative () =
        let fixture, execution = lowerRepresentative ()
        let profile = CSharpBackendProfile.systemConsoleWriteLine fixture.ConsoleWriteLineOperationId
        let artifact =
            match CSharpBackend.emit execution profile with
            | Ok value -> value
            | Error error -> failwithf "Unexpected C# backend error: %A" error
        fixture, execution, artifact

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

        use childProcess = Process.Start(startInfo)
        if isNull childProcess then
            failwith "Failed to start dotnet process."

        let stdoutTask = childProcess.StandardOutput.ReadToEndAsync()
        let stderrTask = childProcess.StandardError.ReadToEndAsync()
        childProcess.WaitForExit()
        childProcess.ExitCode, stdoutTask.Result, stderrTask.Result

    let private buildHarness (fixture: MilestoneZeroFixture) =
        let rawTypeId = StableId.createForTesting<TypeTag> "type-raw-user-input"
        let rawNameFieldId = StableId.createForTesting<FieldTag> "field-raw-name"
        let rawEmailFieldId = StableId.createForTesting<FieldTag> "field-raw-email"
        let rawDisplayNameFieldId = StableId.createForTesting<FieldTag> "field-raw-display-name"

        let rawTypeName = CSharpBackendNames.typeName rawTypeId
        let nameFieldName = CSharpBackendNames.fieldName rawNameFieldId
        let emailFieldName = CSharpBackendNames.fieldName rawEmailFieldId
        let displayNameFieldName = CSharpBackendNames.fieldName rawDisplayNameFieldId
        let policyTypeName = CSharpBackendNames.typeName fixture.PolicyTypeId
        let adapterName = CSharpBackendNames.capabilityAdapterName fixture.ConsoleCapabilityId
        let mainFunctionName = CSharpBackendNames.functionName fixture.MainFunctionId

        $"""using Nayamnyam.Generated;

var console = new {adapterName}();
var policy = new {policyTypeName}();

var valid = new {rawTypeName}(
    {nameFieldName}: "Alice",
    {emailFieldName}: "alice@example.test",
    {displayNameFieldName}: NyOption<string>.None());
NyFunctions.{mainFunctionName}(valid, policy, console);

var emptyName = new {rawTypeName}(
    {nameFieldName}: "",
    {emailFieldName}: "alice@example.test",
    {displayNameFieldName}: NyOption<string>.None());
NyFunctions.{mainFunctionName}(emptyName, policy, console);

var invalidEmail = new {rawTypeName}(
    {nameFieldName}: "Alice",
    {emailFieldName}: "not-an-email",
    {displayNameFieldName}: NyOption<string>.None());
NyFunctions.{mainFunctionName}(invalidEmail, policy, console);
"""

    [<Fact>]
    let ``C# emission is byte-for-byte deterministic`` () =
        let fixture, execution = lowerRepresentative ()
        let profile = CSharpBackendProfile.systemConsoleWriteLine fixture.ConsoleWriteLineOperationId
        let first = CSharpBackend.emit execution profile
        let second = CSharpBackend.emit execution profile
        if first <> second then
            failwith "C# backend emitted different artifacts for identical Execution IR and profile."

    [<Fact>]
    let ``C# backend requires an explicit Console capability binding`` () =
        let fixture, execution = lowerRepresentative ()
        match CSharpBackend.emit execution CSharpBackendProfile.empty with
        | Error(CSharpBackendError.MissingCapabilityBinding operationId) ->
            if operationId <> fixture.ConsoleWriteLineOperationId then
                failwithf "Expected missing binding for %A, got %A" fixture.ConsoleWriteLineOperationId operationId
        | result -> failwithf "Expected MissingCapabilityBinding, got %A" result

    [<Fact>]
    let ``C# backend rejects unknown higher-order List callback targets`` () =
        let _, execution = lowerRepresentative ()
        let unknown = StableId.createForTesting<FunctionTag> "function-m1-h-unknown-list-callback"

        let poison makeKind =
            match execution.Functions with
            | [] -> failwith "Representative execution program has no functions."
            | functionEntity :: remainingFunctions ->
                match functionEntity.Blocks with
                | [] -> failwith "Representative execution function has no blocks."
                | block :: remainingBlocks ->
                    match block.Instructions with
                    | [] -> failwith "Representative execution block has no instructions."
                    | instruction :: remainingInstructions ->
                        let poisonedInstruction =
                            { instruction with Kind = makeKind instruction.Result.Id }
                        let poisonedBlock =
                            { block with Instructions = poisonedInstruction :: remainingInstructions }
                        let poisonedFunction =
                            { functionEntity with Blocks = poisonedBlock :: remainingBlocks }
                        { execution with Functions = poisonedFunction :: remainingFunctions }

        let cases: (string * (ExecutionValueId -> ExecutionInstructionKind)) list =
            [ "ListMap", fun source -> ExecutionInstructionKind.ListMap(source, unknown)
              "ListFilter", fun source -> ExecutionInstructionKind.ListFilter(source, unknown)
              "ListAny", fun source -> ExecutionInstructionKind.ListAny(source, unknown)
              "ListAll", fun source -> ExecutionInstructionKind.ListAll(source, unknown) ]

        for label, makeKind in cases do
            match CSharpBackend.emit (poison makeKind) CSharpBackendProfile.empty with
            | Error(CSharpBackendError.UnknownCallTarget functionId) -> Assert.Equal(unknown, functionId)
            | result -> failwithf "%s should reject its unknown callback target, got %A" label result

    [<Fact>]
    let ``generated C# does not recover semantic identity from display names`` () =
        let _, _, artifact = emitRepresentative ()
        for forbidden in [ "validate_user"; "report_validation"; "RawUserInput" ] do
            if artifact.Source.Contains(forbidden, StringComparison.Ordinal) then
                failwithf "Generated C# unexpectedly contains semantic display name '%s'." forbidden
        if not (artifact.Source.Contains("NyFunctions", StringComparison.Ordinal)) then
            failwith "Generated C# does not contain the expected generated function container."

    [<Fact>]
    let ``generated C# compiles and runs the representative M0 behavior`` () =
        let fixture, _, artifact = emitRepresentative ()
        let tempRoot =
            Path.Combine(Path.GetTempPath(), "nayamnyam-csharp-backend-" + Guid.NewGuid().ToString("N"))
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
            File.WriteAllText(Path.Combine(tempRoot, "Generated.csproj"), projectFile)
            File.WriteAllText(Path.Combine(tempRoot, "Generated.cs"), artifact.Source)
            File.WriteAllText(Path.Combine(tempRoot, "Program.cs"), buildHarness fixture)

            let buildExit, buildOut, buildErr =
                runDotnet tempRoot [ "build"; "Generated.csproj"; "--configuration"; "Release"; "--nologo"; "--verbosity"; "quiet" ]
            if buildExit <> 0 then
                failwithf "Generated C# build failed.\nstdout:\n%s\nstderr:\n%s\nsource:\n%s" buildOut buildErr artifact.Source

            let assemblyPath = Path.Combine(tempRoot, "bin", "Release", "net10.0", "Generated.dll")
            let runExit, runOut, runErr = runDotnet tempRoot [ assemblyPath ]
            if runExit <> 0 then
                failwithf "Generated C# execution failed.\nstdout:\n%s\nstderr:\n%s" runOut runErr

            let normalized = runOut.Replace("\r\n", "\n").Trim()
            let expected = "valid user\nempty name\ninvalid email"
            if normalized <> expected then
                failwithf "Unexpected generated program output. Expected:\n%s\nActual:\n%s" expected normalized
        finally
            if Directory.Exists(tempRoot) then
                Directory.Delete(tempRoot, true)
