# String split lines

- **Status:** Accepted
- **Date:** 2026-09-30
- **Roadmap:** [candidates from the 2026-09-30 probe 5](../plans/post-m2-roadmap.md#candidates-from-the-2026-09-30-probe-5), order 24

This decision settles order 24 of the post-M2 queue. [Probe 5](../verification/rrawrlet-slice-probe5-20260930.md) ranked line splitting as its second remaining cost (#2): `str.splitlines` (10 break characters, `\r\n` as one break) and `bytes.splitlines` (`\n`, `\r`, `\r\n`) were a `SplitAcc` accumulator type and the `splitStep` and `splitLines` functions, 1 type, 2 functions and 55 nodes of `ListFold` over `StringCodePoints`, because `StringSplit` takes one separator.

## Decision

One pure builtin, `StringSplitLines(value, breaks): List<String>`, over two Strings. It runs in linear time and never fails.

**Breaks are a code-point set.** A code point of `value` is a break when it equals any code point of `breaks`, with membership exactly as in [`StringSpan`](string-spans.md): a surrogate pair is one code point, so `"😀"` in `breaks` matches the pair and never either half, and an unpaired surrogate matches only itself. The set is unordered and duplicates are harmless.

**Lines.** Scanning left to right, each break ends the current line and is not kept. When both `"\r"` and `"\n"` are members, a `"\r"` directly followed by `"\n"` is a single break. As in Python `splitlines()`, an empty `value` gives `[]`, and a break at the very end adds no trailing empty line: `"a\n"` gives `["a"]`, `"a\n\nb"` gives `["a", "", "b"]` and `"\n"` gives `[""]`. An empty `breaks` has no members, so a non-empty `value` gives `[value]` and `""` gives `[]`.

The reference, over code points, is:

```python
def split_lines(v, breaks):
    out, cur, i = [], [], 0
    crlf = "\r" in breaks and "\n" in breaks
    while i < len(v):
        c = v[i]
        if c in breaks:
            out.append("".join(cur)); cur = []
            if crlf and c == "\r" and i + 1 < len(v) and v[i+1] == "\n": i += 1
        else: cur.append(c)
        i += 1
    if cur: out.append("".join(cur))
    return out
```

| `breaks` | Equals |
| --- | --- |
| `"\n\r\x0b\x0c\x1c\x1d\x1e\x85  "` | `value.splitlines()` |
| `"\n\r"` | `[b.decode() for b in value.encode().splitlines()]`, for text without unpaired surrogates |

## Reason

**An explicit break set rather than Python's fixed `str.splitlines` set.** The port needs both forms: rrawrlet's `repo_map` splits with `bytes.splitlines`, which breaks only at `\n`, `\r` and `\r\n`, while `review.parse_findings` splits with `str.splitlines`, which also breaks at `\v`, `\f`, `\x1c`-`\x1e`, `\x85`, U+2028 and U+2029. A builtin fixed to either set would leave the fold in place for the other, as probe 5 already noted. A `breaks` String covers both, reads as a short literal like `StringSpan`'s set, and also handles one-off separator sets (`",;"`) that `StringSplit` cannot.

**`\r\n` as one break only when both are members.** Treating `\r\n` as one break is what both Python forms do, and it only makes sense when each half would otherwise be a break of its own. When only `"\r"` is a member, a following `"\n"` is ordinary text (`"a\r\nb"` gives `["a", "\nb"]`), and when only `"\n"` is a member, the `"\r"` stays at the end of the line (`["a\r", "b"]`), exactly as a per-code-point split would give. The rule never depends on anything but membership, so there is no mode flag.

**Python's trailing-break rule rather than `StringSplit`'s.** `StringSplit` keeps a trailing empty piece like `str.split(sep)`, but every line-oriented consumer in the port uses `splitlines`, where a final newline terminates the last line instead of starting an empty one. Matching `splitlines` keeps the port's results identical without a trailing `ListSlice`.

**Never fail, like the other String builtins.** Any pair of Strings has a well-defined result, including an empty `breaks`.

## Consequences

- The node follows the usual path through the semantic graph, validation (`StringIntrinsicTypeMismatch`), snapshot and Nyason codecs (`stringSplitLines` / `string-split-lines` with `value` and `breaks`), typed semantic IR, execution IR, the C# backend, the viewer ("string split lines"), the AuthoringSession wire recipe and the agent recipe reference. The `breaks` operand is a `CharacterSet` value dependency like `StringSpan`'s set, and recipe diagnostics name the operands `body.value` and `body.breaks`.
- The C# backend emits `NyText.SplitLines(text, breaks)`, which builds a hash set of the break code points once, walks the text one code point at a time, and collects the lines into a doubling array wrapped as `NyList<string>`, so it is linear in the text plus the set.
- The `runPure` parity table in `tests/Nyrwvy.Core.Tests/StringSplitLinesTests.fs` was generated with Python (`str.splitlines`, `bytes.splitlines` and the reference above) and covers empty value and breaks, trailing and consecutive breaks, lone `\r`, `\r\n` and `\n\r`, a set with `\r` but not `\n`, every character of the `str.splitlines` set, emoji and lone surrogates; 20,000 lines near the 65,536-unit argument cap split in one pass.

## Revisit conditions

- **Keeping line ends:** a consumer needs `splitlines(keepends=True)`, for example to reproduce exact byte offsets.
- **Multi-character breaks beyond `\r\n`:** a consumer needs another two-character sequence treated as one break.
- **A named Python set:** the `str.splitlines` literal turns out to be written often enough that a constant or a one-argument form pays for itself.
