open System
open System.IO
open System.Text
open Nyrwvy.Core
open Nyrwvy.Core.Tests

/// Every exportable fixture by name; the usage text and the unsupported-fixture message list them in this order.
let private fixtures: (string * (unit -> Snapshot)) list =
    [ "m0", (fun () -> (MilestoneZeroGraph.create ()).Snapshot)
      "m1", (fun () -> (MilestoneOneGraph.create ()).Snapshot)
      M2StructuralEvaluationCorpus.readyFixtureName, (fun () -> (M2StructuralEvaluationCorpus.ready ()).Snapshot)
      M2StructuralEvaluationCorpus.openProtectedFixtureName, (fun () -> (M2StructuralEvaluationCorpus.openProtected ()).Snapshot)
      M2StructuralEvaluationCorpus.resolvedProtectedFixtureName, (fun () -> (M2StructuralEvaluationCorpus.resolvedProtected ()).Snapshot)
      M2ListMapStructuralEvaluationCorpus.readyFixtureName, (fun () -> (M2ListMapStructuralEvaluationCorpus.ready ()).Snapshot)
      M2ListMapStructuralEvaluationCorpus.openProtectedFixtureName, (fun () -> (M2ListMapStructuralEvaluationCorpus.openProtected ()).Snapshot)
      M2ListMapStructuralEvaluationCorpus.resolvedProtectedFixtureName, (fun () -> (M2ListMapStructuralEvaluationCorpus.resolvedProtected ()).Snapshot) ]

let private fixtureNames = fixtures |> List.map fst

let private usage =
    $"""Usage:
  dotnet run --project tools/Nyrwvy.EvaluationFixtureExport -- \
    --fixture <{String.concat "|" fixtureNames}> --output <snapshot.json>

The exported snapshots are produced from the exact fixture source linked into the regression test project.
"""

let private createSnapshot (fixtureName: string): Result<Snapshot, string> =
    match fixtures |> List.tryFind (fun (name, _) -> name = fixtureName) with
    | Some(_, create) -> Ok(create ())
    | None ->
        let expected = fixtureNames |> String.concat ", "
        Error $"Unsupported fixture '{fixtureName}'. Expected one of: {expected}."

let private export (fixtureName: string) (outputPath: string) =
    createSnapshot fixtureName
    |> Result.bind (fun snapshot ->
        SnapshotCodec.serialize snapshot
        |> Result.mapError (fun error -> $"Snapshot serialization failed: {error}."))
    |> Result.bind (ExportCli.writeUtf8 outputPath)

[<EntryPoint>]
let main argv =
    let result =
        ExportCli.parseOptions (set [ "--fixture"; "--output" ]) (argv |> Array.toList)
        |> Result.bind (fun options ->
            match ExportCli.requiredOption "--fixture" options, ExportCli.requiredOption "--output" options with
            | Ok fixtureName, Ok outputPath -> export fixtureName outputPath
            | Error error, _
            | _, Error error -> Error error)

    match result with
    | Ok outputPath ->
        Console.Out.WriteLine(outputPath)
        0
    | Error message ->
        Console.Error.WriteLine(message)
        Console.Error.WriteLine(usage)
        2
