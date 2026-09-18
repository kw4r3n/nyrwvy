namespace Nyrwvy.AgentEval.PureExpression

open System

[<RequireQualifiedAccess>]
module PureExpressionAgentEvalCommandLine =
    let usage =
        $"""Usage:
  dotnet run --project tools/Nyrwvy.AgentEval.PureExpression -- \
    --protocol {PureExpressionProtocol.version}

The process starts from the built-in deterministic pure-control-flow fixture, reads one JSON command per stdin line, and writes one JSON response per stdout line.
"""

    let parse (arguments: string list): Result<unit, string> =
        match arguments with
        | [ "--protocol"; protocol ] when protocol = PureExpressionProtocol.version -> Ok()
        | [ "--protocol"; protocol ] ->
            Error $"Unsupported protocol '{protocol}'. Expected '{PureExpressionProtocol.version}'."
        | [] -> Error "Missing required --protocol argument."
        | _ -> Error "Expected exactly --protocol <version>."

module Program =
    let private runSession (session: PureExpressionSession): int =
        let mutable running = true
        while running do
            let line = Console.In.ReadLine()
            if isNull line then
                running <- false
            else
                Console.Out.WriteLine(session.ExecuteLine(line))
                Console.Out.Flush()
        0

    [<EntryPoint>]
    let main (argv: string array): int =
        match PureExpressionAgentEvalCommandLine.parse (argv |> Array.toList) with
        | Error error ->
            Console.Error.WriteLine(error)
            Console.Error.WriteLine(PureExpressionAgentEvalCommandLine.usage)
            2
        | Ok() ->
            let fixture = PureExpressionEvalFixture.create ()
            match PureExpressionSession.create fixture with
            | Error issues ->
                Console.Error.WriteLine($"Pure-expression agent workspace validation failed: {issues}.")
                2
            | Ok session -> runSession session
