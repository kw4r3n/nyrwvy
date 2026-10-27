# String as a sequence of Unicode scalar values

- **Status:** Accepted
- **Date:** 2026-10-27
- **Plan:** [backend-neutral semantics and generated library bindings](../plans/multi-backend-and-bindings.md#4-string-is-a-sequence-of-unicode-scalar-values), §4

A String was any sequence of UTF-16 code units: it could hold an unpaired surrogate, and concatenating a high and a low surrogate formed a pair at the seam. That is the .NET and JavaScript string, but not the string of Zig, C, Rust or Swift, which hold UTF-8 or scalar values. Keeping it would make a Zig backend store UTF-16 and convert at every C or Zig library call, and would keep a rule (seam pairing) that no code-point language has.

## Decision

**A String is a sequence of Unicode scalar values**, the code points U+0000..U+10FFFF other than the surrogates U+D800..U+DFFF. Every operation already counts positions in code points, so only the places where an unpaired surrogate could appear change:

| Entry | Behaviour |
| --- | --- |
| String literal in a snapshot or record | Validation issue `StringLiteralUnpairedSurrogate` |
| String in the authoring protocol (a literal, a `runPure` argument, a fake response) | Rejected: the request fails with "A String must not contain an unpaired surrogate escape" |
| `StringFromCodePoint(code)` | `None` for 0xD800..0xDFFF, as for a negative code or one above 0x10FFFF |
| A .NET string reaching the program (a host capability's result or argument) | Each unpaired surrogate becomes U+FFFD, at the one conversion from `string` to `NyStr` |

Other producers already yield scalar values: slicing, splitting, search and replacement cut on code-point boundaries, concatenation of scalar sequences is one, `StringDecodeUtf8Lossy` emits U+FFFD for an encoded surrogate, and `BytesToLatin1` gives U+0000..U+00FF. A JSON decoder written in Nyr combines a `😀` escape pair into one code point itself (`0x10000 + (high - 0xD800) * 0x400 + (low - 0xDC00)`), since `StringFromCodePoint` no longer accepts the halves.

Run values and snapshots are rejected rather than repaired, so a test input never changes silently. No migration is needed: no tracked snapshot, saved case or fixture, nor snap's workspace, contains an unpaired surrogate escape (checked on 2026-10-27).

## Reason

Every backend can keep its natural representation: C# and JavaScript use native strings, which are then always well-formed UTF-16, and Zig uses UTF-8 that passes to C and Zig libraries without conversion. Positions in code points need an index in every representation, which the C# runtime already keeps for UTF-16. Python allows lone surrogates in `str`; Nyr's String already differed from it (seam pairing), and now differs only in rejecting them.

## Consequences

- The C# runtime scans each string converted from .NET for surrogate code units (vectorized, `IndexOfAnyInRange`) and walks it only when it finds some.
- `ExceptionMessage` and other host texts are sanitized like any host string.
- Branches for unpaired surrogates in the runtime (case mapping, ordering, code-point walking) are now unreachable and can be removed when that code is next touched.

## Revisit conditions

Revisit if a consumer must round-trip text that holds unpaired surrogates (for example arbitrary Windows file names or JavaScript strings) without loss; that would need a separate type, not a change to String.

## Supersedes

The unpaired-surrogate rules in [String code point conversion](string-code-point-conversion.md), [String code points](string-code-points.md), [String lowercase and UTF-8 length](string-lowercase-and-utf8-length.md), [String replace and trim](string-replace-and-trim.md), [UTF-8 encoding](string-encode-utf8.md), [String split lines](string-split-lines.md) and [list sort and string ordering](list-sort-and-string-ordering.md), and the String run values of the [agent authoring session](../specs/agent-authoring-session.md).
