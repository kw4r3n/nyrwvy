# F# Milestone 0 rebuild — Phase B — 2026-08-25

## Status

**Verified for the current Phase B scope.** The exact implementation tree was built and tested through GitHub Actions on 2026-08-25 using .NET SDK `10.0.400`.

Verification commands:

```shell
dotnet restore NyamNyam.slnx
dotnet build NyamNyam.slnx --configuration Release --no-restore
dotnet test NyamNyam.slnx --configuration Release --no-build --no-restore
```

Observed result:

```text
restore: success
build:   success, 0 warnings, 0 errors
tests:   13 passed, 0 failed, 0 skipped
```

The successful validation run used the same repository tree as the authoritative `rebuild/fsharp-m0-20260825` Phase B implementation. A temporary branch and draft PR were used only to provide a conflict-free GitHub Actions merge candidate against the current `main`; they are not part of the rebuild history.

Local reproduction with the .NET 10 SDK remains useful as an independent check, but there is no remaining Phase B fact that requires a local-only environment to establish.

This verification does **not** mark any Open Question resolved automatically. In particular, Q-001 and Q-002 remain open until their design consequences are reviewed explicitly.

## Implemented scope

- explicit canonical JSON snapshot codec for the current minimal authoritative graph;
- schema-version rejection, unknown-field rejection, and duplicate-property rejection;
- graph validation for duplicate IDs, self-parent snapshots, and blank display names;
- pure rename transaction simulation against an explicit base snapshot;
- explicit proposed snapshot ID supplied by the transaction request;
- net semantic rename diff;
- tests that compare the proposed snapshot with an F# record-update expectation so untouched fields are preserved automatically;
- canonical round-trip tests over every current authoritative snapshot field.

## Deliberate constraints

- No production stable-ID allocator is introduced; Q-002 remains open.
- Schema version `1` is the working codec version for this rebuild branch, not yet an accepted repository-wide persistence decision.
- Snapshot ordering is canonicalized by opaque stable-ID value for persistence only; serialized order is not semantic identity.
- Rename changes display metadata only. IDs remain unchanged.
- Transactions use F# record updates so future snapshot fields are preserved by default unless an operation explicitly changes them.
- Invalid or incomplete information is rejected; no default semantic value or type is invented.
- The snapshot validator proves only invariants available from one snapshot. Repository-history constraints such as collision with an older snapshot ID belong to the future store/repository boundary rather than being guessed here.

## Evidence learned during verification

The real F# compiler boundary caught several test-only inference and parsing issues while the semantic core itself remained structurally sound. Those findings were resolved without broadening the semantic model:

- interpolated-string expressions were simplified for F# parser compatibility;
- record literals with identical field shapes received explicit semantic type annotations;
- boolean invariant checks remain ordinary F# conditions rather than depending on ambiguous xUnit overloads;
- duplicate JSON property names were added to the explicit rejection policy during review.

The important architectural property held: no compile fix required adding fallback semantics, duplicating graph facts, or weakening validation.

## Phase B exit condition

Phase B is complete for the rebuild branch when all of the following remain true:

1. every current authoritative snapshot field round-trips through the explicit codec;
2. canonical output is deterministic for equivalent current snapshots;
3. malformed or unsupported persistence data is rejected rather than partially accepted;
4. rename preserves stable identity;
5. rename preserves every untouched snapshot fact;
6. stale-base and invalid-target requests are rejected without mutation or invented entities;
7. the solution builds with warnings as errors and the Phase B test suite passes.

The verified tree satisfies these conditions for the current minimal graph.

## Next design gate — Phase C

Do not proceed directly to lowering or the viewer.

Before Phase C implementation, define one authoritative representation for function bodies and calls. The design must make it impossible for query projection, transaction mutation, validation, and later lowering to observe independent copies of the same call fact.

Only after that representation is reviewed should Phase C add:

- function bodies and direct calls;
- `function.add_parameter`;
- typed `MissingArgument` obligations and resolution;
- execution-readiness state.

Typed Semantic IR, backend-neutral Execution IR, deterministic C# generation, and viewer projections remain after Phase C is internally consistent.
