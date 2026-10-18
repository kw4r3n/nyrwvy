# UTF-8 decoding with replacement

- **Status:** Accepted
- **Date:** 2026-10-01
- **Roadmap:** [post-M2 queue from the 2026-10-01 owner choice](../plans/post-m2-roadmap.md#queue-from-the-2026-10-01-owner-choice), order 33

This decision settles order 33 of the post-M2 queue. The language has no bytes type. Probes carry bytes as Strings of one code point per byte (U+0000..U+00FF), so that code-point positions are byte offsets and `StringIndexOf`, `StringSlice` and `StringSplitLines` work on bytes unchanged. What that leaves to the program is decoding. [Probe 14](../verification/rrawrlet-slice-probes-summary.md#probe-14) wrote `bytes.decode("utf-8", "replace")` by hand (4 functions, 228 nodes, 13% of the port, and the part easiest to get subtly wrong), [probe 18](../verification/rrawrlet-slice-probes-summary.md#probe-18) needed the same decoder again for Git's output, and [probe 16](../verification/rrawrlet-slice-probes-summary.md#probe-16) wrote strict and `surrogateescape` decoders (429 nodes). The implemented surface is described by the [language subset](../reference/language-subset.md), the [expression authoring specification](../specs/expression-authoring.md#stringdecodeutf8lossy) and the [C# backend](../specs/csharp-backend.md), which remain the authority.

## Decision

**One pure builtin, `StringDecodeUtf8Lossy(bytes): String`.** It reads each code point of `bytes` as one byte and returns what Python's `bytes.decode("utf-8", "replace")` returns for those bytes. Each maximal ill-formed subpart becomes one U+FFFD, the rule Unicode recommends and both Python and .NET follow: a byte that cannot start a sequence (`80`..`C1`, `F5`..`FF`), a second byte outside the range its lead allows (`E0` needs `A0`..`BF`, `ED` needs `80`..`9F`, `F0` needs `90`..`BF`, `F4` needs `80`..`8F`), so overlong forms, encoded surrogates and values above U+10FFFF, and a sequence cut short by another byte or by the end. A BOM is kept, as with Python's `"utf-8"`. It never fails and is linear.

**A code point above U+00FF reads as byte 0xFF.** Such a code point is not a byte. Rather than fail, it reads as the invalid byte 0xFF, so it becomes one U+FFFD and ends a pending sequence exactly as an invalid byte would. A surrogate pair is one code point and gives one U+FFFD.

**Wire forms.** The recipe is `{"kind":"stringDecodeUtf8Lossy","value":<recipe>}`; snapshot JSON writes `stringDecodeUtf8Lossy` and Nyason `string-decode-utf8-lossy`.

**Only the `replace` mode.** Strict decoding and `surrogateescape` were considered and are not added. Only probe 16 needed them. Strict decoding reaches the user through Python's error text (`'utf-8' codec can't decode byte 0xff in position 3: invalid start byte`), so a useful strict form has to return the error's start, end and reason, which is a `Result` shape with its own design. `surrogateescape` produces lone surrogates by design, which the language carries but which a second consumer should justify. Both remain writable by hand, and the [revisit conditions](#revisit-conditions) say when to add them.

## Reason

**It replaces the decoder every byte-handling port rewrote.** The hand-written decoder was 228 nodes; the builtin is one node. Checked through `runPure` against Python on 5,024 inputs (24 edge cases, 2,500 random byte strings and 2,500 mutated or truncated UTF-8 strings), it matched every one, and the non-byte rule matched Python on bytes with 0xFF substituted (`rrawrlet-probe14/order33/dec.py` in private files outside the repository). Decoding 60,000 bytes took 0.43 s of `runPure` wall time, against 0.49 s for probe 18's run of the hand-written decoder on 60,000 random bytes; at this size both are dominated by the run's fixed cost rather than by decoding.

**The latin-1 String stays the bytes representation.** A `Bytes` type would add nothing else these ports needed: offsets, slicing, searching and line splitting already work on the latin-1 form, and probe 14 found no case that diverged on a byte position. The builtin fits that representation instead of replacing it.

**`Lossy` in the name.** The name says what is lost, so a reader of the graph sees that invalid input does not fail. A strict form, if added, gets its own name and type rather than a mode argument.

## Consequences

- The node follows the usual path through the semantic graph, validation (`StringIntrinsicTypeMismatch`), snapshot and Nyason codecs, typed semantic IR, execution IR, the C# backend (`NyText.DecodeUtf8Lossy`, which writes the bytes and calls `Encoding.UTF8.GetString`), the viewer (role `UTF-8 decode (replace)`), both recipe wire forms and the agent recipe reference.
- Probe 14's and probe 18's decoders can be replaced by the builtin; their recorded results are unchanged.
- No schema version changes; snapshots without the node keep their bytes.

## Revisit conditions

- **Strict decoding:** a second consumer needs Python's strict decode, or its error text, from a module that cannot leave decoding to the host.
- **`surrogateescape` or other error modes:** a consumer must round-trip invalid bytes through text, as `os.fsdecode` does.
- **Encoding:** a consumer must produce UTF-8 bytes from text in the program, beyond counting them with `StringUtf8ByteCount`.
- **A bytes type:** a port finds an operation on bytes that the latin-1 String form cannot express or makes quadratic.
