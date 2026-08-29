# Milestone 2 — Bounded structural expression editing

**Status:** Proposed

This document defines a candidate next milestone. It is planning material, not a current semantic contract or a claim that the described agent operations already exist.

[Milestone 1](milestone-1.md) is complete for its accepted bounded-operation claim. Its ordinary agent surface can rename declarations, evolve a function signature with explicit call-site plans, resolve missing-argument obligations, and publish only an exact reviewed proposal. M1-D13 in the [Milestone 1 decisions](../decisions/milestone-1.md) explicitly leaves arbitrary List/numeric structural editing outside that claim.

## Current planning state

| Area | Status | Current artifact |
| --- | --- | --- |
| M2 direction | Proposed | This plan |
| M2.0 structural-edit contract | Drafted proposal; not accepted | [Proposed decisions](../decisions/milestone-2.md) and [proposed specification](../specs/bounded-structural-editing.md) |
| M2.1 implementation | Not started | Requires a separate implementation issue after contract review |
| M2.2 controlled evaluation | Not started | Requires implemented bounded authority and a newly frozen corpus |

Merging or reading the proposed decision/specification does not make the operation current behavior. Acceptance must be explicit, and implementation/evaluation evidence remains required before the milestone claim can become complete.

## Proposed milestone claim

If this plan and its contract are accepted and completed, an ordinary agent should be able to:

1. inspect a bounded function-local semantic slice;
2. request one supported structural expression change through a closed, typed intent;
3. let the system allocate identities for newly created semantic nodes;
4. simulate and fully validate the proposed snapshot;
5. review the exact requested intent, structural result, identity effects, readiness, and affected Focus slices;
6. commit or reject the exact reviewed proposal; and
7. lower and execute the exact committed state where the selected slice is executable.

The claim is deliberately limited to the target forms and expression recipes accepted during this milestone. It is **not** a claim of unrestricted graph editing or general construction of every implemented `SemanticNodeKind`.

## Why this is the next candidate

The semantic language already implements numeric and collection expression nodes, including `Float64Add`, `ListMap`, and `ListFilter`. The ordinary agent facade does not currently construct or replace `ValueGraph` structure, so implemented language capability and agent editing capability are no longer the same surface.

That gap should not be closed by exposing `Snapshot`, accepting raw `SemanticNode list` payloads, or treating JSON/Nyason patches as ordinary semantic operations. The next useful experiment is whether a small structural surface can preserve the same authority, identity, validation, review, and exact-publication properties proven in M1.

## Proposed design direction

The M2.0 draft narrows the first operation further than the original open questions in this plan. The rationale and normative candidate contract live in:

- [Milestone 2 proposed decisions](../decisions/milestone-2.md);
- [bounded structural editing specification](../specs/bounded-structural-editing.md).

### Complete function-body target

The draft chooses complete replacement of one existing function body rather than arbitrary node or subgraph replacement.

The proposed target is bound by:

- existing `FunctionId`;
- the exact inspected `SnapshotId`;
- the expected current body result `GraphNodeId`.

A function is already a stable semantic owner with an explicit result type and bounded Focus projection. This target avoids inventing expression-slot identity and avoids silently defining dead-node cleanup for the first slice.

### Closed and versioned Float64 recipe

The draft recipe contains only:

```text
Float64 parameter reference by ParameterId
exact Float64 literal by Float64Bits
Float64Add(left, right)
```

The recipe is bounded by proposed size/depth limits and materialized by the engine. It is not a serialized `ValueGraph` or general node-construction API. Adding another recipe case is an authority-surface change and requires explicit contract, tests, and evaluation coverage.

### System-owned identity

Existing semantic objects are referenced by stable typed IDs. Every new body node receives a system-issued production `GraphNodeId`; the agent cannot submit those new IDs.

The target function and its signature preserve identity. The complete old body-node set is replaced by a disjoint newly allocated body-node set. Identity-preserving local rewrites remain outside this first operation.

### Conservative obligation boundary

The first operation rejects when any durable obligation, open or resolved, targets a node in the old body. It does not delete, retarget, resolve, or invent invalidation for an obligation merely to permit replacement.

### Exact preconditions and stale failure

The inspected snapshot and expected result-node guards provide early target checks. Existing `TransactionRequest`, preview receipt, and reviewed publication remain the exact proposal authority.

If the workspace or target expectation changes, the operation/proposal fails closed. The system does not silently rebase or re-simulate an already reviewed structural proposal.

### Structural Change Review

The proposed resulting review distinguishes:

- requested target and recursive recipe;
- retained declaration identities;
- complete removed old-body node set;
- newly allocated node IDs and role-specific structural summaries;
- old and new result-node IDs;
- base and proposed Focus projections;
- readiness and exact receipt identities.

Requested intent and resulting diff remain separate facts.

### Full-pipeline slice

The operation does not count as accepted merely because transaction code can materialize it. The resulting existing semantic nodes must participate in full validation, fingerprints, persistence round-trip, bounded review, Typed Semantic IR, Execution IR, C# emission/build, and exact committed-state behavior verification.

## M2.0 — Structural-edit contract gate

**Status: drafted proposal; not accepted.**

Issue #42 produced the proposed decisions and specification linked above. They currently choose:

1. whole-function-body replacement as the initial stable target;
2. one exclusive structural operation per transaction;
3. a small recursive Float64 recipe;
4. system-side left-to-right post-order node allocation;
5. complete replacement of old body-node identities;
6. conservative rejection when obligations reference the old body;
7. explicit structural diff plus base/proposed Focus review;
8. a new versioned agent/runner/evaluation interface while reusing existing semantic node kinds.

Before implementation, review must either accept these choices or revise them explicitly. In particular, no implementation should substitute raw `ValueGraph`/`SemanticNode` payloads, silently preserve local IDs by structural similarity, or weaken obligation handling for convenience.

## M2.1 — Candidate first vertical slice: Float64 body replacement

The proposed logical request is:

```text
ReplaceFunctionBodyWithFloat64Recipe
  targetFunctionId
  expectedBaseSnapshotId
  expectedCurrentResultNodeId
  replacement
```

The candidate representative scenario replaces the complete body of one `Float64`-returning function with the sum of an existing owned `Float64` parameter and an exact literal.

It must prove that:

- the owning function and existing parameter retain identity;
- all new expression nodes receive system-issued identities;
- old and new body-node identity sets are disjoint and complete in review;
- wrong target type, foreign/wrong-type parameter, oversized/deep recipe, stale expectation, and obligation references reject explicitly;
- exact `Float64Bits`, including signed zero and canonical NaN behavior, survive persistence and lowering;
- stale reviewed publication fails through the existing exact receipt boundary;
- exact committed-state projection, lowering, backend build, and behavioral execution pass;
- unrelated semantic entities and relations do not change;
- the ordinary API and runner cannot accept raw graph state or caller-created node IDs.

The operation is complete-only and creates no obligation. Existing unrelated incompleteness remains governed by current readiness/commit policy, while the representative execution scenario starts and ends complete.

## Conditional extension: specialized List structure

After the Float64 slice and its review model are implemented and evaluated, decide whether to extend M2 with a specialized List recipe or defer that work to a later milestone.

A candidate extension would add one narrowly scoped operation such as `ListMap`, followed only if justified by `ListFilter`, `ListAny`, or `ListAll`. It must reference callbacks by stable `FunctionId`, preserve the current pure-callback contract, and reject incompatible or effectful callbacks conservatively.

This extension is intended to test whether agents reliably select a specialized semantic role rather than a generic call-shaped substitute. It is **not part of the core M2 exit criteria unless a later accepted decision explicitly adds it**.

## M2.2 — Evaluation gate

Freeze a new corpus version before recorded model runs. Do not reinterpret or extend `m1-agent-corpus-v1` after the fact.

The first structural-edit corpus should cover at least:

1. successful construction of the accepted Float64 body recipe;
2. invalid type, foreign parameter, and target-precondition rejection;
3. size/depth limit rejection;
4. obligation-protected body rejection;
5. stale structural proposal rejection and fresh recovery;
6. exact system-owned identity allocation;
7. structural review completeness and zero unrelated churn;
8. persistence/fingerprint behavior;
9. exact committed-state lowering and execution;
10. specialized-node selection only if the conditional List extension is accepted.

The runner adapter must expose only the accepted bounded operation. Raw snapshot replacement, raw JSON/Nyason mutation, arbitrary node-ID fabrication, and privileged harness controls remain outside the evaluated agent surface.

Record all attempts, including unsuccessful ones, with the same general discipline established by M1: fixed repository and tool revisions, model/runtime metadata where available, prompt/corpus revision, intervention class, command and retry counts, failure categories, semantic churn, exact verification evidence, and durable terminal artifacts.

No success percentage is fixed by this proposed plan. The completion decision must be based on reviewed evidence that identifies both useful behavior and remaining failure modes.

## Proposed work sequence

1. review and accept or revise the M2.0 proposed decisions/specification;
2. open and implement the bounded Float64 body-replacement slice without widening the accepted authority;
3. verify transaction, validation, diff/review, persistence, lowering, backend, and exact execution behavior locally;
4. add/version the restricted agent runner command;
5. freeze the structural-edit corpus before observed model results;
6. run and review the controlled evaluation tranche;
7. decide whether specialized List construction belongs in M2 or a later milestone;
8. update current references and mark only the evidenced claim complete.

## Proposed core exit criteria

| # | Criterion | Status |
| ---: | --- | --- |
| 1 | Target, recipe, identity, replacement, obligation, and structural-diff contract | **Proposed contract drafted; not accepted** |
| 2 | Ordinary agent authority remains bounded; no raw Snapshot/storage mutation surface | Not started |
| 3 | New semantic identities are system-issued | Not started |
| 4 | Exact inspected-base/target preconditions and stale-proposal failure are enforced | Not started |
| 5 | Structural intent and resulting structural diff are distinct review facts | Not started |
| 6 | Accepted Float64 recipe passes the full semantic-to-execution pipeline | Not started |
| 7 | Exact committed-state verification covers the representative structural edit | Not started |
| 8 | A frozen structural-edit corpus and controlled evaluation record exist | Not started |
| 9 | Current docs state the supported structural scope without widening the claim | Not started |

## Non-goals

Milestone 2 does not initially require:

- unrestricted graph construction, arbitrary node mutation, or snapshot replacement;
- structural operations for every implemented node kind;
- nested expression/subgraph targeting or expression-slot identity;
- identity-preserving local rewrites or local-node reuse;
- dead-node analysis or general graph garbage collection;
- combining structural replacement with other operations in one transaction;
- a conventional source syntax or text-first authoring workflow;
- generic lambdas, closures, or effectful collection callbacks;
- implicit numeric conversion or a generic numeric operator abstraction;
- general node movement, extraction, inlining, or optimization;
- semantic merge, automatic rebase, or multi-agent proposal composition;
- a final persistence-medium choice;
- broad workspace visualization or a general visual programming editor;
- ownership/borrowing semantics or another backend.

## Relationship to open questions

This milestone may provide evidence relevant to first-class identity for expression targets and broader Change Review layout, but it does not silently resolve the general identity, persistence, semantic merge/rebase, obligation-state, or workspace-view questions in [Open questions](../decisions/open-questions.md). Any durable resolution belongs in a separate accepted decision and, where normative, a specification.
