# Milestone 2 proposed decisions — 2026-08-30

**Status:** Proposed

These entries define the current design proposal for the first Milestone 2 structural-editing slice. They are not accepted current behavior. Implementation must not treat them as authority until the proposal is reviewed and accepted.

The proposal refines the direction in the [Milestone 2 plan](../plans/milestone-2.md) and remains constrained by the accepted [Milestone 1 decisions](milestone-1.md), especially M1-D06, M1-D07, M1-D12, and M1-D13.

## M2-D01 — The first structural target is one complete function body

- **Status:** Proposed
- **Date:** 2026-08-30

### Decision

The first bounded structural operation replaces the complete body of one existing function.

The request identifies:

- the target by existing `FunctionId`;
- the exact inspected base by existing `SnapshotId`;
- the expected current body result by existing `GraphNodeId`; and
- a closed replacement recipe.

The operation does not initially target an arbitrary node, nested branch, expression slot, serialized position, or viewer coordinate.

### Reason

A function already has stable semantic identity, an explicit result type, a bounded query surface, and an established Change Review owner. A nested expression slot does not yet have an independent identity contract.

Whole-body replacement also avoids silently introducing dead-node cleanup semantics. The current `ValueGraph` model permits nodes that are not reachable from the result, so replacing only the result or a subgraph would require a separate decision about retained, detached, and collected structure.

### Consequences

- the target function identity and signature remain intact;
- the old body is replaced as one coherent target-scoped change;
- all old body-node identities leave the authoritative snapshot;
- general subgraph editing, local-node movement, and expression-slot identity remain outside the first slice;
- the expected base/result guards are preconditions, while the reviewed transaction receipt remains the publication authority.

### Revisit conditions

Revisit after whole-body replacement has controlled evaluation evidence and a concrete use case requires preserving or targeting a nested expression independently.

## M2-D02 — The first recipe is a closed, specialized Float64 algebra

- **Status:** Proposed
- **Date:** 2026-08-30

### Decision

The first recipe supports only these semantic roles:

```text
Float64 parameter reference by ParameterId
exact Float64 literal by Float64Bits
Float64Add(left, right)
```

The agent does not submit `ValueGraph`, `SemanticNode`, `SemanticNodeKind`, arbitrary node fields, production IDs for new nodes, JSON patches, or Nyason patches.

The recipe is recursively bounded by an explicit maximum node count and depth. New cases require a deliberate authority-surface extension.

### Reason

A small specialized algebra tests structural construction, scoping, system-side identity allocation, review, lowering, and execution without simultaneously introducing conversions, generic operators, collection callbacks, branch scope, or a general graph-builder protocol.

### Consequences

- every recipe is statically Float64-shaped once its parameter references are validated;
- no implicit `Int64`/`Float64` conversion or backend coercion is introduced;
- arbitrary existing local-node reuse and common-subexpression sharing are not available;
- List construction and higher-order callback selection remain a later explicit decision.

### Revisit conditions

Add another recipe case only after the current case has full-pipeline tests and evaluation evidence showing that the extension is useful and remains understandable in review.

## M2-D03 — Recipe materialization creates a fresh body-local identity set

- **Status:** Proposed
- **Date:** 2026-08-30

### Decision

The engine validates the target and recipe before materialization, then allocates every new `GraphNodeId` system-side in left-to-right post-order.

Each recipe occurrence materializes one distinct semantic node. Repeated references to the same `ParameterId` therefore create distinct `ParameterReference` nodes unless a later operation explicitly defines sharing.

The target function, its parameters, result type, required effects, callers, and referenced declarations preserve their existing identities. No old body-node identity is reused in the replacement body.

### Reason

The distinction between declaration identity and expression-node identity should remain explicit. Reusing old local nodes by structural similarity would require an equivalence and continuity rule that the current model does not define. Letting the engine allocate a fresh, closed body gives one simple and reviewable first contract.

### Consequences

- the resulting body has a deterministic dependency/materialization order, although production ID payloads remain opaque UUIDv7 values;
- old and new node sets are disjoint;
- unrelated semantic identities remain unchanged;
- identity-preserving local rewrites are not implied by this operation.

### Revisit conditions

Revisit when preserving a local node across a targeted edit provides demonstrable review, provenance, or incremental-processing value and an exact semantic continuity rule can be stated.

## M2-D04 — Durable obligations referencing the old body block replacement

- **Status:** Proposed
- **Date:** 2026-08-30

### Decision

Simulation rejects whole-body replacement when any durable obligation, whether `Open` or `Resolved`, targets a graph node in the old body.

The first operation does not delete, rewrite, resolve, or invalidate such obligations automatically.

### Reason

Obligations are durable semantic workflow facts and current validation requires their target call node to remain present. The implemented state machine has no `Invalidated` state. Removing the target while silently deleting or rewriting the obligation would violate the accepted conservative obligation policy.

### Consequences

- functions with body-targeting obligation history are not eligible for the first replacement operation;
- rejection is explicit and happens before new body IDs are allocated;
- adding `Invalidated` or another migration rule remains a separate decision with its own consumers and review behavior.

### Revisit conditions

Revisit when an implemented obligation-invalidation workflow can preserve attribution, explain the transition, and distinguish invalidation from resolution or deletion.

## M2-D05 — Structural Change Review exposes both before and after

- **Status:** Proposed
- **Date:** 2026-08-30

### Decision

Change Review for body replacement preserves the requested recipe separately from the resulting structural diff.

The structural diff identifies at least:

- the target `FunctionId`;
- old and new result-node IDs;
- every removed old-body node ID;
- every added node with its allocated ID and role-specific read-only summary; and
- the affected function.

The review projection exposes bounded base and proposed Focus views for the affected function. Existing proposed-state `FunctionFocus` behavior may be retained for compatibility, but the old body must not be reconstructed only from prose or removed-ID counts.

### Reason

A list of changed IDs cannot explain a structural change by itself. Reviewers need the exact old and new shape, types, literals, parameter references, and dependencies while still distinguishing what the agent requested from what the engine materialized.

### Consequences

- automatic ID allocation remains visible in resulting change facts;
- unrelated churn can be checked against the complete removed/added sets and affected-function boundary;
- read-only structural projection does not grant raw snapshot mutation authority;
- viewer and evaluation artifacts can compare exact before/after meaning without generated C# becoming the review source.

### Revisit conditions

Revisit the projection shape after usability evidence shows which structural facts are necessary or excessively verbose for larger recipes.

## M2-D06 — The first structural operation is exclusive and complete-only

- **Status:** Proposed
- **Date:** 2026-08-30

### Decision

A transaction containing the first body-replacement operation contains exactly one operation. It cannot be combined with rename, signature evolution, obligation resolution, or another body replacement in the same request.

The Float64 recipe cannot intentionally create an incomplete body or a new obligation. Existing unrelated incompleteness remains governed by the ordinary readiness and commit-policy rules; the representative executable scenario begins and ends semantically complete.

### Reason

Composition would immediately require semantics for operation ordering around target guards, newly allocated local IDs, multiple body replacements, and interactions with obligation resolution. Those concerns are useful later but are not necessary to test the first bounded structural authority.

### Consequences

- simulation rejects a mixed or multi-operation structural request explicitly;
- stale publication still uses the existing exact reviewed-proposal boundary;
- the initial corpus can attribute structural outcomes to one intent;
- operation composition remains a later evaluated extension rather than an accidental property.

### Revisit conditions

Revisit after single-operation behavior is reliable and a concrete atomic multi-operation workflow cannot be represented safely as separate reviewed publications.

## M2-D07 — Snapshot semantics reuse existing node kinds; protocol artifacts are versioned

- **Status:** Proposed
- **Date:** 2026-08-30

### Decision

The first replacement body uses existing `ParameterReference`, `Float64Literal`, and `Float64Add` semantic node kinds, so it does not introduce a new language construct or redefine the snapshot schema by itself.

The new agent intent, structural diff, review projection, runner command, and evaluation artifacts are versioned as interface/protocol changes. Any discovered persistence impact must follow the existing schema-evolution policy rather than being hidden as an implementation detail.

### Reason

The semantic result already has defined validation, persistence, lowering, and backend behavior. The new work is editing authority and review semantics. Keeping those concerns separate prevents an operation encoding from becoming a competing program representation.

### Consequences

- current snapshot codecs remain the semantic baseline unless implementation proves a real schema change is required;
- recorded M1 corpus/tool conditions are not retroactively reinterpreted;
- M2 uses a new frozen corpus and tool revision before making an agent-reliability claim.

### Revisit conditions

Revisit if implementation requires persistent operation logs, review receipts, or protocol artifacts whose compatibility cannot be maintained without an explicit schema version.
