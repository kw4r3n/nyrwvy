# Reference counting in the Zig backend, stage (c)

Stage (c) of the second milestone of [memory ownership in the Zig backend](../decisions/zig-backend-memory-ownership.md): count-of-one reuse. An update whose input is uniquely owned (count one) writes it in place; a shared or static input is copied as before. Branch `feat/zig-rc-stage-c` from `origin/main` `6607f25c` (stages (a) and (b)); Linux, Zig 0.17.0.

## Change

- **Emitter** (`tools/zig/emitter/main.zig`). The updated operand of `productUpdate` (`sourceValueId`), `listAppendItem`, `listSetAt`, `listSlice` (`listValueId`), `listConcat` (`leftListValueId`), `stringConcat` (`leftValueId`), `setInsert` (`setValueId`) and `mapInsert` (`mapValueId`) is now consumed, not borrowed (`consumedKey`). Liveness already handles a consumed operand: when the value is still live after the update it is retained first, so its count is at least two and the update copies; when the update is its last use, no retain is emitted and the count is one unless another value holds it. Nothing else in the ownership pass changed. `productUpdate` writes the box in place when `nyUnique(source)`, releasing each replaced field; otherwise it copies as before and drops the source reference.
- **Runtime** (`tools/zig/emitter/runtime.zig`). `nyExtend` is the one reuse primitive: for an owned list and a number of extra items it returns the list's own storage when the list is non-empty and its count is one (grown by `realloc`, doubling, when the header's capacity is short; the header's `cap` field follows), otherwise a fresh copy with each item retained and the caller's reference dropped. `nyListAppendOwned`, `nyListConcatOwned` (and `nyConcatOwned` for Strings), `nyListSetAtOwned` are built on it. `nyListSliceOwned` releases the items outside the range of a unique list and moves the rest to the front, keeping the storage unless it is more than twice the kept length (then it shrinks by `realloc`, so a small slice never pins a large list). `nySetInsertInto` and `nyMapInsertInto` consume the container and borrow the item; a Map's keys and values are checked separately (`MapKeys` can share the keys alone). `nySetInsertOwned` and `nyMapInsertOwned` now call them, so the emitted `SetMap`, `MapFilter` and `MapFromList` builder loops, `nyMapUnion` and argument decoding append in place too. The borrowed helpers stay for other callers (`bytesAppend`, `nyBytesSet`, decoding).
- **Uniqueness** is the plain `u32` count (decision A). A static value (`maxInt`) and an empty list (no header) never count as unique.

## Operations

| Operation | In place | Why |
| --- | --- | --- |
| `ListAppend`, `ListConcat` (left), `StringConcat` (left) | yes | list and string building in loops; the stage (a) loop and the build loop below |
| `ListSlice` | yes | the stage (a) loop cuts its accumulator every iteration |
| `ProductUpdate` | yes | record updates in loops |
| `ListSetAt`, `SetInsert`, `MapInsert` | yes | the ADR names them; `MapInsert` measured below (the linear key search of stage (e) remains) |
| `ListInsertAt`, `ListRemoveAt`, `ListDropLast`, `SetRemove`, `MapRemove` | no | no loop in the examples or benchmarks uses them; each still copies (O(n)) |
| `BytesSet`, `BytesAppend` | no | no example uses Bytes |

## Owner decisions (not in the ADR)

- **Spare capacity.** A unique list or string grows by doubling, and a slice keeps its storage up to twice the kept length, so a value may hold up to about twice its length. The header's `cap` field (accepted with stage (a)) records the allocated count.
- **Concatenation and slicing consume their left or only list operand.** The ADR lists `ProductUpdate`, `ListSetAt`, `ListAppend`, `SetInsert`, `MapInsert` "and similar"; `StringConcat`, `ListConcat` and `ListSlice` are read as similar.
- **Updating a field read from the same record is not in place** (the main gap against this ADR's aim that building a list or updating a record is allocation-free once unique). `{ st with items = append(st.items, x) }` reads the field first (`FieldAccess` retains it), so the list has count two when appended and is copied, while the box itself is reused. Taking the field out of a dying box (Perceus borrowing or drop specialisation) is left for later.
  - *2026-11-12:* closed for String, Bytes, List, Set and Map fields when the read and the update are in one block after the emitter merges repeated reads of a parameter or pattern binding and fuses a block into its only jumping predecessor (`mergeRepeatedReads`, then `moveOutUpdatedFields` in `tools/zig/emitter/main.zig`). A threaded-state loop updating one list or map element per turn measured 0 allocations per turn, down from 1. Nested record, union and Option fields still copy, and so does a field read separated from its update by a branch or a merge point.

## Measurements

Execution IR written by hand (scratch, not committed), emitted by the emitter before (`6607f25c`) and after, built with `-O ReleaseSafe` (the checked `DebugAllocator` that runPure uses) and with `-O ReleaseFast` (`smp_allocator`). Peak RSS of the runner process; about 12 MB is its floor. Every run exited 0 with the same output before and after, and the checked runs reported no leak.

- **Stage (a) loop**: `loop(n, acc)`: `Int64ToString(n)` concatenated with itself, appended to `acc`, `acc` cut to 50 items.
- **Build loop**: `build(n, acc, st)`: `Int64ToString(n)` appended to a list that grows to `n` items, and a two-field record updated (`count + 1`, `last`).
- **Map loop**: `fill(n, m)`: `MapInsert(m, Int64ToString(n), n mod 7)`, growing to `n` keys.

| Case | Before, checked | After, checked | Before, ReleaseFast | After, ReleaseFast |
| --- | --- | --- | --- | --- |
| Stage (a) loop, n = 300,000 | 0.21 s, 12.3 MB | 0.08 s, 12.7 MB | 0.05 s, 12.1 MB | 0.01 s, 12.5 MB |
| Stage (a) loop, n = 3,000,000 | 2.07 s, 12.7 MB | 0.82 s, 12.7 MB | 0.54 s, 12.7 MB | 0.11 s, 12.5 MB |
| Build loop, n = 20,000 | 1.85 s, 12.7 MB | 0.00 s, 12.5 MB | 1.45 s | 0.00 s |
| Build loop, n = 100,000 | 45.3 s, 12.5 MB | 0.01 s, 12.5 MB | 34.8 s | 0.01 s |
| Build loop, n = 3,000,000 | not run | 0.16 s, 106.5 MB | not run | not run |
| Map loop, n = 20,000 | 3.14 s, 12.7 MB | 0.46 s, 12.7 MB | not run | not run |

## Checks

| Check | Result |
| --- | --- |
| `runtime_test.zig`, new test "count-of-one updates reuse unique storage and copy shared storage" (append, set, slice and concat of a list, String concat into a unique and a static string, growth past capacity, Set and Map insert, `nyUnique` of a box) under `DebugAllocator` | passed, no leak |
| The new test with the count check removed from `nyExtend`, from `nyListSliceOwned`, and from `nyUnique` (each alone) | fails each time (a shared input is reused or mutated); the slice case rechecked after the shrink rule |
| `npm run test:host` | passed (runtime tests 4 of 4, model reader, host tests) |
| `.opencode/tests/nyrwvy-zig-library.test.mjs` (library mode, per-call arena) | passed |
| `ZigHostRunPure` and `ZigBackend` filters (replayed replies unchanged, goldens not re-recorded), before and after the slice shrink rule | 15 passed |
| `scripts/test-fast.fish` | 710 passed; fast-contracts 64 passed |
| `scripts/test-extended.fish` | Core 134 passed, Extended 2 passed, node extended 4 passed |
| Examples with every runPure forced to Zig (temporary host patch, reverted): `bounded-stream`, `cleanup-shield`, `exception-boundary`, `function-abort`, `task-cancellation-outcome` | passed, no leak |

## Not done or not verified

- `ListSetAt` and `SetInsert` were not measured alone; they use the same `nyExtend` path as the measured append and `MapInsert`.
- The `verification-planner` 256-input sweep was not repeated.
- Stages (d) the persistent List and (e) the Set and Map hash index.
