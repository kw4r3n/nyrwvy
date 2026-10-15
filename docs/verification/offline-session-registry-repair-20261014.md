# Registry identity and actual-data consistency repair — 2026-10-14

Independent review requested two P2 repairs at candidate
`903c184ad31d2d018dc8776273aeb94888518469`, tree
`c4eea7630f97279317a9212a47706e35ce465455`. The review and its 14 independent
processes are retained at `/home/kw4/Documents/Codex/2026-10-14/task-34`.
Those observations are reused RED evidence, not new executions for this repair.

The first failure occurred in an intact serial domain. A capacity-rejected spawn
retained predicted Member number 3; a later distinct admission successfully used
that number. Replaying the rejected ID incorrectly resolved against the later
Member. Replay now requires an existing Member whose retained fact matches the
request identity, fingerprint and Member number. Predicted admission records
remain attempted associations; they do not independently establish acceptance.

The second failure used explicitly constructed damaged input. A ready registry
retained actual cancellation while a contradictory separate outcome supplied
success; startup seeded a cancellation scalar, but inherited reconciliation
preferred the contradictory separate result and rewrote the journal as success.
Another constructed done journal disagreed with otherwise matching actual data.
Startup now checks the current ready admission's journal shape, identity,
fingerprint, selected intent, effect identity, state/provenance/result, separate
outcome and scalar receipt before seeding or starting input. Any disagreement,
malformed value or missing current journal closes without a repair write. Matching
pending journal plus registry-only cancellation still seeds the missing scalar
and returns the exact retained cancellation without repeating effects.

The same boundary checks snapshot Member identities/fingerprints against Member
sidecars and inverse admission associations. Distinct positive IDs below `next`
must cover the full append-only registry counter. Thus the review's constructed
omitted-Member and identity-drift inputs also close instead of being silently
normalized from sidecars. This is consistency within trusted storage, not
authentication against coherent malicious rewriting of all records.

All application policy remains ordinary graph/storage composition. No Host
operation, binding, core/runtime/backend/schema, inherited journal/reader or
shared compiler source changes. The protected actual-publication order is
unchanged. Pending partial/applied-without-actual/unknown effects remain unresolved;
only unchanged intent plus authoritative absence can retry the identical ID.

Fresh final repair validation ran serially in original/moved bundles:

- Existing matrix unchanged: **146 processes, 32 contracts and 30 SIGKILL deaths**.
  Enabled success/same-ID/new-ID remains 1/1/1; disabled remains 2/2/2.
- Separate focused repair proof: **32 processes total, 15 contracts in each bundle**.
  The capacity rejection/reuse sequence executes freshly in an intact domain.
  Contradiction controls are copied, explicitly damaged inputs derived from each
  repaired bundle's fresh success/cancellation seed. Matching evidence is a
  positive recovery control. Closure checks compare moved artifacts, stable
  state/results/actions, source/raw hashes and actual pipe bytes.
- Freshly authored main graph reopens and emits exact unedited C#, which compiles
  in both locations. The exact reviewed restoration-disabled snapshot/runtime is
  reused and reopens exactly; its processes execute freshly. Its selected branch
  and normalized binding are unchanged. Control compilation is reused evidence.

The maintained registry test now includes the new controls. Its underlying
original/moved scripts ran directly with retained artifacts; the temporary
wrapper was not rerun to duplicate those executions. Unchanged journal/reader
regression results and all three independently reviewed regression passes at
903c184 are reused, with provenance and hashes. Reviewed Node/AuthoringSession
assets and installed compiler/reference/runtime remain reused; broad baseline
builds/fast tests are not newly claimed. The original fixtures/evidence are
preserved. New [focused proof](../../examples/offline-session-registry/fixtures/repair-proof.json)
and [closure](../../examples/offline-session-registry/fixtures/repair-replay.json)
separate the repaired executions from the original milestone proof.

Limits remain one intact trusted single-writer domain, selected Linux windows,
synthetic effects and inherited parser/transport bounds. Physical Git/provider
reconciliation, power loss/write faults, arbitrary schedules/races, resumption,
multiple writers, growth/compaction, peer acknowledgement and historical
Rrawrlet/MCP/Python parity remain outside scope. No live effects, credential/access
change, push, merge or main integration occurred. Exact new commit/tree and
independent-review handoff are recorded externally after committing the repair.
