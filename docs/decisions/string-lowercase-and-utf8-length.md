# String lowercase and UTF-8 length

- **Status:** Accepted
- **Date:** 2026-09-29
- **Roadmap:** [post-M2 queue from the 2026-09-29 probe 4](../plans/post-m2-roadmap.md#queue-from-the-2026-09-29-probe-4), order 21

This decision settles order 21 of the post-M2 queue. [Probe 4](../verification/rrawrlet-slice-probes-summary.md#probe-4) had no case mapping (friction 3), so `.lower()` was emulated for ASCII only with 3 functions and 63 nodes and every literal was passed in both spellings, and no UTF-8 length (friction 6), so `len(text.encode())` was emulated per code point with `StringLessThan` thresholds (22 nodes).

## Decision

Two pure builtins over a String. Both never fail.

| Builtin | Result | Python counterpart |
| --- | --- | --- |
| `StringToLower(text)` | `String`: each code point replaced by its Unicode simple lowercase mapping | `text.lower()`, except the two cases below |
| `StringUtf8ByteCount(text)` | `Int64`: bytes in the UTF-8 encoding | `len(text.encode("utf-8"))` |

**Simple (one-to-one) mapping per code point.** `StringToLower` walks the text by code point, as `StringLength` counts, and maps each one with the `Simple_Lowercase_Mapping` of UnicodeData: one code point to one code point, with no culture, no context and no normalization. `I` becomes `i` (never the Turkish dotless `ı`), KELVIN SIGN `K` (U+212A) becomes `k`, OHM SIGN `Ω` becomes `ω`, CAPITAL SHARP S `ẞ` becomes `ß`, and a supplementary letter such as DESERET `𐐀` (U+10400) becomes `𐐨` (U+10428). Code points with no lowercase mapping, digits, CJK and emoji included, are kept. An unpaired surrogate is its own code point, has no mapping and is kept as it is, so `StringLength(StringToLower(t)) = StringLength(t)` for every `t`.

**How this differs from Python `str.lower()`.** Python applies the *full* lowercase mapping of SpecialCasing, which also has context. Outside the following two cases, the results are equal (the parity table is in `tests/Nyrwvy.Core.Tests/StringCaseTests.fs`):

- `İ` (U+0130, LATIN CAPITAL LETTER I WITH DOT ABOVE): Python gives the two code points `i̇` (U+0069 U+0307); `StringToLower` gives the simple mapping `i` (U+0069). This is the only unconditional full lowercase mapping that is longer than one code point.
- Final sigma: Python lowercases a `Σ` at the end of a word to `ς` (Final_Sigma); `StringToLower` maps every `Σ` to `σ`, so `"ΟΔΟΣ"` gives `"οδοσ"`, where Python gives `"οδος"`.

Both Python and `StringToLower` keep lone surrogates unchanged.

**Unicode data version.** The mappings come from the Unicode data of the .NET runtime that runs the program (usually ICU, NLS on Windows when configured, or .NET's own table in globalization-invariant mode). A code point assigned or given a mapping in a Unicode version newer than that data is kept unchanged, so results for such code points can differ from Python built on a newer Unicode version (Python 3.14 uses Unicode 16.0). Long-assigned letters, including every case in the parity table, map the same everywhere.

**UTF-8 length, lone surrogates as 3 bytes.** `StringUtf8ByteCount` counts 1 byte for U+0000..U+007F, 2 for U+0080..U+07FF, 3 for the rest of the BMP and 4 for a surrogate pair (a code point above U+FFFF). An unpaired surrogate counts 3 bytes. Strict Python cannot encode a lone surrogate and raises `UnicodeEncodeError`; 3 is what Python gives with `errors="surrogatepass"` (WTF-8), and also the length of the U+FFFD replacement that .NET's UTF-8 encoder writes for it, so the count equals the byte length of what the runtime would actually write.

**Casefold is out of scope.** `str.casefold()` (full case folding, e.g. `ß` to `ss`) and case-insensitive comparison are separate features; `StringToLower` is only the lowercase mapping. There is no `StringToUpper` yet.

## Reason

**Simple mapping keeps the length.** A one-to-one mapping never changes the code-point count, so positions found in the lowercased text (`StringIndexOf`, `StringSlice`) are positions in the original text. That is what a case-insensitive search over `StringToLower` of both sides needs, and the full mapping of `İ` would break it. The price is two documented differences from Python, both outside ASCII.

**Code points, not UTF-16 units.** Lowercasing each UTF-16 unit on its own cannot map a supplementary letter, and `string.ToLowerInvariant` replaces an unpaired surrogate with U+FFFD. Walking code points keeps both consistent with `StringLength`, `StringSlice` and `StringCodePoints`.

**UnicodeData over .NET's invariant casing for `İ`.** .NET's invariant casing deliberately keeps `İ` (and `ı` for uppercase) unchanged, unlike UnicodeData. `StringToLower` maps `İ` to `i` explicitly so that the definition is the Unicode simple mapping, and the result starts with the same code point as Python's.

**Count, not encode.** A consumer that needs a byte budget (probe 4's `repo_map`) needs only the length; bytes as a value type are not part of the language.

## Consequences

- The two nodes follow the usual path through the semantic graph, validation (`StringIntrinsicTypeMismatch`), snapshot and Nyason codecs (`stringToLower` / `string-to-lower`, `stringUtf8ByteCount` / `string-utf8-byte-count`), typed semantic IR, execution IR, the C# backend, the viewer, the AuthoringSession wire recipe and the agent recipe reference.
- The C# backend emits `NyText.ToLower`, one pass that maps a surrogate pair with `Rune.ToLowerInvariant`, keeps an unpaired surrogate, maps U+0130 to `i` and any other unit with `char.ToLowerInvariant`; and `NyText.Utf8ByteCount`, which is `Encoding.UTF8.GetByteCount`. Both are linear.
- Probe 4's known divergences: MX1 (`x.marKdown` with a Kelvin sign) now matches Python through `StringToLower`. FX3 (`[HİGH]`) stays a divergence by design: Python's own result `hi̇gh` comes from the full mapping.

## Revisit conditions

- **Full mapping or casefold:** a consumer must reproduce Python's `lower()` exactly for `İ` or final sigma, or compare text with `casefold()`.
- **Uppercase or titlecase:** a consumer needs `upper()` or `title()`.
- **Pinned Unicode version:** results must not depend on the runtime's Unicode data.
