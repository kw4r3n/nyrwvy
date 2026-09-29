# rrawrlet slice probe 4 — 2026-09-29

**Status:** Complete. A new part of rrawrlet, `review.parse_findings` and the pure core of `repo_map`, was authored by name only and matched Python on 23/23 in-scope checks. There were no authoring retries. The first, natural version failed on real input: character-by-character recursion overflowed the stack at about 7.7K characters. A workaround fixed it. New gaps are recorded below.

**Date:** 2026-09-29

Baseline: nyrwvy `c511fa2` (main after #246, clean); rrawrlet `2cc7517` (clean working tree). The session server was built once with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release -o <scratchpad>/probe4/bin`, and the repository tree was left clean. The oracle ran the real rrawrlet functions with rrawrlet's own `.venv` (Python 3.14.3, which includes the tree-sitter grammars). The harness used Python 3.14.7.

The method was the same as probe 3. Only the maintained `Nyrwvy.AuthoringSession` JSON-lines protocol was used, with name-resolved requests (`simulateCreateTypeByName`, `simulateCreateFunctionByName`, `simulateReplaceBodyByName`, `commit`, `runPureByName`). The probe needed no `declarations` call and no ID→name map. Exact IDs were needed only to delete functions (friction 5); they were read from the create replies. Probe 3's scripts no longer exist, so everything was written fresh. Scripts and transcripts are kept outside the repository in the session scratchpad `probe4/`:

- `nw.py`: client and metrics.
- `oracle.py`: the real Python oracle, producing `cases.json`.
- `author.py` and `author_map.py`: the authoring.
- `run_cases.py`: the comparison.
- `workaround_dc.py`, `cleanup*.py`, `scale_probe.py`, `stack_probe.py` and `mistakes.py`.
- `*_transcript.jsonl`: every request and reply.

## Why this part

Probes 1-3 covered `team.py`'s leader loop, `records.summarize`/`preview`/`usage_is_complete` and `usage_stats`. This probe picks text processing, which those parts barely touched:

- **`review.parse_findings`** (with the `FINDING` regex, 19 lines). It is a line parser over a model reply that uses `str.splitlines`, a regex with Unicode `\s`, `IGNORECASE`, a lazy `(\S+?)` that backtracks over `:`, `int()`, `.lower()` and `.strip("`")`.
- **`repo_map`'s pure core** (about 80 lines). It includes:
  - `_headings`, a Markdown heading and fence scan over `bytes.splitlines` with two regexes;
  - `_generated`;
  - `outline`'s kind decision, without I/O;
  - `source_files`' prefix filter;
  - the whole rendering in `repo_map()`: right-aligned line numbers, a stable code-before-Markdown sort, a UTF-8 byte budget with early stop, an omitted list with a 20-name limit and `…`, the "other files" budget with a floor of 200, and a final `lstrip("\n")`.

That is about 100 lines of rrawrlet logic, fewer than the 150-400 suggested. It was chosen because it leans on about ten standard-library string behaviours (regex, `splitlines`, `strip`, `lower`, `encode`, `Path.suffix`, `join`, format alignment), and those turned out to be the dominant cost: the port is 47 functions and 1106 recipe nodes. The following were faked or supplied by the harness, as I/O:

- the file walk order (the real `source_files`/`_walked_files`);
- the file text (decoded as in `outline`);
- the size and NUL checks, as a `skipped` Bool;
- tree-sitter symbols for grammar files (`_parsed`), because tree-sitter is an external parser.

Markdown headings, the generated-name check, suffix handling, line counting and everything after them run in nyrwvy.

## What was authored

Request figures count authoring requests only. Each create is simulate + commit. Node counts are the server's `nodeCount` in the compact create reply; they equal the harness's recursive recipe count.

| Part | Types | Functions | Requests / request JSON / reply JSON | Recipe nodes | Trial and error |
| --- | --- | --- | --- | --- | --- |
| Text helpers (`charAt`, `inSet`, `skipIn`, `rskipIn`, `strip`, `splitFrom`, `splitLines`, `ciWordAt`, `endsWith`, `ciEndsWith`) | 0 | 10 | 20 / 16.0 KB / 15.9 KB | 203 (`splitFrom` 76) | 0 |
| `parse_findings` (`Finding`, `Tail`; `wordThen`, `severityAt`, `isDigit`, `digitsEnd`, `tailAt`, `findPath`, `parseLine`, `addFinding`, `parseFindings`) | 2 | 9 | 22 / 21.7 KB / 16.6 KB | 266 (`tailAt` 92) | 0 |
| `repo_map` core (`Symbol`, `SourceFile`, `Outline` union, `Section`, 6 accumulators, `MapResult`; 27 functions) | 11 | 27 | 76 / 55.2 KB / 56.5 KB | 651 (`repoMap` 114, one body with 6 nested `Bind`s) | 0 (1 harness-side Python typo, no request sent) |
| Depth workaround (friction 1): `explode`, `SplitAcc`, `splitStep`, `utf8Add`, 2 body replacements | 1 | +3 | 12 / 8.0 KB / 10.6 KB | +81 new, replaced bodies 6→20 and 4→4 | 0 |
| Cleanup: delete the dead `splitFrom`/`utf8From` | 0 | −2 | 8 / 1.1 KB / 10.3 KB | −109 | 1 rejection (friction 5) |
| **Final** | **14** | **47** | **138 / 101.9 KB / 109.9 KB** | **1106** | **0 recipe retries** |

`SelfCall` by name was used in 10 functions, and `explode` calls itself twice. `ListSortBy`, `StringLessThan`, callback `context`, a Union with a 2-field variant and `matchUnion` field references were all accepted on the first request. The deepest positional reference was `scopeDepth` 5 in `repoMap`. It was tracked by hand-written comments, and no reference was wrong.

## Results vs Python

| Group | Checks | Result |
| --- | --- | --- |
| `parse_findings` | 11 cases: empty; a typical reply; `Findings: none`; `*`, tabs, CRLF and a path containing `:` (lazy backtracking, `a:1:2` → path `a:1`, line 2); a whitespace-only summary (the regex yields `" "`); Unicode path, summary, U+3000 as `\s`, U+2028 and U+0085 as line breaks; 8 non-matching forms; backticks (``` `:1 ``` → path `""`) and mixed-case severity; `:`/`—`/`–` separators; `\v \f \x1c \r` breaks; rrawrlet's own `test_review` fixture | **11/11** |
| `_headings` | the sample (fences, `####### seven`, `#nospace`, `### `, a trailing `##`, U+3000 strip); lone CR/CRLF; rrawrlet's `DESIGN.md` (30.7K chars) | **3/3** (`DESIGN.md` only after the workaround; before it: stack overflow) |
| `repo_map` | 8 synthetic trees: empty; all kinds, including generated, NUL and empty files; 32 files with 25 omitted (`…`) and `x.MD` sorted after code; 60 "other" files cut by the budget (`… 43 more`); a UTF-8 budget with a Japanese heading over 100 chars; prefixes (`src/a.py`, `docs`, `srcx/` excluded); width 2 alignment and CR lines; suffix edges (`.md`, `..md`, `odd.`, `a.b.MarkDown`, `.lock`, `.map`). One real case, R1: rrawrlet's `README.md` and `records`/`review`/`repo_map`/`condense.py` (48.8K chars) | **9/9** |
| Known ASCII-only divergences | FX1 Arabic-Indic digits (`\d` + `int()`); FX2 a line number above Int64; FX3 `[HİGH]` under `IGNORECASE` (Python itself returns severity `'hi̇gh'`, an rrawrlet quirk); MX1 `x.marKdown` (Kelvin sign; Python's `.lower()` makes it Markdown) | 0/4, as expected: the port has no code-point or case-folding access |
| Runner limit | R2: the whole `rrawrlet/` package (66 files, 879K chars; `web_assets/app.js` alone has 67,349) | Rejected before running: `RunArgumentTooLarge` "A String argument or expectation is null or exceeds 65536 UTF-16 code units." |

**No correctness divergence was found within the port's stated ASCII scope.** Each run takes about 1.6-1.8 s.

**Scale (`scale_probe.py`, `stack_probe.py`), first version with per-character self-recursion:**

- `parseFindings` passes at 3,850 chars and fails at 7,700 with `RunRuntimeFailure` `System.StackOverflowException` "Stack overflow." (frames: `splitFrom ← splitLines ← parseFindings`).
- `headingsOf` fails at 8,000 chars. `utf8Len` passes at 4,000 and fails at 16,000.
- A two-parameter `countSp` survives 10,000 chars (1.0 s) and hits `RunTimeout` "exceeded its time limit of 5 s" at 50,000, because each `StringSlice` scans from the start, so a scan is O(n²).

After the workaround, every size up to 32K runs in 1.6 s.

## Probe 3's remaining items

| Probe-3 item | Now | Evidence |
| --- | --- | --- |
| Trivial named callbacks | **Still bites, mildly** | 6 functions of 3-8 nodes exist only to be callbacks: `sameText` 3, `isDigit` 3, `fileSelected` 4, `addFinding` 7, `maxLine` 8, `sectionBefore` 8. |
| Fields echoed in ID order | **Still present, harmless** | A `Finding` comes back as `summary, severity, path, line`, but decoding by `field` name needs no map. |
| Unnamed nested `itemType` | **Still present** | `"itemType":{"kind":"named","typeId":"01a0ecfc-…"}` beside items that do carry `"type":"Finding"`. |
| `Option<Option<R>>` impossible arms | **Did not bite** | 0 impossible arms. The one `None` arm in `tailAt` is reachable (Int64 overflow, FX2). |
| Verbose run-value encoding | **Still bites** | Output: F2's reply is 1,427 B for 173 B of plain JSON (8.2×), and H1's is 1,992 B for 88 B (22×). Input: M2's request is 4,087 B for 820 B (5×). Run requests total 1.35 MB for 28 runs, mostly source text. |
| Positional `scopeDepth` | **No retry** | Depth 5 was reached by hand-tracking. A deliberately wrong depth that hits a binding of another type (m6) reports only `PureExpressionOperandTypeMismatch` "StringLength value operand requires Primitive String, but the recipe produces Primitive Int64.", with no hint about scope. |

## New friction, ranked by impact

| # | Friction | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **Text iteration exists only through self-recursion, which is O(n²) and limited by the stack** | The natural `splitFrom` (per code point, 5 parameters) overflows between 3,850 and 7,700 chars, and `utf8From` below 16,000. It fails at runtime only, with "Stack overflow." and no depth or size. rrawrlet's own `DESIGN.md` (30.7K) failed. `StringSlice` is linear, so the scan is also quadratic: 50K chars reach the 5 s `RunTimeout`. Workaround: `explode` (halving recursion, depth log n) plus fold-based `splitStep`/`SplitAcc` and `utf8Add`. That cost 1 type, 3 functions, 81 new nodes and 2 replacements (12 requests), then 8 more requests to delete the dead recursive helpers. The pattern is not discoverable from the docs. | A linear `StringCodePoints(text): List<String>` (or `StringSplit`, see 2), so that `ListFold` does the iteration. Document the recursion bound, and report it as a diagnostic that names the recursion depth. |
| 2 | **No string search or affix primitives** | Emulated: split lines, index-of and last-index-of (`afterLast`), a run of a character class (`skipIn`/`rskipIn`, `digitsEnd`), strip, `endsWith`, `startsWith` (inline), basename. That took 13 functions and 199 of 1106 nodes (18%). The one-line `FINDING` regex plus the 15-line loop became 5 functions and 240 nodes (`tailAt` 92, `parseLine` 57, `findPath` 51). | `StringIndexOf(text, sub, from): Option<Int64>` and `StringLastIndexOf`, `StringStartsWith`/`StringEndsWith`, and `StringSplit(text, separator)` with exact semantics. Regex stays out of scope. |
| 3 | **No case folding** | `severity.lower()`/`IGNORECASE` and `suffix.lower()` needed `ciWordAt`/`ciEndsWith`/`wordThen` (3 functions, 63 nodes), and every literal is passed in both spellings (`"high"`, `"HIGH"`). The port is ASCII-only, so 2 of the 4 known divergences come from this (FX3, MX1). | `StringToLower` with Unicode simple case mapping (code-point based, culture-free), and a stated behaviour for `İ`/`K`. |
| 4 | **Structural diagnostics use IDs and F# rendering and have no recipe location** | In a `…ByName` request: m2 "Field access requires Named (StableId \"01a0ecfc-…\"), but the source recipe produces Named (StableId \"01a0ecfc-…\")." (should say `Tail` vs `Finding`); m3 "ListSortBy less-than callback '01a0ed01-…' must return Bool."; m4 "…found Normal (BuiltinGeneric (Option, [Primitive String])) and Normal (Primitive String)."; m1 "ListFold folder must have type (accumulator, item) -> accumulator." names neither the folder nor its actual parameter types. None of them locate the node, which matters in 92-114-node bodies. Order 10 covered fields, operations and functions in some diagnostics, not type references or callbacks. | Render types as display names in wire `TypeRef` vocabulary, name callbacks, and add a recipe path (such as `body.whenFalse.value`) to `SimulationRejected` diagnostics. |
| 5 | **A self-recursive function cannot be deleted, and deletion needs exact IDs** | `deleteFunction` on `utf8From` alone was rejected with `FunctionStillReferenced` "Repair all direct calls and List callbacks before deleting this function." Its only reference is its own `SelfCall`, and the message names neither the function nor the referrer. Workaround: replace each body with a non-recursive one, then delete (4 extra requests). `simulateOperations` has no by-name form, so the IDs were taken from the create replies. | Ignore a function's references to itself in the deletion check, and name the referrers. Accept `function` names in `deleteFunction`, or add `simulateOperationsByName`. |
| 6 | **No UTF-8 byte length** | `len(text.encode())` drives both of `repo_map`'s budgets. It is emulated per code point with three `StringLessThan` thresholds (`"\x80"`, `"ࠀ"`, `"\U00010000"`), which relies on order 12's code-point ordering: `utf8Add` + `utf8Len`, 22 nodes (the first version, `utf8From`, was 33 and overflowed, friction 1). M5 (Japanese) confirms parity. | `StringUtf8ByteCount(text): Int64`. |
| 7 | **Missing List and String utilities for join, drop, pad and early stop** | `"\n".join`/`", ".join(xs[:20])` → `joinStep`, `joinFirst` and `JoinAcc` (31 nodes); `sections[len(shown):]` → `omitStep` and `OmitAcc` (19); `'  ' * n` and `{:>{w}}` → `repeat` (12); `break` → a `stopped` flag in `ShowAcc`/`OtherAcc`. In total 4 functions, 2 types and 62 nodes. | `StringJoin(list, separator)` first, then `ListSlice(list, start, end)` with `StringSlice`'s clamping. |
| 8 | **The `runPure` value budget excludes repository-scale input** | R2 is rejected, and R1 had to be pre-filtered to 48.8K chars to fit 65,536 code units. The rejection message is accurate for R2 (one 67K file). The aggregate limit would have rejected it anyway. | Keep the bound (by design). If a consumer needs more, add a file-backed String run value for tests. Otherwise, document the aggregate limit next to `runPureByName`. Low. |
| 9 | Per-create request overhead | Every declaration takes 2 requests (59 commits of 138 requests). A compact create reply is about 1.3 KB even for a 3-node function (`sameText` 1,278 B), mostly the receipt and fingerprints; a type reply is 1.2 KB. Replies (109.9 KB) now roughly equal requests (101.9 KB). | An optional `commit: true` on `simulate*` that commits when the proposal is ready. Low. |

Not queued, unchanged: verbose run-value encoding (see above; a `plain` output view keyed by field name would cut replies 8-22×), trivial callbacks, and the ~1.7 s build per run (28 runs took about 50 s).

## Remaining workarounds

- **Text primitives:** `explode` + `splitStep`/`SplitAcc` (iteration), `charAt`, `inSet`, `skipIn`, `rskipIn`, `strip`, `digitsEnd`, `isDigit`, `afterLast`, `baseName`, `endsWith` (frictions 1-2).
- **Case:** `ciWordAt`, `ciEndsWith`, `wordThen`, and literals in both cases (friction 3). The port is ASCII-only: FX1-FX3 and MX1 differ from Python.
- **Bytes:** `utf8Add`/`utf8Len` by thresholds (friction 6).
- **Lists:** `joinStep`/`joinFirst`/`JoinAcc`, `omitStep`/`OmitAcc`, `repeat`, and `stopped` flags (friction 7).
- **Regex:** `tailAt`/`findPath`/`parseLine`/`severityAt` reproduce the backtracking by hand, including the one-character whitespace summary.
- **Callbacks:** `sameText`, `isDigit`, `fileSelected`, `addFinding`, `maxLine` and `sectionBefore`.
- **Harness:** the size/NUL `skipped` flag and tree-sitter symbols come from Python, and R1's files are pre-filtered to fit the value budget.

## Proposed queue (orders 17+)

Provisional, in the same way as the earlier queues. The queue is maintained in the [post-M2 roadmap](../plans/post-m2-roadmap.md#queue-from-the-2026-09-29-probe-4).

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 17 | Linear text iteration (friction 1) | Stack overflow at about 7.7K chars on the natural recursive scan; O(n²) slicing; a workaround of 3 functions, 1 type and 20 requests | `StringCodePoints(text): List<String>` (linear, never fails), so `ListFold` replaces the recursion. Document the recursion depth bound. |
| 18 | String search and affixes (friction 2) | 13 functions and 199 nodes of emulation; the regex port is 240 nodes | `StringIndexOf`/`StringLastIndexOf` (code-point positions, `Option<Int64>`), `StringStartsWith`/`StringEndsWith`, and `StringSplit`. |
| 19 | Structural diagnostics by name and location (friction 4) | 4 of 5 simulated mistakes print StableIds or F# type dumps and give no node location | Display-name `TypeRef` rendering, callback names, and a recipe path in `SimulationRejected` diagnostics. |
| 20 | Deleting self-recursive functions, and by-name delete (friction 5) | `FunctionStillReferenced` from a function's own `SelfCall`; 4 extra requests; exact IDs needed | Exclude self-references, name the referrers, and accept a function name. |
| 21 | Case mapping and UTF-8 byte count (frictions 3 and 6) | 3 + 2 functions (85 nodes); 2 of the 4 known divergences | `StringToLower` (simple case mapping) and `StringUtf8ByteCount`. |
| 22 | `StringJoin` and `ListSlice` (friction 7) | 4 functions, 2 types, 62 nodes | `StringJoin(list, sep)`, then `ListSlice(list, start, end)`. |

Considered and not proposed yet: regex, which is a larger decision that orders 18 and 21 should precede; a plain run-output view (the known verbose encoding); `commit: true` on simulate; and raising the `runPure` value budget. Inferred, not measured: with orders 17, 18, 21 and 22 the port would shrink by roughly 20-25 of 47 functions and 350-400 of 1106 nodes, based on the category counts above.

## Follow-up: order 19 (structural diagnostics)

The `mistakes.py` requests were replayed against a copy of the probe snapshot with the order-19 build. Codes are unchanged; IDs are shortened here.

| Mistake | Before | After |
| --- | --- | --- |
| m1 folder parameters swapped | `ListFold folder must have type (accumulator, item) -> accumulator.` | `ListFold folder 'countStep' (…) must have type (accumulator, item) -> accumulator, here (Int64, String) -> Int64; the folder has type (line: String, acc: Int64) -> Int64. At body (listFold).` |
| m2 wrong owner type | `Field access requires Named (StableId "…"), but the source recipe produces Named (StableId "…").` | `Field access to 'line' (…) requires 'Finding' (…), but the source recipe produces 'Tail' (…). At body (fieldAccess).` |
| m3 comparator returns Int64 | `ListSortBy less-than callback '…' must return Bool.` | `ListSortBy less-than callback 'cmpLen' (…) must return Bool; it has type (a: String, b: String) -> Int64. At body (listSortBy).` |
| m4 branch type mismatch | `…found Normal (BuiltinGeneric (Option, [Primitive String])) and Normal (Primitive String).` | `…found Option<String> and String. At body (conditional).` |
| m6 wrong operand | `StringLength value operand requires Primitive String, but the recipe produces Primitive Int64.` | `StringLength value operand requires String, but the recipe produces Int64. At body.someBody.body.left (stringLength).` |

m5 and m7 are `ProtocolError`s from the name-resolving reader and already named the property or parameter; they are unchanged.

## Follow-up: order 22 (StringJoin and ListSlice)

The `repo_map` part (`author_map.py`) was re-authored with the order-22 build: `joinFirst(xs, sep, BIG)` became `stringJoin`, `joinFirst(xs, ", ", 20)` became `stringJoin(listSlice(xs, 0, 20), ", ")`, and the `omitStep` fold became `listMap(listSlice(sorted, len(shown), len(sorted)), sectionName)`, where `sectionName` is a 2-node field read. The text and findings parts were left as they were, so orders 17, 18 and 21 are not counted here. Both versions were authored and run with the same build.

| Map part | Before | After |
| --- | --- | --- |
| Functions | 27 | 25 (`joinStep`, `joinFirst`, `omitStep` removed; `sectionName` added) |
| Types | 11 | 9 (`JoinAcc`, `OmitAcc` removed) |
| Nodes | 651 | 603 (-48) |
| Authoring requests / request bytes | 76 / 55,237 | 68 / 50,047 |

All map cases gave the same results before and after (M1-M8 pass; MX1 is the known Kelvin-sign divergence of the unchanged suffix check; R2 exceeds the `runPure` argument size). With this build, R1 and the headings case H3 fail with `RunRuntimeFailure` (stack overflow in the per-character `splitFrom` recursion) in both versions, so the failure is unrelated to order 22 (the original probe binary passed them); re-authoring `splitFrom` over `StringCodePoints` or `StringSplit` (orders 17 and 18) would remove that recursion and was not measured here. The rest of friction 7 (`repeat` for `'  ' * n` and padding, and the `stopped` flags for early exit, 12 nodes and 2 fields) is not addressed by order 22.
