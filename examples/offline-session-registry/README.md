# Durable registry and integrated HEAD restoration

This continuation starts at reviewed PR401 `e85df1c9469eb0110230d587dbbfdc43a6c65326`
(tree `3cacca98e5658d5195a1dbaa844d0773404f9d4e`). It uses existing products,
lists, task scopes, task results, one-shots, cancellation shields and opaque
storage operations. The journal, reader, shared compiler, core, runtime, backend
and schema implementations remain unchanged. All policy is example graph data.
No Rrawrlet-specific core capability is introduced.

## Retained facts and new ownership

`registry/v1` contains Member ID, workspace/base/predecessor, admission identity
and payload fingerprint, selected commit/status/checks, settled/discarded/
superseded flags, integrated HEAD and the next Member ID. It also retains the
integration admission identity, fingerprint, selected intent and, on completed
publication, the actual rendered outcome with `outcomeSource: actual`.
Member admission identities are digests of canonical typed JSON-RPC IDs; payload
fingerprints digest complete frame bytes as in the inherited journal.

Command boundaries publish value snapshots. A successful Member admission waits
for its admission worker and supervisor before accepting another frame, while
the Member/provider remains independently owned. This prevents predicted Member
IDs from colliding across queued spawn admissions. Replaying an exact retained
Member admission returns without creating another workspace/provider/snapshot.
A conflicting payload returns a conflict. Missing Member facts for a retained
admission remain unresolved.

Before starting the input reader, the new execution scope validates the retained
snapshot and creates fresh owned task handles and phase/commit cells. Settled
Members return their retained `Final` values; previously running Members settle
as `stopped`, with no provider restart. The graph waits for those replacement
tasks before admitting input. No runtime handle, task ID, scope or cell is
serialized. The restored `State.next` and admission identities prevent a scripted
prefix from replacing the already integrated Member. Inspect and terminal use
the inherited render functions over that restored owned state.

## Mutation, outcome and publication order

For a new synchronous integration ID with a known target:

1. Retain the selected value snapshot and a pending application admission.
2. Retain the inherited journal pending intent.
3. Run the existing protected integration mutation.
4. Atomically replace the application record with the complete typed publication,
   including the actual rendered outcome and the HEAD-bearing registry.
5. Write the inherited scalar actual-outcome receipt and separate outcome file.
6. Publish the inherited runtime state, finish the request journal and reply.

Step 4 runs inside the existing acceptance shield. Its record is actual result
evidence assembled by the graph from the typed `Outcome`; it is not inferred
from synthetic commit/HEAD/save rows. This makes the complete application
publication the first durable actual receipt in this continuation. The inherited
scalar receipts retain their original meaning and remain usable by the journal.
If death occurs between steps 4 and 5, startup restores the registry and seeds a
missing scalar receipt from that retained actual result under a shield. Existing
contradictory scalar evidence closes admission without rewriting either record.
Before any seed write or input, the ready publication is checked against the
current journal's identity, fingerprint, selected intent, effect identity and
provenance/result, plus every available separate and scalar outcome. Contradictory
or malformed evidence closes admission without repairing or rewriting any source.

A pending application admission blocks distinct integration IDs and new Member
admissions. An identical pending ID can use the inherited journal's authoritative
absence and exact selected-intent check. If death preceded the journal intent,
the application performs that absence/intent check itself before creating it.
Partial, applied-without-actual-result and unavailable effects remain unresolved
without retry or state rewrite. A complete effect ledger alone never supplies
the original request result. Published cancellation replays as `err:cancelled`;
a distinct later request for the retained HEAD can succeed without mutation.

## Offline proof

The paired control changes only graph selection of restoration/publication; it
retains the same journal and normalized binding. Across initial success, an
identical-ID replay and a distinct integration ID, enabled total effects are
commit/HEAD/save **1/1/1**. The control exposes the reviewed gap: replay has an
empty integrated HEAD, then the distinct ID increases total effects to **2/2/2**.
Enabled replay also restores the original selected commit when the fixture's
new-snapshot commit is changed, and runs no provider/workspace/snapshot effects.

The maintained proof includes SIGKILL before/after application intent, after
journal intent, after commit, before/after atomic registry publication, before
the scalar actual receipt and before the separate outcome file. Combined reader
cancellation/publication rows distinguish actual cancellation from success.
Negative controls cover unavailable effects, changed selected intent, integration
and Member admission payload conflicts, malformed/oversized/versioned registry
data, duplicate Member IDs, invalid next IDs/eligibility, missing actual provenance
and contradictory actual receipts. Reader tests retain exact exception identity/
stack, protected tokens, joined cleanup and the inherited transport byte bounds.
The binding also exposes a generic domain-empty query: absent registry facts
permit initial admission only in an empty storage domain. A nonempty journal-only
domain or a lost registry closes admission before the reader starts.

Original and moved bundles reopen saved snapshots and re-emit exact Generated.cs.
They compile it unchanged and use fresh process/state directories. Verification
checks normalized control binding equality, identical causal inputs/actions,
artifact hashes, stable results/retained storage, and actual pipe bytes. The
regression reruns this proof in temporary original/moved directories. The prior
journal and reader-supervision regressions run independently and serially.
The maintained regression also runs `test-repair.mjs` in both bundles. Those
focused controls execute the intact capacity rejection/Member-number reuse
sequence, retain exact cancellation with matching evidence, and distinguish
constructed contradictory data from natural crash traces. A rejected admission's
predicted number resolves only when the actual Member fact matches its identity
and fingerprint. Startup checks each snapshot Member against its Member sidecar
and inverse admission association; IDs must cover the retained next counter.

```nu
node examples/offline-session-registry/run.mjs artifacts/registry/bundle
node examples/offline-session-registry/run.mjs artifacts/registry/bundle/restoration-disabled --restoration-disabled
node examples/offline-session-registry/compile.mjs artifacts/registry/bundle
node examples/offline-session-registry/compile.mjs artifacts/registry/bundle/restoration-disabled
node examples/offline-session-registry/test-runtime.mjs artifacts/registry/bundle
node examples/offline-session-registry/test-repair.mjs artifacts/registry/bundle artifacts/registry/bundle/repair
cp -r artifacts/registry/bundle artifacts/registry/moved
rm -r artifacts/registry/moved/runs
rm -r artifacts/registry/moved/repair
node examples/offline-session-registry/run.mjs --replay artifacts/registry/moved
node examples/offline-session-registry/run.mjs --replay artifacts/registry/moved/restoration-disabled
node examples/offline-session-registry/compile.mjs artifacts/registry/moved
node examples/offline-session-registry/compile.mjs artifacts/registry/moved/restoration-disabled
node examples/offline-session-registry/test-runtime.mjs artifacts/registry/moved
node examples/offline-session-registry/test-repair.mjs artifacts/registry/moved artifacts/registry/moved/repair
node examples/offline-session-registry/verify.mjs artifacts/registry/bundle artifacts/registry/moved
node --experimental-strip-types --test --test-concurrency=1 .opencode/tests-extended/nyrwvy-authoring-offline-session-registry.test.mjs
```

## Limits

This proves selected Linux process-death windows in one intact trusted,
single-writer retained domain. The inherited JSON parser bounds registry
publication to 4096 code points/depth 16; an unrepresentable checkpoint blocks
integration before mutation. If a later completed publication is unrepresentable,
effects remain unresolved. This is bounded ordinary-outcome evidence, not a
general registry of arbitrary size or a compaction implementation.

Member/provider resumption, interrupted workspace creation/discard, arbitrary
asynchronous mutation schedules, physical Git/provider reconciliation, multiple
writers, power loss, storage-write faults and deletion, uncooperative effects,
peer acknowledgement and full historical Rrawrlet/MCP/Python parity remain
separate obligations. Old journal-only domains lack the required application
facts and are rejected; no migration or reconstruction from effect completion is attempted.
No live provider, credential, access change or physical Git mutation probe runs.
Example-source/artifact closure excludes compiler/reference/environment closure;
core/tool implementation remains pinned by the reviewed base. Independent review
must precede publication or integration into main.

See the retained [proof](fixtures/proof.json), [moved closure](fixtures/replay.json)
and [verification record](../../docs/verification/offline-session-registry-20261014.md).
The [repair record](../../docs/verification/offline-session-registry-repair-20261014.md)
separates new execution from reused reviewed evidence.
