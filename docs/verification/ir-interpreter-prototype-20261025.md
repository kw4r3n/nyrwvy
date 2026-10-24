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

## Faster on .NET (second round)

Two further options, measured with the same `bench.mjs`, the same four entries and a 3 s idle time after the session opens (the time an agent spends reading and writing before its first run). Three runs, load average 4–5. The results are in `results.jsonl`.

- **Warmed compiled path** (`NYRWVY_RUNPURE_WARMUP=1`): when the session opens, a background task compiles a trivial program with Roslyn and starts the persistent worker (`PureRunWorker.Warm`). Nothing else changes.
- **In-process JIT** (`NYRWVY_RUNPURE_EXECUTOR=jit`, `ExecutionIrCompiler.fs`): every function of the program is compiled with `System.Linq.Expressions` into a `Func<IrValue[], IrValue>` that the JIT runs.
  - Each frame keeps its values in one `IrValue[]` slot array. Control flow, Int64 and Bool arithmetic, comparisons, direct calls and `ListFold` have generated code. Every other instruction calls back into the interpreter's `Evaluate`, which reads its operands from the slots.
  - Calls go through `EnterCompiled`. It enforces the same depth limit and records frames like the interpreter, but makes no allocation, and it reads the deadline clock only on every 256th call or back edge.
  - In the same round, the interpreter now builds lists from arrays, which brought its `sumTo` from 150 ms to 136 ms.

| Entry | Compiled | Warmed compiled | Interpreter | JIT |
| --- | --- | --- | --- | --- |
| `add`, first call of the process | 1,773–1,788 ms | 658–666 ms | 230–233 ms | 204–208 ms |
| `wordCount`, first call | 167–176 ms | 240–253 ms | 27 ms | 72–76 ms |
| `sumTo`, first call | 349–359 ms | 202–209 ms | 313–338 ms | 45–46 ms |
| `fib`, first call | 185–192 ms | 194–210 ms | 65–66 ms | 22 ms |
| `add`, steady | 2.4–2.6 ms | 2.3 ms | 0.7 ms | 1.1–1.2 ms |
| `wordCount`, steady | 5.5–5.6 ms | 5.2–5.5 ms | 1.3 ms | 2.2–2.3 ms |
| `sumTo(100000)`, steady | 7.8–8.2 ms | 7.9–8.0 ms | 134–139 ms | 39.7–40.1 ms |
| `fib(20)`, steady | 3.2–3.3 ms | 3.0–3.2 ms | 77–79 ms | 8.7–9.2 ms |

- **Warm-up** cuts the first call from 1.8 s to 0.66 s. It costs one background compile and a worker process per session. What remains is emission and the first real Roslyn compile of a generated program, which the trivial program does not warm. New entries still build in 160–250 ms.
- **The JIT** is 3–4 times faster than the interpreter on loops and recursion: `fib` is 2.8 times slower than C# and `sumTo` 5 times slower. Its first call needs no build. The gap that remains is boxing: every Int64 result allocates an `IrInt`, and every call allocates an argument array and a slot array. `ImmutableList` construction also costs about 10 ms per 100,000 items, against NyList's arrays. Typed locals (`long`/`bool` in place of `IrValue` where the IR type is primitive) would be the next step; it has not been measured.
- **JIT compile time** (2,441 runs in the suites): median 0.9 ms and p90 5.3 ms per program. 99 programs took more than 20 ms and 31 more than 100 ms. The largest, a 19-function program, took 2–7 s while the parallel test run loaded the machine. `Expression.Compile` grows badly with very large function bodies, so a real mode needs tiering: interpret first, then compile a function once it has been called enough times, or compile lazily per function.

**JIT parity** (`compare-jit`, the same suites): fast 66 match and 2 fall back. Extended: 2,363 match, 40 fall back, and 12 differ only at limits (9 timeouts, 3 stack-overflow frame lists). No result value differed.

**Not measured:** a runtime-support assembly prebuilt once per session (the per-program Roslyn input would shrink; the 160–250 ms new-entry builds are the ceiling of what it can save), typed locals, and tiering. Under Native AOT `Expression.Compile` falls back to its own interpreter, so the JIT row depends on keeping the session JIT-based (D-008 names AOT only as a future path).

### Reading after the second round

On .NET the options stack up rather than compete:

1. Warm-up is nearly free and removes most of the first-call wait for the compiled path.
2. An in-process executor removes per-program builds. Interpreting first and tiering hot functions to Expression-compiled code gives about 0.2 s on the first call, about 1 ms on small calls, and loops within 3–5 times of C#.
3. The generated C# remains the path for heavy, long-running and concurrent programs.
