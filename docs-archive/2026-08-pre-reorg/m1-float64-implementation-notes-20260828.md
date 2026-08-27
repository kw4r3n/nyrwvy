# M1 Float64 implementation contract — 2026-08-28

## Status

Implementation note for the Milestone 1 Float64 vertical slice.

Compiler/runtime verification completed successfully on exact feature head `79fccfba86c1c00add0a9c50248c55336e7c563b` with .NET SDK `10.0.111`.

Verification result:

- Release build: 0 warnings, 0 errors;
- full test suite: 158 passed, 0 failed, 0 skipped;
- M1-J `Float64Tests`: 8 passed, 0 failed;
- local verifier: `VERIFICATION PASSED`;
- verification log: `local-verify-20260828-061106.log`.

The verified feature content was squash-merged into `rebuild/fsharp-m0-20260825` as `54c26d1dbfc615ef60b00a714552004cf191fefe`.

## Semantic contract

`Float64` means IEEE 754 binary64 numeric semantics.

The initial slice provides:

```text
Float64Literal(bits)
Float64Add(left, right)
Float64Subtract(left, right)
Float64Multiply(left, right)
Float64Divide(left, right)
Float64Equal(left, right)
Float64LessThan(left, right)
Float64IsNaN(value)
Float64IsFinite(value)
Float64IsInfinite(value)
```

No implicit numeric conversion or fused multiply-add is introduced by this slice.

## Literal authority

The authoritative literal representation is the exact 64-bit IEEE binary64 encoding, represented canonically as sixteen lowercase hexadecimal digits in persistence and IR serialization.

This preserves distinctions such as:

- positive zero versus negative zero;
- finite values versus infinities;
- subnormal encodings;
- exact finite binary64 values.

Human decimal rendering is a projection only.

## NaN policy

All semantic NaN literals canonicalize to the quiet-NaN bit pattern:

```text
7ff8000000000000
```

NaN payload bits and NaN sign are therefore not distinct `Float64` semantic facts.

If exact NaN payload identity becomes useful, it belongs in the deferred exact-bit representation domain (`Bits64`) rather than in ordinary Float64 numeric semantics.

## Persistence and fingerprints

The Float64 slice advances the relevant serialization/fingerprint domains:

- canonical snapshot JSON schema: v8;
- Nyason snapshot schema: `nayamnyam.snapshot.v4`;
- Typed Semantic IR codec: v5;
- Execution IR codec: v5;
- snapshot fingerprint domain: v5;
- semantic fingerprint domain: v5.

Signed zero remains fingerprint-significant because its binary64 encoding is authoritative. Distinct NaN payload inputs become fingerprint-equivalent after canonicalization.

## Backend contract

The C# backend emits Float64 as `double`.

Literal construction uses exact bits rather than decimal source text:

```text
BitConverter.UInt64BitsToDouble(0x<16 hex digits>UL)
```

Arithmetic and comparisons use ordinary `double` operators, preserving the intended IEEE binary64 behavior for the supported operations. Classification uses:

```text
double.IsNaN
double.IsFinite
double.IsInfinity
```

In particular, binary64 division by zero is not converted into an integer-style exception contract.

## Verification gate

`Float64Tests` covers at least:

- exact signed-zero/infinity bit preservation;
- NaN canonicalization;
- semantic type/dependency and viewer projection;
- operand validation;
- JSON and Nyason round trips;
- Typed Semantic IR and Execution IR projection/serialization;
- exact-bit C# source emission;
- generated C# compile/run behavior for arithmetic, division, equality, comparison, and classifiers;
- semantic fingerprint behavior for signed zero and NaN payload canonicalization.

The local verifier runs `Float64Tests` explicitly as the M1-J gate and requires at least eight passing tests.
