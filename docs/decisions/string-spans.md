# String spans

- **Status:** Accepted
- **Date:** 2026-09-30
- **Roadmap:** [candidates from the 2026-09-30 probe 5](../plans/post-m2-roadmap.md#candidates-from-the-2026-09-30-probe-5), order 23

This decision settles order 23 of the post-M2 queue. [Probe 5](../verification/rrawrlet-slice-probe5-20260930.md) ranked character-class spans and strip as its largest remaining cost (#1): `charAt`, `inSet`, `skipIn`, `rskipIn`, `strip`, `isDigit` and `digitsEnd` were 7 hand-written self-recursive functions and 70 nodes serving `str.strip()`, `\s+`, `\d+`, `[-*]` and `strip("`")`. They were the port's only remaining per-character recursion and the source of both adversarial slowdowns (16,000 spaces before a path took 3.3 s).

## Decision

Two pure builtins over a String, a character set given as a String, and an Int64 bound. Both return a code-point position, run in linear time and never fail.

| Builtin | Result | Python counterpart |
| --- | --- | --- |
| `StringSpan(value, set, from)` | `Int64`: the first position at or after `from` whose code point is not in `set`, or `StringLength(value)` | `start + len(v[start:]) - len(v[start:].lstrip(set))` with `start = clamp(from, 0, len(v))` |
| `StringSpanBack(value, set, end)` | `Int64`: the least position `p <= end` such that every code point in `[p, end)` is in `set` | `len(v[:e].rstrip(set))` with `e = clamp(end, 0, len(v))` |

**Code points and membership.** Positions count code points exactly as `StringLength`, `StringSlice` and `StringIndexOf` do: a surrogate pair is one code point and an unpaired surrogate is its own code point. A code point is a member when it equals any code point of `set`, so `"😀"` in the set matches the pair and never either half, and a lone surrogate in the set matches only that lone surrogate. The set is unordered and duplicates are harmless.

**Clamped bounds.** `from` and `end` are clamped to `[0, StringLength(value)]` like `StringSlice` bounds: negative is 0, not counted from the end, and a bound past the end is the length.

**Empty set.** An empty set has no members, so `StringSpan` returns the clamped `from` and `StringSpanBack` returns the clamped `end`. This matches Python, where `lstrip("")` and `rstrip("")` strip nothing (unlike `lstrip()` with no argument, which strips whitespace).

**Strip is one expression.** Python `v.strip(chars)` is `StringSlice(v, StringSpan(v, chars, 0), StringSpanBack(v, chars, StringLength(v)))`. When the whole text is in the set, the span is `StringLength(v)` and the back span is 0, and `StringSlice` gives `""` for `end <= start`, as Python does.

## Reason

**Positions rather than a `StringTrim` returning a String.** Of probe 5's uses, three (`skipIn`, `rskipIn` and `digitsEnd`) need the position where a run ends, to slice or keep scanning from there; only `strip` wants the trimmed text, and it is one expression over the two positions. A `StringTrim(text, set)` would cover `strip` alone and leave the three span functions hand-written, while the two span builtins cover all four. Keeping both directions also gives `rstrip`/`lstrip` without a third node.

**A String as the set.** Character classes in the port are short literal sets (whitespace, digits, `-*`, a backtick), which read naturally as a String literal and match Python's `strip(chars)` argument. A dedicated set type or ranges would add a type and syntax for no current use.

**Never fail, like the other String builtins.** Clamping the bound instead of rejecting it keeps the result total, as `StringSlice`, `StringIndexOf` and `ListSlice` already do.

## Consequences

- The two nodes follow the usual path through the semantic graph, validation (`StringIntrinsicTypeMismatch`), snapshot and Nyason codecs (`stringSpan` / `string-span` with `value`, `set`, `from`; `stringSpanBack` / `string-span-back` with `value`, `set`, `end`), typed semantic IR, execution IR, the C# backend, the viewer ("string span", "string span back"), the AuthoringSession wire recipe and the agent recipe reference. Recipe diagnostics name the operands `body.value`, `body.set`, `body.from` and `body.end`.
- The C# backend emits `NyText.Span(text, set, from)` and `NyText.SpanBack(text, set, end)`. Each walks to the clamped bound once, builds a hash set of the set's code points, and scans forward, or backward without splitting a pair, so both are linear in the text plus the set.
- The `runPure` parity table in `tests/Nyrwvy.Core.Tests/StringSpanTests.fs` was generated with Python and covers empty text and set, out-of-range bounds, emoji and lone surrogates, whitespace including U+3000, and digits; 50,000 spaces before `x` run in one pass.

## Revisit conditions

- **Character ranges or classes:** a consumer needs a large class (all Unicode whitespace, letters) that a literal set cannot list reasonably.
- **Negated spans:** a consumer needs "run of code points not in the set" often enough that `StringIndexOf` per member is awkward.
- **`StringTrim` convenience:** strip turns out to dominate real uses and the three-node expression becomes friction.
