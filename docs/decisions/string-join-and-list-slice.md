# String join and list slice

- **Status:** Accepted
- **Date:** 2026-09-29
- **Roadmap:** [post-M2 queue from the 2026-09-29 probe 4](../plans/post-m2-roadmap.md#queue-from-the-2026-09-29-probe-4), order 22

This decision settles order 22 of the post-M2 queue. [Probe 4](../verification/rrawrlet-slice-probe4-20260929.md) had no join and no list slice (friction 7), so `"\n".join(xs)` and `", ".join(xs[:20])` became `joinStep`, `joinFirst` and a `JoinAcc` accumulator (31 nodes), and `sections[len(shown):]` became `omitStep` with an `OmitAcc` accumulator (19 nodes).

## Decision

Two pure builtins. Both never fail.

| Builtin | Result | Python counterpart |
| --- | --- | --- |
| `StringJoin(list: List<String>, separator: String)` | `String` | `separator.join(list)` |
| `ListSlice(list: List<T>, start: Int64, end: Int64)` | `List<T>` | `list[start:end]` for `0 <= start, end` |

**StringJoin is Python join.** The items are concatenated in order with the separator between adjacent items: `[]` gives `""`, `[x]` gives `x`, `["", ""]` with `","` gives `","`. It is the inverse of `StringSplit` for a non-empty separator: `StringJoin(StringSplit(t, s), s) = t`. The list must be exactly `List<String>`; there is no implicit conversion of other item types (map them with `Int64ToString` first). Concatenation is the same as `StringConcat`, so, as there, an item ending in a high surrogate followed by one starting with a low surrogate forms one code point; Python keeps the two lone surrogates. No other difference exists.

**ListSlice clamps like StringSlice.** `ListSlice` counts positions in items the way `StringSlice` counts code points: each bound is clamped to `[0, ListLength(list)]`, the result holds the items at `[start, end)`, and `end <= start` gives the empty list. A negative bound becomes 0; it is **not** counted from the end, so `ListSlice(xs, -2, 2)` is the first two items where Python `xs[-2:2]` is usually empty. This keeps one clamping rule across `StringSlice`, `StringIndexOf`'s `from`, `ListItemAt` (negative index is `None`) and `ListSlice`, keeps the operation total over every Int64 (`Int64.MinValue` included), and avoids a Python rule that changes meaning at zero. A "last n items" slice is `ListSlice(xs, ListLength(xs) - n, ListLength(xs))`.

**Generic over the item type.** Like `ListItemAt`, `ListSlice` infers `T` from the `list` operand, which must be some `List<T>`, and produces the same `List<T>`; item types can be any type, including nested lists, options and named types. No item-type argument is written.

**No step and no other helpers.** A step (`xs[::2]`), negative indexing, string repetition and padding (`'  ' * n`, `{:>{w}}`) and early-stop folds, which friction 7 also lists, are out of scope; they can be added by later queue entries if a probe measures them.

## Consequences

- Wire recipes `stringJoin` (`list`, `separator`) and `listSlice` (`list`, `start`, `end`), Nyason `string-join` and `list-slice`, snapshot kinds `stringJoin` and `listSlice`, typed and execution IR kinds of the same names, and C# emission `NyText.Join` and `NyList<T>.Slice`.
- Persisted-graph validation: `StringIntrinsicTypeMismatch` for `StringJoin`, `ListSliceListTypeMismatch` and `ListSliceBoundTypeMismatch` for `ListSlice`. Authoring reports `PureExpressionOperandTypeMismatch` with the operand path.
- The Python parity table is in `tests/Nyrwvy.Core.Tests/StringJoinListSliceTests.fs`.
