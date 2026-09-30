# rrawrlet slice probe 12 — 2026-10-01

**Status:** Complete. A new part of rrawrlet was authored by name only: the material and cache-key path of `condense.py`. That is `_numbered`, `gather`, `check_words` and `_check_request`, with `cache_key` (`json.dumps(sort_keys=True, ensure_ascii=False)` and SHA-256 of the UTF-8 bytes), and `_cited_files`, which runs `CITATION.findall`, resolves each cited path and hashes the file. The port matched the real Python on **304/304 cases on its first run**. There were no recipe rejections and no port bugs, and there are no known divergences. **Nothing is blocked.** One gap is queued as a candidate (order 30), because it is clearly this slice's largest cost: there are no bit operations, so SHA-256 was written in Int64 arithmetic.

The hash was exact but slow. Each 64-byte block costs about 0.25 ms, where Python's `hashlib` takes about 0.04 µs. The largest material rrawrlet accepts (256 KiB) runs `_check_request` in 1.4–1.6 s wall time, well within the 5 s limit. A cited file above about 1.5 MB would exceed it. The rest of the port is now close to free at these sizes. Numbering, JSON escaping and the citation scan each run 0.25–2.1 MB in 0.1–0.3 s beyond the build. This is because of the linear List builder and the String offset map (PRs #265–#270). A Unicode table that is parsed once per call and searched by binary search removes probe 11's per-lookup cost without a language change.

**Date:** 2026-10-01

Baseline: nyrwvy `68f98d2` (main after #270, clean); rrawrlet `5a46504` (branch `claude/project-thread-jhvo4h`). The session server was built with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release`. The oracle ran rrawrlet's real functions with rrawrlet's own virtual environment, Python 3.14.3 (earlier probes used 3.11). The `\w` and `\d` tables were computed from that interpreter.

The method was probe 10's and probe 11's: the `Nyrwvy.AuthoringSession` JSON-lines protocol with name-resolved requests (`simulateCreateTypesByName`, `simulateCreateEffect`, `simulateCreateCapabilityByName`, `simulateCreateFunctionByName`, `commit` and `runPureByName`) and no `declarations` call, and `runPure` batches of up to 40 cases through a `runCases` entry. Scripts, cases and transcripts were kept outside the repository in a scratch directory (`probe12/`) and are not committed.

## Why these parts

No probe had touched `condense.py`, and this path exercises what probes 4 to 11 left out:

- A cryptographic digest: `hashlib.sha256(...).hexdigest()` of a key and of every cited file. The language has no bit operations ("first-class exact bit manipulation" is outside the [current subset](../reference/language-subset.md#explicitly-outside-the-current-subset)).
- `json.dumps` with `sort_keys=True`, so dict keys are ordered by code point.
- `str.splitlines()` with Python's full set of line breaks, where probe 11 used universal newlines. Also `f"{n:>{width}}"` right alignment.
- A regular expression `findall` with a group, Unicode `\w` in a character class and Unicode `\d`, then `sorted(set(...))`.
- `pathlib`: `Path(raw)` joined to the repository, `.resolve()`, `.is_relative_to()` and `.relative_to().as_posix()`.
- `str.encode()` raising `UnicodeEncodeError` inside a larger function, and a validation loop over paths that raises at the first bad one. This is an **effectful** `ListEffectFold` whose folder returns `Result`, the short-circuit form added after probe 11.
- Material up to rrawrlet's 256 KiB limit, which makes it a natural scale test for the performance work in #265–#270.

`records.py`, `run_text.py`, `model_preferences.py` and `instructions.py` were read and not chosen. Their pure logic is dict validation and f-string formatting that probes 9–11 already covered. The one new feature there, `round(x, 2)` in `normalize_summary`, needs exact decimal rounding of a Float64, which is not expressible (no Float64 to Int64 conversion). That is a smaller and less common consumer than the digest.

## What was ported

| Python | Port |
| --- | --- |
| `Path.is_file()`, `Path.read_bytes()` | Capability `Fs` with `isFile(path): Bool` and `readBytes(path): String`. The oracle wrapped the real `pathlib` methods over a real directory of 25 files, so the port had to make the same calls, in the same order |
| `Path(raw)`, `repo / path`, `.resolve()` | `resolvePath`: an absolute `raw` replaces the repository, then `StringSplit` on `/` and a `normStep` fold drop empty and `.` parts and pop on `..` |
| `.is_relative_to(repo)`, `.relative_to(repo).as_posix()` | `relTo`: equal to the repository gives `.`, a `repo + "/"` prefix gives the rest, and anything else is `None` |
| `_numbered` | `StringSplitLines` with the ten `str.splitlines` breaks, `[""]` for empty text, a `numRow` fold that appends `pad(str(n), width) + "| " + line` to a List, then one `StringJoin` |
| `gather` | An effectful `ListEffectFold` over `paths` whose folder `gatherStep` returns `Result<Gather, String>`, so the first error stops the loop and no later path is resolved or read. Then `addText` (`if text:`, `len(text.encode())`) and `finishGather` (no material, too large) |
| `check_words`, `question.strip()` | `Int64LessThan`, and `StringSpan`/`StringSpanBack` over Python's 29 `str.isspace` code points |
| `INSTRUCTIONS.format(words=...)` | The two literal halves around `Int64ToString(words)` |
| `str.encode()` raising | `encodeError` finds the first run of lone surrogates and words it as Python does (`character '\ud800' in position 1`, or `characters in position 3-4`) |
| `json.dumps([prompt, instructions, order], ensure_ascii=False, sort_keys=True)` | `jstr` escapes `"`, `\` and C0 controls (`\n`, `\r`, `\t`, `\b`, `\f`, else `\u00XX`) through a fold into a List. `choiceJson` sorts each dict's pairs with `ListSortBy` and `StringLessThan`. A `Scalar` union holds `str`, `None` and `bool` values, because `model_order` only produces those |
| `hashlib.sha256(data).hexdigest()` | `sha256hex`, streaming: a fold over the code points feeds 1–4 UTF-8 bytes each into a `Sha` state (hash words, current word, pending words, byte count). Every 16th word runs `compress`. Padding and the bit length follow. See below |
| `CITATION.findall(brief)` | A 4-state fold (`scanStep`): idle, in a run of `[\w./-]`, after `:`, in the digits. A digit after `:` emits `StringSlice(brief, start, colon)`. After a match, scanning resumes after the digits, as `findall` does, so `a:12b:3` yields `a` and `b`. `\w` and `\d` outside ASCII come from boundary tables (see below) |
| `sorted(set(...))` | `ListSortBy` with `StringLessThan`, then a fold that drops a name equal (`ValueEqual` on `Option<String>`) to the last kept one |
| `_cited_files` | An effectful fold over the names. `is_file` runs only for a relative path and `read_bytes` only for a file, as with Python's `and`. A second name that resolves to the same file is read again, and its row keeps its first position, as the dict assignment does |

Out of scope: `~` in `expanduser`, symlinks (`resolve()` follows them, and the port is lexical), byte decoding with `errors="replace"` (the host returns text, so binary detection tests for U+0000), and a `question` or `words` of the wrong Python type (the port is typed). Cases use absolute repository paths without symlinks.

## SHA-256 in Int64 arithmetic

Every 32-bit word is an Int64 in `[0, 2^32)`. Addition is `Int64WrappingAdd` followed by `Int64FloorModulo(_, 2^32)`. A right shift is `Int64FloorDivide` by a power of two, and a rotation is `x // 2^n + (x % 2^n) * 2^(32-n)`. XOR, AND and NOT have no arithmetic form, so the three bitwise functions of SHA-256 (the XOR of three words, majority and choose) share one self-recursive function. `bits3(a, b, c, mode, k, pw, acc)` adds the low bits of `a`, `b` and `c`, derives the result bit (`s % 2`, `s // 2`, or `c + a * (b - c)`), and recurses on the halves 32 times. There are 352 such calls per block (64 rounds × 4 and 48 schedule words × 2).

The 64 round constants are a 65-node `kList()` list. The schedule and the rounds are folds over slices of that list, used only for their length. The round state is a 9-field `Rnd` product. The hash matched `hashlib` on 40 direct cases and on every key and file digest in the other groups: lengths 0–1,000 around the 55/56/64-byte padding boundaries, 2-, 3- and 4-byte UTF-8, U+FFFF, U+10000, U+10FFFF, and 10 random strings.

## Unicode tables, parsed once

`\w` and `\d` are 1,542 and 142 boundaries, written as decimal text in two String literals (8,844 and 805 characters). Probe 11 counted boundaries by splitting the literal on every lookup. Here `citeNames` first checks `StringUtf8ByteCount(brief) == StringLength(brief)`: an all-ASCII brief never touches the tables, because ASCII is classified by ranges. Otherwise it parses both tables once into `List<Int64>` and passes them to the scan fold as callback context. `inTab` is then a binary search (`bsearch`, recursion depth about 11) and an odd/even test. The table machinery (`wordTable`, `digitTable`, `toInt`, `parseTable`, `bsearch`, `inTab`) is 6 functions and 50 nodes.

## What was authored

| Part | Functions / nodes |
| --- | --- |
| Types | 12 in one group request: `Scalar`, `Pair`, `Choice`, `Case`, `Gather`, `NumAcc`, `Sha`, `Rnd`, `Sur`, `Scan`, `Tabs`, `CiteAcc`; 1 effect, 1 capability with 2 operations |
| Helpers (`at`, hex digits, String comparison callbacks) | 5 / 43 |
| `_numbered` and `strip` | 4 / 82 |
| `UnicodeEncodeError` | 2 / 108 (`encodeError` 58, `surStep` 50) |
| SHA-256 | 17 / 622: bit emulation 7 / 138 (`bits3` 67), constants 1 / 65, schedule and rounds 3 / 205 (`compress` 104), byte feeding, padding and hex 6 / 214 (`feedCp` 84) |
| `json.dumps` and `cache_key` | 8 / 126 (`escStep` 52) |
| Paths | 3 / 64 |
| `gather` and `_check_request` | 6 / 200 (`checkRequest` 59, `gatherStep` 56) |
| `_cited_files` with the tables | 14 / 305 (`scanStep` 57) |
| Case dispatch, including the lone-surrogate escape | 5 / 114 (`runCase` 57) |
| **Total** | **64 / 1,664** in 134 requests (67 simulations, 67 commits) |

There were **no recipe rejections**, no `scopeDepth` or type errors, no cycles needing a placeholder, and the differential run found **no port bugs**. The authoring script itself failed twice before the clean run, both times in Python: an unbalanced parenthesis, and a `Rnd` field named `t` that clashed with a helper's keyword argument. The second failure came after some functions were committed, so the snapshot was recreated and the 134 requests above are the clean run. No code was reused from earlier probes, because their scripts were not available. The port needed no JSON parser: the cache key's `order` is typed input.

**Lone surrogates as run values.** A `runPure` String argument must be well-formed UTF-16, so Python strings holding lone surrogates cannot be passed directly. Probe 11 carried them as JSON escapes inside transcript lines. Here the harness sends each one as U+10FFFE followed by four hex digits, and the dispatch's `unesc` restores it with `StringFromCodePoint`. That is 3 functions and 53 nodes of test plumbing.

## Results vs Python

304 cases. A case matches when its output line is equal and the `Fs` calls are the same list: the same operations on the same paths, in order.

| Group | What it covers | Result |
| --- | --- | --- |
| `_numbered` (47) | Every `splitlines` break, `\r\n` as one break, `\r\r\n`, `\n\r`, trailing and only breaks, `\x1f` and tab (not breaks), 9/10/99/100/1,000 lines for the width, empty text, a label with quotes, astral characters, 20 random texts over the breaks | 47/47 |
| SHA-256 (40) | ASCII lengths 0–1,000 around the padding boundaries, 2-, 3- and 4-byte characters at block edges, U+0000, U+007F, U+07FF, U+0800, U+FFFF, U+10000, U+10FFFF, BOM, 10 random strings. `UnicodeEncodeError` for one lone surrogate, a run of two or three, two separate runs (the first is reported), and a surrogate after a 2-byte character | 40/40 |
| `cache_key` (43) | Empty and several orders, `None`, `True` and `False` values, an empty dict, all 32 C0 controls, `"`, `\`, `/`, DEL, C1 controls, U+2028, BOM, astral. Key order `""`, `B`, `_`, `a`, `b`, `é`, U+FFFF, U+10000 (code point order), and escapes inside keys. Surrogates in the prompt and instructions, and 30 random dumps | 43/43 |
| `_check_request` (92) | 46 path lists: `./`, `..` inside and out, the absolute path, a leading `//`, `/etc/hostname`, `../../../..` above the root and back, trailing `/`, `//` inside, the repository itself, `.`, `""`, a directory, a missing file, a binary file, the first error in a list (later paths are not touched), a quote, a space or CJK in the name, CRLF, BOM, U+2028, empty and newline-only files, 1,000 lines. Text: `None`, `""`, multibyte, lone surrogates (and which error wins over a path error). Questions that `strip()` to empty (U+3000, U+2028, `\x1c`, NEL, NBSP), ZWSP (kept). `words` 19/20/0/−5. `max_bytes` 0–100,000 around the exact size, with 2-byte text. No material. Orders with escapes | 92/92 |
| `_cited_files` (82) | Plain, repeated, `./` and `..` spellings of one file; missing files, directories, `.`, `..`, absolute paths inside and outside; `a:b:12`, `x.py:12y.py:3`, `a.py::1`, `a.py:12:34`; full-width, Arabic-Indic, NKo and mathematical digits (`\d`), superscripts (`\w` but not `\d`), combining marks and ZWSP (neither); `é`, CJK and mathematical letters; punctuation around citations; 40 random briefs | 82/82 |
| **Total** | | **304/304** |

There are no known divergences. Python ints beyond Int64, the cause of every known divergence in probes 9–11, cannot occur here: `words` and `max_bytes` are typed, and material sizes are small. All 304 cases took 8 runs and 11.8 s (3.55, 1.79, 1.58, 1.42, 1.11, 0.78, 0.83 and 0.74 s).

## Scale

The text was generated inside the program by doubling a one-line chunk (`rep`, recursion depth about 2 log₂ n), because an argument holds at most 65,536 UTF-16 units. The ASCII line is 52 bytes. The mixed line is 44 code points and 54 bytes of CJK, Latin-1 and ASCII. Times are `runPure` wall time, measured after the session's first run. That first run took 1.9–2.3 s whatever the entry, which is the Roslyn warm-up. Python times are the best of five runs of the real functions. Every row gave the same result as Python.

| Input | Python | Port |
| --- | --- | --- |
| Trivial entry: `StringLength` of the generated text, 52 B to 2.1 MB | — | 0.36–0.50 s |
| `_check_request`, text of 1 line (52 B) | 0.02 ms | 0.41–0.45 s |
| `_check_request`, 250 KB ASCII (4,800 lines) | 3.4 ms | 1.42–1.55 s |
| `_check_request`, 259 KB mixed (4,800 lines) | 4.8 ms | 1.45–1.57 s |
| SHA-256 only: 54 B / 259 KB mixed | 0.001 / 0.39 ms | 0.35–0.38 / 1.18–1.24 s |
| SHA-256 only: 524 KB / 1.05 MB / 2.1 MB ASCII | 0.31 / 0.60 / 1.2 ms | 2.45 / 3.84 s / `RunTimeout` |
| `_numbered` only: 262 KB / 2.1 MB | 2.0 / 20 ms | 0.29–0.31 / 0.25 s |
| `json.dumps` only: 262 KB | 0.95 ms | 0.38 s |
| `_cited_files`: 250 KB ASCII / 259 KB mixed / 2.1 MB ASCII | 8.2 / 7.1 / 71 ms | 0.47 / 0.48 / 0.70 s |
| `\w` lookups, tables parsed once, binary search: 1,001 / 64,001 / 256,004 | 0.14 / 8.8 / 36 ms | 0.51 / 0.55 / 0.70 s |
| `\w` lookups, table split per lookup (probe 11's form): 1,001 / 16,002 / 64,001 / 256,004 | same | 0.78 / 1.49 / 3.68 s / `RunTimeout` |
| 1.4 M bitwise calls (`op3`, the count for 4,000 blocks) | 0.12 s (Python `^`) | 0.87–0.90 s |

The fixed cost of a run is now about 0.35–0.5 s for a small entry. The full case dispatcher, with the whole port in its closure, costs about 0.7–0.8 s. Above that, only the digest is visible. A 256 KiB material spends about 1.0 s hashing (about 0.25 ms per block, half of it in `bits3`) and under 0.1 s numbering, escaping and checking for surrogates. *Inference:* the hash grows linearly at about 3.3 s of run time per MB, so any one file above about 1.5 MB (`_cited_files` hashes whole files, with no size limit) exceeds the 5 s run limit. The material itself is capped at 256 KiB by default (`max_bytes`).

Against the performance focus of #265–#270:

- **Constant factors.** They are the bottleneck only where the language lacks the operation: the digest runs about 7,000 times slower than `hashlib`. Folds over code points are otherwise fast enough: 2.1 MB is scanned for citations in 0.3 s.
- **`StringConcat` by character.** It was not used. Every builder appends to a List and joins once, and 2.1 MB numbered or 262 KB escaped cost no measurable time.
- **Re-sliced tails.** They were not used. The citation scan slices one brief by position, which the offset map makes constant time per slice.
- **Constant tables re-parsed per call.** This costs time only when the table is parsed per lookup: about 50 µs each, and 256,004 lookups time out. Parsed once per call and passed as fold context, the same lookups take 0.25 s. The 64-item constant list rebuilt for every block (4,000 times for 256 KiB) is not visible beside the bit emulation. **Evaluating constants once is not triggered**, because the program-level fix is one Bind or callback context.
- **Per-call build time.** It is 0.35–0.8 s per run, plus 1.9–2.3 s once per session. It is the largest part of every small run and of the 8 case batches, which built one program 8 times with different arguments. It never limited a run. **A build cache keyed by fingerprint** would save most of those 11.8 s, but that is convenience, not reach, so it is recorded here and not queued.
- **Map and StringFold.** They are not triggered. The cited-files dict is a key list with `ListAny`, and the set is a sort and dedupe. `StringCodePoints` over 2.1 MB (a List of about 2 M one-character Strings) ran in well under a second.

## Costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **No bit operations** | SHA-256 in Int64 arithmetic: 7 functions and 138 nodes emulate XOR, AND and choose with a 32-level recursion, which is half of the digest's run time (1.4 M calls ≈ 0.5 s). The digest is about 75% of the largest real run (1.0 s of 1.5 s), about 7,000 times `hashlib`, and caps a hashed file at about 1.5 MB per run. The whole SHA part is 622 nodes, 37% of the port | **Queued as candidate order 30.** A `Bits32` domain as the [bit-representation experiment](../experiments/bit-representation.md) proposes, with only what SHA-256 needs: conversion from Int64 (low 32 bits) and to Int64 (unsigned), `Xor`, `And`, `Not`, `RotateRight` and `ShiftRight`, and wrapping `Add`. It needs a decision first. A digest built-in would be smaller but serves one algorithm |
| 2 | **Build on every run** | 0.35–0.8 s per `runPure`, 1.9–2.3 s for the first run of a session. It dominates small runs and the case batches (8 builds of one program, 11.8 s) | None queued. It is the deferred "arguments as input with a build cache keyed by fingerprint" |
| 3 | **Lone surrogates are not run values** | Python strings with lone surrogates needed a sentinel escape in the harness and `unesc` in the dispatch: 3 functions, 53 nodes | None. This is test plumbing, and a program can still make such strings with `StringFromCodePoint` |
| 4 | **Products restated in full** | The 9-field `Rnd` round state, the 4-field `Sha` state in three constructions in `feed`, and the 5-field `Scan` | None, as after probes 7 and 10 |
| 5 | **Unicode properties from tables** | Exact `\w` and `\d` from two literals in 6 functions and 50 nodes. With a parse once per call they are cheap, and the per-lookup form times out at 256K lookups | None. This settles probe 11's cost #2 without a language change |

Python's exceptions cost less than in probe 11. The one validation loop that raises (`gather`) is a single effectful fold whose folder returns `Result` and stops at the first `Err`. The calls prove it: `["a.py", "bin.dat", "missing.py"]` reads `bin.dat` and never checks `missing.py`. Neither Float64 nor recursion deeper than 32 levels was needed. Function groups were not needed either, since there were no cycles.
