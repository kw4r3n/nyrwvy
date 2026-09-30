namespace Nyrwvy.Tools.AuthoringSession

open System
open System.IO
open System.Threading
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.Emit

/// Compiles a generated runner in this process with Roslyn, with the options `dotnet build -c Release`
/// gave the generated project: an executable for the running .NET major version, nullable enabled
/// and warnings as errors. Only compilation moves in process; the program still runs as a child.
module internal GeneratedBuild =
    type Failure =
        | TimedOut
        | Rejected of string

    let private major = Environment.Version.Major

    /// The framework reference pack of the running major version, or the runtime's own assemblies
    /// when the SDK's `packs` directory is absent.
    let private references =
        lazy
            let runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
            let dotnetRoot = DirectoryInfo(runtime).Parent.Parent.Parent.FullName
            let packs = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref")
            let referencePack =
                if not (Directory.Exists packs) then None else
                    Directory.GetDirectories(packs)
                    |> Array.choose (fun directory ->
                        match Version.TryParse(Path.GetFileName directory) with
                        | true, version when version.Major = major -> Some(version, Path.Combine(directory, "ref", $"net{major}.0"))
                        | _ -> None)
                    |> Array.filter (snd >> Directory.Exists)
                    |> Array.sortByDescending fst
                    |> Array.tryHead
                    |> Option.map snd
            let files =
                match referencePack with
                | Some directory -> Directory.GetFiles(directory, "*.dll")
                | None ->
                    (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") :?> string).Split(Path.PathSeparator)
                    |> Array.filter (fun path -> path.StartsWith(runtime, StringComparison.Ordinal))
            files |> Array.map (fun path -> MetadataReference.CreateFromFile path :> MetadataReference)

    let private parseOptions =
        let symbols =
            [ "RELEASE"; "TRACE"; "NET"; "NETCOREAPP"; $"NET{major}_0" ]
            @ [ for version in 5 .. major -> $"NET{version}_0_OR_GREATER" ]
            @ [ "NETCOREAPP1_0_OR_GREATER"; "NETCOREAPP1_1_OR_GREATER"; "NETCOREAPP2_0_OR_GREATER"; "NETCOREAPP2_1_OR_GREATER"
                "NETCOREAPP2_2_OR_GREATER"; "NETCOREAPP3_0_OR_GREATER"; "NETCOREAPP3_1_OR_GREATER" ]
        CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols = symbols)

    let private compilationOptions =
        CSharpCompilationOptions(
            OutputKind.ConsoleApplication,
            optimizationLevel = OptimizationLevel.Release,
            nullableContextOptions = NullableContextOptions.Enable,
            generalDiagnosticOption = ReportDiagnostic.Error,
            warningLevel = major,
            deterministic = true,
            // The SDK's default NoWarn.
            specificDiagnosticOptions = dict [ "CS1701", ReportDiagnostic.Suppress; "CS1702", ReportDiagnostic.Suppress ])

    /// Compiles `sources` (file name, text) into `<directory>/Generated.dll` with its runtime
    /// configuration and returns the assembly path. Errors are reported in the compiler's
    /// `file(line,col): error CSnnnn: message` form, one per line.
    let compile (directory: string) (sources: (string * string) list) (timeoutMs: int) =
        use cancellation = new CancellationTokenSource(timeoutMs)
        try
            let trees =
                sources |> List.map (fun (name, text) ->
                    CSharpSyntaxTree.ParseText(text, parseOptions, Path.Combine(directory, name), Text.Encoding.UTF8, cancellation.Token))
            let compilation = CSharpCompilation.Create("Generated", trees, references.Force(), compilationOptions)
            let assembly = Path.Combine(directory, "Generated.dll")
            let result =
                use peStream = File.Create(assembly)
                use pdbStream = File.Create(Path.Combine(directory, "Generated.pdb"))
                compilation.Emit(peStream, pdbStream, options = EmitOptions(debugInformationFormat = DebugInformationFormat.PortablePdb), cancellationToken = cancellation.Token)
            if not result.Success then
                result.Diagnostics
                |> Seq.filter (fun item -> item.Severity = DiagnosticSeverity.Error)
                |> Seq.map string
                |> String.concat "\n"
                |> Rejected
                |> Error
            else
                File.WriteAllText(
                    Path.Combine(directory, "Generated.runtimeconfig.json"),
                    "{\"runtimeOptions\":{\"tfm\":\"net" + string major + ".0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"" + string major + ".0.0\"}}}")
                Ok assembly
        with :? OperationCanceledException -> Error TimedOut
