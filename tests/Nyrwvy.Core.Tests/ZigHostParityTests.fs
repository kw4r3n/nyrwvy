namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// The Zig session host (tools/zig/host, docs/decisions/zig-session-host.md) against the F# host:
/// each transcript in tests/host-parity is one request per line, fed to both on the same snapshots,
/// and every response line must be the same bytes; the startups that fail must print the same
/// line and exit code. Two parts are host-specific: .NET's JSON parser text after "Invalid JSON:"
/// and "not valid JSON:" (compared by that prefix) and `status.build` (each host's own build).
module ZigHostParityTests =
    let private unwrap = function Ok x -> x | Error e -> failwithf "%A" e

    let private comparable (line: string) =
        let masked = Regex.Replace(line, "\"build\":\"[^\"]*\"", "\"build\":\"…\"")
        Regex.Replace(masked, "(Invalid JSON: |not valid JSON: ).*", "$1…")

    let private zigHost () = ZigTools.tool "host" |> Result.mapError (fun (code, message) -> $"{code}: {message}") |> unwrap
    let private fsharpHost = Path.Combine(AppContext.BaseDirectory, "Nyrwvy.AuthoringSession.dll")

    /// Starts a host on `arguments`; `requests` are written and its input closed unless `hold`.
    let private start (zig: bool) (arguments: string list) =
        let info = if zig then ProcessStartInfo(zigHost (), arguments) else ProcessStartInfo("dotnet", fsharpHost :: arguments)
        info.RedirectStandardInput <- true
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.Environment["NYRWVY_RUNPURE_WARMUP"] <- "0"
        Process.Start info

    /// Exit code, stderr and stdout lines of a host run to the end of `requests`.
    let private run zig arguments (requests: string list) =
        use host = start zig arguments
        for request in requests do host.StandardInput.Write(request + "\n")
        host.StandardInput.Close()
        let output = host.StandardOutput.ReadToEndAsync()
        let errors = host.StandardError.ReadToEnd()
        host.WaitForExit()
        host.ExitCode, errors.Trim(), output.Result.Split('\n') |> Array.toList |> List.filter ((<>) "")

    let private snapshots (dir: string) =
        let root = ZigModelReaderSupport.root ()
        let records = Directory.GetFiles(Path.Combine(root, "examples"), "*.nyrec", SearchOption.AllDirectories) |> Array.toList
        let json = Path.Combine(dir, "task-outcomes.json")
        File.WriteAllText(json, File.ReadAllText records.Head |> RecordCodec.deserialize |> unwrap |> SnapshotCodec.serialize |> unwrap)
        records @ [ json ]

    let private withDirectory f =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-host-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try f dir finally Directory.Delete(dir, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host answers every transcript as the F# host does`` () =
        withDirectory (fun dir ->
            let transcripts = Directory.GetFiles(Path.Combine(ZigModelReaderSupport.root (), "tests", "host-parity"), "*.jsonl")
            Assert.NotEmpty transcripts
            for snapshot in snapshots dir do
                use session = AuthoringSession.openReadOnly snapshot |> unwrap
                for transcript in transcripts do
                    let requests = File.ReadAllText(transcript).Split('\n') |> Array.toList |> fun lines -> List.take (lines.Length - 1) lines
                    let fsharp = requests |> List.map (AuthoringProtocol.executeLine session)
                    let code, errors, answers = run true [ "open"; "--snapshot"; snapshot; "--read-only" ] requests
                    Assert.True((code = 0), errors)
                    Assert.Equal(requests.Length, answers.Length)
                    List.zip3 requests fsharp answers |> List.iteri (fun index (request, expected, actual) ->
                        if comparable expected <> comparable actual then
                            failwithf "%s on %s line %d: %s\n F#:  %s\n Zig: %s" (Path.GetFileName transcript) (Path.GetFileName snapshot) (index + 1) request expected actual))

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host fails to start as the F# host does`` () =
        withDirectory (fun dir ->
            let record = (snapshots dir).Head
            let json = File.ReadAllText(Path.Combine(dir, "task-outcomes.json"))
            let edited (change: JsonObject -> unit) =
                let document = JsonNode.Parse(json).AsObject()
                change document
                document.ToJsonString()
            let firstFunction (document: JsonObject) = (document["functions"].AsArray()[0]).AsObject()
            let lines = File.ReadAllLines record
            let joined (items: string array) = String.Join("\n", items) + "\n"
            let bound = joined (Array.append [| lines[0] + " dependency=\"x\" digest=00" |] lines[1..])
            let orphan = joined lines + "field field:01a00000-0000-7000-8000-000000000000 owner=type:01a00000-0000-7000-8000-000000000001\n"
            let truncated = joined lines[.. lines.Length - 2]
            let cases =
                [ "missing.json", None
                  "syntax.json", Some "{oops"
                  "shape.json", Some """{"schemaVersion":24}"""
                  "version.json", Some(edited (fun d -> d["schemaVersion"] <- JsonValue.Create 99))
                  "invalid.json", Some(edited (fun d -> (firstFunction d)["resultType"] <- JsonNode.Parse """{"kind":"primitive","primitive":"unit"}"""))
                  "bound.json", Some(edited (fun d -> d["dependency"] <- JsonNode.Parse """{"snapshotId":"x","digest":"00"}"""))
                  "bound.nyrec", Some bound
                  "orphan.nyrec", Some orphan
                  "truncated.nyrec", Some truncated ]
            for name, content in cases do
                let path = Path.Combine(dir, name)
                content |> Option.iter (fun text -> File.WriteAllText(path, text))
                for readOnly in [ true; false ] do
                    let arguments = [ "open"; "--snapshot"; path ] @ (if readOnly then [ "--read-only" ] else [])
                    let fsharp, zig = run false arguments [], run true arguments []
                    let shown (code, errors, _) = $"{code} {comparable errors}"
                    Assert.True(shown fsharp = shown zig, $"{name}:\n F#:  {shown fsharp}\n Zig: {shown zig}")
                    let code, _, _ = fsharp
                    Assert.True((code = 2), $"{name}: the F# host started")
            // A legacy schema (9 to 20) reads only with F#; the Zig host names the migration instead.
            let legacy = Path.Combine(dir, "legacy.json")
            File.WriteAllText(legacy, SnapshotCodec.serializeCanonical (RecordCodec.deserialize (File.ReadAllText record) |> unwrap) |> unwrap)
            let code, errors, _ = run true [ "open"; "--snapshot"; legacy; "--read-only" ] []
            Assert.Equal(2, code)
            Assert.Contains("is a legacy schema, which the Zig host does not read", errors))

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig and F# hosts lock each other out and sweep temporaries`` () =
        withDirectory (fun dir ->
            let snapshot = Path.Combine(dir, "locked.json")
            File.Copy(Path.Combine(dir, (snapshots dir |> List.last)), snapshot)
            let stray = Path.Combine(dir, ".locked.json.0123456789abcdef0123456789abcdef.tmp")
            let foreign = Path.Combine(dir, ".locked.json.not-a-temporary.tmp")
            for file in [ stray; foreign ] do File.WriteAllText(file, "")
            for holder in [ false; true ] do
                use held = start holder [ "open"; "--snapshot"; snapshot ]
                held.StandardInput.WriteLine """{"id":"s","op":"status"}"""
                Assert.Contains("\"ok\":true", held.StandardOutput.ReadLine())
                let code, errors, _ = run (not holder) [ "open"; "--snapshot"; snapshot ] []
                Assert.Equal(2, code)
                Assert.Equal($"LockUnavailable: {snapshot} is open in another authoring session. Close that session first.", errors)
                // Read-only takes no lock.
                let code, _, _ = run (not holder) [ "open"; "--snapshot"; snapshot; "--read-only" ] [ """{"id":"s","op":"status"}""" ]
                Assert.Equal(0, code)
                held.StandardInput.Close()
                held.WaitForExit()
                if holder then
                    Assert.False(File.Exists stray, "The Zig host left a temporary of an interrupted write.")
                    Assert.True(File.Exists foreign, "The Zig host removed a file outside the temporary pattern.")
                File.WriteAllText(stray, ""))
