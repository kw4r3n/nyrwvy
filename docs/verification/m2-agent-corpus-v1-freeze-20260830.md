# M2 structural agent corpus v1 freeze verification — 2026-08-30

**Status:** Observed result / frozen evaluation input

This record freezes the first controlled Milestone 2 structural-edit evaluation corpus after deterministic local verification and before any recorded model result.

It is evaluation-input evidence, not a model-reliability result.

## Frozen condition

- Repository: `kw4r3n/nyrwvy-lang`
- Corpus issue: #50
- Fixture/oracle verification subtask: #58
- Corpus merge PR: #62
- Corpus identifier: `m2-agent-corpus-v1`
- Agent-visible protocol: `m2-structural-v1`
- Frozen branch: `eval/m2-agent-corpus-v1-20260830`
- **Exact frozen SHA:** `efce0cac15565941c11c8af8d1a52e7ecafaa5c6`
- Main merge commit preserving that frozen SHA as a parent: `efd68ef7f739bdb355d9efb4f7a8ea7c970933ea`
- Verification branch: `eval/issue-50-m2-agent-corpus-v1`
- .NET SDK: `10.0.111`
- Verification time: `2026-08-30T07:01:37+09:00`

The frozen branch must not be modified. Any change to task prompts, task/oracle semantics, fixture bindings, required protocol revision, or harness perturbation requires a new corpus version.

## Clean verification

Command:

```shell
bash scripts/verify-local-20260825-1427.sh --clean
```

The script removed all repository `bin/` and `obj/` directories before restore/build/test.

Result:

```text
branch: eval/issue-50-m2-agent-corpus-v1
head: efce0cac15565941c11c8af8d1a52e7ecafaa5c6
dotnet-sdk: 10.0.111

Release build:
  0 warnings
  0 errors

Full test suite:
  passed: 209
  failed: 0
  skipped: 0

VERIFICATION PASSED
```

The host-local verification log path was:

```text
/tmp/nyrwvy-verification/local-verify-20260830-070137.log
```

The path is execution context only; the durable facts are summarized in this record.

## Preserved focused verification

All existing M0/M1 focused verification phases passed at the frozen SHA:

| Phase | Result |
| --- | ---: |
| Phase I execution lowering | 6 / 6 |
| Phase I canonical Execution IR codec | 3 / 3 |
| Phase J C# backend | 5 / 5 |
| Phase K semantic query | 7 / 7 |
| Phase K Change Review query | 3 / 3 |
| Phase L read-only viewer | 6 / 6 |
| Phase M viewer artifact export | 6 / 6 |
| Phase N workspace publication | 8 / 8 |
| Phase O Milestone 0 lifecycle | 1 / 1 |
| M1-A stable ID allocator | 6 / 6 |
| M1-B fingerprints | 6 / 6 |
| M1-C preview receipts | 6 / 6 |
| M1-D agent workflow | 8 / 8 |
| M1-E assembly boundaries | 5 / 5 |
| M1-F Int64 | 7 / 7 |
| M1-G basic List | 7 / 7 |
| M1-H higher-order List | 7 / 7 |
| M1-I expanded program | 1 / 1 |
| M1-I post-commit execution | 3 / 3 |
| M1-J Float64 | 8 / 8 |

## Frozen corpus contents

The frozen revision contains:

- `evaluation/m2-agent-corpus-v1/README.md` — scope, authority boundary, freeze rules, task descriptions;
- `evaluation/m2-agent-corpus-v1/corpus.json` — machine-readable task/oracle and deterministic fixture bindings;
- `evaluation/m2-agent-corpus-v1/run-template.md` — durable per-attempt evidence template;
- `M2StructuralEvaluationCorpus.fs` — one shared deterministic fixture/oracle source used by tests and fixture export;
- fixture exporter support for `m2-ready`, `m2-open-protected`, and `m2-resolved-protected`;
- regression tests binding the machine-readable corpus to the exact fixture/oracle implementation.

The corpus has ten tasks covering the fourteen required #50 evidence classes:

1. successful `Add(Parameter, Literal)` replacement;
2. expected-base mismatch and fresh recovery;
3. expected-result mismatch and fresh recovery;
4. foreign-parameter rejection;
5. wrong-type owned-parameter rejection;
6. recipe-depth rejection;
7. recipe-node-count rejection;
8. open-obligation-protected body rejection;
9. resolved-obligation-protected body rejection;
10. stale reviewed proposal rejection and fresh recovery.

The successful task also verifies system-owned node identity/materialization order, before/after review completeness, exact committed-state lowering and generated C# behavioral execution, and zero unrelated semantic churn.

## Machine-checkable oracle evidence

The frozen regression suite establishes before model runs that:

- all three corpus snapshots pass the normal full snapshot validator;
- `corpus.json` uses the same stable fixture IDs as the shared F# fixture source;
- the corpus protocol version exactly matches `StructuralProtocol.version`;
- every intended invalid request fails with its specified structured diagnostic;
- expected-base/result recovery can proceed through a fresh valid reviewed proposal;
- stale reviewed publication fails closed and fresh recovery preserves the harness-owned intervening change;
- the representative successful edit lowers through Typed Semantic IR and Execution IR, emits/builds generated C#, and executes to the expected `3.75` result.

These checks validate the benchmark input/oracles. They do not count as agent task successes.

## Evaluation boundary

No model output was observed before the corpus was fixed on `eval/m2-agent-corpus-v1-20260830`.

Recorded M2 runs must therefore pin at least:

- corpus `m2-agent-corpus-v1` / frozen SHA `efce0cac15565941c11c8af8d1a52e7ecafaa5c6`;
- protocol `m2-structural-v1`;
- repository/tool revision used to execute the run;
- model/provider/runtime/reasoning configuration where available;
- every attempt, retry, protocol error, simulation rejection, commit attempt, and intervention class;
- semantic/task oracle evidence rather than model self-report.

The next evidence gate is issue #51: controlled recorded model runs and a reviewed M2 evaluation record.
