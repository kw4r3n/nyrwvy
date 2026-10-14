# One interrupted fresh epoch integration disposition

Status: Experimental; controlled offline example. Independent exact-head review
is required before publication. No further admission is implemented.

This successor starts at reviewed epoch commit
`29544f5e2b4725df4c3e381b85b7d80cc105804a`, tree
`d3f8e644d7cc7b85574efb74733f1f145afdacc3`. It retains one interrupted fresh
synchronous integration whose physical plan, pending current registry/journal,
three quiescent pinned-owner launches and exact terminal witnesses exist. The
new effect must explicitly classify as applied with a complete current physical
digest. Aggregate predecessor `state: applied` cannot establish this fact.

The original result, continuation result and fresh integration result remain
unknown. Every old/current record, Member association, counter, physical receipt,
launch, join and activation lineage stays exact. The current registry stays
pending. Physical completion is neither success nor cancellation.

`author.mjs` composes acceptance, authority separation, canonical typed identity,
epoch reconstruction, pending registry/journal validation, exact old entries,
Member admission and counter correlation, receipt encoding, publication order
and closure with ordinary reviewed graph operations. Existing recipe size bounds
are met by factoring validation functions; no generic primitive is missing.
`physical.template.cs` supplies raw evidence and mechanical ownership/physical
observations. It authenticates frozen predecessor evidence separately from the
current epoch, requires all three exact quiescent witnesses, and exposes the new
effect classification, raw inventory and current physical digest. The owner pin
is checked before storage access, inspection, writes or launch. There is no
direct mutation fallback.

Supply scenario `epochDispositionRequest` and exact nonempty JSON-string
`epochDispositionAuthorization`. The request has a distinct 1–80-character ASCII
administrative `id`, `action: retain-unresolved-epoch-integration` and
`scope: one-applied-owned-fresh-integration/v1`. Other fields bind exact accepted
epoch bytes and activation, blocked/registry/journal/plan/custody/inventory
digests, `newEffectState: applied`, complete current state digest, repository
identity, raw integration frame, canonical typed ID and identity digest, effect
identity, Member, next counter, fingerprint and raw intent. `fixture.mjs` contains
the independent offline request oracle; it performs no recovery mutation.

Only the explicit disposition administrative input may be supplied; concurrent
epoch/legacy administrative inputs refuse before writes or command dispatch.
The original 4096-character wire parser remains unchanged. Offline evidence and
decision JSON use a separately authored 262144-character parser entry with the
same depth, grammar and duplicate-key checks.

The decision ID differs from epoch/ancestor administrative IDs, identity digests,
effect identity and the accepted wire IDs' scalar spelling. String and numeric
wire IDs retain their canonical typed distinction. Accepted frame bytes remain
exact even when JSON escaping differs from the canonical ID. Required authority
is opaque: whitespace, Unicode and case remain significant. New authority differs
from all actual accepted ancestral/epoch authorities. Optional current ancestral
and epoch labels may be omitted or empty; present nonempty strings only confirm
accepted bytes. Null/non-string/contradictory labels refuse.

The graph publishes `epoch-disposition/v1/intent`, then one separate
`epoch-disposition/v1/receipt`, inside a cancellation shield. The receipt retains
the exact request and raw accepted epoch, blocked requests, old/current registry,
journal, plan, ordered custody, complete inventory, current new-effect digest,
three unknown outcomes and `admission: closed`. Before the first intent is
published, no decision has been durably accepted. Once intent exists, only its
exact explicit request can finish publication. Startup never completes these
windows. After receipt publication, exact replay reconstructs identical bytes;
changed bytes, ID, authority, lineage, custody, inventory or physical state and
distinct decisions refuse without writes.

Receipt or intent presence closes ordinary startup before input, including old
and new same-ID/changed-payload retries. The receipt permits no Git resumption,
Member/provider/workspace activity, request result, observation release or
admission. A separate authority/design/review is needed for any further admission.
Absent/no-PID, active, unjoined, partial, unavailable and unowned tails refuse;
a late terminal witness cannot clear an active fence. Actual new result
publication excludes disposition and retains its existing once-only replay.

Serialize expensive work in Nushell in fresh outputs:

```nu
node examples/offline-session-epoch-disposition/run.mjs <fresh-bundle>
node examples/offline-session-epoch-disposition/compile.mjs <fresh-bundle>
node examples/offline-session-epoch-disposition/test-runtime.mjs <bundle> <verified-original-parent> <verified-epoch-parent> <fresh-evidence>
# Copy the saved bundle, reopen/re-emit and compile unedited Generated.cs.
node examples/offline-session-epoch-disposition/run.mjs --replay <moved-bundle>
node examples/offline-session-epoch-disposition/compile.mjs <moved-bundle>
```

The finite proof assumes one trusted serialized writer, immutable provisioning,
intact flushed storage, Linux standalone SHA1/single UTF-8 file fixtures and fake
providers with isolated Git hooks/config/signing. General chained recovery,
additional epochs, mutable-owner races, arbitrary descendants, uncooperative
termination, saturated feeding, storage faults/deletion/power loss, multiple
writers, full filesystem/index metadata, linked/live providers, coherent history
rewriting, scale/compaction and peer acknowledgement remain unproved. No PR,
merge, feature push, production recovery or external provider call is part of
this work.

See the [verification record](../../docs/verification/offline-session-epoch-disposition-20261015.md) and [proof manifest](fixtures/proof.json) for observed evidence.
