namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// runPure String values carry any sequence of UTF-16 code units. On the wire, lone surrogates use
/// JSON's own `\uD800` escapes in both directions.
module PureRunLoneSurrogateTests =
    let private stringType = TypeRef.Primitive PrimitiveType.String

    let private intType = TypeRef.Primitive PrimitiveType.Int64

    /// Lone high, lone low, a reversed pair, surrogates at the start and the end, and a mix with
    /// astral characters; the last is well-formed.
    let private texts =
        [ (utf16 "{D800}"); (utf16 "{DC00}"); (utf16 "{DC00}{D800}"); (utf16 "{DBFF}abc"); (utf16 "abc{DC01}"); (utf16 "😀{D800}😀{DFFF} x,\"\\{D83D}"); (utf16 "a{D83D}{DE00}b") ]

    /// The JSON string literal a client writes for `text`: ASCII kept, everything else as `\uXXXX`.
    let private literal (text: string) =
        let escaped =
            text |> Seq.map (fun c ->
                match c with
                | '"' -> "\\\""
                | '\\' -> "\\\\"
                | c when int c < 0x20 || int c > 0x7e -> $"\\u{int c:X4}"
                | c -> string c) |> String.concat ""
        "\"" + escaped + "\""

    let private unwrap label result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "%s: %A" label error

    [<Fact>]
    let ``run Strings are read from and written to JSON with surrogate escapes`` () =
        for text in texts @ [ ""; "雪"; "<&>'"; "\u0001\u2028" ] do
            use document = JsonDocument.Parse(literal text)
            Assert.Equal(text, WireInput.runText document.RootElement)
            let written = WireJson.serialize (WireJson.runString text)
            use reparsed = JsonDocument.Parse(written)
            Assert.Equal(text, WireInput.runText reparsed.RootElement)
        // Well-formed Strings are the plain strings, encoded exactly as before.
        for text in [ ""; "雪😀"; "a\"b\\c\n"; "\u0001\u2028<&>'" ] do
            Assert.Equal(JsonSerializer.Serialize(text), WireJson.serialize (WireJson.runString text))
            Assert.IsType<string>(WireJson.runString text) |> ignore
        // Lowercase escapes, as JavaScript's JSON.stringify writes them, read the same.
        use lower = JsonDocument.Parse("\"\\udc00x\\ud83d\"")
        Assert.Equal(utf16 "{DC00}x{D83D}", WireInput.runText lower.RootElement)
        Assert.Equal("\"\\uD800\"", WireJson.serialize (WireJson.runString (utf16 "{D800}")))
        Assert.Equal("\"\\uDC00\\uD800\"", WireJson.serialize (WireJson.runString (utf16 "{DC00}{D800}")))
        Assert.Equal("\"\\uD83D\\uDE00\\uD800a\\u96EA\\u0022\"", WireJson.serialize (WireJson.runString (utf16 "😀{D800}a雪\"")))

    let private current (session: AuthoringSession) = (session.Status() |> unwrap "status").SnapshotId

    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore

    let private createFunction (session: AuthoringSession) name parameters resultType effects body =
        let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
            { ExpectedBaseSnapshotId = current session
              DisplayName = name
              Parameters = parameters |> List.map (fun (displayName, itemType) -> { DisplayName = displayName; Type = itemType })
              ResultType = resultType
              RequiredEffects = effects
              Body = body }
        session.SimulateCreateFunction request |> unwrap ("create " + name) |> publish session
        session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)

    let private withLiteralSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-literal-wire-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        try
            use session = AuthoringSession.createNew path |> unwrap "session"
            run path session
        finally
            Directory.Delete(root, true)

    let private wireResult session line =
        let raw = AuthoringProtocol.executeLine session line
        use document = JsonDocument.Parse(raw)
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean(), raw)
        document.RootElement.GetProperty("result").Clone()

    let private structuredLiteral (functionValue: JsonElement) =
        let first = (functionValue.GetProperty("body").GetProperty("nodes"))[0]
        WireInput.runText (first.GetProperty("kind").GetProperty("value"))

    [<Theory; InlineData(true); InlineData(false)>]
    let ``wire literal creation replacement review and reopen preserve every String code unit`` named =
        withLiteralSession (fun path session ->
            let expected =
                texts @ [ ""; "\uFFFD"; "雪"; "a\"b\\c\n" ]
                |> List.mapi (fun index text ->
                    let name = "literal" + string index
                    let op = if named then "simulateCreateFunctionByName" else "simulateCreateFunction"
                    let created = wireResult session $"""{{"id":"create","op":"{op}","displayName":"{name}","parameters":[],"resultType":{{"kind":"primitive","primitive":"string"}},"requiredEffects":[],"body":{{"kind":"stringLiteral","value":{literal text}}},"structuredReview":true,"detail":"full"}}"""
                    Assert.Equal(text, structuredLiteral (created.GetProperty("review").GetProperty("createdFunction")))
                    let proposalId = created.GetProperty("proposalId").GetString()
                    let page = wireResult session $"""{{"id":"page","op":"readProposal","proposalId":"{proposalId}","path":"/review/createdFunction/body/nodes/0/kind/value","limit":1024}}"""
                    Assert.Equal(text, WireInput.runText (page.GetProperty("value")))
                    session.Commit(proposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "create commit" |> ignore
                    let fn = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
                    let replacement = text + (utf16 "{DC00}")
                    let selector =
                        if named then $"\"op\":\"simulateReplaceBodyByName\",\"function\":\"{name}\""
                        else $"\"op\":\"simulateReplaceBody\",\"functionId\":\"{StableId.value fn.Id}\",\"expectedCurrentResultNodeId\":\"{StableId.value fn.Body.ResultNodeId}\""
                    let changed = wireResult session $"""{{"id":"replace",{selector},"replacement":{{"kind":"stringLiteral","value":{literal replacement}}},"structuredReview":true,"detail":"full"}}"""
                    Assert.Equal(replacement, structuredLiteral (changed.GetProperty("review").GetProperty("proposedFunction")))
                    session.Commit(changed.GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap "replace commit" |> ignore
                    let changedFn=session.Declarations().Functions |> List.find (fun item -> item.Id=fn.Id)
                    let finalText=replacement+(utf16 "{D800}")
                    let targetSelector = if named then $"\"op\":\"simulateReplaceTargetByName\",\"function\":\"{name}\"" else $"\"op\":\"simulateReplaceTarget\",\"functionId\":\"{StableId.value fn.Id}\""
                    let targeted=wireResult session $"""{{"id":"target",{targetSelector},"targetNodeId":"{StableId.value changedFn.Body.ResultNodeId}","replacement":{{"kind":"stringLiteral","value":{literal finalText}}},"structuredReview":true,"detail":"full"}}"""
                    Assert.Equal(finalText,WireInput.runText (targeted.GetProperty("review").GetProperty("diff").GetProperty("proposedTarget").GetProperty("literal").GetProperty("value")))
                    session.Commit(targeted.GetProperty("proposalId").GetString(),CommitPolicy.RejectBlockingObligations) |> unwrap "target commit" |> ignore
                    let read = wireResult session $"""{{"id":"read","op":"readFunction","function":"{name}"}}"""
                    Assert.Equal(finalText, structuredLiteral (read.GetProperty("function")))
                    name, finalText)
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap "reopen"
            for name, text in expected do
                let read = wireResult reopened $"""{{"id":"read","op":"readFunction","function":"{name}"}}"""
                Assert.Equal(text, structuredLiteral (read.GetProperty("function"))))

    [<Fact>]
    let ``literal review pagination distinguishes lone surrogates from actual pairs`` () =
        withLiteralSession (fun _ session ->
            let text = (utf16 "{D800}x") + String.replicate 1100 (utf16 "{DC00}") + "😀end"
            let proposal =
                session.SimulateCreateFunction
                    { ExpectedBaseSnapshotId = current session; DisplayName = "paged"; Parameters = []
                      ResultType = stringType; RequiredEffects = []; Body = ExpressionRecipe.StringLiteral text }
                |> unwrap "create"
            let id = proposal.ProposalId
            let page offset limit =
                wireResult session $"""{{"id":"page","op":"readProposal","proposalId":"{id}","path":"/review/createdFunction/body/nodes/0/kind/value","offset":{offset},"limit":{limit}}}"""
            let first = page 0 1
            Assert.Equal((utf16 "{D800}"), WireInput.runText (first.GetProperty("value")))
            Assert.Equal(1, first.GetProperty("nextOffset").GetInt32())
            let rebuilt = System.Text.StringBuilder()
            let mutable offset = 0
            let mutable more = true
            while more do
                let result = page offset 1024
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.GetRawText()) <= 8192)
                rebuilt.Append(WireInput.runText (result.GetProperty("value"))) |> ignore
                let next = result.GetProperty("nextOffset")
                more <- next.ValueKind <> JsonValueKind.Null
                if more then offset <- next.GetInt32()
            Assert.Equal(text, rebuilt.ToString())
            let pairOffset = text.Length - "😀end".Length
            use split = JsonDocument.Parse(AuthoringProtocol.executeLine session $"""{{"id":"split","op":"readProposal","proposalId":"{id}","path":"/review/createdFunction/body/nodes/0/kind/value","offset":{pairOffset},"limit":1}}""")
            Assert.False(split.RootElement.GetProperty("ok").GetBoolean())
            Assert.Equal("😀", WireInput.runText ((page pairOffset 2).GetProperty("value"))))

    [<Fact>]
    let ``bounded compact reviews keep lone literal prefixes and remain retrievable`` () =
        withLiteralSession (fun _ session ->
            let fn = createFunction session "large" [] stringType [] (ExpressionRecipe.StringLiteral "seed")
            let text = String.replicate 300 (utf16 "{D800}")
            let body = [1..12] |> List.map (fun _ -> ExpressionRecipe.StringLiteral text) |> List.reduce (fun left right -> ExpressionRecipe.StringConcat(left, right))
            let proposal = session.SimulateReplaceTarget { ExpectedBaseSnapshotId=current session; TargetFunctionId=fn.Id; TargetNodeId=fn.Body.ResultNodeId; Replacement=body } |> unwrap "replace"
            let compact = WireEncode.compactProposal proposal |> WireJson.serialize
            Assert.True(System.Text.Encoding.UTF8.GetByteCount compact <= 16384)
            Assert.DoesNotContain("\\uFFFD", compact, StringComparison.OrdinalIgnoreCase)
            use parsed = JsonDocument.Parse compact
            Assert.True(parsed.RootElement.GetProperty("projection").GetProperty("requiresDetailedReview").GetBoolean())
            let root = wireResult session $"""{{"id":"page","op":"readProposal","proposalId":"{proposal.ProposalId}","limit":20}}"""
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(root.GetRawText()) <= 8192)
            Assert.DoesNotContain("\\uFFFD", root.GetRawText(), StringComparison.OrdinalIgnoreCase))

    [<Fact; Trait("Suite", "Extended")>]
    let ``lone surrogates round-trip through arguments, results, containers and fakes`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-lone-surrogates-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(root, "workspace.json")) |> unwrap "session"
            let field name itemType: AgentTypeFieldIntent = { DisplayName = name; Type = itemType }
            let holderRequest: AgentCreateTypeDeclarationRequest =
                { TypeParameters = []; ExpectedBaseSnapshotId = current session
                  DisplayName = "Holder"
                  Kind =
                    AgentTypeDeclarationKindIntent.Product
                        [ field "text" stringType
                          field "texts" (TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ stringType ]))
                          field "maybe" (TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ stringType ]))
                          field "outcome" (TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ stringType; intType ])) ] }
            session.SimulateCreateType holderRequest |> unwrap "holder" |> publish session
            let holder = session.Declarations().Types |> List.exactlyOne
            let fieldId name =
                match holder.Kind with
                | SemanticTypeKind.Product fields -> (fields |> List.find (fun item -> item.DisplayName = name)).Id
                | _ -> failwith "Expected a Product."
            let holders = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ TypeRef.Named(holder.Id, []) ])
            let echo = createFunction session "echo" [ "text", stringType ] stringType [] (ExpressionRecipe.ParameterSlot 0)
            let join = createFunction session "join" [ "left", stringType; "right", stringType ] stringType [] (ExpressionRecipe.StringConcat(ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.ParameterSlot 1))
            let echoAll = createFunction session "echoAll" [ "holders", holders ] holders [] (ExpressionRecipe.ParameterSlot 0)
            let run (fn: SemanticFunction) arguments expected = PureRun.run session fn.Id arguments expected |> unwrap fn.DisplayName
            // Top-level String arguments, results and expectations.
            for text in texts do
                let result = run echo [ PureRunValue.String text ] (Some(PureRunValue.String text))
                Assert.Equal(PureRunValue.String text, result.Actual)
                Assert.Equal(Some true, result.Matches)
            // A lone surrogate is a distinct value: a different one does not match.
            let differs = run echo [ PureRunValue.String (utf16 "{D800}") ] (Some(PureRunValue.String (utf16 "{DC00}")))
            Assert.Equal(Some false, differs.Matches)
            Assert.Equal(PureRunValue.String (utf16 "{DC00}"), differs.Mismatch.Value.Expected)
            // Results the program builds: halves join to a pair, a reversed pair stays two lone surrogates.
            Assert.Equal(PureRunValue.String "😀", (run join [ PureRunValue.String (utf16 "{D83D}"); PureRunValue.String (utf16 "{DE00}") ] None).Actual)
            Assert.Equal(PureRunValue.String (utf16 "{DE00}{D83D}"), (run join [ PureRunValue.String (utf16 "{DE00}"); PureRunValue.String (utf16 "{D83D}") ] None).Actual)
            Assert.Equal(PureRunValue.String (utf16 "x{D83D}"), (run join [ PureRunValue.String "x"; PureRunValue.String (utf16 "{D83D}") ] None).Actual)
            // Nested containers: List, Option and Result payloads and Product fields.
            let holderValue (text: string) =
                PureRunValue.Product(holder.Id, [],
                    [ fieldId "text", PureRunValue.String text
                      fieldId "texts", PureRunValue.StringList [ text; "ok"; text + text ]
                      fieldId "maybe", PureRunValue.StringOption(Some text)
                      fieldId "outcome", PureRunValue.ResultOk(PureRunScalarKind.Int64, PureRunScalar.String text) ]
                    |> List.sortBy (fst >> StableId.value))
            let all = PureRunValue.ListValue(TypeRef.Named(holder.Id, []), texts |> List.map holderValue)
            let echoed = run echoAll [ all ] (Some all)
            Assert.Equal(all, echoed.Actual)
            Assert.Equal(Some true, echoed.Matches)
            // Fakes: keyed cases, returned values, recorded call arguments and thrown messages.
            let effectRequest: AgentCreateEffectDeclarationRequest = { ExpectedBaseSnapshotId = current session; DisplayName = "Lookup" }
            session.SimulateCreateEffect effectRequest |> unwrap "effect" |> publish session
            let effect = session.Declarations().Effects |> List.exactlyOne
            let capabilityRequest: AgentCreateCapabilityDeclarationRequest =
                { ExpectedBaseSnapshotId = current session
                  DisplayName = "Dictionary"
                  Operations = [ { DisplayName = "get"; Parameters = [ { DisplayName = "key"; Type = stringType } ]; ResultType = stringType; FailureType = None; EffectId = effect.Id } ] }
            session.SimulateCreateCapability capabilityRequest |> unwrap "capability" |> publish session
            let dictionary = session.Declarations().Capabilities |> List.exactlyOne
            let get = dictionary.Operations |> List.exactlyOne
            let key = (List.exactlyOne get.Parameters).Id
            let ask =
                createFunction session "ask" [ "key", stringType; "dictionary", TypeRef.Capability dictionary.Id ] stringType [ effect.Id ]
                    (ExpressionRecipe.CapabilityInvoke(ExpressionRecipe.ParameterSlot 1, get.Id, [ key, ExpressionRecipe.ParameterSlot 0 ]))
            let keyed =
                PureRunValue.FakeCapability(dictionary.Id,
                    [ { OperationId = get.Id; Responses = []
                        Cases = Some(texts |> List.map (fun text ->
                            { Arguments = [ key, PureRunValue.String text ]
                              Responses = [ PureRunFakeResponse.Return(PureRunValue.String("=" + text)) ] })) } ])
            for text in texts do
                let result = run ask [ PureRunValue.String text; keyed ] None
                Assert.Equal(PureRunValue.String("=" + text), result.Actual)
                Assert.Equal<(CapabilityParameterId * PureRunValue) list>([ key, PureRunValue.String text ], result.Calls.Head.Arguments)
            // The high half alone is not the pair: no case matches.
            match PureRun.run session ask.Id [ PureRunValue.String (utf16 "{D83D}"); keyed ] None with
            | Error(PureRunError.RuntimeFailure failure) -> Assert.Equal("FakeArgumentsUnmatchedException", failure.ExceptionType)
            | other -> failwithf "A lone half matched: %A" other
            let throwing = PureRunValue.FakeCapability(dictionary.Id, [ { OperationId = get.Id; Responses = [ PureRunFakeResponse.Throw (utf16 "down{DC00} 😀{D800}") ]; Cases = None } ])
            match PureRun.run session ask.Id [ PureRunValue.String "k"; throwing ] None with
            | Error(PureRunError.RuntimeFailure failure) ->
                Assert.Equal("FakeCapabilityException", failure.ExceptionType)
                Assert.Equal((utf16 "down{DC00} 😀{D800}"), failure.Message)
            | other -> failwithf "The throwing fake was not reported: %A" other
            // The wire: requests and responses use JSON surrogate escapes.
            let send line =
                let response = AuthoringProtocol.executeLine session line
                response, JsonDocument.Parse(response)
            let text = (utf16 "{DC00}a{D800}")
            let request =
                $"""{{"id":"lone","op":"runPure","functionId":"{StableId.value echo.Id}","arguments":[{{"kind":"string","value":{literal text}}}],"expected":{{"kind":"string","value":{literal text}}}}}"""
            let raw, document = send request
            use document = document
            let result = document.RootElement.GetProperty("result")
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean(), raw)
            Assert.True(result.GetProperty("matches").GetBoolean())
            Assert.Equal(text, WireInput.runText (result.GetProperty("actual").GetProperty("value")))
            Assert.Contains("\"value\":\"\\uDC00a\\uD800\"", raw)
            let listRequest =
                $"""{{"id":"lone-list","op":"runPureByName","function":"ask","arguments":[{{"kind":"string","value":"\uD800"}},{{"kind":"fakeCapability","capability":"Dictionary","operations":[{{"operation":"get","responses":[{{"kind":"throw","message":"x\uDFFF"}}]}}]}}]}}"""
            let raw, document = send listRequest
            use document = document
            Assert.False(document.RootElement.GetProperty("ok").GetBoolean(), raw)
            Assert.Equal((utf16 "x{DFFF}"), WireInput.runText (document.RootElement.GetProperty("error").GetProperty("message")))
            Assert.Contains("\"message\":\"x\\uDFFF\"", raw)
            let calls = document.RootElement.GetProperty("error").GetProperty("calls")
            let arguments = calls[0].GetProperty("arguments")
            let call = arguments[0].GetProperty("value").GetProperty("value")
            Assert.Equal((utf16 "{D800}"), WireInput.runText call)
        finally
            if Directory.Exists root then Directory.Delete(root, true)
