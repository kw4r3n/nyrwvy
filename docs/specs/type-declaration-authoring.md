# Atomic product and union declaration authoring

**Status:** Current

This specification defines the first Milestone 4 declaration-authoring slice tracked by issue #154. It implements the Product/Union stage accepted in `docs/decisions/declaration-authoring.md` after function creation and the maintained snapshot-backed authoring session.

## Purpose

An ordinary agent must be able to define one complete product or union declaration without supplying production declaration identities or mutating the persistent schema directly.

The operation reuses the existing `SemanticType`, `SemanticField`, `SemanticVariant`, `TypeRef`, snapshot validation, reviewed receipt, and publication contracts. It does not introduce a second type representation or a generalized schema-authoring framework.

## Public construction intent

The public request surface is:

```text
AgentTypeFieldIntent =
  DisplayName: string
  Type: TypeRef

AgentTypeVariantIntent =
  DisplayName: string
  Fields: AgentTypeFieldIntent list

AgentTypeDeclarationKindIntent =
  | Product of AgentTypeFieldIntent list
  | Union of AgentTypeVariantIntent list

AgentCreateTypeDeclarationRequest =
  ExpectedBaseSnapshotId: SnapshotId
  DisplayName: string
  Kind: AgentTypeDeclarationKindIntent
```

The request never contains a production `TypeId`, `VariantId`, or `FieldId`.

Field order and variant order are carried as ordinary request structure; identity and all later references are by allocated stable ID. This operation does not add name-based uniqueness or name-based identity semantics.

## Preflight

The complete intent is validated before any production declaration identity is allocated.

Preflight requires:

- `ExpectedBaseSnapshotId` to equal the current workspace snapshot;
- a nonblank type display name;
- every field display name to be nonblank;
- every union variant display name to be nonblank;
- every field `TypeRef` to be valid against the already-published base snapshot;
- builtin generic arity to match the existing `Option`, `Result`, and `List` contracts.

`TypeRef.Named` may reference only an already-published `TypeId`. `TypeRef.Capability` may reference only an already-published `CapabilityId`. Primitive and valid builtin-generic references remain available normally.

The new declaration is not visible during preflight. Therefore the first slice cannot express self recursion, mutual recursion, or references to another declaration being created in the same request. No future-ID placeholders or draft-ID lifecycle is introduced.

No additional structural rule is invented when the existing semantic model permits a shape. In particular, display names need not be unique merely because a declaration is being authored through this API.

## Allocation and materialization

Only after preflight succeeds:

1. allocate one system-owned `TypeId`;
2. for a product, allocate one `FieldId` for each requested field;
3. for a union, allocate one `VariantId` for each requested variant and one `FieldId` for every requested variant field;
4. materialize the existing `SemanticType`, `SemanticVariant`, and `SemanticField` records;
5. create a fresh proposed snapshot whose parent is the reviewed base snapshot;
6. run the ordinary full `SnapshotValidator` over the proposed snapshot;
7. compute the ordinary readiness and preview receipt.

The operation does not create functions, effects, capabilities, providers, obligations, migration metadata, or aliases.

## Review contract

A successful simulation returns a private proposal handle and a public review:

```text
AgentTypeDeclarationReview =
  RequestedChange: AgentCreateTypeDeclarationRequest
  Receipt: TransactionPreviewReceipt
  Readiness: TransactionReadiness
  CreatedType: SemanticType
```

`RequestedChange` is the caller's intent. `CreatedType` is the exact materialized declaration with the newly allocated stable IDs. Keeping them separate makes the identity boundary reviewable and gives later expression/function authoring the concrete `TypeId`, `VariantId`, and `FieldId` values it needs.

The proposal exposes only review, transaction ID, and proposed snapshot ID. It does not expose raw snapshot publication authority.

## Publication

Publication follows the established reviewed-candidate rules:

- the current snapshot ID must still equal the reviewed base ID;
- the current base fingerprint must still equal the reviewed base fingerprint;
- the exact proposed snapshot and semantic fingerprints must still match the reviewed receipt;
- blocking obligations obey the existing explicit `CommitPolicy`;
- the exact validated proposed snapshot is published through the opaque `AgentWorkspace` only after these checks succeed;
- stale/review-mismatch rejection leaves the workspace unchanged.

Explicit rejection forgets the proposal and retains the current snapshot.

## Maintained authoring-session integration

The maintained `Nyrwvy.AuthoringSession` JSON-lines transport exposes one thin adapter operation, `simulateCreateType`. It converts JSON declaration intent to `AgentCreateTypeDeclarationRequest`; semantic validation and identity allocation remain exclusively in the Core workflow.

A successful session commit persists the reviewed candidate through the M3 publication boundary before advancing the in-memory session. The declaration then appears through ordinary `declarations` / `getType` discovery and its returned IDs may be used by later `ExpressionRecipe` and function-creation requests.

## Non-goals

This slice does not implement type mutation or deletion, field migration, exhaustive-match repair, partial declarations, recursive type groups, caller-owned declaration IDs, future-ID guessing, multi-declaration draft protocols, effect/capability/provider creation, or a generalized schema-authoring framework.

## Verification contract

Focused deterministic evidence covers:

- product creation with supported field types;
- union creation with multiple variants;
- fresh system-owned type/variant/field IDs and post-publication discovery;
- invalid name/type intent rejecting before allocation/publication;
- review/receipt facts and stale rejection;
- preservation of unrelated semantic state;
- composition with existing function/expression authoring by constructing/accessing the new product and constructing/exhaustively matching the new union;
- one maintained-session integration check.

Existing persistence, lowering, backend, and expression-engine tests remain authoritative unless this implementation changes those contracts. No broad model corpus or always-on extended/live suite is required for this slice.
