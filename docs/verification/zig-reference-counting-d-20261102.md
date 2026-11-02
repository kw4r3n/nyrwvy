# Reference counting in the Zig backend, stage (d)

Stage (d) of the second milestone of [memory ownership in the Zig backend](../decisions/zig-backend-memory-ownership.md): updates to a shared list. Built as a smaller change than the ADR's persistent vector: a shared list appends in place into its storage's spare room when no longer list shares that storage, so it no longer copies. Branch `feat/zig-rc-stage-d` from `origin/main` `a31a713b` (stages (a) to (c)); Linux, Zig 0.17.0.

## Why not the trie yet

The ADR names the C# backend's 32-way trie with tail and a start position. In the Zig backend a List is `[]const T` with a counted header, and that contiguous slice is assumed by 32 lines of emitted list operations (`for (list)`, `.len` for `ListLength`, `setToList` sharing `.items`), 29 runtime functions taking `[]const T` (Set and Map storage, `nyEqual`, `nySortBy`, `nyStringJoin`, `nyParallelAll` and others), argument and result encoding, and the library-mode C ABI, which passes a List of scalars as pointer and length. Switching the representation touches all of them. The workloads stage (c) left slow were appends to a shared list: keeping old versions while appending, and `{ st with items = append(st.items, x) }`, where reading the field makes the list's count two. Both are served without a representation change, so this stage builds that and leaves the trie for an owner decision.

## Change

- **Runtime only** (`tools/zig/emitter/runtime.zig`); the emitter and emitted code are unchanged.
- **Owned items.** Storage records how many items it owns (`used`), which can exceed the length of a list that shares it. The header stays 8 bytes: the top bit of `cap` (`ny_spare`) marks storage with spare room, and its last 4 bytes then hold `used`; without the bit every allocated item is owned. Spare room under 4 bytes (only Strings and Bytes) cannot hold the count and is treated as owned, so it is never claimed. Releasing the last reference releases the `used` items, not the releasing list's length.
- **`nyExtend`**, the stage (c) reuse primitive behind append, concatenation, String concatenation, set-at and Set and Map insert:
  - unique (count one): as before, plus it first releases the items a longer, now dead, list left past this one;
  - shared, extended (`extra > 0`), not static, and its length equal to `used`: claims the next slots in place and raises `used`. Every other list on the storage is no longer, so none of them sees the new items; the count is unchanged, since the update consumed one reference and returns one;
  - otherwise a copy, as before, but an extending copy gets room for twice the old length, so the next shared append claims instead of copying again.
- **`nyListSliceOwned`**: a shared slice from 0 that keeps at least half of the storage's owned items returns the same storage with the shorter length (the reference moves); the unique path releases up to `used` and records the kept length.
- **`nyShrink` and `nyAllocCap`** record `used` for storage allocated larger than its contents.
- Sharing is never observable: a list reads only up to its own length, and a slot past it is written only by the one list whose length equals `used`.

## Operations on a shared list

| Operation | Before | After |
| --- | --- | --- |
| `ListAppend`, `ListConcat` (left), `StringConcat` (left), `SetInsert` and `MapInsert` of a new key | copy, O(n) | in place, amortised O(1), when the list is the longest on its storage; otherwise one copy with doubled room |
| `ListSlice` from 0 keeping at least half | copy | shared, O(1) |
| `ListSetAt`, `ListInsertAt`, `ListRemoveAt`, `ListDropLast`, slice from a later start, `MapInsert` of an existing key | copy, O(n) | unchanged: copy, O(n) |

Two lists extended from the same version: the first claims, the second copies.

## Owner decisions (not in the ADR)

- **Stage (d) without the 32-way trie.** Shared `ListSetAt` stays O(n), below the documented O(log n), and insert and remove stay O(n) for a shared list, like decision D for Sets and Maps; a dequeue (`ListSlice(queue, 1, length)`) still copies. Revisit when a measured workload sets, inserts or dequeues on shared lists heavily; the trie is then the change described above.
- **`cap` holds 31 bits.** A List or String is limited to 2,147,483,647 items (was 4,294,967,295); a larger one fails as out of memory.
- **Spare room on shared copies.** A copy made to extend a shared list has room for twice the old length, as a unique list's growth already does (stage (c)), so a shared value may hold up to about twice its length.
- **A shared prefix slice shares** when it keeps at least half of the storage's items, the halving rule of [list slices that share structure](../decisions/list-slice-sharing.md); a smaller prefix copies.

## Measurements

A Zig driver appended to `runtime.zig` (scratch, not committed), calling the runtime helpers with the ownership the emitter emits for each loop, built against the runtime of `a31a713b` (before) and of this branch (after), with `-O ReleaseSafe` (the checked `DebugAllocator` with its leak check) and `-O ReleaseFast` (`smp_allocator`). Wall time and peak RSS of the process. Every run exited 0 with the same checksum before and after; the checked runs reported no leak. These are not stage (c)'s emitted programs, so absolute times differ from its table.

- **Tail loop** (stage (a)): `Int64ToString(n)` concatenated with itself, appended, the list cut to its last 50 items.
- **Build loop**: append to a growing list and update a two-field record in place.
- **Map loop**: `MapInsert(m, Int64ToString(n), n mod 7)`.
- **History** (new, shared append): append to a list and append the list itself to a history list, keeping every version alive.
- **Field** (new, shared append): `{ st with items = append(st.items, x) }`, the gap stage (c) recorded.

| Case | Before, checked | After, checked | Before, ReleaseFast | After, ReleaseFast |
| --- | --- | --- | --- | --- |
| Tail loop, n = 3,000,000 | 33.2 s, 12.3 MB | 33.3 s, 12.6 MB | 0.157 s, 12.6 MB | 0.182 s, 12.6 MB |
| Build loop, n = 100,000 | 0.01 s, 12.4 MB | 0.01 s, 12.6 MB | 0.00 s | 0.01 s |
| Build loop, n = 3,000,000 | 0.16 s, 102.5 MB | 0.18 s, 102.5 MB | 0.10 s, 92.9 MB | 0.10 s, 93.8 MB |
| Map loop, n = 20,000 | 0.48 s, 12.6 MB | 0.51 s, 12.6 MB | 0.49 s | 0.44 s |
| History, n = 10,000 | 0.58 s, 786 MB | 0.00 s, 12.3 MB | 0.47 s, 783 MB | 0.00 s, 12.5 MB |
| History, n = 20,000 | 2.16 s, 3,094 MB | not run | 1.77 s, 3,091 MB | not run |
| History, n = 100,000 / 1,000,000 | not run | 0.01 s, 12.5 MB / not run | not run | not run / 0.05 s, 63 MB |
| Field, n = 20,000 | 1.86 s, 12.5 MB | 0.00 s, 12.5 MB | 1.44 s | 0.00 s |
| Field, n = 100,000 | not run | 0.01 s | 34.3 s | 0.01 s |
| Field, n = 3,000,000 | not run | not run | not run | 0.13 s, 97 MB |

- The checked tail loop spends 99.7% of its time in the kernel in both builds: the `DebugAllocator` maps and unmaps a page per iteration in this driver. A first version with a 16-byte header (count, cap, used) avoided that by changing the size class (0.34 s) but raised the build loop's peak RSS from 93 to 140 MB, so the header stayed 8 bytes.
- **ReleaseFast tail loop: 16% slower** (0.157 ± 0.002 s to 0.182 ± 0.001 s, five runs each; about 8 ns per iteration, 10% more instructions). Reading and writing `used` on each unique append and slice accounts for part of it; the rest moved with inlining (`nyInt64ToString`, whose code did not change, executes more instructions). Not resolved; the build and map loops did not slow down.

## Checks

| Check | Result |
| --- | --- |
| `runtime_test.zig`, new test "a shared list grows in place past its own length and copies otherwise" (shared append claims and leaves the old list unchanged; a second append from the same version copies and does not overwrite the first; the old list, unique again, releases what the dead longer list left; the last reference releases past its own length; a shared prefix shares and its append copies; a shared Set insert; String claims, and spare room under 4 bytes is not claimed; 10,000 live versions on at most 16 storages) under `DebugAllocator` | passed, no leak |
| The new test with each check removed alone: the `used == length` test, the orphan release, release up to `used`, the 4-byte room rule, the doubled room on a copy, prefix sharing, the shared claim | fails each time (wrong contents, a leak, or a pointer or storage-count check) |
| Stage (c) test, adjusted: its "shared" inputs are full storage (a shared list with room now claims instead of copying) and its slice case cuts `[1, 3)` (a prefix now shares) | passed |
| `npm run test:host` | passed (runtime tests 5 of 5, model reader, host tests) |
| `.opencode/tests/nyrwvy-zig-library.test.mjs` (library mode, per-call arena) | passed |
| `ZigHostRunPure` and `ZigBackend` filters (replayed replies unchanged, goldens not re-recorded) | 15 passed |
| `scripts/test-fast.fish` | passed (Core 710 passed, node fast tests passed) |
| `scripts/test-extended.fish` | Core 134 passed, Extended 2 passed, node extended 4 passed |
| Examples with every runPure forced to Zig (temporary host patch, reverted): `bounded-stream`, `cleanup-shield`, `exception-boundary`, `function-abort`, `task-cancellation-outcome` | passed, no leak |

## Not done or not verified

- The 32-way trie, and with it O(log n) `ListSetAt`, insert and remove on shared lists and sharing dequeues.
- The ReleaseFast tail-loop slowdown above.
- `ListSetAt` on a shared list was not measured; it copies as before.
- The `verification-planner` 256-input sweep was not repeated.
- Stage (e) the Set and Map hash index.
