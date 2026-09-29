# Post-M2 roadmap

- **Status:** Current planning context
- **Prepared:** 2026-09-15
- **Updated:** 2026-09-28
- **Current baseline:** Maintained ordinary AI graph tools and stdio MCP, with qualified Codex maintenance evidence and an on-demand expression-recipe reference driven by an independent consumer task
- **Purpose:** Record the maintained authoring baseline and define the evidence-triggered boundary for later work.

This document began as the post-M2 proposal for moving Nyrwvy from bounded structural editing toward ordinary AI authoring. M3, M4, and the first M5 slice are now implemented and verified. The roadmap therefore no longer treats those stages as future work.

Existing specifications and accepted decisions remain authoritative for exact contracts. This document records milestone outcomes, selection criteria, and conditions for opening later work. It does not create release dates, require broad benchmark programs, or pre-authorize external resources.

## Current recommendation

**Do not expand the language or infrastructure merely because M3-M5 are complete.** The next substantial implementation should be selected from a concrete blocked task, measured interaction cost, or real scale/collaboration pressure.

The 2026-09-24 rrawrlet workload review now supplies a concrete language/tool implementation queue in the [general-purpose programming roadmap](general-purpose-programming-roadmap.md). It targets the ability to author and maintain ordinary programs without editing source text, not rrawrlet-specific builtins or provider implementations. That roadmap's goals (G1–G4, T1–T3) were completed on 2026-09-25 through #214 and #215; its remaining T3 non-support (operations with a declared `failureType`) is recorded there; Float64 run values were added for probe 3 (order 13). The completed M3-M5 claims below remain unchanged; this does not automatically open M6 scale/collaboration work.

The post-M2 sequence established that an agent can create a small persistent program, define its domain/capability surface, and perform one identity-preserving cross-cutting evolution through the maintained authoring path. The next question is therefore not “which generic framework comes next?” but “what real task now fails or costs too much?”

| Milestone | Status | Established outcome |
| --- | --- | --- |
| **M3 — Independent small-program authoring** | **Completed** | Atomic function creation, targeted expression replacement, and the maintained snapshot-backed AuthoringSession compose into ordinary persistent authoring with stable IDs and restart/re-discovery. |
| **M4 — Domain and capability-backed programs** | **Completed** | Product/union, effect, and capability/operation declarations can be authored through the maintained session; one bounded provider-backed workload reaches execution readiness and explicit C# capability binding. |
| **M5 — Atomic cross-cutting evolution** | **First bounded slice completed** | One existing function parameter type can be migrated with its target body and complete direct-caller rewrites in one reviewed publication while preserving FunctionId/ParameterId continuity. |
| **M6 — Measured scale and collaboration** | **Conditional** | No implementation queue exists by default. Start only when an observed bottleneck identifies the smallest useful workstream. |

## What M3 established

The original M3 gaps are closed:

- targeted expression replacement from #148;
- atomic function creation from #149;
- maintained snapshot-backed authoring session from #153;
- exact simulate/review/commit/reject lifecycle and restart-safe persistent snapshots;
- system-owned production IDs discovered and reused through maintained queries rather than predicted by callers.

The important outcome is not the individual operations. It is that ordinary authoring no longer depends on privileged fixture construction or raw graph/snapshot editing.

The maintained session remains intentionally narrow: it is a semantic authoring boundary, not a general orchestration service or durable proposal database. Proposal handles are ephemeral across restart; published semantic identities are durable.

## What M4 established

M4 completed the staged declaration path accepted by the declaration-authoring decision:

1. Product/Union declaration authoring (#154);
2. Effect declaration authoring;
3. Capability/Operation declaration authoring;
4. one bounded provider-backed end-to-end workload (#164/#165).

The representative workload creates `ValidationOutcome`, `ConsoleOutput`, a `Console.write_line` capability operation, and two functions (`classify` and `process`) through the maintained AuthoringSession. The program remains semantically authoritative independently of host binding availability.

The operational boundary is explicit:

- semantic/execution readiness is checked before provider binding;
- an empty C# profile fails specifically with `MissingCapabilityBinding`;
- an explicit `System.Console.WriteLine` binding emits successfully;
- host code supplies the adapter/provider boundary rather than embedding business validation outside the semantic program.

A bounded live authoring run (#166/#167) completed the same maintained workflow without semantic retries. That run also exposed a concrete interaction-cost issue: repeated declaration discovery returned more function detail than ID/signature discovery required. #168 then compacted `declarations` while retaining `focusFunction` as the detailed body/query path. This is the preferred pattern for later ergonomics work: fix measured friction narrowly instead of introducing speculative query infrastructure.

## What the first M5 slice established

The first actual M3/M4 maintenance blocker selected the M5 task rather than the roadmap preselecting a generic migration framework.

#170 implemented a bounded parameter-type migration over the M4 program:

- create `ValidationRequest { email: String }`;
- preserve the existing `classify` FunctionId and input ParameterId while changing the parameter type from `String` to `ValidationRequest`;
- replace `classify` so validation reads `ValidationRequest.email`;
- preserve `process` and its signature while rewriting its body to construct `ValidationRequest` and call the migrated classifier;
- discover the complete direct caller set rather than trust an incomplete caller list;
- reject missing/incompatible caller rewrites and stale proposals;
- validate one final candidate and publish no type-invalid authoritative intermediate state.

The deterministic Core contract, maintained AuthoringSession/JSONL exposure, and one bounded live M4→M5 replay all passed. The live replay used one migration attempt, reported one affected call site and two body rewrites, preserved the relevant FunctionId/ParameterId values across restart, retained execution readiness, and completed the expected provider-binding checks with zero unexpected protocol errors.

This supports the bounded conclusion that continuity-preserving cross-cutting evolution is practical through the maintained authoring surface. It does **not** justify a universal migration planner, arbitrary schema evolution, result-type migration, union-field migration, automatic effect propagation, or a generalized transaction language.

## Ordinary graph tools and real-host MCP evidence

The maintained baseline now includes name-resolved graph-editing tools, shared OpenCode/stdio MCP exposure, and bounded fieldless Union evolution with exhaustive-match repair. The [graph-tool contract](../specs/agent-graph-tools.md) and [MCP guide](../specs/mcp-graph-tools.md) describe the current surface.

The 2026-09-21 Codex CLI probe is complete. [Run 1](../verification/codex-cli-mcp-maintenance-probe.md) found a host approval configuration blocker. [Run 2](../verification/codex-cli-mcp-maintenance-probe-run-2.md) changed only the server-level approval setting and completed known TimedOut maintenance, including review, explicit commit, close/reopen, preserved TypeId/FunctionId, a system-owned VariantId, one repaired match, and final readiness `ready`. No semantic/transport errors, mutation retries, or semantic user intervention occurred in Run 2.

This establishes **Codex CLI + local repository context + Nyrwvy MCP tools** for one known task. The model consulted repository docs and `Protocol.fs`; MCP tool descriptions alone were not proven sufficient. That is observed discovery friction, not proof that reading the implementation was necessary and not a current blocker. Revisit discoverability only if another real task supplies further evidence of the same cost or a failure.

No extra Codex trial, other-host matrix, or repeated success-rate measurement is required to close this probe. The result does not open M6 or authorize speculative semantic/tool redesign.

A subsequent independent `nyrwvy-ai-task-router` project supplied the additional discoverability evidence this roadmap required before changing the tool surface. The model completed new-program authoring without reading Nyrwvy repository docs or implementation, but first attempted unsupported `help` and then needed 10 function-recipe `ProtocolError` iterations to discover exact symbolic wire names. #198 therefore added a sessionless `nyrwvy_recipe_reference` in the shared ordinary-tool layer, with no Core/AuthoringSession semantic change and focused deterministic coverage for the observed `conditional`, `fieldAccess`, and `matchUnion` mistakes.

The bounded 2026-09-22 external-consumer follow-up then created three types and a nested `decide` policy function from an empty project. The model discovered `nyrwvy_recipe_reference` itself, read the index plus five needed recipe shapes, and submitted the function proposal successfully on the first attempt. All four proposals were reviewed and committed, close/reopen preserved the final snapshot/fingerprints, and the reopened function had no effects or obligations. There were zero `ProtocolError`, semantic, transport, or approval failures and no repository-documentation/source lookup. This closes the measured recipe-discovery repair; repeat live validation only after a relevant interaction-surface change.

## Selection rule from here

Continue applying the AI-first priorities in [Vision](../concepts/vision.md): semantic integrity and unambiguous machine interpretation first, then useful capability/composability, persistent identity, focused queries, and human supervision. Direct human-authoring convenience and personal aesthetic preferences remain secondary.

For any proposed next feature, record:

1. the concrete task it enables or repairs;
2. the observed failure, blocker, or cost today;
3. the semantic/identity/review invariant that must remain true;
4. the smallest coherent implementation that changes the outcome;
5. the evidence needed to know whether it worked.

Do not create infrastructure because it is a conventional next layer. Do not turn a successful bounded experiment into a broad superiority claim. Prefer one measured repair over a generic framework whose consumers have not appeared.

## M6 — Measured scale and collaboration

M6 remains intentionally conditional. Choose at most one workstream when a real pressure appears.

| Observed pressure | First investigation | Boundary to preserve |
| --- | --- | --- |
| Focus/discovery responses become costly or omit impact facts | Measure actual payload and task context; tighten focused projections or indexes only as needed. | Lower context cost must not hide affected consumers, stable IDs, or obligations. |
| Validation/fingerprinting/persistence dominates authoring latency | Profile those operations separately before changing architecture. | Full semantic validation remains the normative oracle; storage does not redefine semantics. |
| Real project reuse requires import | Specify dependency closure and preserved/new identity rules for one actual import task. | No silent identity coalescing by spelling or accidental external-ID collision. |
| Concurrent proposals cause meaningful discarded work | Measure actual conflicts and resimulation cost before designing merge/rebase semantics. | No silent stale-approval reuse or intent reordering. |
| Human/agent review misses cross-function consequences | Improve task-focused review projections using observed missed facts. | Avoid replacing focused review with an undifferentiated giant graph dump. |
| A missing language construct blocks a real workload | Add the smallest typed construct or operation required by that workload. | Do not expand language surface for symmetry alone. |

#109 remains deferred. Its measured CI profile says fresh F# compilation dominates, but current build time does not justify structural project changes solely for CI speed. Revisit only when its documented trigger conditions become true.

## Verification policy

Follow [Verification philosophy](../developer/verification.md) and keep evidence proportional to the new boundary.

- **fast:** normal development/CI contracts and focused regressions;
- **extended:** milestone or larger integration boundaries when relevant;
- **live/environment-dependent:** only where a real model, OS feature, provider, or external environment is the thing being tested.

Do not rerun broad suites merely because another document references them. Do not move extended/live checks into routine CI without a new reason. When a materially new AI planning interaction is introduced, one bounded exploratory run is appropriate; repeat it only if the first result leaves a decision-relevant ambiguity.

Judge committed semantic state and execution behavior rather than the model's statement that it finished. Record retries, diagnostics, intervention, tool/model configuration, and the exact boundary being tested.

## Immediate execution queue

The real-host MCP probe is complete. The [general-purpose programming roadmap](general-purpose-programming-roadmap.md) records the current bounded outcomes; its original G1-G4/T1-T3 plan is complete. The next implementation targets come from two sources: a 2026-09-25 rrawrlet slice probe, and the gaps recorded in [Semantic independence](../concepts/semantic-independence.md#current-gaps).

The probe (main `05e3d24`, rrawrlet `d02990a`; recorded in the project's shared files, not in this repository) authored rrawrlet's `summarize`/`usage_is_complete` and a spawn → parallel members → integrate skeleton through the maintained session and `runPure` only. The data processing matched Python on 5 cases, and the skeleton passed 4 fake scenarios using `UseResource`, `ParallelAll` and `ListEffectFold`. It found the blockers and costs below, in order of impact. Function values were not needed for that slice.

**The order is provisional.** String operations, task scopes with time limits and a virtual clock, argument-keyed fake responses, name-resolved capability declaration and run-value input, and dedicated Bool operators are implemented, so every item of this queue is settled. The [2026-09-28 re-probe](#queue-from-the-2026-09-28-re-probe) and [probe 3](#queue-from-the-2026-09-28-probe-3) queues are done too; the [probe 4](#queue-from-the-2026-09-29-probe-4) queue (orders 17-22) is open; Bool equality is settled by `ValueEqual`, sorting and String ordering by `ListSortBy` and `StringLessThan`, Float64 conversion and run values by `Int64ToFloat64` and the `float64` run value, and Map stays deferred with the revisit conditions in its [latest decision](../decisions/value-equality-and-map.md). Revise the order with the reason recorded when a choice is made or new evidence appears.

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 1 | **Done: task scopes and timed waits** | The rrawrlet leader waits for whichever member finishes first with a time limit, and stops one member from outside. `ParallelAll` only waits for all or cancels all on the first `Err`. Same area as the semantic-independence "no abort" and "shared capability under `ParallelAll`" gaps. | Scope/start/Any-All wait/cancel and monotonic limits with deterministic fake time; [design note](../decisions/concurrency-control.md). |
| 2 | ~~No String length or substring~~ **Done** | `preview(text, limit)` could not be written; only empty/contains/equal/concat and Int64 conversion existed. | `StringLength` and clamped `StringSlice`, both counted in code points (see the [language subset](../reference/language-subset.md)). |
| 3 | **Done: argument-keyed fake responses** | The same script fails only member b after reversing task start order, with both resources released. | `runPure` operation `cases` match complete typed arguments and consume independent response queues; [contract](../specs/agent-authoring-session.md#argument-keyed-fake-responses). |
| 4 | **Done: name-resolved declaration and run-value input** | Effect/Capability creation has no name-resolved form, and `runPure` values take only TypeId/FieldId; the probe needed a 30-line name→ID helper. | `simulateCreateCapabilityByName` and `runPureByName` resolve names within their owners with the same ambiguity rejection; [contract](../specs/agent-authoring-session.md#name-resolved-ai-authoring). |
| 5 | **Done: Bool operators; Map deferred** | Expressible (nested conditionals; `List` of entries with a fold upsert), but verbose: 2 extra types and 2 extra functions in the probe. | `BoolNot` and short-circuit `BoolAnd`/`BoolOr` whose right operand is a nested graph; no Map or Bool equality yet; [decision](../decisions/bool-operators-and-map.md). |

### Queue from the 2026-09-28 re-probe

The [re-probe](../verification/rrawrlet-slice-reprobe-20260928.md) rebuilt the same slice on main `70995fa` with name-resolved requests only. Data processing matched Python on 26/26 checks and the leader skeleton passed 6/6 scenarios. The name→ID helper, nested Bool conditionals, timed waits and call-order-only fakes are no longer costs. The order below is provisional in the same way as the queue above.

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 6 | **Done: recipe bounds** (was: 64-node recipe limit splits ordinary functions) | The one-body leader loop was ~204 nodes and was rejected with `StructuralRecipeNodeLimitExceeded`. The split needed 2 bundling types and 5 helpers, and two pieces are still near the limit. The error gives no count or location. | Report the node count and the largest subtrees in the diagnostic; decide, with the reason recorded, whether session create/replace uses a higher documented limit. Implemented: 256 nodes for ordinary authoring and a count-and-subtrees diagnostic; [decision](../decisions/expression-recipe-bounds.md). |
| 7 | **Done: Int64 floor division and modulo** | `preview` needs `//2`; floor-halving took 4 helpers (~50 nodes), one recursive. | `Int64FloorDivide` and `Int64FloorModulo` with Python floor semantics; divisor 0 gives quotient 0 and remainder `left`, and `MinValue / -1` wraps, so neither fails; [language subset](../reference/language-subset.md#int64). |
| 8 | **Done: keyed lookup of task handles** (was: keyed lookup of task handles; Map revisit condition met) | `Task` cannot be stored in declared types and there is no list indexing, so selecting a member by id took 1 type and 4 functions, including an "Err means found" fold. This is the second keyed-lookup consumer named in the [Map decision](../decisions/bool-operators-and-map.md#revisit-conditions). | `ListItemAt(list, index): Option<T>` first; then a decision on a String/Int64-keyed Map, including whether it may hold `Task` values. Implemented: `Task<T>` fields in declared types (not `TaskScope`) with a transitive capability and `runPure` boundary rule, so a `Member { name, task }` Product is found with a first-match fold, and `ListItemAt`; Map revisited and still deferred; [decision](../decisions/task-fields-list-indexing-and-map.md). |
| 9 | **Done: self calls** (was: a function cannot call itself in its creation body) | `simulateCreateFunctionByName` rejects a self-call with "Unknown function"; the workaround is a placeholder body and a replacement. | Resolve the function's own name within its creation body, or at least name the placeholder route in the error. Implemented: a `SelfCall` recipe keyed by parameter slot that materializes to an ordinary `DirectCall` to the owner, in creation and in body/targeted replacement; by name, a `directCall` naming the function being created or replaced resolves to it. Mutual recursion and a function reference to the new function keep the placeholder route; [spec](../specs/expression-authoring.md#selfcall). |
| 10 | **Done: display names in run output and diagnostics** (was: `runPureByName` output and some diagnostics show IDs only) | Reading results and the call log needed ID→name maps built from `declarations`; two diagnostics print IDs under `…ByName` requests. | Add display names next to IDs in run output and in those diagnostics. Implemented: `runPure` and `runPureByName` add `function`/`type`/`variant`/`field`/`capability`/`operation`/`parameter` names beside every ID in results, mismatches, calls and frames (additive; an echoed `actual` is still accepted as exact-ID input), and validation and structural-edit diagnostics name fields, operations, functions and effects as `'name' (id)`; [spec](../specs/agent-authoring-session.md#name-resolved-ai-authoring). |

Not queued: a member's host exception failing the whole run (by design; capabilities return `Result`), the fixed `TaskWait` mode per node (accepted in the concurrency decision), verbose run-value encoding, and positional `scopeDepth` references (watch in live-agent probes).

### Queue from the 2026-09-28 probe 3

[Probe 3](../verification/rrawrlet-slice-probe3-20260928.md) rebuilt the slice on main `5331de0` and added the pure core of rrawrlet's `usage_stats`. Orders 6-9 removed their target workarounds, and order 10 mostly did: data dropped from 12 to 8 functions, and the leader skeleton from 16 functions and 7 types to 9 functions and 5 types, with `teamLoop` as one 206-node self-recursive body. All 39 checks matched Python and S1-S6 behaved as expected. The order below is provisional in the same way as the queues above.

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 11 | **Done: structural value equality** (was: Map-like upsert and key comparison (Map revisit condition met again)) | In `usage_stats`, 10 functions and 147 of 324 nodes replace-or-append into statuses, extras and groups and compare a 6-field `Option<String>` key; the data part still carries `UsageEntry` and 4 functions. | Decide between value equality for function- and task-free types (removes 4 functions, 75 nodes) and a String/Int64-keyed Map; record the decision. Implemented: `ValueEqual(left, right): Bool` over equatable types (no Float64, functions, tasks, capabilities or type parameters, transitively), structural through the C# backend; Map stays deferred. The rewritten core reran 7/7 against Python with 23 functions and 270 nodes (was 24 and 324): `optStrEq` is gone and `sameGroup` fell from 47 to 5 nodes, while the generic upsert keeps its equality callbacks; [decision](../decisions/value-equality-and-map.md). |
| 12 | **Done: List sorting and String ordering** (was: No sort and no String ordering) | 7 functions and 72 nodes for `rows.sort(key=(role, -count))` and the median sort; a fixed role-rank table would sort an unknown role differently from Python. | Ordinal String comparison and a stable `ListSortBy` with a pure key or comparator. Implemented: `StringLessThan(left, right): Bool` in Unicode code-point order (Python's `str` order, not UTF-16 code-unit order) and `ListSortBy(list, lessThan, context)`, a stable top-down merge sort under a pure `(T, T) -> Bool` callback that is deterministic for any callback. The rewritten core reran 8/8 against Python, including a new case with roles outside the old rank table that the previous core sorted wrongly, with 18 functions and 219 nodes (was 23 and 270): the 7 sorting functions (72 nodes) became `intLess` and `rowBefore` (23 nodes); [decision](../decisions/list-sort-and-string-ordering.md). |
| 13 | **Done: Float64 conversion and run values** (was: Float64 unusable end to end (T3 consumer)) | No Int64↔Float64 conversion and no Float64 run values; medians were reported doubled as Int64. The `RunResultTypeUnsupported` message omits Float64 while calling results "scalars". | `Int64ToFloat64` and Float64 run values, with a message that names Float64; rounding later. Implemented: `Int64ToFloat64(value): Float64` (round to nearest, ties to even; never fails) and Float64 `runPure` values as shortest round-trip JSON numbers with `"NaN"`, `"Infinity"`, `"-Infinity"` and `"-0"` strings, nested only through `itemType`/`okType`/`errorType`, matched bitwise with every NaN equal in expectations and fake `cases`; the message now lists String, Bool, Int64, Float64 and Unit. The rewritten core reran 8/8 against Python with real Float64 medians compared directly (no x2 scaling) and seconds passed as Float64, at 20 functions and 258 nodes (was 18 and 219): the doubled `median2` became an exact Int64-sum `medianTokens` and a Float64 `medianSeconds`. `round(median_seconds, 1)` is still done by the harness; Float64 to Int64 and decimal rounding stay deferred; [decision](../decisions/float64-conversion-and-run-values.md). |
| 14 | **Done: compact simulate replies** (was: Simulate replies echo the whole graph) | ~1.7 KB per node: a 16 KB `teamLoop` request gets a 352 KB reply; 841 KB of replies against 76 KB of requests in total. | A compact reply by default (node count, created IDs, fingerprint), with the graph on request. Implemented: every `simulate*` request takes `detail: "compact" | "full"`, default `compact`. Compact reviews render each function as its signature with IDs and display names plus `resultNodeId` and `nodeCount`, and drop a focus's per-node `nodeTypes`; the receipt and fingerprints, readiness, diffs, effect evidence, aborts, callers, callees and open obligations stay. `detail: "full"` returns the previous review, and `nyrwvy_create_function`/`nyrwvy_replace` take `detail`. Function body nodes had been 79-93% of the simulate reply bytes, most of them debug strings that reprinted nested regions. Rerun on the same authoring, simulate replies fell from 168, 473 and 166 KB to 14, 31 and 38 KB (data, leader, usage), and the `teamLoop` reply from 364 KB to 4.5 KB; data 26/26, S1-S6 and usage 8/8 unchanged; [decision](../decisions/compact-simulate-replies.md). |
| 15 | **Done: scope-depth diagnostics** (was: `scopeDepth` diagnostics name slots by StableId only) | One retry because a bindingless None arm also opens a frame; the error names neither the field nor the frames. | Name the field and variant, and list what each depth refers to. Implemented: with the codes unchanged, pattern-reference diagnostics name a field slot as `field 'member' (id) of variant 'Stop' of 'Action'`, list the visible frames innermost first (`depth 0 = None arm of MatchOption (no bindings); depth 1 = arm 'Stop' of MatchUnion on 'Action' (binds 'member')`), and say where the slot is bound when that is another depth (`The slot is found at depth 1.`), for exact-ID and name-resolved recipes alike. Parameter diagnostics name a foreign parameter with its owning function and list the owner's parameters by slot; [spec](../specs/expression-authoring.md#lexical-references). |
| 16 | **Done: JSON depth bound** (was: JSON depth 128 is below the 256-node limit) | A 140-deep `int64WrappingAdd` chain fails as invalid JSON before the node check. | Raise the reader depth to match the recipe bounds, or document it. Implemented: the session's request depth is derived from the recipe node limit, 3 envelope levels + 3 per node × 256 + 64 for a leaf TypeRef = 835, so any recipe within the node limit parses. The 140-deep chain now gets `StructuralRecipeDepthLimitExceeded`, since recipe depth is bounded at 32 before the node count, and the diagnostic states the actual depth (141); only JSON deeper than 835 levels is a `ProtocolError`. Response encoding was also raised from 256 to twice the snapshot codec's 1000 levels: a `readFunction` of about 50 nested `Bind`s used to throw although the snapshot persists about 248. Node adapters have no depth limit; [decision](../decisions/expression-recipe-bounds.md#consequences). |

Not queued: trivial named callbacks (8 functions of 3-6 nodes; partly removed by order 12, and lambdas are not wanted for this), fields and arguments echoed in ID order rather than declaration order, and an unnamed nested `itemType` in run values.

### Queue from the 2026-09-29 probe 4

[Probe 4](../verification/rrawrlet-slice-probe4-20260929.md) authored a new, text-processing part of rrawrlet on main `c511fa2`: `review.parse_findings` and the pure core of `repo_map` (14 types, 47 functions, 1106 recipe nodes), by name only and with no recipe retries. It matched Python on 23/23 in-scope checks; 4 known divergences come from the ASCII-only port. The first, natural version overflowed the stack at about 7.7K characters, and emulating missing String primitives was the dominant cost. The order below is provisional in the same way as the queues above.

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 17 | Linear text iteration (friction 1) | Stack overflow at about 7.7K chars on the natural recursive scan; O(n²) slicing; a workaround of 3 functions, 1 type and 20 requests | `StringCodePoints(text): List<String>` (linear, never fails), so `ListFold` replaces the recursion. Document the recursion depth bound. |
| 18 | String search and affixes (friction 2) | 13 functions and 199 nodes of emulation; the regex port is 240 nodes | `StringIndexOf`/`StringLastIndexOf` (code-point positions, `Option<Int64>`), `StringStartsWith`/`StringEndsWith`, and `StringSplit`. |
| 19 | Structural diagnostics by name and location (friction 4) | 4 of 5 simulated mistakes print StableIds or F# type dumps and give no node location | Display-name `TypeRef` rendering, callback names, and a recipe path in `SimulationRejected` diagnostics. |
| 20 | **Done: Deleting self-recursive functions, and by-name delete** (was: Deleting self-recursive functions, and by-name delete (friction 5)) | `FunctionStillReferenced` from a function's own `SelfCall`; 4 extra requests; exact IDs needed | Exclude self-references, name the referrers, and accept a function name. Implemented: the `deleteFunction` check ignores a function's references to itself (SelfCall/DirectCall, its own callbacks), so a self-recursive function is deleted in one operation; a `FunctionStillReferenced` rejection now names the function and every referring function as `'name' (id)`. New `simulateOperationsByName` takes name-resolved deletions (`deleteFunction` `function`, `deleteType` `type`, `deleteEffect` `effect`, `deleteCapability` `capability`, `deleteCapabilityOperation` `capability`+`operation`) with the usual unknown/ambiguous `ProtocolError`, so probe 4's cleanup (delete `splitFrom`/`utf8From`) is 1 request with no IDs instead of 1 rejection + 4 replacements + a delete. Mutually recursive functions still need one reference repaired first. |
| 21 | Case mapping and UTF-8 byte count (frictions 3 and 6) | 3 + 2 functions (85 nodes); 2 of the 4 known divergences | `StringToLower` (simple case mapping) and `StringUtf8ByteCount`. |
| 22 | `StringJoin` and `ListSlice` (friction 7) | 4 functions, 2 types, 62 nodes | `StringJoin(list, sep)`, then `ListSlice(list, start, end)`. |

Not queued: regex (orders 18 and 21 come first), a plain run-output view for the verbose run-value encoding, `commit: true` on `simulate*`, and a larger `runPure` value budget.

Of the remaining semantic-independence gaps, pure capture-free function values, stateless program capability implementations, reporting derived body effect requirements, typed function abort, and the shared-capability contract under `ParallelAll` are implemented (see the general-purpose roadmap). Stateful program capability implementations follow without a fixed order. Runtime state identity and per-declaration content hashes stay deferred until a use case needs them. Float64 run values are implemented (order 13); the remaining T3 non-support, `failureType` operations, is picked up only when a concrete consumer appears.

Preserve these constraints:

1. **Use the maintained ordinary graph tools and MCP surface as the baseline.** Reuse the recorded Codex result; do not repeat the same live boundary without a relevant change or unresolved decision.
2. **Reuse the recipe-discovery evidence.** The bounded external-consumer follow-up removed the prior recipe trial-and-error; do not rerun it unless the interaction surface changes or a new real task shows comparable discovery friction.
3. **Keep #109 deferred.** Current evidence does not justify build-architecture work.
4. **Keep the workload boundary explicit.** Create implementation issues from the new plan's concrete gaps and completion criteria, not an assumed application architecture. Check current branches/PRs first. Dependency-tool exposure, retained pins, multiple/transitive dependencies, and reviewed upgrades are implemented; use the maintained roadmap rather than the original PR #214 review-date status.

The earlier #152 cleanup suggestion is historical planning context; this probe does not establish that issue/PR's current disposition or make it a prerequisite for using the maintained surface. Reassess any useful remainder separately against the then-current checkout.

## Evidence and authority pointers

- [Vision and priority order](../concepts/vision.md)
- [Architecture](../developer/architecture.md)
- [Current language subset](../reference/language-subset.md)
- [Targeted expression replacement](../decisions/targeted-expression-replacement.md)
- [Staged declaration authoring](../decisions/declaration-authoring.md)
- [Snapshot-backed agent authoring session](../specs/agent-authoring-session.md)
- [Ordinary AI graph tools](../specs/agent-graph-tools.md)
- [MCP configuration and live verification scope](../specs/mcp-graph-tools.md)
- [Codex probe Run 1: host approval blocker](../verification/codex-cli-mcp-maintenance-probe.md)
- [Codex probe Run 2: qualified success](../verification/codex-cli-mcp-maintenance-probe-run-2.md)
- [Verification philosophy](../developer/verification.md)
- [Open questions](../decisions/open-questions.md)
- Completed work/issues: #148, #149, #153, #154, #164, #166, #168, #170
- Completed general-purpose programming roadmap: #214, #215
- Completed measured discoverability repair: #198; [bounded live follow-up](../verification/expression-recipe-reference-live-probe.md)
- Deferred build-cost investigation: #109

Revise this milestone record when later observed work changes its conclusions or creates a concrete M6 trigger; maintain the current language/tool implementation queue in the linked general-purpose programming roadmap. Historical implementation details remain in the linked issues, PRs, specifications, verification records, and experiment artifacts rather than being duplicated here.
