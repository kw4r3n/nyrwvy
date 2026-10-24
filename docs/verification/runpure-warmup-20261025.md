# runPure warm-up at session start — 2026-10-25

**Status:** Implemented. Baseline `a5b6f0b`.

The first `runPure` of a session process used to pay three costs at once: Roslyn start-up, the first compile of the generated runtime support, and building and starting the runner worker. When the `Nyrwvy.AuthoringSession` process opens a session, a background task now pays them in advance (`PureRun.warm`):

1. It emits an empty program, which is the generated runtime support alone, and compiles it with the runner helpers through `GeneratedBuild.compile`, the same path a run takes.
2. It starts the worker (`PureRunWorker.Warm`).

The task reads no session state, so it can run beside requests. A failure is ignored and leaves those steps to the first run. `NYRWVY_RUNPURE_WARMUP=0` turns it off. Sessions created in-process (tests, library use) are unchanged.

## Measurement

`runPureByName` through a fresh session process over JSON lines, with 3 s of idle time between opening and the first request (an agent reads and authors before its first run). Four small entries of a scratch snapshot, three rounds, load average 1.5–2.5. The script (`bench.mjs`) lives on the local IR interpreter prototype branch, which is not published.

| | Warm-up off | Warm-up on |
| --- | --- | --- |
| First call of the process | 1,795–1,906 ms | 472–477 ms |
| First call of a new entry | 180–262 ms | 194–261 ms |
| Repeated call (median) | 2.4–8.7 ms | 2.4–8.4 ms |

An earlier prototype warmed with a trivial C# program and reached 0.66 s. Compiling the real runtime support also warms the binding and emission code a generated program uses, so this version reaches 0.47 s.

## Considered and not done here

- **Building on commit.** Building an entry's runner right after a commit would hide the 160–260 ms build of a new entry. But the runner source depends on the argument types and on which parameters are fakes, and the source construction is interleaved with argument handling in `runVerified`. That would need restructuring, plus a background read of session state that is not thread-safe today. Not a small change.
- **Prebuilding the runtime support once per session** (a referenced assembly in place of the source concatenated into every program). Compiling the runtime support (about 51,000 characters) takes about 150 ms of Roslyn time after warm-up, against about 5 ms for a trivial program. So it accounts for most of a new entry's 160–260 ms build. Sharing it needs:
  - an emitter split between runtime and program;
  - a variant per runtime shape (with or without concurrency);
  - loading in the worker that keeps per-run statics (`NyRuntime.Clock`, cancellation) isolated while sharing the JIT-compiled runtime.

  That is a design change, estimated at a day or more, and is left for its own decision.
