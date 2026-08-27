# F# Milestone 0 rebuild foundation — 2026-08-25

## Purpose

This branch restarts Milestone 0 from the last documentation-only state before the discarded C# semantic-core foundation was introduced.

The rebuild keeps the accepted graph-first architecture and keeps C#/.NET as the first **execution backend**, while implementing the semantic core in F#.

The accepted F# implementation-language decision is restored from the existing `feat/fsharp-semantic-core-bootstrap` branch. That branch remains useful design and implementation evidence, but its larger `Domain.fs` and `Transactions.fs` implementation is not copied wholesale into this clean rebuild.

## Why rebuild instead of port

The discarded C# foundation exposed architecture-level failure modes that should not be translated into F#:

- the same direct call could be represented independently in both a function call list and a function body;
- transactions could reconstruct records while accidentally dropping unrelated semantic state;
- lowering could compensate for missing type information by guessing a String representation;
- validation, query projection, transaction mutation, and executable lowering could observe different representations of the same program fact.

These are treated as negative design evidence, not as implementation to preserve.

## Rebuild invariants

1. **One authoritative representation per semantic fact.** Derived indexes may duplicate information only as reproducible caches or projections.
2. **Transactions preserve all untouched semantic state.** Adding a new field must not make existing operations silently discard it.
3. **No semantic guessing.** Missing or unresolved information blocks the stage that requires it; it is never defaulted to a convenient type or value.
4. **Typed IR means complete typed information for its accepted subset.** Optional fields must represent semantic optionality, not implementation uncertainty.
5. **Validation precedes lowering.** Only snapshots explicitly proven execution-ready may enter executable lowering.
6. **End-to-end invariants cross every layer.** Transaction review, queries, persistence, and execution must agree on the same semantic facts.
7. **Backend choices stay downstream.** The F# semantic core must not encode C# object-model choices; generated C# remains a build artifact.
8. **Milestone claims require executable evidence.** A document may mark an implementation question resolved only after the accepted F# path and its tests demonstrate it.

## Initial implementation boundary

The branch intentionally starts smaller than both discarded implementation branches.

Phase A:

- F# solution and CI;
- domain-typed stable IDs;
- minimal immutable snapshot envelope;
- tests proving rename/display-name independence and absence of implicit defaults.

Phase B:

- canonical snapshot persistence;
- graph validator;
- rename transaction;
- semantic diff;
- round-trip and transaction-state-preservation tests.

Phase C:

- one authoritative function-body/call representation;
- `function.add_parameter`;
- typed `MissingArgument` obligation and resolution;
- execution-readiness state.

Only after Phase C is internally consistent should the branch add:

- Typed Semantic IR;
- backend-neutral Execution IR;
- deterministic C# generation;
- viewer projections.

## Explicit non-goals for the foundation commit

- no C# emitter yet;
- no viewer yet;
- no attempt to migrate either discarded implementation wholesale;
- no schema version claimed before persistence and validation rules are accepted;
- no production stable-ID allocator before Q-002 is resolved;
- no convenience fallback for incomplete semantic information.

## Relationship to existing decisions

D-001 through D-014 remain the architectural baseline. D-008 still defines C#/.NET as the first execution backend. D-015, restored from the existing F# bootstrap branch, defines F# as the initial semantic-core implementation language.

## Next implementation slice

The next branch commit should implement Phase B only. It should not add function bodies, lowering, the viewer, or C# emission in the same change. Before Phase B is considered complete, tests should prove that canonical persistence round-trips every authoritative field and that a rename transaction preserves byte-equivalent untouched semantic state after canonicalization.
