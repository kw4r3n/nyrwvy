# String uppercase

- **Status:** Accepted
- **Date:** 2026-10-03
- **Roadmap:** [post-M2 owner choice of 2026-10-03](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-03), order 42

`StringToUpper` was left out of the [standard operations with an owner choice](standard-operation-choices.md) because Python's `upper()` changes length (`ß` to `SS`) and no consumer had needed it. On 2026-10-03 the owner chose to add operations a language would naturally have without waiting for a consumer, and to prefer what one would ordinarily expect over Python's behaviour. The owner also pointed out that `ß` has an uppercase form, CAPITAL SHARP S `ẞ` (U+1E9E).

## Decision

`StringToUpper(text): String` replaces each code point with its Unicode simple (one-to-one) uppercase mapping, culture invariant, the mirror of [`StringToLower`](string-lowercase-and-utf8-length.md):

- `i` becomes `I` (never the Turkish `İ`), DOTLESS I `ı` becomes `I`, LONG S `ſ` becomes `S`, `ǆ` becomes `Ǆ` (uppercase, not titlecase), and Greek with iota subscript keeps one code point (`ᾳ` to `ᾼ`).
- `ß` (U+00DF) becomes CAPITAL SHARP S `ẞ` (U+1E9E). UnicodeData gives `ß` no simple uppercase mapping. Mapping it to `ẞ` keeps the one-to-one rule and makes `StringToLower` its inverse (`ẞ` lowercases to `ß`).
- Code points with no simple mapping (`ŉ`, unpaired surrogates) are kept, so the code-point length never changes.
- Wire `stringToUpper` (field `value`), Nyason `string-to-upper`. Linear time, never fails.

Where it differs from Python `upper()`: `ß` gives `ẞ`, not `SS`; `ŉ` stays `ŉ`, not `ʼN`; `ᾳ` gives `ᾼ`, not `ΑΙ`. Python's full mapping changes the length, which a per-code-point operation should not.

The C# backend maps with `Rune.ToUpperInvariant` and `char.ToUpperInvariant`. A check of every code point against Python's Unicode 16 data found .NET's invariant casing equal to the simple mapping except U+0131, which .NET keeps, and characters added in later Unicode versions. `NyText.ToUpper` maps U+0131 to `I` and `ß` to `ẞ` explicitly.

## Revisit conditions

- A consumer needs the full mapping (`SS`), for example to compare text case-insensitively; that is a separate case-folding operation rather than a change here.
