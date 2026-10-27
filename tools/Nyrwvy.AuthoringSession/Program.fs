module Nyrwvy.Tools.AuthoringSession.Program

open System
open System.Text.Json
open Nyrwvy.Tools.AuthoringSession

[<RequireQualifiedAccess>]
type private StartupMode =
    | Open
    | OpenReadOnly
    | Create
    | Pin
    | Migrate of target: string option
    | SelectionUpgrade of AuthoringDependencySelectionUpgrade * reviewFingerprint: string
    | Upgrades of AuthoringDependencyUpgrade list * reviewFingerprint: string
    | Upgrade of source: string * replaces: string option * expectedFingerprint: string

module private CommandLine =
    let usage = "Usage: Nyrwvy.AuthoringSession <open|create|pin> --snapshot <snapshot-path> [--dependency <snapshot-path> ...] [--allow-host-sources] (create may name several dependencies; pin takes at most one same-content source)\n       Nyrwvy.AuthoringSession open --snapshot <snapshot-path> --read-only [--allow-host-sources] (takes no lock, so it works while another session has the snapshot open; commit is refused)\n       Nyrwvy.AuthoringSession migrate --snapshot <snapshot-path> [--dependency <exact copy of a legacy pin's source>] [--to <new-path>] (rewrites the snapshot and its dependency sidecar to the current schema, prints the migration report and exits; no session is started; --to writes the result to a new path instead, in the form its extension selects (.nyrec for records), and leaves the source untouched)\n       Nyrwvy.AuthoringSession upgrade --snapshot <snapshot-path> --dependency <new-version-path> --expect <reviewed-fingerprint> [--replaces <snapshot-id>] [--allow-host-sources]\n       Nyrwvy.AuthoringSession upgrade --snapshot <snapshot-path> --upgrades <JSON array of {source,replaces}> --review <reviewFingerprint> [--allow-host-sources]\n       Nyrwvy.AuthoringSession upgrade --snapshot <snapshot-path> --selection <JSON {sources:[paths],replaces:[current SnapshotIds]}> --review <reviewFingerprint> [--allow-host-sources]\n\n       Nyrwvy.AuthoringSession bind-c --header <file.h> ... (proposes capabilities and a C# mapping table for C functions; run bind-c alone for its options)\n--allow-host-sources lets runPure/runPureByName compile and run hostSources C# with your permissions; it is off by default."

    let rec private dependencies collected = function
        | [] -> Ok(List.rev collected)
        | "--dependency" :: path :: rest -> dependencies (path :: collected) rest
        | _ -> Error usage

    let hostSourcesFlag = "--allow-host-sources"

    let parse (arguments: string list): Result<StartupMode * string * string list, string> =
        match arguments |> List.filter ((<>) hostSourcesFlag) with
        | [ "open"; "--snapshot"; path ] -> Ok(StartupMode.Open, path, [])
        | [ "open"; "--snapshot"; path; "--read-only" ] -> Ok(StartupMode.OpenReadOnly, path, [])
        | "create" :: "--snapshot" :: path :: rest -> dependencies [] rest |> Result.map (fun paths -> StartupMode.Create, path, paths)
        | [ "pin"; "--snapshot"; path ] -> Ok(StartupMode.Pin, path, [])
        | [ "pin"; "--snapshot"; path; "--dependency"; dependency ] -> Ok(StartupMode.Pin, path, [ dependency ])
        | [ "migrate"; "--snapshot"; path ] -> Ok(StartupMode.Migrate None, path, [])
        | [ "migrate"; "--snapshot"; path; "--dependency"; dependency ] -> Ok(StartupMode.Migrate None, path, [ dependency ])
        | [ "migrate"; "--snapshot"; path; "--to"; target ] -> Ok(StartupMode.Migrate(Some target), path, [])
        | [ "migrate"; "--snapshot"; path; "--dependency"; dependency; "--to"; target ] -> Ok(StartupMode.Migrate(Some target), path, [ dependency ])
        | [ "upgrade"; "--snapshot"; path; "--dependency"; source; "--expect"; fingerprint ] ->
            Ok(StartupMode.Upgrade(source, None, fingerprint), path, [])
        | [ "upgrade"; "--snapshot"; path; "--dependency"; source; "--expect"; fingerprint; "--replaces"; replaces ] ->
            Ok(StartupMode.Upgrade(source, Some replaces, fingerprint), path, [])
        | [ "upgrade"; "--snapshot"; path; "--upgrades"; payload; "--review"; fingerprint ] ->
            try
                use document = JsonDocument.Parse payload
                let root = document.RootElement
                if root.ValueKind <> JsonValueKind.Array || root.GetArrayLength() = 0 then Error usage
                else
                    let upgrades = root.EnumerateArray() |> Seq.map (fun item ->
                        if item.ValueKind <> JsonValueKind.Object
                           || (item.EnumerateObject() |> Seq.map (fun property -> property.Name) |> Seq.sort |> Seq.toList) <> [ "replaces"; "source" ] then
                            invalidArg "upgrades" "Every replacement needs exactly source and replaces."
                        { Source = item.GetProperty("source").GetString(); Replaces = item.GetProperty("replaces").GetString() }) |> Seq.toList
                    if String.IsNullOrWhiteSpace fingerprint || (upgrades |> List.exists (fun item -> String.IsNullOrWhiteSpace item.Source || String.IsNullOrWhiteSpace item.Replaces)) then Error usage
                    else Ok(StartupMode.Upgrades(upgrades, fingerprint), path, [])
            with
            | :? JsonException
            | :? InvalidOperationException
            | :? ArgumentException
            | :? Collections.Generic.KeyNotFoundException -> Error usage
        | [ "upgrade"; "--snapshot"; path; "--selection"; payload; "--review"; fingerprint ] ->
            try
                use document = JsonDocument.Parse payload
                let root = document.RootElement
                if root.ValueKind <> JsonValueKind.Object
                   || (root.EnumerateObject() |> Seq.map (fun property -> property.Name) |> Seq.sort |> Seq.toList) <> [ "replaces"; "sources" ] then Error usage
                else
                    let strings (name: string) = root.GetProperty(name).EnumerateArray() |> Seq.map (fun item -> item.GetString()) |> Seq.toList
                    let selection = { Sources = strings "sources"; Replaces = strings "replaces" }
                    if String.IsNullOrWhiteSpace fingerprint then Error usage
                    else Ok(StartupMode.SelectionUpgrade(selection, fingerprint), path, [])
            with
            | :? JsonException
            | :? InvalidOperationException
            | :? ArgumentException
            | :? Collections.Generic.KeyNotFoundException -> Error usage
        | _ -> Error usage

[<EntryPoint>]
let main argv =
    match argv |> Array.toList with
    | "bind-c" :: rest -> CBindingGenerator.run rest
    | _ ->
    match CommandLine.parse (argv |> Array.toList) with
    | Error error ->
        Console.Error.WriteLine(error)
        2
    | Ok(StartupMode.Migrate target, path, dependency) ->
        let migrated =
            match target with
            | None -> AuthoringSessionMigration.migrate path (List.tryHead dependency)
            | Some target -> AuthoringSessionMigration.migrateTo path (List.tryHead dependency) target
        match migrated with
        | Error error ->
            Console.Error.WriteLine(WireEncode.startupMessage error)
            2
        | Ok report ->
            Console.Out.WriteLine(AuthoringSessionMigration.reportJson report)
            0
    | Ok(mode, path, dependency) ->
        let result =
            match mode with
            | StartupMode.Migrate _ -> failwith "unreachable: migrate is handled above"
            | StartupMode.Open -> AuthoringSession.openExisting path
            | StartupMode.OpenReadOnly -> AuthoringSession.openReadOnly path
            | StartupMode.Create -> AuthoringSession.createNewWithDependencies path dependency
            | StartupMode.Pin -> AuthoringSession.pinExistingDependency path (List.tryHead dependency)
            | StartupMode.SelectionUpgrade(selection, fingerprint) -> AuthoringSession.upgradeDependencySelection path selection fingerprint
            | StartupMode.Upgrades(upgrades, fingerprint) -> AuthoringSession.upgradeDependencies path upgrades fingerprint
            | StartupMode.Upgrade(source, replaces, fingerprint) -> AuthoringSession.upgradeDependency path source replaces fingerprint
        match result with
        | Error error ->
            Console.Error.WriteLine(WireEncode.startupMessage error)
            2
        | Ok session ->
            use session = session
            session.HostSourcesAllowed <- Array.contains CommandLine.hostSourcesFlag argv
            // NYRWVY_RUNPURE_WARMUP=0 turns the background warm-up off.
            if Environment.GetEnvironmentVariable "NYRWVY_RUNPURE_WARMUP" <> "0" then
                let worker = session.PureRunWorker
                System.Threading.Tasks.Task.Run(fun () -> PureRun.warm worker) |> ignore
            AuthoringProtocol.serve session Console.In Console.Out
            0
