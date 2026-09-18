# Stable IDs

**Status:** Current

Stable IDs are Nyrwvy's semantic identity mechanism. Names, paths, source locations, serialization order, and viewer coordinates are presentation or representation details and do not substitute for identity.

## Current production contract

Production stable-ID payloads use RFC 9562 UUIDv7 values generated with .NET 10 `Guid.CreateVersion7()`.

In memory, semantic domains remain distinct through typed IDs such as:

```text
StableId<'Kind>
```

External/inspection text carries the domain explicitly:

```text
type:<uuidv7>
function:<uuidv7>
node:<uuidv7>
transaction:<uuidv7>
```

Parsing is domain-aware: a value rendered for one stable-ID domain is rejected when supplied where another domain is required.

## Current ID domains

Production allocation currently covers:

- type;
- field;
- variant;
- function;
- parameter;
- pattern binding;
- graph node;
- effect;
- capability;
- capability operation;
- capability parameter;
- obligation;
- snapshot;
- transaction.

The textual domain prefix is transport/inspection structure. It is not duplicated inside the UUID payload.

## Allocation boundary

Ordinary agent-facing creation operations allocate new identities inside trusted Nyrwvy tooling. Agents supply semantic intent and references to existing stable IDs; they do not fabricate production IDs for newly created semantic objects.

Controlled tests and migration/import tooling may use explicit fixture identities where the workflow requires reproducibility or preservation of an existing identity.

## Lifecycle

Stable identity follows semantic continuity rather than file/repository spelling.

- rename preserves the ID;
- semantically legal movement preserves the ID;
- Git clone and branch preserve IDs;
- history-preserving forks preserve IDs unless an explicit copy/import operation creates new semantic objects;
- duplicating an entity as a new semantic object allocates new IDs for the independently identified result;
- template instantiation allocates new IDs for instantiated semantic objects.

General cross-repository import, collision handling, package identity interaction, and semantic merge remain intentionally unresolved. See [Open questions](../decisions/open-questions.md).

## Persistence

Nyason and other codecs preserve typed domains without making their spelling the source of semantic identity. A representation-only migration preserves stable IDs unless the target semantic model genuinely introduces a new independently identified object.

See the [schema evolution policy](../specs/schema-evolution-policy.md) for migration rules.

## Evidence history

The implementation/verification record that established the production UUIDv7 contract is preserved in the [pre-reorganization archive](../../docs-archive/2026-08-pre-reorg/m1-a-stable-id-20260826.md). Exact historical test counts and commit heads belong there rather than in this maintained reference.
