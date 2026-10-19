# Admitted close intent after cancelled handoffs — 2026-10-13

The default admission composition now preserves an admitted close's suppression
of subsequent Member checks when parent cancellation closes the state handoffs
before close publication. The close latch alone lives in a shielded scope; the
admission task starts through the original cancellable scope. Provider work,
queued callers, Member scopes, immutable state handoffs, determined settlement
and completed-result recovery keep their existing cancellation policies.

The isolated branch is `probe/close-intent-handoff` in
`/home/kw4/Documents/Codex/2026-10-13/task-8/nyrwvy`, based exactly on verified
main `afc827984f45a0be3a7c085167ada5ccd77e8547` (PR389), tree
`e0cc84588b2639ffd73086a1935eb2a2d63d109e`. The task-5 checkout at
`107d65692609426d189f316be2d14aa24df92c37`, with that same tree, is preserved.
Ancestor/repository instructions, available skills and prior owner/recovery/
determined reports were inspected; no additional applicable repository skill
or AGENTS.md was found. Commands use Nushell. No core, runtime, schema,
capability, dependency or generated-source modification is needed.

## Actual Python policy and graph boundary

Python remains unchanged at `4bab9e030744a0887c1ee13bdd8fc8db3b98334f` in
`/home/kw4/Documents/Codex/2026-10-12/task-12/rrawrlet-oracle`. Before import,
the oracle checks HEAD and exact working bytes of team.py, codex.py, runner.py
and team_tools.py; their hashes are retained in the independent fixture.

Actual `Team.close` acquires `Team.lock`, sets `closing=True`, then stops/joins
Members outside the lock. Merely requesting close behind the lock has not set
that flag. Actual `Team.execute` determines status before agent cleanup, takes
a completed Member's snapshot, then tests `not self.closing` before calling
checks. Close does not erase an already captured commit or undo checks already
entered. Actual `stop` joins a determined Member instead of cancelling it.
Actual `await_cleanup` drains cleanup before propagating caller cancellation.
The new oracle invokes these actual methods, with synthetic provider, Git,
checks and save effects. It runs no provider process and copies no policy method.

The graph admits an exclusive request after observing its predecessor and
passing its cancellation check. The probe holds `enter:closing` after that
boundary and before close publication, then cancels the owner. In the baseline,
`OneShotComplete(closingSignal)` refuses because the owning scope has closed its
handoffs. A shielded determined snapshot then polls None and starts checks.
CancellationShield cannot reopen a OneShot in a previously cancelled scope.

The fix moves the existing latch to a separately shielded scope. Four ordinary
functions start the whole admission task through the original execution scope,
give its nested cancellable scope the latch, and await its complete task result.
The protected latch scope closes only after that task's scope has drained all
requests, relays and Members. The outer resource release sees the restored
original token. This keeps both success and fault drainage and release ordering.
The subject adds four functions / 32 nodes, one owned task and two scopes per
run. The instrumented graphs contain 121 functions / 2,716 nodes before and
125 / 2,748 after. No new graph type is required.

The latch records admitted close, not a queued request's existence. A queued
close cancelled before admission publishes nothing and returns `err:cancelled`.
An admitted close can continue its existing drain despite later caller
cancellation. Bare owner cancellation with no admitted close still does not
publish this latch; automatic Python-style owner shutdown is a separate policy.

## Baseline, fixed matrix and ordering controls

Seven admitted-close scripts fail the same latch-retention acceptance assertion
in virtual, real TaskCancel and real external-root modes: 21 RED assertions.
Three successful-snapshot scripts additionally demonstrate the actual symptom:
one late check before versus zero after, in all three modes (nine suppression
failures). Typed/raw snapshot and stopped-provider controls do not run checks
in either graph; their failure is specifically lost admitted intent.

| Boundary/control | Baseline | Fixed |
| --- | --- | --- |
| Held completed snapshot | Latch refused; checks run | Accepted; commit1 retained; checks skipped |
| Held completed cleanup | Latch refused; checks run | Accepted; completed status and snapshot retained; checks skipped |
| Typed snapshot failure | Latch refused; failed Final | Accepted; same failed Final |
| Raw snapshot failure | Latch refused; raw fault | Accepted; same raw fault wins cancellation |
| Raw release failure | Late checks run; release fault | Checks skipped; same release fault wins |
| Close published before cancellation | Checks skipped | Same cutoff and drainage |
| Checks entered before close | Checks finish | Checks finish; no retrospective erasure |
| Only admitted close caller cancelled | Close drains; owner succeeds | Same |
| Queued close caller cancelled | No close intent; checks run | Same; caller cancels independently |
| Undetermined provider / completed return after cancellation | Stopped, no snapshot | Same; provider token cancels before release gate |

Eleven scripts pass after the fix in all three graph modes: eleven virtual runs
and twenty-two real runs per graph. The two individual-caller scripts use the
ordinary uncancelled owner in both real invocation modes; they do not inject an
external root cancellation. C# barriers control request admission, publication,
held effects and cancellation release. The external driver is owned and joined.
Python independently controls its actual lock and effect events, repeats owner
cancellation and waits for actual closing before releasing settlement. The
queued Python control cancels close while it is still blocked on the actual
lock, verifies `closing=False`, then permits checks before final cleanup.

The graph queued control admits a second Member as its ownership blocker;
Python uses the actual lock with one Member. Their full records stay separate.
Both graph Members are explicitly joined before inspection. Per-Member effect
order, every reply and retained state are compared across virtual/task/root;
independent Members need not share a total effect order. Before/after outcomes
and per-Member noncheck effects remain unchanged. All opened provider leases
close once and every owned scope drains before one outer release attempt.

Twelve sensitivity controls reject lost publication, late checks, erased prior
checks, invented queued close, protected queued caller, exposed settlement
token, downgraded completion, retained cancelled provider completion, duplicate
cleanup, swallowed raw snapshot/release fault and early release. These mutate
records; they are not compiled graph mutants. Actual Python supplies independent
language evidence. Virtual and real use the same C# emitter/runtime.

An early probe compared a real settled peer with a virtual in-flight peer and
also required a total order between independent Members. The failure was kept
under local artifacts. Explicit peer joining and per-Member order comparisons
now resolve that observation boundary without hiding registry fields.

## Evidence and review scope

Ordinary tools author, review and commit graph proposals. Saved/reopened source
and symbols match. Same-path and copied-path replay retain graph, Generated.cs,
symbols and cases. Installed Roslyn compiles Generated.cs unedited and offline.
The maintained Node test authors both variants afresh, runs real C#, checks
committed observations and independent Python fixtures, then verifies both
replays. Fixtures and closure are in
`examples/member-close-intent/fixtures/`; complete transcripts, runtime bundles,
compiler evidence and serial suite logs are under `artifacts/close-intent/`.
The [README](https://github.com/kw4r3n/nyrwvy/blob/archive/legacy-examples-pre-cutover-20261019/examples/member-close-intent/README.md) gives reproduction commands.

| Final serial aggregate | Result |
| --- | --- |
| Release solution build, no restore | Zero warnings/errors |
| .NET fast / extended | 862 / 154 (123 Core + 31 Extended) |
| Adapter / Node / MCP / Bun | 8 / 45 / 12 / 23 |
| Adapter and Bun typechecks | Both pass |
| Test partition / fish contract / whitespace | Pass |

Historical owner/recovery and determined probes explicitly retain their prior
close-latch recipe by default. `--current-close` additionally runs those same
probes with this milestone's latch composition. This preserves historical
fixtures while enabling regression of the new scope arrangement. Default
admission enables the new composition; the standalone serialized controller
keeps its historical default. Closure records fresh historical independent
Python runs, current-composition checks and final serial aggregate results.

This establishes the bounded admitted-close guarantee. It does not establish
whole Python shutdown parity, queued mutating-handler protection, snapshot-fault
parity, atomic concurrent check entry, provider-specific shutdown, fairness,
arbitrary interleavings/fault classes, physical Git recovery or restart state.
Retained views use explicit joins; there is no authoritative post-scope registry
API. Ticket storage/scanning retains its existing linear/quadratic bounds.
All effects are synthetic. No live provider/account/credential action,
physical integration Git effect, force push, deletion, PR, merge or upload ran.

Local expanded execution supported SDK writes, Node child-process waits and
test sockets. No automatic approval review rejected an action. The exact tested
commit/tree is returned in the parent review checkpoint. Feature-branch
publication waits for the parent's independent review confirmation; the parent
handles PR and merge.
