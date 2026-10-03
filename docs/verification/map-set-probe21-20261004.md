# Map/Set probe 21 — 2026-10-04

**Status:** Complete. Orders 44-47 added `Map<K, V>` and `Set<T>` ahead of probe evidence. This probe used them in ordinary code: word counts with a top-k report, and a dependency planner (group-by into a map of sets, reachability, a topological order with cycle reporting, and direct dependent counts). The port matched a JavaScript reference on **1,000/1,000 cases** on the first full run. Every function was accepted on its first proposal, and nothing is blocked. The two largest costs are counted folds that cannot stop early and the get-or-default upsert.

**Date:** 2026-10-04

Baseline: nyrwvy `eb9748d` (main after #332, clean). The session server was built with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release`. The method was a new Node client over the maintained `createGraphTools` handlers, the same path as the [function-abort example](../../examples/function-abort/README.md): `simulateCreateTypesByName`, `nyrwvy_create_function` with name-resolved recipes, `commit`, and one `runPureByName` per case. Map and Set values crossed the `runPure` boundary directly as `map` and `set` run values, in both arguments and expectations. The harness, reference, snapshot and transcript are not committed.

## Scope

- **Word counts.** `countWords(text): Map<String, Int64>` lowercases, splits on a single space and skips empty words. `topWords(text, k): List<Count>` sorts by count descending, then name ascending, and keeps the first `k`.
- **Dependency planner.** An `Edge { from, to }` means `from` depends on `to`. `depsOf(edges): Map<String, Set<String>>` gives every node, including nodes that only appear as a dependency, in first-appearance order, with duplicate edges removed. `reachable(edges, start): Set<String>` gives the nodes reachable through at least one edge, in breadth-first discovery order. `topoOrder(edges): Result<List<String>, String>` is Kahn's algorithm that takes the smallest ready name first; when a cycle is left, it returns `Err("cycle: " + the remaining names sorted and comma-joined)`. `dependentCounts(edges): List<Count>` counts each node's direct dependents, zeros included, in the `topWords` order.

## What was authored

4 types (`Count`, `Edge`, and the loop states `Walk` and `Topo`) and 19 functions, 212 nodes in all.

| Group | Functions | Nodes | Map/Set operations used |
| --- | --- | --- | --- |
| Word counts | `bump`, `countWords`, `toCount`, `countBefore`, `topWords` | 51 | `MapGet`, `MapInsert`, `MapEmpty`, `MapFold` |
| Group-by | `addDep`, `depsOf` | 32 | `MapGet`, `MapInsert`, `MapContainsKey`, `SetInsert`, `SetFromList`, `SetEmpty` |
| Reachability | `expandDep`, `expandNode`, `walkStep`, `reachable` | 46 | `SetContains`, `SetInsert`, `SetToList`, `MapGet`, `MapSize` |
| Topological order | `nameBefore`, `isReady`, `dropDone`, `topoStep`, `topoOrder` | 64 | `MapFilter`, `MapKeys`, `MapRemove`, `MapFold`, `SetRemove`, `SetSize` |
| Dependent counts | `zeroAll`, `addDependents`, `dependentCounts` | 19 | `MapFold` (twice), `SetToList`; reuses `bump` |

## Results

| Function | Cases | Result |
| --- | --- | --- |
| `countWords` | 200 texts: empty, spaces only, mixed case, repeated and empty words | 200/200 |
| `topWords` | The same texts with `k` from 0 to 4 | 200/200 |
| `topoOrder` | 200 edge lists: empty, a self-loop, a duplicate edge, a 3-cycle with a tail, random DAGs and random graphs | 200/200 (3 cyclic) |
| `reachable` | The same graphs from `a` | 200/200 |
| `dependentCounts` | The same graphs | 200/200 |
| **Total** | | **1,000/1,000** in 74.5 s |

## Scale

Warm `runPure` wall time of one call, process start included. A chain has `n − 1` edges.

| Case | Time |
| --- | --- |
| `topoOrder`, chain of 50 / 200 / 800 | 0.11 / 0.20 / 0.67 s |
| `reachable`, chain of 800 from the far end | 0.10 s |
| `dependentCounts`, chain of 800 | 0.12 s |
| `countWords`, 4,000 words, 700 distinct | 0.10 s |

`topoOrder` grows about quadratically: each step filters the whole pending map, sorts the ready names to take the smallest, and rebuilds the map to drop the finished name.

## Costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **Counted folds cannot stop early** | `reachable` and `topoOrder` iterate with `ListFold` over `ListRange(0, MapSize(deps))`, the worst-case bound. Each needed a state Product (`Walk`, `Topo`) and a step function. The fold keeps running after the frontier is empty or the order is stuck, and `topoStep` has to pass the state through unchanged on `None` | A loop with a stop condition. This is the second ordinary-code observation after probe 15's tail self-call and probe 13's `while`, and the subject of the loop design note |
| 2 | **Get-or-default upsert** | Every accumulating write is `MapInsert(m, k, MatchOption(MapGet(m, k), f(value), default))`. `bump` is 15 nodes for `m[k] = m.get(k, 0) + 1`. `addDep` is 29 nodes for `setdefault(from).add(to)` plus `setdefault(to)`. `MapFromList` does not help, because it indexes and does not accumulate | `MapGetOrDefault(map, key, default): V`, as Python `dict.get(k, d)`. `bump` becomes 13 nodes and `addDep` about 21: a modest node saving, but each write then reads as one expression with no `MatchOption` scope. An update-with-callback form would need a helper function at every use site, so the plain default is the smaller change |
| 3 | **Mapping a map's values** | Dropping a finished name from every pending set took a `dropDone` folder (6 nodes) and a `MapFold` that rebuilds the map from `MapEmpty` | `MapMapValues(map, function)` keeping the keys and their order. First observation |
| 4 | **Smallest item** | The smallest ready name is `ListItemAt(ListSortBy(…), 0)`: a full sort for one item in each step | `ListMinBy(list, lessThan): Option<T>`. First observation |
| 5 | **Entries as records** | Reporting a map needs a record type and a `toCount` folder (5 nodes), since there is no pair type and no entry list | Not worth a builtin while a 5-node folder covers it; recorded only |

Not needed: `SetFold` (`SetToList` and `ListFold`, 1 extra node, were used twice), a Map or Set as a key, Product keys, update-with-callback, and merging maps. Map and Set run values needed no workaround.

## Candidate hit counts after probe 21

| Candidate | Probes |
| --- | --- |
| Map get-or-default | 21 (strong: every accumulating write) |
| Loop with a stop condition (tail self-call as a loop, `while`, `IndexFold`) | 13 (`while`), 15 (tail self-call), 21 (two counted folds) |
| Mapping map values | 21 |
| Smallest item of a list | 21 |
