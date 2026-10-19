# Physical Git executable-mode repair, 2026-10-14

Direct parent: request-changes candidate `98174898cb3ca1db33b44b0453d84304e50c1bd9`,
tree `159613c07f4e2250076cf5023340ac1c48ae4329`. Repair checkout:
`/home/kw4/Documents/Codex/2026-10-14/task-36/mode-repair`, isolated branch
`fix/physical-git-executable-mode`. The previous candidate/review remain read-only.
Publication awaits fresh independent re-review.

## Finding and repair

The independent review at `/home/kw4/Documents/Codex/2026-10-14/task-38/independent-review.md`
found one P2: content-only worktree facts omitted executable mode. With bytes,
HEAD, refs, objects and index unchanged, `chmod file.txt 0644 -> 0755` falsely
authorized a pending retry, which ran three Git mutations and reset the mode.
A ready restart accepted the dirty worktree before input and receipt repair.
The original failure evidence remains under that task's `evidence/mode-finding.json`
and `evidence/independent/{pending,ready}-executable-mode`.

The binding now retains `path:Git-file-mode:content-digest` facts. Linux owner
execute determines regular-file mode `100755` versus `100644`. The expected applied
mode comes from the bounded selected tree, and the clean initial tree check uses
the same representation. Thus absence compares retained mode and completion
compares the selected mode as well as bytes/index entries. Symlink rejection
remains before content inspection. No graph/core/runtime/backend/schema primitive
changes, application policy or Member/admission changes were needed.

Physical-plan payload version is now 2 under the existing opaque
`physical-plan/v1/` key namespace. Version-1 records omitted the original mode,
so they fail closed without migration or rewrite. No mode is inferred from the
current file. Retained outcomes remain on disk; old records cannot authorize
physical retry or ready publication repair. This is a fixture-format boundary,
not historical attestation.

## Fresh repair evidence

All expensive runs were serial in Nushell with isolated repair XDG paths,
installed Roslyn, audited retained build assets and no network/restore:

- **266 fresh application processes**, **96 new physical domains**, **48 contracts
  per bundle** and **72 SIGKILL observations** in
  `mode-repair/artifacts/physical/repair` and `repair-moved`. Both original/control
  saved graphs reopened with exact C# re-emission. Both bindings compiled freshly
  in each location; generated C# remains unedited. Artifact/control normalization,
  stable retained results, raw local byte/mode checks and actual pipe bytes pass.
- **62 fresh processes** in 31 new constructed domains using the prior independent
  reviewer's driver, changed only for import/task/bundle/output paths. All **31
  contracts pass**, including its two original mode cases. This is author execution
  of independently written retained tests, not a new independent-review verdict.
  Driver/adaptation/proof/raw are under task root `mode-independent-probes.mjs`,
  `evidence/mode-probe-adaptation.json` and `evidence/mode-independent/`.
- **Eight metadata-only local Git probes** under `evidence/mode-metadata` verify
  owner executable mode, staged index mode and symlink type. Tested permission
  modes 0600, 0644, 0654 and 0645 have the same Git mode; 0744/0755 change it.
  These are command-exit metadata observations, not additional runtime/SIGKILL rows.

Total fresh repair runtime evidence: **328 processes and 72 SIGKILL observations**.
The original 133-process matrix was invoked directly; its compile/proof/raw files
retain evidence. Nine serialized reopen/moved/verification/adapted-driver logs are
in `evidence/mode-repair`; the audit records their hashes. `validate-mode-repair.nu`
describes the direct/moved sequence; `audit-mode-repair.mjs` checks provenance.

Per physical bundle, including deliberately bad controls:

| Process observation | Count |
| --- | ---: |
| `commit-tree` mutation invocations / introduced objects | 35 / 34 |
| `update-ref` mutation invocations / ref changes | 33 / 33 |
| `read-tree` mutation invocations / worktree-content changes | 31 / 31 |
| Worktree Git-mode changes during runtime | 1, in the bad content-only control |
| Read-only binding Git commands | 3,072 |
| Additional read-only harness Git commands | 1,064 |

Fixture provisioning and deliberate damage are separate. All mode changes made
by test setup are confined to disposable fixtures. Enabled drifted restarts have
zero mutation/graph-storage-write attempts and preserve local storage, index,
refs/objects and file bytes/mode on repeated restart.

## Causal controls and retained semantics

New natural seed crashes at `after-intent` and `after-effect` precede explicitly
constructed mode-only changes. Pending enabled restart returns `err:unresolved`
with unknown evidence. Ready enabled restart closes before reader and receipt
repair. Same graph/compiled binding with only content-only fact encoding selected
reproduces the old failures: pending falsely reports absence, runs commit/ref/
worktree mutations and resets 0755 to 0644; ready admits input/repairs while the
worktree stays dirty at 0755. Those are intentionally unsupported negative controls.

Additional pending/ready staged executable-mode and symlink substitutions fail
closed. Version-1 content-only constructed records also remain unchanged/unresolved.
Metadata setup is labeled deliberate damage rather than a natural crash trace.
The harness records raw permission bits and normalized Git mode and never normalizes
them away in original/moved comparisons.

Lost-response, identical-ID and distinct-ID once-only effects; exact retained
cancellation; original missing-outcome uncertainty; partial/unowned state no-retry;
reader exception identity/stack, shield and cleanup; payload/selected-intent guards;
and the repaired rejected-admission/Member-number reuse control still pass. The
62-process adapted driver also closes all adjacent prior ready actual-result,
Member association and physical-state contradictions.

## Retained evidence, limits and next boundary

Rechecked **4,988** copied Node/AuthoringSession assets and ten unchanged graph
hashes. Previous author 295-process/67-SIGKILL and reviewer 357-process/67-SIGKILL
evidence remain retained. Their inherited baseline/focused checks and eight exact
default-compilation hashes were not rerun because this repair changes only the
physical binding/harness/docs. Broad Release/864 fast tests, expensive graph
authoring and unchanged journal/reader regressions remain retained. Installed
compiler/reference/runtime/environment are reused. The maintained wrapper is
syntax checked and its equivalent retained-seed workflow ran directly; its default
fresh-authoring path was not rerun. The prior failed candidate fixtures are kept
unchanged alongside separate repair proof/closure files.

The existing scope remains one trusted writer, intact flushed application storage,
clean provisioned standalone SHA1/single-UTF-8-file Linux fixtures and quiescent
subprocess-exit gates. Git file mode is checked; full Unix permissions/ownership/
timestamps and arbitrary applied-index extensions/metadata are not authenticated.
No power loss, arbitrary race/interruption, surviving Git child, multiple writer,
storage fault/deletion, linked-worktree/provider lifecycle, compaction, arbitrary
repository scale or peer-processing acknowledgement guarantee is established.
Current ready checks still do not authenticate every historical journal or Member
value field. Parser/predecessor limits remain inherited.

The next demonstrated boundary remains explicit continuation/disposition of partial
physical effects. Object-only/ref-only crashes remain unresolved; complete Git state
cannot recover the missing original outcome. No automatic retry or manufactured
success was added. Child ownership needs investigation before running-child kill
tests. No actual user project, provider, remote Git, network, credentials, active
application state, push, main update or merge was used. No denied/pending/uncertain
execution action remains; publication is stopped for independent re-review.

Retained summaries: [repair proof](https://github.com/kw4r3n/nyrwvy/blob/archive/legacy-examples-pre-cutover-20261019/examples/offline-session-physical-git/fixtures/mode-repair-proof.json),
[repair closure](https://github.com/kw4r3n/nyrwvy/blob/archive/legacy-examples-pre-cutover-20261019/examples/offline-session-physical-git/fixtures/mode-repair-replay.json),
[metadata probes](https://github.com/kw4r3n/nyrwvy/blob/archive/legacy-examples-pre-cutover-20261019/examples/offline-session-physical-git/fixtures/mode-metadata.json).
