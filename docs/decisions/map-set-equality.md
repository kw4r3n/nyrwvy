# Map and Set equality

- **Status:** Accepted
- **Date:** 2026-10-18
- **Supersedes:** the "A Map is not equatable" and "A Set is not equatable" points of the [Map](map.md) and [Set](set.md) decisions
- **Roadmap:** the G1 remaining boundary of the [general-purpose programming roadmap](../plans/general-purpose-programming-roadmap.md); a warranted operation under the [selection rule](../plans/post-m2-roadmap.md#selection-rule-from-here)

Map and Set were left out of `ValueEqual` because no consumer had needed them and the order question was open. Comparing two collections for equality is an ordinary need, and Python, the reference most decisions use, settles the order question: `dict` and `set` equality ignore insertion order.

## Decision

- **`Set<T>` is equatable.** Its items are already equatable. Two sets are equal when they hold the same items, whatever their insertion order (Python `set ==`).
- **`Map<K, V>` is equatable when `V` is.** Its keys are already equatable. Two maps are equal when they hold the same keys with equal values, whatever their insertion order (Python `dict ==`). A map with Float64 values, for example, is not equatable, and `ValueEqualTypeNotEquatable` names the path (`Map value`).
- Because they are equatable, sets and maps can now be Set items, Map keys and `ListIndexOf` items, as any equatable type can.
- Two equal values can still differ in visible order: `MapKeys`, `MapValues`, `SetToList` and the folds follow insertion order. Equality is about content, as in Python, where `{"a": 1, "b": 2} == {"b": 2, "a": 1}` although their `list()` differs.

## Reason

Order-sensitive equality would make Map and Set equality differ from every familiar library and from the mathematical meaning of a set; order-insensitive equality is the one that needs no explanation. The visible-order difference is the same one Python has.

## Consequences

- `EquatableTypes` visits a Set's item type and a Map's key and value types instead of rejecting them.
- The C# runtime's `NyMap<K, V>` and `NySet<T>` implement `IEquatable` with order-independent hash codes (a sum over entries), so `NyEquality.Equal`, nested Set items and Map keys use content equality.
- No node kind or persisted construct changes, so the snapshot schema version is unchanged. A reader from before this decision rejects a graph that relies on it at validation (`ValueEqualTypeNotEquatable`, `MapKeyTypeNotEquatable`, `SetItemTypeNotEquatable`), not silently.
- `MapSetEqualityTests` covers type checking, persistence, lowering and `runPure` results, including sets of sets and maps keyed by sets.
