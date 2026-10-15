# Offline JSONL pipe → Leader → Team → Member

This continuation composes PR396's graph parser and command fold with actual
child-process stdin/stdout pipes. It starts at reviewed commit
`9171508041350969a8d41e0408d9e6b4cba55dd5`, tree
`93a914f2f36bb922fb0c1f3e2aed8b9b42b175ed`. **It depends on draft PR396 and
its unmerged dependency PR395.** Neither dependency is merged or replaced.

`Transport.readLine: Option<String>` is an ordinary application capability.
The authored `transportLoop` retains `AdmissionOwner`, passes every line through
the existing `wireFold`, and returns that owner at EOF. The optional
`sessionInput` hook substitutes only the input parameters and fold: the existing
close-latch, cancellation shield, Member/ticket ownership, finish, explicit
drainage, resource release and post-release terminal publication are reused.
The default typed and finite decoded-wire entries remain covered by their
original regressions. No runtime, core, schema or domain primitive changes.

The framing follows repository JSON-lines transports (`AuthoringClient` and
`AuthoringSession`'s `ReadLine`) and the recorded Rrawrlet reader convention in
[probe 25](../../docs/verification/rrawrlet-slice-probe25-20261005.md).
The host uses .NET's `StreamReader` with strict UTF-8 and BOM detection disabled.
JSON syntax, envelope, keyword, notification and ID policy remain in the graph;
the [bounded application vocabulary](../offline-leader-jsonrpc/README.md) is
unchanged. No MCP handshake, Content-Length framing, Python encoding-detection
equivalence or new wire cancellation message is introduced.

LF and CRLF frames are tested, including coalesced frames and a UTF-8 scalar
split across real reads. The platform reader delivers a final unterminated line
at EOF: valid JSON is dispatched; truncated JSON gets the graph's parse error.
Malformed JSON/envelopes reject before application effects and the next valid
frame continues. Invalid/truncated UTF-8 raises a raw `DecoderFallbackException`,
cancels/drains owned work, and releases once without a successful terminal.
This is a strict host choice, not Python's `json.loads(bytes)` encoding policy.

The input loop reads between synchronous `wireFold` calls. The in-flight cases
use `begin` to keep owned command workers active across reads. Simultaneous EOF
detection during a blocked foreground fold is unverified; it would need a
separate reader/dispatch composition if the application requires that behavior.

EOF is graceful application finish. SIGTERM is intercepted by this test host
and cancels the root execution token; it does not immediately kill the process.
The peer keeps stdin open until the graph has settled and released, then closes
it so the outstanding read can be joined. The initial direct token-bearing
console read stayed blocked on Linux. `WaitAsync(token)` cancels the host wait
while retaining the OS read; `Pipe.Dispose` joins it before once-only pipe close
and process-terminal observation. A compiled uninterruptible-read control
reproduces the failure and is reaped by the peer's bounded watchdog.
**Peer EOF is still required for final read drainage.** This does not establish
autonomous bounded shutdown if a peer indefinitely retains the input pipe.

Thirteen positive compiled pipe cases establish exact ordered/correlated wire
receipts, notification silence, graceful empty/final/truncated EOF, rejection
without mutation, active-Member drainage, strict decoder failures, root
cancellation while reading and while a spawn is queued, and EOF/cancellation
during a held integration commit. Synthetic gates establish ordering without
sleeping. Each positive case has one application release, zero providers at
release, one drained pipe close, and one process-terminal observation. Graceful
success has one final wire terminal after release; transport failure has none.

Held integration finishes `commit`, `head` and `save` after EOF or cancellation.
At EOF its result is `ok:integrated-head`. Root cancellation preserves the
existing `err:cancelled` command outcome despite the completed synthetic save;
the root fails and publishes no success terminal. Dispatch receipts are
receipts, and retry/restart reconciliation after a lost response is unverified.
Synthetic Git/provider effects are reused from the maintained fixture; these
are not physical integration Git or live-model runs.

Four deliberately wrong compiled host variants drop an EOF tail, rewrite IDs,
duplicate cleanup, or block a cancelled read. Each must first show its intended
cause and satisfy the rest of its paired lifecycle, then fail the specific
positive contract for that cause. Tail loss requires a healthy graceful result,
once-only cleanup, an observed dropped frame and the missing correlated reply.
ID corruption requires the graph's correct reply and the altered delivered ID;
duplicate cleanup requires two actual close observations with a healthy result;
blocked read requires an observed wait, delivered cancellation, provider cleanup,
absent application release and watchdog reaping. Arbitrary failures do not count.
A fifth compiled row deliberately throws an unrelated `NotSupportedException`:
the causal checker refuses it as tail-loss evidence. This verification guard is
separate from the four intended negative controls. All flags are fixture controls
outside the supported path. No expected positive output is derived from a second
parser or a host Team controller.

Independent review found that the earlier `drop-tail` EOF probe threw before
dropping anything. The verification revision supports the reader's recorded
synchronous EOF probe, records the drop, and strengthens all four control checks.
The authored graph and transport policy are unchanged.

The review also measured a closed stdout reader during held integration: local
success and once-only release were reported after commit/head/save, while the
peer received no terminal notification. Local success does not establish terminal
delivery. This verification-only revision does not fix that limitation, retained
stdin drainage, or EOF detection during a blocked foreground fold.

## Reproduce

Use Nushell and run heavy checks serially. Dependencies are locked; the offline
Roslyn compiler uses installed reference assemblies without restore/downloads.

```nu
node examples/offline-session-transport/run.mjs artifacts/transport/bundle
node examples/offline-session-transport/compile.mjs artifacts/transport/bundle
node examples/offline-session-transport/test-runtime.mjs artifacts/transport/bundle
cp -r artifacts/transport/bundle artifacts/transport/moved
node examples/offline-session-transport/run.mjs --replay artifacts/transport/moved
node examples/offline-session-transport/compile.mjs artifacts/transport/moved
node examples/offline-session-transport/test-runtime.mjs artifacts/transport/moved
node examples/offline-session-transport/verify.mjs artifacts/transport/bundle artifacts/transport/moved
node --experimental-strip-types --test --test-concurrency=1 .opencode/tests/nyrwvy-authoring-offline-session-transport.test.mjs
```

Bundles retain the reviewed/committed snapshot and transcript, symbols,
declarations, unedited emitted C#, generated effects/pipe host, compiled assembly,
runtime configuration, fixture scenarios, exact peer write bytes/signals/EOF,
actual read bytes, effect events, stdout lines, outcomes and hash closure.
Hash closure covers the declared artifact/application-source set. It excludes
the transcript, mutable scenario file, tool binaries, compiler and reference-pack
bytes; it is not complete environment closure.
Moved replay verifies exact snapshot/source/host/assembly bytes and identical
peer actions, wire outputs and terminal outcomes. Concurrent effect interleavings
may differ; both runs independently satisfy the same happens-before contracts.

## Capability checklist

| Status | Capability / remaining boundary |
| --- | --- |
| Proven | Real compiled OS pipes; fragmentation/coalescing; strict UTF-8; JSONL EOF tail; exact ordered/correlated receipts; malformed/truncated rejection |
| Proven | Active-Member/queued-command root cancellation; protected held integration tail; once-only application release and joined peer-closed input |
| Reused | Graph JSON parser/binding, admission, Members, integration ledger, close intent, explicit joins, post-release success terminal; default entry regressions |
| Missing | Historical private Rrawrlet artifacts; durable restart/reconciliation application; autonomous deadline/escalation for a peer retaining stdin |
| Measured limit | Peer-retained stdin prevents final drainage; held foreground fold delays EOF; closed stdout can lose the terminal despite local success |
| Unverified | EOF detection during blocked foreground folds, write/backpressure failures, frame-byte bounds before decoding, arbitrary host failure, multi-client transports, full MCP/JSON-RPC or Python equivalence, process groups, live providers/physical Git, budgets and unbounded fairness |

The language already expresses this measured boundary. The demonstrated gap
was an interruptible host wait around a blocking OS read, addressed in the
example host. No generic core gap was established.
