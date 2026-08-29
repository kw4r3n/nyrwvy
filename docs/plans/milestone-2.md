# Milestone 2 — Bounded structural expression editing

**Status:** Active

Milestone 2 extends NyamNyam's agent-editing authority from the bounded M1 operations into deliberately limited structural expression authoring while preserving graph authority, stable identity, validation, review, and exact publication.

Milestone 1 remains complete for its accepted bounded-operation claim. M1-D13 explicitly left arbitrary List/numeric structural editing outside that claim; M2 begins by adding one narrow structural surface rather than unrestricted graph mutation.

## Current state

| Area | Status | Evidence / authority |
| --- | --- | --- |
| M2.0 structural-edit contract | **Accepted** | [M2 decisions](../decisions/milestone-2.md), [current spec](../specs/bounded-structural-editing.md) |
| M2.1 Float64 body-replacement implementation | **Implemented and locally verified** | PR #46, [implementation verification](../verification/m2-float64-structural-editing-20260830.md), [contract coverage](../verification/m2-structural-contract-coverage-20260830.md) |
| M2.2 controlled agent evaluation | **Ready for recorded runs** | `m2-structural-v1`, [protocol verification](../verification/m2-structural-agent-eval-v1-20260830.md), frozen `m2-agent-corpus-v1`, [freeze verification](../verification/m2-agent-corpus-v1-freeze-20260830.md) |
| Optional specialized List extension | **Deferred pending evidence** | decide after controlled structural-edit evaluation |

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

The next milestone gate is recorded model evidence, not more semantic breadth. The restricted runner and frozen structural corpus now exist and have deterministic local verification.

### Runner boundary

The current versioned persistent runner is:

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

The exact verified protocol condition is recorded in [M2 structural agent-eval protocol verification](../verification/m2-structural-agent-eval-v1-20260830.md). M1 corpus/tool revisions remain frozen and are not reinterpreted by this addition.

### Frozen corpus

`m2-agent-corpus-v1` is frozen before observed model runs.

Frozen condition:

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

Any prompt, oracle, fixture-binding, or harness-semantics change after this freeze requires a new corpus version.

### Evaluation record

Issue #51 is now the active evidence gate. Record every attempt, including unsuccessful ones, with:

- repository/tool/corpus revisions;
- model/runtime/reasoning metadata where available;
- command and retry counts;
- intervention class;
- failure category;
- structural semantic churn;
- exact terminal and verification artifacts.

No success percentage is predetermined. The gate is reviewed evidence that clearly states useful behavior and remaining failure modes.

## Optional specialized List extension

Do not add List structural recipes merely because List nodes already exist.

After M2 structural evaluation, decide whether one specialized operation such as `ListMap` would provide useful additional evidence. Any accepted List extension must preserve stable callback `FunctionId` references, pure-callback constraints, and conservative type/effect rejection.

It is not part of the current M2 completion claim.

## Work sequence

1. **Complete:** define and accept the first bounded structural contract.
2. **Complete:** implement and locally verify the Float64 complete-body replacement slice.
3. **Complete:** add and verify the restricted `m2-structural-v1` runner condition.
4. **Complete:** freeze `m2-agent-corpus-v1` before model runs at `efce0cac15565941c11c8af8d1a52e7ecafaa5c6`.
5. **Next:** run and review a controlled evaluation tranche under issue #51.
6. Decide from evidence whether specialized List structure belongs in M2 or a later milestone.
7. Update current references and close only the evidenced M2 claim.

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
| 8 | Versioned structural agent runner and frozen M2 corpus exist | **Complete** |
| 9 | Controlled structural agent evaluation record exists | Not started |
| 10 | Current docs accurately state the implemented versus evaluated scope | **Complete** |

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
