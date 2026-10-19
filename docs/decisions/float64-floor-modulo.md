# Float64 floor modulo

- **Status:** Accepted
- **Date:** 2026-10-19
- **Roadmap:** [post-M2 owner choice of 2026-10-16](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-16-ordinary-builtins), order 70

`Float64` had `Float64Divide` but no remainder, so wrapping an angle or a phase (`x % 360.0`) took `Float64Floor` of a quotient and a multiply, which is not exact. `Int64` has `Int64FloorModulo`, Python's `%`. The owner chose on 2026-10-19 to add the Float64 counterpart under the [warranted-operation rule](../plans/post-m2-roadmap.md#selection-rule-from-here).

## Decision

**`Float64FloorModulo(left, right): Float64`** is Python's float `%`, named and signed like `Int64FloorModulo`: the result is `left - right * floor(left / right)` computed exactly, as C's `fmod` moved by one divisor. Concretely, with `r = fmod(left, right)` (the IEEE truncated remainder, C# `%`):

- a nonzero `r` whose sign differs from the divisor's becomes `r + right`, so a nonzero result takes the divisor's sign: `7.5 % 2 = 1.5`, `-7.5 % 2 = 0.5`, `7.5 % -2 = -0.5`, `-7.5 % -2 = -1.5`;
- a zero `r` takes the divisor's sign (`-6.0 % 3.0` is `0.0`, `6.0 % -3.0` is `-0.0`), as Python's does.

**Zero divisors, infinities and NaN follow IEEE, not Python's exceptions.** `Float64Divide` already gives an infinity or NaN where Python raises `ZeroDivisionError`, and "binary64 infinity, NaN, and signed-zero outcomes are normal values" in the [language subset](../reference/language-subset.md#float64). So `x % 0.0`, an infinite dividend and a NaN operand give NaN, which is what `fmod` gives. A finite dividend with an infinite divisor is `fmod`'s result, the dividend, moved by the divisor when the signs differ: `5.0 % inf = 5.0` and `-5.0 % inf = inf`, which is also what Python returns. `Int64FloorModulo` returns the dividend for a zero divisor because an Int64 has no NaN; the Float64 operation does not copy that convention, since its domain has the IEEE value for it.

The fixed-signature row `float64FloorModulo` (wire `float64FloorModulo` with `left` and `right`, Nyason `float64-floor-modulo`) is checked from the `OperationSignatures` table.

## Reason

- **One remainder rule across the numeric types.** The Int64 operation chose Python's floor semantics and name; the Float64 one follows so that `x % m` reads the same way, and because the sign-of-divisor result is the one wrapping and bucketing need.
- **Exactness.** `fmod` is exact for every finite pair, and the one adjustment adds exactly representable values in the common case; the `floor(left / right)` formula rounds twice. Python's own float `%` is implemented this way.
- **No failure.** Every Float64 operation yields a value; raising on a zero divisor would make this the one that aborts.

## Consequences

- It follows `Float64Divide` through every layer: validation (`Float64OperandTypeMismatch` in a persisted graph, `PureExpressionOperandTypeMismatch` from a recipe), both codecs, typed semantic IR, Execution IR, the C# backend (`NyNumber.FloorModulo`, the `%` and the one adjustment above), wire recipes and the recipe reference. A snapshot that uses it is written at snapshot JSON 20 / Nyason `nayamnyam.snapshot.v16`.
- `Float64FloorModuloTests` checks a Python parity table under `runPure`, the signed zeros and every NaN case.

## Revisit conditions

- **`Float64FloorDivide`:** a consumer needs Python's float `//` beside the remainder (today `Float64Floor(Float64Divide(x, y))`, which is not exact for large quotients).
- **Truncated remainder:** a consumer needs C's `fmod` sign-of-dividend semantics, which is `Float64FloorModulo` adjusted back or a new operation.
