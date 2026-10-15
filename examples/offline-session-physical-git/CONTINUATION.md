# Explicit owned-tail continuation and observation admission

Status: Experimental; bounded offline administrative example. Independent review
is pending. The registry graph, semantic core, runtime, backend and generic opaque
storage remain unchanged. Public language-composed recovery and renewed mutation
admission remain missing; this procedure does not implement them in host code.
Separately authorized [interrupted-continuation retention](INTERRUPTION.md)
preserves an unfinished new request without mutation or admission release.

An exact `retain-unresolved` result supplies prior evidence. It grants no authority
to continue Git or open admission. A new `continue-owned-tail` request requires a
distinct ID, separate explicit fixture authorization, the exact raw retention
request, its result digest, the expected partial state and its observed digest.
The decision and observation must match the original invocation, effect, selected
intent, plan, launch and disposable repository through that lineage.

```json
{
  "id": "continue-id",
  "authorization": "offline-owner-continue",
  "action": "continue-owned-tail",
  "retentionRequest": "<exact raw JSON bytes of the prior retain request>",
  "dispositionDigest": "<SHA256 of the retained disposition bytes>",
  "expectedState": "object-only",
  "observedStateDigest": "<stateDigest in the exact prior disposition>"
}
```

Set scenario `continuationRequest` to this file, `continuationAuthorization` to
the explicit continuation authority, `dispositionAuthorization` to the separate
retention authority and `gitOwner` to the maintained owner. Authority strings are
offline operator inputs, not access-control credentials. IDs use the retained
1–80-character ASCII boundary with absolute full-string anchors.

Required authority values are exact nonempty JSON strings. Missing, null, numbers,
booleans, objects, arrays and empty strings refuse before acceptance. Authorities
are opaque values: whitespace and case are significant, with no trimming or new
character grammar. A whitespace-only string is nonempty and requires an exact
explicit match. This does not change the stricter ASCII request-ID grammar.

## New physical request

Before any new intent or Git mutation, the procedure revalidates prior retention,
terminal original custody, complete owned state, original pending registry/journal
correlation and absence of original actual-outcome entries. Authoritative unchanged
(`absent`) and completed (`applied`) states are observable but unsupported for this
action. Missing or contradictory custody, receipts, mode/index/worktree/object/ref
state, legacy active launches, unavailable inspection and orphan continuation
records refuse without a new write.

`physical-continuation-intent/v1` is a single immutable domain reservation. It
retains new request bytes/digest, exact original correlation and hashes of every
original storage file. Object-only completion uses `update-ref` with the retained
old value, then `read-tree --reset -u`; ref-only completion uses only `read-tree`.
It never repeats `commit-tree`, deletes objects, rolls a ref back or restores the
original pre-state to claim absence. `read-tree` is limited to the already
inspected, clean, owned disposable index/worktree.

Each new command has its own `physical-continuation-child/v1/<id>/<stage>` slot,
fresh invocation and exact owner join. The mechanical owner supports selecting
these bounded slots; it implements custody rather than application policy.
The original launch slot, active fence, joins, plans, registry, journals, receipts
and actual outcomes are never replaced. Original-file hashes and a closed allowed
inventory reject changed or newly injected original evidence at replay/release.

`physical-continuation-result/v1/<id>` records only the new actual completion:
pending-intent digest, final state digest, command launches/joins and
`ok:continued-owned-tail:<commit>`. `originalOutcome: not-inferred` is not an
original application outcome. Exact authorized request replay freshly checks
lineage/custody/state and returns this result without mutation or rewrite.
Changed bytes/authority/correlation and a distinct ID refuse.

Death before intent publication permits fresh explicit execution. Once intent
is published, a missing new result stays unknown and blocks same and distinct
IDs, even after an exact join or completed Git state. No command is retried. This
includes prelaunch, between-command and before-result deaths. A separately
authorized [interruption decision](INTERRUPTION.md) can retain supported unfinished
new requests while preserving unknown outcomes and closed admission.

## Separate observation release

Completion closes startup until a new release request is accepted. Release has
its own ID, explicit authority and binding to the exact new result and observed
state; its ID differs from both retention and continuation IDs.

```json
{
  "id": "release-id",
  "authorization": "offline-owner-release",
  "action": "release-observation-admission",
  "continuationId": "continue-id",
  "continuationDigest": "<SHA256 of exact new completion record>",
  "observedStateDigest": "<stateDigest in that completion>",
  "registryDigest": "<digest of unchanged original pending registry>"
}
```

Use scenario `releaseRequest`/`releaseAuthorization`. The procedure retains
`physical-observation-intent/v1`, its separate
`physical-observation-result/v1/<id>` and a last
`physical-observation-release/v1` activation record. Every publication is flushed.
Release performs inspection and storage publication only. Identical explicit
release can finish these publication windows after a crash; until activation,
startup is closed. Conflicting requests/records or missing continuation results
refuse. Release never resumes a pending Git request.

Release authority must differ from the authorities in the exact **accepted**
continuation request and its nested retention request. Omitted earlier scenario
authority settings are permitted. Any nonempty `continuationAuthorization` or
`dispositionAuthorization` supplied during release or startup must match that
accepted lineage; relabelling configuration cannot replace it. These checks run
before any release write, including replay of a partially published release.

Before transport input, startup rechecks release request, result, activation,
original inventory, every new command's join and exact completed physical state.
Authority separation is rechecked at startup too. Previously invalid activated
release records stay intact but close before input; no migration or repair is
inferred. Drift or unavailable inspection closes before input. It then passes the unchanged
pending registry to the inherited graph to restore original Member values. No
synthetic `ready` registry or success/cancellation result is supplied.

The demonstrated admission is observation of retained values (`inspect`, terminal
reporting and settled-task observation). Their retained integrated HEAD stays empty
when it was empty originally; the new physical HEAD belongs to the separate
continuation result. Original and distinct integration IDs, new Member admission
and discard remain unresolved in the graph. The binding refuses all application-
storage writes in this released scope. This is not renewed mutation admission or
migration of the original pending request. Ordinary runtime scope closure does
not determine an original outcome.

## Reproduction and limits

Serialize expensive work in Nushell. Use a fresh graph bundle (or hash-audit a
retained graph), reopen/re-emit it, compile unedited `Generated.cs`, and run:

```nu
node examples/offline-session-physical-git/test-continuation.mjs <bundle> <fresh-output>
node examples/offline-session-physical-git/test-continuation.mjs <moved-bundle> <fresh-moved-output>
node examples/offline-session-physical-git/verify-continuation.mjs <fresh-output> <fresh-moved-output> <bundle> <moved-bundle>
```

The [verification record](../../docs/verification/offline-session-git-continuation-20261014.md)
separates baseline, preparatory, fresh continuation, inherited regression and
mechanical evidence. Local raw storage/physical assertions are exact. Cross-domain
comparison omits only index-stat digest; paths/PIDs remain in raw evidence.
The [release-authority review repair](../../docs/verification/offline-session-git-release-authority-repair-20261014.md)
records the original reviewer reproductions and the focused retained/current
configuration, replay, startup and publication-window checks.

The [authority-value repair](../../docs/verification/offline-session-git-authority-values-repair-20261014.md)
adds strict JSON type/nonempty checks at retention, continuation, release and
retained startup boundaries. Optional earlier configuration permits omission or
an empty string; a present null/non-string setting is malformed and refused.

Scope remains one trusted serialized writer, immutable fixture provisioning,
intact flushed storage and controlled Linux standalone SHA1/single UTF-8 file
repositories with isolated hooks/signing/config and fake providers. No arbitrary
race/escaped-descendant safety, multiple writers, Git fsync/power-loss durability,
storage deletion/faults, coherently rewritten history, full Unix/index metadata,
linked-worktree/live-provider lifecycle, saturated feeding, cancellation beyond
shields, scale/compaction or peer acknowledgement is established. Owner creation
and specification feeding remain synchronous/unbounded. One continuation and one
observation-release decision are retained for this domain; no general chained
recovery, compaction or future physical mutation is supported.
