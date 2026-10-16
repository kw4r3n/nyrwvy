# Task start after a seen ancestor cancellation — 2026-10-17

**Result:** the generated C# runtime now guarantees that a task started into a
scope after the starting thread has seen a cancellation of one of that scope's
ancestors starts with a cancelled token. Before, it could start clear and be
cancelled only a moment later. Virtual-clock execution is unchanged.

## Gap

`CancellationTokenSource.Cancel()` sets `IsCancellationRequested` and the
`WaitHandle` first, then runs the registered callbacks, newest first, on the
cancelling thread. Linked descendants are cancelled only by those callbacks.
`NyTaskScope` links its own token to its parent, the ambient token of whoever
created it. Its body keeps running under that parent token, and `Start` links
each task to the scope's token. So a thread woken by a cancelled ancestor
could call `Start` while the scope's own token was still clear, and the new
task read `CancellationRequested == false` until propagation caught up.

The flaky extended test `nyrwvy-authoring-member-owner-cancellation` was one
case of this: on 2-vCPU runners a late request saw `reject:closing` instead of
`err:cancelled`. kw4r3n/nyrwvy#414 worked around it only in that example's
owner-park barrier. Once this change lands, that barrier is no longer needed
(it does no harm).

## Options

1. **Keep the harness barrier only (status quo).** Each program that relies on
   this ordering needs its own barrier. Rejected.
2. **A cancelled starter implies a cancelled child.** Wrong for a shield
   (`CancellationShield` runs under `None` deliberately) and for starts into an
   outer scope, where a task cancelled by its own `TaskCancel` must still be
   able to start a clear task into its parent scope. Rejected.
3. **Check only the scope's direct parent in `Start`.** This covers starts from
   the scope body and from its descendants, because `Cancel()` flags every
   token on the propagation path before any token below it. It misses a scope
   reached through a capture that does not go through its parent. Example:
   `inner` is created by task `owner` of `outer`, and a later task `member` of
   `outer` captures it. TaskStart captures may hold a TaskScope. Callbacks run
   newest first, so `outer`'s cancellation reaches `member` before `owner`.
   `member` wakes, starts into `inner`, and `inner`'s parent (`owner`) is not
   flagged yet. The regression test shows this variant fails. Rejected.
4. **Atomic propagation behind a runtime-wide gate.** Cancellation inside the
   scope tree takes a global lock, and so does `Start`. All starts would be
   serialized. Host callbacks would run under the lock, which risks deadlock
   when a callback starts a task. A host-supplied root token is cancelled
   outside the gate, so option 3's check is still needed at that boundary.
   Rejected as heavier with no extra guarantee.
5. **Ancestor-path check (chosen).** Each runtime-made token carries its path
   of ancestor tokens (`NyCancellationPath`, in an internal `AsyncLocal` next to
   `NyRuntime.Cancellation`). The path is set by `NyTaskScope.Start` (normal
   and late starts) and by `ParallelAll` workers. Before linking a new task,
   `Start` cancels the scope itself if any ancestor on its path is already
   flagged. That scope would be cancelled by propagation anyway, so this only
   moves the cancellation earlier; no new cancellation is introduced. It needs
   no lock: if a starter has seen ancestor A flagged and A is an ancestor of
   the target scope, A is on that scope's path. The cancel runs outside the
   `_tasks` lock because its callbacks take `HandoffGate`.

A shield does not change the path: the path of `None` is empty, so a scope
created under a shield has no ancestors. A host token installed directly
(tests, runners) does not match the head of the ambient path, so it starts a
new path. The cost is one walk over the scope's nesting depth per `Start`.

## Evidence

`TaskScopeTests` ``a task started after an ancestor's cancellation is seen
starts cancelled on the real runtime`` (Extended) emits the team program,
compiles it with a probe `Program.cs`, and runs it on real threads. Each case
holds the window open with a `Thread.Sleep(300)` cancellation callback:

| Case | Expected | Old runtime | Direct-parent check (3) | Path check (5) |
| --- | --- | --- | --- | --- |
| `host-parent`: start into a scope whose host parent was seen cancelled | cancelled | **clear** | cancelled | cancelled |
| `sibling-owned`: start into a scope owned by a not-yet-cancelled sibling | cancelled | — | **clear** | cancelled |
| ↳ start into a shielded scope after that | clear | — | — | clear |
| `member-only`: only the starting task is cancelled; start into `inner` and `outer` | both clear | — | — | clear |

"—" means the case did not run because an earlier case had already failed.
The fast and extended suites were run with the change. The virtual clock steps
one logical thread at a time, so no start can observe a partly propagated
cancellation there, and the new check is a no-op.
