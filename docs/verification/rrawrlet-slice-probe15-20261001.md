# rrawrlet slice probe 15 — 2026-10-01

**Status:** Complete. A part of rrawrlet that no earlier probe had touched was authored by name only: all of `mcp.py`, rrawrlet's MCP server, except the process plumbing of `serve()`. That covers `serve_streams` (newline-delimited JSON-RPC read in chunks, requests running concurrently, `close()` at EOF), `Server.handle` with `initialize`, `ping`, `tools/list` and `tools/call`, and all 16 tools: argument checks, run paths, capacity, live runs with their report deque and activity, timed `wait_run`, `stop_run`, verification after a run, and the blocking one-shot tools, over a 28-operation `Host` capability. The port matched the real Python on **427 of 428 cases**, including all 16,504 compared host calls in order. The one miss is a known divergence by construction. It found 2 port bugs on the way, both fixed. **Nothing is blocked.**

The largest finding is the event loop. It was written as a self-recursive `serve`, one call per turn, and that recursion overflows the stack between 12,000 and 14,000 turns. A turn is one input chunk, one run event, one timer or one finished call, so a long session with a busy run would crash the server. The same loop written as two nested `ListEffectFold`s with a `Result` folder took 3 functions and 104 nodes (19 more than the recursive `serve`), matched the same 427 cases, ran past 14,000 turns, and is bounded at 10 million turns. Two smaller costs are Float64 conversions that the language does not have (218 nodes for `round(x, 1)` and seconds to milliseconds), and copying 10- and 14-field state records to change one field.

**Date:** 2026-10-01

Baseline: nyrwvy `084890c` (main after #277, clean); rrawrlet `4276240` (clean). The session server was built with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release` on .NET SDK 10.0.112. The oracle ran rrawrlet's real `mcp.serve_streams` and `Server` on Python 3.11.15.

The method was probe 14's: the `Nyrwvy.AuthoringSession` JSON-lines protocol with name-resolved requests and `runPureByName`. Probe 9's JSON parser and serialiser and probe 10's `repr` were reused as they were (59 functions, 1,381 nodes). The harness, cases, snapshots and transcripts are in the project's shared files (`rrawrlet-probe15/`) and are not committed.

## Why this part

`mcp.py` exercises what probes 4 to 14 left out:

- **A long-running event loop.** `serve_streams` reads until EOF and starts a task per request, and requests wait on runs, timers and each other. Earlier probes' loops were bounded by their input.
- **Concurrency with time.** `wait_run` waits for a run with a timeout, `stop_run` cancels a run and waits for its cleanup, one-shot tools block and count toward capacity, and at EOF `close()` cancels everything before the requests of the last chunk run.
- **Callbacks from a long host call.** `api.execute` reports progress through `report` and `activity` callbacks while it runs, and the server's status answers read that live state.
- **JSON-RPC over bytes.** `json.loads` of each line, with its UTF-8 BOM rule and `surrogatepass`, and every answer as `json.dumps(ensure_ascii=False)`.

Of the modules left, `results.py`, `verify.py` and `review.py` are record plumbing of the kind probes 9 to 12 covered, and `web_live_requests.py` is request validation.

## What was ported

| Python | Port |
| --- | --- |
| Host effects: stdin chunks and stdout lines, `time.time`, `asyncio.sleep` in `asyncio.wait`, `api.execute`'s callbacks, `Path` probes, `os.path.expanduser`, and 17 `api` calls (`run_summary`, `run_changes`, `apply_result`, `mark_stopped`, `recent_runs`, `run_usage`, `usage_stats`, the four one-shot tools, `repo_map`, `list_models`, `continuation`, `RunRequest(...).to_args`, the leader choice, `record_run`, `has_registered_checks`, `verify_run`, `new_run_id`) and the package version | Capability `Host` with 28 operations. `readChunk(): Chunk {lines, eof}` is one read of the stream, `nextEvent(output, index): Event` is the index-th callback of a run (`Report`, `Activity`, `Exit`, `Raise`, or `Stopped` when cancelled), and `sleep(ms)` is a timer. API calls answer `Result<String, String>` with JSON text or Python's `str(exc)` |
| `serve_streams` and `close()` | `serve(st, host, scope)`: one `TaskWait` `Any` over every pending task (the reader, one pump per live run, a verifier, a timer per timed wait, a blocker per one-shot call), then the first finished one is handled and `serve` calls itself with the new state. At EOF the pumps, verifiers and blockers are cancelled and the runs marked, then the chunk's requests run and see `closing` |
| A chunk read without yielding | A chunk's parse errors are written before any of its requests run, as Python writes them in its read loop before the request tasks start |
| `Server.handle`, `_error`, `_reply` | Envelope checks in Python's order, `initialize` with its version rule, `tools/list` from the tool schema as one parsed JSON literal, `tools/call` with the name and arguments checks, `ToolError`, `OSError`, `RuntimeError` and `ValueError` as `isError` results, anything else as `-32603 Internal error` |
| `json.loads(line)` | BOM strip, strict UTF-8 with `surrogatepass` (7 functions, 282 nodes), probe 9's parser; a NUL or UTF-16 BOM in the first two bytes is a parse error. A line that has a lone surrogate after decoding is dropped on output, as Python's `write` fails on it |
| `_text`, `_number`, `api.run_path`, `min(max(x, 1), 600)` | Validators with Python's `bool`-is-`int` rule; `clampJ` keeps `min`/`max`'s choice of operand (an `int` stays an `int`); run paths resolved against the root twice, as Python does |
| `_start` with `RunRequest`, leader choice, `record_run`; `_execute`; `_verify_after_run` | A run handle `RunH` (14 fields) in the state; a pump task per event appends to the report deque (last 40 items) and activity; `Exit`, `Raise` and `Stopped` finish the run and start a verifier when checks are registered |
| `_status`, `tool_wait_run` | `elapsed_seconds = round(now - started, 1)` (see [Float64](#float64-without-conversions)); `wait_run` parks a request with a timer task, and the run's end or the timer answers it |
| `tool_stop_run`, `_blocking`, `_one_shot` | Stop cancels the pump and answers when the run's cleanup has finished; one-shot calls run as blocker tasks that count toward capacity and are cancelled at close |

Out of scope: `serve()`'s file-descriptor setup and its thread for `write`, the 16 MiB line limit (fakes carry at most 65,536 UTF-16 units), `json.loads`' UTF-16 and UTF-32 detection, integers beyond Int64, and the order of `asyncio.to_thread` calls that start at the same virtual instant (the cases keep request gaps odd and run delays even so that no two events tie).

## What was authored

| Part | Functions / nodes |
| --- | --- |
| Types | 20 (`Json` and `Member` in one request, then one request each: `Chunk`, `Event`, `Ev`, `Tag`, `Pend`, `RunH`, `ReqKind`, `Req`, `St`, `Fault`, `Out`, `Handled`, …); 1 effect; `Host` with 28 operations |
| JSON and `repr`, reused from probes 9 and 10 | 59 / 1,381 |
| Line decoding, text helpers, rounding | 27 / 610 (`decStart` 126, `round1` 103) |
| Tools and dispatch | 35 / 2,429 (`startC` 179, `toolCall` 145, `handleMessage` 136) |
| Event loop: workers, run steps, waits, close | 36 / 1,146 (`onStep` 179, `finishRun` 111) |
| Paths, validators, state helpers | 34 / 498 |
| **Total** | **191 / 6,064** in 214 proposals |

The `Host` capability shape was the main design step. `api.execute` calls back into the server while it runs, and a capability operation cannot call back into the program, so the callbacks became a pull: the pump asks `nextEvent(output, index)` for one event at a time, and each event is one loop turn. `TaskWait`'s `limit` serves one wait, while the loop can hold several timed `wait_run` requests at once and the language has no clock value to compute a remaining time from, so each timeout is a `Host.sleep` task in the same `Any` wait.

Apart from slips in the authoring script, the only rejections were two functions over the 256-node recipe limit: `toolStopRun` at 291 nodes and `onStep` at 375 (see [cost 3](#costs-ranked-by-impact)).

## Results vs Python

428 cases. The oracle ran the real `serve_streams` and `Server` on an event loop with a virtual clock. The `api` functions, the clock, `Path` and the reader were fakes that record every call, and each case's fake for the port was built from that record. A case matches when `runServer` returns `ok` and the port made the same host calls with the same arguments in the same order. The stream reads, the run events and the timers are the fakes' own schedule and are not compared; every write, clock read, path probe and API call is.

| Group | What it covers | Result |
| --- | --- | --- |
| Protocol (6) | `initialize` versions, `ping`, `tools/list`, notifications; 27 malformed envelopes (`jsonrpc` 2.1 or a number, no id, id `null`, non-object params, methods that are numbers, lists or objects); ids of every JSON type including `-0.0`, `1e300`, `NaN`, `-Infinity`, ` `, escapes, a lone surrogate and `2^63 - 1`; 28 unparsable or odd lines (BOM, two BOMs, invalid UTF-8, encoded surrogates, whitespace-only lines, VT, FF, NBSP, NUL, a missing final newline); `tools/call` with bad names and arguments | 5/6, and the known divergence below |
| Arguments (3) | Every tool's text, number and list checks, including `True` for a number, `1.5` for an int, `0` and negative limits, run paths with `..`, `//`, NUL, `.`, a trailing `/` | 3/3 |
| One-shot tools (4) | `condense` with one or many questions, `snapshot`, `review_diff`, timeouts `0`, `0.5`, `600.0`, `1e9`, errors from the model, capacity with runs and blockers mixed, EOF during a call, `max_concurrent` 1 | 4/4 |
| Saved runs (1) | 30 status, result and wait calls over finished, unfinished, nested, empty, symlinked and looping run directories, apply, usage, verify and review | 1/1 |
| Live runs (14) | The report deque past 40 items, activity fields, timed waits, verification after a run and its errors, exits 0, 1, 130 and a raise, stop before and after the end with `mark_stopped`'s answer, close with runs, verifiers and waits pending, a burst with EOF, leader choices with and without Claude, capacity, `continue_run`, `~` in the repository | 14/14 |
| Random (400) | Sessions of 4 to 18 tool calls with random run events, saves, exits, raises, checks, verifications, waits with timeouts from 0.5 s to 700 s and `max_concurrent` from 1 to 5 | 400/400 |
| **Total** | | **427/428** |

The known divergence is by construction. Python decodes a UTF-8-encoded surrogate pair (`ED A0 BD ED B8 80`) with `surrogatepass` into two lone surrogates, and then its `write` fails and the answer is lost. In a UTF-16 String the two halves are one valid pair, so the port answers.

Across all cases, 158 pending calls and run events were cancelled, and the cases ran 78 timeouts, 1,113 run events and 4,657 answers. All 428 cases took 285.7 s: 9.6 s for the first, which includes the session's first build, then about 0.65 s each.

The differential run found 2 port bugs. `start_run` passed `workspace` and `continues` to `RunRequest` twice, which a smoke case showed. A chunk's parse error was written after the answers of its earlier lines, which the burst case showed; Python writes all of a chunk's parse errors first. Both were fixed by name, and the rest were harness defaults (a run without a script, a one-shot call without an answer, the end of the input as EOF, one same-instant tie).

## The event loop

`serve` calls itself once per turn. The frame stays on the stack until the whole session ends, so the stack bounds the session. Fakes could not show this. A fake operation scripts at most 256 argument cases and a run value at most 8,192 value nodes, so a differential case holds about 500 turns (checked: 240 events with 240 pings, 496 host calls, matched). The depth was measured with a stateless program-defined `Host` instead, with one ping per chunk and no EOF, on a copy of the snapshot whose `onRead` treats the K-th chunk as EOF:

| Loop | 8,000 turns | 10,000 | 12,000 | 14,000 | 20,000 | No bound |
| --- | --- | --- | --- | --- | --- | --- |
| Self-recursive `serve` | ok, 4.3 s | ok | ok | stack overflow | stack overflow | stack overflow |
| Nested `ListEffectFold`s | — | — | ok, 4.8 s | ok, 5.5 s | `RunTimeout` (5 s) | — |

The times are wall time and include about 2.3 s of runner start (100 turns took 2.4 s). A turn costs about 0.25 ms. The fold form is `serveTurn`, which is `serve`'s body with the recursive call replaced by `Ok(state)` and "finished" by `Err("done")`, and two nested `ListEffectFold`s over 1,000 and 10,000 items, which stop at the first `Err`. It is 3 functions and 104 nodes against the recursive `serve`'s 85, and it matched the same 427 of 428 cases. Its bound is the product of the list lengths and can be raised by nesting once more; its `RunTimeout` is `runPure`'s 5 s limit, which a real server does not have.

The recursive form was the natural one, and the language subset does document the stack bound. Its advice, however, is `ListFold` over a list the input gives, and an event loop has no such list. The fold form needs a list invented only to count turns.

A burst of pings in one chunk is a fold rather than recursion: 20,000 requests in one chunk took 3.0 s.

## Float64 without conversions

`_status` returns `round(time.time() - handle.started, 1)`, and `wait_run` turns `timeout_seconds` (an `int` or a `float` after the `min`/`max` clamp) into a timer. The language has no Float64 to Int64 conversion and no decimal rounding. The [decision](../decisions/float64-conversion-and-run-values.md) deferred both and named the conditions to revisit them: a program computing an Int64 from a Float64, and `round(x, n)` needed inside a program rather than for display. This port meets both conditions. The milliseconds for `Host.sleep` are computed in the program, and the rounded seconds are part of the program's own answer, so a harness cannot round them afterwards.

Both were written from `Float64ToString`, which gives Python's `repr` digits. `round1` rounds by the exact binary value: it takes the digits, finds the tenths, and settles a digit string that ends in 5 with an exact product (Dekker's split with 134217729.0) to see whether the true value lies above the tie. `nearestMs` reads milliseconds from the digits in the same way. The two, with their helpers, are 6 functions and 218 nodes (3.6% of the port). `round1` was checked against Python's `round` on 616 values, including ties, and `nearestMs` on the same set. Both were right on the first run, but the tie rule is subtle, and a reader could not tell that it is right without the tests.

## Costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **An unbounded loop has no stack-safe form** | The self-recursive event loop overflows at 12,000–14,000 turns; a busy run emits a turn per progress event. The workaround, nested `Result` folds over invented lists, took 3 functions and 104 nodes, matched every case and ran past the overflow point | Not queued. Candidates: compile a self `DirectCall` in tail position as a loop in the C# backend (no surface change; `serve`'s recursive call is in tail position under `let`, `Option` and `Result` arms), or a loop construct. Probe 13 recorded the missing `while` for bounded loops |
| 2 | **No Float64 to Int64 conversion and no decimal rounding** | 6 functions and 218 nodes from `repr` digits and an exact product; both revisit conditions of the Float64 decision are met | Not queued. Candidates: `Float64ToInt64` with a stated rounding and an `Option` for NaN, infinities and out-of-range values, and `Float64Round(value, digits)` with Python's rule (the exact binary value, ties to even) |
| 3 | **Updating one field copies the whole record** | `St` has 10 fields and `RunH` 14, and 25 update sites restate every other field. Two functions went over the 256-node limit because each update repeated the state expression per field, and the helper had to let-bind it first. *Estimate from the helper, not a measured count:* about 550 nodes (9%) are restated fields | Not queued. Candidate: a product update node, `ProductWith(value, fieldBindings)`, which keeps the other fields |
| 4 | **Progress callbacks become a pull** | `api.execute`'s callbacks became `nextEvent(output, index)`, one pump task and one loop turn per event. It cost the capability design, not nodes, and a turn costs about 0.25 ms | None. A channel or stream would be a new concurrency feature, and the pull form was direct |
| 5 | **Fakes bound the differential sessions** | 256 argument cases per operation and 8,192 value nodes per run cap a case at about 500 turns, so the stack bound needed a program-defined `Host`. A `delayMillis` answer cannot be cut short by `TaskCancel`, so the harness chose `awaitCancellation` for exactly the calls Python cancelled | None. Harness reach, recorded with probes 13 and 14 |
| 6 | **Strict UTF-8 with `surrogatepass`** | 7 functions and 282 nodes. The lossy UTF-8 builtin that probe 14 proposed would not have served here, because `json.loads` rejects invalid bytes rather than replacing them | None |

Against the deferred candidates: the lossy UTF-8 decode builtin was not needed, as cost 6 says. Map was not needed, because runs and requests are few and searched by fold. StringFold and `Bits32` were not needed. The build cache held: after the first run every case took about 0.65 s. Lone-surrogate run values would not have closed the known divergence, which comes from the String representation rather than the run boundary.

## Reproducing

The scripts are in the project's shared files under `rrawrlet-probe15/`. `mkcases.py` builds the case directories under `/tmp/mcp15` and runs the oracle (`oracle.py`) with rrawrlet on `PYTHONPATH`. `author.py <snapshot>` authors the port (`p15.json`), and `run_cases.py <snapshot> cases.json known.json` runs the cases. `scale.py` gives the fake-bound rows and `depth.py` the loop depth and the burst. `depth.py <copy> port` replaces `serve` with the folds (`p15fold.json`).
