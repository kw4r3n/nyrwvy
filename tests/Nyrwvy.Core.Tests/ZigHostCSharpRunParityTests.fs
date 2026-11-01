namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// C# execution stays byte-compatible while graph decisions and worker ownership live in Zig.
module ZigHostCSharpRunParityTests =
    open ZigHostParityTests

    let private success reply =
        use document = parseDocument reply
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean(), reply)
        document.RootElement.GetProperty("result").Clone()

    let private error reply =
        use document = parseDocument reply
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean(), reply)
        document.RootElement.GetProperty("error").Clone()

    let private code reply = (error reply).GetProperty("code").GetString()

    let private scalar name = $"""{{"kind":"primitive","primitive":"{name}"}}"""
    let private named name = $"""{{"kind":"named","name":"{name}"}}"""
    let private generic name arguments = $"""{{"kind":"builtinGeneric","generic":"{name}","arguments":[{String.concat "," arguments}]}}"""
    let private text = scalar "string"
    let private integer = scalar "int64"
    let private parameter name = $"""{{"kind":"parameter","name":"{name}"}}"""
    let private stringValue value = JsonSerializer.Serialize(value: string)
    let private runName name arguments expected =
        $"""{{"id":"run","op":"runPureByName","function":"{name}","backend":"csharp","arguments":[{String.concat "," arguments}]{expected |> Option.map (fun value -> ",\"expected\":" + value) |> Option.defaultValue ""}}}"""

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
              "bytes", scalar "bytes"; "bits", scalar "bits32"; "unit", scalar "unit"
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
        create "choose" [ "flag", scalar "bool"; "left", integer; "right", integer ] integer []
            $"""{{"kind":"conditional","condition":{parameter "flag"},"whenTrue":{parameter "left"},"whenFalse":{parameter "right"}}}"""
        create "optionBranch" [ "value", generic "option" [ integer ] ] integer []
            $"""{{"kind":"matchOption","input":{parameter "value"},"someBody":{{"kind":"int64Literal","value":"7"}},"noneBody":{{"kind":"int64Literal","value":"0"}}}}"""
        publish $"""{{"id":"recursive","op":"simulateCreateFunctionsByName","functions":[{{"displayName":"swapLoop","exported":true,"parameters":[{{"displayName":"a","type":{integer}}},{{"displayName":"b","type":{integer}}},{{"displayName":"count","type":{integer}}}],"resultType":{integer},"requiredEffects":[],"body":{{"kind":"conditional","condition":{{"kind":"int64LessThan","left":{parameter "count"},"right":{{"kind":"int64Literal","value":"1"}}}},"whenTrue":{{"kind":"int64WrappingSubtract","left":{parameter "a"},"right":{parameter "b"}}},"whenFalse":{{"kind":"directCall","function":"swapLoop","arguments":[{{"parameter":"a","value":{parameter "b"}}},{{"parameter":"b","value":{parameter "a"}}},{{"parameter":"count","value":{{"kind":"int64WrappingSubtract","left":{parameter "count"},"right":{{"kind":"int64Literal","value":"1"}}}}}}]}}}}}}]}}"""
        publish $"""{{"id":"recursive","op":"simulateCreateFunctionsByName","functions":[{{"displayName":"deep","exported":true,"parameters":[],"resultType":{integer},"requiredEffects":[],"body":{{"kind":"int64WrappingAdd","left":{{"kind":"directCall","function":"deep","arguments":[]}},"right":{{"kind":"int64Literal","value":"1"}}}}}}]}}"""
        File.WriteAllText(path + ".backends.json", """{"targets":["csharp"]}""")
        path, session.Declarations()

    let private pair textValue count = $"""{{"kind":"product","type":"Pair","fields":[{{"field":"text","value":{{"kind":"string","value":{stringValue textValue}}}}},{{"field":"count","value":{{"kind":"int64","value":"{count}"}}}}]}}"""
    let private found value = $"""{{"kind":"union","type":"Answer","variant":"Found","fields":[{{"field":"text","value":{{"kind":"string","value":{stringValue value}}}}}]}}"""
    let private missing = """{"kind":"union","type":"Answer","variant":"Missing","fields":[]}"""
    let private provider script = $"""{{"kind":"fakeCapability","capability":"Provider","operations":[{{"operation":"lookup",{script}}}]}}"""
    let private responses responses = provider $""""responses":[{String.concat "," responses}]"""
    let private returns value = $"""{{"kind":"return","value":{value}}}"""

    let private withHost environment zig path flags action =
        use host = startWithEnvironment environment zig ([ "open"; "--snapshot"; path ] @ flags)
        let errors = host.StandardError.ReadToEndAsync()
        let send request =
            host.StandardInput.WriteLine(request: string)
            host.StandardInput.Flush()
            let reading = host.StandardOutput.ReadLineAsync()
            if not (reading.Wait silence) then
                host.Kill true
                failwithf "C# host exceeded its response deadline: %s" request
            let reply = reading.Result
            if isNull reply then failwithf "C# host stopped before answering %s" request
            reply
        try action host send
        finally
            host.StandardInput.Close()
            if not (host.WaitForExit(int silence.TotalMilliseconds)) then host.Kill true
            host.WaitForExit()
            Assert.Equal(0, host.ExitCode)
            let seeded=environment |> List.exists(fun (name,_) -> name="NYRWVY_TEST_ID_SEED")
            Assert.Equal((if seeded then seedWarning else ""),errors.Result.Trim())

    let private strictPair path flags requests =
        zigHost () |> ignore
        let runHost zig =
            let exitCode, errors, replies = run zig ([ "open"; "--snapshot"; path; "--read-only" ] @ flags) requests
            Assert.Equal(0,exitCode)
            Assert.Equal("",errors)
            Assert.Equal(requests.Length,replies.Length)
            replies
        let expected,actual=runHost false,runHost true
        for request,(wanted,found) in List.zip requests (List.zip expected actual) do
            use document=parseDocument request
            // status.build identifies the host executable; every other response byte is contractual.
            let status=document.RootElement.GetProperty("op").GetString()="status"
            Assert.True(((if status then comparable wanted else wanted)=(if status then comparable found else found)),$"{request}\n F#: {wanted}\nZig: {found}")
        actual

    let private matches reply = Assert.True((success reply).GetProperty("matches").GetBoolean(),reply)

    let private publish send request =
        let proposal=(send request |> success).GetProperty("proposalId").GetString()
        send $"""{{"id":"commit","op":"commit","proposalId":"{proposal}"}}""" |> success |> ignore

    let private exactName prefix (id:string) = prefix + Convert.ToHexString(Text.Encoding.UTF8.GetBytes id)

    // Only direct children of this test's host qualify; unrelated user dotnet processes are ignored.
    let private workerPid (host:Process) =
        Directory.GetDirectories("/proc")
        |> Array.tryPick(fun path ->
            match Int32.TryParse(Path.GetFileName path) with
            | true,pid ->
                try
                    let stat=File.ReadAllText(Path.Combine(path,"stat"))
                    let parent=stat.Substring(stat.LastIndexOf(')')+2).Split(' ')[1] |> int
                    let cmd=File.ReadAllText(Path.Combine(path,"cmdline"))
                    if parent=host.Id && (cmd.Contains("nyrwvy-csharp-worker",StringComparison.Ordinal) || cmd.Contains("nyrwvy-pure-run-worker",StringComparison.Ordinal)) then Some pid else None
                with :? IOException | :? UnauthorizedAccessException -> None
            | _ -> None)
        |> Option.defaultWith(fun () -> failwith "The host did not retain its C# runner worker.")

    [<Fact; Trait("Suite","Extended")>]
    let ``CSharp JSONL preserves typed plain generic recursive values fakes errors and public emitted control flow`` () =
        withDirectory(fun directory ->
            let path,declarations=fixture directory
            let chainEnd="""{"kind":"union","type":"Chain","variant":"End","fields":[]}"""
            let chain = $"""{{"kind":"union","type":"Chain","variant":"Link","fields":[{{"field":"value","value":{{"kind":"int64","value":"3"}}}},{{"field":"next","value":{chainEnd}}}]}}"""
            let values =
                [ "echo",stringValue "日本 😀\u0000<&"; "integer","\"-9223372036854775808\""
                  "integer","9223372036854775807"; "boolean","true"; "unit","null"
                  "bytes","""{"kind":"bytes","hex":"00ff80"}"""; "bits","""{"kind":"bits32","value":4294967295}"""
                  "pair",pair "a😀" -4; "answer",found "ok"; "chain",chain
                  "box","""{"value":"generic 日本"}"""
                  "nested","""[{"text":"one","count":"1"},null]"""
                  "mapping","""[["b","2"],["a","1"]]"""; "set","[3,1,2]"
                  "result","""{"ok":"42"}"""
                  for number in [ "-0";"1e-5";"5e-324";"\"NaN\"";"\"Infinity\"";"\"-Infinity\"" ] do "float",number ]
            let echo=declarations.Functions |> List.find(fun fn -> fn.DisplayName="echo")
            let exact = $"""{{"id":"exact","op":"runPure","backend":"csharp","functionId":"{StableId.value echo.Id}","arguments":[{{"kind":"string","value":"exact"}}],"expected":{{"kind":"string","value":"exact"}}}}"""
            let requests=values |> List.map(fun (name,arg) -> runName name [arg] (Some arg))
            let queued=responses [returns(found "first");returns(found "unused")]
            let keyed=provider $""""cases":[{{"arguments":[{{"parameter":"key","value":{{"kind":"string","value":"a"}}}}],"responses":[{returns missing}]}}]"""
            let fakeGood=runName "query" ["\"a\"";queued] (Some(found "first"))
            let profile = $"""{{"kind":"externalImplementations","operationIds":[{declarations.Capabilities |> List.collect(fun cap -> cap.Operations |> List.map(fun op -> stringValue(StableId.value op.Id))) |> String.concat ","}],"concurrentCapabilityIds":[]}}"""
            let emission = $"""{{"id":"emission","op":"emitCSharp","profile":{profile}}}"""
            let positive=requests @ [exact;runName "constant" [] (Some "7");runName "choose" ["true";"3";"8"] (Some "3");runName "choose" ["false";"3";"8"] (Some "8");runName "optionBranch" ["null"] (Some "0");runName "optionBranch" ["4"] (Some "7");runName "swapLoop" ["1";"2";"3"] (Some "1");fakeGood;fakeGood;runName "query" ["\"a\"";keyed] (Some missing)]
            let mismatch=runName "pair" [pair "actual" 1] (Some(pair "wanted" 1))
            let failures=
                [ runName "query" ["\"missing\"";keyed] None,"RunBindingNoMatch"
                  runName "query" ["\"a\"";responses []] None,"RunBindingExhausted"
                  runName "query" ["\"a\"";responses ["""{"kind":"throw","message":"down 日本"}"""]] None,"RunRuntimeFailure"
                  runName "echo" [] None,"RunArgumentCountMismatch"
                  runName "echo" ["1"] None,"ProtocolError"
                  runName "echo" ["""{"kind":"int64","value":"1"}"""] None,"RunArgumentTypeMismatch"
                  runName "echo" ["\"ok\""] (Some """{"kind":"bool","value":true}"""),"RunExpectedTypeMismatch" ]
            let all=positive @ [mismatch;emission] @ (failures |> List.collect(fun (request,_) -> [request;fakeGood]))
            let replies=strictPair path [] all
            replies |> List.take positive.Length |> List.iter matches
            let mismatchResult=success replies[positive.Length]
            Assert.False(mismatchResult.GetProperty("matches").GetBoolean())
            Assert.Equal(1,mismatchResult.GetProperty("mismatch").GetProperty("path").GetArrayLength())
            let source=(success replies[positive.Length+1]).GetProperty("source").GetString()
            Assert.Contains("goto L_",source)
            Assert.Contains("jt_",source)
            for index,(_,expectedCode) in failures |> List.indexed do
                let failure=replies[positive.Length+2+index*2]
                Assert.Equal(expectedCode,code failure)
                if index<3 then
                    Assert.NotEmpty((error failure).GetProperty("frames").EnumerateArray())
                    Assert.Single((error failure).GetProperty("calls").EnumerateArray()) |> ignore
                matches replies[positive.Length+3+index*2]
            let call=(success replies[positive.Length-1]).GetProperty("calls").[0]
            Assert.Equal("provider",call.GetProperty("parameter").GetString())
            Assert.Equal("lookup",call.GetProperty("operation").GetString())
            Assert.Equal("key",call.GetProperty("arguments").[0].GetProperty("parameter").GetString()))

    [<Fact; Trait("Suite","Extended")>]
    let ``CSharp host sources require permission and keep statics isolated while changed source invalidates its build`` () =
        withDirectory(fun directory ->
            let path,declarations=fixture directory
            let capability=declarations.Capabilities |> List.find(fun cap -> cap.DisplayName="Provider")
            let operation=capability.Operations |> List.exactlyOne
            let answer=declarations.Types |> List.find(fun typ -> typ.DisplayName="Answer")
            let foundVariant=
                match answer.Kind with
                | SemanticTypeKind.Union variants -> variants |> List.find(fun variant -> variant.DisplayName="Found")
                | _ -> failwith "The Answer fixture must be a union."
            let className=exactName "NyC_" (StableId.value capability.Id)
            let methodName=exactName "Op_" (StableId.value operation.Id)
            let resultName=exactName "NyT_" (StableId.value answer.Id)
            let variantName=exactName "NyV_" (StableId.value foundVariant.Id)
            let sourcePath=Path.Combine(directory,"Host.cs")
            let source prefix =
                $"""using Nyrwvy.Generated;
public sealed class Host : {className}
{{
    private static int count;
    public {resultName} {methodName}(NyStr key) => new {variantName}("{prefix}" + (string)key + ":" + (++count));
}}
public sealed class Broken : {className}
{{
    public {resultName} {methodName}(NyStr key) => throw new System.InvalidOperationException("host offline 日本");
}}
"""
            File.WriteAllText(sourcePath,source "host:")
            let host implementation = $"""{{"kind":"hostCapability","capability":"Provider","implementation":"{implementation}"}}"""
            let request implementation expected sources =
                let line=runName "query" ["\"a\"";host implementation] expected
                line.Insert(line.Length-1,$""", "hostSources":[{String.concat "," (sources |> List.map stringValue)}]""")
            let good=request "Host" (Some(found "host:a:1")) ["Host.cs"]
            Assert.Equal("RunHostSourcesDisabled",code(strictPair path [] [good] |> List.exactlyOne))
            let requests=[good;good;request "Broken" None ["Host.cs"];request "Host" None [];request "Host" None ["Missing.cs"];good]
            let before=File.ReadAllBytes path
            let replies=strictPair path ["--allow-host-sources"] requests
            for index in [0;1;5] do
                matches replies[index]
                Assert.Single((success replies[index]).GetProperty("calls").EnumerateArray()) |> ignore
            Assert.Equal("RunRuntimeFailure",code replies[2])
            Assert.Equal("host offline 日本",(error replies[2]).GetProperty("message").GetString())
            Assert.NotEmpty((error replies[2]).GetProperty("frames").EnumerateArray())
            Assert.Equal("RunHostSourceInvalid",code replies[3])
            Assert.Equal("RunHostSourceInvalid",code replies[4])
            Assert.Equal<byte[]>(before,File.ReadAllBytes path)
            // Both hosts read the same path twice while its C# content changes inside their session.
            for zig in [false;true] do
                File.WriteAllText(sourcePath,source "host:")
                withHost [] zig path ["--allow-host-sources"] (fun _ send ->
                    send good |> matches
                    File.WriteAllText(sourcePath,source "changed:")
                    send (request "Host" (Some(found "changed:a:1")) ["Host.cs"]) |> matches))

    [<Fact; Trait("Suite","Extended")>]
    let ``CSharp sessions reuse cached builds reset the worker after sixty four runs and recover from timeout and stack overflow`` () =
        withDirectory(fun directory ->
            zigHost () |> ignore
            let sourceDirectory=Path.Combine(directory,"source")
            Directory.CreateDirectory sourceDirectory |> ignore
            let source,_=fixture sourceDirectory
            let trace=Path.Combine(directory,"compiler-calls")
            let wrapperDirectory=Path.Combine(directory,"bin")
            Directory.CreateDirectory wrapperDirectory |> ignore
            let environment =
                if OperatingSystem.IsLinux() then
                    let runtime=DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory())
                    let dotnet=Path.Combine(runtime.Parent.Parent.Parent.FullName,"dotnet")
                    Assert.True(File.Exists dotnet,dotnet)
                    let quote (value:string)="'"+value.Replace("'","'\\''")+"'"
                    let wrapper=Path.Combine(wrapperDirectory,"dotnet")
                    File.WriteAllText(wrapper,$"#!/bin/sh\nif [ \"$2\" = compile ]; then printf 'compile\\n' >> {quote trace}; fi\nexec {quote dotnet} \"$@\"\n")
                    File.SetUnixFileMode(wrapper,UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
                    ["PATH",wrapperDirectory+string Path.PathSeparator+Environment.GetEnvironmentVariable("PATH")]
                else []
            let compileCount ()=if File.Exists trace then File.ReadAllLines(trace).Length else 0
            let transcripts=ResizeArray<string list>()
            let paths=ResizeArray<string>()
            for zig in [false;true] do
                let target=Path.Combine(directory,if zig then "zig" else "fsharp")
                Directory.CreateDirectory target |> ignore
                let path=Path.Combine(target,Path.GetFileName source)
                File.Copy(source,path)
                File.Copy(source+".backends.json",path+".backends.json")
                paths.Add path
                let replies=ResizeArray<string>()
                withHost (("NYRWVY_TEST_ID_SEED","step7-csharp-worker")::environment) zig path [] (fun host rawSend ->
                    let send request =
                        let reply=rawSend request
                        replies.Add(reply.Replace(target,"<dir>"))
                        reply
                    // A replayed F# host has no worker process; its runs are checked by their replies.
                    let tracked=OperatingSystem.IsLinux() && (zig || hostReference="live")
                    let pid ()=if tracked then Some(workerPid host) else None
                    let echoed value=runName "echo" [stringValue value] (Some(stringValue value))
                    send (echoed "cold") |> matches
                    let first=pid ()
                    let coldCompiles=compileCount ()
                    send (echoed "warm") |> matches
                    if zig && OperatingSystem.IsLinux() then
                        Assert.True(coldCompiles>=2,"The native host did not compile a runner and its worker.")
                        Assert.Equal(coldCompiles,compileCount ())
                    Assert.Equal(first,pid ())
                    for index in 3..64 do send (echoed(string index)) |> matches
                    Assert.Equal(first,pid ())
                    send (echoed "replacement") |> matches
                    if tracked then Assert.NotEqual(first,pid ())
                    if zig && OperatingSystem.IsLinux() then Assert.Equal(coldCompiles,compileCount ())
                    publish send """{"id":"rename","op":"simulateOperationsByName","operations":[{"kind":"renameFunction","function":"echo","displayName":"renamed"}]}"""
                    send (runName "renamed" ["\"after rename\""] (Some "\"after rename\"")) |> matches
                    if zig && OperatingSystem.IsLinux() then Assert.Equal(coldCompiles,compileCount ())
                    publish send """{"id":"body","op":"simulateReplaceBodyByName","function":"renamed","replacement":{"kind":"stringConcat","left":{"kind":"parameter","name":"value"},"right":{"kind":"stringLiteral","value":"!"}}}"""
                    let recovered ()=send (runName "renamed" ["\"changed\""] (Some "\"changed!\"")) |> matches
                    recovered ()
                    if zig && OperatingSystem.IsLinux() then Assert.Equal(coldCompiles+1,compileCount ())
                    let beforeTimeout=pid ()
                    Assert.Equal("RunTimeout",code(send(runName "spin" [] None)))
                    recovered ()
                    if tracked then Assert.NotEqual(beforeTimeout,pid ())
                    let beforeCrash=pid ()
                    let crash=send(runName "deep" [] None)
                    Assert.Equal("RunRuntimeFailure",code crash)
                    Assert.Equal("System.StackOverflowException",(error crash).GetProperty("exceptionType").GetString())
                    Assert.NotEmpty((error crash).GetProperty("frames").EnumerateArray())
                    recovered ()
                    if tracked then Assert.NotEqual(beforeCrash,pid ()))
                transcripts.Add(List.ofSeq replies)
            Assert.Equal<string list>(transcripts[0],transcripts[1])
            Assert.Equal<byte[]>(File.ReadAllBytes(paths[0]),File.ReadAllBytes(paths[1])))

    [<Fact; Trait("Suite","Extended")>]
    let ``CSharp managed text boundaries preserve source BOMs replacement decoding UTF16 limits and compiler diagnostics`` () =
        withDirectory(fun directory ->
            let path,declarations=fixture directory
            let capability=declarations.Capabilities |> List.find(fun cap -> cap.DisplayName="Provider")
            let operation=capability.Operations |> List.exactlyOne
            let answer=declarations.Types |> List.find(fun typ -> typ.DisplayName="Answer")
            let variant=
                match answer.Kind with
                | SemanticTypeKind.Union variants -> variants |> List.find(fun item -> item.DisplayName="Found")
                | _ -> failwith "The Answer fixture must be a union."
            let resultName=exactName "NyT_" (StableId.value answer.Id)
            let variantName=exactName "NyV_" (StableId.value variant.Id)
            let source body =
                $"""using System; using System.Text; using Nyrwvy.Generated;
public sealed class Host : {exactName "NyC_" (StableId.value capability.Id)}
{{ public {resultName} {exactName "Op_" (StableId.value operation.Id)}(NyStr key) {{ {body} }} }}
"""
            let returned value = $"""return new {variantName}({JsonSerializer.Serialize(value:string)});"""
            let files=ResizeArray<string * string * string option>()
            let add label filename (bytes:byte array) expectedCode =
                File.WriteAllBytes(Path.Combine(directory,filename),bytes)
                files.Add(label,filename,expectedCode)
            let normal=source(returned "source 日本😀")
            let encodings : (string * Text.Encoding) list =
                [ "utf8",Text.UTF8Encoding(true); "utf16le",Text.UnicodeEncoding(false,true)
                  "utf16be",Text.UnicodeEncoding(true,true); "utf32le",Text.UTF32Encoding(false,true)
                  "utf32be",Text.UTF32Encoding(true,true) ]
            for label,encoding in encodings do
                add label (label+".cs") (Array.append (encoding.GetPreamble()) (encoding.GetBytes normal)) None
            let invalidSource=Text.Encoding.UTF8.GetBytes(source(returned "raw~")) |> Array.map(fun item -> if item=byte '~' then 0xffuy else item)
            add "source replacement" "invalid-source.cs" invalidSource None
            add "invalid stdout" "invalid-stdout.cs" (Text.Encoding.UTF8.GetBytes(source("Console.OpenStandardOutput().WriteByte(0xFF); "+returned "unused"))) (Some "RunInvalidResult")
            add "replacement envelope" "replacement-envelope.cs"
                (Text.Encoding.UTF8.GetBytes(source("Console.OpenStandardOutput().Write(new byte[] { 91,34,108,105,109,105,116,34,44,34,114,97,119,255,34,93 }); Environment.Exit(0); "+returned "unused")))
                (Some "RunProcessFailure")
            add "invalid stderr" "invalid-stderr.cs"
                (Text.Encoding.UTF8.GetBytes(source("Console.OpenStandardError().WriteByte(0xFF); Environment.Exit(9); "+returned "unused")))
                (Some "RunProcessFailure")
            add "astral stderr" "astral-stderr.cs"
                (Text.Encoding.UTF8.GetBytes(source("Console.Error.Write(string.Concat(System.Linq.Enumerable.Repeat(\"😀\",2048))); Environment.Exit(9); "+returned "unused")))
                (Some "RunProcessFailure")
            add "stdout BOM" "stdout-bom.cs"
                (Text.Encoding.UTF8.GetBytes(source("Console.OpenStandardOutput().Write(new byte[] { 0xFE,0xFF }); Console.OutputEncoding = Encoding.BigEndianUnicode; "+returned "source 日本😀"))) None
            let longName=String.replicate 3000 "日本"
            add "compiler diagnostic" "compiler-diagnostic.cs"
                (Text.Encoding.UTF8.GetBytes(normal+"\n#line 1 \"StableDiagnostic.cs\"\npublic class "+longName+" {} public class "+longName+" {}"))
                (Some "RunProcessFailure")
            for units in [1048576;1048577] do
                let body = $"""var value = "[\"limit\",\"" + new string('a',{units-12}) + "\"]"; Console.OpenStandardOutput().Write(Encoding.UTF32.GetPreamble()); Console.OpenStandardOutput().Write(Encoding.UTF32.GetBytes(value)); Environment.Exit(0); {returned "unused"}"""
                add ("UTF32 boundary "+string units) ($"utf32-{units}.cs") (Text.Encoding.UTF8.GetBytes(source body))
                    (Some(if units=1048576 then "RunProcessFailure" else "RunOutputTooLarge"))
            let host="""{"kind":"hostCapability","capability":"Provider","implementation":"Host"}"""
            let request label filename =
                let expected=if label="source replacement" then Some(found "raw�") elif label.StartsWith("utf",StringComparison.Ordinal) || label="stdout BOM" then Some(found "source 日本😀") else None
                let line=runName "query" ["\"a\"";host] expected
                line.Insert(line.Length-1,$""", "hostSources":[{stringValue filename}]""")
            let good=runName "constant" [] (Some "7")
            let requests=files |> Seq.collect(fun (label,filename,_) -> [request label filename;"""{"id":"alive","op":"status"}""";good]) |> Seq.toList
            let replies=strictPair path ["--allow-host-sources"] requests
            for index,(label,_,expectedCode) in files |> Seq.indexed do
                let reply=replies[index*3]
                match expectedCode with
                | None -> matches reply
                | Some expected ->
                    Assert.Equal(expected,code reply)
                    let message=(error reply).GetProperty("message").GetString()
                    match label with
                    | "replacement envelope" -> Assert.Equal("raw�",message)
                    | "invalid stderr" -> Assert.Equal("\n�",message)
                    | "astral stderr" | "compiler diagnostic" -> Assert.Equal(4096,message.Length)
                    | "UTF32 boundary 1048576" -> Assert.Equal(1048576-12,message.Length)
                    | _ -> ()
                    if label="compiler diagnostic" then Assert.Contains("日本",message)
                success replies[index*3+1] |> ignore
                matches replies[index*3+2])
