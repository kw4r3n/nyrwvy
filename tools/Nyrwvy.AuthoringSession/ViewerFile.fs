namespace Nyrwvy.Tools.AuthoringSession

open System
open System.IO
open System.Text
open Nyrwvy.Core

/// Writes human-facing viewer HTML for the committed state. The file is a
/// projection only: it is never read back and grants no publication authority.
[<RequireQualifiedAccess>]
module ViewerFile =
    /// Default directory, beside the snapshot, for views written without an explicit output path.
    let defaultDirectoryName = "nyrwvy-views"

    let private outputPath (snapshotPath: string) (requested: string option) (artifact: ViewerArtifact) =
        match requested with
        | None ->
            let directory = Path.GetDirectoryName(Path.GetFullPath snapshotPath)
            Ok(Path.Combine(directory, defaultDirectoryName, artifact.SuggestedFileName))
        | Some path when String.IsNullOrWhiteSpace path -> Error "output must not be blank."
        | Some path when not (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) ->
            Error "output must name an .html file."
        | Some path -> Ok(Path.GetFullPath path)

    let writeFocus
        (snapshotPath: string)
        (requested: string option)
        (focus: FunctionFocusView)
        : Result<string * ViewerArtifact, string> =
        let artifact = ViewerArtifact.ofFocus focus
        outputPath snapshotPath requested artifact
        |> Result.bind (fun path ->
            try
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                File.WriteAllText(path, artifact.Content, UTF8Encoding(false))
                Ok(path, artifact)
            with
            | :? IOException as error -> Error $"I/O error: {error.Message}"
            | :? UnauthorizedAccessException as error -> Error $"Access error: {error.Message}")
