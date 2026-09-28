# rrawrlet slice probe 3 — 2026-09-28

**Status:** Complete. The rebuilt slice and a new `usage_stats` part match Python, and the skeleton passes its scenarios. The 2026-09-28 queue (orders 6-10) removed its target workarounds. New gaps are recorded below.

**Date:** 2026-09-28

Baseline: nyrwvy `5331de0` (main after #232 display names); rrawrlet `c9af98f`. `rrawrlet/team.py` is unchanged since the [re-probe](rrawrlet-slice-reprobe-20260928.md)'s `903f2ce`. rrawrlet's working tree had unrelated uncommitted edits, but `team.py` and `usage_stats.py` were clean, and `saved_runs` was replaced by an in-memory fake, so those edits did not reach the probe. Python 3.14.7 ran the real functions. The `team.py` functions were extracted read-only. `usage_stats()` ran with only `saved_runs`, `run_usage` and `_one_shot_records` faked.

The probe used the same method as the re-probe: only the maintained `Nyrwvy.AuthoringSession` JSON-lines protocol with name-resolved requests (`simulate*ByName`, `commit`, `runPureByName`). It needed no `declarations` call, no exact ID and no ID→name map. This time it removed the workarounds that orders 6-10 made unnecessary and added one new pure part: the aggregation core of `rrawrlet/usage_stats.py` (`_group`, row building and the `(role, -count)` sort, without file I/O). Scripts and transcripts are kept outside the repository in the session scratchpad `probe3/` (`data3.py`, `team3.py`, `usage3.py`, their `run_*3.py` drivers, `*_transcript.jsonl`, `limit_probe.txt`, `float_probe.txt`).

## What was authored

Request figures count authoring requests only. Node counts come from the same counter, which matches the server's own count (383 = 383 in the limit diagnostic).

| Part | Types | Functions | Requests / request JSON | Recipe nodes | Trial and error |
| --- | --- | --- | --- | --- | --- |
| Data, re-probe | 3 | 12 (+1 body replacement) | 33 / 17.4 KB | ~199 | 1 (self-reference rejection) |
| **Data, now** | 3 | 8 | 22 / 12.2 KB | 150 (`preview` 56) | 0 |
| Leader skeleton, re-probe | 7, plus 1 effect and 4 capabilities | 16 (+1 body replacement) | 58 / 44.4 KB | ~379 | 1 restructure |
| **Leader skeleton, now** | 5, plus 1 effect and 4 capabilities | 9 | 38 / 32.3 KB | 331 (`teamLoop` 206 in one body) | 1 (`scopeDepth`, friction 5) |
| **`usage_stats` core (new)** | 7 | 24 | 62 / 31.9 KB | 324 | 0 |

## Results vs Python

| Part | Checks | Result |
| --- | --- | --- |
| Data | The re-probe's 26 cases, unchanged | 26/26. Results were read through the output's `field` names. |
| `usage_stats` | 7 cases: empty; odd count, unsorted; even count across runs with passed/failed verification; missing, `""` and non-numeric status/tokens/seconds, `usage` None, a group differing only by provider; all 4 roles with review findings; ties in first-appearance order with `""` and None backends kept apart; float seconds and tokens above 2^40 | 7/7. Compared: row order, keys, count, statuses and extras as ordered pairs, total tokens, and medians doubled as Int64 (friction 3) |
| Skeleton S1-S6 | Same fakes as the re-probe | 6/6 as expected; `Leader.next` reports and capability call sequences are identical to the re-probe. S6 still fails the whole run by design; its frames now carry names (`Agent.run ← memberBody ← memberWorker ← teamLoop×3 ← runTeam`) |

No correctness divergence was found in 39 checks. A run takes ~1.5-1.8 s.

## The re-probe's frictions

| Re-probe friction | Now | Evidence |
| --- | --- | --- |
| 1. 64-node recipe limit | **Resolved** | `teamLoop` is created as one 206-node body. `Crew`, `LoopState`, `advance`, `onSpawn`, `onWait`, `onStop` and `onIntegrate` are gone. A 383-node body gives "contains 383 nodes, more than 256. Largest subtrees within the limit …: /0 Int64WrappingAdd (255 nodes); /1 …". |
| 2. Keyed lookup of task handles | **Resolved** | `Member { name, task }` replaces the parallel lists; lookup is `ListItemAt(ListFilter(members, isNamed, target), 0)`. `IndexSearch`, `indexStep`, `pick` (the "Err means found" fold), `select` and `selectInto` are gone. Integrate reads its one wait result with `ListItemAt`. Flattening `Option<Option<R>>` still needs a None arm that cannot happen. |
| 3. No Int64 division | **Resolved** | `preview` uses `Int64FloorDivide(n, 2)`. The 4 halving helpers (~50 nodes, one recursive) are gone. |
| 4. Self-recursion placeholder | **Resolved** | `teamLoop` was created with a `directCall` naming itself. |
| 6. IDs in output | **Mostly resolved** | Results, calls and frames carry names, and the harness built no ID map. Remaining: `PureExpressionPatternSlotNotFound` names the field by StableId only (friction 5); a nested list `itemType` is `{"kind":"named","typeId":…}` without a name; Product fields and call arguments come back in ID order, not declaration order (`report` before `turn`), which is cosmetic. |

## New friction, ranked by impact

| # | Friction | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **No Map and no structural equality** | `usage_stats` replaces-or-appends in 3 places (statuses, extras, groups) and compares a 6-field `Option<String>` group key. That cost 10 functions and 147 of 324 nodes (45%): `optStrEq`, `sameGroup` (47), generic `anySame`/`mergeIf`/`upsert`, `sameStatus`/`addCounts`/`sameExtra`/`addExtra`, `mergeGroup`. The data part still carries `UsageEntry` and 4 functions. One generic `upsert<T>`, passed directly as a `ListFold` folder, served all 3 collections on the first try. This is the third keyed-lookup consumer after the [Map revisit](../decisions/task-fields-list-indexing-and-map.md). | Value equality for types without functions or tasks removes 4 functions (75 nodes). A String/Int64-keyed Map removes the rest. |
| 2 | **No sort and no String ordering** | Sorting took 7 functions and 72 nodes (`leX`, `gtX`, `insertSorted`, `rowNotAfter`, `rowAfter`, `insertRow`, `roleRank`). Ordering by role needs a fixed rank table of the 4 known roles (21 nodes), so an unknown role would sort differently from Python. The median also sorts first. | Ordinal String comparison and a stable `ListSortBy` with a pure key or comparator. |
| 3 | **Float64 is unusable end to end** | There is no Int64↔Float64 conversion, no rounding and no Float64 run value. A Float64 result gives `RunResultTypeUnsupported` "Supported results are scalars and …", which is misleading because Float64 is a scalar. An `expected` of kind `float64` gives `ProtocolError` "Unsupported pure run value kind 'float64'." Workaround: medians are reported doubled as Int64 and seconds as Int64 milliseconds. `round(median, 1)` was not reproduced; Python's `round(0.35, 1)` is 0.3 because of binary floats. | `Int64ToFloat64` and Float64 run values (the concrete consumer for the T3 non-support), with a message that names Float64. Rounding later. |
| 4 | **Simulate replies echo the whole graph** | ~1.7 KB per recipe node. The `teamLoop` create reply is 352 KB for a 16 KB request; `preview` (56 nodes) is 98 KB. Replies total 165/460/216 KB against 12/32/32 KB of requests. For a live agent this is the dominant context cost. | A compact reply (node count, created IDs, fingerprint) by default, with the graph on request. |
| 5 | **`scopeDepth` errors do not say what is in scope** | 1 retry: `Stop.member` inside `MatchOption`'s `noneBody` needed depth 1, not 0, because every arm opens a frame, including a None arm with no bindings. This is spec-conformant, but the diagnostic reads "Pattern-reference slot Field (StableId "…") does not exist in scope depth 0." | Name the field and variant, and list what each depth refers to. |
| 6 | Trivial callbacks must be named functions | 8 functions of 3-6 nodes (`isNamed`, `isMember`, `leX`, `gtX`, `rowAfter`, `extraCount`, `sumCount`, `addInt`). | Low priority; some disappear with friction 2. Do not add lambdas for this. |
| 7 | JSON depth 128 is below the node limit | A left-deep chain of 140 `int64WrappingAdd` fails with `ProtocolError` "Invalid JSON: The maximum configured depth of 128 has been exceeded" before the 256-node check. | Raise the reader's depth to match the recipe bounds, or document it. Low impact. |

Not queued, unchanged: a member's host exception failing the whole run (by design), the fixed `TaskWait` mode per node, verbose run-value encoding (~15 lines for `usage_stats`), and the harness's copy of the role/extra derivation, which the typed boundary cannot avoid.

## Remaining workarounds

- **Data:** `UsageEntry` with `sameKey`/`bumpEntry`/`addUsageEntry`/`addAttemptUsage` (Map); `isMember` (callback).
- **Leader:** `MemberSpec` (TaskStart takes one item); `isNamed` and `collectStep` (lookup by name through a fold); `describe`/`outcomeText` (report-text folders); `integrateOne`; two `TaskWait` nodes; one impossible None arm.
- **`usage_stats`:** 10 Map/equality functions, 7 sort functions and 3 folders (above); 4 types that stand in for dicts (`ExtraItem`, `StatusCount`, `ExtraCount`, `Group`); doubled medians and Int64 milliseconds; two impossible None arms in `median2`.
