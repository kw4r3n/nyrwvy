# List sorting and String ordering

- **Status:** Accepted
- **Amended:** 2026-10-27 by [String as a sequence of Unicode scalar values](string-scalar-values.md): Strings no longer hold unpaired surrogates, so the rules for them below no longer apply.
- **Date:** 2026-09-28
- **Roadmap:** [post-M2 queue from the 2026-09-28 probe 3](../plans/post-m2-roadmap.md#queue-from-the-2026-09-28-probe-3), order 12; `ListMinBy` is order 48 (owner choice of 2026-10-04); its counterpart `ListMaxBy` is order 69 ([decision](list-max-by.md))

This decision settles order 12 of the post-M2 queue: how a program sorts a list and orders Strings. [Probe 3](../verification/rrawrlet-slice-probes-summary.md#probe-3) (friction 2) sorted rrawrlet's `usage_stats` rows by `(role, -count)` and sorted values for medians with 7 functions and 72 recipe nodes: insertion sorts built from two `ListFilter`s per insert (`leX`, `gtX`, `insertSorted`, `rowNotAfter`, `rowAfter`, `insertRow`), and a fixed role-rank table (`roleRank`), because there was no String ordering. A role outside the table would sort differently from Python. The implemented surface is described by the [language subset](../reference/language-subset.md) and the [expression authoring specification](../specs/expression-authoring.md), which remain the authority once implemented.

## Decision

**`StringLessThan(left, right): Bool`.** True when `left` sorts strictly before `right`, comparing Unicode code points lexicographically; a proper prefix sorts first. A surrogate pair compares as its scalar value and an unpaired surrogate code unit as itself, as `StringLength` and `StringSlice` count them. The order is ordinal and case-sensitive, with no culture or normalization. It never fails. Recipe and wire name `stringLessThan`, Nyason `string-less-than`. Operand errors are `PureExpressionOperandTypeMismatch` in recipes and `StringIntrinsicTypeMismatch` in persisted graphs, as for `StringEqual`.

**`ListSortBy(list, lessThan, context): List<T>`.** `lessThan` is a pure callback referenced by `FunctionId`, like the `ListFilter` predicate. Its two parameters not bound by the optional context receive two items `(left, right)`, and it returns Bool: true when `left` must come before `right`. The result is defined by one algorithm, a top-down merge sort:

- a list of 0 or 1 items is returned as is;
- otherwise the first `length / 2` items (rounded down) and the rest are sorted separately;
- the halves are merged by taking the right head when `lessThan(rightHead, leftHead)` holds, and the left head otherwise.

For a strict weak order this is the stable sort: items the callback does not order keep their input order. For any other callback it is still a deterministic permutation of the input, the same on every backend. For example, a callback that is always true reverses the list. The callback runs at most `n log2 n` times, and the sort never fails. Recipe and wire name `listSortBy`, Nyason `list-sort-by`, persisted callback field `lessThanFunctionId`. Callback errors use the existing List callback codes: arity (two unbound parameters), parameter types (both the item type), a Bool result, and no required effects.

**`ListMinBy(list, lessThan, context): Option<T>` (order 48).** On 2026-10-04, after [probe 21](../verification/map-set-probes-summary.md#probe-21), the owner chose to add the smallest item of a list ahead of further evidence. It takes the same pure less-than callback as `ListSortBy`. The result is `None` for the empty list. Otherwise one left-to-right scan defines it: `best` starts as the first item, and an item replaces `best` only when `lessThan(item, best)` holds. Among equal items the first wins, and any callback gives a deterministic result; one that is always true gives the last item. The callback runs at most `n - 1` times, and the operation never fails. The largest item is the smallest under the swapped comparison. Recipe and wire name `listMinBy`, Nyason `list-min-by`, persisted callback field `lessThanFunctionId`, with the same callback codes as `ListSortBy`.

**No key-based sort and no three-way `StringCompare`.**

## Reason

**Code-point order.** `StringLength` and `StringSlice` already count code points, and Python compares `str` by code point. Ordinal UTF-16 comparison (`string.CompareOrdinal`) differs from both for surrogate pairs: it puts U+10000 and above (high surrogates, 0xD800 to 0xDBFF) before U+E000 to U+FFFF. Code-point order keeps the language consistent with itself and with the Python programs that probes port.

**`StringLessThan` over `StringCompare`.** The existing orderings are Bool: `Int64LessThan` and its siblings, and `Float64LessThan`. A three-way `StringCompare` returning -1, 0 or 1 would need an Int64 literal and an `Int64Equal` or `Int64LessThan` at each use to become a sort decision or a condition. `StringLessThan` is the decision itself, and with `StringEqual` it expresses every other comparison: `right < left` for greater, `not (right < left)` for less-or-equal. A composite key needs one `StringEqual` and one `StringLessThan`, which `StringCompare` would not save.

**A less-than callback over a key.** Python writes `rows.sort(key=lambda r: (r.role, -r.count))`. The key form relies on tuples compared lexicographically and on negating numbers for descending order. Nyrwvy has neither a tuple type nor a generic ordering over values, and adding them would mean an ordering over every key type: declared Products, Options, and Strings mixed with numbers. A comparator needs neither. It is one more pure callback of the kind `ListFilter`, `ListAny` and `ListFold` already take, including the context that passes runtime values without closures. The composite sort is written directly:

```text
rowBefore(a, b) = Conditional(StringEqual(a.role, b.role),
                              Int64GreaterThan(a.count, b.count),   // -count: descending
                              StringLessThan(a.role, b.role))
ListSortBy(rows, rowBefore)
```

The Bool less-than shape was chosen over an Int64 comparator because a stable merge needs only one question, "must the right item come first?", and because a Bool comparator composes from the existing Bool comparisons with no -1/0/1 convention to get wrong.

**A defined algorithm instead of a consistency obligation.** Nothing can check that a callback is a strict weak order. Library sorts that assume one (.NET's introsort, for example) may give an unspecified order or throw for an inconsistent comparer. Fixing the algorithm makes the result a function of the input and the callback alone, so `runPure`, the C# backend and any later backend agree, and a mistaken callback gives a wrong but reproducible order instead of a failure.

**Measured effect.** The probe 3 core was rewritten with `listSortBy` and `stringLessThan` (`probe3/usage12.py`, outside the repository). The sorting part went from 7 functions and 72 nodes to 2 functions and 23 nodes: `intLess` (3) for the median sort and `rowBefore` (20) for the rows. `median2` and `usageRows` lost one node each. The whole core went from 7 types, 23 functions and 270 nodes to 7 types, 18 functions and 219 nodes. It reran against Python on the 7 earlier cases and a new eighth one with roles outside the old rank table: `snapshot`, a real one-shot kind, and `audit`, a hypothetical one that sorts first. The rewritten core passed 8/8. The previous core, run with the same harness, failed the eighth case by sorting `audit` last.

## Consequences

- `StringLessThan`, `ListSortBy` and `ListMinBy` follow the usual path through the semantic graph, validation, snapshot and Nyason codecs, typed semantic IR, execution IR, the C# backend, the viewer (`string less than` with left and right operand dependencies; `List sort by` with a callback relation) and both recipe wire forms. The sort callback counts as a call target for purity, effect and call-graph analysis, as `ListFilter`'s does.
- The C# backend emits `NyText.LessThan`, which decodes code points itself instead of calling `string.CompareOrdinal`, `NyList<T>.SortBy`, the specified merge sort over a copy, and `NyList<T>.MinBy`, the specified scan. See the [C# backend](../specs/csharp-backend.md#string-ordering).
- `runPure` cannot carry an unpaired surrogate in a String argument, because its JSON transport replaces it. The code-point rule for unpaired surrogates is therefore defined and implemented, but not exercised end to end.
- A sort by a derived key recomputes the key in each comparison. That costs `O(n log n)` key computations instead of `n`.

## Revisit conditions

- **Key-based sort:** a consumer's key is costly enough that recomputing it per comparison matters, or comparators duplicate the same key extraction across several sorts.
- **Locale-aware or case-insensitive ordering:** a consumer must order text for people rather than match Python's `str` order.
- **Other orderings:** a consumer needs an ordering beyond `StringLessThan` and the Int64 and Float64 comparisons, for example over Options or declared types.
