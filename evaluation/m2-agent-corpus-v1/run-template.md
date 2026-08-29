# m2-agent-corpus-v1 run record template

Use one record per attempted task run. Do not discard failed autonomous attempts or overwrite them with retries.

## Run identity

- corpus ID: `m2-agent-corpus-v1`
- corpus freeze SHA:
- repository revision:
- agent protocol: `m2-structural-v1`
- runner/tool revision:
- task ID:
- fixture:
- model:
- provider/runtime:
- reasoning configuration:
- prompt/orchestrator revision:
- started at:
- intervention class: autonomous / assisted / manually repaired

## Starting state

- starting snapshot ID:
- starting snapshot fingerprint:
- starting semantic fingerprint:
- seeded stable IDs:
- harness perturbation configured: yes / no

## Attempt trace

Record every command in order or link the durable event artifact.

- tool calls:
- focus queries:
- simulations:
- commit attempts:
- commits:
- rejects:
- protocol errors:
- structured diagnostic sequence:
- proposal handles / transaction IDs:
- reviewed base/proposed snapshot IDs:

For recovery tasks, identify the failing request/commit and the first subsequent command that re-inspected current state.

## Structural review oracle

- requested recipe matches task: pass / fail
- base Focus identifies expected target/result: pass / fail
- proposed Focus identifies materialized result: pass / fail
- removed-node summaries complete: pass / fail / n/a
- added-node roles/order exact: pass / fail / n/a
- Float64 bits exact: pass / fail / n/a
- new graph-node IDs system-owned: pass / fail / n/a
- old/new body node sets disjoint: pass / fail / n/a

## Final semantic oracle

- expected terminal kind:
- observed terminal kind:
- expected diagnostic:
- observed diagnostic:
- readiness:
- final snapshot ID:
- final snapshot fingerprint:
- final semantic fingerprint:
- target identity preserved: pass / fail / n/a
- caller relation preserved: pass / fail / n/a
- harness-owned stale perturbation preserved: pass / fail / n/a
- unrelated semantic churn count:
- unrelated churn details:

## Exact execution evidence

Required for M2-E1; otherwise mark n/a.

- Typed Semantic IR source snapshot ID:
- Execution IR source snapshot ID:
- C# emission/build: pass / fail / n/a
- behavioral execution: pass / fail / n/a
- expected behavior:
- observed behavior:
- durable verification artifact location:

## Outcome

- semantic task success: yes / no
- protocol/tool success: yes / no
- recovery success: yes / no / n/a
- final intervention class:
- failure categories:
- notes:

The model's own natural-language claim of completion is not an oracle field.
