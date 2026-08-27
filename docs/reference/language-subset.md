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

This subset represents the accepted Milestone 1 language-growth slice. Future features should be added from explicit semantic contracts and evidence rather than inferred from C# or .NET capabilities. The remaining Milestone 1 gate is the recorded agent evaluation in the [Milestone 1 plan](../plans/milestone-1.md).
