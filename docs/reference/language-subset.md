# Current language subset

**Status:** Current

This reference summarizes the semantic surface currently implemented and exercised through Nyrwvy's validation, persistence, lowering, viewer, and C# execution pipeline.

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
- Bool negation and short-circuit Bool and/or;
- exhaustive union, `Option`, and `Result` matching with pattern bindings;
- internal direct function calls;
- pure capture-free function types, named references, and positional invocation;
- program-defined stateless Capability values backed by pure named functions;
- external capability invocation;
- explicit effects and capability requirements.

Wildcard/default match arms are not part of the current accepted subset where exhaustiveness is semantically important.

`BoolNot(operand)` negates a Bool. `BoolAnd(left, right)` is true when both operands are true and `BoolOr(left, right)` when either is. Their `left` operand is an ordinary Bool value of the enclosing graph; their `right` operand is a nested value graph with the scope, typing and effect rules of a `Conditional` branch. It is evaluated at most once and only when `left` does not decide the result: for `BoolAnd` only when `left` is true, for `BoolOr` only when `left` is false. A right operand that aborts, invokes a capability or calls a failing function therefore has that effect only when it is evaluated. All operands and results are Bool; a right operand may instead abort. Each construct is the dedicated form of a checkable conditional expansion (`Conditional(operand, false, true)`, `Conditional(left, right, false)`, `Conditional(left, true, right)`); the graph keeps the dedicated node so review reads the intent directly (see the [decision](../decisions/bool-operators-and-map.md)). There is no Map or Set.

`ValueEqual(left, right)` is structural equality of two values of one *equatable* type; its result is Bool and it never fails. String, Int64, Bool and Unit are equatable, as are `List`, `Option` and `Result` of equatable types and declared Products and Unions whose fields are all equatable, through any nesting and including recursive types. Float64 (NaN; compare with `Float64Equal`), function types, `Task`, `TaskScope`, capabilities and type parameters are not, nor is any type that reaches one. Two values are equal when they have the same variant and pairwise equal fields; Strings compare by ordinal code units, Options and Results by case and payload, and lists by length and then item by item in order. `ValueEqual` also covers Bool equality. A node whose operands differ in type reports `ValueEqualOperandTypeMismatch`, and one over a type that is not equatable reports `ValueEqualTypeNotEquatable`, naming the path to the offending part by display names (for example `Holder.measures -> List item -> Measure.value`). `StringEqual`, `Int64Equal` and `Float64Equal` remain the per-type forms ([decision](../decisions/value-equality-and-map.md)).

## `Int64`

`Int64` is an explicit signed 64-bit integer domain. Current arithmetic is deliberately named by behavior rather than inheriting a backend overflow mode.

Implemented operations include:

```text
Int64WrappingAdd
Int64WrappingSubtract
Int64WrappingMultiply
Int64FloorDivide
Int64FloorModulo
Int64Equal
Int64LessThan
Int64LessThanOrEqual
Int64GreaterThan
Int64GreaterThanOrEqual
```

Wrapping arithmetic uses two's-complement modulo-`2^64` behavior.

`Int64FloorDivide(left, right)` and `Int64FloorModulo(left, right)` follow Python's `//` and `%`: the quotient `q` is `floor(left / right)`, rounding toward negative infinity (`7 / 2 = 3`, `-7 / 2 = -4`, `7 / -2 = -4`, `-7 / -2 = 3`), and the remainder is `left - right * q`, so a nonzero remainder takes the divisor's sign (`7 % 2 = 1`, `-7 % 2 = 1`, `7 % -2 = -1`, `-7 % -2 = -1`). Both operands and the result are Int64 and neither operation has effects. Like every runtime operation of this subset (compare `StringSlice` clamping), they never fail: a zero divisor yields quotient `0` and remainder `left`, which keeps `left = right * q + r`, and `Int64.MinValue / -1` wraps to `Int64.MinValue` with remainder `0`, consistent with wrapping arithmetic. Callers who must distinguish a zero divisor test it with `Int64Equal` before dividing.

Checked arithmetic, implicit conversion, and a broader numeric tower are not part of this contract.

## Immutable ordered `List<T>`

`StringEqual(left, right)` compares two `String` values exactly by ordinal code-unit equality, without culture or case folding. For non-null strings, testing ordinal `StringContains` in both directions is also equivalent to equality; `StringEqual` expresses that intent directly rather than requiring two containment checks. Both operands must be String; its result is Bool. The C# backend uses `StringComparison.Ordinal`. `StringConcat(left, right)` accepts two Strings and yields their literal left-to-right concatenation, with no separator, normalization, or culture-dependent conversion; empty strings are identities. It does not implicitly format non-string values. `Int64ToString(value)` formats an Int64 as canonical invariant decimal text: ASCII digits, a leading `-` only for negative values, no `+`, grouping or padding (`Int64.MinValue` is `"-9223372036854775808"`). `StringParseInt64(text)` yields `Option<Int64>`: `Some` only when the whole String is an optional `-` followed by one or more ASCII digits and the value fits Int64. Leading zeros and `-0` are accepted; `+`, whitespace, separators, non-ASCII digits, exponents, hex, empty text and overflow yield `None`. Parsing never fails at runtime, so callers branch with `MatchOption`. `StringLength(text)` yields the number of code points as `Int64`: a surrogate pair counts once, and an unpaired surrogate code unit also counts once, so it is not the UTF-16 length. `StringSlice(text, start, end)` yields the code points at positions `[start, end)`. Both bounds are clamped to `[0, StringLength(text)]`, and `end <= start` yields `""`, so slicing never fails at runtime and never splits a surrogate pair. Negative positions are clamped, not counted from the end. Both operations scan the text, so their cost is linear in its length. Together they express bounded display text such as keeping the head and tail of a long String.

Function type parameters (`TypeRef.TypeParameter`) make a function generic over the types its parameter types mention; see [expression authoring](../specs/expression-authoring.md#higher-order-list-callbacks) for derivation of type arguments and scope rules. `List<T>` is immutable and ordered; element order is semantic state. `ListFold(source, seed, folder)` traverses left to right with a pure existing two-parameter callback `(accumulator: S, item: T) -> S` and returns the seed unchanged for empty input. The seed can carry runtime context such as a search key; this does not imply effectful traversal or closures. An authored Product accumulator carrying a runtime key, a found flag, and an `Option<T>` result can implement first-match lookup over `List<TItem>` without introducing an application-specific search node. The folder must preserve the accumulated result after the first match; an absent key or empty source yields `None`. A pure user-defined `List<String> * Int64 -> Option<String>` function can use `ListFold` with a Product accumulator holding the remaining index and optional value: negative, empty, and out-of-range inputs return `None`. This composition does not stop the fold early and can be generalized with function type parameters. `ListItemAt(list, index)` reads one item directly: for `list : List<T>` and `index : Int64` it returns `Some(item)` when `0 <= index < length` and `None` otherwise, never failing; negative indexes do not count from the end, as with `StringSlice` ([decision](../decisions/task-fields-list-indexing-and-map.md)).

The current collection surface includes:

```text
ListEmpty
ListConstruct
ListLength
ListItemAt
ListAppendItem
ListConcat
ListFold
ListEffectFold
UseResource
ParallelAll
CancellationRequested
ListMap
ListFilter
ListAny
ListAll
```

Higher-order operations reference callbacks by stable `FunctionId`. The accepted slice is pure: effectful callbacks are rejected rather than acquiring an implicit traversal-order/effect contract. Generic lambdas/closures are not part of the current subset.

## Program-defined capability values

`CapabilityConstruct` maps every operation of an existing Capability to a monomorphic pure function with the same positional parameter types and result type. The resulting value can be passed to existing consumers; invocation still requires the operation's declared Effect. See [program capability implementations](../specs/program-capability-implementations.md) for validation, authoring, review and versioned persistence. State, capture and effectful implementations are outside this slice.

## Pure function values

`TypeRef.Function(parameters, resultType)` describes a pure capture-free callable.
`FunctionReference` produces a value referring to a monomorphic named function;
`FunctionInvoke` checks positional arity and exact argument types. References
require transitive purity, including absence of ambient cancellation reads.
Generic functions can consume, forward, return and invoke function values using
their type parameters. `find<T>` can therefore receive a predicate as a value and
pass it to a `ListFold` folder through callback context. Capturing closures,
effectful function types and direct references to generic functions are excluded;
use a concrete wrapper for the latter. See [the contract](../specs/pure-function-values.md).

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
StringEqual
StringConcat
ValueEqual
Int64ToString
StringParseInt64
StringLength
StringSlice
Int64WrappingAdd / Subtract / Multiply
Int64FloorDivide / FloorModulo
Int64Equal / LessThan / LessThanOrEqual / GreaterThan / GreaterThanOrEqual
Float64Add / Subtract / Multiply / Divide
Float64Equal / LessThan
Float64IsNaN / IsFinite / IsInfinite
ListEmpty
ListConstruct
ListLength
ListItemAt
ListAppendItem
ListConcat
ListFold
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
CapabilityConstruct
FunctionReference
FunctionInvoke
DirectCall
SelfCall
CapabilityInvoke
Bind
BoolNot
BoolAnd / BoolOr
Conditional
MatchUnion
MatchOption
MatchResult
```

Before materialization the workflow checks target/base guards, the 32-depth / 256-node bounds across all children and nested branches, target-owned parameter scope, lexical pattern-reference scope/slots, scalar/List operand types, explicit `TypeRef` validity, higher-order callback contracts, structured declaration bindings, complete call/invocation contracts, predeclared effect requirements, conditional/match input and branch types, exact union arm coverage, exact replacement-result compatibility, and old-body obligation protection. Recipe nodes receive fresh system-owned `GraphNodeId`s; Bind-body and match-arm slots additionally receive fresh system-owned `PatternBindingId`s.

`ListEmpty`, `ListConstruct`, `OptionNone`, and one side of each `Result` constructor carry explicit type information when it cannot be inferred from a child. Named/capability references and builtin-generic arity are checked before materialization.

Higher-order List cases reference existing callbacks by `FunctionId`. `ListFold` accepts a pure binary `(S, T) -> S` with seed `S`; `ListMap` accepts a pure unary `T -> R`; `ListFilter`, `ListAny`, and `ListAll` accept a pure unary `T -> Bool`. An optional context binds further callback parameters by identity to values computed before the traversal, and these arities apply to the remaining unbound parameters (see [expression authoring](../specs/expression-authoring.md#higher-order-list-callbacks)). The workflow does not construct lambdas or permit effectful traversal callbacks. Source expressions may use effects already declared by the target.

Product and union construction bind declaration fields by stable `FieldId`. Preflight requires the exact declared field set with no duplicates and exact field-value types. Caller binding order is not semantic: materialization follows declaration field order and creates ordinary persistent `FieldValueBinding` values only after child nodes receive system-owned IDs. `FieldAccess` requires the exact owning product type. `UnionConstruct` derives its result type from the referenced stable `VariantId`.

`OptionSome` derives its contained type from the child; `OptionNone` names the contained type explicitly. `ResultOk` derives its success type and carries the error type, while `ResultErr` derives its error type and carries the success type.

`DirectCall` references an existing callee by `FunctionId` and arguments by stable callee `ParameterId`. Every callee required effect must already be declared by the target, and every callee parameter must be bound exactly once with an exact-compatible child recipe. Caller binding-list order is ignored; materialization follows callee parameter declaration order and creates persistent `ArgumentBinding` values only after child nodes receive system-owned IDs. Incomplete calls do not create obligations through this surface.

`SelfCall` calls the owning function (the function being created, or the replacement target), with arguments keyed by zero-based parameter slot; it materializes to an ordinary `DirectCall` to the owner, so a recursive function is created in one request. See [expression authoring](../specs/expression-authoring.md#selfcall).

`CapabilityInvoke` takes an ordinary receiver recipe, an existing `CapabilityOperationId`, and `(CapabilityParameterId, ExpressionRecipe)` bindings. The receiver must produce the operation's exact owning capability type; bindings must exactly cover the operation parameters with no duplicates and exact value types. The operation's effect must already be declared by the target. Materialization creates receiver nodes first, argument nodes in operation declaration order, and the invocation last. Its result type is the existing operation's `ResultType`; failure metadata is not reinterpreted.

All recursive receiver/argument expressions and every branch or Bind body are checked against the same target effect declaration. No effect is added or removed automatically, and simulation/publication do not execute the authored capability operations. Repeated effectful recipe occurrences are separate evaluations; references to a Bind's lexical value reuse its evaluated result.

Deterministic construction order is not a runtime sequencing guarantee. `SemanticallyReady` does not imply execution readiness: the existing execution layer still rejects independent effectful nodes with `AmbiguousEffectOrder` when graph dependencies do not order them. For example, an effectful receiver and an independent effectful argument do not become ordered just because the receiver was materialized first. Dependency-ordered chains and branch-local invocations retain their existing executable semantics; no hidden sequencing or node-list-order semantics are introduced.

`Bind(value, body)` explicitly evaluates its input once before its nested body, gives the body an immutable `Value` slot, and returns the body's result. It works for all supported value types, including `Unit` and capabilities; unused bindings retain initializer effects. Its binding is visible only in its body and nested scopes, not in its own initializer. Bind bodies consume one lexical scope depth just like match frames. The authoritative Bind node and system-owned `PatternBindingId` survive persistence and typed projection; existing Execution IR jumps and block parameters retain the order and provenance. Independent effectful siblings within either graph still reject.

Scoped control flow remains part of the same ephemeral construction model rather than exposing child graphs directly. `Conditional` requires a `Bool` condition and equal branch result types; `BoolAnd` and `BoolOr` require a `Bool` left operand and a right operand that produces `Bool` or aborts. `MatchUnion` is exhaustive and canonicalizes arms and field bindings to declaration order. `MatchOption` and `MatchResult` introduce typed branch bindings. `RecipePatternReference` addresses visible Bind-body and match-arm slots by lexical `ScopeDepth`; structurally empty frames such as `Option.None` still consume one scope depth. Branch recipes and the right operands of `BoolAnd`/`BoolOr` materialize as nested `ValueGraph`s, while production pattern identities and binding types remain system-owned and derived from validated semantic facts. Static effect checks cover every branch without hoisting its runtime effects into the parent graph.

The requested construction tree is ephemeral intent; the resulting `SemanticNodeKind` graph remains authoritative state. Review, receipt binding, stale rejection, readiness, publication, and history use the same structural transaction mechanics established in M2. `ResultingChange.AddedNodes` recursively includes branch-local nodes so the review covers the complete materialized result rather than only the parent graph.

See [Expression structural authoring](../specs/expression-authoring.md). The old `PureExpressionRecipe` and `AgentPureExpression...` names remain thin F# source aliases/forwarders for existing clients, not an independent pure-only interface. Existing diagnostic codes and historical evaluation protocol names are retained.

### M1 operation workflow

`AgentWorkflow` / `AgentOperationIntent` supports:

- display-name rename for types and functions;
- adding a function parameter with an explicit plan for every affected direct call;
- resolving a generated missing-argument obligation with an existing compatible value;
- explicitly adding or removing an existing `EffectId` from a function's `RequiredEffects`;
- adding a Product field with an explicit existing typed value for **every** affected `ProductConstruct`, including nested branches; the new `FieldId` is allocated by the system while existing type, function and node IDs remain stable;
- removing a Product field and its constructor bindings when all field accesses have already been repaired; affected functions must declare no required effects, so removal cannot silently drop effectful evaluation;
- deleting an unused function only after all direct calls, function-value references and List callbacks are gone, and when no obligation targets its body;
- deleting an unreferenced Product or Union declaration, including its field/variant identities; dependent functions can be removed in the **same** reviewed transaction, but any surviving references (including nested type references) prevent publication;
- deleting an unreferenced Capability and Effect declaration after consumers have been repaired or removed; one reviewed batch may remove the dependent function, Capability, then Effect in that order, and the diff retains removed operation/parameter identities;
- simulation, Change Review, reviewed publication or rejection, and stale-proposal failure.

Field-addition plans use `(constructNodeId, valueNodeId)` pairs for existing values, not arbitrary graph payloads or an implicit default. No partial publication is allowed: missing/duplicate plans, wrong types, or out-of-scope references reject. Function deletion likewise does not silently rewrite callers. The review diff lists added/removed fields, affected constructor identities, and deleted function/node identities. This is a bounded T1 change, not general Product field migration or deletion of referenced declarations.

Required-effect edits do not propagate automatically through callers and do not create obligations. Multiple explicit function edits may be composed in one transaction; full validation of the final proposed snapshot enforces caller/callee and capability/body effect consistency. Expression authoring still never mutates `RequiredEffects` implicitly.

### Fieldless Union variant evolution

`AgentFieldlessUnionVariantEvolutionWorkflow` supports one bounded schema-evolution operation: add a new **fieldless** variant to an existing Union while preserving the Union's `TypeId`. The caller identifies the target Union and one existing fieldless source `VariantId`; the new `VariantId` remains system-owned.

Publication repairs exhaustive `MatchUnion` nodes atomically by adding an arm for the new variant whose behavior is copied from the selected source arm. Copied arm bodies receive fresh system-owned graph-node and pattern-binding IDs. The variant addition and all required match repairs are reviewed and published as one snapshot, so no intermediate non-exhaustive state becomes authoritative.

This surface intentionally does not cover field-bearing variant evolution, arbitrary new match-arm recipes, type deletion, or a generalized schema-migration framework. It was added for the concrete Mini Work Planner `ExecutionState.TimedOut` maintenance case.

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

## Typed function abort

`Abort(E)` ends the current `Result<T,E>` function invocation with `Err(E)`.
The exact-ID and symbolic recipe is `{"kind":"abort","error":<recipe>}`. It is
allowed at graph results, including nested Bind/match/conditional bodies, and
has no normal value. Only normally completing arms contribute a branch result
type. There is no user-visible Never type or implicit propagation through callees.

The caller can recover from the returned Err. UseResource releases after its
body function exits; ParallelAll requests cancellation on a worker Err, joins
every worker, and chooses the lowest-index typed failure. Host-fault precedence
is unchanged. See the [implemented contract](../specs/typed-function-abort.md)
and [ordinary-tool example](../../examples/function-abort/README.md).
