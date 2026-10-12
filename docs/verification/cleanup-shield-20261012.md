# Parent cancellation during finish and rollback — 2026-10-12

The pinned-main compiler cannot protect owned cleanup from parent cancellation.
The new generic [CancellationShield](../specs/cancellation-shield.md) expresses
the missing lexical boundary, while the graph continues to own finish/rollback
policy and cancellation/error precedence. Fourteen exact per-step comparisons
with unchanged pinned Python now pass. Existing task/observer behavior is preserved.

## Revisions and independent scope

Read-only remote checks confirmed `main` at
`37e512c682b32bbacdf40e9b47f26910f2b59c68`; PR383 was OPEN with no merge time at
`7607a93f4b072ef8b8ab818435175c609dd98f7e`. This branch starts at that main,
changes no existing Member integration recipe/fixture, and has no PR383 dependency.
The original user checkout remains at `90e495c75dd6915440ea22c96222aefcf04f723e`.
Work used isolated feature and detached baseline worktrees of a workspace-local
bare clone. No user checkout or rrawrlet source was edited.

The oracle is rrawrlet `4bab9e030744a0887c1ee13bdd8fc8db3b98334f`. Before import,
the harness checks HEAD and exact working bytes against pinned Git blobs for
`team.py` and `codex.py`. Their SHA-256 hashes are
`25e6fc65c0947eb74e12425c6c74664bd0c0adc3b80114223c4eabb651a8ccf4` and
`d2e5f97ac7c1053b841f0263364ff2a28b2e4eb312840ed7686ae6c1663b889a`.
It calls unchanged `Team.integrate`, `get`, `latest`, the actual lock and
`await_cleanup`; clean/Git/validation/agent-close/save are synthetic effects.

## Actual gap and chosen capability

`Team.integrate` awaits `await_cleanup(finish_integration())` after patch check.
The finish performs apply, commit, HEAD capture, in-memory ledger assignment and
save. Commit failure enters a second `await_cleanup(undo())`, running reverse-check
then reverse-apply before rethrowing. `await_cleanup` owns an independent asyncio
task, shields each wait, remembers repeated caller cancellation, retrieves the
task result (so its fault wins), then propagates pending cancellation.

Nyrwvy's preexisting TaskScope links to the parent token. TaskWait still joins
through cancellation but does not stop that token from reaching effects.
UseResource guarantees invocation of release, with the same ambient token.
PR382's OneShot observer recipe cancels only the observer's wait; it does not
detach owned cleanup from a parent token. Those mechanisms cannot express this
boundary by ordinary graph composition alone.

A separate unmodified checkout of main was built and used to author, reopen and
execute the direct-call baseline. Its **unedited emitted C# and virtual execution
agree**, and disagree with Python in exactly twelve cancellation scenarios.
The current compiler's unshielded branch reproduces the same observations. The
two cases without parent cancellation already matched Python. Negative evidence
is retained in the [sanitized summary](../../examples/cleanup-shield/fixtures/verification-summary.json).

The additive node runs a Unit callback with the ambient token masked and restores
the original token in `finally`. It introduces no task, resource, scheduler,
provider boundary, exception catcher or rrawrlet operation. The graph wraps the
whole finish tail and the rollback callback, waits for both to finish, keeps an
existing typed cleanup error ahead of pending cancellation, and maps successful
cleanup plus restored cancellation to its own `Err(cancelled)` data. Python
exceptions and graph Results are explicitly normalized; raw-fault capture is
not claimed. The graph captures HEAD before save for this comparison; it adds
no retry/ledger fix to PR383's existing example.

Snapshot JSON 15 / Nyason v11 gates the new node. Earlier snapshots keep their
minimum versions and byte identities. Validation, symbolic and ID recipes,
callback references/effects, signature migration, both codecs/IRs, lowering,
runtime selection, recipe discovery and viewer roles include the boundary.

## Per-step evidence

Every case checks ordered effect phases, cancellation at each phase completion,
pending parent state before releasing its gate, final integrated HEAD and result.
The coordinator requests cancellation twice while the selected phase is held;
it observes the parent still pending and only then releases cleanup. Virtual
execution uses scripted phase delays and fixed scheduler order; real C# and
Python use held gates. No wall-time race or real provider/Git operation is used.

| Path | Outcome after cancellation |
| --- | --- |
| Apply/commit/HEAD/save, nonempty patch | Finish continues; HEAD retained; then cancelled |
| HEAD/save, empty patch | Source HEAD retained; then cancelled |
| Commit fails; cancel during commit or either reverse phase | Both reverse effects complete; commit error wins |
| Reverse-apply fails with pending cancellation | Reverse error wins; restoration not established |
| HEAD fails with pending cancellation | HEAD error wins; ledger remains empty |
| Save fails with pending cancellation | Save error wins; captured HEAD retained |

Python save is synchronous. Its stub requests cancellation before return/throw,
when the outer caller is still pending; this does not establish asynchronous
interruption of disk save. Python 3.14 emitted five diagnostic notifications for
faults in shielded futures after caller cancellation. The harness records their
phase names separately while comparing the actual propagated faults; it never
edits Python methods or counts those notifications as additional task outcomes.

Six independent real-runtime controls establish:

- already-cancelled entry masks cancellation and restores the original token;
- a raw host fault passes through unchanged and restores the original token;
- context evaluates before masking and a generic `Result<Int64, Bool>.Err` is retained;
- a scope created inside the shield resists parent cancellation, then its own exit
  cancels and joins its worker through a held cleanup gate before release;
- the same drainage/release ordering holds when that worker raises a raw fault;
- an existing scope passed into a shield retains its original parent cancellation link.

Five comparison mutations are rejected: early settlement, missing rollback,
cancel hiding the commit error, lost ledger, and cancel hiding the save error.
They test comparison sensitivity, not compiled mutants.

## Validation and artifacts

| Check | Result |
| --- | --- |
| Fresh authoring and reopened virtual execution | 14 protected + 14 unshielded |
| Copied-directory replay | Same 28 observations; identical graph/source/symbols |
| Unedited generated C# | Same 28 observations + six generic controls |
| Unchanged Python | 14 exact per-step matches; 12 unshielded disagreements |
| Separate unchanged-main compiler | Same 12 actual disagreements |
| Release solution build | Zero warnings/errors |
| .NET fast / extended | 855 / 154 (123 Core + 31 Extended) |
| npm aggregate | 8 adapter + 38 Node/script + 12 MCP |
| Bun / adapter and Bun typechecks | 23 / both pass |
| Suite partition / fish checks / whitespace | Pass |

The default Node regression authors through ordinary proposals and commits,
compares normalized oracle expectations, copies/reopens/replays, compiles that
same emitted source, checks its hash and validates invalid callback/effect inputs.
Schema tests exercise both codecs' new-version round trip and rejection under a
lower declared version; exhaustive node-shape/runtime-use tests cover reachability
and intrinsic effect classification. No new CI workflow or workflow dispatch.

The local evidence graph has 24 functions / 260 nodes. Stable IDs differ on fresh authoring;
replay of this saved bundle preserves these hashes:

| Artifact | SHA-256 |
| --- | --- |
| Fixed snapshot | `361838941c9bf7163ba7c39432906b7231e394556c8e437feedb39a975d89c81` |
| Unedited emitted C# | `12dba88273835c00dcb1d47accba46a4169b5be14d1adbb2451294ef6f13c485` |
| Canonical symbol JSON | `b12111ef545b488aceeb9bb19a25120a7c182b4cfef20fe8617e2b456ae4a331` |
| Unchanged-main baseline snapshot | `3b3e0dfecf398a639d7155864a80077233331195db143d1a426d077bb7908f00` |
| Unchanged-main emitted C# | `09e01ff85ed9969d8c4085746e8787dfc1ed49ef16409435edf37ec711cb412a` |

Local bundle files contain saved graphs, generated C#, symbols, declarations,
cases, virtual/replay/runtime/oracle evidence and separate runtime harnesses.
The repository commits recipes, mock harnesses, normalized expected observations,
this report and a sanitized summary. Generated sources/binaries and temporary
private paths remain local. Commands are in the [example README](../../examples/cleanup-shield/README.md).
Installed tools were Nushell 0.116.1, Node 26.11.1, Python 3.14.7, Bun 1.4.3,
SDK 10.0.112, runtime/reference pack 10.0.12 and installed Roslyn (167 references).

Initial NuGet audit access failed in the restricted network; offline cached
restore with `NuGetAudit=false` passed. VSTest's loopback socket and Node synchronous
process waits required approved local execution. An initial Bun invocation lost
its `./` prefix through Nushell glob expansion; the maintained npm wrapper passed.
These failed attempts are not counted as successful checks. No dependencies were
installed or manifests changed.

## Limits and next candidate

This compares an already-eligible, sequential finish tail. It does not implement
the complete lifecycle/integration controller, concurrent lock parity, physical
Git/index rollback, filesystem persistence/restart, provider shutdown or real
process cleanup. A shield cannot terminate a non-cooperative effect. Existing
scope links remain authoritative and host faults remain uncaptured by default.
Cancellation is represented by graph data after the shield; Python exception
identity, task identity and arbitrary asynchronous interruption are not reproduced.

The next bounded candidate is composition of the maintained Member lifecycle
with eligible-candidate integration, using the generic observer and this shield:
prove settlement/capacity, integration eligibility and resource closure in one
serialized controller. Rebase or combine PR383's ledger behavior only after its
disposition is explicitly known. No real provider or Git safety claim follows
from that mock composition.
