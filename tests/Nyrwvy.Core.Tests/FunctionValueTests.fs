namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module FunctionValueTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private send (session: AuthoringSession) line =
        let response = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
        response
    let private publish (session: AuthoringSession) (response: JsonDocument) =
        let proposal = response.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
        session.Commit(proposal, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
    let private primitive name = $"""{{"kind":"primitive","primitive":"{name}"}}"""
    let private boolType = primitive "bool"
    let private stringType = primitive "string"
    let private intType = primitive "int64"
    let private t = """{"kind":"typeParameter","name":"T"}"""
    let private container name item = $"""{{"kind":"builtinGeneric","generic":"{name}","arguments":[{item}]}}"""
    let private fnType arguments result =
        let arguments = String.concat "," arguments
        $"""{{"kind":"function","parameters":[{arguments}],"resultType":{result}}}"""
    let private parameter name = $"""{{"kind":"parameter","name":"{name}"}}"""
    let private reference name = $"""{{"kind":"functionReference","function":"{name}"}}"""
    let private invoke value arguments =
        let arguments = String.concat "," arguments
        $"""{{"kind":"functionInvoke","functionValue":{value},"arguments":[{arguments}]}}"""
    let private call name arguments =
        let bindings = arguments |> List.map (fun (name, value) -> $"""{{"parameter":"{name}","value":{value}}}""") |> String.concat ","
        $"""{{"kind":"directCall","function":"{name}","arguments":[{bindings}]}}"""
    let private request name parameters result body =
        let parameters = parameters |> List.map (fun (name, t) -> $"""{{"displayName":"{name}","type":{t}}}""") |> String.concat ","
        $"""{{"id":"create","op":"simulateCreateFunctionByName","exported":true,"structuredReview":true,"displayName":"{name}","parameters":[{parameters}],"resultType":{result},"requiredEffects":[],"body":{body}}}"""
    let private create session name parameters result body =
        use response = send session (request name parameters result body)
        publish session response
    let private find (session: AuthoringSession) name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)

    let private withFixture run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-function-values-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            use session = AuthoringSession.createNew path |> unwrap
            let predicate = fnType [ t ] boolType
            let condition = invoke (parameter "predicate") [ parameter "item" ]
            let item = parameter "item"
            let found = parameter "found"
            let none = $"""{{"kind":"optionNone","itemType":{t}}}"""
            let folder = $"""{{"kind":"matchOption","input":{found},"someBody":{{"kind":"optionSome","value":{{"kind":"patternReference","scopeDepth":0,"slot":"value"}}}},"noneBody":{{"kind":"conditional","condition":{condition},"whenTrue":{{"kind":"optionSome","value":{item}}},"whenFalse":{none}}}}}"""
            create session "findStep" [ "predicate", predicate; "found", container "option" t; "item", t ] (container "option" t) folder
            let source, predicateValue = parameter "items", parameter "predicate"
            let fold = $"""{{"kind":"listFold","source":{source},"seed":{none},"function":"findStep","context":[{{"parameter":"predicate","value":{predicateValue}}}]}}"""
            create session "find" [ "items", container "list" t; "predicate", predicate ] (container "option" t) fold
            create session "hasA" [ "text", stringType ] boolType """{"kind":"stringContains","value":{"kind":"parameter","name":"text"},"substring":{"kind":"stringLiteral","value":"a"}}"""
            create session "isEmpty" [ "text", stringType ] boolType """{"kind":"stringIsEmpty","value":{"kind":"parameter","name":"text"}}"""
            create session "positive" [ "number", intType ] boolType """{"kind":"int64GreaterThan","left":{"kind":"parameter","name":"number"},"right":{"kind":"int64Literal","value":"0"}}"""
            for name, itemType, predicateName in [ "findStrings", stringType, "hasA"; "findEmpty", stringType, "isEmpty"; "findNumbers", intType, "positive" ] do
                create session name [ "items", container "list" itemType ] (container "option" itemType)
                    (call "find" [ "items", parameter "items"; "predicate", reference predicateName ])
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            run reopened path
        finally
            Directory.Delete(root, true)

    [<Fact>]
    let ``ordinary generic function values persist and keep semantic dependencies and query facts`` () =
        withFixture (fun session path ->
            let snapshot = SnapshotCodec.deserialize (File.ReadAllText path) |> unwrap
            Assert.Empty(SnapshotValidator.validate snapshot)
            let restored = SnapshotCodec.deserialize (SnapshotCodec.serialize snapshot |> unwrap) |> unwrap
            Assert.Equal(SemanticFingerprint.compute snapshot, SemanticFingerprint.compute restored)
            Assert.Equal(SnapshotCodec.serialize snapshot, SnapshotCodec.serialize restored)
            let generic = find session "find"
            Assert.Single(TypeParameters.ofFunction generic) |> ignore
            let focused = session.FocusFunction (find session "findStrings").Id |> unwrap
            Assert.Contains(focused.Callees, fun site -> site.CalleeFunctionId = (find session "hasA").Id)
            let referenceNode = ValueGraph.collectNodes focused.Function.Body |> List.find (fun node -> match node.Kind with SemanticNodeKind.FunctionReference _ -> true | _ -> false)
            Assert.Equal(Some(TypeRef.Function([ TypeRef.Primitive PrimitiveType.String ], TypeRef.Primitive PrimitiveType.Bool)), focused.NodeTypes |> List.find (fun n -> n.NodeId = referenceNode.Id) |> fun n -> n.Type)
            Assert.Empty(focused.BodyEffectRequirements.EffectIds)
            let html = focused |> ViewerProjection.focus |> ViewerRendering.focus
            Assert.Contains("references function", html)
            Assert.Contains(StableId.value (find session "hasA").Id, html)
            let typed = TypedSemanticIr.project snapshot |> unwrap
            let execution = ExecutionIr.lower typed |> unwrap
            Assert.Contains("functionReference", TypedSemanticIrSerializer.serialize typed)
            Assert.Contains("functionInvoke", ExecutionIrSerializer.serialize execution)
            let artifact = CSharpBackend.emit execution CSharpBackendProfile.empty |> unwrap
            Assert.Contains("public delegate TResult NyFunction1", artifact.Source)
            let before = File.ReadAllText path
            use invalidEdit = AuthoringProtocol.executeLine session """{"id":"bad","op":"simulateReplaceBodyByName","function":"hasA","replacement":{"kind":"cancellationRequested"}}""" |> JsonDocument.Parse
            Assert.False(invalidEdit.RootElement.GetProperty("ok").GetBoolean())
            Assert.Contains("ImpureFunctionReference", invalidEdit.RootElement.GetRawText())
            match session.SimulateOperations [ AgentOperationIntent.DeleteFunction (find session "hasA").Id ] with
            | Error _ -> ()
            | Ok _ -> failwith "Deleting a referenced function must fail."
            Assert.Equal(before, File.ReadAllText path))

    [<Fact>]
    let ``function values reject wrong types arity capture generic and ambient effects`` () =
        withFixture (fun session path ->
            create session "ambient" [] boolType """{"kind":"cancellationRequested"}"""
            create session "indirectAmbient" [] boolType (call "ambient" [])
            let before = File.ReadAllText path
            let reject expected result body =
                use response = AuthoringProtocol.executeLine session (request "bad" [] result body) |> JsonDocument.Parse
                Assert.False(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
                Assert.Contains(expected, response.RootElement.GetRawText())
            reject "FunctionInvokeArityMismatch" boolType (invoke (reference "hasA") [])
            reject "FunctionInvokeArgumentTypeMismatch" boolType (invoke (reference "hasA") [ """{"kind":"int64Literal","value":"1"}""" ])
            reject "FunctionInvokeTargetTypeMismatch" boolType (invoke """{"kind":"boolLiteral","value":true}""" [])
            reject "ImpureFunctionReference" (fnType [] boolType) (reference "ambient")
            reject "ImpureFunctionReference" (fnType [] boolType) (reference "indirectAmbient")
            reject "GenericFunctionReferenceUnsupported" (fnType [] boolType) (reference "find")
            reject "unsupported properties" (fnType [ stringType ] boolType) """{"kind":"functionReference","function":"hasA","capture":[]}"""
            let badFind = call "find" [ "items", """{"kind":"listEmpty","itemType":{"kind":"primitive","primitive":"int64"}}"""; "predicate", reference "hasA" ]
            reject "PureExpressionCallArgumentTypeMismatch" (container "option" intType) badFind
            Assert.Equal(before, File.ReadAllText path))

    [<Fact; Trait("Suite", "Extended")>]
    let ``one find body accepts different predicates and types through the common runner and reviewed replacement`` () =
        withFixture (fun session path ->
            let execute name argument = (PureRun.run session (find session name).Id [ argument ] None |> unwrap).Actual
            Assert.Equal(PureRunValue.StringOption(Some "alpha"), execute "findStrings" (PureRunValue.StringList [ "zz"; "alpha"; "beta" ]))
            Assert.Equal(PureRunValue.StringOption(Some ""), execute "findEmpty" (PureRunValue.StringList [ "x"; ""; "y" ]))
            Assert.Equal(PureRunValue.StringOption None, execute "findStrings" (PureRunValue.StringList []))
            Assert.Equal(PureRunValue.StringOption None, execute "findStrings" (PureRunValue.StringList [ "xyz" ]))
            Assert.Equal(PureRunValue.Int64Option(Some 2L), execute "findNumbers" (PureRunValue.Int64List [ -1L; 0L; 2L; 3L ]))
            let id = (find session "hasA").Id
            use proposal = send session """{"id":"replace","op":"simulateReplaceBodyByName","function":"hasA","structuredReview":true,"replacement":{"kind":"boolLiteral","value":false}}"""
            publish session proposal
            Assert.Equal(id, (find session "hasA").Id)
            Assert.Equal(PureRunValue.StringOption None, execute "findStrings" (PureRunValue.StringList [ "alpha" ]))
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            Assert.Equal(id, (find reopened "hasA").Id))

    [<Fact>]
    let ``snapshot validation prevents invalid function values bypassing authoring checks`` () =
        withFixture (fun session path ->
            let exactReference = $"""{{"kind":"functionReference","functionId":"{StableId.value (find session "hasA").Id}"}}"""
            let exactRequest = request "apply" [] boolType (invoke exactReference [ """{"kind":"stringLiteral","value":"a"}""" ])
            use exact = send session (exactRequest.Replace("simulateCreateFunctionByName", "simulateCreateFunction"))
            publish session exact
            let snapshot = SnapshotCodec.deserialize (File.ReadAllText path) |> unwrap
            let applyId, predicateId = (find session "apply").Id, (find session "hasA").Id
            let editNodes edit =
                let functions = snapshot.Functions |> List.map (fun fn ->
                    if fn.Id <> applyId then fn
                    else { fn with Body = ValueGraph.mapNodes edit fn.Body })
                { snapshot with Functions = functions }
            let check code candidate = Assert.Contains(SnapshotValidator.validate candidate, fun diagnostic -> diagnostic.Code = code)
            check "UnknownFunctionReference" { snapshot with Functions = snapshot.Functions |> List.filter (fun fn -> fn.Id <> predicateId) }
            check "FunctionInvokeArityMismatch" (editNodes (fun node ->
                match node.Kind with
                | SemanticNodeKind.FunctionInvoke(value, _) -> { node with Kind = SemanticNodeKind.FunctionInvoke(value, []) }
                | _ -> node))
            check "FunctionInvokeTargetTypeMismatch" (editNodes (fun node ->
                match node.Kind with
                | SemanticNodeKind.FunctionInvoke(_, arguments) -> { node with Kind = SemanticNodeKind.FunctionInvoke(arguments.Head, []) }
                | _ -> node))
            check "FunctionInvokeArgumentTypeMismatch" (editNodes (fun node ->
                match node.Kind with
                | SemanticNodeKind.StringLiteral _ -> { node with Kind = SemanticNodeKind.BoolLiteral true }
                | _ -> node))
            let effectId = StableId.createForTesting<EffectTag> "function-purity-effect"
            let impure =
                { snapshot with
                    Effects = [ { Exported = true; Id = effectId; DisplayName = "Observable" } ]
                    Functions = snapshot.Functions |> List.map (fun fn ->
                        if fn.Id = predicateId then { fn with RequiredEffects = [ effectId ] } else fn) }
            check "ImpureFunctionReference" impure
            // A reference's signature is observed at its uses; an incompatible edit cannot be published.
            let changedSignatures = snapshot.Functions |> List.map (fun fn ->
                if fn.Id = predicateId then { fn with ResultType = TypeRef.Primitive PrimitiveType.Int64 } else fn)
            check "ArgumentTypeMismatch" { snapshot with Functions = changedSignatures }
            let encoded = SnapshotCodec.serialize snapshot |> unwrap
            Assert.True(Result.isError (SnapshotCodec.deserialize (encoded.Replace("\"kind\":\"functionReference\"", "\"kind\":\"functionReference\",\"captures\":[]")))))

    [<Fact; Trait("Suite", "Extended")>]
    let ``function values forward through generic returns and options and retain pinned dependencies`` () =
        withFixture (fun session path ->
            create session "truth" [] boolType """{"kind":"boolLiteral","value":true}"""
            create session "zero" [] boolType (invoke (reference "truth") [])
            create session "join" [ "left", stringType; "right", stringType ] stringType
                """{"kind":"stringConcat","left":{"kind":"parameter","name":"left"},"right":{"kind":"parameter","name":"right"}}"""
            let binary = fnType [ t; t ] t
            create session "forward" [ "value", binary ] binary (parameter "value")
            let forwarded = call "forward" [ "value", reference "join" ]
            let invoked = invoke """{"kind":"patternReference","scopeDepth":0,"slot":"value"}"""
                              [ """{"kind":"stringLiteral","value":"left"}"""; """{"kind":"stringLiteral","value":"right"}""" ]
            let body = $"""{{"kind":"matchOption","input":{{"kind":"optionSome","value":{forwarded}}},"someBody":{invoked},"noneBody":{{"kind":"stringLiteral","value":"missing"}}}}"""
            create session "fromOption" [] stringType body
            Assert.Equal(PureRunValue.Bool true, (PureRun.run session (find session "zero").Id [] None |> unwrap).Actual)
            Assert.Equal(PureRunValue.String "leftright", (PureRun.run session (find session "fromOption").Id [] None |> unwrap).Actual)
            // Preserve positional arguments, including non-commutative calls, through both codecs.
            let snapshot = SnapshotCodec.deserialize (File.ReadAllText path) |> unwrap
            let restored = SnapshotCodec.deserialize (SnapshotCodec.serialize snapshot |> unwrap) |> unwrap
            Assert.Equal(SnapshotCodec.serialize snapshot, SnapshotCodec.serialize restored)
            create session "rawFunction" [] (fnType [ stringType; stringType ] stringType) (reference "join")
            match PureRun.run session (find session "rawFunction").Id [] None with
            | Error(PureRunError.Unsupported("RunResultTypeUnsupported", _)) -> ()
            | other -> failwithf "Raw delegates must not cross the runner protocol: %A" other
            let consumerPath = Path.Combine(Path.GetDirectoryName path, "consumer.json")
            session.Close()
            use consumer = AuthoringSession.createNewWithDependency consumerPath (Some path) |> unwrap
            create consumer "imported" [] stringType
                (invoke (reference "join") [ """{"kind":"stringLiteral","value":"imported-"}"""; """{"kind":"stringLiteral","value":"predicate"}""" ])
            let id = (find consumer "imported").Id
            consumer.Close()
            File.Delete path
            use reopened = AuthoringSession.openExisting consumerPath |> unwrap
            Assert.Equal(PureRunValue.String "imported-predicate", (PureRun.run reopened id [] None |> unwrap).Actual))
