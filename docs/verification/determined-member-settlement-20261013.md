# Determined Member settlement — 2026-10-13

The admission composition now preserves a determined Member's cleanup and
snapshot under owner cancellation. It captures status immediately after the
existing cancellable provider scope returns, then runs a small settlement tail
under the existing CancellationShield. Provider work remains promptly
cancellable. The owner still reports `err:owner cancelled` only after drainage
and outer release. Raw snapshot and outer release faults retain precedence.

The isolated branch is `probe/determined-member-settlement` in
`/home/kw4/Documents/Codex/2026-10-13/task-5/nyrwvy`, starting exactly at merged
main `b09bb5675354439b61a9adcc9b3431428aef5819` (PR388). Its tracked starting
content equals prior tested `7a2dcc33dc6a3c40fce4ecf0cf605a199cda3834` in task-3;
that checkout is preserved. Home/ancestor/repository instructions, relevant
`.agents/skills`, the owner cancellation and completed-state recovery reports,
and repository verification guidance were inspected. No applicable additional
skill or repository AGENTS.md was found. Shell commands use local Nushell.

## Actual source boundary

Unchanged Python is pinned to `4bab9e030744a0887c1ee13bdd8fc8db3b98334f` in
`/home/kw4/Documents/Codex/2026-10-12/task-12/rrawrlet-oracle`. Before import,
the harness checks HEAD and exact bytes of team.py, codex.py, runner.py and
team_tools.py. Source hashes are retained in the independent fixture/closure.

`Team.execute` sets the attempt status after provider return, before agent
cleanup in its finally block. Pending cancellation at provider return produces
stopped, even if the provider returns completed. A completed nonleader attempt
snapshots after agent cleanup. `Team.stop` cancels only an unfinished task whose
attempt remains running and whose cancellation count is zero; otherwise it
joins without another cancellation. `Team.close` sets closing under its lock
before calling actual stop/join. The independent harness calls those actual
methods and `await_cleanup`; it does not copy their policies. Synthetic effects
replace provider, Git, checks and save; no provider process starts. The owner
uses Team.close through await_cleanup and repeats cancellation while cleanup
is held. The runner itself and Codex handler shutdown are not executed in this
new ten-case oracle; the retained eleven-case oracle separately exercises the
actual Codex.close handler contract.

## Baseline and composition

The unchanged graph chose status after memberClose, so cancellation while
cleanup was held downgraded an already successful provider outcome to stopped.
After determination, its snapshot still received the linked cancelled token.
The baseline variant selects the original graph recipe explicitly. Probe-only
audits query actual graph cancellation before and after cleanup/snapshot
effects; hosts do not supply those token observations. The same audits and
repeated TaskCancel calls appear in both variants. A synthetic snapshot can
finish despite an exposed token, letting the audit retain that exposure. The
historical eleven-script fixture separately reproduces its original
cooperative snapshot-cancellation fault unchanged.

Five baseline cases fail the same protected-settlement acceptance assertions
in virtual, real owner-task and external-root modes: held successful/typed-fault
snapshot, held successful/failed cleanup, and snapshot plus release fault.
These are fifteen reproducible RED assertions. All ten protected scripts pass
in all three modes. The raw snapshot case verifies fault precedence; it need
not fail an unrelated baseline token assertion after its raw exception exits.

The subject adds one ordinary `memberSettlement` function. Its caller computes
status in the original worker context. The shielded callback publishes that
captured status, calls memberClose once, and either finishes snapshot/checks or
returns the captured noncompleted Final. CancellationShield restores the
original token on both success and fault. The provider TaskScope is outside
the shield, and no task ownership or cancellation linkage changes. The
instrumented probe grows from 120 functions / 2,675 nodes to 121 / 2,696;
the subject adds 21 nodes and one function. No core, runtime, schema,
capability, dependency, admission/recovery-policy or generated-source edits occur.

Default admission and owner authoring enable this composition. The standalone
serialized author retains its historical default. The older owner runner
explicitly passes `protectDetermined:false` so its six and eleven historical
fixtures remain reproducible. Retaining those historical records does not
claim that their original settlement fault describes the new default.

## Bounded matrix and proof

| Held/order boundary | Protected graph result | Actual Python |
| --- | --- | --- |
| Completed snapshot | Completed commit1 retained; token clear throughout hold | Completed commit1 retained; actual stop joins |
| Raw snapshot fault | Raw fault wins owner cancellation; resource still releases once | execute records failed snapshot; owner cancellation propagates after close |
| Typed snapshot fault | Failed Final retained | Synthetic Git failure records failed snapshot |
| Completed cleanup | Completed outcome survives cancellation; snapshot runs once | Determined completed outcome survives cleanup/stop |
| Failed cleanup | Failed outcome retained; no snapshot | Determined failed outcome survives cleanup/stop |
| Undetermined provider | Token cancels before settlement release; stopped retained | Actual stop cancels provider; stopped retained |
| Provider returns completed after cancellation | Stopped retained | Pending task cancellation also yields stopped |
| Before worker/provider entry | Stopped; no provider lease created | Agent can already exist at start; actual cleanup closes it once |
| Settlement before owner cancellation | Completed result remains recoverable | Completed result remains inspectable |
| Raw release fault | Raw release error wins owner cancellation | Actual cleanup release fault wins cancellation |

Each script registers one Member. Real runs use explicit entry/provider/cleanup/
snapshot/owner barriers. The provider control witnesses cancellation before
the held-release gate. The external-root driver is owned and joined, injecting
root cancellation before the graph repeats TaskCancel. Python uses independent
asyncio events. Virtual scripted delays supplement those barriers. All opened
provider leases close once; entry cancellation creates none in the graph.
All owned graph/Member/driver tasks drain before one resource-release attempt.

`prove.mjs` compares exact virtual/task/root graph outcomes, token replies,
retained views and effect order. It also compares the explicit bounded Python
status/commit contract where applicable, keeping each complete record separate.
Eight record-mutation controls reject exposed snapshot cancellation, cleanup
downgrade, cancelled provider completion, duplicate cleanup, swallowed raw
snapshot/release faults, early release and hidden prior settlement. The prior
settlement control explicitly joins the Member before parking/cancelling its
owner, in both C# and actual Python. These are comparison controls,
not compiled graph mutants. The maintained Node regression authors both
variants afresh, runs real C#, checks committed observations and independent
Python fixtures, and verifies same/copied-path replay.

Ordinary create/review/commit tools author the graphs. Saved/reopened emission
and symbols match. Same-path and copied-path replay preserve graph/source/
symbols/cases. Installed Roslyn compiles Generated.cs unedited with installed
reference packs and no restore. Virtual and real share the C# emitter/runtime;
the Python oracle supplies independent language evidence.

## Retained evidence and limits

Fresh retained runs verify the serialized 51 scenarios / 771 request audits,
six admission comparisons (with the new default settlement composition), and
eleven historical recovery scripts (with their explicit historical switch).
Independent queued-request cancellation, protected mutation tails, raw-fault
precedence and recovery of completed immutable State remain covered. Expanded
local execution supports installed SDK writes, Node child waits and test
sockets. No automatic approval review rejected an action. Aggregate suites run
serially; final counts, log hashes and source/bundle hashes are in the
[verified closure](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/member-determined-settlement/fixtures/verified-closure.json).

| Serial aggregate | Result |
| --- | --- |
| Release solution build, no restore | Zero warnings/errors |
| .NET fast / extended | 862 / 154 (123 Core + 31 Extended) |
| Adapter / Node / MCP / Bun | 8 / 44 / 12 / 23 |
| Adapter and Bun typechecks | Both pass |
| Suite partition / fish contract / whitespace | Pass |

Final review strengthened the prior-settlement control with an explicit join.
The bounded before/after matrix, Python oracle, both replays and focused Node
regression were rerun after that harness refinement; the implementation was
unchanged. Final logs preserve both the full aggregate and focused result.

Committed before/after/Python/proof fixtures are in
`examples/member-determined-settlement/fixtures/`; full local transcripts,
runtime bundles, replay identities and suite logs are under
`artifacts/determined-settlement/`. Reproduction commands are in the
[example README](https://github.com/kw4r3n/nyrwvy/blob/bca45b30053b68abbfd071480e7c062735f410e6/examples/member-determined-settlement/README.md).

This repairs the bounded determined token/cleanup gap. It does not establish
whole Python shutdown equivalence. Python converts snapshot errors to failed
attempt records; raw C# snapshot errors propagate. Python close suppresses later
checks, while cancelled graph closing handoffs remain closed and can leave
checks runnable. The graph exposes no authoritative post-scope registry; retained
views here follow explicit task joins. Python queued mutating handlers still
drain, while graph queued callers cancel independently. That broader policy is
intentionally outside this milestone. Tickets remain until scope exit, with
linear storage and quadratic scanning across long scripts. Arbitrary fault
classes/interleavings, unbounded fairness, provider-specific shutdown, physical
Git recovery and restart persistence are unproven.

The next bounded actionable question is whether close intent/check suppression
can be composed without relying on a cancelled OneShot. Queue protection is a
separate subsequent decision. No generic cancellation change is proposed.
All work remains local: no live provider/account/credentials action, physical
integration Git effect, push, PR, upload or repository export. Publication is
reserved for the parent task.
