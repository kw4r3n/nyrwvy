namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
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
    let ``display-name source request resolves exactly one function and keeps the stable-ID artifact`` () =
        let fixture = MilestoneZeroGraph.create ()
        let json = SnapshotCodec.serialize fixture.Snapshot |> unwrap

        let byName = ViewerExport.focusFromSourceByName ViewerSourceEncoding.Json json "validate_user" |> unwrap
        let byId = ViewerExport.focusFromSnapshot fixture.Snapshot fixture.ValidateFunctionId |> unwrap
        Assert.Equal(byId, byName)

        match ViewerExport.focusFromSourceByName ViewerSourceEncoding.Json json "missing" with
        | Error(ViewerExportError.UnknownFunctionName "missing") -> ()
        | result -> failwithf "Expected UnknownFunctionName, got %A" result

        let duplicated =
            fixture.Snapshot.Functions
            |> List.map (fun functionEntity ->
                if functionEntity.Id = fixture.MainFunctionId then { functionEntity with DisplayName = "validate_user" } else functionEntity)
        match ViewerExport.resolveFunctionName { fixture.Snapshot with Functions = duplicated } "validate_user" with
        | Error(ViewerExportError.AmbiguousFunctionName("validate_user", candidates)) ->
            Assert.Equal<FunctionId list>(List.sortBy StableId.value [ fixture.ValidateFunctionId; fixture.MainFunctionId ], candidates)
        | result -> failwithf "Expected AmbiguousFunctionName, got %A" result

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
    let ``viewer export CLI rejects unknown options and two function selectors`` () =
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
            failwithf "CLI unexpectedly accepted an unknown option. stdout=%s" stdout
        if not (stderr.Contains("Unsupported option '--display-name'", StringComparison.Ordinal)) then
            failwithf "CLI did not report the unknown option explicitly. stderr=%s" stderr

        let bothCode, _, bothError =
            runDotnet
                root
                (cliArguments
                    [ "focus"
                      "--input"; "unused.json"
                      "--format"; "json"
                      "--function-id"; "function-main"
                      "--function"; "main" ])
        if bothCode = 0 || not (bothError.Contains("exactly one of --function-id and --function", StringComparison.Ordinal)) then
            failwithf "CLI accepted two function selectors. stderr=%s" bothError

    [<Fact>]
    let ``authoring session viewFunction writes the committed focus HTML without touching the snapshot`` () =
        let fixture = MilestoneZeroGraph.create ()
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-view-function-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        let send session (line: string) =
            AuthoringProtocol.executeLine session line |> JsonDocument.Parse
        try
            let before = SnapshotCodec.serialize fixture.Snapshot |> unwrap
            File.WriteAllText(path, before)
            use session = AuthoringSession.openExisting path |> unwrap
            let expected = ViewerExport.focusFromSnapshot fixture.Snapshot fixture.ValidateFunctionId |> unwrap

            use byName = send session """{"id":"view","op":"viewFunction","function":"validate_user"}"""
            Assert.True(byName.RootElement.GetProperty("ok").GetBoolean(), byName.RootElement.ToString())
            let result = byName.RootElement.GetProperty("result")
            let written = result.GetProperty("path").GetString()
            Assert.Equal(Path.Combine(root, ViewerFile.defaultDirectoryName, expected.SuggestedFileName), written)
            Assert.Equal(StableId.value fixture.ValidateFunctionId, result.GetProperty("functionId").GetString())
            Assert.Equal("validate_user", result.GetProperty("displayName").GetString())
            Assert.Equal(expected.Content, File.ReadAllText(written, Encoding.UTF8))

            let explicitPath = Path.Combine(root, "pages", "main.html")
            use byId =
                send session ($"""{{"id":"view","op":"viewFunction","functionId":"{StableId.value fixture.MainFunctionId}","output":{JsonSerializer.Serialize explicitPath}}}""")
            Assert.True(byId.RootElement.GetProperty("ok").GetBoolean(), byId.RootElement.ToString())
            Assert.Equal(explicitPath, byId.RootElement.GetProperty("result").GetProperty("path").GetString())
            Assert.Contains("Function Focus", File.ReadAllText(explicitPath, Encoding.UTF8))

            use notHtml = send session ($"""{{"id":"view","op":"viewFunction","function":"main","output":{JsonSerializer.Serialize path}}}""")
            Assert.False(notHtml.RootElement.GetProperty("ok").GetBoolean())
            Assert.Equal("ViewWriteFailed", notHtml.RootElement.GetProperty("error").GetProperty("code").GetString())

            // A relative output resolves beside the snapshot, not against the process working directory.
            use relative = send session """{"id":"view","op":"viewFunction","function":"main","output":"pages/relative.html"}"""
            Assert.True(relative.RootElement.GetProperty("ok").GetBoolean(), relative.RootElement.ToString())
            Assert.Equal(Path.Combine(root, "pages", "relative.html"), relative.RootElement.GetProperty("result").GetProperty("path").GetString())
            // Output is confined to the snapshot's directory: neither `..` nor an absolute path may leave it.
            let outside = Path.Combine(Path.GetTempPath(), "nyrwvy-view-escape-" + Guid.NewGuid().ToString("N") + ".html")
            for escape in [ "../escaped.html"; "pages/../../escaped.html"; outside ] do
                use refused = send session ($"""{{"id":"view","op":"viewFunction","function":"main","output":{JsonSerializer.Serialize escape}}}""")
                Assert.False(refused.RootElement.GetProperty("ok").GetBoolean(), escape)
                let error = refused.RootElement.GetProperty("error")
                Assert.Equal("ViewWriteFailed", error.GetProperty("code").GetString())
                Assert.Contains("inside the snapshot's directory", error.GetProperty("message").GetString())
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName root, "escaped.html")))
            Assert.False(File.Exists outside)
            // Review 2026-10-05: confinement also holds through links. A linked default directory, a linked
            // explicit parent and a linked final file are refused, and the file outside stays untouched.
            let outsideDirectory = Path.Combine(Path.GetTempPath(), "nyrwvy-view-link-target-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory outsideDirectory |> ignore
            try
                let victim = Path.Combine(outsideDirectory, expected.SuggestedFileName)
                File.WriteAllText(victim, "outside")
                let defaultDirectory = Path.Combine(root, ViewerFile.defaultDirectoryName)
                Directory.Delete(defaultDirectory, true)
                Directory.CreateSymbolicLink(defaultDirectory, outsideDirectory) |> ignore
                Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outsideDirectory) |> ignore
                File.CreateSymbolicLink(Path.Combine(root, "pages", "linked.html"), victim) |> ignore
                let byDefault = """{"id":"view","op":"viewFunction","function":"validate_user"}"""
                let explicitOutput output = $"""{{"id":"view","op":"viewFunction","function":"validate_user","output":{JsonSerializer.Serialize(output: string)}}}"""
                for line in [ byDefault; explicitOutput ("linked/" + expected.SuggestedFileName); explicitOutput "pages/linked.html" ] do
                    use refused = send session line
                    Assert.False(refused.RootElement.GetProperty("ok").GetBoolean(), line)
                    let error = refused.RootElement.GetProperty("error")
                    Assert.Equal("ViewWriteFailed", error.GetProperty("code").GetString())
                    Assert.Contains("symbolic link", error.GetProperty("message").GetString())
                Assert.Equal("outside", File.ReadAllText victim)
            finally
                for link in [ Path.Combine(root, ViewerFile.defaultDirectoryName); Path.Combine(root, "linked"); Path.Combine(root, "pages", "linked.html") ] do
                    if not (isNull (FileInfo(link).LinkTarget)) then File.Delete link
                Directory.Delete(outsideDirectory, true)
            use both = send session ($"""{{"id":"view","op":"viewFunction","function":"main","functionId":"{StableId.value fixture.MainFunctionId}"}}""")
            Assert.False(both.RootElement.GetProperty("ok").GetBoolean())
            use unknown = send session """{"id":"view","op":"viewFunction","function":"missing"}"""
            Assert.False(unknown.RootElement.GetProperty("ok").GetBoolean())

            Assert.Equal(before, File.ReadAllText path)
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)

    [<Fact>]
    let ``authoring session viewProposal writes a pending proposal's Change Review without publishing it`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-view-proposal-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        let send session (line: string) =
            AuthoringProtocol.executeLine session line |> JsonDocument.Parse
        let ok (document: JsonDocument) =
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean(), document.RootElement.ToString())
            document.RootElement.GetProperty("result")
        let view session (proposalId: string) =
            use reply = send session ($"""{{"id":"view","op":"viewProposal","proposalId":{JsonSerializer.Serialize proposalId}}}""")
            let result = ok reply
            Assert.Equal(proposalId, result.GetProperty("proposalId").GetString())
            Assert.Equal("changeReview", result.GetProperty("kind").GetString())
            let written = result.GetProperty("path").GetString()
            Assert.Equal(Path.Combine(root, ViewerFile.defaultDirectoryName), Path.GetDirectoryName written)
            File.ReadAllText(written, Encoding.UTF8)
        try
            use session = AuthoringSession.createNew path |> unwrap
            use created =
                send session """{"id":"create","op":"simulateCreateFunctionByName","displayName":"flag","parameters":[],"resultType":{"kind":"primitive","primitive":"bool"},"requiredEffects":[],"body":{"kind":"boolLiteral","value":true}}"""
            let createdId = (ok created).GetProperty("proposalId").GetString()
            let createdPage = view session createdId
            Assert.Contains("Change Review — createFunction", createdPage)
            Assert.Contains("flag — created", createdPage)
            Assert.Contains("Engine review", createdPage)
            use _ = send session ($"""{{"id":"commit","op":"commit","proposalId":{JsonSerializer.Serialize createdId}}}""")
            let committed = File.ReadAllText path

            use replaced =
                send session """{"id":"replace","op":"simulateReplaceBodyByName","function":"flag","replacement":{"kind":"boolLiteral","value":false}}"""
            let replacedId = (ok replaced).GetProperty("proposalId").GetString()
            let replacedPage = view session replacedId
            Assert.Contains("Change Review — replaceBody", replacedPage)
            Assert.Contains("flag — changed", replacedPage)
            Assert.Contains("<summary>Committed</summary>", replacedPage)
            Assert.Contains("bool literal true", replacedPage)
            Assert.Contains("bool literal false", replacedPage)
            Assert.Equal(replacedPage, view session replacedId)

            use typed =
                send session """{"id":"type","op":"simulateCreateTypeByName","displayName":"Choice","typeKind":{"kind":"union","variants":[{"displayName":"Yes","fields":[]},{"displayName":"No","fields":[]}]}}"""
            let typePage = view session ((ok typed).GetProperty("proposalId").GetString())
            Assert.Contains("Declarations created or updated", typePage)
            Assert.Contains("Choice", typePage)

            use explicitPage =
                send session ($"""{{"id":"view","op":"viewProposal","proposalId":{JsonSerializer.Serialize replacedId},"output":{JsonSerializer.Serialize(Path.Combine(root, "pages", "replace.html"))}}}""")
            Assert.Equal(Path.Combine(root, "pages", "replace.html"), (ok explicitPage).GetProperty("path").GetString())

            use _ = send session ($"""{{"id":"reject","op":"reject","proposalId":{JsonSerializer.Serialize replacedId}}}""")
            use rejected = send session ($"""{{"id":"view","op":"viewProposal","proposalId":{JsonSerializer.Serialize replacedId}}}""")
            Assert.False(rejected.RootElement.GetProperty("ok").GetBoolean())
            use missingId = send session """{"id":"view","op":"viewProposal"}"""
            Assert.False(missingId.RootElement.GetProperty("ok").GetBoolean())

            Assert.Equal(committed, File.ReadAllText path)
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)

    [<Fact>]
    let ``authoring session viewProposal renders an operations proposal with its Change Review projection`` () =
        let fixture = MilestoneZeroGraph.create ()
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-view-operations-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            File.WriteAllText(path, SnapshotCodec.serialize fixture.Snapshot |> unwrap)
            use session = AuthoringSession.openExisting path |> unwrap
            use renamed =
                AuthoringProtocol.executeLine
                    session
                    ($"""{{"id":"rename","op":"simulateOperations","operations":[{{"kind":"renameFunction","functionId":"{StableId.value fixture.ValidateFunctionId}","displayName":"check_user"}}]}}""")
                |> JsonDocument.Parse
            let proposalId = renamed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
            use page =
                AuthoringProtocol.executeLine session ($"""{{"id":"view","op":"viewProposal","proposalId":"{proposalId}"}}""")
                |> JsonDocument.Parse
            Assert.True(page.RootElement.GetProperty("ok").GetBoolean(), page.RootElement.ToString())
            let content = File.ReadAllText(page.RootElement.GetProperty("result").GetProperty("path").GetString(), Encoding.UTF8)
            Assert.Contains("<h1>Change Review</h1>", content)
            Assert.Contains("validate_user → check_user", content)
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
