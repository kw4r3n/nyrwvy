# Post-M2 roadmap

- **Status:** Current planning context
- **Prepared:** 2026-09-15
- **Updated:** 2026-09-25
- **Current baseline:** Maintained ordinary AI graph tools and stdio MCP, with qualified Codex maintenance evidence and an on-demand expression-recipe reference driven by an independent consumer task
- **Purpose:** Record the maintained authoring baseline and define the evidence-triggered boundary for later work.

This document began as the post-M2 proposal for moving Nyrwvy from bounded structural editing toward ordinary AI authoring. M3, M4, and the first M5 slice are now implemented and verified. The roadmap therefore no longer treats those stages as future work.

Existing specifications and accepted decisions remain authoritative for exact contracts. This document records milestone outcomes, selection criteria, and conditions for opening later work. It does not create release dates, require broad benchmark programs, or pre-authorize external resources.

## Current recommendation

**Do not expand the language or infrastructure merely because M3-M5 are complete.** The next substantial implementation should be selected from a concrete blocked task, measured interaction cost, or real scale/collaboration pressure.

The 2026-09-24 rrawrlet workload review now supplies a concrete language/tool implementation queue in the [general-purpose programming roadmap](general-purpose-programming-roadmap.md). It targets the ability to author and maintain ordinary programs without editing source text, not rrawrlet-specific builtins or provider implementations. That roadmap's goals (G1–G4, T1–T3) were completed on 2026-09-25 through #214 and #215; its remaining T3 non-support (Float64 run values, operations with a declared `failureType`) is recorded there. The completed M3-M5 claims below remain unchanged; this does not automatically open M6 scale/collaboration work.

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

The real-host MCP probe and the [general-purpose programming roadmap](general-purpose-programming-roadmap.md) are complete. The next implementation targets come from two sources: a 2026-09-25 rrawrlet slice probe, and the gaps recorded in [Semantic independence](../concepts/semantic-independence.md#current-gaps).

The probe (main `05e3d24`, rrawrlet `d02990a`; recorded in the project's shared files, not in this repository) authored rrawrlet's `summarize`/`usage_is_complete` and a spawn → parallel members → integrate skeleton through the maintained session and `runPure` only. The data processing matched Python on 5 cases, and the skeleton passed 4 fake scenarios using `UseResource`, `ParallelAll` and `ListEffectFold`. It found the blockers and costs below, in order of impact. Function values were not needed for that slice.

**The order is provisional.** Items 1 and 2 are the two candidates for the next step, and neither has been chosen. Item 1 is the first blocker; item 2 is the smallest certain step. Revise the order with the reason recorded when a choice is made or new evidence appears.

| Order | Gap | Evidence | Smallest change |
| --- | --- | --- | --- |
| 1 | Concurrency control beyond `ParallelAll` | The rrawrlet leader waits for whichever member finishes first with a time limit, and stops one member from outside. `ParallelAll` only waits for all or cancels all on the first `Err`. Same area as the semantic-independence "no abort" and "shared capability under `ParallelAll`" gaps. | A design note first (structured races or task handles, time limits, per-worker cancellation, how abort and `UseResource` release interact), before any construct. |
| 2 | No String length or substring | `preview(text, limit)` cannot be written; only empty/contains/equal/concat and Int64 conversion exist. | A small G1-style addition with UTF-16 or code-point semantics stated. |
| 3 | Fakes cannot target one call | Fake responses are consumed in call order across all workers, so "only member 2 fails" cannot be pinned down. | Responses keyed by argument, or a separate fake per worker, in `runPure`. |
| 4 | Stable IDs still required | Effect/Capability creation has no name-resolved form, and `runPure` values take only TypeId/FieldId; the probe needed a 30-line name→ID helper. | Name-resolved forms with the same ambiguity rejection as other graph tools. |
| 5 | No Bool and/or/not, no Map | Expressible (nested conditionals; `List` of entries with a fold upsert), but verbose: 2 extra types and 2 extra functions in the probe. | Decide whether the review and authoring cost justifies dedicated constructs. |

The remaining semantic-independence gaps follow without a fixed order: scoped interpretation (program-chosen capability implementation), function values, and reporting inferred requirements (small and independent, so it may be taken earlier). Abort and the shared-capability contract are covered by item 1. Runtime state identity and per-declaration content hashes stay deferred until a use case needs them. The T3 non-support (Float64 run values, `failureType` operations) is picked up only when a concrete consumer appears.

Preserve these constraints:

1. **Use the maintained ordinary graph tools and MCP surface as the baseline.** Reuse the recorded Codex result; do not repeat the same live boundary without a relevant change or unresolved decision.
2. **Reuse the recipe-discovery evidence.** The bounded external-consumer follow-up removed the prior recipe trial-and-error; do not rerun it unless the interaction surface changes or a new real task shows comparable discovery friction.
3. **Keep #109 deferred.** Current evidence does not justify build-architecture work.
4. **Keep the workload boundary explicit.** Create implementation issues from the new plan's concrete gaps and completion criteria, not an assumed application architecture. Check current branches/PRs first so work is not duplicated.

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
