# Atomic capability and operation declaration authoring

**Status:** Current

This specification defines the Capability/Operation stage of the accepted staged declaration-authoring sequence tracked by issue #161. It follows atomic function, Product/Union, and Effect declaration creation.

## Purpose

An ordinary agent must be able to create one complete capability declaration together with its operations without supplying production `CapabilityId`, `CapabilityOperationId`, or `CapabilityParameterId` values and without registering a runtime provider.

The operation reuses the existing `SemanticCapability`, `SemanticCapabilityOperation`, `SemanticCapabilityParameter`, `TypeRef`, effect references, snapshot validation, reviewed proposal, and opaque publication contracts. It does not create another capability representation or runtime/provider authority.

## Public construction intent

The public request surface is:

```text
AgentCapabilityParameterIntent =
  DisplayName: string
  Type: TypeRef

AgentCapabilityOperationIntent =
  DisplayName: string
  Parameters: AgentCapabilityParameterIntent list
  ResultType: TypeRef
  FailureType: TypeRef option
  EffectId: EffectId

AgentCreateCapabilityDeclarationRequest =
  ExpectedBaseSnapshotId: SnapshotId
  DisplayName: string
  Operations: AgentCapabilityOperationIntent list
```

The request contains existing published `EffectId` references because operation effects are already semantic facts. It never contains production capability, operation, or parameter identities. The maintained session's `simulateCreateCapabilityByName` convenience resolves effect and type display names to these IDs before building the same request (see [Name-resolved AI authoring](agent-authoring-session.md#name-resolved-ai-authoring)).

Operation and parameter order are carried as ordinary declaration structure. Display names are presentation metadata and never identity; the capability's own display name must only be unique among capabilities (see Preflight).

## Preflight

The complete request is validated before any new production declaration identity is allocated.

Preflight requires:

- `ExpectedBaseSnapshotId` to equal the current workspace snapshot;
- a nonblank capability display name that no existing capability (local or imported) already has (`CapabilityDeclarationDuplicateDisplayName`);
- every operation display name to be nonblank;
- every operation parameter display name to be nonblank;
- every parameter/result/failure `TypeRef` to be valid against the already-published base snapshot;
- every operation `EffectId` to reference an already-published effect;
- builtin generic arity to follow the existing `Option`, `Result`, and `List` contracts.

`TypeRef.Named` may reference only already-published types and `TypeRef.Capability` only already-published capabilities. The capability being created is not visible during preflight, so self-reference and mutually-created declaration references are not part of this slice.

Zero operations and zero-parameter operations remain valid because the current semantic model permits them. No extra structural rule is introduced by the authoring API; operation and parameter names need not be unique, and capability-name uniqueness is enforced at creation, not as a snapshot validation rule.

## Allocation and materialization

Only after preflight succeeds:

1. allocate one system-owned `CapabilityId`;
2. allocate one system-owned `CapabilityOperationId` per operation in request order;
3. allocate one system-owned `CapabilityParameterId` per parameter in request order;
4. materialize the existing semantic capability/operation/parameter records;
5. create a fresh proposed snapshot whose parent is the reviewed base snapshot;
6. append the new capability without otherwise changing semantic state;
7. run ordinary full `SnapshotValidator` validation;
8. compute ordinary readiness and preview fingerprints/receipt.

No effect, function, type, provider, runtime implementation, obligation, or migration metadata is created implicitly.

## Review contract

A successful simulation returns a private proposal handle and public review:

```text
AgentCapabilityDeclarationReview =
  RequestedChange: AgentCreateCapabilityDeclarationRequest
  Receipt: TransactionPreviewReceipt
  Readiness: TransactionReadiness
  CreatedCapability: SemanticCapability
```

`RequestedChange` is caller intent. `CreatedCapability` is the exact materialized declaration containing the newly allocated capability, operation, and parameter identities. Keeping these separate makes the authority boundary reviewable and gives later `ExpressionRecipe.CapabilityInvoke` authoring the concrete stable IDs it needs.

The proposal exposes review, transaction ID, and proposed snapshot ID only. It does not expose raw snapshot publication authority.

## Publication

Publication follows the established exact reviewed-candidate rules:

- current snapshot ID must still equal the reviewed base ID;
- current base fingerprint must still equal the reviewed base fingerprint;
- exact proposed snapshot and semantic fingerprints must still match the reviewed receipt;
- blocking obligations obey the existing explicit `CommitPolicy`;
- the validated proposed snapshot is published through opaque `AgentWorkspace` only after those checks succeed;
- stale or receipt-mismatch rejection leaves current state unchanged.

Explicit rejection retains the current snapshot.

## Composition with expression authoring

After publication, the returned capability and operation IDs are ordinary published semantic references.

Existing function creation may accept a parameter whose type is `TypeRef.Capability createdCapabilityId`, explicitly declare the operation's existing `EffectId`, and author a `CapabilityInvoke` body referencing the created operation/parameter IDs. This declaration workflow does not invoke the capability, choose a provider, or register an implementation.

## Maintained authoring-session integration

The snapshot-backed JSON-lines session exposes one thin adapter operation, `simulateCreateCapability`, carrying the declaration intent above. The session supplies its current snapshot ID as `ExpectedBaseSnapshotId`.

A successful session commit persists the exact reviewed candidate through the established single-writer publication boundary before advancing in-memory state. Reopening the persisted snapshot exposes the same capability/operation/parameter identities through ordinary declaration discovery.

## Verification contract

Focused deterministic evidence covers:

- successful capability creation with multiple operations/parameters;
- fresh system-owned capability/operation/parameter IDs and post-publication discovery;
- invalid display metadata, unknown effect, and invalid `TypeRef` rejection without publication;
- review/receipt accuracy and stale rejection;
- preservation of unrelated semantic state;
- composition with existing function/expression authoring through `CapabilityInvoke`;
- one maintained-session create/commit/reopen discovery check.

Existing persistence, effect, lowering, backend, and provider evidence remains authoritative because this slice does not change those contracts. No live provider or external-service validation is required. See [M4 capability authoring verification](../verification/m4-capability-authoring-20260915.md).

## Non-goals

This slice does not implement provider registration, runtime implementations, capability/operation mutation or deletion, migrations, partial/draft declarations, future IDs, recursive declaration groups, caller-owned production IDs, raw snapshot authority, or generalized schema-authoring infrastructure.
