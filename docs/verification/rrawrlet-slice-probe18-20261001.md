# rrawrlet slice probe 18 — 2026-10-01

**Status:** Complete. The rest of rrawrlet's `review.py` was authored by name only. Probes 4 and 5 had ported just `parse_findings`; this probe ports the whole module: `parse_findings` again, `_revision`, `_pathspecs`, `git_diff` with its `bytes.decode("utf-8", "replace")`, `collect_diff` over its three sources, and `review` with its validation, prompt, output path and result record. `condense`'s `model_order`, `check_model`, `check_words` and `condense_root`, `claude_backend.validate_selection` and `results.saved_result` come with it. `load_run`, Git, `Path.resolve`, `Path.is_dir`, the home lookup of `Path.expanduser`, `models.json`, `runs_root`, `uuid4`, `ask_in_order` and the file write are a 10-operation `Host` capability. The port matched the real Python on **828/828 cases**, including all 1,992 host calls in order. It also matches the three inputs that probe 5 left as known divergences (Arabic-Indic digits, a line number above Int64, `[HİGH]`). There are no known divergences inside the scope below. **Nothing is blocked and nothing is queued.**

No cost limits reach. Two recurring costs came back. `matchUnion` needs every arm (276 nodes, 10% of the port, a fourth observation), and the lossy UTF-8 decoder had to be written by hand again (probe 14's 228 nodes, a third observation after probes 14 and 16). One cost is new: Python's unbounded `int`. A line number beyond Int64 needed a `Big` variant in `Json`, and adding it put one more arm into every `Json` match. The product update and `Float64Round` candidates were not needed, and neither were Map or tail calls.

**Date:** 2026-10-01

Baseline: nyrwvy `ed1141b` (main after #280, clean); rrawrlet `4276240` (clean). The session server was built with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release` on .NET SDK 10.0.112. The oracle ran rrawrlet's real `review` functions on Python 3.11.15.

The method was probe 17's: the `Nyrwvy.AuthoringSession` JSON-lines protocol with name-resolved requests and no `declarations` call, and `runPure` batches through a `runCases` entry. Probe 11's session client and recipe helpers were reused unchanged. Probe 9's JSON helpers and `json.dumps`, probe 14's decoder, and probe 17's strings, paths and `saved_result` were reused as source. The harness, cases, snapshot and transcripts are in private files outside the repository (`rrawrlet-probe18/`) and are not committed.

## Why this part

Of the three modules no probe had covered (`review.py`, `web_live_requests.py`, `codex.py`), `review.py` exercises the most that probes 4 to 17 left out. `codex.py` is mostly process and pipe handling, which would sit on the host. `web_live_requests.py` repeats probe 17's loosely typed validation.

- **A regex with Unicode classes, ported without a regex engine.** `FINDING` uses `re.IGNORECASE` (so `İ` and `ı` match `i`), `\s` over Python's 29 whitespace code points, `\d` over all 660 Unicode decimal digits, a lazy `(\S+?)` path and `(.+?)\s*$`. `str.splitlines` breaks at 10 code points, and `severity.lower()` turns `İ` into two code points.
- **Python's unbounded `int`.** `int(number)` accepts any decimal digits, has no upper bound, and raises `ValueError` beyond 4,300 digits.
- **Bytes from Git.** `git_diff` decodes Git's output with `errors="replace"` in the module itself. Probe 17 could leave every decode on the host; this module cannot.
- **`len(text.encode())`** over a diff given as text, which raises `UnicodeEncodeError` for a lone surrogate, with Python's position text for one or a run of them.
- **A dict built by merging.** `{"findings": …, "review": …, **answer, "source": …, "diff_bytes": …}` after `answer.pop("answer")`, where a key from the answer keeps its early position while a later literal replaces its value.

## What was ported

| Python | Port |
| --- | --- |
| `checkout_git` | `Host.git(cwd, argv): Result<String, Exc>`, argv NUL-joined; Git's bytes are one code point per byte (U+0000–U+00FF), as in probe 14 |
| `load_run`, `Path.resolve`, `Path.is_dir` | `loadRun(value): Result<Run, Exc>`, `resolve(path): Result<String, Exc>`, `isDir(path)` |
| `os.path.expanduser(parts[0])` inside `Path.expanduser` | `userHome(part)`; the port keeps `Path.expanduser`'s own logic, including its `RuntimeError` when the lookup returns a `~` path |
| `preferred_choices`, `runs_root`, `uuid.uuid4().hex` | `preferred(role): Json`, `runsRoot()`, `uuid()` |
| `ask_in_order` | `askInOrder(order: Json, repo, prompt, instructions, output, timeout: Json): Result<Json, Exc>`; the answer object is scripted, including the `output` and `tried` keys that the real function adds |
| `write_record` | `writeText(path, text)`; the port writes `json.dumps(record, ensure_ascii=False, indent=2) + "\n"` itself, and the host only encodes it (a lone surrogate there is Python's `UnicodeEncodeError`) |
| `str.splitlines()` | `StringSplitLines` with Python's 10 break code points (checked against Python when the script runs) |
| `^\s*[-*]\s*\[` … `\]\s+` | `StringSpan` over the whitespace set, one-character slices, `StringIndexOf("]")` |
| `(high\|medium\|low)` under `re.IGNORECASE` | `StringToLower` after replacing `İ` and `ı` with `i` (the full set Python accepts, enumerated over all code points); `severity.lower()` is `StringToLower` after `İ` → `i̇` |
| `(\S+?):(\d+)\s+` | a fold that finds the first whitespace, then `StringLastIndexOf(":")` in that token. A shortest path must be followed by digits only, so the last colon is the only candidate. `StringSpan` over the 660 `\d` code points checks the digits |
| `[-–—:]\s+(.+?)\s*$` | `StringSpan` and `StringSpanBack`; when only whitespace follows the dash, the summary is its last whitespace character, as Python's backtracking gives |
| `int(number)` | a fold over the 66 zero code points maps each digit to ASCII, then the 4,300-digit `ValueError`, leading zeros, and `Int` or `Big` (decimal text) depending on whether it fits Int64 |
| `path.strip("`")` | `StringSpan`/`StringSpanBack` over `` ` `` |
| `bytes.decode("utf-8", "replace")` | probe 14's decoder (`Dec` state, 4 functions), with `StringConcat` per character now that order 31 made it linear |
| `len(text.encode())` | a fold over code points that finds the first run of lone surrogates, then `StringUtf8ByteCount` |
| `Path(repo).expanduser()`, `.is_absolute()`, `condense_root().parent / "review"` | probe 17's `pnorm` and `pjoin`, plus `pparent` |
| `{"effort": effort, **choice, "operator": True}`, the result merge, `answer.pop` | probe 9's `upsert`, which keeps an existing key's position, and a filter |
| `INSTRUCTIONS.format(words=…, state=…)` | the text split at its two fields; `words` is written as JSON, so a `Big` value prints its digits |

Case outputs are one String: `json.dumps(…, ensure_ascii=False)` of what the function returned, the decoded text, or `TypeName: message` for an exception that escapes.

Out of scope: what the host does. That is Git and its sandbox, the model call inside `ask_in_order` with its fallbacks and `model_health`, `models.json` parsing, and `pwd` lookups. `repo`, `run` and `output` are only given as strings or `null`, except for `repo` values that test `Path`'s `TypeError`.

## What was authored

| Part | Functions / nodes |
| --- | --- |
| Types | 10 in two requests (`Json` with `Big`, and `Member`, as one recursive group; `Exc`, `Run`, `Saved`, `Src`, `Dec`, `Sur`, `Scan`, `Case`); 1 effect; `Host` with 10 operations |
| JSON helpers and `json.dumps` (probe 9), strings and paths (probe 17), value helpers | 42 / 607 |
| `bytes.decode("utf-8", "replace")` (probe 14) | 4 / 228 |
| `parse_findings` | 13 / 564 (`findingTail` 108, `findingOf` 83, `parseFindings` 84) |
| `check_words`, `check_model`, `validate_selection`, `model_order` | 8 / 284 |
| `saved_result` | 3 / 179 |
| `_revision`, `_pathspecs`, `git_diff`, `collect_diff` | 8 / 331 (`collectDiff` 164) |
| `review` | 8 / 545 (`reviewAsk` 164, `reviewRun` 132) |
| Case dispatch | 4 / 57 |
| **Total** | **90 / 2,795** in 95 proposals |

There were **no recipe rejections**. One batch failed on the first run of the cases because the snapshot had been built before one change to `instructions` (writing a `words` value beyond Int64 as digits rather than `0`). With the snapshot rebuilt from the same script, all cases matched. The port has not changed since.

## Results vs Python

828 cases. The oracle ran the real function once per case in a scripted world. Python's own `pathlib` logic ran for real, and only the operations in the table above were scripted and recorded. A case matches when its output is equal and the port made the same host calls with the same arguments in the same order: 1,992 calls, 587 of them Git.

| Group | What it covers | Result |
| --- | --- | --- |
| `parse_findings`, one line (76) | Both bullets, whitespace before and between, `-[high]`, mixed case, `İ` and `ı` in the severity, unknown severity, no space after `]`, paths with several colons, `:12` (empty path), `a:`, `a:1x`, Arabic-Indic, Devanagari, full-width and astral digits, leading zeros, `Int64.MaxValue` and the next integer, 2^64, 40 nines, backticked paths including one that strips to `""`, all four dash characters, no space before or after the dash, only whitespace after the dash (one or several characters), U+3000 and U+001F as whitespace, `\x85` in a summary, a lone surrogate in a summary, incomplete lines | 76/76 |
| `parse_findings`, whole texts (27) | `None`, `""`, `0`, `[]`, `False` (no findings), `5`, `[1]`, `{"a": 1}`, `True`, `1.5` (`AttributeError`); `\r\n`, `\r`, `\v`, `\f`, `\x1c`–`\x1e`, `\x85`, U+2028/U+2029 as line breaks; 4,300 digits (valid) and 4,301 in ASCII or Arabic-Indic digits (`ValueError`), after an earlier valid finding; 3,000 colons in one path; 5,000 trailing spaces; 3,000 U+3000 before the path | 27/27 |
| `parse_findings`, random (150) | Finding-shaped lines built from mutated parts, and strings over 24 fragments | 150/150 |
| `decode` (230) | Empty, ASCII, 2-, 3- and 4-byte sequences, overlong `C0`/`C1`/`E0 80`, encoded surrogates (`ED A0 80`), `F4 90`, `F5`–`FF`, lone continuation bytes, sequences cut short at the end and in the middle, NUL; 200 random byte strings and mutated UTF-8 | 230/230 |
| `review`, hand-written (195) | `repo` as `/r/`, `//r`, `""`, relative, `~`, `~/r`, `~bob/x` (the lookup fails), `~root/r`, a home that is `/` or relative, a missing directory, and `5`, `[]`, `True`, `1.5` (`TypeError`); `focus` blank, padded with U+3000, `5`, a lone surrogate (the write fails); 22 explicit model choices (backend defaults, `0` as backend, `opencode` without provider, Claude aliases, `claude-3.5-x`, `claude-`, `Opus`, `" opus"`, `claude-a_b`, `"sonnet\n"`, a non-string model); 10 `models.json` orders with and without `effort` (overrides, `operator` overwritten in place, a missing `backend` or `model` as `KeyError`); `words` 19, 20, `"600"`, `600.0`, `True`, `None`, 10^30; timeouts; `max_bytes` at, above and below the size, with 3-byte characters; every source combination; revisions starting with `-`, blank, with NUL, non-string, `head` falsy or invalid; `paths` valid, empty, a string, `[""]`, NUL, `[1]`, `None`, and ignored for a diff given as text; 10 run records (unfinished, no or a non-string repository, `preparation` a string, a snapshot, `{}`, bad commits, a relative repository, `load_run` raising); Git failing at `rev-parse`, `diff`, `cat-file` and `merge-base`; invalid UTF-8 from Git through both sources; 7 texts with lone surrogates (one, a run of 3, after an astral character, low before high); output paths and `runs_root` values (`/`, `//s/runs`, relative); 14 answers (`answer` missing, `None`, `5`, `""`, keys that collide with `findings`, `source`, `diff_bytes`, `review` and `output`, no `output` key, `İ`, a 25-digit line, 4,301 digits, `ask_in_order` raising); a failing write and a failing `resolve` | 195/195 |
| `review`, random (150) | A valid call through each source with zero, one or two fields changed, random `models.json` orders, answers and Git bytes | 150/150 |
| **Total** | 345 `review` calls: 172 return a result and 173 raise, 10 of those with `UnicodeEncodeError` | **828/828** |

The 828 cases took 24 runs and 16.2 s (about 6 s for the first run, which includes the session's first build, then 0.3–0.6 s each). A deliberately wrong expected output (`hıgh` for `hi̇gh`, a changed line number) and a reordered expected call list were all reported as mismatches, so the comparison is not vacuous.

Probe 5's three known divergences now match. FX1 (Arabic-Indic digits) and FX2 (a line number above Int64) were limits of that port, which used an ASCII `\d` and an Int64 line. FX3 (`[HİGH]`) was left as a `StringToLower` difference; replacing `İ` before lowering gives Python's `hi̇gh` with no new builtin.

## Scale

`runPure` wall time on a copy of the snapshot, including the build and process start (the first run, with the session's first build, took about 6 s). Every row matched Python.

| Input | Time |
| --- | --- |
| `parse_findings` of 26,865 characters with 600 findings | 0.77 s |
| one path with 4,000 / 16,000 colons | 0.43 / 0.49 s |
| 16,000 spaces before the path | 0.50 s |
| 60,000 characters with no finding | 0.42 s |
| a 4,300-digit Arabic-Indic line number | 0.44 s |
| `decode` of 16,000 / 60,000 random bytes | 0.53 / 0.49 s |
| `decode` of 60,000 bytes of 3-byte UTF-8 | 0.50 s |
| `review` of an 18,390-character diff with a 3,000-character answer | 0.60 s |

Every row is linear. Probe 5's adversarial rows were 2.5 s for 4,000 colons and a stack overflow at 8,000, because its `findPath` recursed once per colon. Here the path is found with `StringLastIndexOf`, because a shortest path followed by digits alone can only end at the last colon. Larger inputs are bounded by the harness, not the language: a String argument, a fake's aggregate value and a result are each limited to 65,536 UTF-16 units, and `review`'s default `max_bytes` is 262,144.

## Costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **`matchUnion` needs every arm** (fourth observation) | The port's own 17 matches over the 8 `Json` variants restate a default body in 117 arms: 276 nodes, 10% of the port (`measure.py`, which counts the same 2,795 nodes as the server). The JSON helpers add more. Probes 7, 10 and 17 recorded the same cost | None queued. As before, a default arm trades away exhaustiveness, and the [foundational decisions](../decisions/foundational.md) say wildcard branches must not absorb unknown domain cases. It needs a decision first |
| 2 | **Lossy UTF-8 decoding by hand** (third observation) | `git_diff` decodes Git's bytes with `errors="replace"`. Probe 14's decoder (4 functions, 228 nodes) was reused unchanged and matched 230 decode cases and 38 non-ASCII Git diffs that reached the model call in `review`. Probe 16 needed the strict and `surrogateescape` modes (429 nodes) | None queued. Probe 14's candidate: a decode builtin with the error mode as a parameter. This is the third program to need it, after probes 14 and 16 |
| 3 | **Python's unbounded `int`** (new) | A line number beyond Int64 is ordinary Python. The port added a `Big` variant (decimal text) to `Json`, which put one more arm in every `Json` match (16 default arms, 37 nodes, in the port's own matches) and needed the digit-limit rule written out (`intOf`, 72 nodes). `words` beyond Int64 passes `check_words` and reaches the instructions text the same way | None. One observation. A bounded Int64 is a deliberate subset choice, and the cost here was small |
| 4 | **Regex by hand** | `FINDING` is 13 functions and 564 nodes, with whitespace, digit and case sets enumerated from Python when the script runs. Order 17–22's builtins made every step linear | None. Probe 5 recorded this cost; it no longer brings a divergence or a depth limit |

Not needed this time: product update (the only state products are rebuilt in `surStep` and `scanWs`, a few nodes), `Float64Round` (there are no floats), Map (the only dict merges are `upsert` over short member lists) and tail calls (every loop is a fold).

## Candidate hit counts after probe 18

| Candidate | Probes |
| --- | --- |
| Product update node | 7, 10, 15, 17 (4; not in 18) |
| `matchUnion` default arm (needs a decision) | 7, 10, 17, 18 (4) |
| UTF-8 decode builtin | 14, 16, 18 (3) |
| `Float64Round` | 15, 17 (2) |
| Map | 16 strong, 13 weak |
| Tail self-call as a loop | 15 |
| Unbounded `int` | 18 (small) |

## Reproducing

The scripts are in private files outside the repository under `rrawrlet-probe18/`. `author.py <snapshot>` authors the port (it execs `jsonpart.py`, probe 9's JSON helpers with the `Big` variant) and writes `author_stats.json`. `oracle.py` runs `rrawrlet.review` with the scripted host and records the calls; it needs an rrawrlet checkout at a fixed local path. `mkcases.py [out.json]` builds the 828 cases. `run_cases.py <snapshot> cases.json` runs them. `scale.py <snapshot copy>` gives the scale rows, and `measure.py` counts the default arms without a server. `nw.py` and `dsl9.py` are probe 11's client helpers, unchanged. Run the scripts with `PYTHONPATH=rrawrlet-probe18` and `NW_BIN` set to the built `Nyrwvy.AuthoringSession`.
