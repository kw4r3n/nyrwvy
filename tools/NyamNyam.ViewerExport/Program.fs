open System
open System.IO
open System.Text
open NyamNyam.Core

type private FocusArguments =
    { InputPath: string
      Encoding: ViewerSourceEncoding
      FunctionIdText: string
      OutputPath: string option }

let private usage =
    """Usage:
  dotnet run --project tools/NyamNyam.ViewerExport -- \
    focus --input <snapshot-path> --format <json|nyason> \
    --function-id <stable-function-id> [--output <html-path>]

The command resolves functions only by stable FunctionId. Display-name lookup is not supported.
Unknown, duplicate, missing-value, and positional options are rejected.
"""

let private parseEncoding (value: string) =
    match value with
    | "json" -> Ok ViewerSourceEncoding.Json
    | "nyason" -> Ok ViewerSourceEncoding.Nyason
    | _ -> Error $"Unsupported --format '{value}'. Expected json or nyason."

let private parseOptions
    (allowed: Set<string>)
    (arguments: string list)
    : Result<Map<string, string>, string> =
    let rec loop
        (values: Map<string, string>)
        (remaining: string list)
        : Result<Map<string, string>, string> =
        match remaining with
        | [] -> Ok values
        | [ key ] ->
            if key.StartsWith("--", StringComparison.Ordinal) then
                Error $"Option '{key}' is missing its value."
            else
                Error $"Unexpected positional argument '{key}'."
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

let private requiredOption name (options: Map<string, string>) =
    match Map.tryFind name options with
    | Some value -> Ok value
    | None -> Error $"Missing required {name} argument."

let private parseFocusArguments (arguments: string list): Result<FocusArguments, string> =
    let allowed = set [ "--input"; "--format"; "--function-id"; "--output" ]
    parseOptions allowed arguments
    |> Result.bind (fun options ->
        match requiredOption "--input" options,
              requiredOption "--format" options,
              requiredOption "--function-id" options with
        | Error error, _, _
        | _, Error error, _
        | _, _, Error error -> Error error
        | Ok inputPath, Ok formatText, Ok functionIdText ->
            parseEncoding formatText
            |> Result.map (fun encoding ->
                { InputPath = inputPath
                  Encoding = encoding
                  FunctionIdText = functionIdText
                  OutputPath = Map.tryFind "--output" options }))

let private runFocus (arguments: string list): Result<string, string> =
    parseFocusArguments arguments
    |> Result.bind (fun parsed ->
        try
            let source = File.ReadAllText(parsed.InputPath, Encoding.UTF8)
            match ViewerExport.focusFromSource parsed.Encoding source parsed.FunctionIdText with
            | Error error -> Error $"Viewer export failed: {error}"
            | Ok artifact ->
                let outputPath =
                    parsed.OutputPath
                    |> Option.defaultValue artifact.SuggestedFileName

                let fullPath = Path.GetFullPath(outputPath)
                let directory = Path.GetDirectoryName(fullPath)
                if not (String.IsNullOrEmpty directory) then
                    Directory.CreateDirectory(directory) |> ignore

                File.WriteAllText(fullPath, artifact.Content, UTF8Encoding(false))
                Ok fullPath
        with
        | :? IOException as error -> Error $"I/O error: {error.Message}"
        | :? UnauthorizedAccessException as error -> Error $"Access error: {error.Message}")

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
