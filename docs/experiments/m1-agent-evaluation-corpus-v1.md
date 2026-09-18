# Milestone 1 agent evaluation corpus v1

**Status:** Experimental protocol — frozen before recorded runs

This document defines the first concrete task corpus for the Milestone 1 AI-agent evaluation. It turns the accepted [evaluation methodology](../plans/m1-agent-evaluation-plan-20260826.md) into versioned tasks and task oracles without changing the production editing surface merely to make the benchmark easier.

Corpus identifier: `m1-agent-corpus-v1`

The corpus is frozen once merged. If a task prompt, oracle, seed context, harness perturbation, or allowed operation changes after recorded runs begin, create a new corpus version rather than silently editing v1.

## Scope and current operation boundary

The ordinary `AgentWorkflow` write surface currently supports:

- renaming a type or function display name;
- adding a function parameter with explicit plans for known call sites;
- resolving a missing-argument obligation.

The language implementation is broader than that editing surface. In particular, List and Float64 semantics exist, but an ordinary agent cannot currently construct or replace arbitrary value-graph nodes through `AgentOperationIntent`.

Therefore this corpus distinguishes:

- **covered** — the task class can be exercised through the supported agent contract;
- **partial** — a relevant invariant can be exercised, but the stronger task described by the evaluation plan cannot;
- **blocked** — the current agent operation surface cannot express the required semantic edit.

A partial or blocked class must not be reported as covered merely because the underlying language feature exists.

## Common run contract

Unless a task overrides a rule, every Nyrwvy Mode A run uses these conditions:

1. Start from the recorded repository revision and a newly created fixture snapshot.
2. Give the agent only the supported agent-facing query/edit tools for the run condition. Do not expose raw `Snapshot` replacement or raw JSON/Nyason mutation.
3. Seed only the stable IDs explicitly listed by the task. The agent may discover further context through bounded queries.
4. Require simulation and Change Review before publication.
5. Treat the task oracle as reviewer-side truth. Do not reveal oracle-only implementation details to the agent unless they are explicitly part of the task prompt or seed context.
6. Record all attempts, including rejected proposals and tool-protocol failures.
7. Verify final readiness plus the task-specific semantic oracle. When the final state is executable, run the normal lowering/backend checks required by the task.
8. Count unrelated semantic churn by semantic entities/relations outside the task's permitted-change set, not by serialized line count.

For Mode B source-edit baselines, preserve the same user-visible task intent and behavioral oracle as far as practical. Differences caused by the baseline language/tooling must be recorded rather than normalized away.

## Runtime placeholders

The corpus uses symbolic placeholders instead of embedding fixture test IDs in the prose. The harness resolves them from the named fixture at run start and records the concrete values.

| Placeholder | Fixture binding |
| --- | --- |
| `${validate_user}` | `MilestoneZeroFixture.ValidateFunctionId` / the same function in `MilestoneOneFixture` |
| `${raw_user_input}` | `MilestoneZeroFixture.RawUserInputTypeId` |
| `${report_results}` | `MilestoneOneFixture.ReportResultsFunctionId` |
| `${m1_main}` | `MilestoneOneFixture.MainFunctionId` |
| `${main_report_results_call}` | `MilestoneOneFixture.MainReportResultsCallNodeId` |
| `${m1_main_unit}` | `MilestoneOneFixture.MainUnitNodeId` |
| `${main_validate_call}` | `MilestoneZeroFixture.MainValidateCallNodeId` |
| `${main_policy_value}` | `MilestoneZeroFixture.MainPolicyNodeId` |
| `${policy_type}` | `MilestoneZeroFixture.PolicyTypeId` |
| `${validate_users}` | `MilestoneOneFixture.ValidateUsersFunctionId` |
| `${validate_users_map}` | `MilestoneOneFixture.ValidateUsersMapNodeId` |

Concrete placeholder values are evidence, not secret test answers. Seed context is intentionally narrower than the full table for each task.

## E1 — Identity-preserving function rename

**Evaluation class:** identity-preserving change  
**Starting fixture:** `MilestoneZeroGraph.create()`  
**Seed context:** `${validate_user}` only

### Task prompt

> Rename the target function from `validate_user` to `validate_profile`. Preserve its semantic identity, behavior, callers, and all unrelated program state. Use the normal reviewed agent workflow.

### Permitted semantic change

- display name of `${validate_user}`: `validate_user` -> `validate_profile`.

No stable identity may be replaced.

### Oracle

A successful final state must satisfy all of the following:

- the function still has exactly the original `FunctionId`;
- Change Review reports the intended function rename and no unrelated semantic changes;
- direct callers still target the same `FunctionId`;
- snapshot and semantic fingerprints change as expected for the display-name semantics defined by the current fingerprint contract;
- the workspace is semantically ready;
- the representative program still lowers and executes with unchanged behavior where execution is part of the run harness.

Any recreation of the function under a new identity is `identity-loss`. Any extra semantic edit is candidate `unrelated-churn`.

## E2 — Resolvable cross-function signature evolution

**Evaluation class:** signature evolution / cross-function edit  
**Starting fixture:** `MilestoneOneGraph.create()`  
**Seed context:** `${report_results}` only

### Task prompt

> Add a required `context: Unit` parameter to the target `report_results` function. Update every affected call site using an already-existing compatible Unit value; do not manufacture an arbitrary replacement value or change unrelated functions. Review the complete resulting change before committing it.

### Expected discovery path

The agent may use bounded caller/focus queries to discover `${m1_main}`, `${main_report_results_call}`, and `${m1_main_unit}`. These values are not part of the initial seed.

### Permitted semantic changes

- one newly system-allocated parameter on `${report_results}`;
- one argument attachment at `${main_report_results_call}` using `${m1_main_unit}`;
- transaction/proposed-snapshot identities and expected audit/publication metadata.

### Oracle

- exactly one new parameter is added to `${report_results}` with display name `context` and type `Unit`;
- the parameter ID is system-issued, not supplied by the agent;
- the existing call from `${m1_main}` is repaired using the existing `${m1_main_unit}` value;
- no blocking obligation remains;
- `${report_results}` and its caller relation retain their existing stable identities;
- unrelated higher-order relations, including the `ListMap` callback from `${validate_users}` to `${validate_user}`, remain unchanged;
- the final workspace is semantically ready and the exact committed state lowers and executes successfully.

## E3 — Explicit incompleteness followed by obligation resolution

**Evaluation class:** signature evolution / explicit uncertainty  
**Starting fixture:** `MilestoneZeroGraph.create()`  
**Seed context:** `${validate_user}`, `${policy_type}`

### Task prompt

> Add a required `policy: Policy` parameter to `validate_user`. For the first reviewed change, do not guess or insert a call-site value: leave the affected caller explicitly unresolved and publish that incomplete state only through the policy that permits blocking obligations. Then inspect the resulting obligation and resolve it in a second reviewed change using an existing compatible value from the caller. Do not create a default policy value.

### Expected discovery path

The agent must discover the caller and its existing compatible policy value through bounded queries. `${main_validate_call}` and `${main_policy_value}` are oracle-side bindings, not initial seed context.

### Permitted semantic changes

Phase 1:

- one newly allocated `policy` parameter on `${validate_user}`;
- one newly allocated missing-argument obligation for `${main_validate_call}`.

Phase 2:

- resolution of that obligation with `${main_policy_value}`.

### Oracle

- the initial simulation is `Incomplete` with exactly the generated obligation expected for `${main_validate_call}`;
- ordinary conservative commit of the incomplete proposal is not sufficient; incomplete publication occurs only through the explicit `AllowBlockingObligations` policy;
- the agent does not fabricate a replacement/default value (`implicit-default` if it does);
- the second proposal resolves the existing obligation rather than allocating a substitute identity;
- final readiness is `SemanticallyReady` with no open obligation for the affected call;
- stable function/call identities are preserved and unrelated semantics do not change.

## E4 — Stable higher-order collection reference under rename

**Evaluation class:** collection-associated identity preservation  
**Coverage strength:** partial for the collection task class  
**Starting fixture:** `MilestoneOneGraph.create()`  
**Seed context:** `${validate_user}` only

### Task prompt

> Rename `validate_user` to `validate_account_input` without changing its behavior. It is used indirectly by collection-processing code; preserve all higher-order references and do not rewrite collection structure merely because the display name changes.

### Permitted semantic change

- display name of `${validate_user}` only.

### Oracle

- `${validate_user}` keeps its original `FunctionId`;
- `${validate_users_map}` remains a `ListMap` whose callback is that same `FunctionId`;
- `${validate_users}` retains its stable identity and graph structure;
- no List node is recreated or redirected;
- final readiness and execution behavior remain unchanged.

### Interpretation limit

This task demonstrates that a semantic rename preserves a stable higher-order callback reference. It **does not** test whether an agent can select `ListMap`, `ListFilter`, `ListAny`, or `ListAll` correctly when constructing new collection behavior, because the current ordinary agent operation surface cannot perform that structural edit.

Do not count E4 as evidence for `wrong-specialized-node` selection reliability.

## E5 — Stale reviewed proposal recovery

**Evaluation class:** stale review / conflicting state  
**Starting fixture:** `MilestoneZeroGraph.create()`  
**Seed context:** `${validate_user}` only

### Task prompt

> Rename the target function to `validate_profile` using the reviewed agent workflow. If publication is rejected because the workspace changed after your review, do not bypass review or reuse stale state: inspect the current workspace, create a fresh proposal, review it, and complete the requested rename.

### Deterministic harness perturbation

After the agent has produced and reviewed its first valid rename proposal but before that proposal is committed, the harness advances the same base workspace by committing an unrelated type-display rename on `${raw_user_input}`. The harness records this injected transaction separately and does not expose raw snapshot mutation to the agent.

The agent's first proposal must therefore fail closed as stale.

### Permitted semantic changes

Agent-owned:

- final rename of `${validate_user}` to `validate_profile`.

Harness-owned and excluded from agent churn scoring:

- the explicitly recorded unrelated `${raw_user_input}` display rename used to advance the workspace.

### Oracle

- publication of the pre-perturbation proposal is rejected as stale/mismatched;
- the agent does not attempt to bypass the reviewed receipt boundary;
- recovery starts from the advanced workspace and produces a fresh transaction/proposed snapshot/review;
- the final `${validate_user}` identity is unchanged and its display name is `validate_profile`;
- the harness-owned type rename remains present;
- no additional unrelated semantic changes occur.

A stale commit attempt is expected in this task and is not by itself a failure category; failure is recorded if the agent does not recover correctly or attempts to evade the review contract.

## Coverage matrix

| Evaluation-plan class | v1 status | Evidence |
| --- | --- | --- |
| Identity-preserving changes | Covered | E1, E4 |
| Signature evolution | Covered | E2, E3 |
| Cross-function structural edits | Partial | E2 exercises cross-function propagation, but arbitrary graph restructuring is not exposed |
| Collection edits | Partial / blocked for structural selection | E4 covers stable higher-order reference preservation only |
| Numeric edits | **Blocked** | No bounded AgentOperationIntent can construct/change Float64/Int64 expression structure |
| Stale review / conflicting state | Covered | E5 |

The M1.3 evaluation record must preserve these distinctions. In particular, `m1-agent-corpus-v1` alone is insufficient to claim numeric structural-edit coverage or specialized collection-node selection reliability.

## Blocked coverage and milestone consequence

The accepted evaluation methodology asks the final M1 evidence to exercise representative identity, signature, collection, stale-review, and numeric task classes where implemented. Numeric semantics and specialized collection semantics are implemented, but corresponding structural agent operations are not.

That is a real product/evaluation finding, not a benchmark inconvenience.

Before M1.3 is marked complete, one of the following must happen and be recorded explicitly:

1. add an appropriately bounded semantic editing operation that makes the blocked task class genuinely executable and introduce a new corpus version for those tasks; or
2. revise the evaluation requirement through an explicit design decision explaining why structural editing is outside the M1 agent claim.

Silently using E4 as a substitute for collection construction, or omitting numeric coverage without noting the gap, is not acceptable.

## Run evidence

Record each run with [the v1 run template](m1-agent-evaluation-run-template-v1.md). Raw transcripts may live outside the maintained docs when large, but the run record must identify where they are stored and preserve enough structured evidence for review.
