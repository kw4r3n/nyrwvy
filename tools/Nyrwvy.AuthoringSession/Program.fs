module Nyrwvy.Tools.AuthoringSession.Program

open System
open Nyrwvy.Tools.AuthoringSession

[<RequireQualifiedAccess>]
type private StartupMode =
    | Open
    | Create
    | Pin
    | Upgrade of source: string * replaces: string option * expectedFingerprint: string

module private CommandLine =
    let usage = "Usage: Nyrwvy.AuthoringSession <open|create|pin> --snapshot <snapshot-path> [--dependency <snapshot-path> ...] (create may name several dependencies; pin takes at most one same-content source)\n       Nyrwvy.AuthoringSession upgrade --snapshot <snapshot-path> --dependency <new-version-path> --expect <reviewed-fingerprint> [--replaces <snapshot-id>]"

    let rec private dependencies collected = function
        | [] -> Ok(List.rev collected)
        | "--dependency" :: path :: rest -> dependencies (path :: collected) rest
        | _ -> Error usage

    let parse (arguments: string list): Result<StartupMode * string * string list, string> =
        match arguments with
        | [ "open"; "--snapshot"; path ] -> Ok(StartupMode.Open, path, [])
        | "create" :: "--snapshot" :: path :: rest -> dependencies [] rest |> Result.map (fun paths -> StartupMode.Create, path, paths)
        | [ "pin"; "--snapshot"; path ] -> Ok(StartupMode.Pin, path, [])
        | [ "pin"; "--snapshot"; path; "--dependency"; dependency ] -> Ok(StartupMode.Pin, path, [ dependency ])
        | [ "upgrade"; "--snapshot"; path; "--dependency"; source; "--expect"; fingerprint ] ->
            Ok(StartupMode.Upgrade(source, None, fingerprint), path, [])
        | [ "upgrade"; "--snapshot"; path; "--dependency"; source; "--expect"; fingerprint; "--replaces"; replaces ] ->
            Ok(StartupMode.Upgrade(source, Some replaces, fingerprint), path, [])
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
            | StartupMode.Create -> AuthoringSession.createNewWithDependencies path dependency
            | StartupMode.Pin -> AuthoringSession.pinExistingDependency path (List.tryHead dependency)
            | StartupMode.Upgrade(source, replaces, fingerprint) -> AuthoringSession.upgradeDependency path source replaces fingerprint
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
