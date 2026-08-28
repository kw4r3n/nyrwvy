open System
open System.IO
open System.Text
open NyamNyam.Core
open NyamNyam.AgentEval

type private Arguments =
    { InputPath: string
      StalePerturbation: StalePerturbation option }

let private usage =
    """Usage:
  dotnet run --project tools/NyamNyam.AgentEval -- \
    --input <snapshot.json> \
    [--stale-perturbation-type-id <type-id> --stale-perturbation-name <new-display-name>]

The process reads one JSON command per stdin line and writes one JSON response per stdout line.
The stale-perturbation options are harness-owned startup configuration and are not exposed as agent protocol commands.
"""

let private parseOptions (arguments: string list): Result<Map<string, string>, string> =
    let allowed =
        set
            [ "--input"
              "--stale-perturbation-type-id"
              "--stale-perturbation-name" ]

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

let private parseArguments arguments =
    parseOptions arguments
    |> Result.bind (fun options ->
        requiredOption "--input" options
        |> Result.bind (fun inputPath ->
            match
                Map.tryFind "--stale-perturbation-type-id" options,
                Map.tryFind "--stale-perturbation-name" options
            with
            | None, None ->
                Ok
                    { InputPath = inputPath
                      StalePerturbation = None }
            | Some typeIdText, Some newDisplayName ->
                match StableId.tryCreate<TypeTag> typeIdText with
                | Error error -> Error $"Invalid stale-perturbation type ID: {error}."
                | Ok typeId ->
                    Ok
                        { InputPath = inputPath
                          StalePerturbation =
                            Some
                                { TypeId = typeId
                                  NewDisplayName = newDisplayName } }
            | _ ->
                Error
                    "--stale-perturbation-type-id and --stale-perturbation-name must be supplied together."))

let private createSession (parsed: Arguments) =
    try
        let json = File.ReadAllText(parsed.InputPath, Encoding.UTF8)
        match SnapshotCodec.deserialize json with
        | Error error -> Error $"Snapshot load failed: {error}."
        | Ok snapshot ->
            let config =
                { SessionConfig.defaultValue with
                    StalePerturbation = parsed.StalePerturbation }

            match Session.create snapshot config with
            | Ok session -> Ok session
            | Error issues -> Error $"Agent workspace validation failed: {issues}."
    with
    | :? IOException as error -> Error $"I/O error: {error.Message}"
    | :? UnauthorizedAccessException as error -> Error $"Access error: {error.Message}"

let private runSession (session: Session) =
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
let main argv =
    match argv |> Array.toList |> parseArguments with
    | Error message ->
        Console.Error.WriteLine(message)
        Console.Error.WriteLine(usage)
        2
    | Ok parsed ->
        match createSession parsed with
        | Error message ->
            Console.Error.WriteLine(message)
            2
        | Ok session -> runSession session
