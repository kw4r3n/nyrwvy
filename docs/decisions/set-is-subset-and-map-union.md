# SetIsSubset and MapUnion

- **Status:** Accepted
- **Date:** 2026-10-17
- **Roadmap:** [post-M2 owner choice of 2026-10-16](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-16-ordinary-builtins), order 60

The [Set decision](set.md) added union, intersection and difference, and the [Map decision](map.md) added insert and remove, but neither a subset test nor a merge of two maps. No probe has needed them yet. The owner chose on 2026-10-17 to add them as ordinary builtins ahead of probe evidence, with the semantics one normally expects: Python's, where it applies.

## Decision

- **`SetIsSubset(left, right): Bool`** is true when every item of `left` is in `right`, compared with `ValueEqual` (Python `left <= right`). Both operands are `Set<T>` of the same equatable `T`, with the same diagnostics as the other Set operations. An empty left set is a subset of any set, and a set is a subset of itself. When `left` has more items than `right` the answer is false without any lookup; otherwise each left item is looked up in `right` until the first miss, O(|left| log |right|). It never fails.
- **`MapUnion(left, right): Map<K, V>`** holds every entry of both maps; on a key present in both, the right value wins (Python `left | right`). Both operands are `Map<K, V>` with the same `K` and `V`, `K` equatable as for every Map. The order is exactly that of inserting right's entries one by one into `left` with `MapInsert`: left's keys keep their positions (a shared key takes the right value in place), then right's other keys follow in right's order. It never fails.
- `MapUnion` always inserts right into left, O(|right| log(|left| + |right|)). Inserting the smaller side into the larger one would put the larger side's keys first, so it is not done; the only shortcuts are returning the other map when one side is empty, which gives the same entries in the same order.

## Consequences

- Both go through the semantic graph, validation (`SetInputTypeMismatch`, `MapInputTypeMismatch`), snapshot and Nyason codecs (`setIsSubset` / `set-is-subset`, `mapUnion` / `map-union`, fields `left` and `right`), typed semantic IR, Execution IR, the C# backend (`NySet<T>.IsSubsetOf`, `NyMap<K, V>.Union`), the viewer, wire recipes and the recipe reference. No IR schema version changes. A snapshot that uses either is written at snapshot JSON 17 / Nyason `nayamnyam.snapshot.v13`; see [snapshot schema versions](../specs/schema-evolution-policy.md#snapshot-schema-versions).
- `SetIsSubsetAndMapUnionTests` covers both through every layer and under `runPure`: equal, proper, disjoint and empty subsets, and unions with shared keys, empty sides, `MapKeys` order and maps of a few thousand entries.
