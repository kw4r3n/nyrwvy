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

The maintained surface covers primitive scalar expressions, the current pure List family, and pure structured-value construction:

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
  | ListMap(source, mapperFunctionId)
  | ListFilter(source, predicateFunctionId)
  | ListAny(source, predicateFunctionId)
  | ListAll(source, predicateFunctionId)
  | ProductConstruct(typeId, fieldBindings)
  | FieldAccess(source, fieldId)
  | UnionConstruct(variantId, fieldBindings)
  | OptionNone(itemType)
  | OptionSome(value)
  | ResultOk(errorType, value)
  | ResultErr(okType, error)
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
- materialization is deterministic post-order;
- repeated equal subexpressions remain distinct occurrences.

`ListConstruct` materializes its item recipes in list order and then materializes the `ListConstruct` node. An empty `ListConstruct` is one recipe node at depth 1. Higher-order List recipes materialize the source expression first and then materialize the traversal node referencing the existing callback `FunctionId`.

Product and union field bindings are caller-supplied by stable `FieldId`, but their list order is not semantic. After preflight verifies the exact binding set, materialization evaluates field recipes in the declaration's field order and stores persistent bindings in that same order.

There is no caller-controlled CSE, DAG sharing, local-node reuse, or operand reordering.

## Preflight

Before allocating replacement graph-node IDs, simulation checks:

1. the expected base snapshot is still current;
2. the target function exists;
3. the expected current result node still matches the target body;
4. recipe depth and node count are within bounds;
5. every `Parameter` belongs to the target function;
6. every scalar operation receives operands of its required primitive type;
7. every explicit `TypeRef` is valid in the current snapshot and has valid builtin-generic arity;
8. List construction/query operands have exact compatible List/item types;
9. higher-order List callbacks satisfy the existing pure unary callback contract;
10. `ProductConstruct` names an existing product type and binds every declared field exactly once with an exact-compatible value type;
11. `FieldAccess` names a product field and its source produces the exact owning named type;
12. `UnionConstruct` names an existing variant and binds every variant field exactly once with an exact-compatible value type;
13. `OptionNone`, `ResultOk`, and `ResultErr` explicit type arguments are valid;
14. the inferred recipe result type exactly equals the target function result type;
15. no open or resolved obligation targets a node in the body being replaced.

There is no implicit numeric conversion, collection item conversion, or fallback type coercion.

## Explicit types

`ListEmpty`, `ListConstruct`, and `OptionNone` carry an explicit contained `TypeRef`; `ResultOk` carries its error type and `ResultErr` carries its success type. These facts cannot always be inferred from a child expression.

The supplied types are construction intent, not raw graph payloads. Before materialization they are checked using current semantic type facts:

- primitive types are valid directly;
- named types must name a current `TypeId`;
- capability types must name a current `CapabilityId`;
- `Option<T>` and `List<T>` require one type argument;
- `Result<T,E>` requires two type arguments;
- nested type arguments are validated recursively.

## Higher-order List callbacks

`ListMap`, `ListFilter`, `ListAny`, and `ListAll` reference an existing callback by stable `FunctionId`. They do not embed a lambda or construct a new function.

For a source `List<T>`:

- `ListMap(source, f)` requires a pure unary `f : T -> R` and produces `List<R>`;
- `ListFilter(source, p)` requires a pure unary `p : T -> Bool` and produces `List<T>`;
- `ListAny(source, p)` requires a pure unary `p : T -> Bool` and produces `Bool`;
- `ListAll(source, p)` requires a pure unary `p : T -> Bool` and produces `Bool`.

This is the same pure callback boundary enforced by the semantic validator. Effectful callbacks are rejected rather than introducing a traversal-order/effect contract through this authoring surface.

## Structured declaration bindings

`ProductConstruct` and `UnionConstruct` accept field values as `(FieldId, PureExpressionRecipe)` pairs. They deliberately do not expose persistent `FieldValueBinding`, because that type contains production `GraphNodeId` references.

Preflight requires:

- no duplicate `FieldId` bindings;
- the binding ID set to equal the declaration's field ID set exactly;
- each child recipe result type to equal the declared field type.

Caller binding order is ignored after validation. Materialization follows declaration field order, allocates all child node IDs system-side, and only then constructs ordinary persistent `FieldValueBinding` values.

`FieldAccess` similarly references an existing stable `FieldId`. Only fields owned by product types are accepted, and the source recipe must produce the exact owning `TypeRef.Named` type.

`UnionConstruct` uses the `VariantId` to determine both the owning union type and the exact field contract.

## Option and Result construction

`OptionSome(value)` derives `Option<T>` from the value recipe. `OptionNone(T)` carries explicit `T`.

`ResultOk(E, value)` derives the success type from `value` and produces `Result<T,E>`. `ResultErr(T, error)` derives the error type from `error` and produces `Result<T,E>`.

These cases add no implicit conversion and no special failure semantics; they materialize the existing semantic constructors.

## Materialization

After preflight succeeds, every recipe occurrence receives a fresh `GraphNodeId` from `StableIdAllocator` and becomes the corresponding existing `SemanticNodeKind`.

Existing declaration references such as callback `FunctionId`, product `TypeId`, union `VariantId`, and `FieldId` remain references; the workflow does not allocate or mutate those declarations.

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

The requested construction tree and the materialized graph remain separate review facts. Declaration references remain visible through ordinary semantic node summaries where applicable.

## Compatibility surfaces

The earlier `Float64ExpressionRecipe` / `AgentStructuralWorkflow` surface remains available. It is a narrower compatibility surface backed by the same structural interaction model.

The specialized `AgentListMapStructuralWorkflow` also remains available for compatibility with the M2 ListMap surface and its frozen evidence. Its semantic capability is covered by the general recipe through `ListMap(Parameter(sourceParameterId), mapperFunctionId)` plus the same target/result/callback constraints. Removing or wrapping the specialized API is a separate compatibility decision rather than part of this extension.

## Deliberately outside the current surface

The maintained recipe does not author:

- direct calls;
- capability invocation or other effectful behavior;
- conditional or match expressions;
- pattern bindings or `PatternReference`;
- nested-expression replacement inside an existing body;
- raw `SemanticNode`, `SemanticNodeKind`, `FieldValueBinding`, `ValueGraph`, or `Snapshot` payloads;
- caller-supplied production graph-node IDs;
- semantic merge/rebase or multi-proposal composition.

Direct calls remain a separate decision because they interact with argument completeness, effects, and obligations. Branch/match/effect constructs remain separate design work because they introduce lexical scope, nested graphs, pattern identity, or effect contracts.
