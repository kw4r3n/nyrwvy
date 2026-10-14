# Terminal disposition of one applied additional integration

This finite offline successor is based on reviewed commit
`bb696d77958b30596c0c542a1d69543146bd72ef` (tree
`2599c914f00104c41cf8d74474305d2600bccf6d`). It accepts one explicit
`retain-unresolved-additional-integration` administrative decision after the
selected additional integration is fully applied, quiescent and still pending.
It preserves four unknown outcomes and closes ordinary startup permanently.
No subsequent admission or integration execution is introduced.

`additionalDispositionRequest` names an exact raw JSON request file;
`additionalDispositionAuthorization` is a nonempty opaque JSON string matching
its authority. It differs from all six accepted predecessor authorities.
Optional predecessor labels confirm retained accepted bytes. Administrative IDs
are distinct bounded ASCII strings; retained wire scalar spellings, typed
identities, Member admission identities, epoch/administrative IDs and effect
tokens remain reserved. Runtime authority is part of this offline model.

The graph reconstructs predecessor disposition and additional admission records,
validates both Member associations, unchanged old entries, saved next/counter,
pending registry/journal and empty actual results. Bindings reuse owner pinning,
ordered three-stage launch/terminal witnesses, physical plan and full GitState
classification. Both new and inherited custody are checked before physical
inspection. Each stage must be pinned, PID-bearing, quiescent, successfully
joined, closed and drained. No signal, fence or missing completion is repaired.
The current plan is classified separately against its own immutable baseline;
HEAD alone cannot establish ownership. The worktree checkpoint lacks a save
receipt; it is never fabricated. The applied pre-registry-publication variant
uses the same eligibility.

Only `additional-disposition/v1/intent` and `additional-disposition/v1/receipt`
may be added. Publication is cancellation-shielded and both slots are checked
before any write. Before intent the decision is unaccepted. After intent only
identical request bytes and authority may finish. Exact completed replay returns
one administrative acknowledgement with no new writes. Receipt-only, conflicting
or malformed slots refuse. Startup never completes publication and closes before
reading wire input when either slot exists. The acknowledgement stays outside
integration journals; no success or cancellation is inferred.

The receipt embeds request/digest, accepted additional request/activation,
current and source registry, current journal/plan, ordered raw launch/witness
custody, sorted complete raw-value inventory and all frozen decision digests.
Every other pre-existing raw record—including original/continuation evidence,
first-fresh receipt, additional blocked/result records, all Member associations,
sidecars and physical receipts—is referenced by filename and SHA256 through
`inventory-raw-values/v1`. Replay revalidates every reference and reconstructs
accepted graph records; an injected record cannot gain provenance by inventory.
Older literal `not-inferred` labels remain exact. New labels are
`originalOutcome`, `continuationOutcome`, `freshOutcome`, `additionalOutcome` =
`unknown`, `newEffectState: applied`, `admission: closed`.

Wire/evidence parsers retain limits 4096/262144 characters and depth 16, and each
recipe stays within 256 nodes. No core primitive, owner or ancestor source
changes. The complete receipt must parse within the existing evidence bound
before publication, so large authorities or extra request fields cannot silently
increase retained-record size. See [encoding proof](fixtures/encoding.json),
[acceptance manifest](fixtures/acceptance-manifest.json) and
[verification index](../../docs/verification/offline-terminal-milestone-20261015.md).

Run from the repository root with offline installed dependencies:

```sh
node examples/offline-session-additional-disposition/verify-bundles.mjs artifacts
node examples/offline-session-additional-disposition/run.mjs artifacts/terminal/bundle
node examples/offline-session-additional-disposition/compile.mjs artifacts/terminal/bundle
node examples/offline-session-additional-disposition/test-runtime.mjs artifacts/terminal/bundle artifacts/parent artifacts/epoch-parent artifacts/disposition-parent ../evidence/focused artifacts/original-v7
# Authenticate ancestor/base bundles against the additional-admission proof first.
# Copy the terminal bundle to a fresh location, then:
node examples/offline-session-additional-disposition/run.mjs --replay artifacts/terminal/moved
node examples/offline-session-additional-disposition/compile.mjs artifacts/terminal/moved
```

The claim is one bounded offline slice under a trusted serialized writer,
immutable provisioning and intact flushed storage, using Linux standalone SHA1,
single UTF-8 file fixtures and fake providers. Arbitrary epoch counts, repeated
recovery, active/partial-tail completion, hostile/concurrent writers, mutable-owner
races, storage faults/power loss, complete filesystem/index metadata, live
providers, scale/compaction and peer acknowledgement are deferred. No live
recovery, access changes, push, PR or merge follows from this example.
