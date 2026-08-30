# M2 ListMap controlled run template

Complete this record before the first valid model attempt under a frozen condition.

## Frozen inputs

```text
corpus id: m2-listmap-agent-corpus-v1
corpus branch: <freeze branch>
corpus SHA: <exact SHA>
protocol: m2-listmap-structural-v1
tool/repository revision: <exact SHA>
condition revision: <name + exact SHA>
OpenCode version: <exact version>
model: openai/gpt-5.6-terra
reasoning/variant: high
prompt revision: <exact revision>
transport: <real-runtime-preflighted direct tools>
```

Do not begin corpus attempts until every placeholder above is resolved and the real-runtime preflight has produced durable terminal artifacts.

## Sampling

```text
Stage A: M2-L1..M2-L10, attempt a1 (10 fresh sessions)
Stage B: M2-L1..M2-L10, attempts a2 and a3 (20 fresh sessions)
target: 30 valid attempts
```

Stage A review is a **condition-validity gate only**. Semantic failures do not justify changing prompts/oracles or skipping Stage B when the condition is valid.

## Per-attempt invariants

- one fresh OpenCode session;
- one fresh exported fixture;
- exact frozen task prompt with no prior-result leakage;
- preserve stdout/stderr even on failure;
- preserve every harness run/event artifact;
- no retry in the same session to replace an invalid attempt;
- classify transport/condition validity before semantic correctness;
- exact committed-state verification must be durable rather than inferred from model prose.

## Required terminal evidence

For each valid attempt retain at least:

- run metadata with exact frozen revisions;
- protocol event log;
- harness event log if controlled harness is enabled;
- terminal run state;
- usage/metrics including tool calls, simulations, commit attempts, commits, rejects, and protocol errors;
- proposal/review facts for successful simulations;
- exact structured diagnostic for expected rejection tasks;
- durable execution verification for committed attempts;
- zero-unrelated-semantic-churn result;
- M2-L10 harness perturbation applied/preserved result.

## Scoring

Use only the frozen machine oracle in `corpus.json` for formal task correctness. Additional prompt-fidelity or efficiency analysis may be reported separately but must not rewrite the frozen score after observation.

For rejection tasks, substituting another source or mapper after the requested invalid operation does not satisfy the task.

For M2-L10, expected success requires:

1. reviewed target proposal;
2. harness-owned unrelated publication;
3. first target commit rejected with `StaleBaseSnapshot`;
4. advanced workspace inspection;
5. fresh target proposal and commit;
6. harness rename preserved;
7. committed behavior verified.

## Final aggregate

Report at minimum:

- valid attempts / attempted sessions;
- task-correct attempts / valid attempts;
- per-task 3-attempt result;
- diagnostic accuracy;
- successful commit/execution verification rate;
- zero-churn rate;
- stale recovery rate and harness preservation;
- tool/simulation/commit/reject/protocol-error counts;
- avoidable retries or agent-reject behavior;
- systematic failures versus isolated mistakes.
