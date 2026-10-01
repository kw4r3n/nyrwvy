# Float64 to Int64 with Python's rounding

- **Status:** Accepted
- **Date:** 2026-10-01
- **Roadmap:** [post-M2 queue from the 2026-10-01 probe 20 owner choice](../plans/post-m2-roadmap.md#queue-from-the-2026-10-01-probe-20-owner-choice), order 34

This decision settles order 34 of the post-M2 queue. The [Float64 conversion decision](float64-conversion-and-run-values.md) deferred any conversion from Float64 to Int64 until a consumer computed an Int64 from a Float64 inside a program. [Probe 20](../verification/music-logic-probe20-20261001.md) is that consumer: the nearest n-EDO step of an interval is `round(cents × n / 1200)`, its error in seconds is `round(error × 60)`, and `exp2` needs `floor`. The port wrote them by hand as a 62-step binary search over `Int64ToFloat64` (3 functions, 95 nodes). [Probe 15](../verification/rrawrlet-slice-probe15-20261001.md) had already built milliseconds from Float64 seconds by hand. The implemented surface is described by the [language subset](../reference/language-subset.md#float64), the [expression authoring specification](../specs/expression-authoring.md#float64roundtoint64-and-float64floortoint64) and the [C# backend](../specs/csharp-backend.md), which remain the authority.

## Decision

**Two pure builtins, both `Float64 -> Option<Int64>`:**

- `Float64RoundToInt64(value)` is Python `round(value)` with no digits: the nearest integer, ties to even (`2.5` gives 2, `-0.5` gives 0, `-2.5` gives -2).
- `Float64FloorToInt64(value)` is Python `math.floor(value)`: the largest integer not above the value (`-0.5` gives -1).

Both give `None` for NaN, for either infinity, and for a result outside Int64, where Python raises `ValueError` or `OverflowError` or returns an `int` larger than Int64. Every finite result in range is exact, because every binary64 in [-2^63, 2^63) that is an integer is an Int64. They never fail. Wire kinds `float64RoundToInt64` and `float64FloorToInt64`, Nyason `float64-round-to-int64` and `float64-floor-to-int64`, recipe field `value`.

**Not added:** truncation toward zero (Python `int(x)`), ceiling, and decimal rounding `round(x, n)`. Truncation and ceiling follow from floor in a few nodes (`-floor(-x)`), and no consumer needed them. Decimal rounding rounds by the exact decimal value of the binary64 and returns a Float64, a different operation; probe 17's `round(x, 1)` and probe 15's are its only consumers, and its revisit condition below stays open.

## Reason

**The two rules Python programs use.** `round(x)` and `math.floor(x)` are how Python code turns a float into an int: the step index and seconds of probe 20 use `round`, `exp2` and any bucketing use `floor`. Matching Python exactly keeps ports comparable with their reference, as every other builtin does.

**`Option<Int64>` instead of a partial or saturating result.** A program can always produce NaN or an infinity, and a large finite value has no Int64. Failing would make the conversion an abort point in otherwise total code; saturating would silently give a wrong number. `Option` keeps it total and makes the out-of-range case visible, as `StringParseInt64` already does for text. When a program knows its range, it unwraps with a default.

**Two names, not a mode argument.** A mode would be a new kind of operand (an enum or a string) with its own validation. Two unary nodes reuse the existing unary path end to end.

**Measured effect.** Probe 20 re-authored with the builtins (`author.py order34` in the project's shared files) replaced `truncPos`, `floorF` and `roundF` (95 nodes) with two 5-node wrappers (10 nodes); the module went from 1,153 to 1,068 nodes and matched the same 3,400/3,400 cases. The JSONL test checks both nodes against Python on ties, values just below one half, 2^52 ± 0.5, subnormals, both ends of the Int64 range, NaN and the infinities.

## Consequences

- The nodes follow the usual path through the semantic graph, validation (`Float64OperandTypeMismatch`), snapshot and Nyason codecs, typed semantic IR, execution IR, the C# backend (`NyNumber.RoundToInt64`, which is `Math.Round(value, MidpointRounding.ToEven)`, and `NyNumber.FloorToInt64`, which is `Math.Floor`, each followed by a range check), the viewer (`round to int64`, `floor to int64`), both recipe wire forms and the agent recipe reference.
- No schema version changes; snapshots without the nodes keep their bytes.

## Revisit conditions

- **Truncation or ceiling:** a consumer needs `int(x)` or `math.ceil(x)` often enough that the floor form costs more than a few nodes.
- **Decimal rounding:** a consumer needs `round(x, n)` inside a program rather than for display, or compares rounded values (unchanged from the [Float64 conversion decision](float64-conversion-and-run-values.md#revisit-conditions)).
- **A failing form:** a consumer wants an abort rather than `None` for values it treats as impossible.
