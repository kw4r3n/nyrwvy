# Derived runtime ordering

**Status:** Current

Ordering observability is a derived fact, separate from declared nominal EffectIds. A function is observable if it declares effects, contains an intrinsic capability/resource/task/OneShot/cancellation operation, or executes another observable function through a DirectCall, effectful list callback, worker, or body. Nested branches are conservatively included. Recursive call cycles terminate in a finite reverse-call-graph worklist: a pure cycle is pure; a cycle reaching an observable seed is observable.

Typed Execution IR lowering uses this closure. Semantic admission for graphs containing local Abort retains its historical nominal-callee classification, so snapshots published by earlier versions remain openable and repairable; execution now rejects latent intrinsic ordering conflicts which admission historically accepted. Independent runtime-observable nodes require a dependency path or explicit Bind sequencing. Extracting intrinsic operations into helpers preserves this rule. Bind and branch scopes keep their own local dependency graphs; observability does not flatten their execution boundaries.

ParallelAll has no ordering effect of its own when its worker is pure. FunctionReference and CapabilityConstruct create values rather than executing their targets. Pure function values and pure collection callbacks continue to use the stricter PureFunctions validation; they are not treated as arbitrary hidden effect sources by ordering analysis.

Abort exits its own function and is observable locally. Calling a function which contains only local Abort returns an ordinary Result and does not acquire a runtime ordering effect merely because that callee aborts. No nominal effect is added to the function contract, and no snapshot or IR schema changes are needed.
