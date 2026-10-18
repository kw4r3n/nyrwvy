# Set

- **Status:** Accepted
- **Date:** 2026-10-03
- **Roadmap:** [post-M2 owner choice of 2026-10-03](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-03), orders 46 and 47
- **Design note:** [Map](../../docs-archive/2026-10-decided-plans/map.md), question 5 (Set in a second slice)

The [Map decision](map.md) put Set in a second slice on the same runtime structure. Probes 13 and 14 needed sets: membership in a constant set, and removing repeats. The owner asked for it on 2026-10-03.

## Decision

- **`Set<T>`** holds equatable items, which compare with `ValueEqual`, the same rule as Map keys. It is visited in insertion order and is an immutable, persistent value. A `Set` is not equatable (*superseded on 2026-10-18 by [Map and Set equality](map-set-equality.md)*).
- **Operations:**
  - `SetEmpty(itemType)`, `SetFromList(items)`, `SetInsert(set, item)`, `SetContains(set, item)`, `SetRemove(set, item)`, `SetSize(set)` and `SetToList(set)`.
  - `SetFromList` keeps first occurrences in order.
  - Inserting an item that is already present changes nothing.
- **Set algebra (order 47).** On 2026-10-04 the owner chose to add the set operations one ordinarily expects without waiting for a consumer:
  - `SetUnion(left, right)`: the left items in their order, then the right items not in left, in right's order.
  - `SetIntersection(left, right)`: the left items that right contains, in left's order.
  - `SetDifference(left, right)`: the left items that right does not contain, in left's order.
  - Both operands are sets of the same item type. None of them fails.
- **Left for later, until a consumer needs it:** a fold over a set (`SetToList` with `ListFold` covers it).

## Consequences

- `BuiltinGenericType.Set` of arity 1 runs through every layer like `Map`. The C# backend emits `NySet<T>`.
- New diagnostics:
  - `SetItemTypeNotEquatable`;
  - in persisted graphs, `SetInputTypeMismatch` and `SetItemTypeMismatch`.
- As for `Map` keys, a type parameter is never equatable, so a `Set` item type is always concrete and a generic function cannot take or return `Set<T>`; `SetItemTypeNotEquatable` says so for a type parameter.

## Revisit conditions

- A consumer folds over large sets often enough that building the list first costs: add `SetFold`.
