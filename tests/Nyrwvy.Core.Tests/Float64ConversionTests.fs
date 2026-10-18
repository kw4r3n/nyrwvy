namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// `Int64ToFloat64(value): Float64` (IEEE round to nearest, ties to even) and Float64 run values:
/// `{"kind":"float64","value": <shortest round-trip number> | "NaN" | "Infinity" | "-Infinity" | "-0"}`,
/// compared bitwise with every NaN equal.
module Float64ConversionTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private textType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64
    let private floatType = TypeRef.Primitive PrimitiveType.Float64
    let private listOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
    let private optionOf item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
    let private slot = ExpressionRecipe.ParameterSlot
    let private toFloat = ExpressionRecipe.Int64ToFloat64
    let private float64 (number: float) = PureRunValue.Float64(Float64Bits.ofFloat number)
    let private current (session: AuthoringSession) = (session.Status() |> unwrap).SnapshotId
    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
    let private find (session: AuthoringSession) name = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
    let private request session name parameters resultType effects body: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
        { ExpectedBaseSnapshotId = current session; DisplayName = name
          Parameters = parameters |> List.map (fun (name, t) -> { DisplayName = name; Type = t })
          ResultType = resultType; RequiredEffects = effects; Body = body }
    let private createWith session name parameters resultType effects body =
        request session name parameters resultType effects body |> session.SimulateCreateFunction |> unwrap |> publish session
        find session name
    let private create session name parameters resultType body = createWith session name parameters resultType [] body
    let private rejects (session: AuthoringSession) parameters resultType body =
        match session.SimulateCreateFunction(request session "rejected" parameters resultType [] body) with
        | Error error -> sprintf "%A" error
        | Ok _ -> failwith "The function was accepted."
    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-float64-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            use session = AuthoringSession.createNew path |> unwrap
            run session path
        finally Directory.Delete(root, true)
    let private execute session line = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
    let private send session line =
        let response = execute session line
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
        response
    let private publishWire (session: AuthoringSession) (response: JsonDocument) =
        session.Commit(response.RootElement.GetProperty("result").GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
    let private errorCode session line =
        use response = execute session line
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
        response.RootElement.GetProperty("error").GetProperty("code").GetString()
    let private primitive name = sprintf """{"kind":"primitive","primitive":"%s"}""" name

    [<Fact>]
    let ``Int64ToFloat64 checks its operand in recipes and persisted graphs`` () =
        withSession (fun session _ ->
            create session "toFloat" [ "value", intType ] floatType (toFloat (slot 0)) |> ignore
            Assert.Contains("PureExpressionOperandTypeMismatch", rejects session [ "value", textType ] floatType (toFloat (slot 0)))
            Assert.Contains("PureExpressionOperandTypeMismatch", rejects session [ "value", floatType ] floatType (toFloat (slot 0)))
            // The result is Float64, not Int64.
            rejects session [ "value", intType ] intType (toFloat (slot 0)) |> ignore)
        let nodeId name = StableId.createForTesting<GraphNodeTag> ("node-float-" + name)
        let validate operand resultType =
            let fn: SemanticFunction =
                { Id = StableId.createForTesting<FunctionTag> "function-float-entry"; DisplayName = "entry"; Parameters = []
                  ResultType = resultType; RequiredEffects = []
                  Body =
                    { Nodes = [ { Id = nodeId "value"; Kind = operand }; { Id = nodeId "convert"; Kind = SemanticNodeKind.Int64ToFloat64(nodeId "value") } ]
                      ResultNodeId = nodeId "convert" } }
            SnapshotValidator.validate { Snapshot.empty (StableId.createForTesting<SnapshotTag> "snapshot-float") with Functions = [ fn ] }
            |> List.map (fun issue -> issue.Code)
        Assert.Empty(validate (SemanticNodeKind.Int64Literal 1L) floatType)
        Assert.Contains("Int64IntrinsicTypeMismatch", validate (SemanticNodeKind.StringLiteral "1") floatType)
        Assert.Contains("Int64IntrinsicTypeMismatch", validate (SemanticNodeKind.Float64Literal(Float64Bits.ofFloat 1.0)) floatType)
        Assert.NotEmpty(validate (SemanticNodeKind.Int64Literal 1L) intType)

    [<Fact>]
    let ``exact-ID and name-resolved recipes persist reopen and project`` () =
        withSession (fun session path ->
            let signature = sprintf """"parameters":[{"displayName":"value","type":%s}],"resultType":%s,"requiredEffects":[]""" (primitive "int64") (primitive "float64")
            use exact =
                send session (sprintf """{"id":"exact","op":"simulateCreateFunction","displayName":"exact",%s,"body":{"kind":"int64ToFloat64","value":{"kind":"parameterSlot","index":0}}}""" signature)
            publishWire session exact
            use named =
                send session (sprintf """{"id":"named","op":"simulateCreateFunctionByName","structuredReview":true,"detail":"full","displayName":"named",%s,"body":{"kind":"int64ToFloat64","value":{"kind":"parameter","name":"value"}}}""" signature)
            Assert.Contains("\"kind\":\"int64ToFloat64\"", named.RootElement.GetRawText())
            publishWire session named
            let fn = find session "named"
            let viewer = session.FocusFunction fn.Id |> unwrap |> ViewerProjection.focus
            let node = viewer.Graph.Nodes |> List.find (fun item -> item.Id = fn.Body.ResultNodeId)
            Assert.Equal(ViewerNodeRole.Int64ToFloat64, node.Role)
            Assert.Equal<ValueDependencyRole list>([ ValueDependencyRole.Source ], node.ValueDependencies |> List.map (fun item -> item.Role))
            Assert.Contains("int64 to float64", ViewerHtml.renderFocusDocument viewer)
            session.Close()
            let encoded = File.ReadAllText path
            Assert.Contains("\"kind\":\"int64ToFloat64\"", encoded)
            let graph = SnapshotCodec.deserialize encoded |> unwrap
            Assert.Empty(SnapshotValidator.validate graph)
            Assert.Equal(encoded, SnapshotCodec.serialize graph |> unwrap)
            let nyason = NyasonCodec.serialize graph |> unwrap
            Assert.Contains("int64-to-float64", nyason)
            let restored = NyasonCodec.deserialize nyason |> unwrap
            Assert.Equal(encoded, SnapshotCodec.serialize restored |> unwrap)
            Assert.Equal(SemanticFingerprint.compute graph, SemanticFingerprint.compute restored)
            let typed = TypedSemanticIr.project restored |> unwrap
            Assert.Contains("\"kind\":\"int64ToFloat64\"", TypedSemanticIrSerializer.serialize typed)
            Assert.Contains("\"kind\":\"int64ToFloat64\"", ExecutionIr.lower typed |> unwrap |> ExecutionIrSerializer.serialize)
            use reopened = AuthoringSession.openExisting path |> unwrap
            Assert.Equal(fn.Body.ResultNodeId, (find reopened "named").Body.ResultNodeId))

    [<Fact>]
    let ``malformed Float64 run values are protocol errors`` () =
        withSession (fun session _ ->
            let identity = create session "identity" [ "value", floatType ] floatType (slot 0)
            let run value = sprintf """{"id":"run","op":"runPure","functionId":"%s","arguments":[%s]}""" (StableId.value identity.Id) value
            for value in
                [ """{"kind":"float64","value":"1.5"}"""; """{"kind":"float64","value":"nan"}"""; """{"kind":"float64","value":"+0"}"""
                  """{"kind":"float64","value":null}"""; """{"kind":"float64"}"""; """{"kind":"float64","value":1e400}"""
                  """{"kind":"float64","value":-1e400}"""; """{"kind":"float64","value":1,"extra":0}"""
                  """{"kind":"option","itemKind":"float64","hasValue":false}"""; """{"kind":"list","itemKind":"float64","items":[]}""" ] do
                Assert.Equal("ProtocolError", errorCode session (run value))
            // Float64 has no flat container form, so the flat Result form does not take it either.
            Assert.Equal("ProtocolError", errorCode session (run """{"kind":"result","okKind":"float64","errorKind":"string","isOk":true,"value":1}"""))
            // A Float64 where an Int64 is declared is a type error, not a conversion.
            let intIdentity = create session "intIdentity" [ "value", intType ] intType (slot 0)
            Assert.Equal("RunArgumentTypeMismatch",
                errorCode session (sprintf """{"id":"run","op":"runPure","functionId":"%s","arguments":[{"kind":"float64","value":1}]}""" (StableId.value intIdentity.Id))))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Int64ToFloat64 rounds to nearest even and Float64 values round-trip through runPure`` () =
        withSession (fun session path ->
            let convert = create session "convert" [ "value", intType ] floatType (toFloat (slot 0))
            let convertAll = create session "convertAll" [ "values", listOf intType ] (listOf floatType) (ExpressionRecipe.ListMap(slot 0, convert.Id, []))
            let identity = create session "identity" [ "values", listOf floatType ] (listOf floatType) (slot 0)
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            let run (fn: SemanticFunction) arguments = (PureRun.run reopened fn.Id arguments None |> unwrap).Actual
            let twoTo53 = 9007199254740992L
            let inputs = [ 0L; -1L; 1L; -7L; twoTo53; -twoTo53; twoTo53 + 1L; twoTo53 + 2L; twoTo53 + 3L; Int64.MaxValue; Int64.MinValue; Int64.MaxValue - 511L; Int64.MaxValue - 512L ]
            // Above 2^53 the spacing is 2: 2^53 + 1 ties to the even 2^53 and 2^53 + 3 to 2^53 + 4.
            // Below 2^63 the spacing is 1024: Int64.MaxValue rounds up to 2^63, MaxValue - 511 is the
            // midpoint 2^63 - 512 and ties to the even 2^63, and MaxValue - 512 rounds down to 2^63 - 1024.
            let expected =
                [ 0.0; -1.0; 1.0; -7.0; 9007199254740992.0; -9007199254740992.0; 9007199254740992.0; 9007199254740994.0
                  9007199254740996.0; 9223372036854775808.0; -9223372036854775808.0; 9223372036854775808.0; 9223372036854774784.0 ]
            Assert.Equal(PureRunValue.ListValue(floatType, expected |> List.map float64), run convertAll [ PureRunValue.Int64List inputs ])
            // Python agrees: float(2**53 + 1) == 2**53 and float(2**63 - 1) == 2**63.
            Assert.Equal(float64 (float (twoTo53 + 1L)), float64 9007199254740992.0)
            // Special values survive the runner bit for bit, including -0.0 and the smallest subnormal.
            let specials = [ nan; infinity; -infinity; -0.0; 0.0; 0.1; 1e308; 5e-324; -2.5 ] |> List.map float64
            Assert.Equal(PureRunValue.ListValue(floatType, specials), run identity [ PureRunValue.ListValue(floatType, specials) ])
            // The wire form: finite values are shortest round-trip JSON numbers, the rest strings.
            let items =
                """["NaN","Infinity","-Infinity","-0",0,0.1,1E+308,5E-324,-2.5,1e-400]"""
                |> JsonDocument.Parse |> fun document -> document.RootElement.EnumerateArray() |> Seq.map (fun item -> sprintf """{"kind":"float64","value":%s}""" (item.GetRawText())) |> String.concat ","
            let value = sprintf """{"kind":"list","itemType":%s,"items":[%s]}""" (primitive "float64") items
            use response =
                send reopened (sprintf """{"id":"run","op":"runPure","functionId":"%s","arguments":[%s],"expected":%s}""" (StableId.value identity.Id) value value)
            let result = response.RootElement.GetProperty("result")
            Assert.True(result.GetProperty("matches").GetBoolean(), result.ToString())
            let actual = result.GetProperty("actual").GetProperty("items") |> fun items -> items.EnumerateArray() |> Seq.map (fun item -> item.GetProperty("value").GetRawText()) |> Seq.toList
            // 1e-400 is below the subnormal range and reads as 0 (round to nearest), not as an error.
            Assert.Equal<string list>([ "\"NaN\""; "\"Infinity\""; "\"-Infinity\""; "\"-0\""; "0"; "0.1"; "1E+308"; "5E-324"; "-2.5"; "0" ], actual))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Float64 results nest in Products Lists and Options and match expectations bitwise`` () =
        withSession (fun session path ->
            let stats =
                session.SimulateCreateType
                    { TypeParameters = []; ExpectedBaseSnapshotId = current session; DisplayName = "Stats"
                      Kind = AgentTypeDeclarationKindIntent.Product
                        [ { DisplayName = "median"; Type = floatType }; { DisplayName = "values"; Type = listOf floatType }; { DisplayName = "first"; Type = optionOf floatType } ] }
                |> unwrap |> publish session
                session.Declarations().Types |> List.find (fun item -> item.DisplayName = "Stats")
            let field name =
                match stats.Kind with
                | SemanticTypeKind.Product fields -> (fields |> List.find (fun item -> item.DisplayName = name)).Id
                | SemanticTypeKind.Union _ -> failwith "Expected a Product."
            // median = (float(a) + float(b)) / 2.0, as Python's statistics.median of two items.
            let median =
                ExpressionRecipe.Float64Divide(
                    ExpressionRecipe.Float64Add(toFloat (slot 0), toFloat (slot 1)), ExpressionRecipe.Float64Literal(Float64Bits.ofFloat 2.0))
            create session "stats" [ "a", intType; "b", intType ] (TypeRef.Named(stats.Id, []))
                (ExpressionRecipe.ProductConstruct(stats.Id, [],
                    [ field "median", median
                      field "values", ExpressionRecipe.ListConstruct(floatType, [ toFloat (slot 0); toFloat (slot 1) ])
                      field "first", ExpressionRecipe.OptionSome(toFloat (slot 0)) ])) |> ignore
            create session "divide" [ "a", floatType; "b", floatType ] floatType (ExpressionRecipe.Float64Divide(slot 0, slot 1)) |> ignore
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            let statsId = StableId.value stats.Id
            let expectedStats medianValue =
                sprintf """{"kind":"product","type":"Stats","fields":[{"field":"median","value":{"kind":"float64","value":%s}},{"field":"values","value":{"kind":"list","itemType":%s,"items":[{"kind":"float64","value":3},{"kind":"float64","value":4}]}},{"field":"first","value":{"kind":"option","itemType":%s,"hasValue":true,"value":{"kind":"float64","value":3}}}]}"""
                    medianValue (primitive "float64") (primitive "float64")
            let runByName name arguments expected =
                let response = send reopened (sprintf """{"id":"run","op":"runPureByName","function":"%s","arguments":[%s],"expected":%s}""" name arguments expected)
                response.RootElement.GetProperty("result").Clone()
            let ints = """{"kind":"int64","value":3},{"kind":"int64","value":4}"""
            let matched = runByName "stats" ints (expectedStats "3.5")
            Assert.True(matched.GetProperty("matches").GetBoolean(), matched.ToString())
            Assert.Equal(statsId, matched.GetProperty("actual").GetProperty("typeId").GetString())
            let medianField =
                matched.GetProperty("actual").GetProperty("fields").EnumerateArray() |> Seq.find (fun item -> item.GetProperty("field").GetString() = "median")
            Assert.Equal("3.5", medianField.GetProperty("value").GetProperty("value").GetRawText())
            // No tolerance: 3.5000000000000004 is a different binary64 and the mismatch names the field.
            let missed = runByName "stats" ints (expectedStats "3.5000000000000004")
            Assert.False(missed.GetProperty("matches").GetBoolean())
            let mismatch = missed.GetProperty("mismatch")
            Assert.Equal("median", mismatch.GetProperty("path").[0].GetProperty("field").GetString())
            Assert.Equal("3.5000000000000004", mismatch.GetProperty("expected").GetProperty("value").GetRawText())
            Assert.Equal("3.5", mismatch.GetProperty("actual").GetProperty("value").GetRawText())
            let divide a b expected =
                runByName "divide" (sprintf """{"kind":"float64","value":%s},{"kind":"float64","value":%s}""" a b) (sprintf """{"kind":"float64","value":%s}""" expected)
            // Every NaN matches NaN; -0.0 and 0.0 are different values although Float64Equal holds.
            Assert.True((divide "0" "0" "\"NaN\"").GetProperty("matches").GetBoolean())
            Assert.True((divide "-1" "\"Infinity\"" "\"-0\"").GetProperty("matches").GetBoolean())
            let signed = divide "-1" "\"Infinity\"" "0"
            Assert.False(signed.GetProperty("matches").GetBoolean())
            Assert.Equal("\"-0\"", signed.GetProperty("mismatch").GetProperty("actual").GetProperty("value").GetRawText())
            Assert.True((divide "1" "0" "\"Infinity\"").GetProperty("matches").GetBoolean())
            Assert.True((divide "1" "3" "0.3333333333333333").GetProperty("matches").GetBoolean()))

    [<Fact; Trait("Suite", "Extended")>]
    let ``fake capability cases key and log Float64 arguments bitwise`` () =
        withSession (fun session path ->
            session.SimulateCreateEffect { ExpectedBaseSnapshotId = current session; DisplayName = "IO" } |> unwrap |> publish session
            let effect = session.Declarations().Effects |> List.exactlyOne
            session.SimulateCreateCapability
                { ExpectedBaseSnapshotId = current session; DisplayName = "Scale"
                  Operations =
                    [ { DisplayName = "scale"; Parameters = [ { DisplayName = "value"; Type = floatType } ]
                        ResultType = floatType; FailureType = None; EffectId = effect.Id } ] }
            |> unwrap |> publish session
            let capability = session.Declarations().Capabilities |> List.exactlyOne
            let operation = capability.Operations.Head
            let parameter = operation.Parameters.Head.Id
            let invoke value = ExpressionRecipe.CapabilityInvoke(slot 0, operation.Id, [ parameter, value ])
            let bound depth = ExpressionRecipe.PatternReference { ScopeDepth = depth; Slot = RecipePatternSlot.Value }
            let scaleAll =
                createWith session "scaleAll" [ "scale", TypeRef.Capability capability.Id; "a", floatType; "b", floatType; "c", floatType ]
                    (listOf floatType) [ effect.Id ]
                    // Bind orders the three calls; ScopeDepth 0 is the innermost bound value.
                    (ExpressionRecipe.Bind(invoke (slot 1), ExpressionRecipe.Bind(invoke (slot 2), ExpressionRecipe.Bind(invoke (slot 3),
                        ExpressionRecipe.ListConstruct(floatType, [ bound 2; bound 1; bound 0 ])))))
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap
            let case (key: float) (response: float): PureRunFakeCase =
                { Arguments = [ parameter, float64 key ]; Responses = [ PureRunFakeResponse.Return(float64 response) ] }
            // NaN keys match any NaN argument; -0.0 and 0.0 select different cases.
            let fake =
                PureRunValue.FakeCapability(capability.Id,
                    [ { OperationId = operation.Id; Responses = []; Cases = Some [ case nan 1.0; case -0.0 2.0; case 0.0 infinity ] } ])
            let result = PureRun.run reopened scaleAll.Id [ fake; float64 (-nan); float64 0.0; float64 -0.0 ] None |> unwrap
            Assert.Equal(PureRunValue.ListValue(floatType, [ float64 1.0; float64 infinity; float64 2.0 ]), result.Actual)
            Assert.Equal<PureRunValue list>([ float64 nan; float64 0.0; float64 -0.0 ], result.Calls |> List.map (fun call -> call.Arguments |> List.exactlyOne |> snd)))
