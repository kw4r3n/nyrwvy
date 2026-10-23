/// Model-level codec: `encode` goes through `SnapshotCodec.serialize` (which validates) and the
/// record renderer; `decode` parses to JSON, validates through `SnapshotCodec.deserialize`, then
/// re-encodes and accepts only canonical bytes.
module Nyrwvy.SnapshotRecordFormat.Codec

open System
open System.Text.RegularExpressions
open Nyrwvy.Core
open Nyrwvy.SnapshotRecordFormat.RecordText

let encode (snapshot: Snapshot) : Result<string, SnapshotCodecError> =
    SnapshotCodec.serialize snapshot |> Result.map ofJson

/// A validation or shape error has no line of its own; it is reported on the first record (in file
/// order) whose subject UUID appears earliest in the error text, else on line 1.
let private lineOfError (text: string) (error: SnapshotCodecError) =
    let message = sprintf "%A" error
    let uuid = Regex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")
    text.Split('\n')
    |> Array.mapi (fun i l -> i + 1, l.Split(' '))
    |> Array.choose (fun (line, parts) ->
        let m = if parts.Length < 2 then Match.Empty else uuid.Match parts[1]
        let at = if m.Success then message.IndexOf m.Value else -1
        if at >= 0 then Some(at, line) else None)
    |> Array.sortBy fst
    |> Array.tryHead
    |> Option.map snd
    |> Option.defaultValue 1
    |> fun line -> { Line = line; Message = message }

/// The first 1-based line where two texts differ (comparing lines, then the line count).
let firstDifferingLine (a: string) (b: string) =
    let la, lb = a.Split('\n'), b.Split('\n')
    Seq.zip la lb |> Seq.tryFindIndex (fun (x, y) -> x <> y) |> Option.defaultValue (min la.Length lb.Length) |> (+) 1

let decode (text: string) : Result<Snapshot, RecordError> =
    toJsonStrict text
    |> Result.bind (fun json ->
        match SnapshotCodec.deserialize json with
        | Error error -> Error(lineOfError text error)
        | Ok snapshot ->
            match encode snapshot with
            | Error error -> Error { Line = 1; Message = $"re-encode failed: {error}" }
            | Ok again when again <> text -> Error { Line = firstDifferingLine text again; Message = "not canonical: re-encoding differs" }
            | Ok _ -> Ok snapshot)
