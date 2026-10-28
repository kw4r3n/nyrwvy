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
    // 3 * depth + 1 nodes, nested depth regions deep.
    let private nestedBody depth =
        let mutable body = boolLiteral "false"
        for _ in 1 .. depth do
            body <- conditional (boolLiteral "false") (boolLiteral "true") body
        body
    let private create name detail body =
        $"""{{"id":"create","op":"simulateCreateFunctionByName","exported":true,"displayName":"{name}","parameters":[{{"displayName":"flag","type":{primitive "bool"}}}],"resultType":{primitive "bool"},"requiredEffects":[],"body":{body}{detail}}}"""

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
            Assert.True(compact.RootElement.GetRawText().Length < full.RootElement.GetRawText().Length)
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
    let ``compact targeted review shows literal values and referenced names of the replaced nodes`` () =
        withSession (fun session ->
            let int64Literal (value: string) = $"""{{"kind":"int64Literal","value":"{value}"}}"""
            let body =
                conditional """{"kind":"parameter","name":"flag"}""" (int64Literal "7") (int64Literal "-42")
            use created =
                send session ($"""{{"id":"create","op":"simulateCreateFunctionByName","exported":true,"displayName":"pick","parameters":[{{"displayName":"flag","type":{primitive "bool"}}}],"resultType":{primitive "int64"},"requiredEffects":[],"body":{body}}}""")
            session.Commit((result created).GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let pick = session.Declarations().Functions |> List.exactlyOne
            let nodeWhere predicate = ValueGraph.collectNodes pick.Body |> List.find (fun node -> predicate node.Kind)
            let replace (node: SemanticNode) replacement =
                send session $"""{{"id":"edit","op":"simulateReplaceTargetByName","function":"pick","targetNodeId":"{StableId.value node.Id}","replacement":{replacement}}}"""
            // Two sibling Int64 literals: the compact review names which one is replaced.
            let negative = nodeWhere (function SemanticNodeKind.Int64Literal -42L -> true | _ -> false)
            use edit = replace negative (int64Literal "0")
            let diff = (result edit).GetProperty("review").GetProperty("diff")
            let previous = diff.GetProperty("previousTarget")
            Assert.Equal("int64", previous.GetProperty("literal").GetProperty("kind").GetString())
            Assert.Equal("-42", previous.GetProperty("literal").GetProperty("value").GetString())
            let proposed = diff.GetProperty("proposedTarget")
            Assert.Equal(previous.GetProperty("nodeId").GetString(), proposed.GetProperty("nodeId").GetString())
            Assert.Equal("0", proposed.GetProperty("literal").GetProperty("value").GetString())
            Assert.Equal(JsonValueKind.Null, previous.GetProperty("referenceDisplayName").ValueKind)
            let flag = nodeWhere (function SemanticNodeKind.ParameterReference _ -> true | _ -> false)
            use condition = replace flag (boolLiteral "true")
            let previous = (result condition).GetProperty("review").GetProperty("diff").GetProperty("previousTarget")
            Assert.Equal("flag", previous.GetProperty("referenceDisplayName").GetString())
            Assert.Equal(JsonValueKind.Null, previous.GetProperty("literal").ValueKind)
            let added = (result condition).GetProperty("review").GetProperty("diff").GetProperty("proposedTarget").GetProperty("literal")
            Assert.Equal("bool", added.GetProperty("kind").GetString())
            Assert.True(added.GetProperty("value").GetBoolean()))

    [<Fact>]
    let ``every simulate kind accepts detail and an unknown detail is rejected before simulation`` () =
        withSession (fun session ->
            use effect = send session """{"id":"effect","op":"simulateCreateEffect","exported":true,"displayName":"Io","detail":"full"}"""
            Assert.Equal("full", (result effect).GetProperty("detail").GetString())
            use productType = send session ($"""{{"id":"type","op":"simulateCreateTypeByName","exported":true,"displayName":"Flag","typeKind":{{"kind":"product","fields":[{{"displayName":"on","type":{primitive "bool"}}}]}},"detail":"compact"}}""")
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

    [<Fact>]
    let ``large targeted compact review is bounded and every diff node can be paged by pending identity`` () =
        withSession (fun session ->
            use created = send session (create "large" "" (boolLiteral "true"))
            session.Commit((result created).GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            let fn = session.Declarations().Functions |> List.exactlyOne
            let line = $"""{{"id":"large-edit","op":"simulateReplaceTargetByName","function":"large","targetNodeId":"{StableId.value fn.Body.ResultNodeId}","replacement":{nestedBody 30}}}"""
            use response = send session line
            let compact = result response
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(compact.GetRawText()) <= WireProposalProjection.compactMaxBytes)
            Assert.True(compact.GetProperty("projection").GetProperty("requiresDetailedReview").GetBoolean())
            Assert.Equal("semanticallyReady", compact.GetProperty("review").GetProperty("readiness").GetProperty("kind").GetString())
            let proposalId = compact.GetProperty("proposalId").GetString()
            let pending = session.PendingProposalView proposalId |> unwrap
            let expected =
                match pending.Proposal.Review with
                | AuthoringProposalReview.ReplaceTarget review -> review.ResultingChange.AddedNodes |> List.map (fun n -> StableId.value n.NodeId)
                | _ -> failwith "Expected target review"
            let ids = ResizeArray<string>()
            let mutable offset = 0
            let mutable finished = false
            while not finished do
                use page = send session ($"""{{"id":"page","op":"readProposal","proposalId":"{proposalId}","path":"/review/diff/addedNodes","offset":{offset},"limit":20}}""")
                let value = result page
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(value.GetRawText()) <= WireProposalProjection.pageMaxBytes)
                Assert.True(value.GetProperty("baseIsCurrent").GetBoolean())
                Assert.Equal(expected.Length, value.GetProperty("totalCount").GetInt32())
                let items = value.GetProperty("value")
                Assert.Equal(items.GetArrayLength(), value.GetProperty("returnedCount").GetInt32())
                for item in items.EnumerateArray() do ids.Add(item.GetProperty("nodeId").GetString())
                let next = value.GetProperty("nextOffset")
                if next.ValueKind = JsonValueKind.Null then finished <- true
                else
                    let nextOffset = next.GetInt32()
                    Assert.True(nextOffset > offset)
                    offset <- nextOffset
            Assert.Equal<string list>(expected, List.ofSeq ids)
            // The bounded view does not change the receipt or proposal authority.
            use receiptPage = send session ($"""{{"id":"receipt","op":"readProposal","proposalId":"{proposalId}","path":"/review/receipt"}}""")
            Assert.Equal(compact.GetProperty("review").GetProperty("receipt").GetRawText(), (result receiptPage).GetProperty("value").GetRawText())
            session.Commit(proposalId, CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            use gone = AuthoringProtocol.executeLine session ($"""{{"id":"gone","op":"readProposal","proposalId":"{proposalId}"}}""") |> JsonDocument.Parse
            Assert.Equal("ProposalNotFound", gone.RootElement.GetProperty("error").GetProperty("code").GetString()))

    [<Fact>]
    let ``pending review reads report stale base and do not refresh a stale commit`` () =
        withSession (fun session ->
            use pendingResponse = send session (create "pending" "" (boolLiteral "true"))
            let proposalId = (result pendingResponse).GetProperty("proposalId").GetString()
            use advance = send session (create "advance" "" (boolLiteral "false"))
            session.Commit((result advance).GetProperty("proposalId").GetString(), CommitPolicy.RejectBlockingObligations) |> unwrap |> ignore
            use page = send session ($"""{{"id":"stale-read","op":"readProposal","proposalId":"{proposalId}"}}""")
            Assert.False((result page).GetProperty("baseIsCurrent").GetBoolean())
            match session.Commit(proposalId, CommitPolicy.RejectBlockingObligations) with
            | Error(AuthoringSessionError.CommitRejected diagnostics) -> Assert.Contains(diagnostics, fun d -> d.Code = "StaleBaseSnapshot")
            | other -> failwithf "Stale proposal unexpectedly accepted: %A" other)

    [<Fact>]
    let ``proposal pages reject malformed paths limits and offsets as protocol errors`` () =
        withSession (fun session ->
            use proposed = send session (create "page-input" "" (boolLiteral "true"))
            let proposalId = (result proposed).GetProperty("proposalId").GetString()
            for fields in [ "\"path\":\"bad\""; "\"path\":\"/review/~3\""; "\"path\":\"/review/missing\"";
                            "\"path\":\"/review/createdFunction/body/nodes/00\""; "\"path\":\"/review/createdFunction/body/nodes/+1\"";
                            "\"path\":\"/review/createdFunction/body/nodes/-1\"";
                            "\"offset\":-1"; "\"limit\":0"; "\"limit\":21"; "\"offset\":\"0\"" ] do
                use response = AuthoringProtocol.executeLine session ($"""{{"id":"bad-page","op":"readProposal","proposalId":"{proposalId}",{fields}}}""") |> JsonDocument.Parse
                Assert.False(response.RootElement.GetProperty("ok").GetBoolean())
                Assert.Equal("ProtocolError", response.RootElement.GetProperty("error").GetProperty("code").GetString()))

    [<Fact>]
    let ``projection bounds huge unicode metadata without inventing identity prefixes`` () =
        let huge = String.replicate 5000 "測定😀"
        let node id = WireJson.objectValue [ "nodeId", box id; "referenceDisplayName", box huge ]
        let raw = WireJson.objectValue [ "proposalId", box "fixed-proposal"; "detail", box "compact";
                                        "review", WireJson.objectValue [ "kind", box "operations"; "receipt", WireJson.objectValue [ "baseSnapshotId", box huge ];
                                                                         "readiness", WireJson.objectValue [ "kind", box "incomplete"; "obligationIds", [ 0..1000 ] |> List.map (fun i -> box ("obligation-" + string i)) |> WireJson.arrayValue ];
                                                                         "diff", [ 0..64 ] |> List.map (fun i -> node ("node-" + string i)) |> WireJson.arrayValue ] ]
        let text = WireProposalProjection.compact raw |> WireJson.serialize
        Assert.True(System.Text.Encoding.UTF8.GetByteCount text <= WireProposalProjection.compactMaxBytes)
        use doc = JsonDocument.Parse text
        let review = doc.RootElement.GetProperty("review")
        Assert.Equal("incomplete", review.GetProperty("readiness").GetProperty("kind").GetString())
        Assert.Equal(JsonValueKind.Null, review.GetProperty("receipt").ValueKind)
        let projection = doc.RootElement.GetProperty("projection")
        Assert.True(projection.GetProperty("requiresDetailedReview").GetBoolean())
        Assert.True(projection.GetProperty("omissionCount").GetInt32() > 0)
        for item in review.GetProperty("diff").EnumerateArray() do
            Assert.StartsWith("node-", item.GetProperty("nodeId").GetString())

    [<Fact>]
    let ``value dependency roles and wrapped failures are fixed strings`` () =
        let fields role = WireEncodeCommon.dependencyRole role |> List.map (fun (name, value) -> $"{name}={value}") |> String.concat ","
        Assert.Equal("role=Value", fields ValueDependencyRole.Value)
        Assert.Equal("role=ListItem,index=2", fields (ValueDependencyRole.ListItem 2))
        Assert.Equal("role=CallArgument,parameterId=p", fields (ValueDependencyRole.CallArgument(StableId.createForTesting<ParameterTag> "p")))
        Assert.Equal("role=FieldValue,fieldId=f", fields (ValueDependencyRole.FieldValue(StableId.createForTesting<FieldTag> "f")))
        let error = AuthoringSessionError.BackendFailure(CSharpBackendError.MissingCapabilityBinding(StableId.createForTesting<CapabilityOperationTag> "op"))
        Assert.Equal("BackendFailure: MissingCapabilityBinding", WireEncode.startupMessage error)

    [<Fact>]
    let ``large full node text pages preserve UTF16 and cannot bypass the page byte bound`` () =
        withSession (fun session ->
            let literal = String.replicate 3000 "測定😀"
            let line = JsonSerializer.Serialize {| id = "text"; op = "simulateCreateFunctionByName"; displayName = "text";
                                                  parameters = Array.empty<obj>; resultType = {| kind = "primitive"; primitive = "string" |};
                                                  requiredEffects = Array.empty<obj>; body = {| kind = "stringLiteral"; value = literal |} |}
            use response = send session line
            let proposalId = (result response).GetProperty("proposalId").GetString()
            let pending = session.PendingProposalView proposalId |> unwrap
            use full = WireEncode.proposal pending.Proposal |> WireJson.serialize |> JsonDocument.Parse
            // The debug encoding names the kind only; the literal pages through the structured one.
            Assert.Equal("StringLiteral", full.RootElement.GetProperty("review").GetProperty("createdFunction").GetProperty("body").GetProperty("nodes").[0].GetProperty("kind").GetString())
            let expected = literal
            let collected = Text.StringBuilder()
            let mutable offset = 0
            let mutable finished = false
            while not finished do
                use page = send session ($"""{{"id":"text-page","op":"readProposal","proposalId":"{proposalId}","path":"/review/createdFunction/body/nodes/0/kind/value","offset":{offset},"limit":1024}}""")
                let value = result page
                Assert.True(Text.Encoding.UTF8.GetByteCount(value.GetRawText()) <= WireProposalProjection.pageMaxBytes)
                let part = value.GetProperty("value").GetString()
                Assert.Equal(part.Length, value.GetProperty("returnedCount").GetInt32())
                Assert.True(value.GetProperty("projection").GetProperty("complete").GetBoolean())
                collected.Append part |> ignore
                let next = value.GetProperty("nextOffset")
                if next.ValueKind = JsonValueKind.Null then finished <- true else offset <- next.GetInt32()
            Assert.Equal(expected, collected.ToString())
            // The default object/node page is bounded too; its oversized literal is explicit.
            use nodePage = send session ($"""{{"id":"node-page","op":"readProposal","proposalId":"{proposalId}","path":"/review/createdFunction/body/nodes","limit":1}}""")
            Assert.True(Text.Encoding.UTF8.GetByteCount((result nodePage).GetRawText()) <= WireProposalProjection.pageMaxBytes)
            Assert.True((result nodePage).GetProperty("projection").GetProperty("requiresDetailedReview").GetBoolean())
            let expectedNodeId = full.RootElement.GetProperty("review").GetProperty("createdFunction").GetProperty("body").GetProperty("nodes").[0].GetProperty("id").GetString()
            Assert.Equal(expectedNodeId, (result nodePage).GetProperty("value").[0].GetProperty("id").GetString())
            session.Reject proposalId |> unwrap |> ignore
            use gone = AuthoringProtocol.executeLine session ($"""{{"id":"rejected-page","op":"readProposal","proposalId":"{proposalId}"}}""") |> JsonDocument.Parse
            Assert.Equal("ProposalNotFound", gone.RootElement.GetProperty("error").GetProperty("code").GetString()))

    [<Fact>]
    let ``stream byte counts exactly match escaped and lossless wire text`` () =
        for text in [ "plain"; "測定😀"; "\"\n\t\\"; "\uD800"; "\uDC00"; "a\uD800b😀\uDC00" ] do
            let value = WireJson.objectValue [ "value", WireJson.runString text; "items", WireJson.arrayValue [ box true; box 42L; null ] ]
            Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(WireJson.serialize value), WireJson.byteCount value)

    [<Fact>]
    let ``paging escaped pointers does not encode unselected payloads or mutate the review`` () =
        withSession (fun session ->
            use proposed = send session (create "projection" "" (boolLiteral "true"))
            let pending = session.PendingProposalView((result proposed).GetProperty("proposalId").GetString()) |> unwrap
            let source = WireJson.objectValue [ "a/b~c", WireJson.arrayValue [ WireJson.runString "測定😀" ]; "large", box (String('x', 1024 * 1024)) ]
            let original = WireJson.serialize source
            let page () = WireProposalProjection.page pending true source "/a~1b~0c" 0 1
            page () |> WireJson.serialize |> ignore
            let before = GC.GetAllocatedBytesForCurrentThread()
            let projected = page ()
            let allocated = GC.GetAllocatedBytesForCurrentThread() - before
            // Regression: the previous full-review serialize/parse allocated >9 MB here.
            Assert.True(allocated < 1000000L, $"Unselected payload allocated {allocated} bytes")
            use document = JsonDocument.Parse(WireJson.serialize projected)
            let values = document.RootElement.GetProperty("value")
            Assert.Equal("測定😀", values.[0].GetString())
            Assert.True(document.RootElement.GetProperty("projection").GetProperty("complete").GetBoolean())
            Assert.Equal(original, WireJson.serialize source)
            WireProposalProjection.compact (WireJson.objectValue [ "proposalId", box "id"; "detail", box "compact"; "review", source ]) |> ignore
            Assert.Equal(original, WireJson.serialize source))

    [<Fact>]
    let ``saved engine review retains every nested node in complete structured form`` () =
        withSession (fun session ->
            use proposed = send session (create "durable" "" (nestedBody 12))
            let proposalId = (result proposed).GetProperty("proposalId").GetString()
            use response = send session $"""{{"id":"view","op":"viewProposal","proposalId":"{proposalId}"}}"""
            let html = File.ReadAllText((result response).GetProperty("path").GetString())
            let section = html.Substring(html.IndexOf("<section><h2>Engine review</h2>", StringComparison.Ordinal))
            let start = section.IndexOf("<pre><code>", StringComparison.Ordinal) + "<pre><code>".Length
            let finish = section.IndexOf("</code></pre>", start, StringComparison.Ordinal)
            use document = JsonDocument.Parse(System.Net.WebUtility.HtmlDecode(section.Substring(start, finish-start)))
            let root = document.RootElement
            Assert.Equal("full", root.GetProperty("detail").GetString())
            Assert.False(root.TryGetProperty("projection") |> fst)
            let rec countNodes (value: JsonElement) =
                match value.ValueKind with
                | JsonValueKind.Object -> value.EnumerateObject() |> Seq.sumBy (fun p -> (if p.Name = "nodes" then p.Value.GetArrayLength() else 0) + countNodes p.Value)
                | JsonValueKind.Array -> value.EnumerateArray() |> Seq.sumBy countNodes
                | _ -> 0
            Assert.Equal(37, countNodes (root.GetProperty("review").GetProperty("createdFunction").GetProperty("body")))
            Assert.Contains("\"kind\": \"conditional\"", root.GetRawText()))

    [<Fact>]
    let ``projection of a review with very wide objects still returns the proposal id`` () =
        let wide = [ for i in 0..4000 -> $"field-{i}", box (WireJson.objectValue [ "kind", box "x" ]) ] |> WireJson.objectValue
        let raw = WireJson.objectValue [ "proposalId", box "wide-proposal"; "detail", box "compact";
                                        "review", WireJson.objectValue [ "kind", box "createFunction"; "createdFunction", wide ] ]
        let text = WireProposalProjection.compact raw |> WireJson.serialize
        Assert.True(System.Text.Encoding.UTF8.GetByteCount text <= WireProposalProjection.compactMaxBytes)
        use doc = JsonDocument.Parse text
        Assert.Equal("wide-proposal", doc.RootElement.GetProperty("proposalId").GetString())
        Assert.Equal("createFunction", doc.RootElement.GetProperty("review").GetProperty("kind").GetString())
        Assert.True(doc.RootElement.GetProperty("projection").GetProperty("requiresDetailedReview").GetBoolean())
