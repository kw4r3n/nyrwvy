open System
open System.IO
open System.Text
open Nyrwvy.Core

type private FocusArguments =
    { InputPath: string
      Encoding: ViewerSourceEncoding
      FunctionIdText: string
      OutputPath: string option }

let private usage =
    """Usage:
  dotnet run --project tools/Nyrwvy.ViewerExport -- \
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

let private parseFocusArguments (arguments: string list): Result<FocusArguments, string> =
    let allowed = set [ "--input"; "--format"; "--function-id"; "--output" ]
    ExportCli.parseOptions allowed arguments
    |> Result.bind (fun options ->
        match ExportCli.requiredOption "--input" options,
              ExportCli.requiredOption "--format" options,
              ExportCli.requiredOption "--function-id" options with
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
        let source =
            try
                Ok(File.ReadAllText(parsed.InputPath, Encoding.UTF8))
            with
            | :? IOException as error -> Error $"I/O error: {error.Message}"
            | :? UnauthorizedAccessException as error -> Error $"Access error: {error.Message}"

        source
        |> Result.bind (fun source ->
            match ViewerExport.focusFromSource parsed.Encoding source parsed.FunctionIdText with
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
