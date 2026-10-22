# Raw save exception ledger retention — 2026-10-12

All **38 scenarios / 537 request observations** match unchanged pinned Python in
virtual execution and unedited emitted C#. Four new cases cover patch/empty
integration with clear/pending cancellation; only the first mocked save throws.
The previous 34 independent expected rows remain unchanged.

This local branch, `fix/raw-save-ledger`, starts from pending commit
`812bfe248e6753dcd86876519b168a8424f5eb4f`, tree
`21c7e700879ab9c266f119d977430cd2c812b5ab`. Its existing
`feat/raw-fault-cleanup` branch and checkout remain unchanged. The local worktree
is separate; no push, PR, publication, merge or alternate publication route ran.
Dependencies were copied from the pending checkout without installation.

## RED evidence

The [four baseline traces](https://github.com/kw4r3n/nyrwvy/blob/archive/legacy-examples-pre-cutover-20261019/examples/member-controller/fixtures/raw-save-baseline-red.json)
contain virtual, unedited real C# and independent Python observations. Every
baseline Nyrwvy case returns `err:raw save fault`, but its immediate inspection
loses the integrated HEAD. Retry repeats the full integration, including save.
Python retains HEAD before the exception and returns it on retry with no effects.
Patch retries repeat check/apply/commit; empty retries repeat validation/HEAD/save.
The running peer survives, no rollback occurs, and finish drains/releases once.

Continuation was intentionally absent from RED: admitting a continuation with an
unplanned held snapshot could hang the baseline. It was added to the final cases
after the retained-ledger assertions passed, and is rejected as integrated.

Before importing the oracle, HEAD and working `team.py`, `codex.py`, `runner.py`
bytes are verified against `4bab9e030744a0887c1ee13bdd8fc8db3b98334f`.
Actual Team methods, lock and `await_cleanup` run with synthetic external effects.
`Team.integrate` assigns `attempt.integrated` after HEAD, then calls synchronous
save. `await_cleanup` retrieves the inner result before raising pending caller
cancellation: the save error wins, and the ledger remains in memory.
The Python save stub requests cancellation before raising; real C# holds save
until the request is cancelled; virtual execution uses scripted delays.

## Minimal repair

The integration recipe uses existing ExceptionCatch around save only, after HEAD
and the candidate update. Both `saveRecorded` and `saveRecordedFault` receive
the captured candidate explicitly. Success, typed save Err and raw save fault
all return an Outcome containing that same candidate. The raw fault's original
message remains Err, so the outer request's cancellation check cannot replace it.
Existing outer handling still covers other integration faults and commit rollback.
The boundary is enabled only in the existing shielded raw-boundary composition.
No semantic/runtime core or host-side integration policy changed.

Every raw save case asserts immediate HEAD, original error, effect-free retry,
one HEAD/save, at most one check/apply/commit, no reverse effects, surviving peer,
continuation rejection, one worker cleanup and final drain/release ordering.

## Validation

Fresh ordinary graph authoring produces **89 functions / 1,942 nodes**.
Reopen, same-bundle replay and copied-directory replay preserve graph bytes,
emission and symbols. Real runtime compiles Generated.cs unchanged using installed
Roslyn/reference assemblies, with no restore or network. Pinned Python matches
all requests, normalized Member fields, ordered effects and payloads on both replay
paths. Virtual and real paths share the emitter/runtime; Python is independent.

Thirteen comparison sensitivity controls invoke the actual equality assertions.
Five new mutations cover immediate ledger loss, cancellation hiding the save
fault, retry committing again, spurious rollback and admitted continuation.
These are normalized-record mutations, not compiled graph mutants.
The generic exception fixture still passes four virtual cases and thirteen real
controls, including exception identity/stack/OCE, typed abort and context bypass,
restoration, fault/cancel precedence, drainage and resource release.

| Check | Result |
| --- | --- |
| Release solution build | Zero warnings/errors |
| .NET fast / extended | 862 / 154 (123 Core + 31 Extended) |
| npm adapter / Node / MCP | 8 / 40 / 12 |
| Bun / adapter and Bun typechecks | 23 / both pass |
| Partition / whitespace | Disjoint/exhaustive / pass |
| Default lifecycle / integration | 15 / 36 scenarios, virtual and real |
| Controller / independent Python | 38 / 537 request audits, exact |

Sandbox child-process waits and VSTest sockets required expanded local execution.
The first typecheck failed because the MCP workspace's nested dependencies had
not been copied; copying those installed local files resolved it. No dependency
or repository configuration changed.

Final artifact hashes and source proofs are in the maintained
[summary](https://github.com/kw4r3n/nyrwvy/blob/archive/legacy-examples-pre-cutover-20261019/examples/member-controller/fixtures/verification-summary.json).
Fresh authoring allocates new semantic IDs; saved-bundle replay preserves them.

## Limits and next bounded probe

This proves bounded serialized requests and in-memory retention after ordinary
raw save exceptions. It does not prove physical persistence after save failure,
restart recovery, concurrent admission/lock equivalence, whole external-parent
cancellation, all Python BaseException classes or non-cooperative host completion.
No provider, account, credentials, physical integration Git/index operation or
Library upload ran. The local development worktree and commit are ordinary Git
bookkeeping, separate from mocked integration effects.

The next bounded probe is **a raw HEAD-read exception after successful commit but
before ledger capture**, patch/empty × clear/pending cancellation with only the
first HEAD call throwing. Compare original error, absent ledger, subsequent retry
effects, no save on failure, peer survival and finish ordering against pinned
Python. This is a proposed probe, not an established RED gap.
