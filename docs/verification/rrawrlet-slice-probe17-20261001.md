# rrawrlet slice probe 17 — 2026-10-01

**Status:** Complete. A part of rrawrlet that no earlier probe had touched was authored by name only: all of `verify.py`, which runs operator-registered checks at a finished run's result commit and saves `verification.json`. That covers `check_spec` with command strings through `shlex.split` and `@file` JSON checks, `checks_path`, `registered_checks`, `run_source`, `_clean_at`, `_run_check` with its `try`/`except`/`finally`, `overall`, `verify_run`, `check_submission`, `verification_view` and `saved_verification`, plus `results.saved_result`, which `verify_run` calls first. `load_run`, Git, `Path.resolve`, `Path.home`, `os.environ`, `Path.is_file`, reading a JSON file, the clock, `NativeCodex` and `write_json` are a 14-operation `Host` capability. The port matched the real Python on **1,094/1,094 cases**, including all 3,999 host calls in order. The port itself was not changed after its first build. There are no known divergences inside the scope below. **Nothing is blocked and nothing is queued.**

No cost limits reach. The three recurring costs came back: `matchUnion` needs every arm (298 nodes, 11% of the port, a third observation after probes 7 and 10), products are restated in full to change one field (about 68 of `shStep`'s 183 nodes, after probes 7, 10 and 15), and `round(x, 1)` needed probe 15's hand-built rounding again (172 nodes). Map, a UTF-8 decoder and tail calls were not needed.

**Date:** 2026-10-01

Baseline: nyrwvy `601dfe4` (main after #279, clean); rrawrlet `4276240` (clean). The session server was built with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release` on .NET SDK 10.0.112. The oracle ran rrawrlet's real `verify` functions on Python 3.11.15.

The method was probe 16's: the `Nyrwvy.AuthoringSession` JSON-lines protocol with name-resolved requests and no `declarations` call, and `runPure` batches through a `runCases` entry. Probe 11's session client and recipe helpers were reused unchanged. Probe 9's JSON helpers and `json.dumps` and probe 15's `round1` were reused as source. The harness, cases, snapshot and transcripts are in private files outside the repository (`rrawrlet-probe17/`) and are not committed.

## Why this part

Probe 16 called `verify.py` a thin caller of `results.py` and a model backend. Ported whole, it exercises what probes 4 to 16 left out:

- **A shell lexer both ways.** A registered check given as a string is `shlex.split` in POSIX mode: quotes, backslash escapes that mean different things inside double quotes, empty quoted words, and the two `ValueError`s at end of input. The command sent to the sandbox is `shlex.join` of the argv, with `shlex.quote`'s ASCII-only safe set.
- **Validation of loosely typed JSON with Python's type rules.** `type(x) is not bool`, `type(seconds) not in (int, float)` (so `True` is not a timeout), `math.isfinite`, `str.strip()` over Unicode whitespace for the first argv item, a key set difference, and defaults that apply only to missing keys.
- **Exceptions as control flow.** `_run_check` catches `TimeoutError` before `(OSError, ValueError, RuntimeError)`, although `TimeoutError` is an `OSError`; its `finally` closes the client, can overwrite the status, and can replace an exception that is still propagating. `verify_run` catches only those three families, so a `KeyError` from a missing `exitCode` escapes without writing a record.
- **Dict order.** `result.update(outcome)` keeps existing keys in place and appends new ones, and the record written to `verification.json` is compared as text.
- **Python's own errors on bad records.** `verification_view` of a corrupted `verification.json` iterates whatever `checks` holds and slices whatever `output` holds, so a dict, string, number or list there gives Python's `AttributeError` or `TypeError` text, or a list tail.

## What was ported

| Python | Port |
| --- | --- |
| `load_run`, `checkout_git` | `Host.loadRun(value): Result<Run, Exc>` with `Run {path, metadata: Json, summary: Json, finished}`; `git(cwd, argv): Result<String, Exc>`, argv NUL-joined. `Exc {type, text}` is an exception's class name and `str(exc)` |
| `Path.resolve`, `Path.home`, `os.environ.get`, `Path.is_file` | `resolve(path): Result<String, Exc>`, `home()`, `env(name): Option<String>`, `isFile(path)` |
| `json.loads(Path(p).read_text(encoding="utf-8"))` | `readJson(path): Result<Json, Exc>`: reading, decoding and parsing stay on the host, as in probe 16. Floats are `Float64` |
| `time.time`, `time.monotonic` | `now()`, `monotonic()` |
| `NativeCodex` | `codexStart(directory)`, `codexThread(cwd, gitDir, readOnly)`, `codexShell(command, timeout: Json): Result<Json, Exc>`, `codexClose()`, one call each per check |
| `write_json` | `writeJson(path, text)`; the port writes `json.dumps(record, ensure_ascii=False, indent=2) + "\n"` itself, so the host only encodes the text (a lone surrogate there is Python's `UnicodeEncodeError`) |
| `shlex.split(s)` | A `ListFold` over code points with state `Sh {mode, esc, token, quoted, out}`, then the end-of-input rule (`No closing quotation`, `No escaped character`) |
| `shlex.join`, `shlex.quote` | `StringSpan` over the 72 safe ASCII characters, then `'…'` with `'` as `'"'"'` |
| `check_spec` | `specOf` returns `Result<Spec, Exc>` with `Spec {argv, timeout: Json, preflight: Option<List<String>>, readOnly, required}`; the timeout keeps its JSON type because the record writes it back as `60` or `1.5` |
| `math.isfinite(x) and x > 0` | `x - x == 0.0` and `0.0 < x` over `Float64` |
| `str.strip()` non-empty | `StringSpan` over the 29 code points `str.isspace()` accepts |
| `str(Path(…))`, `Path / name` | probe 16's `pnorm` and `pjoin` |
| dict comprehension `{resolve(k): v}` then `.get(source)` | resolve every key in order, then a `ListFold` that keeps the last member with the key: the dict's last-assignment-wins rule |
| `isinstance(e, (OSError, ValueError, RuntimeError))` | membership of `Exc.type` in fixed lists of the classes that can reach the handler (16 `OSError` classes including `TimeoutError`, 5 `ValueError`, 4 `RuntimeError` ones including rrawrlet's `CodexError`) |
| `try`/`except`/`finally` in `_run_check` | `checkTry` (start, thread, shell), `afterTry` (update, `exitCode` or the handlers; `Err` is an exception still propagating), `afterClose` (the `finally`: a caught close failure updates the result but leaves a propagating exception in place, an uncaught one replaces it) |
| `result.update(...)`, `record["x"] = …` | probe 9's `upsert` over the object's members, which keeps an existing key's position |
| `round(elapsed, 1)` | probe 15's `round1` |
| `for check in record.get("checks", [])`, `output[-3000:]` | `iterJ` (a list, a dict's keys, a string's characters, or `TypeError: 'int' object is not iterable`) and `tail` (a string or list tail, `'int' object is not subscriptable`, `unhashable type: 'slice'`) |

Case outputs are one String: `json.dumps(…, ensure_ascii=False)` of what the function returned, `shlex.join`'s text, or `TypeName: message` for an exception that escapes.

Out of scope: what the host does. That is the sandbox and process handling inside `NativeCodex`, JSON parsing and file decoding, and Git itself. Git's answers are treated as text: `_clean_at`'s `.decode()` of `rev-parse` output is not ported because a commit name is ASCII. Also out of scope are integers beyond Int64 in check files, and `BaseException` subclasses such as `CancelledError`, which the harness does not raise.

## What was authored

| Part | Functions / nodes |
| --- | --- |
| Types | 8 in two requests (`Json` and `Member` as one recursive group; `Exc`, `Run`, `Saved`, `Spec`, `Sh`, `Case`); 1 effect; `Host` with 14 operations |
| JSON helpers and `json.dumps` (probe 9), and value helpers | 34 / 434 |
| `round(x, 1)` (probe 15) | 4 / 172 |
| Strings, paths and exception classes | 10 / 171 |
| `shlex.split`, `shlex.join` | 5 / 263 (`shStep` 183) |
| `check_spec` | 14 / 309 (`specOf` 115) |
| `checks_path`, `registered_checks`, `run_source` | 5 / 231 (`registeredIn` 124) |
| `saved_result` | 3 / 174 |
| `_clean_at`, `_run_check`, `overall` | 9 / 251 |
| `verify_run`, `check_submission` | 6 / 244 |
| `verification_view`, `saved_verification` | 7 / 315 |
| Case dispatch | 8 / 240 |
| **Total** | **105 / 2,804** in 110 proposals |

There were **no recipe rejections** and the authoring script ran clean the first time. No function needed the placeholder route for recursion except probe 9's `dumpsG`, as before.

## Results vs Python

1,094 cases. The oracle ran the real function once per case in a scripted world: real files under `/tmp/v17/<id>/` for check files and `verification.json`, and scripted `load_run`, Git, clock, `NativeCodex` and `write_json`. Python's own `pathlib` calls from `verify` and `results` were recorded as they happened. A case matches when its output is equal and the port made the same host calls with the same arguments in the same order: 3,999 calls, 1,219 of them Git.

| Group | What it covers | Result |
| --- | --- | --- |
| `shlex.split` (248) | 48 hand-written strings: empty and blank input, quotes of both kinds, `''` as a word, escapes outside, inside double and inside single quotes, a trailing backslash, backslash-newline, `\x0b` and U+3000 (not shell whitespace), NUL, astral characters; 200 random strings over 14 characters. 98 end in Python's `ValueError` | 248/248 |
| `shlex.join` (119) | Empty strings, `'` inside a word, `ü` (outside the ASCII safe set), the safe punctuation, NUL, newline; 100 random lists | 119/119 |
| `check_spec` (287) | Every validation branch in order: unknown keys, `required` as `1`, `"true"` or `null`; argv empty, `[""]`, whitespace-only including U+3000 and U+001C, a non-string item, a NUL; timeouts `0`, `-1`, `0.0`, `-0.0`, `5e-324`, `NaN`, `±inf`, `True`, `"5"`, `null` and `Int64.MaxValue`; `preflight_argv` `null`, `[]` and invalid; `read_only_paths` relative, `""`, a string instead of a list, `/` and `//b`. `@` files: missing, a directory, invalid JSON, empty, non-UTF-8, a list, a string, `NaN`, `Infinity`, `1e999`, duplicate keys, CRLF, a BOM, escapes to a surrogate pair, relative paths resolved from the working directory, `@` alone and `@ok.json/`. 200 random specs | 287/287 |
| `registered_checks` (37) | A key that is the source, its symlink, `src/` and `//src/./`, so several keys resolve to the same path and the last one wins; `""` and `.` as keys; a config that is a list, a number or invalid JSON, non-UTF-8; checks that are a string, an object, `null`, or hold an invalid spec; no source; the default path from `XDG_CONFIG_HOME`, an empty `XDG_CONFIG_HOME`, a relative one, and `HOME` with trailing slashes | 37/37 |
| `verify_run` (157) | 22 run records (unfinished, no or a non-string repository, `preparation` a string, a list, `0`, `null` or `{}` with and without `base`, short, uppercase, 64-character and integer commits, a result with a newline, an empty, integer or relative source); `load_run` and each Git step failing; 28 codex scripts for both checks (exit codes `1`, `None`, `-9`, `0.0` and `False`, no `exitCode`, an outcome that overwrites `status` and `argv`, timeouts, each exception family at start, thread and shell, close failures that are caught, that follow a timeout, and that replace a propagating `KeyError`); a dirty tree before or after, a moved `HEAD`, `HEAD` with Unicode whitespace, Git raising `TimeoutError` (an `OSError`) and `KeyError`; registered checks through `XDG_CONFIG_HOME` and `checks_file`; write failures and a lone surrogate reaching `write_json`; 12 checks in one run; 76 random | 157/157 |
| `check_submission` (118) | The same codex scripts and clean states, a Git directory, workspace and directory paths with `//` and `.`, no specs, an invalid spec, a write failure; 74 random | 118/118 |
| `verification_view` (115) | `None`, `[]`, a string and a number as the record; `checks` as `null`, a number, a string, `""`, a dict, `{}`, and lists holding non-dicts; outputs over 3,000 code points with astral characters, exactly 3,000 and 3,001, a list of 3,005 items, a dict, `True`, `0.5`, `""` and `0`; `error` falsy and truthy; 80 random | 115/115 |
| `saved_verification` (13) | A valid record, invalid JSON, an empty file, non-UTF-8, `[1]`, `null` (so `None` is returned), `checks` a number, CRLF, a directory named `verification.json`, a missing file, relative and dotted paths, `""` | 13/13 |
| **Total** | | **1,094/1,094** |

The 1,094 cases took 28 runs and 16.2 s (5.5 s for the first, which includes the session's first build, then 0.3–0.5 s). Two harness faults were fixed before this count; neither touched the port. The oracle's scripted Git encoded its output as latin-1 and failed on U+3000; a real Git answer is UTF-8 bytes. And one view case, a list of 3,005 integers, alone exceeded the 8,192-value-node limit on run values that probe 16 met; it became 3,000 `null`s and 5 integers.

A deliberately wrong expected output and a reordered expected call list were both reported as mismatches, so the comparison is not vacuous. 239 checks in the cases report an `elapsed_seconds`. In addition, `round1` was run against Python's `round(x, 1)` on 4,000 random monotonic differences, 735 of them with a second decimal of 5, with no mismatch (`test_round.py`).

## Scale

`runPure` wall time on a copy of the snapshot, including the build and process start (0.3–0.5 s for a trivial entry after the first build):

| Input | Time |
| --- | --- |
| `shlex.split` of 16,000 / 64,000 characters | 0.27 / 0.45 s |
| `shlex.join` of 1,000 arguments | 0.31 s |
| `verification_view` with a 24,000 / 60,000-unit output | 0.31 / 0.27 s |

Every row is linear. `shlex.split` builds each token with one `StringConcat` per character, which order 31 made linear. `shlex.join` of 4,000 arguments gives 66,889 UTF-16 units, over the 65,536-unit output budget. That is a harness bound, not a language one. The real `NativeCodex` keeps at most the last 24,000 bytes of output.

## Costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **`matchUnion` needs every arm** (third observation) | 25 matches over the 7 `Json` variants restate a default body: 298 nodes, 11% of the port. The largest are `registeredIn` (80 nodes: two `ValueError` bodies, each written out for 6 variants), and `iterJ` and `tail` (27 each, Python's `TypeError` text). Probe 7 recorded about 45 arms and probe 10 257 nodes (10%) | None queued. As probe 10 said, a default arm trades away exhaustiveness when a variant is added, and the [foundational decisions](../decisions/foundational.md) say wildcard branches must not absorb unknown domain cases. It needs a decision first |
| 2 | **Products restated to change one field** | `shStep` builds `Sh` in 11 places. About 34 of those field values are copies of the input's own fields, about 68 of its 183 nodes. Recorded after probes 7 and 10, and as probe 15's candidate 3 (about 550 nodes, 2 node-limit rejections) | None queued. Probe 15's candidate: a product update node. Here it cost nodes but no rejection and no extra request |
| 3 | **`round(x, 1)` by hand, again** | `elapsed_seconds = round(time.monotonic() - started, 1)` in every check record. Probe 15's `round1` (4 functions, 172 nodes, 6% of the port) was reused unchanged and matched Python on 239 recorded values and 4,000 random ones | None queued. Probe 15's candidate `Float64Round(value, digits)` with Python's rule. This is the second program that needs it, after probe 15 (probe 3 rounded on the harness side). `Float64ToInt64` was not needed |
| 4 | **Python's exception classes by name** | `isinstance` against `(OSError, ValueError, RuntimeError)` is membership in fixed lists (2 functions, 54 nodes). `try`/`except`/`finally` became three functions that pass a propagating exception along as `Err` (`afterTry` 59, `afterClose` 25) | None. The lists name the classes that can reach the handlers. A class the host invents outside them would be treated as uncaught |
| 5 | **Run values of at most 8,192 nodes** (again) | One view case alone exceeded it | None. Harness reach, as in probe 16 |

Not needed this time: Map (the one dict is built once and looked up once, so a fold is linear), a UTF-8 decoding builtin (every decode happens on the host: `read_text`, Git's output and `NativeCodex`'s `decode(errors="replace")`), and tail calls (no loop is deeper than the number of checks). Lone-surrogate run values (#275) carried a lone surrogate into `writeJson`, where the host raises Python's `UnicodeEncodeError` as `write_text` would.

## Reproducing

The scripts are in private files outside the repository under `rrawrlet-probe17/`. `author.py <snapshot>` authors the port (it execs `jsonpart.py`, probe 9's JSON helpers and `json.dumps`) and writes `author_stats.json`. `oracle.py` runs `rrawrlet.verify` with the scripted host and records the calls; it needs an rrawrlet checkout at a fixed local path. `mkcases.py [out.json]` builds the 1,094 cases under `/tmp/v17`. `run_cases.py <snapshot> cases.json` runs them. `scale.py <snapshot copy>` gives the scale rows and `test_round.py <snapshot copy>` the rounding check. `nw.py` and `dsl9.py` are probe 11's client helpers, unchanged. Run the scripts with `PYTHONPATH=rrawrlet-probe17` and `NW_BIN` set to the built `Nyrwvy.AuthoringSession`.
