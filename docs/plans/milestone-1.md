# Milestone 1 — Agent-safe semantic editing and language growth

**Status:** Current plan — M1.3 pending

Milestone 1 has completed its implementation-oriented infrastructure and accepted language-growth gates. The remaining milestone gate is **M1.3 — the recorded AI-agent evaluation**.

| Slice | Status | Outcome |
| --- | --- | --- |
| M1.1 — Editing infrastructure | Complete | Production identity, fingerprints, review receipts, bounded agent workflow, reviewed publication, physical boundaries, trusted post-commit execution projection |
| M1.2 — Language growth | Complete for the accepted scope | `Int64`, immutable ordered `List<T>`, pure higher-order List operations, expanded representative program, cross-function agent edit, `Float64` |
| M1.3 — Evaluate the AI-first claim | Pending | Versioned agent task runs and an evaluation record are still required |

Milestone 1 is therefore not closed as a whole. Exact implementation verification belongs in [verification records](../verification/) rather than this plan.

## Primary goal

An AI agent can inspect a bounded semantic slice, request a supported meaning-level change, simulate it, review the exact proposed result, and commit or reject it without direct mutable access to the authoritative snapshot and without manufacturing new stable IDs.

The same workflow remains usable as the semantic subset grows.

## M1.1 — Editing infrastructure

The accepted infrastructure outcomes are:

1. production stable IDs use typed domains and system-side allocation;
2. exact snapshot fingerprints and revision-neutral semantic fingerprints have distinct contracts;
3. simulation produces an opaque reviewed proposal/receipt bound to the base and proposed result;
4. reviewed publication rejects stale or mismatched proposals;
5. the ordinary agent API exposes bounded queries and semantic operations rather than raw snapshot mutation;
6. Change Review preserves requested intent separately from resulting semantic diff;
7. implementation assemblies enforce the semantic / execution / backend / viewer dependency direction;
8. trusted downstream execution can project from the exact committed agent workspace without making raw snapshots part of the ordinary agent API.

## M1.2 — Language growth

The accepted M1 language-growth scope is complete.

Implemented vertical slices include:

1. **`Int64`** — explicit 64-bit integer semantics with wrapping arithmetic and dedicated comparisons.
2. **Immutable ordered `List<T>`** — construction, empty, length, append, concat, and semantic element order.
3. **Pure higher-order List operations** — `ListMap`, `ListFilter`, `ListAny`, and `ListAll` reference callbacks by stable `FunctionId` and conservatively reject unsupported callback contracts.
4. **Expanded representative program** — a larger multi-function scenario combines existing result/union/product/branch/effect/capability semantics with numeric and List constructs.
5. **Non-trivial agent edit** — a cross-function signature change runs through bounded query -> simulate -> Change Review -> reviewed commit while preserving unrelated stable relations.
6. **Post-commit execution handoff** — committed agent state continues through Typed Semantic IR, Execution IR, C# emission, compilation, and execution.
7. **`Float64`** — explicit IEEE 754 binary64 semantics with bit-authoritative literals, signed-zero preservation, infinity behavior, canonical NaN literal policy, arithmetic, comparison, and classification.

The maintained surface is summarized in the [current language subset](../reference/language-subset.md). Detailed Float64 implementation evidence is in the [Float64 verification record](../verification/m1-float64-implementation-notes-20260828.md).

Explicit numeric conversion experiments remain deferred until precision, rounding, and failure contracts are specified. They are not required by the current M1 exit criteria.

## M1.3 — Evaluate the AI-first claim

This is the remaining Milestone 1 gate.

Run a small, versioned set of semantic-editing tasks through the agent-facing workflow and record at least:

- invalid operation rate;
- failed simulation rate;
- validation or lowering failures after accepted edits;
- retry count;
- unrelated semantic churn;
- number of semantic queries / tool calls;
- token use where measurable;
- whether focused slices were sufficient or whole-program retrieval was required;
- whether specialized semantic node kinds were selected reliably.

A conventional source-editing baseline is desirable where a fair comparison can be constructed. Milestone 1 does not require a predetermined percentage improvement; it requires enough evidence to identify which design choices improve or hurt agent reliability.

See the [M1 agent evaluation plan](m1-agent-evaluation-plan-20260826.md).

## Accepted agent workflow

```text
bounded semantic query
    -> operation intent
    -> system allocates any new identities
    -> simulate
    -> opaque proposal + preview receipt
    -> Change Review
       - requested intent
       - resulting semantic diff
       - readiness / obligations
       - affected Focus slices
    -> reviewed commit or reject
    -> trusted execution projection when execution is requested
```

The ordinary agent-facing surface does not expose raw snapshot replacement, raw Nyason mutation, unreviewed commit, or arbitrary production-ID fabrication.

## Accepted dependency boundary

```text
NyamNyam.Semantics
      ^        ^
      |        |
Execution    Viewer
      ^        ^
      |        |
Backend.CSharp  Tools
```

Generated C# never becomes an upstream dependency or program authority.

## Review contracts

Change Review preserves two different facts:

- **requested intent** — what the editing client asked the semantic engine to do;
- **resulting semantic change** — what the engine actually proposes after propagation and validation.

Neither is reconstructed from the other.

The reviewed proposal also binds exact base/proposed state. Stale or changed proposals require a fresh proposal and review.

## Fingerprint contracts

Milestone 1 maintains two distinct purposes:

- **Snapshot fingerprint** — binds an exact canonical snapshot revision and is suitable for exact reviewed-publication checks.
- **Semantic fingerprint** — identifies revision-neutral semantic state while excluding revision/presentation/audit-only noise; it is not a claim of arbitrary behavioral equivalence.

## Exit criteria

| # | Criterion | Status |
| ---: | --- | --- |
| 1 | System-issued identity | Complete |
| 2 | Bounded inspection | Complete |
| 3 | Opaque proposal boundary | Complete |
| 4 | Reviewed publication | Complete |
| 5 | Intent and result review | Complete |
| 6 | Explicit incompleteness | Complete |
| 7 | Physical dependency direction | Complete |
| 8 | `Int64` vertical slice | Complete |
| 9 | `List<T>` vertical slice | Complete |
| 10 | Expanded representative program | Complete |
| 11 | Agent edit scenario through query -> simulate -> review -> commit -> execute | Complete |
| 12 | `Float64` vertical slice | Complete |
| 13 | Reproducibility for implemented representative scenarios | Complete |
| 14 | Evaluation record | **Pending — remaining M1 gate** |

## Non-goals

Milestone 1 does not require:

- generic lambdas or closures;
- effectful collection callbacks;
- implicit numeric conversions or promotion;
- generic numeric type classes/operator overloading;
- mutable or lazy collections;
- a Rust backend or ownership/borrow semantics;
- distributed or multi-agent semantic merge;
- a final persistence-medium choice;
- a general visual programming editor;
- cryptographic or organization-specific approval policy;
- proof of behavioral equivalence between arbitrary snapshots.

## Milestone boundary rule

Do not expand the language merely because a backend can express a feature. A feature counts as accepted only when its semantic contract is explicit and it participates in the authoritative graph, validation, query/review, lowering, backend, and test path required for that feature.

The original chronology-heavy Milestone 1 record is preserved in the [pre-reorganization archive](../../docs-archive/2026-08-pre-reorg/milestone-1.md).
