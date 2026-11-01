namespace Nyrwvy.Core.Tests

open System
open System.IO
open Xunit

module ZigToolSourceHashTests =
    [<Fact; Trait("Suite", "Extended")>]
    let ``host status identifies the commit used for its build`` () =
        let root = ZigModelReaderSupport.root ()
        let commit =
            ZigTools.launch "git" "test build identity" root [ "rev-parse"; "--verify"; "HEAD" ] 2000
            |> Result.map (fun (_, output, _) -> output.Trim())
            |> ZigHostParityTests.unwrap
        let snapshot = Path.Combine(root, "examples", "task-cancellation-outcome", "fixtures", "task-outcomes.nyrec")
        let code, errors, lines = ZigHostParityTests.run true [ "open"; "--snapshot"; snapshot; "--read-only" ] [ "{\"id\":\"1\",\"op\":\"status\"}" ]
        Assert.Equal(0, code)
        Assert.Equal("", errors)
        use reply = ZigHostParityTests.parseDocument (List.exactlyOne lines)
        Assert.Equal("nyrwvy-zig-host+" + commit, reply.RootElement.GetProperty("result").GetProperty("build").GetString())
        ZigHostParityTests.withDirectory (fun directory ->
            let git = Path.Combine(directory, "git")
            File.WriteAllText(git, "#!/bin/sh\nprintf '%s\\n' 0000000000000000000000000000000000000000\n")
            File.SetUnixFileMode(git, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
            let environment = [ "PATH", directory + string Path.PathSeparator + Environment.GetEnvironmentVariable "PATH" ]
            let code, errors, lines = ZigHostParityTests.runWithEnvironment environment true [ "open"; "--snapshot"; snapshot; "--read-only" ] [ "{\"id\":\"1\",\"op\":\"status\"}" ]
            Assert.Equal(0, code)
            Assert.Equal("", errors)
            use reply = ZigHostParityTests.parseDocument (List.exactlyOne lines)
            Assert.Equal("nyrwvy-zig-host+" + commit, reply.RootElement.GetProperty("result").GetProperty("build").GetString()))

    [<Fact>]
    let ``nested runtime resources invalidate tool keys but build outputs do not`` () =
        ZigHostParityTests.withDirectory (fun directory ->
            File.WriteAllText(Path.Combine(directory, "main.zig"), "main")
            Assert.Equal("main.zig\u0000main", ZigTools.sourceText directory)
            let runtime = Path.Combine(directory, "runtime")
            Directory.CreateDirectory runtime |> ignore
            let resource = Path.Combine(runtime, "Support.cs")
            File.WriteAllText(resource, "before")
            let original = ZigTools.sourceText directory
            File.WriteAllText(resource, "after")
            let changed = ZigTools.sourceText directory
            Assert.NotEqual<string>(original, changed)
            let binary = Path.Combine(runtime, "Characters.bin")
            File.WriteAllBytes(binary, [| 0x80uy |])
            let firstBinary = ZigTools.sourceText directory
            File.WriteAllBytes(binary, [| 0x81uy |])
            let changedBinary = ZigTools.sourceText directory
            Assert.NotEqual<string>(firstBinary, changedBinary)
            for name in [ "bin"; "obj"; ".zig-cache" ] do
                let output = Path.Combine(directory, "csharp", name, "nested")
                Directory.CreateDirectory output |> ignore
                File.WriteAllText(Path.Combine(output, "Generated.cs"), "build product")
            Assert.Equal(changedBinary, ZigTools.sourceText directory)
            if OperatingSystem.IsLinux() then
                let output = Path.Combine(directory, "csharp", "obj")
                let originalMode = File.GetUnixFileMode output
                try
                    File.SetUnixFileMode(output, UnixFileMode.None)
                    Assert.Equal(changedBinary, ZigTools.sourceText directory)
                finally
                    File.SetUnixFileMode(output, originalMode))
