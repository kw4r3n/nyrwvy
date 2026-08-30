# M2 structural agent evaluation condition v1.1

**Status:** Candidate remediation after condition v1 real-runtime tool-discovery failure. No valid corpus run may begin until the exact v1.1 candidate passes repository-local verification **and** the real OpenCode runtime preflight.

Condition v1.1 preserves every frozen semantic input from v1. Its only intended change is the OpenCode transport used to expose the three persistent bridge tools.

## Unchanged frozen inputs

- corpus: `m2-agent-corpus-v1`
- corpus branch: `eval/m2-agent-corpus-v1-20260830`
- corpus SHA: `efce0cac15565941c11c8af8d1a52e7ecafaa5c6`
- structural runner protocol: `m2-structural-v1`
- task prompts and machine oracles: unchanged
- fixture source/exporter: unchanged
- minimal seed interpretation: unchanged from condition v1
- model condition planned for the first tranche: `openai/gpt-5.6-terra`, variant `high`

M2-E2/E3 retain the already-recorded v1 interpretation: they measure guard rejection and fresh recovery; the frozen task does not pin a final literal value.

## Why v1.1 exists

Condition v1 passed .NET and Bun tests but failed its first real OpenCode Stage A collection. Ten fresh OpenCode sessions loaded the evaluation-agent instructions yet exposed no callable `nyamnyam_structural_eval_*` tool. No harness run directory was created in any attempt.

That failure is recorded separately in `m2-controlled-evaluation-condition-v1-runtime-failure-20260830.md`. It is a transport-condition failure, not model semantic evidence.

## Direct custom-tool boundary

Condition v1.1 uses OpenCode's project-local custom-tool discovery path directly:

```text
.opencode/tools/nyamnyam_structural_eval_start.ts
.opencode/tools/nyamnyam_structural_eval_request.ts
.opencode/tools/nyamnyam_structural_eval_finalize.ts
```

All three import one process-local shared implementation:

```text
.opencode/lib/nyamnyam-structural-eval-bridge.ts
```

The tool names and agent-visible behavior remain exactly:

- `nyamnyam_structural_eval_start`
- `nyamnyam_structural_eval_request`
- `nyamnyam_structural_eval_finalize`

The evaluated agent still has no repository/file/shell access and no direct artifact path or fixture-selection control.

The old M2 plugin-registered tool surface is removed in v1.1. The M1 `nyamnyam-eval` plugin/condition is not changed.

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
NYAMNYAM_STRUCTURAL_EVAL_MODEL=openai/gpt-5.6-terra
NYAMNYAM_STRUCTURAL_EVAL_PROVIDER_RUNTIME=opencode
NYAMNYAM_STRUCTURAL_EVAL_REASONING_CONFIG=high
NYAMNYAM_STRUCTURAL_EVAL_OPENCODE_VERSION=<exact version>
NYAMNYAM_STRUCTURAL_EVAL_PROMPT_REVISION=m2-corpus-v1-seed-v1
NYAMNYAM_STRUCTURAL_EVAL_TOOL_REVISION=<exact v1.1 condition SHA>
```

The structural runner persists those harness-owned values in `run-metadata.json`. They must match the actual operator command used for a recorded tranche.

## Repository-local verification

Before runtime preflight, the exact candidate must pass:

```text
bash scripts/verify-local-20260825-1427.sh --clean
```

plus focused structural harness/protocol/corpus tests and:

```text
bun test ./.opencode/tests/nyamnyam-structural-eval.test.ts
```

The Bun test imports the three direct custom-tool modules as well as the shared bridge implementation. Tests under `.opencode/tests/` are deliberately outside plugin auto-discovery.

## Required real-runtime preflight

Repository-local imports are not sufficient evidence after the v1 failure.

Run on the exact clean candidate:

```text
fish scripts/preflight-m2-structural-opencode.fish
```

This is **not** a corpus attempt. Its prompt permits only:

1. real `nyamnyam_structural_eval_start` invocation;
2. one real request carrying `{ "command": "describeV1" }`;
3. real `nyamnyam_structural_eval_finalize` invocation.

It must produce exactly one private harness run directory whose:

- `events.jsonl` contains `describeV1`;
- `run-state.json` is terminal;
- required metadata/artifacts are non-empty;
- event log contains no simulation or commit command.

The preflight captures OpenCode stdout and DEBUG stderr for diagnosis. An archive is emitted under `/tmp`.

Only after that exact candidate passes may a dedicated v1.1 condition branch be frozen and replacement Stage A collection start from attempt 1/task. Condition-v1 invalid attempts remain historical and are never mixed into the v1.1 sample.

## Sampling plan after preflight

The pre-result sampling plan itself is unchanged:

- Stage A: one fresh valid attempt per M2-E1..E10;
- inspect only harness validity/durability;
- if valid, Stage B adds two fresh attempts per task regardless of semantic Stage A results;
- target sample: 3 valid attempts/task, 30 valid attempts total.

A semantic failure never cancels Stage B. Another harness/transport failure invalidates v1.1 and requires another named condition revision.
