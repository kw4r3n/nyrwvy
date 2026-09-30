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
