namespace NyamNyam.AgentEval.Structural

open System
open System.IO
open System.Text
open NyamNyam.Core

type StructuralAgentEvalArguments =
    { InputPath: string
      ProtocolVersion: string }

[<RequireQualifiedAccess>]
module StructuralAgentEvalCommandLine =
    let usage =
        $"""Usage:
  dotnet run --project tools/NyamNyam.AgentEval.Structural -- \
    --protocol {StructuralProtocol.version} \
    --input <snapshot.json>

The process reads one JSON command per stdin line and writes one JSON response per stdout line.
The executable is a separate M2 tool condition; it does not change the recorded M1 NyamNyam.AgentEval protocol.
"""

    let parse (arguments: string list): Result<StructuralAgentEvalArguments, string> =
        let rec pairs (values: Map<string, string>) (remaining: string list): Result<Map<string, string>, string> =
            match remaining with
            | [] -> Ok values
            | [ key ] -> Error $"Option '{key}' is missing its value."
            | key :: value :: rest when key = "--protocol" || key = "--input" ->
                if Map.containsKey key values then
                    Error $"Duplicate option '{key}'."
                elif value.StartsWith("--", StringComparison.Ordinal) then
                    Error $"Option '{key}' is missing its value."
                else
                    pairs (Map.add key value values) rest
            | key :: _ when key.StartsWith("--", StringComparison.Ordinal) -> Error $"Unsupported option '{key}'."
            | value :: _ -> Error $"Unexpected positional argument '{value}'."

        pairs Map.empty arguments
        |> Result.bind (fun values ->
            match Map.tryFind "--protocol" values, Map.tryFind "--input" values with
            | None, _ -> Error "Missing required --protocol argument."
            | _, None -> Error "Missing required --input argument."
            | Some protocol, _ when protocol <> StructuralProtocol.version ->
                Error $"Unsupported protocol '{protocol}'. Expected '{StructuralProtocol.version}'."
            | Some protocol, Some input ->
                Ok
                    { InputPath = input
                      ProtocolVersion = protocol })

module Program =
    let private createSession (inputPath: string): Result<StructuralSession, string> =
        try
            let json = File.ReadAllText(inputPath, Encoding.UTF8)
            match SnapshotCodec.deserialize json with
            | Error error -> Error $"Snapshot load failed: {error}."
            | Ok snapshot ->
                match StructuralSession.create snapshot with
                | Ok session -> Ok session
                | Error issues -> Error $"Structural agent workspace validation failed: {issues}."
        with
        | :? IOException as error -> Error $"I/O error: {error.Message}"
        | :? UnauthorizedAccessException as error -> Error $"Access error: {error.Message}"

    let private runSession (session: StructuralSession): int =
        let mutable running = true
        while running do
            let line = Console.In.ReadLine()
            if isNull line then
                running <- false
            else
                session.ExecuteLine(line) |> Console.Out.WriteLine
                Console.Out.Flush()
        0

    [<EntryPoint>]
    let main (argv: string array): int =
        match StructuralAgentEvalCommandLine.parse (argv |> Array.toList) with
        | Error error ->
            Console.Error.WriteLine(error)
            Console.Error.WriteLine(StructuralAgentEvalCommandLine.usage)
            2
        | Ok parsed ->
            match createSession parsed.InputPath with
            | Error error ->
                Console.Error.WriteLine(error)
                2
            | Ok session -> runSession session
