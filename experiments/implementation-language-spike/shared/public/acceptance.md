# Public acceptance contract

The selected baseline is accepted only when its observable behavior matches
`semantic-contract.md`, its public checks pass, and it satisfies the command
surfaces below. Acceptance is language-neutral: the isolated trial workspace
contains the selected baseline at its root and the public contract under
`shared/public`.

## Required behavior

- Parse every valid snapshot described by
  `shared/public/schema/snapshot-v1.schema.json`.
- Reject stable IDs with invalid domains, empty tails, or colon-containing
  atomic tails; reject stale base snapshots and unknown JSON properties without
  producing a candidate snapshot.
- Implement the operation surface required by the applicable acceptance mode
  with the specified transaction results, stable-ID derivation, immutable input
  snapshots, blocking obligations, and semantic diffs.
- Preserve function parameter order and call argument order.
- Sort all snapshot-level ID-keyed collections by ordinal, case-sensitive
  stable-ID text.
- Emit canonical snapshots and summaries deterministically as UTF-8 without a
  byte-order mark, two-space indentation, LF line endings, and exactly one
  trailing LF.

The object-property and collection orders in `semantic-contract.md` are part of
the observable acceptance contract, not formatting suggestions.

## Acceptance modes

Acceptance uses the mode appropriate to the implementation state. These modes
are intentionally distinct so the schema v2 migration does not weaken or
silently replace the untouched-baseline check.

### Untouched baseline mode

Before the maintenance task, the writer is a v1 writer. For the public initial
fixture, `canonical` must emit bytes identical to
`shared/public/fixtures/initial-snapshot-v1.json`. Byte comparison with the v1
fixture or copied `initial-canonical.json` is required in this mode.

The untouched baseline operation surface is `renameFunction` for functions and
`addParameter`, with the v1 semantics defined in `semantic-contract.md`.

`summary` must emit:

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

### Post-maintenance mode

After all four maintenance phases, the implementation must still read the
public v1 fixture, but phase 4 makes the writer v2-only. `canonical` must
therefore emit canonical schema v2 JSON, not bytes identical to the v1 fixture.
Schema-aware checks must parse that output and verify:

- `schema_version` is `2`;
- the snapshot ID and the semantic content and order of `types`, `functions`,
  `calls`, and `obligations` are preserved;
- every native v2 obligation has its creating transaction ID in
  `origin_transaction_id`;
- every obligation migrated from v1 has
  `origin_transaction_id` equal to `transaction:migrated`; and
- canonical formatting and ordering remain deterministic.

The post-maintenance operation surface is `renameSymbol` for both functions and
types, plus `addParameter`. The former function-only rename entry point has been
replaced and is not required in this mode. All cumulative maintenance semantics,
including `NoChange`, type-reference preservation, conflict and missing-argument
obligations, and semantic-diff ordering, remain required.

For the obligation-free public initial fixture, the post-maintenance summary
must emit:

```json
{
  "schema_version": 2,
  "snapshot_id": "snapshot:initial",
  "type_count": 3,
  "function_count": 2,
  "call_count": 1,
  "obligation_count": 0
}
```

Post-maintenance evaluation is schema-aware. It must not compare v2 canonical
output to the v1 fixture or v1 `initial-canonical.json` bytes. If a copied
schema-appropriate v2 expected artifact exists, a byte comparison with that v2
artifact may additionally be performed.

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

Every command must exit successfully and be evaluated in the applicable mode
above. The evaluator does not require `shared/expected` to be visible in a
future trial workspace.

## Forbidden escape hatches

An implementation is not accepted if it changes anything under
`shared/public`, changes the maintenance task or fixture, deletes or bypasses
tests, weakens compiler settings, suppresses warnings, adds third-party
packages, uses network access, hard-codes output only for the public fixture, or
mutates an existing snapshot. A passing public fixture does not override any
semantic requirement; hidden evaluation covers additional valid inputs and
edge cases.
