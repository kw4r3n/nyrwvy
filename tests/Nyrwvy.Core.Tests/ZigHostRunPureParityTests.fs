namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Positive assertions accompany byte comparison: an unsupported reply can never pass as execution.
module ZigHostRunPureParityTests =
    open ZigHostParityTests

    let private sid<'tag> name = StableId.createForTesting<'tag> name

    let private success reply =
        use document = parseDocument reply
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean(), reply)
        document.RootElement.GetProperty("result").Clone()

    let private code reply =
        use document = parseDocument reply
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean(), reply)
        document.RootElement.GetProperty("error").GetProperty("code").GetString()

    let private scalar name = $"""{{"kind":"primitive","primitive":"{name}"}}"""
    let private named name = $"""{{"kind":"named","name":"{name}"}}"""
    let private generic name arguments = $"""{{"kind":"builtinGeneric","generic":"{name}","arguments":[{String.concat "," arguments}]}}"""
    let private text = scalar "string"
    let private integer = scalar "int64"
    let private parameter name = $"""{{"kind":"parameter","name":"{name}"}}"""
    let private stringValue value = JsonSerializer.Serialize(value: string)
    let private runName name arguments expected =
        $"""{{"id":"run","op":"runPureByName","function":"{name}","backend":"zig","arguments":[{String.concat "," arguments}]{expected |> Option.map (fun value -> ",\"expected\":" + value) |> Option.defaultValue ""}}}"""

    let private fixture directory =
        let path = Path.Combine(directory, "workspace.json")
        use session = AuthoringSession.createNew path |> unwrap
        let publish line =
            let result = AuthoringProtocol.executeLine session line |> success
            let id = result.GetProperty("proposalId").GetString()
            AuthoringProtocol.executeLine session $"""{{"id":"commit","op":"commit","proposalId":"{id}"}}""" |> success |> ignore
        let create name parameters result effects body =
            let parameters = parameters |> List.map (fun (name, kind) -> $"""{{"displayName":"{name}","type":{kind}}}""") |> String.concat ","
            publish $"""{{"id":"create","op":"simulateCreateFunctionByName","exported":true,"displayName":"{name}","parameters":[{parameters}],"resultType":{result},"requiredEffects":[{String.concat "," (effects |> List.map stringValue)}],"body":{body}}}"""
        publish $"""{{"id":"type","op":"simulateCreateTypeByName","exported":true,"displayName":"Pair","typeKind":{{"kind":"product","fields":[{{"displayName":"text","type":{text}}},{{"displayName":"count","type":{integer}}}]}}}}"""
        publish $"""{{"id":"type","op":"simulateCreateTypeByName","exported":true,"displayName":"Answer","typeKind":{{"kind":"union","variants":[{{"displayName":"Found","fields":[{{"displayName":"text","type":{text}}}]}},{{"displayName":"Missing","fields":[]}}]}}}}"""
        publish $"""{{"id":"type","op":"simulateCreateTypeByName","exported":true,"displayName":"Tagged","typeKind":{{"kind":"product","fields":[{{"displayName":"kind","type":{text}}}]}}}}"""
        publish """{"id":"type","op":"simulateCreateTypeByName","exported":true,"displayName":"Box","typeParameters":["T"],"typeKind":{"kind":"product","fields":[{"displayName":"value","type":{"kind":"typeParameter","name":"T"}}]}}"""
        publish $"""{{"id":"type","op":"simulateCreateTypesByName","types":[{{"exported":true,"displayName":"Chain","typeKind":{{"kind":"union","variants":[{{"displayName":"End","fields":[]}},{{"displayName":"Link","fields":[{{"displayName":"value","type":{integer}}},{{"displayName":"next","type":{named "Chain"}}}]}}]}}}}]}}"""
        for name, kind in
            [ "echo", text; "integer", integer; "boolean", scalar "bool"; "float", scalar "float64"
              "bytes", scalar "bytes"; "unit", scalar "unit"
              "pair", named "Pair"; "answer", named "Answer"; "chain", named "Chain"
              "tagged", named "Tagged"; "box", $"""{{"kind":"named","name":"Box","arguments":[{text}]}}"""
              "nested", generic "list" [ generic "option" [ named "Pair" ] ]
              "mapping", generic "map" [ text; integer ]; "set", generic "set" [ integer ]
              "result", generic "result" [ integer; text ] ] do
            create name [ "value", kind ] kind [] (parameter "value")
        create "constant" [] integer [] """{"kind":"int64Literal","value":"7"}"""
        create "add" [ "a", integer; "b", integer ] integer [] $"""{{"kind":"int64WrappingAdd","left":{parameter "a"},"right":{parameter "b"}}}"""
        publish """{"id":"effect","op":"simulateCreateEffect","exported":true,"displayName":"io"}"""
        publish $"""{{"id":"capability","op":"simulateCreateCapabilityByName","exported":true,"displayName":"Provider","operations":[{{"displayName":"lookup","parameters":[{{"displayName":"key","type":{text}}}],"resultType":{named "Answer"},"effect":"io"}}]}}"""
        create "query" [ "key", text; "provider", """{"kind":"capability","name":"Provider"}""" ] (named "Answer") [ "io" ]
            $"""{{"kind":"capabilityInvoke","capabilityValue":{parameter "provider"},"capability":"Provider","operation":"lookup","arguments":[{{"parameter":"key","value":{parameter "key"}}}]}}"""
        publish $"""{{"id":"recursive","op":"simulateCreateFunctionsByName","functions":[{{"displayName":"spin","exported":true,"parameters":[],"resultType":{integer},"requiredEffects":[],"body":{{"kind":"directCall","function":"spin","arguments":[]}}}}]}}"""
        File.WriteAllText(path + ".backends.json", """{"targets":["zig"]}""")
        path, session.Declarations()

    let private pair textValue count = $"""{{"kind":"product","type":"Pair","fields":[{{"field":"text","value":{{"kind":"string","value":{stringValue textValue}}}}},{{"field":"count","value":{{"kind":"int64","value":"{count}"}}}}]}}"""
    let private found value = $"""{{"kind":"union","type":"Answer","variant":"Found","fields":[{{"field":"text","value":{{"kind":"string","value":{stringValue value}}}}}]}}"""
    let private missing = """{"kind":"union","type":"Answer","variant":"Missing","fields":[]}"""
    let private provider script = $"""{{"kind":"fakeCapability","capability":"Provider","operations":[{{"operation":"lookup",{script}}}]}}"""
    let private responses responses = provider $""""responses":[{String.concat "," responses}]"""
    let private returns value = $"""{{"kind":"return","value":{value}}}"""

    let private strictPairWithEnvironment environment path requests =
        // Building a tool is outside either host's first-response budget.
        zigHost () |> ignore
        ZigTools.tool "emitter" |> unwrap |> ignore
        if environment |> List.exists (fun (name, value) -> name = "NYRWVY_ZIG_TOOLS" && value = "1") then
            ZigTools.tool "model-reader" |> unwrap |> ignore
            ZigTools.tool "snapshot-reader" |> unwrap |> ignore
        let runHost zig =
            let exitCode, errors, replies = runWithEnvironment environment zig [ "open"; "--snapshot"; path; "--read-only" ] requests
            Assert.Equal(0, exitCode)
            Assert.Equal("", errors)
            Assert.Equal(requests.Length, replies.Length)
            replies
        let expected, actual = runHost false, runHost true
        for request, (wanted, found) in List.zip requests (List.zip expected actual) do
            Assert.True((wanted = found), $"{request}\n F#: {wanted}\nZig: {found}")
        expected

    let private strictPair path requests = strictPairWithEnvironment [] path requests

    let private withHost environment zig path action =
        use host = startWithEnvironment environment zig [ "open"; "--snapshot"; path ]
        let errors = host.StandardError.ReadToEndAsync()
        let send request =
            host.StandardInput.WriteLine(request: string)
            host.StandardInput.Flush()
            let reading = host.StandardOutput.ReadLineAsync()
            if not (reading.Wait silence) then
                host.Kill true
                failwithf "Host exceeded its response deadline: %s" request
            let reply = reading.Result
            if isNull reply then failwithf "Host stopped before answering %s" request
            reply
        try action send
        finally
            host.StandardInput.Close()
            if not (host.WaitForExit(int silence.TotalMilliseconds)) then host.Kill true
            host.WaitForExit()
            Assert.Equal(0, host.ExitCode)
            Assert.Equal("", errors.Result.Trim())

    [<Fact; Trait("Suite", "Extended")>]
    let ``native Zig host executes exact named plain typed and recursive values on JSON and records`` () =
        withDirectory (fun directory ->
            let path, declarations = fixture directory
            let chainEnd = """{"kind":"union","type":"Chain","variant":"End","fields":[]}"""
            let chain = $"""{{"kind":"union","type":"Chain","variant":"Link","fields":[{{"field":"value","value":{{"kind":"int64","value":"3"}}}},{{"field":"next","value":{chainEnd}}}]}}"""
            let values =
                [ "echo", stringValue "日本 😀\u0000<&", stringValue "日本 😀\u0000<&"
                  "integer", "\"-9223372036854775808\"", "\"-9223372036854775808\""
                  "boolean", "true", "true"; "float", "-0.25", "-0.25"
                  "bytes", """{"kind":"bytes","hex":"00ff80"}""", """{"kind":"bytes","hex":"00ff80"}"""
                  "unit", "null", "null"; "pair", pair "a😀" -4, pair "a😀" -4
                  "tagged", """{"kind":"data"}""", """{"kind":"data"}"""
                  "box", """{"value":"generic 日本"}""", """{"value":"generic 日本"}"""
                  "answer", found "ok", found "ok"; "chain", chain, chain
                  "nested", """[{"text":"one","count":"1"},null]""", """[{"text":"one","count":"1"},null]"""
                  "mapping", """[["b","2"],["a","1"]]""", """[["b","2"],["a","1"]]"""
                  "set", "[3,1,2]", "[3,1,2]"; "result", """{"ok":"42"}""", """{"ok":"42"}"""
                  "integer", "9223372036854775807", "9223372036854775807"
                  for value in [ "-0"; "1e-5"; "1e16"; "1.234e17"; "5e-324"; "\"NaN\""; "\"Infinity\""; "\"-Infinity\"" ] do
                      "float", value, value ]
            let echoId = declarations.Functions |> List.find (fun fn -> fn.DisplayName = "echo") |> fun fn -> StableId.value fn.Id
            let exact = $"""{{"id":"exact","op":"runPure","functionId":"{echoId}","backend":"zig","arguments":[{{"kind":"string","value":"exact"}}],"expected":{{"kind":"string","value":"exact"}}}}"""
            let requests = values |> List.map (fun (name, argument, expected) -> runName name [ argument ] (Some expected))
            let manifest = (runName "constant" [] (Some "7")).Replace(",\"backend\":\"zig\"", "")
            let mismatch = runName "pair" [ pair "actual" 1 ] (Some(pair "expected" 1))
            let requests = requests @ [ exact; manifest; mismatch ]
            for record in [ false; true ] do
                let source = if record then Path.ChangeExtension(path, ".nyrec") else path
                if record then
                    File.WriteAllText(source, File.ReadAllText path |> SnapshotCodec.deserialize |> unwrap |> RecordCodec.serialize |> unwrap)
                    File.Copy(path + ".backends.json", source + ".backends.json")
                let replies = strictPair source requests
                for reply in replies |> List.take (replies.Length - 1) do Assert.True((success reply).GetProperty("matches").GetBoolean(), reply)
                let result = success (List.last replies)
                Assert.False(result.GetProperty("matches").GetBoolean())
                Assert.Equal(1, result.GetProperty("mismatch").GetProperty("path").GetArrayLength())
            Assert.Equal("7", (strictPair path [ manifest ] |> List.head |> success).GetProperty("actual").GetProperty("value").GetString()))

    [<Fact; Trait("Suite", "Extended")>]
    let ``native Zig host resets queued and keyed fakes and preserves failures and call metadata`` () =
        withDirectory (fun directory ->
            let path, _ = fixture directory
            let fake = responses [ returns (found "first"); returns (found "unused") ]
            let keyed = provider $""""cases":[{{"arguments":[{{"parameter":"key","value":{{"kind":"string","value":"a"}}}}],"responses":[{returns missing}]}}]"""
            let ok = runName "query" [ "\"a\""; fake ] (Some(found "first"))
            let requests =
                [ ok; ok; runName "query" [ "\"a\""; keyed ] (Some missing)
                  runName "query" [ "\"missing\""; keyed ] None
                  runName "query" [ "\"a\""; responses [] ] None
                  runName "query" [ "\"a\""; responses [ """{"kind":"throw","message":"down 日本"}""" ] ] None
                  ok ]
            let replies = strictPair path requests
            for index in [ 0; 1; 2; 6 ] do
                let result = success replies[index]
                Assert.True(result.GetProperty("matches").GetBoolean())
                let call = result.GetProperty("calls").[0]
                Assert.Equal("provider", call.GetProperty("parameter").GetString())
                Assert.Equal("lookup", call.GetProperty("operation").GetString())
                Assert.Equal("key", call.GetProperty("arguments").[0].GetProperty("parameter").GetString())
            Assert.Equal("RunBindingNoMatch", code replies[3])
            Assert.Equal("RunBindingExhausted", code replies[4])
            Assert.Equal("RunRuntimeFailure", code replies[5]))

    [<Fact; Trait("Suite", "Extended")>]
    let ``native Zig host preserves pre execution error ordering and recovers after each rejection`` () =
        withDirectory (fun directory ->
            let path, declarations = fixture directory
            let echoId = declarations.Functions |> List.find (fun fn -> fn.DisplayName = "echo") |> fun fn -> StableId.value fn.Id
            let failures =
                [ (runName "echo" [] None), "RunArgumentCountMismatch"
                  (runName "echo" [ "1" ] None), "ProtocolError"
                  (runName "echo" [ "\"\\ud800\"" ] None), "ProtocolError"
                  (runName "echo" [ """{"kind":"bits32","value":-0}""" ] None), "ProtocolError"
                  (runName "echo" [ """{"kind":"int64","value":"1"}""" ] None), "RunArgumentTypeMismatch"
                  (runName "echo" [ "\"a\"" ] (Some """{"kind":"bool","value":true}""")), "RunExpectedTypeMismatch"
                  (runName "echo" [ "\"a\"" ] None).Replace("\"backend\":\"zig\"", "\"backend\":\"other\""), "ProtocolError"
                  (runName "echo" [ "\"a\"" ] None).Replace("\"arguments\":", "\"extra\":false,\"arguments\":"), "ProtocolError"
                  $"""{{"id":"run","op":"runPure","functionId":"{echoId}","backend":"zig","arguments":null}}""", "ProtocolError"
                  $"""{{"id":"run","op":"runPure","functionId":"{echoId}","backend":"zig","arguments":["a"],"hostSources":["missing.cs"]}}""", "RunHostSourcesDisabled"
                  runName "query" [ "\"a\""; """{"kind":"fakeCapability","capability":"Provider","operations":[{"operation":"lookup","responses":[]},{"operation":"lookup","responses":[]}]}""" ] None, "RunBindingShapeMismatch"
                  runName "query" [ "\"a\""; responses [ """{"kind":"return","value":{"kind":"int64","value":"1"}}""" ] ] None, "RunBindingShapeMismatch"
                  runName "query" [ "\"a\""; responses [ """{"kind":"return","value":{"kind":"union","type":"Answer","variant":"Found","fields":[{"field":"text","value":{"kind":"int64","value":"1"}}]}}""" ] ] None, "RunUnionShapeMismatch" ]
            let good = runName "constant" [] (Some "7")
            let replies = strictPair path (failures |> List.collect (fun (request, _) -> [ request; good ]))
            for index, (_, expectedCode) in failures |> List.indexed do
                Assert.Equal(expectedCode, code replies[index * 2])
                Assert.True((success replies[index * 2 + 1]).GetProperty("matches").GetBoolean()))

    [<Fact; Trait("Suite", "Extended")>]
    let ``native Zig host runs local and imported entries from embedded dependency selections`` () =
        withDirectory (fun directory ->
            let path, _ = fixture directory
            let original = File.ReadAllText path |> SnapshotCodec.deserialize |> unwrap
            let imported = original.Functions |> List.find (fun fn -> fn.DisplayName = "constant")
            let dependency = { Snapshot.empty (StableId.createForTesting<SnapshotTag> "018bcfe5-6700-7000-8000-000000009999") with Functions = [ imported ] }
            let local = { original with Functions = original.Functions |> List.filter (fun fn -> fn.Id <> imported.Id) }
            let dependencyJson = SnapshotCodec.serialize dependency |> unwrap
            File.WriteAllText(path, SnapshotCodec.serializeResolved dependency local |> unwrap)
            File.WriteAllText(path + ".dependency.json", (memberOf dependency (Some("snapshotJson", dependencyJson)) "missing-dependency.json").ToJsonString())
            let requests =
                [ runName "constant" [] (Some "7")
                  runName "echo" [ "\"local 日本\"" ] (Some "\"local 日本\"")
                  $"""{{"id":"imported","op":"runPure","functionId":"{StableId.value imported.Id}","arguments":[],"backend":"zig","expected":7}}""" ]
            for reply in strictPair path requests do Assert.True((success reply).GetProperty("matches").GetBoolean(), reply))

    [<Fact; Trait("Suite", "Extended")>]
    let ``native Zig host reuses source builds across inputs names and entries and invalidates changed bodies`` () =
        withDirectory (fun directory ->
            let path, declarations = fixture directory
            let trace = Path.Combine(directory, "compiler-builds")
            let cache = Path.Combine(directory, "cache")
            let compiler = Path.Combine(directory, "compiler")
            let quote (value: string) = "'" + value.Replace("'", "'\\''") + "'"
            File.WriteAllText(compiler, $"#!/bin/sh\nif [ \"$1\" = build-exe ]; then printf 'build\\n' >> {quote trace}; fi\nexec {quote (ZigTools.executable ())} \"$@\"\n")
            File.SetUnixFileMode(compiler, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
            zigHost () |> ignore
            let environment = [ "NYRWVY_ZIG", compiler; "XDG_CACHE_HOME", cache ]
            let builds () = if File.Exists trace then File.ReadAllLines(trace).Length else 0
            withHost environment true path (fun send ->
                let original = File.ReadAllText path
                for name, arguments in [ "echo", [ "\"one\"" ]; "echo", [ "\"two\"" ]; "integer", [ "42" ] ] do
                    send (runName name arguments (Some arguments.Head)) |> success |> ignore
                for answer in [ "A"; "B" ] do
                    send (runName "query" [ "\"a\""; responses [ returns (found answer) ] ] (Some(found answer))) |> success |> ignore
                Assert.Equal(original, File.ReadAllText path)
                Assert.Equal(1, builds ())
                let publish request =
                    let proposal = send request |> success |> fun reply -> reply.GetProperty("proposalId").GetString()
                    send $"""{{"id":"commit","op":"commit","proposalId":"{proposal}"}}""" |> success |> ignore
                publish """{"id":"rename","op":"simulateOperationsByName","operations":[{"kind":"renameFunction","function":"echo","displayName":"renamed"}]}"""
                send (runName "renamed" [ "\"three\"" ] (Some "\"three\"")) |> success |> ignore
                Assert.Equal(1, builds ())
                publish """{"id":"body","op":"simulateReplaceBodyByName","function":"constant","replacement":{"kind":"int64Literal","value":"9"}}"""
                let result = send (runName "constant" [] (Some "9")) |> success
                Assert.True(result.GetProperty("matches").GetBoolean())
                Assert.Equal(2, builds ()))
            withHost environment true path (fun send ->
                Assert.True((send (runName "constant" [] (Some "9")) |> success).GetProperty("matches").GetBoolean())
                Assert.Equal(2, builds ()))
            // PATH wrappers can change their build flags while `zig env` stays identical.
            let originalCompiler = File.ReadAllText compiler
            File.WriteAllText(compiler, originalCompiler + "# changed compiler wrapper\n")
            let pathEnvironment = [ "NYRWVY_ZIG", "compiler"; "XDG_CACHE_HOME", cache; "PATH", directory + Path.PathSeparator.ToString() + Environment.GetEnvironmentVariable("PATH") ]
            withHost pathEnvironment true path (fun send ->
                send (runName "constant" [] (Some "9")) |> success |> ignore
                let previous = builds ()
                File.WriteAllText(compiler, originalCompiler + "# changed again within the same host\n")
                send (runName "constant" [] (Some "9")) |> success |> ignore
                Assert.Equal(previous + 1, builds ()))
            let echoId = declarations.Functions |> List.find (fun fn -> fn.DisplayName = "echo") |> fun fn -> StableId.value fn.Id
            let exact = $"""{{"id":"reopen","op":"runPure","backend":"zig","functionId":"{echoId}","arguments":["after reopen"]}}"""
            let _, _, replies = run true [ "open"; "--snapshot"; path; "--read-only" ] [ exact ]
            Assert.Equal("renamed", (success replies.Head).GetProperty("function").GetString()))

    [<Fact; Trait("Suite", "Extended")>]
    let ``native Zig host bounds recursive execution and survives compiler failure`` () =
        withDirectory (fun directory ->
            let path, _ = fixture directory
            let replies = strictPair path [ runName "spin" [] None; runName "constant" [] (Some "7") ]
            Assert.Equal("RunTimeout", code replies[0])
            Assert.True((success replies[1]).GetProperty("matches").GetBoolean())
            let compiler = Path.Combine(directory, "compiler-fails")
            File.WriteAllText(compiler, "#!/bin/sh\nif [ \"$1\" = build-exe ]; then printf 'deliberate compiler failure' >&2; exit 1; fi\nexec zig \"$@\"\n")
            File.SetUnixFileMode(compiler, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
            let environment = [ "NYRWVY_ZIG", compiler; "XDG_CACHE_HOME", Path.Combine(directory, "compiler-cache") ]
            let requests = [ runName "constant" [] None; """{"id":"status","op":"status"}"""; runName "constant" [] None ]
            let expected =
                let exitCode, _, replies = runWithEnvironment environment false [ "open"; "--snapshot"; path; "--read-only" ] requests
                Assert.Equal(0, exitCode)
                replies |> List.map comparable
            let exitCode, _, actual = runWithEnvironment environment true [ "open"; "--snapshot"; path; "--read-only" ] requests
            Assert.Equal(0, exitCode)
            Assert.Equal<string list>(expected, actual |> List.map comparable)
            Assert.Equal("RunProcessFailure", code actual[0])
            success actual[1] |> ignore
            Assert.Equal("RunProcessFailure", code actual[2]))

    [<Fact; Trait("Suite", "Extended")>]
    let ``native Zig host keeps the staged CSharp gate after shared validation`` () =
        withDirectory (fun directory ->
            let path, _ = fixture directory
            let request = (runName "echo" [ "\"value\"" ] None).Replace("\"backend\":\"zig\"", "\"backend\":\"csharp\"")
            let _, _, replies = run true [ "open"; "--snapshot"; path; "--read-only" ] [ request; request.Replace("\"arguments\":[\"value\"]", "\"arguments\":[]") ]
            Assert.Equal("UnsupportedOperation", code replies[0])
            Assert.Equal("RunArgumentCountMismatch", code replies[1]))

    [<Fact; Trait("Suite", "Extended")>]
    let ``native Zig host preserves emitter limits and reachable fallback includes fake signature types`` () =
        withDirectory (fun directory ->
            let path, _ = fixture directory
            do
                use session = AuthoringSession.openExisting path |> unwrap
                let proposal = AuthoringProtocol.executeLine session """{"id":"bits","op":"simulateCreateFunctionByName","exported":true,"displayName":"bits","parameters":[{"displayName":"value","type":{"kind":"primitive","primitive":"bits32"}}],"resultType":{"kind":"primitive","primitive":"bits32"},"requiredEffects":[],"body":{"kind":"parameter","name":"value"}}""" |> success
                AuthoringProtocol.executeLine session $"""{{"id":"commit","op":"commit","proposalId":"{proposal.GetProperty("proposalId").GetString()}"}}""" |> success |> ignore
            let requests = [ runName "bits" [ "4294967295" ] None; runName "constant" [] (Some "7"); runName "query" [ "\"a\""; responses [ returns (found "ok") ] ] (Some(found "ok")) ]
            let replies = strictPair path requests
            Assert.Equal("RunBackendUnsupported", code replies[0])
            for reply in replies.Tail do Assert.True((success reply).GetProperty("matches").GetBoolean(), reply))

    [<Fact; Trait("Suite", "Extended")>]
    let ``native Zig host preserves backend manifest ordering and external source invalidation`` () =
        withDirectory (fun directory ->
            let sourceDir = Path.Combine(directory, "source")
            Directory.CreateDirectory sourceDir |> ignore
            let source, _ = fixture sourceDir
            seededWritablePair "run-persisted" source directory (fun path send ->
                send """{"id":"opened","op":"status"}""" |> success |> ignore
                File.AppendAllText(path, " ")
                File.Delete(path + ".backends.json")
                let invalid = (runName "echo" [] None).Replace(",\"backend\":\"zig\"", "")
                Assert.Equal("ProtocolError", code (send invalid))
                File.WriteAllText(path + ".backends.json", """{"targets":["zig"]}""")
                Assert.Equal("ExternalModification", code (send (runName "constant" [] None)))
                Assert.Equal("SessionInvalidated", code (send (runName "constant" [] None)))) |> ignore)

    [<Fact; Trait("Suite", "Extended")>]
    let ``native Zig runner transports recursive keys UTF16 identities and seventeen fake bindings`` () =
        withDirectory (fun directory ->
            let path, _ = fixture directory
            do
                use session = AuthoringSession.openExisting path |> unwrap
                let publish request =
                    let proposal = AuthoringProtocol.executeLine session request |> success |> fun item -> item.GetProperty("proposalId").GetString()
                    AuthoringProtocol.executeLine session $"""{{"id":"commit","op":"commit","proposalId":"{proposal}"}}""" |> success |> ignore
                let parameters = [ 0 .. 16 ] |> List.map (fun index -> $"""{{"displayName":"provider{index}","type":{{"kind":"capability","name":"Provider"}}}}""") |> String.concat ","
                publish $"""{{"id":"many","op":"simulateCreateFunctionByName","exported":true,"displayName":"many","parameters":[{parameters}],"resultType":{named "Answer"},"requiredEffects":["io"],"body":{{"kind":"capabilityInvoke","capabilityValue":{parameter "provider16"},"capability":"Provider","operation":"lookup","arguments":[{{"parameter":"key","value":{{"kind":"stringLiteral","value":"a"}}}}]}}}}"""
                publish $"""{{"id":"walker","op":"simulateCreateCapabilityByName","exported":true,"displayName":"Walker","operations":[{{"displayName":"walk","parameters":[{{"displayName":"chain","type":{named "Chain"}}}],"resultType":{named "Answer"},"effect":"io"}}]}}"""
                publish $"""{{"id":"walk","op":"simulateCreateFunctionByName","exported":true,"displayName":"walk","parameters":[{{"displayName":"chain","type":{named "Chain"}}},{{"displayName":"walker","type":{{"kind":"capability","name":"Walker"}}}}],"resultType":{named "Answer"},"requiredEffects":["io"],"body":{{"kind":"capabilityInvoke","capabilityValue":{parameter "walker"},"capability":"Walker","operation":"walk","arguments":[{{"parameter":"chain","value":{parameter "chain"}}}]}}}}"""
                publish $"""{{"id":"reader","op":"simulateCreateCapabilityByName","exported":true,"displayName":"FloatReader","operations":[{{"displayName":"read","parameters":[{{"displayName":"number","type":{scalar "float64"}}}],"resultType":{text},"effect":"io"}}]}}"""
                publish $"""{{"id":"nan","op":"simulateCreateFunctionByName","exported":true,"displayName":"negatedNaN","parameters":[{{"displayName":"reader","type":{{"kind":"capability","name":"FloatReader"}}}},{{"displayName":"number","type":{scalar "float64"}}}],"resultType":{text},"requiredEffects":["io"],"body":{{"kind":"capabilityInvoke","capabilityValue":{parameter "reader"},"capability":"FloatReader","operation":"read","arguments":[{{"parameter":"number","value":{{"kind":"float64Negate","value":{parameter "number"}}}}}]}}}}"""
            let snapshot = File.ReadAllText path |> SnapshotCodec.deserialize |> unwrap
            let rewrittenTypes =
                snapshot.Types |> List.map (fun item ->
                    match item.DisplayName, item.Kind with
                    | "Pair", SemanticTypeKind.Product fields ->
                        { item with Kind = SemanticTypeKind.Product(fields |> List.map (fun field -> { field with Id = StableId.createForTesting<FieldTag> (if field.DisplayName = "text" then "field-\U00010000" else "field-\uE000") })) }
                    | "Answer", SemanticTypeKind.Union variants ->
                        { item with Kind = SemanticTypeKind.Union(variants |> List.map (fun variant -> { variant with Id = StableId.createForTesting<VariantTag> (if variant.DisplayName = "Found" then "variant-\U00010000" else "variant-\uE000") })) }
                    | _ -> item)
            let rewritten = { snapshot with Types = rewrittenTypes }
            File.WriteAllText(path, SnapshotCodec.serialize rewritten |> unwrap)
            let chain = """{"kind":"union","type":"Chain","variant":"Link","fields":[{"field":"value","value":{"kind":"int64","value":"3"}},{"field":"next","value":{"kind":"union","type":"Chain","variant":"End","fields":[]}}]}"""
            let walker = $"""{{"kind":"fakeCapability","capability":"Walker","operations":[{{"operation":"walk","cases":[{{"arguments":[{{"parameter":"chain","value":{chain}}}],"responses":[{returns (found "walked")}]}}]}}]}}"""
            let requests =
                [ runName "many" ([ 0 .. 16 ] |> List.map (fun index -> responses [ returns (found (string index)) ])) (Some(found "16"))
                  runName "walk" [ chain; walker ] (Some(found "walked"))
                  runName "pair" [ pair "unicode identities" 2 ] (Some(pair "unicode identities" 2))
                  runName "query" [ "\"a\""; responses [ returns missing ] ] (Some missing)
                  runName "negatedNaN" [ """{"kind":"fakeCapability","capability":"FloatReader","operations":[{"operation":"read","cases":[{"arguments":[{"parameter":"number","value":{"kind":"float64","value":"NaN"}}],"responses":[{"kind":"return","value":{"kind":"string","value":"matched"}}]}]}]}"""; "\"NaN\"" ] None ]
            let replies = strictPair path requests
            for reply in replies |> List.take 4 do Assert.True((success reply).GetProperty("matches").GetBoolean(), reply)
            Assert.Equal("provider16", (success replies.Head).GetProperty("calls").[0].GetProperty("parameter").GetString())
            // The existing Zig backend compares scripted NaN keys by their token bits.
            Assert.Equal("RunBindingNoMatch", code replies[4]))

    [<Fact; Trait("Suite", "Extended")>]
    let ``native Zig host respects private imported declaration visibility before backend execution`` () =
        withDirectory (fun directory ->
            let path, _ = fixture directory
            let snapshot = File.ReadAllText path |> SnapshotCodec.deserialize |> unwrap
            let pairType = snapshot.Types |> List.find (fun item -> item.DisplayName = "Pair")
            let fn = snapshot.Functions |> List.find (fun item -> item.DisplayName = "pair")
            let dependency =
                { Snapshot.empty (StableId.createForTesting<SnapshotTag> "private-dependency") with
                    Types = [ { pairType with Exported = false } ]; Functions = [ fn ] }
            let local = Snapshot.empty (StableId.createForTesting<SnapshotTag> "private-local")
            File.WriteAllText(path, SnapshotCodec.serializeResolved dependency local |> unwrap)
            File.WriteAllText(path + ".dependency.json", (memberOf dependency (Some("snapshotJson", SnapshotCodec.serialize dependency |> unwrap)) "missing.json").ToJsonString())
            let request = runName "pair" [ """{"text":"hidden","count":1}""" ] None
            Assert.Equal("ProtocolError", code (strictPair path [ request ]).Head))

    [<Fact; Trait("Suite", "Extended")>]
    let ``native Zig host accepts recursive outputs through the complete envelope depth boundary`` () =
        withDirectory (fun directory ->
            let typeId = sid<TypeTag> "deep-node"
            let fieldId = sid<FieldTag> "deep-next"
            let nodeType = TypeRef.Named(typeId, [])
            let optionType = TypeRef.BuiltinGeneric(BuiltinGenericType.Option, [ nodeType ])
            let create count optionResult: SemanticFunction =
                let nodes = ResizeArray<SemanticNode>()
                let nodeId suffix = sid<GraphNodeTag> $"deep-{count}-{suffix}"
                let mutable previous = nodeId "none"
                nodes.Add { Id = previous; Kind = SemanticNodeKind.OptionNone nodeType }
                for index in 0 .. count - 1 do
                    let value = nodeId $"node-{index}"
                    nodes.Add { Id = value; Kind = SemanticNodeKind.ProductConstruct(typeId, [], [ { TargetFieldId = fieldId; ValueNodeId = previous } ]) }
                    previous <- value
                    if index < count - 1 || optionResult then
                        let some = nodeId $"some-{index}"
                        nodes.Add { Id = some; Kind = SemanticNodeKind.OptionSome previous }
                        previous <- some
                { Id = sid<FunctionTag> $"deep-{count}"; DisplayName = $"deep{count}"; Exported = true
                  TypeParameters = []; Parameters = []; ResultType = if optionResult then optionType else nodeType
                  RequiredEffects = []; MustNotWait = false; Body = { Nodes = List.ofSeq nodes; ResultNodeId = previous } }
            let snapshot =
                { Snapshot.empty (sid<SnapshotTag> "deep-snapshot") with
                    Types = [ { Id = typeId; DisplayName = "Node"; Exported = true; TypeParameters = []
                                Kind = SemanticTypeKind.Product [ { Id = fieldId; DisplayName = "next"; Type = optionType } ] } ]
                    Functions = [ create 66 false; create 127 true; create 128 true ] }
            let path = Path.Combine(directory, "deep.json")
            File.WriteAllText(path, SnapshotCodec.serialize snapshot |> unwrap)
            let requests = [ runName "deep66" [] None; runName "deep127" [] None; runName "deep128" [] None ]
            let replies = strictPair path requests
            Assert.Equal("product", (success replies[0]).GetProperty("actual").GetProperty("kind").GetString())
            Assert.Equal("option", (success replies[1]).GetProperty("actual").GetProperty("kind").GetString())
            Assert.Equal("RunInvalidResult", code replies[2]))

    [<Fact; Trait("Suite", "Extended")>]
    let ``native Zig host preserves multiline unsupported diagnostics through the checked emitter tool`` () =
        withDirectory (fun directory ->
            let bits = TypeRef.Primitive PrimitiveType.Bits32
            let nested = [ 1 .. 4 ] |> List.fold (fun item _ -> TypeRef.BuiltinGeneric(BuiltinGenericType.List, [ item ])) bits
            let parameterId = sid<ParameterTag> "unsupported-parameter"
            let nodeId = sid<GraphNodeTag> "unsupported-read"
            let fn: SemanticFunction =
                { Id = sid<FunctionTag> "unsupported-entry"; DisplayName = "unsupported"; Exported = true
                  TypeParameters = []; Parameters = [ { Id = parameterId; DisplayName = "value"; Type = nested } ]
                  ResultType = nested; RequiredEffects = []; MustNotWait = false
                  Body = { Nodes = [ { Id = nodeId; Kind = SemanticNodeKind.ParameterReference parameterId } ]; ResultNodeId = nodeId } }
            let snapshot = { Snapshot.empty (sid<SnapshotTag> "unsupported-snapshot") with Functions = [ fn ] }
            let path = Path.Combine(directory, "unsupported.json")
            File.WriteAllText(path, SnapshotCodec.serialize snapshot |> unwrap)
            let request = runName "unsupported" [ "[[[[1]]]]" ] None
            for environment in [ []; [ "NYRWVY_ZIG_TOOLS", "1" ]; [ "NYRWVY_ZIG_EMITTER_CHECK", "1" ] ] do
                let reply = (strictPairWithEnvironment environment path [ request ]).Head
                Assert.Equal("RunBackendUnsupported", code reply)
                use document = parseDocument reply
                Assert.Contains("BuiltinGeneric\n", document.RootElement.GetProperty("error").GetProperty("message").GetString()))
