# Effect ordering checked at proposal time

- **Status:** Accepted
- **Date:** 2026-10-18
- **Refines:** [Effectful expression ordering](effectful-expression-ordering.md) (its "no execution gate on simulation/publication" consequence) and the semantic admission rule of the [typed function-abort contract](../specs/typed-function-abort.md)

Semantic validation ordered effects only in functions containing local Abort, and then counted a callee as observable only when it declared effects. Lowering orders every function and also counts callees that are observable through intrinsic operations (OneShot, task, cancellation, resource) in their bodies or their own callees. So a helper with no declared effects whose body is `OneShotComplete(cell, value)`, called twice from an Abort-free caller with no dependency between the calls, simulated as `semanticallyReady` and committed, and then `ProjectExecution` failed with `AmbiguousEffectOrder`. A `CancellationRequested` helper beside a local Abort did the same. The author learned about the problem only after publishing, and the two places held two definitions of "observable" that could drift.

## Decision

- One definition of the ordering rule lives in Semantics (`GraphOrder`): the observable-function closure (declared effects, or any intrinsic node other than a local Abort, or a call into an observable function through a direct call, effectful fold callback, worker or body), the per-node observability test and the unordered-pair search. Lowering calls the same functions on typed nodes. Only the per-kind classification is written once per node type, and a reflection test compares the two for every node kind, including intrinsic and transitive helpers and helpers that only abort.
- Every proposal and every commit of a reviewed candidate rejects `AmbiguousEffectOrder` for each function the proposal adds, edits (body or declared effects), or makes newly ambiguous because a callee became observable. This applies with or without Abort. The message names the function, both unordered nodes (kind and callee or capability operation), and the Bind repair.
- Full snapshot validation keeps the historical abort-only rule, so it accepts every snapshot it accepted before. A function that was published earlier and that the runtime rule finds ambiguous is tolerated while it stays unchanged. Lowering still rejects it, proposals that do not touch it still publish, and any edit to it must be ordered.

## Reason

The author should learn about an ambiguity when it is introduced, from the same rule execution applies, instead of after publication. Comparing against the published base keeps the check strict for new work without turning existing snapshots into unopenable files: they stay repairable through the ordinary edit path.

## Consequences

- `semanticallyReady` now implies that no function the proposal added or edited would fail lowering for effect order. Ordering conflicts can still exist in a snapshot only in functions inherited unchanged from a snapshot published before this decision.
- Changing a helper so that it becomes observable is refused while an unchanged caller would become ambiguous; the caller has to be ordered first or in the same proposal.
- Tests that published an ambiguous graph to observe the lowering error now assert the proposal refusal instead, or build a legacy snapshot directly.
- No snapshot, Nyason or IR schema changes.

## Revisit conditions

Revisit if full validation should adopt the runtime rule as well (for example at a schema bump that already requires migrating stored graphs), or if a new node kind's ordering classification cannot be expressed through the shared view.
