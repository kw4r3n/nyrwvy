# List positional updates

- **Status:** Accepted
- **Date:** 2026-10-17
- **Roadmap:** [post-M2 owner choice of 2026-10-16](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-16-ordinary-builtins), order 62

A list could be read at a position (`ListItemAt`) and cut (`ListSlice`), but replacing, inserting or removing one item took a slice, an append and a concat, or a fold. The owner chose to add the three ordinary positional updates ahead of probe evidence, with semantics following normal expectation: Python's list operations where they apply, except where stated below.

## Decision

- **`ListSetAt(list, index, item): List<T>`**: the list with the item at zero-based Int64 `index` replaced by `item`. An index outside `[0, length)`, negative included, returns the list unchanged.
- **`ListInsertAt(list, index, item): List<T>`**: the list with `item` inserted before position `index`. The index clamps to `[0, length]`: 0 inserts first, `length` or more appends, and a negative index inserts first; it is not counted from the end, as `StringSlice` and `ListSlice` bounds are not.
- **`ListRemoveAt(list, index): List<T>`**: the list without the item at `index`. An index outside `[0, length)` returns the list unchanged.

`item` must have the list's item type, of any type: unlike `ListIndexOf`, nothing is compared, so there is no equatable constraint. All three are pure, leave their input unchanged and never fail.

Costs on the [persistent vector](persistent-vector-list.md): `ListSetAt` copies the one leaf (or the tail) holding the index and the trie nodes on its path, O(log₃₂ n). `ListInsertAt` and `ListRemoveAt` share the items before the index as a slice and append the rest, O(log₃₂ n + length − index): appending, removing the last item and removing the first item (a shared slice) are cheap, and an update near the front copies the rest of the list.

## Reason

- Out-of-range `ListSetAt` and `ListRemoveAt` differ from Python, where `xs[i] = v`, `del xs[i]` and `xs.pop(i)` raise `IndexError`. They follow the never-fail convention of the other list operations: a pure expression has no exception to raise, and a `Result` or `Option` would make every update site unwrap a case that a bounds check before the call already rules out.
- `ListItemAt` reports an out-of-range index as `None` because it has no item to return. These operations have a natural total result, the list itself, so they return it rather than an `Option<List<T>>`; a caller that must know can compare the index with `ListLength` first.
- `ListInsertAt` matches Python's `list.insert` clamping for non-negative indexes. For negative indexes Python counts from the end (`insert(-1, x)` inserts before the last item); here a negative index inserts first, consistent with `ListSlice`, `StringSlice` and `ListItemAt`, none of which count from the end.

## Consequences

- The three node kinds go through the semantic graph, validation (`ListSetAtListTypeMismatch`, `ListSetAtIndexTypeMismatch`, `ListSetAtItemTypeMismatch`, the same three for `ListInsertAt`, and `ListRemoveAtListTypeMismatch`, `ListRemoveAtIndexTypeMismatch`), snapshot and Nyason codecs (`listSetAt`, `listInsertAt`, `listRemoveAt`; `list-set-at`, `list-insert-at`, `list-remove-at`), typed semantic IR, Execution IR, the C# backend (`NyList<T>.SetAt`, `InsertAt`, `RemoveAt`), the viewer, wire recipes and the recipe reference. Recipes report a wrong operand as `PureExpressionOperandTypeMismatch` from the signature table.
- A snapshot that uses any of them is written at snapshot JSON 17 / Nyason `nayamnyam.snapshot.v13` (feature `ListPositionalUpdate`); see [snapshot schema versions](../specs/schema-evolution-policy.md#snapshot-schema-versions).
- `ListPositionalUpdateTests` covers them through every layer, the persisted-graph diagnostics, indexes −1, 0, last, length and length + 5 on empty, short and 70-item lists under `runPure` (each run also returns the original list to show it unchanged, and two updates of one list to show they do not share writes), and a `ListFold` that applies `ListSetAt` 50,000 times.

## Revisit conditions

- A probe or program needs Python's negative-index counting or an error on an out-of-range index; that would be a separate operation, not a change to these.
- Inserting or removing near the front of a long list shows up as a cost in a measured program; a relaxed-radix tree or a front buffer would then be the candidate.
