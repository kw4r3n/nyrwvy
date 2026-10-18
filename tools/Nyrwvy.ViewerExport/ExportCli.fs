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

/// Writes `content` as UTF-8 without a BOM, creating the parent directory, and returns the full path.
let writeUtf8 (outputPath: string) (content: string) : Result<string, string> =
    try
        let fullPath = Path.GetFullPath(outputPath)
        let directory = Path.GetDirectoryName(fullPath)
        if not (String.IsNullOrEmpty directory) then
            Directory.CreateDirectory(directory) |> ignore
        File.WriteAllText(fullPath, content, UTF8Encoding(false))
        Ok fullPath
    with
    | :? IOException as error -> Error $"I/O error: {error.Message}"
    | :? UnauthorizedAccessException as error -> Error $"Access error: {error.Message}"
