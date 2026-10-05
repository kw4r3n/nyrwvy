# rrawrlet slice probe 13 — 2026-10-01

**Status:** Complete. Two new parts of rrawrlet were authored by name only. The first is all of `ref_backup.py`: `backup_refs`, `ref_changes` and `drop_backup` over a `Git` capability, with the `for-each-ref` listing parsed into a dict, the stash reflog, `merge-base` ancestry and the `update-ref --stdin` payloads. The second is `supervisor.py`: `main()`'s `argparse` command line (`--pid-file`, `--timeout`, `--pass-fd` and a `REMAINDER` command) and `supervise()`, a process state machine with a clock, a poll loop with a deadline, signals and a cleanup loop with its own deadline, over a `Host` capability. The first differential run matched the real Python on **349/355 cases**. The 6 misses were 2 port bugs, both fixed by name in 6 requests. After the fixes and 16 added `errno` cases, the port matched **371/371**, including every Git and host call in order. There were no recipe rejections. **Nothing is blocked and nothing is queued.**

The largest language costs are the missing `while` loop and the missing dict and set, and neither limits reach. The three loops of `supervise` are `ListEffectFold`s over a pre-built bounded list, with a folder that returns `Result`. That is 11 functions and 199 nodes, and the bound is a divergence by construction, because Python can loop forever. Execution is fast: 4.2 M poll iterations run in 0.53 s, where the Python loop over the same fake takes 3.8 s. The dict and set are a sorted list with binary search, 10 functions and 159 nodes. With them, a snapshot and change report over 100,000 refs runs in 4.05 s, against 0.24 s in Python. A plain fold lookup is quadratic but only reaches the run limit at about 40,000 refs. The limits that did bind were the harness's: scripted fakes allow at most 256 responses per operation, so the poll loop could be tested against Python for at most about 250 iterations. More than half of the port (1,461 of 2,664 nodes) reproduces Python library behaviour: `argparse`, `float()`, `int()`, `repr()`, `PurePosixPath` and the `OSError` subclass for an `errno`.

**Date:** 2026-10-01

Baseline: nyrwvy `29017a9` (main after #272, clean); rrawrlet `5a46504` (clean). The session server was built with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release`. The oracle ran rrawrlet's real functions with rrawrlet's own virtual environment, Python 3.14.3, with `argparse` from that interpreter. The `str.isprintable` and `str.isdecimal` tables were computed from it.

The method was probe 12's: the `Nyrwvy.AuthoringSession` JSON-lines protocol with name-resolved requests (`simulateCreateTypesByName`, `simulateCreateEffect`, `simulateCreateCapabilityByName`, `simulateCreateFunctionByName`, `simulateReplaceBodyByName`, `commit` and `runPureByName`), no `declarations` call, and `runPure` batches of up to 40 cases through a `runCases` entry. Probe 12's recipe helper and session client were reused. None of its port was reused, because these parts share no logic with it. Scripts, cases and transcripts were kept outside the repository in a scratch directory (`probe13/`) and are not committed.

## Why these parts

`ref_backup.py` and `supervisor.py` exercise what probes 4 to 12 left out:

- **Git ref logic.** The module reads `for-each-ref` output into a dict, backs every branch, tag and stash entry up under `refs/rrawrlet/backup/<run>/`, and later classifies each ref as deleted, fast-forwarded (ignored) or rewritten through `merge-base`, which may exit 1. It needs a dict with Python's duplicate-key rule, a set, `str.splitlines()` and `str.split()` over Python's whitespace, and exact `update-ref --stdin` payloads.
- **A process state machine with a clock.** `supervise` polls a child until it exits, a deadline passes or a signal handler sets `stopped`. It then kills and reaps descendants until `waitpid` reports no children, or raises when a second deadline passes. There are three `while` loops, a `try`/`finally` whose `finally` can replace the exception, asynchronous state written by a signal handler, and Float64 clock arithmetic with `0`, `-0.0`, `nan` and `inf` timeouts.
- **CLI argument parsing.** `main()` is real `argparse`: option abbreviations, `--opt=value`, `-h` clusters, `--`, negative-number detection with Unicode `\d`, `nargs=REMAINDER`, `type=float`, `type=int` and `type=Path` conversion, and error messages that quote the argument with `repr()`.

`run_options.py` is only `argparse` declarations, and `supervisor.main` exercises the same parser logic on a smaller parser. `attempts.py` is constructor wiring. `git_sandbox.py` and `checkout.py` are mostly file-descriptor and `bwrap` plumbing, which would be ported as a capability surface with little logic left. `issue_target.github_repository` depends on `urllib.parse.urlsplit`'s quirks, which is a narrower library-emulation test than `argparse`. `cleanup.py`'s `candidate` is mostly record validation of the kind probes 9 to 11 covered. The new `Bits32` domain was not needed: neither part does bit operations.

## What was ported

| Python | Port |
| --- | --- |
| `git(repo, *args, data=..., ok_codes=...)` | Capability `Git` with one operation, `run(repo, args, data: Option<String>, okCodes: List<Int64>): String`. The oracle's fake Git is an in-memory repository (refs, stash reflog, a commit parent graph), so `merge-base` answers from real ancestry |
| `_refs`: `dict(line.split(" ", 1) for line in listing.decode().splitlines() if line)` | `StringSplitLines` with Python's ten breaks, `ListFilter` of empty lines, then an effect fold whose folder returns `Result`: a line without a space stops with Python's `ValueError` text (`dictionary update sequence element #N has length 1; 2 is required`, counting only non-empty lines). `dictify` sorts the pairs stably by key, merges a repeated key into its first position with its last value, and keeps both the insertion order and the sorted order |
| `current.get(ref)`, `before not in stash` | Binary search over the sorted pairs (`pairFind`, `pairGet`) and the sorted stash (`strFind`, `strMember`). A naive `ListFold` lookup was kept for the scale comparison only |
| `_stash` | `for-each-ref refs/stash`; an empty answer gives `[]`, otherwise `str.split()` of the reflog as a fold over code points against Python's 29 whitespace characters |
| `backup_refs`, `drop_backup` | `create <ns>/<ref without refs/> <sha>` and `create <ns>/stash/<n> <sha>` lines, joined with `\n` plus a final `\n`, and `delete <name>\n` lines. `update-ref` is invoked only when there is at least one line |
| `ref_changes`, `_is_ancestor` | An effect fold over the snapshot's refs in their order. `merge-base` runs only for a moved `refs/heads/` ref, with `okCodes [0, 1]`; its answer is compared after Python's `strip()`. Then `stash@{n}` for each dropped entry |
| Team's use (`team.py` 1140–1144) | `lifecycle`: backup, then changes; drop the backup when nothing changed |
| `supervise` | Capability `Host` with 12 operations: `subreaper`, `strerror`, `popen` (`Result<Int64, String>`), `close`, `monotonic` (Float64), `getpid`, `writePid` (`Option<String>` error), `poll`, `sleep` (returns the signal delivered while sleeping, or 0), `readText`, `kill` (`Option<String>`; `ProcessLookupError` is suppressed by the host, as `contextlib.suppress` does) and `waitpid` (`Wait {pid, status, noChildren}`) |
| The three `while` loops | `ListEffectFold` over a bound list of 1,024 items built by doubling (`dbl`), with folders returning `Result`: `Ok` continues and `Err` carries the final state. The poll loop's state is `PollSt {outcome, stopped}`; the cleanup loop's is `CleanSt {stopped, error}`, and its inner `waitpid` loop stops with `Err(true)` for no children and `Err(false)` for pid 0. An `Ok` after the last item means the bound was reached and is reported as `ERR LoopBound` |
| `try: ... finally: <cleanup>` | `tryPart` returns `Result<PollSt, String>` (the `write_text` error). `cleanup` always runs, and `finish` prefers its error, as an exception raised in `finally` replaces the first one |
| `monotonic() >= deadline`, `if timeout` | `fge(a, b) = b < a or a == b`, which is false for NaN and evaluates the clock once. `not Float64Equal(timeout, 0.0)`, which treats `-0.0` as false and NaN as true |
| `OSError(errno, strerror)` | `oserrorName`: Python's constructor picks the subclass from the errno (1 and 13 give `PermissionError`, 2 `FileNotFoundError`, 11, 114 and 115 `BlockingIOError`, and so on for 14 subclasses) |
| `main()` and `argparse` | `clsStep` classifies each argument as `argparse` does (`A`, `-` for `--`, or `O` with its option tuples from exact names, `name=value`, prefixes and the `-h` cluster), with `-\.?\d` over Unicode decimal digits for negative numbers. `mainStep` is a pure `Result` fold over the positions: an option consumes itself and its argument; the first positional takes the rest of the list (`REMAINDER`, keeping a `--`). `helpExplicit` is argparse's `-h` cluster loop in closed form (`-hhx` is help, `-hh=x` and `-h-x` are errors). Then the required-argument check, `unrecognized arguments`, and `main`'s own checks |
| `float(s)`, `int(s)` | Each code point is mapped as CPython does before parsing: ASCII is kept, other whitespace becomes a space and Unicode decimal digits become ASCII digits. Anything else cannot parse. After an ASCII `strip()`, a sign, `inf`/`infinity`/`nan`, underscores only between digits, and the mantissa and exponent are checked. The result is rewritten in JSON number form for `StringParseFloat64` or `StringParseInt64` |
| `repr(s)` | The quote choice, `\\`, the quote, `\t`, `\n`, `\r`, and `\xNN`, `\uNNNN` or `\UNNNNNNNN` for every code point that `str.isprintable` rejects, from a boundary table |
| `str(Path(s))` | `//` kept only when not followed by a third `/`; empty and `.` parts dropped; `.` for empty |

Out of scope: refnames that are not valid UTF-8 (the port's Git returns text), Git failing with a nonzero exit (a `throw` from the fake is a `RunRuntimeFailure`, as Python raises `RuntimeError` out of the module), `print_help` text (the port reports `EXIT 0`), and the `ctypes`/`signal`/`/proc` mechanics behind the `Host` operations.

## Loops without `while`

The language has no unbounded loop, and self-recursion overflows the stack at a few thousand levels. Each of `supervise`'s loops is therefore a `ListEffectFold` over a list whose length is the bound, with a folder that returns `Result`, so the fold stops when the Python loop would. `pollStep` checks `outcome is None and not stopped` at the top of each step, as the `while` condition does, so the loop body runs as often as in Python and one extra step ends it. The bound is 1,024 per loop, built once per run by doubling a one-item list and passed as callback context. With scripted fakes it can never be reached first, because each iteration makes at least one call and a run allows at most 1,024 calls and 256 responses per operation. Scaled with a program-defined capability, 2²² iterations run in 0.53 s.

The cost is the state threading, not speed. Each loop is a folder, a state product and a `Result` wrapper, and the inner `waitpid` loop's `break` and `except ChildProcessError: break` are two `Err` values of one Bool. The loops are 11 functions and 199 nodes. Python's `nonlocal stopped`, written by a signal handler at any bytecode boundary, becomes the return value of the `sleep` operation, which the loops fold into their state.

## Python library semantics

`argparse` is 18 functions and 822 nodes, `float()` and `int()` 14 and 326, `repr()` 4 and 121, `Path` 2 and 38, the Unicode tables 8 and 67, and `oserrorName` 87 nodes. Together they are 1,461 nodes, 55% of the 2,664-node port. None of this is a language gap. Every piece was expressible directly, and `StringParseFloat64` (order 29) and `Float64ToString` made the float path short. It is the cost of reproducing a library whose behaviour the program exposes: `--timeout x` prints `invalid float value: 'x'` with Python's `repr`, `-٣` is a positional because `\d` is Unicode, and `--timeout ١e٢` is `100.0`.

The tables are parsed once per `main` call and only when some argument is not ASCII, as probe 12 did for `\w`. `str.isprintable` has 1,472 boundaries and `str.isdecimal` 142.

## What was authored

| Part | Functions / nodes |
| --- | --- |
| Types | 17 in one group request (`Pair`, `Seg`, `Dict`, `Snap`, `Change`, `SplitAcc`, `Numbered`, `SegAcc`, `Wait`, `PollSt`, `CleanSt`, `Opt`, `Cls`, `ClsAcc`, `Parse`, `Stop`, `Tabs`, and the `Case` union with 6 variants); 1 effect; `Git` with 1 operation, `Host` with 12 |
| `ref_backup` logic: listing parse, stash, backup, changes, drop, lifecycle | 16 / 445 (`backupRefs` 59, `refStep` 53) |
| Dict and set: stable sort, duplicate merge, binary search | 10 / 159 |
| `str.split()` and `strip()` over Python whitespace | 3 / 80 |
| Output rendering | 4 / 44 |
| `supervise`: entry, loop driver, exit code, `fge`, `oserrorName` | 6 / 222 (`oserrorName` 87) |
| The three loops and the bound list | 11 / 199 |
| `argparse`: classification | 8 / 286 (`parseOptional` 70) |
| `argparse`: the `-h` cluster | 3 / 76 |
| `argparse`: the consume loop, conversion and `main`'s checks | 7 / 460 (`consume1` 140, `mainStep` 108) |
| `float()` and `int()` | 14 / 326 (`floatBody` 67) |
| `repr()` and `Path` | 6 / 159 |
| Unicode tables | 8 / 67 |
| Case dispatch | 4 / 65 |
| Lone surrogates in and out (`unesc`, `escOut`) | 5 / 76 |
| **Port and harness** | **105 / 2,664** |
| Scale entries: two program-defined `Git` worlds, a stateless `Host`, the naive lookup | 27 / 254 |
| **Total** | **132 / 2,918** in 276 requests |

The build took 270 requests (types, effect, two capabilities and 131 functions, each simulated and committed). The fixes took 6 more: `oserrorName` was created and `stashChange` and `supervise` were replaced by name. There were **no recipe rejections**, and no `scopeDepth` or type errors reached the session. Six `None` arms that referred to an outer frame one level too shallow were found by reading the script before the first build: a `None` arm opens a frame with no bindings, so an outer value is one level deeper there than in the `Some` arm.

The differential run found **2 port bugs**:

1. `stashChange` read the entry number from `stash@{n}` at offset 6 instead of 7, so every dropped entry after the first reported entry 0 (5 cases).
2. `supervise` labelled every `prctl` failure `OSError`. Python's `OSError(1, ...)` constructs a `PermissionError` (1 case). The fix added `oserrorName` and 16 cases for other errnos.

**Lone surrogates, both ways.** `sys.argv` holds lone surrogates when the command line is not valid UTF-8 (`surrogateescape`), and `main()` either quotes them with `repr` or passes them on in the command. Run values must be well-formed UTF-16, so the harness sends each one as U+10FFFE followed by four hex digits, as in probe 12. This time the output needs it too: a parsed command that contains one is escaped the same way by `escOut`. That is 5 functions and 76 nodes of test plumbing.

## Results vs Python

371 cases. A case matches when its output line is equal and the calls are the same list: the same operations with the same arguments, in the same order. That includes every `update-ref` payload, every `merge-base` pair, every Float64 `sleep` argument, and every `kill` pid and signal.

| Group | What it covers | Result |
| --- | --- | --- |
| `backup_refs` (54) | Empty and newline-only listings; CRLF, lone CR, `\r\r\n`, VT/FF, `\x1c`–`\x1e`, U+2029, U+2028 and NEL inside a refname (which split it, so the next line has no space: `ValueError` with its element number), a line with no space after blank lines, values with spaces, leading and trailing spaces, duplicate keys (first position, last value), no final newline; stash reflogs split on tabs, U+3000, `\x1c`, NEL and NBSP but not ZWSP, and a stash ref with an empty reflog; run IDs with `/`, spaces and non-ASCII; 25 random repositories with heads, tags, remotes and notes | 54/54 |
| `ref_changes` (38) | Unchanged, deleted head and tag, fast-forward, backward, to a sibling, to an unrelated root, to a merge; a fast-forwarded tag (still rewritten); new refs ignored; kept, dropped, reordered and duplicated stash entries; `merge-base` answers padded with spaces, NBSP or U+3000 (equal after `strip`), upper case, ending in `\x1c` (stripped) or ZWSP (not stripped), two lines; a broken or duplicated current listing; 12 random snapshots | 38/38 |
| `drop_backup` (5) | Empty (no `update-ref`), refs only, stash only, non-ASCII, refs without the `refs/` prefix | 5/5 |
| Lifecycle (24) | Random repositories with no change (backup dropped), fast-forwarded branches, deletion, rewrite, a dropped stash entry and a new one | 24/24 |
| `supervise` (76) | Not Linux; `prctl` failing with 18 errnos (`PermissionError`, `FileNotFoundError`, `BlockingIOError`, `BrokenPipeError`, …, plain `OSError`); `Popen` raising (fds still closed); exits 0, 3, 255, −9 (137) and −15 (143); a timeout reached, reached exactly, and reached on the poll that saw the exit (124 wins); timeouts `0`, `-0.0` (no deadline, no clock read), NaN (never fires), infinity, negative; a clock at 10¹⁵ where 0.02 s is below the float spacing; SIGTERM and SIGINT while waiting, two signals in one sleep (the last wins), a signal during cleanup (changes the exit code); an unwritable pid file (cleanup still runs), and one whose cleanup also times out (`RuntimeError` replaces the `OSError`); children killed and reaped over several rounds, `ProcessLookupError` suppressed, `PermissionError` from `kill` propagating, whitespace in `/proc/.../children`, the cleanup deadline reached and reached exactly; 37 pass fds; 24 random scripts | 76/76 |
| `main` (174) | 134 handcrafted command lines: abbreviations and ambiguity (`--p`, `-=x`, `--=x`), `name=value`, the `-h` cluster forms, help before and after a bad value, `--` in every position, options after the command, a missing value, negative numbers (`-5`, `-.5`, `-٣`) and `-inf` (an option, so `expected one argument`), `float()` inputs (underscores, `.5`, `5.`, `1e`, `0x10`, `inf`/`nan` in any case, `1e309`, `1e-400`, `4.9e-324`, NEL, U+3000, `\x1c`, Arabic-Indic and mathematical digits, superscripts), `int()` inputs, `Path` normalisation, unrecognised options, `repr` of quotes, backslashes, controls, C1, soft hyphen, U+2028, private use, tags, astral characters and lone surrogates, and a lone surrogate passed through the command. 40 random command lines from a token pool | 174/174 |
| **Total** | | **371/371** |

All 371 cases took 10 runs and 14.7 s (4.70 s for the session's first build, then 0.87–1.89 s).

## Known divergences

None showed in the cases. Three follow from the design and were confirmed separately:

1. **Integers beyond Int64.** `--pass-fd 9223372036854775808` is accepted by Python and rejected by the port as `invalid int value`. This is the same class as probes 9–11.
2. **The loop bound.** With no timeout and a child that never exits, Python polls forever and the port stops after 1,024 iterations with `ERR LoopBound`. Under scripted fakes the run fails first with `RunBindingExhausted` after 256 polls.
3. **When a signal is seen.** The port learns of a signal from `sleep`'s result. Python's handler runs between any two bytecodes. If a signal arrives during the final `poll` and cleanup ends without sleeping, Python returns `128 + signum` and this port returns the exit code (by construction; not run). The oracle delivered signals only during sleeps. A faithful host interface would add a `stopped()` read before the return.

## Scale

Times are `runPure` wall time after the session's first run. Python times are the best of three runs of the real functions over an equivalent fake. Every row gave the same result as Python. The build is included in every port time. It varied between 0.4 and 1.1 s per run on this machine, so rows below about 1 s mostly measure the build.

The ref worlds were generated inside the program by two program-defined `Git` capabilities. A plain function decodes `N:S` from the repository path and builds the listing: 90% heads and 10% tags, every tenth ref deleted afterwards and every tenth moved, a twentieth of the moves fast-forward, and half the stash dropped. Scripted fakes could not carry this: a response counts toward the 65,536-unit String budget, which holds about 1,000 refs. The poll loop used a stateless program-defined `Host` whose `poll` never reports an exit, so the loop runs to its bound.

| Input | Python | Port |
| --- | --- | --- |
| Trivial entry (`toInt`) | — | 0.40–0.56 s |
| Backup and changes, sorted lookup: 100 / 1,000 / 10,000 refs | 0.33 / 2.0 / 19 ms | 1.14 / 1.02 / 0.97 s |
| Backup and changes, sorted lookup: 50,000 / 100,000 / 200,000 refs | 114 / 239 / 489 ms | 1.58 / 4.05 s / `RunTimeout` |
| Backup and changes, naive fold lookup: 1,000 / 5,000 / 10,000 / 20,000 / 40,000 refs | 2.0 / 9.1 / 19 / 39 / 82 ms | 0.43 / 0.68 / 0.85 / 1.77 / 4.93 s |
| Poll loop, 2¹⁰ / 2¹⁷ / 2²⁰ / 2²² iterations | 1.0 / 103 / 952 / 3,757 ms | 0.30 / 0.32 / 0.48 / 0.53 s |
| `main`, 100 / 1,000 / 2,000 `--pass-fd` pairs (up to 4,003 arguments) | 0.5 / 3.5 / 8.6 ms | 0.59 / 0.67 / 0.79 s |
| `main`, `--timeout` error quoting 600 / 6,000 / 24,000 non-ASCII code points | 0.3–0.6 ms | 0.56–0.61 s; 24,000 within the build's noise (0.56–2.7 s) |
| `main`, the same with 54,000 code points (a 126,050-unit message) | 0.9 ms | `RunProcessFailure`: result exceeds 65,536 String code units |

A realistic repository (hundreds to a few thousand refs) costs the build and nothing else. The port's own work only shows above about 20,000 refs. There it is about 17 times Python at 100,000 refs, and the in-program generation of two 100,000-line listings is part of that. The naive lookup is quadratic: 20,000 refs take 1.77 s and 40,000 reach the limit. With the sorted index the snapshot reaches 100,000 refs. The poll loop is faster than Python per iteration, because its folder and program-defined capability compile to plain C#.

Against the deferred candidates:

- **Evaluating constants once.** Not triggered. The two tables are parsed once per `main` call and passed as context, and only when an argument is not ASCII. The bound list for the loops is built once per `runCases` run. Quoting 24,000 non-ASCII code points, each needing two table lookups, costs nothing visible beyond the build.
- **Arguments as input with a build cache keyed by fingerprint.** Recorded again, not queued. Every run rebuilds the same program: the 10 case batches spent most of their 14.7 s building, and about 45 scale runs spent 0.4–1.1 s each. It never limited a run.
- **Map.** Weakly triggered. Python's `dict` (with its duplicate-key rule) and `set` cost 10 functions and 159 nodes. A naive `ListFold` lookup, which a first draft would use, is quadratic and reaches the run limit at about 40,000 refs. The sorted index is a program-level fix that reaches 100,000, so the gap costs nodes and care, not reach.
- **StringFold.** Not triggered. `str.split()`, `repr()` and the float normalisation fold over `StringCodePoints`, and 24,000 code points cost nothing measurable.
- **Lone surrogates as run arguments.** Triggered again, and now in both directions, by a real input class (non-UTF-8 `argv` bytes): 5 functions and 76 nodes of harness escaping. The port itself handles them: `repr` escapes them, and `StringFromCodePoint` and `StringToCodePoint` round-trip them.

Two harness limits bound what could be measured, not what the port can do. Scripted fakes allow at most 256 responses per operation and 1,024 calls per run, so the poll loop was checked against Python for at most about 250 iterations (5 s of simulated time at 0.02 s per sleep). Program-defined capabilities are stateless, so a fake world with a clock that `sleep` advances cannot be written in the language and was scripted from Python. A result String is limited to 65,536 code units.

## Costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **No unbounded loop** | Three `while` loops became `ListEffectFold`s over a bound list with `Result` folders and explicit state products: 11 functions and 199 nodes, plus a bound that diverges from Python by construction (a supervisor with no timeout polls forever). Speed is not the issue: 4.2 M iterations in 0.53 s | None queued. The `Result` fold already stops early; what is left is the bound list and state threading. A bounded `Iterate(seed, step, limit)` node would remove only `dbl` and the `Ok`-at-the-end arm, about 40 nodes |
| 2 | **No dict or set** | 10 functions and 159 nodes for a stable-sort merge with Python's duplicate-key rule and binary search. The naive fold is quadratic (40,000 refs reach the 5 s limit); the sorted form reaches 100,000 | None. The deferred Map. A program-level index suffices at every realistic ref count |
| 3 | **Scripted fakes for long effect sequences** | 256 responses per operation and 1,024 calls per run cap a differential test of the poll loop at about 250 iterations. A clock that `sleep` advances needs state, which program capabilities do not have, so the scale runs used a stateless host | None. This is harness reach; stateful program capability implementations are already listed without a fixed order |
| 4 | **Build on every run** | 0.4–1.1 s per `runPure` (4.7 s for the session's first), which is most of the 14.7 s for 371 cases and every small scale row | None queued. The deferred build cache keyed by fingerprint |
| 5 | **Lone surrogates as run values, in and out** | `argv` with lone surrogates needed the U+10FFFE escape in both directions: 5 functions and 76 nodes | None. Test plumbing |
| 6 | **Asynchronous state as a return value** | The signal handler's `nonlocal stopped` became `sleep`'s result, which leaves divergence 3 open unless the host adds a read | None. It is a host-interface design point, not a language gap |

Python library behaviour (`argparse`, `float()`, `int()`, `repr()`, `Path`, `OSError`'s subclass choice) was 55% of the nodes, but it is not ranked: each part was direct to write. Float64 was needed only for the clock. Its two traps were known from probe 11 and were handled when first written: `>=` as `b < a or a == b` with the clock read once, and truthiness by `Float64Equal` so that `-0.0` is false and NaN true. Neither `Bits32`, function groups nor recursion deeper than the tables' binary search (about 11 levels) was needed.
