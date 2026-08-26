# Open questions status review — 2026-08-26

## Purpose

`open-questions.md` contains the original design questions and should remain useful as historical context, but several questions now have implementation-backed answers from Milestone 0 and early Milestone 1.

This document records the current status without pretending that partially solved topics are completely closed.

Status meanings:

- **Resolved for current architecture** — enough is decided to proceed; revisit only with contrary evidence.
- **Partially resolved** — a concrete current rule exists, but a broader future case remains open.
- **Open** — no accepted answer yet.
- **Deferred** — intentionally outside the current milestone; not a blocker.

## Summary

| Question | Status | Current answer |
|---|---|---|
| Q-001 Canonical persistence | Open | JSON is strict reference/debug codec; Nyason remains experimental; permanent storage medium undecided. |
| Q-002 Stable IDs | Partially resolved | Production IDs use UUIDv7 payloads; typed external domains; system allocates IDs for ordinary creation. Cross-repository import/merge remains open. |
| Q-003 Snapshots vs operation logs | Resolved for current architecture | Published snapshot is authoritative; operation history is provenance/audit, not competing state authority. |
| Q-004 First-class graph entities | Partially resolved | M0 entity boundary is implemented and verified; add identity only when targeting/provenance/query needs justify it. Future constructs may extend the boundary. |
| Q-005 Operation composition/conflicts | Partially resolved | Explicit sequential order inside a transaction; exact base/review binding; stale proposals reject. General semantic merge/rebase remains open. |
| Q-006 Obligation state machine | Partially resolved | `Open` and `Resolved` are current states. `Invalidated` is the next justified extension when stale-obligation handling needs it. |
| Q-007 Incremental validation | Resolved for current architecture | Full validation is the correctness oracle; future incremental validation is a behavior-preserving optimization/cache. |
| Q-008 Graph slices/layout | Partially resolved | Focus and Change Review contracts are implemented. Broader workspace views/layout algorithms remain open. |
| Q-009 Typed Semantic IR vs Execution IR | Resolved | Distinct schemas/stages are implemented and verified; Execution IR owns explicit operational order. |
| Q-010 Capability strictness | Partially resolved | Declare only facts with current validator/planner/backend/viewer consumers. Synchronous Console subset is proven; additional properties are demand-driven. |
| Q-011 Core types -> C# | Partially resolved | M0 mapping is verified and backend-local. List/Float64 mappings are future backend decisions constrained by semantic contracts. |
| Q-012 Ownership | Deferred | Managed semantics remain current. Ownership/borrowing waits for a backend/use case that requires it. |
| Q-013 RAST provenance | Open but low priority | No runtime dependency. A lightweight provenance convention should be defined before importing new RAST-derived tests/rules. |
| Q-014 Git integration | Partially resolved | Stable IDs survive clone/branch; canonical snapshot + semantic review artifacts are the direction. General semantic merge and final committed storage format remain open. |
| Q-015 Approval | Partially resolved | Approval is workflow metadata bound to an exact reviewed proposal/receipt. Cryptographic and organizational policy remains open. |

## Q-001 — Canonical persistence format

**Status: Open.**

Current evidence establishes:

- deterministic strict JSON codec;
- deterministic Nyason v0 experimental codec;
- canonical round-trip testing;
- no requirement for humans to edit storage directly.

It does not establish which format should become long-term canonical persistence.

Do not close Q-001 until focused agent/edit experiments and storage operational requirements provide evidence.

## Q-002 — Stable IDs

**Status: Partially resolved.**

Accepted current rules:

- production payloads are UUIDv7;
- in-memory semantic domains are typed;
- external/inspection forms use domain prefixes;
- ordinary creation allocates IDs inside trusted tooling rather than asking the agent to invent them;
- Git clone/branch/history-preserving fork preserve semantic IDs;
- semantic duplication/template instantiation creates new IDs.

Still open:

- general cross-repository import;
- package identity interaction;
- semantic merge collision handling beyond rejection;
- whether externally imported identities can be trusted/preserved automatically.

## Q-003 — Snapshots and operation logs

**Status: Resolved for current architecture.**

The published snapshot is authoritative current program state.

Operation history is optional provenance/audit information and must not be a second source of truth. Replay can be used as a check or migration tool but is not required to reconstruct current meaning.

## Q-004 — First-class graph entities

**Status: Partially resolved.**

The M0 rule has held up:

> give stable identity to objects that require independent targeting, continuity, provenance, durable diagnostics, or durable query references; keep pure associations embedded.

Current identified entities include declarations, parameters, pattern bindings, graph nodes, effects/capabilities, obligations, snapshots, and transactions. Match arms and argument/field bindings remain associations.

New M1 collection element slots should initially remain associations unless real editing operations demonstrate independent-targeting pressure.

## Q-005 — Operation composition and conflicts

**Status: Partially resolved.**

Current rules:

- operation order inside one transaction is explicit and semantic;
- simulation and commit use the same ordered request;
- reviewed commit is bound to exact base/proposed fingerprints;
- stale proposal => reject;
- no silent automatic rebase/reorder.

Still open:

- semantic conflict sets;
- multi-agent proposal merge;
- automatic rebase;
- proven commuting-operation optimization.

Any future merge/rebase creates a new proposal and therefore requires new review.

## Q-006 — Obligation state machine

**Status: Partially resolved.**

Implemented:

- `Open`;
- `Resolved` with resolution provenance.

Next justified extension:

- `Invalidated` when a later semantic edit makes an existing obligation no longer applicable.

Do not add `Superseded` or additional workflow states until a consumer needs the distinction.

## Q-007 — Incremental validation

**Status: Resolved for current architecture.**

Full deterministic validation is normative.

Incremental validation, if introduced, is a cache/scheduling optimization that must produce the same result and may safely fall back to full recomputation.

The concrete incremental algorithm remains a performance task, not a language-semantics question.

## Q-008 — Graph slices and layout

**Status: Partially resolved.**

Implemented and verified:

- Function Focus slice;
- Change Review slice;
- typed viewer model;
- deterministic read-only HTML projection;
- relation-specific roles rather than generic unlabeled graph edges.

Still open:

- broader workspace navigation;
- spatial stability algorithms;
- richer progressive disclosure;
- interactive detail/UI behavior;
- layout choices for larger collection-heavy programs.

## Q-009 — Typed Semantic IR vs Execution IR

**Status: Resolved.**

They are distinct schemas with distinct responsibilities.

- Typed Semantic IR: validated backend-neutral meaning, complete types/effects/provenance, no editing obligations or execution order.
- Execution IR: explicit operational order, blocks, values, terminators, extraction, capability invocation.
- target backend consumes Execution IR, not the authoritative graph.

Revisit only if concrete implementation evidence shows the separation has become artificial.

## Q-010 — Capability contract strictness

**Status: Partially resolved.**

Rule:

> capability metadata is added only when a validator, planner, backend, viewer, or agent workflow has a concrete consumer.

Do not speculatively add determinism/idempotency/thread-safety/cancellation/resource metadata merely because other systems often have it.

The synchronous Console contract proves the minimum boundary. Future capabilities may justify additional explicit facts.

## Q-011 — C# representations

**Status: Partially resolved.**

M0 representations for String/Bool/Unit, Option/Result, products/unions, functions, and Console are verified.

The invariant is resolved: representation is backend-local and cannot redefine semantic meaning.

Still open:

- `List<T>` concrete C# representation;
- `Float64` bit-preserving emission details;
- performance-specialized layouts;
- Native AOT-specific choices.

## Q-012 — Ownership

**Status: Deferred.**

M1 keeps managed shared-value semantics.

Do not introduce ownership/borrowing syntax or semantic obligations until a non-GC backend, resource lifetime, or concurrency use case demonstrates a concrete need.

## Q-013 — RAST provenance

**Status: Open, non-blocking.**

Accepted invariant:

- RAST is reference/migration input only;
- no runtime dependency.

Before importing additional RAST-derived semantic rules/tests, define a lightweight source-revision/provenance annotation convention.

## Q-014 — Git integration

**Status: Partially resolved.**

Accepted direction:

- clone/branch preserve stable semantic IDs;
- generated C# is not ordinary authoritative source;
- semantic review is based on transactions/diffs, not only serialized line diffs;
- canonical committed state must be sufficient to rebuild without a centralized service.

Still open:

- final persistence file format;
- package/module file layout;
- semantic merge UX;
- cross-repository imports;
- whether semantic transaction/audit records are committed by default.

## Q-015 — Approval

**Status: Partially resolved.**

The technical binding question is resolved:

- approval is workflow metadata, not executable graph semantics;
- publication authorization must bind to the exact reviewed proposal/base/result;
- changed/stale proposal requires new approval.

Preview receipts supply this binding primitive.

Still open:

- signatures;
- approver identity model;
- roles/quorum;
- automated policy;
- repository/service storage location for approval records.

## New M1 questions

The following are intentionally not answered by the original Q-001..Q-015 register.

### M1-Q1 — Float64 NaN payload policy

Should semantic Float64 literals preserve arbitrary NaN payload bits or canonicalize NaN literals to one quiet-NaN representation?

Resolve before finalizing the Float64 persistence/backend slice.

### M1-Q2 — When do specialized nodes justify generalization?

Use agent-editing evidence from `ListMap` / `ListFilter` / numeric nodes before introducing generic higher-order or numeric operator abstractions.

No threshold is preselected; compare error rate, query burden, node-selection reliability, and schema complexity.

### M1-Q3 — What is the first meaningful performance threshold for incremental validation?

Do not implement incremental validation simply because it is architecturally possible. Measure representative M1 graph sizes and preview latency first.

### M1-Q4 — What is the minimal package/module semantic model?

Physical .NET project boundaries can be split during M1 without inventing language-level package/module semantics. Language-level modules/packages need their own stable identities and operation/query contracts later.
