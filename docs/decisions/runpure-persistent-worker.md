# A persistent runner process for `runPure`

**Status:** Accepted (2026-10-19)

## Context

`runPure`, `runPureByName` and `verifyChange` compile the selected program in process with Roslyn and keep the built assembly in a session cache ([build cache](../verification/runpure-build-cache-20261001.md)), so a repeated run of one entry skips the build. Every run then still started a fresh `dotnet Generated.dll input.txt` child process. On the cleanup-shield example (28 runs of a handful of builds) that child cost 80–200 ms per run, and almost none of it was the program: runtime start is ~25 ms, JIT of `System.Text.Json` and friends ~30 ms, and JIT of the generated runtime support (`NyList`, `NyStr`, the virtual clock) ~50 ms, while the program's own work takes about 1 ms. Together with the session side (projecting and emitting the entry, fingerprinting the current snapshot) a run took ~390 ms end to end against a budget where an agent runs dozens of cases per authoring step.

## Decision

Each `AuthoringSession` owns one long-lived **runner worker**: a small C# program, compiled once per session process with the same in-process Roslyn pipeline as the generated runners, started lazily with the first run and killed when the session closes. The session hands it one request per line on standard input (`["run", assembly, pdb, input]`) and reads one reply line from its standard output.

The worker loads each run's assembly into a **fresh `AssemblyLoadContext`**. It invokes the program's entry point **on its own main thread**, with the input path as the only argument and `Console.Out` captured, so the program keeps the stack a child process's main thread has and prints the same envelope it printed before. Framework code (the runtime, `System.Text.Json`, the console) is loaded and compiled once per worker; the program's own code is compiled per run exactly as a fresh child compiled it, at the JIT's first tier, without inlining, and with the tiering delay re-armed by that compilation, so the frames a runtime failure reports and the stack depth a recursion reaches are what they were.

The contexts are **not collectible**, and the session replaces the worker after 64 runs instead: the runtime compiles the code of a collectible assembly fully optimized from the start, which inlined a generated function out of the frames of a reported runtime failure. For the same reason a build is not kept loaded across runs of the same fingerprint, although a compiled program's own work takes ~1 ms: a loaded method may be promoted and inlined after enough calls, and a later run then reported fewer frames than the first; neither `DOTNET_TieredCompilation=0` (inlines from the start) nor `DOTNET_TC_CallCounting=0` kept the frames. Fresh code per run is the behaviour the diagnostics were specified against.

Everything the child process guaranteed is kept, by the same mechanism or an equivalent one:

- **Time limit.** The session waits 5 s for the reply line; on timeout it kills the worker and reports `RunTimeout` (or `RunReapFailure` when the worker does not die), exactly as for the child. The next run starts a new worker.
- **Stack overflow and crashes.** A stack overflow or an unhandled exception on a program thread kills the worker as it killed the child. The session reads the worker's standard error, reports the same `System.StackOverflowException` runtime failure with the frames the runtime printed, or the same `RunProcessFailure` otherwise, and starts a new worker for the next run.
- **Output limit.** The worker keeps at most 1 MiB (plus one character) of captured output; the session applies the same `RunOutputTooLarge` check.
- **Core dumps.** The worker opts out of core dumps with `PR_SET_DUMPABLE` once at start, as every generated program still does for itself.
- **Isolation between runs.** A fresh load context gives the program fresh statics (its fake call log, its virtual clock); the worker restores its main thread's `ExecutionContext` after every run so no `AsyncLocal` value (ambient cancellation, task order) survives. Structured concurrency joins every task before the entry returns, so no program thread outlives its run; a thread that did would hold only its own context.
- **Host sources.** A run with `hostSources` executes arbitrary C# that may change process-wide state (console, environment, thread pool), so it still runs in a fresh child process of its own.
- **Lifetime.** The worker dies with the session (`Dispose`), with the session process (it holds the worker's standard input), after ten minutes without a run, and after 64 runs (its loaded programs go with it); its temporary directory is removed with it, and the session's finalizer kills a worker whose session was never closed.

The same change made the session side cheaper: validation issues and the snapshot and semantic fingerprints of a snapshot are now retained for as long as that snapshot object lives (a weak table under the request scope), so the committed base is no longer re-validated and re-fingerprinted by every simulate, commit and run; an entry's execution projection (selection, validation and lowering of the entry's call graph) is kept with the workspace state it was projected from, so repeated runs of one entry project it once; union case names used in review summaries are read through precomputed tag readers instead of per-node reflection; and the recipe bounds check names subtrees only for a recipe that exceeds the limit.

## Consequences

- On the cleanup-shield example a run's execution step went from 80–200 ms (a child process) to 20–50 ms (loading and compiling the program in the worker), and the whole `runPureByName` round trip from ~380 ms to ~100 ms on the measuring machine, where the remaining session-side time is mostly C# emission while the session's own code is still being compiled by its JIT. The first run of a session pays for the worker's build (~200 ms warm, more cold) and start (~50 ms) once.
- Frames and stack capacity are those of freshly compiled code in every run, as in a short-lived child.
- A session process holds one more child process while runs are recent, about the size of an idle .NET console application plus up to 64 loaded programs. Tests that count processes or temporary directories see none after the session is disposed.
- The worker's protocol is private to the session: `runPure`'s request and response contracts, diagnostics and limits did not change.
