namespace Nyrwvy.Tools.AuthoringSession

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// What became of one runner program handed to the worker.
[<RequireQualifiedAccess>]
type internal PureRunWorkerOutcome =
    /// The program's entry point returned; `output` is everything it wrote to standard output.
    | Completed of output: string
    /// The program or the worker died outside the program's own reporting, as a crashed child
    /// process would: `errors` is what the runtime or the worker wrote about it.
    | Crashed of output: string * errors: string
    /// A runner limit or failure, as a runPure diagnostic code and message.
    | Failed of code: string * message: string

/// The worker program. It reads one request per line from standard input, `["run", assembly, pdb,
/// input]`, and answers with one line: `["ok", output]` when the program's entry point returned,
/// `["crash", output, text]` when it threw, or `["refused", message]` when the assembly could not be
/// loaded. The entry point runs on the worker's main thread, so it has the stack a child process's
/// main thread has, with the input path as its only argument and standard output captured.
///
/// Every run loads its assembly into a fresh AssemblyLoadContext, so the program starts with fresh
/// statics and its code is compiled as a fresh child compiled it, at the JIT's first tier without
/// inlining, so the frames a runtime failure reports and the stack depth a recursion reaches are the
/// same. The context is not collectible: the runtime compiles collectible code fully optimized from
/// the start, which inlined generated functions out of the reported frames. Loaded programs
/// therefore stay in memory, and the session replaces the worker after a bounded number of runs.
/// The main thread's ExecutionContext is restored between runs so no AsyncLocal value survives.
/// Standard input closing ends the worker.
module internal PureRunWorkerProgram =
    let source =
        String.concat "\n" [
            "using System;"
            "using System.IO;"
            "using System.Reflection;"
            "using System.Runtime.InteropServices;"
            "using System.Runtime.Loader;"
            "using System.Text;"
            "using System.Text.Json;"
            "using System.Threading;"
            ""
            "static class RunnerWorker"
            "{"
            "    [DllImport(\"libc\")] static extern int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);"
            // One more character than the session's 1 MiB output limit, so the limit check sees the overflow.
            "    const int OutputLimit = 1048577;"
            ""
            "    static int Main()"
            "    {"
            // A stack overflow aborts the worker; where the OS captures core dumps, writing one would
            // delay the exit by seconds and consume the run's time limit, so the worker opts out first.
            "        if (OperatingSystem.IsLinux()) prctl(4 /* PR_SET_DUMPABLE */, 0, 0, 0, 0);"
            "        var replies = Console.Out;"
            "        var requests = Console.In;"
            "        var pristine = ExecutionContext.Capture()!;"
            "        while (true)"
            "        {"
            "            var line = requests.ReadLine();"
            "            if (line == null) return 0;"
            "            string reply;"
            "            using (var request = JsonDocument.Parse(line))"
            "            {"
            "                var items = request.RootElement;"
            "                reply = Run(items[1].GetString()!, items[2].GetString()!, items[3].GetString()!);"
            "            }"
            "            ExecutionContext.Restore(pristine);"
            "            replies.WriteLine(reply);"
            "            replies.Flush();"
            "        }"
            "    }"
            ""
            "    static string Run(string assemblyPath, string symbolsPath, string inputPath)"
            "    {"
            "        var context = new AssemblyLoadContext(\"nyrwvy-pure-run\", isCollectible: false);"
            "        var output = new BoundedWriter(OutputLimit);"
            "        var previous = Console.Out;"
            "        try"
            "        {"
            "            MethodInfo entry;"
            "            try"
            "            {"
            "                using var assemblyStream = File.OpenRead(assemblyPath);"
            "                using var symbolsStream = File.OpenRead(symbolsPath);"
            "                entry = context.LoadFromStream(assemblyStream, symbolsStream).EntryPoint ?? throw new BadImageFormatException(\"The runner has no entry point.\");"
            "            }"
            "            catch (Exception error) when (error is IOException or BadImageFormatException or UnauthorizedAccessException)"
            "            {"
            "                return JsonSerializer.Serialize(new object[] { \"refused\", error.Message });"
            "            }"
            "            Console.SetOut(output);"
            "            try"
            "            {"
            "                var arguments = entry.GetParameters().Length == 0 ? Array.Empty<object>() : new object[] { new[] { inputPath } };"
            "                entry.Invoke(null, arguments);"
            "                return JsonSerializer.Serialize(new object[] { \"ok\", output.Text });"
            "            }"
            "            catch (TargetInvocationException error)"
            "            {"
            "                return JsonSerializer.Serialize(new object[] { \"crash\", output.Text, \"Unhandled exception. \" + (error.InnerException ?? error).ToString() });"
            "            }"
            "        }"
            "        finally"
            "        {"
            "            Console.SetOut(previous);"
            "        }"
            "    }"
            "}"
            ""
            // Keeps the first `limit` characters written; anything beyond is dropped.
            "sealed class BoundedWriter(int limit) : TextWriter"
            "{"
            "    readonly StringBuilder text = new();"
            "    public override Encoding Encoding => Encoding.UTF8;"
            "    public string Text => text.ToString();"
            "    public override void Write(char value) { if (text.Length < limit) text.Append(value); }"
            "    public override void Write(string? value) { if (value != null) Write(value.AsSpan()); }"
            "    public override void Write(char[] buffer, int index, int count) => Write(buffer.AsSpan(index, count));"
            "    public override void Write(ReadOnlySpan<char> buffer)"
            "    {"
            "        int room = limit - text.Length;"
            "        if (room > 0) text.Append(buffer.Length <= room ? buffer : buffer.Slice(0, room));"
            "    }"
            "}" ]

    /// The worker built once per session process; a failure is the compiler's diagnostics.
    let assembly =
        lazy
            match GeneratedBuild.emit (Path.Combine(Path.GetTempPath(), "nyrwvy-pure-run-worker")) [ "Worker.cs", source ] 30000 with
            | Ok built -> Ok built
            | Error GeneratedBuild.TimedOut -> Error "The worker build exceeded its time limit of 30 s."
            | Error(GeneratedBuild.Rejected errors) -> Error errors

type private PureRunWorkerProcess =
    { Process: Process
      /// Everything the worker writes to standard error, available once it has exited.
      Errors: Task<string>
      Root: string }

/// A long-lived child process that runs compiled runner programs for one session, so a run skips
/// process start, runtime initialization and the compilation of framework code. Runs are serialized.
/// The process starts on the first run, is killed and replaced after a timeout or a crash and after
/// `runsPerWorker` runs (each run's program stays loaded in it), is killed after ten minutes without a
/// run, and dies with the session (or with this process, whose standard input it holds).
type internal PureRunWorker() =
    let gate = obj ()
    let mutable current: PureRunWorkerProcess option = None
    let mutable disposed = false
    let mutable lastRun = Stopwatch.GetTimestamp()
    let mutable served = 0
    let idleLimit = TimeSpan.FromMinutes 10.0
    /// Programs stay loaded in a worker, so it is replaced after this many runs.
    let runsPerWorker = 64

    let deleteRoot root =
        if Directory.Exists root then
            try Directory.Delete(root, true)
            with
            | :? IOException
            | :? UnauthorizedAccessException -> ()

    let kill (worker: PureRunWorkerProcess) =
        try worker.Process.Kill(true)
        with
        | :? InvalidOperationException
        | :? System.ComponentModel.Win32Exception -> ()

    /// Kills the current worker, waits for it and removes its directory; false when it did not exit.
    let retire () =
        match current with
        | None -> true
        | Some worker ->
            current <- None
            kill worker
            let exited = worker.Process.WaitForExit 5000
            worker.Process.Dispose()
            deleteRoot worker.Root
            exited

    let start () =
        match PureRunWorkerProgram.assembly.Force() with
        | Error message -> Error("RunStartupFailure", "The runner worker could not be built: " + message)
        | Ok built ->
            let root = Path.Combine(Path.GetTempPath(), "nyrwvy-pure-run-worker-" + Guid.NewGuid().ToString("N"))
            try
                Directory.CreateDirectory root |> ignore
                let path = GeneratedBuild.install root built
                let info = ProcessStartInfo("dotnet")
                info.WorkingDirectory <- root
                info.RedirectStandardInput <- true
                info.RedirectStandardOutput <- true
                info.RedirectStandardError <- true
                info.ArgumentList.Add path
                let child = Process.Start info
                if isNull child then
                    deleteRoot root
                    Error("RunStartupFailure", "Could not start dotnet.")
                else
                    let worker = { Process = child; Errors = child.StandardError.ReadToEndAsync(); Root = root }
                    current <- Some worker
                    served <- 0
                    Ok worker
            with
            | :? System.ComponentModel.Win32Exception as error ->
                deleteRoot root
                Error("RunStartupFailure", error.Message)
            | :? IOException as error ->
                deleteRoot root
                Error("RunIoFailure", error.Message)
            | :? UnauthorizedAccessException as error ->
                deleteRoot root
                Error("RunIoFailure", error.Message)

    let ensure () =
        match current with
        | Some worker when not worker.Process.HasExited && served < runsPerWorker -> Ok worker
        | _ ->
            retire () |> ignore
            start ()

    /// The worker's answer line as an outcome; a worker that answers nonsense is replaced.
    let parseReply (line: string) =
        try
            use document = JsonDocument.Parse line
            let root = document.RootElement
            let text (index: int) = root[index].GetString()
            match text 0, root.GetArrayLength() with
            | "ok", 2 -> Some(PureRunWorkerOutcome.Completed(text 1))
            | "crash", 3 -> Some(PureRunWorkerOutcome.Crashed(text 1, text 2))
            | "refused", 2 -> Some(PureRunWorkerOutcome.Crashed("", text 1))
            | _ -> None
        with
        | :? JsonException
        | :? InvalidOperationException
        | :? IndexOutOfRangeException -> None

    /// What a worker that died during a run reported: its standard error, read once it has exited.
    let died (worker: PureRunWorkerProcess) =
        current <- None
        let exited = worker.Process.WaitForExit 5000
        let outcome =
            if not exited || not (worker.Errors.Wait 5000) then
                PureRunWorkerOutcome.Failed("RunOutputTimeout", "The process exited but its output streams did not close.")
            else PureRunWorkerOutcome.Crashed("", worker.Errors.Result)
        if not exited then kill worker
        worker.Process.Dispose()
        deleteRoot worker.Root
        outcome

    // Retires a worker that has been idle for the limit; the gate keeps it clear of a run.
    let idle =
        new Timer((fun _ ->
            if Monitor.TryEnter gate then
                try
                    if not disposed && current.IsSome && Stopwatch.GetElapsedTime lastRun > idleLimit then retire () |> ignore
                finally Monitor.Exit gate), null, TimeSpan.FromMinutes 1.0, TimeSpan.FromMinutes 1.0)

    /// Runs the program at `assemblyPath` (with its PDB at `symbolsPath`) on `inputPath` within
    /// `timeoutMs`. `phase` names the timed step in the timeout diagnostic. A timeout or a crash retires
    /// the worker; the next run starts another.
    /// Builds and starts the worker ahead of the first run.
    member _.Warm() = lock gate (fun () -> ensure () |> ignore)

    member _.Run(phase: string, assemblyPath: string, symbolsPath: string, inputPath: string, timeoutMs: int) =
        lock gate (fun () ->
            lastRun <- Stopwatch.GetTimestamp()
            if disposed then PureRunWorkerOutcome.Failed("RunStartupFailure", "The session's runner worker is closed.") else
            match ensure () with
            | Error(code, message) -> PureRunWorkerOutcome.Failed(code, message)
            | Ok worker ->
                served <- served + 1
                try
                    worker.Process.StandardInput.WriteLine(JsonSerializer.Serialize [| "run"; assemblyPath; symbolsPath; inputPath |])
                    worker.Process.StandardInput.Flush()
                    let reply = worker.Process.StandardOutput.ReadLineAsync()
                    if not (reply.Wait timeoutMs) then
                        if retire () then PureRunWorkerOutcome.Failed("RunTimeout", $"The {phase} exceeded its time limit of {timeoutMs / 1000} s.")
                        else PureRunWorkerOutcome.Failed("RunReapFailure", "The timed-out child has not been confirmed terminated.")
                    else
                        match reply.Result with
                        | null -> died worker
                        | line ->
                            match parseReply line with
                            | Some outcome -> outcome
                            | None ->
                                retire () |> ignore
                                PureRunWorkerOutcome.Failed("RunInvalidResult", "The runner worker did not answer with one result line.")
                with
                | :? IOException as error ->
                    retire () |> ignore
                    PureRunWorkerOutcome.Failed("RunIoFailure", error.Message)
                | :? AggregateException as error ->
                    retire () |> ignore
                    PureRunWorkerOutcome.Failed("RunIoFailure", error.Message)
                | :? ObjectDisposedException as error ->
                    retire () |> ignore
                    PureRunWorkerOutcome.Failed("RunIoFailure", error.Message))

    /// The worker's directory while one is alive.
    member _.Directory = lock gate (fun () -> current |> Option.filter (fun worker -> not worker.Process.HasExited) |> Option.map (fun worker -> worker.Root))

    /// The worker process ID while one is alive.
    member _.ProcessId = lock gate (fun () -> current |> Option.filter (fun worker -> not worker.Process.HasExited) |> Option.map (fun worker -> worker.Process.Id))

    member this.Dispose() =
        lock gate (fun () ->
            if not disposed then
                disposed <- true
                idle.Dispose()
                retire () |> ignore)
        GC.SuppressFinalize this

    /// A session that was never closed still takes its worker down with it.
    override _.Finalize() =
        match current with
        | Some worker -> kill worker
        | None -> ()

    interface IDisposable with
        member this.Dispose() = this.Dispose()
