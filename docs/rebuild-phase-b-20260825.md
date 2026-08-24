# F# Milestone 0 rebuild — Phase B candidate — 2026-08-25

## Status

Prepared for local build and test verification. This document does **not** mark any Open Question resolved yet.

Phase B intentionally stops before function bodies, calls, obligations, lowering, C# emission, or viewer work.

## Candidate scope

- explicit canonical JSON snapshot codec for the current minimal authoritative graph;
- schema-version rejection and unknown-field rejection;
- graph validation for duplicate IDs, self-parent snapshots, and blank display names;
- pure rename transaction simulation against an explicit base snapshot;
- explicit proposed snapshot ID supplied by the transaction request;
- net semantic rename diff;
- tests that compare the proposed snapshot with an F# record-update expectation so untouched fields are preserved automatically;
- canonical round-trip tests over every current authoritative snapshot field.

## Deliberate constraints

- No production stable-ID allocator is introduced; Q-002 remains open.
- Schema version `1` is a candidate codec version for this rebuild branch, not yet an accepted repository-wide schema decision.
- Snapshot ordering is canonicalized by opaque stable-ID value for persistence only; serialized order is not semantic identity.
- Rename changes display metadata only. IDs remain unchanged.
- Transactions use F# record updates so future snapshot fields are preserved by default unless an operation explicitly changes them.
- Invalid or incomplete information is rejected; no default semantic value or type is invented.

## Local verification gate

Run from a clean checkout of `rebuild/fsharp-m0-20260825` with the .NET 10 SDK:

```shell
dotnet restore Nayamnyam.slnx
dotnet build Nayamnyam.slnx --configuration Release --no-restore
dotnet test Nayamnyam.slnx --configuration Release --no-build --no-restore
```

Phase B should not be treated as accepted until all three commands succeed with warnings treated as errors.

If compilation exposes F# type-inference or overload-resolution issues, fix only the Phase B implementation and tests. Do not broaden scope into function bodies, call modeling, obligations, IR, viewer, or backend code merely to make the build pass.

## After the local gate

If the gate passes:

1. record the verified commands and .NET SDK version;
2. decide whether Q-001 can be marked resolved for the current Milestone 0 subset or should remain a provisional storage decision;
3. begin Phase C by designing one authoritative function-body/call representation before writing transaction logic for `function.add_parameter`.
