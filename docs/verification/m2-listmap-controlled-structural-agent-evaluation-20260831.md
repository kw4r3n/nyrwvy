# M2 controlled ListMap structural agent evaluation — 2026-08-31

**Status:** Complete controlled evidence tranche for the frozen specialized ListMap structural-editing claim.

This record reports the 30 valid autonomous attempts collected under frozen `m2-listmap-agent-corpus-v1`, frozen `m2-listmap-condition-v1`, and frozen operator orchestration. It preserves the pre-observation corpus/oracles and reports the operator-summary defect separately; it does not change or rescore the frozen machine oracle after observing results.

## Fixed condition

- corpus: `m2-listmap-agent-corpus-v1`
- corpus branch: `eval/m2-listmap-agent-corpus-v1-20260831`
- corpus SHA: `a2ffb5615d37d44bbd910c4f47c36136c31cdce4`
- runner protocol: `m2-listmap-structural-v1`
- condition branch: `eval/m2-listmap-condition-v1-20260831`
- condition/repository/tool SHA: `23bf09d87c03b9c15dd4640b662a05b24aa5e330`
- operator branch: `eval/m2-listmap-orchestrator-v1-20260831`
- operator SHA: `f7d6d07cd47367dde2272386b6e430fddef6d03a`
- model: `openai/gpt-5.6-terra`
- reasoning variant: `high`
- provider/runtime: `opencode`
- OpenCode: `1.18.18`
- prompt revision: `m2-listmap-corpus-v1-seed-v1`
- attempts: 3 fresh sessions per M2-L1..M2-L10, 30 valid attempts total
- human intervention inside attempts: none

The scored prompt wrapper was frozen before the first model observation. It consists only of the frozen corpus task prompt plus the frozen target/source/mapper stable semantic IDs. It does not append expected diagnostics, oracle values, fixture names, unrelated IDs, or prior-run results.

## Raw evidence packages

The complete private run artifacts were collected outside the repository. This record pins their exact packages and hashes without committing generated build trees.

| Package | Attempts | Bytes | SHA-256 |
| --- | ---: | ---: | --- |
| `m2-listmap-condition-v1-stage-a-20260831-085213.tar.gz` | a1 for L1-L10 | 322487 | `97a8141d9e5cc120341fa3e671b22f8e78693fb563034dea87a142ded1b37938` |
| `m2-listmap-condition-v1-stage-b-20260831-091117.tar.gz` | a2/a3 for L1-L10 | 642439 | `edeb404dabe8bda87252774a52636884e133f9336b3832305ebc8eec5b25f328` |

Every valid attempt has one OpenCode session, one harness run directory, exact pinned run metadata, protocol transcript, terminal run state, metrics, and committed-state verification artifacts when a publication occurs.

## Headline result

**30/30 attempts passed the frozen machine oracle (100%).**

All 30 attempts were independently reclassified from the durable raw artifacts rather than trusting the operator TSV summary. The operator's frozen summary code has a known post-observation reporting defect described below, but it did not affect model-visible inputs, runner behavior, stored artifacts, or machine-oracle values.

Additional headline evidence:

- condition-valid terminal attempts: **30/30**;
- exact frozen metadata pinning: **30/30**;
- zero unrelated semantic churn: **30/30**;
- protocol-input errors: **0/30**;
- explicit agent proposal rejects: **0/30**;
- expected rejection simulations: **24/24** produced the exact frozen diagnostic;
- successful publications: **6/6** passed durable exact materialization and Typed Semantic IR -> Execution IR -> C# verification;
- successful execution output: **6/6 = `true`**;
- M2-L10 stale reviewed-proposal recovery: **3/3**;
- M2-L10 harness-owned unrelated rename preserved: **3/3**.

Unlike the completed Float64 tranche, this shallow ListMap tranche exposed no recursive-request construction failure, transport-format error, agent self-reject retry, or semantic-oracle failure.

## Per-task summary

| Task | Frozen oracle | Result | Notes |
| --- | --- | --- | --- |
| M2-L1 | reviewed `ListMap(values, positive)` commit; exact two-node materialization; execution `true` | **3/3** | Each publication added exactly fresh `ParameterReference(values)` then `ListMap(..., positive)` and executed `true`. |
| M2-L2 | foreign List source rejected | **3/3** | `StructuralListSourceParameterOutOfScope`; zero commits/churn. |
| M2-L3 | owned non-List source rejected | **3/3** | `StructuralListSourceTypeMismatch`; zero commits/churn. |
| M2-L4 | missing mapper rejected | **3/3** | `StructuralListMapperNotFound`; zero commits/churn. |
| M2-L5 | mapper input mismatch rejected | **3/3** | `StructuralListMapperInputTypeMismatch`; zero commits/churn. |
| M2-L6 | mapper result mismatch rejected | **3/3** | `StructuralListMapperResultTypeMismatch`; zero commits/churn. |
| M2-L7 | effectful mapper rejected | **3/3** | `StructuralListMapperEffectsNotAllowed`; zero commits/churn. |
| M2-L8 | open-obligation-protected body rejected | **3/3** | `StructuralBodyReferencedByObligation`; zero commits/churn. |
| M2-L9 | resolved historical obligation-protected body rejected | **3/3** | `StructuralBodyReferencedByObligation`; zero commits/churn. |
| M2-L10 | reviewed proposal becomes stale after harness publication, then fresh recovery commit | **3/3** | `StaleBaseSnapshot` first, fresh ListMap publication second, harness rename preserved, execution `true`. |

## Per-attempt protocol metrics

| Task | Attempt | Oracle | Tool calls | Focus | Simulations | Commit attempts | Commits | Rejected simulations | Rejected commits | Agent rejects | Protocol errors | Pending proposals |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| L1 | a1 | pass | 5 | 1 | 1 | 1 | 1 | 0 | 0 | 0 | 0 | 0 |
| L1 | a2 | pass | 5 | 1 | 1 | 1 | 1 | 0 | 0 | 0 | 0 | 0 |
| L1 | a3 | pass | 5 | 1 | 1 | 1 | 1 | 0 | 0 | 0 | 0 | 0 |
| L2 | a1 | pass | 4 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L2 | a2 | pass | 4 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L2 | a3 | pass | 4 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L3 | a1 | pass | 5 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L3 | a2 | pass | 5 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L3 | a3 | pass | 5 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L4 | a1 | pass | 4 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L4 | a2 | pass | 4 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L4 | a3 | pass | 5 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L5 | a1 | pass | 4 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L5 | a2 | pass | 4 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L5 | a3 | pass | 4 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L6 | a1 | pass | 5 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L6 | a2 | pass | 5 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L6 | a3 | pass | 4 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L7 | a1 | pass | 4 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L7 | a2 | pass | 4 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L7 | a3 | pass | 4 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L8 | a1 | pass | 4 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L8 | a2 | pass | 4 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L8 | a3 | pass | 4 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L9 | a1 | pass | 4 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L9 | a2 | pass | 5 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L9 | a3 | pass | 5 | 1 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 0 |
| L10 | a1 | pass | 9 | 2 | 2 | 2 | 1 | 0 | 1 | 0 | 0 | 1 |
| L10 | a2 | pass | 11 | 3 | 2 | 2 | 1 | 0 | 1 | 0 | 0 | 1 |
| L10 | a3 | pass | 8 | 2 | 2 | 2 | 1 | 0 | 1 | 0 | 0 | 1 |

Aggregate protocol metrics across all 30 attempts:

- tool calls: **147** (mean **4.90**, median **4**)
- Focus queries: **34**
- simulations: **33**
- commit attempts: **9**
- successful commits: **6**
- expected rejected simulations: **24**
- expected stale commit rejections: **3**
- explicit agent proposal rejects: **0**
- protocol-input errors: **0**

The 33 simulations are exactly the minimum semantic sample implied by the frozen tasks: 3 L1 success simulations + 24 expected-rejection simulations + 6 L10 stale/fresh simulations. There were no extra simulation retries.

## Exact committed-state evidence

There were six successful publications: L1 and L10 in all three repetitions.

**6/6 publication verifications passed durably.** Each report records:

- exact reviewed publication receipt and fingerprint consistency;
- exact owner function;
- exactly two fresh added nodes;
- first role `ParameterReference` bound to `parameter-m2-listmap-eval-target-values`;
- second role `ListMap` bound to `function-m2-listmap-eval-positive`;
- ListMap dependency on the fresh source-reference node;
- result node equal to the fresh ListMap node;
- removed body identity not reused;
- Typed Semantic IR projection from the published snapshot;
- Execution IR lowering from the same published snapshot;
- C# emission and Release build exit code 0;
- generated executable exit code 0;
- exact output `true`.

Thus the six observed publications support both the bounded semantic materialization claim and the exact backend behavior claim.

## Rejection-boundary evidence

L2-L9 deliberately do not publish. Across 24 attempts:

- every attempt performed exactly one ListMap simulation;
- every attempt received exactly the frozen structured diagnostic for its task;
- no attempt substituted a valid source/mapper after rejection;
- no attempt committed anything;
- current snapshot and semantic fingerprints remained equal to the starting fingerprints;
- zero unrelated semantic churn remained true.

This is particularly useful evidence for the new ListMap-specific type/effect authority boundary: source ownership, List source shape, mapper existence, mapper input/result compatibility, mapper purity, and durable obligation protection all fail closed without graph mutation.

## Stale review/publication evidence

L10 was **3/3**.

Each run:

1. inspected the current bounded state/focus;
2. created the valid reviewed ListMap proposal;
3. received the harness-owned unrelated rename publication;
4. attempted the old reviewed proposal and received `StaleBaseSnapshot`;
5. inspected the advanced workspace;
6. created a fresh ListMap proposal against the advanced state;
7. committed exactly that fresh proposal;
8. preserved `unrelated_marker_harness_advanced`;
9. passed exact materialization and C# execution verification.

As in the earlier Float64 E10 tranche, the old stale proposal handle remains pending at terminal state (`pendingProposalCount=1`) in all three L10 attempts. This is a known proposal-lifecycle/cleanup issue tracked by #74; it does not affect the published semantic state or the frozen L10 oracle.

## Operator-summary defect and raw reclassification

The frozen operator revision `f7d6d07c...` incorrectly treated every durable artifact path as requiring non-zero length. In particular it required `harness-events.jsonl` to satisfy `test -s`.

For L2-L9, an empty `harness-events.jsonl` is expected: these tasks end at the protocol simulation rejection and perform no harness-owned stale perturbation or committed-state verification. Therefore their harness event file exists but is 0 bytes.

The frozen operator TSV consequently displayed only:

- Stage A `terminal+pinned=2`, `machine oracle passes=2`;
- Stage B `terminal+pinned=4`, `machine oracle passes=4`.

Those numbers are **operator reporting errors**, not attempt failures. Direct inspection of the authoritative durable files (`run-state.json`, `run-metadata.json`, and `metrics.json`) establishes 10/10 valid in Stage A and 20/20 valid in Stage B.

The orchestrator was intentionally not modified between stages after this issue was discovered, preserving the same frozen operator revision for all 30 observed sessions. The defect is tracked separately by #96 and should be fixed only after this tranche.

## Comparison with the Float64 structural tranche

The earlier Float64 controlled tranche scored 29/30 on its frozen machine oracle, with the dominant weakness being exact construction of deep recursive explicit JSON recipes. It also observed nine explicit agent proposal rejects and two protocol-input errors across 30 attempts.

The specialized ListMap tranche is intentionally much shallower and role-specific. Under that narrower authority surface it achieved:

- **30/30** frozen oracle pass;
- **0** explicit proposal rejects;
- **0** protocol errors;
- no extra simulation retries;
- exact type/effect/ownership/obligation diagnostics in all 24 expected rejection attempts.

This does not imply generic higher-order graph editing is solved. It supports the narrower conclusion anticipated by #52: exposing a shallow semantic-role operation with existing stable `FunctionId`/`ParameterId` references is materially easier for the agent than asking it to construct large recursive graph recipes manually.

## What this tranche supports

Together with the implementation/protocol/corpus/condition records, this tranche supports a Milestone-2 claim that the current **specialized bounded ListMap complete-body workflow** is usable by an isolated agent with:

- stable semantic authority;
- system-owned new graph identities;
- requested semantic IDs separated from materialized graph IDs;
- exact reviewed publication receipts;
- conservative source/type/mapper/effect/obligation rejection;
- stale-review failure and fresh recovery;
- zero unrelated semantic churn;
- exact Typed Semantic IR -> Execution IR -> C# committed behavior.

It does **not** support arbitrary List expression construction, recursive List recipes, generic calls, generic lambdas/closures, effectful callback authority, unrestricted graph editing, or arbitrary higher-order structural editing.

## Conclusion

The frozen ListMap tranche is complete at **30/30 valid machine-oracle passes**. No semantic failure, transport failure, protocol-input error, or agent retry pathology was observed in the 30 scored sessions. The only tranche-wide defect is the external operator summary's treatment of intentionally empty harness-event files; the raw durable evidence is complete and independently classifiable.

Issue #77 can therefore be closed for the specialized ListMap controlled-evaluation claim. The operator reporting defect remains separately actionable under #96, and stale proposal-handle cleanup remains separately tracked under #74.