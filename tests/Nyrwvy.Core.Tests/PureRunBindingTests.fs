namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// runPure with capability parameters bound to scripted fakes instead of a handwritten host.
module PureRunBindingTests =
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private unitType = TypeRef.Primitive PrimitiveType.Unit

    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    type private Fixture =
        { Session: AuthoringSession
          Path: string
          Request: SemanticType
          Key: SemanticField
          Response: SemanticType
          Value: SemanticField
          Provider: SemanticCapability
          Lookup: SemanticCapabilityOperation
          Fallback: SemanticCapabilityOperation
          Log: SemanticCapability
          Write: SemanticCapabilityOperation
          Metric: SemanticCapability
          Function: string -> SemanticFunction }

    let private current (session: AuthoringSession) = (session.Status() |> unwrap "status").SnapshotId

    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore

    let private createType (session: AuthoringSession) name fieldName =
        let request: AgentCreateTypeDeclarationRequest =
            { ExpectedBaseSnapshotId = current session
              DisplayName = name
              Kind = AgentTypeDeclarationKindIntent.Product [ { DisplayName = fieldName; Type = stringType } ] }
        session.SimulateCreateType request |> unwrap "create type" |> publish session
        let entity = session.Declarations().Types |> List.find (fun item -> item.DisplayName = name)
        match entity.Kind with
        | SemanticTypeKind.Product [ field ] -> entity, field
        | _ -> failwith "Expected one product field."

    let private createCapability (session: AuthoringSession) name operations =
        let request: AgentCreateCapabilityDeclarationRequest =
            { ExpectedBaseSnapshotId = current session; DisplayName = name; Operations = operations }
        session.SimulateCreateCapability request |> unwrap "create capability" |> publish session
        session.Declarations().Capabilities |> List.find (fun item -> item.DisplayName = name)

    let private createFunction (session: AuthoringSession) name parameters resultType effects body =
        let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = current session
              DisplayName = name
              Parameters = parameters |> List.map (fun (displayName, itemType) -> { DisplayName = displayName; Type = itemType })
              ResultType = resultType
              RequiredEffects = effects
              Body = body }
        session.SimulateCreateFunction request |> unwrap ("create " + name) |> publish session

    let private withFixture run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-binding-run-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            let session = AuthoringSession.createNew path |> unwrap "session"
            use _ = session
            let requestType, key = createType session "LookupRequest" "key"
            let responseType, value = createType session "LookupResponse" "value"
            let effectRequest: AgentCreateEffectDeclarationRequest = { ExpectedBaseSnapshotId = current session; DisplayName = "External" }
            session.SimulateCreateEffect effectRequest |> unwrap "create effect" |> publish session
            let effect = session.Declarations().Effects |> List.exactlyOne
            let lookupResult = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ TypeRef.Named responseType.Id; stringType ])
            let operation name parameters resultType: AgentCapabilityOperationIntent =
                { DisplayName = name
                  Parameters = parameters |> List.map (fun (displayName, itemType) -> { DisplayName = displayName; Type = itemType })
                  ResultType = resultType
                  FailureType = None
                  EffectId = effect.Id }
            let provider =
                createCapability session "LookupProvider"
                    [ operation "lookup" [ "request", TypeRef.Named requestType.Id ] lookupResult
                      operation "fallback" [] lookupResult ]
            let log = createCapability session "Log" [ operation "write" [ "message", stringType ] unitType ]
            let metric = createCapability session "Metric" [ operation "record" [ "value", TypeRef.Primitive PrimitiveType.Float64 ] unitType ]
            let lookup = provider.Operations |> List.find (fun item -> item.DisplayName = "lookup")
            let fallback = provider.Operations |> List.find (fun item -> item.DisplayName = "fallback")
            let write = List.exactlyOne log.Operations
            let valueSlot = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Value }
            let errorSlot = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Error }
            createFunction session "query"
                [ "request", TypeRef.Named requestType.Id; "provider", TypeRef.Capability provider.Id ] stringType [ effect.Id ]
                (ExpressionRecipe.MatchResult(
                    ExpressionRecipe.CapabilityInvoke(ExpressionRecipe.ParameterSlot 1, lookup.Id,
                        [ (List.exactlyOne lookup.Parameters).Id, ExpressionRecipe.ParameterSlot 0 ]),
                    ExpressionRecipe.FieldAccess(valueSlot, value.Id),
                    errorSlot))
            let message = (List.exactlyOne write.Parameters).Id
            let writeWith text = ExpressionRecipe.CapabilityInvoke(ExpressionRecipe.ParameterSlot 1, write.Id, [ message, text ])
            createFunction session "logTwice" [ "message", stringType; "log", TypeRef.Capability log.Id ] unitType [ effect.Id ]
                (ExpressionRecipe.Bind(writeWith (ExpressionRecipe.ParameterSlot 0),
                    writeWith (ExpressionRecipe.StringConcat(ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.StringLiteral "!"))))
            createFunction session "measure" [ "metric", TypeRef.Capability metric.Id ] (TypeRef.Primitive PrimitiveType.Bool) []
                (ExpressionRecipe.BoolLiteral true)
            createFunction session "declaresEffect" [ "text", stringType ] stringType [ effect.Id ] (ExpressionRecipe.ParameterSlot 0)
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap "reopen"
            let functionNamed name = reopened.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
            run { Session = reopened; Path = path; Request = requestType; Key = key; Response = responseType; Value = value
                  Provider = provider; Lookup = lookup; Fallback = fallback; Log = log; Write = write; Metric = metric
                  Function = functionNamed }
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let private request (fixture: Fixture) key = PureRunValue.Product(fixture.Request.Id, [ fixture.Key.Id, PureRunValue.String key ])

    let private found (fixture: Fixture) text =
        PureRunValue.ResultValue(TypeRef.Named fixture.Response.Id, stringType, true,
            PureRunValue.Product(fixture.Response.Id, [ fixture.Value.Id, PureRunValue.String text ]))

    let private missing (fixture: Fixture) text =
        PureRunValue.ResultValue(TypeRef.Named fixture.Response.Id, stringType, false, PureRunValue.String text)

    let private provider (fixture: Fixture) responses =
        PureRunValue.FakeCapability(fixture.Provider.Id, [ { OperationId = fixture.Lookup.Id; Responses = responses } ])

    [<Fact>]
    let ``fake bindings are checked against capability declarations before building`` () =
        withFixture (fun fixture ->
            let before = File.ReadAllText fixture.Path
            let reject name arguments code =
                match PureRun.run fixture.Session (fixture.Function name).Id arguments None with
                | Error(PureRunError.Unsupported(actual, _)) -> Assert.Equal(code, actual)
                | other -> failwithf "Invalid binding accepted for %s: %A" name other
            let query = request fixture "k"
            reject "query" [ query; PureRunValue.String "not a capability" ] "RunArgumentTypeMismatch"
            reject "query" [ query; PureRunValue.FakeCapability(fixture.Log.Id, []) ] "RunArgumentTypeMismatch"
            let foreign = { OperationId = fixture.Write.Id; Responses = [] }
            reject "query" [ query; PureRunValue.FakeCapability(fixture.Provider.Id, [ foreign ]) ] "RunBindingShapeMismatch"
            let twice = { OperationId = fixture.Lookup.Id; Responses = [] }
            reject "query" [ query; PureRunValue.FakeCapability(fixture.Provider.Id, [ twice; twice ]) ] "RunBindingShapeMismatch"
            reject "query" [ query; provider fixture [ PureRunFakeResponse.Return(PureRunValue.String "wrong type") ] ] "RunBindingShapeMismatch"
            reject "query" [ query; provider fixture [ PureRunFakeResponse.Return(PureRunValue.FakeCapability(fixture.Log.Id, [])) ] ] "RunBindingPlacement"
            reject "query" [ query; provider fixture [ PureRunFakeResponse.Throw(String([| char 0xD800 |])) ] ] "RunInvalidString"
            reject "measure" [ PureRunValue.FakeCapability(fixture.Metric.Id, []) ] "RunBindingTypeUnsupported"
            match PureRun.run fixture.Session (fixture.Function "declaresEffect").Id [ PureRunValue.String "x" ] (Some(PureRunValue.FakeCapability(fixture.Log.Id, []))) with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunExpectedTypeMismatch", code)
            | other -> failwithf "Fake expectation accepted: %A" other
            let wire response =
                $"""{{"id":"fake","op":"runPure","functionId":"{StableId.value (fixture.Function "logTwice").Id}","arguments":[{{"kind":"string","value":"m"}},{{"kind":"fakeCapability","capabilityId":"{StableId.value fixture.Log.Id}","operations":[{{"operationId":"{StableId.value fixture.Write.Id}","responses":[{response}]}}]}}]}}"""
            for response in [ """{"kind":"return"}"""; """{"kind":"yield","value":{"kind":"unit"}}"""; """{"kind":"throw","message":"x","value":{"kind":"unit"}}"""; """{"kind":"return","value":{"kind":"unit","extra":1}}""" ] do
                use document = AuthoringProtocol.executeLine fixture.Session (wire response) |> JsonDocument.Parse
                Assert.False(document.RootElement.GetProperty("ok").GetBoolean())
                Assert.Equal("ProtocolError", document.RootElement.GetProperty("error").GetProperty("code").GetString())
            Assert.Equal(before, File.ReadAllText fixture.Path))

    [<Fact; Trait("Suite", "Extended")>]
    let ``runPure runs effectful functions against scripted fakes and records calls`` () =
        withFixture (fun fixture ->
            let before = File.ReadAllText fixture.Path
            let query = (fixture.Function "query").Id
            let providerParameter = ((fixture.Function "query").Parameters |> List.item 1).Id
            let requestArgument = (List.exactlyOne fixture.Lookup.Parameters).Id
            let result = PureRun.run fixture.Session query [ request fixture "yes"; provider fixture [ PureRunFakeResponse.Return(found fixture "found") ] ] (Some(PureRunValue.String "found")) |> unwrap "found"
            Assert.Equal(Some true, result.Matches)
            Assert.Equal<PureRunCall list>(
                [ { ParameterId = providerParameter; OperationId = fixture.Lookup.Id; Arguments = [ requestArgument, request fixture "yes" ] } ],
                result.Calls)
            let failed = PureRun.run fixture.Session query [ request fixture "no"; provider fixture [ PureRunFakeResponse.Return(missing fixture "missing") ] ] None |> unwrap "missing"
            Assert.Equal(PureRunValue.String "missing", failed.Actual)
            match PureRun.run fixture.Session query [ request fixture "k"; provider fixture [] ] None with
            | Error(PureRunError.RuntimeFailure failure) ->
                Assert.True(failure.BindingExhausted)
                Assert.Equal<PureRunFrame list>([ PureRunFrame.CapabilityOperation fixture.Lookup.Id; PureRunFrame.Function query ], failure.Frames)
                Assert.Equal(1, failure.Calls.Length)
            | other -> failwithf "Exhausted fake was not reported: %A" other
            match PureRun.run fixture.Session query [ request fixture "k"; provider fixture [ PureRunFakeResponse.Throw "service unavailable" ] ] None with
            | Error(PureRunError.RuntimeFailure failure) ->
                Assert.False(failure.BindingExhausted)
                Assert.Equal("FakeCapabilityException", failure.ExceptionType)
                Assert.Equal("service unavailable", failure.Message)
                Assert.Equal<PureRunFrame list>([ PureRunFrame.CapabilityOperation fixture.Lookup.Id; PureRunFrame.Function query ], failure.Frames)
            | other -> failwithf "Throwing fake was not a runtime failure: %A" other
            let effectOnly = PureRun.run fixture.Session (fixture.Function "declaresEffect").Id [ PureRunValue.String "x" ] (Some(PureRunValue.String "x")) |> unwrap "declared effect"
            Assert.Equal(Some true, effectOnly.Matches)
            Assert.Empty(effectOnly.Calls)
            let logTwice = fixture.Function "logTwice"
            let messageArgument = (List.exactlyOne fixture.Write.Parameters).Id
            let unitReturn = """{"kind":"return","value":{"kind":"unit"}}"""
            let line =
                $"""{{"id":"log","op":"runPure","functionId":"{StableId.value logTwice.Id}","arguments":[{{"kind":"string","value":"雪"}},{{"kind":"fakeCapability","capabilityId":"{StableId.value fixture.Log.Id}","operations":[{{"operationId":"{StableId.value fixture.Write.Id}","responses":[{unitReturn},{unitReturn}]}}]}}],"expected":{{"kind":"unit"}}}}"""
            use response = AuthoringProtocol.executeLine fixture.Session line |> JsonDocument.Parse
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
            let resultElement = response.RootElement.GetProperty("result")
            Assert.True(resultElement.GetProperty("matches").GetBoolean())
            Assert.Equal("unit", resultElement.GetProperty("actual").GetProperty("kind").GetString())
            let calls = resultElement.GetProperty("calls").EnumerateArray() |> Seq.toList
            Assert.Equal(2, calls.Length)
            let logParameter = (logTwice.Parameters |> List.item 1).Id
            calls |> List.iteri (fun index call ->
                Assert.Equal(StableId.value logParameter, call.GetProperty("parameterId").GetString())
                Assert.Equal(StableId.value fixture.Write.Id, call.GetProperty("operationId").GetString())
                let argument = call.GetProperty("arguments").[0]
                Assert.Equal(StableId.value messageArgument, argument.GetProperty("capabilityParameterId").GetString())
                Assert.Equal((if index = 0 then "雪" else "雪!"), argument.GetProperty("value").GetProperty("value").GetString()))
            let exhausted = line.Replace($"[{unitReturn},{unitReturn}]", $"[{unitReturn}]")
            use response = AuthoringProtocol.executeLine fixture.Session exhausted |> JsonDocument.Parse
            let error = response.RootElement.GetProperty("error")
            Assert.Equal("RunBindingExhausted", error.GetProperty("code").GetString())
            Assert.Equal(2, error.GetProperty("calls").GetArrayLength())
            let innermost = error.GetProperty("frames").[0]
            Assert.Equal(StableId.value fixture.Write.Id, innermost.GetProperty("operationId").GetString())
            Assert.Equal(before, File.ReadAllText fixture.Path))
