# Release authority bound to accepted requests

Status: Observed result; author repair, independent re-review pending.

This repairs P2-1 on request-changes candidate
`420a30b753020a361098adc3612a0ee70f6c9091`, tree
`e3fa4b524d90a6a4b3f8e1673199caeeeff560e0`. The original independent review,
finding, raw reproductions and author/reviewer matrices remain intact. Exact
repair source/artifact hashes are in
[release-authority-repair.json](../../examples/offline-session-physical-git/fixtures/release-authority-repair.json).

## Finding and repair

The old release procedure compared release authorization only to current scenario
`continuationAuthorization` and `dispositionAuthorization`. Omission or relabelling
could therefore permit reuse of an accepted earlier authority. The unmodified
reviewer driver freshly reproduced all three reported violations on the archived
candidate: 17 applications, one fresh ref-only domain and one SIGKILL. Each invalid
case published three release records and opened observation input. No original
record or Git state was modified to obtain those results.

`ReleaseAuthorityLineage` now reads authority values from the exact accepted
continuation request and its nested retention request, after completion/lineage
validation. Release authority must differ from both values. Omitted or empty
earlier scenario settings remain supported; supplied nonempty values must match
their corresponding accepted values. These checks precede every release write,
including completion of a partially published release. The release request still
requires its own matching explicit scenario authority.

Startup applies the same retained-authority/configuration checks before input.
Previously invalid activated release records are refused without rewrite or
migration. Valid activation can be inspected with omitted or matching settings;
contradictory supplied settings close admission without revoking or overwriting
the stored decision. Accepted original and new records, fences, request identities
and missing-outcome uncertainty retain their previous meaning.

Only the example release/startup authority checks changed. Continuation/completion
code preceding release is byte-identical to the reviewed candidate, as are owner,
Git evidence, disposition, compiler, generic storage, graph/core/runtime/backend
and schema sources. No new storage fields, keys or versions were introduced.

## Focused fresh evidence

All expensive checks were serialized in Nushell, using only disposable Git
repositories, fake providers and controlled Linux children:

| Evidence | Final observations |
| --- | --- |
| Focused authority original/moved matrices | 334 applications, 16 fresh Git domains, 28 SIGKILLs; 167/8/14 per location |
| Exact unmodified original reviewer driver on repaired bindings | 34 applications, two fresh domains, two SIGKILLs; seven cases per location, zero mismatches |
| Legacy invalid activation checks | Three old invalid copies inspected twice and replay-refused in both locations: 18 applications included above, one existing disposable domain reused |
| Application write firewall | Two separate mechanical processes, using existing repaired disposable domains; zero storage/Git changes |
| Graph/artifact closure | Four exact graph reopen/re-emissions and four offline compilations with unedited application Generated.cs; 18 original/moved enabled/control artifacts match |

Coverage includes standard and independently chosen accepted authority values;
same-continuation/same-retention release authority with omitted, empty, correctly
bound and relabelled current settings; valid distinct authority with omitted,
empty, partially or fully bound settings; nonempty current continuation/retention
mismatches, swapped settings and a setting equal to the release authority; and
wrong current release authority. Positive releases replay without writes.
All refused releases leave raw storage and physical state exact, including refusal
before any plan/storage preparation. Startup closes for current-setting mismatches
and reopens for matching settings without rewriting the accepted release.

Every release intent/result/activation publication window is exercised. After
launcher death, an accepted earlier authority or relabelled setting cannot finish
the release; the valid explicit release can finish its storage publication safely.
Original and distinct integration IDs still return unresolved after valid
observation admission. Original records/fences and missing success/cancellation
remain exact; no Git command is dispatched during release.

The original/moved comparator checks all 167 corresponding rows and validates
each random join invocation's hashed filename before mapping it to command and
multiplicity. Local storage/physical assertions remain raw and exact. Cross-domain
physical comparison omits only index-stat digest. All 108 actual fresh owner/root
helper PIDs are absent or no longer running at audit; PID cleanup is never
authorization. Legacy/control PID fields are excluded from that fresh count.

## Retained evidence and reproduction

Author task root: `/home/kw4/Documents/Codex/2026-10-14/task-48`.
Final repair evidence: `evidence/release-repair/`. The exact reviewed bundles are
archived under `source-base/{original,moved}`. The original P2 review and finding
remain in task-49, with hashes pinned in `reuse.json` and the repair audit.

Preparation performed 173 exact retained author/reviewer source, artifact, raw,
proof and graph checks. Unaffected 630/98/144 continuation and 188/14/10 regression
packages remain separate author/reviewer observations and are reused rather than
recounted. The saved graph and broad generic build/test evidence are unchanged.
Fresh repair totals exclude the 17-process old-candidate causal reproduction.

The first preparation assumed reviewer regression metadata contained per-location
sets. Its partial exact archive was preserved and hash-checked before preparation
resumed using the actual totals/raw/proof structure; no application or Git effect
ran in that failed preparation. The first new closure comparison treated random
join filenames as stable. Its source/error log remain preserved; key normalization
was corrected after validating invocation-to-key hashes. Passed application runs
were not repeated for either bookkeeping error. No denied, pending or uncertain
execution action remains.

For a fresh reviewer-owned clone at the revised commit, read actual instructions,
CONTINUATION.md, the original task-49 review/finding and this record. Audit retained
graphs or author them with the maintained tools, reopen enabled/control graphs and
compile original/moved bundles without editing Generated.cs. Serialize:

```nu
node examples/offline-session-physical-git/test-release-authority.mjs <bundle> <fresh-output> <fresh-old-reproduction>
node examples/offline-session-physical-git/test-release-authority.mjs <moved-bundle> <fresh-moved-output> <fresh-old-reproduction>
node examples/offline-session-physical-git/verify-release-authority.mjs <fresh-output> <fresh-moved-output> <bundle> <moved-bundle>
```

The optional old-reproduction argument points to a fresh output of the exact
task-49 `review-authority.mjs` run against the archived reviewed binding. Run that
unmodified driver against both repaired bundles too, requiring zero mismatches.
Its hardcoded candidate literal names the original reviewed reference; the repair
audit binds each actual target binary/source hash. Adapt author wrappers/output
paths to reviewer ownership; never overwrite retained evidence.

Scope remains the offline single-writer fixture policy, not credential security.
Observation admission only; renewed mutation admission, interrupted-new-request
disposition and public graph-composed recovery remain missing. Intact flushed
storage and immutable provisioning are trusted. Arbitrary races/descendants,
uncooperative termination, saturated creation/specification feeding, cancellation
beyond shields, power loss/storage faults/deletion, multiple writers, full metadata,
linked worktrees/live providers, coherently rewritten history, scale/compaction
and peer acknowledgement remain unproved. No publication, merge, service/access
change, live provider or real user-repository recovery occurred.
