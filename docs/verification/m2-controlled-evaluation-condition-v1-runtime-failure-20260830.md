# M2 controlled structural evaluation condition v1 runtime failure — 2026-08-30

**Status:** Condition v1 is invalid for recorded model evaluation and is superseded by the v1.1 remediation tracked in Issue #68.

This record preserves the first real OpenCode Stage A collection attempt after condition v1 had passed repository-local .NET/Bun verification. It is a harness/transport failure record, not semantic task evidence.

## Frozen condition under test

- condition branch: `eval/m2-structural-condition-v1-20260830`
- condition SHA: `6cbf13695fa90b1311ae7ece7da403f7cbeafe05`
- corpus: `m2-agent-corpus-v1`
- corpus SHA: `efce0cac15565941c11c8af8d1a52e7ecafaa5c6`
- agent-visible runner protocol: `m2-structural-v1`
- intended model condition: `openai/gpt-5.6-terra`, `high`, OpenCode `run`

No corpus prompt, oracle, seed interpretation, or structural runner command/schema changed during this attempt.

## Collection result

The corrected operator collection script launched one fresh OpenCode `run` session for each M2-E1 through M2-E10.

Operator evidence for all ten attempts showed:

- `opencode` exit code `0`;
- operator JSONL output was non-empty;
- harness run-directory count remained `0 -> 0` for every task;
- therefore no `fixture.json`, `run-metadata.json`, `events.jsonl`, `metrics.json`, `run-state.json`, or committed-state verification directory was created.

The model-facing logs consistently showed the evaluated agent had received the `nyrwvy-structural-eval` instructions and knew the expected `nyrwvy_structural_eval_*` names, but OpenCode did not expose those names as actual callable tools. The model attempted several textual pseudo-tool syntaxes and then reported that the required structural-evaluation tools were unavailable.

There were no real structural protocol requests and no semantic workspace edits.

## Classification

All ten attempted Stage A sessions are classified as:

```text
invalid-condition / opencode-tool-discovery
```

They are retained as attempted runs but count as **0 valid corpus attempts**. They must not be scored as task failures and must not be included in the planned 3-attempt-per-task sample.

This follows the pre-result #51 plan: a harness/artifact/transport failure invalidates the condition, is retained, and requires a new named condition revision rather than replacement runs silently mixed into condition v1.

## Root boundary

Repository-local tests proved the TypeScript bridge helpers and plugin implementation could be imported and the F# harness behaved correctly. They did **not** prove that a real OpenCode `run --agent nyrwvy-structural-eval` session would include plugin-registered custom tools in the model's runtime tool list.

Current OpenCode documentation provides project-local `.opencode/tools/` as the direct custom-tool discovery path. Condition v1.1 therefore moves the three agent-callable bridge actions to that standard path while keeping persistent session state in a shared helper module.

## Required remediation

Issue #68 requires:

- direct custom tools with the unchanged names `nyrwvy_structural_eval_start`, `nyrwvy_structural_eval_request`, and `nyrwvy_structural_eval_finalize`;
- frozen corpus and `m2-structural-v1` unchanged;
- operator-fixed model/runtime/reasoning metadata passed without depending on a plugin `chat.message` hook;
- a real OpenCode runtime preflight proving start -> `describeV1` -> finalize and durable terminal artifacts before any replacement Stage A collection;
- a new exact condition SHA and named freeze branch.

Until that preflight succeeds, Issue #51 remains blocked from valid model collection.
