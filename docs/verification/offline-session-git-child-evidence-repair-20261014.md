# Git child overflow evidence correction, 2026-10-14

Status: Observed result; narrow independent re-review/publication pending.

Direct parent is request-changes `30851b84a85585aa8d66d06450eb0e37c220e9f0`,
tree `31b91d49a47d00ea968f0ae8684bee1b959ef355`, on isolated branch
`feat/physical-git-child-quiescence`. Task-41's independent review found no unsafe
replay or ownership/join defect within scope and requested one P2 evidence correction.
The original [record](offline-session-git-child-20261014.md) and task-root handoff
incorrectly claimed that overflow retained an actual graph error.

## Correction and maintained regression

Stdout/stderr overflow and active output-drain failure retain **no actual request
outcome**. The active launch fence rejects the registry read needed by
`registryPublish`; wire output is `err:unresolved`; the journal remains pending
with empty outcome/provenance; the registry is idle with empty outcome/provenance;
both `outcome-receipt/<token>` and `outcome/<token>` are absent. No attempted or
completed actual-outcome write/ready publication occurs. Restart closes before
input and preserves every retained storage and physical fact. A child exception
observation does not supply the missing original result.

Joined timeout and nonzero failures with a quiescent fence remain distinct: the
graph can retain their exact actual error and replay it without mutation. The
binding, graph, storage implementation, runtime/backend/schema and recovery policy
are unchanged. This repair changes documentation and example evidence assertions;
it does not manufacture an outcome or clear a fence.

The maintained child driver now imports `assertMissingActualOutcome` and asserts
both absent outcome keys, no actual-outcome write/ready publication, pending/empty
journal, idle/empty registry, active undrained fence and unresolved first wire reply.
It applies these assertions before/after restart for stdout and stderr overflow.
`--overflow-only` selects those affected controls so the unrelated matrix need not
be repeated. The existing maintained wrapper invokes the default driver, which also
includes these assertions and the added stderr case.

## Fresh versus reused evidence

Fresh repair execution was serialized in Nushell: **eight application processes,
four new disposable domains, zero parent SIGKILL observations**. Original/moved
locations each run stdout overflow plus restart and stderr overflow plus restart,
against unchanged reviewed compiled artifacts. Root children are terminated/joined
in all four overflow fixtures; their helper PIDs are observed no longer running.
Original/moved stable facts and offered/read/accepted output bytes pass. Raw restart
checks preserve storage, objects/refs/HEAD/index/file bytes/mode without writes or
Git mutation.

The same new assertions were freshly evaluated against **14 retained author/reviewer
overflow/drain rows**, including the independent stderr and descriptor-drain controls.
**18 retained quiescent timeout/nonzero rows** were checked for exact actual-error
receipt/outcome/provenance. These are new assertion evaluations of retained rows,
not new application executions. Six contradictory in-memory copies (receipt,
separate outcome, journal result, ready registry, actual write and wire result)
are rejected by the assertions. Original fixture/storage files are never rewritten.

Task-41's valid independent evidence is reused: **388 processes, 145 domains and
111 SIGKILL observations**, plus its separate **three-process/one-domain/one-SIGKILL
causal baseline**. The repair audit rechecks accepted raw hashes, sixteen passing
log hashes, all 18 enabled/control artifact hashes per original/moved location and
unchanged binding/compiler/registry/journal source bytes. Author task-39's 366-process
matrix and older assets/builds remain retained; they are not added to the independent
review totals. No new compilation, graph authoring/re-emission, full matrix or broad
build was needed because the executable implementation did not change. Changed
driver/helper and existing wrapper syntax checks pass. The full default driver path
was not rerun; its affected branches ran through the focused selection.

Evidence: task-root `evidence/overflow-repair-{original,moved}`, their raw/proof and
original `verified.json`, plus `evidence/overflow-repair-audit.json`. The checked-in
[repair proof](../../examples/offline-session-physical-git/fixtures/overflow-evidence-repair.json)
contains exact source/artifact/retained-review hashes and assertion results.
`audit-overflow-repair.mjs` rechecks the retained and new evidence. Review inputs
remain at `/home/kw4/Documents/Codex/2026-10-14/task-41/{independent-review.md,findings.json}`.
The original handoff is preserved as explicitly historical
`independent-review-handoff-30851b8.md`; current handoffs state the corrected result.

Reproduce only the affected execution in fresh output directories:

```nu
node examples/offline-session-physical-git/test-child-lifetime.mjs artifacts/child/original ../evidence/new-overflow-original --overflow-only
node examples/offline-session-physical-git/test-child-lifetime.mjs artifacts/child/moved ../evidence/new-overflow-moved --overflow-only
node examples/offline-session-physical-git/verify-child-lifetime.mjs ../evidence/new-overflow-original ../evidence/new-overflow-moved
```

Scope and next gap remain unchanged: one trusted writer, intact storage, controlled
Linux single-file Git fixtures; no arbitrary descendant/race, saturated input,
power-loss/storage-fault/multi-writer/provider-lifecycle or historical-authentication
guarantee. Explicit quiescence/disposition of abandoned active launches and owned
partial tails remains missing and must preserve original pending uncertainty.
No denied/pending/uncertain execution action remains. Independent evidence approval
and publication are pending. No push, PR, merge, live provider, remote Git/network,
credentials/access/service activation or user-project mutation occurred.
