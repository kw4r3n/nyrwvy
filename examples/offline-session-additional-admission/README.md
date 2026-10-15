# One additional admission after a fresh integration disposition

Experimental offline application recipe, based on reviewed commit
`98f22b98c2537f24af8095022d4ec6cfe772140f` (tree
`9e512d3efa6c316151521a29d08c401fe45c4667`). This finite successor accepts one
explicit additional admission, one fresh Member at the saved `next`, and one
preselected synchronous integration frame. It implements no subsequent recovery
or admission. All original, continuation and first fresh integration outcomes
remain unknown, with their pending registries, journals and full evidence exact.

The original executable closes before reading an additional begin/integrate
pair after disposition. This example composes existing graph operations to
address that policy gap. No core, parser, owner, ancestor source or generated
C# is modified. Unaffected recipes are shared; only storage-dependent runtime
recipes and their callers are composed into a new namespace. Old Member values
are restored into new owned handles without restarting old providers/workspaces.

`additionalRequest` names a raw JSON administrative request file.
`additionalAuthorization` must be a nonempty JSON string whose exact opaque bytes
match its request. This is explicit modeled runtime authority, within the offline
delegation; it does not request new human approval. The distinct ASCII `id` and
`epoch`, exact wire frames, canonical typed identity digests, selected commit,
saved next counter, predecessor receipt/inventory/custody, verified current HEAD
and complete baseline state are bound together. All five accepted ancestor
authorities differ from the new authority; optional ancestor labels can only
confirm accepted bytes. String and numeric wire IDs retain their distinction,
including every retained Member admission identity. Duplicate JSON keys refuse.

The graph validates and reconstructs the completed predecessor disposition,
then publishes `additional/v1/intent`, a blocked record retaining the three
unknowns and predecessor, an administrative result, an initial
`additional-registry/v1`, its `additional-lineage/v1/` sidecar, and activation
last. The old pending registry is never changed to idle/ready. Startup does not
complete administrative windows: after intent, only the identical explicit
request finishes. Exact replay returns the same administrative result and
preserves evolved new records. Concurrent/old administrative requests refuse.
Before the first intent there is no durable administrative acceptance.

Only the exact authorized begin/integrate frames can create a Member or run an
integration. The new Member's base is the verified current HEAD. Its candidate
must exist in the frozen original owned object inventory, provisioned before any
ancestor execution. New associations, registry/journal and lineage use separate
`additional-*` keys. The existing pinned epoch owner and three-stage launch keys
are reused with a distinct new effect token. Every retained command requires an
exact pinned quiescent launch and terminal witness; active/no-PID/unjoined,
partial, unavailable, unowned and inconsistent evidence refuses before writes.
Late terminal evidence cannot remove an active fence.

After a durable new plan/pending/launch, an interruption before actual result
publication retains the new unknown and closes startup. No success/cancellation
is inferred and no distinct retry or next recovery is accepted. A crash before
plan/pending acceptance permits the same selected integration. Once the actual
result snapshot is saved, response loss replays that actual result with no new
Git/provider/workspace effect. The old receipt is authenticated as frozen
baseline evidence; new plan custody and physical state are checked separately.

The wire parser stays bounded at 4096 characters, evidence/administrative parser
at 262144 characters, each recipe at 256 nodes. Expensive operations are run
serially in Nushell:

```nu
node examples/offline-session-additional-admission/run.mjs <fresh-bundle>
node examples/offline-session-additional-admission/compile.mjs <fresh-bundle>
node examples/offline-session-additional-admission/test-runtime.mjs <bundle> <verified-original-parent> <verified-epoch-parent> <verified-disposition-parent> <fresh-evidence>
# Copy bundle; reopen and re-emit exactly, then compile unedited Generated.cs.
node examples/offline-session-additional-admission/run.mjs --replay <moved-bundle>
node examples/offline-session-additional-admission/compile.mjs <moved-bundle>
```

The proof is limited to one trusted serialized writer, immutable provisioning,
intact flushed storage, Linux standalone SHA1/single UTF-8 file disposable
fixtures and fake providers with isolated Git configuration. It makes no claim
for repeated recovery, arbitrary epoch count/descendants, mutable-owner races,
storage faults/deletion/power loss, concurrent writers, complete filesystem
metadata, linked/live providers, scale/compaction or peer acknowledgement.
No live provider, production recovery, access change, PR, merge or publication
is part of this implementation.

See the [verification record](../../docs/verification/offline-session-additional-admission-20261015.md)
and [proof manifest](fixtures/proof.json) for measured results and retained evidence hashes.
