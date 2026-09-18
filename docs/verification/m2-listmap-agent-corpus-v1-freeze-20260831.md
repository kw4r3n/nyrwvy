# M2 ListMap agent corpus v1 freeze verification — 2026-08-31

## Result

The specialized M2.3 ListMap evaluation corpus is frozen before any ListMap model observation.

```text
corpus:   m2-listmap-agent-corpus-v1
protocol: m2-listmap-structural-v1
runner:   tools/Nyrwvy.AgentEval.ListMapStructural
freeze branch: eval/m2-listmap-agent-corpus-v1-20260831
freeze SHA:    a2ffb5615d37d44bbd910c4f47c36136c31cdce4
merged by:     PR #88
merge commit:  8e853c0e7189b4c7552729edc44ece8c83b9ae21
```

The freeze branch was created directly from the exact locally verified candidate SHA and is treated as immutable. It must not be force-updated. Any semantic corpus/prompt/oracle/fixture change requires a new corpus revision and fresh verification.

No ListMap OpenCode/model corpus attempt had been run when this revision was frozen.

## Local verification

Environment reported by the exact local verification:

```text
branch: eval/m2-listmap-corpus-v1-candidate
head: a2ffb5615d37d44bbd910c4f47c36136c31cdce4
dotnet-sdk: 10.0.111
full verification: PASSED
focused tests: 21/21 passed
```

The full verification script was:

```fish
bash scripts/verify-local-20260825-1427.sh --clean
```

Focused verification was:

```fish
dotnet test tests/Nyrwvy.Core.Tests/Nyrwvy.Core.Tests.fsproj \
  --configuration Release --no-build --no-restore \
  --filter 'FullyQualifiedName~M2ListMapStructuralEvaluationCorpusTests|FullyQualifiedName~ListMapStructuralAgentEvalProtocolTests|FullyQualifiedName~AgentListMapStructuralWorkflowTests'
```

All deterministic fixture exports also succeeded:

```text
/tmp/m2-listmap-ready.json
/tmp/m2-listmap-open-protected.json
/tmp/m2-listmap-resolved-protected.json
```

## Frozen task matrix

The corpus contains exactly ten tasks, `M2-L1` through `M2-L10`:

1. successful reviewed ListMap replacement using a target-owned `List<Int64>` source and compatible pure `Int64 -> Bool` mapper;
2. foreign source parameter rejection;
3. target-owned non-List source rejection;
4. unknown mapper FunctionId rejection;
5. mapper input-type mismatch rejection;
6. mapper result-type mismatch rejection;
7. effectful mapper rejection;
8. open-obligation-protected body rejection;
9. resolved historical obligation-protected body rejection;
10. stale reviewed proposal after one harness-owned unrelated publication, followed by fresh recovery preserving that publication.

The success oracle requires exactly two reviewed fresh materialized nodes with roles `ParameterReference` then `ListMap`, while keeping requested `SourceParameterId` / `MapperFunctionId` distinct from system-owned GraphNodeIds. It also verifies zero unrelated semantic churn and exact committed Typed Semantic IR -> Execution IR -> C# execution.

Codec/storage collection order is not an oracle. The reviewed structural materialization order is.

## Frozen sampling decision

Before model observation, the controlled tranche was fixed at:

```text
model: openai/gpt-5.6-terra
variant/reasoning: high
tasks: 10
fresh attempts per task: 3
target valid attempts: 30
```

Stage discipline is also fixed pre-result:

- Stage A: one fresh attempt per task;
- inspect condition validity and durable artifacts only;
- if the condition is valid, Stage B runs two additional fresh attempts per task regardless of Stage A semantic outcomes;
- preserve every attempt including failures and protocol mistakes;
- never change v1 prompt/oracle semantics in response to model results.

OpenCode/transport/prompt implementation revisions are pinned separately by the later controlled-condition work and may not mutate this corpus.

## Authority boundary

The frozen corpus evaluates only the already-merged shallow ListMap authority. It does not expose:

- raw Snapshot/ValueGraph/SemanticNode mutation;
- caller-selected production GraphNodeIds;
- generic List or expression recipes;
- generic call constructors;
- lambdas or closures;
- effectful callback authority;
- unrestricted graph editing.

The completed Float64 `m2-agent-corpus-v1` and its 30-attempt results remain independent historical evidence and are not modified or reinterpreted by this freeze.

## References

- Issue #77 — ListMap controlled evaluation umbrella
- Issue #86 — deterministic ListMap corpus freeze
- PR #88 — verified corpus merge
- PR #84 — verified `m2-listmap-structural-v1` protocol merge
