# M2 controlled structural evaluation condition v1 verification — 2026-08-30

**Status:** Verified and frozen before recorded model runs.

This record verifies the controlled OpenCode execution condition for the already frozen `m2-agent-corpus-v1`. It does not change the corpus, its task oracles, the minimal seed interpretation fixed before model observation, or the agent-visible `m2-structural-v1` protocol.

## Exact revisions

- Condition freeze branch: `eval/m2-structural-condition-v1-20260830`
- Exact verified condition SHA: `6cbf13695fa90b1311ae7ece7da403f7cbeafe05`
- Main integration PR: #66
- Main merge commit: `dcf37897a4d0f33e7ab628cdef2661d895df9ac8`
- Frozen corpus branch: `eval/m2-agent-corpus-v1-20260830`
- Frozen corpus SHA: `efce0cac15565941c11c8af8d1a52e7ecafaa5c6`
- Agent-visible protocol: `m2-structural-v1`
- OpenCode evaluation agent: `nyamnyam-structural-eval`

The verified condition SHA is retained directly in repository history by a normal merge and is also fixed on the dedicated condition branch. Recorded M2 model runs must use that exact condition revision rather than an arbitrary later `main` revision.

## Final local verification

Environment:

- local date: 2026-08-30
- .NET SDK: `10.0.111`
- exact branch head: `6cbf13695fa90b1311ae7ece7da403f7cbeafe05`

Clean verification command:

```text
bash scripts/verify-local-20260825-1427.sh --clean
```

Result:

- Release build: **0 warnings, 0 errors**
- full test suite: **213 passed, 0 failed, 0 skipped**
- all required M0/M1 focused phases: **passed**
- JSON/Nyason comparison artifact: produced
- terminal result: `=== VERIFICATION PASSED ===`

Additional focused structural condition tests:

```text
dotnet test tests/NyamNyam.Core.Tests/NyamNyam.Core.Tests.fsproj \
  --configuration Release --no-build --no-restore \
  --filter 'FullyQualifiedName~StructuralEvalHarnessTests|FullyQualifiedName~StructuralAgentEvalProtocolTests|FullyQualifiedName~M2StructuralEvaluationCorpusTests'
```

Result: **16 passed, 0 failed, 0 skipped**.

OpenCode bridge tests:

```text
bun test ./.opencode/plugins/nyamnyam-structural-eval.test.ts
```

Result: **7 passed, 0 failed**, 26 assertions.

The Bun suite covered response framing, one-object request validation, artifact-root isolation, frozen corpus/fixture/exporter revision pinning, E10-only stale perturbation startup configuration, private metadata forwarding, and Unix private permission helpers.

## Initial failed attempt retained

The first local attempt on `afab3afa8fc5bd0a60687490e0e4b609eb4f7f0e` failed before any model execution:

- one F# parser error in `Program.fs` from an extra closing parenthesis;
- the subsequent `dotnet test --no-build` had no valid built test assembly and therefore did not provide test evidence;
- the initial Bun command used `.opencode/...` as a filter-like argument rather than `./.opencode/...` as a path, so no Bun test file was selected.

The F# syntax and Bun invocation were corrected, the branch was re-squashed, and the exact final condition head above was clean-verified. These were harness/test invocation defects, not observed model behavior.

## Verified condition properties

The final condition establishes, before recorded model observation:

- a dedicated `nyamnyam-structural-eval` agent isolated from repository/file/shell access;
- only persistent start/request/finalize bridge tools exposed to that agent;
- first runner request required to be `describeV1`;
- one persistent `NyamNyam.AgentEval.Structural` process per evaluated agent session;
- no change to the agent-visible `m2-structural-v1` command/schema/authority surface;
- startup rejection when the current corpus directory, M2 fixture source, or fixture exporter differs from frozen corpus SHA `efce0cac...`;
- private run metadata and complete request/response transcript outside the worktree;
- private rejected-simulation and rejected-commit counts;
- terminal workspace identity, fingerprints, pending-proposal count, and zero-unrelated-semantic-churn result;
- exact committed-state Typed Semantic IR, Execution IR, generated C#, C# build, and behavioral execution evidence for semantically-ready commits;
- M2-E1 exact behavioral oracle `evaluate_metric() == 3.75`;
- M2-E10 harness-owned unrelated structural publication between review and commit, with private receipt and terminal preservation check;
- artifact/finalization failure treated as an invalid controlled run rather than silently accepted evidence.

## Frozen pre-run interpretation

No model output was observed before the following condition rules were fixed.

### E2 / E3 literal wording

The frozen v1 task text says “requested literal replacement” without fixing literal bits. The frozen machine oracle is therefore guard rejection followed by fresh reviewed recovery and a valid structural commit, not one preferred resulting literal value. Successful commits still require exact-state lowering/build/execution evidence.

### Minimal seed policy

Every task receives only:

```text
function-m2-eval-target
```

Additional initial stable IDs:

| Task | Additional seed |
| --- | --- |
| M2-E2 | `snapshot-m2-eval-wrong-base` |
| M2-E3 | `node-m2-eval-wrong-result` |
| M2-E4 | `parameter-m2-eval-foreign-value` |
| M2-E1, E5-E10 | none |

Correct current snapshot/result guards and target parameter identities must be discovered through the bounded protocol after `describeV1`.

Changing this seed policy after observing model results creates a new named evaluation condition. Changing prompt/oracle semantics requires a new corpus version.

## Claim boundary

This verification proves the controlled **evaluation condition is ready**. It is not model-performance evidence and does not satisfy M2 exit criterion 9 by itself.

Issue #51 remains the evidence gate for recorded autonomous runs. The condition verification does not extend structural authority beyond complete Float64 function-body replacement and does not add List structural editing.
