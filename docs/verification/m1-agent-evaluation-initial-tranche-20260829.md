# M1 agent evaluation initial autonomous tranche - 2026-08-29

**Status:** Protocol preflight, unqualified trials, and one controlled pre-integration condition-v2.1 sample per covered task. M1.3 remains pending.

This record reports every attempted run for the first `m1-agent-corpus-v1` tranche for Issue #36. It does not modify the frozen corpus, its oracle, the agent authority boundary, or the Milestone 1 plan status.

## Evaluation setup

- Corpus: `m1-agent-corpus-v1`.
- Mode: A - NyamNyam semantic workflow.
- Repository revision: `b0fe4639a91017ac19a1bd33ee70a6c3e691f713` (`feat/issue-34-agent-eval-runner`). This was the checked-out clean revision, not the later main revision named in Issue #36.
- Run date: 2026-08-29 (local timezone +09:00); per-run wall-clock duration was not captured by runner v1.
- Fixtures: fresh `m0` or `m1` exports from `NyamNyam.EvaluationFixtureExport` for each session.
- Runner/tool surface: `NyamNyam.AgentEval` at the repository revision above, JSON Lines stdin/stdout protocol (`state`, bounded queries, `simulate`, reviewed `commit`/`commitAllowIncomplete`, and `reject`).
- Protocol preflight operator: `openai/gpt-5.6-terra` in the evaluator session, runtime-default reasoning configuration. This operator had read the frozen corpus/oracles as required of the evaluator, so the preflight is not autonomous agent evidence.
- Autonomous trial agent model: unavailable. OpenCode's delegated-task runtime did not emit a model identifier or reasoning budget for individual agents, so same-model control cannot be verified.
- Provider/runtime: OpenCode API tool-calling session and fresh OpenCode delegated-task contexts.
- System/instruction prompt revision: default OpenCode instructions plus the runner command schema supplied to each trial; no task-specific oracle was supplied to fresh trial agents.
- Task prompt revision: frozen `m1-agent-corpus-v1` task text only. No corpus oracle was supplied to trial agents.
- Human intervention: `none` inside the fresh trial agents. Fixture export, runner startup, logging, and the E5 perturbation were harness infrastructure. The preflight was evaluator-directed and is explicitly excluded from autonomous scoring.
- Raw mutable snapshot, JSON/Nyason mutation, fixture edits, test edits, and the E5 harness perturbation command were not used in either preflight or trial transcripts. The trial tool restriction was instruction-enforced by the delegated runtime, not a separate operating-system sandbox.

The runner condition is named **condition v1**. The preflight and fresh-agent trial conditions are distinct because the preflight had evaluator oracle exposure and the trial model/configuration cannot be identified. Neither condition is a controlled same-model autonomous sample suitable for an M1.3 completion claim.

## Important validation limit

The v1 runner exposes the final workspace identity, fingerprints, Change Review diff, readiness, and bounded semantic queries. It does **not** expose a harness-owned verification/export command for lowering, backend build, or behavioral execution from the exact dynamically committed snapshot. The general implementation suite proves that path, including `AgentExecutionTests`, but cannot prove it for these UUIDv7-allocated run snapshots.

Consequently, the semantic portions of the frozen oracles below are mechanically verified. Oracle rows that require exact committed-state lowering/execution are `ambiguous`, not assumed to pass from agent prose or from the general test suite. This is tracked separately in [Issue #37](https://github.com/kw4r3n/nyamnyam-lang/issues/37). A subsequent runner condition must be versioned rather than retroactively changing condition v1.

## Run summary

| Run ID | Task | Attempts | Semantic outcome | Primary level | Exact lowering/execution evidence | Task-correct |
| --- | --- | ---: | --- | --- | --- | --- |
| `m1-v1-preflight-e1-a1` | E1 | 1 | scoped semantic pass; evaluator oracle exposure | excluded | unavailable in runner v1 | excluded |
| `m1-v1-preflight-e2-a1` | E2 | 1 | scoped semantic pass; evaluator oracle exposure | excluded | unavailable in runner v1 | excluded |
| `m1-v1-preflight-e3-a1` | E3 | interrupted | Phase 1 correct; evaluator closed stdin before Phase 2 | excluded | unavailable in runner v1 | no final result |
| `m1-v1-preflight-e3-a2` | E3 | 1 retry | scoped semantic pass; evaluator oracle exposure | excluded | unavailable in runner v1 | excluded |
| `m1-v1-preflight-e4-a1` | E4 | 1 | scoped semantic pass; evaluator oracle exposure | excluded | unavailable in runner v1 | excluded |
| `m1-v1-preflight-e5-a1` | E5 | 1 | scoped semantic pass; evaluator oracle exposure | excluded | unavailable in runner v1 | excluded |
| `m1-v1-trial-e1-a1` | E1 | 1 | final scoped semantic pass after runner restart | S3, unqualified | unavailable in runner v1 | ambiguous |
| `m1-v1-trial-e2-a1` | E2 | 1 | final scoped semantic pass after runner restart | S3, unqualified | unavailable in runner v1 | ambiguous |
| `m1-v1-trial-e3-a1` | E3 | 1 | scoped semantic pass after one malformed query | S3, unqualified | unavailable in runner v1 | ambiguous |
| `m1-v1-trial-e4-a1` | E4 | 1 | final scoped semantic pass after runner restart | S3, unqualified | unavailable in runner v1 | ambiguous |
| `m1-v1-trial-e5-a1` | E5 | 1 | stale rejection and fresh recovery pass | S4, unqualified | unavailable in runner v1 | ambiguous |

`S3/S4, unqualified` applies the frozen outcome definitions only to the runner-observable semantic path. It is not a claim that the trial met model-control or exact execution requirements. E3's intentional two-phase flow and E5's deterministic stale rejection do not themselves lower a success level; E1/E2/E4 had avoidable runner-session restarts and E3 had a `tool-protocol-error`.

## Protocol preflight records

### `m1-v1-preflight-e1-a1` - E1 identity-preserving function rename

- Starting fixture/state: `m0`, `snapshot-m0-initial`, snapshot fingerprint `sha256:c5bb800fc6c15fbee91e429e75630e90a53f0f21cd60a8be21a60aef68538aa4`, semantic fingerprint `sha256:1c04b4c278074bb10519a3a722373717b51100faf67d6e7aede295b9f87bf25e`.
- Seed supplied: `${validate_user}` = `function-validate-user`.
- Discovered by one `focusFunction`: caller `function-main` at `node-main-validate-call` still targets `function-validate-user`.
- Timeline: focus target; simulate `renameFunction(function-validate-user, validate_profile)`; review one function rename and `semanticallyReady`; commit `proposal-1`; query target and callers; final state.
- Review receipt: transaction `01a04c7f-d977-7999-815f-625c4671a62a`; proposed/published snapshot `01a04c7f-d977-7ab2-9e9f-511804a6cb05`.
- Final fingerprints: snapshot `sha256:074e64c69e196baad56fdb3c6430560721f9bf0ad4856461df526b960e008401`; semantic `sha256:1c04b4c278074bb10519a3a722373717b51100faf67d6e7aede295b9f87bf25e`.
- Oracle evidence: final function ID remained `function-validate-user`, display name is `validate_profile`, final caller still targets that ID, Change Review/publication has exactly one function rename, and readiness is `semanticallyReady`. The semantic fingerprint intentionally did not change for this presentation-only rename; the snapshot fingerprint changed.
- Failure categories: none.

| Metric | Value |
| --- | ---: |
| Semantic query count | 3 |
| Focus query count | 1 |
| Broader/whole-program retrieval count | 0 |
| Operation/simulation count | 1 |
| Failed/rejected proposals attributable to agent error | 0 |
| Protocol-required rejected/intermediate proposals | 0 |
| Commit attempts | 1 |
| Agent retries | 0 |
| Raw tool-call count | 7 |
| Input/output tokens | unavailable |
| Focused context bytes/entities retrieved | unavailable |
| Unrelated semantic entities changed | 0 |
| Compiler/test/lowering failures after accepted edits | unavailable in runner v1 |
| Wall-clock duration | unavailable |

Query sufficiency: level 1, one Focus slice; post-commit bounded queries were oracle verification, not broader discovery. Permitted change was the target function display name. Actual agent-owned unrelated change: none.

### `m1-v1-preflight-e2-a1` - E2 resolvable signature evolution

- Starting fixture/state: `m1`, `snapshot-m1-expanded-program`, snapshot fingerprint `sha256:c50f511116f59607e4e9089fbe3f2b69e3c9894a644c8c8b8fd001331d9d84f5`, semantic fingerprint `sha256:b3c5b18ea371ae6c49c95c76934b823dae1951d434b922cca2d22aec2b2d520c`.
- Seed supplied: `${report_results}` = `function-m1-report-results`.
- Discovery: `focusFunction` found `${m1_main}` = `function-m1-main` and `${main_report_results_call}` = `node-m1-main-report-results`; bounded `getFunction(function-m1-main)` found `${m1_main_unit}` = `node-m1-main-unit-context` as `UnitLiteral`.
- Timeline: focus target; inspect caller; simulate one `addFunctionParameter` request with `context: Unit` and a `bindExistingValue` call plan; review; commit `proposal-1`; query target, caller, and open obligations; final state.
- Review receipt: transaction `01a04c80-3716-7ba2-8275-20029c1da7f5`; proposed/published snapshot `01a04c80-3717-76ce-86dd-dc50a135892e`.
- Allocated parameter: `01a04c80-3718-76f6-a4b1-00a8a9b8dee0`, system-issued UUIDv7, display name `context`, type `Unit`.
- Final fingerprints: snapshot `sha256:cbad1a30c9c19164f486601c2174c6b7d5834a715776cdf5b05665738d6e86f1`; semantic `sha256:54fb8cdc54892873b17d218c57337897a1fcecf2c11932bd49259f55a9e3f92b`.
- Oracle evidence: the sole added parameter is on `function-m1-report-results`; the sole attached argument is at `node-m1-main-report-results` using `node-m1-main-unit-context`; final open obligations for `function-m1-main` are empty; review affects only `function-m1-main` and `function-m1-report-results`; no unrelated rename, higher-order callback, or collection relation appears in the diff.
- Failure categories: none.

| Metric | Value |
| --- | ---: |
| Semantic query count | 5 |
| Focus query count | 1 |
| Broader/whole-program retrieval count | 0 |
| Operation/simulation count | 1 |
| Failed/rejected proposals attributable to agent error | 0 |
| Protocol-required rejected/intermediate proposals | 0 |
| Commit attempts | 1 |
| Agent retries | 0 |
| Raw tool-call count | 9 |
| Input/output tokens | unavailable |
| Focused context bytes/entities retrieved | unavailable |
| Unrelated semantic entities changed | 0 |
| Compiler/test/lowering failures after accepted edits | unavailable in runner v1 |
| Wall-clock duration | unavailable |

Query sufficiency: level 2, Focus plus a bounded caller-body query. Permitted changes were the allocated parameter and one argument attachment. Actual agent-owned unrelated change: none.

### `m1-v1-preflight-e3-a1` - E3 interrupted attempt

- Starting fixture/state: the E3 `m0` initial state above.
- Seed supplied: `${validate_user}` = `function-validate-user`; `${policy_type}` = `type-validation-policy`.
- Completed event sequence: focus target; query policy type and caller body; simulate Phase 1 `policy: Policy` with `leaveMissing(node-main-validate-call)`; review reported `Incomplete` with exactly one generated obligation; publish through `commitAllowIncomplete`; query that obligation.
- Phase 1 receipt: transaction `01a04c80-685f-7132-9017-5c07192e7985`; published incomplete snapshot `01a04c80-685f-7ca2-8462-d41fed508967`; parameter `01a04c80-6860-78e6-ad12-6b3bdcc2dc22`; obligation `01a04c80-6861-7c20-987a-2cbc5004bc81` for `node-main-validate-call`.
- End state: `Incomplete`; the evaluator sent EOF before the required Phase 2 command could be delivered. No semantic error, malformed protocol command, human repair, or unsafe write occurred.
- Classification: infrastructure-interrupted attempt, not an S0-S4 result. It is retained because it is a genuine attempted session, but it is not evidence of an autonomous model failure. `m1-v1-e3-a2` restarted from a new fresh fixture under the unchanged condition.
- Failure categories: none; do not misclassify evaluator EOF as `tool-protocol-error`.

| Metric before interruption | Value |
| --- | ---: |
| Semantic query count | 4 |
| Focus query count | 1 |
| Operation/simulation count | 1 |
| Commit attempts | 1 |
| Raw tool-call count | 8 |
| Agent retries | 0 |

### `m1-v1-preflight-e3-a2` - E3 explicit incompleteness and resolution

- Starting fixture/state: the E3 `m0` initial state above.
- Seed and discovery: identical to `m1-v1-e3-a1`; the caller body established `node-main-policy` as the existing compatible `Policy` value.
- Timeline: simulate Phase 1 leave-missing request; review `Incomplete` with one obligation; `commitAllowIncomplete(proposal-1)`; inspect open obligation; simulate `resolveMissingArgument` using that same obligation ID and `node-main-policy`; review; commit `proposal-2`; query empty open obligations and final target; final state.
- Phase 1 receipt: transaction `01a04c81-30b9-7619-8eed-f56b9a9c280e`; incomplete snapshot `01a04c81-30ba-7eda-ac04-2be1c42e68bf`; parameter `01a04c81-30bb-73d5-ae06-6e5e84017636`; obligation `01a04c81-30bb-7865-9f4c-3017149ef269` at `node-main-validate-call`.
- Phase 2 receipt: transaction `01a04c81-3139-76d7-9209-b26c3a5b4872`; final snapshot `01a04c81-3139-7f76-8011-5579078588cc`.
- Final fingerprints: snapshot `sha256:674c8de8f271f30e8dd1b688fb29596992e0735f2be0351a1d26b93eba4912ea`; semantic `sha256:8324c099a823199ebab5624edabd0e20418bd3a092b18d7f49887c4f050600b2`.
- Oracle evidence: Phase 1 review/publish has exactly the allocated parameter and matching open obligation; Phase 2 resolves that same obligation, attaches the existing `node-main-policy`, and leaves no open obligation. Final readiness is `semanticallyReady`; no default value or substitute obligation identity was created.
- Failure categories: none.

| Metric | Value |
| --- | ---: |
| Semantic query count | 6 |
| Focus query count | 1 |
| Broader/whole-program retrieval count | 0 |
| Operation/simulation count | 2 |
| Failed/rejected proposals attributable to agent error | 0 |
| Protocol-required rejected/intermediate proposals | 1 incomplete publication |
| Commit attempts | 2 |
| Agent retries | 0 |
| Raw tool-call count | 12 |
| Input/output tokens | unavailable |
| Focused context bytes/entities retrieved | unavailable |
| Unrelated semantic entities changed | 0 |
| Compiler/test/lowering failures after accepted edits | unavailable in runner v1 |
| Wall-clock duration | unavailable |

Query sufficiency: level 2, Focus plus bounded type/caller/obligation queries. Permitted changes were the allocated parameter, generated obligation, and its resolution. Actual agent-owned unrelated change: none.

### `m1-v1-preflight-e4-a1` - E4 stable higher-order collection reference

- Starting fixture/state: the E2 `m1` initial state above.
- Seed supplied: `${validate_user}` = `function-validate-user`.
- Discovery: Focus showed the higher-order caller `${validate_users}` = `function-m1-validate-users` at `${validate_users_map}` = `node-m1-validate-users-map`; bounded `getFunction` confirmed `ListMap(node-m1-validate-users-inputs, function-validate-user)`.
- Timeline: focus target; inspect `validate_users`; simulate `renameFunction(function-validate-user, validate_account_input)`; review; commit `proposal-1`; query target and `validate_users`; final state.
- Review receipt: transaction `01a04c81-5bb8-71a6-b750-55a96c450d79`; proposed/published snapshot `01a04c81-5bb9-72f8-81f1-7f0c7313cb97`.
- Final fingerprints: snapshot `sha256:17a18053ad13977b85596208706d98f5ce175c97d0976b67e3499de5ee68dfb6`; semantic `sha256:b3c5b18ea371ae6c49c95c76934b823dae1951d434b922cca2d22aec2b2d520c`.
- Oracle evidence: `function-validate-user` remains the target ID with display name `validate_account_input`; `function-m1-validate-users` retains its ID and `node-m1-validate-users-map` remains `ListMap` with callback `function-validate-user`; the review/publication diff contains only the target rename and readiness is `semanticallyReady`.
- Failure categories: none.

| Metric | Value |
| --- | ---: |
| Semantic query count | 4 |
| Focus query count | 1 |
| Broader/whole-program retrieval count | 0 |
| Operation/simulation count | 1 |
| Failed/rejected proposals attributable to agent error | 0 |
| Protocol-required rejected/intermediate proposals | 0 |
| Commit attempts | 1 |
| Agent retries | 0 |
| Raw tool-call count | 8 |
| Input/output tokens | unavailable |
| Focused context bytes/entities retrieved | unavailable |
| Unrelated semantic entities changed | 0 |
| Compiler/test/lowering failures after accepted edits | unavailable in runner v1 |
| Wall-clock duration | unavailable |

Query sufficiency: level 2, Focus plus the bounded higher-order caller query. Permitted change was the target display name only. Actual agent-owned unrelated change: none.

This is partial collection coverage only. It demonstrates reference stability under rename, not an agent's ability to select or construct `ListMap`, `ListFilter`, `ListAny`, or `ListAll`.

### `m1-v1-preflight-e5-a1` - E5 stale reviewed proposal recovery

- Starting fixture/state: the E1 `m0` initial state above.
- Seed supplied: `${validate_user}` = `function-validate-user`.
- Harness configuration: `--stale-perturbation-trigger-function-id function-validate-user --stale-perturbation-trigger-name validate_profile --stale-perturbation-type-id type-raw-user-input --stale-perturbation-name RawAccountInput`. These options are process startup infrastructure and are not protocol commands; the advance occurs only after the first valid reviewed target rename proposal.
- Timeline: focus target; simulate rename `proposal-1`; harness commits its type rename after review; agent commits `proposal-1` and receives `StaleBaseSnapshot`; agent queries the changed type and fresh focus; simulates fresh `proposal-2`; reviews and commits it; queries final target/callers; final state.
- Agent proposal 1 receipt: transaction `01a04c81-9da0-76d4-854f-5ac2f2b2a31c`; base `snapshot-m0-initial`; commit rejected with `StaleBaseSnapshot`.
- Harness event: committed transaction `01a04c81-9de8-7715-8894-90bfddb07cba`, published snapshot `01a04c81-9de8-744c-969b-5f723a52064c`, renaming `type-raw-user-input` to `RawAccountInput`.
- Fresh agent receipt: transaction `01a04c81-9e0e-7bb8-9c6b-7aa0beed7c31`; base is the harness-published snapshot; proposed/published snapshot `01a04c81-9e0e-7dd8-8253-3a4f4a00f4bd`.
- Final fingerprints: snapshot `sha256:e9c415a05f524da8d9829d1512f1e61f1ee9d5886c74ee9bd44ee1005ad1a24d`; semantic `sha256:1c04b4c278074bb10519a3a722373717b51100faf67d6e7aede295b9f87bf25e`.
- Oracle evidence: first commit failed closed; second review receipt is bound to the advanced workspace; final `function-validate-user` identity is unchanged with display name `validate_profile`; the harness-owned `RawAccountInput` rename remains; final agent publication adds only the function rename; readiness is `semanticallyReady`.
- Failure categories: none. The stale rejection is an expected protocol transition, not `stale-review` failure because the agent did not try to bypass the receipt boundary.

| Metric | Value |
| --- | ---: |
| Semantic query count | 5 |
| Focus query count | 2 |
| Broader/whole-program retrieval count | 0 |
| Operation/simulation count | 2 |
| Failed/rejected proposals attributable to agent error | 0 |
| Protocol-required rejected/intermediate proposals | 1 stale commit rejection |
| Commit attempts | 2 |
| Agent retries | 1 protocol-required fresh recovery |
| Raw tool-call count | 11 |
| Input/output tokens | unavailable |
| Focused context bytes/entities retrieved | unavailable |
| Unrelated semantic entities changed | 0 agent-owned |
| Compiler/test/lowering failures after accepted edits | unavailable in runner v1 |
| Wall-clock duration | unavailable |

Query sufficiency: level 2, fresh Focus plus a bounded type query after the injected state change. Permitted agent change was the target function rename; the harness type rename is separately recorded and excluded from agent churn. The runner retained stale `proposal-1` as a pending opaque handle after the fresh commit; this did not alter semantic state, but is a runner lifecycle UX finding.

## Fresh autonomous-agent trial records

Each trial agent had a fresh context containing only its task prompt, seed stable IDs, the runner command/schema, and the Mode A restriction. It was explicitly prohibited from reading the repository, corpus, oracle, tests, fixtures, or serialized snapshot. Its recorded Bash command transcript only started the runner and sent JSON Lines commands. The delegated-task runtime did not reveal its model or reasoning configuration, so these are unqualified trials rather than controlled evaluation results.

### `m1-v1-trial-e1-a1` - E1

- Protocol events: `getFunction`, a duplicate `getFunction`, and rename `simulate` were sent to an initial runner process; it returned a valid uncommitted proposal and then that process ended. The agent started a fresh runner, queried the target, simulated the same rename, committed `proposal-1`, queried `state`, then queried `metrics`.
- Committed receipt: transaction `01a04c87-8c8d-78f2-a416-bd18724e52f8`; final snapshot `01a04c87-8c8e-7d3f-9fa8-b7140b2bb00b`; snapshot fingerprint `sha256:c4646436748cd78e7069b77c95d5e61a3349077ea3af3c5d3f3246e44d7aa76e`; semantic fingerprint `sha256:1c04b4c278074bb10519a3a722373717b51100faf67d6e7aede295b9f87bf25e`.
- Runner evidence: final review/publication reported only the requested function rename and no pending proposals. The trial did not query callers after commit, so caller preservation is not independently observed in this trial.
- Outcome: S3, unqualified. The initial process abandonment is an avoidable tool/session handling retry; classify `tool-protocol-error`. No semantic failure or unrelated churn was observed.
- Observed cross-session counts: 8 raw calls, 3 semantic queries, 0 Focus queries, 2 simulations, 1 commit, 1 retry. The final session's own `metrics` response was 5 calls, 1 query, 1 simulation, and 1 commit.

### `m1-v1-trial-e2-a1` - E2

- Protocol events: `getFunction(report_results)`, `getCallers`, and `getFunction(m1_main)` discovered `function-m1-main`, `node-m1-main-report-results`, and `node-m1-main-unit-context`. The agent simulated the correct add-parameter operation in an initial process, then restarted the runner, re-simulated it, committed `proposal-1`, and queried final state, target, caller, and metrics.
- Committed receipt: transaction `01a04c88-720f-7fe7-935b-b30da4e774a3`; parameter `01a04c88-7211-7f46-8e55-3eb3163235b4`; final snapshot `01a04c88-7210-71d5-8e74-944cbe48d715`; snapshot fingerprint `sha256:48dbf9c7954dc5bb2b140748c8bf3acd7469d1208c597372f1910147300f28e3`; semantic fingerprint `sha256:6099d4935ccd4c7cbdac47fbf553128fcf4f43699134004224d6dda35bc623af`.
- Runner evidence: reviewed/publication diff has one `context: Unit` parameter and one attachment using `node-m1-main-unit-context`; final target and caller queries confirmed both. The agent reported one local `Bad file descriptor` while reusing a closed coprocess; it sent no JSON command and changed no runner state.
- Outcome: S3, unqualified. Classify the abandoned first proposal/session handling as `tool-protocol-error`; no semantic failure or unrelated churn was observed.
- Observed cross-session counts: 10 raw calls, 5 semantic queries, 0 Focus queries, 2 simulations, 1 commit, 1 retry. The final session's `metrics` response was 6 calls, 2 queries, 1 simulation, and 1 commit.

### `m1-v1-trial-e3-a1` - E3

- Protocol events: `getFunction(validate_user)`, `getCallers`, `getFunction(main)`, and `getType(policy)` discovered the call and existing policy value. The first reviewed proposal added a generated policy parameter and generated missing-argument obligation. The agent queried `getOpenObligations` without the required `functionId`, received a `ProtocolError`, then correctly queried the caller after `commitAllowIncomplete`, resolved that exact generated obligation with `node-main-policy`, committed `proposal-2`, and verified empty open obligations.
- Phase 1 receipt: transaction `01a04c90-8c5a-7b39-b490-d8a2e403d7df`; parameter `01a04c90-8c5c-7850-a677-b9ddf20cd6b6`; obligation `01a04c90-8c5c-7709-b169-7a34dac50bca`; incomplete snapshot `01a04c90-8c5b-7d97-8c72-fbee5a5593b9`.
- Final receipt/state: transaction `01a04c91-1f75-73b5-bf1e-3716f0a4cff9`; final snapshot `01a04c91-1f75-78b5-91ca-071830c43830`; snapshot fingerprint `sha256:133740675ab14d289b45dd71dd842cd2981257585f3a12c605883fb3e1380615`; semantic fingerprint `sha256:9c55b5f34b9b17763232445cb5cdef1cfe1bdcce0eaad5b1fd9ea1bc5ca38ce0`.
- Runner evidence: Phase 1 was explicitly incomplete, only `commitAllowIncomplete` published it, Phase 2 resolved the same obligation with the existing policy node, and final open obligations were empty. No default value was created.
- Outcome: S3, unqualified. `tool-protocol-error` applies to the malformed obligation query; it was recovered without a semantic error. Intentional incompleteness is not an error category.
- Counts: 17 raw calls, 11 semantic queries, 0 Focus queries, 2 simulations, 2 commits, 1 retry, 1 protocol error, and 0 unrelated semantic entities changed.

### `m1-v1-trial-e4-a1` - E4

- Protocol events: `getFunction(validate_user)`, `getCallers`, and `getFunction(function-m1-validate-users)` discovered `node-m1-validate-users-map` as `ListMap(..., function-validate-user)`. The agent simulated a rename in an initial process, started a fresh runner and simulated it again, queried state, committed `proposal-1`, then queried the target, collection function, callers, and metrics.
- Committed receipt: transaction `01a04c87-c629-79bc-8e71-88f2fb256063`; final snapshot `01a04c87-c62b-78ad-8bbb-95af53692f28`; snapshot fingerprint `sha256:bdf808acc443954f2b183d478d07a5cdf8f7d160743a64c7316ba38c000ac438`; semantic fingerprint `sha256:b3c5b18ea371ae6c49c95c76934b823dae1951d434b922cca2d22aec2b2d520c`.
- Runner evidence: final target display name is `validate_account_input`; final `ListMap` still targets `function-validate-user`; review/publication diff contains only the rename.
- Outcome: S3, unqualified. Classify the abandoned first proposal/session handling as `tool-protocol-error`; no semantic failure or unrelated churn was observed.
- Observed cross-session counts: 12 raw calls, 6 semantic queries, 0 Focus queries, 2 simulations, 1 commit, 1 retry. The final session's `metrics` response was 8 calls, 3 queries, 1 simulation, and 1 commit.

This remains partial collection coverage only. It does not exercise collection-node selection or construction.

### `m1-v1-trial-e5-a1` - E5

- Protocol events: query target and state; simulate rename `proposal-1`; commit receives `StaleBaseSnapshot`; inspect advanced state and target; simulate fresh `proposal-2`; commit; query final target and metrics.
- Agent proposal 1 receipt: transaction `01a04c88-0ed0-7f2a-ae74-bd67975cc5da`; it was rejected as stale.
- Fresh receipt/state: transaction `01a04c88-0f5d-70b4-a8c9-e6f3609111ff`; final snapshot `01a04c88-0f5d-7415-96c4-f3e3bed77497`; snapshot fingerprint `sha256:9a745cd3e4e0cbaacbb5dd81e8f88e5ef7d4926fadc773bd3b378ff210b2aec1`; semantic fingerprint `sha256:1c04b4c278074bb10519a3a722373717b51100faf67d6e7aede295b9f87bf25e`.
- Runner evidence: the fresh proposal was reviewed/committed after the stale rejection and the final function name is `validate_profile`. The agent did not query the harness-mutated type or return its harness event, so the frozen oracle's requirement that the injected type rename remains is not independently observed in this trial.
- Outcome: S4, unqualified for the stale-recovery path. The stale rejection and fresh recovery are protocol-required, not an avoidable error. Final type-rename and lowering/execution oracle rows are ambiguous; no unrelated agent-owned change appears in the published diff.
- Counts: 11 raw calls, 3 semantic queries, 0 Focus queries, 2 simulations, 2 commits, 1 protocol-required recovery, no malformed protocol command, and one pending stale handle after final commit.

## Aggregate overhead and churn

The evaluator-directed preflight completed the five semantic task paths with 47 raw tool calls, 23 semantic queries, 6 Focus queries, 7 simulations, and 7 agent commit attempts. Its E3 interruption separately used 8 calls, 4 queries, 1 simulation, and 1 intended incomplete commit. These are protocol feasibility measurements only, not autonomous-model metrics.

The five fresh-agent trials observed 58 raw protocol calls, 28 semantic queries, 0 Focus queries, 10 simulations, and 7 commits. Three agents abandoned a reviewed proposal by ending a runner process, and E3 made one malformed query; record four `tool-protocol-error` occurrences across the five trials. All final published semantic diffs were scoped, with zero observed agent-owned unrelated semantic entities. E5's harness-owned type rename is excluded from agent churn but was not independently queried by the trial agent.

Token input/output counts, focused context byte/entity counts, exact model/reasoning configuration, and wall-clock durations are unavailable. Runner v1 does not emit the first three metric classes; the delegated-task runtime does not expose the model/configuration. Do not infer token efficiency, reproducibility, or same-model reliability from this record.

## Runner and query UX findings

### Keep

- The bounded operation surface prevented raw graph/snapshot edits and system-allocated parameter/obligation identities correctly.
- `focusFunction` exposed both direct and higher-order callers in the evaluator-directed preflight. Fresh trial agents instead used smaller `getCallers` and `getFunction` queries, which still exposed E4's `ListMap` relation without serialization access.
- Change Review made the expected scoped diffs, readiness, receipts, and generated identities inspectable before commit.
- E3's explicit `commitAllowIncomplete` boundary and E5's stale receipt rejection behaved as intended; no bypass path was needed.

### Change

- Add harness-owned exact committed-state validation/export evidence, tracked in #37. Without it, final lowering/build/execution oracle rows cannot be mechanically closed for a recorded session.
- Make runner metric output include focused entity/byte counts and durable per-run event artifacts. The current counters cover calls but not context cost.
- Consider automatically invalidating or explicitly retiring a proposal after stale commit rejection. E5's final state retained stale `proposal-1`, which is harmless but leaves avoidable protocol state for clients to manage.
- Provide a supported persistent client or session protocol adapter for multi-step agent integrations, tracked in [Issue #38](https://github.com/kw4r3n/nyamnyam-lang/issues/38). Three fresh agents abandoned a reviewed proposal when their shell driver restarted the line-oriented process; this is a tool-integration failure, not semantic reasoning evidence.

### Investigate

- E2 and E3 required bounded caller-body queries to locate compatible value nodes. Focus was sufficient in the preflight but unused by fresh trial agents; later controlled runs should compare the query choices before expanding Focus closure.
- The JSON type-ref grammar is precise but not self-describing through a discovery command. E3's omitted `functionId` on `getOpenObligations` produced a recoverable `tool-protocol-error`; later independent runs should measure schema ergonomics across providers.
- A process/session driver must keep stdin open for multi-phase tasks. The preflight E3-A1 was evaluator interruption; fresh E1/E2/E4 trials also restarted a session after simulation. Future orchestration should treat a recorded run as terminal only after the task's required final state is observed.

## Blocked coverage

E4 is not evidence for specialized collection-node selection. The ordinary `AgentOperationIntent` surface cannot construct or replace arbitrary List graph structure, so `wrong-specialized-node` reliability remains untested.

Numeric structural editing is also blocked. `Int64` and `Float64` semantics exist, but no ordinary bounded operation can construct or alter their expression structure. The corpus's numeric task class is therefore unexercised. This is a product/evaluation limitation, not a reason to reinterpret E4 as collection-construction coverage.

Per the frozen corpus, M1.3 requires either a bounded structural editing operation plus a new corpus version, or an explicit design decision that structural editing lies outside the M1 agent claim. Neither has happened.

## M1.3 decision

**Pending.** The protocol preflight and fresh-agent E1-E5 trials are recorded. The runner-observable semantic outcomes show that the bounded operations can support explicit obligation and stale-review recovery, but there is not yet a qualifying controlled autonomous tranche. M1.3 cannot be marked complete because:

1. exact committed-state lowering/backend/behavioral evidence is missing from condition v1 (#37);
2. the fresh trial agents' model identifier and reasoning configuration were not emitted, so the required same-model condition cannot be verified;
3. E1/E2/E4 and E3 exposed runner-client/session/schema protocol friction that must be measured or improved before interpreting semantic reliability;
4. only one trial per task is insufficient for the methodology's stochastic-agent conclusions;
5. structural List selection and numeric structural editing remain blocked; and
6. the required explicit decision on the blocked structural-edit coverage has not been made.

The immediate next step is to resolve #37 and #38 in a separately versioned runner condition, then run controlled autonomous samples without rewriting or deleting this evidence.

## Condition v2 readiness

Infrastructure commit `99189bb` implements the runner-side portions of #37 and #38 without changing the frozen corpus or ordinary agent mutation authority.

- `--artifact-dir` creates a unique durable run directory with JSONL events, atomic metrics, run metadata, and terminal run state. Harness-provided model, runtime, reasoning, prompt, and tool revisions are recorded, or explicitly `unavailable`.
- `--verification-fixture m0|m1` requires an artifact directory and runs only after a semantically-ready reviewed commit. It verifies the exact committed workspace through Typed Semantic IR, Execution IR, C# emission/build, and fixture behavior; detailed artifacts remain harness-only rather than agent-visible.
- E5 perturbation now triggers only when the effective review diff is exactly one net target rename and records its structured before/after receipt, readiness, and semantic diff only in harness artifacts/operator session data; multi-operation requests do not trigger it, and advance failures invalidate the run and discard the triggering proposal. Agent-visible metrics omit harness events and receipts.
- Artifact persistence failure invalidates the run record without hiding the semantic commit result. Finalization makes the terminal artifact state explicit, rejects later commands, and returns a nonzero runner exit when final artifact status is not durable and terminal.

At the time of this readiness note, no condition-v2 model run had been recorded. The selected #38 direction is the provider-specific OpenCode persistent client/agent tool described in [condition v2](../experiments/m1-agent-evaluation-condition-v2.md). It preserves opaque proposal state in one OpenCode-owned runner process and deliberately does not add cross-process checkpoint/resume authority.

## Controlled condition v2 attempts and v2.1 pre-integration tranche

The following runs use the persistent OpenCode adapter and fresh fixtures. The v2.1 runs occurred at historical branch revision `41d60b4` before the infrastructure reached `main`; that revision is preserved locally by `eval-condition-v2.1-pre-rebase-20260830`. They are controlled pre-integration evidence, not the post-main-integration fixed-revision tranche. All artifacts are durable, terminal, and private under `/home/kw4/.local/share/nyamnyam/evaluation-runs/`; each cited session directory contains `events.jsonl`, metrics, metadata, final state, and, after a ready commit, exact committed-workspace Typed Semantic IR, Execution IR, generated C#, compiler/execution outputs, and `verification.json`.

All runs requested and recorded model `openai/gpt-5.6-terra`, provider/runtime `opencode`, prompt revision `m1-agent-corpus-v1`, and the stated tool revision. OpenCode did not report a reasoning configuration, so it is recorded as `unavailable` rather than inferred.

### Superseded condition v2 attempts

| Run ID | Task | Tool revision | Terminal result | Classification |
| --- | --- | --- | --- | --- |
| `m1-v2-e1-a1` | E1 | `condition-v2-014f018` | committed and exact verification passed; 9 protocol errors | S3; retain, not comparable to v2.1 |
| `m1-v2-e2-a1` | E2 | `condition-v2-014f018` | no commit; 38 protocol errors, one rejected simulation, two reject actions | S0 / `tool-protocol-error` |

`m1-v2-e1-a1` is session `ses_fb23e0925ffew6fkaIo4Cz2tW5-1788010955794-f4427311-7f21-42fa-b410-3ba3155b4b0a/session-7b7c46ef334e454d89452051e8d97d59`. It reached a verified E1 result but guessed unsupported request schemas before the valid rename. `m1-v2-e2-a1` is session `ses_fb23ca45dffefoa6RosuvI6aLT-1788011049178-4f369b31-21b4-4d53-91a4-0bd6fdcc3849/session-ec1958cc3e8947aa8f4ad33f27253c80`; it never reached a valid proposal. These attempts exposed missing task-independent JSON request/operation schema in the tool instructions. Commit `41d60b4` introduced that schema and named the resulting condition v2.1; the corpus, task prompts, seed IDs, oracles, fixture choice, and mutation authority did not change.

### Condition v2.1 pre-integration summary

| Run ID | Task | Fixture | Primary level | Verification | Calls / queries / simulations / commits | Protocol errors |
| --- | --- | --- | --- | --- | ---: | ---: |
| `m1-v2.1-e1-a1` | E1 | `m0` | S4 | passed | 3 / 1 / 1 / 1 | 0 |
| `m1-v2.1-e2-a1` | E2 | `m1` | S4 | passed | 5 / 3 / 1 / 1 | 0 |
| `m1-v2.1-e3-a1` | E3 | `m0` | S4 | passed | 8 / 4 / 2 / 2 | 0 |
| `m1-v2.1-e4-a1` | E4 | `m1` | S4 | passed | 3 / 1 / 1 / 1 | 0 |
| `m1-v2.1-e5-a1` | E5 | `m0` | S4 | passed | 6 / 1 / 2 / 1 | 0 |

E5's one rejected commit is the required stale-review transition, not an avoidable retry. Its metrics record one rejection and two commit attempts.

### `m1-v2.1-e1-a1` - identity-preserving rename

- Artifact session: `ses_fb2394cf6ffeQwRdRTEyLkxBoi-1788011266038-419f4d37-ab17-4cb0-9f21-65dddbd6ff9a/session-29d6dba4a1c74e32851f2cb964898537`.
- The agent queried `function-validate-user`, reviewed one `renameFunction` to `validate_profile`, and committed it. The review/publication changed only that function display name; the original function identity was retained.
- Exact committed-workspace verification passed with transaction `01a04dc7-207e-78f5-8860-e1123fda013b`. Final readiness was semantic-ready, the semantic fingerprint remained `sha256:1c04b4c278074bb10519a3a722373717b51100faf67d6e7aede295b9f87bf25e`, and the snapshot fingerprint changed to `sha256:4c5d0c8882057ea67320d878c136abe725bf012936161f9a5218a69ce9203ed5`.

### `m1-v2.1-e2-a1` - resolvable signature evolution

- Artifact session: `ses_fb238993bffekzni6A9s7NTf7c-1788011312642-96134896-b2b7-4a85-adca-4b66e402df9f/session-e67ea0bd165f4cd5b0f8e24d0b5bf8f1`.
- The agent queried the target, its callers, and `function-m1-main`; it reviewed and committed one `context: Unit` parameter plus the sole `node-m1-main-report-results` attachment using existing `node-m1-main-unit-context`.
- The review was scoped to the target/caller relationship with no open obligation. Exact committed-workspace verification passed for transaction `01a04dc7-a829-7574-bb53-649d529ba5d3`.

### `m1-v2.1-e3-a1` - explicit incompleteness and resolution

- Artifact session: `ses_fb237f9e6ffeupDV8jBAYjNeWc-1788011353300-a8801563-17bb-4c1d-a636-7457c2ad5b51/session-cd90ba2c6bba4157ad022f0608bf18e9`.
- The first reviewed change added `policy: Policy` with `leaveMissing(node-main-validate-call)` and was published through `commitAllowIncomplete`. The second change queried the generated obligation and resolved that same obligation with existing `node-main-policy`; it did not manufacture a default.
- The final workspace had no open obligation and exact committed-workspace verification passed for the phase-two transaction `01a04dc8-68eb-76e2-831b-85237188d4e1`.

### `m1-v2.1-e4-a1` - stable higher-order reference

- Artifact session: `ses_fb23754bbffedkpTWzoBYJ14HM-1788011395296-adaa9e31-b655-4a6c-8a37-a35c55644da1/session-24ccc7b1df3b4c5d96208a8cfcdc9be8`.
- The only reviewed/publication change renamed `function-validate-user` to `validate_account_input`. Stable callback identity therefore preserves the existing `ListMap` relation; no collection operation or unrelated structural change occurred.
- Exact committed-workspace verification passed for transaction `01a04dc8-de01-7da1-8d27-e7cf7fa817bb`. As required by the corpus, this remains partial collection coverage, not evidence for specialized collection-node construction/selection.

### `m1-v2.1-e5-a1` - stale reviewed proposal recovery

- Artifact session: `ses_fb236cebeffeyKRcDXdbH3n3ku-1788011429738-46c1bbb0-f176-42cf-bcec-9af1a93d7667/session-3e4f08adcbd94b5ab16e2d4c342275ff`.
- After the first valid reviewed target rename, the harness committed its configured `RawUserInput` -> `RawAccountInput` type-display perturbation. The first agent commit failed closed with `StaleBaseSnapshot`.
- The agent queried current state, created/reviewed a fresh rename proposal, and committed it. The harness receipt records the perturbation transaction `01a04dc9-5af9-764f-9a6c-0776b9ad4267`; its semantic fingerprint was unchanged and the type rename remains in the advanced base. The agent's fresh transaction was `01a04dc9-7d92-70b5-be58-977ac11c85f0`.
- Exact committed-workspace verification passed. The agent-owned final diff is only the requested function rename; the harness-owned type rename is excluded from churn.

## Controlled tranche interpretation

The controlled pre-integration v2.1 sample supplies one terminal, exact-verified result for every corpus task that the current bounded operation surface can express. It also removes the v1 shell-session restart confounder and v2 schema-guessing condition from those samples. It is not a basis for an M1.3 completion claim: it predates the post-main-integration fixed-revision condition, has one sample per task, records no runtime reasoning configuration, and leaves the frozen structural List/numeric coverage decision unresolved.
