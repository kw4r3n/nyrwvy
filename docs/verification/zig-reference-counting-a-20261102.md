# Reference counting in the Zig backend, stage (a)

Stage (a) of the second milestone of [memory ownership in the Zig backend](../decisions/zig-backend-memory-ownership.md): counted values, retain and release in emitted code, and an allocator that reclaims them. Branch `feat/zig-reference-counting` from `7a7ffaac`, with `origin/main` `97347cfa` merged; Linux, Zig 0.17.0.

## Change

- **Runtime** (`tools/zig/emitter/runtime.zig`). Every String, Bytes, List, Set and Map storage and boxed Product or Union payload starts after an 8-byte header: the `u32` count and the allocated item count. The second field lets a value be a prefix of a fresh allocation (a filter result) and still free exactly; the ADR's "one `u32` count" is otherwise unchanged. Value representations are unchanged (`[]const u8`, `[]const T`, `*const T`, `NySet`, `NyMap`, `NyBytes`). Empty slices are never allocated. String literals are static values (`nyStatic`, count `maxInt(u32)`, never changed). `nyRetain` and `nyRelease` are type-directed; releasing a List releases its items in a loop, and releasing a box continues on a field of the box's own type instead of recursing, so a million-link user chain releases without growing the stack. Runtime helpers borrow their arguments and return owned results (a returned input or item is retained). Every helper that returned a sub-slice of its input (`ListSlice`, `ListDropLast`, `StringSlice`, trims, split pieces, code points) now copies.
- **Emitter** (`tools/zig/emitter/main.zig`). A liveness pass per function over its blocks (fixpoint, block and tail-call parameters as definitions). Constructors, `optionSome`/`resultOk`/`resultErr`, `readParameter`/`readPatternBinding`, call and invoke arguments, fold seeds, task captures, hand-off values and `return`/`abort`/`jump`/`tailCall` operands consume a reference: retained before the use unless it is the last. Every other operand is borrowed: released after the instruction that uses it last. A result never used is released at once. On a branch or match, each target releases what the terminator held and the target does not need; a tail call releases its dead parameters after rebinding. Field and item reads retain the value read; a callback's borrowed arguments (items, context values) are retained per call, since the callee owns its parameters. `UseResource` keeps its resource release and now also releases the body outcome or the release result it drops.
- **Allocators.** Values come from `ny_values`. The runner sets it in `main`: `std.heap.DebugAllocator` in the ReleaseSafe build that runPure uses, `std.heap.smp_allocator` in any other build (nothing in the repository builds that variant; it was checked by hand with `-O ReleaseFast`). Library mode leaves it null, so values stay in the arena that each export resets (`reset(.retain_capacity)`); arguments are copied into counted values. Runtime records (scheduler, tasks, cancellation, fake scripts, failure messages) stay in the arena until stage (b). Scripted fake responses are decoded as static values, because the script keeps them for the run.
- **Leak check.** After `main` releases the encoded result, the runner counts live values; any left makes it exit with code 86 and `leak: <n> values are still allocated after the run.` on stderr. The host reports that as the runtime failure `ZigLeak` (`run_value.zig`), so a leaking case fails with no protocol change and a case that does not leak keeps its reply.
- **DebugAllocator cost.** The checked allocator unmaps a bucket page as soon as its last slot is freed, so an allocate-and-free loop faulted in a fresh 128 KiB page per value (300,000 loop iterations took 7.4 s, mostly page faults). The runner backs it with a cache of 64 pages; the same run takes 0.21 s.
- **Case mapping.** `nyMapCase` builds in a temporary buffer and returns a counted copy; `tests/Nyrwvy.Core.Tests/ZigRuntime.fs` generates the same text, as `ZigBackendTests` requires.

## Scope of the leak check (stage b missing)

The check does not run when a run:

- ends with an uncaught failure (the frames it unwound have no `errdefer` yet);
- catches a failure (`ExceptionCatch`, or a task or `ParallelAll` worker that failed), for the same reason;
- starts a task whose result type is counted, or completes a hand-off cell with a counted value (task records are not released yet);
- has a task that never started because the virtual clock failed every waiter.

Before this scoping, with the check on every run, only two example sweeps failed on it: `bounded-stream` (OneShot cursors holding values) and `cleanup-shield` (task and cancellation paths). `exception-boundary`, `task-cancellation-outcome` and `function-abort` passed the full check. The query test of `ZigHostRunPureParityTests` leaked only in its uncaught-failure cases.

## Checks

| Check | Result |
| --- | --- |
| `tools/zig/emitter/runtime_test.zig` under `DebugAllocator` (strings, lists, sets, maps, sort, case mapping and a 1,000,000-link chain), now run by `npm run test:host` | passed, no leak |
| A deliberately leaking runner (one release removed) | exit 86, 5 values reported |
| `ZigHostRunPure` and `ZigBackend` filters (replayed F# replies, unchanged; `ZigBackendTests` is only the case-mapping file check, the runPure tests are the C#-vs-Zig agreement) | 15 passed |
| `scripts/test-fast.fish` | 710 passed; fast-contracts 64 passed |
| `npm run test:host` | passed (runtime test, model reader, host tests) |
| `.opencode/tests/nyrwvy-zig-library.test.mjs` (library mode, bounded memory) | passed |
| `scripts/test-extended.fish` | Core 134 passed, Extended 2 passed, node extended 4 passed |
| Hand-written IR (scratch, not committed) using every callback kind with counted items (`ListMap` with a context value, `ListFilter`, `ListFold`, `ListScan`, `ListSortBy`, `ListMinBy`, `ListFlatMap`, `ListZipWith`, `SetFromList`/`SetMap`/`SetFilter`, `MapFromList`/`MapMapValues`/`MapFilter`/`MapFold`, `MapKeys`), `ProductUpdate`, `FieldAccess`, `StringSplit`/`Join`/`Trim`, string literals into folds, and a `MatchOption` into a join block with a parameter; six inputs, checked runner | output byte-identical to the `97347cfa` emitter's runner, exit 0, no leak |
| `verification-planner` snapshot through `run-fixtures --backends csharp,zig`, 256 inputs of `plan_verification` | 256 of 256 agree, none leaked |
| Examples run with every runPure forced to Zig (temporary host patch, not committed): `bounded-stream`, `cleanup-shield`, `exception-boundary`, `task-cancellation-outcome`, `function-abort` | passed |

`bounded-worker-stream`, `dynamic-worker-collector`, `scoped-result-handoff` and `config-request-batch` fail on Zig at `97347cfa` too (emitter type errors for task and generic types, host sources), so they say nothing about this change.

## Reclamation

One tail-recursive loop (`loop(n, acc)`: `Int64ToString(n)`, concatenated with itself, appended to `acc`, `acc` cut to 50 items), emitted from hand-written execution IR and built with `-O ReleaseSafe` unless noted. Peak RSS of the runner process (`getrusage` of children); about 12 MB is its floor.

| Runner | n = 300,000 | n = 3,000,000 |
| --- | --- | --- |
| Before (arena, `97347cfa`) | 246,580 KB, 0.05 s | not run |
| After, checked (`DebugAllocator` with page cache) | 12,532 KB, 0.21 s | 12,360 KB, 2.08 s |
| After, `-O ReleaseFast` (`smp_allocator`) | 12,184 KB, 0.05 s | not run |

## Not done or not verified

- Stage (b) (`errdefer`, task ownership), (c) count-of-one reuse, (d) the persistent List and (e) the Set and Map hash index.
- The repository has no committed corpus of saved cases on the Zig backend; "every saved case" here means the runPure cases of the tests above, the planner sweep and the example sweep.
- Runs that hit the 1,024-capability-call limit exit before the check.
- Values decoded for a fake's script and runtime records grow the arena for the life of a runner process.
