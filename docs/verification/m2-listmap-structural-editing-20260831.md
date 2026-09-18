# M2 ListMap structural editing verification — 2026-08-31

## Status

**Verified and merged.**

The bounded M2.3 `ListMap` complete-body replacement implementation was clean-verified on the exact candidate head before merge.

## Exact implementation revision

```text
branch: feat/issue-76-bounded-listmap-body-replacement
verified head: d5e5bb445bf0dacaff781ff1cca6b12cbba20671
merge PR: #80
main merge: 638cf453414b2c39f4a25ecea40d1d5b3304351d
SDK: 10.0.111
```

Draft PR #79 used the same exact candidate head but was closed unmerged because the draft-to-ready connector mutation remained broken. Replacement PR #80 merged the unchanged verified head.

## Verification result

The clean verification command was:

```text
bash scripts/verify-local-20260825-1427.sh --clean
```

Observed on the exact candidate head:

- clean worktree: yes;
- Release build: **0 warnings / 0 errors**;
- full test suite: **222 passed / 0 failed / 0 skipped**;
- focused `AgentListMapStructuralWorkflowTests | AgentStructuralWorkflowTests | ListHigherOrderTests`: **24 passed / 0 failed / 0 skipped**;
- canonical execution/backend/query/viewer/workspace and M0/M1 focused verification phases all passed;
- encoding comparison remained JSON 8440 bytes / Nyason 16205 bytes / ratio 1.920;
- terminal clean script result: `=== VERIFICATION PASSED ===`.

Local log reported by the operator:

```text
/tmp/nyrwvy-verification/local-verify-20260831-030135.log
```

## First pre-candidate verification failure

An earlier candidate (`41023810bff740d999b135280cf3be7b518ec008`) failed while compiling the new test file because seven list-valued xUnit `Assert.Equal` calls were ambiguous between generic equality and sequence overloads in F#.

This was a test-code overload-resolution issue, not a semantic implementation failure. The list-valued assertions were changed to F# structural equality, the branch was re-squashed to one commit, and the exact replacement candidate `d5e5bb...` then passed all verification above.

The focused `dotnet test --no-build --no-restore` failure in that first attempt was secondary: the failed build had not produced the test assembly.

## Verified authority boundary

The accepted public request remains deliberately shallow:

```text
AgentReplaceFunctionBodyWithListMap
  TargetFunctionId
  ExpectedBaseSnapshotId
  ExpectedCurrentResultNodeId
  SourceParameterId
  MapperFunctionId
```

Preflight occurs before production graph-node allocation and requires:

- exact current base snapshot and result-node guards;
- existing target function returning `List<R>`;
- target-owned source parameter of type `List<T>`;
- existing mapper function;
- exactly one mapper parameter;
- mapper input type exactly `T`;
- mapper result type exactly `R`;
- mapper with no required effects;
- no open or resolved durable obligation targeting the old body.

The success case materializes exactly two fresh system-owned nodes:

1. `ParameterReference(SourceParameterId)`;
2. `ListMap(sourceNodeId, MapperFunctionId)`.

The request exposes no raw `Snapshot`, `ValueGraph`, `SemanticNode`, caller-selected production `GraphNodeId`, generic call/node constructor, lambda/closure construction, or effectful callback authority.

## Verified behavior

Dedicated tests cover:

- exact two-node materialization order, types, dependencies, and fresh UUIDv7 graph identities;
- target/base/result/source guards;
- foreign and non-List source-parameter rejection;
- unknown mapper, mapper arity/input/result mismatch, and effectful mapper rejection;
- stable target signature and zero caller/declaration/unrelated-function churn;
- stale reviewed proposal rejection after ordinary workspace advance;
- open and resolved obligation-protected body rejection;
- JSON and Nyason snapshot roundtrip with identical snapshot/semantic fingerprints;
- reflection-based authority-surface checks;
- exact committed-state projection through Typed Semantic IR -> Execution IR -> C#;
- generated C# using `.Map(NyFunctions.<mapper>)`, building successfully, and executing the caller result as `true`.

The existing Float64 `AgentStructuralWorkflow`, frozen `m2-structural-v1` protocol, and completed Float64 corpus/evaluation were not widened or reinterpreted by this implementation.

## Claim boundary

This verification supports the implementation claim for one bounded shallow `ListMap` complete-body replacement. It does **not** yet provide controlled model evidence for ListMap agent use.

The next evidence gate is Issue #77: freeze a ListMap-specific versioned adapter/corpus/condition before model observation, then run the controlled ListMap tranche. The completed Float64 condition and results remain immutable historical evidence.
