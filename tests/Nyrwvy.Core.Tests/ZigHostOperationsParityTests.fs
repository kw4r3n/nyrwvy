namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Nyrwvy.Core
open Xunit

module ZigHostOperationsParityTests =
    open ZigHostParityTests

    let private sid<'T> value = StableId.createForTesting<'T> value
    let private integer = """{"kind":"primitive","primitive":"int64"}"""
    let private q (value: string) = JsonSerializer.Serialize value
    let private snapshot () =
        let original = ZigHostCreateInferenceTests.snapshot ()
        let target = original.Functions |> List.find (fun fn -> fn.DisplayName = "twice")
        let caller: SemanticFunction =
            { target with Id = sid<FunctionTag> "op-caller"; DisplayName = "caller"; Parameters = []
                          Body = { Nodes = [ { Id = sid<GraphNodeTag> "op-value"; Kind = SemanticNodeKind.Int64Literal 2L }
                                             { Id = sid<GraphNodeTag> "op-call"; Kind = SemanticNodeKind.DirectCall(target.Id, []) } ]
                                   ResultNodeId = sid<GraphNodeTag> "op-call" } }
        let spare: SemanticFunction =
            { target with Id = sid<FunctionTag> "op-spare"; DisplayName = "spare"; Parameters = []
                          Body = { Nodes = [ { Id = sid<GraphNodeTag> "op-spare-node"; Kind = SemanticNodeKind.BoolLiteral true } ]
                                   ResultNodeId = sid<GraphNodeTag> "op-spare-node" } }
        { original with Functions = [ target; caller; spare ]
                        Types = original.Types @ [ { Id = sid<TypeTag> "op-spare-type"; DisplayName = "SpareType"; Exported = true; TypeParameters = []; Kind = SemanticTypeKind.Product [] } ]
                        Effects = original.Effects @ [ { Id = sid<EffectTag> "op-spare-effect"; DisplayName = "spareEffect"; Exported = true } ]
                        Obligations = [ { Id = sid<ObligationTag> "op-missing"; OriginTransactionId = sid<TransactionTag> "op-origin"
                                          CallNodeId = sid<GraphNodeTag> "op-call"; ParameterId = target.Parameters.Head.Id; Status = ObligationStatus.Open } ] }

    let private request byName operations =
        let op = if byName then "simulateOperationsByName" else "simulateOperations"
        $"""{{"id":"operations","op":"{op}","operations":[{String.concat "," operations}]}}"""
    let private mutate (line: string) change =
        let node = JsonNode.Parse(line).AsObject()
        change node
        node.ToJsonString()
    let private successes byName =
        let target exact symbolic = if byName then symbolic else exact
        let operation kind fields = $"""{{"kind":"{kind}",{fields}}}"""
        let rename kind exact symbolic = operation kind (target exact symbolic + ",\"displayName\":\"Renamed\"")
        [ rename "renameFunction" "\"functionId\":\"zig-twice\"" "\"function\":\"twice\""
          rename "renameType" "\"typeId\":\"zig-Point\"" "\"type\":\"Point\""
          rename "renameField" "\"fieldId\":\"zig-point-x\"" "\"type\":\"Point\",\"field\":\"x\""
          rename "renameVariant" "\"variantId\":\"zig-circle\"" "\"type\":\"Shape\",\"variant\":\"circle\""
          rename "renameParameter" "\"parameterId\":\"zig-twice-n\"" "\"function\":\"twice\",\"parameter\":\"n\""
          rename "renameEffect" "\"effectId\":\"zig-io\"" "\"effect\":\"io\""
          rename "renameCapability" "\"capabilityId\":\"zig-IO\"" "\"capability\":\"IO\""
          rename "renameCapabilityOperation" "\"operationId\":\"zig-read\"" "\"capability\":\"IO\",\"operation\":\"read\""
          rename "renameCapabilityParameter" "\"parameterId\":\"zig-read-p\"" "\"capability\":\"IO\",\"operation\":\"read\",\"parameter\":\"p\""
          operation "setMayWait" (target "\"operationId\":\"zig-read\"" "\"capability\":\"IO\",\"operation\":\"read\"" + "," + "\"mayWait\":true")
          operation "setTaskBound" (target "\"capabilityId\":\"zig-IO\"" "\"capability\":\"IO\"" + "," + "\"taskBound\":true")
          operation "setMustNotWait" (target "\"functionId\":\"zig-twice\"" "\"function\":\"twice\"" + "," + "\"mustNotWait\":true")
          operation "setExported" (target "\"functionId\":\"zig-twice\"" "\"function\":\"twice\"" + "," + "\"exported\":false")
          operation "addFunctionParameter" (target "\"functionId\":\"zig-twice\"" "\"function\":\"twice\"" + "," + "\"displayName\":\"extra\",\"type\":" + integer + ",\"callSites\":[{\"kind\":\"leaveMissing\",\"callNodeId\":\"op-call\"}]")
          operation "addProductField" (if byName then $""" "type":"Point","displayName":"z","fieldType":{integer},"constructions":[] """ else $""" "typeId":"zig-Point","displayName":"z","type":{integer},"constructions":[] """)
          operation "addCapabilityOperation" (target "\"capabilityId\":\"zig-IO\"" "\"capability\":\"IO\"" + "," + "\"displayName\":\"extra\",\"parameters\":[],\"resultType\":" + integer + "," + (target "\"effectId\":\"zig-io\"" "\"effect\":\"io\"") + ",\"implementations\":[]")
          operation "addUnionVariant" (target "\"typeId\":\"zig-Shape\"" "\"type\":\"Shape\"" + "," + "\"displayName\":\"extra\",\"fields\":[{\"displayName\":\"value\",\"type\":" + integer + "}],\"arms\":[]")
          operation "removeProductField" (target "\"fieldId\":\"zig-point-x\"" "\"type\":\"Point\",\"field\":\"x\"")
          operation "removeFunctionParameter" (target "\"parameterId\":\"zig-twice-n\"" "\"function\":\"twice\",\"parameter\":\"n\"")
          operation "removeUnionVariant" (target "\"variantId\":\"zig-circle\"" "\"type\":\"Shape\",\"variant\":\"circle\"")
          operation "deleteFunction" (target "\"functionId\":\"op-spare\"" "\"function\":\"spare\"")
          operation "deleteType" (target "\"typeId\":\"op-spare-type\"" "\"type\":\"SpareType\"")
          operation "deleteEffect" (target "\"effectId\":\"op-spare-effect\"" "\"effect\":\"spareEffect\"")
          operation "deleteCapability" (target "\"capabilityId\":\"zig-IO\"" "\"capability\":\"IO\"")
          operation "deleteCapabilityOperation" (target "\"operationId\":\"zig-read\"" "\"capability\":\"IO\",\"operation\":\"read\"")
          operation "resolveMissingArgument" """ "obligationId":"op-missing","valueNodeId":"op-value" """
          operation "addRequiredEffect" (target "\"functionId\":\"zig-twice\",\"effectId\":\"zig-io\"" "\"function\":\"twice\",\"effect\":\"io\"")
          operation "removeRequiredEffect" (target "\"functionId\":\"zig-twice\",\"effectId\":\"zig-io\"" "\"function\":\"twice\",\"effect\":\"io\"") ]

    [<Fact; Trait("Suite", "Extended")>]
    let ``Every ordinary operation kind succeeds on both surfaces with strict seeded reviews`` () =
        withDirectory (fun dir ->
            let source = Path.Combine(dir, "operations.json")
            let fixture = snapshot ()
            // Give removeRequiredEffect a positive case while keeping the target's body pure.
            let fixture = { fixture with Functions = fixture.Functions |> List.map (fun fn -> if fn.DisplayName = "twice" then { fn with RequiredEffects = [ sid<EffectTag> "zig-io" ] } else if fn.DisplayName = "caller" then { fn with RequiredEffects = [ sid<EffectTag> "zig-io" ] } else fn) }
            File.WriteAllText(source, SnapshotCodec.serialize fixture |> unwrap)
            let lines =
                [ for byName in [ false; true ] do
                    for operation in successes byName do
                        // addRequiredEffect targets the otherwise unused pure spare function.
                        let operation = if operation.Contains("addRequiredEffect", StringComparison.Ordinal) then operation.Replace("zig-twice", "op-spare").Replace("\"twice\"", "\"spare\"") else operation
                        let line = request byName [ operation ]
                        yield line
                        yield mutate line (fun root -> root["detail"] <- JsonValue.Create "full")
                        yield mutate line (fun root -> root["detail"] <- JsonValue.Create "full"; root["structuredReview"] <- JsonValue.Create true) ]
            let expected, actual = seededPair "operations-all-kinds" source lines
            Assert.Equal<string list>(expected, actual)
            for reply in expected do
                use parsed = parseDocument reply
                Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), reply))

    let private recipeSnapshot () =
        let original = snapshot ()
        let point = original.Types |> List.find (fun t -> t.DisplayName = "Point")
        let shape = original.Types |> List.find (fun t -> t.DisplayName = "Shape")
        let pointFields = match point.Kind with SemanticTypeKind.Product fields -> fields | _ -> failwith "Point"
        let variants = match shape.Kind with SemanticTypeKind.Union variants -> variants | _ -> failwith "Shape"
        let template = original.Functions.Head
        let intT = TypeRef.Primitive PrimitiveType.Int64
        let parameter: SemanticParameter = { Id = sid<ParameterTag> "op-seed-param"; DisplayName = "seed"; Type = intT }
        let maker: SemanticFunction =
            { template with Id = sid<FunctionTag> "op-maker"; DisplayName = "maker"; Parameters = [ parameter ]; RequiredEffects = []; ResultType = TypeRef.Named(point.Id, [])
                            Body = { Nodes = [ { Id = sid<GraphNodeTag> "op-seed-value"; Kind = SemanticNodeKind.ParameterReference parameter.Id }
                                               { Id = sid<GraphNodeTag> "op-construct"; Kind = SemanticNodeKind.ProductConstruct(point.Id, [], pointFields |> List.map (fun field -> { TargetFieldId = field.Id; ValueNodeId = sid<GraphNodeTag> "op-seed-value" })) } ]
                                     ResultNodeId = sid<GraphNodeTag> "op-construct" } }
        let arms: UnionMatchArm list =
            variants |> List.mapi (fun index variant ->
                let value = sid<GraphNodeTag> $"op-arm-{index}"
                { VariantIds = [ variant.Id ]
                  FieldBindings = variant.Fields |> List.mapi (fun fieldIndex field -> { SourceFieldId = field.Id; Binding = { Id = sid<PatternBindingTag> $"op-binding-{index}-{fieldIndex}"; DisplayName = Some field.DisplayName; Type = field.Type } })
                  Body = { Nodes = [ { Id = value; Kind = SemanticNodeKind.Int64Literal(int64 index) } ]; ResultNodeId = value } })
        let matcher: SemanticFunction =
            { template with Id = sid<FunctionTag> "op-matcher"; DisplayName = "matcher"; Parameters = []; RequiredEffects = []; ResultType = intT
                            Body = { Nodes = [ { Id = sid<GraphNodeTag> "op-union-value"; Kind = SemanticNodeKind.UnionConstruct((variants |> List.find (fun variant -> variant.DisplayName = "dot")).Id, [], []) }
                                               { Id = sid<GraphNodeTag> "op-match"; Kind = SemanticNodeKind.MatchUnion(sid<GraphNodeTag> "op-union-value", arms) } ]
                                     ResultNodeId = sid<GraphNodeTag> "op-match" } }
        { original with Functions = original.Functions @ [ maker; matcher ] }

    [<Fact; Trait("Suite", "Extended")>]
    let ``Operation recipes use construction parameters and newly allocated arm fields`` () =
        withDirectory (fun dir ->
            let source = Path.Combine(dir, "plans.json")
            File.WriteAllText(source, SnapshotCodec.serialize (recipeSnapshot ()) |> unwrap)
            let productExact = """{"kind":"addProductField","typeId":"zig-Point","displayName":"z","type":{"kind":"primitive","primitive":"int64"},"constructions":[{"constructNodeId":"op-construct","value":{"kind":"parameter","parameterId":"op-seed-param"}}]}"""
            let productNamed = """{"kind":"addProductField","type":"Point","displayName":"z","fieldType":{"kind":"primitive","primitive":"int64"},"constructions":[{"constructNodeId":"op-construct","value":{"kind":"parameter","name":"seed"}}]}"""
            let unionExact = """{"kind":"addUnionVariant","typeId":"zig-Shape","displayName":"New","fields":[{"displayName":"value","type":{"kind":"primitive","primitive":"int64"}}],"arms":[{"matchNodeId":"op-match","body":{"kind":"patternReference","slot":"field","field":"value","scopeDepth":0}}]}"""
            let unionNamed = """{"kind":"addUnionVariant","type":"Shape","displayName":"New","fields":[{"displayName":"value","type":{"kind":"primitive","primitive":"int64"}}],"arms":[{"matchNodeId":"op-match","body":{"kind":"patternReference","slot":"field","type":"Shape","variant":"New","field":"value","scopeDepth":0}}]}"""
            let lines =
                [ for byName, operation in [ false, productExact; true, productNamed; false, unionExact; true, unionNamed ] do
                    let line = request byName [ operation ]
                    yield line
                    yield mutate line (fun root -> root["detail"] <- JsonValue.Create "full"; root["structuredReview"] <- JsonValue.Create true) ]
            let firstField = """{"kind":"addProductField","typeId":"zig-Point","displayName":"first","type":{"kind":"primitive","primitive":"int64"},"constructions":[{"constructNodeId":"op-construct","valueNodeId":"op-seed-value"}]}"""
            let secondField = firstField.Replace("first", "second")
            let lines = lines @ [ request false [ firstField; secondField ] ]
            let expected, actual = seededPair "operations-plan-recipes" source lines
            Assert.Equal<string list>(expected, actual)
            for reply in expected do
                use parsed = parseDocument reply
                Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), reply))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Operation failures preserve decoding order atomicity and allocator consumption`` () =
        withDirectory (fun dir ->
            let source = Path.Combine(dir, "failures.json")
            File.WriteAllText(source, SnapshotCodec.serialize (snapshot ()) |> unwrap)
            let valid = request false [ """{"kind":"renameFunction","functionId":"zig-twice","displayName":"Renamed"}""" ]
            let malformed =
                [ for byName in [ false; true ] do
                    yield request byName []
                    yield request byName [ "{}" ]
                    yield request byName [ "null" ]
                    yield request byName [ """{"kind":"unknown"}""" ]
                    for operation in successes byName do
                        let line = request byName [ operation ]
                        yield mutate line (fun root -> root["operations"].AsArray().[0].AsObject().Add("unknown", JsonValue.Create true))
                        yield request byName [ operation.Replace("\"kind\":", "\"kind\":\"duplicate\",\"kind\":", StringComparison.Ordinal) ]
                    yield mutate (request byName []) (fun root -> root["operations"] <- JsonNode.Parse("{}"))
                  yield request true [ """{"kind":"renameFunction","function":{"name":"twice"},"displayName":"x"}""" ]
                  yield mutate valid (fun root ->
                      let operation = root["operations"].AsArray().[0].AsObject()
                      operation.Add("𐀀", JsonValue.Create true)
                      operation.Add("", JsonValue.Create true))
                  yield request false [ """{"kind":"renameFunction","functionId":"missing","displayName":"x"}""" ]
                  yield request false [ """{"kind":"renameFunction","functionId":"zig-twice","displayName":" "}""" ]
                  yield request false [ """{"kind":"renameFunction","functionId":"zig-twice","displayName":"spare"}""" ]
                  yield request false [ """{"kind":"setExported","functionId":"zig-twice","typeId":"zig-Point","exported":true}""" ]
                  yield request false [ """{"kind":"setExported","exported":true}""" ]
                  yield request false [ """{"kind":"setExported","functionId":"missing","exported":"bad"}""" ]
                  yield request false [ """{"kind":"setMayWait","operationId":"zig-read","mayWait":false}""" ]
                  yield request false [ """{"kind":"addProductField","typeId":"zig-Point","displayName":"x","type":{"kind":"primitive","primitive":"int64"},"constructions":[]}""" ]
                  yield request false [ """{"kind":"addFunctionParameter","functionId":"zig-twice","displayName":"new","type":{"kind":"named","typeId":"missing"},"callSites":[{"kind":"leaveMissing","callNodeId":"op-call"}]}""" ]
                  yield request false [ """{"kind":"addUnionVariant","typeId":"zig-Shape","displayName":"New","fields":[{"displayName":"x","type":{"kind":"primitive","primitive":"int64"}}],"arms":[{"matchNodeId":"missing","body":{"kind":"unitLiteral"}}]}""" ]
                  yield request false [ """{"kind":"addUnionVariant","typeId":"zig-Point","displayName":"New","fields":[],"arms":[]}""" ]
                  yield request false [ """{"kind":"renameFunction","functionId":"zig-twice","displayName":"temporary"}"""; """{"kind":"renameFunction","functionId":"zig-twice","displayName":"twice"}""" ]
                  yield request true [ """{"kind":"renameFunction","function":"twice","displayName":"temporary"}"""; """{"kind":"renameFunction","function":"twice","displayName":"twice"}""" ]
                  yield request false [ """{"kind":"renameFunction","functionId":"zig-twice","displayName":"temporary"}"""; """{"kind":"deleteType","typeId":"missing"}""" ]
                  yield request false [ """{"kind":"setExported","functionId":"zig-twice","exported":false}"""; """{"kind":"setExported","functionId":"zig-twice","exported":true}""" ]
                  yield request false [ """{"kind":"setMustNotWait","functionId":"zig-twice","mustNotWait":true}"""; """{"kind":"setMustNotWait","functionId":"zig-twice","mustNotWait":false}""" ]
                  yield request false [ """{"kind":"addRequiredEffect","functionId":"zig-twice","effectId":"zig-io"}"""; """{"kind":"removeRequiredEffect","functionId":"zig-twice","effectId":"zig-io"}""" ]
                  yield mutate valid (fun root -> root["expectedSnapshotId"] <- JsonValue.Create "stale") ]
            let lines = malformed |> List.collect (fun invalid -> [ invalid; valid ])
            let expected, actual = seededPair "operations-failures" source lines
            Assert.Equal<string list>(expected, actual)
            for index, reply in expected |> List.indexed do
                use parsed = parseDocument reply
                Assert.Equal(index % 2 = 1, parsed.RootElement.GetProperty("ok").GetBoolean()))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Operation proposals persist review page view stale commit and cross host reads`` () =
        withDirectory (fun dir ->
            for records, resolved in [ false, false; true, false; false, true; true, true ] do
                let case = Path.Combine(dir, string records + "-" + string resolved)
                let input = Path.Combine(case, "input")
                Directory.CreateDirectory input |> ignore
                let source = Path.Combine(input, if records then "snapshot.nyrec" else "snapshot.json")
                let fixture = if records then recordFixture "operations-persistence" (recipeSnapshot ()) else recipeSnapshot ()
                let dependency = Snapshot.empty (sid<SnapshotTag> "018bcfe5-6700-7000-8000-000000000099")
                let json = if resolved then SnapshotCodec.serializeResolved dependency fixture |> unwrap else SnapshotCodec.serialize fixture |> unwrap
                File.WriteAllText(source, if records then RecordCodec.ofJson json else json)
                if resolved then
                    let manifest = memberOf dependency (Some("snapshotJson", SnapshotCodec.serialize dependency |> unwrap)) "dependency.json"
                    File.WriteAllText(source + ".dependency.json", manifest.ToJsonString())
                for suffix in [ ".backends.json"; ".previous"; ".dependency.json.previous" ] do File.WriteAllText(source + suffix, "sentinel")
                let succeed (reply: string) =
                    use parsed = parseDocument reply
                    Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), reply)
                let proposal reply =
                    succeed reply
                    use parsed = parseDocument reply
                    parsed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                let operation op id = $"""{{"id":"lifecycle","op":"{op}","proposalId":{q id}}}"""
                let commit id = $"""{{"id":"commit","op":"commit","proposalId":{q id},"allowIncomplete":true}}"""
                let fsharp, zig =
                    seededWritablePair "operations-lifecycle" source case (fun _ send ->
                        let change name = request true [ $"""{{"kind":"renameFunction","function":"twice","displayName":{q name}}}""" ]
                        let first = send (change "changed") |> proposal
                        let stale = send (change "stale") |> proposal
                        let inspect id =
                            send (operation "viewProposal" id) |> succeed
                            send $"""{{"id":"page","op":"readProposal","proposalId":{q id},"path":"/review/diff","offset":0,"limit":3,"structuredReview":true}}""" |> succeed
                            send """{"id":"list","op":"proposals"}""" |> succeed
                        inspect first
                        Assert.Contains("BlockingObligations", send (operation "commit" first))
                        send (commit first) |> succeed
                        inspect stale
                        Assert.Contains("StaleBaseSnapshot", send (commit stale))
                        send (operation "reject" stale) |> succeed
                        Assert.Contains("ProposalNotFound", send (operation "readProposal" stale))
                        let deleted = send (request true [ """{"kind":"deleteFunction","function":"spare"}""" ]) |> proposal
                        inspect deleted
                        send (commit deleted) |> succeed
                        let maker = fixture.Functions |> List.find (fun fn -> fn.DisplayName = "maker")
                        let constructor = maker.Body.ResultNodeId |> StableId.value
                        let field = $"""{{"kind":"addProductField","type":"Point","displayName":"z","fieldType":{integer},"constructions":[{{"constructNodeId":{q constructor},"value":{{"kind":"parameter","name":"seed"}}}}]}}"""
                        let product = send (request true [ field ]) |> proposal
                        inspect product
                        send (commit product) |> succeed
                        let matcher = fixture.Functions |> List.find (fun fn -> fn.DisplayName = "matcher")
                        let variant = $"""{{"kind":"addUnionVariant","type":"Shape","displayName":"New","fields":[],"arms":[{{"matchNodeId":{q (StableId.value matcher.Body.ResultNodeId)},"body":{{"kind":"int64Literal","value":"4"}}}}]}}"""
                        let union = send (request true [ variant ]) |> proposal
                        inspect union
                        send (commit union) |> succeed
                        // The semantic snapshot keeps a newly created function last, even when
                        // its fresh UUID sorts before existing IDs; a later operation keeps it so.
                        let createMatcher = """{"id":"create","op":"simulateCreateFunctionByName","displayName":"newMatch","parameters":[],"resultType":{"kind":"primitive","primitive":"int64"},"requiredEffects":[],"body":{"kind":"matchUnion","type":"Shape","input":{"kind":"unionConstruct","type":"Shape","variant":"dot","fields":[]},"arms":[{"variant":"circle","body":{"kind":"int64Literal","value":"1"}},{"variant":"dot","body":{"kind":"int64Literal","value":"1"}},{"variant":"square","body":{"kind":"int64Literal","value":"1"}}],"otherwise":{"kind":"int64Literal","value":"1"}}}"""
                        let created = send createMatcher |> proposal
                        send (commit created) |> succeed
                        send """{"id":"search","op":"search","text":"match"}""" |> succeed
                        let renamed = send (request true [ """{"kind":"renameFunction","function":"newMatch","displayName":"newMatchRenamed"}""" ]) |> proposal
                        send (commit renamed) |> succeed
                        send """{"id":"search","op":"search","text":"match"}""" |> succeed
                        let readMatch = send """{"id":"read","op":"readFunction","function":"newMatchRenamed","detail":"full"}"""
                        succeed readMatch
                        use matchDocument = parseDocument readMatch
                        let secondMatch = matchDocument.RootElement.GetProperty("result").GetProperty("function").GetProperty("body").GetProperty("resultNodeId").GetString()
                        let duplicatePlans = mutate (request true [ variant ]) (fun root ->
                            let operation = root["operations"].AsArray().[0]
                            let arms = operation["arms"].AsArray()
                            let first = arms.[0].DeepClone()
                            let second = first.DeepClone()
                            second["matchNodeId"] <- JsonValue.Create secondMatch
                            arms.Clear()
                            for plan in [ first; second; second.DeepClone(); first.DeepClone() ] do arms.Add plan)
                        Assert.Contains("UnionMatchArmPlanMismatch", send duplicatePlans)
                        let incomplete = request true [ """{"kind":"addUnionVariant","type":"Shape","displayName":"Later","fields":[],"arms":[]}""" ]
                        Assert.Contains("UnionMatchArmPlanMismatch", send incomplete)
                        let removedVariant = send (request true [ """{"kind":"removeUnionVariant","type":"Shape","variant":"circle"}""" ]) |> proposal
                        send (commit removedVariant) |> succeed
                        let createMaker = """{"id":"create","op":"simulateCreateFunctionByName","displayName":"newMaker","parameters":[],"resultType":{"kind":"named","name":"Point"},"requiredEffects":[],"body":{"kind":"productConstruct","type":"Point","fields":[{"field":"x","value":{"kind":"int64Literal","value":"1"}},{"field":"y","value":{"kind":"int64Literal","value":"1"}},{"field":"z","value":{"kind":"int64Literal","value":"1"}}]}}"""
                        send (commit (send createMaker |> proposal)) |> succeed
                        let removedField = send (request true [ """{"kind":"removeProductField","type":"Point","field":"z"}""" ]) |> proposal
                        send (commit removedField) |> succeed
                        let createCaller = """{"id":"create","op":"simulateCreateFunctionByName","displayName":"newCaller","parameters":[],"resultType":{"kind":"primitive","primitive":"bool"},"requiredEffects":[],"body":{"kind":"directCall","function":"changed","arguments":[{"parameter":"n","value":{"kind":"int64Literal","value":"2"}}]}}"""
                        send (commit (send createCaller |> proposal)) |> succeed
                        let readNewCaller = send """{"id":"read","op":"readFunction","function":"newCaller","detail":"full"}"""
                        succeed readNewCaller
                        use readDocument = parseDocument readNewCaller
                        let graph = readDocument.RootElement.GetProperty("result").GetProperty("function").GetProperty("body")
                        let newCall = graph.GetProperty("resultNodeId").GetString()
                        let newValue = graph.GetProperty("nodes").EnumerateArray() |> Seq.find (fun node -> node.GetProperty("kind").GetProperty("kind").GetString() = "int64Literal") |> fun node -> node.GetProperty("id").GetString()
                        let caller = fixture.Functions |> List.find (fun fn -> fn.DisplayName = "caller")
                        let call = StableId.value caller.Body.ResultNodeId
                        let oldMissing = fixture.Obligations.Head.Id |> StableId.value
                        let value = caller.Body.Nodes |> List.find (fun node -> match node.Kind with SemanticNodeKind.Int64Literal _ -> true | _ -> false) |> fun node -> StableId.value node.Id
                        let boundParameter = $"""{{"kind":"addFunctionParameter","function":"changed","displayName":"dropExtra","type":{integer},"callSites":[{{"kind":"bind","callNodeId":{q call},"valueNodeId":{q value}}},{{"kind":"bind","callNodeId":{q newCall},"valueNodeId":{q newValue}}}]}}"""
                        send (commit (send (request true [ boundParameter ]) |> proposal)) |> succeed
                        let removedParameter = send (request true [ """{"kind":"removeFunctionParameter","function":"changed","parameter":"dropExtra"}""" ]) |> proposal
                        send (commit removedParameter) |> succeed
                        let addParameter = $"""{{"kind":"addFunctionParameter","function":"changed","displayName":"retiredExtra","type":{integer},"callSites":[{{"kind":"leaveMissing","callNodeId":{q call}}},{{"kind":"leaveMissing","callNodeId":{q newCall}}}]}}"""
                        let addedReply = send (request true [ addParameter ])
                        let added = proposal addedReply
                        use addedDocument = parseDocument addedReply
                        let createdObligations = addedDocument.RootElement.GetProperty("result").GetProperty("review").GetProperty("diff").GetProperty("obligationsCreated").EnumerateArray() |> Seq.map (fun obligation -> obligation.GetProperty("obligationId").GetString(), (if obligation.GetProperty("callNodeId").GetString() = call then value else newValue)) |> Seq.toList
                        send (commit added) |> succeed
                        let resolutions = (oldMissing, value) :: createdObligations |> List.map (fun (obligation, value) -> $"""{{"kind":"resolveMissingArgument","obligationId":{q obligation},"valueNodeId":{q value}}}""")
                        let resolved = send (request false resolutions) |> proposal
                        send (commit resolved) |> succeed
                        let removed = send (request true [ """{"kind":"deleteFunction","function":"caller"}""" ]) |> proposal
                        send (commit removed) |> succeed
                        send """{"id":"d","op":"declarations"}""" |> succeed)
                for output in [ fsharp; zig ] do
                    let path = Path.Combine(output, Path.GetFileName source)
                    let lines = [ """{"id":"s","op":"status"}"""; """{"id":"d","op":"declarations"}"""; """{"id":"f","op":"readFunction","function":"maker","detail":"full"}""" ]
                    let read zigHost =
                        let code, errors, replies = run zigHost [ "open"; "--snapshot"; path; "--read-only" ] lines
                        Assert.True((code = 0), errors)
                        replies |> List.iter succeed
                        replies |> List.map comparable
                    Assert.Equal<string list>(read false, read true))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Generic shared union arms preserve enclosing frames and recipe failure allocations`` () =
        withDirectory (fun dir ->
            let fixture = snapshot ()
            let maybe = fixture.Types |> List.find (fun t -> t.DisplayName = "Maybe")
            let variants = match maybe.Kind with SemanticTypeKind.Union variants -> variants | _ -> failwith "Maybe"
            let empty = variants |> List.find (fun variant -> variant.DisplayName = "nothing")
            let intT = TypeRef.Primitive PrimitiveType.Int64
            let bound: PatternBinding = { Id = sid<PatternBindingTag> "op-outer"; DisplayName = Some "outer"; Type = intT }
            let shared: UnionMatchArm =
                { VariantIds = variants |> List.map (fun variant -> variant.Id); FieldBindings = []
                  Body = { Nodes = [ { Id = sid<GraphNodeTag> "op-shared-result"; Kind = SemanticNodeKind.Int64Literal 3L } ]; ResultNodeId = sid<GraphNodeTag> "op-shared-result" } }
            let inner: ValueGraph =
                { Nodes = [ { Id = sid<GraphNodeTag> "op-generic-input"; Kind = SemanticNodeKind.UnionConstruct(empty.Id, [ intT ], []) }
                            { Id = sid<GraphNodeTag> "op-generic-match"; Kind = SemanticNodeKind.MatchUnion(sid<GraphNodeTag> "op-generic-input", [ shared ]) } ]
                  ResultNodeId = sid<GraphNodeTag> "op-generic-match" }
            let functionEntity: SemanticFunction =
                { fixture.Functions.Head with Id = sid<FunctionTag> "op-generic-matcher"; DisplayName = "genericMatcher"; Parameters = []; ResultType = intT; RequiredEffects = []
                                              Body = { Nodes = [ { Id = sid<GraphNodeTag> "op-outer-value"; Kind = SemanticNodeKind.Int64Literal 2L }
                                                                 { Id = sid<GraphNodeTag> "op-outer-bind"; Kind = SemanticNodeKind.Bind(sid<GraphNodeTag> "op-outer-value", { ValueBinding = bound; Body = inner }) } ]
                                                       ResultNodeId = sid<GraphNodeTag> "op-outer-bind" } }
            let source = Path.Combine(dir, "generic-plans.json")
            File.WriteAllText(source, SnapshotCodec.serialize { fixture with Functions = fixture.Functions @ [ functionEntity ] } |> unwrap)
            let operation = """{"kind":"addUnionVariant","type":"Maybe","displayName":"More","fields":[{"displayName":"value","type":{"kind":"typeParameter","name":"T"}}],"arms":[{"matchNodeId":"op-generic-match","body":{"kind":"int64WrappingAdd","left":{"kind":"patternReference","slot":"field","type":"Maybe","variant":"More","field":"value","scopeDepth":0},"right":{"kind":"patternReference","slot":"value","scopeDepth":1}}}]}"""
            let valid = request true [ operation ]
            let malformed =
                [ operation.Replace("op-generic-match", "missing")
                  operation.Replace("\"scopeDepth\":1", "\"scopeDepth\":-1")
                  operation.Replace("\"name\":\"T\"", "\"name\":\"Undeclared\"")
                  operation.Replace("\"int64WrappingAdd\"", "\"stringConcat\"") ]
                |> List.map (fun op -> request true [ op ])
            let duplicate = mutate valid (fun root ->
                let operation = root["operations"].AsArray().[0]
                let arms = operation["arms"].AsArray()
                arms.Add(arms.[0].DeepClone()))
            let missing = mutate valid (fun root ->
                let operation = root["operations"].AsArray().[0]
                operation["arms"] <- JsonNode.Parse("[]"))
            let stale = mutate valid (fun root -> root["expectedSnapshotId"] <- JsonValue.Create "stale")
            let lines = valid :: ([ duplicate; missing; stale ] @ malformed |> List.collect (fun invalid -> [ invalid; valid ]))
            let expected, actual = seededPair "operations-generic-plans" source lines
            Assert.Equal<string list>(expected, actual)
            for index, reply in expected |> List.indexed do
                use parsed = parseDocument reply
                Assert.True(((index = 0 || index % 2 = 0) = parsed.RootElement.GetProperty("ok").GetBoolean()), reply))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Operation ownership guards reject imported edits and hidden references`` () =
        withDirectory (fun dir ->
            let local = snapshot ()
            let imported = { local.Functions.Head with Id = sid<FunctionTag> "dep-function"; DisplayName = "imported"; Parameters = []; Body = { Nodes = [ { Id = sid<GraphNodeTag> "dep-node"; Kind = SemanticNodeKind.BoolLiteral true } ]; ResultNodeId = sid<GraphNodeTag> "dep-node" } }
            let visible: SemanticType = { Id = sid<TypeTag> "dep-visible"; DisplayName = "ImportedType"; Exported = true; TypeParameters = []; Kind = SemanticTypeKind.Product [] }
            let hidden: SemanticType = { visible with Id = sid<TypeTag> "dep-hidden"; DisplayName = "HiddenType"; Exported = false }
            let dependency = { Snapshot.empty (sid<SnapshotTag> "dep-root") with Types = [ visible; hidden ]; Functions = [ imported ] }
            let source = Path.Combine(dir, "resolved.json")
            File.WriteAllText(source, SnapshotCodec.serializeResolved dependency local |> unwrap)
            let manifest = memberOf dependency (Some("snapshotJson", SnapshotCodec.serialize dependency |> unwrap)) "dependency.json"
            File.WriteAllText(source + ".dependency.json", manifest.ToJsonString())
            let valid = request true [ """{"kind":"renameFunction","function":"twice","displayName":"changed"}""" ]
            let invalids =
                [ request false [ """{"kind":"renameFunction","functionId":"dep-function","displayName":"changed"}""" ]
                  request true [ """{"kind":"renameFunction","function":"imported","displayName":"changed"}""" ]
                  request false [ """{"kind":"deleteType","typeId":"dep-visible"}""" ]
                  request false [ """{"kind":"addFunctionParameter","functionId":"zig-twice","displayName":"hidden","type":{"kind":"named","typeId":"dep-hidden"},"callSites":[{"kind":"leaveMissing","callNodeId":"op-call"}]}""" ]
                  request true [ """{"kind":"addFunctionParameter","function":"twice","displayName":"hidden","type":{"kind":"named","name":"HiddenType"},"callSites":[{"kind":"leaveMissing","callNodeId":"op-call"}]}""" ]
                  request true [ """{"kind":"renameType","type":{"name":"ImportedType","ownership":"imported"},"displayName":"changed"}""" ] ]
            let lines = invalids |> List.collect (fun line -> [ line; valid ])
            let expected, actual = seededPair "operations-dependency" source lines
            Assert.Equal<string list>(expected, actual)
            for index, reply in expected |> List.indexed do
                use parsed = parseDocument reply
                Assert.Equal(index % 2 = 1, parsed.RootElement.GetProperty("ok").GetBoolean()))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Unseeded operation reviews sort unordered new fields and preserve parameter order`` () =
        withDirectory (fun dir ->
            let source = Path.Combine(dir, "unordered.json")
            File.WriteAllText(source, SnapshotCodec.serialize (snapshot ()) |> unwrap)
            let fields = [ "third"; "first"; "second" ] |> List.map (fun name -> $"""{{"displayName":{q name},"type":{integer}}}""") |> String.concat ","
            let variant = $"""{{"kind":"addUnionVariant","type":"Shape","displayName":"New","fields":[{fields}],"arms":[]}}"""
            let capability = $"""{{"kind":"addCapabilityOperation","capability":"IO","displayName":"new","parameters":[{fields}],"resultType":{integer},"effect":"io","implementations":[]}}"""
            let lines = [ request true [ variant ]; request true [ capability ] ] |> List.map (fun line -> mutate line (fun root -> root["detail"] <- JsonValue.Create "full"; root["structuredReview"] <- JsonValue.Create true))
            for zig in [ false; true ] do
                let code, errors, replies = run zig [ "open"; "--snapshot"; source; "--read-only" ] lines
                Assert.True((code = 0), errors)
                use variantReply = parseDocument replies.[0]
                Assert.True(variantReply.RootElement.GetProperty("ok").GetBoolean(), replies.[0])
                let added = variantReply.RootElement.GetProperty("result").GetProperty("review").GetProperty("diff").GetProperty("unionVariantsAdded").[0].GetProperty("variant").GetProperty("fields")
                let ids = added.EnumerateArray() |> Seq.map (fun field -> field.GetProperty("id").GetString()) |> Seq.toList
                Assert.Equal<string list>(List.sort ids, ids)
                use capabilityReply = parseDocument replies.[1]
                Assert.True(capabilityReply.RootElement.GetProperty("ok").GetBoolean(), replies.[1])
                let parameters = capabilityReply.RootElement.GetProperty("result").GetProperty("review").GetProperty("diff").GetProperty("capabilityOperationsAdded").[0].GetProperty("operation").GetProperty("parameters")
                let names = parameters.EnumerateArray() |> Seq.map (fun parameter -> parameter.GetProperty("displayName").GetString()) |> Seq.toList
                Assert.Equal<string list>([ "third"; "first"; "second" ], names))

    [<Fact; Trait("Suite", "Extended")>]
    let ``External changes invalidate operation commits without overwriting changed files`` () =
        withDirectory (fun dir ->
            let input = Path.Combine(dir, "input")
            Directory.CreateDirectory input |> ignore
            let source = Path.Combine(input, "external.json")
            File.WriteAllText(source, SnapshotCodec.serialize (snapshot ()) |> unwrap)
            seededWritablePair "operations-external" source dir (fun path send ->
                let line = request true [ """{"kind":"renameType","type":"Point","displayName":"changed"}""" ]
                let reply = send line
                use parsed = parseDocument reply
                Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), reply)
                let proposal = parsed.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
                File.AppendAllText(path, "\n")
                let commit = $"""{{"id":"commit","op":"commit","proposalId":{q proposal},"allowIncomplete":true}}"""
                Assert.Contains("ExternalModification", send commit)
                Assert.Contains("SessionInvalidated", send commit)
                Assert.Contains("SessionInvalidated", send line)) |> ignore)

    [<Fact; Trait("Suite", "Extended")>]
    let ``Ordinary repair plans and original name resolution compose atomically`` () =
        withDirectory (fun dir ->
            let fixture = snapshot ()
            let template = fixture.Functions.Head
            let intT = TypeRef.Primitive PrimitiveType.Int64
            let parameter: SemanticParameter = { Id = sid<ParameterTag> "op-read-parameter"; DisplayName = "value"; Type = intT }
            let read: SemanticFunction =
                { template with Id = sid<FunctionTag> "op-read"; DisplayName = "readImpl"; Parameters = [ parameter ]; ResultType = intT; RequiredEffects = []
                                Body = { Nodes = [ { Id = sid<GraphNodeTag> "op-read-value"; Kind = SemanticNodeKind.ParameterReference parameter.Id } ]; ResultNodeId = sid<GraphNodeTag> "op-read-value" } }
            let release: SemanticFunction =
                { read with Id = sid<FunctionTag> "op-release"; DisplayName = "releaseImpl"; Parameters = [ { parameter with Id = sid<ParameterTag> "op-release-parameter" } ]; ResultType = TypeRef.Primitive PrimitiveType.Unit
                            Body = { Nodes = [ { Id = sid<GraphNodeTag> "op-release-value"; Kind = SemanticNodeKind.UnitLiteral } ]; ResultNodeId = sid<GraphNodeTag> "op-release-value" } }
            let factory: SemanticFunction =
                { template with Id = sid<FunctionTag> "op-cap-factory"; DisplayName = "factory"; Parameters = []; ResultType = TypeRef.Capability(sid<CapabilityTag> "zig-IO"); RequiredEffects = []
                                Body = { Nodes = [ { Id = sid<GraphNodeTag> "op-cap-construct"; Kind = SemanticNodeKind.CapabilityConstruct(sid<CapabilityTag> "zig-IO", [ sid<CapabilityOperationTag> "zig-read", read.Id; sid<CapabilityOperationTag> "zig-release", release.Id ]) } ]; ResultNodeId = sid<GraphNodeTag> "op-cap-construct" } }
            let a: SemanticFunction = { template with Id = sid<FunctionTag> "op-a"; DisplayName = "A"; Parameters = []; Body = { Nodes = [ { Id = sid<GraphNodeTag> "op-a-call"; Kind = SemanticNodeKind.DirectCall(sid<FunctionTag> "op-b", []) } ]; ResultNodeId = sid<GraphNodeTag> "op-a-call" } }
            let b: SemanticFunction = { a with Id = sid<FunctionTag> "op-b"; DisplayName = "B"; Body = { Nodes = [ { Id = sid<GraphNodeTag> "op-b-call"; Kind = SemanticNodeKind.DirectCall(a.Id, []) } ]; ResultNodeId = sid<GraphNodeTag> "op-b-call" } }
            let source = Path.Combine(dir, "ordinary-plans.json")
            File.WriteAllText(source, SnapshotCodec.serialize { fixture with Functions = fixture.Functions @ [ read; release; factory; a; b ] } |> unwrap)
            let exact = $"""{{"kind":"addCapabilityOperation","capabilityId":"zig-IO","displayName":"extra","parameters":[{{"displayName":"value","type":{integer}}}],"resultType":{integer},"effectId":"zig-io","implementations":[{{"constructNodeId":"op-cap-construct","functionId":"op-read"}}]}}"""
            let named = $"""{{"kind":"addCapabilityOperation","capability":"IO","displayName":"extra","parameters":[{{"displayName":"value","type":{integer}}}],"resultType":{integer},"effect":"io","implementations":[{{"constructNodeId":"op-cap-construct","function":{{"id":"op-read","name":"readImpl","ownership":"local"}}}}]}}"""
            let bound = $"""{{"kind":"addFunctionParameter","functionId":"zig-twice","displayName":"extra","type":{integer},"callSites":[{{"kind":"bind","callNodeId":"op-call","valueNodeId":"op-value"}}]}}"""
            let successes =
                [ request false [ exact ]
                  request true [ named ]
                  request false [ bound ]
                  request true [ """{"kind":"renameFunction","function":"twice","displayName":"spare"}"""; """{"kind":"renameFunction","function":"spare","displayName":"twice"}""" ]
                  request false [ """{"kind":"deleteFunction","functionId":"op-a"}"""; """{"kind":"deleteFunction","functionId":"op-b"}""" ] ]
            let failures =
                [ request false [ exact.Replace("op-read", "missing") ]
                  request false [ exact.Replace("op-read", "op-spare") ]
                  request false [ bound.Replace("op-value", "missing") ]
                  request true [ named.Replace("\"ownership\":\"local\"", "\"ownership\":\"imported\"") ] ]
            let valid = successes.Head
            let lines = successes @ (failures |> List.collect (fun invalid -> [ invalid; valid ]))
            let expected, actual = seededPair "operations-ordinary-plans" source lines
            Assert.Equal<string list>(expected, actual)
            for index, reply in expected |> List.indexed do
                let shouldSucceed = index < successes.Length || (index - successes.Length) % 2 = 1
                use parsed = parseDocument reply
                Assert.True((shouldSucceed = parsed.RootElement.GetProperty("ok").GetBoolean()), reply))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Product recipe plans reject bad scopes completion and plan sets without losing IDs`` () =
        withDirectory (fun dir ->
            let source = Path.Combine(dir, "product-errors.json")
            File.WriteAllText(source, SnapshotCodec.serialize (recipeSnapshot ()) |> unwrap)
            let operation = """{"kind":"addProductField","type":"Point","displayName":"z","fieldType":{"kind":"primitive","primitive":"int64"},"constructions":[{"constructNodeId":"op-construct","value":{"kind":"parameter","name":"seed"}}]}"""
            let valid = request true [ operation ]
            let changePlan change = mutate valid (fun root ->
                let operation = root["operations"].AsArray().[0]
                change operation)
            let duplicate = changePlan (fun operation ->
                let plans = operation["constructions"].AsArray()
                plans.Add(plans.[0].DeepClone()))
            let missing = changePlan (fun operation -> operation["constructions"] <- JsonNode.Parse("[]"))
            let badType = changePlan (fun operation ->
                let plan = operation["constructions"].AsArray().[0]
                plan["value"] <- JsonNode.Parse("""{"kind":"boolLiteral","value":true}"""))
            let aborting = changePlan (fun operation ->
                let plan = operation["constructions"].AsArray().[0]
                plan["value"] <- JsonNode.Parse("""{"kind":"abort","error":{"kind":"stringLiteral","value":"stop"}}"""))
            let badScope = changePlan (fun operation ->
                let plan = operation["constructions"].AsArray().[0]
                plan["value"] <- JsonNode.Parse("""{"kind":"patternReference","slot":"value","scopeDepth":0}"""))
            let tooDeep = changePlan (fun operation ->
                let plan = operation["constructions"].AsArray().[0]
                plan["value"] <- JsonNode.Parse(nested 40))
            let tooWide = changePlan (fun operation ->
                let plan = operation["constructions"].AsArray().[0]
                plan["value"] <- JsonNode.Parse(wide 1100))
            let conflicts = changePlan (fun operation ->
                let plan = operation["constructions"].AsArray().[0]
                plan["valueNodeId"] <- JsonValue.Create "op-seed-value")
            let exactMissing = request false [ """{"kind":"addProductField","typeId":"zig-Point","displayName":"z","type":{"kind":"primitive","primitive":"int64"},"constructions":[{"constructNodeId":"op-construct","valueNodeId":"missing"}]}""" ]
            let invalids = [ duplicate; missing; badType; aborting; badScope; tooDeep; tooWide; conflicts; exactMissing; valid.Replace("op-construct", "missing"); valid.Replace("\"type\":\"Point\"", "\"type\":{\"name\":\"Point\"}") ]
            let lines = invalids |> List.collect (fun invalid -> [ invalid; valid ])
            let expected, actual = seededPair "operations-product-plan-errors" source lines
            Assert.Equal<string list>(expected, actual)
            for index, reply in expected |> List.indexed do
                use parsed = parseDocument reply
                Assert.True(((index % 2 = 1) = parsed.RootElement.GetProperty("ok").GetBoolean()), reply))
