/// Record text <-> strict JSON, mechanically following the JSON codec writer's shape
/// (docs/decisions/snapshot-record-format.md, "Per-kind keys"). No model, no validation:
/// `ofJson` walks the writer's JSON; `toJson` rebuilds it using a per-kind key table that is
/// derived at startup by walking the structural all-kinds fixture written by that same writer.
module Nyrwvy.SnapshotRecordFormat.RecordText

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Nyrwvy.Core

type RecordError = { Line: int; Message: string }

exception RecordException of RecordError
let private fail line message = raise (RecordException { Line = line; Message = message })

// ---------------------------------------------------------------------------------------------
// Naming rules
// ---------------------------------------------------------------------------------------------

/// Longest ID noun first; the tags of StableId.fs.
let private domains =
    [ "CapabilityParameterId", "capability-parameter"; "TypeParameterId", "type-parameter"; "PatternBindingId", "binding"
      "OperationId", "operation"; "TransactionId", "transaction"; "NodeId", "node"; "FunctionId", "function"
      "FieldId", "field"; "ParameterId", "parameter"; "VariantId", "variant"; "EffectId", "effect"
      "CapabilityId", "capability"; "TypeId", "type"; "SnapshotId", "snapshot" ]

/// The ID domain an ID-valued JSON key names, from its suffix; None for a free value.
let private domainOf (key: string) =
    domains |> List.tryPick (fun (suffix, domain) -> if key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) then Some domain else None)

/// Bare-string ID arrays: the element domain comes from the model's element type, not the key
/// (SnapshotCodec.fs writeFunction requiredEffects, matchUnion variantIds, listConstruct itemNodeIds,
/// functionInvoke arguments).
let private arrayDomain = dict [ "requiredEffects", "effect"; "variantIds", "variant"; "itemNodeIds", "node"; "arguments", "node" ]

/// Whether a JSON key (or the key of the array holding a bare string) holds IDs.
let isIdKey (key: string) = (domainOf key).IsSome || arrayDomain.ContainsKey key

/// Collections the writer omits when empty (SnapshotCodec.fs writeTypeParameters, writeTypeArguments,
/// writeNamedCallbackContext); every other collection is written even when empty, so a record
/// without it reads as empty.
let private optionalWhenEmpty = set [ "typeParameters"; "typeArguments"; "context"; "bodyContext"; "handlerContext" ]

let private memberKindOf = dict [ "parameters", "parameter"; "fields", "field"; "variants", "variant"; "operations", "operation"; "typeParameters", "type-parameter" ]
let private collectionOf = dict [ "parameter", "parameters"; "field", "fields"; "variant", "variants"; "operation", "operations"; "type-parameter", "typeParameters" ]
/// Collections whose order is semantic carry `position=`.
let private ordered = set [ "parameters"; "typeParameters" ]
let private declarationGroups = [ "types"; "effects"; "capabilities"; "functions"; "obligations" ]
let private groupOfKind = dict [ "product", "types"; "union", "types"; "effect", "effects"; "capability", "capabilities"; "function", "functions"; "missing-argument", "obligations" ]
let private primitives = set [ "string"; "bool"; "unit"; "int64"; "float64"; "bits32" ]
let private generics = set [ "option"; "result"; "map"; "list"; "set"; "one-shot"; "task"; "exception"; "task-scope" ]

let kebab (s: string) = Regex.Replace(s, "(?<=[a-z0-9])([A-Z])", "-$1").ToLowerInvariant()
let camel (s: string) = Regex.Replace(s, "-([a-z])", fun m -> m.Groups[1].Value.ToUpperInvariant())

/// JSON key -> record key.
let keyName (key: string) =
    if key = "displayName" then "name"
    elif key.EndsWith("NodeIds", StringComparison.Ordinal) then kebab (key.Substring(0, key.Length - 7) + "s")
    elif key.EndsWith("Ids", StringComparison.Ordinal) then kebab (key.Substring(0, key.Length - 3) + "s")
    elif key.EndsWith("NodeId", StringComparison.Ordinal) then kebab (key.Substring(0, key.Length - 6))
    elif key.EndsWith("Id", StringComparison.Ordinal) then kebab (key.Substring(0, key.Length - 2))
    else kebab key

let private uuidPattern = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
let private idRegex = Regex($"^([a-z-]+):({uuidPattern})$")

// ---------------------------------------------------------------------------------------------
// Forward: writer JSON -> record text
// ---------------------------------------------------------------------------------------------

/// What a JSON key holds, as observed in the writer's output; the inverse needs it.
type ScalarKind =
    | Id of domain: string
    | Bool
    | Int
    | Str

type Shape =
    | Scalar of ScalarKind
    | Graph
    | ValueArm of bindingKey: string
    | Arms
    | TypeExpr
    | TypeList
    | IdList of domain: string
    | Pairs of string * string
    | Members of memberKind: string
    | Unknown

let private isGraph (v: JsonElement) = v.ValueKind = JsonValueKind.Object && v.TryGetProperty("nodes") |> fst && v.TryGetProperty("resultNodeId") |> fst
let private has (v: JsonElement) name = v.TryGetProperty(name: string) |> fst
let private isValueArm (v: JsonElement) = v.ValueKind = JsonValueKind.Object && has v "body" && not (has v "id") && not (has v "variantId") && not (has v "variantIds")
let private isTypeRef (v: JsonElement) = v.ValueKind = JsonValueKind.Object && has v "kind" && not (has v "id")

let rec private typeExpr (t: JsonElement) =
    let args (name: string) = t.GetProperty(name).EnumerateArray() |> Seq.map typeExpr |> String.concat ","
    match t.GetProperty("kind").GetString() with
    | "primitive" -> t.GetProperty("primitive").GetString()
    | "named" -> "type:" + t.GetProperty("typeId").GetString() + (if has t "arguments" then "(" + args "arguments" + ")" else "")
    | "builtinGeneric" -> kebab (t.GetProperty("generic").GetString()) + "(" + args "arguments" + ")"
    | "typeParameter" when not (has t "displayName") -> "type-parameter:" + t.GetProperty("typeParameterId").GetString()
    | "capability" -> "capability:" + t.GetProperty("capabilityId").GetString()
    | "function" -> "(" + args "parameters" + ")->" + typeExpr (t.GetProperty "resultType")
    | other -> failwith $"unmapped type reference {other}"

/// Observations of one JSON object: its record kind and (JSON key, shape) in writer order.
type private Observe = string -> (string * Shape) list -> unit

let private memberSubject (memberKind: string) (parentKind: string) (m: JsonElement) =
    let domain = if memberKind = "parameter" && parentKind = "operation" then "capability-parameter" else memberKind
    domain + ":" + (if has m "id" then m.GetProperty("id").GetString() else m.GetProperty("typeParameterId").GetString())

let rec private renderObject (observe: Observe) (kind: string) (subject: string) (obj: JsonElement) (owner: string option) (position: int option) (acc: List<string>) : string =
    let fields = List<string * string>()
    let seen = List<string * Shape>()
    for p in obj.EnumerateObject() do
        let jk, v = p.Name, p.Value
        let add shape (value: string option) =
            seen.Add((jk, shape))
            value |> Option.iter (fun value -> fields.Add((keyName jk, value)))
        match jk with
        | "id" | "kind" | "typeParameterId" -> seen.Add((jk, Scalar Str))
        | _ when isGraph v -> add Graph (Some("node:" + renderGraph observe v (subject + "/" + keyName jk) acc))
        | _ when isValueArm v ->
            let loc = subject + "/" + keyName jk
            let bindingKey = if has v "errorBinding" then "errorBinding" else "valueBinding"
            acc.Add(renderBinding observe (v.GetProperty bindingKey) loc [])
            add (ValueArm bindingKey) (Some("node:" + renderGraph observe (v.GetProperty "body") loc acc))
        | "arms" ->
            for arm in v.EnumerateArray() do
                let vids =
                    if has arm "variantIds" then arm.GetProperty("variantIds").EnumerateArray() |> Seq.map (fun x -> x.GetString()) |> List.ofSeq
                    else [ arm.GetProperty("variantId").GetString() ]
                let loc = subject + "/arm/variant:" + List.head vids
                for fb in arm.GetProperty("fieldBindings").EnumerateArray() do
                    acc.Add(renderBinding observe (fb.GetProperty "binding") loc [ "source-field", "field:" + fb.GetProperty("sourceFieldId").GetString() ])
                let armFields = if vids.Length > 1 then [ "variants", "[" + (vids |> List.map (fun x -> "variant:" + x) |> String.concat " ") + "]" ] else []
                let body = "node:" + renderGraph observe (arm.GetProperty "body") loc acc
                acc.Add(line "arm" loc None None (armFields @ [ "body", body ]))
            add Arms None
        | _ when isTypeRef v -> add TypeExpr (Some(typeExpr v))
        | _ when v.ValueKind = JsonValueKind.Array ->
            let items = v.EnumerateArray() |> List.ofSeq
            match items with
            | [] -> add Unknown None
            | first :: _ when isTypeRef first -> add TypeList (Some("[" + (items |> List.map typeExpr |> String.concat " ") + "]"))
            | first :: _ when first.ValueKind = JsonValueKind.String ->
                let domain = arrayDomain[jk]
                add (IdList domain) (Some("[" + (items |> List.map (fun x -> domain + ":" + x.GetString()) |> String.concat " ") + "]"))
            | first :: _ when has first "id" || has first "typeParameterId" ->
                let memberKind = memberKindOf[jk]
                add (Members memberKind) None
                items |> List.iteri (fun i m ->
                    let mkind = if has m "kind" then kebab (m.GetProperty("kind").GetString()) else memberKind
                    acc.Add(renderObject observe mkind (memberSubject memberKind kind m) m (Some subject) (if ordered.Contains jk then Some i else None) acc))
            | first :: _ ->
                let names = first.EnumerateObject() |> Seq.map (fun p -> p.Name) |> List.ofSeq
                let k1, k2 = match names with [ a; b ] -> a, b | _ -> failwith $"unmapped keyed list {jk}"
                let item (p: JsonElement) = domainOf(k1).Value + ":" + p.GetProperty(k1).GetString() + "=" + domainOf(k2).Value + ":" + p.GetProperty(k2).GetString()
                add (Pairs(k1, k2)) (Some("[" + (items |> List.map item |> String.concat " ") + "]"))
        | _ when v.ValueKind = JsonValueKind.True || v.ValueKind = JsonValueKind.False -> add (Scalar Bool) (Some(v.GetRawText()))
        | _ when v.ValueKind = JsonValueKind.Number -> add (Scalar Int) (Some(v.GetRawText()))
        | _ when v.ValueKind = JsonValueKind.String ->
            match domainOf jk with
            | Some domain -> add (Scalar(Id domain)) (Some(domain + ":" + v.GetString()))
            | None -> add (Scalar Str) (Some(v.GetRawText()))
        | _ -> failwith $"unmapped value under {kind}.{jk}"
    observe kind (List.ofSeq seen)
    line kind subject owner position (List.ofSeq fields)

and private line kind subject owner position fields =
    [ yield kind
      yield subject
      match owner with Some o -> yield "in=" + o | None -> ()
      match position with Some p -> yield $"position={p}" | None -> ()
      for k, v in fields -> k + "=" + v ]
    |> String.concat " "

and private renderBinding (observe: Observe) (b: JsonElement) (owner: string) (extra: (string * string) list) =
    let fields = List<string * string>(extra)
    let seen = List<string * Shape>()
    for p in b.EnumerateObject() do
        match p.Name with
        | "id" -> seen.Add(("id", Scalar Str))
        | "displayName" -> seen.Add(("displayName", Scalar Str)); fields.Add(("name", p.Value.GetRawText()))
        | "type" -> seen.Add(("type", TypeExpr)); fields.Add(("type", typeExpr p.Value))
        | other -> failwith $"unmapped binding key {other}"
    observe "binding" (List.ofSeq seen)
    line "binding" ("binding:" + b.GetProperty("id").GetString()) (Some owner) None (List.ofSeq fields)

and private renderGraph (observe: Observe) (g: JsonElement) (loc: string) (acc: List<string>) =
    for n in g.GetProperty("nodes").EnumerateArray() do
        acc.Add(renderObject observe (kebab (n.GetProperty("kind").GetString())) ("node:" + n.GetProperty("id").GetString()) n (Some loc) None acc)
    g.GetProperty("resultNodeId").GetString()

let private positionRegex = Regex(" position=(\\d+)")

/// Declaration members: own ordered members (type parameters, then parameters) by position, then
/// everything else by subject.
let private memberOrder (declaration: string) (lines: List<string>) =
    let key (l: string) =
        let parts = l.Split(' ', 3)
        let kind, subject = parts[0], parts[1]
        let position = positionRegex.Match l
        if position.Success && l.Contains(" in=" + declaration + " ") then (0, (if kind = "type-parameter" then 0 else 1), int position.Groups[1].Value, subject)
        else (1, 0, 0, subject)
    lines |> Seq.sortWith (fun a b ->
        let (a1, a2, a3, a4), (b1, b2, b3, b4) = key a, key b
        match compare (a1, a2, a3) (b1, b2, b3) with 0 -> String.CompareOrdinal(a4, b4) | c -> c) |> List.ofSeq

let private renderWith (observe: Observe) (json: string) : string =
    use doc = JsonDocument.Parse(json, JsonDocumentOptions(MaxDepth = SnapshotCodec.maxJsonDepth))
    let root = doc.RootElement
    let head = List<string>([ "snapshot"; "snapshot:" + root.GetProperty("snapshotId").GetString(); "schema=" + root.GetProperty("schemaVersion").GetRawText() ])
    match root.TryGetProperty "parentSnapshotId" with
    | true, p -> head.Add("parent=snapshot:" + p.GetString())
    | _ -> ()
    match root.TryGetProperty "dependency" with
    | true, d -> head.Add("dependency=" + d.GetProperty("snapshotId").GetRawText()); head.Add("digest=" + d.GetProperty("digest").GetString())
    | _ -> ()
    let out = List<string>([ String.concat " " head ])
    for group in declarationGroups do
        let domain = (dict [ "types", "type"; "effects", "effect"; "capabilities", "capability"; "functions", "function"; "obligations", "obligation" ])[group]
        for d in root.GetProperty(group).EnumerateArray() do
            let acc = List<string>()
            let kind = if has d "kind" then kebab (d.GetProperty("kind").GetString()) else domain
            let subject = domain + ":" + d.GetProperty("id").GetString()
            let first = renderObject observe kind subject d None None acc
            out.Add ""
            out.Add first
            out.AddRange(memberOrder subject acc)
    String.concat "\n" out + "\n"

/// Writer JSON -> record text.
let ofJson (json: string) : string = renderWith (fun _ _ -> ()) json

// ---------------------------------------------------------------------------------------------
// The per-kind table, derived from the writer's output on the structural fixture
// ---------------------------------------------------------------------------------------------

/// `Optional`: keys absent from at least one observed record of the kind (the writer omits an
/// absent option), plus `exported`, which the writer omits when false (SnapshotCodec.fs
/// writeVisibility) and which the fixture only shows absent on some kinds.
type KindTable = { Keys: string list; Shapes: IDictionary<string, Shape>; Optional: Set<string> }

let private deriveTable () : IDictionary<string, KindTable> =
    let keys = Dictionary<string, List<string>>()
    let shapes = Dictionary<string, Dictionary<string, Shape>>()
    let records = Dictionary<string, int>()
    let occurrences = Dictionary<string, Dictionary<string, int>>()
    let observe kind (seen: (string * Shape) list) =
        if not (keys.ContainsKey kind) then
            keys[kind] <- List<string>()
            shapes[kind] <- Dictionary<string, Shape>()
            records[kind] <- 0
            occurrences[kind] <- Dictionary<string, int>()
        records[kind] <- records[kind] + 1
        let order, known = keys[kind], shapes[kind]
        let mutable previous = -1
        for jk, shape in seen do
            if not (order.Contains jk) then order.Insert(previous + 1, jk)
            previous <- order.IndexOf jk
            occurrences[kind][jk] <- (match occurrences[kind].TryGetValue jk with | true, n -> n + 1 | _ -> 1)
            match known.TryGetValue jk with
            | true, existing when existing <> Unknown -> ()
            | _ -> known[jk] <- shape
    renderWith observe (AllKinds.json ()) |> ignore
    for KeyValue(kind, known) in shapes do
        for KeyValue(jk, shape) in known do
            if shape = Unknown then failwith $"table: shape of {kind}.{jk} never observed non-empty"
    keys
    |> Seq.map (fun (KeyValue(kind, order)) ->
        let optional = order |> Seq.filter (fun jk -> jk = "exported" || occurrences[kind][jk] < records[kind]) |> Set.ofSeq
        kind, { Keys = List.ofSeq order; Shapes = shapes[kind]; Optional = optional })
    |> dict

let table = lazy (deriveTable ())

/// The table as text, one kind per line, in the form the production codec embeds
/// (src/Nyrwvy.Semantics/RecordKinds.fs): `record-kind[=jsonKind]: id|kind|typeParameterId|jsonKey/record-key:shape[?][~]`
/// where `?` marks a key the writer sometimes omits and `~` a collection it omits when empty.
let describeTable () =
    let shapeText shape =
        match shape with
        | Scalar(Id d) -> $"id({d})"
        | Scalar Bool -> "bool"
        | Scalar Int -> "int"
        | Scalar Str -> "str"
        | Graph -> "graph"
        | ValueArm b -> $"arm({b})"
        | Arms -> "arms"
        | TypeExpr -> "type"
        | TypeList -> "types"
        | IdList d -> $"ids({d})"
        | Pairs(a, b) -> $"pairs({a}:{(domainOf a).Value},{b}:{(domainOf b).Value})"
        | Members m -> $"members({m})"
        | Unknown -> failwith "unobserved shape"
    table.Value
    |> Seq.sortBy (fun (KeyValue(k, _)) -> k)
    |> Seq.map (fun (KeyValue(kind, t)) ->
        let cell jk =
            match jk with
            | "id" | "kind" | "typeParameterId" -> jk
            | _ -> $"{jk}/{keyName jk}:{shapeText t.Shapes[jk]}" + (if t.Optional.Contains jk then "?" else "") + (if optionalWhenEmpty.Contains jk then "~" else "")
        kind + (if t.Keys |> List.contains "kind" then "=" + camel kind else "") + ": " + (t.Keys |> List.map cell |> String.concat " "))
    |> String.concat "\n"
    |> fun text -> text + "\n"

// ---------------------------------------------------------------------------------------------
// Inverse: record text -> writer JSON
// ---------------------------------------------------------------------------------------------

type private J =
    | Raw of string
    | Obj of (string * J) list
    | Arr of J list

type Record = { Line: int; Kind: string; Subject: string; Fields: (string * string) list; Owner: string option }

/// Key and kind spellings repeat on every line; one shared string per spelling saves an allocation per token.
let private spellings = System.Collections.Concurrent.ConcurrentDictionary<string, string>()
let private spellingLookup = spellings.GetAlternateLookup<ReadOnlySpan<char>>()
let private intern (span: ReadOnlySpan<char>) =
    let mutable found = null
    if spellingLookup.TryGetValue(span, &found) then found
    else let s = span.ToString() in spellings.GetOrAdd(s, s)

/// The JSON string-literal grammar (RFC 8259) for the escapes and control characters between the quotes.
let private validStringBody (text: string) (start: int) (stop: int) =
    let isHex c = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')
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
            | 'u' when i + 5 < stop && isHex text[i + 2] && isHex text[i + 3] && isHex text[i + 4] && isHex text[i + 5] -> i <- i + 6
            | _ -> ok <- false
    ok

/// Splits `kind subject key=value ...`; values are a JSON string literal, a bracket list or a bare token.
let parseLine (line: int) (text: string) : Record =
    let n = text.Length
    let mutable i = 0
    let token () =
        let start = i
        while i < n && text[i] <> ' ' do i <- i + 1
        if i = start then fail line "empty token"
        start
    let space () =
        if i < n then
            if text[i] <> ' ' then fail line "expected a space"
            i <- i + 1
            if i >= n || text[i] = ' ' then fail line "single spaces only, no trailing space"
    let kindStart = token ()
    let kindSpan = text.AsSpan(kindStart, i - kindStart)
    for c in kindSpan do
        if not ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-') then fail line $"bad record kind '{kindSpan.ToString()}'"
    let kind = intern kindSpan
    space ()
    let subjectStart = token ()
    let subject = text.Substring(subjectStart, i - subjectStart)
    let fields = List<string * string>()
    while i < n do
        space ()
        let start = i
        while i < n && text[i] <> '=' && text[i] <> ' ' do i <- i + 1
        if i >= n || text[i] <> '=' then fail line "expected key=value"
        if i = start then fail line "empty key"
        let key = intern (text.AsSpan(start, i - start))
        i <- i + 1
        let vstart = i
        if i < n && text[i] = '"' then
            i <- i + 1
            while i < n && text[i] <> '"' do
                if text[i] = '\\' then i <- i + 1
                i <- i + 1
            if i >= n then fail line "unterminated string"
            i <- i + 1
            if not (validStringBody text (vstart + 1) (i - 1)) then fail line "bad string literal"
        elif i < n && text[i] = '[' then
            while i < n && text[i] <> ']' do i <- i + 1
            if i >= n then fail line "unterminated list"
            i <- i + 1
        else
            while i < n && text[i] <> ' ' do i <- i + 1
            if i = vstart then fail line $"empty value for {key}"
        fields.Add((key, text.Substring(vstart, i - vstart)))
    let mutable owner = None
    for a in 0 .. fields.Count - 1 do
        let key = fst fields[a]
        for b in a + 1 .. fields.Count - 1 do
            if obj.ReferenceEquals(key, fst fields[b]) then fail line "duplicate key"
        if key = "in" then owner <- Some(snd fields[a])
    { Line = line; Kind = kind; Subject = subject; Fields = List.ofSeq fields; Owner = owner }

let private parseId line (expected: string option) (token: string) =
    let m = idRegex.Match token
    if not m.Success then fail line $"expected an ID (domain:uuid), got '{token}'"
    match expected with
    | Some d when d <> m.Groups[1].Value -> fail line $"expected a {d} ID, got '{token}'"
    | _ -> ()
    m.Groups[2].Value

let private quoted (s: string) = Raw("\"" + s + "\"")

let rec private parseType line (s: string) : J =
    if s.StartsWith '(' then
        let mutable depth, i = 0, 0
        let mutable close = -1
        while close < 0 && i < s.Length do
            depth <- depth + (if s[i] = '(' then 1 elif s[i] = ')' then -1 else 0)
            if depth = 0 then close <- i
            i <- i + 1
        if close < 0 || not (s.Substring(close).StartsWith(")->", StringComparison.Ordinal)) then fail line $"bad function type '{s}'"
        Obj [ "kind", quoted "function"; "parameters", Arr(splitArgs line (s.Substring(1, close - 1))); "resultType", parseType line (s.Substring(close + 3)) ]
    else
        let m = Regex.Match(s, "^([a-z0-9-]+(?::[0-9a-f-]+)?)(?:\\((.*)\\))?$")
        if not m.Success then fail line $"bad type expression '{s}'"
        let head, args = m.Groups[1].Value, (if m.Groups[2].Success then Some m.Groups[2].Value else None)
        if head.Contains ':' then
            let domain = head.Substring(0, head.IndexOf ':')
            let uuid = parseId line (Some domain) head
            match domain, args with
            | "type", None -> Obj [ "kind", quoted "named"; "typeId", quoted uuid ]
            | "type", Some a -> Obj [ "kind", quoted "named"; "typeId", quoted uuid; "arguments", Arr(splitArgs line a) ]
            | "type-parameter", None -> Obj [ "kind", quoted "typeParameter"; "typeParameterId", quoted uuid ]
            | "capability", None -> Obj [ "kind", quoted "capability"; "capabilityId", quoted uuid ]
            | _ -> fail line $"bad type expression '{s}'"
        else
            match args with
            | None when primitives.Contains head -> Obj [ "kind", quoted "primitive"; "primitive", quoted head ]
            | Some a when generics.Contains head -> Obj [ "kind", quoted "builtinGeneric"; "generic", quoted (camel head); "arguments", Arr(splitArgs line a) ]
            | _ -> fail line $"bad type expression '{s}'"

and private splitArgs line (s: string) : J list =
    let out = List<string>()
    let mutable depth, start = 0, 0
    for i in 0 .. s.Length - 1 do
        match s[i] with
        | ',' when depth = 0 -> out.Add(s.Substring(start, i - start)); start <- i + 1
        | '(' -> depth <- depth + 1
        | ')' -> depth <- depth - 1
        | _ -> ()
    if s.Length > 0 then out.Add(s.Substring start)
    out |> Seq.map (parseType line) |> List.ofSeq

let private listItems (raw: string) = raw.Substring(1, raw.Length - 2).Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

let private subjectDomain (kind: string) (owner: string option) =
    match kind with
    | "parameter" -> if owner |> Option.exists (fun o -> o.StartsWith("operation:", StringComparison.Ordinal)) then "capability-parameter" else "parameter"
    | "type-parameter" | "field" | "variant" | "operation" | "binding" | "effect" | "capability" | "function" -> kind
    | "product" | "union" -> "type"
    | "missing-argument" -> "obligation"
    | _ -> "node"

/// Record text -> the writer's JSON.
let private convert (strict: bool) (misspellings: bool) (text: string) : Result<string, RecordError> =
    try
        if not (text.EndsWith("\n", StringComparison.Ordinal)) then fail (max 1 (text.Split('\n').Length)) "missing final newline"
        if text.Length > 0 && text[0] = '\uFEFF' then fail 1 "byte order mark"
        if text.Contains "\r" then fail ((text.Substring(0, text.IndexOf '\r').Split('\n').Length)) "carriage return"
        let lines = text.Substring(0, text.Length - 1).Split('\n')
        if lines.Length = 0 || lines[0] = "" then fail 1 "missing header"
        // Header.
        let head = parseLine 1 lines[0]
        if head.Kind <> "snapshot" then fail 1 "header must start with 'snapshot'"
        let headerFields = List<string * J>()
        let snapshotId = parseId 1 (Some "snapshot") head.Subject
        let schema = head.Fields |> List.tryFind (fun (k, _) -> k = "schema") |> Option.map snd |> Option.defaultWith (fun () -> fail 1 "missing schema=")
        if not (Regex.IsMatch(schema, "^[0-9]+$")) then fail 1 "schema= must be an integer"
        headerFields.Add(("schemaVersion", Raw schema))
        headerFields.Add(("snapshotId", quoted snapshotId))
        let dependency = head.Fields |> List.tryFind (fun (k, _) -> k = "dependency") |> Option.map snd
        let digest = head.Fields |> List.tryFind (fun (k, _) -> k = "digest") |> Option.map snd
        match dependency, digest with
        | Some d, Some g when d.StartsWith '"' && Regex.IsMatch(g, "^[0-9a-f]+$") -> headerFields.Add(("dependency", Obj [ "snapshotId", Raw d; "digest", quoted g ]))
        | None, None -> ()
        | _ -> fail 1 "dependency= (quoted) and digest= (lowercase hex) must appear together"
        head.Fields |> List.tryFind (fun (k, _) -> k = "parent") |> Option.iter (fun (_, v) -> headerFields.Add(("parentSnapshotId", quoted (parseId 1 (Some "snapshot") v))))
        for k, _ in head.Fields do
            if not (List.contains k [ "schema"; "parent"; "dependency"; "digest" ]) then fail 1 $"unknown header key {k}"
        // Records.
        let records =
            lines |> Array.mapi (fun i l -> i + 1, l) |> Array.skip 1 |> Array.filter (fun (_, l) -> l <> "")
            |> Array.map (fun (i, l) -> parseLine i l) |> List.ofArray
        let owned = Dictionary<string, List<Record>>()
        for r in records do
            match r.Owner with
            | Some o ->
                if not (owned.ContainsKey o) then owned[o] <- List<Record>()
                owned[o].Add r
            | None -> ()
        let consumed = HashSet<Record>(HashIdentity.Reference)
        let take (owner: string) = match owned.TryGetValue owner with | true, rs -> List.ofSeq rs | _ -> []
        let tbl = table.Value
        // Structural checks before any model is built (strict mode): malformed subjects and owners,
        // duplicates (reported on the second line), members out of canonical order, missing owners and
        // dangling references. A reference one edit away from an existing subject is a misspelling,
        // reported where it is written; otherwise the record is missing and the line where it would sit
        // in canonical order is reported (members by subject inside the declaration's block, ordered
        // members by the first gap in `position=`, a declaration at its block's first line).
        let subjectRegex = Regex($"^[a-z-]+:{uuidPattern}(/[a-z-]+(/variant:{uuidPattern})?)?$")
        let subjects = Dictionary<string, Record>()
        let blocks = List<List<Record>>()
        let blockOf = Dictionary<Record, List<Record>>(HashIdentity.Reference)
        let mutable previousLine = 0
        for r in records do
            if blocks.Count = 0 || r.Line <> previousLine + 1 then blocks.Add(List<Record>())
            blocks[blocks.Count - 1].Add r
            blockOf[r] <- blocks[blocks.Count - 1]
            previousLine <- r.Line
            if strict then
                if not (subjectRegex.IsMatch r.Subject) then fail r.Line $"malformed subject '{r.Subject}'"
                r.Owner |> Option.iter (fun o -> if not (subjectRegex.IsMatch o) then fail r.Line $"malformed owner '{o}'")
                if subjects.ContainsKey r.Subject then fail r.Line $"duplicate record {r.Subject}"
            subjects[r.Subject] <- r
        let positionOf (r: Record) = r.Fields |> List.tryPick (fun (k, v) -> if k = "position" then Some v else None)
        if strict then
            for block in blocks do
                let mutable previous = None
                for r in block |> Seq.skip 1 do
                    if (positionOf r).IsNone then
                        match previous with
                        | Some (p: string) when String.CompareOrdinal(p, r.Subject) > 0 -> fail r.Line $"record {r.Subject} is out of canonical order"
                        | _ -> ()
                        previous <- Some r.Subject
        let declarationDomains = set [ "type"; "effect"; "capability"; "function"; "obligation" ]
        let slot (block: List<Record>) (domain: string) (subject: string) (fallback: int) =
            let same = block |> Seq.filter (fun r -> r.Subject.StartsWith(domain + ":", StringComparison.Ordinal)) |> List.ofSeq
            if domain = "parameter" || domain = "type-parameter" then
                let ordered = same |> List.filter (fun r -> (positionOf r).IsSome)
                match ordered |> List.mapi (fun i r -> i, r) |> List.tryFind (fun (i, r) -> positionOf r <> Some(string i)) with
                | Some(_, r) -> r.Line
                | None when not ordered.IsEmpty -> (List.last ordered).Line + 1
                | None -> block[0].Line + 1
            else
                match same |> List.tryFind (fun r -> String.CompareOrdinal(r.Subject, subject) > 0) with
                | Some r -> r.Line
                | None when not same.IsEmpty -> (List.last same).Line + 1
                | None -> if declarationDomains.Contains domain then block[0].Line else fallback
        let idTokens = Regex("([a-z-]+):(" + uuidPattern + ")")
        let memberDomains = set [ "node"; "binding"; "parameter"; "capability-parameter"; "type-parameter"; "field"; "variant"; "operation"; "type"; "effect"; "capability"; "function"; "obligation" ]
        let editDistanceOne (a: string) (b: string) =
            if a.Length = b.Length then (Seq.zip a b |> Seq.filter (fun (x, y) -> x <> y) |> Seq.length) = 1
            elif abs (a.Length - b.Length) = 1 then
                let longer, shorter = (if a.Length > b.Length then a, b else b, a)
                let mutable i = 0
                while i < shorter.Length && longer[i] = shorter[i] do i <- i + 1
                longer.Substring(i + 1) = shorter.Substring i
            else false
        let misspelled (subject: string) = misspellings && (subjects.Keys |> Seq.exists (editDistanceOne subject))
        let blockOfRecord (subject: string) = match subjects.TryGetValue subject with | true, r -> Some blockOf[r] | _ -> None
        for r in records do
          if strict then
            match r.Owner with
            | Some o ->
                let needed = if o.Contains "/arm/" then o else o.Split('/')[0]
                if not (subjects.ContainsKey needed) then
                    let domain = needed.Substring(0, needed.IndexOf ':')
                    fail (if misspelled needed then r.Line else slot blockOf[r] domain needed r.Line) $"owner {o} not found"
            | None -> ()
            let field name = r.Fields |> List.tryPick (fun (k, v) -> if k = name then Some v else None)
            for k, raw in r.Fields do
                if k <> "in" then
                    for m in idTokens.Matches raw do
                        let domain, subject = m.Groups[1].Value, m.Value
                        if memberDomains.Contains domain && not (subjects.ContainsKey subject) then
                            let line =
                                if misspelled subject then r.Line
                                elif (domain = "node" || domain = "binding" || domain = "type-parameter") && r.Owner.IsSome then slot blockOf[r] domain subject r.Line
                                elif domain = "parameter" then
                                    match field "target-function" |> Option.bind blockOfRecord with
                                    | Some callee -> slot callee domain subject r.Line
                                    | None -> if r.Owner.IsSome then slot blockOf[r] domain subject r.Line else r.Line
                                elif domain = "capability-parameter" then
                                    match field "operation" |> Option.bind blockOfRecord with
                                    | Some callee -> slot callee domain subject r.Line
                                    | None -> r.Line
                                else
                                    // The block whose same-domain subjects bracket the missing one, else this line.
                                    blocks |> Seq.tryPick (fun b ->
                                        let same = b |> Seq.filter (fun x -> x.Subject.StartsWith(domain + ":", StringComparison.Ordinal)) |> List.ofSeq
                                        if same.IsEmpty then None
                                        elif String.CompareOrdinal(same.Head.Subject, subject) < 0 && String.CompareOrdinal((List.last same).Subject, subject) > 0 then Some(slot b domain subject r.Line)
                                        else None)
                                    |> Option.defaultValue r.Line
                            fail line $"{k}= refers to {subject}, which has no record"
        let rec materialize (r: Record) : J =
            consumed.Add r |> ignore
            let t = match tbl.TryGetValue r.Kind with | true, t -> t | _ -> fail r.Line $"unknown record kind '{r.Kind}'"
            let props = Dictionary<string, J>()
            let subjectId = if r.Subject.Contains "/" then fail r.Line "a locator cannot be a record subject here" else parseId r.Line (Some(subjectDomain r.Kind r.Owner)) r.Subject
            if t.Keys |> List.contains "typeParameterId" then props["typeParameterId"] <- quoted subjectId else props["id"] <- quoted subjectId
            if t.Keys |> List.contains "kind" then props["kind"] <- quoted (camel r.Kind)
            for rk, raw in r.Fields do
                match rk with
                | "in" -> ()
                | "position" when collectionOf.ContainsKey r.Kind && ordered.Contains collectionOf[r.Kind] ->
                    if not (Regex.IsMatch(raw, "^[0-9]+$")) then fail r.Line "position= must be an integer"
                | "position" -> fail r.Line "position= is only for parameters and type parameters"
                | _ ->
                    let jk = t.Keys |> List.tryFind (fun jk -> keyName jk = rk && jk <> "id" && jk <> "kind" && jk <> "typeParameterId") |> Option.defaultWith (fun () -> fail r.Line $"unknown key {rk}= on {r.Kind}")
                    props[jk] <- value r t jk rk raw
            // Member records owned by this subject; ordered collections sort by position (stable).
            let members = Dictionary<string, List<int option * J>>()
            for m in take r.Subject do
                match collectionOf.TryGetValue m.Kind with
                | true, collection when t.Shapes.ContainsKey collection ->
                    let position = m.Fields |> List.tryFind (fun (k, _) -> k = "position") |> Option.map snd
                    let position =
                        match position with
                        | Some p when ordered.Contains collection -> if Regex.IsMatch(p, "^[0-9]+$") then Some(int p) else fail m.Line "position= must be an integer"
                        | None when ordered.Contains collection -> fail m.Line "missing position="
                        | _ -> None
                    if not (members.ContainsKey collection) then members[collection] <- List<int option * J>()
                    members[collection].Add((position, materialize m))
                | _ -> fail m.Line $"{m.Kind} record cannot be owned by {r.Kind} {r.Subject}"
            for KeyValue(collection, items) in members do
                props[collection] <- Arr(items |> Seq.sortBy fst |> Seq.map snd |> List.ofSeq)
            // Arms of a match-union.
            if t.Shapes.ContainsKey "arms" then
                let prefix = r.Subject + "/arm/"
                let arms = records |> List.filter (fun a -> a.Kind = "arm" && a.Subject.StartsWith(prefix, StringComparison.Ordinal))
                props["arms"] <- Arr(arms |> List.map (fun a -> arm r a))
            // Absent mandatory collections read as empty; a graph or value arm cannot be absent.
            for jk in t.Keys do
                match t.Shapes[jk] with
                | (Members _ | IdList _ | Pairs _ | Arms | TypeList) when not (props.ContainsKey jk) && not (optionalWhenEmpty.Contains jk) -> props[jk] <- Arr []
                | (Graph | ValueArm _) when not (props.ContainsKey jk) -> fail r.Line $"missing {keyName jk}="
                | _ -> ()
            Obj [ for jk in t.Keys do match props.TryGetValue jk with | true, v -> yield jk, v | _ -> () ]
        and value (r: Record) (t: KindTable) (jk: string) (rk: string) (raw: string) : J =
            match t.Shapes[jk] with
            | Graph -> graph r.Line (r.Subject + "/" + rk) raw (take (r.Subject + "/" + rk))
            | ValueArm bindingKey ->
                let loc = r.Subject + "/" + rk
                let bindings = take loc |> List.filter (fun m -> m.Kind = "binding")
                match bindings with
                | [ b ] -> Obj [ bindingKey, binding b false; "body", graph r.Line loc raw (take loc |> List.filter (fun m -> m.Kind <> "binding")) ]
                | _ -> fail r.Line $"{rk}= needs exactly one binding record in {loc}"
            | TypeExpr -> parseType r.Line raw
            | TypeList -> if raw.StartsWith '[' then Arr(listItems raw |> List.map (parseType r.Line)) else fail r.Line $"{rk}= must be a list"
            | IdList domain -> if raw.StartsWith '[' then Arr(listItems raw |> List.map (parseId r.Line (Some domain) >> quoted)) else fail r.Line $"{rk}= must be a list"
            | Pairs(k1, k2) ->
                if not (raw.StartsWith '[') then fail r.Line $"{rk}= must be a list"
                Arr(listItems raw |> List.map (fun item ->
                    match item.Split('=') with
                    | [| a; b |] -> Obj [ k1, quoted (parseId r.Line (domainOf k1) a); k2, quoted (parseId r.Line (domainOf k2) b) ]
                    | _ -> fail r.Line $"bad pair '{item}' in {rk}="))
            | Arms | Members _ | Unknown -> fail r.Line $"{rk}= cannot be given inline"
            | Scalar _ ->
                match domainOf jk with
                | Some domain -> quoted (parseId r.Line (Some domain) raw)
                | None ->
                    if raw.StartsWith '"' || raw = "true" || raw = "false" || Regex.IsMatch(raw, "^-?[0-9]+$") then Raw raw
                    else fail r.Line $"bad value for {rk}="
        and graph line (loc: string) (raw: string) (members: Record list) : J =
            let result = parseId line (Some "node") raw
            let nodes = members |> List.map (fun m -> if m.Kind = "binding" || m.Kind = "arm" || collectionOf.ContainsKey m.Kind then fail m.Line $"{m.Kind} record cannot be owned by graph {loc}" else materialize m)
            Obj [ "nodes", Arr nodes; "resultNodeId", quoted result ]
        and binding (b: Record) (withSourceField: bool) : J =
            consumed.Add b |> ignore
            let mutable name, typ, source = None, None, None
            for k, raw in b.Fields do
                match k with
                | "in" -> ()
                | "name" when raw.StartsWith '"' -> name <- Some raw
                | "type" -> typ <- Some(parseType b.Line raw)
                | "source-field" when withSourceField -> source <- Some(parseId b.Line (Some "field") raw)
                | _ -> fail b.Line $"unknown key {k}= on binding"
            let typ = match typ with Some t -> t | None -> fail b.Line "missing type="
            let inner =
                Obj
                    [ yield "id", quoted (parseId b.Line (Some "binding") b.Subject)
                      match name with
                      | Some n -> yield "displayName", Raw n
                      | None -> ()
                      yield "type", typ ]
            if withSourceField then Obj [ "sourceFieldId", quoted (match source with Some s -> s | None -> fail b.Line "missing source-field="); "binding", inner ] else inner
        and arm (m: Record) (a: Record) : J =
            consumed.Add a |> ignore
            let key = a.Subject.Substring(m.Subject.Length + 5)
            let first = parseId a.Line (Some "variant") key
            let variants = a.Fields |> List.tryFind (fun (k, _) -> k = "variants") |> Option.map (snd >> fun raw -> if raw.StartsWith '[' then listItems raw |> List.map (parseId a.Line (Some "variant")) else fail a.Line "variants= must be a list")
            let body = a.Fields |> List.tryFind (fun (k, _) -> k = "body") |> Option.map snd |> Option.defaultWith (fun () -> fail a.Line "missing body=")
            for k, _ in a.Fields do if k <> "variants" && k <> "body" then fail a.Line $"unknown key {k}= on arm"
            let variantPart =
                match variants with
                | None -> [ "variantId", quoted first ]
                | Some(v :: _ as vs) when v = first && vs.Length > 1 -> [ "variantIds", Arr(vs |> List.map quoted) ]
                | _ -> fail a.Line "variants= must list two or more variants starting with the locator's key"
            let members = take a.Subject
            let fieldBindings =
                members |> List.filter (fun b -> b.Kind = "binding") |> List.map (fun b -> binding b true)
                |> List.sortBy (fun j -> match j with Obj(("sourceFieldId", Raw s) :: _) -> s | _ -> "")
            let nodes = members |> List.filter (fun b -> b.Kind <> "binding") |> List.map (fun n -> if n.Kind = "arm" || collectionOf.ContainsKey n.Kind then fail n.Line $"{n.Kind} record cannot be owned by arm {a.Subject}" else materialize n)
            Obj(variantPart @ [ "fieldBindings", Arr fieldBindings; "body", Obj [ "nodes", Arr nodes; "resultNodeId", quoted (parseId a.Line (Some "node") body) ] ])
        // Declarations.
        let groups = Dictionary<string, List<J>>()
        for g in declarationGroups do groups[g] <- List<J>()
        for r in records do
            if r.Owner.IsNone && r.Kind <> "arm" then
                match groupOfKind.TryGetValue r.Kind with
                | true, g -> groups[g].Add(materialize r)
                | _ -> fail r.Line $"'{r.Kind}' is not a declaration kind"
        for r in records do
            if not (consumed.Contains r) then fail r.Line (match r.Owner with Some o -> $"owner {o} not found" | None -> "orphan record")
        let root = Obj(List.ofSeq headerFields @ [ for g in declarationGroups -> g, Arr(List.ofSeq groups[g]) ])
        // Emit.
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(MaxDepth = SnapshotCodec.maxJsonDepth))
        let rec write (j: J) =
            match j with
            | Raw s -> writer.WriteRawValue(s, true)
            | Arr items -> writer.WriteStartArray(); List.iter write items; writer.WriteEndArray()
            | Obj props -> writer.WriteStartObject(); (for k, v in props do writer.WritePropertyName k; write v); writer.WriteEndObject()
        write root
        writer.Flush()
        Ok(Encoding.UTF8.GetString(stream.ToArray()))
    with RecordException e -> Error e

/// Record text -> the writer's JSON, structure only: no reference or owner existence checks, so the
/// structural tier (which does not validate) converts.
let toJson (text: string) : Result<string, RecordError> = convert false true text

/// Record text -> the writer's JSON with the structural checks (duplicates, dangling references,
/// missing owners) reported on the line where the record is or should be. The model-level decoder uses it.
let toJsonStrict (text: string) : Result<string, RecordError> = convert true true text

/// `toJsonStrict` without the one-edit misspelling rule: a missing ID is always reported at its canonical position (ablation).
let toJsonStrictNoMisspellings (text: string) : Result<string, RecordError> = convert true false text

/// The row table: every non-blank line tokenized into a record (header included), no model.
let parseRows (text: string) : Result<Record[], RecordError> =
    try
        let rows = List<Record>()
        let mutable start, line = 0, 1
        while start <= text.Length do
            let stop = match text.IndexOf('\n', start) with -1 -> text.Length | e -> e
            if stop > start then rows.Add(parseLine line (text.Substring(start, stop - start)))
            start <- stop + 1
            line <- line + 1
        Ok(rows.ToArray())
    with RecordException e -> Error e
