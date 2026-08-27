# Milestone 1 — Agent-safe semantic editing and language growth

## Status

Milestone 1 has completed its implementation-oriented infrastructure and language-growth gates. The remaining milestone gate is **M1.3 — the recorded AI-agent evaluation**.

Current status:

| Slice | Status | Evidence |
| --- | --- | --- |
| M1.1 — Editing infrastructure | Complete | UUIDv7 IDs, fingerprints, preview receipts, opaque agent facade, reviewed publication, physical assembly split, trusted post-commit execution projection |
| M1.2 — Language growth | Complete for the accepted scope | Int64, basic List, higher-order List, expanded representative program, cross-function agent edit, Float64 |
| M1.3 — Evaluate the AI-first claim | Pending | Versioned agent task runs and evaluation record still required |

The latest accepted code verification used .NET SDK `10.0.111`, built with **0 warnings / 0 errors**, and passed the full suite with **158 passed / 0 failed / 0 skipped**. The final M1-J `Float64Tests` gate passed **8/8**.

Milestone 1 is therefore **not yet closed as a whole**: the implementation baseline is ready, but exit criterion 14 remains open until the evaluation record is produced.

## Primary goal

An AI agent can inspect a bounded semantic slice, request a supported meaning-level change, simulate it, review the exact proposed result, and commit or reject it without direct mutable access to the authoritative snapshot and without manufacturing new stable IDs.

The same workflow remains usable as the semantic subset grows to include explicit integer, ordered-collection, and binary64 numeric processing.

## M1.1 — Editing infrastructure

The infrastructure slice is complete.

Verified outcomes:

1. production stable-ID allocation uses UUIDv7 payloads and domain-typed external text forms;
2. new semantic identities needed by ordinary agent operations are allocated by the system, not invented by the agent;
3. exact snapshot fingerprints and revision-neutral semantic fingerprints have distinct contracts;
4. simulation produces a preview receipt bound to the reviewed base and proposed result;
5. reviewed commit re-simulates and rejects stale or mismatched previews;
6. the ordinary agent API is an opaque facade over bounded queries and semantic operations rather than raw snapshot mutation;
7. Change Review distinguishes requested operation intent from resulting semantic diff;
8. implementation assemblies are physically separated along the accepted semantic / execution / backend / viewer dependency directions;
9. trusted downstream execution can project from the exact committed `AgentWorkspace` without adding a public raw-snapshot getter to the ordinary agent API.

## M1.2 — Language growth

The accepted M1 language-growth scope is complete.

Verified vertical slices:

1. **`Int64`** — explicit 64-bit integer domain with wrapping arithmetic and dedicated comparisons through validation, persistence, both IR layers, viewer/query projection, deterministic C# generation, compilation, and execution.
2. **Immutable ordered `List<T>`** — construction, empty, length, append, and concat with list order preserved as semantic state.
3. **Pure higher-order List operations** — `ListMap`, `ListFilter`, `ListAny`, and `ListAll` reference callbacks by stable `FunctionId` and reject unsupported callback contracts conservatively.
4. **Expanded representative program** — multi-user validation reuses existing `Result`, union, product, branching, effects, capabilities, Int64, and List semantics.
5. **Non-trivial agent edit** — a cross-function signature change is completed through bounded query -> simulate -> Change Review -> reviewed commit while preserving unrelated stable callback relations.
6. **Post-commit execution handoff** — the exact committed agent workspace projects into Typed Semantic IR and continues through Execution IR, C# emission, compile, and execution.
7. **`Float64`** — explicit IEEE 754 binary64 semantics with bit-authoritative literals, signed-zero preservation, infinity behavior, canonical NaN literal policy, arithmetic, comparison, and classification.

Explicit numeric conversion experiments remain deferred until their precision, rounding, and failure contracts are specified. They are not required by the current M1 exit criteria.

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

A conventional source-editing baseline is desirable where a fair comparison can be constructed. Milestone 1 does not require a predetermined percentage improvement; it requires enough evidence to identify which NyamNyam design choices improve or hurt agent reliability.

See [M1 agent evaluation plan](m1-agent-evaluation-plan-20260826.md).

## Accepted agent workflow

The ordinary editing workflow is:

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

The agent-facing surface must not make these ordinary editing operations available:

```text
replace_snapshot(...)
edit_raw_snapshot(...)
edit_raw_nyason(...)
commit_unreviewed_transaction(...)
choose_arbitrary_new_stable_id(...)
```

Low-level snapshot and codec APIs may remain available to migrations, tests, recovery tooling, and internal infrastructure. Their existence does not make them part of the supported agent editing contract.

## Accepted implementation dependency boundaries

The implementation is now physically separated approximately as follows:

```text
NyamNyam.Semantics
    Stable IDs and allocation
    semantic graph model
    validation and readiness
    semantic facts and query APIs
    operations and transactions
    obligations
    fingerprints and reviewed proposals
    agent workflow facade

NyamNyam.Execution
    Typed Semantic IR
    backend-neutral Execution IR
    trusted AgentWorkspace execution projection

NyamNyam.Backend.CSharp
    backend profile
    C# validation and emission

NyamNyam.Viewer
    Focus / Change Review viewer projection
    ViewerModel
    static HTML rendering
    viewer artifact construction

NyamNyam.Tools.*
    CLI and filesystem/export integration
```

Required dependency direction remains:

```text
NyamNyam.Semantics
      ^        ^
      |        |
Execution    Viewer
      ^        ^
      |        |
Backend.CSharp  Tools
```

Generated C# never becomes an upstream dependency.

## Change Review contract

A review preserves two different kinds of truth.

### Requested intent

What the editing client asked the semantic engine to do, such as renaming a stable function, adding a typed parameter, leaving a call unresolved, or resolving an obligation.

### Resulting semantic change

What the semantic engine actually proposes after propagation and validation, such as a parameter addition, argument attachment, obligation creation/resolution, readiness change, or affected function slice.

Requested intent is not reconstructed from the resulting diff, and the resulting diff is not reconstructed from requested intent.

## Fingerprint contracts

Milestone 1 keeps two distinct fingerprints.

### Snapshot fingerprint

Binds an exact canonical snapshot revision. It includes revision identity and presentation / obligation state carried by that snapshot. It is appropriate for reviewed-commit binding and exact artifact checks.

### Semantic fingerprint

Identifies revision-neutral semantic state while excluding revision, presentation, and audit/provenance-only noise. Stable semantic identities and actual semantic structure remain significant.

It is not alpha-equivalence, optimizer equivalence, or behavioral equivalence.

## Float64 contract reached in M1-J

`Float64` means explicit IEEE 754 binary64 numeric semantics.

The initial slice contains:

```text
Float64Literal(bits)
Float64Add(left, right)
Float64Subtract(left, right)
Float64Multiply(left, right)
Float64Divide(left, right)
Float64Equal(left, right)
Float64LessThan(left, right)
Float64IsNaN(value)
Float64IsFinite(value)
Float64IsInfinite(value)
```

Finite values, infinities, subnormals, and signed zero preserve exact binary64 literal bits. Semantic NaN literals canonicalize to quiet NaN `7ff8000000000000`; exact NaN payload identity remains a future bit-domain concern rather than ordinary Float64 numeric meaning.

See [Float64 implementation notes](m1-float64-implementation-notes-20260828.md).

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
| 13 | Reproducibility | Complete for implemented representative scenarios |
| 14 | Evaluation record | **Pending — remaining M1 gate** |

## Verification baseline

Latest accepted code verification:

```text
feature head: 79fccfba86c1c00add0a9c50248c55336e7c563b
.NET SDK:     10.0.111
build:        0 warnings / 0 errors
full suite:   158 passed / 0 failed / 0 skipped
M1-J Float64: 8 passed / 0 failed
result:       VERIFICATION PASSED
```

The verified implementation tree was squash-merged as `54c26d1dbfc615ef60b00a714552004cf191fefe`. Subsequent accepted changes before this status update were documentation-only.

## Non-goals

Milestone 1 does not require:

- generic lambdas or closures;
- effectful collection callbacks;
- implicit numeric conversions or promotion;
- generic numeric type classes / operator overloading;
- mutable or lazy collections;
- a Rust backend;
- ownership / borrow semantics;
- distributed or multi-agent semantic merge;
- a final choice between JSON, Nyason, database, or binary canonical persistence;
- a general visual programming editor;
- cryptographic or organizational approval policy;
- proof of behavioral equivalence between arbitrary snapshots.

## Milestone boundary rule

Do not expand the language merely because a backend can express a feature. A feature counts as part of Milestone 1 only when its semantic contract is explicit and it participates in the same authoritative graph, validation, query, review, lowering, backend, and test path as the existing subset.
