# Float64 decimal text

- **Status:** Accepted
- **Date:** 2026-09-30
- **Roadmap:** [post-M2 findings from the 2026-09-30 probe 9](../plans/post-m2-roadmap.md#findings-from-the-2026-09-30-probe-9), order 29

[Probe 9](../verification/rrawrlet-slice-probe9-20260930.md) ported rrawrlet's Team tool relay, which reads JSON with `json.loads` and writes it with `json.dumps`. A JSON number with a fraction or an exponent becomes a Python `float`, and `json.dumps` prints a float as `repr(value)`. The language had no conversion between Float64 and decimal text. The port emulated `float()` in 6 functions and 250 nodes: a decimal digit string, an Int64 mantissa of at most 17 digits and `Float64Multiply` or `Float64Divide` by a power of ten. That is exact only in the fast path, at most 15 digits and exponents within ±22. `repr` could not be emulated at all, because finding the shortest digits that round-trip needs the exact value. So `Json.Float` carried the source text beside the value, and a Float64 the program computed, such as a fake's `elapsed_seconds`, could not be printed.

## Decision

Two pure builtins. Both never fail.

| Builtin | Result | Python counterpart |
| --- | --- | --- |
| `Float64ToString(value)` | `String`: the shortest decimal text that reads back to the same binary64, in Python's layout | `repr(value)` |
| `StringParseFloat64(text)` | `Option<Float64>`: `Some` correctly rounded value when the whole text is a decimal number, otherwise `None` | `float(text)` on the JSON number grammar |

**`Float64ToString`.** The digits are the shortest that round-trip, as in Python, JavaScript and .NET `"R"`.
- **Layout:** Python's `repr`. The value is written `0.d1d2…dn × 10^decpt`.
  - When `decpt <= -4` or `decpt > 16`, the result is in exponent form `d1.d2…dne±XX`, with the `.d2…dn` omitted for a single digit and at least two exponent digits: `1e+16`, `1.5e-05`, `5e-324`, `1.7976931348623157e+308`.
  - Otherwise it is in fixed form with at least one digit after the point: `0.0001`, `100.0`, `1234567890123456.0`, `0.30000000000000004`.
- **Special values:** NaN gives `nan`, the infinities `inf` and `-inf`, and the zeros `0.0` and `-0.0`.
- **Round trip:** `StringParseFloat64(Float64ToString(x))` gives `Some x` bit for bit for every finite `x`, and `None` for `nan`, `inf` and `-inf`.

**`StringParseFloat64`.**
- **Accepted text:** `Some` only when the whole text matches `-?[0-9]+(\.[0-9]+)?([eE][+-]?[0-9]+)?` in ASCII digits. This is the JSON number grammar, except that leading zeros are accepted as `StringParseInt64` accepts them.
- **Rejected text (`None`):** a `+` sign, whitespace, `.5`, `5.`, `inf`, `nan`, underscores, hexadecimal, non-ASCII digits and the empty String.
- **Rounding:** the value is the nearest binary64, ties to even, for any number of digits and any exponent length. `1.00000000000000011102230246251565404236316680908203125`, an exact tie, gives `1.0`. The same text with one more nonzero digit gives the next binary64.
- **Out of range:**
  - Overflow gives `Some` of a signed infinity: `1e400` gives +Infinity, as Python's `float('1e400')` and `json.loads` give.
  - Underflow gives `Some` of a signed zero: `-1e-400` gives -0.0.
  - Integer text is accepted too: `StringParseFloat64("9007199254740993")` gives `2^53`. A caller that needs Python's int or float split, as `json.loads` does, decides by the grammar before converting.

## Reason

**Python's semantics, because the consumer is Python.** Every probe compares the port with the real Python program. `repr` is also the shortest-round-trip text that JavaScript and .NET print, apart from the layout, so it is the least surprising form. Python chose that layout for its own `str`, and `json.dumps` prints it.

**Only the JSON grammar.** Python `float()` also accepts whitespace, `_` separators, `inf`/`nan`/`infinity` in any case, `+`, `.5` and `5.`. A program that needs any of those can test for it with the String builtins before converting. Accepting them would make every caller that parses one grammar reject the others itself. The one extension, leading zeros, keeps `StringParseFloat64` accepting every text `StringParseInt64` accepts.

**Infinity on overflow, not `None`.** Python gives a finite or infinite float for every number in the grammar, and `json.loads("1e400")` is `inf`. `None` on overflow would force a JSON reader to invent a value. The input grammar rules out NaN, so `Some` never holds NaN.

**Both never fail.** They follow the other text conversions. Malformed text is ordinary data in a reader, and printing a Float64 has no failure case.

## Alternatives not chosen

- **General formatting** (a precision or `%g`/`%e`/`%f` style): no consumer needs it yet, and `round(x, n)` stays deferred by the [Float64 conversion decision](float64-conversion-and-run-values.md).
- **.NET's own layout** (`"R"` as is: `1E+16`, `100`, `1E-05`): its digits are the same, but it matches no consumer's output, and a program would have to re-layout it with String builtins.
- **`None` on overflow:** rejected above.
- **Python `float()`'s full grammar:** rejected above.

## Consequences

- The two nodes follow the usual path through the semantic graph, validation, snapshot and Nyason codecs, typed semantic IR, execution IR, the C# backend, the viewer, the AuthoringSession wire recipe and the agent recipe reference:
  - **Validation:** `Float64OperandTypeMismatch` for a non-Float64 `Float64ToString` operand, `StringIntrinsicTypeMismatch` for a non-String `StringParseFloat64` operand, and `PureExpressionOperandTypeMismatch` in recipes.
  - **Codec names:** `float64ToString` / `float64-to-string` and `stringParseFloat64` / `string-parse-float64`.
- **C# backend:**
  - `NyText.FromFloat64` takes only the digits and decimal exponent from `double.ToString("R", InvariantCulture)` and lays them out as Python does.
  - `NyText.ParseFloat64` checks the grammar by hand, then calls `double.Parse` with `AllowLeadingSign | AllowDecimalPoint | AllowExponent`, which in .NET Core 3.0+ is correctly rounded, gives signed infinities and zeros, and accepts an exponent of any length.
  - A differential run of the emitted helpers against CPython found 0 mismatches over 49,564 inputs, random bit patterns and halfway cases included.
- **Probe 9 port:** in the re-authored port, `Json.Float` holds only its value. The float emulation was removed: 6 functions and 263 nodes. The known divergence for more digits than binary64 holds is gone.

## Revisit conditions

- **Formatting with a precision:** a consumer prints a Float64 with a fixed number of digits (`f"{x:.2f}"`, `round(x, n)` for display).
- **A wider grammar:** a consumer reads numbers that are not JSON, such as `inf`, `nan` or `1_000`, from text it does not control.
