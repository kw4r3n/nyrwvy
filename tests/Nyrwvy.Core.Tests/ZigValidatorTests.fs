namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit
open Xunit.Abstractions

/// The Zig model reader's `--validate` mode (tools/zig/model-reader) reports the issues of the
/// codes in `validation_codes.txt` exactly as `SnapshotValidator.validate` does on the shape-decoded
/// model, filtered to those codes.
module ZigValidatorSupport =
    let codes () = File.ReadAllLines(ZigModelReaderSupport.toolFile "validation_codes.txt") |> Array.filter (fun line -> line <> "") |> Set.ofArray

    let escape (text: string) = text.Replace("\r", "\\r").Replace("\n", "\\n")

    let objectsIn (node: JsonNode) =
        ZigModelReaderSupport.containers node |> List.choose (function :? JsonObject as o -> Some o | _ -> None)

    /// The objects of each array property with this name, in document order.
    let arrayItems (name: string) (document: JsonNode) =
        objectsIn document
        |> List.collect (fun o ->
            match o[name] with
            | :? JsonArray as items -> [ items ]
            | _ -> [])

    let private obj (pairs: (string * JsonNode) list) =
        let o = JsonObject()
        for name, value in pairs do o[name] <- value
        o

    let private s (text: string) = JsonValue.Create text :> JsonNode

    let private typeParameter id name = obj [ "typeParameterId", s id; "displayName", s name ] :> JsonNode

    let private obligation id call parameter (resolution: string option) =
        obj
            [ yield "id", s id
              yield "kind", s "missingArgument"
              yield "originTransactionId", s "origin-transaction"
              yield "callNodeId", s call
              yield "parameterId", s parameter
              match resolution with
              | None -> yield "status", s "open"
              | Some value ->
                  yield "status", s "resolved"
                  yield "resolutionTransactionId", s "resolution-transaction"
                  yield "resolutionValueNodeId", s value ]
        :> JsonNode

    let private withTypeParameters (o: JsonObject) (declarations: JsonNode list) =
        let list = match o["typeParameters"] with :? JsonArray as items -> items | _ -> (let items = JsonArray() in o["typeParameters"] <- items; items)
        for declaration in declarations do list.Add declaration

    /// Named edits that keep the shape valid and break the ported rules. Each returns false when it
    /// does not apply to the document.
    let semanticEdits: (string * (JsonObject -> bool)) list =
        let first name (root: JsonObject) = arrayItems name root |> List.tryPick (fun items -> if items.Count > 0 then Some items else None)
        let functions (root: JsonObject) = match root["functions"] with :? JsonArray as items -> [ for f in items -> f :?> JsonObject ] | _ -> []
        let directCalls (root: JsonObject) =
            [ for f in functions root do
                  for node in ZigModelReaderSupport.graphNodes f do
                      if string (node["kind"]) = "directCall" then
                          match node["arguments"] with
                          | :? JsonArray as arguments when arguments.Count > 0 -> yield f, node, (arguments[0] :?> JsonObject)
                          | _ -> () ]
        let nodeOutside (root: JsonObject) (owner: JsonObject) =
            functions root |> List.filter (fun f -> not (Object.ReferenceEquals(f, owner))) |> List.tryPick (fun f -> ZigModelReaderSupport.graphNodes f |> List.tryHead)
        let addObligations (root: JsonObject) (make: JsonObject -> JsonObject -> JsonObject -> JsonNode list) =
            match directCalls root with
            | (owner, call, argument) :: _ ->
                let list = root["obligations"] :?> JsonArray
                for o in make owner call argument do list.Add o
                true
            | [] -> false
        [ "parent is self", (fun root -> root["parentSnapshotId"] <- s (string (root["snapshotId"])); true)
          for declarations in [ "types"; "effects"; "capabilities"; "functions"; "obligations" ] do
              $"duplicate {declarations} entry",
              (fun root ->
                  match root[declarations] with
                  | :? JsonArray as items when items.Count > 0 -> items.Add(items[0].DeepClone()); true
                  | _ -> false)
          for members in [ "fields"; "variants"; "operations"; "parameters"; "nodes" ] do
              $"second of {members} takes the first's ID",
              (fun root ->
                  match arrayItems members root |> List.tryFind (fun items -> items.Count > 1) with
                  | Some items -> items[1]["id"] <- s (string (items[0]["id"])); true
                  | None -> false)
          "a binding takes another's ID",
          (fun root ->
              let bindings = objectsIn root |> List.collect (fun o -> [ for name in [ "valueBinding"; "errorBinding"; "binding" ] do match o[name] with :? JsonObject as b -> yield b | _ -> () ])
              match bindings with
              | b1 :: b2 :: _ -> b2["id"] <- s (string (b1["id"])); true
              | _ -> false)
          for blank in [ ""; " "; "\u3000" ] do
              $"every display name is '{blank}'",
              (fun root ->
                  let named = objectsIn root |> List.filter (fun o -> o.ContainsKey "displayName")
                  for o in named do o["displayName"] <- s blank
                  not named.IsEmpty)
          "bindings get a blank display name",
          (fun root ->
              let bindings = objectsIn root |> List.collect (fun o -> [ for name in [ "valueBinding"; "errorBinding"; "binding" ] do match o[name] with :? JsonObject as b -> yield b | _ -> () ])
              for b in bindings do b["displayName"] <- s "\t"
              not bindings.IsEmpty)
          for name in [ "effectId"; "callNodeId"; "parameterId"; "resolutionValueNodeId"; "targetFunctionId" ] do
              $"{name} points nowhere",
              (fun root ->
                  match objectsIn root |> List.tryFind (fun o -> o.ContainsKey name) with
                  | Some o -> o[name] <- s "missing-reference"; true
                  | None -> false)
          "a required effect is unknown and one is listed twice",
          (fun root ->
              match functions root |> List.tryFind (fun f -> (f["requiredEffects"] :?> JsonArray).Count > 0) with
              | Some f ->
                  let effects = f["requiredEffects"] :?> JsonArray
                  effects.Add(effects[0].DeepClone())
                  effects.Add(s "missing-effect")
                  true
              | None -> false)
          "a function declares unused, duplicate and blank type parameters",
          (fun root ->
              match functions root with
              | f :: _ ->
                  withTypeParameters f [ typeParameter "tp-unused" "T"; typeParameter "tp-unused" "T"; typeParameter "tp-blank" " " ]
                  true
              | [] -> false)
          "a type declares duplicate and blank type parameters",
          (fun root ->
              match first "types" root with
              | Some types ->
                  withTypeParameters (types[0] :?> JsonObject) [ typeParameter "tp-type" "T"; typeParameter "tp-type" "T"; typeParameter "tp-type-blank" "" ]
                  true
              | None -> false)
          "two declarations share a type parameter",
          (fun root ->
              match functions root with
              | f :: _ ->
                  withTypeParameters f [ typeParameter "tp-shared" "S" ]
                  match root["types"] with
                  | :? JsonArray as types when types.Count > 0 -> withTypeParameters (types[0] :?> JsonObject) [ typeParameter "tp-shared" "S" ]
                  | _ -> withTypeParameters f [ typeParameter "tp-shared" "S" ]
                  true
              | [] -> false)
          "a capability operation uses a type parameter",
          (fun root ->
              match arrayItems "operations" root |> List.tryFind (fun items -> items.Count > 0) with
              | Some operations ->
                  operations[0]["resultType"] <- obj [ "kind", s "builtinGeneric"; "generic", s "list"; "arguments", JsonArray(obj [ "kind", s "typeParameter"; "typeParameterId", s "tp-op" ]) ]
                  true
              | None -> false)
          "an irregular recursive type",
          (fun root ->
              let self (arguments: JsonNode list) = obj [ "kind", s "named"; "typeId", s "rec-type"; "arguments", JsonArray(Array.ofList arguments) ] :> JsonNode
              let int64 () = obj [ "kind", s "primitive"; "primitive", s "int64" ] :> JsonNode
              let field id (typeRef: JsonNode) = obj [ "id", s id; "displayName", s id; "type", typeRef ] :> JsonNode
              (root["types"] :?> JsonArray).Add(
                  obj
                      [ "id", s "rec-type"
                        "displayName", s "Rec"
                        "typeParameters", JsonArray(typeParameter "rec-t" "T")
                        "kind", s "product"
                        "fields",
                        JsonArray(
                            field "rec-irregular" (self [ int64 () ]),
                            field "rec-regular" (self [ obj [ "kind", s "typeParameter"; "typeParameterId", s "rec-t" ] ]),
                            field "rec-nested" (obj [ "kind", s "builtinGeneric"; "generic", s "option"; "arguments", JsonArray(self [ self [ int64 () ] ]) ])) ])
              true)
          "obligations on a direct call",
          (fun root ->
              addObligations root (fun owner call argument ->
                  let callId = string (call["id"])
                  let parameter = string (argument["targetParameterId"])
                  let value = string (argument["valueNodeId"])
                  let outside = nodeOutside root owner |> Option.map (fun n -> string (n["id"])) |> Option.defaultValue "missing-node"
                  let other = ZigModelReaderSupport.graphNodes owner |> List.tryFind (fun n -> string (n["kind"]) <> "directCall") |> Option.map (fun n -> string (n["id"])) |> Option.defaultValue "missing-node"
                  [ obligation "ob-open-1" callId parameter None
                    obligation "ob-open-2" callId parameter None
                    obligation "ob-resolved" callId parameter (Some value)
                    obligation "ob-resolved-elsewhere" callId parameter (Some outside)
                    obligation "ob-foreign-parameter" callId "missing-parameter" None
                    obligation "ob-not-a-call" other parameter None
                    obligation "ob-unknown-node" "missing-node" parameter None
                    obligation "ob-unknown-node" "another-missing-node" parameter None ])) ]

module ZigValidatorTests =
    [<Fact>]
    let ``validation_codes.txt names validator codes`` () =
        let source = File.ReadAllText(Path.Combine(ZigModelReaderSupport.root (), "src", "Nyrwvy.Semantics", "Validation.fs"))
        for code in ZigValidatorSupport.codes () do
            Assert.True(source.Contains $"\"{code}\"", $"{code} is not a code of Validation.fs")

/// The extended comparison (a class, so that it can report its counts).
type ZigValidatorComparison(output: ITestOutputHelper) =
    let kindNames = lazy (File.ReadAllLines(ZigModelReaderSupport.toolFile "model_kinds.txt") |> Array.map (fun line -> line.Split('|')[0]))

    [<Fact; Trait("Suite", "Extended")>]
    member _.``The Zig validator matches the F# validator on the ported codes`` () =
        let root = ZigModelReaderSupport.root ()
        let codes = ZigValidatorSupport.codes ()
        let allKinds = ZigModelReaderSupport.allKindsJson ()
        let fixtures =
            Directory.GetFiles(Path.Combine(root, "tests", "Nyrwvy.Core.Tests", "fixtures", "record-format"), "*.json")
            |> Array.map File.ReadAllText |> List.ofArray
        let repository =
            Directory.GetFiles(Path.Combine(root, "examples"), "*.nyrec", SearchOption.AllDirectories)
            |> Array.map (File.ReadAllText >> RecordCodec.toJson >> ZigModelReaderSupport.unwrap) |> List.ofArray
        let originals = allKinds @ fixtures @ repository
        let apply (edits: (JsonObject -> bool) list) (json: string) =
            let document = JsonNode.Parse json :?> JsonObject
            let changed = edits |> List.fold (fun changed edit -> edit document || changed) false
            if changed then Some(document.ToJsonString()) else None
        let targeted = [ for json in originals do for _, edit in ZigValidatorSupport.semanticEdits do yield! Option.toList (apply [ edit ] json) ]
        // Two or three random edits together, so rules meet each other's damage.
        let combined =
            originals |> List.mapi (fun index json ->
                let random = Random(1000 + index)
                [ for _ in 1 .. 6 do
                      let edits = [ for _ in 1 .. random.Next(2, 4) -> snd ZigValidatorSupport.semanticEdits[random.Next ZigValidatorSupport.semanticEdits.Length] ]
                      yield! Option.toList (apply edits json) ])
            |> List.concat
        // The slice-2 shape mutations as a sanity subset: shape failures pass through unchanged.
        let shape = originals |> List.mapi (fun index json -> let random = Random index in [ for _ in 1 .. 2 -> ZigModelReaderSupport.mutate kindNames.Value random json ]) |> List.concat
        let texts = originals @ targeted @ combined @ shape |> List.distinct
        let reader = PureRun.zigTool "model-reader" |> ZigModelReaderSupport.unwrap
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-validator-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            let info = ProcessStartInfo(reader, RedirectStandardOutput = true)
            info.ArgumentList.Add "--validate"
            texts |> List.iteri (fun index text ->
                File.WriteAllText(Path.Combine(dir, $"{index}.in"), text, Text.UTF8Encoding false)
                info.ArgumentList.Add(Path.Combine(dir, $"{index}.in")))
            use child = Process.Start info
            let lines = child.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            child.WaitForExit()
            Assert.Equal(0, child.ExitCode)
            // One result per input: a single reader line, or issue lines closed by `end`.
            let results =
                let results = ResizeArray<string list>()
                let mutable pending = []
                for line in lines do
                    if line.StartsWith "issue " then pending <- line :: pending
                    elif line = "end" then results.Add(List.rev (line :: pending)); pending <- []
                    else
                        Assert.True(pending.IsEmpty, $"a reader line inside an issue list: {line}")
                        results.Add [ line ]
                results
            Assert.Equal(texts.Length, results.Count)
            let caseOnly = [ "error InvalidJson"; "error InvalidShape: Invalid JSON string encoding:" ]
            let mutable clean, failing, rejected, skipped, throws = 0, 0, 0, 0, 0
            let produced = Collections.Generic.Dictionary<string, int>()
            texts |> List.iteri (fun index text ->
                let actual = results[index]
                if actual.Head.StartsWith "skip legacy schema " then skipped <- skipped + 1
                else
                    match SnapshotCodec.readUnvalidated text with
                    | Error error ->
                        rejected <- rejected + 1
                        let line =
                            ZigValidatorSupport.escape <|
                            match error with
                            | SnapshotCodecError.InvalidJson message -> $"error InvalidJson: {message}"
                            | SnapshotCodecError.InvalidShape message -> $"error InvalidShape: {message}"
                            | SnapshotCodecError.UnsupportedSchemaVersion version -> $"error UnsupportedSchemaVersion: {version}"
                            | SnapshotCodecError.MaximumDepthExceeded depth -> $"error MaximumDepthExceeded: {depth}"
                            | SnapshotCodecError.InvalidSnapshot issues -> failwithf "readUnvalidated validated: %A" issues
                        match caseOnly |> List.tryFind line.StartsWith with
                        | Some prefix -> Assert.True(actual.Head.StartsWith prefix, $"case {index}: F# says {line}, Zig says {actual.Head}\n{text}")
                        | None -> Assert.True([ line ] = actual, $"case {index}: F# says {line}, Zig says %A{actual}\n{text}")
                    | Ok snapshot ->
                        let issues =
                            // Excluded analyses (type inference, effect order, …) run too and may throw on a mutated model.
                            try Some(SnapshotValidator.validate snapshot)
                            with error ->
                                output.WriteLine($"case {index}: the F# validator threw {error.GetType().Name}: {error.Message}")
                                None
                        match issues with
                        | None -> throws <- throws + 1
                        | Some issues ->
                            let ported = issues |> List.filter (fun issue -> codes.Contains issue.Code)
                            if ported.IsEmpty then clean <- clean + 1 else failing <- failing + 1
                            for issue in ported do produced[issue.Code] <- (match produced.TryGetValue issue.Code with | true, n -> n + 1 | _ -> 1)
                            let expected = [ for issue in ported -> ZigValidatorSupport.escape $"issue {issue.Code}: {issue.Message}" ] @ [ "end" ]
                            let show (lines: string list) = String.concat "\n" lines
                            Assert.True((expected = actual), $"case {index}: F# says\n{show expected}\nZig says\n{show actual}\n{text}"))
            output.WriteLine($"{texts.Length} texts ({originals.Length} originals, {targeted.Length} targeted, {combined.Length} combined, {shape.Length} shape): {clean} clean, {failing} with ported issues, {rejected} shape rejections, {skipped} legacy skipped, {throws} F# validator failures")
            for KeyValue(code, count) in produced |> Seq.sortBy (fun pair -> pair.Key) do output.WriteLine($"{count,6} {code}")
            let missing = codes - set produced.Keys
            Assert.True(missing.IsEmpty, "codes the corpus never produces: " + String.Join(", ", missing))
            Assert.True(clean > 20 && failing > 100 && rejected > 20, $"clean {clean}, failing {failing}, rejected {rejected}")
            Assert.True(throws * 20 < texts.Length, $"the F# validator threw on {throws} of {texts.Length} texts")
        finally Directory.Delete(dir, true)
