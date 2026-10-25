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
    let ``named recipes may be loaded from confined JSON files`` () =
        withSession (fun session execute rejected ->
            let dir = Path.GetDirectoryName(session.Path)
            File.WriteAllText(Path.Combine(dir, "body.json"), add n (lit 1))
            let second = fn "second" (call "first" n)
            File.WriteAllText(Path.Combine(dir, "functions.json"), $"[{second}]")
            let first = $"""{{"id":"first","op":"simulateCreateFunctionByName","displayName":"first","exported":true,"parameters":[{{"displayName":"n","type":{int64}}}],"resultType":{int64},"requiredEffects":[],"bodyFile":"body.json"}}"""
            commit execute (execute first)
            commit execute (execute """{"id":"batch","op":"simulateCreateFunctionsByName","functionsFile":"functions.json"}""")
            runs execute "second" 2 3
            File.WriteAllText(Path.Combine(dir, "replacement.json"), add n (lit 2))
            commit execute (execute """{"id":"replace","op":"simulateReplaceBodyByName","function":"first","replacementFile":"replacement.json"}""")
            runs execute "first" 2 4
            let target = session.Declarations().Functions |> List.find (fun item -> item.DisplayName = "first")
            commit execute (execute $"""{{"id":"target","op":"simulateReplaceTargetByName","function":"first","targetNodeId":"{StableId.value target.Body.ResultNodeId}","replacementFile":"replacement.json"}}""")
            Assert.Contains("exactly one", rejected (first.TrimEnd('}') + ",\"body\":" + n + "}"))
            Assert.Contains("exactly one", rejected """{"id":"both","op":"simulateCreateFunctionsByName","functions":[],"functionsFile":"functions.json"}""")
            Assert.Contains("exactly one", rejected """{"id":"both","op":"simulateReplaceBodyByName","function":"first","replacement":{"kind":"parameter","name":"n"},"replacementFile":"replacement.json"}""")
            Assert.Contains("stay inside", rejected (first.Replace("body.json", "../outside.json")))
            Assert.Contains("stay inside", rejected """{"id":"escape","op":"simulateCreateFunctionsByName","functionsFile":"../outside.json"}""")
            Assert.Contains("stay inside", rejected """{"id":"escape","op":"simulateReplaceBodyByName","function":"first","replacementFile":"../outside.json"}"""))

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
    let ``a batch reports every name resolution problem with its item`` () =
        withSession (fun session execute rejected ->
            let badParameter = (call "first" n).Replace("\"parameter\":\"n\"", "\"parameter\":\"missing\"")
            let response = rejected $"""{{"id":"bad","op":"simulateCreateFunctionsByName","functions":[{fn "first" n},{fn "second" (call "unknown" n)},{fn "third" badParameter}]}}"""
            use document = JsonDocument.Parse(response)
            let error = document.RootElement.GetProperty("error")
            Assert.Equal("ProtocolError", error.GetProperty("code").GetString())
            let problems = error.GetProperty("problems").EnumerateArray() |> Seq.toArray
            Assert.Equal(2, problems.Length)
            Assert.Equal(1, problems[0].GetProperty("index").GetInt32())
            Assert.Equal("second", problems[0].GetProperty("displayName").GetString())
            Assert.Contains("Unknown function", problems[0].GetProperty("message").GetString())
            Assert.Equal(2, problems[1].GetProperty("index").GetInt32())
            Assert.Equal("third", problems[1].GetProperty("displayName").GetString())
            Assert.Contains("Unknown parameter", problems[1].GetProperty("message").GetString())
            Assert.Contains("[1] second:", error.GetProperty("message").GetString())
            Assert.Contains("[2] third:", error.GetProperty("message").GetString())
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

    [<Fact>]
    let ``an unsupported recipe property lists the allowed ones`` () =
        withSession (fun _ _ rejected ->
            let float64 = """{"kind":"primitive","primitive":"float64"}"""
            let text = rejected $"""{{"id":"f","op":"simulateCreateFunctionByName","displayName":"f","exported":true,"parameters":[],"resultType":{float64},"requiredEffects":[],"body":{{"kind":"float64Literal","value":1.5}}}}"""
            Assert.Contains("unsupported properties: value.", text)
            Assert.Contains("Allowed: kind, bits.", text))

    [<Fact>]
    let ``search finds functions by part of a name or a string literal and readFunction can be compact`` () =
        withSession (fun _ execute rejected ->
            let greeting = """{"kind":"stringLiteral","value":"async function "}"""
            let text = $"""{{"displayName":"isDefinition","exported":true,"parameters":[],"resultType":{{"kind":"primitive","primitive":"string"}},"requiredEffects":[],"body":{greeting}}}"""
            commit execute (execute $"""{{"id":"b","op":"simulateCreateFunctionsByName","functions":[{fn "planRun" n},{text}]}}""")
            let found (query: string) =
                (execute $"""{{"id":"s","op":"search","text":"{query}"}}""").GetProperty("matches").EnumerateArray()
                |> Seq.map (fun item -> item.GetProperty("kind").GetString(), item.GetProperty(if item.GetProperty("kind").GetString() = "stringLiteral" then "function" else "name").GetString())
                |> List.ofSeq
            Assert.Equal<(string * string) list>([ "function", "planRun" ], found "PLAN")
            Assert.Equal<(string * string) list>([ "stringLiteral", "isDefinition" ], found "async func")
            Assert.Contains("text must not be empty", rejected """{"id":"e","op":"search","text":" "}""")
            let compact = execute """{"id":"r","op":"readFunction","function":"planRun","detail":"compact"}"""
            let body = compact.GetProperty("function").GetProperty("body")
            Assert.False(body.TryGetProperty("nodes") |> fst)
            Assert.Equal(1, body.GetProperty("nodeCount").GetInt32())
            let full = execute """{"id":"r","op":"readFunction","function":"planRun"}"""
            Assert.True(full.GetProperty("function").GetProperty("body").TryGetProperty("nodes") |> fst))

    [<Fact>]
    let ``a read-only session opens beside a writer, reads and emits, and cannot commit`` () =
        withSession (fun writer execute _ ->
            commit execute (execute $"""{{"id":"c","op":"simulateCreateFunctionByName",{(fn "inc" (add n (lit 1))).TrimStart('{')}""")
            use reader = AuthoringSession.openReadOnly writer.Path |> function Ok value -> value | Error error -> failwithf "%A" error
            Assert.True reader.ReadOnly
            Assert.Equal(1, reader.Declarations().Functions.Length)
            let send line = JsonDocument.Parse(AuthoringProtocol.executeLine reader line)
            use emitted = send """{"id":"e","op":"emitCSharp","profile":{"kind":"empty"}}"""
            Assert.True(emitted.RootElement.GetProperty("ok").GetBoolean(), emitted.RootElement.ToString())
            use proposal = send $"""{{"id":"p","op":"simulateCreateFunctionByName",{(fn "dec" n).TrimStart('{')}"""
            let proposalId = proposal.RootElement.GetProperty("result").GetProperty("proposalId").GetString()
            use refused = send $"""{{"id":"x","op":"commit","proposalId":"{proposalId}"}}"""
            Assert.Equal("ReadOnlySession", refused.RootElement.GetProperty("error").GetProperty("code").GetString())
            reader.Close()
            Assert.True(File.Exists(writer.Path + ".lock"), "closing the reader leaves the writer's lock"))

    [<Fact>]
    let ``emitCSharp writes the source to output and keeps products that contain themselves as classes`` () =
        withSession (fun session execute rejected ->
            let product name fields = $"""{{"id":"t","op":"simulateCreateTypeByName","exported":true,"displayName":"{name}","typeKind":{{"kind":"product","fields":[{fields}]}}}}"""
            commit execute (execute (product "Pair" $"""{{"displayName":"a","type":{int64}}}"""))
            let chain = """{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"named","name":"Chain"}]}"""
            commit execute (execute (product "Chain" $"""{{"displayName":"next","type":{chain}}}"""))
            let emitted = execute """{"id":"e","op":"emitCSharp","profile":{"kind":"empty"},"output":"gen/Program.cs"}"""
            Assert.False(emitted.TryGetProperty("source") |> fst)
            let path = emitted.GetProperty("path").GetString()
            Assert.Equal(Path.Combine(Path.GetDirectoryName session.Path, "gen", "Program.cs"), path)
            let typeName name = (session.Declarations().Types |> List.find (fun t -> t.DisplayName = name)).Id |> CSharpBackendNames.typeName
            let source = File.ReadAllText path
            Assert.Contains("public readonly record struct " + typeName "Pair" + "(", source)
            Assert.Contains("public sealed record " + typeName "Chain" + "(", source)
            Assert.Contains("must name a .cs file", rejected """{"id":"e","op":"emitCSharp","profile":{"kind":"empty"},"output":"gen/Program.txt"}""")
            Assert.Contains("output must stay inside", rejected """{"id":"e","op":"emitCSharp","profile":{"kind":"empty"},"output":"../escape.cs"}"""))

    [<Fact>]
    let ``scopes list the bindings at a node and a targeted review names the binding a reference reads`` () =
        withSession (fun _ execute _ ->
            let maybe = """{"kind":"builtinGeneric","generic":"option","arguments":[{"kind":"primitive","primitive":"int64"}]}"""
            let someBody = add """{"kind":"patternReference","name":"x"}""" (lit 7)
            let body = $"""{{"kind":"matchOption","input":{{"kind":"parameter","name":"maybe"}},"someName":"x","someBody":{someBody},"noneBody":{lit 0}}}"""
            commit execute (execute $"""{{"id":"c","op":"simulateCreateFunctionByName","displayName":"g","exported":true,"parameters":[{{"displayName":"maybe","type":{maybe}}}],"resultType":{int64},"requiredEffects":[],"body":{body}}}""")
            let compact = execute """{"id":"r","op":"readFunction","function":"g","detail":"compact"}"""
            let seven =
                compact.GetProperty("literals").EnumerateArray()
                |> Seq.find (fun item -> item.GetProperty("literal").GetProperty("value").GetString() = "7")
            let nodeId = seven.GetProperty("nodeId").GetString()
            let scopes = execute $"""{{"id":"s","op":"bindingScopes","function":"g","nodeId":"{nodeId}"}}"""
            let frame = scopes.GetProperty("frames").EnumerateArray() |> Seq.exactlyOne
            Assert.Equal(0, frame.GetProperty("scopeDepth").GetInt32())
            Assert.Equal("optionSome", frame.GetProperty("role").GetString())
            let binding = frame.GetProperty("bindings").EnumerateArray() |> Seq.exactlyOne
            Assert.Equal("value", binding.GetProperty("slot").GetString())
            let proposal = execute $"""{{"id":"t","op":"simulateReplaceTargetByName","function":"g","targetNodeId":"{nodeId}","replacement":{{"kind":"patternReference","scopeDepth":0,"slot":"value"}}}}"""
            let reference = proposal.GetProperty("review").GetProperty("diff").GetProperty("patternReferences").EnumerateArray() |> Seq.exactlyOne
            Assert.Equal(nodeId, reference.GetProperty("nodeId").GetString())
            Assert.Equal("optionSome", reference.GetProperty("role").GetString())
            Assert.Equal(frame.GetProperty("ownerNodeId").GetString(), reference.GetProperty("ownerNodeId").GetString())
            Assert.Equal(binding.GetProperty("bindingId").GetString(), reference.GetProperty("binding").GetProperty("bindingId").GetString()))
