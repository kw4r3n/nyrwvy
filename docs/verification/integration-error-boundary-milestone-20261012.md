# Serialized integration error boundary milestone — 2026-10-12

**51 scenarios / 771 request audits** match unchanged pinned Python, virtual
execution and unedited emitted C#. The nine new scenarios already pass on the
unchanged saved graph from `fb078f8ba60f9320ccdef571fe5fa9cf2ff0412f`.
**Baseline pass is explicit: no new RED, repair or generic capability.** Existing
ExceptionCatch and CancellationShield composition remains byte-for-byte unchanged.
All prior **42 scenarios / 609 independent expectations** are preserved.

This is one completed milestone for the known bounded serialized integration
error boundary matrix. There is no next speculative probe queued. Parent review
awaits publication resolution; publication remains blocked.

## Scope and source derivation

The isolated local checkout is `task-15/nyrwvy`, branch
`verify/integration-error-matrix`, based on exact local `fb078f8`. Original
`verify/raw-head-read`, `fix/raw-save-ledger` and `feat/raw-fault-cleanup` branches
and checkouts remain unchanged. Exact pending `812bfe2` bundle/patch hashes are
rechecked in the [summary](https://github.com/kw4r3n/nyrwvy/blob/archive/legacy-examples-pre-cutover-20261019/examples/member-controller/fixtures/verification-summary.json).
No push, PR, export, merge, publication or alternate publication route ran.

Ancestor/repository instructions and repository `.agents`/skill files were
inspected. No Nyrwvy repository-specific instruction or skill file was present.
Shell work uses Nushell through the Bash-only execution transport after shell
discovery. Dependencies were copied from installed local files; .NET restore
used the installed cache and an empty local source. No installation from a
network source or dependency-version change occurred.

Before import, the oracle checks HEAD and working `team.py`, `codex.py` and
`runner.py` against `4bab9e030744a0887c1ee13bdd8fc8db3b98334f`. Actual Team
methods, lock and `await_cleanup` execute with synthetic external effects.
Pinned `team.py` lines 1058–1096 place check before `await_cleanup`, apply inside
the shielded finish operation but before commit's `except BaseException`, both
reverse operations inside shielded undo, HEAD after commit handling, and ledger
assignment before synchronous save. `codex.py` lines 21–33 retrieve the inner
result before propagating pending caller cancellation.

Consequences established by the nine new cases:

- Raw apply after successful check cannot enter commit rollback. Its original
  error wins pending cancellation; no commit, HEAD, save or reverse effect occurs
  on the failed request. The completed/snapshotted/checked Member has no ledger.
- Raw check leaves the same empty ledger and cannot reach apply. Check is outside
  the shield. One bounded throwing host catches caller cancellation while waiting
  for the explicit release, then throws its original error. A second cooperative
  host exits on cancellation before the planned throw; no raw fault is invented.
- After a typed Nyrwvy commit Err, a raw reverse-check/reverse-apply error replaces
  the commit error and pending cancellation. Reverse-check failure prevents
  reverse-apply. Python represents these host failures as ordinary RuntimeError;
  typed Err is the Nyrwvy host representation, not a separate Python error class.
- Each new fault occurs only on the first selected effect. Immediate inspection
  confirms the empty ledger, a held peer remains running, and a full retry captures
  HEAD and saves exactly once. The third request returns that ledger without new
  effects; continuation is rejected as integrated. Worker cleanup and owner
  drain/release/acknowledgement ordering remain intact.

Real C# gates and Python events hold the selected boundary until two cancellation
requests are issued. The protected hosts assert cancellation remains masked;
the raw check hosts demonstrate it is visible outside the shield. Virtual cases
use deterministic scripted delays. The cooperative virtual case uses the
normalized cancellation message; real C# throws OperationCanceledException and
Python raises CancelledError. Exception identity is separately covered by the
inherited generic exception fixture.

## Finite coverage matrix

Here “covered” means observed in the cited finite fixtures, not all possible
exception types or physical side effects. Empty patch never reaches check,
apply, commit or either reverse operation.

| Boundary / trigger | Covered cases and cancellation | Ledger, failure precedence and retry |
| --- | --- | --- |
| Eligibility and pre-mutation validation | Existing lifecycle/controller and 36 default integration scenarios: running, status, lineage, overlap and typed errors at clean/validate/ancestor/path/close/patch phases | Rejection/error before integration; no new raw-phase enumeration claim |
| Check raw throw | New `raw-check-clear`, `raw-check-cancel` | Empty ledger; bounded throwing host error wins pending cancellation; retry runs full integration |
| Check cooperative cancellation before planned throw | New `raw-check-cooperative-cancel` | Cancellation wins before fault exists; empty ledger; full retry |
| Apply raw throw after successful check | New `raw-apply-clear`, `raw-apply-cancel` | Original error wins; no commit/rollback; empty ledger; full retry |
| Commit typed error, reverse operations succeed | Existing `retry-commit`, `rollback-cancel-*`; default `commit-rollback` and `retry-after-rollback` | Commit error wins pending cancellation after undo; empty ledger; successful retry |
| Commit raw error, reverse operations succeed | Existing `raw-host-fault`, `raw-cancel-*` | Original raw commit error wins after undo; repeated persistent fault remains available to serialized retry |
| Typed commit error, typed reverse error | Existing `rollback-error-reverse-*` with pending cancellation; default `rollback-*-fails` with clear cancellation | Reverse error replaces commit error; failed reverse-check stops undo; empty ledger |
| Typed commit error, raw reverse error | Four new `typed-commit-raw-reverse-{check,apply}-{clear,cancel}` | Reverse fault wins commit error/cancellation; empty ledger; one-shot faults allow full successful retry and effect-free third request |
| Raw commit error, typed or raw reverse error | Eight inherited `raw-rollback-{typed,raw}-reverse-{check,apply}-{clear,cancel}` | Reverse failure wins; failed reverse-check stops undo; persistent retry retains fault and empty ledger |
| HEAD raw throw after successful commit / empty patch | Four inherited `raw-head-{patch,empty}-{clear,cancel}` | Empty ledger; original fault wins; no rollback/save; full retry, then effect-free third request |
| Save typed / raw error after capture | Existing patch/empty typed save cases and four `raw-save-{patch,empty}-{clear,cancel}` | Captured HEAD survives; save error wins cancellation; retry uses ledger without effects |
| Successful protected tail with pending cancellation | Existing patch apply/commit/HEAD/save and empty HEAD/save cancellation cases | Completion captures ledger before caller cancellation; retry uses ledger |
| Peer survival, subsequent serialized requests, finish | All new cases plus inherited fault cases; separate finish cancellation scenario | Peer survives integration fault; cleanup once; owner drains before release, releases once, then acknowledges |

## Verification and evidence

The [baseline closure](https://github.com/kw4r3n/nyrwvy/blob/archive/legacy-examples-pre-cutover-20261019/examples/member-controller/fixtures/integration-boundary-verified-closure.json)
contains the nine virtual, real and independent Python per-request traces, source
proofs and baseline artifact hashes. Baseline graph, Generated.cs and symbols
are exactly the previous `task-14/head-fresh` bytes. Fresh ordinary graph proposals
still produce **89 functions / 1,942 nodes**. Reopen, same-bundle replay and copied
directory replay preserve snapshot, emission, symbols and case bytes; real runtime
compiles Generated.cs unchanged with installed Roslyn/reference assemblies.

All baseline/fresh/copied paths match the independent expected records. Virtual
and real paths share the emitter/runtime; Python supplies independent language
evidence. The **35 sensitivity controls** invoke actual equality comparisons and
reject normalized-record mutations, including lost ledger, cancellation hiding
the fault, retry skipping effects, spurious apply rollback, continued undo after
reverse-check failure and an invented fault from cooperative check cancellation.
These are comparison controls, not compiled graph mutants.

| Aggregate check | Result |
| --- | --- |
| Offline Release solution build | Zero warnings/errors |
| .NET fast / extended | 862 / 154 (123 Core + 31 Extended) |
| npm adapter / Node / MCP | 8 / 40 / 12 |
| Bun / adapter and Bun typechecks | 23 / both pass |
| Test suite partition / whitespace | Disjoint/exhaustive / pass |
| Default lifecycle / integration | 15 / 36 scenarios, virtual and real, through Node aggregate |
| Generic exception fixture | Four virtual cases / thirteen real controls, through Node aggregate |
| Controller / independent Python | 51 / 771 request audits, exact |

Sandboxed Node synchronous child-process waits reported EPERM despite successful
child output. Authorized expanded local execution completed runtime and aggregate
checks, including VSTest sockets. No approval review rejected an action.

## Explicit exclusions and remaining consequential gaps

No known mismatch remains in the serialized boundary matrix above. These limits
remain consequential and explicitly outside this milestone:

| Excluded work / remaining gap | Why current evidence does not establish it |
| --- | --- |
| Physical Git/index rollback or partial host mutation | All integration effects are synthetic; error propagation does not prove filesystem recovery |
| Save durability or restart persistence | Ledger retention is in memory; no restart/recovery expansion is authorized |
| Concurrent admission and lock equivalence | Requests are serialized; no unbounded concurrency expansion |
| External controller-parent cancellation | Request and finish cancellation are covered; whole-parent lifecycle is not |
| All BaseException classes and non-cooperative host completion | Ordinary throwing hosts and one bounded cancellation-resistant check are sampled; no forced-completion claim |
| Every raw pre-validation host phase, release/provider shutdown faults | Typed early errors and generic catch semantics are covered, but these are not exhaustively sampled integration tail boundaries |
| Provider/account/credential effects or publication | None executed; publication remains blocked |

The completed milestone is ready for parent review. Further local probes should
require a concrete consequential gap or observed mismatch rather than extending
this matrix indefinitely.
