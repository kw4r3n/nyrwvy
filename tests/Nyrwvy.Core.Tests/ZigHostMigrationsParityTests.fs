namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Nyrwvy.Core
open Xunit

module ZigHostMigrationsParityTests =
    open ZigHostParityTests
    let private sid<'T> s = StableId.createForTesting<'T> s
    let private fixture () =
        let int64 = TypeRef.Primitive PrimitiveType.Int64
        let parameter: SemanticParameter = { Id = sid<ParameterTag> "m-p"; DisplayName = "value"; Type = int64 }
        let target: SemanticFunction =
            { Id = sid<FunctionTag> "m-target"; DisplayName = "target"; Exported = true; MustNotWait = false
              TypeParameters = []; Parameters = [parameter]; ResultType = int64; RequiredEffects = []
              Body = { Nodes = [{ Id = sid<GraphNodeTag> "m-ref"; Kind = SemanticNodeKind.ParameterReference parameter.Id }]; ResultNodeId = sid<GraphNodeTag> "m-ref" } }
        let caller =
            { target with Id = sid<FunctionTag> "m-caller"; DisplayName = "caller"; Parameters = []
                          Body = { Nodes = [{ Id = sid<GraphNodeTag> "m-value"; Kind = SemanticNodeKind.Int64Literal 1L }
                                            { Id = sid<GraphNodeTag> "m-call"; Kind = SemanticNodeKind.DirectCall(target.Id, [{ TargetParameterId = parameter.Id; ValueNodeId = sid<GraphNodeTag> "m-value" }]) }]
                                   ResultNodeId = sid<GraphNodeTag> "m-call" } }
        { Id = sid<SnapshotTag> "migration-base"; ParentId = None; Types = []; Effects = []; Capabilities = []; Functions = [target; caller]; Obligations = [] }
    let private request byName parameter =
        let op = (if parameter then "simulateParameterTypeMigration" else "simulateResultTypeMigration") + (if byName then "ByName" else "")
        let target = if byName then "\"function\":\"target\"" else "\"targetFunctionId\":\"m-target\""
        let changed = if parameter then (if byName then "\"parameter\":\"value\", " else "\"targetParameterId\":\"m-p\", ") + "\"newParameterType\"" else "\"newResultType\""
        let caller = if byName then "\"function\":\"caller\"" else "\"callerFunctionId\":\"m-caller\""
        let body = if parameter then """{"kind":"int64Literal","value":2}""" else """{"kind":"stringLiteral","value":"new"}"""
        $"""{{"id":"migration","op":"{op}",{target},{changed}:{{"kind":"primitive","primitive":"string"}},"targetReplacement":{body},"callerRewrites":[{{{caller},"replacement":{{"kind":"int64Literal","value":3}}}}]}}"""

    [<Fact; Trait("Suite", "Extended")>]
    let ``Parameter and result migration reviews match exact and symbolic seeded streams`` () =
        withDirectory (fun dir ->
            let path = Path.Combine(dir, "migration.json")
            File.WriteAllText(path, SnapshotCodec.serialize (fixture ()) |> unwrap)
            let lines =
                [ for byName in [false; true] do
                    for parameter in [false; true] do
                        let line = request byName parameter
                        yield line
                        let full = JsonNode.Parse(line)
                        full["detail"] <- JsonValue.Create "full"
                        yield full.ToJsonString()
                        full["structuredReview"] <- JsonValue.Create true
                        yield full.ToJsonString() ]
            let expected, actual = seededPair "migrations-reviews" path lines
            Assert.Equal<string list>(expected, actual)
            for line in expected do
                use parsed = parseDocument line
                Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), line))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Migration caller recipes redirect stable calls to the changed target signature`` () =
        withDirectory (fun dir ->
            let path = Path.Combine(dir, "calls.json")
            File.WriteAllText(path, SnapshotCodec.serialize (fixture ()) |> unwrap)
            let lines =
                [ for byName in [false;true] do
                    for parameter in [false;true] do
                        let argument = if parameter then """{"kind":"stringLiteral","value":"argument"}""" else """{"kind":"int64Literal","value":7}"""
                        let call = if byName then $"""{{"kind":"directCall","function":"target","arguments":[{{"parameter":"value","value":{argument}}}]}}""" else $"""{{"kind":"directCall","functionId":"m-target","arguments":[{{"parameterId":"m-p","value":{argument}}}]}}"""
                        let body = if parameter then call else $"""{{"kind":"stringLength","value":{call}}}"""
                        let line = JsonNode.Parse(request byName parameter)
                        line["callerRewrites"].[0].["replacement"] <- JsonNode.Parse body
                        yield line.ToJsonString()
                        line["detail"] <- JsonValue.Create "full"
                        line["structuredReview"] <- JsonValue.Create true
                        yield line.ToJsonString() ]
            let expected, actual = seededPair "migrations-calls" path lines
            Assert.Equal<string list>(expected, actual)
            for line in expected do
                use parsed = parseDocument line
                Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), line))

    let private mutate (line: string) action =
        let node = JsonNode.Parse(line)
        action node
        node.ToJsonString()

    [<Fact; Trait("Suite", "Extended")>]
    let ``Migration rejections preserve phase and following allocations`` () =
        withDirectory (fun dir ->
            let path = Path.Combine(dir, "negative.json")
            File.WriteAllText(path, SnapshotCodec.serialize (fixture ()) |> unwrap)
            let invalids line =
                [ mutate line (fun n -> n["expectedSnapshotId"] <- JsonValue.Create "stale")
                  mutate line (fun n -> n["callerRewrites"] <- JsonNode.Parse "[]")
                  mutate line (fun n -> n["callerRewrites"].AsArray().Add(n["callerRewrites"].[0].DeepClone()))
                  mutate line (fun n -> n["targetReplacement"] <- JsonNode.Parse """{"kind":"parameterSlot","index":0}""")
                  mutate line (fun n -> n["targetReplacement"] <- JsonNode.Parse """{"kind":"functionReference","functionId":"m-target"}""")
                  mutate line (fun n -> n["callerRewrites"].[0].["replacement"] <- JsonNode.Parse """{"kind":"stringLiteral","value":"wrong"}""")
                  mutate line (fun n -> n["callerRewrites"].[0].["replacement"] <- JsonNode.Parse """{"kind":"parameter","parameterId":"unknown"}""")
                  mutate line (fun n -> n["callerRewrites"].[0].["replacement"] <- JsonNode.Parse """{"kind":"directCall","functionId":"m-target","arguments":[]}""")
                  mutate line (fun n -> n["targetReplacement"] <- JsonNode.Parse """{"kind":"taskScope","bodyFunctionId":"m-target"}""")
                  mutate line (fun n -> n["targetReplacement"] <- JsonNode.Parse """{"kind":"cancellationShield","bodyFunctionId":"m-target"}""")
                  mutate line (fun n -> n["targetReplacement"] <- JsonNode.Parse """{"kind":"parallelAll","source":{"kind":"unitLiteral"},"workerFunctionId":"m-target"}""")
                  mutate line (fun n -> n["targetReplacement"] <- JsonNode.Parse """{"kind":"taskStart","scope":{"kind":"unitLiteral"},"workerFunctionId":"m-target","item":{"kind":"unitLiteral"}}""")
                  mutate line (fun n -> n["targetReplacement"] <- JsonNode.Parse """{"kind":"exceptionCatch","bodyFunctionId":"m-target","handlerFunctionId":"m-target"}""")
                  mutate line (fun n -> n["targetReplacement"] <- JsonNode.Parse """{"kind":"taskStart","scope":{"kind":"unitLiteral"},"workerFunctionId":"m-caller","context":[{"parameterId":"unused","value":{"kind":"parameterSlot","index":0}}],"item":{"kind":"functionReference","functionId":"m-target"}}""")
                  line.Replace("\"id\":\"migration\"", "\"id\":\"migration\",\"unexpected\":0", StringComparison.Ordinal)
                  line.Replace("\"op\":", "\"id\":\"duplicate\",\"op\":", StringComparison.Ordinal)
                  line.Replace("\"migration\"", "\"\\uD800\"", StringComparison.Ordinal) ]
            let lines =
                [ for parameter in [false; true] do
                    let valid = request false parameter |> fun line -> JsonNode.Parse(line).ToJsonString()
                    for invalid in invalids valid do yield invalid; yield valid ]
            let expected, actual = seededPair "migrations-negative" path lines
            Assert.Equal<string list>(expected, actual)
            for index, line in expected |> List.indexed do
                use parsed = parseDocument line
                Assert.Equal(index % 2 = 1, parsed.RootElement.GetProperty("ok").GetBoolean()))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Recursive generic migrations retain original type and parameter ownership through nested frames`` () =
        withDirectory (fun dir ->
            let baseSnapshot = fixture ()
            let t = sid<TypeParameterTag> "m-T"
            let u = sid<TypeParameterTag> "m-U"
            let functions =
                baseSnapshot.Functions |> List.map (fun f ->
                    if f.DisplayName = "target" then
                        { f with TypeParameters = [t, "T"]
                                 Parameters = f.Parameters |> List.map (fun p -> { p with Type = TypeRef.TypeParameter t })
                                 ResultType = TypeRef.TypeParameter t }
                    else
                        { f with TypeParameters = [u, "U"]
                                 Parameters = [{ Id = sid<ParameterTag> "m-cp"; DisplayName = "unused"; Type = TypeRef.TypeParameter u }] })
            let generic = { baseSnapshot with Functions = functions }
            let path = Path.Combine(dir, "generic.json")
            File.WriteAllText(path, SnapshotCodec.serialize generic |> unwrap)
            let lines =
                [ for byName in [false; true] do
                    for parameter in [false; true] do
                        let typeRef = if byName then """{"kind":"typeParameter","name":"T"}""" else """{"kind":"typeParameter","typeParameterId":"m-T"}"""
                        let value = if byName then """{"kind":"parameter","name":"value"}""" else """{"kind":"parameter","parameterId":"m-p"}"""
                        let body =
                            if not parameter then $"""{{"kind":"listConstruct","itemType":{typeRef},"items":[{value}]}}"""
                            elif byName then $"""{{"kind":"directCall","function":"target","arguments":[{{"parameter":"value","value":{value}}}]}}"""
                            else $"""{{"kind":"selfCall","arguments":[{{"slot":0,"value":{value}}}]}}"""
                        let own = if byName then """{"kind":"parameter","name":"unused"}""" else """{"kind":"parameter","parameterId":"m-cp"}"""
                        let line = mutate (request byName parameter) (fun n ->
                            n[if parameter then "newParameterType" else "newResultType"] <- JsonNode.Parse($"""{{"kind":"builtinGeneric","generic":"list","arguments":[{typeRef}]}}""")
                            n["targetReplacement"] <- JsonNode.Parse body
                            n["callerRewrites"].[0].["replacement"] <- JsonNode.Parse($"""{{"kind":"bind","value":{own},"body":{{"kind":"int64Literal","value":3}}}}"""))
                        yield line
                        yield mutate line (fun n -> n["detail"] <- JsonValue.Create "full")
                        yield mutate line (fun n -> n["detail"] <- JsonValue.Create "full"; n["structuredReview"] <- JsonValue.Create true) ]
            let expected, actual = seededPair "migrations-generic" path lines
            Assert.Equal<string list>(expected, actual)
            for line in expected do
                use parsed = parseDocument line
                Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), line))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Migration proposals page view commit reject stale and cross reopen all persistence forms`` () =
        withDirectory (fun dir ->
            for records, resolved in [false,false; true,false; false,true; true,true] do
                let case = Path.Combine(dir, string records + "-" + string resolved)
                let input = Path.Combine(case, "input")
                Directory.CreateDirectory input |> ignore
                let source = Path.Combine(input, if records then "migration.nyrec" else "migration.json")
                let snapshot = if records then recordFixture "migrations" (fixture ()) else fixture ()
                let dependency = Snapshot.empty (sid<SnapshotTag> "018bcfe5-6700-7000-8000-000000000099")
                let json = if resolved then SnapshotCodec.serializeResolved dependency snapshot |> unwrap else SnapshotCodec.serialize snapshot |> unwrap
                File.WriteAllText(source, if records then RecordCodec.ofJson json else json)
                if resolved then File.WriteAllText(source + ".dependency.json", (memberOf dependency (Some("snapshotJson", SnapshotCodec.serialize dependency |> unwrap)) "dependency.json").ToJsonString())
                for suffix in [".backends.json"; ".previous"; ".dependency.json.previous"] do File.WriteAllText(source + suffix, "sentinel")
                let ok reply =
                    use parsed = parseDocument reply
                    Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), reply)
                let proposal reply =
                    ok reply
                    use parsed = parseDocument reply
                    parsed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                let q value = System.Text.Json.JsonSerializer.Serialize<string> value
                let operation op id = $"""{{"id":"life","op":"{op}","proposalId":{q id}}}"""
                let fsharp, zig = seededWritablePair "migrations-lifecycle" source case (fun _ send ->
                    let first = send (request true false) |> proposal
                    let stale = send (request true false) |> proposal
                    let inspect id =
                        send (operation "viewProposal" id) |> ok
                        for structured in [false;true] do
                            send $"""{{"id":"page","op":"readProposal","proposalId":{q id},"path":"/review/diff/bodyRewrites","offset":0,"limit":2,"structuredReview":{string structured |> fun s -> s.ToLowerInvariant()}}}""" |> ok
                        send """{"id":"list","op":"proposals"}""" |> ok
                    inspect first
                    send (operation "commit" first) |> ok
                    inspect stale
                    Assert.Contains("StaleBaseSnapshot", send (operation "commit" stale))
                    send (operation "reject" stale) |> ok
                    Assert.Contains("ProposalNotFound", send (operation "readProposal" stale))
                    let next = mutate (request true true) (fun n -> n["targetReplacement"] <- JsonNode.Parse """{"kind":"stringLiteral","value":"next"}"""; n["callerRewrites"] <- JsonNode.Parse "[]")
                    let id = send next |> proposal
                    inspect id
                    send (operation "commit" id) |> ok
                    let pending = mutate next (fun n -> n["newParameterType"] <- JsonNode.Parse """{"kind":"primitive","primitive":"bool"}""") |> send |> proposal
                    let rename = send """{"id":"rename","op":"simulateOperationsByName","operations":[{"kind":"renameFunction","function":"target","displayName":"renamed"},{"kind":"renameParameter","function":"target","parameter":"value","displayName":"renamedValue"}]}""" |> proposal
                    send (operation "commit" rename) |> ok
                    let listed = send """{"id":"list-renamed","op":"proposals"}"""
                    use listedDocument = parseDocument listed
                    let summary = listedDocument.RootElement.GetProperty("result").GetProperty("proposals").[0].GetProperty("summary").GetString()
                    Assert.Equal("parameterTypeMigration of 'renamed' parameter 'renamedValue'", summary)
                    let removed = send """{"id":"remove","op":"simulateOperationsByName","operations":[{"kind":"deleteFunction","function":"renamed"}]}""" |> proposal
                    send (operation "commit" removed) |> ok
                    let target = snapshot.Functions |> List.find (fun f -> f.DisplayName = "target")
                    let fallback = $"parameterTypeMigration of {StableId.value target.Id} parameter {StableId.value target.Parameters.Head.Id}"
                    Assert.Contains(fallback, send """{"id":"list-removed","op":"proposals"}""")
                    send (operation "reject" pending) |> ok)
                for directory in [fsharp;zig] do
                    let path = Path.Combine(directory, Path.GetFileName source)
                    let lines = ["""{"id":"status","op":"status"}"""; """{"id":"read","op":"readFunction","function":"caller","detail":"full"}"""]
                    let fc, fe, f = run false ["open"; "--snapshot"; path; "--read-only"] lines
                    let zc, ze, z = run true ["open"; "--snapshot"; path; "--read-only"] lines
                    Assert.True((fc = 0 && zc = 0), fe + ze)
                    Assert.Equal<string list>(List.map comparable f, List.map comparable z)
                    for line in f do ok line)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Imported migrations reject before registration and successful local proposals remain usable`` () =
        withDirectory (fun dir ->
            let local = fixture ()
            let imported =
                { local.Functions.Head with Id = sid<FunctionTag> "dep-target"; DisplayName = "imported"
                                            Parameters = []
                                            Body = { Nodes = [{Id = sid<GraphNodeTag> "dep-node"; Kind = SemanticNodeKind.Int64Literal 1L}]; ResultNodeId = sid<GraphNodeTag> "dep-node" } }
            let dependency = { Snapshot.empty (sid<SnapshotTag> "dep-root") with Functions = [imported] }
            let source = Path.Combine(dir, "imported.json")
            File.WriteAllText(source, SnapshotCodec.serializeResolved dependency local |> unwrap)
            File.WriteAllText(source + ".dependency.json", (memberOf dependency (Some("snapshotJson", SnapshotCodec.serialize dependency |> unwrap)) "dependency.json").ToJsonString())
            let valid = request true false
            let invalid = mutate valid (fun n -> n["function"] <- JsonValue.Create "imported"; n["callerRewrites"] <- JsonNode.Parse "[]")
            let expected, actual = seededPair "migrations-imported" source [invalid;valid]
            Assert.Equal<string list>(expected, actual)
            Assert.Contains("ImportedDeclarationModified", expected.Head)
            use parsed = parseDocument expected.[1]
            Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), expected.[1]))

    [<Fact; Trait("Suite", "Extended")>]
    let ``External changes invalidate migration commits without overwriting the snapshot`` () =
        withDirectory (fun dir ->
            let input = Path.Combine(dir, "input")
            Directory.CreateDirectory input |> ignore
            let source = Path.Combine(input, "external.json")
            File.WriteAllText(source, SnapshotCodec.serialize (fixture ()) |> unwrap)
            seededWritablePair "migrations-external" source dir (fun path send ->
                let reply = send (request true false)
                use parsed = parseDocument reply
                Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), reply)
                let id = parsed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                File.AppendAllText(path, "\n")
                let commit = $"""{{"id":"commit","op":"commit","proposalId":"{id}"}}"""
                Assert.Contains("ExternalModification", send commit)
                Assert.Contains("SessionInvalidated", send commit)
                Assert.Contains("SessionInvalidated", send (request true false))) |> ignore)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Migration preserves unrelated blocking obligations and commit policy`` () =
        withDirectory (fun dir ->
            let original = fixture ()
            let helper: SemanticFunction =
                { original.Functions.Head with
                    Id = sid<FunctionTag> "other"
                    DisplayName = "other"
                    Parameters = [{ original.Functions.Head.Parameters.Head with Id = sid<ParameterTag> "other-p" }]
                    Body = { Nodes = [{ Id = sid<GraphNodeTag> "other-node"; Kind = SemanticNodeKind.ParameterReference(sid<ParameterTag> "other-p") }]; ResultNodeId = sid<GraphNodeTag> "other-node" } }
            let blocked: SemanticFunction =
                { helper with
                    Id = sid<FunctionTag> "blocked"
                    DisplayName = "blocked"
                    Parameters = []
                    Body = { Nodes = [{ Id = sid<GraphNodeTag> "blocked-call"; Kind = SemanticNodeKind.DirectCall(helper.Id, []) }]; ResultNodeId = sid<GraphNodeTag> "blocked-call" } }
            let snapshot =
                { original with
                    Functions = original.Functions @ [helper; blocked]
                    Obligations = [{ Id = sid<ObligationTag> "missing"; OriginTransactionId = sid<TransactionTag> "origin"; CallNodeId = sid<GraphNodeTag> "blocked-call"; ParameterId = sid<ParameterTag> "other-p"; Status = ObligationStatus.Open }] }
            let input = Path.Combine(dir, "input")
            Directory.CreateDirectory input |> ignore
            let source = Path.Combine(input, "blocking.json")
            File.WriteAllText(source, SnapshotCodec.serialize snapshot |> unwrap)
            seededWritablePair "migrations-blocking" source dir (fun _ send ->
                let reply = send (request true false)
                use parsed = parseDocument reply
                Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), reply)
                Assert.Contains("incomplete", reply)
                let id = parsed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                Assert.Contains("BlockingObligations", send $"""{{"id":"blocked","op":"commit","proposalId":"{id}"}}""")
                let committed = send $"""{{"id":"allowed","op":"commit","proposalId":"{id}","allowIncomplete":true}}"""
                use final = parseDocument committed
                Assert.True(final.RootElement.GetProperty("ok").GetBoolean(), committed)) |> ignore)
