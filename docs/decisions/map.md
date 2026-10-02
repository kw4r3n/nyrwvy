# Map

- **Status:** Accepted
- **Date:** 2026-10-03
- **Roadmap:** [post-M2 owner choice of 2026-10-03](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-03), orders 44 and 45
- **Design note:** [Map](../plans/map.md)
- **Supersedes:** the "Map stays deferred" parts of [value equality, and Map revisited again](value-equality-and-map.md)

Five probes (3, 7, 13, 14 and 16) paid for the missing keyed collection. Probe 16's tree comparison went over the run limit. The [design note](../plans/map.md) put five questions to the owner, who accepted every recommendation on 2026-10-03.

## Decision

- **Keys of any equatable type.** `K` is any type `ValueEqual` accepts, so composite keys (Products, Unions, Lists) work as they are. Float64, functions, tasks, capabilities and maps are not keys (`MapKeyTypeNotEquatable`).
- **Insertion order.** Entries are visited in the order their keys were first inserted. Replacing a value keeps the key's position; removing and re-inserting puts it last. The order never depends on hash values.
- **Immutable values.** `MapInsert` and `MapRemove` return new maps. The runtime structure is persistent and O(log n).
- **Operations.**
  - Order 44 added `MapEmpty`, `MapInsert`, `MapGet`, `MapContainsKey`, `MapRemove`, `MapSize`, `MapKeys` and `MapValues`.
  - Order 45 added `MapFold` (folder `(S, K, V) -> S`) and `MapFromList` (key callback `T -> K`, later items win, first position kept). They share the callback handling of `ListFold` and `ListMap`.
- **A Map is not equatable.** `ValueEqual` rejects it, and so do keys. Order-insensitive equality would make two "equal" maps differ in `MapKeys`; order-sensitive equality would surprise readers used to Python. No consumer has compared maps.
- **Set comes second.** `Set<K>` follows in a later slice on the same runtime structure. Until then, `Map<K, Unit>` serves.
- **No general tuple type.** The owner agreed that positional tuples would lose the named, stable-ID fields that make changes traceable; declared Products serve. If an API needs pairs (map entries, zip), a single built-in `Pair<A, B>` with named fields `first` and `second` is the candidate, not tuples.

## Consequences

- `BuiltinGenericType.Map` of arity 2 runs through validation, the type codecs, typed and execution IR, the C# backend (`NyMap<K, V>`), the viewer, `runPure` values and both recipe wire forms.
- New diagnostics:
  - `MapKeyTypeNotEquatable`, for any `Map` type and for `MapEmpty`;
  - in persisted graphs, `MapInputTypeMismatch`, `MapKeyTypeMismatch` and `MapValueTypeMismatch`.
- `EquatableTypes` treats `Map` as not equatable.

## Revisit conditions

- A consumer compares maps: decide between order-sensitive and order-insensitive equality.
- A consumer needs entries as pairs, zipping, or merging: consider `Pair<A, B>` and the operation together.
