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

    /// A `SessionError` reply carries .NET's exception text, so it is compared by its code (and ID) only.
    let internal sessionErrorCode (reply: string) =
        Text.RegularExpressions.Regex.Replace(reply, "\"code\":\"SessionError\",\"message\":\"(?:[^\"\\\\]|\\\\.)*\"", "\"code\":\"SessionError\"")

    /// The diagnostic codes of the `simulateCreateFunction(ByName)` preflight the Zig host answers (step 3c-3a).
    let internal preflightCodes =
        set [ "FunctionCreationTypeParameterNameBlank"; "FunctionCreationDuplicateTypeParameter"; "FunctionCreationTypeParameterNotDeclared"; "FunctionCreationTypeParameterNotInferable"
              "StructuralExpectedBaseMismatch"; "FunctionCreationDisplayNameBlank"; "FunctionCreationDuplicateDisplayName"; "FunctionCreationParameterDisplayNameBlank"
              "PureExpressionInvalidTypeRef"; "NamedTypeArityMismatch"; "FunctionCreationDuplicateRequiredEffect"; "FunctionCreationRequiredEffectNotFound"
              "StructuralRecipeDepthLimitExceeded"; "StructuralRecipeNodeLimitExceeded" ]

    /// The diagnostic codes of the ported inference (step 3c-3b), located at their node.
    let internal inferenceCodes =
        set [ "PureExpressionOperandTypeMismatch"; "ListIndexOfItemTypeNotEquatable"; "ListLastIndexOfItemTypeNotEquatable"; "ListContainsItemTypeNotEquatable"
              "SetItemTypeNotEquatable"; "MapKeyTypeNotEquatable"; "ValueEqualTypeNotEquatable"; "OriginalTargetOutsideTargetedReplacement"
              "PureExpressionParameterOutOfScope"; "PureExpressionParameterSlotOutOfRange"; "PureExpressionPatternScopeDepthInvalid"; "PureExpressionPatternScopeNotFound"
              "PureExpressionPatternSlotNotFound"; "PureExpressionDuplicateFieldBinding"; "PureExpressionFieldBindingMismatch"; "PureExpressionFieldBindingTypeMismatch"
              "PureExpressionTypeArgumentArityMismatch"; "PureExpressionTypeArgumentsRequired"; "PureExpressionProductTypeNotFound"; "PureExpressionProductTargetMismatch"
              "PureExpressionProductFieldNotFound"; "PureExpressionFieldAccessTypeMismatch"; "PureExpressionEmptyProductUpdate"; "PureExpressionProductUpdateSourceMismatch"
              "PureExpressionUnionVariantNotFound"; "AbortValueUse"; "PureExpressionBranchTypeMismatch"; "AbortPayloadTypeMismatch"; "AbortOwnerResultTypeMismatch"
              "PureExpressionMatchArmDuplicate"; "PureExpressionMatchArmMismatch"; "PureExpressionMatchOtherwiseDuplicate"; "PureExpressionMatchOtherwiseUnused"
              "PureExpressionMatchInputTypeMismatch"; "FunctionCreationResultTypeMismatch" ]

    /// The recipe kinds the Zig host infers (tools/zig/host/infer.zig), plus the type kinds a request spells.
    let internal portedKinds =
        set [ yield! OperationSignatures.table |> List.map (fun entry -> entry.Kind)
              yield! [ "parameter"; "parameterSlot"; "patternReference"; "originalTarget"; "stringLiteral"; "boolLiteral"; "unitLiteral"; "int64Literal"; "float64Literal"
                       "boolAnd"; "boolOr"; "valueEqual"; "listEmpty"; "setEmpty"; "mapEmpty"; "listConstruct"; "productConstruct"; "fieldAccess"; "productUpdate"; "unionConstruct"
                       "optionNone"; "resultOk"; "resultErr"; "abort"; "conditional"; "matchUnion"; "bind"; "matchOption"; "matchResult"
                       "primitive"; "named"; "capability"; "typeParameter"; "builtinGeneric"; "function" ] ]

    /// Whether the request spells a recipe kind the Zig host does not infer yet.
    let internal hasUnportedKind (request: string) =
        Regex.Matches(request, "\"kind\":\"([A-Za-z0-9]+)\"") |> Seq.exists (fun m -> not (portedKinds.Contains m.Groups[1].Value))

    /// The Zig host's fixed `UnsupportedOperation` replies of step 3c-3b, told apart by message; the
    /// first names the kind it stopped at and its path.
    let internal unportedKindReply (zig: string) = zig.Contains "\"code\":\"UnsupportedOperation\",\"message\":\"The Zig host does not infer "
    let internal unportedKindPath (zig: string) = Regex.Match(zig, "does not infer [A-Za-z0-9]+ recipes yet \\(at (body[^ ]*)\\)").Groups[1].Value
    let internal typeParameterIdentityReply (zig: string) = zig.Contains "\"message\":\"The Zig host does not spell allocated type-parameter identities in diagnostics yet.\""

    /// `locate`'s suffix on an inference diagnostic: the failing node's path, or None for a preflight one.
    let internal locatedPath (message: string) =
        let found = Regex.Match(message, " At (body[^ ]*) \\([a-z][A-Za-z0-9]*\\)\\.(?:\"|$)")
        if found.Success then Some found.Groups[1].Value else None

    /// Whether `path` is `ancestor` or below it (`body.arms[1].body` is below `body.arms[1]`).
    let internal atOrBelow (ancestor: string) (path: string) =
        path = ancestor || path.StartsWith(ancestor + ".") || path.StartsWith(ancestor + "[")

    /// A UUID in `text` that `request` does not spell: an identity the F# host allocated.
    let internal spellsAllocatedId (request: string) (text: string) =
        Regex.Matches(text, "[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}") |> Seq.exists (fun m -> not (request.Contains m.Value))

    /// Whether the Zig reply to a create request matches the F# one: a `ProtocolError`, a preflight or
    /// an inference `SimulationRejected` byte for byte, a `SessionError` by code, and
    /// `UnsupportedOperation` for every other reply (the simulation itself is not ported yet). An
    /// inference rejection may instead be the Zig host's fixed `UnsupportedOperation` when the request
    /// reaches a kind it does not infer, or declares a type parameter whose fresh F# identity the
    /// diagnostic would spell.
    let internal createReplyMatches (request: string) (fsharp: string) (zig: string) =
        use reply = JsonDocument.Parse fsharp
        match reply.RootElement.TryGetProperty "error" with
        | true, error ->
            match error.GetProperty("code").GetString() with
            | "SessionError" -> sessionErrorCode fsharp = sessionErrorCode zig
            | "ProtocolError" -> fsharp = zig
            | "SimulationRejected" ->
                let diagnostic = error.GetProperty("details").GetProperty("diagnostics").[0]
                let code = diagnostic.GetProperty("code").GetString()
                let message = diagnostic.GetProperty("message").GetString()
                let located = locatedPath message
                if preflightCodes.Contains code && located.IsNone then fsharp = zig
                elif preflightCodes.Contains code || inferenceCodes.Contains code then
                    // The Zig host stops at a kind it does not infer where F# enters it, so F#'s diagnostic
                    // came from that subtree, from an ancestor it returned to, or from after inference (the
                    // result-type check); one from a sibling F# reached later still counts as a mismatch.
                    fsharp = zig
                    || (unportedKindReply zig && hasUnportedKind request && (located |> Option.forall (fun path -> atOrBelow (unportedKindPath zig) path || atOrBelow path (unportedKindPath zig))))
                    || (typeParameterIdentityReply zig && request.Contains "\"typeParameter\"" && spellsAllocatedId request message)
                else zig.Contains "\"code\":\"UnsupportedOperation\""
            | _ -> zig.Contains "\"code\":\"UnsupportedOperation\""
        | _ -> zig.Contains "\"code\":\"UnsupportedOperation\""

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

/// Its own class, so xunit runs it beside the other host parity tests.
module ZigHostCreateDecodeTests =
    open ZigHostParityTests

    /// A valid instance of each exact-ID recipe kind, built from tools/zig/host/recipe_kinds.txt
    /// (every allowed property present), and the requests that break it one way at a time.
    let private recipeRequests () =
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
    let private preflightRequests (declarations: AuthoringDeclarations) (snapshotId: string) =
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
            use session = AuthoringSession.openReadOnly path |> unwrap
            let requests = recipeRequests () @ requestMutations () @ preflightRequests (session.Declarations()) (StableId.value session.SnapshotId)
            let code, errors, answers = run true [ "open"; "--snapshot"; path; "--read-only" ] requests
            Assert.True((code = 0), errors)
            Assert.Equal(requests.Length, answers.Length)
            let mutable rejected = 0
            let mutable preflight = 0
            let mismatches = ResizeArray<string>()
            let answered = Text.StringBuilder()
            for request, zig in List.zip requests answers do
                let fsharp = AuthoringProtocol.executeLine session request
                answered.Append(fsharp) |> ignore
                if fsharp.Contains "\"code\":\"ProtocolError\"" then rejected <- rejected + 1
                if isPreflightRejection fsharp then preflight <- preflight + 1
                if not (createReplyMatches request fsharp zig) then mismatches.Add $"{request}\n F#:  {fsharp}\n Zig: {zig}"
            let shownText = answered.ToString()
            for shown in Set.toList (Set.remove "FunctionCreationTypeParameterNameBlank" preflightCodes) @ [ "Largest subtrees within the limit" ] do
                Assert.True(shownText.Contains shown, $"No F# reply shows {shown}; the decode parity test does not cover it.")
            Assert.True(mismatches.Count = 0, $"{mismatches.Count} mismatches:\n" + String.Join("\n", mismatches |> Seq.truncate 12))
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
            use session = AuthoringSession.openReadOnly path |> unwrap
            let requests = spoiled (queries session)
            let code, errors, answers = run true [ "open"; "--snapshot"; path; "--read-only" ] requests
            Assert.True((code = 0), errors)
            let fsharp = requests |> List.map (AuthoringProtocol.executeLine session >> sessionErrorCode)
            sameAnswers "lone surrogates" fsharp (answers |> List.map sessionErrorCode) requests
            Assert.Contains(fsharp, fun reply -> reply.Contains "\"code\":\"SessionError\""))

/// Its own class, so xunit runs it beside the other host parity tests.
module ZigHostCreateByNameTests =
    open ZigHostParityTests

    let private unit () = JsonNode.Parse """{"kind":"unitLiteral"}"""
    let private int64Type () = JsonNode.Parse """{"kind":"primitive","primitive":"int64"}"""

    /// Requests for a session's declarations: a valid instance of each by-name recipe kind (from
    /// tools/zig/host/recipe_kinds_byname.txt), broken one way at a time, and the signature around it.
    let private requests (declarations: AuthoringDeclarations) (snapshotId: string) =
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
                use session = AuthoringSession.openReadOnly snapshot |> unwrap
                let lines = requests (session.Declarations()) (StableId.value session.SnapshotId)
                let code, errors, answers = run true [ "open"; "--snapshot"; snapshot; "--read-only" ] lines
                Assert.True((code = 0), errors)
                Assert.Equal(lines.Length, answers.Length)
                for request, zig in List.zip lines answers do
                    let fsharp = AuthoringProtocol.executeLine session request
                    answered.Append(fsharp) |> ignore
                    if request.Contains "\"bodyFile\":\"nope.json\"" then
                        // The Zig host does not read bodyFile yet (a documented shortcut).
                        if not (zig.Contains "\"code\":\"UnsupportedOperation\"") then mismatches.Add $"{request}\n Zig: {zig}"
                    else
                        if fsharp.Contains "\"code\":\"ProtocolError\"" then rejected <- rejected + 1
                        if isPreflightRejection fsharp then preflight <- preflight + 1
                        if not (createReplyMatches request fsharp zig) then mismatches.Add $"{Path.GetFileName snapshot}: {request}\n F#:  {fsharp}\n Zig: {zig}"
            let shownText = answered.ToString().Replace("\\u0027", "'")
            // Of the preflight, a blank type-parameter name, a taken display name and an unknown effect, type or capability ID are decode rejections by name.
            let preflightShown = Set.toList (Set.difference preflightCodes (set [ "FunctionCreationTypeParameterNameBlank"; "FunctionCreationRequiredEffectNotFound"; "FunctionCreationDuplicateDisplayName" ]))
            for shown in [ "Unknown function '"; "Unknown type '"; "Unknown capability '"; "Unknown effect '"; "Unknown field on "; "Unknown variant on "; "Unknown field on variant "; "Unknown operation on "; "Unknown parameter on operation "; "Unknown parameter on "; "Unknown new function parameter"; " Available: "; "is not a product type"; "is not a union type"; "is the function being created"; "keepUnlisted applies only"; "No current function matches the explicit declaration selector"; "Selector ownership must be local or imported."; "Unsupported symbolic expression recipe kind"; "Unsupported symbolic type kind"; "already exists. Choose a unique name"; "Pass exactly one of body and bodyFile."; "Ambiguous new function parameter 'x'."; "Ambiguous parameter on made 'x'."; "Current candidates (advice only)"; "SimulationRejected"; "Largest subtrees within the limit" ] @ preflightShown do
                Assert.True(shownText.Contains shown, $"No F# reply shows {shown}; the by-name parity test does not cover it.")
            Assert.True(mismatches.Count = 0, $"{mismatches.Count} mismatches:\n" + String.Join("\n", mismatches |> Seq.truncate 12))
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
    let private snapshot () =
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
        { Snapshot.empty (id<SnapshotTag> "zig-host-inference") with Types = types; Functions = [ twice ] }

    /// Requests reaching each inference diagnostic, the location suffix through each renamed field,
    /// F#'s traversal order around kinds the Zig host does not infer, and the type-parameter cases.
    let private requests () =
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
        let unported = recipe "listMap" [ "source", listEmpty int64T; "functionId", "\"zig-twice\""; "context", "[]" ]
        let line (parameters: string list) resultType body = $"""{{"id":"i","op":"simulateCreateFunction","displayName":"made","parameters":[{String.Join(",", parameters)}],"resultType":{resultType},"requiredEffects":[],"body":{body}}}"""
        let parameter name (t: string) = $"""{{"displayName":"{name}","type":{t}}}"""
        let withTypeParameters (parameters: string list) resultType body = (line parameters resultType body).Replace("\"requiredEffects\":[]", "\"requiredEffects\":[],\"typeParameters\":[\"T\"]")
        [ // Fixed signatures: a fixed operand, a shape, a bound variable, the equatable constraint before the next operand.
          line [] unitT (binary "stringConcat" int str)
          line [] unitT badList
          line [] unitT (recipe "listAppendItem" [ "list", listEmpty int64T; "item", str ])
          line [] unitT (recipe "stringContains" [ "value", int; "substring", str ])
          line [] unitT (recipe "listIndexOf" [ "list", listEmpty float64T; "item", unported ])
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
          // Kinds the Zig host does not infer, reached or not in F#'s order.
          line [] unitT (binary "stringConcat" int unported)
          line [] unitT (binary "stringConcat" str unported)
          line [] unitT (conditional bool unported int)
          // Type parameters: owner-named in operand messages, fresh identities elsewhere.
          withTypeParameters [ parameter "x" (typeParameter "T") ] int64T (slot 0)
          withTypeParameters [ parameter "x" (typeParameter "T") ] unitT (binary "valueEqual" (slot 0) (slot 0))
          withTypeParameters [ parameter "x" (typeParameter "T") ] unitT (recipe "listConstruct" [ "itemType", typeParameter "T"; "items", array [ int ] ])
          withTypeParameters [ parameter "x" (typeParameter "T") ] unitT (recipe "setEmpty" [ "itemType", typeParameter "T" ])
          withTypeParameters [ parameter "x" (typeParameter "T") ] unitT (conditional bool (slot 0) int)
          withTypeParameters [ parameter "x" (typeParameter "T") ] unitT (access (slot 0) "zig-point-x")
          withTypeParameters [ parameter "x" (typeParameter "T") ] unitT (construct "zig-Pair" [ bound "zig-pair-first" (slot 0); bound "zig-pair-second" (listEmpty int64T) ])
          withTypeParameters [ parameter "x" (typeParameter "T") ] (generic "list" [ typeParameter "T" ]) (recipe "productUpdate" [ "typeId", quoted "zig-Point"; "source", slot 0; "fields", array [ bound "zig-point-x" int ] ])
          line [ parameter "x" (typeParameter "Point") ] unitT (recipe "listConstruct" [ "itemType", named "zig-Point" []; "items", array [ slot 0 ] ]) ]

    [<Fact; Trait("Suite", "Extended")>]
    let ``The Zig host infers simulateCreateFunction bodies as the F# host does`` () =
        withDirectory (fun dir ->
            let path = Path.Combine(dir, "inference.json")
            File.WriteAllText(path, SnapshotCodec.serialize (snapshot ()) |> unwrap)
            use session = AuthoringSession.openReadOnly path |> unwrap
            let lines = requests ()
            // Built before the clock starts: the first run of a changed host compiles it.
            zigHost () |> ignore
            let clock = Stopwatch.StartNew()
            let code, errors, answers = run true [ "open"; "--snapshot"; path; "--read-only" ] lines
            // The deep generic constructors would take 2^30 inferences without the cache.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds 20.0, $"The Zig host took {clock.Elapsed} to answer the inference corpus.")
            Assert.True((code = 0), errors)
            Assert.Equal(lines.Length, answers.Length)
            let mismatches = ResizeArray<string>()
            let answered = Text.StringBuilder()
            let mutable located = 0
            for request, zig in List.zip lines answers do
                let fsharp = AuthoringProtocol.executeLine session request
                answered.Append(fsharp) |> ignore
                if (locatedPath zig).IsSome then located <- located + 1
                if not (createReplyMatches request fsharp zig) then mismatches.Add $"{request}\n F#:  {fsharp}\n Zig: {zig}"
                // Every kind here is inferred, except the one deliberately unported recipe.
                if unportedKindReply zig && not (request.Contains "\"kind\":\"listMap\"") then mismatches.Add $"{request}\n Zig did not infer: {zig}"
            let shownText = answered.ToString().Replace("\\u0027", "'").Replace("\\u003C", "<").Replace("\\u003E", ">")
            // The wire spells one `otherwise` arm, so a second one is a decode rejection.
            for shown in Set.toList (Set.remove "PureExpressionMatchOtherwiseDuplicate" inferenceCodes) @ [ "(type parameter "; "The slot is found at depths 0, 1;"; "Visible frames, innermost first: depth 0 = Bind body (binds Value)"; "binds 'radius'"; "(not a declared field)"; "At body.end (listLength)"; "At body.defaultValue (listLength)"; "At body.key (listLength)"; "At body.left (listLength)"; "At body.right (listLength)"; "At body.items[1] (listLength)"; "At body.fields[1].value (listLength)"; "At body.arms[2].body (listLength)"; "At body.arms[1].body (listLength)"; "At body.fields[0].value (originalTarget)"; "At body.arms[0].body (originalTarget)"; "At body.right (originalTarget)"; "The function body produces T, but the requested result type is Int64."; "found T and Int64"; "but the source recipe produces T. At body (fieldAccess)."; "but the source recipe produces T. At body (productUpdate)."; "requires List<T>, but the recipe produces List<Int64>"; "The function body produces " + String.replicate 30 "Box<" + "Int64" + String.replicate 30 ">" + ", but"; "The function body produces " + String.replicate 28 "Maybe<" + "Point" + String.replicate 28 ">" + ", but"; "Exception<> is not equatable"; "at Shape.circle.radius"; "at List item"; "type parameters are never equatable" ] do
                Assert.True(shownText.Contains shown, $"No F# reply shows {shown}; the inference parity test does not cover it.")
            Assert.True(mismatches.Count = 0, $"{mismatches.Count} mismatches:\n" + String.Join("\n", mismatches |> Seq.truncate 12))
            Assert.True(located > 60, $"Only {located} Zig replies locate their diagnostic."))
