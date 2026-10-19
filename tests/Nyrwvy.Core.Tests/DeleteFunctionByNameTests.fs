namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module DeleteFunctionByNameTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private execute (session: AuthoringSession) (line: string) =
        (AuthoringProtocol.executeLine session line |> JsonDocument.Parse).RootElement.Clone()
    let private send session line =
        let response = execute session line
        Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString())
        response.GetProperty("result")
    let private error session line =
        let response = execute session line
        Assert.False(response.GetProperty("ok").GetBoolean(), response.ToString())
        response.GetProperty("error")
    let private primitive name = $"""{{"kind":"primitive","primitive":"{name}"}}"""
    let private flag = """{"kind":"parameter","name":"flag"}"""
    let private call name = $"""{{"kind":"directCall","function":"{name}","arguments":[{{"parameter":"flag","value":{flag}}}]}}"""
    let private create (session: AuthoringSession) name body =
        let line =
            $"""{{"id":"create","op":"simulateCreateFunctionByName","exported":true,"displayName":"{name}","parameters":[{{"displayName":"flag","type":{primitive "bool"}}}],"resultType":{primitive "bool"},"requiredEffects":[],"body":{body}}}"""
        let result = send session line
        session.Commit(result.GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
    let private deleteByName (operations: string list) =
        let items = operations |> String.concat ","
        $"""{{"id":"delete","op":"simulateOperationsByName","operations":[{items}]}}"""
    let private deleteFunction name = $"""{{"kind":"deleteFunction","function":"{name}"}}"""
    let private functionId (session: AuthoringSession) name =
        session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name) |> fun fn -> StableId.value fn.Id
    let private removedNames (result: JsonElement) =
        result.GetProperty("review").GetProperty("diff").GetProperty("functionsRemoved").EnumerateArray()
        |> Seq.map (fun item -> item.GetProperty("displayName").GetString())
        |> Set.ofSeq

    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-delete-by-name-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(root, "workspace.json")) |> unwrap
            run session
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact>]
    let ``a self-recursive function is deleted by name because its own self-calls do not block deletion`` () =
        withSession (fun session ->
            // countdown(flag) = if flag then false else countdown(flag): its only reference is a SelfCall.
            create session "countdown" $"""{{"kind":"conditional","condition":{flag},"whenTrue":{{"kind":"boolLiteral","value":false}},"whenFalse":{call "countdown"}}}"""
            let result = send session (deleteByName [ deleteFunction "countdown" ])
            Assert.Equal<Set<string>>(set [ "countdown" ], removedNames result)
            session.Commit(result.GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            Assert.Empty(session.Declarations().Functions))

    [<Fact>]
    let ``exact-ID deletion of a self-recursive function also succeeds`` () =
        withSession (fun session ->
            create session "loop" (call "loop")
            let id = functionId session "loop"
            let result = send session $"""{{"id":"delete","op":"simulateOperations","operations":[{{"kind":"deleteFunction","functionId":"{id}"}}]}}"""
            Assert.Equal<Set<string>>(set [ "loop" ], removedNames result))

    [<Fact>]
    let ``a rejected deletion names the function and every referring function`` () =
        withSession (fun session ->
            create session "leaf" flag
            create session "caller" (call "leaf")
            create session "other" (call "leaf")
            let failure = error session (deleteByName [ deleteFunction "leaf" ])
            Assert.Equal("SimulationRejected", failure.GetProperty("code").GetString())
            let diagnostic = failure.GetProperty("details").GetProperty("diagnostics").[0]
            Assert.Equal("FunctionStillReferenced", diagnostic.GetProperty("code").GetString())
            let message = diagnostic.GetProperty("message").GetString()
            for name in [ "leaf"; "caller"; "other" ] do
                let id = functionId session name
                Assert.Contains($"'{name}' ({id})", message)

            // Deleting the referrers earlier in the same batch clears the references.
            let result = send session (deleteByName [ deleteFunction "caller"; deleteFunction "other"; deleteFunction "leaf" ])
            Assert.Equal<Set<string>>(set [ "leaf"; "caller"; "other" ], removedNames result))

    [<Fact>]
    let ``simulateOperationsByName rejects unknown names, exact-ID keys and unsupported kinds`` () =
        withSession (fun session ->
            create session "leaf" flag
            let protocolMessage line =
                let failure = error session line
                Assert.Equal("ProtocolError", failure.GetProperty("code").GetString())
                failure.GetProperty("message").GetString()
            Assert.Contains("Unknown function 'missing'", protocolMessage (deleteByName [ deleteFunction "missing" ]))
            let id = functionId session "leaf"
            protocolMessage (deleteByName [ $"""{{"kind":"deleteFunction","functionId":"{id}"}}""" ]) |> ignore
            Assert.Contains(
                "Unsupported name-resolved operation kind 'frobnicate'",
                protocolMessage (deleteByName [ """{"kind":"frobnicate","function":"leaf"}""" ]))
            Assert.Contains("Unknown type 'Missing'", protocolMessage (deleteByName [ """{"kind":"deleteType","type":"Missing"}""" ]))
            Assert.Single(session.Declarations().Functions) |> ignore)
