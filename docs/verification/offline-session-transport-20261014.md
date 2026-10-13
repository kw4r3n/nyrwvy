# Offline session transport continuation

Base: reviewed `feat/offline-leader-jsonrpc` commit
`9171508041350969a8d41e0408d9e6b4cba55dd5`, tree
`93a914f2f36bb922fb0c1f3e2aed8b9b42b175ed`. **This separate feature depends on
draft PR396, stacked on unmerged PR395.** The parent independent review approved
only finite decoded-text sessions; this continuation measures the next boundary
without merging or publishing either dependency.

The [example](../../examples/offline-session-transport/README.md) adds an ordinary
`Transport.readLine` capability and a tail-recursive graph input loop. EOF
returns the retained owner to the inherited finish path. `sessionInput` composes
that loop into the existing resource/close-latch lifetime rather than creating
another controller. Parser/envelope/binding/notification/correlation policy,
typed admission, Member workers, integration, tickets, shields and explicit joins
remain in the authored graph. Both original entry paths keep their regressions.
No generic core, runtime, schema or domain change was necessary.

This is actual transport execution: Node starts compiled unedited C# with OS
stdin/stdout/stderr pipes, writes exact byte fragments, closes stdin or delivers
SIGTERM, observes strict decoding/read events and exact wire outputs, and waits
for child `close`. Provider/Git effects and gates are synthetic. The host
template reuses the maintained effects fixture and adds only pipe I/O and
observations. No model, credentials, account operation, external network or
network listener is involved. Test-framework local IPC remains a verification
implementation detail.

JSONL comes from the existing AuthoringSession/AuthoringClient transports and
the recorded Rrawrlet [probe-25](rrawrlet-slice-probe25-20261005.md) reader
convention. Platform strict UTF-8 decoding stays in the host, and the graph
continues to own the PR396 application protocol. Historical Python artifacts
were not recovered and no Python/encoding/MCP equivalence is claimed.

| Status | Boundary | Evidence / practical limit |
| --- | --- | --- |
| Proven | Fragmentation/coalescing and correlation | Real split inside a multibyte scalar; coalesced LF/CRLF frames; escaped/Unicode/integer IDs; exact ordered response bytes and silent notification |
| Proven | Malformed/truncated JSON and EOF tail | Invalid frames reject before mutation; next valid frame proceeds; final valid unterminated JSON dispatches, truncated JSON gets parse error, empty EOF closes |
| Proven | Strict decoding failure | Invalid and incomplete UTF-8 with an active Member escape as DecoderFallbackException; Member joins, once-only release, no success terminal |
| Proven | EOF/cancellation with work in flight | Active held Member, queued spawn, and held integration commit; real EOF/SIGTERM and deterministic gates |
| Reused and proven here | Protected mutation tail and cleanup | Commit/head/save finish once after terminal condition; EOF reports integrated HEAD; cancellation reports err:cancelled despite completed synthetic save |
| Proven | Terminal ownership | Every positive row releases once with zero providers active, joins peer-closed read, closes transport once, then records process terminal; successful wire terminal follows application release |
| Proven rejection | Compiled negative controls | Dropped tail, wrong ID, double cleanup and uninterruptible read fail the paired positive acceptance |
| Reused | Finite typed/decoded-text paths | Existing focused session regressions; parser and previous Member/cleanup/release fault identity controls remain authoritative |
| Missing | Complete reconstruction | Historical private artifacts, durable restart/lost-response reconciliation and autonomous deadline/escalation for a retained peer pipe |
| Unverified | Further transport/application boundaries | EOF detection during blocked foreground folds, write/backpressure failure, pre-decode frame-byte bound, arbitrary host failure, multi-client/process-group transport, full protocol/Python equivalence, live provider/physical Git, budgets/fairness |

The first host attempt exposed a concrete limitation of the installed Linux
console stream: token-bearing `ReadLineAsync` remained blocked when cancelled
with stdin open. The host now cancels its wait with `WaitAsync(token)` while
retaining the original OS read. The graph drains/releases while the peer still
keeps stdin open; only then does the peer close stdin. The host joins that read
before transport disposal and process-terminal observation. The compiled
uninterruptible-read variant reproduces the blocked boundary; a two-second
negative-control watchdog reaps it with SIGKILL. Positive cases have a separate
20-second test watchdog and never require it. These are harness bounds, not a
product timeout guarantee. Final transport drainage still requires peer EOF.

Root cancellation during held integration establishes completion of the
protected synthetic mutation tail and exactly-once release. It does **not**
establish durable application recovery: the command's inherited result is
`err:cancelled`, the root fails, and no successful session report is published.
This preserves the dispatch-receipt contract and leaves restart reconciliation
as the next bounded application measurement.

The reader runs between synchronous folds. In-flight EOF/cancellation cases
schedule command workers with `begin`, retaining their ownership across reads.
This establishes that tested overlap, not independent EOF detection while a
foreground fold itself remains blocked.

The maintained regression authors/reviews/commits the graph, saves/reopens it,
compiles its unedited C# with installed Roslyn and reference assemblies, runs
13 positive cases and four negative controls serially, moves the bundle,
reopens/re-emits/recompiles, repeats pipe execution and verifies exact required
bytes, peer actions, wire outputs/outcomes and both artifact/source hash closure.
Concurrent effect interleavings are allowed to differ; each replay separately
checks required happens-before relations and effect counts.

Durable local evidence is retained in `artifacts/transport/proof-bundle` and
`artifacts/transport/proof-moved`: `pipe-proof.json`, `pipe-raw.json`,
`verified.json`, the authoring transcript/snapshot, source/symbol/declaration
files, host, assembly/runtime configuration, cases and compilation record.
[`fixtures/proof.json`](../../examples/offline-session-transport/fixtures/proof.json)
retains compact proof, verification and check/log hash records. Fresh authoring
IDs vary; retained hashes identify this run. The README provides replay commands.

Only examples, documentation and a Node regression change. No backend semantic
change requires the extended backend suite. Heavy builds/tests run serially with
one .NET build worker. No push, PR creation, merge or modification of the source
working trees is part of this milestone.

Final validation: Release fast-test graph build **zero errors/warnings**, fast
suite **864/864**, maintained focused transport regression **1/1**, serial
authoring/script aggregate **47/47**, including both existing session regressions
and the transport regression. The retained final original/moved execution each
passes **13** positive pipe cases and **four** negative controls. Exact moved
replay and artifact/application-source hash verification pass. Counts describe
this run rather than imposing future count gates.

Execution used approved sandbox exceptions for local .NET child execution and
test-framework IPC. Dependencies came from existing local caches; restore used
an empty local package source, and runtime compilation used installed Roslyn
without restore or downloads. No external service or account operation occurred.
