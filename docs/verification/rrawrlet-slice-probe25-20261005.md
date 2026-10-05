# rrawrlet slice probe 25 — 2026-10-05

**Status:** Complete. rrawrlet's `codex.py`, the last module no probe had covered, was authored by name only. The port covers the `Codex` client (`start`, `request` with its timeout, `_send`, `_read`, `_handle`, `models`, `start_thread`, `turn`, `close` and `_close`), `transport_errors.codex_error`, `start_process`'s environment filter, `group_alive`, `signal_group` and `terminate_group`. The child process, its pipes, `/proc`, `killpg`, the logs and the two callbacks are a 21-operation `Host` capability. The port matched the real Python on **489/490 cases** after two port bugs were fixed, including 7,433 host calls in order. The first full run matched 485. The remaining case is a known divergence: asyncio lets a task run between two steps that happen at the same virtual instant, and Nyrwvy has no zero-time yield. **Nothing is blocked and nothing is queued.**

The concurrency part was expressible with task scopes, `TaskWait` (any) and `TaskCancel`. Its costs are new. There is no time value, so the turn's deadline, every request timeout and every `wait_for` in `terminate_group` is a task that runs `Host.sleep`. A task sees only its start arguments. There are no futures or channels, so the reader, its response routing and every wait became one event loop over a 25-field state. Two older costs came back: Python's strict decoding with its error text (a second consumer, for the [UTF-8 decode decision](../decisions/utf8-decode.md#revisit-conditions)), and Python library behaviour written by hand, here `json.loads` with CPython's own error messages.

**Date:** 2026-10-05

Baseline: nyrwvy `800f364` (main after #352, clean); rrawrlet `4a28172` (clean). The session server was built with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release` on .NET SDK 10.0.112. The oracle ran rrawrlet's real `codex.py` on Python 3.11.15.

The method was probe 19's: the `Nyrwvy.AuthoringSession` JSON-lines protocol with name-resolved requests, and `runPure` batches through a `runCases` entry. Probe 11's session client and probe 19's recipe helpers were reused. `dsl9.py` gained `mu2`, a `matchUnion` helper that writes order 41's `otherwise` and shared arms, and probe 9's JSON helpers now use it. The harness, cases, snapshot and transcripts are in private files outside the repository (`codex-probe25/`) and are not committed.

## Why this part

`codex.py` is a JSON-RPC client around a child process. [Probe 19](rrawrlet-slice-probe19-20261001.md#why-this-part) set it aside because nearly all of it would sit on the host. With orders 41 to 55 done, it is the part of rrawrlet that exercises the concurrency constructs hardest:

- **A reader task beside the caller.** `_read` runs as its own task, resolves the futures that `request` waits on, starts a handler task per server request, and on any exception fails every pending future with one `CodexError`.
- **Time.** Every request has `asyncio.wait_for(future, 60)`. The driver wraps a turn in `asyncio.timeout(deadline)`, and `close` waits up to 2 s for an interrupt and the turn's end. `terminate_group` waits 3 s after `SIGTERM` and 1 s after `SIGKILL`.
- **Cancellation with exceptions.** `close` cancels running handlers unless their tool is a Git mutation, waits for them all, interrupts the turn, and stops the process group. Leftover pipe data is handed to the reader before the pipe closes.
- **Bytes.** `json.loads(line)` takes bytes, so it detects UTF-8, UTF-16 or UTF-32 per line and decodes with `surrogatepass`. Its `UnicodeDecodeError` and `JSONDecodeError` text becomes the transport error that callers see.
- **`/proc`.** `group_alive` parses `/proc/<pid>/stat` with `rpartition(b")")`, `split()` and `int(bytes)`, which has its own rules for `_`, whitespace and the 4,300-digit limit.
- **A regular expression with `IGNORECASE`.** `codex_error` finds `request ID[: ]+([a-fA-F0-9-]{36})` in a message. Python folds `ſ` to `s`, `İ` and `ı` to `i`, and `K` (U+212A) to `k`.

## What was ported

| Python | Port |
| --- | --- |
| The asyncio event loop, `_read` and `request`'s future | `waitRound` waits for any of: the pending `Host.read` task, the timers, handler tasks or `process.wait`. A chunk read is split into lines (`drainLines`) and handled as `_read` would, then the next read starts. `pump(want)` repeats rounds until a response, `turn/completed`, the turn's end or the reader's end holds, or a timer fires |
| `asyncio.wait_for`, `asyncio.timeout` | `timer(tag, ms)` starts a task that calls `Host.sleep(ms)`; a fired timer's tag decides `TimeoutError` or `CancelledError`. A turn's deadline is passed into every request it makes |
| `Codex` attributes | `St`, a Product of 25 fields, threaded through every function and updated with `ProductUpdate` |
| `_handle` as a task | `handleWorker` started with an `HReq` holding the message and `closed` and `on_tool` as they are at the start; it writes the response itself |
| `close`, `_close`, `terminate_group` | `close`: cancel non-mutating handlers, `gather` the rest, `interrupt`, `terminate` (two `waitFor` rounds, `abort`, `os.read` of the leftover, pipe close), then `pump("reader")`. The pending read is cancelled, and its `awaitCancellation` value is the EOF chunk |
| `json.loads(bytes)` | `detectEncoding`, `decodeBytes` (UTF-8 with `utf-8-sig`, UTF-16 and UTF-32 in both orders, all with `surrogatepass` and CPython's `UnicodeDecodeError` text), then a scanner with CPython 3.11's C scanner messages and positions, the 4,300-digit limit, `NaN` and `Infinity` |
| `json.dumps`, `str()`, `repr()`, `dict.get` and the exceptions on the wrong type | Probe 9's `json.dumps`; `pyStr`, `pyRepr` with probe 11's `isprintable` table; `attrGet`, `subscript` and `pyIn` with Python's `AttributeError`, `TypeError` and `KeyError` text |
| Writing `events.jsonl` (UTF-8, strict) | `encodeError`: a lone surrogate gives Python's `UnicodeEncodeError` text, which ends the reader |
| `codex_error` and its regular expression | `codexError`; `requestId` scans with case sets taken from Python's `re` for each letter of the pattern |
| `group_alive`, `int(bytes)` | `aliveLoop` over `Host.procList` and `Host.procStat`; `fields3` splits on ASCII whitespace as `bytes.split()` does; `intEquals` follows `int()`'s order of checks (underscores before the digit limit, other garbage after) and its message, whose `repr` is cut to 200 characters (`%.200R`) |
| `start_process`'s environment | `envFilter` drops the seven `GIT_*` variables |

Case outputs are the driver's lines: `start: <info>`, then one line per step (`models`, `thread` with the network access, `turn`), `close`, and `end:` with the transport error, thread ID and turn ID. An exception prints as `<step>: !Type: text`.

Out of scope: `_spawn`'s pipe plumbing, `communicate`, `run`, and cancellation of `start_process` while it spawns. These are the host's side of the boundary. Python's `RecursionError` for deeply nested JSON is also out, as in probe 19.

## What was authored

| Part | Functions / nodes |
| --- | --- |
| Types | 21, among them `Json`, `Exc`, `Chunk`, the `Ev` union of task results, `St` (25 fields), `Handler`, `Timer`, `Step` and `Case`; 1 effect; `Host` with 21 operations |
| JSON values, `json.dumps`, `repr`, `str`, Python's lookups | 43 / 677 |
| `bytes.decode` as `json` does it | 20 / 877 (`u8Three` 138, `u8Seq` 133, `u8Four` 125, `wideStep` 118) |
| `json.loads` scanner | 13 / 1,048 (`scanOnce` 244, `scanString` 243, `matchNumber` 236) |
| Logs and `codex_error` | 11 / 367 (`codexError` 162) |
| Reader | 9 / 391 |
| Event loop: rounds, timers, `gather`, `wait_for` | 19 / 290 |
| Handler | 5 / 208 |
| Requests, `models`, `start_thread`, `turn` | 12 / 602 (`request` 102) |
| Process group | 12 / 439 |
| `start`, `close` | 9 / 329 |
| Driver and case dispatch | 7 / 357 |
| **Total** | **160 / 5,585** |

Order 41's `otherwise` is used in 24 matches (22 over `Json`, 2 over `Ev`), order 49's tail self-calls with order 54's `keepUnlisted` in 23 loops, and order 51's `ListFindIndex` in 5 places.

**Rejections.** One was a language limit: `u8Seq` was 376 nodes, over the 256-node recipe limit, and the diagnostic named the 256-node subtree to move out. It was split into `u8Seq`, `u8Three` and `u8Four`. Three more nodes than 236 would have put `scanOnce` and `scanString` over the limit too. One was a slip (a placeholder body of the wrong type). The rest were "Unknown function": the session needs a callee before its caller, and the source is grouped by topic, so the harness retries a definition after each new one. The final run made 105 such retries across 291 proposals.

**Port bugs found by the cases (2).** Both were in the port, not the language. A response's `error` became `CodexError(repr(error))` where Python uses `str()`. `int()`'s error message did not cut its `repr` at 200 characters. Both were fixed, and the run then matched every case except the known divergence.

## Results vs Python

490 cases. The oracle ran rrawrlet's real `codex.py` on an asyncio event loop whose clock is virtual: the selector advances time to the next timer instead of sleeping. A scripted app-server answers each line the client writes after a given number of milliseconds. Only the process, its pipes, `/proc`, `killpg` and the callbacks are scripted, and every effect is recorded with its time and coroutine. The port's fake is derived from that record. A read answers the next chunk after the delay since the previous one. A tool call or `process.wait` answers after its Python duration, or waits for cancellation where Python cancelled it. Sleeps answer after their own length. A case matches when its output is equal and the port made the same host calls with the same arguments in the same order. Reads and sleeps are left out of the order, because the fake supplies their timing.

| Group | What it covers | Result |
| --- | --- | --- |
| `json.loads(bytes)` (136) | Every scanner message and position; numbers at the edges (`-`, `01`, `1.`, `1e+`, `2e308`, Int64 bounds, 4,300 and 4,301 digits); escapes and escaped pairs; raw control characters; invalid UTF-8 of every kind, encoded surrogates (`surrogatepass`); a BOM; UTF-16 and UTF-32 with and without a BOM, truncated, and beyond U+10FFFF | 136/136 |
| `repr` and `str` (42) | Floats, big integers, quotes, control and format characters, unassigned code points, lone surrogates, nested values | 42/42 |
| `group_alive` (36) | `)` in the command, missing fields, whitespace kinds (`\x0b`, `\x0c`, `\x1c`, `\xa0`, `\x85`), `+77`, `-0`, `0077`, underscores, 4,300 and 4,301 digits with `_` or garbage after, non-ASCII digits in names and fields, unreadable entries, `/proc` unreadable | 36/36 |
| Environment (4) | Explicit and inherited, every `GIT_*` name, near misses | 4/4 |
| Sessions (272) | Start, thread and turn; 11 `config/read` and 36 `thread/start` shapes with three sandboxes; model paging and falsy cursors; responses matched by `1.0`, `true`, `"1"`, `[1]`, `-0`, duplicates; 10 error payloads; 15 notification shapes (lone surrogates end the reader through the log); 38 transport-error records, including the regular expression's case folding; malformed lines, partial lines, EOF and exit at each step; request timeouts at 59,999 and 60,001 ms; turn deadlines with every interrupt outcome; tool calls with delays, 16 exception types (caught and not), 10 malformed parameter shapes, other server requests, no `on_tool`; `close` with mutating and other tools still running; `SIGTERM` ignored, slow `SIGKILL`, a descendant holding the group, held stdout with leftover data, `killpg` and `/proc` errors; `mkdir`, spawn and `initialize` failures | 271/272 |
| **Total** | | **489/490** |

The 490 cases took 41 runs and 29 s, about 0.5 s per warm run. Changed outputs and two swapped calls were reported as failures, so the comparison is not vacuous.

**Known divergence (1 case).** In `s-hold-leftover-req`, `terminate_group` hands the reader leftover data that holds a server request, and then closes the pipe. In Python, the reader starts the handler task and ends, and `close` is woken by `await self.reader`. The handler was scheduled first, so it writes its refusal (`BrokenPipeError`, suppressed) before `close` closes the logs. In the port, the main thread keeps running at the same instant until it blocks. It closes the logs first, and the handler runs when the scope ends. The output is the same; the order of two host calls differs. Nyrwvy has no operation that gives way to other ready tasks without waiting for time to pass, so this order cannot be written.

**Same-instant races were designed out.** In asyncio, which of two things due at the same millisecond runs first depends on the order of the loop's ready queue. The cases keep apart: a server request and the line that completes the caller's wait (in different chunks); data and a timer's expiry (at odd millisecond offsets); and a handler's start and `close`. A Python attribute read when a task first runs, such as `self.closed` in `_handle`, is passed to the port's task at its start. That is the same value only because nothing happens in between.

## Scale

Warm `runPure` wall time of one session, including process start. Every row matched Python.

| Session | runPure | Python | Host calls compared |
| --- | --- | --- | --- |
| 240 notifications, one per chunk | 0.84 s | 5.7 ms | 508 |
| 240 notifications, 40 per chunk | 0.60 s | 2.9 ms | 508 |
| One 60 KB notification | 0.56 s | 1.3 ms | 28 |
| 60 tool calls | 0.67 s | 4.6 ms | 208 |

The fixed cost of a run dominates. The rows stop at 240 because of the fake's limits, not the language's. A fake operation takes at most 256 responses (`events.jsonl` gets one write per notification), and each argument value at most 65,536 UTF-16 units: 200 tool calls exceeded it.

## Costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **No futures or channels between tasks** (first observation) | `_read` resolves a future that `request` awaits, from another task. A Nyrwvy task returns one value when it ends and sees only its start arguments. So the reader cannot be a task, and the port folds it into the caller as an event loop over a 25-field state that every function threads through: 40 functions and 1,283 nodes for the reader, the rounds and the requests. Each read is a task, and its result is handled by whoever is waiting | None proposed. The single loop is a clear, deterministic shape, and every wait point is explicit. A channel or a settable value that one task fills and another waits on would let the reader stay separate. It would need a design for cancellation and for the virtual clock, and one program is the only evidence |
| 2 | **No time value; timeouts are host sleeps** (first observation) | Timeouts, the turn deadline and `terminate_group`'s waits are tasks that call `Host.sleep` (`timer`, `timerWorker`, `firedTag`: 26 nodes, plus a `timers` parameter on `request` and `pump`). The turn deadline must be passed into every request inside the turn, because a later wait cannot compute the time left without a clock. `TaskWait`'s limit covers a single wait | None proposed. A monotonic `Now` would let one deadline span several waits. The [concurrency decision](../decisions/concurrency-control.md) kept time to wait limits. A deadline over several waits is the case it would need |
| 3 | **Strict decoding with Python's error text** (second consumer) | `json.loads` of bytes: encoding detection, UTF-8, UTF-16 and UTF-32 with `surrogatepass`, and `UnicodeDecodeError`'s text, which becomes the transport error callers see: 20 functions, 877 nodes. The host returns pipe chunks, not lines, so the decode has to follow the program's own line splitting. Probe 16 was the first consumer | The [UTF-8 decode decision's](../decisions/utf8-decode.md#revisit-conditions) strict-decoding condition names this case: a second consumer needing the error text from a module that cannot leave decoding to the host. The mode needed here is `json`'s (`surrogatepass`, UTF-16 and UTF-32), which the decision's options do not name. Presented for the owner's decision; not queued |
| 4 | **Python library behaviour by hand** | `json.loads` with CPython's messages and positions (1,048 nodes, new, because the message reaches the output), `repr`, `str`, Python's exceptions on the wrong type, `int(bytes)`, `re.IGNORECASE` case sets | None. These are Python's behaviour, not missing language. Three scanner functions are within 20 nodes of the recipe limit |
| 5 | **No zero-time yield** (first observation) | 1 divergence (above), and the cases had to keep same-instant events apart | None proposed. A yield would make order at one instant part of a program's meaning. The divergence is harmless here, and one probe is the only evidence |

**Harness-side costs, not language costs.** A fake cannot answer "after this delay, unless cancelled first". So where Python cancelled a tool call or `process.wait`, the fake waits for cancellation instead. The case then encodes when Python cancelled, and the comparison checks the calls around it rather than the timing itself. The fake's 256-response and 65,536-unit limits capped the scale rows.

**Effect of orders 41 to 55.** `otherwise` kept the 24 `Json` and `Ev` matches to the variants they handle; probe 19 paid 295 nodes for the same pattern. The 23 self-calls name only the arguments they change (`keepUnlisted`). `ListFindIndex` finds the scanner's next stop character and a fired timer. The persistent-vector list (orders 52 and 55) was not needed: the lists are short.

## Candidate hit counts after probe 25

| Candidate | Probes |
| --- | --- |
| `matchUnion` default arm | 7, 10, 17, 18, 19; done as order 41, used in 25 |
| Tail self-call as a loop, naming only the changed arguments | 15, 21, 23; done as orders 49 and 54, used in 25 |
| UTF-8 decode, strict with error text | 14, 16, 18, 19, 25 (the strict-decoding revisit condition, for the owner) |
| Futures or channels between tasks | 25 (1) |
| A time value for deadlines over several waits | 25 (1) |
| Zero-time yield | 25 (1) |
| Unbounded `int` | 18, 19 (2, small); here only as `int()`'s 4,300-digit check |

## Reproducing

The scripts are in private files outside the repository (`codex-probe25/`). `author.py <snapshot>` authors the port (it execs `jsonpart.py`) and writes `author_stats.json`. `oracle.py` runs rrawrlet's `codex.py` on the virtual clock with the scripted app-server, and needs an rrawrlet checkout at a fixed local path. `mkcases.py [out.json]` builds the 490 cases and the port's fake for each. `run_cases.py <snapshot> cases.json known.json` runs them, and `scale.py <snapshot>` gives the scale rows. `run-first.txt` and `run-final.txt` are the two full runs. Run the scripts with `PYTHONPATH=codex-probe25` and `NW_BIN` set to the built `Nyrwvy.AuthoringSession`.
