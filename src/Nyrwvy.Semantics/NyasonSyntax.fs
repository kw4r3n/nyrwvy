namespace Nyrwvy.Core

open System
open System.Globalization
open System.Text
open System.Text.Json

[<RequireQualifiedAccess>]
type NyasonValue =
    | Atom of string
    | Text of string
    | Boolean of bool
    | List of NyasonValue list
    | Record of tag: string * fields: (string * NyasonValue) list

[<Struct>]
type NyasonSyntaxError =
    { Position: int
      Message: string }

exception private NyasonParseException of int * string

[<RequireQualifiedAccess>]
module NyasonSyntax =
    /// Nesting allowance shared with the JSON snapshot codec, so a document one codec accepts
    /// is never too deep for the other, and deeper input is a parse error, not a stack overflow.
    let maxDepth = SnapshotCodec.maxJsonDepth

    let private unpairedSurrogateOffset (word: string) =
        let rec scan offset =
            if offset >= word.Length then None
            elif Char.IsHighSurrogate word[offset] then
                if offset + 1 < word.Length && Char.IsLowSurrogate word[offset + 1] then scan (offset + 2) else Some offset
            elif Char.IsLowSurrogate word[offset] then Some offset
            else scan (offset + 1)
        scan 0

    type private Parser(text: string) =
        let mutable index = 0
        let mutable depth = 0

        member private _.Fail(message: string) =
            raise (NyasonParseException(index, message))

        member private _.FailAt(position: int, message: string) =
            raise (NyasonParseException(position, message))

        member private this.Enter() =
            depth <- depth + 1
            if depth > maxDepth then
                this.Fail($"Nyason nesting exceeds the maximum depth of {maxDepth}.")

        member private _.Leave() =
            depth <- depth - 1

        member private _.SkipWhitespace() =
            while index < text.Length && Char.IsWhiteSpace text[index] do
                index <- index + 1

        member private this.Expect(expected: char) =
            this.SkipWhitespace()
            if index >= text.Length || text[index] <> expected then
                this.Fail($"Expected '{expected}'.")
            index <- index + 1

        member private this.ParseUnicodeEscape() =
            if index + 4 > text.Length then
                this.Fail("Incomplete Unicode escape in string literal.")

            let hex = text.Substring(index, 4)
            match Int32.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) with
            | true, value when hex |> Seq.forall Uri.IsHexDigit ->
                index <- index + 4
                char value
            | _ ->
                this.Fail("Invalid Unicode escape in string literal.")
                Unchecked.defaultof<char>

        member private this.ParseStringValue() =
            this.SkipWhitespace()
            if index >= text.Length || text[index] <> '"' then
                this.Fail("Expected string literal.")

            index <- index + 1
            let builder = StringBuilder()
            let mutable closed = false

            while index < text.Length && not closed do
                let current = text[index]
                index <- index + 1

                match current with
                | '"' -> closed <- true
                | '\\' ->
                    if index >= text.Length then
                        this.Fail("Incomplete escape in string literal.")

                    let escaped = text[index]
                    index <- index + 1

                    match escaped with
                    | '"' -> builder.Append('"') |> ignore
                    | '\\' -> builder.Append('\\') |> ignore
                    | '/' -> builder.Append('/') |> ignore
                    | 'b' -> builder.Append('\b') |> ignore
                    | 'f' -> builder.Append('\f') |> ignore
                    | 'n' -> builder.Append('\n') |> ignore
                    | 'r' -> builder.Append('\r') |> ignore
                    | 't' -> builder.Append('\t') |> ignore
                    | 'u' ->
                        // Each escape is one UTF-16 code unit: a high and a low surrogate escape in
                        // sequence form a pair, and a lone one is kept, as a String may hold it.
                        builder.Append(this.ParseUnicodeEscape()) |> ignore
                    | _ -> this.Fail($"Unsupported string escape '\\{escaped}'.")
                | '\r'
                | '\n' -> this.Fail("String literals cannot contain raw line breaks.")
                | value when Char.IsHighSurrogate value ->
                    if index < text.Length && Char.IsLowSurrogate text[index] then
                        builder.Append(value).Append(text[index]) |> ignore
                        index <- index + 1
                    else
                        this.FailAt(index - 1, "Unpaired surrogate in string literal.")
                | value when Char.IsLowSurrogate value ->
                    this.FailAt(index - 1, "Unpaired surrogate in string literal.")
                | value -> builder.Append(value) |> ignore

            if not closed then
                this.Fail("Unterminated string literal.")

            NyasonValue.Text(builder.ToString())

        member private this.ParseWord() =
            this.SkipWhitespace()
            let start = index

            let isDelimiter value =
                Char.IsWhiteSpace value
                || value = '{'
                || value = '}'
                || value = '['
                || value = ']'
                || value = '='
                || value = '"'

            while index < text.Length && not (isDelimiter text[index]) do
                index <- index + 1

            if index = start then
                this.Fail("Expected identifier or atom.")

            let word = text.Substring(start, index - start)

            match unpairedSurrogateOffset word with
            | Some offset -> this.FailAt(start + offset, "Unpaired surrogate in identifier or atom.")
            | None -> ()

            word

        member private this.ParseList() =
            this.Expect('[')
            this.Enter()
            let values = ResizeArray<NyasonValue>()
            let mutable closed = false

            while not closed do
                this.SkipWhitespace()
                if index >= text.Length then
                    this.Fail("Unterminated list.")
                elif text[index] = ']' then
                    index <- index + 1
                    closed <- true
                else
                    values.Add(this.ParseValue())

            this.Leave()
            NyasonValue.List(List.ofSeq values)

        member private this.ParseRecord(tag: string) =
            this.Expect('{')
            this.Enter()
            let fields = ResizeArray<string * NyasonValue>()
            let names = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            let mutable closed = false

            while not closed do
                this.SkipWhitespace()
                if index >= text.Length then
                    this.Fail($"Unterminated '{tag}' record.")
                elif text[index] = '}' then
                    index <- index + 1
                    closed <- true
                else
                    let name = this.ParseWord()
                    if not (names.Add name) then
                        this.Fail($"Duplicate field '{name}' in '{tag}' record.")
                    this.Expect('=')
                    fields.Add(name, this.ParseValue())

            this.Leave()
            NyasonValue.Record(tag, List.ofSeq fields)

        member this.ParseValue() =
            this.SkipWhitespace()
            if index >= text.Length then
                this.Fail("Expected Nyason value.")

            match text[index] with
            | '"' -> this.ParseStringValue()
            | '[' -> this.ParseList()
            | '}' ->
                this.Fail("Unexpected '}'.")
                Unchecked.defaultof<NyasonValue>
            | ']' ->
                this.Fail("Unexpected ']'.")
                Unchecked.defaultof<NyasonValue>
            | _ ->
                let word = this.ParseWord()
                this.SkipWhitespace()

                if index < text.Length && text[index] = '{' then
                    this.ParseRecord(word)
                else
                    match word with
                    | "true" -> NyasonValue.Boolean true
                    | "false" -> NyasonValue.Boolean false
                    | atom -> NyasonValue.Atom atom

        member this.ParseRoot() =
            let value = this.ParseValue()
            this.SkipWhitespace()
            if index <> text.Length then
                this.Fail("Unexpected trailing Nyason content.")
            value

    let parse (text: string): Result<NyasonValue, NyasonSyntaxError> =
        if isNull text then
            Error { Position = 0; Message = "Nyason text must not be null." }
        else
            try
                Ok(Parser(text).ParseRoot())
            with
            | NyasonParseException(position, message) ->
                Error { Position = position; Message = message }

    let private writeIndent (builder: StringBuilder) count =
        builder.Append(' ', count) |> ignore

    let rec private writeValue (builder: StringBuilder) indent value =
        match value with
        | NyasonValue.Atom atom -> builder.Append(atom) |> ignore
        | NyasonValue.Text text when LooseJsonString.hasLoneSurrogate text ->
            // The serializer would replace a lone surrogate with U+FFFD; it is escaped explicitly instead.
            builder.Append(LooseJsonString.encode Encodings.Web.JavaScriptEncoder.Default text) |> ignore
        | NyasonValue.Text text -> builder.Append(JsonSerializer.Serialize(text)) |> ignore
        | NyasonValue.Boolean value -> builder.Append(if value then "true" else "false") |> ignore
        | NyasonValue.List [] -> builder.Append("[]") |> ignore
        | NyasonValue.List values ->
            builder.AppendLine("[") |> ignore
            values
            |> List.iter (fun item ->
                writeIndent builder (indent + 2)
                writeValue builder (indent + 2) item
                builder.AppendLine() |> ignore)
            writeIndent builder indent
            builder.Append(']') |> ignore
        | NyasonValue.Record(tag, []) ->
            builder.Append(tag).Append(" {}") |> ignore
        | NyasonValue.Record(tag, fields) ->
            builder.Append(tag).AppendLine(" {") |> ignore
            fields
            |> List.iter (fun (name, fieldValue) ->
                writeIndent builder (indent + 2)
                builder.Append(name).Append(" = ") |> ignore
                writeValue builder (indent + 2) fieldValue
                builder.AppendLine() |> ignore)
            writeIndent builder indent
            builder.Append('}') |> ignore

    /// Whether `value` nests more lists and records than `parse` accepts, so a document that
    /// cannot be read back is never written.
    let exceedsMaxDepth value =
        let rec beyond remaining value =
            match value with
            | NyasonValue.List values -> remaining = 0 || values |> List.exists (beyond (remaining - 1))
            | NyasonValue.Record(_, fields) -> remaining = 0 || fields |> List.exists (snd >> beyond (remaining - 1))
            | NyasonValue.Atom _
            | NyasonValue.Text _
            | NyasonValue.Boolean _ -> false
        beyond maxDepth value

    let render value =
        let builder = StringBuilder()
        writeValue builder 0 value
        builder.AppendLine() |> ignore
        builder.ToString()
