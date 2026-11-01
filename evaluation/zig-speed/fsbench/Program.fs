// `fsbench <validate|lower> <snapshot.json> [runs|check]`: the F# counterpart of `model-reader
// --validate` and `model-reader --execution-ir` (the F# emitter was removed). Without `runs` it does the task once (a
// cold process); with it, it prints the median of `runs` runs after `runs` warm-up runs, in ms.
open System
open System.Diagnostics
open System.IO
open Nyrwvy.Core

let ok label (result: Result<'a, 'e>) = match result with Ok x -> x | Error e -> failwithf "%s: %A" label e

// The model reader's lowering: read and validate, project, lower, write the IR JSON.
let lower (text: string) =
    use _scope = SnapshotComputationScope.Start()
    SnapshotCodec.deserialize text |> ok "read" |> TypedSemanticIr.project |> ok "project" |> ExecutionIr.lower |> ok "lower"

[<EntryPoint>]
let main argv =
    let text = File.ReadAllText argv[1]
    let task: unit -> string =
        match argv[0] with
        | "validate" -> fun () -> SnapshotCodec.deserialize text |> ok "validate" |> fun _ -> "ok"
        | "lower" -> fun () -> lower text |> ExecutionIrSerializer.serialize
        | task -> failwithf "unknown task %s" task
    if argv.Length < 3 then task () |> ignore
    // `check`: the output's SHA-256, so that the JIT, ReadyToRun and AOT builds can be compared.
    elif argv[2] = "check" then Console.WriteLine(Convert.ToHexString(Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes(task ()))))
    else
        let runs = int argv[2]
        // As many warm-up runs as measured ones, so that tiered JIT has promoted the hot methods.
        for _ in 1 .. runs do task () |> ignore
        let times = [| for _ in 1 .. runs -> let watch = Stopwatch.StartNew() in task () |> ignore; watch.Elapsed.TotalMilliseconds |]
        Array.sortInPlace times
        // Not printf: its float formatting needs code Native AOT does not generate.
        Console.WriteLine(times[runs / 2].ToString("F2", Globalization.CultureInfo.InvariantCulture))
    0
