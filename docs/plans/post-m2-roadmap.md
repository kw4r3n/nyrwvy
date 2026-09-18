# Post-M2 roadmap

- **Status:** Current planning context
- **Prepared:** 2026-09-15
- **Updated:** 2026-09-17
- **Current baseline:** `main` after completion of the first bounded M5 evolution slice
- **Purpose:** Record what M3-M5 established and define the evidence-triggered boundary for later work.

This document began as the post-M2 proposal for moving Nyrwvy from bounded structural editing toward ordinary AI authoring. M3, M4, and the first M5 slice are now implemented and verified. The roadmap therefore no longer treats those stages as future work.

Existing specifications and accepted decisions remain authoritative for exact contracts. This document records milestone outcomes, selection criteria, and conditions for opening later work. It does not create release dates, require broad benchmark programs, or pre-authorize external resources.

## Current recommendation

**Do not expand the language or infrastructure merely because M3-M5 are complete.** The next substantial implementation should be selected from a concrete blocked task, measured interaction cost, or real scale/collaboration pressure.

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

With M3-M5's first slice complete, the remaining repository work is deliberately small:

1. **Bring this roadmap current and merge it.** Stop presenting completed M3/M4/M5 work as future milestones.
2. **Reassess #152 against current main.** Port only still-useful verification/documentation cleanup; do not blindly revive stale proposal-state refactoring or old hardcoded assumptions.
3. **Keep #109 deferred.** Current evidence does not justify build-architecture work.
4. **Do not pre-create the next semantic milestone.** Open the next implementation issue only when a concrete task or measured cost identifies it.

If #152's useful portion reduces repetitive verification commands or stale documentation, prefer one small current-main PR and close the old PR as superseded. If nothing remains valuable, close it without replacement.

## Evidence and authority pointers

- [Vision and priority order](../concepts/vision.md)
- [Architecture](../developer/architecture.md)
- [Current language subset](../reference/language-subset.md)
- [Targeted expression replacement](../decisions/targeted-expression-replacement.md)
- [Staged declaration authoring](../decisions/declaration-authoring.md)
- [Snapshot-backed agent authoring session](../specs/agent-authoring-session.md)
- [Verification philosophy](../developer/verification.md)
- [Open questions](../decisions/open-questions.md)
- Completed work/issues: #148, #149, #153, #154, #164, #166, #168, #170
- Deferred build-cost investigation: #109

The roadmap should be revised again only when later observed work changes these milestone conclusions or creates a concrete M6 trigger. Historical implementation details remain in the linked issues, PRs, specifications, verification records, and experiment artifacts rather than being duplicated here.