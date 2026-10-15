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
| Proven rejection | Compiled negative controls | Intended defects observed; paired lifecycle checks pass except for that defect; dropped tail/wrong ID/double cleanup/blocked read fail their specific positive contracts; unrelated exception refused |
| Reused | Finite typed/decoded-text paths | Existing focused session regressions; parser and previous Member/cleanup/release fault identity controls remain authoritative |
| Missing | Complete reconstruction | Historical private artifacts, durable restart/lost-response reconciliation and autonomous deadline/escalation for a retained peer pipe |
| Measured limitation | Terminal delivery and independent EOF | Review observed no final read drainage with retained stdin, delayed EOF during a held foreground fold, and local success without terminal delivery after stdout closed; unchanged in this verification revision |
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
13 positive cases, four causal negative controls and one unrelated-fault guard
serially, moves the bundle,
reopens/re-emits/recompiles, repeats pipe execution and verifies exact required
bytes, peer actions, wire outputs/outcomes and both artifact/source hash closure.
Concurrent effect interleavings are allowed to differ; each replay separately
checks required happens-before relations and effect counts.

Corrected local evidence is retained in `artifacts/transport/revision-bundle` and
`artifacts/transport/revision-moved`: `pipe-proof.json`, `pipe-raw.json`,
`verified.json`, the authoring transcript/snapshot, source/symbol/declaration
files, host, assembly/runtime configuration, cases and compilation record.
[`fixtures/proof.json`](../../examples/offline-session-transport/fixtures/proof.json)
retains corrected compact proof, verification and check/log hash records. The
original rejected evidence remains in `artifacts/transport/proof-bundle` and
`artifacts/transport/proof-moved`. Hash closure means the declared artifact and
application-source set, excluding transcript, mutable scenario, tool binaries,
compiler and reference-pack bytes. Fresh authoring
IDs vary; retained hashes identify this run. The README provides replay commands.

Only examples, documentation and a Node regression change. No backend semantic
change requires the extended backend suite. Heavy builds/tests run serially with
one .NET build worker. No push, PR creation, merge or modification of the source
working trees is part of this milestone.

Independent review of `08ed17df46dff79f754db220d8b594354d43b45a` rejected the
four-control claim: `drop-tail` reached an unsupported synchronous read from
`StreamReader.EndOfStream`, threw `NotSupportedException`, and the harness
accepted that unrelated failure as tail-loss evidence. No supported-path defect
was found in the 13 positive pipe cases. The other three negative rows showed
their intended defects.

This revision implements recorded synchronous reads for the control's EOF probe
and records `tail-dropped` before returning EOF. The control must now complete
gracefully with once-only release and a delivered terminal, while omitting only
the correlated tail reply. The other three controls require cause-specific
observations and reject only their intended positive contract. A compiled
unrelated-exception guard recreates the old failure category and is refused by
the causal checker. No authored graph, core or supported transport policy change
is part of this verification-only correction.

Prior validation remains retained evidence: Release build **zero errors/warnings**,
fast suite **864/864**, serial aggregate **47/47**. The reviewer freshly reran the
fast suite and three related regressions, **3/3**. Those checks did not validate
the defective tail-loss claim and are not presented as fresh revision runs.
Corrected validation passed the maintained focused transport regression **1/1**,
including fresh authoring and moved replay. Retained corrected original/moved
bundles each passed **13** positive cases, **four** causal negative controls and
the unrelated-fault guard. Exact moved replay, sent/read byte equality and
declared artifact/application-source hash closure passed. The corrected retained
snapshot, unedited generated C#, symbols, declarations and authoring record are
byte-identical to the reviewed candidate; only the host/control verification and
evidence changed. `artifacts/transport/revision-checks.json` separates fresh
revision results from reused prior checks and records their log hashes. Counts
describe observations rather than future count gates.

Execution used approved sandbox exceptions for local .NET child execution and
test-framework IPC. Dependencies came from existing local caches; restore used
an empty local package source, and runtime compilation used installed Roslyn
without restore or downloads. No external service or account operation occurred.
