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
