# Map (design note)

**Status:** Decided 2026-10-03: every recommendation accepted (keys of any equatable type, insertion order, the ten operations, Map not equatable, Set second). The owner also agreed that the language has no general tuple type (see the [decision](../decisions/map.md)). The first eight operations are order 44; `MapFold` and `MapFromList` are order 45; Set is order 46; set algebra and `MapFilter` are order 47; `MapGetOrDefault` and `MapMapValues` are order 48. The analysis below is kept as written. Following the owner's stance of 2026-10-03, each choice prefers what one would ordinarily expect of a map over matching Python exactly.

## Context

Nyrwvy has no keyed collection. Programs look keys up with a fold over a `List`, which is linear per lookup and quadratic over a loop:

| Probe | What it cost |
| --- | --- |
| [3](../verification/rrawrlet-slice-probe3-20260928.md) | Replace-or-append in 3 places; settled for that size by `ValueEqual` and a generic `upsert` (27 nodes) |
| [7](../verification/rrawrlet-slice-probe7-20260929.md) | Five dicts and sets keyed by message ID or `(messageID, partID)`. Map emulation 17 nodes in 150; linear lookups were about 3.4 s of 3.7 s at 19,200 events |
| [13](../verification/rrawrlet-slice-probe13-20261001.md) | `dict` with its duplicate-key rule, and `set`: 10 functions and 159 nodes for a sorted index with binary search. The naive fold hit the 5 s limit at about 40,000 refs |
| [14](../verification/rrawrlet-slice-probe14-20261001.md) | Constant sets written as literal lists, about 90 nodes, 36 comparisons per tree node |
| [16](../verification/rrawrlet-slice-probe16-20261001.md) | Comparing two file trees by name is quadratic: over the 5 s limit at 16,000 files. The Map-free rewrite (sort both, then merge-join) took 7 requests and 327 nodes, and is an algorithm the Python does not contain |

Earlier decisions deferred Map for two reasons. There was no key-equality contract, and [`ValueEqual`](../decisions/value-equality-and-map.md) has since supplied one. The evidence was also thin. That evidence now stands at five probes. A map is something a general-purpose language is expected to have.

## Invariants to keep

- **Determinism.** The same program and input give the same output on every run and backend. Nothing observable may depend on hash values or memory addresses.
- **Values are immutable.** Like `List`, a Map is a value; "inserting" returns a new map and leaves the old one unchanged.
- **Total operations.** No runtime operation fails on ordinary input; a missing key is `None`, not an exception.
- **One key equality.** Keys compare with `ValueEqual`, so the language has one notion of "same value".

## Questions

### 1. Which types can be keys?

- **A. String and Int64 only.** This was the smallest candidate in the earlier decisions. It cannot hold probe 3's 6-field group key or probe 7's `(messageID, partID)` without encoding them into a String.
- **B. Any equatable type (recommended).** This is the set `ValueEqual` accepts: String, Int64, Bool, Unit, `List`/`Option`/`Result` of equatable types, and declared Products and Unions of equatable fields. Composite keys work as they are. Float64 stays excluded because of NaN, as in `ValueEqual`. The C# backend already gives every equatable type structural `Equals` and `GetHashCode`.

### 2. In what order are entries visited?

Iteration order is observable through `MapKeys`, `MapValues` and `MapFold`, so it must be defined.

- **A. Insertion order (recommended).** Entries are visited in the order their keys were first inserted. Replacing a key's value keeps its position; removing and re-inserting moves it to the end. Python's `dict` and JavaScript's `Map` both behave this way, so it is what most readers expect, and probe ports keep their output order without extra work.
- **B. Sorted by key.** This needs a total order over every key type. Nyrwvy defines ordering only for String (by code point) and Int64, so B would either narrow the keys to A's set or define a new order for Products and Unions.
- **C. Unspecified.** Rejected, because it breaks determinism.

### 3. Which operations?

A first slice with the operations every map API has (`K` key, `V` value):

| Operation | Type | Meaning |
| --- | --- | --- |
| `MapEmpty(K, V)` | `Map<K, V>` | The empty map |
| `MapInsert(map, key, value)` | `Map<K, V>` | `key` set to `value`. A new key goes last; an existing key keeps its position |
| `MapGet(map, key)` | `Option<V>` | The value, or `None` |
| `MapContainsKey(map, key)` | `Bool` | Whether `key` is present |
| `MapRemove(map, key)` | `Map<K, V>` | Without `key`; unchanged if absent |
| `MapSize(map)` | `Int64` | The number of entries |
| `MapKeys(map)` | `List<K>` | Keys in iteration order |
| `MapValues(map)` | `List<V>` | Values in iteration order |
| `MapFold(map, seed, folder)` | `S` | `folder(state, key, value)` over the entries in order, like `ListFold` |
| `MapFromList(items, keyOf)` | `Map<K, T>` | Items indexed by `keyOf(item)`. On a duplicate key the later item wins and the first position is kept, as with repeated `MapInsert` |

Nyrwvy has no tuple type, so the slice takes no list of pairs. `MapFromList` with a key callback covers "index these records by id", which is the probes' common construction. `MapFold` covers walking the entries.

Left for later, each until a consumer needs it: update-with-function (`MapGet` plus `MapInsert` does it), merging two maps, and a list of entries (it needs a pair type). Filtering was later added as `MapFilter` (order 47), and a get with a default and mapping the values as `MapGetOrDefault` and `MapMapValues` (order 48).

### 4. Is a Map itself equatable, and can it be a key?

- **A. Not equatable for now (recommended).** `ValueEqual` and keys reject `Map`. Two maps with the same entries in a different order raise a hard question. Python says they are equal. But they are then "equal" while `MapKeys` tells them apart, which breaks the rule that equal values behave the same. No probe compared maps.
- **B. Equal when they hold the same entries in the same order.** This is consistent with observable behaviour, but it surprises Python readers.
- **C. Equal when they hold the same entries in any order.** This matches Python but breaks substitutability, as described under A.

### 5. Set

Probes 13 and 14 needed sets: membership in a constant set, and de-duplication. A set is also something a language is expected to have.

- **A. Set in a second slice (recommended).** `Set<K>` with `SetFromList`, `SetInsert`, `SetContains`, `SetRemove`, `SetSize` and `SetToList`, in insertion order, on the same runtime structure. Until then, `Map<K, Unit>` serves.
- **B. Set in the same slice.** This means one larger change.
- **C. No Set.** `Map<K, Unit>` is the idiom. It is wordier, and it is not what a reader expects.

## Runtime and backend

- **Complexity.** Insert, get, remove and contains are O(log n); size is O(1); keys, values and fold are O(n). There is no worst case quadratic in n.
- **C#.** The map is persistent, so old versions stay valid after an insert. It uses `System.Collections.Immutable` (part of the shared framework):
  - an `ImmutableDictionary<K, (long Order, V Value)>` for lookup, under the structural `EqualityComparer<K>.Default` that `ValueEqual` already relies on;
  - an `ImmutableSortedDictionary<long, K>` from insertion number to key, for the order;
  - a counter for the next insertion number.

  Hash codes affect only speed, never the visible order.
- **Types.** `Map` is a new built-in generic of arity 2 (`BuiltinGenericType.Map`, `TypeRef.BuiltinGeneric(Map, [K; V])`), validated so that `K` is equatable.
- **Run values.** `runPure` takes and returns `{"kind":"map","keyType":...,"valueType":...,"entries":[{"key":...,"value":...}]}` in iteration order; a repeated key in an input is rejected.

## Recommended first slice

Keys of any equatable type (1B), insertion order (2A), the ten operations of question 3, Map not equatable (4A), and Set in a second slice (5A).

## Cost

This is the largest addition since `Task` types:
- a new built-in generic, carried through type checking, `EquatableTypes` (to reject Map), the codecs for types and values, typed and execution IR, the C# backend's runtime type, the viewer, run values and both recipe wire forms;
- ten operations, two of them with callbacks (`MapFold`, `MapFromList`), which reuse the callback handling of `ListFold`.

## Evidence it worked

- Re-author probe 16's tree comparison with `MapFromList` and `MapGet`. Confirm the same 518 cases match and that the 16,000-file comparison finishes within the run limit, in fewer nodes than the 327-node merge join.
- Re-author probe 13's index (159 nodes) the same way.
- Unit tests for insertion order:
  - replacing a value keeps the key's position;
  - removing and re-inserting moves the key to the end;
  - `MapFromList` keeps the first position and the last value;
  - keys that are Products and Unions work;
  - a Float64 or Map key is rejected.

## Choices for the owner

1. Key types: 1A or 1B (recommended).
2. Order: 2A (recommended) or 2B.
3. Operations: the ten in question 3, or a different set.
4. Map equality: 4A (recommended), 4B or 4C.
5. Set: 5A (recommended), 5B or 5C.
