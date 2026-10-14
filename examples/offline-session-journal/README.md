# Durable integration requests and pending-intent reconciliation

This application graph continues reviewed PR400 commit
`8e33e1553397f926d95b335d2bc5f86740b2f9f0`, tree
`365e6a1cdacc4094529adadf0a18e845e23258f3`. It composes existing graph functions,
products, conditionals, storage capability calls, tasks and cancellation shields.
No core, runtime, backend or schema capability was added. Generated C# is emitted
and compiled unchanged. The shared example compiler gains an optional host method
map and the existing Option type mapping for the new fixture operations.

Synchronous `integrate` wire requests require a non-null JSON-RPC ID. Stable
identity is the SHA256 of the parser's canonical typed ID, within one retained
storage domain. Payload identity is SHA256 of the complete decoded frame before
its line delimiter. Reordered/otherwise differently encoded payloads conflict,
even if their decoded arguments agree. String and numeric IDs remain distinct.
Digest correctness/collision resistance is a binding assumption.

Before dispatch, the graph durably writes a versioned pending record containing
identity, payload fingerprint, selected mutation intent and effect identity.
Intent includes target, workspace, selected commit, status, settled/latest/
discarded flags and existing integrated HEAD. The inherited integration stores
its actual typed outcome under the stable token inside a shield, then publishes
the inherited state. A final record retains that outcome before wire output.
Identical identity/payload replays return the original outcome; differing payloads
are rejected before effect observation or dispatch. Typed failures are retained.

On reopening a pending record, the graph asks for independent effect evidence.
A retained determined outcome can complete the journal. Otherwise an applied
receipt supplies the original outcome; authoritative absence permits dispatch
only if the current selected intent equals the persisted intent. When no
determined outcome was retained, partial, unavailable or malformed evidence
stays unresolved. Pending does not imply
either successful application or permission to repeat it. Unsupported null-ID,
notification and asynchronous begin-integrate forms stop before mutation.

The binding exposes generic opaque-key read/write, SHA256, effect correlation
and effect inspection. File replacement flushes the file and then fsyncs its
containing directory. Synthetic commit/HEAD/save are flushed rows in a separate
effect ledger. Inspection derives absence, the complete ordered effect and its
outcome, or a partial effect from that ledger. Neither the binding nor the peer
implements journal admission, payload conflict, outcome replay or retry policy.
Tagged ledger rows **are** the synthetic effects; this does not demonstrate an
atomic physical Git mutation plus receipt. Authoritative absence requires this
single-writer binding and retained, intact effect storage.

The retained proof uses 33 fresh processes in each original/moved bundle:

| Contract | Observation across restart |
| --- | --- |
| Lost response after commit/HEAD/save | One of each; original retained outcome |
| Identical host, journal-disabled causal graph | Two of each |
| SIGKILL after durable intent, before mutation | Explicit absence; one safe mutation |
| SIGKILL after save, before outcome persistence | Applied receipt; one total mutation |
| SIGKILL after commit only | Partial receipt; unresolved; no repeat, HEAD or save |
| Pending with unavailable evidence | No mutation until later authoritative absence |
| Same payload, changed candidate commit | Unresolved despite absence; original intent retained |
| Conflicting payload with pending/done record | Conflict; storage unchanged; original remains replayable |
| Same-process replay, malformed record, retained unknown-target failure | Once-only effect or no effect, as appropriate |
| Unsupported ID/async forms | No mutation or journal write |
| Held integration plus oversize/invalid UTF-8 reader fault | Linked-provider cancellation before gate release; protected effect and outcome retained; exact primary exception and joined cleanup |

The reader-fault outcomes remain `err:cancelled` after the protected mutation.
Reopening returns that exact result. The graph does not convert cancellation
into success. A 350 ms held observation checks that mutation/release wait for
the explicit peer gate. Fresh normal runs check one open/close, joined providers,
balanced reads, bounded input and accepted output bytes. SIGKILL rows intentionally
cannot assert cooperative cleanup; their durable state is inspected after exit.
No EOF or external cancellation drives the lost-response cleanup case.

Run serially from the repository root, with installed local dependencies and
the installed .NET 10 SDK. Every execution bundle must be fresh. Compiler/test
subprocess waiting or local VSTest sockets may need the narrow execution
permission used by the retained run. No download, provider or credentials are
needed.

```nu
node examples/offline-session-journal/run.mjs artifacts/journal/bundle
node examples/offline-session-journal/run.mjs artifacts/journal/bundle/disabled --disabled
node examples/offline-session-journal/compile.mjs artifacts/journal/bundle
node examples/offline-session-journal/compile.mjs artifacts/journal/bundle/disabled
node examples/offline-session-journal/test-runtime.mjs artifacts/journal/bundle
cp -r artifacts/journal/bundle artifacts/journal/moved
rm -r artifacts/journal/moved/runs
node examples/offline-session-journal/run.mjs --replay artifacts/journal/moved
node examples/offline-session-journal/run.mjs --replay artifacts/journal/moved/disabled
node examples/offline-session-journal/compile.mjs artifacts/journal/moved
node examples/offline-session-journal/compile.mjs artifacts/journal/moved/disabled
node examples/offline-session-journal/test-runtime.mjs artifacts/journal/moved
node examples/offline-session-journal/verify.mjs artifacts/journal/bundle artifacts/journal/moved
node --experimental-strip-types --test --test-concurrency=1 .opencode/tests/nyrwvy-authoring-offline-session-journal.test.mjs
```

This establishes bounded application behavior across real process death and
reopen on Linux, with synthetic effects. It does not establish power-loss
durability, multi-writer exclusion, arbitrary crash positions, physical Git or
provider reconciliation, complete live registry restoration, journal compaction,
peer acknowledgement, full historical Rrawrlet/MCP/Python equivalence or all
outcome sizes. The inherited JSON parser admits at most 4096 code points/depth
16; input transport independently admits at most 16 KiB payload. Oversized or
corrupt stored records fail unresolved. Example-source/artifact hashes exclude
core/tool implementation, compiler/reference binaries and environment closure;
unchanged implementation sources are pinned by the base commit.

See [proof](fixtures/proof.json), [moved replay](fixtures/replay.json),
[verification record](../../docs/verification/offline-session-journal-20261014.md)
and [capability checklist](../../docs/verification/reconstruction-capability-checklist.md).
