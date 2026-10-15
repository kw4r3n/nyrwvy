# One explicit renewed admission epoch

Status: Experimental; independent review pending. Controlled offline example.

This example starts at independently reviewed interruption commit
`95b331ae008c1504ce3c6c1b0d5675bf43926445`, tree
`7ced2a777227845abf39b45f2c3c14038d120295`. It composes one separately
authorized admission epoch with ordinary reviewed graph operations. Core,
runtime, backend, schema and all predecessor procedures remain unchanged.

The supported predecessor has an accepted interrupted-continuation disposition
for an applied, clean, fully owned tail. Both old application results are absent.
Every retained original and continuation command has its exact terminal owner
witness. Active predecessor slots remain active; no-PID or unjoined slots and
object-only/ref-only predecessor tails refuse. The physical HEAD is the new
epoch's baseline; the retained old Member's integrated field remains empty.

`author.mjs` authors authority separation, canonical typed wire identities,
payload conflicts, frozen lineage, blocked requests, publication ordering,
registry restoration and the allowed request scope. `physical.template.cs`
provides inspection and custody facts. It authenticates frozen predecessor files
separately from current owned effects. It never returns a fabricated ready old
registry, an old outcome or execution permission. `compile.mjs` composes these
example bindings and compiles unedited `Generated.cs` with installed Roslyn.

The administrative request supplies `id`, `epoch`, exact opaque nonempty
`authorization`, `action: activate-renewed-admission`, and
`scope: one-fresh-member-one-synchronous-integration/v1`. It binds the exact
interruption receipt/inventory/baseline digests, baseline HEAD and repository
identity. `originalRequest` is the original accepted frame, checked against its
retained typed-ID and payload digests. `memberRequest` and `integrationRequest`
are exact independently selected fresh frames with distinct canonical typed-ID
digests. Both target the retained next Member number. `selectedCommit` belongs
to the frozen owned object inventory and is selected by the fresh Member.

Use scenario `epochRequest` for the request file and `epochAuthorization` for
the matching authority. Required authorities must be JSON strings with nonempty
length. Whitespace, Unicode and case remain opaque bytes. The new authority must
differ from the authorities in the exact accepted retention, continuation and
interruption requests. Optional earlier authority settings permit omission or
empty strings; supplied values must confirm accepted bytes. Administrative IDs
have the separate full-string ASCII rule. Numeric and string wire IDs retain
their inherited canonical distinction.

The graph publishes `epoch/v1/intent`, `epoch/v1/blocked`, `epoch/v1/result`, an
initial separate `epoch-registry/v1` and its lineage, then `epoch/v1/activation`
last. Block entries retain request classes, typed identity, raw original frame
and intent, raw accepted continuation request and intent, correlations and prior
digests, with both outcomes unknown. These entries never populate `outcome/` or
`outcome-receipt/`. The original registry, journals, Member associations, receipts
and launch fences remain exact. Orphans, changed records and contradictory
activation refuse before writes. Only exact explicit administrative replay may
finish its own publication windows. Startup cannot finish them.

After complete activation, the graph restores prior Member values using fresh
owned handles, retains `State.next`, admits exactly the selected fresh Member,
and accepts exactly the selected new synchronous integration frame. New records
carry an exact activation lineage sidecar. The fresh Member uses the epoch
baseline HEAD as its base; old providers/workspaces are never restarted. A stale
selected commit or base refuses before integration preparation. The original
integration ID stays unresolved, changed payloads keep their conflict meaning,
and distinct unrelated requests remain closed before and after the new effect.

New commands use immutable distinct
`physical-epoch-child/v1/<effect-token-digest>/<stage>` slots and a separately
pinned mechanical owner. Epoch inspection, storage and mutation require a
nonempty JSON-string `gitOwner`, an available ordinary file without symlink
ancestry, and exact compiled owner length/digest. Each launch rechecks that pin
before retaining its slot. Direct Git mutation is prohibited in epoch mode;
empty or unavailable configuration cannot fall back to the original slot.
The original owner/slot contract is unchanged. Actual
new graph outcomes retain the inherited shield and publication semantics.
Identical request replay restores that actual result without repeating Git.
Missing new results close startup whenever a plan or pending admission exists.
An active new slot remains unresolved even after a later terminal witness;
this example supplies no disposition or automatic retry for a fresh interruption.

Run expensive work serially in Nushell with fresh outputs:

```nu
node examples/offline-session-epoch/run.mjs <fresh-bundle>
node examples/offline-session-epoch/compile.mjs <fresh-bundle>
node examples/offline-session-epoch/test-runtime.mjs <bundle> <hash-verified-parent-bundle> <fresh-evidence>
# Copy the bundle to a moved location, reopen/re-emit, compile unchanged, and run
# the same driver against fresh domains before comparing artifacts/evidence.
node examples/offline-session-epoch/run.mjs --replay <moved-bundle>
node examples/offline-session-epoch/compile.mjs <moved-bundle>
```

Scope is one trusted serialized writer, immutable provisioning and intact flushed
storage in standalone Linux SHA1/single UTF-8 file fixtures, fake providers and
isolated Git config/hooks/signing. Full repository file bytes are observed;
full Unix/index metadata is not proved. Arbitrary races/descendants, uncooperative
termination, saturated synchronous creation/feeding, storage faults/deletion/
power loss, multiple writers, linked worktrees/live providers, coherent history
rewriting, general chained recovery, scale/compaction and peer acknowledgement
remain unproved. No real recovery, production change, push, PR or merge is part
of this example. Independent review must cover the exact successor commit/tree
before publication.

See the [verification record](../../docs/verification/offline-session-epoch-20261014.md)
and [proof manifest](fixtures/proof.json) for initial evidence and retained reuse.
The [owner-dispatch repair](../../docs/verification/offline-session-epoch-owner-repair-20261014.md)
records P1's exact reproductions and focused repaired controls.
