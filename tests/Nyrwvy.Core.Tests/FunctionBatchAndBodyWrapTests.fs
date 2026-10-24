namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// docs/decisions/function-creation-batches.md and the whole-body addendum of
/// docs/decisions/original-target-reference.md.
module FunctionBatchAndBodyWrapTests =
    let private int64 = """{"kind":"primitive","primitive":"int64"}"""
    let private n = """{"kind":"parameter","name":"n"}"""
    let private lit (value: int) = $"""{{"kind":"int64Literal","value":"{value}"}}"""
    let private add left right = $"""{{"kind":"int64WrappingAdd","left":{left},"right":{right}}}"""
    let private call name argument = $"""{{"kind":"directCall","function":"{name}","arguments":[{{"parameter":"n","value":{argument}}}]}}"""
    let private fn name body =
        $"""{{"displayName":"{name}","exported":true,"parameters":[{{"displayName":"n","type":{int64}}}],"resultType":{int64},"requiredEffects":[],"body":{body}}}"""

    let private withSession (test: AuthoringSession -> (string -> JsonElement) -> (string -> string) -> unit) =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-function-batch-" + Guid.NewGuid().ToString("N"))
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
                document.RootElement.GetRawText()
            test session execute rejected
        finally
            Directory.Delete(dir, true)

    let private commit (execute: string -> JsonElement) (proposal: JsonElement) =
        execute $"""{{"id":"commit","op":"commit","proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}""" |> ignore

    let private runs (execute: string -> JsonElement) name (argument: int) (expected: int) =
        let run = execute $"""{{"id":"run","op":"runPureByName","function":"{name}","arguments":[{{"kind":"int64","value":{argument}}}],"expected":{{"kind":"int64","value":{expected}}}}}"""
        Assert.True(run.GetProperty("matches").GetBoolean(), run.ToString())

    [<Fact>]
    let ``a batch creates functions in order in one proposal and later ones may call earlier ones`` () =
        withSession (fun session execute _ ->
            let batch = $"""[{fn "inc" (add n (lit 1))},{fn "twice" (call "inc" (call "inc" n))},{fn "down" (call "down" n)}]"""
            let proposal = execute $"""{{"id":"batch","op":"simulateCreateFunctionsByName","functions":{batch}}}"""
            let review = proposal.GetProperty("review")
            Assert.Equal("createFunction", review.GetProperty("kind").GetString())
            Assert.Equal("inc", review.GetProperty("createdFunction").GetProperty("displayName").GetString())
            let others = review.GetProperty("batch").EnumerateArray() |> Seq.map (fun item -> item.GetProperty("createdFunction").GetProperty("displayName").GetString()) |> List.ofSeq
            Assert.Equal<string list>([ "twice"; "down" ], others)
            commit execute proposal
            Assert.Equal(3, session.Declarations().Functions.Length)
            runs execute "twice" 5 7)

    [<Fact>]
    let ``a batch is rejected whole for a later callee or a repeated name`` () =
        withSession (fun session execute rejected ->
            let later = rejected $"""{{"id":"b1","op":"simulateCreateFunctionsByName","functions":[{fn "first" (call "second" n)},{fn "second" n}]}}"""
            Assert.Contains("second", later)
            let repeated = rejected $"""{{"id":"b2","op":"simulateCreateFunctionsByName","functions":[{fn "same" n},{fn "same" n}]}}"""
            Assert.Contains("already exists", repeated)
            let empty = rejected """{"id":"b3","op":"simulateCreateFunctionsByName","functions":[]}"""
            Assert.Contains("at least one function", empty)
            Assert.Empty(session.Declarations().Functions)
            Assert.Empty((execute """{"id":"p","op":"proposals"}""").GetProperty("proposals").EnumerateArray()))

    [<Fact>]
    let ``originalTarget in a whole-body replacement wraps the body and keeps its result node`` () =
        withSession (fun session execute rejected ->
            commit execute (execute $"""{{"id":"create","op":"simulateCreateFunctionByName",{(fn "f" (add n (lit 1))).TrimStart('{')}""")
            let before = session.Declarations().Functions |> List.exactlyOne
            let wrap = add """{"kind":"originalTarget"}""" (lit 10)
            let proposal = execute $"""{{"id":"wrap","op":"simulateReplaceBodyByName","function":"f","replacement":{wrap}}}"""
            Assert.Equal("replaceTarget", proposal.GetProperty("review").GetProperty("kind").GetString())
            commit execute proposal
            let after = session.Declarations().Functions |> List.exactlyOne
            Assert.Equal(before.Body.ResultNodeId, after.Body.ResultNodeId)
            let ids (f: SemanticFunction) = ValueGraph.collectNodes f.Body |> List.map (fun node -> node.Id) |> Set.ofList
            // The wrapper takes the old root ID and the moved root a fresh one, so every old ID stays
            // and the wrapper adds two nodes (the moved root's new ID and the literal).
            Assert.True(Set.isSubset (ids before) (ids after))
            Assert.Equal((ids before).Count + 2, (ids after).Count)
            runs execute "f" 1 12
            // A stale expected result node stays a whole-body request and reports the mismatch.
            let other = ValueGraph.collectNodes after.Body |> List.find (fun node -> node.Id <> after.Body.ResultNodeId)
            let stale =
                rejected $"""{{"id":"stale","op":"simulateReplaceBodyByName","function":"f","expectedResultNodeId":"{StableId.value other.Id}","replacement":{wrap}}}"""
            Assert.Contains("StructuralExpectedResultMismatch", stale))

    [<Fact>]
    let ``an unknown name lists the few known names or suggests the closest of many`` () =
        withSession (fun _ execute rejected ->
            let few = rejected $"""{{"id":"f","op":"simulateCreateFunctionsByName","functions":[{fn "inc" (add n (lit 1))},{fn "twice" (call "icn" n)}]}}"""
            Assert.Contains("Unknown function", few)
            Assert.Contains("Available:", few)
            Assert.Contains("inc", few)
            let misnamed = (call "inc" n).Replace("\"parameter\":\"n\"", "\"parameter\":\"m\"")
            let parameter = rejected $"""{{"id":"p","op":"simulateCreateFunctionsByName","functions":[{fn "inc" (add n (lit 1))},{fn "twice" misnamed}]}}"""
            Assert.Contains("Unknown parameter on inc", parameter)
            Assert.Contains("Available:", parameter)
            let many = [ for index in 1 .. 12 -> fn $"helper{index}" n ] |> String.concat ","
            commit execute (execute $"""{{"id":"m","op":"simulateCreateFunctionsByName","functions":[{many},{fn "totalCount" n}]}}""")
            let close = rejected $"""{{"id":"c","op":"simulateCreateFunctionByName",{(fn "user" (call "totalCuont" n)).TrimStart('{')}"""
            Assert.Contains("Did you mean", close)
            Assert.Contains("totalCount", close)
            Assert.DoesNotContain("helper1", close)
            let far = rejected $"""{{"id":"x","op":"simulateCreateFunctionByName",{(fn "user" (call "zzz" n)).TrimStart('{')}"""
            Assert.DoesNotContain("Did you mean", far))
