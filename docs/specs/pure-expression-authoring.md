# Pure expression structural authoring

**Status:** Current

This specification defines the reusable cross-domain construction surface for agent-authored pure expressions.

The purpose is to separate two representations that serve different jobs:

```text
ephemeral typed construction intent
  -> preflight type/scope validation
  -> system-side materialization
  -> persistent typed semantic graph
```

`PureExpressionRecipe` is not authoritative program state. It is a bounded tree-shaped request used to construct authoritative semantic nodes without giving the caller production `GraphNodeId` authority.

## Scope

`AgentPureExpressionStructuralWorkflow` replaces the complete body of one existing function with a body materialized from `PureExpressionRecipe`.

The maintained surface currently covers primitive scalar expressions and pure List construction/query expressions:

```text
PureExpressionRecipe =
  | Parameter(ParameterId)
  | StringLiteral(String)
  | BoolLiteral(Bool)
  | UnitLiteral
  | Int64Literal(Int64)
  | Float64Literal(Float64Bits)
  | StringIsEmpty(expr)
  | StringContains(value, substring)
  | Int64WrappingAdd(left, right)
  | Int64WrappingSubtract(left, right)
  | Int64WrappingMultiply(left, right)
  | Int64Equal(left, right)
  | Int64LessThan(left, right)
  | Int64LessThanOrEqual(left, right)
  | Int64GreaterThan(left, right)
  | Int64GreaterThanOrEqual(left, right)
  | Float64Add(left, right)
  | Float64Subtract(left, right)
  | Float64Multiply(left, right)
  | Float64Divide(left, right)
  | Float64Equal(left, right)
  | Float64LessThan(left, right)
  | Float64IsNaN(expr)
  | Float64IsFinite(expr)
  | Float64IsInfinite(expr)
  | ListEmpty(itemType)
  | ListConstruct(itemType, items)
  | ListLength(list)
  | ListAppendItem(list, item)
  | ListConcat(left, right)
```

This remains a selected pure construction algebra rather than a generic wrapper around every `SemanticNodeKind`.

## Request

```text
AgentReplaceFunctionBodyWithPureExpressionRecipe
  TargetFunctionId: FunctionId
  ExpectedBaseSnapshotId: SnapshotId
  ExpectedCurrentResultNodeId: GraphNodeId
  Replacement: PureExpressionRecipe
```

The request references existing semantic declarations by typed stable ID where needed. It does not accept caller-supplied production IDs for newly materialized nodes.

## Bounds

The construction tree uses the same practical bounds already established by bounded structural authoring:

- maximum materialized recipe nodes: **64**;
- maximum recipe depth: **32**;
- every recipe occurrence materializes one semantic node;
- materialization is left-to-right post-order;
- repeated equal subexpressions remain distinct occurrences.

`ListConstruct` materializes its item recipes in list order and then materializes the `ListConstruct` node. An empty `ListConstruct` is one recipe node at depth 1.

There is no caller-controlled CSE, DAG sharing, local-node reuse, or operand reordering.

## Preflight

Before allocating replacement graph-node IDs, simulation checks:

1. the expected base snapshot is still current;
2. the target function exists;
3. the expected current result node still matches the target body;
4. recipe depth and node count are within bounds;
5. every `Parameter` belongs to the target function;
6. every scalar operation receives operands of its required primitive type;
7. every explicit List item `TypeRef` is valid in the current snapshot and has valid builtin-generic arity;
8. every `ListConstruct` item exactly matches its declared item type;
9. `ListLength` receives `List<T>`;
10. `ListAppendItem` receives `List<T>` plus `T`;
11. `ListConcat` receives two lists of the same exact list type;
12. the inferred recipe result type exactly equals the target function result type;
13. no open or resolved obligation targets a node in the body being replaced.

There is no implicit numeric conversion, List item conversion, or fallback type coercion.

## Explicit List types

`ListEmpty` and `ListConstruct` carry an explicit item `TypeRef` because their item type cannot always be inferred from a child expression.

The supplied type is construction intent, not a raw graph payload. Before materialization it is checked using current semantic type facts:

- primitive types are valid directly;
- named types must name a current `TypeId`;
- capability types must name a current `CapabilityId`;
- `Option<T>` and `List<T>` require one type argument;
- `Result<T,E>` requires two type arguments;
- nested type arguments are validated recursively.

## Materialization

After preflight succeeds, every recipe occurrence receives a fresh `GraphNodeId` from `StableIdAllocator` and becomes the corresponding existing `SemanticNodeKind`.

The workflow adds no new persisted semantic node role and therefore does not require a snapshot-schema change.

## Review and publication

The workflow reuses the existing structural transaction mechanics:

- full proposed-snapshot validation;
- readiness classification;
- exact base/proposed snapshot and semantic fingerprints;
- base/proposed Function Focus views;
- `FunctionBodyReplacementDiff` with stable node summaries;
- stale-base and receipt-mismatch rejection;
- reviewed publication through `publishValidatedSnapshot`;
- explicit rejection without mutation.

The requested construction tree and the materialized graph remain separate review facts.

## Compatibility surfaces

The earlier `Float64ExpressionRecipe` / `AgentStructuralWorkflow` surface remains available. It is a narrower compatibility surface backed by the same structural interaction model.

The specialized ListMap workflow also remains available. General pure-expression authoring now covers List construction/query nodes, but it does not yet cover higher-order List callbacks, so the specialized workflow is not redundant yet.

## Deliberately outside the current surface

The maintained recipe does not yet author:

- `ListMap`, `ListFilter`, `ListAny`, or `ListAll`;
- products, fields, unions, `Option`, or `Result` construction;
- direct calls;
- capability invocation or other effectful behavior;
- conditional or match expressions;
- pattern bindings or `PatternReference`;
- nested-expression replacement inside an existing body;
- raw `SemanticNode`, `SemanticNodeKind`, `ValueGraph`, or `Snapshot` payloads;
- caller-supplied production graph-node IDs;
- semantic merge/rebase or multi-proposal composition.

Higher-order List expressions are the next intended extension because the existing semantic validator already defines a narrow pure unary callback contract. Branch/match/effect constructs remain separate design work because they introduce lexical scope, nested graphs, pattern identity, or effect contracts.
