# Task communication probe 26 — 2026-10-07

**Status:** Complete. This is the measurement chosen as option A of the [design note on communication between tasks and time](../plans/task-communication-and-time.md#1-communication-between-tasks). The same reduced client was written twice, once as probe 25's event loop and once with a reader task and scoped `OneShot` cells. Both pass the same 9 scenarios. They are about the same size (508 and 491 nodes), so `OneShot` does not remove probe 25's reader cost, and nothing here asks for a channel.

**Date:** 2026-10-07

Baseline: nyrwvy `1a180ec` (main after #368), session server built with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release` on .NET SDK 10.0.112.

## Why reduced, and what that means

Probe 25's harness, cases and snapshot were kept outside the repository and are not available on this machine, so its port could not be re-used. Instead, one specification was reduced from rrawrlet's `codex.py` (`request`, `_read`, `_handle`, `turn`, `_close`), and both versions implement exactly it:

- Messages are typed values from the host; JSON, logs, `codex_error` and the process group are out of scope.
- The host is a `Pipe` capability: `send(Out)`, `read(): Option<In>`, `tool(name, args)`, `event(method, text)` and `sleep(ms)`. `Out` is `Call | Reply` and `In` is `Response | ServerCall | Notice`.
- The session sends `initialize`, `thread/start` and `turn/start` with sequential IDs and a per-request limit, waits for `turn/completed` under a turn deadline that covers both, and always closes: it cancels non-`commit` handlers, waits for all handlers, and interrupts an unfinished turn with a 2 ms limit.
- A handler started after close began replies `unavailable`; a cancelled handler replies nothing. A response with an unknown or already answered ID is ignored. End of input fails whatever is waiting with `closed`.

There is no Python oracle: correctness is the 9 scenarios' expected outputs and reply sets, written from the specification before either version. The two versions were written by two separate agents (Claude Opus 5.5) from the same specification, helpers and scenarios, so some of the difference is authoring style. The harness and both programs were kept in the session's scratch directory and are not committed.

## Scenarios

| Scenario | What it checks | Event loop | OneShot |
| --- | --- | --- | --- |
| `happy` | Three requests, a notice, turn completion | Pass | Pass |
| `ignored-ids` | Unknown ID before the first response; a duplicate after it | Pass | Pass |
| `tools` | Two server calls during the turn; `Ok` and `Err` replies | Pass | Pass |
| `request-timeout` | `thread/start` never answered | Pass | Pass |
| `rpc-error` | An error response | Pass | Pass |
| `deadline-interrupt` | Turn deadline at t=110; interrupt answered at 111, turn completes at 112 | Pass | Pass |
| `deadline-interrupt-unanswered` | Interrupt and turn wait both time out | Pass | Pass |
| `eof` | Input ends while the turn waits | Pass | Pass |
| `close-with-handlers` | Close cancels `lint`, waits for `commit` (until t=220), and answers a server call that arrives during close with `unavailable` | Pass | Pass |

Each run checks the output and the multiset of `Reply` messages sent. Both versions were re-run independently after their authors finished.

## Size and shape

| | Event loop | OneShot |
| --- | --- | --- |
| Functions / nodes | 26 / 508 | 17 / 491 |
| Reader | 6 / 120 | 4 / 127 |
| Requests and waiting | 10 / 176 | 7 / 199 |
| Handlers and close | 8 / 148 | 4 / 110 |
| Entry | 2 / 64 | 2 / 55 |
| Declared types added | 4: `St` (11 fields), `Wake`, `Waiting`, `Handler` | 2: `Pending` (id, answer cell, next cell), `Started` (tool, task, next cell) |

**Event loop.** The caller's loop (`pump`) waits with `TaskWait(Any)` over the pending read task and the timer tasks, and folds the results through the handlers. Everything it learns goes into an 11-field state: the pending request, the outcome of the current wait, the turn result, the handlers, the closed and ended flags, the output parts. The costs are the state itself (13 of `body`'s 59 nodes to build it; `settle`, `expect` and a reset before every wait), one result type for everything waited on, and a "stopped" check repeated at each step. Folding the wait results in list order gave the tie rules for free.

**OneShot.** `readLoop` is its own task from the start, as `_read` is. The driver appends a `Pending` link (ID, answer cell) to a chain before each request. The reader completes the answer for a matching response and moves past it, and appends a `Started` link per handler. A turn cell, a `started` cell and a `closing` cell carry the rest. No state Product is threaded through the program. The costs moved elsewhere:

- A cell cannot be awaited together with a task. Each such wait starts a task that waits on the cell and races it with `TaskWait(Any)` (`awaitCell`, `race`, `firstDone`: 26 nodes), then cancels the loser. This happens once per request and twice per turn.
- There is no counter, so `enqueue` walks the request chain from its head on every request and takes the position as the ID (24 nodes, linear per request).
- The reader checks the request chain with 0 ms waits. This is safe only because one sequential driver issues requests, so anything the reader skips has already timed out. This matches `codex.py`. Several producers would need the retry loop the design note described.
- `close` walks the handler chain with 0 ms waits to find the tasks to cancel and join.

Both versions name earlier values by pattern depth through deep `Bind` chains: up to depth 6 in the OneShot `steps` (99 nodes), and depth 2 through five binds in the event loop's `closeSession`.

**Rejections.** None in the event-loop version. In the OneShot version, one `ProtocolError` (`Unsupported pattern slot 'id'`) came from the author's recipe syntax: a union arm's field is `slot: 'field'` with `type`, `variant` and `field`. No language limit was hit. Union variants without fields, and a `TaskScope` passed through a fold callback's context, were accepted.

## Findings

1. **The mailbox chain works with the existing surface.** A recursive type through a `OneShot` field, a `TaskScope` passed to a task, a reader that creates cells as it goes, and a consumer that waits with a limit were all accepted and ran deterministically. A smaller check before this probe (a reader feeding three items to a driver through such a chain, 63 nodes) passed its three cases too.
2. **It is not cheaper.** A reader task with cells replaces probe 25's event-loop state, but the total is within 4% (491 against 508 nodes). The saving in state handling (no `St`, no outcome field) is spent on racing cells against tasks and walking chains.
3. **A channel would remove only part of that.** A scoped channel would replace the chain walks: `enqueue` (24) and parts of `deliver` (24) and `stopHandlers` (23), at most about 70 of the 491 nodes. It would not remove the race helpers, because waiting on a channel together with a task still needs a task per wait. Removing those would also need a wait over cells and tasks together. One program does not justify a channel and a combined wait.
4. **For `codex.py`, the direction is the reader to the driver.** The design note assumed requesters must register cells with a running reader and might race on the tail. Only the sequential driver issues requests, so a single-producer chain is enough. The note is corrected.

## Consequences

- No channel is queued. The open candidate stays open, with this record as its evidence.
- The design note's revisit condition for option B becomes concrete: a program with several concurrent producers into one consumer, or one where racing a cell against a task is a large share of its cost.
- Monotonic time (option D) and the zero-time yield are unchanged by this probe. Both versions computed deadlines with `sleep` timer tasks, as probe 25 did.
