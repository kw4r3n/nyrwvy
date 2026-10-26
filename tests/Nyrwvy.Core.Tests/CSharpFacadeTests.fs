namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// emitCSharp facade: readable C# names for exported declarations, compiled and run beside the program.
module CSharpFacadeTests =
    let private int64 = """{"kind":"primitive","primitive":"int64"}"""
    let private string = """{"kind":"primitive","primitive":"string"}"""
    let private named name = $"""{{"kind":"named","name":"{name}"}}"""
    let private p name = $"""{{"kind":"parameter","name":"{name}"}}"""

    let private withSession (test: AuthoringSession -> (string -> JsonElement) -> (string -> JsonElement) -> unit) =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-facade-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            use session = AuthoringSession.createNew (Path.Combine(dir, "graph.json")) |> function Ok value -> value | Error error -> failwithf "%A" error
            let respond (line: string) =
                use document = JsonDocument.Parse(AuthoringProtocol.executeLine session line)
                document.RootElement.Clone()
            let execute line =
                let response = respond line
                Assert.True(response.GetProperty("ok").GetBoolean(), response.ToString())
                let result = response.GetProperty("result")
                match result.TryGetProperty("proposalId") with
                | true, id -> respond $"""{{"id":"c","op":"commit","proposalId":"{id.GetString()}"}}""" |> ignore
                | _ -> ()
                result
            test session execute respond
        finally
            Directory.Delete(dir, true)

    let private createFunction (execute: string -> JsonElement) name parameters result effects body =
        let parameters = parameters |> List.map (fun (name, t) -> $"""{{"displayName":"{name}","type":{t}}}""") |> String.concat ","
        execute $"""{{"id":"f","op":"simulateCreateFunctionByName","exported":true,"displayName":"{name}","parameters":[{parameters}],"resultType":{result},"requiredEffects":{effects},"body":{body}}}"""
        |> ignore

    let private author (execute: string -> JsonElement) =
        let field name t = $"""{{"displayName":"{name}","type":{t}}}"""
        let createType name typeParameters kind =
            execute $"""{{"id":"t","op":"simulateCreateTypeByName","exported":true,"displayName":"{name}","typeParameters":{typeParameters},"typeKind":{kind}}}""" |> ignore
        createType "Pair" "[]" $"""{{"kind":"product","fields":[{field "left" int64},{field "right" int64}]}}"""
        createType "Shape" "[]" $"""{{"kind":"union","variants":[{{"displayName":"Circle","fields":[{field "radius" int64}]}},{{"displayName":"Empty","fields":[]}}]}}"""
        let typeParameter = """{"kind":"typeParameter","name":"T"}"""
        createType "Box" """["T"]""" $"""{{"kind":"product","fields":[{field "item" typeParameter}]}}"""
        execute """{"id":"e","op":"simulateCreateEffect","exported":true,"displayName":"io"}""" |> ignore
        execute $"""{{"id":"k","op":"simulateCreateCapabilityByName","displayName":"Store","operations":[{{"displayName":"get","parameters":[{field "key" string}],"resultType":{int64},"effect":"io"}}]}}""" |> ignore
        let pairField name = $"""{{"kind":"fieldAccess","source":{p "pair"},"type":"Pair","field":"{name}"}}"""
        createFunction execute "swap" [ "pair", named "Pair" ] (named "Pair") "[]"
            $"""{{"kind":"productConstruct","type":"Pair","fields":[{{"field":"left","value":{pairField "right"}}},{{"field":"right","value":{pairField "left"}}}]}}"""
        createFunction execute "keep" [ "shape", named "Shape" ] (named "Shape") "[]" (p "shape")
        let t = """{"kind":"typeParameter","name":"T"}"""
        let boxOfT = $"""{{"kind":"named","name":"Box","arguments":[{t}]}}"""
        execute $"""{{"id":"f","op":"simulateCreateFunctionByName","exported":true,"displayName":"unbox","typeParameters":["T"],"parameters":[{{"displayName":"box","type":{boxOfT}}}],"resultType":{t},"requiredEffects":[],"body":{{"kind":"fieldAccess","source":{p "box"},"type":"Box","field":"item"}}}}"""
        |> ignore
        createFunction execute "lookup" [ "store", """{"kind":"capability","name":"Store"}"""; "key", string ] int64 """["io"]"""
            $"""{{"kind":"capabilityInvoke","capabilityValue":{p "store"},"capability":"Store","operation":"get","arguments":[{{"parameter":"key","value":{p "key"}}}]}}"""

    // The Store capability is implemented by the host, so its operation is an external implementation.
    let private emitRequest (session: AuthoringSession) =
        let operation = (session.Declarations().Capabilities |> List.exactlyOne).Operations |> List.exactlyOne
        $"""{{"id":"e","op":"emitCSharp","profile":{{"kind":"externalImplementations","operationIds":["{StableId.value operation.Id}"]}},"output":"gen/Generated.cs","facade":"gen/Facade.cs"}}"""

    [<Fact>]
    let ``a host uses only facade names to build, read, match, implement and call`` () =
        withSession (fun session execute _ ->
            author execute
            let emitted = execute (emitRequest session)
            // Emitting again replaces the earlier generated facade.
            let emitted = execute (emitRequest session)
            let facade = File.ReadAllText(emitted.GetProperty("facadePath").GetString())
            Assert.Contains("global using Pair = Nyrwvy.Generated.NyT_", facade)
            Assert.Contains("global using ShapeCircle = Nyrwvy.Generated.NyV_", facade)
            Assert.DoesNotContain("global using Box =", facade)
            let host =
                """
using Nyrwvy.Generated;
using static Nyrwvy.Generated.NyFacade;
sealed class MemoryStore : StoreBase { public override long get(NyStr key) => key == "k" ? 7 : 0; }
static class Runner
{
    static int Main()
    {
        Pair swapped = swap(NewPair(left: 1, right: 2));
        if (swapped is not { left: 2, right: 1 }) return 1;
        if (keep(NewShapeCircle(radius: 3)) is not ShapeCircle { radius: 3 }) return 2;
        if (keep(NewShapeEmpty()) is not ShapeEmpty) return 3;
        if (NewBox(item: 5L).item != 5 || unbox(NewBox(item: 6L)) != 6) return 4;
        if (lookup(new MemoryStore(), "k") != 7) return 5;
        return 0;
    }
}
"""
            let generated = File.ReadAllText(emitted.GetProperty("path").GetString())
            let root = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName session.Path, "build")).FullName
            match GeneratedBuild.compile root [ "Generated.cs", generated; "Facade.cs", facade; "Program.cs", host ] 60000 with
            | Ok assembly ->
                let info = ProcessStartInfo("dotnet", RedirectStandardOutput = true)
                info.ArgumentList.Add(assembly)
                use child = Process.Start(info)
                child.WaitForExit()
                Assert.Equal(0, child.ExitCode)
            | Error failure -> failwithf "Build failed: %A" failure)

    [<Fact>]
    let ``a display name carried by two facade names is rejected before any file is written`` () =
        withSession (fun session execute respond ->
            author execute
            // A function named like a type's constructor would share one facade name.
            createFunction execute "NewPair" [ "pair", named "Pair" ] (named "Pair") "[]" (p "pair")
            let response = respond (emitRequest session)
            Assert.False(response.GetProperty("ok").GetBoolean())
            let error = response.GetProperty("error")
            Assert.Equal("FacadeNameConflict", error.GetProperty("code").GetString())
            let problem = error.GetProperty("problems").[0]
            Assert.Equal("NewPair", problem.GetProperty("name").GetString())
            Assert.Equal(2, problem.GetProperty("declarations").GetArrayLength())
            Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName session.Path, "gen"))))

    [<Fact>]
    let ``an imported capability the host implements gets a base class without an exported signature reaching it`` () =
        withSession (fun library execute _ ->
            execute """{"id":"e","op":"simulateCreateEffect","exported":true,"displayName":"io"}""" |> ignore
            execute $"""{{"id":"k","op":"simulateCreateCapabilityByName","displayName":"Store","exported":true,"operations":[{{"displayName":"get","parameters":[{{"displayName":"key","type":{string}}}],"resultType":{int64},"effect":"io"}}]}}""" |> ignore
            let operation = (library.Declarations().Capabilities |> List.exactlyOne).Operations |> List.exactlyOne
            library.Close()
            let path = Path.Combine(Path.GetDirectoryName library.Path, "program.json")
            use program = AuthoringSession.createNewWithDependencies path [ library.Path ] |> function Ok value -> value | Error error -> failwithf "%A" error
            let emit operationIds =
                use document =
                    JsonDocument.Parse(
                        AuthoringProtocol.executeLine program
                            $"""{{"id":"e","op":"emitCSharp","profile":{{"kind":"externalImplementations","operationIds":[{operationIds}]}},"output":"gen/Generated.cs","facade":"gen/Facade.cs"}}""")
                Assert.True(document.RootElement.GetProperty("ok").GetBoolean(), document.RootElement.ToString())
                File.ReadAllText(document.RootElement.GetProperty("result").GetProperty("facadePath").GetString())
            Assert.Contains("public abstract class StoreBase", emit $"\"{StableId.value operation.Id}\"")
            // Without the host implementing it, an unreached imported capability stays out (no re-export).
            Assert.DoesNotContain("StoreBase", emit ""))
