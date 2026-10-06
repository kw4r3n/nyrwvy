# Probe 26: a reduced JSON-RPC client, two ways

Reduced from rrawrlet's `codex.py` (`~/Work/Tools/rrawrlet/rrawrlet/codex.py`: `request`, `_read`, `_handle`, `turn`, `_close`). JSON is out of scope: messages are typed values from the host. Both versions implement exactly this spec, with the shared declarations below, and pass every scenario in `cases.mjs`.

## Shared declarations (identical in both versions)

- Effect `Io`.
- Union `Out`: `Call { id: Int64, method: String, param: String }` | `Reply { id: Int64, ok: Bool, text: String }`.
- Union `In`: `Response { id: Int64, isError: Bool, text: String }` | `ServerCall { id: Int64, tool: String, args: String }` | `Notice { method: String, text: String }`.
- Capability `Pipe` (every operation has effect `Io`): `send(message: Out): Unit`, `read(): Option<In>` (`None` is end of input), `tool(name: String, args: String): Result<String, String>`, `event(method: String, text: String): Unit`, `sleep(ms: Int64): Unit`.
- Entry: `session(pipe: Pipe, prompt: String, requestLimit: Int64, turnLimit: Int64): Result<String, String>`, always `Ok(output)` for the scenarios.

Other types and functions are each version's own. Field and variant names above are fixed because the scenarios use them.

## Behavior

The session runs these steps in order inside one `TaskScope`; reading runs concurrently from the start.

1. `initialize`: request with method `initialize`, param `""` → output part `info=<text>`.
2. `thread/start`, param `""` → `thread=<text>`.
3. Turn: the turn deadline is `turnLimit` ms from the start of this step and covers both of the following. Request `turn/start` with param `prompt` → `turn=<text>`; then wait for the turn to complete → `result=<completion text>`.
4. Close (always runs, also after a failure) → `close=<status>`.

Output: the parts joined by `;`. If a step fails, append `error=<reason>` instead of its part, skip the remaining steps, and still close. Reasons: `timeout <method>` (request limit), `rpc <text>` (error response), `deadline` (turn deadline), `closed` (input ended while waiting).

**Requests.** IDs are 1, 2, 3, … in sending order (the interrupt in close takes the next one). A request sends `Call { id, method, param }` and waits for the `Response` with its ID, at most `requestLimit` ms (the interrupt: 2 ms), and during the turn also at most until the turn deadline (whichever comes first; the deadline wins a tie only if it is earlier). `isError` → fails with `rpc <text>`.

**Reading.** Each `read` result is handled in order:
- `Response`: completes the request with that ID if it is still waiting; otherwise (unknown, already answered, timed out) it is ignored.
- `ServerCall`: starts a handler task (below); reading continues at once.
- `Notice`: call `event(method, text)`. If `method` is `turn/completed`, the turn is finished with `text`.
- `None`: input ended. A request still waiting, and a turn still waiting for completion, fail with `closed`. Reading stops.

**Handlers.** A handler started after close began sends `Reply { id, ok: false, text: "unavailable" }`. Otherwise it calls `tool(tool, args)`; if its cancellation was requested by then it sends nothing; else it sends `Reply { id, ok: true, text }` for `Ok`, `ok: false` for `Err`.

**Close.** Mark the session closed. Request cancellation of every running handler whose tool is not `commit`, then wait for all handlers started so far to finish. Then, if the turn started (`turn/start` answered), has not finished, and input has not ended: send the interrupt request (`turn/interrupt`, param = the turn text, limit 2 ms), then wait at most 2 ms for the turn to finish. Status: `interrupted` if both succeeded, `interrupt-failed` otherwise; `ok` when no interrupt was needed. Leaving the scope then stops reading.

Time comes only from `sleep(ms)` (the fake answers after `ms` virtual ms for 2, `requestLimit` and `turnLimit`) and from `TaskWait`/`OneShotWait` limits. There is no clock value.

## Rules

- Author through the ordinary tools with `lib.mjs` (import it; do not change it) and run `cases.mjs`'s scenarios through `runPureByName` on `session`: every result must match `expected(s)`, and the `Reply` messages sent must equal `s.replies` as a multiset. Do not change `cases.mjs`'s scenarios or expected outputs; if you believe one is wrong under this spec, stop and report why.
- Write ordinary, readable code as you would for a real program; do not golf node counts or inflate them.
- Record per function: name, node count (`nodes` from `lib.mjs`), and a one-line role. Report total functions and nodes, and which functions are the reader, the request/wait path, and the handler/close path.
