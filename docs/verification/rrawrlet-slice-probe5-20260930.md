# rrawrlet slice probe 5 — 2026-09-30

**Status:** Complete. Probe 4's whole slice (`review.parse_findings` and the pure core of `repo_map`) was re-authored with the order 17-22 builtins, by name only. It matched Python on 24/24 in-scope checks, including the two cases that overflowed the stack in probe 4's natural version (H3 and R1) and the Kelvin-sign case MX1, which probe 4 could not reach. It needs no depth workaround. The port shrank from 14 types, 47 functions and 1106 nodes to 12, 38 and 897. There was one recipe retry, a wrong `scopeDepth`, which the diagnostic located at once. The remaining costs are listed below; none blocks the slice.

**Date:** 2026-09-30

Baseline: nyrwvy `4f69c9b` (main after #253, clean); rrawrlet `72847cd`. The session server was built once with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release -o <scratchpad>/probe5/bin`. The oracle cases are probe 4's `cases.json`, unchanged, so the expected values are the same real-Python results.

The method was probe 4's: the maintained `Nyrwvy.AuthoringSession` JSON-lines protocol with `simulateCreateTypeByName`, `simulateCreateFunctionByName`, `commit` and `runPureByName`, and no `declarations` call. Probe 4's harness (`nw.py`, `run_cases.py`) was reused, and `author.py`/`author_map.py` were rewritten wherever a new builtin replaces an emulation; everything else was kept, so the difference measures orders 17-22. Scripts and transcripts are kept outside the repository in the session scratchpad `probe5/`.

## What changed in the port

| Probe 4 emulation | Probe 5 | Order |
| --- | --- | --- |
| `splitFrom` (per-character self-recursion), later `explode` + `splitStep` | `ListFold(StringCodePoints(text), …, splitStep)`; `explode` and `splitFrom` are gone | 17 |
| `findPath` stepping one character per call over the lazy `(\S+?):` | `StringIndexOf(text, ":", from)` jumps to each colon, and `ListAny(StringCodePoints(segment), inSet)` checks the new segment for `\s`; recursion is per colon | 17, 18 |
| `afterLast` + slicing for the base name | `StringLastIndexOf(name, "/")` | 18 |
| `endsWith`, the `underPrefix` slice comparison, `isFence`'s two slice comparisons | `StringEndsWith`, `StringStartsWith` | 18 |
| `ciWordAt`, `ciEndsWith`, `wordThen`, and every literal in both cases | `StringToLower` once, then `StringStartsWith`/`StringEndsWith` | 18, 21 |
| `utf8From`/`utf8Len`, later `utf8Add` by code-point thresholds | `StringUtf8ByteCount` | 21 |
| `joinStep`, `joinFirst`, `omitStep`, `JoinAcc`, `OmitAcc` | `StringJoin`, `ListSlice` (as in probe 4's order-22 follow-up) | 22 |

## What was authored

| Part | Probe 4 final (types / functions / nodes) | Probe 5 | Requests / request JSON / reply JSON |
| --- | --- | --- | --- |
| Text helpers | 1 / 11 / 204 (including the workaround's `explode`, `SplitAcc`, `splitStep`) | 1 / 8 / 114 | 18 / 10.7 KB / 13.6 KB |
| `parse_findings` | 2 / 9 / 266 | 2 / 8 / 243 | 20 / 19.4 KB / 14.9 KB |
| `repo_map` core | 11 / 27 / 636 (including the workaround's `utf8Add`; the order-22 follow-up measured 651 → 603 on the natural version) | 9 / 22 / 540 | 62 / 44.9 KB / 45.9 KB |
| **Total** | **14 / 47 / 1106**; 138 requests / 101.9 KB / 109.9 KB | **12 / 38 / 897** (−19%) | **100 / 75.0 KB / 74.4 KB** |

Probe 4's totals include 20 requests for the depth workaround and for deleting the dead recursive helpers; probe 5 needed neither. The text helpers lost the most (204 → 114 nodes), and `findPath` went from a per-character scan to a per-colon one (51 → 41 nodes).

There was **one recipe retry**. In `findPath`, the recursive call in the `None` arm of `MatchOption(tailAt(…))` referred to the colon as `scopeDepth` 0, but the `None` arm is its own frame with no bindings. The rejection said so directly: "Pattern-reference slot Value does not exist in scope depth 0. The slot is found at depth 1. Visible frames, innermost first: depth 0 = None arm of MatchOption (no bindings); depth 1 = Some arm of MatchOption (binds Value). At body.someBody.whenFalse.noneBody.arguments[3].value.left (patternReference)." The fix was one edit. There was also one harness-side Python syntax error, which sent no request.

## Results vs Python

| Group | Result |
| --- | --- |
| `parse_findings` (F1-F11) | **11/11** |
| `_headings` (H1-H3; H3 is rrawrlet's `DESIGN.md`, 30.7K chars) | **3/3**, with no workaround |
| `repo_map` (M1-M8, R1 at 48.8K chars) | **9/9**, with no workaround |
| MX1 `x.marKdown` (Kelvin sign) | **Now matches**, through `StringToLower` |
| FX1 Arabic-Indic digits, FX2 a line number above Int64 | Still differ: the port keeps an ASCII `\d` and an Int64 line (`isDigit`, `stringParseInt64`) |
| FX3 `[HİGH]` | Still differs, by design: `StringToLower` gives `high`, Python gives `hi̇gh` (see the [decision](../decisions/string-lowercase-and-utf8-length.md)) |
| R2, the whole package | Rejected by the `runPure` argument limit, as in probe 4 |

28 runs took 43.6 s, about 1.6 s each, which is mostly the per-run build.

**Scale (`scale_probe.py`):** `parseFindings` on 800 finding lines (61.6K chars) and `headingsOf` on 60K chars both finish in about 1.5-1.7 s. Probe 4's natural version overflowed at 7.7K and 8K chars.

Two adversarial inputs still show the cost of walking code-point positions. Python handles both instantly.

- **A path with thousands of colons** (`- [high] a:a:…:1 - x`). The first `findPath` checked the whole path again at every colon: 4,000 colons took 4.2 s, and 8,000 hit `RunTimeout`. After it was changed to check only the new segment (the version measured above), 4,000 colons take 2.5 s. At 8,000 colons, the per-colon recursion overflows with the new, explanatory message ("Stack overflow: recursion was too deep. Nested self or mutual DirectCall recursion runs on the runner's thread…"). At 16,000 it hits `RunTimeout`. Every step also pays for `StringIndexOf(…, from)` and `StringSlice` walking from the start of the text, so the scan stays O(n²).
- **16,000 spaces before the path** pass in 3.3 s, compared with 1.6 s for a short line. The cause is the same: `skipIn` calls `charAt` once per character, and each call slices from the start.

## Remaining costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **Character-class spans and strip are hand-written self-recursion** | `charAt`, `inSet`, `skipIn`, `rskipIn`, `strip`, `isDigit` and `digitsEnd` are 7 functions and 70 nodes. They are the port's only remaining per-character recursion, so they are also the source of both adversarial slowdowns above. They serve `str.strip()`, `\s+`, `\d+`, `[-*]` and `strip("`")`. | A span primitive over a character set, such as `StringSpan(text, set, from): Int64` and its reverse, or `StringTrim(text, set)` for the strip case. Decide from the uses: `skipIn`, `rskipIn` and `digitsEnd` are spans, `strip` combines two of them, and the other three only support them. |
| 2 | **Line splitting needs a fold with an accumulator type** | `str.splitlines` (10 break characters, `\r\n` as one) and `bytes.splitlines` (`\n`, `\r`, `\r\n`) are `SplitAcc` + `splitStep` + `splitLines`: 1 type, 2 functions and 55 nodes. `StringSplit` takes one separator. | `StringSplitLines(text)` with `str.splitlines` semantics. The `bytes` form would still need the fold, or a separator-set variant. |
| 3 | **Regex tails stay hand-written** | `tailAt` is 92 nodes for `(\d+)\s+[-–—:]\s+(.+?)\s*$`, and `parseLine` is 57. | None: regex stays out of scope. Items 1 and 2 would shorten both. |
| 4 | **Position-walking scans are O(n²) and stack-bound** | See the scale section. It appears only on adversarial input here. | Only document it for now: a loop that indexes by code-point position pays O(position) per step. Revisit if a real input hits it. |
| 5 | **Repeat, padding and early exit** (friction 7's remainder) | `repeat` (12 nodes, self-recursive) and the `stopped` flags in `ShowAcc` and `OtherAcc`. | Unchanged from probe 4, and low. |
| 6 | **Trivial callback wrappers** | `endsWith` 3, `sameText` 3, `sectionName` 2, `fileSelected` 4. | Not queued (unchanged). |

Not queued, unchanged: the verbose run-value encoding, `commit: true` on `simulate*`, and a larger `runPure` value budget.
