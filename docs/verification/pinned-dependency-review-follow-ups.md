# Pinned-dependency review follow-ups

**Status:** Observed result

Baseline: `23a2df0`; follow-up working tree on `fix/review-dependency-guardrails`.
This record covers the remaining runtime/tooling review findings after the
[semantic-facts scaling repair](semantic-facts-scaling.md).

## Implemented scope

- **Finding 4:** Enforce the existing complete-source dependency contract at
  creation and reopen. Open source obligations produce `DependencyFailure`
  before admitting a workspace whose default commits would all be blocked.
  This does **not** add incomplete-dependency support or relax commit policy;
  standalone incomplete authoring remains valid.
- **Finding 5:** Reject imported edits before registering/exposing a proposal.
  Reuse workflow change-review affected owners, including caller/match repairs.
  Retain the independent immutable-import check during serialization/publication.
- **Finding 6:** Reject null/blank/incorrectly typed dependency manifest fields;
  return controlled errors for manifest access failures; release the lock on
  startup failure. Manifest cleanup after failed creation is best-effort and
  cannot replace the original error or prevent lock disposal.
- **Finding 7:** Name-resolved function creation rejects existing exact local or
  imported display names. Explicit identity-based operations and old snapshots
  retain duplicate-name support; ambiguous name resolution still fails closed.
- **Finding 8:** Add the shared `nyrwvy_open.dependency` argument for creation.
  Relative dependency/local paths use the selected workspace in both hosts;
  dependency arguments on reopen are rejected before spawning a child.
- **Session maintenance:** Store transaction identity with each pending proposal
  and use one rejection path instead of nine identical workflow dispatches.
  A stale proposal's rejection receipt retains the *current* snapshot identity.

## Verification

- Release AuthoringSession build: no warnings or errors.
- Focused dependency/session xUnit run: 29 passed, including 21 added dependency
  cases (malformed manifests, access failures, invalid creation paths, cleanup,
  incomplete source selection, and seven imported-edit workflows).
- Final fast xUnit suite: **369 passed**.
- TypeScript checks for both Node/OpenCode and Bun: passed.
- Adapter/schema/typecheck contracts: **8 passed**.
- Node authoring/evaluation transport/script contracts: **40 passed**.
- MCP stdio contracts: **11 passed**, including dependency-backed creation,
  foreign calls, immutable imports, local-only persistence, reopen, and a null
  manifest path through the real AuthoringSession process.
- Bun contracts: **31 passed**.
- `git diff --check`: passed.

The existing `verification-planner/transcript.jsonl` and review archive were not
modified. No live model evaluation, full extended suite, or power-loss durability
claim is part of this repair.

## Separately tracked work

| Remaining review scope | Issue |
| --- | --- |
| Finding 3: reproducible pins and reviewed recovery after upstream edits/relocation | [#209](https://github.com/kw4r3n/nyrwvy/issues/209) |
| Finding 1 remainder: repeated validation/fingerprint/serialization work across simulate+commit | [#210](https://github.com/kw4r3n/nyrwvy/issues/210) |
| Source/assembly ownership, shared workflow contracts, downstream evaluation compatibility | [#211](https://github.com/kw4r3n/nyrwvy/issues/211) |
| Explicit xUnit Trait suite classification instead of display-name matching | [#212](https://github.com/kw4r3n/nyrwvy/issues/212) |
| SDK policy, common project settings, maintained fish tooling coverage in CI | [#213](https://github.com/kw4r3n/nyrwvy/issues/213) |

Open issues/PRs and the remote default branch were checked first. Existing
[#109](https://github.com/kw4r3n/nyrwvy/issues/109) remains the deferred compiler
build-time investigation; the new issues distinguish runtime latency and
architecture/reproducibility rather than duplicating that compiler-speed task.
