# Scoped one-shot handoff

Status: Implemented and verified locally; publication requires separate approval.
Base: `a1d44a8b9687a1add4d9909616a4fb28a23b8be9`.

The [example](../../examples/scoped-result-handoff/README.md) has two request workers and one independent receiver. The receiver routes reversed responses by ID to the request workers. A single event loop already expresses the behavior, but its owner must perform both reading and request waiting. Result delivery uses the existing TaskScope lifetime, cancellation and join.

## Surface

`OneShot<T>` is a copyable opaque runtime handle owned by one existing TaskScope. Creating two cells creates independent handoffs; copying a handle shares its one outcome. There is no equality, public runtime ID, reset, queue, callback registration or new scheduler.

| Operation | Type and rule |
| --- | --- |
| `OneShotCreate(scope, valueType)` | `OneShot<T>`. Creates an empty cell owned by that scope. Creating through an already closed/cancelled scope returns a closed empty cell; it never starts work. |
| `OneShotComplete(cell, value)` | `Bool`. The exact `T` is required. Atomically accepts the first value while pending and owner open; returns true. Duplicate or closed-cell completion returns false without overwriting. Arguments use ordinary evaluation rules even on refusal. |
| `OneShotWait(cell, limit?)` | `Option<T>`. Observes the same accepted value as every other waiter, or None if its relative timeout elapses, its own ambient cancellation is requested, or the owner closes while unresolved. Wait does not consume, complete, cancel or reset the cell. Missing limit is unbounded; negative limits count as zero. |

All three operations are intrinsically effectful for purity and ordering, like existing task primitives. Calls and effectful callbacks propagate their callees’ runtime ordering behavior transitively, including intrinsic OneShot/task/resource/cancellation operations without declared effects; sequence dependent calls with Bind. Pure-worker ParallelAll remains non-observable for ordering. They do not silently add a nominal EffectId or widen a function's declared contract. Expected failures are ordinary payloads, e.g. `OneShot<Result<Response, RequestError>>`; they remain separate from host exceptions.

None deliberately means “no value observed,” not a distinguished timeout exception. A worker checks existing CancellationRequested when it needs to distinguish its cancellation. The broker checks it immediately after waiting, even after Some, and the coordinator checks before saving; observed cancellation returns Err and does not become a timeout or saved success. These checks are cooperative: cancellation arriving after the final check during an external save cannot atomically roll back that side effect. This minimal surface does not independently report closed versus timeout. A completed value is observed before checking cancellation/timeout, including zero-time waits.

## State, closure and races

A cell has exactly one terminal transition: Pending -> Completed(value), or Pending -> Closed. Scope closure and completion share a serialized decision; concurrent callers cannot both return true. A completion that wins remains observable after owner closure; one that loses returns false. A timeout or one waiter's cancellation is local and leaves an open cell pending for its other users. If completion becomes available before the wait's final observation, Some wins; no cross-task real-time ordering is promised.

The owner tracks only unresolved cells. Successful completion unregisters the cell under the same publication gate, so a long-lived scope does not retain completed payloads after their handles are discarded. An escaped completed handle still retains its own value. Closure empties the remaining registry.

On normal/Err/host-exception exit, close every unresolved cell before cancelling and joining the owned tasks. Parent cancellation closes unresolved cells and wakes their waiters promptly, without joining tasks inside a cancellation callback. Closing cells never bypasses the existing scope join or UseResource release. No scope-owned task survives the scope's completion.

Escaped handles are valid terminal handles: completed cells remain readable and closed empty cells return None immediately; completion returns false. A handle used from another scope retains its original owner. The waiting task's cancellation stops only that wait; the other scope cannot take ownership or reopen the cell.

Do not hold the cell/owner publication gate while blocking, scheduling, invoking callbacks or joining tasks. The virtual clock evaluates readiness predicates under its own gate; lock ordering must not introduce a reverse edge from the publication gate into the clock or task-start gate.

## Clock, typed graph and persistence

Virtual waits register a readiness predicate with the existing NyVirtualClock.Block, so active work prevents time advances, scripted delays retain their current ordering and due-time saturation, and a program with every task blocked and no future due event keeps the existing deadlock diagnostic. A completion wakes eligible real waiters and becomes visible to virtual waiters on the existing scheduler's next blocking/exit boundary; completing a cell does not introduce a yield. Real waits use the existing monotonic time policy, cooperative cancellation and bounded long timeout steps, never polling sleeps.

The new type may appear in ordinary function/context parameters, results, immutable containers and declared fields, like Task handles. It is not equatable. Runtime-bearing types must remain transitively excluded from Capability signatures and runPure input/output, including through Products/Unions/containers. TaskScope remains excluded from declared fields and must not become hidden inside a OneShot payload. No live cell state is serialized, and wire/runPure inputs cannot forge a live cell.

A type parameter used inside a OneShot payload must be instantiated with a type that contains no TaskScope. This requirement follows generic direct calls and callback bindings transitively, including a function that creates a cell internally and discards it. It is derived from the graph at validation, without persisting new constraints; both ordinary-tool simulation and full validation reject unsafe instantiations before projection. Unrelated generic arguments may still be TaskScope.

Ordinary name-resolved and exact-ID recipes expose the three operations. The same exact type/effect/boundary checks run at proposal simulation and full snapshot validation. Shared recipe reference, inspect/review, save/reopen, lowering and the C# projection must all preserve the typed constructs. New graph IDs are system assigned; targeted replacement preserves its node ID.

The type anywhere, or any of the three nodes, selects snapshot schema 14 (`nayamnyam.snapshot.v10`) in the central schema policy. Old graphs retain their existing minimum versions, bytes and fingerprints. Old readers reject the new schema explicitly. Typed and Execution IR debug output selects version 10 for these constructs, including local container types. Graphs without them retain their existing versions; IR output remains write-only debug data.

## Broker acceptance and comparison

Use a small existing Host/fake transport boundary for send/read/close only. Response-ID dispatch, pending cells, timeout and shutdown stay in the graph. The receiver and both request workers use the same existing TaskScope. The coordinator observes their task outcomes with existing TaskWait, using a common typed worker outcome where needed; a faulted receiver must not leave the coordinator waiting only on request cells.

| Case | Required observation |
| --- | --- |
| Reverse responses | `b` then `a` reaches the correct two independently waiting request workers. |
| One timeout | One waiter returns None; the other gets its value; timeout does not cancel the entire owner or erase a later value. |
| Duplicate | First completion returns true, second false; every waiter sees the first payload. |
| Receiver expected error / host exception | Expected Err completes pending request outcomes explicitly. A host exception is observed by the coordinator, closes cells through scope exit, joins all workers, and retains existing runtime fault frames/precedence. |
| Parent cancel | All pending waits wake; workers finish cooperatively; scope joins them; no post-close publication succeeds. |
| Close/races | Real-runtime barrier-controlled completion/completion and completion/close races; no double winner, lost wakeup or stuck join. Verify completed and empty escaped cells. |
| Release | The acquired transport closes exactly once after owned tasks finish, on every path. |
| Persistence/parity | Ordinary tool author/review/commit/save/reopen runs the broker; virtual fake and generated real-runtime harness agree on results and terminal invariants without assuming one cross-task call order. |

Keep the existing event-loop baseline and compare outputs and code responsibility, not a node-count quota. The current local baseline checks reverse/duplicate/receiver host exception and exactly-once release. Its separate-task probe checks that finishing one receiver does not finish another waiting request, followed by scope cancellation/join and release. The example verifies all six cases with the existing virtual clock, and its unchanged C# projection verifies the same cases with real threads. The real harness also checks multiple observers, completed values under cancellation, escaped terminal handles, cross-scope waiter cancellation and 200 controlled races.

Publication requires separate approval. No source checkout changes, paid provider runs, new agent wrappers, generic channels, scheduler, effectful Capability construction or existing process interruption are involved.
