# Owned input → ordered offline session dispatch

This follow-up starts at reviewed `b655d24ff70f67334cdbc9d43c40a0ddf64d1fe8`,
tree `a294493609f2ee63fdba2cd9fd2080bf7bec9c31`. Draft **PR397 → PR396 →
PR395 remain unmerged dependencies**. It adds an application example and
verification; core, backend, runtime, schema and inherited session defaults
are unchanged.

The baseline's real pipe probes reproduced three boundaries: retained stdin
prevented final read drainage after cancellation; a held foreground integration
prevented EOF observation; closed stdout lost the terminal while the Console
host reported local success. The retained reproduction is in the verification
record linked below.

`inputReader` is an ordinary scope-owned task. It publishes `InputFrame` links
through `OneShot` cells. The dispatcher acknowledges each link before calling
the existing `wireFold`, so the reader can read one following frame or EOF while
that foreground fold waits. The acknowledgement bounds prefetch to one queued
frame. The driver keeps the `AdmissionOwner`, preserves command/response order,
and explicitly joins a normally ended reader. Fault observation uses a timed
cell wait and the reader's task snapshot; an invalid UTF-8 exception cannot leave
the driver waiting forever on an uncompleted cell. Exceptional scope exit joins
the reader, commands, supervisors and Members before resource release.

Cancellation is checked before taking another queued frame. A first probe without
that check dispatched a prefetched spawn after cancellation (it emitted a receipt,
but acquired no workspace). The corrected composition joins the cancelled reader
before dispatching that frame. The held integration still completes its protected
`commit`, `head`, and `save`; its inherited command result remains `err:cancelled`.
The foreground command's receipt can still be written before cancellation escapes.
A dispatch receipt therefore does not certify a successful mutation.

The host binds Linux stdin/stdout using nonblocking `read`/`write` and `poll`.
Input checks cancellation between 25 ms poll intervals. There is no detached
Console read, and transport close asserts zero active reads. Output handles
partial writes and EAGAIN/EINTR with a 250 ms monotonic write deadline. EPIPE
and deadline expiry escape as observable errors, after graph-owned cleanup.
The write budget also applies when application cancellation is shielded. This
is a Linux example binding; the timing budget is not a new language contract.
Tests use a broad 2 s process-shutdown observation budget to accommodate OS
scheduling. This does not bound uncooperative providers, Git effects, stderr
consumption or total application completion.

Output evidence separates three facts:

- `LocalCompleted`: the graph completed its final local report after application
  release. Internal command results and `save` effects separately show mutation
  completion even when the overall local session has not completed.
- `TerminalWritten`: all bytes of the final wire notification were accepted by
  the OS. Each accepted byte fragment is recorded. This is not a peer acknowledgement.
- The test peer's exact stdout lines: receipt was observed by that peer in the
  tested run. The protocol has no acknowledgement proving consumption after a
  successful OS write.

Closed stdout after held integration now records `LocalCompleted=true`,
`TerminalWritten=false`, and `IOException` (`write errno=32`). The synthetic
integration's HEAD/save and release complete once. A peer that refuses to read
valid correlated replies produces `TimeoutException`; already accepted bytes
are an exact response prefix, cleanup joins the input task without peer EOF,
and no successful terminal is reported. The peer may still have queued input
that was never read or dispatched.

The harness deliberately replays the complete lost-terminal script in a new
process, using identical IDs and request bytes. It observes a second synthetic
commit/head/save. The in-memory ledger has no durable restart deduplication.
The application performs no automatic retry. A client with a lost response must
treat mutation outcome as uncertain until a durable status/reconciliation design
exists; that design is outside this milestone.

Twenty positive pipe cases cover the inherited framing/UTF-8/EOF/cancellation
cases, foreground EOF and cancellation, bounded prefetch cutoff, broken/stalled
output, deliberate restart replay, and a user command named `terminal` that must
not set local completion before release. Six compiled causal controls cover dropped
tail, rewritten ID, duplicated release, an uninterruptible read, suppressed output
failure, and the old sequential-reader graph under the **same new host binding**.
Each requires its specific cause and the rest of its paired lifecycle before
failing its intended positive contract. The unrelated tail-fault guard is retained.

## Reproduce

Run from the repository root with Nushell, installed .NET 10 and locked/cached
Node dependencies. Run heavy checks serially. Compilation uses installed Roslyn
and reference assemblies without restore, downloads, listeners or live models.

```nu
node examples/offline-session-owned-io/run.mjs artifacts/owned-io/bundle
node examples/offline-session-owned-io/run.mjs artifacts/owned-io/bundle/sequential --sequential
node examples/offline-session-owned-io/compile.mjs artifacts/owned-io/bundle
node examples/offline-session-owned-io/compile.mjs artifacts/owned-io/bundle/sequential
node examples/offline-session-owned-io/test-runtime.mjs artifacts/owned-io/bundle
cp -r artifacts/owned-io/bundle artifacts/owned-io/moved
node examples/offline-session-owned-io/run.mjs --replay artifacts/owned-io/moved
node examples/offline-session-owned-io/run.mjs --replay artifacts/owned-io/moved/sequential
node examples/offline-session-owned-io/compile.mjs artifacts/owned-io/moved
node examples/offline-session-owned-io/compile.mjs artifacts/owned-io/moved/sequential
node examples/offline-session-owned-io/test-runtime.mjs artifacts/owned-io/moved
node examples/offline-session-owned-io/verify.mjs artifacts/owned-io/bundle artifacts/owned-io/moved
node --experimental-strip-types --test --test-concurrency=1 .opencode/tests/nyrwvy-authoring-offline-session-owned-io.test.mjs
```

Bundles retain snapshot/transcript, symbols/declarations, unedited emitted C#,
binding source, assemblies, scenarios, exact peer actions, read/write byte
fragments, effect traces, wire lines, outcomes and timing observations. Moved
replay checks exact snapshot/source/host/assembly bytes, source closure, intended
peer actions and outcomes. Every ordinary read stream equals the supplied bytes;
stalled output can stop with an exact prefix of queued input. Accepted write
bytes are checked against graph-generated replies in each run; concurrent
effect interleavings and timings are allowed to vary.

Hash closure covers the declared artifact and imported application-source set,
including the sequential control artifacts. It excludes transcripts, mutable
`scenario.json`, tool binaries, compiler/reference-pack bytes and environment
state. It is not full environment closure. See the [verification record](../../docs/verification/offline-session-owned-io-20261014.md).

## Capability checklist

| Status | Capability / boundary |
| --- | --- |
| Proven | Real compiled Linux pipe framing, ordered correlated receipts, strict UTF-8 failures, EOF tails and cancellation with joined cleanup |
| Proven | Owned reader detects EOF during held foreground integration when no queued frame precedes EOF; retained stdin no longer blocks cancellation shutdown |
| Proven | One-frame prefetch and tested cancellation cutoff; broken stdout and stalled output report failure separately from committed/local completion |
| Measured limit | Successful OS write has no peer acknowledgement; explicit fresh-process replay can repeat synthetic mutation after a lost terminal |
| Bounded composition limit | A queued frame blocks further reading until dispatch acknowledges it, so EOF behind backlog waits for dispatch; frame byte size before decoding remains unbounded |
| Reused | Graph parsing/binding, admission, Member/integration ledger, shields, close intent, command/supervisor joins and default-entry regressions |
| Missing | Durable outcome retention, restart reconciliation/deduplication and the original private Rrawrlet artifacts |
| Unverified | Arbitrary simultaneous EOF/SIGTERM races, cancellation at every instruction, uncooperative physical effects, stderr/backpressure, Windows/macOS, process groups, multi-client transport, full MCP/Python equivalence, live providers and physical Git |

The measured improvement uses existing tasks, scopes, cells, timed waits,
cancellation checks and ordinary external capabilities. It establishes no need
for a new core primitive or an application-specific language feature.
