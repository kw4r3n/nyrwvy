# Standard operations with an owner choice

- **Status:** Accepted
- **Date:** 2026-10-01
- **Roadmap:** [post-M2 queue from the 2026-10-01 standard-operations review](../plans/post-m2-roadmap.md#queue-from-the-2026-10-01-standard-operations-review), orders 37 to 39

The [standard-operations decision](standard-operations.md) added the operations whose meaning has no choice to make and left a table of operations that each had one. `ListRange` and hexadecimal text already had observed hand-written cost, so they needed only the owner's choice. The owner made the two choices below on 2026-10-01. The implemented surface is described by the [language subset](../reference/language-subset.md), the [expression authoring specification](../specs/expression-authoring.md) and the [C# backend](../specs/csharp-backend.md), which remain the authority.

## Decision

**A result too large to allocate is a host failure** (owner choice). `ListRange(start, end)` is `(Int64, Int64) -> List<Int64>`, Python `list(range(start, end))`, empty when `end <= start`. `StringRepeat(text, count)` is `(String, Int64) -> String`, Python `text * count`, `""` when `count <= 0`. Neither returns `Option` and neither clamps. A size that cannot be allocated stops the run as a host failure, which `runPure` reports as `RunRuntimeFailure` with `System.OutOfMemoryException`. The alternatives were `Option` with a fixed limit, or clamping at a limit. Both need a limit with no basis, and neither would make the operations total, because the real limit is the memory left, not a number. A chain of `StringConcat` or `ListConcat` that doubles its value already fails this way. Wire `listRange` (fields `start`, `end`) and `stringRepeat` (fields `text`, `count`); Nyason `list-range` and `string-repeat`.

**Hexadecimal text is signed** (owner choice). `Int64ToHexString(value)` writes the value's sign and magnitude: lowercase, the fewest digits, `-` for negative values (`-255` gives `-ff`), no `0x` and no padding. This is the same form as `Int64ToString`, so the text means the same number in either base, and it is also Python `format(value, "x")`. The alternative was two's complement as C# `ToString("x")` (`-1` gives `ffffffffffffffff`). Matching Python was not the reason, since the language does not have to follow Python. The reason is that the signed form keeps the number's meaning, and a program that wants two's-complement text can still build it: the low and high 32-bit words are `Bits32ToInt64(Bits32FromInt64(value))` and the same of `Int64FloorDivide(value, 4294967296)`, each formatted and padded to 8 digits with `StringRepeat`. `StringParseInt64Hex(text)` is `String -> Option<Int64>` with the strictness of `StringParseInt64`: an optional `-` and one or more ASCII hexadecimal digits of either case, within range. `0x`, `+`, whitespace and `_` give `None`, although Python `int(text, 16)` accepts them. Both cases are accepted because JSON `\u` escapes allow both. Wire `int64ToHexString` and `stringParseInt64Hex`; Nyason `int64-to-hex-string` and `string-parse-int64-hex`.

**Also added, with no choice left:**

- `StringRepeat` follows from the size choice above, which was its only open point. Padding (`rjust`, `zfill`) is not a separate operation; it is `StringConcat(StringRepeat(fill, width - StringLength(text)), text)`.
- `Int64Min` and `Int64Max`, Python `min` and `max` of two values, have no choice to make.
- `Int64WrappingAbs` makes the `Int64.MinValue` case explicit in its name, as `Int64WrappingAdd` does: `Int64.MinValue` gives itself, the two's-complement wrap that `Int64FloorDivide` already gives for `Int64.MinValue / -1`. Python `abs` gives `2^63`, which Int64 cannot hold.

Wire `int64Min` and `int64Max` (fields `left`, `right`) and `int64WrappingAbs` (field `value`); Nyason `int64-min`, `int64-max` and `int64-wrapping-abs`.

**Still not added:** `StringToUpper`, because Python `upper()` changes length (`ß` to `SS`) and no consumer has needed it.

## Reason

Probes 5, 7 and 12 wrote ranges by hand (self-recursive `repeat`, `"x" * n` as a generator, a constant list sliced only for its length), and seven probes wrote hex digits by hand (JSON `\u` escapes in 9 to 11, `hexdigest` in 12 and 16, `isHex` in 17 and 18). With `ListRange`, a `ListFold` over `ListRange(0, n)` is a counted loop, and `ListEffectFold`'s early stop makes it a bounded while loop. Probe 15's self-recursive event loop overflowed the stack at 12,000 to 14,000 turns; this form avoids that recursion (inferred, not measured).

## Revisit when

- A consumer needs a range with a step or a descending range. It can be composed today with `ListMap` or `ListReverse`.
- A consumer needs to recover from a size that is too large instead of stopping. That would need a fixed limit and an `Option` form.
- A consumer needs `0x` prefixes, uppercase output or fixed-width two's-complement text often enough that the composition above is a cost.
