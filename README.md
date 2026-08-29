# NyamNyam

**NyamNyam is an AI-native semantic programming system in which programs are persistent typed semantic graphs and changes are validated meaning-level operations.**

Source text is not the authoritative program form. Stable semantic identity, relationships, validation, change review, and unresolved work are represented explicitly so AI agents can make bounded changes without reconstructing program meaning from text, while humans can review what was requested and what will actually change.

## Current status

NyamNyam is an active research and implementation project.

- **Milestone 0 is complete:** the graph-first architecture has passed an end-to-end lifecycle from persistence through reviewed editing and generated execution.
- **Milestone 1 is complete for the accepted bounded-operation claim:** production stable IDs, fingerprints, reviewed publication, physical assembly boundaries, `Int64`, immutable ordered `List<T>`, pure higher-order List operations, explicit IEEE 754 binary64 `Float64`, and controlled agent-evaluation evidence are in place.
- **Milestone 2 is proposed:** the candidate next step is a deliberately bounded structural expression-editing surface. It is planning material, not current agent capability or an accepted language contract.

Exact build/test and evaluation evidence belongs under verification records rather than this landing page.

## Core model

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

The semantic graph is the source of truth. Textual code, generated C#, serialized encodings, diagnostic reports, query payloads, and viewer artifacts are projections or storage representations.

Important boundaries include:

- stable IDs define semantic identity; names are presentation metadata;
- ordinary edits use typed semantic operations rather than raw graph or serialization mutation;
- simulation and review precede publication;
- unresolved semantic decisions are represented explicitly as obligations when a safe incomplete state exists;
- generated backend source never becomes upstream authority;
- backend-specific representation choices do not define core language semantics.

## Build and verify

The repository uses the .NET 10 SDK.

```shell
dotnet restore NyamNyam.slnx
dotnet build NyamNyam.slnx --configuration Release --no-restore
dotnet test NyamNyam.slnx --configuration Release --no-build --no-restore
```

For the stricter repository-local verification path:

```shell
bash scripts/verify-local-20260825-1427.sh --clean
```

## Documentation

Start with the [documentation portal](docs/README.md).

Useful entry points:

- [Vision](docs/concepts/vision.md) — mission, priorities, principles, non-goals, and success conditions.
- [Architecture](docs/developer/architecture.md) — current high-level boundaries and invariants.
- [Current language subset](docs/reference/language-subset.md) — implemented semantic and ordinary agent-editing surfaces at a glance.
- [Decisions](docs/decisions/README.md) — accepted rationale and bounded open questions.
- [Milestone 1 record](docs/plans/milestone-1.md) — completed bounded-operation milestone and the exact claim boundary.
- [Proposed Milestone 2 plan](docs/plans/milestone-2.md) — candidate next work on bounded structural expression editing.
- [Contributor/reviewer entry point](docs/developer/README.md) — implementation-oriented navigation.

Historical and superseded material is kept separately under [`docs-archive/`](docs-archive/README.md) and is not authoritative for current behavior.

## Relationship to RAST

NyamNyam is informed by earlier RAST work, but RAST is not a runtime or semantic dependency. Conceptual ancestry does not require a special provenance system; concrete copied or substantially adapted material uses ordinary source attribution. See the [RAST provenance decision](docs/decisions/rast-provenance.md).
