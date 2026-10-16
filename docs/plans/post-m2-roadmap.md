# Post-M2 roadmap

- **Status:** Current planning context
- **Prepared:** 2026-09-15
- **Updated:** 2026-10-07
- **Current baseline:** Maintained ordinary AI graph tools and stdio MCP, with qualified Codex maintenance evidence and an on-demand expression-recipe reference driven by an independent consumer task
- **Purpose:** Record the maintained authoring baseline and define the evidence-triggered boundary for later work.

This document began as the post-M2 proposal for moving Nyrwvy from bounded structural editing toward ordinary AI authoring. M3, M4, and the first M5 slice are now implemented and verified. The roadmap therefore no longer treats those stages as future work.

Existing specifications and accepted decisions remain authoritative for exact contracts. This document records milestone outcomes, selection criteria, and conditions for opening later work. It does not create release dates, require broad benchmark programs, or pre-authorize external resources.

## Current recommendation

**Do not expand the language or infrastructure merely because M3-M5 are complete.** The next substantial implementation should be selected from a concrete blocked task, measured interaction cost, or real scale/collaboration pressure.

The 2026-09-24 rrawrlet workload review supplied a concrete language/tool plan, the [general-purpose programming roadmap](general-purpose-programming-roadmap.md); the implementation queue that followed it lives in this document, in the [language and tool implementation record](#language-and-tool-implementation-record). Both target the ability to author and maintain ordinary programs without editing source text, not rrawrlet-specific builtins or provider implementations. That roadmap's goals (G1–G4, T1–T3) were completed on 2026-09-25 through #214 and #215; its remaining T3 non-support (operations with a declared `failureType`) is recorded there; Float64 run values were added for probe 3 (order 13). The completed M3-M5 claims below remain unchanged; this does not automatically open M6 scale/collaboration work.

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

**Exception for standard operations with no choice to make** (owner decision, 2026-10-01). An operation that ordinary programs commonly need may be added without an observed cost when its meaning is fixed completely by an external standard (IEEE 754) or an obvious definition, so there is nothing for a consumer to decide, and its implementation is small. Waiting for a probe would only mean hand-written versions in the meantime. Item 2 above is then the expected hand-written cost rather than an observed one. Anything with a choice to make (a size limit, a difference from a familiar library, the edge values such as `Int64.MinValue`) still waits for an observed consumer or an owner choice. Nyrwvy need not follow Python or any other library: a familiar library's behaviour is one input, and consistency with the rest of the specification decides (owner stance, 2026-10-01). See the [decision](../decisions/standard-operations.md).

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

## Language and tool implementation record

<a id="immediate-execution-queue"></a>

The language/tool implementation queue lives in this document. An *order* is one numbered, bounded language or tool change with its evidence and decision; a *probe* is one numbered live porting or authoring session recorded under [verification](../verification/README.md#live-agent-probes). The orders came from rrawrlet slice probes (the first on 2026-09-25), one music-logic probe, the gaps recorded in [Semantic independence](../concepts/semantic-independence.md#current-gaps), and owner choices under the [selection rule](#selection-rule-from-here). Orders 1-58 are all done, and no order is pending. The next order is chosen from the [open candidates](#open-candidates) when a task shows the cost again or the owner chooses one; record the reason with it. The [general-purpose programming roadmap](general-purpose-programming-roadmap.md) records the bounded G1-G4/T1-T3 outcomes, completed on 2026-09-25. The per-probe sections below keep each order's evidence and rationale; the details of each probe are in its linked verification record.

### Orders 1-39

| Order | What | Decision |
| --- | --- | --- |
| 1 | Task scopes and timed waits | [concurrency-control](../decisions/concurrency-control.md) |
| 2 | `StringLength` and `StringSlice` | — ([language subset](../reference/language-subset.md)) |
| 3 | Argument-keyed fake responses | — ([session spec](../specs/agent-authoring-session.md#argument-keyed-fake-responses)) |
| 4 | Name-resolved capability declaration and run-value input | — ([session spec](../specs/agent-authoring-session.md#name-resolved-ai-authoring)) |
| 5 | Bool operators; Map deferred | [bool-operators-and-map](../decisions/bool-operators-and-map.md) |
| 6 | Recipe bounds (256 nodes) and a count-and-subtrees diagnostic | [expression-recipe-bounds](../decisions/expression-recipe-bounds.md) |
| 7 | Int64 floor division and modulo | — ([language subset](../reference/language-subset.md#int64)) |
| 8 | `Task<T>` fields and `ListItemAt`; Map still deferred | [task-fields-list-indexing-and-map](../decisions/task-fields-list-indexing-and-map.md) |
| 9 | Self calls in a creation body | — ([expression authoring](../specs/expression-authoring.md#selfcall)) |
| 10 | Display names in run output and diagnostics | — ([session spec](../specs/agent-authoring-session.md#name-resolved-ai-authoring)) |
| 11 | Structural value equality `ValueEqual`; Map still deferred | [value-equality-and-map](../decisions/value-equality-and-map.md) |
| 12 | `ListSortBy` and `StringLessThan` | [list-sort-and-string-ordering](../decisions/list-sort-and-string-ordering.md) |
| 13 | `Int64ToFloat64` and Float64 run values | [float64-conversion-and-run-values](../decisions/float64-conversion-and-run-values.md) |
| 14 | Compact simulate replies | [compact-simulate-replies](../decisions/compact-simulate-replies.md) |
| 15 | Scope-depth diagnostics | — ([expression authoring](../specs/expression-authoring.md#lexical-references)) |
| 16 | JSON request depth derived from the recipe bounds | [expression-recipe-bounds](../decisions/expression-recipe-bounds.md#consequences) |
| 17 | `StringCodePoints` (linear text iteration) | [string-code-points](../decisions/string-code-points.md) |
| 18 | String search and affixes | [string-search-and-affixes](../decisions/string-search-and-affixes.md) |
| 19 | Structural diagnostics by name and location | — ([expression authoring](../specs/expression-authoring.md#diagnostic-text)) |
| 20 | Deleting self-recursive functions; by-name delete | — |
| 21 | `StringToLower` and `StringUtf8ByteCount` | [string-lowercase-and-utf8-length](../decisions/string-lowercase-and-utf8-length.md) |
| 22 | `StringJoin` and `ListSlice` | [string-join-and-list-slice](../decisions/string-join-and-list-slice.md) |
| 23 | `StringSpan` and `StringSpanBack` | [string-spans](../decisions/string-spans.md) |
| 24 | `StringSplitLines` over a break set | [string-split-lines](../decisions/string-split-lines.md) |
| 25 | Document the cost of positional String operations | — ([probe 5 follow-up](../verification/rrawrlet-slice-probe5-20260930.md#follow-up-orders-23-and-24)) |
| 26 | Recursive type declarations by name | [recursive-type-declarations](../decisions/recursive-type-declarations.md) |
| 27 | A callback naming the function being created explains the route | — ([session spec](../specs/agent-authoring-session.md#self-calls)) |
| 28 | Code point conversion | [string-code-point-conversion](../decisions/string-code-point-conversion.md) |
| 29 | Float64 decimal text | [float64-decimal-text](../decisions/float64-decimal-text.md) |
| 30 | Fixed-width bit operations (`Bits32`) | [bits32-domain](../decisions/bits32-domain.md) |
| 31 | Amortised `StringConcat` | — ([verification](../verification/string-amortised-concat-20261001.md)) |
| 32 | Product update | [product-update](../decisions/product-update.md) |
| 33 | UTF-8 decode with `replace` | [utf8-decode](../decisions/utf8-decode.md) |
| 34 | Float64 to Int64 rounding and floor | [float64-to-int64](../decisions/float64-to-int64.md) |
| 35 | `ListReverse` | [standard-operations](../decisions/standard-operations.md) |
| 36 | Float64 sign and integral operations, and square root | [standard-operations](../decisions/standard-operations.md) |
| 37 | `ListRange` and `StringRepeat` | [standard-operation-choices](../decisions/standard-operation-choices.md) |
| 38 | Hexadecimal text | [standard-operation-choices](../decisions/standard-operation-choices.md) |
| 39 | Int64 minimum, maximum and wrapping absolute value | [standard-operation-choices](../decisions/standard-operation-choices.md) |
| 40 | Checked Int64 add, subtract and multiply | [checked-int64-arithmetic](../decisions/checked-int64-arithmetic.md) |
| 41 | `MatchUnion` arms shared by several variants, and `otherwise` | [match-union-shared-arms](../decisions/match-union-shared-arms.md) |
| 42 | `StringToUpper` | [string-uppercase](../decisions/string-uppercase.md) |
| 43 | Rounding half away from zero, and `Float64Round` | [rounding-half-away-from-zero](../decisions/rounding-half-away-from-zero.md) |
| 44 | `Map<K, V>`, first eight operations | [map](../decisions/map.md) |
| 45 | `MapFold` and `MapFromList` | [map](../decisions/map.md) |
| 46 | `Set<T>` | [set](../decisions/set.md) |
| 47 | Set algebra and `MapFilter` | [set](../decisions/set.md), [map](../decisions/map.md) |
| 48 | `MapGetOrDefault`, `MapMapValues` and `ListMinBy` | [map](../decisions/map.md), [list-sort-and-string-ordering](../decisions/list-sort-and-string-ordering.md) |
| 49 | Tail self-calls compiled as loops | [tail-self-calls](../decisions/tail-self-calls.md) |

One change has no order number: after probe 11, a pure `ListEffectFold` whose folder returns `Result` stops at the first `Err` ([result-fold-short-circuit](../decisions/result-fold-short-circuit.md)).

### Open candidates

Counts come from the "Candidate hit counts" tables of the [probe 18](../verification/rrawrlet-slice-probe18-20261001.md#candidate-hit-counts-after-probe-18), [probe 19](../verification/rrawrlet-slice-probe19-20261001.md#candidate-hit-counts-after-probe-19), [probe 20](../verification/music-logic-probe20-20261001.md#candidate-hit-counts-after-probe-20), [probe 21](../verification/map-set-probe21-20261004.md#candidate-hit-counts-after-probe-21), [probe 22](../verification/map-set-probe22-20261005.md#candidate-hit-counts-after-probe-22), [probe 23](../verification/map-set-probe23-20261005.md#candidate-hit-counts-after-probe-23), [probe 24](../verification/map-set-probe24-20261005.md#candidate-hit-counts-after-probe-24) and [probe 25](../verification/rrawrlet-slice-probe25-20261005.md#candidate-hit-counts-after-probe-25) records, or from the sections and records named in the row. The breadth-first queue is done as order 55; none of the others is queued.

| Candidate | Observed in | Status and what it waits on |
| --- | --- | --- |
| Tail self-call compiled as a loop, or a loop construct | Probe 15 (1). Probe 13 recorded the missing `while` for bounded loops separately. [Probe 21](../verification/map-set-probe21-20261004.md) (2): reachability and a topological order ran as counted folds that could not stop early | **Done for self-calls** (order 49): a self `DirectCall` in tail position runs as a jump, as a language guarantee ([decision](../decisions/tail-self-calls.md)). No loop construct is added; mutual recursion stays out until a probe loops through two functions. `IndexFold` below remains the processor-side candidate of the [real-time note](realtime-processors.md). [Probe 22](../verification/map-set-probe22-20261005.md) wrote probe 21's loops and a depth-first search as tail self-calls: they stop when done, ran 300,000 turns, and needed no loop construct or mutual recursion |
| `IndexFold(count, seed, step)` | Not counted: proposed in the [real-time note](realtime-processors.md), which says probe 13's `while` and the `ListRange` observations may already be enough evidence | Proposed only, as the processor-side counted loop bounded by the block size. Ordinary code loops with a tail self-call since order 49, so ordinary-code use needs its own evidence or an owner choice. `ListFold` over `ListRange` (order 37) is a counted loop today, but builds the list first |
| Unbounded integers | Probes 18 and 19 (2, small). Earlier probes recorded integers beyond Int64 as known divergences only | Not queued; a bounded Int64 is a deliberate subset choice ([probe 19](../verification/rrawrlet-slice-probe19-20261001.md#costs-ranked-by-impact)) |
| Transcendental Float64 functions | Probe 20 (1) | A further observation; whether results are bit-exact across backends is the open question (#284) |
| UTF-8 decode with `strict` or `surrogateescape` | Probe 16 (1, per the [UTF-8 decode decision](../decisions/utf8-decode.md)). Probe 15 also wrote strict decoding with `surrogatepass` by hand, and probe 19 checked strict validity with the order 33 builtin. [Probe 25](../verification/rrawrlet-slice-probe25-20261005.md#costs-ranked-by-impact) (2): `json.loads` of each line read from the pipe needs `json`'s decoding (encoding detection, UTF-8, UTF-16 and UTF-32 with `surrogatepass`) and `UnicodeDecodeError`'s text, which reaches callers; 877 nodes by hand | The [decision's revisit conditions](../decisions/utf8-decode.md#revisit-conditions). Probe 25 is a second consumer of the strict-decoding condition, in a mode the decision's options do not name; for the owner's decision, not queued |
| Last item or non-copying stack operations | [Probe 22](../verification/map-set-probe22-20261005.md) (1): a depth-first search with an explicit stack was 117 nodes and quadratic, since peeking, popping and replacing the top each copy or index from `ListLength − 1` | **Done as order 50** (`ListLast`, `ListDropLast`); a push after a pop still copies, and a persistent-vector list waits for a program that exceeds a run limit because of it ([decision](../decisions/list-last-and-drop-last.md)). [Probe 23](../verification/map-set-probe23-20261005.md) (2): on a constructed comb, where every push follows a pop, the cycle path exceeds the 5 s limit at 40,000 nodes and Tarjan's components at 20,000; the owner counts a constructed input of realistic shape, so the revisit condition is met: **done as order 52**, the persistent-vector list; the comb's cycle path at 40,000 takes 1.09 s, and the components, with `ListLastIndexOf` and a smaller-side `SetDifference` (**order 53**), take 1.73 s at 40,000 ([decision](../decisions/list-last-index-and-set-difference.md)) |
| Position of an item in a list (`ListIndexOf` or `ListFindIndex`) | Probe 22 (1): a 16-node fold took the stack from a node to the top | **Done as order 51** (`ListIndexOf`, `ListFindIndex`; [decision](../decisions/list-index.md)) |
| Self-call naming only the changed arguments | [Probe 23](../verification/map-set-probe23-20261005.md) (1): `tarjanLoop` passes 47 of its 70 self-call arguments through unchanged, about 23% of its 208 nodes | A recipe shorthand expanding to the full `DirectCall`, as `otherwise` expands to a variant list; a state Product with `ProductUpdate`, tried after order 53, was 236 nodes instead of 208, since every read of the state becomes a `FieldAccess` ([follow-up](../verification/map-set-probe23-20261005.md#follow-up-loop-state-in-a-product)). First observation. **Done as order 54** (`keepUnlisted`; [decision](../decisions/self-call-keep-unlisted.md)) |
| Breadth-first queue (non-copying dequeue) | [Probe 24](../verification/map-set-probe24-20261005.md) (1): a shortest path and probe 21's `reachable`, both dequeuing with `ListSlice(1, …)`, exceed the 5 s limit on a constructed fan of 40,000 (one name with many dependencies) and are quadratic from 10,000 | **Done as order 55**: a slice from any start shares the trie ([decision](../decisions/list-slice-sharing.md)). The [order 52 decision's revisit condition](../decisions/persistent-vector-list.md#revisit-conditions) for a breadth-first queue is met by a constructed input. Options: a non-copying front removal on the persistent vector or a queue type; or no language change, with the two-stack idiom (`ListLast`, `ListDropLast`, `ListReverse`) documented, which the probe measured as linear (300,000 in 3.9 s, 9 more nodes) |
| Futures or channels between tasks | [Probe 25](../verification/rrawrlet-slice-probe25-20261005.md#costs-ranked-by-impact) (1): `codex.py`'s reader task resolves futures that requests await; a Nyrwvy task returns once and sees only its start arguments, so the reader became one event loop over a 25-field state (40 functions, 1,283 nodes for the reader, rounds and requests) | **Partly done as order 56** (`OneShot`, scoped). [Probe 26](../verification/task-communication-probe26-20261007.md) measured option A of the [design note](task-communication-and-time.md#1-communication-between-tasks): a reader task with `OneShot` chains is 491 nodes against the event loop's 508 on the same reduced client, so no channel is queued. It waits on several concurrent producers into one consumer, or on racing a cell against a task being a large share of a program |
| A time value for deadlines over several waits | [Probe 25](../verification/rrawrlet-slice-probe25-20261005.md#costs-ranked-by-impact) (1): every timeout is a task that calls `Host.sleep`, and a turn's deadline is passed into each request inside the turn, since `TaskWait`'s limit covers one wait | Not queued: the owner chose no change on 2026-10-07 ([design note](task-communication-and-time.md#2-a-monotonic-time-value), option D) until a second program needs a deadline across waits; if added, it is a builtin (`MonotonicNow`), not a host operation |
| Zero-time yield | [Probe 25](../verification/rrawrlet-slice-probe25-20261005.md#results-vs-python) (1): one case where asyncio runs a ready task between two steps at the same instant, which the port cannot write (the order of two host calls differs; the output is the same) | Not added: the owner confirmed on 2026-10-07 ([design note](task-communication-and-time.md#3-a-zero-time-yield)) |

### Queue from the 2026-09-25 probe

The probe (main `05e3d24`, rrawrlet `d02990a`; recorded in private files, not in this repository) authored rrawrlet's `summarize`/`usage_is_complete` and a spawn → parallel members → integrate skeleton through the maintained session and `runPure` only. The data processing matched Python on 5 cases, and the skeleton passed 4 fake scenarios using `UseResource`, `ParallelAll` and `ListEffectFold`. It found the blockers and costs below, in order of impact. Function values were not needed for that slice.

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 1 | **Done: task scopes and timed waits** | The rrawrlet leader waits for whichever member finishes first with a time limit, and stops one member from outside. `ParallelAll` only waits for all or cancels all on the first `Err`. Same area as the semantic-independence "no abort" and "shared capability under `ParallelAll`" gaps. | Scope/start/Any-All wait/cancel and monotonic limits with deterministic fake time; [design note](../decisions/concurrency-control.md). |
| 2 | ~~No String length or substring~~ **Done** | `preview(text, limit)` could not be written; only empty/contains/equal/concat and Int64 conversion existed. | `StringLength` and clamped `StringSlice`, both counted in code points (see the [language subset](../reference/language-subset.md)). |
| 3 | **Done: argument-keyed fake responses** | The same script fails only member b after reversing task start order, with both resources released. | `runPure` operation `cases` match complete typed arguments and consume independent response queues; [contract](../specs/agent-authoring-session.md#argument-keyed-fake-responses). |
| 4 | **Done: name-resolved declaration and run-value input** | Effect/Capability creation has no name-resolved form, and `runPure` values take only TypeId/FieldId; the probe needed a 30-line name→ID helper. | `simulateCreateCapabilityByName` and `runPureByName` resolve names within their owners with the same ambiguity rejection; [contract](../specs/agent-authoring-session.md#name-resolved-ai-authoring). |
| 5 | **Done: Bool operators; Map deferred** | Expressible (nested conditionals; `List` of entries with a fold upsert), but verbose: 2 extra types and 2 extra functions in the probe. | `BoolNot` and short-circuit `BoolAnd`/`BoolOr` whose right operand is a nested graph; no Map or Bool equality yet; [decision](../decisions/bool-operators-and-map.md). |

### Queue from the 2026-09-28 re-probe

The [re-probe](../verification/rrawrlet-slice-reprobe-20260928.md) rebuilt the 2026-09-25 slice by name only on main `70995fa`: 26/26 data checks and 6/6 skeleton scenarios as expected, with the 2026-09-25 costs resolved. It queued orders 6-10.

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 6 | **Done: recipe bounds** (was: 64-node recipe limit splits ordinary functions) | The one-body leader loop was ~204 nodes and was rejected with `StructuralRecipeNodeLimitExceeded`. The split needed 2 bundling types and 5 helpers, and two pieces are still near the limit. The error gives no count or location. | Report the node count and the largest subtrees in the diagnostic; decide, with the reason recorded, whether session create/replace uses a higher documented limit. Implemented: 256 nodes for ordinary authoring and a count-and-subtrees diagnostic; [decision](../decisions/expression-recipe-bounds.md). |
| 7 | **Done: Int64 floor division and modulo** | `preview` needs `//2`; floor-halving took 4 helpers (~50 nodes), one recursive. | `Int64FloorDivide` and `Int64FloorModulo` with Python floor semantics; divisor 0 gives quotient 0 and remainder `left`, and `MinValue / -1` wraps, so neither fails; [language subset](../reference/language-subset.md#int64). |
| 8 | **Done: keyed lookup of task handles** (was: keyed lookup of task handles; Map revisit condition met) | `Task` cannot be stored in declared types and there is no list indexing, so selecting a member by id took 1 type and 4 functions, including an "Err means found" fold. This is the second keyed-lookup consumer named in the [Map decision](../decisions/bool-operators-and-map.md#revisit-conditions). | `ListItemAt(list, index): Option<T>` first; then a decision on a String/Int64-keyed Map, including whether it may hold `Task` values. Implemented: `Task<T>` fields in declared types (not `TaskScope`) with a transitive capability and `runPure` boundary rule, so a `Member { name, task }` Product is found with a first-match fold, and `ListItemAt`; Map revisited and still deferred; [decision](../decisions/task-fields-list-indexing-and-map.md). |
| 9 | **Done: self calls** (was: a function cannot call itself in its creation body) | `simulateCreateFunctionByName` rejects a self-call with "Unknown function"; the workaround is a placeholder body and a replacement. | Resolve the function's own name within its creation body, or at least name the placeholder route in the error. Implemented: a `SelfCall` recipe keyed by parameter slot that materializes to an ordinary `DirectCall` to the owner, in creation and in body/targeted replacement; by name, a `directCall` naming the function being created or replaced resolves to it. Mutual recursion and a function reference to the new function keep the placeholder route; [spec](../specs/expression-authoring.md#selfcall). |
| 10 | **Done: display names in run output and diagnostics** (was: `runPureByName` output and some diagnostics show IDs only) | Reading results and the call log needed ID→name maps built from `declarations`; two diagnostics print IDs under `…ByName` requests. | Add display names next to IDs in run output and in those diagnostics. Implemented: `runPure` and `runPureByName` add `function`/`type`/`variant`/`field`/`capability`/`operation`/`parameter` names beside every ID in results, mismatches, calls and frames (additive; an echoed `actual` is still accepted as exact-ID input), and validation and structural-edit diagnostics name fields, operations, functions and effects as `'name' (id)`; [spec](../specs/agent-authoring-session.md#name-resolved-ai-authoring). |

Not queued: a member's host exception failing the whole run (by design; capabilities return `Result`), the fixed `TaskWait` mode per node (accepted in the concurrency decision), verbose run-value encoding, and positional `scopeDepth` references (watch in live-agent probes).

### Queue from the 2026-09-28 probe 3

[Probe 3](../verification/rrawrlet-slice-probe3-20260928.md) rebuilt the slice on main `5331de0` and added the pure core of rrawrlet's `usage_stats`: all 39 checks matched Python, and orders 6-10 removed their target workarounds. It queued orders 11-16.

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

[Probe 4](../verification/rrawrlet-slice-probe4-20260929.md) authored `review.parse_findings` and the pure core of `repo_map` by name on main `c511fa2`: 23/23 in-scope checks matched Python, with 4 known divergences from the ASCII-only port. The natural version overflowed the stack at about 7.7K characters, and emulating missing String primitives was the dominant cost. It queued orders 17-22.

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 17 | **Done: linear text iteration** (was: text iteration only through stack-bound self-recursion, friction 1) | Stack overflow at about 7.7K chars on the natural recursive scan; O(n²) slicing; a workaround of 3 functions, 1 type and 20 requests | `StringCodePoints(text): List<String>` (linear, never fails), so `ListFold` replaces the recursion. Document the recursion depth bound. Implemented: `StringCodePoints(text): List<String>` returns each code point as a one-code-point String in one linear pass and never fails; a lone surrogate is its own item, as `StringLength` counts it. It is wired through validation, both codecs, typed and execution IR, the C# backend, the viewer, the wire recipe and the recipe reference. A `ListFold` over it counts 15,000 repetitions of `ab😀` (60K UTF-16 units) in one `runPure`. The recursion depth bound (several thousand nested calls) is documented in the language subset and expression authoring, and `runPure` reports a stack overflow as recursion that was too deep, pointing to `ListFold`. See the [decision](../decisions/string-code-points.md). |
| 18 | **Done: string search and affixes** (was: only `StringContains` for searching text, friction 2) | 13 functions and 199 nodes of emulation; the regex port is 240 nodes | `StringIndexOf`/`StringLastIndexOf` (code-point positions, `Option<Int64>`), `StringStartsWith`/`StringEndsWith`, and `StringSplit`. Implemented: the five builtins compare ordinally, count positions in code points, never fail and never split a surrogate pair; they match Python's `find`/`rfind`/`startswith`/`endswith`/`split(sep)` except that `from` clamps like `StringSlice` and an empty separator gives `[text]`. They are wired through validation, both codecs, typed and execution IR, the C# backend (`NyText` helpers over ordinal .NET search), the viewer, the wire recipe and the recipe reference, with a `runPure` parity table including emoji and lone surrogates. See the [decision](../decisions/string-search-and-affixes.md). |
| 19 | **Done: structural diagnostics by name and location** (was: Structural diagnostics by name and location, friction 4) | 4 of 5 simulated mistakes print StableIds or F# type dumps and give no node location | Display-name `TypeRef` rendering, callback names, and a recipe path in `SimulationRejected` diagnostics. Implemented: with the codes unchanged, preflight diagnostics render types in wire `TypeRef` notation by display name (`Option<String>`, `List<Tail>`; IDs only for an unknown type and for the two sides of a `FieldAccess` owner mismatch), name callbacks, callees and bodies as `'name' (id)` with their declared signature and the concrete shape the role needs (`here (Int64, String) -> Int64; the folder has type (line: String, acc: Int64) -> Int64`), name call and capability arguments, and end with the request path and kind of the failing node (`At body.whenFalse (stringLength).`, `At replacement.arms[0].body.noneBody (patternReference).`) for exact-ID and `…ByName` creation and replacement alike. Replaying probe 4's mistakes: all 5 rejections now name their types and callbacks and locate the node; [spec](../specs/expression-authoring.md#diagnostic-text), [probe 4 follow-up](../verification/rrawrlet-slice-probe4-20260929.md#follow-up-order-19-structural-diagnostics). |
| 20 | **Done: Deleting self-recursive functions, and by-name delete** (was: Deleting self-recursive functions, and by-name delete (friction 5)) | `FunctionStillReferenced` from a function's own `SelfCall`; 4 extra requests; exact IDs needed | Exclude self-references, name the referrers, and accept a function name. Implemented: the `deleteFunction` check ignores a function's references to itself (SelfCall/DirectCall, its own callbacks), so a self-recursive function is deleted in one operation; a `FunctionStillReferenced` rejection now names the function and every referring function as `'name' (id)`. New `simulateOperationsByName` takes name-resolved deletions (`deleteFunction` `function`, `deleteType` `type`, `deleteEffect` `effect`, `deleteCapability` `capability`, `deleteCapabilityOperation` `capability`+`operation`) with the usual unknown/ambiguous `ProtocolError`, so probe 4's cleanup (delete `splitFrom`/`utf8From`) is 1 request with no IDs instead of 1 rejection + 4 replacements + a delete. Mutually recursive functions still need one reference repaired first. |
| 21 | **Done: case mapping and UTF-8 byte count** (was: no case mapping and no UTF-8 byte count, frictions 3 and 6) | 3 + 2 functions (85 nodes); 2 of the 4 known divergences | `StringToLower` (simple case mapping) and `StringUtf8ByteCount`. Implemented: `StringToLower` maps each code point with the culture-invariant Unicode simple lowercase mapping and keeps unpaired surrogates, matching Python `lower()` except `İ` (simple `i`, not `i̇`) and final sigma; `StringUtf8ByteCount` is `len(text.encode("utf-8"))` with an unpaired surrogate as 3 bytes. Both never fail and are wired through validation, both codecs, typed and execution IR, the C# backend (`NyText.ToLower`/`Utf8ByteCount`), the viewer, the wire recipe and the recipe reference, with a `runPure` Python-parity table. MX1 (Kelvin sign) is now reachable; FX3 stays a divergence by design. See the [decision](../decisions/string-lowercase-and-utf8-length.md). |
| 22 | **Done: `StringJoin` and `ListSlice`** (was: `StringJoin` and `ListSlice`, friction 7) | 4 functions, 2 types, 62 nodes | `StringJoin(list, sep)`, then `ListSlice(list, start, end)`. Implemented: `StringJoin(list: List<String>, separator)` is Python `separator.join(list)` (`[]` gives `""`, inverse of `StringSplit`); `ListSlice(list: List<T>, start, end)` is generic like `ListItemAt` and clamps both bounds to `[0, length]` exactly like `StringSlice` (negative bounds are 0, not counted from the end; `end <= start` gives `[]`). Both never fail and are wired through validation, both codecs, typed and execution IR, the C# backend (`NyText.Join`, `NyList<T>.Slice`), the viewer, the wire recipe (`list`/`separator`, `list`/`start`/`end`, so diagnostic paths name them), and the recipe reference, with a `runPure` Python-parity table. Re-authoring probe 4's map part removed `joinStep`/`joinFirst`/`omitStep`, `JoinAcc` and `OmitAcc`: 27 to 25 functions, 11 to 9 types, 651 to 603 nodes. See the [decision](../decisions/string-join-and-list-slice.md). |

Not queued: regex (orders 18 and 21 come first), a plain run-output view for the verbose run-value encoding, `commit: true` on `simulate*`, and a larger `runPure` value budget.

### Candidates from the 2026-09-30 probe 5

[Probe 5](../verification/rrawrlet-slice-probe5-20260930.md) re-authored probe 4's slice with orders 17-22 on main `4f69c9b`: 24/24 in-scope checks with no depth workaround, and 1106 nodes became 897. Nothing blocked the slice; orders 23-25 took up its remaining costs #1, #2 and #4.

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 23 | **Done: character-class spans** (was: spans and strip as hand-written self-recursion, cost #1) | 7 functions and 70 nodes of per-character recursion; both adversarial slowdowns | `StringSpan(value, set, from)` and `StringSpanBack(value, set, end)`, Int64 code-point positions (Python `lstrip`/`rstrip` as positions, bounds clamped like `StringSlice`), so `strip(set)` is one `StringSlice` over both. Both never fail, run in linear time and are wired through every layer (`NyText.Span`/`SpanBack`), with a `runPure` Python-parity table. See the [decision](../decisions/string-spans.md). |
| 24 | **Done: line splitting over a break set** (was: `str.splitlines`/`bytes.splitlines` as a fold with an accumulator type, cost #2) | 1 type, 2 functions and 55 nodes; `StringSplit` takes one separator | `StringSplitLines(value, breaks)`, `List<String>` split at any code point of `breaks`, `\r\n` one break when both are members, Python `splitlines` edges (`""` gives `[]`, no trailing empty line), so both `str.splitlines` and `bytes.splitlines` are one node with a literal set. Never fails, runs in linear time and is wired through every layer (`NyText.SplitLines`), with a `runPure` Python-parity table. See the [decision](../decisions/string-split-lines.md). |
| 25 | **Done: document the position cost** (was: position-walking scans are O(n²) and stack-bound, cost #4) | Only on adversarial input: thousands of colons, 16,000 spaces | Document in the language subset and expression authoring that positional String operations are linear in the position. Re-measured with orders 23-24: the slice is 11 types, 32 functions and 797 nodes (from 12, 38 and 897), and 16,000 spaces now take 1.5 s instead of 3.3 s; see [probe 5's follow-up](../verification/rrawrlet-slice-probe5-20260930.md#follow-up-orders-23-and-24). |

### Findings from the 2026-09-30 probe 6

[Probe 6](../verification/rrawrlet-slice-probe6-20260930.md) authored the stateful `LeaderGuard.read_member` by name on main `84d1dab` and matched the real Python on 16/16 request sequences. It gives no evidence for stateful program capability implementations, non-local abort or runtime state identity; such an abort would need to carry updated state. Nothing is queued.

### Findings from the 2026-09-29 probe 7

[Probe 7](../verification/rrawrlet-slice-probe7-20260929.md) authored rrawrlet's OpenCode event observer by name on main `ea35e73` and matched the real Python on 35/35 event sequences. Its one blocker, declaring a recursive type by name, became order 26. Its linear keyed lookups are the first measured consumer evidence for Map, which stays deferred because nothing is blocked.

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 26 | **Done: recursive type declarations by name** (was: recursive types cannot be declared by name, cost #1) | `Json`/`Member` needed exact-ID variant evolution: 4 extra requests and IDs copied from reviews, reachable from the ordinary tools only through `request` | `simulateCreateTypesByName` creates a group of types that may name themselves and each other, and `simulateCreateTypeByName` accepts its own name (a group of one). Members resolve to request-local references that simulation replaces with allocated TypeIds; an uninhabited member is `TypeDeclarationUninhabited`. Re-authoring the probe: `Json` and `Member` in one request, 35/35. See the [decision](../decisions/recursive-type-declarations.md). |

### Findings from the 2026-09-29 probe 8

[Probe 8](../verification/rrawrlet-slice-probe8-20260929.md) authored rrawrlet's secret redaction over decoded JSON and the JSON helpers of `diagnostics.py` by name on main `fcaa805` and matched the real Python on 113/113 cases. Recursion through callbacks cost one placeholder transaction per cycle (4 of 122 requests), so function groups are not queued; order 27 makes the rejection name the placeholder route.

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 27 | **Done: a callback naming the function being created explains the route** (was: rejected as an unknown function, cost #1) | `listMap(items, "redactValue")` inside the creation of `redactValue` failed as `Unknown function 'redactValue'` | In `…ByName` creation, a callback, worker, task body, capability implementation or `functionReference` naming the function being created is a `ProtocolError` that says only a `directCall` is a self call and names the placeholder-then-replace route; replacement is unchanged. Tests: `SelfCallTests`; [session spec](../specs/agent-authoring-session.md#self-calls). |

### Findings from the 2026-09-30 probe 9

[Probe 9](../verification/rrawrlet-slice-probe9-20260930.md) authored rrawrlet's Team tool relay by name on main `10b5e11`, the first part to parse and print JSON text itself. It matched 212/228 relay lines at first; orders 28 and 29 raised that to 226/228 and then 227/228 (the remaining miss is an integer beyond Int64). Character-at-a-time text building was quadratic and was recorded, not queued. List appends have since become amortised O(1) ([verification](../verification/list-amortised-append-20260930.md)), so appending characters to a List and joining once with `StringJoin` is linear; character-at-a-time `StringConcat` became linear too with order 31.

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 28 | **Done: code point conversion** (was: no code point ↔ Int64 conversion, cost #1) | 14/228 lines failed; the BMP table cost 4 functions and 117 nodes; a literal cannot hold an unpaired surrogate | `StringFromCodePoint(code): Option<String>` (Python `chr`, surrogate codes included) and `StringToCodePoint(text): Option<Int64>` (Python `ord`). Re-authored: 2 functions, 4 nodes, 226/228; [decision](../decisions/string-code-point-conversion.md). |
| 29 | **Done: Float64 decimal text** (was: Float64 decimal text, cost #2) | `float()`/`repr()` emulated in 6 functions and 250 nodes, exact only for at most 15 digits and exponents within ±22; `Json.Float` carries its `repr` text because a Float64 the program computes cannot be printed | `Float64ToString(value): String` (Python `repr`) and `StringParseFloat64(text): Option<Float64>` (Python `float` on the JSON number grammar with leading zeros, correctly rounded, a signed infinity on overflow). Re-authored: 85 functions and 2,116 nodes (−6 functions, −263 nodes), 227/228 relay lines (1 known: an integer beyond Int64) and 180/182 float-heavy lines (both misses the same integer); [decision](../decisions/float64-decimal-text.md), [probe 9 follow-up](../verification/rrawrlet-slice-probe9-20260930.md#follow-up-order-29-float64-decimal-text). |

### Findings from the 2026-09-30 probe 10

[Probe 10](../verification/rrawrlet-slice-probe10-20260930.md) authored rrawrlet's OpenCode configuration and model catalog code by name on main `d58e709` and matched the real Python on 317/320 cases on its first run (3 known divergences). Nothing is blocked and nothing is queued.

### Findings from the 2026-09-30 probe 11

[Probe 11](../verification/rrawrlet-slice-probe11-20260930.md) authored the transcript reduction in `snapshot.py` and all of `model_health.py` by name on main `c689bde` and matched the real Python on 301/304 cases (3 known divergences). Nothing is blocked and nothing is queued.

Its largest new cost was Python's exceptions written as `Err` values threaded through folds that cannot stop (8% of the new nodes). **Done:** a pure `ListEffectFold` whose folder returns `Result<S, E>` is the fold that stops at the first `Err`, 5 nodes fewer per fold than the pass-through form; [decision](../decisions/result-fold-short-circuit.md).

### Findings from the 2026-10-01 probe 12

[Probe 12](../verification/rrawrlet-slice-probe12-20261001.md) authored the material and cache-key path of rrawrlet's `condense.py` by name on main `68f98d2` and matched the real Python on 304/304 cases, with no known divergences.

The largest cost was the missing bit operations: SHA-256 written in Int64 arithmetic was about 7,000 times slower than `hashlib`. Nothing was blocked. The owner chose the candidate below, and it is done.

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 30 | **Done: fixed-width bit operations** (was: cost #1) | 7 functions and 138 nodes emulate SHA-256's bitwise functions; they are half of the digest's run time (1.4 M calls ≈ 0.5 s), and the digest is 75% of the largest real run and caps a hashed file at about 1.5 MB per run | A separate `Bits32` primitive (C# `uint`), per the [experiment](../experiments/bit-representation.md)'s direction rather than masks on `Int64`: `Bits32FromInt64` (low 32 bits), `Bits32ToInt64` (unsigned), `Xor`, `And`, `Or`, `Not`, wrapping `Add`, `RotateRight` (amount floor-modulo 32) and logical `ShiftRight`/`ShiftLeft` (amount <= 0 unchanged, >= 32 zero), all total, with unsigned decimal run values and no literal. Probe 12's SHA-256 re-authored: 17 functions and 547 nodes (from 622), the NIST vectors including one million `a`, about 20 times the throughput (1.09 MB in 0.5 s instead of 3.4 s; 17.5 MB in 2.4 s where 2.2 MB timed out); [decision](../decisions/bits32-domain.md), [verification](../verification/bits32-20261001.md). |

### Findings from the 2026-10-01 probe 13

[Probe 13](../verification/rrawrlet-slice-probe13-20261001.md) authored rrawrlet's `ref_backup.py` and `supervisor.py` by name on main `29017a9` and, after 2 port bugs were fixed, matched the real Python on 371/371 cases. The largest costs, the missing `while` loop and the missing dict and set, did not limit reach; Map was weakly triggered. Nothing is blocked and nothing is queued.

### Findings from the 2026-10-01 probe 14

[Probe 14](../verification/rrawrlet-slice-probe14-20261001.md) authored rrawrlet's `repo_map.py` by name on main `7277ecd` and matched the real Python on 310/310 cases, with no known divergences.

Its largest cost was `bytes.decode("utf-8", "replace")` written by hand (4 functions, 228 nodes), whose natural first draft, one `StringConcat` per character, timed out at 195 KB. The owner chose amortised `StringConcat` as order 31; the lossy UTF-8 decode builtin was not queued then and became order 33 later.

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 31 | **Done: amortised `StringConcat`** (was: cost #1) | The decoder's natural first draft, one `StringConcat` per character, timed out at 195 KB; the list-and-join rewrite took 12 requests | The C# backend emits `String` as `NyStr`, a string or a prefix of a shared char store as `NyList<T>` is, and `StringConcat` onto the previous result takes the store's spare capacity by compare-exchange. A `StringConcat` fold rebuilds 1,048,576 code points in 0.59 s where 131,072 timed out, and the first-draft decode runs 3.12 MB in 1.91 s. Generated capability interfaces now use `NyStr` for `String`, which `hostSources` must follow; [verification](../verification/string-amortised-concat-20261001.md), [probe 14 follow-up](../verification/rrawrlet-slice-probe14-20261001.md#follow-up-order-31-amortised-stringconcat). |

### Findings from the 2026-10-01 probe 15

[Probe 15](../verification/rrawrlet-slice-probe15-20261001.md) authored rrawrlet's MCP server, `mcp.py`, by name on main `084890c` and matched the real Python on 427 of 428 cases (the one miss is by construction). Nothing is blocked and nothing is queued.

Its self-recursive event loop overflowed the stack between 12,000 and 14,000 turns and was rewritten as nested `ListEffectFold`s. It also needed the Float64 conversions that the [Float64 decision](../decisions/float64-conversion-and-run-values.md) deferred, meeting both of that decision's revisit conditions, and it restated large records to change one field. The candidates are, in order: a self `DirectCall` in tail position compiled as a loop, or a loop construct; `Float64ToInt64` and `Float64Round(value, digits)` with Python's rule (rounding to Int64 later became order 34); and a product update node (later order 32).

### Findings from the 2026-10-01 probe 16

[Probe 16](../verification/rrawrlet-slice-probe16-20261001.md) authored rrawrlet's `results.py` by name on main `084890c` and matched the real Python on 518/518 cases. With no Map, the natural tree comparison is quadratic and exceeds the 5 s run limit at 16,000 files, the strongest trigger for the deferred Map so far (probe 13 had found it weak). Strict and `surrogateescape` UTF-8 decoding cost 429 nodes, after probe 14's 228 for `replace`. Neither is queued.

### Findings from the 2026-10-01 probe 17

[Probe 17](../verification/rrawrlet-slice-probe17-20261001.md) authored all of rrawrlet's `verify.py` by name on main `601dfe4` and matched the real Python on 1,094/1,094 cases. No cost limits reach. Three recurring costs came back: `matchUnion` needing every arm (298 nodes, 11%), records restated to change one field (supporting probe 15's product update candidate), and hand-built `round(elapsed, 1)` (the second program to need `Float64Round`).

### Findings from the 2026-10-01 probe 18

[Probe 18](../verification/rrawrlet-slice-probe18-20261001.md) authored the rest of rrawrlet's `review.py` by name on main `ed1141b` and matched the real Python on 828/828 cases. No cost limits reach. `matchUnion` needing every arm came back (276 nodes, 10%, the fourth observation), probe 14's hand-written `replace` decoder was needed again (the third program), and Python's unbounded `int` appeared for the first time (small).

### Queue from the 2026-10-01 owner choice

After probe 18 the queue was empty again. The owner chose the two recurring language candidates that need no change of direction, in this order. The most frequent candidate, a default arm for `matchUnion` (probes 7, 10, 17 and 18), is not queued: it needs an owner decision first, because a default arm trades away the language's [exhaustiveness rule](../concepts/ai-first-design-principles.md#exhaustiveness-over-catch-all-behavior), which keeps the impact of adding a variant mechanically observable. (D-015 in the [foundational decisions](../decisions/foundational.md#d-015--f-is-the-initial-semantic-core-implementation-language), which forbids wildcard branches that absorb unknown domain cases, is the F# core's own code style, not a rule about Nyrwvy's `MatchUnion`.) Tail calls as loops (probe 15), Map (probe 16), `Float64Round` (probes 15 and 17) and unbounded integers (probe 18) stay [open candidates](#open-candidates).

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 32 | **Done: product update** | Records rebuilt to change one field in probes 7 (8 setters, 134 nodes, 10%), 10 (rebuilding helpers for 3 states), 15 (about 550 nodes, 2 functions over the 256-node limit) and 17 (`shStep` rebuilt its 5-field state in 11 places, about 68 of 183 nodes) | `ProductUpdate(typeId, source, fields)`: a copy with at least one named field replaced and the rest kept, emitted as a C# `with` expression. Probe 17's `shStep` re-authored with it is 131 nodes instead of 183, with the same results on 614 inputs, all equal to Python's `shlex.split`; [decision](../decisions/product-update.md). |
| 33 | **Done: UTF-8 decode with `replace`** | `bytes.decode("utf-8", "replace")` written by hand in probes 14 (4 functions, 228 nodes, 13%) and 18 (the same 228 nodes); probe 16 needed strict and `surrogateescape` modes (429 nodes) | `StringDecodeUtf8Lossy(bytes)`: Python's `replace` decoding of a String of one code point per byte, one U+FFFD per maximal ill-formed subpart, a code point above U+00FF read as byte 0xFF. One node where the hand-written decoder was 228; it matched Python on 5,024 inputs. Strict and `surrogateescape` were considered and not added (only probe 16 needed them); [decision](../decisions/utf8-decode.md). |

### Findings from the 2026-10-01 probe 19

[Probe 19](../verification/rrawrlet-slice-probe19-20261001.md) authored rrawrlet's `web_live_requests.py` by name on main `a2736ba`, the first probe after orders 32 and 33, and matched the real Python on 2,762/2,775 cases (8 known divergences and 5 cases beyond `runPure`'s argument and result limits). `ProductUpdate` and `StringDecodeUtf8Lossy` served it; `matchUnion` needing every arm came back a fifth time and Python's unbounded `int` a second time. Nothing is blocked and nothing is queued.

### Findings from the 2026-10-01 probe 20

[Probe 20](../verification/music-logic-probe20-20261001.md) is the first probe outside rrawrlet: #283's just-intonation module, authored by name on main `f9f6335`. It matched a Python reference on 3,400/3,400 cases. Nothing is blocked.

It converted Float64 to Int64 with Python's rounding by hand (95 nodes), the conversion probe 15 also needed, so it meets #283's decision rule and the [Float64 decision](../decisions/float64-conversion-and-run-values.md)'s revisit condition; the owner queued it as order 34 (below). Transcendental functions (#284) and checked Int64 arithmetic were first observations.

### Queue from the 2026-10-01 probe 20 owner choice

After probe 20 the owner chose its one candidate that met the decision rule. Transcendental functions (probe 20) stay an [open candidate](#open-candidates); checked Int64 arithmetic was added later as order 40.

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 34 | **Done: Float64 to Int64 with Python's rounding** | Probe 20 rounded to an EDO step and to seconds, and floored inside `exp2`, by hand (3 functions, 95 nodes, a 62-step binary search over `Int64ToFloat64`); probe 15 built milliseconds from Float64 seconds by hand | `Float64RoundToInt64` (Python `round(x)`, ties to even) and `Float64FloorToInt64` (`math.floor(x)`), both `Float64 -> Option<Int64>` with `None` for NaN, the infinities and results outside Int64. Probe 20 re-authored with them is 1,068 nodes instead of 1,153 and matches the same 3,400/3,400 cases; [decision](../decisions/float64-to-int64.md). |

The host side of a music application (#284) has a separate [design note](realtime-processors.md): a restricted `Processor` declaration that the validator proves real-time safe. It is proposed only; implementation waits for the note's evidence trigger.

### Queue from the 2026-10-01 standard-operations review

A review of the builtin surface against ordinary language libraries ([decision](../decisions/standard-operations.md)) found operations missing that programs commonly need. The owner chose to add those whose meaning has no choice to make, under the exception in the selection rule above. `ListRange`, `StringRepeat`, hexadecimal formatting and parsing, `StringToUpper` and Int64 `Abs`/`Min`/`Max` each had a choice to make; `ListRange` and hexadecimal text already had observed hand-written cost (probes 5, 7 and 12, and probes 9 to 12 and 16 to 18). The owner made those choices the same day ([decision](../decisions/standard-operation-choices.md)), which added orders 37 to 39; `StringToUpper` followed as order 42.

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 35 | **Done: reversing a List** | Probe 16 reversed a list with a fold of `ListConcat([item], acc)`, which copies the accumulator each step (O(n²)); probe 11 rewrote `reversed(lines[1:])` with a `break` as a forward fold (2 functions, 67 nodes). The only other route, `ListSortBy` with an always-true callback, hides the intent | `ListReverse(list)`: `List<T> -> List<T>`, Python `list(reversed(list))`, linear, never fails |
| 36 | **Done: Float64 sign and integral operations, and square root** | Probe 20 wrote `fabs` and `fmax` by hand. Square root and floor, ceiling and truncation as Float64 are ordinary numeric needs with one IEEE 754 result each | `Float64Sqrt`, `Float64Abs`, `Float64Negate`, `Float64Floor`, `Float64Ceiling`, `Float64Truncate`, all `Float64 -> Float64`, never fail |
| 37 | **Done: ranges and repeated text** (owner choice: a size too large to allocate is a host failure) | Probes 5 (`repeat` by self-recursion), 7 (`"x" * n` as a generator) and 12 (a constant list sliced only for its length) | `ListRange(start, end): List<Int64>`, Python `list(range(start, end))`, and `StringRepeat(text, count): String`, Python `text * count`; [decision](../decisions/standard-operation-choices.md) |
| 38 | **Done: hexadecimal text** (owner choice: signed like `Int64ToString`, `-ff` for -255) | Probes 9, 10, 11 (JSON `\u` escapes), 12, 16 (`hexdigest`), 17 and 18 (`isHex`) | `Int64ToHexString(value): String` and `StringParseInt64Hex(text): Option<Int64>`, strict like `StringParseInt64` |
| 39 | **Done: Int64 minimum, maximum and absolute value** | Probes 15 (`clampInt`) and 20 (`absI`, `dimMax`), 3 to 5 nodes each | `Int64Min`, `Int64Max` and `Int64WrappingAbs` (`Int64.MinValue` gives itself, as wrapping arithmetic does) |

### Owner choice of 2026-10-03

Checked Int64 arithmetic had one observation (probe 20) and was an open candidate waiting on another. The owner chose to add it on that observation ([decision](../decisions/checked-int64-arithmetic.md)). The same day the owner chose option D of the [`MatchUnion` design note](match-union-shared-arms.md) for the recurring repeated-arm cost.

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 40 | **Done: checked Int64 arithmetic** (owner choice on one observation) | Probe 20 wrote checked add and multiply by hand (`addChk`, `mulChk`: 59 nodes) because wrapping is the only Int64 arithmetic | `Int64CheckedAdd`, `Int64CheckedSubtract` and `Int64CheckedMultiply`, all `(Int64, Int64) -> Option<Int64>`: the exact result, or `None` outside Int64 |
| 41 | **Done: shared `MatchUnion` arms** (owner choice of option D; `otherwise` taken as AI-facing shorthand) | Probes 7, 10, 17, 18 and 19 repeated one body across arms: 8 to 11% of each port (probe 19: 133 arms, 295 nodes) | An arm names one or more variants and has one body; a recipe `otherwise` expands to an explicit variant list; [design note](match-union-shared-arms.md), [decision](../decisions/match-union-shared-arms.md) |
| 42 | **Done: uppercase** (owner choice: what one would ordinarily expect over Python; `ß` to `ẞ`) | None: found by the standard-operations review | `StringToUpper(text)`, the simple uppercase mapping per code point, the mirror of `StringToLower`; [decision](../decisions/string-uppercase.md) |
| 43 | **Done: ordinary rounding** (owner choice: what one would ordinarily expect over Python) | Probes 15 (milliseconds) and 17 (`round(x, 1)`) rounded to decimal places by hand; order 34 followed Python's ties to even | `Float64RoundToInt64` rounds half away from zero, and `Float64Round(value, digits): Float64` rounds the shortest decimal text half away from zero; [decision](../decisions/rounding-half-away-from-zero.md) |
| 44 | **Done: Map** (owner accepted the [design note](map.md)'s recommendations) | Probes 3, 7, 13, 14 and 16; probe 16's tree comparison was quadratic and over the run limit | `Map<K, V>` keyed by any equatable type, insertion order, `MapEmpty`/`Insert`/`Get`/`ContainsKey`/`Remove`/`Size`/`Keys`/`Values`; [decision](../decisions/map.md) |
| 45 | **Done: Map callbacks** | The same probes; `MapFromList` replaces the index-by-id folds, `MapFold` walks entries | `MapFold(map, seed, folder)` and `MapFromList(items, keyOf)`; [decision](../decisions/map.md) |
| 46 | **Done: Set** (Map design note, second slice) | Probes 13 (`set`) and 14 (constant sets as literal lists) | `Set<T>` with `SetEmpty`/`FromList`/`Insert`/`Contains`/`Remove`/`Size`/`ToList`, insertion order; [decision](../decisions/set.md) |
| 47 | **Done: set algebra and `MapFilter`** (owner choice of 2026-10-04: operations one ordinarily expects, ahead of a consumer) | None counted; the [Map design note](map.md) left filtering for later and the [Set decision](../decisions/set.md) left union, intersection and difference for later | `SetUnion`, `SetIntersection` and `SetDifference` keep the left operand's order (union appends the right's new items), and `MapFilter(map, keep)` keeps entries in place; [set](../decisions/set.md), [map](../decisions/map.md) |
| 48 | **Done: `MapGetOrDefault`, `MapMapValues` and `ListMinBy`** (owner choice of 2026-10-04, after probe 21: small ordinary builtins ahead of further evidence) | [Probe 21](../verification/map-set-probe21-20261004.md): get-or-default at every accumulating write, mapping a map's values once, the smallest item of a list once | `MapGetOrDefault(map, key, default)`, `MapMapValues(map, mapper)` keeping keys and order, and `ListMinBy(list, lessThan): Option<T>` keeping the first of equal items; [map](../decisions/map.md), [list-sort-and-string-ordering](../decisions/list-sort-and-string-ordering.md) |
| 49 | **Done: tail self-calls as loops** (owner accepted the [loops design note](loops.md)'s three recommendations on 2026-10-04) | Probe 15's event loop overflowed at 12,000–14,000 turns; probe 21's reachability and topological order ran as counted folds that could not stop early; a minimal `sumTo(n, acc)` overflowed between 50,000 and 100,000 calls | A self `DirectCall` in tail position (body result, `Bind` body, both `Conditional` branches, every match arm and `otherwise`, `BoolAnd`/`BoolOr` right operand) never grows the stack, as a language guarantee; Execution IR `tailCall` terminator, emitted as a parallel assignment of the parameter locals and a jump to the entry block; mutual recursion out of scope; [decision](../decisions/tail-self-calls.md) |

Of the remaining semantic-independence gaps, pure capture-free function values, stateless program capability implementations, reporting derived body effect requirements, typed function abort, and the shared-capability contract under `ParallelAll` are implemented (see the general-purpose roadmap). Stateful program capability implementations follow without a fixed order; probe 6's stateful guard did not need them. Runtime state identity and per-declaration content hashes stay deferred until a use case needs them. Float64 run values are implemented (order 13); the remaining T3 non-support, `failureType` operations, is picked up only when a concrete consumer appears.

Preserve these constraints:

1. **Use the maintained ordinary graph tools and MCP surface as the baseline.** Reuse the recorded Codex result; do not repeat the same live boundary without a relevant change or unresolved decision.
2. **Reuse the recipe-discovery evidence.** The bounded external-consumer follow-up removed the prior recipe trial-and-error; do not rerun it unless the interaction surface changes or a new real task shows comparable discovery friction.
3. **Keep #109 deferred.** Current evidence does not justify build-architecture work.
4. **Keep the workload boundary explicit.** Create implementation issues from the new plan's concrete gaps and completion criteria, not an assumed application architecture. Check current branches/PRs first. Dependency-tool exposure, retained pins, multiple/transitive dependencies, and reviewed upgrades are implemented; use the maintained roadmap rather than the original PR #214 review-date status.

The earlier #152 cleanup suggestion is historical planning context; this probe does not establish that issue/PR's current disposition or make it a prerequisite for using the maintained surface. Reassess any useful remainder separately against the then-current checkout.

### Findings from the 2026-10-05 probe 22

[Probe 22](../verification/map-set-probe22-20261005.md) authored probe 21's planner again on orders 48 and 49, plus parallel levels and a cycle path by depth-first search: 1,400/1,400 against a JavaScript reference, every function accepted first time, nothing blocked. Probe 21's scope took 175 nodes instead of 212 and two types instead of four; tail self-calls replaced both counted folds and ran 300,000 turns. The new costs are the explicit stack of the depth-first search and finding an item's position, both first observations and listed in the [open candidates](#open-candidates).

### Queue from the 2026-10-05 probe 22 owner choice

The owner queued both probe 22 costs in this order. For order 50 the owner accepted a large runtime change in principle and asked for its other risks first; after reading them in the [design note](list-stack-operations.md), the owner chose the smaller change.

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 50 | **Done: `ListLast` and `ListDropLast`** (owner choice of the smaller change) | Probe 22's depth-first search: 117 nodes, and quadratic because dropping the last item and appending copies the stack every turn | `ListLast(list): Option<T>` and `ListDropLast(list)`, a pop that shares the store; probe 22's search rewritten with them ran 40,000 frames in 0.66 s. The persistent vector is deferred; [decision](../decisions/list-last-and-drop-last.md) |
| 51 | **Done: `ListIndexOf` and `ListFindIndex`** | Probe 22 took the stack from a node to the top with a 16-node fold | `ListIndexOf(list, item): Option<Int64>` for an equatable item type and `ListFindIndex(list, predicate): Option<Int64>` with a `ListFilter`-style callback; both give the first match; [decision](../decisions/list-index.md) |

### Findings from the 2026-10-05 probe 23

[Probe 23](../verification/map-set-probe23-20261005.md) authored probe 22's planner on orders 50 and 51, plus a linear Kahn's algorithm and Tarjan's strongly connected components: 1,800/1,800 against a JavaScript reference, every function accepted first time, nothing blocked. The cycle path is linear on a chain (40,000 frames in 0.68 s, 300,000 in 3.6 s), and the linear Kahn's algorithm orders 100,000 names in 1.8 s with no language change. A push after a pop still copies the stack: on a constructed comb both searches exceed the run limit, which bears on the order 50 decision's revisit condition. The new cost is loop state threaded through every self-call. Both are listed in the [open candidates](#open-candidates).

### Queue from the 2026-10-05 probe 23 owner choice

Probe 23's comb was built on purpose, but the owner counts a constructed input when its shape could occur in a real program (the owner's rule: even a deliberately constructed case should be handled when it is realistically possible): a node whose first dependency is a leaf is ordinary. That meets the [order 50 decision](../decisions/list-last-and-drop-last.md#revisit-conditions)'s revisit condition. After order 52 left the components over the limit, the owner queued order 53 for their two remaining linear steps, and after the state-Product follow-up, order 54 for probe 23's second cost.

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 52 | **Done: persistent-vector list** (was: a push after a pop copies the stack) | [Probe 23](../verification/map-set-probe23-20261005.md): on a comb, where every push follows a pop, the cycle path exceeds the 5 s limit at 40,000 nodes and Tarjan's components at 20,000 | `NyList<T>` becomes a persistent vector, as the [design note](list-stack-operations.md) proposes, checked against its risks and acceptance criteria plus the comb at 40,000. No language, schema or IR change. The components loop has further linear steps per pop (`ListIndexOf` from the front, `SetDifference` over the left operand, `ListSlice` truncation); they are measured after order 52 and proposed separately. Implemented: the comb's cycle path at 40,000 takes 1.09 s; the components still exceed the limit at 20,000 ([decision](../decisions/persistent-vector-list.md)) |
| 53 | **Done: `ListLastIndexOf` and a smaller-side `SetDifference`** (owner choice after order 52) | After order 52, probe 23's components on a comb of 20,000 still exceeded the limit: each closed one-name component was found with `ListIndexOf` from the front of the component stack and removed from the on-stack set by a `SetDifference` over the whole set | `ListLastIndexOf(list, item): Option<Int64>`, the last equal item's index scanned from the end; `SetDifference` removes the right set's items one by one when it is the smaller set, with the same result. The comb of 40,000 takes 1.73 s ([decision](../decisions/list-last-index-and-set-difference.md)) |
| 54 | **Done: self calls that keep unlisted arguments** (owner choice: evidence sufficient after the follow-up) | Probe 23's `tarjanLoop` passed 47 of 70 self-call arguments through unchanged; a state Product with `ProductUpdate` was 236 nodes instead of 208 | `"keepUnlisted": true` on a name-resolved self-call `directCall` binds every unnamed parameter to itself, expanded when the recipe is read; `tarjanLoop`'s recipe is 27% shorter with 23 arguments instead of 70, and its graph is unchanged ([decision](../decisions/self-call-keep-unlisted.md)) |

### Findings from the 2026-10-05 probe 24

[Probe 24](../verification/map-set-probe24-20261005.md) authored probe 23's planner on orders 52 to 54, plus a breadth-first shortest dependency path and a project length: 2,200/2,200 against a JavaScript reference, 70 of 71 functions accepted first time (one slot-name slip), nothing blocked. Every self-call names only its changed arguments (`tarjanLoop` 70 → 23, graph unchanged), and probe 23's combs now run at 100,000 (cycle path 2.25 s, components 4.57 s). The new cost is a breadth-first queue: dequeuing with `ListSlice(1, …)` copies the queue, so on a fan of 40,000 both the shortest path and probe 21's `reachable` exceed the run limit, which bears on the order 52 decision's revisit condition. It is listed in the [open candidates](#open-candidates).

### Queue from the 2026-10-05 probe 24 owner choice

The owner queued probe 24's queue cost; the first step of order 55 was to choose the approach, and the owner chose (1).

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 55 | **Done: list slices that share structure** (was: a breadth-first queue copies itself on every dequeue) | [Probe 24](../verification/map-set-probe24-20261005.md): dequeuing with `ListSlice(1, …)`, the shortest path and probe 21's `reachable` exceed the 5 s limit on a fan of 40,000 and are quadratic from 10,000; a two-stack queue written by hand is linear (300,000 in 3.9 s, 9 more nodes) | Choose one, then measure the fan at 40,000 and 100,000. (1) A `ListSlice` from any start shares structure, through a start offset on the persistent vector: no language, schema or IR change, and existing code such as `reachable` becomes linear unchanged; the risks are retaining a large list behind a small slice (drop leaves the offset passes, or copy small slices) and an offset in every `NyList` operation, checked as order 52 was. (2) A `Queue<T>` type: the intent is in the type, but it is a language, schema and IR change, and existing code is not sped up. (3) No language change: document the two-stack idiom; nothing to implement, but the trap stays silent at small sizes and authors write `ListSlice(1, …)` first. Recommended: (1). **Chosen: (1)**: a slice keeping fewer than half of the positions up to its end copies; dequeuing 300,000 items takes 0.15 s, where 40,000 exceeded the limit ([decision](../decisions/list-slice-sharing.md)) |

### Findings from the 2026-10-05 probe 25

[Probe 25](../verification/rrawrlet-slice-probe25-20261005.md) authored rrawrlet's `codex.py`, the last module no probe had covered: the JSON-RPC client with its reader, request timeouts, tool-call handlers, turn deadlines and `close`, `codex_error`, and the process-group shutdown over `/proc`, with the process and pipes behind a 21-operation `Host`. Against the real Python on a virtual-clock event loop it matched 489/490 cases and 7,433 host calls in order, after two port bugs were fixed; the remaining case is a same-instant ordering asyncio allows and Nyrwvy cannot write. Nothing is blocked. The new costs are concurrency ones: no futures or channels between tasks, no time value, and no zero-time yield. Strict decoding with Python's error text came back as a second consumer of the UTF-8 decode decision's revisit condition. All are in the [open candidates](#open-candidates), none queued.

### Owner choices of 2026-10-06 and 2026-10-07

After probe 25 nothing was blocked. The owner chose three changes: a scoped handoff between tasks, from probe 25's first cost; checking the behavior of a pending change before committing it; and the Capability signature migration that the general-purpose roadmap's T1 row still listed as a remaining boundary.

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 56 | **Done: scoped one-shot handoff between tasks** (#362) | [Probe 25](../verification/rrawrlet-slice-probe25-20261005.md#costs-ranked-by-impact): `_read` resolves a future another task awaits, so the reader became one event loop over a 25-field state (40 functions, 1,283 nodes) | `OneShot<T>` owned by a `TaskScope`, with `OneShotCreate`, `OneShotComplete` (first value wins) and `OneShotWait` (non-consuming, optional limit); closed with its scope; snapshot JSON 14 / Nyason v10 ([contract](../specs/scoped-one-shot.md), [example](../../examples/scoped-result-handoff/README.md)). Registering a new cell with a task that is already running is [still open](task-communication-and-time.md#1-communication-between-tasks) |
| 57 | **Done: opt-in behavioral verification of a pending change** (#363) | Structural review shows what a proposal changes in the graph, not whether outputs change; the [experiment](../experiments/semantic-change-verification.md) and its [practical trial](../experiments/change-verification-practical-trial.md) ran real batch-formatter changes and coverage controls | `verifyChange` runs chosen cases against the committed baseline and the pending candidate through the `runPure` path (fake capabilities only) and reports observations, fingerprints, counterexamples and unknowns with a Change Review artifact; optional, not an approval gate or proof ([session contract](../specs/agent-authoring-session.md#opt-in-change-verification), [verification policy](../developer/verification.md)) |
| 58 | **Done: Capability operation signature migration** (#365) | T1's remaining boundary; the [config-request batch example](../../examples/config-request-batch/README.md) needed `fetch` to return `Result<String,FetchFailure>` to retry only retryable failures | `simulateCapabilityOperationSignatureMigration` (and `…ByName`) changes one operation's argument and result types with a complete set of repairs for its implementations and affected callers, in one reviewed publication; IDs, parameter shape, effects and `FailureType` unchanged ([decision](../decisions/capability-operation-signature-migration.md)) |

#364 fixed confined viewer writes to return errors instead of throwing; it is a fix, not an order.

### Owner choice of 2026-10-16: ordinary builtins

The owner chose to add ordinary builtins ahead of probe evidence, with semantics that follow normal expectation (Python where it applies, unless stated otherwise).

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 59 | **Done: `StringReplace` and the trims** (owner choice ahead of evidence) | None ranked; probe 8 wrote `str.replace` as `StringJoin(StringSplit(text, secret), "[REDACTED]")`, and several probes wrote `strip()` with `StringSpan`, `StringSpanBack` and `StringSlice` | `StringReplace(value, pattern, replacement)`, Python `str.replace(old, new)` with an empty pattern returning `value`; `StringTrim`, `StringTrimStart` and `StringTrimEnd` over Unicode White_Space ([decision](../decisions/string-replace-and-trim.md)) |
| 60 | **Done: `SetIsSubset` and `MapUnion`** (owner choice: ordinary builtins ahead of evidence) | None counted; the [Set](../decisions/set.md) and [Map](../decisions/map.md) decisions had no subset test and no merge of two maps | `SetIsSubset(left, right)` is Python `left <= right`; `MapUnion(left, right)` is Python `left \| right`, the order of inserting right into left with `MapInsert`; snapshot JSON 17 / Nyason v13 ([decision](../decisions/set-is-subset-and-map-union.md)) |

## Bounded Member integration evidence — 2026-10-12

The [offline integration composition](../verification/member-integration-20261012.md)
uses existing products, Results, Sets and effectful folds for the sequential
`Team.integrate` policy over already-observed candidate facts: 28/28 normalized
Python comparisons and virtual/reopened/moved/unedited-C# cases. It found no
missing primitive and adds no implementation queue. Live settlement polling,
concurrent lock equivalence, real Git safety/persistence and cancellation during
rollback remain outside that evidence. At that point, the Member lifecycle
reports tracked TaskWait caller cancellation as a separate confirmed gap.

The [save-failure follow-up](../verification/member-integration-save-failure-20261012.md)
extends the existing composition to 36 scenarios / 46 per-request observations:
the candidate retains captured HEAD even when save returns Err, so eligible
retries return Ok without repeating effects. It uses the existing Outcome product
and effectful fold and adds no primitive. Parent cancellation shielding during
integration/rollback remains separate; PR382's observer cancellation does not
establish that behavior.

## Parent cancellation during owned cleanup — 2026-10-12

The [finish/rollback probe](../verification/cleanup-shield-20261012.md) demonstrated
that parent-linked scopes and independent observer cancellation cannot shield a
mutation tail. The unchanged pinned-main compiler disagrees with Python in twelve
cancellation scenarios. A generic [lexical CancellationShield](../specs/cancellation-shield.md)
now protects a bounded callback, restores the caller token and preserves scope
ownership. Fourteen per-step oracle comparisons and six generic ownership/fault
controls pass. Snapshot JSON 15 / Nyason v11 gates the new node; older graphs are
unchanged. PR383's save-error ledger follow-up is independent and is not assumed
merged. Full controller composition, concurrent admission, real Git and restart
remain separate; this slice adds no speculative subsystem.

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

Revise this milestone record when later observed work changes its conclusions or creates a concrete M6 trigger. The language/tool implementation queue lives in this document, in the [language and tool implementation record](#language-and-tool-implementation-record); the linked general-purpose programming roadmap is a completed plan. Historical implementation details remain in the linked issues, PRs, specifications, verification records, and experiment artifacts rather than being duplicated here.
