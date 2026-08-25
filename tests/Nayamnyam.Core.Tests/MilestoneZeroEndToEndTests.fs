namespace Nayamnyam.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text
open Nayamnyam.Core
open Xunit

module MilestoneZeroEndToEndTests =
    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s failed: %A" label error

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

    let private buildHarness (fixture: MilestoneZeroFixture): string =
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

    let private lowerAndEmit
        (fixture: MilestoneZeroFixture)
        (snapshot: Snapshot)
        : CSharpArtifact =
        let typed = TypedSemanticIr.project snapshot |> unwrap "Typed Semantic IR projection"
        let execution = ExecutionIr.lower typed |> unwrap "Execution IR lowering"
        let profile = CSharpBackendProfile.systemConsoleWriteLine fixture.ConsoleWriteLineOperationId
        CSharpBackend.emit execution profile |> unwrap "C# backend emission"

    let private executeArtifact
        (fixture: MilestoneZeroFixture)
        (artifact: CSharpArtifact)
        : string =
        let tempRoot =
            Path.Combine(Path.GetTempPath(), "nayamnyam-m0-e2e-" + Guid.NewGuid().ToString("N"))
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
                    "Generated C# build failed.\nstdout:\n%s\nstderr:\n%s\nsource:\n%s"
                    buildOut
                    buildErr
                    artifact.Source

            let assemblyPath = Path.Combine(tempRoot, "bin", "Release", "net10.0", "Generated.dll")
            let runExit, runOut, runErr = runDotnet tempRoot [ assemblyPath ]
            if runExit <> 0 then
                failwithf "Generated C# execution failed.\nstdout:\n%s\nstderr:\n%s" runOut runErr

            runOut.Replace("\r\n", "\n").Trim()
        finally
            if Directory.Exists tempRoot then
                Directory.Delete(tempRoot, true)

    let private expectedOutput = "valid user\nempty name\ninvalid email"

    [<Fact>]
    let ``Milestone 0 complete lifecycle is reproducible end to end`` () =
        // 1-3. Construct without parsing source, persist/reload, and establish readiness.
        let fixture = MilestoneZeroGraph.create ()
        let initialJson = SnapshotCodec.serialize fixture.Snapshot |> unwrap "initial snapshot serialization"
        let reloaded = SnapshotCodec.deserialize initialJson |> unwrap "initial snapshot reload"
        if reloaded <> fixture.Snapshot then
            failwith "Snapshot persistence lost semantic identity or structure."

        match SnapshotReadiness.evaluate reloaded with
        | Ok SemanticReadiness.Complete -> ()
        | readiness -> failwithf "Initial snapshot is not semantically complete: %A" readiness

        let workspace = SemanticWorkspace.create reloaded |> unwrap "workspace creation"

        // 4. Focus projection must expose the selected stable function and its structure.
        let initialFocus = SemanticQuery.focusFunction reloaded fixture.ValidateFunctionId |> unwrap "initial focus query"
        if initialFocus.Function.Id <> fixture.ValidateFunctionId then
            failwith "Focus query changed validate_user semantic identity."
        let initialFocusArtifact = ViewerArtifact.ofFocus initialFocus
        if not (initialFocusArtifact.Content.Contains(StableId.value fixture.ValidateFunctionId, StringComparison.Ordinal)) then
            failwith "Focus artifact does not expose the selected stable function identity."

        // 5. Lower, generate, compile, and execute the initial program.
        let initialArtifact = lowerAndEmit fixture reloaded
        let initialOutput = executeArtifact fixture initialArtifact
        if initialOutput <> expectedOutput then
            failwithf "Unexpected initial output. Expected:\n%s\nActual:\n%s" expectedOutput initialOutput

        // 6-7. Preview and commit rename, preserving stable identity and behavior.
        let renameRequest: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-e2e-rename"
              BaseSnapshotId = (SemanticWorkspace.currentSnapshot workspace).Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-e2e-renamed"
              Operations =
                [ SemanticOperation.RenameDisplayName(
                    RenameTarget.FunctionTarget fixture.ValidateFunctionId,
                    "validate_profile"
                  ) ] }

        let renamePreview =
            match SemanticWorkspace.simulate workspace renameRequest with
            | WorkspaceSimulationResult.Preview preview -> preview
            | WorkspaceSimulationResult.Rejected diagnostics ->
                failwithf "Rename preview was rejected: %A" diagnostics

        if renamePreview.Readiness <> TransactionReadiness.SemanticallyReady then
            failwithf "Rename preview readiness changed unexpectedly: %A" renamePreview.Readiness
        match renamePreview.Simulation.Diff.Renames with
        | [ FunctionRenamed(id, "validate_user", "validate_profile") ] when id = fixture.ValidateFunctionId -> ()
        | diff -> failwithf "Rename preview did not preserve one stable function identity: %A" diff

        let renamedWorkspace =
            match SemanticWorkspace.commit workspace renameRequest with
            | WorkspaceCommitResult.Committed(nextWorkspace, publication) ->
                if publication.Diff <> renamePreview.Simulation.Diff then
                    failwith "Committed rename diff does not match preview."
                nextWorkspace
            | WorkspaceCommitResult.Rejected diagnostics ->
                failwithf "Rename commit was rejected: %A" diagnostics

        let renamedSnapshot = SemanticWorkspace.currentSnapshot renamedWorkspace
        let renamedFunction =
            renamedSnapshot.Functions
            |> List.find (fun functionEntity -> functionEntity.Id = fixture.ValidateFunctionId)
        if renamedFunction.DisplayName <> "validate_profile" then
            failwithf "Rename did not update display metadata: %s" renamedFunction.DisplayName

        let renamedArtifact = lowerAndEmit fixture renamedSnapshot
        let renamedOutput = executeArtifact fixture renamedArtifact
        if renamedOutput <> initialOutput then
            failwithf "Rename changed observable behavior. Before:\n%s\nAfter:\n%s" initialOutput renamedOutput

        // 8-10. Preview signature evolution with an explicit missing-argument obligation,
        // render its Change Review, reject it by default, then explicitly publish incomplete state.
        let newParameterId = StableId.createForTesting<ParameterTag> "parameter-e2e-policy"
        let obligationId = StableId.createForTesting<ObligationTag> "obligation-e2e-policy"
        let addParameterRequest: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-e2e-add-policy"
              BaseSnapshotId = renamedSnapshot.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-e2e-incomplete"
              Operations =
                [ SemanticOperation.AddFunctionParameter
                    { TargetFunctionId = fixture.ValidateFunctionId
                      NewParameterId = newParameterId
                      NewParameterDisplayName = "policy"
                      NewParameterType = TypeRef.Named fixture.PolicyTypeId
                      CallSitePlans = [ LeaveMissing(fixture.MainValidateCallNodeId, obligationId) ] } ] }

        let incompletePreview =
            match SemanticWorkspace.simulate renamedWorkspace addParameterRequest with
            | WorkspaceSimulationResult.Preview preview -> preview
            | WorkspaceSimulationResult.Rejected diagnostics ->
                failwithf "Incomplete signature preview was rejected: %A" diagnostics

        if incompletePreview.Readiness <> TransactionReadiness.Incomplete [ obligationId ] then
            failwithf "Expected one blocking obligation, got %A" incompletePreview.Readiness

        let changeReview = ChangeReviewQuery.project incompletePreview.Simulation |> unwrap "Change Review projection"
        match changeReview.Readiness with
        | ChangeReviewReadiness.Incomplete ids when ids = [ obligationId ] -> ()
        | readiness -> failwithf "Change Review lost blocking obligation readiness: %A" readiness
        let affectedFunctions = Set.ofList changeReview.AffectedFunctionIds
        let expectedAffectedFunctions = Set.ofList [ fixture.ValidateFunctionId; fixture.MainFunctionId ]
        if affectedFunctions <> expectedAffectedFunctions then
            failwithf "Unexpected affected function slice: %A" changeReview.AffectedFunctionIds

        let reviewArtifact = ViewerArtifact.ofChangeReview changeReview
        if not (reviewArtifact.Content.Contains(StableId.value obligationId, StringComparison.Ordinal)) then
            failwith "Change Review artifact does not expose the blocking obligation identity."

        match SemanticWorkspace.commit renamedWorkspace addParameterRequest with
        | WorkspaceCommitResult.Rejected [ diagnostic ] when diagnostic.Code = "BlockingObligations" -> ()
        | result -> failwithf "Default commit should reject blocking obligations, got %A" result

        let incompleteWorkspace =
            match
                SemanticWorkspace.commitWithPolicy
                    renamedWorkspace
                    addParameterRequest
                    CommitPolicy.AllowBlockingObligations
            with
            | WorkspaceCommitResult.Committed(nextWorkspace, publication) ->
                if publication.Readiness <> TransactionReadiness.Incomplete [ obligationId ] then
                    failwithf "Published incomplete readiness changed: %A" publication.Readiness
                nextWorkspace
            | WorkspaceCommitResult.Rejected diagnostics ->
                failwithf "Explicit incomplete commit was rejected: %A" diagnostics

        // 11. Lowering must remain blocked while the obligation is open.
        let incompleteSnapshot = SemanticWorkspace.currentSnapshot incompleteWorkspace
        match TypedSemanticIr.project incompleteSnapshot with
        | Error(TypedSemanticIrError.IncompleteSnapshot ids) when ids = [ obligationId ] -> ()
        | result -> failwithf "Expected lowering to be blocked by the obligation, got %A" result

        // 12-14. Resolve through a semantic operation, restore readiness, then compile/run again.
        let resolveRequest: TransactionRequest =
            { Id = StableId.createForTesting<TransactionTag> "transaction-e2e-resolve-policy"
              BaseSnapshotId = incompleteSnapshot.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-e2e-resolved"
              Operations =
                [ SemanticOperation.ResolveMissingArgument
                    { TargetObligationId = obligationId
                      ResolutionValueNodeId = fixture.MainPolicyNodeId } ] }

        let resolvedWorkspace =
            match SemanticWorkspace.commit incompleteWorkspace resolveRequest with
            | WorkspaceCommitResult.Committed(nextWorkspace, publication) ->
                if publication.Readiness <> TransactionReadiness.SemanticallyReady then
                    failwithf "Resolution did not restore semantic readiness: %A" publication.Readiness
                nextWorkspace
            | WorkspaceCommitResult.Rejected diagnostics ->
                failwithf "Obligation resolution commit was rejected: %A" diagnostics

        let resolvedSnapshot = SemanticWorkspace.currentSnapshot resolvedWorkspace
        match SnapshotReadiness.evaluate resolvedSnapshot with
        | Ok SemanticReadiness.Complete -> ()
        | readiness -> failwithf "Resolved snapshot is not semantically complete: %A" readiness

        let resolvedArtifact = lowerAndEmit fixture resolvedSnapshot
        let resolvedOutput = executeArtifact fixture resolvedArtifact
        if resolvedOutput <> expectedOutput then
            failwithf "Unexpected resolved output. Expected:\n%s\nActual:\n%s" expectedOutput resolvedOutput

        // 15. Reproduce canonical graph and generated-source artifacts deterministically.
        let canonicalFirst = SnapshotCodec.serialize resolvedSnapshot |> unwrap "resolved snapshot serialization"
        let roundTripped = SnapshotCodec.deserialize canonicalFirst |> unwrap "resolved snapshot reload"
        let canonicalSecond = SnapshotCodec.serialize roundTripped |> unwrap "resolved snapshot reserialization"
        if canonicalFirst <> canonicalSecond then
            failwith "Canonical snapshot output changed after round-trip."

        let repeatedArtifact = lowerAndEmit fixture roundTripped
        if resolvedArtifact.Source <> repeatedArtifact.Source then
            failwith "Generated C# source is not reproducible for the identical resolved semantic snapshot."
