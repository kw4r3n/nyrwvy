# Bounded ordered expression binding

**Status:** Completed implementation plan for the [accepted effect-ordering design](../../docs/decisions/effectful-expression-ordering.md). Issue #141 recorded the design; implementation followed separately. This document retains the planned boundaries and gates; the maintained contract is in the [expression authoring specification](../../docs/specs/expression-authoring.md).

Completion evidence is recorded in [ordered expression binding verification](../../docs/verification/ordered-expression-binding-20260908.md).

## Authoring and authority boundary

The intended in-process recipe case is:

```text
ExpressionRecipe.Bind(value: ExpressionRecipe, body: ExpressionRecipe)
```

Infer `value` in the enclosing lexical environment. Infer `body` in one additional frame containing `RecipePatternSlot.Value` with exactly the inferred value type. `PatternReference { ScopeDepth = 0; Slot = Value }` refers to this binding; an outer frame is reached by increasing the depth. `Field` and `Error` slots are invalid in this frame. Existing match frames, including empty frames, continue to count normally.

The new binding is visible only inside `body`, including its nested bodies. It is not visible in its own initializer, cannot escape through a reference outside the body, and has no caller-provided name, type, or stable ID. An initializer may reference a binding from an enclosing scope. The whole expression has the body's result type, which must still match the target function's declared result type.

Both children count toward the existing 32-depth / 64-node recipe bounds. There is one additional semantic Bind node and one system-allocated binding, with no hidden wrapper or fallback branch. Both children's effects must already be declared by the target; the implementation must not rewrite `RequiredEffects` or any declaration. Existing obligation protection and complete-call checks remain in force.

Conceptual examples (`ref(depth)` means the Value slot at that lexical depth):

```text
// Receiver, then message, then invocation; send returns the body result.
Bind(acquireConsole(), send(ref(0), loadMessage()))

// Discard a result but retain its effects before the body.
Bind(audit(), loadMessage())

// Acquire exactly once, send in order, and return the original capability.
Bind(acquireConsole(),
  Bind(send(ref(0), "first"),
    Bind(send(ref(1), "second"), ref(2))))
```

These are schematic recipes, not newly supported source syntax. Supplying the same acquisition recipe twice still asks for two evaluations; only references to the lexical binding reuse the result. Binding an `Option` or `Result` does not unwrap it, short-circuit it, or introduce failure handling. Use existing matches for those behaviors.

## Authoritative representation

Add a semantic node with the conceptual shape:

```text
SemanticNodeKind.Bind(inputNodeId: GraphNodeId, body: ValueMatchArm)
ValueMatchArm = { ValueBinding: PatternBinding; Body: ValueGraph }
```

Reuse the existing `PatternBinding`, `PatternBindingId`, and `PatternReference` machinery. The existing value-arm carrier can hold the binding and body without introducing another identity domain; its use here does not imply a conditional branch or an Option/Result extraction.

Materialize input occurrences in the containing graph, allocate a fresh binding with the inferred input type and `DisplayName = None`, and materialize the body in its extended environment. The explicit Bind node references the input and owns the nested body. Existing materialization determinism is retained only for construction and presentation.

Semantic validation must check input existence/type, binding uniqueness/type, lexical visibility, body validity/result type, and effects. Graph traversals must include the body and correctly distinguish its local references from enclosing free references. Reject forged binding types, invalid scopes, or cycles through the ordinary validation boundary.

The Bind boundary means that the entire input evaluation completes before the body begins, even when the binding is unused. Body-local effects cannot be hoisted into the containing graph or run before the input. This adds an explicit dependency/scope relationship, not incidental order to existing sibling lists.

## Persistence, review, and execution

| Boundary | Required behavior in the implementation change |
| --- | --- |
| Snapshot and Nyason persistence | Encode/decode the input reference, binding identity/type, nested graph, and Bind node kind; retain strict decoding and choose explicit new schema versions for changed closed contracts. Old artifacts must never acquire inferred effect order during migration. |
| Fingerprints | Include the new semantic relationship and body through the existing canonical mechanism. Preserve node-list permutation invariance; reversing nested effectful binds is a semantic change. Apply the fingerprint-domain version policy explicitly. |
| Focus/query and change review | Show the input, body scope, binding type/references, and before/after order. Added/removed node summaries must include nested nodes. A flat allocation-order list alone must not be presented as evidence of execution order. |
| Simulation/publication | Reuse guarded body replacement, system allocation, exact preview/receipt binding, stale rejection, and atomic commit. The reviewed graph is the published graph. |
| Typed projection | Retain Bind's input, typed binding, body scope, result type, and semantic origins rather than flattening to unordered siblings. Version the changed closed typed contract. |
| Effect analysis | Classify Bind as effectful when its body is effectful; input effects are tracked through the input dependency. Validate the input's containing graph and the nested body, preserving rejection of unordered effects in either. Include enclosing free references in dependencies as for existing scoped nodes. |
| Execution lowering | Obtain the input value once before entering the body, extend the lexical binding environment with that value, lower the body, then associate its result with the Bind node. Preserve node/binding provenance. Reuse existing block/value/environment machinery; no runtime match or synthetic extraction is needed. Determine whether existing Execution IR instructions can carry the required provenance before choosing an IR schema change. |
| Backend | Consume explicit Execution IR order. Do not reconstruct ordering from semantic IDs or source-generation traversal. Effects execute once per reached Bind evaluation; unselected outer branches remain unexecuted. |

Do not prune an unused initializer merely because no reference reads the binding: effects must still execute. Existing dependency ordering continues to govern independent expressions; Bind does not make two effectful siblings inside its body ordered automatically.

## Execution-readiness reporting

Keep simulation and semantic publication independent of Execution IR support. Preserve the current `SemanticallyReady` outcome and the separate `ExecutionIr.lower` result. No new early diagnostic API is required for the first Bind slice.

If an author-facing preview advisory is added later, run the existing typed projection/lowering on the exact reviewed snapshot from a downstream layer, without making Semantics depend on Execution. Report the function and node origins from `AmbiguousEffectOrder`, bind the advisory to that snapshot, and distinguish execution failure or an unavailable check from semantic invalidity. An error in an unrelated function is not proof that the edited body is invalid. Do not duplicate ordering analysis in recipe preflight or turn such an advisory into a new publication gate.

## Targeted validation gates

Before claiming an implementation complete, use focused contract tests for:

1. **Motivating behavior:** independently effectful receiver/argument recipes still reject without Bind; the explicitly bound version lowers and produces the requested observable trace. Reverse nested binds and verify reversed effect order. Include an unused Unit result followed by another effect.
2. **Once-only binding and scopes:** reuse one effectfully acquired value in two ordered operations and return it without reacquisition. Cover nested binds mixed with matches, empty match frames, outer references, invalid depth/slot, self/out-of-scope references, and forged persistent binding types.
3. **Remaining ambiguity and branches:** unordered effects inside an initializer or body, and an independent effectful sibling of Bind, still reject. A Bind in an unselected conditional/match arm has no effects. Unused initializers retain their effects.
4. **Authority and bounds:** preserve target effects and unrelated declarations; reject missing effects and wrong target result types. Cover depth/node limits with nesting, system-owned fresh IDs, and existing guarded publication/stale recovery for a bound body.
5. **Durable meaning and review:** round-trip the supported persistence/typed encodings, check strict version/kind rejection and explicit migration policy, and verify binding identity/scope and ordered structure in fingerprints and review. Permuting node storage must not change behavior or semantic fingerprint; reversing the ordered structure must be detected.
6. **Execution integration:** inspect the explicit lowered control/data flow and run a small recording-capability backend test to verify effect count/order and returned value. Verify semantic and binding provenance for diagnostics without adding a broad runtime subsystem.

Run focused tests and the existing fast suite. The recording-capability integration belongs at this semantic/lowering completion boundary under the existing [verification philosophy](../../docs/developer/verification.md); place it in the appropriate existing suite. No new CI job, frozen corpus, or controlled model tranche is part of this plan.

General local symbols, arbitrary DAG sharing, recursive/mutable bindings, automatic sequencing of lists/arguments, effect-declaration editing, concurrency, and new failure semantics remain outside this slice.
