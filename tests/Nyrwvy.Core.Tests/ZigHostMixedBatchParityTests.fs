namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Xunit

module ZigHostMixedBatchParityTests =
    open ZigHostParityTests

    let private sid<'T> value = StableId.createForTesting<'T> value
    let private integer = """{"kind":"primitive","primitive":"int64"}"""
    let private boolean = """{"kind":"boolLiteral","value":true}"""
    let private literal = """{"kind":"int64Literal","value":"1"}"""
    let private fn name body = $"""{{"displayName":"{name}","exported":true,"parameters":[],"resultType":{integer},"requiredEffects":[],"body":{body}}}"""
    let private create name body = "{" + "\"kind\":\"createFunction\"," + (fn name body).Substring(1)
    let private batch steps = $"""{{"id":"batch","op":"simulateBatchByName","steps":[{String.concat "," steps}]}}"""
    let private success reply =
        use doc = parseDocument reply
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), reply)
        doc.RootElement.GetProperty("result").Clone()
    let private proposal reply = (success reply).GetProperty("proposalId").GetString()
    let private operation op id = $"""{{"id":"lifecycle","op":"{op}","proposalId":"{id}"}}"""
    let private flag = """{"kind":"createType","displayName":"Flag","typeKind":{"kind":"product","fields":[]}}"""
    let private group = """{"kind":"createTypes","types":[{"displayName":"First","typeKind":{"kind":"product","fields":[]}},{"displayName":"Second","typeKind":{"kind":"product","fields":[]}}]}"""
    let private rename = """{"kind":"operations","operations":[{"kind":"renameFunction","function":"made","displayName":"renamed"}]}"""
    let private replace = $"""{{"kind":"replaceBody","function":"renamed","replacement":{literal}}}"""
    let private ordered =
        [ flag; group; create "made" literal; rename; replace
          """{"kind":"createFunctions","functions":[{"displayName":"after","parameters":[],"resultType":{"kind":"primitive","primitive":"int64"},"requiredEffects":[],"body":{"kind":"directCall","function":"renamed","arguments":[]}},{"displayName":"last","parameters":[],"resultType":{"kind":"primitive","primitive":"int64"},"requiredEffects":[],"body":{"kind":"directCall","function":"after","arguments":[]}}]}""" ]

    [<Fact; Trait("Suite", "Extended")>]
    let ``Mixed steps preserve ordered resolution strict envelopes and failure allocator state`` () =
        withDirectory (fun dir ->
            let input = Path.Combine(dir, "input")
            Directory.CreateDirectory input |> ignore
            let source = Path.Combine(input, "snapshot.json")
            let fixture = ZigHostCreateInferenceTests.snapshot ()
            File.WriteAllText(source, SnapshotCodec.serialize fixture |> unwrap)
            let failures =
                [ batch []; """{"id":"b","op":"simulateBatchByName","steps":null}"""
                  batch [ """{"kind":"createType","\ud800":1,"kind":"unknown"}""" ]
                  batch [ """{"\ud800":1,"kind":"unknown"}""" ]
                  batch [ """{"kind":"unknown","\ud800":1}""" ]
                  batch [ """{"kind":"\ud800"}""" ]
                  batch [ """{"kind":"createType","displayName":"A","typeKind":{"kind":"product","fields":[]},"\ud800":1}""" ]
                  batch [ "null" ]; batch [ "{}" ]; batch [ "{\"kind\":false}" ]; batch [ "{\"kind\":\"bogus\"}" ]
                  batch [ flag; create "made" literal; """{"kind":"replaceBody","function":"missing","replacement":{"kind":"unitLiteral"}}""" ]
                  batch [ (create "made" literal).Replace("\"kind\":\"createFunction\"", "\"kind\":\"createFunction\",\"kind\":\"createFunction\"") ]
                  batch [ (create "made" literal).Replace("\"parameters\":[]", "\"parameters\":[],\"parameters\":[]") ]
                  batch [ create "wrong" """{"kind":"boolLiteral","value":true}""" ]
                  batch [ """{"kind":"operations","operations":[{"kind":"renameFunction","function":"unknown","displayName":"x"}]}""" ]
                  batch [ """{"kind":"createFunctions","functions":[{},null]}""" ]
                  batch [ flag.Replace("\"displayName\":\"Flag\"", "\"displayName\":\"Flag\",\"expectedSnapshotId\":\"x\"") ]
                  batch [ flag.Replace("\"displayName\":\"Flag\"", "\"displayName\":\"Flag\",\"structuredReview\":true") ]
                  (batch [ flag ]).Replace("\"steps\":", "\"expectedSnapshotId\":\"old\",\"steps\":") ]
            let good = batch ordered
            let requests =
                [ for failure in failures do
                      yield failure
                      yield good
                  yield good.Replace("\"steps\":", "\"detail\":\"full\",\"steps\":")
                  yield good.Replace("\"steps\":", "\"detail\":\"full\",\"structuredReview\":true,\"steps\":")
                  let target = fixture.Functions |> List.find (fun fn -> fn.DisplayName = "twice")
                  yield batch [ flag; $"""{{"kind":"replaceTarget","function":"twice","targetNodeId":"{StableId.value target.Body.ResultNodeId}","replacement":{boolean}}}""" ]
                  yield batch [ group ]
                  yield batch [ """{"kind":"createFunctions","functions":[{"displayName":"grouped","parameters":[],"resultType":{"kind":"primitive","primitive":"int64"},"requiredEffects":[],"body":{"kind":"int64Literal","value":"1"}}]}""" ]
                  yield batch [ $"""{{"kind":"replaceBody","function":"twice","replacement":{boolean}}}""" ]
                  yield batch [ $"""{{"kind":"replaceTarget","function":"twice","targetNodeId":"{StableId.value target.Body.ResultNodeId}","replacement":{boolean}}}""" ]
                  yield batch [ """{"kind":"createType","displayName":"Recursive","typeKind":{"kind":"product","fields":[{"displayName":"next","type":{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"named","name":"Recursive"}]}}]}}""" ]
                  yield batch [ """{"kind":"createTypes","types":[{"displayName":"MutualLeft","typeKind":{"kind":"product","fields":[{"displayName":"right","type":{"kind":"named","name":"MutualRight"}}]}},{"displayName":"MutualRight","typeKind":{"kind":"product","fields":[{"displayName":"left","type":{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"named","name":"MutualLeft"}]}}]}}]}""" ]
                  yield """{"id":"p","op":"proposals"}""" ]
            let expected, actual = seededPair "mixed-errors" source requests
            Assert.Equal<string list>(expected, actual)
            for index in 0 .. failures.Length - 1 do
                use rejected = parseDocument expected[index * 2]
                Assert.False(rejected.RootElement.GetProperty("ok").GetBoolean(), expected[index * 2])
                success expected[index * 2 + 1] |> ignore
            for reply in expected |> List.skip (failures.Length * 2) do success reply |> ignore)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Mixed file batches own one proposal and retain reviews through commit reject and reopen`` () =
        withDirectory (fun dir ->
            for records, resolved in [ false, false; true, false; false, true; true, true ] do
                let case = Path.Combine(dir, $"{records}-{resolved}")
                let input = Path.Combine(case, "input")
                Directory.CreateDirectory input |> ignore
                let source = Path.Combine(input, if records then "snapshot.nyrec" else "snapshot.json")
                let snapshot = Snapshot.empty (StableId.createForTesting<SnapshotTag> "018bcfe5-6700-7000-8000-000000000001")
                let dependency = Snapshot.empty (sid<SnapshotTag> "018bcfe5-6700-7000-8000-000000000099")
                let json = if resolved then SnapshotCodec.serializeResolved dependency snapshot |> unwrap else SnapshotCodec.serialize snapshot |> unwrap
                File.WriteAllText(source, if records then RecordCodec.ofJson json else json)
                if resolved then
                    let manifest = memberOf dependency (Some("snapshotJson", SnapshotCodec.serialize dependency |> unwrap)) "dependency.json"
                    File.WriteAllText(source + ".dependency.json", manifest.ToJsonString())
                File.WriteAllText(Path.Combine(input, "body.json"), literal)
                File.WriteAllText(Path.Combine(input, "functions.json"), "[" + fn "fileFunction" literal + "]")
                File.WriteAllText(Path.Combine(input, "steps.json"), "[" + String.concat "," (ordered @ [ """{"kind":"createFunctions","functionsFile":"functions.json"}"""; (create "bodyFile" literal).Replace("\"body\":" + literal, "\"bodyFile\":\"body.json\"") ]) + "]")
                let fsharp, zig = seededWritablePair "mixed-files" source case (fun path send ->
                    for request in
                        [ """{"id":"b","op":"simulateBatchByName","stepsFile":"../outside.json"}"""
                          """{"id":"b","op":"simulateBatchByName","steps":[],"stepsFile":"steps.json"}"""
                          batch [ flag; (create "bad" literal).Replace("\"body\":" + literal, "\"bodyFile\":\"missing.json\"") ] ] do
                        use rejected = parseDocument (send request)
                        Assert.False(rejected.RootElement.GetProperty("ok").GetBoolean())
                    let request = """{"id":"b","op":"simulateBatchByName","stepsFile":"steps.json","detail":"full"}"""
                    let stale = send request |> proposal
                    let current = send request |> proposal
                    let inspect id =
                        send (operation "viewProposal" id) |> success |> ignore
                        for structured in [ "false"; "true" ] do
                            for path in [ "/review"; "/review/steps"; "/review/steps/4/batch" ] do
                                send $"""{{"id":"page","op":"readProposal","proposalId":"{id}","structuredReview":{structured},"path":"{path}","limit":1}}""" |> success |> ignore
                            Assert.Contains("Cannot traverse a scalar", send $"""{{"id":"page","op":"readProposal","proposalId":"{id}","structuredReview":{structured},"path":"/review/steps/4/batch/0"}}""")
                    inspect current
                    send """{"id":"p","op":"proposals"}""" |> success |> ignore
                    send (operation "commit" current) |> success |> ignore
                    inspect stale
                    Assert.Contains("StaleBaseSnapshot", send (operation "commit" stale))
                    send (operation "reject" stale) |> success |> ignore
                    Assert.Contains("ProposalNotFound", send (operation "readProposal" stale))
                    send """{"id":"d","op":"declarations"}""" |> success |> ignore
                    let external = send (batch [ create "externalProbe" literal ]) |> proposal
                    let before = File.ReadAllText path
                    File.WriteAllText(path, before + " ")
                    Assert.Contains("ExternalModification", send (operation "commit" external))
                    Assert.Contains("SessionInvalidated", send request)
                    File.WriteAllText(path, before))
                for directory in [ fsharp; zig ] do
                    let path = Path.Combine(directory, Path.GetFileName source)
                    let requests = [ """{"id":"s","op":"status"}"""; """{"id":"f","op":"readFunction","function":"last","detail":"full"}""" ]
                    let readHost zig =
                        let code, errors, replies = run zig [ "open"; "--snapshot"; path; "--read-only" ] requests
                        Assert.True((code = 0), errors)
                        replies |> List.iter (success >> ignore)
                        List.map comparable replies
                    Assert.Equal<string list>(readHost false, readHost true))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Mixed compact reviews project whole steps and final readiness resolves intermediate obligations`` () =
        withDirectory (fun dir ->
            let input = Path.Combine(dir, "input")
            Directory.CreateDirectory input |> ignore
            let source = Path.Combine(input, "snapshot.json")
            let fixture = ZigHostCreateInferenceTests.snapshot ()
            let target = fixture.Functions |> List.find (fun fn -> fn.DisplayName = "twice")
            let caller =
                { target with Id = StableId.createForTesting<FunctionTag> "mixed-caller"; DisplayName = "caller"; Parameters = []
                              Body = { Nodes = [ { Id = StableId.createForTesting<GraphNodeTag> "mixed-value"; Kind = SemanticNodeKind.Int64Literal 1L }
                                                 { Id = StableId.createForTesting<GraphNodeTag> "mixed-call"; Kind = SemanticNodeKind.DirectCall(target.Id, [ { TargetParameterId = target.Parameters.Head.Id; ValueNodeId = StableId.createForTesting<GraphNodeTag> "mixed-value" } ]) } ]; ResultNodeId = StableId.createForTesting<GraphNodeTag> "mixed-call" } }
            File.WriteAllText(source, SnapshotCodec.serialize { fixture with Functions = [ target; caller ] } |> unwrap)
            let add = $"""{{"kind":"operations","operations":[{{"kind":"addFunctionParameter","function":"twice","displayName":"extra","type":{integer},"callSites":[{{"kind":"leaveMissing","callNodeId":"mixed-call"}}]}}]}}"""
            let remove = """{"kind":"operations","operations":[{"kind":"removeFunctionParameter","function":"twice","parameter":"extra"}]}"""
            let large = batch [ for index in 0 .. 59 -> create $"step{index}" literal ]
            let requests = [ batch [ add; remove ]; batch [ flag; add ]; large ]
            let expected, actual = seededPair "mixed-readiness" source requests
            Assert.Equal<string list>(expected, actual)
            let ready = (success expected[0]).GetProperty("review").GetProperty("readiness").GetProperty("kind").GetString()
            let incomplete = (success expected[1]).GetProperty("review").GetProperty("readiness").GetProperty("kind").GetString()
            Assert.Equal("semanticallyReady", ready)
            Assert.Equal("incomplete", incomplete)
            Assert.True((success expected[2]).GetProperty("projection").GetProperty("requiresDetailedReview").GetBoolean())
            seededWritablePair "mixed-policy" source (Path.Combine(dir, "lifecycle")) (fun _ send ->
                let incomplete = send (batch [ flag; add ]) |> proposal
                Assert.Contains("BlockingObligations", send (operation "commit" incomplete))
                let ready = send (batch [ add; remove ]) |> proposal
                send (operation "commit" ready) |> success |> ignore
                send (operation "reject" incomplete) |> success |> ignore
                let incomplete = send (batch [ add ]) |> proposal
                send $"""{{"id":"commit","op":"commit","proposalId":"{incomplete}","allowIncomplete":true}}""" |> success |> ignore)
            |> ignore)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Mixed batches enforce imported ownership and keep file failures local to their step`` () =
        withDirectory (fun dir ->
            let visible: SemanticType = { Id = sid<TypeTag> "mixed-imported-type"; DisplayName = "Visible"; Exported = true; TypeParameters = []; Kind = SemanticTypeKind.Product [] }
            let hidden = { visible with Id = sid<TypeTag> "mixed-hidden-type"; DisplayName = "Hidden"; Exported = false }
            let dependency = { Snapshot.empty (sid<SnapshotTag> "mixed-dependency") with Types = [ visible; hidden ] }
            let local = Snapshot.empty (sid<SnapshotTag> "mixed-local")
            let source = Path.Combine(dir, "snapshot.json")
            File.WriteAllText(source, SnapshotCodec.serializeResolved dependency local |> unwrap)
            let manifest = memberOf dependency (Some("snapshotJson", SnapshotCodec.serialize dependency |> unwrap)) "dependency.json"
            File.WriteAllText(source + ".dependency.json", manifest.ToJsonString())
            let field name = $"""{{"kind":"createType","displayName":"Owner","typeKind":{{"kind":"product","fields":[{{"displayName":"value","type":{{"kind":"named","name":"{name}"}}}}]}}}}"""
            let requests =
                [ batch [ flag; field "Visible" ]
                  batch [ flag; field "Hidden" ]
                  batch [ flag; """{"kind":"operations","operations":[{"kind":"renameType","type":"Visible","displayName":"changed"}]}""" ]
                  """{"id":"p","op":"proposals"}""" ]
            let expected, actual = seededPair "mixed-dependency" source requests
            Assert.Equal<string list>(expected, actual)
            let id = expected[0] |> proposal
            Assert.Contains("BatchStepRejected", expected[1])
            Assert.Contains("ImportedDeclarationModified", expected[2])
            Assert.Equal(1, (success expected[3]).GetProperty("count").GetInt32())
            let readonlyRequests = [ requests[0]; operation "commit" id; operation "reject" id ]
            let expected, actual = seededPair "mixed-dependency" source readonlyRequests
            Assert.Equal<string list>(expected, actual)
            Assert.Contains("ReadOnlySession", expected[1]))
