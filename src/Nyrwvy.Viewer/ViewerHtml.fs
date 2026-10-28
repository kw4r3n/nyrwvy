namespace Nyrwvy.Core

open System.IO

/// The viewer page: one fixed HTML/CSS/JavaScript asset (`viewer.html`) with the viewer data, the
/// JSON a session host writes for a view, embedded where it says `@DATA@`. The asset renders the
/// data; no markup is generated per view.
[<RequireQualifiedAccess>]
module ViewerHtml =
    /// Marks a page as viewer output, so a tool replaces only its own pages, never a person's file.
    let generatorMeta = "<meta name=\"generator\" content=\"Nyrwvy viewer\">"

    let private placeholder = "@DATA@"

    /// The asset's bytes as text, exactly as the repository holds them.
    let asset =
        lazy
            (use stream = typeof<ViewerTarget>.Assembly.GetManifestResourceStream("Nyrwvy.Viewer.viewer.html")
             use reader = new StreamReader(stream)
             reader.ReadToEnd())

    /// The page for `data`, a JSON text that escapes `<` (as every session host's encoder does), so
    /// no text in it can close the script element that holds it.
    let page (data: string): string =
        let asset = asset.Value
        let index = asset.IndexOf(placeholder, System.StringComparison.Ordinal)
        asset.Substring(0, index) + data + asset.Substring(index + placeholder.Length)
