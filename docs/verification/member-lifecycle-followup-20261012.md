# Offline Member lifecycle: frozen no-core follow-ups

The later [generic task observer](../../examples/task-wait-observer/README.md)
extracts the cancellation composition into reusable U/E graph helpers and adds
maintained regression coverage. It preserves this frozen evidence and direct
TaskWait semantics; it does not broaden the tagged provider-effect probe.

The bounded observer composition and tagged provider-effect contract pass their
frozen checks without changing Nyrwvy runtime/core code. These are **semantic
adaptations**, not fixes to direct `TaskWait` cancellation or general raw host
exception handling. They use Nyrwvy to probe a rrawrlet workload, not to integrate
Nyrwvy into rrawrlet.

Nyrwvy base and verified publication-time remote main:
`ec6f3c4eb18c7c1e5385ce19a92fb93b06cac851`.
Actual Python Team oracle:
`4bab9e030744a0887c1ee13bdd8fc8db3b98334f`.
Its `team.py` SHA-256 remains
`25e6fc65c0947eb74e12425c6c74664bd0c0adc3b80114223c4eabb651a8ccf4`.

## Exact matrix

| Validation | Baseline | Adapted follow-up |
| --- | --- | --- |
| Virtual assertion passes | 15/15 | 16/16 |
| Reopened replay | 15/15 | 16/16 |
| Moved-directory replay | 15/15 | 16/16 |
| Unedited emitted C# assertion passes | 15/15 | 16/16 |
| Actual Team matches | 13 | 13 |
| Demonstrated/intentional divergences | 2 | 3 |
| Negative comparison controls rejected | 7/7 | 5/5 |
| Runtime/core/dependency changes | None | None |

Assertion passes include fixtures whose required result is a mismatch or scope
fault. They do not imply 15 or 16 Team matches. The original caller-cancellation
and raw-provider-fault divergences were rechecked with their unchanged saved
baseline graph, fixtures and C# projection; both remain controls.

The follow-up positive cases are any/all, finite/zero timeout, independent caller
cancellation, cancel-first, complete-first, publication-before-cancellation,
snapshot-gated timeout, tagged unexpected/expected provider errors, owner-scope
exit and external parent-token cancellation. All real cases measured two
overlapping providers and one global resource close; both provider leases close
once. Registered-check/snapshot/lineage/discard behavior remains covered by the
baseline rather than being broadened here.

## Caller cancellation composition

The controller starts a settlement monitor in the same owning scope as its
Members. The monitor uses actual Member task handles and `TaskWait`, then
publishes their ordered snapshots to a scope-owned `OneShot`. A separate caller
uses `OneShotWait` and checks `CancellationRequested` before returning its
Result. The cell does not contain task handles. The observer's returned payload
is tested independently from fresh task polling, including a gated snapshot and
zero timeout; provider completion cannot masquerade as task settlement.

In `caller-cancel`, the observer returns `Err("caller cancelled")` while the
monitor and both Members are still running. Occupancy remains two and neither
Member token is cancelled. The controller later stops/joins Members and joins
the monitor. In the raw-fault paths the controller observes monitor-or-caller
using existing `TaskWait(any)` with a common Result type. This avoids leaving a
caller parked indefinitely behind a monitor that threw without publishing.

The cancellation tests exercise three forced partial orders, not arbitrary race
interleavings: cancellation before release/completion, caller completion before
cancellation, and publication/read before cancellation while the caller's return
is gated. The last case returns a cancelled Result despite `OneShotWait` already
observing Some. The oracle gates the corresponding caller wrapper around actual
`Team.wait`; it does not replace Team policy. Direct `TaskWait` still ignores its
waiting caller's token. The adaptation costs one monitor and one cell per bounded
wait; no unbounded-scale or concurrent-admission claim is made.

## Explicit provider-effect contract

The `TaggedProvider.run` effect returns a product with `kind` and `cause`.
Expected errors and unexpected ordinary exceptions **inside provider run** are
distinct data. Graph code chooses failed status and retains cause evidence.
The typed unexpected-error case matches actual Team's failed Member and its
error message; the held healthy peer stays uncancelled and then completes.
An expected provider error whose text is `deadline` still becomes failed.

This explicitly changes the host contract; it is not a generic graph catcher.
The narrow translation does not surround effect setup, cleanup or graph/runtime
execution. Cancellation with the currently requested matching provider token
returns a separate cooperative cancellation Result. Other cancellation is
re-thrown, not tagged as an unexpected provider error. `UseResource` owns the
provider lease and runs its release before task settlement even on a throw.
Workspaces are not worker-scoped resources and are never implicitly removed.

Three intentional controls remain different from Team:

| Raw control | Nyrwvy adaptation | Actual Team |
| --- | --- | --- |
| Provider-lease cleanup throws | Scope failure, no normal final report | Failed Member with cleanup evidence |
| Exception outside translated provider run | Scope failure | Isolated failed Member |
| Foreign/unrequested cancellation exception | Scope failure | `CancelledError` yields stopped Member |

These controls verify that cancellation and cleanup faults were not implicitly
swallowed. Parent shutdown cancels and joins Members, observer and monitor before
resource close. The real external-parent-token fixture verifies the linked
tokens; the graph returns an explicit global-cancellation Err. Global oracle
comparisons cover Member records and cleanup: `Team.close` itself returns its
shutdown summary, not the graph's Result representation.

## Frozen artifacts and reproducibility

The standalone follow-up contains 74 functions / 1,585 nodes: the original
50 functions / 955 nodes plus 24 adaptation functions / 630 nodes. All functions
were created/reviewed/committed through ordinary graph tools. Both graph bundles
were closed/reopened and re-emitted with identical source and symbol identities.
Runtime checks compile unedited emitted source with installed Roslyn/reference
assemblies, with no restore, installation or network dependency.

| SHA-256 artifact | Baseline | Follow-up |
| --- | --- | --- |
| Saved graph | `427f2e02169e337fa153b960cce140497723878887c37d9be8c7e058a115b1ef` | `beb3bfab4a8437188798e6630a11dba7668a70f3cdf96687078fa9fe94c4a704` |
| Emitted C# | `aaffa604f59219f371bbf25627914e07e950bbf74c360be6f6bad586671b7850` | `7b1006d433dbc1c482ac4dcf49fe57487f0979abdd841471b7102cc9faf9b14d` |
| Symbol manifest | `bbf8c3011395be74f1cfc6cf5da3c5aab910906f26261408cf51de4857b715b4` | `0a6903f1d9536b86ed4a86e74ff46256b8648a734bbe8201958e77c226e52ded` |
| Synthetic arguments | `c24b07ff9bc86316542b3f902dedfe06dc0410bfc16a8957ab39b09775f0a31d` | `c77beea5ac1dee20caec2ff293c63456343fe2f779daf991905ef5840ee49e14` |

Tested with Node 26.11.1, Python 3.14.7, Nushell 0.116.1, .NET SDK 10.0.112,
runtime/reference pack 10.0.12 and Roslyn 5.0.0-dev. Saved graphs and argument
fixtures contain synthetic program definitions/data only. Source hashes and
sanitized per-case comparison classifications are in each committed
`fixtures/verification-summary.json`. No credentials, host paths, private runtime
logs, generated C# or binaries are included in Git. The materializer regenerates
the exact projections from those fixtures.

Argument fixtures are committed as compact JSON with identical parsed input
trees. The table records the observed pretty-JSON argument hashes; committed
byte hashes are also recorded in `verification-summary.json`. Graph and emitted
source identities are unchanged by this packaging choice.

Reproduction commands are in the [follow-up README](https://github.com/kw4r3n/nyrwvy/blob/archive/legacy-examples-pre-cutover-20261019/examples/member-lifecycle-followup/README.md).
The initial virtual peer-release fixture incorrectly modeled a peer as waiting
only for cancellation while the script wanted completion; its synthetic delay
fixture was corrected and real release barriers verified. That was a fixture
error, not a missing language primitive. The final scope is frozen at 16 cases.
The repository's full test suite and manual CI were not run. Real Git security,
integration/rollback, authentication/network providers, catalogs, budgets,
conversation reuse, UI and unbounded scaling remain outside this probe.
