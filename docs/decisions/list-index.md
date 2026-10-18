# ListIndexOf and ListFindIndex

- **Status:** Accepted
- **Date:** 2026-10-05
- **Roadmap:** [post-M2 queue from the 2026-10-05 probe 22 owner choice](../plans/post-m2-roadmap.md#queue-from-the-2026-10-05-probe-22-owner-choice), order 51

[Probe 22](../verification/map-set-probes-summary.md#probe-22) took a depth-first search's stack from a node to the top with a 16-node `ListFold` that started collecting at the first frame naming the node: there was no way to ask where an item is. The owner queued both usual forms on 2026-10-05.

## Decision

- **`ListIndexOf(list, item): Option<Int64>`**: the zero-based index of the first item equal to `item`, compared as `ValueEqual` compares, or `None`. The item has the list's item type, which must be equatable (`ListIndexOfItemTypeNotEquatable` otherwise, with the same explanation as `ValueEqual`).
- **`ListFindIndex(source, predicate, context): Option<Int64>`**: the index of the first item for which a pure `T -> Bool` callback is true, or `None`. The callback and its context are those of `ListAny`.

Both scan left to right and stop at the first match, and neither fails. They return `Option<Int64>` like `ListItemAt` and `StringIndexOf`, rather than −1, so an absent item cannot be used as an index by mistake.

## Reason

Both are what one ordinarily expects of a list (Python `list.index` without its exception, JavaScript `indexOf` and `findIndex`). `ListIndexOf` covers the common equality case without a callback; `ListFindIndex` covers a field match and the types `ValueEqual` excludes, such as Float64.

With them, the cycle path of probe 22's search is `ListSlice(stack, i, ListLength(stack))` after `ListIndexOf(stack, w)`: a 12-node helper instead of the 14-node fold callback, and it reads as what it does.

## Consequences

- Both go through the semantic graph, validation, snapshot and Nyason codecs, typed semantic IR, Execution IR, the C# backend (`NyList<T>.IndexOf` and `FindIndex`), the viewer, wire recipes and the recipe reference. No IR schema version changes. A snapshot that uses them is written at snapshot JSON 13 / Nyason `nayamnyam.snapshot.v9` since 2026-10-05; see [snapshot schema versions](../specs/schema-evolution-policy.md#snapshot-schema-versions).
- `ListIndexTests` covers rejection, persistence, Nyason, lowering and emission, and under `runPure` the first match, a later duplicate, an absent item and the empty list.
