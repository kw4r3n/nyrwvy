# Bounded structural editing — proposed M2 Float64 body-replacement contract

**Status:** Proposed

This specification defines the candidate normative contract for the first Milestone 2 structural operation. It is not current implemented behavior. The corresponding rationale is in the [Milestone 2 proposed decisions](../decisions/milestone-2.md).

Normative terms such as **must**, **must not**, **should**, and **may** apply only if this proposal is accepted.

## Scope

The first structural operation replaces the complete `ValueGraph` body of one existing function with a body materialized from a closed Float64 recipe.

The operation exists to test bounded structural authoring through the established NyamNyam workflow:

```text
bounded query
  -> typed body-replacement intent
  -> precondition and recipe validation
  -> system-side graph-node allocation
  -> transaction simulation
  -> structural Change Review
  -> exact reviewed commit or reject
  -> exact committed-state lowering and execution
```

It does not create a general graph builder, expose mutable snapshot state, or define arbitrary subgraph editing.

## Authority boundary

The ordinary agent-facing request must contain only:

- existing typed identities returned by bounded queries;
- an exact inspected-base expectation;
- an exact current-result expectation; and
- a closed recipe from the cases defined below.

It must not accept:

- a raw `Snapshot`;
- a raw `SemanticFunction`, `ValueGraph`, `SemanticNode`, or `SemanticNodeKind` payload;
- a caller-supplied production `GraphNodeId`, `SnapshotId`, or `TransactionId` for newly created objects;
- a JSON, Nyason, generated-C#, or other serialized program patch;
- a callback that can manufacture arbitrary nodes;
- a privileged fixture or stale-state perturbation command.

The system remains responsible for materialization, identity allocation, validation, diff production, readiness evaluation, proposal binding, and publication.

## Logical request model

The logical request has these fields:

```text
ReplaceFunctionBodyWithFloat64Recipe
  targetFunctionId: FunctionId
  expectedBaseSnapshotId: SnapshotId
  expectedCurrentResultNodeId: GraphNodeId
  replacement: Float64ExpressionRecipe
```

The implementation may choose idiomatic F# names, but it must preserve these semantic facts separately.

`expectedBaseSnapshotId` is the snapshot observed by the agent's bounded query. It is an early query-to-simulation guard. It does not replace the `TransactionRequest.BaseSnapshotId`, preview receipt, or exact reviewed-publication checks.

`expectedCurrentResultNodeId` identifies the result node of the complete body the agent intends to replace. It is a target-local guard and must be displayed in requested intent.

## Closed Float64 recipe

The first recipe algebra is exactly:

```text
Float64ExpressionRecipe =
  | Parameter(parameterId: ParameterId)
  | Literal(bits: Float64Bits)
  | Add(left: Float64ExpressionRecipe, right: Float64ExpressionRecipe)
```

The cases mean:

- `Parameter` materializes `SemanticNodeKind.ParameterReference`;
- `Literal` materializes `SemanticNodeKind.Float64Literal`;
- `Add` materializes both operands and then `SemanticNodeKind.Float64Add` referencing their allocated node IDs.

No other primitive, conversion, comparison, classification, call, capability, branch, match, collection, product, union, `Option`, or `Result` recipe is part of this version.

## Recipe bounds

Before allocating body-node identities, the system must compute recipe size and depth.

- Maximum materialized node count: **64**.
- Maximum recipe depth: **32**.
- Every recipe occurrence counts as one node.
- A leaf has depth 1.
- `Add(left, right)` has depth `1 + max(depth(left), depth(right))`.

Requests over either bound must be rejected. These initial limits are conservative protocol limits, not language-semantic limits. Revising them is an interface change and must be covered by tests and evaluation metadata.

## Simulation preconditions

The structural request must be the only operation in its transaction.

Simulation must reject before body-node allocation unless all of the following hold:

1. the current workspace snapshot ID equals `expectedBaseSnapshotId`;
2. `targetFunctionId` resolves to exactly one current function;
3. the function's current `Body.ResultNodeId` equals `expectedCurrentResultNodeId`;
4. the target function's declared result type is `Primitive Float64`;
5. the recipe is within the node-count and depth limits;
6. every `Parameter` leaf resolves to a parameter owned by the target function;
7. every referenced parameter has type `Primitive Float64`;
8. no durable obligation, open or resolved, targets any node contained in the old function body.

The request cannot refer to another function's parameter even when its type is compatible. It cannot refer directly to an existing local graph node.

The operation does not infer a conversion, select a parameter by display name, or fall back to a similarly shaped function when a precondition fails.

## Recommended validation order

Implementations should reject in this order so diagnostics remain predictable and no new body IDs are allocated for a request whose bounded target is already invalid:

1. structural-operation exclusivity;
2. expected-base match;
3. target-function existence;
4. expected-result match;
5. obligation references to the old body;
6. target result type;
7. recipe size and depth;
8. parameter ownership and type.

Full snapshot validation after materialization remains the normative correctness oracle. Passing preflight checks does not bypass or redefine full validation.

## Materialization

After all preflight checks pass, the engine materializes the recipe in **left-to-right post-order**.

For each recipe occurrence it must:

1. materialize child occurrences first, left before right;
2. allocate one fresh production `GraphNodeId` through the system allocator;
3. construct the corresponding existing `SemanticNodeKind`; and
4. append the node to the new body's direct `Nodes` list.

The root occurrence's allocated ID becomes the new `ValueGraph.ResultNodeId`.

Every occurrence receives a distinct node identity. In particular:

```text
Add(Parameter(p), Parameter(p))
```

creates two distinct `ParameterReference` nodes that both refer to the same existing `ParameterId`, followed by one `Float64Add` node.

The implementation must not perform common-subexpression elimination, local-node reuse, algebraic simplification, constant folding, operand reordering, or literal rewriting during this semantic operation.

`Float64Bits` remains authoritative. Runner or transport text must use the existing exact hexadecimal parsing/canonicalization contract, including canonical semantic NaN and preservation of non-NaN bits such as signed zero.

## Snapshot transition

The proposed snapshot must differ from the base snapshot only as follows:

- the target function's `Body` is replaced with the newly materialized `ValueGraph`;
- ordinary transaction/snapshot identity and parent facts change according to the existing publication workflow.

The following target-function facts must remain unchanged:

- `FunctionId`;
- display name;
- ordered parameter list, including every `ParameterId`, display name, and type;
- declared result type;
- required effects.

All other functions, types, effects, capabilities, and obligations must remain byte-for-byte/structurally equal to their base semantic facts.

Every graph node contained in the old target body is absent from the proposed snapshot. No old body-node identity is retained merely because a new node has the same kind or references the same parameter.

Because replacement is complete, this operation does not define retention or garbage collection for detached old-body structure: there is no detached old-body structure in the resulting target function.

## Obligation behavior

Let `OldBodyNodeIds` be the IDs returned by recursively collecting every node in the target's current body.

If any current obligation has `CallNodeId` in `OldBodyNodeIds`, simulation must reject, regardless of whether the obligation is `Open` or `Resolved`.

The operation must not:

- remove such an obligation;
- mark it resolved;
- retarget it to a new call;
- create a replacement obligation;
- treat a resolved obligation as disposable history; or
- invent an `Invalidated` state.

Obligations unrelated to the target body remain unchanged. Their presence continues to affect ordinary snapshot readiness and commit policy exactly as before.

## Structural semantic diff

`SemanticDiff` must gain an explicit body-replacement fact rather than relying on snapshot comparison outside the transaction engine.

The logical diff record must preserve at least:

```text
FunctionBodyReplacement
  ownerFunctionId: FunctionId
  oldResultNodeId: GraphNodeId
  newResultNodeId: GraphNodeId
  removedNodes: StructuralNodeReview[]
  addedNodes: StructuralNodeReview[]
```

Each `StructuralNodeReview` must expose enough read-only semantic information to understand that node without generated code:

- node ID;
- semantic role;
- resolved node type where available;
- role-specific references, including parameter ID, exact literal bits, or operand node IDs.

`removedNodes` must include every node recursively owned by the old body. `addedNodes` must include every materialized recipe node.

For deterministic artifacts:

- removed-node summaries should be ordered by stable-ID value;
- added-node summaries must follow materialization order;
- there must be exactly one body-replacement diff item for the transaction.

The diff is resulting engine-owned evidence. The agent-authored recursive recipe remains preserved separately in `RequestedOperations`.

## Change Review projection

The target function must appear in `AffectedFunctionIds`.

The review must expose both:

- a bounded base-state Focus view for the target function; and
- a bounded proposed-state Focus view for the target function.

The implementation may retain the current `FunctionFocus` field as the proposed/after view for compatibility and add a separate base/before projection. Whatever representation is chosen, reviewers must be able to inspect the complete old and new bodies, their node types, and their referenced declarations without reconstructing the old body from prose or counts.

The review must continue to bind:

- requested intent;
- resulting semantic diff;
- exact base and proposed snapshot IDs/fingerprints through the existing receipt;
- readiness;
- affected Focus slices.

A read-only base/proposed structural projection is not mutation authority and must not add a raw snapshot-replacement command to the ordinary agent API.

## Readiness and publication

The Float64 recipe itself is complete-only and creates no obligation.

After materialization, the ordinary full validator and readiness evaluator run over the entire proposed snapshot. Any validation issue rejects simulation as under the existing transaction contract.

Existing unrelated open obligations may leave the proposed snapshot incomplete. Publication remains governed by the existing normal versus explicit-incomplete commit policy; this operation does not weaken that distinction.

The representative M2 execution scenario must start and end semantically complete.

Commit and rejection use the existing opaque `AgentChangeProposal` and exact preview receipt. If the workspace advances after simulation, committing the old proposal must fail through the current stale-proposal checks. The system must not rebase, re-simulate, or substitute another structural result behind an already reviewed receipt.

## Persistence and fingerprints

The resulting snapshot uses only existing semantic node kinds. The operation therefore does not require a new language node or a snapshot-schema change solely because the body was authored through a new API.

Implementation verification must nevertheless prove that the proposed and committed snapshots:

- round-trip through the strict JSON reference codec;
- round-trip through every maintained applicable snapshot codec;
- preserve exact `Float64Bits`;
- produce the expected changed snapshot and semantic fingerprints;
- preserve unrelated semantic facts across round-trip.

If operation history, structural diff, or review artifacts are serialized durably, their schema/version change must be explicit under the [schema-evolution policy](schema-evolution-policy.md).

## Runner and evaluation protocol

A runner command for this operation must accept only:

- target function ID;
- expected base snapshot ID;
- expected current result-node ID;
- the closed recursive recipe;
- exact Float64 hexadecimal literal text where transport text is required.

It must not accept new graph-node IDs from the evaluated agent.

Adding the command creates a new tool/protocol condition. It must not retroactively change `m1-agent-corpus-v1`, M1 run records, or their tool revision.

Before recorded model evaluation, freeze a new structural-edit corpus whose task oracles inspect the exact committed snapshot and exact execution result rather than relying on agent prose.

## Diagnostics

The implementation must return structured diagnostics for at least these distinct conditions. Exact final code names may be refined before acceptance, but the distinctions must not collapse into one generic failure:

| Condition | Proposed diagnostic code |
| --- | --- |
| Structural operation combined with another operation | `StructuralOperationMustBeExclusive` |
| Inspected base no longer current | `StructuralExpectedBaseMismatch` |
| Target function missing | existing `TargetNotFound` or a documented equivalent |
| Current body result differs from expectation | `StructuralExpectedResultMismatch` |
| Target result type is not Float64 | `StructuralTargetTypeMismatch` |
| Recipe exceeds node limit | `StructuralRecipeTooLarge` |
| Recipe exceeds depth limit | `StructuralRecipeTooDeep` |
| Parameter not owned by target | `StructuralParameterOutOfScope` |
| Parameter is not Float64 | `StructuralParameterTypeMismatch` |
| Old body is referenced by an obligation | `StructuralTargetHasObligation` |

Messages must identify the relevant existing stable target but must not expose a raw snapshot mutation mechanism.

## Required implementation verification

A later implementation issue must include automated coverage for at least:

1. successful parameter-plus-literal `Float64Add` body replacement;
2. fresh system-issued node IDs and disjoint old/new body-node sets;
3. preservation of function, parameter, caller, declaration, and unrelated node identities;
4. exact negative-zero and canonical-NaN literal handling;
5. expected-base mismatch;
6. expected-result mismatch;
7. unknown target function;
8. non-Float64 target function;
9. foreign parameter and non-Float64 parameter rejection;
10. node-count and depth limits;
11. rejection for both open and resolved obligations targeting the old body;
12. rejection of mixed or multiple operations;
13. structural diff completeness and deterministic ordering;
14. base/proposed Focus availability in Change Review;
15. stale reviewed-proposal rejection after an intervening publication;
16. strict snapshot-codec round-trip and fingerprint behavior;
17. Typed Semantic IR projection, Execution IR lowering, C# emission/build, and behavioral execution from the exact committed workspace;
18. absence of unrelated semantic churn;
19. inability of the ordinary agent or runner command to supply production IDs for new nodes or raw graph/snapshot payloads.

## Explicit non-goals

This version does not define:

- replacement of one nested node or branch;
- identity-preserving local rewrites;
- reuse of an existing local graph node;
- dead-node analysis or general graph garbage collection;
- multiple structural operations in one transaction;
- interaction ordering with rename, signature evolution, or obligation resolution;
- construction of `ListMap`, `ListFilter`, or another collection node;
- generic lambdas or closures;
- implicit numeric conversion, promotion, or generic numeric operators;
- effects, capability invocation, branching, matching, calls, products, unions, `Option`, or `Result` recipes;
- semantic merge, automatic rebase, or multi-agent proposal composition;
- a permanent persistence medium;
- a conventional source syntax or general visual editor.
