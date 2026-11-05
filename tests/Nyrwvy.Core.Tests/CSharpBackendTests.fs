namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open Nyrwvy.Core
open Xunit

module CSharpBackendTests =
    let private literalProbeValue = "CRLF\r\nCR\rLF\n\t\"\\\u0000\u2028\u00E9\U0001F642"
    let private literalProbeFunctionId = StableId.createForTesting<FunctionTag> "function-backend-literal-probe"

    let private withLiteralProbe (fixture: MilestoneZeroFixture) =
        let nodeId = StableId.createForTesting<GraphNodeTag> "node-backend-literal-probe"
        let functionEntity: SemanticFunction =
            { MustNotWait = false; Exported = true; Id = literalProbeFunctionId
              TypeParameters = []
              DisplayName = "literal_probe"
              Parameters = []
              ResultType = TypeRef.Primitive PrimitiveType.String
              RequiredEffects = []
              Body =
                { Nodes = [ { Id = nodeId; Kind = SemanticNodeKind.StringLiteral literalProbeValue } ]
                  ResultNodeId = nodeId } }
        { fixture with Snapshot = { fixture.Snapshot with Functions = fixture.Snapshot.Functions @ [ functionEntity ] } }

    let private lowerRepresentative () =
        let fixture = MilestoneZeroGraph.create () |> withLiteralProbe
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

    let internal runDotnet (workingDirectory: string) (arguments: string list) =
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
        let literalProbeName = CSharpBackendNames.functionName literalProbeFunctionId
        let literalCodeUnits =
            literalProbeValue.ToCharArray()
            |> Array.map (fun character -> "(char)" + string (int character))
            |> String.concat ", "
        let expectedLiteral = "new string(new char[] { " + literalCodeUnits + " })"

        $"""using Nyrwvy.Generated;

var console = new {adapterName}();
var policy = new {policyTypeName}();

var valid = new {rawTypeName}(
    {nameFieldName}: "Alice",
    {emailFieldName}: "alice@example.test",
    {displayNameFieldName}: NyOption<NyStr>.None());
NyFunctions.{mainFunctionName}(valid, policy, console);

var emptyName = new {rawTypeName}(
    {nameFieldName}: "",
    {emailFieldName}: "alice@example.test",
    {displayNameFieldName}: NyOption<NyStr>.None());
NyFunctions.{mainFunctionName}(emptyName, policy, console);

var invalidEmail = new {rawTypeName}(
    {nameFieldName}: "Alice",
    {emailFieldName}: "not-an-email",
    {displayNameFieldName}: NyOption<NyStr>.None());
NyFunctions.{mainFunctionName}(invalidEmail, policy, console);

if (NyFunctions.{literalProbeName}() != {expectedLiteral})
    throw new System.InvalidOperationException("Generated literal changed UTF-16 code units.");
"""

    [<Fact; Trait("Suite", "Extended")>]
    let ``generated C# executes Bind effects once in explicit order and skips unselected bodies`` () =
        let execution, profile, harness = AgentExpressionBindTests.executionFixture ()
        let artifact =
            match CSharpBackend.emit execution profile with
            | Ok value -> value
            | Error error -> failwithf "Bind emission failed: %A" error
        let tempRoot = Path.Combine(Path.GetTempPath(), "nyrwvy-bind-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore
        try
            let project =
                [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
                  "  <PropertyGroup>"
                  "    <OutputType>Exe</OutputType>"
                  "    <TargetFramework>net10.0</TargetFramework>"
                  "    <Nullable>enable</Nullable>"
                  "    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>"
                  "  </PropertyGroup>"
                  "</Project>" ] |> String.concat "\n"
            File.WriteAllText(Path.Combine(tempRoot, "Generated.csproj"), project)
            File.WriteAllText(Path.Combine(tempRoot, "Generated.cs"), artifact.Source)
            File.WriteAllText(Path.Combine(tempRoot, "Program.cs"), harness)
            let buildExit, buildOut, buildErr = runDotnet tempRoot [ "build"; "Generated.csproj"; "--configuration"; "Release"; "--nologo"; "--verbosity"; "quiet" ]
            Assert.True((buildExit = 0), $"Generated Bind build failed:\n{buildOut}\n{buildErr}\n{artifact.Source}")
            let runExit, runOut, runErr = runDotnet tempRoot [ Path.Combine(tempRoot, "bin", "Release", "net10.0", "Generated.dll") ]
            Assert.True((runExit = 0), $"Generated Bind execution failed:\n{runOut}\n{runErr}")
            Assert.Equal("acquire,message,payload,second\nacquire,second,message,payload\nskipped", runOut.Replace("\r\n", "\n").Trim())
        finally
            Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``C# emission is byte-for-byte deterministic with canonical LF and exact literal escaping`` () =
        let fixture, execution, first = emitRepresentative ()
        let profile = CSharpBackendProfile.systemConsoleWriteLine fixture.ConsoleWriteLineOperationId
        let second =
            match CSharpBackend.emit execution profile with
            | Ok value -> value
            | Error error -> failwithf "Unexpected C# backend error: %A" error
        Assert.Equal<CSharpArtifact>(first, second)
        Assert.DoesNotContain("\r", first.Source)
        Assert.StartsWith("// Generated by Nyrwvy. Do not edit as authoritative source.\n#nullable enable\n", first.Source)
        Assert.EndsWith("#pragma warning restore CS0219\n", first.Source)
        Assert.Contains("\"CRLF\\r\\nCR\\rLF\\n\\t\\\"\\\\\\u0000\\u2028\\u00E9\\uD83D\\uDE42\"", first.Source)

    [<Fact>]
    let ``C# artifact exposes emitted public symbol manifest`` () =
        let fixture, _, artifact = emitRepresentative ()
        let find (id: 'Id) (items: CSharpSymbol<'Id> list) =
            items
            |> List.find (fun item -> item.Id = id)
            |> fun item -> item.Name

        Assert.Equal(CSharpBackendNames.generatedNamespace, artifact.Symbols.Namespace)
        Assert.Equal(CSharpBackendNames.functionContainerName, artifact.Symbols.FunctionContainer)
        Assert.Equal(
            CSharpBackendNames.typeName fixture.PolicyTypeId,
            find fixture.PolicyTypeId artifact.Symbols.Types
        )
        Assert.Equal(
            CSharpBackendNames.functionName fixture.MainFunctionId,
            find fixture.MainFunctionId artifact.Symbols.Functions
        )
        Assert.Equal(
            CSharpBackendNames.parameterName fixture.ValidateInputParameterId,
            find fixture.ValidateInputParameterId artifact.Symbols.Parameters
        )
        Assert.Equal(
            CSharpBackendNames.capabilityName fixture.ConsoleCapabilityId,
            find fixture.ConsoleCapabilityId artifact.Symbols.Capabilities
        )
        Assert.Equal(
            CSharpBackendNames.capabilityOperationName fixture.ConsoleWriteLineOperationId,
            find fixture.ConsoleWriteLineOperationId artifact.Symbols.CapabilityOperations
        )
        Assert.Equal(
            CSharpBackendNames.capabilityAdapterName fixture.ConsoleCapabilityId,
            find fixture.ConsoleCapabilityId artifact.Symbols.CapabilityAdapters
        )

    [<Fact>]
    let ``C# backend requires an explicit Console capability binding`` () =
        let fixture, execution = lowerRepresentative ()
        match CSharpBackend.emit execution CSharpBackendProfile.empty with
        | Error(CSharpBackendError.MissingCapabilityBinding operationId) ->
            if operationId <> fixture.ConsoleWriteLineOperationId then
                failwithf "Expected missing binding for %A, got %A" fixture.ConsoleWriteLineOperationId operationId
        | result -> failwithf "Expected MissingCapabilityBinding, got %A" result

    [<Fact>]
    let ``C# backend rejects duplicate capability bindings`` () =
        let fixture, execution = lowerRepresentative ()
        let binding =
            { OperationId = fixture.ConsoleWriteLineOperationId
              Kind = CSharpCapabilityBindingKind.SystemConsoleWriteLine }
        let profile = { CSharpBackendProfile.empty with CapabilityBindings = [ binding; binding ] }
        match CSharpBackend.emit execution profile with
        | Error(CSharpBackendError.DuplicateCapabilityBinding operationId) -> Assert.Equal(fixture.ConsoleWriteLineOperationId, operationId)
        | result -> failwithf "Expected DuplicateCapabilityBinding, got %A" result

    [<Fact>]
    let ``C# backend rejects unknown capability binding operations`` () =
        let _, execution = lowerRepresentative ()
        let unknown = StableId.createForTesting<CapabilityOperationTag> "operation-backend-unknown-binding"
        match CSharpBackend.emit execution (CSharpBackendProfile.systemConsoleWriteLine unknown) with
        | Error(CSharpBackendError.UnknownCapabilityBindingOperation operationId) -> Assert.Equal(unknown, operationId)
        | result -> failwithf "Expected UnknownCapabilityBindingOperation, got %A" result

    [<Fact>]
    let ``C# backend rejects incompatible System Console WriteLine bindings`` () =
        let fixture, execution = lowerRepresentative ()
        let incompatible =
            { execution with
                Capabilities =
                    execution.Capabilities
                    |> List.map (fun capability ->
                        if capability.Id <> fixture.ConsoleCapabilityId then capability
                        else
                            { capability with
                                Operations =
                                    capability.Operations
                                    |> List.map (fun operation ->
                                        if operation.Id = fixture.ConsoleWriteLineOperationId then
                                            { operation with Parameters = [] }
                                        else operation) }) }
        match CSharpBackend.emit incompatible (CSharpBackendProfile.systemConsoleWriteLine fixture.ConsoleWriteLineOperationId) with
        | Error(CSharpBackendError.IncompatibleSystemConsoleWriteLineBinding operationId) -> Assert.Equal(fixture.ConsoleWriteLineOperationId, operationId)
        | result -> failwithf "Expected IncompatibleSystemConsoleWriteLineBinding, got %A" result

    [<Fact>]
    let ``C# backend rejects unknown capability operations used by instructions`` () =
        let _, execution = lowerRepresentative ()
        let unknown = StableId.createForTesting<CapabilityOperationTag> "operation-backend-unknown-use"
        let mutable replaced = false
        let poisoned =
            { execution with
                Functions =
                    execution.Functions
                    |> List.map (fun fn ->
                        { fn with
                            Blocks =
                                fn.Blocks
                                |> List.map (fun block ->
                                    { block with
                                        Instructions =
                                            block.Instructions
                                            |> List.map (fun instruction ->
                                                match instruction.Kind with
                                                | ExecutionInstructionKind.CapabilityInvoke(receiver, _, arguments) when not replaced ->
                                                    replaced <- true
                                                    { instruction with Kind = ExecutionInstructionKind.CapabilityInvoke(receiver, unknown, arguments) }
                                                | _ -> instruction) }) }) }
        Assert.True(replaced, "Representative execution program had no capability invocation.")
        match CSharpBackend.emit poisoned CSharpBackendProfile.empty with
        | Error(CSharpBackendError.UnknownCapabilityOperation operationId) -> Assert.Equal(unknown, operationId)
        | result -> failwithf "Expected UnknownCapabilityOperation, got %A" result

    [<Fact>]
    let ``external binding does not erase FailureType or silently translate host exceptions`` () =
        let fixture, execution = lowerRepresentative ()
        let withFailure =
            { execution with
                Capabilities =
                    execution.Capabilities
                    |> List.map (fun capability ->
                        if capability.Id <> fixture.ConsoleCapabilityId then capability
                        else
                            { capability with
                                Operations =
                                    capability.Operations
                                    |> List.map (fun operation ->
                                        { operation with FailureType = Some(TypeRef.Primitive PrimitiveType.String) }) }) }
        let profile = CSharpBackendProfile.externalImplementations [ fixture.ConsoleWriteLineOperationId ]
        match CSharpBackend.emit withFailure profile with
        | Error(CSharpBackendError.UnsupportedCapabilityFailure operationId) ->
            Assert.Equal(fixture.ConsoleWriteLineOperationId, operationId)
        | result -> failwithf "FailureType must not acquire an implicit exception mapping: %A" result

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
            [ "ListMap", fun source -> ExecutionInstructionKind.ListMap(source, unknown, [])
              "ListFilter", fun source -> ExecutionInstructionKind.ListFilter(source, unknown, [])
              "ListAny", fun source -> ExecutionInstructionKind.ListAny(source, unknown, [])
              "ListFindIndex", fun source -> ExecutionInstructionKind.ListFindIndex(source, unknown, [])
              "ListAll", fun source -> ExecutionInstructionKind.ListAll(source, unknown, []) ]

        for label, makeKind in cases do
            match CSharpBackend.emit (poison makeKind) CSharpBackendProfile.empty with
            | Error(CSharpBackendError.UnknownCallTarget functionId) -> Assert.Equal(unknown, functionId)
            | result -> failwithf "%s should reject its unknown callback target, got %A" label result

    [<Fact>]
    let ``C# backend rejects a tail call binding an unknown parameter`` () =
        let fixture, execution = lowerRepresentative ()
        let functionEntity = List.head execution.Functions
        let block = List.head functionEntity.Blocks
        let instruction = List.head block.Instructions
        let argument: ExecutionCallArgument =
            { TargetParameterId = StableId.createForTesting<ParameterTag> "parameter-unknown-tail"
              ValueId = instruction.Result.Id }
        let poisoned =
            { functionEntity with
                Blocks = { block with Terminator = ExecutionTerminator.TailCall(instruction.OriginNodeId, [ argument ]) } :: List.tail functionEntity.Blocks }
        match CSharpBackend.emit { execution with Functions = poisoned :: List.tail execution.Functions } (CSharpBackendProfile.systemConsoleWriteLine fixture.ConsoleWriteLineOperationId) with
        | Error(CSharpBackendError.MalformedExecutionIr _) -> ()
        | result -> failwithf "Expected MalformedExecutionIr, got %A" result

    [<Fact>]
    let ``generated C# does not recover semantic identity from display names`` () =
        let _, _, artifact = emitRepresentative ()
        for forbidden in [ "validate_user"; "report_validation"; "RawUserInput" ] do
            if artifact.Source.Contains(forbidden, StringComparison.Ordinal) then
                failwithf "Generated C# unexpectedly contains semantic display name '%s'." forbidden
        if not (artifact.Source.Contains("NyFunctions", StringComparison.Ordinal)) then
            failwith "Generated C# does not contain the expected generated function container."

    [<Fact; Trait("Suite", "Extended")>]
    let ``generated C# compiles and runs the representative M0 behavior`` () =
        let original = MilestoneZeroGraph.create () |> withLiteralProbe
        let unusedLiteral: SemanticNode =
            { Id = StableId.createForTesting<GraphNodeTag> "node-backend-unused-literal"
              Kind = SemanticNodeKind.Int64Literal 42L }
        let fixture =
            { original with
                Snapshot =
                    { original.Snapshot with
                        Functions =
                            original.Snapshot.Functions
                            |> List.map (fun functionEntity ->
                                if functionEntity.Id = original.ValidateFunctionId then
                                    { functionEntity with
                                        Body = { functionEntity.Body with Nodes = unusedLiteral :: functionEntity.Body.Nodes } }
                                else
                                    functionEntity) } }
        let typed =
            match TypedSemanticIr.project fixture.Snapshot with
            | Ok value -> value
            | Error error -> failwithf "Unexpected Typed Semantic IR error: %A" error
        let execution =
            match ExecutionIr.lower typed with
            | Ok value -> value
            | Error error -> failwithf "Unexpected Execution IR error: %A" error
        let profile = CSharpBackendProfile.systemConsoleWriteLine fixture.ConsoleWriteLineOperationId
        let artifact =
            match CSharpBackend.emit execution profile with
            | Ok value -> value
            | Error error -> failwithf "Unexpected C# backend error: %A" error
        let tempRoot =
            Path.Combine(Path.GetTempPath(), "nyrwvy-csharp-backend-" + Guid.NewGuid().ToString("N"))
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
