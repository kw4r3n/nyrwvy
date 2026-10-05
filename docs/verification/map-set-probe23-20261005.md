# Map/Set probe 23 — 2026-10-05

**Status:** Complete. Probe 22's planner was authored again on orders 50 (`ListLast`, `ListDropLast`) and 51 (`ListIndexOf`, `ListFindIndex`), and extended with a linear Kahn's algorithm and Tarjan's strongly connected components. The port matched a JavaScript reference on **1,800/1,800 cases** on the first full run, and every one of the 54 functions was accepted on its first proposal. The cycle path is 128 nodes instead of 145 and is linear on a chain: 40,000 frames in 0.68 s, as the order 50 decision measured (0.66 s), and 300,000 in 3.6 s. Nothing is blocked. The largest cost is the one order 50 left open: a search whose pushes follow pops copies its stack each time, so on a comb-shaped graph both the cycle path and the components **exceed the 5 s run limit** (40,000 and 20,000 nodes). The second is threading loop state through a tail self-call: `tarjanLoop` is 208 nodes with 10 parameters, and about a quarter of them pass parameters through unchanged.

**Date:** 2026-10-05

Baseline: nyrwvy `4ed78e8` (main after #340, clean). The session server was built with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release`. The method was probe 22's: a Node client over the maintained `createGraphTools` handlers, `simulateCreateTypesByName`, `create_function` with name-resolved recipes, `commit`, and one `runPureByName` per case, against a JavaScript reference over a seeded corpus (seed 23, so the graphs differ from probe 22's). Node counts are the `nodeCount` of each `create_function` review. The harness, reference, snapshot and transcript are not committed.

## Scope

Probe 22's [scope](map-set-probe22-20261005.md#scope) unchanged, with `findCycle` rewritten on orders 50-51. Two additions:

- **Linear topological order.** `kahnOrder(edges)` gives the same result as `topoOrder` (smallest ready name first, the same `Err` on a cycle) from in-degree counts and a dependents map: each turn decrements the counts of the chosen name's dependents and adds those that reach zero to a ready set. Probe 22 said this would make the order linear without a language change.
- **Components.** `components(edges): List<List<String>>` is Tarjan's algorithm, iterative: roots in first-appearance order, dependencies in insertion order, components in the order Tarjan closes them (each one sorted). It keeps two stacks, the call stack of names and the component stack, plus index, low-link and next-dependency maps and an on-stack set, all as parameters of one tail-recursive `tarjanLoop`. The reference was checked against mutual reachability on all 200 graphs.

## What was authored

3 types and 54 functions, 801 nodes in all, including 119 nodes of scale wrappers that build chains and combs in the program. `Frame` was declared with probe 22's types and left unused; the program needs only `Count` and `Edge`.

| Group | Functions | Nodes | Probe 22 | Change |
| --- | --- | --- | --- | --- |
| Probe 21's scope | 21 functions | 175 | 175 | Unchanged |
| Levels | `notIn`, `removeAll`, `levelLoop`, `levels` | 43 | 43 | Unchanged |
| Cycle path | `cyclePath`, `dfsLoop`, `findCycle` | 128 | 145 (`frameNode`, `collectFrom`, `dfsLoop`, `findCycle`) | The stack holds names and each node's next dependency index lives in a `Map`, so neither `Frame` nor `ProductUpdate` is used; `ListLast` peeks and `ListDropLast` pops. `dfsLoop` is 105 nodes (probe 22: 117): the map read and write for the next index cost most of what the frame removed. `cyclePath` (12) is `ListIndexOf` and a `ListSlice`, replacing the 16-node `collectFrom` fold |
| Linear Kahn (new) | `depCount`, `noDependents`, `addDependent`, `addDependents2`, `isZero`, `isPositive`, `isZeroIn`, `decrement`, `kahnLoop`, `kahnOrder` | 100 | — | Twice the naive form's 45 nodes. Seven of the ten functions are one-expression callbacks (33 nodes). The turn needs two results, the new counts and the newly ready names; a fold over the dependents returns the counts and a `ListFilter` over them reads the counts back, so no pair type was needed |
| Components (new) | `lowerParent`, `tarjanLoop`, `components` | 236 | — | `tarjanLoop` alone is 208 nodes; see cost 2. `ListIndexOf` finds the component's start on the component stack and two `ListSlice`s split it |

## Results

| Function | Cases | Result |
| --- | --- | --- |
| `countWords` | 200 texts: empty, spaces only, mixed case, repeated and empty words | 200/200 |
| `topWords` | The same texts with `k` from 0 to 4 | 200/200 |
| `topoOrder` | 200 edge lists: empty, a self-loop, a duplicate edge, a 3-cycle with a tail, random DAGs and random graphs | 200/200 (71 cyclic) |
| `reachable` | The same graphs from `a` | 200/200 |
| `dependentCounts` | The same graphs | 200/200 |
| `levels` | The same graphs | 200/200 |
| `findCycle` | The same graphs | 200/200 |
| `kahnOrder` | The same graphs | 200/200 |
| `components` | The same graphs | 200/200 |
| **Total** | | **1,800/1,800** in 174.9 s |

## Scale

Warm `runPure` wall time of one call, process start included, measured as in probe 22 (each case twice, the second time shown). A chain's DFS is shallow, because its first root `n1` depends only on `n0`; a closed chain adds `n0 → n(n−1)`, so the search from `n1` goes the whole length. A comb is a closed chain whose every `n(i)` depends first on a leaf `l(i)` and then on `n(i−1)`: the search pops the leaf before every push, so each push follows a pop.

| Case | Time | Probe 22 |
| --- | --- | --- |
| `topoOrder`, chain of 50 / 200 / 800 | 0.12 / 0.19 / 0.65 s | 0.11 / 0.19 / 0.64 s |
| `kahnOrder`, chain of 200 / 800 | 0.12 / 0.14 s | — |
| `reachable`, `dependentCounts`, `countWords`, `levels` | 0.13, 0.14, 0.12, 0.77 s | 0.11, 0.14, 0.11, 0.82 s |
| Topological order of a built chain, naive, 1,000 / 4,000 | 0.72 s / over the 5 s limit | 0.72 s / over |
| Topological order of a built chain, linear, 1,000 / 4,000 / 10,000 / 100,000 / 300,000 | 0.14 / 0.22 / 0.42 / 1.81 s / over the 5 s limit | — |
| Cycle path of a closed chain, 10,000 / 20,000 / 40,000 / 100,000 / 300,000 | 0.28 / 0.43\* / 0.68 / 1.17 / 3.61 s | 0.43 s / 1.95 s / over / — / — |
| Cycle path of a comb, 1,000 / 4,000 / 10,000 / 20,000 / 40,000 | 0.16 / 0.27 / 0.78 / 2.24 s / **over the 5 s limit** | — |
| Components of a chain, open / closed, 10,000 / 40,000 / 100,000 | 0.39 / 0.47, 0.83 / 1.16, 1.66 / 2.62 s | — |
| Components of a comb, 1,000 / 4,000 / 10,000 / 20,000 | 0.33 / 0.85 / 2.92 s / **over the 5 s limit** | — |

\* From the isolation session below.

Building a comb is not the cost: in a second session on a copy of the snapshot, with two added functions that only build the comb and its dependency map, building a 40,000-node comb took 0.13 s and its dependency map 0.52 s, while the search from 10,000 to 20,000 nodes took 2.7 times as long and 40,000 exceeded the limit. On the closed chain, where only pushes follow pushes, the same search is linear. This is the known limit the [order 50 decision](../decisions/list-last-and-drop-last.md) recorded (0.80 s and 2.4 s at 10,000 and 20,000), now measured past the run limit.

The components loop is quadratic on the comb for more than one reason, and the probe did not separate them: the call stack pushes after a pop, every leaf becomes a one-name component, so `ListIndexOf` scans the component stack from the front to find it at the top, and the component stack is truncated with `ListSlice` and pushed onto again.

The linear Kahn's algorithm confirms probe 22's claim: 100,000 names in 1.8 s against 4,000 over the limit for the naive form, with no language change. Tail self-calls held throughout: no run ended in a stack overflow.

## Costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **A push after a pop copies the stack** | The cycle path of a 40,000-node comb and the components of a 20,000-node comb exceed the 5 s limit; a closed chain of 300,000 does not. The input is constructed, but its shape is ordinary: any node with a leaf dependency before a deep one | The order 50 decision's [revisit condition](../decisions/list-last-and-drop-last.md#revisit-conditions), "a program exceeds a run limit because pushes after pops copy a deep stack", is met by a constructed input; whether that counts is the owner's call. The persistent-vector `NyList<T>` of the [design note](../plans/list-stack-operations.md) addresses the cycle path; the components would also need the component start found without scanning from the front (a `ListLastIndexOf`, or a position map kept by the author). Second observation of the stack (probe 22 first) |
| 2 | **Loop state threaded through every self-call** | `tarjanLoop` has 10 parameters and 7 self-calls; 47 of the 70 arguments pass a parameter through unchanged, about 23% of its 208 nodes. `dfsLoop` passes 20 of 30 through. The harness hid this with a helper that spreads the unchanged arguments, which a recipe cannot do. Scope depths still needed a comment per level, as in probe 22 | A recipe shorthand for a self-call that names only the changed arguments, expanding to the full `DirectCall` as `otherwise` expands to a variant list; or a state Product with `ProductUpdate`, which exists today and the probe did not try. First observation |
| 3 | **One-expression callbacks** | The linear Kahn's algorithm has seven callbacks of 1 to 9 nodes (`depCount`, `noDependents`, `isZero`, `isPositive`, …), each a declared function because a callback must be one. Map callbacks take the key even when they ignore it | None proposed; recorded with probe 22's `zeroFor` and `notIn` |
| 4 | **An `Option` that cannot be `None`** | `ListIndexOf(scc, top)` runs only when `top` is on the component stack, but its `None` arm still needs a body (a self-call that cannot run) | Not worth a builtin; recorded only |

Not needed: a loop construct, mutual recursion, a pair type, `ListFindIndex` (`ListIndexOf` covered both uses), `ListLast` on anything but the stack top. Every self-call was again in tail position without rearranging.

## Follow-up: loop state in a Product

After orders 52 and 53, cost 2's untried alternative was authored in the same session: `tarjanLoop2(deps, roots, root, state)` keeps the seven changing values in a `Tarjan` Product, reads them with `FieldAccess` and passes `ProductUpdate(state, changed fields)` to each self-call. It was accepted first time, matched `components` on all 200 graphs, and ran the comb of 10,000 / 20,000 / 40,000 in 0.67 / 0.97 / 1.73 s, the same as the parameter form.

It is **236 nodes instead of 208** (+13%). Each self-call names only the changed fields, which removes the 47 pass-through arguments, but every read of the state becomes a two-node `FieldAccess` instead of a one-node `Parameter`, and the loop reads the state more often than it passes it through; the type adds a declaration. The positional scope references are unchanged. So `ProductUpdate` does not cover cost 2, and a self-call that names only the changed arguments, expanding to the full `DirectCall`, would remove the 47 nodes (about 23%) without the reads' cost.

## Candidate hit counts after probe 23

| Candidate | Probes |
| --- | --- |
| Last item / non-copying stack operations | 22; **done as order 50**, used here by both searches. Non-copying push after pop (persistent vector): 22, 23, over the run limit on a comb |
| Position of an item in a list | 22; **done as order 51**, used twice here. Searching from the end: 23 |
| Self-call naming only the changed arguments | 23 (a state Product with `ProductUpdate` was 13% larger, not smaller) |
