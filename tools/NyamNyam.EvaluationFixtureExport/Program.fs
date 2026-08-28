open System
open System.IO
open System.Text
open NyamNyam.Core
open NyamNyam.Core.Tests

let private usage =
    """Usage:
  dotnet run --project tools/NyamNyam.EvaluationFixtureExport -- \
    --fixture <m0|m1> --output <snapshot.json>

The exported snapshots are produced from the exact MilestoneZeroGraph/MilestoneOneGraph fixture source used by tests.
"""

let private parseOptions (arguments: string list): Result<Map<string, string>, string> =
    let allowed = set [ "--fixture"; "--output" ]

    let rec loop values remaining =
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

let private requiredOption name options =
    match Map.tryFind name options with
    | Some value -> Ok value
    | None -> Error $"Missing required {name} argument."

let private createSnapshot fixtureName =
    match fixtureName with
    | "m0" -> Ok((MilestoneZeroGraph.create ()).Snapshot)
    | "m1" -> Ok((MilestoneOneGraph.create ()).Snapshot)
    | value -> Error $"Unsupported fixture '{value}'. Expected m0 or m1."

let private export fixtureName outputPath =
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
