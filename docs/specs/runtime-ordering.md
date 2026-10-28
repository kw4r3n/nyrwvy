# Derived runtime ordering

**Status:** Current

Ordering observability is a derived fact, separate from declared nominal EffectIds. A function is observable if it declares effects, contains an intrinsic capability/resource/task/OneShot/cancellation operation, or executes another observable function through a DirectCall, effectful list callback, worker, or body. Nested branches are conservatively included. Recursive call cycles terminate in a finite reverse-call-graph worklist: a pure cycle is pure; a cycle reaching an observable seed is observable.

Independent runtime-observable nodes require a dependency path or explicit Bind sequencing. Extracting intrinsic operations into helpers preserves this rule. Bind and branch scopes keep their own local dependency graphs; observability does not flatten their execution boundaries.

## Where the rule is enforced

There is one rule, defined once in `GraphOrder` (Semantics): the observable-function closure, the per-node observability test and the unordered-pair search. Semantic admission applies it to semantic nodes and Typed Execution IR lowering applies it to typed nodes through the same functions; only the per-kind classification is written once per node type, and a reflection test checks the two classifications agree for every node kind, including transitive helpers.

- **Validation, and so proposals, commits and open.** Full snapshot validation rejects `AmbiguousEffectOrder` for every function with an unordered observable pair, so every proposal (the transaction path and the structural, migration and declaration workflows), every publication of a reviewed candidate and every open or deserialize applies the rule (since 2026-10-28; before that, full validation kept a historical abort-only rule and only proposals applied this one). The diagnostic names the function and both unordered nodes (kind, callee or capability operation) and asks for Bind sequencing. This holds whether or not the function contains Abort, so an Abort-free caller of an intrinsic helper (for example two unordered calls to a helper whose body is `OneShotComplete` or `CancellationRequested`) is refused before publication instead of failing later at lowering.
- **Snapshots from before that change.** A snapshot holding a function the historical rule tolerated no longer opens; it reports `InvalidSnapshot` with the `AmbiguousEffectOrder` issue.
- **Lowering.** Still rejects every unordered pair, as a second line; on a validated model it cannot find one.

ParallelAll has no ordering effect of its own when its worker is pure. FunctionReference and CapabilityConstruct create values rather than executing their targets. Pure function values and pure collection callbacks continue to use the stricter PureFunctions validation; they are not treated as arbitrary hidden effect sources by ordering analysis.

Abort exits its own function and is observable locally. Calling a function which contains only local Abort returns an ordinary Result and does not acquire a runtime ordering effect merely because that callee aborts. No nominal effect is added to the function contract, and no snapshot or IR schema changes are needed.
