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
            // A duplicate capability name is refused by the convenience operation but can still exist via exact IDs.
            protocolError $"""{{"id":"c2","op":"simulateCreateCapabilityByName","displayName":"Provider","operations":[]}}""" "Capability display name 'Provider' already exists"
            protocolError $"""{{"id":"c3","op":"simulateCreateCapabilityByName","displayName":"Other","operations":[{{"displayName":"lookup","parameters":[],"resultType":{str},"effect":"absent"}}]}}""" "Unknown effect 'absent'"
            publish session $"""{{"id":"c4","op":"simulateCreateCapability","displayName":"Provider","operations":[{{"displayName":"lookup","parameters":[],"resultType":{str},"effectId":"{effectId}"}}]}}""" |> ignore
            protocolError (run (String.Join(",", [ request "a"; fake ])) (found "A")) "Ambiguous capability 'Provider'")
