# Milestone 2 — Bounded structural expression editing

**Status:** Complete for the accepted bounded Float64 + specialized ListMap scope

Milestone 2 extends NyamNyam's agent-editing authority from the bounded M1 operations into deliberately limited structural expression authoring while preserving graph authority, stable identity, validation, review, and exact publication.

Milestone 1 remains complete for its accepted bounded-operation claim. M1-D13 explicitly left arbitrary List/numeric structural editing outside that claim; M2 adds narrow structural surfaces rather than unrestricted graph mutation. Completion here means the accepted Float64 and shallow specialized ListMap claims are implemented, versioned, locally verified, and supported by frozen controlled-agent evidence. It does **not** mean arbitrary graph editing, generic recursive List recipes, lambdas/closures, or effectful callbacks are accepted.

## Current state

| Area | Status | Evidence / authority |
| --- | --- | --- |
| M2.0 structural-edit contract | **Accepted** | [M2 decisions](../decisions/milestone-2.md), [current spec](../specs/bounded-structural-editing.md) |
| M2.1 Float64 body-replacement implementation | **Implemented and locally verified** | PR #46, [implementation verification](../verification/m2-float64-structural-editing-20260830.md), [contract coverage](../verification/m2-structural-contract-coverage-20260830.md) |
| M2.2 controlled agent evaluation | **Complete for frozen Float64 claim** | `m2-structural-v1`, frozen `m2-agent-corpus-v1`, verified/frozen [condition v1.1](../experiments/m2-agent-evaluation-condition-v1.1.md), [30-attempt evaluation record](../verification/m2-controlled-structural-agent-evaluation-20260831.md) |
| M2.3 specialized ListMap extension | **Complete for frozen specialized ListMap claim** | [ListMap decision](../decisions/milestone-2-listmap.md), [implementation verification](../verification/m2-listmap-structural-editing-20260831.md), [protocol verification](../verification/m2-listmap-structural-agent-protocol-v1-20260831.md), [corpus freeze](../verification/m2-listmap-agent-corpus-v1-freeze-20260831.md), [condition preflight](../verification/m2-listmap-controlled-condition-v1-preflight-20260831.md), [30-attempt evaluation record](../verification/m2-listmap-controlled-structural-agent-evaluation-20260831.md) |

The current implementation exposes two separate bounded structural operations through dedicated workflows/protocols; it does not widen M1 `AgentOperationIntent` into a generic graph-builder surface.

Canonical immutable freeze names are maintained as Git tags. Historical verification records may retain `branch` terminology where that describes how the freeze was originally created.

## Accepted M2.1 slice

The first structural operation replaces the complete body of one existing `Float64`-returning function.

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
tag:    eval/m2-structural-condition-v1.1-20260830
sha:    178f38274890a28ae61a1c70c9d598bc01492256
OpenCode: 1.18.18
model: openai/gpt-5.6-terra
variant: high
```

That exact revision passed clean local verification, focused structural tests, direct-tool Bun tests, and a real start -> `describeV1` -> finalize OpenCode preflight with terminal durable artifacts and zero semantic changes. See [v1.1 runtime verification](../verification/m2-controlled-evaluation-condition-v1.1-preflight-20260830.md).

### Runner boundary

The versioned persistent Float64 runner remains:

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
tag:    eval/m2-agent-corpus-v1-20260830
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

The evidence supports the current bounded Float64 structural claim. It does not support arbitrary graph editing or reliable exact construction of huge deep recipes.

## M2.3 — Specialized ListMap structural slice

Issue #52 accepted one shallow specialized `ListMap` body-replacement role in M2. The maintained decision is [Milestone 2 ListMap decision](../decisions/milestone-2-listmap.md).

Logical request:

```text
AgentReplaceFunctionBodyWithListMap
  TargetFunctionId
  ExpectedBaseSnapshotId
  ExpectedCurrentResultNodeId
  SourceParameterId
  MapperFunctionId
```

The slice is intentionally not a recursive List recipe language. It permits only:

- a target returning `List<R>`;
- one target-owned source parameter of type `List<T>`;
- one existing mapper `FunctionId` compatible with the current pure `T -> R` List callback rule;
- system-materialized fresh `ParameterReference` and `ListMap` nodes.

It reuses the existing structural invariants for exact guards, obligation protection, requested-vs-materialized review, system-owned graph identity, stale publication failure, zero unrelated churn, and exact committed-state execution.

### Implementation and protocol

The bounded operation is implemented and clean-verified under #76/#80. The exact implementation verification record is [M2 ListMap structural editing](../verification/m2-listmap-structural-editing-20260831.md).

The dedicated agent-visible protocol is:

```text
tools/NyamNyam.AgentEval.ListMapStructural
protocol: m2-listmap-structural-v1
```

It exposes only existing stable semantic IDs plus exact inspected-state guards and process-local proposal handles. It does not expose raw snapshots, generic expression/List recipes, caller-created production GraphNodeIds, lambda/closure construction, or effectful callback authority. The exact protocol candidate was clean-verified before merge; see [ListMap protocol verification](../verification/m2-listmap-structural-agent-protocol-v1-20260831.md).

### Frozen ListMap corpus

The independent ListMap corpus was frozen before model observation at:

```text
corpus: m2-listmap-agent-corpus-v1
tag:    eval/m2-listmap-agent-corpus-v1-20260831
sha:    a2ffb5615d37d44bbd910c4f47c36136c31cdce4
protocol: m2-listmap-structural-v1
```

Its ten tasks cover:

1. successful reviewed ListMap commit with exact two-node system-owned materialization and `true` execution;
2. foreign and non-List source rejection;
3. missing mapper rejection;
4. mapper input/result type mismatch rejection;
5. effectful mapper rejection under the pure-callback rule;
6. open/resolved obligation-protected body rejection;
7. stale reviewed proposal after one harness-owned unrelated publication followed by fresh recovery;
8. exact committed-state Typed Semantic IR -> Execution IR -> C# verification;
9. requested `SourceParameterId` / `MapperFunctionId` kept distinct from materialized graph identities;
10. zero unrelated semantic churn.

The freeze passed clean local verification, focused tests, and all three deterministic fixture exports. See [ListMap corpus freeze verification](../verification/m2-listmap-agent-corpus-v1-freeze-20260831.md).

The pre-result sampling plan was fixed at **three fresh attempts per M2-L1..M2-L10 (30 valid attempts target)**. Stage A was one attempt per task and served only as a condition-validity/durability gate; Stage B then ran the two remaining attempts per task without changing frozen prompts/oracles.

### Frozen ListMap condition

The real OpenCode condition is frozen at:

```text
condition: m2-listmap-condition-v1
tag:    eval/m2-listmap-condition-v1-20260831
sha:    23bf09d87c03b9c15dd4640b662a05b24aa5e330
OpenCode: 1.18.18
model: openai/gpt-5.6-terra
variant: high
prompt revision: m2-listmap-corpus-v1-seed-v1
```

That exact condition passed clean local verification, **26/26** focused .NET tests, **8/8** direct-tool Bun tests, and a real OpenCode start -> exact `describeV1` -> finalize preflight with zero simulations/commits/protocol errors and durable terminal artifacts. See [ListMap condition preflight verification](../verification/m2-listmap-controlled-condition-v1-preflight-20260831.md).

The condition uses direct project-local OpenCode tools from the start; it does not repeat the earlier unverified plugin-registration path. M2-L10's unrelated publication is harness-owned and does not widen the agent-visible command set.

### Frozen operator and completed controlled tranche

Operator-side prompt wrapping and Stage A/B orchestration were frozen before the first scored observation at:

```text
tag:    eval/m2-listmap-orchestrator-v1-20260831
sha:    f7d6d07cd47367dde2272386b6e430fddef6d03a
prompt revision: m2-listmap-corpus-v1-seed-v1
```

The completed ListMap tranche contains **30 valid autonomous attempts** across M2-L1..M2-L10. See [M2 ListMap controlled structural agent evaluation](../verification/m2-listmap-controlled-structural-agent-evaluation-20260831.md).

Headline evidence:

- frozen machine-oracle pass: **30/30 (100%)**;
- exact expected structured diagnostics for rejection tasks: **24/24**;
- successful reviewed publications: **6/6**;
- exact two-node system-owned `ParameterReference -> ListMap` materialization and durable Typed Semantic IR -> Execution IR -> C# verification: **6/6**, execution output `true` in all six;
- M2-L10 stale reviewed-proposal rejection and fresh recovery: **3/3**, with the harness-owned unrelated rename preserved;
- zero unrelated semantic churn: **30/30**;
- protocol errors: **0**;
- explicit agent proposal rejects: **0**;
- simulations: **33**, exactly the minimum implied by three L1 successes, twenty-four expected rejections, and two simulations in each of three L10 stale-recovery attempts.

The frozen operator's TSV summary initially undercounted L2-L9 because it required `harness-events.jsonl` to be non-empty even when no harness-owned event was expected. Raw durable artifacts established the 30/30 result without changing any oracle. The bug was tracked in #96 and fixed **after** the tranche in #99; frozen Stage A/B evidence remains on the unchanged `f7d6d07c...` operator revision.

The completed Float64 corpus/results remain immutable and independent; ListMap evidence does not reinterpret or rescore them.

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
6. **Complete:** run and review the 30-attempt controlled Float64 Stage A/Stage B tranche under Issue #51.
7. **Complete:** decide under Issue #52 to add one shallow specialized ListMap slice.
8. **Complete:** implement and locally verify the bounded ListMap operation under #76/#80.
9. **Complete:** add and locally verify dedicated `m2-listmap-structural-v1` protocol under #82/#84.
10. **Complete:** freeze `m2-listmap-agent-corpus-v1` at `a2ffb5615d37d44bbd910c4f47c36136c31cdce4` before model observation.
11. **Complete:** verify/freeze real ListMap OpenCode condition v1 at `23bf09d87c03b9c15dd4640b662a05b24aa5e330`.
12. **Complete:** freeze operator prompt/orchestration at `f7d6d07cd47367dde2272386b6e430fddef6d03a` before scored observation.
13. **Complete:** run and review the 30-attempt controlled ListMap Stage A/Stage B tranche under #77 and record the result.
14. **Complete:** fix the post-tranche operator summary bug under #96/#99 without changing frozen evidence.

## Core exit criteria

| # | Criterion | Status |
| ---: | --- | --- |
| 1 | Target, recipe, identity, replacement, obligation, and review contract accepted | **Complete** |
| 2 | Ordinary structural authority remains bounded; no raw Snapshot/storage mutation surface | **Complete for implemented slices** |
| 3 | New structural identities are system-issued | **Complete** |
| 4 | Exact inspected-base/target preconditions and stale-proposal failure are enforced | **Complete** |
| 5 | Requested structural intent and materialized result remain distinct review facts | **Complete** |
| 6 | Float64 recipe passes semantic-to-execution behavior | **Complete for Float64 slice** |
| 7 | Clean local verification exists for exact implementation heads | **Complete for Float64 and ListMap** |
| 8 | Versioned structural runner protocols and frozen corpora exist | **Complete for Float64 and ListMap** |
| 9 | Real-runtime controlled OpenCode conditions are verified | **Complete for Float64 v1.1 and ListMap v1** |
| 10 | Controlled structural agent evaluation records exist | **Complete for Float64 and ListMap** |
| 11 | Current docs accurately state the implemented versus evaluated scope | **Complete** |
| 12 | ListMap extension decision is evidence-backed and remains bounded | **Complete — #52** |
| 13 | Bounded ListMap implementation and controlled evidence exist | **Complete — 30/30 frozen machine-oracle pass** |

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