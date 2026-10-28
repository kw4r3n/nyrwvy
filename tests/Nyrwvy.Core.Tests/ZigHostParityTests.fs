namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// The Zig session host (tools/zig/host, docs/decisions/zig-session-host.md) against the F# host: each
/// transcript in tests/host-parity is one request per line, fed to both on the same snapshot, and
/// every response line must be the same bytes. .NET's JSON parser text after "Invalid JSON:" is
/// the one part compared by prefix.
module ZigHostParityTests =
    let private unwrap = function Ok x -> x | Error e -> failwithf "%A" e

    let private comparable (line: string) =
        let at = line.IndexOf "\"message\":\"Invalid JSON: "
        if at < 0 then line else line.Substring(0, at) + "\"message\":\"Invalid JSON: …\"}}"

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host answers every transcript as the F# host does`` () =
        let root = ZigModelReaderSupport.root ()
        let snapshot = Path.Combine(root, "examples", "task-cancellation-outcome", "fixtures", "task-outcomes.nyrec")
        let host = ZigTools.tool "host" |> Result.mapError (fun (code, message) -> $"{code}: {message}") |> unwrap
        let transcripts = Directory.GetFiles(Path.Combine(root, "tests", "host-parity"), "*.jsonl")
        Assert.NotEmpty transcripts
        for transcript in transcripts do
            let requests = File.ReadAllText(transcript).Split('\n') |> Array.toList |> fun lines -> List.take (lines.Length - 1) lines
            let fsharp =
                use session = AuthoringSession.openReadOnly snapshot |> unwrap
                requests |> List.map (AuthoringProtocol.executeLine session)
            let info = ProcessStartInfo(host, [ "open"; "--snapshot"; snapshot; "--read-only" ])
            info.RedirectStandardInput <- true
            info.RedirectStandardOutput <- true
            info.UseShellExecute <- false
            use zig = Process.Start info
            for request in requests do zig.StandardInput.Write(request + "\n")
            zig.StandardInput.Close()
            let output = zig.StandardOutput.ReadToEnd()
            zig.WaitForExit()
            Assert.Equal(0, zig.ExitCode)
            let answers = output.Split('\n') |> Array.toList |> List.filter ((<>) "")
            Assert.Equal(requests.Length, answers.Length)
            List.zip3 requests fsharp answers |> List.iteri (fun index (request, expected, actual) ->
                if comparable expected <> comparable actual then
                    failwithf "%s line %d: %s\n F#:  %s\n Zig: %s" (Path.GetFileName transcript) (index + 1) request expected actual)
