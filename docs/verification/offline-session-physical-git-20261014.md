# Bounded physical Git reconciliation, 2026-10-14

Base: independently reviewed `b44dbdabb9d17ae8c641acbd1813cb014e39f326`, tree
`79b5430f90469ff0f1e25d1842853f7ce5ad7b5d`, published draft PR402 on PR401.
Candidate work is isolated on `feat/physical-git-reconciliation`; publication and
main integration await independent review. This record does not approve them.

## Result and missing capability assessment

Four preliminary command-exit probes establish the previous binding's missing
evidence: an empty synthetic receipt ledger reports absence after an actual
unreachable commit, after a ref write, and after index/worktree synchronization.
Only the unchanged physical domain is an absence candidate. No crash was used
in those four preliminary rows.

The new [example](../../examples/offline-session-physical-git/README.md) composes
the unchanged registry graph with a bounded physical Git binding. Its plan binds
repository/worktree identity, pre-HEAD, all refs/objects, raw pre-index bytes,
index entries/worktree file hashes and selected commit/tree/patch to the request
token/selected intent. `commit-tree`, `update-ref` and `read-tree --reset -u`
perform the actual disposable mutations. HEAD observation is read-only. Missing
side receipts never establish absence; partial/unowned/contradictory inspection
and unavailable state remain unresolved without retry or storage rewrite.

The current ready publication is physically checked before receipt repair/input.
Complete effects before an actual publication cannot recover a request result.
The graph retains exact cancellation rather than inferring success from HEAD.
Lost response, same-ID replay and a distinct ID for an already integrated Member
leave one effect set. The repaired rejected-admission/Member-number reuse guard
and current-ready contradictory-result guard are preserved and freshly exercised.
No missing generic core primitive was demonstrated. No graph/core/runtime/backend/
schema or Rrawrlet policy changes were needed. The existing registry compiler gains
an optional example-transform export; its default output remains byte-identical.

## Fresh final evidence

All expensive steps were serialized in Nushell with isolated task XDG paths,
installed Roslyn compilation and no package restore/network. Evidence is under
`/home/kw4/Documents/Codex/2026-10-14/task-36/`:

- `candidate/artifacts/physical/final` and `moved-final`: **206 fresh processes**,
  **76 fresh retained physical domains**, 38 contracts per bundle and **52 SIGKILL
  observations**. Both graph variants are freshly reopened and exactly re-emitted;
  both physical bindings are freshly compiled unchanged in each location.
- `candidate/artifacts/physical/inherited-registry`: **73 fresh baseline** and
  **16 fresh focused repair processes**, including **15 SIGKILL observations**.
  Its original/control default binding, generated C#, runtime DLL and runtime
  configuration match all eight reviewed hashes exactly after fresh compilation.
- Final accepted matrices total **295 fresh processes** and **67 SIGKILL
  observations**. Fifteen passing validation logs are retained in
  `evidence/physical/`; `audit.json` records hashes and provenance.

Physical effect counts per physical bundle, including the deliberate bad control:

| Observation | Count |
| --- | ---: |
| `commit-tree` mutation command invocations | 29 |
| New commit objects during those processes | 28 |
| `update-ref` mutation invocations / ref-state changes | 27 / 27 |
| `read-tree` mutation invocations / worktree-content changes | 25 / 25 |
| Read-only Git commands inside the binding | 2,512 |
| Graph HEAD observation steps | 25 |
| Additional read-only harness Git inspection commands | 824 |

The one repeated `commit-tree` invocation writes an already present object in the
receipt-only negative control; it is not a second unique commit object. Each bundle
also provisions 38 repositories separately: nine setup Git commands per repository
(seven mutating, two read-only), plus fixture file/marker writes. Constructed-damage
commands are explicitly recorded in the test source and on-disk input states; they
are setup for negative controls, not process integration effects. No fake provider
or Member workspace operation is counted as physical Git.

Natural crash gates cover durable physical intent, registry/journal intent,
subprocess exit after object/ref/worktree mutation but before side receipt,
commit receipt, completion before actual registry publication, atomic ready
publication before scalar receipt and actual receipt before separate outcome.
Reader faults retain exact exception identity/stack, shield tokens, cancelled
witness provider, joined cleanup and pipe bytes. Closing the peer output before
gate release measures actual lost-response retention.

Negative controls include unchanged intent with unavailable inspection, payload
conflict before effect inspection, changed selected intent despite authoritative
absence, unreachable extra objects, index/worktree drift, extra refs, missing or
unowned plan, repository identity drift, contradictory side receipt and current-ready
HEAD/actual-result contradiction. Constructed damaged domains are labeled separately
from natural process-death traces. Repeated unknown-state requests perform no Git
mutation and preserve all storage/physical bytes.

The paired receipt-only control uses the same graph and compiled binding with the
faulty absence rule/guard selected: after object creation with no receipt it invokes
`commit-tree` twice across restart and fails the no-repeat contract. The reviewed
restoration-disabled graph shares the identical normalized physical binding but
fails restored Member/HEAD inspection after same-ID result replay. Original/moved
artifact hashes, normalized host hash, causal inputs/actions, stable storage and
physical observations, input bytes and accepted output bytes all match.

## Retained evidence and provenance

The saved registry/control graphs and their authoring transcripts are reused from
the reviewed base. Ten saved graph/artifact hashes match exactly. **4,988** copied
Node/dependency/AuthoringSession build assets are individually audited against the
read-only reviewed source checkout. This is the relevant reused subset; it is not
a fresh broad build or a claim that the previous review's entire 5,706-file audit
was repeated. The installed compiler/reference/runtime/environment are reused.

The prior review's authoring, broad Release/864 fast tests and unchanged journal/
reader regressions remain retained reviewed evidence and were not run freshly.
The new maintained wrapper is syntax-checked; its underlying retained-seed workflow
ran directly with persistent evidence. Its default fresh-authoring path was not
run. An earlier 103-process preparatory pass and a failed overly strong storage
assertion trace are preserved under `artifacts/physical/bundle` and
`failed-first-runs`; they are excluded from final accepted totals. The assertion
was corrected because an original pending journal may legitimately become done
after a later distinct-ID publication; physical effects still remain unchanged.

The preliminary sandbox invocation returned a child-wait `EPERM` after Git init
had executed. The partial initialized directory is preserved and was inspected;
the complete preliminary probe used a fresh directory with offline execution
allowance. No denied, pending or uncertain operation remains. No live provider,
remote Git, credential/access/global config change, actual user project mutation,
push, main integration or merge occurred.

## Limits and next demonstrated boundary

Approval is sought only for a trusted single writer, intact flushed application
storage, clean provisioned standalone SHA1/single-UTF-8-file fixtures and quiescent
Git subprocess-exit gates. Index entry/worktree content matches establish applied
state; they do not authenticate arbitrary index extensions/metadata. Raw index
bytes are compared for absence and locally unchanged restart checks. Cross-domain
normalization removes fixture paths/marker identities and index stat-byte hashes;
it is never used to authorize a retry.

No power-loss Git durability, arbitrary interruption/race, concurrent writer,
surviving child ownership, storage fault/deletion, linked-worktree lifecycle,
live provider parity/resumption, uncooperative effect, arbitrary repository scale,
compaction or peer processing acknowledgement guarantee is established. The
4096-code-point/depth-16 parser and inherited predecessor behavior remain limits.
Only the current ready admission is validated; old journals and arbitrary Member
value fields still trust storage. Association consistency and fixture correlation
do not provide historical integrity or cryptographic attestation.

The measured next boundary is explicit continuation/disposition of partial
physical operations: object-only and ref-only crash domains remain unresolved
on repeated restart. No automatic retry is appropriate and the missing original
result cannot be reconstructed from completion. Investigate a bounded explicit
recovery protocol and Git child ownership before expanding to interruption while
commands run. Real workspace creation/discard and provider effects remain separate.
Independent review must precede feature publication/main integration.

Supporting retained summaries: [preliminary probes](../../examples/offline-session-physical-git/fixtures/probe.json),
[runtime proof](../../examples/offline-session-physical-git/fixtures/proof.json),
[artifact/source closure](../../examples/offline-session-physical-git/fixtures/replay.json).
