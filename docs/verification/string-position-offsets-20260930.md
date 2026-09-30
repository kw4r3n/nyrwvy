# Code-point position offsets for String operations — 2026-09-30

**Status:** Implemented and verified. Baseline: `94a8447`.

A `String` is UTF-16 while every position counts code points, so each positional operation walked from offset 0 to its position. A loop that stepped a position and sliced or searched again was O(n²) ([language subset](../reference/language-subset.md), probes 4 and 5). The emitted `NyText` class now maps positions through a per-String position map:

- A text without surrogate code units (one vectorised `IndexOfAnyInRange('\uD800', '\uDFFF')` scan) has offset == position and length == `text.Length`.
- A text of 256 or more UTF-16 units is scanned once and its map kept in a `ConditionalWeakTable<string, PositionMap>`: a no-surrogate flag, or otherwise the code-point length and the UTF-16 offset of every 64th code point. A lookup starts at the nearest checkpoint and walks at most 63 code points; an offset maps back to a position by binary search over the checkpoints. The table is thread-safe, a map is immutable once built, and it lives as long as its String.
- A shorter text with surrogates is still walked from its start.

`StringLength`, `StringSlice`, `StringIndexOf` (its `from` and the found offset), `StringLastIndexOf` (the found offset and the empty-substring length), `StringSpan` and `StringSpanBack` (their start positions) use the new `NyText.CodePointLength`, `OffsetOf` and `PositionOf`. The semantics are unchanged: positions stay code points ([decision](../decisions/string-search-and-affixes.md)), a valid pair counts once and a lone surrogate counts as one code point, bounds are clamped to `[0, length]` as before, and an offset never falls inside a pair.

## Measurements

A microbenchmark of the emitted helpers, before (`94a8447`) and after, compiled in Release on one Linux machine, best of three runs. The slice scan is `while i < StringLength(text) { StringSlice(text, i, i + 1); i++ }`; the IndexOf scan counts `,` separators, each search starting after the last match (one `,` every 8 code points). The tail scan replaces the text with `StringSlice(rest, 1, StringLength(rest))` each step.

| Scan | Before | After |
| --- | --- | --- |
| Per-code-point slice scan, 16,000 spaces | 224 ms | 1.6 ms |
| Per-code-point slice scan, 65,536 ASCII units | 3,711 ms | 3.1 ms |
| IndexOf-from scan, 65,536 ASCII units | 153 ms | 0.57 ms |
| Per-code-point slice scan, 65,530 units with a pair every 9 code points | 3,307 ms | 17.0 ms |
| IndexOf-from scan, 65,530 units with a pair every 9 code points | 179 ms | 4.9 ms |
| Tail-slicing scan, 16,000 ASCII units | 222 ms | 38 ms |
| Tail-slicing scan, 16,000 units with pairs | 213 ms | 116 ms |

The first five rows step a position over one String, so after the first call each step is constant (no surrogates) or at most 63 code points (with surrogates): the scans become linear. The tail scan makes a new String every step, so every step pays the first scan again, and the substring copy was already O(n) per step; it stays O(n²) and gains only from the vectorised scan.

## Verification

- `StringPositionOffsetTests` (Extended) compares `StringLength`, `StringSlice`, `StringIndexOf`, `StringLastIndexOf`, `StringSpan` and `StringSpanBack` with a code-point model over three literal texts: 1,681 units of pairs with lone high and low surrogates (a lone high surrogate at the end), 70 units of the same, and 2,000 ASCII units. Positions are -2, 0, 1, 2, 63, 64, 65, 127, 128, 129, the middle, length − 1, length and length + 3, so every lookup crosses a checkpoint and the clamping. It also slices and searches deep into a 50,000-unit argument with pairs. Moving either checkpoint base by one code point made the test fail.
- `./scripts/test-fast.fish`: 599 Core tests and 29 adapter tests passed. `ListTests` keeps `System.Collections.Generic.List` out of the emitted source, so the checkpoints are built in an array trimmed to their count.
- `./scripts/test-extended.fish`: 31 Extended and 73 extended Core tests passed (the new test adds one, about 35 s of `runPure` calls).

## Not done

- The map is per String value, so text built anew each step (tail slicing, concatenation) pays the first scan every time.
- `StringCodePoints` and `StringLessThan` already walk once and are unchanged.
