# Map/Set probe 22 — 2026-10-05

**Status:** Complete. Probe 21's word counts and dependency planner were authored again on orders 48 (`MapGetOrDefault`, `MapMapValues`, `ListMinBy`) and 49 (tail self-calls as loops), and extended with two loops of new shapes: parallel levels and one cycle path found by depth-first search. The port matched a JavaScript reference on **1,400/1,400 cases** on the first full run, and every one of the 34 functions was accepted on its first proposal. Probe 21's five groups take 175 nodes in 21 functions and two types, instead of 212 nodes in 19 functions and four types. Tail self-calls ran 300,000 turns in constant stack. Nothing is blocked. The largest new cost is the explicit stack of the depth-first search: 117 nodes for one loop function, and time that grows quadratically, consistent with copying the stack each turn.

**Date:** 2026-10-05

Baseline: nyrwvy `b32b902` (main after #336, clean). The session server was built with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release`. The method was probe 21's: a Node client over the maintained `createGraphTools` handlers, `simulateCreateTypesByName`, `create_function` with name-resolved recipes, `commit`, and one `runPureByName` per case, against a JavaScript reference over a seeded corpus. Node counts are the `nodeCount` of each `create_function` review. The harness, reference, snapshot and transcript are not committed.

## Scope

Probe 21's [scope](map-set-probe21-20261004.md#scope) unchanged: `countWords`, `topWords`, `depsOf`, `reachable` (breadth-first discovery order), `topoOrder` (Kahn's algorithm, smallest ready name first, `Err("cycle: " + remaining names sorted and joined with ", ")`) and `dependentCounts`. Two additions:

- **Levels.** `levels(edges): Result<List<List<String>>, String>` gives every ready name at once, sorted, as one level, removes them all, and repeats; a cycle gives the same `Err` as `topoOrder`.
- **Cycle path.** `findCycle(edges): Option<List<String>>` runs a depth-first search from each node in first-appearance order, visiting dependencies in insertion order, and returns the first cycle it closes as `[w, …, w]`: the stack from `w` to the top, then `w` again. The search keeps an explicit stack of `Frame { node, next }` (the node and the index of its next dependency) and a colour map, all as parameters of one tail-recursive function.

## What was authored

3 types (`Count`, `Edge` and `Frame`) and 34 functions, 413 nodes in all, including 50 nodes of scale wrappers that build chains in the program. `frameNode` (2 nodes) was written and left unused.

| Group | Functions | Nodes | Probe 21 | Change |
| --- | --- | --- | --- | --- |
| Word counts | `bump`, `countWord`, `countWords`, `toCount`, `countBefore`, `report`, `topWords` | 56 | 51 | The upsert `bump` is 9 nodes with `MapGetOrDefault`. The empty-word check moved to `countWord` (7) so `dependentCounts` can reuse `bump`, and the sort moved to `report` (4), which `dependentCounts` also reuses; with them, the group is 5 nodes larger than probe 21's |
| Group-by | `addDep`, `depsOf` | 25 | 32 | `addDep` is 22 nodes (probe 21: 29), two `MapGetOrDefault`s and no `MatchOption` |
| Reachability | `notSeen`, `reachLoop`, `reachable` | 36 | 46 | One tail-recursive `reachLoop(deps, frontier, seen)` replaces the `Walk` state, the step function and the counted fold, and stops when the frontier is empty |
| Topological order | `nameBefore`, `isReady`, `dropDone`, `cycleError`, `topoLoop`, `topoOrder` | 45 | 64 | `topoLoop(pending, order)` returns the `Result` itself, so its self-call is in tail position; `ListMinBy` replaces the sort, `MapMapValues` the rebuilding fold, and the `Topo` type is gone. `cycleError` is shared with `levels` |
| Dependent counts | `zeroFor`, `addDependents`, `dependentCounts` | 13 | 19 | The zero map is `MapMapValues(deps, zeroFor)`; the report is the shared `report` |
| **Probe 21's scope** | 21 functions, 2 types | **175** | **212** (19 functions, 4 types) | −17% |
| Levels (new) | `notIn`, `removeAll`, `levelLoop`, `levels` | 43 | — | `MapFilter` drops the level's names and `MapMapValues` with `SetDifference` drops them from every pending set |
| Cycle path (new) | `collectFrom`, `dfsLoop`, `findCycle` (and the unused `frameNode`) | 143 (+2) | — | `dfsLoop` alone is 117 nodes; see cost 1 |

## Results

| Function | Cases | Result |
| --- | --- | --- |
| `countWords` | 200 texts: empty, spaces only, mixed case, repeated and empty words | 200/200 |
| `topWords` | The same texts with `k` from 0 to 4 | 200/200 |
| `topoOrder` | 200 edge lists: empty, a self-loop, a duplicate edge, a 3-cycle with a tail, random DAGs and random graphs | 200/200 (63 cyclic) |
| `reachable` | The same graphs from `a` | 200/200 |
| `dependentCounts` | The same graphs | 200/200 |
| `levels` | The same graphs | 200/200 |
| `findCycle` | The same graphs | 200/200 (63 cycles found) |
| **Total** | | **1,400/1,400** in 123.6 s |

## Scale

Warm `runPure` wall time of one call, process start included: each case ran twice and the second time is shown, because the first call of a function in a session also builds it (about 0.2–0.3 s more). A chain of `n` nodes has `n − 1` edges. The `chain…` rows build the chain inside the program from `ListRange`, because a run value holds at most 4,096 list items.

| Case | Time | Probe 21 |
| --- | --- | --- |
| `topoOrder`, chain of 50 / 200 / 800 | 0.11 / 0.19 / 0.64 s | 0.11 / 0.20 / 0.67 s |
| `reachable`, chain of 800 from the far end | 0.11 s | 0.10 s |
| `dependentCounts`, chain of 800 | 0.14 s | 0.12 s |
| `countWords`, 4,000 words, 700 distinct | 0.11 s | 0.10 s |
| `levels`, chain of 800 | 0.82 s | — |
| `findCycle`, chain of 800 closed into a cycle | 0.11 s | — |
| Topological order of a built chain, 1,000 / 2,000 / 3,000 / 4,000 | 0.72 / 2.01 / 4.31 s / over the 5 s limit | — |
| Reachability of a built chain from the far end, 10,000 / 100,000 / 300,000 / 1,000,000 | 0.24 / 0.97 / 2.64 s / over the 5 s limit | — |
| Cycle path of a built closed chain, 1,000 / 4,000 / 10,000 / 20,000 / 40,000 | 0.11 / 0.17 / 0.43 / 1.95 s / over the 5 s limit | — |

The tail self-calls held: `reachLoop` ran 300,000 turns and `dfsLoop` 20,000 turns with a stack 20,000 frames deep with no stack overflow, well past the few thousand nested calls that bound other recursion. No run ended in a stack overflow.

Time is unchanged from probe 21 where the algorithm is unchanged. The topological order is still about quadratic: `ListMinBy` makes the choice linear, but each turn still filters the whole pending map and maps every pending set. That is the naive form of Kahn's algorithm, chosen to match probe 21; keeping in-degree counts and a dependents map would make it linear without any language change. Reachability is linear. The cycle path grows quadratically (10,000 to 20,000 turns takes 4.5 times as long), consistent with the slice and append copying the stack each turn (cost 1).

## Costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **An explicit stack is long and quadratic** | `dfsLoop` is 117 nodes, 28% of the program. Peeking, popping and replacing the top frame are `ListItemAt(stack, ListLength(stack) − 1)`, `ListSlice(stack, 0, ListLength(stack) − 1)` and `ListAppendItem` of a `ProductUpdate`; the slice and the append each copy the list, and a search 20,000 frames deep takes 1.95 s while 40,000 exceeds the run limit. The body nests two `MatchOption`s and two `Bind`s, and the positional references to the top frame, the rest of the stack and the next dependency shift at each level (the `None` arm also opens a frame); the harness needed a comment per level to keep them straight, though every function was accepted first time | The natural form, a recursive `visit(node)` over the dependencies, is not a tail call, so order 49 does not make it a loop; it is bounded by the stack at a few thousand frames. Candidates, none queued: a last-item access (`ListLast`, or a `ListItemAt` that counts from the end), or a list whose append and drop-last do not copy. First observation |
| 2 | **Finding an item's position** | Taking the stack from `w` to the top needed `collectFrom` (16 nodes): a `ListFold` that starts collecting at the first frame whose node equals `w`. There is no `ListIndexOf` or first-index-where | `ListFindIndex(list, predicate): Option<Int64>`, or `ListIndexOf` for equatable items. First observation |
| 3 | **Removing several keys** | `levels` removes the names of one level from the pending map with `MapFilter` and a `notIn` callback (4 nodes), then from every pending set with `MapMapValues` and `SetDifference` (3 more). `MapFilter` covers it | Not worth a builtin; recorded only |

Not needed: a loop construct, `IndexFold`, mutual recursion, `SetFold`, a pair type. No self-call had to be rearranged into tail position: each loop's natural shape was "match, then call yourself in each arm", and the one trap the [decision](../decisions/tail-self-calls.md) names, a wrapped call such as `ResultOk(loop(…))`, did not arise because each loop function returns its caller's whole result type. The review does not mark which self-calls are tail calls; every one in this probe was, and nothing was misread, so the decision's second revisit condition is not met. No loop wanted to go through two functions, so the first is not met either.

## Candidate hit counts after probe 22

| Candidate | Probes |
| --- | --- |
| Loop with a stop condition (tail self-call as a loop, `while`, `IndexFold`) | 13, 15, 21; **met by order 49** for ordinary code: probe 22's three loops are tail self-calls that stop when done |
| Last item / non-copying stack operations | 22 |
| Position of an item in a list | 22 |
| Map get-or-default, mapping map values, smallest item of a list | 21; done as order 48, used here 6, 3 and 1 times |
