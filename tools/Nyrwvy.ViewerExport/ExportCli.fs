/// Option parsing and output writing for the ViewerExport tool.
[<RequireQualifiedAccess>]
module internal ExportCli

open System
open System.IO
open System.Text

/// Parses `--key value` pairs, rejecting unknown, duplicate, missing-value and positional arguments.
let parseOptions (allowed: Set<string>) (arguments: string list) : Result<Map<string, string>, string> =
    let isOption (argument: string) = argument.StartsWith("--", StringComparison.Ordinal)

    let rec loop (values: Map<string, string>) (remaining: string list) =
        match remaining with
        | [] -> Ok values
        | key :: _ when not (isOption key) -> Error $"Unexpected positional argument '{key}'."
        | [ key ] -> Error $"Option '{key}' is missing its value."
        | key :: value :: rest ->
            if not (Set.contains key allowed) then
                Error $"Unsupported option '{key}'."
            elif Map.containsKey key values then
                Error $"Duplicate option '{key}'."
            elif isOption value then
                Error $"Option '{key}' is missing its value."
            else
                loop (Map.add key value values) rest

    loop Map.empty arguments

let requiredOption (name: string) (options: Map<string, string>) =
    match Map.tryFind name options with
    | Some value -> Ok value
    | None -> Error $"Missing required {name} argument."

let rec private resolveLinks (path: string) =
    let parent = Path.GetDirectoryName path
    if String.IsNullOrEmpty parent then path
    else
        let candidate = Path.Combine(resolveLinks parent, Path.GetFileName path)
        let info = FileInfo candidate
        if isNull info.LinkTarget then candidate
        else resolveLinks (info.ResolveLinkTarget(true).FullName)

let private resolveEntry (path: string) =
    let fullPath = Path.GetFullPath path
    let parent = Path.GetDirectoryName fullPath
    if String.IsNullOrEmpty parent then fullPath
    else Path.Combine(resolveLinks parent, Path.GetFileName fullPath)

/// Replaces the output with UTF-8 HTML without writing through links to the input.
let writeUtf8 (inputPath: string) (outputPath: string) (content: string) : Result<string, string> =
    try
        let fullPath = Path.GetFullPath(outputPath)
        let outputEntry = resolveEntry fullPath
        let comparison =
            if OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() then StringComparison.OrdinalIgnoreCase
            else StringComparison.Ordinal
        if String.IsNullOrEmpty(Path.GetFileName fullPath) then
            Error "Output must name a file."
        elif [ resolveEntry inputPath; resolveLinks (Path.GetFullPath inputPath) ]
           |> List.exists (fun input -> String.Equals(input, outputEntry, comparison)) then
            Error "Output must not replace the input snapshot."
        elif not (isNull (FileInfo(outputEntry).LinkTarget)) then
            Error "Output must not be a symbolic link."
        else
            let directory = Path.GetDirectoryName outputEntry
            Directory.CreateDirectory(directory) |> ignore
            let temporary = Path.Combine(directory, $".{Path.GetFileName outputEntry}.{Guid.NewGuid():N}.tmp")
            try
                do
                    use stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                    use writer = new StreamWriter(stream, UTF8Encoding(false))
                    writer.Write(content)
                if not (OperatingSystem.IsWindows()) && File.Exists outputEntry then
                    File.SetUnixFileMode(temporary, File.GetUnixFileMode outputEntry)
                File.Move(temporary, outputEntry, true)
                Ok fullPath
            finally
                if File.Exists temporary then File.Delete temporary
    with
    | :? IOException as error -> Error $"I/O error: {error.Message}"
    | :? UnauthorizedAccessException as error -> Error $"Access error: {error.Message}"
