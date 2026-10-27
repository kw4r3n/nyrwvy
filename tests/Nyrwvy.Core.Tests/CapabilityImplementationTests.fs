namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module CapabilityImplementationTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private send session request =
        let response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
        response
    let private publish (session: AuthoringSession) (response: JsonDocument) =
        let proposal = response.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
        session.Commit(proposal, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
    let private primitive name = $"""{{"kind":"primitive","primitive":"{name}"}}"""
    let private intType = primitive "int64"
    let private stringType = primitive "string"
    let private boolType = primitive "bool"
    let private capType = """{"kind":"capability","name":"Values"}"""
    let private parameter name = $"""{{"kind":"parameter","name":"{name}"}}"""
    let private number value = $"""{{"kind":"int64Literal","value":"{value}"}}"""
    let private call name arguments =
        let bindings = arguments |> List.map (fun (name, value) -> $"""{{"parameter":"{name}","value":{value}}}""") |> String.concat ","
        $"""{{"kind":"directCall","function":"{name}","arguments":[{bindings}]}}"""
    let private construct read =
        $"""{{"kind":"capabilityConstruct","capability":"Values","implementations":[{{"operation":"read","function":"{read}"}},{{"operation":"join","function":"join"}}]}}"""
    let private request name parameters result effects body =
        let parameters = parameters |> List.map (fun (name, t) -> $"""{{"displayName":"{name}","type":{t}}}""") |> String.concat ","
        $"""{{"id":"create","op":"simulateCreateFunctionByName","exported":true,"structuredReview":true,"displayName":"{name}","parameters":[{parameters}],"resultType":{result},"requiredEffects":{effects},"body":{body}}}"""
    let private create session name parameters result effects body =
        use response = send session (request name parameters result effects body)
        publish session response
    let private find (session: AuthoringSession) name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
    let private snapshot path = SnapshotCodec.deserialize (File.ReadAllText path) |> unwrap
    let private withFixture run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-capability-implementations-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            use session = AuthoringSession.createNew path |> unwrap
            let current () = (session.Status() |> unwrap).SnapshotId
            let commit (proposal: AuthoringProposal) = session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            session.SimulateCreateEffect { Exported = true; ExpectedBaseSnapshotId = current (); DisplayName = "Read" } |> unwrap |> commit
            let effect = session.Declarations().Effects |> List.exactlyOne
            let operation name parameters result: AgentCapabilityOperationIntent =
                { MayWait = false; DisplayName = name; Parameters = parameters |> List.map (fun name -> { DisplayName = name; Type = TypeRef.Primitive PrimitiveType.String })
                  ResultType = result; FailureType = None; EffectId = effect.Id }
            session.SimulateCreateCapability
                { TaskBound = false; Exported = true; ExpectedBaseSnapshotId = current (); DisplayName = "Values"
                  Operations = [ operation "read" [] (TypeRef.Primitive PrimitiveType.Int64); operation "join" [ "left"; "right" ] (TypeRef.Primitive PrimitiveType.String) ] }
            |> unwrap |> commit
            create session "ten" [] intType "[]" (number "10")
            create session "twenty" [] intType "[]" (number "20")
            create session "join" [ "a", stringType; "b", stringType ] stringType "[]"
                """{"kind":"stringConcat","left":{"kind":"parameter","name":"a"},"right":{"kind":"parameter","name":"b"}}"""
            let oldBytes = File.ReadAllText path
            Assert.Contains(sprintf "\"schemaVersion\":%d" SnapshotSchema.current.Json, oldBytes)
            Assert.Equal(oldBytes, SnapshotCodec.serialize (snapshot path) |> unwrap)
            create session "low" [] capType "[]" (construct "ten")
            create session "high" [] capType "[]" (construct "twenty")
            create session "read" [ "values", capType ] intType "[\"Read\"]"
                """{"kind":"capabilityInvoke","capabilityValue":{"kind":"parameter","name":"values"},"capability":"Values","operation":"read","arguments":[]}"""
            let yes, no, condition = call "low" [], call "high" [], parameter "low"
            let selected = $"""{{"kind":"conditional","condition":{condition},"whenTrue":{yes},"whenFalse":{no}}}"""
            create session "choose" [ "low", boolType ] intType "[\"Read\"]" (call "read" [ "values", selected ])
            let value = call "low" []
            create session "joinViaCapability" [] stringType "[\"Read\"]"
                ($"""{{"kind":"capabilityInvoke","capabilityValue":{value},"capability":"Values","operation":"join","arguments":[{{"parameter":"right","value":{{"kind":"stringLiteral","value":"R"}}}},{{"parameter":"left","value":{{"kind":"stringLiteral","value":"L"}}}}]}}""")
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            run reopened path
        finally
            Directory.Delete(root, true)

    [<Fact>]
    let ``program implementations persist with reviewable semantic dependencies and explicit schema versions`` () =
        withFixture (fun session path ->
            let graph = snapshot path
            let encoded = SnapshotCodec.serializeCanonical graph |> unwrap
            Assert.Contains("\"schemaVersion\":10", encoded)
            let restored = SnapshotCodec.deserialize encoded |> unwrap
            Assert.Equal(encoded, SnapshotCodec.serializeCanonical restored |> unwrap)
            Assert.Equal(SemanticFingerprint.compute graph, SemanticFingerprint.compute restored)
            Assert.True(Result.isError(SnapshotCodec.deserialize (encoded.Replace("\"schemaVersion\":10", "\"schemaVersion\":9"))))
            Assert.True(Result.isError(SnapshotCodec.deserialize (encoded.Replace("\"kind\":\"capabilityConstruct\"", "\"kind\":\"capabilityConstruct\",\"captures\":[]"))))
            let low = find session "low"
            let focus = session.FocusFunction low.Id |> unwrap
            Assert.Equal(2, focus.Callees.Length)
            Assert.Contains(focus.Callees, fun site -> site.CalleeFunctionId = (find session "ten").Id)
            Assert.Empty(focus.BodyEffectRequirements.EffectIds)
            let html = ViewerProjection.focus focus |> ViewerHtml.renderFocusDocument
            Assert.Contains("capability construction", html)
            Assert.Contains(StableId.value (find session "ten").Id, html)
            use inspected = send session ($"""{{"id":"read","op":"readFunction","functionId":"{StableId.value low.Id}"}}""")
            Assert.Contains("implementations", inspected.RootElement.GetRawText())
            let typed = TypedSemanticIr.project graph |> unwrap
            let execution = ExecutionIr.lower typed |> unwrap
            Assert.Contains("capabilityConstruct", TypedSemanticIrSerializer.serialize typed)
            Assert.Contains("capabilityConstruct", ExecutionIrSerializer.serialize execution)
            let reachable = ExecutionCallGraph.forEntry execution (find session "choose").Id
            Assert.Contains(reachable.Functions, fun fn -> fn.SemanticFunctionId = (find session "ten").Id)
            Assert.Contains(reachable.Functions, fun fn -> fn.SemanticFunctionId = (find session "twenty").Id)
            Assert.Contains(reachable.Functions, fun fn -> fn.SemanticFunctionId = (find session "join").Id)
            let reverseImplementations (node: SemanticNode) =
                match node.Kind with
                    | SemanticNodeKind.CapabilityConstruct(id, implementations) -> { node with Kind = SemanticNodeKind.CapabilityConstruct(id, List.rev implementations) }
                    | _ -> node
            let reordered =
                { graph with
                    Functions = graph.Functions |> List.map (fun fn -> { fn with Body = ValueGraph.mapNodes reverseImplementations fn.Body }) }
            Assert.Equal(encoded, SnapshotCodec.serializeCanonical reordered |> unwrap))

    [<Fact>]
    let ``invalid implementations and later edits cannot bypass the snapshot oracle`` () =
        withFixture (fun session path ->
            create session "ambient" [] intType "[]"
                """{"kind":"conditional","condition":{"kind":"cancellationRequested"},"whenTrue":{"kind":"int64Literal","value":"0"},"whenFalse":{"kind":"int64Literal","value":"1"}}"""
            create session "indirectAmbient" [] intType "[]" (call "ambient" [])
            create session "declaredEffect" [] intType "[\"Read\"]" (number "0")
            let typeParameter = """{"kind":"typeParameter","name":"T"}"""
            create session "generic" [ "item", typeParameter ] typeParameter "[]" (parameter "item")
            let before = File.ReadAllText path
            let reject code body =
                use response = AuthoringProtocol.executeLine session (request "bad" [] capType "[]" body) |> JsonDocument.Parse
                Assert.False(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
                Assert.Contains(code, response.RootElement.GetRawText())
            reject "ImpureCapabilityImplementation" (construct "indirectAmbient")
            reject "ImpureCapabilityImplementation" (construct "declaredEffect")
            reject "GenericCapabilityImplementationUnsupported" (construct "generic")
            reject "CapabilityImplementationSignatureMismatch" (construct "join")
            reject "CapabilityImplementationSetMismatch" """{"kind":"capabilityConstruct","capability":"Values","implementations":[]}"""
            reject "DuplicateCapabilityImplementation" """{"kind":"capabilityConstruct","capability":"Values","implementations":[{"operation":"read","function":"ten"},{"operation":"read","function":"twenty"},{"operation":"join","function":"join"}]}"""
            reject "unsupported properties" ((construct "ten").Replace("\"implementations\"", "\"capture\":[],\"implementations\""))
            use noEffect = AuthoringProtocol.executeLine session (request "missingEffect" [] intType "[]" (call "read" [ "values", call "low" [] ])) |> JsonDocument.Parse
            Assert.False(noEffect.RootElement.GetProperty("ok").GetBoolean())
            use edit = AuthoringProtocol.executeLine session """{"id":"edit","op":"simulateReplaceBodyByName","function":"ten","replacement":{"kind":"directCall","function":"ambient","arguments":[]}}""" |> JsonDocument.Parse
            Assert.False(edit.RootElement.GetProperty("ok").GetBoolean())
            Assert.Contains("ImpureCapabilityImplementation", edit.RootElement.GetRawText())
            Assert.True(Result.isError(session.SimulateOperations [ AgentOperationIntent.DeleteFunction (find session "ten").Id ]))
            Assert.Equal(before, File.ReadAllText path)
            let graph = snapshot path
            let capability = List.exactlyOne graph.Capabilities
            let read = capability.Operations |> List.find (fun op -> op.DisplayName = "read")
            let check code candidate = Assert.Contains(SnapshotValidator.validate candidate, fun diagnostic -> diagnostic.Code = code)
            check "UnknownCapabilityImplementationFunction" { graph with Functions = graph.Functions |> List.filter (fun fn -> fn.Id <> (find session "ten").Id) }
            check "CapabilityImplementationSetMismatch" { graph with Capabilities = [ { capability with Operations = capability.Operations |> List.filter (fun op -> op.Id <> read.Id) } ] }
            let changedFunctions = graph.Functions |> List.map (fun fn ->
                if fn.Id = (find session "ten").Id then { fn with ResultType = TypeRef.Primitive PrimitiveType.Bool } else fn)
            check "CapabilityImplementationSignatureMismatch" { graph with Functions = changedFunctions }
            let exactBody = $"""{{"kind":"capabilityConstruct","capabilityId":"{StableId.value capability.Id}","implementations":[{{"operationId":"{StableId.value read.Id}","functionId":"{StableId.value (find session "ten").Id}"}}]}}"""
            use exact = AuthoringProtocol.executeLine session ((request "exact" [] ($"""{{"kind":"capability","capabilityId":"{StableId.value capability.Id}"}}""") "[]" exactBody).Replace("simulateCreateFunctionByName", "simulateCreateFunction")) |> JsonDocument.Parse
            Assert.Contains("CapabilityImplementationSetMismatch", exact.RootElement.GetRawText()))

    [<Fact; Trait("Suite", "Extended")>]
    let ``two program implementations execute through the same consumer and preserve reviewed identity`` () =
        withFixture (fun session path ->
            let run name args = (PureRun.run session (find session name).Id args None |> unwrap).Actual
            Assert.Equal(PureRunValue.Int64 10L, run "choose" [ PureRunValue.Bool true ])
            Assert.Equal(PureRunValue.Int64 20L, run "choose" [ PureRunValue.Bool false ])
            Assert.Equal(PureRunValue.String "LR", run "joinViaCapability" [])
            let functionId = (find session "low").Id
            let nodeId = (find session "low").Body.ResultNodeId
            let capability = List.exactlyOne (session.Declarations().Capabilities)
            let bindings = capability.Operations |> List.map (fun op ->
                let target = find session (if op.DisplayName = "read" then "twenty" else "join")
                $"""{{"operationId":"{StableId.value op.Id}","functionId":"{StableId.value target.Id}"}}""") |> String.concat ","
            use replacement = send session ($"""{{"id":"edit","op":"simulateReplaceTarget","structuredReview":true,"detail":"full","functionId":"{StableId.value functionId}","targetNodeId":"{StableId.value nodeId}","replacement":{{"kind":"capabilityConstruct","capabilityId":"{StableId.value capability.Id}","implementations":[{bindings}]}}}}""")
            Assert.Contains("implementations", replacement.RootElement.GetRawText())
            publish session replacement
            Assert.Equal(functionId, (find session "low").Id)
            Assert.Equal(nodeId, (find session "low").Body.ResultNodeId)
            Assert.Equal(PureRunValue.Int64 20L, run "choose" [ PureRunValue.Bool true ])
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            Assert.Equal(functionId, (find reopened "low").Id)
            Assert.Equal(nodeId, (find reopened "low").Body.ResultNodeId))

    [<Fact; Trait("Suite", "Extended")>]
    let ``program capabilities survive pinned library reuse and can be shared by parallel workers`` () =
        withFixture (fun session path ->
            let result = $"""{{"kind":"builtinGeneric","generic":"result","arguments":[{intType},{stringType}]}}"""
            let read = call "read" [ "values", parameter "values" ]
            create session "worker" [ "values", capType; "item", intType ] result "[\"Read\"]" ($"""{{"kind":"resultOk","errorType":{stringType},"value":{read}}}""")
            let listType = $"""{{"kind":"builtinGeneric","generic":"list","arguments":[{intType}]}}"""
            let resultList = $"""{{"kind":"builtinGeneric","generic":"result","arguments":[{listType},{stringType}]}}"""
            let source = $"""{{"kind":"listConstruct","itemType":{intType},"items":[{{"kind":"int64Literal","value":"1"}},{{"kind":"int64Literal","value":"2"}}]}}"""
            let low = call "low" []
            create session "parallel" [] resultList "[\"Read\"]"
                ($"""{{"kind":"parallelAll","source":{source},"worker":"worker","context":[{{"parameter":"values","value":{low}}}]}}""")
            let actual = (PureRun.run session (find session "parallel").Id [] None |> unwrap).Actual
            Assert.Equal(PureRunValue.ResultValue(TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ TypeRef.Primitive PrimitiveType.Int64 ]), TypeRef.Primitive PrimitiveType.String, true, PureRunValue.Int64List [ 10L; 10L ]), actual)
            let consumerPath = Path.Combine(Path.GetDirectoryName path, "consumer.json")
            session.Close()
            use consumer = AuthoringSession.createNewWithDependency consumerPath (Some path) |> unwrap
            create consumer "imported" [] intType "[\"Read\"]" (call "read" [ "values", call "high" [] ])
            let id = (find consumer "imported").Id
            consumer.Close()
            File.Delete path
            use reopened = AuthoringSession.openExisting consumerPath |> unwrap
            Assert.Equal(PureRunValue.Int64 20L, (PureRun.run reopened id [] None |> unwrap).Actual))
