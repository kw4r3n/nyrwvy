namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

module PureRunTests =
    let private id<'tag> name = StableId.createForTesting<'tag> ("pure-run-" + name)
    let private stringType = TypeRef.Primitive PrimitiveType.String
    let private intType = TypeRef.Primitive PrimitiveType.Int64

    let private fixture () =
        let initial = Snapshot.empty (id<SnapshotTag> "initial")
        let initialWorkspace = AgentWorkspace.create initial |> function
            | Ok value -> value
            | Error errors -> failwithf "Invalid fixture: %A" errors
        let create ws name parameterType resultType body =
            let request: AgentExpressionStructuralWorkflow.FunctionCreation.Request =
                { ExpectedBaseSnapshotId = AgentWorkspace.currentSnapshotId ws
                  DisplayName = name
                  Parameters =
                    [ { DisplayName = "left"; Type = parameterType }
                      { DisplayName = "right"; Type = parameterType } ]
                  ResultType = resultType
                  RequiredEffects = []
                  Body = body }
            let proposal = AgentExpressionStructuralWorkflow.FunctionCreation.simulate ws request |> function
                | Ok result -> result
                | Error error -> failwithf "Cannot author %s: %A" name error
            let fn =
                proposal
                |> AgentExpressionStructuralWorkflow.FunctionCreation.Proposal.review
                |> fun review -> review.CreatedFunction
            match AgentExpressionStructuralWorkflow.FunctionCreation.commit ws proposal with
            | AgentExpressionStructuralWorkflow.FunctionCreation.CommitResult.Committed(next, _) -> next, fn.Id
            | other -> failwithf "Cannot publish %s: %A" name other
        let ws, concat =
            create initialWorkspace "join" stringType stringType
                (ExpressionRecipe.StringConcat(ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.ParameterSlot 1))
        let ws, equal =
            create ws "same" stringType (TypeRef.Primitive PrimitiveType.Bool)
                (ExpressionRecipe.StringEqual(ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.ParameterSlot 1))
        let ws, plus =
            create ws "plus" intType intType
                (ExpressionRecipe.Int64WrappingAdd(ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.ParameterSlot 1))
        let ws, unitFunction =
            create ws "unit" stringType (TypeRef.Primitive PrimitiveType.Unit) ExpressionRecipe.UnitLiteral
        let json = AgentWorkspace.serializeCurrentSnapshot ws |> function
            | Ok text -> text
            | Error error -> failwithf "Fixture serialization failed: %A" error
        json, concat, equal, plus, unitFunction

    let private tempSession () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-run-tests-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        let json, concat, equal, plus, unitFunction = fixture ()
        File.WriteAllText(path, json)
        root, path, concat, equal, plus, unitFunction

    [<Fact>]
    let ``runPure rejects wrong types missing targets and unsupported results without publication`` () =
        let root, path, concat, _, _, unitFunction = tempSession ()
        try
            use session = AuthoringSession.openExisting path |> function
                | Ok result -> result
                | Error error -> failwithf "Cannot open session: %A" error
            let original = File.ReadAllText(path)
            let reject request expected =
                use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
                Assert.False(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
                Assert.Equal(expected, response.RootElement.GetProperty("error").GetProperty("code").GetString())
                Assert.Equal(original, File.ReadAllText(path))
            reject
                ($"""{{"id":"wrong","op":"runPure","functionId":"{StableId.value concat}","arguments":[{{"kind":"bool","value":true}},{{"kind":"string","value":"x"}}]}}""")
                "RunArgumentTypeMismatch"
            reject
                ($"""{{"id":"count","op":"runPure","functionId":"{StableId.value concat}","arguments":[]}}""")
                "RunArgumentCountMismatch"
            reject
                ($"""{{"id":"unknown","op":"runPure","functionId":"{StableId.value (id<FunctionTag> "missing")}","arguments":[]}}""")
                "RunFunctionNotFound"
            reject
                ($"""{{"id":"mismatch","op":"runPure","functionId":"{StableId.value concat}","arguments":[{{"kind":"string","value":"a"}},{{"kind":"string","value":"b"}}],"expected":{{"kind":"bool","value":true}}}}""")
                "RunExpectedTypeMismatch"
            reject
                ($"""{{"id":"unsupported","op":"runPure","functionId":"{StableId.value unitFunction}","arguments":[{{"kind":"string","value":"a"}},{{"kind":"string","value":"b"}}]}}""")
                "RunResultTypeUnsupported"
            reject
                ($"""{{"id":"bad-wire","op":"runPure","functionId":"{StableId.value concat}","arguments":[{{"kind":"string","value":"x","extra":0}},{{"kind":"string","value":"y"}}]}}""")
                "ProtocolError"
            let invalid = String([| char 0xD800 |])
            match PureRun.run session concat [ PureRunValue.String invalid; PureRunValue.String "x" ] None with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunInvalidString", code)
            | other -> failwithf "Invalid UTF-16 argument passed execution: %A" other
            File.AppendAllText(path, " ")
            let modified = File.ReadAllText(path)
            let request =
                $"""{{"id":"modified","op":"runPure","functionId":"{StableId.value concat}","arguments":[{{"kind":"string","value":"a"}},{{"kind":"string","value":"b"}}]}}"""
            use rejected = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
            Assert.False(rejected.RootElement.GetProperty("ok").GetBoolean())
            Assert.Equal("ExternalModification", rejected.RootElement.GetProperty("error").GetProperty("code").GetString())
            Assert.Equal(modified, File.ReadAllText(path))
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact>]
    let ``runPure refuses an effectful entrypoint before compiling`` () =
        let root, sourcePath, concat, _, _, _ = tempSession ()
        try
            let source = SnapshotCodec.deserialize (File.ReadAllText sourcePath) |> function
                | Ok value -> value
                | Error error -> failwithf "Fixture decoding failed: %A" error
            let effectId = id<EffectTag> "test-effect"
            let effectful =
                { source with
                    Effects = [ { Id = effectId; DisplayName = "test" } ]
                    Functions =
                        source.Functions |> List.map (fun fn ->
                            if fn.Id = concat then { fn with RequiredEffects = [ effectId ] } else fn) }
            let path = Path.Combine(root, "effectful.json")
            File.WriteAllText(path, SnapshotCodec.serialize effectful |> function
                | Ok value -> value
                | Error error -> failwithf "Fixture encoding failed: %A" error)
            use session = AuthoringSession.openExisting path |> function
                | Ok value -> value
                | Error error -> failwithf "Cannot open fixture: %A" error
            match PureRun.run session concat [ PureRunValue.String "a"; PureRunValue.String "b" ] None with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunEffectsUnsupported", code)
            | other -> failwithf "Effectful entrypoint was executed: %A" other
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact>]
    let ``authoring CLI runs committed pure scalar function without a handwritten C# harness`` () =
        let root, path, concat, _, _, _ = tempSession ()
        try
            let original = File.ReadAllText(path)
            let start = ProcessStartInfo("dotnet")
            start.RedirectStandardInput <- true
            start.RedirectStandardOutput <- true
            start.RedirectStandardError <- true
            for arg in [ typeof<AuthoringSession>.Assembly.Location; "open"; "--snapshot"; path ] do
                start.ArgumentList.Add(arg)
            use child = Process.Start(start)
            let command =
                $"""{{"id":"cli","op":"runPure","functionId":"{StableId.value concat}","arguments":[{{"kind":"string","value":"from "}},{{"kind":"string","value":"CLI"}}],"expected":{{"kind":"string","value":"from CLI"}}}}"""
            child.StandardInput.WriteLine(command)
            child.StandardInput.Close()
            let stdout = child.StandardOutput.ReadToEndAsync()
            let stderr = child.StandardError.ReadToEndAsync()
            if not (child.WaitForExit(40000)) then
                child.Kill(true)
                failwith "Authoring CLI failed to terminate."
            Assert.Equal(0, child.ExitCode)
            Assert.Equal("", stderr.Result)
            use result = JsonDocument.Parse(stdout.Result.Trim())
            Assert.True(result.RootElement.GetProperty("ok").GetBoolean(), result.RootElement.ToString())
            let output = result.RootElement.GetProperty("result")
            Assert.Equal(StableId.value concat, output.GetProperty("functionId").GetString())
            Assert.Equal("from CLI", output.GetProperty("actual").GetProperty("value").GetString())
            Assert.True(output.GetProperty("matches").GetBoolean())
            Assert.Equal(original, File.ReadAllText(path))
            use reopened = AuthoringSession.openExisting path |> function
                | Ok value -> value
                | Error error -> failwithf "CLI left snapshot locked: %A" error
            Assert.Equal(2, reopened.Declarations().Functions |> List.filter (fun fn -> fn.DisplayName = "join" || fn.DisplayName = "same") |> List.length)
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact>]
    let ``runPure compiles and checks typed scalar results for the committed snapshot`` () =
        let root, path, concat, equal, plus, _ = tempSession ()
        try
            use session = AuthoringSession.openExisting path |> function
                | Ok result -> result
                | Error error -> failwithf "Cannot open session: %A" error
            let original = File.ReadAllText(path)
            let send command =
                let document = AuthoringProtocol.executeLine session command |> JsonDocument.Parse
                if not (document.RootElement.GetProperty("ok").GetBoolean()) then
                    failwithf "runPure failed: %s" (document.RootElement.ToString())
                document
            use textResponse =
                send ($"""{{"id":"text","op":"runPure","functionId":"{StableId.value concat}","arguments":[{{"kind":"string","value":"a"}},{{"kind":"string","value":"🍀"}}],"expected":{{"kind":"string","value":"wrong"}}}}""")
            let text = textResponse.RootElement.GetProperty("result")
            Assert.Equal(StableId.value concat, text.GetProperty("functionId").GetString())
            Assert.Equal("a🍀", text.GetProperty("actual").GetProperty("value").GetString())
            Assert.False(text.GetProperty("matches").GetBoolean())
            use numberResponse =
                send ($"""{{"id":"int64","op":"runPure","functionId":"{StableId.value plus}","arguments":[{{"kind":"int64","value":"9223372036854775807"}},{{"kind":"int64","value":"1"}}],"expected":{{"kind":"int64","value":"-9223372036854775808"}}}}""")
            let number = numberResponse.RootElement.GetProperty("result")
            Assert.Equal("-9223372036854775808", number.GetProperty("actual").GetProperty("value").GetString())
            Assert.True(number.GetProperty("matches").GetBoolean())
            Assert.Equal(text.GetProperty("sourceSnapshotId").GetString(), number.GetProperty("sourceSnapshotId").GetString())
            use boolResponse =
                send ($"""{{"id":"bool","op":"runPure","functionId":"{StableId.value equal}","arguments":[{{"kind":"string","value":"same"}},{{"kind":"string","value":"same"}}]}}""")
            let boolean = boolResponse.RootElement.GetProperty("result")
            Assert.True(boolean.GetProperty("actual").GetProperty("value").GetBoolean())
            Assert.Equal(JsonValueKind.Null, boolean.GetProperty("matches").ValueKind)
            Assert.Equal(original, File.ReadAllText(path))
        finally
            if Directory.Exists root then Directory.Delete(root, true)
