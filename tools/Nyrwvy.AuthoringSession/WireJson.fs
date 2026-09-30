namespace Nyrwvy.Tools.AuthoringSession

open System
open System.Collections.Generic
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Serialization

module internal WireJson =
    let objectValue (pairs: (string * obj) list): obj =
        let value = Dictionary<string, obj>(StringComparer.Ordinal)
        for key, item in pairs do
            value[key] <- item
        box value

    let arrayValue (items: obj list): obj = items |> List.toArray |> box

    /// A response must be able to render any graph the snapshot codec can persist. Per nested
    /// scope a response uses at most 4/3 of the snapshot's JSON levels (Conditional: 4 against 3;
    /// Bind: 5 against 4; MatchUnion: 6 against 5), plus a small envelope, so twice the snapshot
    /// codec's depth covers it with margin. This is a response-encoding allowance, not a change
    /// to Core recipe bounds.
    let responseMaxJsonDepth = 2 * Nyrwvy.Core.SnapshotCodec.maxJsonDepth
    let private responseOptions = JsonSerializerOptions(MaxDepth = responseMaxJsonDepth)
    let serialize (value: obj): string = JsonSerializer.Serialize(value, responseOptions)

    let private wellFormed (text: string) =
        let mutable index = 0
        let mutable valid = true
        while valid && index < text.Length do
            if Char.IsHighSurrogate(text[index]) && index + 1 < text.Length && Char.IsLowSurrogate(text[index + 1]) then index <- index + 2
            elif Char.IsSurrogate(text[index]) then valid <- false
            else index <- index + 1
        valid

    /// Escapes `text[start .. finish - 1]`, a well-formed run, exactly as the serializer would.
    let private appendEscaped (literal: StringBuilder) (encoder: JavaScriptEncoder) (text: string) start finish =
        if finish > start then
            literal.Append(JsonEncodedText.Encode(text.AsSpan(start, finish - start), encoder).ToString()) |> ignore

    /// A run-value String with a lone surrogate. The serializer would replace the surrogate with
    /// U+FFFD, so it is written as a JSON string literal with a `\uD800`-style escape instead; the
    /// rest of the string is escaped as the serializer escapes it.
    [<JsonConverter(typeof<LooseTextConverter>)>]
    type LooseText(text: string) =
        member _.Text = text

    and LooseTextConverter() =
        inherit JsonConverter<LooseText>()
        override _.Read(_, _, _) = raise (NotSupportedException "Run-value Strings are only written.")
        override _.Write(writer, value, options) =
            let text = value.Text
            let encoder = if isNull options.Encoder then JavaScriptEncoder.Default else options.Encoder
            let literal = StringBuilder("\"")
            let mutable start = 0
            let mutable index = 0
            while index < text.Length do
                if Char.IsHighSurrogate(text[index]) && index + 1 < text.Length && Char.IsLowSurrogate(text[index + 1]) then
                    index <- index + 2
                elif Char.IsSurrogate(text[index]) then
                    appendEscaped literal encoder text start index
                    literal.Append("\\u").Append((int text[index]).ToString("X4")) |> ignore
                    index <- index + 1
                    start <- index
                else index <- index + 1
            appendEscaped literal encoder text start text.Length
            writer.WriteRawValue(literal.Append('"').ToString(), true)

    /// Boxes a run-value String for a response. Well-formed text is the plain string, encoded exactly
    /// as before; text with a lone surrogate keeps it as a JSON `\uXXXX` escape.
    let runString (text: string): obj = if isNull text || wellFormed text then box text else box (LooseText text)
