# Offline Member integration composition

Historical 28-case record. The [save-failure follow-up](member-integration-save-failure-20261012.md)
extends this matrix to 36 scenarios / 46 per-request observations and closes the
ledger gap excluded below, using the existing graph operations.

Existing Nyrwvy graph operations express a bounded `Team.integrate` policy slice:
28 normalized Python matches, 28 virtual/reopened/moved/C# passes per stage,
12 functions and 231 nodes. No missing language capability was found in this
slice, and no core feature is proposed. This is evidence for implementing a
rrawrlet workload **using Nyrwvy**, not integration of Nyrwvy into rrawrlet.

## Selection and scope

PR380's [follow-up report](member-lifecycle-followup-20261012.md) excludes Git
integration and rollback. The [roadmap](../plans/post-m2-roadmap.md#current-recommendation)
requires selecting a concrete task before expanding the language. The pinned
Python `Team.integrate` supplied one: validate latest/settled/completed/snapshotted
state, reject overlapping paths, check/apply/commit a patch, and reverse it if
commit fails. The scope and source evidence were shared before implementation.
The reviewer's generic TaskWait cancellation recommendation remains a separate
confirmed gap; this task chose to prove the next policy composition without
changing that contract. The independent late-source-error probe is not included.

Verified base: PR380 merge `44835914c6f10d845facbe872d8be660c5fd5e53`, tree
`2f5254910f6e4fb19f097d4f66557eb77bde4692`. Python oracle:
`4bab9e030744a0887c1ee13bdd8fc8db3b98334f`; unchanged `team.py` SHA-256:
`25e6fc65c0947eb74e12425c6c74664bd0c0adc3b80114223c4eabb651a8ccf4`.
The oracle verifies the working source bytes against the pinned Git blob.

The graph consumes already-observed candidate facts, including task settlement.
It does not obtain those facts from live Nyrwvy handles. The oracle independently
constructs real held/settled asyncio tasks and uses unchanged `integrate`, `get`,
`latest`, and the actual lock. Clean/Git/validation/provider-close/save effects
are replaced by deterministic stubs; no actual Git mutation or provider occurs.

## Behavior and matrix

| Verification | Passes |
| --- | ---: |
| Saved graph virtual execution | 28/28 |
| Reopened replay | 28/28 |
| Moved-directory replay | 28/28 |
| Unedited emitted C# | 28/28 |
| Unchanged Team normalized comparison | 28/28 |
| Negative comparison controls rejected | 6/6 |
| .NET fast / extended aggregates | 854 / 154 |
| npm adapter / Node / MCP aggregates | 8 / 36 / 12 |
| Adapter / Bun typecheck targets | Both pass |

Solution Release build passed with zero warnings/errors. The extended aggregate
is 123 Core cases plus 31 Extended cases. No manual CI workflow was dispatched.
Bun runtime tests were not run; its typecheck target passed. Aggregate checks
were performed after the confirmed merge and apply to unchanged repository
runtime/tool sources; the final example was separately reverified after its
harness corrections.

The 28 frozen cases cover successful disjoint paths, repeat-call idempotence,
already-integrated and superseded-integrated precedence, discarded/running/failed/
stopped/unsnapshotted state, overlapping paths, duplicate/empty path sets, an
empty patch, eleven individual effect failures, failed commit with successful
rollback, two rollback failures, and retry after successful rollback.

Every case checks exact effect order, exact patch/save payloads, the returned
Result and every candidate field. The Python comparison checks ordered effects,
normalized Result and integration ledger. Guard rejection performs no effects;
already-integrated calls perform none after eligibility checks. Empty patches
skip check/apply/commit but still capture HEAD and save. A failed commit runs
reverse-check before reverse-apply; successful rollback retains the original
commit error. A failed rollback reports that phase and makes no restoration
claim. The failed candidate retains its old integration ledger; successful
requests capture HEAD, pass it to save and update the graph record. Repeat
success does not reapply; the retry case exercises a failed commit followed by
a fresh successful attempt, not automatic retry policy.

The six comparison controls falsify idempotence, settlement gating, overlap
rejection, rollback ordering, ledger preservation and rollback-error precedence.
They test observation comparison sensitivity, not mutants compiled into the
runtime or random race coverage.

## Artifacts and reproduction

[Example commands](../../examples/member-integration/README.md#reproduce) support
fresh ordinary authoring or exact frozen materialization. All functions were
created/reviewed/committed through ordinary tools; graph JSON was never authored
or edited directly. Reopening preserves symbol identities and re-emits identical
source. Execution leaves the saved graph unchanged.

| Frozen artifact | SHA-256 |
| --- | --- |
| Saved graph | `e4e93ef1ebcdbf02a0bb96a08f646a8d22a1feaca25fa686adef20404b7d63d2` |
| Emitted C# | `ff30a1c106c4d035343bf3532b4d8367ee9c49e5d1d1cb2da339168466c1aa77` |
| Symbol manifest | `d01cde86a8af90104fe4b727a891a103ea29b8ba977cf3b466f6b8597320f2c1` |
| Pretty argument cases | `bb5f6431891ebb07bd0f638e48a80b9b1db79127764f774b4480463ad1ca37d4` |

The committed cases use compact JSON with identical parsed inputs. Their byte
hash is recorded in `fixtures/verification-summary.json`. Saved graphs and
cases contain synthetic program definitions/data only. Generated C#, binaries,
private raw logs, machine paths and credentials are excluded from Git.
Validation used Node 26.11.1, Python 3.14.7, Nushell 0.116.1, SDK 10.0.112,
.NET runtime/reference pack 10.0.12 and Roslyn 5.0.0-dev. The local build restored
only from installed package cache; generated-runtime compilation used installed
assemblies with no restore or network.

Initial harness failures involved fake-capability response/case exclusivity,
flat versus nested container encodings, output/argument decoding and a runner
class name. They were corrected without changing language/runtime behavior.
The final decoder uses parameter/field names, so newly allocated IDs cannot
silently change positional decoding. Dependency copies replaced symlinks after
aggregate checks exposed package resolution and real-path identity failures.
These unsuccessful attempts are not counted as passes.

## Exact limits

This proves sequential integration policy over supplied facts and explicit typed
effect failures. It does not compose lifecycle admission/polling with integration,
prove concurrent lock equivalence, or implement real Git command/byte decoding,
path security, hooks, index restoration, persistence, credentials or providers.
A `String` patch is synthetic; actual binary patch transport is outside scope.
Disk save failure is excluded: Python assigns its ledger before a failing save,
and this graph returns its updated record only after save succeeds. Cancellation
or raw exceptions during commit/rollback, rollback shielding (`await_cleanup`),
provider ownership, parent-scope drainage, budgets and Leader publication remain
unverified here. Rollback failure leaves physical state unknown. The existing
TaskWait and default task-fault contracts remain unchanged.
