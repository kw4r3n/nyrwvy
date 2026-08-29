# Bounded structural editing — Float64 body replacement

**Status:** Current

This specification defines the implemented first Milestone 2 structural-editing contract. The accepted rationale is recorded in the [Milestone 2 decisions](../decisions/milestone-2.md), and implementation evidence is recorded in [M2 Float64 structural-edit verification](../verification/m2-float64-structural-editing-20260830.md).

## Scope

The current structural operation replaces the complete body of one existing `Float64`-returning function with a body materialized from a closed recipe.

The accepted lifecycle is:

```text
bounded AgentWorkspace state
  -> AgentReplaceFunctionBodyWithFloat64Recipe
  -> structural preflight
  -> system-side node materialization
  -> full snapshot validation/readiness
  -> AgentStructuralChangeReview
  -> opaque AgentStructuralChangeProposal
  -> exact reviewed commit/reject
  -> Typed Semantic IR -> Execution IR -> C#
```

This is not arbitrary graph editing and does not expose raw snapshot/storage mutation authority.

## Workflow boundary

The operation is implemented by `AgentStructuralWorkflow` and is deliberately separate from the M1 `AgentWorkflow`, `AgentOperationIntent`, `SemanticOperation`, and `SemanticDiff` surfaces.

The structural workflow shares the opaque `AgentWorkspace`. Trusted access to the underlying semantic workspace/snapshot is assembly-internal.

A structural proposal represents exactly one body replacement. There is no accepted operation list that can combine it atomically with rename, signature evolution, obligation resolution, or another body replacement.

## Request

```text
AgentReplaceFunctionBodyWithFloat64Recipe
  TargetFunctionId: FunctionId
  ExpectedBaseSnapshotId: SnapshotId
  ExpectedCurrentResultNodeId: GraphNodeId
  Replacement: Float64ExpressionRecipe
```

The first recipe algebra is exactly:

```text
Float64ExpressionRecipe =
  | Parameter(ParameterId)
  | Literal(Float64Bits)
  | Add(Float64ExpressionRecipe, Float64ExpressionRecipe)
```

The request does not accept:

- a raw `Snapshot`, `SemanticFunction`, `ValueGraph`, `SemanticNode`, or `SemanticNodeKind`;
- a caller-supplied production `GraphNodeId`, `SnapshotId`, or `TransactionId` for newly created objects;
- JSON/Nyason/generated-C# patches;
- arbitrary local-node references;
- callbacks or generic node-construction functions.

## Bounds

Before allocating replacement body IDs:

- maximum materialized recipe nodes: **64**;
- maximum recipe depth: **32**;
- every occurrence counts as one node;
- leaf depth is 1;
- `Add(left,right)` depth is `1 + max(depth(left), depth(right))`.

Depth is checked before node count so the two diagnostic classes remain distinguishable for a recipe that violates both.

## Preflight

Before materialization, simulation rejects unless:

1. `ExpectedBaseSnapshotId` equals the current workspace snapshot ID;
2. `TargetFunctionId` identifies a current function;
3. its current `Body.ResultNodeId` equals `ExpectedCurrentResultNodeId`;
4. its declared result type is `Primitive Float64`;
5. recipe depth and node count are within bounds;
6. every `Parameter` belongs to the target function;
7. every referenced parameter has type `Primitive Float64`;
8. no current obligation, open or resolved, targets any node recursively contained in the old body.

The operation does not select targets or parameters by display name and does not infer conversions.

## Materialization

After preflight succeeds, materialization is left-to-right post-order.

- `Parameter(p)` allocates a fresh node and emits `ParameterReference p`.
- `Literal(bits)` allocates a fresh node and emits `Float64Literal bits`.
- `Add(left,right)` materializes `left`, then `right`, allocates a fresh node, and emits `Float64Add(leftResult,rightResult)`.

Every recipe occurrence gets a distinct `GraphNodeId` from `StableIdAllocator`.

The operation performs no CSE, local-node reuse, constant folding, algebraic simplification, operand reordering, or implicit conversion.

`Float64Bits` remains authoritative, including canonical semantic NaN and preservation of every non-NaN binary64 bit pattern.

## Snapshot transition

The proposed snapshot differs semantically from the base by replacing only the target function body. Snapshot identity and parent are updated for the proposal/publication lifecycle.

The target function preserves:

- `FunctionId`;
- display name;
- ordered parameters and their IDs/names/types;
- result type;
- required effects.

All unrelated functions, types, effects, capabilities, and obligations remain unchanged.

Every old body node is absent from the new body; no old body node is retained by structural similarity.

## Obligation behavior

Let `OldBodyNodeIds` contain every node recursively collected from the current target body.

If any obligation has `CallNodeId` in that set, simulation rejects regardless of whether the obligation is `Open` or `Resolved`.

The workflow does not delete, retarget, resolve, replace, or invalidate such an obligation.

## Review model

`AgentStructuralChangeReview` contains:

- `RequestedChange` — the original typed structural request/recipe;
- `Receipt` — exact base/proposed snapshot and fingerprint binding;
- `Readiness`;
- `ResultingChange` — `FunctionBodyReplacementDiff`;
- `BaseFocus`;
- `ProposedFocus`.

`FunctionBodyReplacementDiff` contains:

```text
OwnerFunctionId
OldResultNodeId
NewResultNodeId
RemovedNodes
AddedNodes
```

Each `StructuralNodeSummary` exposes:

- node ID;
- semantic role name;
- resolved type where available;
- direct dependencies;
- referenced parameter/function where applicable;
- exact Float64 literal bits as canonical hexadecimal where applicable.

Removed nodes are sorted by stable-ID value. Added nodes remain in materialization order.

The requested recipe and resulting materialized structure are distinct review facts.

## Proposal and receipt

`AgentStructuralChangeProposal` is opaque. Simulation allocates the transaction ID and proposed snapshot ID system-side and stores the exact proposed snapshot plus review.

Commit checks:

1. the current snapshot ID still equals the reviewed base snapshot ID;
2. the current base fingerprint still equals the reviewed base fingerprint;
3. the stored proposed snapshot and semantic fingerprints still equal the reviewed receipt;
4. readiness satisfies the requested commit policy.

A stale or mismatched proposal rejects. The workflow does not automatically rebase or substitute another materialization behind a reviewed receipt.

## Publication and history

Publication goes through assembly-internal `SemanticWorkspace.publishValidatedSnapshot`.

That primitive:

- requires `candidate.ParentId = Some currentSnapshot.Id`;
- rejects candidate snapshot IDs already present in workspace history;
- re-runs full snapshot validation;
- publishes the exact candidate as current;
- prepends the previous current snapshot to `PreviousSnapshots`.

This primitive is trusted infrastructure and is not exposed as ordinary agent raw-snapshot publication authority.

## Readiness

The Float64 recipe itself creates no obligation.

After materialization the full validator and `SnapshotReadiness` evaluate the entire proposed snapshot. Existing unrelated open obligations may therefore keep the proposal incomplete.

Normal commit rejects blocking obligations. `commitWithPolicy ... AllowBlockingObligations` is required to publish an otherwise valid incomplete structural proposal.

## Persistence and execution

The resulting body uses existing `ParameterReference`, `Float64Literal`, and `Float64Add` semantic nodes. This operation alone therefore does not add a new language node or require a snapshot-schema version bump.

Once committed, the opaque `AgentWorkspace` remains the execution authority and is projected through the existing:

```text
AgentExecution
  -> Typed Semantic IR
  -> Execution IR
  -> C# backend
```

Generated source does not become semantic authority.

## Diagnostics

Current structured diagnostic codes include:

| Condition | Code |
| --- | --- |
| Inspected base mismatch | `StructuralExpectedBaseMismatch` |
| Target function missing | `StructuralTargetNotFound` |
| Expected result-node mismatch | `StructuralExpectedResultMismatch` |
| Target does not return Float64 | `StructuralTargetResultTypeMismatch` |
| Recipe depth > 32 | `StructuralRecipeDepthLimitExceeded` |
| Recipe nodes > 64 | `StructuralRecipeNodeLimitExceeded` |
| Parameter not owned by target | `StructuralParameterOutOfScope` |
| Parameter not Float64 | `StructuralParameterTypeMismatch` |
| Old body referenced by any obligation | `StructuralBodyReferencedByObligation` |
| Current workspace advanced before commit | `StaleBaseSnapshot` |
| Current base content differs from reviewed base | `PreviewReceiptBaseMismatch` |
| Stored proposed result differs from reviewed receipt | `PreviewReceiptResultMismatch` |
| Incomplete proposal without explicit policy | `BlockingObligations` |
| Trusted publication parent mismatch | `ValidatedSnapshotParentMismatch` |
| Trusted publication reuses a snapshot ID | `SnapshotAlreadyPublished` |

Full snapshot validation failures remain prefixed/represented through the existing validation diagnostic path.

## Current non-goals

This contract does not define:

- nested node/branch/subgraph replacement;
- expression-slot identity;
- identity-preserving local rewrites;
- existing-local-node reuse;
- general dead-node analysis/GC;
- combining structural and M1 operations atomically;
- List or higher-order structural recipes;
- generic lambdas/closures;
- implicit numeric conversions or generic numeric operators;
- branches, matches, calls, effects, capabilities, products, unions, `Option`, or `Result` recipes;
- semantic merge/rebase or multi-agent proposal composition;
- a permanent persistence-medium decision;
- a general visual programming editor.

## Evaluation boundary

The implementation contract above is current, but the controlled M2 agent-evaluation condition is not yet established.

Before a reliability claim is made, a versioned runner command exposing only this bounded request must be added, a new M2 corpus must be frozen, and controlled runs must be recorded separately from the M1 corpus/tool condition.