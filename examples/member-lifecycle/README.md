# Offline Member lifecycle probe

A Nyrwvy-owned controller using the current Python rrawrlet `Team` as a
differential oracle. This is an offline experiment **using Nyrwvy**, not a
Nyrwvy integration into rrawrlet. No language primitives or runtime changes.

The bounded workload is two live Members and at most one fresh-context
continuation in each script. Leader requests are data: spawn, wait(any/all),
inspect, stop, spawn(previous), discard and finish. The graph processes them
serially. There is no claim about concurrent admission or a public Team API.

## Boundary

| Graph owns | Fake host supplies |
| --- | --- |
| Registry, deterministic attempt IDs, lineage, capacity | Typed provider work and expected error translation |
| Task handles, deadline classification, settlement | Fake workspace create/discard effects |
| Determined status and captured-commit handoffs | Fake snapshot and registered-check effects |
| Snapshot/check decisions, stop and finish | Clocks/barriers and recorded replies |
| Workspace ownership, latest-only discard eligibility | A provider-service resource and its close effect |

No task handles cross the host interface. A workspace is never a worker-scoped
resource: completion, stop, expected failure, timeout and finish retain it.
Only an accepted explicit discard calls the workspace discard effect. The graph
marks all related attempts discarded and rejects subsequent reuse/removal.

A provider result becomes a determined status before snapshot/check settlement.
`inspect` can show completed with no commit during snapshot, and completed with
the captured commit while checks run. `wait` reports running until the full
worker task settles. That same settlement test controls capacity and previous
eligibility. Stop joins an already determined result without cancelling its
snapshot/checks. Finish signals closing before joining, skips checks that have
not started, closes the provider resource exactly once, then acknowledges
finish. A second graph scope handles the suffix of the script with admission
closed; completed handles remain graph-internal and readable after owner closure.

## Reproduce

The committed `fixtures/` directory contains only synthetic graph definitions,
fake arguments, symbol identities, authoring metadata and a sanitized validation
summary. Regenerate the exact saved C# projection without committing runtime logs
or binaries:

```nu
node examples/member-lifecycle/materialize.mjs baseline /tmp/member-baseline-repro
node examples/member-lifecycle/run.mjs --replay /tmp/member-baseline-repro
node examples/member-lifecycle/test-runtime.mjs /tmp/member-baseline-repro
python examples/member-lifecycle/oracle.py /path/to/rrawrlet /tmp/member-baseline-repro
```

Use existing repository dependencies and installed .NET 10; install nothing for
this probe. From the Nyrwvy repository root, with a clean pinned rrawrlet checkout
at `4bab9e030744a0887c1ee13bdd8fc8db3b98334f`:

```nu
dotnet build tools/Nyrwvy.AuthoringSession --configuration Release --ignore-failed-sources -p:NuGetAudit=false -m:1
node examples/member-lifecycle/run.mjs /path/to/empty-bundle
node examples/member-lifecycle/run.mjs --replay /path/to/bundle
node examples/member-lifecycle/test-runtime.mjs /path/to/bundle
python examples/member-lifecycle/oracle.py /path/to/rrawrlet /path/to/bundle
```

`run.mjs` uses ordinary `createGraphTools` handlers, reviews and commits each
proposal, saves the graph and emitted projection, closes/reopens, re-emits with
identical source and symbols, then runs saved argument-keyed virtual fixtures.
Replay uses the saved graph/fixtures and does not author a replacement graph.
Move the whole bundle to verify independence from its original directory.

The runtime test compiles **unedited** `Generated.cs` with the existing offline
compiler helper: installed Roslyn and .NET reference assemblies, no restore,
PackageReference, SDK download or network. The host supplies actual thread
barriers. Two providers must both enter before either can leave in the overlap
case; before-start, snapshot and check gates have explicit release barriers.
Test process watchdogs are limited to these newly created fake test processes.

The oracle imports pinned `Team` and calls its actual `spawn`, `wait`, `inspect`,
`stop`, `discard`, `close`, `execute`, `latest` and `run_member_checks`. Only
external effects are replaced (`clean`, Git/workspaces, `build_attempt`,
`check_submission`). No subclass or replacement Team policy. Comparisons
normalize IDs, paths, clocks and the selected record fields, then compare
replies exactly. Per-attempt effect ordering is checked separately; arbitrary
cross-worker interleavings are not compared. Seven negative record controls
must fail comparison.

## Result and material gaps

Fifteen virtual, reopened replay and real-runtime fixtures pass their assertions.
Thirteen match the actual Team oracle. Two are explicit **fidelity failures**:

| Behavior | Outcome |
| --- | --- |
| Genuine two-Member overlap; wait any/all and timeout | Matched |
| Snapshot/check gated: running wait, occupied capacity, previous rejected | Matched |
| Stop before provider start/during run; retained workspace | Matched |
| Stop during snapshot/checks preserves determined completion | Matched |
| Expected provider error isolated; inner timeout failed; deadline timed_out | Matched |
| Snapshot failure failed without commit; checks failed/unavailable stay completed | Matched |
| Completed/stopped/failed/timed-out continuation reuses workspace/base, new ID | Matched |
| Superseded/discarded previous rejected; latest terminal discard once marks lineage | Matched |
| Finish rejects admission, joins, closes once, retains workspaces | Matched |
| Cancelling the caller's wait leaves Members alive | Member preserved; caller wait cancellation **fails** |
| Unexpected host exception isolated as a failed Member | **Fails**: Nyrwvy scope fault cancels peers |

`TaskWait` currently ignores caller cancellation in both its virtual blocking
path and generated real wait path. The negative graph cancels a separate waiter,
observes that it is still pending and its Member remains running, then cancels
and joins the Member to release the waiter. Python's caller wait cancels
immediately without cancelling the Member. No polling workaround or new
cancellation primitive is introduced.

Expected fake provider exceptions and inner timeouts are narrowly translated
into typed `Result.Err`. Unexpected host exceptions remain faults: they abort
the graph scope, cancel/join peers, close the provider resource, and cannot
produce an ordinary final controller report. An exception before the worker's
normal cleanup step also bypasses its `memberClose` effect. Python `execute`
catches ordinary `Exception`, records failed and closes that Member. This is
disclosed rather than silently translating every exception to expected failure.

Real Git security/integration/rollback, authentication, network providers,
catalog validation, token budgets, Leader deadline pausing, conversation reuse,
UI, unbounded scale, concurrent requests, arbitrary malformed request validation,
durable restart of live task state, and provider/workspace cleanup-effect failure
recovery remain unimplemented or excluded. Fresh context here is an independent
fake provider invocation, with no retained conversation object. The real
before-start barrier proves stop before provider invocation; the virtual and
Python cases also cover cancellation before coroutine work starts.

See the [verification record](../../docs/verification/member-lifecycle-offline-20261012.md)
for exact artifact identities, environment versions and the saved evidence layout.
The subsequent [no-core follow-ups](../member-lifecycle-followup/README.md) verify
an observer composition and an explicitly changed provider-effect contract while
retaining this baseline's raw divergences.
