# Git retention independent-review repair, 2026-10-14

Status: Observed result; fresh independent re-review pending. Publication remains
unauthorized.

Direct parent: request-changes `7247ba58082cef18000bd8795fe31fef9cf0a466`, tree
`d88700d541290295c0d8dd563a79834817a99b03`. Work remains isolated on
`feat/explicit-git-disposition` in task-43. Task-46 review and findings are retained
unchanged. The task-root handoff pins the final revised commit/tree.

## Findings and changes

Task-46 found two P2 defects and one P3 defect despite the passing maintained
matrix. Ordinary `update-ref`/`read-tree` ignored stdin and completed Git changes,
but an empty data write could raise EPIPE; the owner also checked input closure
before its close callback. Held wrappers consumed stdin and masked that boundary.
Matching future-phase side receipts were accepted for an object-only tail. The
.NET `$` regex anchor accepted a recovery ID ending with LF.

The owner now closes empty input without a data write and explicitly joins stdin
finish/descriptor-close alongside root/output closure within the existing deadline
and drain window. Genuine feed/capture/start/drain failures remain unresolved.
Adjacent inspection found that root exit followed by the deadline could arm two
drain timers, leaving an uncancelled timer after valid drainage. Both now use one
idempotently armed timer; bounded descriptor release produces a valid join and
normal owner exit, while held descriptors still produce no witness.

Receipt validation now correlates phase with physical state and last mutation:
`commit`/`head` require matching ref state and `update-ref`/`read-tree`; `save`
requires applied state and `read-tree`. Absent/object-only tails reject every side
receipt. Missing receipts remain non-authoritative. These checks precede existing
retention-result replay. Full-string `\A...\z` anchors reject trailing LF/CRLF in
IDs and, as an adjacent check, output/error digests. No payload migration or storage
rewrite is introduced.

Original journals, outcomes/receipts, registry, physical plans/receipts and launch
fences retain their meaning. Retention still has a separate authorized result and
cannot continue a tail, release admission or infer original success/cancellation.
The reviewed registry graph, core/runtime/backend/schema and generic storage are
unchanged. Changes remain in the example binding, maintained probes and records.

## Fresh focused evidence

All expensive executions ran serially in Nushell, with offline fake providers,
fresh disposable standalone repositories and installed local compiler assets.
Both enabled/control graphs reopened and re-emitted exactly in original/moved
locations; four bindings compiled offline with unedited Generated.cs. All 18
enabled/control artifact hashes match. Final templates occur verbatim in compiled
programs. The final external owner script hash is pinned separately and checked
against every fresh witness.

Fresh final application/disposition execution: **188 processes in 14 Git domains,
10 SIGKILL observations** (94/7/5 per original/moved location). Controls use fresh
explicit storage copies in those domains, with seed originals and raw snapshots
preserved. Coverage includes:

- Ordinary foreground owner integration using real Git, with three observed joins
  for commit-tree/update-ref/read-tree, exact original success, same-ID replay,
  separately retained disposition and distinct-ID no-mutation behavior.
- Ordinary foreground cancellation, exact retained `err:cancelled`, same-ID replay
  before/after retention and distinct-ID no-mutation behavior.
- A receipt phase/state table for absent, object-only, ref-only, applied and
  witnessed active object-only tails, with missing receipts as positive controls.
  Matching future phases and contradictory latest invocations fail closed.
  Ten constructed already-retained results cannot override contradictory receipts.
- Thirteen unsupported string IDs plus null/numeric IDs are rejected: LF/CRLF,
  leading/embedded newline, whitespace/tab/NUL/slash, Unicode/fullwidth, empty and
  length 81. Five legal 1/80-length/ASCII/underscore/hyphen boundaries retain and
  replay exactly. LF/CRLF on each capture digest is rejected.
- Retained active startup remains closed before input; absence and late physical
  effects cannot manufacture the missing original result. Accepted disposition
  requests add only their separate result; rejected/replayed requests and physical
  state remain byte-for-byte intact.

Separate mechanical execution, excluded from application counts:

- **15 repaired direct owners / 15 domains**, five each of ordinary commit-tree,
  update-ref and read-tree without stdin-consuming wrappers. All exit normally and
  retain exact joins, launch/input correlation and output/error capture digests.
- **Eight repaired owner closure probes / eight domains**: seven no-witness
  failures (both overflows, failed creation, genuine failed nonempty feed, held
  output descriptors and invalid UTF-8 on each output stream), plus successful
  descriptor release after the 500 ms deadline within bounded drainage. Original
  active launch bytes and physical Git state remain intact. Two controlled
  descriptor helpers are explicitly observed terminated.
- **15 old-owner causal controls / 15 domains**, using exact archived parent owner
  bytes: all ten ordinary no-input update-ref/read-tree commands complete physical
  changes but lose their join. These controls are not repaired-owner evidence.

One hundred recorded helper PID observations across these fresh/control cases are
absent or no longer running. Cleanup queries are test observations, never production
authorization for disposition. Original/moved semantic comparisons omit only
index-stat hashes and helper PIDs; raw local storage/physical preservation and
launch/plan/witness checks remain exact. Application offered/read and accepted
output bytes pass.

## Retained evidence and provenance

5,087 fresh hash comparisons cover 4,988 local assets, ten exact graph seeds,
retained raw/proofs/logs/artifacts/closures, old source bytes and unchanged inherited
sources. The original author **686-process/196-domain/164-SIGKILL** package remains
retained; task-46's independently repeated 686-process package is a separate
retained record. They are not added to fresh repair counts. Old measured held-
wrapper/receipt-field cases remain valid; they did not demonstrate the ordinary
no-input and phase/newline contracts repaired here. Broad builds/864 fast tests,
unchanged inherited matrices and expensive fresh authoring are reused.

Preparatory repair matrices (82 per location, then 94 per location before timer
strengthening), fifteen earlier repaired owners and seven earlier failure probes
remain preserved and excluded from final totals. Generated C# and the final C#
binding bytes did not change during the later mechanical timer/test refinements,
so verified compilation was reused rather than repeated. The final owner bytes
and all affected application/mechanical cases were executed freshly afterward.
Maintained drivers/wrapper pass syntax and Git diff checks; the full default wrapper
and its expensive fresh-authoring path were not repeated. Its affected branches ran
through `--repair-only`, plus the separate direct/closure drivers.

Task-root reproducibility files: `prepare-review-repair.mjs`,
`validate-review-repair.nu`, `validate-review-repair-final.nu`,
`audit-review-repair.mjs`; evidence in `evidence/review-repair/{reuse,audit}.json`,
`{original,moved,direct-owner,owner-failures,old-owner-control}`, logs and preserved
preparatory locations. The checked-in [repair proof](https://github.com/kw4r3n/nyrwvy/blob/archive/legacy-examples-pre-cutover-20261019/examples/offline-session-physical-git/fixtures/disposition-review-repair.json)
pins raw/source/artifact/log hashes and counts. New output directories are required;
adapt task-root paths for independent re-review without altering author evidence.

## Limits and next gap

Scope remains one trusted serialized writer, intact flushed storage, controlled
Linux standalone SHA1/single-UTF-8-file fixtures, isolated Git config/hooks/signing
and fake providers. Process creation/specification feeding remain synchronous and
unbounded. The failed large-feed control proves refusal, not successful saturated-
input behavior. Controlled descriptor helpers do not prove arbitrary escaped-
descendant safety. Arbitrary races/uncooperative termination, cancellation beyond
existing shields, power loss/storage faults/deletion, multiple writers, full Unix/
index metadata, linked-worktree/live-provider lifecycle, arbitrary scale/compaction,
peer acknowledgement and coherently rewritten historical authentication remain
unestablished.

Next gap remains separately authorized physical continuation/admission release,
with its own durable pending intent/result, custody and interruption protocol,
preserving missing original outcome uncertainty. Public language-composed recovery
remains missing; no generic core deficiency was demonstrated.
No denied, pending or uncertain execution action remains. Independent re-review
is pending. No publication, merge, user-project probe, remote Git/live provider,
service activation or credentials/access change occurred.
