# Reference counting in the Zig backend, stage (e)

Stage (e) of the second milestone of [memory ownership in the Zig backend](../decisions/zig-backend-memory-ownership.md): the Set and Map hash index of decision D, a dense array plus a hash index, with no HAMT. Branch `feat/zig-rc-stage-e` from `origin/main` `71b87ff1` (stages (a) to (d)); Linux, Zig 0.17.0.

## Change

- **Representation** (`tools/zig/emitter/runtime.zig`). `NySet(T)` keeps `items` and `NyMap(K, V)` keeps `keys` and `values`, the insertion-ordered dense arrays emitted code iterates, and both gain `index: []const u32 = &.{}`: counted storage of power-of-two slots, each 0 (empty) or a position + 1, linear probing, at most half full. An index covers exactly the items of the value holding it. Retain and release take it with the items. An empty index means none: lookups scan, which is what small Sets and Maps use.
- **Threshold.** No index up to 32 items for scalar items or keys (Int64, Bool, Float64) and up to 8 for everything else (Strings, records, lists, unions); measured below.
- **Lookup** (`nySetFind`, `nyMapFind`, used by contains, `MapGet`, insert, remove, intersection, difference, subset and `ValueEqual`): a scan without an index, otherwise a probe from `nyHash(item)`. Expected O(1).
- **Insert** (`nySetInsertInto`, `nyMapInsertInto`; the borrowed `nySetInsert` and `nyMapInsert`, which nothing called, are removed): the items append through stage (c) and (d)'s `nyExtend`, then the index adds the new position in place when its count is one and is at most half full, and is rebuilt from the items otherwise (shared, static, or growing to twice the slots). So an insert into a unique Set or Map is amortised O(1), and one into a shared Set or Map is O(n), as decision D says, even when stage (d) appends its items in place. Replacing a Map value keeps the keys and their index.
- **Remove** compacts the items as before and rebuilds the index: O(n), shared or not.
- **Builders.** `SetFromList` and `SetUnion` insert one by one (they scanned the kept items for each item: O(n²) before); intersection, difference and the emitted `SetFilter` build the index of their result (`nySetOf`); `MapUnion`, `MapFilter`, `MapFromList`, `SetMap` and argument decoding already inserted.
- **Hashing.** `nyHash`, beside `nyEqual`, mirrors it case by case: Int64 and Bits32 through the MurmurHash3 finaliser; Strings and Bytes by Wyhash of their bytes; Float64 as equality compares it (every NaN alike, `-0.0` as `0.0`), although Float64 is not an equatable key type, since equality reaches it inside other values in the runtime; Options, lists by length and items, Products by fields in order, boxes through the pointer, Unions by tag and payload; Sets and Maps by a sum over items (keys with their values), so independent of insertion order, as their equality is.
- **Emitter** (`tools/zig/emitter/main.zig`, four lines): `SetContains` and `MapContainsKey` call `nySetFind` and `nyMapFind` instead of scanning `.items` and `.keys`; `SetFilter` wraps its result in `nySetOf`; `MapMapValues` shares the source's index with its keys.
- **Inlining.** Index upkeep (`nyProbe`, `nyIndexGrow`, `nyIndexBuild`) is out of line and the four insert entry points are `inline`, so a Set or Map below the threshold runs the same scan as before (see measurements).
- **Order.** Iteration, `SetToList`, `MapKeys`, `MapValues`, folds and argument and result encoding read the dense arrays, whose order is unchanged; the index never decides an order. The order is therefore still that of first insertion, as the [language reference](../reference/language-subset.md) specifies.

## Operations

| Operation | Before | After |
| --- | --- | --- |
| `SetContains`, `MapGet`, `MapContainsKey` | scan, O(n) | O(1) expected |
| `SetInsert`, `MapInsert` (new key) into a unique value | scan, then append amortised O(1) | amortised O(1) |
| `SetInsert`, `MapInsert` into a shared value | scan, then append or copy | O(n): index rebuilt |
| `MapInsert` of an existing key | scan, then set-at | O(1) expected for the key, then set-at as before |
| `SetRemove`, `MapRemove` | O(n) | O(n): compact and rebuild |
| `SetFromList`, `SetUnion` | O(n²) | O(n) expected |
| `SetIntersection`, `SetDifference`, `SetIsSubset`, `ValueEqual` of Sets and Maps | O(n·m) | O(n) expected |

## Owner decisions (not in the ADR)

- **The index is a field of the Set and Map values**, not kept behind `items`: ADR "Before F# removal" placed it behind the existing field to keep emitted text unchanged; F# removal is done, so the emitter changed four lines instead of a global table keyed by storage address.
- **No tombstones.** The ADR's Collections bullet says tombstones arrive with reuse; removal still compacts and rebuilds (O(n)). Revisit when a measured workload removes from large Sets or Maps.
- **Per-type threshold** of 32 scalar or 8 other items without an index, from the measurements below.
- **A shared insert rebuilds the index** although stage (d) may append the items in place: the index is updated only when its count is one, so a version kept alive while inserting costs O(n) per insert (decision D's shared update). A shared index would need entries past a holder's length to be skipped and a slot count of stale entries; not built.
- **`SetFromList` and `SetUnion` results may hold spare room** of up to their length (they grow by doubling instead of being copied exactly), as unique lists already do since stage (c).

## Measurements

The stage (d) driver appended to `runtime.zig` (scratch, not committed), with new cases, built against the runtime of `71b87ff1` (before; its `SetContains` and `MapContainsKey` are the emitted scans) and of this branch (after), with `-O ReleaseSafe` (the checked `DebugAllocator` with its leak check) and `-O ReleaseFast` (`smp_allocator`). Wall time and peak RSS of the process. Every run exited 0 with the same checksum before and after; the checked runs reported no leak.

- **Map insert**: `MapInsert(m, Int64ToString(i), i mod 7)` growing to n String keys (stage (c)'s map loop).
- **Lookup**: the map insert loop, then `MapGet` of every key and `MapContainsKey` of n absent keys.
- **Set**: `SetFromList` over 2n Strings (each value twice), then n `SetContains`.
- **Small Int64 map**: n times, a map of s Int64 keys built, every key read twice, released.
- **Small String map**: the same with s String keys (built once, shared).
- **Tail, build, history, field**: stage (d)'s loops.

| Case | Before, checked | After, checked | Before, ReleaseFast | After, ReleaseFast |
| --- | --- | --- | --- | --- |
| Map insert, n = 20,000 | 0.46 s, 12.4 MB | 0.00 s, 12.8 MB | 0.44 s, 12.8 MB | 0.00 s, 12.8 MB |
| Map insert, n = 1,000,000 | not run | 0.18 s, 50.4 MB | not run | 0.15 s, 46.8 MB |
| Lookup, n = 20,000 | 2.66 s, 12.8 MB | 0.48 s, 12.8 MB | 1.78 s, 12.8 MB | 0.01 s, 12.8 MB |
| Lookup, n = 1,000,000 | not run | not run | not run | 0.39 s, 46.8 MB |
| Set, n = 20,000 | 2.04 s, 12.8 MB | 0.24 s, 12.8 MB | 1.55 s, 12.8 MB | 0.00 s, 12.8 MB |
| Set, n = 1,000,000 | not run | not run | not run | 0.29 s, 84.9 MB |
| Small Int64 map, s = 4, n = 2,000,000 | 72.6 s, 12.8 MB | 72.3 s, 12.8 MB | 0.224 ± 0.002 s | 0.233 ± 0.001 s |
| Small Int64 map, s = 8, n = 1,000,000 | not run | not run | 0.198 ± 0.001 s | 0.208 ± 0.004 s |
| Small Int64 map, s = 64, n = 125,000 | not run | not run | 0.579 ± 0.004 s | 0.368 ± 0.002 s |
| Small String map, s = 12, n = 666,666 | 75.8 s, 12.8 MB | 74.4 s, 12.8 MB | 0.51 s | 0.37 s |
| Tail loop, n = 3,000,000 | 34.4 s, 12.8 MB | 34.1 s, 12.8 MB | 0.175 ± 0.005 s | 0.177 ± 0.003 s |
| Build loop, n = 3,000,000 | 0.17 s, 102.1 MB | 0.17 s, 101.5 MB | 0.10 s, 93.5 MB | 0.10 s, 92.5 MB |
| History, n = 1,000,000 | 0.08 s, 65.9 MB | 0.08 s, 66.2 MB | 0.05 s, 62.5 MB | 0.05 s, 62.9 MB |
| Field, n = 3,000,000 | 0.22 s, 134.7 MB | 0.22 s, 134.9 MB | 0.13 s, 96.8 MB | 0.13 s, 98.1 MB |

± is the spread of five runs (`perf stat -r 5`). The checked small-map and tail loops spend their time in the `DebugAllocator` mapping and unmapping pages (stage (d)).

**Threshold.** ReleaseFast, the small-map loops with the index from 9, 17 or 33 items, two runs each:

| Keys | Before (scan) | Index past 8 | Index past 16 | Index past 32 |
| --- | --- | --- | --- | --- |
| 16 Int64 | 0.20–0.22 s | 0.31 s | 0.21 s | 0.21 s |
| 32 Int64 | 0.26 s | 0.32 s | 0.29 s | 0.27 s |
| 64 Int64 | 0.53 s | 0.38 s | 0.36 s | 0.33 s |
| 12 Strings | 0.50 s | 0.38 s | 0.49 s | 0.50 s |
| 24 Strings | 0.77 s | 0.36 s | 0.40 s | 0.79 s |

Comparing two Int64 costs one instruction, so a scan of up to 32 beats hashing; a String comparison reads memory, and the index wins from 12. Before the insert entry points were made `inline`, the first version (index upkeep inline, one threshold of 8) was 18% slower on the 8-key Int64 loop (0.235 s against 0.197 s): the larger insert was no longer inlined into the loop.

## Checks

| Check | Result |
| --- | --- |
| `runtime_test.zig`, new test "sets and maps look up through a hash index that agrees with equality" (equal values hash equally: `-0.0` and `0.0`, NaNs with different bits, separately built Strings, Sets and Maps built in opposite orders; a Set of 40 records found by equal records built with `-0.0` and another NaN; no index up to the threshold, at most half full up to 1,000 items; a unique insert updates the index in place, a shared insert leaves the old Set's lookups unchanged; removal rebuilds the positions; intersection, difference, union, subset and equality; 20,000 String keys, a replaced value keeps its place, a removed key inserted again goes last; `MapMapValues` sharing the index) under `DebugAllocator` | passed, no leak |
| The new test with each check removed alone: the `-0.0` rule, the NaN rule, the count-of-one test, the half-full growth, the rebuild on removal, the index release, the order-independent Set hash, the index built by `nySetOf` | fails each time |
| `npm run test:host` | passed (runtime tests 6 of 6, model reader, host tests) |
| `.opencode/tests/nyrwvy-zig-library.test.mjs` (library mode, per-call arena) | passed |
| `ZigHostRunPure` and `ZigBackend` filters (replayed replies unchanged, goldens not re-recorded) | 15 passed |
| `scripts/test-fast.fish` | passed (Core 710 passed, fast-contracts 64 passed) |
| `scripts/test-extended.fish` | Core 134 passed, Extended 2 passed, node extended 4 passed |
| Examples with every runPure forced to Zig (temporary host patch, reverted): `bounded-stream`, `cleanup-shield`, `exception-boundary`, `function-abort`, `task-cancellation-outcome` | passed (exit 0), no leak |

## Not done or not verified

- Tombstones, and with them O(1) removal from a unique Set or Map.
- Small Int64 maps (up to 32 keys, no index) are about 4–5% slower in the ReleaseFast loop above (about 1 ns per operation); not resolved.
- Sets and Maps are not C ABI types in library mode; they appear there only inside exported functions, under the per-call arena, which the library test covers only in general.
- Scripted fake responses (`ny_static_values`): their storage has a static count, so an index there is rebuilt on every insert past the threshold and the old one stays in the arena, O(n) per insert; `nyExtend` already copied every append in that mode. Not measured.
- Hash quality on adversarial keys: the hash is unseeded, so keys chosen to collide make lookups O(n), as the scan was.
- The `verification-planner` 256-input sweep was not repeated.
