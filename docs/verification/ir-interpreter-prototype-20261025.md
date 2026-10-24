# Execution IR interpreter prototype — 2026-10-25

**Status:** Prototype on branch `claude/ir-interpreter-prototype` (base `a5b6f0b`). Not a decision; no ADR.

## Question

How much does running the Execution IR directly in the session process, instead of generating C#, building it with Roslyn and running it in the persistent worker, change `runPure` latency?

## What was built

- `src/Nyrwvy.Execution/ExecutionIrInterpreter.fs`: an interpreter over `ExecutionProgram`. Values are an F# union with structural equality matching `EqualityComparer<T>.Default` in the generated code. Lists are `ImmutableList`, and Map and Set mirror NyMap/NySet (insertion order kept by insertion numbers). NyText and NyNumber are ported line by line. Every instruction kind runs except the concurrency and task kinds (`ParallelAll`, `CancellationRequested`, `CancellationShield`, `Task*`, `OneShot*`).
- `PureRun.fs`: `NYRWVY_RUNPURE_EXECUTOR=interpreter` interprets when every reachable instruction is supported and every capability argument is a fake with immediate responses (`return`/`throw`), and otherwise falls back to the compiled path. `compare` runs both, returns the compiled result and appends `MATCH`/`DIFF`/`FALLBACK` to `NYRWVY_RUNPURE_COMPARE_LOG`. Emission still runs first, so validation and readiness are unchanged.
- In-process safety mirrors the runner: a dedicated thread with a 512 MiB stack and a 10,000-frame depth limit reported as `System.StackOverflowException`, a 5 s deadline checked at every block, the 8192-node/4096-item/32-layer/65536-unit result budget and the 1024-call limit. Running out of memory would still take down the session process.

## Parity

The suites ran in `compare` mode (Release, `dotnet test`):

| Suite | Tests | runPure calls | Match | Differ | Fell back |
| --- | --- | --- | --- | --- | --- |
| fast | 1133 passed | 68 | 66 | 0 | 2 |
| extended (Core + Extended) | 149 passed | 2415 | 2359 | 16 | 40 |

All 16 differences are limits, and no result value differed:

- 13 timeouts. These are heavy loops (10⁶ iterations, SHA-256) that finish in the compiled runner and exceed 5 s in the interpreter while the test run loads the machine.
- 3 stack overflows on both sides. Only the length of the frame list differs: the compiled runner reads it from the dump on stderr.

Error message text, frames of ordinary host exceptions, fake call logs and keyed fakes matched wherever the suites exercise them.

## Latency

`evaluation/ir-interpreter/bench.mjs` drives a fresh `Nyrwvy.AuthoringSession open --read-only` process per mode over JSON lines with `runPureByName`. Each entry gets 1 first call and then 10 calls with different arguments; three runs gave consistent results (`results.jsonl`). One Linux machine, load average around 7 from other work, 24 cores. Session startup itself was 440–450 ms in both modes.

| Entry | Compiled first call | Interpreter first call | Compiled steady (median) | Interpreter steady (median) |
| --- | --- | --- | --- | --- |
| `add(a, b)`, first entry of the process | 1,800–1,857 ms | 232–243 ms | 2.3–2.6 ms | 0.6–0.7 ms |
| `wordCount` (split, filter callback, ~55 words) | 162–165 ms | 29–30 ms | 5.4–6.1 ms | 1.3–1.4 ms |
| `sumTo(100000)` (fold over a range) | 265–355 ms | 356–371 ms | 8.7–8.9 ms | 147–153 ms |
| `fib(20)` (about 22,000 non-tail calls) | 184–263 ms | 66–69 ms | 3.2–3.5 ms | 76–81 ms |

- **First call**: the compiled path pays Roslyn warm-up on the first entry of a process (about 1.8 s) and 160–260 ms of build for each new program after that. The interpreter needs no build. Its first call costs about 230 ms of JIT for itself, then 30–70 ms for new entries.
- **Steady state**: when the work is small, the interpreter is 3–4 times faster, since it needs no worker round trip or result envelope (the build cache already makes repeated compiled calls cheap). On loops and recursion this prototype is 17–25 times slower, at about 1.5 µs per fold iteration and 3.6 µs per call.

The interpreter is naive. Every instruction looks values up in a `Dictionary` by string ID, and every call allocates a new dictionary and closures. Compiling slots in advance would make it faster by an unmeasured factor. Even so, a tree-walking interpreter is not expected to match JIT-compiled C# on heavy loops.

## Reading

The two executors complement each other: the interpreter for programs the agent writes and runs once or a few times (the authoring loop, change verification), the compiled C# for heavy computation and for concurrency. A production mode would need, at least:

- a selection rule, such as interpreting first and switching to compiled on a budget or a repeat;
- a decision on in-process memory exhaustion;
- the concurrency kinds, or keeping the fallback;
- the differential compare kept in the fast suite, so the two executors cannot drift.
