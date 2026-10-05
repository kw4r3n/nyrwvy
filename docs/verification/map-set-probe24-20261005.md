# Map/Set probe 24 — 2026-10-05

**Status:** Complete. Probe 23's planner was authored again on orders 52 (persistent-vector list), 53 (`ListLastIndexOf`, smaller-side `SetDifference`) and 54 (`keepUnlisted`), and extended with a breadth-first shortest dependency path and a project length (each name finishes its days after its latest dependency). The port matched a JavaScript reference on **2,200/2,200 cases** on the first full run; 70 of the 71 functions were accepted on their first proposal. Probe 23's two over-limit combs now run: the cycle path of 100,000 in 2.25 s and the components of 100,000 in 4.57 s. Nothing is blocked. The largest cost is new: a breadth-first queue dequeued with `ListSlice(1, …)` copies the queue each turn, so on a fan, one name with many dependencies, both the shortest path and probe 21's unchanged `reachable` **exceed the 5 s run limit** at 40,000 names. A two-stack queue written by hand is linear (300,000 in 3.9 s) for 9 more nodes.

**Date:** 2026-10-05

Baseline: nyrwvy `2e7b8db` (main after #346, clean). The session server was built with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release`. The method was probe 23's: a Node client over the maintained `createGraphTools` handlers, `simulateCreateTypesByName`, `create_function` with name-resolved recipes, `commit`, and one `runPureByName` per case, against a JavaScript reference over a seeded corpus (seed 24, so the graphs differ from probe 23's). Node counts are the `nodeCount` of each `create_function` review. Probe 23's harness spread unchanged self-call arguments with a JavaScript helper; here every self-call recipe names only the arguments it changes and sets `keepUnlisted`, as an author would write it. The harness, reference, snapshot and transcript are not committed.

## Scope

Probe 23's [scope](map-set-probe23-20261005.md#scope) unchanged. Two additions:

- **Shortest dependency path.** `shortestPath(edges, from, to): Option<List<String>>` is a breadth-first search over dependencies in insertion order: a queue of names, dequeued with `ListSlice(queue, 1, length)` and extended with `ListConcat`, and a parent map that also marks visited names. `walkBack` follows the parents from `to` and the path is reversed. `None` when `to` is not reached; `[from]` when they are equal.
- **Project length.** `projectLength(tasks: List<Task>, edges): Result<Int64, String>` takes `Task { name, days }` and the edges, orders the names with probe 23's `kahnOrder` (its `Err` on a cycle passes through), and folds the order into a finish map: a name finishes its own days (0 when no task names it) after the latest finish of its dependencies. The result is the largest finish or task duration, 0 for nothing.

## What was authored

4 types and 71 functions, 998 nodes in all, including 183 nodes of scale wrappers. `Frame` is again declared and unused.

| Group | Functions | Nodes | Probe 23 | Change |
| --- | --- | --- | --- | --- |
| Probe 21's scope | 21 functions | 175 | 175 | Unchanged |
| Levels | `notIn`, `removeAll`, `levelLoop`, `levels` | 43 | 43 | Unchanged |
| Cycle path | `cyclePath`, `dfsLoop`, `findCycle` | 128 | 128 | Unchanged graph; the recipe is shorter (below) |
| Linear Kahn | 10 functions | 100 | 100 | Unchanged |
| Components | `lowerParent`, `tarjanLoop`, `components` | 236 | 236 | Unchanged graph; `ListLastIndexOf` finds the component start, as since order 53 |
| Shortest path (new) | `notParented`, `setParent`, `bfsLoop`, `walkBack`, `shortestPath` | 80 | — | `bfsLoop` is 32 nodes. Two of the five are one-expression callbacks (8 nodes) |
| Project length (new) | `addTask`, `laterFinish`, `finishStep`, `maxOf`, `projectLength` | 53 | — | Three one-expression callbacks (15 nodes); `finishStep` is the fold step (16) |

The one rejection: `projectLength`'s first proposal referred to the `Err` arm's payload with slot `value`. The diagnostic named the depth and the slot the arm binds (`Error`), and the second proposal, with slot `error`, was accepted. It is an authoring slip, not a language cost.

### Self-calls with `keepUnlisted` (order 54)

Six loops had self-call arguments that pass a parameter through; `topoLoop` and `levelLoop` had none, so their self-calls are written in full.

| Function | Self-call arguments | Recipe JSON | Nodes |
| --- | --- | --- | --- |
| `tarjanLoop` | 70 → 23 | 10,896 → 8,000 characters (−27%) | 208 (probe 23: 208) |
| `dfsLoop` | 30 → 10 | 5,569 → 4,371 (−22%) | 105 (105) |
| `kahnLoop` | 4 → 3 | 2,370 → 2,314 | 45 (45) |
| `reachLoop` | 3 → 2 | 1,227 → 1,183 | 26 (26) |
| `bfsLoop` (new) | 4 → 2 | 1,615 → 1,503 | 32 |
| `walkBack` (new) | 3 → 2 | 840 → 792 | 18 |

Every one was accepted first time with the graph unchanged, and the JSON sizes agree with the [order 54 decision](../decisions/self-call-keep-unlisted.md#measurements)'s (8,007 and 4,376) within a few characters of harness formatting. The two new loops are small, so the shorthand saves little there; it matters for wide state such as `tarjanLoop`'s.

## Results

| Function | Cases | Result |
| --- | --- | --- |
| `countWords` | 200 texts: empty, spaces only, mixed case, repeated and empty words | 200/200 |
| `topWords` | The same texts with `k` from 0 to 4 | 200/200 |
| `topoOrder` | 200 edge lists: empty, a self-loop, a duplicate edge, a 3-cycle with a tail, random DAGs and random graphs | 200/200 (67 cyclic) |
| `reachable` | The same graphs from `a` | 200/200 |
| `dependentCounts` | The same graphs | 200/200 |
| `levels` | The same graphs | 200/200 |
| `findCycle` | The same graphs | 200/200 |
| `kahnOrder` | The same graphs | 200/200 |
| `shortestPath` (new) | The same graphs; `from` an edge's source, `to` reachable from it in two thirds of the cases, else any name; `a` to `a` and `a` to an absent name | 200/200 (145 found, 15 longer than two names) |
| `projectLength` (new) | The same graphs with 0 to 8 tasks (durations 0 to 5, repeated names, a name outside the graph) | 200/200 |
| `components` | The same graphs | 200/200 |
| **Total** | | **2,200/2,200** in 216.4 s |

## Scale

Warm `runPure` wall time of one call, process start included, measured as in probe 23 (each case twice, the second time shown). A fan is `r` depending on `m0` … `m(n−1)`, each of which depends on `t`: breadth-first from `r`, the queue holds `n` names before `t` is reached. A chain's queue never holds more than one name.

| Case | Time | Probe 23 or decision |
| --- | --- | --- |
| `topoOrder`, chain of 50 / 200 / 800; `kahnOrder`, chain of 200 / 800 | 0.12 / 0.20 / 0.67 s; 0.12 / 0.13 s | 0.12 / 0.19 / 0.65 s; 0.12 / 0.14 s |
| `reachable`, `dependentCounts`, `countWords`, `levels` | 0.12, 0.14, 0.11, 0.77 s | 0.13, 0.14, 0.12, 0.77 s |
| Reachable size of a built chain, 100,000 / 300,000 | 0.97 / 2.79 s | — |
| Topological order of a built chain, linear, 1,000 / 4,000 / 10,000 / 100,000 / 300,000 | 0.14 / 0.22 / 0.40 / 1.81 s / over the 5 s limit | 0.14 / 0.22 / 0.42 / 1.81 s / over |
| Cycle path of a closed chain, 10,000 / 40,000 / 100,000 / 300,000 | 0.28 / 0.64 / 1.18 / 3.61 s | 0.28 / 0.68 / 1.17 / 3.61 s |
| Cycle path of a comb, 1,000 / 10,000 / 40,000 / 100,000 | 0.15 / 0.43 / 1.05 / 2.25 s | 40,000 over; order 52: 1.09 s at 40,000 |
| Components of a chain, open / closed, 10,000 / 40,000 / 100,000 | 0.39 / 0.47, 0.81 / 1.11, 1.67 / 2.52 s | 0.39 / 0.47, 0.83 / 1.16, 1.66 / 2.62 s |
| Components of a comb, 1,000 / 4,000 / 10,000 / 20,000 / 40,000 / 100,000 | 0.19 / 0.37 / 0.60 / 1.03 / 1.82 / 4.57 s | 20,000 over; order 53: 1.73 s at 40,000 |
| Shortest path along a chain, 10,000 / 100,000 / 300,000 | 0.25 / 0.97 / 2.76 s | — |
| Project length of a chain of one-day tasks, 10,000 / 100,000 / 300,000 | 0.58 / 2.89 s / over the 5 s limit | — |
| **Shortest path across a fan, 1,000 / 4,000 / 10,000 / 20,000 / 40,000 / 100,000** | 0.13 / 0.26 / 0.65 / 2.58 s / **over** / **over** | — |
| **Reachable size of a fan, 1,000 / 10,000 / 40,000 / 100,000** | 0.14 / 0.68 s / **over** / **over** | — |

The project length over the limit at 300,000 is `kahnOrder`'s, which is over at 300,000 on its own; the finish fold adds about 1.1 s at 100,000 and is linear.

Building a fan is not the cost. In a second session on a copy of the snapshot, with functions that only build the fan and its dependency map, building 300,000 took 0.40 s and its dependency map 2.79 s, while the search went from 0.67 s at 10,000 to 2.55 s at 20,000 (four times for twice the names) and over the limit at 40,000. In the same session, `bfsLoop2` (41 nodes, 9 more than `bfsLoop`) keeps the same `ListConcat` and parent fold and changes only the dequeue: a two-stack queue, a front list popped with `ListLast` and `ListDropLast` and refilled with `ListReverse` of the back list when empty. Measured by the size of the parent map it returns (the path is not reconstructed), it ran 10,000 / 40,000 / 100,000 / 300,000 in 0.31 / 0.63 / 1.40 / 3.93 s, linear, against 2.79 s for building the fan and its map. So the copy is the `ListSlice` from start 1: the [order 52 decision](../decisions/persistent-vector-list.md) shares structure only for a slice from 0. `bfsLoop2` was written in the isolation session, not on the first authoring pass.

Tail self-calls held throughout: no run ended in a stack overflow.

## Costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **A breadth-first queue copies itself on every dequeue** | The shortest path and probe 21's `reachable`, unchanged for three probes, exceed the 5 s limit on a fan of 40,000 and are quadratic from 10,000 (0.65 s) to 20,000 (2.58 s). `reachable` passed every chain case only because a chain's queue holds one name. The input is constructed, but its shape is ordinary: any graph with a wide level, such as one name with many dependencies | The [order 52 decision's revisit condition](../decisions/persistent-vector-list.md#revisit-conditions), "a probe that dequeues with `ListSlice(1, …)` at scale needs a queue", is met by a constructed input; whether that counts is the owner's call. Two options: a non-copying front removal on the persistent vector (a slice from a nonzero start that shares structure, or a dedicated drop-first), or a queue type; or no language change, with the two-stack idiom (`ListLast`, `ListDropLast`, `ListReverse`; +9 nodes, linear) documented for authors. First observation |
| 2 | **One-expression callbacks** | 5 of the 10 new functions are callbacks of 3 to 6 nodes (`notParented`, `setParent`, `addTask`, `laterFinish`, `maxOf`: 23 nodes). `maxOf` only wraps `int64Max` for a `ListFold` | None proposed; third probe in a row (22, 23, 24) |
| 3 | **An `Option` that cannot be `None`** | `walkBack` reads a parent that always exists on the path from `to`, but `mapGet`'s `None` arm still needs a body | Not worth a builtin; recorded only. Second observation (23) |

Not needed: a loop construct, mutual recursion, a pair type, a `ListMax` (one fold), a separate visited set (the parent map is one). `keepUnlisted` covered every pass-through argument, and no new loop needed more than four parameters.

## Candidate hit counts after probe 24

| Candidate | Probes |
| --- | --- |
| Last item / non-copying stack operations | 22; **done as orders 50 and 52**. Both depth-first searches now run combs of 100,000 |
| Position of an item in a list | 22; **done as order 51**. Searching from the end: 23; **done as order 53** |
| Self-call naming only the changed arguments | 23; **done as order 54**, used in six loops here (`tarjanLoop` 70 → 23 arguments) |
| Breadth-first queue / non-copying dequeue | 24 (over the run limit on a fan; `reachable` from probe 21 included) |
| One-expression callbacks | 22, 23, 24 (recorded only) |
| `Option` that cannot be `None` | 23, 24 (recorded only) |
