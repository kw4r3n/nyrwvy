namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Name-resolved declaration and run-value input over the maintained JSONL protocol, without caller-held IDs.
module PureRunByNameTests =
    let private execute session (line: string) = AuthoringProtocol.executeLine session line |> JsonDocument.Parse

    let private result session line =
        use reply = execute session line
        if not (reply.RootElement.GetProperty("ok").GetBoolean()) then failwithf "JSONL request failed: %s" (reply.RootElement.GetRawText())
        reply.RootElement.GetProperty("result").Clone()

    let private rejected session line =
        use reply = execute session line
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean())
        let error = reply.RootElement.GetProperty("error")
        error.GetProperty("code").GetString(), error.GetProperty("message").GetString()

    let private publish session line =
        let proposal = result session line
        result session $"""{{"id":"commit","op":"commit","proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}""" |> ignore
        proposal

    let private str = """{"kind":"primitive","primitive":"string"}"""

    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-run-by-name-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            use session =
                match AuthoringSession.createNew (Path.Combine(root, "workspace.json")) with
                | Ok value -> value
                | Error error -> failwithf "Session creation failed: %A" error
            let effectId =
                (publish session """{"id":"e","op":"simulateCreateEffect","displayName":"io"}""")
                    .GetProperty("review").GetProperty("createdEffect").GetProperty("id").GetString()
            publish session $"""{{"id":"t1","op":"simulateCreateTypeByName","displayName":"Request","typeKind":{{"kind":"product","fields":[{{"displayName":"key","type":{str}}}]}}}}""" |> ignore
            publish session $"""{{"id":"t2","op":"simulateCreateTypeByName","displayName":"Answer","typeKind":{{"kind":"union","variants":[{{"displayName":"Found","fields":[{{"displayName":"text","type":{str}}}]}},{{"displayName":"Missing","fields":[]}}]}}}}""" |> ignore
            publish session $"""{{"id":"c","op":"simulateCreateCapabilityByName","displayName":"Provider","operations":[{{"displayName":"lookup","parameters":[{{"displayName":"key","type":{str}}}],"resultType":{{"kind":"named","name":"Answer"}},"effect":"io"}}]}}""" |> ignore
            publish session """{"id":"f","op":"simulateCreateFunctionByName","displayName":"query","parameters":[{"displayName":"request","type":{"kind":"named","name":"Request"}},{"displayName":"provider","type":{"kind":"capability","name":"Provider"}}],"resultType":{"kind":"named","name":"Answer"},"requiredEffects":["io"],"body":{"kind":"capabilityInvoke","capabilityValue":{"kind":"parameter","name":"provider"},"capability":"Provider","operation":"lookup","arguments":[{"parameter":"key","value":{"kind":"fieldAccess","source":{"kind":"parameter","name":"request"},"type":"Request","field":"key"}}]}}""" |> ignore
            run session effectId
        finally
            Directory.Delete(root, true)

    let private request key = $"""{{"kind":"product","type":"Request","fields":[{{"field":"key","value":{{"kind":"string","value":"{key}"}}}}]}}"""
    let private found text = $"""{{"kind":"union","type":"Answer","variant":"Found","fields":[{{"field":"text","value":{{"kind":"string","value":"{text}"}}}}]}}"""
    let private missing = """{"kind":"union","type":"Answer","variant":"Missing","fields":[]}"""

    let private provider cases =
        $"""{{"kind":"fakeCapability","capability":"Provider","operations":[{{"operation":"lookup","cases":[{cases}]}}]}}"""

    let private case key answer = $"""{{"arguments":[{{"parameter":"key","value":{{"kind":"string","value":"{key}"}}}}],"responses":[{{"kind":"return","value":{answer}}}]}}"""

    let private run arguments expected =
        $"""{{"id":"run","op":"runPureByName","function":"query","arguments":[{arguments}],"expected":{expected}}}"""

    [<Fact>]
    let ``name-resolved declarations and run values execute without caller-held IDs`` () =
        withSession (fun session _ ->
            let declarations = result session """{"id":"d","op":"declarations"}"""
            let idOf collection name =
                declarations.GetProperty(collection: string).EnumerateArray()
                |> Seq.find (fun item -> item.GetProperty("displayName").GetString() = name)
                |> fun item -> item.GetProperty("id").GetString()
            let cases = String.Join(",", [ case "a" (found "A"); case "b" missing ])
            for key, expected in [ "a", found "A"; "b", missing ] do
                let answer = result session (run (String.Join(",", [ request key; provider cases ])) expected)
                Assert.True(answer.GetProperty("matches").GetBoolean())
                Assert.Equal(1, answer.GetProperty("calls").GetArrayLength())
                // Names lower to the published identities; results are reported in the exact-ID form.
                Assert.Equal(idOf "functions" "query", answer.GetProperty("functionId").GetString())
                Assert.Equal(idOf "types" "Answer", answer.GetProperty("actual").GetProperty("typeId").GetString()))

    [<Fact>]
    let ``run output reports display names next to exact IDs`` () =
        withSession (fun session _ ->
            let name (element: JsonElement) key = element.GetProperty(key: string).GetString()
            let fake = provider (case "a" (found "A"))
            for op in [ "runPureByName"; "runPure" ] do
                let line =
                    if op = "runPureByName" then run (String.Join(",", [ request "a"; fake ])) (found "B")
                    else
                        // The exact-ID form gets the same names; its request is built from the by-name reply.
                        let byName = result session (run (String.Join(",", [ request "a"; fake ])) (found "A"))
                        let actual = byName.GetProperty("actual")
                        let call = byName.GetProperty("calls").[0]
                        let requestType = (result session """{"id":"d","op":"declarations"}""").GetProperty("types").EnumerateArray() |> Seq.find (fun item -> name item "displayName" = "Request")
                        let keyField = requestType.GetProperty("typeKind").GetProperty("fields").[0].GetProperty("id").GetString()
                        let value = $"""{{"kind":"string","value":"a"}}"""
                        let requestValue = $"""{{"kind":"product","typeId":"{name requestType "id"}","fields":[{{"fieldId":"{keyField}","value":{value}}}]}}"""
                        let capabilityId = (result session """{"id":"d","op":"declarations"}""").GetProperty("capabilities").[0].GetProperty("id").GetString()
                        let fakeValue = $"""{{"kind":"fakeCapability","capabilityId":"{capabilityId}","operations":[{{"operationId":"{name call "operationId"}","responses":[{{"kind":"return","value":{actual.GetRawText()}}}]}}]}}"""
                        $"""{{"id":"run","op":"runPure","functionId":"{name byName "functionId"}","arguments":[{requestValue},{fakeValue}]}}"""
                let answer = result session line
                Assert.Equal("query", name answer "function")
                let actual = answer.GetProperty("actual")
                Assert.Equal("Answer", name actual "type")
                Assert.Equal("Found", name actual "variant")
                let field = actual.GetProperty("fields").[0]
                Assert.Equal("text", name field "field")
                Assert.False(String.IsNullOrEmpty(name field "fieldId"))
                let call = answer.GetProperty("calls").[0]
                Assert.Equal("provider", name call "parameter")
                Assert.Equal("Provider", name call "capability")
                Assert.Equal("lookup", name call "operation")
                Assert.Equal("key", name (call.GetProperty("arguments").[0]) "parameter")
                if op = "runPureByName" then
                    let mismatch = answer.GetProperty("mismatch")
                    let step = mismatch.GetProperty("path").[0]
                    Assert.Equal("Answer", name step "type")
                    Assert.Equal("text", name step "field")
                    Assert.Equal("B", name (mismatch.GetProperty("expected")) "value")
            // Runtime failures name the function and the calls made before the failure.
            use reply = execute session (run (String.Join(",", [ request "z"; fake ])) (found "A"))
            let error = reply.RootElement.GetProperty("error")
            Assert.Equal("RunBindingNoMatch", name error "code")
            Assert.Equal("query", name error "function"))

    [<Fact>]
    let ``name-resolved run values reject unknown mismatched and mixed selectors before running`` () =
        withSession (fun session effectId ->
            let fake = provider (case "a" (found "A"))
            let protocolError line (fragment: string) =
                let code, message = rejected session line
                Assert.Equal("ProtocolError", code)
                Assert.Contains(fragment, message)
            protocolError (run (String.Join(",", [ request "a"; fake ])) (found "A") |> fun line -> line.Replace("\"function\":\"query\"", "\"function\":\"absent\"")) "Unknown function 'absent'"
            protocolError (run (String.Join(",", [ request "a"; fake ])) (found "A") |> fun line -> line.Replace("\"function\":\"query\"", "\"functionId\":\"query\"")) "functionId"
            protocolError (run (String.Join(",", [ (request "a").Replace("\"field\":\"key\"", "\"field\":\"nope\""); fake ])) (found "A")) "Unknown field on Request 'nope'"
            protocolError (run (String.Join(",", [ (request "a").Replace("\"field\":", "\"fieldId\":"); fake ])) (found "A")) "fieldId"
            protocolError (run (String.Join(",", [ (request "a").Replace("\"type\":\"Request\"", "\"type\":\"Answer\""); fake ])) (found "A")) "Type 'Answer' is not a product type."
            protocolError (run (String.Join(",", [ request "a"; fake ])) ((found "A").Replace("\"variant\":\"Found\"", "\"variant\":\"Missing\""))) "Unknown field on variant Missing 'text'"
            protocolError (run (String.Join(",", [ request "a"; fake.Replace("\"operation\":\"lookup\"", "\"operation\":\"write\"") ])) (found "A")) "Unknown operation on Provider 'write'"
            protocolError (run (String.Join(",", [ request "a"; fake.Replace("\"parameter\":\"key\"", "\"parameter\":\"text\"") ])) (found "A")) "Unknown parameter on operation lookup 'text'"
            protocolError (run (String.Join(",", [ request "a"; """{"kind":"hostCapability","capability":"Absent","implementation":"Host"}""" ])) (found "A")) "Unknown capability 'Absent'"
            // The exact-ID operation keeps its stable-ID vocabulary.
            protocolError """{"id":"x","op":"runPure","function":"query","arguments":[]}""" "function"
            // A duplicate capability name is refused by the convenience operation and by exact-ID creation.
            protocolError $"""{{"id":"c2","op":"simulateCreateCapabilityByName","displayName":"Provider","operations":[]}}""" "Capability display name 'Provider' already exists"
            protocolError $"""{{"id":"c3","op":"simulateCreateCapabilityByName","displayName":"Other","operations":[{{"displayName":"lookup","parameters":[],"resultType":{str},"effect":"absent"}}]}}""" "Unknown effect 'absent'"
            do
                use reply = execute session $"""{{"id":"c4","op":"simulateCreateCapability","displayName":"Provider","operations":[{{"displayName":"lookup","parameters":[],"resultType":{str},"effectId":"{effectId}"}}]}}"""
                Assert.False(reply.RootElement.GetProperty("ok").GetBoolean())
                Assert.Contains("CapabilityDeclarationDuplicateDisplayName", reply.RootElement.GetRawText())
            // Name resolution therefore stays unambiguous.
            Assert.True((result session (run (String.Join(",", [ request "a"; fake ])) (found "A"))).GetProperty("matches").GetBoolean()))

    // Exercise the protocol budget as well as the semantic/runner budgets: a tagged child
    // must be charged by recursive parsing once, while flat payloads still consume nodes.
    let private intType = """{"kind":"primitive","primitive":"int64"}"""
    let private listInts = $"""{{"kind":"builtinGeneric","generic":"list","arguments":[{intType}]}}"""
    let private mapInts = $"""{{"kind":"builtinGeneric","generic":"map","arguments":[{intType},{intType}]}}"""
    let private intValue n = $"""{{"kind":"int64","value":"{n}"}}"""
    let private scalarMap count =
        let entries = [ 0 .. count - 1 ] |> List.map (fun n -> $"""{{"key":{intValue n},"value":{intValue n}}}""") |> String.concat ","
        $"""{{"kind":"map","keyType":{intType},"valueType":{intType},"entries":[{entries}]}}"""
    let private scalarList count =
        let items = [ 0 .. count - 1 ] |> List.map string |> String.concat ","
        $"""{{"kind":"list","itemKind":"int64","items":[{items}]}}"""
    let private identity session name typeRef =
        publish session $"""{{"id":"identity","op":"simulateCreateFunctionByName","displayName":"{name}","parameters":[{{"displayName":"value","type":{typeRef}}}],"resultType":{typeRef},"requiredEffects":[],"body":{{"kind":"parameter","name":"value"}}}}""" |> ignore
    let private runValue session name value =
        result session $"""{{"id":"budget","op":"runPureByName","function":"{name}","arguments":[{value}]}}"""
    let private budgetRejected session name value =
        let code, message = rejected session $"""{{"id":"budget","op":"runPureByName","function":"{name}","arguments":[{value}]}}"""
        Assert.Equal("ProtocolError", code)
        Assert.Contains("8192 value nodes per argument or expected value", message)
        Assert.Contains("arguments[0]", message)
        Assert.Contains("at least 8193 nodes", message)
        Assert.Contains("Split the input", message)

    [<Fact>]
    let ``budget guidance identifies expectation and review case without publishing`` () =
        withSession (fun session _ ->
            identity session "budgetLocation" mapInts
            let before = File.ReadAllBytes session.Path
            let oversized = scalarMap 4096
            let code, message = rejected session $"""{{"id":"expected","op":"runPureByName","function":"budgetLocation","arguments":[{scalarMap 0}],"expected":{oversized}}}"""
            Assert.Equal("ProtocolError", code)
            Assert.Contains("At expected:", message)
            let proposal = result session """{"id":"proposal","op":"simulateReplaceBodyByName","function":"budgetLocation","replacement":{"kind":"parameter","name":"value"}}"""
            let proposalId = proposal.GetProperty("proposalId").GetString()
            let fn = session.Declarations().Functions |> List.find (fun f -> f.DisplayName = "budgetLocation")
            let functionId = Nyrwvy.Core.StableId.value fn.Id
            for arguments, expected, location in
                [ oversized, "", "cases[0].arguments[0]"
                  scalarMap 0, $",\"expectedCandidate\":{oversized}", "cases[0].expectedCandidate" ] do
                let line = $"""{{"id":"case","op":"verifyChange","proposalId":"{proposalId}","cases":[{{"label":"budget","functionId":"{functionId}","arguments":[{arguments}]{expected}}}]}}"""
                let code, message = rejected session line
                Assert.Equal("ProtocolError", code)
                Assert.Contains($"At {location}:", message)
                Assert.Contains("per argument or expected value", message)
            Assert.Equal<byte>(before, File.ReadAllBytes session.Path))

    [<Fact>]
    let ``protocol counts Map and Set tagged children once and roundtrips output`` () =
        withSession (fun session _ ->
            identity session "budgetMap" mapInts
            let map = runValue session "budgetMap" (scalarMap 2048)
            let actual = map.GetProperty("actual")
            Assert.Equal(2048, actual.GetProperty("entries").GetArrayLength())
            Assert.Equal(actual.GetRawText(), (runValue session "budgetMap" (actual.GetRawText())).GetProperty("actual").GetRawText())
            Assert.Equal(4095, (runValue session "budgetMap" (scalarMap 4095)).GetProperty("actual").GetProperty("entries").GetArrayLength())
            budgetRejected session "budgetMap" (scalarMap 4096) // 1 + 2 * 4096 = 8193 genuine nodes
            let setType = $"""{{"kind":"builtinGeneric","generic":"set","arguments":[{intType}]}}"""
            identity session "budgetSet" setType
            let items = [ 0 .. 4095 ] |> List.map intValue |> String.concat ","
            let setValue = $"""{{"kind":"set","itemType":{intType},"items":[{items}]}}"""
            let actual = (runValue session "budgetSet" setValue).GetProperty("actual")
            Assert.Equal(4096, actual.GetProperty("items").GetArrayLength())
            Assert.Equal(actual.GetRawText(), (runValue session "budgetSet" (actual.GetRawText())).GetProperty("actual").GetRawText()))

    [<Fact>]
    let ``protocol accepts exactly 8192 nodes through nested Result and rejects 8193`` () =
        withSession (fun session _ ->
            let resultType inner = $"""{{"kind":"builtinGeneric","generic":"result","arguments":[{inner},{str}]}}"""
            let resultValue inner value = $"""{{"kind":"result","okType":{inner},"errorType":{str},"isOk":true,"value":{value}}}"""
            identity session "budgetResult" (resultType mapInts)
            let exact = resultValue mapInts (scalarMap 4095) // Result + Map + 8190 leaves
            Assert.True((runValue session "budgetResult" exact).GetProperty("actual").GetProperty("isOk").GetBoolean())
            identity session "budgetNestedResult" (resultType (resultType mapInts))
            budgetRejected session "budgetNestedResult" (resultValue (resultType mapInts) exact))

    let private createBudgetHolder session kind =
        let fields = $"""[{{"displayName":"first","type":{listInts}}},{{"displayName":"second","type":{listInts}}},{{"displayName":"tag","type":{intType}}}]"""
        let typeKind =
            if kind = "product" then $"""{{"kind":"product","fields":{fields}}}"""
            else $"""{{"kind":"union","variants":[{{"displayName":"Held","fields":{fields}}}]}}"""
        publish session $"""{{"id":"holder","op":"simulateCreateTypeByName","displayName":"BudgetHolder","typeKind":{typeKind}}}""" |> ignore
    let private holderValue kind first second =
        let variant = if kind = "union" then ", \"variant\":\"Held\"" else ""
        $"""{{"kind":"{kind}","type":"BudgetHolder"{variant},"fields":[{{"field":"first","value":{scalarList first}}},{{"field":"second","value":{scalarList second}}},{{"field":"tag","value":{intValue 0}}}]}}"""

    [<Theory>]
    [<InlineData("product")>]
    [<InlineData("union")>]
    let ``protocol counts named fields once at the genuine value budget`` kind =
        withSession (fun session _ ->
            createBudgetHolder session kind
            identity session "budgetHolder" """{"kind":"named","name":"BudgetHolder"}"""
            // Holder + first List + 4096 leaves + second List + 4092 leaves + tag = 8192.
            let actual = (runValue session "budgetHolder" (holderValue kind 4096 4092)).GetProperty("actual")
            Assert.Equal(3, actual.GetProperty("fields").GetArrayLength())
            budgetRejected session "budgetHolder" (holderValue kind 4096 4093))

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``fake queues and keyed cases preserve payload and metadata budgets`` keyed =
        withSession (fun session _ ->
            createBudgetHolder session "product"
            publish session """{"id":"cap","op":"simulateCreateCapabilityByName","displayName":"BudgetProvider","operations":[{"displayName":"get","parameters":[],"resultType":{"kind":"named","name":"BudgetHolder"},"effect":"io"}]}""" |> ignore
            publish session """{"id":"fn","op":"simulateCreateFunctionByName","displayName":"budgetFake","parameters":[{"displayName":"provider","type":{"kind":"capability","name":"BudgetProvider"}}],"resultType":{"kind":"named","name":"BudgetHolder"},"requiredEffects":["io"],"body":{"kind":"capabilityInvoke","capabilityValue":{"kind":"parameter","name":"provider"},"capability":"BudgetProvider","operation":"get","arguments":[]}}""" |> ignore
            let fake second =
                let response = $"""{{"kind":"return","value":{holderValue "product" 4096 second}}}"""
                let script = if keyed then $""""cases":[{{"arguments":[],"responses":[{response}]}}]""" else $""""responses":[{response}]"""
                $"""{{"kind":"fakeCapability","capability":"BudgetProvider","operations":[{{"operation":"get",{script}}}]}}"""
            // Queue: fake + payload. Keyed: also one case and one response metadata node.
            let second = if keyed then 4089 else 4091
            Assert.Equal(1, (runValue session "budgetFake" (fake second)).GetProperty("calls").GetArrayLength())
            budgetRejected session "budgetFake" (fake (second + 1)))

    [<Fact>]
    let ``fake keyed arguments count their wrapper and tagged payload once`` () =
        withSession (fun session _ ->
            createBudgetHolder session "product"
            publish session """{"id":"cap","op":"simulateCreateCapabilityByName","displayName":"BudgetEcho","operations":[{"displayName":"send","parameters":[{"displayName":"payload","type":{"kind":"named","name":"BudgetHolder"}}],"resultType":{"kind":"primitive","primitive":"unit"},"effect":"io"}]}""" |> ignore
            publish session """{"id":"fn","op":"simulateCreateFunctionByName","displayName":"budgetEcho","parameters":[{"displayName":"payload","type":{"kind":"named","name":"BudgetHolder"}},{"displayName":"provider","type":{"kind":"capability","name":"BudgetEcho"}}],"resultType":{"kind":"primitive","primitive":"unit"},"requiredEffects":["io"],"body":{"kind":"capabilityInvoke","capabilityValue":{"kind":"parameter","name":"provider"},"capability":"BudgetEcho","operation":"send","arguments":[{"parameter":"payload","value":{"kind":"parameter","name":"payload"}}]}}""" |> ignore
            let payload = holderValue "product" 4096 4087 // 8187 genuine value nodes
            let fake second =
                $"""{{"kind":"fakeCapability","capability":"BudgetEcho","operations":[{{"operation":"send","cases":[{{"arguments":[{{"parameter":"payload","value":{holderValue "product" 4096 second}}}],"responses":[{{"kind":"return","value":{{"kind":"unit"}}}}]}}]}}]}}"""
            // fake + case + argument metadata + response metadata + Unit + payload = 8192.
            let line fake = $"""{{"id":"run","op":"runPureByName","function":"budgetEcho","arguments":[{payload},{fake}]}}"""
            Assert.Equal("unit", (result session (line (fake 4087))).GetProperty("actual").GetProperty("kind").GetString())
            let code, message = rejected session (line (fake 4088))
            Assert.Equal("ProtocolError", code)
            Assert.Contains("8192 value nodes per argument or expected value", message)
            Assert.Contains("At arguments[1]:", message))

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``queued throw messages count once at the same protocol and direct run budget`` delayed =
        withSession (fun session _ ->
            let operations = [ 0 .. 31 ] |> List.map (fun n ->
                $"""{{"displayName":"op{n}","parameters":[],"resultType":{{"kind":"primitive","primitive":"unit"}},"effect":"io"}}""") |> String.concat ","
            publish session $"""{{"id":"cap","op":"simulateCreateCapabilityByName","displayName":"ThrowBudget","operations":[{operations}]}}""" |> ignore
            publish session """{"id":"fn","op":"simulateCreateFunctionByName","displayName":"ignoreThrows","parameters":[{"displayName":"provider","type":{"kind":"capability","name":"ThrowBudget"}}],"resultType":{"kind":"primitive","primitive":"bool"},"requiredEffects":[],"body":{"kind":"cancellationRequested"}}""" |> ignore
            let response = if delayed then """{"kind":"throw","message":"","delayMillis":1}""" else """{"kind":"throw","message":""}"""
            let script lastCount =
                let ops = [ 0 .. 31 ] |> List.map (fun n ->
                    let responses = List.replicate (if n = 31 then lastCount else 256) response |> String.concat ","
                    $"""{{"operation":"op{n}","responses":[{responses}]}}""") |> String.concat ","
                $"""{{"kind":"fakeCapability","capability":"ThrowBudget","operations":[{ops}]}}"""
            let accepted = (runValue session "ignoreThrows" (script 255)).GetProperty("actual") // root + 8191
            Assert.Equal("bool", accepted.GetProperty("kind").GetString())
            Assert.False(accepted.GetProperty("value").GetBoolean())
            budgetRejected session "ignoreThrows" (script 256) // root + 8192
            let capability = session.Declarations().Capabilities |> List.find (fun c -> c.DisplayName = "ThrowBudget")
            let fn = session.Declarations().Functions |> List.find (fun f -> f.DisplayName = "ignoreThrows")
            let throw = PureRunFakeResponse.Throw ""
            let throw = if delayed then PureRunFakeResponse.Delayed(1, throw) else throw
            let fake = PureRunValue.FakeCapability(capability.Id, capability.Operations |> List.map (fun op ->
                { OperationId = op.Id; Responses = List.replicate 256 throw; Cases = None }))
            match PureRun.run session fn.Id [ fake ] None with
            | Error(PureRunError.Unsupported("RunArgumentTooLarge", _)) -> ()
            | other -> failwithf "Direct API admitted queued throws outside the budget: %A" other)
