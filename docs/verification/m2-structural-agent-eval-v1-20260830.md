# M2 structural agent-eval protocol v1 verification — 2026-08-30

**Status:** Observed result

This record captures local verification for the first versioned M2 structural agent-evaluation adapter. It is protocol/infrastructure evidence, not a controlled model-evaluation result.

## Verified protocol condition

- Repository: `kw4r3n/nyrwvy-lang` (the repository's name at the time; now `kw4r3n/nyrwvy`)
- Implementation issue: #49
- Verified PR head: `1cfd3f9f8f220deecbd188d3a5836e4e295e6d32`
- Merge PR: #57
- Merge commit preserving the verified head as a parent: `2e4252f8aea0c90e1064e734109af89926664d83`
- Branch: `feat/issue-49-structural-agent-eval-v1`
- Protocol identifier: `m2-structural-v1`
- Executable: `tools/Nyrwvy.AgentEval.Structural`
- .NET SDK: `10.0.111`
- Final verification time: `2026-08-30T06:47:37+09:00`

The adapter is a separate executable from `Nyrwvy.AgentEval`; the recorded M1 command/tool condition was not widened in place.

## Final clean verification

Command:

```shell
bash scripts/verify-local-20260825-1427.sh --clean
```

Result:

```text
Release build:
  0 warnings
  0 errors

Full test suite:
  passed: 202
  failed: 0
  skipped: 0

VERIFICATION PASSED
```

All existing M0/M1 focused verification phases passed. The verified base also already contained the #48 structural-contract coverage, so the final run exercised the protocol adapter together with the accepted implementation and its focused contract-edge regressions.

## Agent-visible boundary

The adapter exposes the versioned command set:

- `describeV1`;
- `stateV1`;
- `focusFunctionV1`;
- `simulateStructuralV1`;
- `commitStructuralV1`;
- `commitStructuralAllowIncompleteV1`;
- `rejectStructuralV1`;
- `metricsV1`.

`simulateStructuralV1` accepts only:

- target `FunctionId`;
- expected base `SnapshotId`;
- expected current result `GraphNodeId`;
- the closed `Parameter` / `Literal` / `Add` Float64 recipe.

Exact Float64 text transport uses 16 hexadecimal digits and is parsed through the existing `Float64Bits` contract.

The protocol rejects unsupported fields, so callers cannot inject new production graph-node IDs, raw snapshots, raw `ValueGraph` payloads, or raw semantic nodes.

## Reviewed proposal behavior

Regression tests verify that:

- simulation returns the requested recipe separately from materialized review facts;
- generated graph-node IDs remain system-owned;
- exact literal bits, including NaN canonicalization, are visible in review;
- opaque proposal handles survive across commands in one persistent session;
- commit and reject operate on the stored reviewed proposal;
- a second proposal from an advanced base rejects with `StaleBaseSnapshot`;
- structured workflow diagnostics survive protocol transport;
- CLI startup pins the exact `m2-structural-v1` condition.

## Compile corrections during verification

Several pre-final heads exposed F# compile-only issues in the new adapter/tests: ambiguous JSON overload inference, record inference around diagnostic types, private constructor accessibility, chained JSON indexing syntax, and an ambiguous xUnit string `Assert.NotEqual` overload.

Each was corrected without changing the semantic authority surface. The branch was then rebuilt onto the #48-integrated `main` and the final clean run above passed from deleted `bin/obj` state.

## Evaluation boundary

This record does not establish structural-agent reliability. Before model results are interpreted, `m2-agent-corpus-v1` must be frozen against this protocol condition and the controlled M2 run tranche must record every attempt separately from the M1 corpus/results.
