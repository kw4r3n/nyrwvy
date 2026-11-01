namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit
open Xunit.Abstractions

/// The Zig model reader (tools/zig/model-reader) turns snapshot JSON into the same canonical JSON as
/// `SnapshotCodec.toCanonicalJson`, or rejects it with the same error.
module ZigModelReaderSupport =
    let root () =
        let rec find (directory: DirectoryInfo) =
            if File.Exists(Path.Combine(directory.FullName, "Nyrwvy.slnx")) then directory.FullName else find directory.Parent
        find (DirectoryInfo AppContext.BaseDirectory)

    let unwrap = function Ok x -> x | Error e -> failwithf "%A" e

    let toolFile name = Path.Combine(root (), "tools", "zig", "model-reader", name)

    /// Every object and array below a node, in document order.
    let rec containers (node: JsonNode) : JsonNode list =
        match node with
        | :? JsonObject as o -> node :: [ for property in o do if not (isNull property.Value) then yield! containers property.Value ]
        | :? JsonArray as items -> node :: [ for item in items do if not (isNull item) then yield! containers item ]
        | _ -> []

    /// The graph nodes of a snapshot JSON (the objects in every `nodes` array), in document order.
    let graphNodes (document: JsonNode) =
        containers document
        |> List.collect (fun node ->
            match node with
            | :? JsonObject as o ->
                match o["nodes"] with
                | :? JsonArray as items -> [ for item in items do match item with :? JsonObject as n -> yield n | _ -> () ]
                | _ -> []
            | _ -> [])

    let allKindsJson () = RecordCodecAllKindsTests.allKindSnapshots () |> List.map (SnapshotCodec.serialize >> unwrap)

    /// `schema_minimums.txt`: the supported versions, then every construct row in table order, with
    /// the node kinds that always need it. The features a kind needs only in some forms (a timed
    /// TaskWait, a shared MatchUnion arm, a construct with type arguments) list no kinds, since the
    /// corpus need not show the other forms; the model reader marks those itself, as it marks the
    /// rows of types and declarations.
    let minimumsText () =
        let snapshots = RecordCodecAllKindsTests.allKindSnapshots ()
        let nodes = snapshots |> List.collect (fun snapshot -> snapshot.Functions |> List.collect (fun fn -> ValueGraph.collectNodes fn.Body))
        let kindName (node: SemanticNode) = let name = SemanticNodeKind.name node.Kind in string (Char.ToLowerInvariant name[0]) + name.Substring 1
        let byPayload = set [ SnapshotSchemaFeature.TimedTaskWait; SnapshotSchemaFeature.SharedUnionMatchArm; SnapshotSchemaFeature.GenericTypes ]
        let kindsOf feature =
            if byPayload.Contains feature then [] else
            nodes
            |> List.groupBy kindName
            |> List.filter (fun (_, group) -> group |> List.forall (fun node -> SnapshotSchema.featureOfNodeKind node.Kind = Some feature))
            |> List.map fst
            |> List.sortWith (fun x y -> String.CompareOrdinal(x, y))
        let versions = SnapshotSchema.supported |> List.map (fun version -> version.Json)
        Assert.Equal<int list>([ List.min versions .. List.max versions ], versions)
        [ yield $"versions {List.min versions} {SnapshotSchema.firstRecord.Json} {SnapshotSchema.current.Json}"
          for feature, minimum, name in SnapshotSchema.minimumVersions do
              yield $"{minimum.Json}|{feature}|{name}|" + String.Join(" ", kindsOf feature) ]
        |> List.map (fun line -> line + "\n")
        |> String.concat ""

    /// One deterministic structural change to a snapshot JSON.
    let mutate (kindNames: string array) (random: Random) (json: string) =
        let document = JsonNode.Parse json
        let all = containers document |> Array.ofList
        let objects = all |> Array.choose (function :? JsonObject as o when o.Count > 0 -> Some o | _ -> None)
        let pick (items: 'a array) = items[random.Next items.Length]
        let keys (o: JsonObject) = o |> Seq.map (fun property -> property.Key) |> Array.ofSeq
        let isString (node: JsonNode) = node :? JsonValue && node.GetValueKind() = JsonValueKind.String
        let stringProperties (test: string -> bool) =
            objects |> Array.collect (fun o -> keys o |> Array.filter (fun key -> test key && isString o[key]) |> Array.map (fun key -> o, key))
        let text () = document.ToJsonString()
        match random.Next 10 with
        | 0 ->
            let o = pick objects
            o.Remove(pick (keys o)) |> ignore
            text ()
        | 1 ->
            (pick objects)[pick [| "unexpected"; "Id"; "kind2" |]] <- JsonValue.Create 1
            text ()
        | 2 ->
            let o = pick objects
            let key = pick (keys o)
            o[key] <-
                match o[key] with
                | :? JsonObject -> JsonArray() :> JsonNode
                | :? JsonArray -> JsonObject()
                | value when isNull value -> JsonValue.Create "null"
                | value when isString value -> JsonValue.Create 7
                | value when value.GetValueKind() = JsonValueKind.Number -> JsonValue.Create "7"
                | _ -> JsonValue.Create 0
            text ()
        | 3 ->
            match stringProperties (fun key -> key = "id" || key.EndsWith "Id") with
            | [||] -> text ()
            | candidates ->
                let o, key = pick candidates
                o[key] <- JsonValue.Create(pick [| ""; " "; "\t"; "\u00a0"; "\u3000"; "a\nb"; "x\r"; "\u0085"; "é"; "<&>" |])
                text ()
        | 4 ->
            // A duplicated property, the first copy holding the same or another value.
            let o = pick objects
            let key = pick (keys o)
            let value = if isNull o[key] then "null" else o[key].ToJsonString()
            o.Remove key |> ignore
            o["\u0000duplicate"] <- JsonValue.Create 0
            let first = pick [| value; "\"bogus\""; "true" |]
            let name = JsonSerializer.Serialize key
            text().Replace("\"\\u0000duplicate\":0", $"{name}:{first},{name}:{value}")
        | 5 ->
            match all |> Array.choose (function :? JsonArray as items when items.Count > 1 -> Some items | _ -> None) with
            | [||] -> text ()
            | arrays ->
                let items = pick arrays
                let content = items |> Array.ofSeq
                items.Clear()
                let shift = random.Next(1, content.Length)
                for index in 0 .. content.Length - 1 do items.Add(content[(index + shift) % content.Length])
                text ()
        | 6 ->
            let version = pick [| "20"; "21"; "22"; "23"; "25"; "9"; "8"; "0"; "-1"; "24.0"; "2.4e1"; "\"24\""; "null"; "2147483648"; "-0" |]
            Regex("\"schemaVersion\":-?\\d+").Replace(text (), $"\"schemaVersion\":{version}", 1)
        | 7 ->
            match stringProperties (fun key -> key = "kind") with
            | [||] -> text ()
            | candidates ->
                let o, key = pick candidates
                o[key] <- JsonValue.Create(pick (Array.append kindNames [| "product"; "union"; "primitive"; "named"; "function"; "missingArgument"; "bogus" |]))
                text ()
        | 8 ->
            match stringProperties (fun key -> key = "displayName" || key = "value" || key = "bits" || key = "mode" || key = "status" || key = "primitive" || key = "generic") with
            | [||] -> text ()
            | candidates ->
                let o, key = pick candidates
                o[key] <- JsonValue.Create(pick [| "é<&>\"'+`\u007f\u2028😀"; ""; "\\"; "\b\f\n\r\t\u0001"; "3FF0000000000000"; "3ff000000000000g"; "all"; "resolved"; "bytes"; "taskScope" |])
                text ()
        | _ ->
            let o = pick objects
            o[pick [| "exported"; "mayWait"; "taskBound"; "mustNotWait"; "typeParameters"; "context"; "typeArguments"; "failureType"; "limitNodeId"; "parentSnapshotId"; "displayName" |]] <-
                pick [| JsonValue.Create true :> JsonNode; JsonValue.Create false; JsonArray(); JsonValue.Create "x" |]
            text ()

module ZigModelReaderTests =
    open ZigModelReaderSupport

    [<Fact>]
    let ``schema_minimums.txt is the construct table derived from SnapshotSchema`` () =
        let derived = minimumsText ()
        Assert.True(
            (derived = File.ReadAllText(toolFile "schema_minimums.txt")),
            $"tools/zig/model-reader/schema_minimums.txt no longer matches SnapshotSchema; replace it with:\n\n{derived}")

    [<Fact>]
    let ``model_kinds.txt names every node kind the writer writes`` () =
        let written = allKindsJson () |> List.collect (fun json -> graphNodes (JsonNode.Parse json)) |> List.map (fun node -> string node["kind"]) |> Set.ofList
        let table = File.ReadAllLines(toolFile "model_kinds.txt") |> Array.map (fun line -> line.Split('|')[0]) |> Set.ofArray
        Assert.Equal<Set<string>>(written, table)

/// The extended comparison (a class, so that it can report its counts).
type ZigModelReaderComparison(output: ITestOutputHelper) =
    let kindNames = lazy (File.ReadAllLines(ZigModelReaderSupport.toolFile "model_kinds.txt") |> Array.map (fun line -> line.Split('|')[0]))

    let mutate random json = ZigModelReaderSupport.mutate kindNames.Value random json

    /// For the first node of each kind: an unsupported property, then each property dropped and retyped.
    let perKind (documents: string list) =
        let seen = Collections.Generic.HashSet<string>()
        [ for json in documents do
              let nodes = ZigModelReaderSupport.graphNodes (JsonNode.Parse json)
              for index, node in List.indexed nodes do
                  if seen.Add(string node["kind"]) then
                      let edit (change: JsonObject -> unit) =
                          let document = JsonNode.Parse json
                          change (List.item index (ZigModelReaderSupport.graphNodes document))
                          document.ToJsonString()
                      yield edit (fun n -> n["unexpected"] <- JsonValue.Create 1)
                      for property in node |> Seq.map (fun p -> p.Key) |> List.ofSeq do
                          if property <> "id" then
                              yield edit (fun n -> n.Remove property |> ignore)
                              yield edit (fun n -> n[property] <- (if n[property] :? JsonValue && n[property].GetValueKind() = JsonValueKind.Number then JsonValue.Create "0" else JsonValue.Create 0)) ]

    [<Fact; Trait("Suite", "Extended")>]
    member _.``The Zig model reader matches the F# shape reader`` () =
        let root = ZigModelReaderSupport.root ()
        let allKinds = ZigModelReaderSupport.allKindsJson ()
        let fixtures =
            Directory.GetFiles(Path.Combine(root, "tests", "Nyrwvy.Core.Tests", "fixtures", "record-format"), "*.json")
            |> Array.map File.ReadAllText |> List.ofArray
        let repository =
            Directory.GetFiles(Path.Combine(root, "examples"), "*.nyrec", SearchOption.AllDirectories)
            |> Array.map (File.ReadAllText >> RecordCodec.toJson >> ZigModelReaderSupport.unwrap) |> List.ofArray
        let originals = allKinds @ fixtures @ repository
        let mutated = originals |> List.mapi (fun index json -> let random = Random index in [ for _ in 1 .. 4 -> mutate random json ]) |> List.concat
        let replaceFirst (pattern: string) (replacement: string) (text: string) = Regex(pattern).Replace(text, replacement, 1)
        let version (v: string) = replaceFirst "\"schemaVersion\":\\d+" $"\"schemaVersion\":{v}"
        let nested depth = String.replicate depth "{\"kind\":\"builtinGeneric\",\"generic\":\"list\",\"arguments\":[" + "{\"kind\":\"primitive\",\"primitive\":\"int64\"}" + String.replicate depth "]}"
        // Type nesting across the 1,000-container limit: both sides of it occur.
        let deep =
            let host = allKinds |> List.find (fun json -> json.Contains "{\"kind\":\"primitive\",\"primitive\":\"int64\"}")
            [ for depth in 494 .. 500 -> replaceFirst "\\{\"kind\":\"primitive\",\"primitive\":\"int64\"\\}" (nested depth) host ]
        let deepOutcomes = deep |> List.map (SnapshotCodec.toCanonicalJson >> function Ok _ -> "ok" | Error(SnapshotCodecError.InvalidJson _) -> "json" | Error e -> $"%A{e}") |> set
        Assert.Equal<Set<string>>(set [ "ok"; "json" ], deepOutcomes)
        // Each targeted edit on the first eight documents it changes.
        let edit (change: string -> string) =
            originals |> List.choose (fun json -> let edited = change json in if edited <> json then Some edited else None) |> List.truncate 8
        let targeted =
            [ for json in allKinds do
                  yield! [ "21"; "22"; "23" ] |> List.map (fun v -> version v json)
              yield! edit (replaceFirst "\"displayName\":" "\"display\\u004Eame\":")
              yield! edit (replaceFirst "\"displayName\":\"" "\"displayName\":\"\\ud800")
              yield! edit (replaceFirst "\"kind\":\"stringLiteral\",\"value\":\"" "\"kind\":\"stringLiteral\",\"value\":\"\\udc00x\\ud83d\\ude00")
              for bits in [ "7FF8000000000000"; "7ff0000000000001"; "fff8000000000000"; "7ff800000000000"; "7ff800000000000g"; "+7ff800000000000" ] do
                  yield! edit (replaceFirst "\"kind\":\"float64Literal\",\"bits\":\"[0-9a-f]{16}\"" $"\"kind\":\"float64Literal\",\"bits\":\"{bits}\"")
              for n in [ "-0"; "1.0"; "1e2"; "9223372036854775808"; "-9223372036854775808" ] do
                  yield! edit (replaceFirst "\"kind\":\"int64Literal\",\"value\":-?\\d+" $"\"kind\":\"int64Literal\",\"value\":{n}")
              for marker in [ "\"exported\":false"; "\"exported\":true"; "\"exported\":1" ] do
                  yield! edit (replaceFirst "\\{\"id\":" ("{" + marker + ",\"id\":"))
              yield! deep
              for json in List.truncate 20 originals do
                  let random = Random json.Length
                  yield json.Substring(0, random.Next json.Length)
                  let at = random.Next json.Length
                  yield json.Insert(at, [| ","; "}"; "x"; "\"" |][random.Next 4]) ]
            |> List.distinct
        let texts = originals @ mutated @ perKind allKinds @ targeted
        let reader = ZigTools.tool "model-reader" |> ZigModelReaderSupport.unwrap
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-model-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            let info = ProcessStartInfo(reader, RedirectStandardOutput = true)
            texts |> List.iteri (fun index text ->
                File.WriteAllText(Path.Combine(dir, $"{index}.in"), text, Text.UTF8Encoding false)
                info.ArgumentList.Add(Path.Combine(dir, $"{index}.in"))
                info.ArgumentList.Add(Path.Combine(dir, $"{index}.json")))
            use child = Process.Start info
            let lines = child.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            child.WaitForExit()
            Assert.Equal(0, child.ExitCode)
            Assert.Equal(texts.Length, lines.Length)
            // Messages that embed a .NET exception message are compared by their prefix only.
            let caseOnly = [ "error InvalidJson"; "error InvalidShape: Invalid JSON string encoding:" ]
            let mutable accepted, skipped, writerFailures = 0, 0, 0
            let rejected = Collections.Generic.Dictionary<string, int>()
            texts |> List.iteri (fun index text ->
                if lines[index].StartsWith "skip legacy schema " then skipped <- skipped + 1
                else
                    let expected =
                        try Some(SnapshotCodec.toCanonicalJson text)
                        with error ->
                            output.WriteLine($"case {index}: the F# writer threw {error.GetType().Name}: {error.Message}")
                            None
                    match expected with
                    | None -> writerFailures <- writerFailures + 1
                    | Some(Ok json) ->
                        accepted <- accepted + 1
                        Assert.True("ok" = lines[index], $"case {index}: F# accepts, Zig says {lines[index]}\n{text}")
                        Assert.True((json = File.ReadAllText(Path.Combine(dir, $"{index}.json"))), $"case {index}: the JSON differs\n{text}")
                    | Some(Error error) ->
                        let line =
                            // One line per input: the Zig reader writes a line break in a message as `\n`.
                            (fun (text: string) -> text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n")) <|
                            match error with
                            | SnapshotCodecError.InvalidJson message -> $"error InvalidJson: {message}"
                            | SnapshotCodecError.InvalidShape message -> $"error InvalidShape: {message}"
                            | SnapshotCodecError.UnsupportedSchemaVersion version -> $"error UnsupportedSchemaVersion: {version}"
                            | SnapshotCodecError.MaximumDepthExceeded depth -> $"error MaximumDepthExceeded: {depth}"
                            | SnapshotCodecError.InvalidSnapshot issues -> failwithf "toCanonicalJson validated: %A" issues
                        match caseOnly |> List.tryFind line.StartsWith with
                        | Some prefix ->
                            Assert.True(lines[index].StartsWith prefix, $"case {index}: F# says {line}, Zig says {lines[index]}\n{text}")
                            rejected[prefix] <- (match rejected.TryGetValue prefix with | true, n -> n + 1 | _ -> 1)
                        | None ->
                            Assert.True((line = lines[index]), $"case {index}: F# says {line}, Zig says {lines[index]}\n{text}")
                            let shape = Regex.Replace(line, "'[^']*'", "'…'")
                            rejected[shape] <- (match rejected.TryGetValue shape with | true, n -> n + 1 | _ -> 1))
            let rejectedCount = rejected.Values |> Seq.sum
            output.WriteLine($"{texts.Length} texts: {accepted} accepted, {rejectedCount} rejected with {rejected.Count} distinct messages, {skipped} legacy skipped, {writerFailures} writer failures")
            for KeyValue(shape, count) in rejected |> Seq.sortBy (fun pair -> -pair.Value) do output.WriteLine($"{count,5} {shape}")
            // Both outcomes occur and the rejections are varied, so neither half of the comparison is vacuous.
            Assert.True(accepted > 100 && rejectedCount > 300 && rejected.Count > 40, $"accepted {accepted}, rejected {rejectedCount}, distinct {rejected.Count}")
        finally Directory.Delete(dir, true)

module ZigTypedDirectWriterTests =
    open ZigModelReaderSupport

    let private id<'tag> text = StableId.createForTesting<'tag> text

    [<Fact; Trait("Suite", "Extended")>]
    let ``The direct typed writer matches F sharp canonical snapshots and fingerprints`` () =
        let reader = ZigTools.tool "model-reader" |> unwrap
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-direct-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            // Reverse only collections whose order is defined by ID, leaving parameters and type arguments alone.
            let reordered (json: string) =
                let root = JsonNode.Parse(json).AsObject()
                for key in [ "types"; "effects"; "capabilities"; "functions"; "obligations" ] do
                    match root[key] with
                    | :? JsonArray as items ->
                        let copy = items |> Seq.map (fun item -> item.DeepClone()) |> Seq.rev |> Seq.toArray
                        items.Clear()
                        for item in copy do items.Add item
                    | _ -> ()
                // Reverse object properties too, so the input is not already in canonical order.
                let properties = root |> Seq.map (fun p -> p.Key, if isNull p.Value then null else p.Value.DeepClone()) |> Seq.rev |> Seq.toArray
                root.Clear()
                for key, value in properties do root.Add(key, value)
                root.ToJsonString()
            let check name input expected printed =
                let source, output = Path.Combine(dir, name + ".in.json"), Path.Combine(dir, name + ".out.json")
                File.WriteAllText(source, reordered input, Text.UTF8Encoding false)
                let info = ProcessStartInfo(reader, [ "--typed-direct-roundtrip"; source; output ])
                info.RedirectStandardOutput <- true
                info.RedirectStandardError <- true
                use child = Process.Start info
                let actual = child.StandardOutput.ReadToEnd()
                let errors = child.StandardError.ReadToEnd()
                child.WaitForExit()
                Assert.True(child.ExitCode = 0, $"{name}: {errors}")
                Assert.Equal(printed, actual)
                Assert.Equal(expected, File.ReadAllText output)
            let plain name (snapshot: Snapshot) =
                let json = SnapshotCodec.serialize snapshot |> unwrap
                let hashes = $"snapshot {SnapshotFingerprint.compute snapshot |> unwrap |> SnapshotFingerprint.value}\nsemantic {SemanticFingerprint.compute snapshot |> unwrap |> SemanticFingerprint.value}\n"
                check name json json hashes
            let parameter = id<TypeParameterTag> "typed-unicode-parameter"
            let generic: Snapshot =
                { Snapshot.empty (id<SnapshotTag> "typed-unicode-snapshot") with
                    Types = [ { Id = id<TypeTag> "typed-unicode-type"; DisplayName = "型😀<&>"; Exported = true
                                TypeParameters = [ parameter, "型引数😀" ]
                                Kind = SemanticTypeKind.Product [ { Id = id<FieldTag> "typed-unicode-field"; DisplayName = "値 é"; Type = TypeRef.TypeParameter parameter } ] } ] }
            plain "unicode-generic" generic
            let kinds = RecordCodecAllKindsTests.allKindSnapshots ()
            for index, snapshot in List.indexed kinds do plain $"kind-{index}" snapshot
            let examples = Directory.GetFiles(Path.Combine(root (), "examples"), "*.nyrec", SearchOption.AllDirectories) |> Array.sort
            Assert.NotEmpty examples
            for index, path in Array.indexed examples do
                let snapshot = File.ReadAllText path |> RecordCodec.deserialize |> unwrap
                plain $"example-{index}" snapshot
            let snapshot = List.head kinds
            let dependency = { snapshot with Id = id<SnapshotTag> "typed-direct-dependency"; ParentId = None; Functions = []; Obligations = [] }
            let local = { snapshot with Types = []; Effects = []; Capabilities = [] }
            let resolved = SnapshotCodec.serializeResolved dependency local |> unwrap
            check "resolved" resolved resolved "resolved\n"
        finally
            Directory.Delete(dir, true)
