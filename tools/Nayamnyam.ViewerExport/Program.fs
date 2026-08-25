open System
open System.IO
open System.Text
open Nayamnyam.Core

type private FocusArguments =
    { InputPath: string
      Encoding: ViewerSourceEncoding
      FunctionIdText: string
      OutputPath: string option }

let private usage =
    """Usage:
  dotnet run --project tools/Nayamnyam.ViewerExport -- \
    focus --input <snapshot-path> --format <json|nyason> \
    --function-id <stable-function-id> [--output <html-path>]

The command resolves functions only by stable FunctionId. Display-name lookup is not supported.
"""

let private parseEncoding value =
    match value with
    | "json" -> Ok ViewerSourceEncoding.Json
    | "nyason" -> Ok ViewerSourceEncoding.Nyason
    | _ -> Error $"Unsupported --format '{value}'. Expected json or nyason."

let private optionValue name (arguments: string list) =
    let rec find remaining =
        match remaining with
        | [] -> None
        | key :: value :: _ when key = name -> Some value
        | _ :: rest -> find rest
    find arguments

let private parseFocusArguments (arguments: string list): Result<FocusArguments, string> =
    match optionValue "--input" arguments,
          optionValue "--format" arguments,
          optionValue "--function-id" arguments with
    | None, _, _ -> Error "Missing required --input argument."
    | _, None, _ -> Error "Missing required --format argument."
    | _, _, None -> Error "Missing required --function-id argument."
    | Some inputPath, Some formatText, Some functionIdText ->
        parseEncoding formatText
        |> Result.map (fun encoding ->
            { InputPath = inputPath
              Encoding = encoding
              FunctionIdText = functionIdText
              OutputPath = optionValue "--output" arguments })

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
