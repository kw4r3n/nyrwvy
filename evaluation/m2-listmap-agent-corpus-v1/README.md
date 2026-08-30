# M2 ListMap agent corpus v1

`m2-listmap-agent-corpus-v1` is the deterministic controlled-evaluation corpus for the specialized M2.3 `ListMap` structural role.

This corpus is **separate from** the completed Float64 `m2-agent-corpus-v1`. The Float64 corpus/results are historical evidence and are not modified or reinterpreted here.

## Freeze boundary

```text
corpus:   m2-listmap-agent-corpus-v1
protocol: m2-listmap-structural-v1
runner:   tools/NyamNyam.AgentEval.ListMapStructural
```

The freeze SHA is recorded only after clean local verification. Once a freeze branch is created, task prompts, fixture bindings, expected diagnostics, behavior oracles, and sampling discipline are immutable for v1.

No ListMap model observation may occur before the freeze branch/SHA is recorded.

## Pre-result sampling plan

The sampling plan is fixed before results:

```text
model: openai/gpt-5.6-terra
reasoning: high
10 tasks
3 fresh attempts per task
30 valid attempts target
```

Use the same staged validity gate as the completed Float64 evaluation:

- **Stage A:** one fresh attempt for each M2-L1..M2-L10;
- inspect only transport/condition validity, artifact durability, and whether an attempt is valid evidence;
- semantic failures do **not** stop the tranche;
- if the condition is valid, **Stage B:** two additional fresh attempts for every task regardless Stage A semantic outcomes.

Any condition-level defect may require a separately versioned condition revision without changing this frozen corpus.

## Task matrix

| Task | Purpose | Frozen oracle |
| --- | --- | --- |
| M2-L1 | valid target-owned List source + pure mapper | reviewed proposal commits; added roles `ParameterReference`, `ListMap`; exact execution `true`; zero unrelated churn |
| M2-L2 | foreign List source | `StructuralListSourceParameterOutOfScope` |
| M2-L3 | target-owned non-List source | `StructuralListSourceTypeMismatch` |
| M2-L4 | absent mapper FunctionId | `StructuralListMapperNotFound` |
| M2-L5 | mapper input mismatch | `StructuralListMapperInputTypeMismatch` |
| M2-L6 | mapper result mismatch | `StructuralListMapperResultTypeMismatch` |
| M2-L7 | effectful compatible mapper | `StructuralListMapperEffectsNotAllowed` |
| M2-L8 | open obligation protects old body | `StructuralBodyReferencedByObligation` |
| M2-L9 | resolved obligation protects old body | `StructuralBodyReferencedByObligation` |
| M2-L10 | stale reviewed proposal after harness publication | first commit `StaleBaseSnapshot`, then fresh commit preserving harness change |

Rejection tasks must not be rescored as successful merely because the agent substitutes another valid source/mapper after the expected rejection. The prompts explicitly forbid substitution where that distinction matters.

## Main fixture

`m2-listmap-ready` contains:

- `map_signs(values: List<Int64>, count: Int64) -> List<Bool>`;
- pure compatible mapper `positive : Int64 -> Bool`;
- pure wrong-input mapper `string-to-bool : String -> Bool`;
- pure wrong-result mapper `int-identity : Int64 -> Int64`;
- effectful compatible-signature mapper `effectful-positive : Int64 -> Bool`;
- foreign `List<Int64>` parameter owned by `foreign_source`;
- `evaluate_listmap()` caller that returns `true` after the valid ListMap replacement;
- an unrelated function used by the stale-publication harness;
- a deterministic absent mapper FunctionId seed.

`m2-listmap-open-protected` and `m2-listmap-resolved-protected` retain the target/source/mapper identity where practical while replacing the target body with an obligation-referenced call.

## Success oracle

M2-L1 requires more than a successful commit:

- requested `SourceParameterId` is exactly the frozen target-owned List parameter;
- requested `MapperFunctionId` is exactly the frozen pure compatible mapper;
- requested semantic IDs remain separate from materialized node identities;
- exactly two fresh production nodes are materialized in reviewed order: `ParameterReference`, `ListMap`;
- new GraphNodeIds are system-issued/fresh and disjoint from removed nodes;
- target identity/signature, caller, foreign source, unrelated function, declarations, and obligations are preserved;
- committed state lowers through Typed Semantic IR -> Execution IR -> C#;
- execution of the frozen caller produces `true`;
- no unrelated semantic churn occurs.

Codec/storage collection order is **not** an oracle. Semantic dependency, result identity, and exact fingerprints are authoritative for persistence checks.

## Stale oracle

For M2-L10 the controlled harness, not the agent, publishes one unrelated function rename after the initial target proposal is reviewed:

```text
unrelated_marker -> unrelated_marker_harness_advanced
```

The initial reviewed target proposal must then reject with `StaleBaseSnapshot`. The agent must inspect the advanced state, create a fresh target proposal, and commit it while preserving the harness rename.

The expected harness mutation is excluded from the zero-unrelated-churn comparison only because it is the explicit baseline advancement being tested.

## Condition discipline

The eventual controlled condition must pin:

- exact corpus freeze SHA;
- exact protocol/tool/repository revision;
- exact OpenCode version and real-runtime transport;
- exact model/reasoning configuration;
- exact prompt/seed revision;
- fresh session/fixture per attempt;
- durable event/run-state/verification artifacts;
- harness-owned stale perturbation only for M2-L10.

No post-hoc oracle or prompt changes are permitted after model observation. If the frozen corpus itself is later superseded, create a new corpus version instead of editing v1.

Refs: #77, #86, #82, #84
