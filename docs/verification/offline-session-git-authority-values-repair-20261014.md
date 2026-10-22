# Authority JSON value repair

Status: Observed result; author repair, independent re-review pending.

This repairs P2-2 on `221c92914d2de8c5dfb6aba55d72ecb50b547c3a`, tree
`f05f0dc00fefe37ab61014f5b3c095ebb87cccd2`. Task-49 closed P2-1 but demonstrated
that null request/current release authorities passed the empty-string comparisons,
published three records and opened input. Original records/fences and unknown
outcomes remained intact. The exact driver reproduced that failure on the archived
parent. Adjacent parent probes also demonstrated null retention and continuation
acceptance, then retained two invalid ancestral activations in disposable fixtures.

`AuthorityValue` now checks JSON string kind, runtime non-nullness and required
nonempty length. `ScenarioAuthority` distinguishes optional absence/empty string
from present malformed values. Retention, continuation, release, accepted original
lineage and startup all use these checks before publication or admission. The P2-1
accepted-authority separation/current-setting rules remain intact. Historical null
release and null accepted retention/continuation ancestry are refused without
rewriting any stored record. No schema, key, core, graph, storage or owner change
was introduced.

The existing authority schema is an exact opaque nonempty string, without an ASCII
character rule. Whitespace/case remain significant: whitespace-only and padded
strings are valid when explicitly matched; missing/null/non-string/empty required
values are invalid. No trimming or case folding is introduced. Optional earlier
settings allow absence/empty strings but refuse null or other JSON types. Request
IDs retain their stricter ASCII/full-string rule; LF/CRLF/space/Unicode ID controls
still refuse.

## Fresh serial offline checks

| Evidence | Observed result |
| --- | --- |
| All three authority-value boundaries, original/moved | 322 applications, 14 fresh domains, 14 kills; malformed JSON types, missing/empty values, whitespace/case, literal string `null`, Unicode authority, ID controls and three historical invalid contexts |
| Exact unmodified P2-2 reviewer driver | 32 applications, existing fresh seed domains, zero violations |
| Exact unmodified P2-1 reviewer driver/seed | 34 applications, two fresh domains, two kills, zero mismatches |
| Earlier authority separation and six release publication windows | 334 applications, 16 fresh domains, 28 kills, including 18 old P2-1 activation inspections/replays |
| Affected retention/ordinary outcome regression | 188 applications, 14 fresh domains, ten kills; exact success/cancellation and original-record preservation |
| Firewall | Two separate mechanical processes in existing disposable domains, zero storage/Git changes |

Final affected total: 910 application runs, 46 fresh Git domains, 54 SIGKILLs.
Historical disposable domains reused for read-only refusal are not added to fresh
domain totals. Four saved graph reopen/re-emissions and four offline installed-
Roslyn compilations use unedited Generated.cs. All 18 enabled/control original/
moved artifacts match. All 224 actual fresh owner/root helper PIDs were absent or
no longer running at audit; cleanup observations are not authority.

The separate parent controls are excluded: 17 seed applications/one domain/one
kill, 16 original null release reproduction processes, and eight related null
retention/continuation processes/two domains/two kills. Their invalid historical
records and exact parent artifacts remain preserved. Preparation checked 118 exact
source/artifact/raw/graph hashes and pinned the original re-review/finding and
audit. Unaffected broad core/owner evidence is reused, not recounted.

Author task root: `/home/kw4/Documents/Codex/2026-10-14/task-48`.
Final evidence: `evidence/null-authority-repair/`; exact reviewed artifacts:
`source-base/{original,moved}`. The final audit is byte-identical to
[authority-values-repair.json](https://github.com/kw4r3n/nyrwvy/blob/archive/legacy-examples-pre-cutover-20261019/examples/offline-session-physical-git/fixtures/authority-values-repair.json).
Earlier author and independent reviewer evidence remains separate and unchanged.

For a reviewer-owned fresh clone, audit/reopen retained graph seeds or author fresh
graphs, then compile enabled/control bundles in original/moved locations. Serialize
the exact task-49 null reproducer and the maintained `test-authority-values.mjs`,
`test-release-authority.mjs`, `test-disposition.mjs --repair-only`, their closure
checks and firewall probe. The authority-value driver accepts paths to fresh old
null-release and related-ancestor control outputs; these are read-only inputs.
Adapt author wrappers to fresh reviewer paths. Never edit Generated.cs or rewrite
retained history to make an authority case pass.

No denied, pending or uncertain execution action remains. Independent re-review
is pending. Scope remains controlled offline single-writer Linux SHA1/single UTF-8
file fixtures, isolated Git settings/hooks/signing, immutable provisioning and
intact flushed storage. Observation admission only; renewed mutation admission,
public graph-composed recovery and interrupted-new-request disposition remain
missing. Arbitrary races/descendants, saturated synchronous feeding, uncooperative
termination, cancellation beyond shields, power loss/storage faults/deletion,
multiple writers, full metadata, linked worktrees/live providers, coherently
rewritten history, scale/compaction and peer acknowledgement remain unproved.
No publication, merge, service/access change, live provider or real recovery occurred.
