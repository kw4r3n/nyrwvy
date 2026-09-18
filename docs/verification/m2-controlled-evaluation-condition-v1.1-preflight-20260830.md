# M2 controlled evaluation condition v1.1 runtime preflight — 2026-08-30

**Status:** Verified and frozen for recorded model runs.

This record verifies the real OpenCode transport/runtime boundary for M2 controlled condition v1.1. The condition preserves the frozen `m2-agent-corpus-v1`, the `m2-structural-v1` runner protocol, task prompts/oracles/fixtures, minimal seed policy, and F# semantic harness.

## Exact revisions

- condition branch: `eval/m2-structural-condition-v1.1-20260830`
- exact condition SHA: `178f38274890a28ae61a1c70c9d598bc01492256`
- integration PR: #71
- main merge commit: `abdb6276b67e36deb917a22f1e81623689ad4905`
- frozen corpus branch: `eval/m2-agent-corpus-v1-20260830`
- frozen corpus SHA: `efce0cac15565941c11c8af8d1a52e7ecafaa5c6`
- protocol: `m2-structural-v1`
- OpenCode: `1.18.18`
- model: `openai/gpt-5.6-terra`
- variant: `high`

The condition SHA is the exact revision that passed both clean repository-local verification and the final real OpenCode runtime preflight. Later documentation/operator-checker changes do not redefine the condition.

## Repository-local verification

Environment:

- .NET SDK: `10.0.111`
- exact branch head: `178f38274890a28ae61a1c70c9d598bc01492256`

Clean verification:

```text
bash scripts/verify-local-20260825-1427.sh --clean
```

Result:

- Release build: **0 warnings, 0 errors**
- full suite: **213 passed, 0 failed, 0 skipped**
- all required M0/M1 focused phases: **passed**
- terminal result: `=== VERIFICATION PASSED ===`

Additional focused structural tests:

```text
dotnet test tests/Nyrwvy.Core.Tests/Nyrwvy.Core.Tests.fsproj \
  --configuration Release --no-build --no-restore \
  --filter 'FullyQualifiedName~StructuralEvalHarnessTests|FullyQualifiedName~StructuralAgentEvalProtocolTests|FullyQualifiedName~M2StructuralEvaluationCorpusTests'
```

Result: **16 passed, 0 failed, 0 skipped**.

Direct custom-tool Bun tests:

```text
bun test ./.opencode/tests/nyrwvy-structural-eval.test.ts
```

Result: **8 passed, 0 failed**, 32 assertions.

The Bun suite includes direct imports of all three `.opencode/tools/` modules, request framing/validation, frozen-input pinning, E10-only perturbation configuration, operator-fixed metadata, and private Unix permissions.

## Final real-runtime preflight

Command:

```text
fish scripts/preflight-m2-structural-opencode.fish
```

Result on exact SHA `178f38274890a28ae61a1c70c9d598bc01492256`:

```text
=== RUNTIME PREFLIGHT PASSED ===
```

The uploaded archive contained exactly one harness run directory plus OpenCode JSON stdout and DEBUG stderr.

OpenCode emitted real completed `tool_use` events for each bridge action exactly once:

1. `nyrwvy_structural_eval_start`
2. `nyrwvy_structural_eval_request`
3. `nyrwvy_structural_eval_finalize`

The request tool input was exactly:

```json
{"command":"describeV1"}
```

The F# runner's `events.jsonl` contained exactly one request, also `describeV1`.

Terminal runner metrics were:

- `toolCalls`: 1
- `focusQueries`: 0
- `simulations`: 0
- `commitAttempts`: 0
- `commits`: 0
- `agentRejects`: 0
- `protocolErrors`: 0
- `rejectedSimulations`: 0
- `rejectedCommits`: 0
- `eventCount`: 1
- `pendingProposalCount`: 0
- `zeroUnrelatedSemanticChurn`: true
- `harnessPerturbationApplied`: false
- `harnessPerturbationPreserved`: true
- verification reports: none, as expected because no semantic commit occurred

`run-state.json` was terminal and retained `snapshot-m2-eval-ready` with the original semantic fingerprint.

`run-metadata.json` recorded:

- corpus revision `efce0cac15565941c11c8af8d1a52e7ecafaa5c6`;
- protocol `m2-structural-v1`;
- model `openai/gpt-5.6-terra`;
- provider/runtime `opencode`;
- reasoning `high`;
- OpenCode `1.18.18`;
- repository/tool revision `178f38274890a28ae61a1c70c9d598bc01492256`.

## Earlier checker false positive

The first real-runtime v1.1 preflight on an earlier candidate had already proved the direct tool transport, but the operator shell checker searched the entire `describeV1` response for semantic command names. Because `describeV1` intentionally lists supported commands, that run was falsely reported as if a simulation/commit had occurred.

The checker was corrected to inspect the exact runner request and private counters. The final exact-condition rerun above passed normally. The false-positive attempt remains evidence and is not a corpus attempt.

## Condition-v1 contrast

Condition v1 had loaded the evaluation-agent instructions but exposed no callable M2 bridge tools in ten fresh OpenCode sessions; zero harness run directories were created. Those sessions remain `invalid-condition / opencode-tool-discovery` and count as **0 valid corpus attempts**.

Condition v1.1 removes the failed plugin registration dependency by using direct project-local custom tools under `.opencode/tools/` with shared persistent state under `.opencode/lib/`.

## Separate M1 runtime note

DEBUG stderr reported an unrelated load failure for the historical M1 plugin `.opencode/plugins/nyrwvy-eval.ts` under OpenCode 1.18.18. It did not prevent the M2 direct tools from loading or executing. This is tracked separately in Issue #70 and is not an M2 v1.1 blocker.

## Freeze consequence

Condition v1.1 is now cleared for the previously fixed #51 sampling plan:

- Stage A: one fresh valid attempt per M2-E1..E10;
- inspect harness validity/durability only before continuing;
- if Stage A condition artifacts are valid, Stage B adds two fresh attempts per task regardless of semantic Stage A results;
- target: 3 valid attempts/task, 30 valid attempts total.

The original condition-v1 invalid attempts do not enter this sample. Any later transport/harness condition change requires another named condition revision; any task/oracle/fixture semantic change requires a new corpus version.
