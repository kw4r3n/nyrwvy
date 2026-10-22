# Offline registry continuation — 2026-10-14

The new [registry example](https://github.com/kw4r3n/nyrwvy/blob/archive/legacy-examples-pre-cutover-20261019/examples/offline-session-registry/README.md)
addresses the separately reviewed restoration gap at PR401
`e85df1c9469eb0110230d587dbbfdc43a6c65326`, tree
`3cacca98e5658d5195a1dbaa844d0773404f9d4e`. Before this continuation, an
identical integration ID replayed its retained outcome while the rebuilt
registry still had an empty integrated HEAD. A distinct ID repeated integration.

The example now restores Member identity, workspace/base/predecessor, selected
commit/status/checks, settled/discarded/superseded flags, integrated HEAD, next
Member ID and admission identity/fingerprint before starting input. It creates
fresh owned task handles and phase/commit cells from those facts. Completed
Members return retained Final values; interrupted Members stop without restarting
providers. Replayed Member admission cannot replace restored state.

Application admission is retained before the journal intent and protected
mutation. Acceptance atomically retains the complete HEAD-bearing registry and
the actual rendered typed outcome under the inherited shield, before the scalar
actual receipt, separate outcome file, runtime publication, final journal and
wire reply. This full publication is authoritative actual result data. Startup
can restore it and seed a missing scalar receipt; contradictory existing actual
data closes admission without rewrite. Complete synthetic effects alone cannot
recover an unpersisted result. A pending application admission blocks distinct
IDs; authoritative absence and unchanged selected intent are necessary before
retrying the identical admission.

The only new external binding operation is the generic `storageEmpty` query.
It establishes whether an absent registry belongs to a new empty domain or to
an existing domain with missing application facts. The latter closes admission
before reading input. No core primitive was found missing; core/runtime/backend/
schema and inherited examples/compiler sources are unchanged.

The fresh maintained proof pairs enabled and restoration-disabled graph
selection over the identical normalized binding and scripted inputs. Initial
success, same-ID replay and new-ID admission total **1/1/1** commit/HEAD/save with
restoration, versus **2/2/2** without it. Inspect/terminal recover the original
Member/HEAD, even when a prospective new snapshot would choose a different
commit. Reopen runs no provider/workspace/snapshot effects for that Member.

Fifteen explicit SIGKILL rows per bundle cover application/journal intent,
partial commit and publication/receipt windows, including combined reader
cancellation. Before the first actual application publication, complete effects
stay unresolved without retry or rewrite. After it, the same ID returns the
original `err:cancelled`, and a distinct ID uses the restored HEAD without
mutation. Other controls cover unknown evidence, changed selected intent,
payload/admission conflicts, malformed/oversized registry state, duplicates,
invalid next/eligibility, absent provenance/registry and contradictory receipts.
An oversized prospective checkpoint blocks intent/mutation. Existing reader
exception identity/stack, clear protected tokens, joined cleanup and pipe bounds
remain checked. The discarded-state test uses fresh graph dispatch; superseded
eligibility uses a clearly labeled constructed state derived from the fresh seed.
The inherited wire spawn predecessor reset was observed during fixture setup;
this continuation does not repair or claim fresh superseding dispatch.

Original and moved bundles each use fresh runtime processes/state directories,
reopen snapshots and re-emit exact unedited C#. They recompile locally and compare
artifact closure, stable observations and actual pipe bytes. The maintained
registry regression repeats original/moved execution in temporary directories;
the journal and reader-propagation regressions run independently and serially.
Installed tools, locked Node dependencies and reviewed AuthoringSession build
assets are reused and separately hashed. Earlier broad builds/fast tests are
historical evidence, not fresh executions for this change. External task evidence
records final counts, log hashes, exact candidate commit/tree and preservation.

This is bounded application continuation in one intact trusted, single-writer
Linux domain with synthetic effects. It is not physical Git/provider proof.
The inherited parser limits snapshots to 4096 code points/depth 16. Actual result
provenance is a trusted-storage rule, not cryptographic attestation. Old
journal-only domains are rejected rather than migrated. Arbitrary asynchronous
mutation schedules, interrupted Member/workspace/provider effects, storage-write
faults, multiple writers, power loss, arbitrary race windows, compaction, peer
acknowledgement and full historical Rrawrlet/MCP/Python parity remain unverified.
No credentials/access changes, live provider or remote Git probes, push, merge
or integration into main were performed. Independent review is required next.
