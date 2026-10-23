/// Phase 2 mechanical measurements: corruption (seeded mutations) and load (100x synthetic).
/// Diff locality is driven from locality.mjs through the authoring session.
module Nyrwvy.SnapshotRecordFormat.Phase2

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Nyrwvy.Core
open Nyrwvy.SnapshotRecordFormat

// ---------------------------------------------------------------------------------------------
// Corruption
// ---------------------------------------------------------------------------------------------

let private closedEnumerations = set [ "kind"; "status"; "primitive"; "generic"; "schemaVersion" ]

/// Differing leaves between two writer JSON documents as (key, before, after); a structural
/// difference (array length, property set) is one entry with "<structure>" values.
let private leafDifferences (a: string) (b: string) =
    use da = JsonDocument.Parse a
    use db = JsonDocument.Parse b
    let diffs = Collections.Generic.List<string * string * string>()
    let rec walk (key: string) (x: JsonElement) (y: JsonElement) =
        if x.ValueKind <> y.ValueKind then diffs.Add((key, "<structure>", "<structure>"))
        else
            match x.ValueKind with
            | JsonValueKind.Object ->
                let px, py = x.EnumerateObject() |> List.ofSeq, y.EnumerateObject() |> List.ofSeq
                if (px |> List.map (fun p -> p.Name)) <> (py |> List.map (fun p -> p.Name)) then diffs.Add((key, "<structure>", "<structure>"))
                else List.iter2 (fun (p: JsonProperty) (q: JsonProperty) -> walk p.Name p.Value q.Value) px py
            | JsonValueKind.Array ->
                let ix, iy = x.EnumerateArray() |> List.ofSeq, y.EnumerateArray() |> List.ofSeq
                if ix.Length <> iy.Length then diffs.Add((key, "<structure>", "<structure>"))
                else List.iter2 (walk key) ix iy
            | _ -> if x.GetRawText() <> y.GetRawText() then diffs.Add((key, x.GetRawText(), y.GetRawText()))
    walk "" da.RootElement db.RootElement
    List.ofSeq diffs

/// The plan's free-value classifier: exactly one leaf differs, a string or number that is neither
/// an ID nor a closed enumeration.
let private isFreeValueEdit (original: string) (mutated: string) =
    match leafDifferences original mutated with
    | [ key, before, after ] ->
        let scalar (v: string) = v.StartsWith '"' || Regex.IsMatch(v, "^-?[0-9]")
        scalar before && scalar after && key <> "id" && not (RecordText.isIdKey key) && not (closedEnumerations.Contains key)
    | _ -> false

type Mutation = { Index: int; Fixture: string; Kind: string; MutatedLines: int list; Before: string; After: string }

let private alphabet = [| yield '\n'; for c in 0x20 .. 0x7E -> char c |]

let private lineOf (text: string) (pos: int) = 1 + (text.Substring(0, pos) |> Seq.filter ((=) '\n') |> Seq.length)
let private lineText (text: string) (line: int) = let ls = text.Split('\n') in if line >= 1 && line <= ls.Length then ls[line - 1] else ""

/// 1,000 mutations: 500 single-character (replace, insert, delete), 500 single-line (delete,
/// duplicate, swap with the next), round-robin over the fixtures, from one seed.
let private mutate (rng: Random) (index: int) (fixture: string) (text: string) : Mutation * string =
    let kind = index % 3
    if index < 500 then
        let len = text.Length
        match kind with
        | 0 ->
            let pos = rng.Next len
            let mutable c = alphabet[rng.Next alphabet.Length]
            while c = text[pos] do c <- alphabet[rng.Next alphabet.Length]
            let line = lineOf text pos
            let after = text.Substring(0, pos) + string c + text.Substring(pos + 1)
            { Index = index; Fixture = fixture; Kind = "char-replace"; MutatedLines = [ line ]; Before = lineText text line; After = lineText after line }, after
        | 1 ->
            let pos = rng.Next(len + 1)
            let c = alphabet[rng.Next alphabet.Length]
            let line = lineOf text pos
            let after = text.Substring(0, pos) + string c + text.Substring pos
            { Index = index; Fixture = fixture; Kind = "char-insert"; MutatedLines = [ line ]; Before = lineText text line; After = lineText after line }, after
        | _ ->
            let pos = rng.Next len
            let line = lineOf text pos
            let after = text.Substring(0, pos) + text.Substring(pos + 1)
            { Index = index; Fixture = fixture; Kind = "char-delete"; MutatedLines = [ line ]; Before = lineText text line; After = lineText after line }, after
    else
        let lines = text.Substring(0, text.Length - 1).Split('\n')
        let n = lines.Length
        let join (ls: string seq) = String.Join("\n", ls) + "\n"
        match kind with
        | 0 ->
            let i = rng.Next n
            let after = join (lines |> Array.removeAt i)
            { Index = index; Fixture = fixture; Kind = "line-delete"; MutatedLines = [ i + 1 ]; Before = lines[i]; After = lineText after (i + 1) }, after
        | 1 ->
            let i = rng.Next n
            let after = join (lines |> Array.insertAt i lines[i])
            { Index = index; Fixture = fixture; Kind = "line-duplicate"; MutatedLines = [ i + 1; i + 2 ]; Before = lines[i]; After = lines[i] + "\n" + lines[i] }, after
        | _ ->
            let i = rng.Next(n - 1)
            let swapped = Array.copy lines
            swapped[i] <- lines[i + 1]
            swapped[i + 1] <- lines[i]
            { Index = index; Fixture = fixture; Kind = "line-swap"; MutatedLines = [ i + 1; i + 2 ]; Before = lines[i] + "\n" + lines[i + 1]; After = lines[i + 1] + "\n" + lines[i] }, join swapped

type Outcome =
    { Mutation: Mutation
      Outcome: string // rejected | accepted-same | accepted-free-value | accepted-changed
      ReportedLine: int
      LineOk: bool
      Message: string }

let corrupt (fixtures: string) (seed: int) (misspellings: bool) (resultsDir: string) =
    let rng = Random seed
    let files = Directory.GetFiles(Path.Combine(fixtures, "validated"), "*.json") |> Array.sort
    let originals =
        files |> Array.map (fun path ->
            let json = File.ReadAllText path
            let snapshot = SnapshotCodec.deserialize json |> function Ok s -> s | Error e -> failwith $"{path}: {e}"
            let text = Codec.encode snapshot |> function Ok t -> t | Error e -> failwith $"{path}: {e}"
            Path.GetFileName path, text, (SnapshotCodec.serialize snapshot |> Result.defaultValue ""), (SemanticFingerprint.compute snapshot |> Result.defaultValue Unchecked.defaultof<_>))
    // Slots 0..999 fix the kind schedule; a draw that leaves the bytes unchanged (a no-op mutation) is
    // discarded and the slot redrawn from the same seed stream, so every counted mutation is real.
    let mutable noOps = 0
    let outcomes =
        [ for slot in 0 .. 999 do
            let name, text, json, fingerprint = originals[slot % originals.Length]
            let mutable drawn = mutate rng slot name text
            while snd drawn = text do
                noOps <- noOps + 1
                drawn <- mutate rng slot name text
            let mutation, mutated = drawn
            match Codec.decodeWith misspellings mutated with
            | Error e ->
                yield { Mutation = mutation; Outcome = "rejected"; ReportedLine = e.Line; LineOk = List.contains e.Line mutation.MutatedLines; Message = e.Message.Split('\n')[0] }
            | Ok decoded ->
                let same = (SemanticFingerprint.compute decoded |> Result.defaultValue Unchecked.defaultof<_>) = fingerprint
                let outcome =
                    if isFreeValueEdit json (SnapshotCodec.serialize decoded |> Result.defaultValue "") then "accepted-free-value"
                    elif same then "accepted-same"
                    else "accepted-changed"
                yield { Mutation = mutation; Outcome = outcome; ReportedLine = 0; LineOk = true; Message = "" } ]
    let rejected = outcomes |> List.filter (fun o -> o.Outcome = "rejected")
    let lineOk = rejected |> List.filter (fun o -> o.LineOk)
    let changed = outcomes |> List.filter (fun o -> o.Outcome = "accepted-changed")
    let count outcome = outcomes |> List.filter (fun o -> o.Outcome = outcome) |> List.length
    let ratio = float lineOk.Length / float rejected.Length
    printfn "corruption: seed=%d misspelling-rule=%b no-op-draws-discarded=%d mutations=%d rejected=%d line-ok=%d (%.1f%%) accepted-same=%d accepted-free-value=%d accepted-changed=%d" seed misspellings noOps outcomes.Length rejected.Length lineOk.Length (100.0 * ratio) (count "accepted-same") (count "accepted-free-value") changed.Length
    printfn "%-16s %8s %8s %8s" "kind" "total" "rejected" "line-ok"
    for kind in outcomes |> List.map (fun o -> o.Mutation.Kind) |> List.distinct do
        let ofKind = outcomes |> List.filter (fun o -> o.Mutation.Kind = kind)
        printfn "%-16s %8d %8d %8d" kind ofKind.Length (ofKind |> List.filter (fun o -> o.Outcome = "rejected") |> List.length) (ofKind |> List.filter (fun o -> o.Outcome = "rejected" && o.LineOk) |> List.length)
    let semanticPass, linePass = changed.IsEmpty, ratio >= 0.95
    printfn "criterion semantics (no accepted-changed): %s; criterion line (>=95%% of rejected on the mutated line): %s" (if semanticPass then "PASS" else "FAIL") (if linePass then "PASS" else "FAIL")
    Directory.CreateDirectory resultsDir |> ignore
    let breaking = outcomes |> List.filter (fun o -> o.Outcome = "accepted-changed" || (o.Outcome = "rejected" && not o.LineOk))
    let doc =
        {| seed = seed
           misspellingRule = misspellings
           noOpDrawsDiscarded = noOps
           summary = {| mutations = outcomes.Length; rejected = rejected.Length; rejectedOnMutatedLine = lineOk.Length; rejectedOnMutatedLineRatio = ratio; acceptedSame = count "accepted-same"; acceptedFreeValue = count "accepted-free-value"; acceptedChanged = changed.Length; semanticsPass = semanticPass; linePass = linePass |}
           breaking = breaking |> List.map (fun o -> {| index = o.Mutation.Index; fixture = o.Mutation.Fixture; kind = o.Mutation.Kind; mutatedLines = o.Mutation.MutatedLines; outcome = o.Outcome; reportedLine = o.ReportedLine; message = o.Message; before = o.Mutation.Before; after = o.Mutation.After |})
           all = outcomes |> List.map (fun o -> {| index = o.Mutation.Index; fixture = o.Mutation.Fixture; kind = o.Mutation.Kind; mutatedLines = o.Mutation.MutatedLines; outcome = o.Outcome; reportedLine = o.ReportedLine; before = o.Mutation.Before; after = o.Mutation.After |}) |}
    let file = Path.Combine(resultsDir, (if misspellings then "corruption.json" else "corruption-no-misspelling-rule.json"))
    File.WriteAllText(file, JsonSerializer.Serialize(doc, JsonSerializerOptions(WriteIndented = true)))
    printfn "wrote %s (%d criterion-breaking mutations)" file breaking.Length
    if semanticPass && linePass then 0 else 1

// ---------------------------------------------------------------------------------------------
// Load
// ---------------------------------------------------------------------------------------------

let private uuidRegex = Regex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")

/// The planner with its functions copied `copies` times under fresh IDs (every ID that occurs only
/// inside the functions array: function, parameter, node and binding IDs, including the calls
/// between them), written by the codec so the result is canonical.
let synthetic (plannerJson: string) (copies: int) =
    use doc = JsonDocument.Parse plannerJson
    let root = doc.RootElement
    let functionsRaw = root.GetProperty("functions").GetRawText()
    let elsewhere =
        [ "types"; "effects"; "capabilities"; "obligations" ] |> List.map (fun g -> root.GetProperty(g).GetRawText()) |> String.concat ""
        |> uuidRegex.Matches |> Seq.map (fun m -> m.Value) |> Set.ofSeq
        |> Set.add (root.GetProperty("snapshotId").GetString())
    let inner = functionsRaw.Substring(1, functionsRaw.Length - 2)
    let copy k = uuidRegex.Replace(inner, fun m -> if elsewhere.Contains m.Value then m.Value else AllKinds.uuid $"load/{k}/{m.Value}")
    let json = plannerJson.Replace(functionsRaw, "[" + String.Join(",", [ for k in 1 .. copies -> copy k ]) + "]")
    match SnapshotCodec.deserialize json |> Result.bind SnapshotCodec.serialize with
    | Ok normalized -> normalized
    | Error e -> failwith $"synthetic snapshot does not validate: {e}"

let private median (samples: float list) = let s = List.sort samples in s[s.Length / 2]

/// Median of `runs` timings (ms) after `warmup` runs.
let private time (warmup: int) (runs: int) (f: unit -> unit) =
    for _ in 1 .. warmup do f ()
    [ for _ in 1 .. runs ->
        let sw = Stopwatch.StartNew()
        f ()
        sw.Elapsed.TotalMilliseconds ] |> median

let load (plannerPath: string) (copies: int) (resultsDir: string) =
    let json = synthetic (File.ReadAllText plannerPath) copies
    let text = RecordText.ofJson json
    let rows = RecordText.parseRows text |> function Ok r -> r.Length | Error e -> failwith e.Message
    printfn "synthetic: %d copies, json %d bytes, record %d bytes, %d rows" copies json.Length (Encoding.UTF8.GetByteCount text) rows
    let ok r = match r with Ok _ -> () | Error e -> failwith $"{e}"
    let rowTable = time 3 20 (fun () -> RecordText.parseRows text |> ok)
    let jsonDeserialize = time 3 20 (fun () -> SnapshotCodec.deserialize json |> ok)
    let fresh () = match SnapshotCodec.deserialize json with Ok s -> { s with Id = s.Id } | Error e -> failwith $"{e}"
    let validateOnly = [ for _ in 1 .. 23 -> let s = fresh () in let sw = Stopwatch.StartNew() in SnapshotValidator.validate s |> ignore; sw.Elapsed.TotalMilliseconds ] |> List.skip 3 |> median
    let jsonParseOnly = time 3 20 (fun () -> JsonDocument.Parse(json, JsonDocumentOptions(MaxDepth = SnapshotCodec.maxJsonDepth)).Dispose())
    let recordToJson = time 3 20 (fun () -> RecordText.toJson text |> ok)
    let recordDecode = time 3 20 (fun () -> Codec.decode text |> ok)
    let jsonTreeWalk = jsonDeserialize - validateOnly
    let ratio = rowTable / jsonTreeWalk
    printfn "record row table (tokenize every line, no model):          %8.2f ms" rowTable
    printfn "JSON deserialize (JsonDocument + tree walk + validate):    %8.2f ms" jsonDeserialize
    printfn "  validate alone (fresh model):                            %8.2f ms" validateOnly
    printfn "  JSON tree walk without validation (difference):          %8.2f ms" jsonTreeWalk
    printfn "  JsonDocument.Parse alone:                                %8.2f ms" jsonParseOnly
    printfn "record -> JSON text (structure, no model, no validation):  %8.2f ms" recordToJson
    printfn "record full decode (toJson + deserialize + canonical check): %6.2f ms" recordDecode
    printfn "ratio row table / JSON tree walk = %.2f (criterion <= 1.5): %s" ratio (if ratio <= 1.5 then "PASS" else "FAIL")
    Directory.CreateDirectory resultsDir |> ignore
    let doc = {| copies = copies; jsonBytes = json.Length; recordBytes = Encoding.UTF8.GetByteCount text; rows = rows; medianOf = 20; warmup = 3; ms = {| recordRowTable = rowTable; jsonDeserializeWithValidation = jsonDeserialize; validateOnly = validateOnly; jsonTreeWalkWithoutValidation = jsonTreeWalk; jsonDocumentParseOnly = jsonParseOnly; recordToJsonText = recordToJson; recordFullDecode = recordDecode |}; ratioRowTableToJsonTreeWalk = ratio; pass = ratio <= 1.5 |}
    File.WriteAllText(Path.Combine(resultsDir, "load.json"), JsonSerializer.Serialize(doc, JsonSerializerOptions(WriteIndented = true)))
    if ratio <= 1.5 then 0 else 1
