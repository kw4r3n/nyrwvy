namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Only the four genuinely different module identities are excluded from behavioral evidence parity.
module ZigHostChangeVerificationParityTests =
    open ZigHostParityTests

    let private success reply =
        use document = parseDocument reply
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean(), reply)
        document.RootElement.GetProperty("result").Clone()

    let private errorCode reply =
        use document = parseDocument reply
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean(), reply)
        document.RootElement.GetProperty("error").GetProperty("code").GetString()

    let private scalar name = sprintf """{"kind":"primitive","primitive":"%s"}""" name
    let private generic name arguments = sprintf """{"kind":"builtinGeneric","generic":"%s","arguments":[%s]}""" name (String.concat "," arguments)
    let private integer, text = scalar "int64", scalar "string"
    let private parameter name = sprintf """{"kind":"parameter","name":"%s"}""" name
    let private literal n = sprintf """{"kind":"int64Literal","value":"%d"}""" n
    let private intValue n = sprintf """{"kind":"int64","value":"%d"}""" n
    let private stringValue value = sprintf """{"kind":"string","value":%s}""" (JsonSerializer.Serialize(value: string))
    let private bind first second = sprintf """{"kind":"bind","value":%s,"body":%s}""" first second
    let private lookup key = sprintf """{"kind":"capabilityInvoke","capabilityValue":%s,"capability":"Provider","operation":"lookup","arguments":[{"parameter":"key","value":%s}]}""" (parameter "provider") key
    let private parallelBody = sprintf """{"kind":"parallelAll","source":{"kind":"listConstruct","itemType":%s,"items":[%s,%s]},"worker":"job"}""" integer (literal 1) (literal 2)
    let private fake responses = sprintf """{"kind":"fakeCapability","capability":"Provider","operations":[{"operation":"lookup","responses":[%s]}]}""" (String.concat "," responses)
    let private returns value = sprintf """{"kind":"return","value":%s}""" (stringValue value)
    let private case name arguments expected =
        sprintf """{"label":"explicit condition","function":"%s","arguments":[%s]%s}""" name (String.concat "," arguments) (expected |> Option.map (fun value -> ",\"expectedCandidate\":" + value) |> Option.defaultValue "")
    let private replace send name body =
        send (sprintf """{"id":"replace","op":"simulateReplaceBodyByName","function":"%s","replacement":%s}""" name body)
        |> success |> fun value -> value.GetProperty("proposalId").GetString()
    let private lifecycle send op proposal = send (sprintf """{"id":"lifecycle","op":"%s","proposalId":"%s"}""" op proposal)

    let private fixture directory records =
        Directory.CreateDirectory directory |> ignore
        let json = Path.Combine(directory, "workspace.json")
        use session = AuthoringSession.createNew json |> unwrap
        let publish line =
            let proposal = AuthoringProtocol.executeLine session line |> success |> fun value -> value.GetProperty("proposalId").GetString()
            AuthoringProtocol.executeLine session (sprintf """{"id":"commit","op":"commit","proposalId":"%s"}""" proposal) |> success |> ignore
        let create name parameters result effects body =
            let ps = parameters |> List.map (fun (name, kind) -> sprintf """{"displayName":"%s","type":%s}""" name kind) |> String.concat ","
            publish (sprintf """{"id":"create","op":"simulateCreateFunctionByName","exported":true,"displayName":"%s","parameters":[%s],"resultType":%s,"requiredEffects":[%s],"body":%s}""" name ps result (effects |> List.map (fun (name: string) -> JsonSerializer.Serialize name) |> String.concat ",") body)
        publish (sprintf """{"id":"pair","op":"simulateCreateTypeByName","exported":true,"displayName":"Pair","typeKind":{"kind":"product","fields":[{"displayName":"left","type":%s},{"displayName":"right","type":%s}]}}""" integer text)
        for name, kind in [ "echo", integer; "text", text; "float", scalar "float64"; "texts", generic "list" [ text ]; "pair", """{"kind":"named","name":"Pair"}""" ] do
            create name [ "value", kind ] kind [] (parameter "value")
        create "caller" [] integer [] (sprintf """{"kind":"directCall","function":"echo","arguments":[{"parameter":"value","value":%s}]}""" (literal 42))
        publish """{"id":"effect","op":"simulateCreateEffect","exported":true,"displayName":"io"}"""
        publish (sprintf """{"id":"provider","op":"simulateCreateCapabilityByName","exported":true,"displayName":"Provider","operations":[{"displayName":"lookup","parameters":[{"displayName":"key","type":%s}],"resultType":%s,"effect":"io"}]}""" text text)
        let capability = """{"kind":"capability","name":"Provider"}"""
        create "query" [ "key", text; "provider", capability ] text [ "io" ] (lookup (parameter "key"))
        create "twoCalls" [ "provider", capability ] text [ "io" ] (bind (lookup """{"kind":"stringLiteral","value":"first"}""") (lookup """{"kind":"stringLiteral","value":"second"}"""))
        create "job" [ "item", integer ] (generic "result" [ integer; text ]) [] (sprintf """{"kind":"resultOk","errorType":%s,"value":%s}""" text (parameter "item"))
        create "fan" [] (generic "result" [ generic "list" [ integer ]; text ]) [] parallelBody
        publish """{"id":"generic","op":"simulateCreateFunctionByName","displayName":"generic","typeParameters":["T"],"parameters":[{"displayName":"value","type":{"kind":"typeParameter","name":"T"}}],"resultType":{"kind":"typeParameter","name":"T"},"requiredEffects":[],"body":{"kind":"parameter","name":"value"}}"""
        let declarations = session.Declarations()
        let path = if records then Path.Combine(directory, "workspace.nyrec") else json
        if records then File.WriteAllText(path, RecordCodec.ofJson(File.ReadAllText json))
        path, declarations

    let private artifactData path =
        let page = File.ReadAllText path
        let data = Regex.Match(page, "<script type=\"application/json\" id=\"nyrwvy-data\">(.*?)</script>", RegexOptions.Singleline)
        Assert.True(data.Success, path)
        data.Groups[1].Value

    let private zigExecutableIdentity = lazy (
        use executable = File.OpenRead(zigHost ())
        "zig-sha256:" + Convert.ToHexString(SHA256.HashData executable))

    let private normalize zig directory (json: string) =
        let node = JsonNode.Parse(json.Replace(directory, "<dir>") |> comparable, documentOptions = JsonDocumentOptions(MaxDepth = WireInput.requestMaxJsonDepth))
        let get (key: string) (node: JsonNode) = match node with :? JsonObject as value -> value[key] | _ -> null
        let evidence =
            let response = node |> get "result" |> get "evidence"
            if isNull response then get "verification" node else response
        if not (isNull evidence) then
            let identity = get "verifierIdentity" evidence
            let keys = [ "authoringModule"; "semanticsModule"; "executionModule"; "backendModule" ]
            let ids = keys |> List.map (fun (key: string) -> identity[key].GetValue<string>())
            if zig then
                for id in ids do Assert.Matches("^zig-sha256:[0-9A-F]{64}$", id)
                Assert.Equal(1, ids |> List.distinct |> List.length)
                for id in ids do Assert.Equal(zigExecutableIdentity.Value, id)
            else
                for id in ids do
                    let valid, value = Guid.TryParse id
                    Assert.True(valid, id)
                    Assert.NotEqual(Guid.Empty, value)
            for key in keys do identity[key] <- JsonValue.Create("<actual-module-identity>")
        node.ToJsonString()

    type private During = (Process -> Task<string> -> unit) -> string -> string

    /// Same established host launch and seed policy; the evidence identity is the sole extra normalization.
    let private pair seed (source: string) flags (transcript: string -> (string -> string) -> During -> unit) =
        withDirectory (fun directory ->
            zigHost () |> ignore
            let runHost zig name =
                let target = Path.Combine(directory, name)
                Directory.CreateDirectory target |> ignore
                for file in Directory.GetFiles(Path.GetDirectoryName source) do File.Copy(file, Path.Combine(target, Path.GetFileName file))
                let path = Path.Combine(target, Path.GetFileName source)
                use host = startWithEnvironment [ "NYRWVY_TEST_ID_SEED", seed ] zig ([ "open"; "--snapshot"; path ] @ flags)
                let errors = host.StandardError.ReadToEndAsync()
                let replies = ResizeArray<string * string>()
                let artifacts = ResizeArray<string>()
                let exchange during request =
                    host.StandardInput.WriteLine(request: string)
                    host.StandardInput.Flush()
                    let reading = host.StandardOutput.ReadLineAsync()
                    during |> Option.iter (fun action -> action host reading)
                    if not (reading.Wait silence) then
                        host.Kill true
                        failwithf "Verification host exceeded its deadline: %s" request
                    let reply = reading.Result
                    Assert.False(isNull reply, request)
                    replies.Add(request.Replace(target, "<dir>"), normalize zig target reply)
                    use document = parseDocument reply
                    match document.RootElement.TryGetProperty "result" with
                    | true, result ->
                        let saved =
                            match result.TryGetProperty "reviewPath" with
                            | true, value -> Some(value.GetString())
                            | _ -> match result.TryGetProperty "path" with true, value when result.TryGetProperty("kind") |> fst -> Some(value.GetString()) | _ -> None
                        saved |> Option.iter (fun output -> artifacts.Add(normalize zig target (artifactData output)))
                    | _ -> ()
                    reply
                try transcript path (exchange None) (fun action -> exchange (Some action))
                finally
                    host.StandardInput.Close()
                    if not (host.WaitForExit(int silence.TotalMilliseconds)) then host.Kill true
                    host.WaitForExit()
                Assert.Equal(0, host.ExitCode)
                Assert.Equal(seedWarning, errors.Result.Trim())
                List.ofSeq replies, List.ofSeq artifacts, File.ReadAllBytes path
            let expected, expectedArtifacts, expectedSource = runHost false "fsharp"
            let actual, actualArtifacts, actualSource = runHost true "zig"
            Assert.Equal<(string * string) list>(expected, actual)
            Assert.Equal<string list>(expectedArtifacts, actualArtifacts)
            Assert.Equal<byte>(expectedSource, actualSource))

    let private verifyOn op path send proposal cases output =
        let before = File.ReadAllBytes path
        let line = sprintf """{"id":"verify","op":"%s","proposalId":"%s","cases":%s,"output":"%s"}""" op proposal cases output
        let result = send line |> success
        Assert.Equal<byte>(before, File.ReadAllBytes path)
        let evidence = result.GetProperty("evidence")
        Assert.Equal(cases, evidence.GetProperty("caseRequest").GetString())
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes cases)), evidence.GetProperty("casesDigest").GetString())
        use page = parseDocument (artifactData (result.GetProperty("reviewPath").GetString()))
        Assert.Equal(evidence.GetRawText(), page.RootElement.GetProperty("verification").GetRawText())
        evidence.Clone()

    let private verify = verifyOn "verifyChangeByName"

    let private statuses (evidence: JsonElement) = evidence.GetProperty("cases").EnumerateArray() |> Seq.map (fun item -> item.GetProperty("status").GetString()) |> Seq.toList
    let private fnId (declarations: AuthoringDeclarations) name = declarations.Functions |> List.find (fun fn -> fn.DisplayName = name) |> fun fn -> StableId.value fn.Id

    [<Theory; Trait("Suite", "Extended"); InlineData(false); InlineData(true)>]
    let ``verification preserves raw cases caller closure context names and saved receipts on JSON and records`` records =
        withDirectory (fun directory ->
            let source, declarations = fixture (Path.Combine(directory, "input")) records
            pair "verification-context" source [ "--read-only" ] (fun path send _ ->
                let proposal = replace send "echo" (sprintf """{"kind":"int64WrappingAdd","left":%s,"right":%s}""" (parameter "value") (literal 1))
                Assert.Equal("ReadOnlySession", lifecycle send "commit" proposal |> errorCode)
                lifecycle send "viewProposal" proposal |> success |> ignore
                let cases = """[ { "label" : "\u65E5\u672C condition", "function":"echo", "arguments":[{"kind":"int64","value":"42"}] }, {"label":"explicit intent","function":"echo","arguments":[{"kind":"int64","value":"42"}],"expectedCandidate":{"kind":"int64","value":"43"}}, {"label":"untouched","function":"text","arguments":[{"kind":"string","value":"\u65e5\u672c"}]}, {"label":"typed scalar list","function":"texts","arguments":[{"kind":"list","itemType":{"kind":"primitive","primitive":"string"},"items":[{"kind":"string","value":"x"}]}]} ]"""
                let evidence = verify path send proposal cases "evidence.html"
                Assert.Equal<string list>([ "counterexample"; "intended-change-tested"; "tested-agreement"; "tested-agreement" ], statuses evidence)
                Assert.Equal<string list>([ fnId declarations "echo"; fnId declarations "caller" ] |> List.sort, evidence.GetProperty("affectedFunctionIds").EnumerateArray() |> Seq.map (fun item -> item.GetString()) |> Seq.toList)
                Assert.True(evidence.GetProperty("cases").[3].GetProperty("arguments").[0].TryGetProperty("itemType") |> fst)
                let batch = """{"id":"batch","op":"simulateBatchByName","steps":[{"kind":"operations","operations":[{"kind":"renameType","type":"Pair","displayName":"RenamedPair"}]},{"kind":"replaceBody","function":"pair","replacement":{"kind":"parameter","name":"value"}}]}"""
                let renamed = send batch |> success |> fun value -> value.GetProperty("proposalId").GetString()
                let product = """{"kind":"product","type":"Pair","fields":[{"field":"right","value":{"kind":"string","value":"same"}},{"field":"left","value":{"kind":"int64","value":"3"}}]}"""
                let expected = """{"kind":"product","type":"RenamedPair","fields":[{"field":"left","value":{"kind":"int64","value":"3"}},{"field":"right","value":{"kind":"string","value":"same"}}]}"""
                let context = verify path send renamed ("[" + case "pair" [ product ] (Some expected) + "]") "renamed.html"
                let item = context.GetProperty("cases").[0]
                Assert.Equal("intended-change-tested", item.GetProperty("status").GetString())
                Assert.Equal("Pair", item.GetProperty("arguments").[0].GetProperty("type").GetString())
                Assert.Equal("Pair", item.GetProperty("baseline").GetProperty("actual").GetProperty("type").GetString())
                Assert.Equal("RenamedPair", item.GetProperty("candidate").GetProperty("actual").GetProperty("type").GetString())
                Assert.Equal("RenamedPair", item.GetProperty("expectedCandidate").GetProperty("type").GetString())
                Assert.Equal<string list>([ "right"; "left" ], item.GetProperty("arguments").[0].GetProperty("fields").EnumerateArray() |> Seq.map (fun field -> field.GetProperty("field").GetString()) |> Seq.toList)))

    [<Fact; Trait("Suite", "Extended")>]
    let ``verification compares ordered fake calls and keeps concurrency and prior failures unknown`` () =
        withDirectory (fun directory ->
            let source, declarations = fixture (Path.Combine(directory, "input")) false
            pair "verification-observations" source [ "--read-only"; "--allow-host-sources" ] (fun path send _ ->
                let exhausted = replace send "query" (bind (lookup (parameter "key")) (lookup (parameter "key")))
                let e = verify path send exhausted ("[" + case "query" [ stringValue "a"; fake [ returns "ok" ] ] None + "]") "exhausted.html"
                Assert.Equal<string list>([ "counterexample" ], statuses e)
                let failure = e.GetProperty("cases").[0].GetProperty("candidate").GetProperty("error")
                Assert.Equal("FakeResponsesExhaustedException", failure.GetProperty("exceptionType").GetString())
                Assert.Equal(2, failure.GetProperty("calls").GetArrayLength())
                Assert.Contains(failure.GetProperty("frames").EnumerateArray(), fun frame -> frame.GetProperty("kind").GetString() = "function" && frame.GetProperty("functionId").GetString() = fnId declarations "query")
                let fresh = replace send "query" """{"kind":"stringLiteral","value":"new"}"""
                let prior = verify path send fresh ("[" + case "query" [ stringValue "a"; fake [] ] None + "]") "prior.html"
                Assert.Equal<string list>([ "unknown" ], statuses prior)
                Assert.True(prior.GetProperty("cases").[0].GetProperty("candidate").TryGetProperty("actual") |> fst)
                let reversed = replace send "twoCalls" (bind (lookup """{"kind":"stringLiteral","value":"second"}""") (lookup """{"kind":"stringLiteral","value":"first"}"""))
                let ordered = verify path send reversed ("[" + case "twoCalls" [ fake [ returns "same"; returns "same" ] ] (Some(stringValue "same")) + "]") "ordered.html"
                Assert.Equal<string list>([ "counterexample" ], statuses ordered)
                let calls = ordered.GetProperty("cases").[0]
                Assert.Equal(calls.GetProperty("baseline").GetProperty("actual").GetRawText(), calls.GetProperty("candidate").GetProperty("actual").GetRawText())
                let float = replace send "float" (sprintf """{"kind":"float64Negate","value":%s}""" (parameter "value"))
                let floats = verify path send float ("[" + case "float" [ """{"kind":"float64","value":"NaN"}""" ] None + "," + case "float" [ """{"kind":"float64","value":0}""" ] None + "]") "float.html"
                Assert.Equal<string list>([ "tested-agreement"; "counterexample" ], statuses floats)
                let fan = replace send "fan" parallelBody
                let concurrent = verify path send fan ("[" + case "fan" [] None + "]") "concurrent.html"
                Assert.Equal<string list>([ "unknown" ], statuses concurrent)
                for side in [ "baseline"; "candidate" ] do Assert.True(concurrent.GetProperty("cases").[0].GetProperty(side).TryGetProperty("actual") |> fst)
                let host = """{"kind":"hostCapability","capability":"Provider","implementation":"NeverCompiled"}"""
                let refusal = verify path send fresh ("[" + case "query" [ stringValue "a"; host ] None + "]") "host.html"
                for side in [ "baseline"; "candidate" ] do Assert.Equal("VerificationHostUnsupported", refusal.GetProperty("cases").[0].GetProperty(side).GetProperty("error").GetProperty("code").GetString())
                let generic = verify path send fresh ("[" + case "generic" [ stringValue "unsupported" ] None + "]") "generic.html"
                Assert.Equal<string list>([ "unknown" ], statuses generic)
                for side in [ "baseline"; "candidate" ] do
                    let error = generic.GetProperty("cases").[0].GetProperty(side).GetProperty("error")
                    Assert.Equal("RunGenericEntryUnsupported", error.GetProperty("code").GetString())
                    Assert.False(error.TryGetProperty("details") |> fst)))

    [<Fact; Trait("Suite", "Extended")>]
    let ``verification rejects malformed forged operations stale and discarded requests without cached successes`` () =
        withDirectory (fun directory ->
            let source, declarations = fixture (Path.Combine(directory, "input")) false
            pair "verification-lifecycle" source [] (fun path send _ ->
                let cases = "[" + case "echo" [ intValue 42 ] None + "]"
                let request proposal value = sprintf """{"id":"bad","op":"verifyChangeByName","proposalId":"%s","cases":%s}""" proposal value
                for values in [ "[]"; "[" + String.concat "," (List.replicate 33 (case "echo" [ intValue 42 ] None)) + "]"; """[{"label":"\u3000","function":"echo","arguments":[]}]"""; """[{"label":" ","function":"echo","arguments":[]}]""" ] do
                    Assert.Equal("DependencyFailure", request "missing" values |> send |> errorCode)
                for values in [ "null"; """[{"label":"plain","function":"echo","arguments":[42]}]"""; """[{"label":"duplicate","label":"again","function":"echo","arguments":[]}]"""; """[{"label":"extra","function":"echo","arguments":[],"status":"tested-agreement"}]""" ] do
                    Assert.Equal("ProtocolError", request "missing" values |> send |> errorCode)
                let forged = sprintf """{"id":"forged","op":"verifyChangeByName","proposalId":"missing","cases":%s,"evidence":{"status":"tested-agreement"}}""" cases
                Assert.Equal("ProtocolError", send forged |> errorCode)
                let rejected = replace send "echo" (literal 1)
                let malformed = verify path send rejected ("[" + case "echo" [ """{"kind":"bool","value":true}""" ] None + "]") "invalid-input.html"
                Assert.Equal<string list>([ "unknown" ], statuses malformed)
                for side in [ "baseline"; "candidate" ] do
                    let error = malformed.GetProperty("cases").[0].GetProperty(side).GetProperty("error")
                    Assert.Equal("RunArgumentTypeMismatch", error.GetProperty("code").GetString())
                    Assert.Equal("arguments", error.GetProperty("details").GetProperty("path").[0].GetString())
                let exactCase = sprintf """[{"label":"exact ID","functionId":"%s","arguments":[%s],"expectedCandidate":%s}]""" (fnId declarations "echo") (intValue 42) (intValue 1)
                let exact = verifyOn "verifyChange" path send rejected exactCase "exact.html"
                Assert.Equal<string list>([ "intended-change-tested" ], statuses exact)
                let missing = verifyOn "verifyChange" path send rejected """[{"label":"missing exact ID","functionId":"verification-missing","arguments":[]}]""" "missing-entry.html"
                Assert.Equal<string list>([ "unknown" ], statuses missing)
                verify path send rejected cases "before-reject.html" |> ignore
                lifecycle send "reject" rejected |> success |> ignore
                Assert.Equal("ProposalNotFound", request rejected cases |> send |> errorCode)
                let operations = send """{"id":"operations","op":"simulateOperationsByName","operations":[{"kind":"renameFunction","function":"text","displayName":"renamedText"}]}""" |> success |> fun result -> result.GetProperty("proposalId").GetString()
                Assert.Equal("DependencyFailure", request operations cases |> send |> errorCode)
                lifecycle send "reject" operations |> success |> ignore
                let committed = replace send "echo" (literal 2)
                let stale = replace send "text" (parameter "value")
                let escaped = sprintf """{"id":"escape","op":"verifyChangeByName","proposalId":"%s","cases":%s,"output":"../outside.html"}""" committed cases
                Assert.Equal("ViewWriteFailed", send escaped |> errorCode)
                Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName path, "..", "outside.html")))
                lifecycle send "commit" committed |> success |> ignore
                Assert.Equal("ProposalNotFound", request committed cases |> send |> errorCode)
                Assert.Equal("ExternalModification", request stale cases |> send |> errorCode)
                send """{"id":"run","op":"runPureByName","backend":"csharp","function":"echo","arguments":[42],"expected":2}""" |> success |> fun result -> Assert.True(result.GetProperty("matches").GetBoolean())))

    [<Fact; Trait("Suite", "Extended")>]
    let ``verification detects a source edit after execution starts and preserves the external bytes`` () =
        // Edits the source while the F# host is still running; a replayed run has already answered.
        if OperatingSystem.IsLinux() && hostReference = "live" then
            withDirectory (fun directory ->
                let source, _ = fixture (Path.Combine(directory, "input")) false
                pair "verification-external-edit" source [] (fun path send during ->
                    let recursive = sprintf """{"kind":"directCall","function":"echo","arguments":[{"parameter":"value","value":%s}]}""" (parameter "value")
                    let proposal = replace send "echo" recursive
                    let original = File.ReadAllBytes path
                    let edited = Array.append original [| byte '\n' |]
                    let change (host: Process) (reading: Task<string>) =
                        // Observe a child of this host after the initial persisted-source check, without a delay guess.
                        let started () =
                            reading.IsCompleted ||
                            (try Directory.GetDirectories(sprintf "/proc/%d/task" host.Id)
                                 |> Array.exists (fun thread -> not (String.IsNullOrWhiteSpace(File.ReadAllText(Path.Combine(thread, "children")))))
                             with :? IOException | :? UnauthorizedAccessException -> false)
                        Assert.True(SpinWait.SpinUntil(Func<bool>(started), silence), "No managed child started within the response budget.")
                        Assert.False(reading.IsCompleted, "The recursive candidate must still be running when the source is changed.")
                        File.WriteAllBytes(path, edited)
                    let request = sprintf """{"id":"edit","op":"verifyChangeByName","proposalId":"%s","cases":[%s]}""" proposal (case "echo" [ intValue 42 ] None)
                    Assert.Equal("ExternalModification", during change request |> errorCode)
                    Assert.Equal<byte>(edited, File.ReadAllBytes path)
                    Assert.Equal("SessionInvalidated", send """{"id":"after","op":"runPureByName","backend":"csharp","function":"echo","arguments":[42]}""" |> errorCode)))
