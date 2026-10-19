# Actual-outcome recovery after cancellation and crash — 2026-10-14

The repaired graph recovers a request result only from a graph-written actual
outcome or actual-outcome receipt. Complete synthetic commit/HEAD/save evidence
establishes effect completion and saved HEAD; it cannot establish success rather
than cancellation. A crash before the first actual receipt remains unresolved
without retry or journal rewrite. A crash after that receipt returns the exact
retained result, including `err:cancelled`.

This repairs the P2 from independent review of candidate
`6f4c2c3a8e9dea07ff992de1b29190e82d5bb19e`, tree
`5c77e7f281a48c1ffdfffca4ce3f23135c5d47d1`. That candidate could render
`err:cancelled`, die before the outcome write and manufacture
`ok:integrated-head` from complete effects on restart. Mutations remained
once-only while outcome identity failed. The review reproduced this with the
unchanged original binding and retained a diagnostic observation of the actual
rendered value before the crash checkpoint.

Repair checkout: `/home/kw4/Documents/Codex/2026-10-14/task-31/durable-outcome`,
branch `feat/journal-actual-outcome-receipts`. The previous candidate checkout,
all its original/moved evidence and reviewer evidence are preserved. The common
reviewed base is PR400 `8e33e1553397f926d95b335d2bc5f86740b2f9f0`, tree
`365e6a1cdacc4094529adadf0a18e845e23258f3`. Ancestor/repository instructions and
the review were read. Nushell is used; heavy validation is serial. No core,
backend, runtime or schema implementation changed, and no Rrawrlet-specific
core policy was added.

## What changed

`journalAcceptProtected` writes the rendered, immutable typed Outcome to
`outcome-receipt/<stable token>` inside its existing shield before writing the
separate `outcome/<token>` entry and publishing inherited registry state.
Generic storage flushes each replacement file and directory before returning.
The actual receipt is application data; the binding does not choose its value.
The binding's effect inspector now reports state and HEAD, without an outcome.
The application no longer reads a request result from effect evidence.

Pending reconciliation first inspects effect evidence, then reads the existing
actual outcome or receipt. Valid actual evidence can complete the journal with
the original selected intent. In its absence, only authoritative absence plus
unchanged intent permits retry. Applied, partial, unavailable or malformed
effect evidence cannot supply a missing outcome. A result observed on stderr by
the test driver is diagnostic evidence, not a durable application receipt.

New terminal records include `outcomeSource: actual`. A legacy terminal record
without provenance must reconcile against a separately retained actual result.
If no such result exists it stays unchanged and is reported unresolved; it is
never eligible for retry. This prevents the faulty candidate's already-persisted
inferred success from becoming trusted merely because the record says `done`.
Legacy pending records can still reconcile or safely retry authoritative
absence with matching intent. Trusted, intact storage and one writer remain
assumptions; the marker is not protection against malicious storage alteration.

## Fresh maintained proof

The final original/moved bundles are `artifacts/actual-outcome/final` and
`artifacts/actual-outcome/moved-final`. Each runs 43 fresh processes, 23 contracts
and ten explicit SIGKILL interruptions. All 17 prior journal contracts remain
in the suite. The six added contracts cover:

| Window/input | Result |
| --- | --- |
| Reader invalid UTF-8 cancels linked witness, mutation completes, actual receipt persists, SIGKILL before separate outcome file | Original `err:cancelled` recovered; commit/HEAD/save total 1/1/1 |
| Same binding and scenario with graph receipt write disabled | No actual receipt; unresolved and unchanged; fails the identical exact-cancellation-recovery assertion; effects still 1/1/1 |
| SIGKILL before first actual receipt after determined success | Complete effects, but unresolved; no retry/rewrite; effects 1/1/1 |
| Same first-receipt window after determined cancellation | Complete effects, but unresolved; no retry/rewrite; effects 1/1/1 |
| Exact legacy inferred terminal state from reviewer unmodified probe | Unresolved, unchanged and no new mutation |
| Exact legacy actual terminal state from author lost-response probe | Authenticated by retained actual outcome, returned and marked actual; no new mutation |

The combined cancellation rows send malformed bytes while commit stays held,
observe linked-provider cancellation before release, hold another 350 ms with
no mutation/release, then release the protected tail. Generic write-attempt
observations show the already-rendered actual value before explicit SIGKILL.
Receipt and outcome calls have clear protected tokens. SIGKILL rows establish
retained state, not cooperative cleanup. The existing non-crash reader rows
still preserve exact primary exception identity/stack and joined cleanup.

Both causal graphs use the identical binding after declared-symbol
normalization. The receipt-disabled graph changes a Boolean selecting only the
actual receipt write; its reconciliation still refuses to infer success. The
historical P2 is RED baseline evidence from the independent reviewer, not a
fresh execution claimed by this repair. The maintained receipt-disabled control
provides a fresh causal refusal of the exact-outcome recovery contract.

Two legacy rows per bundle are fresh repaired-graph executions over **reused**
stored-state input. The committed
[legacy fixture](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/offline-session-journal/fixtures/legacy-outcome-state.json)
retains exact storage bytes, source row identity and raw hashes. No historical
execution is claimed as fresh. The reviewer trace/raw data and original author
state remain untouched and are separately audited.

Saved snapshots reopen and re-emit exact Generated.cs/symbols without generated
source edits. All 27 artifact files per bundle match after moved recompilation.
Stable actions, outputs, actual input/accepted-output bytes, durable state and
effect rows match. The [proof](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/offline-session-journal/fixtures/proof.json)
and [replay closure](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/offline-session-journal/fixtures/replay.json)
identify exact bundle/source/raw hashes. Example-source closure includes the
legacy input fixture; it excludes compiler/reference/environment and core/tool
implementation closure, with implementation sources pinned by Git base.

Fresh serial validation runs the full retained original/moved proof, the
maintained journal regression (another fresh original/moved proof in temporary
directories), and the reader-supervision regression with its prior controls.
Installed .NET `10.0.112`, Node `v26.11.1`, Nushell `0.116.1` and locked local
assets are reused. The Release build, 864 passing fast tests, and other inherited
baseline regressions are previously completed author/reviewer evidence, not
fresh repair results. Core code and the shared compiler are unchanged by this
repair. External task31 audit/logs and the handoff give final counts and hashes.

## Recovery boundary and separate gap

Before the first durable actual receipt, the request's result may have been
determined in memory but cannot be recovered from retained effects. The graph
returns `err:unresolved`, keeps storage unchanged and repeats no mutation.
Physical effect reconciliation may expose HEAD or other facts, but cannot
retroactively establish the original success/cancellation without authoritative
result evidence. The typed integration result is what is retained here; outer
session exception objects or stacks are not restored across process death.

Complete registry/integrated-HEAD restoration remains a separately demonstrated
gap, outside this repair. Independent review showed that identical-ID replay
adds no effects but leaves the rebuilt registry's integrated HEAD empty. A new
request ID rebuilding the same prefix repeats integration: aggregate 2/2/2.
This repair does not establish durable application continuation or deduplication
of different IDs. That milestone must restore state before admitting new work.

Other limits remain: one retained single-writer domain, intact trusted storage,
selected Linux crash windows, inherited parser/transport bounds and ordinary
outcome sizes. Power loss, arbitrary races/interrupts, physical Git/provider
effects, journal-write faults, uncooperative storage, compaction, multi-client
exclusion, peer acknowledgement and historical Rrawrlet/MCP/Python parity remain
unverified. No live provider/Git mutation tests, credentials, broad host access,
Rrawrlet invocation/alias expansion, push, PR or merge are part of this repair.
Independent review of the new candidate is required before publication.
