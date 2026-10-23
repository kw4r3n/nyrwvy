namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module ExceptionBoundaryTests =
    let private unwrap = function Ok x -> x | Error e -> failwithf "%A" e
    let private stringType = """{"kind":"primitive","primitive":"string"}"""
    let private unitType = """{"kind":"primitive","primitive":"unit"}"""
    let private exceptionType = """{"kind":"builtinGeneric","generic":"exception","arguments":[]}"""
    let private resultType = $"""{{"kind":"builtinGeneric","generic":"result","arguments":[{stringType},{stringType}]}}"""
    let private execute session op (arguments: string) =
        let fields = arguments.Trim().Substring(1, arguments.Trim().Length - 2)
        use doc = JsonDocument.Parse(AuthoringProtocol.executeLine session ($"""{{"id":"exception-test","op":"{op}",{fields}}}"""))
        doc.RootElement.Clone()
    let private success (response: JsonElement) =
        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString())
        response.GetProperty("result")
    let private propose session name parameters result body =
        execute session "simulateCreateFunctionByName" ($"""{{"exported":true,"displayName":"{name}","parameters":{parameters},"resultType":{result},"requiredEffects":[],"body":{body}}}""")
    let private create session name parameters result body =
        let proposal = propose session name parameters result body |> success
        Assert.Equal("semanticallyReady", proposal.GetProperty("review").GetProperty("readiness").GetProperty("kind").GetString())
        execute session "commit" ($"""{{"proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}""") |> success |> ignore
    let private fixture test =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-exception-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        let path = Path.Combine(dir, "graph.json")
        try
            use session = AuthoringSession.createNew path |> unwrap
            create session "body" ($"""[{{"displayName":"item","type":{unitType}}}]""") resultType ($"""{{"kind":"resultOk","value":{{"kind":"stringLiteral","value":"ok"}},"errorType":{stringType}}}""")
            create session "handler" ($"""[{{"displayName":"exception","type":{exceptionType}}}]""") resultType ($"""{{"kind":"resultErr","error":{{"kind":"exceptionMessage","value":{{"kind":"parameter","name":"exception"}}}},"okType":{stringType}}}""")
            create session "caught" "[]" resultType """{"kind":"exceptionCatch","body":"body","handler":"handler"}"""
            create session "rethrow" ($"""[{{"displayName":"exception","type":{exceptionType}}}]""") resultType ($"""{{"kind":"exceptionRethrow","value":{{"kind":"parameter","name":"exception"}},"resultType":{resultType}}}""")
            test session path
        finally Directory.Delete(dir, true)
    let private snapshot path = File.ReadAllText path |> SnapshotCodec.deserialize |> unwrap

    [<Fact>]
    let ``explicit exception boundary roundtrips JSON both IRs and reopens`` () =
        fixture (fun session path ->
            let original = snapshot path
            Assert.Empty(SnapshotValidator.validate original)
            let json = SnapshotCodec.serializeCanonical original |> unwrap
            Assert.Contains("\"schemaVersion\":16", json)
            Assert.True(Result.isError(SnapshotCodec.deserialize(json.Replace("\"schemaVersion\":16", "\"schemaVersion\":15"))))
            Assert.Equal(json, SnapshotCodec.deserialize json |> unwrap |> SnapshotCodec.serializeCanonical |> unwrap)
            let typed = TypedSemanticIr.project original |> unwrap
            Assert.Contains($"\"schemaVersion\":{TypedSemanticIrSerializer.schemaVersion}", TypedSemanticIrSerializer.serialize typed)
            Assert.Contains($"\"schemaVersion\":{ExecutionIrSerializer.schemaVersion}", ExecutionIr.lower typed |> unwrap |> ExecutionIrSerializer.serialize)
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            Assert.Equal(json, snapshot path |> SnapshotCodec.serializeCanonical |> unwrap)
            Assert.Contains("ExceptionDispatchInfo.Capture", (reopened.EmitCSharp CSharpBackendProfile.empty |> unwrap).Source)
            let old = Snapshot.empty (StableId.createForTesting<SnapshotTag> "exception-old")
            Assert.Contains("\"schemaVersion\":9", SnapshotCodec.serializeCanonical old |> unwrap))

    [<Fact>]
    let ``invalid callbacks and exception operands refuse publication independently of full validation`` () =
        fixture (fun session path ->
            create session "wrong" ($"""[{{"displayName":"item","type":{unitType}}}]""") unitType """{"kind":"unitLiteral"}"""
            let before = File.ReadAllBytes path
            for body, code in [ """{"kind":"exceptionCatch","body":"handler","handler":"handler"}""", "ExceptionCatchCallbackMismatch"
                                """{"kind":"exceptionCatch","body":"body","handler":"wrong"}""", "ExceptionCatchCallbackMismatch"
                                """{"kind":"exceptionMessage","value":{"kind":"stringLiteral","value":"fake"}}""", "ExceptionValueTypeMismatch" ] do
                let response = propose session "invalid" "[]" resultType body
                Assert.False(response.GetProperty("ok").GetBoolean())
                Assert.Contains(code, response.ToString())
                Assert.Equal<byte>(before, File.ReadAllBytes path)
            let original = snapshot path
            let broken = { original with Functions = original.Functions |> List.map (fun fn -> if fn.DisplayName = "handler" then { fn with Parameters = fn.Parameters |> List.map (fun p -> { p with Type = TypeRef.Primitive PrimitiveType.String }) } else fn) }
            Assert.Contains(SnapshotValidator.validate broken, fun d -> d.Code = "ExceptionCatchCallbackMismatch"))

    [<Fact>]
    let ``captured exception cannot be equatable or cross run and capability boundaries`` () =
        fixture (fun session path ->
            Assert.False(EquatableTypes.isEquatable [] (TypeRef.BuiltinGeneric(BuiltinGenericType.Exception, [])))
            let run = execute session "runPureByName" """{"function":"rethrow","arguments":[{"kind":"unit"}]}"""
            Assert.False(run.GetProperty("ok").GetBoolean())
            Assert.Contains("RunTaskTypeUnsupported", run.ToString())
            let effect = execute session "simulateCreateEffect" """{"exported":true,"displayName":"IO"}""" |> success
            execute session "commit" ($"""{{"proposalId":"{effect.GetProperty("proposalId").GetString()}"}}""") |> success |> ignore
            let response = execute session "simulateCreateCapabilityByName" ($"""{{"exported":true,"displayName":"Host","operations":[{{"displayName":"take","parameters":[{{"displayName":"fault","type":{exceptionType}}}],"resultType":{unitType},"effect":"IO"}}]}}""")
            Assert.False(response.GetProperty("ok").GetBoolean())
            Assert.Contains("TaskTypeAtCapabilityBoundary", response.ToString()))

    [<Fact>]
    let ``generic catch and rethrow preserve owner type parameters including payload types`` () =
        fixture (fun session path ->
            let t = """{"kind":"typeParameter","name":"T"}"""
            let e = """{"kind":"typeParameter","name":"E"}"""
            let packet = $"""{{"kind":"builtinGeneric","generic":"result","arguments":[{t},{e}]}}"""
            create session "genericBody" ($"""[{{"displayName":"packet","type":{packet}}},{{"displayName":"item","type":{unitType}}}]""") packet """{"kind":"parameter","name":"packet"}"""
            create session "genericRethrow" ($"""[{{"displayName":"packet","type":{packet}}},{{"displayName":"exception","type":{exceptionType}}}]""") packet ($"""{{"kind":"exceptionRethrow","value":{{"kind":"parameter","name":"exception"}},"resultType":{packet}}}""")
            create session "genericCatch" ($"""[{{"displayName":"packet","type":{packet}}}]""") packet """{"kind":"exceptionCatch","body":"genericBody","bodyContext":[{"parameter":"packet","value":{"kind":"parameter","name":"packet"}}],"handler":"genericRethrow","handlerContext":[{"parameter":"packet","value":{"kind":"parameter","name":"packet"}}]}"""
            Assert.Empty(SnapshotValidator.validate(snapshot path))
            let fn = (snapshot path).Functions |> List.find (fun f -> f.DisplayName = "genericRethrow")
            let types = ValueGraph.collectNodes fn.Body |> List.collect SemanticNodeShape.payloadTypeRefs
            Assert.Contains(fn.ResultType, types)
            session.EmitCSharp CSharpBackendProfile.empty |> unwrap |> ignore)

    [<Fact>]
    let ``both body and handler effects must be declared before a boundary can publish`` () =
        fixture (fun session path ->
            let publish op arguments =
                let proposal = execute session op arguments |> success
                execute session "commit" ($"""{{"proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}""") |> success |> ignore
            publish "simulateCreateEffect" """{"exported":true,"displayName":"IO"}"""
            publish "simulateCreateCapabilityByName" ($"""{{"exported":true,"displayName":"Host","operations":[{{"displayName":"step","parameters":[],"resultType":{resultType},"effect":"IO"}}]}}""")
            let hostType = """{"kind":"capability","name":"Host"}"""
            let invoke = """{"kind":"capabilityInvoke","capabilityValue":{"kind":"parameter","name":"host"},"capability":"Host","operation":"step","arguments":[]}"""
            publish "simulateCreateFunctionByName" ($"""{{"exported":true,"displayName":"effectBody","parameters":[{{"displayName":"host","type":{hostType}}},{{"displayName":"item","type":{unitType}}}],"resultType":{resultType},"requiredEffects":["IO"],"body":{invoke}}}""")
            publish "simulateCreateFunctionByName" ($"""{{"exported":true,"displayName":"effectHandler","parameters":[{{"displayName":"host","type":{hostType}}},{{"displayName":"exception","type":{exceptionType}}}],"resultType":{resultType},"requiredEffects":["IO"],"body":{invoke}}}""")
            let before = File.ReadAllBytes path
            for body in [ """{"kind":"exceptionCatch","body":"effectBody","bodyContext":[{"parameter":"host","value":{"kind":"parameter","name":"host"}}],"handler":"handler"}"""
                          """{"kind":"exceptionCatch","body":"body","handler":"effectHandler","handlerContext":[{"parameter":"host","value":{"kind":"parameter","name":"host"}}]}""" ] do
                let response = propose session "missingEffect" ($"""[{{"displayName":"host","type":{hostType}}}]""") resultType body
                Assert.False(response.GetProperty("ok").GetBoolean())
                Assert.Contains("MissingEffectRequirement", response.ToString())
                Assert.Equal<byte>(before, File.ReadAllBytes path))

    [<Fact>]
    let ``cancellation classification is pure Bool with gated persistence and full operand validation`` () =
        fixture (fun session path ->
            create session "isCancellation" ($"""[{{"displayName":"exception","type":{exceptionType}}}]""") """{"kind":"primitive","primitive":"bool"}""" """{"kind":"exceptionIsCancellation","value":{"kind":"parameter","name":"exception"}}"""
            let original = snapshot path
            let fn = original.Functions |> List.find (fun fn -> fn.DisplayName = "isCancellation")
            let node = ValueGraph.collectNodes fn.Body |> List.find (fun n -> match n.Kind with SemanticNodeKind.ExceptionIsCancellation _ -> true | _ -> false)
            Assert.False(SemanticNodeShape.isIntrinsicallyEffectful node)
            Assert.Empty(SnapshotValidator.validate original)
            let json = SnapshotCodec.serializeCanonical original |> unwrap
            Assert.Contains("\"schemaVersion\":17", json)
            Assert.True(Result.isError(SnapshotCodec.deserialize(json.Replace("\"schemaVersion\":17", "\"schemaVersion\":16"))))
            Assert.Equal(json, SnapshotCodec.deserialize json |> unwrap |> SnapshotCodec.serializeCanonical |> unwrap)
            let typed = TypedSemanticIr.project original |> unwrap
            Assert.Contains($"\"schemaVersion\":{TypedSemanticIrSerializer.schemaVersion}", TypedSemanticIrSerializer.serialize typed)
            Assert.Contains("exceptionIsCancellation", ExecutionIr.lower typed |> unwrap |> ExecutionIrSerializer.serialize)
            Assert.Contains($"\"schemaVersion\":{ExecutionIrSerializer.schemaVersion}", ExecutionIr.lower typed |> unwrap |> ExecutionIrSerializer.serialize)
            let viewer = SemanticQuery.focusFunction original fn.Id |> unwrap |> ViewerProjection.focus |> ViewerHtml.renderFocusDocument
            Assert.Contains("exception is cancellation", viewer)
            let source = (session.EmitCSharp CSharpBackendProfile.empty |> unwrap).Source
            Assert.Contains(".SourceException is System.OperationCanceledException", source)
            let before = File.ReadAllBytes path
            let bad = propose session "invalidClassification" "[]" """{"kind":"primitive","primitive":"bool"}""" """{"kind":"exceptionIsCancellation","value":{"kind":"stringLiteral","value":"cancelled"}}"""
            Assert.False(bad.GetProperty("ok").GetBoolean())
            Assert.Contains("ExceptionValueTypeMismatch", bad.ToString())
            Assert.Equal<byte>(before, File.ReadAllBytes path)
            let broken = { original with Functions = original.Functions |> List.map (fun f -> if f.Id = fn.Id then { f with Parameters = f.Parameters |> List.map (fun p -> { p with Type = TypeRef.Primitive PrimitiveType.String }) } else f) }
            Assert.Contains(SnapshotValidator.validate broken, fun d -> d.Code = "ExceptionValueTypeMismatch")
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            Assert.Equal(source, (reopened.EmitCSharp CSharpBackendProfile.empty |> unwrap).Source))
