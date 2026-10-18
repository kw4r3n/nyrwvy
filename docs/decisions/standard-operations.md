# Standard operations with no choice to make

- **Status:** Accepted
- **Date:** 2026-10-01
- **Roadmap:** [post-M2 queue from the 2026-10-01 standard-operations review](../plans/post-m2-roadmap.md#queue-from-the-2026-10-01-standard-operations-review), orders 35 and 36
- **Superseded in part (2026-10-18):** the exception below was replaced by the rule that [language operations must be warranted](../plans/post-m2-roadmap.md#selection-rule-from-here). The operations it added stay as decided.

The [selection rule](../plans/post-m2-roadmap.md#selection-rule-from-here) adds language work only for an observed failure, blocker or cost. Its reason is that defining an operation before anyone uses it means guessing its meaning. The [Float64 conversion decision](float64-conversion-and-run-values.md) says this directly: without a consumer, the rounding choice "would be a guess". A review of the builtin surface against ordinary language libraries found operations that programs commonly need but that no probe had reported as a cost, because each hand-written version was small. Some of them have no meaning to guess. The implemented surface is described by the [language subset](../reference/language-subset.md), the [expression authoring specification](../specs/expression-authoring.md) and the [C# backend](../specs/csharp-backend.md), which remain the authority.

## Decision

**An exception to the selection rule.** An operation that ordinary programs commonly need may be added without an observed cost when an external standard (IEEE 754) or an obvious definition fixes its meaning completely, so there is nothing for a consumer to decide, and its implementation is small. Anything with a choice to make still waits for an observed consumer or an owner choice.

**Added under it:**

- Order 35: `ListReverse(list)`, `List<T> -> List<T>`, Python `list(reversed(list))`. Linear time, never fails. Wire `listReverse` (field `value`), Nyason `list-reverse`.
- Order 36: `Float64Sqrt`, `Float64Abs`, `Float64Negate`, `Float64Floor`, `Float64Ceiling` and `Float64Truncate`, all `Float64 -> Float64`, never fail. Wire `float64Sqrt` and so on (field `value`), Nyason `float64-sqrt` and so on.

The Float64 operations are the IEEE 754 operations `squareRoot`, `abs`, `negate`, `roundToIntegralTowardNegative`, `roundToIntegralTowardPositive` and `roundToIntegralTowardZero`. Each has exactly one result for every input, including NaN, the infinities and the signed zeros, so the C# backend and any later backend agree bit for bit. Square root is correctly rounded in IEEE 754, unlike `log` or `exp`, whose results differ between math libraries ([#284](https://github.com/kw4r3n/nyrwvy/issues/284)).

**Where Python differs.** `math.sqrt` of a negative value raises `ValueError`; `Float64Sqrt` gives NaN, as IEEE 754 does, which keeps every runtime operation total. `math.floor`, `math.ceil` and `math.trunc` return an `int`; the Float64 forms return the same integral value as a Float64, so a zero result keeps the input's sign (`math.ceil(-0.5)` is `0`, `Float64Ceiling(-0.5)` is `-0.0`) and NaN and the infinities pass through instead of raising. The Int64 forms remain `Float64RoundToInt64` and `Float64FloorToInt64`.

**Not added, because each has a choice to make:**

| Candidate | Choice | Observed |
| --- | --- | --- |
| `ListRange(start, end)` | What a huge range does: return `Option`, clamp, or fail like a stack overflow, given that runtime operations never fail | Probes 5 (`repeat` by self-recursion), 7 (`"x" * n` as a generator) and 12 (a constant list sliced only for its length) |
| `StringRepeat` and padding | The same size limit | Probes 5 and 12 |
| Hexadecimal formatting and parsing | Negative values (Python gives `-ff`), digit count, case | Probes 9, 10, 11 (JSON `\u` escapes), 12, 16 (`hexdigest`), 17 and 18 |
| `StringToUpper` | Python's `upper()` changes length (`ß` to `SS`); a one-to-one mapping would not | None |
| Int64 `Abs`, `Min`, `Max` | `Abs(Int64.MinValue)` | Probes 15 and 20, 3 to 5 nodes each |

`ListRange` and hexadecimal text already have observed cost, so they need only the owner's choice. The owner made those choices the same day; see [standard operations with an owner choice](standard-operation-choices.md), which also added `StringRepeat` and Int64 `Min`, `Max` and `WrappingAbs`.

## Reason

Waiting for a probe protects against a wrong guess. When there is nothing to guess, waiting protects against nothing, and in the meantime programs carry hand-written versions that are larger and sometimes worse: probe 16's reversal by `ListConcat([item], acc)` copies the accumulator on every step and is O(n²), and the only linear alternative, `ListSortBy` with a callback that always returns true, works but does not say what it does. A square root written by hand would not be correctly rounded.

Each operation also has costs: about 30 places in the semantic graph, codecs, IR, backend, viewer and wire forms, and one more entry for agents to find in the recipe reference. These are small for seven operations. The rule limits the exception to operations with no choice, so it does not turn into adding every conventional library function.

## Revisit when

- An operation in the table above gets its choice made by the owner or by a consumer.
- The recipe reference becomes hard to search as operations grow. The same change split the reference's `scalar` category by domain, added one-line signatures per category and let one call read several kinds; the next steps, if needed, are keyword search over Python names and generating the signatures from the F# types.
