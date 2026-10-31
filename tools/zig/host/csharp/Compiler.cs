using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

internal static class Compiler
{
    private sealed record Source(string name, string text);
    private static readonly int Major = Environment.Version.Major;
    private static readonly string Runtime = RuntimeEnvironment.GetRuntimeDirectory();
    private static readonly string[] ReferencePaths = FindReferences();
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest, preprocessorSymbols:
        new[] { "RELEASE", "TRACE", "NET", "NETCOREAPP", $"NET{Major}_0" }
        .Concat(Enumerable.Range(5, Math.Max(0, Major - 4)).Select(v => $"NET{v}_0_OR_GREATER"))
        .Concat(new[] { "NETCOREAPP1_0_OR_GREATER", "NETCOREAPP1_1_OR_GREATER", "NETCOREAPP2_0_OR_GREATER",
            "NETCOREAPP2_1_OR_GREATER", "NETCOREAPP2_2_OR_GREATER", "NETCOREAPP3_0_OR_GREATER", "NETCOREAPP3_1_OR_GREATER" }));
    private static readonly CSharpCompilationOptions CompilationOptions = new(OutputKind.ConsoleApplication,
        optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable,
        generalDiagnosticOption: ReportDiagnostic.Error, warningLevel: Major, deterministic: true,
        specificDiagnosticOptions: ImmutableDictionary<string, ReportDiagnostic>.Empty
            .Add("CS1701", ReportDiagnostic.Suppress).Add("CS1702", ReportDiagnostic.Suppress));

    private static string[] FindReferences()
    {
        string root = new DirectoryInfo(Runtime).Parent!.Parent!.Parent!.FullName;
        string packs = Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref");
        string? pack = Directory.Exists(packs) ? Directory.GetDirectories(packs)
            .Select(path => (path, version: Version.TryParse(Path.GetFileName(path), out var v) ? v : null))
            .Where(item => item.version?.Major == Major && Directory.Exists(Path.Combine(item.path, "ref", $"net{Major}.0")))
            .OrderByDescending(item => item.version).Select(item => Path.Combine(item.path, "ref", $"net{Major}.0"))
            .FirstOrDefault() : null;
        return pack is not null ? Directory.GetFiles(pack, "*.dll") :
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => path.StartsWith(Runtime, StringComparison.Ordinal)).ToArray();
    }

    private static string Identity()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        void AddFile(string path)
        {
            Add(path);
            using var stream = File.OpenRead(path);
            hash.AppendData(SHA256.HashData(stream));
        }
        Add($"runtime={Environment.Version};language={LanguageVersion.Latest};release;nullable;warnings-as-errors;warning-level={Major};deterministic;CS1701,CS1702=suppress;portable-pdb");
        AddFile(typeof(Compiler).Assembly.Location);
        AddFile(typeof(CSharpCompilation).Assembly.Location);
        AddFile(typeof(Compilation).Assembly.Location);
        foreach (string path in ReferencePaths) AddFile(path);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string BuildContextFingerprint()
    {
        string identity = $"compiler={typeof(CSharpCompilation).Assembly.GetName().Version}\nlanguage={LanguageVersion.Latest}\nruntime={Environment.Version}\noptions=release,nullable,warnaserror,{CompilationOptions.WarningLevel}\nreferences=\n{string.Join("\n", ReferencePaths)}";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, identity.Length);
        hash.AppendData(length);
        hash.AppendData(Encoding.Unicode.GetBytes(identity));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static int Compile(string manifest, string directory)
    {
        var sources = JsonSerializer.Deserialize<Source[]>(File.ReadAllText(manifest)) ?? throw new InvalidDataException("Missing sources.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var trees = sources.Select(source => CSharpSyntaxTree.ParseText(source.text, ParseOptions,
                Path.Combine(directory, source.name), Encoding.UTF8, cancellation.Token)).ToArray();
            var references = ReferencePaths.Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("Generated", trees, references, CompilationOptions);
            using var assembly = new MemoryStream();
            using var symbols = new MemoryStream();
            var result = compilation.Emit(assembly, symbols, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
                cancellationToken: cancellation.Token);
            if (!result.Success)
            {
                var diagnostics = new StringBuilder();
                foreach (var diagnostic in result.Diagnostics)
                {
                    if (diagnostic.Severity != DiagnosticSeverity.Error) continue;
                    if (diagnostics.Length != 0) diagnostics.Append('\n');
                    string text = diagnostic.ToString();
                    int count = Math.Min(text.Length, 4095 - diagnostics.Length);
                    diagnostics.Append(text, 0, count);
                    if (diagnostics.Length >= 4095) break;
                }
                // The console's UTF-8 encoder can buffer a final unmatched surrogate until exit.
                if (diagnostics.Length != 0 && char.IsHighSurrogate(diagnostics[^1])) diagnostics[^1] = '\uFFFD';
                Console.Write(diagnostics);
                return 1;
            }
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "Generated.dll"), assembly.ToArray());
            File.WriteAllBytes(Path.Combine(directory, "Generated.pdb"), symbols.ToArray());
            File.WriteAllText(Path.Combine(directory, "Generated.runtimeconfig.json"),
                "{\"runtimeOptions\":{\"tfm\":\"net" + Major + ".0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"" + Major + ".0.0\"}}}");
            return 0;
        }
        catch (OperationCanceledException) { return 2; }
    }

    private static int Main(string[] args)
    {
        try
        {
            if (args is ["identity"])
            {
                Console.Write(JsonSerializer.Serialize(new { identity = Identity(), major = Major, runtimeVersion = Environment.Version.ToString(), buildContextFingerprint = BuildContextFingerprint() }));
                return 0;
            }
            if (args is ["compile", var manifest, var directory]) return Compile(manifest, directory);
            Console.Error.WriteLine("Usage: Compiler identity | compile <manifest.json> <outdir>");
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
    }
}
