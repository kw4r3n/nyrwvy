namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Nyrwvy.Core
open Xunit

/// The Zig session host (tools/zig/host, docs/decisions/zig-session-host.md) against the F# host:
/// each transcript in tests/host-parity is one request per line, fed to both on the same snapshots,
/// and every response line must be the same bytes; the startups that fail must print the same
/// line and exit code. Two parts are host-specific: .NET's JSON parser text after "Invalid JSON:"
/// and "not valid JSON:" (compared by that prefix) and `status.build` (each host's own build).
module ZigHostParityTests =
    let internal unwrap = function Ok x -> x | Error e -> failwithf "%A" e

    /// The hosts' request depth limit (3 + 3 * 256 + 64, `max_depth` in tools/zig/host/main.zig).
    let internal requestMaxJsonDepth = 835

    // Successful proposals can carry signatures deeper than JsonDocument's default 64 levels.
    let internal parseDocument (json: string) =
        JsonDocument.Parse(json, JsonDocumentOptions(MaxDepth = requestMaxJsonDepth))

    let internal comparable (line: string) =
        let masked = Regex.Replace(line, "\"build\":\"[^\"]*\"", "\"build\":\"…\"")
        Regex.Replace(masked, "(Invalid JSON: |not valid JSON: ).*", "$1…")

    let internal zigHost () = ZigTools.tool "host" |> Result.mapError (fun (code, message) -> $"{code}: {message}") |> unwrap

    let private goldenRuns = Collections.Concurrent.ConcurrentDictionary<string, int ref>()

    /// The calling test method, found on the stack (so only on the test's own thread).
    let internal testName () =
        Diagnostics.StackTrace().GetFrames()
        |> Array.tryPick (fun frame ->
            match frame.GetMethod() with
            | null -> None
            | m when m.IsDefined(typeof<FactAttribute>, true) -> Some(Regex.Replace($"{m.DeclaringType.Name}.{m.Name}", "[^A-Za-z0-9.]+", "-").Trim('-'))
            | _ -> None)

    /// The run file of the next F# host start in the calling test: the test method and its count
    /// of F# starts so far.
    let internal goldenRun () =
        let name = testName () |> Option.defaultWith (fun () -> failwith "An F# host reference started outside a test method's thread.")
        let count = goldenRuns.GetOrAdd(name, fun _ -> ref 0)
        let n = lock count (fun () -> count.Value <- count.Value + 1; count.Value)
        Path.Combine(ZigModelReaderSupport.root (), "tests", "goldens", "zig-host", name, $"{n}.jsonl.br")

    /// Starts a redirected host on `arguments`, with optional per-process environment overrides: the
    /// Zig host, or for the reference (`zig = false`) the replay of the F# host's recorded run
    /// (scripts/host-golden.mjs; the F# host is at tag fsharp-host-final).
    let internal startWithEnvironment (environment: (string * string) list) (zig: bool) (arguments: string list) =
        let info =
            if zig then ProcessStartInfo(zigHost (), arguments)
            else
                let overrides = JsonSerializer.Serialize(dict environment)
                let script = Path.Combine(ZigModelReaderSupport.root (), "scripts", "host-golden.mjs")
                ProcessStartInfo("node", [ script; "replay"; goldenRun (); overrides; "--" ] @ arguments)
        info.RedirectStandardInput <- true
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        for name, value in environment do info.Environment[name] <- value
        Process.Start info

    let internal start zig arguments = startWithEnvironment [] zig arguments

    /// How long a host may go without answering before the run fails.
    let internal silence = TimeSpan.FromSeconds 60.0

    /// Exit code, stderr and stdout lines of a host run to the end of `requests`. Output is read
    /// while the requests are written, so a host blocked on a full stdout never deadlocks the run,
    /// and a host that stops answering for `silence` is killed and named.
    let internal runWithEnvironment environment zig arguments (requests: string list) =
        use host = startWithEnvironment environment zig arguments
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

    let internal run zig arguments requests = runWithEnvironment [] zig arguments requests

    /// The F# host's read-only replies to `requests` on `snapshot`, through `run`, so the goldens hold them.
    let internal fsharpReplies snapshot requests =
        let code, errors, replies = run false [ "open"; "--snapshot"; snapshot; "--read-only" ] requests
        Assert.True((code = 0), errors)
        replies

    /// Record IDs must be UUIDs in every domain, unlike JSON's permissive test IDs. Keep the
    /// graph and its names/literals intact, using a seed distinct from any host transcript seed.
    let internal recordFixture name (snapshot: Snapshot) =
        let ids = Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
        let mapped text =
            match ids.TryGetValue text with
            | true, id -> id
            | _ ->
                let id = StableIdAllocator.seededPayload ("record-fixture:" + name) (int64 ids.Count + 1L)
                ids.Add(text, id)
                id
        let rec rewrite identity (node: JsonNode) : JsonNode =
            match node with
            | null -> null
            | :? JsonObject as o ->
                for key in [ for property in o -> property.Key ] do
                    // FunctionInvoke has bare node IDs in arguments; other argument arrays contain objects.
                    let isId =
                        key = "id" || key.EndsWith("Id", StringComparison.Ordinal) || key.EndsWith("Ids", StringComparison.Ordinal)
                        || key = "requiredEffects" || key = "arguments"
                    let rewritten = rewrite isId o[key]
                    if not (obj.ReferenceEquals(rewritten, o[key])) then o[key] <- rewritten
                node
            | :? JsonArray as items ->
                for index in 0 .. items.Count - 1 do
                    let rewritten = rewrite identity items[index]
                    if not (obj.ReferenceEquals(rewritten, items[index])) then items[index] <- rewritten
                node
            | _ when identity -> JsonValue.Create(mapped (node.GetValue<string>())) :> JsonNode
            | _ -> node
        let json = SnapshotCodec.serialize snapshot |> unwrap
        let node = JsonNode.Parse(json, documentOptions = JsonDocumentOptions(MaxDepth = SnapshotCodec.maxJsonDepth))
        rewrite false node |> fun root -> SnapshotCodec.deserialize (root.ToJsonString()) |> unwrap

    /// The example records in the order the goldens were recorded in (shallower first, then by path), not
    /// in the file system's directory order, which differs between machines.
    let internal exampleRecords () =
        Directory.GetFiles(Path.Combine(ZigModelReaderSupport.root (), "examples"), "*.nyrec", SearchOption.AllDirectories)
        |> Array.sortBy (fun path -> path.Split(Path.DirectorySeparatorChar).Length, path) |> Array.toList

    let internal snapshots (dir: string) =
        let records = exampleRecords ()
        let json = Path.Combine(dir, "task-outcomes.json")
        File.WriteAllText(json, File.ReadAllText records.Head |> RecordCodec.deserialize |> unwrap |> SnapshotCodec.serialize |> unwrap)
        records @ [ json ]

    // The last seeded ID count each test's Zig-built fixtures reached.
    let private fixtureCounts = Collections.Concurrent.ConcurrentDictionary<string, int64>()

    /// A fresh directory for `f`. In-process fixtures built inside get IDs seeded by the test's name,
    /// so the requests made from them match the recorded F# runs.
    let internal withDirectory f =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-zig-host-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        // Each body starts its fixture count afresh, as the F# fixtures' async-local seed did.
        testName () |> Option.iter (fixtureCounts.TryRemove >> ignore)
        let seeded = match testName () with Some name -> StableIdAllocator.withTestSeed ("fixture:" + name) | None -> fun run -> run ()
        try seeded (fun () -> f dir) finally Directory.Delete(dir, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host answers every transcript as the F# host does`` () =
        withDirectory (fun dir ->
            let transcripts = Directory.GetFiles(Path.Combine(ZigModelReaderSupport.root (), "tests", "host-parity"), "*.jsonl") |> Array.sort
            Assert.NotEmpty transcripts
            for snapshot in snapshots dir do
                for transcript in transcripts do
                    let requests = File.ReadAllText(transcript).Split('\n') |> Array.toList |> fun lines -> List.take (lines.Length - 1) lines
                    let fsharp = fsharpReplies snapshot requests
                    let code, errors, answers = run true [ "open"; "--snapshot"; snapshot; "--read-only" ] requests
                    Assert.True((code = 0), errors)
                    Assert.Equal(requests.Length, answers.Length)
                    List.zip3 requests fsharp answers |> List.iteri (fun index (request, expected, actual) ->
                        if comparable expected <> comparable actual then
                            failwithf "%s on %s line %d: %s\n F#:  %s\n Zig: %s" (Path.GetFileName transcript) (Path.GetFileName snapshot) (index + 1) request expected actual))

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host discards a partial internal failure and serves the next request`` () =
        withDirectory (fun dir ->
            let snapshot = snapshots dir |> List.head
            let requests =
                [ """{"id":"before","op":"declarations"}"""
                  """{"id":"failed","op":"status"}"""
                  """{"id":"after","op":"declarations"}""" ]
            let code, errors, answers =
                runWithEnvironment [ "NYRWVY_TEST_HOST_FAIL_OPERATION", "status" ] true
                    [ "open"; "--snapshot"; snapshot; "--read-only" ] requests
            Assert.True((code = 0), errors)
            Assert.Equal(3, answers.Length)
            Assert.Equal(
                """{"id":"failed","ok":false,"error":{"code":"InternalError","message":"Unexpected internal failure; this request was not applied and the session is unchanged."}}""",
                answers[1])
            use before = parseDocument answers[0]
            use after = parseDocument answers[2]
            Assert.True(before.RootElement.GetProperty("ok").GetBoolean())
            Assert.True(after.RootElement.GetProperty("ok").GetBoolean())
            Assert.Equal("after", after.RootElement.GetProperty("id").GetString())
            Assert.Equal(before.RootElement.GetProperty("result").GetRawText(), after.RootElement.GetProperty("result").GetRawText()))

    /// Exit code, stderr and stdout lines of a host fed `input` as raw bytes, which a TextWriter would re-encode.
    let internal runBytes zig arguments (input: byte[]) =
        use host = start zig arguments
        let output = host.StandardOutput.ReadToEndAsync()
        let errors = host.StandardError.ReadToEndAsync()
        try
            host.StandardInput.BaseStream.Write(input, 0, input.Length)
            host.StandardInput.Close()
        with :? IOException -> ()
        if not (host.WaitForExit 300000) then
            host.Kill true
            failwithf "The %s host did not finish its byte input" (if zig then "Zig" else "F#")
        host.ExitCode, errors.Result.Trim(), output.Result.Split('\n') |> Array.filter ((<>) "") |> Array.toList

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host splits limits and decodes request lines as the F# host does`` () =
        withDirectory (fun dir ->
            let snapshot = snapshots dir |> List.head
            let ascii (text: string) = Text.Encoding.ASCII.GetBytes text
            let limit = 16 * 1024 * 1024
            // 0xE9 alone is ill-formed UTF-8; the limit counts UTF-16 units, so two-byte é fills it at 32 MiB.
            let input =
                Array.concat
                    [ ascii "{\"id\":\"cr\",\"op\":\"declarations\"}\r"
                      ascii "{\"id\":\"bad\",\"op\":\"search\",\"text\":\"a"; [| 0xE9uy |]; ascii "b\"}\r\n\r\r\n"
                      Array.replicate limit (Text.Encoding.UTF8.GetBytes "é") |> Array.concat; ascii "\n"
                      Array.replicate (limit + 1) (Text.Encoding.UTF8.GetBytes "é") |> Array.concat; ascii "\n"
                      ascii "{\"id\":\"last\",\"op\":\"status\"}" ]
            let arguments = [ "open"; "--snapshot"; snapshot; "--read-only" ]
            let fsharpCode, fsharpErrors, expected = runBytes false arguments input
            let zigCode, zigErrors, actual = runBytes true arguments input
            Assert.True((fsharpCode = 0), fsharpErrors)
            Assert.True((zigCode = 0), zigErrors)
            Assert.Equal(7, expected.Length)
            Assert.Equal("""{"id":null,"ok":false,"error":{"code":"ProtocolError","message":"Request line exceeds 16 MiB."}}""", expected[5])
            Assert.Contains("\\uFFFD", expected[1])
            Assert.Equal<string list>(expected |> List.map comparable, actual |> List.map comparable))

    /// Simulates and commits each request in turn on a Zig host (`create` makes the snapshot), as
    /// the in-process F# fixtures did. New IDs are seeded by the test's name, as `withDirectory`
    /// seeds in-process allocation, so the fixture and the requests built from it match the
    /// recorded F# runs. The F# fixtures of one test shared one count, so a later fixture of the
    /// same test starts from an empty snapshot whose ID is the next seeded one, which the host
    /// observes before it allocates.
    let internal publishWithZigHost (mode: string) (path: string) (requests: string list) =
        let name = testName () |> Option.defaultWith (fun () -> failwith "A Zig fixture was built outside a test method's thread.")
        let seed = "fixture:" + name
        let mode =
            match mode, fixtureCounts.TryGetValue name with
            | "create", (true, count) ->
                let id = StableId.tryCreate<SnapshotTag> (StableIdAllocator.seededPayload seed (count + 1L)) |> unwrap
                let workspace = AgentWorkspace.create (Snapshot.empty id) |> unwrap
                File.WriteAllText(path, AgentWorkspace.serializeCurrentSnapshot workspace |> unwrap)
                File.WriteAllText(path + ".backends.json", "{\"targets\": [\"csharp\"]}\n")
                "open"
            | _ -> mode
        let info = ProcessStartInfo(zigHost (), [ mode; "--snapshot"; path ])
        info.RedirectStandardInput <- true
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.Environment["NYRWVY_TEST_ID_SEED"] <- seed
        use host = Process.Start info
        let errors = host.StandardError.ReadToEndAsync()
        let mutable reached = 0L
        let send (line: string) =
            host.StandardInput.Write(line + "\n")
            host.StandardInput.Flush()
            let reply = host.StandardOutput.ReadLine()
            if isNull reply then failwithf "The Zig host ended before answering %s: %s" line errors.Result
            reached <- max reached (StableIdAllocator.seededCount seed reply)
            use document = parseDocument reply
            if not (document.RootElement.GetProperty("ok").GetBoolean()) then failwithf "%s\n%s" line reply
            document.RootElement.GetProperty("result").Clone()
        for request in requests do
            let proposal = (send request).GetProperty("proposalId").GetString()
            send $"""{{"id":"commit","op":"commit","proposalId":"{proposal}"}}""" |> ignore
        host.StandardInput.Close()
        host.WaitForExit()
        Assert.True((host.ExitCode = 0), errors.Result)
        let written = StableIdAllocator.seededCount seed (File.ReadAllText path)
        fixtureCounts.AddOrUpdate(name, max reached written, fun _ count -> max count (max reached written)) |> ignore

    /// A snapshot file without a dependency, read with the F# libraries.
    let internal readSnapshot (path: string) =
        let text = File.ReadAllText path
        if path.EndsWith(".nyrec", StringComparison.Ordinal) then RecordCodec.deserialize text |> unwrap
        else SnapshotCodec.deserialize text |> unwrap

    /// The declarations a session on a snapshot without a dependency lists, each sorted by ID as
    /// `AgentWorkspace` lists them, and the snapshot's ID.
    type internal FixtureDeclarations =
        { Functions: SemanticFunction list
          Types: SemanticType list
          Effects: SemanticEffect list
          Capabilities: SemanticCapability list }

    let internal declarationsOf (path: string) =
        let workspace = AgentWorkspace.create (readSnapshot path) |> unwrap
        { Functions = AgentWorkspace.listFunctions workspace
          Types = AgentWorkspace.listTypes workspace
          Effects = AgentWorkspace.listEffects workspace
          Capabilities = AgentWorkspace.listCapabilities workspace },
        StableId.value (AgentWorkspace.currentSnapshotId workspace)

    /// `references`, `search` and `unreferenced` requests built from the declarations the Zig host
    /// lists for `snapshot` (its `declarations` reply, which the parity tests hold equal to the F#
    /// host's): every declaration and member by exact ID and by display names (as a string and as a
    /// selector object), and searches for parts of their names. Function bodies come from
    /// `functions` by ID. None when the Zig host does not open the snapshot.
    let internal queriesFrom (functions: SemanticFunction list) (snapshot: string) =
        let code, _, replies = run true [ "open"; "--snapshot"; snapshot; "--read-only" ] [ """{"id":"d","op":"declarations"}""" ]
        let reply = if code = 0 && not replies.IsEmpty then Some(parseDocument replies.Head) else None
        let build (declarations: JsonElement) =
            let all (owner: JsonElement) (name: string) = owner.GetProperty(name).EnumerateArray() |> Seq.toList
            let text (item: JsonElement) (name: string) = item.GetProperty(name).GetString()
            let bodies = functions |> List.map (fun item -> StableId.value item.Id, item) |> Map.ofList
            let quote (text: string) = JsonSerializer.Serialize text
            let id = StableId.value
            let byName kind (key: string) name =
                [ $"""{{"id":"r","op":"references","kind":"{kind}","{key}":{quote name}}}"""
                  $"""{{"id":"r","op":"references","kind":"{kind}","{key}":{{"name":{quote name}}}}}""" ]
            let named kind exactKey (exact: string) (name: string) =
                $"""{{"id":"r","op":"references","kind":"{kind}","{exactKey}":{quote exact}}}""" :: byName kind kind name
            let parts (name: string) = [ name; name.ToUpperInvariant(); (if name.Length > 2 then name.Substring(1, 2) else name) ]
            let listed = all declarations "functions" |> List.map (fun item -> bodies[text item "id"])
            Some
                [ for item in listed do
                      yield! named "function" "functionId" (id item.Id) item.DisplayName
                      yield! parts item.DisplayName |> List.map (fun text -> $"""{{"id":"q","op":"search","text":{quote text},"limit":3}}""")
                  for item in all declarations "types" do
                      let typeName = text item "displayName"
                      yield! named "type" "typeId" (text item "id") typeName
                      let kind = item.GetProperty "typeKind"
                      match kind.GetProperty("kind").GetString() with
                      | "product" ->
                          for field in all kind "fields" do
                              yield $"""{{"id":"r","op":"references","kind":"field","fieldId":{quote (text field "id")}}}"""
                              yield $"""{{"id":"r","op":"references","kind":"field","type":{quote typeName},"field":{quote (text field "displayName")}}}"""
                          yield $"""{{"id":"r","op":"references","kind":"variant","type":{quote typeName},"variant":"x"}}"""
                      | _ ->
                          for variant in all kind "variants" do
                              yield $"""{{"id":"r","op":"references","kind":"variant","variantId":{quote (text variant "id")}}}"""
                              yield $"""{{"id":"r","op":"references","kind":"variant","type":{{"id":{quote (text item "id")}}},"variant":{quote (text variant "displayName")}}}"""
                              for field in all variant "fields" do
                                  yield $"""{{"id":"r","op":"references","kind":"field","fieldId":{quote (text field "id")}}}"""
                                  yield $"""{{"id":"r","op":"references","kind":"field","type":{quote typeName},"variant":{quote (text variant "displayName")},"field":{quote (text field "displayName")}}}"""
                          yield $"""{{"id":"r","op":"references","kind":"field","type":{quote typeName},"field":"x"}}"""
                  for item in all declarations "effects" do yield! named "effect" "effectId" (text item "id") (text item "displayName")
                  for item in all declarations "capabilities" do
                      let capabilityName = text item "displayName"
                      yield! named "capability" "capabilityId" (text item "id") capabilityName
                      for operation in all item "operations" do
                          yield $"""{{"id":"r","op":"references","kind":"capabilityOperation","operationId":{quote (text operation "id")}}}"""
                          yield $"""{{"id":"r","op":"references","kind":"capabilityOperation","capability":{quote capabilityName},"operation":{quote (text operation "displayName")}}}"""
                          yield! parts (text operation "displayName") |> List.map (fun text -> $"""{{"id":"q","op":"search","text":{quote text}}}""")
                  for item in all declarations "provenance" do
                      let kind, itemId = text item "kind", text item "id"
                      for ownership in [ "local"; "imported" ] do
                          yield $"""{{"id":"r","op":"references","kind":"{kind}","{kind}":{{"id":{quote itemId},"ownership":"{ownership}"}}}}"""
                      for source in all item "sources" do
                          yield $"""{{"id":"r","op":"references","kind":"{kind}","{kind}":{{"id":{quote itemId},"dependencySnapshotId":{quote (text source "snapshotId")}}}}}"""
                  for item in listed do
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
                  for item in listed |> List.truncate 3 do
                      yield $"""{{"id":"u","op":"unreferenced","roots":[{quote item.DisplayName}]}}""" ]
        match reply with
        | Some reply when reply.RootElement.GetProperty("ok").GetBoolean() -> build (reply.RootElement.GetProperty "result")
        | _ -> None

    /// `queriesFrom` on a snapshot without a dependency.
    let internal queries (snapshot: string) = queriesFrom (readSnapshot snapshot).Functions snapshot |> Option.get

    /// A `SessionError` reply carries .NET's exception text, so it is compared by its code (and ID) only.
    let internal sessionErrorCode (reply: string) =
        Text.RegularExpressions.Regex.Replace(reply, "\"code\":\"SessionError\",\"message\":\"(?:[^\"\\\\]|\\\\.)*\"", "\"code\":\"SessionError\"")

    /// The diagnostic codes of the `simulateCreateFunction(ByName)` preflight the Zig host answers (step 3c-3a).
    let internal preflightCodes =
        set [ "FunctionCreationTypeParameterNameBlank"; "FunctionCreationDuplicateTypeParameter"; "FunctionCreationTypeParameterNotDeclared"; "FunctionCreationTypeParameterNotInferable"
              "StructuralExpectedBaseMismatch"; "FunctionCreationDisplayNameBlank"; "FunctionCreationDuplicateDisplayName"; "FunctionCreationParameterDisplayNameBlank"
              "PureExpressionInvalidTypeRef"; "NamedTypeArityMismatch"; "FunctionCreationDuplicateRequiredEffect"; "FunctionCreationRequiredEffectNotFound"
              "StructuralRecipeDepthLimitExceeded"; "StructuralRecipeNodeLimitExceeded" ]

    /// The diagnostic codes of the ported inference (steps 3c-3b and 3c-3c), located at their node.
    let internal inferenceCodes =
        set [ "PureExpressionOperandTypeMismatch"; "ListIndexOfItemTypeNotEquatable"; "ListLastIndexOfItemTypeNotEquatable"; "ListContainsItemTypeNotEquatable"
              "SetItemTypeNotEquatable"; "MapKeyTypeNotEquatable"; "ValueEqualTypeNotEquatable"; "OriginalTargetOutsideTargetedReplacement"
              "PureExpressionParameterOutOfScope"; "PureExpressionParameterSlotOutOfRange"; "PureExpressionPatternScopeDepthInvalid"; "PureExpressionPatternScopeNotFound"
              "PureExpressionPatternSlotNotFound"; "PureExpressionDuplicateFieldBinding"; "PureExpressionFieldBindingMismatch"; "PureExpressionFieldBindingTypeMismatch"
              "PureExpressionTypeArgumentArityMismatch"; "PureExpressionTypeArgumentsRequired"; "PureExpressionProductTypeNotFound"; "PureExpressionProductTargetMismatch"
              "PureExpressionProductFieldNotFound"; "PureExpressionFieldAccessTypeMismatch"; "PureExpressionEmptyProductUpdate"; "PureExpressionProductUpdateSourceMismatch"
              "PureExpressionUnionVariantNotFound"; "AbortValueUse"; "PureExpressionBranchTypeMismatch"; "AbortPayloadTypeMismatch"; "AbortOwnerResultTypeMismatch"
              "PureExpressionMatchArmDuplicate"; "PureExpressionMatchArmMismatch"; "PureExpressionMatchOtherwiseDuplicate"; "PureExpressionMatchOtherwiseUnused"
              "PureExpressionMatchInputTypeMismatch"; "FunctionCreationResultTypeMismatch"
              "CancellationShieldBodyMismatch"; "CancellationShieldResultMismatch"; "CapabilityImplementationSetMismatch"
              "CapabilityImplementationSignatureMismatch"; "DuplicateCapabilityImplementation"; "ExceptionCatchCallbackMismatch"
              "ExceptionCatchResultMismatch"; "ExceptionValueTypeMismatch"; "ExpressionCallMissingEffectRequirement"
              "ExpressionCapabilityArgumentSetMismatch"; "ExpressionCapabilityArgumentTypeMismatch"; "ExpressionCapabilityMissingEffectRequirement"
              "ExpressionCapabilityOperationNotFound"; "ExpressionCapabilityValueTypeMismatch"; "ExpressionDuplicateCapabilityArgument"
              "FunctionInvokeArgumentTypeMismatch"; "FunctionInvokeArityMismatch"; "FunctionInvokeTargetTypeMismatch"
              "GenericCapabilityImplementationUnsupported"; "GenericFunctionReferenceUnsupported"; "ImpureCapabilityImplementation"; "ImpureFunctionReference"
              "OneShotCellTypeMismatch"; "OneShotLimitTypeMismatch"; "OneShotScopePayloadUnsupported"; "OneShotScopeTypeMismatch"; "OneShotValueTypeMismatch"
              "PureExpressionCallArgumentMissing"; "PureExpressionCallArgumentTargetNotFound"; "PureExpressionCallArgumentTypeMismatch"
              "PureExpressionCallTargetNotFound"; "PureExpressionCallbackArityMismatch"; "PureExpressionCallbackContextTypeMismatch"
              "PureExpressionCallbackEffectsNotAllowed"; "PureExpressionCallbackInputTypeMismatch"; "PureExpressionCallbackNotFound"
              "PureExpressionCallbackResultTypeMismatch"; "PureExpressionDuplicateCallArgument"; "TaskCancelInputTypeMismatch"; "TaskScopeBodyMismatch"
              "TaskScopeResultMismatch"; "TaskStartScopeTypeMismatch"; "TaskWaitInputTypeMismatch"; "TaskWaitLimitTypeMismatch"
              "UnknownCapabilityConstructTarget"; "UnknownCapabilityImplementationFunction"; "UnknownFunctionReference"; "UseResourceAcquireTypeMismatch"
              "UseResourceBodyMismatch"; "UseResourceReleaseReceiverMismatch"; "UseResourceReleaseSignatureMismatch"; "UseResourceResultMismatch" ]

    /// `locate`'s suffix on an inference diagnostic: the failing node's path, or None for a preflight one.
    let internal locatedPath (message: string) =
        let found = Regex.Match(message, " At (body[^ ]*) \\([a-z][A-Za-z0-9]*\\)\\.(?:\"|$)")
        if found.Success then Some found.Groups[1].Value else None

    /// Whether the Zig reply to a create request matches the F# one: a `SessionError` by code, and
    /// every other reply (protocol errors, rejections, compact and full successes) byte for byte.
    let internal createReplyMatches (_request: string) (fsharp: string) (zig: string) =
        use reply = parseDocument fsharp
        match reply.RootElement.TryGetProperty "error" with
        | true, error when error.GetProperty("code").GetString() = "SessionError" -> sessionErrorCode fsharp = sessionErrorCode zig
        | _ -> fsharp = zig

    [<Fact>]
    let ``Deep reply parsing preserves byte-exact create comparisons`` () =
        let nested value = String.replicate 80 "[" + value + String.replicate 80 "]"
        let request = "{\"body\":" + nested "0" + "}"
        let reply value = "{\"ok\":true,\"result\":" + nested value + "}"
        Assert.True(createReplyMatches request (reply "0") (reply "0"))
        Assert.False(createReplyMatches request (reply "0") (reply "1"))
        Assert.False(createReplyMatches request (reply "0") (reply "0" + " "))

    let internal seedWarning = "Warning: NYRWVY_TEST_ID_SEED is set, so new IDs are deterministic test IDs. Use it only on test copies."

    /// Separate processes and separate copies: the process-wide seed counter cannot be shared
    /// with other tests. Successful and rejected requests share one ID stream.
    let internal seededPair (seed: string) (source: string) (requests: string list) =
        withDirectory (fun dir ->
            let copy (name: string) =
                let target = Path.Combine(dir, name)
                Directory.CreateDirectory target |> ignore
                let original = Path.GetDirectoryName source
                for file in Directory.GetFiles(original, "*", SearchOption.AllDirectories) do
                    let destination = Path.Combine(target, Path.GetRelativePath(original, file))
                    Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
                    File.Copy(file, destination)
                target, Path.Combine(target, Path.GetFileName source)
            let fsharpDir, fsharpSnapshot = copy "fsharp"
            let zigDir, zigSnapshot = copy "zig"
            let environment = [ "NYRWVY_TEST_ID_SEED", seed ]
            let runHost zig (directory: string) (path: string) =
                let code, errors, answers = runWithEnvironment environment zig [ "open"; "--snapshot"; path; "--read-only" ] requests
                let next = requests |> List.tryItem answers.Length |> Option.defaultValue "<end of transcript>"
                Assert.True((code = 0), $"{errors}\nNext unanswered request: {next}")
                Assert.Equal(seedWarning, errors)
                Assert.Equal(requests.Length, answers.Length)
                answers |> List.map (fun reply -> reply.Replace(directory, "<dir>") |> comparable)
            let fsharp = runHost false fsharpDir fsharpSnapshot
            let zig = runHost true zigDir zigSnapshot
            for request, (expected, actual) in List.zip requests (List.zip fsharp zig) do
                Assert.True(createReplyMatches request expected actual, $"{request}\n F#:  {expected}\n Zig: {actual}")
            fsharp, zig)

    /// Writable seeded pair. The caller owns `directory`, so output remains available for cross-host
    /// reopen checks. Capture the entire tree after each commit, including failed commits.
    let internal seededWritablePair (seed: string) (source: string) (directory: string) (transcript: string -> (string -> string) -> unit) =
        let files (root: string) =
            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            |> Array.map (fun file ->
                // The active host holds FileShare.None on its empty lock file. Its length tells
                // us its complete content without opening a conflicting shared read handle.
                let bytes = if FileInfo(file).Length = 0L then Array.empty<byte> else File.ReadAllBytes file
                Path.GetRelativePath(root, file), bytes)
            |> Map.ofArray
        let runHost zig name =
            let target = Path.Combine(directory, name)
            Directory.CreateDirectory target |> ignore
            let original = Path.GetDirectoryName source
            for file in Directory.GetFiles(original, "*", SearchOption.AllDirectories) do
                let destination = Path.Combine(target, Path.GetRelativePath(original, file))
                Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
                File.Copy(file, destination)
            let path = Path.Combine(target, Path.GetFileName source)
            use host = startWithEnvironment [ "NYRWVY_TEST_ID_SEED", seed ] zig [ "open"; "--snapshot"; path ]
            let errors = host.StandardError.ReadToEndAsync()
            let replies = ResizeArray<string * string>()
            let trees = ResizeArray<Map<string, byte[]>>()
            let stopped request =
                try host.StandardInput.Close() with :? IOException -> ()
                if not (host.WaitForExit(int silence.TotalMilliseconds)) then
                    host.Kill true
                    host.WaitForExit()
                let stderr = errors.GetAwaiter().GetResult()
                failwithf "The %s host closed stdout without answering. Snapshot: %s\nRequest: %s\nExit code: %d\nStderr:\n%s"
                    name path request host.ExitCode stderr
            let send request =
                try
                    host.StandardInput.WriteLine(request: string)
                    host.StandardInput.Flush()
                with :? IOException -> stopped request
                let reading = host.StandardOutput.ReadLineAsync()
                if not (reading.Wait silence) then
                    host.Kill true
                    failwithf "Host did not answer %s" request
                let reply = reading.Result
                // EOF can be a startup failure as well as a request crash.
                if isNull reply then stopped request
                replies.Add(request, comparable (reply.Replace(target, "<dir>")))
                use doc = parseDocument request
                if doc.RootElement.GetProperty("op").GetString() = "commit" then trees.Add(files target)
                reply
            try transcript path send
            finally
                try host.StandardInput.Close() with :? IOException -> ()
                if not (host.WaitForExit(int silence.TotalMilliseconds)) then
                    host.Kill true
                    host.WaitForExit()
            Assert.Equal(0, host.ExitCode)
            Assert.Equal(seedWarning, errors.Result.Trim())
            target, List.ofSeq replies, List.ofSeq trees
        let fsharpDir, expected, expectedTrees = runHost false "fsharp"
        let zigDir, actual, actualTrees = runHost true "zig"
        Assert.Equal(expected.Length, actual.Length)
        for (request, wanted), (actualRequest, found) in List.zip expected actual do
            Assert.Equal(request, actualRequest)
            Assert.Equal(wanted, found)
        for wanted, found in List.zip (expectedTrees @ [ files fsharpDir ]) (actualTrees @ [ files zigDir ]) do
            Assert.Equal<string list>(wanted |> Map.keys |> Seq.toList, found |> Map.keys |> Seq.toList)
            for KeyValue(name, bytes) in wanted do
                let actual = found[name]
                if bytes <> actual then
                    let offset = Seq.zip bytes actual |> Seq.tryFindIndex (fun (left, right) -> left <> right) |> Option.defaultValue (min bytes.Length actual.Length)
                    let excerpt (content: byte[]) =
                        let start = max 0 (offset - 80)
                        Text.Encoding.UTF8.GetString(content, start, min 240 (content.Length - start))
                    Assert.Fail($"File differs: {name} at byte {offset}\nF#: {excerpt bytes}\nZig: {excerpt actual}")
        fsharpDir, zigDir

    /// Whether the F# reply is a preflight rejection, which the Zig host must answer byte for byte.
    let internal isPreflightRejection (fsharp: string) = preflightCodes |> Set.exists (fun code -> fsharp.Contains $"\"code\":\"{code}\"")

    /// A body `depth` string-length nodes deep, and one of `items` + 1 nodes two deep.
    let internal nested (depth: int) = String.replicate depth """{"kind":"stringLength","value":""" + """{"kind":"unitLiteral"}""" + String.replicate depth "}"
    let internal wide (items: int) =
        let units = String.Join(",", List.replicate items """{"kind":"unitLiteral"}""")
        $"""{{"kind":"listConstruct","itemType":{{"kind":"primitive","primitive":"int64"}},"items":[{units}]}}"""

    /// `node` with each unit literal replaced by a list of its own size (131, 141, …), so the
    /// node-limit reply's largest-subtree paths pin the child order of the kind and its child count.
    let internal inflated (node: JsonNode) =
        let copy = node.DeepClone()
        let mutable next = 0
        let isUnit (child: JsonNode) =
            match child with
            | :? JsonObject as o -> o.Count = 1 && (match o["kind"] with :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> v.GetValue<string>() = "unitLiteral" | _ -> false)
            | _ -> false
        let list () =
            next <- next + 1
            JsonNode.Parse(wide (120 + 10 * next))
        let rec visit (current: JsonNode) =
            match current with
            | :? JsonObject as o ->
                for key in [ for property in o -> property.Key ] do
                    if isUnit o[key] then o[key] <- list () else visit o[key]
            | :? JsonArray as items ->
                for index in 0 .. items.Count - 1 do
                    if isUnit items[index] then items[index] <- list () else visit items[index]
            | _ -> ()
        visit copy
        copy

    let internal sameAnswers label (fsharp: string list) (zig: string list) (requests: string list) =
        Assert.Equal(requests.Length, zig.Length)
        List.zip3 requests fsharp zig |> List.iteri (fun index (request, expected, actual) ->
            if comparable expected <> comparable actual then
                failwithf "%s line %d: %s\n F#:  %s\n Zig: %s" label (index + 1) request expected actual)

    /// The ownership functions of the removed Bytes tests, created and committed with the F#
    /// libraries as the F# session did (so the seeded IDs are the same), written to `path` with the
    /// backend sidecar a new session writes.
    let internal writeOwnershipSnapshot (path: string) =
        let bytesType = TypeRef.Primitive PrimitiveType.Bytes
        let intType = TypeRef.Primitive PrimitiveType.Int64
        let listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
        let slot = ExpressionRecipe.ParameterSlot
        let mutable workspace = AgentWorkspace.create (Snapshot.empty (StableIdAllocator.newSnapshotId ())) |> unwrap
        let create name parameters resultType body =
            let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId workspace; DisplayName = name
                  TypeParameters = None
                  Parameters = parameters |> List.map (fun (name, t) -> { DisplayName = name; Type = t })
                  ResultType = resultType; RequiredEffects = []; Body = body }
            let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate workspace request |> unwrap
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit workspace proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(candidate, _) -> workspace <- candidate
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Rejected diagnostics -> failwithf "%A" diagnostics
            AgentWorkspace.listFunctions workspace |> List.find (fun fn -> fn.DisplayName = name)
        let value = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Value }
        let plus1 recipe = ExpressionRecipe.Int64WrappingAdd(recipe, ExpressionRecipe.Int64Literal 1L)
        // A tail-recursive fill: after the first copy, every turn writes into the same buffer.
        let fillLoop =
            create "fillLoop" [ "b", bytesType; "i", intType; "n", intType ] bytesType
                (ExpressionRecipe.Conditional(
                    ExpressionRecipe.Int64LessThan(slot 1, slot 2),
                    ExpressionRecipe.SelfCall [ 0, ExpressionRecipe.BytesSet(slot 0, slot 1, plus1 (slot 1)); 1, plus1 (slot 1); 2, slot 2 ],
                    slot 0))
        // The caller's buffer is unchanged by the loop it was passed to.
        let keepsArgument =
            create "keepsArgument" [ "b", bytesType ] (listOf bytesType)
                (ExpressionRecipe.ListConstruct(bytesType,
                    [ slot 0; ExpressionRecipe.DirectCall(fillLoop.Id, [ fillLoop.Parameters[0].Id, slot 0; fillLoop.Parameters[1].Id, ExpressionRecipe.Int64Literal 0L; fillLoop.Parameters[2].Id, ExpressionRecipe.Int64Literal 3L ]) ]))
        // A buffer stored into a list must not change when the loop updates it on a later turn.
        let snapshots =
            create "snapshots" [ "buf", bytesType; "acc", listOf bytesType; "i", intType ] (listOf bytesType)
                (ExpressionRecipe.Conditional(
                    ExpressionRecipe.Int64LessThan(slot 2, ExpressionRecipe.Int64Literal 3L),
                    ExpressionRecipe.SelfCall [ 0, ExpressionRecipe.BytesSet(slot 0, ExpressionRecipe.Int64Literal 0L, slot 2); 1, ExpressionRecipe.ListAppendItem(slot 1, slot 0); 2, plus1 (slot 2) ],
                    slot 1))
        // One buffer passed as two arguments of a tail self-call: updating one must not change the other.
        let twins =
            create "twins" [ "a", bytesType; "c", bytesType; "i", intType ] (listOf bytesType)
                (ExpressionRecipe.Conditional(
                    ExpressionRecipe.Int64LessThan(slot 2, ExpressionRecipe.Int64Literal 1L),
                    ExpressionRecipe.Bind(ExpressionRecipe.BytesSet(slot 0, ExpressionRecipe.Int64Literal 0L, ExpressionRecipe.Int64Literal 1L),
                        ExpressionRecipe.SelfCall [ 0, value; 1, value; 2, ExpressionRecipe.Int64Literal 1L ]),
                    ExpressionRecipe.ListConstruct(bytesType, [ ExpressionRecipe.BytesSet(slot 0, ExpressionRecipe.Int64Literal 0L, ExpressionRecipe.Int64Literal 5L); slot 1 ])))
        // The old value is read after the update: the update copies.
        let oldAndNew =
            create "oldAndNew" [ "b", bytesType ] (listOf bytesType)
                (ExpressionRecipe.Bind(ExpressionRecipe.BytesSet(slot 0, ExpressionRecipe.Int64Literal 0L, ExpressionRecipe.Int64Literal 7L),
                    ExpressionRecipe.ListConstruct(bytesType, [ value; slot 0 ])))
        // The same read-after-update on a buffer the invocation already owns (second loop turn): a wrong
        // in-place decision would show up in the result here, not only in the plan.
        let ownedOldAndNew =
            create "ownedOldAndNew" [ "b", bytesType; "i", intType ] (listOf bytesType)
                (ExpressionRecipe.Conditional(
                    ExpressionRecipe.Int64LessThan(slot 1, ExpressionRecipe.Int64Literal 1L),
                    ExpressionRecipe.SelfCall [ 0, ExpressionRecipe.BytesSet(slot 0, ExpressionRecipe.Int64Literal 1L, ExpressionRecipe.Int64Literal 1L); 1, plus1 (slot 1) ],
                    ExpressionRecipe.Bind(ExpressionRecipe.BytesSet(slot 0, ExpressionRecipe.Int64Literal 0L, ExpressionRecipe.Int64Literal 7L),
                        ExpressionRecipe.ListConstruct(bytesType, [ value; slot 0 ]))))
        // The old value returned through a conditional's merge block, which has no instruction of its
        // own: on an owned buffer (second turn) the update must still copy.
        let ownedMergeReturn =
            create "ownedMergeReturn" [ "b", bytesType; "i", intType ] bytesType
                (ExpressionRecipe.Conditional(
                    ExpressionRecipe.Int64LessThan(slot 1, ExpressionRecipe.Int64Literal 1L),
                    ExpressionRecipe.SelfCall [ 0, ExpressionRecipe.BytesSet(slot 0, ExpressionRecipe.Int64Literal 1L, ExpressionRecipe.Int64Literal 1L); 1, plus1 (slot 1) ],
                    ExpressionRecipe.Bind(ExpressionRecipe.BytesSet(slot 0, ExpressionRecipe.Int64Literal 0L, ExpressionRecipe.Int64Literal 7L),
                        ExpressionRecipe.Conditional(ExpressionRecipe.Int64LessThan(slot 1, ExpressionRecipe.Int64Literal 5L), slot 0, slot 0))))
        // Appending in a loop grows one buffer.
        let appendLoop =
            create "appendLoop" [ "b", bytesType; "i", intType ] bytesType
                (ExpressionRecipe.Conditional(
                    ExpressionRecipe.Int64LessThan(slot 1, ExpressionRecipe.Int64Literal 300L),
                    ExpressionRecipe.SelfCall [ 0, ExpressionRecipe.BytesAppend(slot 0, slot 1); 1, plus1 (slot 1) ],
                    slot 0))
        // A loop that reads its buffer beside the update: lowering must emit the read first, whatever
        // order the node IDs happen to have, so the update can still write in place. Several copies,
        // because IDs created within one millisecond are ordered by their random bits.
        let readerLoops =
            [ for k in 1..8 ->
                create $"readerLoop{k}" [ "b", bytesType; "i", intType; "n", intType; "acc", intType ] intType
                    (ExpressionRecipe.Conditional(
                        ExpressionRecipe.Int64LessThan(slot 1, slot 2),
                        ExpressionRecipe.SelfCall
                            [ 0, ExpressionRecipe.BytesSet(slot 0, slot 1, ExpressionRecipe.Int64Literal 1L)
                              1, plus1 (slot 1)
                              2, slot 2
                              3, ExpressionRecipe.Int64WrappingAdd(slot 3, ExpressionRecipe.BytesLength(slot 0)) ],
                        slot 3)) ]
        // The old value read by a function value invoked after the update, which forces the copy.
        let passBytes = create "passBytes" [ "b", bytesType ] bytesType (slot 0)
        create "invokesOld" [ "b", bytesType ] (listOf bytesType)
            (ExpressionRecipe.Bind(ExpressionRecipe.BytesSet(slot 0, ExpressionRecipe.Int64Literal 0L, ExpressionRecipe.Int64Literal 7L),
                ExpressionRecipe.ListConstruct(bytesType, [ value; ExpressionRecipe.FunctionInvoke(ExpressionRecipe.FunctionReference passBytes.Id, [ slot 0 ]) ])))
        |> ignore
        File.WriteAllText(path, AgentWorkspace.serializeCurrentSnapshot workspace |> unwrap)
        File.WriteAllText(path + ".backends.json", "{\"targets\": [\"csharp\"]}\n")

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
        writeOwnershipSnapshot ownership
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
                  use document = parseDocument (SnapshotCodec.serializeUnvalidated snapshot |> Result.defaultWith (failwithf "%A"))
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
                let requests = queries snapshot
                let code, errors, answers = run true [ "open"; "--snapshot"; snapshot; "--read-only" ] requests
                Assert.True((code = 0), errors)
                let fsharp = fsharpReplies snapshot requests
                fsharp |> List.iter (answered.Append >> ignore)
                sameAnswers (Path.GetFileName snapshot) fsharp answers requests
            for shown in [ "\"aborts\":[{"; "\"mayWait\":true"; "\"openObligations\":[{"; "\"frames\":[{"; "\"variants\":["; "\"slot\":\"field\""; "\"evidence\":[{"; "\"references\":[{"; "\"role\":\"patternBinding\""; "\"truncated\":true"; "\"outlineTruncated\":true"; "scopeDepth=1"; "\\\\uD83D\\\\u2026"; "ref "; "\"nodeTypes\":[{"; "\"copiedUpdates\":[{"; "\"kind\":\"ready\""; "\"code\":\"SemanticProjectionFailure\",\"message\":\"IncompleteSnapshot\"" ] do
                Assert.True(answered.ToString().Contains shown, $"No F# reply shows {shown}; the parity test does not cover it.")
            // `full` replies show every node kind's structured form.
            for row in File.ReadAllLines(Path.Combine(ZigModelReaderSupport.root (), "tools", "zig", "host", "graph_kinds.txt")) do
                if not (row.StartsWith "#") then
                    let shown = $"\"kind\":{{\"kind\":\"{row.Split('|')[1]}\""
                    Assert.True(answered.ToString().Contains shown, $"No F# reply shows {shown}; the parity test does not cover it."))

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host canonicalizes unordered input before answering queries`` () =
        withDirectory (fun dir ->
            let unordered = set [ "types"; "effects"; "capabilities"; "functions"; "obligations"; "nodes"; "fields"; "variants"; "operations"; "fieldBindings"; "implementations"; "arms"; "variantIds"; "requiredEffects" ]
            let mutable reversed = 0
            let sources = constructedSnapshots dir |> List.truncate 4
            Assert.NotEmpty sources
            for index, source in List.indexed sources do
                let doc = JsonNode.Parse(File.ReadAllText source)
                for node in ZigModelReaderSupport.containers doc do
                    match node with
                    | :? JsonObject as obj ->
                        for p in obj do
                            match p.Value with
                            | :? JsonArray as values when unordered.Contains p.Key && values.Count > 1 ->
                                let items = values |> Seq.map (fun item -> if isNull item then null else item.DeepClone()) |> Seq.rev |> Seq.toArray
                                values.Clear()
                                for item in items do values.Add item
                                reversed <- reversed + 1
                            | _ -> ()
                    | _ -> ()
                let path = Path.Combine(dir, $"unordered-{index}.json")
                File.WriteAllText(path, doc.ToJsonString(), Text.UTF8Encoding false)
                let requests = queries path
                let expected = fsharpReplies path requests
                let code, errors, actual = run true [ "open"; "--snapshot"; path; "--read-only" ] requests
                Assert.True((code = 0), errors)
                sameAnswers (Path.GetFileName path) expected actual requests
            Assert.True(reversed > 0, "No ID-keyed input collection was reordered."))

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
                let functions = (fst (declarationsOf fsharpSnapshot)).Functions
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
                let fsharp = fsharpReplies fsharpSnapshot requests |> List.map (fun reply -> reply.Replace(fsharpDir, "<dir>"))
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
                    Assert.True((shown fsharp = shown zig), $"{name}:\n F#:  {shown fsharp}\n Zig: {shown zig}")
                    let code, _, _ = fsharp
                    Assert.True((code = 2), $"{name}: the F# host started")
            // A legacy schema (9 to 20) reads only with F#; the Zig host names the migration instead.
            let legacy = Path.Combine(dir, "legacy.json")
            File.WriteAllText(legacy, SnapshotCodec.serializeCanonical (RecordCodec.deserialize (File.ReadAllText record) |> unwrap) |> unwrap)
            let code, errors, _ = run true [ "open"; "--snapshot"; legacy; "--read-only" ] []
            Assert.Equal(2, code)
            Assert.Contains("is a legacy schema, which the Zig host does not read", errors))

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
                exampleRecords () |> List.map (File.ReadAllText >> RecordCodec.deserialize >> unwrap)
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
                            match queriesFrom (originals |> List.collect (fun snapshot -> snapshot.Functions)) path with
                            | Some queries -> request @ queries
                            | None -> request
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
                let snapshot = recordFixture name snapshot
                let fingerprints = $"snapshot {SnapshotFingerprint.compute snapshot |> unwrap |> SnapshotFingerprint.value}\nsemantic {SemanticFingerprint.compute snapshot |> unwrap |> SemanticFingerprint.value}\n"
                roundTrip name (SnapshotCodec.serialize snapshot |> unwrap) (Some fingerprints)
            // What a commit of each example writes, and of its resolved form (the declarations as an
            // imported dependency, the functions local), as `serializeWithDependency` writes it.
            for index, path in List.indexed (snapshots (Path.Combine(dir, "json") |> fun d -> Directory.CreateDirectory d |> ignore; d)) do
                let snapshot = AgentWorkspace.create (readSnapshot path) |> unwrap |> AgentWorkspace.serializeCurrentSnapshot |> unwrap |> SnapshotCodec.deserialize |> unwrap
                plain $"example-{index}" snapshot
                let snapshot = recordFixture $"resolved-{index}" snapshot
                let dependencyId = StableIdAllocator.seededPayload "record-fixture:typed-dependency" (int64 index + 1L) |> StableId.createForTesting<SnapshotTag>
                let dependency = { snapshot with Id = dependencyId; ParentId = None; Functions = []; Obligations = [] }
                let local = { snapshot with Types = []; Effects = []; Capabilities = [] }
                roundTrip $"resolved-{index}" (SnapshotCodec.serializeResolved dependency local |> unwrap) None
            for index, snapshot in List.indexed (RecordCodecAllKindsTests.allKindSnapshots () @ [ (MilestoneZeroGraph.create ()).Snapshot ]) do
                plain $"kinds-{index}" snapshot
            for index, path in List.indexed (constructedSnapshots (Path.Combine(dir, "constructed") |> fun d -> Directory.CreateDirectory d |> ignore; d)) do
                plain $"constructed-{index}" (SnapshotCodec.deserialize (File.ReadAllText path) |> unwrap))

/// Its own class, so xunit runs it beside the other host parity tests.
module ZigHostCreateDecodeTests =
    open ZigHostParityTests

    /// A valid instance of each exact-ID recipe kind, built from tools/zig/host/recipe_kinds.txt
    /// (every allowed property present), and the requests that break it one way at a time.
    let internal recipeRequests () =
        let rows =
            File.ReadAllLines(Path.Combine(ZigModelReaderSupport.root (), "tools", "zig", "host", "recipe_kinds.txt"))
            |> Array.filter (fun line -> not (line.StartsWith "#"))
            |> Array.map (fun line -> let parts = line.Split('|') in parts[0], parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries), parts[4].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        let unit = JsonObject(dict [ "kind", JsonValue.Create "unitLiteral" :> JsonNode ])
        let int64Type () = JsonNode.Parse """{"kind":"primitive","primitive":"int64"}"""
        let valueOf (shape: string) (name: string) : JsonNode =
            match shape with
            | "recipe" | "optrecipe" -> unit.DeepClone()
            | "recipes" -> JsonArray(unit.DeepClone())
            | "id" -> JsonValue.Create $"zig-decode-{name}"
            | "type" -> int64Type ()
            | "types" -> JsonArray(int64Type ())
            | "int" | "int64" -> JsonValue.Create 0
            | "bool" -> JsonValue.Create true
            | "text" -> JsonValue.Create "text"
            | "bits" -> JsonValue.Create "3ff0000000000000"
            | "context" | "args" -> JsonNode.Parse """[{"parameterId":"zig-decode-parameter","value":{"kind":"unitLiteral"}}]"""
            | "capargs" -> JsonNode.Parse """[{"capabilityParameterId":"zig-decode-capability-parameter","value":{"kind":"unitLiteral"}}]"""
            | "fields" -> JsonNode.Parse """[{"fieldId":"zig-decode-field","value":{"kind":"unitLiteral"}}]"""
            | "mode" -> JsonValue.Create "any"
            | "slot" -> JsonValue.Create "value"
            | "selfargs" -> JsonNode.Parse """[{"slot":0,"value":{"kind":"unitLiteral"}}]"""
            | "impls" -> JsonNode.Parse """[{"operationId":"zig-decode-operation","functionId":"zig-decode-function"}]"""
            | "arms" -> JsonNode.Parse """[{"variantId":"zig-decode-variant","body":{"kind":"unitLiteral"}},{"variantIds":["a","b"],"body":{"kind":"unitLiteral"}}]"""
            | other -> failwith other
        let instance (kind: string, _, reads: string array) =
            let node = JsonObject()
            node["kind"] <- JsonValue.Create kind
            for read in reads do
                let name, shape = let at = read.IndexOf ':' in read.Substring(0, at), read.Substring(at + 1)
                if shape = "arms" then node["otherwise"] <- unit.DeepClone()
                node[name] <- valueOf shape name
            node
        let request (body: JsonNode) =
            $"""{{"id":"c","op":"simulateCreateFunction","displayName":"made","parameters":[],"resultType":{{"kind":"primitive","primitive":"unit"}},"requiredEffects":[],"body":{body.ToJsonString()}}}"""
        [ for (kind, allowed, reads) as row in rows do
              let valid = instance row
              yield request valid
              yield request (inflated valid)
              // Nested, so a rejection inside sits at a path below the body.
              yield request (JsonObject(dict [ "kind", JsonValue.Create "stringConcat" :> JsonNode; "left", unit.DeepClone(); "right", valid.DeepClone() ]))
              let changed (change: JsonObject -> unit) = let copy = valid.DeepClone().AsObject() in change copy; request copy
              yield changed (fun node -> node["zzz"] <- JsonValue.Create 1)
              for name in allowed do
                  if name <> "kind" then
                      yield changed (fun node -> node.Remove name |> ignore)
                      yield changed (fun node -> node[name] <- JsonValue.Create 3)
                      yield changed (fun node -> node[name] <- JsonValue.Create "s")
                      yield changed (fun node -> node[name] <- JsonArray(JsonValue.Create 3))
                      yield changed (fun node -> node[name] <- JsonObject())
              // A property named twice, which the parse keeps once.
              let text = request valid
              yield text.Replace("\"body\":{\"kind\":", "\"body\":{\"kind\":\"x\",\"kind\":")
          yield request (JsonObject(dict [ "kind", JsonValue.Create "noSuchKind" :> JsonNode ]))
          yield request (JsonValue.Create 3)
          yield request (JsonObject())
          // Named pattern references.
          for body in
              [ """{"kind":"bind","name":"x","value":{"kind":"unitLiteral"},"body":{"kind":"patternReference","name":"x"}}"""
                """{"kind":"bind","name":"x","value":{"kind":"unitLiteral"},"body":{"kind":"patternReference","name":"y"}}"""
                """{"kind":"patternReference","name":"y"}"""
                """{"kind":"bind","name":3,"value":{"kind":"unitLiteral"},"body":{"kind":"unitLiteral"}}"""
                """{"kind":"bind","name":"x","value":{"kind":"unitLiteral"},"body":{"kind":"patternReference","name":"x","slot":"value"}}"""
                """{"kind":"matchResult","okName":"o","errName":"e","input":{"kind":"unitLiteral"},"okBody":{"kind":"patternReference","name":"o"},"errBody":{"kind":"patternReference","name":"e"}}"""
                """{"kind":"matchOption","someName":"s","input":{"kind":"unitLiteral"},"someBody":{"kind":"patternReference","name":"s"},"noneBody":{"kind":"patternReference","name":"s"}}"""
                """{"kind":"matchUnion","input":{"kind":"unitLiteral"},"arms":[{"variantId":"v","body":{"kind":"bind","name":"b","value":{"kind":"unitLiteral"},"body":{"kind":"patternReference","name":"b"}}}],"otherwise":{"kind":"patternReference","name":"b"}}"""
                """{"kind":"patternReference","slot":"field","scopeDepth":0}"""
                """{"kind":"patternReference","slot":"field","field":"f","scopeDepth":0}"""
                """{"kind":"patternReference","slot":"other","scopeDepth":0}"""
                """{"kind":"float64Literal","bits":"3FF000000000000G"}"""
                """{"kind":"float64Literal","bits":"3ff00000"}"""
                """{"kind":"int64Literal","value":"+12"}"""
                """{"kind":"int64Literal","value":" 12"}"""
                """{"kind":"int64Literal","value":"99999999999999999999"}"""
                """{"kind":"int64Literal","value":1.5}"""
                """{"kind":"taskWait","tasks":{"kind":"unitLiteral"},"mode":"some"}"""
                """{"kind":"matchUnion","input":{"kind":"unitLiteral"},"arms":[{"variantIds":[],"body":{"kind":"unitLiteral"}}]}"""
                """{"kind":"matchUnion","input":{"kind":"unitLiteral"},"arms":[{"variantIds":[3],"body":{"kind":"unitLiteral"}}]}"""
                """{"kind":"matchUnion","input":{"kind":"unitLiteral"},"arms":[{"body":{"kind":"unitLiteral"}}]}"""
                """{"kind":"directCall","functionId":" ","arguments":[]}""" ] do
              yield request (JsonNode.Parse body)
          // Spelled with escapes, which the raw text check of `WireRecipeNames.resolve` does not see.
          let raw (body: string) = $"""{{"id":"c","op":"simulateCreateFunction","displayName":"made","parameters":[],"resultType":{{"kind":"primitive","primitive":"unit"}},"requiredEffects":[],"body":{body}}}"""
          yield raw """{"kind":"bind","n\u0061me":"x","value":{"kind":"unitLiteral"},"body":{"kind":"unitLiteral"}}"""
          yield raw """{"kind":"float64Literal","bits":"3ff000000000000\u00e9"}"""
          yield raw """{"kind":"int64Literal","value":"12\u0000"}"""
          yield raw """{"kind":"float64Literal","bits":"3ff000000000000\u0000"}"""
          yield raw """{"kind":"float64Literal","bits":"\u0000\u0000\u0000\u0000\u0000\u0000\u0000\u0000\u0000\u0000\u0000\u0000\u0000\u0000\u0000\u0000"}"""
          yield raw """{"kind":"stringLiteral","value":"a\ud800"}"""
          yield raw """{"kind":"stringLiteral","value":"\udc00b"}"""
          yield raw """{"kind":"stringLiteral","value":"\ud83d\ude00"}"""
          yield raw """{"kind":"float64Literal","bits":"3ff00000000000\ud800"}"""
          yield raw """{"kind":"int64Literal","value":"1\ud800"}"""
          yield raw """{"kind":"directCall","functionId":"f\ud800","arguments":[]}"""
          yield raw """{"kind":"bind","name":"x\ud800","value":{"kind":"unitLiteral"},"body":{"kind":"unitLiteral"}}"""
          yield raw """{"kind":"bind","name":"x","value":{"kind":"stringLiteral","value":"a\ud800"},"body":{"kind":"unitLiteral"}}"""
          yield raw """{"kind":"unitLiteral","x\ud800":1}"""
          yield raw """{"kind":"stringConcat","left":{"kind":"stringLiteral","value":"a"},"right":{"kind":"unitLiteral","\udc00":1}}"""
          yield raw """{"kind":"stringConcat","left":{"kind":"stringLiteral","value":"a\ud800"},"right":{"kind":"unitLiteral","\udc00":1}}"""
          yield raw """{"kind":"stringConcat","left":3,"right":{"kind":"unitLiteral","\udc00":1}}"""
          yield raw """{"kind":"bind","name":"x","value":{"kind":"unitLiteral","kind":"unitLiteral"},"body":{"kind":"unitLiteral"}}"""
          yield raw """{"kind":"bind","name":"x","name":"y","value":{"kind":"unitLiteral"},"body":{"kind":"unitLiteral"}}"""
          yield raw """{"kind":"stringConcat","left":3,"right":{"kind":"bind","name":"x","value":[{"a":1,"a":2}],"body":{"kind":"unitLiteral"}}}"""
          yield raw """{"kind":"bind","name":"x","value":{"kind":"unitLiteral","\ud800":1,"\udc00":2},"body":{"kind":"unitLiteral"}}"""
          yield raw """{"kind":"bind","name":1,"value":{"kind":"unitLiteral","\ud800":1},"body":{"kind":"unitLiteral"}}"""
          yield raw """{"kind":"bind","name":"x","x":1,"x":2,"value":{"kind":"unitLiteral","\ud800":1},"body":{"kind":"unitLiteral"}}"""
          yield raw """{"kind":"float64Literal","bits":"3ff00000\u00000000000"}""" ]

    /// Mutations of the request around the recipe.
    let private requestMutations () =
        let line (fields: string) = $"""{{"id":"c","op":"simulateCreateFunction",{fields}}}"""
        let ok = "\"displayName\":\"made\",\"parameters\":[],\"resultType\":{\"kind\":\"primitive\",\"primitive\":\"unit\"},\"requiredEffects\":[],\"body\":{\"kind\":\"unitLiteral\"}"
        let types =
            [ """{"kind":"function","parameters":[{"kind":"primitive","primitive":"int64"}],"resultType":{"kind":"primitive","primitive":"bool"}}"""
              """{"kind":"function","parameters":3,"resultType":{"kind":"primitive","primitive":"bool"}}"""
              """{"kind":"primitive","primitive":"char"}"""
              """{"kind":"named","typeId":"t","arguments":[{"kind":"primitive","primitive":"int64"}]}"""
              """{"kind":"named","typeId":""}"""
              """{"kind":"named","typeId":"t","arguments":"x"}"""
              """{"kind":"capability","capabilityId":"c"}"""
              """{"kind":"typeParameter","name":"T"}"""
              """{"kind":"typeParameter","name":" "}"""
              """{"kind":"typeParameter","typeParameterId":"t","displayName":"T"}"""
              """{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"primitive","primitive":"int64"}]}"""
              """{"kind":"builtinGeneric","generic":"vector","arguments":[]}"""
              """{"kind":"other"}"""
              """{"primitive":"int64"}"""
              "3" ]
        [ yield line ok
          yield line (ok + ",\"extra\":1")
          yield line (ok.Replace("\"body\":{\"kind\":\"unitLiteral\"}", "\"body\":3"))
          yield line (ok.Replace("\"parameters\":[]", "\"parameters\":3"))
          yield line (ok.Replace("\"parameters\":[]", "\"parameters\":[{\"displayName\":\"x\"}]"))
          yield line (ok.Replace("\"parameters\":[]", "\"parameters\":[{\"displayName\":3,\"type\":{\"kind\":\"primitive\",\"primitive\":\"int64\"}}]"))
          yield line (ok.Replace("\"parameters\":[]", "\"parameters\":[3]"))
          yield line (ok.Replace("\"requiredEffects\":[]", "\"requiredEffects\":[3]"))
          yield line (ok.Replace("\"requiredEffects\":[]", "\"requiredEffects\":[\" \"]"))
          yield line (ok.Replace("\"displayName\":\"made\"", "\"displayName\":3"))
          yield line (ok + ",\"exported\":\"yes\"")
          yield line (ok + ",\"mustNotWait\":1")
          yield line (ok + ",\"expectedSnapshotId\":\"\"")
          yield line (ok + ",\"expectedSnapshotId\":3")
          yield line (ok + ",\"typeParameters\":[3]")
          yield line (ok + ",\"typeParameters\":[\" \"]")
          yield line (ok + ",\"typeParameters\":\"T\"")
          for t in types do
              yield line (ok.Replace("\"resultType\":{\"kind\":\"primitive\",\"primitive\":\"unit\"}", "\"resultType\":" + t))
              yield line (ok.Replace("\"parameters\":[]", "\"parameters\":[{\"displayName\":\"x\",\"type\":" + t + "}]")) ]

    /// Requests that reach each preflight diagnostic (step 3c-3a), against the session's declarations.
    let private preflightRequests (declarations: FixtureDeclarations) (snapshotId: string) =
        let line (fields: string) (body: string) = $"""{{"id":"p","op":"simulateCreateFunction",{fields},"body":{body}}}"""
        let unitType = """{"kind":"primitive","primitive":"unit"}"""
        let int64Type = """{"kind":"primitive","primitive":"int64"}"""
        let unit = """{"kind":"unitLiteral"}"""
        let typeParameter name = $"""{{"kind":"typeParameter","name":"{name}"}}"""
        let listOf itemType = $"""{{"kind":"listEmpty","itemType":{itemType}}}"""
        let signature (parameters: string list) resultType (effects: string list) =
            $""""displayName":"made","parameters":[{String.Join(",", parameters)}],"resultType":{resultType},"requiredEffects":[{String.Join(",", effects |> List.map (fun e -> "\"" + e + "\""))}]"""
        let parameter name (t: string) = $"""{{"displayName":"{name}","type":{t}}}"""
        let ok = signature [] unitType []
        let stale = ",\"expectedSnapshotId\":\"zig-stale-base\""
        let existing = declarations.Functions |> List.tryHead |> Option.map (fun f -> f.DisplayName) |> Option.defaultValue "none"
        let effect = declarations.Effects |> List.tryHead |> Option.map (fun e -> StableId.value e.Id) |> Option.defaultValue "zig-no-effect"
        [ yield line (ok + stale) unit
          yield line (ok + $",\"expectedSnapshotId\":\"{snapshotId}\"") unit
          // The type-parameter check runs before the stale-base check; the stale-base check before the blank name.
          yield line (signature [] (typeParameter "T") [] + stale) unit
          yield line (ok.Replace("\"made\"", "\" \"")) unit
          yield line (ok.Replace("\"made\"", "\" \"") + stale) unit
          yield line (ok.Replace("\"made\"", $"\"{existing}\"")) unit
          yield line (signature [ parameter " " int64Type ] unitType []) unit
          yield line (signature [ parameter "x" (typeParameter "T") ] unitType [] + ",\"typeParameters\":[\"T\",\"T\"]") unit
          yield line (signature [] (typeParameter "T") []) unit
          yield line ok (listOf (typeParameter "T"))
          // Two undeclared type parameters: the first in the F# constructor's order is named.
          yield line ok $"""{{"kind":"resultOk","errorType":{typeParameter "U"},"value":{listOf (typeParameter "V")}}}"""
          yield line ok $"""{{"kind":"exceptionRethrow","value":{listOf (typeParameter "V")},"resultType":{typeParameter "U"}}}"""
          yield line ok $"""{{"kind":"matchUnion","arms":[{{"variantId":"v","body":{listOf (typeParameter "A")}}}],"input":{listOf (typeParameter "I")}}}"""
          yield line ok $"""{{"kind":"taskStart","scope":{unit},"workerFunctionId":"w","context":[{{"parameterId":"p","value":{listOf (typeParameter "C")}}}],"item":{listOf (typeParameter "I")}}}"""
          yield line (signature [ parameter "x" int64Type ] unitType [] + ",\"typeParameters\":[\"T\"]") unit
          yield line (signature [] """{"kind":"named","typeId":"zig-no-such-type"}""" []) unit
          yield line (signature [ parameter "x" """{"kind":"capability","capabilityId":"zig-no-such-capability"}""" ] unitType []) unit
          for t in declarations.Types |> List.truncate 1 do
              let arguments = String.Join(",", List.replicate (t.TypeParameters.Length + 1) int64Type)
              yield line (signature [] $"""{{"kind":"named","typeId":"{StableId.value t.Id}","arguments":[{arguments}]}}""" []) unit
          yield line (signature [] """{"kind":"builtinGeneric","generic":"oneShot","arguments":[]}""" []) unit
          yield line (signature [ parameter "x" $"""{{"kind":"builtinGeneric","generic":"result","arguments":[{int64Type}]}}""" ] unitType []) unit
          yield line (signature [] unitType [ effect; effect ]) unit
          yield line (signature [] unitType [ "zig-no-such-effect" ]) unit
          yield line ok (nested 33)
          yield line ok (nested 32)
          yield line ok (wide 256)
          yield line ok (wide 255) ]

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host decodes simulateCreateFunction requests as the F# host does`` () =
        withDirectory (fun dir ->
            let path = Path.Combine(dir, "decode.json")
            File.WriteAllText(path, SnapshotCodec.serialize (MilestoneZeroGraph.create ()).Snapshot |> unwrap)
            let declarations, snapshotId = declarationsOf path
            let requests = recipeRequests () @ requestMutations () @ preflightRequests declarations snapshotId
            let expected, answers = seededPair "create-decode" path requests
            let mutable rejected = 0
            let mutable preflight = 0
            let mismatches = ResizeArray<string>()
            let answered = Text.StringBuilder()
            for request, (fsharp, zig) in List.zip requests (List.zip expected answers) do
                answered.Append(fsharp) |> ignore
                if fsharp.Contains "\"code\":\"ProtocolError\"" then rejected <- rejected + 1
                if isPreflightRejection fsharp then preflight <- preflight + 1
                if not (createReplyMatches request fsharp zig) then mismatches.Add $"{request}\n F#:  {fsharp}\n Zig: {zig}"
            let shownText = answered.ToString()
            for shown in Set.toList (Set.remove "FunctionCreationTypeParameterNameBlank" preflightCodes) @ [ "Largest subtrees within the limit" ] do
                Assert.True(shownText.Contains shown, $"No F# reply shows {shown}; the decode parity test does not cover it.")
            Assert.True((mismatches.Count = 0), $"{mismatches.Count} mismatches:\n" + String.Join("\n", mismatches |> Seq.truncate 12))
            Assert.True(rejected > 1000, $"Only {rejected} requests are decode rejections.")
            Assert.True(preflight > 100, $"Only {preflight} requests are preflight rejections."))

/// Its own class, so xunit runs it beside the other host parity tests.
module ZigHostLoneSurrogateTests =
    open ZigHostParityTests

    /// Each query with a lone surrogate escape at the start of one of its strings (values and property names).
    let private spoiled (requests: string list) =
        [ for request in requests do
              let mutable inString = false
              let mutable escaped = false
              for index in 0 .. request.Length - 1 do
                  let c = request[index]
                  if escaped then escaped <- false
                  elif c = '\\' then escaped <- true
                  elif c = '"' then
                      if not inString then
                          let escape = if index % 2 = 0 then "\\ud800" else "\\udc00"
                          yield request.Insert(index + 1, escape)
                      inString <- not inString ]

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host answers strings with a lone surrogate escape as the F# host does`` () =
        withDirectory (fun dir ->
            let path = Path.Combine(dir, "lone.json")
            File.WriteAllText(path, SnapshotCodec.serialize (MilestoneZeroGraph.create ()).Snapshot |> unwrap)
            let requests = spoiled (queries path)
            let code, errors, answers = run true [ "open"; "--snapshot"; path; "--read-only" ] requests
            Assert.True((code = 0), errors)
            let fsharp = fsharpReplies path requests |> List.map sessionErrorCode
            sameAnswers "lone surrogates" fsharp (answers |> List.map sessionErrorCode) requests
            Assert.Contains(fsharp, fun reply -> reply.Contains "\"code\":\"SessionError\""))

/// Its own class, so xunit runs it beside the other host parity tests.
module ZigHostCreateByNameTests =
    open ZigHostParityTests

    let private unit () = JsonNode.Parse """{"kind":"unitLiteral"}"""
    let private int64Type () = JsonNode.Parse """{"kind":"primitive","primitive":"int64"}"""

    /// Requests for a session's declarations: a valid instance of each by-name recipe kind (from
    /// tools/zig/host/recipe_kinds_byname.txt), broken one way at a time, and the signature around it.
    let internal requests (declarations: FixtureDeclarations) (snapshotId: string) =
        let first fallback items = items |> List.tryHead |> Option.defaultValue fallback
        let products = declarations.Types |> List.choose (fun t -> match t.Kind with SemanticTypeKind.Product fields when not fields.IsEmpty -> Some(t.DisplayName, fields.Head.DisplayName) | _ -> None)
        // Prefer members with members of their own; fall back to any, so names still resolve partway.
        let unions =
            declarations.Types
            |> List.collect (fun t -> match t.Kind with SemanticTypeKind.Union variants -> variants |> List.map (fun v -> t.DisplayName, v.DisplayName, v.Fields |> List.tryHead |> Option.map (fun f -> f.DisplayName)) | _ -> [])
            |> List.sortBy (fun (_, _, field) -> Option.isNone field)
            |> List.map (fun (t, v, field) -> t, v, Option.defaultValue "noField" field)
        let product, productField = first ("NoProduct", "noField") products
        let union, variant, variantField = first ("NoUnion", "noVariant", "noField") unions
        let functions = declarations.Functions |> List.filter (fun f -> not f.Parameters.IsEmpty)
        let fn, fnParameter = functions |> List.map (fun f -> f.DisplayName, f.Parameters.Head.DisplayName) |> first ("noFunction", "noParameter")
        let operations =
            declarations.Capabilities
            |> List.collect (fun c -> c.Operations |> List.map (fun o -> c.DisplayName, o.DisplayName, o.Parameters |> List.tryHead |> Option.map (fun p -> p.DisplayName)))
            |> List.sortBy (fun (_, _, parameter) -> Option.isNone parameter)
            |> List.map (fun (c, o, parameter) -> c, o, Option.defaultValue "noParameter" parameter)
        let capability, operation, operationParameter = first ("NoCapability", "noOperation", "noParameter") operations
        let effect = declarations.Effects |> List.map (fun e -> e.DisplayName) |> first "NoEffect"
        let node (text: string) = JsonNode.Parse text
        let str (text: string) : JsonNode = JsonValue.Create text
        let valueOf (kind: string) (shape: string) : JsonNode =
            match shape with
            | "recipe" | "optrecipe" -> unit ()
            | "recipes" -> JsonArray(unit ())
            | "type" -> JsonObject(dict [ "kind", str "named"; "name", str product ])
            | "types" -> JsonArray(int64Type ())
            | "int" | "int64" -> JsonValue.Create 0
            | "bool" -> JsonValue.Create true
            | "text" -> str "text"
            | "bits" -> str "3ff0000000000000"
            | "mode" -> str "any"
            | "pname" -> str "x"
            | "fnref" -> str fn
            | "ctx" -> JsonArray(JsonObject(dict [ "parameter", str fnParameter; "value", unit () ]))
            | "capsel" -> str capability
            | "capop" -> str operation
            | "typesel" -> str (if kind = "unionConstruct" || kind = "matchUnion" then union else product)
            | "variant" -> str variant
            | "field" -> str productField
            | "pfields" -> JsonArray(JsonObject(dict [ "field", str productField; "value", unit () ]))
            | "ufields" -> JsonArray(JsonObject(dict [ "field", str variantField; "value", unit () ]))
            | "scapargs" -> JsonArray(JsonObject(dict [ "parameter", str operationParameter; "value", unit () ]))
            | "simpls" -> JsonArray(JsonObject(dict [ "operation", str operation; "function", str fn ]))
            | "sarms" -> JsonArray(JsonObject(dict [ "variant", str variant; "body", unit () ]), JsonObject(dict [ "variants", JsonArray(str variant) :> JsonNode; "body", unit () ]))
            | other -> failwith other
        let rows file =
            File.ReadAllLines(Path.Combine(ZigModelReaderSupport.root (), "tools", "zig", "host", file))
            |> Array.filter (fun line -> not (line.StartsWith "#"))
            |> Array.map (fun line -> let parts = line.Split('|') in parts[0], parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries), parts[4].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        let byName = rows "recipe_kinds_byname.txt"
        let simple = set [ "recipe"; "recipes"; "optrecipe"; "int"; "int64"; "bool"; "text"; "bits"; "mode" ]
        let shared =
            rows "recipe_kinds.txt"
            |> Array.filter (fun (kind, _, reads) ->
                not (byName |> Array.exists (fun (k, _, _) -> k = kind))
                && reads |> Array.forall (fun read -> simple.Contains(read.Split(':')[1])))
        let instance (kind: string, _, reads: string array) =
            let built = JsonObject()
            built["kind"] <- str kind
            for read in reads do
                let parts = read.Split(':')
                if parts[1] = "sarms" then built["otherwise"] <- unit ()
                built[parts[0]] <- valueOf kind parts[1]
            built
        let signature = """"parameters":[{"displayName":"x","type":{"kind":"primitive","primitive":"int64"}},{"displayName":"y","type":{"kind":"primitive","primitive":"unit"}}],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[]"""
        let line (fields: string) = $"""{{"id":"n","op":"simulateCreateFunctionByName","displayName":"made",{fields}}}"""
        let request (body: JsonNode) = line $"""{signature},"body":{body.ToJsonString()}"""
        let raw (body: string) = line $"""{signature},"body":{body}"""
        [ for (kind, allowed, reads) as row in byName do
              let valid = instance row
              yield request valid
              yield request (inflated valid)
              yield request (JsonObject(dict [ "kind", str "stringConcat"; "left", unit (); "right", valid.DeepClone() ]))
              let changed (change: JsonObject -> unit) = let copy = valid.DeepClone().AsObject() in change copy; request copy
              yield changed (fun built -> built["zzz"] <- JsonValue.Create 1)
              for name in allowed do
                  if name <> "kind" then
                      yield changed (fun built -> built.Remove name |> ignore)
                      yield changed (fun built -> built[name] <- JsonValue.Create 3)
                      yield changed (fun built -> built[name] <- str "s")
                      yield changed (fun built -> built[name] <- JsonArray(JsonValue.Create 3))
                      yield changed (fun built -> built[name] <- JsonObject())
                      match valid[name] with
                      | :? JsonValue as text when text.GetValueKind() = JsonValueKind.String ->
                          let name' = text.GetValue<string>()
                          yield changed (fun built -> built[name] <- str (name' + "x"))
                          yield changed (fun built -> built[name] <- JsonObject(dict [ "name", str name' ]))
                          yield changed (fun built -> built[name] <- JsonObject(dict [ "name", str name'; "ownership", str "local" ]))
                          yield changed (fun built -> built[name] <- JsonObject(dict [ "name", str name'; "ownership", str "elsewhere" ]))
                          yield changed (fun built -> built[name] <- JsonObject(dict [ "name", str name'; "ownership", str "imported" ]))
                          yield changed (fun built -> built[name] <- JsonObject(dict [ "id", str "zig-no-such-id" ]))
                      | :? JsonArray as items when items.Count > 0 && (items[0] :? JsonObject) ->
                          for key in [ for property in items[0].AsObject() -> property.Key ] do
                              yield changed (fun built -> let item = built[name].AsArray() in item[0].AsObject()[key] <- str "Zz")
                              yield changed (fun built -> let item = built[name].AsArray() in item[0].AsObject().Remove(key) |> ignore)
                      | _ -> ()
          for row in shared do
              yield request (instance row)
              yield request (inflated (instance row))
          for body in
              [ """{"kind":"parameterSlot","index":0}"""
                """{"kind":"selfCall","arguments":[]}"""
                """{"kind":"noSuchKind"}"""
                """{"kind":"parameter","name":"z"}"""
                """{"kind":"patternReference","slot":"value","scopeDepth":0}"""
                """{"kind":"patternReference","slot":"error","scopeDepth":0,"type":"T"}"""
                """{"kind":"patternReference","slot":"other","scopeDepth":0}"""
                """{"kind":"patternReference","scopeDepth":0}"""
                $"""{{"kind":"patternReference","slot":"field","scopeDepth":0,"type":"{union}","variant":"{variant}","field":"{variantField}"}}"""
                $"""{{"kind":"patternReference","slot":"field","scopeDepth":0,"type":"{union}","variant":"{variant}","field":"zz"}}"""
                $"""{{"kind":"patternReference","slot":"field","scopeDepth":0,"type":"{product}","variant":"{variant}","field":"zz"}}"""
                $"""{{"kind":"fieldAccess","source":{{"kind":"unitLiteral"}},"type":"{union}","field":"f"}}"""
                """{"kind":"directCall","function":"made","arguments":[{"parameter":"x","value":{"kind":"unitLiteral"}}]}"""
                """{"kind":"directCall","function":"made","arguments":[],"keepUnlisted":true}"""
                """{"kind":"directCall","function":"made","arguments":[{"parameter":"q","value":{"kind":"unitLiteral"}}]}"""
                """{"kind":"directCall","function":"made","arguments":[],"keepUnlisted":3}"""
                $"""{{"kind":"directCall","function":"{fn}","arguments":[{{"parameter":"{fnParameter}","value":{{"kind":"unitLiteral"}}}}]}}"""
                $"""{{"kind":"directCall","function":"{fn}","arguments":[],"keepUnlisted":true}}"""
                $"""{{"kind":"directCall","function":{{"name":"{fn}"}},"arguments":[{{"parameter":"zz","value":{{"kind":"unitLiteral"}}}}]}}"""
                """{"kind":"directCall","function":{"name":"made"},"arguments":[]}"""
                """{"kind":"directCall","function":3,"arguments":[]}"""
                """{"kind":"functionReference","function":"made"}"""
                """{"kind":"listMap","source":{"kind":"unitLiteral"},"function":"made"}"""
                $"""{{"kind":"listMap","source":{{"kind":"unitLiteral"}},"function":"{fn}","context":[{{"parameter":"zz","value":{{"kind":"unitLiteral"}}}}]}}"""
                $"""{{"kind":"listMap","source":{{"kind":"unitLiteral"}},"function":"{fn}","context":3}}"""
                $"""{{"kind":"listEmpty","itemType":{{"kind":"named","name":"Zz","arguments":[3]}}}}"""
                $"""{{"kind":"listEmpty","itemType":{{"kind":"named","name":{{"name":"{product}"}}}}}}"""
                $"""{{"kind":"listEmpty","itemType":{{"kind":"capability","name":"{capability}"}}}}"""
                """{"kind":"listEmpty","itemType":{"kind":"typeParameter","name":" "}}"""
                """{"kind":"listEmpty","itemType":{"kind":"typeParameter","typeParameterId":"t"}}"""
                """{"kind":"listEmpty","itemType":{"kind":"named","typeId":"t"}}"""
                """{"kind":"listEmpty","itemType":{"kind":"other"}}"""
                """{"kind":"listEmpty","itemType":{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"named","name":"Zz"}]}}"""
                """{"kind":"bind","name":"v","value":{"kind":"unitLiteral"},"body":{"kind":"patternReference","name":"v"}}""" ] do
              yield raw body
          // The signature around a valid body.
          let ok = """{"kind":"unitLiteral"}"""
          let around (fields: string) = line $"""{fields},"body":{ok}"""
          yield around signature
          yield (around signature).Replace("\"displayName\":\"made\"", $"\"displayName\":\"{fn}\"")
          yield (around signature).Replace("\"displayName\":\"made\"", "\"displayName\":3")
          for t in [ $"""{{"kind":"named","name":"{product}"}}"""; """{"kind":"named","name":"Zz"}"""; $"""{{"kind":"capability","name":"{capability}"}}"""; """{"kind":"capability","name":"Zz"}"""; """{"kind":"typeParameter","name":"T"}"""; """{"kind":"named","typeId":"x"}""" ] do
              yield around $""""parameters":[{{"displayName":"x","type":{t}}}],"resultType":{t},"requiredEffects":[]"""
          for effects in [ $"""["{effect}"]"""; """["Zz"]"""; $"""[{{"name":"{effect}"}}]"""; """[3]"""; """[{}]"""; """3""" ] do
              yield around $""""parameters":[],"resultType":{{"kind":"primitive","primitive":"unit"}},"requiredEffects":{effects}"""
          yield line signature
          let twice = """"parameters":[{"displayName":"x","type":{"kind":"primitive","primitive":"unit"}},{"displayName":"x","type":{"kind":"primitive","primitive":"unit"}}],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[]"""
          yield line $"""{twice},"body":{{"kind":"parameter","name":"x"}}"""
          yield line $"""{twice},"body":{{"kind":"directCall","function":"made","arguments":[],"keepUnlisted":true}}"""
          yield line $"""{twice},"body":{{"kind":"directCall","function":"made","arguments":[{{"parameter":"x","value":{{"kind":"unitLiteral"}}}}]}}"""
          yield line $"""{signature},"body":{ok},"bodyFile":"b.json" """
          yield line $"""{signature},"bodyFile":3"""
          yield line $"""{signature},"bodyFile":"nope.json" """
          yield line $"""{signature},"bodyFile":" " """
          yield line $"""{signature},"body":{ok},"expectedSnapshotId":" " """
          yield line $"""{signature},"body":{ok},"exported":3"""
          yield line $"""{signature},"body":{ok},"typeParameters":["T"]"""
          yield line $"""{signature},"body":{ok},"zzz":1"""
          // The preflight (step 3c-3a) by name; the signature's two parameters are x: int64 and y: unit.
          let typeParameter name = $"""{{"kind":"typeParameter","name":"{name}"}}"""
          let listOf itemType = $"""{{"kind":"listEmpty","itemType":{itemType}}}"""
          let signed (parameters: string) (resultType: string) (effects: string) = $""""parameters":{parameters},"resultType":{resultType},"requiredEffects":{effects}"""
          let unitType = """{"kind":"primitive","primitive":"unit"}"""
          let int64Type = """{"kind":"primitive","primitive":"int64"}"""
          let stale = ",\"expectedSnapshotId\":\"zig-stale-base\""
          yield line $"""{signature}{stale},"body":{ok}"""
          yield line $"""{signature},"expectedSnapshotId":"{snapshotId}","body":{ok}"""
          yield line $"""{signed "[]" (typeParameter "T") "[]"}{stale},"body":{ok}"""
          yield (around signature).Replace("\"displayName\":\"made\"", "\"displayName\":\" \"")
          yield around (signed $"""[{{"displayName":" ","type":{int64Type}}}]""" unitType "[]")
          yield around (signed $"""[{{"displayName":"x","type":{typeParameter "T"}}}]""" unitType "[]" + ",\"typeParameters\":[\"T\",\"T\"]")
          yield around (signed "[]" (typeParameter "T") "[]")
          yield line $"""{signature},"body":{listOf (typeParameter "T")}"""
          // Two undeclared type parameters, named in the F# constructor's order (source before fields; scope, context, item).
          yield line $"""{signature},"body":{{"kind":"productUpdate","type":"{product}","fields":[{{"field":"{productField}","value":{listOf (typeParameter "V")}}}],"source":{listOf (typeParameter "U")}}}"""
          yield line $"""{signature},"body":{{"kind":"taskStart","worker":"{fn}","scope":{ok},"context":[{{"parameter":"{fnParameter}","value":{listOf (typeParameter "C")}}}],"item":{listOf (typeParameter "I")}}}"""
          yield around (signed $"""[{{"displayName":"x","type":{int64Type}}}]""" unitType "[]" + ",\"typeParameters\":[\"T\"]")
          for t in declarations.Types |> List.filter (fun t -> t.DisplayName = product) |> List.truncate 1 do
              let arguments = String.Join(",", List.replicate (t.TypeParameters.Length + 1) int64Type)
              yield around (signed "[]" $"""{{"kind":"named","name":"{product}","arguments":[{arguments}]}}""" "[]")
          yield around (signed "[]" """{"kind":"builtinGeneric","generic":"oneShot","arguments":[]}""" "[]")
          yield around (signed "[]" unitType $"""["{effect}","{effect}"]""")
          yield line $"""{signature},"body":{nested 33}"""
          yield line $"""{signature},"body":{nested 32}"""
          yield line $"""{signature},"body":{wide 256}"""
          // The kept parameter slot counts as a node.
          yield line $"""{signature},"body":{{"kind":"directCall","function":"made","arguments":[{{"parameter":"x","value":{wide 255}}}],"keepUnlisted":true}}""" ]

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host decodes simulateCreateFunctionByName requests as the F# host does`` () =
        withDirectory (fun dir ->
            let mutable rejected = 0
            let mutable preflight = 0
            let mismatches = ResizeArray<string>()
            let answered = Text.StringBuilder()
            for snapshot in snapshots dir do
                let lines = declarationsOf snapshot ||> requests
                let expected, answers = seededPair "create-by-name" snapshot lines
                for request, (fsharp, zig) in List.zip lines (List.zip expected answers) do
                    answered.Append(fsharp) |> ignore
                    if fsharp.Contains "\"code\":\"ProtocolError\"" then rejected <- rejected + 1
                    if isPreflightRejection fsharp then preflight <- preflight + 1
                    if not (createReplyMatches request fsharp zig) then mismatches.Add $"{Path.GetFileName snapshot}: {request}\n F#:  {fsharp}\n Zig: {zig}"
            let shownText = answered.ToString().Replace("\\u0027", "'")
            // Of the preflight, a blank type-parameter name, a taken display name and an unknown effect, type or capability ID are decode rejections by name.
            let preflightShown = Set.toList (Set.difference preflightCodes (set [ "FunctionCreationTypeParameterNameBlank"; "FunctionCreationRequiredEffectNotFound"; "FunctionCreationDuplicateDisplayName" ]))
            for shown in [ "Unknown function '"; "Unknown type '"; "Unknown capability '"; "Unknown effect '"; "Unknown field on "; "Unknown variant on "; "Unknown field on variant "; "Unknown operation on "; "Unknown parameter on operation "; "Unknown parameter on "; "Unknown new function parameter"; " Available: "; "is not a product type"; "is not a union type"; "is the function being created"; "keepUnlisted applies only"; "No current function matches the explicit declaration selector"; "Selector ownership must be local or imported."; "Unsupported symbolic expression recipe kind"; "Unsupported symbolic type kind"; "already exists. Choose a unique name"; "Pass exactly one of body and bodyFile."; "Ambiguous new function parameter 'x'."; "Ambiguous parameter on made 'x'."; "Current candidates (advice only)"; "SimulationRejected"; "Largest subtrees within the limit" ] @ preflightShown do
                Assert.True(shownText.Contains shown, $"No F# reply shows {shown}; the by-name parity test does not cover it.")
            Assert.True((mismatches.Count = 0), $"{mismatches.Count} mismatches:\n" + String.Join("\n", mismatches |> Seq.truncate 12))
            Assert.True(rejected > 1000, $"Only {rejected} requests are decode rejections.")
            Assert.True(preflight > 100, $"Only {preflight} requests are preflight rejections."))

/// Its own class, so xunit runs it beside the other host parity tests.
module ZigHostCreateInferenceTests =
    open ZigHostParityTests

    let private id<'Kind> value = StableId.createForTesting<'Kind> value
    let private field name (fieldType: TypeRef) : SemanticField = { Id = id<FieldTag> ("zig-" + name); DisplayName = name.Substring(name.LastIndexOf '-' + 1); Type = fieldType }
    let private variant name fields : SemanticVariant = { Id = id<VariantTag> ("zig-" + name); DisplayName = name.Substring(name.LastIndexOf '-' + 1); Fields = fields }
    let private declared name typeParameters kind : SemanticType = { Id = id<TypeTag> ("zig-" + name); DisplayName = name; Exported = true; TypeParameters = typeParameters; Kind = kind }
    let private int64T = TypeRef.Primitive PrimitiveType.Int64
    let private t name = id<TypeParameterTag> ("zig-t-" + name)

    /// Declarations the inference diagnostics name: a product, generic products, a union with a
    /// Float64 field, a generic union, a recursive product and a function with a parameter.
    let internal snapshot () =
        let types =
            [ declared "Point" [] (SemanticTypeKind.Product [ field "point-x" int64T; field "point-y" int64T ])
              declared "Box" [ t "box", "T" ] (SemanticTypeKind.Product [ field "box-item" (TypeRef.TypeParameter(t "box")) ])
              declared "Pair" [ t "pair", "T" ] (SemanticTypeKind.Product [ field "pair-first" (TypeRef.TypeParameter(t "pair")); field "pair-second" (TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ TypeRef.TypeParameter(t "pair") ])) ])
              declared "Shape" [] (SemanticTypeKind.Union [ variant "circle" [ field "circle-radius" (TypeRef.Primitive PrimitiveType.Float64) ]; variant "dot" []; variant "square" [ field "square-side" int64T ] ])
              declared "Maybe" [ t "maybe", "T" ] (SemanticTypeKind.Union [ variant "just" [ field "just-value" (TypeRef.TypeParameter(t "maybe")) ]; variant "nothing" [] ])
              declared "Tree" [] (SemanticTypeKind.Product [ field "tree-label" (TypeRef.Primitive PrimitiveType.String); field "tree-children" (TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ TypeRef.Named(id<TypeTag> "zig-Tree", []) ])) ]) ]
        let twice: SemanticFunction =
            { MustNotWait = false; Exported = true; Id = id<FunctionTag> "zig-twice"; DisplayName = "twice"; TypeParameters = []
              Parameters = [ { Id = id<ParameterTag> "zig-twice-n"; DisplayName = "n"; Type = int64T } ]
              ResultType = TypeRef.Primitive PrimitiveType.Bool; RequiredEffects = []
              Body = { Nodes = [ { Id = id<GraphNodeTag> "zig-twice-body"; Kind = SemanticNodeKind.BoolLiteral true } ]; ResultNodeId = id<GraphNodeTag> "zig-twice-body" } }
        let unitT = TypeRef.Primitive PrimitiveType.Unit
        let stringT = TypeRef.Primitive PrimitiveType.String
        let resultT = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ int64T; stringT ])
        let scopeT = TypeRef.BuiltinGeneric(BuiltinGenericType.TaskScope, [])
        let exceptionT = TypeRef.BuiltinGeneric(BuiltinGenericType.Exception, [])
        let effectId = id<EffectTag> "zig-io"
        let make name inputs resultType effects typeParameters kinds : SemanticFunction =
            let nodes: SemanticNode list = kinds |> List.mapi (fun i kind -> { Id = id<GraphNodeTag> ($"zig-{name}-{i}"); Kind = kind })
            { MustNotWait = false; Exported = true; Id = id<FunctionTag> ("zig-" + name); DisplayName = name; TypeParameters = typeParameters
              Parameters = inputs |> List.mapi (fun i typ -> { Id = id<ParameterTag> ($"zig-{name}-p{i}"); DisplayName = $"p{i}"; Type = typ })
              ResultType = resultType; RequiredEffects = effects; Body = { Nodes = nodes; ResultNodeId = (List.last nodes).Id } }
        let identity name inputs effects = make name inputs (List.head inputs) effects [] [ SemanticNodeKind.ParameterReference(id<ParameterTag> ($"zig-{name}-p0")) ]
        let worker name input effects =
            make name [ input ] resultT effects []
                [ SemanticNodeKind.Int64Literal 1L; SemanticNodeKind.ResultOk(stringT, id<GraphNodeTag> ($"zig-{name}-0")) ]
        let generic =
            make "generic" [ TypeRef.TypeParameter(t "fn") ] (TypeRef.TypeParameter(t "fn")) [] [ t "fn", "T" ]
                [ SemanticNodeKind.ParameterReference(id<ParameterTag> "zig-generic-p0") ]
        // The maker returns Unit: its OneShot requirement is visible only in the body, and must
        // propagate through the wrapper's generic call rather than just through its signature.
        let maker =
            make "maker" [ scopeT; TypeRef.TypeParameter(t "maker") ] unitT [] [ t "maker", "T" ]
                [ SemanticNodeKind.ParameterReference(id<ParameterTag> "zig-maker-p0")
                  SemanticNodeKind.OneShotCreate(id<GraphNodeTag> "zig-maker-0", TypeRef.TypeParameter(t "maker"))
                  SemanticNodeKind.UnitLiteral ]
        let forward =
            make "forward" [ scopeT; TypeRef.TypeParameter(t "forward") ] unitT [] [ t "forward", "T" ]
                [ SemanticNodeKind.ParameterReference(id<ParameterTag> "zig-forward-p0")
                  SemanticNodeKind.ParameterReference(id<ParameterTag> "zig-forward-p1")
                  SemanticNodeKind.DirectCall(maker.Id,
                      [ { TargetParameterId = id<ParameterTag> "zig-maker-p0"; ValueNodeId = id<GraphNodeTag> "zig-forward-0" }
                        { TargetParameterId = id<ParameterTag> "zig-maker-p1"; ValueNodeId = id<GraphNodeTag> "zig-forward-1" } ]) ]
        let nested =
            make "nested" [ int64T ] (TypeRef.Primitive PrimitiveType.Bool) [] []
                [ SemanticNodeKind.BoolLiteral true
                  SemanticNodeKind.Conditional(id<GraphNodeTag> "zig-nested-0",
                      { Nodes = [ { Id = id<GraphNodeTag> "zig-nested-true"; Kind = SemanticNodeKind.CancellationRequested } ]; ResultNodeId = id<GraphNodeTag> "zig-nested-true" },
                      { Nodes = [ { Id = id<GraphNodeTag> "zig-nested-false"; Kind = SemanticNodeKind.BoolLiteral false } ]; ResultNodeId = id<GraphNodeTag> "zig-nested-false" }) ]
        let functions =
            [ twice; generic; maker; forward; nested
              identity "identity" [ int64T ] []; identity "pair" [ int64T; int64T ] []; identity "triple" [ int64T; int64T; int64T ] []
              identity "string" [ stringT ] []; identity "unit" [ unitT ] []; identity "scope" [ scopeT ] []; identity "exception" [ exceptionT ] []
              identity "effect" [ int64T ] [ effectId ]; identity "effectPair" [ int64T; int64T ] [ effectId ]
              make "less" [ int64T; int64T ] (TypeRef.Primitive PrimitiveType.Bool) [] [] [ SemanticNodeKind.BoolLiteral true ]
              make "listMapper" [ int64T ] (TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ int64T ])) [] [] [ SemanticNodeKind.ListEmpty int64T ]
              make "optionMapper" [ int64T ] (TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ int64T ])) [] [] [ SemanticNodeKind.OptionNone int64T ]
              make "resultPair" [ int64T; int64T ] resultT [] []
                  [ SemanticNodeKind.Int64Literal 1L; SemanticNodeKind.ResultOk(stringT, id<GraphNodeTag> "zig-resultPair-0") ]
              make "transitive" [ int64T ] (TypeRef.Primitive PrimitiveType.Bool) [] []
                  [ SemanticNodeKind.ParameterReference(id<ParameterTag> "zig-transitive-p0")
                    SemanticNodeKind.DirectCall(nested.Id, [ { TargetParameterId = id<ParameterTag> "zig-nested-p0"; ValueNodeId = id<GraphNodeTag> "zig-transitive-0" } ]) ]
              make "release" [ int64T ] unitT [] [] [ SemanticNodeKind.UnitLiteral ]
              make "float" [ int64T ] (TypeRef.Primitive PrimitiveType.Float64) [] [] [ SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 1.0) ]
              worker "worker" int64T []; worker "body" unitT []; worker "scopeBody" scopeT []; worker "handler" exceptionT []
              worker "effectWorker" int64T [ effectId ]; worker "effectBody" unitT [ effectId ] ]
        let operation name resultType : SemanticCapabilityOperation =
            { Id = id<CapabilityOperationTag> ("zig-" + name); DisplayName = name; MayWait = false; FailureType = None
              Parameters = [ { Id = id<CapabilityParameterTag> ("zig-" + name + "-p"); DisplayName = "p"; Type = int64T } ]
              ResultType = resultType; EffectId = effectId }
        let capability: SemanticCapability =
            { Id = id<CapabilityTag> "zig-IO"; DisplayName = "IO"; Exported = true; TaskBound = false
              Operations = [ operation "read" int64T; operation "release" unitT ] }
        { Snapshot.empty (id<SnapshotTag> "zig-host-inference") with
            Types = types; Functions = functions
            Effects = [ { Id = effectId; DisplayName = "io"; Exported = true } ]; Capabilities = [ capability ] }

    /// Requests reaching each inference diagnostic, the location suffix through each renamed field,
    /// F#'s traversal order, and the type-parameter cases.
    let internal requests () =
        let primitive name = $"""{{"kind":"primitive","primitive":"{name}"}}"""
        let unitT, int64T, boolT, stringT, float64T = primitive "unit", primitive "int64", primitive "bool", primitive "string", primitive "float64"
        let named id (arguments: string list) = $"""{{"kind":"named","typeId":"{id}","arguments":[{String.Join(",", arguments)}]}}"""
        let generic name (arguments: string list) = $"""{{"kind":"builtinGeneric","generic":"{name}","arguments":[{String.Join(",", arguments)}]}}"""
        let typeParameter name = $"""{{"kind":"typeParameter","name":"{name}"}}"""
        let unit = """{"kind":"unitLiteral"}"""
        let int = """{"kind":"int64Literal","value":1}"""
        let str = """{"kind":"stringLiteral","value":"s"}"""
        let bool = """{"kind":"boolLiteral","value":true}"""
        let flt = """{"kind":"float64Literal","bits":"3ff0000000000000"}"""
        let slot index = $"""{{"kind":"parameterSlot","index":{index}}}"""
        let unary kind value = $"""{{"kind":"{kind}","value":{value}}}"""
        let binary kind left right = $"""{{"kind":"{kind}","left":{left},"right":{right}}}"""
        let recipe kind (fields: (string * string) list) = "{\"kind\":\"" + kind + "\"," + String.Join(",", fields |> List.map (fun (name, value) -> $"\"{name}\":{value}")) + "}"
        let quoted (value: string) = "\"" + value + "\""
        let array (items: string list) = "[" + String.Join(",", items) + "]"
        let listEmpty itemType = recipe "listEmpty" [ "itemType", itemType ]
        let bound (fieldId: string) value = $"""{{"fieldId":"{fieldId}","value":{value}}}"""
        let construct typeId (fields: string list) = recipe "productConstruct" [ "typeId", quoted typeId; "fields", array fields ]
        let constructWith typeId (arguments: string list) (fields: string list) = recipe "productConstruct" [ "typeId", quoted typeId; "typeArguments", array arguments; "fields", array fields ]
        let unionConstruct variantId (arguments: string list) (fields: string list) = recipe "unionConstruct" [ "variantId", quoted variantId; "typeArguments", array arguments; "fields", array fields ]
        let update typeId source (fields: string list) = recipe "productUpdate" [ "typeId", quoted typeId; "source", source; "fields", array fields ]
        let access source fieldId = recipe "fieldAccess" [ "source", source; "fieldId", quoted fieldId ]
        let pattern slot depth = $"""{{"kind":"patternReference","slot":"{slot}","scopeDepth":{depth}}}"""
        let fieldPattern fieldId depth = $"""{{"kind":"patternReference","slot":"field","fieldId":"{fieldId}","scopeDepth":{depth}}}"""
        let bind value body = recipe "bind" [ "value", value; "body", body ]
        let conditional condition whenTrue whenFalse = recipe "conditional" [ "condition", condition; "whenTrue", whenTrue; "whenFalse", whenFalse ]
        let matchOption input someBody noneBody = recipe "matchOption" [ "input", input; "someBody", someBody; "noneBody", noneBody ]
        let matchResult input okBody errBody = recipe "matchResult" [ "input", input; "okBody", okBody; "errBody", errBody ]
        let arm (variantId: string) body = $"""{{"variantId":"{variantId}","body":{body}}}"""
        let arms (variantIds: string list) body = "{\"variantIds\":" + array (List.map quoted variantIds) + ",\"body\":" + body + "}"
        let matchUnion input (arms: string list) = recipe "matchUnion" [ "input", input; "arms", array arms ]
        let matchUnionOtherwise input (arms: string list) otherwise = recipe "matchUnion" [ "input", input; "arms", array arms; "otherwise", otherwise ]
        let abort payload = recipe "abort" [ "error", payload ]
        let point = construct "zig-Point" [ bound "zig-point-x" int; bound "zig-point-y" int ]
        let dot = unionConstruct "zig-dot" [] []
        let badList = unary "listLength" int
        let mapped = recipe "listMap" [ "source", listEmpty int64T; "functionId", "\"zig-twice\""; "context", "[]" ]
        let line (parameters: string list) resultType body = $"""{{"id":"i","op":"simulateCreateFunction","displayName":"made","parameters":[{String.Join(",", parameters)}],"resultType":{resultType},"requiredEffects":[],"body":{body}}}"""
        let parameter name (t: string) = $"""{{"displayName":"{name}","type":{t}}}"""
        let withTypeParameters (parameters: string list) resultType body = (line parameters resultType body).Replace("\"requiredEffects\":[]", "\"requiredEffects\":[],\"typeParameters\":[\"T\"]")
        let context id value = "{\"parameterId\":" + quoted id + ",\"value\":" + value + "}"
        let callback kind sourceField source name bindings =
            recipe kind [ sourceField, source; "functionId", quoted ("zig-" + name); "context", array bindings ]
        let call name bindings = recipe "directCall" [ "functionId", quoted ("zig-" + name); "arguments", array bindings ]
        let reference name = recipe "functionReference" [ "functionId", quoted ("zig-" + name) ]
        let invoke value arguments = recipe "functionInvoke" [ "functionValue", value; "arguments", array arguments ]
        let set = recipe "setEmpty" [ "itemType", int64T ]
        let map = recipe "mapEmpty" [ "keyType", int64T; "valueType", int64T ]
        let option = recipe "optionNone" [ "itemType", int64T ]
        let ok value = recipe "resultOk" [ "errorType", stringT; "value", value ]
        let body kind name bindings = recipe kind [ "bodyFunctionId", quoted ("zig-" + name); "context", array bindings ]
        let scopeT, exceptionT = generic "taskScope" [], generic "exception" []
        let cellT = generic "oneShot" [ int64T ]
        let implementation operation name = "{\"operationId\":" + quoted ("zig-" + operation) + ",\"functionId\":" + quoted ("zig-" + name) + "}"
        let capability target implementations = recipe "capabilityConstruct" [ "capabilityId", quoted target; "implementations", array implementations ]
        let implementations = [ implementation "read" "identity"; implementation "release" "release" ]
        let cap = capability "zig-IO" implementations
        let capArgument name value = "{\"capabilityParameterId\":" + quoted name + ",\"value\":" + value + "}"
        let capInvoke receiver operation bindings = recipe "capabilityInvoke" [ "capabilityValue", receiver; "operationId", quoted operation; "arguments", array bindings ]
        let resource acquire receiver operation name bindings =
            recipe "useResource" [ "acquire", acquire; "releaseCapability", receiver; "releaseOperationId", quoted operation; "bodyFunctionId", quoted ("zig-" + name); "context", array bindings ]
        let catch body handler bodyContext handlerContext =
            recipe "exceptionCatch" [ "bodyFunctionId", quoted ("zig-" + body); "handlerFunctionId", quoted ("zig-" + handler); "bodyContext", array bodyContext; "handlerContext", array handlerContext ]
        let start scope name item bindings = recipe "taskStart" [ "scope", scope; "workerFunctionId", quoted ("zig-" + name); "item", item; "context", array bindings ]
        let wait tasks limit = recipe "taskWait" ([ "tasks", tasks; "mode", quoted "all" ] @ (limit |> Option.toList |> List.map (fun x -> "limit", x)))
        let cell scope valueType = recipe "oneShotCreate" [ "scope", scope; "valueType", valueType ]
        let complete cell value = recipe "oneShotComplete" [ "cell", cell; "value", value ]
        let cellWait cell limit = recipe "oneShotWait" [ "cell", cell; "limit", limit ]
        let withEffects (request: string) = request.Replace("\"requiredEffects\":[]", "\"requiredEffects\":[\"zig-io\"]")
        let deep wrap value = [ 1 .. 25 ] |> List.fold (fun value _ -> wrap value) value
        let more =
            [ // Every callback role: source shape, missing target, arity, context, input, result and purity.
              for kind, sourceField, source, name in
                  [ "listMap", "source", listEmpty int64T, "identity"; "listFlatMap", "source", listEmpty int64T, "identity"
                    "mapFromList", "source", listEmpty int64T, "identity"; "listFilter", "source", listEmpty int64T, "twice"
                    "listAny", "source", listEmpty int64T, "twice"; "listAll", "source", listEmpty int64T, "twice"
                    "listFindIndex", "source", listEmpty int64T, "twice"; "listSortBy", "source", listEmpty int64T, "pair"
                    "listMinBy", "source", listEmpty int64T, "pair"; "listMaxBy", "source", listEmpty int64T, "pair"
                    "mapFilter", "map", map, "pair"; "mapMapValues", "map", map, "pair"
                    "setFilter", "set", set, "twice"; "setMap", "set", set, "identity"
                    "optionMap", "option", option, "identity"; "optionAndThen", "option", option, "identity"
                    "resultMap", "result", ok int, "identity"; "resultMapErr", "result", ok int, "string"
                    "resultAndThen", "result", ok int, "identity" ] do
                  for source, target, bindings in
                      [ badList, name, []; int, name, []; source, "missing", []; source, "unit", []; source, "pair", []
                        source, name, []; source, "generic", []; source, "effect", []; source, "nested", []; source, "transitive", []
                        source, name, [ context "foreign" badList ]
                        source, name, [ context "foreign" int; context "foreign" badList ]
                        source, name, [ context (if name = "twice" then "zig-twice-n" else $"zig-{name}-p0") str ] ] do
                      line [] unitT (callback kind sourceField source target bindings)
              line [] unitT (callback "listFilter" "source" (listEmpty int64T) "identity" [])
              line [] unitT (callback "mapFromList" "source" (listEmpty int64T) "float" [])
              line [] unitT (callback "setMap" "set" set "float" [])
              for kind, sourceField, source, target in
                  [ "listFlatMap", "source", listEmpty int64T, "listMapper"; "listSortBy", "source", listEmpty int64T, "less"
                    "listMinBy", "source", listEmpty int64T, "less"; "listMaxBy", "source", listEmpty int64T, "less"
                    "mapFilter", "map", map, "less"; "optionAndThen", "option", option, "optionMapper"
                    "resultAndThen", "result", ok int, "worker" ] do
                  line [] unitT (callback kind sourceField source target [])
              for kind in [ "listFold"; "listEffectFold" ] do
                  line [] unitT (recipe kind [ "source", listEmpty int64T; "seed", int; "functionId", quoted "zig-resultPair" ])
              for kind, sourceField, source in [ "listFold", "source", listEmpty int64T; "listScan", "source", listEmpty int64T; "listEffectFold", "source", listEmpty int64T; "mapFold", "map", map; "setFold", "set", set ] do
                  for target in [ "missing"; "identity"; "pair"; "triple"; "effectPair"; "worker" ] do
                      line [] unitT (recipe kind [ sourceField, source; "seed", int; "functionId", quoted ("zig-" + target) ])
                  line [] unitT (recipe kind [ sourceField, badList; "seed", str; "functionId", quoted "zig-missing" ])
              for target in [ "missing"; "identity"; "pair"; "effectPair" ] do
                  line [] unitT (recipe "listZipWith" [ "left", listEmpty int64T; "right", listEmpty int64T; "functionId", quoted ("zig-" + target) ])
              for target in [ "missing"; "identity"; "pair"; "worker"; "effectWorker" ] do
                  line [] unitT (recipe "parallelAll" [ "source", listEmpty int64T; "workerFunctionId", quoted ("zig-" + target) ])
              // The symbolic decoder must feed the same inference graph (including callback context).
              for kind, sourceField, source, name in
                  [ "listMap", "source", listEmpty int64T, "unit"; "mapFilter", "map", map, "pair"
                    "setMap", "set", set, "float"; "optionAndThen", "option", option, "identity"
                    "resultAndThen", "result", ok int, "identity" ] do
                  (line [] unitT (callback kind sourceField source name []))
                      .Replace("simulateCreateFunction", "simulateCreateFunctionByName")
                      .Replace("\"functionId\":\"zig-" + name + "\"", "\"function\":\"" + name + "\"")
              for kind, name in [ "cancellationShield", "unit"; "taskScope", "scope" ] do
                  (line [] unitT (body kind name []))
                      .Replace("simulateCreateFunction", "simulateCreateFunctionByName")
                      .Replace("\"bodyFunctionId\":\"zig-" + name + "\"", "\"body\":\"" + name + "\"")
              for name in [ "identity"; "generic" ] do
                  line [] unitT (recipe "directCall" [ "function", quoted name; "arguments", "[{\"parameter\":\"p0\",\"value\":" + str + "}]" ])
                  |> fun request -> request.Replace("simulateCreateFunction", "simulateCreateFunctionByName")
              line [] unitT (recipe "listMap" [ "source", listEmpty int64T; "function", quoted "pair"; "context", "[{\"parameter\":\"p0\",\"value\":" + str + "}]" ])
              |> fun request -> request.Replace("simulateCreateFunction", "simulateCreateFunctionByName")
              // Calls validate duplicate/foreign/missing bindings before typing values, in declaration order.
              for bindings in [ []; [ context "foreign" badList ]; [ context "zig-identity-p0" str ]; [ context "zig-identity-p0" int; context "zig-identity-p0" badList ]; [ context "zig-identity-p0" int ] ] do
                  line [] unitT (call "identity" bindings)
              line [] unitT (call "missing" [])
              line [] unitT (call "effect" [])
              line [] unitT (call "generic" [ context "zig-generic-p0" badList ])
              line [] unitT (call "generic" [ context "zig-generic-p0" int ])
              line [ parameter "scope" scopeT ] unitT (call "generic" [ context "zig-generic-p0" (slot 0) ])
              for target in [ "maker"; "forward" ] do
                  line [ parameter "scope" scopeT ] unitT (call target [ context ($"zig-{target}-p0") (slot 0); context ($"zig-{target}-p1") (slot 0) ])
                  line [ parameter "scope" scopeT ] int64T (call target [ context ($"zig-{target}-p0") (slot 0); context ($"zig-{target}-p1") int ])
              line [] unitT (binary "stringConcat" int (call "generic" [ context "zig-generic-p0" badList ]))
              line [] unitT (binary "stringConcat" str (call "generic" [ context "zig-generic-p0" badList ]))
              for arguments in [ "[]"; "[{\"slot\":2,\"value\":" + badList + "}]"; "[{\"slot\":0,\"value\":" + str + "}]"; "[{\"slot\":0,\"value\":" + int + "},{\"slot\":0,\"value\":" + badList + "}]" ] do
                  line [ parameter "n" int64T ] unitT (recipe "selfCall" [ "arguments", arguments ])
              for name in [ "missing"; "generic"; "effect"; "nested"; "transitive"; "identity" ] do
                  line [] unitT (reference name)
              for value, arguments in [ int, [ badList ]; reference "identity", []; reference "identity", [ str ]; reference "identity", [ int ] ] do
                  line [] unitT (invoke value arguments)
              // Capability implementation coverage and the first failing implementation's diagnostics.
              line [] unitT (capability "missing" [])
              line [] unitT (capability "zig-IO" [])
              line [] unitT (capability "zig-IO" [ implementation "read" "identity"; implementation "read" "identity" ])
              for name in [ "missing"; "generic"; "effect"; "nested"; "twice"; "identity" ] do
                  line [] unitT (capability "zig-IO" [ implementation "read" name; implementation "release" "release" ])
              line [] unitT (capInvoke badList "missing" [])
              line [] unitT (capInvoke int "zig-read" [])
              line [] unitT (capInvoke cap "zig-read" [])
              for arguments in [ []; [ capArgument "foreign" badList ]; [ capArgument "zig-read-p" str ]; [ capArgument "zig-read-p" int; capArgument "zig-read-p" badList ]; [ capArgument "zig-read-p" int ] ] do
                  line [] unitT (capInvoke cap "zig-read" arguments) |> withEffects
              // Resource bodies, receiver, effects, result and release signature (in that order).
              line [] unitT (resource int cap "zig-release" "worker" [])
              line [] unitT (resource (ok int) cap "missing" "worker" [])
              line [] unitT (resource (ok int) cap "zig-release" "missing" [])
              line [] unitT (resource (ok int) cap "zig-release" "unit" [])
              line [] unitT (resource (ok int) int "zig-release" "worker" [])
              line [] unitT (resource (ok int) cap "zig-release" "worker" [])
              line [] unitT (resource (ok int) cap "zig-release" "identity" []) |> withEffects
              line [] unitT (resource (ok int) cap "zig-read" "worker" []) |> withEffects
              line [] unitT (resource (ok int) cap "zig-release" "worker" []) |> withEffects
              line [] unitT (resource (ok int) badList "zig-release" "worker" [ context "foreign" int ])
              // Exception and task callbacks, including errors in both operands.
              for bodyName, handler in [ "missing", "handler"; "identity", "handler"; "body", "identity"; "unit", "exception"; "body", "handler"; "effectBody", "handler" ] do
                  line [] unitT (catch bodyName handler [] [])
              line [] unitT (catch "body" "handler" [ context "foreign" int ] [ context "foreign" str ])
              line [] unitT (unary "exceptionMessage" int)
              line [] unitT (unary "exceptionIsCancellation" int)
              line [] unitT (recipe "exceptionRethrow" [ "value", int; "resultType", int64T ])
              for kind, good, wrong in [ "cancellationShield", "body", "unit"; "taskScope", "scopeBody", "scope" ] do
                  for name in [ "missing"; "identity"; good; wrong ] do line [] unitT (body kind name [])
              for name in [ "missing"; "identity"; "pair"; "worker"; "effectWorker" ] do
                  line [ parameter "scope" scopeT ] unitT (start (slot 0) name int [])
              line [] unitT (start int "worker" int [])
              line [] unitT (start badList "worker" (unary "stringLength" int) [])
              for tasks, limit in [ int, None; int, Some int; badList, Some int; badList, Some (unary "stringLength" int); listEmpty (generic "task" [ int64T ]), Some option ] do
                  line [] unitT (wait tasks limit)
              line [] unitT (cell int int64T)
              line [ parameter "scope" scopeT ] unitT (cell (slot 0) (generic "list" [ scopeT ]))
              line [ parameter "scope" scopeT ] unitT (cell (slot 0) int64T)
              line [] unitT (complete int int)
              line [ parameter "cell" cellT ] unitT (complete (slot 0) str)
              line [] unitT (complete badList (unary "stringLength" int))
              for value, limit in [ int, option; int, int; badList, int; badList, unary "stringLength" int ] do
                  line [] unitT (cellWait value limit)
              line [] unitT (recipe "taskCancel" [ "task", int ])
              line [] unitT (recipe "taskCancel" [ "task", badList ])
              for kind in [ "exceptionMessage"; "exceptionIsCancellation" ] do line [] unitT (unary kind badList)
              line [] unitT (recipe "exceptionRethrow" [ "value", badList; "resultType", int64T ])
              line [] unitT (callback "mapFilter" "map" badList "pair" [])
              line [] unitT (callback "setMap" "set" badList "identity" [])
              line [] unitT (callback "listMap" "source" (listEmpty int64T) "pair" [ context "zig-pair-p0" badList ])
              line [] unitT (invoke (reference "identity") [ badList ])
              // 25-deep or wide cases for each family stay inside the preflight bounds and the 20s gate.
              line [] unitT (deep (fun source -> callback "listMap" "source" source "identity" []) (listEmpty int64T))
              line [] unitT (deep (fun source -> recipe "listScan" [ "source", source; "seed", int; "functionId", quoted "zig-pair" ]) (listEmpty int64T))
              line [] unitT (deep (fun source -> callback "mapMapValues" "map" source "pair" []) map)
              line [] unitT (deep (fun source -> callback "setMap" "set" source "identity" []) set)
              line [] unitT (deep (fun source -> callback "optionMap" "option" source "identity" []) option)
              line [] unitT (deep (fun source -> callback "resultMap" "result" source "identity" []) (ok int))
              line [ parameter "n" int64T ] int64T (deep (fun value -> recipe "selfCall" [ "arguments", "[{\"slot\":0,\"value\":" + value + "}]" ]) (slot 0))
              line [ parameter "scope" scopeT ] unitT (recipe "listConstruct" [ "itemType", scopeT; "items", array (List.replicate 80 (call "generic" [ context "zig-generic-p0" (slot 0) ])) ])
              line [ parameter "cell" cellT ] unitT (recipe "listConstruct" [ "itemType", generic "option" [ int64T ]; "items", array (List.replicate 80 (cellWait (slot 0) option)) ])
              line [ parameter "task" (generic "task" [ int64T ]) ] unitT (recipe "taskCancel" [ "task", slot 0 ])
              line [] unitT (deep (fun value -> call "identity" [ context "zig-identity-p0" value ]) int)
              line [] unitT (deep (fun value -> invoke (reference "identity") [ value ]) int)
              line [] unitT (deep (fun value -> capInvoke cap "zig-read" [ capArgument "zig-read-p" value ]) int) |> withEffects
              line [] unitT (deep (fun acquire -> resource acquire cap "zig-release" "worker" []) (ok int)) |> withEffects
              line [ parameter "ex" exceptionT ] unitT (deep (fun value -> recipe "exceptionRethrow" [ "value", value; "resultType", exceptionT ]) (slot 0))
              line [] unitT (recipe "listConstruct" [ "itemType", generic "result" [ int64T; stringT ]; "items", array (List.replicate 100 (catch "body" "handler" [] [])) ])
              line [] unitT (recipe "listConstruct" [ "itemType", generic "result" [ int64T; stringT ]; "items", array (List.replicate 100 (body "cancellationShield" "body" [])) ])
              line [] unitT (recipe "listConstruct" [ "itemType", generic "result" [ int64T; stringT ]; "items", array (List.replicate 100 (body "taskScope" "scopeBody" [])) ])
              line [ parameter "scope" scopeT ] unitT (recipe "listConstruct" [ "itemType", generic "task" [ generic "result" [ int64T; stringT ] ]; "items", array (List.replicate 80 (start (slot 0) "worker" int [])) ])
              line [ parameter "scope" scopeT ] unitT (recipe "listConstruct" [ "itemType", cellT; "items", array (List.replicate 100 (cell (slot 0) int64T)) ])
              line [] unitT (recipe "listConstruct" [ "itemType", generic "result" [ generic "list" [ int64T ]; stringT ]; "items", array (List.replicate 100 (recipe "parallelAll" [ "source", listEmpty int64T; "workerFunctionId", quoted "zig-worker" ])) ]) ]
        [ // Fixed signatures: a fixed operand, a shape, a bound variable, the equatable constraint before the next operand.
          line [] unitT (binary "stringConcat" int str)
          line [] unitT badList
          line [] unitT (recipe "listAppendItem" [ "list", listEmpty int64T; "item", str ])
          line [] unitT (recipe "stringContains" [ "value", int; "substring", str ])
          line [] unitT (recipe "listIndexOf" [ "list", listEmpty float64T; "item", mapped ])
          line [] unitT (recipe "listLastIndexOf" [ "list", listEmpty float64T; "item", flt ])
          line [] unitT (recipe "listContains" [ "list", listEmpty (named "zig-Shape" []); "item", dot ])
          line [] unitT (unary "setFromList" (listEmpty (generic "list" [ float64T ])))
          line [] unitT (recipe "mapInsert" [ "map", recipe "mapEmpty" [ "keyType", int64T; "valueType", stringT ]; "key", int; "value", int ])
          // The F# field names of renamed wire fields in the location suffix.
          line [] unitT (recipe "mapInsert" [ "map", recipe "mapEmpty" [ "keyType", int64T; "valueType", stringT ]; "key", int; "value", badList ])
          line [] unitT (recipe "optionGetOrDefault" [ "option", recipe "optionNone" [ "itemType", int64T ]; "default", badList ])
          line [] unitT (recipe "setInsert" [ "set", recipe "setEmpty" [ "itemType", int64T ]; "item", badList ])
          line [] unitT (recipe "listRange" [ "start", badList; "end", int ])
          line [] unitT (recipe "stringRepeat" [ "text", str; "count", badList ])
          line [] unitT (recipe "float64Round" [ "value", flt; "digits", badList ])
          line [] unitT (recipe "listConstruct" [ "itemType", int64T; "items", array [ int; badList ] ])
          line [] unitT (construct "zig-Point" [ bound "zig-point-x" int; bound "zig-point-y" badList ])
          line [] unitT (matchUnion dot [ arm "zig-circle" int; arm "zig-dot" int; arm "zig-square" badList ])
          line [] unitT (matchUnionOtherwise dot [ arm "zig-circle" int ] badList)
          // A field-less F# case is one object: the path names its first occurrence, not the failing one.
          line [] unitT (construct "zig-Point" [ bound "zig-point-y" """{"kind":"originalTarget"}"""; bound "zig-point-x" """{"kind":"originalTarget"}""" ])
          line [] unitT (matchUnion dot [ arm "zig-square" """{"kind":"originalTarget"}"""; arm "zig-circle" """{"kind":"originalTarget"}"""; arm "zig-dot" int ])
          line [] unitT (binary "stringConcat" str """{"kind":"originalTarget"}""")
          // Operators, constructors and equatability.
          line [] unitT (binary "boolAnd" int bool)
          line [] unitT (binary "boolOr" bool int)
          line [] unitT (binary "boolAnd" bool (abort str))
          line [] unitT (binary "valueEqual" int str)
          line [] unitT (binary "valueEqual" flt flt)
          line [] unitT (binary "valueEqual" dot dot)
          line [] unitT (binary "valueEqual" (listEmpty float64T) (listEmpty float64T))
          line [] unitT (binary "valueEqual" (construct "zig-Tree" [ bound "zig-tree-label" str; bound "zig-tree-children" (listEmpty (named "zig-Tree" [])) ]) (construct "zig-Tree" [ bound "zig-tree-label" str; bound "zig-tree-children" (listEmpty (named "zig-Tree" [])) ]))
          line [ parameter "x" (generic "exception" []) ] unitT (binary "valueEqual" (slot 0) (slot 0))
          line [ parameter "x" (generic "map" [ stringT; int64T ]) ] unitT (binary "valueEqual" (slot 0) (slot 0))
          line [] unitT (recipe "setEmpty" [ "itemType", float64T ])
          line [] unitT (recipe "setEmpty" [ "itemType", named "zig-Box" [ generic "option" [ float64T ] ] ])
          line [] unitT (recipe "mapEmpty" [ "keyType", float64T; "valueType", int64T ])
          line [] unitT (recipe "listConstruct" [ "itemType", int64T; "items", array [ int; str ] ])
          line [] unitT (listEmpty (named "zig-nope" []))
          line [] unitT (recipe "optionNone" [ "itemType", named "zig-Box" [] ])
          line [] unitT (recipe "resultOk" [ "errorType", generic "list" []; "value", int ])
          line [] unitT (recipe "resultErr" [ "okType", generic "capability" []; "error", int ])
          line [] unitT (constructWith "zig-Box" [ named "zig-nope" [] ] [ bound "zig-box-item" int ])
          line [] unitT (constructWith "zig-Box" [ int64T; int64T ] [ bound "zig-box-item" int ])
          line [] unitT (constructWith "zig-Box" [ int64T ] [ bound "zig-box-item" str ])
          line [] unitT (construct "zig-Pair" [ bound "zig-pair-first" int; bound "zig-pair-second" (listEmpty stringT) ])
          line [] unitT (construct "zig-Point" [ bound "zig-point-x" int; bound "zig-point-x" int ])
          line [] unitT (construct "zig-Point" [ bound "zig-point-x" int ])
          line [] unitT (construct "zig-Point" [ bound "zig-point-x" int; bound "zig-point-y" str ])
          line [] unitT (construct "zig-nope" [])
          line [] unitT (construct "zig-Shape" [])
          line [] unitT (unionConstruct "zig-nope" [] [])
          line [] unitT (unionConstruct "zig-nothing" [] [])
          line [] unitT (unionConstruct "zig-just" [] [ bound "zig-just-value" str ])
          line [] unitT (unionConstruct "zig-just" [ int64T ] [ bound "zig-just-value" str ])
          line [] unitT (access int "zig-nope")
          line [] unitT (access int "zig-circle-radius")
          line [] unitT (access int "zig-point-x")
          line [] unitT (access dot "zig-point-x")
          line [] unitT (access (constructWith "zig-Box" [ int64T ] [ bound "zig-box-item" int ]) "zig-point-x")
          line [] unitT (access (construct "zig-Pair" [ bound "zig-pair-first" str; bound "zig-pair-second" (listEmpty stringT) ]) "zig-pair-second")
          line [] unitT (update "zig-Point" point [])
          line [] unitT (update "zig-Point" point [ bound "zig-nope" int ])
          line [] unitT (update "zig-Point" int [ bound "zig-point-x" int ])
          line [] unitT (update "zig-Point" point [ bound "zig-point-y" str ])
          line [] unitT (update "zig-nope" int [])
          line [] unitT (update "zig-Shape" int [])
          line [] unitT (update "zig-Point" point [ bound "zig-point-y" int ])
          // Leaves.
          line [] unitT """{"kind":"originalTarget"}"""
          line [] unitT """{"kind":"parameter","parameterId":"zig-twice-n"}"""
          line [ parameter "a" int64T; parameter "b" stringT ] unitT """{"kind":"parameter","parameterId":"zig-nope"}"""
          line [ parameter "a" int64T ] unitT (slot 1)
          line [] unitT (slot -1)
          line [] unitT (pattern "value" 0)
          line [] unitT (pattern "value" -1)
          line [] unitT (bind int (pattern "error" 0))
          line [] unitT (matchOption (recipe "optionNone" [ "itemType", int64T ]) (bind str (pattern "value" 2)) int)
          line [] unitT (matchUnion dot [ arm "zig-circle" (fieldPattern "zig-circle-radius" 0); arm "zig-dot" flt; arm "zig-square" flt ])
          line [] unitT (matchUnion dot [ arm "zig-circle" (fieldPattern "zig-square-side" 0); arm "zig-dot" flt; arm "zig-square" flt ])
          line [] unitT (matchUnion dot [ arm "zig-circle" (fieldPattern "zig-nope" 0); arm "zig-dot" flt; arm "zig-square" flt ])
          line [] unitT (matchUnion dot [ arms [ "zig-circle"; "zig-square" ] (pattern "value" 0); arm "zig-dot" flt ])
          line [] unitT (matchResult (recipe "resultOk" [ "errorType", stringT; "value", int ]) (pattern "value" 0) (pattern "value" 0))
          // Branches, arms and aborts.
          line [] unitT (conditional int int int)
          line [] unitT (conditional bool int str)
          line [] (generic "result" [ unitT; stringT ]) (conditional bool (abort str) int)
          line [] (generic "result" [ unitT; stringT ]) (conditional bool (abort str) (abort str))
          line [] (generic "result" [ unitT; stringT ]) (unary "stringLength" (abort str))
          line [] (generic "result" [ unitT; stringT ]) (abort int)
          line [] unitT (abort str)
          line [] unitT (matchOption int int int)
          line [] unitT (matchOption (recipe "optionNone" [ "itemType", int64T ]) (pattern "value" 0) str)
          line [] unitT (matchResult int int int)
          line [] unitT (matchResult (recipe "resultOk" [ "errorType", stringT; "value", int ]) (pattern "value" 0) (pattern "error" 0))
          line [] unitT (matchUnion int [])
          line [] unitT (matchUnion point [])
          line [] unitT (matchUnion dot [ arm "zig-circle" int; arm "zig-circle" int; arm "zig-dot" int ])
          line [] unitT (matchUnion dot [ arm "zig-circle" int ])
          line [] unitT (matchUnion dot [ arm "zig-nope" int ])
          line [] unitT (matchUnionOtherwise dot [ arm "zig-circle" int; arm "zig-dot" int; arm "zig-square" int ] int)
          line [] unitT (matchUnionOtherwise dot [ arm "zig-circle" int ] str)
          line [] unitT (matchUnion dot [ arm "zig-square" int; arm "zig-circle" str; arm "zig-dot" int ])
          line [] unitT (matchUnion (unionConstruct "zig-just" [] [ bound "zig-just-value" str ]) [ arm "zig-just" (fieldPattern "zig-just-value" 0); arm "zig-nothing" str ])
          // Generic constructors nested as deep as the bounds allow, with derived type arguments: each
          // level's value is inferred for the arguments and again for the bindings, so the cache keeps it linear.
          line [] unitT (List.fold (fun inner _ -> construct "zig-Box" [ bound "zig-box-item" inner ]) int [ 1 .. 30 ])
          line [] unitT (List.fold (fun inner _ -> unionConstruct "zig-just" [] [ bound "zig-just-value" inner ]) (update "zig-Point" point [ bound "zig-point-x" int ]) [ 1 .. 28 ])
          // The result type.
          line [] unitT int
          line [] int64T point
          line [] (named "zig-Box" [ int64T ]) (constructWith "zig-Box" [ stringT ] [ bound "zig-box-item" str ])
          // Callback kinds, reached or not in F#'s operand order.
          line [] unitT (binary "stringConcat" int mapped)
          line [] unitT (binary "stringConcat" str mapped)
          line [] unitT (conditional bool mapped int)
          // Type parameters: owner-named in operand messages, fresh identities elsewhere.
          withTypeParameters [ parameter "x" (typeParameter "T") ] int64T (slot 0)
          withTypeParameters [ parameter "x" (typeParameter "T") ] unitT (binary "valueEqual" (slot 0) (slot 0))
          withTypeParameters [ parameter "x" (typeParameter "T") ] unitT (recipe "listConstruct" [ "itemType", typeParameter "T"; "items", array [ int ] ])
          withTypeParameters [ parameter "x" (typeParameter "T") ] unitT (recipe "setEmpty" [ "itemType", typeParameter "T" ])
          withTypeParameters [ parameter "x" (typeParameter "T") ] unitT (conditional bool (slot 0) int)
          withTypeParameters [ parameter "x" (typeParameter "T") ] unitT (access (slot 0) "zig-point-x")
          withTypeParameters [ parameter "x" (typeParameter "T") ] unitT (construct "zig-Pair" [ bound "zig-pair-first" (slot 0); bound "zig-pair-second" (listEmpty int64T) ])
          withTypeParameters [ parameter "x" (typeParameter "T") ] (generic "list" [ typeParameter "T" ]) (recipe "productUpdate" [ "typeId", quoted "zig-Point"; "source", slot 0; "fields", array [ bound "zig-point-x" int ] ])
        ] @ more

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host infers simulateCreateFunction bodies as the F# host does`` () =
        withDirectory (fun dir ->
            let path = Path.Combine(dir, "inference.json")
            File.WriteAllText(path, SnapshotCodec.serialize (snapshot ()) |> unwrap)
            let lines = requests ()
            // Built before the clock starts: the first run of a changed host compiles it.
            zigHost () |> ignore
            let clock = Stopwatch.StartNew()
            let expected, answers = seededPair "create-inference" path lines
            // Both subprocesses run here; deep generic constructors still must remain linear.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds 40.0, $"The seeded pair took {clock.Elapsed} to answer the inference corpus.")
            let mismatches = ResizeArray<string>()
            let answered = Text.StringBuilder()
            let mutable located = 0
            for request, (fsharp, zig) in List.zip lines (List.zip expected answers) do
                answered.Append(fsharp) |> ignore
                if (locatedPath zig).IsSome then located <- located + 1
                if not (createReplyMatches request fsharp zig) then mismatches.Add $"{request}\n F#:  {fsharp}\n Zig: {zig}"
            let shownText = answered.ToString().Replace("\\u0027", "'").Replace("\\u003C", "<").Replace("\\u003E", ">")
            // The wire spells one `otherwise` arm, so a second one is a decode rejection.
            for shown in Set.toList (Set.remove "PureExpressionMatchOtherwiseDuplicate" inferenceCodes) @ [ "This generic call would place TaskScope inside a OneShot payload, directly or through another generic call."; "At body.exceptionValue (listLength)"; "At body.source (listLength)"; "At body.task (listLength)"; "At body.context[0].value (listLength)"; "At body.arguments[0] (listLength)"; "The slot is found at depths 0, 1;"; "Visible frames, innermost first: depth 0 = Bind body (binds Value)"; "binds 'radius'"; "(not a declared field)"; "At body.end (listLength)"; "At body.defaultValue (listLength)"; "At body.key (listLength)"; "At body.left (listLength)"; "At body.right (listLength)"; "At body.items[1] (listLength)"; "At body.fields[1].value (listLength)"; "At body.arms[2].body (listLength)"; "At body.arms[1].body (listLength)"; "At body.fields[0].value (originalTarget)"; "At body.arms[0].body (originalTarget)"; "At body.right (originalTarget)"; "The function body produces T, but the requested result type is Int64."; "found T and Int64"; "but the source recipe produces T. At body (fieldAccess)."; "but the source recipe produces T. At body (productUpdate)."; "requires List<T>, but the recipe produces List<Int64>"; "The function body produces " + String.replicate 30 "Box<" + "Int64" + String.replicate 30 ">" + ", but"; "The function body produces " + String.replicate 28 "Maybe<" + "Point" + String.replicate 28 ">" + ", but"; "Exception<> is not equatable"; "at Shape.circle.radius"; "at List item"; "type parameters are never equatable" ] do
                Assert.True(shownText.Contains shown, $"No F# reply shows {shown}; the inference parity test does not cover it.")
            Assert.True((mismatches.Count = 0), $"{mismatches.Count} mismatches:\n" + String.Join("\n", mismatches |> Seq.truncate 12))
            Assert.True(located > 60, $"Only {located} Zig replies locate their diagnostic."))

    /// Post-order and binding allocation across mixed compact/full successes and failures.
    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host materializes compact and full create proposals with seeded byte parity`` () =
        withDirectory (fun dir ->
            let primitive name = $"""{{"kind":"primitive","primitive":"{name}"}}"""
            let intT, stringT, unitT = primitive "int64", primitive "string", primitive "unit"
            let parameterT = """{"kind":"typeParameter","name":"T"}"""
            let parameter name typ = $"""{{"displayName":"{name}","type":{typ}}}"""
            let int = """{"kind":"int64Literal","value":7}"""
            let unit = """{"kind":"unitLiteral"}"""
            let pattern = """{"kind":"patternReference","scopeDepth":0,"slot":"value"}"""
            let slot = """{"kind":"parameterSlot","index":0}"""
            let request op name (parameters: string list) resultType body =
                $"""{{"id":"c","op":"{op}","displayName":"{name}","parameters":[{String.Join(",", parameters)}],"resultType":{resultType},"requiredEffects":[],"body":{body}}}"""
            let exact parameters resultType body = request "simulateCreateFunction" "made" parameters resultType body
            let symbolic parameters resultType body = request "simulateCreateFunctionByName" "made" parameters resultType body
            let optionMatch = $"""{{"kind":"matchOption","input":{{"kind":"optionSome","value":{int}}},"someBody":{pattern},"noneBody":{int}}}"""
            let resultMatch = $"""{{"kind":"matchResult","input":{{"kind":"resultOk","errorType":{stringT},"value":{int}}},"okBody":{pattern},"errBody":{int}}}"""
            let bind = $"""{{"kind":"bind","value":{int},"body":{{"kind":"bind","value":{pattern},"body":{{"kind":"patternReference","scopeDepth":1,"slot":"value"}}}}}}"""
            let large = request "simulateCreateFunction" (String.replicate 20000 "x") [] unitT unit
            let common =
                [ exact [] unitT unit
                  exact [] unitT int // inference rejection between successes
                  exact [ parameter "x" parameterT ] parameterT slot
                  symbolic [ parameter "x" parameterT ] parameterT """{"kind":"parameter","name":"x"}"""
                  exact [] intT bind
                  symbolic [] intT optionMatch
                  exact [] intT resultMatch
                  large
                  exact [] intT int
                  exact [ parameter "x" intT ] intT $"""{{"kind":"selfCall","arguments":[{{"slot":0,"value":{slot}}}]}}"""
                  symbolic [ parameter "x" intT ] intT """{"kind":"directCall","function":"made","arguments":[],"keepUnlisted":true}"""
                  exact [ parameter "x" parameterT ] unitT slot // rejected after allocating a fresh type parameter
                  exact [] intT int
                  """{"id":"p","op":"proposals"}"""
                  """{"id":"s","op":"status"}"""
                  """{"id":"d","op":"declarations"}""" ]
            let path = Path.Combine(dir, "inference.json")
            File.WriteAllText(path, SnapshotCodec.serialize (snapshot ()) |> unwrap)
            let cellT = """{"kind":"builtinGeneric","generic":"oneShot","arguments":[{"kind":"primitive","primitive":"int64"}]}"""
            let optionIntT = """{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"primitive","primitive":"int64"}]}"""
            let constructed =
                [ // An abort-only body has no normal result, but still materializes and reports aborts.
                  exact [] """{"kind":"builtinGeneric","generic":"result","arguments":[{"kind":"primitive","primitive":"int64"},{"kind":"primitive","primitive":"string"}]}"""
                      """{"kind":"abort","error":{"kind":"stringLiteral","value":"stopped"}}"""
                  exact [] stringT $"""{{"kind":"matchResult","input":{{"kind":"resultErr","okType":{intT},"error":{{"kind":"stringLiteral","value":"error"}}}},"okBody":{{"kind":"stringLiteral","value":"ok"}},"errBody":{{"kind":"patternReference","scopeDepth":0,"slot":"error"}}}}"""
                  // Exercise JSON escaping and UTF-16 truncation in the compact projection.
                  (symbolic [] stringT """{"kind":"stringLiteral","value":"quoted \" and \uD83D\uDE00"}""").Replace("\"displayName\":\"made\"", "\"displayName\":" + JsonSerializer.Serialize(String.replicate 3000 "😀界\"\n"))
                  // Waiting nodes exercise optional operands and the focus wait-path fields.
                  exact [ parameter "cell" cellT ] optionIntT $"""{{"kind":"oneShotWait","cell":{slot}}}"""
                  symbolic [ parameter "cell" cellT ] optionIntT """{"kind":"oneShotWait","cell":{"kind":"parameter","name":"cell"},"limit":{"kind":"optionNone","itemType":{"kind":"primitive","primitive":"int64"}}}"""
                  // Calls and contexts arrive backwards; allocation follows declaration order.
                  exact [] intT $"""{{"kind":"directCall","functionId":"zig-pair","arguments":[{{"parameterId":"zig-pair-p1","value":{int}}},{{"parameterId":"zig-pair-p0","value":{int}}}]}}"""
                  exact [] """{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"primitive","primitive":"int64"}]}"""
                      $"""{{"kind":"listMap","source":{{"kind":"listConstruct","itemType":{intT},"items":[{int}]}},"functionId":"zig-pair","context":[{{"parameterId":"zig-pair-p1","value":{int}}}]}}"""
                  symbolic [] intT $"""{{"kind":"directCall","function":"pair","arguments":[{{"parameter":"p1","value":{int}}},{{"parameter":"p0","value":{int}}}]}}"""
                  exact [] """{"kind":"named","typeId":"zig-Box","arguments":[{"kind":"primitive","primitive":"int64"}]}"""
                      $"""{{"kind":"productConstruct","typeId":"zig-Box","fields":[{{"fieldId":"zig-box-item","value":{int}}}]}}"""
                  exact [] intT $"""{{"kind":"matchUnion","input":{{"kind":"unionConstruct","variantId":"zig-square","fields":[{{"fieldId":"zig-square-side","value":{int}}}]}},"arms":[{{"variantId":"zig-square","body":{{"kind":"patternReference","slot":"field","scopeDepth":0,"fieldId":"zig-square-side"}}}},{{"variantId":"zig-dot","body":{int}}},{{"variantId":"zig-circle","body":{int}}}]}}"""
                  exact [] """{"kind":"capability","capabilityId":"zig-IO"}"""
                      """{"kind":"capabilityConstruct","capabilityId":"zig-IO","implementations":[{"operationId":"zig-release","functionId":"zig-release"},{"operationId":"zig-read","functionId":"zig-identity"}]}"""
                  (exact [ parameter "io" """{"kind":"capability","capabilityId":"zig-IO"}""" ] intT
                      $"""{{"kind":"capabilityInvoke","capabilityValue":{slot},"operationId":"zig-read","arguments":[{{"capabilityParameterId":"zig-read-p","value":{int}}}]}}""").Replace("\"requiredEffects\":[]", "\"requiredEffects\":[\"zig-io\"]") ]
            // Each create (including rejections) is followed by both full encodings. Later list/status
            // reads check registration and ID consumption without changing the committed snapshot.
            let withFullReviews (requests: string list) =
                requests |> List.collect (fun line ->
                    use parsed = parseDocument line
                    let op = parsed.RootElement.GetProperty("op").GetString()
                    if op = "simulateCreateFunction" || op = "simulateCreateFunctionByName" then
                        let full = line.Insert(1, "\"detail\":\"full\",")
                        [ line; full; full.Insert(1, "\"structuredReview\":true,") ]
                    else [ line ])
            let compactRequests = common @ constructed @ List.skip (common.Length - 3) common
            let fullRequests = withFullReviews compactRequests
            let expected, actual = seededPair "materialize" path fullRequests
            Assert.Equal<string list>(expected, actual)
            let compactReplies =
                (fullRequests, expected) ||> List.zip
                |> List.choose (fun (line, reply) -> if line.Contains "\"detail\":\"full\"" then None else Some reply)
            use cut = parseDocument compactReplies[7]
            Assert.True(cut.RootElement.GetProperty("result").GetProperty("projection").GetProperty("requiresDetailedReview").GetBoolean())
            // Pin successful coverage: equal error replies are not a materialization test.
            for index in [ 0; 2; 3; 4; 5; 6; 7; 8; 9; 12 ] do
                use parsed = parseDocument compactReplies[index]
                Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), compactReplies[index])
            for index in [ common.Length + 3; common.Length + 4 ] do
                use parsed = parseDocument compactReplies[index]
                Assert.True(parsed.RootElement.GetProperty("result").GetProperty("review").GetProperty("mayWait").GetBoolean(), compactReplies[index])
            for reply in List.skip common.Length compactReplies do
                use parsed = parseDocument reply
                Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), reply)
            for index in 0 .. fullRequests.Length - 1 do
                if fullRequests[index].Contains "\"detail\":\"full\"" then
                    use parsed = parseDocument expected[index]
                    // Full reviews of successful compact requests must themselves succeed.
                    let compactIndex = if fullRequests[index].Contains "structuredReview" then index - 2 else index - 1
                    use compact = parseDocument expected[compactIndex]
                    Assert.Equal(compact.RootElement.GetProperty("ok").GetBoolean(), parsed.RootElement.GetProperty("ok").GetBoolean())
                    if parsed.RootElement.GetProperty("ok").GetBoolean() then
                        let result = parsed.RootElement.GetProperty("result")
                        Assert.Equal("full", result.GetProperty("detail").GetString())
                        Assert.False(fst (result.TryGetProperty "projection"))
                        let name = result.GetProperty("review").GetProperty("createdFunction").GetProperty("displayName").GetString()
                        if name.Length >= 20000 then Assert.True(Text.Encoding.UTF8.GetByteCount(expected[index]) > 16384)
                        let body = result.GetProperty("review").GetProperty("createdFunction").GetProperty("body")
                        Assert.True(body.GetProperty("nodes").GetArrayLength() > 0)
            for source in snapshots dir do
                let expected, actual = seededPair "materialize-examples" source (withFullReviews common)
                Assert.Equal<string list>(expected, actual))

    /// Same mismatch as the old in-process corpus: the type parameter and declared type both
    /// render as Point, so `typeRefPairIn` must disambiguate with the freshly allocated ID.
    let private genericMismatch =
        """{"id":"g","op":"simulateCreateFunction","displayName":"made","parameters":[{"displayName":"x","type":{"kind":"typeParameter","name":"Point"}}],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"kind":"listConstruct","itemType":{"kind":"named","typeId":"zig-Point"},"items":[{"kind":"parameterSlot","index":0}]}}"""

    let private diagnosticMessage (reply: string) =
        use parsed = parseDocument reply
        parsed.RootElement.GetProperty("error").GetProperty("details").GetProperty("diagnostics").[0].GetProperty("message").GetString()

    let private assertFresh n (reply: string) =
        let expected = StableIdAllocator.seededPayload "parity" n
        Assert.Contains($"Point (type parameter {expected})", diagnosticMessage reply)

    [<Fact; Trait("Suite", "Extended")>]
    let ``The seeded hosts spell consecutive fresh type parameter IDs`` () =
        withDirectory (fun dir ->
            let path = Path.Combine(dir, "generic.json")
            File.WriteAllText(path, SnapshotCodec.serialize (snapshot ()) |> unwrap)
            let exact = genericMismatch.Replace("\"name\":\"Point\"", "\"typeParameterId\":\"Point\"")
            let symbolic = genericMismatch.Replace("simulateCreateFunction", "simulateCreateFunctionByName").Replace("\"typeId\":\"zig-Point\"", "\"name\":\"Point\"").Replace("\"kind\":\"parameterSlot\",\"index\":0", "\"kind\":\"parameter\",\"name\":\"x\"")
            let fsharp, zig = seededPair "parity" path [ genericMismatch; exact; symbolic ]
            for replies in [ fsharp; zig ] do
                replies |> List.iteri (fun i reply -> assertFresh (int64 i + 1L) reply))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Preflight rejection consumes fresh type parameter IDs in declaration order`` () =
        withDirectory (fun dir ->
            let path = Path.Combine(dir, "generic.json")
            File.WriteAllText(path, SnapshotCodec.serialize (snapshot ()) |> unwrap)
            let declared = JsonNode.Parse genericMismatch
            declared["typeParameters"] <- JsonNode.Parse "[\"Other\",\"Point\"]"
            declared["parameters"].AsArray().Add(JsonNode.Parse """{"displayName":"y","type":{"kind":"typeParameter","name":"Other"}}""")
            let rejected = declared.DeepClone()
            rejected["expectedSnapshotId"] <- JsonValue.Create "wrong-base"
            let invalid = declared.DeepClone()
            invalid["typeParameters"] <- JsonNode.Parse "[\"Other\",\"Point\",\"Unused\"]"
            let fsharp, zig = seededPair "parity" path [ invalid.ToJsonString(); rejected.ToJsonString(); declared.ToJsonString(); genericMismatch ]
            for replies in [ fsharp; zig ] do
                Assert.Contains("FunctionCreationTypeParameterNotInferable", replies[0])
                Assert.Contains("StructuralExpectedBaseMismatch", replies[1])
                // Validation allocates nothing; stale base consumes two. Point is second in the
                // declared list (not parameter occurrence order), then the derived request takes 5.
                assertFresh 4L replies[2]
                assertFresh 5L replies[3])

    [<Fact; Trait("Suite", "Extended")>]
    let ``The seeded hosts observe existing IDs before allocating`` () =
        withDirectory (fun dir ->
            let taken = StableIdAllocator.seededPayload "parity" 30L
            let text = (SnapshotCodec.serialize (snapshot ()) |> unwrap).Replace("\"zig-twice\"", JsonSerializer.Serialize taken)
            // JSON only: the record form needs a real snapshot ID, and the guard reads the same text either way.
            let source = Path.Combine(dir, "guard.json")
            File.WriteAllText(source, text)
            let fsharp, zig = seededPair "parity" source [ genericMismatch; genericMismatch ]
            for replies in [ fsharp; zig ] do
                assertFresh 31L replies[0]
                assertFresh 32L replies[1])

    [<Fact; Trait("Suite", "Extended")>]
    let ``Without a seed the Zig host allocates real UUIDv7 identities`` () =
        withDirectory (fun dir ->
            let source = Path.Combine(dir, "generic.json")
            File.WriteAllText(source, SnapshotCodec.serialize (snapshot ()) |> unwrap)
            // Empty is unset, even if the parent test process inherited a seed.
            let code, errors, replies = runWithEnvironment [ "NYRWVY_TEST_ID_SEED", "" ] true [ "open"; "--snapshot"; source; "--read-only" ] [ genericMismatch; genericMismatch ]
            Assert.True((code = 0), errors)
            Assert.Equal("", errors)
            Assert.Equal(2, replies.Length)
            let ids = replies |> List.map (fun reply ->
                let found = Regex.Match(diagnosticMessage reply, "Point \\(type parameter ([0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12})\\)")
                Assert.True(found.Success, reply)
                let value = found.Groups[1].Value
                let milliseconds = Convert.ToInt64(value.Replace("-", "").Substring(0, 12), 16)
                Assert.InRange(milliseconds, DateTimeOffset.UtcNow.AddMinutes(-5.0).ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.AddMinutes(1.0).ToUnixTimeMilliseconds())
                value)
            Assert.NotEqual<string>(ids[0], ids[1]))

/// Body maintenance uses the existing owner's identities, not create-time parameter slots.
module ZigHostReplaceBodyTests =
    open ZigHostParityTests

    let private quoted (text: string) = JsonSerializer.Serialize text
    let private unit = """{"kind":"unitLiteral"}"""
    let private int = """{"kind":"int64Literal","value":7}"""
    let private bool = """{"kind":"boolLiteral","value":true}"""

    let private exact (f: SemanticFunction) body =
        $"""{{"id":"r","op":"simulateReplaceBody","functionId":{quoted (StableId.value f.Id)},"expectedCurrentResultNodeId":{quoted (StableId.value f.Body.ResultNodeId)},"replacement":{body}}}"""

    let private symbolic (f: SemanticFunction) body =
        $"""{{"id":"r","op":"simulateReplaceBodyByName","function":{{"id":{quoted (StableId.value f.Id)}}},"replacement":{body}}}"""

    let private changed (line: string) (change: JsonObject -> unit) =
        let root = JsonNode.Parse(line).AsObject()
        change root
        root.ToJsonString()

    let private fullReviews (lines: string list) =
        [ for line in lines do
              yield line
              yield changed line (fun root -> root["detail"] <- JsonValue.Create "full")
              yield changed line (fun root -> root["detail"] <- JsonValue.Create "full"; root["structuredReview"] <- JsonValue.Create true) ]

    // Keep the raw body text: reserializing it would discard duplicate properties and escapes.
    let private bodies (requests: string list) =
        [ for request in requests do
              use doc = parseDocument request
              match doc.RootElement.TryGetProperty "body" with
              | true, body -> yield body.GetRawText()
              | _ -> () ]

    let private strictPair seed source requests =
        let expected, actual = seededPair seed source requests
        for wanted, found in List.zip expected actual do
            // Existing accepted difference: .NET's lone-surrogate exception text is not ported.
            if wanted.Contains "\"code\":\"SessionError\"" then Assert.Equal(sessionErrorCode wanted, sessionErrorCode found)
            else Assert.Equal(wanted, found)
        expected

    let private mutations (f: SemanticFunction) =
        [ for line in [ exact f unit; symbolic f unit ] do
              use doc = parseDocument line
              let names = doc.RootElement.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Seq.filter (fun n -> n <> "id" && n <> "op") |> Seq.toList
              yield changed line (fun root -> root["extra"] <- JsonValue.Create true)
              for name in names do
                  yield changed line (fun root -> root.Remove name |> ignore)
                  for value in [ "null"; "3"; "[]"; "{}"; "true"; "\"\""; "\" \"" ] do
                      yield changed line (fun root -> root[name] <- JsonNode.Parse value)
              for name in [ "detail"; "structuredReview"; "expectedSnapshotId" ] do
                  yield changed line (fun root -> root[name] <- JsonValue.Create 3)
              yield changed line (fun root -> root["expectedSnapshotId"] <- JsonValue.Create "old")
          yield changed (exact f unit) (fun root -> root["functionId"] <- JsonValue.Create "missing")
          yield changed (exact f unit) (fun root -> root["expectedCurrentResultNodeId"] <- JsonValue.Create "old")
          yield changed (symbolic f unit) (fun root -> root["expectedResultNodeId"] <- JsonValue.Create "old")
          yield changed (symbolic f unit) (fun root -> root["function"] <- JsonValue.Create f.DisplayName)
          for selector in [ "\"missing\""; "{\"name\":\"missing\"}"; "{\"id\":\"missing\"}"; "{\"ownership\":\"bad\"}"; "{\"name\":3}"; "{\"extra\":true}" ] do
              yield changed (symbolic f unit) (fun root -> root["function"] <- JsonNode.Parse selector)
          // Field read order: exact IDs read function, base, result, recipe; symbolic reads selector first.
          yield changed (exact f unit) (fun root -> root["functionId"] <- JsonValue.Create 3; root["expectedSnapshotId"] <- JsonValue.Create 3)
          yield changed (exact f unit) (fun root -> root["expectedSnapshotId"] <- JsonValue.Create 3; root["expectedCurrentResultNodeId"] <- JsonValue.Create 3)
          yield changed (symbolic f unit) (fun root -> root["function"] <- JsonValue.Create "missing"; root["expectedSnapshotId"] <- JsonValue.Create 3)
          yield changed (symbolic f unit) (fun root -> root["replacementFile"] <- JsonValue.Create "nope.json") ]

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host decodes exact and symbolic body replacements with byte parity`` () =
        withDirectory (fun dir ->
            let inference = Path.Combine(dir, "inference.json")
            File.WriteAllText(inference, SnapshotCodec.serialize (ZigHostCreateInferenceTests.snapshot ()) |> unwrap)
            let mutable protocols = 0
            for source in inference :: snapshots dir do
                let declarations, snapshotId = declarationsOf source
                let f = declarations.Functions.Head
                let requests =
                    mutations f
                    @ (bodies (ZigHostCreateDecodeTests.recipeRequests ()) |> List.map (exact f))
                    @ (bodies (ZigHostCreateByNameTests.requests declarations snapshotId) |> List.map (symbolic f))
                let replies = strictPair "replace-decode" source requests
                protocols <- protocols + (replies |> List.filter (fun r -> r.Contains "\"code\":\"ProtocolError\"") |> List.length)
            Assert.True(protocols > 1000, $"Only {protocols} decode rejections."))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Replacement duplicate checks use the recipe root before named binding resolution and decoding`` () =
        withDirectory (fun dir ->
            let snapshot = ZigHostCreateInferenceTests.snapshot ()
            let source = Path.Combine(dir, "duplicates.json")
            File.WriteAllText(source, SnapshotCodec.serialize snapshot |> unwrap)
            let f = snapshot.Functions.Head
            // Raw strings retain duplicates, including objects inside arrays that decoding never reaches.
            let bodies =
                [ """{"kind":"stringConcat","left":3,"right":{"kind":"bind","name":"x","value":[{"a":1,"a":2}],"body":{"kind":"unitLiteral"}}}"""
                  """{"kind":"bind","name":"x","name":"y","value":{"kind":"unitLiteral"},"body":{"kind":"unitLiteral"}}"""
                  """{"kind":"stringConcat","left":{"kind":"patternReference","name":"missing"},"right":[{"a":1,"a":2}]}"""
                  // Without a name marker, preserve decode order instead of scanning for duplicates first.
                  """{"kind":"stringConcat","left":3,"right":{"kind":"unitLiteral","kind":"unitLiteral"}}"""
                  """{"kind":"stringConcat","left":{"kind":"unitLiteral"},"right":{"kind":"unitLiteral","kind":"unitLiteral"}}""" ]
            let requests = [ for request in [ exact f; symbolic f ] do for body in bodies do yield request body ]
            let replies = strictPair "replace-duplicates" source requests
            for index, reply in List.indexed replies do
                use parsed = parseDocument reply
                let error = parsed.RootElement.GetProperty("error")
                Assert.Equal("ProtocolError", error.GetProperty("code").GetString())
                let message = error.GetProperty("message").GetString()
                if index % bodies.Length <> 3 then Assert.Equal("JSON object contains duplicate property names.", message)
                else Assert.NotEqual<string>("JSON object contains duplicate property names.", message))

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host infers replacement recipes in their existing owner context`` () =
        withDirectory (fun dir ->
            let snapshot = ZigHostCreateInferenceTests.snapshot ()
            let source = Path.Combine(dir, "inference.json")
            File.WriteAllText(source, SnapshotCodec.serialize snapshot |> unwrap)
            let find name = snapshot.Functions |> List.find (fun f -> f.DisplayName = name)
            let f = find "unit"
            let generic = find "generic"
            let parameter (p: SemanticParameter) = $"""{{"kind":"parameter","parameterId":{quoted (StableId.value p.Id)}}}"""
            let genericCall value = $"""{{"kind":"directCall","functionId":"zig-generic","arguments":[{{"parameterId":"zig-generic-p0","value":{value}}}]}}"""
            let lines =
                (bodies (ZigHostCreateInferenceTests.requests ()) |> List.map (exact f))
                @ [ exact generic (genericCall int)
                    exact generic (genericCall (parameter generic.Parameters.Head))
                    exact generic (parameter (find "twice").Parameters.Head)
                    exact f """{"kind":"parameter","parameterId":"unknown"}"""
                    exact f """{"kind":"parameterSlot","index":0}"""
                    exact generic int
                    symbolic generic """{"kind":"parameter","name":"p0"}"""
                    symbolic generic """{"kind":"directCall","function":"generic","arguments":[],"keepUnlisted":true}"""
                    symbolic generic """{"kind":"directCall","function":{"id":"zig-generic"},"arguments":[],"keepUnlisted":true}"""
                    symbolic generic """{"kind":"listEmpty","itemType":{"kind":"typeParameter","name":"T"}}"""
                    symbolic generic """{"kind":"listEmpty","itemType":{"kind":"typeParameter","name":"missing"}}"""
                    exact (find "twice") int
                    exact (find "twice") $"""{{"kind":"abort","error":{int}}}""" ]
            let replies = strictPair "replace-inference" source lines
            let text = String.concat "\n" replies
            for code in [ "PureExpressionParameterOutOfScope"; "PureExpressionParameterSlotUnavailable"; "PureExpressionResultTypeMismatch"; "PureExpressionCallArgumentTypeMismatch"; "AbortOwnerResultTypeMismatch" ] do
                Assert.Contains(code, text)
            Assert.Contains("At replacement", text))

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host mixes seeded body replacement and create proposals in every review form`` () =
        withDirectory (fun dir ->
            let inference = Path.Combine(dir, "inference.json")
            File.WriteAllText(inference, SnapshotCodec.serialize (ZigHostCreateInferenceTests.snapshot ()) |> unwrap)
            for source in inference :: snapshots dir do
                let functions = (fst (declarationsOf source)).Functions
                let lines =
                    [ for f in functions do
                          // A complete self call preserves the result type and existing parameter IDs.
                          let args = f.Parameters |> List.mapi (fun i p -> $"""{{"slot":{i},"value":{{"kind":"parameter","parameterId":{quoted (StableId.value p.Id)}}}}}""") |> String.concat ","
                          yield exact f $"""{{"kind":"selfCall","arguments":[{args}]}}"""
                          yield symbolic f $"""{{"kind":"directCall","function":{{"id":{quoted (StableId.value f.Id)}}},"arguments":[],"keepUnlisted":true}}"""
                          yield exact f $"""{{"kind":"bind","value":{int},"body":{{"kind":"selfCall","arguments":[{args}]}}}}"""
                          // Both successes and mismatches between other successes consume the same IDs.
                          yield exact f bool
                          yield exact f unit
                          yield changed (exact f unit) (fun root -> root["expectedCurrentResultNodeId"] <- JsonValue.Create "stale") ]
                    |> fullReviews
                let create = """{"id":"c","op":"simulateCreateFunction","displayName":"replace-test-created","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"kind":"unitLiteral"}}"""
                let requests = create :: lines @ [ create; """{"id":"p","op":"proposals"}"""; """{"id":"s","op":"status"}"""; """{"id":"d","op":"declarations"}""" ]
                let replies = strictPair "replace-mixed" source requests
                let successes = replies |> List.filter (fun reply -> reply.Contains "\"kind\":\"replaceBody\"")
                Assert.True(successes.Length >= functions.Length * 9, $"Only {successes.Length} successful replacement reviews."))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Replacement validation failures consume IDs and compact reviews project oversized names`` () =
        withDirectory (fun dir ->
            let original = ZigHostCreateInferenceTests.snapshot ()
            let snapshot =
                { original with
                    Functions =
                        original.Functions |> List.map (fun f ->
                            if f.DisplayName = "unit" then { f with MustNotWait = true }
                            elif f.DisplayName = "identity" then { f with DisplayName = String.replicate 20000 "x" }
                            else f) }
            let f = snapshot.Functions |> List.find (fun f -> f.DisplayName = "unit")
            let large = snapshot.Functions |> List.find (fun f -> f.DisplayName.Length = 20000)
            let source = Path.Combine(dir, "validation.json")
            File.WriteAllText(source, SnapshotCodec.serialize snapshot |> unwrap)
            let wait = """{"kind":"bind","value":{"kind":"taskWait","tasks":{"kind":"listEmpty","itemType":{"kind":"builtinGeneric","generic":"task","arguments":[{"kind":"primitive","primitive":"int64"}]}},"mode":"all"},"body":{"kind":"unitLiteral"}}"""
            let readParameter = $"""{{"kind":"parameter","parameterId":{quoted (StableId.value large.Parameters.Head.Id)}}}"""
            let original = """{"kind":"originalTarget"}"""
            let longLiteral = quoted (String.replicate 255 "x" + "🚀" + String.replicate 300 "y")
            let wrap = $"""{{"kind":"bind","value":{{"kind":"stringLiteral","value":{longLiteral}}},"body":{original}}}"""
            let lines = [ exact f wait; exact f unit; exact large readParameter; symbolic f wait; exact f unit; exact large original; exact f wrap ] |> fullReviews
            let replies = strictPair "replace-validation" source lines
            Assert.Contains("MustNotWaitFunctionWaits", replies[0])
            use compact = parseDocument replies[6]
            Assert.True(compact.RootElement.GetProperty("result").GetProperty("projection").GetProperty("requiresDetailedReview").GetBoolean()))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Body replacement refuses bodies referenced by any obligation`` () =
        withDirectory (fun dir ->
            let fixture = MilestoneZeroGraph.create ()
            let obligationId = StableId.createForTesting<ObligationTag> "replace-obligation"
            let simulate snapshot name operations =
                Transaction.simulate snapshot
                    { Id = StableId.createForTesting<TransactionTag> ("replace-" + name)
                      BaseSnapshotId = snapshot.Id
                      ProposedSnapshotId = StableId.createForTesting<SnapshotTag> ("replace-" + name)
                      Operations = operations }
                |> function Proposed result -> result.ProposedSnapshot | Rejected errors -> failwithf "%A" errors
            let incomplete =
                simulate fixture.Snapshot "incomplete"
                    [ AddFunctionParameter
                        { TargetFunctionId = fixture.ValidateFunctionId
                          NewParameterId = StableId.createForTesting<ParameterTag> "replace-policy"
                          NewParameterDisplayName = "extraPolicy"
                          NewParameterType = TypeRef.Named(fixture.PolicyTypeId, [])
                          CallSitePlans = [ LeaveMissing(fixture.MainValidateCallNodeId, obligationId) ] } ]
            let resolved =
                simulate incomplete "resolved"
                    [ ResolveMissingArgument { TargetObligationId = obligationId; ResolutionValueNodeId = fixture.MainPolicyNodeId } ]
            for index, snapshot in List.indexed [ incomplete; resolved ] do
                let source = Path.Combine(dir, $"obligations-{index}.json")
                File.WriteAllText(source, SnapshotCodec.serialize snapshot |> unwrap)
                let lines =
                    [ for obligation in snapshot.Obligations do
                          let f = snapshot.Functions |> List.find (fun f -> ValueGraph.collectNodes f.Body |> List.exists (fun n -> n.Id = obligation.CallNodeId))
                          let args = f.Parameters |> List.mapi (fun i p -> $"""{{"slot":{i},"value":{{"kind":"parameter","parameterId":{quoted (StableId.value p.Id)}}}}}""") |> String.concat ","
                          yield exact f $"""{{"kind":"selfCall","arguments":[{args}]}}"""
                          yield symbolic f $"""{{"kind":"directCall","function":{{"id":{quoted (StableId.value f.Id)}}},"arguments":[],"keepUnlisted":true}}""" ]
                let replies = strictPair "replace-obligation" source lines
                Assert.NotEmpty replies
                for reply in replies do Assert.Contains("StructuralBodyReferencedByObligation", reply)
                let targets =
                    [ for obligation in snapshot.Obligations do
                          let f = snapshot.Functions |> List.find (fun f -> ValueGraph.collectNodes f.Body |> List.exists (fun n -> n.Id = obligation.CallNodeId))
                          yield changed (exact f """{"kind":"originalTarget"}""") (fun root ->
                              root["op"] <- JsonValue.Create "simulateReplaceTarget"
                              root.Remove "expectedCurrentResultNodeId" |> ignore
                              root["targetNodeId"] <- JsonValue.Create (StableId.value obligation.CallNodeId)) ] |> fullReviews
                for reply in strictPair "replace-target-obligation" source targets do
                    Assert.Contains("TargetedExpressionReferencedByObligation", reply))

    [<Fact; Trait("Suite", "Extended")>]
    let ``File recipes share confined parsing and seeded successful transcripts`` () =
        withDirectory (fun dir ->
            let snapshot = ZigHostCreateInferenceTests.snapshot ()
            let f = snapshot.Functions |> List.find (fun f -> f.DisplayName = "unit")
            let source = Path.Combine(dir, "files.json")
            File.WriteAllText(source, SnapshotCodec.serialize snapshot |> unwrap)
            File.WriteAllText(Path.Combine(dir, "valid.json"), unit)
            File.WriteAllText(Path.Combine(dir, "wrap.json"), """{"kind":"bind","value":{"kind":"originalTarget"},"body":{"kind":"patternReference","slot":"value","scopeDepth":0}}""")
            File.WriteAllText(Path.Combine(dir, "invalid.json"), "{")
            File.WriteAllText(Path.Combine(dir, "large.json"), String.replicate 1048577 " ")
            File.WriteAllText(Path.Combine(dir, "deep.json"), String.replicate 836 "[" + "0" + String.replicate 836 "]")
            File.WriteAllText(Path.Combine(dir, "duplicate.json"), """{"kind":"unitLiteral","kind":"unitLiteral"}""")
            // A recipe's String literal may contain a seeded ID. This read uses Parse(stream),
            // not SessionStorage.readText, and must NOT advance the restart guard.
            let seeded = StableIdAllocator.seededPayload "file-recipes" 1000L
            File.WriteAllText(Path.Combine(dir, "id.json"), $"""{{"kind":"stringLiteral","value":{quoted seeded}}}""")
            Directory.CreateDirectory(Path.Combine(dir, "nested")) |> ignore
            Directory.CreateSymbolicLink(Path.Combine(dir, "link"), Path.Combine(dir, "nested")) |> ignore
            File.WriteAllText(Path.Combine(dir, "nested", "valid.json"), unit)
            let create = """{"id":"r","op":"simulateCreateFunctionByName","displayName":"fromFile","parameters":[],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":[],"body":{"kind":"unitLiteral"}}"""
            let target = changed (symbolic f unit) (fun root ->
                root["op"] <- JsonValue.Create "simulateReplaceTargetByName"
                root["targetNodeId"] <- JsonValue.Create (StableId.value f.Body.ResultNodeId))
            let fileInput inlineName (fileName: string) line (value: string) = changed line (fun root ->
                root.Remove inlineName |> ignore
                root[fileName] <- JsonNode.Parse value)
            let lines =
                [ for line, inlineName, fileName in [ create, "body", "bodyFile"; symbolic f unit, "replacement", "replacementFile"; target, "replacement", "replacementFile" ] do
                      for value in [ quoted "valid.json"; quoted "nested/../valid.json"; quoted "../outside.json"; quoted "."; quoted "link/valid.json"; quoted "missing.json"; quoted "large.json"; quoted "invalid.json"; quoted "deep.json"; quoted "duplicate.json"; "3"; "null"; quoted ""; quoted " "; quoted "bad\u0000name" ] do
                          yield fileInput inlineName fileName line value
                      yield fileInput inlineName fileName line (quoted "valid.json")
                      yield changed line (fun root -> root[fileName] <- JsonValue.Create "valid.json")
                      yield changed line (fun root -> root.Remove inlineName |> ignore)
                  yield fileInput "replacement" "replacementFile" (symbolic f unit) (quoted "wrap.json")
                  yield fileInput "replacement" "replacementFile" target (quoted "wrap.json")
                  yield fileInput "body" "bodyFile" (changed create (fun root -> root["resultType"] <- JsonNode.Parse """{"kind":"primitive","primitive":"string"}""")) (quoted "id.json")
                  yield create
                  yield exact f unit ] |> fullReviews
            // Run against the same immutable directory: seededPair copies regular inputs, which
            // would dereference the directory link this corpus specifically needs to reject.
            let runFiles zig =
                let code, errors, replies = runWithEnvironment [ "NYRWVY_TEST_ID_SEED", "file-recipes" ] zig [ "open"; "--snapshot"; source; "--read-only" ] lines
                Assert.Equal(0, code)
                Assert.Equal(seedWarning, errors)
                replies
            let replies = runFiles false
            let actual = runFiles true
            Assert.Equal(replies.Length, actual.Length)
            for expected, found in List.zip replies actual do Assert.Equal(expected, found)
            let text = String.concat "\n" replies
            for message in [ "must stay inside"; "must not pass through"; "must name an existing JSON file"; "exceeds 1 MiB"; "must contain valid JSON"; "must be a file path"; "cannot be read as a JSON file" ] do
                Assert.Contains(message, text)
            Assert.Contains("\"kind\":\"replaceTarget\"", text)
            Assert.Contains("\"kind\":\"createFunction\"", text))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Target review focuses are complete sibling objects and errors leave the host alive`` () =
        withDirectory (fun dir ->
            let snapshot = ZigHostCreateInferenceTests.snapshot ()
            let f = snapshot.Functions |> List.find (fun f -> f.DisplayName = "unit")
            let source = Path.Combine(dir, "focus.json")
            File.WriteAllText(source, SnapshotCodec.serialize snapshot |> unwrap)
            File.WriteAllText(Path.Combine(dir, "invalid.json"), "{")
            let original = """{"kind":"originalTarget"}"""
            let target = changed (symbolic f original) (fun root ->
                root["op"] <- JsonValue.Create "simulateReplaceTargetByName"
                root["targetNodeId"] <- JsonValue.Create (StableId.value f.Body.ResultNodeId))
            let invalid = changed target (fun root ->
                root.Remove "replacement" |> ignore
                root["replacementFile"] <- JsonValue.Create "invalid.json")
            let proposals = [ exact f original; symbolic f original; target ] |> fullReviews
            let read = $"""{{"id":"read","op":"readFunction","functionId":{quoted (StableId.value f.Id)}}}"""
            let reads = [ "compact"; "outline"; "full" ] |> List.map (fun detail -> changed read (fun root -> root["detail"] <- JsonValue.Create detail))
            let lines = invalid :: proposals @ [ invalid ] @ reads
            let replies = strictPair "target-focus-json" source lines
            Assert.Contains("must contain valid JSON", replies.Head)
            for reply in replies |> List.skip 1 |> List.take proposals.Length do
                use doc = parseDocument reply
                Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), reply)
                let review = doc.RootElement.GetProperty("result").GetProperty("review")
                Assert.Equal("replaceTarget", review.GetProperty("kind").GetString())
                // A missing brace used to nest proposedFocus inside baseFocus, then either
                // crash compact projection with error.Json or emit truncated full JSON.
                Assert.Equal(JsonValueKind.Object, review.GetProperty("baseFocus").ValueKind)
                Assert.Equal(JsonValueKind.Object, review.GetProperty("proposedFocus").ValueKind)
            Assert.Equal(lines.Length, replies.Length))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Targeted replacements retain identities and match every review form`` () =
        withDirectory (fun dir ->
            let source = Path.Combine(dir, "targeted.json")
            File.WriteAllText(source, SnapshotCodec.serialize (ZigHostCreateInferenceTests.snapshot ()) |> unwrap)
            let original = """{"kind":"originalTarget"}"""
            let wrap = """{"kind":"bind","value":{"kind":"originalTarget"},"body":{"kind":"patternReference","scopeDepth":0,"slot":"value"}}"""
            let repeated = """{"kind":"bind","value":{"kind":"originalTarget"},"body":{"kind":"originalTarget"}}"""
            let bad = """{"kind":"boolNot","value":{"kind":"originalTarget"}}"""
            let targeted byName (f: SemanticFunction) node body =
                changed ((if byName then symbolic else exact) f body) (fun root ->
                    root["op"] <- JsonValue.Create (if byName then "simulateReplaceTargetByName" else "simulateReplaceTarget")
                    root.Remove "expectedCurrentResultNodeId" |> ignore
                    root.Remove "expectedResultNodeId" |> ignore
                    root["targetNodeId"] <- JsonValue.Create (StableId.value node))
            let mutable successes = 0
            for path in source :: snapshots dir do
                let functions = (fst (declarationsOf path)).Functions
                let requests =
                    [ for f in functions do
                          // Body wrappers take the same target path, including the allocated old-root ID
                          // when inference rejects after originalTarget has been captured.
                          for body in [ original; wrap; repeated; bad ] do
                              yield exact f body
                              yield symbolic f body
                          for n in ValueGraph.collectNodes f.Body do
                              for byName in [ false; true ] do
                                  for body in [ original; wrap; repeated; bad; unit; bool; int
                                                """{"kind":"patternReference","scopeDepth":0,"slot":"value"}"""
                                                """{"kind":"patternReference","scopeDepth":0,"slot":"error"}"""
                                                """{"kind":"abort","error":{"kind":"int64Literal","value":7}}""" ] do
                                      yield targeted byName f n.Id body
                          for byName in [ false; true ] do
                              let line = targeted byName f f.Body.ResultNodeId original
                              yield changed line (fun root -> root["targetNodeId"] <- JsonValue.Create "missing")
                              yield changed line (fun root -> root["expectedSnapshotId"] <- JsonValue.Create "stale")
                              for key in [ "targetNodeId"; "replacement" ] do
                                  yield changed line (fun root -> root.Remove key |> ignore)
                                  yield changed line (fun root -> root[key] <- JsonValue.Create 3)
                          yield exact f unit ]
                    |> fullReviews
                let requests = requests @ [ """{"id":"p","op":"proposals"}"""; """{"id":"s","op":"status"}"""; """{"id":"d","op":"declarations"}""" ]
                let replies = strictPair "targeted-replacements" path requests
                for reply in replies do
                    use doc = parseDocument reply
                    if doc.RootElement.GetProperty("ok").GetBoolean() then
                        successes <- successes + 1
                Assert.Contains("TargetedExpressionOriginalTargetRepeated", String.concat "\n" replies)
            Assert.True(successes > 100, $"Only {successes} successful target/create replies."))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Replacement registration protects imported functions and hidden dependency references`` () =
        withDirectory (fun dir ->
            let original = ZigHostCreateInferenceTests.snapshot ()
            let dependency = { original with Functions = original.Functions |> List.map (fun f -> if f.DisplayName = "identity" then { f with Exported = false } else f) }
            let imported = dependency.Functions |> List.find (fun f -> f.DisplayName = "twice")
            let nodeId = StableId.createForTesting<GraphNodeTag> "replace-local-node"
            let localFunction =
                { imported with Id = StableId.createForTesting<FunctionTag> "replace-local"; DisplayName = "local"
                                Parameters = []; Body = { Nodes = [ { Id = nodeId; Kind = SemanticNodeKind.BoolLiteral true } ]; ResultNodeId = nodeId } }
            let local = { Snapshot.empty (StableId.createForTesting<SnapshotTag> "replace-local-snapshot") with Functions = [ localFunction ] }
            let source = Path.Combine(dir, "resolved.json")
            File.WriteAllText(source, SnapshotCodec.serializeResolved dependency local |> unwrap)
            let manifest = memberOf dependency (Some("snapshotJson", SnapshotCodec.serialize dependency |> unwrap)) "library.json"
            File.WriteAllText(source + ".dependency.json", manifest.ToJsonString())
            let hiddenCall = $"""{{"kind":"bind","value":{{"kind":"directCall","functionId":"zig-identity","arguments":[{{"parameterId":"zig-identity-p0","value":{int}}}]}},"body":{bool}}}"""
            let requests = [ exact imported bool; exact localFunction bool; symbolic imported bool; exact localFunction hiddenCall; exact localFunction bool ]
            let replies = strictPair "replace-imports" source requests
            Assert.Contains("ImportedDeclarationModified", replies[0])
            Assert.Contains("ImportedDeclarationModified", replies[2])
            Assert.Contains("ImportedDeclarationNotExported", replies[3])
            for index in [ 1; 4 ] do
                use reply = parseDocument replies[index]
                Assert.True(reply.RootElement.GetProperty("ok").GetBoolean(), replies[index]))


module ZigHostCommitParityTests =
    open ZigHostParityTests

    let private create name =
        $"""{{"id":"create","op":"simulateCreateFunctionByName","displayName":"{name}","parameters":[],"resultType":{{"kind":"primitive","primitive":"unit"}},"requiredEffects":[],"body":{{"kind":"bind","value":{{"kind":"int64Literal","value":7}},"body":{{"kind":"bind","value":{{"kind":"stringLiteral","value":"kept"}},"body":{{"kind":"unitLiteral"}}}}}}}}"""
    let private proposal (reply: string) =
        use doc = parseDocument reply
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), reply)
        doc.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
    let private operation op id = $"""{{"id":"{op}","op":"{op}","proposalId":"{id}"}}"""
    let private reads =
        [ """{"id":"s","op":"status"}"""
          """{"id":"d","op":"declarations"}"""
          """{"id":"p","op":"proposals"}"""
          """{"id":"f","op":"readFunction","function":"made","detail":"full"}"""
          """{"id":"fc","op":"readFunction","function":"made","detail":"compact"}"""
          """{"id":"fo","op":"readFunction","function":"made","detail":"outline"}"""
          """{"id":"q","op":"search","text":"made"}"""
          """{"id":"e","op":"executionReadiness"}""" ]
    let private succeeded (reply: string) =
        use doc = parseDocument reply
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), reply)

    [<Fact>]
    let ``Record fixtures round-trip without consuming transcript IDs`` () =
        let snapshots =
            ZigHostCreateInferenceTests.snapshot ()
            :: (MilestoneZeroGraph.create ()).Snapshot
            :: RecordCodecAllKindsTests.allKindSnapshots ()
        for index, snapshot in List.indexed snapshots do
            let fixture = recordFixture $"roundtrip-{index}" snapshot
            let records = RecordCodec.serialize fixture |> unwrap
            let restored = RecordCodec.deserialize records |> unwrap
            Assert.Equal(records, RecordCodec.serialize restored |> unwrap)
            Assert.Equal(SnapshotCodec.serialize fixture |> unwrap, SnapshotCodec.serialize restored |> unwrap)
            for seed in [ "commit-parity"; "commit-legacy-record" ] do
                Assert.Equal(0L, StableIdAllocator.seededCount seed records)
        // These fixed UUIDv7 timestamps predate every seeded allocator's range.
        for id in [ "018bcfe5-6700-7000-8000-000000000001"; "018bcfe5-6700-7000-8000-000000000002" ] do
            let snapshot = Snapshot.empty (StableId.createForTesting<SnapshotTag> id)
            let records = RecordCodec.serialize snapshot |> unwrap
            Assert.Equal(snapshot, RecordCodec.deserialize records |> unwrap)
            for seed in [ "commit-parity"; "commit-legacy-record" ] do
                Assert.Equal(0L, StableIdAllocator.seededCount seed records)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Commit and reject persist identical files and install the next base in both formats`` () =
        withDirectory (fun dir ->
            for records in [ false; true ] do
                for resolved in [ false; true ] do
                    let case = Path.Combine(dir, $"{records}-{resolved}")
                    let input = Path.Combine(case, "input")
                    Directory.CreateDirectory input |> ignore
                    let dependency = ZigHostCreateInferenceTests.snapshot () |> recordFixture "commit-dependency"
                    let local = Snapshot.empty (StableId.createForTesting<SnapshotTag> "018bcfe5-6700-7000-8000-000000000001")
                    let json = if resolved then SnapshotCodec.serializeResolved dependency local |> unwrap else SnapshotCodec.serialize local |> unwrap
                    let source = Path.Combine(input, if records then "snapshot.nyrec" else "snapshot.json")
                    let text = if records then RecordCodec.ofJson json else json
                    if records then
                        let restored = if resolved then RecordCodec.deserializeResolved dependency text |> unwrap else RecordCodec.deserialize text |> unwrap
                        let encoded = if resolved then RecordCodec.serializeResolved dependency restored |> unwrap else RecordCodec.serialize restored |> unwrap
                        Assert.Equal(text, encoded)
                    File.WriteAllText(source, text)
                    if resolved then
                        let manifest = memberOf dependency (Some("snapshotJson", SnapshotCodec.serialize dependency |> unwrap)) "library.json"
                        File.WriteAllText(source + ".dependency.json", manifest.ToJsonString())
                        File.WriteAllText(source + ".dependency.json.previous", "old recovery copy\n")
                    File.WriteAllText(source + ".backends.json", "{\"targets\": [\"csharp\"]}\n")
                    File.WriteAllText(source + ".previous", "unchanged\n")
                    let fsharpDir, zigDir =
                        seededWritablePair "commit-parity" source case (fun _ send ->
                            let first = send (create "made") |> proposal
                            let stale = send (create "stale") |> proposal
                            send (operation "commit" first) |> succeeded
                            for request in reads do send request |> succeeded
                            let rejected = send (operation "commit" stale)
                            Assert.Contains("StaleBaseSnapshot", rejected)
                            send (operation "reject" stale) |> succeeded
                            let replacement = send """{"id":"replace","op":"simulateReplaceBodyByName","function":"made","detail":"full","replacement":{"kind":"unitLiteral"}}""" |> proposal
                            send (operation "commit" replacement) |> succeeded
                            for request in reads do send request |> succeeded
                            let last = send (create "forgotten") |> proposal
                            send (operation "reject" last) |> succeeded
                            Assert.Contains("ProposalNotFound", send (operation "reject" last))
                            Assert.Contains("ProposalNotFound", send (operation "reject" "unknown")))
                    // Each output is opened by the other implementation, on the very same path.
                    for output in [ fsharpDir; zigDir ] do
                        let path = Path.Combine(output, Path.GetFileName source)
                        let runRead zig =
                            let code, errors, replies = run zig [ "open"; "--snapshot"; path; "--read-only" ] reads
                            Assert.True((code = 0), errors)
                            replies |> List.map comparable
                        Assert.Equal<string list>(runRead false, runRead true))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Commit decoding read-only precedence and rejection agree`` () =
        withDirectory (fun dir ->
            let source = Path.Combine(dir, "snapshot.json")
            File.WriteAllText(source, SnapshotCodec.serialize (Snapshot.empty (StableId.createForTesting<SnapshotTag> "commit-readonly")) |> unwrap)
            let discovered, _ = seededPair "commit-readonly" source [ create "made" ]
            let pending = proposal discovered.Head
            let requests =
                [ create "made"
                  operation "commit" "unknown"
                  operation "commit" pending
                  operation "reject" pending
                  operation "reject" pending
                  """{"id":"c","op":"commit","allowIncomplete":"bad","proposalId":3}"""
                  """{"id":"c","op":"commit","proposalId":"x","proposalId":"y"}"""
                  """{"id":"c","op":"commit","proposalId":null}"""
                  """{"id":"c","op":"commit","proposalId":""}"""
                  """{"id":"c","op":"commit","extra":true}"""
                  """{"id":"c","op":"reject","proposalId":false}"""
                  operation "reject" "unknown" ]
            let expected, actual = seededPair "commit-readonly" source requests
            Assert.Equal<string list>(expected, actual)
            Assert.Contains("ReadOnlySession", expected[1]))

    [<Fact; Trait("Suite", "Extended")>]
    let ``External modification invalidates publication without overwriting the changed file`` () =
        withDirectory (fun dir ->
            let input = Path.Combine(dir, "input")
            Directory.CreateDirectory input |> ignore
            let source = Path.Combine(input, "snapshot.json")
            File.WriteAllText(source, SnapshotCodec.serialize (Snapshot.empty (StableId.createForTesting<SnapshotTag> "commit-external")) |> unwrap)
            seededWritablePair "commit-external" source dir (fun path send ->
                let pending = send (create "made") |> proposal
                File.AppendAllText(path, "\n")
                Assert.Contains("ExternalModification", send (operation "commit" pending))
                Assert.Contains("SessionInvalidated", send (operation "commit" pending))
                Assert.Contains("SessionInvalidated", send (operation "reject" pending))
                Assert.Contains("SessionInvalidated", send (create "after-external"))) |> ignore)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Blocking obligations require explicit allowIncomplete at commit`` () =
        withDirectory (fun dir ->
            let fixture = MilestoneZeroGraph.create ()
            let incomplete =
                Transaction.simulate fixture.Snapshot
                    { Id = StableId.createForTesting<TransactionTag> "commit-incomplete"
                      BaseSnapshotId = fixture.Snapshot.Id
                      ProposedSnapshotId = StableId.createForTesting<SnapshotTag> "commit-incomplete"
                      Operations =
                        [ AddFunctionParameter
                            { TargetFunctionId = fixture.ValidateFunctionId
                              NewParameterId = StableId.createForTesting<ParameterTag> "commit-policy"
                              NewParameterDisplayName = "extraPolicy"
                              NewParameterType = TypeRef.Named(fixture.PolicyTypeId, [])
                              CallSitePlans = [ LeaveMissing(fixture.MainValidateCallNodeId, StableId.createForTesting<ObligationTag> "commit-missing") ] } ] }
                |> function
                | Proposed result -> result.ProposedSnapshot
                | Rejected errors -> failwithf "%A" errors
            let input = Path.Combine(dir, "input")
            Directory.CreateDirectory input |> ignore
            let source = Path.Combine(input, "snapshot.json")
            File.WriteAllText(source, SnapshotCodec.serialize incomplete |> unwrap)
            seededWritablePair "commit-incomplete" source dir (fun _ send ->
                let pending = send (create "made") |> proposal
                Assert.Contains("BlockingObligations", send (operation "commit" pending))
                send $"""{{"id":"allow","op":"commit","proposalId":"{pending}","allowIncomplete":true}}""" |> succeeded
                for request in reads do send request |> ignore) |> ignore)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Record commits can reread a legacy record dependency after each request arena is freed`` () =
        withDirectory (fun dir ->
            let input = Path.Combine(dir, "input")
            Directory.CreateDirectory input |> ignore
            let dependency = ZigHostCreateInferenceTests.snapshot () |> recordFixture "commit-dependency"
            let library = Path.Combine(input, "library.nyrec")
            let libraryText = RecordCodec.serialize dependency |> unwrap
            Assert.Equal(libraryText, RecordCodec.deserialize libraryText |> unwrap |> RecordCodec.serialize |> unwrap)
            File.WriteAllText(library, libraryText)
            let local = Snapshot.empty (StableId.createForTesting<SnapshotTag> "018bcfe5-6700-7000-8000-000000000002")
            let source = Path.Combine(input, "snapshot.nyrec")
            let text = RecordCodec.serializeResolved dependency local |> unwrap
            Assert.Equal(text, RecordCodec.deserializeResolved dependency text |> unwrap |> RecordCodec.serializeResolved dependency |> unwrap)
            File.WriteAllText(source, text)
            // A legacy selection's absolute authority is immutable and shared by both sessions.
            File.WriteAllText(source + ".dependency.json", (memberOf dependency None library).ToJsonString())
            seededWritablePair "commit-legacy-record" source dir (fun _ send ->
                for name in [ "made"; "other"; "last" ] do
                    let pending = send (create name) |> proposal
                    send (operation "commit" pending) |> succeeded
                    for request in reads do send request |> succeeded) |> ignore)

module ZigHostProposalParityTests =
    open ZigHostParityTests

    let private proposal (reply: string) =
        use doc = parseDocument reply
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), reply)
        doc.RootElement.GetProperty("result").GetProperty("proposalId").GetString()

    let private operation op id = $"""{{"id":"{op}","op":"{op}","proposalId":"{id}"}}"""

    let private create detail =
        // A traversal order different from canonical node order, plus UTF-16 paging boundaries.
        $"""{{"id":"create","op":"simulateCreateFunctionByName","displayName":"made","parameters":[],"resultType":{{"kind":"primitive","primitive":"string"}},"requiredEffects":[],{detail}"body":{{"kind":"stringConcat","left":{{"kind":"stringLiteral","value":"a😀b"}},"right":{{"kind":"stringLiteral","value":"tail"}}}}}}"""

    let private read id fields = $"""{{"id":"read","op":"readProposal","proposalId":"{id}"{fields}}}"""

    let private exercise (send: string -> string) id label =
        for structured in [ "false"; "true" ] do
            for path in [ ""; "/review"; "/review/receipt"; "/review/receipt/transactionId"; "/detail" ] do
                for offset, limit in [ 0, 1; 1, 2; 0, 20 ] do
                    send (read id $""", "structuredReview":{structured},"path":"{path}","offset":{offset},"limit":{limit}""") |> ignore
        send (operation "viewProposal" id) |> ignore
        send $"""{{"id":"view","op":"viewProposal","proposalId":"{id}","output":"views/{label}.html"}}""" |> ignore
        // The same generated page can be replaced, but unrelated files cannot.
        send $"""{{"id":"view","op":"viewProposal","proposalId":"{id}","output":"views/{label}.html"}}""" |> ignore
        for output in [ ""; "wrong.txt"; "../escape.html"; "ordinary.html"; "blocked/page.html" ] do
            send $"""{{"id":"view","op":"viewProposal","proposalId":"{id}","output":"{output}"}}""" |> ignore
        for fields in
            [ ",\"path\":\"not-a-pointer\""; ",\"path\":\"/~2\""; ",\"path\":\"/absent\""
              ",\"path\":\"/detail/x\""; ",\"offset\":-1"; ",\"offset\":99999"; ",\"limit\":0"
              ",\"limit\":21"; ",\"offset\":-0"; ",\"offset\":1.0"; ",\"limit\":2147483648"; ",\"structuredReview\":null"
              ",\"detail\":\"compact\""; ",\"detail\":\"full\""; ",\"path\":null"; ",\"offset\":null,\"limit\":null" ] do
            send (read id fields) |> ignore

    [<Fact; Trait("Suite", "Extended")>]
    let ``Pending reviews and pages match for creates body and targeted replacements including stale handles`` () =
        withDirectory (fun dir ->
            let input = Path.Combine(dir, "input")
            Directory.CreateDirectory input |> ignore
            let source = Path.Combine(input, "snapshot.json")
            File.WriteAllText(source, SnapshotCodec.serialize (Snapshot.empty (StableId.createForTesting<SnapshotTag> "proposal-pages")) |> unwrap)
            File.WriteAllText(Path.Combine(input, "ordinary.html"), "not generated")
            File.WriteAllText(Path.Combine(input, "blocked"), "not a directory")
            seededWritablePair "proposal-pages" source dir (fun _ send ->
                for id in [ "unknown"; "" ] do
                    send (read id ",\"structuredReview\":null,\"offset\":null") |> ignore
                    send (operation "viewProposal" id) |> ignore
                for request in
                    [ """{"id":"bad","op":"readProposal"}"""
                      """{"id":"bad","op":"readProposal","proposalId":3,"path":null}"""
                      """{"id":"bad","op":"readProposal","proposalId":3}"""
                      """{"id":"bad","op":"readProposal","proposalId":"unknown","proposalId":"again"}"""
                      """{"id":"bad","op":"viewProposal","proposalId":3,"output":null}"""
                      """{"id":"bad","op":"viewProposal","proposalId":"unknown","output":null}"""
                      """{"id":"bad","op":"viewProposal","proposalId":"unknown","extra":true}""" ] do
                    send request |> ignore
                let stale = send (create "") |> proposal
                exercise send stale "create-compact"
                for detail in [ "\"detail\":\"full\","; "\"detail\":\"full\",\"structuredReview\":true," ] do
                    let pending = send (create detail) |> proposal
                    exercise send pending (if detail.Contains "structuredReview" then "create-structured" else "create-full")
                    send (operation "reject" pending) |> ignore
                    send (read pending "") |> ignore
                    send (operation "viewProposal" pending) |> ignore
                let installed = send (create "") |> proposal
                send (operation "commit" installed) |> ignore
                send (read installed "") |> ignore
                send (operation "viewProposal" installed) |> ignore
                exercise send stale "stale"
                send """{"id":"list","op":"proposals"}""" |> ignore
                send """{"id":"status","op":"status"}""" |> ignore
                for detail, fields in [ "compact", "\"detail\":\"compact\""; "full", "\"detail\":\"full\""; "structured", "\"detail\":\"full\",\"structuredReview\":true" ] do
                    let body = send $"""{{"id":"body","op":"simulateReplaceBodyByName","function":"made",{fields},"replacement":{{"kind":"stringLiteral","value":"a😀b"}}}}""" |> proposal
                    exercise send body ("body-" + detail)
                    // Read the full stored function to test string and graph pagination paths.
                    for structured in [ "false"; "true" ] do
                        for path in [ "/review/proposedFunction"; "/review/proposedFunction/body/nodes"; "/review/proposedFunction/nodes" ] do
                            send (read body $""", "structuredReview":{structured},"path":"{path}" """) |> ignore
                    for offset, limit in [ 0, 2; 1, 1; 1, 2; 2, 2; 4, 20; 0, 1025 ] do
                        send (read body $""", "path":"/review/proposedFunction/body/nodes/0/kind/value","offset":{offset},"limit":{limit}""") |> ignore
                    send (operation "reject" body) |> ignore
                use focus = parseDocument (send """{"id":"focus","op":"readFunction","function":"made","detail":"compact"}""")
                let target = focus.RootElement.GetProperty("result").GetProperty("function").GetProperty("body").GetProperty("resultNodeId").GetString()
                for detail, fields in [ "compact", "\"detail\":\"compact\""; "full", "\"detail\":\"full\""; "structured", "\"detail\":\"full\",\"structuredReview\":true" ] do
                    let replacement = send $"""{{"id":"target","op":"simulateReplaceTargetByName","function":"made","targetNodeId":"{target}",{fields},"replacement":{{"kind":"stringLiteral","value":"changed"}}}}""" |> proposal
                    exercise send replacement ("target-" + detail)
                    for path in [ "/review/diff"; "/review/baseFocus"; "/review/proposedFocus" ] do
                        send (read replacement $""", "path":"{path}" """) |> ignore
                    send (operation "reject" replacement) |> ignore
                send (operation "reject" stale) |> ignore
                send (read stale "") |> ignore
                send (operation "viewProposal" stale) |> ignore) |> ignore)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Proposal page budgets preserve original array indices and full engine review pages`` () =
        withDirectory (fun dir ->
            let input = Path.Combine(dir, "input")
            Directory.CreateDirectory input |> ignore
            let source = Path.Combine(input, "snapshot.json")
            File.WriteAllText(source, SnapshotCodec.serialize (Snapshot.empty (StableId.createForTesting<SnapshotTag> "proposal-budget")) |> unwrap)
            let literal = String.replicate 2048 "x" + "😀"
            // 41 recipe nodes, only 2 levels deep; the long sibling literals force page cuts.
            let items = List.replicate 40 $"""{{"kind":"stringLiteral","value":"{literal}"}}""" |> String.concat ","
            let body = $"""{{"kind":"listConstruct","itemType":{{"kind":"primitive","primitive":"string"}},"items":[{items}]}}"""
            seededWritablePair "proposal-budget" source dir (fun _ send ->
                let pending = send $"""{{"id":"wide","op":"simulateCreateFunctionByName","displayName":"wide","parameters":[],"resultType":{{"kind":"builtinGeneric","generic":"list","arguments":[{{"kind":"primitive","primitive":"string"}}]}},"requiredEffects":[],"body":{body}}}""" |> proposal
                for structured in [ "false"; "true" ] do
                    for path in [ ""; "/review"; "/review/createdFunction"; "/review/createdFunction/body/nodes" ] do
                        for offset in [ 0; 1 ] do
                            let reply = send (read pending $""", "structuredReview":{structured},"path":"{path}","offset":{offset}""")
                            Assert.True(Text.Encoding.UTF8.GetByteCount(reply) < 8400, reply)
                            use doc = parseDocument reply
                            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), reply)
                            if structured = "true" && path = "/review/createdFunction/body/nodes" then
                                Assert.True(doc.RootElement.GetProperty("result").GetProperty("projection").GetProperty("requiresDetailedReview").GetBoolean(), reply)
                for path in [ "/review/createdFunction/body/nodes/01"; "/review/createdFunction/body/nodes/-1"; "/review/createdFunction/body/nodes/~01" ] do
                    send (read pending $""", "path":"{path}" """) |> ignore
                send (operation "viewProposal" pending) |> ignore) |> ignore)

/// Step 4a only: declaration intents, shared publication, persistence and review artifacts.
module ZigHostDeclarationParityTests =
    open ZigHostParityTests

    let private quoted (text: string) = JsonSerializer.Serialize text
    let private mutate (line: string) change =
        let node = JsonNode.Parse(line).AsObject()
        change node
        node.ToJsonString()
    let private request op fields = $"""{{"id":"declaration","op":"{op}",{fields}}}"""
    let private primitive = """{"kind":"primitive","primitive":"int64"}"""
    let private named name = $"""{{"kind":"named","name":{quoted name}}}"""
    let private listOf t = $"""{{"kind":"builtinGeneric","generic":"list","arguments":[{t}]}}"""
    let private field name t = $"""{{"displayName":{quoted name},"type":{t}}}"""
    let private product fields = $"""{{"kind":"product","fields":[{String.concat "," fields}]}}"""
    let private declaration name kind = $""" "displayName":{quoted name},"typeKind":{kind}"""
    let private create op name kind = request op (declaration name kind)
    let private group =
        let a = declaration "NewA" (product [ field "next" (named "NewB") ])
        let b = declaration "NewB" """{"kind":"union","variants":[{"displayName":"End","fields":[]},{"displayName":"More","fields":[{"displayName":"value","type":{"kind":"named","name":"NewA"}}]}]}"""
        request "simulateCreateTypesByName" $""" "types":[{{{a}}},{{{b}}}]"""
    let private capability byName =
        let effect = if byName then "\"effect\":\"io\"" else "\"effectId\":\"zig-io\""
        request (if byName then "simulateCreateCapabilityByName" else "simulateCreateCapability")
            $""" "displayName":"NewCapability","exported":true,"taskBound":true,"operations":[{{"displayName":"read","parameters":[{{"displayName":"key","type":{primitive}}}],"resultType":{{"kind":"builtinGeneric","generic":"result","arguments":[{primitive},{{"kind":"primitive","primitive":"string"}}]}},{effect},"mayWait":true}}]"""
    let private succeeded (reply: string) =
        use doc = parseDocument reply
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), reply)
    let private proposal (reply: string) =
        succeeded reply
        use doc = parseDocument reply
        doc.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
    let private operation op id = request op $""" "proposalId":{quoted id}"""
    let private strict seed source requests =
        let expected, actual = seededPair seed source requests
        Assert.Equal<string list>(expected, actual)
        expected
    let private fullReviews lines =
        lines |> List.collect (fun line ->
            [ line
              mutate line (fun root -> root["detail"] <- JsonValue.Create "full")
              mutate line (fun root -> root["detail"] <- JsonValue.Create "full"; root["structuredReview"] <- JsonValue.Create true) ])

    [<Fact; Trait("Suite", "Extended")>]
    let ``Declaration creation matches exact symbolic generic recursive and group successes`` () =
        withDirectory (fun dir ->
            let source = Path.Combine(dir, "declarations.json")
            let snapshot = ZigHostCreateInferenceTests.snapshot ()
            File.WriteAllText(source, SnapshotCodec.serialize snapshot |> unwrap)
            let generic =
                create "simulateCreateType" "NewGeneric" (product [ field "value" """{"kind":"typeParameter","name":"T"}""" ])
                |> fun line -> mutate line (fun root -> root["typeParameters"] <- JsonNode.Parse("[\"T\"]"))
            let successes =
                [ create "simulateCreateType" "NewProduct" (product [ field "x" primitive ])
                  create "simulateCreateTypeByName" "NewProduct" (product [ field "point" (named "Point") ])
                  generic
                  generic.Replace("simulateCreateType", "simulateCreateTypeByName")
                  create "simulateCreateTypeByName" "NewTree" (product [ field "children" (listOf (named "NewTree")) ])
                  create "simulateCreateTypeByName" "NewUnion" """{"kind":"union","variants":[{"displayName":"Empty","fields":[]},{"displayName":"Full","fields":[{"displayName":"x","type":{"kind":"primitive","primitive":"int64"}}]}]}"""
                  (create "simulateCreateTypeByName" "GenericTree" """{"kind":"product","fields":[{"displayName":"value","type":{"kind":"typeParameter","name":"T"}},{"displayName":"children","type":{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"named","name":"GenericTree","arguments":[{"kind":"typeParameter","name":"T"}]}]}}]}"""
                  |> fun line -> mutate line (fun root -> root["typeParameters"] <- JsonNode.Parse("[\"T\"]")))
                  group
                  request "simulateCreateTypesByName" """ "types":[{"displayName":"GA","exported":true,"typeParameters":["T"],"typeKind":{"kind":"product","fields":[{"displayName":"bs","type":{"kind":"builtinGeneric","generic":"list","arguments":[{"kind":"named","name":"GB","arguments":[{"kind":"typeParameter","name":"T"}]}]}}]}},{"displayName":"GB","typeParameters":["U"],"typeKind":{"kind":"product","fields":[{"displayName":"as","type":{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"named","name":"GA","arguments":[{"kind":"typeParameter","name":"U"}]}]}}]}}]"""
                  request "simulateCreateTypesByName" $""" "types":[{{{declaration "Solo" (product [])}}}]"""
                  request "simulateCreateEffect" """ "displayName":"NewEffect" """
                  request "simulateCreateEffect" """ "displayName":"NewEffect","exported":true"""
                  capability false
                  capability true
                  mutate (capability false) (fun root ->
                      root.Remove "exported" |> ignore
                      root.Remove "taskBound" |> ignore
                      (root["operations"].AsArray()).[0]["failureType"] <- null)
                  (create "simulateCreateType" "GenericUnion" """{"kind":"union","variants":[{"displayName":"None","fields":[]},{"displayName":"Some","fields":[{"displayName":"value","type":{"kind":"typeParameter","name":"T"}}]}]}"""
                   |> fun line -> mutate line (fun root -> root["typeParameters"] <- JsonNode.Parse("[\"T\"]"))) ]
                |> fullReviews
            let replies = strict "declaration-success" source (successes @ [ """{"id":"p","op":"proposals"}"""; """{"id":"d","op":"declarations"}""" ])
            for reply in replies do succeeded reply
            for reply in replies |> List.take successes.Length do
                use doc = parseDocument reply
                let kind = doc.RootElement.GetProperty("result").GetProperty("review").GetProperty("kind").GetString()
                Assert.Contains(kind, [ "createType"; "createTypes"; "createEffect"; "createCapability" ])
            // No declaration from an uncommitted proposal may leak into the base.
            Assert.DoesNotContain("NewProduct", List.last replies)
            Assert.DoesNotContain("NewCapability", List.last replies))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Declaration rejection decoding validation and allocation order match`` () =
        withDirectory (fun dir ->
            let source = Path.Combine(dir, "declarations.json")
            File.WriteAllText(source, SnapshotCodec.serialize (ZigHostCreateInferenceTests.snapshot ()) |> unwrap)
            let good = create "simulateCreateTypeByName" "NewType" (product [ field "x" primitive ])
            let bad =
                [ create "simulateCreateType" "" (product [])
                  create "simulateCreateType" "Point" (product [])
                  create "simulateCreateTypeByName" "Point" (product [])
                  create "simulateCreateType" "Unknown" (product [ field "x" """{"kind":"named","typeId":"absent"}""" ])
                  create "simulateCreateType" "Unknown" (product [ field "x" """{"kind":"capability","capabilityId":"absent"}""" ])
                  create "simulateCreateTypeByName" "Unknown" (product [ field "x" (named "absent") ])
                  create "simulateCreateType" "Parameter" (product [ field "x" """{"kind":"typeParameter","name":"T"}""" ])
                  create "simulateCreateTypeByName" "Cycle" (product [ field "x" (named "Cycle") ])
                  create "simulateCreateTypeByName" "Blank" (product [ field " " primitive ])
                  create "simulateCreateTypeByName" "Duplicate" (product [ field "x" primitive; field "x" primitive ])
                  create "simulateCreateTypeByName" "BadArity" (product [ field "x" (named "Box") ])
                  create "simulateCreateType" "BadBuiltin" (product [ field "x" """{"kind":"builtinGeneric","generic":"map","arguments":[]}""" ])
                  request "simulateCreateTypesByName" """ "types":[]"""
                  request "simulateCreateTypesByName" $""" "types":[{{{declaration "Same" (product [])}}},{{{declaration "Same" (product [])}}}]"""
                  mutate good (fun root -> root["expectedSnapshotId"] <- JsonValue.Create "stale")
                  mutate good (fun root -> root["typeParameters"] <- JsonNode.Parse("[\"T\",\"T\"]"))
                  request "simulateCreateEffect" """ "displayName":" " """
                  request "simulateCreateEffect" """ "displayName":"io" """
                  request "simulateCreateCapability" """ "displayName":"IO","operations":[]"""
                  request "simulateCreateCapabilityByName" """ "displayName":"IO","operations":null"""
                  (capability false).Replace("zig-io", "absent")
                  (capability true).Replace("\"io\"", "\"absent\"")
                  (capability false).Replace("\"key\"", "\" \"")
                  (capability false).Replace("\"read\"", "\" \"")
                  (capability true).Replace("\"mayWait\":true", "\"mayWait\":null")
                  (capability false).Replace("\"mayWait\":true", "\"failureType\":{},\"mayWait\":true")
                  mutate (capability false) (fun root ->
                      let operations = root["operations"].AsArray()
                      let firstOperation = operations.[0]
                      operations.Add(firstOperation.DeepClone()))
                  mutate (capability false) (fun root ->
                      let firstOperation = (root["operations"].AsArray()).[0]
                      let parameters = (firstOperation["parameters"]).AsArray()
                      let firstParameter = parameters.[0]
                      parameters.Add(firstParameter.DeepClone())) ]
            let malformed =
                [ for line in [ good; group; capability true; capability false; request "simulateCreateEffect" """ "displayName":"NewEffect" """ ] do
                      for key in [ "displayName"; "exported"; "expectedSnapshotId"; "extra" ] do
                          yield mutate line (fun root -> root[key] <- JsonValue.Create 7)
                      yield mutate line (fun root -> root.Remove "op" |> ignore)
                      yield line.Replace("\"id\":\"declaration\"", "\"id\":\"declaration\",\"id\":\"twice\"")
                  yield good.Replace("\"fields\":[", "\"fields\":[3,")
                  yield good.Replace("\"type\":", "\"unknown\":true,\"type\":") ]
            // A successful request after every failure pins the ID stream, not merely error text.
            let requests = (bad @ malformed) |> List.collect (fun line -> [ line; good ])
            let replies = strict "declaration-rejection" source requests
            for pair in replies |> List.chunkBySize 2 do
                use rejected = parseDocument pair[0]
                Assert.False(rejected.RootElement.GetProperty("ok").GetBoolean(), pair[0])
                succeeded pair[1])

    [<Fact; Trait("Suite", "Extended")>]
    let ``Declaration proposals page render commit persist and reopen through either host`` () =
        withDirectory (fun dir ->
            for records, resolved in [ false, false; true, false; false, true; true, true ] do
                let case = Path.Combine(dir, string records + "-" + string resolved)
                let input = Path.Combine(case, "input")
                Directory.CreateDirectory input |> ignore
                let source = Path.Combine(input, if records then "snapshot.nyrec" else "snapshot.json")
                let empty = Snapshot.empty (StableId.createForTesting<SnapshotTag> "018bcfe5-6700-7000-8000-000000000001")
                let dependency = ZigHostCreateInferenceTests.snapshot () |> recordFixture "declaration-dependency"
                let json = if resolved then SnapshotCodec.serializeResolved dependency empty |> unwrap else SnapshotCodec.serialize empty |> unwrap
                File.WriteAllText(source, if records then RecordCodec.ofJson json else json)
                if resolved then
                    let manifest = memberOf dependency (Some("snapshotJson", SnapshotCodec.serialize dependency |> unwrap)) "library.json"
                    File.WriteAllText(source + ".dependency.json", manifest.ToJsonString())
                let fsharp, zig =
                    seededWritablePair "declaration-persistence" source case (fun _ send ->
                        let stale = send group |> proposal
                        let effect = send (request "simulateCreateEffect" """ "displayName":"NewEffect","exported":true """) |> proposal
                        let inspect id =
                            for structured in [ "false"; "true" ] do
                                send (request "readProposal" $""" "proposalId":{quoted id},"structuredReview":{structured},"path":"/review","offset":0,"limit":2""") |> succeeded
                            send (operation "viewProposal" id) |> succeeded
                            send """{"id":"p","op":"proposals"}""" |> succeeded
                        inspect stale
                        inspect effect
                        send (operation "commit" effect) |> succeeded
                        inspect stale
                        Assert.Contains("StaleBaseSnapshot", send (operation "commit" stale))
                        send (operation "reject" stale) |> succeeded
                        Assert.Contains("ProposalNotFound", send (operation "readProposal" stale))
                        let types = send group |> proposal
                        inspect types
                        send (operation "commit" types) |> succeeded
                        let wide = create "simulateCreateType" "Wide" (product [ for i in 0 .. 119 -> field (string i + String.replicate 200 "x") primitive ])
                        for line in fullReviews [ wide ] do
                            let pending = send line |> proposal
                            inspect pending
                            send (request "readProposal" $""" "proposalId":{quoted pending},"path":"/review/createdType/typeKind/fields","offset":8,"limit":3""") |> succeeded
                            send (operation "reject" pending) |> succeeded
                        let cap = (capability true).Replace("\"io\"", "\"NewEffect\"")
                        let pending = send cap |> proposal
                        inspect pending
                        send (operation "commit" pending) |> succeeded
                        // Existing function publication must still work after each declaration family.
                        let functionId = send """{"id":"f","op":"simulateCreateFunctionByName","displayName":"made","parameters":[],"requiredEffects":[],"resultType":{"kind":"primitive","primitive":"unit"},"body":{"kind":"unitLiteral"}}""" |> proposal
                        send (operation "commit" functionId) |> succeeded
                        send """{"id":"d","op":"declarations"}""" |> succeeded)
                for directory in [ fsharp; zig ] do
                    let path = Path.Combine(directory, Path.GetFileName source)
                    let reads = [ """{"id":"s","op":"status"}"""; """{"id":"d","op":"declarations"}"""; """{"id":"f","op":"readFunction","function":"made","detail":"full"}""" ]
                    let readHost zig =
                        let code, errors, replies = run zig [ "open"; "--snapshot"; path; "--read-only" ] reads
                        Assert.True((code = 0), errors)
                        replies |> List.iter succeeded
                        List.map comparable replies
                    Assert.Equal<string list>(readHost false, readHost true))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Unseeded declaration reviews sort members without reordering parameters`` () =
        let names (items: JsonElement) =
            items.EnumerateArray() |> Seq.map (fun item -> item.GetProperty("displayName").GetString()) |> Seq.toList
        let sorted count (items: JsonElement) =
            let ids = items.EnumerateArray() |> Seq.map (fun item -> item.GetProperty("id").GetString()) |> Seq.toList
            Assert.Equal(count, ids.Length)
            Assert.Equal<string list>(List.sort ids, ids)
        let check key (item: JsonElement) =
            if key = "createdCapability" then
                let operations = item.GetProperty("operations")
                sorted 8 operations
                for operation in operations.EnumerateArray() do
                    Assert.Equal<string list>([ "z"; "a" ], names (operation.GetProperty("parameters")))
            else
                Assert.Equal<string list>([ "Z"; "A" ], names (item.GetProperty("typeParameters")))
                let kind = item.GetProperty("typeKind")
                if kind.GetProperty("kind").GetString() = "product" then sorted 12 (kind.GetProperty("fields"))
                else
                    let variants = kind.GetProperty("variants")
                    sorted 4 variants
                    for variant in variants.EnumerateArray() do sorted 4 (variant.GetProperty("fields"))
        let fields count = [ for i in 1 .. count -> field (string i) primitive ]
        let generic line = mutate line (fun root -> root["typeParameters"] <- JsonNode.Parse("[\"Z\",\"A\"]"))
        let variants =
            [ for i in 1 .. 4 ->
                  $"""{{"displayName":"V{i}","fields":[{String.concat "," (fields 4)}]}}""" ] |> String.concat ","
        let operations =
            [ for i in 1 .. 8 ->
                  $"""{{"displayName":"op{i}","parameters":[{field "z" primitive},{field "a" primitive}],"resultType":{primitive},"effect":"io"}}""" ] |> String.concat ","
        let cases =
            [ "createdType", "declaredTypes", generic (create "simulateCreateType" "SortedProduct" (product (fields 12)))
              "createdType", "declaredTypes", generic (create "simulateCreateType" "SortedUnion" $"""{{"kind":"union","variants":[{variants}]}}""")
              "createdCapability", "declaredCapabilities", request "simulateCreateCapabilityByName" $""" "displayName":"SortedCapability","operations":[{operations}]""" ]
        withDirectory (fun dir ->
            // Each host checks its own random IDs, never compares them with the other host.
            for zig in [ false; true ] do
                let directory = Path.Combine(dir, string zig)
                Directory.CreateDirectory directory |> ignore
                let source = Path.Combine(directory, "snapshot.json")
                File.WriteAllText(source, SnapshotCodec.serialize (ZigHostCreateInferenceTests.snapshot ()) |> unwrap)
                use host = startWithEnvironment [ "NYRWVY_TEST_ID_SEED", "" ] zig [ "open"; "--snapshot"; source ]
                let errors = host.StandardError.ReadToEndAsync()
                let send (line: string) =
                    host.StandardInput.WriteLine line
                    host.StandardInput.Flush()
                    let reading = host.StandardOutput.ReadLineAsync()
                    if not (reading.Wait silence) then
                        host.Kill true
                        failwithf "Host did not answer %s" line
                    let reply = reading.Result
                    Assert.False(isNull reply, "Host closed stdout")
                    succeeded reply
                    reply
                try
                    for key, viewerKey, line in cases do
                        for simulation in fullReviews [ line ] do
                            let reply = send simulation
                            let pending = proposal reply
                            use doc = parseDocument reply
                            check key (doc.RootElement.GetProperty("result").GetProperty("review").GetProperty(key))
                            for structured in [ "false"; "true" ] do
                                let page = send (request "readProposal" $""" "proposalId":{quoted pending},"structuredReview":{structured},"path":"/review/{key}","limit":20""")
                                use pageDoc = parseDocument page
                                let result = pageDoc.RootElement.GetProperty("result")
                                Assert.True(result.GetProperty("projection").GetProperty("complete").GetBoolean(), page)
                                check key (result.GetProperty("value"))
                            send (request "viewProposal" $""" "proposalId":{quoted pending},"output":"review.html" """) |> ignore
                            let html = File.ReadAllText(Path.Combine(directory, "review.html"))
                            let data = Regex.Match(html, """(?s)<script type="application/json" id="nyrwvy-data">(.*?)</script>""")
                            Assert.True(data.Success, "Missing viewer data")
                            let viewJson = data.Groups.[1].Value
                            use view = parseDocument viewJson
                            let declared = view.RootElement.GetProperty(viewerKey).EnumerateArray() |> Seq.exactlyOne
                            check key declared
                            check key (view.RootElement.GetProperty("engineReview").GetProperty("review").GetProperty(key))
                            send (operation "reject" pending) |> ignore
                finally
                    host.StandardInput.Close()
                    if not (host.WaitForExit(int silence.TotalMilliseconds)) then
                        host.Kill true
                        host.WaitForExit()
                Assert.Equal(0, host.ExitCode)
                Assert.Equal("", errors.Result.Trim()))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Declaration selectors honor ambiguity dependency guards and hidden imports`` () =
        withDirectory (fun dir ->
            let original = ZigHostCreateInferenceTests.snapshot ()
            let duplicate: SemanticType =
                { original.Types.Head with Id = StableId.createForTesting<TypeTag> "another-point"; Kind = SemanticTypeKind.Product [] }
            let ambiguous = { original with Types = original.Types @ [ duplicate ] }
            let source = Path.Combine(dir, "ambiguous.json")
            File.WriteAllText(source, SnapshotCodec.serialize ambiguous |> unwrap)
            let ambiguousRequest = create "simulateCreateTypeByName" "NewPoint" (product [ field "x" (named "Point") ])
            let explicitRequest = ambiguousRequest.Replace("\"name\":\"Point\"", "\"name\":{\"id\":\"zig-Point\",\"ownership\":\"local\"}")
            let replies = strict "declaration-ambiguous" source [ ambiguousRequest; explicitRequest ]
            Assert.Contains("Ambiguous type", replies.Head)
            succeeded replies[1]
            let originalDependency = { original with Types = original.Types |> List.map (fun t -> if t.DisplayName = "Point" then { t with Exported = false } else t) }
            let local = Snapshot.empty (StableId.createForTesting<SnapshotTag> "018bcfe5-6700-7000-8000-000000000011")
            for records in [ false; true ] do
                let dependency = if records then recordFixture "declaration-guard-dependency" originalDependency else originalDependency
                let case = Path.Combine(dir, "dependency-" + string records)
                Directory.CreateDirectory case |> ignore
                let path = Path.Combine(case, if records then "snapshot.nyrec" else "snapshot.json")
                let json = SnapshotCodec.serializeResolved dependency local |> unwrap
                let serialized = if records then RecordCodec.ofJson json else json
                if records then
                    Assert.Equal(serialized, RecordCodec.deserializeResolved dependency serialized |> unwrap |> RecordCodec.serializeResolved dependency |> unwrap)
                File.WriteAllText(path, serialized)
                let manifest = memberOf dependency (Some("snapshotJson", SnapshotCodec.serialize dependency |> unwrap)) "library.json"
                File.WriteAllText(path + ".dependency.json", manifest.ToJsonString())
                let pin = quoted (StableId.value dependency.Id)
                let typeId name = dependency.Types |> List.find (fun t -> t.DisplayName = name) |> fun t -> quoted (StableId.value t.Id)
                let selector = $"""{{"id":{typeId "Shape"},"name":"Shape","ownership":"imported","dependencySnapshotId":{pin}}}"""
                let good = create "simulateCreateTypeByName" "NewShape" (product [ field "shape" $"""{{"kind":"named","name":{selector}}}""" ])
                let hidden = create "simulateCreateType" "NewPoint" (product [ field "point" $"""{{"kind":"named","typeId":{typeId "Point"}}}""" ])
                let lines =
                    [ good
                      good.Replace("\"imported\"", "\"local\"")
                      good.Replace(pin, "\"wrong-pin\"")
                      good.Replace(typeId "Shape", typeId "Point").Replace("\"name\":\"Shape\"", "\"name\":\"Point\"")
                      hidden
                      ambiguousRequest
                      capability true
                      group ]
                let replies = strict "declaration-dependency" path lines
                for index in [ 0; 6; 7 ] do succeeded replies[index]
                for index in [ 1; 2; 3; 4; 5 ] do
                    use reply = parseDocument replies[index]
                    Assert.False(reply.RootElement.GetProperty("ok").GetBoolean(), replies[index])
                Assert.Contains("ImportedDeclarationNotExported", replies[4]))

module ZigHostFunctionBatchParityTests =
    open ZigHostParityTests

    let private integer = """{"kind":"primitive","primitive":"int64"}"""
    let private literal = """{"kind":"int64Literal","value":"1"}"""
    let private fn name body =
        $"""{{"displayName":"{name}","exported":true,"mustNotWait":true,"parameters":[],"resultType":{integer},"requiredEffects":[],"body":{body}}}"""
    let private batch functions = $"""{{"id":"batch","op":"simulateCreateFunctionsByName","functions":[{String.concat "," functions}]}}"""
    let private success (reply: string) =
        use doc = parseDocument reply
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), reply)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Function batches preserve ordered creation aggregation and post failure allocator parity`` () =
        withDirectory (fun dir ->
            let source = Path.Combine(dir, "functions.json")
            File.WriteAllText(source, SnapshotCodec.serialize (ZigHostCreateInferenceTests.snapshot ()) |> unwrap)
            let ordered = batch [ fn "first" literal; fn "second" """{"kind":"directCall","function":"first","arguments":[]}""" ]
            let invalids =
                [ batch []
                  """{"id":"batch","op":"simulateCreateFunctionsByName","functions":{}}"""
                  batch [ fn "later" """{"kind":"directCall","function":"notYet","arguments":[]}"""; fn "notYet" literal ]
                  batch [ fn "dup" literal; fn "dup" literal ]
                  batch [ fn "bad" """{"kind":"boolLiteral","value":true}""" ]
                  batch [ (fn "malformed" literal).Replace("\"parameters\":[]", "\"parameters\":{}") ]
                  batch [ (fn "marker" literal).Replace("\"mustNotWait\":true", "\"mustNotWait\":1") ]
                  batch [ fn "unresolved" """{"kind":"directCall","function":"unknown","arguments":[]}"""; fn "createdBeforeRejection" literal ]
                  batch [ fn "unresolved" """{"kind":"directCall","function":"unknown","arguments":[]}"""; fn "invalidPreflight" """{"kind":"boolLiteral","value":true}""" ]
                  batch [ "{}"; "null"; fn "bad" """{"kind":"directCall","function":"unknown","arguments":[]}""" ]
                  batch [ (fn "duplicate" literal).Replace("\"displayName\":\"duplicate\"", "\"displayName\":\"duplicate\",\"displayName\":\"duplicate\"") ] ]
            let requests =
                [ for line in invalids do
                      yield line
                      yield ordered
                  yield ordered
                  yield batch [ fn "recursive" """{"kind":"directCall","function":"recursive","arguments":[]}""" ]
                  yield batch [ """{"displayName":"newGeneric","typeParameters":["Z","A"],"parameters":[{"displayName":"z","type":{"kind":"typeParameter","name":"Z"}},{"displayName":"a","type":{"kind":"typeParameter","name":"A"}}],"resultType":{"kind":"typeParameter","name":"A"},"requiredEffects":[],"body":{"kind":"parameter","name":"a"}}"""; fn "following" literal ]
                  let node = JsonNode.Parse(ordered).AsObject()
                  node["detail"] <- JsonValue.Create "full"
                  yield node.ToJsonString()
                  node["structuredReview"] <- JsonValue.Create true
                  yield node.ToJsonString()
                  yield """{"id":"list","op":"proposals"}""" ]
            let expected, actual = seededPair "function-batch" source requests
            Assert.Equal<string list>(expected, actual)
            for i in 0 .. invalids.Length - 1 do
                use rejected = parseDocument expected[i * 2]
                Assert.False(rejected.RootElement.GetProperty("ok").GetBoolean(), expected[i * 2])
                success expected[i * 2 + 1]
            for reply in expected |> List.skip (invalids.Length * 2) do success reply)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Function batch files lifecycle reviews and persistence agree in both formats`` () =
        withDirectory (fun dir ->
            for records in [ false; true ] do
                let case = Path.Combine(dir, string records)
                let input = Path.Combine(case, "input")
                Directory.CreateDirectory input |> ignore
                let source = Path.Combine(input, if records then "snapshot.nyrec" else "snapshot.json")
                let snapshot = Snapshot.empty (StableId.createForTesting<SnapshotTag> "018bcfe5-6700-7000-8000-000000000001")
                File.WriteAllText(source, if records then RecordCodec.serialize snapshot |> unwrap else SnapshotCodec.serialize snapshot |> unwrap)
                let first = fn "first" literal
                let second = (fn "second" literal).Replace("\"body\":" + literal, "\"bodyFile\":\"body.json\"")
                File.WriteAllText(Path.Combine(input, "body.json"), literal)
                File.WriteAllText(Path.Combine(input, "functions.json"), $"[{first},{second}]")
                File.WriteAllText(Path.Combine(input, "bad.json"), "{}")
                let operation op id = $"""{{"id":"lifecycle","op":"{op}","proposalId":"{id}"}}"""
                let proposal reply =
                    success reply
                    use doc = parseDocument reply
                    doc.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                let fsharp, zig =
                    seededWritablePair "function-batch-files" source case (fun _ send ->
                        for line in
                            [ """{"id":"batch","op":"simulateCreateFunctionsByName","functionsFile":"../outside.json"}"""
                              """{"id":"batch","op":"simulateCreateFunctionsByName","functionsFile":"bad.json"}"""
                              """{"id":"batch","op":"simulateCreateFunctionsByName","functions":[],"functionsFile":"functions.json"}""" ] do
                            use reply = parseDocument (send line)
                            Assert.False(reply.RootElement.GetProperty("ok").GetBoolean())
                        let request = """{"id":"batch","op":"simulateCreateFunctionsByName","functionsFile":"functions.json","detail":"full"}"""
                        let stale = send request |> proposal
                        let current = send request |> proposal
                        let inspect id =
                            send (operation "viewProposal" id) |> success
                            for structured in [ "false"; "true" ] do
                                send $"""{{"id":"page","op":"readProposal","proposalId":"{id}","structuredReview":{structured},"path":"/review/batch","limit":1}}""" |> success
                                Assert.Contains("Cannot traverse a scalar", send $"""{{"id":"page","op":"readProposal","proposalId":"{id}","structuredReview":{structured},"path":"/review/batch/0"}}""")
                        inspect stale
                        inspect current
                        send (operation "commit" current) |> success
                        inspect stale
                        Assert.Contains("StaleBaseSnapshot", send (operation "commit" stale))
                        send (operation "reject" stale) |> success
                        Assert.Contains("ProposalNotFound", send (operation "readProposal" stale))
                        send """{"id":"list","op":"proposals"}""" |> success
                        send """{"id":"d","op":"declarations"}""" |> success)
                for directory in [ fsharp; zig ] do
                    let path = Path.Combine(directory, Path.GetFileName source)
                    let requests = [ """{"id":"s","op":"status"}"""; """{"id":"f","op":"readFunction","function":"second","detail":"full"}""" ]
                    let readHost zig =
                        let code, errors, replies = run zig [ "open"; "--snapshot"; path; "--read-only" ] requests
                        Assert.True((code = 0), errors)
                        replies |> List.iter success
                        List.map comparable replies
                    Assert.Equal<string list>(readHost false, readHost true))
