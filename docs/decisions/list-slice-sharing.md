# List slices that share structure

- **Status:** Accepted
- **Date:** 2026-10-05
- **Roadmap:** [post-M2 queue from the 2026-10-05 probe 24 owner choice](../plans/post-m2-roadmap.md#queue-from-the-2026-10-05-probe-24-owner-choice), order 55

[Probe 24](../verification/map-set-probes-summary.md#probe-24)'s breadth-first searches dequeue with `ListSlice(queue, 1, length)`. After order 52 a slice from 0 shared the persistent vector's nodes, but a slice from any other start copied its items, so each dequeue copied the queue: on a fan of 40,000 both the shortest path and probe 21's `reachable` exceeded the 5 s run limit. That met the [order 52 decision](persistent-vector-list.md#revisit-conditions)'s revisit condition for a breadth-first queue. Of the three options in the [roadmap](../plans/post-m2-roadmap.md#queue-from-the-2026-10-05-probe-24-owner-choice), the slice that shares structure was chosen over a `Queue<T>` type and over documenting the two-stack idiom.

## Decision

`NyList<T>` keeps a start position in its trie as well as an end, and `ListSlice` from any start shares the list's nodes: it moves the start and, like a slice from 0, the end. A dequeue is O(log₃₂ n) and copies nothing. Language, semantics, run values, Execution IR, snapshots and Nyason are unchanged, so existing programs such as `reachable` become linear without a change. The [C# backend specification](../specs/csharp-backend.md) describes the representation.

- **Retention.** A slice that would keep fewer than half of the trie positions up to its end copies its items instead, so a small slice of a large list never keeps more than twice its own length alive through its start. Repeated dequeues copy the queue once each time it halves, which keeps them amortised O(log₃₂ n) each. A slice from 0 still shares whatever its length, as since order 52.
- **Every operation** reads from the start instead of 0. Appends, drop-last and last work at the end as before; the trie never drops the leaves before the start, which the retention rule bounds.
- **Equality** compares two lists segment by segment when their starts are equal modulo 32, and otherwise looks the other list's items up one by one (O(log₃₂ n) each).

## Measurements

One Linux machine through `runPure`, warm calls, the same build path before and after (main `05688c3` and this change). Probe 24's harness is not committed, so the loop below isolates its cost: `drain(queue, acc)` dequeues with `ListSlice(queue, 1, ListLength(queue))` until `ListItemAt(queue, 0)` is `None`, starting from `ListRange(0, n)`; `rotate(queue, turns, acc)` dequeues the head and appends it again for `3n` turns, so the queue keeps its length while its start moves.

| Case | Before | After |
| --- | --- | --- |
| `drain`, 10,000 / 40,000 / 100,000 / 300,000 | 0.84 s / over the 5 s limit / over / over | 0.10 / 0.11 / 0.14 / 0.15 s |
| `rotate`, 10,000 / 40,000 / 100,000 / 300,000 | 3.18 s / over / over / over | 0.12 / 0.14 / 0.16 / 0.28 s |

The List, Map, Set and String tests of the Extended suite pass unchanged, including the amortised-append and linear-rebuild timings.

## Consequences

- `ListAppendTests`' compiled driver adds runs of up to 80 dequeues to its random operation sequences, and checks every result against a list of the same items whose start is elsewhere in its trie (equality both ways, hash, `IndexOf`, `LastIndexOf`, `FindIndex`, `MinBy`, `Any`, `All`, `Filter`, `Map`, `Reverse` and `SortBy`), on every list of up to 1,100 items and a sample of larger ones. A search that looks before the start, or an equality that assumes aligned starts, fails it.
- Memory: a sliced list keeps the nodes before its start alive until the retention rule copies it.

## Revisit conditions

- **A slice that keeps a large list alive in practice**, despite the halving rule: compact on a smaller fraction, or drop leaves before the start when the trie is copied anyway.
- **A deque.** Removing from both ends already shares; adding at the front still copies the list. A probe that pushes at the front at scale needs a front tail.
