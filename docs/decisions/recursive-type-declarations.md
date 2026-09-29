# Recursive type declarations

- **Status:** Accepted
- **Date:** 2026-09-29
- **Roadmap:** [findings from the 2026-09-29 probe 7](../plans/post-m2-roadmap.md#findings-from-the-2026-09-29-probe-7), order 26

This decision settles order 26. [Probe 7](../verification/rrawrlet-slice-probe7-20260929.md) ported rrawrlet's OpenCode event observer, whose input is untyped JSON. Representing it needs `Json` with `Arr(items: List<Json>)` and `Obj(members: List<Member>)`, where `Member` holds a `Json`. Type creation resolved named field types against published types only, so neither the self reference nor the pair could be declared by name. The probe declared them through exact-ID variant evolution (4 extra requests and IDs copied from reviews), which the ordinary AI-facing tools reach only through the generic `request` tool. This meets the revisit condition of the [declaration-authoring decision](declaration-authoring.md#revisit-conditions) for self-recursive creation and mutually dependent new declarations.

## Decision

**A type group is created in one reviewed transaction.** `simulateCreateTypesByName` takes `types: [{ displayName, typeKind }]`, in the `simulateCreateTypeByName` shape. A `{"kind":"named","name":…}` inside any member resolves first to a member of the group, then to a published type. `simulateCreateTypeByName` also accepts its own display name, which creates a group of one. A declaration that names no group member keeps the existing single-type path and review.

**References are request-local.** The Core request (`AgentCreateTypeDeclarationGroupRequest`) gives each member a `Reference`, a TypeId that fields in the group may use in `TypeRef.Named`. The transport allocates fresh references; they must be distinct and must not name a published type (`TypeDeclarationGroupReferenceInvalid`). Simulation allocates every member's real TypeId first, then rewrites each reference to it, so no reference is ever persisted and the published graph is ordinary: recursive named types were already valid, run, compared by `ValueEqual` and encoded by `runPure`.

**Every member must have a finite value.** A member whose Product fields, or some field of each of its Union variants, need a group member that has none can never be constructed, so it is rejected as `TypeDeclarationUninhabited` (`Loop { next: Loop }`, or `A { b: B }` with `B { a: A }`). Published types, primitives, `List` and `Option` (empty), and function, capability and task types count as inhabited; a `Result` is inhabited when either side is. The check is a fixpoint over the group only, so no published type is re-examined.

**The other rules are unchanged.** Names must be nonblank and unique among types and within the group (`TypeDeclarationDuplicateDisplayName`), an empty group is `TypeDeclarationGroupEmpty`, field types must otherwise resolve, and the whole candidate snapshot is validated once before review. The review is `createTypes` with `createdTypes` in request order; commit publishes all members or none.

**No partial declarations.** There is still no body-less or incomplete declaration and no draft-ID protocol: a group is complete when simulated. Mutual recursion between new functions keeps its placeholder-then-replace route.

## Consequences

- Protocol operation `simulateCreateTypesByName`; `simulateCreateTypeByName` accepts a self reference and then reviews as `createTypes`.
- Core `AgentTypeDeclarationWorkflow.simulateGroup`/`commitGroup`, session `SimulateCreateTypeGroup`, and diagnostics `TypeDeclarationGroupEmpty`, `TypeDeclarationGroupReferenceInvalid` and `TypeDeclarationUninhabited`.
- The `create_type` ordinary tool describes the self reference and points to the group operation.
- Tests: `tests/Nyrwvy.Core.Tests/RecursiveTypeGroupTests.fs`.

## Revisit conditions

- Traversing a recursive type with list operations needs a function and its callback to call each other, which still needs a placeholder callee. Revisit if a probe measures that cost.
