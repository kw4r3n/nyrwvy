# Bounded structural editing — Float64 body replacement

**Status:** Current

This specification defines the maintained Float64 structural body-replacement contract. The original Milestone 2 rationale is recorded in the [Milestone 2 decisions](../decisions/milestone-2.md), implementation evidence is recorded in [M2 Float64 structural-edit verification](../verification/m2-float64-structural-editing-20260830.md), and the completed controlled evaluation remains documented by the [Milestone 2 record](../plans/milestone-2.md).

The initial M2 experiment exposed only `Parameter` / `Literal` / `Add`. The maintained recipe now also exposes the already-implemented Float64 `Subtract`, `Multiply`, and `Divide` semantic roles through the same typed interaction model.

## Scope

The current structural operation replaces the complete body of one existing `Float64`-returning function with a body materialized from a typed closed recipe.

The lifecycle is:

```text
AgentWorkspace state
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

The operation is implemented by `AgentStructuralWorkflow` and remains separate from the M1 `AgentWorkflow`, `AgentOperationIntent`, `SemanticOperation`, and `SemanticDiff` surfaces because complete-body construction has a different request and review shape.

The structural workflow shares the opaque `AgentWorkspace`. Trusted access to the underlying semantic workspace/snapshot is assembly-internal.

A structural proposal represents exactly one body replacement. There is no current operation list that can combine it atomically with rename, signature evolution, obligation resolution, or another body replacement.

## Request

```text
AgentReplaceFunctionBodyWithFloat64Recipe
  TargetFunctionId: FunctionId
  ExpectedBaseSnapshotId: SnapshotId
  ExpectedCurrentResultNodeId: GraphNodeId
  Replacement: Float64ExpressionRecipe
```

The maintained recipe algebra is:

```text
Float64ExpressionRecipe =
  | Parameter(ParameterId)
  | Literal(Float64Bits)
  | Add(Float64ExpressionRecipe, Float64ExpressionRecipe)
  | Subtract(Float64ExpressionRecipe, Float64ExpressionRecipe)
  | Multiply(Float64ExpressionRecipe, Float64ExpressionRecipe)
  | Divide(Float64ExpressionRecipe, Float64ExpressionRecipe)
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
- every binary arithmetic recipe has depth `1 + max(depth(left), depth(right))`.

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
- `Subtract(left,right)` emits `Float64Subtract(leftResult,rightResult)` with the same ordering and allocation rules.
- `Multiply(left,right)` emits `Float64Multiply(leftResult,rightResult)` with the same ordering and allocation rules.
- `Divide(left,right)` emits `Float64Divide(leftResult,rightResult)` with the same ordering and allocation rules.

Every recipe occurrence gets a distinct `GraphNodeId` from `StableIdAllocator`.

The operation performs no CSE, local-node reuse, constant folding, algebraic simplification, operand reordering, division-specific fallback, or implicit conversion.

`Float64Bits` remains authoritative, including canonical semantic NaN and preservation of every non-NaN binary64 bit pattern. IEEE 754 infinity, NaN, and signed-zero results remain ordinary Float64 semantics rather than structural diagnostics.

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

Its `TransactionPreviewReceipt` exposes the same six read-only identity/fingerprint facts as ordinary transaction review, but the structural intent is bound by the opaque proposal rather than a `TransactionRequest`. Such a snapshot-only receipt cannot authorize publication through low-level `SemanticWorkspace.commitReviewed`, even if a supplied operation sequence produces the same snapshot. See the [low-level receipt contract and in-process API migration](../developer/architecture.md#low-level-reviewed-receipts). Wire receipt projections and frozen evaluation records are unchanged.

Commit checks:

1. the current snapshot ID still equals the reviewed base snapshot ID;
2. the current base fingerprint still equals the reviewed base fingerprint;
3. the stored proposed snapshot and semantic fingerprints still equal the reviewed receipt;
4. readiness satisfies the requested commit policy.

A stale or mismatched proposal rejects. The workflow does not automatically rebase or substitute another materialization behind a reviewed receipt.

The maintained agent-evaluation session retires a process-local proposal handle after an unambiguously terminal commit rejection such as `StaleBaseSnapshot` or a receipt-content mismatch. This is orchestration cleanup, not semantic rejection authority and not an agent `reject` action.

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

The resulting body uses existing `ParameterReference`, `Float64Literal`, `Float64Add`, `Float64Subtract`, `Float64Multiply`, and `Float64Divide` semantic nodes. This extension adds no new language node and requires no snapshot-schema version bump.

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
- a general List or higher-order expression recipe (the specialized ListMap workflow is a separate maintained surface);
- generic lambdas/closures;
- implicit numeric conversions or generic numeric operators;
- branches, matches, calls, effects, capabilities, products, unions, `Option`, or `Result` recipes;
- semantic merge/rebase or multi-agent proposal composition;
- a permanent persistence-medium decision;
- a general visual programming editor.

These are current scope boundaries, not evidence that each future semantic role requires a new dedicated protocol.

## Evaluation evidence

The original bounded Float64 structural interaction was validated by the completed Milestone 2 controlled evaluation. The frozen evaluation condition exercised the initial `Parameter` / `Literal` / `Add` surface and remains historical evidence exactly as recorded.

Adding `Subtract`, `Multiply`, and `Divide` reuses the same target guards, system-owned identity allocation, materialization ordering, review binding, stale handling, publication, and downstream execution path. It therefore extends an already-evidenced interaction model rather than introducing a new experimental claim that requires another controlled model tranche.

See the maintained [verification philosophy](../developer/verification.md) for when a later structural-authoring change should trigger milestone or experimental validation.
