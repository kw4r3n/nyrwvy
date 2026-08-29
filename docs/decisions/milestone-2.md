# Milestone 2 decisions — bounded structural editing

**Status:** Accepted

These decisions define the accepted contract for the first Milestone 2 structural-editing slice implemented by `AgentStructuralWorkflow`. They refine the earlier M2 proposal using the implementation and local verification evidence recorded in [M2 Float64 structural-edit verification](../verification/m2-float64-structural-editing-20260830.md).

They remain constrained by the accepted [Milestone 1 decisions](milestone-1.md), especially system-owned identity, exact reviewed publication, conservative obligation handling, and the rule that arbitrary structural editing was outside the M1 claim.

## M2-D01 — The first structural target is one complete function body

- **Status:** Accepted
- **Date:** 2026-08-30

### Decision

The first structural operation replaces the complete body of one existing function. The request targets an existing `FunctionId` and carries exact expectations for the inspected `SnapshotId` and current body `ResultNodeId`.

Nested-node replacement, expression-slot identity, serialized positions, viewer coordinates, and general subgraph editing are outside this slice.

### Reason

A function already has stable semantic identity, an explicit result type, and a bounded Focus projection. Whole-body replacement avoids inventing local-slot identity and avoids defining detached/dead-node semantics prematurely.

### Consequences

- the function identity and signature remain unchanged;
- every old body node leaves the new authoritative body;
- local identity-preserving rewrites remain a later design problem.

### Revisit conditions

Revisit when controlled evidence shows that preserving or independently targeting a nested expression has concrete value.

## M2-D02 — Structural editing uses a separate bounded workflow surface

- **Status:** Accepted
- **Date:** 2026-08-30

### Decision

The first structural operation is exposed through `AgentStructuralWorkflow` and an opaque `AgentStructuralChangeProposal`, sharing the existing opaque `AgentWorkspace` but not adding a structural case to the M1 `AgentOperationIntent` / `SemanticOperation` unions.

The structural request therefore represents exactly one body replacement by construction rather than being one item in a heterogeneous operation list.

### Reason

This keeps the M1 operation contract stable while the new authority surface is evaluated. It also avoids accidentally making the internal `ValueGraph` representation or generic transaction operation list the public structural-authoring API.

### Consequences

- M1 rename/signature/obligation behavior remains unchanged;
- structural and M1 proposals are distinct opaque proposal types;
- future composition between structural and non-structural edits requires a separate accepted design.

### Revisit conditions

Revisit only after a concrete atomic multi-operation workflow cannot be represented safely as separate reviewed publications.

## M2-D03 — The first recipe is a closed Float64 algebra

- **Status:** Accepted
- **Date:** 2026-08-30

### Decision

The accepted recipe is exactly:

```text
Float64ExpressionRecipe =
  | Parameter(ParameterId)
  | Literal(Float64Bits)
  | Add(Float64ExpressionRecipe, Float64ExpressionRecipe)
```

The recipe is limited to 64 materialized nodes and depth 32. It accepts no raw `Snapshot`, `ValueGraph`, `SemanticNode`, storage patch, generated source, or caller-supplied production identity for new nodes.

### Reason

This is enough to exercise structural construction, scope/type checks, identity allocation, review, publication, lowering, and execution without simultaneously introducing conversions, callbacks, branches, or a generic graph builder.

### Consequences

- every accepted recipe is Float64-shaped once parameter references are validated;
- no implicit numeric conversion or generic numeric operator abstraction is introduced;
- List construction and higher-order structural recipes remain future work.

### Revisit conditions

Add recipe cases only with explicit contract, tests, review semantics, and evaluation coverage.

## M2-D04 — Replacement body identities are fresh and system-owned

- **Status:** Accepted
- **Date:** 2026-08-30

### Decision

After preflight succeeds, recipe occurrences are materialized left-to-right in post-order. Every occurrence receives a fresh production `GraphNodeId` from `StableIdAllocator`.

Repeated `Parameter(p)` occurrences create distinct `ParameterReference` nodes pointing to the same existing `ParameterId`. No old body-node ID is reused by structural similarity.

### Reason

The model does not yet define semantic continuity for local expression nodes. Fresh body-local identities make the first replacement contract deterministic in shape and reviewable without inventing an equivalence rule.

### Consequences

- old and new body-node ID sets are disjoint;
- function and parameter identities remain stable;
- no CSE, constant folding, operand reordering, or identity-preserving local rewrite occurs during materialization.

### Revisit conditions

Revisit when local identity continuity has demonstrated value and a precise preservation rule can be stated.

## M2-D05 — Durable obligations block removal of their target body

- **Status:** Accepted
- **Date:** 2026-08-30

### Decision

Simulation rejects replacement when any current obligation, whether `Open` or `Resolved`, targets a node in the old body.

The operation does not delete, retarget, resolve, or invalidate that obligation automatically.

### Reason

Obligations are durable workflow facts and current validation requires their target call node to remain meaningful. The current obligation state machine has no accepted invalidation transition for this case.

### Consequences

- obligation-protected bodies are ineligible for this operation;
- adding obligation invalidation/migration remains a separate decision.

### Revisit conditions

Revisit when a concrete consumer requires an explicit invalidation workflow that preserves attribution and reviewability.

## M2-D06 — Structural review separates requested intent from materialized result

- **Status:** Accepted
- **Date:** 2026-08-30

### Decision

`AgentStructuralChangeReview` preserves the recursive requested recipe separately from `FunctionBodyReplacementDiff`.

The resulting diff records the owner function, old/new result-node IDs, complete removed-node summaries, and materialized added-node summaries. The review also exposes base and proposed `FunctionFocusView` projections and an exact `TransactionPreviewReceipt`.

Removed summaries are stable-ID ordered; added summaries follow materialization order.

### Reason

Automatically allocated identities and materialized dependencies are resulting engine facts, not agent-authored intent. Review must show both without requiring raw snapshot comparison or generated code.

### Consequences

- before/after structure is directly inspectable;
- exact base/proposed fingerprints remain part of review binding;
- stale publication fails closed rather than rebasing or substituting another result.

### Revisit conditions

Revisit review layout after larger recipes produce real usability evidence.

## M2-D07 — Trusted structural publication preserves workspace history

- **Status:** Accepted
- **Date:** 2026-08-30

### Decision

Structural publication uses an assembly-internal `SemanticWorkspace.publishValidatedSnapshot` primitive. It requires the candidate snapshot parent to equal the current snapshot, rejects reuse of a previously published snapshot ID, revalidates the snapshot, and publishes it while retaining `CurrentSnapshot :: PreviousSnapshots` history.

The structural body itself uses existing semantic node kinds, so this slice does not introduce a new language node or snapshot schema solely because the body was authored through a new workflow.

### Reason

Reconstructing a fresh workspace from the proposed snapshot would discard publication history. A shared trusted publication primitive preserves the same workspace-history invariant without exposing raw snapshot publication to ordinary agents.

### Consequences

- the opaque workspace retains prior snapshots after structural publication;
- the raw publication primitive remains assembly-internal;
- snapshot persistence remains governed by existing codecs and schema-evolution policy;
- runner/evaluation protocol additions are still a separate M2.2 concern.

### Revisit conditions

Revisit only if future proposal composition or persistence requirements need a broader publication abstraction.