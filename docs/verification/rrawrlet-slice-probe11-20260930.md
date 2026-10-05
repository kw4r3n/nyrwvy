# rrawrlet slice probe 11 — 2026-09-30

**Status:** Complete. Two new parts of rrawrlet were authored by name only: the transcript reduction in `snapshot.py` (`entries`, `_tool_input`, `_result_text`, `_clip` and `render`) and all of `model_health.py` (`key`, `cooldown_seconds`, `cooling`, `record`, `available` and `record_run`). The port matched the real Python on 301/304 cases. The 3 misses are known divergences, all from integers beyond what Int64 and Float64 hold exactly. **Nothing is blocked, and nothing is queued.**

The port needed one rename (a display name clash) and had one bug, which the first run found: an authoring-script macro for `<=` read the clock twice, because Float64 has no `<=` operator. Python's exceptions had to be written as data, and that was the largest new cost. Unicode properties could be read exactly from boundary tables written as literals. This also removes probe 10's `str.isprintable` divergence, with no language change. A 4.2 MB transcript runs within the 5 s limit.

**Date:** 2026-09-30

Baseline: nyrwvy `c689bde` (main after #263, clean); rrawrlet `714ed17`. The session server was built with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release`. The oracle ran rrawrlet's real functions with Python 3.11.

The method was probe 10's: the `Nyrwvy.AuthoringSession` JSON-lines protocol with name-resolved requests and no `declarations` call, and `runPure` batches of up to 40 cases through a `runCases` entry. Scripts, cases and transcripts are kept outside the repository in private files outside the repository (`rrawrlet-probe11/`).

## Why these parts

Neither part had been probed. Between them they exercise what probes 4 to 10 left out:

- A regular expression search with `re.IGNORECASE` and `\b` over any text (`cooldown_seconds`).
- A clock, and a file that is read, changed and written back (`cooling` and `record`).
- `json.dumps` with `indent=2` and `ensure_ascii=False`.
- Output shrunk to a UTF-8 byte budget, with a loop that walks backwards and breaks (`render`).
- `str.split()` whitespace, `str[:n]` and `str.encode()` on text that can hold lone surrogates.
- Code that relies on Python raising: `.get` on a non-dict, hashing a list, `str.join` of a non-string, `>` between `str` and `float`, iterating an `int`, and a generator that stops at the first match.

Probe 10's JSON scanner and its `str()`/`repr()` of JSON values were copied into the new recipes. `json.dumps` was generalised.

## What was ported

| Python | Port |
| --- | --- |
| Transcript file, cooldown file, `time.time()` | Capability `Host` with `readText(path): Option<String>`, `writeText(path, text): Unit` and `clock(): Float64`. The oracle used a fake `Path` and a fake `time`, so the port had to make the same reads, the same writes with the same text, and the same number of clock reads, in the same order |
| `open(path)` lines | `StringSplitLines(text, "\n\r")`, which is Python's universal newlines (`\r\n`, `\r` and `\n`) |
| `json.loads(line)` | `loads`: probe 10's scanner in its plain mode, so the last duplicate key wins |
| `entries` | A fold over the lines with a `Result<List<Entry>, String>` accumulator. After an `Err`, the remaining lines are not parsed. Each Python exception is an `Err` carrying `Type: message` exactly as Python prints it |
| `_tool_input`, `_result_text` | `firstStr` over the six keys, else `dumpsG(value, ascii = false)`; `partStep` counts only the dict parts for `sequence item N` |
| `_clip` | `" ".join(text.split())` is `StringSplitLines` with the 29 `str.isspace` code points as breaks, then `ListFilter` and `StringJoin` |
| `render` | `tryLevel` for each of the three limits, with `StringUtf8ByteCount` for `len(text.encode())`. `encodeError` finds the first run of lone surrogates and words the `UnicodeEncodeError` as Python does (`character '\udc00' in position 11`, or `characters in position 6-7`). The backward loop with a break became a forward fold that drops entries from the front while the rest does not fit (`dropStep`), which keeps the same longest suffix |
| `re.search(LIMIT / PROVIDER, text)` | `foldChar` maps each code point to what `re.IGNORECASE` matches: ASCII case, U+0130 and U+0131 to `i`, U+212A to `k`, U+017F to `s`. The literal alternatives are then `StringContains` checks. `\b429\b` and `\b50[0234]\b` split the text on the digits and check the neighbouring characters (`bndStep`) |
| `\w`, `str.isprintable` | Boundary tables (see below) |
| `cooling`, `record` | `untilNum` treats `bool` as an `int`, as `isinstance` does. `until <= now` and `v.get("until", 0) > now` compare as Float64, and a non-number raises the `'>' not supported between instances of 'str' and 'float'` error. `record` writes `json.dumps(data, ensure_ascii=False, indent=2) + "\n"`, or returns Python's `UnicodeEncodeError` when the text holds a lone surrogate |
| `available` | `listEffectFold` over the order, because each `cooling` reads the file |
| `record_run` | `summary.get` on a non-dict, iteration of `str`, dict and number values, and a `leaderStep` fold that ignores items after the first match, because Python's `next(...)` never looks at them |

Out of scope: `find_transcript`, `snapshot()` itself (it calls a model), and byte decoding. `Path(output) / "summary.json"` was written as `output + "/summary.json"`, and the cases use clean paths.

## Unicode properties as boundary tables

`\w` in a `str` pattern and `str.isprintable` are Unicode properties. The port stores each one as the sorted code points where the property changes, written as decimal text in a String literal: 1,468 boundaries (8,376 characters) for `\w` and 1,400 (8,179 characters) for printable. The tables were computed from Python 3.11 when the script authored the recipes. `inTable(n, table)` splits the text on `,` and counts the boundaries at or below `n`; an odd count means the property holds. That is 4 functions and 32 nodes.

The answers are exact. A 23-string `repr` case covering ZWSP, SOFT HYPHEN, a tag character, an unassigned code point, U+FEFF, U+061C, a combining mark and astral characters matches Python. So probe 10's known `repr` divergence goes away without a language change, if a port ever needs it. The cost is time: every lookup parses the whole table. 4,000 `\b` checks took about 2.6 s, roughly 0.3 ms per lookup, where Python took 4 ms in total. Code that looks up every character of a long text would hit the run limit. *Inference:* a sorted `List<Int64>` held as a value would make each lookup cheaper, but a list literal is bounded by the 256-node recipe limit, and a `runPure` argument holds at most 4,096 items.

## What was authored

| Part | Functions / nodes |
| --- | --- |
| Types | 13: `Json` and `Member` in one group, `Frame`, `Mode`, `Parser`, `NumParts` (probe 10's), and `Entry`, `Case`, `Sur`, `Lines`, `Rendered`, `Drop`, `Bnd`, `AvailAcc`; 1 effect, 1 capability with 3 operations |
| Reused from probes 9 and 10: JSON access, the scanner, `str()`/`repr()` | 50 / 1,128 |
| `json.dumps` with `ensure_ascii` and `indent` | 7 / 163 |
| Unicode tables | 4 / 32 |
| Python exceptions: type names, `AttributeError`, `UnicodeEncodeError` | 4 / 129 (`surStep` 64) |
| `snapshot.entries` and `json.loads` | 11 / 475 (`recordEntries` 77, `blockStep` 75) |
| `_clip` and `render` | 10 / 259 (`lineAt` 52, `tail` 47) |
| `key` and `cooldown_seconds` | 7 / 224 (`cooldownSeconds` 80, `bndStep` 62) |
| `cooling`, `record`, `available`, `record_run` | 12 / 434 (`recordOutcome` 69) |
| Case dispatch | 4 / 71 |
| **Total** | **109 / 2,915** in 252 requests |

There was **one rejection**, and it was not a recipe error. `finish` already named the scanner's finishing function, so the new function became `finishRecord`. There were no `scopeDepth` or type errors. The first run of the cases found **one port bug**. The script's `<=` helper expands to `Float64LessThan(a, b) || Float64Equal(a, b)`, and `cooling` passed it the clock read, which then ran twice. Three `now=None` cases exhausted the fake's clock responses. Binding the read first fixed it. Float64 has `Equal` and `LessThan` only, while Int64 also has `LessThanOrEqual`, `GreaterThan` and `GreaterThanOrEqual`. Two pairs of functions call each other, `dumpsG` with its item and member callbacks, and probe 10's `pyRepr`. Each cost one placeholder body and one replacement.

## Results vs Python

304 cases. A case matches when its output line is equal and the `Host` calls are the same list, including the text of each write.

| Group | What it covers | Result |
| --- | --- | --- |
| `snapshot` (82) | CRLF, CR-only, no final newline, a BOM, blank and garbage lines, side chains with truthy and falsy flags, thinking blocks, compact summaries, `queued_command` attachments (empty, missing, list and number prompts), a non-dict attachment for each JSON type, an unhashable `type`, every content shape. Text blocks holding numbers, lists, `0`, `False`. Tool inputs with each of the six keys, non-string values, `None`, lists, duplicate keys, `NaN`/`Infinity`/`1e400`, and `ensure_ascii=False` escapes. Result parts that are not strings for each JSON type, and `is_error` values. Clipping at 299–302 and 599–601 characters, all 29 whitespace code points, ZWSP (not whitespace), astral characters. Byte budgets from 100,000 down to 0, results only, one entry, multibyte text, exactly fitting and one byte short. Lone surrogates in kept, clipped-away, dropped and escaped text | 81/82 (1 known) |
| `key` (11) | Falsy and non-string parts, containers, floats, Unicode, and `repr` of 23 unprintable and printable strings | 11/11 |
| `cooldown_seconds` (87) | Each alternative in either case, `rate[ _-]?limit` variants, `\b` beside letters, digits, `_`, `é`, full-width and Arabic-Indic digits, superscripts, combining marks, NBSP, CJK. `50[0234]` against 501, 505, 5000, 5503, `50 3`. `İ`, `ı`, Kelvin sign and long s, `ß` and full-width letters (no match). Integer, float, list and dict errors through `str()` | 87/87 |
| `cooling` (36) | Missing, empty, invalid, list and BOM files; `until` as int, float, bool, string, null, `NaN`, `±Infinity`, or missing; equal to now; reason missing, empty, `0`, a number, a dict; duplicate keys; clock reads | 35/36 (1 known) |
| `record` (45) | New cooldowns, no cooldown, `completed` with and without an entry, replacing an entry in place, dropping expired and non-dict entries, `TypeError` for each non-number `until` and which one comes first, reasons cut at 300 code points (including astral), `str()` of dict, list and number errors, status values, lone surrogates kept or expired, nested and float values in `indent=2` output | 44/45 (1 known) |
| `available` (11) | Empty and single orders (no reads), some or all cooling, a missing file, clock reads | 11/11 |
| `record_run` (32) | Missing and invalid summaries, every non-dict summary, `attempts` of each JSON type, non-dict items before and after the leader, two leaders, missing status or error | 32/32 |
| **Total** | | **301/304** |

The 3 known divergences:

- `123456789012345678901` in a tool input is a Python `int` beyond Int64. The port cannot parse the line and skips it, as in probes 9 and 10.
- `until` = 2<sup>53</sup> + 1 against now = 2<sup>53</sup>, in `cooling` and in `record`. Python compares an `int` and a `float` exactly, so the entry is still cooling. `Int64ToFloat64` rounds it to 2<sup>53</sup>, so the port treats it as expired. An exact comparison would need Float64 to Int64 conversion, which is deferred. A cooldown time is a Unix time plus at most 3,600 s, so real files never hold such values.

All 304 cases took 45 runs and about 200 s. Transcripts are large, so batches were limited by size rather than by the 40-case limit.

## Scale

Each transcript turn was three lines: a user request, an assistant text and a Bash `tool_use` with about 1.1 KB of command, and a `tool_result` of the same size. The text was generated inside the program. Times are `runPure` wall time, and about 2.8–4.5 s of each is the build and process start. The default `max_bytes` is 200 KiB.

| Input | Python | Port |
| --- | --- | --- |
| 1 turn (2.8 KB) | 0.2 ms | 4.0–4.5 s |
| 100 turns (0.28 MB) | 4 ms | 3.8 s |
| 400 turns (1.1 MB) | 20 ms | 4.8 s |
| 1,000 turns (2.8 MB) | 70 ms | 6.6 s |
| 1,500 turns (4.2 MB) | 83 ms | 8.4 s |
| 2,000 turns (5.6 MB) | 112 ms | `RunTimeout` (5 s limit) |
| 400 / 1,000 turns with `max_bytes` 20,000 (tail path) | 27 / 68 ms | 5.7 / 6.9 s |
| `cooldown_seconds`, `x429 ` × 4,000 (20 KB, 4,000 `\b` checks) | 3.6 ms | 5.4 s |
| `cooldown_seconds`, `word ` × 4,000 (20 KB, no digits) | 3.4 ms | 3.5 s |

Every row gave the same output as Python. The transcript cost grows about linearly: roughly 1.2 s per MB above the build. *Inference:* most of that is the scanner's fold over every code point, and since Claude Code transcripts of long sessions can reach several MB, the largest ones would exceed the run limit.

## Costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **Python exceptions written as data** | 8 Python exception messages (some with a type name or position filled in) are reproduced as `Err` values. 4 functions and 129 nodes build the messages, and 138 `Result` nodes (8% of the new 1,787) carry them. Every fold whose Python loop can raise needs a `Result` accumulator and a pass-through arm, because a fold cannot stop | None queued. Probe 6 recorded the pass-through cost as small; here it is spread over every fold that reads untrusted JSON |
| 2 | **A Unicode property lookup parses its whole table** | Exact `\w` and `str.isprintable` from two 8 KB literals, 4 functions and 32 nodes. Each lookup costs about 0.3 ms, and 4,000 lookups used half the run limit | None queued. Revisit if a port needs a property for every character of a long text |
| 3 | **Transcripts above about 4 MB exceed the run limit** | 4.2 MB ran in 8.4 s wall time; 5.6 MB timed out. Python took 0.1 s | None; the limit belongs to `runPure`, and the program is linear |
| 4 | **No Float64 `<=`, `>` or `>=`** | The one port bug: an expanded `<=` evaluated its effectful operand twice | Possibly `Float64LessThanOrEqual` (and `GreaterThan`) to match Int64. None queued, since binding the operand is enough |
| 5 | **A backward loop with a break** | `reversed(lines[1:])` with `break` became a forward fold that drops from the front: 2 functions, 67 nodes, with a note on why it is equivalent | None |
| 6 | **Integers beyond Int64** | 3 known divergences | None, as in probes 9 and 10 |

The regular expressions cost less than expected: 7 functions and 224 nodes, with `IGNORECASE`'s four non-ASCII folds found by testing Python and not by reading its source. Map was not needed. The cooldown file is a dict of at most a few entries, and `upsert`, `ListAny` and `ListFilter` over its members were enough. Function groups were not needed either: 2 cycles cost 2 placeholder transactions.
