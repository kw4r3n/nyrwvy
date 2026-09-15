namespace NyamNyam.Core.Tests

open System
open System.IO
open System.Text.Json
open NyamNyam.Tools.AuthoringSession
open Xunit

module AuthoringTypeSessionTests =
    let private tempDirectory () =
        let path = Path.Combine(Path.GetTempPath(), "nyamnyam-type-session-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(path) |> ignore
        path

    let private requireOk response =
        let document = JsonDocument.Parse(response)
        if not (document.RootElement.GetProperty("ok").GetBoolean()) then
            let rendered = document.RootElement.GetRawText()
            document.Dispose()
            failwithf "JSONL request failed: %s" rendered
        document

    [<Fact>]
    let ``ordinary authoring session creates persists and rediscovers a product declaration`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        try
            use session =
                match AuthoringSession.createNew path with
                | Ok value -> value
                | Error error -> failwithf "Session creation failed: %A" error

            let proposalId, typeId, fieldId =
                use document =
                    AuthoringProtocol.executeLine
                        session
                        """{"id":"type","op":"simulateCreateType","displayName":"Box","typeKind":{"kind":"product","fields":[{"displayName":"value","type":{"kind":"primitive","primitive":"int64"}}]}}"""
                    |> requireOk
                let result = document.RootElement.GetProperty("result")
                let createdType = result.GetProperty("review").GetProperty("createdType")
                let field = createdType.GetProperty("typeKind").GetProperty("fields")[0]
                result.GetProperty("proposalId").GetString(),
                createdType.GetProperty("id").GetString(),
                field.GetProperty("id").GetString()

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
            let types = declarations.RootElement.GetProperty("result").GetProperty("types")
            Assert.Equal(1, types.GetArrayLength())
            Assert.Equal<string>(typeId, types[0].GetProperty("id").GetString())
            let fields = types[0].GetProperty("typeKind").GetProperty("fields")
            Assert.Equal(1, fields.GetArrayLength())
            Assert.Equal<string>(fieldId, fields[0].GetProperty("id").GetString())
        finally
            if Directory.Exists root then Directory.Delete(root, true)
