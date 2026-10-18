# M2 structural agent evaluation condition v1

**Status:** Frozen historical condition at `6cbf13695fa90b1311ae7ece7da403f7cbeafe05`, but **invalid for recorded model evaluation** after the first real OpenCode Stage A attempt exposed a tool-discovery failure. See [local condition verification](../verification/m2-controlled-evaluation-condition-v1-20260830.md), [runtime failure record](../verification/m2-controlled-evaluation-condition-v1-runtime-failure-20260830.md), and the [v1.1 remediation](m2-agent-evaluation-condition-v1.1.md).

> The evaluation runners, OpenCode bridges, scripts and corpora this document names were removed from `main` on 2026-10-18. Replay them from the [evaluation freeze tags](../operations/evaluation-freeze-tags.md) or from commit `b3a958a`, the last `main` commit that contained them.

This document defines the original controlled execution condition for the already frozen `m2-agent-corpus-v1`. It does not modify the corpus, its task oracles, or the agent-visible `m2-structural-v1` protocol.

Condition v1 passed repository-local .NET/Bun verification, but that verification did not prove that plugin-registered custom tools appeared in a real OpenCode `run` model tool list. In the first corrected Stage A collection, all ten OpenCode sessions loaded this agent's instructions but none exposed a callable `nyrwvy_structural_eval_*` tool; no harness run directory was created. Those attempts are retained as invalid-condition evidence and are not semantic task results.

## Frozen inputs

- Corpus: `m2-agent-corpus-v1`.
- Frozen corpus branch: `eval/m2-agent-corpus-v1-20260830`.
- Frozen corpus SHA: `efce0cac15565941c11c8af8d1a52e7ecafaa5c6`.
- Controlled condition branch: `eval/m2-structural-condition-v1-20260830`.
- Controlled condition SHA: `6cbf13695fa90b1311ae7ece7da403f7cbeafe05`.
- Agent-visible protocol: `m2-structural-v1`.
- Corpus freeze evidence: [`m2-agent-corpus-v1-freeze-20260830.md`](../verification/m2-agent-corpus-v1-freeze-20260830.md).
- Controlled condition evidence: [`m2-controlled-evaluation-condition-v1-20260830.md`](../verification/m2-controlled-evaluation-condition-v1-20260830.md).

The controlled harness may add private evidence and startup configuration. It may not change the JSON commands/cases/authority described by `describeV1`.

Before exporting a run fixture, the OpenCode bridge executes a Git comparison against the exact frozen corpus SHA. The following current-checkout inputs must be byte-for-byte unchanged relative to that revision:

- `evaluation/m2-agent-corpus-v1/`;
- `tests/Nyrwvy.Core.Tests/M2StructuralEvaluationCorpus.fs`;
- `tools/Nyrwvy.EvaluationFixtureExport/Program.fs`;
- `tools/Nyrwvy.EvaluationFixtureExport/Nyrwvy.EvaluationFixtureExport.fsproj`.

A drifted corpus, fixture source, or exporter therefore prevents the controlled session from starting rather than silently evaluating a different input while claiming v1.

## OpenCode boundary

The project-local `nyrwvy-structural-eval` agent denies the inherited tool surface and explicitly permits only:

- `nyrwvy_structural_eval_start`;
- `nyrwvy_structural_eval_request`;
- `nyrwvy_structural_eval_finalize`.

The evaluated agent cannot inspect repository files, tests, corpus/oracle files, raw fixture snapshots, prior results, or artifact paths.

The harness selects the task and fixture through environment configuration. The agent's first runner request must be `describeV1`; it discovers the exact command and recipe schema from the frozen runner response rather than receiving a pre-expanded structural command schema in its system instructions.

One OpenCode agent session is intended to map to one persistent `Nyrwvy.AgentEval.Structural` process. Opaque proposal handles therefore remain valid across query, simulation, review, commit/reject, stale recovery, and finalization once the bridge tool is actually available.

## Harness-owned startup configuration

For a recorded run the operator sets, at minimum:

```text
NYRWVY_STRUCTURAL_EVAL_TASK_ID=M2-E<task>
NYRWVY_STRUCTURAL_EVAL_FIXTURE=<frozen fixture>
NYRWVY_STRUCTURAL_EVAL_OPENCODE_VERSION=<exact version>
NYRWVY_STRUCTURAL_EVAL_PROMPT_REVISION=<exact revision>
NYRWVY_STRUCTURAL_EVAL_TOOL_REVISION=<exact condition revision>
```

`NYRWVY_STRUCTURAL_EVAL_ARTIFACT_ROOT` may override the default private artifact root, but it must be an absolute path outside the evaluation worktree.

The bridge records the checked-out repository revision itself. Condition v1 attempted to capture model/provider/runtime/reasoning metadata from the OpenCode `chat.message` plugin hook.

## Frozen v1 seed interpretation

The frozen corpus states that task seeds may supply stable IDs for existing semantic facts, but it does not enumerate a per-task seed table. Before observing any model output, condition v1 fixed the **minimal seed interpretation** below so later runs cannot selectively reveal more context after seeing failures.

Every task receives the frozen target function ID:

```text
function-m2-eval-target
```

Additional seed IDs are supplied only where the frozen prompt explicitly says a wrong/supplied identity is part of the first request:

| Task | Additional seed |
| --- | --- |
| M2-E2 | stale expected base `snapshot-m2-eval-wrong-base` |
| M2-E3 | wrong expected current result `node-m2-eval-wrong-result` |
| M2-E4 | foreign parameter `parameter-m2-eval-foreign-value` |
| M2-E1, E5-E10 | none |

The agent must discover the actual current `SnapshotId`, target result-node guard, target parameter IDs/types, and other bounded structural context through `stateV1` / `focusFunctionV1` after first discovering those commands through `describeV1`.

This seed table remains unchanged in v1.1. Changing it after model observation requires a new named evaluation condition, and changing the task/oracle semantics still requires a corpus version change.

## Private durable evidence

The F# runner/harness was designed to write private artifacts under the harness run directory:

- `run-metadata.json` — frozen corpus/protocol/task/fixture, starting identities, repository/model/runtime revisions;
- `events.jsonl` — every agent-visible request/response with byte counts;
- `harness-events.jsonl` — harness-only perturbation and verification receipts;
- `metrics.json` — terminal command counts, rejected simulation/commit counts, zero-unrelated-churn result, harness-perturbation preservation result, and verification artifact references;
- `run-state.json` — terminal workspace identity/fingerprints, pending-proposal state, zero-churn result, and harness-perturbation state;
- `verification-<transaction>/...` — exact committed-state Typed Semantic IR, Execution IR, generated C#, compiler/execution output, and terminal verification report for semantically-ready commits.

On Unix the artifact root, run directory, fixture, and runner artifacts are restricted to the owner. Artifact initialization/finalization failure invalidates the run rather than silently returning incomplete evidence.

For M2-E1 the exact committed behavioral oracle is `evaluate_metric() == 3.75`. For recovery tasks whose frozen oracle does not pin a resulting literal, committed-state verification still requires successful projection/lowering/C# build/execution and records the actual output without inventing a post-hoc expected value.

The harness snapshots every non-target function in the frozen fixture at startup. Terminal `zeroUnrelatedSemanticChurn` compares those facts against the final workspace. For M2-E10, the explicitly harness-owned `unrelated_metric` advance is excluded from agent churn and is checked separately through `harnessPerturbationPreserved`.

None of these artifacts were produced in the runtime-invalid Stage A attempt because the OpenCode bridge start tool was never actually invoked.

## M2-E10 harness perturbation

Only M2-E10 enables the harness stale-workspace perturbation.

After the first successful reviewed proposal targeting the frozen `target_metric` function is emitted to the evaluated agent, but before the next agent command can be processed, the runner advances the same workspace by publishing the frozen unrelated structural edit on `unrelated_metric` through `AgentStructuralWorkflow`.

The original target proposal remains stored. Its subsequent commit must therefore fail through the ordinary `StaleBaseSnapshot` check. The perturbation receipt and new result-node identity are private harness evidence and are excluded from agent-owned semantic-churn scoring. Terminal evidence separately verifies that the harness-owned result survives the agent's recovery.

The evaluated agent has no command or tool argument that can invoke, change, or suppress this perturbation.

## Pre-run corpus interpretation limit

Before any model output was observed, the frozen v1 corpus had one wording limitation that remains explicit:

- M2-E2 and M2-E3 say “requested literal replacement” but do not pin literal bits in the task prompt/seed.
- Their frozen machine oracle is guard rejection followed by fresh reviewed recovery (`recoverThenCommit`), not a specific final literal value.

They must therefore be scored as recovery tasks, while still requiring a valid bounded structural commit and exact committed-state verification. Do not select a preferred literal after observing model behavior. If a later evaluation needs a specific replacement value, create and freeze `m2-agent-corpus-v2`.

M2-E8/M2-E9 similarly care about the specified obligation-protection rejection rather than the value of an otherwise-valid literal request. M2-E10 measures stale-reviewed-proposal recovery and preservation of the harness advance; its frozen oracle does not prescribe one target replacement recipe.

## Historical run controls

Condition v1 intended every recorded autonomous attempt to:

1. check out exact revision `6cbf13695fa90b1311ae7ece7da403f7cbeafe05`, with no tracked or untracked worktree changes;
2. restart OpenCode so project-local agent/plugin configuration is loaded from that revision;
3. set task/fixture and exact OpenCode/prompt/tool revision metadata;
4. give the agent exactly one frozen task prompt plus the permitted seed IDs;
5. hide corpus oracle and fixture source;
6. use one persistent session, discover `describeV1`, and finalize once;
7. retain unsuccessful/invalid attempts;
8. apply the frozen oracle from durable artifacts.

The real-runtime failure occurred before step 6 could begin: the custom bridge tools were not present in the OpenCode model tool list.

## Classification boundary

- A harness/artifact/transport failure is an invalid controlled run, not an agent semantic failure.
- A malformed/unsupported agent request is a tool-protocol error only after a real request tool invocation occurs.
- Expected simulation rejection in M2-E2 through M2-E9 is not itself task failure.
- The first stale commit rejection in M2-E10 is protocol-required behavior, not agent error.
- A semantic commit without durable exact verification is not an execution-oracle pass.
- `zeroUnrelatedSemanticChurn=false` or `harnessPerturbationPreserved=false` is semantic oracle evidence, not a harness invalidation by itself.
- Agent prose is never sufficient evidence of success.

**Do not start or resume corpus collection under condition v1.** Issue #51 is blocked on the condition v1.1 runtime preflight tracked by Issue #68. The frozen corpus remains unchanged.
