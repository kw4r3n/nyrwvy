namespace NyamNyam.AgentEval.ListMapStructural

open System
open System.IO
open System.Text
open System.Text.Json
open NyamNyam.Core

type ListMapStructuralAgentEvalArguments =
    { InputPath: string
      ProtocolVersion: string
      HarnessConfig: ListMapStructuralHarnessConfig option }

[<RequireQualifiedAccess>]
module ListMapStructuralAgentEvalCommandLine =
    let usage =
        $"""Usage:
  dotnet run --project tools/NyamNyam.AgentEval.ListMapStructural -- \
    --protocol {ListMapStructuralProtocol.version} \
    --input <snapshot.json> \
    [--artifact-dir <directory> --fixture <m2-listmap-ready|m2-listmap-open-protected|m2-listmap-resolved-protected> --task-id <M2-L1..M2-L10>] \
    [--stale-perturbation m2-l10] \
    [--model <model>] [--provider-runtime <runtime>] [--reasoning-config <config>] \
    [--opencode-version <version>] [--prompt-revision <revision>] [--tool-revision <revision>] \
    [--repository-revision <revision>]

The process reads one JSON command per stdin line and writes one JSON response per stdout line.
Controlled-run options are harness-owned startup configuration and do not change the agent-visible m2-listmap-structural-v1 command schema.
"""

    let private allowed =
        set
            [ "--protocol"
              "--input"
              "--artifact-dir"
              "--fixture"
              "--task-id"
              "--stale-perturbation"
              "--model"
              "--provider-runtime"
              "--reasoning-config"
              "--opencode-version"
              "--prompt-revision"
              "--tool-revision"
              "--repository-revision" ]

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

    let private required (name: string) (values: Map<string, string>): Result<string, string> =
        match Map.tryFind name values with
        | Some value -> Ok value
        | None -> Error $"Missing required {name} argument."

    let private metadata (values: Map<string, string>): ListMapStructuralHarnessRunMetadata =
        { Model = Map.tryFind "--model" values
          ProviderRuntime = Map.tryFind "--provider-runtime" values
          ReasoningConfig = Map.tryFind "--reasoning-config" values
          OpenCodeVersion = Map.tryFind "--opencode-version" values
          PromptRevision = Map.tryFind "--prompt-revision" values
          ToolRevision = Map.tryFind "--tool-revision" values
          RepositoryRevision = Map.tryFind "--repository-revision" values }

    let parse (arguments: string list): Result<ListMapStructuralAgentEvalArguments, string> =
        parsePairs arguments
        |> Result.bind (fun values ->
            match required "--protocol" values, required "--input" values with
            | Error error, _
            | _, Error error -> Error error
            | Ok protocol, _ when protocol <> ListMapStructuralProtocol.version ->
                Error $"Unsupported protocol '{protocol}'. Expected '{ListMapStructuralProtocol.version}'."
            | Ok protocol, Ok input ->
                let artifact = Map.tryFind "--artifact-dir" values
                let fixture = Map.tryFind "--fixture" values
                let taskId = Map.tryFind "--task-id" values
                let stale = Map.tryFind "--stale-perturbation" values
                let anyHarnessMetadata =
                    [ "--model"
                      "--provider-runtime"
                      "--reasoning-config"
                      "--opencode-version"
                      "--prompt-revision"
                      "--tool-revision"
                      "--repository-revision" ]
                    |> List.exists (fun key -> Map.containsKey key values)

                match artifact, fixture, taskId with
                | None, None, None when Option.isNone stale && not anyHarnessMetadata ->
                    Ok
                        { InputPath = input
                          ProtocolVersion = protocol
                          HarnessConfig = None }
                | Some artifactDirectory, Some fixtureName, Some task ->
                    match stale with
                    | Some value when value <> "m2-l10" ->
                        Error $"Unsupported stale perturbation '{value}'. Expected m2-l10."
                    | Some _ when task <> "M2-L10" ->
                        Error "--stale-perturbation m2-l10 is permitted only for task M2-L10."
                    | None when task = "M2-L10" ->
                        Error "Task M2-L10 requires --stale-perturbation m2-l10."
                    | _ ->
                        Ok
                            { InputPath = input
                              ProtocolVersion = protocol
                              HarnessConfig =
                                Some
                                    { ArtifactDirectory = artifactDirectory
                                      FixtureName = fixtureName
                                      TaskId = task
                                      EnableStalePerturbation = Option.isSome stale
                                      RunMetadata = metadata values } }
                | _ ->
                    Error "Controlled ListMap evaluation requires --artifact-dir, --fixture, and --task-id together.")

module Program =
    let private successfulProposalTarget (line: string) (response: string): FunctionId option =
        try
            use requestDocument = JsonDocument.Parse(line)
            use responseDocument = JsonDocument.Parse(response)
            let requestRoot = requestDocument.RootElement
            let responseRoot = responseDocument.RootElement
            if requestRoot.ValueKind <> JsonValueKind.Object
               || requestRoot.GetProperty("command").GetString() <> "simulateListMapV1" then
                None
            elif not (responseRoot.GetProperty("ok").GetBoolean()) then
                None
            elif responseRoot.GetProperty("result").GetProperty("status").GetString() <> "proposed" then
                None
            else
                let text = requestRoot.GetProperty("targetFunctionId").GetString()
                StableId.tryCreate<FunctionTag> text |> Result.toOption
        with
        | _ -> None

    let private createSession
        (parsed: ListMapStructuralAgentEvalArguments)
        : Result<ListMapStructuralSession * ListMapStructuralHarness option, string> =
        try
            let json = File.ReadAllText(parsed.InputPath, Encoding.UTF8)
            match SnapshotCodec.deserialize json with
            | Error error -> Error $"Snapshot load failed: {error}."
            | Ok snapshot ->
                match ListMapStructuralSession.create snapshot with
                | Error issues -> Error $"ListMap structural agent workspace validation failed: {issues}."
                | Ok session ->
                    match parsed.HarnessConfig with
                    | None -> Ok(session, None)
                    | Some config ->
                        match ListMapStructuralHarness.Create(snapshot, config) with
                        | Ok harness -> Ok(session, Some harness)
                        | Error error -> Error $"ListMap structural evaluation harness initialization failed: {error}"
        with
        | :? IOException as error -> Error $"I/O error: {error.Message}"
        | :? UnauthorizedAccessException as error -> Error $"Access error: {error.Message}"

    let private runSession
        (session: ListMapStructuralSession)
        (harness: ListMapStructuralHarness option)
        : int =
        let mutable running = true
        let mutable finalizationOk = true
        try
            while running do
                let line = Console.In.ReadLine()
                if isNull line then
                    running <- false
                else
                    let response = session.ExecuteLine(line)
                    let proposalTarget = successfulProposalTarget line response

                    match harness with
                    | None -> ()
                    | Some controlled ->
                        controlled.RecordEvent(line, response)
                        match session.LastPublication with
                        | Some publication -> controlled.VerifyCommit(session.CurrentWorkspace, publication)
                        | None -> ()

                    Console.Out.WriteLine(response)
                    Console.Out.Flush()

                    match harness, proposalTarget with
                    | Some controlled, Some targetFunctionId when controlled.Config.EnableStalePerturbation ->
                        let advanced = controlled.MaybeApplyStalePerturbation(session.CurrentWorkspace, targetFunctionId)
                        session.ReplaceWorkspace advanced
                    | _ -> ()
        finally
            match harness with
            | None -> ()
            | Some controlled ->
                finalizationOk <-
                    controlled.Finalize(
                        session.CurrentWorkspace,
                        session.Usage,
                        session.PendingProposalCount
                    )

        if finalizationOk then 0 else 1

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
            | Ok(session, harness) -> runSession session harness
