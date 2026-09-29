# String search, affixes and split

- **Status:** Accepted
- **Date:** 2026-09-29
- **Roadmap:** [post-M2 queue from the 2026-09-29 probe 4](../plans/post-m2-roadmap.md#queue-from-the-2026-09-29-probe-4), order 18

This decision settles order 18 of the post-M2 queue. [Probe 4](../verification/rrawrlet-slice-probe4-20260929.md) (friction 2) had only `StringContains` for searching text, so finding a position, testing a prefix or splitting a line took 13 functions and 199 nodes of emulation.

## Decision

Five pure builtins. All take Strings (and `from` an Int64), compare ordinally (exact UTF-16 code units, no culture, case folding or normalization), count positions in code points as `StringLength` and `StringSlice` do, and never fail.

| Builtin | Result | Python counterpart |
| --- | --- | --- |
| `StringIndexOf(text, substring, from)` | `Option<Int64>`: first occurrence starting at or after `from` | `text.find(substring, from)`, `-1` as `None` |
| `StringLastIndexOf(text, substring)` | `Option<Int64>`: last occurrence | `text.rfind(substring)`, `-1` as `None` |
| `StringStartsWith(text, prefix)` | `Bool` | `text.startswith(prefix)` |
| `StringEndsWith(text, suffix)` | `Bool` | `text.endswith(suffix)` |
| `StringSplit(text, separator)` | `List<String>`: pieces between non-overlapping occurrences, left to right, empty pieces kept | `text.split(separator)` |

**`from` clamps like `StringSlice`.** It is clamped to `[0, StringLength(text)]`. A negative `from` is 0, not a position counted from the end, and a `from` past the end is the end. An empty substring is found at the clamped `from`, and `StringLastIndexOf` finds it at `StringLength(text)`.

**An empty separator gives `[text]`.** Python raises `ValueError` for `split("")`. `StringSplit` must not fail, and `[text]` is what a separator that never occurs gives, so `join(StringSplit(t, s), s) = t` holds for every separator. Splitting into characters is already `StringCodePoints(text)`.

**Matches respect code-point boundaries.** A match counts only when it starts and ends on a code-point boundary. A lone surrogate substring does not match half of a surrogate pair, and a prefix or suffix that ends inside a pair does not match. This is what Python's code-point strings do, and it means a split never breaks a pair.

Outside the clamping of `from` and the empty separator, the results equal Python's (a parity table is in `tests/Nyrwvy.Core.Tests/StringSearchTests.fs`).

## Reason

**Code-point positions.** `StringLength`, `StringSlice` and `StringCodePoints` already count code points, so a position from `StringIndexOf` can go straight into `StringSlice`. UTF-16 offsets would be a second position space that is wrong for text above U+FFFF.

**`Option<Int64>` over `-1`.** A sentinel is easy to pass to `StringSlice` by mistake; `Option` makes the caller branch with `MatchOption`, as `StringParseInt64` and `ListItemAt` do.

**Clamping over Python's negative positions.** Every other Nyrwvy position (`StringSlice`, `ListItemAt`) treats negative values as clamped or absent, never as counting from the end. One rule for all positions is easier to author than Python parity for one argument.

**No culture, no regex.** Ordinal search is exact and portable, and matches `StringEqual` and `StringContains`. Case-insensitive search and patterns are separate features with their own semantics.

## Consequences

- The five nodes follow the usual path through the semantic graph, validation (`StringIntrinsicTypeMismatch`), snapshot and Nyason codecs (`stringIndexOf` / `string-index-of`, and so on), typed semantic IR, execution IR, the C# backend, the viewer, the AuthoringSession wire recipe and the agent recipe reference. New value-dependency roles name the `from`, prefix, suffix and separator edges.
- The C# backend emits `NyText` helpers that search with ordinal `string.IndexOf` and `LastIndexOf` and then convert UTF-16 offsets to code points in one pass, so each call is linear in the text apart from the ordinal search itself.
- `runPure` cannot carry an unpaired surrogate in a String, so tests check the boundary rule from String literals.

## Revisit conditions

- **Case-insensitive or culture-aware search:** a consumer must match text regardless of case or normalization.
- **Patterns:** a consumer needs regular expressions or wildcard matching rather than exact substrings.
- **Split limits:** a consumer needs Python's `maxsplit` or whitespace splitting (`split()` without a separator).
