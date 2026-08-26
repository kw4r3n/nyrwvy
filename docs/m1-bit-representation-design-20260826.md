# Future bit-representation domain — design direction — 2026-08-26

## Status

Post-M1 development idea. This is **not** a Milestone 1 implementation requirement and does not add any semantic node or type yet.

The purpose of this note is to avoid coupling future bit manipulation to `Int64` or `Float64` merely because those values have machine representations.

## Core direction

If Nayamnyam needs first-class bit manipulation, introduce a dedicated fixed-width representation domain rather than adding bitwise behavior directly to numeric semantic types.

Candidate types:

```text
Bits8
Bits16
Bits32
Bits64
Bits128
```

The exact initial width set remains deferred. A first implementation should add only widths required by concrete use cases.

A value of `Bits64` means exactly sixty-four bits. It does **not** intrinsically mean:

- signed integer;
- unsigned integer;
- IEEE 754 binary64;
- pointer/address;
- character encoding;
- protocol field;
- arbitrary numeric magnitude.

This keeps representation semantics separate from numeric semantics.

## Why this is separate from `Int64` and `Float64`

Nayamnyam should distinguish:

```text
Int64    signed 64-bit integer semantics
Float64  IEEE 754 binary64 numeric semantics
Bits64   exact 64-bit representation semantics
```

This prevents operations such as masks, shifts, rotations, and raw bit reinterpretation from making `Float64` or `Int64` carry two different semantic roles.

The distinction also gives an agent a strong local signal. Seeing `Bits64Xor` means exact bit manipulation; seeing `Float64Add` means floating-point arithmetic. No ambient type convention is required to decide which interpretation is intended.

## Candidate operations

If introduced later, prefer role-specific fixed-width node kinds such as:

```text
Bits64And(left, right)          -> Bits64
Bits64Or(left, right)           -> Bits64
Bits64Xor(left, right)          -> Bits64
Bits64Not(value)                -> Bits64
Bits64ShiftLeft(value, count)   -> Bits64
Bits64ShiftRightLogical(value, count) -> Bits64
Bits64RotateLeft(value, count)  -> Bits64
Bits64RotateRight(value, count) -> Bits64
```

The exact shift-count contract must be explicit before implementation. Do not inherit masking/trapping behavior from a backend language implicitly.

A future generic `Bits<N>` abstraction should not be introduced solely to reduce implementation duplication. Fixed-width constructs may remain preferable if they are easier for agents to classify and validate.

## Explicit reinterpretation boundary

Numeric values should cross into the bit-representation domain through explicit nodes rather than hidden casts.

Candidate future nodes:

```text
Float64ToBits64(value) -> Bits64
Float64FromBits64(bits) -> Float64

Int64ToBits64(value) -> Bits64
Int64FromBits64(bits) -> Int64
```

These nodes represent bit reinterpretation, not numeric conversion.

For example, `Float64FromBits64` interprets the supplied sixty-four bits as an IEEE 754 binary64 encoding. `Int64FromBits64` interprets them as the two's-complement bit pattern of a signed 64-bit integer. No arithmetic rounding or value conversion occurs.

If numeric conversion is desired instead, it remains a different semantic node such as the planned explicit `Int64ToFloat64`.

## Interpretation hints — deferred, non-authoritative idea

A bit value may eventually benefit from an explanatory hint indicating what its bits are expected to represent in the surrounding workflow.

Possible projection/query metadata might look conceptually like:

```text
BitInterpretationHint
  Float64Encoding
  Int64TwosComplement
  ProtocolField(...)
  Opaque
```

This is deliberately **not** part of the current semantic design.

If such hints are introduced:

- they must not change the meaning of the underlying `BitsN` value;
- they must not be required for validation of ordinary bit operations;
- they must not become a second authoritative type fact;
- changing or removing a hint must not silently reinterpret the bits;
- the actual reinterpretation must still be represented by explicit semantic nodes.

The likely role is AI/viewer assistance: a retrieved `Bits64` node could be displayed with a best-known provenance or interpretation hint without making that hint semantic authority.

Do not add hints until real use shows that agents repeatedly lose useful representation context without them.

## Relationship to Float64 NaN

A separate exact-bit domain makes the Float64 literal policy simpler.

Current preferred semantic direction:

> `Float64` treats NaN as numeric floating-point state and canonicalizes semantic NaN literals to one canonical quiet-NaN representation.

Arbitrary NaN payload identity is therefore not required inside ordinary `Float64` semantic values.

If exact payload bits become relevant, they belong in the representation domain:

```text
Bits64
  -> Float64FromBits64
  -> Float64
```

This separates two questions that otherwise become entangled:

- numeric meaning: "this value is NaN";
- representation identity: "these are exactly the sixty-four supplied bits".

The first belongs to `Float64`; the second belongs to `Bits64`.

## Fingerprinting and persistence

If `BitsN` is introduced, its authoritative value is the exact bit sequence.

Therefore:

- canonical persistence preserves every bit exactly;
- semantic fingerprinting includes the exact bit value;
- non-authoritative interpretation hints, if they later exist, are excluded from semantic fingerprinting;
- conversion/reinterpretation nodes are explicit graph structure and therefore affect semantic fingerprinting normally.

## Non-goals

This note does not commit M1 to:

- any `BitsN` type;
- arbitrary-width bit vectors;
- unsigned integer semantics;
- bit-field schema support;
- SIMD/vector values;
- cryptographic primitives;
- packet/protocol modeling;
- interpretation hints;
- generic `Bits<N>` type parameters.

Those should be driven by concrete use cases and agent-editing evidence.

## Decision summary

For future work:

1. numeric types represent numeric semantics;
2. exact raw bit manipulation gets a dedicated representation domain;
3. reinterpretation between the domains is explicit;
4. interpretation hints are a possible non-authoritative AI/viewer aid, not current semantic state;
5. no Bits implementation is required for Milestone 1.
