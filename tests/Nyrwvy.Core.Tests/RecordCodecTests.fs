namespace Nyrwvy.Core.Tests

open System
open System.IO
open Nyrwvy.Core
open Xunit

/// The record form (docs/decisions/snapshot-record-format.md) against the JSON codec: fixtures under
/// tests/Nyrwvy.Core.Tests/fixtures/record-format are copies of the evaluation corpus.
module RecordCodecTests =
    let private unwrap = function
        | Ok value -> value
        | Error error -> failwithf "Unexpected codec error: %A" error

    let private repoRoot () =
        let rec find (directory: DirectoryInfo) =
            if isNull directory then failwith "Could not locate repository root."
            elif File.Exists(Path.Combine(directory.FullName, "Nyrwvy.slnx")) then directory.FullName
            else find directory.Parent
        find (DirectoryInfo(AppContext.BaseDirectory))

    let private fixture (name: string) =
        File.ReadAllText(Path.Combine(repoRoot (), "tests", "Nyrwvy.Core.Tests", "fixtures", "record-format", name + ".json"))
        |> SnapshotCodec.deserialize
        |> unwrap

    [<Theory>]
    [<InlineData("verification-planner")>]
    [<InlineData("mini-work-planner")>]
    [<InlineData("function-abort-workspace")>]
    let ``record text round-trips the JSON-decoded model with its fingerprints`` (name: string) =
        let original = fixture name
        let text = RecordCodec.serialize original |> unwrap
        let restored = RecordCodec.deserialize text |> unwrap
        Assert.Equal(original, restored)
        Assert.Equal(text, RecordCodec.serialize restored |> unwrap)
        Assert.Equal(SnapshotFingerprint.compute original |> unwrap, SnapshotFingerprint.compute restored |> unwrap)
        Assert.Equal(SemanticFingerprint.compute original |> unwrap, SemanticFingerprint.compute restored |> unwrap)
        Assert.EndsWith("\n", text)
        Assert.StartsWith("snapshot snapshot:", text)

    [<Fact>]
    let ``a resolved snapshot carries the dependency marker in its header and round-trips`` () =
        let local = fixture "verification-planner"
        let dependency = Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-dependency")
        let text = RecordCodec.serializeResolved dependency local |> unwrap
        let header = text.Substring(0, text.IndexOf '\n')
        Assert.Contains(" dependency=\"snapshot-dependency\" digest=", header)
        Assert.Matches("digest=[0-9a-f]{64}$", header)
        let restored = RecordCodec.deserializeResolved dependency text |> unwrap
        Assert.Equal(local, restored)
        Assert.Equal(text, RecordCodec.serializeResolved dependency restored |> unwrap)
        // Without the dependency the marker is rejected, as the JSON codec rejects it.
        match RecordCodec.deserialize text with
        | Error error -> Assert.Equal(1, error.Line)
        | Ok _ -> failwith "A resolved snapshot decoded without its dependency."

    /// `edit` rewrites the first line containing `needle`; the expected position is (line, column).
    let private rejected (name: string) (needle: string) (edit: string -> string) =
        let text = RecordCodec.serialize (fixture name) |> unwrap
        let lines = text.Split '\n'
        let at = lines |> Array.findIndex (fun l -> l.Contains needle)
        lines[at] <- edit lines[at]
        let mutated = String.Join("\n", lines)
        match RecordCodec.deserialize mutated with
        | Ok _ -> failwithf "Non-canonical text was accepted: %s" lines[at]
        | Error error -> error, at + 1, lines[at]

    [<Fact>]
    let ``non-canonical spellings are rejected with the line and column`` () =
        let check (name: string) (needle: string) (edit: string -> string) (columnOf: string -> int) (message: string) =
            let error, line, mutated = rejected name needle edit
            Assert.Equal((line, columnOf mutated, message), (error.Line, error.Column, error.Message.Split('\n')[0]))
        // An explicit default.
        check "verification-planner" " exported=true" (fun l -> l.Replace(" exported=true", " exported=false")) (fun l -> l.IndexOf "exported=" + 1) "exported=false is omitted"
        // Keys out of the writer's order.
        check "verification-planner" "result-type=bool body=" (fun l -> l.Replace(" result-type=bool", "") + " result-type=bool") (fun l -> l.IndexOf "result-type=" + 1) "result-type= is out of canonical order"
        // Two spaces.
        check "verification-planner" "parameter-reference" (fun l -> l.Replace(" in=", "  in=")) (fun l -> l.IndexOf "  in=" + 2) "single spaces only, no trailing space"
        // A string escape the writer would not produce.
        check "verification-planner" "value=\"fast tests only\"" (fun l -> l.Replace("\"fast tests only\"", "\"fast \\u0074ests only\"")) (fun l -> l.IndexOf "value=" + 1) "value= is not spelled as the writer's encoder spells it"
        // A keyed list out of key order.
        check "mini-work-planner" "arguments=[parameter:" (fun l ->
            let at = l.IndexOf "arguments=["
            let items = l.Substring(at + 11, l.IndexOf(']', at) - at - 11).Split ' '
            l.Substring(0, at + 11) + String.Join(" ", Array.rev items) + l.Substring(l.IndexOf(']', at)))
            (fun l -> l.IndexOf "arguments=" + 1) "arguments= is not in canonical order"
        // A non-canonical integer.
        check "mini-work-planner" " position=1 " (fun l -> l.Replace(" position=1 ", " position=01 ")) (fun l -> l.IndexOf "position=" + 1) "position= must be an integer"
        // A member out of canonical order: the first parameter after the second.
        let planner = RecordCodec.serialize (fixture "mini-work-planner") |> unwrap
        let plannerLines = planner.Split '\n'
        let p1 = plannerLines |> Array.findIndex (fun l -> l.Contains " position=1 ")
        let swapped = Array.copy plannerLines
        swapped[p1 - 1] <- plannerLines[p1]
        swapped[p1] <- plannerLines[p1 - 1]
        match RecordCodec.deserialize (String.Join("\n", swapped)) with
        | Error error -> Assert.Equal((p1, "position= must be 0"), (error.Line, error.Message))
        | Ok _ -> failwith "Swapped parameters were accepted."
        let text = RecordCodec.serialize (fixture "verification-planner") |> unwrap
        let lines = text.Split '\n'
        // A missing final newline and a carriage return.
        match RecordCodec.deserialize (text.TrimEnd '\n') with
        | Error error -> Assert.Equal((lines.Length - 1, "missing final newline"), (error.Line, error.Message))
        | Ok _ -> failwith "A missing final newline was accepted."
        // An earlier schema version: the record form has only the current one.
        let current = $" schema={SnapshotSchema.current.Json}"
        match RecordCodec.deserialize (text.Replace(current, " schema=20")) with
        | Error error -> Assert.Equal((1, text.IndexOf(current) + 2), (error.Line, error.Column))
        | Ok _ -> failwith "An earlier schema version was accepted."
        match RecordCodec.deserialize (text.Replace("\n", "\r\n")) with
        | Error error -> Assert.Equal((1, "carriage return"), (error.Line, error.Message))
        | Ok _ -> failwith "CRLF was accepted."

    [<Fact>]
    let ``a validation failure keeps the JSON reader's structured issues`` () =
        let text = RecordCodec.serialize (fixture "verification-planner") |> unwrap
        let line = text.Split('\n') |> Array.find (fun l -> l.StartsWith "function " && l.Contains " result-type=bool ")
        match RecordCodec.deserialize (text.Replace(line, line.Replace(" result-type=bool ", " result-type=string "))) with
        | Error { Model = Some(SnapshotCodecError.InvalidSnapshot issues) } -> Assert.NotEmpty issues
        | other -> failwithf "Expected a structured validation failure, got %A" other

    [<Fact>]
    let ``a dangling reference is reported where the missing record would sit`` () =
        let text = RecordCodec.serialize (fixture "verification-planner") |> unwrap
        let lines = text.Split '\n'
        // Delete a node record: its owner's reference to it dangles at the deleted line.
        let at = lines |> Array.findIndex (fun l -> l.StartsWith "bool-literal ")
        let mutated = String.Join("\n", lines |> Array.removeAt at)
        match RecordCodec.deserialize mutated with
        | Error error -> Assert.Equal(at + 1, error.Line)
        | Ok _ -> failwith "A deleted node was not noticed."

    /// One seeded mutation of the harness's six kinds (Phase2.fs `mutate`): a character replaced,
    /// inserted or deleted; a line deleted, duplicated or swapped with the next.
    let private mutate (rng: Random) (index: int) (text: string) =
        let alphabet = [| yield '\n'; for c in 0x20 .. 0x7E -> char c |]
        if index % 2 = 0 then
            let pos = rng.Next text.Length
            match index % 6 with
            | 0 -> text.Substring(0, pos) + string alphabet[rng.Next alphabet.Length] + text.Substring(pos + 1)
            | 2 -> text.Substring(0, pos) + string alphabet[rng.Next alphabet.Length] + text.Substring pos
            | _ -> text.Substring(0, pos) + text.Substring(pos + 1)
        else
            let lines = text.Substring(0, text.Length - 1).Split '\n'
            let i = rng.Next(lines.Length - 1)
            let join (ls: string seq) = String.Join("\n", ls) + "\n"
            match index % 6 with
            | 1 -> join (Array.removeAt i lines)
            | 3 -> join (Array.insertAt i lines[i] lines)
            | _ ->
                let swapped = Array.copy lines
                swapped[i] <- lines[i + 1]
                swapped[i + 1] <- lines[i]
                join swapped

    [<Fact>]
    let ``every accepted input is canonical: serialize (deserialize x) = x`` () =
        let text = RecordCodec.serialize (fixture "verification-planner") |> unwrap
        let rng = Random 20261023
        let mutable accepted = 0
        for index in 0 .. 299 do
            let mutated = mutate rng index text
            match RecordCodec.deserialize mutated with
            | Error error -> Assert.True(error.Line >= 1 && error.Column >= 1, $"#{index}: {error}")
            | Ok snapshot ->
                accepted <- accepted + 1
                Assert.Equal(mutated, RecordCodec.serialize snapshot |> unwrap)
        Assert.True(accepted < 10, $"{accepted} of 300 mutations were accepted")
