# F# semantic core conventions

The Nayamnyam semantic core is initially implemented in F#. This document records
the coding constraints for the core; it does not define language semantics.

## Scope

The F# core owns the authoritative semantic graph, validation, semantic
operations, transaction results, obligations, semantic diffs, and lowering to
backend-neutral execution IR.

C# remains the first execution target. Generated C# and its runtime adapter are
build artifacts and must not become authoritative inputs.

## Core style

- Use immutable records at public domain boundaries.
- Use discriminated unions for closed semantic states.
- Match every domain union case explicitly. Do not use a wildcard branch to
  absorb unknown domain cases.
- Use named functions and intermediate values instead of custom operators,
  active patterns, or dense point-free pipelines.
- Keep Option and Result explicit at validation boundaries.
- Treat compiler warnings as errors.
- Do not rely on automatic serialization of F# internal types. Canonical
  persistence and IR formats use explicit, versioned codecs.
- Do not expose F# discriminated-union representations directly across a
  C#-facing component boundary. Use a backend contract or canonical IR when a
  separate C# component must consume data.

## Initial bootstrap

The first slice establishes typed stable IDs, immutable snapshots, duplicate-ID
validation, and the transactional symbol.rename behavior for functions. It is
deliberately smaller than Milestone 0 and exists to prove the implementation
shape before adding storage, obligations, IR, or the C# emitter.
