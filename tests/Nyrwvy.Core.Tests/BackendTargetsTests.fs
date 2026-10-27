namespace Nyrwvy.Core.Tests

open System
open System.IO
open System.Text.Json
open Nyrwvy.Core
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// A workspace declares its target backends in `<snapshot>.backends.json` (a new workspace gets
/// `["csharp"]`); runPure without `backend` runs on the first, an explicit `backend` wins, and an
/// undeclared workspace needs an explicit backend. The entry
/// uses Bits32, which the Zig backend does not support, so choosing Zig shows without a Zig build.
module BackendTargetsTests =
    let private unwrap = function Ok x -> x | Error e -> failwithf "%A" e

    let private withWorkspace (test: AuthoringSession -> string -> unit) =
        let dir = Path.Combine(Path.GetTempPath(), "nyrwvy-targets-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        let path = Path.Combine(dir, "graph.json")
        try
            use session = AuthoringSession.createNew path |> unwrap
            let execute (request: string) =
                use doc = JsonDocument.Parse(AuthoringProtocol.executeLine session request)
                doc.RootElement.Clone()
            let proposal = execute """{"id":"c","op":"simulateCreateFunctionByName","exported":true,"displayName":"bits","parameters":[{"displayName":"n","type":{"kind":"primitive","primitive":"int64"}}],"resultType":{"kind":"primitive","primitive":"bits32"},"requiredEffects":[],"body":{"kind":"bits32FromInt64","value":{"kind":"parameter","name":"n"}}}"""
            Assert.True(proposal.GetProperty("ok").GetBoolean(), proposal.ToString())
            let commit = execute ($"""{{"id":"m","op":"commit","proposalId":"{proposal.GetProperty("result").GetProperty("proposalId").GetString()}"}}""")
            Assert.True(commit.GetProperty("ok").GetBoolean(), commit.ToString())
            test session path
        finally Directory.Delete(dir, true)

    let private run (session: AuthoringSession) (backend: string option) =
        let field = backend |> Option.map (fun name -> $""","backend":"{name}" """) |> Option.defaultValue ""
        use doc = JsonDocument.Parse(AuthoringProtocol.executeLine session ($"""{{"id":"r","op":"runPureByName","function":"bits","arguments":[{{"kind":"int64","value":"5"}}]{field}}}"""))
        doc.RootElement.Clone()

    let private code (response: JsonElement) =
        if response.GetProperty("ok").GetBoolean() then "ok" else response.GetProperty("error").GetProperty("code").GetString()

    [<Fact>]
    let ``runPure resolves its backend from the workspace's declared targets`` () =
        withWorkspace (fun session path ->
            let declare (text: string) = File.WriteAllText(path + ".backends.json", text)
            Assert.Equal("""{"targets": ["csharp"]}""", File.ReadAllText(path + ".backends.json").Trim())
            Assert.Equal("ok", code (run session None))
            File.Delete(path + ".backends.json")
            Assert.Equal("ProtocolError", code (run session None))
            Assert.Equal("ok", code (run session (Some "csharp")))
            declare """{"targets":["zig","csharp"]}"""
            Assert.Equal("RunBackendUnsupported", code (run session None))
            Assert.Equal("ok", code (run session (Some "csharp")))
            declare """{"targets":["csharp","zig"]}"""
            Assert.Equal("ok", code (run session None))
            Assert.Equal("RunBackendUnsupported", code (run session (Some "zig")))
            for invalid in [ """{"targets":["rust"]}"""; """{"targets":[]}"""; """{"targets":["zig","zig"]}"""; "not json" ] do
                declare invalid
                Assert.Equal("ProtocolError", code (run session None))
            Assert.Equal("ok", code (run session (Some "csharp"))))
