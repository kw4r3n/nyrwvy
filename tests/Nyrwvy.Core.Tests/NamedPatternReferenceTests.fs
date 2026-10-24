namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Nyrwvy.Tools.AuthoringSession
open Xunit

module NamedPatternReferenceTests =
    let private resolve (json: string) =
        use document = JsonDocument.Parse json
        (WireRecipeNames.resolve document.RootElement).GetRawText()
    let private same (expected: string) (actual: string) =
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse expected, JsonNode.Parse actual), actual)
    let private named name = $"""{{"kind":"patternReference","name":"{name}"}}"""
    let private at depth slot = $"""{{"kind":"patternReference","scopeDepth":{depth},"slot":"{slot}"}}"""
    let private one = """{"kind":"int64Literal","value":"1"}"""

    [<Fact>]
    let ``named references count every frame the recipe opens`` () =
        let input =
            $"""{{"kind":"bind","name":"a","value":{one},"body":{{"kind":"matchOption","input":{one},"someName":"b","someBody":{{"kind":"bind","value":{one},"body":{{"kind":"int64WrappingAdd","left":{named "a"},"right":{named "b"}}}}},"noneBody":{named "a"}}}}}"""
        let expected =
            $"""{{"kind":"bind","value":{one},"body":{{"kind":"matchOption","input":{one},"someBody":{{"kind":"bind","value":{one},"body":{{"kind":"int64WrappingAdd","left":{at 2 "value"},"right":{at 1 "value"}}}}},"noneBody":{at 1 "value"}}}}}"""
        same expected (resolve input)
        let union =
            $"""{{"kind":"bind","name":"x","value":{one},"body":{{"kind":"matchUnion","type":"T","input":{one},"arms":[{{"variant":"V","body":{named "x"}}}],"otherwise":{named "x"}}}}}"""
        same $"""{{"kind":"bind","value":{one},"body":{{"kind":"matchUnion","type":"T","input":{one},"arms":[{{"variant":"V","body":{at 1 "value"}}}],"otherwise":{at 1 "value"}}}}}""" (resolve union)
        let result = $"""{{"kind":"matchResult","input":{one},"okName":"v","errName":"e","okBody":{named "v"},"errBody":{{"kind":"bind","name":"e","value":{one},"body":{named "e"}}}}}"""
        same $"""{{"kind":"matchResult","input":{one},"okBody":{at 0 "value"},"errBody":{{"kind":"bind","value":{one},"body":{at 0 "value"}}}}}""" (resolve result)

    [<Fact>]
    let ``unknown, mixed and malformed names are rejected and recipes without names are unchanged`` () =
        let rejects (json: string) (fragment: string) =
            match (try Ok(resolve json) with WireInput.ProtocolInputException message -> Error message) with
            | Error message -> Assert.Contains(fragment, message)
            | Ok rewritten -> failwithf "Accepted %s as %s" json rewritten
        rejects $"""{{"kind":"bind","name":"a","value":{one},"body":{named "b"}}}""" "names in scope: a"
        rejects $"""{{"kind":"bind","name":"a","value":{one},"body":{{"kind":"patternReference","name":"a","scopeDepth":0,"slot":"value"}}}}""" "one form"
        rejects $"""{{"kind":"bind","name":1,"value":{one},"body":{at 0 "value"}}}""" "binding name string"
        rejects $"""{{"kind":"bind","name":null,"value":{one},"body":{at 0 "value"}}}""" "binding name string"
        let plain = $"""{{"kind":"bind","value":{one},"body":{at 0 "value"}}}"""
        Assert.Equal(plain, resolve plain)

    [<Fact>]
    let ``a function written with named references runs like its scopeDepth form`` () =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-named-references-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(dir, "graph.json")) |> function Ok value -> value | Error error -> failwithf "%A" error
            let execute (line: string) =
                use document = JsonDocument.Parse(AuthoringProtocol.executeLine session line)
                Assert.True(document.RootElement.GetProperty("ok").GetBoolean(), document.RootElement.ToString())
                document.RootElement.GetProperty("result").Clone()
            let int64 = """{"kind":"primitive","primitive":"int64"}"""
            let add left right = $"""{{"kind":"int64WrappingAdd","left":{left},"right":{right}}}"""
            let x = add """{"kind":"parameter","name":"n"}""" one
            let y = add (named "x") """{"kind":"int64Literal","value":"10"}"""
            let body =
                $"""{{"kind":"bind","name":"x","value":{x},"body":{{"kind":"bind","name":"y","value":{y},"body":{add (named "x") (named "y")}}}}}"""
            let proposal =
                execute ($"""{{"id":"create","op":"simulateCreateFunctionByName","exported":true,"displayName":"f","parameters":[{{"displayName":"n","type":{int64}}}],"resultType":{int64},"requiredEffects":[],"body":{body}}}""")
            execute ($"""{{"id":"commit","op":"commit","proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}""") |> ignore
            let run = execute """{"id":"run","op":"runPureByName","function":"f","arguments":[{"kind":"int64","value":1}],"expected":{"kind":"int64","value":14}}"""
            Assert.True(run.GetProperty("matches").GetBoolean(), run.ToString())
        finally
            Directory.Delete(dir, true)
