# Owned reader failure propagation during foreground work

This continuation starts at reviewed/pushed `43507a5aee1aa0f706ec4b86e19392936fdcd94a`
(tree `a5ac4707f764dcd2219e7d16badb57a29f5b18c3`, PR399 stacked on PR398).
The independent review measured 1,008 ms after reader end without outcome,
HEAD or release while a foreground command stayed held at commit. External
SIGTERM then released the protected effect and allowed joined cleanup.

The new composition starts the dispatcher as an owned task. An owned observer
waits on the reader and returns the same result type as the dispatcher, so the
session body uses ordinary `TaskWait(any)` to observe either failure. The
existing execution scope cancels and joins its tasks on body exception,
preserving the first observed exception. No backend, schema, runtime or core
primitive changes. The inherited mutation shields and close-latch ownership
remain intact. A successful reader EOF waits for dispatcher completion.

This bounds failure **observation and cooperative cancellation**, not protected
effect duration or total session completion. The held commit ignores
cancellation under its inherited shield. A peer-controlled marker file releases
that test effect independently of reader faults and SIGTERM. Supported fault
rows observe a separate linked provider cancel before gate release, while HEAD,
save, application outcome and release stay absent during a 350 ms hold. After
gate release, HEAD/save complete once under a clear cancellation token, the
original reader exception object/stack survives, and all work joins before one
descriptor close. The broad observation budget is 1 s, not a runtime deadline
or universal scheduling guarantee. Uncooperative effects can delay completion.

The host remains Linux framing/syscalls plus declared synthetic application
effects. Added test observations record provider cancellation and protected
tokens. The external gate never responds to faults or cancellation. A separately
authored old graph uses the same binding and fails the same prompt-cancellation
contract for both oversize and malformed UTF-8. An unrelated exception is
preserved but refused as oversize evidence.

Eight supported rows, two causal controls and one unrelated-fault guard cover
held foreground oversize/UTF-8, healthy/EOF held commands, idle faults, retained
partial-input cancellation and fragmented valid input. All executions are
serial. Snapshot replay reopens/re-emits/recompiles the moved bundle and checks
exact source/binding/assembly bytes, observed peer input/output and hash closure.
Generated C# is never edited. Compilation uses installed .NET 10 Roslyn and
reference assemblies without generated-fixture restore or network access.

```nu
node examples/offline-session-reader-propagation/run.mjs artifacts/reader-propagation/bundle
node examples/offline-session-reader-propagation/run.mjs artifacts/reader-propagation/bundle/deferred --deferred
node examples/offline-session-reader-propagation/compile.mjs artifacts/reader-propagation/bundle
node examples/offline-session-reader-propagation/compile.mjs artifacts/reader-propagation/bundle/deferred
node examples/offline-session-reader-propagation/test-runtime.mjs artifacts/reader-propagation/bundle
node examples/offline-session-reader-propagation/probe-restart.mjs artifacts/reader-propagation/bundle
cp -r artifacts/reader-propagation/bundle artifacts/reader-propagation/moved
node examples/offline-session-reader-propagation/run.mjs --replay artifacts/reader-propagation/moved
node examples/offline-session-reader-propagation/run.mjs --replay artifacts/reader-propagation/moved/deferred
node examples/offline-session-reader-propagation/compile.mjs artifacts/reader-propagation/moved
node examples/offline-session-reader-propagation/compile.mjs artifacts/reader-propagation/moved/deferred
node examples/offline-session-reader-propagation/test-runtime.mjs artifacts/reader-propagation/moved
node examples/offline-session-reader-propagation/probe-restart.mjs artifacts/reader-propagation/moved
node examples/offline-session-reader-propagation/verify.mjs artifacts/reader-propagation/bundle artifacts/reader-propagation/moved
node --experimental-strip-types --test --test-concurrency=1 .opencode/tests-extended/nyrwvy-authoring-offline-session-reader-propagation.test.mjs
```

The one next-gap probe closes peer stdout at the held integration, releases the
effect, and observes once-only commit/HEAD/save followed by an output exception.
Input remains open, so successful cleanup also exercises dispatcher-fault
supervision. A fresh process receives the identical request bytes and ID and
repeats commit/HEAD/save: correlation alone provides no durable deduplication.
This is synthetic effect repetition; there is no physical Git mutation or
durable journal. See the [capability checklist](../../docs/verification/reconstruction-capability-checklist.md)
for the application-level journal/restart milestone and remaining boundaries.

The process deliberately exits zero after recording its structured outcome.
Only the outcome, phase counts and lifecycle establish a passing contract.
Source/artifact closure excludes mutable scenarios, transcript, compiler/tool
and reference pack binaries and environment state; it is not environment
closure. No live providers or publication are part of this cycle.

Retained [proof](fixtures/proof.json), [replay audit](fixtures/replay.json) and
[restart probe](fixtures/restart.json) identify the exact observed bundles.
See the [verification record](../../docs/verification/offline-session-reader-propagation-20261014.md)
for fresh validation and measured limits.
