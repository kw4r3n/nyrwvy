# rrawrlet slice probe 7 — 2026-09-29

**Status:** Complete. A new part of rrawrlet, the OpenCode event observer (`opencode_events.py`: `usage_counts` and `OpenCodeObservations`), was authored by name only. It matched the real Python on 35/35 event sequences: every emitted update in order, the error that ends a sequence, and the final `usage_complete` and `missing_usage`. There were no recipe retries. The observer consumes untyped JSON, and representing JSON needs a recursive type, which the name-resolved surface could not declare: that was the one blocker, and it is now implemented ([order 26](../plans/post-m2-roadmap.md#findings-from-the-2026-09-29-probe-7)). The literal port of the observer's dict lookups was cubic and hit the `runPure` limit at 600 model steps; restructuring one function brought it within 3-10 times the Python time up to 19,200 events. The linear keyed lookups are then most of what remains, which is the first measured evidence for Map; nothing is blocked, so it stays deferred.

**Date:** 2026-09-29

Baseline: nyrwvy `ea35e73` (main after #258, clean); rrawrlet `ee03576`. The session server was built with `dotnet build tools/Nyrwvy.AuthoringSession --configuration Release -o <scratchpad>/p7/bin`. The oracle ran rrawrlet's real `OpenCodeObservations` with Python 3.11 and a recording `emit`, over deep copies of each event, as `opencode_backend` hands it freshly decoded JSON.

The method was probes 5 and 6's: the maintained `Nyrwvy.AuthoringSession` JSON-lines protocol with `simulateCreateTypeByName`, `simulateCreateFunctionByName`, `commit` and `runPureByName`, and no `declarations` call. A new client was written in the style of probe 5's `nw.py`, since earlier scratchpads are gone. Scripts, cases and transcripts are kept outside the repository in the session scratchpad `p7/`.

## Why this part

Probes 1-6 ported stateless text processing, a spawn/integrate skeleton over fakes, and one stateful guard; every input was already typed. The observer is the first part whose input is untyped JSON checked at run time (`type(value) is int`, `isinstance(tokens, dict)`, `info.get("time", {}).get("completed")`), and whose state is five dicts and sets keyed by message ID or by `(messageID, partID)`, with parts that arrive before their message buffered and replayed. That makes it the part most likely to hit the two things no probe had needed yet: a recursive type, and Map.

## What was ported

| Python | Port |
| --- | --- |
| A JSON value from `json.loads` | `Json` Union (`Null`, `Bool`, `Int`, `Float`, `Str`, `Arr(items: List<Json>)`, `Obj(members: List<Member>)`), `Member { key, value: Json }` |
| `d.get(k)`, `k in d`, `x is None`, truthiness, `str(x)` | `jsonGet` (last match, like `json.loads` with duplicate keys), `hasKey`, `fieldPresent`, `truthy`, `pyStr` |
| `usage_counts` and its `count` | `usageCounts : Result<Option<Usage>, String>`; `Usage` has one `Option<Int64>` per reported key |
| The set intersection and sums over known reports | `totalStep` folding `intersect` over the reports: a key is kept only when every report has it |
| `messages`, `reports`, `parts`, `text_lengths`, `started`, `actions` | `List<String>`, `List<Report>`, `List<StoredPart>`, `List<TextLength>`, `List<PartKey>` in insertion order, with upsert helpers |
| `emit(AttemptUpdate(...))` | `Update` Union appended to the `Obs` state; one variant per shape the module emits |
| `message`, `part`, `readback`, raising `OpenCodeError` | `messageEvent`, `partEvent`, `readback` over `Result<Obs, Failure>`; `Failure` carries the state at the error |
| `Path(workdir)`, `is_absolute()`, `Path(workspace) / cwd` | `normPath` (split on `/`, drop empty and `.` parts, keep `//` but not `///`), `stringStartsWith` |
| The dispatcher loop | `observe(config, events)`: `listEffectFold` over `Event` (`MessageEvent`, `PartEvent`, `ReadbackEvent`), stopping at the first `Err` |

Out of scope: `str()` of a float, list or dict (Python's `repr`), and inputs on which Python raises something other than `OpenCodeError` (a missing `id`, `state` or `messageID`, a `None` metadata dict). Python integers are unbounded; the port's `Int64` sums wrap. No case reaches these.

## What was authored

| Part | Declarations | Functions / nodes |
| --- | --- | --- |
| Types | 15 (`Json`, `Member`, `Usage`, `Activity`, `Command`, `Update` with 5 variants, `Event`, `Config`, `PartKey`, `StoredPart`, `Report`, `TextLength`, `Obs` with 8 fields, `Failure`, `Outcome`) | — |
| JSON access | — | 17 / 175 (`truthy` 23, `owns` 16) |
| `usage_counts` and totals | — | 11 / 267 (`usageCounts` 58, `inputTokens` 48) |
| `Obs` setters and `emit` | — | 9 / 134 |
| Map emulation | — | 17 / 150 (`setPart`, `setReport`, `setTextLength` 18 each) |
| `part`, `message`, `readback`, entry | — | 21 / 595 (`bashCommand` 68, `readbackFinish` 66, `partEvent` 61) |
| **Total** | 15 types | **75 / 1321**; 184 requests / 134 KB / 155 KB |

There were **no recipe retries**. The deepest pattern reference is depth 5 (`usageCounts` chains six `matchResult`s). Two harness mistakes sent requests that were rejected with a clear message: a missing `requiredEffects` (the property is required even when empty) and a function created before its callback (`Unknown function 'keepPart'`).

## The blocker: recursive types by name

`simulateCreateTypeByName` resolves every named field type against published types only, so `Json` could not name itself (`Unknown type 'Json'.`), and `Json` and `Member` could not name each other. The [type-declaration spec](../specs/type-declaration-authoring.md) said so, and the [declaration-authoring decision](../decisions/declaration-authoring.md#revisit-conditions) listed self-recursive and mutually dependent creation as revisit conditions.

The probe worked around it on the exact-ID surface: create `Json` without `Arr` and `Obj`, add `Arr` with `simulateAddUnionVariantLike` naming the new TypeId, create `Member`, then add `Obj` the same way. That is 4 extra requests and TypeIds and a VariantId copied out of reviews, and it works only because `Json` is a Union; a recursive Product needs a field-type migration. The ordinary AI-facing tools expose it only through the generic `request` tool with exact IDs. On the name-resolved surface a recursive type could not be declared at all.

**Implemented (order 26):** `simulateCreateTypesByName` creates a group of types that may name themselves and each other, and `simulateCreateTypeByName` accepts its own name (a group of one). Group members resolve to request-local references that simulation replaces with allocated TypeIds; a member with no finite value (`Loop { next: Loop }`) is rejected as `TypeDeclarationUninhabited`. See the [decision](../decisions/recursive-type-declarations.md). Re-authoring with it, `Json` and `Member` are one request, 178 requests in all, and the port again matched 35/35.

## Results vs Python

| Group | What it covers | Result |
| --- | --- | --- |
| T1-T3 | rrawrlet's own unit tests: a tool starts and completes once, text streams only for owned messages, a foreign part in readback | **3/3** |
| H1 | A two-step session: streamed text (80-character threshold, a 700-character text with astral characters cut to the last 512), a bash tool, two completions, readback with ignored and non-text parts | **1/1** |
| U1-U10 | `usage_counts`: missing or null total, total only, negative, `True` and `3.0` counters, partial categories and their intersection, a bad counter in cache, output checked only with reasoning, input used only with both cache counters, non-dict tokens | **10/10** |
| R1 | Re-completion with equal and changed totals, a completed message without tokens (missing usage) | **1/1** |
| O1-O3, P1 | Ownership (role, session, parent, null session), a provider or a missing model changing, parts before their message and their replay on every later message event | **4/4** |
| L1, B1, S1 | Tool labels (`0`, `5`, `True`, `""`, `None`, a list input), bash working directories (`/tmp/x/`, `//srv//a`, `///srv`, `..`, `./`), interrupted, exit codes of any JSON type, durations with bad or reversed times, tool statuses including `None` and `7` | **3/3** |
| X1 | Text: empty, non-string, `ignored` of `True`/`False`/`0`, 600-680 emoji, a part without a type | **1/1** |
| RB1-RB12 | Readback: idle, an earlier message's error, errors without or with a null name, a falsy error, not completed, `length`, missing and null finish, foreign messages skipped and text parts joined, a part from another session, a model change | **12/12** |

A negative control (one expected working directory changed) reported a mismatch at `Outcome.updates[1].command.some.cwd`. 35 runs took 98 s, about 2.8 s each, mostly the per-run build.

## Scale

Real event logs were not available in the container, so the stream was synthesized: each model step is a message, a text part updated `deltas` times, a bash tool running and completed, and the completed message (`4 + deltas` events). Because `runPure` limits an argument to 8,192 value nodes (about 300 such events), the events were generated inside the program by `synthAll` (8 functions, 259 nodes) and `scaleRun` returned a summary; update counts matched Python at every size. Times are wall time minus the same request running only the generator, so each includes about ±0.5 s of build noise. How many events a real long run produces is an estimate, not a measurement.

| Steps × deltas | Events | Python | Literal port | After restructuring |
| --- | --- | --- | --- | --- |
| 400 × 0 | 1,600 | 0.08 s | 2.4 s | 0.1 s |
| 600 × 0 | 2,400 | 0.15 s | `RunTimeout` (> 5 s) | — |
| 800 × 0 | 3,200 | 0.33 s | `RunTimeout` | 1.4 s |
| 1600 × 0 | 6,400 | 1.24 s | `RunTimeout` | 3.5 s |
| 400 × 20 | 9,600 | 0.12 s | 3.0 s | 1.7 s |
| 600 × 20 | 14,400 | 0.23 s | `RunTimeout` | 2.3 s |
| 800 × 20 | 19,200 | 0.40 s | `RunTimeout` | 3.7 s |

**The literal port was cubic.** `usage_complete` runs on every message event and computes `missing_usage`, a filter over messages with `reports.get(key)`; Python's lookup is O(1), the port's `reportFor` is a fold over the reports. A benchmark of only that step timed out at 800 messages. The fix needed no new builtin: report keys are always recorded messages, so the missing count is the message count minus the reports with usage, one fold (`knownStep`, 1 new function and one body replacement, 4 requests, still 35/35).

**What remains is the keyed lookups on every part event.** Parts, text lengths, started and actions are upserted or searched linearly, O(parts) per event where Python is O(1). A benchmark of only those operations cost about 1.2 s at 800 parts (9,600 events) and 3.4 s at 1,600 parts (19,200 events), so after restructuring they are most of the remaining time (about 70% and 90%). Python is itself quadratic here, since `message` replays over all stored parts, which is why the port stays within 3-10 times its time.

## Costs, ranked by impact

| # | Cost | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **Recursive types could not be declared by name** | `Json` naming itself; `Json` and `Member` naming each other; the workaround needed exact IDs and 4 extra requests | **Done:** order 26, [decision](../decisions/recursive-type-declarations.md). |
| 2 | **Dict lookups become linear folds** | The literal `missing_usage` was cubic and timed out at 600 steps; per-part keyed lookups remain O(parts) | None queued. This is the first measured consumer for the [Map revisit condition](../decisions/value-equality-and-map.md#revisit-conditions): after restructuring, linear keyed lookups are most of the remaining time, but the port finishes 19,200 events in 3.7 s, within 10 times Python, and a real observer handles one event at a time. Nothing is blocked, so Map stays deferred; queue it if a consumer needs it to finish, or the owner chooses it. |
| 3 | **Product update restates every field** | 8 setters for the 8-field `Obs` state, 134 nodes (10% of the port; probe 6 measured 4%) | None queued. A record-update form would remove them; revisit if a third probe grows it. |
| 4 | **`matchUnion` needs every arm** | 8 shallow `Json` matches spell out about 45 default arms | None queued; small. |
| 5 | **`runPure` input budgets limit event streams** | About 300 events per argument; scale needed an in-program generator, iterating `StringCodePoints` of `"x" * n` for a range | None queued; harness-only. |

`listEffectFold` over a `Result` accumulator served as the early-exit fold in a pure function, as its contract says; no effect was declared.
