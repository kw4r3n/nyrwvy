# runPure Release build, Roslyn build and early StringSlice — 2026-09-30

**Status:** Implemented and verified. Baseline: `0dda240`.

The rrawrlet slice probes reported that each `runPure` spent about 3 s before the program ran, and that position-walking scans were slow. Three small changes address the cheapest parts:

- The generated program is built in the **Release** configuration. The run was built in Debug, so the 5 s limit measured unoptimized JIT code.
- The generated project never changes, so its **package restore runs once per session process**. Each build copies that `obj` and uses `--no-restore`; a failed restore is not kept, and that build restores on its own.
- **`StringSlice` stops walking at its end bound** instead of walking to the end of the whole text.

Stack overflow reporting, frames, the 5 s limit, and the child-process isolation are unchanged.

## Measurements

These were measured on one Linux machine. The probe snapshots were not available locally, so the probe 11 transcript rows were not re-run.

| What | Before | After |
| --- | --- | --- |
| Build of a trivial generated project (restore vs `--no-restore`) | 1.31 s | 0.90 s |
| 8 runPure-heavy Core tests (StringCodePoints, StringSpan, StringSearch, PureRunProvenance), about 25 runs | 21.1–21.4 s | 16.1 s |
| Per-code-point slice scan over 16,000 spaces (emitted slice loop) | 1,763 ms (Debug) | 152 ms (Release, early stop) |
| Linear fold over code points, 4.0 M chars (list of one-code-point Strings) | 289 ms (Debug) | 240 ms (Release) |

The scan row is a microbenchmark of the emitted slice loop. Release makes it about 6 times faster, and the early stop halves it again. A scan that steps a position is still O(n²); only the constant changed. Allocation-bound work, such as `StringCodePoints` over a large text, gains little from Release.

## Verification

- `./scripts/test-fast.fish`: all Core tests and 29 adapter tests passed.
- `./scripts/test-extended.fish`: 31 Extended and 72 extended Core tests passed. These include the stack overflow frame mapping in `PureRunProvenanceTests`.

## Follow-up: in-process Roslyn build

The shared restore was then replaced by compiling in the session process with Roslyn (`GeneratedBuild.fs`, Microsoft.CodeAnalysis.CSharp 5.0.0). It uses the options of the Release `dotnet build`: optimized, nullable enabled, warnings as errors (the SDK's default `NoWarn` of CS1701 and CS1702 kept), the SDK's `DefineConstants`, against the reference pack of the running .NET version. The program still runs as a child process with the same 5 s limit, so stack overflow reporting and isolation are unchanged. The build keeps its 30 s limit through a cancellation token.

Per-call times, measured inside `runPure` over the same 8 tests (28 runs):

| Step | MSBuild with shared restore | Roslyn |
| --- | --- | --- |
| Build | about 0.9 s | 1.6–1.7 s on the first calls of a process (compiler warm-up), then 90–180 ms |
| Run | about 80 ms | 55–100 ms |

| Suite | Before (`0dda240`) | Shared restore | Roslyn |
| --- | --- | --- | --- |
| The 8 runPure-heavy tests | 21.1–21.4 s | 16.1 s | 6.7–6.9 s |
| Extended Core tests (72) | — | 1 m | 21 s |

The difference from `dotnet build`: the SDK's code analyzers (CA rules) no longer run, so a host source that only had an analyzer warning now builds. Compiler warnings still fail the build. There is no package restore, so a build no longer reaches the network. `GeneratedBuildTests` checks that errors, warnings and nullable warnings are reported with the file, position and code, and that a compiled runner runs.

## Not done

- Passing arguments as input and caching the built program by fingerprint.
- Code-point offset caching for positional String operations.
