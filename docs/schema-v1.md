# Milestone 0 Graph Schema v2

## Status

Accepted for the Milestone 0 foundation. Version 2 supersedes the initial v1
foundation and adds modeled calls and typed missing-argument obligations. This schema is an authoritative data
contract, not a human-authored programming language.

## Scope

Schema v1 foundation defines the persistent snapshot envelope and the entity boundary
needed to construct, persist, query, and validate the initial user-validation
program. Expression and control-flow payloads remain deliberately narrow until
the Typed Semantic IR boundary is designed. Function bodies, capability
operations and backend bindings, and obligation impact and provenance fields are
not accepted by this foundation reader yet; it rejects unknown fields rather
than discarding them.

## Persistence format

Snapshots are UTF-8 JSON documents. Canonical serialization is defined as:

- object properties are emitted in ordinal key order;
- entity collections are emitted in ordinal stable-ID order;
- optional fields are omitted when absent;
- strings use the standard JSON escaping rules;
- no insignificant whitespace is emitted;
- numbers use only their canonical JSON representation.

The JSON document is storage data. Consumers must use builders, queries, and
semantic operations rather than treating its layout as an editing API.

## Stable IDs

Every ID is an opaque string with a domain prefix and a UUID value:

```text
type:018f6b1f-...     function:018f6b20-...     snapshot:018f6b21-...
```

The prefix identifies the ID domain and is validated on deserialization. IDs
are allocated with UUIDv7 in production. Builders accept an injected allocator
so fixtures and tests can use fixed IDs. IDs are local to one repository in
Milestone 0; importing, copying, forking, and merging snapshots are unsupported.

## Snapshot envelope

```json
{
  "schemaVersion": 2,
  "snapshotId": "snapshot:...",
  "parentSnapshotId": "snapshot:...",
  "types": [],
  "functions": [],
  "effects": [],
  "capabilities": [],
  "obligations": []
}
```

`parentSnapshotId` is omitted for the initial snapshot. A snapshot is immutable
after publication. Transactions refer to the complete `snapshotId`, not a
serialized position or display name.

## Entity boundary

The first persistent entities are types, fields, variants, functions,
parameters, effects, capabilities, obligations, transactions, and snapshots.
They need independent identity because operations, references, diagnostics, or
review views target them directly.

Expressions are structurally owned by a function in v1. They receive graph-node
IDs only when an operation, obligation, diagnostic, or query must identify them
independently. Literal values and type applications are embedded values. This
keeps the initial graph inspectable without making every syntax-sized fragment a
top-level entity.

## Initial entity shapes

```text
Type
  id, displayName, kind (product | union | builtin)
  fields[] or variants[]

Function
  id, displayName, parameters[], resultType, declaredEffectIds[], calls[]

Call
  graph-node id, calleeFunctionId, arguments[]

ArgumentBinding
  targetParameterId, sourceParameterId

Parameter
  id, displayName, type

Effect
  id, displayName

Capability
  id, displayName, effectId

Obligation
  id, callId, parameterId, originatingTransactionId, status,
  resolutionTransactionId?, resolutionSourceParameterId?
```

References use the target stable ID. In particular, a call maps each argument to
the selected callee parameter ID; positional association is not authoritative.

## Invariants

- IDs are unique within their domain and have the matching domain prefix.
- Owned entities appear only under their owner.
- References resolve to an entity of the expected kind.
- A function parameter belongs to exactly one function.
- An argument may target only a parameter owned by its selected callee.
- Snapshot serialization and deserialization preserve every stable ID exactly.

## Compatibility

`schemaVersion` is mandatory. Readers reject versions they do not support.
Migrations create a new snapshot and preserve semantic IDs unless an explicit,
documented migration rule says otherwise.
