open System
open System.IO
open System.Text
open NyamNyam.Core
open NyamNyam.Core.Tests

let private usage =
    $"""Usage:
  dotnet run --project tools/NyamNyam.EvaluationFixtureExport -- \
    --fixture <m0|m1|{M2StructuralEvaluationCorpus.readyFixtureName}|{M2StructuralEvaluationCorpus.openProtectedFixtureName}|{M2StructuralEvaluationCorpus.resolvedProtectedFixtureName}|{M2ListMapStructuralEvaluationCorpus.readyFixtureName}|{M2ListMapStructuralEvaluationCorpus.openProtectedFixtureName}|{M2ListMapStructuralEvaluationCorpus.resolvedProtectedFixtureName}> --output <snapshot.json>

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
    match fixtureName with
    | "m0" -> Ok((MilestoneZeroGraph.create ()).Snapshot)
    | "m1" -> Ok((MilestoneOneGraph.create ()).Snapshot)
    | value when value = M2StructuralEvaluationCorpus.readyFixtureName ->
        Ok((M2StructuralEvaluationCorpus.ready ()).Snapshot)
    | value when value = M2StructuralEvaluationCorpus.openProtectedFixtureName ->
        Ok((M2StructuralEvaluationCorpus.openProtected ()).Snapshot)
    | value when value = M2StructuralEvaluationCorpus.resolvedProtectedFixtureName ->
        Ok((M2StructuralEvaluationCorpus.resolvedProtected ()).Snapshot)
    | value when value = M2ListMapStructuralEvaluationCorpus.readyFixtureName ->
        Ok((M2ListMapStructuralEvaluationCorpus.ready ()).Snapshot)
    | value when value = M2ListMapStructuralEvaluationCorpus.openProtectedFixtureName ->
        Ok((M2ListMapStructuralEvaluationCorpus.openProtected ()).Snapshot)
    | value when value = M2ListMapStructuralEvaluationCorpus.resolvedProtectedFixtureName ->
        Ok((M2ListMapStructuralEvaluationCorpus.resolvedProtected ()).Snapshot)
    | value ->
        let expected =
            [ "m0"
              "m1"
              M2StructuralEvaluationCorpus.readyFixtureName
              M2StructuralEvaluationCorpus.openProtectedFixtureName
              M2StructuralEvaluationCorpus.resolvedProtectedFixtureName
              M2ListMapStructuralEvaluationCorpus.readyFixtureName
              M2ListMapStructuralEvaluationCorpus.openProtectedFixtureName
              M2ListMapStructuralEvaluationCorpus.resolvedProtectedFixtureName ]
            |> String.concat ", "
        Error $"Unsupported fixture '{value}'. Expected one of: {expected}."

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
