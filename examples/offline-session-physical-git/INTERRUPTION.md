# Retaining an interrupted continuation

Status: Experimental; controlled offline administrative example. Independent
review is pending. Renewed mutation admission and public language-composed
recovery remain missing.

`retain-unresolved-continuation` records one separately authorized decision for
the **new** continuation request whose immutable intent exists and whose actual
result is missing. It changes only `physical-continuation-disposition/v1`.
Original and new intents, launches, joins, outcomes, registry/journal, receipts
and fences remain exact. Its receipt is not either request's success or
cancellation; both missing outcomes remain missing.

Use scenario `interruptedContinuationRequest` and a nonempty
`interruptedContinuationAuthorization`. The administrative decision runs before
the registry graph or transport input. Supply exactly one administrative request:

```json
{
  "id": "retain-continuation-id",
  "authorization": "offline-owner-retain-continuation",
  "action": "retain-unresolved-continuation",
  "continuationId": "continue-id",
  "continuationRequest": "<exact accepted continuation request bytes>",
  "continuationIntentDigest": "<SHA256 of exact retained intent bytes>",
  "custodyDigest": "<SHA256 of ordered per-stage custody array>",
  "evidenceDigest": "<SHA256 of complete retained evidence inventory>",
  "expectedState": "ref-only",
  "observedStateDigest": "<SHA256 of current complete physical-state JSON>",
  "storageContract": "intact-flushed-single-writer-before-slot/v1"
}
```

The custody array covers every command slot, including absent slots. Rows have
`key`, `stage`, `status`, `invocation`, `launchDigest` and `witnessDigest`; absent
rows use empty strings for the last three fields. The inventory maps sorted
SHA256 storage filenames to exact byte hashes, excluding only this decision's
receipt. The receipt retains the array/inventory, exact request, original
correlation/digests, current state digest, `originalOutcome: not-inferred`,
`continuationOutcome: not-inferred` and `admission: closed`. Any retained evidence
change refuses exact replay. Encodings belong to this fixture binding, not a new
public protocol or generic storage schema.

The new ID differs from the accepted retention/continuation IDs and original
effect identity. IDs retain the full-string 1–80-character ASCII rule. Required
authorities are exact nonempty JSON strings; whitespace, Unicode, padding and
case remain significant. New authority must differ from the actual accepted
retention and continuation authorities. Optional earlier scenario settings may
be absent or empty strings; nonempty values must exactly confirm accepted
authority. Present null/non-string settings refuse. Current settings cannot
relabel accepted authority, including during replay.

## Custody before inspection

Both the request and scenario `interruptedContinuationStorageContract` must name
`intact-flushed-single-writer-before-slot/v1`. This is an explicit trusted immutable
provisioning assumption, not storage-fault detection or authentication. The probe
demonstrates that intent publication precedes slot creation and a flushed active
slot precedes process creation. An absent slot supports only before-creation
observation under intact storage. It manufactures no completion and permits no
execution. An active prelaunch slot without a PID refuses.

Every retained new launch requires its exact controlled-owner terminal witness
before any Git inspection. The owner digest is pinned into the compiled binding.
Unsupported versions/owners, missing witnesses, root exit/PID absence alone,
open input/output, changed arguments/input/capture or contradictory joins refuse.
Quiescent launches reconstruct their exact prior active payload to validate the
owner witness and actual exit. Active witnessed launches stay active. Supported
new `update-ref`/`read-tree` captures have empty stdout/stderr; other captures
remain unresolved in this bounded milestone.

Later slots require an earlier quiescent zero-exit launch. An absent earlier slot
followed by a later retained launch refuses. Complete owned objects, refs/HEAD,
index and worktree bytes/modes must fit the last witnessed command and explicit
current observation. Before-creation object/ref tails, joined partial states and
applied tails with a lost new result are supported. Unavailable, contradictory or
unowned state refuses without writes. Late effects never supply request outcomes.

## Publication and replay

One flushed receipt retains one interruption decision per domain. Before-publication
death permits the explicit read-only decision again. After-publication death
requires exact replay: fresh authority, custody, lineage and physical checks must
reproduce exact receipt bytes. Same-ID changed bytes and distinct IDs refuse
without rewrite. Contradictory receipts stay intact. Actual continuation completion,
observation intent or release records exclude this procedure.

The decision performs no Git mutation, command resumption, record replacement,
object deletion, ref rollback or index/worktree reset. Startup stays closed before
input. Existing continuation/release cannot use its receipt as execution or
admission authority. A later admission design must explicitly retain blocked
request identities, outcomes and immutable evidence.

## Reproduction and limits

Serialize heavy work in Nushell; reopen/re-emit the unchanged saved graph and
compile unedited `Generated.cs` in original/moved locations. Run
`test-interruption.mjs <bundle> <fresh-output>` in each location, then
`verify-interruption.mjs <original-output> <moved-output> <bundle> <moved-bundle>`.
Run affected inherited continuation/disposition regressions separately. The
[verification record](../../docs/verification/offline-session-git-interruption-20261014.md)
separates fresh checks from hash-verified reused evidence.

Scope is one trusted serialized writer, immutable provisioning and intact flushed
storage in controlled Linux standalone SHA1/single UTF-8 file fixtures, with fake
providers and isolated Git config/hooks/signing. Arbitrary races/descendants,
uncooperative termination, saturated synchronous creation/feeding, cancellation
beyond shields, storage deletion/faults/power loss, multiple writers, full Unix/index
metadata, linked worktrees/live providers, coherently rewritten history,
scale/compaction and peer acknowledgement remain unproved. Core, generic storage,
task, shield and registry-graph semantics remain unchanged.
