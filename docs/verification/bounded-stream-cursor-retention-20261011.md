# Bounded-stream prerequisite: retained moving cursors

Historical baseline. The separately authorized and verified repair is recorded in
[TaskStart capture lifetime repair](task-start-capture-release-20261011.md).
The current runtime harness expects collection while workers and owner are open;
the retention assertions described here belong to the original reproducer.

Date: 2026-10-11.
Base: `90e495c75dd6915440ea22c96222aefcf04f723e` (main after #375).

## Result and stopping point

The moving-cursor prerequisite reproduced a live-task retention cost. Two graph
workers advanced their OneShot cursors 4 times each, and then 256 times each.
While both workers remained alive at an I/O checkpoint, the original heads
remained reachable and still led to all 4/4 and 256/256 completed links.
The owner registry held only two unresolved cells in both runs.

After task settlement **and owner closure**, the registry was empty and both
original heads collected, even while the owner object was kept alive. This
negative control is not evidence of collection while the owner remains open.

This establishes a minimal blocker for claiming that this long-lived-worker,
forward-linked moving-cursor composition has bounded retained history. It does
not establish that every stream expressible with existing primitives must
retain history. Rotating scopes or windowed batches have different lifetime
and overlap behavior and were not substituted silently.

The agreed stopping point is reached: preserve the reproducer and a smallest
candidate repair. No Channel primitive, runtime semantic change, or core repair
is included. The full capacity-2 streaming example is not claimed complete.

## What was verified

Reported by the local .NET verifier against the pinned base:

- Cloud/source and offline compiler-helper checks: 5/5
- Ordinary authoring: 6 functions, 81 graph nodes
- Saved virtual-time cases: 2/2 (4 and 256 advances per worker)
- Reopened snapshot replay: 2/2
- Unedited generated-C# real-runtime retention cases: 2/2
- Live original-head chain lengths: 4/4, then 256/256
- Live unresolved-cell registry entries: 2 in each case
- After settlement and closure: registry 0; original heads collected
- `Generated.cs` hash unchanged through runtime verification

The ordinary authoring uses create/review/commit tools and saves/reopens its
snapshot. Virtual fixtures execute the authored `run` entry with two workers.
For the real GC diagnostic, the harness directly invokes the same authored
`newCursor`, `spawn` and `retainedLength` functions, with `spawn` launching
`cursorWorker` through its TaskStart callback. This lets the harness keep only
weak references to the initial heads. It does not edit the emitted
projection or pass OneShot handles through a Host capability boundary.

The local evidence archive was reported as 135466 bytes, SHA-256:

`928e61f5d112fcb59c4a8b6aa1f1f6a5c87cc219ccfedd334e56f67f908da383`

The archive contains the verifier's `REPORT.ja.md` and execution evidence.
This document records reported local results; it is not a claim that the cloud
executor ran .NET or that the repository's full test suite was rerun.

## Probe design and controls

Each worker creates one next cell, completes its current cell with
`Link { next, sequence }`, and self-tail-calls with the next cell. It does not
keep a graph log or traverse from its initial head. On reaching its requested
count it blocks at a Host I/O checkpoint. Two such workers run concurrently.

The real harness creates each initial head in a separate non-inlined method,
retains only a WeakReference after returning from that method, and forces GC
while both workers are blocked. A separate non-inlined inspection method walks
any surviving original head using the authored `retainedLength` function.
Returning a scalar length prevents that inspection frame from carrying a head
into the later collection control. The owner remains alive via GC.KeepAlive.

Reflection is used only in the diagnostic harness to count the private pending
registry. It is not exposed to the graph or offered as a production API. The
registry's constant count excludes completed-cell registration as the cause of
the observed retained chain; successful OneShot completion already unregisters
its cell under the publication gate.

Per worker the loop performs N next-cell allocations and N completions, followed
by one checkpoint; each worker also starts with one separately allocated head. The production loop does not re-walk history. The diagnostic
walk performs N link steps after GC solely to measure the retained chain. The
observed chain length grows from 4 to 256 even though unresolved registry size
stays constant. These are structural/operation counts, not throughput or heap
byte measurements.

## Attribution and smallest candidate repair

At the pinned base, `CSharpBackendEmitter.fs` TaskStart lowering (lines
2187–2202) copies each context and item into fresh block-scoped variables and
captures those variables in the worker callback. They are never cleared. The
runtime's `NyTaskScope.Start` wraps that callback for task execution.

This is a concrete candidate path retaining the initial cursor. The reproducer
establishes reachable chains during the workers' lifetime, not an exclusive GC
root-path attribution: JIT-preserved function-entry parameters or prologue
locals may also contribute. The emitter copies entry parameters into mutable
loop locals (lines 2466–2467), and their actual stack liveness is runtime-specific.

The smallest candidate repair is to consume each TaskStart capture once when
the callback begins:

1. Copy each captured context/item into callback-local invocation arguments.
2. Clear the closure's captured slots before invoking the worker.
3. Invoke with those saved arguments, retaining existing per-start snapshots,
   cancellation behavior and exactly-once invocation, including late starts on
   closed scopes.

This would be an emitter/lifetime repair, not a new communication primitive.
It must be separately implemented and verified; clearing fields alone is not
proof of collection. A post-repair test should require original heads to collect
**while both tasks and the owner are still open**, with the unresolved registry
still at two. Preserve the current after-settlement/closure negative control
and recheck TaskStart capture isolation across tail-loop starts. If live roots
remain, investigate the actual remaining root rather than widening semantics
or weakening the assertion.

## Authoring friction and limits

The 6-function, 81-node graph and lexical/context recipes passed ordinary
creation and replay without a reported authoring failure. This small result
does not justify an editor redesign or quantify larger-program authoring cost.
The diagnostic needed explicit weak-reference framing and generated-symbol
mapping in its C# harness; those are measurement concerns, not graph APIs.

A bound on unresolved cells or outstanding records is not a bound on retained
completed history or measured heap size. This prerequisite does not test
capacity-2 admission, slow-sink backpressure, acknowledgements, graceful Stop,
cancellation/fault wakeup, exact-once sink delivery, or resource cleanup. Those
remain the downstream bounded-stream acceptance criteria after the retention
issue has an evidence-backed resolution. No rollback or complete accepted
history guarantee is added to abort behavior.
