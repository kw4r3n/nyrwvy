# Bounded concurrent request admission — 2026-10-13

**Six runs from five gated scripts match unchanged pinned Python, virtual
execution and unedited emitted C#.** The unchanged whole-request controller
reproduced a dispatch blocker. The repair is ordinary graph composition;
**no generic Mutex, new capability, core/runtime change or schema change** was
needed. The prior **51 scenarios / 771 request audits** are unchanged and pass.

The isolated local branch `feat/bounded-request-admission` starts at verified
main `5a68e8556a27ab61ae0fefcffae6787e06e790ea`, the merged serialized integration
milestone. The workspace is
`/home/kw4/Documents/Codex/2026-10-13/task/nyrwvy`. Ancestor instructions,
repository instruction/skill locations and previous milestone reports were
inspected. No repository AGENTS.md or skill instructions were present.
Nushell invokes the installed SDK and locally copied dependencies; restore uses
an empty local package source. No dependency versions changed.

## Source policy and baseline

The oracle checks HEAD and working `team.py`, `codex.py`, `runner.py` bytes against
`4bab9e030744a0887c1ee13bdd8fc8db3b98334f` before importing. The pinned source
checkout is clean. Actual Team.spawn/integrate/discard use its asyncio.Lock;
Team.close locks only the `closing=true` transition. Wait/stop are nonexclusive.
The six runs call actual methods and await_cleanup with synthetic external
effects. Request-local ContextVar attribution replaces the sequential harness's
shared phase/label flags; shielded child tasks inherit that request attribution.

The baseline authors the unchanged 89-function / 1,942-node controller. Its
Generated.cs bytes are unchanged when compiled. After A settles, the baseline
holds A's commit and submits stop(B), followed by the release gate. The whole
request fold cannot reach either later request until the commit barrier times
out. The existing catch then returns the timeout error and only afterward
dispatches stop. The independent Python run settles wait(B)/stop(B) before the
commit gate opens. This is a reproduced limitation of whole-request serialization,
not evidence that Python's lock serializes wait or the whole close drain.

## Composition and finite coverage

The new [authoring recipe](../../examples/member-concurrency/author.mjs) extends
the existing controller in a separate entry. An immutable owner holds scoped
state handoffs and request tickets. Mutation workers await predecessor state;
independent supervisors join them and publish their retained state. A queued
cancelled caller returns immediately without registration/effects. Its supervisor
later forwards predecessor state so subsequent mutations remain available.

Readers select the most recent completed state/snapshot cell without awaiting
the active mutation's whole request. Integration uses the inherited shield and
catch directly in its caller cancellation context. It captures the ledger before
the supervisor transfers ownership. Close publishes the closing state to its
successor before draining Members outside the ownership interval. The owner
joins all supervisors, then TaskScope joins all owned tasks, then UseResource
releases once.

The Python synthetic GitHost.release counts its actual invocation and checks
that every Team task is settled and every agent is closed at that point. The
oracle calls Team.close once. C# checks inactive providers at resource release;
the graph explicitly joins request supervisors and its owning TaskScope. Release
placement relative to subsequent independent inspection is not compared as a
cross-request total order.

| Script | Runs | Required observation |
| --- | ---: | --- |
| Capacity contention | 1 | One held Member; winner's workspace is gated; competing spawn waits, then rejects capacity with no workspace/registration effects |
| Queued spawn cancellation | 1 | Queued caller cancels before ownership; no registration/effects; later capacity rejection and successful finish prove continued chain progress |
| Duplicate integration | 2 | Clear first caller or first caller cancelled during held protected commit; exactly one check/apply/commit/HEAD/save; second returns retained HEAD without effects |
| Wait/stop during integration | 1 | Both B callers settle while A's commit remains held; integration remains unaffected |
| Close cutoff before drain | 1 | Close queues behind integration; closing transition follows retained HEAD; late spawn rejects while close is still held in drain |

All runs use at most two registered Members and three concurrent callers.
Real C#/Python use explicit gates; virtual execution uses deterministic delays
and provides supplementary scheduling evidence. C# AsyncLocal is attribution
only. Hosts own effects and barriers, not registry, capacity, ledger or ownership.

Comparison checks exact outcomes, nine-field final Member state, mutation order
within each request and required happens-before constraints. It allows arbitrary
ordering between independent caller replies. Ten sensitivity controls run the
actual comparison/assertions and reject loser effects, queued registration,
duplicate commit, lost ledger, serialized wait, late spawn admission, duplicate
release, reordered mutation phases, premature cancelled settlement and early
ownership transfer. These mutate normalized records, not compiled graphs.

## Evidence and validation

The [verified closure](../../examples/member-concurrency/fixtures/verified-closure.json)
contains baseline traces, source proofs, all six virtual/real/Python records,
artifact hashes, reopen/moved identities, compiler evidence, retained audit
counts and comparison controls. Fresh ordinary proposals are simulated,
reviewed and committed through the existing authoring tools. Reopen, same-path
replay and copied/moved replay preserve graph, emission, symbols and cases.
Roslyn compiles Generated.cs without edits or restore.

The final graph has **115 functions / 2,530 nodes**, adding **26 functions /
588 nodes** to the inherited graph. Existing controller authoring files and its
independent expectations remain byte-for-byte unchanged.

| Check | Result |
| --- | --- |
| Offline Release solution build | Zero warnings/errors |
| .NET fast / extended | 862 / 154 (123 Core + 31 Extended) |
| Adapter / Node / MCP | 8 / 41 / 12 |
| Bun / adapter and Bun typechecks | 23 / both pass |
| Partition / whitespace | Disjoint/exhaustive / pass |
| New admission runs | 6 virtual, real and independent Python; reopen and moved replay |
| Retained serialized regression | 51 scenarios / 771 actual pinned Python request audits; exact virtual and real comparison |

Aggregate suites run serially with Node test concurrency 1. Expanded local
execution supplies SDK restore/cache writes, local test sockets and Node child
waits that the sandbox does not support. Initial typecheck lacked the MCP
workspace's installed modules; copying those local modules resolved it. An
incorrect Bun path filter was corrected to four explicit `.ts` paths. No
automatic approval review rejected an action.

## Cost, limits and handoff

This is a bounded recipe and regression fixture, not a public Team service or
general queue. It retains immutable request tickets until scope exit and scans
their published snapshots, giving linear retained storage and quadratic total
scanning across long scripts. Supervisors add one owned task per request to
preserve prompt queued cancellation without losing ownership transfer. The
finite scripts do not establish exhaustive fairness, arbitrary host-fault
handoff behavior, whole-parent cancellation or restart durability.

No provider, credentials/account mutation, physical Git integration effect,
Library upload, push, PR or repository export ran. No publication retry or
alternate route was attempted. The milestone is local and ready for parent
review. Human publication handoff after review:

```nu
git -C /home/kw4/Documents/Codex/2026-10-13/task/nyrwvy push https://github.com/kw4r3n/nyrwvy.git HEAD:refs/heads/feat/bounded-request-admission
```

The next meaningful gap is external controller-parent cancellation across a
queued ownership handoff and a protected integration tail. It remains outside
this finite milestone, along with provider shutdown, physical Git recovery and
persistence; no further probe is queued.
