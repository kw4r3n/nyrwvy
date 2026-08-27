# Nayamnyam

**Nayamnyam is an AI-native semantic programming system in which programs are represented as persistent typed semantic graphs and changed through validated semantic operations.**

The project reverses several conventional programming assumptions:

- source text is not the program's authoritative form;
- names and source positions are not semantic identity;
- AI agents request typed meaning-level changes instead of editing text patches;
- humans primarily understand, review, and approve programs through graph-oriented views;
- executable artifacts are generated through backend-neutral lowering, with C#/.NET as the first backend.

## Current status

`main` is the authoritative development branch.

- **Milestone 0: complete.** The graph-first architecture has passed a complete semantic lifecycle from persistence through reviewed editing and generated execution.
- **Milestone 1.1: complete.** Production UUIDv7 stable IDs, fingerprints, preview receipts, the opaque agent workflow facade, trusted post-commit execution handoff, and physical assembly boundaries are implemented and verified.
- **Milestone 1.2: complete for the accepted language-growth scope.** `Int64`, immutable ordered `List<T>`, pure higher-order List operations, the expanded multi-user validation program, the non-trivial cross-function agent edit, and explicit IEEE 754 binary64 `Float64` semantics all run through the same end-to-end pipeline.
- **Milestone 1.3: next.** The remaining Milestone 1 gate is the recorded AI-agent evaluation described in [the evaluation plan](docs/m1-agent-evaluation-plan-20260826.md).

Latest accepted code verification used .NET SDK `10.0.111` and passed a Release build with **0 warnings / 0 errors** and the full test suite with **158 passed / 0 failed / 0 skipped**. The final M1-J `Float64Tests` gate passed **8/8**.

See [Milestone 1](docs/milestone-1.md) for the current exit-criteria status and [Float64 implementation notes](docs/m1-float64-implementation-notes-20260828.md) for the latest vertical-slice verification record.

## Core idea

```mermaid
flowchart TD
    A[Human intent or AI task] --> B[Bounded semantic query]
    B --> C[Operation intent]
    C --> D[Simulate]
    D --> E[Change Review]
    E --> F[Commit or reject]
    F --> G[Persistent typed semantic graph]
    G --> H[Focus / Change Review projections]
    G --> I[Typed Semantic IR]
    I --> J[Backend-neutral Execution IR]
    J --> K[C# / .NET backend]
```

The semantic graph is the source of truth. Textual code, generated C#, diagnostic reports, serialized encodings, query payloads, viewer models, and visual artifacts are projections.

## Established direction

The following direction is accepted unless implementation evidence requires revision:

1. The persistent typed semantic graph is the authoritative program representation.
2. Program changes are expressed as semantic operations and applied transactionally.
3. Stable IDs define semantic identity; names are presentation metadata.
4. Explicit obligations represent unresolved work instead of guessed code.
5. The ordinary agent path is bounded query -> operation intent -> simulate -> review -> commit/reject.
6. Human-authored source code is not a primary requirement.
7. Nayamnyam is an independent programming system, not an extension of a host language.
8. Lowering passes through backend-neutral IR before reaching C#/.NET.
9. The semantic model is implemented in F#/.NET; generated C# remains the first execution backend.
10. When design choices conflict, AI semantic comprehension and edit reliability take priority over source-level elegance or brevity.

## Current semantic subset

The verified subset now includes:

- primitive `String`, `Bool`, `Unit`, `Int64`, and `Float64` domains;
- products, unions, `Option`, and `Result`;
- explicit branches and exhaustive matches with pattern bindings;
- internal direct calls and external capability invocation;
- explicit effects and capability requirements;
- immutable ordered `List<T>` construction, append, concat, and length;
- pure `ListMap`, `ListFilter`, `ListAny`, and `ListAll` using stable `FunctionId` callback references;
- explicit Int64 wrapping arithmetic and comparisons;
- IEEE 754 binary64 Float64 arithmetic, comparison, classification, signed-zero preservation, and canonical NaN literal policy.

No implicit numeric promotion, generic lambdas/closures, or effectful collection callbacks are part of the current accepted subset.

## Agent-safe editing boundary

The ordinary agent-facing workflow is:

```text
bounded semantic query
    -> operation intent
    -> system allocates new identities
    -> simulate
    -> opaque proposal + preview receipt
    -> Change Review
       - requested intent
       - resulting semantic diff
       - readiness / obligations
       - affected Focus slices
    -> reviewed commit or reject
    -> trusted execution projection when needed
```

Ordinary agent code is not expected to replace raw snapshots, mutate Nyason directly, manufacture stable IDs, or commit unreviewed transactions.

## Physical architecture

The implementation is split into build-enforced assemblies:

```text
Nayamnyam.Semantics
      ^        ^
      |        |
Execution    Viewer
      ^        ^
      |        |
Backend.CSharp  Tools
```

- `Nayamnyam.Semantics` owns stable IDs, the semantic graph, validation/readiness, queries, transactions, fingerprints, and the agent workflow facade.
- `Nayamnyam.Execution` owns Typed Semantic IR and backend-neutral Execution IR.
- `Nayamnyam.Backend.CSharp` consumes Execution IR and emits deterministic C#.
- `Nayamnyam.Viewer` consumes semantic query/review projections and renders read-only artifacts.
- tools compose those public APIs and own filesystem/process integration.

Generated C# and viewer artifacts never become upstream semantic authority.

## Persistence and Nyason

JSON remains the strict reference/debug codec. Nyason remains a dedicated experimental encoding layer rather than the semantic model itself. Current schema evolution and storage direction are documented separately so persistence experiments do not silently change semantic authority.

See:

- [Nyason v0 design](docs/nyason-v0-20260825.md)
- [Nyason/storage direction](docs/nyason-storage-direction-20260826.md)
- [Schema evolution policy](docs/schema-evolution-policy-20260826.md)

## Build and verification

The repository uses the .NET 10 SDK:

```shell
dotnet restore Nayamnyam.slnx
dotnet build Nayamnyam.slnx --configuration Release --no-restore
dotnet test Nayamnyam.slnx --configuration Release --no-build --no-restore
```

The stricter accepted local verification path is:

```shell
bash scripts/verify-local-20260825-1427.sh --clean
```

Latest accepted code verification:

```text
feature head: 79fccfba86c1c00add0a9c50248c55336e7c563b
.NET SDK:     10.0.111
build:        0 warnings / 0 errors
full suite:   158 passed / 0 failed / 0 skipped
M1-J Float64: 8 passed / 0 failed
result:       VERIFICATION PASSED
```

That verified implementation tree was squash-merged as `54c26d1dbfc615ef60b00a714552004cf191fefe`; subsequent accepted changes before this README update were documentation-only.

## Milestones and records

- [Milestone 0](docs/milestone-0.md) — original vertical-slice acceptance criteria
- [Milestone 0 results](docs/milestone-0-results-20260826.md) — final M0 exit-criteria audit
- [Milestone 1](docs/milestone-1.md) — current M1 status and remaining evaluation gate
- [M1 architecture decisions](docs/m1-architecture-decisions-20260826.md)
- [M1 language-growth design](docs/m1-language-growth-design-20260826.md)
- [M1 agent evaluation plan](docs/m1-agent-evaluation-plan-20260826.md)
- [M1 Float64 implementation notes](docs/m1-float64-implementation-notes-20260828.md)

Detailed rebuild phase records remain under `docs/rebuild-*` as historical verification and design evidence.

## Documentation

- [Vision](docs/vision.md) — purpose, priorities, non-goals, and success conditions
- [Architecture](docs/architecture.md) — system boundaries, data flow, invariants, and validation model
- [Decisions](docs/decisions.md) — accepted foundational decisions and consequences
- [AI-first design principles](docs/ai-first-design-principles-20260825.md) — semantic representation tie-breakers
- [Failure vs obligation policy](docs/failure-obligation-policy-20260826.md)
- [Open questions](docs/open-questions.md)
- [Open-question status](docs/open-questions-status-20260826.md)

## Relationship to RAST

Nayamnyam is informed by earlier Readable AST Language work, including conservative semantic checking, explicit Result/Option handling, effect-oriented design, and backend-neutral IR experiments. RAST remains external to this repository; migrated rules or tests should record provenance rather than creating a runtime dependency.
