# Explicit ordering for effectful expression composition

- **Status:** Accepted
- **Date:** 2026-09-08
- **Issue:** [#141](https://github.com/kw4r3n/nyrwvy/issues/141)

This decision selected the bounded Bind authoring contract in Issue #141. The subsequent implementation is described by the [expression authoring specification](../specs/expression-authoring.md), which remains the authority for the implemented surface.

## Decision

Use one scoped value-binding expression, `Bind(value, body)`. Evaluate `value` once, then evaluate `body` with that result available through a system-owned lexical binding. The expression returns the body's result. Ignoring the binding expresses sequencing without a second construct.

Keep existing dependency-ordered calls and genuine conditional/match control flow. Preserve `AmbiguousEffectOrder` for independent effectful siblings within each graph. Do not derive effect order from `ValueGraph.Nodes`, argument/field binding-list order, materialization traversal, or allocated IDs.

The accepted representation and implementation gates are in the [bounded Bind plan](../plans/ordered-expression-binding.md). This decision authorizes a design boundary, not a broad sequencing implementation, new CI job, or model corpus.

## Reason

The examples below use descriptive function/operation names as pseudocode. Actual recipes reference existing typed stable declaration IDs and must satisfy exact parameter, receiver, result, and predeclared-effect contracts.

| Motivating expression | What the current graph can express | Design consequence |
| --- | --- | --- |
| `send(consoleParameter, loadMessage())` | The message call precedes its consuming invocation through a value dependency; the receiver is pure. | No additional sequencing form is needed. |
| `send(acquireConsole(), literalMessage)` | The receiver call precedes its consuming invocation. | Ordinary nesting is sufficient. |
| `send(acquireConsole(), loadMessage())` | Both effectful calls precede `send`, but neither orders the other. Authoring/publication can succeed while lowering rejects. | Bind the receiver before entering a body that loads the message and sends it. |
| `match tryAcquireConsole() with Some c -> send(c, loadMessage()) | None -> fallback` | The scrutinee precedes the selected arm. A pattern reference reuses its payload, and the unselected arm does not execute. `Result` and union matches provide the analogous boundary. | Retain natural matches when selection is part of the requested behavior. |
| `audit(); loadMessage()` | Independent calls have no value dependency. A synthetic conditional or match can impose a boundary but adds a condition/wrapper and alternative body unrelated to the intent. | `Bind(audit(), loadMessage())` orders the calls even when the first result is unused. |
| Acquire one console, send two messages through it, and return that same console. | Repeating the acquisition recipe creates separate call occurrences. A natural match works if the acquisition already returns a matching structured value. | A once-computed value must remain referable across nested ordered bodies; discard-only sequencing is insufficient. |

For example, `MatchOption(OptionSome(acquireConsole()), someBody, noneBody)` can order acquisition before `someBody` using today's representation. This is a real semantic control-flow boundary, not node-list ordering. It is a useful existing workaround when both bodies can be supplied with the same result type. However, it requires an artificial wrapper and a statically checked fallback even though this particular input is always `Some`. For a body returning a capability, there is no general literal fallback capability; repeating acquisition or introducing another declaration just to satisfy that branch changes the request. A synthetic one-variant union likewise depends on a suitable existing declaration. Do not promise either encoding as a universal authoring contract or add unreachable-branch special cases to make it one.

An explicit scoped binding directly represents the common intent, supports arbitrary already-supported value types, and avoids a general local-symbol or arbitrary DAG-reference API. It reuses the existing pattern-binding identity and lexical-reference mechanisms rather than introducing a second local identity family.

## Consequences

- The authoritative graph gains an explicit input/body scope boundary. A recipe-only macro or backend-only ordering hint is insufficient.
- The new body binding is immutable and non-recursive. Recipe references remain lexical depth/slot requests; production node and binding IDs remain system-owned.
- Nesting binds specifies a sequence. Multiple references to one bound value reuse its evaluated result; they do not repeat its producer. Independent effects inside the input or body still require their own dependency or scope order.
- Target `RequiredEffects`, declarations, exact types, existing recipe bounds, review binding, stale rejection, and atomic publication remain enforced. This does not permit effectful List callbacks.
- The scope, binding, and input relationship must survive persistence, fingerprints, typed projection, semantic queries, change review, and execution lowering. Closed schema changes require explicit versioning under the [schema evolution policy](../specs/schema-evolution-policy.md).
- `SemanticallyReady` continues to describe semantic publication readiness. Execution diagnostics stay a distinct downstream result. The first implementation does not add an execution gate to simulation/publication; any later early advisory must reuse execution lowering and clearly distinguish the two readiness domains.

## Revisit conditions

Revisit this boundary if a concrete use case requires values escaping lexical scope, recursive bindings, mutation, shared nodes across unrelated recipes, effectful traversal, concurrency, or changes to effect declarations. Do not add those capabilities to satisfy the examples above. Reconsider a recipe-only encoding only if it preserves arbitrary result types, once-only evaluation, review clarity, bounds, and declaration reuse without artificial fallback behavior.

## Existing implementation evidence

These sources established the pre-Bind ordering boundary. The later Bind contract has focused coverage in [`AgentExpressionBindTests.fs`](../../tests/Nyrwvy.Core.Tests/AgentExpressionBindTests.fs) and generated execution coverage in [`CSharpBackendTests.fs`](../../tests/Nyrwvy.Core.Tests/CSharpBackendTests.fs).

- [`AgentExpressionCapabilityTests.fs`](../../tests/Nyrwvy.Core.Tests/AgentExpressionCapabilityTests.fs): published independent receiver/argument calls retain `AmbiguousEffectOrder`; dependency-ordered variants lower; a lexical capability invocation remains inside its match arm.
- [`ExecutionIrAnalysis.fs`](../../src/Nyrwvy.Execution/ExecutionIrAnalysis.fs), `validateEffectOrder`: compares effectful nodes by dependency reachability and validates nested graphs independently; control nodes include nested effects in their effect classification.
- [`ExecutionIrLoweringCore.fs`](../../src/Nyrwvy.Execution/ExecutionIrLoweringCore.fs), `lowerOptionMatch`: requires the scrutinee value before branching, extends the selected arm's pattern environment, and merges arm results.
- [`AgentExpressionStructuralWorkflow.fs`](../../src/Nyrwvy.Semantics/AgentExpressionStructuralWorkflow.fs), `MatchOption` materialization: allocates the binding system-side and materializes separate scoped bodies. None arms retain an empty lexical frame.

The synthetic-match examples above follow from these composition rules; they are not a claim of new end-to-end runtime measurements.
