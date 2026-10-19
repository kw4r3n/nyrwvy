namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Tools.AuthoringSession
open Xunit

module AuthoringTypeSessionTests =
    let private tempDirectory () =
        let path = Path.Combine(Path.GetTempPath(), "nyrwvy-type-session-" + Guid.NewGuid().ToString("N"))
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
    let ``an unexpected failure replies InternalError and leaves the session unchanged`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        try
            use session =
                match AuthoringSession.createNew path with
                | Ok value -> value
                | Error error -> failwithf "Session creation failed: %A" error
            let proposalId =
                use document =
                    AuthoringProtocol.executeLine
                        session
                        """{"id":"type","op":"simulateCreateType","exported":true,"displayName":"Box","typeKind":{"kind":"product","fields":[{"displayName":"value","type":{"kind":"primitive","primitive":"int64"}}]}}"""
                    |> requireOk
                document.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
            let persisted = File.ReadAllText path
            let target = Path.GetFullPath path
            let commit = sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposalId
            SessionStorageFaults.failReplacement <- fun candidate ->
                if candidate = target then raise (NotSupportedException "simulated fault") else false
            let response =
                try AuthoringProtocol.executeLine session commit
                finally SessionStorageFaults.failReplacement <- fun _ -> false
            use failed = JsonDocument.Parse response
            Assert.Equal("commit", failed.RootElement.GetProperty("id").GetString())
            Assert.False(failed.RootElement.GetProperty("ok").GetBoolean())
            Assert.Equal("InternalError", failed.RootElement.GetProperty("error").GetProperty("code").GetString())
            // The wire carries a stable message, never the exception's type or text.
            Assert.Equal(AuthoringProtocol.internalErrorMessage, failed.RootElement.GetProperty("error").GetProperty("message").GetString())
            Assert.DoesNotContain("simulated fault", response)
            Assert.DoesNotContain("NotSupportedException", response)
            Assert.Equal(persisted, File.ReadAllText path)
            use declarations = AuthoringProtocol.executeLine session """{"id":"decls","op":"declarations"}""" |> requireOk
            Assert.Equal(0, declarations.RootElement.GetProperty("result").GetProperty("types").GetArrayLength())
            // The pending proposal survived and still commits.
            use committed = AuthoringProtocol.executeLine session commit |> requireOk
            use after = AuthoringProtocol.executeLine session """{"id":"decls","op":"declarations"}""" |> requireOk
            Assert.Equal(1, after.RootElement.GetProperty("result").GetProperty("types").GetArrayLength())
        finally
            Directory.Delete(root, true)

    [<Fact>]
    let ``ordinary authoring session creates persists and rediscovers a product declaration`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        try
            use session =
                match AuthoringSession.createNew path with
                | Ok value -> value
                | Error error -> failwithf "Session creation failed: %A" error

            let ids: string * string * string =
                use document =
                    AuthoringProtocol.executeLine
                        session
                        """{"id":"type","op":"simulateCreateType","exported":true,"displayName":"Box","typeKind":{"kind":"product","fields":[{"displayName":"value","type":{"kind":"primitive","primitive":"int64"}}]}}"""
                    |> requireOk
                let result: JsonElement = document.RootElement.GetProperty("result")
                let createdType: JsonElement = result.GetProperty("review").GetProperty("createdType")
                let field: JsonElement = createdType.GetProperty("typeKind").GetProperty("fields")[0]
                result.GetProperty("proposalId").GetString(),
                createdType.GetProperty("id").GetString(),
                field.GetProperty("id").GetString()
            let proposalId, typeId, fieldId = ids

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
            let types: JsonElement = declarations.RootElement.GetProperty("result").GetProperty("types")
            Assert.Equal(1, types.GetArrayLength())
            Assert.Equal<string>(typeId, types[0].GetProperty("id").GetString())
            let fields: JsonElement = types[0].GetProperty("typeKind").GetProperty("fields")
            Assert.Equal(1, fields.GetArrayLength())
            Assert.Equal<string>(fieldId, fields[0].GetProperty("id").GetString())
        finally
            if Directory.Exists root then Directory.Delete(root, true)


    [<Fact>]
    let ``ordinary authoring session evolves a fieldless Union and repairs exhaustive matches atomically`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        try
            use session =
                match AuthoringSession.createNew path with
                | Ok value -> value
                | Error error -> failwithf "Session creation failed: %A" error

            let typeProposalId, typeId, failedVariantId, succeededVariantId =
                use document =
                    AuthoringProtocol.executeLine
                        session
                        """{"id":"type","op":"simulateCreateType","exported":true,"displayName":"ExecutionState","typeKind":{"kind":"union","variants":[{"displayName":"Failed","fields":[]},{"displayName":"Succeeded","fields":[]}]}}"""
                    |> requireOk
                let result = document.RootElement.GetProperty("result")
                let createdType = result.GetProperty("review").GetProperty("createdType")
                let variants = createdType.GetProperty("typeKind").GetProperty("variants")
                result.GetProperty("proposalId").GetString(),
                createdType.GetProperty("id").GetString(),
                variants[0].GetProperty("id").GetString(),
                variants[1].GetProperty("id").GetString()

            AuthoringProtocol.executeLine
                session
                (sprintf """{"id":"type-commit","op":"commit","proposalId":"%s"}""" typeProposalId)
            |> requireOk
            |> fun document -> document.Dispose()

            let functionRequest =
                sprintf
                    """{"id":"function","op":"simulateCreateFunction","exported":true,"displayName":"next_action","parameters":[{"displayName":"execution","type":{"kind":"named","typeId":"%s"}}],"resultType":{"kind":"primitive","primitive":"bool"},"requiredEffects":[],"body":{"kind":"matchUnion","input":{"kind":"parameterSlot","index":0},"arms":[{"variantId":"%s","body":{"kind":"boolLiteral","value":false}},{"variantId":"%s","body":{"kind":"boolLiteral","value":true}}]}}"""
                    typeId
                    failedVariantId
                    succeededVariantId

            let functionProposalId, functionId =
                use document =
                    AuthoringProtocol.executeLine session functionRequest
                    |> requireOk
                let result = document.RootElement.GetProperty("result")
                let createdFunction = result.GetProperty("review").GetProperty("createdFunction")
                result.GetProperty("proposalId").GetString(),
                createdFunction.GetProperty("id").GetString()

            AuthoringProtocol.executeLine
                session
                (sprintf """{"id":"function-commit","op":"commit","proposalId":"%s"}""" functionProposalId)
            |> requireOk
            |> fun document -> document.Dispose()

            let evolutionProposalId, addedVariantId =
                use document =
                    AuthoringProtocol.executeLine
                        session
                        (sprintf
                            """{"id":"evolve","op":"simulateAddUnionVariantLike","typeId":"%s","sourceVariantId":"%s","displayName":"TimedOut"}"""
                            typeId
                            failedVariantId)
                    |> requireOk
                let result = document.RootElement.GetProperty("result")
                let review = result.GetProperty("review")
                Assert.Equal("unionVariantEvolution", review.GetProperty("kind").GetString())
                Assert.Equal(1, review.GetProperty("repairedMatchCount").GetInt32())
                let repairedFunctionIds = review.GetProperty("repairedFunctionIds")
                Assert.Equal(1, repairedFunctionIds.GetArrayLength())
                Assert.Equal<string>(functionId, repairedFunctionIds[0].GetString())
                Assert.False(review.TryGetProperty("rewrittenFunctions") |> fst)
                // A full review adds the repaired function's proposed body.
                use full =
                    AuthoringProtocol.executeLine
                        session
                        (sprintf
                            """{"id":"evolve-full","op":"simulateAddUnionVariantLike","typeId":"%s","sourceVariantId":"%s","displayName":"TimedOut","detail":"full"}"""
                            typeId
                            failedVariantId)
                    |> requireOk
                let rewritten = full.RootElement.GetProperty("result").GetProperty("review").GetProperty("rewrittenFunctions")
                Assert.Equal(1, rewritten.GetArrayLength())
                Assert.Equal<string>(functionId, rewritten[0].GetProperty("id").GetString())
                Assert.True(rewritten[0].GetProperty("body").GetProperty("nodes").GetArrayLength() > 0)
                result.GetProperty("proposalId").GetString(),
                review.GetProperty("addedVariantId").GetString()

            AuthoringProtocol.executeLine
                session
                (sprintf """{"id":"evolve-commit","op":"commit","proposalId":"%s"}""" evolutionProposalId)
            |> requireOk
            |> fun document -> document.Dispose()

            session.Close()

            use reopened =
                match AuthoringSession.openExisting path with
                | Ok value -> value
                | Error error -> failwithf "Session reopen failed: %A" error

            use declarations =
                AuthoringProtocol.executeLine reopened """{"id":"decls","op":"declarations"}"""
                |> requireOk
            let result = declarations.RootElement.GetProperty("result")
            let types = result.GetProperty("types").EnumerateArray() |> Seq.toList
            let functions = result.GetProperty("functions").EnumerateArray() |> Seq.toList
            let executionState =
                types
                |> List.find (fun item -> item.GetProperty("id").GetString() = typeId)
            let variants = executionState.GetProperty("typeKind").GetProperty("variants")
            Assert.Equal(3, variants.GetArrayLength())
            let timedOut =
                variants.EnumerateArray()
                |> Seq.find (fun item -> item.GetProperty("displayName").GetString() = "TimedOut")
            Assert.Equal<string>(addedVariantId, timedOut.GetProperty("id").GetString())
            Assert.Empty(timedOut.GetProperty("fields").EnumerateArray())
            Assert.Contains(
                functions,
                fun item -> item.GetProperty("id").GetString() = functionId
            )

            use readiness =
                AuthoringProtocol.executeLine reopened """{"id":"ready","op":"executionReadiness"}"""
                |> requireOk
            Assert.Equal("ready", readiness.RootElement.GetProperty("result").GetProperty("kind").GetString())
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact>]
    let ``every input line gets exactly one reply, a blank line a ProtocolError`` () =
        let root = tempDirectory ()
        let path = Path.Combine(root, "workspace.json")
        try
            use session =
                match AuthoringSession.createNew path with
                | Ok value -> value
                | Error error -> failwithf "Session creation failed: %A" error
            let input = String.Join("\n", [ "{\"id\":\"a\",\"op\":\"status\"}"; ""; "   \t"; "{\"id\":\"b\",\"op\":\"status\"}" ]) + "\n"
            use output = new StringWriter()
            AuthoringProtocol.serve session (new StringReader(input)) output
            let replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            Assert.Equal(4, replies.Length)
            for index, expectedId in [ 0, "a"; 3, "b" ] do
                use reply = requireOk replies[index]
                Assert.Equal(expectedId, reply.RootElement.GetProperty("id").GetString())
            for index in [ 1; 2 ] do
                use reply = JsonDocument.Parse replies[index]
                Assert.False(reply.RootElement.GetProperty("ok").GetBoolean())
                Assert.Equal(JsonValueKind.Null, reply.RootElement.GetProperty("id").ValueKind)
                Assert.Equal("ProtocolError", reply.RootElement.GetProperty("error").GetProperty("code").GetString())
        finally
            Directory.Delete(root, true)
