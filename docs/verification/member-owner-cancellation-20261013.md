# Whole-owner cancellation and shutdown — 2026-10-13

The admission recipe now has a bounded, reproducible parent-cancellation probe
and one composition-only return adaptation. **Six scripts / twelve real runs
agree exactly with virtual execution on their observable C# records. They do
not match Python's whole shutdown policy.** Four baseline `Ok` owner returns
become `Err("owner cancelled")` after scope drainage and resource release;
the two raw-fault owner outcomes are unchanged. No core, runtime, schema,
capability, dependency or inherited controller/admission changes were made.

The isolated local branch is `feat/controller-parent-cancellation`, starting at
`9e98b728b834a8ba5625c4a4a136bdbd904336fd`. Its source branch remains
`feat/bounded-request-admission`. Ancestor and repository instructions, `.agents`
and available skills were inspected; no applicable repository skill or additional
AGENTS.md was found. Nushell runs installed local tools and copied dependencies.
This milestone remains local, with publication blocked for parent human review.

## Source contract and baseline

The oracle requires unchanged rrawrlet HEAD
`4bab9e030744a0887c1ee13bdd8fc8db3b98334f` and exact working bytes for
`team.py`, `codex.py`, `runner.py` and `team_tools.py` before import. Their hashes
are recorded in the independent fixture and closure. It calls actual Team
methods/lock, `Codex.close`/`_close` and `await_cleanup`, with synthetic external
effects. It never starts a Codex process. The owner mirrors the runner's nested
cleanup ordering; `run_team` itself is not executed.

Python tasks do not inherit parent cancellation. `Codex._close` cancels
nonmutating handlers and gathers all handlers, while retaining `team_spawn`,
`team_integrate`, `team_discard` and `team_stop`. The runner finally awaits
`Team.close` through `await_cleanup`. Close obtains the Team lock for its
closing transition, then stops/joins Members outside that interval. Stop does
not cancel an already determined completed Member's snapshot. Cleanup retrieves
its result before propagating pending parent cancellation, so a raw cleanup
fault wins. These policies are distinct from individual request cancellation.

The unchanged C# TaskScope links each child token to its parent and closes
unresolved OneShots when its token cancels. CancellationShield masks the current
token for a protected callback; it does not disconnect an enclosing scope or
reopen handoff cells. The admission recipe therefore cancels queued mutations
and Members together, and an active mutation's late state transfer can fail
after its handoff has closed. The baseline owner still returned `Ok` after
draining. These are measured outcomes, not inferred Python equivalence.

| Held boundary | C# baseline and adaptation | Actual pinned Python |
| --- | --- | --- |
| Active workspace creation plus queued spawn | In-flight winner registers; queued loser/late caller cancel; closed state handoff hides winner in the retained view | Mutating handlers drain; winner registers, loser rejects capacity; close later rejects late spawn |
| Protected commit plus queued duplicate | First tail completes once and returns cancelled; duplicate cancels; late ledger handoff is unavailable | Both mutating handlers drain; first integrates and duplicate observes retained HEAD |
| Raw protected commit fault plus duplicate | Rollback finishes once; raw fault wins caller cancellation; duplicate cancels | Same first raw fault and rollback; retained duplicate retries successfully before shutdown |
| Determined Member snapshot | Synthetic snapshot host observes linked cancellation and throws a raw fault; owning scope drains before release | Parent cancellation does not reach Member task; completed snapshot is retained; close suppresses later checks |
| Shutdown drain | Graph closing request drains and acknowledges; baseline owner returns Ok, adaptation reports owner cancellation | Shielded runner cleanup drains, then raises pending cancellation |
| Raw resource release fault during shutdown | Release fault propagates, winning owner cancellation | Actual cleanup fault wins pending cancellation |

The complete request/outcome/view records differ in **all six scripts**. Common
invariants and selected fault precedence agree; the fixture never labels them
as whole-policy matches. The Python 3.14 release-fault run also records its
`RuntimeError exception in shielded future` diagnostic. The cleanup fault is
retrieved, all tasks finish, and it remains the owner outcome. The diagnostic
is retained as observable evidence.

## Minimal adaptation and deterministic bounds

[The authoring recipe](../../examples/member-owner-cancellation/author.mjs)
imports the existing composition unchanged. Four probe functions own it as one
task and inject linked parent cancellation through ordinary TaskCancel. The
adaptation adds `ownerCancellationRun`, which checks the restored owner token
only after `concurrentRun` returns from its drained scope and resource release.
The final graph has **120 functions / 2,582 nodes**: the 115-function / 2,530-node
subject, four harness functions / 43 nodes, and one return helper / 9 nodes.

Real runs exercise both graph parent-task cancellation and an external root
token. The external driver waits for the owner gate, cancels the root before
the graph repeats cancellation, and is joined before the host result is emitted.
Python repeats owner cancellation while shutdown is still held. No handler,
cleanup, Member, relay or driver is orphaned. Real/Python use explicit workspace,
commit, snapshot and shutdown barriers; virtual timing is supplementary
scheduling evidence, not a replacement for real barriers.

The settlement graph registry read is gated before releasing the raw snapshot
fault and follows an explicit peer join. Python's registry is read after final
cleanup. These are the actual available observable views. C# does not expose an
authoritative post-scope registry; the checker does not reconstruct one or
normalize away missing Member/ledger records. Other C# views follow request
joins; the queued case additionally joins its visible Member before inspection.

Each script has at most two registered Members and three concurrent callers.
All opened provider leases close exactly once; all owned tasks drain before the
one resource-release attempt. The fault case makes that release attempt throw.
Cancelled queued/late graph callers produce no workspace effects. Python rejects
late spawn after the actual close cutoff while drain remains held. An
uncancelled close-cutoff control verifies the wrapper retains Ok and the existing
composition still rejects admission before close drain finishes.

## Evidence and verification

Separate committed fixtures retain [baseline C#](../../examples/member-owner-cancellation/fixtures/nyrwvy-baseline.json),
[adapted C#](../../examples/member-owner-cancellation/fixtures/nyrwvy-adapted.json)
and [independent Python](../../examples/member-owner-cancellation/fixtures/oracle-expected.json).
Comparison checks exact request outcomes, nine-field registry views, mutation
order within each request, Member cleanup/snapshot/check order and required
happens-before. It permits independent reply order. Twelve controls mutate
normalized records and invoke these same assertions, including hidden owner
cancellation, queued admission, early/duplicate release, lost tail/rollback,
invented late ledger, determined Member cancellation and cleanup-fault precedence.
They are comparison controls, not compiled graph mutants.

Fresh proposals are reviewed and committed through ordinary authoring tools.
Saved/reopened emission, symbols, same-path replay and copied/moved replay retain
their identities. Installed Roslyn compiles Generated.cs without edits or
restore. Virtual and real paths share the unchanged C# emitter/runtime;
independent language evidence comes from pinned Python. The maintained Node
regression reproduces the complete adaptation and records the six divergences.

The [verified closure](../../examples/member-owner-cancellation/fixtures/verified-closure.json)
contains final hashes, source proofs, baseline/adapted classifications, compiler
evidence, retained regression counts and aggregate results. Reproduction commands
are in the [example README](../../examples/member-owner-cancellation/README.md).

| Check | Result |
| --- | --- |
| Offline Release solution build | Zero warnings/errors |
| .NET fast / extended | 862 / 154 (123 Core + 31 Extended) |
| Adapter / Node / MCP | 8 / 42 / 12 |
| Bun / adapter and Bun typechecks | 23 / both pass |
| Partition / staged whitespace | Disjoint/exhaustive / pass |
| Retained serialized oracle | 51 scenarios / 771 request audits; exact virtual/real/Python comparison |
| Retained admission oracle | Six exact virtual/real/Python comparisons |
| Parent cancellation | Six baseline and six adapted virtual runs; twelve real runs for each; six independent Python contracts; zero whole-record Python matches |

All suites and reproduction scripts ran serially. Local expanded execution
provided SDK output writes, test sockets and Node child waits; no network install
or dependency change was needed. Bun's initial bare path filters matched no
files; rerunning the four paths with `./` passed. No automatic approval review
rejected an action. Earlier fixture iteration exposed registry-read races; an
explicit peer join and a read-before-snapshot-fault release gate now force the
recorded partial orders, and exact virtual/task/root comparison rejects drift.

## Remaining gap and local handoff

This is a return-contract improvement and a verified divergence milestone,
not a repair of whole-parent Python lifecycle equivalence. Queue protection,
determined Member settlement, immutable state transfer after parent cancellation,
nonmutating handler shutdown and provider-specific cleanup policy remain
distinct. The probe does not claim all BaseException/OCE classes, simultaneous
typed acquisition failure/cancellation, arbitrary host faults or arbitrary
interleavings. The synthetic snapshot cancellation fault is ordinary raw host
failure after a held boundary, not a general provider adapter contract.

Tickets remain retained until scope exit, with linear storage and quadratic
total scanning across long scripts. Fairness, unbounded scaling, physical Git
rollback/recovery and restart persistence remain outside this milestone. No
live provider, credentials/account action, physical integration Git effect,
push, PR, upload or repository export ran. Source and user checkouts are
preserved. No further probe or publication retry is queued.
