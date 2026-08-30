# M2 ListMap controlled condition v1 preflight — 2026-08-31

**Status:** Verified and frozen before scored ListMap corpus observation.

This record freezes the real-runtime condition used for the specialized M2.3 `ListMap` controlled evaluation. It does not contain any scored `M2-L1..M2-L10` result; the runtime preflight deliberately exercised transport only.

## Frozen condition

```text
condition: m2-listmap-condition-v1
condition branch: eval/m2-listmap-condition-v1-20260831
condition/repository/tool SHA: 23bf09d87c03b9c15dd4640b662a05b24aa5e330
condition merge: bdb69b34d1536a5515101aa25136a438bcc7a835
corpus: m2-listmap-agent-corpus-v1
corpus branch: eval/m2-listmap-agent-corpus-v1-20260831
corpus SHA: a2ffb5615d37d44bbd910c4f47c36136c31cdce4
protocol: m2-listmap-structural-v1
OpenCode: 1.18.18
model: openai/gpt-5.6-terra
variant: high
provider/runtime: opencode
scored prompt revision: m2-listmap-corpus-v1-seed-v1
```

The condition branch is immutable for this tranche. Any change to bridge, harness, restricted agent, direct tools, verification, or protocol wiring requires a separately named condition revision and a fresh real-runtime preflight.

## Local verification gate

Exact candidate `23bf09d87c03b9c15dd4640b662a05b24aa5e330` passed:

- clean repository verification with `scripts/verify-local-20260825-1427.sh --clean`;
- focused ListMap workflow/protocol/corpus/harness tests: **26/26 passed**;
- Bun direct-tool/bridge tests: **8/8 passed**, 0 failed, 32 expectations;
- real OpenCode runtime preflight on OpenCode 1.18.18, Terra high.

The initial Bun invocation omitted `./` before the hidden-directory path, so Bun treated the argument as a filter and ran no test file. This was an operator-command issue, not a code failure. The corrected command was:

```fish
bun test ./.opencode/tests/nyamnyam-listmap-eval.test.ts
```

and all eight tests passed on the unchanged exact condition SHA.

## Transport boundary

The condition uses the already-proven direct project-local OpenCode custom-tool pattern rather than the failed plugin-registration discovery path from the earlier Float64 condition v1 experiment.

The evaluated agent is restricted to exactly three OpenCode transport tools:

```text
nyamnyam_listmap_eval_start
nyamnyam_listmap_eval_request
nyamnyam_listmap_eval_finalize
```

Those tools transport requests to the already-frozen seven-command `m2-listmap-structural-v1` protocol. They do not add semantic mutation authority.

The restricted agent cannot inspect repository files, corpus/oracle files, raw snapshots, prior run artifacts, or unrelated tools.

## Frozen-input pinning

Before starting an attempt, the bridge requires the condition worktree's evaluation inputs to match the immutable corpus revision. The pin includes:

- `evaluation/m2-listmap-agent-corpus-v1`;
- deterministic ListMap fixture/oracle source;
- fixture exporter inputs;
- `AgentListMapStructuralWorkflow`;
- `m2-listmap-structural-v1` protocol source.

The harness separately validates exported snapshot identities against its frozen binding table.

## Durable evidence

Each controlled run writes outside the evaluation worktree with private permissions and records:

- starting fixture and exact frozen revisions;
- model/provider/reasoning/OpenCode/prompt/tool/repository metadata;
- protocol request/response event log;
- harness-private events;
- terminal state and semantic fingerprints;
- usage metrics and structured diagnostics;
- zero-unrelated-semantic-churn result;
- frozen machine-oracle component checks;
- for committed L1/L10 attempts, durable Typed Semantic IR -> Execution IR -> C# build/execution evidence.

Successful L1/L10 commit verification also requires the publication diff itself to be exactly the frozen shallow materialization:

```text
ParameterReference(source ParameterId)
ListMap(source node, mapper FunctionId)
```

with exactly two fresh system-owned added node IDs, the expected dependency/result relation, and no reuse of removed node IDs.

## M2-L10 perturbation

The stale-workspace perturbation is harness-owned and is not an agent-visible command. After the first successful target proposal, the harness publishes one ordinary rename of the frozen unrelated function to:

```text
unrelated_marker_harness_advanced
```

The target's reviewed proposal must then reject as `StaleBaseSnapshot`. A fresh target proposal may commit only against the advanced workspace, and terminal evidence independently requires the harness rename to remain preserved.

## Real OpenCode preflight

The preflight used the L1 fixture only as a valid fixture for transport. It was explicitly **not** a scored L1 attempt.

The model was instructed only to:

1. start the configured condition once;
2. send exact `{"command":"describeV1"}` once;
3. finalize once;
4. perform no simulation or commit.

Observed runtime evidence:

- one real `nyamnyam_listmap_eval_start` tool event;
- one real `nyamnyam_listmap_eval_request` tool event;
- one real `nyamnyam_listmap_eval_finalize` tool event;
- one exact `describeV1` runner request;
- simulations: **0**;
- commit attempts: **0**;
- commits: **0**;
- protocol errors: **0**;
- terminal durable run state present;
- `zeroUnrelatedSemanticChurn=true`;
- no harness stale perturbation applied.

The preflight terminal state reported `oraclePassed=false`. That is expected and is not a condition failure: the frozen M2-L1 semantic oracle requires one valid committed ListMap replacement, while the transport preflight intentionally performs no semantic proposal or commit.

The preflight produced a private evidence archive:

```text
/tmp/m2-listmap-runtime-preflight-20260831-065322.tar.gz
```

No scored model result is inferred from this transport-only archive.

## What this verifies

This gate establishes that, before the first scored ListMap corpus attempt:

- the corpus and condition revisions are distinct and pinned;
- OpenCode can discover and execute the real direct custom tools;
- the persistent ListMap protocol runner can be started, queried, and finalized through OpenCode;
- terminal artifacts survive process finalization;
- the condition does not require widened graph authority;
- semantic scoring remains based on durable machine evidence rather than model prose.

## Next gate

The exact operator-side prompt wrapper and Stage A/Stage B orchestration are frozen separately before the first scored model observation. Stage A remains one fresh attempt per `M2-L1..M2-L10`; its review is a condition-validity/durability gate only. If the condition remains valid, the two remaining fresh attempts per task proceed regardless of Stage A semantic outcomes.

Refs #77
Refs #90
Refs #92
Refs #93
