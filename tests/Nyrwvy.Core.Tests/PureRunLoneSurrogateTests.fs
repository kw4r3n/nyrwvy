namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// A String is a sequence of Unicode scalar values. On the wire, astral characters may arrive as
/// JSON surrogate-pair escapes and round-trip; an unpaired surrogate escape in a run value, a fake
/// response or a literal is rejected.
module PureRunLoneSurrogateTests =
    let private stringType = TypeRef.Primitive PrimitiveType.String

    let private unpaired =
        "A String must not contain an unpaired surrogate escape; a String is a sequence of Unicode scalar values."

    /// Astral characters alone, between, before and after BMP text, and next to JSON's own escapes.
    let private texts =
        [ "😀"; "a😀b"; "\U00010000abc"; "abc\U0010FFFF"; "😀雪😀 x,\"\\𝄞" ]

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
    let ``run Strings read surrogate pair escapes and reject unpaired surrogate escapes`` () =
        for text in texts @ [ ""; "雪"; "<&>'"; "\u0001\u2028" ] do
            use document = JsonDocument.Parse(literal text)
            Assert.Equal(text, WireInput.runText document.RootElement)
            let written = WireJson.serialize (WireJson.runString text)
            use reparsed = JsonDocument.Parse(written)
            Assert.Equal(text, WireInput.runText reparsed.RootElement)
        // Strings are the plain strings, encoded as JSON encodes them.
        for text in [ ""; "雪😀"; "a\"b\\c\n"; "\u0001\u2028<&>'" ] do
            Assert.Equal(JsonSerializer.Serialize(text), WireJson.serialize (WireJson.runString text))
        // Lowercase escapes, as JavaScript's JSON.stringify writes them, read the same.
        use lower = JsonDocument.Parse("\"\\ud83d\\ude00x\"")
        Assert.Equal("😀x", WireInput.runText lower.RootElement)
        // Lone high, lone low, a reversed pair, at the start and the end, and a high before a pair.
        for raw in [ "\\uD800"; "\\uDC00"; "\\uDC00\\uD800"; "\\udbffabc"; "abc\\uDC01"; "\\uD83D\\uD83D\\uDE00" ] do
            use document = JsonDocument.Parse("\"" + raw + "\"")
            match (try Ok(WireInput.runText document.RootElement) with WireInput.ProtocolInputException message -> Error message) with
            | Error message -> Assert.Equal(unpaired, message)
            | Ok text -> failwithf "%s was read as %A" raw text

    let private current (session: AuthoringSession) = (session.Status() |> unwrap "status").SnapshotId

    let private publish (session: AuthoringSession) (proposal: AuthoringProposal) =
        session.Commit(proposal.ProposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "commit" |> ignore

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

    let private wireError session line =
        let raw = AuthoringProtocol.executeLine session line
        use document = JsonDocument.Parse(raw)
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean(), raw)
        document.RootElement.GetProperty("error").GetProperty("message").GetString()

    let private structuredLiteral (functionValue: JsonElement) =
        let first = (functionValue.GetProperty("body").GetProperty("nodes"))[0]
        WireInput.runText (first.GetProperty("kind").GetProperty("value"))

    [<Theory; InlineData(true); InlineData(false)>]
    let ``wire literal creation replacement review and reopen preserve astral text and reject unpaired surrogates`` named =
        withLiteralSession (fun path session ->
            let op = if named then "simulateCreateFunctionByName" else "simulateCreateFunction"
            let create name text =
                $"""{{"id":"create","op":"{op}","displayName":"{name}","parameters":[],"resultType":{{"kind":"primitive","primitive":"string"}},"requiredEffects":[],"body":{{"kind":"stringLiteral","value":{text}}},"structuredReview":true,"detail":"full"}}"""
            Assert.Equal(unpaired, wireError session (create "lone" "\"a\\uD800\""))
            let expected =
                texts @ [ ""; "\uFFFD"; "雪"; "a\"b\\c\n" ]
                |> List.mapi (fun index text ->
                    let name = "literal" + string index
                    let created = wireResult session (create name (literal text))
                    Assert.Equal(text, structuredLiteral (created.GetProperty("review").GetProperty("createdFunction")))
                    let proposalId = created.GetProperty("proposalId").GetString()
                    let page = wireResult session $"""{{"id":"page","op":"readProposal","proposalId":"{proposalId}","path":"/review/createdFunction/body/nodes/0/kind/value","limit":1024}}"""
                    Assert.Equal(text, WireInput.runText (page.GetProperty("value")))
                    session.Commit(proposalId, CommitPolicy.RejectBlockingObligations) |> unwrap "create commit" |> ignore
                    let fn = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
                    let replacement = text + "😀"
                    let selector =
                        if named then $"\"op\":\"simulateReplaceBodyByName\",\"function\":\"{name}\""
                        else $"\"op\":\"simulateReplaceBody\",\"functionId\":\"{StableId.value fn.Id}\",\"expectedCurrentResultNodeId\":\"{StableId.value fn.Body.ResultNodeId}\""
                    let replace value = $"""{{"id":"replace",{selector},"replacement":{{"kind":"stringLiteral","value":{value}}},"structuredReview":true,"detail":"full"}}"""
                    Assert.Equal(unpaired, wireError session (replace "\"\\uDE00\""))
                    let changed = wireResult session (replace (literal replacement))
                    Assert.Equal(replacement, structuredLiteral (changed.GetProperty("review").GetProperty("proposedFunction")))
                    session.Commit(changed.GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap "replace commit" |> ignore
                    let changedFn = session.Declarations().Functions |> List.find (fun item -> item.Id = fn.Id)
                    let finalText = replacement + "\U0010FFFF"
                    let targetSelector = if named then $"\"op\":\"simulateReplaceTargetByName\",\"function\":\"{name}\"" else $"\"op\":\"simulateReplaceTarget\",\"functionId\":\"{StableId.value fn.Id}\""
                    let targeted = wireResult session $"""{{"id":"target",{targetSelector},"targetNodeId":"{StableId.value changedFn.Body.ResultNodeId}","replacement":{{"kind":"stringLiteral","value":{literal finalText}}},"structuredReview":true,"detail":"full"}}"""
                    Assert.Equal(finalText, WireInput.runText (targeted.GetProperty("review").GetProperty("diff").GetProperty("proposedTarget").GetProperty("literal").GetProperty("value")))
                    session.Commit(targeted.GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap "target commit" |> ignore
                    let read = wireResult session $"""{{"id":"read","op":"readFunction","function":"{name}"}}"""
                    Assert.Equal(finalText, structuredLiteral (read.GetProperty("function")))
                    name, finalText)
            session.Close()
            use reopened = AuthoringSession.openExisting path |> unwrap "reopen"
            for name, text in expected do
                let read = wireResult reopened $"""{{"id":"read","op":"readFunction","function":"{name}"}}"""
                Assert.Equal(text, structuredLiteral (read.GetProperty("function"))))

    [<Fact>]
    let ``literal review pagination never splits a surrogate pair`` () =
        withLiteralSession (fun _ session ->
            let text = "x" + String.replicate 1100 "雪😀" + "😀end"
            let proposal =
                session.SimulateCreateFunction
                    { MustNotWait = false; Exported = true; ExpectedBaseSnapshotId = current session; DisplayName = "paged"; Parameters = []
                      TypeParameters = None
                      ResultType = stringType; RequiredEffects = []; Body = ExpressionRecipe.StringLiteral text }
                |> unwrap "create"
            let id = proposal.ProposalId
            let page offset limit =
                wireResult session $"""{{"id":"page","op":"readProposal","proposalId":"{id}","path":"/review/createdFunction/body/nodes/0/kind/value","offset":{offset},"limit":{limit}}}"""
            let first = page 0 1
            Assert.Equal("x", WireInput.runText (first.GetProperty("value")))
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
    let ``bounded compact reviews keep astral literal prefixes whole and remain retrievable`` () =
        withLiteralSession (fun _ session ->
            let fn = createFunction session "large" [] stringType [] (ExpressionRecipe.StringLiteral "seed")
            let text = String.replicate 300 "😀"
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
    let ``runPure round-trips astral text and rejects unpaired surrogate arguments and fake responses`` () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-lone-surrogates-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(root, "workspace.json")) |> unwrap "session"
            let echo = createFunction session "echo" [ "text", stringType ] stringType [] (ExpressionRecipe.ParameterSlot 0)
            let join = createFunction session "join" [ "left", stringType; "right", stringType ] stringType [] (ExpressionRecipe.StringConcat(ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.ParameterSlot 1))
            let run (fn: SemanticFunction) arguments expected = PureRun.run session fn.Id arguments expected |> unwrap fn.DisplayName
            for text in texts do
                let result = run echo [ PureRunValue.String text ] (Some(PureRunValue.String text))
                Assert.Equal(PureRunValue.String text, result.Actual)
                Assert.Equal(Some true, result.Matches)
            Assert.Equal(PureRunValue.String "😀\U0010FFFF", (run join [ PureRunValue.String "😀"; PureRunValue.String "\U0010FFFF" ] None).Actual)
            // Fakes: a returned astral value and recorded astral call arguments.
            let effectRequest: AgentCreateEffectDeclarationRequest = { Exported = true; ExpectedBaseSnapshotId = current session; DisplayName = "Lookup" }
            session.SimulateCreateEffect effectRequest |> unwrap "effect" |> publish session
            let effect = session.Declarations().Effects |> List.exactlyOne
            let capabilityRequest: AgentCreateCapabilityDeclarationRequest =
                { TaskBound = false; Exported = true; ExpectedBaseSnapshotId = current session
                  DisplayName = "Dictionary"
                  Operations = [ { MayWait = false; DisplayName = "get"; Parameters = [ { DisplayName = "key"; Type = stringType } ]; ResultType = stringType; FailureType = None; EffectId = effect.Id } ] }
            session.SimulateCreateCapability capabilityRequest |> unwrap "capability" |> publish session
            let dictionary = session.Declarations().Capabilities |> List.exactlyOne
            let get = dictionary.Operations |> List.exactlyOne
            let key = (List.exactlyOne get.Parameters).Id
            createFunction session "ask" [ "key", stringType; "dictionary", TypeRef.Capability dictionary.Id ] stringType [ effect.Id ]
                (ExpressionRecipe.CapabilityInvoke(ExpressionRecipe.ParameterSlot 1, get.Id, [ key, ExpressionRecipe.ParameterSlot 0 ]))
            |> ignore
            // The wire: astral text as surrogate-pair escapes round-trips; an unpaired escape is rejected.
            let send line =
                let response = AuthoringProtocol.executeLine session line
                response, JsonDocument.Parse(response)
            let runPure argument expected =
                $"""{{"id":"run","op":"runPure","functionId":"{StableId.value echo.Id}","arguments":[{{"kind":"string","value":{argument}}}],"expected":{{"kind":"string","value":{expected}}}}}"""
            let text = "😀a\U0010FFFF"
            let raw, document = send (runPure (literal text) (literal text))
            use document = document
            let result = document.RootElement.GetProperty("result")
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean(), raw)
            Assert.True(result.GetProperty("matches").GetBoolean())
            Assert.Equal(text, WireInput.runText (result.GetProperty("actual").GetProperty("value")))
            Assert.Equal(unpaired, wireError session (runPure "\"\\uDC00a\\uD800\"" (literal "a")))
            Assert.Equal(unpaired, wireError session (runPure (literal "a") "\"\\uD800\""))
            let ask argument response =
                $"""{{"id":"ask","op":"runPureByName","function":"ask","arguments":[{{"kind":"string","value":{argument}}},{{"kind":"fakeCapability","capability":"Dictionary","operations":[{{"operation":"get","responses":[{response}]}}]}}]}}"""
            let returned value = $"""{{"kind":"return","value":{{"kind":"string","value":{value}}}}}"""
            let answered = wireResult session (ask (literal "😀") (returned (literal "=😀")))
            Assert.Equal("=😀", WireInput.runText (answered.GetProperty("actual").GetProperty("value")))
            let call = (answered.GetProperty("calls")[0]).GetProperty("arguments")
            Assert.Equal("😀", WireInput.runText ((call[0]).GetProperty("value").GetProperty("value")))
            // Fake responses are rejected as an invalid run value at the response's path, with the reason.
            for response in [ returned "\"=\\uD83D\""; """{"kind":"throw","message":"x\uDFFF"}""" ] do
                let error = wireError session (ask (literal "k") response)
                Assert.StartsWith("Invalid run value at arguments[1].operations[0].responses[0]", error)
                Assert.Contains(unpaired, error)
        finally
            if Directory.Exists root then Directory.Delete(root, true)
