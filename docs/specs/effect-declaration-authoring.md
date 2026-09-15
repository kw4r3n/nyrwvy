# Atomic effect declaration authoring

**Status:** Current

This specification defines the Effect stage of the accepted staged declaration-authoring sequence tracked by issue #160. It follows atomic function and Product/Union declaration creation and precedes capability/capability-operation creation.

## Purpose

An ordinary agent must be able to create one effect declaration without supplying a production `EffectId` or mutating the snapshot directly.

The operation reuses the existing `SemanticEffect`, snapshot validation, readiness, fingerprint, reviewed proposal, and opaque publication contracts. It does not introduce effect handlers, provider registration, propagation policy, or a generalized declaration framework.

## Public construction intent

The public request surface is:

```text
AgentCreateEffectDeclarationRequest =
  ExpectedBaseSnapshotId: SnapshotId
  DisplayName: string
```

The request never contains a production `EffectId`.

Display names are presentation metadata. This operation does not introduce name-based identity or uniqueness semantics.

## Preflight and allocation

The complete intent is validated before production identity allocation.

Preflight requires:

- `ExpectedBaseSnapshotId` to equal the current workspace snapshot;
- a nonblank effect display name.

Only after preflight succeeds:

1. allocate one system-owned `EffectId`;
2. materialize the existing `SemanticEffect` record;
3. create a fresh proposed snapshot whose parent is the reviewed base snapshot;
4. append the new effect without otherwise changing semantic state;
5. run ordinary full `SnapshotValidator` validation;
6. compute ordinary readiness and preview fingerprints/receipt.

No function requirement, capability operation, runtime provider, handler, or obligation is created implicitly.

## Review contract

A successful simulation returns a private proposal handle and the public review:

```text
AgentEffectDeclarationReview =
  RequestedChange: AgentCreateEffectDeclarationRequest
  Receipt: TransactionPreviewReceipt
  Readiness: TransactionReadiness
  CreatedEffect: SemanticEffect
```

`RequestedChange` is the caller intent. `CreatedEffect` is the exact materialized declaration containing the allocated stable ID. Keeping them separate makes the identity boundary reviewable and gives later function/capability authoring the concrete `EffectId` it needs.

The proposal exposes review, transaction ID, and proposed snapshot ID only. It does not expose raw snapshot publication authority.

## Publication

Publication follows the existing exact reviewed-candidate rules:

- the current snapshot ID must still equal the reviewed base ID;
- the current base fingerprint must still equal the reviewed base fingerprint;
- the exact proposed snapshot and semantic fingerprints must still match the reviewed receipt;
- blocking obligations obey the existing explicit `CommitPolicy`;
- the validated proposed snapshot is published through the opaque `AgentWorkspace` only after those checks succeed;
- stale or receipt-mismatch rejection leaves current state unchanged.

Explicit rejection retains the current snapshot.

## Composition

After publication, the returned `EffectId` is an ordinary already-published effect reference. Existing function required-effect operations and atomic function creation may use it without any new propagation semantics.

A function may therefore explicitly declare the new effect through existing authoring APIs. This operation itself never edits any function and never walks or rewrites the call graph.

## Maintained authoring-session integration

The snapshot-backed JSON-lines session exposes one thin adapter operation:

```text
simulateCreateEffect
  displayName: string
```

The session supplies its current snapshot ID as `ExpectedBaseSnapshotId`. Semantic validation and production ID allocation remain exclusively in the Core workflow.

A successful session commit persists the exact reviewed candidate through the established single-writer publication boundary before advancing in-memory state. Reopening the persisted snapshot exposes the same `EffectId` through ordinary declaration discovery.

## Verification contract

Focused deterministic evidence covers:

- successful effect creation with a fresh system-owned `EffectId`;
- post-publication discovery of that exact declaration;
- blank display metadata rejection without publication;
- review/receipt binding and stale rejection;
- preservation of unrelated semantic state;
- composition with existing function authoring using the published `EffectId`;
- one maintained-session create/commit/reopen discovery check.

Existing persistence, readiness, execution, and effect-validation evidence remain authoritative because this slice does not change those contracts. See [M4 effect authoring verification](../verification/m4-effect-authoring-20260915.md).

## Non-goals

This slice does not implement effect mutation/deletion, new propagation policy, handlers/providers, automatic caller rewrites, capability or operation creation, runtime registration, caller-owned IDs, raw snapshot authority, or generalized schema-authoring infrastructure.

Capability/capability-operation declaration authoring remains issue #161.
