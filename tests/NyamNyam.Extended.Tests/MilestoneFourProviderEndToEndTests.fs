namespace NyamNyam.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text
open NyamNyam.Core
open NyamNyam.Tools.AuthoringSession
open Xunit

module MilestoneFourProviderEndToEndTests =
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private unitType = TypeRef.Primitive PrimitiveType.Unit

    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s failed: %A" label error

    let private currentSnapshotId (session: AuthoringSession) =
        (session.Status() |> unwrap "authoring session status").SnapshotId

    let private commit (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations)
        |> unwrap "authoring commit"
        |> ignore

    let private createOutcomeType (session: AuthoringSession) =
        let request: AgentCreateTypeDeclarationRequest =
            { ExpectedBaseSnapshotId = currentSnapshotId session
              DisplayName = "ValidationOutcome"
              Kind =
                AgentTypeDeclarationKindIntent.Union
                    [ { DisplayName = "Accepted"
                        Fields = [ { DisplayName = "message"; Type = stringType } ] }
                      { DisplayName = "Rejected"
                        Fields = [ { DisplayName = "reason"; Type = stringType } ] } ] }

        session.SimulateCreateType request
        |> unwrap "outcome type simulation"
        |> commit session

        let outcome =
            session.Declarations().Types
            |> List.find (fun item -> item.DisplayName = "ValidationOutcome")

        match outcome.Kind with
        | SemanticTypeKind.Product _ -> failwith "ValidationOutcome was not materialized as a union."
        | SemanticTypeKind.Union variants ->
            let accepted = variants |> List.find (fun item -> item.DisplayName = "Accepted")
            let rejected = variants |> List.find (fun item -> item.DisplayName = "Rejected")
            outcome, accepted, List.exactlyOne accepted.Fields, rejected, List.exactlyOne rejected.Fields

    let private createOutputEffect (session: AuthoringSession) =
        let request: AgentCreateEffectDeclarationRequest =
            { ExpectedBaseSnapshotId = currentSnapshotId session
              DisplayName = "ConsoleOutput" }

        session.SimulateCreateEffect request
        |> unwrap "output effect simulation"
        |> commit session

        session.Declarations().Effects
        |> List.find (fun item -> item.DisplayName = "ConsoleOutput")

    let private createConsoleCapability (session: AuthoringSession) effectId =
        let request: AgentCreateCapabilityDeclarationRequest =
            { ExpectedBaseSnapshotId = currentSnapshotId session
              DisplayName = "Console"
              Operations =
                [ { DisplayName = "write_line"
                    Parameters = [ { DisplayName = "text"; Type = stringType } ]
                    ResultType = unitType
                    FailureType = None
                    EffectId = effectId } ] }

        session.SimulateCreateCapability request
        |> unwrap "console capability simulation"
        |> commit session

        session.Declarations().Capabilities
        |> List.find (fun item -> item.DisplayName = "Console")

    let private createFunction
        (session: AuthoringSession)
        displayName
        (parameters: AgentExpressionStructuralWorkflow.FunctionCreation.ParameterIntent list)
        resultType
        requiredEffects
        body
        =
        let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = currentSnapshotId session
              DisplayName = displayName
              Parameters = parameters
              ResultType = resultType
              RequiredEffects = requiredEffects
              Body = body }

        session.SimulateCreateFunction request
        |> unwrap ($"{displayName} function simulation")
        |> commit session

        session.Declarations().Functions
        |> List.find (fun item -> item.DisplayName = displayName)

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

    let private executeArtifact capabilityId entryFunctionId (artifact: CSharpArtifact) =
        let root =
            Path.Combine(Path.GetTempPath(), "nyamnyam-m4-provider-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore

        try
            let project =
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
            let adapterName = CSharpBackendNames.capabilityAdapterName capabilityId
            let functionName = CSharpBackendNames.functionName entryFunctionId
            let harness =
                $"""using NyamNyam.Generated;

var console = new {adapterName}();
NyFunctions.{functionName}("alice@example.test", console);
NyFunctions.{functionName}("not-an-email", console);
"""

            File.WriteAllText(Path.Combine(root, "Generated.csproj"), project, UTF8Encoding(false))
            File.WriteAllText(Path.Combine(root, "Generated.cs"), artifact.Source, UTF8Encoding(false))
            File.WriteAllText(Path.Combine(root, "Program.cs"), harness, UTF8Encoding(false))

            let buildExit, buildOut, buildErr =
                runDotnet
                    root
                    [ "build"
                      "Generated.csproj"
                      "--configuration"; "Release"
                      "--nologo"
                      "--verbosity"; "quiet" ]

            if buildExit <> 0 then
                failwithf
                    "Generated M4 C# build failed.\nstdout:\n%s\nstderr:\n%s\nsource:\n%s"
                    buildOut
                    buildErr
                    artifact.Source

            let assemblyPath = Path.Combine(root, "bin", "Release", "net10.0", "Generated.dll")
            let runExit, runOut, runErr = runDotnet root [ assemblyPath ]
            if runExit <> 0 then
                failwithf "Generated M4 C# execution failed.\nstdout:\n%s\nstderr:\n%s" runOut runErr

            runOut.Replace("\r\n", "\n").Trim()
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)

    [<Fact>]
    let ``M4 authored domain and capability execute through an explicit host provider`` () =
        let root =
            Path.Combine(Path.GetTempPath(), "nyamnyam-m4-authoring-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")

        try
            let outcomeId, capabilityId, operationId, entryFunctionId =
                use session = AuthoringSession.createNew path |> unwrap "authoring session creation"

                let outcome, accepted, acceptedField, rejected, rejectedField =
                    createOutcomeType session
                let effect = createOutputEffect session
                let capability = createConsoleCapability session effect.Id
                let operation = List.exactlyOne capability.Operations
                let operationParameter = List.exactlyOne operation.Parameters

                let classifyParameter: AgentExpressionStructuralWorkflow.FunctionCreation.ParameterIntent =
                    { DisplayName = "input"
                      Type = stringType }

                let classify =
                    createFunction
                        session
                        "classify"
                        [ classifyParameter ]
                        (TypeRef.Named outcome.Id)
                        []
                        (ExpressionRecipe.Conditional(
                            ExpressionRecipe.StringContains(
                                ExpressionRecipe.ParameterSlot 0,
                                ExpressionRecipe.StringLiteral "@"
                            ),
                            ExpressionRecipe.UnionConstruct(
                                accepted.Id,
                                [ acceptedField.Id, ExpressionRecipe.StringLiteral "accepted" ]
                            ),
                            ExpressionRecipe.UnionConstruct(
                                rejected.Id,
                                [ rejectedField.Id, ExpressionRecipe.StringLiteral "rejected" ]
                            )
                        ))

                let classifyInput = List.exactlyOne classify.Parameters
                let patternField fieldId =
                    ExpressionRecipe.PatternReference
                        { ScopeDepth = 0
                          Slot = RecipePatternSlot.Field fieldId }
                let writeField fieldId =
                    ExpressionRecipe.CapabilityInvoke(
                        ExpressionRecipe.ParameterSlot 1,
                        operation.Id,
                        [ operationParameter.Id, patternField fieldId ]
                    )

                let processInput: AgentExpressionStructuralWorkflow.FunctionCreation.ParameterIntent =
                    { DisplayName = "input"
                      Type = stringType }
                let processConsole: AgentExpressionStructuralWorkflow.FunctionCreation.ParameterIntent =
                    { DisplayName = "console"
                      Type = TypeRef.Capability capability.Id }

                let process =
                    createFunction
                        session
                        "process"
                        [ processInput; processConsole ]
                        unitType
                        [ effect.Id ]
                        (ExpressionRecipe.MatchUnion(
                            ExpressionRecipe.DirectCall(
                                classify.Id,
                                [ classifyInput.Id, ExpressionRecipe.ParameterSlot 0 ]
                            ),
                            [ accepted.Id, writeField acceptedField.Id
                              rejected.Id, writeField rejectedField.Id ]
                        ))

                session.Close()
                outcome.Id, capability.Id, operation.Id, process.Id

            use reopened = AuthoringSession.openExisting path |> unwrap "authoring session reopen"
            let declarations = reopened.Declarations()
            Assert.Contains(declarations.Types, fun item -> item.Id = outcomeId)
            Assert.Contains(declarations.Capabilities, fun item -> item.Id = capabilityId)
            Assert.Contains(declarations.Functions, fun item -> item.Id = entryFunctionId)

            reopened.ProjectExecution()
            |> unwrap "execution projection after reopen"
            |> ignore

            match reopened.EmitCSharp CSharpBackendProfile.empty with
            | Error(AuthoringSessionError.BackendFailure(CSharpBackendError.MissingCapabilityBinding missingOperationId)) ->
                Assert.Equal(operationId, missingOperationId)
            | result -> failwithf "Expected explicit missing-provider failure, got %A" result

            let artifact =
                reopened.EmitCSharp(CSharpBackendProfile.systemConsoleWriteLine operationId)
                |> unwrap "C# emission with explicit provider"

            let output = executeArtifact capabilityId entryFunctionId artifact
            Assert.Equal("accepted\nrejected", output)
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
