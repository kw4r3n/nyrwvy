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

let private parseOptions (arguments: string list): Result<Map<string, string>, string> =
    let allowed = set [ "--fixture"; "--output" ]

    let rec loop (values: Map<string, string>) (remaining: string list) =
        match remaining with
        | [] -> Ok values
        | [ key ] -> Error $"Option '{key}' is missing its value."
        | key :: value :: rest ->
            if not (key.StartsWith("--", StringComparison.Ordinal)) then
                Error $"Unexpected positional argument '{key}'."
            elif not (Set.contains key allowed) then
                Error $"Unsupported option '{key}'."
            elif Map.containsKey key values then
                Error $"Duplicate option '{key}'."
            elif value.StartsWith("--", StringComparison.Ordinal) then
                Error $"Option '{key}' is missing its value."
            else
                loop (Map.add key value values) rest

    loop Map.empty arguments

let private requiredOption (name: string) (options: Map<string, string>) =
    match Map.tryFind name options with
    | Some value -> Ok value
    | None -> Error $"Missing required {name} argument."

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
    |> Result.bind (fun json ->
        try
            let fullPath = Path.GetFullPath(outputPath)
            let directory = Path.GetDirectoryName(fullPath)
            if not (String.IsNullOrEmpty directory) then
                Directory.CreateDirectory(directory) |> ignore
            File.WriteAllText(fullPath, json, UTF8Encoding(false))
            Ok fullPath
        with
        | :? IOException as error -> Error $"I/O error: {error.Message}"
        | :? UnauthorizedAccessException as error -> Error $"Access error: {error.Message}")

[<EntryPoint>]
let main argv =
    let result =
        parseOptions (argv |> Array.toList)
        |> Result.bind (fun options ->
            match requiredOption "--fixture" options, requiredOption "--output" options with
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
