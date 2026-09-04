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

`PureExpressionRecipe` is not authoritative program state. It is a bounded tree-shaped request used to construct authoritative semantic nodes without giving the caller production `GraphNodeId` or `PatternBindingId` authority.

## Scope

`AgentPureExpressionStructuralWorkflow` replaces the complete body of one existing function with a body materialized from `PureExpressionRecipe`.

The maintained surface covers primitive scalar expressions, the current pure List family, pure structured-value construction, complete calls to existing pure functions, and scoped pure control flow:

```text
RecipePatternSlot =
  | Field(FieldId)
  | Value
  | Error

RecipePatternReference =
  ScopeDepth: non-negative integer
  Slot: RecipePatternSlot

PureExpressionRecipe =
  | Parameter(ParameterId)
  | PatternReference(RecipePatternReference)
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
  | DirectCall(targetFunctionId, argumentBindings)
  | Conditional(condition, whenTrue, whenFalse)
  | MatchUnion(input, arms)
  | MatchOption(input, someBody, noneBody)
  | MatchResult(input, okBody, errBody)
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

The request references existing semantic declarations by typed stable ID where needed. It does not accept caller-supplied production IDs for newly materialized graph nodes or pattern bindings.

## Bounds

The complete construction tree uses the same practical bounds already established by bounded structural authoring:

- maximum recipe nodes: **64**;
- maximum recipe depth: **32**;
- condition/input expressions and every nested branch body count toward the same bounds;
- every recipe occurrence materializes one semantic node;
- repeated equal subexpressions remain distinct occurrences.

`ListConstruct` materializes its item recipes in list order and then materializes the `ListConstruct` node. Higher-order List recipes materialize the source expression first and then the traversal node referencing the existing callback `FunctionId`.

Product and union field bindings are caller-supplied by stable `FieldId`, but their list order is not semantic. After preflight verifies the exact binding set, materialization evaluates field recipes in declaration order and stores persistent bindings in that same order.

`DirectCall` arguments are caller-supplied by the callee's stable `ParameterId`, but argument-list order is likewise not semantic. After exact binding validation, argument recipes materialize in callee parameter declaration order and the call node is created last.

Control-flow condition/input expressions materialize in the current graph. Branch recipes materialize as child `ValueGraph`s. Union match arms and their pattern-field bindings are canonicalized to declaration order.

There is no caller-controlled CSE, DAG sharing, local-node reuse, let-binding, or operand reordering.

## Preflight

Before allocating replacement graph-node or pattern-binding IDs, simulation checks:

1. the expected base snapshot is still current;
2. the target function exists;
3. the expected current result node still matches the target body;
4. recipe depth and node count are within bounds across all nested branches;
5. every `Parameter` belongs to the target function;
6. every `PatternReference` resolves to a visible lexical match-arm frame and slot;
7. every scalar operation receives operands of its required primitive type;
8. every explicit `TypeRef` is valid in the current snapshot and has valid builtin-generic arity;
9. List construction/query operands have exact compatible List/item types;
10. higher-order List callbacks satisfy the existing pure unary callback contract;
11. `ProductConstruct` names an existing product type and binds every declared field exactly once with an exact-compatible value type;
12. `FieldAccess` names a product field and its source produces the exact owning named type;
13. `UnionConstruct` names an existing variant and binds every variant field exactly once with an exact-compatible value type;
14. `OptionNone`, `ResultOk`, and `ResultErr` explicit type arguments are valid;
15. `DirectCall` names an existing function whose `RequiredEffects` is empty;
16. every `DirectCall` callee parameter is bound exactly once by its stable `ParameterId`, with no unknown or omitted parameter;
17. every `DirectCall` argument recipe exactly matches the declared callee parameter type;
18. every `Conditional` condition produces `Bool` and both branches produce exactly the same type;
19. every match input has the required union/`Option<T>`/`Result<T,E>` shape;
20. `MatchUnion` contains every declared variant exactly once, with no unknown or duplicate variants;
21. all branches of one match produce exactly the same type;
22. the inferred recipe result type exactly equals the target function result type;
23. no open or resolved obligation targets a node in the body being replaced.

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

Effectful callbacks are rejected rather than introducing a traversal-order/effect contract through this surface.

## Structured declaration bindings

`ProductConstruct` and `UnionConstruct` accept field values as `(FieldId, PureExpressionRecipe)` pairs. They deliberately do not expose persistent `FieldValueBinding`, because that type contains production `GraphNodeId` references.

Preflight requires no duplicate fields, the exact declaration field set, and exact child value types. Caller binding order is ignored after validation. Materialization follows declaration field order, allocates all child node IDs system-side, and constructs the ordinary persistent field bindings.

`FieldAccess` references an existing stable `FieldId`. Only fields owned by product types are accepted, and the source recipe must produce the exact owning `TypeRef.Named` type.

## Option and Result construction

`OptionSome(value)` derives `Option<T>` from the value recipe. `OptionNone(T)` carries explicit `T`.

`ResultOk(E, value)` derives the success type from `value` and produces `Result<T,E>`. `ResultErr(T, error)` derives the error type from `error` and produces `Result<T,E>`.

These cases add no implicit conversion and no special failure semantics; they materialize the existing semantic constructors.

## Complete pure DirectCall

`DirectCall(targetFunctionId, arguments)` references an existing callee by stable `FunctionId`. Arguments are `(ParameterId, PureExpressionRecipe)` pairs and deliberately do not expose persistent call bindings.

The maintained construction surface only accepts **complete pure calls**:

- the callee must exist;
- the callee must have `RequiredEffects = []`;
- each callee parameter must be bound exactly once;
- unknown, duplicate, or omitted parameter IDs are rejected;
- every child recipe result type must equal the corresponding declared parameter type.

Caller argument-list order is ignored after validation. Materialization follows callee parameter declaration order and creates the `DirectCall` node last.

This surface does **not** create missing-argument obligations. Incomplete calls and effectful calls remain outside this boundary.

## Scoped pure control flow

### Lexical references

Match arms introduce ephemeral recipe-local lexical frames. `RecipePatternReference.ScopeDepth = 0` addresses the current enclosing match-arm frame, `1` addresses the next outer frame, and so on.

Frames exist structurally even when they bind no values. In particular, an `Option.None` body receives an empty frame, so an outer slot seen from inside that body moves to the next scope depth rather than collapsing inward.

Slots are derived from the persistent semantic shape:

- union arm: `Field(fieldId)` for every declared variant field;
- `Option.Some`: `Value`;
- `Option.None`: no slots;
- `Result.Ok`: `Value`;
- `Result.Err`: `Error`.

The caller never supplies a production `PatternBindingId`, binding type, or pattern display name. Slot types come from the union declaration or matched `Option`/`Result` input type.

### Conditional

`Conditional(condition, whenTrue, whenFalse)` requires a `Bool` condition. Both branches are validated in the same lexical pattern environment and must produce exactly one common type.

The condition materializes in the current graph. Each branch materializes into its own child `ValueGraph`, followed by the parent `Conditional` node.

### MatchUnion

`MatchUnion(input, arms)` requires an input whose inferred type names an existing union. The caller supplies `(VariantId, PureExpressionRecipe)` arms but the set must equal the declared variant set exactly.

Caller arm order is not semantic. Persistent arms follow union declaration order, and each arm's pattern-field bindings follow field declaration order. Fresh production `PatternBindingId`s are allocated system-side with `DisplayName = None` and declaration-derived types.

All arm bodies must produce the same result type.

### MatchOption

`MatchOption(input, someBody, noneBody)` requires `Option<T>`.

- Some body receives `Value : T`;
- None body receives an empty lexical frame.

Both bodies must produce the same result type. The Some pattern binding is allocated system-side.

### MatchResult

`MatchResult(input, okBody, errBody)` requires `Result<T,E>`.

- Ok body receives `Value : T`;
- Err body receives `Error : E`.

Both bodies must produce the same result type. Both pattern bindings are allocated system-side.

### Outer local values remain out of scope

Branch recipes may reference target-function parameters, existing semantic declarations, and visible lexical pattern slots. They do not gain caller-controlled references to arbitrary outer graph nodes.

If the same pure computation is needed inside a branch, it is represented again as recipe structure. General local symbols, let-binding, DAG sharing, and CSE remain separate design questions.

## Materialization

After complete preflight succeeds, recipe nodes receive fresh `GraphNodeId`s from `StableIdAllocator` and become the corresponding existing `SemanticNodeKind` values.

Match arm slots additionally receive fresh production `PatternBindingId`s. Their persistent `PatternBinding.Type` is derived from validated semantic facts and `DisplayName` is initially `None`.

Branch recipes become nested child `ValueGraph`s. No caller-supplied raw graph, semantic-node, or pattern-binding representation crosses the construction boundary.

Existing declaration references such as callback/callee `FunctionId`, callee `ParameterId`, product `TypeId`, union `VariantId`, and `FieldId` remain references; the workflow does not allocate or mutate those declarations.

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

`ResultingChange.AddedNodes` recursively includes branch-local nodes through `ValueGraph.collectNodes`, while `RequestedChange` remains the original construction intent. This preserves the distinction between what the caller asked for and the full materialized result.

## Compatibility surfaces

The earlier `Float64ExpressionRecipe` / `AgentStructuralWorkflow` surface remains available as a narrower compatibility surface backed by the same structural interaction model.

The specialized `AgentListMapStructuralWorkflow` also remains available for compatibility with the M2 ListMap surface and its frozen evidence. Removing or wrapping that specialized API is a separate compatibility decision.

## Deliberately outside the current surface

The maintained recipe does not author:

- incomplete direct calls or missing-argument obligation creation;
- effectful direct calls;
- capability invocation or other effectful behavior;
- nested-expression replacement inside an existing body;
- raw `SemanticNode`, `SemanticNodeKind`, `PatternBinding`, `PatternBindingId`, `FieldValueBinding`, `CallArgumentBinding`, `ValueGraph`, or `Snapshot` payloads;
- caller-supplied production graph-node IDs;
- caller-controlled local node references, let-binding, DAG sharing, or CSE;
- wildcard/default match arms;
- pattern display-name authoring;
- declaration mutation as part of match construction;
- semantic merge/rebase or multi-proposal composition.

These exclusions are deliberate authority or semantic boundaries rather than missing encodings of the current feature.