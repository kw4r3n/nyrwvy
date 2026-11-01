namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open Nyrwvy.Core
open Xunit
open Xunit.Abstractions

module ZigLoweringTests =
    [<Fact>]
    let ``a Snapshot read from Zig-validated JSON is not validated again`` () =
        let json = ZigModelReaderSupport.allKindsJson () |> List.head
        let validations read =
            use scope = SnapshotComputationScope.Start(measure = true)
            let snapshot = read json |> ZigModelReaderSupport.unwrap
            Assert.Empty(SnapshotValidator.validate snapshot)
            scope.Measurements |> List.sumBy (fun item -> if item.Name = "validation" then item.Computations else 0)
        Assert.Equal(0, validations SnapshotCodec.readZigValidated)
        Assert.Equal(1, validations SnapshotCodec.readUnvalidated)

    [<Fact>]
    let ``instruction_kinds.txt lists every node kind that lowers to an instruction`` () =
        let rows = File.ReadAllLines(ZigModelReaderSupport.toolFile "instruction_kinds.txt") |> Array.map (fun line -> line.Split('|')[0]) |> Set.ofArray
        let handwritten = set [ "parameterReference"; "patternReference"; "abort"; "conditional"; "boolAnd"; "boolOr"; "matchUnion"; "bind"; "matchOption"; "matchResult" ]
        let kinds = File.ReadAllLines(ZigModelReaderSupport.toolFile "model_kinds.txt") |> Array.map (fun line -> line.Split('|')[0]) |> Set.ofArray
        Assert.Equal<Set<string>>(kinds - handwritten, rows)

/// Compares `model-reader --typed-ir` with `TypedSemanticIr.project` of `SnapshotCodec.readUnvalidated`
/// (after `ExecutionCallGraph.forSnapshotEntry` for an entry), serialized by `TypedSemanticIrSerializer`.
type ZigLoweringComparison(output: ITestOutputHelper) =
    let project (snapshot: Snapshot) (entry: FunctionId option) =
        let selected = match entry with Some id -> ExecutionCallGraph.forSnapshotEntry snapshot id | None -> snapshot
        TypedSemanticIr.project selected
        |> Result.mapError (function
            | TypedSemanticIrError.InvalidSnapshot issues -> $"error InvalidSnapshot: {issues.Length} issues"
            | TypedSemanticIrError.IncompleteSnapshot ids -> "error IncompleteSnapshot: " + String.Join(", ", ids |> List.map StableId.value)
            | TypedSemanticIrError.MissingNodeType(functionId, nodeId) -> $"error MissingNodeType: {StableId.value functionId} {StableId.value nodeId}")

    /// The F# line for one input and entry: `ok` with the JSON, or the error line the Zig tool prints.
    let typedReference snapshot entry =
        match project snapshot entry with
        | Ok program -> "ok", Some(TypedSemanticIrSerializer.serialize program)
        | Error line -> line, None

    let executionReference snapshot entry =
        let id x = StableId.value x
        match project snapshot entry |> Result.bind (fun typed -> ExecutionIr.lower typed |> Result.mapError (function
            | ExecutionIrError.MissingNodeValue(f, node) -> $"error MissingNodeValue: {id f} {id node}"
            | ExecutionIrError.MissingPatternValue(f, binding) -> $"error MissingPatternValue: {id f} {id binding}"
            | ExecutionIrError.MissingFunctionParameter(f, parameter) -> $"error MissingFunctionParameter: {id f} {id parameter}"
            | ExecutionIrError.UnresolvedDependencies(f, nodes) -> $"error UnresolvedDependencies: {id f} " + String.Join(", ", nodes |> List.map id)
            | ExecutionIrError.AmbiguousEffectOrder(f, first, second) -> $"error AmbiguousEffectOrder: {id f} {id first} {id second}"
            | ExecutionIrError.UnterminatedBlock(f, block) -> $"error UnterminatedBlock: {id f} {ExecutionBlockId.value block}")) with
        | Ok program -> "ok", Some(ExecutionIrSerializer.serialize program)
        | Error line -> line, None

    /// The first difference of two texts, with some context.
    let difference (expected: string) (actual: string) =
        let at = Seq.zip expected actual |> Seq.tryFindIndex (fun (left, right) -> left <> right) |> Option.defaultValue (min expected.Length actual.Length)
        let around (text: string) = text.Substring(max 0 (at - 120), min 240 (text.Length - max 0 (at - 120)))
        $"at {at}:\nF#  {around expected}\nZig {around actual}"

    /// Runs `model-reader <mode>` on every corpus text that decodes, whole and with each of up to six
    /// of its functions as entry, and compares each line and JSON with `reference`.
    let compare (mode: string) reference =
        let originals, targeted, combined, _ = ZigValidatorSupport.corpus ()
        let texts = originals @ targeted @ combined |> List.distinct
        // Each model whole, and with each of up to six of its functions as entry.
        let cases =
            [ for text in texts do
                  match SnapshotCodec.readUnvalidated text with
                  | Error _ -> ()
                  | Ok snapshot ->
                      yield text, snapshot, None
                      for fn in snapshot.Functions |> List.truncate 6 do yield text, snapshot, Some fn.Id ]
        let reader = ZigTools.tool "model-reader" |> ZigModelReaderSupport.unwrap
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-lowering-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            let inputs = Collections.Generic.Dictionary<string, string>()
            let input text =
                match inputs.TryGetValue text with
                | true, path -> path
                | _ ->
                    let path = Path.Combine(dir, $"{inputs.Count}.in")
                    File.WriteAllText(path, text, Text.UTF8Encoding false)
                    inputs[text] <- path
                    path
            // In batches, within the argument list limit.
            let run (mode: string list) (suffix: string) (selected: (int * _) list) =
                selected |> List.chunkBySize 1000 |> List.collect (fun batch ->
                    let info = ProcessStartInfo(reader, RedirectStandardOutput = true)
                    for argument in mode do info.ArgumentList.Add argument
                    for index, (text, _, entry) in batch do
                        info.ArgumentList.Add(input text)
                        info.ArgumentList.Add(Path.Combine(dir, $"{index}{suffix}"))
                        info.ArgumentList.Add(match entry with Some id -> StableId.value id | None -> "-")
                    use child = Process.Start info
                    let lines = child.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    child.WaitForExit()
                    Assert.Equal(0, child.ExitCode)
                    List.ofArray lines)
                |> Array.ofList
            let lines = run [ mode ] ".out" (List.indexed cases)
            Assert.Equal(cases.Length, lines.Length)
            let outcomes = Collections.Generic.Dictionary<string, int>()
            let mutable throws = 0
            let failures = ResizeArray<string>()
            cases |> List.iteri (fun index (text, snapshot, entry) ->
                match (try Some(reference snapshot entry : string * string option) with error -> output.WriteLine($"case {index}: F# threw {error.GetType().Name}: {error.Message}"); None) with
                | None -> throws <- throws + 1
                | Some(line, json) ->
                    let key = (line.Split(':')[0])
                    outcomes[key] <- (match outcomes.TryGetValue key with | true, n -> n + 1 | _ -> 1)
                    if lines[index] <> line then failures.Add($"case {index} (entry {entry}): F# says {line}, Zig says {lines[index]}")
                    else
                        match json with
                        | Some expected ->
                            let actual = File.ReadAllText(Path.Combine(dir, $"{index}.out"))
                            if expected <> actual then failures.Add($"case {index} (entry {entry}): the JSON differs {difference expected actual}")
                        | None -> ())
            output.WriteLine($"{cases.Length} cases from {texts.Length} texts ({inputs.Count} decoded), {throws} F# failures")
            for KeyValue(outcome, count) in outcomes |> Seq.sortBy (fun pair -> pair.Key) do output.WriteLine($"{count,6} {outcome}")
            Assert.True(failures.Count = 0, $"{failures.Count} differences; the first:\n" + String.Join("\n\n", failures |> Seq.truncate 5))
            let count key = match outcomes.TryGetValue key with | true, n -> n | _ -> 0
            Assert.True(count "ok" > 100 && count "error InvalidSnapshot" > 100 && count "error IncompleteSnapshot" > 0, "every outcome must occur")
            // A case where F# throws would compare nothing; none does on this corpus.
            Assert.Equal(0, throws)
            if mode = "--execution-ir" then
                // A model whose whole passes validation: no entry of it is invalid (readiness validates
                // the selection), and `--validated` lowers each case to the same line and bytes.
                let valid =
                    cases |> List.indexed |> List.choose (fun (index, (text, _, entry)) ->
                        if entry.IsNone && (lines[index] = "ok" || lines[index].StartsWith "error IncompleteSnapshot") then Some text else None)
                    |> set
                let selected = cases |> List.indexed |> List.filter (fun (_, (text, _, _)) -> valid.Contains text)
                for index, (_, _, entry) in selected do
                    Assert.False(lines[index].StartsWith "error InvalidSnapshot", $"case {index} (entry {entry}): an entry of a valid model is invalid: {lines[index]}")
                let again = run [ mode; "--validated" ] ".v.out" selected
                selected |> List.iteri (fun position (index, (_, _, entry)) ->
                    Assert.True((lines[index] = again[position]), $"case {index} (entry {entry}): {lines[index]} without --validated, {again[position]} with it")
                    if lines[index] = "ok" then
                        Assert.True((File.ReadAllText(Path.Combine(dir, $"{index}.out")) = File.ReadAllText(Path.Combine(dir, $"{index}.v.out"))), $"case {index} (entry {entry}): --validated changes the IR"))
                output.WriteLine($"{selected.Length} cases of {valid.Count} valid models again with --validated, identical")
        finally Directory.Delete(dir, true)

    [<Fact; Trait("Suite", "Extended")>]
    member _.``The Zig typed semantic IR matches the F# projection`` () = compare "--typed-ir" typedReference

    [<Fact; Trait("Suite", "Extended")>]
    member _.``The Zig Execution IR matches the F# lowering`` () = compare "--execution-ir" executionReference

