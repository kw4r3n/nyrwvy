# ListLastIndexOf and a smaller-side SetDifference

- **Status:** Accepted
- **Date:** 2026-10-05
- **Roadmap:** [post-M2 queue from the 2026-10-05 probe 23 owner choice](../plans/post-m2-roadmap.md#queue-from-the-2026-10-05-probe-23-owner-choice), order 53

After [order 52](persistent-vector-list.md), [probe 23](../verification/map-set-probe23-20261005.md)'s Tarjan components still exceeded the 5 s run limit on a comb of 20,000 nodes. Each leaf there closes a one-name component at the top of the component stack, and two steps of closing it were linear in the stack's depth: `ListIndexOf(scc, top)` scans from the front, and `SetDifference(onStack, component)` folds over the whole on-stack set. The owner queued both fixes on 2026-10-05, as the order 52 decision's revisit condition proposed.

## Decision

- **`ListLastIndexOf(list, item): Option<Int64>`**: the zero-based index of the last item equal to `item`, compared as `ValueEqual` compares, or `None`. It has `ListIndexOf`'s rules (the item has the list's item type, which must be equatable) and scans from the end, so an item near the end of a stack is found in time proportional to its distance from the end. A `ListFindLastIndex` with a callback is not added; no probe has asked for one.
- **`SetDifference(left, right)`** removes the right set's items from the left one by one when the right set is smaller, and otherwise tests every left item as before. Removal keeps the other items' insertion numbers, so the result, its order and a later insert's position are the same either way. No language change.

## Measurements

Probe 23's harness on the order 52 runtime, with `tarjanLoop` changed only from `listIndexOf` to `listLastIndexOf` (still 208 nodes):

| Case | Order 52 | Order 53 |
| --- | --- | --- |
| Components, comb of 4,000 / 10,000 / 20,000 / 40,000 | 0.90 / 2.70 s / over the 5 s limit / over | 0.39 / 0.65 / 0.98 / 1.73 s |
| Components, chain of 100,000, open / closed | 1.76 / 2.71 s | 1.63 / 2.48 s |

Probe 23's 1,800 cases still match their reference, `components` among them.

## Consequences

- `ListLastIndexOf` goes through the semantic graph, validation (`ListLastIndexOfListTypeMismatch`, `ListLastIndexOfItemTypeMismatch`, `ListLastIndexOfItemTypeNotEquatable`), snapshot and Nyason codecs (`listLastIndexOf`, `list-last-index-of`), typed semantic IR, Execution IR, the C# backend (`NyList<T>.LastIndexOf`), the viewer, wire recipes and the recipe reference. No schema version changes.
- `ListIndexTests` covers it through every layer and under `runPure`, including a 70-item list whose matches lie in different leaves. `SetTests` adds a difference with a smaller right set followed by an insert.
- The viewer labels of `ListIndexOf` and `ListFindIndex`, which read "Set contains" and "List any" since order 51, now read "List index of" and "List find index".
