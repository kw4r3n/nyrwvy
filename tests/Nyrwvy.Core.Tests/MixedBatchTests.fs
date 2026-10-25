namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// docs/decisions/mixed-authoring-batches.md.
module MixedBatchTests =
    let private int64 = """{"kind":"primitive","primitive":"int64"}"""
    let private boolean = """{"kind":"primitive","primitive":"bool"}"""
    let private n = """{"kind":"parameter","name":"n"}"""
    let private lit (value: int) = $"""{{"kind":"int64Literal","value":"{value}"}}"""
    let private add left right = $"""{{"kind":"int64WrappingAdd","left":{left},"right":{right}}}"""
    let private call name argument = $"""{{"kind":"directCall","function":"{name}","arguments":[{{"parameter":"n","value":{argument}}}]}}"""
    let private fn name body =
        $"""{{"displayName":"{name}","exported":true,"parameters":[{{"displayName":"n","type":{int64}}}],"resultType":{int64},"requiredEffects":[],"body":{body}}}"""

    let private withSession (test: AuthoringSession -> (string -> JsonElement) -> (string -> string) -> unit) =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-mixed-batch-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(dir, "graph.json")) |> function Ok value -> value | Error error -> failwithf "%A" error
            let execute (line: string) =
                use document = JsonDocument.Parse(AuthoringProtocol.executeLine session line)
                Assert.True(document.RootElement.GetProperty("ok").GetBoolean(), document.RootElement.ToString())
                document.RootElement.GetProperty("result").Clone()
            let rejected (line: string) =
                use document = JsonDocument.Parse(AuthoringProtocol.executeLine session line)
                Assert.False(document.RootElement.GetProperty("ok").GetBoolean(), document.RootElement.ToString())
                document.RootElement.GetProperty("error").Clone().GetRawText()
            test session execute rejected
        finally
            Directory.Delete(dir, true)

    let private proposalId (proposal: JsonElement) = proposal.GetProperty("proposalId").GetString()

    let private commit (execute: string -> JsonElement) (proposal: JsonElement) =
        execute $"""{{"id":"commit","op":"commit","proposalId":"{proposalId proposal}"}}""" |> ignore

    let private runs (execute: string -> JsonElement) name (argument: int) (expected: int) =
        let run = execute $"""{{"id":"run","op":"runPureByName","function":"{name}","arguments":[{{"kind":"int64","value":{argument}}}],"expected":{{"kind":"int64","value":{expected}}}}}"""
        Assert.True(run.GetProperty("matches").GetBoolean(), run.ToString())

    let private batch (steps: string list) = $"""{{"id":"batch","op":"simulateBatchByName","steps":[{String.concat "," steps}]}}"""

    let private createFunction name body = $"""{{"kind":"createFunction",{(fn name body).TrimStart('{')}"""

    let private createFlag = $"""{{"kind":"createType","displayName":"Flag","exported":true,"typeKind":{{"kind":"product","fields":[{{"displayName":"on","type":{boolean}}}]}}}}"""

    let private seed (execute: string -> JsonElement) =
        commit execute (execute $"""{{"id":"seed","op":"simulateCreateFunctionsByName","functions":[{fn "inc" (add n (lit 1))},{fn "caller" (call "inc" n)}]}}""")

    [<Fact>]
    let ``a type, a function using it and a body replacement commit in one batch`` () =
        withSession (fun session execute _ ->
            seed execute
            let useFlag =
                """{"kind":"createFunction","displayName":"isOn","exported":true,"parameters":[{"displayName":"flag","type":{"kind":"named","name":"Flag"}}],"resultType":{"kind":"primitive","primitive":"bool"},"requiredEffects":[],"body":{"kind":"fieldAccess","source":{"kind":"parameter","name":"flag"},"type":"Flag","field":"on"}}"""
            let replace = $"""{{"kind":"replaceBody","function":"inc","replacement":{add n (lit 2)}}}"""
            let before = session.SnapshotId
            let proposal = execute (batch [ createFlag; useFlag; replace ])
            Assert.Equal("createType", proposal.GetProperty("review").GetProperty("kind").GetString())
            let steps = proposal.GetProperty("review").GetProperty("steps").EnumerateArray() |> Seq.toList
            Assert.Equal<string list>([ "createFunction"; "replaceBody" ], steps |> List.map (fun step -> step.GetProperty("kind").GetString()))
            Assert.Equal("semanticallyReady", proposal.GetProperty("review").GetProperty("readiness").GetProperty("kind").GetString())
            Assert.Equal(before, session.SnapshotId)
            commit execute proposal
            let declarations = session.Declarations()
            Assert.Contains(declarations.Types, fun item -> item.DisplayName = "Flag")
            Assert.Contains(declarations.Functions, fun item -> item.DisplayName = "isOn")
            runs execute "inc" 5 7
            runs execute "caller" 5 7
            Assert.Empty((execute """{"id":"p","op":"proposals"}""").GetProperty("proposals").EnumerateArray()))

    [<Fact>]
    let ``a later step resolves a declaration an earlier step created`` () =
        withSession (fun session execute _ ->
            let twice = createFunction "twice" (call "once" (call "once" n))
            let proposal = execute (batch [ createFunction "once" (add n (lit 1)); twice; $"""{{"kind":"replaceBody","function":"twice","replacement":{call "once" (call "once" (call "once" n))}}}""" ])
            commit execute proposal
            Assert.Equal(2, session.Declarations().Functions.Length)
            runs execute "twice" 1 4)

    [<Fact>]
    let ``an unknown name in the third step rejects the batch with its index and kind`` () =
        withSession (fun session execute rejected ->
            let before = session.SnapshotId
            let error = rejected (batch [ createFlag; createFunction "once" (add n (lit 1)); """{"kind":"replaceBody","function":"missing","replacement":{"kind":"parameter","name":"n"}}""" ])
            use document = JsonDocument.Parse error
            let root = document.RootElement
            Assert.Equal("BatchStepRejected", root.GetProperty("code").GetString())
            Assert.Equal(2, root.GetProperty("stepIndex").GetInt32())
            Assert.Equal("replaceBody", root.GetProperty("stepKind").GetString())
            Assert.Equal("ProtocolError", root.GetProperty("stepError").GetProperty("code").GetString())
            Assert.Contains("missing", root.GetProperty("message").GetString())
            Assert.Contains("Step 2 (replaceBody)", root.GetProperty("message").GetString())
            let unknownKind = rejected (batch [ createFlag; """{"kind":"deleteEverything"}""" ])
            Assert.Contains("\"stepIndex\":1", unknownKind)
            Assert.Contains("createType", unknownKind)
            Assert.Empty((execute """{"id":"p","op":"proposals"}""").GetProperty("proposals").EnumerateArray())
            Assert.Equal(0, (match session.Status() with Ok status -> status.PendingProposalCount | Error error -> failwithf "%A" error))
            Assert.Equal(before, session.SnapshotId)
            Assert.Empty(session.Declarations().Types))

    [<Fact>]
    let ``a batch simulated before another commit is rejected as stale`` () =
        withSession (fun session execute _ ->
            seed execute
            let stale = execute (batch [ createFlag; $"""{{"kind":"replaceBody","function":"inc","replacement":{add n (lit 2)}}}""" ])
            commit execute (execute $"""{{"id":"other","op":"simulateCreateFunctionByName",{(fn "other" n).TrimStart('{')}""")
            let response = AuthoringProtocol.executeLine session $"""{{"id":"late","op":"commit","proposalId":"{proposalId stale}"}}"""
            Assert.Contains("StaleBaseSnapshot", response)
            Assert.DoesNotContain(session.Declarations().Types, fun item -> item.DisplayName = "Flag")
            runs execute "inc" 5 6)

    [<Fact>]
    let ``readiness is judged on the final candidate only`` () =
        withSession (fun session execute _ ->
            seed execute
            let callNode =
                (session.Declarations().Functions |> List.find (fun item -> item.DisplayName = "caller")).Body
                |> ValueGraph.collectNodes
                |> List.pick (fun node -> match node.Kind with SemanticNodeKind.DirectCall _ -> Some node.Id | _ -> None)
            let addParameter =
                $"""{{"kind":"operations","operations":[{{"kind":"addFunctionParameter","function":"inc","displayName":"extra","type":{int64},"callSites":[{{"kind":"leaveMissing","callNodeId":"{StableId.value callNode}"}}]}}]}}"""
            let removeParameter = """{"kind":"operations","operations":[{"kind":"removeFunctionParameter","function":"inc","parameter":"extra"}]}"""
            let readinessOf (proposal: JsonElement) = proposal.GetProperty("review").GetProperty("readiness").GetProperty("kind").GetString()
            // The obligation the first step leaves open is retired by the second: ready.
            let resolved = execute (batch [ addParameter; removeParameter ])
            Assert.Equal("semanticallyReady", readinessOf resolved)
            // The step that leaves it open comes last: incomplete, and a plain commit is refused.
            let open' = execute (batch [ createFunction "other" n; addParameter ])
            Assert.Equal("incomplete", readinessOf open')
            let refused = AuthoringProtocol.executeLine session $"""{{"id":"c","op":"commit","proposalId":"{proposalId open'}"}}"""
            Assert.Contains("BlockingObligations", refused)
            commit execute resolved
            Assert.Equal<string list>([ "n" ], (session.Declarations().Functions |> List.find (fun item -> item.DisplayName = "inc")).Parameters |> List.map (fun item -> item.DisplayName)))

    [<Fact>]
    let ``steps may be read from a confined file and reviews page by step`` () =
        withSession (fun session execute rejected ->
            let dir = Path.GetDirectoryName(session.Path)
            let steps = [ createFlag; createFunction "once" (add n (lit 1)) ]
            File.WriteAllText(Path.Combine(dir, "steps.json"), "[" + String.concat "," steps + "]")
            let proposal = execute """{"id":"file","op":"simulateBatchByName","stepsFile":"steps.json"}"""
            let page = execute $"""{{"id":"page","op":"readProposal","proposalId":"{proposalId proposal}","path":"/review/steps/0"}}"""
            Assert.Equal("createFunction", page.GetProperty("value").GetProperty("kind").GetString())
            Assert.Contains("stay inside", rejected """{"id":"escape","op":"simulateBatchByName","stepsFile":"../outside.json"}""")
            Assert.Contains("exactly one", rejected """{"id":"both","op":"simulateBatchByName","steps":[],"stepsFile":"steps.json"}""")
            Assert.Contains("at least one step", rejected """{"id":"empty","op":"simulateBatchByName","steps":[]}"""))

    [<Fact>]
    let ``a review over the compact bound points at step paths that readProposal pages`` () =
        withSession (fun _ execute _ ->
            let steps = [ for index in 0 .. 59 -> createFunction $"step{index}" (add n (lit index)) ]
            let proposal = execute (batch steps)
            let projection = proposal.GetProperty("projection")
            Assert.True(projection.GetProperty("requiresDetailedReview").GetBoolean(), projection.ToString())
            let paths = projection.GetProperty("omissions").EnumerateArray() |> Seq.map (fun item -> item.GetProperty("path").GetString()) |> List.ofSeq
            Assert.Contains(paths, fun path -> path.StartsWith("/review/steps", StringComparison.Ordinal))
            let page = execute $"""{{"id":"page","op":"readProposal","proposalId":"{proposalId proposal}","path":"/review/steps/58/createdFunction"}}"""
            Assert.Contains("step59", page.GetProperty("value").ToString()))
