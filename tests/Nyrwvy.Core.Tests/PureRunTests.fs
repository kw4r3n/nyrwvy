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
        let ws, floatFunction =
            create ws "float" stringType (TypeRef.Primitive PrimitiveType.Float64) (ExpressionRecipe.Float64Literal(Float64Bits.ofFloat 1.5))
        let listType item = TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])
        let ws, _ =
            create ws "sumLists" (listType intType) intType
                (ExpressionRecipe.ListFold(
                    ExpressionRecipe.ListConcat(ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.ParameterSlot 1),
                    ExpressionRecipe.Int64Literal 0L, plus))
        let ws =
            [ "intList", intType; "stringList", stringType; "boolList", TypeRef.Primitive PrimitiveType.Bool ]
            |> List.fold (fun current (name, itemType) ->
                create current name (listType itemType) (listType itemType)
                    (ExpressionRecipe.ListConcat(ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.ParameterSlot 1))
                |> fst) ws
        let optionType item = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ item ])
        let ws =
            [ "intOption", intType; "stringOption", stringType; "boolOption", TypeRef.Primitive PrimitiveType.Bool ]
            |> List.fold (fun current (name, itemType) ->
                create current name (optionType itemType) (optionType itemType) (ExpressionRecipe.ParameterSlot 0)
                |> fst) ws
        let resultType okType errorType = TypeRef.BuiltinGeneric(BuiltinGenericType.Result, [ okType; errorType ])
        let boolType = TypeRef.Primitive PrimitiveType.Bool
        let ws =
            [ "intStringResult", intType, stringType
              "stringBoolResult", stringType, boolType
              "boolIntResult", boolType, intType
              "boolBoolResult", boolType, boolType ]
            |> List.fold (fun current (name, okType, errorType) ->
                create current name (resultType okType errorType) (resultType okType errorType) (ExpressionRecipe.ParameterSlot 0)
                |> fst) ws
        let ws, _ =
            create ws "checkedText" stringType (resultType intType stringType)
                (ExpressionRecipe.Conditional(
                    ExpressionRecipe.StringIsEmpty(ExpressionRecipe.ParameterSlot 0),
                    ExpressionRecipe.ResultErr(intType, ExpressionRecipe.ParameterSlot 1),
                    ExpressionRecipe.ResultOk(stringType, ExpressionRecipe.Int64Literal Int64.MinValue)))
        let ws, _ =
            create ws "optionalSum" (listType intType) (optionType intType)
                (ExpressionRecipe.Conditional(
                    ExpressionRecipe.Int64Equal(ExpressionRecipe.ListLength(ExpressionRecipe.ParameterSlot 0), ExpressionRecipe.Int64Literal 0L),
                    ExpressionRecipe.OptionNone intType,
                    ExpressionRecipe.OptionSome(ExpressionRecipe.ListFold(
                        ExpressionRecipe.ParameterSlot 0, ExpressionRecipe.Int64Literal 0L, plus))))
        let json = AgentWorkspace.serializeCurrentSnapshot ws |> function
            | Ok text -> text
            | Error error -> failwithf "Fixture serialization failed: %A" error
        json, concat, equal, plus, floatFunction

    let private tempSession () =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-run-tests-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "workspace.json")
        let json, concat, equal, plus, floatFunction = fixture ()
        File.WriteAllText(path, json)
        root, path, concat, equal, plus, floatFunction

    [<Fact>]
    let ``runPure rejects malformed Results and mismatched inactive kinds before execution`` () =
        let root, path, _, _, _, _ = tempSession ()
        try
            use session = AuthoringSession.openExisting path |> function
                | Ok result -> result
                | Error error -> failwithf "Cannot open session: %A" error
            let fn = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = "intStringResult")
            let original = File.ReadAllText path
            let valid = """{"kind":"result","okKind":"int64","errorKind":"string","isOk":true,"value":"0"}"""
            let reject value expectedCode =
                let request = $"""{{"id":"bad-result","op":"runPure","functionId":"{StableId.value fn.Id}","arguments":[{value},{valid}]}}"""
                use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
                Assert.False(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
                Assert.Equal(expectedCode, response.RootElement.GetProperty("error").GetProperty("code").GetString())
            for malformed in
                [ """{"kind":"result","okKind":"int64","isOk":true,"value":"0"}"""
                  """{"kind":"result","okKind":"int64","errorKind":"string","value":"0"}"""
                  """{"kind":"result","okKind":"int64","errorKind":"string","isOk":false,"value":null}"""
                  """{"kind":"result","okKind":"int64","errorKind":"string","isOk":true,"value":"9223372036854775808"}"""
                  """{"kind":"result","okKind":"int64","errorKind":"string","isOk":true,"value":false}"""
                  """{"kind":"result","okKind":"int64","errorKind":"list","isOk":true,"value":"0"}"""
                  """{"kind":"result","okKind":"int64","errorKind":"string","isOk":true}"""
                  """{"kind":"result","okKind":"int64","errorKind":"string","isOk":true,"value":"0","extra":0}""" ] do
                reject malformed "ProtocolError"
            reject """{"kind":"result","okKind":"int64","errorKind":"bool","isOk":true,"value":"0"}""" "RunArgumentTypeMismatch"
            reject """{"kind":"result","okKind":"bool","errorKind":"string","isOk":false,"value":"err"}""" "RunArgumentTypeMismatch"
            let validValue = PureRunValue.ResultOk(PureRunScalarKind.String, PureRunScalar.Int64 0L)
            let wrongExpected = PureRunValue.ResultOk(PureRunScalarKind.Bool, PureRunScalar.Int64 0L)
            match PureRun.run session fn.Id [ validValue; validValue ] (Some wrongExpected) with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunExpectedTypeMismatch", code)
            | other -> failwithf "Mismatched Result expectation accepted: %A" other
            let args = [ PureRunValue.ResultErr(PureRunScalarKind.Int64, PureRunScalar.String(String([| char 0xD800 |])))
                         PureRunValue.ResultOk(PureRunScalarKind.String, PureRunScalar.Int64 0L) ]
            match PureRun.run session fn.Id args None with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunInvalidString", code)
            | other -> failwithf "Invalid payload accepted: %A" other
            let large = PureRunValue.ResultErr(PureRunScalarKind.Int64, PureRunScalar.String(String('x', 65537)))
            match PureRun.run session fn.Id [ large; large ] None with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunArgumentTooLarge", code)
            | other -> failwithf "Oversized payload accepted: %A" other
            Assert.Equal(original, File.ReadAllText path)
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``runPure roundtrips scalar Results and evaluates both authored branches`` () =
        let root, path, _, _, _, _ = tempSession ()
        try
            use session = AuthoringSession.openExisting path |> function
                | Ok result -> result
                | Error error -> failwithf "Cannot open session: %A" error
            let original = File.ReadAllText path
            let run name args (expected: string) expectedMatch =
                let fn = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
                let request = $"""{{"id":"result","op":"runPure","functionId":"{StableId.value fn.Id}","arguments":[{args}],"expected":{expected}}}"""
                use response = AuthoringProtocol.executeLine session request |> JsonDocument.Parse
                Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
                let result = response.RootElement.GetProperty("result")
                Assert.Equal(expectedMatch, result.GetProperty("matches").GetBoolean())
                if expectedMatch then
                    use expectedJson = JsonDocument.Parse(expected)
                    Assert.True(JsonElement.DeepEquals(expectedJson.RootElement, result.GetProperty("actual")), result.ToString())
            for name, value in
                [ "intStringResult", """{"kind":"result","okKind":"int64","errorKind":"string","isOk":true,"value":"9223372036854775807"}"""
                  "intStringResult", """{"kind":"result","okKind":"int64","errorKind":"string","isOk":false,"value":""}"""
                  "stringBoolResult", """{"kind":"result","okKind":"string","errorKind":"bool","isOk":true,"value":"雪\\\"\n"}"""
                  "stringBoolResult", """{"kind":"result","okKind":"string","errorKind":"bool","isOk":false,"value":false}"""
                  "boolIntResult", """{"kind":"result","okKind":"bool","errorKind":"int64","isOk":true,"value":false}"""
                  "boolIntResult", """{"kind":"result","okKind":"bool","errorKind":"int64","isOk":false,"value":"-9223372036854775808"}""" ] do
                run name (value + "," + value) value true
            let ok = """{"kind":"result","okKind":"bool","errorKind":"bool","isOk":true,"value":false}"""
            let err = """{"kind":"result","okKind":"bool","errorKind":"bool","isOk":false,"value":false}"""
            run "boolBoolResult" (ok + "," + err) err false
            run "checkedText" """{"kind":"string","value":""},{"kind":"string","value":"missing"}"""
                """{"kind":"result","okKind":"int64","errorKind":"string","isOk":false,"value":"missing"}""" true
            run "checkedText" """{"kind":"string","value":"present"},{"kind":"string","value":"unused"}"""
                """{"kind":"result","okKind":"int64","errorKind":"string","isOk":true,"value":"-9223372036854775808"}""" true
            Assert.Equal(original, File.ReadAllText path)
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact>]
    let ``runPure rejects wrong types missing targets and unsupported results without publication`` () =
        let root, path, concat, _, _, floatFunction = tempSession ()
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
                ($"""{{"id":"unsupported","op":"runPure","functionId":"{StableId.value floatFunction}","arguments":[{{"kind":"string","value":"a"}},{{"kind":"string","value":"b"}}]}}""")
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
    let ``runPure rejects malformed and oversized List values before execution`` () =
        let root, path, _, _, _, _ = tempSession ()
        try
            use session = AuthoringSession.openExisting path |> function
                | Ok value -> value
                | Error error -> failwithf "Open failed: %A" error
            let target = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = "sumLists")
            for value in
                [ """{"kind":"list","itemKind":"int64","items":[true]}"""
                  """{"kind":"list","itemKind":"int64","items":["9223372036854775808"]}"""
                  """{"kind":"list","itemKind":"list","items":[]}"""
                  """{"kind":"list","itemKind":"int64","items":[],"extra":true}""" ] do
                let command = $"""{{"id":"bad","op":"runPure","functionId":"{StableId.value target.Id}","arguments":[{value},{value}]}}"""
                use response = AuthoringProtocol.executeLine session command |> JsonDocument.Parse
                Assert.False(response.RootElement.GetProperty("ok").GetBoolean())
                Assert.Equal("ProtocolError", response.RootElement.GetProperty("error").GetProperty("code").GetString())
            match PureRun.run session target.Id [ PureRunValue.Int64List(List.replicate 4097 1L); PureRunValue.Int64List [] ] None with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunArgumentTooLarge", code)
            | other -> failwithf "Oversized list accepted: %A" other
            match PureRun.run session target.Id [ PureRunValue.StringList []; PureRunValue.Int64List [] ] None with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunArgumentTypeMismatch", code)
            | other -> failwithf "Wrong empty list type accepted: %A" other
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``runPure executes authored fold and roundtrips typed List results`` () =
        let root, path, _, _, _, _ = tempSession ()
        try
            use session = AuthoringSession.openExisting path |> function
                | Ok value -> value
                | Error error -> failwithf "Open failed: %A" error
            let original = File.ReadAllText(path)
            let run name left right expected =
                let target = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
                let command = $"""{{"id":"list","op":"runPure","functionId":"{StableId.value target.Id}","arguments":[{left},{right}],"expected":{expected}}}"""
                use response = AuthoringProtocol.executeLine session command |> JsonDocument.Parse
                Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
                let result = response.RootElement.GetProperty("result")
                Assert.True(result.GetProperty("matches").GetBoolean(), result.ToString())
                result.GetProperty("actual").Clone()
            let empty = """{"kind":"list","itemKind":"int64","items":[]}"""
            run "sumLists" """{"kind":"list","itemKind":"int64","items":["1","2","3"]}""" empty
                """{"kind":"int64","value":"6"}""" |> ignore
            run "sumLists" empty empty """{"kind":"int64","value":"0"}""" |> ignore
            let integers = """{"kind":"list","itemKind":"int64","items":["9223372036854775807","-9223372036854775808"]}"""
            let actual = run "intList" integers empty integers
            Assert.Equal("9223372036854775807", (actual.GetProperty("items")[0]).GetString())
            let texts = """{"kind":"list","itemKind":"string","items":["🍀","","a\r\n"]}"""
            run "stringList" texts """{"kind":"list","itemKind":"string","items":[]}""" texts |> ignore
            let booleans = """{"kind":"list","itemKind":"bool","items":[true,false]}"""
            run "boolList" """{"kind":"list","itemKind":"bool","items":[]}""" booleans booleans |> ignore
            Assert.Equal(original, File.ReadAllText(path))
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact>]
    let ``runPure rejects malformed Option values and distinguishes empty element types`` () =
        let root, path, _, _, _, _ = tempSession ()
        try
            use session = AuthoringSession.openExisting path |> function
                | Ok value -> value
                | Error error -> failwithf "Open failed: %A" error
            let target = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = "intOption")
            for value in
                [ """{"kind":"option","itemKind":"int64","hasValue":false,"value":0}"""
                  """{"kind":"option","itemKind":"int64","hasValue":true}"""
                  """{"kind":"option","itemKind":"int64","hasValue":true,"value":null}"""
                  """{"kind":"option","itemKind":"int64","hasValue":true,"value":"9223372036854775808"}"""
                  """{"kind":"option","itemKind":"list","hasValue":false}""" ] do
                let command = $"""{{"id":"bad","op":"runPure","functionId":"{StableId.value target.Id}","arguments":[{value},{value}]}}"""
                use response = AuthoringProtocol.executeLine session command |> JsonDocument.Parse
                Assert.False(response.RootElement.GetProperty("ok").GetBoolean())
                Assert.Equal("ProtocolError", response.RootElement.GetProperty("error").GetProperty("code").GetString())
            match PureRun.run session target.Id [ PureRunValue.StringOption None; PureRunValue.Int64Option None ] None with
            | Error(PureRunError.Unsupported(code, _)) -> Assert.Equal("RunArgumentTypeMismatch", code)
            | other -> failwithf "Wrong None type accepted: %A" other
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``runPure roundtrips scalar Options and executes optional fold results`` () =
        let root, path, _, _, _, _ = tempSession ()
        try
            use session = AuthoringSession.openExisting path |> function
                | Ok value -> value
                | Error error -> failwithf "Open failed: %A" error
            let original = File.ReadAllText(path)
            let run name left right expected matches =
                let target = session.Declarations().Functions |> List.find (fun fn -> fn.DisplayName = name)
                let command = $"""{{"id":"option","op":"runPure","functionId":"{StableId.value target.Id}","arguments":[{left},{right}],"expected":{expected}}}"""
                use response = AuthoringProtocol.executeLine session command |> JsonDocument.Parse
                Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString())
                let result = response.RootElement.GetProperty("result")
                Assert.Equal(matches, result.GetProperty("matches").GetBoolean())
                result.GetProperty("actual").Clone()
            let none = """{"kind":"option","itemKind":"int64","hasValue":false}"""
            let some = """{"kind":"option","itemKind":"int64","hasValue":true,"value":"9223372036854775807"}"""
            let actual = run "intOption" some none some true
            Assert.Equal("9223372036854775807", actual.GetProperty("value").GetString())
            let empty = """{"kind":"list","itemKind":"int64","items":[]}"""
            let absent = run "optionalSum" empty empty none true
            Assert.False(absent.GetProperty("hasValue").GetBoolean())
            Assert.False(absent.TryGetProperty("value") |> fst)
            run "optionalSum" """{"kind":"list","itemKind":"int64","items":["2","-2"]}""" empty none false |> ignore
            let text = """{"kind":"option","itemKind":"string","hasValue":true,"value":"🍀"}"""
            run "stringOption" text text text true |> ignore
            let boolean = """{"kind":"option","itemKind":"bool","hasValue":true,"value":false}"""
            run "boolOption" boolean boolean boolean true |> ignore
            Assert.Equal(original, File.ReadAllText(path))
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact; Trait("Suite", "Extended")>]
    let ``runPure uses embedded dependency after source deletion and refuses changed pins`` () =
        let root, source, concat, _, _, _ = tempSession ()
        try
            let local = Path.Combine(root, "local.json")
            use created = AuthoringSession.createNewWithDependency local (Some source) |> function
                | Ok value -> value
                | Error error -> failwithf "Create failed: %A" error
            created.Close()
            File.Delete(source)
            use session = AuthoringSession.openExisting local |> function
                | Ok value -> value
                | Error error -> failwithf "Reopen failed: %A" error
            let args = [ PureRunValue.String "pinned"; PureRunValue.String " content" ]
            match PureRun.run session concat args (Some(PureRunValue.String "pinned content")) with
            | Ok result -> Assert.Equal(Some true, result.Matches)
            | Error error -> failwithf "Embedded execution failed: %A" error
            File.AppendAllText(local + ".dependency.json", " ")
            match PureRun.run session concat args None with
            | Error(PureRunError.SessionFailure(AuthoringSessionError.DependencyFailure _)) -> ()
            | other -> failwithf "Modified pin executed: %A" other
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Fact; Trait("Suite", "Extended")>]
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

    [<Fact; Trait("Suite", "Extended")>]
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
