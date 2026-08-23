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

The current bootstrap establishes:

- typed stable IDs, authoritative value entities, and immutable snapshots;
- duplicate function, parameter, and call ID validation plus call-edge integrity checks;
- functions with parameter lists and calls whose arguments are keyed by
  parameter identity;
- immutable base references containing snapshot ID, revision, and commit
  transaction identity;
- transactional function rename with stale-base, reused-snapshot, and no-change
  handling;
- transactional parameter addition and MissingArgument resolution with provenance;
- collision-free deterministic MissingArgument obligations for calls affected by
  a new parameter;
- Executable versus Incomplete classification with explicit readiness transitions;
- semantic-diff output and dependency-free executable tests on .NET 10.

The bootstrap validates value identity but deliberately defers value typing and
type compatibility until the typed semantic-model slice.

It remains deliberately smaller than Milestone 0. Persistence, broader graph
operations, backend-neutral IR, the read-only viewer, and the C# emitter are
follow-up slices.
