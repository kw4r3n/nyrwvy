namespace Nyrwvy.AgentEval.Structural

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.AgentEval

[<RequireQualifiedAccess>]
type StructuralVerificationStatus =
    | Passed
    | Failed

[<RequireQualifiedAccess>]
module StructuralVerificationStatus =
    let name = function
        | StructuralVerificationStatus.Passed -> "passed"
        | StructuralVerificationStatus.Failed -> "failed"

type StructuralVerificationReport =
    { TaskId: string
      FixtureName: string
      Status: StructuralVerificationStatus
      TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      PublishedSnapshotId: SnapshotId
      EvidencePath: string
      ActualOutput: string option
      ExpectedOutput: string option
      Error: string option
      EvidenceDurable: bool }

module private StructuralVerificationArtifacts =
    let normalizeOutput (value: string) = value.Replace("\r\n", "\n").TrimEnd('\n', '\r')

[<RequireQualifiedAccess>]
module StructuralVerification =
    open ArtifactJson

    let private projectFile =
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>NyrwvyStructuralVerification</AssemblyName>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
"""

    let private harnessSource (fixture: StructuralHarnessFixtureBinding) =
        let callerName = CSharpBackendNames.functionName fixture.CallerFunctionId
        $"""using System;
using System.Globalization;
using Nyrwvy.Generated;
Console.WriteLine(NyFunctions.{callerName}().ToString("R", CultureInfo.InvariantCulture));
"""

    let private expectedOutput taskId =
        if taskId = "M2-E1" then Some "3.75" else None

    let private reportValue
        (report: StructuralVerificationReport)
        (publication: PublishedStructuralTransaction)
        (currentSnapshotFingerprint: SnapshotFingerprint option)
        (currentSemanticFingerprint: SemanticFingerprint option)
        (typedSource: SnapshotId option)
        (executionSource: SnapshotId option)
        (compileExitCode: int option)
        (executionExitCode: int option) =
        let optionalSnapshotId = function
            | Some value -> box (StableId.value value)
            | None -> null
        let optionalSnapshotFingerprint = function
            | Some value -> box (SnapshotFingerprint.value value)
            | None -> null
        let optionalSemanticFingerprint = function
            | Some value -> box (SemanticFingerprint.value value)
            | None -> null
        let optionalInt = function
            | Some value -> box value
            | None -> null

        objectValue
            [ "schemaVersion", box 1
              "state", box (if report.EvidenceDurable then "terminal" else "inProgress")
              "taskId", box report.TaskId
              "fixture", box report.FixtureName
              "status", box (StructuralVerificationStatus.name report.Status)
              "evidenceDurable", box report.EvidenceDurable
              "transactionId", box (StableId.value report.TransactionId)
              "baseSnapshotId", box (StableId.value report.BaseSnapshotId)
              "publishedSnapshotId", box (StableId.value report.PublishedSnapshotId)
              "receipt",
                objectValue
                    [ "baseSnapshotFingerprint", box (SnapshotFingerprint.value publication.Receipt.BaseSnapshotFingerprint)
                      "proposedSnapshotFingerprint", box (SnapshotFingerprint.value publication.Receipt.ProposedSnapshotFingerprint)
                      "proposedSemanticFingerprint", box (SemanticFingerprint.value publication.Receipt.ProposedSemanticFingerprint) ]
              "currentSnapshotFingerprint", optionalSnapshotFingerprint currentSnapshotFingerprint
              "currentSemanticFingerprint", optionalSemanticFingerprint currentSemanticFingerprint
              "typedSemanticSourceSnapshotId", optionalSnapshotId typedSource
              "executionSourceSnapshotId", optionalSnapshotId executionSource
              "compileExitCode", optionalInt compileExitCode
              "executionExitCode", optionalInt executionExitCode
              "actualOutput", optionalString report.ActualOutput
              "expectedOutput", optionalString report.ExpectedOutput
              "error", optionalString report.Error ]

    let run
        (fixtureName: string)
        (taskId: string)
        (workspace: AgentWorkspace)
        (publication: PublishedStructuralTransaction)
        (artifactDirectory: string)
        : StructuralVerificationReport =
        let transactionText = StableId.value publication.TransactionId
        let runDirectory = Path.Combine(Path.GetFullPath(artifactDirectory), "verification-" + transactionText)
        let evidencePath = Path.Combine(runDirectory, "verification.json")
        let projectPath = Path.Combine(runDirectory, "NyrwvyStructuralVerification.csproj")
        let generatedSourcePath = Path.Combine(runDirectory, "Generated.cs")
        let harnessPath = Path.Combine(runDirectory, "Program.cs")
        let typedPath = Path.Combine(runDirectory, "typed-semantic-ir.json")
        let executionPath = Path.Combine(runDirectory, "execution-ir.json")
        let compileStdoutPath = Path.Combine(runDirectory, "compile.stdout.txt")
        let compileStderrPath = Path.Combine(runDirectory, "compile.stderr.txt")
        let runStdoutPath = Path.Combine(runDirectory, "execution.stdout.txt")
        let runStderrPath = Path.Combine(runDirectory, "execution.stderr.txt")
        let assemblyPath = Path.Combine(runDirectory, "bin", "Release", "net10.0", "NyrwvyStructuralVerification.dll")

        ArtifactPermissions.ensureDirectory runDirectory
        ArtifactPermissions.restrictTree runDirectory

        let mutable failure: string option = None
        let mutable currentSnapshotFingerprint: SnapshotFingerprint option = None
        let mutable currentSemanticFingerprint: SemanticFingerprint option = None
        let mutable typedSource: SnapshotId option = None
        let mutable executionSource: SnapshotId option = None
        let mutable compileExitCode: int option = None
        let mutable executionExitCode: int option = None
        let mutable actualOutput: string option = None
        let expected = expectedOutput taskId

        let fail message = if Option.isNone failure then failure <- Some message

        let fixture =
            match FrozenM2StructuralCorpus.fixtureByName fixtureName with
            | Ok value -> Some value
            | Error _ ->
                fail "Unknown M2 structural verification fixture."
                None

        match AgentWorkspace.currentSnapshotFingerprint workspace with
        | Ok value -> currentSnapshotFingerprint <- Some value
        | Error _ -> fail "Current snapshot fingerprint could not be computed."

        match AgentWorkspace.currentSemanticFingerprint workspace with
        | Ok value -> currentSemanticFingerprint <- Some value
        | Error _ -> fail "Current semantic fingerprint could not be computed."

        if AgentWorkspace.currentSnapshotId workspace <> publication.PublishedSnapshotId then
            fail "Workspace current snapshot differs from the published snapshot."
        if publication.Receipt.ProposedSnapshotId <> publication.PublishedSnapshotId then
            fail "Reviewed proposed snapshot differs from the published snapshot."
        match currentSnapshotFingerprint with
        | Some value when value = publication.Receipt.ProposedSnapshotFingerprint -> ()
        | Some _ -> fail "Current snapshot fingerprint differs from reviewed receipt."
        | None -> ()
        match currentSemanticFingerprint with
        | Some value when value = publication.Receipt.ProposedSemanticFingerprint -> ()
        | Some _ -> fail "Current semantic fingerprint differs from reviewed receipt."
        | None -> ()

        let mutable typedProgram: TypedSemanticProgram option = None
        if Option.isNone failure then
            match AgentExecution.projectTypedSemanticIr workspace with
            | Error _ -> fail "Typed Semantic IR projection failed."
            | Ok typed ->
                typedSource <- Some typed.SourceSnapshotId
                if typed.SourceSnapshotId <> publication.PublishedSnapshotId then
                    fail "Typed Semantic IR is not sourced from the published snapshot."
                else
                    typedProgram <- Some typed
                    TypedSemanticIrSerializer.serialize typed |> ArtifactFiles.writeText typedPath FileMode.Create

        let mutable executionProgram: ExecutionProgram option = None
        if Option.isNone failure then
            match typedProgram with
            | None -> fail "Typed Semantic IR was unavailable."
            | Some typed ->
                match ExecutionIr.lower typed with
                | Error _ -> fail "Execution IR lowering failed."
                | Ok execution ->
                    executionSource <- Some execution.SourceSnapshotId
                    if execution.SourceSnapshotId <> publication.PublishedSnapshotId then
                        fail "Execution IR is not sourced from the published snapshot."
                    else
                        executionProgram <- Some execution
                        ExecutionIrSerializer.serialize execution |> ArtifactFiles.writeText executionPath FileMode.Create

        if Option.isNone failure then
            match fixture, executionProgram with
            | Some fixture, Some execution ->
                match CSharpBackend.emit execution CSharpBackendProfile.empty with
                | Error _ -> fail "C# emission failed."
                | Ok artifact ->
                    ArtifactFiles.writeText projectPath FileMode.Create projectFile
                    ArtifactFiles.writeText generatedSourcePath FileMode.Create artifact.Source
                    ArtifactFiles.writeText harnessPath FileMode.Create (harnessSource fixture)
            | _ -> fail "Verification fixture or Execution IR was unavailable."

        if Option.isNone failure then
            match DotnetProcess.run runDirectory [ "build"; Path.GetFileName(projectPath); "--configuration"; "Release"; "--nologo"; "--verbosity"; "quiet" ] with
            | Error _ -> fail "Generated C# verification build could not be started."
            | Ok result ->
                compileExitCode <- Some result.ExitCode
                ArtifactFiles.writeText compileStdoutPath FileMode.Create result.StandardOutput
                ArtifactFiles.writeText compileStderrPath FileMode.Create result.StandardError
                if result.TimedOut then fail "Generated C# verification build timed out."
                elif result.ExitCode <> 0 then fail "Generated C# verification build failed."

        if Option.isNone failure then
            match DotnetProcess.run runDirectory [ assemblyPath ] with
            | Error _ -> fail "Generated C# verification execution could not be started."
            | Ok result ->
                executionExitCode <- Some result.ExitCode
                ArtifactFiles.writeText runStdoutPath FileMode.Create result.StandardOutput
                ArtifactFiles.writeText runStderrPath FileMode.Create result.StandardError
                let normalized = StructuralVerificationArtifacts.normalizeOutput result.StandardOutput
                actualOutput <- Some normalized
                if result.TimedOut then fail "Generated C# verification execution timed out."
                elif result.ExitCode <> 0 then fail "Generated C# verification execution failed."
                else
                    match expected with
                    | Some expectedValue when normalized <> expectedValue ->
                        fail "Generated C# behavior did not match the frozen task oracle."
                    | _ -> ()

        ArtifactPermissions.restrictTree runDirectory

        let provisional =
            { TaskId = taskId
              FixtureName = fixtureName
              Status = if Option.isNone failure then StructuralVerificationStatus.Passed else StructuralVerificationStatus.Failed
              TransactionId = publication.TransactionId
              BaseSnapshotId = publication.BaseSnapshotId
              PublishedSnapshotId = publication.PublishedSnapshotId
              EvidencePath = evidencePath
              ActualOutput = actualOutput
              ExpectedOutput = expected
              Error = failure
              EvidenceDurable = false }

        reportValue provisional publication currentSnapshotFingerprint currentSemanticFingerprint typedSource executionSource compileExitCode executionExitCode
        |> ArtifactJson.serialize
        |> ArtifactFiles.writeAtomically evidencePath
        ArtifactPermissions.restrictTree runDirectory

        let finalReport = { provisional with EvidenceDurable = true }
        reportValue finalReport publication currentSnapshotFingerprint currentSemanticFingerprint typedSource executionSource compileExitCode executionExitCode
        |> ArtifactJson.serialize
        |> ArtifactFiles.writeAtomically evidencePath
        ArtifactPermissions.restrictTree runDirectory
        finalReport
