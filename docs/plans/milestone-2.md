# Milestone 2 — Bounded structural expression editing

**Status:** Active

Milestone 2 extends NyamNyam's agent-editing authority from the bounded M1 operations into deliberately limited structural expression authoring while preserving graph authority, stable identity, validation, review, and exact publication.

Milestone 1 remains complete for its accepted bounded-operation claim. M1-D13 explicitly left arbitrary List/numeric structural editing outside that claim; M2 begins by adding one narrow structural surface rather than unrestricted graph mutation.

## Current state

| Area | Status | Evidence / authority |
| --- | --- | --- |
| M2.0 structural-edit contract | **Accepted** | [M2 decisions](../decisions/milestone-2.md), [current spec](../specs/bounded-structural-editing.md) |
| M2.1 Float64 body-replacement implementation | **Implemented and locally verified** | PR #46, [implementation verification](../verification/m2-float64-structural-editing-20260830.md), [contract coverage](../verification/m2-structural-contract-coverage-20260830.md) |
| M2.2 controlled agent evaluation | **Complete for frozen Float64 claim** | `m2-structural-v1`, frozen `m2-agent-corpus-v1`, verified/frozen [condition v1.1](../experiments/m2-agent-evaluation-condition-v1.1.md), [30-attempt evaluation record](../verification/m2-controlled-structural-agent-evaluation-20260831.md) |
| M2.3 specialized ListMap extension | **Accepted; implementation next** | [ListMap decision](../decisions/milestone-2-listmap.md), #76 implementation, #77 controlled evaluation |

The current implementation exposes the first structural operation through `AgentStructuralWorkflow`; it does not widen M1 `AgentOperationIntent` into a generic graph-builder surface.

## Accepted M2.1 slice

The current operation replaces the complete body of one existing `Float64`-returning function.

Logical request:

```text
AgentReplaceFunctionBodyWithFloat64Recipe
  TargetFunctionId
  ExpectedBaseSnapshotId
  ExpectedCurrentResultNodeId
  Replacement
```

Accepted recipe:

```text
Parameter(ParameterId)
Literal(Float64Bits)
Add(left, right)
```

The slice proves that a bounded structural workflow can:

- target an existing stable function and exact inspected body state;
- validate scope/type/recipe bounds before materialization;
- allocate all new body-node identities system-side;
- replace the old body with a disjoint fresh body-node set;
- reject bodies referenced by open or resolved obligations;
- preserve requested recipe separately from materialized structural diff;
- expose both base and proposed Focus views;
- bind publication to exact reviewed fingerprints;
- fail stale publication closed;
- preserve workspace publication history;
- execute the committed state through Typed Semantic IR, Execution IR, and C#.

The exact current contract is maintained in [Bounded structural editing](../specs/bounded-structural-editing.md).

## M2.2 — Controlled agent evaluation

The controlled evidence gate for the Float64 structural slice is complete. The structural runner, frozen corpus, real OpenCode condition, and 30 valid autonomous attempts are independently recorded.

Condition v1 (`6cbf13695fa90b1311ae7ece7da403f7cbeafe05`) passed repository-local verification but failed real OpenCode tool discovery. Ten fresh sessions exposed no callable M2 bridge tool and produced zero harness run directories. Those sessions remain invalid-condition evidence and contribute zero valid corpus attempts. See [runtime failure record](../verification/m2-controlled-evaluation-condition-v1-runtime-failure-20260830.md).

Condition v1.1 fixes only the OpenCode transport boundary and is frozen at:

```text
branch: eval/m2-structural-condition-v1.1-20260830
sha:    178f38274890a28ae61a1c70c9d598bc01492256
OpenCode: 1.18.18
model: openai/gpt-5.6-terra
variant: high
```

That exact revision passed clean local verification, focused structural tests, direct-tool Bun tests, and a real start -> `describeV1` -> finalize OpenCode preflight with terminal durable artifacts and zero semantic changes. See [v1.1 runtime verification](../verification/m2-controlled-evaluation-condition-v1.1-preflight-20260830.md).

### Runner boundary

The versioned persistent runner remains:

```text
tools/NyamNyam.AgentEval.Structural
protocol: m2-structural-v1
```

It exposes only the accepted structural request:

- target `FunctionId`;
- expected base `SnapshotId`;
- expected current result `GraphNodeId`;
- closed Float64 recipe;
- exact Float64 hexadecimal transport where text encoding is required.

The runner does not expose:

- raw snapshots or graph payloads;
- caller-created production node IDs;
- generic node constructors;
- JSON/Nyason mutation;
- privileged fixture controls as agent commands.

The verified protocol itself is recorded in [M2 structural agent-eval protocol verification](../verification/m2-structural-agent-eval-v1-20260830.md). The condition-v1 transport failure did not change that runner/protocol claim. M1 corpus/tool revisions remain frozen and are not reinterpreted by this addition.

### Frozen corpus

`m2-agent-corpus-v1` was frozen before valid model runs.

```text
branch: eval/m2-agent-corpus-v1-20260830
sha:    efce0cac15565941c11c8af8d1a52e7ecafaa5c6
protocol: m2-structural-v1
```

The corpus covers:

1. successful parameter-plus-literal body replacement;
2. expected-base and expected-result mismatch recovery;
3. foreign/wrong-type parameter rejection;
4. recipe depth/node-limit rejection;
5. open/resolved obligation-protected body rejection;
6. exact system-owned identity allocation and materialization order;
7. structural before/after review completeness;
8. stale proposal rejection and fresh recovery;
9. exact committed-state lowering/execution;
10. zero unrelated semantic churn.

Task oracles inspect deterministic semantic artifacts and execution results rather than relying on agent prose. The machine-readable corpus, deterministic fixture bindings, exporter support, and expected failure classes are regression-tested at the frozen SHA. See [M2 structural agent corpus v1 freeze verification](../verification/m2-agent-corpus-v1-freeze-20260830.md).

Any prompt, oracle, fixture-binding, or semantic harness change after this freeze requires a new corpus version. Transport-condition revisions may be named separately when they do not change those frozen semantics.

### Completed evaluation record

The final controlled tranche used three fresh attempts for each M2-E1..M2-E10, for **30 valid attempts** total. See [M2 controlled structural agent evaluation](../verification/m2-controlled-structural-agent-evaluation-20260831.md).

Headline evidence:

- frozen machine-oracle pass: **29/30 (96.7%)**;
- stricter exact depth-33 prompt-fidelity analysis: **27/30 (90.0%)**, entirely due to M2-E6;
- clean oracle-correct attempts without avoidable agent reject/protocol-error retries: **24/30**;
- exact committed-state verification: **12/12 passed**;
- E1 exact behavior: **3/3 = 3.75**;
- expected-base/result recovery: **6/6**;
- parameter scope/type and obligation protection: **12/12**;
- E10 stale reviewed-proposal recovery: **3/3**;
- terminal unrelated semantic churn: **0 in 30/30 attempts**.

The systematic weakness is exact construction of very deep recursive explicit JSON recipes. M2-E6/a1 never reached the boundary, while a2/a3 obtained the expected depth-limit diagnostic with depth-34 recipes rather than the requested depth 33. This is tracked by Issue #73 without changing the frozen oracle. All E10 attempts also retained the old stale proposal handle at terminal state; that lifecycle finding is tracked by Issue #74 and is not an E10 semantic-oracle failure.

The evidence supports the current bounded Float64 structural claim. It does not support arbitrary graph editing, reliable exact construction of huge deep recipes, or any List/higher-order structural claim without separate implementation/evaluation.

## M2.3 — Specialized ListMap structural slice

Issue #52 is complete and accepts one shallow specialized `ListMap` body-replacement role in M2. The maintained decision is [Milestone 2 ListMap decision](../decisions/milestone-2-listmap.md).

Logical request direction:

```text
AgentReplaceFunctionBodyWithListMap
  TargetFunctionId
  ExpectedBaseSnapshotId
  ExpectedCurrentResultNodeId
  SourceParameterId
  MapperFunctionId
```

The first slice is intentionally not a recursive List recipe language. It permits only:

- a target returning `List<R>`;
- one target-owned source parameter of type `List<T>`;
- one existing mapper `FunctionId` compatible with the current pure `T -> R` List callback rule;
- system-materialized fresh `ParameterReference` and `ListMap` nodes.

It reuses the existing structural invariants for exact guards, obligation protection, requested-vs-materialized review, system-owned graph identity, stale publication failure, zero unrelated churn, and exact committed-state execution.

The completed Float64 evidence is the reason to keep this role shallow. M2-E6 showed that exact construction of very deep recursive JSON is the current ergonomics weakness; the next slice should add higher-order/type/effect evidence instead of adding more recursive syntax.

Implementation is tracked by **#76**. A separate List-specific frozen corpus and controlled evaluation are tracked by **#77**. The existing Float64 corpus/results remain immutable and complete independently.

Out of scope for M2.3:

- arbitrary nested List source recipes;
- ListFilter/ListAny/ListAll in the same slice;
- generic call/graph constructors;
- generic lambdas/closures;
- effectful List callbacks;
- unrestricted graph editing.

## Work sequence

1. **Complete:** define and accept the first bounded structural contract.
2. **Complete:** implement and locally verify the Float64 complete-body replacement slice.
3. **Complete:** add and verify the restricted `m2-structural-v1` runner protocol.
4. **Complete:** freeze `m2-agent-corpus-v1` before valid model runs at `efce0cac15565941c11c8af8d1a52e7ecafaa5c6`.
5. **Complete:** verify/freeze real OpenCode condition v1.1 at `178f38274890a28ae61a1c70c9d598bc01492256`.
6. **Complete:** run and review the 30-attempt controlled Stage A/Stage B tranche under Issue #51.
7. **Complete:** decide under Issue #52 to add one shallow specialized ListMap slice.
8. **Next:** implement and locally verify the bounded ListMap operation under Issue #76.
9. Freeze and run the ListMap-specific controlled corpus under Issue #77.
10. Update current references and close only the evidenced M2 claim.

## Core exit criteria

| # | Criterion | Status |
| ---: | --- | --- |
| 1 | Target, recipe, identity, replacement, obligation, and review contract accepted | **Complete** |
| 2 | Ordinary structural authority remains bounded; no raw Snapshot/storage mutation surface | **Complete for M2.1** |
| 3 | New structural identities are system-issued | **Complete** |
| 4 | Exact inspected-base/target preconditions and stale-proposal failure are enforced | **Complete** |
| 5 | Requested structural intent and materialized result remain distinct review facts | **Complete** |
| 6 | Float64 recipe passes semantic-to-execution behavior | **Complete for implemented slice** |
| 7 | Clean local verification exists for the exact implementation head | **Complete** |
| 8 | Versioned structural runner protocol and frozen M2 corpus exist | **Complete** |
| 9 | Real-runtime controlled OpenCode condition is verified | **Complete — v1.1** |
| 10 | Controlled structural agent evaluation record exists | **Complete — 30 valid attempts** |
| 11 | Current docs accurately state the implemented versus evaluated scope | **Complete for current state** |
| 12 | ListMap extension decision is evidence-backed and remains bounded | **Complete — #52** |
| 13 | Bounded ListMap implementation and controlled evidence exist | Not started |

## Non-goals

Milestone 2 does not currently require:

- unrestricted graph construction or snapshot replacement;
- structural operations for every implemented node kind;
- nested expression/subgraph targeting or expression-slot identity;
- identity-preserving local rewrites;
- general dead-node analysis or GC;
- atomic composition of structural replacement with M1 operations;
- generic lambdas/closures;
- implicit numeric conversion or generic numeric operators;
- semantic merge/rebase or multi-agent proposal composition;
- a final persistence-medium choice;
- broad visual programming/editor work;
- ownership/borrowing semantics or another backend.

## Relationship to open questions

M2 provides evidence about local identity and structural review but does not silently resolve the broader identity, persistence, semantic merge/rebase, obligation-state, or workspace-view questions in [Open questions](../decisions/open-questions.md). Durable resolutions still require explicit decisions/specifications.
