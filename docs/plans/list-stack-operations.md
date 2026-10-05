# List stack operations

**Status:** Decided 2026-10-05: after reading the risks below, the owner chose the smaller change, `ListLast` and a store-sharing `ListDropLast`, implemented as order 50 ([decision](../decisions/list-last-and-drop-last.md)). The persistent vector is deferred until a program exceeds a run limit because pushes after pops copy a deep stack; the analysis below is kept as written for that day. **Revisited 2026-10-05:** [probe 23](../verification/map-set-probe23-20261005.md)'s comb exceeded the run limit, and the owner queued the persistent vector as [order 52](post-m2-roadmap.md#queue-from-the-2026-10-05-probe-23-owner-choice), now [implemented](../decisions/persistent-vector-list.md).

## The cost

Probe 22's depth-first search keeps an explicit stack of frames in a `List<Frame>`. Each turn reads the top with `ListItemAt(stack, ListLength(stack) − 1)` and replaces it with `ListAppendItem(ListSlice(stack, 0, ListLength(stack) − 1), updated)`. The loop function is 117 nodes, and a search 20,000 frames deep takes 1.95 s while 40,000 exceeds the 5 s run limit: the time grows quadratically.

The cause is the representation. A `NyList<T>` is the first `count` items of a shared array store ([C# backend](../specs/csharp-backend.md)). An append writes in place only when the list ends at the store's used count; `ListSlice` copies; so "drop the last, then append" copies the whole stack every turn.

## Why a small change is not enough

`ListLast` and a `ListDropLast` that shares the store (a prefix needs no copy) shorten the code and make the pop O(1), but the push after it still copies: the longer list still exists and owns the slot, and values are immutable, so the slot cannot be overwritten. Moving the frame's next index into a `Map` would leave only pushes and pops, but every push after a pop would still copy. Only a representation in which a new list shares structure with the old one without sharing a slot fixes the cost.

## Proposal

1. **Language.** `ListLast(list): Option<T>` (`None` for the empty list) and `ListDropLast(list): List<T>` (the empty list stays empty and never fails, as `ListSlice` clamps). Together with `ListAppendItem` they are push, peek and pop.
2. **Runtime.** `NyList<T>` becomes a persistent vector: a 32-way trie with a tail buffer, as Clojure's and Scala's vectors are. Append, drop-last and the item at an index are O(log₃₂ n), which is at most 4 levels for a million items, and the append and drop-last are effectively constant through the tail. A new list shares every untouched node with the old one and never writes into a node another list can see.

Semantics, equality, ordering, run values, Execution IR, snapshots and Nyason do not change, so nothing needs a schema version. Only the two new nodes go through every layer; order 48 touched 36 files for three builtins, which is the expected footprint.

## Risks

| Risk | Assessment | Mitigation or acceptance |
| --- | --- | --- |
| **Build time.** The runtime classes are emitted into every program, and every `runPure` builds it with Roslyn; a function's first call costs about 0.2–0.3 s more than a warm one today (probe 22) | Real: a trie adds code | Measure emitted source length and first-call time before and after on the same machine; the increase must stay small against 0.2–0.3 s |
| **Append throughput.** Probe 9 made 1,048,576 appends take 0.4 s through the shared store | Real: a pure tail copies up to 32 items per append | Rerun the [amortised append](../verification/list-amortised-append-20260930.md) benchmark; if it regresses materially, write in place into the tail only, with the current compare-exchange rule |
| **Indexing.** `ListItemAt` goes from O(1) to O(log₃₂ n) | Small: at most 4 steps | None |
| **Contiguous readers.** About 40 places in the runtime read `ReadOnlySpan<T> Items` (`SortBy`, `MinBy`, `Equals`, `GetHashCode`, `Fold`, `Map`, `Filter`, `NyText.Join`, …) | Real work, no complexity change: each already takes O(n) and will walk the leaves or copy them once | Rewrite them inside the runtime class. Nothing outside it uses `Items` or the store: run-value conversion and `AgentEval` use only `Create`, `Empty` and the public members, which stay |
| **`Concat` and general `Slice`** | Unchanged: both copy today and keep doing so (no relaxed radix balancing). A breadth-first queue dequeued with `ListSlice(1, …)` stays O(n) per step | Recorded; a queue is a separate candidate if a probe needs one |
| **Concurrency.** Lists are shared by tasks and `ParallelAll` workers | Lower than today: a fully persistent structure needs no compare-exchange. Only the tail fallback above would bring one back | Keep `ListAppendTests`' concurrent rounds |
| **Memory.** Small lists carry a root, a tail and a shift; a prefix keeps the larger tree alive | Real but bounded; the current store has the same retention for slices of a doubled buffer | Recorded only |
| **Trie correctness.** Off-by-one errors at the 32-item boundaries in push, pop and tail promotion | The main implementation risk | A randomized test of operation sequences (append, drop-last, item at, slice, concat, fold, equality) against .NET `List<T>`, across sizes that cross 32, 1,024 and 32,768, through the existing compiled-driver pattern of `ListAppendTests` |
| **Emitted-source tests.** `ListTests` asserts strings such as `Concat(NyList<T> other)` | Mechanical | Keep the public signatures; update the rest |

Not affected: `NyStr` keeps its own store, the [real-time processors](realtime-processors.md) are separate from ordinary code, and `examples/mini-work-planner/runner/Generated.cs` is a frozen copy of an older runtime that nothing regenerates.

## Acceptance criteria

- Probe 22's closed-chain cycle path at 20,000 and 40,000 frames runs in roughly linear time, well inside the run limit.
- The amortised append benchmark of 1,048,576 items stays near 0.4 s.
- The first-call time of a small program grows by no more than a small fraction of today's 0.2–0.3 s build.
- The randomized trie test and the existing fast and extended suites pass.
