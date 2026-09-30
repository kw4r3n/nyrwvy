namespace Nyrwvy.AgentEval

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Core.Tests

[<RequireQualifiedAccess>]
type VerificationFixture =
    | MilestoneZero
    | MilestoneOne

[<RequireQualifiedAccess>]
module VerificationFixture =
    let name = function
        | VerificationFixture.MilestoneZero -> "m0"
        | VerificationFixture.MilestoneOne -> "m1"

    let parse value =
        match value with
        | "m0" -> Ok VerificationFixture.MilestoneZero
        | "m1" -> Ok VerificationFixture.MilestoneOne
        | _ -> Error $"Unsupported verification fixture '{value}'. Expected m0 or m1."

[<RequireQualifiedAccess>]
type VerificationStatus =
    | Passed
    | Failed

[<RequireQualifiedAccess>]
module VerificationStatus =
    let name = function
        | VerificationStatus.Passed -> "passed"
        | VerificationStatus.Failed -> "failed"

type VerificationReport =
    { Fixture: VerificationFixture
      Status: VerificationStatus
      TransactionId: TransactionId
      BaseSnapshotId: SnapshotId
      PublishedSnapshotId: SnapshotId
      BaseSnapshotFingerprint: SnapshotFingerprint
      ProposedSnapshotFingerprint: SnapshotFingerprint
      ProposedSemanticFingerprint: SemanticFingerprint
      CurrentSnapshotFingerprint: SnapshotFingerprint option
      CurrentSemanticFingerprint: SemanticFingerprint option
      TypedSemanticSourceSnapshotId: SnapshotId option
      ExecutionSourceSnapshotId: SnapshotId option
      EvidencePath: string
      RunDirectory: string
      Error: string option
      EvidenceDurable: bool }

module private VerificationJson =
    let objectValue (pairs: (string * obj) list): obj =
        let value = Dictionary<string, obj>(StringComparer.Ordinal)
        for key, item in pairs do
            value[key] <- item
        box value

    let arrayValue (items: obj list): obj =
        items |> List.toArray |> box

    let optionalString (value: string option): obj =
        match value with
        | Some item -> box item
        | None -> null

    let serialize (value: obj): string =
        JsonSerializer.Serialize(value)

module private VerificationProcess =
    type ProcessResult =
        { ExitCode: int
          TimedOut: bool
          StandardOutput: string
          StandardError: string }

    let private timeoutMilliseconds = 120000

    let run (workingDirectory: string) (arguments: string list): Result<ProcessResult, string> =
        try
            let startInfo = ProcessStartInfo("dotnet")
            startInfo.WorkingDirectory <- workingDirectory
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] <- "1"
            startInfo.Environment["DOTNET_NOLOGO"] <- "1"

            for argument in arguments do
                startInfo.ArgumentList.Add(argument)

            match Process.Start(startInfo) with
            | null -> Error "Failed to start dotnet process."
            | childProcess ->
                use childProcess = childProcess
                let standardOutputTask = childProcess.StandardOutput.ReadToEndAsync()
                let standardErrorTask = childProcess.StandardError.ReadToEndAsync()
                let completed = childProcess.WaitForExit(timeoutMilliseconds)
                let timedOut = not completed

                if timedOut then
                    try
                        childProcess.Kill(true)
                    with
                    | _ -> ()

                    childProcess.WaitForExit()

                Ok
                    { ExitCode = childProcess.ExitCode
                      TimedOut = timedOut
                      StandardOutput = standardOutputTask.GetAwaiter().GetResult()
                      StandardError = standardErrorTask.GetAwaiter().GetResult() }
        with
        | _ -> Error "Verification process could not be started or completed."

module private VerificationArtifacts =
    let private utf8 = UTF8Encoding(false)

    let writeText path (value: string) =
        let bytes = utf8.GetBytes(value)

        use stream =
            new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough
            )

        stream.Write(bytes, 0, bytes.Length)
        stream.Flush(true)
        ArtifactPermissions.ensureFile path

    let writeAtomically path (value: string) =
        let temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N")

        try
            writeText temporaryPath value
            File.Move(temporaryPath, path, true)
            ArtifactPermissions.ensureFile path
        finally
            if File.Exists temporaryPath then
                File.Delete temporaryPath

    let normalizeOutput (value: string) =
        value.Replace("\r\n", "\n")

type private VerificationStage =
    { Name: string
      Status: string
      Error: string option
      Details: Map<string, string> }

type private VerificationFixtureSpec =
    { Profile: CSharpBackendProfile
      HarnessSource: string
      ExpectedOutput: string }

[<RequireQualifiedAccess>]
module Verification =
    open VerificationJson

    let matchesExpectedOutput expected actual =
        VerificationArtifacts.normalizeOutput expected = VerificationArtifacts.normalizeOutput actual

    let private projectFile =
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>NyrwvyVerification</AssemblyName>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
"""

    let private expectedM0Output =
        String.concat
            "\n"
            [ "valid user"
              "empty name"
              "invalid email" ]
        + "\n"

    let private expectedM1Output =
        String.concat
            "\n"
            [ "validation errors present"
              "all users valid"
              "no users"
              "2" ]
        + "\n"

    let private m0Harness (fixture: MilestoneZeroFixture) =
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
"""

    let private m1Harness (fixture: MilestoneOneFixture) =
        let rawTypeName = CSharpBackendNames.typeName fixture.RawUserInputTypeId
        let nameFieldName = CSharpBackendNames.fieldName fixture.RawNameFieldId
        let emailFieldName = CSharpBackendNames.fieldName fixture.RawEmailFieldId
        let displayNameFieldName = CSharpBackendNames.fieldName fixture.RawDisplayNameFieldId
        let adapterName = CSharpBackendNames.capabilityAdapterName fixture.MilestoneZero.ConsoleCapabilityId
        let mainFunctionName = CSharpBackendNames.functionName fixture.MainFunctionId
        let validateUsersFunctionName = CSharpBackendNames.functionName fixture.ValidateUsersFunctionId
        let countInvalidFunctionName = CSharpBackendNames.functionName fixture.CountInvalidResultsFunctionId

        $"""using System;
using Nyrwvy.Generated;

var console = new {adapterName}();
var valid = new {rawTypeName}(
    {nameFieldName}: "Alice",
    {emailFieldName}: "alice@example.test",
    {displayNameFieldName}: NyOption<NyStr>.None());
var emptyName = new {rawTypeName}(
    {nameFieldName}: "",
    {emailFieldName}: "empty@example.test",
    {displayNameFieldName}: NyOption<NyStr>.None());
var invalidEmail = new {rawTypeName}(
    {nameFieldName}: "Bob",
    {emailFieldName}: "not-an-email",
    {displayNameFieldName}: NyOption<NyStr>.None());

var mixed = NyList<{rawTypeName}>.Create(valid, emptyName, invalidEmail);
NyFunctions.{mainFunctionName}(mixed, console);

var allValid = NyList<{rawTypeName}>.Create(valid, valid);
NyFunctions.{mainFunctionName}(allValid, console);

var empty = NyList<{rawTypeName}>.Empty();
NyFunctions.{mainFunctionName}(empty, console);

var validated = NyFunctions.{validateUsersFunctionName}(mixed);
Console.WriteLine(NyFunctions.{countInvalidFunctionName}(validated));
"""

    let private fixtureSpec fixture =
        match fixture with
        | VerificationFixture.MilestoneZero ->
            let value = MilestoneZeroGraph.create ()

            { Profile = CSharpBackendProfile.systemConsoleWriteLine value.ConsoleWriteLineOperationId
              HarnessSource = m0Harness value
              ExpectedOutput = expectedM0Output }
        | VerificationFixture.MilestoneOne ->
            let value = MilestoneOneGraph.create ()

            { Profile =
                CSharpBackendProfile.systemConsoleWriteLine value.MilestoneZero.ConsoleWriteLineOperationId
              HarnessSource = m1Harness value
              ExpectedOutput = expectedM1Output }

    let private stageValue (stage: VerificationStage) =
        let details =
            stage.Details
            |> Map.toList
            |> List.map (fun (key, value) -> key, box value)
            |> objectValue

        objectValue
            [ "name", box stage.Name
              "status", box stage.Status
              "error", optionalString stage.Error
              "details", details ]

    let private receiptValue (publication: PublishedTransaction) =
        let receipt = publication.Receipt

        objectValue
            [ "transactionId", box (StableId.value receipt.TransactionId)
              "baseSnapshotId", box (StableId.value receipt.BaseSnapshotId)
              "baseSnapshotFingerprint", box (SnapshotFingerprint.value receipt.BaseSnapshotFingerprint)
              "proposedSnapshotId", box (StableId.value receipt.ProposedSnapshotId)
              "proposedSnapshotFingerprint", box (SnapshotFingerprint.value receipt.ProposedSnapshotFingerprint)
              "proposedSemanticFingerprint", box (SemanticFingerprint.value receipt.ProposedSemanticFingerprint) ]

    let private reportValue
        (report: VerificationReport)
        (publication: PublishedTransaction)
        (currentSnapshotId: SnapshotId)
        (stages: VerificationStage list)
        (artifactPaths: Map<string, string>) =
        let currentSnapshotFingerprint =
            report.CurrentSnapshotFingerprint
            |> Option.map SnapshotFingerprint.value

        let currentSemanticFingerprint =
            report.CurrentSemanticFingerprint
            |> Option.map SemanticFingerprint.value

        let typedSourceSnapshotId =
            report.TypedSemanticSourceSnapshotId
            |> Option.map StableId.value

        let executionSourceSnapshotId =
            report.ExecutionSourceSnapshotId
            |> Option.map StableId.value

        let artifacts =
            artifactPaths
            |> Map.toList
            |> List.map (fun (key, value) -> key, box value)
            |> objectValue

        objectValue
            [ "schemaVersion", box 1
              "state", box "terminal"
              "fixture", box (VerificationFixture.name report.Fixture)
              "status", box (VerificationStatus.name report.Status)
              "evidenceDurable", box report.EvidenceDurable
              "commit",
                objectValue
                    [ "status", box "committed"
                      "transactionId", box (StableId.value report.TransactionId)
                      "baseSnapshotId", box (StableId.value report.BaseSnapshotId)
                      "publishedSnapshotId", box (StableId.value report.PublishedSnapshotId) ]
              "publishedSnapshot",
                objectValue
                    [ "currentSnapshotId", box (StableId.value currentSnapshotId)
                      "publishedSnapshotId", box (StableId.value report.PublishedSnapshotId)
                      "currentSnapshotFingerprint", optionalString currentSnapshotFingerprint
                      "currentSemanticFingerprint", optionalString currentSemanticFingerprint
                      "publishedSnapshotFingerprint", box (SnapshotFingerprint.value report.ProposedSnapshotFingerprint)
                      "publishedSemanticFingerprint", box (SemanticFingerprint.value report.ProposedSemanticFingerprint)
                      "proposedSnapshotFingerprint", box (SnapshotFingerprint.value report.ProposedSnapshotFingerprint)
                      "proposedSemanticFingerprint", box (SemanticFingerprint.value report.ProposedSemanticFingerprint)
                      "typedSemanticSourceSnapshotId", optionalString typedSourceSnapshotId
                      "executionSourceSnapshotId", optionalString executionSourceSnapshotId ]
              "receipt", receiptValue publication
              "evidencePath", box report.EvidencePath
              "runDirectory", box report.RunDirectory
              "artifacts", artifacts
              "stages", stages |> List.map stageValue |> arrayValue
              "error", optionalString report.Error ]

    let run
        (fixture: VerificationFixture)
        (workspace: AgentWorkspace)
        (publication: PublishedTransaction)
        (artifactDirectory: string)
        : VerificationReport =
        let transactionText = StableId.value publication.TransactionId
        let runDirectory = Path.Combine(Path.GetFullPath(artifactDirectory), "verification-" + transactionText)
        let evidencePath = Path.Combine(runDirectory, "verification.json")
        let projectPath = Path.Combine(runDirectory, "NyrwvyVerification.csproj")
        let generatedSourcePath = Path.Combine(runDirectory, "Generated.cs")
        let harnessSourcePath = Path.Combine(runDirectory, "Program.cs")
        let expectedOutputPath = Path.Combine(runDirectory, "expected-output.txt")
        let typedSemanticIrPath = Path.Combine(runDirectory, "typed-semantic-ir.json")
        let executionIrPath = Path.Combine(runDirectory, "execution-ir.json")
        let compileStandardOutputPath = Path.Combine(runDirectory, "compile.stdout.txt")
        let compileStandardErrorPath = Path.Combine(runDirectory, "compile.stderr.txt")
        let executionStandardOutputPath = Path.Combine(runDirectory, "execution.stdout.txt")
        let executionStandardErrorPath = Path.Combine(runDirectory, "execution.stderr.txt")
        let assemblyPath = Path.Combine(runDirectory, "bin", "Release", "net10.0", "NyrwvyVerification.dll")
        let mutable stages: VerificationStage list = []
        let mutable failure: string option = None
        let mutable currentSnapshotFingerprint: SnapshotFingerprint option = None
        let mutable currentSemanticFingerprint: SemanticFingerprint option = None
        let mutable typedSemanticSourceSnapshotId: SnapshotId option = None
        let mutable executionSourceSnapshotId: SnapshotId option = None
        let mutable typedSemanticProgram: TypedSemanticProgram option = None
        let mutable evidenceDurable = false

        let addStage name status error details =
            stages <-
                stages
                @ [ { Name = name
                      Status = status
                      Error = error
                      Details = Map.ofList details } ]

        let failStage name message details =
            addStage name "failed" (Some message) details
            if Option.isNone failure then
                failure <- Some message

        let skipStage name =
            if stages |> List.exists (fun stage -> stage.Name = name) |> not then
                addStage name "notRun" None []

        let fingerprintText result =
            match result with
            | Ok value -> SnapshotFingerprint.value value
            | Error error -> "error:" + sprintf "%A" error

        let semanticFingerprintText result =
            match result with
            | Ok value -> SemanticFingerprint.value value
            | Error error -> "error:" + sprintf "%A" error

        let currentSnapshotId = AgentWorkspace.currentSnapshotId workspace
        let receipt = publication.Receipt
        let mutable fixtureSpecValue: VerificationFixtureSpec option = None
        let artifactPaths =
            [ "project", projectPath
              "generatedSource", generatedSourcePath
              "harnessSource", harnessSourcePath
              "expectedOutput", expectedOutputPath
              "typedSemanticIr", typedSemanticIrPath
              "executionIr", executionIrPath
              "compileStdout", compileStandardOutputPath
              "compileStderr", compileStandardErrorPath
              "executionStdout", executionStandardOutputPath
              "executionStderr", executionStandardErrorPath
              "assembly", assemblyPath ]
            |> Map.ofList

        let inProgressValue () =
            objectValue
                [ "schemaVersion", box 1
                  "state", box "inProgress"
                  "fixture", box (VerificationFixture.name fixture)
                  "transactionId", box (StableId.value receipt.TransactionId)
                  "baseSnapshotId", box (StableId.value receipt.BaseSnapshotId)
                  "publishedSnapshotId", box (StableId.value publication.PublishedSnapshotId)
                  "baseSnapshotFingerprint", box (SnapshotFingerprint.value receipt.BaseSnapshotFingerprint)
                  "proposedSnapshotFingerprint", box (SnapshotFingerprint.value receipt.ProposedSnapshotFingerprint)
                  "proposedSemanticFingerprint", box (SemanticFingerprint.value receipt.ProposedSemanticFingerprint) ]

        try
            ArtifactPermissions.ensureDirectory runDirectory
            ArtifactPermissions.restrictTree runDirectory
            inProgressValue () |> serialize |> VerificationArtifacts.writeAtomically evidencePath
            ArtifactPermissions.restrictTree runDirectory
            addStage "artifactPermissions" "passed" None [ "runDirectory", runDirectory ]

            let snapshotFingerprintResult = AgentWorkspace.currentSnapshotFingerprint workspace
            let semanticFingerprintResult = AgentWorkspace.currentSemanticFingerprint workspace
            currentSnapshotFingerprint <- Result.toOption snapshotFingerprintResult
            currentSemanticFingerprint <- Result.toOption semanticFingerprintResult

            let identityMismatches =
                [ if receipt.TransactionId <> publication.TransactionId then
                      yield "Receipt transaction ID differs from publication."
                  if receipt.BaseSnapshotId <> publication.BaseSnapshotId then
                      yield "Receipt base snapshot ID differs from publication."
                  if receipt.ProposedSnapshotId <> publication.PublishedSnapshotId then
                      yield "Receipt proposed snapshot ID differs from published snapshot ID."
                  if currentSnapshotId <> publication.PublishedSnapshotId then
                      yield "Workspace current snapshot ID differs from published snapshot ID."
                  match snapshotFingerprintResult with
                  | Ok value when value = receipt.ProposedSnapshotFingerprint -> ()
                  | Ok _ -> yield "Workspace snapshot fingerprint differs from the reviewed receipt."
                  | Error _ -> yield "Workspace snapshot fingerprint could not be computed."
                  match semanticFingerprintResult with
                  | Ok value when value = receipt.ProposedSemanticFingerprint -> ()
                  | Ok _ -> yield "Workspace semantic fingerprint differs from the reviewed receipt."
                  | Error _ -> yield "Workspace semantic fingerprint could not be computed." ]

            let identityDetails =
                [ "currentSnapshotId", StableId.value currentSnapshotId
                  "publishedSnapshotId", StableId.value publication.PublishedSnapshotId
                  "receiptTransactionId", StableId.value receipt.TransactionId
                  "receiptBaseSnapshotId", StableId.value receipt.BaseSnapshotId
                  "receiptProposedSnapshotId", StableId.value receipt.ProposedSnapshotId
                  "currentSnapshotFingerprint", fingerprintText snapshotFingerprintResult
                  "receiptProposedSnapshotFingerprint", SnapshotFingerprint.value receipt.ProposedSnapshotFingerprint
                  "currentSemanticFingerprint", semanticFingerprintText semanticFingerprintResult
                  "receiptProposedSemanticFingerprint", SemanticFingerprint.value receipt.ProposedSemanticFingerprint ]

            match identityMismatches with
            | [] -> addStage "snapshotIdentity" "passed" None identityDetails
            | mismatches -> failStage "snapshotIdentity" (String.concat " " mismatches) identityDetails

            if Option.isNone failure then
                let spec = fixtureSpec fixture
                fixtureSpecValue <- Some spec
                VerificationArtifacts.writeText projectPath projectFile
                VerificationArtifacts.writeText harnessSourcePath spec.HarnessSource
                VerificationArtifacts.writeText expectedOutputPath spec.ExpectedOutput
                addStage
                    "fixtureHarness"
                    "passed"
                    None
                    [ "fixture", VerificationFixture.name fixture
                      "harnessSource", harnessSourcePath
                      "expectedOutput", expectedOutputPath ]

            if Option.isNone failure then
                match AgentExecution.projectTypedSemanticIr workspace with
                | Error error ->
                    failStage "typedSemanticIr" (sprintf "%A" error) []
                | Ok typed ->
                    typedSemanticProgram <- Some typed
                    typedSemanticSourceSnapshotId <- Some typed.SourceSnapshotId

                    if typed.SourceSnapshotId <> publication.PublishedSnapshotId then
                        failStage
                            "typedSemanticIr"
                            "Typed Semantic IR is not sourced from the published snapshot."
                            [ "sourceSnapshotId", StableId.value typed.SourceSnapshotId
                              "publishedSnapshotId", StableId.value publication.PublishedSnapshotId ]
                    else
                        try
                            TypedSemanticIrCodec.serialize typed
                            |> VerificationArtifacts.writeText typedSemanticIrPath
                            addStage
                                "typedSemanticIr"
                                "passed"
                                None
                                 [ "sourceSnapshotId", StableId.value typed.SourceSnapshotId
                                   "artifact", typedSemanticIrPath ]
                         with
                         | _ -> failStage "typedSemanticIr" "Typed Semantic IR evidence could not be persisted." []

            if Option.isNone failure then
                match typedSemanticProgram with
                | None -> failStage "executionIr" "Typed Semantic IR projection was not available." []
                | Some typed ->
                    match ExecutionIr.lower typed with
                    | Error error -> failStage "executionIr" (sprintf "%A" error) []
                    | Ok execution ->
                        executionSourceSnapshotId <- Some execution.SourceSnapshotId

                        if execution.SourceSnapshotId <> publication.PublishedSnapshotId then
                            failStage
                                "executionIr"
                                "Execution IR is not sourced from the published snapshot."
                                [ "sourceSnapshotId", StableId.value execution.SourceSnapshotId
                                  "publishedSnapshotId", StableId.value publication.PublishedSnapshotId ]
                        else
                            try
                                ExecutionIrCodec.serialize execution
                                |> VerificationArtifacts.writeText executionIrPath
                                addStage
                                    "executionIr"
                                    "passed"
                                    None
                                 [ "sourceSnapshotId", StableId.value execution.SourceSnapshotId
                                   "artifact", executionIrPath ]
                             with
                             | _ -> failStage "executionIr" "Execution IR evidence could not be persisted." []

                            if Option.isNone failure then
                                match fixtureSpecValue with
                                | None -> failStage "csharpEmission" "Fixture harness configuration was not prepared." []
                                | Some spec ->
                                    match CSharpBackend.emit execution spec.Profile with
                                    | Error error -> failStage "csharpEmission" (sprintf "%A" error) []
                                    | Ok artifact ->
                                        try
                                            VerificationArtifacts.writeText generatedSourcePath artifact.Source
                                            addStage
                                                "csharpEmission"
                                                "passed"
                                                None
                                                 [ "generatedSource", generatedSourcePath ]
                                         with
                                         | _ -> failStage "csharpEmission" "C# source evidence could not be persisted." []

            if Option.isNone failure then
                 match VerificationProcess.run runDirectory [ "build"; Path.GetFileName(projectPath); "--configuration"; "Release"; "--nologo"; "--verbosity"; "quiet" ] with
                 | Error error -> failStage "compile" error []
                 | Ok result ->
                     try
                         ArtifactPermissions.restrictTree runDirectory
                         VerificationArtifacts.writeText compileStandardOutputPath result.StandardOutput
                         VerificationArtifacts.writeText compileStandardErrorPath result.StandardError

                         let details =
                             [ "exitCode", string result.ExitCode
                               "timedOut", string result.TimedOut
                               "stdout", compileStandardOutputPath
                               "stderr", compileStandardErrorPath ]

                         if result.TimedOut then
                             failStage "compile" "Generated C# fixture harness compilation timed out." details
                         elif result.ExitCode = 0 then
                             addStage "compile" "passed" None details
                         else
                             failStage "compile" "Generated C# fixture harness did not compile." details
                     with
                     | _ -> failStage "artifactPermissions" "Per-run artifact permissions could not be enforced." []

            if Option.isNone failure then
                match fixtureSpecValue with
                | None -> failStage "execute" "Fixture harness configuration was not prepared." []
                | Some spec ->
                     match VerificationProcess.run runDirectory [ assemblyPath ] with
                     | Error error -> failStage "execute" error []
                     | Ok result ->
                         ArtifactPermissions.restrictTree runDirectory
                         VerificationArtifacts.writeText executionStandardOutputPath result.StandardOutput
                         VerificationArtifacts.writeText executionStandardErrorPath result.StandardError
                         let actualOutput = VerificationArtifacts.normalizeOutput result.StandardOutput

                         let details =
                             [ "exitCode", string result.ExitCode
                               "timedOut", string result.TimedOut
                               "stdout", executionStandardOutputPath
                               "stderr", executionStandardErrorPath
                               "actualOutput", actualOutput
                               "expectedOutput", spec.ExpectedOutput ]

                         if result.TimedOut then
                             failStage "execute" "Generated C# fixture harness execution timed out." details
                         elif result.ExitCode <> 0 then
                             failStage "execute" "Generated C# fixture harness exited unsuccessfully." details
                         elif not (matchesExpectedOutput spec.ExpectedOutput result.StandardOutput) then
                             failStage "execute" "Generated C# fixture harness output differed from the fixture oracle." details
                         else
                             addStage "execute" "passed" None details
         with
         | _ -> failStage "verification" "Unexpected verification failure." []

        [ "snapshotIdentity"
          "artifactPermissions"
          "fixtureHarness"
          "typedSemanticIr"
          "executionIr"
          "csharpEmission"
          "compile"
          "execute" ]
        |> List.iter skipStage

        let status =
            match failure with
            | None -> VerificationStatus.Passed
            | Some _ -> VerificationStatus.Failed

        let report =
            { Fixture = fixture
              Status = status
              TransactionId = publication.TransactionId
              BaseSnapshotId = publication.BaseSnapshotId
              PublishedSnapshotId = publication.PublishedSnapshotId
              BaseSnapshotFingerprint = receipt.BaseSnapshotFingerprint
              ProposedSnapshotFingerprint = receipt.ProposedSnapshotFingerprint
              ProposedSemanticFingerprint = receipt.ProposedSemanticFingerprint
              CurrentSnapshotFingerprint = currentSnapshotFingerprint
              CurrentSemanticFingerprint = currentSemanticFingerprint
              TypedSemanticSourceSnapshotId = typedSemanticSourceSnapshotId
              ExecutionSourceSnapshotId = executionSourceSnapshotId
              EvidencePath = evidencePath
              RunDirectory = runDirectory
              Error = failure
              EvidenceDurable = evidenceDurable }

        try
            ArtifactPermissions.restrictTree runDirectory
            reportValue
                { report with EvidenceDurable = true }
                publication
                currentSnapshotId
                stages
                artifactPaths
            |> serialize
            |> VerificationArtifacts.writeAtomically evidencePath
            evidenceDurable <- true
            { report with EvidenceDurable = true }
        with
        | _ ->
            { report with
                Status = VerificationStatus.Failed
                EvidenceDurable = false
                Error =
                    Some(
                        match report.Error with
                        | Some existing -> existing + " Evidence persistence failed."
                        | None -> "Evidence persistence failed."
                    ) }
