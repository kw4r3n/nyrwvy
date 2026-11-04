using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Threading;

static class RunnerWorker
{
    [DllImport("libc")] static extern int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);
    const int OutputLimit = 1048577;

    static int Main()
    {
        if (OperatingSystem.IsLinux())
        {
            prctl(4 /* PR_SET_DUMPABLE */, 0, 0, 0, 0);
            // Killed with the host even when it is SIGKILLed and cannot kill its process group.
            prctl(1 /* PR_SET_PDEATHSIG */, 9 /* SIGKILL */, 0, 0, 0);
        }
        var replies = Console.Out;
        var requests = Console.In;
        replies.WriteLine("[\"ready\"]");
        replies.Flush();
        var pristine = ExecutionContext.Capture()!;
        while (true)
        {
            var line = requests.ReadLine();
            if (line == null) return 0;
            string reply;
            using (var request = JsonDocument.Parse(line))
            {
                var items = request.RootElement;
                reply = Run(items[1].GetString()!, items[2].GetString()!, items[3].GetString()!);
            }
            ExecutionContext.Restore(pristine);
            replies.WriteLine(reply);
            replies.Flush();
        }
    }

    static string Run(string assemblyPath, string symbolsPath, string inputPath)
    {
        var context = new AssemblyLoadContext("nyrwvy-pure-run", isCollectible: false);
        var output = new BoundedWriter(OutputLimit);
        var previous = Console.Out;
        try
        {
            MethodInfo entry;
            try
            {
                using var assemblyStream = File.OpenRead(assemblyPath);
                using var symbolsStream = File.OpenRead(symbolsPath);
                entry = context.LoadFromStream(assemblyStream, symbolsStream).EntryPoint ?? throw new BadImageFormatException("The runner has no entry point.");
            }
            catch (Exception error) when (error is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                return JsonSerializer.Serialize(new object[] { "refused", error.Message });
            }
            Console.SetOut(output);
            try
            {
                var arguments = entry.GetParameters().Length == 0 ? Array.Empty<object>() : new object[] { new[] { inputPath } };
                entry.Invoke(null, arguments);
                return JsonSerializer.Serialize(new object[] { "ok", output.Text });
            }
            catch (TargetInvocationException error)
            {
                return JsonSerializer.Serialize(new object[] { "crash", output.Text, "Unhandled exception. " + (error.InnerException ?? error).ToString() });
            }
        }
        finally
        {
            Console.SetOut(previous);
        }
    }
}

sealed class BoundedWriter(int limit) : TextWriter
{
    readonly StringBuilder text = new();
    public override Encoding Encoding => Encoding.UTF8;
    public string Text => text.ToString();
    public override void Write(char value) { if (text.Length < limit) text.Append(value); }
    public override void Write(string? value) { if (value != null) Write(value.AsSpan()); }
    public override void Write(char[] buffer, int index, int count) => Write(buffer.AsSpan(index, count));
    public override void Write(ReadOnlySpan<char> buffer)
    {
        int room = limit - text.Length;
        if (room > 0) text.Append(buffer.Length <= room ? buffer : buffer.Slice(0, room));
    }
}