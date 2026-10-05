# Persistent-vector List

- **Status:** Accepted
- **Date:** 2026-10-05
- **Roadmap:** [post-M2 queue from the 2026-10-05 probe 23 owner choice](../plans/post-m2-roadmap.md#queue-from-the-2026-10-05-probe-23-owner-choice), order 52

[Probe 23](../verification/map-set-probe23-20261005.md)'s depth-first searches exceeded the 5 s run limit on a comb, a graph whose every node depends first on a leaf, so every push onto the stack follows a pop. With order 50's `ListDropLast` sharing an array store, that push copied the whole stack. The owner counts a constructed input of realistic shape, which met the [order 50 decision](list-last-and-drop-last.md#revisit-conditions)'s revisit condition.

## Decision

`NyList<T>` is a persistent vector, as the [design note](../plans/list-stack-operations.md) proposed: a 32-way trie of full leaves and a tail of 1 to 32 items. Append, drop-last, last, item-at and a slice from 0 share every untouched node and take O(log₃₂ n); a push after a pop copies at most the 32-item tail. The tail keeps the compare-exchange rule of the old store, so a chain of appends writes in place. The [C# backend specification](../specs/csharp-backend.md) describes the representation.

Language, semantics, run values, Execution IR, snapshots and Nyason are unchanged, and the public members of `NyList<T>` keep their signatures. A slice from 0 now shares nodes instead of copying, which the design note did not propose; it falls out of the trie, since a prefix is a shorter count over the same nodes, and it makes `ListSlice(stack, 0, i)` a constant-size truncation.

## Measurements

One Linux machine through `runPure`, warm calls, the same build path before and after (main `4c72ebb` and this change):

| Case | Before | After |
| --- | --- | --- |
| Probe 23 cycle path, comb of 10,000 / 20,000 / 40,000 | 0.81 / 2.26 s / over the 5 s limit | 0.48 / 0.69 / 1.09 s |
| Probe 23 cycle path, closed chain of 40,000 / 300,000 | 0.69 / 3.46 s | 0.64 / 3.72 s |
| Probe 23 components, comb of 4,000 / 10,000 / 20,000 | 0.89 / 2.92 s / over | 0.90 / 2.70 s / over |
| Linear Kahn, chain of 100,000 | 1.84 s | 1.86 s |
| Probe 22 reachability (`ListConcat` each turn), chain of 100,000 / 300,000 | 0.97 / 2.64 s (probe 22) | 0.98 / 2.83 s |
| Amortised append benchmark, 1,048,576 code points ([method](../verification/list-amortised-append-20260930.md#measurements)) | 0.215 s | 0.292 s |
| A tiny function: first call (build) / warm call, median of five | 0.198 / 0.060 s | 0.199 / 0.059 s |

The comb search is now linear and the build cost is unchanged. 1M appends cost about 80 ms more, still under the design note's 0.4 s bound: rebuilding a 1M list from a code-point array now splits it into leaves, and each 32nd append copies a trie path. The chain search and the reachability are about 7% slower at 300,000, the cost of the trie walk in `ItemAt` and of appending a concatenation item by item. Probe 23's 1,800 cases still match their reference on the new runtime.

The components still exceed the limit on a comb of 20,000. Their remaining per-pop steps are linear in the depth: `ListIndexOf` scans the component stack from the front to find a one-name component at the top, and `SetDifference(onStack, component)` folds over the whole on-stack set. Both are outside this decision; see [Revisit conditions](#revisit-conditions).

## Consequences

- `ListAppendTests`' compiled driver adds random operation sequences (append, drop-last, any slice, a slice from 0, concat, a rebuilt list) against .NET `List<T>`, at sizes that cross 32, 1,024 and 32,768 items, and checks every earlier list again at the end. Its concurrent rounds now start from bases of up to 70 items, so they cross a leaf boundary.
- `ListConcat` appends the right list's items one at a time instead of copying an array once. It stays linear in the right list's length.
- Memory: a small list carries a tail array; a dropped or sliced list keeps the larger trie alive, as a slice of the old doubled store did.
- `NyText.Join` copies the list to an array first.

## Revisit conditions

- **The components loop on a comb.** A from-the-end search (`ListLastIndexOf`) and a `SetDifference` that folds over the smaller operand when the right one is smaller would make each pop proportional to the component; the second needs no language change. Propose both together. **Done as order 53** ([decision](list-last-index-and-set-difference.md)): the comb of 40,000 takes 1.73 s.
- **A breadth-first queue.** A slice from any start but 0 still copies; a probe that dequeues with `ListSlice(1, …)` at scale needs a queue. [Probe 24](../verification/map-set-probe24-20261005.md) met it on a fan of 40,000: **done as order 55**, a slice from any start shares the trie ([decision](list-slice-sharing.md)).
