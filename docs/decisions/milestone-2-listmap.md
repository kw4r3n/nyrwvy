# Milestone 2 decision — specialized ListMap structural slice

**Status:** Accepted

This decision records the post-evaluation outcome of Issue #52. It extends Milestone 2 with one additional, deliberately shallow structural role without changing the completed Float64 M2.1/M2.2 claim.

Evidence basis: [M2 controlled structural agent evaluation](../verification/m2-controlled-structural-agent-evaluation-20260831.md).

## M2-D08 — Add one shallow specialized ListMap complete-body replacement

- **Status:** Accepted
- **Date:** 2026-08-31

### Decision

Milestone 2 will add one specialized complete-body ListMap operation logically equivalent to:

```text
AgentReplaceFunctionBodyWithListMap
  TargetFunctionId
  ExpectedBaseSnapshotId
  ExpectedCurrentResultNodeId
  SourceParameterId
  MapperFunctionId
```

The operation replaces the complete body of one existing function whose result type is `List<R>` with a shallow structural form equivalent to:

```text
ListMap(
  ParameterReference(SourceParameterId),
  MapperFunctionId
)
```

The source is restricted to one existing parameter owned by the target function. The mapper is restricted to one existing stable `FunctionId`. This slice does not introduce a recursive List recipe language.

### Type and effect contract

For a request to be eligible:

- the target result must be `List<R>`;
- `SourceParameterId` must identify a parameter owned by the target;
- the source parameter must have type `List<T>`;
- `MapperFunctionId` must identify an existing function compatible with the current ListMap callback contract from `T` to `R`;
- the mapper must satisfy the current pure-callback restriction;
- unknown, foreign, non-List, input-incompatible, result-incompatible, or effectful references reject before materialization with structured diagnostics.

This decision does not change the language-level semantics of ListMap or callback purity. It adds only a bounded agent-authoring operation over already-supported semantic nodes.

### Identity and authority contract

The caller supplies only stable IDs for existing semantic entities. It does not supply production graph-node IDs.

After preflight succeeds, the system materializes exactly the required fresh body nodes, conceptually:

1. `ParameterReference(SourceParameterId)`;
2. `ListMap(sourceNodeId, MapperFunctionId)`.

Every new `GraphNodeId` is allocated system-side. Old and new body-node IDs remain disjoint. No raw `Snapshot`, `ValueGraph`, `SemanticNode`, storage patch, generated source, generic graph constructor, or generic call constructor is exposed.

### Review and publication contract

The ListMap slice reuses the established structural invariants:

- exact expected-base `SnapshotId` guard;
- exact expected current result-node guard;
- conservative rejection when open or resolved durable obligations target the old body;
- requested stable semantic IDs kept distinct from materialized graph-node identities;
- complete before/after structural review;
- exact reviewed fingerprint binding;
- stale reviewed publication fails closed;
- exact committed-state projection through Typed Semantic IR -> Execution IR -> C#.

A dedicated request/review/proposal type is preferred where it keeps this authority boundary clearer than widening `Float64ExpressionRecipe` into an unrelated generic expression union.

### Reason

The completed Float64 controlled tranche strongly supports the core structural workflow:

- 29/30 frozen machine-oracle passes;
- 12/12 committed-state execution passes;
- 30/30 zero unrelated semantic churn;
- reliable stable-ID authority, review, guard recovery, and conservative semantic rejection.

The systematic weakness was exact construction of very deep recursive explicit JSON recipes, especially the depth-boundary task tracked in Issue #73. Therefore the next structural proof target should not be a larger recursive expression language.

A shallow ListMap operation provides materially new evidence instead:

- stable callback identity by `FunctionId`;
- generic List source/result compatibility;
- callback signature validation;
- pure/effectful callback enforcement;
- higher-order lowering/execution.

These are semantically different from Float64 arithmetic while remaining locally inspectable and bounded.

### Consequences

- M2.1/M2.2 Float64 claims remain complete independently;
- M2.3 implementation is tracked by Issue #76;
- a new List-specific frozen corpus/condition is required and tracked by Issue #77;
- the existing `m2-agent-corpus-v1` is not extended or reinterpreted;
- ListFilter, ListAny, ListAll, arbitrary nested List sources, generic lambdas/closures, and effectful callbacks remain outside this slice;
- this decision must not evolve implicitly into a general-purpose expression or graph-building API.

### Evaluation requirements

The ListMap corpus must separately cover at least:

- successful reviewed ListMap replacement and exact execution;
- materialized role/order and system-owned identity;
- foreign source parameter;
- non-List source parameter;
- unknown mapper;
- mapper input mismatch;
- mapper result mismatch;
- effectful mapper rejection;
- obligation-protected body rejection;
- stale reviewed proposal recovery;
- zero unrelated semantic churn.

The corpus must be frozen before model observation under a separately pinned condition. Results from the Float64 corpus are evidence motivating this slice, not substitutes for ListMap evaluation.

### Revisit conditions

Revisit the shape only after the specialized ListMap implementation/evaluation provides concrete evidence that another role is useful. Do not generalize from this decision alone to recursive List recipes, arbitrary calls, lambdas, closures, or unrestricted graph construction.
