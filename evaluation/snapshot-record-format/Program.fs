module Nyrwvy.SnapshotRecordFormat.Program

open System
open System.IO
open System.Text
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.SnapshotRecordFormat

let private write (path: string) (text: string) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text, UTF8Encoding(false))

/// Node kinds (camelCase, as the writer spells them) used anywhere in a snapshot JSON document.
let private nodeKinds (json: string) =
    use doc = JsonDocument.Parse(json, JsonDocumentOptions(MaxDepth = SnapshotCodec.maxJsonDepth))
    let rec walk (e: JsonElement) =
        seq {
            match e.ValueKind with
            | JsonValueKind.Object ->
                match e.TryGetProperty "id", e.TryGetProperty "kind" with
                | (true, _), (true, k) -> yield k.GetString()
                | _ -> ()
                for p in e.EnumerateObject() do yield! walk p.Value
            | JsonValueKind.Array -> for item in e.EnumerateArray() do yield! walk item
            | _ -> ()
        }
    walk doc.RootElement |> Set.ofSeq |> Set.remove "product" |> Set.remove "union" |> Set.remove "missingArgument"

let private structuralPath (fixtures: string) = Path.Combine(fixtures, "structural", "all-kinds.json")

/// Returns (fixture name, passed, columns).
let private checkValidated (path: string) =
    let name = Path.GetFileName path
    let json = File.ReadAllText path
    match SnapshotCodec.deserialize json with
    | Error error -> name, false, [ $"json decode failed: {error}" ]
    | Ok m1 ->
        match Codec.encode m1 with
        | Error error -> name, false, [ $"encode failed: {error}" ]
        | Ok r1 ->
            write (Path.ChangeExtension(path, ".records.txt")) r1
            match Codec.decode r1 with
            | Error error -> name, false, [ $"decode failed at line {error.Line}: {error.Message}" ]
            | Ok m2 ->
                let r2 = Codec.encode m2 |> Result.defaultValue ""
                let fp (f: Snapshot -> Result<'a, _>) = match f m1, f m2 with | Ok a, Ok b -> a = b | _ -> false
                let modelsEqual, recordsEqual = (m1 = m2), (r1 = r2)
                let snapshotFp, semanticFp = fp SnapshotFingerprint.compute, fp SemanticFingerprint.compute
                let lines = r1.Split('\n').Length - 1
                let bytes = Encoding.UTF8.GetByteCount r1
                let cell b = if b then "yes" else "NO"
                name, (modelsEqual && recordsEqual && snapshotFp && semanticFp),
                [ string lines; string bytes; cell modelsEqual; cell recordsEqual; cell snapshotFp; cell semanticFp ]

let private checkStructural (path: string) =
    let name = "structural/" + Path.GetFileName path
    let json = File.ReadAllText path
    let fresh = AllKinds.json () = json
    let r1 = RecordText.ofJson json
    write (Path.ChangeExtension(path, ".records.txt")) r1
    match RecordText.toJson r1 with
    | Error error -> name, false, [ $"record->json failed at line {error.Line}: {error.Message}" ]
    | Ok json2 ->
        let jsonEqual = json2 = json
        let r2 = RecordText.ofJson json2
        let recordsEqual = r1 = r2
        let cell b = if b then "yes" else "NO"
        let lines = r1.Split('\n').Length - 1
        name, (fresh && jsonEqual && recordsEqual),
        [ string lines; string (Encoding.UTF8.GetByteCount r1); "-"; cell recordsEqual; "-"; "-"; (if fresh then "" else "generator output differs from the committed fixture: run generate"); (if jsonEqual then "" else $"json differs at byte {Seq.zip json json2 |> Seq.tryFindIndex (fun (a, b) -> a <> b) |> Option.defaultValue (min json.Length json2.Length)}") ]

let private roundtrip (fixtures: string) =
    let validated = Directory.GetFiles(Path.Combine(fixtures, "validated"), "*.json") |> Array.sort
    let rows = (validated |> Array.map checkValidated |> List.ofArray) @ [ checkStructural (structuralPath fixtures) ]
    printfn "%-48s %6s %7s %6s %7s %6s %6s" "fixture" "lines" "bytes" "model" "records" "fp" "semfp"
    for name, _, cells in rows do
        printfn "%-48s %s" name (cells |> List.filter (fun c -> c <> "") |> List.map (sprintf "%6s") |> String.concat " ")
    let all = AllKinds.json () |> nodeKinds
    let covered = validated |> Array.map (File.ReadAllText >> nodeKinds) |> Set.unionMany
    let uncovered = Set.difference all covered
    printfn "\nvalidated-tier node kinds: %d of %d (%d SemanticNodeKind cases)" covered.Count all.Count AllKinds.nodeKindCount
    printfn "uncovered: %s" (uncovered |> String.concat " ")
    let failed = rows |> List.filter (fun (_, ok, _) -> not ok)
    printfn "\n%d fixtures, %d failed" rows.Length failed.Length
    if failed.IsEmpty then 0 else 1

[<EntryPoint>]
let main argv =
    let fixtures = if argv.Length > 1 then argv[1] else "fixtures"
    match List.ofArray argv with
    | "generate" :: _ ->
        write (structuralPath fixtures) (AllKinds.json ())
        printfn "wrote %s" (structuralPath fixtures)
        0
    | "roundtrip" :: _ -> roundtrip fixtures
    | "schema" :: _ ->
        printfn "%s" (RecordText.describeTable ())
        0
    | "encode" :: path :: _ ->
        match SnapshotCodec.deserialize (File.ReadAllText path) |> Result.bind Codec.encode with
        | Ok text -> Console.Out.Write text; 0
        | Error error -> eprintfn "%A" error; 1
    | "decode" :: path :: _ ->
        match Codec.decode (File.ReadAllText path) |> Result.bind (Codec.encode >> Result.mapError (fun e -> { RecordText.Line = 1; RecordText.Message = string e })) with
        | Ok text -> Console.Out.Write text; 0
        | Error error -> eprintfn "line %d: %s" error.Line error.Message; 1
    | _ ->
        eprintfn "usage: generate [fixtures] | roundtrip [fixtures] | schema | encode <snapshot.json> | decode <snapshot.records.txt>"
        2
