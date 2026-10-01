# rrawrlet slice re-probe — 2026-09-28

**Status:** Complete. Data processing matches Python; the leader skeleton passes its scenarios. New gaps recorded below.

**Date:** 2026-09-28

Baseline: nyrwvy `70995fa` (branch `claude/rrawrlet-reprobe`, main after #225 Bool operators); rrawrlet `903f2ce`. `preview`, `usage_is_complete` and `summarize` are unchanged since the 2026-09-25 probe's `d02990a` (the AST is identical). Python 3.14.7 ran the real functions, extracted read-only from `rrawrlet/team.py`.

This re-runs the 2026-09-25 slice probe (see [post-M2 roadmap](../plans/post-m2-roadmap.md#queue-from-the-2026-09-25-probe)) after queue orders 1-5 were implemented. It uses only the maintained `Nyrwvy.AuthoringSession` JSON-lines protocol: `simulate*ByName`, `commit`, `declarations` and `runPureByName`. No exact-ID authoring request was needed. The driver is a transport client with pure JSON builders and does not look up names or IDs. Scripts and transcripts are kept outside the repository in the session scratchpad `reprobe/` (`client.py`, `data.py`, `run_data.py`, `team.py`, `team_single_body.py`, `run_team.py`, `count.py`, `*_transcript.jsonl`).

## What was authored

| Part | Types | Functions | Requests / request JSON | Recipe nodes (approx.) |
| --- | --- | --- | --- | --- |
| Data: `usage_is_complete`, `summarize`, `preview` | 3 (`UsageEntry`, `Attempt`, `Summary`) | 12 (+1 body replacement) | 33 / 17.4 KB | ~199 |
| Leader skeleton | 7, plus 1 effect and 4 capabilities (`Leader`, `Workspaces`, `Agent`, `Repo`) | 16 (+1 body replacement) | 58 / 44.4 KB | ~379 |

Only 3 data functions and 4 skeleton functions (`memberBody`, `memberWorker`, `teamLoop`, `runTeam`) carry the rrawrlet logic. The other functions and types are workarounds, counted in the friction table.

The skeleton follows rrawrlet's `spawn`/`wait`/`stop`/`integrate`/`close` as one structured loop, as sketched in the [concurrency decision](../decisions/concurrency-control.md#sketch-rrawrlets-team-as-one-loop). `runTeam` opens a `TaskScope`. Each turn, `teamLoop` calls `Leader.next(turn, report)` and matches `Action = Spawn | Wait{members, all, limitMillis} | Stop | Integrate | Finish`. `Spawn` runs `TaskStart` of `memberWorker`, which is `UseResource(Workspaces.create/remove)` around `Agent.run`, with `CancellationRequested` mapped to `Stopped`. `Wait` runs `TaskWait` any or all with an `Option<Int64>` limit. `Stop` is `TaskCancel` followed by a wait. `Integrate` makes a zero-limit wait, then calls `Repo.integrate`. A turn budget returns `Err` (N5). Bool operators are used wherever the Python uses `and`/`or`/`not`: `usage_is_complete` (4), `isMember`, and `summarize`'s `bool(attempts) and all(...)`.

## Results vs Python (26/26)

| Function | Cases | Result |
| --- | --- | --- |
| `summarize` | 6 attempt lists: empty; codex running/completed; opencode with explicit `usage_complete` plus claude with no usage; `waiting_input` with explicit False and `usage: {}`; missing backend (defaults to codex); all complete | 6/6: `member_spawn_count`, `usage_complete` and `reported_usage` (compared as maps) equal |
| `usage_is_complete` | Each of the 11 attempts above | 11/11 |
| `preview` | (5,10), (60,40), (50,26) tail=0 quirk, (50,25), (30,10) negative head/tail, 80 code points of emoji and accented text with limit 31, (7000,6000), ("",0), ("abc",-3) | 9/9, including Python's `text[-0:]` and negative floor-division behaviour |

`reported_usage` key order differs: Nyrwvy uses first-appearance order, while Python iterates a set. The Python order is not semantic.

## Skeleton scenarios (6/6 as expected, identical on two full re-authoring runs)

| Scenario | Fakes | Observed |
| --- | --- | --- |
| S1 any-wait selects the faster member | m1 300 ms, m2 100 ms; wait any, limit 1000 | `running;done:m2:c2;`. Both integrated; 2/2 workspaces removed |
| S2 timed wait expires, stop from outside | m1 `awaitCancellation`; wait [m1] any, limit 100 | `running;` → stop → `stopped:m1;`. Integrating m1 is refused, m2 is integrated; 2/2 released |
| S3 member failure, reversed start order | m1 `Err boom` 50 ms, m2 ok; `cases` keyed by (workspace, assignment) | `failed:m1:boom;done:m2:c2;`. Only m1 fails; 2/2 released |
| S4 finish while members run | m1 `awaitCancellation`, m2 5000 ms | Zero-limit integrate reports "still running". An unknown id is reported as an error. On Finish the scope cancels and awaits both; 2/2 released after the last leader turn |
| S5 turn budget | `maxTurns` 3 | `Err("turn budget reached")`; the member is cancelled and released |
| S6 member host exception | m1 `throw` | Whole run fails with `RunRuntimeFailure` (`FakeCapabilityException`) and frames. rrawrlet instead records the member as `failed`. Expected, see friction 5 |

A `runPureByName` run takes ~1.8-2.2 s, mostly for the build. Authoring 58 requests takes ~2 s.

## Friction, ranked by impact

| # | Friction | Evidence | Smallest candidate change |
| --- | --- | --- | --- |
| 1 | **64-node recipe limit forces the leader loop apart** | The one-body loop was ~204 nodes and failed with `StructuralRecipeNodeLimitExceeded`. The split needed 2 bundling Products (`Crew`, `LoopState`) and 5 helpers (`advance`, `onSpawn`, `onWait`, `onStop`, `onIntegrate`). `onWait` (42) and `teamLoop` (48) are still near the limit. The error does not report the actual count or the largest subtree. | Report the node count and the largest subtrees in the diagnostic. Separately, decide whether session create/replace may use a higher documented limit. |
| 2 | **Keyed lookup of task handles, a second Map consumer** | `Task` cannot be stored in declared types, and there is no list indexing. Selecting a member by id needed parallel `names`/`tasks` lists, an `IndexSearch` type and 4 functions (`indexStep`, `pick`, `select`, `selectInto`). `pick` uses the "Err means found" `ListEffectFold` trick. Integrate also folds over a one-element wait result just to extract it. | `ListItemAt(list, index): Option<T>` removes `pick` and the Err trick. A String-keyed builtin Map whose values may be `Task` would remove all 4 functions. This meets the Map revisit condition. |
| 3 | **No Int64 division** | `preview` needs `(remaining+1)//2` and `remaining//2`. Floor-halving took 4 helpers (`powersUpTo`, `halfStep`, `halfNonNegative`, `floorHalf`, ~50 nodes), one of them recursive. | `Int64FloorDivide`/`Int64Remainder` (floor semantics matter for Python-faithful negatives; define division by zero). |
| 4 | Self-recursion needs a placeholder, then a body replacement | `simulateCreateFunctionByName` with a self-call → `ProtocolError: Unknown function 'powersUpTo'.` The error gives no hint. `teamLoop` also needs the placeholder route, created before its mutually recursive helpers. | Resolve the function's own `displayName` within its creation body, or add a hint that names the placeholder route. |
| 5 | A member's host exception cannot become a member status | S6: `TaskWait` rethrows, so the whole team fails. rrawrlet's `execute` catches `Exception` → `failed`. Workaround: capabilities return `Result`. | None now (by design). Revisit if a real host binding throws. |
| 6 | Output is reported in exact IDs only | Comparing `summarize` needed a field ID→name map built from `declarations` (~8 lines). Reading the call log needed an operation ID→name map. Some diagnostics under `…ByName` requests print IDs: `ExpressionCapabilityMissingEffectRequirement`, and `TaskTypeInDeclaredType`, which names the field by ID. | Add display names next to IDs in `runPureByName` actual/mismatch/calls output and in these diagnostics. |
| 7 | Verbose run-value encoding | A ~20-line harness encoder turned Python dicts into tagged values (`itemKind` vs `itemType`, `hasValue`, `Option<List<Product>>`). | None; inherent to typed values. |
| 8 | `TaskWait` mode is fixed per node | The Python `mode` argument became a conditional with two `TaskWait` nodes. | None (accepted in the decision). |
| 9 | Positional `scopeDepth` references | Depths up to 3 in `usage_is_complete` and 4 nested binds in `preview`. Everything was right on the first try, but an off-by-one between same-typed binds would type-check silently. | None now; watch in live-agent probes. |

Trial and error: the data part published on the first run, apart from the expected self-reference rejection. The skeleton needed one restructure (friction 1) and then passed all scenarios on its first run.

## Earlier costs

| 2026-09-25 cost | Now |
| --- | --- |
| ~30-line name→ID helper | **Resolved for authoring and inputs.** No ID was supplied anywhere. The remaining cost is on the output side (friction 6). |
| Nested conditionals for and/or/not | **Resolved:** `boolAnd`/`boolOr`/`boolNot`. |
| No timed Any wait / stop one member | **Resolved:** `TaskScope`/`TaskStart`/`TaskWait` any/all with a limit/`TaskCancel`, deterministic on the virtual clock (S1-S5). |
| Call-order-only fakes | **Resolved:** `cases` keyed by argument; reversing start order still fails only m1 (S3). |
| String preview not writable | **String part resolved** (`StringLength`/`StringSlice`, code points match Python). The function still needs Int64 division (friction 3). |
| Map-like upsert (2 types + 2 functions) | **Unchanged, deferred:** `UsageEntry` plus 4 small functions (`sameKey`, `bumpEntry`, `addUsageEntry`, `addAttemptUsage`). A second keyed-lookup consumer now exists (friction 2). |
