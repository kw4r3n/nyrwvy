namespace Nyrwvy.Tools.AuthoringSession

open System
open System.IO
open System.Threading
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.Emit

/// Compiles a generated runner in this process with Roslyn, with the options `dotnet build -c Release`
/// gave the generated project: an executable for the running .NET major version, nullable enabled
/// and warnings as errors. Only compilation moves in process; the program still runs in a child
/// process, the session's runner worker.
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

    let private writeRuntimeConfig directory =
        File.WriteAllText(
            Path.Combine(directory, "Generated.runtimeconfig.json"),
            "{\"runtimeOptions\":{\"tfm\":\"net" + string major + ".0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"" + string major + ".0.0\"}}}")

    /// Compiles `sources` (file name, text) into an assembly and its portable PDB in memory. Errors are
    /// reported in the compiler's `file(line,col): error CSnnnn: message` form, one per line, with the
    /// file names placed in `directory`.
    let emit (directory: string) (sources: (string * string) list) (timeoutMs: int) =
        use cancellation = new CancellationTokenSource(timeoutMs)
        try
            let trees =
                sources |> List.map (fun (name, text) ->
                    CSharpSyntaxTree.ParseText(text, parseOptions, Path.Combine(directory, name), Text.Encoding.UTF8, cancellation.Token))
            let compilation = CSharpCompilation.Create("Generated", trees, references.Force(), compilationOptions)
            use peStream = new MemoryStream()
            use pdbStream = new MemoryStream()
            let result =
                compilation.Emit(peStream, pdbStream, options = EmitOptions(debugInformationFormat = DebugInformationFormat.PortablePdb), cancellationToken = cancellation.Token)
            if not result.Success then
                result.Diagnostics
                |> Seq.filter (fun item -> item.Severity = DiagnosticSeverity.Error)
                |> Seq.map string
                |> String.concat "\n"
                |> Rejected
                |> Error
            else Ok(peStream.ToArray(), pdbStream.ToArray())
        with :? OperationCanceledException -> Error TimedOut

    /// Writes a built assembly, its PDB and a runtime configuration into `directory` and returns the
    /// assembly path.
    let install directory (assembly: byte[], symbols: byte[]) =
        let path = Path.Combine(directory, "Generated.dll")
        File.WriteAllBytes(path, assembly)
        File.WriteAllBytes(Path.Combine(directory, "Generated.pdb"), symbols)
        writeRuntimeConfig directory
        path

    /// Compiles `sources` (file name, text) into `<directory>/Generated.dll` with its runtime
    /// configuration and returns the assembly path. Errors are reported in the compiler's
    /// `file(line,col): error CSnnnn: message` form, one per line.
    let compile (directory: string) (sources: (string * string) list) (timeoutMs: int) =
        emit directory sources timeoutMs |> Result.map (install directory)

    /// A bounded least-recently-used store of compiled assemblies, keyed by build fingerprint. It
    /// holds at most `capacity` entries and `maxBytes` bytes of assembly and PDB; adding an entry
    /// evicts the least recently used ones until both bounds hold. An entry larger than `maxBytes`
    /// is not kept. Safe for concurrent use.
    type BuildCache(capacity: int, maxBytes: int64) =
        let entries = Collections.Generic.LinkedList<string * byte[] * byte[]>()
        let index = Collections.Generic.Dictionary<string, Collections.Generic.LinkedListNode<string * byte[] * byte[]>>(StringComparer.Ordinal)
        let mutable bytes = 0L
        let size (_, assembly: byte[], symbols: byte[]) = int64 assembly.Length + int64 symbols.Length
        member _.TryGet(key: string) =
            lock entries (fun () ->
                match index.TryGetValue key with
                | true, node ->
                    entries.Remove node
                    entries.AddFirst node
                    let _, assembly, symbols = node.Value
                    Some(assembly, symbols)
                | _ -> None)
        member _.Add(key: string, assembly: byte[], symbols: byte[]) =
            lock entries (fun () ->
                match index.TryGetValue key with
                | true, node ->
                    bytes <- bytes - size node.Value
                    entries.Remove node
                    index.Remove key |> ignore
                | _ -> ()
                let entry = key, assembly, symbols
                if size entry <= maxBytes && capacity > 0 then
                    index[key] <- entries.AddFirst entry
                    bytes <- bytes + size entry
                    while entries.Count > capacity || bytes > maxBytes do
                        let last = entries.Last
                        let lastKey, _, _ = last.Value
                        bytes <- bytes - size last.Value
                        entries.RemoveLast()
                        index.Remove lastKey |> ignore)
        member _.Contains(key: string) = lock entries (fun () -> index.ContainsKey key)
        member _.Count = lock entries (fun () -> entries.Count)
        member _.Bytes = lock entries (fun () -> bytes)

    /// The runPure build cache of this session process: 32 assemblies or 64 MiB, whichever is reached first.
    let cache = BuildCache(32, 64L * 1024L * 1024L)

    /// Everything besides the sources that shapes the build: the compiler version, the language and
    /// runtime versions, and the reference set. Options are fixed in this module, so its version covers them.
    let private buildIdentity =
        lazy
            let compiler = typeof<CSharpCompilation>.Assembly.GetName().Version
            let referencePaths = references.Force() |> Array.map (fun reference -> reference.Display) |> String.concat "\n"
            $"compiler={compiler}\nlanguage={LanguageVersion.Latest}\nruntime={Environment.Version}\noptions=release,nullable,warnaserror,{compilationOptions.WarningLevel}\nreferences=\n{referencePaths}"

    /// The build fingerprint: SHA-256 over the build identity and every source's name and text.
    let fingerprint (sources: (string * string) list) =
        use hash = Security.Cryptography.IncrementalHash.CreateHash(Security.Cryptography.HashAlgorithmName.SHA256)
        let add (text: string) =
            hash.AppendData(BitConverter.GetBytes(text.Length))
            hash.AppendData(Runtime.InteropServices.MemoryMarshal.AsBytes(text.AsSpan()))
        add (buildIdentity.Force())
        for name, text in sources do
            add name
            add text
        Convert.ToHexString(hash.GetHashAndReset())

    /// The outcome of the last cached build on this thread: the fingerprint and whether it was a hit.
    let lastLookup = new ThreadLocal<(string * bool) option>(fun () -> None)

    /// Like `compile`, but reuses an assembly this process already built from the same fingerprint.
    /// Failed builds are not cached, so their diagnostics always name this build's directory.
    let compileCached (directory: string) (sources: (string * string) list) (timeoutMs: int) =
        let key = fingerprint sources
        match cache.TryGet key with
        | Some built ->
            lastLookup.Value <- Some(key, true)
            Ok(install directory built)
        | None ->
            lastLookup.Value <- Some(key, false)
            emit directory sources timeoutMs
            |> Result.map (fun (assembly, symbols) ->
                cache.Add(key, assembly, symbols)
                install directory (assembly, symbols))
