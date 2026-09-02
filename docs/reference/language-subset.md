# Current language subset

**Status:** Current

This reference summarizes the semantic surface currently implemented and exercised through NyamNyam's validation, persistence, lowering, viewer, and C# execution pipeline.

It is a current capability reference, not a promise that every listed construct is permanently frozen.

## Primitive domains

The current primitive value domains are:

- `String`;
- `Bool`;
- `Unit`;
- `Int64`;
- `Float64`.

There is no implicit numeric promotion between `Int64` and `Float64`.

## Structured values and control

The implemented subset includes:

- product values and field access/construction;
- unions and variant construction;
- `Option<T>`;
- `Result<T,E>`;
- explicit conditionals;
- exhaustive union, `Option`, and `Result` matching with pattern bindings;
- internal direct function calls;
- external capability invocation;
- explicit effects and capability requirements.

Wildcard/default match arms are not part of the current accepted subset where exhaustiveness is semantically important.

## `Int64`

`Int64` is an explicit signed 64-bit integer domain. Current arithmetic is deliberately named by behavior rather than inheriting a backend overflow mode.

Implemented operations include:

```text
Int64WrappingAdd
Int64WrappingSubtract
Int64WrappingMultiply
Int64Equal
Int64LessThan
Int64LessThanOrEqual
Int64GreaterThan
Int64GreaterThanOrEqual
```

Wrapping arithmetic uses two's-complement modulo-`2^64` behavior. Checked arithmetic, implicit conversion, and a broader numeric tower are not part of this contract.

## Immutable ordered `List<T>`

`List<T>` is immutable and ordered; element order is semantic state.

The current collection surface includes:

```text
ListEmpty
ListConstruct
ListLength
ListAppendItem
ListConcat
ListMap
ListFilter
ListAny
ListAll
```

Higher-order operations reference callbacks by stable `FunctionId`. The accepted slice is pure: effectful callbacks are rejected rather than acquiring an implicit traversal-order/effect contract. Generic lambdas/closures are not part of the current subset.

## `Float64`

`Float64` means IEEE 754 binary64 numeric semantics.

The authoritative literal representation is the exact 64-bit encoding, canonically rendered in persistence/IR as sixteen lowercase hexadecimal digits. This preserves signed zero, infinities, subnormal values, and exact finite binary64 encodings.

Semantic NaN literals canonicalize to:

```text
7ff8000000000000
```

NaN payload and sign are therefore not distinct ordinary `Float64` semantic facts.

Implemented nodes include:

```text
Float64Literal
Float64Add
Float64Subtract
Float64Multiply
Float64Divide
Float64Equal
Float64LessThan
Float64IsNaN
Float64IsFinite
Float64IsInfinite
```

Binary64 infinity, NaN, and signed-zero outcomes are normal values under this contract rather than transaction diagnostics. See the [Float64 verification record](../verification/m1-float64-implementation-notes-20260828.md).

## Agent editing surfaces

The implemented semantic language and agent editing authority are deliberately distinct. Typed agent operations expose useful semantic construction without making raw snapshot or graph payloads ordinary editing authority.

### M1 operation workflow

`AgentWorkflow` / `AgentOperationIntent` supports:

- display-name rename for types and functions;
- adding a function parameter with an explicit plan for every affected direct call;
- resolving a generated missing-argument obligation with an existing compatible value;
- simulation, Change Review, reviewed publication or rejection, and stale-proposal failure.

### Pure expression body replacement

`AgentPureExpressionStructuralWorkflow` is the reusable cross-domain construction surface. It replaces the complete body of one existing function from a tree-shaped `PureExpressionRecipe` without exposing production graph-node identities.

The current recipe covers primitive scalar expressions plus pure List construction/query and higher-order traversal roles:

```text
Parameter(ParameterId)
StringLiteral
BoolLiteral
UnitLiteral
Int64Literal
Float64Literal
StringIsEmpty
StringContains
Int64WrappingAdd / Subtract / Multiply
Int64Equal / LessThan / LessThanOrEqual / GreaterThan / GreaterThanOrEqual
Float64Add / Subtract / Multiply / Divide
Float64Equal / LessThan
Float64IsNaN / IsFinite / IsInfinite
ListEmpty
ListConstruct
ListLength
ListAppendItem
ListConcat
ListMap
ListFilter
ListAny
ListAll
```

Before materialization the workflow checks target/base guards, the established 32-depth / 64-node bounds, target-owned parameter scope, scalar operand types, explicit List item `TypeRef` validity, List item compatibility, higher-order callback existence/arity/input/result/purity, exact replacement-result compatibility, and old-body obligation protection. Every recipe occurrence then receives a fresh system-owned `GraphNodeId` in deterministic left-to-right post-order; `ListConstruct` preserves item order.

`ListEmpty` and `ListConstruct` carry explicit item types because an empty or heterogeneous-free construction cannot always derive its item type from a child. Named/capability references and builtin-generic arity are checked before materialization.

Higher-order List cases reference existing callbacks by `FunctionId`. `ListMap` accepts a pure unary `T -> R`; `ListFilter`, `ListAny`, and `ListAll` accept a pure unary `T -> Bool`. The workflow does not construct lambdas or permit effectful traversal callbacks.

The requested construction tree is ephemeral intent; the resulting `SemanticNodeKind` graph remains authoritative state. Review, receipt binding, stale rejection, readiness, publication, and history use the same structural transaction mechanics established in M2.

See [Pure expression structural authoring](../specs/pure-expression-authoring.md).

### Float64 structural compatibility surface

`AgentStructuralWorkflow` remains available for the narrower Float64-only body-replacement recipe:

```text
Parameter(ParameterId)
Literal(Float64Bits)
Add(left, right)
Subtract(left, right)
Multiply(left, right)
Divide(left, right)
```

It preserves the original bounded Float64 interaction surface and frozen M2 evidence. Adjacent Float64 arithmetic roles were added without creating separate operation/protocol families.

The exact narrower contract is maintained in [Bounded structural editing](../specs/bounded-structural-editing.md).

### Specialized ListMap compatibility surface

`AgentListMapStructuralWorkflow` supports the original shallow `ParameterReference -> ListMap` M2 body-replacement shape. The source is a target-owned `List<T>` parameter and the mapper an existing pure unary function compatible with `T -> R`; replacement node identities remain system-owned and the same simulate/review/publication mechanics apply.

The general `PureExpressionRecipe` now covers the same semantic capability through `ListMap(Parameter(sourceParameterId), mapperFunctionId)` while also allowing composed source expressions. The specialized workflow remains available for compatibility and historical evidence; removing or wrapping it is a separate API-cleanup decision rather than a requirement of the general construction model.

### Current boundary

The reusable `PureExpressionRecipe` currently covers primitive scalar expressions and the implemented pure List expression family, including higher-order traversal with existing pure callbacks. It does **not** yet author products, unions, `Option`, `Result`, direct calls, capability invocation, branches/matches, pattern bindings, or arbitrary nested-expression replacement.

No maintained structural authoring surface accepts arbitrary `Snapshot`, `ValueGraph`, `SemanticNode`, `SemanticNodeKind`, or caller-owned production node IDs.

Those limits describe the current incremental implementation, not a standing requirement that agent authority remain one operation per semantic role. Broader typed construction should be judged by semantic clarity, validation, reviewability, and evidence rather than by the shape of the M2 bootstrap experiment.

## Evidence status

Milestone 1 and Milestone 2 both have recorded controlled agent-evaluation evidence.

M2 established, under its frozen conditions, that agents could inspect stable semantic facts, produce reviewed structural proposals, publish exact reviewed results, preserve unrelated semantic state, reject stale work, and recover through fresh inspection/simulation. The current pure-expression workflow reuses that transaction/review interaction model while broadening the construction algebra across primitive scalar and the current pure List domains.

The broader cross-domain surface is maintained implementation, but it does not retroactively change the frozen M2 evaluation condition or claim that the new algebra has separately undergone controlled model evaluation.

See the [Milestone 2 record](../plans/milestone-2.md) and the maintained [verification philosophy](../developer/verification.md).

## Explicitly outside the current subset

The following are not currently accepted language features merely because a backend could express them:

- implicit numeric promotion or conversion;
- generic numeric operator bags;
- generic lambdas/closures;
- effectful List traversal callbacks;
- hidden backend exception/default semantics;
- first-class exact bit manipulation.

A dedicated exact-bit domain remains an [experiment](../experiments/bit-representation.md), separate from `Int64` and `Float64` numeric semantics.

## Failure behavior

Invalid semantic structure, explicit unresolved work, backend readiness failure, and expected runtime-domain failure are separate concepts. See the [failure and obligation policy](../specs/failure-obligation-policy.md).

## Milestone context

Milestone 1 is complete for its accepted bounded-operation claim, including its recorded agent evaluation.

Milestone 2 is complete for its accepted bounded structural-editing claim, including the Float64 and ListMap controlled evaluation records. Subsequent maintained extensions may reuse that evidence while their interaction-model assumptions remain materially unchanged.
