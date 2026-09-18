namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text
open Nyrwvy.Core
open Xunit

module ViewerArtifactTests =
    let private unwrap result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "Unexpected result error: %A" error

    let private repoRoot () =
        let rec find (directory: DirectoryInfo) =
            if isNull directory then
                failwith "Could not locate repository root."
            elif File.Exists(Path.Combine(directory.FullName, "Nyrwvy.slnx")) then
                directory.FullName
            else
                find directory.Parent
        find (DirectoryInfo(AppContext.BaseDirectory))

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

    let private cliArguments tail =
        [ "run"
          "--no-build"
          "--configuration"; "Release"
          "--project"; "tools/Nyrwvy.ViewerExport/Nyrwvy.ViewerExport.fsproj"
          "--" ] @ tail

    [<Fact>]
    let ``JSON and Nyason sources export the identical focus artifact`` () =
        let fixture = MilestoneZeroGraph.create ()
        let json = SnapshotCodec.serialize fixture.Snapshot |> unwrap
        let nyason = NyasonCodec.serialize fixture.Snapshot |> unwrap
        let functionIdText = StableId.value fixture.MainFunctionId

        let fromJson =
            ViewerExport.focusFromSource ViewerSourceEncoding.Json json functionIdText
            |> unwrap
        let fromNyason =
            ViewerExport.focusFromSource ViewerSourceEncoding.Nyason nyason functionIdText
            |> unwrap

        if fromJson <> fromNyason then
            failwith "Equivalent JSON and Nyason snapshots produced different viewer artifacts."

    [<Fact>]
    let ``focus artifact filename is stable-ID derived and display-name independent`` () =
        let fixture = MilestoneZeroGraph.create ()
        let original = ViewerExport.focusFromSnapshot fixture.Snapshot fixture.ValidateFunctionId |> unwrap

        let renamedFunctions =
            fixture.Snapshot.Functions
            |> List.map (fun functionEntity ->
                if functionEntity.Id = fixture.ValidateFunctionId then
                    { functionEntity with DisplayName = "completely_different_display_name" }
                else
                    functionEntity)
        let renamedSnapshot = { fixture.Snapshot with Functions = renamedFunctions }
        let renamed = ViewerExport.focusFromSnapshot renamedSnapshot fixture.ValidateFunctionId |> unwrap

        if original.SuggestedFileName <> renamed.SuggestedFileName then
            failwith "Viewer artifact filename changed when only display metadata changed."
        if original.SuggestedFileName.Contains("validate", StringComparison.OrdinalIgnoreCase) then
            failwith "Viewer artifact filename leaked a display-name-derived token."

    [<Fact>]
    let ``invalid function ID source request returns an explicit ID error`` () =
        let fixture = MilestoneZeroGraph.create ()
        let json = SnapshotCodec.serialize fixture.Snapshot |> unwrap

        match ViewerExport.focusFromSource ViewerSourceEncoding.Json json "   " with
        | Error(ViewerExportError.InvalidFunctionId StableIdError.BlankValue) -> ()
        | result -> failwithf "Expected InvalidFunctionId BlankValue, got %A" result

    [<Fact>]
    let ``change review artifact is deterministic and transaction-ID named`` () =
        let fixture = MilestoneZeroGraph.create ()
        let transactionId = StableId.createForTesting<TransactionTag> "transaction-artifact-review"
        let request: TransactionRequest =
            { Id = transactionId
              BaseSnapshotId = fixture.Snapshot.Id
              ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "snapshot-artifact-review"
              Operations =
                [ SemanticOperation.RenameDisplayName(
                    RenameTarget.FunctionTarget fixture.ValidateFunctionId,
                    "renamed_for_artifact_review"
                  ) ] }
        let simulation =
            match Transaction.simulate fixture.Snapshot request with
            | Proposed value -> value
            | Rejected diagnostics -> failwithf "Unexpected transaction rejection: %A" diagnostics

        let first = ViewerExport.changeReview simulation |> unwrap
        let second = ViewerExport.changeReview simulation |> unwrap
        if first <> second then
            failwith "Repeated change review export was not deterministic."
        if first.Kind <> ViewerArtifactKind.ChangeReview then
            failwithf "Unexpected artifact kind: %A" first.Kind
        if first.SuggestedFileName.Contains("renamed", StringComparison.OrdinalIgnoreCase) then
            failwith "Change review filename leaked display metadata."

    [<Fact>]
    let ``viewer export CLI writes a real focus HTML artifact by stable function ID`` () =
        let fixture = MilestoneZeroGraph.create ()
        let json = SnapshotCodec.serialize fixture.Snapshot |> unwrap
        let root = repoRoot ()
        let tempRoot =
            Path.Combine(Path.GetTempPath(), "nyrwvy-viewer-export-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tempRoot) |> ignore

        try
            let snapshotPath = Path.Combine(tempRoot, "snapshot.json")
            let outputPath = Path.Combine(tempRoot, "focus.html")
            File.WriteAllText(snapshotPath, json, UTF8Encoding(false))

            let exitCode, stdout, stderr =
                runDotnet
                    root
                    (cliArguments
                        [ "focus"
                          "--input"; snapshotPath
                          "--format"; "json"
                          "--function-id"; StableId.value fixture.MainFunctionId
                          "--output"; outputPath ])

            if exitCode <> 0 then
                failwithf "Viewer export CLI failed.\nstdout:\n%s\nstderr:\n%s" stdout stderr
            if not (File.Exists outputPath) then
                failwith "Viewer export CLI did not create the requested HTML file."

            let html = File.ReadAllText(outputPath, Encoding.UTF8)
            if not (html.Contains("Function Focus", StringComparison.Ordinal)) then
                failwith "Exported HTML does not contain the Focus viewer heading."
            if not (html.Contains(StableId.value fixture.MainFunctionId, StringComparison.Ordinal)) then
                failwith "Exported HTML does not expose the selected stable function ID."
            if Path.GetFullPath(stdout.Trim()) <> Path.GetFullPath(outputPath) then
                failwithf "CLI stdout did not report the output path. stdout=%s" stdout
        finally
            if Directory.Exists tempRoot then
                Directory.Delete(tempRoot, true)

    [<Fact>]
    let ``viewer export CLI rejects display-name lookup and unknown options`` () =
        let root = repoRoot ()
        let exitCode, stdout, stderr =
            runDotnet
                root
                (cliArguments
                    [ "focus"
                      "--input"; "unused.json"
                      "--format"; "json"
                      "--function-id"; "function-main"
                      "--display-name"; "main" ])

        if exitCode = 0 then
            failwithf "CLI unexpectedly accepted display-name lookup. stdout=%s" stdout
        if not (stderr.Contains("Unsupported option '--display-name'", StringComparison.Ordinal)) then
            failwithf "CLI did not report the unknown option explicitly. stderr=%s" stderr
