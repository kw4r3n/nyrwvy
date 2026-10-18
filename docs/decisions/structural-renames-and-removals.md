# Structural renames and removals

- **Status:** Accepted
- **Date:** 2026-10-18
- **Roadmap:** the T1 row of the [general-purpose programming roadmap](../plans/general-purpose-programming-roadmap.md)

Only functions and types could be renamed through the ordinary reviewed operations, and a function parameter or a Union variant could not be removed at all. A misnamed field, variant, parameter, effect or capability, or a parameter or variant that is no longer needed, could only be fixed by recreating the declaration under new identities and repairing every user. That loses stable identity, which is what review, obligations and imports rely on.

## Decision

The ordinary `simulateOperations` set gains these exact-ID operations, reviewed and committed like every other proposal:

- **Renames** of a Product or Union variant field (`renameField`), a Union variant (`renameVariant`), a function parameter (`renameParameter`), an Effect (`renameEffect`), a Capability (`renameCapability`), a Capability operation (`renameCapabilityOperation`) and a Capability operation parameter (`renameCapabilityParameter`). A rename changes only the display name: every stable ID and every graph reference stays as it is. A blank name is `InvalidDisplayName`. A name another member of the same scope already has is `DuplicateDisplayName`, with a message that names the scope: fields within their Product or Union variant, variants within their Union, parameters within their function, operations within their Capability, operation parameters within their operation, effects and capabilities among all effects and capabilities. Pre-existing duplicates are left alone, as for type and function renames.
- **`removeFunctionParameter`** removes one parameter and drops its argument binding from every direct call of the function; the bound value node stays in the caller graph, as `removeProductField` leaves a removed binding's value. Every obligation for the parameter, open or resolved, is retired and recorded in the review. It is rejected while the body still references the parameter (`ParameterStillReferenced`), when the function has uses other than direct calls (`ParameterRemovalNonCallUse`, since those fix its signature), when a function that would lose a binding declares effects (`ParameterRemovalMayDiscardEffects`), and for a parameter added in the same transaction (`ParameterCreatedInTransaction`).
- **`removeUnionVariant`** removes one variant. Every `MatchUnion` arm drops it; an arm left with no variant is removed with its body; a shared arm left with one variant that has fields binds those fields, since a one-variant arm binds each field exactly once. It is rejected while any `UnionConstruct` builds the variant (`UnionVariantStillConstructed`), for the Union's last variant (`UnionVariantLast`), when a function whose arm would be dropped declares effects (`UnionVariantRemovalMayDiscardEffects`), and for any validation issue the removal introduces, such as an obligation owned by a removed arm (`UnionVariantStillReferenced`).
- `simulateOperationsByName` accepts the two removals by name (`{ function, parameter }`, `{ type, variant }`), since the session already resolves those members within their owners. Renames stay exact-ID only, as `renameFunction` and `renameType` are.

## Reason

These are the remaining single-declaration edits a programmer expects next to the ones already reviewed, and each keeps the identity-preserving guarantee: a rename touches nothing but a name, and a removal repairs exactly the references the operation itself can repair without guessing. Everything that would need a choice (a still-referenced parameter, a still-constructed variant, effectful evaluation that would be discarded) is rejected with a code that names what to repair, the same conservative rule `removeProductField` follows.

## Consequences

- `SymbolRename` has a case per rename kind; the review's `renames` entries carry a `kind` per case and, for a scoped name, its owner IDs (`ownerTypeId`, `ownerVariantId`, `ownerFunctionId`, `ownerCapabilityId`, `ownerOperationId`). Existing type and function entries keep their shape and order.
- `SemanticDiff` gains `ParametersRemoved` and `UnionVariantsRemoved`, on the wire as `parametersRemoved` and `unionVariantsRemoved`. Both lists appear only when non-empty, so the review of every other operation is byte-identical to before. The viewer shows them in the same conditional way.
- A narrowed shared arm's new field bindings take pattern-binding IDs derived from the transaction, match node and field, so re-simulating the reviewed request at commit gives the same snapshot.
- Renames and removals of imported declarations are rejected as `ImportedDeclarationModified` before a proposal is registered, as other edits of imported declarations are.
- No node kind or persisted construct changes; the snapshot schema version is unchanged.
- `StructuralRenameAndRemovalTests` covers every kind through the JSONL protocol.
- No ordinary operation adds a variant within a transaction, so a variant added and removed in one transaction cannot occur; the variant evolution workflows publish their own proposals.

## Revisit conditions

- A caller needs a rename resolved by name; the rename kinds would then join `simulateOperationsByName` together with `renameFunction` and `renameType`.
- Removing a parameter or a variant from functions that declare effects is needed often enough to justify an explicit plan for preserving the discarded evaluation.
- A removal should repair remaining references automatically (for example by replacing parameter references with a chosen value) rather than reject them.
