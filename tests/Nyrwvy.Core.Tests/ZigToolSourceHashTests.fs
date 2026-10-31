namespace Nyrwvy.Core.Tests

open System
open System.IO
open Nyrwvy.Tools.AuthoringSession
open Xunit

module ZigToolSourceHashTests =
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
