# Milestone 1 — Agent-safe semantic editing and language growth

## Status

Accepted planning target for the work after the verified Milestone 0 vertical slice.

Milestone 1 is not primarily a feature-count milestone. Its main purpose is to turn the verified semantic core into an editing environment that an AI agent can use through bounded semantic queries and reviewed semantic transactions, then grow the language only through that same path.

Milestone 0 proved that the architecture can complete one semantic lifecycle. Milestone 1 must prove that the architecture remains useful when the agent, not a hand-written fixture, is the ordinary editing client.

## Primary goal

An AI agent can inspect a bounded semantic slice, request a supported meaning-level change, simulate it, review the exact proposed result, and commit or reject it without direct mutable access to the authoritative snapshot and without manufacturing new stable IDs.

The same workflow must remain usable as the semantic subset grows to include basic numeric and ordered-collection processing.

## Milestone structure

### M1.1 — Editing infrastructure

The infrastructure slice establishes the safe agent boundary before language growth.

Required outcomes:

1. production stable-ID allocation uses UUIDv7 payloads and domain-typed external text forms;
2. new semantic identities needed by ordinary agent operations are allocated by the system, not invented by the agent;
3. exact snapshot fingerprints and revision-neutral semantic fingerprints have distinct contracts;
4. simulation produces a preview receipt bound to the reviewed base and proposed result;
5. reviewed commit re-simulates and rejects stale or mismatched previews;
6. the ordinary agent API is a facade over bounded queries and semantic operations rather than raw snapshot mutation;
7. Change Review distinguishes requested operation intent from resulting semantic diff;
8. implementation assemblies are separated along the accepted semantic / execution / backend / viewer dependency directions.

The UUIDv7 allocator, fingerprint / preview-receipt boundaries, and agent workflow facade are now locally verified on the rebuild line. The remaining M1.1 infrastructure gate is primarily the physical project/assembly split that turns the dependency direction from convention into a build-enforced boundary.

### M1.2 — Language growth

After the agent workflow is verified, extend the semantic subset in deliberately small slices:

1. `Int64` with explicit width and explicit arithmetic semantics;
2. immutable ordered `List<T>` construction and structural operations;
3. pure specialized `ListMap`, `ListFilter`, `ListAny`, and `ListAll` operations using stable `FunctionId` references;
4. a larger representative multi-user validation program that reuses the Milestone 0 `Result`, union, product, branch, effect, capability, and transaction semantics;
5. `Float64` with explicit IEEE 754 binary64 semantics and bit-authoritative literals;
6. explicit numeric conversion experiments only after their precision, rounding, and failure contracts are specified.

See [Milestone 1 language-growth design](m1-language-growth-design-20260826.md).

### M1.3 — Evaluate the AI-first claim

Milestone 1 must end with measurements rather than only architecture assertions.

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

A conventional source-editing baseline is desirable where a fair comparison can be constructed. Milestone 1 does not require a predetermined percentage improvement; it requires enough evidence to identify which Nayamnyam design choices improve or hurt agent reliability.

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
       - affected focus slices
    -> reviewed commit or reject
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

Milestone 1 should physically separate the currently co-located components approximately as follows:

```text
Nayamnyam.Semantics
    Stable IDs and allocation
    semantic graph model
    validation and readiness
    semantic facts and query APIs
    operations and transactions
    obligations
    fingerprints and reviewed proposals
    agent workflow facade

Nayamnyam.Execution
    Typed Semantic IR
    backend-neutral Execution IR

Nayamnyam.Backend.CSharp
    backend profile
    C# validation and emission

Nayamnyam.Viewer
    Focus / Change Review viewer projection
    ViewerModel
    static HTML rendering
    viewer artifact construction

Nayamnyam.Tools.*
    CLI and filesystem/export integration
```

Required dependency direction:

```text
Nayamnyam.Semantics
      ^        ^
      |        |
Execution    Viewer
      ^        ^
      |        |
Backend.CSharp  Tools
```

More precisely:

- `Semantics` depends on no backend, viewer, or tool assembly;
- `Execution` may depend on `Semantics` but not on a target backend;
- `Backend.CSharp` may depend on `Execution` and the minimum shared semantic identity types required by the execution contract;
- `Viewer` may depend on semantic query / review contracts but not on execution or backend implementation;
- tools compose public APIs and own filesystem / process interaction;
- generated C# never becomes an upstream dependency.

Exact project names may change. The dependency direction is the decision.

## Change Review contract

A review must preserve two different kinds of truth:

### Requested intent

What the editing client asked the semantic engine to do.

Examples:

- rename this stable function;
- add a parameter of this type;
- leave this affected call unresolved;
- resolve this obligation with this value.

### Resulting semantic change

What the semantic engine actually proposes after propagation and validation.

Examples:

- parameter added;
- argument attached;
- obligation created;
- obligation resolved;
- readiness changed;
- affected function slice changed.

Requested intent must not be reconstructed from the resulting diff, and the resulting diff must not be reconstructed from requested intent. They answer different review questions and neither is a substitute for the other.

## Fingerprint contracts

Milestone 1 keeps two different fingerprints:

### Snapshot fingerprint

Binds an exact canonical snapshot revision. It includes revision identity and presentation / obligation state carried by that snapshot. It is appropriate for reviewed-commit binding and exact artifact checks.

### Semantic fingerprint

Identifies revision-neutral semantic state while excluding revision, presentation, and audit/provenance-only noise. Stable semantic identities and actual semantic structure remain significant.

It is not alpha-equivalence, optimizer equivalence, or behavioral equivalence. Those would be separate future concepts if needed.

## Exit criteria

Milestone 1 is complete when all of the following are demonstrated by tests, reproducible artifacts, or recorded experiments:

1. **System-issued identity** — ordinary agent operations that create semantic entities do not require the agent to invent stable IDs.
2. **Bounded inspection** — the agent can perform the representative edit scenarios using semantic query APIs rather than raw whole-snapshot traversal as the normal path.
3. **Opaque proposal boundary** — the agent can review a proposal but cannot mutate its internal low-level transaction request or preview receipt through the supported facade.
4. **Reviewed publication** — a commit is bound to an exact reviewed base and proposed result; stale or mismatched proposals are rejected.
5. **Intent and result review** — Change Review exposes requested intent and resulting semantic change separately.
6. **Explicit incompleteness** — unresolved decisions remain typed obligations; incomplete publication requires explicit policy and cannot lower accidentally.
7. **Physical dependency direction** — semantic, execution, backend, viewer, and tool components no longer rely only on convention to preserve their dependency direction.
8. **`Int64` vertical slice** — literals and the accepted minimal integer operations work through validation, persistence, both IR layers, viewer/query projections, C# generation, compilation, and execution.
9. **`List<T>` vertical slice** — ordered list construction and the accepted pure specialized collection operations work end to end without introducing implicit effect ordering or lambda semantics.
10. **Expanded representative program** — a multi-user validation program exercises the new collection/numeric subset together with existing `Result`, union, effect, and capability semantics.
11. **Agent edit scenario** — at least one non-trivial cross-function change to the expanded representative program is completed through query -> simulate -> review -> commit -> execute with no raw snapshot edit.
12. **`Float64` vertical slice** — binary64 literals and the accepted initial IEEE operations have deterministic persistence/fingerprint/backend behavior, including explicit treatment of signed zero, infinity, and NaN policy.
13. **Reproducibility** — canonical snapshot artifacts, fingerprints, execution IR, and generated backend source are reproducible for the representative scenarios.
14. **Evaluation record** — the project records agent-editing results and identifies concrete design changes to retain, revise, or investigate in Milestone 2.

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
