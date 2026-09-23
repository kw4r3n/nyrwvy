module Nyrwvy.Tools.AuthoringSession.Program

open System
open Nyrwvy.Tools.AuthoringSession

[<RequireQualifiedAccess>]
type private StartupMode =
    | Open
    | Create

module private CommandLine =
    let usage = "Usage: Nyrwvy.AuthoringSession <open|create> --snapshot <snapshot-path> [--dependency <snapshot-path> (create only)]"

    let parse (arguments: string list): Result<StartupMode * string * string option, string> =
        match arguments with
        | [ "open"; "--snapshot"; path ] -> Ok(StartupMode.Open, path, None)
        | [ "create"; "--snapshot"; path ] -> Ok(StartupMode.Create, path, None)
        | [ "create"; "--snapshot"; path; "--dependency"; dependency ] -> Ok(StartupMode.Create, path, Some dependency)
        | _ -> Error usage

[<EntryPoint>]
let main argv =
    match CommandLine.parse (argv |> Array.toList) with
    | Error error ->
        Console.Error.WriteLine(error)
        2
    | Ok(mode, path, dependency) ->
        let result =
            match mode with
            | StartupMode.Open -> AuthoringSession.openExisting path
            | StartupMode.Create -> AuthoringSession.createNewWithDependency path dependency
        match result with
        | Error error ->
            Console.Error.WriteLine(sprintf "%A" error)
            2
        | Ok session ->
            use session = session
            let mutable running = true
            while running do
                match Console.In.ReadLine() with
                | null -> running <- false
                | line when String.IsNullOrWhiteSpace line -> ()
                | line ->
                    Console.Out.WriteLine(AuthoringProtocol.executeLine session line)
                    Console.Out.Flush()
            0
