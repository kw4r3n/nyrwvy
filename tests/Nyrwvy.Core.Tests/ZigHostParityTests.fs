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

/// The Zig session host (tools/zig/host, docs/decisions/zig-session-host.md) against the F# host:
/// each transcript in tests/host-parity is one request per line, fed to both on the same snapshots,
/// and every response line must be the same bytes; the startups that fail must print the same
/// line and exit code. Two parts are host-specific: .NET's JSON parser text after "Invalid JSON:"
/// and "not valid JSON:" (compared by that prefix) and `status.build` (each host's own build).
module ZigHostParityTests =
    let private unwrap = function Ok x -> x | Error e -> failwithf "%A" e

    let private comparable (line: string) =
        let masked = Regex.Replace(line, "\"build\":\"[^\"]*\"", "\"build\":\"…\"")
        Regex.Replace(masked, "(Invalid JSON: |not valid JSON: ).*", "$1…")

    let private zigHost () = ZigTools.tool "host" |> Result.mapError (fun (code, message) -> $"{code}: {message}") |> unwrap
    let private fsharpHost = Path.Combine(AppContext.BaseDirectory, "Nyrwvy.AuthoringSession.dll")

    /// Starts a host on `arguments`; `requests` are written and its input closed unless `hold`.
    let private start (zig: bool) (arguments: string list) =
        let info = if zig then ProcessStartInfo(zigHost (), arguments) else ProcessStartInfo("dotnet", fsharpHost :: arguments)
        info.RedirectStandardInput <- true
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.Environment["NYRWVY_RUNPURE_WARMUP"] <- "0"
        Process.Start info

    /// Exit code, stderr and stdout lines of a host run to the end of `requests`.
    let private run zig arguments (requests: string list) =
        use host = start zig arguments
        // A host that fails to start may exit before reading its input.
        try
            for request in requests do host.StandardInput.Write(request + "\n")
            host.StandardInput.Close()
        with :? IOException -> ()
        let output = host.StandardOutput.ReadToEndAsync()
        let errors = host.StandardError.ReadToEnd()
        host.WaitForExit()
        host.ExitCode, errors.Trim(), output.Result.Split('\n') |> Array.toList |> List.filter ((<>) "")

    let private snapshots (dir: string) =
        let root = ZigModelReaderSupport.root ()
        let records = Directory.GetFiles(Path.Combine(root, "examples"), "*.nyrec", SearchOption.AllDirectories) |> Array.toList
        let json = Path.Combine(dir, "task-outcomes.json")
        File.WriteAllText(json, File.ReadAllText records.Head |> RecordCodec.deserialize |> unwrap |> SnapshotCodec.serialize |> unwrap)
        records @ [ json ]

    let private withDirectory f =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-host-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try f dir finally Directory.Delete(dir, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host answers every transcript as the F# host does`` () =
        withDirectory (fun dir ->
            let transcripts = Directory.GetFiles(Path.Combine(ZigModelReaderSupport.root (), "tests", "host-parity"), "*.jsonl")
            Assert.NotEmpty transcripts
            for snapshot in snapshots dir do
                use session = AuthoringSession.openReadOnly snapshot |> unwrap
                for transcript in transcripts do
                    let requests = File.ReadAllText(transcript).Split('\n') |> Array.toList |> fun lines -> List.take (lines.Length - 1) lines
                    let fsharp = requests |> List.map (AuthoringProtocol.executeLine session)
                    let code, errors, answers = run true [ "open"; "--snapshot"; snapshot; "--read-only" ] requests
                    Assert.True((code = 0), errors)
                    Assert.Equal(requests.Length, answers.Length)
                    List.zip3 requests fsharp answers |> List.iteri (fun index (request, expected, actual) ->
                        if comparable expected <> comparable actual then
                            failwithf "%s on %s line %d: %s\n F#:  %s\n Zig: %s" (Path.GetFileName transcript) (Path.GetFileName snapshot) (index + 1) request expected actual))

    /// `references`, `search` and `unreferenced` requests built from a session's own declarations:
    /// every declaration and member by exact ID and by display names (as a string and as a
    /// selector object), and searches for parts of their names.
    let private queries (session: AuthoringSession) =
        let declarations = session.Declarations()
        let quote (text: string) = JsonSerializer.Serialize text
        let id = StableId.value
        let byName kind (key: string) name =
            [ $"""{{"id":"r","op":"references","kind":"{kind}","{key}":{quote name}}}"""
              $"""{{"id":"r","op":"references","kind":"{kind}","{key}":{{"name":{quote name}}}}}""" ]
        let named kind exactKey (exact: string) (name: string) =
            $"""{{"id":"r","op":"references","kind":"{kind}","{exactKey}":{quote exact}}}""" :: byName kind kind name
        let parts (name: string) = [ name; name.ToUpperInvariant(); (if name.Length > 2 then name.Substring(1, 2) else name) ]
        [ for item in declarations.Functions do
              yield! named "function" "functionId" (id item.Id) item.DisplayName
              yield! parts item.DisplayName |> List.map (fun text -> $"""{{"id":"q","op":"search","text":{quote text},"limit":3}}""")
          for item in declarations.Types do
              yield! named "type" "typeId" (id item.Id) item.DisplayName
              match item.Kind with
              | SemanticTypeKind.Product fields ->
                  for field in fields do
                      yield $"""{{"id":"r","op":"references","kind":"field","fieldId":{quote (id field.Id)}}}"""
                      yield $"""{{"id":"r","op":"references","kind":"field","type":{quote item.DisplayName},"field":{quote field.DisplayName}}}"""
                  yield $"""{{"id":"r","op":"references","kind":"variant","type":{quote item.DisplayName},"variant":"x"}}"""
              | SemanticTypeKind.Union variants ->
                  for variant in variants do
                      yield $"""{{"id":"r","op":"references","kind":"variant","variantId":{quote (id variant.Id)}}}"""
                      yield $"""{{"id":"r","op":"references","kind":"variant","type":{{"id":{quote (id item.Id)}}},"variant":{quote variant.DisplayName}}}"""
                      for field in variant.Fields do
                          yield $"""{{"id":"r","op":"references","kind":"field","fieldId":{quote (id field.Id)}}}"""
                          yield $"""{{"id":"r","op":"references","kind":"field","type":{quote item.DisplayName},"variant":{quote variant.DisplayName},"field":{quote field.DisplayName}}}"""
                  yield $"""{{"id":"r","op":"references","kind":"field","type":{quote item.DisplayName},"field":"x"}}"""
          for item in declarations.Effects do yield! named "effect" "effectId" (id item.Id) item.DisplayName
          for item in declarations.Capabilities do
              yield! named "capability" "capabilityId" (id item.Id) item.DisplayName
              for operation in item.Operations do
                  yield $"""{{"id":"r","op":"references","kind":"capabilityOperation","operationId":{quote (id operation.Id)}}}"""
                  yield $"""{{"id":"r","op":"references","kind":"capabilityOperation","capability":{quote item.DisplayName},"operation":{quote operation.DisplayName}}}"""
                  yield! parts operation.DisplayName |> List.map (fun text -> $"""{{"id":"q","op":"search","text":{quote text}}}""")
          for item in declarations.Provenance do
              for ownership in [ "local"; "imported" ] do
                  yield $"""{{"id":"r","op":"references","kind":"{item.Kind}","{item.Kind}":{{"id":{quote item.Id},"ownership":"{ownership}"}}}}"""
              for source in item.Sources do
                  yield $"""{{"id":"r","op":"references","kind":"{item.Kind}","{item.Kind}":{{"id":{quote item.Id},"dependencySnapshotId":{quote source.SnapshotId}}}}}"""
          for item in declarations.Functions do
              for node in ValueGraph.collectNodes item.Body |> List.truncate 60 do
                  yield $"""{{"id":"b","op":"bindingScopes","functionId":{quote (id item.Id)},"nodeId":{quote (id node.Id)}}}"""
              yield $"""{{"id":"b","op":"bindingScopes","function":{quote item.DisplayName},"nodeId":{quote (id item.Body.ResultNodeId)}}}"""
              yield $"""{{"id":"b","op":"bindingScopes","function":{quote item.DisplayName},"nodeId":"missing"}}"""
              // Bytes copy plans (`copiedUpdates`) come with the Execution IR in the next step.
              let updatesBytes =
                  ValueGraph.collectNodes item.Body |> List.exists (fun node ->
                      match node.Kind with
                      | SemanticNodeKind.BytesSet _ | SemanticNodeKind.BytesAppend _ -> true
                      | _ -> false)
              if not updatesBytes then
                  yield $"""{{"id":"f","op":"readFunction","functionId":{quote (id item.Id)},"detail":"compact"}}"""
                  yield $"""{{"id":"f","op":"readFunction","function":{quote item.DisplayName},"detail":"compact"}}"""
                  yield $"""{{"id":"f","op":"readFunction","functionId":{quote (id item.Id)},"detail":"outline"}}"""
                  yield $"""{{"id":"f","op":"readFunction","functionId":{quote (id item.Id)}}}"""
          yield """{"id":"u","op":"unreferenced"}"""
          for item in declarations.Functions |> List.truncate 3 do
              yield $"""{{"id":"u","op":"unreferenced","roots":[{quote item.DisplayName}]}}""" ]

    let private sameAnswers label (fsharp: string list) (zig: string list) (requests: string list) =
        Assert.Equal(requests.Length, zig.Length)
        List.zip3 requests fsharp zig |> List.iteri (fun index (request, expected, actual) ->
            if comparable expected <> comparable actual then
                failwithf "%s line %d: %s\n F#:  %s\n Zig: %s" label (index + 1) request expected actual)

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host answers queries built from each snapshot as the F# host does`` () =
        withDirectory (fun dir ->
            // The examples lack some constructs (aborts, waits, open obligations); the all-kinds snapshots have them.
            let constructed =
                let fixture = MilestoneZeroGraph.create ()
                // An open obligation: a parameter added with the call site left missing.
                let incomplete =
                    Transaction.simulate fixture.Snapshot
                        { Id = StableId.createForTesting<TransactionTag> "zig-host-add-policy"
                          BaseSnapshotId = fixture.Snapshot.Id
                          ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "zig-host-incomplete"
                          Operations =
                            [ AddFunctionParameter
                                { TargetFunctionId = fixture.ValidateFunctionId
                                  NewParameterId = StableId.createForTesting<ParameterTag> "zig-host-policy"
                                  NewParameterDisplayName = "policy"
                                  NewParameterType = TypeRef.Named(fixture.PolicyTypeId, [])
                                  CallSitePlans = [ LeaveMissing(fixture.MainValidateCallNodeId, StableId.createForTesting<ObligationTag> "zig-host-obligation") ] } ] }
                    |> function Proposed simulation -> simulation.ProposedSnapshot | Rejected diagnostics -> failwithf "%A" diagnostics
                // Literals longer than the summaries (256 units) and the outline (64 units) show, with a
                // surrogate pair at each cut, and a body whose outline passes its byte budget.
                let long =
                    let fn name resultType (nodes: SemanticNode list) =
                        { MustNotWait = false; Exported = true; Id = StableId.createForTesting<FunctionTag> ("zig-host-" + name); DisplayName = name
                          TypeParameters = []; Parameters = []; ResultType = TypeRef.Primitive resultType; RequiredEffects = []
                          Body = { Nodes = nodes; ResultNodeId = (List.last nodes).Id } }
                    let literal name text = fn name PrimitiveType.String [ { Id = StableId.createForTesting<GraphNodeTag> ("zig-host-" + name); Kind = SemanticNodeKind.StringLiteral text } ]
                    let node index = StableId.createForTesting<GraphNodeTag> $"zig-host-wide-{index:D3}"
                    let wide =
                        fn "wide" PrimitiveType.Bool
                            [ yield { Id = node 0; Kind = SemanticNodeKind.BoolLiteral true }
                              for index in 1 .. 400 -> { Id = node index; Kind = SemanticNodeKind.BoolNot(node (index - 1)) } ]
                    { Snapshot.empty (StableId.createForTesting<SnapshotTag> "zig-host-long") with
                        Functions =
                          [ literal "long" ("long" + String.replicate 251 "a" + "😀" + String.replicate 200 "b")
                            literal "split" (String.replicate 63 "y" + "😀z")
                            wide ] }
                RecordCodecAllKindsTests.allKindSnapshots () @ [ fixture.Snapshot; incomplete; long ]
                |> List.mapi (fun index snapshot ->
                    let path = Path.Combine(dir, $"kinds-{index}.json")
                    File.WriteAllText(path, SnapshotCodec.serialize snapshot |> unwrap)
                    path)
            let answered = Text.StringBuilder()
            for snapshot in snapshots dir @ constructed do
                use session = AuthoringSession.openReadOnly snapshot |> unwrap
                let requests = queries session
                let code, errors, answers = run true [ "open"; "--snapshot"; snapshot; "--read-only" ] requests
                Assert.True((code = 0), errors)
                let fsharp = requests |> List.map (AuthoringProtocol.executeLine session)
                fsharp |> List.iter (answered.Append >> ignore)
                sameAnswers (Path.GetFileName snapshot) fsharp answers requests
            for shown in [ "\"aborts\":[{"; "\"mayWait\":true"; "\"openObligations\":[{"; "\"frames\":[{"; "\"variants\":["; "\"slot\":\"field\""; "\"evidence\":[{"; "\"references\":[{"; "\"role\":\"patternBinding\""; "\"truncated\":true"; "\"outlineTruncated\":true"; "scopeDepth=1"; "\\\\uD83D\\\\u2026"; "ref "; "\"nodeTypes\":[{" ] do
                Assert.True(answered.ToString().Contains shown, $"No F# reply shows {shown}; the parity test does not cover it.")
            // `full` replies show every node kind's structured form, but for the Bytes updates, whose functions wait for `copiedUpdates`.
            for row in File.ReadAllLines(Path.Combine(ZigModelReaderSupport.root (), "tools", "zig", "host", "graph_kinds.txt")) do
                if not (row.StartsWith "#" || row.StartsWith "bytesSet|" || row.StartsWith "bytesAppend|") then
                    let shown = $"\"kind\":{{\"kind\":\"{row.Split('|')[1]}\""
                    Assert.True(answered.ToString().Contains shown, $"No F# reply shows {shown}; the parity test does not cover it."))

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host fails to start as the F# host does`` () =
        withDirectory (fun dir ->
            let record = (snapshots dir).Head
            let json = File.ReadAllText(Path.Combine(dir, "task-outcomes.json"))
            let edited (change: JsonObject -> unit) =
                let document = JsonNode.Parse(json).AsObject()
                change document
                document.ToJsonString()
            let firstFunction (document: JsonObject) = (document["functions"].AsArray()[0]).AsObject()
            let lines = File.ReadAllLines record
            let joined (items: string array) = String.Join("\n", items) + "\n"
            let bound = joined (Array.append [| lines[0] + " dependency=\"x\" digest=00" |] lines[1..])
            let orphan = joined lines + "field field:01a00000-0000-7000-8000-000000000000 owner=type:01a00000-0000-7000-8000-000000000001\n"
            let truncated = joined lines[.. lines.Length - 2]
            let cases =
                [ "missing.json", None
                  "syntax.json", Some "{oops"
                  "shape.json", Some """{"schemaVersion":24}"""
                  "version.json", Some(edited (fun d -> d["schemaVersion"] <- JsonValue.Create 99))
                  "invalid.json", Some(edited (fun d -> (firstFunction d)["resultType"] <- JsonNode.Parse """{"kind":"primitive","primitive":"unit"}"""))
                  "bound.json", Some(edited (fun d -> d["dependency"] <- JsonNode.Parse """{"snapshotId":"x","digest":"00"}"""))
                  "bound.nyrec", Some bound
                  "orphan.nyrec", Some orphan
                  "truncated.nyrec", Some truncated ]
            for name, content in cases do
                let path = Path.Combine(dir, name)
                content |> Option.iter (fun text -> File.WriteAllText(path, text))
                for readOnly in [ true; false ] do
                    let arguments = [ "open"; "--snapshot"; path ] @ (if readOnly then [ "--read-only" ] else [])
                    let fsharp, zig = run false arguments [], run true arguments []
                    let shown (code, errors, _) = $"{code} {comparable errors}"
                    Assert.True(shown fsharp = shown zig, $"{name}:\n F#:  {shown fsharp}\n Zig: {shown zig}")
                    let code, _, _ = fsharp
                    Assert.True((code = 2), $"{name}: the F# host started")
            // A legacy schema (9 to 20) reads only with F#; the Zig host names the migration instead.
            let legacy = Path.Combine(dir, "legacy.json")
            File.WriteAllText(legacy, SnapshotCodec.serializeCanonical (RecordCodec.deserialize (File.ReadAllText record) |> unwrap) |> unwrap)
            let code, errors, _ = run true [ "open"; "--snapshot"; legacy; "--read-only" ] []
            Assert.Equal(2, code)
            Assert.Contains("is a legacy schema, which the Zig host does not read", errors))

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig and F# hosts lock each other out and sweep temporaries`` () =
        withDirectory (fun dir ->
            let snapshot = Path.Combine(dir, "locked.json")
            File.Copy(Path.Combine(dir, (snapshots dir |> List.last)), snapshot)
            let stray = Path.Combine(dir, ".locked.json.0123456789abcdef0123456789abcdef.tmp")
            let foreign = Path.Combine(dir, ".locked.json.not-a-temporary.tmp")
            for file in [ stray; foreign ] do File.WriteAllText(file, "")
            for holder in [ false; true ] do
                use held = start holder [ "open"; "--snapshot"; snapshot ]
                held.StandardInput.WriteLine """{"id":"s","op":"status"}"""
                Assert.Contains("\"ok\":true", held.StandardOutput.ReadLine())
                let code, errors, _ = run (not holder) [ "open"; "--snapshot"; snapshot ] []
                Assert.Equal(2, code)
                Assert.Equal($"LockUnavailable: {snapshot} is open in another authoring session. Close that session first.", errors)
                // Read-only takes no lock.
                let code, _, _ = run (not holder) [ "open"; "--snapshot"; snapshot; "--read-only" ] [ """{"id":"s","op":"status"}""" ]
                Assert.Equal(0, code)
                held.StandardInput.Close()
                held.WaitForExit()
                if holder then
                    Assert.False(File.Exists stray, "The Zig host left a temporary of an interrupted write.")
                    Assert.True(File.Exists foreign, "The Zig host removed a file outside the temporary pattern.")
                File.WriteAllText(stray, ""))

    /// One sidecar member, as `PinnedDependency.embed` writes it, or by path for a legacy selection.
    let private memberOf (dependency: Snapshot) (content: (string * string) option) (path: string) =
        let fingerprint = SnapshotFingerprint.compute dependency |> unwrap |> SnapshotFingerprint.value
        let item = JsonObject()
        item["fingerprint"] <- JsonValue.Create fingerprint
        item["path"] <- JsonValue.Create path
        item["snapshotId"] <- JsonValue.Create(StableId.value dependency.Id)
        content |> Option.iter (fun (key, text) -> item[key] <- JsonValue.Create text)
        item

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host opens resolved snapshots as the F# host does`` () =
        withDirectory (fun dir ->
            // Record text needs UUIDs.
            let names = Collections.Generic.Dictionary<string, SnapshotId>()
            let dependencyId name =
                match names.TryGetValue name with
                | true, id -> id
                | _ ->
                    let id = StableId.tryCreate<SnapshotTag> $"01a20000-0000-7000-8000-{names.Count:D12}" |> unwrap
                    names[name] <- id
                    id
            let asDependency name (snapshot: Snapshot) = { snapshot with Id = dependencyId name; ParentId = None; Obligations = [] }
            let originals =
                Directory.GetFiles(Path.Combine(ZigModelReaderSupport.root (), "examples"), "*.nyrec", SearchOption.AllDirectories)
                |> Array.toList |> List.map (File.ReadAllText >> RecordCodec.deserialize >> unwrap)
            let mutable cases = 0
            let outcomes = Collections.Generic.HashSet<string>()
            let check (name: string) (write: string -> unit) =
                let path = Path.Combine(dir, name)
                write path
                for readOnly in [ true; false ] do
                    let arguments = [ "open"; "--snapshot"; path ] @ (if readOnly then [ "--read-only" ] else [])
                    let shown (code, errors, (lines: string list)) = $"{code} {comparable errors} {comparable (String.Join(' ', lines))}"
                    let request = [ """{"id":"s","op":"status"}"""; """{"id":"d","op":"declarations"}"""; """{"id":"e","op":"dependency"}""" ]
                    let opened = match AuthoringSession.openReadOnly path with | Ok session -> Some session | Error _ -> None
                    let request = request @ (opened |> Option.map queries |> Option.defaultValue [])
                    opened |> Option.iter (fun session -> (session :> IDisposable).Dispose())
                    let fsharpRun = run false arguments request
                    let fsharp, zig = shown fsharpRun, shown (run true arguments request)
                    Assert.True((fsharp = zig), $"{name}:\n F#:  {fsharp}\n Zig: {zig}")
                    // The accepted forms open; every other case fails to start.
                    let opens = [ "embedded-"; "by-path-"; "set-" ] |> List.exists name.StartsWith && not (name.StartsWith "set-conflict")
                    let code, errors, _ = fsharpRun
                    Assert.True((code = (if opens then 0 else 2)), $"{name}: exit {code} {errors}")
                    if not opens then outcomes.Add(Regex.Replace(name, "-[0-9]+\\..*", "") + ": " + errors.Substring(0, min 60 errors.Length)) |> ignore
                cases <- cases + 1
            let sidecar (path: string) (members: JsonObject list) =
                let manifest = match members with [ single ] -> single | _ -> JsonObject(dict [ "dependencies", JsonArray(members |> List.map (fun m -> m :> JsonNode) |> List.toArray) :> JsonNode ])
                File.WriteAllText(path + ".dependency.json", manifest.ToJsonString())
            for index, original in List.indexed originals do
                let dependency = asDependency $"single-{index}" { original with Functions = [] }
                let local = { original with Types = []; Effects = []; Capabilities = [] }
                let json, records = SnapshotCodec.serializeResolved dependency local |> unwrap, RecordCodec.serializeResolved dependency local |> unwrap
                let embedded = memberOf dependency (Some("snapshotJson", SnapshotCodec.serialize dependency |> unwrap)) "/provenance/only.json"
                let embeddedRecords = memberOf dependency (Some("snapshotRecords", RecordCodec.serialize dependency |> unwrap)) "/provenance/only.nyrec"
                let accepted (name: string) (text: string) members =
                    check name (fun path -> File.WriteAllText(path, text); sidecar path members)
                accepted $"embedded-{index}.json" json [ embedded ]
                accepted $"embedded-{index}.nyrec" records [ embeddedRecords ]
                // A legacy selection names the dependency's file.
                let source = Path.Combine(dir, $"library-{index}.json")
                File.WriteAllText(source, SnapshotCodec.serialize dependency |> unwrap)
                accepted $"by-path-{index}.json" json [ memberOf dependency None source ]
                // A dependency set: the dependency and a library of one type, each valid alone; and a
                // copy of the dependency with one type renamed, which collides.
                let first = asDependency $"set-a-{index}" dependency
                let extra =
                    { Snapshot.empty (dependencyId $"set-b-{index}") with
                        Types =
                            [ { Id = StableId.createForTesting<TypeTag> $"zig-host-extra-{index}"; DisplayName = "Extra"; Exported = true; TypeParameters = []
                                Kind = SemanticTypeKind.Product [ { Id = StableId.createForTesting<FieldTag> $"zig-host-extra-value-{index}"; DisplayName = "value"; Type = TypeRef.Primitive PrimitiveType.Int64 } ] } ] }
                let embed (item: Snapshot) = memberOf item (Some("snapshotJson", SnapshotCodec.serialize item |> unwrap)) "/provenance/set.json"
                let combined = DependencyGraph.combine [ first; extra ] |> unwrap
                accepted $"set-{index}.json" (SnapshotCodec.serializeResolved combined local |> unwrap) [ embed first; embed extra ]
                match first.Types with
                | head :: rest ->
                    let renamed = asDependency $"set-c-{index}" { first with Types = { head with DisplayName = head.DisplayName + "Other" } :: rest }
                    accepted $"set-conflict-{index}.json" (SnapshotCodec.serializeResolved combined local |> unwrap) [ embed first; embed renamed ]
                | [] -> ()
                // A local document at an older version than a construct its dependency uses (Bytes, 23).
                let bytes =
                    { Id = StableId.createForTesting<TypeTag> $"zig-host-bytes-{index}"; DisplayName = "Raw"; Exported = true; TypeParameters = []
                      Kind = SemanticTypeKind.Product [ { Id = StableId.createForTesting<FieldTag> $"zig-host-bytes-value-{index}"; DisplayName = "data"; Type = TypeRef.Primitive PrimitiveType.Bytes } ] }
                let withBytes = asDependency $"bytes-{index}" { dependency with Types = bytes :: dependency.Types }
                let older = (SnapshotCodec.serializeResolved withBytes local |> unwrap).Replace("\"schemaVersion\":24", "\"schemaVersion\":21")
                accepted $"embedded-older-{index}.json" older [ embed withBytes ]
                // Failures: the manifest, its fields and its content, the marker, and the recovery hints.
                let edited (change: JsonObject -> unit) = let copy = JsonNode.Parse(embedded.ToJsonString()).AsObject() in change copy; copy
                check $"duplicate-{index}.json" (fun path -> File.WriteAllText(path, json); File.WriteAllText(path + ".dependency.json", "{\"path\":\"/x\"," + embedded.ToJsonString().Substring 1))
                check $"syntax-{index}.json" (fun path -> File.WriteAllText(path, json); File.WriteAllText(path + ".dependency.json", "{oops"))
                accepted $"fields-{index}.json" json [ edited (fun m -> m.Remove "path" |> ignore) ]
                accepted $"blank-{index}.json" json [ edited (fun m -> m["fingerprint"] <- JsonValue.Create " ") ]
                accepted $"number-{index}.json" json [ edited (fun m -> m["path"] <- JsonValue.Create 1) ]
                accepted $"empty-{index}.json" json [ edited (fun m -> m["snapshotJson"] <- JsonValue.Create "") ]
                accepted $"changed-{index}.json" json [ edited (fun m -> m["fingerprint"] <- JsonValue.Create "sha256:00") ]
                accepted $"unavailable-{index}.json" json [ memberOf dependency None (Path.Combine(dir, "absent.json")) ]
                accepted $"lone-set-{index}.json" json [ JsonObject(dict [ "dependencies", JsonArray([| embedded.DeepClone() |]) :> JsonNode ]) ]
                accepted $"bare-set-{index}.json" json [ memberOf dependency None source; memberOf dependency None source ]
                accepted $"marker-{index}.json" (Regex.Replace(json, "\"digest\":\"(.)", fun m -> "\"digest\":\"" + (if m.Groups[1].Value = "0" then "1" else "0"))) [ embedded ]
                // An interrupted replacement (the previous sidecar matches) and an interrupted first addition.
                check $"previous-{index}.json" (fun path ->
                    File.WriteAllText(path, json)
                    File.WriteAllText(path + ".dependency.json", (edited (fun m -> m["fingerprint"] <- JsonValue.Create "sha256:00")).ToJsonString())
                    File.WriteAllText(path + ".dependency.json.previous", embedded.ToJsonString()))
                check $"standalone-{index}.json" (fun path ->
                    File.WriteAllText(path, SnapshotCodec.serialize original |> unwrap)
                    sidecar path [ embedded ])
            Assert.True(cases > 40, $"{cases} cases")
            Assert.True(outcomes.Count >= 12 && outcomes |> Seq.exists (fun line -> line.Contains "Dependencies declare"), String.Join("\n", outcomes)))
