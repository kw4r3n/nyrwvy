# Staged declaration authoring

- **Status:** Accepted; implemented. The current contracts are the [product/union](../specs/type-declaration-authoring.md), [effect](../specs/effect-declaration-authoring.md), and [capability/operation](../specs/capability-declaration-authoring.md) declaration authoring specifications.
- **Date:** 2026-09-08
- **Issue:** [#146](https://github.com/kw4r3n/nyrwvy-lang/issues/146)

This decision defines the next declaration-authoring direction after broad `ExpressionRecipe` construction became available. It does not turn declarations into a generic raw graph-editing surface.

## Decision

Grow ordinary declaration authority in this order:

1. function creation and function-signature/effect evolution;
2. product/union declarations;
3. effect declarations;
4. capability/capability-operation declarations.

Implement each stage separately. New production identities remain system-owned and references to already-published declarations continue to use typed stable IDs.

The first implementation is **atomic function creation with a complete `ExpressionRecipe` body**. Intentionally body-less function creation is deferred because the current semantic model has no valid missing-body representation and the current obligation model represents only missing direct-call arguments.

Editing a function's `RequiredEffects` against already-existing `EffectId` values is a separate early function-stage slice and precedes authority to create new effects.

## Why the first function must have a complete body

`SemanticFunction` currently contains a mandatory `Body: ValueGraph`, and validation requires every graph to own a valid result node. The durable obligation collection is currently `MissingArgumentObligation list`; it does not contain a general "function body required" obligation or another placeholder semantic state.

Adding body-less functions now would therefore require at least:

- changing the authoritative function representation;
- generalizing the obligation model;
- updating validation/readiness;
- updating persistence/fingerprints/projections;
- defining lowering behavior for the new incomplete declaration state.

That is disproportionate to the first useful creation operation. A fake/dummy body would be worse because it would invent executable semantics for an unresolved decision.

The first slice therefore creates a function only when its signature and body can be validated atomically. Revisit explicit missing-body incompleteness when a real workflow independently justifies a broader obligation/incomplete-declaration model.

## Function creation request

The first ordinary request carries construction intent rather than production IDs:

```text
ExpectedBaseSnapshotId
DisplayName
Parameters: ordered list of
  - DisplayName
  - TypeRef
ResultType: TypeRef
RequiredEffects: existing EffectId list
Body: ExpressionRecipe
```

The system allocates `FunctionId`, every `ParameterId`, every body `GraphNodeId`, and every body `PatternBindingId` only after full preflight succeeds.

Parameter order is semantic. Required-effect order is not; new function creation should store it canonically and reject duplicates.

The first slice references only already-existing named types, capabilities, effects, functions, variants, fields, and capability operations. Creating several mutually dependent declarations in one request is outside this slice.

## Referring to newly created parameters

A complete body must be able to refer to parameters whose production `ParameterId` values do not exist before materialization. Caller-supplied future `ParameterId` values would violate the established identity boundary, while a stateful prepare/draft protocol would add unnecessary lifecycle complexity.

Extend the **single maintained `ExpressionRecipe` algebra** with one ephemeral creation-only leaf, provisionally:

```text
ParameterSlot(index)
```

`index` is a zero-based position in the creation request's ordered parameter list. It is not a persistent semantic ID.

For function creation:

- preflight derives a slot's type from the parameter intent at that position;
- an out-of-range slot rejects;
- ordinary `Parameter(ParameterId)` leaves reject because no existing parameter is owned by the not-yet-published function;
- after preflight, system allocation maps every slot to the corresponding new `ParameterId` and materializes an ordinary persistent `ParameterReference` node.

For existing-function body replacement and targeted replacement, `ParameterSlot` is unavailable and rejects. Existing `Parameter(ParameterId)` behavior remains unchanged.

This keeps one expression algebra while adding only the ephemeral addressing mechanism that function creation concretely requires.

## Creation preflight and materialization

Before allocating production IDs, validate at least:

- non-blank function and parameter display names;
- every parameter/result `TypeRef` against the current snapshot;
- no duplicate required effect and every required `EffectId` exists;
- recipe depth/node limits using the maintained 32/64 bounds;
- every `ParameterSlot` is in range and has its declaration-derived type;
- every stable declaration reference in the body resolves in the current snapshot;
- every direct call/capability invocation is compatible with the requested function's declared effect set;
- body result type exactly matches the requested function result type;
- all ordinary scalar/List/structured/control-flow/Bind rules already maintained by `ExpressionRecipe`.

Only after that succeeds:

1. allocate the new function ID;
2. allocate parameter IDs in declared parameter order;
3. materialize the body using the slot-to-parameter mapping and fresh system-owned local identities;
4. run ordinary full snapshot validation;
5. expose requested creation intent and the exact materialized function separately in review.

The first creation slice does not support a recursive `DirectCall` to the function being created because no published `FunctionId` exists in the input intent. Self-recursive creation can be designed later if a concrete need justifies an ephemeral self reference. After publication, ordinary expression editing can refer to the stable function ID.

## Review and publication

Function creation uses the established simulate -> review -> exact commit/reject lifecycle.

Review must show at least:

- requested display name, ordered parameter intents, result type, required effects, and body recipe;
- newly allocated function and parameter IDs;
- complete materialized body structural summary;
- resulting Function Focus/declaration closure;
- readiness and receipt binding.

The request does not expose raw `SemanticFunction`, `ValueGraph`, or snapshot mutation authority.

## Function signature evolution

Treat signature edits as the same declaration-authoring family, but do not force unsafe state transitions merely for API symmetry.

### Add parameter

The existing `AddFunctionParameter` operation remains the accepted first signature-evolution slice. Its explicit per-call plan and missing-argument obligation behavior remain useful evidence.

### Remove parameter

A future remove operation may be added after the targeted-expression cleanup contract in [targeted expression replacement](targeted-expression-replacement.md) is implemented or equivalent local cleanup behavior exists.

The operation should require that the target function body no longer references the parameter. Removing the parameter then deterministically removes the corresponding argument binding from every direct call; it must not guess replacements for body references. Argument-value expressions made newly unused need the same bounded cleanup reasoning as targeted structural edits rather than becoming accidental detached state.

### Change parameter type

Defer the first implementation. Changing a parameter type can invalidate both function-body consumers and every caller argument. Current missing-argument obligations cannot represent arbitrary type-invalid intermediate graphs. A later operation should therefore bundle explicit compatible rewrites atomically or reject; it must not publish an invalid intermediate snapshot.

### Change result type

Likewise defer until an atomic body/caller rewrite contract exists. Changing only the declaration while relying on later repair would violate current full validation.

These deferrals are implementation ordering, not a decision that signature types are immutable forever.

## Editing existing required effects

Before creating effects themselves, add typed function effect-requirement edits over **existing** `EffectId` values.

Prefer two simple intents:

```text
AddFunctionRequiredEffect(FunctionId, EffectId)
RemoveFunctionRequiredEffect(FunctionId, EffectId)
```

They do not propagate automatically through callers and do not create obligations.

Multiple explicit edits may be composed in one ordinary transaction. Full validation of the final proposed snapshot remains authoritative:

- adding an effect ultimately requires every direct caller to declare the callee's requirements; callers may be edited explicitly in the same transaction or beforehand;
- removing an effect rejects when the function body, called function requirements, or capability invocations still require it;
- extra declared effects remain allowed, so a caller can be prepared before a callee requirement is added;
- effect IDs must already exist and duplicate requirements remain invalid.

This supports explicit propagation without inventing a hidden call-graph rewrite policy. Expression authoring continues to check the target's declared effects and never mutates them as a side effect.

## Later declaration stages

### Product and union

Use complete declaration-shaped construction with system-owned `TypeId`, `VariantId`, and `FieldId` values. Validate field `TypeRef`s explicitly. Do not introduce partial type declarations until a concrete workflow needs them.

The first type-declaration slice need not solve mutually recursive/newly co-referential declarations; add ephemeral declaration references only when that use case becomes concrete.

### Effect

Effect creation follows existing-effect requirement editing. Keep the semantic declaration minimal (`EffectId` plus presentation metadata) until another concrete validator/planner/reviewer needs additional properties.

### Capability and operation

Handle capability/operation declaration authoring after effects because each operation combines owner capability type, ordered parameters, result/failure types, and an effect association. Semantic declaration creation does not automatically register or implement a runtime/backend provider; that boundary stays explicit.

## Shared authority rules

Across declaration kinds:

- callers express typed construction/edit intent rather than persisted declaration records;
- production IDs are system-allocated;
- existing declaration references use typed stable IDs;
- full validation is normative at publication;
- requested intent and resulting declarations are distinct review facts;
- exact receipt binding and stale rejection remain unchanged;
- no declaration operation performs automatic semantic merge/rebase;
- expression authoring never smuggles declaration mutation into an expression request.

## Verification direction

Use focused fast tests for each new contract/state transition. Reuse the established transaction/review and expression-authoring evidence when the interaction assumptions remain the same.

The first function-creation implementation should cover at least:

- successful multi-parameter creation and deterministic parameter-slot resolution;
- system-owned function/parameter/body identities;
- slot bounds and exact body-result typing;
- required-effect validation;
- nested Bind/match body materialization through the existing recipe machinery;
- review and stale publication;
- raw-authority exclusion.

Existing-effect requirement editing should cover explicit multi-function transactions and final-validator rejection when caller/body requirements are unsatisfied.

No broad model corpus or routine extended/live CI is authorized merely because a declaration role is added. Reconsider exploratory model evaluation only when a later slice introduces a materially new multi-step repair/planning interaction.

## Consequences

- Nyrwvy can move from editing only pre-existing function declarations toward constructing new executable functions without caller-owned identities.
- The first creation slice stays complete/atomic rather than broadening incompleteness prematurely.
- `ExpressionRecipe` gains one context-dependent ephemeral parameter slot instead of a second creation-specific expression language or a stateful ID-preallocation protocol.
- Signature evolution remains incremental: safe existing parameter addition stays available, while type/result changes wait for an atomic rewrite contract.
- Required-effect evolution becomes explicit and composable before effect creation, with final graph validation—not hidden propagation—deciding whether the requested set is coherent.

## Revisit conditions

Revisit this boundary when real workflows require body-less function declarations, self-recursive creation, mutually dependent new declarations, atomic parameter/result type migration, partial type declarations, or capability declaration plus provider registration as one lifecycle.

Refs [#44](https://github.com/kw4r3n/nyrwvy-lang/issues/44), [#114](https://github.com/kw4r3n/nyrwvy-lang/issues/114), [#119](https://github.com/kw4r3n/nyrwvy-lang/issues/119), [#136](https://github.com/kw4r3n/nyrwvy-lang/issues/136), [#137](https://github.com/kw4r3n/nyrwvy-lang/issues/137), [#145](https://github.com/kw4r3n/nyrwvy-lang/issues/145).