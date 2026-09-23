// Build: dotnet build tools/Nyrwvy.AuthoringSession -c Release
// Run: dotnet fsi scripts/measure-authoring-passes.fsx
// Full validation remains enabled in both modes; only scoped reuse differs.
#r "../src/Nyrwvy.Semantics/bin/Release/net10.0/Nyrwvy.Semantics.dll"
#r "../src/Nyrwvy.Execution/bin/Release/net10.0/Nyrwvy.Execution.dll"
#r "../src/Nyrwvy.Backend.CSharp/bin/Release/net10.0/Nyrwvy.Backend.CSharp.dll"
#r "../tools/Nyrwvy.AuthoringSession/bin/Release/net10.0/Nyrwvy.AuthoringSession.dll"

open System
open System.Diagnostics
open System.IO
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession

let unwrap = function Ok value -> value | Error error -> failwithf "%A" error
let intType = TypeRef.Primitive PrimitiveType.Int64
let functionId index = StableId.createForTesting<FunctionTag> $"function-profile-{index}"
let nodeId owner index = StableId.createForTesting<GraphNodeTag> $"node-profile-{owner}-{index}"
let fixture count =
    { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-profile") with
        Functions =
            [ for owner in 1 .. count ->
                { Id = functionId owner; DisplayName = $"function_{owner}"; Parameters = []
                  ResultType = intType; RequiredEffects = []
                  Body =
                    { Nodes =
                        [ for index in 0 .. 13 ->
                            { Id = nodeId owner index
                              Kind = if index = 0 then SemanticNodeKind.Int64Literal 1L
                                     else SemanticNodeKind.Int64WrappingAdd(nodeId owner (index - 1), nodeId owner 0) } ]
                      ResultNodeId = nodeId owner 13 } } ] }

type Phase = { Name: string; Milliseconds: float; Bytes: int64; Measurements: SnapshotComputationMeasurement list }
let phase reuse name action =
    use scope = SnapshotComputationScope.Start(reuse = reuse, measure = true)
    let timer = Stopwatch.StartNew()
    let start = GC.GetAllocatedBytesForCurrentThread()
    let result = action ()
    let bytes = GC.GetAllocatedBytesForCurrentThread() - start
    timer.Stop()
    result, { Name = name; Milliseconds = timer.Elapsed.TotalMilliseconds; Bytes = bytes; Measurements = scope.Measurements }

let run reuse operation snapshot =
    let directory = Path.Combine(Path.GetTempPath(), "nyrwvy-pass-profile-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory directory |> ignore
    try
        let path = Path.Combine(directory, "snapshot.json")
        File.WriteAllText(path, SnapshotCodec.serialize snapshot |> unwrap)
        use session = AuthoringSession.openExisting path |> unwrap
        let proposal, simulation = phase reuse "simulate" (fun () ->
            match operation with
            | "rename" -> session.SimulateOperations [ AgentOperationIntent.RenameDisplayName(AgentRenameTarget.Function(functionId 1), "renamed") ] |> unwrap
            | "create" -> session.SimulateCreateFunction
                                { ExpectedBaseSnapshotId = snapshot.Id; DisplayName = "created"; Parameters = []
                                  ResultType = intType; RequiredEffects = []; Body = ExpressionRecipe.Int64Literal 1L } |> unwrap
            | _ -> failwith "Unknown operation")
        let _, commit = phase reuse "commit" (fun () -> session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap)
        [ simulation; commit ]
    finally Directory.Delete(directory, true)

for reuse in [ false; true ] do
    for operation in [ "rename"; "create" ] do run reuse operation (fixture 20) |> ignore
printfn "functions,operation,reuse,phase,total_ms,total_allocated_bytes,computation,runs,hits,inclusive_ms,inclusive_allocated_bytes"
for count in [ 100; 400; 531 ] do
    let snapshot = fixture count
    for operation in [ "rename"; "create" ] do
        for reuse in [ false; true ] do
            let sample =
                [ for _ in 1 .. 3 -> run reuse operation snapshot ]
                |> List.sortBy (List.sumBy (fun phase -> phase.Milliseconds))
                |> List.item 1
            for phase in sample do
                for metric in phase.Measurements do
                    let milliseconds = float metric.ElapsedTicks * 1000.0 / float Stopwatch.Frequency
                    printfn "%d,%s,%b,%s,%.3f,%d,%s,%d,%d,%.3f,%d"
                        count operation reuse phase.Name phase.Milliseconds phase.Bytes metric.Name
                        metric.Computations metric.CacheHits milliseconds metric.AllocatedBytes
