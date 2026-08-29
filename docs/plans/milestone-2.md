# Milestone 2 — Bounded structural expression editing

**Status:** Proposed

This document defines a candidate next milestone. It is planning material, not a current semantic contract or a claim that the described agent operations already exist.

[Milestone 1](milestone-1.md) is complete for its accepted bounded-operation claim. Its ordinary agent surface can rename declarations, evolve a function signature with explicit call-site plans, resolve missing-argument obligations, and publish only an exact reviewed proposal. M1-D13 in the [Milestone 1 decisions](../decisions/milestone-1.md) explicitly leaves arbitrary List/numeric structural editing outside that claim.

## Proposed milestone claim

If this plan is accepted and completed, an ordinary agent should be able to:

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

## Proposed design principles

### Bounded target

A structural intent must identify an explicit semantic owner and one supported expression target. It must not grant ambient access to every function body or arbitrary snapshot fields.

The first target may be a function-local result expression or another explicitly identified expression slot. The exact target model is a design decision, not fixed by this plan.

### Closed and versioned recipe

The agent supplies a closed algebra of supported semantic expression roles rather than serialized internal records. Adding another recipe case is an authority-surface change and requires validation, review, persistence, lowering, tests, and evaluation coverage appropriate to that case.

### System-owned identity

Existing semantic objects are referenced by their stable typed IDs. Newly created graph-node and transaction identities are allocated by the system. An ordinary agent does not submit production `GraphNodeId`, `SnapshotId`, or `TransactionId` values for new objects.

### Exact preconditions and stale failure

A structural operation must bind to an exact base and target expectation. If the owner, target slot, or expected expression has changed, the proposal must fail or become stale rather than silently applying to a nearby structure.

### Structural Change Review

The resulting review must make the structural effect understandable without requiring a raw snapshot diff. At minimum it should distinguish:

- the requested target and recipe;
- retained existing identities;
- newly allocated identities and their semantic roles;
- replaced or detached structure;
- affected functions and Focus slices;
- validation/readiness changes;
- exact base and proposed snapshot identities/fingerprints.

### Full-pipeline slice

A recipe case does not count as accepted merely because transaction code can materialize it. The slice must participate in the authoritative graph, full validation, fingerprints, persistence/schema handling, bounded queries and review, Typed Semantic IR, Execution IR, the selected backend, and executable verification where applicable.

## M2.0 — Structural-edit contract gate

Before implementation, record an accepted decision and normative specification covering the following questions.

### Target and ownership

- What is the first independently targetable expression slot?
- Is the operation limited to one owning function?
- How is the target identified without relying on serialized position or viewer layout?
- Does the target itself need first-class stable identity, or is owner plus a typed slot/precondition sufficient?

### Reuse, replacement, and reachability

- Which existing parameters, declarations, or expression nodes may a recipe reference?
- When is an existing node identity retained rather than recreated?
- What happens to structure no longer reachable from the selected result?
- Is cleanup part of the same semantic operation, a deterministic engine rule, or explicitly deferred?

### Type and scope checking

- How are parameter, pattern-binding, function, and local-node references scoped?
- At what stage are recipe types checked?
- Can a recipe represent an intentionally incomplete but safe state, or must the first slice be complete-only?

### Diff and review

- What structural diff facts are durable enough for review and evaluation?
- How are nested expression changes summarized without exposing a raw mutable representation?
- How is unrelated semantic churn measured?

### Persistence and compatibility

- Does the operation or review artifact require a schema-version change?
- Which parts are persistent semantic state, transaction/audit metadata, or runner-only evaluation data?

Do not begin by adding a general `ValueGraph` or `SemanticNode list` payload to `AgentOperationIntent`. The contract gate exists to prevent an internal representation from becoming the public authority model by convenience.

## M2.1 — Candidate first vertical slice: `Float64`

The first implementation candidate should use a deliberately small expression recipe, sufficient to exercise structural construction without introducing collection callback rules at the same time.

Candidate roles are:

- a permitted function-parameter reference by `ParameterId`;
- an exact `Float64Literal` represented by canonical `Float64Bits`;
- `Float64Add` over two recipe expressions.

The contract gate may narrow or adjust these roles. No implicit `Int64`/`Float64` conversion, generic numeric operator family, arbitrary bit operation, or backend-inferred coercion is introduced.

A representative successful scenario should replace one accepted function-local expression target with a sum of an existing `Float64` parameter and an exact literal. It should prove that:

- the owning function and existing parameter retain identity;
- every new expression node receives a system-issued identity;
- the old and new structural result is visible in Change Review;
- invalid type/scope combinations reject before publication;
- stale target expectations fail closed;
- exact committed-state projection, lowering, backend build, and behavior verification pass;
- unrelated semantic entities and relations do not change.

## Conditional extension: specialized List structure

After the Float64 slice and its review model are verified, decide whether to extend M2 with a specialized List recipe or defer that work to a later milestone.

A candidate extension would add one narrowly scoped operation such as `ListMap`, followed only if justified by `ListFilter`, `ListAny`, or `ListAll`. It must reference callbacks by stable `FunctionId`, preserve the current pure-callback contract, and reject incompatible or effectful callbacks conservatively.

This extension is intended to test whether agents reliably select a specialized semantic role rather than a generic call-shaped substitute. It is **not part of the core M2 exit criteria unless a later accepted decision explicitly adds it**.

## M2.2 — Evaluation gate

Freeze a new corpus version before recorded model runs. Do not reinterpret or extend `m1-agent-corpus-v1` after the fact.

The first structural-edit corpus should cover at least:

1. construction of the accepted Float64 expression recipe;
2. invalid type or out-of-scope reference rejection;
3. stale structural proposal rejection and fresh recovery;
4. exact system-owned identity allocation;
5. structural review completeness and zero unrelated churn;
6. exact committed-state lowering and execution;
7. specialized-node selection only if the conditional List extension is accepted.

The runner adapter must expose only the accepted bounded operation. Raw snapshot replacement, raw JSON/Nyason mutation, arbitrary node-ID fabrication, and privileged harness controls remain outside the evaluated agent surface.

Record all attempts, including unsuccessful ones, with the same general discipline established by M1: fixed repository and tool revisions, model/runtime metadata where available, prompt/corpus revision, intervention class, command and retry counts, failure categories, semantic churn, exact verification evidence, and durable terminal artifacts.

No success percentage is fixed by this proposed plan. The completion decision must be based on reviewed evidence that identifies both useful behavior and remaining failure modes.

## Proposed work sequence

1. accept or revise this milestone direction;
2. record the target/recipe/identity/diff decisions from M2.0;
3. write the normative bounded structural-edit specification;
4. implement the Float64 vertical slice through transaction, review, persistence, lowering, backend, and tests;
5. freeze the structural-edit corpus and expose the exact bounded runner command;
6. run and review the evaluation tranche;
7. decide whether specialized List construction belongs in M2 or a later milestone;
8. update current references and mark only the evidenced claim complete.

## Proposed core exit criteria

| # | Criterion | Status |
| ---: | --- | --- |
| 1 | Accepted target, recipe, identity, reachability, and structural-diff contract | Not started |
| 2 | Ordinary agent authority remains bounded; no raw Snapshot/storage mutation surface | Not started |
| 3 | New semantic identities are system-issued | Not started |
| 4 | Exact target preconditions and stale-proposal failure are enforced | Not started |
| 5 | Structural intent and resulting structural diff are distinct review facts | Not started |
| 6 | Accepted Float64 recipe passes the full semantic-to-execution pipeline | Not started |
| 7 | Exact committed-state verification covers the representative structural edit | Not started |
| 8 | A frozen structural-edit corpus and controlled evaluation record exist | Not started |
| 9 | Current docs state the supported structural scope without widening the claim | Not started |

## Non-goals

Milestone 2 does not initially require:

- unrestricted graph construction, arbitrary node mutation, or snapshot replacement;
- structural operations for every implemented node kind;
- a conventional source syntax or text-first authoring workflow;
- generic lambdas, closures, or effectful collection callbacks;
- implicit numeric conversion or a generic numeric operator abstraction;
- general node movement, extraction, inlining, or dead-structure optimization;
- semantic merge, automatic rebase, or multi-agent proposal composition;
- a final persistence-medium choice;
- broad workspace visualization or a general visual programming editor;
- ownership/borrowing semantics or another backend.

## Relationship to open questions

This milestone may provide evidence relevant to first-class identity for expression targets and broader Change Review layout, but it does not silently resolve the general identity, persistence, semantic merge/rebase, or workspace-view questions in [Open questions](../decisions/open-questions.md). Any durable resolution belongs in a separate accepted decision and, where normative, a specification.
