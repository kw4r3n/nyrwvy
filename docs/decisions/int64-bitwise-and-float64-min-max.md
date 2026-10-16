# Int64 bitwise operations and Float64Min and Float64Max

- **Status:** Accepted
- **Date:** 2026-10-17
- **Roadmap:** [post-M2 owner choice of 2026-10-16](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-16-ordinary-builtins), order 61

The language had bitwise operations only on `Bits32`, and min and max only on Int64. The owner chose to add Int64 bitwise operations and shifts and a Float64 minimum and maximum as ordinary builtins ahead of probe evidence, with semantics that follow normal expectation: Python where it applies, and IEEE 754-2019 for the Float64 pair, where Python's `min` and `max` depend on operand order.

## Decision

All eight are pure and never fail at run time.

- **`Int64BitAnd(left, right)`, `Int64BitOr(left, right)`, `Int64BitXor(left, right)`** and **`Int64BitNot(value)`** are two's-complement bitwise operations on Int64, as Python `&`, `|`, `^` and `~` on integers in the Int64 range: `Int64BitNot(x)` is `-x - 1`.
- **`Int64ShiftLeft(value, count)`** and **`Int64ShiftRight(value, count)`** take the count clamped to `0..64`. A negative count shifts by 0, so the value is unchanged (Python raises). `Int64ShiftLeft` keeps the low 64 bits, so it wraps like the other wrapping Int64 arithmetic, and a count of 64 or more gives 0. `Int64ShiftRight` is arithmetic, as Python `>>`: the sign propagates, and a count of 64 or more gives 0 for a non-negative value and -1 for a negative one. The count is never masked to 6 bits, as C# and most hardware mask it.
- **`Float64Min(left, right)`** and **`Float64Max(left, right)`** are the IEEE 754-2019 `minimum` and `maximum`, which .NET `Math.Min` and `Math.Max` on `double` implement: NaN when either operand is NaN, and -0.0 is less than 0.0, so `Float64Min(0.0, -0.0)` and `Float64Min(-0.0, 0.0)` are both -0.0. The result does not depend on operand order. Python `min` and `max` return the first operand when the comparison is false, so they differ for NaN on the right and for signed zeros.

## Reason

The pieces were expressible before (bits through `Bits32` halves, or arithmetic; min and max through `Float64LessThan`, `Float64IsNaN` and a branch), but only as long graphs whose edge cases an author can easily get wrong. A shift whose count is clamped rather than masked or rejected has one result for every Int64 count, which keeps the operations total.

## Consequences

- Each operation goes through the semantic graph, validation (`Int64OperandTypeMismatch` for the binary Int64 operations, `Int64IntrinsicTypeMismatch` for `Int64BitNot`, `Float64OperandTypeMismatch` for `Float64Min` and `Float64Max`), the snapshot and Nyason codecs, typed semantic IR, Execution IR, the C# backend, the viewer, wire recipes and the recipe reference. The shifts use the wire fields `value` and `count`; the others use `left` and `right`, or `value`.
- A snapshot that uses any of them is written at snapshot JSON 17 / Nyason `nayamnyam.snapshot.v13`; see [snapshot schema versions](../specs/schema-evolution-policy.md#snapshot-schema-versions).
- The C# backend emits `&`, `|`, `^` and `~` on `long`, explicit clamping for the shifts, and `System.Math.Min` and `System.Math.Max` on `double`.
- `Int64BitwiseFloat64MinMaxTests` covers every layer and, under `runPure`, shift counts -1, 0, 1, 63, 64 and 100 on positive and negative values and `Int64.MinValue`, and NaN and signed zeros on either side of the Float64 pair.

## Revisit conditions

- A probe needs a logical (zero-filling) Int64 right shift, a rotation, or a population count.
- A probe needs Python's order-dependent `min` and `max` on Float64.
