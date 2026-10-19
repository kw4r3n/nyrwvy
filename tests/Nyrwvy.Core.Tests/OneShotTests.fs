namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// The new runtime handle follows the ordinary authoring, snapshot oracle and persistence contracts.
module OneShotTests =
    let private unwrap = function Ok x -> x | Error e -> failwithf "%A" e
    let private stringJson = """{"kind":"primitive","primitive":"string"}"""
    let private scopeJson = """{"kind":"builtinGeneric","generic":"taskScope","arguments":[]}"""
    let private cellJson = """{"kind":"builtinGeneric","generic":"oneShot","arguments":[{"kind":"primitive","primitive":"string"}]}"""
    let private boolJson = """{"kind":"primitive","primitive":"bool"}"""
    let private execute session op (arguments: string) =
        let fields = arguments.Trim().Substring(1, arguments.Trim().Length - 2)
        use doc = JsonDocument.Parse(AuthoringProtocol.executeLine session ($"""{{"id":"test","op":"{op}",{fields}}}"""))
        doc.RootElement.Clone()
    let private requireSuccess (response: JsonElement) =
        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString())
        response.GetProperty("result")
    let private create session name parameters result body =
        let response = execute session "simulateCreateFunctionByName" ($"""{{"exported":true,"displayName":"{name}","parameters":{parameters},"resultType":{result},"requiredEffects":[],"body":{body}}}""")
        let proposal = requireSuccess response
        Assert.Equal("semanticallyReady", proposal.GetProperty("review").GetProperty("readiness").GetProperty("kind").GetString())
        execute session "commit" ($"""{{"proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}""") |> requireSuccess |> ignore
    let private withFixture test =
        let directory = Path.Combine(Path.GetTempPath(), "nyrwvy-one-shot-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory directory |> ignore
        let path = Path.Combine(directory, "graph.json")
        try
            use session = AuthoringSession.createNew path |> unwrap
            create session "make" ($"""[{{"displayName":"scope","type":{scopeJson}}}]""") cellJson ($"""{{"kind":"oneShotCreate","scope":{{"kind":"parameter","name":"scope"}},"valueType":{stringJson}}}""")
            create session "finish" ($"""[{{"displayName":"cell","type":{cellJson}}},{{"displayName":"value","type":{stringJson}}}]""") boolJson """{"kind":"oneShotComplete","cell":{"kind":"parameter","name":"cell"},"value":{"kind":"parameter","name":"value"}}"""
            create session "take" ($"""[{{"displayName":"cell","type":{cellJson}}}]""") ($"""{{"kind":"builtinGeneric","generic":"option","arguments":[{stringJson}]}}""") """{"kind":"oneShotWait","cell":{"kind":"parameter","name":"cell"}}"""
            test session path
        finally
            Directory.Delete(directory, true)
    let private read path = File.ReadAllText path |> SnapshotCodec.deserialize |> unwrap

    [<Fact>]
    let ``one-shot definition round trips with explicit schema and stable IDs, not live cell state`` () =
        withFixture (fun session path ->
            let snapshot = read path
            let json = SnapshotCodec.serializeCanonical snapshot |> unwrap
            Assert.Contains("\"schemaVersion\":14", json)
            Assert.DoesNotContain("pendingValue", json)
            Assert.True(Result.isError(SnapshotCodec.deserialize(json.Replace("\"schemaVersion\":14", "\"schemaVersion\":13"))))
            let nyason = NyasonCodec.serializeCanonical snapshot |> unwrap
            Assert.Contains("nayamnyam.snapshot.v10", nyason)
            Assert.Equal(json, NyasonCodec.deserialize nyason |> unwrap |> SnapshotCodec.serializeCanonical |> unwrap)
            let typed = TypedSemanticIr.project snapshot |> unwrap
            Assert.Contains($"\"schemaVersion\":{TypedSemanticIrSerializer.schemaVersion}", TypedSemanticIrSerializer.serialize typed)
            let lowered = session.ProjectExecution() |> unwrap
            Assert.Contains($"\"schemaVersion\":{ExecutionIrSerializer.schemaVersion}", ExecutionIrSerializer.serialize lowered)
            let beforeIds = snapshot.Functions |> List.map (fun f -> f.Id)
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            Assert.Equal<FunctionId list>(beforeIds, (read path).Functions |> List.map (fun f -> f.Id))
            reopened.EmitCSharp CSharpBackendProfile.empty |> unwrap |> ignore
            let old = Snapshot.empty (StableId.createForTesting<SnapshotTag> "one-shot-old")
            let oldJson = SnapshotCodec.serializeCanonical old |> unwrap
            Assert.Contains("\"schemaVersion\":9", oldJson)
            Assert.Equal(oldJson, SnapshotCodec.deserialize oldJson |> unwrap |> SnapshotCodec.serializeCanonical |> unwrap))

    [<Fact>]
    let ``one-shot type used only by a local empty container still selects the new IR versions`` () =
        withFixture (fun session path ->
            create session "emptyLength" "[]" """{"kind":"primitive","primitive":"int64"}""" ($"""{{"kind":"listLength","value":{{"kind":"listEmpty","itemType":{cellJson}}}}}""")
            let snapshot = read path
            let localOnly = { snapshot with Functions = snapshot.Functions |> List.filter (fun f -> f.DisplayName = "emptyLength") }
            Assert.Empty(SnapshotValidator.validate localOnly)
            Assert.Contains("\"schemaVersion\":14", SnapshotCodec.serializeCanonical localOnly |> unwrap)
            let typed = TypedSemanticIr.project localOnly |> unwrap
            Assert.Contains($"\"schemaVersion\":{TypedSemanticIrSerializer.schemaVersion}", TypedSemanticIrSerializer.serialize typed)
            let execution = ExecutionIr.lower typed |> unwrap
            Assert.Contains($"\"schemaVersion\":{ExecutionIrSerializer.schemaVersion}", ExecutionIrSerializer.serialize execution))

    [<Fact>]
    let ``invalid scope payload and limit proposals refuse without changing persisted bytes`` () =
        withFixture (fun session path ->
            let before = File.ReadAllBytes path
            let proposals =
                [ "wrongScope", "[]", cellJson, $"""{{"kind":"oneShotCreate","scope":{{"kind":"stringLiteral","value":"scope"}},"valueType":{stringJson}}}""", "OneShotScopeTypeMismatch"
                  "wrongValue", $"""[{{"displayName":"cell","type":{cellJson}}}]""", boolJson, """{"kind":"oneShotComplete","cell":{"kind":"parameter","name":"cell"},"value":{"kind":"boolLiteral","value":true}}""", "OneShotValueTypeMismatch"
                  "wrongLimit", $"""[{{"displayName":"cell","type":{cellJson}}}]""", $"""{{"kind":"builtinGeneric","generic":"option","arguments":[{stringJson}]}}""", """{"kind":"oneShotWait","cell":{"kind":"parameter","name":"cell"},"limit":{"kind":"int64Literal","value":"1"}}""", "OneShotLimitTypeMismatch"
                  "hiddenScope", $"""[{{"displayName":"scope","type":{scopeJson}}}]""", $"""{{"kind":"builtinGeneric","generic":"oneShot","arguments":[{scopeJson}]}}""", $"""{{"kind":"oneShotCreate","scope":{{"kind":"parameter","name":"scope"}},"valueType":{scopeJson}}}""", "OneShotScopePayloadUnsupported" ]
            for name, parameters, result, body, code in proposals do
                let response = execute session "simulateCreateFunctionByName" ($"""{{"exported":true,"displayName":"{name}","parameters":{parameters},"resultType":{result},"requiredEffects":[],"body":{body}}}""")
                Assert.False(response.GetProperty("ok").GetBoolean())
                Assert.Contains(code, response.ToString())
                Assert.Equal<byte>(before, File.ReadAllBytes path)
            // Full snapshot validation independently repeats the type check.
            let snapshot = read path
            let make = snapshot.Functions |> List.find (fun f -> f.DisplayName = "make")
            let invalid = { snapshot with Functions = snapshot.Functions |> List.map (fun f -> if f.Id = make.Id then { f with Parameters = f.Parameters |> List.map (fun p -> { p with Type = TypeRef.Primitive PrimitiveType.String }) } else f) }
            let diagnostics = SnapshotValidator.validate invalid
            Assert.Contains(diagnostics, fun d -> d.Code = "OneShotScopeTypeMismatch"))

    [<Fact>]
    let ``runtime-bearing containers cannot cross host or run value boundaries or pretend to be pure`` () =
        withFixture (fun session path ->
            let snapshot = read path
            for fn in snapshot.Functions do Assert.False(PureFunctions.isPure snapshot fn.Id)
            let effect = execute session "simulateCreateEffect" """{"exported":true,"displayName":"IO"}""" |> requireSuccess
            execute session "commit" ($"""{{"proposalId":"{effect.GetProperty("proposalId").GetString()}"}}""") |> requireSuccess |> ignore
            let box = execute session "simulateCreateTypeByName" ($"""{{"exported":true,"displayName":"Box","typeKind":{{"kind":"product","fields":[{{"displayName":"cell","type":{cellJson}}}]}}}}""") |> requireSuccess
            execute session "commit" ($"""{{"proposalId":"{box.GetProperty("proposalId").GetString()}"}}""") |> requireSuccess |> ignore
            let boundary = execute session "simulateCreateCapabilityByName" """{"exported":true,"displayName":"Host","operations":[{"displayName":"take","parameters":[{"displayName":"box","type":{"kind":"named","name":"Box"}}],"resultType":{"kind":"primitive","primitive":"unit"},"effect":"IO"}]}"""
            Assert.False(boundary.GetProperty("ok").GetBoolean())
            Assert.Contains("TaskTypeAtCapabilityBoundary", boundary.ToString())
            let run = execute session "runPureByName" """{"function":"take","arguments":[{"kind":"unit"}]}"""
            Assert.False(run.GetProperty("ok").GetBoolean())
            Assert.Contains("RunTaskTypeUnsupported", run.ToString()))

    [<Fact>]
    let ``generic one-shot recipes allocate and retain their function-owned type parameters`` () =
        withFixture (fun session path ->
            let t = """{"kind":"typeParameter","name":"T"}"""
            let cellT = $"""{{"kind":"builtinGeneric","generic":"oneShot","arguments":[{t}]}}"""
            create session "genericMake" ($"""[{{"displayName":"scope","type":{scopeJson}}},{{"displayName":"witness","type":{t}}}]""") cellT ($"""{{"kind":"oneShotCreate","scope":{{"kind":"parameter","name":"scope"}},"valueType":{t}}}""")
            create session "genericFinish" ($"""[{{"displayName":"cell","type":{cellT}}},{{"displayName":"value","type":{t}}}]""") boolJson """{"kind":"oneShotComplete","cell":{"kind":"parameter","name":"cell"},"value":{"kind":"parameter","name":"value"}}"""
            let snapshot = read path
            let factory = snapshot.Functions |> List.find (fun f -> f.DisplayName = "genericMake")
            let parameterIds = TypeParameters.ofFunction factory |> List.map fst
            Assert.Single(parameterIds) |> ignore
            let constructType = ValueGraph.collectNodes factory.Body |> List.choose (fun n -> match n.Kind with SemanticNodeKind.OneShotCreate(_, TypeRef.TypeParameter(id)) -> Some id | _ -> None)
            Assert.Equal<TypeParameterId list>(parameterIds, constructType)
            Assert.Empty(SnapshotValidator.validate snapshot)
            let artifact = session.EmitCSharp CSharpBackendProfile.empty |> unwrap
            Assert.Contains("CreateOneShot<", artifact.Source)
            Assert.Contains("NyOneShot<", artifact.Source))

    [<Theory>]
    [<InlineData("genericMake", false)>]
    [<InlineData("genericMake", true)>]
    [<InlineData("genericDiscard", false)>]
    [<InlineData("relayDiscard", false)>]
    [<InlineData("deepDiscard", false)>]
    let ``generic calls cannot hide scope payloads even when results are discarded`` (target: string) (nested: bool) =
        withFixture (fun session path ->
            let t = """{"kind":"typeParameter","name":"T"}"""
            let unitJson = """{"kind":"primitive","primitive":"unit"}"""
            let parameters = $"""[{{"displayName":"scope","type":{scopeJson}}},{{"displayName":"witness","type":{t}}}]"""
            let call name witness = $"""{{"kind":"directCall","function":"{name}","arguments":[{{"parameter":"scope","value":{{"kind":"parameter","name":"scope"}}}},{{"parameter":"witness","value":{witness}}}]}}"""
            let discard value = $"""{{"kind":"bind","value":{value},"body":{{"kind":"unitLiteral"}}}}"""
            let newCell = $"""{{"kind":"oneShotCreate","scope":{{"kind":"parameter","name":"scope"}},"valueType":{t}}}"""
            create session "genericMake" parameters ($"""{{"kind":"builtinGeneric","generic":"oneShot","arguments":[{t}]}}""") newCell
            create session "genericDiscard" parameters unitJson (discard newCell)
            create session "relayDiscard" parameters unitJson (call "genericDiscard" """{"kind":"parameter","name":"witness"}""")
            create session "deepDiscard" parameters unitJson (call "relayDiscard" ($"""{{"kind":"listConstruct","itemType":{t},"items":[{{"kind":"parameter","name":"witness"}}]}}"""))
            create session "noCell" parameters unitJson """{"kind":"unitLiteral"}"""
            create session "allowedScopeArgument" ($"""[{{"displayName":"scope","type":{scopeJson}}}]""") unitJson (call "noCell" """{"kind":"parameter","name":"scope"}""")
            // Ordinary safe generic composition remains accepted and projectable.
            create session "safeCaller" ($"""[{{"displayName":"scope","type":{scopeJson}}}]""") unitJson (discard (call target """{"kind":"stringLiteral","value":"safe"}"""))
            let before = File.ReadAllBytes path
            let scopeValue = """{"kind":"parameter","name":"scope"}"""
            let forbidden = if nested then $"""{{"kind":"listConstruct","itemType":{scopeJson},"items":[{scopeValue}]}}""" else scopeValue
            let proposal = execute session "simulateCreateFunctionByName" ($"""{{"exported":true,"displayName":"unsafeCaller","parameters":[{{"displayName":"scope","type":{scopeJson}}}],"resultType":{unitJson},"requiredEffects":[],"body":{discard (call target forbidden)}}}""")
            Assert.False(proposal.GetProperty("ok").GetBoolean(), proposal.ToString())
            Assert.Contains("OneShotScopePayloadUnsupported", proposal.ToString())
            Assert.Equal<byte>(before, File.ReadAllBytes path)
            let snapshot = read path
            let caller = snapshot.Functions |> List.find (fun f -> f.DisplayName = "safeCaller")
            let scopeId = caller.Parameters.Head.Id
            // Independently tamper the persisted call argument: full validation must refuse too.
            let invalidCaller = { caller with Body = { caller.Body with Nodes = caller.Body.Nodes |> List.map (fun n -> match n.Kind with SemanticNodeKind.StringLiteral "safe" -> { n with Kind = SemanticNodeKind.ParameterReference scopeId } | _ -> n) } }
            let preliminary = { snapshot with Functions = snapshot.Functions |> List.map (fun f -> if f.Id = caller.Id then invalidCaller else f) }
            let resolve = SemanticFacts.createNodeTypeResolver preliminary invalidCaller
            let repairedNodes =
                invalidCaller.Body.Nodes |> List.map (fun n ->
                    match n.Kind with
                    | SemanticNodeKind.Bind(input, arm) ->
                        let binding = { arm.ValueBinding with Type = resolve input |> Option.get }
                        { n with Kind = SemanticNodeKind.Bind(input, { arm with ValueBinding = binding }) }
                    | _ -> n)
            let repairedBindings = { invalidCaller with Body = { invalidCaller.Body with Nodes = repairedNodes } }
            let invalid = { preliminary with Functions = preliminary.Functions |> List.map (fun f -> if f.Id = caller.Id then repairedBindings else f) }
            Assert.Contains(SnapshotValidator.validate invalid, fun d -> d.Code = "OneShotScopePayloadUnsupported" && d.Message.Contains("Call node"))
            Assert.True(Result.isError(TypedSemanticIr.project invalid))
            session.EmitCSharp CSharpBackendProfile.empty |> unwrap |> ignore)

    [<Fact>]
    let ``generic callback bindings cannot bypass scope-free payload requirements`` () =
        withFixture (fun session path ->
            let t = """{"kind":"typeParameter","name":"T"}"""
            let unitJson = """{"kind":"primitive","primitive":"unit"}"""
            let parameters = $"""[{{"displayName":"scope","type":{scopeJson}}},{{"displayName":"item","type":{t}}}]"""
            let outcome = $"""{{"kind":"builtinGeneric","generic":"result","arguments":[{unitJson},{stringJson}]}}"""
            let body = $"""{{"kind":"bind","value":{{"kind":"oneShotCreate","scope":{{"kind":"parameter","name":"scope"}},"valueType":{t}}},"body":{{"kind":"resultOk","errorType":{stringJson},"value":{{"kind":"unitLiteral"}}}}}}"""
            create session "worker" parameters outcome body
            let folderParameters = $"""[{{"displayName":"scope","type":{scopeJson}}},{{"displayName":"seed","type":{unitJson}}},{{"displayName":"item","type":{t}}}]"""
            create session "folder" folderParameters outcome body
            create session "pureMapper" ($"""[{{"displayName":"item","type":{t}}}]""") """{"kind":"primitive","primitive":"int64"}""" ($"""{{"kind":"listLength","value":{{"kind":"listEmpty","itemType":{{"kind":"builtinGeneric","generic":"oneShot","arguments":[{t}]}}}}}}""")
            let scopeValue = """{"kind":"parameter","name":"scope"}"""
            let context = $"""[{{"parameter":"scope","value":{scopeValue}}}]"""
            let list value itemType = $"""{{"kind":"listConstruct","itemType":{itemType},"items":[{value}]}}"""
            let nodes value itemType =
                [ $"""{{"kind":"taskStart","scope":{scopeValue},"worker":"worker","context":{context},"item":{value}}}"""
                  $"""{{"kind":"parallelAll","source":{list value itemType},"worker":"worker","context":{context}}}"""
                  $"""{{"kind":"listEffectFold","source":{list value itemType},"seed":{{"kind":"unitLiteral"}},"function":"folder","context":{context}}}"""
                  $"""{{"kind":"listMap","source":{list value itemType},"function":"pureMapper"}}""" ]
            let callerParameters = $"""[{{"displayName":"scope","type":{scopeJson}}}]"""
            for i, node in nodes """{"kind":"stringLiteral","value":"safe"}""" stringJson |> List.indexed do
                create session ($"safeCallback{i}") callerParameters unitJson ($"""{{"kind":"bind","value":{node},"body":{{"kind":"unitLiteral"}}}}""")
            let before = File.ReadAllBytes path
            let invalidScopeBody = $"""{{"kind":"taskScope","body":"worker","context":{context}}}"""
            for node in invalidScopeBody :: nodes scopeValue scopeJson do
                let proposal = execute session "simulateCreateFunctionByName" ($"""{{"exported":true,"displayName":"unsafeCallback","parameters":{callerParameters},"resultType":{unitJson},"requiredEffects":[],"body":{{"kind":"bind","value":{node},"body":{{"kind":"unitLiteral"}}}}}}""")
                Assert.False(proposal.GetProperty("ok").GetBoolean(), proposal.ToString())
                Assert.Contains("OneShotScopePayloadUnsupported", proposal.ToString())
                Assert.Equal<byte>(before, File.ReadAllBytes path)
            Assert.Empty(SnapshotValidator.validate (read path))
            session.EmitCSharp CSharpBackendProfile.empty |> unwrap |> ignore)

    [<Fact>]
    let ``abort free helpers cannot publish unordered one shot completions through direct calls`` () =
        withFixture (fun session path ->
            let parameters = $"""[{{"displayName":"cell","type":{cellJson}}},{{"displayName":"value","type":{stringJson}}}]"""
            create session "finishHop" parameters boolJson """{"kind":"directCall","function":"finish","arguments":[{"parameter":"cell","value":{"kind":"parameter","name":"cell"}},{"parameter":"value","value":{"kind":"parameter","name":"value"}}]}"""
            let call name value = $"""{{"kind":"directCall","function":"{name}","arguments":[{{"parameter":"cell","value":{{"kind":"parameter","name":"cell"}}}},{{"parameter":"value","value":{{"kind":"stringLiteral","value":"{value}"}}}}]}}"""
            let cellParameters = $"""[{{"displayName":"cell","type":{cellJson}}}]"""
            let unordered helper = $"""{{"kind":"valueEqual","left":{call helper "a"},"right":{call helper "b"}}}"""
            let ordered helper = $"""{{"kind":"bind","value":{call helper "a"},"body":{call helper "b"}}}"""
            // `finish` and `finishHop` declare no effects and contain no Abort: the helpers'
            // OneShotComplete is still observable, so the unordered caller is refused at proposal
            // time, naming both calls, instead of committing and failing at lowering.
            for helper in [ "finish"; "finishHop" ] do
                let before = File.ReadAllBytes path
                let response =
                    execute session "simulateCreateFunctionByName"
                        ($"""{{"displayName":"{helper}Unordered","parameters":{cellParameters},"resultType":{boolJson},"requiredEffects":[],"body":{unordered helper}}}""")
                Assert.False(response.GetProperty("ok").GetBoolean(), response.ToString())
                Assert.Contains("AmbiguousEffectOrder", response.ToString())
                Assert.Contains("unordered observable nodes", response.ToString())
                Assert.Equal<byte>(before, File.ReadAllBytes path)
                create session (helper + "Ordered") cellParameters boolJson (ordered helper)
                session.ProjectExecution() |> unwrap |> ignore
                // Editing an ordered caller back to the unordered form is refused the same way.
                let beforeEdit = File.ReadAllBytes path
                let edit =
                    execute session "simulateReplaceBodyByName"
                        ($"""{{"function":"{helper}Ordered","replacement":{unordered helper}}}""")
                Assert.False(edit.GetProperty("ok").GetBoolean(), edit.ToString())
                Assert.Contains("AmbiguousEffectOrder", edit.ToString())
                Assert.Equal<byte>(beforeEdit, File.ReadAllBytes path))
