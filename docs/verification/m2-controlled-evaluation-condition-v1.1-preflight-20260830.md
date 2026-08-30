# M2 controlled evaluation condition v1.1 runtime preflight — 2026-08-30

**Status:** The first real-runtime preflight on candidate `66857fb5b216b718220e2b0d7b37a704c41a9f8a` substantively passed the transport/runtime requirements. The preflight shell script itself reported a false positive because it searched the full `describeV1` response for the names of semantic commands. The checker is corrected in the next candidate; exact-condition freeze still requires one final rerun on that exact head.

## Candidate and runtime

- candidate revision: `66857fb5b216b718220e2b0d7b37a704c41a9f8a`
- OpenCode: `1.18.18`
- model: `openai/gpt-5.6-terra`
- variant: `high`
- frozen corpus SHA: `efce0cac15565941c11c8af8d1a52e7ecafaa5c6`
- protocol: `m2-structural-v1`
- purpose: harness/tool transport preflight only; not a corpus attempt

## Durable evidence

The uploaded preflight archive contained exactly one harness run directory plus OpenCode JSON stdout and DEBUG stderr.

OpenCode emitted real `tool_use` events, each completed successfully:

1. `nyamnyam_structural_eval_start`
2. `nyamnyam_structural_eval_request` with exact input `{"command":"describeV1"}`
3. `nyamnyam_structural_eval_finalize`

The F# runner's `events.jsonl` contained exactly one request:

```json
{"command":"describeV1"}
```

The apparent strings `simulateStructuralV1`, `commitStructuralV1`, and `commitStructuralAllowIncompleteV1` occurred only inside the **response schema returned by `describeV1`**. They were not requests.

Terminal runner metrics were:

- `toolCalls`: 1
- `focusQueries`: 0
- `simulations`: 0
- `commitAttempts`: 0
- `commits`: 0
- `protocolErrors`: 0
- `eventCount`: 1
- `pendingProposalCount`: 0
- `zeroUnrelatedSemanticChurn`: true
- `harnessPerturbationApplied`: false
- `harnessPerturbationPreserved`: true

`run-state.json` was terminal and retained the original `snapshot-m2-eval-ready` semantic state. No verification report was expected because no semantic commit occurred.

`run-metadata.json` correctly recorded the candidate SHA as both repository/tool revision and recorded model/runtime/reasoning/OpenCode metadata.

## Checker false positive

The original preflight shell script rejected the run with:

```text
ERROR: preflight performed a semantic simulation/commit.
```

Its check was equivalent to searching the whole runner event line for semantic command names. Because `describeV1` intentionally returns the supported command names as schema data, the response itself contains those strings.

This is a **preflight-checker defect**, not a tool-transport/harness defect.

The checker is changed to validate:

- one real OpenCode `tool_use` event for each of start/request/finalize;
- the exact `events.jsonl` request field for `describeV1`;
- runner counters `simulations=0`, `commitAttempts=0`, `commits=0`, `eventCount=1`, `protocolErrors=0`;
- terminal run state.

## Other observed runtime note

OpenCode DEBUG stderr also reported that the separate historical M1 plugin `.opencode/plugins/nyamnyam-eval.ts` failed to load with `Spread syntax requires ...iterable not be null or undefined`. This did not prevent the new direct M2 tools from loading or executing. It is not part of the M2 v1.1 transport failure/fix and should not be mixed into the v1.1 semantic condition claim.

## Freeze consequence

This run proves that direct `.opencode/tools/` discovery fixes the actual condition-v1 failure mode. However, because the checker itself is part of the condition branch and is being corrected, do not freeze `66857...` as the final v1.1 condition. Run repository-local verification and one final real-runtime preflight on the exact post-checker-fix candidate, then freeze that exact SHA before corpus collection.

No corpus task was evaluated by this preflight.