# Bounded physical Git reconciliation

The [child launch milestone](../../docs/verification/offline-session-git-child-20261014.md)
follows independently approved `6d5058463f6648d5ca0c29492b624196efe8c6da`
(tree `0abc652ab2625d1b525b6ec228bc9ed2dcbf6c69`). A fresh held-child control
demonstrated that its absence rule could retry while the original Git child still
survived its parent. This continuation fences that launch before creation. It is
the first dependency for explicit disposition of partial effects, which remains
unimplemented; finishing a physical tail cannot reconstruct a missing request result.

Physical plans now use payload version 3 in the existing opaque namespace. Versions
1 and 2 fail closed without migration: they did not record this child-lifetime
contract. The separate `physical-child/v1` slot retains random invocation identity,
request effect identity, repository/fixture, exact arguments, input digest, child
PID and observed quiescence. One trusted writer serializes mutations. The slot is
flushed before process creation and again with the PID; only observed root exit,
closed input and successful output/error drainage permit a quiescent replacement.
An active slot closes startup before input and blocks every binding Git command.
It remains active after parent death, PID disappearance or late child effects;
none of those observations authorizes clearing it. The slot contains no request
outcome. Actual graph results retain their inherited provenance and meaning.

Input is written asynchronously and closed by its owner. Child input, stdout,
stderr and root exit share a 10-second fixture deadline. On failure the owner
requests tree termination, waits up to two seconds for exit and two seconds for
drainage, and leaves the slot active if joining/draining cannot be established.
Each output stream is bounded to 1,048,576 decoded UTF-16 code units. This is a
retained-output bound, not a byte quota or an arbitrary-repository guarantee.
The fixture can select a shorter 250–10,000 ms deadline for deterministic tests.
Process creation itself is synchronous; no bounded creation-time claim is made.
Direct local Git with hooks/signing disabled is the supported child boundary;
arbitrary surviving descendants and uncooperative termination remain unverified.

The offline child suite preserves pre-launch death, a surviving held child and its
late local object write, root termination/join, unread input, output overflow,
object/ref/worktree partial effects, missing outcomes and constructed ownership/
argument/input/metadata/restoration contradictions. Small fixture input does not
saturate a pipe, so arbitrary input backpressure remains a separate obligation.
Old unfenced plans, unavailable inspection and partial state cannot authorize
replay. No recovery/disposition identity or result is introduced in this milestone.
Joined failures with a quiescent fence can retain exact actual graph errors.
Overflow or failed drainage leaves an active fence: registry publication is then
blocked, the journal remains pending, both actual outcome entries are absent,
and the wire reply is `err:unresolved`. Child error observations do not supply
an original request outcome. See the [evidence correction](../../docs/verification/offline-session-git-child-evidence-repair-20261014.md).

The executable-mode repair follows the request-changes candidate
`98174898cb3ca1db33b44b0453d84304e50c1bd9` (tree
`159613c07f4e2250076cf5023340ac1c48ae4329`). Its independent review found that
content-only worktree facts could authorize retry or accept ready state after
`chmod 0644 -> 0755`. The [repair record](../../docs/verification/offline-session-physical-git-mode-repair-20261014.md)
separates fresh repair evidence from the retained original/reviewer matrices.

This continuation reuses the independently reviewed registry graph at
`b44dbdabb9d17ae8c641acbd1813cb014e39f326` (tree
`79b5430f90469ff0f1e25d1842853f7ce5ad7b5d`). It changes the example's effect
binding, plus exports the existing registry compiler's transform hook. No graph,
core, runtime, schema or backend primitive changes. Member identity/admission
guards and current-ready actual-result checks remain in the inherited graph.
The provider and Member workspace creation remain entirely fake. Real Git is
limited to fresh disposable repositories provisioned by `git-fixture.mjs`.

## Measured gap and binding

`probe.mjs` first executes four local Git command-exit probes. An empty synthetic
receipt ledger misclassifies three changed physical states: an unreachable commit,
a changed ref with the old index/worktree, and a completed ref/index/worktree.
These preliminary observations are command exits, not crash observations.

Before graph pending intent publication, the binding retains a physical plan:
exact correlation token and selected intent, absolute standalone repository and
fixture marker, selected commit/tree/patch, pre-mutation HEAD/symbolic branch,
all refs and objects including unreachable ones, index bytes and entries, and
worktree file hashes and Git-normalized executable modes. On Linux the owner
execute bit distinguishes regular-file Git modes `100644` and `100755`; other
permission bits, ownership and timestamps are outside tree-mode authentication.
The provisioned fixture is one SHA1 repository on `main`
with a single plain UTF-8 `file.txt`, a clean owned index/worktree and no external
object store. Immutable fixture provisioning and the initial object inventory
are trusted. This is an example binding for these fixtures, not a Git API for
arbitrary repositories. The plan predicts the exact commit object from fixed
fixture identity/date, parent, selected tree and correlation message.

The existing `step` interface binds physical effects as follows:

| Graph step | Physical behavior |
| --- | --- |
| `commit` | `commit-tree` writes the predicted object; `update-ref` changes `main` using the retained old HEAD |
| `head` | Reads/validates HEAD; no ref write |
| `save` | `read-tree --reset -u` synchronizes the disposable index/worktree to that commit |

Earlier path/check/apply steps remain fixture operations. The binding materializes
the selected tree through the commands above; it does not implement arbitrary
patch application or real provider work. Hooks, signing, global/system Git config,
prompting and optional read locks are isolated per subprocess. Subprocess output
is drained and the child is joined before emitting the crash checkpoint.

Physical-plan payload version 3 retains `path:Git-mode:content-digest` facts and
derives applied mode from the selected tree. The opaque `physical-plan/v1/` key
namespace is unchanged; payload version checks reject version-1 content-only
plans and version-2 unfenced plans without rewrite or migration. Their original
mode or launch contract respectively was never retained.

Missing side receipts do not authorize absence. Absence requires the complete
observed state, including raw index bytes and unreachable objects, to equal the
retained pre-state. Completion requires the exact predicted object, ref/HEAD,
selected index entries and worktree bytes/modes, with no extra objects/refs. Partial,
unowned, contradictory, missing-plan or unavailable inspection stays unknown;
the graph returns unresolved without automatic retry or storage rewrite. Present
side receipts must agree but remain observations, not authoritative absence.
The current ready registry is physically cross-checked before Member reconstruction,
receipt repair or reader start. A distinct ID for an already integrated Member
retains a no-mutation plan and an actual outcome; it performs no Git mutation.

Physical completion cannot determine original success versus cancellation.
The unchanged graph's atomic ready registry, scalar receipt, separate outcome
and done journal retain the actual result in their original order. Completion
before the first actual publication stays unresolved. Matching retained
`err:cancelled` replays exactly, while a later distinct integration ID returns
the retained HEAD without mutation. Lost pipe response also leaves one physical
effect set. No synthetic result is manufactured from HEAD or object presence.

## Validation

Run all heavy steps serially in Nushell. A fresh graph can be authored using
`run.mjs`; an audited exact retained graph may instead be copied and reopened.
Generated C# is never edited. Original/moved bundles require fresh `runs`.

```nu
node examples/offline-session-physical-git/run.mjs artifacts/physical/bundle
node examples/offline-session-physical-git/run.mjs artifacts/physical/bundle/restoration-disabled --restoration-disabled
node examples/offline-session-physical-git/compile.mjs artifacts/physical/bundle
node examples/offline-session-physical-git/compile.mjs artifacts/physical/bundle/restoration-disabled
node examples/offline-session-physical-git/test-runtime.mjs artifacts/physical/bundle
cp -r artifacts/physical/bundle artifacts/physical/moved
rm -r artifacts/physical/moved/runs
node examples/offline-session-physical-git/run.mjs --replay artifacts/physical/moved
node examples/offline-session-physical-git/run.mjs --replay artifacts/physical/moved/restoration-disabled
node examples/offline-session-physical-git/compile.mjs artifacts/physical/moved
node examples/offline-session-physical-git/compile.mjs artifacts/physical/moved/restoration-disabled
node examples/offline-session-physical-git/test-runtime.mjs artifacts/physical/moved
node examples/offline-session-physical-git/verify.mjs artifacts/physical/bundle artifacts/physical/moved
```

The maintained test wrapper follows that workflow, with an explicit optional
`NYRWVY_PHYSICAL_GIT_SEED` for an audited retained graph. Cross-bundle verification
checks exact artifacts, normalized identical control binding, request actions,
retained results, semantic physical state and actual input/accepted pipe bytes.
Fixture paths/marker identities and per-domain index stat-byte hashes are normalized
only for comparing fresh domains; local restart assertions retain raw-byte checks.

The faulty receipt-only control shares the graph and binding except the evidence
rule and its dispatch guard. After object creation without a side receipt it
repeats `commit-tree`: two command invocations, one unique object. The unchanged
restoration-disabled graph, with the identical normalized physical binding,
replays an actual result but fails the restored Member/HEAD contract. These are
causal negative controls, not supported modes. The added `content-only` worktree
fact control also shares the exact graph/compiled binding, omitting only Git mode
from facts. It reproduces false pending absence/mutation and ready admission/repair
after mode-only drift. Enabled pending and ready restarts preserve file bytes,
mode, index and storage on repeated execution, with no mutation or storage write.
Adjacent index-mode and symlink controls also fail closed. Eight metadata-only
Git probes establish that owner-execute changes are relevant while the tested
group/other execute and read/write permission differences are not Git mode changes.

## Limits and next gap

This proves selected subprocess-exit and controlled held-child process-death gates
under one trusted writer with intact flushed application storage. Git fsync/power-loss
durability, arbitrary command interruption, uncontrolled descendant lifetime,
races, storage faults/deletion, concurrent writers, linked-worktree
creation/discard, provider resumption, arbitrary repository formats/contents,
compaction, peer processing acknowledgement and provider/MCP/Python parity remain
outside the claim. A ref old-value check is not multi-writer exclusion.
The graph parser still bounds registry publication to 4096 code points/depth 16.
Current-ready association/result consistency does not authenticate old journals,
every Member value field or coherently rewritten trusted storage. The physical
plan binds selected facts and a fixture identity; it is not cryptographic attestation.

The next measured boundary is partial-operation continuation: the object-only
and ref-only crash domains remain unresolved on repeated restart. The binding
can detect and block these states but supplies no explicit recovery/disposition
protocol. That protocol must preserve the missing original outcome instead of
manufacturing success or retrying uncertainty. Active launches also remain blocked
pending an explicit quiescence/disposition protocol. Arbitrary running-command
interruption and descendant ownership require further investigation. Interrupted
real workspace creation/discard and live
provider effects remain later obligations.
See [verification](../../docs/verification/offline-session-physical-git-20261014.md)
and the fresh [mode repair proof](fixtures/mode-repair-proof.json)/[closure](fixtures/mode-repair-replay.json).
The original [proof](fixtures/proof.json)/[closure](fixtures/replay.json) remain
retained evidence for the request-changes candidate, not current repair evidence.
