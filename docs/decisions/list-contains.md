# ListContains

- **Status:** Accepted
- **Date:** 2026-10-18
- **Roadmap:** [post-M2 owner choice of 2026-10-16](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-16-ordinary-builtins), order 66

Python's `item in list` had no direct form. `ListIndexOf` ([decision](list-index.md)) answers it, but as an `Option<Int64>`, so a membership test took a match (or `OptionIsSome` since [order 65](option-result-accessors.md)) over an index nobody wanted, and `ListAny` needs a named predicate callback. `SetContains` and `MapContainsKey` exist for their collections, and [retiring the duplicate kinds](retired-authoring-kinds.md) kept `StringContains` for exactly this reason: `StringIndexOf` gives an `Option<Int64>`, so testing it takes a match. The owner chose on 2026-10-18 to add the List form under the [warranted-operation rule](../plans/post-m2-roadmap.md#selection-rule-from-here).

## Decision

**`ListContains(list, item): Bool`** is true when some item of the `List<T>` equals `item` under `ValueEqual`, as Python `item in list`. `T` must be equatable, the same restriction as `ListIndexOf`, `SetContains` and the Set item type: a `List<Float64>` uses `ListAny` with a callback. It scans left to right and stops at the first equal item, so it is linear in the position of the first match and never fails. It is a fixed-signature operation checked from the `OperationSignatures` table with the equatable constraint `ListContainsItemTypeNotEquatable`; wire `listContains` with the fields `list` and `item`, Nyason `list-contains`.

Repeated membership tests against the same list are still better served by `SetFromList` once and `SetContains`, which the recipe reference says.

## Consequences

- It goes through the semantic graph, validation (`ListContainsListTypeMismatch`, `ListContainsItemTypeMismatch`, `ListContainsItemTypeNotEquatable`), both codecs, typed semantic IR, Execution IR, the C# backend (`NyList<T>.Contains`, which is `IndexOf(item).HasValue`), the viewer, wire recipes and the recipe reference. A snapshot that uses it is written at snapshot JSON 19 / Nyason `nayamnyam.snapshot.v15`; see [snapshot schema versions](../specs/schema-evolution-policy.md#snapshot-schema-versions).
- `ListContainsTests` covers it through every layer and under `runPure`, including a list of Sets (equatable by content) and a list of a few thousand items.
