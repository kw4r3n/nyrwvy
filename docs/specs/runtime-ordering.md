# Derived runtime ordering

**Status:** Current

Ordering observability is a derived fact, separate from declared nominal EffectIds. A function is observable if it declares effects, contains an intrinsic capability/resource/task/OneShot/cancellation operation, or executes another observable function through a DirectCall, effectful list callback, worker, or body. Nested branches are conservatively included. Recursive call cycles terminate in a finite reverse-call-graph worklist: a pure cycle is pure; a cycle reaching an observable seed is observable.

Independent runtime-observable nodes require a dependency path or explicit Bind sequencing. Extracting intrinsic operations into helpers preserves this rule. Bind and branch scopes keep their own local dependency graphs; observability does not flatten their execution boundaries.

## Where the rule is enforced

There is one rule, defined once in `GraphOrder` (Semantics): the observable-function closure, the per-node observability test and the unordered-pair search. Semantic admission applies it to semantic nodes and Typed Execution IR lowering applies it to typed nodes through the same functions; only the per-kind classification is written once per node type, and a reflection test checks the two classifications agree for every node kind, including transitive helpers.

- **Proposals and commits.** Every proposal (the transaction path and the structural, migration and declaration workflows) and every publication of a reviewed candidate rejects `AmbiguousEffectOrder` for each function that the proposal adds, edits (body or declared effects), or makes newly ambiguous because a callee became observable. The diagnostic names the function and both unordered nodes (kind, callee or capability operation) and asks for Bind sequencing. This holds whether or not the function contains Abort, so an Abort-free caller of an intrinsic helper (for example two unordered calls to a helper whose body is `OneShotComplete` or `CancellationRequested`) is refused before publication instead of failing later at lowering.
- **Full snapshot validation (open, reopen, deserialize).** Keeps the historical rule: only functions containing local Abort are ordered, and a callee counts as observable only when it declares effects. A snapshot published before proposals used the runtime rule therefore still opens. A function it carries that the runtime rule finds ambiguous keeps failing at lowering with `AmbiguousEffectOrder` until it is edited; proposals that leave it untouched still publish, and any edit to it must be ordered.
- **Lowering.** Still rejects every unordered pair, so execution never depends on node order even for such a legacy function.

ParallelAll has no ordering effect of its own when its worker is pure. FunctionReference and CapabilityConstruct create values rather than executing their targets. Pure function values and pure collection callbacks continue to use the stricter PureFunctions validation; they are not treated as arbitrary hidden effect sources by ordering analysis.

Abort exits its own function and is observable locally. Calling a function which contains only local Abort returns an ordinary Result and does not acquire a runtime ordering effect merely because that callee aborts. No nominal effect is added to the function contract, and no snapshot or IR schema changes are needed.
