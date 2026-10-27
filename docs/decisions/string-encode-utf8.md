# UTF-8 encoding

- **Status:** Accepted
- **Amended:** 2026-10-27 by [String as a sequence of Unicode scalar values](string-scalar-values.md): Strings no longer hold unpaired surrogates, so the rules for them below no longer apply.
- **Date:** 2026-10-18
- **Roadmap:** [post-M2 owner choice of 2026-10-16](../plans/post-m2-roadmap.md#owner-choice-of-2026-10-16-ordinary-builtins), order 67

The [UTF-8 decoding decision](utf8-decode.md) gave the language `StringDecodeUtf8Lossy` over bytes held one per code point (U+0000..U+00FF, the latin-1 form in which probes carry bytes) and named encoding, "producing UTF-8 bytes from text in the program, beyond counting them with `StringUtf8ByteCount`", as a revisit condition. A program that reads bytes with the decoder has no way to write them back: a port that frames, hashes or measures text as bytes could count them but not produce them. The owner chose on 2026-10-18 to add the inverse under the [warranted-operation rule](../plans/post-m2-roadmap.md#selection-rule-from-here).

## Decision

**`StringEncodeUtf8(text): String`** is the UTF-8 encoding of `text` as a String of one byte per code point (U+0000..U+00FF), the form `StringDecodeUtf8Lossy` reads, so `StringDecodeUtf8Lossy(StringEncodeUtf8(text))` is `text` for every well-formed `text`. It equals Python `text.encode("utf-8")` wherever Python does not raise. It never fails and is linear.

**An unpaired surrogate encodes as U+FFFD, `EF BF BD`.** Python's strict `encode("utf-8")` raises on a lone surrogate, which this language's Strings can hold ([decision](../decisions/utf8-decode.md) and `StringFromCodePoint`); Python's `errors="replace"` writes one `?` byte, and WTF-8 writes the surrogate's own three bytes `ED A0 80`..`ED BF BF`. `StringUtf8ByteCount` already counts an unpaired surrogate as the 3 bytes of U+FFFD ([decision](string-lowercase-and-utf8-length.md)), so U+FFFD is the choice under which `StringLength(StringEncodeUtf8(text))` equals `StringUtf8ByteCount(text)` for every String. It is also the choice consistent with the decoder: decoding WTF-8's `ED A0 80` lossily gives three U+FFFD (each byte is its own maximal ill-formed subpart), whereas decoding `EF BF BD` gives the one U+FFFD that round-trips. Both builtins and the count use .NET's `Encoding.UTF8`, whose replacement fallback does exactly this, so the three agree by construction.

The name has no `Lossy`, unlike the decoder's, because nothing is lost on any well-formed String: only a lone surrogate, which is not a Unicode scalar value, is replaced, as `StringUtf8ByteCount` already treats it.

## Consequences

- It goes through the semantic graph, validation (`StringIntrinsicTypeMismatch`), both codecs (`stringEncodeUtf8`, `string-encode-utf8`), typed semantic IR, Execution IR, the C# backend (`NyText.EncodeUtf8`: `Encoding.UTF8.GetBytes` widened one char per byte), the viewer (role `UTF-8 encode`), wire recipes and the recipe reference. A snapshot that uses it is written at snapshot JSON 19 / Nyason `nayamnyam.snapshot.v15`; see [snapshot schema versions](../specs/schema-evolution-policy.md#snapshot-schema-versions).
- `StringEncodeUtf8Tests` checks a Python-parity table under `runPure`, that decoding the encoding gives the input back (U+FFFD for lone surrogates), and that the encoded length equals `StringUtf8ByteCount` on every case.
- The encoding revisit condition of the [UTF-8 decoding decision](utf8-decode.md) is settled; its others (strict decoding, other error modes, a bytes type) stand.
