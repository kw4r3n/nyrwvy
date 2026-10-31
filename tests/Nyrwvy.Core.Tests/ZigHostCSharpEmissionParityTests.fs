namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Public emission and bind-c retain the F# workflow, including generated-file refusal and publication order.
module ZigHostCSharpEmissionParityTests =
    open ZigHostParityTests

    let private quote (value: string) = JsonSerializer.Serialize value
    let private sid<'T> name = StableIdAllocator.derived<'T> ("csharp-emission:" + name)
    let private identifier<'T> name = StableId.value (sid<'T> name)
    let private integer = TypeRef.Primitive PrimitiveType.Int64
    let private success reply =
        use document = parseDocument reply
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean(), reply)
        document.RootElement.GetProperty("result").Clone()
    let private errorCode reply =
        use document = parseDocument reply
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean(), reply)
        document.RootElement.GetProperty("error").GetProperty("code").GetString()
    let private operation name =
        { Id = sid<CapabilityOperationTag> name; DisplayName = name; MayWait = false
          Parameters = [ { Id = sid<CapabilityParameterTag> (name + "-a"); DisplayName = "a"; Type = integer }
                         { Id = sid<CapabilityParameterTag> (name + "-b"); DisplayName = "b"; Type = integer } ]
          ResultType = integer; FailureType = None; EffectId = sid<EffectTag> "calls" }
    let private demo =
        { Id = sid<CapabilityTag> "demo"; DisplayName = "Demo"; Exported = true; TaskBound = false
          Operations = [ operation "add"; operation "βeta" ] }
    let private console =
        { Id = sid<CapabilityTag> "console"; DisplayName = "Console"; Exported = true; TaskBound = false
          Operations = [ { Id = sid<CapabilityOperationTag> "line"; DisplayName = "line"; MayWait = false
                           Parameters = [ { Id = sid<CapabilityParameterTag> "text"; DisplayName = "text"; Type = TypeRef.Primitive PrimitiveType.String } ]
                           ResultType = TypeRef.Primitive PrimitiveType.Unit; FailureType = None; EffectId = sid<EffectTag> "calls" } ] }
    let private constant name parameters =
        let node = { Id = sid<GraphNodeTag> (name + "-literal"); Kind = SemanticNodeKind.Int64Literal 7L }
        { Id = sid<FunctionTag> name; DisplayName = name; Exported = true; MustNotWait = false; TypeParameters = []
          Parameters = parameters; ResultType = integer; RequiredEffects = []; Body = { Nodes = [ node ]; ResultNodeId = node.Id } }
    let private fixture () =
        let capability: SemanticParameter = { Id = sid<ParameterTag> "sum-provider"; DisplayName = "provider"; Type = TypeRef.Capability demo.Id }
        let value id kind: SemanticNode = { Id = sid<GraphNodeTag> id; Kind = kind }
        let owner = value "provider" (SemanticNodeKind.ParameterReference capability.Id)
        let left = value "left" (SemanticNodeKind.Int64Literal 2L)
        let right = value "right" (SemanticNodeKind.Int64Literal 3L)
        let call = value "call" (SemanticNodeKind.CapabilityInvoke(owner.Id, demo.Operations.Head.Id,
                            [ { TargetCapabilityParameterId = demo.Operations.Head.Parameters[0].Id; ValueNodeId = left.Id }
                              { TargetCapabilityParameterId = demo.Operations.Head.Parameters[1].Id; ValueNodeId = right.Id } ]))
        { Snapshot.empty (sid<SnapshotTag> "workspace") with
            Effects = [ { Id = sid<EffectTag> "calls"; DisplayName = "Calls"; Exported = true } ]
            Capabilities = [ demo; console ]
            Functions = [ { (constant "sum" [ capability ]) with RequiredEffects = [ sid<EffectTag> "calls" ]; Body = { Nodes = [ owner; left; right; call ]; ResultNodeId = call.Id } } ] }
    let private save path snapshot = File.WriteAllText(path, SnapshotCodec.serialize snapshot |> unwrap)
    let private table = """{"format":"nyrwvy.csharp-mapping","version":1,"library":"demo","capabilities":{"Demo":{"operations":{"add":{"symbol":"nyd_add","arguments":[{"from":"a","c":"int32"},{"from":"b","c":"int32"}],"result":{"c":"int32"}},"βeta":{"symbol":"nyd_βETA","arguments":[{"from":"a","c":"int64"},{"from":"b","c":"int64"}],"result":{"c":"int64"}}}}}}"""
    let private tableProfile name = $"""{{"kind":"externalImplementations","operationIds":[],"tables":[{quote name}]}}"""
    let private externalProfile =
        let ids = demo.Operations |> List.map (fun op -> quote (StableId.value op.Id)) |> String.concat ","
        $"""{{"kind":"externalImplementations","operationIds":[{ids}]}}"""
    let private emission id profile fields = $"""{{"id":{quote id},"op":"emitCSharp","profile":{profile}{fields}}}"""
    let private runHost zig path requests =
        let code, errors, replies = run zig [ "open"; "--snapshot"; path; "--read-only" ] requests
        Assert.Equal(0, code)
        Assert.Equal("", errors)
        Assert.Equal(requests.Length, replies.Length)
        replies
    let private strictPair path requests =
        let expected, actual = runHost false path requests, runHost true path requests
        for request, (left, right) in List.zip requests (List.zip expected actual) do
            Assert.True((left = right), $"{request}\nF#: {left}\nZig: {right}")
        actual
    let private collect (child: System.Diagnostics.Process) timeout label =
        let output, errors = child.StandardOutput.ReadToEndAsync(), child.StandardError.ReadToEndAsync()
        try
            let completed = Threading.Tasks.Task.WhenAll([| output :> Threading.Tasks.Task; errors :> Threading.Tasks.Task; child.WaitForExitAsync() |])
            if not (completed.Wait(timeout: TimeSpan)) then failwith $"{label} exceeded its response deadline."
            child.ExitCode, output.Result, errors.Result
        finally
            if not child.HasExited then
                try child.Kill true
                with :? InvalidOperationException when child.HasExited -> ()
            child.WaitForExit()
    let private requireClang () =
        try
            use child = System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo("clang", [ "--version" ], RedirectStandardOutput = true, RedirectStandardError = true))
            let code, _, errors = collect child (TimeSpan.FromSeconds 10.0) "Required clang --version probe"
            Assert.True((code = 0), $"bind-c parity requires clang on PATH; --version exited {code}: {errors}")
        with :? System.ComponentModel.Win32Exception as error ->
            failwith $"bind-c parity requires clang on PATH: {error.Message}"
    let private cli zig arguments =
        use child = start zig ("bind-c" :: arguments)
        child.StandardInput.Close()
        collect child silence "bind-c"
    let private cliArguments directory functions =
        [ "--header"; Path.Combine(directory, "demo.h"); "--library"; "demo"; "--capability"; "Demo"; "--effect"; "Calls"
          "--prefix"; "nyd_"; "--functions"; functions; "--requests"; Path.Combine(directory, "requests.jsonl"); "--table"; Path.Combine(directory, "table.json") ]
    let private files directory = File.ReadAllBytes(Path.Combine(directory, "requests.jsonl")), File.ReadAllBytes(Path.Combine(directory, "table.json"))
    let private sentinel directory =
        File.WriteAllText(Path.Combine(directory, "requests.jsonl"), "old requests")
        File.WriteAllText(Path.Combine(directory, "table.json"), "old table")

    [<Fact; Trait("Suite", "Extended")>]
    let ``Public CSharp profiles tables overrides symbols and confined generated writes are exact`` () =
        withDirectory (fun directory ->
            let path = Path.Combine(directory, "workspace.json")
            save path (fixture ())
            File.WriteAllText(Path.Combine(directory, "demo.json"), table)
            File.WriteAllText(Path.Combine(directory, "demo.override.json"), """{"format":"nyrwvy.csharp-mapping","version":1,"capabilities":{"Demo":{"operations":{"βeta":{"unavailable":true}}}}}""")
            let profile = tableProfile "demo.json"
            let requests =
                [ emission "native" profile ""
                  emission "external" externalProfile ""
                  emission "empty" """{"kind":"empty"}""" ""
                  emission "unknown" """{"kind":"unknown"}""" ""
                  emission "fields" """{"kind":"empty","extra":1}""" ""
                  emission "escape" profile ", \"output\":\"../outside.cs\""
                  emission "same" profile ", \"output\":\"same.cs\",\"facade\":\"./same.cs\""
                  emission "facadeExtension" profile ", \"output\":\"not-written.cs\",\"facade\":\"bad.txt\"" ]
            let before = File.ReadAllBytes path
            let replies = strictPair path requests
            let native = success replies[0]
            Assert.Contains("This operation is unavailable on the C# backend.", native.GetProperty("source").GetString())
            Assert.Single(native.GetProperty("symbols").GetProperty("capabilityAdapters").EnumerateArray()) |> ignore
            Assert.Equal(2, (success replies[1]).GetProperty("symbols").GetProperty("capabilities").GetArrayLength())
            for index in [ 2; 3; 4; 5; 6; 7 ] do errorCode replies[index] |> ignore
            Assert.Equal("BackendFailure", errorCode replies[2])
            Assert.Equal("ProtocolError", errorCode replies[3])
            Assert.Equal("EmitWriteFailed", errorCode replies[7])
            Assert.False(File.Exists(Path.Combine(directory, "not-written.cs")))
            Assert.False(File.Exists(Path.Combine(directory, "same.cs")))
            let unmarked = Path.Combine(directory, "unmarked.cs")
            File.WriteAllText(unmarked, "person's source")
            let refused =
                [ emission "unmarked" profile ", \"output\":\"unmarked.cs\""
                  emission "facadeUnmarked" profile ", \"output\":\"not-written.cs\",\"facade\":\"unmarked.cs\"" ]
            strictPair path refused |> List.iter (fun reply -> Assert.Equal("EmitWriteFailed", errorCode reply))
            Assert.Equal("person's source", File.ReadAllText unmarked)
            Assert.False(File.Exists(Path.Combine(directory, "not-written.cs")))
            if not (OperatingSystem.IsWindows()) then
                File.CreateSymbolicLink(Path.Combine(directory, "linked.cs"), unmarked) |> ignore
                strictPair path [ emission "link" profile ", \"output\":\"linked.cs\"" ]
                |> List.iter (fun reply -> Assert.Equal("EmitWriteFailed", errorCode reply))
            let write = emission "write" profile ", \"output\":\"Generated.cs\",\"facade\":\"Facade.cs\""
            let expected = runHost false path [ write ] |> List.exactlyOne
            success expected |> ignore
            let generated, facade = Path.Combine(directory, "Generated.cs"), Path.Combine(directory, "Facade.cs")
            let expectedSource, expectedFacade = File.ReadAllBytes generated, File.ReadAllBytes facade
            File.WriteAllText(generated, "// Generated by Nyrwvy.\nold generated source")
            File.WriteAllText(facade, "// Generated by Nyrwvy.\nold generated facade")
            let actual = runHost true path [ write ] |> List.exactlyOne
            Assert.Equal(expected, actual)
            Assert.Equal<byte>(expectedSource, File.ReadAllBytes generated)
            Assert.Equal<byte>(expectedFacade, File.ReadAllBytes facade)
            Assert.Equal<byte>(before, File.ReadAllBytes path)
            let consolePath = Path.Combine(directory, "console.json")
            save consolePath { fixture () with Capabilities = [ console ]; Functions = [] }
            let consoleProfile = $"""{{"kind":"systemConsoleWriteLine","operationId":"{identifier<CapabilityOperationTag> "line"}"}}"""
            let consoleReply = strictPair consolePath [ emission "console" consoleProfile "" ] |> List.exactlyOne |> success
            Assert.Contains("System.Console.WriteLine(", consoleReply.GetProperty("source").GetString()))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Imported mapping names are visible but imported functions do not become facade roots and name failures write nothing`` () =
        withDirectory (fun directory ->
            let library = { fixture () with Id = sid<SnapshotTag> "library"; Functions = [ constant "libraryOnly" [] ]; Capabilities = [ demo ] }
            let local =
                { Snapshot.empty (sid<SnapshotTag> "local") with
                    Functions = [ constant "local" [ { Id = sid<ParameterTag> "local-provider"; DisplayName = "provider"; Type = TypeRef.Capability demo.Id } ] ] }
            let source, path = Path.Combine(directory, "library.json"), Path.Combine(directory, "local.json")
            save source library
            File.WriteAllText(path, SnapshotCodec.serializeResolved library local |> unwrap)
            File.WriteAllText(path + ".dependency.json", (memberOf library (Some("snapshotJson", SnapshotCodec.serialize library |> unwrap)) source).ToJsonString())
            File.WriteAllText(Path.Combine(directory, "demo.json"), table)
            let request = emission "import" (tableProfile "demo.json") ", \"facade\":\"ImportedFacade.cs\""
            let expected = runHost false path [ request ] |> List.exactlyOne
            let facade = Path.Combine(directory, "ImportedFacade.cs")
            let expectedFacade = File.ReadAllBytes facade
            File.Delete facade
            Assert.False(File.Exists facade)
            let actual = runHost true path [ request ] |> List.exactlyOne
            Assert.Equal(expected, actual)
            Assert.Equal<byte>(expectedFacade, File.ReadAllBytes facade)
            let facadeSource = File.ReadAllText facade
            Assert.Contains(" local(", facadeSource)
            Assert.DoesNotContain("libraryOnly", facadeSource)
            let symbols = (success actual).GetProperty("symbols").GetProperty("functions").EnumerateArray() |> Seq.map (fun item -> item.GetProperty("id").GetString()) |> Seq.toList
            Assert.Contains(identifier<FunctionTag> "libraryOnly", symbols)
            let invalid = { fixture () with Functions = [ { (fixture ()).Functions.Head with DisplayName = "bad-name" } ] }
            let invalidPath = Path.Combine(directory, "invalid.json")
            save invalidPath invalid
            let rejected = strictPair invalidPath [ emission "badName" (tableProfile "demo.json") ", \"output\":\"name-source.cs\",\"facade\":\"name-facade.cs\"" ] |> List.exactlyOne
            Assert.Equal("FacadeNameInvalid", errorCode rejected)
            Assert.False(File.Exists(Path.Combine(directory, "name-source.cs")))
            Assert.False(File.Exists(Path.Combine(directory, "name-facade.cs"))))

    [<Fact; Trait("Suite", "Extended")>]
    let ``Mapping table file encodings and malformed native entries retain strict public diagnostics`` () =
        withDirectory (fun directory ->
            let path = Path.Combine(directory, "workspace.json")
            save path (fixture ())
            let encodings: (string * Encoding) list = [ "utf8", UTF8Encoding(true); "utf16", UnicodeEncoding(false, true); "utf16be", UnicodeEncoding(true, true); "utf32", UTF32Encoding(false, true); "utf32be", UTF32Encoding(true, true) ]
            for name, encoding in encodings do File.WriteAllText(Path.Combine(directory, name + ".json"), table, encoding)
            let malformed =
                [ "constant", table.Replace("{\"from\":\"a\",\"c\":\"int32\"}", "{\"value\":null,\"c\":\"int32\"}")
                  "parameter", table.Replace("\"from\":\"a\"", "\"from\":\"absent\"")
                  "failure", table.Replace("\"symbol\":\"nyd_add\"", "\"failure\":\"wrong\",\"symbol\":\"nyd_add\"")
                  "duplicate", table.Replace("\"symbol\":\"nyd_add\"", "\"symbol\":\"old\",\"symbol\":\"nyd_add\"") ]
            for name, contents in malformed do File.WriteAllText(Path.Combine(directory, name + ".json"), contents)
            File.WriteAllText(Path.Combine(directory, "abi.json"), table.Replace("\"result\":{\"c\":\"int32\"}", "\"result\":{\"c\":\"bool\"}"))
            let requests = [ for name, _ in encodings -> emission name (tableProfile(name + ".json")) "" ] @ [ for name, _ in malformed -> emission name (tableProfile(name + ".json")) "" ] @ [ emission "abi" (tableProfile "abi.json") "" ]
            let replies = strictPair path requests
            success replies[0] |> ignore
            for reply in replies.Tail |> List.take (replies.Length - 2) do Assert.Equal("ProtocolError", errorCode reply)
            Assert.Equal("BackendFailure", errorCode (List.last replies))
            let valueKinds = [ "string", "\"1\""; "null", "null"; "true", "true"; "false", "false"; "object", "{}"; "array", "[]"; "fraction", "1.5"; "overflow", "9223372036854775808" ]
            let cases =
                [ for field in [ "value"; "retry" ] do
                      for kind, value in valueKinds do
                          let name = field + "-" + kind
                          let argument = if field = "value" then $"""{{"value":{value},"c":"int32"}}""" else $"""{{"out":"bytes","capacity":8,"retry":{value}}}"""
                          let contents = table.Replace("{\"from\":\"a\",\"c\":\"int32\"}", argument)
                          File.WriteAllText(Path.Combine(directory, name + ".json"), contents)
                          let code = if kind = "fraction" || kind = "overflow" || (field = "value" && kind = "null") then "ProtocolError" else "SessionError"
                          yield emission name (tableProfile(name + ".json")) "", code ]
            let answers = strictPair path (cases |> List.map fst)
            for (_, expected), reply in List.zip cases answers do Assert.Equal(expected, errorCode reply))

    let private header = """#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
typedef struct nyd_counter nyd_counter;
int32_t nyd_add(int32_t a, int32_t b);
int64_t nyd_βETA(int64_t ÄMOUNT);
int64_t nyd_f_K(void);
bool nyd_even(int64_t value);
nyd_counter *nyd_counter_new(int64_t value);
int64_t nyd_counter_get(nyd_counter *counter);
void nyd_counter_free(nyd_counter *counter);
void nyd_keep(nyd_counter *counter, const char *text);
int nyd_buffer(nyd_counter *counter, char *buffer, size_t capacity, size_t *written);
struct nyd_options { size_t size; float scale; int value; };
void nyd_configure(nyd_counter *counter, const struct nyd_options *options);
enum nyd_status { NYD_RETRY = -1, NYD_TOO_BIG = 0xFFFFFFFFFFFFFFFFULL };
int nyd_variadic(const char *format, ...);
"""
    let private spec = """{"capabilities":{"Demo":{"operations":{"numbers":{"symbol":"nyd_add","arguments":[{"value":1,"c":"int32","name":1.5},{"from":"b","c":"int32"}]}}},"Counter":{"operations":{"kept":{"symbol":"nyd_keep","arguments":[{"self":true},{"from":"text","c":"utf8","retain":true}]},"buffer":{"symbol":"nyd_buffer","arguments":[{"self":true},{"out":"bytes","capacity":8,"retry":"NYD_RETRY"}],"failure":"negative"},"configure":{"symbol":"nyd_configure","arguments":[{"self":true},{"struct":[{"sizeof":true,"c":"uint64"},{"from":"scale","c":"float32"},{"value":1,"c":"int32","name":[1e+02,-0,1.2300,18446744073709551616]}],"by":"pointer"}]}}}}}"""

    [<Fact; Trait("Suite", "Extended")>]
    let ``bind-c regenerates self contained Unicode handle struct retained and retry fixtures byte for byte`` () =
        requireClang ()
        withDirectory (fun directory ->
            File.WriteAllText(Path.Combine(directory, "demo.h"), header, UTF8Encoding(false))
            let specPath = Path.Combine(directory, "spec.json")
            let args = cliArguments directory "nyd_add,nyd_βETA,nyd_f_K,nyd_even,nyd_counter_new,nyd_counter_get,nyd_counter_free,nyd_keep,nyd_buffer,nyd_configure,nyd_variadic"
                       @ [ "--release"; "nyd_counter_free"; "--failure"; "nyd_counter_new=null"; "--spec"; specPath ]
            let encodings: Encoding list = [ UTF8Encoding(true); UnicodeEncoding(false, true); UnicodeEncoding(true, true); UTF32Encoding(false, true); UTF32Encoding(true, true) ]
            for encoding in encodings do
                File.WriteAllText(specPath, spec, encoding)
                let expected = cli false args
                Assert.Equal(0, let code, _, _ = expected in code)
                let _, reportText, _ = expected
                use report = JsonDocument.Parse reportText
                let unmapped = report.RootElement.GetProperty("unmapped")
                Assert.Single(unmapped.EnumerateArray()) |> ignore
                Assert.Equal("nyd_variadic", unmapped.[0].GetProperty("function").GetString())
                let expectedFiles = files directory
                File.Delete(Path.Combine(directory,"requests.jsonl"))
                File.Delete(Path.Combine(directory,"table.json"))
                Assert.False(File.Exists(Path.Combine(directory,"requests.jsonl")))
                Assert.False(File.Exists(Path.Combine(directory,"table.json")))
                let actual = cli true args
                Assert.Equal(expected, actual)
                let actualFiles = files directory
                Assert.Equal<byte>(fst expectedFiles, fst actualFiles)
                Assert.Equal<byte>(snd expectedFiles, snd actualFiles)
                use generated = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "table.json")))
                let counter = generated.RootElement.GetProperty("capabilities").GetProperty("Counter").GetProperty("operations")
                Assert.True(counter.GetProperty("kept").GetProperty("arguments").[1].GetProperty("retain").GetBoolean())
                Assert.Equal(-1L, counter.GetProperty("buffer").GetProperty("arguments").[1].GetProperty("retry").GetInt64())
                Assert.Equal("pointer", counter.GetProperty("configure").GetProperty("arguments").[1].GetProperty("by").GetString())
                Assert.True(counter.GetProperty("configure").GetProperty("arguments").[1].GetProperty("struct").[0].GetProperty("sizeof").GetBoolean())
                let operations = generated.RootElement.GetProperty("capabilities").GetProperty("Demo").GetProperty("operations")
                Assert.Equal("nyd_βETA", operations.GetProperty("βeta").GetProperty("symbol").GetString())
                Assert.Equal("nyd_f_K",operations.GetProperty("fK").GetProperty("symbol").GetString())
                let tableText = File.ReadAllText(Path.Combine(directory,"table.json"))
                for lexical in [ "\"name\": 1.5"; "1e+02"; "-0"; "1.2300"; "18446744073709551616" ] do Assert.Contains(lexical,tableText)
                use requestDocument = JsonDocument.Parse(File.ReadAllLines(Path.Combine(directory, "requests.jsonl")) |> Array.last)
                let beta = requestDocument.RootElement.GetProperty("operations").EnumerateArray() |> Seq.find (fun op -> op.GetProperty("displayName").GetString() = "βeta")
                Assert.Equal("ämount", beta.GetProperty("parameters").[0].GetProperty("displayName").GetString()))

    [<Fact; Trait("Suite", "Extended")>]
    let ``bind-c rejects malformed specs before writes and preserves the source runtime fatal publication boundary`` () =
        requireClang ()
        withDirectory (fun directory ->
            File.WriteAllText(Path.Combine(directory, "demo.h"), header, UTF8Encoding(false))
            let specPath = Path.Combine(directory, "spec.json")
            let args = cliArguments directory "nyd_add" @ [ "--spec"; specPath ]
            File.WriteAllText(specPath, "{\"capabilities\":{}}")
            for badArguments in [ []; args @ [ "--unknown"; "value" ]; args @ [ "--buffer"; "0" ]; args @ [ "--clang-arg"; "--target=x86_64-pc-windows-msvc" ] ] do
                sentinel directory
                let before = files directory
                let expected = cli false badArguments
                let code, output, _ = expected
                Assert.Equal(2, code)
                Assert.Equal("", output)
                Assert.Equal(expected, cli true badArguments)
                Assert.Equal<byte>(fst before, fst(files directory))
                Assert.Equal<byte>(snd before, snd(files directory))
            let argument value = $"""{{"capabilities":{{"Demo":{{"operations":{{"add":{{"symbol":"nyd_add","arguments":[{{"value":{value},"c":"int32"}},{{"from":"b","c":"int32"}}]}}}}}}}}}}"""
            let invalid =
                [ argument "1.5"; argument "9223372036854775808"
                  """{"capabilities":{"Demo":{"operations":{"add":{"symbol":"old","symbol":"nyd_add"}}}}}"""
                  "{\"capabilities\":{},\"unused\":" + String.replicate 70 "[" + "0" + String.replicate 70 "]" + "}"
                  "{"; "{x:1}"; "[1,]"; "1e+"; "{\"a\":1"; "{}"
                  """{"capabilities":{"Demo":{"operations":{"add":{"symbol":"\uD800"}}}}}""" ]
            for contents in invalid do
                File.WriteAllText(specPath, contents)
                sentinel directory
                let before = files directory
                let expected = cli false args
                let code, output, errors = expected
                Assert.Equal(2, code)
                Assert.Equal("", output)
                Assert.StartsWith("--spec ", errors)
                Assert.Equal<byte>(fst before, fst(files directory))
                Assert.Equal<byte>(snd before, snd(files directory))
                let actual = cli true args
                Assert.Equal(expected, actual)
                Assert.Equal<byte>(fst before, fst(files directory))
                Assert.Equal<byte>(snd before, snd(files directory))
            let special = argument "1" |> fun value -> value.Replace("\"symbol\":\"nyd_add\"", "\"symbol\":\"nyd_add\",\"unused\":\"\\uD800\"")
            File.WriteAllText(specPath, special)
            let results =
                [ for zig in [ false; true ] do
                      sentinel directory
                      let code, _, _ = cli zig args
                      Assert.NotEqual(0, code)
                      Assert.Equal("old table", File.ReadAllText(Path.Combine(directory, "table.json")))
                      Assert.NotEqual<string>("old requests", File.ReadAllText(Path.Combine(directory, "requests.jsonl")))
                      yield File.ReadAllBytes(Path.Combine(directory, "requests.jsonl")) ]
            // The F# oracle aborts here with a .NET stack; native failures have their own exit/stack.
            // Only the failed publication boundary is contractual for this out-of-contract corner.
            Assert.Equal<byte>(results[0], results[1]))
