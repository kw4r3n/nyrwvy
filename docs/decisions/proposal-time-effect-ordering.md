# Effect ordering checked at proposal time

- **Status:** Accepted (amended 2026-10-28: full validation applies the runtime rule)
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

## Amendment: full validation applies the runtime rule (2026-10-28)

The revisit condition below was met by the Zig port of the lowering: its parity test found 3,402 models that validate and then fail lowering with `AmbiguousEffectOrder` (docs/decisions/zig-tools.md, "Third tool"). The user decided that a model which validates must never fail lowering on effect order, accepting that some models valid today become invalid.

- **Rule.** `SnapshotValidator.validate` reports `AmbiguousEffectOrder` for every function with an unordered observable pair under the runtime rule: `GraphOrder.tryEffectOrderConflict` with `semanticObservableFunctions` over the snapshot's functions, with or without Abort. `tryAbortOrderConflict` goes. The message is the one proposals already use (both nodes with their kind and callee or capability operation, and the Bind repair), with the function prefix full validation gives it.
- **Proposals.** With full validation strict, the proposal-only check adds nothing: `newEffectOrderConflicts` and its tolerance of unchanged legacy functions go, and `validateProposal` is `validate`.
- **Why it holds for every entry.** Lowering checks the functions an entry reaches, with the observable set of that selection. A function's observability depends only on its own body and its callees (through calls, effect callbacks, workers and bodies), all of which an entry that reaches the function also reaches, so the selection gives the same set for the functions it holds, and full validation has already checked each of them. The pair the two name can differ (the typed view visits nested graphs by node ID and union arms by their first variant), but whether one exists cannot. This rests on the typed node's references (`TypedSemanticNodeKind.valueReferences`) equalling the semantic ones (`SemanticNodeShape`), which the Zig lowering's byte-identical comparison exercises on every node kind but no test states on its own.
- **Zig.** `model-reader --validate` applies the same rule (its observable set last-wins per function ID, as `Map.ofSeq`), so the read path and the extended parity tests stay aligned.
- **Consequences.** A snapshot with a conflict that the historical rule tolerated no longer decodes: opening it reports `InvalidSnapshot` with `AmbiguousEffectOrder`, and there is no tolerated-legacy path left to repair it through the session. No repository snapshot or fixture changed (3 `.nyrec` and 4 JSON snapshots checked); the one test model that did, the all-kinds snapshots' shared `scopeBody` function, now orders its task and OneShot operations with Bind. The structured `ExecutionLoweringFailure` details for this error are no longer reachable through a session; the validation issue names the same nodes and repair. Tests that built a legacy ambiguous snapshot to observe the lowering error assert the validation issue instead.

## Revisit conditions

Revisit if full validation should adopt the runtime rule as well (for example at a schema bump that already requires migrating stored graphs), or if a new node kind's ordering classification cannot be expressed through the shared view.
