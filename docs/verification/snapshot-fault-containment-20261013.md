# Opt-in snapshot-fault containment — 2026-10-13

Historical PR391 evidence. The next milestone adds generic cancellation
classification and refines the opt-in handler; see [the current proof](exception-is-cancellation-20261014.md).

The opt-in admission/controller recipe now contains ordinary raw Host.snapshot
exceptions as failed Final values and skips checks. It uses the existing
ExceptionCatch and typed Result composition; the catch encloses only the snapshot
invocation. Commit publication, checks, memberClose and outer resource release
remain outside. The default `containSnapshotFaults` is false throughout the
serialized controller, admission and owner recipes. No generic semantics are
required, and no core, runtime, schema, capability or generated-source edit occurs.

The isolated checkout is `/home/kw4/Documents/Codex/2026-10-13/task-17/nyrwvy`,
branch `probe/snapshot-fault-containment`, based exactly on merged main
`86d5262524f0709ee07d3477d56c99e398a116a5` (PR390), tree
`633b0e797e94292192ed03603e52125533fb189e`. Prior task-8 at
`1f39f0c21cdc4039ba433c4db794915b49547fca` has that same tree and is preserved.
Home instructions, ancestor/repository instructions and the existing exception,
determined-settlement, close-intent and verification recipes were inspected.
No additional applicable AGENTS.md or skill was found. Commands execute through
Nushell. Installed dependencies/build assets were copied locally, then the
Release solution was rebuilt; tracked source remains the named main baseline.

## Source and boundary

Unchanged Python remains pinned to `4bab9e030744a0887c1ee13bdd8fc8db3b98334f`
in `/home/kw4/Documents/Codex/2026-10-12/task-12/rrawrlet-oracle`. Before import,
the independent oracle checks HEAD and exact working bytes of team.py, codex.py,
runner.py and team_tools.py against Git. Their hashes are retained in its fixture.

Actual Team.execute lines 699–725 snapshots after Member cleanup and determination,
sets failed on `except Exception`, then starts checks only if still completed and
not closing. CancelledError is excluded from that catch. Actual stop joins a
determined Member without injecting another cancellation. Actual close sets
closing under its actual lock, then stops/joins; await_cleanup drains it through
repeated caller cancellation. The oracle calls those actual methods with synthetic
external effects. It starts no provider and copies no method policy.

Two ordinary graph functions, `snapshotInvoke` and `snapshotFault`, form the
boundary. The handler returns Err(ExceptionMessage); the existing MatchResult
returns a failed Final without publishing a commit or starting checks. Final has
no error-text field, so the bounded comparison covers status/commit/checks, not
Python's persisted diagnostic text. The probe adds the same token/close audits
to both variants. Instrumented graphs grow from 125 functions / 2,748 nodes to
127 / 2,755: two functions and seven nodes. Subject snapshotInvoke contains only
the host invocation; probe audits surround it in the verification projection.

## Before/after proof

| Script/control | Baseline | Opt-in result |
| --- | --- | --- |
| Ordinary raw snapshot | Raw fault | Failed Final retained, owner succeeds |
| Held raw snapshot plus owner/root cancellation | Raw fault wins cancellation | Failed Final retained, owner cancellation after drain |
| Admitted close plus held raw snapshot | Raw fault | Close drains, failed Final retained, no checks |
| Raw snapshot plus raw release | Snapshot fault wins | Snapshot contained; release fault wins cancellation |
| Typed snapshot failure | Failed Final | Same complete observations |
| Success without close | Completed commit1, checks passed | Same complete observations |
| Success with admitted close | Completed commit1, checks skipped | Same complete observations |
| Success plus raw release | Release fault | Same complete observations |
| Raw checks fault | Propagates | Same raw fault; catch does not cover checks |
| Undetermined provider cancellation | Stopped, no snapshot | Same complete observations |
| Explicit snapshot OCE | Real C# OCE propagates | Failed Final; Python CancelledError remains distinct |

Three ordinary-fault scripts fail the exact same acceptance assertions before
the change in virtual, real owner TaskCancel and external-root modes: nine RED
assertions. All eleven protected scripts pass in all three modes: 33 passes.
Six unchanged controls preserve complete before/after graph observations in
those modes: 18 comparisons. Uncancelled concurrentRun controls use the same
uncancelled owner in both real modes; only ownerProbe scripts inject root
cancellation. Both variants use identical scripts, effect arguments and audits.

Real C# uses explicit snapshot/provider/check, close-admission, owner-park,
cancellation-injection and release barriers. The provider witnesses cancellation
before its release gate. Python independently uses asyncio events and the real
Team.lock; admitted close waits behind that lock until after owner cancellation.
Every retained graph view follows an explicit wait/join. Every opened provider
lease cleans up once; graph-owned requests/relays/Members drain before one release
attempt. The synthetic external driver is explicitly joined after the graph
invocation finishes, before publishing evidence. All per-script external effect sequences agree
before/after. Virtual and real share the emitter/runtime; Python is independent.

Fourteen sensitivity controls reject escaping snapshot faults, late checks,
lost failed Final, inspection before join, duplicate cleanup/release, early
release, swallowed release/checks faults, lost close intent, erased success
checks, snapshots of undetermined providers, exposed snapshot tokens and hidden
OCE divergence. These mutate records, not compiled graphs.

The explicit real OCE uses a clear token; neither its message nor token state
classifies it in the graph. Actual Python instead has a cancelled Member task,
completed status, no commit/checks and no final save. It remains inspectable
after actual wait and close. Its ordinary failures record failed status and a
Snapshot failed diagnostic. The graph's opt-in catch has the broader existing
System.Exception semantics. Python's raw checks error also leaves a completed
commit but faults its Member task; it is observed and retrieved separately from
the graph's propagating scope error. Whole shutdown parity is false.

## Retained evidence and review

Ordinary create/review/commit tools author the graph. Saved/reopened emission
and symbols match. Same-path and copied-path replays preserve graph, source,
symbols and cases. Installed Roslyn compiles emitted Generated.cs unedited,
without restore. Committed before/after/Python/proof fixtures and the independent
oracle are in [the example](https://github.com/kw4r3n/nyrwvy/blob/archive/legacy-examples-pre-cutover-20261019/examples/member-snapshot-faults/README.md).
Complete local transcripts, runtime bundles, replays and serial suite logs are
under `artifacts/snapshot-faults/`. The closure records their hashes and commands.

Fresh retained controller, admission, recovery and determined examples exercise
the unchanged defaults and previous cancellation boundaries. The existing
close-intent, exception and other Node regressions also run in the serial
aggregate. Final suite counts and log hashes are in the
[verified closure](https://github.com/kw4r3n/nyrwvy/blob/archive/legacy-examples-pre-cutover-20261019/examples/member-snapshot-faults/fixtures/verified-closure.json).

The first Node aggregate passed 45 of 46 tests. Its CLI contract failed because
the local dependency copy omitted the MCP workspace's separate node_modules.
Restoring those unchanged locked dependencies from task-8 made all five affected
CLI fixture tests pass. The complete Node aggregate was then rerun; the original
setup failure remains in the local logs and closure. No dependency declaration
or lockfile changed.
The first Bun invocation selected no tests because its path filters lacked the
required `./` prefix. The corrected invocation ran all tests; that selector error
is also retained.

| Final serial aggregate | Result |
| --- | --- |
| Release solution build, no restore | Zero warnings/errors |
| .NET fast / extended | 862 / 154 (123 Core + 31 Extended) |
| Adapter / Node / MCP / Bun | 8 / 46 / 12 / 23 |
| Adapter and Bun typechecks | Both pass |
| Suite partition / fish contract and syntax / whitespace | Pass |

This is a bounded opt-in implementation probe. Queued-handler protection,
bare-owner shutdown, atomic concurrent check cutoff, arbitrary fault classes or
interleavings, provider-specific shutdown, physical Git recovery and restart
persistence remain excluded. No cancellation class test or new generic semantic
capability is proposed. Retained views use explicit joins; they do not establish
a new authoritative post-scope registry API. Existing ticket storage/scanning
still has linear space and quadratic total scanning bounds.

Expanded local execution supports SDK writes, Node child-process waits and test
sockets. No automatic approval review rejected an action. All effects are
synthetic; no live provider/account/credential action, force push, branch deletion,
PR, merge or unrelated upload occurs. The exact tested commit/tree is returned
to the parent for independent review. Feature-branch publication waits for the
parent's review confirmation; the parent handles PR and merge.
