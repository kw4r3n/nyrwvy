# Bounded input before UTF-8 decoding

This continuation starts at reviewed `f62782acd06188f35ba4ccb4fd6c3517a261a956`,
tree `1b5fe82c6d64bd7dfee934e7ff3afbdee0ae416a`. Its unmerged dependencies remain
**PR397 → PR396 → PR395**. The prior independent probe read 1 MiB of
unterminated ASCII before any frame decoding or parser limit could apply.

The ordinary semantic graph now invokes the generic external operation
`Transport.readLine(maxBytes: int64)` with **16384**. This policy covers every
UTF-8 representation of a frame within the existing 4096-code-point parser
ceiling. Framing and byte enforcement belong to the Linux binding. The graph
retains reader ownership, handoff, ordered command dispatch and cleanup. No new
core primitive, schema, backend or application-specific operation is added.
The inherited owned-I/O example retains its original no-argument operation.

The transport contract is:

- Each line admits at most 16384 payload bytes, excluding CR/LF terminators.
  CR, LF and CRLF match the inherited line-reader behavior.
- The binding rejects the first excess payload byte with `InvalidDataException`
  before UTF-8 decoding or graph dispatch. The error escapes through owned
  cleanup without draining the frame or trying to resynchronize. A held
  foreground effect can defer session termination, as measured below.
- The payload array never exceeds the supplied limit. A fixed 1024-byte syscall
  buffer may contain read-ahead bytes. This bounds transport-owned input
  storage; it is not a bound on kernel/peer buffers, trace storage, decoded
  application state or total process memory.
- Exactly the limit without a terminator waits for more input, EOF or
  cancellation. EOF decodes a nonempty tail once; empty EOF produces no frame.
  Split UTF-8 remains raw bytes until a complete line/tail; strict decoding
  rejects malformed or truncated sequences.
- Cancellation still checks between bytes and 25 ms syscall poll steps.
  Resource release follows joined reader/provider work, with one descriptor
  close and no abandoned read. A transport failure produces no successful
  wire terminal. The fixture's structured `outcome` is authoritative; its
  process exit code alone does not certify success.

The 16384-byte transport limit and 4096-code-point parser limit are distinct.
An exact-limit ASCII frame is decoded and receives a parse error; a valid
4096-code-point frame with an emoji ID succeeds. A byte overrun in the middle
of a multibyte sequence is an oversize fault before decoding, rather than a
decoder error. This is intentional precedence.

## Reproduce

Use Nushell, locked local Node dependencies, and a fresh Release build of
`tools/Nyrwvy.AuthoringSession`. Heavy commands run serially. Generated C# is
never edited. Compilation uses installed .NET 10 Roslyn/reference assemblies
without generated-fixture restore, downloads or live services.

```nu
node examples/offline-session-bounded-input/run.mjs artifacts/bounded-input/bundle
node examples/offline-session-bounded-input/run.mjs artifacts/bounded-input/bundle/relaxed --relaxed
node examples/offline-session-bounded-input/compile.mjs artifacts/bounded-input/bundle
node examples/offline-session-bounded-input/compile.mjs artifacts/bounded-input/bundle/relaxed
node examples/offline-session-bounded-input/test-runtime.mjs artifacts/bounded-input/bundle
cp -r artifacts/bounded-input/bundle artifacts/bounded-input/moved
node examples/offline-session-bounded-input/run.mjs --replay artifacts/bounded-input/moved
node examples/offline-session-bounded-input/run.mjs --replay artifacts/bounded-input/moved/relaxed
node examples/offline-session-bounded-input/compile.mjs artifacts/bounded-input/moved
node examples/offline-session-bounded-input/compile.mjs artifacts/bounded-input/moved/relaxed
node examples/offline-session-bounded-input/test-runtime.mjs artifacts/bounded-input/moved
node examples/offline-session-bounded-input/verify.mjs artifacts/bounded-input/bundle artifacts/bounded-input/moved
node --experimental-strip-types --test --test-concurrency=1 .opencode/tests/nyrwvy-authoring-offline-session-bounded-input.test.mjs
```

Each bundle retains snapshot/transcript, symbols/declarations, unedited emitted
source, binding, assemblies, exact peer actions, syscall byte fragments,
outcomes and lifecycle traces. Replay reopens/re-emits/recompiles the moved
snapshot, executes all probes again and checks exact sources/assembly bytes,
offered input prefixes, accepted output bytes, outcomes and source/artifact
hash closure. Read fragmentation, task interleaving and timing may vary.
Closure excludes transcripts, mutable scenarios, tool/compiler/reference-pack
bytes and environment state; it does not establish environment closure.

Twenty probes cover terminated/unterminated oversize, the 1 MiB baseline,
Member cleanup, exact limits, CRLF split across writes, 2/3/4-byte UTF-8 splits,
byte-limit precedence, malformed JSON/UTF-8, EOF and cancellation. Three causal
controls author a separate graph with a relaxed 2 MiB policy under the same
binding. They actually decode an oversized complete frame or accumulate the
entire incomplete input before signal recovery. An unrelated host exception
must have no oversize event and is refused as limit evidence.

## Remaining reconstruction boundaries

The final probe measures a reader fault while a foreground integration is held
at commit. The reader rejects input promptly, but the driver observes that fault
only after the fold returns. Signal recovery permits the shielded HEAD/save and
joined cleanup, retaining the original `InvalidDataException`. A byte bound
alone therefore does not guarantee prompt session
termination during a held foreground effect. The next composition requirement
is a policy for observing owned reader faults during foreground work without
interrupting protected mutation completion.

EOF behind a queued frame still waits for dispatch acknowledgement. Successful
OS writes still have no peer acknowledgement. No durable outcome retention,
restart reconciliation or durable deduplication exists; the inherited explicit
restart probe demonstrates repeated synthetic commit/HEAD/save after a lost
response. Historical private Rrawrlet artifacts, full MCP/Python equivalence,
physical Git/live providers, arbitrary EOF/SIGTERM races, uncooperative effects,
blocked stderr, other platforms, process groups and multi-client transport
remain absent or unverified. Evidence is scoped to real Linux pipes and
synthetic application effects.

See the [verification record](../../docs/verification/offline-session-bounded-input-20261014.md).
