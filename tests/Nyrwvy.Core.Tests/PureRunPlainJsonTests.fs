namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// runPure arguments and expected values as plain JSON, decoded against the declared parameter and result types.
module PureRunPlainJsonTests =
    let private execute session (line: string) = AuthoringProtocol.executeLine session line |> JsonDocument.Parse

    let private result session line =
        use reply = execute session line
        if not (reply.RootElement.GetProperty("ok").GetBoolean()) then failwithf "JSONL request failed: %s" (reply.RootElement.GetRawText())
        reply.RootElement.GetProperty("result").Clone()

    let private rejected session line =
        use reply = execute session line
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean())
        reply.RootElement.GetProperty("error").GetProperty("message").GetString()

    let private publish session line =
        let proposal = result session line
        result session $"""{{"id":"commit","op":"commit","proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}""" |> ignore

    let private str = """{"kind":"primitive","primitive":"string"}"""
    let private int64 = """{"kind":"primitive","primitive":"int64"}"""
    let private generic name arguments = $"""{{"kind":"builtinGeneric","generic":"{name}","arguments":[{String.Join(",", (arguments: string list))}]}}"""

    /// Declares `name` as the identity function over one parameter of `typeJson`.
    let private identity session name typeJson =
        publish session $"""{{"id":"f","op":"simulateCreateFunctionByName","exported":true,"displayName":"{name}","parameters":[{{"displayName":"x","type":{typeJson}}}],"resultType":{typeJson},"requiredEffects":[],"body":{{"kind":"parameter","name":"x"}}}}"""

    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-run-plain-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            use session =
                match AuthoringSession.createNew (Path.Combine(root, "workspace.json")) with
                | Ok value -> value
                | Error error -> failwithf "Session creation failed: %A" error
            publish session $"""{{"id":"t1","op":"simulateCreateTypeByName","exported":true,"displayName":"Request","typeKind":{{"kind":"product","fields":[{{"displayName":"key","type":{str}}},{{"displayName":"count","type":{int64}}}]}}}}"""
            publish session $"""{{"id":"t2","op":"simulateCreateTypeByName","exported":true,"displayName":"Answer","typeKind":{{"kind":"union","variants":[{{"displayName":"Found","fields":[{{"displayName":"text","type":{str}}}]}},{{"displayName":"Missing","fields":[]}}]}}}}"""
            identity session "nested" (generic "list" [ generic "list" [ int64 ] ])
            identity session "request" """{"kind":"named","name":"Request"}"""
            identity session "answers" (generic "list" [ """{"kind":"named","name":"Answer"}""" ])
            identity session "maybe" (generic "option" [ str ])
            identity session "outcome" (generic "result" [ int64; str ])
            run session
        finally
            Directory.Delete(root, true)

    let private matches session op functionName (value: string) =
        let functionKey = if op = "runPureByName" then "function" else "functionId"
        let functionRef =
            if op = "runPureByName" then functionName
            else
                (result session """{"id":"d","op":"declarations"}""").GetProperty("functions").EnumerateArray()
                |> Seq.find (fun item -> item.GetProperty("displayName").GetString() = functionName)
                |> fun item -> item.GetProperty("id").GetString()
        let answer = result session $"""{{"id":"run","op":"{op}","{functionKey}":"{functionRef}","arguments":[{value}],"expected":{value}}}"""
        answer.GetProperty("matches").GetBoolean()

    [<Fact>]
    let ``plain JSON arguments decode against declared types in both run operations`` () =
        withSession (fun session ->
            for op in [ "runPure"; "runPureByName" ] do
                Assert.True(matches session op "nested" "[[1,2],[3],[]]")
                Assert.True(matches session op "request" """{"key":"a","count":"9007199254740993"}""")
                Assert.True(matches session op "answers" """[{"Found":{"text":"x"}},{"Missing":{}}]""")
                Assert.True(matches session op "maybe" "null")
                Assert.True(matches session op "maybe" "\"v\"")
                Assert.True(matches session op "outcome" """{"ok":1}""")
                Assert.True(matches session op "outcome" """{"error":"bad"}"""))

    [<Fact>]
    let ``tagged run values still decode inside plain JSON`` () =
        withSession (fun session ->
            Assert.True(matches session "runPureByName" "nested" """[{"kind":"list","itemKind":"int64","items":[1,2]},[3]]"""))

    [<Fact>]
    let ``plain JSON that does not fit the declared type is rejected with its location`` () =
        withSession (fun session ->
            let run functionName (value: string) =
                rejected session $"""{{"id":"run","op":"runPureByName","function":"{functionName}","arguments":[{value}]}}"""
            Assert.Contains("Missing field 'count'", run "request" """{"key":"a"}""")
            Assert.Contains("no field 'other'", run "request" """{"key":"a","count":1,"other":2}""")
            Assert.Contains("more than once", run "request" """{"key":"a","key":"b","count":1}""")
            Assert.Contains("arguments[0][1][0]", run "nested" """[[1],["x"]]""")
            Assert.Contains("Result", run "outcome" """{"ok":1,"error":"x"}"""))
