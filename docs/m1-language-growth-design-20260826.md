# Milestone 1 language-growth design draft — 2026-08-26

## Status

Design draft for the post-infrastructure portion of Milestone 1. This document intentionally does not change the semantic model yet.

It is based on the verified Milestone 0 architecture and the accepted AI-first semantic design principles. Implementation should begin only after the M1 agent-facing workflow boundary is verified, so that new language constructs can be exercised through the same query / simulate / review / commit path that agents will actually use.

The proposed order is:

1. `Int64` minimal total semantics;
2. `List<T>` and a small specialized collection-operation set;
3. an expanded representative multi-user program;
4. `Float64` with explicit IEEE 754 binary64 semantics;
5. explicit numeric conversions only where their failure/precision behavior is fully specified;
6. AI-editing experiments before generalizing the node taxonomy.

A separate exact-bit representation domain such as future `Bits64` is a deferred development idea, not a Milestone 1 requirement. See `m1-bit-representation-design-20260826.md`.

## Design constraints

New constructs follow the existing Nayamnyam rules:

- semantic role should be visible from the node kind itself;
- no implicit numeric conversions;
- no generic `NumericBinaryOperation` in the first slice;
- no hidden defaulting, exception semantics, or backend-specific numeric behavior;
- ordered collections declare their element order as semantic rather than relying on incidental serialization order;
- higher-order collection operations initially target stable `FunctionId`s rather than introducing lambdas/closures prematurely;
- effectful higher-order functions are excluded from the initial collection slice so list traversal does not accidentally introduce an execution-order contract;
- association slots such as list elements do not receive stable IDs unless later operations demonstrate a need for independent targeting;
- exact bit manipulation, if added later, belongs to a dedicated representation type rather than being overloaded onto numeric types.

## 1. `Int64`

### Type

Add one primitive numeric type:

```text
Int64
```

The name is deliberately width-specific. `Int`, `Number`, platform word size, and implicit widening are not part of this slice.

### Literal

Proposed semantic node:

```text
Int64Literal(value)
```

`value` is an exact signed 64-bit integer. Canonical persistence must preserve the full `[-2^63, 2^63-1]` range without passing through binary floating point.

### Initial operations

Do not introduce a bare `Int64Add` whose overflow behavior must be remembered from ambient language rules. Start with semantically total, explicitly named operations:

```text
Int64WrappingAdd(left, right)       -> Int64
Int64WrappingSubtract(left, right)  -> Int64
Int64WrappingMultiply(left, right)  -> Int64
Int64Equal(left, right)             -> Bool
Int64LessThan(left, right)          -> Bool
Int64LessThanOrEqual(left, right)   -> Bool
Int64GreaterThan(left, right)       -> Bool
Int64GreaterThanOrEqual(left, right)-> Bool
```

Wrapping operations use two's-complement modulo-`2^64` behavior. The semantic role is therefore local to the node and cannot silently become checked or saturating behavior on another backend.

This is not a claim that wrapping arithmetic should become the eventual human-facing default. It is the smallest total arithmetic contract that can be implemented without hidden traps or introducing a new arithmetic-error type prematurely.

### Deferred integer operations

Defer these until their contracts are explicit:

- checked add/subtract/multiply;
- saturating arithmetic;
- division and remainder;
- numeric parsing;
- shifts and bit operations;
- arbitrary precision integers.

In particular, integer division is not added merely because C# has `/`: zero division, `Int64.MinValue / -1`, and truncation direction must all be represented intentionally.

Bit operations are intentionally deferred to a possible dedicated exact-bit representation domain rather than being assumed to belong to `Int64` merely because it has a two's-complement machine representation.

## 2. `List<T>`

### Type form

Extend builtin generics with:

```text
List<T>
```

Arity is exactly one and is validated in the same closed manner as `Option<T>` and `Result<T,E>`.

`List<T>` is immutable and ordered. Element order is semantic.

### Construction

Proposed nodes:

```text
ListEmpty(itemType)                 -> List<T>
ListConstruct(itemType, elements[]) -> List<T>
```

`ListConstruct.elements` is an explicitly ordered sequence of `GraphNodeId`s. Each element must have exactly `itemType`.

No element-slot stable ID is introduced initially; the slot is an association inside one list-construction node rather than an independently targetable semantic entity.

### Structural collection operations

Proposed initial nodes:

```text
ListLength(source)                  -> Int64
ListAppendItem(source, item)        -> List<T>
ListConcat(left, right)             -> List<T>
```

`ListAppendItem` and `ListConcat` remain distinct node kinds instead of overloading an ambiguous `Append` operation.

### Specialized higher-order operations

Proposed nodes:

```text
ListMap(source, mapperFunctionId)       -> List<U>
ListFilter(source, predicateFunctionId) -> List<T>
ListAny(source, predicateFunctionId)    -> Bool
ListAll(source, predicateFunctionId)    -> Bool
```

Initial validation rules:

- `ListMap` source is `List<T>`;
- mapper function has exactly one parameter of `T` and returns `U`;
- `ListFilter`, `ListAny`, and `ListAll` predicates have exactly one parameter of `T` and return `Bool`;
- mapper/predicate functions have no required effects in the first slice;
- the target is a stable `FunctionId`, never a display name;
- no implicit argument adaptation or type conversion occurs.

The effect-free restriction is deliberate. It keeps the initial nodes about value transformation rather than quietly defining iteration order, short-circuit effect ordering, retries, or concurrency semantics. Effectful traversal can be designed later as a separate construct if needed.

### Why specialized list nodes first

Do not introduce these merely to make the collection API look conventional:

```text
FunctionValue
Lambda
Closure
Capture
GenericHigherOrderCall
```

The specialized node set is intentionally larger but locally classifiable. M1 should measure whether this helps agent editing before generalizing it away.

## 3. Representative M1 program

Grow the M0 user-validation program instead of replacing it.

Candidate data flow:

```text
List<RawUserInput>
    -> validate_users
    -> List<Result<ValidUser, ValidationError>>
    -> report_results
    -> Console
```

A useful first version should exercise:

- `List<T>` construction;
- `ListMap` or equivalent validation mapping;
- `ListLength`;
- `Int64` counting;
- existing `Result`, product, union, branching, capability, and effect semantics;
- a cross-function signature edit performed through the agent workflow;
- Focus and Change Review projections for the larger graph.

The representative program should remain small enough that failures can still be inspected manually. Its purpose is semantic-editing pressure, not application realism.

## 4. `Float64`

`Float64` belongs in M1 language growth, but after the integer/list slice is working through the agent workflow.

### Type

```text
Float64
```

The contract is IEEE 754 binary64, not an abstract `Float` whose width or rounding can vary by backend.

### Literal authority

A float literal must have exactly one authoritative representation.

Recommended semantic authority: canonical IEEE 754 binary64 bits. Human decimal rendering is a projection, not a second stored truth.

This avoids ambiguity around:

- `-0.0` versus `+0.0`;
- infinities;
- NaN;
- decimal strings with multiple spellings that round to the same binary64 value;
- exact persistence and fingerprinting.

The creation/query API may accept and display convenient decimal/special-token forms, but the semantic node should not store both decimal text and bits as independently editable facts.

### Initial operations

Proposed nodes:

```text
Float64Add(left, right)         -> Float64
Float64Subtract(left, right)    -> Float64
Float64Multiply(left, right)    -> Float64
Float64Divide(left, right)      -> Float64
Float64Equal(left, right)       -> Bool
Float64LessThan(left, right)    -> Bool
Float64IsNaN(value)             -> Bool
Float64IsFinite(value)          -> Bool
Float64IsInfinite(value)        -> Bool
```

Arithmetic follows explicitly documented IEEE 754 binary64 behavior. Division by zero produces the corresponding infinity/NaN result rather than borrowing C# exception semantics.

`Float64Equal` follows IEEE numeric equality: NaN does not compare equal to itself and `+0.0` compares equal to `-0.0`. If bitwise identity is later needed, it belongs to the separate exact-bit representation domain rather than an undocumented mode of numeric equality.

Do not introduce implicit FMA. A future fused operation must be a distinct node because it can produce a different rounded result from separate multiply/add nodes.

### NaN policy

Current accepted design direction:

> semantic `Float64` NaN literals canonicalize to one canonical quiet-NaN representation.

The ordinary semantic fact is that a Float64 value is NaN; arbitrary NaN payload bits are not treated as distinct numeric meaning.

This reduces irrelevant persistence and fingerprint variability and keeps Float64 focused on numeric semantics.

If exact NaN payload identity becomes useful later, preserve it in the separate exact-bit representation domain:

```text
Bits64
  -> Float64FromBits64
  -> Float64
```

In that future model, `Bits64` preserves the supplied sixty-four bits exactly while `Float64` preserves the declared IEEE numeric semantics. The two facts do not compete.

Implementation should revalidate the chosen canonical quiet-NaN bit pattern when the Float64 codec/backend slice is built, but arbitrary payload preservation should not be introduced by default without new evidence.

## 5. Numeric conversions

### `Int64 -> Float64`

A future explicit node is reasonable:

```text
Int64ToFloat64(value) -> Float64
```

It is total but may lose integer precision above the exactly representable binary64 range. That precision loss must be documented and must never occur implicitly.

### `Float64 -> Int64`

Do not add this in the first Float64 slice.

The conversion needs explicit answers for:

- NaN;
- positive/negative infinity;
- out-of-range finite values;
- fractional values;
- rounding direction.

A future design will likely need either a result/error form or a node whose name/fields explicitly encode the rounding and failure contract. A bare cast node is rejected as too implicit.

### Numeric conversion versus representation reinterpretation

Numeric conversion and bit reinterpretation are separate operations.

For example:

```text
Int64ToFloat64(value)
```

means numeric conversion and may lose precision.

A future:

```text
Float64ToBits64(value)
Float64FromBits64(bits)
```

would mean exact representation reinterpretation and belongs to the deferred bit-representation design.

Never use one node to ambiguously mean both.

## 6. Future bit-representation domain — outside M1 scope

A future exact-bit type family such as `Bits64` may be useful for masks, rotations, representation-preserving reinterpretation, protocol fields, NaN payload inspection, or similar low-level work.

This is deliberately not required for M1.

Current direction:

- numeric types keep numeric semantics;
- exact bit manipulation gets its own representation semantics;
- reinterpretation between numeric and bit domains is explicit;
- a possible interpretation hint may later help AI/viewer presentation but remains non-authoritative;
- no hint field is added until real agent workflows show a need.

See `m1-bit-representation-design-20260826.md` for the deferred design idea.

## 7. IR and backend consequences

Every accepted semantic node requires a corresponding explicit path through:

```text
Semantic Graph
 -> SemanticFacts type derivation
 -> validation
 -> persistence codecs
 -> Typed Semantic IR
 -> Execution IR
 -> C# backend
 -> Focus / Change Review / Viewer
```

No backend-only implementation counts as adding a language feature.

For lists, the first C# representation may use a simple immutable/generated representation chosen by the backend; that representation is not semantic authority.

For Float64, the C# backend must preserve the declared binary64 contract, the selected canonical NaN policy, and authoritative literal bit patterns. Generated source readability is secondary to deterministic faithful emission.

A future Bits backend must preserve exact bit patterns and cannot reinterpret them based on display hints.

## 8. Agent-facing evaluation gates

Before generalizing numeric or collection nodes, run actual editing tasks through the agent facade.

Candidate tasks:

1. add a count of valid users;
2. add a count of invalid users;
3. filter a list before validation;
4. map validation across the input list;
5. rename or move a predicate function without breaking `ListFilter` references;
6. change a predicate signature and observe explicit obligations;
7. later, add a Float64-derived score without implicit Int64 conversion.

Measure at least:

- invalid semantic operation rate;
- failed simulation rate;
- validation/lowering failure rate;
- retry count;
- unrelated semantic churn;
- tool calls and token use;
- whether the agent selected the correct specialized node without needing whole-program reconstruction.

## 9. Explicit non-goals for the first M1.2 implementation

Do not add in the same slice:

- generic numeric type classes;
- operator overloading;
- implicit numeric promotion;
- arbitrary user-defined generics;
- lambdas or closures;
- mutable lists;
- lazy/infinite sequences;
- parallel collection traversal;
- effectful collection callbacks;
- arbitrary precision decimals;
- generic `Operation(kind, operands)` nodes;
- first-class `BitsN` representation types;
- bit-interpretation hints.

These may become useful later, but none are prerequisites for testing the current AI-first semantic design.

## Recommended implementation sequence after M1 infrastructure

```text
M1-E  Int64 type + literals + comparisons + explicit wrapping arithmetic
M1-F  List<T> type + construction + length/append/concat
M1-G  Pure ListMap/ListFilter/ListAny/ListAll
M1-H  Expanded multi-user validation program + agent-editing scenarios
M1-I  Float64 type + bit-authoritative literal + canonical NaN + IEEE operations
M1-J  Explicit numeric conversion experiments
M1-K  AI editing benchmark and node-taxonomy review
```

A future Bits slice, if justified, belongs after these M1 goals rather than being pulled into the milestone opportunistically.

Phase lettering is provisional and should be reconciled with whichever M1 infrastructure phases have landed by implementation time.
