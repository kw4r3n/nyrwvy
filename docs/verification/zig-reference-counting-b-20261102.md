# Reference counting in the Zig backend, stage (b)

Stage (b) of the second milestone of [memory ownership in the Zig backend](../decisions/zig-backend-memory-ownership.md): releases on failure paths and ownership of values held by tasks and hand-off cells, so the runner's leak check runs on every run. Branch `feat/zig-rc-stage-b` from `origin/main` `29743769` (stage (a)); Linux, Zig 0.17.0.

## Change

- **Failure paths** (`tools/zig/emitter/main.zig`, `Ownership.blockLines`). An instruction that can raise a failure (the effectful kinds, or a call, `ExceptionCatch` handler, `CancellationShield` body or `ListEffectFold` folder that may fail; one predicate, `instructionFails`, now also drives `computeMayFail`) is emitted inside its own block with an `errdefer` that releases what the frame holds at that point: the counted values live after it, plus the operands it borrows. Operands it consumes are not released there: the callee owns them and releases them on its own failure. The releases that follow a successful instruction stay outside the block. Inside instruction templates, `UseResource` and `TaskScope` release their body `outcome` when the resource release or the scope close then fails. `ExceptionCatch` no longer marks the run exempt.
- **Task and cell handles** (`tools/zig/emitter/runtime.zig`). `Task<T>` and `OneShot<T>` handles are counted values now (the emitter's `counted` no longer excludes them, so liveness retains and releases them like any value, in Lists and Product fields too). The records stay in the runtime arena; each has its own `refs` count (`ny_handle` marks the type). A task record starts with two references, the handle and its run; the run drops its own when the task is done. A cell starts with one, its handle. The last reference releases the value the record holds: a succeeded task's result, a completed cell's value. So an escaped handle still reads its value after its scope has closed, as the [task spec](../specs/expression-authoring.md) and [scoped one-shot](../specs/scoped-one-shot.md) require, and a value is never released while a task that will write it is still running.
- **Never-started tasks.** A `TaskStart` worker struct gains `drop`, which releases the arguments the call would have moved in; a task whose start the virtual clock failed calls it before failing. `ParallelAll` workers borrow their item and context from the caller, which waits for every worker, so their `discard` does nothing.
- **Leak check.** `ny_leak_exempt` is gone. The check runs after an uncaught failure too (after its envelope is written, so a leaking failure still reports `ZigLeak` through exit 86, as before).

## Deviation from the ADR (owner decision)

The ADR says a task's own reference to its result "is dropped when the task record is released at scope exit". As built, scope exit releases nothing: the result goes with the last handle (or with the run, when every handle was dropped before the task finished). Releasing at scope exit would free a value that an escaped handle can still read through `TaskWait`, which the task spec allows. Task and cell records themselves (and cancellation sources, scheduler waiters, fake scripts, failure messages) still grow the runtime arena for the life of a runner process.

## Leak check scope

The check now runs on every run except one: a run that exceeds 1,024 capability calls exits from inside the 1,025th call (`nyRecordCall`, `["limit", ...]`, exit 0) with every frame on every task still live, so a count there would report values in flight, not leaks. Turning the limit into an unwinding failure would let `ExceptionCatch` catch it, which changes behaviour.

## Checks

| Check | Result |
| --- | --- |
| `tools/zig/emitter/runtime_test.zig`, new test (a failure releasing held values, a handle dropped before its task runs, a waited task, a failed task, a handle escaping its closed scope, a task started on a closed scope, a cell completed then refused, a never-started task) under `DebugAllocator` | passed, no leak |
| The new test with the never-started `drop` removed, and with the run's own `drop` removed | fails with leaks |
| `ZigHostRunPure` and `ZigBackend` filters (replayed replies unchanged, goldens not re-recorded); the query test's uncaught-failure cases now run the leak check | 15 passed |
| `npm run test:host` | passed (runtime tests 2 of 2, model reader, host tests) |
| `.opencode/tests/nyrwvy-zig-library.test.mjs` (library mode) | passed |
| `scripts/test-fast.fish` | 710 passed; fast-contracts 64 passed after fixing `typed host gate selects both Zig suites` (stage (a) made the gate run the emitter runtime test first, and the test still expected two calls; it fails on `origin/main` too) |
| `scripts/test-extended.fish` | Core 134 passed, Extended 2 passed, node extended 4 passed |
| Examples with every runPure forced to Zig (temporary host patch, not committed), leak check on every run: `bounded-stream`, `cleanup-shield`, `exception-boundary`, `task-cancellation-outcome`, `function-abort` | passed, no leak |

`bounded-worker-stream` and `dynamic-worker-collector` (emitter type error `expected type 'u32', found 'i64'`), `scoped-result-handoff` (generic type parameter) and `config-request-batch` (host sources) fail on Zig for the same reasons as before stage (a); no leak was reported for any of them. The first forced-Zig `cleanup-shield` run found that `@hasDecl` does not see a non-`pub` declaration, so the handle marker and `drop` are `pub`.

## Not done or not verified

- Stages (c) count-of-one reuse, (d) the persistent List and (e) the Set and Map hash index.
- Runtime records (task and cell records, cancellation, waiters, fake scripts, failure messages) stay in the arena.
- **Deadlock unwinding** (fixed after review). When nothing can resume, the virtual clock used to release every failed waiter at once (`nySchedule`), so several task threads unwound at the same time and their `errdefer` releases changed plain counts concurrently, outside decision A. Every waiter is still marked failed, but `nySchedule` now releases failed waiters one at a time by start order (`nyFirstFailed`, ahead of any other waiter), so unwinding stays on one thread. Unwind-time capability calls were unordered before (threads raced) and now follow start order. `runtime_test.zig` "a deadlock fails every waiter and each unwinds alone" fails on the old scheduler (two threads unwind together) and passes now.
- There is no emitter-level leak test that compiles a hand-written program with a failing call; the failure path is covered by the runPure query test's failure cases, the forced-Zig examples and the shape test in `runtime_test.zig`.
- The `verification-planner` 256-input sweep of stage (a) was not repeated: its functions are pure, with no failing instruction and no task, so their emitted code is unchanged by this stage.
