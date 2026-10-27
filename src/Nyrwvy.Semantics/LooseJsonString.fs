namespace Nyrwvy.Core

open System
open System.Globalization
open System.Text
open System.Text.Encodings.Web
open System.Text.Json

/// JSON string literals for text that may hold an unpaired surrogate code unit. A String cannot
/// (validation rejects such a literal), but the .NET writers would replace one with U+FFFD and
/// `GetString` rejects its escape, so such text is written with an explicit `\uXXXX` escape and
/// read back exactly; the validator then names the problem instead of a silent substitution.
[<RequireQualifiedAccess>]
module internal LooseJsonString =
    let hasLoneSurrogate (text: string) =
        let mutable index = 0
        let mutable found = false
        while not found && index < text.Length do
            if Char.IsHighSurrogate(text[index]) && index + 1 < text.Length && Char.IsLowSurrogate(text[index + 1]) then index <- index + 2
            elif Char.IsSurrogate(text[index]) then found <- true
            else index <- index + 1
        found

    /// The quoted JSON literal for `text`: well-formed runs are escaped by `encoder` exactly as the
    /// serializer escapes them, and each lone surrogate is a `\uXXXX` escape.
    let encode (encoder: JavaScriptEncoder) (text: string) =
        let literal = StringBuilder("\"")
        let appendRun start finish =
            if finish > start then
                literal.Append(JsonEncodedText.Encode(text.AsSpan(start, finish - start), encoder).ToString()) |> ignore
        let mutable start = 0
        let mutable index = 0
        while index < text.Length do
            if Char.IsHighSurrogate(text[index]) && index + 1 < text.Length && Char.IsLowSurrogate(text[index + 1]) then
                index <- index + 2
            elif Char.IsSurrogate(text[index]) then
                appendRun start index
                literal.Append("\\u").Append((int text[index]).ToString("X4", CultureInfo.InvariantCulture)) |> ignore
                index <- index + 1
                start <- index
            else index <- index + 1
        appendRun start text.Length
        literal.Append('"').ToString()

    /// Decodes the raw text of a JSON string token the reader has already validated, keeping a
    /// lone surrogate escape as its code unit.
    let decodeRaw (raw: string) =
        let builder = StringBuilder()
        let mutable index = 1
        let last = raw.Length - 1
        while index < last do
            let current = raw[index]
            if current = '\\' then
                let escaped = raw[index + 1]
                index <- index + 2
                match escaped with
                | 'b' -> builder.Append('\b') |> ignore
                | 'f' -> builder.Append('\f') |> ignore
                | 'n' -> builder.Append('\n') |> ignore
                | 'r' -> builder.Append('\r') |> ignore
                | 't' -> builder.Append('\t') |> ignore
                | 'u' ->
                    builder.Append(char (Int32.Parse(raw.AsSpan(index, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))) |> ignore
                    index <- index + 4
                | other -> builder.Append(other) |> ignore
            else
                builder.Append(current) |> ignore
                index <- index + 1
        builder.ToString()
