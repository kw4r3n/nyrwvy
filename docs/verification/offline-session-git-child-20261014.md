# Fenced physical Git child launch, 2026-10-14

Status: Observed result; independent review and publication pending.

Base: independently approved `6d5058463f6648d5ca0c29492b624196efe8c6da`, tree
`0abc652ab2625d1b525b6ec228bc9ed2dcbf6c69`. Isolated branch:
`feat/physical-git-child-quiescence` under task-39. The reviewed source, original
candidate and independent-review checkouts remain unchanged. No push/PR/merge
authorization carries onto this successor.

## Demonstrated gap and first dependency

The supplied handoff identified partial physical effects and child lifetime as
the next boundaries. A fresh paired probe used the retained approved runtime and
exact saved graph with a controlled local executable wrapper. It held before
`commit-tree` after receiving input. SIGKILL of the .NET parent left that child
alive while all repository facts still matched pre-state. A fresh restart reported
absence and invoked another `commit-tree`; explicit release then let the original
child write the same object. Duplicate invocation is distinct from unique object
count. This is three fresh application processes and one SIGKILL, with a reused
approved runtime. It demonstrates an unsafe quiescence assumption, not arbitrary
Git timing or an uncontrolled natural race.

The smallest dependency-first milestone therefore fences child launch rather than
adding recovery. The example binding now flushes an active invocation record before
mutation child creation, retains its PID after creation, and publishes quiescence
only after root exit, input closure and successful stdout/stderr drainage. The
record binds repository/fixture, effect token, arguments and input digest to its
physical plan. Active launch records close admission before input, even when the
registry is idle. They are never cleared from missing PID, parent death, complete
physical state or a late child write. A pre-launch death deliberately stays blocked
even though no child was created. No original result is inferred from these records.

Async input, bounded output capture and a shared deadline replace the old synchronous
stdin write followed by timeout. Timeout requests tree termination and bounded
exit/drain waits. Failed drainage leaves the active record intact. Joined failure
can retain and replay the graph's exact actual error; missing original outcomes
remain unresolved. Output overflow can retain an actual graph error while admission
stays closed because physical child quiescence is unavailable. Those facts are
separate and neither is rewritten on restart.

Only example binding, harness, closure, wrapper and documentation change. Existing
opaque storage, tasks, shields and graph outcome machinery are sufficient. No new
generic semantic/runtime/backend/schema primitive or Rrawrlet core policy is shown
missing. Physical-plan payload version 3 remains in `physical-plan/v1/`; explicit
versions 1/2 fail closed without migration. Trusted storage and inherited default
field behavior remain assumptions; this is not historical authentication.

## Fresh final verification and retained evidence

All expensive execution was serialized in Nushell. Original/moved bundles each
reopened the exact saved enabled/control graph and freshly compiled the binding
with unedited Generated.cs. Per location:

- 139 physical regression processes, 50 contracts and 38 subprocess-exit SIGKILLs.
- 44 child-lifetime processes, 19 fixture domains and 16 parent SIGKILLs, covering
  active pre-launch/held-child boundaries plus existing quiescent partial effects.

Final total: **366 fresh application processes, 138 disposable domains and 108
SIGKILL observations**. The separate three-process baseline is not added to those
totals. Original/moved artifact bytes, normalized bindings/observations, raw local
storage/physical-state equality, input and accepted output bytes pass. Every
controlled held helper is observed no longer running after its fixture completes.

Fresh child negatives include correlation, repository identity, arguments, input
digest, malformed launch data, explicit version-2 plan, mode/unowned object/ref,
partial index/worktree restoration and unavailable inspection. Pre-launch and
held-child deaths close before input with no storage rewrite/Git mutation. Late
object creation never clears the fence. Object-only/ref-only/completed physical
effects still cannot recover the missing original outcome. Timeout/unread-input
cases join the terminated child and replay the exact actual interruption error.
Output overflow joins the root but retains the active fence because drainage fails.
Fixture input is small; this does not demonstrate saturated-pipe backpressure.

The inherited matrix preserves once-only same/distinct-ID/lost-response behavior,
exact retained cancellation, reader exception identity/stack, shields/cleanup,
Member/admission guards, selected-intent/payload guards, mode/index/symlink checks,
version-1 content-only rejection, receipt-only duplicate-invocation control and the
restoration-disabled graph control. New pending/ready explicit version-2 controls
remain unresolved and preserve storage/physical facts without migration.

Audited reuse: **4,988 dependency/AuthoringSession files** and **ten exact graph
hashes**. Broad build/864 fast tests, expensive graph authoring, unchanged inherited
default-binding/registry/journal/reader regressions, installed compiler/reference/
runtime/environment remain retained evidence. The maintained wrapper is syntax
checked and its equivalent retained-seed sequence ran directly with persistent
evidence; its default fresh-authoring path was not rerun. Prior reviewer/author
totals are not relabeled fresh. Preparatory passing/failed suites are preserved and
excluded from final accepted totals.

Evidence under `/home/kw4/Documents/Codex/2026-10-14/task-39/` includes
`evidence/baseline-final`, `evidence/child-original-final3`, `evidence/child-moved`,
`candidate/artifacts/child/{original,moved}`, `evidence/logs`, `evidence/reuse.json`
and `evidence/final-audit.json`. `prepare.mjs`, `validate-child.nu` and
`audit-child.mjs` reproduce asset auditing and serialized verification. Summaries
are [physical proof](../../examples/offline-session-physical-git/fixtures/child-physical-proof.json),
[child proof](../../examples/offline-session-physical-git/fixtures/child-lifetime-proof.json)
and [closure](../../examples/offline-session-physical-git/fixtures/child-replay.json).

The initial sandbox `spawnSync EPERM` occurred after Git init reported success.
That empty initialized fixture was inspected and preserved. Narrow offline
execution escalation succeeded; no denial or uncertain action remains. Two early
compilation failures and preparatory harness expectation failures were corrected
before final verification. The supplied checkout has no AGENTS.md or .agents;
home AGENTS and checked-in developer/verification/example instructions were read.

## Limits and next gap

One trusted writer, intact flushed application storage, clean standalone SHA1
single-file Linux fixtures and controlled direct local Git remain required.
Creation is synchronous and its duration is not bounded. `Kill(true)` plus root
exit/drain observations are not proof that arbitrary escaped descendants cannot
write; hooks/signing are disabled and uncontrolled descendant/race coverage remains
separate. No cooperative cancellation outside the graph's existing shield is
introduced. Stream capture bounds decoded UTF-16 code units, not bytes. Full Unix
metadata/index extensions, power loss, storage faults/deletion, multiple writers,
linked-worktree/provider lifecycle, arbitrary scale, compaction, peer acknowledgement,
historical authentication and parser/predecessor limits remain unestablished.

Explicit disposition is still the next gap. An active fence cannot yet be cleared,
and owned object-only/ref-only tails cannot yet be continued through an explicit
recovery identity/result. Such an operation must establish quiescence, retain exact
authorization/correlation, reject unowned/contradictory state and preserve the
original pending uncertainty. Completing the physical tail cannot manufacture the
missing original success/cancellation result. No orphan deletion, ref rollback,
worktree reset or evidence replacement was used to repair an original request.

No live provider, remote Git, network, credentials/access expansion, service
activation, actual user-project probe, push, PR or merge occurred.
