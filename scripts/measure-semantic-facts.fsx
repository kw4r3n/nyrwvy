// Build first: dotnet build src/Nyrwvy.Execution/Nyrwvy.Execution.fsproj -c Release
// Run: dotnet fsi scripts/measure-semantic-facts.fsx
// Descriptive timings only; routine regressions use allocation bounds instead.
#r "../src/Nyrwvy.Semantics/bin/Release/net10.0/Nyrwvy.Semantics.dll"
#r "../src/Nyrwvy.Execution/bin/Release/net10.0/Nyrwvy.Execution.dll"

open System
open System.Diagnostics
open Nyrwvy.Core

let intType = TypeRef.Primitive PrimitiveType.Int64
let listType = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ intType ])
let nodeId functionIndex index = StableId.createForTesting<GraphNodeTag> $"node-{functionIndex}-{index}"

let makeFunction sharedLists depth functionIndex: SemanticFunction =
    { Id = StableId.createForTesting<FunctionTag> $"function-{functionIndex}"
      DisplayName = $"function_{functionIndex}"
      Parameters = []
      ResultType = if sharedLists then listType else intType
      RequiredEffects = []
      Body =
        { Nodes =
            [ for index in 0 .. depth ->
                { Id = nodeId functionIndex index
                  Kind =
                    match sharedLists, index with
                    | true, 0 -> SemanticNodeKind.ListEmpty intType
                    | false, 0 -> SemanticNodeKind.Int64Literal 1L
                    | true, _ ->
                        SemanticNodeKind.ListConcat(nodeId functionIndex (index - 1), nodeId functionIndex (index - 1))
                    | false, _ ->
                        SemanticNodeKind.Int64WrappingAdd(nodeId functionIndex (index - 1), nodeId functionIndex 0) } ]
          ResultNodeId = nodeId functionIndex depth } }

let snapshot functions =
    { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-profile") with Functions = functions }

let unwrap = function
    | Ok value -> value
    | Error error -> failwithf "%A" error

let validate snapshot =
    match SnapshotValidator.validate snapshot with
    | [] -> ()
    | issues -> failwithf "%A" issues

let measure label action =
    action () // JIT / warm-up outside samples
    let samples =
        [ for _ in 1 .. 5 ->
            let timer = Stopwatch.StartNew()
            let before = GC.GetAllocatedBytesForCurrentThread()
            action ()
            let allocated = GC.GetAllocatedBytesForCurrentThread() - before
            timer.Stop()
            timer.Elapsed.TotalMilliseconds, allocated ]
    let milliseconds = samples |> List.map fst |> List.sort |> List.item 2
    let allocated = samples |> List.map snd |> List.sort |> List.item 2
    printfn "%s,%.3f,%d" label milliseconds allocated

printfn "case,median_ms,median_allocated_bytes"
for count in [ 100; 200; 400; 531 ] do
    let input = snapshot [ for index in 1 .. count -> makeFunction false 13 index ]
    measure $"validate-{count}-functions" (fun () -> validate input)
    measure $"fingerprint-{count}-functions" (fun () -> SnapshotFingerprint.compute input |> unwrap |> ignore)
    measure $"typed-ir-{count}-functions" (fun () -> TypedSemanticIr.project input |> unwrap |> ignore)

for depth in [ 12; 16; 20 ] do
    let functionEntity = makeFunction true depth 0
    let input = snapshot [ functionEntity ]
    measure $"resolve-{depth + 1}-shared-nodes" (fun () ->
        let resolve = SemanticFacts.createNodeTypeResolver input functionEntity
        if resolve functionEntity.Body.ResultNodeId <> Some listType then failwith "Unexpected node type")
    measure $"validate-{depth + 1}-shared-nodes" (fun () -> validate input)
