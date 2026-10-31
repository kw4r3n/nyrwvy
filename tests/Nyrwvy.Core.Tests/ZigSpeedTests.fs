namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit
open Xunit.Abstractions

/// Wall time of the Zig path against the F# path, warm, median of 9 runs after one warm-up; results
/// and how to run it: evaluation/zig-speed/README.md. `Suite=Benchmark` keeps it out of the suites.
type ZigSpeed(output: ITestOutputHelper) =
    let runs = 9
    let median (f: unit -> unit) =
        f ()
        let times = [| for _ in 1 .. runs -> let watch = Stopwatch.StartNew() in f (); watch.Elapsed.TotalMilliseconds |]
        Array.sortInPlace times
        times[runs / 2]
    let ok (result: Result<'a, 'e>) = match result with Ok x -> x | Error e -> failwithf "%A" e
    let tool name = ZigTools.tool name |> Result.mapError (fun (code, message) -> $"{code}: {message}") |> ok
    let launch exe (args: string list) =
        match ZigTools.launch exe "bench" ZigTools.cache args 120000 with
        | Ok(_, out, _) -> out.Trim()
        | Error e -> failwithf "%A" e
    let withReader (value: string) f =
        Environment.SetEnvironmentVariable("NYRWVY_SNAPSHOT_READER", value)
        try f () finally Environment.SetEnvironmentVariable("NYRWVY_SNAPSHOT_READER", null)

    [<Fact; Trait("Suite", "Benchmark")>]
    member _.``ZigSpeed: runPure Zig and CSharp backends through both hosts`` () =
        let dir = Path.Combine(ZigTools.cache, "speed-run-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            let path = Path.Combine(dir, "workspace.json")
            do
                use session = AuthoringSession.createNew path |> ok
                let request line =
                    use reply = Text.Json.JsonDocument.Parse(AuthoringProtocol.executeLine session line)
                    Assert.True(reply.RootElement.GetProperty("ok").GetBoolean(), reply.RootElement.GetRawText())
                    reply.RootElement.GetProperty("result").Clone()
                let created = request """{"id":"c","op":"simulateCreateFunctionByName","exported":true,"displayName":"echo","parameters":[{"displayName":"value","type":{"kind":"primitive","primitive":"string"}}],"resultType":{"kind":"primitive","primitive":"string"},"requiredEffects":[],"body":{"kind":"parameter","name":"value"}}"""
                let proposalId = created.GetProperty("proposalId").GetString()
                request $"""{{"id":"m","op":"commit","proposalId":"{proposalId}"}}""" |> ignore
            let dll = Path.Combine(ZigModelReaderSupport.root (), "tools", "Nyrwvy.AuthoringSession", "bin", "Release", "net10.0", "Nyrwvy.AuthoringSession.dll")
            if not (File.Exists dll) then failwithf "%s is not built (dotnet build -c Release)" dll
            let zigHost = tool "host"
            let runHost backend label exe args =
                let info = ProcessStartInfo(exe, args @ [ "open"; "--snapshot"; path; "--read-only" ])
                info.Environment["NYRWVY_RUNPURE_WARMUP"] <- "0"
                info.RedirectStandardInput <- true
                info.RedirectStandardOutput <- true
                info.RedirectStandardError <- true
                use host = Process.Start info
                let errors = host.StandardError.ReadToEndAsync()
                let read () =
                    let reading = host.StandardOutput.ReadLineAsync()
                    if not (reading.Wait(TimeSpan.FromMinutes 6.)) then
                        host.Kill true
                        failwithf "%s timed out" label
                    let line = reading.Result
                    if isNull line then failwithf "%s exited before replying" label
                    line
                let run () =
                    host.StandardInput.WriteLine($"""{{"id":"r","op":"runPureByName","function":"echo","backend":"{backend}","arguments":["hello"],"expected":"hello"}}""")
                    host.StandardInput.Flush()
                    let line = read ()
                    use reply = Text.Json.JsonDocument.Parse line
                    Assert.True(reply.RootElement.GetProperty("ok").GetBoolean(), line)
                    let result = reply.RootElement.GetProperty("result")
                    Assert.Equal("hello", result.GetProperty("actual").GetProperty("value").GetString())
                    Assert.True(result.GetProperty("matches").GetBoolean())
                try
                    host.StandardInput.WriteLine """{"id":"s","op":"status"}"""
                    host.StandardInput.Flush()
                    use status = Text.Json.JsonDocument.Parse(read ())
                    Assert.True(status.RootElement.GetProperty("ok").GetBoolean(), status.RootElement.GetRawText())
                    let first = Stopwatch.StartNew()
                    run ()
                    let firstMs = first.Elapsed.TotalMilliseconds
                    let repeated = median run
                    host.StandardInput.Close()
                    if not (host.WaitForExit 10000) then host.Kill true
                    host.WaitForExit()
                    Assert.Equal(0, host.ExitCode)
                    Assert.Equal("", errors.Result.Trim())
                    output.WriteLine($"{label}: first request {firstMs:F1} ms; repeated median {repeated:F1} ms (9 runs after warm-up)")
                finally
                    if not host.HasExited then
                        host.Kill true
                        host.WaitForExit()
            output.WriteLine "runPure Zig and C# backends, same small snapshot; first request may build, repeated requests reuse the backend cache; host startup excluded; F# warm-up disabled"
            for backend in [ "zig"; "csharp" ] do
                runHost backend $"F# host (backend={backend})" "dotnet" [ dll ]
                runHost backend $"Zig host (backend={backend})" zigHost []
        finally Directory.Delete(dir, true)

    [<Fact; Trait("Suite", "Benchmark")>]
    member _.``ZigSpeed: Zig tools against F#`` () =
        let dir = Path.Combine(ZigTools.cache, "speed-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            let root = ZigModelReaderSupport.root ()
            let modelReader, emitter = tool "model-reader", tool "emitter"
            let inputs =
                [ yield "task-outcomes.nyrec", Path.Combine(root, "examples", "task-cancellation-outcome", "fixtures", "task-outcomes.nyrec")
                  match Environment.GetEnvironmentVariable "NYRWVY_SPEED_RHIFILL" with
                  | null | "" -> ()
                  | path ->
                      // A resolved workspace reads with F# only, so its merged model is timed as a plain JSON snapshot.
                      use session = AuthoringSession.openReadOnly path |> ok
                      let merged = Path.Combine(dir, "rhifill-merged.json")
                      File.WriteAllText(merged, (session.ExecutionContext None |> ok).ModelJson () |> ok |> fst)
                      yield "Rhifill (merged)", merged ]
            output.WriteLine($"median of {runs} runs after one warm-up, ms; process spawn {median (fun () -> launch modelReader [] |> ignore):F1}")
            let row (name: string) (cells: (string * float) list) =
                output.WriteLine(name.PadRight 22 + String.Join("  ", cells |> List.map (fun (k, v) -> $"{k} {v:F1}")))
            for name, source in inputs do
                let path = Path.Combine(dir, Path.GetFileName source)
                if source <> path then File.Copy(source, path, true)
                // (1) opening a session (read, validate, workspace), Zig-decided and F# only.
                let openZig = median (fun () -> (AuthoringSession.openReadOnly path |> ok).Close())
                let openFs = withReader "fsharp" (fun () -> median (fun () -> (AuthoringSession.openReadOnly path |> ok).Close()))
                // (2) runPure's whole program up to the Zig source, as the routes do it.
                use session = AuthoringSession.openReadOnly path |> ok
                let context = session.ExecutionContext None |> ok
                let json, ir = Path.Combine(dir, "model.json"), Path.Combine(dir, "ir.json")
                let zigModel = median (fun () -> File.WriteAllText(json, context.ModelJson () |> ok |> fst))
                // As runPure's route: JSON the Zig validator accepted is not validated again.
                let mode = if context.ModelJson () |> ok |> snd then [ "--execution-ir"; "--validated" ] else [ "--execution-ir" ]
                let lowered = launch modelReader (mode @ [ json; ir; "-" ])
                let zigLower = median (fun () -> launch modelReader (mode @ [ json; ir; "-" ]) |> ignore)
                let emit = median (fun () -> launch emitter [ ir ] |> ignore)
                // runPure's emission, which a repeated run of the same IR takes from the cache.
                let irText = File.ReadAllText ir
                let emitRepeated = median (fun () -> PureRun.zigEmit irText (fun () -> failwith "no check") None |> ok |> ignore)
                let fsLower = median (fun () -> session.ProjectExecution() |> ok |> ExecutionIrSerializer.serialize |> ignore)
                let program = session.ProjectExecution() |> ok
                let fsEmit = median (fun () -> ZigBackend.runner program None |> ignore)
                // (3) the tools on their own.
                let validate = median (fun () -> launch modelReader [ "--validate"; json ] |> ignore)
                let text = File.ReadAllText json
                let build = median (fun () -> SnapshotCodec.readUnvalidated text |> ok |> ignore)
                let snapshot = SnapshotCodec.readUnvalidated text |> ok
                let fsValidate = median (fun () -> SnapshotValidator.validate snapshot |> ignore)
                output.WriteLine($"-- {name}: {text.Length / 1024} KiB model JSON, lowering {lowered} ({String.Join(' ', mode)})")
                row "(1) open" [ "zig-decided", openZig; "fsharp", openFs ]
                row "(2) to Zig source" [ "zig", zigModel + zigLower + emit; "fsharp+zig-emitter", fsLower + emit; "fsharp", fsLower + fsEmit ]
                row "    zig route" [ "ModelJson", zigModel; "model-reader lower", zigLower; "emitter", emit; "repeated runPure emit", emitRepeated ]
                row "    fsharp route" [ "project+lower+ser", fsLower; "F# emitter", fsEmit ]
                row "(3) tools" [ "model-reader validate", validate; "F# Snapshot build", build; "F# validate", fsValidate ]
            match Environment.GetEnvironmentVariable "NYRWVY_SPEED_RHIFILL" with
            | null | "" -> ()
            | path ->
                // The resolved workspace itself, with its dependency sidecar (last, as it changes the later rows' heap).
                let openResolved = median (fun () -> (AuthoringSession.openReadOnly path |> ok).Close())
                row "Rhifill resolved open" [ "as read", openResolved ]
        finally Directory.Delete(dir, true)

    /// Where an AuthoringSession spends its time on Rhifill: the host process an agent starts, then
    /// single requests through `executeLine`, the per-line entry `serve` runs (the host minus stdio).
    [<Fact; Trait("Suite", "Benchmark")>]
    member _.``ZigSpeed: AuthoringSession ops on Rhifill`` () =
        match Environment.GetEnvironmentVariable "NYRWVY_SPEED_RHIFILL" with
        | null | "" -> output.WriteLine "NYRWVY_SPEED_RHIFILL is unset; skipped."
        | source ->
        let dir = Path.Combine(ZigTools.cache, "speed-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            // The workspace and its sidecars (not its lock), so commit writes only the copy.
            let path = Path.Combine(dir, Path.GetFileName source)
            for file in Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath source), Path.GetFileName source + "*") do
                if not (file.EndsWith ".lock") then File.Copy(file, Path.Combine(dir, Path.GetFileName file))
            let row (name: string) (cells: (string * float) list) =
                output.WriteLine(name.PadRight 22 + String.Join("  ", cells |> List.map (fun (k, v) -> $"{k} {v:F1}")))
            let time f = let watch = Stopwatch.StartNew() in f (); watch.Elapsed.TotalMilliseconds
            // As tools/nyrwvy-agent/authoring.ts launches the host for a read session.
            let dll = Path.Combine(ZigModelReaderSupport.root (), "tools", "Nyrwvy.AuthoringSession", "bin", "Release", "net10.0", "Nyrwvy.AuthoringSession.dll")
            if not (File.Exists dll) then failwithf "%s is not built (dotnet build -c Release)" dll
            let spawnWith (file: string) (arguments: string list) (warmup: string) =
                let info = ProcessStartInfo(file, arguments)
                info.Environment["NYRWVY_RUNPURE_WARMUP"] <- warmup
                info.RedirectStandardInput <- true
                info.RedirectStandardOutput <- true
                info.RedirectStandardError <- true
                let watch = Stopwatch.StartNew()
                use host = Process.Start info
                host.StandardInput.WriteLine """{"id":"s","op":"status"}"""
                host.StandardInput.Flush()
                let reply = host.StandardOutput.ReadLine()
                let elapsed = watch.Elapsed.TotalMilliseconds
                host.StandardInput.Close()
                host.WaitForExit()
                if isNull reply || not (reply.Contains "\"ok\":true") then failwithf "host: %s %s" reply (host.StandardError.ReadToEnd())
                elapsed
            let spawn = spawnWith "dotnet" [ dll; "open"; "--snapshot"; path; "--read-only" ]
            let medianOf (f: unit -> float) = ([| for _ in 0 .. runs -> f () |] |> Array.skip 1 |> Array.sort)[runs / 2]
            let spawned warmup = medianOf (fun () -> spawn warmup)
            // The same host given no arguments prints its usage and exits: the runtime start alone.
            let usage = median (fun () ->
                let info = ProcessStartInfo("dotnet", [ dll ])
                info.RedirectStandardError <- true
                use host = Process.Start info
                host.StandardError.ReadToEnd() |> ignore
                host.WaitForExit())
            let openReadOnly = median (fun () -> (AuthoringSession.openReadOnly path |> ok).Close())
            let zigHost = ZigTools.tool "host" |> Result.mapError (fun (code, message) -> $"{code}: {message}") |> ok
            row "start" [ "host spawn to status reply", spawned "1"; "same, warm-up off", spawned "0"; "host usage exit", usage; "in-process openReadOnly", openReadOnly ]
            row "start, Zig host" [ "spawn to status reply", medianOf (fun () -> spawnWith zigHost [ "open"; "--snapshot"; path; "--read-only" ] "1") ]
            // Both hosts on the merged model as a plain snapshot (no sidecar).
            let merged = Path.Combine(dir, "merged.json")
            (using (AuthoringSession.openReadOnly path |> ok) (fun session -> File.WriteAllText(merged, (session.ExecutionContext None |> ok).ModelJson () |> ok |> fst)))
            let onMerged = [ "open"; "--snapshot"; merged; "--read-only" ]
            row "start, merged model" [ "zig host", medianOf (fun () -> spawnWith zigHost onMerged "1"); "F# host", medianOf (fun () -> spawnWith "dotnet" (dll :: onMerged) "1") ]
            use session = AuthoringSession.openExisting path |> ok
            let request line =
                use reply = Text.Json.JsonDocument.Parse(AuthoringProtocol.executeLine session line)
                if not (reply.RootElement.GetProperty("ok").GetBoolean()) then failwithf "%s -> %s" line (reply.RootElement.GetRawText())
                reply.RootElement.GetProperty("result").Clone()
            let status = median (fun () -> request """{"id":"s","op":"status"}""" |> ignore)
            let read = median (fun () -> request """{"id":"r","op":"readFunction","function":"actionName"}""" |> ignore)
            let search = median (fun () -> request """{"id":"q","op":"search","text":"binding"}""" |> ignore)
            row "read ops" [ "status", status; "readFunction", read; "search", search ]
            // Rhifill's case actionName-focus_left. The host warms the worker in the background at start.
            let run = """{"id":"p","op":"runPureByName","function":"actionName","arguments":[4],"expected":"focus_left"}"""
            let warm = time (fun () -> PureRun.warm session.PureRunWorker)
            let first = time (fun () -> request run |> ignore)
            let repeated = median (fun () -> request run |> ignore)
            // Rhifill declares only csharp (the Zig backend refuses its capability operation types), so no Zig tool runs here.
            row "runPure (csharp)" [ "warm-up", warm; "first (once)", first; "repeated", repeated ]
            // Each round proposes a new one-literal function and commits it, so the copy grows by one function a round.
            let rounds =
                [| for round in 0 .. runs ->
                     let mutable proposal = ""
                     let simulate = time (fun () ->
                         let line = $"""{{"id":"c","op":"simulateCreateFunctionByName","exported":true,"displayName":"speed{round}","parameters":[],"resultType":{{"kind":"primitive","primitive":"int64"}},"requiredEffects":[],"body":{{"kind":"int64Literal","value":1}}}}"""
                         proposal <- (request line).GetProperty("proposalId").GetString())
                     simulate, time (fun () -> request $"""{{"id":"m","op":"commit","proposalId":"{proposal}"}}""" |> ignore) |]
                |> Array.skip 1
            let mid (values: float[]) = (Array.sort values)[runs / 2]
            row "mutation" [ "simulateCreateFunction", rounds |> Array.map fst |> mid; "commit", rounds |> Array.map snd |> mid ]
        finally Directory.Delete(dir, true)
