# Completed mutation state after owner cancellation — 2026-10-13

**Correction, 2026-10-14:** The inherited owner integration script could inspect
peer Member 2 before its cancelled task completed. The
[pre-join harness repair](owner-prejoin-observation-20261014.md) adds its explicit
join and reruns this eleven-case matrix under controlled provider schedules.
The historical fixture hashes remain unchanged. The state-recovery runtime
and its existing Python-policy divergences are unchanged by this repair.

The admission recipe now retains completed immutable mutation state after
parent cancellation closes its handoffs. In the held-commit acceptance case,
the retained Member has `integrated-head`, with exactly one commit, HEAD read
and save. The queued duplicate replies `err:cancelled` without mutation, every
owned worker drains, and the resource releases once. The active caller still
replies `err:cancelled` after its protected tail finishes.

This local milestone starts at `1af5ae38ea426dbe964147151f5cd7b17efae0e9` on
`probe/completed-mutation-recovery` in
`/home/kw4/Documents/Codex/2026-10-13/task-3/nyrwvy`. The source checkout is
preserved. Ancestor instructions, repository files and relevant `.agents/skills`
were inspected; none required an additional workflow for this ordinary recipe
change. Commands run through local Nushell. No core change, runtime change,
schema, capability, dependency, credential or unrelated-file edit was needed.

## Failing baseline and minimal recovery

The pinned report and closure identify the failure: the integration worker
already returns `Ok(State)` with the captured ledger. Its supervisor tries to
complete scoped OneShots after cancellation has closed them; failed completion
leaves the reader using the acquired, precommit snapshot. CancellationShield
does not reopen those cells. Owned task results, however, remain observable.

`admissionReadSnapshot` is the only changed subject function. It first reads the
existing snapshot/next cells, then performs a zero-time TaskWait poll for an
exclusive ticket. A completed `Some(Ok(State))` supplies the latest mutation
state; pending, cancelled and Err outcomes leave the current fold value intact.
Nonexclusive readers cannot supply recovery state because their results may be
older than an in-flight mutation. This uses the existing task and immutable
state composition. Handoff completion, cancellation propagation, dispatch,
request shielding, caller return behavior and generic drainage are unchanged.

The final owner graph has 120 functions / 2,604 nodes versus the baseline's
120 / 2,582: 22 additional nodes in one function, no additional function. The
baseline flag reproduces the pinned subject; legacy six-case fixtures remain
unchanged and exactly reproduce the earlier milestone.

| Held integration | Baseline retained ledger | Recovered ledger | commit / HEAD / save |
| --- | --- | --- | --- |
| Successful nonempty patch | Empty | integrated-head | 1 / 1 / 1 |
| Empty patch, held HEAD read | Empty | source-head | 0 / 1 / 1 |
| Raw save fault | Empty | integrated-head | 1 / 1 / 1 attempted |
| Raw HEAD fault | Empty | Empty | 1 / 1 attempted / 0 |
| Raw or typed commit failure | Empty | Empty | 1 attempted / 0 / 0; one rollback |

`prove-recovery.mjs` applies the same HEAD-retention acceptance assertion to
before and after evidence in virtual, real TaskCancel and external-root modes.
It requires the baseline assertion to fail, then requires recovery to pass. It
also compares exact request outcomes, request-local mutation traces, Member
effect order and release counts across the entire eleven-script before/after
matrix. Only the retained registry view changes. Successful completed spawn
state is also recovered, exposing the already-admitted second Member that the
baseline's closed handoff hid; queued admission remains cancelled.

The stale-reader control reads only the already-settled target while commit is
held, joins that reader before owner parking, and later verifies that its old
ledger cannot erase recovered HEAD. Initial inspection of both Members exposed
a virtual/real difference in peer cancellation timing. That failed comparison
was retained during development; targeting the settled Member makes the
ledger-specific control deterministic without normalizing away peer state.

## Evidence and scope

Eleven scripts cover the original six plus empty patch, raw save, raw HEAD,
typed commit and stale-reader controls. Each before/after graph runs once with
the virtual clock and twice in unedited generated C#, using real owner-task and
external-root cancellation. There are 22 real runs per graph. Real and Python
use explicit held effects and cancellation gates. The C# driver is owned and
joined; hosts own synthetic effects and barriers, not graph state or recovery.
All provider leases close once before the single outer release. Raw Member
settlement and outer release faults retain their precedence.

Actual Python remains pinned to
`4bab9e030744a0887c1ee13bdd8fc8db3b98334f` in the unchanged oracle checkout.
Before import the harness checks HEAD and exact working bytes of `team.py`,
`codex.py`, `runner.py` and `team_tools.py`. It invokes actual Team methods and
lock, Codex.close/_close and await_cleanup with synthetic external effects.
No Codex process is started. The original six Python records and source proofs
are checked unchanged before storing the expanded independent fixture.

Python retains integration HEAD and drains its mutating handlers, including
the queued duplicate. C# still cancels the queued duplicate. The first four
original owner scripts expose meaningful policy differences; recovery repairs
the lost completed-state symptom, while queue protection and determined Member
settlement remain open. The two shutdown scripts chiefly differ in caller
context and harness records, with drainage and raw cleanup-fault precedence
agreeing. The checker retains exact outcomes and actual observable registry
views; it does not classify all six as shutdown defects or invent a shared
terminal registry. Python's shielded-future release-fault diagnostic remains
recorded. All eleven complete records remain distinct from Python.

Fresh graph proposals are reviewed and committed through ordinary authoring
tools. Saved/reopened source and symbols match; same-path and copied-path
replays preserve graph, Generated.cs, symbols and cases. Installed Roslyn
compiles Generated.cs unchanged and offline. Virtual and real execution share
the C# emitter/runtime and are not independent interpreters; Python supplies
the independent oracle. Twelve comparison sensitivity controls reject altered
records, including loss of the newly retained ledger. These are record
mutations, not compiled graph mutants.

Committed evidence is in `examples/member-owner-cancellation/fixtures/`:
`nyrwvy-recovery-baseline.json`, `nyrwvy-recovered.json`,
`recovery-oracle-expected.json`, `recovery-baseline-red.json` and
`recovery-verified-closure.json`. Full local authoring, runtime, Python and
replay bundles and suite logs are preserved under `artifacts/completed-mutation/`
in this checkout. The example README lists reproduction commands.

| Check | Result |
| --- | --- |
| Offline Release build | Zero warnings/errors |
| .NET fast / extended | 862 / 154 (123 Core + 31 Extended) |
| Adapter / Node / MCP / Bun | 8 / 43 / 12 / 23 |
| Adapter and Bun typechecks / test partition | Both pass / disjoint and exhaustive |
| Serialized independent oracle | 51 scenarios / 771 request audits |
| Admission independent oracle | Six exact comparisons |
| Recovery before / after | Eleven virtual + 22 real each; eleven independent Python contracts |
| Reopen / same-path replay / copied replay | Source, symbols, graph and cases preserved |

Node initially passed 42 of 43 tests; its remaining CLI test failed because
the new clone lacked the MCP workspace's nested dependency directory. Copying
that existing local directory and rerunning only the affected CLI test passed.
Both logs remain in the closure; no dependency definition or lockfile changed,
and no install ran. The inherited serialized and admission oracles were also
rerun fresh against actual pinned Python, including explicit current-request
cancellation and retry/idempotence controls.

## Limits and handoff

This is in-memory recovery of completed owned results within the live scope,
not recovery of closed cells or persistent/restart state. It does not protect
Python-style queued mutating handlers or settle determined Members after
parent cancellation. There is no authoritative post-scope registry API or
claim about arbitrary faults/interleavings, provider shutdown, fairness or
physical Git recovery. Tickets remain until scope exit: linear storage and
quadratic total scanning across long scripts, with an added zero-time poll for
exclusive tickets. The bounded matrix has at most two registered Members and
three concurrent request callers.

Local offline execution needed expanded permissions for Node child-process
waits and test sockets; no automatic approval review rejected an action.
All jobs ran serially. No live provider, account, credentials, physical Git
integration effects, push, PR, upload or outward code export ran. The isolated
checkout, local branch and full evidence remain available for human handoff;
publication is reserved for that review.
