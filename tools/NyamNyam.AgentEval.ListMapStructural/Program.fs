namespace NyamNyam.AgentEval.ListMapStructural

open System
open System.IO
open System.Text
open NyamNyam.Core

type ListMapStructuralAgentEvalArguments =
    { InputPath: string
      ProtocolVersion: string }

[<RequireQualifiedAccess>]
module ListMapStructuralAgentEvalCommandLine =
    let usage =
        $"""Usage:
  dotnet run --project tools/NyamNyam.AgentEval.ListMapStructural -- \
    --protocol {ListMapStructuralProtocol.version} \
    --input <snapshot.json>

The process reads one JSON command per stdin line and writes one JSON response per stdout line.
"""

    let private allowed = set [ "--protocol"; "--input" ]

    let private parsePairs (arguments: string list): Result<Map<string, string>, string> =
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

    let private required (name: string) (values: Map<string, string>) =
        match Map.tryFind name values with
        | Some value -> Ok value
        | None -> Error $"Missing required {name} argument."

    let parse (arguments: string list): Result<ListMapStructuralAgentEvalArguments, string> =
        parsePairs arguments
        |> Result.bind (fun values ->
            match required "--protocol" values, required "--input" values with
            | Error error, _
            | _, Error error -> Error error
            | Ok protocol, _ when protocol <> ListMapStructuralProtocol.version ->
                Error $"Unsupported protocol '{protocol}'. Expected '{ListMapStructuralProtocol.version}'."
            | Ok protocol, Ok input ->
                Ok
                    { InputPath = input
                      ProtocolVersion = protocol })

module Program =
    let private createSession
        (parsed: ListMapStructuralAgentEvalArguments)
        : Result<ListMapStructuralSession, string> =
        try
            let json = File.ReadAllText(parsed.InputPath, Encoding.UTF8)
            match SnapshotCodec.deserialize json with
            | Error error -> Error $"Snapshot load failed: {error}."
            | Ok snapshot ->
                match ListMapStructuralSession.create snapshot with
                | Ok session -> Ok session
                | Error issues -> Error $"ListMap structural agent workspace validation failed: {issues}."
        with
        | :? IOException as error -> Error $"I/O error: {error.Message}"
        | :? UnauthorizedAccessException as error -> Error $"Access error: {error.Message}"

    let private runSession (session: ListMapStructuralSession): int =
        let mutable running = true
        while running do
            let line = Console.In.ReadLine()
            if isNull line then
                running <- false
            else
                let response = session.ExecuteLine(line)
                Console.Out.WriteLine(response)
                Console.Out.Flush()
        0

    [<EntryPoint>]
    let main (argv: string array): int =
        match ListMapStructuralAgentEvalCommandLine.parse (argv |> Array.toList) with
        | Error error ->
            Console.Error.WriteLine(error)
            Console.Error.WriteLine(ListMapStructuralAgentEvalCommandLine.usage)
            2
        | Ok parsed ->
            match createSession parsed with
            | Error error ->
                Console.Error.WriteLine(error)
                2
            | Ok session -> runSession session
