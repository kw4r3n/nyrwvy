namespace Nyrwvy.Core

open System
open System.Buffers
open System.Collections.Generic
open System.Text
open System.Text.Encodings.Web
open System.Text.Json

/// A record-form error at a 1-based line and column.
type RecordCodecError = { Line: int; Column: int; Message: string }

/// The one-record-per-line snapshot form (docs/decisions/snapshot-record-format.md): the strict JSON
/// writer's shape rendered as records. `serialize` renders the JSON codec's output; `deserialize`
/// tokenizes the rows into an ownership tree, checks canonical form token by token (no re-encode),
/// writes the writer's JSON as UTF-8 bytes into a reused buffer and hands those bytes to
/// `SnapshotCodec`'s reader. Ported from the evaluated harness (evaluation/snapshot-record-format).
[<RequireQualifiedAccess>]
module RecordCodec =
    exception internal RecordFailure of RecordCodecError
    let private fail line column message = raise (RecordFailure { Line = line; Column = column; Message = message })

    // ---------------------------------------------------------------------------------------------
    // The per-kind key table (RecordKinds.fs, generated from the writer) and the hand-stated facts
    // ---------------------------------------------------------------------------------------------

    type private Scalar =
        | Id of string
        | Bool
        | Int
        | Str

    type private Shape =
        | Meta
        | Scalar of Scalar
        | Graph
        | ValueArm of bindingKey: string
        | Arms
        | TypeExpr
        | TypeList
        | IdList of domain: string
        | Pairs of string * string * string * string
        | Members of memberKind: string

    type private Key = { Json: string; Record: string; Shape: Shape; Optional: bool; OmitEmpty: bool }
    /// `JsonKind` is null when the object carries no `kind` property.
    type private Kind = { Name: string; JsonKind: string; Keys: Key[] }

    /// Key and kind spellings repeat on every line; one shared string per spelling lets keys compare by reference.
    let private spellings = System.Collections.Concurrent.ConcurrentDictionary<string, string>()
    let private spellingLookup = spellings.GetAlternateLookup<ReadOnlySpan<char>>()
    let private intern (span: ReadOnlySpan<char>) =
        let mutable found = null
        if spellingLookup.TryGetValue(span, &found) then found
        else let s = span.ToString() in spellings.GetOrAdd(s, s)

    let private inKey = intern (ReadOnlySpan<char>("in".ToCharArray()))
    let private positionKey = intern (ReadOnlySpan<char>("position".ToCharArray()))

    let private parseShape (s: string) =
        let paren = s.IndexOf '('
        let head, inner = if paren < 0 then s, "" else s.Substring(0, paren), s.Substring(paren + 1, s.Length - paren - 2)
        match head with
        | "id" -> Scalar(Id inner)
        | "bool" -> Scalar Bool
        | "int" -> Scalar Int
        | "str" -> Scalar Str
        | "graph" -> Graph
        | "arm" -> ValueArm inner
        | "arms" -> Arms
        | "type" -> TypeExpr
        | "types" -> TypeList
        | "ids" -> IdList inner
        | "pairs" ->
            let pair = inner.Split ','
            let a, b = pair[0].Split ':', pair[1].Split ':'
            Pairs(a[0], a[1], b[0], b[1])
        | "members" -> Members(intern (inner.AsSpan()))
        | other -> failwith $"RecordKinds: unknown shape '{other}'"

    /// The table text (public so the harness can check it against the writer).
    let kindTable = RecordKinds.text.Trim '\n' + "\n"

    let private tables =
        let byName = Dictionary<string, Kind>()
        let byJson = Dictionary<string, Kind>()
        for line in kindTable.Split('\n', StringSplitOptions.RemoveEmptyEntries) do
            let colon = line.IndexOf ": "
            let head = line.Substring(0, colon)
            let name, jsonKind = match head.IndexOf '=' with -1 -> head, null | i -> head.Substring(0, i), head.Substring(i + 1)
            let keys =
                line.Substring(colon + 2).Split ' '
                |> Array.map (fun token ->
                    match token with
                    | "id" | "kind" | "typeParameterId" -> { Json = token; Record = token; Shape = Meta; Optional = false; OmitEmpty = false }
                    | _ ->
                        let omitEmpty = token.EndsWith '~'
                        let token = token.TrimEnd '~'
                        let optional = token.EndsWith '?'
                        let token = token.TrimEnd '?'
                        let slash, shapeAt = token.IndexOf '/', token.IndexOf ':'
                        { Json = token.Substring(0, slash)
                          Record = intern (token.AsSpan(slash + 1, shapeAt - slash - 1))
                          Shape = parseShape (token.Substring(shapeAt + 1))
                          Optional = optional
                          OmitEmpty = omitEmpty })
            let kind = { Name = intern (name.AsSpan()); JsonKind = jsonKind; Keys = keys }
            byName[kind.Name] <- kind
            if not (isNull jsonKind) then byJson[jsonKind] <- kind
        byName, byJson

    let private kinds = fst tables
    let private kindsByJson = snd tables

    /// Declaration groups in schema order: the JSON collection and the subject domain.
    let private groups = [| "types", "type"; "effects", "effect"; "capabilities", "capability"; "functions", "function"; "obligations", "obligation" |]
    let private groupOfKind = dict [ "product", 0; "union", 0; "effect", 1; "capability", 2; "function", 3; "missing-argument", 4 ]
    /// Member record kind -> the JSON collection holding it, from the table's Members shapes.
    let private collectionOf =
        dict [ for KeyValue(_, kind) in kinds do
                   for key in kind.Keys do
                       match key.Shape with
                       | Members m -> yield m, key.Json
                       | _ -> () ]
    /// Collections whose order is semantic and whose records carry `position=` (SnapshotCodec.fs
    /// writeTypeParameters, writeFunction, writeCapabilityOperation: "declared order").
    let private ordered = HashSet [ "parameters"; "typeParameters" ]
    let private declarationDomains = HashSet [ "type"; "effect"; "capability"; "function"; "obligation" ]
    /// ID lists the writer sorts by ID (SnapshotCodec.fs writeFunction requiredEffects); every pair list
    /// is sorted by its pair (writeNode contexts, arguments, fields, implementations), and an arm's
    /// `variants=` by ID (UnionMatchArm.canonicalVariantIds). Positional lists keep their order.
    let private sortedIdLists = HashSet [ "requiredEffects" ]
    let private memberDomains = HashSet [ "node"; "binding"; "parameter"; "capability-parameter"; "type-parameter"; "field"; "variant"; "operation"; "type"; "effect"; "capability"; "function"; "obligation" ]
    let private memberDomainLookup = memberDomains.GetAlternateLookup<ReadOnlySpan<char>>()
    let private primitives = HashSet [ "string"; "bool"; "unit"; "int64"; "float64"; "bits32" ]
    let private primitiveLookup = primitives.GetAlternateLookup<ReadOnlySpan<char>>()
    let private generics = Dictionary<string, string>(dict [ "option", "option"; "result", "result"; "map", "map"; "list", "list"; "set", "set"; "one-shot", "oneShot"; "task", "task"; "exception", "exception"; "task-scope", "taskScope" ])
    let private genericLookup = generics.GetAlternateLookup<ReadOnlySpan<char>>()
    let private genericsByJson = dict [ for KeyValue(k, v) in generics -> v, k ]

    /// The ID domain of a record's subject; the tags of StableId.fs. An operation's parameters are
    /// capability parameters (SnapshotCodec.fs writeCapabilityParameter).
    let private subjectDomain (kind: string) (owner: string) =
        match kind with
        | "parameter" -> if not (isNull owner) && owner.StartsWith("operation:", StringComparison.Ordinal) then "capability-parameter" else "parameter"
        | "type-parameter" | "field" | "variant" | "operation" | "binding" | "effect" | "capability" | "function" -> kind
        | "product" | "union" -> "type"
        | "missing-argument" -> "obligation"
        | _ -> "node"

    // ---------------------------------------------------------------------------------------------
    // Token grammar
    // ---------------------------------------------------------------------------------------------

    let private isHex (c: char) = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')
    let private isDomainChar (c: char) = (c >= 'a' && c <= 'z') || c = '-'

    /// The full lowercase hyphenated UUID.
    let private isUuid (s: ReadOnlySpan<char>) =
        let mutable ok = s.Length = 36
        let mutable i = 0
        while ok && i < 36 do
            ok <- if i = 8 || i = 13 || i = 18 || i = 23 then s[i] = '-' else isHex s[i]
            i <- i + 1
        ok

    /// `domain:uuid` -> the domain's length, else -1.
    let private idDomainLength (s: ReadOnlySpan<char>) =
        let colon = s.IndexOf ':'
        if colon <= 0 || s.Length <> colon + 37 || not (isUuid (s.Slice(colon + 1))) then -1
        else
            let mutable ok = true
            for i in 0 .. colon - 1 do
                if not (isDomainChar s[i]) then ok <- false
            if ok then colon else -1

    let private isId (s: ReadOnlySpan<char>) = idDomainLength s >= 0
    let private hasDomain (s: ReadOnlySpan<char>) (domain: string) = isId s && s.Length = domain.Length + 37 && s.StartsWith(domain, StringComparison.Ordinal)

    /// `id`, `id/role` or `id/arm/variant:uuid`.
    let private isSubjectSyntax (s: ReadOnlySpan<char>) =
        let slash = s.IndexOf '/'
        if slash < 0 then isId s
        elif not (isId (s.Slice(0, slash))) then false
        else
            let rest = s.Slice(slash + 1)
            let slash2 = rest.IndexOf '/'
            let role = if slash2 < 0 then rest else rest.Slice(0, slash2)
            let mutable ok = role.Length > 0
            for i in 0 .. role.Length - 1 do
                if not (isDomainChar role[i]) then ok <- false
            ok && (slash2 < 0 || hasDomain (rest.Slice(slash2 + 1)) "variant")

    /// The integer spelling of Utf8JsonWriter: no sign on zero, no leading zeros, no plus.
    let private isCanonicalInt (s: ReadOnlySpan<char>) =
        let digits = if s.Length > 0 && s[0] = '-' then s.Slice 1 else s
        let mutable ok = digits.Length > 0 && (digits.Length = 1 || digits[0] <> '0') && not (digits.Length = 1 && digits[0] = '0' && s.Length = 2)
        for i in 0 .. digits.Length - 1 do
            if digits[i] < '0' || digits[i] > '9' then ok <- false
        ok

    /// The JSON string-literal grammar (RFC 8259) for the escapes and control characters between the quotes.
    let private validStringBody (text: string) (start: int) (stop: int) =
        let isHexAny c = isHex c || (c >= 'A' && c <= 'F')
        let mutable i, ok = start, true
        while ok && i < stop do
            let c = text[i]
            if c < ' ' || Char.IsSurrogate c && not (Char.IsSurrogatePair(text, i)) then ok <- false
            elif Char.IsHighSurrogate c then i <- i + 2
            elif c <> '\\' then i <- i + 1
            elif i + 1 >= stop then ok <- false
            else
                match text[i + 1] with
                | '"' | '\\' | '/' | 'b' | 'f' | 'n' | 'r' | 't' -> i <- i + 2
                | 'u' when i + 5 < stop && isHexAny text[i + 2] && isHexAny text[i + 3] && isHexAny text[i + 4] && isHexAny text[i + 5] -> i <- i + 6
                | _ -> ok <- false
        ok

    /// Whether a string literal is spelled as the writer's encoder spells its content.
    let private isCanonicalString (raw: string) =
        LooseJsonString.encode JavaScriptEncoder.Default (LooseJsonString.decodeRaw raw) = raw

    // ---------------------------------------------------------------------------------------------
    // Rows: one tokenized line is one tree node
    // ---------------------------------------------------------------------------------------------

    /// A `key=value` field: the value as a range into the file text.
    [<Struct>]
    type private Field = { Key: string; Start: int; Length: int; Column: int }

    [<AllowNullLiteral>]
    type private Record(line: int, column: int, endColumn: int, kind: string, subject: string, owner: string, fields: Field[], block: List<Record>) =
        member _.Line = line
        /// The subject's column.
        member _.Column = column
        member _.EndColumn = endColumn
        member _.Kind = kind
        member _.Subject = subject
        /// The `in=` value, or null.
        member _.Owner = owner
        member _.Fields = fields
        /// The declaration block (consecutive lines) the record sits in.
        member _.Block = block
        member val Consumed = false with get, set

    /// Splits `kind subject key=value ...`; values are a JSON string literal, a bracket list or a bare token.
    let private parseLine (text: string) (line: int) (lineStart: int) (lineEnd: int) (block: List<Record>) : Record =
        let n = lineEnd
        let mutable i = lineStart
        let col j = j - lineStart + 1
        let token () =
            let start = i
            while i < n && text[i] <> ' ' do i <- i + 1
            if i = start then fail line (col start) "empty token"
            start
        let space () =
            if i < n then
                if text[i] <> ' ' then fail line (col i) "expected a space"
                i <- i + 1
                if i >= n || text[i] = ' ' then fail line (col i) "single spaces only, no trailing space"
        let kindStart = token ()
        for j in kindStart .. i - 1 do
            let c = text[j]
            if not ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-') then fail line (col kindStart) $"bad record kind '{text.Substring(kindStart, i - kindStart)}'"
        let kind = intern (text.AsSpan(kindStart, i - kindStart))
        space ()
        let subjectStart = token ()
        let subject = text.Substring(subjectStart, i - subjectStart)
        let fields = List<Field>()
        while i < n do
            space ()
            let start = i
            while i < n && text[i] <> '=' && text[i] <> ' ' do i <- i + 1
            if i >= n || text[i] <> '=' then fail line (col start) "expected key=value"
            if i = start then fail line (col start) "empty key"
            let key = intern (text.AsSpan(start, i - start))
            i <- i + 1
            let vstart = i
            if i < n && text[i] = '"' then
                i <- i + 1
                while i < n && text[i] <> '"' do
                    if text[i] = '\\' then i <- i + 1
                    i <- i + 1
                if i >= n then fail line (col vstart) "unterminated string"
                i <- i + 1
                if not (validStringBody text (vstart + 1) (i - 1)) then fail line (col vstart) "bad string literal"
            elif i < n && text[i] = '[' then
                while i < n && text[i] <> ']' do i <- i + 1
                if i >= n then fail line (col vstart) "unterminated list"
                i <- i + 1
            else
                while i < n && text[i] <> ' ' do i <- i + 1
                if i = vstart then fail line (col vstart) $"empty value for {key}"
            fields.Add { Key = key; Start = vstart; Length = i - vstart; Column = col start }
        for a in 0 .. fields.Count - 1 do
            for b in a + 1 .. fields.Count - 1 do
                if obj.ReferenceEquals(fields[a].Key, fields[b].Key) then fail line fields[b].Column "duplicate key"
        let owner = if fields.Count > 0 && obj.ReferenceEquals(fields[0].Key, inKey) then text.Substring(fields[0].Start, fields[0].Length) else null
        Record(line, col subjectStart, col n, kind, subject, owner, fields.ToArray(), block)

    // ---------------------------------------------------------------------------------------------
    // Type expressions
    // ---------------------------------------------------------------------------------------------

    let rec private writeType (w: Utf8JsonWriter) (line: int) (column: int) (text: string) (start: int) (len: int) =
        let bad () = fail line column $"bad type expression '{text.Substring(start, len)}'"
        let s = text.AsSpan(start, len)
        if len = 0 then bad ()
        elif s[0] = '(' then
            let mutable depth, i, close = 0, 0, -1
            while close < 0 && i < len do
                if s[i] = '(' then depth <- depth + 1 elif s[i] = ')' then depth <- depth - 1
                if depth = 0 then close <- i
                i <- i + 1
            if close < 0 || close + 3 >= len || s[close + 1] <> '-' || s[close + 2] <> '>' then bad ()
            w.WriteStartObject()
            w.WriteString("kind", "function")
            w.WriteStartArray "parameters"
            writeArgs w line column text (start + 1) (close - 1)
            w.WriteEndArray()
            w.WritePropertyName "resultType"
            writeType w line column text (start + close + 3) (len - close - 3)
            w.WriteEndObject()
        else
            let paren = s.IndexOf '('
            let headLen = if paren < 0 then len else paren
            // A generic may take no arguments (`task-scope()`); a named type omits empty arguments.
            if paren >= 0 && (s[len - 1] <> ')' || (len - paren - 2 <= 0 && s.IndexOf ':' >= 0)) then bad ()
            let head = s.Slice(0, headLen)
            let colon = head.IndexOf ':'
            if colon >= 0 then
                if not (isId head) then bad ()
                let uuid = head.Slice(colon + 1)
                w.WriteStartObject()
                if head.StartsWith("type:", StringComparison.Ordinal) then
                    w.WriteString("kind", "named")
                    w.WriteString("typeId", uuid)
                    if paren >= 0 then
                        w.WriteStartArray "arguments"
                        writeArgs w line column text (start + paren + 1) (len - paren - 2)
                        w.WriteEndArray()
                elif paren >= 0 then bad ()
                elif head.StartsWith("type-parameter:", StringComparison.Ordinal) then
                    w.WriteString("kind", "typeParameter")
                    w.WriteString("typeParameterId", uuid)
                elif head.StartsWith("capability:", StringComparison.Ordinal) then
                    w.WriteString("kind", "capability")
                    w.WriteString("capabilityId", uuid)
                else bad ()
                w.WriteEndObject()
            elif paren < 0 then
                if not (primitiveLookup.Contains head) then bad ()
                w.WriteStartObject()
                w.WriteString("kind", "primitive")
                w.WriteString("primitive", head)
                w.WriteEndObject()
            else
                let mutable generic = null
                if not (genericLookup.TryGetValue(head, &generic)) then bad ()
                w.WriteStartObject()
                w.WriteString("kind", "builtinGeneric")
                w.WriteString("generic", generic)
                w.WriteStartArray "arguments"
                writeArgs w line column text (start + paren + 1) (len - paren - 2)
                w.WriteEndArray()
                w.WriteEndObject()

    /// Comma-separated type expressions (commas inside parentheses belong to the argument).
    and private writeArgs (w: Utf8JsonWriter) (line: int) (column: int) (text: string) (start: int) (len: int) =
        if len > 0 then
            let s = text.AsSpan(start, len)
            let mutable depth, from = 0, 0
            for i in 0 .. len - 1 do
                match s[i] with
                | ',' when depth = 0 ->
                    writeType w line column text (start + from) (i - from)
                    from <- i + 1
                | '(' -> depth <- depth + 1
                | ')' -> depth <- depth - 1
                | _ -> ()
            writeType w line column text (start + from) (len - from)

    // ---------------------------------------------------------------------------------------------
    // Decode: rows -> tree -> the writer's JSON bytes -> SnapshotCodec's reader
    // ---------------------------------------------------------------------------------------------

    let private buffers = new Threading.ThreadLocal<ArrayBufferWriter<byte>>(fun () -> ArrayBufferWriter<byte>(1 <<< 16))

    let private editDistanceOne (a: string) (b: string) =
        if a.Length = b.Length then (Seq.zip a b |> Seq.filter (fun (x, y) -> x <> y) |> Seq.length) = 1
        elif abs (a.Length - b.Length) = 1 then
            let longer, shorter = (if a.Length > b.Length then a, b else b, a)
            let mutable i = 0
            while i < shorter.Length && longer[i] = shorter[i] do i <- i + 1
            longer.Substring(i + 1) = shorter.Substring i
        else false

    let private decodeWith (dependency: Snapshot option) (text: string) : Result<Snapshot, RecordCodecError> =
        let records = List<Record>()
        try
            if isNull text || text.Length = 0 then fail 1 1 "missing header"
            if text[0] = '﻿' then fail 1 1 "byte order mark"
            let cr = text.IndexOf '\r'
            if cr >= 0 then
                let before = text.Substring(0, cr)
                fail (before.Split('\n').Length) (cr - before.LastIndexOf '\n') "carriage return"
            if text[text.Length - 1] <> '\n' then
                let lastBreak = text.LastIndexOf '\n'
                fail (text.Split('\n').Length) (text.Length - lastBreak) "missing final newline"
            let positionValue (r: Record) =
                // -1 when absent, -2 when not canonical.
                if r.Fields.Length > 1 && obj.ReferenceEquals(r.Fields[1].Key, positionKey) then
                    let span = text.AsSpan(r.Fields[1].Start, r.Fields[1].Length)
                    let mutable value = 0
                    if isCanonicalInt span && span[0] <> '-' && Int32.TryParse(span, &value) then value else -2
                else -1
            let buffer = buffers.Value
            buffer.ResetWrittenCount()
            use w = new Utf8JsonWriter(buffer, JsonWriterOptions(MaxDepth = SnapshotCodec.maxJsonDepth))
            // Header.
            let headerEnd = text.IndexOf '\n'
            if headerEnd = 0 then fail 1 1 "missing header"
            let head = parseLine text 1 0 headerEnd null
            if head.Kind <> "snapshot" then fail 1 1 "header must start with 'snapshot'"
            if not (hasDomain (head.Subject.AsSpan()) "snapshot") then fail 1 head.Column $"expected a snapshot ID, got '{head.Subject}'"
            w.WriteStartObject()
            let fields = head.Fields
            let mutable fi = 0
            if fi >= fields.Length || fields[fi].Key <> "schema" then fail 1 head.EndColumn "missing schema="
            if not (isCanonicalInt (text.AsSpan(fields[fi].Start, fields[fi].Length))) then fail 1 fields[fi].Column "schema= must be an integer"
            w.WritePropertyName "schemaVersion"
            w.WriteRawValue(text.AsSpan(fields[fi].Start, fields[fi].Length), true)
            w.WriteString("snapshotId", head.Subject.AsSpan 9)
            fi <- fi + 1
            if fi < fields.Length && fields[fi].Key = "parent" then
                let v = text.AsSpan(fields[fi].Start, fields[fi].Length)
                if not (hasDomain v "snapshot") then fail 1 fields[fi].Column "parent= must be a snapshot ID"
                w.WriteString("parentSnapshotId", v.Slice 9)
                fi <- fi + 1
            if fi < fields.Length && fields[fi].Key = "dependency" then
                let d = fields[fi]
                if text[d.Start] <> '"' || not (isCanonicalString (text.Substring(d.Start, d.Length))) then fail 1 d.Column "dependency= must be a quoted string as the writer's encoder spells it"
                if fi + 1 >= fields.Length || fields[fi + 1].Key <> "digest" then fail 1 (if fi + 1 < fields.Length then fields[fi + 1].Column else head.EndColumn) "dependency= must be followed by digest="
                let g = fields[fi + 1]
                for j in g.Start .. g.Start + g.Length - 1 do
                    if not (isHex text[j]) then fail 1 g.Column "digest= must be lowercase hex"
                w.WriteStartObject "dependency"
                w.WritePropertyName "snapshotId"
                w.WriteRawValue(text.AsSpan(d.Start, d.Length), true)
                w.WriteString("digest", text.AsSpan(g.Start, g.Length))
                w.WriteEndObject()
                fi <- fi + 2
            if fi < fields.Length then fail 1 fields[fi].Column $"unknown header key {fields[fi].Key}"
            // Rows: tokenize, index and check the block grammar in one pass.
            let blocks = List<List<Record>>()
            let subjects = Dictionary<string, Record>()
            let subjectLookup = subjects.GetAlternateLookup<ReadOnlySpan<char>>()
            let owned = Dictionary<string, List<Record>>()
            let arms = Dictionary<string, List<Record>>()
            let mutable pos = headerEnd + 1
            let mutable line = 2
            // 0: after the header, 1: after a blank line, 2: inside a block.
            let mutable state = 0
            let mutable block: List<Record> = null
            let mutable declaration: Record = null
            let mutable phase = 0
            let mutable nextPosition = 0
            let mutable previousSubject: string = null
            let mutable previousLoose: string = null
            let mutable groupIndex = -1
            let mutable lastDeclaration: string = null
            // The placement of `position=` records (the declaration's own type parameters, then its
            // parameters, both by position, before every other member) is checked here but reported
            // last, after the structural and model checks, as the harness's re-encode comparison was.
            let mutable deferred: RecordCodecError option = None
            let defer line column message = if deferred.IsNone then deferred <- Some { Line = line; Column = column; Message = message }
            while pos < text.Length do
                let lineEnd = text.IndexOf('\n', pos)
                if lineEnd = pos then
                    if state = 1 then fail line 1 "expected a declaration record after a blank line"
                    state <- 1
                else
                    if state = 0 then fail line 1 "expected a blank line before a declaration"
                    if state = 1 then
                        block <- List<Record>()
                        blocks.Add block
                    let r = parseLine text line pos lineEnd block
                    if not (isSubjectSyntax (r.Subject.AsSpan())) then fail r.Line r.Column $"malformed subject '{r.Subject}'"
                    if not (isNull r.Owner) && not (isSubjectSyntax (r.Owner.AsSpan())) then fail r.Line r.Fields[0].Column $"malformed owner '{r.Owner}'"
                    if subjects.ContainsKey r.Subject then fail r.Line r.Column $"duplicate record {r.Subject}"
                    subjects[r.Subject] <- r
                    records.Add r
                    block.Add r
                    if r.Kind = "arm" then
                        if not (isNull r.Owner) then fail r.Line r.Fields[0].Column "an arm record carries no in="
                        let slash = r.Subject.IndexOf '/'
                        if slash < 0 || not (r.Subject.AsSpan(slash).StartsWith("/arm/variant:", StringComparison.Ordinal)) then fail r.Line r.Column $"malformed arm subject '{r.Subject}'"
                        let matchSubject = r.Subject.Substring(0, slash)
                        match arms.TryGetValue matchSubject with
                        | true, list -> list.Add r
                        | _ -> arms[matchSubject] <- List<Record>([ r ])
                    elif r.Subject.Contains '/' then fail r.Line r.Column "a locator cannot be a record subject here"
                    if not (isNull r.Owner) then
                        match owned.TryGetValue r.Owner with
                        | true, list -> list.Add r
                        | _ -> owned[r.Owner] <- List<Record>([ r ])
                    if state = 1 then
                        if r.Kind = "arm" || not (isNull r.Owner) then fail r.Line 1 "expected a declaration record after a blank line"
                        match groupOfKind.TryGetValue r.Kind with
                        | false, _ -> fail r.Line 1 $"'{r.Kind}' is not a declaration kind"
                        | true, g ->
                            if g < groupIndex || (g = groupIndex && String.CompareOrdinal(lastDeclaration, r.Subject) >= 0) then fail r.Line r.Column $"declaration {r.Subject} is out of canonical order"
                            groupIndex <- g
                            lastDeclaration <- r.Subject
                        declaration <- r
                        phase <- 0
                        nextPosition <- 0
                        previousSubject <- null
                        previousLoose <- null
                        state <- 2
                    else
                        if isNull r.Owner && r.Kind <> "arm" then
                            if groupOfKind.ContainsKey r.Kind then fail r.Line 1 "expected a blank line before a declaration"
                            else fail r.Line 1 $"'{r.Kind}' is not a declaration kind"
                        // Records without `position=` must be in subject order (reported at once).
                        if positionValue r = -1 then
                            if not (isNull previousLoose) && String.CompareOrdinal(previousLoose, r.Subject) > 0 then fail r.Line r.Column $"record {r.Subject} is out of canonical order"
                            previousLoose <- r.Subject
                        let positioned = r.Kind = "type-parameter" || r.Kind = "parameter"
                        if positioned && r.Owner = declaration.Subject then
                            let p = if r.Kind = "type-parameter" then 0 else 1
                            if p < phase || phase = 2 then defer r.Line r.Column $"record {r.Subject} is out of canonical order"
                            if p > phase then
                                phase <- p
                                nextPosition <- 0
                            match positionValue r with
                            | -1 -> defer r.Line r.EndColumn "missing position="
                            | -2 -> defer r.Line r.Fields[1].Column "position= must be an integer"
                            | p when p <> nextPosition -> defer r.Line r.Fields[1].Column $"position= must be {nextPosition}"
                            | _ -> nextPosition <- nextPosition + 1
                        else
                            phase <- 2
                            if not (isNull previousSubject) && String.CompareOrdinal(previousSubject, r.Subject) > 0 then defer r.Line r.Column $"record {r.Subject} is out of canonical order"
                            previousSubject <- r.Subject
                pos <- lineEnd + 1
                line <- line + 1
            if state = 1 then fail (line - 1) 1 "trailing blank line"
            // Structural checks before any model is built: missing owners and dangling references,
            // reported on the line where the record is or should be. A reference one edit away from an
            // existing subject is a misspelling, reported where it is written; otherwise the line where
            // the record would sit in canonical order (members by subject inside the declaration's
            // block, ordered members by the first gap in `position=`, a declaration at its block's first line).
            let slot (block: List<Record>) (domain: string) (subject: string) (fallback: int) =
                let same = block |> Seq.filter (fun r -> r.Subject.StartsWith(domain + ":", StringComparison.Ordinal)) |> List.ofSeq
                if domain = "parameter" || domain = "type-parameter" then
                    let positioned = same |> List.filter (fun r -> positionValue r <> -1)
                    match positioned |> List.mapi (fun i r -> i, r) |> List.tryFind (fun (i, r) -> positionValue r <> i) with
                    | Some(_, r) -> r.Line
                    | None when not positioned.IsEmpty -> (List.last positioned).Line + 1
                    | None -> block[0].Line + 1
                else
                    match same |> List.tryFind (fun r -> String.CompareOrdinal(r.Subject, subject) > 0) with
                    | Some r -> r.Line
                    | None when not same.IsEmpty -> (List.last same).Line + 1
                    | None -> if declarationDomains.Contains domain then block[0].Line else fallback
            let misspelled (subject: string) = subjects.Keys |> Seq.exists (editDistanceOne subject)
            let blockOfRecord (subject: string) = match subjects.TryGetValue subject with | true, r -> Some r.Block | _ -> None
            let field (r: Record) (name: string) = r.Fields |> Array.tryFind (fun f -> f.Key = name) |> Option.map (fun f -> text.Substring(f.Start, f.Length))
            for r in records do
                if not (isNull r.Owner) then
                    let o = r.Owner
                    let needed = if o.Contains "/arm/" then o else (match o.IndexOf '/' with -1 -> o | i -> o.Substring(0, i))
                    if not (subjects.ContainsKey needed) then
                        let domain = needed.Substring(0, needed.IndexOf ':')
                        if misspelled needed then fail r.Line r.Fields[0].Column $"owner {o} not found"
                        else fail (slot r.Block domain needed r.Line) 1 $"owner {o} not found"
                for f in r.Fields do
                    if not (obj.ReferenceEquals(f.Key, inKey)) && text[f.Start] <> '"' then
                        // Every `domain:uuid` token in the value (lists, pairs and type expressions included).
                        let stop = f.Start + f.Length
                        let mutable i = f.Start
                        while i < stop do
                            if text[i] = ':' && i + 37 <= stop && isUuid (text.AsSpan(i + 1, 36)) then
                                let mutable d = i
                                while d > f.Start && isDomainChar text[d - 1] do d <- d - 1
                                if d < i && memberDomainLookup.Contains(text.AsSpan(d, i - d)) && not (subjectLookup.ContainsKey(text.AsSpan(d, i + 37 - d))) then
                                    let subject = text.Substring(d, i + 37 - d)
                                    let domain = text.Substring(d, i - d)
                                    let line, column =
                                        if misspelled subject then r.Line, f.Column
                                        elif (domain = "node" || domain = "binding" || domain = "type-parameter") && not (isNull r.Owner) then slot r.Block domain subject r.Line, 1
                                        elif domain = "parameter" then
                                            match field r "target-function" |> Option.bind blockOfRecord with
                                            | Some callee -> slot callee domain subject r.Line, 1
                                            | None -> if not (isNull r.Owner) then slot r.Block domain subject r.Line, 1 else r.Line, f.Column
                                        elif domain = "capability-parameter" then
                                            match field r "operation" |> Option.bind blockOfRecord with
                                            | Some callee -> slot callee domain subject r.Line, 1
                                            | None -> r.Line, f.Column
                                        else
                                            // The block whose same-domain subjects bracket the missing one, else this line.
                                            blocks
                                            |> Seq.tryPick (fun b ->
                                                let same = b |> Seq.filter (fun x -> x.Subject.StartsWith(domain + ":", StringComparison.Ordinal)) |> List.ofSeq
                                                if same.IsEmpty then None
                                                elif String.CompareOrdinal(same.Head.Subject, subject) < 0 && String.CompareOrdinal((List.last same).Subject, subject) > 0 then Some(slot b domain subject r.Line, 1)
                                                else None)
                                            |> Option.defaultValue (r.Line, f.Column)
                                    fail line column $"{f.Key}= refers to {subject}, which has no record"
                                i <- i + 37
                            else i <- i + 1
            // Materialize: the writer's JSON, checking key presence and order against the kind table
            // and every value against its shape.
            let badValue (r: Record) (f: Field) (key: Key) = fail r.Line f.Column $"bad value for {key.Record}="
            let listBounds (r: Record) (f: Field) (key: Key) =
                if text[f.Start] <> '[' || text[f.Start + f.Length - 1] <> ']' then fail r.Line f.Column $"{key.Record}= must be a list"
                if f.Length = 2 then fail r.Line f.Column $"{key.Record}= must not be empty"
                f.Start + 1, f.Start + f.Length - 1
            /// Calls `item start length` for each space-separated item of a list value; a sorted list
            /// must be strictly increasing by item text (every ID has the same length, so that is key order).
            let listItems (r: Record) (f: Field) (key: Key) (sorted: bool) (item: int -> int -> unit) =
                let start, stop = listBounds r f key
                let mutable from = start
                let mutable previous = -1
                for i in start .. stop do
                    if i = stop || text[i] = ' ' then
                        if i = from then fail r.Line f.Column $"bad list for {key.Record}="
                        if sorted && previous >= 0 && text.AsSpan(previous, from - 1 - previous).SequenceCompareTo(text.AsSpan(from, i - from)) >= 0 then
                            fail r.Line f.Column $"{key.Record}= is not in canonical order"
                        item from (i - from)
                        previous <- from
                        from <- i + 1
            let rec materialize (block: List<Record>) (r: Record) =
                r.Consumed <- true
                if not (obj.ReferenceEquals(r.Block, block)) then fail r.Line r.Column $"record {r.Subject} belongs in the block of its declaration"
                let kind = match kinds.TryGetValue r.Kind with | true, k -> k | _ -> fail r.Line 1 $"unknown record kind '{r.Kind}'"
                let domain = subjectDomain r.Kind r.Owner
                if not (hasDomain (r.Subject.AsSpan()) domain) then fail r.Line r.Column $"expected a {domain} ID, got '{r.Subject}'"
                let mutable fi = if isNull r.Owner then 0 else 1
                let positioned = r.Kind = "parameter" || r.Kind = "type-parameter"
                if fi < r.Fields.Length && obj.ReferenceEquals(r.Fields[fi].Key, positionKey) then
                    if not positioned then fail r.Line r.Fields[fi].Column "position= is only for parameters and type parameters"
                    if positionValue r < 0 then fail r.Line r.Fields[fi].Column "position= must be an integer"
                    fi <- fi + 1
                elif positioned then fail r.Line r.EndColumn "missing position="
                w.WriteStartObject()
                body block r kind fi domain
                w.WriteEndObject()
            and body (block: List<Record>) (r: Record) (kind: Kind) (first: int) (domain: string) =
                let mutable fi = first
                let members = match owned.TryGetValue r.Subject with | true, m -> m | _ -> null
                for key in kind.Keys do
                    match key.Shape with
                    | Meta -> if key.Json = "kind" then w.WriteString("kind", kind.JsonKind) else w.WriteString(key.Json, r.Subject.AsSpan(domain.Length + 1))
                    | Members m ->
                        let items = List<Record>()
                        if not (isNull members) then
                            for x in members do
                                if obj.ReferenceEquals(x.Kind, m) then items.Add x
                        if items.Count > 0 || not key.OmitEmpty then
                            w.WriteStartArray key.Json
                            if ordered.Contains key.Json then
                                items.Sort(fun a b -> compare (positionValue a) (positionValue b))
                                for i in 0 .. items.Count - 1 do
                                    let column = if items[i].Fields.Length > 1 then items[i].Fields[1].Column else items[i].EndColumn
                                    match positionValue items[i] with
                                    | -2 -> fail items[i].Line column "position= must be an integer"
                                    | p when p <> i -> fail items[i].Line column $"position= must be {i}"
                                    | _ -> ()
                            for x in items do materialize block x
                            w.WriteEndArray()
                    | Arms ->
                        w.WriteStartArray key.Json
                        match arms.TryGetValue r.Subject with
                        | true, list -> for a in list do arm block r a
                        | _ -> ()
                        w.WriteEndArray()
                    | shape ->
                        if fi < r.Fields.Length && obj.ReferenceEquals(r.Fields[fi].Key, key.Record) then
                            value block r r.Fields[fi] key
                            fi <- fi + 1
                        else
                            let later = r.Fields |> Array.tryFind (fun f -> obj.ReferenceEquals(f.Key, key.Record))
                            match shape, later with
                            | _, Some f -> fail r.Line f.Column $"{key.Record}= is out of canonical order"
                            | (Graph | ValueArm _), None -> fail r.Line r.EndColumn $"missing {key.Record}="
                            | (TypeList | IdList _ | Pairs _), None ->
                                if not key.OmitEmpty then
                                    w.WriteStartArray key.Json
                                    w.WriteEndArray()
                            | _, None -> if not key.Optional then fail r.Line r.EndColumn $"missing {key.Record}="
                if fi < r.Fields.Length then fail r.Line r.Fields[fi].Column $"unknown key {r.Fields[fi].Key}= on {r.Kind}"
                if not (isNull members) then
                    for x in members do
                        if not x.Consumed then fail x.Line x.Column $"{x.Kind} record cannot be owned by {r.Kind} {r.Subject}"
            and value (block: List<Record>) (r: Record) (f: Field) (key: Key) =
                let s = text.AsSpan(f.Start, f.Length)
                match key.Shape with
                | Scalar(Id d) ->
                    if not (hasDomain s d) then fail r.Line f.Column $"expected a {d} ID, got '{s.ToString()}'"
                    w.WriteString(key.Json, s.Slice(d.Length + 1))
                | Scalar Bool ->
                    if s.SequenceEqual("true".AsSpan()) then w.WriteBoolean(key.Json, true)
                    // The writer omits an optional boolean that is false (`exported`: SnapshotCodec.fs writeVisibility).
                    elif s.SequenceEqual("false".AsSpan()) && key.Optional then fail r.Line f.Column $"{key.Record}=false is omitted"
                    elif s.SequenceEqual("false".AsSpan()) then w.WriteBoolean(key.Json, false)
                    else badValue r f key
                | Scalar Int ->
                    if not (isCanonicalInt s) then badValue r f key
                    w.WritePropertyName key.Json
                    w.WriteRawValue(s, true)
                | Scalar Str ->
                    if s[0] <> '"' then badValue r f key
                    if not (isCanonicalString (s.ToString())) then fail r.Line f.Column $"{key.Record}= is not spelled as the writer's encoder spells it"
                    w.WritePropertyName key.Json
                    w.WriteRawValue(s, true)
                | TypeExpr ->
                    w.WritePropertyName key.Json
                    writeType w r.Line f.Column text f.Start f.Length
                | TypeList ->
                    w.WriteStartArray key.Json
                    listItems r f key false (fun start length -> writeType w r.Line f.Column text start length)
                    w.WriteEndArray()
                | IdList d ->
                    w.WriteStartArray key.Json
                    listItems r f key (sortedIdLists.Contains key.Json) (fun start length ->
                        if not (hasDomain (text.AsSpan(start, length)) d) then fail r.Line f.Column $"expected a {d} ID, got '{text.Substring(start, length)}'"
                        w.WriteStringValue(text.AsSpan(start + d.Length + 1, 36)))
                    w.WriteEndArray()
                | Pairs(k1, d1, k2, d2) ->
                    w.WriteStartArray key.Json
                    listItems r f key true (fun start length ->
                        let eq = text.IndexOf('=', start, length)
                        if eq < 0 || not (hasDomain (text.AsSpan(start, eq - start)) d1) || not (hasDomain (text.AsSpan(eq + 1, start + length - eq - 1)) d2) then
                            fail r.Line f.Column $"bad pair '{text.Substring(start, length)}' in {key.Record}="
                        w.WriteStartObject()
                        w.WriteString(k1, text.AsSpan(start + d1.Length + 1, 36))
                        w.WriteString(k2, text.AsSpan(eq + 1 + d2.Length + 1, 36))
                        w.WriteEndObject())
                    w.WriteEndArray()
                | Graph ->
                    if not (hasDomain s "node") then fail r.Line f.Column $"expected a node ID, got '{s.ToString()}'"
                    let loc = r.Subject + "/" + key.Record
                    w.WritePropertyName key.Json
                    graph block loc f false
                | ValueArm bindingKey ->
                    if not (hasDomain s "node") then fail r.Line f.Column $"expected a node ID, got '{s.ToString()}'"
                    let loc = r.Subject + "/" + key.Record
                    let mutable binding: Record = null
                    let mutable count = 0
                    match owned.TryGetValue loc with
                    | true, members ->
                        for m in members do
                            if m.Kind = "binding" then
                                binding <- m
                                count <- count + 1
                    | _ -> ()
                    if count <> 1 then fail r.Line f.Column $"{key.Record}= needs exactly one binding record in {loc}"
                    w.WriteStartObject key.Json
                    w.WritePropertyName bindingKey
                    writeBinding block binding false
                    w.WritePropertyName "body"
                    graph block loc f true
                    w.WriteEndObject()
                | Meta | Arms | Members _ -> fail r.Line f.Column $"{key.Record}= cannot be given inline"
            /// `{nodes, resultNodeId}` for the graph at `loc`; `result` holds `node:R`.
            and graph (block: List<Record>) (loc: string) (result: Field) (skipBindings: bool) =
                w.WriteStartObject()
                w.WriteStartArray "nodes"
                match owned.TryGetValue loc with
                | true, members ->
                    for m in members do
                        if m.Kind = "binding" && skipBindings then ()
                        elif m.Kind = "binding" || m.Kind = "arm" || collectionOf.ContainsKey m.Kind || groupOfKind.ContainsKey m.Kind then
                            fail m.Line m.Column $"{m.Kind} record cannot be owned by graph {loc}"
                        else materialize block m
                | _ -> ()
                w.WriteEndArray()
                w.WriteString("resultNodeId", text.AsSpan(result.Start + 5, 36))
                w.WriteEndObject()
            and writeBinding (block: List<Record>) (b: Record) (withSourceField: bool) =
                b.Consumed <- true
                if not (obj.ReferenceEquals(b.Block, block)) then fail b.Line b.Column $"record {b.Subject} belongs in the block of its declaration"
                if b.Kind <> "binding" then fail b.Line 1 $"expected a binding record, got {b.Kind}"
                if not (hasDomain (b.Subject.AsSpan()) "binding") then fail b.Line b.Column $"expected a binding ID, got '{b.Subject}'"
                let mutable fi = 1
                if withSourceField then
                    if fi >= b.Fields.Length || b.Fields[fi].Key <> "source-field" then fail b.Line (if fi < b.Fields.Length then b.Fields[fi].Column else b.EndColumn) "missing source-field="
                    let f = b.Fields[fi]
                    if not (hasDomain (text.AsSpan(f.Start, f.Length)) "field") then fail b.Line f.Column $"expected a field ID, got '{text.Substring(f.Start, f.Length)}'"
                    w.WriteStartObject()
                    w.WriteString("sourceFieldId", text.AsSpan(f.Start + 6, 36))
                    w.WriteStartObject "binding"
                    fi <- fi + 1
                else w.WriteStartObject()
                body block b kinds["binding"] fi "binding"
                w.WriteEndObject()
                if withSourceField then w.WriteEndObject()
            and arm (block: List<Record>) (m: Record) (a: Record) =
                a.Consumed <- true
                if not (obj.ReferenceEquals(a.Block, block)) then fail a.Line a.Column $"record {a.Subject} belongs in the block of its declaration"
                let key = a.Subject.AsSpan(m.Subject.Length + 5)
                if not (hasDomain key "variant") then fail a.Line a.Column $"malformed arm subject '{a.Subject}'"
                let mutable fi = 0
                w.WriteStartObject()
                if fi < a.Fields.Length && a.Fields[fi].Key = "variants" then
                    let f = a.Fields[fi]
                    let bounds = listBounds a f { Json = "variantIds"; Record = "variants"; Shape = IdList "variant"; Optional = true; OmitEmpty = true }
                    let mutable count = 0
                    let mutable previousItem = 0
                    w.WriteStartArray "variantIds"
                    let mutable from = fst bounds
                    for i in fst bounds .. snd bounds do
                        if i = snd bounds || text[i] = ' ' then
                            let item = text.AsSpan(from, i - from)
                            if not (hasDomain item "variant") then fail a.Line f.Column "variants= must list two or more variants starting with the locator's key"
                            if count = 0 && not (item.SequenceEqual key) then fail a.Line f.Column "variants= must list two or more variants starting with the locator's key"
                            if count > 0 && text.AsSpan(previousItem, from - 1 - previousItem).SequenceCompareTo item >= 0 then fail a.Line f.Column "variants= is not in canonical order"
                            previousItem <- from
                            w.WriteStringValue(item.Slice 8)
                            count <- count + 1
                            from <- i + 1
                    w.WriteEndArray()
                    if count < 2 then fail a.Line f.Column "variants= must list two or more variants starting with the locator's key"
                    fi <- fi + 1
                else w.WriteString("variantId", key.Slice 8)
                if fi >= a.Fields.Length || a.Fields[fi].Key <> "body" then fail a.Line (if fi < a.Fields.Length then a.Fields[fi].Column else a.EndColumn) "missing body="
                let result = a.Fields[fi]
                if not (hasDomain (text.AsSpan(result.Start, result.Length)) "node") then fail a.Line result.Column $"expected a node ID, got '{text.Substring(result.Start, result.Length)}'"
                fi <- fi + 1
                if fi < a.Fields.Length then fail a.Line a.Fields[fi].Column $"unknown key {a.Fields[fi].Key}= on arm"
                let bindings = List<struct (string * Record)>()
                match owned.TryGetValue a.Subject with
                | true, members ->
                    for b in members do
                        if b.Kind = "binding" then
                            let source = if b.Fields.Length > 1 && b.Fields[1].Key = "source-field" then text.Substring(b.Fields[1].Start, b.Fields[1].Length) else ""
                            bindings.Add(struct (source, b))
                | _ -> ()
                bindings.Sort(fun (struct (x, _)) (struct (y, _)) -> String.CompareOrdinal(x, y))
                w.WriteStartArray "fieldBindings"
                for struct (_, b) in bindings do writeBinding block b true
                w.WriteEndArray()
                w.WritePropertyName "body"
                graph block a.Subject result true
                w.WriteEndObject()
            // Declarations in file order, which the block grammar verified is group order.
            let mutable g = 0
            w.WriteStartArray(fst groups[0])
            for block in blocks do
                let d = block[0]
                let dg = groupOfKind[d.Kind]
                while g < dg do
                    w.WriteEndArray()
                    g <- g + 1
                    w.WriteStartArray(fst groups[g])
                materialize block d
            while g < groups.Length - 1 do
                w.WriteEndArray()
                g <- g + 1
                w.WriteStartArray(fst groups[g])
            w.WriteEndArray()
            w.WriteEndObject()
            w.Flush()
            for r in records do
                if not r.Consumed then fail r.Line r.Column (if isNull r.Owner then "orphan record" else $"owner {r.Owner} not found")
            match SnapshotCodec.deserializeUtf8 dependency buffer.WrittenMemory with
            | Ok _ when deferred.IsSome -> Error deferred.Value
            | Ok snapshot -> Ok snapshot
            | Error error ->
                // A shape or validation error has no line of its own: it is reported on the first record
                // (in file order) whose subject UUID appears earliest in the error text, else on line 1.
                let message = sprintf "%A" error
                let mutable best = Int32.MaxValue
                let mutable where: Record = null
                for r in records do
                    let colon = r.Subject.IndexOf ':'
                    let at = message.AsSpan().IndexOf(r.Subject.AsSpan(colon + 1, 36))
                    if at >= 0 && at < best then
                        best <- at
                        where <- r
                Error { Line = (if isNull where then 1 else where.Line); Column = (if isNull where then 1 else where.Column); Message = message }
        with RecordFailure e -> Error e

    /// Record text -> the snapshot, validated by `SnapshotCodec`'s reader. Only canonical bytes decode.
    let deserialize (text: string) : Result<Snapshot, RecordCodecError> = decodeWith None text

    /// A local snapshot whose header carries the `dependency=` marker, validated against the dependency
    /// as `SnapshotCodec.deserializeResolved` does.
    let deserializeResolved (dependency: Snapshot) (text: string) : Result<Snapshot, RecordCodecError> = decodeWith (Some dependency) text

    // ---------------------------------------------------------------------------------------------
    // Encode: the writer's JSON -> record text
    // ---------------------------------------------------------------------------------------------

    let rec private typeExpr (t: JsonElement) =
        let args (name: string) = t.GetProperty(name).EnumerateArray() |> Seq.map typeExpr |> String.concat ","
        match t.GetProperty("kind").GetString() with
        | "primitive" -> t.GetProperty("primitive").GetString()
        | "named" -> "type:" + t.GetProperty("typeId").GetString() + (match t.TryGetProperty "arguments" with | true, _ -> "(" + args "arguments" + ")" | _ -> "")
        | "builtinGeneric" -> genericsByJson[t.GetProperty("generic").GetString()] + "(" + args "arguments" + ")"
        | "typeParameter" -> "type-parameter:" + t.GetProperty("typeParameterId").GetString()
        | "capability" -> "capability:" + t.GetProperty("capabilityId").GetString()
        | "function" -> "(" + args "parameters" + ")->" + typeExpr (t.GetProperty "resultType")
        | other -> failwith $"unmapped type reference {other}"

    /// A member line with its place in the declaration's block: the declaration's own type
    /// parameters (phase 0) and parameters (phase 1) by position, then everything else by subject.
    [<Struct>]
    type private Line = { Phase: int; Position: int; Subject: string; Text: string }

    let private kindOf (element: JsonElement) (fallback: string) =
        match element.TryGetProperty "kind" with
        | true, k -> kindsByJson[k.GetString()]
        | _ -> kinds[fallback]

    let rec private renderObject (declaration: string) (kind: Kind) (subject: string) (obj: JsonElement) (owner: string) (position: int) (acc: List<Line>) : string =
        let sb = StringBuilder(kind.Name).Append(' ').Append(subject)
        if not (isNull owner) then sb.Append(" in=").Append(owner) |> ignore
        if position >= 0 then sb.Append(" position=").Append(position) |> ignore
        for key in kind.Keys do
            match key.Shape, obj.TryGetProperty key.Json with
            | Meta, _ | _, (false, _) -> ()
            | shape, (true, v) ->
                let field (value: string) = sb.Append(' ').Append(key.Record).Append('=').Append(value) |> ignore
                let list (items: string seq) = let joined = String.concat " " items in if joined <> "" then field ("[" + joined + "]")
                match shape with
                | Meta -> ()
                | Scalar(Id d) -> field (d + ":" + v.GetString())
                | Scalar _ -> field (v.GetRawText())
                | TypeExpr -> field (typeExpr v)
                | TypeList -> list (v.EnumerateArray() |> Seq.map typeExpr)
                | IdList d -> list (v.EnumerateArray() |> Seq.map (fun x -> d + ":" + x.GetString()))
                | Pairs(k1, d1, k2, d2) -> list (v.EnumerateArray() |> Seq.map (fun p -> d1 + ":" + p.GetProperty(k1).GetString() + "=" + d2 + ":" + p.GetProperty(k2).GetString()))
                | Graph -> field ("node:" + renderGraph declaration v (subject + "/" + key.Record) acc)
                | ValueArm bindingKey ->
                    let loc = subject + "/" + key.Record
                    acc.Add(renderBinding (v.GetProperty bindingKey) loc null)
                    field ("node:" + renderGraph declaration (v.GetProperty "body") loc acc)
                | Arms ->
                    for arm in v.EnumerateArray() do
                        let variants =
                            match arm.TryGetProperty "variantIds" with
                            | true, ids -> ids.EnumerateArray() |> Seq.map (fun x -> x.GetString()) |> List.ofSeq
                            | _ -> [ arm.GetProperty("variantId").GetString() ]
                        let loc = subject + "/arm/variant:" + List.head variants
                        for fb in arm.GetProperty("fieldBindings").EnumerateArray() do
                            acc.Add(renderBinding (fb.GetProperty "binding") loc ("field:" + fb.GetProperty("sourceFieldId").GetString()))
                        let body = renderGraph declaration (arm.GetProperty "body") loc acc
                        let text =
                            "arm " + loc
                            + (if variants.Length > 1 then " variants=[" + (variants |> List.map (fun x -> "variant:" + x) |> String.concat " ") + "]" else "")
                            + " body=node:" + body
                        acc.Add { Phase = 2; Position = 0; Subject = loc; Text = text }
                | Members m ->
                    let isOrdered = ordered.Contains key.Json
                    let domain = if m = "parameter" && kind.Name = "operation" then "capability-parameter" else m
                    v.EnumerateArray()
                    |> Seq.iteri (fun i item ->
                        let id = match item.TryGetProperty "id" with | true, id -> id.GetString() | _ -> item.GetProperty("typeParameterId").GetString()
                        let memberSubject = domain + ":" + id
                        let text = renderObject declaration (kindOf item m) memberSubject item subject (if isOrdered then i else -1) acc
                        let phase = if isOrdered && subject = declaration then (if m = "type-parameter" then 0 else 1) else 2
                        acc.Add { Phase = phase; Position = (if phase = 2 then 0 else i); Subject = memberSubject; Text = text })
        sb.ToString()

    and private renderGraph (declaration: string) (g: JsonElement) (loc: string) (acc: List<Line>) =
        for n in g.GetProperty("nodes").EnumerateArray() do
            let subject = "node:" + n.GetProperty("id").GetString()
            acc.Add { Phase = 2; Position = 0; Subject = subject; Text = renderObject declaration (kindOf n "") subject n loc -1 acc }
        g.GetProperty("resultNodeId").GetString()

    and private renderBinding (b: JsonElement) (owner: string) (sourceField: string) : Line =
        let subject = "binding:" + b.GetProperty("id").GetString()
        let sb = StringBuilder("binding ").Append(subject).Append(" in=").Append(owner)
        if not (isNull sourceField) then sb.Append(" source-field=").Append(sourceField) |> ignore
        for key in kinds["binding"].Keys do
            match key.Shape, b.TryGetProperty key.Json with
            | TypeExpr, (true, v) -> sb.Append(' ').Append(key.Record).Append('=').Append(typeExpr v) |> ignore
            | Scalar _, (true, v) -> sb.Append(' ').Append(key.Record).Append('=').Append(v.GetRawText()) |> ignore
            | _ -> ()
        { Phase = 2; Position = 0; Subject = subject; Text = sb.ToString() }

    /// The writer's JSON (`SnapshotCodec.serialize` output) rendered as record text, mechanically and
    /// without validation; the harness cross-checks it against its renderer.
    let ofJson (json: string) : string =
        use doc = JsonDocument.Parse(json, JsonDocumentOptions(MaxDepth = SnapshotCodec.maxJsonDepth))
        let root = doc.RootElement
        let sb = StringBuilder("snapshot snapshot:").Append(root.GetProperty("snapshotId").GetString()).Append(" schema=").Append(root.GetProperty("schemaVersion").GetRawText())
        match root.TryGetProperty "parentSnapshotId" with
        | true, p -> sb.Append(" parent=snapshot:").Append(p.GetString()) |> ignore
        | _ -> ()
        match root.TryGetProperty "dependency" with
        | true, d -> sb.Append(" dependency=").Append(d.GetProperty("snapshotId").GetRawText()).Append(" digest=").Append(d.GetProperty("digest").GetString()) |> ignore
        | _ -> ()
        sb.Append('\n') |> ignore
        for collection, domain in groups do
            for d in root.GetProperty(collection).EnumerateArray() do
                let subject = domain + ":" + d.GetProperty("id").GetString()
                let acc = List<Line>()
                let first = renderObject subject (kindOf d domain) subject d null -1 acc
                acc.Sort(fun a b ->
                    match compare a.Phase b.Phase with
                    | 0 -> (match compare a.Position b.Position with 0 -> String.CompareOrdinal(a.Subject, b.Subject) | c -> c)
                    | c -> c)
                sb.Append('\n').Append(first).Append('\n') |> ignore
                for l in acc do sb.Append(l.Text).Append('\n') |> ignore
        sb.ToString()

    /// The snapshot in record form (validated by `SnapshotCodec.serialize`).
    let serialize (snapshot: Snapshot) : Result<string, SnapshotCodecError> = SnapshotCodec.serialize snapshot |> Result.map ofJson

    /// `local` with its dependency marker in the header (`SnapshotCodec.serializeResolved`).
    let serializeResolved (dependency: Snapshot) (local: Snapshot) : Result<string, SnapshotCodecError> =
        SnapshotCodec.serializeResolved dependency local |> Result.map ofJson
