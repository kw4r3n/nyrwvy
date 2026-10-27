namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// Acceptance for C# mapping tables (docs/decisions/csharp-mapping-tables.md): a C library compiled
/// here, `bind-c` on its header, the proposals applied through a session, a Nyr program emitted with
/// the table profile and run against the real library. Needs cc and clang; skips with a message without them.
module CSharpMappingTableTests =
    let private header = """#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
typedef struct nyd_counter nyd_counter;
int32_t nyd_add(int32_t a, int32_t b);
bool nyd_is_even(int64_t n);
char *nyd_greet(const char *name);
void nyd_free(char *text);
nyd_counter *nyd_counter_create(int64_t start);
int64_t nyd_counter_add(nyd_counter *counter, int64_t amount);
void nyd_counter_destroy(nyd_counter *counter);
int nyd_parse_port(const char *text);
size_t nyd_checksum(const unsigned char *data, size_t length);
int nyd_live(void);
int nyd_platform_only(void);
struct point { int x, y; };
struct point nyd_origin(void);
"""

    let private source = """#include "nyrdemo.h"
#include <stdlib.h>
#include <string.h>
struct nyd_counter { int64_t value; };
static int live = 0;
int32_t nyd_add(int32_t a, int32_t b) { return a + b; }
bool nyd_is_even(int64_t n) { return n % 2 == 0; }
char *nyd_greet(const char *name) {
    char *text = malloc(strlen(name) + 8);
    strcpy(text, "hello, ");
    strcat(text, name);
    return text;
}
void nyd_free(char *text) { free(text); }
nyd_counter *nyd_counter_create(int64_t start) {
    if (start < 0) return NULL;
    nyd_counter *counter = malloc(sizeof *counter);
    counter->value = start;
    live++;
    return counter;
}
int64_t nyd_counter_add(nyd_counter *counter, int64_t amount) { return counter->value += amount; }
void nyd_counter_destroy(nyd_counter *counter) { free(counter); live--; }
int nyd_parse_port(const char *text) {
    char *end;
    long value = strtol(text, &end, 10);
    return *text == 0 || *end != 0 || value < 1 || value > 65535 ? -22 : (int)value;
}
size_t nyd_checksum(const unsigned char *data, size_t length) {
    size_t sum = 0;
    for (size_t i = 0; i < length; i++) sum += data[i];
    return sum;
}
int nyd_live(void) { return live; }
struct point nyd_origin(void) { struct point p = { 0, 0 }; return p; }
"""

    let private run (directory: string) (environment: (string * string) list) (file: string) (arguments: string list) =
        let info = ProcessStartInfo(file)
        info.WorkingDirectory <- directory
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] <- "1"
        info.Environment["DOTNET_NOLOGO"] <- "1"
        for name, value in environment do info.Environment[name] <- value
        for argument in arguments do info.ArgumentList.Add argument
        use child = Process.Start info
        let stderr = child.StandardError.ReadToEndAsync()
        let stdout = child.StandardOutput.ReadToEnd()
        child.WaitForExit()
        child.ExitCode, stdout, stderr.Result

    let private succeed directory environment file arguments =
        match run directory environment file arguments with
        | 0, stdout, _ -> stdout
        | code, stdout, stderr -> failwithf "%s %A exited %d:\n%s\n%s" file arguments code stdout stderr

    let private available tool =
        try (let code, _, _ = run (Path.GetTempPath()) [] tool [ "--version" ] in code = 0) with _ -> false

    let private send (session: AuthoringSession) (line: string) =
        use document = AuthoringProtocol.executeLine session line |> JsonDocument.Parse
        document.RootElement.Clone()

    let private sendOk session line =
        let reply = send session line
        if not (reply.GetProperty("ok").GetBoolean()) then failwithf "%s\n-> %s" line (reply.ToString())
        reply.GetProperty("result")

    let private commit session (line: string) =
        let proposal = (sendOk session line).GetProperty("proposalId").GetString()
        sendOk session $"""{{"id":"commit","op":"commit","proposalId":"{proposal}"}}""" |> ignore

    let private program adapter = $"""using System;
using System.Text;
using Nyrwvy.Generated;

int failures = 0;
void Check(string name, bool ok)
{{
    Console.WriteLine($"{{(ok ? "ok  " : "FAIL")}} {{name}}");
    if (!ok) failures++;
}}
var demo = new {adapter}();
Check("int", NyFacade.sum(demo, 2, 40) == 42);
Check("Int64 out of the C int32 range is a host failure", Fails<OverflowException>(() => NyFacade.sum(demo, 1L << 40, 1)));
Check("bool", NyFacade.even(demo, 4) && !NyFacade.even(demo, 7));
Check("owned string result", NyFacade.greet(demo, "Nyrwvy").Text == "hello, Nyrwvy");
Check("non-ASCII string", NyFacade.greet(demo, "ü€").Text == "hello, ü€");
Check("String with U+0000 is a host failure", Fails<ArgumentException>(() => NyFacade.greet(demo, "a\0b")));
Check("bytes", NyFacade.checksum(demo, NyBytes.FromOwnedArray([1, 2, 250])) == 253);
Check("negative return is Err", NyFacade.port(demo, "x") is {{ IsOk: false }} bad && bad.ErrorValue.Text == "nyd_parse_port failed with -22");
Check("non-negative return is Ok", NyFacade.port(demo, "8080") is {{ IsOk: true, OkValue: 8080 }});
Check("handle through UseResource", NyFacade.counted(demo, 10, 5) is {{ IsOk: true, OkValue: 15 }});
Check("release ran", NyFacade.live(demo) == 0);
Check("null handle is Err", NyFacade.counted(demo, -1, 5) is {{ IsOk: false }} none && none.ErrorValue.Text == "nyd_counter_create returned null");
Check("use after release is a host failure", Fails<InvalidOperationException>(() => NyFacade.stale(demo), "Handle is not open"));
Check("release ran before the failure", NyFacade.live(demo) == 0);
Check("unavailable operation is a host failure", Fails<NotSupportedException>(() => NyFacade.platformOnly(demo)));
return failures == 0 ? 0 : 1;

static bool Fails<T>(Func<object> action, string? message = null) where T : Exception
{{
    try {{ action(); return false; }}
    catch (T error) {{ return message is null || error.Message == message; }}
}}
"""

    [<Fact; Trait("Suite", "Extended")>]
    let ``bind-c table binds a real C library without host code`` () =
        if not (available "cc" && available "clang") then
            Console.WriteLine "SKIPPED bind-c acceptance: cc and clang are required to build and read the test library."
        else
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-bind-c-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try
            File.WriteAllText(Path.Combine(root, "nyrdemo.h"), header)
            File.WriteAllText(Path.Combine(root, "nyrdemo.c"), source)
            succeed root [] "cc" [ "-shared"; "-fPIC"; "-o"; "libnyrdemo.so"; "nyrdemo.c" ] |> ignore
            let tool = typeof<AuthoringSession>.Assembly.Location
            let generate () =
                succeed root [] "dotnet"
                    [ tool; "bind-c"; "--header"; "nyrdemo.h"; "--library"; "nyrdemo"; "--capability"; "Demo"; "--effect"; "DemoCalls"; "--prefix"; "nyd_"
                      "--functions"; "nyd_add,nyd_is_even,nyd_greet,nyd_counter_create,nyd_counter_add,nyd_counter_destroy,nyd_parse_port,nyd_checksum,nyd_live,nyd_platform_only,nyd_origin,nyd_free"
                      "--release"; "nyd_counter_destroy"; "--free"; "nyd_greet=nyd_free"
                      "--failure"; "nyd_counter_create=null"; "--failure"; "nyd_parse_port=negative"
                      "--requests"; "demo.requests.jsonl"; "--table"; "demo.csharp.json" ]
            use report = JsonDocument.Parse(generate ())
            let unmapped = report.RootElement.GetProperty("unmapped").EnumerateArray() |> Seq.map (fun item -> item.GetProperty("function").GetString()) |> Seq.toList
            Assert.Equal<string list>([ "nyd_origin"; "nyd_free" ], unmapped)
            // nyd_platform_only is declared but not built here: the hand-edited override marks it unavailable.
            File.WriteAllText(Path.Combine(root, "demo.csharp.override.json"),
                """{"format":"nyrwvy.csharp-mapping","version":1,"capabilities":{"Demo":{"operations":{"platformOnly":{"unavailable":true}}}}}""")
            use session = AuthoringSession.createNew (Path.Combine(root, "demo.json")) |> function Ok session -> session | Error error -> failwithf "%A" error
            for line in File.ReadAllLines(Path.Combine(root, "demo.requests.jsonl")) do commit session line
            let int64 = """{"kind":"primitive","primitive":"int64"}"""
            let str = """{"kind":"primitive","primitive":"string"}"""
            let demo = """{"displayName":"demo","type":{"kind":"capability","name":"Demo"}}"""
            let invoke (value: string) operation arguments = $"""{{"kind":"capabilityInvoke","capabilityValue":{value},"capability":"{(if value.Contains "counter" then "Counter" else "Demo")}","operation":"{operation}","arguments":[{arguments}]}}"""
            let p name = $"""{{"kind":"parameter","name":"{name}"}}"""
            let arg name value = $"""{{"parameter":"{name}","value":{value}}}"""
            let create name parameters resultType body =
                commit session $"""{{"id":"f","op":"simulateCreateFunctionByName","displayName":"{name}","exported":true,"parameters":[{parameters}],"resultType":{resultType},"requiredEffects":["DemoCalls"],"body":{body}}}"""
            let param name typeJson = $"""{{"displayName":"{name}","type":{typeJson}}}"""
            let bytes = """{"kind":"primitive","primitive":"bytes"}"""
            let counterType = """{"kind":"capability","name":"Counter"}"""
            let one = """{"kind":"int64Literal","value":"1"}"""
            let zero = """{"kind":"int64Literal","value":"0"}"""
            let resultInt = $"""{{"kind":"builtinGeneric","generic":"result","arguments":[{int64},{str}]}}"""
            create "sum" $"""{demo},{param "a" int64},{param "b" int64}""" int64 (invoke (p "demo") "add" $"""{arg "a" (p "a")},{arg "b" (p "b")}""")
            create "even" $"""{demo},{param "n" int64}""" """{"kind":"primitive","primitive":"bool"}""" (invoke (p "demo") "isEven" (arg "n" (p "n")))
            create "greet" $"""{demo},{param "name" str}""" str (invoke (p "demo") "greet" (arg "name" (p "name")))
            create "checksum" $"""{demo},{param "data" bytes}""" int64 (invoke (p "demo") "checksum" (arg "data" (p "data")))
            create "port" $"""{demo},{param "text" str}""" resultInt (invoke (p "demo") "parsePort" (arg "text" (p "text")))
            create "live" demo int64 (invoke (p "demo") "live" "")
            create "platformOnly" demo int64 (invoke (p "demo") "platformOnly" "")
            create "bump" $"""{param "amount" int64},{param "counter" counterType}""" resultInt
                $"""{{"kind":"resultOk","errorType":{str},"value":{invoke (p "counter") "add" (arg "amount" (p "amount"))}}}"""
            create "counted" $"""{demo},{param "start" int64},{param "amount" int64}""" resultInt
                $"""{{"kind":"useResource","acquire":{invoke (p "demo") "counterCreate" (arg "start" (p "start"))},"releaseCapability":{p "demo"},"capability":"Demo","releaseOperation":"counterDestroy","body":"bump","context":[{arg "amount" (p "amount")}]}}"""
            let pattern = """{"kind":"patternReference","name":"counter"}"""
            create "stale" demo int64
                $"""{{"kind":"matchResult","okName":"counter","errName":"failure","input":{invoke (p "demo") "counterCreate" (arg "start" one)},"okBody":{{"kind":"bind","value":{invoke (p "demo") "counterDestroy" (arg "counter" pattern)},"body":{invoke pattern "add" (arg "amount" one)}}},"errBody":{zero}}}"""

            let emit tables = $"""{{"id":"emit","op":"emitCSharp","profile":{{"kind":"externalImplementations","operationIds":[],"tables":[{tables}]}},"output":"Generated.cs","facade":"Facade.cs"}}"""
            // Regeneration rewrites the table but keeps the override.
            generate () |> ignore
            let emitted = sendOk session (emit "\"demo.csharp.json\"")
            let adapter =
                emitted.GetProperty("symbols").GetProperty("capabilityAdapters").EnumerateArray()
                |> Seq.map (fun item -> item.GetProperty("name").GetString())
                |> Seq.find (fun name -> name = CSharpBackendNames.capabilityAdapterName (session.Declarations().Capabilities |> List.find (fun item -> item.DisplayName = "Demo")).Id)

            // Mismatches are diagnostics before emission.
            let refused (table: string) (expected: string) =
                File.WriteAllText(Path.Combine(root, "broken.csharp.json"), table)
                let reply = send session (emit "\"broken.csharp.json\"")
                Assert.False(reply.GetProperty("ok").GetBoolean())
                Assert.Contains(expected, reply.GetProperty("error").GetProperty("message").GetString())
            let generated = File.ReadAllText(Path.Combine(root, "demo.csharp.json"))
            refused (generated.Replace("\"isEven\": {", "\"isEvenRenamed\": {")) "has no operation 'isEvenRenamed'"
            refused (generated.Replace("\"c\": \"bool\"", "\"c\": \"utf8\"")) "IncompatibleNativeBinding"
            refused (generated.Replace("\"failure\": \"negative\"", "\"failure\": \"null\"")) "IncompatibleNativeBinding"
            refused (generated.Replace("\"version\": 1", "\"version\": 2")) "only version 1"

            File.WriteAllText(Path.Combine(root, "Program.cs"), program adapter)
            File.WriteAllText(Path.Combine(root, "Demo.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>")
            succeed root [] "dotnet" [ "build"; "Demo.csproj"; "-v:q" ] |> ignore
            let output = succeed root [ "LD_LIBRARY_PATH", root ] "dotnet" [ Path.Combine(root, "bin", "Debug", "net10.0", "Demo.dll") ]
            Assert.DoesNotContain("FAIL", output)
            Assert.Equal(15, output.Split('\n') |> Array.filter (fun line -> line.StartsWith "ok  ") |> Array.length)
        finally
            if Directory.Exists root then Directory.Delete(root, true)
