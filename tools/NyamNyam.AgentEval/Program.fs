open System
open System.IO
open System.Text
open NyamNyam.Core
open NyamNyam.AgentEval

type AgentEvalArguments =
    { InputPath: string
      StalePerturbation: StalePerturbation option
      VerificationFixture: VerificationFixture option
      ArtifactDirectory: string option
      RunMetadata: SessionRunMetadata }

[<RequireQualifiedAccess>]
module AgentEvalCommandLine =
    let usage =
        """Usage:
  dotnet run --project tools/NyamNyam.AgentEval -- \
    --input <snapshot.json> \
    [--stale-perturbation-type-id <type-id> --stale-perturbation-name <new-display-name>] \
    [--verification-fixture <m0|m1>] \
    [--artifact-dir <directory>] \
    [--model <model>] \
    [--provider-runtime <provider/runtime>] \
    [--reasoning-config <configuration>] \
    [--prompt-revision <revision>] \
    [--tool-revision <revision>]

The process reads one JSON command per stdin line and writes one JSON response per stdout line.
The stale-perturbation, verification, artifact, and metadata options are harness-owned startup configuration and are not exposed as agent protocol commands.
"""

    let private parseOptions (arguments: string list): Result<Map<string, string>, string> =
        let allowed =
            set
                [ "--input"
                  "--stale-perturbation-type-id"
                  "--stale-perturbation-name"
                  "--verification-fixture"
                  "--artifact-dir"
                  "--model"
                  "--provider-runtime"
                  "--reasoning-config"
                  "--prompt-revision"
                  "--tool-revision" ]

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

    let private requiredOption name options =
        match Map.tryFind name options with
        | Some value -> Ok value
        | None -> Error $"Missing required {name} argument."

    let private runMetadata options =
        { Model = Map.tryFind "--model" options
          ProviderRuntime = Map.tryFind "--provider-runtime" options
          ReasoningConfig = Map.tryFind "--reasoning-config" options
          PromptRevision = Map.tryFind "--prompt-revision" options
          ToolRevision = Map.tryFind "--tool-revision" options }

    let parse (arguments: string list): Result<AgentEvalArguments, string> =
        parseOptions arguments
        |> Result.bind (fun options ->
            requiredOption "--input" options
            |> Result.bind (fun inputPath ->
                let verificationFixture =
                    match Map.tryFind "--verification-fixture" options with
                    | None -> Ok None
                    | Some value -> VerificationFixture.parse value |> Result.map Some

                verificationFixture
                |> Result.bind (fun verificationFixture ->
                    match verificationFixture, Map.tryFind "--artifact-dir" options with
                    | Some _, None -> Error "--verification-fixture requires --artifact-dir."
                    | _ ->
                        match
                            Map.tryFind "--stale-perturbation-type-id" options,
                            Map.tryFind "--stale-perturbation-name" options
                        with
                        | None, None ->
                            Ok
                                { InputPath = inputPath
                                  StalePerturbation = None
                                  VerificationFixture = verificationFixture
                                  ArtifactDirectory = Map.tryFind "--artifact-dir" options
                                  RunMetadata = runMetadata options }
                        | Some typeIdText, Some newDisplayName ->
                            match StableId.tryCreate<TypeTag> typeIdText with
                            | Error error -> Error $"Invalid stale-perturbation type ID: {error}."
                            | Ok typeId ->
                                Ok
                                    { InputPath = inputPath
                                      StalePerturbation =
                                        Some
                                            { TypeId = typeId
                                              NewDisplayName = newDisplayName }
                                      VerificationFixture = verificationFixture
                                      ArtifactDirectory = Map.tryFind "--artifact-dir" options
                                      RunMetadata = runMetadata options }
                        | _ ->
                            Error
                                "--stale-perturbation-type-id and --stale-perturbation-name must be supplied together.")))

let private createSession (parsed: AgentEvalArguments) =
    try
        let json = File.ReadAllText(parsed.InputPath, Encoding.UTF8)
        match SnapshotCodec.deserialize json with
        | Error error -> Error $"Snapshot load failed: {error}."
        | Ok snapshot ->
            let config =
                { SessionConfig.defaultValue with
                    StalePerturbation = parsed.StalePerturbation
                    VerificationFixture = parsed.VerificationFixture
                    RunMetadata = parsed.RunMetadata }

            match Session.createWithArtifacts snapshot config parsed.ArtifactDirectory with
            | Ok session -> Ok session
            | Error(SessionCreationError.WorkspaceValidation issues) ->
                Error $"Agent workspace validation failed: {issues}."
            | Error(SessionCreationError.ArtifactInitializationFailed error) ->
                Error $"Artifact initialization failed: {error}."
            | Error(SessionCreationError.InvalidConfiguration error) ->
                Error $"Invalid session configuration: {error}."
    with
    | :? IOException as error -> Error $"I/O error: {error.Message}"
    | :? UnauthorizedAccessException as error -> Error $"Access error: {error.Message}"

let private runSession (session: Session) =
    try
        let mutable running = true
        while running do
            let line = Console.In.ReadLine()
            if isNull line then
                running <- false
            else
                session.ExecuteLine(line) |> Console.Out.WriteLine
                Console.Out.Flush()
    finally
        session.Finalize() |> ignore

    0

[<EntryPoint>]
let main argv =
    match argv |> Array.toList |> AgentEvalCommandLine.parse with
    | Error message ->
        Console.Error.WriteLine(message)
        Console.Error.WriteLine(AgentEvalCommandLine.usage)
        2
    | Ok parsed ->
        match createSession parsed with
        | Error message ->
            Console.Error.WriteLine(message)
            2
        | Ok session -> runSession session
