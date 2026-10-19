# Set callbacks: SetFold, SetFilter and SetMap

- **Status:** Accepted
- **Date:** 2026-10-19
- **Roadmap:** [post-M2 owner choice of 2026-10-16](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-16-ordinary-builtins), order 68

`Set<T>` ([decision](set.md)) had the algebra of order 47 but no callback operation: a fold over a set was "left for later" with `SetToList` and `ListFold` covering it, and a filter or a comprehension took `SetToList`, the List callback and `SetFromList` back, two copies and three nodes for Python's `{x for x in s if p(x)}` or `{f(x) for x in s}`. `Map` meanwhile has `MapFold`, `MapFilter` and `MapMapValues`, and the List family has `ListFold`, `ListFilter` and `ListMap`. The owner chose on 2026-10-19 to add the Set counterparts under the [warranted-operation rule](../plans/post-m2-roadmap.md#selection-rule-from-here), with symmetry to the existing List and Map callback operations as the criterion.

## Decision

Three pure callback operations over a `Set<T>`, visiting the items in insertion order as `MapFold`, `MapFilter` and `MapMapValues` visit entries and as `SetToList` lists them:

- **`SetFold(set, seed, folder, context): S`** is `ListFold` over the items: a pure `(S, T) -> S` folder with exactly two parameters not bound by context, receiving `(accumulator, item)`, returning the seed type; the empty set gives the seed.
- **`SetFilter(set, predicate, context): Set<T>`** is `ListFilter` over the items: a pure `T -> Bool` predicate; the kept items keep their insertion order, so the result is the same `Set<T>` type with a subset of the items in the same relative order.
- **`SetMap(set, mapper, context): Set<U>`** is Python's set comprehension `{f(x) for x in s}`: a pure `T -> U` mapper called once per item in insertion order, each result inserted once, so a repeated result keeps the position of its first occurrence and the result can be smaller than the source. `U` must be equatable, the rule every Set item type follows; a mapper returning `Float64`, a function or another non-equatable type reports `SetItemTypeNotEquatable`.

All three never fail. Their wire recipes take the set in the field `set` (as the Map callbacks take `map`), the callback as `function` or `functionId`, and the optional `context`; the persisted field is `sourceSetNodeId`, and Nyason uses `set-fold`, `set-filter` and `set-map`.

**Not added: `SetAny` and `SetAll`.** `Map`, the sibling collection with the same keyed-iteration shape, has no `MapAny` or `MapAll`; the membership case is `SetContains`; and the remaining case is `ListAny` or `ListAll` over `SetToList`, one extra node and one copy. They are added when a consumer shows the cost.

## Reason

- **Symmetry.** An agent that knows `MapFilter` and `ListFilter` expects `SetFilter`; the `SetToList`/`SetFromList` detour is a cost to discover as well as to run. The three chosen are exactly the Map and List callback operations that have a Set reading; `ListSortBy`, `ListMinBy`, `ListFindIndex`, `ListZipWith` and `ListFlatMap` concern positions or several sources and have none.
- **Insertion order.** Visiting in insertion order is what `SetToList` already exposes and what `Map` does, so a fold or a filter gives the result the list detour gives, with no new order.
- **Equatable results for `SetMap`.** A `Set<U>` exists only for equatable `U`, so the restriction is the Set type's own, checked where `SetFromList` and `MapFromList` check theirs.

## Consequences

- The three nodes go through the semantic graph, validation (`SetInputTypeMismatch` for a fold over a non-set, `HigherOrderSourceTypeMismatch` for a filter or map over one, the `Callback*` and `PredicateResultTypeMismatch` codes of the List callbacks, `SetItemTypeNotEquatable`), both codecs, typed semantic IR, Execution IR, the C# backend (`NySet<T>.Fold`, the new `Filter`, which removes the rejected items and so keeps the others' insertion numbers, and `Map`, which inserts the mapped items in order), wire recipes and the recipe reference. The viewer labels them from their kind names.
- The validation of a one-parameter callback over a List is shared with Sets (`validateCallbackOver`), so the two families cannot drift.
- A snapshot that uses any of them is written at snapshot JSON 20 / Nyason `nayamnyam.snapshot.v16`, the version this batch of additions (orders 68 to 71) shares; see [snapshot schema versions](../specs/schema-evolution-policy.md#snapshot-schema-versions).
- `SetCallbackTests` covers them through every layer and under `runPure`, including a fold with context and a map whose results collide.

## Revisit conditions

- **`SetAny` or `SetAll`:** a consumer writes `ListAny(SetToList(s), p)` often enough that the copy matters, or Map gains the same pair.
- **Keyed operations:** a consumer needs `SetMap` to keep the source item beside its key (a `Map<U, T>`), which is `MapFromList` over `SetToList` today.
