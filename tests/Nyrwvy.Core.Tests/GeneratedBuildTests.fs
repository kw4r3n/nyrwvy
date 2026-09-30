namespace Nyrwvy.Core.Tests

open System
open System.Diagnostics
open System.IO
open Nyrwvy.Tools.AuthoringSession
open Xunit

/// The in-process Roslyn build keeps the generated project's options: nullable, warnings as errors.
module GeneratedBuildTests =
    let private withDirectory action =
        let root = Path.Combine(Path.GetTempPath(), "nyrwvy-generated-build-test-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try action root
        finally Directory.Delete(root, true)

    let private rejected result =
        match result with
        | Error(GeneratedBuild.Rejected errors) -> errors
        | other -> failwithf "The source was not rejected: %A" other

    [<Fact>]
    let ``a compiled runner runs as a child process`` () =
        withDirectory (fun root ->
            let source = "static class Runner { static void Main() => System.Console.Write(\"ok\"); }"
            match GeneratedBuild.compile root [ "Program.cs", source ] 30000 with
            | Ok assembly ->
                let info = ProcessStartInfo("dotnet", RedirectStandardOutput = true)
                info.ArgumentList.Add(assembly)
                use child = Process.Start(info)
                let output = child.StandardOutput.ReadToEnd()
                child.WaitForExit()
                Assert.Equal(0, child.ExitCode)
                Assert.Equal("ok", output)
            | Error failure -> failwithf "Build failed: %A" failure)

    [<Fact>]
    let ``errors and warnings are reported with file, position and code`` () =
        withDirectory (fun root ->
            let error = rejected (GeneratedBuild.compile root [ "Program.cs", "static class Runner { static void Main() { missing(); } }" ] 30000)
            Assert.Contains("Program.cs(1,44): error CS0103", error)
            // An unused local is a warning, which the generated project treats as an error.
            let warning = rejected (GeneratedBuild.compile root [ "Program.cs", "static class Runner { static void Main() { int unused; } }" ] 30000)
            Assert.Contains("error CS0168", warning)
            // Nullable reference types are enabled.
            let nullable = rejected (GeneratedBuild.compile root [ "Program.cs", "static class Runner { static void Main() { string text = null; System.Console.Write(text); } }" ] 30000)
            Assert.Contains("error CS8600", nullable))

    [<Fact>]
    let ``the build cache evicts the least recently used entry by count and by bytes`` () =
        let bytes count = Array.zeroCreate<byte> count
        let byCount = GeneratedBuild.BuildCache(2, 1000L)
        byCount.Add("a", bytes 1, bytes 1)
        byCount.Add("b", bytes 1, bytes 1)
        Assert.True((byCount.TryGet "a").IsSome)
        byCount.Add("c", bytes 1, bytes 1)
        // "b" was used least recently, so it goes; "a" was just read.
        Assert.False(byCount.Contains "b")
        Assert.True(byCount.Contains "a" && byCount.Contains "c")
        Assert.Equal(2, byCount.Count)
        let byBytes = GeneratedBuild.BuildCache(8, 100L)
        byBytes.Add("a", bytes 40, bytes 20)
        byBytes.Add("b", bytes 30, bytes 0)
        Assert.Equal(90L, byBytes.Bytes)
        byBytes.Add("c", bytes 30, bytes 0)
        Assert.False(byBytes.Contains "a")
        Assert.Equal(60L, byBytes.Bytes)
        // An entry larger than the whole bound is not kept and evicts nothing.
        byBytes.Add("huge", bytes 101, bytes 0)
        Assert.False(byBytes.Contains "huge")
        Assert.Equal(2, byBytes.Count)
        // Adding a key again replaces its entry.
        byBytes.Add("b", bytes 10, bytes 0)
        Assert.Equal(40L, byBytes.Bytes)

    [<Fact>]
    let ``cached builds are keyed by every source name and text`` () =
        let sources = [ "Program.cs", "static class Runner { static void Main() => System.Console.Write(\"cached\"); }" ]
        let key = GeneratedBuild.fingerprint sources
        Assert.Equal(key, GeneratedBuild.fingerprint sources)
        Assert.NotEqual<string>(key, GeneratedBuild.fingerprint [ "Other.cs", snd sources.Head ])
        Assert.NotEqual<string>(key, GeneratedBuild.fingerprint [ "Program.cs", snd sources.Head + " " ])
        Assert.NotEqual<string>(key, GeneratedBuild.fingerprint (sources @ [ "Extra.cs", "" ]))
        let run () =
            withDirectory (fun root ->
                match GeneratedBuild.compileCached root sources 30000 with
                | Ok assembly ->
                    Assert.True(File.Exists assembly)
                    Assert.True(File.Exists(Path.Combine(root, "Generated.runtimeconfig.json")))
                    let info = ProcessStartInfo("dotnet", RedirectStandardOutput = true)
                    info.ArgumentList.Add(assembly)
                    use child = Process.Start(info)
                    let output = child.StandardOutput.ReadToEnd()
                    child.WaitForExit()
                    Assert.Equal("cached", output)
                    GeneratedBuild.lastLookup.Value
                | Error failure -> failwithf "Build failed: %A" failure)
        run () |> ignore
        Assert.Equal(Some(key, true), run ())
        // Failed builds are not cached.
        withDirectory (fun root ->
            let broken = [ "Program.cs", "static class Runner { static void Main() { missing(); } }" ]
            for _ in 1 .. 2 do
                Assert.Contains("error CS0103", rejected (GeneratedBuild.compileCached root broken 30000))
                Assert.Equal(Some(GeneratedBuild.fingerprint broken, false), GeneratedBuild.lastLookup.Value))
