open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

[<RequireQualifiedAccess>]
type private ViewerSourceEncoding =
    | Json
    | Record

[<RequireQualifiedAccess>]
type private FunctionSelector =
    | Id of string
    | DisplayName of string

type private FocusArguments =
    { InputPath: string
      Encoding: ViewerSourceEncoding
      Function: FunctionSelector
      OutputPath: string option }

let private usage =
    """Usage:
  dotnet run --project tools/Nyrwvy.ViewerExport -- \
    focus --input <snapshot-path> --format <json|record> \
    (--function-id <stable-function-id> | --function <display-name>) [--output <html-path>]

--function takes an exact display name and fails when no function or several functions have it.
The default output file name is derived from the stable FunctionId, never from the display name.
The output must not replace the input snapshot or its target.
An existing output is replaced as a file; output symbolic links are refused.
Unknown, duplicate, missing-value, and positional options are rejected.
"""

let private parseEncoding (value: string) =
    match value with
    | "json" -> Ok ViewerSourceEncoding.Json
    | "record" -> Ok ViewerSourceEncoding.Record
    | _ -> Error $"Unsupported --format '{value}'. Expected json or record."

let private parseFocusArguments (arguments: string list): Result<FocusArguments, string> =
    let allowed = set [ "--input"; "--format"; "--function-id"; "--function"; "--output" ]
    let functionSelector options =
        match Map.tryFind "--function-id" options, Map.tryFind "--function" options with
        | Some id, None -> Ok(FunctionSelector.Id id)
        | None, Some displayName -> Ok(FunctionSelector.DisplayName displayName)
        | _ -> Error "Pass exactly one of --function-id and --function."
    ExportCli.parseOptions allowed arguments
    |> Result.bind (fun options ->
        match ExportCli.requiredOption "--input" options,
              ExportCli.requiredOption "--format" options,
              functionSelector options with
        | Error error, _, _
        | _, Error error, _
        | _, _, Error error -> Error error
        | Ok inputPath, Ok formatText, Ok selector ->
            parseEncoding formatText
            |> Result.map (fun encoding ->
                { InputPath = inputPath
                  Encoding = encoding
                  Function = selector
                  OutputPath = Map.tryFind "--output" options }))

/// The Zig session host: `NYRWVY_HOST_EXECUTABLE`, or the repository's build (`npm run build:host`).
let private hostExecutable () =
    match Environment.GetEnvironmentVariable "NYRWVY_HOST_EXECUTABLE" with
    | null | "" ->
        let rec root (directory: DirectoryInfo) =
            if isNull directory then None
            elif File.Exists(Path.Combine(directory.FullName, "Nyrwvy.slnx")) then Some directory.FullName
            else root directory.Parent
        let name = if OperatingSystem.IsWindows() then "nyrwvy-host.exe" else "nyrwvy-host"
        match root (DirectoryInfo AppContext.BaseDirectory) with
        | Some repository -> Ok(Path.Combine(repository, "tools", "zig", "host", "bin", name))
        | None -> Error "Cannot find the repository's Zig host; set NYRWVY_HOST_EXECUTABLE."
    | path -> Ok path

/// The Function Focus page from the host's `viewFunction`, on a private copy of the snapshot so the
/// host writes nothing beside the input: the suggested file name and the page's content.
let private focusPage (encoding: ViewerSourceEncoding) (inputPath: string) (source: string) (selector: FunctionSelector) : Result<string * string, string> =
    hostExecutable ()
    |> Result.bind (fun executable ->
        let directory = Directory.CreateTempSubdirectory("nyrwvy-viewer-export-").FullName
        try
            let snapshot = Path.Combine(directory, (match encoding with ViewerSourceEncoding.Json -> "snapshot.json" | ViewerSourceEncoding.Record -> "snapshot.nyrec"))
            File.WriteAllText(snapshot, source, UTF8Encoding false)
            // The host refuses a snapshot with dependencies when its sidecar is not beside it.
            let sidecar = inputPath + ".dependency.json"
            if File.Exists sidecar then File.Copy(sidecar, snapshot + ".dependency.json")
            let request = JsonObject()
            request["id"] <- JsonValue.Create "view"
            request["op"] <- JsonValue.Create "viewFunction"
            match selector with
            | FunctionSelector.Id id -> request["functionId"] <- JsonValue.Create id
            | FunctionSelector.DisplayName name -> request["function"] <- JsonValue.Create name
            let info = ProcessStartInfo(executable, [ "open"; "--snapshot"; snapshot; "--read-only" ])
            info.RedirectStandardInput <- true
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true
            info.StandardOutputEncoding <- UTF8Encoding false
            info.StandardErrorEncoding <- UTF8Encoding false
            use host = Process.Start info
            let errors = host.StandardError.ReadToEndAsync()
            // A host that fails at startup exits before reading the request; its output says why.
            (try
                host.StandardInput.Write(request.ToJsonString() + "\n")
                host.StandardInput.Close()
             with :? IOException -> ())
            let reply = host.StandardOutput.ReadLine()
            host.WaitForExit()
            if isNull reply || host.ExitCode <> 0 then Error((if isNull reply then "" else reply.Trim() + " ") + errors.Result.Trim())
            else
                use document = JsonDocument.Parse reply
                let root = document.RootElement
                if root.GetProperty("ok").GetBoolean() then
                    let path = root.GetProperty("result").GetProperty("path").GetString()
                    let written = if Path.IsPathRooted path then path else Path.Combine(directory, path)
                    Ok(Path.GetFileName written, File.ReadAllText(written, Encoding.UTF8))
                else
                    let error = root.GetProperty "error"
                    Error(error.GetProperty("code").GetString() + ": " + error.GetProperty("message").GetString())
        with
        | :? ComponentModel.Win32Exception as error -> Error $"Cannot start the Zig host {executable}: {error.Message}"
        | :? IOException as error -> Error $"I/O error: {error.Message}"
        | :? UnauthorizedAccessException as error -> Error $"Access error: {error.Message}"
        | :? JsonException
        | :? Collections.Generic.KeyNotFoundException
        | :? InvalidOperationException -> Error "The Zig host's reply is not a viewFunction reply."
        |> fun result ->
            (try Directory.Delete(directory, true) with _ -> ())
            result)

let private runFocus (arguments: string list): Result<string, string> =
    parseFocusArguments arguments
    |> Result.bind (fun parsed ->
        let source =
            try
                Ok(File.ReadAllText(parsed.InputPath, Encoding.UTF8))
            with
            | :? IOException as error -> Error $"I/O error: {error.Message}"
            | :? UnauthorizedAccessException as error -> Error $"Access error: {error.Message}"

        source
        |> Result.bind (fun source ->
            match focusPage parsed.Encoding parsed.InputPath source parsed.Function with
            | Error error -> Error $"Viewer export failed: {error}"
            | Ok(suggested, content) ->
                let outputPath = parsed.OutputPath |> Option.defaultValue suggested
                ExportCli.writeUtf8 parsed.InputPath outputPath content))

[<EntryPoint>]
let main argv =
    let arguments = argv |> Array.toList
    let result =
        match arguments with
        | "focus" :: rest -> runFocus rest
        | [] -> Error "Missing command."
        | command :: _ -> Error $"Unsupported command '{command}'."

    match result with
    | Ok outputPath ->
        Console.Out.WriteLine(outputPath)
        0
    | Error message ->
        Console.Error.WriteLine(message)
        Console.Error.WriteLine(usage)
        2
