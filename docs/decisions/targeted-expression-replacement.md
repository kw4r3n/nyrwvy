# Targeted expression replacement

- **Status:** Accepted; implemented (issue #148). The current contract is in the [expression authoring specification](../specs/expression-authoring.md) and the [agent authoring session](../specs/agent-authoring-session.md).
- **Date:** 2026-09-08
- **Issue:** [#145](https://github.com/kw4r3n/nyrwvy-lang/issues/145)

This decision extends the structural-authoring direction beyond the whole-function-body boundary established for Milestone 2. It defines a typed targeted edit, not arbitrary graph mutation. The maintained [expression authoring specification](../specs/expression-authoring.md) is authoritative for `ExpressionRecipe` itself.

## Decision

Add one targeted expression-replacement operation whose semantic target is an existing `GraphNodeId` owned by an existing `FunctionId`.

The operation replaces the meaning of that node for **all of its uses** while preserving the target node's `GraphNodeId`. The replacement root therefore keeps the existing semantic identity. Every other replacement node and every replacement-created pattern binding receives a fresh system-owned ID. A per-use/edge rewrite is a separate future operation.

The request uses the current `ExpressionRecipe` and remains tree-shaped. It may reference target-function parameters, existing typed declarations, and lexical pattern slots visible at the target location. It does not gain arbitrary references to other existing graph nodes, caller-controlled DAG sharing, or production-ID construction.

Targeted replacement is valid in the function's top-level body graph and inside nested `Bind`, `Conditional`, `MatchUnion`, `MatchOption`, and `MatchResult` child graphs.

## Why the target ID survives

`GraphNodeId` is already the stable identity used by node-oriented queries, uses/dependencies, obligations, review facts, and semantic references. The requested operation means "change this semantic expression", not "delete one expression and redirect one particular incoming edge".

Keeping the target root ID has three useful consequences:

- every existing consumer observes the replacement without a separate rewiring authority;
- replacing a graph result does not require changing its containing `ValueGraph.ResultNodeId`;
- shared targets naturally implement the accepted all-uses behavior.

Fresh IDs remain appropriate for newly introduced child expressions because they are new semantic objects. Whole-body replacement remains different: it intentionally replaces the old local body identities as one coarse operation.

An obligation targeting the replacement root still blocks this operation, even though the root ID survives. Current obligations encode facts about a direct-call node and parameter; preserving only the ID would not prove that the new node still satisfies that obligation's meaning.

## Target and stale contract

The first request needs these semantic facts:

```text
TargetFunctionId
ExpectedBaseSnapshotId
TargetNodeId
Replacement: ExpressionRecipe
```

No additional expected-result-node or target fingerprint is required. The exact base snapshot already binds the request to the target's complete prior meaning; simulation rejects a missing/non-owned target, and reviewed publication retains the existing exact receipt/stale checks.

The operation remains one typed semantic request. The caller never supplies new `GraphNodeId`, `PatternBindingId`, snapshot, or raw `SemanticNode`/`ValueGraph` payloads.

## Nested lexical context

A replacement inside an existing nested graph is checked in the lexical context of that exact location.

The implementation reconstructs the unique structural path from the owning function body to the `ValueGraph` that directly owns `TargetNodeId`. `GraphNodeId` uniqueness makes the target location unambiguous.

The initial recipe frame stack is the enclosing persistent lexical frames, ordered innermost first. Existing `RecipePatternReference` semantics are reused:

- entering a `Bind` body contributes one `Value` frame;
- a union arm contributes its declared field slots;
- `Option.Some` contributes `Value`;
- `Option.None` contributes an **empty frame** and still consumes one `ScopeDepth`;
- `Result.Ok` contributes `Value`;
- `Result.Err` contributes `Error`;
- `Conditional` branches add no binding frame.

If the replacement recipe creates another Bind/match scope, its new frame is pushed in front of these reconstructed outer frames exactly as in current whole-body recipe materialization.

Persistent `PatternBindingId` values are never exposed in the request. Preflight derives slot types from the enclosing graph and materialization resolves accepted recipe slots to the existing persistent bindings internally.

A target in a control-flow condition/input is in the parent graph and does not see the child arm's bindings.

## Exact type and effects

Preflight infers the current target node type in its lexical context and requires the replacement root to produce exactly that `TypeRef`.

The replacement is checked against the owning function's existing `RequiredEffects` with the same rules as current `ExpressionRecipe` authoring. The operation never adds or removes effects implicitly.

`SemanticallyReady` remains distinct from execution readiness. Existing execution diagnostics such as `AmbiguousEffectOrder` continue to apply after replacement; target editing does not derive hidden ordering from node-list order, allocation, or traversal.

## Locally bounded pruning

Current semantic validation requires graph results and references to be valid and lexically visible, but it does not require every stored node to be reachable from a function result. Targeted replacement therefore needs an explicit cleanup rule so removed computations, including effects, do not survive merely as detached nodes.

The operation performs **replacement-local pruning**, not general graph GC:

1. preserve `TargetNodeId` as the replacement root;
2. remove nested child graphs structurally owned by the old target node when its old node kind is replaced;
3. consider the old target's former value-dependency cone as cleanup candidates;
4. after replacing the root, recursively remove a candidate only when it has no remaining semantic use and is not the result node of a retained `ValueGraph`;
5. when a removed control node owns nested graphs, those nested nodes are removed with it and their former outer dependencies may become candidates;
6. preserve any dependency still used by a node outside the removed set;
7. do not inspect or delete unrelated pre-existing detached nodes outside the old target dependency cone.

This makes replacing `oldEffectfulComputation()` with another expression remove the old effect when nothing else uses it, while preserving a shared producer that another consumer still needs.

The implementation may phrase this as a local liveness/reference-count walk rather than a generic reachability engine. The observable contract is limited to facts made obsolete by this replacement.

## Obligations

Simulation rejects when any open **or resolved** obligation targets:

- `TargetNodeId`; or
- an old node that replacement-local pruning would remove.

An obligation on a shared old dependency does not block if that dependency remains because another consumer still uses it.

This preserves the existing conservative rule used by whole-body replacement without invalidating unrelated durable obligation history.

## Review

Requested intent and materialized result remain separate review facts.

The targeted review must identify at least:

- owner function and retained target node ID;
- requested `ExpressionRecipe`;
- exact prior target role/type;
- removed old nodes, including recursively removed nested nodes;
- fresh added nodes and bindings;
- existing dependency nodes retained because they remain shared;
- the target's affected direct consumers/uses;
- base and proposed Function Focus;
- receipt/readiness information.

The review does not expose a raw mutation command merely because it contains structural facts.

## Implementation boundary

The first implementation should reuse the existing expression inference/materialization engine by allowing an initial lexical-frame context and by assigning the already-owned target ID to the materialized root. Do not duplicate the expression algebra.

Focused fast verification should cover:

- top-level targeted replacement;
- a shared target changing all consumers while retaining its ID;
- one nested lexical replacement that resolves an existing outer slot;
- an invalid lexical slot/depth rejection;
- exact result-type mismatch rejection;
- local pruning of an exclusively used old dependency while preserving a shared dependency;
- obligation protection;
- review facts and stale publication.

Reuse existing persistence/lowering/backend evidence when serialized semantic shapes are unchanged. No new model corpus or routine extended/live CI is authorized by this decision alone.

## Consequences

- Existing node IDs become an ordinary **targeting** authority but not an identity-construction authority.
- A target node can change semantic role while retaining identity; durable consumers whose meaning depends on the old role, currently obligations, must explicitly block rather than being reinterpreted.
- The first targeted operation cannot reuse arbitrary neighboring computations from the old graph. The agent can reconstruct them through `ExpressionRecipe`, use function parameters/declarations, or use visible lexical slots.
- Targeted local pruning becomes part of this operation's semantics but does not make global reachability a snapshot invariant.
- Per-edge edits, identity-preserving child rewrites, and general DAG authoring remain separate questions.

## Revisit conditions

Revisit this boundary when a concrete workflow needs only one use of a shared node changed, arbitrary existing-node reuse in a replacement, explicit expression-slot identity, global dead-node cleanup, general CSE/DAG construction, or an obligation type that can safely survive a role-changing replacement.

Refs [#42](https://github.com/kw4r3n/nyrwvy-lang/issues/42), [#44](https://github.com/kw4r3n/nyrwvy-lang/issues/44), [#114](https://github.com/kw4r3n/nyrwvy-lang/issues/114), [#121](https://github.com/kw4r3n/nyrwvy-lang/issues/121), [#141](https://github.com/kw4r3n/nyrwvy-lang/issues/141).