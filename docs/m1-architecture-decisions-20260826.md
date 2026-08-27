# Milestone 1 architecture decisions — 2026-08-26

## Status

Accepted Milestone 1 design decisions derived from the verified Milestone 0 architecture and the verified M1 stable-ID / fingerprint / preview-receipt work.

These decisions constrain new implementation work. Items explicitly marked deferred remain open and must not be silently guessed by implementation code.

## M1-D01 — Current snapshots are authoritative; operation history is provenance

### Decision

The published semantic snapshot is authoritative program state.

Transaction / operation history may be retained for audit, explanation, metrics, migration assistance, or replay experiments, but it is not a second authoritative representation from which current program meaning must be reconstructed.

### Consequences

- snapshot and operation log divergence cannot create two competing versions of program meaning;
- deleting or compacting old operation history must not change the semantic identity or meaning of the current snapshot;
- replay may be tested as a consistency check, but replay success is not required to read the current program;
- operation schema evolution does not make old programs unreadable as long as their snapshot schema remains supported;
- provenance records may reference snapshot / transaction fingerprints without becoming executable semantics.

This resolves the basic canonicality direction of Q-003. Storage durability and audit-log retention policy remain implementation concerns.

## M1-D02 — Stable-ID lifecycle follows semantic identity, not file/repository identity

### Decision

Stable IDs identify semantic objects. Ordinary version-control operations preserve IDs because they preserve semantic history.

Rules:

- Git clone preserves all semantic IDs;
- Git branch preserves all semantic IDs;
- Git history-preserving fork preserves IDs unless an explicit import/copy operation intentionally creates new semantic objects;
- rename and semantically legal movement preserve IDs;
- duplicating an entity as a new semantic object allocates new IDs recursively for independently identified contained entities;
- template instantiation allocates new IDs for instantiated semantic objects;
- the agent does not choose these new IDs manually;
- merge/import collision handling must be explicit and may reject rather than silently rewrite identities.

### Deferred

General cross-repository import and semantic merge policy remains open because package/module identity and merge semantics are not yet implemented.

This partially resolves Q-002 beyond local UUIDv7 generation without pretending that cross-repository merge is solved.

## M1-D03 — Full validation is the correctness oracle

### Decision

Full validation of an immutable snapshot remains the normative correctness definition.

Future incremental validation is a cache / scheduling optimization over the same facts, not a second validation semantics.

### Consequences

- cache state cannot affect validation results;
- incremental results must be reproducible by full validation;
- invalidation rules are implementation metadata, not program semantics;
- tests may compare incremental results against full-validation results;
- an optimization may fall back to full recomputation whenever dependency information is uncertain.

This resolves the architecture direction of Q-007. The concrete incremental algorithm remains deferred until performance measurements justify it.

## M1-D04 — Operation order inside a transaction is explicit and semantic

### Decision

Operations in one transaction are applied in their explicit request order. The engine must not silently reorder them to improve convenience or apparent commutativity.

General conflict analysis may later prove that some operations commute, but automatic reordering is a separate feature requiring explicit semantics and tests.

### Consequences

- simulation and commit consume the same ordered operation sequence;
- a proposal is bound to its exact ordered request and resulting preview;
- optimistic concurrency occurs at the base-snapshot / reviewed-preview boundary;
- stale proposals are rejected rather than automatically rebased;
- future rebase / merge tooling must produce a new reviewed proposal rather than mutate an already reviewed receipt.

This fixes the current answer to the basic part of Q-005 while leaving semantic merge and automatic rebase open.

## M1-D05 — Approval is workflow metadata bound to an exact reviewed proposal

### Decision

Human or automated approval is graph-adjacent workflow metadata, not executable semantic graph state.

An approval that authorizes publication must bind to the reviewed proposal, including at minimum:

- transaction identity;
- base snapshot identity / fingerprint;
- proposed snapshot identity / fingerprint.

A stale or changed proposal requires new approval.

### Consequences

- approval does not change program semantics merely by existing;
- approval data never lowers to Typed Semantic IR or Execution IR;
- the semantic snapshot remains readable without organizational approval infrastructure;
- cryptographic signatures, roles, quorum policy, and organization-specific policy can be layered later;
- approval may be stored alongside transaction/audit metadata.

The verified preview-receipt mechanism supplies the exact binding primitive required by Q-015. Organizational and cryptographic policy remain open.

## M1-D06 — Agent creation APIs allocate identities on the system side

### Decision

When an ordinary agent operation creates semantic objects, the system allocates their production stable IDs.

The agent supplies semantic intent and references existing objects by stable ID. It does not fabricate IDs for new parameters, obligations, nodes, transactions, snapshots, or future first-class semantic entities.

### Consequences

- agent errors cannot accidentally reuse a hand-written ID;
- creation operations may return allocated identities in the proposal / diff;
- deterministic fixture IDs remain available only for tests and controlled import/migration work;
- an import operation that must preserve external identities is a distinct privileged workflow, not ordinary creation.

## M1-D07 — Requested intent and resulting semantic diff are distinct review facts

### Decision

Change Review preserves both:

1. the operation intent submitted by the editing client; and
2. the semantic diff produced by the engine.

Neither is reconstructed from the other.

### Reason

They answer different questions:

- intent: "what did the agent ask for?"
- diff: "what will actually change if this proposal is published?"

Automatic propagation, generated obligations, or rejected preconditions can make these diverge meaningfully.

### Consequences

- review UI may display the two side-by-side;
- audit tooling may attribute unexpected changes to propagation rather than user intent;
- tests can assert both operation preservation and diff correctness;
- semantic diff remains derived from the proposed semantic transition, not authored manually by the agent.

## M1-D08 — Physical project boundaries enforce dependency direction

### Decision

Milestone 1 should split the current co-located implementation into physical projects/assemblies after the agent workflow facade is verified and before language growth becomes large.

Target direction:

```text
Semantics <- Execution <- Backend.CSharp
    ^
    |
  Viewer <- Tools
```

The exact project names are secondary. Dependency direction is not.

### Required properties

- Semantics has no reference to Viewer, Execution backend implementations, or filesystem/CLI tooling;
- Execution contains backend-neutral lowering only;
- C# backend cannot query or mutate authoritative snapshots directly;
- Viewer consumes semantic query/review contracts rather than internal mutable state;
- Tools own process and filesystem side effects;
- tests may span assemblies, but production dependency direction cannot be reversed for test convenience.

### Migration rule

Perform the split mainly as a move/refactor with behavior-preserving tests. Do not combine broad semantic feature additions with the physical split unless a small boundary adapter is required.

## M1-D09 — Obligation states grow only when a consumer needs the distinction

### Decision

`Open` and `Resolved` remain the implemented obligation states until a concrete stale-obligation workflow requires another state.

The next justified state is `Invalidated`: an obligation whose target/context changed such that the old required decision is no longer applicable.

Do not add `Superseded` or a larger workflow taxonomy preemptively. If replacement relationships become useful, add them when a query, review, or repair workflow consumes them.

### Consequences

- obligations never silently disappear;
- resolution remains explicit and attributable;
- invalidation, when introduced, must be explicit and attributable;
- deleting an obligation because it is inconvenient is never valid cleanup.

This narrows Q-006 without inventing unused state.

## M1-D10 — Canonical persistence remains replaceable

### Decision

Do not select Nyason, JSON, binary storage, or a database as the permanent canonical persistence format during M1.1 solely because the current codecs exist.

JSON remains the strict reference/debug codec. Nyason remains an experimental NyamNyam-specific encoding until agent editing experiments provide evidence.

### Consequences

- semantic APIs and fingerprints must not depend on humans editing one storage syntax;
- focused agent experiments should compare representations later;
- storage format choice remains Q-001;
- schema versioning and deterministic serialization remain required regardless of the eventual medium.

## M1-D11 — Backend representation cannot define semantic numeric/collection meaning

### Decision

The C#/.NET representation for `Int64`, `List<T>`, and `Float64` is downstream implementation detail.

Semantic contracts are fixed before backend mapping and must survive another backend without reinterpretation.

Examples:

- integer overflow behavior is in the semantic node kind, not inherited from a C# compilation context;
- `List<T>` ordering/immutability are semantic, while the concrete C# storage class is backend-local;
- `Float64` means IEEE 754 binary64, not "whatever the backend's float means";
- numeric conversion never appears merely because the backend inserts an implicit conversion.

## M1-D12 — New generic abstractions require evidence from specialized nodes first

### Decision

Do not generalize specialized semantic nodes merely because several nodes share implementation code.

In M1, use concrete roles such as `ListMap`, `ListFilter`, `Float64IsNaN`, and explicit integer arithmetic variants. Measure agent use before introducing generic higher-order calls, numeric operator families, or overloaded operation bags.

Generalization is accepted only when it reduces complexity without forcing semantic role back into ambient type inference or string/operator metadata.
