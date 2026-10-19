# ListMaxBy

- **Status:** Accepted
- **Date:** 2026-10-19
- **Roadmap:** [post-M2 owner choice of 2026-10-16](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-16-ordinary-builtins), order 69

`ListMinBy` ([decision](list-sort-and-string-ordering.md), order 48) gives the first smallest item under a less-than callback, and its reference said the largest is the smallest under the swapped comparison. A comparator in Nyrwvy is a declared function, so the swap is a second declaration per ordering, and Python has both `min` and `max`. The owner chose on 2026-10-19 to add the counterpart under the [warranted-operation rule](../plans/post-m2-roadmap.md#selection-rule-from-here).

## Decision

**`ListMaxBy(list, lessThan, context): Option<T>`** takes the same pure `(T, T) -> Bool` less-than callback as `ListSortBy` and `ListMinBy` and returns the largest item: `None` for the empty list; otherwise a left-to-right scan that starts from the first item and replaces the best so far with an item only when `lessThan(best, item)` holds.

**Among equal largest items the first wins,** as Python's `max(items, key=...)` returns the first maximal item, and as `ListMinBy` returns the first minimal one. The scan is the same shape as `ListMinBy`'s with the comparison arguments swapped, so any callback, consistent or not, gives a deterministic result: an always-true callback keeps replacing and the last item wins; an always-false one never replaces and the first item wins. It calls the callback at most `n - 1` times and never fails.

Wire `listMaxBy` with the `listMinBy` fields (`source`, `function` or `functionId`, `context`), Nyason `list-max-by`, persisted callback field `lessThanFunctionId`.

## Reason

- **One comparator per ordering.** With `ListMinBy` alone, "largest by length" needs a `longerThan` function beside `shorterThan`, or a `ListSortBy` and `ListLast`, which sorts the whole list to read one item.
- **Python parity for ties.** `max` and `min` both return the first extreme item; mirroring `ListMinBy` keeps the one rule and makes `ListMaxBy(xs, lt)` equal to `ListMinBy(xs, flip lt)` item for item.

## Consequences

- It follows `ListMinBy` through every layer: validation (`validateLessThanCallback`, the same `CallbackArityMismatch`, `CallbackParameterTypeMismatch`, `PredicateResultTypeMismatch` and `CallbackEffectsNotAllowed` codes), both codecs, typed semantic IR, Execution IR, the C# backend (`NyList<T>.MaxBy`, the specified scan), wire recipes and the recipe reference. A snapshot that uses it is written at snapshot JSON 20 / Nyason `nayamnyam.snapshot.v16`.
- `ListMaxByTests` covers it through every layer and under `runPure`, including ties, the constant callbacks and a context-bound target.
