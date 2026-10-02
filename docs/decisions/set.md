# Set

- **Status:** Accepted
- **Date:** 2026-10-03
- **Roadmap:** [post-M2 owner choice of 2026-10-03](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-03), order 46
- **Design note:** [Map](../plans/map.md), question 5 (Set in a second slice)

The [Map decision](map.md) put Set in a second slice on the same runtime structure. Probes 13 and 14 needed sets: membership in a constant set, and removing repeats. The owner asked for it on 2026-10-03.

## Decision

- **`Set<T>`** holds equatable items, which compare with `ValueEqual`, the same rule as Map keys. It is visited in insertion order and is an immutable, persistent value. A `Set` is not equatable.
- **Operations:**
  - `SetEmpty(itemType)`, `SetFromList(items)`, `SetInsert(set, item)`, `SetContains(set, item)`, `SetRemove(set, item)`, `SetSize(set)` and `SetToList(set)`.
  - `SetFromList` keeps first occurrences in order.
  - Inserting an item that is already present changes nothing.
- **Left for later, until a consumer needs them:** union, intersection and difference, and a fold over a set (`SetToList` with `ListFold` covers it).

## Consequences

- `BuiltinGenericType.Set` of arity 1 runs through every layer like `Map`. The C# backend emits `NySet<T>`.
- New diagnostics:
  - `SetItemTypeNotEquatable`;
  - in persisted graphs, `SetInputTypeMismatch` and `SetItemTypeMismatch`.

## Revisit conditions

- A consumer combines sets: add union, intersection and difference together, keeping insertion order defined (left operand's order first).
