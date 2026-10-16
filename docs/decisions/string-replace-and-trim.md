# String replace and trim

- **Status:** Accepted
- **Date:** 2026-10-16
- **Roadmap:** [post-M2 owner choice of 2026-10-16](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-16-ordinary-string-builtins), order 59

The owner chose to add these ordinary builtins ahead of probe evidence. Their semantics follow what one would normally expect, which is Python's except where stated below. No probe ranked their absence as a cost, but probes wrote around them: [probe 8](../verification/rrawrlet-slice-probe8-20260929.md) wrote `text.replace(secret, "[REDACTED]")` as a `StringSplit` and `StringJoin`, and several probes wrote `strip()` with `StringSpan`, `StringSpanBack` and `StringSlice`.

## Decision

- **`StringReplace(value, pattern, replacement): String`** replaces every non-overlapping occurrence of `pattern` with `replacement`. It scans left to right, like Python `str.replace(old, new)`: `"aaa"` with `"aa"` replaced by `"b"` gives `"ba"`. A replacement that contains the pattern is not scanned again. Matching is ordinal and takes the same matches as `StringSplit`: a match must start and end on code-point boundaries, so a pattern that is half of a surrogate pair is not found inside the pair. Wire `stringReplace` with fields `value`, `pattern` and `replacement`; Nyason `string-replace`.
- **An empty pattern returns `value` unchanged.** Python inserts the replacement before every code point and at the end (`"abc".replace("", "x")` is `"xaxbxcx"`, and `"".replace("", "x")` is `"x"`). This follows `StringSplit`, where an empty separator gives `[value]` and does not split at every position ([string search](string-search-and-affixes.md)). Use `StringCodePoints` and `StringJoin` to put text between every code point.
- **`StringTrim(value)`, `StringTrimStart(value)` and `StringTrimEnd(value)`** remove the code points with the Unicode White_Space property from both ends, from the start, or from the end. That set is exactly .NET `char.IsWhiteSpace`: U+0009–U+000D, U+0020, U+0085, U+00A0, U+1680, U+2000–U+200A, U+2028, U+2029, U+202F, U+205F and U+3000. Wire `stringTrim`, `stringTrimStart` and `stringTrimEnd` with field `value`; Nyason `string-trim`, `string-trim-start` and `string-trim-end`.
- **Not Python's `str.isspace` set.** Python's `strip()`, `lstrip()` and `rstrip()` also remove U+001C–U+001F, the information separators, because Python treats them as white space. They are not White_Space in Unicode, and `StringTrim` keeps them. U+200B ZERO WIDTH SPACE and U+FEFF are not White_Space either, so they are kept as in Python. `StringSpan` and `StringSpanBack` with `StringSlice` remain the way to strip an explicit set, including Python's 29-code-point set or `strip(chars)`.
- All four operations are pure, never fail and run in linear time. A trim never splits a surrogate pair, because every White_Space code point is in the BMP and no surrogate is white space.

## Consequences

- The four nodes go through the semantic graph, validation (`StringIntrinsicTypeMismatch`), snapshot and Nyason codecs, typed semantic IR, Execution IR, the C# backend (`NyText.Replace`, `NyText.Trim`, `NyText.TrimStart` and `NyText.TrimEnd`), the viewer, wire recipes and the recipe reference. Structural dependencies name the pattern `substring` and the replacement `replacement`, a new role. No IR schema version changes. A snapshot that uses any of them is written at snapshot JSON 17 / Nyason `nayamnyam.snapshot.v13` (feature `StringReplaceAndTrim`); see [snapshot schema versions](../specs/schema-evolution-policy.md#snapshot-schema-versions).
- `StringReplaceTrimTests` covers them through every layer. Under `runPure` it checks overlapping patterns, empty patterns and replacements, a pattern that is not found, and surrogate pairs. For the trims it checks each of the 25 White_Space code points, U+001C, U+001F, U+200B and U+FEFF (kept), all-white-space text and empty text.

## Revisit conditions

- A consumer needs Python's set exactly (`strip()` of text with information separators). That is `StringSpan` with an explicit set, or a separate operation, not a change here.
- A consumer needs a bounded number of replacements (`str.replace(old, new, count)`).
