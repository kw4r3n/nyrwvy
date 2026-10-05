# rrawrlet slice probe 19 — 2026-10-01

**Status:** Complete. rrawrlet's `web_live_requests.py` was authored by name only, together with the body step of `web.WebHandler._read_json` that feeds it. The port covers `request_text`, `check_keys`, `check_revision`, `fixed_options` and `build_args`, with `checkout.normalize_exclusions`, `RunRequest.validate` and `to_args` (over `run_options.run_defaults`), `leader.validate_leader` for a Claude Leader and `claude_backend.validate_selection`. The body step is the 64 KiB cap, strict `body.decode("utf-8")`, `json.loads`, the lone-surrogate check and the dict check. The home lookup of `Path.expanduser` and `Path.resolve` are a 2-operation `Host` capability. The port matched the real Python on **2,762/2,775 cases on its first full run**, including all 903 host calls in order. The other 13 are 8 known divergences of one kind and 5 cases that exceed a `runPure` argument or result limit; 10 cases that build those bodies inside the program all match. There were no port bugs. **Nothing is blocked and nothing is queued.**

This is the first probe after orders 32 and 33. `ProductUpdate` replaced three record rebuilds, which saved 27 nodes. `StringDecodeUtf8Lossy` was enough for strict decoding here, because `_read_json` only needs to know whether decoding failed: the check is 2 functions and 20 nodes. Probe 16 wrote strict and `surrogateescape` decoders by hand in 429 nodes. `matchUnion` needing every arm came back a fifth time (295 nodes, 7.8% of the port), and Python's unbounded `int` a second time.

**Date:** 2026-10-01

Baseline: nyrwvy `a2736ba` (main after #285, clean); rrawrlet `9aeb6c3` (clean). The session server was built with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release` on .NET SDK 10.0.112. The oracle ran rrawrlet's real functions on Python 3.11.15.

The method was probe 18's: the `Nyrwvy.AuthoringSession` JSON-lines protocol with name-resolved requests, and `runPure` batches through a `runCases` entry. Probe 11's session client and recipe helpers were reused unchanged. Probe 9's JSON helpers and `json.dumps` (with probe 18's `Big`), probe 15's `json.loads` parser, probe 11's `str.isprintable` table and probe 17's paths were reused as source. The harness, cases, snapshot and transcripts are in private files outside the repository (`rrawrlet-probe19/`) and are not committed.

## Why this part

The two modules no probe had covered were `web_live_requests.py` and `codex.py`. `codex.py` is a JSON-RPC client around a child process: spawning, process groups, `/proc`, pipe EOF and asyncio cancellation. Nearly all of that would sit on the host. `web_live_requests.py` is the WebUI's trust boundary: every HTTP value is checked there before it reaches a run. With `_read_json` in front, it is a whole path from request bytes to run arguments, and it uses both new constructs:

- **A record built from defaults and then partly overwritten.** `to_args` takes `run_defaults()` (argparse's defaults: 31 for `run`, 29 for `chat`) and sets 30 attributes on it. That is a product update.
- **Bytes from HTTP.** `body.decode("utf-8")` is strict, and any `UnicodeError` becomes `Invalid JSON request`.
- **`json.loads` in full.** Python's big integers, `NaN` and `Infinity`, duplicate keys, escaped surrogate pairs and lone surrogates, a BOM, and the 4,300-digit limit on `int`.
- **Python's numeric tower at the edges.** `type(x) is int` excludes `bool`. `math.isfinite` of an `int` beyond 2^1024 − 2^970 raises `OverflowError`, which the code turns into a validation error.
- **Ordering by code point.** `check_keys` reports `sorted(unknown)[0]`, and exclusions are `sorted(set(...))`. Characters above U+FFFF sort after U+FFFF.
- **`{value!r}`** in an error message, so Python's `repr` with `str.isprintable`.

## What was ported

| Python | Port |
| --- | --- |
| `_read_json` after the headers | `readJson(body)`: the body is one code point per byte; over 65,536 bytes gives 413; then strict decode, `loads`, the surrogate check and the dict check, each failing as Python does |
| `body.decode("utf-8")` | `decodeStrict`: `StringDecodeUtf8Lossy`, then success only when the result has exactly as many U+FFFD as the body has `EF BF BD` sequences. `EF` never continues a sequence, so every encoded U+FFFD decodes to itself and any ill-formed subpart adds one more |
| `json.loads` | probe 15's parser (a fold over code points with a `Parser` state). An integer that does not fit Int64 is now a `Big` with its digits, unless it has more than 4,300 digits |
| `json.dumps(value, ensure_ascii=False).encode("utf-8")` | `jsonHasLone`, a walk over keys and strings for an unpaired surrogate |
| `request_text`, `len(value.encode("utf-8"))` | `requestText`: lone surrogate first, then `StringUtf8ByteCount` |
| `check_keys` | `ListFilter` of unknown keys, `ListSortBy` with `StringLessThan`, first item |
| `check_revision`, `max_members`, `max_total_tokens` | `nonNegInt` and `posInt` over `Int` and `Big`; a `Big` never equals the Int64 revision |
| `attempt_seconds` with `math.isfinite` | `secondsOk`: `Int` > 0, `Float64IsFinite` and > 0, or a positive `Big` whose digits sort below 2^1024 − 2^970 (309 digits) |
| `fixed_options` over `getattr` | `fixedOptions` over a JSON object; `{"$path": s}` in a case stands for `Path(s)`, so `str` and `os.PathLike` both occur |
| `normalize_exclusions` | a fold returning `Result`, `PurePosixPath` parts by splitting, then sort and dedupe; the message uses `reprStr` with probe 11's exact `isprintable` table |
| `RunRequest.validate`, `claude_backend.validate_selection`, `leader.validate_leader` | `validateReq`, `validateClaude` (`claude-[a-z0-9.-]+` by `StringSpan`), `validateLeader` |
| `run_defaults()` then `setattr` in `to_args` | `runDefaults(interactive)` constructs an `Args` record of 37 fields; `toArgs` is one `ProductUpdate` of 30 of them |
| `Path(repo).expanduser().resolve()` | probe 18's `expandUser` over `Host.userHome`, then `Host.resolve: Result<String, Exc>`; a `ValueError` from it becomes a `LiveRequestError` as in Python, and `RuntimeError` or `OSError` escape |

Case outputs are one String: `json.dumps(vars(args), sort_keys=True, ensure_ascii=True, default=str)` of the result, or `LiveRequestError <status>: message`, or `TypeName: message` for another exception.

Out of scope: the HTTP headers (`Content-Type`, `Content-Length`, chunking), the controller in `web_live.py` that calls these functions, and what `resolve` does on disk. Python's `RecursionError` for JSON nested about 1,000 deep is also out of scope: the port's parser has no recursion limit, so it would accept such a body. The cases nest at most 300 deep.

## What was authored

| Part | Functions / nodes |
| --- | --- |
| Types | 13 in 12 requests (`Json` with `Big`, and `Member`; probe 15's `Frame`, `Mode`, `Parser`, `NumParts`; `Exc` with a status, `Opts`, `Sel`, `Nums`, `Req`, `Args` with 37 fields, `Case`); 1 effect; `Host` with 2 operations |
| JSON helpers (14 / 133), `json.dumps` (11 / 245), `json.loads` (28 / 812) | 53 / 1,190 |
| Strings, paths, `repr` | 22 / 303 |
| `_read_json` body step | 5 / 84 (`decodeStrict` 14, `countOf` 6) |
| `request_text`, `check_keys`, `check_revision` | 6 / 229 |
| `fixed_options` | 7 / 224 |
| `build_args` checks, exclusions, Sidekick, `validate`, `validate_selection` | 16 / 844 |
| `run_defaults`, `to_args`, `validate_leader`, `build_args`, the output | 8 / 597 (`argsJson` 189, `buildArgs` 139, `toArgs` 137) |
| Case dispatch, including the in-program body builder | 13 / 323 |
| **Total** | **130 / 3,794** in 147 proposals |

There was **one recipe rejection**: an arm read the `Big` variant's field as `value` instead of `digits` (`ProtocolError: Unknown field on variant Big`). The port did not change after the first full case run.

## Results vs Python

2,775 cases. The oracle ran the real function once per case. Python's `pathlib`, `json` and `math` ran for real, and only the two host operations were scripted and recorded. A case matches when its output is equal and the port made the same host calls with the same arguments in the same order.

| Group | What it covers | Result |
| --- | --- | --- |
| `request_text` (168) | `None`, `""`, NUL, ASCII, 2-, 3- and 4-byte characters, a lone high or low surrogate, a surrogate pair held as two code points, non-strings, U+3000; required or not; limits 0, 3, 4 and 10 bytes | 160/168 |
| `check_revision` (98) | Missing, `None`, 0, 1, 5, −1, `True`, `False`, `1.0`, `"1"`, `[]`, 2^63, 2^80, −2^70, `Int64.MaxValue`; current 0, 5 and `Int64.MaxValue`; required or not | 98/98 |
| `fixed_options` (129) | Each option missing, `None`, a string, a `Path` (including `Path("")` and `Path("/")`), numbers, `True`/`False`, `0`, `0.0`, `-0.0`, a list, a dict; workspace values and falsy values; the order of the checks | 129/129 |
| `build_args`, hand-written (260 as a payload, 258 as a body) | Every field's type errors; unknown keys including U+FFFF against an astral key; the prompt, repo and selector byte limits with 3-byte characters; task mode and delegation aliases and nulls; integers and floats around every bound, 2^63, 2^200, the float overflow bound ±1, `inf`, `nan`, `-0.0`, `5e-324`; final formats; 25 exclusion lists (absolute, `//`, `..`, `.`, NUL, duplicates, `'` and `"` in the repr, `\x7f`, U+00AD, U+200B, a tag character, an unassigned code point, an astral character, a lone surrogate); leader and Sidekick backends with every model, effort, provider and variant rule; `~`, `~/a`, `~u/a`, a failing home lookup, relative and `//` repos, and `resolve` raising `RuntimeError`, `OSError` or `ValueError`; output paths; options in the build | 515/518 |
| Bodies (52) | Empty, whitespace, non-objects, a BOM, a trailing byte `FF`, a truncated sequence, an encoded surrogate, an encoded U+FFFD, `F4 90`, an overlong `C0 AF`, escaped lone surrogates in a value and a key, an escaped pair and a reversed one, duplicate keys, `NaN`, `±Infinity`, `1e400`, `-0`, `01`, 4,300 and 4,301 digits, raw tab and NUL escapes, 20 and 300 levels of nesting, trailing data, single quotes, `tru`, `\x41`, `1.`, `-`, `1E+2` | 50/52 |
| Bodies at the cap (10) | Built in the program: exactly 64 KiB and one byte more, in ASCII, 3-byte UTF-8, `€` escapes and invalid bytes, and a 513-byte model | 10/10 |
| Random (900 as a payload, 900 as a body) | A valid start with 0 to 5 fields replaced by random values, random Sidekick selections, options and resolved repos | 1,800/1,800 |
| **Total** | 726 builds return arguments; 1,642 fail with a `LiveRequestError` and 12 with another exception (`RuntimeError`, `OSError`, and `ValueError` from `fixed_options`) | **2,762/2,775** |

The 2,775 cases took 78 runs and 40 s (about 7 s for the first run, which includes the session's first build, then about 0.4 s each). Wrong expected outputs (U+FFFF swapped for an astral key, a changed error, `\x7f` unescaped in the repr) and a reordered call list were all reported as failures, so the comparison is not vacuous.

**Known divergence (8 cases).** A Python `str` can hold a high and a low surrogate as two separate code points, and then `encode("utf-8")` fails. A Nyrwvy String is UTF-16, so the same two units are one astral character, which is valid. This only happens when Python code builds such a string directly. Through HTTP it cannot: `json.loads` joins an escaped pair into one character itself, and the port agrees with Python on every escaped pair and reversed pair in the bodies. Probe 15 recorded the same class of divergence.

**Harness limits (5 cases).** Each `runPure` String argument, each argument value in total, and the result are limited to 65,536 UTF-16 units. rrawrlet's body cap is also 64 KiB, so a body one byte over the cap cannot be passed in, and a 64 KiB prompt echoed back in the output exceeds the result limit. The `cap-*` cases build the same bodies inside the program by doubling, and return the output's length with its first and last 200 characters. All 10 match, including the 413 one byte over the cap.

**Both product forms give the same results.** `author.py construct` writes the three update sites as `ProductConstruct`, as probe 15 and the original `to_args` port would have. That port also matched 2,762/2,775.

## Scale

Warm `runPure` wall time of one case on the snapshot, including process start. Every row matched Python.

| Input | runPure | Python |
| --- | --- | --- |
| 26-byte body | 0.71 s | 1.2 ms |
| 64 KiB body, ASCII prompt | 0.71 s | 2.3 ms |
| 64 KiB body, 3-byte UTF-8 prompt | 0.64 s | 1.5 ms |
| 64 KiB body, `€` escapes | 0.66 s | 2.1 ms |
| 64 KiB body, invalid UTF-8 | 0.53 s | 0.02 ms |
| 64 KiB body, 13,000 equal exclusions (sort and dedupe) | 0.67 s | 42 ms |

The fixed cost of a run dominates every row. The module's own caps keep inputs at 64 KiB, so no limit of the language is near.

## Costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **`matchUnion` needs every arm** (fifth observation) | The port's own 20 matches over the 8 `Json` variants restate a default body in 133 arms: 295 nodes, 7.8% of the port (`measure.py`). Probe 15's parser adds more: `finish` lists all 14 `Mode` variants to change 2 | None queued. A default arm trades away exhaustiveness, and the [foundational decisions](../decisions/foundational.md) say wildcard branches must not absorb unknown domain cases. It needs a decision first |
| 2 | **Python's unbounded `int`** (second observation) | `max_members`, `max_total_tokens` and `expected_revision` accept any non-negative Python `int`, and `json.loads` produces one from any 4,300 digits. `attempt_seconds` depends on whether the `int` converts to a float. Writing this out cost about 100 nodes: `nonNegInt`, `posInt`, `bigFloats`, the `Big` rule in `numberJson`, and 17 `Big` default arms (41 nodes) | None. Two small observations (18, 19). A bounded Int64 is a deliberate subset choice |
| 3 | **Python library behaviour by hand** | `json.loads` (812 nodes, reused), `json.dumps` (245), `repr` with `isprintable` (103), `PurePosixPath` parts and normalisation | None. Each was reused from an earlier probe without change, apart from `Big` |

**Effect of orders 32 and 33.**

- `ProductUpdate` was used in three places: `toArgs` (150 → 137 nodes), and probe 15's parser copies `mk` (10 → 5) and `stepAt` (19 → 10). That is 3,821 → 3,794 nodes in total, 0.7%. `toArgs` gains little, because it sets 30 of the 37 fields. The other gain is maintenance: `toArgs` names only the fields `to_args` sets, so a new argparse default needs no change there. A `ProductConstruct` would have to list it.
- `StringDecodeUtf8Lossy` was enough for strict decoding. `_read_json` maps every `UnicodeError` to the same message, so it needs only success or failure, not Python's error text. Counting U+FFFD before and after gives that in 20 nodes. The decision note's revisit condition for strict decoding asks for a consumer that needs the strict decode or its error text from a module that cannot leave decoding to the host. This module needs the strict decode, but the existing builtin covers it, so the condition is not met in a way that calls for a new builtin.

Not needed this time: `Float64Round` (floats are only compared), Map (payloads have at most 25 keys, and lookups are list folds), tail calls (every loop is a fold; the harness-only `rep` recurses log₂ n deep).

**An observation about rrawrlet (inferred from the code, confirmed by the cap cases).** `MAX_PROMPT_BYTES` and `MAX_JSON_BYTES` are both 64 KiB, and a body always contains the prompt plus at least 23 bytes of framing. So `prompt is too long` cannot be reached through HTTP; an oversized prompt gets 413 from `_read_json` first. The check still matters for direct callers of `build_args`.

## Candidate hit counts after probe 19

| Candidate | Probes |
| --- | --- |
| Product update node | 7, 10, 15, 17; done as order 32, used in 19 |
| `matchUnion` default arm (needs a decision) | 7, 10, 17, 18, 19 (5) |
| UTF-8 decode builtin | 14, 16, 18; done as order 33 (`replace` only), used in 19 for a strict check |
| `Float64Round` | 15, 17 (2) |
| Map | 16 strong, 13 weak |
| Tail self-call as a loop | 15 |
| Unbounded `int` | 18, 19 (2, small) |

## Reproducing

The scripts are in private files outside the repository under `rrawrlet-probe19/`. `author.py <snapshot> [construct]` authors the port (it execs `jsonpart.py` and `parserpart.py`) and writes `author_stats.json`, or `author_stats_construct.json` for the `ProductConstruct` form. `oracle.py` runs `rrawrlet.web_live_requests` and `web.WebHandler._read_json` with the scripted host and records the calls; it needs an rrawrlet checkout at a fixed local path. `mkcases.py [out.json]` builds the 2,775 cases. `run_cases.py <snapshot> cases.json known.json` runs them. `scale.py <snapshot>` gives the scale rows, and `measure.py` counts the default arms without a server. `nw.py` and `dsl9.py` are probe 11's client helpers, unchanged. Run the scripts with `PYTHONPATH=rrawrlet-probe19` and `NW_BIN` set to the built `Nyrwvy.AuthoringSession`.
