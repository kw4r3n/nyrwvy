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
          Issuer: SemanticCapability
          Function: string -> SemanticFunction }

    let private current (session: AuthoringSession) = (session.Status() |> unwrap "status").SnapshotId

    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore

    let private createType (session: AuthoringSession) name fieldName =
        let request: AgentCreateTypeDeclarationRequest =
            { Exported = true; TypeParameters = []; ExpectedBaseSnapshotId = current session
              DisplayName = name
              Kind = AgentTypeDeclarationKindIntent.Product [ { DisplayName = fieldName; Type = stringType } ] }
        session.SimulateCreateType request |> unwrap "create type" |> publish session
        let entity = session.Declarations().Types |> List.find (fun item -> item.DisplayName = name)
        match entity.Kind with
        | SemanticTypeKind.Product [ field ] -> entity, field
        | _ -> failwith "Expected one product field."

    let private createCapability (session: AuthoringSession) name operations =
        let request: AgentCreateCapabilityDeclarationRequest =
            { TaskBound = false; Exported = true; ExpectedBaseSnapshotId = current session; DisplayName = name; Operations = operations }
        session.SimulateCreateCapability request |> unwrap "create capability" |> publish session
        session.Declarations().Capabilities |> List.find (fun item -> item.DisplayName = name)

    let private createFunction (session: AuthoringSession) name parameters resultType effects body =
        let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = current session
              TypeParameters = None
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
            let effectRequest: AgentCreateEffectDeclarationRequest = { Exported = true; ExpectedBaseSnapshotId = current session; DisplayName = "External" }
            session.SimulateCreateEffect effectRequest |> unwrap "create effect" |> publish session
            let effect = session.Declarations().Effects |> List.exactlyOne
            let lookupResult = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ TypeRef.Named(responseType.Id, []); stringType ])
            let operation name parameters resultType: AgentCapabilityOperationIntent =
                { MayWait = false; DisplayName = name
                  Parameters = parameters |> List.map (fun (displayName, itemType) -> { DisplayName = displayName; Type = itemType })
                  ResultType = resultType
                  FailureType = None
                  EffectId = effect.Id }
            let provider =
                createCapability session "LookupProvider"
                    [ operation "lookup" [ "request", TypeRef.Named(requestType.Id, []) ] lookupResult
                      operation "fallback" [] lookupResult ]
            let log = createCapability session "Log" [ operation "write" [ "message", stringType ] unitType ]
            // Nine nested Lists exceed the eight composite layers a run value type may have.
            let rec nestedLists depth = if depth = 0 then TypeRef.Primitive PrimitiveType.Int64 else TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ nestedLists (depth - 1) ])
            let metric = createCapability session "Metric" [ operation "record" [ "value", nestedLists 9 ] unitType ]
            // A host handle: Issuer returns and takes Log capability values, which are not run values.
            let issuer =
                createCapability session "Issuer"
                    [ operation "open" [ "name", stringType ] (TypeRef.Capability log.Id)
                      operation "close" [ "log", TypeRef.Capability log.Id; "note", stringType ] unitType ]
            let lookup = provider.Operations |> List.find (fun item -> item.DisplayName = "lookup")
            let fallback = provider.Operations |> List.find (fun item -> item.DisplayName = "fallback")
            let write = List.exactlyOne log.Operations
            let valueSlot = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Value }
            let errorSlot = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = RecipePatternSlot.Error }
            createFunction session "query"
                [ "request", TypeRef.Named(requestType.Id, []); "provider", TypeRef.Capability provider.Id ] stringType [ effect.Id ]
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
            let close = issuer.Operations |> List.find (fun item -> item.DisplayName = "close")
            let closeArgument name = (close.Parameters |> List.find (fun item -> item.DisplayName = name)).Id
            createFunction session "closeWith" [ "issuer", TypeRef.Capability issuer.Id; "log", TypeRef.Capability log.Id ] unitType [ effect.Id ]
                (ExpressionRecipe.CapabilityInvoke(ExpressionRecipe.ParameterSlot 0, close.Id,
                    [ closeArgument "log", ExpressionRecipe.ParameterSlot 1; closeArgument "note", ExpressionRecipe.StringLiteral "done" ]))
            createFunction session "declaresEffect" [ "text", stringType ] stringType [ effect.Id ] (ExpressionRecipe.ParameterSlot 0)
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap "reopen"
            let functionNamed name = reopened.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
            run { Session = reopened; Path = path; Request = requestType; Key = key; Response = responseType; Value = value
                  Provider = provider; Lookup = lookup; Fallback = fallback; Log = log; Write = write; Metric = metric; Issuer = issuer
                  Function = functionNamed }
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let private request (fixture: Fixture) key = PureRunValue.Product(fixture.Request.Id, [], [ fixture.Key.Id, PureRunValue.String key ])

    let private found (fixture: Fixture) text =
        PureRunValue.ResultValue(TypeRef.Named(fixture.Response.Id, []), stringType, true,
            PureRunValue.Product(fixture.Response.Id, [], [ fixture.Value.Id, PureRunValue.String text ]))

    let private missing (fixture: Fixture) text =
        PureRunValue.ResultValue(TypeRef.Named(fixture.Response.Id, []), stringType, false, PureRunValue.String text)

    let private provider (fixture: Fixture) responses =
        PureRunValue.FakeCapability(fixture.Provider.Id, [ { OperationId = fixture.Lookup.Id; Cases = None; Responses = responses } ])

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
            let foreign = { OperationId = fixture.Write.Id; Cases = None; Responses = [] }
            reject "query" [ query; PureRunValue.FakeCapability(fixture.Provider.Id, [ foreign ]) ] "RunBindingShapeMismatch"
            let twice = { OperationId = fixture.Lookup.Id; Cases = None; Responses = [] }
            reject "query" [ query; PureRunValue.FakeCapability(fixture.Provider.Id, [ twice; twice ]) ] "RunBindingShapeMismatch"
            reject "query" [ query; provider fixture [ PureRunFakeResponse.Return(PureRunValue.String "wrong type") ] ] "RunBindingShapeMismatch"
            reject "query" [ query; provider fixture [ PureRunFakeResponse.Return(PureRunValue.FakeCapability(fixture.Log.Id, [])) ] ] "RunBindingPlacement"
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

    /// A host implementation module, written against the generated interface as a host author would.
    let private hostSource (fixture: Fixture) =
        let capability = CSharpBackendNames.capabilityName fixture.Provider.Id
        let request = CSharpBackendNames.typeName fixture.Request.Id
        let response = CSharpBackendNames.typeName fixture.Response.Id
        let lookup = CSharpBackendNames.capabilityOperationName fixture.Lookup.Id
        let fallback = CSharpBackendNames.capabilityOperationName fixture.Fallback.Id
        let argument = CSharpBackendNames.capabilityParameterName (List.exactlyOne fixture.Lookup.Parameters).Id
        let key = CSharpBackendNames.fieldName fixture.Key.Id
        $"""using System;
using Nyrwvy.Generated;

namespace Example.Hosts;

public sealed class Directory : {capability}
{{
    public NyResult<{response}, NyStr> {lookup}({request} {argument}) =>
        {argument}.{key} == "yes"
            ? NyResult<{response}, NyStr>.Ok(new {response}("found"))
            : NyResult<{response}, NyStr>.Err("missing");
    public NyResult<{response}, NyStr> {fallback}() => NyResult<{response}, NyStr>.Err("fallback");
}}

public sealed class Broken : {capability}
{{
    public NyResult<{response}, NyStr> {lookup}({request} {argument}) => throw new InvalidOperationException("directory offline");
    public NyResult<{response}, NyStr> {fallback}() => throw new InvalidOperationException("directory offline");
}}

public sealed class Noisy : {capability}
{{
    public NyResult<{response}, NyStr> {lookup}({request} {argument}) {{ Console.Error.Write(new string('x', 2 * 1048576)); return NyResult<{response}, NyStr>.Err("noisy"); }}
    public NyResult<{response}, NyStr> {fallback}() => NyResult<{response}, NyStr>.Err("noisy");
}}

public sealed class Loud : {capability}
{{
    public NyResult<{response}, NyStr> {lookup}({request} {argument}) {{ Console.Out.Write(new string('x', 2 * 1048576)); return NyResult<{response}, NyStr>.Err("loud"); }}
    public NyResult<{response}, NyStr> {fallback}() => NyResult<{response}, NyStr>.Err("loud");
}}
"""

    [<Fact>]
    let ``host sources are disabled unless the session opted in and nothing is read or built`` () =
        withFixture (fun fixture ->
            let before = File.ReadAllText fixture.Path
            let query = (fixture.Function "query").Id
            Assert.False(fixture.Session.HostSourcesAllowed)
            let host = PureRunValue.HostCapability(fixture.Provider.Id, "Example.Hosts.Directory", false)
            let reject arguments sources =
                match PureRun.runWithHost fixture.Session query arguments None sources with
                | Error(PureRunError.Unsupported(actual, _)) -> Assert.Equal("RunHostSourcesDisabled", actual)
                | other -> failwithf "Host source accepted without opt-in: %A" other
            // Missing files prove the gate rejects before any source is read.
            reject [ request fixture "k"; host ] [ "Missing.cs" ]
            reject [ request fixture "k"; host ] []
            reject [ request fixture "k"; provider fixture [] ] [ "Missing.cs" ]
            let line =
                $"""{{"id":"host","op":"runPure","functionId":"{StableId.value query}","hostSources":["Missing.cs"],"arguments":[{{"kind":"product","typeId":"{StableId.value fixture.Request.Id}","fields":[{{"fieldId":"{StableId.value fixture.Key.Id}","value":{{"kind":"string","value":"k"}}}}]}},{{"kind":"hostCapability","capabilityId":"{StableId.value fixture.Provider.Id}","implementation":"Example.Hosts.Directory"}}]}}"""
            use response = AuthoringProtocol.executeLine fixture.Session line |> JsonDocument.Parse
            Assert.False(response.RootElement.GetProperty("ok").GetBoolean())
            Assert.Equal("RunHostSourcesDisabled", response.RootElement.GetProperty("error").GetProperty("code").GetString())
            Assert.Equal(before, File.ReadAllText fixture.Path))

    [<Fact>]
    let ``host bindings require explicit readable sources and a plain type name before building`` () =
        withFixture (fun fixture ->
            fixture.Session.HostSourcesAllowed <- true
            let before = File.ReadAllText fixture.Path
            let query = (fixture.Function "query").Id
            let host name = PureRunValue.HostCapability(fixture.Provider.Id, name, false)
            let reject arguments sources code =
                match PureRun.runWithHost fixture.Session query arguments None sources with
                | Error(PureRunError.Unsupported(actual, _)) -> Assert.Equal(code, actual)
                | other -> failwithf "Invalid host binding accepted: %A" other
            let source = Path.Combine(Path.GetDirectoryName fixture.Path, "Hosts.cs")
            File.WriteAllText(source, hostSource fixture)
            reject [ request fixture "k"; host "Example.Hosts.Directory" ] [] "RunHostSourceInvalid"
            reject [ request fixture "k"; provider fixture [] ] [ "Hosts.cs" ] "RunHostSourceInvalid"
            reject [ request fixture "k"; host "Example.Hosts.Directory" ] [ "Missing.cs" ] "RunHostSourceInvalid"
            reject [ request fixture "k"; host "Example.Hosts.Directory" ] [ fixture.Path ] "RunHostSourceInvalid"
            reject [ request fixture "k"; host "Example.Hosts.Directory(); System.Environment.Exit(0" ] [ "Hosts.cs" ] "RunBindingShapeMismatch"
            reject [ request fixture "k"; PureRunValue.HostCapability(fixture.Log.Id, "Example.Hosts.Directory", false) ] [ "Hosts.cs" ] "RunArgumentTypeMismatch"
            Assert.Equal(before, File.ReadAllText fixture.Path))

    [<Fact; Trait("Suite", "Extended")>]
    let ``runPure runs an explicitly registered host implementation and records its calls`` () =
        withFixture (fun fixture ->
            fixture.Session.HostSourcesAllowed <- true
            let before = File.ReadAllText fixture.Path
            let query = fixture.Function "query"
            File.WriteAllText(Path.Combine(Path.GetDirectoryName fixture.Path, "Hosts.cs"), hostSource fixture)
            let host name = PureRunValue.HostCapability(fixture.Provider.Id, name, false)
            let found = PureRun.runWithHost fixture.Session query.Id [ request fixture "yes"; host "Example.Hosts.Directory" ] (Some(PureRunValue.String "found")) [ "Hosts.cs" ] |> unwrap "found"
            Assert.Equal(Some true, found.Matches)
            Assert.Equal<PureRunCall list>(
                [ { ParameterId = (query.Parameters |> List.item 1).Id; OperationId = fixture.Lookup.Id
                    Arguments = [ (List.exactlyOne fixture.Lookup.Parameters).Id, request fixture "yes" ] } ],
                found.Calls)
            let missing = PureRun.runWithHost fixture.Session query.Id [ request fixture "no"; host "Example.Hosts.Directory" ] None [ "Hosts.cs" ] |> unwrap "missing"
            Assert.Equal(PureRunValue.String "missing", missing.Actual)
            match PureRun.runWithHost fixture.Session query.Id [ request fixture "k"; host "Example.Hosts.Broken" ] None [ "Hosts.cs" ] with
            | Error(PureRunError.RuntimeFailure failure) ->
                Assert.Equal("System.InvalidOperationException", failure.ExceptionType)
                Assert.Equal("directory offline", failure.Message)
                Assert.Equal<PureRunFrame list>([ PureRunFrame.CapabilityOperation fixture.Lookup.Id; PureRunFrame.CapabilityOperation fixture.Lookup.Id; PureRunFrame.Function query.Id ], failure.Frames)
                Assert.Equal(1, failure.Calls.Length)
            | other -> failwithf "Host exception was not a runtime failure: %A" other
            match PureRun.runWithHost fixture.Session query.Id [ request fixture "k"; host "Example.Hosts.Noisy" ] None [ "Hosts.cs" ] with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunOutputTooLarge", code)
            | other -> failwithf "2 MiB of host output was not refused: %A" other
            match PureRun.runWithHost fixture.Session query.Id [ request fixture "k"; host "Example.Hosts.Loud" ] None [ "Hosts.cs" ] with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunOutputTooLarge", code)
            | other -> failwithf "2 MiB of host standard output was not refused: %A" other
            let line =
                $"""{{"id":"host","op":"runPure","functionId":"{StableId.value query.Id}","hostSources":["Hosts.cs"],"arguments":[{{"kind":"product","typeId":"{StableId.value fixture.Request.Id}","fields":[{{"fieldId":"{StableId.value fixture.Key.Id}","value":{{"kind":"string","value":"yes"}}}}]}},{{"kind":"hostCapability","capabilityId":"{StableId.value fixture.Provider.Id}","implementation":"Example.Hosts.Directory"}}],"expected":{{"kind":"string","value":"found"}}}}"""
            use response = AuthoringProtocol.executeLine fixture.Session line |> JsonDocument.Parse
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
            Assert.True(response.RootElement.GetProperty("result").GetProperty("matches").GetBoolean())
            Assert.Equal(1, response.RootElement.GetProperty("result").GetProperty("calls").GetArrayLength())
            Assert.Equal(before, File.ReadAllText fixture.Path))

    let private keyed (fixture: Fixture) cases =
        PureRunValue.FakeCapability(fixture.Provider.Id, [ { OperationId = fixture.Lookup.Id; Responses = []; Cases = Some cases } ])

    let private lookupCase (fixture: Fixture) key responses: PureRunFakeCase =
        { Arguments = [ fixture.Lookup.Parameters.Head.Id, request fixture key ]; Responses = responses }

    [<Fact>]
    let ``keyed fakes reject incomplete mistyped duplicate and mixed scripts without changing state`` () =
        withFixture (fun fixture ->
            let before = File.ReadAllText fixture.Path
            let valid = lookupCase fixture "a" [ PureRunFakeResponse.Return(found fixture "ok") ]
            let check binding =
                match PureRun.run fixture.Session (fixture.Function "query").Id [ request fixture "a"; binding ] None with
                | Error(PureRunError.Unsupported _) -> ()
                | other -> failwithf "Invalid key was accepted: %A" other
            for cases in
                [ [ { valid with Arguments = [] } ]
                  [ { valid with Arguments = valid.Arguments @ valid.Arguments } ]
                  [ { valid with Arguments = [ fixture.Write.Parameters.Head.Id, request fixture "a" ] } ]
                  [ { valid with Arguments = [ fixture.Lookup.Parameters.Head.Id, PureRunValue.String "a" ] } ]
                  [ valid; valid ]
                  [ lookupCase fixture (String.replicate 65537 "x") [] ]
                  [ { valid with Responses = List.replicate 257 (PureRunFakeResponse.Throw "too many") } ] ] do
                check (keyed fixture cases)
            check (PureRunValue.FakeCapability(fixture.Provider.Id,
                [ { OperationId = fixture.Lookup.Id; Responses = valid.Responses; Cases = Some [ valid ] } ]))
            let capId, opId, functionId = StableId.value fixture.Provider.Id, StableId.value fixture.Lookup.Id, StableId.value (fixture.Function "query").Id
            for script in [ """"responses":[],"cases":[]"""; """"cases":null"""; """"cases":[{"arguments":[],"responses":[],"extra":true}]""" ] do
                let line = $"""{{"id":"bad-case","op":"runPure","functionId":"{functionId}","arguments":[{{"kind":"fakeCapability","capabilityId":"{capId}","operations":[{{"operationId":"{opId}",{script}}}]}}]}}"""
                use reply = AuthoringProtocol.executeLine fixture.Session line |> JsonDocument.Parse
                Assert.False(reply.RootElement.GetProperty("ok").GetBoolean())
                Assert.Equal("ProtocolError", reply.RootElement.GetProperty("error").GetProperty("code").GetString())
            Assert.Equal(before, File.ReadAllText fixture.Path))

    [<Fact; Trait("Suite", "Extended")>]
    let ``keyed fake queues are independent and distinguish no match from exhaustion`` () =
        withFixture (fun fixture ->
            let query = fixture.Function "query"
            let a = lookupCase fixture "a" [ PureRunFakeResponse.Return(found fixture "a1"); PureRunFakeResponse.Return(found fixture "a2") ]
            let b = lookupCase fixture "b" [ PureRunFakeResponse.Return(found fixture "b1") ]
            let invoke index = ExpressionRecipe.CapabilityInvoke(ExpressionRecipe.ParameterSlot 2, fixture.Lookup.Id,
                                    [ fixture.Lookup.Parameters.Head.Id, ExpressionRecipe.ParameterSlot index ])
            createFunction fixture.Session "interleaved"
                [ "a", TypeRef.Named(fixture.Request.Id, []); "b", TypeRef.Named(fixture.Request.Id, []); "provider", TypeRef.Capability fixture.Provider.Id ]
                fixture.Lookup.ResultType [ fixture.Lookup.EffectId ]
                (ExpressionRecipe.Bind(invoke 0, ExpressionRecipe.Bind(invoke 1, invoke 0)))
            for cases in [ [ a; b ]; [ b; a ] ] do
                let answer = PureRun.run fixture.Session (fixture.Function "interleaved").Id
                                [ request fixture "a"; request fixture "b"; keyed fixture cases ] (Some(found fixture "a2")) |> unwrap "independent queues"
                Assert.Equal(Some true, answer.Matches)
                Assert.Equal(3, answer.Calls.Length)
            for cases, exhausted in [ [ b ], false; [], false; [ { a with Responses = [] }; b ], true ] do
                match PureRun.run fixture.Session query.Id [ request fixture "a"; keyed fixture cases ] None with
                | Error(PureRunError.RuntimeFailure failure) ->
                    Assert.Equal(exhausted, failure.BindingExhausted)
                    Assert.Equal((if exhausted then "FakeResponsesExhaustedException" else "FakeArgumentsUnmatchedException"), failure.ExceptionType)
                    Assert.Equal<PureRunFrame list>([ PureRunFrame.CapabilityOperation fixture.Lookup.Id; PureRunFrame.Function query.Id ], failure.Frames)
                    Assert.Equal(1, failure.Calls.Length)
                | other -> failwithf "Missing response was not reported: %A" other
            // Zero-argument operations have one possible complete key, not an implicit default.
            createFunction fixture.Session "fallbackOnly" [ "provider", TypeRef.Capability fixture.Provider.Id ] fixture.Fallback.ResultType [ fixture.Fallback.EffectId ]
                (ExpressionRecipe.CapabilityInvoke(ExpressionRecipe.ParameterSlot 0, fixture.Fallback.Id, []))
            let fallback = PureRunValue.FakeCapability(fixture.Provider.Id,
                            [ { OperationId = fixture.Fallback.Id; Responses = []; Cases = Some [ { Arguments = []; Responses = [ PureRunFakeResponse.Return(found fixture "zero") ] } ] } ])
            let answer = PureRun.run fixture.Session (fixture.Function "fallbackOnly").Id [ fallback ] (Some(found fixture "zero")) |> unwrap "zero arguments"
            Assert.Equal(Some true, answer.Matches)
            let capId, opId, parameterId = StableId.value fixture.Log.Id, StableId.value fixture.Write.Id, StableId.value fixture.Write.Parameters.Head.Id
            let functionId = StableId.value (fixture.Function "logTwice").Id
            let wire = $"""{{"id":"missing-key","op":"runPure","functionId":"{functionId}","arguments":[{{"kind":"string","value":"x"}},{{"kind":"fakeCapability","capabilityId":"{capId}","operations":[{{"operationId":"{opId}","cases":[{{"arguments":[{{"capabilityParameterId":"{parameterId}","value":{{"kind":"string","value":"x"}}}}],"responses":[{{"kind":"return","value":{{"kind":"unit"}}}}]}}]}}]}}]}}"""
            use reply = AuthoringProtocol.executeLine fixture.Session wire |> JsonDocument.Parse
            Assert.False(reply.RootElement.GetProperty("ok").GetBoolean())
            Assert.Equal("RunBindingNoMatch", reply.RootElement.GetProperty("error").GetProperty("code").GetString()))

    [<Fact; Trait("Suite", "Extended")>]
    let ``keyed fakes compare nested values structurally and ignore argument ordering`` () =
        withFixture (fun fixture ->
            let s = fixture.Session
            let intType = TypeRef.Primitive PrimitiveType.Int64
            let listOf t = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ t ])
            let optionOf t = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ t ])
            s.SimulateCreateType
                { Exported = true; TypeParameters = []; ExpectedBaseSnapshotId = current s; DisplayName = "KeyChoice"
                  Kind = AgentTypeDeclarationKindIntent.Union
                    [ { DisplayName = "Numbers"; Fields = [ { DisplayName = "items"; Type = listOf intType } ] }
                      { DisplayName = "Empty"; Fields = [] } ] }
            |> unwrap "choice" |> publish s
            let choice = s.Declarations().Types |> List.find (fun t -> t.DisplayName = "KeyChoice")
            let variants = match choice.Kind with SemanticTypeKind.Union variants -> variants | _ -> failwith "union"
            let numbers = variants |> List.find (fun v -> v.DisplayName = "Numbers")
            let empty = variants |> List.find (fun v -> v.DisplayName = "Empty")
            let resultType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ TypeRef.Named(choice.Id, []); stringType ])
            let nestedType = listOf (optionOf resultType)
            let parameters = [ "left", nestedType; "right", nestedType; "unit", unitType ]
            let cap = createCapability s "NestedLookup"
                        [ { MayWait = false; DisplayName = "lookup"; Parameters = parameters |> List.map (fun (name, t) -> { DisplayName = name; Type = t })
                            ResultType = stringType; FailureType = None; EffectId = fixture.Lookup.EffectId } ]
            let op = cap.Operations.Head
            createFunction s "nestedLookup" (parameters @ [ "provider", TypeRef.Capability cap.Id ]) stringType [ fixture.Lookup.EffectId ]
                (ExpressionRecipe.CapabilityInvoke(ExpressionRecipe.ParameterSlot 3, op.Id,
                    op.Parameters |> List.mapi (fun i p -> p.Id, ExpressionRecipe.ParameterSlot i)))
            let numberValues xs = PureRunValue.Union(choice.Id, [], numbers.Id, [ numbers.Fields.Head.Id, PureRunValue.Int64List xs ])
            let nested value = PureRunValue.ListValue(optionOf resultType,
                                [ PureRunValue.OptionValue(resultType, Some(PureRunValue.ResultValue(TypeRef.Named(choice.Id, []), stringType, true, value)))
                                  PureRunValue.OptionValue(resultType, None) ])
            let a, b = nested (numberValues [ Int64.MinValue; 2L ]), nested (numberValues [ Int64.MinValue; 3L ])
            let noFields = nested (PureRunValue.Union(choice.Id, [], empty.Id, []))
            let key left right answer: PureRunFakeCase =
                { Arguments = [ op.Parameters[2].Id, PureRunValue.Unit; op.Parameters[1].Id, right; op.Parameters[0].Id, left ]
                  Responses = [ PureRunFakeResponse.Return(PureRunValue.String answer) ] }
            let cases = [ key a a "aa"; key a b "ab"; key noFields b "empty" ]
            for left, right, expected in [ a, a, "aa"; a, b, "ab"; noFields, b, "empty" ] do
                let binding = PureRunValue.FakeCapability(cap.Id, [ { OperationId = op.Id; Responses = []; Cases = Some cases } ])
                let answer = PureRun.run s (fixture.Function "nestedLookup").Id [ left; right; PureRunValue.Unit; binding ] (Some(PureRunValue.String expected)) |> unwrap "structural key"
                Assert.Equal(Some true, answer.Matches)
            let duplicate = [ key a b "one"; { key a b "two" with Arguments = (key a b "two").Arguments |> List.rev } ]
            match PureRun.run s (fixture.Function "nestedLookup").Id
                    [ a; b; PureRunValue.Unit; PureRunValue.FakeCapability(cap.Id, [ { OperationId = op.Id; Responses = []; Cases = Some duplicate } ]) ] None with
            | Error(PureRunError.Unsupported("RunBindingShapeMismatch", _)) -> ()
            | other -> failwithf "Canonical duplicate keys were accepted: %A" other)


    [<Fact>]
    let ``fake Result wire errors locate operations cases responses and nested values without payloads`` () =
        withFixture (fun fixture ->
            let before = File.ReadAllText fixture.Path
            let functionId = StableId.value (fixture.Function "logTwice").Id
            let capId, opId = StableId.value fixture.Log.Id, StableId.value fixture.Write.Id
            let wire script =
                $"""{{"id":"shape","op":"runPure","functionId":"{functionId}","arguments":[{{"kind":"string","value":"m"}},{{"kind":"fakeCapability","capabilityId":"{capId}","operations":[{{"operationId":"{opId}","responses":[]}},{{"operationId":"{opId}",{script}}}]}}]}}"""
            let response value = """{"kind":"return","value":PAYLOAD}""".Replace("PAYLOAD", value)
            let malformedFlat = """{"kind":"result","okKind":"string","errorKind":"string","isOk":true,"value":{"kind":"string","value":"PRIVATE_SENTINEL"}}"""
            let malformedNested = """{"kind":"result","okType":{"kind":"primitive","primitive":"unit"},"errorType":{"kind":"primitive","primitive":"string"},"isOk":true,"value":{"kind":"unit","PRIVATE_SENTINEL":true}}"""
            let check script suffix expected =
                use reply = AuthoringProtocol.executeLine fixture.Session (wire script) |> JsonDocument.Parse
                let error = reply.RootElement.GetProperty("error")
                Assert.Equal("ProtocolError", error.GetProperty("code").GetString())
                let message = error.GetProperty("message").GetString()
                Assert.Contains("arguments[1].operations[1]." + suffix, message)
                Assert.Contains(expected, message)
                Assert.DoesNotContain("PRIVATE_SENTINEL", message)
            check ("\"responses\":[" + response malformedFlat + "]") "responses[0].value.value" "raw JSON string"
            check ("\"cases\":[{\"arguments\":[],\"responses\":[]},{\"arguments\":[],\"responses\":[{\"kind\":\"return\",\"value\":{\"kind\":\"unit\"}}," + response malformedNested + "]}]")
                "cases[1].responses[1].value.value" "Unit {kind: unit}"
            let nestedList = """{"kind":"list","itemType":{"kind":"primitive","primitive":"unit"},"items":[{"kind":"unit"},{"kind":"unit","PRIVATE_SENTINEL":false}]}"""
            check ("\"responses\":[" + response nestedList + "]") "responses[0].value.items[1]" "Unit {kind: unit}"
            check "\"responses\":[{\"kind\":\"return\",\"value\":{\"kind\":\"unit\"},\"delayMillis\":-1}]" "responses[0]" "delayMillis must not be negative"
            check "\"cases\":[{\"arguments\":[{}, {\"PRIVATE_SENTINEL\":true}],\"responses\":[]}]" "cases[0].arguments[0]" "declared parameter and typed value"
            let rec tooDeep depth =
                if depth = 0 then "{\"kind\":\"unit\"}"
                else "{\"kind\":\"option\",\"itemType\":{\"kind\":\"primitive\",\"primitive\":\"unit\"},\"hasValue\":true,\"value\":" + tooDeep (depth - 1) + "}"
            check ("\"responses\":[" + response (tooDeep PureRun.maxValueLayers) + "]") "responses[0].value" "nested composite layers"
            for value in
                [ """{"kind":"result","okKind":"string","errorKind":"string","isOk":true,"value":"ok"}"""
                  """{"kind":"result","okKind":"string","errorKind":"string","isOk":false,"value":"error"}"""
                  """{"kind":"result","okType":{"kind":"primitive","primitive":"unit"},"errorType":{"kind":"primitive","primitive":"string"},"isOk":true,"value":{"kind":"unit"}}"""
                  """{"kind":"result","okType":{"kind":"primitive","primitive":"unit"},"errorType":{"kind":"primitive","primitive":"string"},"isOk":false,"value":{"kind":"string","value":"error"}}""" ] do
                use reply = AuthoringProtocol.executeLine fixture.Session (wire ("\"responses\":[" + response value + "]")) |> JsonDocument.Parse
                // Binding validation rejects duplicate operations/the declared Unit result, after wire parsing.
                Assert.NotEqual<string>("ProtocolError", reply.RootElement.GetProperty("error").GetProperty("code").GetString())
            Assert.Equal(before, File.ReadAllText fixture.Path))

    [<Fact>]
    let ``typed scalar Result fakes accept valid shapes and locate a wrong scripted payload`` () =
        withFixture (fun fixture ->
            let effect = fixture.Session.Declarations().Effects |> List.exactlyOne
            let resultType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ stringType; stringType ])
            let operation name: AgentCapabilityOperationIntent =
                { MayWait = false; DisplayName = name
                  Parameters = [ { DisplayName = "key"; Type = stringType } ]
                  ResultType = resultType; FailureType = None; EffectId = effect.Id }
            let capability = createCapability fixture.Session "ShapeSource" [ operation "first"; operation "read" ]
            createFunction fixture.Session "fakeShapes" [ "source", TypeRef.Capability capability.Id ] stringType [] (ExpressionRecipe.StringLiteral "unused")
            let fn = fixture.Function "fakeShapes"
            let first = capability.Operations |> List.find (fun item -> item.DisplayName = "first")
            let read = capability.Operations |> List.find (fun item -> item.DisplayName = "read")
            let declarations = fixture.Session.Declarations()
            let binding value = PureRunValue.FakeCapability(capability.Id,
                [ { OperationId = read.Id; Cases = None; Responses = [ PureRunFakeResponse.Return value ] } ])
            for value in [ PureRunValue.ResultOk(PureRunScalarKind.String, PureRunScalar.String "ok"); PureRunValue.ResultErr(PureRunScalarKind.String, PureRunScalar.String "error")
                           PureRunValue.ResultValue(stringType, stringType, true, PureRunValue.String "ok")
                           PureRunValue.ResultValue(stringType, stringType, false, PureRunValue.String "error") ] do
                Assert.True((PureRun.inputIssue declarations fn.Id [ binding value ] None).IsNone)
            let unitResultType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ unitType; stringType ])
            let unitCapabilities =
                declarations.Capabilities |> List.map (fun cap ->
                    if cap.Id <> capability.Id then cap
                    else
                        let operations = cap.Operations |> List.map (fun op -> { op with ResultType = unitResultType })
                        { cap with Operations = operations })
            let unitDeclarations = { declarations with Capabilities = unitCapabilities }
            for isOk, payload in [ true, PureRunValue.Unit; false, PureRunValue.String "error" ] do
                let value = PureRunValue.ResultValue(unitType, stringType, isOk, payload)
                Assert.True((PureRun.inputIssue unitDeclarations fn.Id [ binding value ] None).IsNone)
            let before = File.ReadAllText fixture.Path
            for names in [ false; true ] do
                let entry = if names then "\"op\":\"runPureByName\",\"function\":\"fakeShapes\"" else "\"op\":\"runPure\",\"functionId\":\"" + StableId.value fn.Id + "\""
                let cap = if names then "\"capability\":\"ShapeSource\"" else "\"capabilityId\":\"" + StableId.value capability.Id + "\""
                let op (value: SemanticCapabilityOperation) = if names then "\"operation\":\"" + value.DisplayName + "\"" else "\"operationId\":\"" + StableId.value value.Id + "\""
                let parameter = if names then "\"parameter\":\"key\"" else "\"capabilityParameterId\":\"" + StableId.value read.Parameters.Head.Id + "\""
                let good = """{"kind":"return","value":{"kind":"result","okKind":"string","errorKind":"string","isOk":true,"value":"ok"}}"""
                let bad = """{"kind":"return","value":{"kind":"result","okType":{"kind":"primitive","primitive":"string"},"errorType":{"kind":"primitive","primitive":"string"},"isOk":true,"value":{"kind":"list","itemKind":"string","items":["PRIVATE_SENTINEL"]}}}"""
                let case key responses = $"""{{"arguments":[{{{parameter},"value":{{"kind":"string","value":"{key}"}}}}],"responses":[{responses}]}}"""
                let line = $"""{{"id":"nested-scalar",{entry},"arguments":[{{"kind":"fakeCapability",{cap},"operations":[{{{op first},"responses":[]}},{{{op read},"cases":[{case "a" good},{case "b" (good + "," + bad)}]}}]}}]}}"""
                use reply = AuthoringProtocol.executeLine fixture.Session line |> JsonDocument.Parse
                let error = reply.RootElement.GetProperty("error")
                Assert.Equal("RunContainerTypeMismatch", error.GetProperty("code").GetString())
                let details = error.GetProperty("details")
                let path = details.GetProperty("path").EnumerateArray() |> Seq.map (fun item -> item.GetString()) |> String.concat "."
                Assert.Equal("arguments.source.operations[1].cases[1].responses[1].value.ok", path)
                Assert.Contains("type", details.GetProperty("reason").GetString())
                Assert.DoesNotContain("PRIVATE_SENTINEL", reply.RootElement.GetRawText())
            Assert.Equal(before, File.ReadAllText fixture.Path))

    [<Fact; Trait("Suite", "Extended")>]
    let ``typed scalar fake case arguments and Result responses execute through the binding`` () =
        withFixture (fun fixture ->
            let effect = fixture.Session.Declarations().Effects |> List.exactlyOne
            let strings = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ stringType ])
            let resultType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ stringType; stringType ])
            let operation: AgentCapabilityOperationIntent =
                { MayWait = false; DisplayName = "echo"; Parameters = [ { DisplayName = "items"; Type = strings } ]
                  ResultType = resultType; FailureType = None; EffectId = effect.Id }
            let capability = createCapability fixture.Session "TypedEcho" [ operation ]
            let echo = capability.Operations |> List.exactlyOne
            let parameter = echo.Parameters |> List.exactlyOne
            let slot name = ExpressionRecipe.PatternReference { ScopeDepth = 0; Slot = name }
            createFunction fixture.Session "typedEcho" [ "source", TypeRef.Capability capability.Id ] stringType [ effect.Id ]
                (ExpressionRecipe.MatchResult(
                    ExpressionRecipe.CapabilityInvoke(ExpressionRecipe.ParameterSlot 0, echo.Id,
                        [ parameter.Id, ExpressionRecipe.ListConstruct(stringType, [ ExpressionRecipe.StringLiteral "key" ]) ]),
                    slot RecipePatternSlot.Value, slot RecipePatternSlot.Error))
            let binding = PureRunValue.FakeCapability(capability.Id,
                [ { OperationId = echo.Id; Responses = []
                    Cases = Some [ { Arguments = [ parameter.Id, PureRunValue.ListValue(stringType, [ PureRunValue.String "key" ]) ]
                                     Responses = [ PureRunFakeResponse.Return(PureRunValue.ResultValue(stringType, stringType, true, PureRunValue.String "echoed")) ] } ] } ])
            let result = PureRun.run fixture.Session (fixture.Function "typedEcho").Id [ binding ] (Some(PureRunValue.String "echoed")) |> unwrap "typed fake"
            Assert.Equal(Some true, result.Matches)
            Assert.Single(result.Calls) |> ignore)

    [<Fact; Trait("Suite", "Extended")>]
    let ``runPure binds capabilities whose operations take or return capability values`` () =
        withFixture (fun fixture ->
            let closeWith = fixture.Function "closeWith"
            let operation name = fixture.Issuer.Operations |> List.find (fun item -> item.DisplayName = name)
            let close, opening = operation "close", operation "open"
            let note = (close.Parameters |> List.find (fun item -> item.DisplayName = "note")).Id
            let issuer responses =
                PureRunValue.FakeCapability(fixture.Issuer.Id,
                    [ { OperationId = close.Id; Cases = None; Responses = [ PureRunFakeResponse.Return PureRunValue.Unit ] }
                      { OperationId = opening.Id; Cases = None; Responses = responses } ])
            let log = PureRunValue.FakeCapability(fixture.Log.Id, [])
            // The capability argument is left out of the recorded call; the String argument stays.
            let result = PureRun.run fixture.Session closeWith.Id [ issuer [ PureRunFakeResponse.Throw "not opened" ]; log ] None |> unwrap "close"
            Assert.Equal<PureRunCall list>(
                [ { ParameterId = (List.head closeWith.Parameters).Id; OperationId = close.Id; Arguments = [ note, PureRunValue.String "done" ] } ],
                result.Calls)
            // No run value has a capability type, so a fake can only throw from an operation returning one.
            match PureRun.run fixture.Session closeWith.Id [ issuer [ PureRunFakeResponse.Return(PureRunValue.String "x") ]; log ] None with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunBindingShapeMismatch", code)
            | other -> failwithf "Capability-returning fake value accepted: %A" other)
