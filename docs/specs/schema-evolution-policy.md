# Schema evolution and migration policy — 2026-08-26

## Status

Accepted policy for Milestone 1 and later schema evolution.

Nyrwvy has multiple serialized/public contracts, but none may silently reinterpret old data. Schema evolution is explicit, versioned, and separated from semantic editing.

## Contracts that require independent versioning

At minimum, treat these as separately versioned contracts:

- authoritative snapshot persistence schema;
- Nyason encoding schema;
- Typed Semantic IR schema (in-process; its JSON output is debug-only, see [IR evolution](#ir-evolution));
- Execution IR schema (in-process; its JSON output is debug-only, see [IR evolution](#ir-evolution));
- agent-facing operation/tool schema;
- durable transaction/audit record schema if introduced;
- viewer artifact schema only if viewer data becomes a machine-consumed durable format.

A shared repository version does not replace schema versions.

## Version identity

Every durable serialized contract has an explicit schema identifier/version.

Examples of the existing style:

```text
nayamnyam.snapshot.v0
nyrwvy.typed-semantic-ir.v1
nyrwvy.execution-ir.v1
```

Exact naming can evolve, but version identity must be carried by the serialized artifact rather than inferred from filename, Git revision, or parser behavior.

## Snapshot schema versions

The snapshot JSON `schemaVersion` and the Nyason `schema` identifier name the same set of persisted constructs and move together. One table, `SnapshotSchema.minimumVersions` in `src/Nyrwvy.Semantics/SnapshotSchema.fs`, gives the minimum version of every construct that an older version cannot hold. Both codecs derive everything from it:

- a writer emits the smallest version that holds every construct the snapshot uses, so a snapshot keeps its version, bytes and fingerprint until it uses something newer;
- a reader rejects a version outside the supported range, and rejects a document whose declared version is lower than the minimum of a construct in it (`<`, for every row).

| JSON `schemaVersion` | Nyason `schema` | Constructs that need at least this version |
| --- | --- | --- |
| 9 | `nayamnyam.snapshot.v5` | Baseline: every construct not listed below. |
| 10 | `nayamnyam.snapshot.v6` | `CapabilityConstruct`. |
| 11 | `nayamnyam.snapshot.v7` | `Abort`. |
| 12 | `nayamnyam.snapshot.v8` | `TaskWait` with a `limit` operand. |
| 13 | `nayamnyam.snapshot.v9` | `Int64CheckedAdd`, `Int64CheckedSubtract`, `Int64CheckedMultiply`; a `MatchUnion` arm that names several variants; `StringToUpper`; `Float64Round`; the `Map<K, V>` type anywhere and every `Map*` node except `MapUnion`; the `Set<T>` type anywhere and every `Set*` node except `SetIsSubset`; `ListMinBy`; `ListLast`, `ListDropLast`; `ListIndexOf`, `ListLastIndexOf`, `ListFindIndex`. |
| 14 | `nayamnyam.snapshot.v10` | `OneShot<T>` anywhere; `OneShotCreate`, `OneShotComplete`, `OneShotWait`. |
| 15 | `nayamnyam.snapshot.v11` | `CancellationShield`. |
| 16 | `nayamnyam.snapshot.v12` | `Exception` anywhere; `ExceptionCatch`, `ExceptionMessage`, `ExceptionRethrow`. |
| 17 | `nayamnyam.snapshot.v13` | `ExceptionIsCancellation`; `StringReplace`, `StringTrim`, `StringTrimStart`, `StringTrimEnd`; `SetIsSubset`, `MapUnion`; `Int64BitAnd`, `Int64BitOr`, `Int64BitXor`, `Int64BitNot`, `Int64ShiftLeft`, `Int64ShiftRight`, `Float64Min`, `Float64Max`; `ListSetAt`, `ListInsertAt`, `ListRemoveAt`; `ListZipWith`, `ListFlatMap`; `OptionGetOrDefault`, `OptionMap`, `OptionAndThen`, `ResultMap`, `ResultMapErr`, `ResultAndThen`. |
| 18 | `nayamnyam.snapshot.v14` | A Product or Union with type parameters, a named type with type arguments, or a construct node with type arguments ([generic types](../decisions/generic-types.md)). |
| 19 | `nayamnyam.snapshot.v15` | `ResultGetOrDefault`, `OptionOkOr`, `ResultOkValue`, `ResultErrValue`, `OptionIsSome`, `ResultIsOk` ([Option and Result accessors](../decisions/option-result-accessors.md)); `ListContains` ([decision](../decisions/list-contains.md)); `StringEncodeUtf8` ([decision](../decisions/string-encode-utf8.md)). |

Versions 9 to 12 keep their earlier meaning. Version 13 collects the constructs added in orders 40 to 53, which were first written as version 9 and are now gated; a snapshot that uses none of them is unchanged. Older versions (JSON 8, Nyason v4 and before) are rejected.

A new persisted construct gets a row in the table, normally with a new version. `SnapshotSchema.featureOfNodeKind` matches every `SemanticNodeKind` without a wildcard, so a new node kind does not compile until it is assigned a version or explicitly left at the baseline. `SnapshotSchemaTests` writes one snapshot per row, checks that it is written at the row's version, and checks that both codecs reject it under the version just below.

## Closed decoding rule

Decoders remain strict by default.

For a known schema version:

- unknown required structure is rejected;
- duplicate fields are rejected;
- missing required fields are rejected;
- invalid typed stable-ID domains are rejected;
- invalid enum/node/operation kinds are rejected;
- unsupported schema versions are rejected explicitly.

Do not ignore unknown semantic fields merely to appear forward-compatible. Silent field dropping can change meaning while still producing a valid-looking snapshot.

## Compatible versus incompatible changes

### Compatible implementation change

Does not change the serialized contract or semantic interpretation.

Examples:

- internal refactor;
- cache/index change;
- algorithm optimization with identical results;
- viewer layout change when layout is not serialized semantic state.

No schema bump is required.

### Compatible additive schema change

Only allowed when the schema explicitly defines an extension point whose absence/presence semantics are already fixed.

Do not assume ordinary closed records are extensible.

### Incompatible schema change

Requires a new schema version.

Examples:

- adding a new required semantic field;
- changing field meaning;
- changing ID-domain interpretation;
- changing collection ordering semantics;
- changing numeric literal authority;
- changing an operation/node kind contract;
- changing whether a fact is authoritative or derived;
- changing default behavior for omitted data.

## No semantic defaults during migration

A migration may transform representation, but it may not invent unresolved semantic decisions.

If an old schema lacks information required by the new schema and no unique meaning-preserving conversion exists, migration must:

1. reject; or
2. create an explicit typed obligation if the new semantic model supports a safe incomplete state for that missing decision.

Do not fill missing information from display names, backend behavior, common practice, or guessed defaults.

## Migration is not an ordinary semantic edit

Schema migration operates at a privileged infrastructure boundary.

It may preserve IDs and rewrite representation without expressing every storage-level conversion as a user semantic operation.

However:

- migration cannot bypass validation;
- the migrated snapshot must pass the validator for the target schema or contain only explicitly permitted obligations;
- migration does not make generated backend source authoritative;
- migration must not silently allocate replacement IDs for existing semantic objects merely because representation changed.

Ordinary program changes continue to use semantic operations.

## Identity preservation rule

Representation-only migration preserves stable semantic IDs.

Allocate new IDs only when the target semantic model genuinely introduces a new independently identified object that did not exist in the source semantic model.

Examples:

- converting one storage record layout to another: preserve ID;
- renaming a field in the persistence schema: preserve semantic ID;
- splitting one old semantic entity into two genuinely independent target entities: preserve the identity of the continuing entity when definable and allocate a new ID for the new entity; otherwise require an explicit migration policy.

## Migration fingerprints and evidence

A durable migration should record enough evidence to audit the transition:

- source schema ID;
- target schema ID;
- source snapshot fingerprint when available;
- target snapshot fingerprint;
- migration tool/version;
- diagnostics or obligations generated;
- whether semantic fingerprint was expected to remain equal.

For representation-only migrations, semantic fingerprint equality should normally be an asserted invariant.

If a migration intentionally changes semantics, it should not be described as representation-only migration; treat the semantic portion as an explicit program transformation with review evidence.

## Fingerprint versioning

Fingerprint domains are versioned independently from the snapshot schema.

A fingerprint algorithm/domain change must not produce a value that can be confused with the old fingerprint contract.

The current `sha256:` display identifies the digest algorithm, while the hashed domain string distinguishes the fingerprint contract/version.

Changing which semantic facts are included in `SemanticFingerprint` requires a new fingerprint domain version even if SHA-256 remains the digest algorithm.

## Operation/tool schema versioning

The in-process F# discriminated union is not by itself a durable operation schema.

When agent operations are exposed through a durable tool/protocol boundary:

- operation kind is explicit;
- version is explicit at the protocol/schema boundary;
- stable target domains are typed;
- unsupported operation versions reject;
- a new operation version does not silently reinterpret an old request;
- server-side ID allocation remains part of operation semantics where defined.

Changing an agent-tool description without changing operation semantics may be versioned as interface metadata rather than a new semantic operation version, but recorded evaluations must still identify the tool-surface revision.

## IR evolution

Typed Semantic IR and Execution IR are internal-but-explicit contracts and evolve independently.

Rules:

- backend code consumes the Execution IR version it declares support for;
- a backend must not guess how to interpret a newer unknown instruction/terminator;
- a lowering adapter may migrate one IR version to another if meaning is uniquely preserved;
- editing-only metadata must not be added to IR merely to simplify migration;
- provenance needed for diagnostics may evolve as explicit non-semantic fields.

The IR JSON writers, `TypedSemanticIrSerializer` and `ExecutionIrSerializer`, are write-only debug and inspection output. Nothing reads them, there is no round trip, and their shape carries no compatibility promise: it may change in any commit without a version bump. Their `schemaVersion` field is informational. Do not persist this output or use it as an interchange format; persist the semantic snapshot and derive the IR again.

## Persistence-format experiments

JSON and Nyason may evolve at different speeds.

Do not force their syntax/schema version numbers to match merely because they encode the same semantic snapshot model.

A format experiment can be replaced without changing semantic graph identity as long as decode -> semantic snapshot meaning is preserved.

This is one reason Q-001 remains open while the semantic model continues to evolve.

## Deprecation policy

Do not promise indefinite support for every experimental pre-M1 schema.

When a schema becomes obsolete:

1. identify the last reader/writer version;
2. provide a migration path where practical;
3. keep golden fixtures for important historical versions while migration support exists;
4. remove support only in an explicit change with documentation;
5. never silently reinterpret an obsolete version as the newest schema.

The next removal is the planned [legacy cutover](../plans/post-m2-roadmap.md#legacy-cutover-planned).

## Testing requirements

For each supported migration/version boundary, test as applicable:

- known-version round-trip;
- unknown-version rejection;
- unknown semantic field/kind rejection;
- deterministic migration output;
- stable-ID preservation;
- target validation;
- semantic-fingerprint preservation for representation-only migrations;
- explicit obligation/rejection when meaning cannot be derived uniquely;
- old artifact -> migrate -> current artifact reproducibility.

## M1 rule

During the Int64/List/Float64 expansion, prefer explicit schema version bumps over compatibility tricks. M1 is still early enough that clear contracts are more valuable than preserving accidental experimental syntax forever.
