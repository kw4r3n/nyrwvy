open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Security.Cryptography
open System.Diagnostics
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession

let ok = function Ok x -> x | Error e -> failwithf "%A" e
let repo, output, sizes, samples = fsi.CommandLineArgs[1], fsi.CommandLineArgs[2], fsi.CommandLineArgs[3], int fsi.CommandLineArgs[4]
let measureScopes = if fsi.CommandLineArgs.Length > 5 then bool.Parse fsi.CommandLineArgs[5] else true
let expectProbe = if fsi.CommandLineArgs.Length > 6 then bool.Parse fsi.CommandLineArgs[6] else false
let probe = typeof<Snapshot>.Assembly.GetType("Nyrwvy.Core.AllocationProbe")
if expectProbe && isNull probe then failwith "instrumented build is missing AllocationProbe"
let benchmarkProcess = Process.GetCurrentProcess()
printfn "runtime stopwatchFrequency=%d serverGC=%b scopeCounters=%b instrumented=%b" Stopwatch.Frequency System.Runtime.GCSettings.IsServerGC measureScopes (not (isNull probe))
let resetProbe () = if not (isNull probe) then probe.GetMethod("reset").Invoke(null, [||]) |> ignore
let probeMeasurements () =
    if isNull probe then []
    else probe.GetMethod("results").Invoke(null, [||]) :?> SnapshotComputationMeasurement list
let mib bytes = float bytes / 1048576.0
let baseJson =
    use scope = SnapshotComputationScope.Start()
    File.ReadAllText(repo + "/examples/verification-planner/planner.snapshot.nyrec")
    |> RecordCodec.deserialize |> ok |> SnapshotCodec.serialize |> ok
let uuidRegex = Regex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")
let synthetic copies =
    use scope = SnapshotComputationScope.Start()
    use doc = JsonDocument.Parse baseJson
    let root = doc.RootElement
    let raw = root.GetProperty("functions").GetRawText()
    let shared =
        [ "types"; "effects"; "capabilities"; "obligations" ]
        |> List.map (fun g -> root.GetProperty(g).GetRawText()) |> String.concat ""
        |> uuidRegex.Matches |> Seq.map (fun m -> m.Value) |> Set.ofSeq
    let inner = raw.Substring(1, raw.Length - 2)
    let copy k = uuidRegex.Replace(inner, fun m ->
        if shared.Contains m.Value then m.Value
        else Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"nyrec-profile/{k}/{m.Value}")).AsSpan(0, 16)).ToString())
    let json = baseJson.Replace(raw, "[" + String.Join(",", [ for k in 1 .. copies -> copy k ]) + "]")
    let snapshot = SnapshotCodec.deserialize json |> ok
    if snapshot.Functions.Length <> copies * root.GetProperty("functions").GetArrayLength() then failwith "wrong synthetic function count"
    snapshot

type Measurement =
    { Copies: int; Format: string; Operation: string; Sample: int
      ElapsedMs: float; AllocatedBytes: int64; Gen0: int; Gen1: int; Gen2: int
      ProcessAllocatedBytes: int64; ProcessCpuMs: float; GCPauseMs: float
      Scope: SnapshotComputationMeasurement list; Phases: SnapshotComputationMeasurement list }

let log = new StreamWriter(output, false, UTF8Encoding(false))
let measure copies format operation sample action =
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect()
    use scope = SnapshotComputationScope.Start(measure = measureScopes)
    resetProbe ()
    let gcBefore = [| for g in 0..2 -> GC.CollectionCount g |]
    let processBefore = GC.GetTotalAllocatedBytes(true)
    let cpuBefore = benchmarkProcess.TotalProcessorTime.TotalMilliseconds
    let pauseBefore = GC.GetTotalPauseDuration().TotalMilliseconds
    let before = GC.GetAllocatedBytesForCurrentThread()
    let sw = Stopwatch.StartNew()
    let value = action ()
    sw.Stop()
    let allocated = GC.GetAllocatedBytesForCurrentThread() - before
    let result =
        { Copies = copies; Format = format; Operation = operation; Sample = sample
          ElapsedMs = sw.Elapsed.TotalMilliseconds; AllocatedBytes = allocated
          ProcessAllocatedBytes = GC.GetTotalAllocatedBytes(true) - processBefore
          ProcessCpuMs = benchmarkProcess.TotalProcessorTime.TotalMilliseconds - cpuBefore
          GCPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds - pauseBefore
          Gen0 = GC.CollectionCount(0) - gcBefore[0]; Gen1 = GC.CollectionCount(1) - gcBefore[1]; Gen2 = GC.CollectionCount(2) - gcBefore[2]
          Scope = scope.Measurements; Phases = probeMeasurements () }
    log.WriteLine(JsonSerializer.Serialize result)
    log.Flush()
    printfn "%s copies=%d %s sample=%d %.1fms %.1fMiB" format copies operation sample result.ElapsedMs (mib allocated)
    value

let work = Path.Combine(repo, "evaluation/nyrec-scaling/.work", Guid.NewGuid().ToString("N"))
Directory.CreateDirectory work |> ignore
try
    for copies in sizes.Split(',') |> Array.map int do
        let snapshot = synthetic copies
        let json, records =
            use scope = SnapshotComputationScope.Start()
            SnapshotCodec.serialize snapshot |> ok, RecordCodec.serialize snapshot |> ok
        let checkRoundtrip decode source =
            use scope = SnapshotComputationScope.Start()
            let decoded = decode source |> ok
            if (SnapshotCodec.serialize decoded |> ok) <> json then failwith "canonical JSON round trip failed"
            if (RecordCodec.serialize decoded |> ok) <> records then failwith "canonical records round trip failed"
        checkRoundtrip SnapshotCodec.deserialize json
        checkRoundtrip RecordCodec.deserialize records
        printfn "fixture copies=%d functions=%d nodes=%d recordBytes=%d jsonBytes=%d" copies snapshot.Functions.Length (snapshot.Functions |> List.sumBy (fun f -> (ValueGraph.collectNodes f.Body).Length)) (Encoding.UTF8.GetByteCount records) (Encoding.UTF8.GetByteCount json)
        for sample in -1 .. samples - 1 do
            measure copies "model" "validate-cold" sample (fun () ->
                let issues = SnapshotValidator.validate { snapshot with Id = snapshot.Id }
                if not issues.IsEmpty then failwithf "validation failed: %A" issues) |> ignore
            measure copies "json" "encode-cold" sample (fun () -> SnapshotCodec.serialize { snapshot with Id = snapshot.Id } |> ok) |> ignore
            measure copies "nyrec" "encode-from-json" sample (fun () -> RecordCodec.ofJson json) |> ignore
            measure copies "json" "decode" sample (fun () -> SnapshotCodec.deserialize json |> ok) |> ignore
            measure copies "nyrec" "decode" sample (fun () -> RecordCodec.deserialize records |> ok) |> ignore
        for format, source in [ "nyrec", records; "json", json ] do
            let path = Path.Combine(work, $"{copies}.{format}")
            for sample in -1 .. samples - 1 do
                do
                    use file = new FileStream(path, FileMode.Create, FileAccess.Write)
                    let bytes = UTF8Encoding(false).GetBytes source
                    file.Write(bytes, 0, bytes.Length)
                    file.Flush(true)
                let originalId, committedId, functionId, name =
                    use session = measure copies format "open" sample (fun () -> AuthoringSession.openExisting path |> ok)
                    let originalId = session.SnapshotId
                    let functionId = session.Declarations().Functions.Head.Id
                    let name = $"profile_{sample}"
                    let proposal = measure copies format "simulate-rename" sample (fun () ->
                        session.SimulateOperations [ AgentOperationIntent.RenameDisplayName(AgentRenameTarget.Function functionId, name) ] |> ok)
                    let receipt = measure copies format "commit-rename" sample (fun () ->
                        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> ok)
                    if receipt.PublishedSnapshotId = originalId || session.SnapshotId <> receipt.PublishedSnapshotId then failwith "commit did not publish"
                    if not (session.PendingProposals().IsEmpty) then failwith "committed proposal still pending"
                    originalId, receipt.PublishedSnapshotId, functionId, name
                do
                    use scope = SnapshotComputationScope.Start()
                    let decoded = (if format = "nyrec" then RecordCodec.deserialize (File.ReadAllText path) |> ok else SnapshotCodec.deserialize (File.ReadAllText path) |> ok)
                    if decoded.Id <> committedId || decoded.ParentId <> Some originalId then failwith "wrong persisted lineage"
                    if decoded.Functions.Length <> snapshot.Functions.Length then failwith "commit lost functions"
                    if (decoded.Functions |> List.find (fun f -> f.Id = functionId)).DisplayName <> name then failwith "rename not persisted"
                    use reopened = AuthoringSession.openExisting path |> ok
                    if reopened.SnapshotId <> committedId then failwith "reopen lost commit"
finally
    Directory.Delete(work, true)
