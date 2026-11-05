namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit

/// Runs the export tool as a user would, against the real Zig host.
module ViewerExportTests =
    let private host () =
        match ZigTools.tool "host" with
        | Ok path -> path
        | Error(code, message) -> failwith $"{code}: {message}"

    let private withDirectory action =
        let directory = Directory.CreateTempSubdirectory("nyrwvy-viewer-export-test-").FullName
        try action directory
        finally Directory.Delete(directory, true)

    let private start (executable: string) (args: string list) =
        let info = ProcessStartInfo(executable, args)
        info.RedirectStandardInput <- true
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.Environment["NYRWVY_HOST_EXECUTABLE"] <- host ()
        Process.Start info

    let private finish (child: Process) =
        child.StandardInput.Close()
        let output = child.StandardOutput.ReadToEndAsync()
        let errors = child.StandardError.ReadToEndAsync()
        child.WaitForExit()
        child.ExitCode, output.Result, errors.Result

    /// A snapshot created with `dependency` when given, holding one committed function `name`.
    let private createSnapshot (path: string) (name: string) (dependency: string option) =
        let args = [ "create"; "--snapshot"; path ] @ (dependency |> Option.map (fun item -> [ "--dependency"; item ]) |> Option.defaultValue [])
        use child = start (host ()) args
        let send (line: string) =
            child.StandardInput.WriteLine line
            child.StandardInput.Flush()
            JsonDocument.Parse(child.StandardOutput.ReadLine())
        use created = send ("""{"id":"create","op":"simulateCreateFunctionByName","displayName":"NAME","parameters":[],"resultType":{"kind":"primitive","primitive":"bool"},"requiredEffects":[],"body":{"kind":"boolLiteral","value":true}}""".Replace("NAME", name))
        Assert.True(created.RootElement.GetProperty("ok").GetBoolean(), created.RootElement.GetRawText())
        let proposal = created.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
        use committed = send $$"""{"id":"commit","op":"commit","proposalId":"{{proposal}}"}"""
        Assert.True(committed.RootElement.GetProperty("ok").GetBoolean(), committed.RootElement.GetRawText())
        let code, _, errors = finish child
        Assert.True((code = 0), errors)

    let private export (args: string list) =
        // The test project references the tool without its assembly, so run it from the tool's own build output.
        let configuration = DirectoryInfo(AppContext.BaseDirectory).Parent.Name
        let tool = Path.Combine(ZigModelReaderSupport.root (), "tools", "Nyrwvy.ViewerExport", "bin", configuration, "net10.0", "Nyrwvy.ViewerExport.dll")
        use child = start "dotnet" ([ tool; "focus"; "--format"; "json"; "--function"; "value" ] @ args)
        finish child

    [<Fact; Trait("Suite", "Extended")>]
    let ``focus exports a snapshot that has a dependency`` () =
        withDirectory (fun directory ->
            let library = Path.Combine(directory, "library.json")
            let app = Path.Combine(directory, "app.json")
            let page = Path.Combine(directory, "page.html")
            createSnapshot library "imported" None
            createSnapshot app "value" (Some library)
            Assert.True(File.Exists(app + ".dependency.json"))
            let code, _, errors = export [ "--input"; app; "--output"; page ]
            Assert.True((code = 0), errors)
            Assert.Contains("value", File.ReadAllText page))

    [<Fact; Trait("Suite", "Extended")>]
    let ``focus refuses to overwrite its input`` () =
        withDirectory (fun directory ->
            let app = Path.Combine(directory, "app.json")
            createSnapshot app "value" None
            let before = File.ReadAllBytes app
            let code, _, errors = export [ "--input"; app; "--output"; app ]
            Assert.True((code = 2), errors)
            Assert.Equal<byte[]>(before, File.ReadAllBytes app))
