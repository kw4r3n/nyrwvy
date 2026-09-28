namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module CompactSimulateReplyTests =
    let private unwrap = function Ok value -> value | Error error -> failwithf "%A" error
    let private send (session: AuthoringSession) line =
        let response = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
        response
    let private result (response: JsonDocument) = response.RootElement.GetProperty("result")
    let private names (element: JsonElement) = element.EnumerateObject() |> Seq.map (fun item -> item.Name) |> Set.ofSeq
    let private primitive name = $"""{{"kind":"primitive","primitive":"{name}"}}"""
    let private boolLiteral value = $"""{{"kind":"boolLiteral","value":{value}}}"""
    let private conditional condition whenTrue whenFalse =
        $"""{{"kind":"conditional","condition":{condition},"whenTrue":{whenTrue},"whenFalse":{whenFalse}}}"""
    // 3 * depth + 1 nodes, nested depth regions deep: the old debug-string echo repeated
    // every nested region inside each enclosing node.
    let private nestedBody depth =
        let mutable body = boolLiteral "false"
        for _ in 1 .. depth do
            body <- conditional (boolLiteral "false") (boolLiteral "true") body
        body
    let private create name detail body =
        $"""{{"id":"create","op":"simulateCreateFunctionByName","displayName":"{name}","parameters":[{{"displayName":"flag","type":{primitive "bool"}}}],"resultType":{primitive "bool"},"requiredEffects":[],"body":{body}{detail}}}"""

    let private withSession run =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-compact-review-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(root, "workspace.json")) |> unwrap
            run session
            session.Close()
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact>]
    let ``simulate replies are compact by default and a compact proposal commits exactly what was reviewed`` () =
        withSession (fun session ->
            use compact = send session (create "nested" "" (nestedBody 12))
            let reply = result compact
            Assert.Equal("compact", reply.GetProperty("detail").GetString())
            let review = reply.GetProperty("review")
            Assert.Equal("createFunction", review.GetProperty("kind").GetString())
            Assert.Equal<Set<string>>(set [ "kind"; "receipt"; "readiness"; "createdFunction"; "bodyEffectRequirements"; "aborts" ], names review)
            let created = review.GetProperty("createdFunction")
            Assert.Equal("nested", created.GetProperty("displayName").GetString())
            let parameter = created.GetProperty("parameters").[0]
            Assert.Equal("flag", parameter.GetProperty("displayName").GetString())
            Assert.False(String.IsNullOrEmpty(parameter.GetProperty("id").GetString()))
            let body = created.GetProperty("body")
            Assert.Equal<Set<string>>(set [ "resultNodeId"; "nodeCount" ], names body)
            Assert.Equal(37, body.GetProperty("nodeCount").GetInt32())
            let receipt = review.GetProperty("receipt")
            Assert.StartsWith("sha256:", receipt.GetProperty("proposedSnapshotFingerprint").GetString())
            Assert.Equal("semanticallyReady", review.GetProperty("readiness").GetProperty("kind").GetString())

            // Full detail is the previous echo: the same review with the per-node graph.
            use full = send session (create "nested" ",\"detail\":\"full\"" (nestedBody 12))
            Assert.Equal("full", (result full).GetProperty("detail").GetString())
            let fullBody = (result full).GetProperty("review").GetProperty("createdFunction").GetProperty("body")
            Assert.Equal(37, fullBody.GetProperty("nodes").GetArrayLength())
            use structured = send session (create "nested" ",\"detail\":\"full\",\"structuredReview\":true" (nestedBody 12))
            let structuredBody = (result structured).GetProperty("review").GetProperty("createdFunction").GetProperty("body")
            Assert.Contains("\"kind\":\"conditional\"", structuredBody.GetProperty("nodes").GetRawText())
            Assert.True(compact.RootElement.GetRawText().Length * 10 < full.RootElement.GetRawText().Length)
            // structuredReview alone does not bring the graph back.
            use structuredCompact = send session (create "nested" ",\"structuredReview\":true" (nestedBody 12))
            Assert.Equal("compact", (result structuredCompact).GetProperty("detail").GetString())
            Assert.False((result structuredCompact).GetProperty("review").GetProperty("createdFunction").GetProperty("body").TryGetProperty("nodes") |> fst)

            let proposalId = reply.GetProperty("proposalId").GetString()
            session.Commit(proposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let committed = session.Declarations().Functions |> List.exactlyOne
            Assert.Equal(created.GetProperty("id").GetString(), StableId.value committed.Id)
            Assert.Equal(body.GetProperty("resultNodeId").GetString(), StableId.value committed.Body.ResultNodeId)
            Assert.Equal(37, ValueGraph.collectNodes committed.Body |> List.length)
            let status = session.Status() |> unwrap
            Assert.Equal(receipt.GetProperty("proposedSemanticFingerprint").GetString(), SemanticFingerprint.value status.SemanticFingerprint))

    [<Fact>]
    let ``compact targeted review keeps callers diff and obligations and drops node types`` () =
        withSession (fun session ->
            use leaf = send session (create "leaf" "" (boolLiteral "true"))
            session.Commit((result leaf).GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let call = """{"kind":"directCall","function":"leaf","arguments":[{"parameter":"flag","value":{"kind":"parameter","name":"flag"}}]}"""
            use caller = send session (create "caller" "" call)
            session.Commit((result caller).GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let leafFunction = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = "leaf")
            let target = StableId.value leafFunction.Body.ResultNodeId
            let replace detail =
                $"""{{"id":"edit","op":"simulateReplaceTargetByName","function":"leaf","targetNodeId":"{target}","replacement":{boolLiteral "false"}{detail}}}"""
            use compact = send session (replace "")
            let review = (result compact).GetProperty("review")
            Assert.Equal(target, review.GetProperty("diff").GetProperty("targetNodeId").GetString())
            for focusName in [ "baseFocus"; "proposedFocus" ] do
                let focus = review.GetProperty(focusName)
                Assert.Equal<Set<string>>(
                    set [ "snapshotId"; "function"; "bodyEffectRequirements"; "aborts"; "callers"; "callees"; "openObligations" ],
                    names focus)
                Assert.Equal(1, focus.GetProperty("callers").GetArrayLength())
                Assert.Equal("leaf", focus.GetProperty("function").GetProperty("displayName").GetString())
                Assert.Equal(1, focus.GetProperty("function").GetProperty("body").GetProperty("nodeCount").GetInt32())
            use full = send session (replace ",\"detail\":\"full\"")
            let fullFocus = (result full).GetProperty("review").GetProperty("proposedFocus")
            Assert.Equal(1, fullFocus.GetProperty("nodeTypes").GetArrayLength())
            Assert.Equal(1, fullFocus.GetProperty("function").GetProperty("body").GetProperty("nodes").GetArrayLength())
            session.Commit((result compact).GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore)

    [<Fact>]
    let ``every simulate kind accepts detail and an unknown detail is rejected before simulation`` () =
        withSession (fun session ->
            use effect = send session """{"id":"effect","op":"simulateCreateEffect","displayName":"Io","detail":"full"}"""
            Assert.Equal("full", (result effect).GetProperty("detail").GetString())
            use productType = send session ($"""{{"id":"type","op":"simulateCreateTypeByName","displayName":"Flag","typeKind":{{"kind":"product","fields":[{{"displayName":"on","type":{primitive "bool"}}}]}},"detail":"compact"}}""")
            Assert.Equal("compact", (result productType).GetProperty("detail").GetString())
            Assert.Equal("Flag", (result productType).GetProperty("review").GetProperty("createdType").GetProperty("displayName").GetString())
            let before = session.Status() |> unwrap
            for detail in [ "\"summary\""; "true" ] do
                use rejected = AuthoringProtocol.executeLine session (create "bad" $",\"detail\":{detail}" (boolLiteral "true")) |> JsonDocument.Parse
                Assert.False(rejected.RootElement.GetProperty("ok").GetBoolean())
                Assert.Contains("detail", rejected.RootElement.GetProperty("error").GetProperty("message").GetString())
            use status = AuthoringProtocol.executeLine session """{"id":"status","op":"status","detail":"full"}""" |> JsonDocument.Parse
            Assert.False(status.RootElement.GetProperty("ok").GetBoolean())
            Assert.Equal(before, session.Status() |> unwrap))
