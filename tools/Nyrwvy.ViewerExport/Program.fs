open System
open System.IO
open System.Text
open Nyrwvy.Core

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
            let artifact =
                match parsed.Function with
                | FunctionSelector.Id id -> ViewerExport.focusFromSource parsed.Encoding source id
                | FunctionSelector.DisplayName name -> ViewerExport.focusFromSourceByName parsed.Encoding source name
            match artifact with
            | Error error -> Error $"Viewer export failed: {error}"
            | Ok artifact ->
                let outputPath = parsed.OutputPath |> Option.defaultValue artifact.SuggestedFileName
                ExportCli.writeUtf8 outputPath artifact.Content))

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
