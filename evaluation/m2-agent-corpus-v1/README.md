# m2-agent-corpus-v1

**Status:** Frozen-input definition; model execution is forbidden until an exact verified freeze-branch SHA is recorded for this content.

This directory defines the first controlled Milestone 2 structural-editing corpus. It is separate from `m1-agent-corpus-v1` and must not reinterpret or modify any M1 result.

## Fixed condition

- Corpus identifier: `m2-agent-corpus-v1`
- Agent-visible protocol: `m2-structural-v1`
- Agent executable: `tools/Nyrwvy.AgentEval.Structural`
- Machine-readable tasks/oracles: [`corpus.json`](corpus.json)
- Deterministic fixture source: `tests/Nyrwvy.Core.Tests/M2StructuralEvaluationCorpus.fs`
- Fixture exporter: `tools/Nyrwvy.EvaluationFixtureExport`

The corpus becomes active only when issue #50 records the exact locally verified SHA of the dedicated freeze branch. Once that SHA is recorded, prompts, task classes, fixture bindings, harness perturbations, and oracles in v1 are immutable. Any semantic change requires a new corpus version.

## Authority boundary

Mode A agents may use only the `m2-structural-v1` commands exposed by the separate structural runner. They may not receive:

- raw `Snapshot`, `ValueGraph`, or `SemanticNode` mutation;
- caller-selected IDs for newly materialized graph nodes;
- JSON/Nyason program patches;
- generic graph-node constructors;
- privileged direct workspace replacement.

Stable IDs supplied in task seeds identify existing semantic facts only. Newly materialized structural IDs remain system-owned.

## Deterministic fixtures

The corpus uses three exported snapshots from one shared F# fixture source.

### `m2-ready`

A semantically ready program containing:

- `target_metric(value: Float64, count: Int64) -> Float64`;
- `evaluate_metric() -> Float64`, which calls the target with existing values `1.5` and `7`;
- `foreign_source(foreign_value: Float64) -> Float64` for out-of-scope parameter rejection;
- `unrelated_metric() -> Float64` for zero-churn checks and the stale-proposal harness advance.

The initial target body is `value + 1.0` and has stable pre-existing graph-node IDs. The successful corpus replacement changes it to `value + 2.25` through the structural workflow.

### `m2-open-protected`

The target body contains a direct call referenced by an **open** durable missing-argument obligation. Structural replacement must reject conservatively rather than deleting or retargeting that obligation.

### `m2-resolved-protected`

The target body contains a direct call referenced by a **resolved** durable obligation. Structural replacement must still reject because historical durable obligation identity protects the old body node.

All three fixtures must pass ordinary `SnapshotValidator` validation before the corpus is frozen.

## Task set

| Task | Class | Expected terminal behavior |
| --- | --- | --- |
| M2-E1 | successful reviewed replacement | commit |
| M2-E2 | expected-base mismatch recovery | reject, inspect, fresh commit |
| M2-E3 | expected-result mismatch recovery | reject, inspect, fresh commit |
| M2-E4 | foreign parameter | simulation reject |
| M2-E5 | wrong-type owned parameter | simulation reject |
| M2-E6 | recipe depth overflow | simulation reject |
| M2-E7 | recipe node-count overflow | simulation reject |
| M2-E8 | open obligation protects old body | simulation reject |
| M2-E9 | resolved obligation protects old body | simulation reject |
| M2-E10 | stale reviewed proposal | stale commit reject, fresh recovery |

M2-E1 also carries the shared success oracles for system-owned node identities/order, complete before/after review, exact committed-state lowering/execution, and zero unrelated semantic churn. This avoids duplicating the same successful edit into several benchmark tasks merely to count oracle facets separately.

## Required coverage mapping

The v1 task set covers the issue #50 requirements as follows:

1. successful `Add(Parameter, Literal)` replacement — M2-E1;
2. expected-base recovery — M2-E2;
3. expected-result recovery — M2-E3;
4. foreign parameter rejection — M2-E4;
5. wrong-type parameter rejection — M2-E5;
6. depth rejection — M2-E6;
7. node-count rejection — M2-E7;
8. open-obligation protection — M2-E8;
9. resolved-obligation protection — M2-E9;
10. materialized identity ownership/order — M2-E1 oracle;
11. before/after review completeness — M2-E1 oracle;
12. stale reviewed proposal — M2-E10;
13. exact committed lowering/behavior — M2-E1 oracle;
14. zero unrelated semantic churn — M2-E1 oracle.

## Recovery tasks

Invalid-request tasks and stale-publication tasks are deliberately distinct.

- M2-E2 and M2-E3 fail during **simulation preflight** because the request guards are wrong. Recovery requires querying the current state/Focus and creating a new proposal.
- M2-E10 first creates a valid reviewed proposal. The harness then advances the workspace by publishing an unrelated structural edit through the ordinary structural workflow. The already-reviewed target proposal must fail at **publication** with `StaleBaseSnapshot`, after which the agent must re-inspect and produce a fresh proposal.

The harness-owned unrelated edit is excluded from agent semantic-churn scoring but must remain present in the final state.

## Oracles and evidence

Agent prose is never the task oracle. The run harness must inspect structured protocol responses and, where applicable, durable verification artifacts.

For every attempt record at least:

- exact repository revision;
- exact corpus freeze SHA;
- `m2-structural-v1` tool/protocol revision;
- model/provider/runtime/reasoning metadata when available;
- task ID and fixture name;
- command/simulation/commit/reject counts;
- structured diagnostic codes;
- proposal/transaction/base/proposed/published identities;
- requested recipe versus materialized structural diff;
- final semantic readiness;
- semantic churn outside the task's permitted set;
- exact lowering/backend/behavior verification for M2-E1;
- intervention class and every failed/retried attempt.

A run is not successful merely because the model says it completed the task.

## Fixture export

After the #50 implementation is merged, deterministic snapshots are exported with:

```shell
dotnet run --project tools/Nyrwvy.EvaluationFixtureExport -- \
  --fixture m2-ready \
  --output /tmp/m2-ready.json
```

The same command accepts `m2-open-protected` and `m2-resolved-protected`.

The exporter links the exact fixture source used by regression tests instead of maintaining a second snapshot definition.

## Non-goals

This corpus does not include:

- List structural recipes;
- arbitrary node/subgraph replacement;
- local-node identity-preserving rewrites;
- generic numeric operators or implicit conversions;
- raw snapshot/serialization mutation;
- semantic merge/rebase;
- a comparison with M1 tasks as if their tool conditions were equivalent.

List structural coverage remains deferred until the post-evaluation decision tracked separately from this v1 corpus.

## Freeze rule

Before any model execution:

1. the corpus fixture/oracle regression suite must pass on a clean local build;
2. the exact candidate commit must be placed on a dedicated `eval/m2-agent-corpus-v1-20260830` freeze branch;
3. issue #50 and a maintained verification record must record that exact SHA;
4. no task/prompt/oracle/fixture change may occur on that frozen branch.

If any model result motivates a prompt or oracle correction, create `m2-agent-corpus-v2`; do not edit v1 in place.
