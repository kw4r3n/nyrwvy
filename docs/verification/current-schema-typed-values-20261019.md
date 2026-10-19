# Current persistence and typed scalar run values — 2026-10-19

This bounded code slice follows the separate [copy-migration checkpoint](legacy-cutover-copy-migration-20261019.md), commit `2826f2985c2e566fa891f9cd7c24923dd652a037`. It does not apply external consumer updates or retire any reader. The local freeze tag `archive/legacy-examples-pre-cutover-20261019` still points to `0913a1b6db48386301a638d7a4d3c75f47832e4a`.

## Changes

Ordinary `SnapshotCodec.serialize`, `serializeResolved` and `NyasonCodec.serialize` now write JSON 21 / Nyason v17, including all-exported graphs whose function type parameters follow the implied order. Migration and later ordinary saves therefore keep the explicit current form. Embedded retained dependency content uses the same public writer.

The v6 snapshot and semantic fingerprints retain their minimum-version canonical encoding behind internal helpers. The existing dependency digest calculation is unchanged. Persisted bytes can change without invalidating an existing fingerprint or dependency pin. Legacy schema decoding and its construct gates remain supported; minimum-version tests now create explicit internal fixtures rather than depending on ordinary saves to produce old versions.

`runPure` and `runPureByName` accept typed scalar Lists, Options and Results with tagged payloads, including inside Products, Unions and other containers, in expectations, and in fake case arguments/responses. Valid String/Bool/Int64 containers normalize to the existing scalar representation before execution/comparison. Responses, expected values and call records preserve their existing scalar shapes. Incorrect child types still produce located diagnostics; mixed legacy/typed metadata is still a protocol error. Existing depth, size and secret-redaction checks remain.

No compatibility decision from the owner is required for this additive input support. Removing legacy output forms would be a separate breaking API decision with downstream updates and evidence. This slice deliberately leaves that decision open.

## Focused evidence

Release .NET checks select fast cases from 35 changed/compatibility test modules and all cases from five run-value modules. They cover codecs and schema gates, frozen v6 hashes, migration rollback/identity/pins, dependency recovery and retained content, declaration visibility/type parameters, nested values, fake dispatch and generated runtime execution. The new identity runs compare typed/legacy inputs and expectations in both directions for all three scalar kinds, empty/nonempty Lists, Some/None, Ok/Err, distinct Result side types, Unicode, Int64.MinValue and nested Option values. The post-migration save/reopen test keeps an imported digest and reopens retained content after deleting only its temporary test source.

The final selection passes **302/302**. The existing Extended migration/runPure parity test passes **1/1** separately. Node fast contracts pass **39/39** after a cached, locked dependency install with lifecycle scripts disabled. Both external original snapshot hashes still match the preceding inventory. No broad unrelated suite or live provider was run. Earlier focused failures were fixture expectations tied to old ordinary-writer versions and typed-scalar rejection; those fixtures now explicitly test legacy bytes or incorrect payloads, preserving the negative checks.

Local task evidence is in `current-values-evidence/selection.json`, the final TRX and `current-values-focused.log`, plus `current-values-evidence/node-contracts.log`. The earlier copy-only example/oracle evidence remains in the preceding checkpoint; it is not presented as a fresh run of this code branch.

## Remaining gate

Known external originals and their legacy source-only pin remain unchanged. The copy-migration preparation and its freeze tag remain separate local refs. Unknown downstream consumers retain current legacy reader/input/output support. Reader retirement, external application and publication need their own reviewed boundary; none is established by these additive changes. Two-file migration rollback is still not crash-atomic.
