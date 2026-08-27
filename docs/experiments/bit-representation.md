# Exact-bit representation experiment

**Status:** Proposed

NyamNyam does not currently expose first-class exact bit manipulation. This experiment records the preferred direction if a concrete use case eventually requires it.

## Direction

Keep representation semantics separate from numeric semantics. Rather than adding masks, shifts, rotations, and raw reinterpretation directly to `Int64` or `Float64`, introduce a dedicated fixed-width bit domain when evidence justifies it.

Candidate forms include:

```text
Bits8
Bits16
Bits32
Bits64
Bits128
```

A `Bits64` value would mean exactly sixty-four bits, without intrinsically meaning signed integer, IEEE 754 binary64, pointer, character data, or protocol field.

## Why it remains separate

The distinction should stay explicit:

```text
Int64    signed 64-bit integer semantics
Float64  IEEE 754 binary64 numeric semantics
Bits64   exact 64-bit representation semantics
```

If added, conversion across this boundary should use explicit reinterpretation nodes such as `Float64ToBits64` / `Float64FromBits64` rather than hidden casts.

Do not introduce a generic `Bits<N>` abstraction, interpretation-hint metadata, or a broad operation family solely to reduce implementation duplication. Add only the widths and roles that concrete agent/program workflows require.

## Current consequence

Arbitrary NaN payload identity is not part of ordinary `Float64`; semantic NaN literals canonicalize. If exact payload bits become important, that is evidence for a bit-representation domain rather than a reason to give `Float64` two semantic roles.

## Historical design record

The full dated design exploration is preserved in the [pre-reorganization archive](../../docs-archive/2026-08-pre-reorg/m1-bit-representation-design-20260826.md).
