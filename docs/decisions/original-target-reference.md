# Original-target reference in targeted replacement

- **Status:** Accepted; implemented
- **Date:** 2026-10-24
- **Issue:** none yet

This proposal refines the [targeted expression replacement](targeted-expression-replacement.md) boundary that "the first targeted operation cannot reuse arbitrary neighboring computations from the old graph". It meets that decision's revisit condition "arbitrary existing-node reuse in a replacement" for one bounded case: reusing the target's own old expression. The [expression authoring specification](../specs/expression-authoring.md) is authoritative for `ExpressionRecipe`.

## Decision

Add one recipe kind, `{ "kind": "originalTarget" }`, accepted only in the replacement recipe of a targeted replacement (`replace` with `targetNodeId`, by ID or by name). It stands for the target node's expression as it was in the base snapshot.

- It may appear **at most once** in a replacement. It cannot be duplicated or shared.
- Its type is the target's old type. Its effects are the old target's effects, so they are already within the owning function's `RequiredEffects`.
- The replacement root keeps `TargetNodeId`, as today, so every existing consumer observes the wrapper. The old root node of the target gets a fresh system-owned ID. Every other node it brings along keeps its ID.
- The old target moves together with its **exclusively owned dependencies in its own graph**: the nodes of that graph that the current pruning plan would remove. Its nested graphs move inside it. They move to the graph position where the recipe places `originalTarget`. Shared dependencies, and exclusive ones in enclosing graphs, stay where they are and remain visible to the moved nodes.
- Nothing of the old cone is pruned, since the old expression is reused whole.
- Every other recipe consumer (whole-body replacement, function creation, field-type migration rewrites, union arm plans) rejects `originalTarget`.

Typical uses, with `T` as the old target:

- post-processing: `f(T)`, or `bind(T, g(v()))`;
- inserting a step before `T`: `bind(step, T)`, or `okOr(step, T, failure)`;
- guarding `T`: `if(cond, T, other)`.

## Reason

Real agent workflows rebuild whole function bodies to insert one step in the middle. Examples are a follow-up fold inserted before the changed files are collected, and a merge step inserted between two folds. The rebuild reconstructs every untouched neighbor through `ExpressionRecipe`. Every reference below the insertion point then needs its `scopeDepth` raised by hand, because a new binding frame sits above it. That is the main source of error in such edits.

Persistent pattern references already store a `PatternBindingId`, not a depth (`SemanticNodeKind.PatternReference`). Depth exists only in the recipe. Moving the old nodes deeper keeps every reference valid: the new position is always lexically inside the target's old position, so every binding that was visible stays visible. Only the newly written wrapper uses recipe depths, and those are shallow and fresh.

Moving the exclusive cone, rather than only the root, keeps evaluation inside the new scope. Ordering is derived from dependencies, not from node-list order. A cone left in the outer graph while only its consumer moved into a Bind body would either run before the inserted step or become `AmbiguousEffectOrder`.

At most one occurrence keeps the request tree-shaped. Duplication would need a choice between sharing the nodes (DAG construction) and copying them (duplicated effects), and neither is decided.

## Consequences

- Wrapping and inserting no longer require reconstructing untouched neighbors or adjusting their depths.
- The old target root changes ID while its children keep theirs. A durable consumer of the old root's role, currently an obligation, still blocks under the existing role-change rule, because the target ID now names the wrapper.
- Preflight infers the recipe with `originalTarget` typed as the old target type. Materialization needs one new step: relocate the cone instead of generating nodes for it. The existing frame reconstruction (`TargetedReplacementGraph.tryFindTarget`) and pruning plan (`planPruning`) are reused.
- Review lists no removed nodes. Moved nodes keep their IDs, so they are neither added nor removed; the old root under its fresh ID is listed as added.
- Per-use edits, reuse of nodes outside the target's cone, and general DAG authoring remain separate questions.

## Revisit conditions

Revisit when a concrete workflow needs the old target more than once, needs a node outside the target's exclusive cone reused at a new position, needs a moved node to keep the target's old root ID, or needs `originalTarget` outside targeted replacement.
