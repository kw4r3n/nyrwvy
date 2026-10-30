namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Nyrwvy.Core
open Xunit

module ZigHostDependencySimulationParityTests =
    open ZigHostParityTests
    let private sid<'T> text = StableIdAllocator.derived<'T> ("dependency-simulation:" + text)
    let private textId<'T> name = StableId.value (sid<'T> name)
    let private integer = TypeRef.Primitive PrimitiveType.Int64
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private library version fieldType =
        let parameter: SemanticParameter = { Id = sid<ParameterTag> "read-input"; DisplayName = "input"; Type = TypeRef.Named(sid<TypeTag> "Money", []) }
        let reference: SemanticNode = { Id = sid<GraphNodeTag> "read-reference"; Kind = SemanticNodeKind.ParameterReference parameter.Id }
        let field: SemanticNode = { Id = sid<GraphNodeTag> "read-field"; Kind = SemanticNodeKind.FieldAccess(reference.Id, sid<FieldTag> "amount") }
        { Snapshot.empty (sid<SnapshotTag> version) with
            Types = [ { Id = sid<TypeTag> "Money"; DisplayName = (if version = "old" then "Money" else "UpgradedMoney"); Exported = true; TypeParameters = []
                        Kind = SemanticTypeKind.Product [ { Id = sid<FieldTag> "amount"; DisplayName = "amount"; Type = fieldType } ] } ]
            Functions = [ { Id = sid<FunctionTag> "read"; DisplayName = (if version = "old" then "read" else "readNext"); Exported = true; MustNotWait = false; TypeParameters = []
                            Parameters = [ parameter ]; ResultType = fieldType; RequiredEffects = []; Body = { Nodes = [ reference; field ]; ResultNodeId = field.Id } } ] }
    let private fixture () =
        let parameter: SemanticParameter = { Id = sid<ParameterTag> "relay-input"; DisplayName = "input"; Type = TypeRef.Named(sid<TypeTag> "Money", []) }
        let reference: SemanticNode = { Id = sid<GraphNodeTag> "relay-reference"; Kind = SemanticNodeKind.ParameterReference parameter.Id }
        let call: SemanticNode = { Id = sid<GraphNodeTag> "relay-call"; Kind = SemanticNodeKind.DirectCall(sid<FunctionTag> "read", [ { TargetParameterId = sid<ParameterTag> "read-input"; ValueNodeId = reference.Id } ]) }
        let genericParameter: SemanticParameter = { Id = sid<ParameterTag> "identity-input"; DisplayName = "input"; Type = TypeRef.TypeParameter(sid<TypeParameterTag> "T") }
        let genericNode: SemanticNode = { Id = sid<GraphNodeTag> "identity-reference"; Kind = SemanticNodeKind.ParameterReference genericParameter.Id }
        { Snapshot.empty (sid<SnapshotTag> "app") with
            Types = [ { Id = sid<TypeTag> "Holder"; DisplayName = "Holder"; Exported = true; TypeParameters = []
                        Kind = SemanticTypeKind.Product [ { Id = sid<FieldTag> "held"; DisplayName = "held"; Type = integer } ] } ]
            Functions = [ { Id = sid<FunctionTag> "relay"; DisplayName = "relay"; Exported = true; MustNotWait = false; TypeParameters = []
                            Parameters = [ parameter ]; ResultType = integer; RequiredEffects = []; Body = { Nodes = [ reference; call ]; ResultNodeId = call.Id } }
                          { Id = sid<FunctionTag> "identity"; DisplayName = "identity"; Exported = true; MustNotWait = false; TypeParameters = [ sid<TypeParameterTag> "T", "T" ]
                            Parameters = [ genericParameter ]; ResultType = genericParameter.Type; RequiredEffects = []; Body = { Nodes = [ genericNode ]; ResultNodeId = genericNode.Id } } ]
            Effects = [ { Id = sid<EffectTag> "record"; DisplayName = "record"; Exported = true } ]
            Capabilities = [ { Id = sid<CapabilityTag> "Store"; DisplayName = "Store"; Exported = true; TaskBound = false
                               Operations = [ { Id = sid<CapabilityOperationTag> "record"; DisplayName = "record"; MayWait = false
                                                Parameters = [ { Id = sid<CapabilityParameterTag> "record-value"; DisplayName = "value"; Type = integer } ]
                                                ResultType = integer; FailureType = None; EffectId = sid<EffectTag> "record" } ] } ] }
    let private quoted (value: string) = JsonSerializer.Serialize value
    let private request named selection source =
        let prop key value = quoted key + ":" + quoted value
        let fn name = if named then quoted name else quoted (textId<FunctionTag> name)
        let primitive = """{"kind":"primitive","primitive":"string"}"""
        let money = if named then """{"kind":"named","name":"UpgradedMoney"}""" else $"""{{"kind":"named","typeId":"{textId<TypeTag> "Money"}"}}"""
        let parameter = prop (if named then "parameter" else "parameterId") (if named then "input" else textId<ParameterTag> "relay-input")
        let readParameter = prop (if named then "parameter" else "parameterId") (if named then "input" else textId<ParameterTag> "read-input")
        let relayBodyParameter = prop (if named then "name" else "parameterId") (if named then "input" else textId<ParameterTag> "relay-input")
        let fnKey = if named then "function" else "functionId"
        let call = $"""{{"kind":"directCall","{fnKey}":{(if named then quoted "readNext" else fn "read")},"arguments":[{{{readParameter},"value":{{"kind":"parameter",{relayBodyParameter}}}}}]}}"""
        let field =
            prop (if named then "type" else "typeId") (if named then "Holder" else textId<TypeTag> "Holder")
            + "," + prop (if named then "field" else "fieldId") (if named then "held" else textId<FieldTag> "held")
        let op = if named then "\"capability\":\"Store\",\"operation\":\"record\"" else prop "operationId" (textId<CapabilityOperationTag> "record")
        let opParameter = prop (if named then "parameter" else "parameterId") (if named then "value" else textId<CapabilityParameterTag> "record-value")
        let generic = if named then """{"kind":"typeParameter","name":"T"}""" else $"""{{"kind":"typeParameter","typeParameterId":"{textId<TypeParameterTag> "T"}"}}"""
        let genericParameter = prop (if named then "parameter" else "parameterId") (if named then "input" else textId<ParameterTag> "identity-input")
        let genericBody = if named then """{"kind":"parameter","name":"input"}""" else $"""{{"kind":"parameter","parameterId":"{textId<ParameterTag> "identity-input"}"}}"""
        let change =
            if selection then quoted "selection" + ":" + $"""{{"sources":[{quoted source}],"replaces":["{textId<SnapshotTag> "old"}"]}}"""
            else quoted "upgrades" + ":" + $"""[{{"source":{quoted source},"replaces":"{textId<SnapshotTag> "old"}"}}]"""
        let opName = "simulateDependencyUpgrade" + (if named then "ByName" else "")
        $"""{{"id":"repair","op":"{opName}",{change},"repairs":{{"fields":[{{{field},"newType":{money}}}],"functions":[{{"{fnKey}":{fn "relay"},"parameterTypes":[{{{parameter},"type":{money}}}],"resultType":{primitive},"replacement":{call}}},{{"{fnKey}":{fn "identity"},"parameterTypes":[{{{genericParameter},"type":{generic}}}],"resultType":{generic},"replacement":{genericBody}}}],"operations":[{{{op},"parameterTypes":[{{{opParameter},"type":{money}}}],"resultType":{money}}}]}}}}"""
    let private writeFixture dir records =
        Directory.CreateDirectory dir |> ignore
        let encode json = if records then RecordCodec.ofJson json else json
        let old, updated = library "old" integer, library "new" stringType
        let path = Path.Combine(dir, if records then "app.nyrec" else "app.json")
        let source = Path.Combine(dir, if records then "new.nyrec" else "new.json")
        File.WriteAllText(source, SnapshotCodec.serialize updated |> unwrap |> encode)
        File.WriteAllText(path, SnapshotCodec.serializeResolved old (fixture ()) |> unwrap |> encode)
        File.WriteAllText(path + ".dependency.json", (memberOf old (Some("snapshotJson", SnapshotCodec.serialize old |> unwrap)) (Path.Combine(dir, "old.json"))).ToJsonString())
        path, source
    let private success reply =
        use parsed = parseDocument reply
        Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), reply)
        parsed.RootElement.GetProperty("result").Clone()
    let private rejected reply =
        use parsed = parseDocument reply
        Assert.False(parsed.RootElement.GetProperty("ok").GetBoolean(), reply)
        parsed.RootElement.GetProperty("error").GetProperty("code").GetString()
    let private proposal reply = (success reply).GetProperty("proposalId").GetString()
    let private sendProposal op id = $"""{{"id":"lifecycle","op":"{op}","proposalId":"{id}"}}"""
    let private mutate (line: string) action =
        let root = JsonNode.Parse line
        action root
        root.ToJsonString()
    let private strictPair path requests =
        let runHost zig =
            let code, errors, answers = runWithEnvironment [ "NYRWVY_TEST_ID_SEED", "dependency-simulation" ] zig [ "open"; "--snapshot"; path; "--read-only" ] requests
            Assert.True((code = 0), $"zig={zig}, exit={code}, stderr={errors}")
            Assert.Equal(seedWarning, errors)
            Assert.Equal(requests.Length, answers.Length)
            answers
        let expected, actual = runHost false, runHost true
        for request, (left, right) in List.zip requests (List.zip expected actual) do Assert.True((comparable left = comparable right), $"{request}\nF#: {left}\nZig: {right}")
        expected

    [<Fact; Trait("Suite", "Extended")>]
    let ``Dependency repair exact symbolic batch and selection reviews preserve all local repair shapes`` () =
        withDirectory (fun dir ->
            for records in [ false; true ] do
                let path, source = writeFixture (Path.Combine(dir, string records)) records
                let requests =
                    [ for named in [ false; true ] do
                        for selection in [ false; true ] do
                            let line = request named selection source
                            yield line
                            yield mutate line (fun n -> n["detail"] <- JsonValue.Create "full")
                            yield mutate line (fun n -> n["detail"] <- JsonValue.Create "full"; n["structuredReview"] <- JsonValue.Create true)
                      yield """{"id":"declarations","op":"declarations"}"""
                      yield """{"id":"proposals","op":"proposals"}""" ]
                let saved = File.ReadAllBytes path, File.ReadAllBytes(path + ".dependency.json")
                for reply in strictPair path requests do success reply |> ignore
                Assert.Equal(saved, (File.ReadAllBytes path, File.ReadAllBytes(path + ".dependency.json"))))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Dependency invalid repairs preserve guard order and the following successful allocator stream`` () =
        withDirectory (fun dir ->
            let path, source = writeFixture dir false
            let hiddenSource = Path.Combine(dir, "hidden.json")
            let selected = library "new" stringType
            File.WriteAllText(hiddenSource, SnapshotCodec.serialize { selected with Types = [ { selected.Types.Head with Exported = false } ] } |> unwrap)
            let failures named =
                let line = request named true source
                let functionKey = if named then "function" else "functionId"
                let ownerKey = if named then "type" else "typeId"
                let fieldKey = if named then "field" else "fieldId"
                [ mutate line (fun n -> n["expectedSnapshotId"] <- JsonValue.Create "stale")
                  mutate line (fun n -> n["repairs"].["fields"].AsArray().Add(n["repairs"].["fields"].[0].DeepClone()))
                  mutate line (fun n -> n["repairs"].["functions"].AsArray().Add(n["repairs"].["functions"].[0].DeepClone()))
                  mutate line (fun n -> n["repairs"].["operations"].AsArray().Add(n["repairs"].["operations"].[0].DeepClone()))
                  mutate line (fun n -> n["repairs"].["functions"].[0].["parameterTypes"] <- JsonNode.Parse "[]")
                  mutate line (fun n -> n["repairs"].["operations"].[0].["parameterTypes"] <- JsonNode.Parse "[]")
                  mutate line (fun n -> n["repairs"].["functions"].[0].[functionKey] <- JsonValue.Create "missing")
                  mutate line (fun n -> n["repairs"].["fields"].[0].[ownerKey] <- JsonValue.Create (if named then "UpgradedMoney" else textId<TypeTag> "Money"); n["repairs"].["fields"].[0].[fieldKey] <- JsonValue.Create (if named then "amount" else textId<FieldTag> "amount"))
                  mutate line (fun n -> n["repairs"].["functions"].AsArray().RemoveAt 0)
                  mutate line (fun n -> n["repairs"].["functions"].[0].["replacement"] <- JsonNode.Parse """{"kind":"boolLiteral","value":true}""")
                  mutate line (fun n -> n["repairs"].["fields"].[0].["newType"] <- JsonNode.Parse """{"kind":"typeParameter","name":"unknown"}""")
                  mutate line (fun n -> n["repairs"].["functions"].[1].["resultType"] <- JsonNode.Parse """{"kind":"typeParameter","name":"unknown"}""")
                  mutate line (fun n -> n["repairs"].["functions"].[1].["replacement"] <- JsonNode.Parse """{"kind":"parameterSlot","index":0}""")
                  mutate line (fun n -> n["repairs"] <- null)
                  mutate line (fun n -> n["repairs"].["unknown"] <- JsonNode.Parse "[]")
                  mutate line (fun n -> n["selection"].["sources"] <- JsonNode.Parse "[false]")
                  mutate line (fun n -> n["selection"].["sources"] <- JsonNode.Parse "[]"; n["repairs"] <- null)
                  line.Replace("\"fields\":[", "\"fields\":[],\"fields\":[")
                  request named true hiddenSource ]
            for named in [ false; true ] do
                let invalid = failures named
                let good = request named true source
                let requests = [ for line in invalid do yield line; yield good ]
                let expected = strictPair path requests
                for index in 0 .. invalid.Length - 1 do
                    rejected expected[index * 2] |> ignore
                    success expected[index * 2 + 1] |> ignore)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Dependency repairs retain one selected generation through pages views reject commit and cross host reopen`` () =
        withDirectory (fun dir ->
            for records, named in [ false, false; false, true; true, false; true, true ] do
                let case = Path.Combine(dir, $"{records}-{named}")
                let path, source = writeFixture (Path.Combine(case, "input")) records
                File.WriteAllText(path + ".dependency.json.previous", "old-recovery-sentinel")
                File.WriteAllText(path + ".backends.json", "backend-sentinel")
                let first = request named true source
                let fsharp, zig = seededWritablePair "dependency-simulation-lifecycle" path case (fun _ send ->
                    let id = send first |> proposal
                    send """{"id":"listing","op":"proposals"}""" |> success |> ignore
                    send (sendProposal "readProposal" id) |> success |> ignore
                    send ($"""{{"id":"page","op":"readProposal","proposalId":"{id}","path":"/review/changedFunctions","offset":0,"limit":1}}""") |> success |> ignore
                    send (sendProposal "viewProposal" id) |> success |> ignore
                    send (sendProposal "reject" id) |> success |> ignore
                    Assert.Equal("ProposalNotFound", send (sendProposal "commit" id) |> rejected)
                    let committed = send (mutate first (fun n -> n["detail"] <- JsonValue.Create "full"; n["structuredReview"] <- JsonValue.Create true)) |> proposal
                    let stale = send first |> proposal
                    send (sendProposal "commit" committed) |> success |> ignore
                    send """{"id":"declarations","op":"declarations"}""" |> success |> ignore
                    send """{"id":"dependency","op":"dependency"}""" |> success |> ignore
                    send (sendProposal "readProposal" stale) |> success |> ignore
                    send (sendProposal "viewProposal" stale) |> success |> ignore
                    Assert.Equal("DependencyFailure", send (sendProposal "commit" stale) |> rejected)
                    send (sendProposal "reject" stale) |> success |> ignore
                    Assert.Equal("ProposalNotFound", send (sendProposal "commit" committed) |> rejected))
                for folder in [ fsharp; zig ] do
                    let reopened = Path.Combine(folder, Path.GetFileName path)
                    Assert.False(File.Exists(reopened + ".dependency.json.previous"))
                    Assert.Equal("backend-sentinel", File.ReadAllText(reopened + ".backends.json"))
                    let lines = [ """{"id":"s","op":"status"}"""; """{"id":"d","op":"declarations"}"""; """{"id":"f","op":"readFunction","function":"relay","detail":"full"}""" ]
                    let fc, fe, left = run false [ "open"; "--snapshot"; reopened; "--read-only" ] lines
                    let zc, ze, right = run true [ "open"; "--snapshot"; reopened; "--read-only" ] lines
                    Assert.True((fc = 0 && zc = 0), fe + ze)
                    Assert.Equal("", fe + ze)
                    Assert.Equal<string list>(left |> List.map comparable, right |> List.map comparable)
                    for reply in left @ right do success reply |> ignore)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Dependency source approval and persisted base guards refuse changed reviews without discarding proposals`` () =
        withDirectory (fun dir ->
            let path, source = writeFixture (Path.Combine(dir, "input")) false
            let original = File.ReadAllText source
            let line = request true true source
            seededWritablePair "dependency-source-approval" path dir (fun _ send ->
                File.WriteAllText(source, original)
                try
                    let changed = send line |> proposal
                    File.WriteAllText(source, SnapshotCodec.serialize (library "changed" stringType) |> unwrap)
                    Assert.Equal("DependencyFailure", send (sendProposal "commit" changed) |> rejected)
                    send (sendProposal "readProposal" changed) |> success |> ignore
                    File.WriteAllText(source, original)
                    send (sendProposal "reject" changed) |> success |> ignore
                    let stale = send line |> proposal
                    let ordinary = send """{"id":"ordinary","op":"simulateCreateEffect","displayName":"localChange","exported":true}""" |> proposal
                    send (sendProposal "commit" ordinary) |> success |> ignore
                    Assert.Equal("DependencyFailure", send (sendProposal "commit" stale) |> rejected)
                    send (sendProposal "viewProposal" stale) |> success |> ignore
                    send (sendProposal "reject" stale) |> success |> ignore
                    let current = send line |> proposal
                    send (sendProposal "commit" current) |> success |> ignore
                    Assert.Equal("SnapshotCodecFailure", send """{"id":"readonly-import","op":"simulateReplaceBodyByName","function":"readNext","replacement":{"kind":"stringLiteral","value":"forbidden"}}""" |> rejected)
                finally File.WriteAllText(source, original)) |> ignore)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Dependency simulations allow read only reviews and keep blocking obligations behind commit policy`` () =
        withDirectory (fun dir ->
            let path, source = writeFixture (Path.Combine(dir, "input")) false
            let line = request true true source
            let code, _, first = runWithEnvironment [ "NYRWVY_TEST_ID_SEED", "dependency-simulation" ] false [ "open"; "--snapshot"; path; "--read-only" ] [ line ]
            Assert.Equal(0, code)
            let id = proposal first.Head
            let replies = strictPair path [ line; sendProposal "commit" id; sendProposal "reject" id; sendProposal "readProposal" id ]
            success replies.Head |> ignore
            Assert.Equal("ReadOnlySession", rejected replies[1])
            success replies[2] |> ignore
            Assert.Equal("ProposalNotFound", rejected replies[3])
            let original = fixture ()
            let parameter: SemanticParameter = { Id = sid<ParameterTag> "helper-input"; DisplayName = "input"; Type = integer }
            let node: SemanticNode = { Id = sid<GraphNodeTag> "helper-reference"; Kind = SemanticNodeKind.ParameterReference parameter.Id }
            let helper: SemanticFunction =
                { original.Functions.Head with
                    Id = sid<FunctionTag> "helper"; DisplayName = "helper"; Parameters = [ parameter ]
                    ResultType = integer; Body = { Nodes = [ node ]; ResultNodeId = node.Id } }
            let call: SemanticNode = { Id = sid<GraphNodeTag> "blocked-call"; Kind = SemanticNodeKind.DirectCall(helper.Id, []) }
            let blocked = { helper with Id = sid<FunctionTag> "blocked"; DisplayName = "blocked"; Parameters = []; Body = { Nodes = [ call ]; ResultNodeId = call.Id } }
            let incomplete =
                { original with Functions = original.Functions @ [ helper; blocked ]
                                Obligations = [ { Id = sid<ObligationTag> "missing"; OriginTransactionId = sid<TransactionTag> "origin"; CallNodeId = call.Id; ParameterId = parameter.Id; Status = ObligationStatus.Open } ] }
            File.WriteAllText(path, SnapshotCodec.serializeResolved (library "old" integer) incomplete |> unwrap)
            seededWritablePair "dependency-policy" path dir (fun _ send ->
                let reply = send line
                Assert.Contains("incomplete", reply)
                let id = proposal reply
                Assert.Equal("CommitRejected", send (sendProposal "commit" id) |> rejected)
                send ($"""{{"id":"allowed","op":"commit","proposalId":"{id}","allowIncomplete":true}}""") |> success |> ignore) |> ignore)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Dependency selection repairs flatten nested sources and preserve first selection rejection`` () =
        withDirectory (fun dir ->
            for records in [ false; true ] do
                let case = Path.Combine(dir, string records)
                let path, source = writeFixture (Path.Combine(case, "input")) records
                let extra =
                    { Snapshot.empty (sid<SnapshotTag> "extra") with
                        Types = [ { Id = sid<TypeTag> "Extra"; DisplayName = "Extra"; Exported = true; TypeParameters = []; Kind = SemanticTypeKind.Product [] } ] }
                let extraPath = Path.Combine(Path.GetDirectoryName source, "extra.json")
                File.WriteAllText(extraPath, SnapshotCodec.serialize extra |> unwrap)
                let nested = SnapshotCodec.serializeResolved extra (library "new" stringType) |> unwrap
                File.WriteAllText(source, if records then RecordCodec.ofJson nested else nested)
                File.WriteAllText(source + ".dependency.json", (memberOf extra (Some("snapshotJson", SnapshotCodec.serialize extra |> unwrap)) extraPath).ToJsonString())
                for named in [ false; true ] do
                    let line = request named true source |> fun line -> mutate line (fun n -> n["selection"].["sources"].AsArray().Add(JsonValue.Create extraPath))
                    for reply in strictPair path [ line; """{"id":"d","op":"declarations"}""" ] do success reply |> ignore
                let standalone = Path.Combine(case, "first.json")
                File.WriteAllText(standalone, SnapshotCodec.serialize (Snapshot.empty (sid<SnapshotTag> "standalone")) |> unwrap)
                let firstRequests =
                    [ for named in [ false; true ] do
                        let line = request named true source |> fun line -> mutate line (fun n -> n["selection"].["replaces"] <- JsonNode.Parse "[]"; n["repairs"] <- JsonNode.Parse "{}")
                        yield line
                        yield mutate line (fun n -> n["expectedSnapshotId"] <- JsonValue.Create "stale") ]
                for reply in strictPair standalone firstRequests do Assert.Equal("DependencyFailure", rejected reply))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Dependency repair commits invalidate changed snapshot and dependency sidecar files`` () =
        withDirectory (fun dir ->
            for sidecar in [ false; true ] do
                let case = Path.Combine(dir, string sidecar)
                let path, source = writeFixture (Path.Combine(case, "input")) false
                let line = request true true source
                seededWritablePair "dependency-external" path case (fun current send ->
                    let id = send line |> proposal
                    let target = if sidecar then current + ".dependency.json" else current
                    let saved = File.ReadAllText target
                    File.AppendAllText(target, " ")
                    try
                        Assert.Equal((if sidecar then "DependencyFailure" else "ExternalModification"), send (sendProposal "commit" id) |> rejected)
                        Assert.Equal("SessionInvalidated", send line |> rejected)
                    finally File.WriteAllText(target, saved)) |> ignore)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Dependency pair prewrite IO failure keeps disk and proposal recoverable for retry`` () =
        withDirectory (fun dir ->
            let path, source = writeFixture dir false
            let line = request true true source
            let sourceBytes = File.ReadAllBytes path
            let manifestBytes = File.ReadAllBytes(path + ".dependency.json")
            let transcript zig =
                File.WriteAllBytes(path, sourceBytes)
                File.WriteAllBytes(path + ".dependency.json", manifestBytes)
                use host = startWithEnvironment [ "NYRWVY_TEST_ID_SEED", "dependency-pair-io" ] zig [ "open"; "--snapshot"; path ]
                let send request =
                    host.StandardInput.WriteLine(request: string)
                    host.StandardInput.Flush()
                    let reading = host.StandardOutput.ReadLineAsync()
                    Assert.True(reading.Wait silence, "Host did not answer.")
                    Assert.NotNull reading.Result
                    reading.Result
                let id = send line |> proposal
                Directory.CreateDirectory(path + ".dependency.json.previous") |> ignore
                try
                    // OS-specific I/O wording differs; the real error code and on-disk transaction are checked.
                    Assert.Equal("IoFailure", send (sendProposal "commit" id) |> rejected)
                    Assert.Equal<byte>(sourceBytes, File.ReadAllBytes path)
                    Assert.Equal<byte>(manifestBytes, File.ReadAllBytes(path + ".dependency.json"))
                    send (sendProposal "readProposal" id) |> success |> ignore
                finally Directory.Delete(path + ".dependency.json.previous")
                send (sendProposal "commit" id) |> success |> ignore
                host.StandardInput.Close()
                Assert.True(host.WaitForExit(int silence.TotalMilliseconds))
                Assert.Equal(0, host.ExitCode)
                Assert.Equal(seedWarning, host.StandardError.ReadToEnd().Trim())
                Assert.False(File.Exists(path + ".dependency.json.previous"))
                File.ReadAllBytes path, File.ReadAllBytes(path + ".dependency.json")
            Assert.Equal(transcript false, transcript true))
