# Rounding half away from zero, and decimal places

- **Status:** Accepted
- **Date:** 2026-10-03
- **Roadmap:** [post-M2 owner choice of 2026-10-03](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-03), order 43
- **Amends:** [Float64 to Int64](float64-to-int64.md) (the tie rule of `Float64RoundToInt64`)

On 2026-10-03 the owner chose to prefer what one would ordinarily expect over Python's behaviour. Python's `round` rounds ties to even (`round(0.5)` is 0, `round(2.5)` is 2) and rounds the binary value (`round(2.675, 2)` is 2.67). Most people expect 1, 3 and 2.68. Decimal rounding, `round(x, n)`, was an open candidate observed in probes 15 and 17. The owner chose that both roundings follow the ordinary rule.

## Decision

- **`Float64RoundToInt64(value)` rounds half away from zero.** `0.5` gives 1, `2.5` gives 3, `-0.5` gives -1 and `-2.5` gives -3. It is no longer ties to even. `None` is still returned for NaN, the infinities and results outside Int64.
- **`Float64Round(value, digits): Float64`** rounds to `digits` decimal places, half away from zero. Negative `digits` round to tens, hundreds and so on (`Float64Round(1250, -2)` is 1300).
  - It rounds the decimal the value is read and written as: the shortest decimal text that reads back to the same binary64, the text `Float64ToString` produces. So `Float64Round(2.675, 2)` is 2.68 and `Float64Round(1.005, 2)` is 1.01, although each binary64 lies just below the written decimal.
  - The rounded decimal is read back as the nearest binary64.
  - NaN, the infinities and zeros are returned unchanged.
  - A result that rounds to zero keeps the input's sign (`Float64Round(-49, -2)` is `-0.0`), as `Float64Truncate` does.
  - A result beyond the Float64 range is infinity (`Float64Round(1.7976931348623157e308, -308)`).
  - `digits` at or beyond the value's last significant digit return the value unchanged.
  - Never fails. Wire `float64Round` with the fields `value` and `digits`; Nyason `float64-round`.
- `Float64Round(value, 0)` and `Float64RoundToInt64(value)` agree for every value in Int64 range. A binary64 whose shortest text ends in `.5` is exactly that half, so the decimal and binary roundings coincide at zero places.

## Reason

Ties to even avoids bias in long sums, which matters to statistics code but surprises everyone else, and rounding the binary value makes `2.675` round down although it is written `2.675`. Rounding the shortest decimal text matches what the program and its reader see. It also makes the result independent of how close the binary64 happens to fall to the written decimal.

## Consequences

- Programs authored against order 34 that relied on ties to even now get the other neighbour at exact halves. The only consumers were probe ports, whose Python comparisons at exact halves change.
- The C# backend's `NyNumber.RoundToInt64` uses `MidpointRounding.AwayFromZero`. `NyNumber.Round` formats with `"R"`, rounds the digit string and parses the result back.

## Revisit conditions

- A consumer needs unbiased (ties to even) rounding, for example to aggregate many rounded values. That would be a separately named operation, not a change to these.
