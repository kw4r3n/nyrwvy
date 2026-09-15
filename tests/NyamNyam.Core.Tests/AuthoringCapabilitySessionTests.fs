namespace NyamNyam.Core.Tests

open System
open System.IO
open System.Text.Json
open NyamNyam.Tools.AuthoringSession
open Xunit

module AuthoringCapabilitySessionTests =
    let private tempDirectory () =
        let path = Path.Combine(Path.GetTempPath(), "nyamnyam-capability-session-" + Guid.NewGuid().ToString("N"))
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
    let ``ordinary authoring session creates persists and rediscovers a capability declaration`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        try
            use session =
                match AuthoringSession.createNew path with
                | Ok value -> value
                | Error error -> failwithf "Session creation failed: %A" error

            let effectProposalId, effectId =
                use document =
                    AuthoringProtocol.executeLine
                        session
                        """{"id":"effect","op":"simulateCreateEffect","displayName":"io"}"""
                    |> requireOk
                let result: JsonElement = document.RootElement.GetProperty("result")
                let created: JsonElement = result.GetProperty("review").GetProperty("createdEffect")
                result.GetProperty("proposalId").GetString(), created.GetProperty("id").GetString()

            use effectCommit =
                AuthoringProtocol.executeLine
                    session
                    (sprintf """{"id":"effectCommit","op":"commit","proposalId":"%s"}""" effectProposalId)
                |> requireOk
            Assert.True(effectCommit.RootElement.GetProperty("ok").GetBoolean())

            let capabilityProposalId, capabilityId, operationId, parameterId =
                let payload =
                    sprintf
                        """{"id":"capability","op":"simulateCreateCapability","displayName":"Console","operations":[{"displayName":"write","parameters":[{"displayName":"text","type":{"kind":"primitive","primitive":"string"}}],"resultType":{"kind":"primitive","primitive":"unit"},"effectId":"%s"}]}"""
                        effectId
                use document = AuthoringProtocol.executeLine session payload |> requireOk
                let result: JsonElement = document.RootElement.GetProperty("result")
                let created: JsonElement = result.GetProperty("review").GetProperty("createdCapability")
                let operation: JsonElement = created.GetProperty("operations")[0]
                let parameter: JsonElement = operation.GetProperty("parameters")[0]
                result.GetProperty("proposalId").GetString(),
                created.GetProperty("id").GetString(),
                operation.GetProperty("id").GetString(),
                parameter.GetProperty("id").GetString()

            use capabilityCommit =
                AuthoringProtocol.executeLine
                    session
                    (sprintf """{"id":"capabilityCommit","op":"commit","proposalId":"%s"}""" capabilityProposalId)
                |> requireOk
            Assert.True(capabilityCommit.RootElement.GetProperty("ok").GetBoolean())
            session.Close()

            use reopened =
                match AuthoringSession.openExisting path with
                | Ok value -> value
                | Error error -> failwithf "Session reopen failed: %A" error
            use declarations =
                AuthoringProtocol.executeLine reopened """{"id":"decls","op":"declarations"}"""
                |> requireOk
            let capabilities: JsonElement = declarations.RootElement.GetProperty("result").GetProperty("capabilities")
            Assert.Equal(1, capabilities.GetArrayLength())
            Assert.Equal<string>(capabilityId, capabilities[0].GetProperty("id").GetString())
            let operations: JsonElement = capabilities[0].GetProperty("operations")
            Assert.Equal(1, operations.GetArrayLength())
            Assert.Equal<string>(operationId, operations[0].GetProperty("id").GetString())
            Assert.Equal<string>(effectId, operations[0].GetProperty("effectId").GetString())
            let parameters: JsonElement = operations[0].GetProperty("parameters")
            Assert.Equal(1, parameters.GetArrayLength())
            Assert.Equal<string>(parameterId, parameters[0].GetProperty("id").GetString())
        finally
            if Directory.Exists root then Directory.Delete(root, true)
