# rrawrlet slice probe 8 — 2026-09-29

**Status:** Complete. A new part of rrawrlet, secret redaction over decoded JSON (`OpenCodeAttempt.redact` and `redact_value`, and the secret set from the provider config) with the JSON helpers of `diagnostics.py` (`usage_counts` and its deltas, `valid_http_status`, `present_request_id`, `terminal_message`, `terminal_threads_compatible`, `http_summary`, `tool_response`), was authored by name only. It matched the real Python on 113/113 cases with no recipe retries, and on generated input it kept up with Python: 64,000 tool events redacted in about the same time, and nesting four times deeper than Python's `redact_value` survives. It was chosen to measure the one revisit condition order 26 left open: traversing a recursive type with list operations needs a function and its callback to call each other. That cost 2 placeholder bodies and 4 extra requests of 122, so the placeholder route stays. What it did not do was say so: a callback naming the function being created was rejected as `Unknown function 'redactValue'`, although a `directCall` by that name is a self call. That message now names the route.

**Date:** 2026-09-29

Baseline: nyrwvy `fcaa805` (main after #259, clean); rrawrlet `ee03576`. The session server was built with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release -o <scratchpad>/p8/bin`. The oracle ran rrawrlet's real `redact`, `redact_value` and `diagnostics` functions with Python 3.11. `OpenCodeAttempt.__init__` validates the provider and resolves paths, so the oracle built the instance with `object.__new__` and computed the secret set with the constructor's expression copied verbatim.

The method was probe 7's: the maintained `Nyrwvy.AuthoringSession` JSON-lines protocol with `simulateCreateTypesByName`, `simulateCreateTypeByName`, `simulateCreateFunctionByName`, `simulateReplaceBodyByName`, `commit` and `runPureByName`, and no `declarations` call. Probe 7's client and `Json` helpers were reused. Scripts, cases and transcripts are kept outside the repository in the session scratchpad `p8/`.

## Why this part

Probe 7 declared `Json` by name but never traversed it: every access was a lookup at a fixed depth. rrawrlet redacts every OpenCode event and readback before persisting it with `redact_value`, which recurses into each list item and dict value, and `diagnostics.py` compares thread IDs with Python `==`, which recurses the same way. Both are the shape the [recursive type decision](../decisions/recursive-type-declarations.md#revisit-conditions) left open: `redactValue` maps itself over `Arr` items, and maps a `Member` callback that calls it back. The diagnostics helpers add the remaining JSON idioms the observer did not need: `type(n) is int` against `bool`, `str.strip()` over Unicode whitespace, a `Counter` with sorted keys, and `"".join` behind two validation errors.

## What was ported

| Python | Port |
| --- | --- |
| `{v for v in (options.get("apiKey"), *options.get("headers", {}).values()) if isinstance(v, str) and v}` | `secrets(config: Json) : List<String>`, first occurrence kept, over `listConcat` and `listMap` of the header values |
| `redact`: `sorted(secrets, key=len, reverse=True)`, then `text.replace(secret, "[REDACTED]")` | `listSortBy` with `longer`, then a `listFold` of `StringJoin(StringSplit(text, secret), "[REDACTED]")` |
| `redact_value` | `redactValue(value: Json, secrets)`: `Str` redacted, `Arr` items by `listMap` over `redactValue` itself, `Obj` members by `listMap` over `redactMember`, which calls `redactValue` |
| `usage_counts`, and the delta against a baseline in `diagnose_attempt` | `usageCounts : Option<List<Count>>` in `USAGE_KEYS` order; `usageDelta : Result<Option<List<Count>>, String>` |
| `valid_http_status`, `present_request_id` | Int arm with bounds; `pyStrip` with `StringSpan`/`StringSpanBack` over Python's 29 whitespace code points |
| `terminal_message` | `terminalError` and `terminalMessage` over `jsonGet` and `fieldIs` |
| `terminal_threads_compatible` and Python `==` | `pyEqual(left, right: Json)`: `True == 1 == 1.0`, NaN unequal, lists item by item (`itemEqualStep` via `listItemAt`), dicts by key regardless of order (`memberEqual`); both callbacks call `pyEqual` |
| `http_summary` with its `Counter` | `Observation { available, status: Option<Int64>, requestId }`; a `StatusCount` upsert fold, `listSortBy` by status, `unavailable xN` last |
| `tool_response` up to `json.loads` | `toolResponse : Result<ToolResponse, String>` (`Text` or `Unavailable`); the two `ValueError`s are `Err` |

Out of scope: `json.loads` itself (the port receives decoded JSON, as the observer did; decoding is at the host boundary), the byte-level redaction of the log stream in `_read_log`, and equality between an integer and a float beyond 2^53, where `Int64ToFloat64` rounds and Python compares exactly. Python orders equal-length secrets by set iteration, which changes with the hash seed, so no case has two overlapping secrets of one length; the port keeps first-seen order. The oracle's expected values were identical under four hash seeds.

## What was authored

| Part | Declarations | Functions / nodes |
| --- | --- | --- |
| Types | 7 (`Json` and `Member` in one group request, `Count`, `StatusCount`, `Observation`, `ToolResponse`, `Indexed`) | — |
| JSON access (probe 7's, as needed) | — | 10 / 95 |
| Secrets and `redact` | — | 10 / 79 (`secrets` 29) |
| `redact_value` | — | 3 / 29 (`redactValue` 18) |
| `usage_counts` and delta | — | 5 / 104 (`usageCounts` 35) |
| Status, request ID, terminal message | — | 5 / 73 |
| Python `==` and thread IDs | — | 6 / 175 (`pyEqual` 113) |
| `http_summary` | — | 9 / 130 (`httpSummary` 67) |
| `tool_response` | — | 4 / 88 (`toolResponse` 57) |
| **Total** | 7 types | **52 / 773**; 122 requests / 91 KB / 97 KB |

There were **no recipe retries**. The two requests that failed were planned: each first tried to create a recursive function directly, to measure what happens.

## Recursion through callbacks: measured

`redactValue` passes itself to `listMap`, and `redactMember`, its `Obj` callback, calls it back; `pyEqual` has two such callbacks (`itemEqualStep`, `memberEqual`). A `directCall` naming the function being created is a self call ([`SelfCall`](../specs/expression-authoring.md#selfcall)), but a callback, worker or `functionReference` needs a published FunctionId. Each cycle was therefore authored as: create the function with a 1-node placeholder body (`value`, `false`), create the callbacks, then `simulateReplaceBodyByName` with the real body, which resolves the function's own name. The real body is written once either way.

| Cycle | Direct attempt | Route | Extra |
| --- | --- | --- | --- |
| `redactValue` ↔ `redactMember` (and `redactValue` as its own callback) | `Unknown function 'redactValue'.` | placeholder, callback, replace | 1 reviewed transaction (2 requests) |
| `pyEqual` ↔ `itemEqualStep`, `memberEqual` | `Unknown function 'itemEqualStep'.` | placeholder, 2 callbacks, replace | 1 reviewed transaction (2 requests) |

So the cost is one extra reviewed transaction per recursive cycle, 4 of 122 requests here, and a placeholder that is live between two commits. It does not justify function groups with request-local references, the counterpart of order 26 for functions, and the [decision](../decisions/recursive-type-declarations.md#revisit-conditions) is updated to say so.

The first rejection was the real cost. `Unknown function 'redactValue'` names the function the author is creating, while a `directCall` to it in the same body would have worked, and nothing points to the route. **Changed in this probe:** a callback (`listMap`, `listFilter`, `listAny`, `listAll`, `listSortBy`, `listFold`, `listEffectFold`), worker, task or capability implementation, or `functionReference` naming the function being created by name now fails with `Function 'redactValue' is the function being created: only a directCall to it is a self call. A callback, worker or functionReference naming it (and mutual recursion through another new function) needs it to exist, so create it with a placeholder body, create the functions it calls back, then give it the real body with simulateReplaceBodyByName.` A replacement is unaffected, since the function then exists. The second rejection names a callee that does not exist yet, which an author can act on; it is unchanged.

## Results vs Python

| Group | What it covers | Result |
| --- | --- | --- |
| S1-S9 | The secret set: no config, empty config, no options, non-string and empty values, a header repeating the API key, a config with only `models` | **9/9** |
| D1-D9 | `redact`: longest first (`tok` inside `Bearer tok123`), repeats, a secret matching text inside an earlier `[REDACTED]`, astral and accented secrets, overlapping runs (`aaa`, `aa` over `aaaaaaa`) | **9/9** |
| V1-V8 | `redact_value`: every JSON kind, keys never redacted, 7-deep lists, a realistic tool event, no secrets | **8/8** |
| U1-U11, UD1-UD4 | `usage_counts` (non-dict, empty, `True`, `3.0`, negative, `None`, unknown keys, key order) and deltas (no baseline, invalid, disjoint and partial keys, negative deltas) | **15/15** |
| H1-H10, R1-R10 | HTTP status bounds against `True`, `200.0` and `"200"`; request IDs that are blank under every Python whitespace code point, `​` (not whitespace), non-strings | **20/20** |
| T1-T11 | `terminal_message`: both methods, non-string or empty messages, a non-dict error, turn or params, a non-dict event | **11/11** |
| C1-C21 | Thread IDs by Python `==`: absent and null, `1 == True == 1.0`, `0 == False`, `-0.0 == 0`, infinities, nested lists and dicts in another key order, dict against list, unequal nested values | **21/21** |
| HS1-HS6 | `http_summary`: none, one, mixed statuses sorted with `unavailable` last, only unavailable, out-of-range statuses, an empty request ID | **6/6** |
| TR1-TR14 | `tool_response`: `success` of `False` and `0`, `failed`, missing, null, empty and non-list content, non-dict items, non-string text, all-empty text, joined text | **14/14** |

113 runs took about 250 s, about 2.2 s each, mostly the per-run build. A harness error was found on the way: probe 7's run-value converter wrote an infinite float as a JSON number, which the protocol rejects ([`"Infinity"` is a string](../specs/agent-authoring-session.md)).

## Scale and depth

The inputs were generated inside the program, as in probe 7, because a `runPure` argument is limited to 8,192 value nodes and 32 layers (a JSON object level is 3 layers: `Json`, `List`, `Member`). Times are wall time minus the same request running only the generator, about ±0.5 s of build noise.

| Tool events (about 25 JSON values each) | Python `redact_value` | Port | Python `==` | Port `pyEqual` (with a second generator run) |
| --- | --- | --- | --- | --- |
| 16,000 | 0.26 s | 0.5 s | 0.15 s | 0.4 s |
| 64,000 | 0.96 s | 0.9 s | 0.69 s | 0.7 s |

Depth was measured on `{"a": [ … "tok" … ]}` nested `d` times. Python's `redact_value` raises `RecursionError` from 250 levels (about 4 frames per level under the default limit of 1,000), and a `json.dumps`/`json.loads` round trip fails from 500. The port redacted and compared 1,000 levels and overflowed the stack by 2,000, with `runPure`'s "recursion was too deep" message. Every input `json.loads` can produce is therefore within reach; the difference is that Python raises a catchable error where the port's process aborts, as the [language subset](../reference/language-subset.md) documents.

## Costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **A callback to the function being created was rejected as unknown** | The direct attempt at `redactValue` failed with `Unknown function 'redactValue'`, while a `directCall` by that name is a self call | **Done:** the rejection names the placeholder-then-replace route. |
| 2 | **Recursion through callbacks needs a placeholder** | 2 cycles, 4 extra requests of 122, a 1-node placeholder live between two commits | None queued: function groups would remove it, but the measured cost is one transaction per cycle. Revisit if a probe needs several interlocking cycles or a placeholder causes a wrong run. |
| 3 | **Python `==` across JSON number kinds is hand-written** | `pyEqual` is 113 nodes, the largest function; its `Bool`, `Int` and `Float` arms spell out the coercions between them | None queued: `ValueEqual` is structural by design, and Python's numeric tower is Python's. |
| 4 | **Equal-length secrets have no defined order in Python** | `sorted` over a set is stable over hash order, so overlapping secrets of one length redact differently per run | None; a note for rrawrlet, not Nyrwvy. |

Map was not needed: the only keyed state, the status counter, has a handful of keys.
