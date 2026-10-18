# ListLast and ListDropLast

- **Status:** Accepted
- **Date:** 2026-10-05
- **Roadmap:** [post-M2 queue from the 2026-10-05 probe 22 owner choice](../plans/post-m2-roadmap.md#queue-from-the-2026-10-05-probe-22-owner-choice), order 50
- **Design note:** [List stack operations](../../docs-archive/2026-10-decided-plans/list-stack-operations.md)

[Probe 22](../verification/map-set-probes-summary.md#probe-22)'s depth-first search kept an explicit stack in a `List<Frame>`, read its top with `ListItemAt(stack, ListLength(stack) − 1)` and replaced it with `ListAppendItem(ListSlice(stack, 0, ListLength(stack) − 1), …)`. The loop was 117 nodes and quadratic: 1.95 s at 20,000 frames, over the 5 s limit at 40,000. The [design note](../../docs-archive/2026-10-decided-plans/list-stack-operations.md) weighed a persistent-vector `NyList<T>` against two small builtins on the current store. The owner chose the small change on 2026-10-05, with the persistent vector deferred to a measured need.

## Decision

- **`ListLast(list): Option<T>`**: `Some` last item, `None` for the empty list. Never fails.
- **`ListDropLast(list): List<T>`**: the list without its last item, as Python `items[:-1]`; the empty list stays empty, as `ListSlice` clamps. Never fails.
- **Runtime.** `NyList<T>.Last()` reads the item before the count. `NyList<T>.DropLast()` returns a list over the same store with a count one smaller, so a pop is constant time. The store's used count is unchanged, so an append to the popped list loses the compare-exchange and copies once; no slot is rewritten, and every value stays immutable.

`ListSlice` keeps copying: a prefix that shares a large store would keep it alive, which the [amortised append record](../verification/list-amortised-append-20260930.md) chose to avoid. A pop shares, because a stack's prefix is about as large as the stack.

## Reason

**The small change covers the measured case.** With `ListLast` and `ListDropLast`, and the next index of each node in a `Map` instead of in a frame, probe 22's search needs no `Frame` type, and its stack only grows and shrinks. On the same closed chain it took 0.42 s at 20,000 frames, 0.66 s at 40,000 and 3.4 s at 300,000, and the loop is 108 nodes instead of 117.

**The persistent vector's risks fall on every program.** It would put a larger runtime into every build and its 32-way trie at the centre of every list, while only stacks thousands deep benefit. The two builtins are the same either way, so changing the representation later needs no change to programs.

## Consequences

- `ListLast` and `ListDropLast` go through the semantic graph, validation (`ListLastInputTypeMismatch`, `ListDropLastInputTypeMismatch`), snapshot and Nyason codecs, typed semantic IR, Execution IR, the C# backend, the viewer, wire recipes and the recipe reference. No IR schema version changes. A snapshot that uses them is written at snapshot JSON 13 / Nyason `nayamnyam.snapshot.v9` since 2026-10-05; see [snapshot schema versions](../specs/schema-evolution-policy.md#snapshot-schema-versions).
- `StandardOperationTests` covers both through every layer, and `ListAppendTests`' compiled driver checks that pushing twice onto one popped list leaves the original and both results independent.
- **Known limit.** A push right after a pop copies the stack. A search that pops a leaf before every push on a deep path is still quadratic: on a comb whose every node depends first on a leaf, 10,000 nodes take 0.80 s and 20,000 take 2.4 s (the frame version: 1.0 s and 4.7 s).

## Revisit conditions

- **A program exceeds a run limit because pushes after pops copy a deep stack.** Then make `NyList<T>` a persistent vector, following the [design note](../../docs-archive/2026-10-decided-plans/list-stack-operations.md)'s risks and acceptance criteria. **Met on 2026-10-05** by [probe 23](../verification/map-set-probes-summary.md#probe-23)'s comb, a constructed input of realistic shape; done as order 52 ([decision](persistent-vector-list.md)).
