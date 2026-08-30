# M2 controlled structural agent evaluation — 2026-08-31

**Status:** Complete controlled evidence tranche for the frozen Float64 structural-editing claim.

This record reports the 30 valid autonomous attempts collected under frozen `m2-agent-corpus-v1` and controlled OpenCode condition v1.1. It preserves the frozen machine oracles and separately reports additional prompt-fidelity findings; it does not change corpus definitions after observing results.

## Fixed condition

- corpus: `m2-agent-corpus-v1`
- corpus branch: `eval/m2-agent-corpus-v1-20260830`
- corpus SHA: `efce0cac15565941c11c8af8d1a52e7ecafaa5c6`
- runner protocol: `m2-structural-v1`
- condition branch: `eval/m2-structural-condition-v1.1-20260830`
- condition/repository/tool SHA: `178f38274890a28ae61a1c70c9d598bc01492256`
- model: `openai/gpt-5.6-terra`
- reasoning variant: `high`
- provider/runtime: `opencode`
- OpenCode: `1.18.18`
- prompt revision: `m2-corpus-v1-seed-v1`
- attempts: 3 fresh sessions per M2-E1..M2-E10, 30 valid attempts total
- human intervention inside attempts: none

Condition v1 preceded this tranche but failed real OpenCode custom-tool discovery. Its ten Stage-A sessions produced no harness runs and are retained as invalid-condition evidence, not part of these 30 valid attempts. Condition v1.1 moved the same bridge behavior to direct project-local custom tools and passed a real runtime preflight before this tranche.

## Raw evidence packages

The complete private run artifacts were collected in two archives outside the repository. This repository record pins their exact names and hashes without committing generated binaries/build trees.

| Package | Attempts | Bytes | SHA-256 |
| --- | ---: | ---: | --- |
| `m2-condition-v1.1-stage-a-20260830-160815.tar.gz` | a1 for E1-E10 | 551159 | `28226492e8e349c54390ec9c61f3cf6a236a62a07460b29a402e46e2a2f789f6` |
| `m2-condition-v1.1-stage-b-20260830-201209.tar.gz` | a2/a3 for E1-E10 | 1074432 | `d0204a475f001017c5ba7d1edec1cd6d012f20ab4a6041e3d8c5f0da4be4dcc8` |

Every valid attempt contains operator JSONL/DEBUG stderr plus a private harness run directory with exact metadata, request/response events, terminal state, metrics, and committed-state verification artifacts where applicable.

## Headline results

### Frozen machine-oracle result

**29/30 attempts passed the frozen machine oracle (96.7%).**

The single oracle-level failure was M2-E6/a1, where the agent never constructed a recipe large/deep enough to trigger the required `StructuralRecipeDepthLimitExceeded` diagnostic.

The frozen E6 machine oracle checks the diagnostic, so E6/a2 and E6/a3 count as oracle passes. They nevertheless expose an additional exact-request-fidelity problem described below.

### Supplemental exact-request fidelity

The M2-E6 prompt explicitly requests a left-deep recipe of **depth 33** and says to preserve the exact requested structure. The frozen regression helper `deepRecipe 33` has 65 nodes / 33 literal leaves / depth 33 under the runner's depth definition.

Observed E6 behavior:

- a1: only reached 3 nodes/depth 2, 9/depth 5, and 11/depth 6; all were proposed then self-rejected by the agent;
- a2: after one undersized proposal, submitted 67 nodes / 34 literal leaves / depth 34 and received the expected depth-limit diagnostic;
- a3: after three undersized proposals, submitted the same 67-node/depth-34 shape and received the expected diagnostic.

Thus **27/30 attempts (90.0%) satisfy this stricter prompt-shape fidelity interpretation**. This is supplemental analysis only; it does not rewrite or rescore the frozen machine oracle.

### Clean-attempt result

**24/30 attempts were oracle-correct without an agent self-reject retry or protocol-input error.**

The six attempts with additional avoidable work/errors were:

- E6/a1, a2, a3 — repeated undersized/off-by-one recursive recipe construction;
- E7/a1 — two undersized proposals before the correct balanced 65-node request;
- E9/a3 — one protocol error from sending decimal Float64 bits instead of 16 hexadecimal digits, then recovery;
- E10/a3 — the same Float64-bits transport mistake, then recovery.

Expected guard rejections in E2/E3 and the expected stale commit rejection in E10 are protocol-required task behavior and are not counted as agent errors.

## Per-task summary

| Task | Frozen oracle | Result | Notes |
| --- | --- | --- | --- |
| M2-E1 | reviewed Add(Parameter, Literal) commit + exact behavior | **3/3** | Exact materialized roles/order and literal bits; committed execution `3.75` in all attempts. |
| M2-E2 | expected-base rejection -> fresh recovery commit | **3/3** | `StructuralExpectedBaseMismatch` first, then valid commit in all attempts. |
| M2-E3 | expected-result rejection -> fresh recovery commit | **3/3** | `StructuralExpectedResultMismatch` first, then valid commit in all attempts. |
| M2-E4 | foreign parameter rejected | **3/3** | `StructuralParameterOutOfScope`. |
| M2-E5 | wrong-type owned parameter rejected | **3/3** | `StructuralParameterTypeMismatch`. |
| M2-E6 | recipe depth overflow rejected | **2/3 machine-oracle** | a1 missed boundary; a2/a3 obtained `StructuralRecipeDepthLimitExceeded` with depth 34 rather than requested depth 33. Exact depth fidelity: **0/3**. |
| M2-E7 | recipe node-count overflow rejected | **3/3** | a1 needed two self-rejected undersized recipes; a2/a3 directly produced 65 nodes / depth 7 and `StructuralRecipeNodeLimitExceeded`. |
| M2-E8 | open-obligation-protected body rejected | **3/3** | `StructuralBodyReferencedByObligation`. |
| M2-E9 | resolved-obligation-protected body rejected | **3/3** | Same diagnostic; a3 recovered from one malformed Float64-bits request. |
| M2-E10 | stale reviewed proposal -> fresh recovery | **3/3** | All received `StaleBaseSnapshot`, recovered, committed, and preserved harness change. All ended with one stale proposal handle still pending. |

## Per-attempt metrics

| Task | Attempt | Oracle | E6 exact-depth fidelity | Tool calls | Simulations | Agent rejects | Protocol errors | Pending proposals | Exact execution output |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| E1 | a1 | pass | — | 5 | 1 | 0 | 0 | 0 | `3.75` |
| E1 | a2 | pass | — | 5 | 1 | 0 | 0 | 0 | `3.75` |
| E1 | a3 | pass | — | 5 | 1 | 0 | 0 | 0 | `3.75` |
| E2 | a1 | pass | — | 8 | 2 | 0 | 0 | 0 | `2` |
| E2 | a2 | pass | — | 8 | 2 | 0 | 0 | 0 | `2` |
| E2 | a3 | pass | — | 9 | 2 | 0 | 0 | 0 | `1` |
| E3 | a1 | pass | — | 7 | 2 | 0 | 0 | 0 | `42` |
| E3 | a2 | pass | — | 6 | 2 | 0 | 0 | 0 | `1` |
| E3 | a3 | pass | — | 7 | 2 | 0 | 0 | 0 | `1` |
| E4 | a1 | pass | — | 5 | 1 | 0 | 0 | 0 | — |
| E4 | a2 | pass | — | 3 | 1 | 0 | 0 | 0 | — |
| E4 | a3 | pass | — | 4 | 1 | 0 | 0 | 0 | — |
| E5 | a1 | pass | — | 4 | 1 | 0 | 0 | 0 | — |
| E5 | a2 | pass | — | 3 | 1 | 0 | 0 | 0 | — |
| E5 | a3 | pass | — | 4 | 1 | 0 | 0 | 0 | — |
| E6 | a1 | **fail** | fail | 13 | 3 | 3 | 0 | 0 | — |
| E6 | a2 | pass | **fail** | 9 | 2 | 1 | 0 | 0 | — |
| E6 | a3 | pass | **fail** | 14 | 4 | 3 | 0 | 0 | — |
| E7 | a1 | pass | — | 8 | 3 | 2 | 0 | 0 | — |
| E7 | a2 | pass | — | 5 | 1 | 0 | 0 | 0 | — |
| E7 | a3 | pass | — | 6 | 1 | 0 | 0 | 0 | — |
| E8 | a1 | pass | — | 6 | 1 | 0 | 0 | 0 | — |
| E8 | a2 | pass | — | 4 | 1 | 0 | 0 | 0 | — |
| E8 | a3 | pass | — | 5 | 1 | 0 | 0 | 0 | — |
| E9 | a1 | pass | — | 5 | 1 | 0 | 0 | 0 | — |
| E9 | a2 | pass | — | 4 | 1 | 0 | 0 | 0 | — |
| E9 | a3 | pass | — | 7 | 2 | 0 | 1 | 0 | — |
| E10 | a1 | pass | — | 9 | 2 | 0 | 0 | 1 | `2` |
| E10 | a2 | pass | — | 9 | 2 | 0 | 0 | 1 | `3.5` |
| E10 | a3 | pass | — | 10 | 3 | 0 | 1 | 1 | `3.5` |

Aggregate protocol metrics across all 30 attempts:

- tool calls: **197** (mean 6.57, median 6)
- Focus queries: **51**
- simulations: **49**
- commit attempts: **15**
- successful commits: **12**
- expected stale commit rejections: **3**
- rejected simulations: **23**
- explicit agent proposal rejects: **9**
- protocol-input errors: **2**

## Exact committed-state evidence

There were 12 successful commit attempts: E1, E2, E3, and E10 each committed in all three repetitions.

**12/12 committed snapshots passed durable verification:** Typed Semantic IR projection, Execution IR lowering, C# emission, Release build, and execution all succeeded with exit code 0.

- E1 exact behavior: `3.75`, `3.75`, `3.75` — **3/3 exact oracle pass**.
- E2 execution outputs: `2`, `2`, `1`.
- E3 execution outputs: `42`, `1`, `1`.
- E10 execution outputs: `2`, `3.5`, `3.5`.

E2/E3/E10 intentionally do not pin one resulting literal/output in the frozen v1 oracle. Their varied outputs are therefore not scored post-hoc; the evidence requirement is a valid reviewed recovery commit whose exact committed state builds and executes.

## Semantic safety and authority findings

### Stable semantic IDs and system-owned production identities

The agent consistently used existing `FunctionId`, `ParameterId`, `SnapshotId`, and result `GraphNodeId` guards discovered through the bounded runner. No observed request attempted raw Snapshot/ValueGraph/SemanticNode mutation or caller-selected production IDs for newly materialized graph nodes.

For E1, all three successful reviews materialized the exact role order:

```text
ParameterReference
Float64Literal
Float64Add
```

with literal bits `4002000000000000`. Every new node ID was system-issued and disjoint from the removed body IDs.

### Guard recovery

E2 and E3 were **6/6 successful** across their two recovery classes. The agent first exercised the supplied stale/wrong guard, observed the intended structured diagnostic, re-inspected bounded state/Focus, and created a fresh proposal before publication.

### Scope/type/obligation safety

E4, E5, E8, and E9 were **12/12 successful**. Scope, primitive type, open-obligation protection, and resolved-obligation historical protection all rejected for the intended diagnostic class without semantic churn.

### Review/publication and stale recovery

E10 was **3/3 successful** on the frozen oracle. The original reviewed target proposal failed with `StaleBaseSnapshot` after the harness-owned unrelated publication; the agent then created and committed a fresh target proposal. `harnessPerturbationPreserved=true` in all three attempts.

The old stale proposal handle remained in the session (`pendingProposalCount=1`) in all E10 attempts. This is not a frozen oracle failure, but it is a repeated proposal-lifecycle/cleanup finding tracked separately.

### Unrelated semantic churn

**30/30 terminal runs recorded `zeroUnrelatedSemanticChurn=true`.**

This includes E10, where the harness-owned unrelated change is explicitly excluded from agent-owned churn and independently checked for preservation.

## Agent/tool ergonomics findings

### Deep recursive explicit JSON is the main weakness

The dominant failure mode is not semantic authority or review handling; it is constructing a very deep recursive recipe with an exact boundary depth. E6 consumed 36 tool calls across three attempts, including seven explicit self-rejected proposals, and never produced the exact requested depth-33 structure.

By contrast E7's balanced 33-leaf / 65-node recipe succeeded in all three attempts. Its first repetition needed two undersized retries, but the next two generated the correct 65-node request directly. The issue therefore appears especially acute for manual *deep-chain counting*, not for every large recursive recipe.

This is tracked in Issue #73; the frozen corpus is not changed.

### Hex Float64 transport is mostly reliable but not perfect

Two of 30 attempts (E9/a3 and E10/a3) initially sent decimal bit-pattern strings such as `4607182418800017408`/`4611686018427387904` where the protocol requires exactly 16 hexadecimal digits. Both received `ProtocolError`, corrected the request, and completed the task successfully.

This is a minor recoverable affordance issue rather than a systematic tranche failure.

### Stale proposal cleanup

All three E10 sessions finalized with one pending stale proposal handle. The semantic state was correct and the process terminated normally, but the repeated behavior suggests that proposal retirement semantics should be made explicit. Tracked in Issue #74.

## Answers to Issue #51 evaluation questions

- **Can the agent discover and construct the closed recursive recipe schema?** Yes for ordinary and large balanced recipes; exact deep-chain boundary construction is unreliable.
- **Does it use stable semantic IDs as authority?** Yes in all observed valid runs; no raw/new production-ID authority bypass was observed.
- **Does it recover from expected-base/result staleness?** Yes, 6/6 recovery attempts.
- **Does it respect parameter scope/type and obligation-protected-body diagnostics?** Yes, 12/12 relevant attempts.
- **Does it understand system-owned node identity?** Yes in the successful materialization/review evidence; newly materialized graph IDs remained system-owned.
- **Can it distinguish requested recipe from materialized structural review?** Yes in successful E1 and recovery proposals; reviews were inspected before commit.
- **Does it publish only the reviewed proposal and recover from stale commit rejection?** Yes, E10 3/3; stale-handle cleanup remains a UX finding.
- **Is unrelated semantic churn zero?** Yes, 30/30.
- **Does exact committed-state execution match the task oracle?** E1 3/3 exact; all 12 committed states built/executed successfully, with E2/E3/E10 scored according to their non-value-pinning frozen oracle.

## What this tranche supports

The tranche supports a Milestone-2 claim that the current **bounded Float64 complete-body structural-edit workflow** is usable by an isolated agent with stable semantic authority, structured review, stale-guard recovery, conservative scope/type/obligation rejection, system-owned node identities, exact reviewed publication, and zero unrelated semantic churn.

It also supports the controlled runner/harness as an evaluation mechanism after the v1.1 transport correction: all 30 valid attempts produced durable terminal evidence under one pinned model/runtime/tool/corpus condition.

## What this tranche does not support

It does **not** support claims of:

- arbitrary graph/expression editing;
- reliable exact construction of very deep recursive JSON recipes;
- List/higher-order structural editing;
- generic lambdas/closures;
- effectful List callbacks;
- semantic merge/rebase;
- proposal lifecycle behavior beyond the observed process-local condition;
- equivalence with M1 results under a different tool condition.

## Follow-ups

- Issue #73 — investigate exact depth-boundary recipe construction failures.
- Issue #74 — decide lifecycle/cleanup for stale reviewed proposals after rejected commit.
- Issue #70 — unrelated historical M1 OpenCode plugin compatibility issue observed under current runtime; not part of this M2 score.
- Issue #52 — use this evidence to decide whether a narrowly specialized List structural slice belongs in M2 or should be deferred.

No frozen corpus prompt/oracle/fixture was modified in response to these results.
