namespace NyamNyam.Tools.AuthoringSession

open System

[<RequireQualifiedAccess>]
type private StartupMode =
    | Open
    | Create

module private CommandLine =
    let usage = "Usage: NyamNyam.AuthoringSession <open|create> --snapshot <snapshot-path>"

    let parse = function
        | [ "open"; "--snapshot"; path ] -> Ok(StartupMode.Open, path)
        | [ "create"; "--snapshot"; path ] -> Ok(StartupMode.Create, path)
        | _ -> Error usage

[<EntryPoint>]
let main argv =
    match CommandLine.parse (argv |> Array.toList) with
    | Error error -> Console.Error.WriteLine(error); 2
    | Ok(mode, path) ->
        let result =
            match mode with
            | StartupMode.Open -> AuthoringSession.openExisting path
            | StartupMode.Create -> AuthoringSession.createNew path
        match result with
        | Error error -> Console.Error.WriteLine(sprintf "%A" error); 2
        | Ok session ->
            use session = session
            let mutable running = true
            while running do
                match Console.In.ReadLine() with
                | null -> running <- false
                | line when String.IsNullOrWhiteSpace line -> ()
                | line -> Console.Out.WriteLine(AuthoringProtocol.executeLine session line); Console.Out.Flush()
            0
