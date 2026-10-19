# Legacy cutover: copy migration and maintained examples

Base: merged main `0913a1b6db48386301a638d7a4d3c75f47832e4a` (PR428).
This checkpoint prepares roadmap step 2. Legacy schema readers, retired Core
node kinds, `pin`, `FailureType` and scalar `runPure` forms remain available.
No production runtime or external original was changed, and this record does
not authorize publication or reader removal.

## Inventory and preservation

The tracked-file inventory found ten persisted root snapshots: three maintained
snapshots and seven in the historical Member/offline series (including four gzip
inputs). JSON proof/observation records were not counted as snapshots. No
tracked Nyason snapshot was found. The known external program root contained
two more snapshots and one legacy source-only dependency manifest.

All twelve snapshots were first migrated on copies using the clean base's
`Nyrwvy.AuthoringSession migrate`. Each result validated, kept every existing
stable ID, reached schema 21 and was byte-identical on a second migration.
The source byte hashes were preserved. The seven historical snapshots contain
retired graph nodes: their rewrites intentionally change structural/semantic
fingerprints; validation is not a new replay of their historical Python/binary
oracles. The five maintained/external snapshots were representation-only
migrations with equal before/after snapshot and semantic fingerprints.

| Maintained snapshot | Source schema | Target | Behavior check |
| --- | --- | --- | --- |
| `examples/verification-planner/planner.snapshot.json` | 9 | 21 | 1,024 exhaustive maintained cases |
| `examples/mini-work-planner/planner.snapshot.json` | 9 | 21 | 13 policy cases |
| `examples/task-cancellation-outcome/fixtures/task-outcomes.json` | 17 | 21 | Migrated fixed-identity snapshot: 12 virtual / 44 real compiled controls |

The migrated copies replace these three maintained files; each has an adjacent
`.migration.json` report. Only its displayed `path` is normalized to a repository
relative path; original stdout reports remain with the local evidence. Generated
planner C# was re-emitted, never hand-edited. The cancellation fixture's historical
hash-authenticated proof records remain frozen rather than being relabeled;
`fixtures/current-proof.json` records fresh migrated-snapshot controls.

## External consumers

Both explicitly documented consumers were located:

- [AI Task Router](https://github.com/kw4r3n/nyrwvy-ai-task-router): schema-9
  `ai-task-policy.nyrwvy.json`; migrated copy passed its existing generated-C#
  runner's 32/32 policy cases with the same snapshot/declaration/node IDs.
- [Batch Task Planner](https://github.com/kw4r3n/nyrwvy-batch-task-planner): schema-9
  `planner.nyrwvy.json` and a source-only pin to the policy above; its copied
  sidecar became embedded schema-21 content, with equal fingerprints. Its existing
  runner reopened the migrated copy and passed 3/3 batch tasks.

External originals and their sidecar remain byte-identical at their existing
locations. The migrated copies and original backups are retained only in the
local `cutover-evidence/external/` packet; they are not installed into the original
consumer repositories. No known documented consumer was unlocated. Discovery
covered the known external program root plus name-based searches of local Tools
and Projects; it is not an exhaustive registry of every scratch snapshot, other
machine or downstream user. Consumers outside that scope remain unknown.

## Historical examples and active checks

Tag `archive/legacy-examples-pre-cutover-20261019` pins base `0913a1b6` with the
original examples, authenticated input packs, proof records and compatible reader.
Every removed tracked file was checked against that frozen tree. The Member,
Offline Leader and offline-session chain and their owning test entry points are
excluded from the maintained set; historical verification links target the tag.
Publish the freeze tag together with any reviewed branch publication.

Kept authoring scripts use `ValueEqual`, swapped/negated `Int64LessThan`, and
`OptionGetOrDefault(MapGet(...))` replacements as applicable. The file-backed
batch's dependency function is explicitly exported under the visibility rule.
Fresh checks passed:

- 29 manual source/compiler/oracle tests for the three stream/collector examples;
- fresh author, save/reopen replay and real generated-runtime checks for bounded
  stream, bounded worker stream, dynamic collector and scoped result handoff;
- the function-abort scenarios; file-backed batch v2 and signature evolution;
  the real change-verification batch;
- three maintained integration tests covering task observation, exception boundary
  and cleanup shield; the migrated cancellation controls listed above;
- 39 Node fast contracts after removing the frozen retained-input-pack tests.

The tool built in Release with zero warnings/errors. Product assemblies and their
unit tests were unchanged; the PR428 exact-tree .NET evidence is reused, not
presented as a new broad run. The first fast-contract attempt lacked the owning
workspace's nested MCP SDK; installing the cached locked dependencies with scripts
disabled resolved it, and the final 39/39 passed. No live models/providers were used.

## Reader-retirement gate

Global legacy-reader removal is pending, not established by this checkpoint:

1. `SnapshotCodec.serialize` still writes the minimum schema. A later ordinary
   save can produce schema 9–20 again even after a one-time migration. A coordinated
   writer/fingerprint/dependency transition needs its own regression evidence.
2. `PureRun` currently requires flat scalar List/Option/Result forms; typed
   containers are restricted to other payload types. The maintained fixtures
   still use that accepted contract. Replacing it requires a coordinated
   wire/runtime conversion and behavioral coverage, not mechanical fixture edits.
3. Known external consumers have validated migrated copies, while their original
   repositories still contain legacy snapshots/pins. Applying reviewed consumer
   updates and checking their supported entry points remains separate from these
   copy-only checks. Unknown external consumers must receive a useful migration
   diagnostic and a maintained legacy-input migration path.

Keep legacy support until these boundaries are handled together; do not call this
a completed breaking cutover. Migration's existing two-file rollback remains
non-crash-atomic. The bounded Ponytail review favored the existing migration and
runner paths over a new compatibility framework or silently rewriting old proofs.

## Subsequent bounded code slice

The [writer/value follow-up](current-schema-typed-values-20261019.md) resolves
items 1 and the typed-input portion of item 2 above on a separate branch based on
this checkpoint. It preserves legacy response shapes and readers. The external
originals, unknown downstream support and reader-retirement gate remain open.
