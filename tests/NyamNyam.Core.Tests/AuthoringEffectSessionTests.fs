namespace NyamNyam.Core.Tests

open System
open System.IO
open System.Text.Json
open NyamNyam.Tools.AuthoringSession
open Xunit

module AuthoringEffectSessionTests =
    let private tempDirectory () =
        let path = Path.Combine(Path.GetTempPath(), "nyamnyam-effect-session-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(path) |> ignore
        path

    let private requireOk (response: string): JsonDocument =
        let document = JsonDocument.Parse(response)
        if not (document.RootElement.GetProperty("ok").GetBoolean()) then
            let rendered = document.RootElement.GetRawText()
            document.Dispose()
            failwithf "JSONL request failed: %s" rendered
        document

    [<Fact>]
    let ``ordinary authoring session creates persists and rediscovers an effect declaration`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        try
            use session =
                match AuthoringSession.createNew path with
                | Ok value -> value
                | Error error -> failwithf "Session creation failed: %A" error

            let ids: string * string =
                use document =
                    AuthoringProtocol.executeLine
                        session
                        """{"id":"effect","op":"simulateCreateEffect","displayName":"io"}"""
                    |> requireOk
                let result: JsonElement = document.RootElement.GetProperty("result")
                let createdEffect: JsonElement = result.GetProperty("review").GetProperty("createdEffect")
                result.GetProperty("proposalId").GetString(),
                createdEffect.GetProperty("id").GetString()
            let proposalId, effectId = ids

            use commitResponse =
                AuthoringProtocol.executeLine
                    session
                    (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId)
                |> requireOk
            Assert.True(commitResponse.RootElement.GetProperty("ok").GetBoolean())
            session.Close()

            use reopened =
                match AuthoringSession.openExisting path with
                | Ok value -> value
                | Error error -> failwithf "Session reopen failed: %A" error
            use declarations =
                AuthoringProtocol.executeLine reopened """{"id":"decls","op":"declarations"}"""
                |> requireOk
            let effects: JsonElement = declarations.RootElement.GetProperty("result").GetProperty("effects")
            Assert.Equal(1, effects.GetArrayLength())
            Assert.Equal<string>(effectId, effects[0].GetProperty("id").GetString())
            Assert.Equal("io", effects[0].GetProperty("displayName").GetString())
        finally
            if Directory.Exists root then Directory.Delete(root, true)
