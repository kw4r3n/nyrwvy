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

### Float64 structural body replacement

`AgentStructuralWorkflow` supports complete replacement of one `Float64`-returning function body with the typed recipe:

```text
Parameter(ParameterId)
Literal(Float64Bits)
Add(left, right)
Subtract(left, right)
Multiply(left, right)
Divide(left, right)
```

The operation:

- targets an existing `FunctionId`, expected base `SnapshotId`, and expected current body result `GraphNodeId`;
- checks recipe depth/node limits and parameter scope/type before materialization;
- allocates all replacement body-node IDs system-side;
- replaces the complete old body with a disjoint fresh body-node set;
- rejects any old body referenced by an open or resolved obligation;
- preserves the requested recipe separately from materialized structural diff facts;
- exposes base and proposed Focus views plus exact review fingerprints;
- preserves workspace publication history when committed;
- lowers and executes through the ordinary semantic/execution/backend pipeline.

The current contract is maintained in [Bounded structural editing](../specs/bounded-structural-editing.md). `Subtract`, `Multiply`, and `Divide` extend the already-evidenced Float64 recipe interaction model rather than introducing separate operation/protocol families for each arithmetic node.

### Specialized ListMap body replacement

`AgentListMapStructuralWorkflow` supports complete replacement of one compatible List-returning function body with a shallow `ParameterReference -> ListMap` shape. The source must be a target-owned `List<T>` parameter and the mapper an existing pure unary function compatible with `T -> R`; replacement node identities remain system-owned and the same simulate/review/publication mechanics apply.

The specialized ListMap surface remains separate because its semantic inputs and higher-order function constraints differ from the Float64 expression recipe. Its existence does not imply every List role should receive another permanently separate protocol.

### Current boundary

The current structural authoring surfaces still do **not** accept arbitrary `Snapshot`, `ValueGraph`, `SemanticNode`, or caller-owned production node IDs. They also do not yet provide a general typed authoring form for branches, calls, capabilities, products, unions, `Option`, `Result`, or arbitrary List expressions, nor do they target a nested expression independently.

Those limits describe current implementation, not a standing requirement that agent authority remain one operation per node kind. Broader typed construction should be judged by semantic clarity, validation, reviewability, and evidence rather than by the shape of the M2 bootstrap experiment.

## Evidence status

Milestone 1 and Milestone 2 both have recorded controlled agent-evaluation evidence.

M2 established, under its frozen conditions, that agents could inspect stable semantic facts, produce reviewed structural proposals, publish exact reviewed results, preserve unrelated semantic state, reject stale work, and recover through fresh inspection/simulation. The Float64 and ListMap slices therefore provide evidence for the shared interaction model rather than a requirement to repeat a large model-evaluation tranche for every adjacent semantic role.

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
