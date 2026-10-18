# M2 structural agent evaluation condition v1.1

**Status:** Verified and frozen for recorded runs at `178f38274890a28ae61a1c70c9d598bc01492256`. See [runtime verification](../verification/m2-controlled-evaluation-condition-v1.1-preflight-20260830.md).

> The evaluation runners, OpenCode bridges, scripts and corpora this document names were removed from `main` on 2026-10-18. Replay them from the [evaluation freeze tags](../operations/evaluation-freeze-tags.md) or from commit `b3a958a`, the last `main` commit that contained them.

Condition v1.1 preserves every frozen semantic input from v1. Its only change is the OpenCode transport used to expose the three persistent bridge tools.

## Frozen condition

- condition branch: `eval/m2-structural-condition-v1.1-20260830`
- condition SHA: `178f38274890a28ae61a1c70c9d598bc01492256`
- integration PR: #71
- corpus: `m2-agent-corpus-v1`
- corpus branch: `eval/m2-agent-corpus-v1-20260830`
- corpus SHA: `efce0cac15565941c11c8af8d1a52e7ecafaa5c6`
- structural runner protocol: `m2-structural-v1`
- task prompts and machine oracles: unchanged
- fixture source/exporter: unchanged
- minimal seed interpretation: unchanged from condition v1
- first tranche model condition: `openai/gpt-5.6-terra`, variant `high`
- verified OpenCode runtime: `1.18.18`

M2-E2/E3 retain the already-recorded v1 interpretation: they measure guard rejection and fresh recovery; the frozen task does not pin a final literal value.

## Why v1.1 exists

Condition v1 passed .NET and Bun tests but failed its first real OpenCode Stage A collection. Ten fresh OpenCode sessions loaded the evaluation-agent instructions yet exposed no callable `nyrwvy_structural_eval_*` tool. No harness run directory was created in any attempt.

That failure is recorded in `m2-controlled-evaluation-condition-v1-runtime-failure-20260830.md`. It is a transport-condition failure, not model semantic evidence, and contributes zero valid corpus attempts.

## Direct custom-tool boundary

Condition v1.1 uses OpenCode's project-local custom-tool discovery path directly:

```text
.opencode/tools/nyrwvy_structural_eval_start.ts
.opencode/tools/nyrwvy_structural_eval_request.ts
.opencode/tools/nyrwvy_structural_eval_finalize.ts
```

All three import one process-local shared implementation:

```text
.opencode/lib/nyrwvy-structural-eval-bridge.ts
```

The tool names and agent-visible behavior remain exactly:

- `nyrwvy_structural_eval_start`
- `nyrwvy_structural_eval_request`
- `nyrwvy_structural_eval_finalize`

The evaluated agent still has no repository/file/shell access and no direct artifact path or fixture-selection control. Agent tool exposure is explicitly whitelisted to these three bridge tools.

The old M2 plugin-registered tool surface is removed in v1.1. The historical M1 `nyrwvy-eval` condition is not reinterpreted by this change.

## Persistent session semantics

The shared bridge module owns the same process-local session map used by all three direct custom-tool modules. One OpenCode agent session may start one structural runner, send one JSON request at a time, and finalize it once.

The F# runner and harness remain unchanged from the v1 condition. In particular:

- `describeV1` remains the first required runner request;
- opaque proposal handles remain process-local;
- frozen-input Git drift checks still run before fixture export;
- E10 stale perturbation remains harness-owned;
- exact committed-state Typed Semantic IR -> Execution IR -> C# verification remains harness-owned;
- terminal zero-unrelated-churn and perturbation-preservation evidence remains unchanged.

## Metadata

Condition v1 depended on a plugin `chat.message` hook to observe model/provider/reasoning metadata. v1.1 removes that dependency from the M2 bridge.

The operator fixes and exports the same values used to launch OpenCode:

```text
NYRWVY_STRUCTURAL_EVAL_MODEL=openai/gpt-5.6-terra
NYRWVY_STRUCTURAL_EVAL_PROVIDER_RUNTIME=opencode
NYRWVY_STRUCTURAL_EVAL_REASONING_CONFIG=high
NYRWVY_STRUCTURAL_EVAL_OPENCODE_VERSION=1.18.18
NYRWVY_STRUCTURAL_EVAL_PROMPT_REVISION=m2-corpus-v1-seed-v1
NYRWVY_STRUCTURAL_EVAL_TOOL_REVISION=178f38274890a28ae61a1c70c9d598bc01492256
```

The structural runner persists those harness-owned values in `run-metadata.json`. They must match the actual operator command used for a recorded tranche.

## Verification evidence

The exact condition SHA passed:

```text
bash scripts/verify-local-20260825-1427.sh --clean
```

with 0 warnings/errors and 213/213 tests, plus 16/16 focused structural tests and 8/8 direct-tool Bun tests.

The exact same revision then passed:

```text
fish scripts/preflight-m2-structural-opencode.fish
```

in OpenCode 1.18.18 using `openai/gpt-5.6-terra`, variant `high`.

The real runtime emitted exactly one completed tool event for each of start/request/finalize. The runner received exactly one `{ "command": "describeV1" }` request; private metrics reported one tool call, zero simulations/commits/protocol errors, and terminal state with zero semantic churn.

See [M2 condition v1.1 runtime verification](../verification/m2-controlled-evaluation-condition-v1.1-preflight-20260830.md).

## Frozen seed interpretation

Every task receives:

```text
targetFunctionId = function-m2-eval-target
```

Additional seed IDs remain:

| Task | Additional seed |
| --- | --- |
| M2-E2 | `staleExpectedBaseSnapshotId = snapshot-m2-eval-wrong-base` |
| M2-E3 | `wrongExpectedCurrentResultNodeId = node-m2-eval-wrong-result` |
| M2-E4 | `foreignParameterId = parameter-m2-eval-foreign-value` |
| M2-E1, E5-E10 | none |

Correct current guards/parameter context must be discovered through the bounded structural runner after `describeV1`.

## Sampling plan

The pre-result sampling plan remains unchanged:

- Stage A: one fresh valid attempt per M2-E1..E10;
- inspect only harness validity/durability before proceeding;
- if valid, Stage B adds two fresh attempts per task regardless of semantic Stage A results;
- target sample: 3 valid attempts/task, 30 valid attempts total.

One fresh OpenCode session and fixture export are required per attempt. Do not continue a previous session. Invalid harness/transport runs are retained but do not enter the 30-valid-attempt sample.

## Classification boundary

- condition-v1 tool-discovery attempts are historical invalid-condition evidence, not semantic task failures;
- expected simulation rejection in M2-E2 through M2-E9 is not itself task failure;
- the first stale commit rejection in M2-E10 is required behavior;
- semantic commits require durable exact-state verification where the frozen oracle requires commit success;
- agent prose is never sufficient evidence;
- task/oracle/fixture semantic changes require a new corpus version;
- later transport/harness-condition changes require a new named evaluation condition.

Condition v1.1 is cleared for #51 Stage A collection under this exact frozen condition.
