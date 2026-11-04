# Milestone 1 agent evaluation run template v1

**Status:** Experimental procedure

Use this template for runs of [`m1-agent-corpus-v1`](m1-agent-evaluation-corpus-v1.md). Keep unsuccessful and interrupted valid attempts; do not retain only favorable runs.

One record may summarize a tightly controlled batch only when every run has the same corpus task, repository revision, model/configuration, tool surface, and prompt revision. Otherwise create separate records.

## Run identity

- **Run ID:**
- **Corpus:** `m1-agent-corpus-v1`
- **Task:** `E1 | E2 | E3 | E4 | E5`
- **Mode:** `A — Nyrwvy semantic workflow | B — source baseline | C — raw graph diagnostic`
- **Date/time:**
- **Repository revision:**
- **Starting snapshot ID/fingerprint:**
- **Starting semantic fingerprint:**
- **Fixture / baseline program revision:**

## Agent condition

- **Model:**
- **Provider/runtime:**
- **Reasoning configuration / budget:**
- **Tool-surface version:**
- **System/instruction prompt revision:**
- **Task-prompt revision:** `m1-agent-corpus-v1`
- **Context/preloaded material:**
- **Human intervention:** `none | clarification-only | repair-guidance | manual-edit`
- **First attempt or retry:**

If a model, tool, prompt, or harness changed from another run, record the change instead of treating the runs as identical repetitions.

## Resolved task inputs

Record the concrete runtime values for every corpus placeholder supplied to or discovered during the run.

| Placeholder / input | Concrete value | Supplied initially or discovered | Discovery query/tool |
| --- | --- | --- | --- |
|  |  |  |  |

For E5 also record the harness-owned perturbation transaction and its exact before/after workspace identity.

## Outcome

- **Primary success level:** `S0 | S1 | S2 | S3 | S4`
- **Task-correct:** `yes | no | ambiguous`
- **Final readiness:**
- **Final snapshot ID/fingerprint:**
- **Final semantic fingerprint:**
- **Reviewer:**

Success levels follow the accepted evaluation plan:

- **S0** — no valid proposal/result within the run budget;
- **S1** — structurally valid but task-incorrect;
- **S2** — correct with unrelated semantic churn;
- **S3** — correct and scoped after one or more recoverable failed/rejected attempts;
- **S4** — first accepted proposal is correct, scoped, and passes required verification.

For E3 and E5, protocol-required intermediate rejection/incompleteness does not automatically reduce an otherwise correct run from S4. Count only avoidable agent retries/errors when assigning the success level; record required protocol transitions separately below.

## Failure categories

Mark every category observed, even if the run later recovered.

- [ ] `wrong-target`
- [ ] `identity-loss`
- [ ] `missing-propagation`
- [ ] `unrelated-churn`
- [ ] `invalid-reference`
- [ ] `type-error`
- [ ] `effect-error`
- [ ] `order-error`
- [ ] `implicit-default`
- [ ] `obligation-bypass`
- [ ] `stale-review`
- [ ] `wrong-specialized-node`
- [ ] `backend-leak`
- [ ] `tool-protocol-error`
- [ ] none

Notes / evidence for classifications:

## Attempt timeline

Record all meaningful attempts and state transitions, not only the final successful path.

| # | Action | Result | Workspace/proposal identity | Expected protocol transition? | Notes |
| ---: | --- | --- | --- | --- | --- |
| 1 |  |  |  |  |  |

Examples of expected protocol transitions include E3's deliberate incomplete publication and E5's injected stale rejection.

## Quantitative metrics

Use `unavailable` rather than estimating values that the runtime cannot measure reliably.

A `codex exec --json` run reports its tokens in `turn.completed` events; save the JSONL before the temporary output is removed and sum them (cached input is part of `input_tokens`):

```shell
jq -s '[.[] | select(.type == "turn.completed") | .usage] | {input: (map(.input_tokens) | add), cachedInput: (map(.cached_input_tokens // 0) | add), output: (map(.output_tokens) | add)}' run.jsonl
```

| Metric | Value |
| --- | ---: |
| Semantic query count |  |
| Focus query count |  |
| Broader/whole-program retrieval count |  |
| Operation/simulation count |  |
| Failed/rejected proposals attributable to agent error |  |
| Protocol-required rejected/intermediate proposals |  |
| Commit attempts |  |
| Agent retries |  |
| Raw tool-call count |  |
| Input tokens |  |
| Output tokens |  |
| Focused context bytes/entities retrieved |  |
| Unrelated semantic entities changed |  |
| Compiler/test/lowering failures after accepted edits |  |
| Wall-clock duration |  |

Token and latency data are operational measurements, not correctness scores.

## Query sufficiency

Highest retrieval level required by the successful path:

- [ ] 1 — one Focus slice
- [ ] 2 — multiple bounded semantic queries
- [ ] 3 — broader snapshot query
- [ ] 4 — raw whole-snapshot/serialization access
- [ ] no successful path

Explain why broader access was required, if applicable:

## Semantic churn oracle

List the task-oracle entities/relations that were permitted to change, then record actual changes outside that set.

### Permitted changes

- 

### Actual unrelated changes

- none / 

Do not count E5's explicitly harness-owned perturbation as agent churn; record it separately as harness state.

## Verification

- **Task-specific semantic oracle:** `pass | fail | ambiguous`
- **Snapshot validation/readiness:**
- **Typed Semantic IR projection:**
- **Execution IR lowering:**
- **Backend emission/build:**
- **Behavioral execution checks:**
- **Source-baseline tests, if Mode B:**

Evidence / command output / artifact references:

## Transcript and artifacts

- **Transcript/event summary:**
- **Proposal/receipt/Change Review artifacts:**
- **Final snapshot/source artifact:**
- **Test/verification artifacts:**
- **Other evidence:**

Large raw artifacts may be stored outside maintained documentation, but references must be durable enough for later review.

## Reviewer note

Summarize what this run says about the design rather than merely restating pass/fail.

Questions to consider:

- Did bounded semantic queries provide enough context?
- Did the operation surface prevent an error or merely constrain the task?
- Did explicit obligations/review binding improve recovery?
- Did the agent spend substantial effort translating intent into tool protocol?
- Did any failure reveal a missing semantic operation or an over-specialized API?
- Is the result evidence to **keep**, **change**, or **investigate** a design choice?
