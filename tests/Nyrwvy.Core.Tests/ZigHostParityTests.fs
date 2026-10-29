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
    let internal unwrap = function Ok x -> x | Error e -> failwithf "%A" e

    let internal comparable (line: string) =
        let masked = Regex.Replace(line, "\"build\":\"[^\"]*\"", "\"build\":\"…\"")
        Regex.Replace(masked, "(Invalid JSON: |not valid JSON: ).*", "$1…")

    let internal zigHost () = ZigTools.tool "host" |> Result.mapError (fun (code, message) -> $"{code}: {message}") |> unwrap
    let internal fsharpHost = Path.Combine(AppContext.BaseDirectory, "Nyrwvy.AuthoringSession.dll")

    /// Starts a host on `arguments`; `requests` are written and its input closed unless `hold`.
    let internal start (zig: bool) (arguments: string list) =
        let info = if zig then ProcessStartInfo(zigHost (), arguments) else ProcessStartInfo("dotnet", fsharpHost :: arguments)
        info.RedirectStandardInput <- true
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.Environment["NYRWVY_RUNPURE_WARMUP"] <- "0"
        Process.Start info

    /// How long a host may go without answering before the run fails.
    let internal silence = TimeSpan.FromSeconds 60.0

    /// Exit code, stderr and stdout lines of a host run to the end of `requests`. Output is read
    /// while the requests are written, so a host blocked on a full stdout never deadlocks the run,
    /// and a host that stops answering for `silence` is killed and named.
    let internal run zig arguments (requests: string list) =
        use host = start zig arguments
        let lines = Collections.Concurrent.ConcurrentQueue<string>()
        let mutable last = DateTime.UtcNow
        let reading =
            Threading.Tasks.Task.Run(fun () ->
                let mutable line = host.StandardOutput.ReadLine()
                while not (isNull line) do
                    lines.Enqueue line
                    last <- DateTime.UtcNow
                    line <- host.StandardOutput.ReadLine())
        let errors = host.StandardError.ReadToEndAsync()
        // A host that fails to start may exit before reading its input.
        let writing =
            Threading.Tasks.Task.Run(fun () ->
                try
                    for request in requests do host.StandardInput.Write(request + "\n")
                    host.StandardInput.Close()
                with :? IOException -> ())
        while not (reading.Wait(TimeSpan.FromSeconds 1.0)) do
            if DateTime.UtcNow - last > silence then
                host.Kill true
                let answered = lines.Count
                let next = if answered < requests.Length then requests[answered] else "<none>"
                failwithf "The %s host answered nothing for %.0f s after %d of %d requests; the next one is: %s"
                    (if zig then "Zig" else "F#") silence.TotalSeconds answered requests.Length (next.Substring(0, min 300 next.Length))
        writing.Wait()
        host.WaitForExit()
        host.ExitCode, errors.Result.Trim(), lines |> Seq.filter ((<>) "") |> Seq.toList

    let internal snapshots (dir: string) =
        let root = ZigModelReaderSupport.root ()
        let records = Directory.GetFiles(Path.Combine(root, "examples"), "*.nyrec", SearchOption.AllDirectories) |> Array.toList
        let json = Path.Combine(dir, "task-outcomes.json")
        File.WriteAllText(json, File.ReadAllText records.Head |> RecordCodec.deserialize |> unwrap |> SnapshotCodec.serialize |> unwrap)
        records @ [ json ]

    let internal withDirectory f =
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
    let internal queries (session: AuthoringSession) =
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
              yield $"""{{"id":"f","op":"readFunction","functionId":{quote (id item.Id)},"detail":"compact"}}"""
              yield $"""{{"id":"f","op":"readFunction","function":{quote item.DisplayName},"detail":"compact"}}"""
              yield $"""{{"id":"f","op":"readFunction","functionId":{quote (id item.Id)},"detail":"outline"}}"""
              yield $"""{{"id":"f","op":"readFunction","functionId":{quote (id item.Id)}}}"""
          yield """{"id":"e","op":"executionReadiness"}"""
          yield """{"id":"e","op":"executionReadiness","extra":true}"""
          yield """{"id":"u","op":"unreferenced"}"""
          for item in declarations.Functions |> List.truncate 3 do
              yield $"""{{"id":"u","op":"unreferenced","roots":[{quote item.DisplayName}]}}""" ]

    let internal sameAnswers label (fsharp: string list) (zig: string list) (requests: string list) =
        Assert.Equal(requests.Length, zig.Length)
        List.zip3 requests fsharp zig |> List.iteri (fun index (request, expected, actual) ->
            if comparable expected <> comparable actual then
                failwithf "%s line %d: %s\n F#:  %s\n Zig: %s" label (index + 1) request expected actual)

    /// Snapshots with the constructs the examples lack (aborts, waits, open obligations, long
    /// literals, Bytes updates), written into `dir`.
    let internal constructedSnapshots (dir: string) =
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
                    { literal "split" (String.replicate 63 "y" + "😀z") with MustNotWait = true }
                    wide ] }
        // Bytes updates that write in place and that must copy (`copiedUpdates`).
        let ownership = Path.Combine(dir, "ownership.json")
        do
            use session = AuthoringSession.createNew ownership |> unwrap
            BytesTests.ownershipFunctions session |> ignore
            session.Close()
        (RecordCodecAllKindsTests.allKindSnapshots () @ [ fixture.Snapshot; incomplete; long ]
         |> List.mapi (fun index snapshot ->
             let path = Path.Combine(dir, $"kinds-{index}.json")
             File.WriteAllText(path, SnapshotCodec.serialize snapshot |> unwrap)
             path))
        @ [ ownership ]

    /// The seeded IDs a host writes under `NYRWVY_TEST_ID_SEED`, which the Zig host must allocate
    /// the same way: the `n`-th is a UUIDv7 at 1 700 000 000 000 ms + `n` with SHA-256 bits of `seed:n`.
    [<Fact>]
    let ``Seeded IDs are UUIDv7 values derived from the seed and the count`` () =
        Assert.Equal("018bcfe5-6801-7", (StableIdAllocator.seededPayload "parity" 1L).Substring(0, 15))
        Assert.Equal("018bcfe5-6802-7", (StableIdAllocator.seededPayload "parity" 2L).Substring(0, 15))
        Assert.Equal(StableIdAllocator.seededPayload "parity" 7L, StableIdAllocator.seededPayload "parity" 7L)
        Assert.NotEqual<string>(StableIdAllocator.seededPayload "parity" 7L, StableIdAllocator.seededPayload "other" 7L)
        for n in 1L .. 50L do
            let text = StableIdAllocator.seededPayload "parity" n
            Assert.Contains(text[19], "89ab")
            Assert.True(StableId.tryCreate<FunctionTag> text |> Result.isOk, text)
        // The guard counts only IDs the same seed derives, wherever they appear in a file's text.
        let text = String.concat " " [ "x"; StableIdAllocator.seededPayload "parity" 3L; StableIdAllocator.seededPayload "parity" 9L; StableIdAllocator.seededPayload "other" 40L; string (Guid.CreateVersion7()) ]
        Assert.Equal(9L, StableIdAllocator.seededCount "parity" text)
        Assert.Equal(40L, StableIdAllocator.seededCount "other" text)
        Assert.Equal(0L, StableIdAllocator.seededCount "parity" "no IDs")

    /// `ValueDependency.ofNode` as the Zig host reads it (tools/zig/host/dependency_roles.txt): per
    /// node kind, in dependency order, the snapshot property that holds each operand and its role.
    /// `name[]` stands for each item of an array: a node ID (`ListItem` and `FunctionArgument` with
    /// the item's index) or a binding whose `valueNodeId` is the operand (the role keyed by its target).
    /// Each kind is built with every optional part present and a fresh ID in every place, then read
    /// back from its serialized form.
    let dependencyRoleTable () =
        let mutable next = 0
        let fresh () =
            next <- next + 1
            $"zig-roles-{next:D4}"
        let flags = Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic
        let rec sample (t: Type) : obj =
            if t = typeof<string> then box (fresh ())
            elif t = typeof<int64> then box 0L
            elif t = typeof<int> then box 0
            elif t = typeof<uint64> then box 0UL
            elif t = typeof<bool> then box false
            elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<_>> then
                let item = t.GetGenericArguments()[0]
                let cons = t.GetMethod("Cons")
                let empty = t.GetProperty("Empty").GetValue(null)
                cons.Invoke(null, [| sample item; cons.Invoke(null, [| sample item; empty |]) |])
            elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>> then
                t.GetMethod("Some").Invoke(null, [| sample (t.GetGenericArguments()[0]) |])
            elif t = typeof<TypeRef> then box (TypeRef.Primitive PrimitiveType.Int64)
            elif t = typeof<ValueGraph> then box ({ Nodes = []; ResultNodeId = StableId.createForTesting<GraphNodeTag> (fresh ()) }: ValueGraph)
            elif Microsoft.FSharp.Reflection.FSharpType.IsTuple t then
                Microsoft.FSharp.Reflection.FSharpValue.MakeTuple(Microsoft.FSharp.Reflection.FSharpType.GetTupleElements t |> Array.map sample, t)
            elif Microsoft.FSharp.Reflection.FSharpType.IsRecord(t, flags) then
                Microsoft.FSharp.Reflection.FSharpValue.MakeRecord(t, Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(t, flags) |> Array.map (fun field -> sample field.PropertyType), flags)
            elif Microsoft.FSharp.Reflection.FSharpType.IsUnion(t, flags) then
                let case = Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(t, flags)[0]
                Microsoft.FSharp.Reflection.FSharpValue.MakeUnion(case, case.GetFields() |> Array.map (fun field -> sample field.PropertyType), flags)
            else failwithf "No sample for %s." t.FullName
        let rows =
            [ for case in Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<SemanticNodeKind>) do
                  let kind = Microsoft.FSharp.Reflection.FSharpValue.MakeUnion(case, case.GetFields() |> Array.map (fun field -> sample field.PropertyType)) :?> SemanticNodeKind
                  let node = { Id = StableId.createForTesting<GraphNodeTag> (fresh ()); Kind = kind }
                  let snapshot =
                      { Snapshot.empty (StableId.createForTesting<SnapshotTag> "zig-roles") with
                          Functions =
                            [ { MustNotWait = false; Exported = true; Id = StableId.createForTesting<FunctionTag> "zig-roles"; DisplayName = "roles"
                                TypeParameters = []; Parameters = []; ResultType = TypeRef.Primitive PrimitiveType.Int64; RequiredEffects = []
                                Body = { Nodes = [ node ]; ResultNodeId = node.Id } } ] }
                  use document = JsonDocument.Parse(SnapshotCodec.serializeUnvalidated snapshot |> Result.defaultWith (failwithf "%A"))
                  let json = (document.RootElement.GetProperty("functions")[0]).GetProperty("body").GetProperty("nodes")[0]
                  let entries =
                      ValueDependency.ofNode node
                      |> List.map (fun dependency ->
                          let wanted = StableId.value dependency.NodeId
                          let role = (Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(dependency.Role, typeof<ValueDependencyRole>) |> fst).Name
                          let places =
                              [ for property in json.EnumerateObject() do
                                    match property.Value.ValueKind with
                                    | JsonValueKind.String when property.Value.GetString() = wanted -> yield property.Name
                                    | JsonValueKind.Array ->
                                        for item in property.Value.EnumerateArray() do
                                            match item.ValueKind with
                                            | JsonValueKind.String when item.GetString() = wanted -> yield property.Name + "[]"
                                            | JsonValueKind.Object when (match item.TryGetProperty "valueNodeId" with | true, value -> value.GetString() = wanted | _ -> false) -> yield property.Name + "[]"
                                            | _ -> ()
                                    | _ -> () ]
                          match places with
                          | [ place ] -> place + ":" + role
                          | other -> failwithf "%s: operand %s is at %A." case.Name wanted other)
                  // The items of one array are one entry.
                  let entries = entries |> List.fold (fun acc entry -> match acc with | last :: _ when last = entry && entry.Contains "[]" -> acc | _ -> entry :: acc) [] |> List.rev
                  json.GetProperty("kind").GetString(), entries ]
        rows |> List.sortWith (fun (x, _) (y, _) -> String.CompareOrdinal(x, y)) |> List.map (fun (kind, entries) -> kind + "|" + String.concat " " entries + "\n") |> String.concat ""

    [<Fact>]
    let ``The Zig host's dependency roles match ValueDependency.ofNode`` () =
        let file = Path.Combine(ZigModelReaderSupport.root (), "tools", "zig", "host", "dependency_roles.txt")
        Assert.True((dependencyRoleTable () = File.ReadAllText file), "tools/zig/host/dependency_roles.txt is stale.")

    /// One sidecar member, as `PinnedDependency.embed` writes it, or by path for a legacy selection.
    let internal memberOf (dependency: Snapshot) (content: (string * string) option) (path: string) =
        let fingerprint = SnapshotFingerprint.compute dependency |> unwrap |> SnapshotFingerprint.value
        let item = JsonObject()
        item["fingerprint"] <- JsonValue.Create fingerprint
        item["path"] <- JsonValue.Create path
        item["snapshotId"] <- JsonValue.Create(StableId.value dependency.Id)
        content |> Option.iter (fun (key, text) -> item[key] <- JsonValue.Create text)
        item

/// Its own class, so xunit runs it beside the other host parity tests.
module ZigHostQueryParityTests =
    open ZigHostParityTests

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host answers queries built from each snapshot as the F# host does`` () =
        withDirectory (fun dir ->
            let constructed = constructedSnapshots dir
            // One snapshot at a time: running them in parallel starved the timed runPure tests beside this one.
            let answered = Text.StringBuilder()
            for snapshot in snapshots dir @ constructed do
                use session = AuthoringSession.openReadOnly snapshot |> unwrap
                let requests = queries session
                let code, errors, answers = run true [ "open"; "--snapshot"; snapshot; "--read-only" ] requests
                Assert.True((code = 0), errors)
                let fsharp = requests |> List.map (AuthoringProtocol.executeLine session)
                fsharp |> List.iter (answered.Append >> ignore)
                sameAnswers (Path.GetFileName snapshot) fsharp answers requests
            for shown in [ "\"aborts\":[{"; "\"mayWait\":true"; "\"openObligations\":[{"; "\"frames\":[{"; "\"variants\":["; "\"slot\":\"field\""; "\"evidence\":[{"; "\"references\":[{"; "\"role\":\"patternBinding\""; "\"truncated\":true"; "\"outlineTruncated\":true"; "scopeDepth=1"; "\\\\uD83D\\\\u2026"; "ref "; "\"nodeTypes\":[{"; "\"copiedUpdates\":[{"; "\"kind\":\"ready\""; "\"code\":\"SemanticProjectionFailure\",\"message\":\"IncompleteSnapshot\"" ] do
                Assert.True(answered.ToString().Contains shown, $"No F# reply shows {shown}; the parity test does not cover it.")
            // `full` replies show every node kind's structured form.
            for row in File.ReadAllLines(Path.Combine(ZigModelReaderSupport.root (), "tools", "zig", "host", "graph_kinds.txt")) do
                if not (row.StartsWith "#") then
                    let shown = $"\"kind\":{{\"kind\":\"{row.Split('|')[1]}\""
                    Assert.True(answered.ToString().Contains shown, $"No F# reply shows {shown}; the parity test does not cover it."))

/// Its own class, so xunit runs it beside the other host parity tests.
module ZigHostSeedTests =
    open ZigHostParityTests

    /// With the seed, two runs of the F# host on the same requests answer the same, new IDs included.
    [<Fact; Trait("Suite", "Extended")>]
    let ``The F# host allocates the same IDs for the same seed`` () =
        withDirectory (fun dir ->
            let session seed =
                let path = Path.Combine(dir, $"{seed}-{Guid.NewGuid():N}.json")
                File.WriteAllText(path, SnapshotCodec.serialize (MilestoneZeroGraph.create ()).Snapshot |> unwrap)
                let info = ProcessStartInfo("dotnet", [ fsharpHost; "open"; "--snapshot"; path ])
                info.RedirectStandardInput <- true
                info.RedirectStandardOutput <- true
                info.RedirectStandardError <- true
                info.Environment["NYRWVY_RUNPURE_WARMUP"] <- "0"
                info.Environment["NYRWVY_TEST_ID_SEED"] <- seed
                use host = Process.Start info
                host.StandardInput.Write(
                    """{"id":"c","op":"simulateCreateFunctionByName","exported":true,"displayName":"flag","parameters":[{"displayName":"x","type":{"kind":"primitive","primitive":"int64"}}],"resultType":{"kind":"primitive","primitive":"bool"},"requiredEffects":[],"body":{"kind":"boolLiteral","value":true}}"""
                    + "\n")
                host.StandardInput.Close()
                let output = host.StandardOutput.ReadToEnd()
                host.WaitForExit()
                output
            let first = session "parity"
            Assert.Contains("\"proposalId\":\"018bcfe5-68", first)
            Assert.Equal(first, session "parity")
            Assert.NotEqual<string>(first, session "other")
            // A snapshot that already holds this seed's IDs (a seeded run's commit): the count starts
            // past them, and the host warns that the seed is set.
            let path = Path.Combine(dir, "seeded.json")
            let fixture = MilestoneZeroGraph.create ()
            let taken = StableIdAllocator.seededPayload "parity" 30L
            File.WriteAllText(path, (SnapshotCodec.serialize fixture.Snapshot |> unwrap).Replace(StableId.value fixture.ValidateFunctionId, taken))
            let info = ProcessStartInfo("dotnet", [ fsharpHost; "open"; "--snapshot"; path ])
            info.RedirectStandardInput <- true
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true
            info.Environment["NYRWVY_RUNPURE_WARMUP"] <- "0"
            info.Environment["NYRWVY_TEST_ID_SEED"] <- "parity"
            use host = Process.Start info
            host.StandardInput.Write("""{"id":"c","op":"simulateCreateFunctionByName","exported":true,"displayName":"flag","parameters":[],"resultType":{"kind":"primitive","primitive":"bool"},"requiredEffects":[],"body":{"kind":"boolLiteral","value":true}}""" + "\n")
            host.StandardInput.Close()
            let output = host.StandardOutput.ReadToEnd()
            let errors = host.StandardError.ReadToEnd()
            host.WaitForExit()
            Assert.Contains("NYRWVY_TEST_ID_SEED is set", errors)
            use reply = JsonDocument.Parse output
            let proposal = reply.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
            Assert.True(StableIdAllocator.seededCount "parity" proposal > 30L, proposal))

/// Its own class, so xunit runs it beside the other host parity tests.
module ZigHostViewParityTests =
    open ZigHostParityTests

    /// `viewFunction` writes pages beside the snapshot, so each host gets its own copy of the snapshot's
    /// directory: the replies are compared with that directory masked, and the pages byte for byte.
    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host writes viewFunction pages as the F# host does`` () =
        withDirectory (fun dir ->
            let quote (text: string) = JsonSerializer.Serialize text
            let pages = Text.StringBuilder()
            let replies = Text.StringBuilder()
            let sources = snapshots (Path.Combine(dir, "json") |> fun d -> Directory.CreateDirectory d |> ignore; d) @ constructedSnapshots (Path.Combine(dir, "constructed") |> fun d -> Directory.CreateDirectory d |> ignore; d)
            for index, source in List.indexed sources do
                // Each host's copy of the snapshot's directory, with the setups the error cases need.
                let copy (host: string) =
                    let target = Path.Combine(dir, $"{index}-{host}")
                    Directory.CreateDirectory target |> ignore
                    for file in Directory.GetFiles(Path.GetDirectoryName source) do File.Copy(file, Path.Combine(target, Path.GetFileName file))
                    Directory.CreateDirectory(Path.Combine(target, "elsewhere")) |> ignore
                    File.CreateSymbolicLink(Path.Combine(target, "link"), Path.Combine(target, "elsewhere")) |> ignore
                    File.WriteAllText(Path.Combine(target, "own.html"), "<p>a person's page</p>")
                    File.WriteAllText(Path.Combine(target, "plain.html"), "not a directory")
                    target, Path.Combine(target, Path.GetFileName source)
                let fsharpDir, fsharpSnapshot = copy "fsharp"
                let zigDir, zigSnapshot = copy "zig"
                use session = AuthoringSession.openReadOnly fsharpSnapshot |> unwrap
                let functions = session.Declarations().Functions
                let id (value: StableId<'Kind>) = StableId.value value
                let requests =
                    [ for position, item in List.indexed functions do
                          yield $"""{{"id":"v","op":"viewFunction","functionId":{quote (id item.Id)}}}"""
                          yield $"""{{"id":"v","op":"viewFunction","function":{quote item.DisplayName},"output":"views/{position}.html"}}"""
                      match functions with
                      | first :: _ ->
                          let at (output: string) = $"""{{"id":"v","op":"viewFunction","functionId":{quote (id first.Id)},"output":{quote output}}}"""
                          // A page this tool wrote is replaced; the others are refused.
                          yield at "views/0.html"
                          yield! [ ""; "  "; "page.txt"; "../outside.html"; "/elsewhere/outside.html"; "link/page.html"; "own.html"; "plain.html/page.html"; "nested/deeper/page.HTML"; "page.html\u0000.html" ] |> List.map at
                          yield $"""{{"id":"v","op":"viewFunction","functionId":{quote (id first.Id)},"output":3}}"""
                      | [] -> ()
                      yield """{"id":"v","op":"viewFunction"}"""
                      yield """{"id":"v","op":"viewFunction","function":"x","functionId":"y"}"""
                      yield """{"id":"v","op":"viewFunction","functionId":"missing"}"""
                      yield """{"id":"v","op":"viewFunction","functionId":"missing","extra":1}""" ]
                let code, errors, answers = run true [ "open"; "--snapshot"; zigSnapshot; "--read-only" ] requests
                Assert.True((code = 0), errors)
                let fsharp = requests |> List.map (AuthoringProtocol.executeLine session >> fun reply -> reply.Replace(fsharpDir, "<dir>"))
                fsharp |> List.iter (replies.Append >> ignore)
                sameAnswers (Path.GetFileName source) fsharp (answers |> List.map (fun reply -> reply.Replace(zigDir, "<dir>"))) requests
                let written (root: string) =
                    Directory.GetFiles(root, "*.html", SearchOption.AllDirectories)
                    |> Array.map (fun file -> Path.GetRelativePath(root, file))
                    |> Array.sort
                    |> Array.toList
                Assert.Equal<string list>(written fsharpDir, written zigDir)
                for file in written fsharpDir do
                    let expected = File.ReadAllBytes(Path.Combine(fsharpDir, file))
                    Assert.True((expected = File.ReadAllBytes(Path.Combine(zigDir, file))), $"{Path.GetFileName source}: {file} differs.")
                    pages.Append(Text.Encoding.UTF8.GetString expected) |> ignore
            for shown in [ "\"role\":\"UnionVariant\""; "\"sourceFieldId\":\""; "\"kind\":\"capabilityConstruct\""; "\"waitPath\":\""; "\"mustNotWait\":true"; "\"referencedCapabilities\":[{"; "\"originTransactionId\":\""; "\"role\":\"CallArgument\""; "\"kind\":\"CallsFunction\""; "\"bits\":\"" ] do
                Assert.True(pages.ToString().Contains shown, $"No F# page shows {shown}; the parity test does not cover it.")
            for shown in [ "output must not be blank."; "output must name a .html file."; "output must stay inside"; "is one."; "is one or is not a directory."; "without the generated-file marker"; "\"kind\":\"functionFocus\"" ] do
                Assert.True(replies.ToString().Contains shown, $"No F# reply shows {shown}; the parity test does not cover it."))

/// Its own class, so xunit runs it beside the other host parity tests.
module ZigHostStartupParityTests =
    open ZigHostParityTests

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

/// Its own class, so xunit runs it beside the other host parity tests.
module ZigHostLockParityTests =
    open ZigHostParityTests

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

/// Its own class, so xunit runs it beside the other host parity tests.
module ZigHostResolvedParityTests =
    open ZigHostParityTests

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
                    // The queries read the same model in both modes, so the read-only run asks them; the writable run checks the open.
                    let request =
                        if not readOnly then request
                        else
                            match AuthoringSession.openReadOnly path with
                            | Ok session ->
                                use session = session
                                request @ queries session
                            | Error _ -> request
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

/// Its own class, so xunit runs it beside the other host parity tests.
module ZigTypedModelTests =
    open ZigHostParityTests

    /// The Zig host's typed model (tools/zig/model-reader/typed.zig) reads what an F# commit writes
    /// and writes it back byte for byte (`model-reader --typed-roundtrip`), resolved snapshots with
    /// their dependency marker, and gives the F# fingerprints of a plain one; `snapshot-reader
    /// --records` writes that text in record form as `RecordCodec.ofJson` does.
    [<Fact; Trait("Suite", "Extended")>]
    let ``The typed model writes snapshots as the F# writer does`` () =
        withDirectory (fun dir ->
            let reader = ZigTools.tool "model-reader" |> Result.mapError (fun (code, message) -> $"{code}: {message}") |> unwrap
            let recordWriter = ZigTools.tool "snapshot-reader" |> Result.mapError (fun (code, message) -> $"{code}: {message}") |> unwrap
            let roundTrip name (expected: string) (fingerprints: string option) =
                let input, output = Path.Combine(dir, name + ".in.json"), Path.Combine(dir, name + ".out.json")
                File.WriteAllText(input, expected, Text.UTF8Encoding false)
                let info = ProcessStartInfo(reader, [ "--typed-roundtrip"; input; output ])
                info.RedirectStandardOutput <- true
                info.RedirectStandardError <- true
                use zig = Process.Start info
                let printed = zig.StandardOutput.ReadToEnd()
                let errors = zig.StandardError.ReadToEnd()
                zig.WaitForExit()
                Assert.True((zig.ExitCode = 0), $"{name}: {errors}")
                let written = if File.Exists output then File.ReadAllText output else ""
                if written <> expected then
                    let at = Seq.zip expected written |> Seq.tryFindIndex (fun (x, y) -> x <> y) |> Option.defaultValue (min expected.Length written.Length)
                    let around (text: string) = if at < text.Length then text.Substring(max 0 (at - 80), min 160 (text.Length - max 0 (at - 80))) else "<end>"
                    failwithf "%s: the written text differs at %d (%s)\n F#:  %s\n Zig: %s" name at printed (around expected) (around written)
                Assert.Equal(defaultArg fingerprints "resolved\n", printed)
                // The same text in record form (`.nyrec`), as `RecordCodec.ofJson` writes it.
                let records = Path.Combine(dir, name + ".nyrec")
                let info = ProcessStartInfo(recordWriter, [ "--records"; output; records ])
                info.RedirectStandardError <- true
                use writer = Process.Start info
                let errors = writer.StandardError.ReadToEnd()
                writer.WaitForExit()
                Assert.True((writer.ExitCode = 0), $"{name}: {errors}")
                let expectedRecords = RecordCodec.ofJson expected
                let writtenRecords = File.ReadAllText records
                if writtenRecords <> expectedRecords then
                    let at = Seq.zip expectedRecords writtenRecords |> Seq.tryFindIndex (fun (x, y) -> x <> y) |> Option.defaultValue (min expectedRecords.Length writtenRecords.Length)
                    let around (text: string) = if at < text.Length then text.Substring(max 0 (at - 80), min 160 (text.Length - max 0 (at - 80))) else "<end>"
                    failwithf "%s: the records differ at %d\n F#:  %s\n Zig: %s" name at (around expectedRecords) (around writtenRecords)
            let plain name (snapshot: Snapshot) =
                let fingerprints = $"snapshot {SnapshotFingerprint.compute snapshot |> unwrap |> SnapshotFingerprint.value}\nsemantic {SemanticFingerprint.compute snapshot |> unwrap |> SemanticFingerprint.value}\n"
                roundTrip name (SnapshotCodec.serialize snapshot |> unwrap) (Some fingerprints)
            // What a commit of each example writes, and of its resolved form (the declarations as an
            // imported dependency, the functions local), as `serializeWithDependency` writes it.
            for index, path in List.indexed (snapshots (Path.Combine(dir, "json") |> fun d -> Directory.CreateDirectory d |> ignore; d)) do
                use session = AuthoringSession.openReadOnly path |> unwrap
                let snapshot = session.CommitJson() |> unwrap |> SnapshotCodec.deserialize |> unwrap
                plain $"example-{index}" snapshot
                let dependency = { snapshot with Id = StableId.createForTesting<SnapshotTag> $"zig-typed-dependency-{index}"; ParentId = None; Functions = []; Obligations = [] }
                let local = { snapshot with Types = []; Effects = []; Capabilities = [] }
                roundTrip $"resolved-{index}" (SnapshotCodec.serializeResolved dependency local |> unwrap) None
            for index, snapshot in List.indexed (RecordCodecAllKindsTests.allKindSnapshots () @ [ (MilestoneZeroGraph.create ()).Snapshot ]) do
                plain $"kinds-{index}" snapshot
            for index, path in List.indexed (constructedSnapshots (Path.Combine(dir, "constructed") |> fun d -> Directory.CreateDirectory d |> ignore; d)) do
                plain $"constructed-{index}" (SnapshotCodec.deserialize (File.ReadAllText path) |> unwrap))
