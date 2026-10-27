# String code point conversion

- **Status:** Accepted
- **Amended:** 2026-10-27 by [String as a sequence of Unicode scalar values](string-scalar-values.md): Strings no longer hold unpaired surrogates, so the rules for them below no longer apply.
- **Date:** 2026-09-30
- **Roadmap:** [post-M2 findings from the 2026-09-30 probe 9](../plans/post-m2-roadmap.md#findings-from-the-2026-09-30-probe-9), order 28

A probe authoring rrawrlet's MCP relay needed `json.loads` and `json.dumps` with `ensure_ascii`: decoding a `\uXXXX` escape into its character and encoding each non-ASCII character as `\uXXXX`. Both need a conversion between a code point and an Int64, which the language did not have. The only workaround was a literal table of the Basic Multilingual Plane, in 64 chunks of 1024 code points, looked up with `StringIndexOf`. Even that could not cover surrogates: a request literal cannot hold an unpaired surrogate, because the session's JSON transport rejects one, so an astral character written as a `\ud83d\ude00` escape pair and a lone-surrogate escape were both unrepresentable.

## Decision

Two pure builtins. Both never fail.

| Builtin | Result | Python counterpart |
| --- | --- | --- |
| `StringFromCodePoint(code)` | `Option<String>`: `Some` one-code-point String for `0 <= code <= 0x10FFFF`, otherwise `None` | `chr(code)`, `None` where it raises `ValueError` |
| `StringToCodePoint(text)` | `Option<Int64>`: `Some` code point when `text` is exactly one code point, otherwise `None` | `ord(text)`, `None` where it raises `TypeError` |

**Surrogate codes are code points.** As in Python, `StringFromCodePoint` accepts the surrogate range 0xD800..0xDFFF and gives a String holding one unpaired surrogate code unit. `StringConcat` of a lone high and a lone low surrogate then forms the pair, so decoding `\ud83d\ude00` one escape at a time and concatenating gives `😀`, exactly as Python's `json` decoder output. `StringToCodePoint` counts code points as `StringLength` does: a valid surrogate pair gives its scalar value (U+10000..U+10FFFF), an unpaired surrogate gives its code unit (0xD800..0xDFFF), and the empty String or any longer String gives `None`.

**Round trip.** `StringToCodePoint(StringFromCodePoint(n)) = Some n` for every `n` in `0..0x10FFFF`, and for every item `c` of `StringCodePoints(t)`, `StringFromCodePoint` of `StringToCodePoint(c)` gives back `c`. `StringFromCodePoint` is O(1) and `StringToCodePoint` looks at no more than two code units.

## Reason

**Option, not failure, at the edges.** A negative or too large code and a String that is not one code point are ordinary data in a decoder, not program errors; `None` lets the caller report them, as `StringParseInt64` does for malformed digits.

**Surrogates accepted, not rejected.** JSON encodes an astral character as two `\uXXXX` escapes and allows a lone one, and Python's `json.loads` decodes each to a surrogate and keeps what it gets. Rejecting surrogate codes would make both unrepresentable again, which is the friction this settles. Strings already hold unpaired surrogates (`StringLength`, `StringSlice` and `StringCodePoints` count them as code points), so no new kind of value appears.

## Alternatives not chosen

- **A code-point-at-index operation** (`StringCodePointAt(text, index)`): positional access to a String is linear in the index, so walking text with it is quadratic. `StringCodePoints` already walks text in one pass, and a one-code-point conversion composes with it.
- **Failing, or `None`, on surrogate codes:** would break decoding of JSON surrogate-pair and lone-surrogate escapes, and would give `StringFromCodePoint` a narrower range than the Strings it builds from.

## Consequences

- The two nodes follow the usual path through the semantic graph, validation (`StringIntrinsicTypeMismatch`, with an Int64 operand for `StringFromCodePoint`), snapshot and Nyason codecs (`stringFromCodePoint` / `string-from-code-point`, `stringToCodePoint` / `string-to-code-point`), typed semantic IR, execution IR, the C# backend, the viewer, the AuthoringSession wire recipe and the agent recipe reference.
- The C# backend emits `NyText.FromCodePoint`, which casts a code below 0x10000 (surrogates included) to one `char` and uses `char.ConvertFromUtf32` only above it, since that throws for a surrogate; and `NyText.ToCodePoint`, which reads one code unit, or a valid surrogate pair with `char.ConvertToUtf32`.
- A persisted String literal still cannot hold an unpaired surrogate (the snapshot's JSON writer replaces it with U+FFFD); a lone surrogate is made at run time with `StringFromCodePoint`.

## Revisit conditions

- **Byte-level conversion:** a consumer needs UTF-8 bytes (`encode`/`decode`) rather than code points.
- **Bulk conversion is hot:** converting whole Strings through `StringCodePoints` and `ListMap` turns out to dominate a real workload.
