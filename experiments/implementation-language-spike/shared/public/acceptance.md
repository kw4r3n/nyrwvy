# Public acceptance contract

The selected baseline is accepted only when its observable behavior matches
`semantic-contract.md`, its public checks pass, and it satisfies the command
surfaces below. Acceptance is language-neutral: the isolated trial workspace
contains the selected baseline at its root and the public contract under
`shared/public`.

## Required v1 behavior

- Parse every valid snapshot described by
  `shared/public/schema/snapshot-v1.schema.json`.
- Reject invalid stable-ID domains, stale base snapshots, and unknown JSON
  properties without producing a candidate snapshot.
- Implement `renameFunction` and `addParameter` with the transaction results,
  stable-ID derivation, immutable input snapshots, blocking obligations, and
  semantic diffs defined in `semantic-contract.md`.
- Preserve function parameter order and call argument order.
- Sort all snapshot-level ID-keyed collections by ordinal, case-sensitive
  stable-ID text.
- Emit canonical snapshots and summaries deterministically as UTF-8 without a
  byte-order mark, two-space indentation, LF line endings, and exactly one
  trailing LF.

For the public initial fixture, `canonical` must emit bytes identical to
`shared/public/fixtures/initial-snapshot-v1.json`. `summary` must emit:

```json
{
  "schema_version": 1,
  "snapshot_id": "snapshot:initial",
  "type_count": 3,
  "function_count": 2,
  "call_count": 1,
  "obligation_count": 0
}
```

The object-property and collection orders in `semantic-contract.md` are part of
the observable acceptance contract, not formatting suggestions.

## Required public commands

Run exactly one solution build for the selected baseline:

```bash
dotnet build Nayamnyam.Spike.FSharp.sln
```

or:

```bash
dotnet build Nayamnyam.Spike.CSharp.sln
```

Then run the shared console tests:

```bash
dotnet run --project tests/Nayamnyam.Spike.Tests
```

Exercise both CLI output surfaces:

```bash
dotnet run --project src/Nayamnyam.Spike.Cli -- canonical shared/public/fixtures/initial-snapshot-v1.json
dotnet run --project src/Nayamnyam.Spike.Cli -- summary shared/public/fixtures/initial-snapshot-v1.json
```

Every command must exit successfully. If the isolated workspace includes a
copied expected artifact, compare CLI output byte-for-byte with that artifact.
The evaluator does not require `shared/expected` to be visible in a future
trial workspace.

## Forbidden escape hatches

An implementation is not accepted if it changes anything under
`shared/public`, changes the maintenance task or fixture, deletes or bypasses
tests, weakens compiler settings, suppresses warnings, adds third-party
packages, uses network access, hard-codes output only for the public fixture, or
mutates an existing snapshot. A passing public fixture does not override any
semantic requirement; hidden evaluation covers additional valid inputs and
edge cases.
