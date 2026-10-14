# Explicit retention disposition

Status: Experimental; bounded offline example procedure. Independent review is
required before publishing this successor.

`retain-unresolved` records an explicit decision to retain an owned physical tail
and the original request's uncertainty. It changes one separate disposition key.
Original launch fences, journals, registry, outcomes/receipts, plans and side
receipts remain byte-for-byte intact. Missing results remain missing; retained
success and `err:cancelled` remain exact. No Git file/ref is restored or removed.

The Nyrwvy registry graph is unchanged. This administrative fixture mode runs
before that graph and transport input. Physical inspection and process custody
belong to the example binding; no Git/recovery primitive enters core. A language-
composed public recovery command and physical continuation remain later work.
The local fixture authorization string is explicit operator input, not a provider
credential or proof of access-control security.

## Controlled owner

Set scenario `gitOwner` to the absolute path of `git-owner.mjs` to opt in for new
mutation launches. The parent flushes a payload-version-2 active child record in
the existing `physical-child/v1` slot before owner creation, then retains the owner
PID before delivering the launch specification. The owner validates that PID,
invocation, repository, input and script digest against the retained record.
Only it owns the mutation child's stdin, stdout, stderr and root lifetime. It
closes stdin, waits for root exit and descriptor closure, drains bounded output,
and flushes `physical-join/v1/<invocation>` before returning output. Launcher
SIGKILL cannot turn a missing witness into a join observation.

Empty input is closed without a data write. The owner explicitly awaits stdin's
finish and close events as well as the child/output close event; root completion
alone does not establish input closure. Root exit and the deadline share one drain
timer, so successful bounded drainage cancels every termination timer. The
[review repair](../../docs/verification/offline-session-git-disposition-review-repair-20261014.md)
adds ordinary Git foreground/direct-command coverage alongside the held wrappers.

The witness binds the exact raw launch digest, owner script/PID, child PID,
invocation/effect/repository, arguments/input digest, observed exit code or SIGKILL
signal, actual input closure, completed drains and output/error digests. Normal
foreground completion checks those digests before publishing the inherited
quiescent record. A witnessed interrupted child can leave the active fence and
missing original outcome intact.

Each owner output stream is capped at 1,048,576 **bytes** with strict UTF-8 decoding;
the inherited direct binding's limit remains decoded UTF-16 units. The fixture
deadline is 250–10,000 ms, followed by up to two seconds for descriptor drainage.
Failed creation, feeding, capture or drainage publishes no witness. Process
creation and the small launch-specification write are synchronous and unbounded;
saturated input is unverified. This is a per-invocation local process, not a
service, and it cannot execute a later Git command after its terminal join.

Direct payload-version-1 launches retain their behavior. A quiescent version-1
record can be inspected for retention; an abandoned active version-1 record cannot
be migrated or disposed from its PID. Null-PID prelaunch records, missing witnesses,
root disappearance, complete Git state, false drainage and changed correlation
stay unresolved. No PID existence query authorizes disposition.

## Separate request and result

Provide a request JSON file through scenario `dispositionRequest`, alongside an
explicit nonempty `dispositionAuthorization`. Run the compiled `Runtime.dll` with
that scenario. The fixture prints exactly one disposition result and exits before
transport or the registry graph.

```json
{
  "id": "recovery-id",
  "authorization": "offline-owner-retain",
  "action": "retain-unresolved",
  "invocation": "<exact retained invocation>",
  "identity": "<original effect token>",
  "repository": "<absolute disposable git directory>",
  "repositoryId": "<fixture marker>",
  "launchDigest": "<SHA256 of exact raw launch record>",
  "planDigest": "<SHA256 of exact raw physical plan>",
  "expectedState": "object-only"
}
```

The recovery ID is a distinct 1–80-character ASCII identifier. Authorization must
match the scenario's explicit fixture authority. The binding validates the launch
against the version-3 physical plan and selected intent, then establishes join
evidence before active-launch inspection. Only this exclusive read-only inspection
scope can pass an active witnessed fence; mutation dispatch is refused inside it.
Application startup still refuses that unchanged active fence.

Inspection distinguishes `absent`, `object-only`, `ref-only` and `applied`.
Object-only/ref-only requires exact owned object inventory, refs/HEAD, pre-index
bytes/entries and pre-worktree bytes/Git mode. Applied state uses inherited exact
selected tree/HEAD/index-entry/worktree checks. The tail must fit the last invocation
and caller's expected state. Unowned objects/refs/index/worktree/mode, contradictory
side receipts, unavailable inspection and faulty evidence controls are refused.

`commit` and `head` receipts require the matching ref state (`ref-only` or `applied`)
and a last mutation invocation of `update-ref` or `read-tree`. A `save` receipt
requires `applied` state and a last invocation of `read-tree`. No receipt is valid
for an absent or object-only tail. Missing receipts remain non-authoritative.
These checks run before replay of an existing retention result, and refusal never
rewrites that result or an original application record. ID and output/error digest
validators use absolute full-string anchors, including final-newline refusal.

Success flushes `physical-disposition/v1/<id>` with request/launch/plan/state/witness
digests, exact correlation and `ok:retained-unresolved:<state>`. Its
`originalOutcome: "not-inferred"` field is never an application outcome. Identical
request bytes replay after fresh authentication/inspection without another write.
A different authorized ID can record its own retention decision. Conflicting
bytes, authorization, physical facts or retained disposition fields return
`err:unresolved` without rewrite. Before-publication crashes safely repeat this
read-only decision; after-publication crashes replay the separate retained result.

## Scope and next boundary

One trusted serialized application writer transfers launch custody to one
controlled owner. Parent and owner write different slots in the tested order;
this is not multi-writer exclusion. Intact flushed storage and immutable fixture
provisioning are trusted. Hashes and fixture authority do not authenticate
coherently rewritten historical storage.

Supported fixtures are standalone Linux SHA1 repositories with one UTF-8 file,
disabled hooks/signing and isolated Git config. Controlled test wrappers hold and
release local commands; their observations do not establish arbitrary escaped-
descendant safety. Uncooperative effects, arbitrary races, power loss/storage
faults, full Unix/index metadata, linked worktrees/live providers, arbitrary
scale/compaction, peer acknowledgement and inherited parser limits are unverified.

Next: separately authorized continuation of an owned partial tail, including its
own durable pending intent, mutation custody and crash windows. Retention leaves
an abandoned active original domain closed. Continuation/release must preserve
original uncertainty; physical completion cannot supply the lost original result.

Run the maintained physical-Git wrapper, or serialize `test-disposition.mjs` for
fresh original/moved bundles and then `verify-disposition.mjs`. See the
[verification record](../../docs/verification/offline-session-git-disposition-20261014.md).
