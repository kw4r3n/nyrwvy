# Float64 conversion and run values

- **Status:** Accepted
- **Date:** 2026-09-28
- **Roadmap:** [post-M2 queue from the 2026-09-28 probe 3](../plans/post-m2-roadmap.md#queue-from-the-2026-09-28-probe-3), order 13

This decision settles order 13 of the post-M2 queue: how a program gets a Float64 from an Int64, and how `runPure` carries Float64 values. [Probe 3](../verification/rrawrlet-slice-probe3-20260928.md) (friction 3) could not use Float64 end to end. There was no Int64 to Float64 conversion, a Float64 result was rejected with `RunResultTypeUnsupported`, and a Float64 argument or expectation was a protocol error. The rrawrlet `usage_stats` core therefore reported its medians doubled, as Int64 tokens and Int64 milliseconds. The rejection message also said that supported results are "scalars", although Float64 is a scalar. This is the concrete consumer for the Float64 run values that the [general-purpose roadmap](../plans/general-purpose-programming-roadmap.md) left unsupported under T3. The implemented surface is described by the [language subset](../reference/language-subset.md), the [expression authoring specification](../specs/expression-authoring.md) and the [agent authoring session](../specs/agent-authoring-session.md), which remain the authority once implemented.

## Decision

**`Int64ToFloat64(value): Float64`.** It returns the Float64 nearest to the Int64 value. Every value with `|value| <= 2^53` converts exactly. Larger values round to nearest, ties to even, as C# `(double)long`, Java `(double)long` and Python `float(int)` do: `2^53 + 1` gives `2^53`, `2^53 + 3` gives `2^53 + 4`, and `Int64.MaxValue` gives `2^63`. Every Int64 is finite as a Float64, so the conversion never fails. Recipe and wire name `int64ToFloat64`, Nyason `int64-to-float64`. The name follows `Int64ToString`. An operand that is not Int64 reports `PureExpressionOperandTypeMismatch` in recipes and `Int64IntrinsicTypeMismatch` in persisted graphs, as for `Int64ToString`.

**No Float64 to Int64 conversion and no rounding yet.**

**Float64 run values.** `runPure` and `runPureByName` accept and return Float64 as `{ "kind": "float64", "value": ... }`:

- A finite value other than -0.0 is a JSON number. Responses write the shortest decimal that reads back to the same binary64 (.NET `"R"`), for example `0.1`, `3.5000000000000004`, `1E+308` and `5E-324`. Requests may write any JSON number. It is read as the nearest binary64, so `1e-400` reads as 0. A number beyond the finite range, such as `1e400`, is a protocol error rather than an infinity.
- NaN, +Infinity, -Infinity and -0.0 are the strings `"NaN"`, `"Infinity"`, `"-Infinity"` and `"-0"`. Any other string is a protocol error. A request may also write -0.0 as the number `-0` or `-0.0`.

Float64 has no flat container form: it is not accepted as `itemKind`, `okKind` or `errorKind`. A List or Option of Float64, and a Result with a Float64 side, use the nested form with `itemType`, `okType` and `errorType`, as Unit does. Float64 nests through List, Option, Result, Product and Union like every other leaf. It works in arguments, expectations, results, fake return values, argument-keyed fake `cases` and recorded call arguments.

**Bitwise matching, with every NaN equal.** An expectation matches a Float64 when the two binary64 encodings are identical, after every NaN is canonicalized to the one NaN that `Float64Bits` already uses for literals. So `NaN` matches any NaN, `-0` does not match `0`, and `3.5` does not match `3.5000000000000004`. There is no tolerance. Argument-keyed fake `cases` select keys under the same rule. A mismatch reports the first differing Float64 with its path, like any other leaf.

**The `RunResultTypeUnsupported` message names the leaves.** It now reads: "Supported results are String, Bool, Int64, Float64 and Unit values and List/Option/Result/Product/Union compositions of them with at most eight composite layers along any non-recursive path; function, capability and type-parameter types are not run values."

## Reason

**The conversion the consumer needs.** Python's `statistics.median` of an even number of `int` values is `(a + b) / 2`: an exact integer sum, then true division rounded once. `Int64ToFloat64(Int64WrappingAdd(a, b))` divided by `2.0` gives the same binary64 for every sum within Int64, because halving is exact apart from subnormals. The probe's seconds are Python floats (`0.1`, `2.5`), so they are passed as Float64 run values, and their median is `Float64Add` then `Float64Divide`, the same two IEEE operations Python performs.

**Round to nearest even.** It is the IEEE 754 default rounding, the one every mainstream conversion uses. Emitting `(double)value` in C# gives it directly, because .NET converts `long` to `double` with IEEE rounding on every supported platform. A conversion that failed above `2^53` would make a total function partial, and an ordinary program using Int64 counters would have to handle a failure it cannot cause in practice.

**No reverse conversion now.** The consumer only produces Float64 values. Nothing it computes goes back from Float64 to Int64. A Float64 to Int64 conversion has to choose how to round (toward zero, floor or nearest) and what to do with NaN, infinities and values beyond the Int64 range. Without a consumer, that choice would be a guess.

**`round(median_seconds, 1)` stays unreproduced.** Python's `round(x, 1)` rounds to the nearest multiple of 0.1 by the exact decimal value of `x`, with ties to even. Neither `Float64Multiply` and a conversion nor any combination of the existing nodes reproduces it for every input: `x * 10` already rounds once, and a conversion would round again. The probe core therefore returns the unrounded median, and the test harness rounds it with Python's `round`, which matches Python's `usage_stats` on all 8 cases. A decimal rounding operation is deferred until a consumer needs it inside a program rather than for display.

**Numbers on the wire, strings only where JSON has no number.** JSON numbers read as binary64 in JavaScript, Python and .NET, and the shortest round-trip decimal reads back to the same value in all three, so finite values stay readable and lossless. Int64 run values are strings because a JavaScript number cannot hold every Int64, but every Float64 fits a JavaScript number. JSON has no NaN or infinities. JavaScript also writes -0 as `0`, so -0.0 is a string as well. The strings are the names .NET and JavaScript print for these values. Hexadecimal bits, as snapshot literals use, would be exact too, but an agent could neither read nor write `0.1` as `3fb999999999999a` without a tool.

**Bitwise matching.** A tolerance would make the result depend on a chosen epsilon, and the probe's comparison against Python needs exact results anyway. IEEE equality (`Float64Equal`) cannot serve as expectation matching, because NaN would never match itself and `-0` would match `0`. Bitwise equality with one canonical NaN is reflexive and deterministic. It is also the rule the semantic graph already applies to Float64 literals, where NaN payloads and signs are not distinct facts.

**Measured effect.** The probe 3 core was rewritten with real Float64 medians (`probe3/usage13.py`, outside the repository): `Row.medianTokens` and `Row.medianSeconds` are `Option<Float64>`, and `Sample.elapsedSeconds` is an `Option<Float64>` argument instead of rounded Int64 milliseconds. It reran 8/8 against Python and compared the medians directly, without the x2 scaling. The unrounded seconds median equals `statistics.median` exactly in every case, including `0.15000000000000002` for 0.1 and 0.2, and it gives Python's `median_seconds` after harness-side rounding. The core went from 7 types, 18 functions and 219 nodes to 7 types, 20 functions and 258 nodes. The single doubled `median2` (31 nodes) became `medianTokens` (34, exact Int64 sum and one conversion) and `medianSeconds` (33, Float64), plus `floatLess` (3). One Float64 median over converted tokens would have been about 19 functions and 224 nodes, but it would round each token before the sum, which differs from Python above `2^53`. The probe chose the exact form.

## Consequences

- `Int64ToFloat64` follows the usual path through the semantic graph, validation, snapshot and Nyason codecs, typed semantic IR, execution IR, the C# backend (`(double)value`), the viewer (`int64 to float64` with a source dependency) and both recipe wire forms.
- `PureRunValue.Float64` holds `Float64Bits`, so run values inherit its NaN canonicalization. The generated runner reports a Float64 result or call argument as its 16 hexadecimal bit digits, so no value is rounded between the child process and the response. It builds Float64 arguments and fake responses from their bits.
- The runner and protocol tests that used Float64 as the example of an unsupported run value now use a type nested nine Lists deep.
- Operations with a declared `failureType` remain the only T3 non-support.

## Revisit conditions

- **Float64 to Int64:** a consumer computes an Int64 (an index, a count, a bucket) from a Float64 inside a program.
- **Decimal rounding:** a consumer needs `round(x, n)` inside a program rather than for display, or compares rounded values.
- **Float64 equality in `ValueEqual`, or Float64 keys:** a consumer needs a structural comparison or a lookup key that contains Float64, which would need the same bitwise rule as run values.
