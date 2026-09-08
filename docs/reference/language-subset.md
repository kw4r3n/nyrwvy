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

### Expression body replacement

`AgentExpressionStructuralWorkflow` is the reusable cross-domain construction surface. It replaces the complete body of one existing function from a tree-shaped `ExpressionRecipe` without exposing production graph-node or pattern-binding identities.

The current recipe covers primitive scalar expressions, the implemented pure-callback List family, structured values, complete direct calls, capability invocation, and scoped control flow:

```text
Parameter(ParameterId)
PatternReference(RecipePatternReference)
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
ProductConstruct
FieldAccess
UnionConstruct
OptionNone
OptionSome
ResultOk
ResultErr
DirectCall
CapabilityInvoke
Bind
Conditional
MatchUnion
MatchOption
MatchResult
```

Before materialization the workflow checks target/base guards, the established 32-depth / 64-node bounds across all children and nested branches, target-owned parameter scope, lexical pattern-reference scope/slots, scalar/List operand types, explicit `TypeRef` validity, higher-order callback contracts, structured declaration bindings, complete call/invocation contracts, predeclared effect requirements, conditional/match input and branch types, exact union arm coverage, exact replacement-result compatibility, and old-body obligation protection. Recipe nodes receive fresh system-owned `GraphNodeId`s; Bind-body and match-arm slots additionally receive fresh system-owned `PatternBindingId`s.

`ListEmpty`, `ListConstruct`, `OptionNone`, and one side of each `Result` constructor carry explicit type information when it cannot be inferred from a child. Named/capability references and builtin-generic arity are checked before materialization.

Higher-order List cases reference existing callbacks by `FunctionId`. `ListMap` accepts a pure unary `T -> R`; `ListFilter`, `ListAny`, and `ListAll` accept a pure unary `T -> Bool`. The workflow does not construct lambdas or permit effectful traversal callbacks. Source expressions may use effects already declared by the target.

Product and union construction bind declaration fields by stable `FieldId`. Preflight requires the exact declared field set with no duplicates and exact field-value types. Caller binding order is not semantic: materialization follows declaration field order and creates ordinary persistent `FieldValueBinding` values only after child nodes receive system-owned IDs. `FieldAccess` requires the exact owning product type. `UnionConstruct` derives its result type from the referenced stable `VariantId`.

`OptionSome` derives its contained type from the child; `OptionNone` names the contained type explicitly. `ResultOk` derives its success type and carries the error type, while `ResultErr` derives its error type and carries the success type.

`DirectCall` references an existing callee by `FunctionId` and arguments by stable callee `ParameterId`. Every callee required effect must already be declared by the target, and every callee parameter must be bound exactly once with an exact-compatible child recipe. Caller binding-list order is ignored; materialization follows callee parameter declaration order and creates persistent `ArgumentBinding` values only after child nodes receive system-owned IDs. Incomplete calls do not create obligations through this surface.

`CapabilityInvoke` takes an ordinary receiver recipe, an existing `CapabilityOperationId`, and `(CapabilityParameterId, ExpressionRecipe)` bindings. The receiver must produce the operation's exact owning capability type; bindings must exactly cover the operation parameters with no duplicates and exact value types. The operation's effect must already be declared by the target. Materialization creates receiver nodes first, argument nodes in operation declaration order, and the invocation last. Its result type is the existing operation's `ResultType`; failure metadata is not reinterpreted.

All recursive receiver/argument expressions and every branch or Bind body are checked against the same target effect declaration. No effect is added or removed automatically, and simulation/publication do not execute the authored capability operations. Repeated effectful recipe occurrences are separate evaluations; references to a Bind's lexical value reuse its evaluated result.

Deterministic construction order is not a runtime sequencing guarantee. `SemanticallyReady` does not imply execution readiness: the existing execution layer still rejects independent effectful nodes with `AmbiguousEffectOrder` when graph dependencies do not order them. For example, an effectful receiver and an independent effectful argument do not become ordered just because the receiver was materialized first. Dependency-ordered chains and branch-local invocations retain their existing executable semantics; no hidden sequencing or node-list-order semantics are introduced.

`Bind(value, body)` explicitly evaluates its input once before its nested body, gives the body an immutable `Value` slot, and returns the body's result. It works for all supported value types, including `Unit` and capabilities; unused bindings retain initializer effects. Its binding is visible only in its body and nested scopes, not in its own initializer. Bind bodies consume one lexical scope depth just like match frames. The authoritative Bind node and system-owned `PatternBindingId` survive persistence and typed projection; existing Execution IR jumps and block parameters retain the order and provenance. Independent effectful siblings within either graph still reject.

Scoped control flow remains part of the same ephemeral construction model rather than exposing child graphs directly. `Conditional` requires a `Bool` condition and equal branch result types. `MatchUnion` is exhaustive and canonicalizes arms and field bindings to declaration order. `MatchOption` and `MatchResult` introduce typed branch bindings. `RecipePatternReference` addresses visible Bind-body and match-arm slots by lexical `ScopeDepth`; structurally empty frames such as `Option.None` still consume one scope depth. Branch recipes materialize as nested `ValueGraph`s, while production pattern identities and binding types remain system-owned and derived from validated semantic facts. Static effect checks cover every branch without hoisting its runtime effects into the parent graph.

The requested construction tree is ephemeral intent; the resulting `SemanticNodeKind` graph remains authoritative state. Review, receipt binding, stale rejection, readiness, publication, and history use the same structural transaction mechanics established in M2. `ResultingChange.AddedNodes` recursively includes branch-local nodes so the review covers the complete materialized result rather than only the parent graph.

See [Expression structural authoring](../specs/expression-authoring.md). The old `PureExpressionRecipe` and `AgentPureExpression...` names remain thin F# source aliases/forwarders for existing clients, not an independent pure-only interface. Existing diagnostic codes and historical evaluation protocol names are retained.

### M1 operation workflow

`AgentWorkflow` / `AgentOperationIntent` supports:

- display-name rename for types and functions;
- adding a function parameter with an explicit plan for every affected direct call;
- resolving a generated missing-argument obligation with an existing compatible value;
- explicitly adding or removing an existing `EffectId` from a function's `RequiredEffects`;
- simulation, Change Review, reviewed publication or rejection, and stale-proposal failure.

Required-effect edits do not propagate automatically through callers and do not create obligations. Multiple explicit function edits may be composed in one transaction; full validation of the final proposed snapshot enforces caller/callee and capability/body effect consistency. Expression authoring still never mutates `RequiredEffects` implicitly.

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

The general `ExpressionRecipe` covers the same semantic capability through `ListMap(Parameter(sourceParameterId), mapperFunctionId)` while also allowing composed source expressions. The specialized workflow remains available for compatibility and historical evidence; removing or wrapping it is a separate API-cleanup decision rather than a requirement of the general construction model.

### Current boundary

The reusable `ExpressionRecipe` covers primitive scalar expressions, the implemented pure-callback List family, product/field/union construction, `Option`/`Result` construction, complete direct calls and capability invocations within predeclared effects, explicit scoped sequencing/value reuse through `Bind`, and scoped conditionals/matches with recipe-local lexical pattern references. It does **not** author incomplete calls, effect/capability/operation declarations, arbitrary references to outer graph nodes, or nested-expression replacement inside an existing body. `ExpressionRecipe` never changes the target function's effect declaration implicitly; `AgentWorkflow` separately supports explicit add/remove edits using already-existing `EffectId`s.

No maintained structural authoring surface accepts arbitrary `Snapshot`, `ValueGraph`, `SemanticNode`, `SemanticNodeKind`, `PatternBinding`, `FieldValueBinding`, `ArgumentBinding`, `CapabilityArgumentBinding`, or caller-owned production node/pattern IDs.

Those limits describe the current incremental implementation, not a standing requirement that agent authority remain one operation per semantic role. Broader typed construction should be judged by semantic clarity, validation, reviewability, and evidence rather than by the shape of the M2 bootstrap experiment.

## Evidence status

Milestone 1 and Milestone 2 both have recorded controlled agent-evaluation evidence.

M2 established, under its frozen conditions, that agents could inspect stable semantic facts, produce reviewed structural proposals, publish exact reviewed results, preserve unrelated semantic state, reject stale work, and recover through fresh inspection/simulation. The current expression workflow reuses that transaction/review interaction model across scalar, List, structured-value, complete-call, capability-invocation, and scoped control-flow domains.

The initial cross-domain scalar/List slice also passed the maintained extended verification suite after #117. Later additions reused that architectural evidence while their interaction assumptions remained materially the same.

Scoped control-flow authoring is a more meaningful interaction extension because agents must understand lexical pattern slots and `ScopeDepth`, while the materialized result includes nested branch graphs and system-owned pattern identities. Issue #132 records its completed architectural/extended pass and four exploratory tasks with accepted and committed proposals and no reported lexical diagnostic. This does not alter or retroactively broaden the frozen M2 claim.

The broader cross-domain surface is maintained implementation, but it does not claim that the entire algebra has independently undergone a new large controlled model evaluation. Capability authoring reuses existing persistent capability semantics and is covered by focused deterministic authoring and execution-IR lowering contracts; it does not claim a new live model evaluation.

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

Milestone 2 is complete for its accepted bounded structural-editing claim, including the Float64 and ListMap controlled evaluation records. Subsequent maintained extensions may reuse that evidence while their interaction-model assumptions remain materially unchanged; scoped lexical control-flow authoring was verified separately because it introduced a new interaction assumption rather than changing the completed M2 milestone.
