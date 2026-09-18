# M4 product/union declaration authoring verification — 2026-09-15

**Status:** Observed result

This record captures deterministic repository verification for issue #154 and PR #159. It is implementation evidence for the first Milestone 4 declaration-authoring slice, not a live model-evaluation result.

## Verified implementation

- Repository: `kw4r3n/nyrwvy-lang`
- Implementation issue: #154
- Implementation PR: #159
- Exact verified implementation head: `7a98e1a0a93670ca9430265c7bc6b365fa710a91`
- Branch: `feat/154-product-union-authoring`
- GitHub Actions workflow run: CI #1160 (`34960961510`)
- .NET SDK: `10.0.401`

The implementation reuses the existing `SemanticType` / `SemanticField` / `SemanticVariant` model, reviewed snapshot publication, `ExpressionRecipe`, atomic function creation, and the M3 snapshot-backed authoring session. It does not introduce a second schema representation or expression engine.

## Verification result

The maintained CI path completed successfully at the exact implementation head:

```text
Release build:
  warnings: 0
  errors: 0

Fast suite:
  passed: 261
  failed: 0
  skipped: 0
```

No extended/live suite was added to ordinary CI for this slice.

## Completion evidence

The focused type-authoring tests establish the contracts required by #154:

1. A complete product request allocates a fresh system-owned `TypeId` and `FieldId` values, publishes through the reviewed workspace boundary, and is discoverable afterward by stable ID.
2. A complete union request allocates a fresh `TypeId`, distinct `VariantId` values, and variant field IDs, including a multi-variant declaration.
3. Invalid existing-type references and blank variant names reject before publication; the base workspace remains unchanged.
4. Review exposes the original intent separately from the exact allocated `CreatedType`, binds that result to the ordinary preview receipt, and rejects a proposal after the workspace has advanced.
5. An unrelated pre-existing function remains unchanged across declaration publication and subsequent composed authoring.
6. The published product IDs are immediately reusable by existing atomic function authoring for `ProductConstruct` and `FieldAccess`.
7. The published union IDs are immediately reusable by existing atomic function authoring for `UnionConstruct` and an exhaustive `MatchUnion` using the materialized field/variant IDs.
8. The ordinary snapshot-backed authoring session exposes `simulateCreateType`, persists the reviewed declaration through the existing publication boundary, closes, reopens, and rediscovers the same type and field IDs.
9. Reflection-based authority-surface checks confirm the public request carries presentation/type intent but not caller-supplied production `TypeId`, `VariantId`, `FieldId`, raw `SemanticType`, or raw `Snapshot` authority.

## Maintained boundaries

The resulting slice remains intentionally narrow:

- one atomic product or union declaration per proposal;
- field type references are primitives/builtin generics or already-published valid declarations/capabilities;
- complete preflight occurs before production declaration IDs are allocated;
- identity is system-owned and returned through review for later stable-ID composition;
- full `SnapshotValidator`, readiness, fingerprints, exact reviewed commit/reject, stale checks, and session persistence are reused;
- display names remain presentation metadata and are not made unique identity keys.

Self/mutual recursion, future IDs, draft declaration groups, type mutation/deletion, migration/repair, effect/capability/provider creation, and generalized schema-authoring infrastructure remain outside this slice.
